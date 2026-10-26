using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Diagnostics.Tracing.Parsers;
using ResourceManager.App.Application.ResourceBreakdown;
using ResourceManager.App.Infrastructure.Telemetry.Etw;
using ResourceManager.ReadWrite.Windows.Devices;

namespace ResourceManager.ReadWrite.Windows.Processes;

/// <summary>The shared DiskIO subscription with bounded byte totals at intake.
/// No worker, second window or physical-device resolver is installed.</summary>
public sealed class EtwPhysicalDiskIoCaptureOwner : IDisposable
{
    private const KernelTraceEventParser.Keywords RequiredKeywords = KernelTraceEventParser.Keywords.DiskIO | KernelTraceEventParser.Keywords.DiskIOInit | KernelTraceEventParser.Keywords.Process;

    private const long WindowMilliseconds = 2000;
    private const long RetentionMilliseconds = 8000;
    internal const int MaximumActivePairs = 8192;
    internal const int DefaultBucketWidthMilliseconds = 100;
    private readonly IKernelEtwSessionBroker broker;
    private readonly KernelEtwDiagnostics diagnostics;
    private readonly Func<long> clock;
    private readonly Func<uint, IWindowsNetworkEndpointProcessLease?> openProcess;
    private readonly object subscriptionGate = new();
    private readonly object eventGate = new();
    private readonly Dictionary<DiskPair, DiskTotals> pairs = [];
    private readonly Dictionary<uint, long> disks = [];
    private readonly Dictionary<int, ulong?> processBirths = [];
    private readonly int bucketWidthMilliseconds, bucketCount, maximumActivePairs;
    private readonly Func<int, CaptureBucketMemory> allocateBuckets;
    private long lastPrunedBucket = long.MinValue;

    private IKernelEtwSubscription? subscription;
    private long generation;
    private long incompleteThroughTick = long.MinValue;
    private long startedTick = long.MinValue;
    private long processRevision;
    private int observedLoss;
    private bool disposed, disposeCompleted;
    private volatile string state = PhysicalDiskIoProviderStates.Idle;
    private volatile string message = "ETW DiskIO 物理磁盘归因按需启动。";
    public string State => state;
    public string Message => message;
    public bool IsRunning { get { lock (subscriptionGate) return subscription is not null && (Volatile.Read(ref subscription)?.GetSnapshot() ?? broker.GetSnapshot()) is { IsRunning: true } snapshot && snapshot.Generation == Volatile.Read(ref generation); } }

    public EtwPhysicalDiskIoCaptureOwner(IKernelEtwSessionBroker broker, KernelEtwDiagnostics? diagnostics = null,
        PhysicalDiskIoCaptureConfiguration? configuration = null)
        : this(broker, diagnostics ?? new(), static () => Environment.TickCount64, WindowsNetworkEndpointProcessLease.Open,
            (configuration ?? PhysicalDiskIoCaptureConfiguration.Default).BucketWidthMilliseconds,
            (configuration ?? PhysicalDiskIoCaptureConfiguration.Default).MaximumActivePairs) { }
    public PhysicalDiskIoCaptureConfiguration Configuration => new(bucketWidthMilliseconds, maximumActivePairs);
    internal EtwPhysicalDiskIoCaptureOwner(IKernelEtwSessionBroker broker, KernelEtwDiagnostics diagnostics,
        Func<long> clock, Func<uint, IWindowsNetworkEndpointProcessLease?> openProcess,
        int bucketWidthMilliseconds = DefaultBucketWidthMilliseconds, int maximumActivePairs = MaximumActivePairs,
        Func<int, CaptureBucketMemory>? allocateBuckets = null)
    {
        if (bucketWidthMilliseconds <= 0) throw new ArgumentOutOfRangeException(nameof(bucketWidthMilliseconds));
        if (maximumActivePairs <= 0) throw new ArgumentOutOfRangeException(nameof(maximumActivePairs));
        this.broker = broker; this.diagnostics = diagnostics; this.clock = clock; this.openProcess = openProcess;
        this.bucketWidthMilliseconds = bucketWidthMilliseconds; this.maximumActivePairs = maximumActivePairs;
        bucketCount = checked((int)((WindowMilliseconds + bucketWidthMilliseconds - 1) / bucketWidthMilliseconds) + 2);
        this.allocateBuckets = allocateBuckets ?? CaptureBucketMemory.AllocateManaged;
    }

