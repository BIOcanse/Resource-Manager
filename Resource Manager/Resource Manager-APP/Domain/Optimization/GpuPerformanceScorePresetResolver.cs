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

    private static readonly HashSet<string> IntegratedModels = new(StringComparer.Ordinal)
    {
        "AMD RADEON 8060S",
        "AMD RADEON 8050S",
        "AMD RADEON 890M",
        "AMD RADEON 880M",
        "AMD RADEON 780M",
        "AMD RADEON 760M",
        "AMD RADEON 680M",
        "AMD RADEON 660M",
        "AMD RADEON 610M",
    };

    public static GpuPerformanceScorePresetResult Resolve(string gpuName, ulong totalMemoryBytes,
        int standardGraphicsFrequencyMhz) => ResolveRaster(gpuName, totalMemoryBytes);

    // Persisted legacy use-case inputs no longer modify hardware capacity.
    public static GpuPerformanceScorePresetResult Resolve(string gpuName, ulong totalMemoryBytes,
        int standardGraphicsFrequencyMhz, string? gpuPerformanceUseCase) => ResolveRaster(gpuName, totalMemoryBytes);

    public static GpuPerformanceScorePresetResult Resolve(string gpuName, ulong totalMemoryBytes,
        int standardGraphicsFrequencyMhz, IEnumerable<string>? gpuPerformanceUseCases) => ResolveRaster(gpuName, totalMemoryBytes);

    private static GpuPerformanceScorePresetResult ResolveRaster(string gpuName, ulong totalMemoryBytes)
    {
        var normalized = Normalize(gpuName);
        var matched = GpuSteelNomadDefaults.TryGet(normalized, out var model, out var score);
        var integrated = IntegratedModels.Any(model => normalized == model || normalized.StartsWith(model + " ", StringComparison.Ordinal))
            || IsIntegratedNameFallback(gpuName, totalMemoryBytes);
        return new(score, score, 0, 0, [AppGpuPerformanceUseCases.General],
            matched ? GpuSteelNomadDefaults.Source : "unavailable:steel-nomad-dx12",
            model, matched, integrated);
    }

    public static bool IsLikelyIntegratedGpuName(string gpuName) => ResolveRaster(gpuName, 0).IsIntegrated;

    public static double NormalizeManualScore(double value) => double.IsFinite(value) ? Math.Max(0, value) : 0;

    private static bool IsIntegratedNameFallback(
        string gpuName,
        ulong totalMemoryBytes)
    {
        var normalized = Normalize(gpuName);
        if (normalized.Contains("RTX ", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("GTX ", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("GEFORCE", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("QUADRO", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("ARC ", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("ARC ", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains(" RX ", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("RX ", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("PRO W", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return totalMemoryBytes == 0
            || normalized.Contains("INTEGRATED", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("IRIS", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("UHD", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("RADEON GRAPHICS", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("GRAPHICS", StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var builder = new StringBuilder(value.Length);
        foreach (var character in value.Normalize(NormalizationForm.FormKD))
        {
            if (char.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
                builder.Append(char.IsLetterOrDigit(character) ? char.ToUpperInvariant(character) : ' ');
        }
        return string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(static token => token is not ("TM" or "R" or "GPU" or "GEFORCE"))
            .Select(static token => token == "LAPTOP" ? "NOTEBOOK" : token));
    }
}
