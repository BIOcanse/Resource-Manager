using System.Collections.Frozen;
using System.Diagnostics;
using ResourceManager.App.Infrastructure.Adaptation;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.Optimization;
using ResourceManager.App.Infrastructure.CpuTopology;
using ResourceManager.App.Infrastructure.Telemetry.Etw;
using ResourceManager.App.Application.ResourceBreakdown;
using ResourceManager.ReadWrite.Windows.Devices;
using Xunit.Abstractions;

namespace Resource_Manager_APP.Tests;

[Collection(ProcessHeapMeasurementCollection.Name)]
public sealed class CaptureAggregationBucketTests(ITestOutputHelper output)
{
    private static readonly DateTimeOffset Time = DateTimeOffset.UnixEpoch;

    [Fact]
    public void TwoMillionSwitchesKeepBoundedTotalsAndMatchExactWindow()
    {
        const int processors = 32, processes = 500, eventCount = 2_000_000;
        var heapBefore = GC.GetTotalMemory(true);
        var storage = new StorageCounter();
        var buffer = new CpuResidencyAggregationBuffer(1_000_000, 5000, 16384, 50, storage.Allocate);
        for (var pid = 1; pid <= processes; pid++)
        {
            buffer.SeedProcess(pid, pid, "P");
            for (var core = 0; core < processors; core++) buffer.SeedThread(pid, pid * processors + core);
        }
        void FeedSwitch(long tick, int core, int old, int next) => buffer.ObserveContextSwitch(tick, Time, core,
            old, old == 0 ? 0 : old * processors + core, next, null, next == 0 ? 0 : next * processors + core);
        var current = new int[processors]; var starts = new long[processors];
        for (var core = 0; core < processors; core++) { current[core] = core + 1; FeedSwitch(0, core, 0, current[core]); }
        var retainedAtMidpoint = 0L;
        var exact = new Dictionary<(int Pid, int Core), long>();
        var watch = Stopwatch.StartNew();
        for (var index = 1; index <= eventCount; index++)
        {
            var tick = index * 5L; var core = (index - 1) % processors;
            var next = ((index - 1) / processors + core * 17) % processes + 1;
            AddExact(exact, current[core], core, starts[core], tick, 5_000_000, 10_000_000);
            FeedSwitch(tick, core, current[core], next);
            starts[core] = tick; current[core] = next;
            if (index == eventCount / 2) retainedAtMidpoint = storage.LiveBytes;
        }
        watch.Stop();
        var intakeNs = watch.Elapsed.TotalNanoseconds / eventCount;
        for (var core = 0; core < processors; core++)
        {
            AddExact(exact, current[core], core, starts[core], 10_000_000, 5_000_000, 10_000_000);
            FeedSwitch(10_000_000, core, current[core], 0);
        }
        var ids = Enumerable.Range(0, processors).ToArray();
        var snapshot = buffer.Snapshot(ids);
        Assert.True(snapshot.IsComplete);
        Assert.Equal(0, buffer.GetDiagnostics().CapacityResetCount);
        Assert.Equal(processors * processes, snapshot.Records.Count);
        foreach (var row in snapshot.Records) Assert.Equal(exact[(row.ProcessId, row.LogicalProcessorId)], row.ExecutionTimeQpc);
        var diagnostics = buffer.GetDiagnostics();
        var bound = diagnostics.MaximumBucketBytes;
        Assert.InRange(storage.LiveBytes, 1, bound);
        Assert.Equal(storage.LiveBytes, diagnostics.RetainedBucketBytes);
        Assert.InRange(storage.LiveBytes - retainedAtMidpoint, 0, processors * processes * 24L * 2);
        var heapGrowth = GC.GetTotalMemory(true) - heapBefore;
        // Includes pair objects, dictionary capacity, the 500 explicit identities and this test's exact reference/output.
        var managedBound = bound + diagnostics.MaximumActivePairs * 512L + processes * 2048L + 8 * 1024 * 1024;
        Assert.InRange(heapGrowth, 0, managedBound);
        output.WriteLine($"CPU retained GC growth={heapGrowth}, computed total test bound={managedBound}");
        var reads = 20;
        watch.Restart();
        for (var index = 0; index < reads; index++) Assert.True(buffer.Snapshot(ids).IsComplete);
        watch.Stop();
        output.WriteLine($"CPU bucket: {intakeNs:F1} ns/switch, {watch.Elapsed.TotalMilliseconds / reads:F3} ms/read; pairs={diagnostics.ActivePairCount}, live bucket bytes={storage.LiveBytes}, peak={storage.PeakBytes}, cap bytes={bound}");
        buffer.Clear();
        Assert.Equal(0, storage.LiveBytes);
        Assert.Equal(0, buffer.GetDiagnostics().RetainedBucketBytes);
    }

