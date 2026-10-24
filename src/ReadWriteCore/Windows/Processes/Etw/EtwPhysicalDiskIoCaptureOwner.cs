using System.Globalization;
using Microsoft.Diagnostics.Tracing.Parsers;
using ResourceManager.App.Application.ResourceBreakdown;
using ResourceManager.App.Infrastructure.Telemetry.Etw;
using ResourceManager.ReadWrite.Windows.Devices;

namespace ResourceManager.ReadWrite.Windows.Processes;

/// <summary>The original DiskIO subscription and queue, extracted from the Host reader.
/// No worker, second window or physical-device resolver is installed.</summary>
public sealed class EtwPhysicalDiskIoCaptureOwner : IDisposable
{
    private const KernelTraceEventParser.Keywords RequiredKeywords = KernelTraceEventParser.Keywords.DiskIO | KernelTraceEventParser.Keywords.DiskIOInit | KernelTraceEventParser.Keywords.Process;

    private const long WindowMilliseconds = 2000;
    private const long RetentionMilliseconds = 8000;
    internal const int MaximumTrackedEvents = 131072;
    private readonly IKernelEtwSessionBroker broker;
    private readonly KernelEtwDiagnostics diagnostics;
    private readonly Func<long> clock;
    private readonly Func<uint, IWindowsNetworkEndpointProcessLease?> openProcess;
    private readonly object subscriptionGate = new();
    private readonly object eventGate = new();
    private readonly Queue<DiskEvent> events = new();
    private IKernelEtwSubscription? subscription;
    private long generation;
    private long incompleteThroughTick = long.MinValue;
    private long startedTick = long.MinValue;
    private long processRevision;
    private int observedLoss;
    private bool disposed;
    private volatile string state = "Idle";
    private volatile string message = "ETW DiskIO 物理磁盘归因按需启动。";
    public string State => state;
    public string Message => message;
    public bool IsRunning { get { lock (subscriptionGate) return subscription is not null && (Volatile.Read(ref subscription)?.GetSnapshot() ?? broker.GetSnapshot()) is { IsRunning: true } snapshot && snapshot.Generation == Volatile.Read(ref generation); } }

    public EtwPhysicalDiskIoCaptureOwner(IKernelEtwSessionBroker broker, KernelEtwDiagnostics? diagnostics = null)
        : this(broker, diagnostics ?? new(), static () => Environment.TickCount64, WindowsNetworkEndpointProcessLease.Open) { }
    internal EtwPhysicalDiskIoCaptureOwner(IKernelEtwSessionBroker broker, KernelEtwDiagnostics diagnostics,
        Func<long> clock, Func<uint, IWindowsNetworkEndpointProcessLease?> openProcess)
    { this.broker = broker; this.diagnostics = diagnostics; this.clock = clock; this.openProcess = openProcess; }

