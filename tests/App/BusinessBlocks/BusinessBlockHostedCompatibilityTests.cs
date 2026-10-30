using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ResourceManager.App.Application.RuntimeSpecialization.BusinessBlocks;
using ResourceManager.App.Hosting;
using ResourceManager.App.Infrastructure.RuntimeSpecialization.BusinessBlocks;

namespace Resource_Manager_APP.Tests;

public sealed class BusinessBlockHostedCompatibilityTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task HostStopNotifiesEveryServiceInItsReverseHostTurnAndLetsCleanupDrain()
    {
        var observations = new ConcurrentQueue<StopObservation>();
        using var host = BuildHost(services =>
        {
            services.AddBusinessBlockLifetime<FirstStopService>(BackendUids.RuntimeSpecializationCoordinator, _ => new(observations), constructService: true);
            services.AddBusinessBlockLifetime<SecondStopService>(BackendUids.ServiceAutoStartSettings, _ => new(observations), constructService: true);
            services.AddBusinessBlockLifetime<ThirdStopService>(BackendUids.TrustedAccessInvocationHostedService, _ => new(observations), constructService: true);
        });

        await host.StartAsync();
        await WaitUntilAsync(() => host.Services.GetRequiredService<BusinessBlockScheduler>().ReadState(BackendUids.TrustedAccessInvocationHostedService) == BusinessBlockState.Running);
        await WaitUntilAsync(() => host.Services.GetRequiredService<BusinessBlockScheduler>().ReadState(BackendUids.ServiceAutoStartSettings) == BusinessBlockState.Running);
        await WaitUntilAsync(() => host.Services.GetRequiredService<BusinessBlockScheduler>().ReadState(BackendUids.RuntimeSpecializationCoordinator) == BusinessBlockState.Running);
        await host.StopAsync();

        var stopped = observations.ToArray();
        Assert.Equal(new[] { "third", "second", "first" }, stopped.Select(value => value.Name));
        Assert.All(stopped, value =>
        {
            Assert.Equal(CancellationToken.None, value.Token);
        });
    }

    [Fact]
    public async Task HostDeadlineReturnsWhileOriginalStopTaskStillOwnsCleanup()
    {
        using var hostDeadline = new CancellationTokenSource();
        var releaseStop = NewSignal();
        var service = new LifecycleService(_ => releaseStop.Task);
        using var host = BuildHost(services =>
            services.AddBusinessBlockLifetime<LifecycleService>(BackendUids.RuntimeSpecializationCoordinator, _ => service, constructService: true));
        await host.StartAsync();
        await WaitUntilAsync(() => service.StartCount == 1);
        var stopping = Hosted<LifecycleService>(host).StopAsync(hostDeadline.Token);
        try
        {
            await service.StopStarted.Task.WaitAsync(TestTimeout);
            hostDeadline.Cancel();
            await stopping.WaitAsync(TestTimeout);
            Assert.Equal(BusinessBlockState.Stopping,
                host.Services.GetRequiredService<BusinessBlockScheduler>().ReadState(BackendUids.RuntimeSpecializationCoordinator));
            Assert.Equal(CancellationToken.None, service.LastStopToken);
            releaseStop.TrySetResult();
            await WaitUntilAsync(() => host.Services.GetRequiredService<BusinessBlockScheduler>()
                .ReadState(BackendUids.RuntimeSpecializationCoordinator) == BusinessBlockState.Stopped);
        }
        finally
        {
            releaseStop.TrySetResult();
            await stopping.WaitAsync(TestTimeout);
        }
        Assert.Equal(1, service.StopCount);
        await host.StopAsync().WaitAsync(TestTimeout);
    }

    [Fact]
    public async Task ServiceTimeoutExceptionIsPropagatedAsTheOriginalFailure()
    {
        var failure = new TimeoutException("service-owned timeout");
        var service = new LifecycleService(_ => Task.FromException(failure));
        using var host = BuildHost(services =>
            services.AddBusinessBlockLifetime<LifecycleService>(BackendUids.RuntimeSpecializationCoordinator, _ => service, constructService: true));
        await host.StartAsync();
        await WaitUntilAsync(() => service.StartCount == 1);
        var thrown = await Assert.ThrowsAsync<TimeoutException>(() => Hosted<LifecycleService>(host).StopAsync(CancellationToken.None));
        Assert.Same(failure, thrown);
        Assert.Equal(1, service.StopCount);
        var result = host.Services.GetRequiredService<BusinessBlockDiagnosticsRegistry>()
            .Read(BackendUids.RuntimeSpecializationCoordinator).Result;
        Assert.Equal(BusinessBlockOutcome.Failed, result!.Outcome);
        Assert.Equal(BusinessBlockFailureStage.Stop, result.FailureStage);
        var hostStopError = await Record.ExceptionAsync(() => host.StopAsync().WaitAsync(TestTimeout));
        Assert.Same(failure, hostStopError);
    }

    [Fact]
    public async Task DisposeAfterCanceledBackgroundStopIsBoundedAndDoesNotIssueAnotherStop()
    {
        var release = NewSignal();
        var worker = new GatedBackgroundService(release.Task);
        using var cancellation = new CancellationTokenSource();
        using var host = BuildHost(services =>
            services.AddBusinessBlockWorker<GatedBackgroundService>(BackendUids.RuntimeSpecializationCoordinator, _ => worker, constructService: true));
        await host.StartAsync();
        await worker.ExecutionStarted.Task.WaitAsync(TestTimeout);
        var scheduler = host.Services.GetRequiredService<BusinessBlockScheduler>();
        var stopping = Hosted<GatedBackgroundService>(host).StopAsync(cancellation.Token);
        try
        {
            await worker.StopStarted.Task.WaitAsync(TestTimeout);
            cancellation.Cancel();
            await stopping.WaitAsync(TestTimeout);
            Assert.False(worker.ExecuteTask!.IsCompleted);
            Assert.Equal(BusinessBlockState.Stopping, scheduler.ReadState(BackendUids.RuntimeSpecializationCoordinator));
            Assert.Equal(CancellationToken.None, worker.LastStopToken);
            await host.StopAsync(cancellation.Token).WaitAsync(TestTimeout);
            var elapsed = Stopwatch.StartNew();
            await scheduler.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1));
            Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(1));
            Assert.Equal(1, worker.StopCount);
        }
        finally { release.TrySetResult(); }
        await WaitUntilAsync(() => scheduler.ReadState(BackendUids.RuntimeSpecializationCoordinator) == BusinessBlockState.Stopped);
        Assert.Equal(1, worker.StopCount);
    }

    [Fact]
    public async Task WithdrawalDuringStartWaitsBeforeCallingOriginalStop()
    {
        var service = new GatedStartService();
        using var host = BuildHost(services =>
            services.AddBusinessBlockLifetime<GatedStartService>(BackendUids.RuntimeSpecializationCoordinator, _ => service, constructService: true));
        var hosted = Hosted<GatedStartService>(host);
        var starting = hosted.StartAsync(CancellationToken.None);
        await service.StartEntered.Task.WaitAsync(TestTimeout);
        var stopping = hosted.StopAsync(CancellationToken.None);
        try
        {
            await Task.Delay(30);
            Assert.Equal(0, service.StopCount);
            Assert.False(stopping.IsCompleted);
        }
        finally { service.ReleaseStart.TrySetResult(); }
        await starting.WaitAsync(TestTimeout);
        await stopping.WaitAsync(TestTimeout);
        Assert.Equal(1, service.StopCount);
        Assert.False(service.StartStopOverlapped);
    }

    [Fact]
    public async Task FailedRunCleansUpBeforeReturningAndItsLateHostTurnCannotStopTheNextRun()
    {
        var service = new RecoverableStartService();
        using var host = BuildHost(services =>
            services.AddBusinessBlockLifetime<RecoverableStartService>(BackendUids.RuntimeSpecializationCoordinator, _ => service, constructService: true));
        var block = host.Services.GetRequiredService<HostedBusinessBlockRegistry>().GetOrAdd(BackendUids.RuntimeSpecializationCoordinator, () => service);
        var first = block.PrepareStart(CancellationToken.None);
        var firstRun = block.RunAsync(new(CancellationToken.None, () => { }));
        try
        {
            await service.FirstStopEntered.Task.WaitAsync(TestTimeout);
            Assert.False(firstRun.IsCompleted);
            Assert.False(first.Readiness.Task.IsCompleted);
        }
        finally { service.ReleaseFirstStop.TrySetResult(); }
        await firstRun.WaitAsync(TestTimeout);
        var startupError = await Assert.ThrowsAsync<IOException>(() => first.Readiness.Task);
        Assert.Same(service.StartupError, startupError);
        Assert.Equal(1, service.StopCount);

        using var stop = new CancellationTokenSource();
        var second = block.PrepareStart(CancellationToken.None);
        var secondRun = block.RunAsync(new(stop.Token, () => { }));
        try
        {
            await second.Readiness.Task.WaitAsync(TestTimeout);
            Assert.True(service.IsRunning);
            Assert.Equal(2, service.StartCount);

        block.SetHostStopTurn();
            await first.StopCompleted.Task.WaitAsync(TestTimeout);

            Assert.True(service.IsRunning);
            Assert.False(secondRun.IsCompleted);
            Assert.Equal(1, service.StopCount);
        }
        finally { stop.Cancel(); }
        await secondRun.WaitAsync(TestTimeout);
        Assert.Equal(2, service.StopCount);
        Assert.False(service.IsRunning);
    }

    [Fact]
    public async Task HostStartAdoptsAnAlreadyPermittedRunningService()
    {
        var service = new LifecycleService(_ => Task.CompletedTask);
        using var host = BuildHost(services =>
            services.AddBusinessBlockLifetime<LifecycleService>(BackendUids.RuntimeSpecializationCoordinator, _ => service, constructService: true));
        var scheduler = host.Services.GetRequiredService<BusinessBlockScheduler>();
        await host.Services.GetRequiredService<BackendHostPermitFacts>().SetAsync(BackendUids.RuntimeSpecializationCoordinator, true, TestReason("early-permit")).WaitAsync(TestTimeout);
        try
        {
            await WaitUntilAsync(() => scheduler.ReadState(BackendUids.RuntimeSpecializationCoordinator) == BusinessBlockState.Running);
            await host.StartAsync().WaitAsync(TestTimeout);
            Assert.Equal(1, service.StartCount);
        }
        finally { await host.StopAsync().WaitAsync(TestTimeout); }
        Assert.Equal(1, service.StopCount);
    }

    [Fact]
    public async Task HostStopAwaitsTheCurrentRunAfterItsOriginalRunReturnedAndRestarted()
    {
        var secondStopEntered = NewSignal();
        var releaseSecondStop = NewSignal();
        var stopCalls = 0;
        var service = new LifecycleService(_ =>
        {
            if (Interlocked.Increment(ref stopCalls) != 2) return Task.CompletedTask;
            secondStopEntered.TrySetResult();
            return releaseSecondStop.Task;
        });
        using var host = BuildHost(services =>
            services.AddBusinessBlockLifetime<LifecycleService>(BackendUids.RuntimeSpecializationCoordinator, _ => service, constructService: true));
        var scheduler = host.Services.GetRequiredService<BusinessBlockScheduler>();
        Task? stopping = null;
        try
        {
            await host.StartAsync().WaitAsync(TestTimeout);
            await WaitUntilAsync(() => service.StartCount == 1);
            var blockId = BackendUids.RuntimeSpecializationCoordinator;
            await host.Services.GetRequiredService<BackendHostPermitFacts>().SetAsync(blockId, false, TestReason("first-run-stop")).WaitAsync(TestTimeout);
            await WaitUntilAsync(() => scheduler.ReadState(blockId) == BusinessBlockState.Stopped);
            await host.Services.GetRequiredService<BackendHostPermitFacts>().SetAsync(blockId, true, TestReason("second-run-start")).WaitAsync(TestTimeout);
            await WaitUntilAsync(() => scheduler.ReadState(blockId) == BusinessBlockState.Running);
            Assert.Equal(2, service.StartCount);

            stopping = host.StopAsync();
            await secondStopEntered.Task.WaitAsync(TestTimeout);
            Assert.False(stopping.IsCompleted);
        }
        finally
        {
            releaseSecondStop.TrySetResult();
            await (stopping ?? host.StopAsync()).WaitAsync(TestTimeout);
        }
        Assert.Equal(2, service.StopCount);
    }

    [Fact]
    public void DirectServicesAreConstructedInEnumerationOrderWhileGenericAliasesStayLazy()
    {
        var events = new ConcurrentQueue<string>();
        using var host = BuildHost(services =>
        {
            services.AddBusinessBlockLifetime<FirstConstructedService>(BackendUids.RuntimeSpecializationCoordinator, _ =>
            { events.Enqueue("first:construct"); return new(events); }, constructService: true);
            services.AddSingleton<LazyAliasService>(_ =>
            { events.Enqueue("alias:construct"); return new(events); });
            services.AddHostedServiceAlias<LazyAliasService>(BackendUids.ServiceAutoStartSettings, static _ => null, forwardRunFailureToHost: false);
            services.AddBusinessBlockLifetime<SecondConstructedService>(BackendUids.TrustedAccessRequestOwner, _ =>
            { events.Enqueue("second:construct"); return new(events); }, constructService: true);
        });
        var hosted = host.Services.GetServices<IHostedService>()
            .Where(static service => service is NonOwningHostedService<FirstConstructedService>
                or NonOwningHostedService<LazyAliasService> or NonOwningHostedService<SecondConstructedService>).ToArray();
        Assert.Equal(new[] { "first:construct", "second:construct" }, events);
        Assert.Equal(3, hosted.Length);
    }

    [Fact]
    public async Task DirectConstructorFailureAbortsHostEnumerationBeforeAnyServiceStarts()
    {
        var events = new ConcurrentQueue<string>();
        var failure = new InvalidOperationException("direct service constructor failed");
        using var host = BuildHost(services =>
        {
            services.AddSingleton<LazyAliasService>(_ =>
            { events.Enqueue("alias:construct"); return new(events); });
            services.AddHostedServiceAlias<LazyAliasService>(BackendUids.ServiceAutoStartSettings, static _ => null, forwardRunFailureToHost: false);
            services.AddBusinessBlockLifetime<ThrowingConstructedService>(BackendUids.ServiceRecoverySettings, _ =>
            { events.Enqueue("direct:construct"); throw failure; }, constructService: true);
        });
        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync());
        Assert.Same(failure, thrown);
        Assert.Equal(new[] { "direct:construct" }, events);
    }

    private static IHost BuildHost(Action<IServiceCollection> configure) => Host.CreateDefaultBuilder()
        .ConfigureLogging(logging => logging.ClearProviders())
        .ConfigureServices(services =>
        {
            configure(services);
            NativeBackendBlockTestSupport.AddFullTreeHostFixture(services);
        }).Build();
    private static NonOwningHostedService<TService> Hosted<TService>(IHost host) where TService : class, IHostedService
        => Assert.Single(host.Services.GetServices<IHostedService>().OfType<NonOwningHostedService<TService>>());
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static BusinessBlockReason TestReason(string cause) => new(BusinessBlockRequestKind.User,
        "hosted-compatibility-test", cause, 0);
    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TestTimeout);
        while (!condition()) await Task.Delay(5, timeout.Token);
    }

    private abstract class OrderedStopService(string name, ConcurrentQueue<StopObservation> observations) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken)
        { observations.Enqueue(new(name, cancellationToken)); return Task.CompletedTask; }
    }
    private sealed class FirstStopService(ConcurrentQueue<StopObservation> observations) : OrderedStopService("first", observations);
    private sealed class SecondStopService(ConcurrentQueue<StopObservation> observations) : OrderedStopService("second", observations);
    private sealed class ThirdStopService(ConcurrentQueue<StopObservation> observations) : OrderedStopService("third", observations);
    private sealed record StopObservation(string Name, CancellationToken Token);

    private sealed class LifecycleService(Func<CancellationToken, Task> stop) : IHostedService
    {
        public TaskCompletionSource StopStarted { get; } = NewSignal();
        public int StartCount { get; private set; }
        public int StopCount { get; private set; }
        public CancellationToken? LastStopToken { get; private set; }
        public Task StartAsync(CancellationToken cancellationToken) { StartCount++; return Task.CompletedTask; }
        public Task StopAsync(CancellationToken cancellationToken)
        { StopCount++; LastStopToken = cancellationToken; StopStarted.TrySetResult(); return stop(cancellationToken); }
    }
    private sealed class GatedBackgroundService(Task executionRelease) : BackgroundService
    {
        public TaskCompletionSource StopStarted { get; } = NewSignal();
        public TaskCompletionSource ExecutionStarted { get; } = NewSignal();
        public int StopCount { get; private set; }
        public CancellationToken? LastStopToken { get; private set; }
        protected override Task ExecuteAsync(CancellationToken stoppingToken)
        { ExecutionStarted.TrySetResult(); return executionRelease; }
        public override Task StopAsync(CancellationToken cancellationToken)
        { StopCount++; LastStopToken = cancellationToken; StopStarted.TrySetResult(); return base.StopAsync(cancellationToken); }
    }
    private sealed class GatedStartService : IHostedService
    {
        public TaskCompletionSource StartEntered { get; } = NewSignal();
        public TaskCompletionSource ReleaseStart { get; } = NewSignal();
        public int StopCount { get; private set; }
        public bool StartStopOverlapped { get; private set; }
        private bool starting;
        public async Task StartAsync(CancellationToken cancellationToken)
        { starting = true; StartEntered.TrySetResult(); await ReleaseStart.Task; starting = false; }
        public Task StopAsync(CancellationToken cancellationToken)
        { StopCount++; StartStopOverlapped = starting; return Task.CompletedTask; }
    }
    private sealed class RecoverableStartService : IHostedService
    {
        public IOException StartupError { get; } = new("first startup failed");
        public TaskCompletionSource FirstStopEntered { get; } = NewSignal();
        public TaskCompletionSource ReleaseFirstStop { get; } = NewSignal();
        public int StartCount { get; private set; }
        public int StopCount { get; private set; }
        public bool IsRunning { get; private set; }
        public Task StartAsync(CancellationToken cancellationToken)
        {
            StartCount++;
            IsRunning = true;
            if (StartCount == 1) throw StartupError;
            return Task.CompletedTask;
        }
        public Task StopAsync(CancellationToken cancellationToken)
        {
            StopCount++;
            IsRunning = false;
            if (StopCount != 1) return Task.CompletedTask;
            FirstStopEntered.TrySetResult();
            return ReleaseFirstStop.Task;
        }
    }
    private abstract class ConstructionService(ConcurrentQueue<string> events, string name) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) { events.Enqueue(name + ":start"); return Task.CompletedTask; }
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
    private sealed class LazyAliasService(ConcurrentQueue<string> events) : ConstructionService(events, "alias");
    private sealed class FirstConstructedService(ConcurrentQueue<string> events) : ConstructionService(events, "first");
    private sealed class SecondConstructedService(ConcurrentQueue<string> events) : ConstructionService(events, "second");
    private sealed class ThrowingConstructedService : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