    [Fact]
    public void OldExactEventModelMeasuresIntakeAndReadOnTheSameFeed()
    {
        // The former hot data path: one 48-byte closed record per interval and a dictionary reduction per read.
        // Lifecycle/ETW dispatch is excluded from this reference, so its intake time is a lower bound.
        const int count = 2_000_000;
        var events = new Queue<OldSlice>(); var starts = new long[32]; var current = Enumerable.Range(1, 32).ToArray();
        var watch = Stopwatch.StartNew();
        for (var index = 1; index <= count; index++)
        {
            var tick = index * 5L; var core = (index - 1) % 32;
            events.Enqueue(new(starts[core], tick, current[core], core, 0, 0));
            starts[core] = tick; current[core] = ((index - 1) / 32 + core * 17) % 500 + 1;
            while (events.TryPeek(out var first) && first.End <= tick - 5_000_000) events.Dequeue();
        }
        watch.Stop(); var intake = watch.Elapsed.TotalNanoseconds / count;
        Dictionary<(int Pid, int Core), long>? exact = null;
        watch.Restart();
        for (var read = 0; read < 20; read++)
        {
            exact = new();
            foreach (var slice in events) AddExact(exact, slice.Pid, slice.Core, slice.Start, slice.End, 5_000_000, 10_000_000);
        }
        watch.Stop(); Assert.NotEmpty(exact!);
        output.WriteLine($"CPU old exact event model (cap disabled to retain the full five seconds): {intake:F1} ns/switch, {watch.Elapsed.TotalMilliseconds / 20:F3} ms/read; retained events={events.Count}, payload bytes={events.Count * 48L}");
    }

    [Fact]
    public void UnalignedCpuWindowHasOnlyTheDocumentedEdgeError()
    {
        var buffer = new CpuResidencyAggregationBuffer(1000, 1000, 8, 100);
        for (var pid = 1; pid <= 2; pid++) { buffer.SeedProcess(pid, pid, "P"); buffer.SeedThread(pid, pid); }
        Switch(buffer, 0, 0, 0, 1);
        var exact = new Dictionary<(int Pid, int Core), long>();
        var old = 1; long start = 0;
        for (var tick = 37; tick <= 1998; tick += 37)
        {
            AddExact(exact, old, 0, start, tick, 1033, 2033);
            var next = 3 - old; Switch(buffer, tick, 0, old, next); old = next; start = tick;
        }
        AddExact(exact, old, 0, start, 2033, 1033, 2033); Switch(buffer, 2033, 0, old, 0);
        var snapshot = buffer.Snapshot([0]); Assert.True(snapshot.IsComplete);
        foreach (var row in snapshot.Records) Assert.InRange(Math.Abs(row.ExecutionTimeQpc - exact[(row.ProcessId, 0)]), 0, 200);
        Assert.InRange(snapshot.Records.Sum(row => row.ExecutionTimeQpc), 997, 1000);
    }