    public PhysicalDiskIoAttributionSnapshot Read(bool includeObservedTraffic = false)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        EnsureStarted();
        var now = DateTimeOffset.UtcNow;
        var tick = clock();
        var before = (Volatile.Read(ref subscription)?.GetSnapshot() ?? broker.GetSnapshot());
        RefreshBrokerState(before);
        var selected = SelectWindow(tick);
        var legacy = selected.Complete ? AggregateLegacy(selected.Events) : new Dictionary<int, PhysicalDiskProcessAttribution>();
        var scopes = selected.Disks.Select(number => new PhysicalDiskIoScope(selected.Generation, number)).ToArray();
        var observed = !includeObservedTraffic ? null : selected.Complete ? BuildObserved(selected.Events, now, selected.Generation) with { Disks = scopes }
            : new PhysicalDiskProcessIoWindow(now, WindowMilliseconds, []) { Complete = false, Disks = scopes };
        var after = (Volatile.Read(ref subscription)?.GetSnapshot() ?? broker.GetSnapshot());
        lock (eventGate)
        {
            if (!before.IsRunning || !after.IsRunning || before.Generation != after.Generation
                || before.EventsLost != after.EventsLost || after.Generation != generation || after.EventsLost != observedLoss
                || selected.ProcessRevision != processRevision || !selected.Complete)
            {
                legacy = new();
                observed = includeObservedTraffic && after.IsRunning && after.Generation == selected.Generation
                    ? new PhysicalDiskProcessIoWindow(now, WindowMilliseconds, []) { Complete = false, Disks = scopes } : null;
                state = after.IsRunning ? "Warming" : after.State;
                message = after.IsRunning ? "ETW DiskIO 采样窗口不完整。" : after.Message;
            }
            else { state = "Running"; message = "ETW DiskIO 正在采集磁盘传输完成事件。"; }
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
            catch (Exception error) { state = "Unavailable"; message = error.Message; diagnostics.LogWarning(error, "Failed to start shared DiskIO source."); }
        }
    }

    internal void BindKernelEvents(KernelEtwEventSource source, KernelEtwSessionContext context)
    {
        lock (eventGate)
        {
            events.Clear(); observedLoss = 0; generation = context.Generation;
            incompleteThroughTick = long.MinValue; startedTick = clock(); processRevision++;
        }
        source.DiskIORead += data => ObserveBoundDiskTransfer(source, context, data.ProcessID,
            unchecked((uint)data.TransferSize), false, clock(), data.DiskNumber < 0 ? null : (uint)data.DiskNumber, ToFileTime(data.TimeStamp));
        source.DiskIOWrite += data => ObserveBoundDiskTransfer(source, context, data.ProcessID,
            unchecked((uint)data.TransferSize), true, clock(), data.DiskNumber < 0 ? null : (uint)data.DiskNumber, ToFileTime(data.TimeStamp));
        source.ProcessStartGroup += _ => ProcessIdentityChanged(source, context);
        source.ProcessEndGroup += _ => ProcessIdentityChanged(source, context);
    }
    private void ProcessIdentityChanged(KernelEtwEventSource source, KernelEtwSessionContext context)
    { lock (eventGate) { if (source.IsActive && context.Generation == generation) processRevision++; } }

    private void RefreshBrokerState(KernelEtwSessionSnapshot snapshot)
    {
        lock (eventGate)
        {
            if (snapshot.IsRunning && snapshot.Generation == generation)
            {
                if (snapshot.EventsLost != observedLoss)
                { events.Clear(); incompleteThroughTick = clock(); observedLoss = snapshot.EventsLost; }
            }
            else { state = snapshot.State; message = snapshot.Message; }
        }
    }
    internal void ObserveDiskTransfer(int pid, long bytes, bool write, long tick)
    { if (bytes > 0) ObserveDiskTransfer(pid, (ulong)bytes, write, tick, null, null); }
    internal void ObserveDiskTransfer(int pid, ulong bytes, bool write, long tick, uint? disk, ulong? eventTime)
    { if (bytes == 0) return; lock (eventGate) Enqueue(pid, bytes, write, tick, disk, eventTime); }
    internal void ObserveBoundDiskTransfer(KernelEtwEventSource source, KernelEtwSessionContext context,
        int pid, long bytes, bool write, long tick)
    { if (bytes > 0) ObserveBoundDiskTransfer(source, context, pid, (ulong)bytes, write, tick, null, null); }
    internal void ObserveBoundDiskTransfer(KernelEtwEventSource source, KernelEtwSessionContext context,
        int pid, ulong bytes, bool write, long tick, uint? disk, ulong? eventTime)
    {
        if (bytes == 0) return;
        lock (eventGate) { if (source.IsActive && context.Generation == generation) Enqueue(pid, bytes, write, tick, disk, eventTime); }
    }
    private void Enqueue(int pid, ulong bytes, bool write, long tick, uint? disk, ulong? eventTime)
    {
        if (events.Count >= MaximumTrackedEvents) incompleteThroughTick = Math.Max(incompleteThroughTick, events.Dequeue().Tick);
        events.Enqueue(new(tick, Math.Max(0, pid), disk, bytes, write, eventTime));
    }
    internal int RetainedEventCount { get { lock (eventGate) return events.Count; } }
    private (DiskEvent[] Events, bool Complete, long Generation, long ProcessRevision, uint[] Disks) SelectWindow(long now)
    {
        lock (eventGate)
        {
            while (events.TryPeek(out var oldest) && oldest.Tick < now - RetentionMilliseconds) events.Dequeue();
            var cutoff = now - WindowMilliseconds;
            // Pure legacy recorded seams have no subscription start; bound production always does.
            var selected = events.Where(item => item.Tick >= cutoff && item.Tick <= now).ToArray();
            return (selected, incompleteThroughTick < cutoff && (startedTick == long.MinValue || now - startedTick >= WindowMilliseconds), generation, processRevision,
                events.Where(item => item.Disk is not null && item.Tick <= now).Select(static item => item.Disk!.Value).Distinct().Order().ToArray());
        }
    }
    internal (IReadOnlyDictionary<int, PhysicalDiskProcessAttribution> Processes, bool Complete) ReadWindow(long now)
    { var selected = SelectWindow(now); return (selected.Complete ? AggregateLegacy(selected.Events) : new Dictionary<int, PhysicalDiskProcessAttribution>(), selected.Complete); }

    private static Dictionary<int, PhysicalDiskProcessAttribution> AggregateLegacy(DiskEvent[] selected)
        => selected.GroupBy(static item => item.Pid).ToDictionary(static group => group.Key, static group =>
            new PhysicalDiskProcessAttribution(group.Key, group.Where(static item => !item.Write).Sum(static item => (double)item.Bytes) / 2,
                group.Where(static item => item.Write).Sum(static item => (double)item.Bytes) / 2,
                group.Count(static item => !item.Write), group.Count(static item => item.Write)));

    private PhysicalDiskProcessIoWindow BuildObserved(DiskEvent[] selected, DateTimeOffset now, long session)
    {
        var rows = new List<PhysicalDiskProcessIoObservation>();
        foreach (var process in selected.Where(static item => item.Disk is not null).GroupBy(static item => item.Pid))
        {
            var disks = process.Select(static item => item.Disk!.Value).Distinct().Order().ToArray();
            IWindowsNetworkEndpointProcessLease? lease = null;
            var start = rows.Count;
            foreach (var disk in disks) rows.Add(new(new(session, disk), process.Key, null, null, null));
            try
            {
                if (process.Key <= 0 || (lease = openProcess((uint)process.Key)) is null || lease.ProcessId != (uint)process.Key
                    || !lease.TryReadCreationFileTime(out var birth) || birth == 0 || process.Any(static item => item.EventTime is null)) continue;
                foreach (var disk in disks)
                {
                    ulong read = 0, write = 0;
                    foreach (var item in process.Where(item => item.Disk == disk && item.EventTime is { } eventTime && eventTime >= birth))
                        if (item.Write) write = checked(write + item.Bytes); else read = checked(read + item.Bytes);
                    var row = start + Array.IndexOf(disks, disk);
                    rows[row] = new(new(session, disk), process.Key, birth.ToString(CultureInfo.InvariantCulture),
                        read.ToString(CultureInfo.InvariantCulture), write.ToString(CultureInfo.InvariantCulture));
                }
                if (!lease.TryReadCreationFileTime(out var finalBirth) || finalBirth != birth)
                    for (var index = start; index < rows.Count; index++) rows[index] = rows[index] with { CreationWindowsFileTime100Ns = null, ReadBytes = null, WriteBytes = null };
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
            lock (eventGate) { events.Clear(); startedTick = long.MinValue; incompleteThroughTick = long.MinValue; processRevision++; }
            state = "Idle"; message = idleMessage;
        }
    }
    public void Dispose()
    { lock (subscriptionGate) { if (disposed) return; disposed = true; } Stop("ETW DiskIO 物理磁盘归因正在随程序停止。"); }
    private readonly record struct DiskEvent(long Tick, int Pid, uint? Disk, ulong Bytes, bool Write, ulong? EventTime);
}
