using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Infrastructure.ResourceBreakdown;
using ResourceManager.App.Infrastructure.Telemetry.Etw;

namespace Resource_Manager_APP.Tests;

public sealed class PhysicalDiskIoWindowTests
{
    [Fact]
    public void DisposeReleasesReadAcquiredLeaseAndPreventsReacquisition()
    {
        var broker = new Broker();
        var reader = NewReader(broker);
        reader.Read();
        var released = 0;
        broker.OnRelease = () => released++;
        reader.Dispose();
        Assert.Equal(1, released);
        Assert.Throws<ObjectDisposedException>(() => reader.Read());
        reader.ApplyMode(ResourceManager.App.Domain.Adaptation.ResourceManagerComputeZoneMode.Normal);
        Assert.Throws<ObjectDisposedException>(() => reader.Read());
        reader.Dispose();
        Assert.Equal(1, released);
    }

    [Fact]
    public void LossDuringSnapshotAssemblySuppressesThePreviousWindow()
    {
        var broker = new Broker();
        using var reader = NewReader(broker);
        reader.Read();
        reader.ObserveDiskTransfer(41, 4096, false, Environment.TickCount64);
        var queries = 0;
        broker.OnSnapshot = () => { if (++queries == 2) broker.EventsLost++; };

        var snapshot = reader.Read();

        Assert.Empty(snapshot.Processes);
        Assert.Equal("Warming", snapshot.ProviderState.State);
    }

    [Fact]
    public void UnavailableSourceCannotReturnPreviouslyRecordedTransfers()
    {
        var broker = new Broker();
        using var reader = NewReader(broker);
        reader.Read();
        reader.ObserveDiskTransfer(41, 4096, false, Environment.TickCount64);
        broker.State = "Unavailable";

        var snapshot = reader.Read();

        Assert.Empty(snapshot.Processes);
        Assert.Equal("Unavailable", snapshot.ProviderState.State);
    }

    [Fact]
    public void ReacquisitionDuringOldLeaseReleasePreservesTheNewWindowAndState()
    {
        var broker = new Broker();
        using var reader = NewReader(broker);
        reader.Read();
        broker.OnRelease = () =>
        {
            reader.ApplyMode(ResourceManager.App.Domain.Adaptation.ResourceManagerComputeZoneMode.Normal);
            reader.Read();
            var source = new KernelEtwEventSource();
            var context = new KernelEtwSessionContext(0, DateTimeOffset.UnixEpoch);
            broker.Request!.Bind(source, context);
            source.Activate();
            reader.ObserveBoundDiskTransfer(source, context, 42, 8192, true, Environment.TickCount64);
        };

        reader.ApplyMode(ResourceManager.App.Domain.Adaptation.ResourceManagerComputeZoneMode.Freeze);

        var snapshot = reader.Read();
        Assert.Equal("Warming", snapshot.ProviderState.State);
        Assert.Empty(snapshot.Processes); // Replacement collection needs its own complete two-second window.
        reader.ApplyMode(ResourceManager.App.Domain.Adaptation.ResourceManagerComputeZoneMode.Freeze);
    }

    [Fact]
    public void CancelledBindingCannotWriteIntoAReplacementWindowInTheSameKernelGeneration()
    {
        var broker = new Broker();
        using var reader = NewReader(broker);
        reader.Read();
        var context = new KernelEtwSessionContext(0, DateTimeOffset.UnixEpoch);
        var previous = new KernelEtwEventSource();
        broker.Request!.Bind(previous, context);
        previous.Activate();
        var now = Environment.TickCount64;
        reader.ObserveBoundDiskTransfer(previous, context, 41, 2048, false, now + 1000);
        previous.Cancel();
        var current = new KernelEtwEventSource();
        broker.Request.Bind(current, context);
        current.Activate();

        // Represents a delivery already admitted before cancellation but delayed
        // until after the consumer has bound its replacement window.
        reader.ObserveBoundDiskTransfer(previous, context, 41, 4096, true, now + 1000);
        reader.ObserveBoundDiskTransfer(current, context, 42, 8192, true, now + 1000);

        var window = reader.ReadWindow(now + 2100);
        Assert.True(window.Complete);
        var only = Assert.Single(window.Processes);
        Assert.Equal(42, only.Key);
        Assert.Equal(4096, only.Value.WriteBytesPerSecond);
    }

    [Fact]
    public void ProductionSubscriptionRequestsPhysicalCompletionsWithoutLogicalFileIo()
    {
        var broker = new Broker();
        using var reader = NewReader(broker);
        reader.Read();
        Assert.True(broker.Request!.Keywords.HasFlag(KernelTraceEventParser.Keywords.DiskIO));
        Assert.True(broker.Request.Keywords.HasFlag(KernelTraceEventParser.Keywords.DiskIOInit));
        Assert.False(broker.Request.Keywords.HasFlag(KernelTraceEventParser.Keywords.FileIO));
        Assert.False(broker.Request.Keywords.HasFlag(KernelTraceEventParser.Keywords.FileIOInit));
    }