    [Fact]
    public void HalfMillionDiskTransfersKeepGroupsAndReleaseExpiredStorage()
    {
        var storage = new StorageCounter();
        using var disk = new EtwPhysicalDiskIoCaptureOwner(new Broker(), new(), static () => 0, pid => new ProcessLease(pid, 100),
            allocateBuckets: storage.Allocate);
        var watch = Stopwatch.StartNew();
        for (var index = 0; index < 500_000; index++)
            disk.ObserveDiskTransfer(index % 500 + 1, 7UL, index % 2 == 0, index / 50, (uint)(index % 3), 101);
        watch.Stop(); var intake = watch.Elapsed.TotalNanoseconds / 500_000;
        var window = disk.ReadWindow(10000); Assert.True(window.Complete); Assert.Equal(500, window.Processes.Count);
        Assert.Equal(100_000 * 3.5, window.Processes.Values.Sum(row => row.ReadBytesPerSecond + row.WriteBytesPerSecond));
        Assert.InRange(disk.RetainedPairCount, 1, 1500);
        Assert.Equal(storage.LiveBytes, disk.RetainedBucketBytes);
        Assert.InRange(storage.LiveBytes, 1, 8192L * 22 * 40);
        watch.Restart();
        for (var read = 0; read < 20; read++) Assert.True(disk.ReadWindow(10000).Complete);
        watch.Stop();
        output.WriteLine($"Disk bucket: {intake:F1} ns/event, {watch.Elapsed.TotalMilliseconds / 20:F3} ms/read; pairs={disk.RetainedPairCount}, live bytes={storage.LiveBytes}, peak={storage.PeakBytes}");
        Assert.Empty(disk.ReadWindow(20000).Processes); Assert.Equal(0, storage.LiveBytes);
    }

    [Fact]
    public void OldDiskQueueModelMeasuresTheSameTransferFeed()
    {
        var events = new Queue<OldDiskEvent>();
        var watch = Stopwatch.StartNew();
        for (var index = 0; index < 500_000; index++) events.Enqueue(new(index / 50, index % 500 + 1, (uint)(index % 3), 7, index % 2 == 0, 101));
        watch.Stop(); var intake = watch.Elapsed.TotalNanoseconds / 500_000;
        watch.Restart();
        for (var read = 0; read < 20; read++)
        {
            while (events.TryPeek(out var oldest) && oldest.Tick < 2000) events.Dequeue();
            var disks = events.Where(item => item.Disk is not null && item.Tick <= 10000).Select(item => item.Disk).Distinct().Order().ToArray();
            Assert.Equal(3, disks.Length);
            var selected = events.Where(item => item.Tick >= 8000 && item.Tick <= 10000).ToArray();
            var sums = selected.GroupBy(item => (item.Pid, item.Disk, item.Write)).Select(group => group.Sum(item => (double)item.Bytes)).ToArray();
            Assert.NotEmpty(sums);
        }
        watch.Stop();
        output.WriteLine($"Disk old queue model (event cap disabled): {intake:F1} ns/event, {watch.Elapsed.TotalMilliseconds / 20:F3} ms/read; payload bytes={events.Count * 56L}");
    }

    [Fact]
    public void PageSizedHistoryUsesRealLedgerStorageUntilItExpires()
    {
        using var ledger = new ResourceManagerLedgerOwner();
        var buffer = new CpuResidencyAggregationBuffer(1000, 1000, 8, 1, length => LedgerCaptureBucketMemory.Create(ledger, length));
        buffer.SeedProcess(1, 1, "P"); buffer.SeedThread(1, 1); Switch(buffer, 0, 0, 0, 1); Switch(buffer, 1000, 0, 1, 0);
        Assert.True(buffer.Snapshot([0]).IsComplete);
        Assert.True(ledger.ReadTable().RegisteredBytes >= 1002UL * 24);
        Assert.Equal(0UL, ledger.Tick().Released); // History is canonical and cannot be reconstructed after eviction.
        Switch(buffer, 2100, 0, 0, 0); buffer.Snapshot([0]);
        Assert.Equal(0UL, ledger.ReadTable().RegisteredBytes);
        buffer.Clear();
        using var small = LedgerCaptureBucketMemory.Create(ledger, 100);
        Assert.Equal(0UL, ledger.ReadTable().RegisteredBytes);
    }

