using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ResourceManager.App.Application.RuntimeSpecialization.BusinessBlocks;
using ResourceManager.App.Endpoints;
using ResourceManager.App.Infrastructure.RuntimeSpecialization.BusinessBlocks;

namespace Resource_Manager_APP.Tests;

public sealed class BusinessBlockDiagnosticSafetyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExceptionDetailsReachTheLoggerButNeverTheDiagnosticWire(bool throwFromRun)
    {
        const string secret = "user-secret-token";
        var error = new IOException(@"C:\Users\Alice\private; S-1-5-21-123456; " + secret);
        var logger = new ErrorLogger();
        using var services = new ServiceCollection().BuildServiceProvider();
        await using var controller = new BusinessBlockController(services,
            [new("block", [], _ => new Block(_ => Task.FromException<BusinessBlockResult>(error)))],
            Options.Create(new BusinessBlockOptions()), logger);
        if (throwFromRun)
        {
            await controller.StartAsync("block", new(BusinessBlockRequestKind.User, "test", "explicit", 1));
            var result = await controller.GetCompletion("block");
            var resultJson = JsonSerializer.Serialize(result);
            Assert.DoesNotContain(secret, resultJson);
            Assert.DoesNotContain("Alice", resultJson);
        }
        else controller.ReportFailure("block", error, "record-write");

        var options = new Microsoft.AspNetCore.Http.Json.JsonOptions().SerializerOptions;
        ResourceManagerEndpointRouteBuilderExtensions.ConfigureJsonSerializerOptions(options);
        var json = JsonSerializer.Serialize(controller.Read(), options);
        Assert.DoesNotContain(secret, json);
        Assert.DoesNotContain("Alice", json);
        Assert.DoesNotContain("S-1-5-21-123456", json);
        Assert.DoesNotContain("\"failure\":", json);
        var failure = controller.Read().History.Last();
        Assert.Equal(typeof(IOException).FullName, failure.FailureType);
        Assert.Equal(throwFromRun ? BusinessBlockFailureStage.Run : BusinessBlockFailureStage.ReportedFailure, failure.FailureStage);
        Assert.Contains(logger.Errors, actual => ReferenceEquals(error, actual));
    }

    [Fact]
    public async Task DefaultMeasurementHasNoTimerAndReadIncludesUnsampledSegmentAllocations()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        await using var controller = new BusinessBlockController(services,
            [new("block", [], _ => new Block(async stop =>
            {
                try { await Task.Delay(Timeout.InfiniteTimeSpan, stop); }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
                return BusinessBlockResult.Stopped;
            }))], Options.Create(new BusinessBlockOptions()), new ErrorLogger());
        var reason = new BusinessBlockReason(BusinessBlockRequestKind.User, "test", "explicit", 1);
        await controller.StartAsync("block", reason);
        try
        {
            var before = controller.Read();
            using (controller.MeasureSegment("block")) GC.KeepAlive(new byte[32 * 1024]);
            var after = controller.Read();
            Assert.Null(after.MetricsInterval);
            Assert.Null(typeof(BusinessBlockController).GetField("samplingTask", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(controller));
            Assert.True(after.Blocks[0].Metrics.TotalAllocatedBytes >= 32 * 1024);
            Assert.Equal(before.Blocks[0].Metrics.SampledAt, after.Blocks[0].Metrics.SampledAt);
            Assert.Equal(before.History, after.History);
        }
        finally { await controller.StopAsync("block", reason); }
    }

    private sealed class Block(Func<CancellationToken, Task<BusinessBlockResult>> run) : IBusinessBlock
    {
        public Task<BusinessBlockResult> RunAsync(CancellationToken stopNotification) => run(stopNotification);
    }
    private sealed class ErrorLogger : ILogger<BusinessBlockController>
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
