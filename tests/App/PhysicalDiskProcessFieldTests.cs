using Microsoft.Diagnostics.Tracing.Parsers;
using ResourceManager.App.Infrastructure.Telemetry.Etw;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.ProcessFields;
using ResourceManager.ReadWrite.Windows.Devices;
using ResourceManager.ReadWrite.Windows.Processes;

namespace Resource_Manager_APP.Tests;

public sealed class PhysicalDiskProcessFieldTests
{
    private const string Read = "process_all_physical_disk_read_bytes_attributed";
    private const string Write = "process_all_physical_disk_write_bytes_attributed";

    [Fact]
    public void OriginalSharedQueuePreservesDiskScopeBirthAndBytesAboveDoublePrecision()
    {
        long tick = 1000; var broker = new Broker(); var releases = 0;
        using var owner = new EtwPhysicalDiskIoCaptureOwner(broker, new(), () => tick,
            pid => new ProcessLease(pid, 100, () => releases++));
        Assert.Null(owner.Read(true).ObservedTraffic!.Processes.FirstOrDefault());
        Assert.Equal(1, broker.Acquires);
        owner.ObserveBoundDiskTransfer(broker.Source!, broker.Context, 41, 9007199254740993UL, false, 2000, 0, 101);
        owner.ObserveBoundDiskTransfer(broker.Source!, broker.Context, 41, 17UL, true, 2000, 1, 101);
        tick = 2500;
        var warming = owner.Read(true);
        Assert.False(warming.ObservedTraffic!.Complete); Assert.Empty(warming.Processes);
        Assert.All(WindowsPhysicalDiskProcessFields.Project([Read, Write], Inventory(), warming.ObservedTraffic).Processes,
            row => Assert.All(row.Values, value => Assert.Null(value.IntegerValue)));
        tick = 3100;
        var complete = owner.Read(true);
        Assert.True(complete.ObservedTraffic!.Complete);
        Assert.Equal("9007199254740993", complete.ObservedTraffic.Processes.Single(row => row.Disk.DiskNumber == 0).ReadBytes);
        Assert.Equal("17", complete.ObservedTraffic.Processes.Single(row => row.Disk.DiskNumber == 1).WriteBytes);
        Assert.All(complete.ObservedTraffic.Processes, row => Assert.Equal("100", row.CreationWindowsFileTime100Ns));
        Assert.Equal(2, releases); // One intake identity lease plus one read/recheck lease shared by both disks.
        Assert.True(broker.Keywords.HasFlag(KernelTraceEventParser.Keywords.Process));
        Assert.Equal(typeof(ProcessMetricFieldRegistry).Assembly, typeof(EtwPhysicalDiskIoCaptureOwner).Assembly);
    }

    [Fact]
    public void ReusedPidCannotReceiveTransfersFromBeforeItsCreationAndFailedRecheckIsUnavailable()
    {
        long tick = 1000; var broker = new Broker(); var recheck = false;
        using var owner = new EtwPhysicalDiskIoCaptureOwner(broker, new(), () => tick,
            pid => { var reads = 0; return new ProcessLease(pid, 200, static () => { }, () => !recheck || ++reads == 1); });
        owner.Read(true);
        owner.ObserveDiskTransfer(41, 4096UL, false, 2000, 3, 199);
        owner.ObserveDiskTransfer(41, 7UL, false, 2000, 3, 201);
        tick = 3100;
        var valid = owner.Read(true);
        Assert.Equal("7", valid.ObservedTraffic!.Processes.Single().ReadBytes);
        Assert.Equal(200UL, valid.Processes[41].ProcessStartKey);
        Assert.Equal(3.5, valid.Processes[41].ReadBytesPerSecond);
        recheck = true;
        var invalid = owner.Read(true);
        Assert.Empty(invalid.Processes);
        var unavailable = invalid.ObservedTraffic!.Processes.Single();
        Assert.Null(unavailable.CreationWindowsFileTime100Ns); Assert.Null(unavailable.ReadBytes);
        Assert.Null(unavailable.WriteBytes);
    }

    [Fact]
    public void LostGenerationAndCancelledLeaseCannotReturnTheOldExactWindow()
    {
        long tick = 1000; var broker = new Broker();
        using var owner = new EtwPhysicalDiskIoCaptureOwner(broker, new(), () => tick,
            pid => new ProcessLease(pid, 100, static () => { }));
        owner.Read(true); var previous = broker.Source!;
        owner.ObserveDiskTransfer(41, 3UL, false, 2000, 0, 101); tick = 3100;
        Assert.True(owner.Read(true).ObservedTraffic!.Complete);
        broker.Lost++;
        var lost = owner.Read(true);
        Assert.False(lost.ObservedTraffic!.Complete); Assert.Empty(lost.Processes);
        owner.Stop(); Assert.Equal(1, broker.Releases);
        owner.Read(true);
        owner.ObserveBoundDiskTransfer(previous, broker.Context, 41, 9UL, false, 4200, 0, 102);
        Assert.Equal(0, owner.RetainedPairCount);
        broker.Generation++;
        Assert.Null(owner.Read(true).ObservedTraffic);
    }