    [Fact]
    public void CompletedTransfersKeepReadWriteTotalsAndUnassignedPhysicalIo()
    {
        using var reader = NewReader(new Broker());
        reader.ObserveDiskTransfer(41, 2048, false, 1900);
        reader.ObserveDiskTransfer(41, 4096, true, 2000);
        reader.ObserveDiskTransfer(-1, 512, false, 2100);
        reader.ObserveDiskTransfer(42, 8192, false, 100); // Outside the current two-second window.
        var window = reader.ReadWindow(3000);
        Assert.True(window.Complete);
        Assert.Equal(2, window.Processes.Count);
        Assert.Equal(1024, window.Processes[41].ReadBytesPerSecond);
        Assert.Equal(2048, window.Processes[41].WriteBytesPerSecond);
        Assert.Equal(1, window.Processes[41].ReadEventCount);
        Assert.Equal(1, window.Processes[41].WriteEventCount);
        Assert.Equal(256, window.Processes[0].ReadBytesPerSecond);
    }

    [Fact]
    public void SnapshotDoesNotIncludeACompletionAfterItsTimestamp()
    {
        using var reader = NewReader(new Broker());
        reader.ObserveDiskTransfer(41, 4096, false, 3001);
        Assert.Empty(reader.ReadWindow(3000).Processes);
        Assert.Equal(1, reader.RetainedEventCount);
        Assert.Equal(2048, reader.ReadWindow(3001).Processes[41].ReadBytesPerSecond);
    }

    [Fact]
    public void CapacityLimitSuppressesAnIncompleteWindowAndRecoversWithoutInventedShares()
    {
        using var reader = NewReader(new Broker());
        for (var index = 0; index <= EtwPhysicalDiskIoAttributionReader.MaximumTrackedEvents; index++)
            reader.ObserveDiskTransfer(41, 4096, false, 2000);
        Assert.Equal(EtwPhysicalDiskIoAttributionReader.MaximumTrackedEvents, reader.RetainedEventCount);
        var incomplete = reader.ReadWindow(3000);
        Assert.False(incomplete.Complete); Assert.Empty(incomplete.Processes);
        reader.ObserveDiskTransfer(42, 16384, true, 4000);
        var recovered = reader.ReadWindow(5001);
        Assert.True(recovered.Complete);
        Assert.Equal(8192, Assert.Single(recovered.Processes).Value.WriteBytesPerSecond);
        Assert.Empty(reader.ReadWindow(12001).Processes); Assert.Equal(0, reader.RetainedEventCount);
    }

    [Fact]
    public void HistoricalCumulativeLossDoesNotPermanentlyDisableNewWindows()
    {
        var broker = new Broker { EventsLost = 1 };
        using var reader = NewReader(broker);
        var first = reader.Read();
        Assert.Equal("Warming", first.ProviderState.State); Assert.Empty(first.Processes);
        var future = Environment.TickCount64 + 2100;
        reader.ObserveDiskTransfer(41, 4096, false, future);
        var recovered = reader.ReadWindow(future);
        Assert.True(recovered.Complete); Assert.Equal(2048, recovered.Processes[41].ReadBytesPerSecond);
    }

    private static EtwPhysicalDiskIoAttributionReader NewReader(Broker broker) => new(broker, NullLogger<EtwPhysicalDiskIoAttributionReader>.Instance);
    private sealed class Broker : IKernelEtwSessionBroker
    {
        internal KernelEtwSubscriptionRequest? Request;
        internal int EventsLost;
        internal string State = "Running";
        internal Action? OnSnapshot;
        internal Action? OnRelease;
        public IKernelEtwSubscription Subscribe(KernelEtwSubscriptionRequest request) { Request = request; return new Lease(this); }
        public KernelEtwSessionSnapshot GetSnapshot()
        {
            OnSnapshot?.Invoke();
            return new(State, "owned fake session", 0, DateTimeOffset.UtcNow,
                Request?.Keywords ?? 0, Request is null ? 0 : 1, EventsLost);
        }
        private sealed class Lease(Broker owner) : IKernelEtwSubscription
        {
            public KernelEtwSessionSnapshot GetSnapshot() => owner.GetSnapshot();

            private int disposed;
            public void Dispose()
            {
                if (Interlocked.Exchange(ref disposed, 1) != 0) return;
                var callback = owner.OnRelease;
                owner.OnRelease = null;
                callback?.Invoke();
            }
        }
    }
}

