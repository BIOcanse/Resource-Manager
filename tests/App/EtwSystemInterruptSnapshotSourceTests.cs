using ResourceManager.App.Domain.SystemHealth;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Infrastructure.Telemetry.Etw;
using ResourceManager.App.Domain.Adaptation;
using ResourceManager.App.Infrastructure.SystemHealth.Interrupts;

namespace Resource_Manager_APP.Tests;

public sealed class EtwSystemInterruptSnapshotSourceTests
{
    [Fact]
    public void ReadReturnsThePublishedSnapshotWithoutCreatingNewObservations()
    {
        using var fixture = new HostManagerSamplingSubscriptionTestFixture();
        using var source = new EtwSystemInterruptSnapshotSource(
            new IdleKernelEtwSessionBroker(),
            fixture.Owner,
            NullLogger<EtwSystemInterruptSnapshotSource>.Instance);

        var first = source.Read();
        var repeated = source.Read();

        Assert.Same(first, repeated);

        source.ApplyMode(ResourceManagerComputeZoneMode.Freeze);
        var frozen = source.Read();

        Assert.NotSame(first, frozen);
        Assert.Same(frozen, source.Read());
        Assert.Equal(SystemInterruptProviderStates.Frozen, frozen.ProviderState.State);
    }

    [Fact]
    public void LateLeaseReleaseAfterSourceDisposalIsHarmless()
    {
        using var fixture = new HostManagerSamplingSubscriptionTestFixture();
        var source = new EtwSystemInterruptSnapshotSource(
            new IdleKernelEtwSessionBroker(),
            fixture.Owner,
            NullLogger<EtwSystemInterruptSnapshotSource>.Instance);
        var lease = source.AcquireSubscription(
            "report-owner",
            TimeSpan.FromSeconds(10));

        source.Dispose();

        var exception = Record.Exception(lease.Dispose);
        Assert.Null(exception);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitWorkerRunsReleaseKernelLeaseRetainValueAndCanSubscribeAgain(bool running)
    {
        using var fixture = new HostManagerSamplingSubscriptionTestFixture();
        var broker = new DemandBroker(running);
        using var source = new EtwSystemInterruptSnapshotSource(broker, fixture.Owner,
            NullLogger<EtwSystemInterruptSnapshotSource>.Instance);
        IDisposable? lease = null;
        try
        {
            Assert.Equal(0, broker.SubscribeCount);
            lease = source.AcquireSubscription("report", TimeSpan.FromMilliseconds(250));
            Assert.Equal(0, broker.SubscribeCount);
            await source.StartAsync(CancellationToken.None);
            await WaitUntilAsync(() => broker.SubscribeCount == 2);
            lease.Dispose();
            // Subscription withdrawal changes activity; the owner stops the run.
            Assert.Equal(0, broker.DisposeCount);
            await source.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(2, broker.DisposeCount);
            var saved = source.Read();
            await Task.Delay(500);
            Assert.Same(saved, source.Read());
            Assert.Equal(2, broker.SubscribeCount);

            lease = source.AcquireSubscription("report-again", TimeSpan.FromMilliseconds(250));
            Assert.Equal(2, broker.SubscribeCount);
            Assert.Same(saved, source.Read());
            await source.StartAsync(CancellationToken.None);
            await WaitUntilAsync(() => broker.SubscribeCount == 4);
            await source.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(4, broker.DisposeCount);
        }
        finally
        {
            lease?.Dispose();
            await source.StopAsync(CancellationToken.None);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(10, cancellation.Token);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FrozenRunRetainsOneKernelHolderUntilItsFinalReturn(bool initiallyFrozen)
    {
        using var fixture = new HostManagerSamplingSubscriptionTestFixture();
        var broker = new DemandBroker(true);
        using var source = new EtwSystemInterruptSnapshotSource(broker, fixture.Owner,
            NullLogger<EtwSystemInterruptSnapshotSource>.Instance);
        if (initiallyFrozen) source.ApplyMode(ResourceManagerComputeZoneMode.Freeze);
        Assert.Equal(0, broker.SubscribeCount);
        await source.StartAsync(CancellationToken.None);
        try
        {
            await WaitUntilAsync(() => broker.SubscribeCount == (initiallyFrozen ? 1 : 2));
            for (var cycle = 0; cycle < 3; cycle++)
            {
                source.ApplyMode(ResourceManagerComputeZoneMode.Freeze);
                source.ApplyMode(ResourceManagerComputeZoneMode.Freeze);
                Assert.Equal(SystemInterruptProviderStates.Frozen, source.Read().ProviderState.State);
                Assert.Equal(cycle + (initiallyFrozen ? 1 : 2), broker.SubscribeCount);
                Assert.Equal(cycle + (initiallyFrozen ? 0 : 1), broker.DisposeCount);
                source.ApplyMode(ResourceManagerComputeZoneMode.Normal);
                Assert.Equal(cycle + (initiallyFrozen ? 2 : 3), broker.SubscribeCount);
                Assert.Equal(cycle + (initiallyFrozen ? 0 : 1), broker.DisposeCount);
            }
        }
        finally { await source.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)); }
        Assert.Equal(broker.SubscribeCount, broker.DisposeCount);
    }

    private sealed class DemandBroker(bool running) : IKernelEtwSessionBroker
    {
        private int subscribed;
        private int released;
        internal int SubscribeCount => Volatile.Read(ref subscribed);
        internal int DisposeCount => Volatile.Read(ref released);

        public IKernelEtwSubscription Subscribe(KernelEtwSubscriptionRequest request)
        {
            if (running)
            {
                var events = new KernelEtwEventSource();
                request.Bind(events, new KernelEtwSessionContext(1, DateTimeOffset.UtcNow));
                events.Activate();
            }
            Interlocked.Increment(ref subscribed);
            return new Lease(this);
        }
        public KernelEtwSessionSnapshot GetSnapshot() => new(running ? "Running" : "Unavailable", "recorded broker",
            running ? 1 : 0, DateTimeOffset.UnixEpoch, KernelTraceEventParser.Keywords.None, 1, 0);

        private sealed class Lease(DemandBroker owner) : IKernelEtwSubscription
        {
            public KernelEtwSessionSnapshot GetSnapshot() => owner.GetSnapshot();

            private int disposed;
            public void Dispose()
            {
                if (Interlocked.Exchange(ref disposed, 1) == 0) Interlocked.Increment(ref owner.released);
            }
        }
    }

    private sealed class IdleKernelEtwSessionBroker : IKernelEtwSessionBroker
    {
        public IKernelEtwSubscription Subscribe(KernelEtwSubscriptionRequest request)
            => throw new InvalidOperationException(
                "The cache-only read test must not start ETW.");

        public KernelEtwSessionSnapshot GetSnapshot()
            => new(
                "Idle",
                "No subscription.",
                0,
                DateTimeOffset.UnixEpoch,
                KernelTraceEventParser.Keywords.None,
                0,
                0);
    }
}
