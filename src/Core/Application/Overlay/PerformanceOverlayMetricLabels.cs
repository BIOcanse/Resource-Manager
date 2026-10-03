using System.Text.Json;
using System.Text.RegularExpressions;

namespace ResourceManager.App.Application.Overlay;

/// <summary>Labels are generated from the frontend's nine metric-copy owners.</summary>
public static partial class PerformanceOverlayMetricLabels
{
    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Labels = Load();

    public static IReadOnlyDictionary<string, string> ForMetric(string metricId)
    {
        var match = IndexedMetricId().Match(metricId);
        var key = match.Success
            ? string.Concat(metricId.AsSpan(0, match.Groups["index"].Index),
                "{index}", metricId.AsSpan(match.Groups["index"].Index + match.Groups["index"].Length))
            : metricId;
        if (!Labels.TryGetValue(key, out var labels))
            return new Dictionary<string, string>
            {
                ["zh-CN"] = metricId, ["zh-TW"] = metricId, ["en-US"] = metricId,
                ["ja-JP"] = metricId, ["ko-KR"] = metricId, ["fr-FR"] = metricId,
                ["de-DE"] = metricId, ["es-ES"] = metricId, ["ru-RU"] = metricId
            };
        if (!match.Success) return labels;
        var index = match.Groups["index"].Value;
        return labels.ToDictionary(
            static pair => pair.Key,
            pair => pair.Value.Replace("{index}", index, StringComparison.Ordinal));
    }

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Load()
    {
        using var stream = typeof(PerformanceOverlayMetricLabels).Assembly.GetManifestResourceStream(
            "ResourceManager.Resources.performance-overlay-metric-labels.json")
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
