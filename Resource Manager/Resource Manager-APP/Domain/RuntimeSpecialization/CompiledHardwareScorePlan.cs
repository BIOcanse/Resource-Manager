using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Domain.Settings;

namespace ResourceManager.App.Domain.RuntimeSpecialization;

public sealed record CompiledHardwareScorePlan(
    IReadOnlyList<string> GpuPerformanceUseCases,
    IReadOnlyDictionary<string, double> GpuPerformanceScoresByGpuId,
    string CpuName,
    IReadOnlyDictionary<int, double> CpuPerformanceScoresByCoreIndex)
{
    public static CompiledHardwareScorePlan Default { get; } = new(
        [AppGpuPerformanceUseCases.General],
        new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase),
        string.Empty,
        new Dictionary<int, double>());

    public double ResolveGpuPerformanceScore(GpuMetrics gpu)
    {
        var gpuId = GpuPerformanceScoreIds.FromIndex(gpu.Index);
        return GpuPerformanceScoresByGpuId.TryGetValue(gpuId, out var overrideScore)
            ? GpuPerformanceScorePresetResolver.NormalizeManualScore(overrideScore)
            : GpuPerformanceScorePresetResolver.Resolve(
                gpu.Name,
                gpu.TotalMemoryBytes,
                gpu.StandardGraphicsFrequencyMhz,
                GpuPerformanceUseCases).Score;
    }

    public double ResolveCpuPerformanceScore(int coreIndex, double fallbackScore)
    {
        return CpuPerformanceScoresByCoreIndex.TryGetValue(coreIndex, out var overrideScore)
            ? Math.Max(0, double.IsFinite(overrideScore) ? overrideScore : 0)
            : Math.Max(0, double.IsFinite(fallbackScore) ? fallbackScore : 0);
    }
}
