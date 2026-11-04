using System.Text.Json;

namespace ResourceManager.NativeUi.Preferences;

// Targeted recording view templates belong to the frontend (docs/targeted_recording_views_2026-10-30.md §3 "视图").
// The shell only checks the outer shape at the write/read boundary: an array of objects, each with a unique
// non-empty id, a string name and a views array. The view fields themselves are validated by the frontend.
internal static class FrontendReportViewTemplatesCache
{
    internal const int MaximumTemplates = 200;
    internal const int MaximumViewsPerTemplate = 64;

    internal static void Validate(JsonElement? value)
    {
        if (value is null || value.Value.ValueKind == JsonValueKind.Null) return;
        var templates = value.Value;
        if (templates.ValueKind != JsonValueKind.Array || templates.GetArrayLength() > MaximumTemplates) throw Invalid();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var template in templates.EnumerateArray())
        {
            if (template.ValueKind != JsonValueKind.Object
                || !template.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(id.GetString()) || !ids.Add(id.GetString()!)
                || !template.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String
                || !template.TryGetProperty("views", out var views) || views.ValueKind != JsonValueKind.Array
                || views.GetArrayLength() is 0 or > MaximumViewsPerTemplate) throw Invalid();
            foreach (var view in views.EnumerateArray())
                if (view.ValueKind != JsonValueKind.Object) throw Invalid();
        }
    }

    private static InvalidDataException Invalid() => new("Invalid frontend targeted report view templates cache.");
}
