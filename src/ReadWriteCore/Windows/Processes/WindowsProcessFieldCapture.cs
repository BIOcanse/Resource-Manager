using System.Diagnostics;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Infrastructure.ResourceBreakdown;
using ResourceManager.ReadWrite.ProcessFields;

namespace ResourceManager.ReadWrite.Windows.Processes;

internal delegate bool TryCaptureProcess<T>(Process process, out T sample, out TimeSpan cpu, out ProcessInstanceKey? identity);
internal sealed record ProcessCaptureBatch<T>(SamplingObservationStatus Status, uint EnumeratedCount,
    uint ExcludedCount, uint SkippedCount, IReadOnlyList<T> Samples, DateTimeOffset CapturedAt,
    ProcessCpuDeltaTracker.IntervalResult Cpu);
internal sealed record ProcessFieldSample(int ProcessId, long? Creation, ProcessBasicMetricObservation Basic,
    bool HasProcessorTime);

public sealed partial class WindowsProcessFieldOwner
{
    internal IWindowsProcessInventoryReader Inventory { get; set; } = WindowsProcessInventoryReader.Instance;
    internal WindowsProcessBasicMetricReader BasicReader { get; set; } = new();
    internal ProcessCpuDeltaTracker HostedCpu { get; } = new();
    internal ProcessCpuDeltaTracker DirectCpu { get; } = new();
    internal Func<DateTimeOffset> CaptureClock { get; set; } = static () => DateTimeOffset.UtcNow;
    internal ProcessCaptureBatch<T> CaptureBatch<T>(bool includeCpu, ProcessCpuDeltaTracker tracker,
        TryCaptureProcess<T> capture, Func<T, bool> hasCpu,
        Action<List<T>, HashSet<ProcessInstanceKey>>? afterCapture = null, DateTimeOffset? captureStart = null,
        Action<Process[]>? prepare = null, int maximumProcessCount = int.MaxValue)
    {
        if (maximumProcessCount <= 0) throw new ArgumentOutOfRangeException(nameof(maximumProcessCount));
        var now = captureStart ?? CaptureClock();
        var cpu = new Dictionary<ProcessInstanceKey, TimeSpan>();
        var samples = new List<T>();
        var live = new HashSet<ProcessInstanceKey>();
        Process[] processes;
        try { processes = Inventory.GetProcesses(); }
        catch { return new(SamplingObservationStatus.Unavailable, 0, 0, 0, [], now, new(now, null, new Dictionary<ProcessInstanceKey, double>())); }
        if (processes.Length > maximumProcessCount)
        {
            foreach (var process in processes)
                try { process.Dispose(); } catch { }
            var enumerated = checked((uint)processes.Length);
            return new(SamplingObservationStatus.Unavailable, enumerated, 0, enumerated, [], now,
                new(now, null, new Dictionary<ProcessInstanceKey, double>()));
        }
        uint excluded = 0, skipped = 0;
        try
        {
            prepare?.Invoke(processes);
            foreach (var process in processes)
            {
                using var release = prepare is null ? process : null;
                if (process.Id <= 0) { excluded++; continue; }
                if (!capture(process, out var sample, out var time, out var key)) { skipped++; continue; }
                if (key is { } identity) live.Add(identity);
                if (includeCpu && hasCpu(sample) && key is { } cpuIdentity) cpu[cpuIdentity] = time;
                samples.Add(sample);
            }
        }
        finally
        {
            if (prepare is not null) foreach (var process in processes) process.Dispose();
        }
        afterCapture?.Invoke(samples, live);
        var interval = tracker.UpdateInterval(includeCpu, now, cpu, Environment.ProcessorCount);
        return new(SamplingObservationStatus.Current, checked((uint)processes.Length), excluded, skipped, samples, now, interval);
    }
    internal sealed record CapturedProcessBasic(long? Creation, ProcessBasicMetricObservation Basic);
    internal IReadOnlyDictionary<Process, CapturedProcessBasic> ReadBasicBatch(Process[] processes,
        Func<Process, long?, ProcessMetricReadMask> selectMask)
    {
        var selected = new List<(Process Process, long? Creation, ProcessBasicMetricRequest Request)>();
        foreach (var process in processes)
        {
            try
            {
                if (process.Id <= 0) continue;
                var creation = ReadCreation(process);
                selected.Add((process, creation, new(process.Id, creation ?? 0, selectMask(process, creation))));
            }
            catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
        }
        var readings = BasicReader.ReadBatch(selected.Select(row => row.Request).ToArray());
        return selected.Select((row, index) => (row.Process, Value: new CapturedProcessBasic(row.Creation, readings[index])))
            .ToDictionary(row => row.Process, row => row.Value);
    }
    internal static long? ReadCreation(Process process)
    {
        try { return process.StartTime.ToFileTimeUtc(); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { return null; }
    }
    private bool CaptureFieldSample(Process process, ProcessMetricReadMask mask, bool includeCpu,
        out ProcessFieldSample sample, out TimeSpan cpu, out ProcessInstanceKey? key,
        IReadOnlyDictionary<Process, CapturedProcessBasic>? prepared = null)
    {
        sample = null!; cpu = default; key = null;
        try
        {
            var pid = process.Id;
            var captured = prepared?.GetValueOrDefault(process);
            var creation = captured is null ? ReadCreation(process) : captured.Creation;
            key = creation is { } birth ? new(pid, birth) : null;
            var basic = prepared is null ? BasicReader.Read(pid, creation ?? 0, mask)
                : captured?.Basic ?? ProcessBasicMetricObservation.Unavailable;
            var hasCpu = includeCpu && basic.CpuTime100Ns is { } ticks && ticks <= (UInt128)long.MaxValue;
            cpu = hasCpu ? TimeSpan.FromTicks((long)basic.CpuTime100Ns!.Value) : TimeSpan.Zero;
            sample = new(pid, creation, basic, hasCpu);
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { return false; }
    }
}
