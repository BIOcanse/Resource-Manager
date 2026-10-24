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
    public async Task ActualWorkerReleasesLastKernelLeaseRetainsValueAndCanSubscribeAgain(bool running)
    {
        using var fixture = new HostManagerSamplingSubscriptionTestFixture();
        var broker = new DemandBroker(running);
        using var source = new EtwSystemInterruptSnapshotSource(broker, fixture.Owner,
            NullLogger<EtwSystemInterruptSnapshotSource>.Instance);
        await source.StartAsync(CancellationToken.None);
        IDisposable? lease = null;
        try
        {
            Assert.Equal(0, broker.SubscribeCount);
            lease = source.AcquireSubscription("report", TimeSpan.FromMilliseconds(250));
            await WaitUntilAsync(() => broker.SubscribeCount == 1);
            lease.Dispose();
            await WaitUntilAsync(() => broker.DisposeCount == 1);
            var saved = source.Read();
            await Task.Delay(500);
            Assert.Same(saved, source.Read());
            Assert.Equal(1, broker.SubscribeCount);

            lease = source.AcquireSubscription("report-again", TimeSpan.FromMilliseconds(250));
            await WaitUntilAsync(() => broker.SubscribeCount == 2);
            await source.StopAsync(CancellationToken.None);
            Assert.Equal(2, broker.DisposeCount);
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