    [Fact]
    public void DefaultBucketsPreserveExactZeroAndWeightedScoringAtAlignedEdges()
    {
        var host = HostManagerTestPlanFactory.CreatePlan(cpuTopology: HostManagerTestPlanFactory.CreateCpuTopology(2));
        var plan = host.CpuCoreResidency with { ObservationWindowMilliseconds = 1000 };
        var buffer = EtwCpuResidencyCaptureOwner.CreateAggregationBuffer(new(1000, plan.ExecutionTimeSource.MaximumActivePairs,
            plan.PhysicalCoreByLogicalProcessor.ToFrozenDictionary(pair => pair.Key,
                pair => new CpuResidencyCoreAccounting(pair.Value.PhysicalCoreId, pair.Value.CcdId, pair.Value.LogicalProcessorCount, pair.Value.LogicalProcessorShare)),
            "compiled scoring test", plan.ExecutionTimeSource.BucketWidthMilliseconds), 1000);
        buffer.SeedProcess(1, 1, "P"); buffer.SeedThread(1, 1);
        Switch(buffer, 0, 0, 0, 1); Switch(buffer, 0, 1, 0, 0);
        Switch(buffer, 250, 0, 1, 0); Switch(buffer, 250, 1, 0, 1);
        Switch(buffer, 1250, 0, 0, 0); Switch(buffer, 1250, 1, 1, 0);
        var actual = buffer.Snapshot([0, 1]); Assert.True(actual.IsComplete);
        var exact = actual with { Records = [new(1, 1, 1, "P", 1, 1, 1, 1000, 1)] };
        var weights = host.CpuScoring!.CoreWeights.Cores.Select(core => core.ReferenceWeight).ToArray();
        var config = HostManagerComputeScoringConfigurationFactory.Create(host.SmartCoordinator, host.CpuScoring);
        using var native = new NativeComputeScoringSession(in config, weights);
        double Score(CpuResidencyAggregationSnapshot snapshot)
        {
            var projected = EtwCpuCoreResidencyReader.BuildProcessSnapshot(plan, 7, snapshot);
            var process = Assert.Single(projected);
            Assert.DoesNotContain(process.LogicalProcessors, row => row.LogicalProcessorId == 0);
            var rows = process.PhysicalCores.Select(core => new NativeComputeScoringCpuCoreInput
            {
                ProcessIndex = 0,
                CoreIndex = (uint)Array.FindIndex(host.CpuScoring.CoreWeights.Cores.ToArray(), weight => weight.PhysicalCoreId == core.PhysicalCoreId),
                UsagePercent = core.UsagePercent
            }).ToArray();
            double[] result = [0];
            Assert.Equal(NativeComputeScoringStatus.Ok, native.CalculateWeightedCpuUse(rows, result));
            return result[0];
        }
        Assert.Equal(Score(exact), Score(actual));
        buffer.Clear();
    }

