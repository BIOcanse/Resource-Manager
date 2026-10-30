using ResourceManager.App.Domain.SystemHealth;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Infrastructure.Telemetry.Etw;
using ResourceManager.App.Infrastructure.SystemHealth.Interrupts;

namespace Resource_Manager_APP.Tests;

public sealed class EtwSystemInterruptSnapshotSourceTests
{
    [Fact]
    public void ReadReturnsThePublishedSnapshotWithoutCreatingNewObservations()
    {
        using var fixture = new HostManagerSamplingSubscriptionTestFixture();
        using var idle = new NoAcquisitionBroker();
        using var source = new EtwSystemInterruptSnapshotSource(
            idle,
            fixture.Owner,
            NullLogger<EtwSystemInterruptSnapshotSource>.Instance);

        var first = source.Read();
        var repeated = source.Read();

        Assert.Same(first, repeated);
        Assert.Equal(0, idle.Acquisitions);

    }

    [Fact]
    public void LateLeaseReleaseAfterSourceDisposalIsHarmless()
    {
        using var fixture = new HostManagerSamplingSubscriptionTestFixture();
        using var idle = new NoAcquisitionBroker();
        var source = new EtwSystemInterruptSnapshotSource(
            idle,
            fixture.Owner,
            NullLogger<EtwSystemInterruptSnapshotSource>.Instance);
        var lease = source.AcquireSubscription(
            "report-owner",
            TimeSpan.FromSeconds(10));
        Assert.Equal(0, idle.Acquisitions);

        source.Dispose();

        var exception = Record.Exception(lease.Dispose);
        Assert.Null(exception);
        Assert.Equal(0, idle.Acquisitions);
    }

    private sealed class NoAcquisitionBroker : RecordingNativeKernelEtwBroker
    {
        internal int Acquisitions;
        public override IKernelEtwSubscription Acquire(KernelEtwSubscriptionRequest request)
        {
            Interlocked.Increment(ref Acquisitions);
            throw new InvalidOperationException("A cache read or data registration must not acquire kernel ETW.");
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitWorkerRunsReleaseKernelLeaseRetainValueAndCanSubscribeAgain(bool running)
    {
        using var fixture = new HostManagerSamplingSubscriptionTestFixture();
        using var broker = new DemandBroker(running);
        using var source = new EtwSystemInterruptSnapshotSource(broker, fixture.Owner,
            NullLogger<EtwSystemInterruptSnapshotSource>.Instance);
        IDisposable? lease = null;
        try
        {
            Assert.Equal(0, broker.MetadataSubscribeCount);
            lease = source.AcquireSubscription("report", TimeSpan.FromMilliseconds(250));
            Assert.Equal(0, broker.MetadataSubscribeCount);
            await source.StartAsync(CancellationToken.None);
            await WaitUntilAsync(() => broker.MetadataSubscribeCount == 1);
            lease.Dispose();
            // Subscription withdrawal changes activity; the owner stops the run.
            Assert.Equal(0, broker.MetadataDisposeCount);
            await source.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, broker.MetadataDisposeCount);
            var saved = source.Read();
            await Task.Delay(500);
            Assert.Same(saved, source.Read());
            Assert.Equal(1, broker.MetadataSubscribeCount);

            lease = source.AcquireSubscription("report-again", TimeSpan.FromMilliseconds(250));
            Assert.Equal(1, broker.MetadataSubscribeCount);
            Assert.Same(saved, source.Read());
            await source.StartAsync(CancellationToken.None);
            await WaitUntilAsync(() => broker.MetadataSubscribeCount == 2);
            await source.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(2, broker.MetadataDisposeCount);
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


    private sealed class DemandBroker(bool running) : RecordingNativeKernelEtwBroker
    {
        private int metadataAcquires, metadataReleases, durationAcquires, durationReleases;
        internal int MetadataSubscribeCount => Volatile.Read(ref metadataAcquires);
        internal int MetadataDisposeCount => Volatile.Read(ref metadataReleases);
        internal int DurationAcquires => Volatile.Read(ref durationAcquires);
        internal int DurationReleases => Volatile.Read(ref durationReleases);
        public override IKernelEtwSubscription Acquire(KernelEtwSubscriptionRequest request)
        {
            var metadata = request.Keywords == KernelTraceEventParser.Keywords.ImageLoad;
            var lease = base.Acquire(request);
            if (metadata)
            {
                Interlocked.Increment(ref metadataAcquires);
                if (!running) Resource.Fail(3); // Actual native terminal failure; no null-native event fake.
            }
            else Interlocked.Increment(ref durationAcquires);
            return new Held(this, lease, metadata);
        }
        private sealed class Held(DemandBroker owner, IKernelEtwSubscription lease, bool metadata) : IKernelEtwSubscription
        {
            private int disposed;
            public KernelEtwSessionSnapshot GetSnapshot() => lease.GetSnapshot();
            public void Dispose()
            {
                if (Interlocked.Exchange(ref disposed, 1) != 0) return;
                lease.Dispose();
                if (metadata) Interlocked.Increment(ref owner.metadataReleases);
                else Interlocked.Increment(ref owner.durationReleases);
            }
        }
    }
}

