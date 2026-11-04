using System.Globalization;
using ResourceManager.ReadWrite.Native.Pdh;
using ResourceManager.ReadWrite.Windows.Processes;

namespace ResourceManager.ReadWrite.ProcessFields;

public readonly record struct ProcessGpuEngineScope(ulong AdapterLuid, uint PhysicalIndex, uint EngineIndex);
public sealed record ProcessGpuEngineInterval(long BeginWindowsFileTime100Ns, long EndWindowsFileTime100Ns,
    long BeginCounter, long EndCounter, long CounterFrequency, NativePdhEngineClass EngineClass)
{ public bool IsLastGood { get; init; } }

/// <summary>Projects original PDH formatted busy percent. Attribution requires the same raw
/// counter window and a PID creation check performed after the source read. No baseline is kept.</summary>
public static class WindowsPdhProcessGpuFields
{
    public const string FieldName = "process_all_gpu_residency_attributed";
    private static readonly WindowsProcessBasicMetricReader BasicMetricReader = new();
    public static ProcessMetricFieldSnapshot Read(INativePdhSnapshotSource source,
        ProcessMetricFieldSnapshot inventory, ulong? topologyFingerprint = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        var read = source.Read(topologyFingerprint);
        return Project(read, inventory, ReadCurrentCreation);
    }
    internal static ProcessMetricFieldSnapshot Project(NativePdhReadResult read,
        ProcessMetricFieldSnapshot inventory, Func<int, long?> readCurrentCreation)
    {
        var definition = ProcessMetricFieldRegistry.Find(FieldName)!;
        var snapshot = read.Snapshot;
        if (snapshot is null) return new(null, null, [definition], []);
        var identities = inventory.Processes.Where(static row => row.PhysicalDisk is null && row.GpuEngine is null)
            .GroupBy(static row => row.ProcessId).Where(static group => group.Count() == 1)
            .Select(static group => group.Single()).Where(static row => row.ProcessId > 0)
            .ToDictionary(static row => row.ProcessId);
        var validated = new Dictionary<int, long>();
        foreach (var pair in identities)
            if (long.TryParse(pair.Value.CreationWindowsFileTime100Ns, NumberStyles.None, CultureInfo.InvariantCulture, out var birth)
                && birth > 0 && readCurrentCreation(pair.Key) == birth) validated.Add(pair.Key, birth);
        var windows = snapshot.EngineWindows.GroupBy(static window =>
            (window.AdapterLuid, window.ProcessId, window.PhysicalIndex, window.EngineIndex))
            .Where(static group => group.Count() == 1).ToDictionary(static group => group.Key, static group => group.Single());
        var rows = new List<ProcessMetricFieldValues>();
        foreach (var group in snapshot.EngineRows.GroupBy(static row =>
            (row.AdapterLuid, row.ProcessId, row.PhysicalIndex, row.EngineIndex)))
        {
            // Ambiguous rows cannot identify one original engine counter instance.
            if (group.Count() != 1 || group.Key.ProcessId is 0 or > int.MaxValue) continue;
            var row = group.Single();
            if (!validated.TryGetValue((int)row.ProcessId, out var birth)) continue;
            double? value = null;
            ProcessGpuEngineInterval? interval = null;
            if (snapshot.GpuEngineObservation.Availability is NativePdhProviderAvailability.Available or NativePdhProviderAvailability.LastGood
                && windows.TryGetValue(group.Key, out var window)
                && birth <= window.BeginFileTime && window.BeginFileTime > 0 && window.EndFileTime > window.BeginFileTime
                && window.EndFileTime <= 2650467743999999999L
                && window.BeginCounter > 0 && window.EndCounter > window.BeginCounter && window.CounterFrequency > 0
                && double.IsFinite(row.UsagePercent))
            {
                // Native PDH still owns the original formatted-percent calculation.
                value = Math.Clamp(row.UsagePercent, 0, 100);
                interval = new(window.BeginFileTime, window.EndFileTime, window.BeginCounter, window.EndCounter,
                    window.CounterFrequency, row.EngineClass)
                { IsLastGood = snapshot.GpuEngineObservation.Availability == NativePdhProviderAvailability.LastGood };
            }
            rows.Add(new((int)row.ProcessId, birth.ToString(CultureInfo.InvariantCulture), [new(FieldName, null, value)])
            { GpuEngine = new(row.AdapterLuid, row.PhysicalIndex, row.EngineIndex), GpuInterval = interval });
        }
        var capturedAt = rows.Where(static row => row.GpuInterval is not null)
            .Select(static row => (DateTimeOffset?)DateTimeOffset.FromFileTime(row.GpuInterval!.EndWindowsFileTime100Ns))
            .DefaultIfEmpty(null).Max();
        return new(capturedAt, null, [definition], rows)
        { Sources = [new("windows.pdh.gpu-engine", capturedAt, null)] };
    }
    private static long? ReadCurrentCreation(int pid)
        => BasicMetricReader.ReadCurrentCreationFileTime(pid);
}