    [Fact]
    public void CpuSkewStorageShrinksAndSkewBeyondTheBoundIsIncomplete()
    {
        var storage = new StorageCounter();
        var buffer = new CpuResidencyAggregationBuffer(1000, 500, 8, 5, storage.Allocate);
        buffer.SeedProcess(1, 1, "P"); buffer.SeedThread(1, 1);
        Switch(buffer, 0, 0, 0, 1); Switch(buffer, 0, 1, 0, 0);
        Switch(buffer, 500, 1, 0, 0); Switch(buffer, 1000, 0, 1, 0);
        Assert.True(buffer.Snapshot([0, 1]).IsComplete);
        Assert.True(storage.LiveBytes > 102 * 24);
        Switch(buffer, 1500, 1, 0, 0); buffer.Snapshot([0, 1]);
        Assert.Equal(102 * 24, storage.LiveBytes);
        Switch(buffer, 2000, 0, 0, 0); buffer.Snapshot([0, 1]);
        Assert.Equal(0, storage.LiveBytes);
        buffer.Clear();
        Switch(buffer, 0, 0, 0, 1); Switch(buffer, 0, 1, 0, 0); // Clear removed identities too; re-seed explicitly.
        buffer.SeedProcess(1, 1, "P"); buffer.SeedThread(1, 1);
        Switch(buffer, 0, 0, 1, 1); Switch(buffer, 5000, 0, 1, 0);
        Assert.False(buffer.Snapshot([0, 1]).IsComplete);
        Assert.Equal(1, buffer.GetDiagnostics().CapacityResetCount);
        Assert.Equal(0, storage.LiveBytes);
    }

    [Fact]
    public void DiskPidReuseInsideOneBucketKeepsBirthsSeparateAndEdgesKeepIntegerBytes()
    {
        ulong birth = 100;
        using var disk = new EtwPhysicalDiskIoCaptureOwner(new Broker(), new(), static () => 0, pid => new ProcessLease(pid, birth));
        disk.ObserveDiskTransfer(1, 9007199254740993UL, false, 1050, 0, 101);
        birth = 200; disk.ObserveProcessIdentityChange(1);
        disk.ObserveDiskTransfer(1, 7UL, false, 1060, 0, 201);
        var current = disk.ReadWindow(3000);
        Assert.True(current.Complete); Assert.Equal(3.5, current.Processes[1].ReadBytesPerSecond);
        Assert.Equal(200UL, current.Processes[1].ProcessStartKey);
        // A partial bucket is omitted as a whole, never divided or rounded into invented bytes.
        disk.ObserveDiskTransfer(1, 11UL, false, 1090, 0, 201);
        var edge = disk.ReadWindow(3070);
        Assert.True(edge.Complete); Assert.Empty(edge.Processes);
    }

    [Fact]
    public void StorageAdmissionFailureCannotPublishACompleteWindowOrAllocateAFallback()
    {
        var cpuAttempts = 0;
        var cpu = new CpuResidencyAggregationBuffer(1000, 1000, 8, 50,
            length => ++cpuAttempts == 1 ? throw new IOException("storage admission refused") : CaptureBucketMemory.AllocateManaged(length));
        cpu.SeedProcess(1, 1, "P"); cpu.SeedThread(1, 1); Switch(cpu, 0, 0, 0, 1);
        Assert.Throws<IOException>(() => Switch(cpu, 1000, 0, 1, 0));
        Assert.Equal(1, cpuAttempts); Assert.False(cpu.Snapshot([0]).IsComplete);
        Switch(cpu, 1000, 0, 0, 1); Switch(cpu, 2000, 0, 1, 0);
        Assert.True(cpu.Snapshot([0]).IsComplete); cpu.Clear();
        var diskAttempts = 0;
        using var disk = new EtwPhysicalDiskIoCaptureOwner(new Broker(), new(), static () => 0, pid => new ProcessLease(pid, 100),
            allocateBuckets: length => ++diskAttempts == 1 ? throw new IOException("storage admission refused") : CaptureBucketMemory.AllocateManaged(length));
        Assert.Throws<IOException>(() => disk.ObserveDiskTransfer(1, 7UL, false, 1000, 0, 101));
        Assert.Equal(1, diskAttempts); Assert.False(disk.ReadWindow(2000).Complete);
        disk.ObserveDiskTransfer(1, 7UL, false, 4000, 0, 101);
        Assert.True(disk.ReadWindow(4000).Complete);
    }

