using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ResourceManager.App.Hosting;
using ResourceManager.App.Application.RuntimeSpecialization.BusinessBlocks;
using ResourceManager.App.Infrastructure.RuntimeSpecialization.BusinessBlocks;
using ResourceManager.Shared.BusinessBlocks.State;

namespace Resource_Manager_APP.Tests;

// Uses the real hosted adapter, controller and compiled fact scheduler around
// one actual producer. Other compiled tree entries are the standard inert fixture.
internal sealed class ProducerSubscriptionRestartHost<TService> : IAsyncDisposable
    where TService : class, IHostedService
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    private readonly TaskCompletionSource<Task> firstRun = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<Task> secondRun = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly IHost host;
    private readonly FailureLogger log;
    private readonly BusinessBlockScheduler scheduler;
    private readonly int producerUid;
    private readonly int demandUid;
    private int runs;

    private ProducerSubscriptionRestartHost(TService producer, Func<TService, Task?> execution,
        int producerUid, int demandUid, Action allowRecordedReads)
    {
        this.producerUid = producerUid;
        this.demandUid = demandUid;
        log = new(allowRecordedReads);
        host = Host.CreateDefaultBuilder().ConfigureLogging(logging => logging.ClearProviders())
            .ConfigureServices(services =>
            {
                services.AddSingleton(producer);
                services.AddSingleton<ILogger<BusinessBlockDiagnosticsRegistry>>(log);
                services.AddHostedServiceAlias<TService>(producerUid, service =>
                {
                    var task = execution(service) ?? throw new InvalidOperationException("The actual producer has no execution task.");
                    if (Interlocked.Increment(ref runs) == 1) firstRun.TrySetResult(task);
                    else secondRun.TrySetResult(task);
                    return task;
                }, forwardRunFailureToHost: false);
                NativeBackendBlockTestSupport.AddFullTreeHostFixture(services);
            }).Build();
        scheduler = host.Services.GetRequiredService<BusinessBlockScheduler>();
        log.ReadResult = () => host.Services.GetRequiredService<BusinessBlockDiagnosticsRegistry>()
            .Read(producerUid).Result;
    }

    internal Task<Task> FirstRun => firstRun.Task.WaitAsync(Deadline);

    internal static Task<ProducerSubscriptionRestartHost<TService>> StartAsync(TService producer,
        Func<TService, Task?> execution, int producerUid, int demandUid, Func<bool> readDemand,
        ProducerSubscriptionCurrentBackend backend)
        => StartAsync(producer, execution, producerUid, demandUid, readDemand, backend.AllowReads);

    internal static async Task<ProducerSubscriptionRestartHost<TService>> StartAsync(TService producer,
        Func<TService, Task?> execution, int producerUid, int demandUid, Func<bool> readDemand,
        Action allowRecordedReads)
    {
        var fixture = new ProducerSubscriptionRestartHost<TService>(producer, execution,
            producerUid, demandUid, allowRecordedReads);
        try
        {
            // These tests hold their subscriptions for the whole host turn. Read
            // that original demand through the producer's production decision fact.
            await fixture.scheduler.Authority.ClaimReadFunction<bool>(demandUid,
                    () => StateRead<bool>.FromValue(readDemand()))
                .NotifyChangedAsync(new(BusinessBlockRequestKind.User, "producer-subscription-test", "consumer-held", 0));
            await fixture.host.StartAsync().WaitAsync(Deadline);
            return fixture;
        }
        catch { await fixture.DisposeAsync(); throw; }
    }

    internal async Task AssertControllerRestartedAsync()
    {
        var failed = await log.Failed.Task.WaitAsync(Deadline);
        Assert.Equal(BusinessBlockFailureStage.Run, failed.FailureStage);
        Assert.Equal(BusinessBlockOutcome.Failed, failed.Outcome);
        var next = await secondRun.Task.WaitAsync(Deadline);
        Assert.NotSame(await FirstRun, next);
        using var timeout = new CancellationTokenSource(Deadline);
        while (scheduler.ReadState(producerUid) != BusinessBlockState.Running)
            await Task.Delay(1, timeout.Token);
        Assert.False(next.IsCompleted);
        Assert.True(scheduler.Authority.Read<bool>(demandUid).Value);
        Assert.False(host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.IsCancellationRequested);
    }

    public async ValueTask DisposeAsync()
    {
        try { await host.StopAsync().WaitAsync(Deadline); }
        finally { host.Dispose(); }
    }

    private sealed class FailureLogger(Action allowRecordedReads) : ILogger<BusinessBlockDiagnosticsRegistry>
    {
        internal Func<BusinessBlockResult?> ReadResult = null!;
        internal readonly TaskCompletionSource<BusinessBlockResult> Failed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? error,
            Func<TState, Exception?, string> formatter)
        {
            if (error?.Message != ProducerSubscriptionCurrentBackend.FailureMessage
                || ReadResult() is not { FailureStage: BusinessBlockFailureStage.Run } result) return;
            allowRecordedReads();
            Failed.TrySetResult(result);
        }
    }
}
