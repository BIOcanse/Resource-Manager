using Microsoft.Diagnostics.Tracing.Parsers;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Domain.Settings;
using ResourceManager.App.Domain.GpuPlacement;
using ResourceManager.App.Infrastructure.GpuPlacement.External;
using ResourceManager.App.Infrastructure.Telemetry.Etw;

namespace Resource_Manager_APP.Tests;

public sealed partial class HostManagerSmartCoordinatorScoreOnlyCompositionTests
{
    [Fact]
    public async Task HostedSmartRunOwnsRendererObservationAndReleasesItBeforeReturning()
    {
        var broker = new RendererObservationBroker();
        using var cache = new RendererAbsenceCache(broker);
        var actions = new RecordingRunningGpuActions { AcquireObservation = cache.Acquire };
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(
            warm: true, scoreOnlyEnabled: false, policyExecutionEnabled: false,
            automaticMemoryCleanupEnabled: false, runningGpuActions: actions);
        fixture.RuntimePlanProvider.Publish(fixture.RuntimePlan with
        {
            Version = 2,
            GpuPlacement = fixture.RuntimePlan.GpuPlacement with { GlobalPreciseProviderEnabled = true }
        });
        var identity = new GpuPlacementProcessInstance(601, 6001, "renderer.exe", @"C:\Apps\renderer.exe");
        Assert.False(cache.Read(identity).CanPublishAbsent);
        Assert.Equal(0, broker.Acquires);
        RendererAbsenceCache.Observation previous = default;

        for (var run = 1; run <= 2; run++)
        {
            await fixture.Coordinator.StartAsync(CancellationToken.None);
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                while (broker.Acquires < run || !cache.Read(identity).CanPublishAbsent)
                    await Task.Delay(1, timeout.Token);
                cache.PublishAbsent(identity, previous);
                Assert.False(cache.Read(identity).IsAbsent);
                previous = cache.Read(identity);
                cache.PublishAbsent(identity, previous);
                Assert.True(cache.Read(identity).IsAbsent);
                Assert.Equal(run - 1, broker.Releases);
            }
            finally
            {
                await fixture.Coordinator.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
            }
            Assert.True(fixture.Coordinator.ExecuteTask!.IsCompleted);
            Assert.Equal(run, broker.Releases);
            Assert.False(broker.Source.IsActive);
            cache.PublishAbsent(identity, previous);
            Assert.False(cache.Read(identity).CanPublishAbsent);
            Assert.Equal(run, broker.Acquires);
        }
    }

    [Fact]
    public async Task RunningSmartOnlyHoldsRendererEventsWhileQualificationCanBeUsed()
    {
        var broker = new RendererObservationBroker();
        using var cache = new RendererAbsenceCache(broker);
        var actions = new RecordingRunningGpuActions { AcquireObservation = cache.Acquire };
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(
            warm: true, scoreOnlyEnabled: false, policyExecutionEnabled: false,
            automaticMemoryCleanupEnabled: false, optimizationMode: AppOptimizationModes.Normal,
            runningGpuActions: actions);
        await fixture.Coordinator.StartAsync(CancellationToken.None);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (ReadPrivateReference(fixture.Coordinator, "smartSubscriptions") is null)
                await Task.Delay(1, timeout.Token);
            var coordinatorGate = (SemaphoreSlim)ReadPrivateReference(fixture.Coordinator, "gate")!;
            async Task PublishAndSynchronize(bool gpuMode, bool providerEnabled, bool scoreOnly, bool hardwareEnabled = true)
            {
                await coordinatorGate.WaitAsync(timeout.Token);
                try
                {
                    fixture.ControlZones.HardwarePlacementEnabled = hardwareEnabled;
                    var current = fixture.RuntimePlanProvider.Current;
                    fixture.RuntimePlanProvider.Publish(current with
                    {
                        Version = current.Version + 1,
                        OptimizationMode = CompiledOptimizationModePlan.Compile(gpuMode ? AppOptimizationModes.GpuOnly : AppOptimizationModes.Normal),
                        GpuPlacement = current.GpuPlacement with { GlobalPreciseProviderEnabled = providerEnabled },
                        Diagnostics = current.Diagnostics with { HostManagerSmartCoordinatorScoreOnlyEnabled = scoreOnly }
                    });
                    var synchronize = typeof(ResourceManager.App.Infrastructure.Optimization.HostManagerSmartCoordinator)
                        .GetMethod("SynchronizeSmartSubscriptionsAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
                    await ((Task)synchronize.Invoke(fixture.Coordinator, [timeout.Token])!).WaitAsync(timeout.Token);
                }
                finally { coordinatorGate.Release(); }
            }
            // Warm construction, Normal, disabled precise provider and score-only
            // have no renderer qualification consumer despite a running scheduler.
            await PublishAndSynchronize(false, true, false);
            Assert.Equal(0, broker.Acquires);
            await PublishAndSynchronize(true, false, false);
            Assert.Equal(0, broker.Acquires);
            await PublishAndSynchronize(true, true, true);
            Assert.Equal(0, broker.Acquires);
            await PublishAndSynchronize(true, true, false);
            Assert.Equal(1, broker.Acquires);
            Assert.Equal(0, broker.Releases);
            await PublishAndSynchronize(true, true, false);
            Assert.Equal(1, broker.Acquires);
            await PublishAndSynchronize(false, true, false);
            Assert.Equal(1, broker.Releases);
            await PublishAndSynchronize(true, true, false);
            Assert.Equal(2, broker.Acquires);
            await PublishAndSynchronize(true, true, true);
            Assert.Equal(2, broker.Releases);
            await PublishAndSynchronize(true, true, false, hardwareEnabled: false);
            Assert.Equal(2, broker.Acquires);
            await PublishAndSynchronize(true, true, false);
            Assert.Equal(3, broker.Acquires);
            await PublishAndSynchronize(true, true, false, hardwareEnabled: false);
            Assert.Equal(3, broker.Releases);
        }
        finally { await fixture.Coordinator.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10)); }
        Assert.Equal(broker.Acquires, broker.Releases);
    }

    private sealed class RendererObservationBroker : IKernelEtwSessionBroker
    {
        private int acquires, releases;
        private KernelEtwSessionContext context = new(0, DateTimeOffset.UnixEpoch);
        internal int Acquires => Volatile.Read(ref acquires);
        internal int Releases => Volatile.Read(ref releases);
        internal KernelEtwEventSource Source { get; private set; } = new();
        public IKernelEtwSubscription Subscribe(KernelEtwSubscriptionRequest request)
        {
            context = new(Interlocked.Increment(ref acquires), DateTimeOffset.UtcNow);
            Source = new();
            request.Bind(Source, context);
            Source.Activate();
            return new Held(this, Source);
        }
        public KernelEtwSessionSnapshot GetSnapshot() => new("Running", "fixture", context.Generation,
            context.StartedAt, KernelTraceEventParser.Keywords.Process | KernelTraceEventParser.Keywords.ImageLoad,
            Acquires - Releases, 0);
        private sealed class Held(RendererObservationBroker broker, KernelEtwEventSource source) : IKernelEtwSubscription
        {
            private int disposed;
            public KernelEtwSessionSnapshot GetSnapshot() => broker.GetSnapshot();
            public void Dispose()
            {
                if (Interlocked.Exchange(ref disposed, 1) != 0) return;
                source.Cancel();
                Interlocked.Increment(ref broker.releases);
            }
        }
    }
}
