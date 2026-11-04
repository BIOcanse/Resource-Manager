namespace ResourceManager.ReadWrite.Native.Pdh;

public readonly record struct NativePdhGpuEngineKey(ulong AdapterLuid, uint PhysicalIndex, uint EngineIndex);

public readonly record struct NativePdhGpuEngineIdentity(
    ulong AdapterLuid,
    uint PhysicalIndex,
    uint EngineIndex,
    NativePdhEngineClass EngineClass);

public static class NativePdhGpuUsage
{
    public static NativePdhGpuEngineKey EngineOf(NativePdhGpuEngineRow row)
        => new(row.AdapterLuid, row.PhysicalIndex, row.EngineIndex);

    public static NativePdhGpuEngineIdentity ExactEngineOf(NativePdhGpuEngineRow row)
        => new(row.AdapterLuid, row.PhysicalIndex, row.EngineIndex, row.EngineClass);

    public static Dictionary<NativePdhGpuEngineIdentity, double> ByExactEngine(
        IEnumerable<NativePdhGpuEngineRow> rows)
        => rows.Where(static row => double.IsFinite(row.UsagePercent))
            .GroupBy(ExactEngineOf)
            .ToDictionary(
                static group => group.Key,
                static group => Math.Clamp(group.Sum(row => Sanitize(row.UsagePercent)), 0, 100));

    public static Dictionary<NativePdhGpuEngineKey, double> ByEngine(IEnumerable<NativePdhGpuEngineRow> rows)
        => rows.GroupBy(EngineOf).ToDictionary(group => group.Key,
            group => Math.Clamp(group.Sum(row => Sanitize(row.UsagePercent)), 0, 100));

    public static Dictionary<ulong, KeyValuePair<NativePdhGpuEngineKey, double>> BusiestEngines(
        IEnumerable<NativePdhGpuEngineRow> rows)
        => ByEngine(rows).GroupBy(pair => pair.Key.AdapterLuid).ToDictionary(group => group.Key,
            group => group.OrderByDescending(pair => pair.Value)
                .ThenBy(pair => pair.Key.PhysicalIndex).ThenBy(pair => pair.Key.EngineIndex).First());

    public static Dictionary<(ulong AdapterLuid, uint ProcessId), double> ByProcess(
        IEnumerable<NativePdhGpuEngineRow> rows)
        => rows.GroupBy(row => (Engine: EngineOf(row), row.ProcessId))
            .Select(group => (group.Key.Engine.AdapterLuid, group.Key.ProcessId,
                Usage: Math.Clamp(group.Sum(row => Sanitize(row.UsagePercent)), 0, 100)))
            .GroupBy(row => (row.AdapterLuid, row.ProcessId))
            .ToDictionary(group => group.Key, group => group.Max(row => row.Usage));

    private static double Sanitize(double value) => double.IsFinite(value) ? Math.Clamp(value, 0, 100) : 0;
}
