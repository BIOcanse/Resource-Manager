using System.Text.Json;
using System.Text.RegularExpressions;

namespace ResourceManager.NativeUi.Overlay;

/// <summary>Labels are generated from the frontend's nine metric-copy owners.</summary>
internal static partial class OverlayMetricLabels
{
    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Labels = Load();

    internal static string ForMetric(string metricId, string language)
    {
        var match = IndexedMetricId().Match(metricId);
        var key = match.Success
            ? string.Concat(metricId.AsSpan(0, match.Groups["index"].Index),
                "{index}", metricId.AsSpan(match.Groups["index"].Index + match.Groups["index"].Length))
            : metricId;
        if (!Labels.TryGetValue(key, out var labels)) return metricId;
        var label = labels.GetValueOrDefault(language)
            ?? throw new InvalidDataException($"The overlay locale '{language}' has no metric '{key}'.");
        if (!match.Success) return label;
        var index = match.Groups["index"].Value;
        return label.Replace("{index}", index, StringComparison.Ordinal);
    }

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Load()
    {
        using var stream = typeof(OverlayMetricLabels).Assembly.GetManifestResourceStream(
            "ResourceManager.NativeUi.Overlay.metric-labels.json")
            ?? throw new InvalidOperationException("The overlay metric label resource is missing.");
        var parsed = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(stream)
            ?? throw new InvalidDataException("The overlay metric label resource is empty.");
        return parsed.ToDictionary(
            static pair => pair.Key,
            static pair => (IReadOnlyDictionary<string, string>)pair.Value,
            StringComparer.OrdinalIgnoreCase);
    }

    [GeneratedRegex(@"\.(?<index>\d+)(?=\.|$)")]
    private static partial Regex IndexedMetricId();
}
