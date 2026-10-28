using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ResourceManager.App.Application.RuntimeSpecialization.BusinessBlocks;
using ResourceManager.App.Hosting;
using ResourceManager.App.Infrastructure.RuntimeSpecialization.BusinessBlocks;
using ResourceManager.Shared.BusinessBlocks.State;

namespace Resource_Manager_APP.Tests;

public sealed class BusinessBlockHostedRestartFailureTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static readonly BusinessBlockReason Reason = new(BusinessBlockRequestKind.User,
        "hosted-restart-test", "subscription-activity", 0);

    [Fact]
    public async Task AliasObservesOriginalExecutionFailureAfterFactDrivenStopAndRestart()
    {
        var service = new RestartableService();
        var resultLog = new FailureResultLogger(service.Failure);
        using var host = Host.CreateDefaultBuilder()
            .ConfigureLogging(logging => logging.ClearProviders())
            .ConfigureServices(services =>
            {
                services.Configure<HostOptions>(options =>
                    options.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore);
                services.AddBusinessBlockHostedService<RestartableService>(BackendUids.ControlActualStateReader, _ => service);
                services.AddSingleton<ILogger<BusinessBlockDiagnosticsRegistry>>(resultLog);
                NativeBackendBlockTestSupport.AddFullTreeHostFixture(services);
            }).Build();
        var scheduler = host.Services.GetRequiredService<BusinessBlockScheduler>();
        resultLog.ReadResult = () => host.Services.GetRequiredService<BusinessBlockDiagnosticsRegistry>()
            .Read(BackendUids.ControlActualStateReader).Result;
        var activity = scheduler.Authority.ClaimCell<bool>(BackendItems.ActualControlValuesInUse);
        var alias = Assert.Single(host.Services.GetServices<IHostedService>().OfType<NonOwningHostedService<RestartableService>>());
        await activity.PublishAsync(true, Reason);
        await host.StartAsync().WaitAsync(Timeout);
        try
        {
            await Until(() => scheduler.ReadState(BackendUids.ControlActualStateReader) == BusinessBlockState.Running);
            Assert.Equal(1, service.StartCount);
            Assert.False(alias.ExecuteTask!.IsCompleted);

            await activity.PublishAsync(false, Reason);
            await Until(() => scheduler.ReadState(BackendUids.ControlActualStateReader) == BusinessBlockState.Stopped);
            Assert.Equal(1, service.StopCount);
            Assert.False(alias.ExecuteTask.IsCompleted);
            var stopped = host.Services.GetRequiredService<BusinessBlockDiagnosticsRegistry>()
                .Read(BackendUids.ControlActualStateReader).Result;
            Assert.Equal(BusinessBlockOutcome.Stopped, stopped!.Outcome);
            Assert.Null(stopped.FailureStage);

            await activity.PublishAsync(true, Reason);
            await Until(() => service.StartCount == 2
                && scheduler.ReadState(BackendUids.ControlActualStateReader) == BusinessBlockState.Running);
            service.FailSecondRun.TrySetResult();
            var failure = await Assert.ThrowsAsync<IOException>(() => alias.ExecuteTask.WaitAsync(Timeout));
            Assert.Same(service.Failure, failure);
            Assert.Equal(service.FirstStartupToken, service.SecondStartupToken);
            var failed = await resultLog.Failed.Task.WaitAsync(Timeout);
            Assert.Equal(BusinessBlockOutcome.Failed, failed!.Outcome);
            Assert.Equal(BusinessBlockFailureStage.Run, failed.FailureStage);
            Assert.True(service.StopCount >= 2);
        }
        finally { await host.StopAsync().WaitAsync(Timeout); }
    }

    private static async Task Until(Func<bool> ready)
    {
        using var cancellation = new CancellationTokenSource(Timeout);
        while (!ready()) await Task.Delay(1, cancellation.Token);
    }

    private sealed class RestartableService : BackgroundService
    {
        private int starts, stops;
        internal int StartCount => Volatile.Read(ref starts);
        internal int StopCount => Volatile.Read(ref stops);
        internal readonly IOException Failure = new("second execution failed");
        internal readonly TaskCompletionSource FailSecondRun = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal CancellationToken FirstStartupToken, SecondStartupToken;

        public override Task StartAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref starts) == 1) FirstStartupToken = cancellationToken;
            else SecondStartupToken = cancellationToken;
            return base.StartAsync(cancellationToken);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (StartCount != 2)
            {
                try { await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
                return;
            }
            await FailSecondRun.Task.WaitAsync(stoppingToken);
            throw Failure;
        }

        public override Task StopAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref stops);
            return base.StopAsync(cancellationToken);
        }
    }

    // Capture the committed failure before another permitted run can replace the current diagnostics.
    private sealed class FailureResultLogger(Exception expected) : ILogger<BusinessBlockDiagnosticsRegistry>
    {
        internal Func<BusinessBlockResult?> ReadResult = null!;
        internal readonly TaskCompletionSource<BusinessBlockResult> Failed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? error,
            Func<TState, Exception?, string> formatter)
        {
            if (ReferenceEquals(error, expected)) Failed.TrySetResult(ReadResult()!);
        }
    }
}
