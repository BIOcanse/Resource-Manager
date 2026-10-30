using System.Diagnostics;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Infrastructure.ResourceBreakdown;
using ResourceManager.ReadWrite.ProcessFields;

namespace ResourceManager.ReadWrite.Windows.Processes;

internal delegate bool TryCaptureProcess<T>(WindowsProcessInventoryEntry process, ProcessCreationRead creation, out T sample, out TimeSpan cpu, out ProcessInstanceKey? identity);
internal sealed record ProcessCaptureBatch<T>(SamplingObservationStatus Status, uint EnumeratedCount,
    uint ExcludedCount, uint SkippedCount, IReadOnlyList<T> Samples, DateTimeOffset CapturedAt,
    ProcessCpuDeltaTracker.IntervalResult Cpu)
{
    internal bool HasUnidentifiedProcesses { get; init; }
}
internal sealed record ProcessFieldSample(int ProcessId, long? Creation, ProcessBasicMetricObservation Basic,
    bool HasProcessorTime);

public sealed partial class WindowsProcessFieldOwner
{
    private readonly WindowsProcessInventoryBuffer inventoryBuffer = new();
    internal int InventoryBufferCapacityBytes => inventoryBuffer.CapacityBytes;
    /// <summary>The inventory workspace's return policy (memory/CPU tier option bufferReturn).</summary>
    internal Func<WindowsProcessInventoryBufferReturn> InventoryBufferReturn
    {
        get => inventoryBuffer.ReturnPolicy;
        set => inventoryBuffer.ReturnPolicy = value ?? throw new ArgumentNullException(nameof(value));
    }
    internal async Task ReleaseProcessInventoryBufferAsync(CancellationToken cancellationToken)
    {
        await CaptureGate.WaitAsync(cancellationToken);
        try { inventoryBuffer.Release(); }
        finally { CaptureGate.Release(); }
    }
    internal IWindowsProcessInventoryReader Inventory { get; set; } = WindowsProcessInventoryReader.Instance;
    internal WindowsProcessBasicMetricReader BasicReader { get; set; } = new();
    internal ProcessCpuDeltaTracker HostedCpu { get; } = new();
    internal ProcessCpuDeltaTracker DirectCpu { get; } = new();
    internal Func<DateTimeOffset> CaptureClock { get; set; } = static () => DateTimeOffset.UtcNow;
    internal ProcessCaptureBatch<T> CaptureBatch<T>(bool includeCpu, ProcessCpuDeltaTracker tracker,
        TryCaptureProcess<T> capture, Func<T, bool> hasCpu,
        Action<List<T>, HashSet<ProcessInstanceKey>>? afterCapture = null, DateTimeOffset? captureStart = null,
        Action<WindowsProcessInventoryEntry[]>? prepare = null, int maximumProcessCount = int.MaxValue)
    {
        if (maximumProcessCount <= 0) throw new ArgumentOutOfRangeException(nameof(maximumProcessCount));
        var now = captureStart ?? CaptureClock();
        var cpu = new Dictionary<ProcessInstanceKey, TimeSpan>();
        var samples = new List<T>();
        var live = new HashSet<ProcessInstanceKey>();
        WindowsProcessInventoryCapture inventory;
        try { inventory = Inventory.Capture(inventoryBuffer); }
        catch { inventoryBuffer.ReturnAfterUse(); return new(SamplingObservationStatus.Unavailable, 0, 0, 0, [], now, new(now, null, new Dictionary<ProcessInstanceKey, double>())); }
        using var inventoryLease = inventory;
        // Capture parsed the kernel table into the inventory, so the workspace can be returned now.
        inventoryBuffer.ReturnAfterUse();
        var processes = inventory.Processes;
        if (processes.Length > maximumProcessCount)
        {
            var enumerated = checked((uint)processes.Length);
            return new(SamplingObservationStatus.Unavailable, enumerated, 0, enumerated, [], now,
                new(now, null, new Dictionary<ProcessInstanceKey, double>()));
        }
        uint excluded = 0, skipped = 0;
        var unidentified = false;
        prepare?.Invoke(processes);
        foreach (var process in processes)
        {
            if (process.Id <= 0) { excluded++; continue; }
            var creation = process.Creation;
            if (creation.Status == ProcessReadStatus.Exited) { excluded++; continue; }
            var captured = capture(process, creation, out var sample, out var time, out var key);
            if (key is null)
            {
                // Only a proven exit may be absent from a complete inventory.
                // A live identity failure must remain visible to its owner.
                unidentified = true;
            }
            if (key is { } identity) live.Add(identity);
            if (!captured) { skipped++; continue; }
            if (includeCpu && hasCpu(sample) && key is { } cpuIdentity) cpu[cpuIdentity] = time;
            samples.Add(sample);
        }
        PruneFixedFields(live);
        afterCapture?.Invoke(samples, live);
        var interval = tracker.UpdateInterval(includeCpu, now, cpu, Environment.ProcessorCount);
        return new(SamplingObservationStatus.Current, checked((uint)processes.Length), excluded, skipped, samples, now, interval)
        { HasUnidentifiedProcesses = unidentified };
    }
    internal sealed record CapturedProcessBasic(long? Creation, ProcessBasicMetricObservation Basic);
    /// <summary>Reads a whole inventory with one handle batch. Kernel-table entries take their dynamic categories
    /// from their own records, so the batch opens handles only for their unattempted fixed facts; entries without
    /// table counters read every requested category, thread counts included, through the batch.</summary>
    [RuntimeReadCache]
    internal IReadOnlyDictionary<WindowsProcessInventoryEntry, CapturedProcessBasic> ReadBasicBatch(WindowsProcessInventoryEntry[] processes,
        Func<WindowsProcessInventoryEntry, long?, ProcessMetricReadMask> selectMask, ProcessInstanceTextSharing? textSharing = null)
    {
        var selected = new List<(WindowsProcessInventoryEntry Process, long? Creation, ProcessMetricReadMask Requested, ProcessBasicMetricRequest Request)>();
        foreach (var process in processes)
        {
            try
            {
                if (process.Id <= 0) continue;
                var creation = process.Creation.Creation;
                var requestMask = selectMask(process, creation);
                var handleMask = process.Counters is null ? requestMask : requestMask & ~WindowsProcessTableCounters.Fields;
                selected.Add((process, creation, requestMask, new(process.Id, creation ?? 0, SelectUncachedFields(process.Id, creation ?? 0, handleMask))));
            }
            catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
        }
        var readings = BasicReader.ReadBatch(selected.Select(row => row.Request).ToArray());
        return selected.Select((row, index) =>
        {
            var basic = ReuseFixedFields(row.Process.Id, row.Creation ?? 0, row.Requested, row.Request.Mask, readings[index]);
            if (row.Process.Counters is { } counters) basic = counters.Fill(row.Requested, basic);
            if (row.Creation is { } birth && textSharing is not null) basic = textSharing.Share(new(row.Process.Id, birth), basic);
            return (row.Process, Value: new CapturedProcessBasic(row.Creation, basic));
        })
            .ToDictionary(row => row.Process, row => row.Value);
    }
    internal static ProcessCreationRead ReadCreation(Process process)
        => WindowsProcessCreationQuery.Read(process.Id);
    private bool CaptureFieldSample(WindowsProcessInventoryEntry process, ProcessCreationRead creationRead, ProcessMetricReadMask mask, bool includeCpu,
        out ProcessFieldSample sample, out TimeSpan cpu, out ProcessInstanceKey? key,
        IReadOnlyDictionary<WindowsProcessInventoryEntry, CapturedProcessBasic>? prepared = null, ProcessInstanceTextSharing? textSharing = null)
    {
        sample = null!; cpu = default; key = null;
        try
        {
            var pid = process.Id;
            var captured = prepared?.GetValueOrDefault(process);
            var creation = captured is null ? creationRead.Creation : captured.Creation;
            key = creation is { } birth ? new(pid, birth) : null;
            var basic = prepared is null ? ReadBasic(process, mask) : captured?.Basic ?? ProcessBasicMetricObservation.Unavailable;
            if (key is { } textKey && textSharing is not null) basic = textSharing.Share(textKey, basic);
            var hasCpu = includeCpu && basic.CpuTime100Ns is { } ticks && ticks <= (UInt128)long.MaxValue;
            cpu = hasCpu ? TimeSpan.FromTicks((long)basic.CpuTime100Ns!.Value) : TimeSpan.Zero;
            sample = new(pid, creation, basic, hasCpu);
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { return false; }
    }
}
