using System.Text.Json;

namespace ResourceManager.NativeUi.Overlay;

/// <summary>Overlay item names and unavailable reasons, generated from the frontend's nine copies
/// (scripts/generateOverlayMetricLabels.mjs writes overlay-item-labels.json). The lookup order is the one in
/// src/UI/Frontend/src/features/overlay/overlayItemLabels.ts:
/// - frames items keep the old overlay's names (frames.fps → target.fps, frames.present → target.frameTime,
///   frames.low1 → target.onePercentLow, frames.low01 → target.pointOnePercentLow);
/// - the four software items the old overlay had keep its names too (cpu.usage → target.cpu, memory.usage →
///   target.memory, gpu.usage → target.gpu, gpu.vram → target.vram);
/// - other software items are the software template around a name: the process field name, else the metric name,
///   else the ID;
/// - metric items are the metric name, else the ID.</summary>
internal static class OverlayItemLabels
{
    private sealed record Document(
        Dictionary<string, string> SoftwareTemplate,
        Dictionary<string, Dictionary<string, string>> ProcessFields,
        Dictionary<string, Dictionary<string, string>> Reasons);

    private static readonly Dictionary<string, string> FrameLabelKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["frames.fps"] = "target.fps",
        ["frames.present"] = "target.frameTime",
        ["frames.low1"] = "target.onePercentLow",
        ["frames.low01"] = "target.pointOnePercentLow"
    };

    private static readonly Dictionary<string, string> SoftwareLabelKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["cpu.usage"] = "target.cpu",
        ["memory.usage"] = "target.memory",
        ["gpu.usage"] = "target.gpu",
        ["gpu.vram"] = "target.vram"
    };

    private static readonly Document Labels = Load();

    internal static string ForItem(string kind, string id, string language)
    {
        if (kind == "frames")
            return FrameLabelKeys.TryGetValue(id, out var key) ? OverlayMetricLabels.ForMetric(key, language) : id;
        if (kind != "software") return OverlayMetricLabels.ForMetric(id, language);
        if (SoftwareLabelKeys.TryGetValue(id, out var softwareKey)) return OverlayMetricLabels.ForMetric(softwareKey, language);
        var name = Labels.ProcessFields.TryGetValue(id, out var field) ? InLanguage(field, language, id)
            : OverlayMetricLabels.ForMetric(id, language);
        return InLanguage(Labels.SoftwareTemplate, language, "software template").Replace("{name}", name, StringComparison.Ordinal);
    }

    // An unknown reason code is shown as is.
    internal static string ForReason(string reason, string language) =>
        Labels.Reasons.TryGetValue(reason, out var texts) ? InLanguage(texts, language, reason) : reason;

    private static string InLanguage(Dictionary<string, string> texts, string language, string what) =>
        texts.GetValueOrDefault(language)
        ?? throw new InvalidDataException($"The overlay locale '{language}' has no text for '{what}'.");

    private static Document Load()
    {
        using var stream = typeof(OverlayItemLabels).Assembly.GetManifestResourceStream(
            "ResourceManager.NativeUi.Overlay.overlay-item-labels.json")
            ?? throw new InvalidOperationException("The overlay item label resource is missing.");
        using var json = JsonDocument.Parse(stream);
        var root = json.RootElement;
        Dictionary<string, string> Texts(JsonElement element) =>
            element.EnumerateObject().ToDictionary(static item => item.Name, static item => item.Value.GetString()
                ?? throw new InvalidDataException("The overlay item label resource has an empty text."), StringComparer.Ordinal);
        Dictionary<string, Dictionary<string, string>> Table(string name) =>
            root.GetProperty(name).EnumerateObject().ToDictionary(static item => item.Name, item => Texts(item.Value),
                StringComparer.Ordinal);
        return new(Texts(root.GetProperty("softwareTemplate")), Table("processFields"), Table("reasons"));
    }
}