    [Fact]
    public void DiskOwnerKeepsFailedLedgerCloseUntilDisposeRetry()
    {
        using var ledger = new ResourceManagerLedgerOwner();
        var disk = new EtwPhysicalDiskIoCaptureOwner(new Broker(), new(), static () => 0, pid => new ProcessLease(pid, 100),
            bucketWidthMilliseconds: 1, allocateBuckets: length => new FailCloseOnce(LedgerCaptureBucketMemory.Create(ledger, length)));
        disk.ObserveDiskTransfer(1, 7UL, false, 1000, 0, 101);
        Assert.Throws<IOException>(disk.Dispose);
        Assert.True(ledger.ReadTable().RegisteredBytes > 0);
        Assert.Equal(0UL, ledger.Tick().Released);
        Assert.Throws<ObjectDisposedException>(() => disk.Read());
        disk.Dispose(); disk.Dispose();
        Assert.Equal(0UL, ledger.ReadTable().RegisteredBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CpuOwnerKeepsFailedLedgerCloseAcrossStopOrBrokerRebind(bool rebind)
    {
        using var ledger = new ResourceManagerLedgerOwner();
        var broker = new RebindingBroker();
        using var cpu = new EtwCpuResidencyCaptureOwner(broker,
            () => new(1000, 8, new Dictionary<int, CpuResidencyCoreAccounting> { [0] = new("core:0", "", 1, 1) }.ToFrozenDictionary(), "recorded", 1),
            new(), static (_, _) => { }, allocateBuckets: length => new FailCloseOnce(LedgerCaptureBucketMemory.Create(ledger, length)));
        cpu.EnsureStarted();
        // Feed the actual active buffer without synthesizing OS TraceEvent objects or starting ETW.
        var session = typeof(EtwCpuResidencyCaptureOwner).GetField("activeSession", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(cpu)!;
        var buffer = (CpuResidencyAggregationBuffer)session.GetType().GetProperty("Buffer")!.GetValue(session)!;
        buffer.SeedProcess(1, 1, "P"); buffer.SeedThread(1, 1);
        Switch(buffer, 0, 0, 0, 1); Switch(buffer, Stopwatch.Frequency, 0, 1, 0);
        Assert.True(ledger.ReadTable().RegisteredBytes > 0);
        if (rebind) Assert.Throws<IOException>(broker.Rebind);
        else Assert.Throws<IOException>(() => cpu.Stop());
        Assert.True(cpu.HasSessionLease);
        Assert.True(ledger.ReadTable().RegisteredBytes > 0);
        Assert.Equal(0UL, ledger.Tick().Released);
        if (rebind) broker.Rebind(); else cpu.Stop();
        Assert.Equal(0UL, ledger.ReadTable().RegisteredBytes);
        cpu.Dispose(); cpu.Dispose();
        Assert.Equal(1, broker.Releases);
    }

    private sealed class FailCloseOnce(CaptureBucketMemory inner) : CaptureBucketMemory
    {
        private bool failed;
        internal override Span<byte> Bytes => inner.Bytes;
        public override void Dispose()
        {
            if (!failed) { failed = true; throw new IOException("recorded close failure"); }
            inner.Dispose();
        }
    }
    private sealed class RebindingBroker : IKernelEtwSessionBroker
    {
        private KernelEtwSubscriptionRequest? request;
        private KernelEtwEventSource? source;
        internal int Releases;
        public IKernelEtwSubscription Subscribe(KernelEtwSubscriptionRequest next) { request = next; Rebind(); return new Lease(this); }
        internal void Rebind()
        { source?.Cancel(); source = new(); request!.Bind(source, new(7, Time)); source.Activate(); }
        public KernelEtwSessionSnapshot GetSnapshot() => new("Running", "recorded", 7, Time, request!.Keywords, 1 - Releases, 0);
        private sealed class Lease(RebindingBroker owner) : IKernelEtwSubscription
        {
            public KernelEtwSessionSnapshot GetSnapshot() => owner.GetSnapshot();
            public void Dispose() { owner.source?.Cancel(); owner.Releases++; }
        }
    }

    [Fact]
    public void DiskTemporaryIdentityFailureRecoversOnlyWithANewVerifiedCleanWindow()
    {
        var readable = false;
        using var disk = new EtwPhysicalDiskIoCaptureOwner(new Broker(), new(), static () => 0,
            pid => readable ? new ProcessLease(pid, 100) : null);
        disk.ObserveDiskTransfer(1, 7UL, false, 1000, 0, 101);
        readable = true;
        Assert.Empty(disk.ReadWindow(3000).Processes); // Never attach old unverified history to a later PID lookup.
        disk.ObserveDiskTransfer(1, 11UL, false, 4000, 0, 101);
        var recovered = disk.ReadWindow(4001);
        Assert.True(recovered.Complete);
        Assert.Equal(5.5, recovered.Processes[1].ReadBytesPerSecond);
    }

    [Fact]
    public void CpuResizeRetainsBothFailedLedgerCloseObligations()
    {
        using var ledger = new ResourceManagerLedgerOwner();
        var buffer = new CpuResidencyAggregationBuffer(1000, 1000, 8, 1,
            length => new FailCloseOnce(LedgerCaptureBucketMemory.Create(ledger, length)));
        buffer.SeedProcess(1, 1, "P"); buffer.SeedThread(1, 1);
        Switch(buffer, 0, 0, 0, 1); Switch(buffer, 0, 1, 0, 0);
        Switch(buffer, 1000, 1, 0, 0);
        Assert.Throws<IOException>(() => Switch(buffer, 2000, 0, 1, 0));
        Assert.False(buffer.Snapshot([0, 1]).IsComplete);
        Assert.True(ledger.ReadTable().RegisteredBytes > 0);
        Assert.Equal(0UL, ledger.Tick().Released);
        buffer.Clear();
        Assert.Equal(0UL, ledger.ReadTable().RegisteredBytes);
    }

    private static void Switch(CpuResidencyAggregationBuffer buffer, long tick, int core, int old, int next)
        => buffer.ObserveContextSwitch(tick, Time, core, old, old, next, null, next);
    private static void AddExact(Dictionary<(int Pid, int Core), long> totals, int pid, int core, long from, long to, long start, long end)
    {
        var duration = Math.Min(to, end) - Math.Max(from, start);
        if (duration > 0) totals[(pid, core)] = totals.GetValueOrDefault((pid, core)) + duration;
    }
    private readonly record struct OldSlice(long Start, long End, long PidValue, long CoreValue, long ProcessReference, long ThreadReference)
    { internal int Pid => (int)PidValue; internal int Core => (int)CoreValue; }
    private readonly record struct OldDiskEvent(long Tick, int Pid, uint? Disk, ulong Bytes, bool Write, ulong? EventTime);
    private sealed class StorageCounter
    {
        internal long LiveBytes, PeakBytes;
        internal CaptureBucketMemory Allocate(int length) { LiveBytes += length; PeakBytes = Math.Max(PeakBytes, LiveBytes); return new Counted(this, length); }
        private sealed class Counted(StorageCounter owner, int length) : CaptureBucketMemory
        {
            private byte[]? bytes = new byte[length];
            internal override Span<byte> Bytes => bytes ?? throw new ObjectDisposedException(nameof(Counted));
            public override void Dispose() { if (bytes is null) return; bytes = null; owner.LiveBytes -= length; }
        }
    }
    private sealed class ProcessLease(uint pid, ulong birth) : IWindowsNetworkEndpointProcessLease
    {
        public uint ProcessId => pid;
        public bool TryReadCreationFileTime(out ulong value) { value = birth; return true; }
        public void Dispose() { }
    }
    private sealed class Broker : IKernelEtwSessionBroker
    {
        public IKernelEtwSubscription Subscribe(KernelEtwSubscriptionRequest request) => throw new NotSupportedException("recorded intake only");
        public KernelEtwSessionSnapshot GetSnapshot() => throw new NotSupportedException("recorded intake only");
    }
}
