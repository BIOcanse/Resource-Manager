using System.Globalization;
using System.Text;
namespace ResourceManager.ReadWrite.Windows.Gpu;

/// <summary>The original name-based classification; it does not prove a physical integrated/discrete topology.</summary>
public static class GpuAdapterNameClassifier
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
        "INTEL ARC GRAPHICS",
        "INTEL ARC 140T",
        "INTEL ARC 130T",
        "INTEL ARC 140V",
        "INTEL ARC 130V",
    };

    public static bool IsLikelyIntegratedGpuName(string gpuName)
    {
        var normalized = Normalize(gpuName);
        return IntegratedModels.Any(model => normalized == model || normalized.StartsWith(model + " ", StringComparison.Ordinal))
            || IsIntegratedNameFallback(gpuName);
    }

    private static bool IsIntegratedNameFallback(string gpuName)
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

        return normalized.Contains("INTEGRATED", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("IRIS", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("UHD", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("RADEON GRAPHICS", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("GRAPHICS", StringComparison.OrdinalIgnoreCase);
    }

    public static string Normalize(string value)
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
