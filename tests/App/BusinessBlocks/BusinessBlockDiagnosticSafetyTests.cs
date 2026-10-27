using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Application.RuntimeSpecialization.BusinessBlocks;
using ResourceManager.App.Endpoints;
using ResourceManager.App.Infrastructure.RuntimeSpecialization.BusinessBlocks;
using static Resource_Manager_APP.Tests.BusinessBlockTestSupport;

namespace Resource_Manager_APP.Tests;

public sealed class BusinessBlockDiagnosticSafetyTests
{
    [Fact]
    public async Task CurrentDecisionRemainsQueryableWhenHistoryIsDisabled()
    {
        var block = new Probe();
        await using var scheduler = Create([Register(116, block)], historyLimit: 0);
        try
        {
            var demand = Reason(BusinessBlockRequestKind.User, "operator", "observe", 19);
            await scheduler.SetDemandAsync("observe", 116, demand);
            await State(scheduler, 116, BusinessBlockState.Running);
            var snapshot = scheduler.Read();
            Assert.Empty(snapshot.History);
            Assert.Empty(snapshot.Decisions);
            var current = Assert.Single(snapshot.CurrentDecisions);
            Assert.Equal(BackendUids.Program, current.ParentId);
            Assert.Equal([116], current.ShouldRun);
            Assert.Contains(current.Items, fact => fact.ItemUid == BackendItems.BlockDemand && fact.BlockUid == 116
                && fact.Value is true);
            Assert.Equal(demand, Assert.Single(snapshot.Demands, value => value.BlockId == 116 && value.Key == "observe").Reason);
            await scheduler.SetDemandAsync("observe", null, demand);
            await State(scheduler, 116, BusinessBlockState.Stopped);
            Assert.Empty(Assert.Single(scheduler.Read().CurrentDecisions).ShouldRun);
        }
        finally { await StopAll(scheduler); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExceptionDetailsReachTheLoggerButNeverTheDiagnosticWire(bool recordResult)
    {
        const string secret = "user-secret-token";
        var error = new IOException(@"C:\Users\Alice\private; S-1-5-21-123456; " + secret);
        var logger = new ErrorLogger();
        var registrations = new[] { Register(116, new Probe()) };
        var diagnostics = new BusinessBlockDiagnosticsRegistry(registrations, logger);
        await using var scheduler = Create(registrations);
        if (recordResult) diagnostics.RecordResult(116, BusinessBlockResult.Failed(error));
        else diagnostics.ReportFailure(116, error, "record-write");
        var options = new Microsoft.AspNetCore.Http.Json.JsonOptions().SerializerOptions;
        ResourceManagerEndpointRouteBuilderExtensions.ConfigureJsonSerializerOptions(options);
        var snapshot = new BusinessBlockDebugSnapshot(scheduler.Read(), diagnostics.ReadAll());
        var json = JsonSerializer.Serialize(snapshot, options);
        Assert.DoesNotContain(secret, json);
        Assert.DoesNotContain("Alice", json);
        Assert.DoesNotContain("S-1-5-21-123456", json);
        var result = Assert.Single(snapshot.Diagnostics).Result!;
        Assert.Equal(typeof(IOException).FullName, result.FailureType);
        Assert.Equal(recordResult ? BusinessBlockFailureStage.Run : BusinessBlockFailureStage.ReportedFailure, result.FailureStage);
        Assert.Contains(logger.Errors, actual => ReferenceEquals(error, actual));
        Assert.Empty(snapshot.Scheduling.History);
        Assert.Empty(snapshot.Scheduling.Decisions);
    }

    [Fact]
    public void DiagnosticsRequirePredeclaredIdsIncludingObservationOnlyComponents()
    {
        var diagnostics = new BusinessBlockDiagnosticsRegistry([], NullLogger<BusinessBlockDiagnosticsRegistry>.Instance,
            [new(136)]);
        diagnostics.ReportFailure(136, new IOException("observed failure"), "observe");
        Assert.Equal(136, Assert.Single(diagnostics.ReadAll()).Id);
        Assert.Throws<KeyNotFoundException>(() => diagnostics.Read(152));
        Assert.Throws<KeyNotFoundException>(() => diagnostics.BeginRun(152));
        Assert.Throws<KeyNotFoundException>(() => diagnostics.RecordResult(152, BusinessBlockResult.Done));
        Assert.Throws<KeyNotFoundException>(() => diagnostics.ReportFailure(152, new IOException(), "observe"));
        Assert.Throws<KeyNotFoundException>(() => diagnostics.MeasureSegment(152));
        Assert.Throws<KeyNotFoundException>(() => diagnostics.RegisterConsumerThread(152, "session"));
        Assert.Single(diagnostics.ReadAll());
    }

    [Fact]
    public async Task BlockOwnedResultAndMeasurementsAreSeparateFromSchedulingAndReadsArePure()
    {
        var registrations = new[] { Register(116, new Probe()) };
        var diagnostics = new BusinessBlockDiagnosticsRegistry(registrations, NullLogger<BusinessBlockDiagnosticsRegistry>.Instance);
        await using var scheduler = Create(registrations);
        diagnostics.BeginRun(116);
        var scheduling = scheduler.Read();
        using (diagnostics.MeasureSegment(116)) GC.KeepAlive(new byte[32 * 1024]);
        diagnostics.RecordResult(116, BusinessBlockResult.Done);
        var measured = diagnostics.Read(116);
        Assert.True(measured.Metrics.TotalAllocatedBytes >= 32 * 1024);
        Assert.Equal(BusinessBlockOutcome.Done, measured.Result!.Outcome);
        Assert.Equal(measured.Result, diagnostics.Read(116).Result);
        Assert.Equal(scheduling.History, scheduler.Read().History);
        Assert.Equal(scheduling.Decisions, scheduler.Read().Decisions);
        Assert.Equal(BusinessBlockState.Stopped, scheduler.ReadState(116));
        diagnostics.BeginRun(116);
        Assert.Null(diagnostics.Read(116).Result);
    }

    private sealed class ErrorLogger : ILogger<BusinessBlockDiagnosticsRegistry>
    {
        public List<Exception> Errors { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? error, Func<TState, Exception?, string> formatter)
        {
            if (error is not null) Errors.Add(error);
        }
    }
}
