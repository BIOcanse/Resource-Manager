using System.Text.Json;

namespace ResourceManager.NativeUi.Preferences;

// Dashboard data belongs to the frontend. The shell validates its cache at the
// write/read boundary; it does not instantiate device-dependent presentation.
internal static class FrontendDashboardCache
{
    internal static void Validate(JsonElement? value)
    {
        if (value is null || value.Value.ValueKind == JsonValueKind.Null) return;
        var layout = value.Value;
        if (layout.ValueKind != JsonValueKind.Object) throw Invalid();
        foreach (var name in new[] { "cards", "resourceBars", "resourceTableColumns", "resourceTableProcessColumns" })
        {
            if (!layout.TryGetProperty(name, out var items) || items.ValueKind == JsonValueKind.Null) continue;
            if (items.ValueKind != JsonValueKind.Array || items.GetArrayLength() > 10000) throw Invalid();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in items.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("id", out var id)
                    || id.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(id.GetString())
                    || !ids.Add(id.GetString()!)) throw Invalid();
                if (name == "cards")
                {
                    if (item.TryGetProperty("main", out var main) && main.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)) throw Invalid();
                    if (item.TryGetProperty("small", out var small) && small.ValueKind != JsonValueKind.Null)
                    {
                        if (small.ValueKind != JsonValueKind.Array || small.GetArrayLength() > FrontendPoints.DashboardLayoutLimits.SmallMetricLimit) throw Invalid();
                        foreach (var metric in small.EnumerateArray())
                            if (metric.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(metric.GetString())) throw Invalid();
                    }
                }
                else if (name == "resourceBars")
                {
                    if (!item.TryGetProperty("metricId", out var metric) || metric.ValueKind != JsonValueKind.String
                        || string.IsNullOrWhiteSpace(metric.GetString())
                        || !item.TryGetProperty("scaleMode", out var mode) || mode.ValueKind != JsonValueKind.String
                        || mode.GetString() is not ("active" or "capacity")) throw Invalid();
                    ValidateSource(item);
                }
                else
                {
                    if (!item.TryGetProperty("visible", out var visible) || visible.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw Invalid();
                    if (item.TryGetProperty("width", out var width) && width.ValueKind != JsonValueKind.Null
                        && (width.ValueKind != JsonValueKind.Number || !width.TryGetDouble(out var number)
                            || !double.IsFinite(number) || number < FrontendPoints.DashboardLayoutLimits.MinimumColumnWidth
                            || number > FrontendPoints.DashboardLayoutLimits.MaximumColumnWidth)) throw Invalid();
                    ValidateSource(item);
                }
            }
        }
    }

    // A bar's or column's source: absent or null is automatic, otherwise a non-empty backend
    // process field name (docs/metric_item_source_choice_2026-10-25.md).
    private static void ValidateSource(JsonElement item)
    {
        if (item.TryGetProperty("source", out var source) && source.ValueKind != JsonValueKind.Null
            && (source.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(source.GetString()))) throw Invalid();
    }

    private static InvalidDataException Invalid() => new("Invalid frontend dashboard cache.");
}
