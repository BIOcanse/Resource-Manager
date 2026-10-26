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
        await using var scheduler = Create([Register("block", block)], historyLimit: 0);
        try
        {
            var demand = Reason(BusinessBlockRequestKind.User, "operator", "observe", 19);
            await scheduler.SetDemandAsync("observe", "block", demand);
            await State(scheduler, "block", BusinessBlockState.Running);
            var snapshot = scheduler.Read();
            Assert.Empty(snapshot.History);
            Assert.Empty(snapshot.Decisions);
            var current = Assert.Single(snapshot.CurrentDecisions);
            Assert.Equal(BackendBlockGraph.Program, current.ParentId);
            Assert.Equal(["block"], current.ShouldRun);
            Assert.Contains(current.Facts, fact => fact.Value && fact.Reasons.Contains(demand));
            await scheduler.SetDemandAsync("observe", null, demand);
            await State(scheduler, "block", BusinessBlockState.Stopped);
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
        var registrations = new[] { Register("block", new Probe()) };
        var diagnostics = new BusinessBlockDiagnosticsRegistry(registrations, logger);
        await using var scheduler = Create(registrations);
        if (recordResult) diagnostics.RecordResult("block", BusinessBlockResult.Failed(error));
        else diagnostics.ReportFailure("block", error, "record-write");
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
            [new("observation")]);
        diagnostics.ReportFailure("observation", new IOException("observed failure"), "observe");
        Assert.Equal("observation", Assert.Single(diagnostics.ReadAll()).Id);
        Assert.Throws<KeyNotFoundException>(() => diagnostics.Read("typo"));
        Assert.Throws<KeyNotFoundException>(() => diagnostics.BeginRun("typo"));
        Assert.Throws<KeyNotFoundException>(() => diagnostics.RecordResult("typo", BusinessBlockResult.Done));
        Assert.Throws<KeyNotFoundException>(() => diagnostics.ReportFailure("typo", new IOException(), "observe"));
        Assert.Throws<KeyNotFoundException>(() => diagnostics.MeasureSegment("typo"));
        Assert.Throws<KeyNotFoundException>(() => diagnostics.RegisterConsumerThread("typo", "session"));
        Assert.Single(diagnostics.ReadAll());
    }

    [Fact]
    public async Task BlockOwnedResultAndMeasurementsAreSeparateFromSchedulingAndReadsArePure()
    {
        var registrations = new[] { Register("block", new Probe()) };
        var diagnostics = new BusinessBlockDiagnosticsRegistry(registrations, NullLogger<BusinessBlockDiagnosticsRegistry>.Instance);
        await using var scheduler = Create(registrations);
        diagnostics.BeginRun("block");
        var scheduling = scheduler.Read();
        using (diagnostics.MeasureSegment("block")) GC.KeepAlive(new byte[32 * 1024]);
        diagnostics.RecordResult("block", BusinessBlockResult.Done);
        var measured = diagnostics.Read("block");
        Assert.True(measured.Metrics.TotalAllocatedBytes >= 32 * 1024);
        Assert.Equal(BusinessBlockOutcome.Done, measured.Result!.Outcome);
        Assert.Equal(measured.Result, diagnostics.Read("block").Result);
        Assert.Equal(scheduling.History, scheduler.Read().History);
        Assert.Equal(scheduling.Decisions, scheduler.Read().Decisions);
        Assert.Equal(BusinessBlockState.Stopped, scheduler.ReadState("block"));
        diagnostics.BeginRun("block");
        Assert.Null(diagnostics.Read("block").Result);
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
