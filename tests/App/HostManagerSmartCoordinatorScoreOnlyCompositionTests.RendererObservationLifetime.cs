using Microsoft.Diagnostics.Tracing.Parsers;
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
