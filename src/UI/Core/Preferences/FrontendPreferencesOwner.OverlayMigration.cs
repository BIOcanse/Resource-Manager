using System.Text.Json;
using System.Text.Json.Nodes;

namespace ResourceManager.NativeUi.Preferences;

// Migration frontend-preferences-overlays-to-backend-v1 (owner half; the window process's half is
// FrontendOverlayBackendMigration.cs). Since 2026-10-31 each software's overlay settings are the backend setting
// performanceOverlay (docs/frontend_block_trees_2026-10-26.md「浮窗设置归后端」). A cache written before that still
// carries "overlays": {softwareId: {enabled, mode, items, sizeMode, ...}}. The owner keeps those entries out of
// FrontendPreferences: Read takes them from the file into legacyOverlays, every Save writes them back unchanged, Reset keeps
// them, and the page never sees them. After the window process has written them to the backend, CompleteOverlayMigration
// removes them from the file. Delete this file, the overlays handling in the legacy imports and FrontendOverlayItems once
// every supported upgrade starts from this version.
internal sealed partial class FrontendPreferencesOwner
{
    private static readonly string[] LegacyOverlayKeys =
        ["enabled", "mode", "items", "sizeMode", "fontSizePx", "regionWidthRatio", "regionHeightRatio", "anchor", "marginPx"];

    // The cache file's overlay entries waiting for the backend migration (raw JSON by software id); null when none remain.
    private JsonObject? legacyOverlays;
    private bool legacyOverlaysLoaded;

    /// <summary>The overlay entries still waiting for the backend, as copies; empty when none remain.</summary>
    internal IReadOnlyList<KeyValuePair<string, JsonObject>> PendingOverlayEntries()
    {
        lock (gate)
        {
            Read();
            return legacyOverlays?.Where(static pair => pair.Value is JsonObject)
                .Select(static pair => KeyValuePair.Create(pair.Key, pair.Value!.DeepClone().AsObject())).ToArray() ?? [];
        }
    }

    /// <summary>Removes the overlay entries from the cache file (atomic replacement) after the backend committed them.</summary>
    internal bool CompleteOverlayMigration()
    {
        lock (gate)
        {
            var current = Read();
            if (legacyOverlays is null) return false;
            // Read under the same lock as Save: a preference write during the backend request must survive cleanup.
            // Publish to disk before clearing the in-memory pending entries, so a failed replacement can be retried.
            WriteAtomic(cachePath, JsonSerializer.SerializeToUtf8Bytes(current, Json));
            legacyOverlays = null;
            return true;
        }
    }

    // Removes "overlays" from a parsed cache before the strict read; returns the entries when there are any.
    private static JsonObject? TakeLegacyOverlays(JsonObject node)
    {
        if (!node.TryGetPropertyValue("overlays", out var value)) return null;
        node.Remove("overlays");
        if (value is not JsonObject overlays)
            throw new InvalidDataException("Invalid frontend overlay cache.");
        foreach (var (softwareId, entry) in overlays)
            if (string.IsNullOrWhiteSpace(softwareId) || entry is not JsonObject)
                throw new InvalidDataException("Invalid frontend overlay cache key.");
        return overlays.Count == 0 ? null : overlays;
    }

    // A Save before any Read (an explicit replacement) still keeps the entries the file carries. A cache that does not
    // parse has no entries to keep; the strict read reports it.
    private void LoadLegacyOverlays()
    {
        if (legacyOverlaysLoaded) return;
        try
        {
            using var stream = Configuration.SettingsFileReader.Open(cachePath);
            if (JsonNode.Parse(stream) is JsonObject node) legacyOverlays = TakeLegacyOverlays(node);
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException or JsonException
            or InvalidDataException) { }
        // A transient I/O/access failure must not mark the input as loaded: the next Save still has to preserve it.
        legacyOverlaysLoaded = true;
    }

    private byte[] SerializeCache(FrontendPreferences value)
    {
        if (legacyOverlays is null) return JsonSerializer.SerializeToUtf8Bytes(value, Json);
        var node = JsonSerializer.SerializeToNode(value, Json)!.AsObject();
        node["overlays"] = legacyOverlays.DeepClone();
        return JsonSerializer.SerializeToUtf8Bytes(node, Json);
    }

    // The overlay fields of a legacy backend-era entry, as they were stored; the backend migration checks each one.
    private static JsonObject LegacyOverlayFields(JsonObject legacy)
    {
        var entry = new JsonObject();
        foreach (var key in LegacyOverlayKeys)
            if (legacy.TryGetPropertyValue(key, out var value) && value is not null) entry[key] = value.DeepClone();
        return entry;
    }
}
