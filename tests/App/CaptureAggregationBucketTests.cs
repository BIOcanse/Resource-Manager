using Resource_Manager_APP.Tests.Oracles.KernelEtw;
using System.Collections.Frozen;
using System.Diagnostics;
using Microsoft.Diagnostics.Tracing.Parsers;
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
        const int processors = 32, processes = 500, eventCount = 2_000_000, slots = 10;
        var heapBefore = GC.GetTotalMemory(true);
        var buffer = new OracleCpuResidencyAggregationBuffer(1_000_000, 5000, 16384, Enumerable.Range(0, processors).ToArray());
        // Continuous: ten 500,000-QPC slots, so the last complete window before 10,000,000 is [5,000,000, 10,000,000).
        buffer.BeginWindow(0, slots);
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
            if (index == eventCount / 2) retainedAtMidpoint = buffer.GetDiagnostics().WindowStorageBytes;
        }
        watch.Stop();
        var intakeNs = watch.Elapsed.TotalNanoseconds / eventCount;
        for (var core = 0; core < processors; core++)
        {
            AddExact(exact, current[core], core, starts[core], 10_000_000, 5_000_000, 10_000_000);
            FeedSwitch(10_000_000, core, current[core], 0);
        }
        var snapshot = buffer.Snapshot();
        Assert.True(snapshot.IsComplete);
        Assert.Equal(5_000_000, snapshot.WindowStartQpc);
        Assert.Equal(10_000_000, snapshot.MeasuredThroughQpc);
        Assert.Equal(0, buffer.GetDiagnostics().CapacityResetCount);
        Assert.Equal(processors * processes, snapshot.Records.Count);
        foreach (var row in snapshot.Records) Assert.Equal(exact[(row.ProcessId, row.LogicalProcessorId)], row.ExecutionTimeQpc);
        var diagnostics = buffer.GetDiagnostics();
        var bound = diagnostics.MaximumWindowStorageBytes;
        var liveBytes = diagnostics.WindowStorageBytes;
        Assert.InRange(liveBytes, 1, bound);
        // Every pair stays live (each core revisits all 500 processes within W): chunks follow 16,000 pairs.
        Assert.Equal(OracleCpuResidencyAggregationBuffer.WindowStorageBytes(processors * processes, slots * 2 + 2), liveBytes);
        Assert.InRange(liveBytes - retainedAtMidpoint, 0, processors * processes * 24L * 2);
        var heapGrowth = GC.GetTotalMemory(true) - heapBefore;
        // Includes pair objects, dictionary capacity, the 500 explicit identities and this test's exact reference/output.
        var managedBound = bound + diagnostics.MaximumActivePairs * 512L + processes * 2048L + 8 * 1024 * 1024;
        Assert.InRange(heapGrowth, 0, managedBound);
        output.WriteLine($"CPU retained GC growth={heapGrowth}, computed total test bound={managedBound}");
        var reads = 20;
        watch.Restart();
        for (var index = 0; index < reads; index++) Assert.True(buffer.Snapshot().IsComplete);
        watch.Stop();
        output.WriteLine($"CPU continuous window: {intakeNs:F1} ns/switch, {watch.Elapsed.TotalMilliseconds / reads:F3} ms/read; pairs={diagnostics.ActivePairCount}, window storage bytes={liveBytes}, cap bytes={bound}");
        buffer.Clear();
        Assert.Equal(0, buffer.GetDiagnostics().WindowStorageBytes);
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

    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    public void CpuWindowIsExactOverItsReportedRange(int slots)
    {
        // The former bucket window had up to one bucket of edge error; both window shapes are exact.
        var buffer = new OracleCpuResidencyAggregationBuffer(1000, 1000, 8, [0]);
        buffer.BeginWindow(0, slots);
        for (var pid = 1; pid <= 2; pid++) { buffer.SeedProcess(pid, pid, "P"); buffer.SeedThread(pid, pid); }
        Switch(buffer, 0, 0, 0, 1);
        var slices = new List<(int Pid, long From, long To)>();
        var old = 1; long start = 0;
        for (var tick = 37; tick <= 1998; tick += 37)
        {
            slices.Add((old, start, tick));
            var next = 3 - old; Switch(buffer, tick, 0, old, next); old = next; start = tick;
        }
        slices.Add((old, start, 2033)); Switch(buffer, 2033, 0, old, 0);
        var snapshot = buffer.Snapshot(); Assert.True(snapshot.IsComplete);
        // Finite: [S, S + W) = [0, 1000). Continuous: the last ten complete 100-QPC slots before 2033.
        Assert.Equal(slots == 0 ? 0 : 1000, snapshot.WindowStartQpc);
        Assert.Equal(slots == 0 ? 1000 : 2000, snapshot.MeasuredThroughQpc);
        var exact = new Dictionary<(int Pid, int Core), long>();
        foreach (var slice in slices)
            AddExact(exact, slice.Pid, 0, slice.From, slice.To, snapshot.WindowStartQpc, snapshot.MeasuredThroughQpc);
        Assert.Equal(exact.Count, snapshot.Records.Count);
        foreach (var row in snapshot.Records) Assert.Equal(exact[(row.ProcessId, 0)], row.ExecutionTimeQpc);
        Assert.Equal(1000, snapshot.Records.Sum(row => row.ExecutionTimeQpc));
    }

    [Fact]
    public void HalfMillionDiskTransfersKeepGroupsAndReleaseExpiredStorage()
    {
        var storage = new StorageCounter();
        using var disk = new OraclePhysicalDiskIoCaptureOwner(new Broker(), new(), static () => 0, pid => new ProcessLease(pid, 100),
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
    public void ContinuousSlotsPreserveExactZeroAndWeightedScoringAtAlignedEdges()
    {
        var host = HostManagerTestPlanFactory.CreatePlan(cpuTopology: HostManagerTestPlanFactory.CreateCpuTopology(2));
        var plan = host.CpuCoreResidency with { ObservationWindowMilliseconds = 1000 };
        var buffer = OracleCpuResidencyCaptureOwner.CreateAggregationBuffer(new(1000, plan.ExecutionTimeSource.MaximumActivePairs,
            plan.PhysicalCoreByLogicalProcessor.ToFrozenDictionary(pair => pair.Key,
                pair => new CpuResidencyCoreAccounting(pair.Value.PhysicalCoreId, pair.Value.CcdId, pair.Value.LogicalProcessorCount, pair.Value.LogicalProcessorShare)),
            "compiled scoring test"), 1000);
        // n = 4 slots of 250 QPC: the window read at 1250 is [250, 1250).
        buffer.BeginWindow(0, 4);
        buffer.SeedProcess(1, 1, "P"); buffer.SeedThread(1, 1);
        Switch(buffer, 0, 0, 0, 1); Switch(buffer, 0, 1, 0, 0);
        Switch(buffer, 250, 0, 1, 0); Switch(buffer, 250, 1, 0, 1);
        Switch(buffer, 1250, 0, 0, 0); Switch(buffer, 1250, 1, 1, 0);
        var actual = buffer.Snapshot(); Assert.True(actual.IsComplete);
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
    public void CpuSkewDropsDeadPairsAndSkewBeyondTheRingIsARingSpanReset()
    {
        // W = 500, n = 5 (100-QPC slots), R = 12.
        var buffer = new OracleCpuResidencyAggregationBuffer(1000, 500, 8, [0, 1]);
        buffer.BeginWindow(0, 5);
        buffer.SeedProcess(1, 1, "P"); buffer.SeedThread(1, 1);
        Switch(buffer, 0, 0, 0, 1); Switch(buffer, 0, 1, 0, 0);
        Switch(buffer, 500, 1, 0, 0); Switch(buffer, 1000, 0, 1, 0);
        Assert.True(buffer.Snapshot().IsComplete);
        Assert.Equal(1, buffer.GetDiagnostics().ActivePairCount);
        var storage = buffer.GetDiagnostics().WindowStorageBytes;
        Assert.Equal(OracleCpuResidencyAggregationBuffer.WindowStorageBytes(1, 12), storage);
        // F = slot(1000) - 5 = 5: the pair (last written slot 9) is still live.
        Switch(buffer, 1500, 1, 0, 0); buffer.Snapshot();
        Assert.Equal(1, buffer.GetDiagnostics().ActivePairCount);
        // F = slot(1500) - 5 = 10: the pair is dropped; its chunk stays for reuse until the next clear.
        Switch(buffer, 2000, 0, 0, 0); buffer.Snapshot();
        Assert.Equal(0, buffer.GetDiagnostics().ActivePairCount);
        Assert.Equal(storage, buffer.GetDiagnostics().WindowStorageBytes);
        buffer.Clear();
        Assert.Equal(0, buffer.GetDiagnostics().WindowStorageBytes);
        // Clear returns to a finite window and removes identities; begin and re-seed explicitly.
        buffer.BeginWindow(0, 5);
        Switch(buffer, 0, 0, 0, 1); Switch(buffer, 0, 1, 0, 0);
        buffer.SeedProcess(1, 1, "P"); buffer.SeedThread(1, 1);
        Switch(buffer, 0, 0, 1, 1); Switch(buffer, 5000, 0, 1, 0);
        Assert.False(buffer.Snapshot().IsComplete);
        Assert.Equal(1, buffer.GetDiagnostics().CapacityResetCount);
        Assert.Equal("ring-span", buffer.GetDiagnostics().LastCapacityReset!.Reason);
        Assert.Equal(0, buffer.GetDiagnostics().WindowStorageBytes);
    }

    [Fact]
    public void DiskPidReuseInsideOneBucketKeepsBirthsSeparateAndEdgesKeepIntegerBytes()
    {
        ulong birth = 100;
        using var disk = new OraclePhysicalDiskIoCaptureOwner(new Broker(), new(), static () => 0, pid => new ProcessLease(pid, birth));
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
        var diskAttempts = 0;
        using var disk = new OraclePhysicalDiskIoCaptureOwner(new Broker(), new(), static () => 0, pid => new ProcessLease(pid, 100),
            allocateBuckets: length => ++diskAttempts == 1 ? throw new IOException("storage admission refused") : OracleCaptureBucketMemory.AllocateManaged(length));
        Assert.Throws<IOException>(() => disk.ObserveDiskTransfer(1, 7UL, false, 1000, 0, 101));
        Assert.Equal(1, diskAttempts); Assert.False(disk.ReadWindow(2000).Complete);
        disk.ObserveDiskTransfer(1, 7UL, false, 4000, 0, 101);
        Assert.True(disk.ReadWindow(4000).Complete);
    }

    [Fact]
    public async Task ManagedOracleDiskOwnerKeepsFailedLedgerCloseUntilDisposeRetry()
    {
        using var ledger = new ResourceManagerLedgerOwner();
        await OracleLedgerCaptureBucketStorage.RunAsync(ledger, allocate =>
        {
            var disk = new OraclePhysicalDiskIoCaptureOwner(new Broker(), new(), static () => 0, pid => new ProcessLease(pid, 100),
                bucketWidthMilliseconds: 1, allocateBuckets: length => new FailCloseOnce(allocate(length)));
            disk.ObserveDiskTransfer(1, 7UL, false, 1000, 0, 101);
            Assert.Throws<IOException>(disk.Dispose);
            Assert.True(ledger.ReadTable().RegisteredBytes > 0);
            Assert.Equal(0UL, ledger.Tick().Released);
            Assert.Throws<ObjectDisposedException>(() => disk.Read());
            disk.Dispose(); disk.Dispose();
            Assert.Equal(0UL, ledger.ReadTable().RegisteredBytes);
            return Task.CompletedTask;
        });
    }

    private sealed class FailCloseOnce(OracleCaptureBucketMemory inner) : OracleCaptureBucketMemory
    {
        private bool failed;
        internal override Span<byte> Bytes => inner.Bytes;
        public override void Dispose()
        {
            if (!failed) { failed = true; throw new IOException("recorded close failure"); }
            inner.Dispose();
        }
    }
    [Fact]
    public void DiskTemporaryIdentityFailureRecoversOnlyWithANewVerifiedCleanWindow()
    {
        var readable = false;
        using var disk = new OraclePhysicalDiskIoCaptureOwner(new Broker(), new(), static () => 0,
            pid => readable ? new ProcessLease(pid, 100) : null);
        disk.ObserveDiskTransfer(1, 7UL, false, 1000, 0, 101);
        readable = true;
        Assert.Empty(disk.ReadWindow(3000).Processes); // Never attach old unverified history to a later PID lookup.
        disk.ObserveDiskTransfer(1, 11UL, false, 4000, 0, 101);
        var recovered = disk.ReadWindow(4001);
        Assert.True(recovered.Complete);
        Assert.Equal(5.5, recovered.Processes[1].ReadBytesPerSecond);
    }

    private static void Switch(OracleCpuResidencyAggregationBuffer buffer, long tick, int core, int old, int next)
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
        internal OracleCaptureBucketMemory Allocate(int length) { LiveBytes += length; PeakBytes = Math.Max(PeakBytes, LiveBytes); return new Counted(this, length); }
        private sealed class Counted(StorageCounter owner, int length) : OracleCaptureBucketMemory
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
    private sealed class Broker : IOracleKernelEtwSessionBroker
    {
        public IOracleKernelEtwSubscription Acquire(OracleKernelEtwSubscriptionRequest request) => throw new NotSupportedException("recorded intake only");
        public KernelEtwSessionSnapshot GetSnapshot() => throw new NotSupportedException("recorded intake only");
    }
}
