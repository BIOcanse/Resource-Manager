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
        catch { return new(SamplingObservationStatus.Unavailable, 0, 0, 0, [], now, new(now, null, new Dictionary<ProcessInstanceKey, double>())); }
        using var inventoryLease = inventory;
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
            if (!captured) { skipped++; continue; }
            if (key is { } identity) live.Add(identity);
            if (includeCpu && hasCpu(sample) && key is { } cpuIdentity) cpu[cpuIdentity] = time;
            samples.Add(sample);
        }
        BasicReader.PruneCommandLineFailures(live);
        afterCapture?.Invoke(samples, live);
        var interval = tracker.UpdateInterval(includeCpu, now, cpu, Environment.ProcessorCount);
        return new(SamplingObservationStatus.Current, checked((uint)processes.Length), excluded, skipped, samples, now, interval)
        { HasUnidentifiedProcesses = unidentified };
    }
    internal sealed record CapturedProcessBasic(long? Creation, ProcessBasicMetricObservation Basic);
    internal IReadOnlyDictionary<WindowsProcessInventoryEntry, CapturedProcessBasic> ReadBasicBatch(WindowsProcessInventoryEntry[] processes,
        Func<WindowsProcessInventoryEntry, long?, ProcessMetricReadMask> selectMask, ProcessInstanceTextSharing? textSharing = null)
    {
        var selected = new List<(WindowsProcessInventoryEntry Process, long? Creation, ProcessBasicMetricRequest Request)>();
        foreach (var process in processes)
        {
            try
            {
                if (process.Id <= 0) continue;
                var creation = process.Creation.Creation;
                var requestMask = selectMask(process, creation);
                if (process.Source is null && process.Creation.HandleCount is not null) requestMask &= ~ProcessMetricReadMask.HandleCount;
                selected.Add((process, creation, new(process.Id, creation ?? 0, requestMask)));
            }
            catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
        }
        var threadCounts = selected.Any(static row => row.Request.Mask.HasFlag(ProcessMetricReadMask.ThreadCount))
            && processes.All(static process => process.Source is null)
            ? processes.Where(static process => process.Creation.Creation is > 0)
                .ToDictionary(static process => new ProcessInstanceKey(process.Id, process.Creation.Creation!.Value), static process => process.ThreadCount)
            : null;
        var readings = BasicReader.ReadBatch(selected.Select(row => row.Request).ToArray(), threadCounts);
        return selected.Select((row, index) =>
        {
            var basic = readings[index];
            if (row.Process.Source is null && row.Process.Creation.HandleCount is { } nativeHandles) basic = basic with { HandleCount = nativeHandles };
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
            var basic = prepared is null ? BasicReader.Read(pid, creation ?? 0, process.Source is null && creationRead.HandleCount is not null ? mask & ~ProcessMetricReadMask.HandleCount : mask)
                : captured?.Basic ?? ProcessBasicMetricObservation.Unavailable;
            if (process.Source is null && mask.HasFlag(ProcessMetricReadMask.HandleCount) && creationRead.HandleCount is { } nativeHandles)
                basic = basic with { HandleCount = nativeHandles };
            if (key is { } textKey && textSharing is not null) basic = textSharing.Share(textKey, basic);
            var hasCpu = includeCpu && basic.CpuTime100Ns is { } ticks && ticks <= (UInt128)long.MaxValue;
            cpu = hasCpu ? TimeSpan.FromTicks((long)basic.CpuTime100Ns!.Value) : TimeSpan.Zero;
            sample = new(pid, creation, basic, hasCpu);
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { return false; }
    }
}