    public PhysicalDiskIoAttributionSnapshot Read(bool includeObservedTraffic = false)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        EnsureStarted();
        var currentSubscription = Volatile.Read(ref subscription);
        if (currentSubscription is null)
            return new(DateTimeOffset.UtcNow, new Dictionary<int, PhysicalDiskProcessAttribution>(),
                new("etw-fileio-disk", state, message));
        var now = DateTimeOffset.UtcNow;
        var tick = clock();
        var before = currentSubscription.GetSnapshot();
        RefreshBrokerState(before);
        var selected = SelectWindow(tick);
        var legacy = new Dictionary<int, PhysicalDiskProcessAttribution>();
        var verified = selected.Complete ? BuildObserved(selected.Events, now, selected.Generation, selected.ProcessRevision, out legacy) : null;
        var scopes = selected.Disks.Select(number => new PhysicalDiskIoScope(selected.Generation, number)).ToArray();
        var observed = !includeObservedTraffic ? null : verified is not null ? verified with { Disks = scopes }
            : new PhysicalDiskProcessIoWindow(now, WindowMilliseconds, []) { Complete = false, Disks = scopes };
        var after = (Volatile.Read(ref subscription)?.GetSnapshot() ?? broker.GetSnapshot());
        lock (eventGate)
        {
            // Process churn is not ETW loss. BuildObserved verifies each PID's
            // creation identity twice; an unrelated lifecycle event must not
            // discard all the other processes' completed disk transfers.
            if (!before.IsRunning || !after.IsRunning || before.Generation != after.Generation
                || before.EventsLost != after.EventsLost || after.Generation != generation || after.EventsLost != observedLoss
                || !ReferenceEquals(currentSubscription, Volatile.Read(ref subscription))
                || selected.StartedTick != startedTick || !selected.Complete)
            {
                legacy = new();
                observed = includeObservedTraffic && after.IsRunning && after.Generation == selected.Generation
                    ? new PhysicalDiskProcessIoWindow(now, WindowMilliseconds, []) { Complete = false, Disks = scopes } : null;
                state = after.IsRunning ? PhysicalDiskIoProviderStates.Warming : PhysicalDiskIoProviderStates.FromKernelEtwState(after.State);
                message = after.IsRunning ? "ETW DiskIO 采样窗口不完整。" : after.Message;
            }
            else { state = PhysicalDiskIoProviderStates.Running; message = "ETW DiskIO 正在采集磁盘传输完成事件。"; }
            return new(now, legacy, new("etw-fileio-disk", state, message)) { ObservedTraffic = observed };
        }
    }

    private void EnsureStarted()
    {
        lock (subscriptionGate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (subscription is not null) return;
            try
            {
                subscription = broker.Subscribe(new("physical-disk-io-attribution",
                    RequiredKeywords,
                    BindKernelEvents));
            }
            catch (Exception error) { state = PhysicalDiskIoProviderStates.Unavailable; message = error.Message; diagnostics.LogWarning(error, "Failed to start shared DiskIO source."); }
        }
    }

    internal void BindKernelEvents(KernelEtwEventSource source, KernelEtwSessionContext context)
    {
        lock (eventGate)
        {
            ClearTotals(); observedLoss = 0; generation = context.Generation;
            incompleteThroughTick = long.MinValue; startedTick = clock(); processRevision++;
        }
        source.DiskIORead += data => ObserveBoundDiskTransfer(source, context, data.ProcessID,
            unchecked((uint)data.TransferSize), false, clock(), data.DiskNumber < 0 ? null : (uint)data.DiskNumber, ToFileTime(data.TimeStamp));
        source.DiskIOWrite += data => ObserveBoundDiskTransfer(source, context, data.ProcessID,
            unchecked((uint)data.TransferSize), true, clock(), data.DiskNumber < 0 ? null : (uint)data.DiskNumber, ToFileTime(data.TimeStamp));
        source.ProcessStartGroup += data => ProcessIdentityChanged(source, context, data.ProcessID);
        source.ProcessEndGroup += data => ProcessIdentityChanged(source, context, data.ProcessID);
    }
    private void ProcessIdentityChanged(KernelEtwEventSource source, KernelEtwSessionContext context, int pid)
    { lock (eventGate) { if (source.IsActive && context.Generation == generation) InvalidateProcessCore(pid); } }
    private void InvalidateProcessCore(int pid) { processRevision++; processBirths.Remove(pid); }
    internal void ObserveProcessIdentityChange(int pid) { lock (eventGate) InvalidateProcessCore(pid); }

    private void RefreshBrokerState(KernelEtwSessionSnapshot snapshot)
    {
        lock (eventGate)
        {
            if (snapshot.IsRunning && snapshot.Generation == generation)
            {
                if (snapshot.EventsLost != observedLoss)
                { ClearTotals(); incompleteThroughTick = clock(); observedLoss = snapshot.EventsLost; }
            }
            else { state = PhysicalDiskIoProviderStates.FromKernelEtwState(snapshot.State); message = snapshot.Message; }
        }
    }
    internal void ObserveDiskTransfer(int pid, long bytes, bool write, long tick)
    { if (bytes > 0) ObserveDiskTransfer(pid, (ulong)bytes, write, tick, null, null); }
    internal void ObserveDiskTransfer(int pid, ulong bytes, bool write, long tick, uint? disk, ulong? eventTime)
    { if (bytes == 0) return; lock (eventGate) RecordTransfer(pid, bytes, write, tick, disk, eventTime); }
    internal void ObserveBoundDiskTransfer(KernelEtwEventSource source, KernelEtwSessionContext context,
        int pid, long bytes, bool write, long tick)
    { if (bytes > 0) ObserveBoundDiskTransfer(source, context, pid, (ulong)bytes, write, tick, null, null); }
    internal void ObserveBoundDiskTransfer(KernelEtwEventSource source, KernelEtwSessionContext context,
        int pid, ulong bytes, bool write, long tick, uint? disk, ulong? eventTime)
    {
        if (bytes == 0) return;
        lock (eventGate) { if (source.IsActive && context.Generation == generation) RecordTransfer(pid, bytes, write, tick, disk, eventTime); }
    }
    private void RecordTransfer(int pid, ulong bytes, bool write, long tick, uint? disk, ulong? eventTime)
    {
        try { Enqueue(pid, bytes, write, tick, disk, eventTime); }
        catch { incompleteThroughTick = Math.Max(incompleteThroughTick, tick); throw; }
    }
    private void Enqueue(int pid, ulong bytes, bool write, long tick, uint? disk, ulong? eventTime)
    {
        Prune(tick);
        if (disk is { } number)
        {
            if (!disks.ContainsKey(number) && disks.Count >= maximumActivePairs)
                incompleteThroughTick = Math.Max(incompleteThroughTick, tick);
            else disks[number] = Math.Max(disks.GetValueOrDefault(number, long.MinValue), tick);
        }
        pid = Math.Max(0, pid);
        if (!processBirths.TryGetValue(pid, out var birth))
        {
            // One identity read per active PID/lifecycle, never one handle per transfer.
            try
            {
                using var lease = pid > 0 ? openProcess((uint)pid) : null;
                birth = lease is not null && lease.ProcessId == (uint)pid && lease.TryReadCreationFileTime(out var value) && value > 0 ? value : null;
            }
            catch (Exception error) { diagnostics.LogDebug(error, "Could not identify DiskIO process at intake."); }
            // This is only an identity lookup cache. Eviction loses no captured totals.
            if (processBirths.Count >= maximumActivePairs) processBirths.Remove(processBirths.Keys.First());
            processBirths[pid] = birth;
        }
        // An unknown event timestamp makes that PID's selected attribution unavailable, as before.
        var key = new DiskPair(pid, disk, write, eventTime is null ? null : birth);
        if (birth is { } knownBirth && eventTime is { } timestamp && timestamp < knownBirth) return;
        if (!pairs.TryGetValue(key, out var totals))
        {
            if (pairs.Count >= maximumActivePairs)
            {
                var oldest = pairs.MinBy(pair => pair.Value.LastTick);
                incompleteThroughTick = Math.Max(incompleteThroughTick, oldest.Value.LastTick);
                oldest.Value.Memory.Dispose(); pairs.Remove(oldest.Key);
            }
            totals = new(allocateBuckets(checked(bucketCount * Marshal.SizeOf<DiskBucket>())));
            pairs.Add(key, totals);
        }
        var id = tick / bucketWidthMilliseconds;
        ref var bucket = ref totals.Cells[(int)(id % bucketCount)];
        if (bucket.Id != id || bucket.Count == 0) bucket = new() { Id = id, FirstTick = tick, LastTick = tick };
        bucket.FirstTick = Math.Min(bucket.FirstTick, tick); bucket.LastTick = Math.Max(bucket.LastTick, tick);
        try { bucket.Bytes = checked(bucket.Bytes + bytes); bucket.Count = checked(bucket.Count + 1); }
        catch (OverflowException) { bucket.Invalid = true; }
        totals.LastTick = Math.Max(totals.LastTick, tick);

    }
    internal int RetainedPairCount { get { lock (eventGate) return pairs.Count; } }
    internal long RetainedBucketBytes { get { lock (eventGate) return (long)pairs.Count * bucketCount * Marshal.SizeOf<DiskBucket>(); } }
    private void Prune(long now)
    {
        var bucket = now / bucketWidthMilliseconds;
        if (bucket <= lastPrunedBucket) return;
        lastPrunedBucket = bucket;
        foreach (var pair in pairs)
            if (pair.Value.LastTick < now - WindowMilliseconds - bucketWidthMilliseconds)
            { pair.Value.Memory.Dispose(); pairs.Remove(pair.Key); }
        foreach (var disk in disks)
            if (disk.Value < now - RetentionMilliseconds) disks.Remove(disk.Key);
        var activePids = new HashSet<int>();
        foreach (var key in pairs.Keys) activePids.Add(key.Pid);
        foreach (var pid in processBirths.Keys)
            if (!activePids.Contains(pid)) processBirths.Remove(pid);
        if (pairs.EnsureCapacity(0) > Math.Max(16, pairs.Count * 4)) pairs.TrimExcess();
        if (processBirths.EnsureCapacity(0) > Math.Max(16, processBirths.Count * 4)) processBirths.TrimExcess();
        if (disks.EnsureCapacity(0) > Math.Max(16, disks.Count * 4)) disks.TrimExcess();
    }
    private (DiskEvent[] Events, bool Complete, long Generation, long ProcessRevision, uint[] Disks, long StartedTick) SelectWindow(long now)
    {
        lock (eventGate)
        {
            Prune(now);
            var cutoff = now - WindowMilliseconds;
            var selected = new List<DiskEvent>(pairs.Count);
            foreach (var pair in pairs)
            {
                ulong bytes = 0; int count = 0; bool invalid = false;
                foreach (ref var bucket in pair.Value.Cells)
                {
                    // Keep exact byte integers. A partial edge bucket is omitted instead of prorating its traffic.
                    if (bucket.Count == 0 || bucket.FirstTick < cutoff || bucket.LastTick > now) continue;
                    try { bytes = checked(bytes + bucket.Bytes); count = checked(count + bucket.Count); }
                    catch (OverflowException) { invalid = true; }
                    invalid |= bucket.Invalid;
                }
                if (count != 0 || invalid) selected.Add(new(pair.Key.Pid, pair.Key.Disk, bytes, pair.Key.Write, pair.Key.Birth, count, invalid));
            }
            return (selected.ToArray(), incompleteThroughTick < cutoff && (startedTick == long.MinValue || now - startedTick >= WindowMilliseconds), generation, processRevision,
                disks.Where(pair => pair.Value <= now).Select(pair => pair.Key).Order().ToArray(), startedTick);
        }
    }
    private void ClearTotals()
    {
        foreach (var pair in pairs) { pair.Value.Memory.Dispose(); pairs.Remove(pair.Key); }
        pairs.TrimExcess(); disks.Clear(); disks.TrimExcess(); processBirths.Clear(); processBirths.TrimExcess(); lastPrunedBucket = long.MinValue;
    }
    internal (IReadOnlyDictionary<int, PhysicalDiskProcessAttribution> Processes, bool Complete) ReadWindow(long now)
    {
        var selected = SelectWindow(now);
        var legacy = new Dictionary<int, PhysicalDiskProcessAttribution>();
        if (selected.Complete) BuildObserved(selected.Events, DateTimeOffset.UtcNow, selected.Generation, selected.ProcessRevision, out legacy);
        return (legacy, selected.Complete);
    }

    private PhysicalDiskProcessIoWindow BuildObserved(DiskEvent[] selected, DateTimeOffset now, long session, long expectedProcessRevision,
        out Dictionary<int, PhysicalDiskProcessAttribution> legacy)
    {
        legacy = new();
        var rows = new List<PhysicalDiskProcessIoObservation>();
        foreach (var process in selected.GroupBy(static item => item.Pid))
        {
            var disks = process.Where(static item => item.Disk is not null).Select(static item => item.Disk!.Value).Distinct().Order().ToArray();
            IWindowsNetworkEndpointProcessLease? lease = null;
            var start = rows.Count;
            foreach (var disk in disks) rows.Add(new(new(session, disk), process.Key, null, null, null));
            try
            {
                if (process.Key <= 0 || (lease = openProcess((uint)process.Key)) is null || lease.ProcessId != (uint)process.Key
                    || !lease.TryReadCreationFileTime(out var birth) || birth == 0) continue;
                if (process.Any(item => item.Birth is null))
                {
                    // A transient intake identity failure must not poison this active PID forever.
                    // Verify the live handle twice, then identify FUTURE transfers only; never backfill unknown history.
                    if (lease.TryReadCreationFileTime(out var verifiedBirth) && verifiedBirth == birth)
                        lock (eventGate)
                            if (generation == session && processRevision == expectedProcessRevision
                                && processBirths.TryGetValue(process.Key, out var cached) && cached is null)
                                processBirths[process.Key] = birth;
                    continue;
                }
                if (process.Any(item => item.Birth == birth && item.Invalid)) continue;
                var current = process.Where(item => item.Birth == birth).ToArray();
                var contribution = new PhysicalDiskProcessAttribution(process.Key,
                    current.Where(static item => !item.Write).Sum(static item => (double)item.Bytes) / 2,
                    current.Where(static item => item.Write).Sum(static item => (double)item.Bytes) / 2,
                    current.Where(static item => !item.Write).Sum(static item => item.Count), current.Where(static item => item.Write).Sum(static item => item.Count))
                    { ProcessStartKey = birth };
                foreach (var disk in disks)
                {
                    ulong read = 0, write = 0;
                    foreach (var item in process.Where(item => item.Disk == disk && item.Birth == birth))
                        if (item.Write) write = checked(write + item.Bytes); else read = checked(read + item.Bytes);
                    var row = start + Array.IndexOf(disks, disk);
                    rows[row] = new(new(session, disk), process.Key, birth.ToString(CultureInfo.InvariantCulture),
                        read.ToString(CultureInfo.InvariantCulture), write.ToString(CultureInfo.InvariantCulture));
                }
                if (!lease.TryReadCreationFileTime(out var finalBirth) || finalBirth != birth)
                    for (var index = start; index < rows.Count; index++) rows[index] = rows[index] with { CreationWindowsFileTime100Ns = null, ReadBytes = null, WriteBytes = null };
                else legacy[process.Key] = contribution;
            }
            catch (Exception error)
            {
                for (var index = start; index < rows.Count; index++) rows[index] = rows[index] with { CreationWindowsFileTime100Ns = null, ReadBytes = null, WriteBytes = null };
                diagnostics.LogDebug(error, "Could not verify DiskIO process identity or exact byte sum.");
            }
            finally
            {
                try { lease?.Dispose(); }
                catch (Exception error) { diagnostics.LogWarning(error, "Could not release DiskIO process identity handle."); }
            }
        }
        return new(now, WindowMilliseconds, rows.ToArray());
    }
    private static ulong? ToFileTime(DateTime timestamp)
    { try { return checked((ulong)timestamp.ToUniversalTime().ToFileTimeUtc()); } catch (ArgumentOutOfRangeException) { return null; } catch (OverflowException) { return null; } }

    public void Stop(string idleMessage = "ETW DiskIO 物理磁盘归因已因无请求而停止。")
    {
        IKernelEtwSubscription? retired;
        lock (subscriptionGate) { retired = subscription; subscription = null; Volatile.Write(ref generation, 0); }
        retired?.Dispose();
        lock (subscriptionGate)
        {
            if (subscription is not null) return;
            lock (eventGate) { ClearTotals(); startedTick = long.MinValue; incompleteThroughTick = long.MinValue; processRevision++; }
            state = PhysicalDiskIoProviderStates.Idle; message = idleMessage;
        }
    }
    public void Dispose()
    {
        lock (subscriptionGate) { if (disposeCompleted) return; disposed = true; }
        Stop("ETW DiskIO 物理磁盘归因正在随程序停止。");
        lock (subscriptionGate) disposeCompleted = true;
    }
    private readonly record struct DiskEvent(int Pid, uint? Disk, ulong Bytes, bool Write, ulong? Birth, int Count, bool Invalid);
    private readonly record struct DiskPair(int Pid, uint? Disk, bool Write, ulong? Birth);
    private struct DiskBucket
    {
        internal long Id, FirstTick, LastTick;
        internal ulong Bytes;
        internal int Count;
        internal bool Invalid;
    }
    private sealed class DiskTotals(CaptureBucketMemory memory)
    {
        internal CaptureBucketMemory Memory { get; } = memory;
        internal long LastTick = long.MinValue;
        internal Span<DiskBucket> Cells => MemoryMarshal.Cast<byte, DiskBucket>(Memory.Bytes);
    }
}