    [Fact]
    public void UnsignedSumOverflowIsUnavailableInsteadOfWrappedOrRounded()
    {
        long tick = 1000; var broker = new Broker();
        using var owner = new EtwPhysicalDiskIoCaptureOwner(broker, new(), () => tick,
            pid => new ProcessLease(pid, 100, static () => { }));
        owner.Read(true);
        owner.ObserveDiskTransfer(41, ulong.MaxValue, false, 2000, 0, 101);
        owner.ObserveDiskTransfer(41, 1UL, false, 2000, 0, 101); tick = 3100;
        Assert.Null(owner.Read(true).ObservedTraffic!.Processes.Single().ReadBytes);
    }

    [Fact]
    public void BackendCachedCurrentAndTypedBorrowKeepEachDiskSeparateAndRemainReadOnly()
    {
        var zero = new PhysicalDiskIoScope(17, 0); var one = new PhysicalDiskIoScope(17, 1);
        var window = new PhysicalDiskProcessIoWindow(DateTimeOffset.UnixEpoch, 2000,
            [new(zero, 41, "100", "9007199254740993", "0"), new(one, 41, "100", "0", "17")]) { Disks = [zero, one] };
        var snapshot = WindowsPhysicalDiskProcessFields.Project([Read, Write], Inventory(), window);
        var current = new ProcessFieldCurrentState(); current.Apply([Read, Write], snapshot);
        using var hostedOwner = new WindowsProcessFieldOwner(current);
        var catalog = hostedOwner.Backend.Catalog;
        Assert.Equal(2, catalog.Devices.Length); Assert.Equal(4, catalog.Fields.Length);
        Assert.All(catalog.Fields, field => Assert.False(field.Write.Supported));
        var field = catalog.Fields.Single(field => field.Field.DeviceIdentity.EndsWith("|etw-disk:17:0", StringComparison.Ordinal)
            && field.Definition.Name == Read).Field;
        Assert.Equal(MetricInteger.Parse("9007199254740993"), hostedOwner.Backend.ReadCached(field).Value!.Integer);
        Assert.Equal(2000UL, hostedOwner.Backend.ReadCached(field).SampleDurationMilliseconds);
        using var borrow = hostedOwner.BorrowSubscriptionCapture();
        var binding = Assert.Single(borrow.Bind([field]));
        Assert.Contains(ProcessMetricDatasetIds.ObservedPhysicalDiskIo, binding.CollectionItemIds);
        var cleared = snapshot.Clear(); current.Apply([Read, Write], cleared);
        Assert.Null(hostedOwner.Backend.ReadCached(field).Value);
        Assert.Equal(4, hostedOwner.Backend.Catalog.Fields.Length);
        var merged = ProcessMetricFieldSnapshot.Merge([Inventory(), snapshot]);
        Assert.Equal(MetricInteger.Parse("17"), merged.FindProcess(41, "100", one)!.Values.Single(value => value.Field == Write).IntegerValue);
        Assert.Empty(merged.FindProcess(41, "100")!.Values);
        Assert.Equal(zero, snapshot.Select(new HashSet<string> { Read }).Processes[0].PhysicalDisk);
    }

    private static ProcessMetricFieldSnapshot Inventory() => new(DateTimeOffset.UnixEpoch, null, [], [new(41, "100", [])]);
    private sealed class ProcessLease(uint pid, ulong birth, Action release, Func<bool>? valid = null) : IWindowsNetworkEndpointProcessLease
    {
        public uint ProcessId => pid;
        public bool TryReadCreationFileTime(out ulong creation) { creation = birth; return valid?.Invoke() ?? true; }
        public void Dispose() => release();
    }
    private sealed class Broker : IKernelEtwSessionBroker
    {
        internal int Acquires, Releases, Lost;
        internal long Generation = 17;
        internal KernelEtwEventSource? Source;
        internal KernelTraceEventParser.Keywords Keywords;
        internal KernelEtwSessionContext Context => new(Generation, DateTimeOffset.UnixEpoch);
        public IKernelEtwSubscription Subscribe(KernelEtwSubscriptionRequest request)
        { Acquires++; Keywords = request.Keywords; Source = new(); request.Bind(Source, Context); Source.Activate(); return new Lease(this, Source); }
        public KernelEtwSessionSnapshot GetSnapshot() => new("Running", "recorded", Generation, DateTimeOffset.UnixEpoch, Keywords, Acquires - Releases, Lost);
        private sealed class Lease(Broker owner, KernelEtwEventSource source) : IKernelEtwSubscription
        {
            public KernelEtwSessionSnapshot GetSnapshot() => owner.GetSnapshot();
            public void Dispose() { source.Cancel(); owner.Releases++; }
        }
    }
}

