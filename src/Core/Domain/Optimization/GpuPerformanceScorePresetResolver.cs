using System.Globalization;
using System.Text;
using ResourceManager.App.Domain.Settings;

namespace ResourceManager.App.Domain.Optimization;

public static class GpuPerformanceScoreIds
{
    public static string FromIndex(int index)
    {
        return $"gpu:{Math.Max(0, index)}";
    }
}

public sealed record GpuPerformanceScorePresetResult(
    double Score,
    double RasterScore,
    double GenerationBonusScore,
    double UseCaseBonusScore,
    IReadOnlyList<string> GpuPerformanceUseCases,
    string Source,
    string? MatchedPreset,
    bool IsPresetMatch,
    bool IsIntegrated);

public static class GpuPerformanceScorePresetResolver
{

    public static GpuPerformanceScorePresetResult Resolve(string gpuName, ulong totalMemoryBytes,
        int standardGraphicsFrequencyMhz) => ResolveRaster(gpuName, totalMemoryBytes);

    // Persisted legacy use-case inputs no longer modify hardware capacity.
    public static GpuPerformanceScorePresetResult Resolve(string gpuName, ulong totalMemoryBytes,
        int standardGraphicsFrequencyMhz, string? gpuPerformanceUseCase) => ResolveRaster(gpuName, totalMemoryBytes);

    public static GpuPerformanceScorePresetResult Resolve(string gpuName, ulong totalMemoryBytes,
        int standardGraphicsFrequencyMhz, IEnumerable<string>? gpuPerformanceUseCases) => ResolveRaster(gpuName, totalMemoryBytes);

    private static GpuPerformanceScorePresetResult ResolveRaster(string gpuName, ulong _)
    {
        var normalized = Normalize(gpuName);
        var matched = GpuSteelNomadDefaults.TryGet(normalized, out var model, out var score);
        var integrated = GpuAdapterNameClassifier.IsLikelyIntegratedGpuName(gpuName);
        return new(score, score, 0, 0, [AppGpuPerformanceUseCases.General],
            matched ? GpuSteelNomadDefaults.GetSource(normalized) : "unavailable:steel-nomad-dx12",
            model, matched, integrated);
    }

    public static bool IsLikelyIntegratedGpuName(string gpuName) => GpuAdapterNameClassifier.IsLikelyIntegratedGpuName(gpuName);

    public static double NormalizeManualScore(double value) => double.IsFinite(value) ? Math.Max(0, value) : 0;

    private static string Normalize(string value) => GpuAdapterNameClassifier.Normalize(value);
}
