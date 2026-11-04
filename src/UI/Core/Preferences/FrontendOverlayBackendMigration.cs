using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ResourceManager.NativeUi.Preferences;

// Migration frontend-preferences-overlays-to-backend-v1 (window-process half; the owner half is
// FrontendPreferencesOwner.OverlayMigration.cs). The user ruled on 2026-10-31 that each software's overlay settings belong
// to the backend setting performanceOverlay (docs/frontend_block_trees_2026-10-26.md「浮窗设置归后端」); the backend does
// not read frontend preferences, so on the first backend-session-ready after start the window process writes the entries an
// older cache still carries once:
//   1. GET /api/settings/app: the revision and the software ids the backend already has (compared ignoring case).
//   2. Each cache entry the backend does not have becomes a backend entry: switch, mode and presentation, old
//      metrics/metricSources through FrontendOverlayItems.MigrateLegacyPresentation. A field that fails the old cache
//      shape check is left out and the backend fills its default; the omission is traced. Numeric ranges are checked by
//      the backend write gate. The backend wins for ids it has.
//   3. PATCH /api/settings/app with only those ids. After a committed write (or when the backend already had every id)
//      the owner removes "overlays" from the cache file and the caller tells the resident the preferences changed.
// Any failure keeps the cache entries and is traced; the next start tries again. There is no retry loop and no timer.
// Delete this file with the owner half once every supported upgrade starts from this version.
internal static class FrontendOverlayBackendMigration
{
    internal enum Outcome { NothingPending, Completed, Failed }

    private static readonly string[] CommittedDispositions =
        ["committedAndApplied", "committedWithCapabilityConstraints", "committedWithDeliveryFailures"];

    internal static async Task<Outcome> RunAsync(FrontendPreferencesOwner owner, Uri apiBase,
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send, CancellationToken cancellation)
    {
        try
        {
            var pending = owner.PendingOverlayEntries();
            if (pending.Count == 0) return Outcome.NothingPending;
            var settingsUri = new Uri(apiBase, "api/settings/app");
            JsonObject current;
            using (var request = new HttpRequestMessage(HttpMethod.Get, settingsUri))
                current = await SendAsync(send, request, cancellation).ConfigureAwait(false);
            var revision = ReadRevision(current);
            var existing = ReadSoftware(current).Select(static pair => pair.Key);
            var changes = BuildSoftwareChanges(pending, existing);
            if (changes.Count != 0)
            {
                var body = new JsonObject
                {
                    ["expectedRevision"] = revision,
                    ["changes"] = new JsonObject { ["performanceOverlay"] = new JsonObject { ["software"] = changes } }
                };
                using var request = new HttpRequestMessage(HttpMethod.Patch, settingsUri)
                {
                    Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
                };
                var result = await SendAsync(send, request, cancellation).ConfigureAwait(false);
                var disposition = result["runtimeApplicationDisposition"]?.GetValue<string>();
                if (disposition is null || !CommittedDispositions.Contains(disposition, StringComparer.Ordinal))
                    throw new InvalidDataException($"The backend did not commit the overlay settings: {disposition ?? "no disposition"}.");
                _ = ReadRevision(result);
                // An older backend may ignore an unknown patch section. A generic committed receipt is insufficient.
                var committed = new HashSet<string>(ReadSoftware(result).Select(static pair => pair.Key), StringComparer.OrdinalIgnoreCase);
                if (pending.Any(pair => !committed.Contains(pair.Key)))
                    throw new InvalidDataException("The committed settings do not contain every migrated software entry.");
            }
            owner.CompleteOverlayMigration();
            Trace.TraceInformation("Overlay settings migration: {0} of {1} cached entries written to the backend settings.",
                changes.Count, pending.Count);
            return Outcome.Completed;
        }
        catch (Exception error) when (error is HttpRequestException or IOException or JsonException or InvalidDataException
            or InvalidOperationException or UnauthorizedAccessException or OperationCanceledException)
        {
            Trace.TraceWarning("Overlay settings migration failed; the cached entries are kept for the next start: {0}", error.Message);
            return Outcome.Failed;
        }
    }

    /// <summary>The PATCH value of performanceOverlay.software: one converted entry per cache entry the backend lacks.</summary>
    internal static JsonObject BuildSoftwareChanges(IEnumerable<KeyValuePair<string, JsonObject>> pending, IEnumerable<string> backendIds)
    {
        var known = new HashSet<string>(backendIds, StringComparer.OrdinalIgnoreCase);
        var changes = new JsonObject();
        foreach (var (softwareId, entry) in pending)
            if (known.Add(softwareId)) changes[softwareId] = ConvertEntry(softwareId, entry);
        return changes;
    }

    /// <summary>One cache entry as a backend entry; fields that fail the old cache check are left to the backend default.</summary>
    internal static JsonObject ConvertEntry(string softwareId, JsonObject cached)
    {
        var legacy = cached.DeepClone().AsObject();
        FrontendOverlayItems.MigrateLegacyPresentation(legacy, softwareId);
        var entry = new JsonObject();
        if (legacy["enabled"] is JsonValue enabled && enabled.TryGetValue<bool>(out var on)) entry["enabled"] = on;
        if (ReadString(legacy, "mode", static value => value is "external" or "injected") is { } mode) entry["mode"] = mode;
        var items = ReadItems(legacy);
        if (items is not null) entry["items"] = items;
        if (ReadString(legacy, "sizeMode", static value => value is "absolutePixels" or "windowRatio") is { } sizeMode)
            entry["sizeMode"] = sizeMode;
        // Range choices belong to the backend's declared settings contract. Preserve numbers and let that write gate
        // reject unsupported values, keeping the old cache available rather than silently substituting defaults.
        if (ReadInteger(legacy, "fontSizePx") is { } fontSize) entry["fontSizePx"] = fontSize;
        if (ReadNumber(legacy, "regionWidthRatio") is { } width) entry["regionWidthRatio"] = width;
        if (ReadNumber(legacy, "regionHeightRatio") is { } height) entry["regionHeightRatio"] = height;
        if (ReadString(legacy, "anchor", static value => value is "topLeft" or "topRight" or "bottomLeft" or "bottomRight") is { } anchor)
            entry["anchor"] = anchor;
        if (ReadInteger(legacy, "marginPx") is { } margin) entry["marginPx"] = margin;
        var omitted = legacy.Select(static pair => pair.Key).Where(key => !entry.ContainsKey(key)).ToArray();
        if (omitted.Length != 0)
            Trace.TraceWarning("Overlay settings migration {0}: invalid cached fields left to the backend default: {1}",
                softwareId, string.Join(", ", omitted));
        return entry;

        JsonArray? ReadItems(JsonObject source)
        {
            if (source["items"] is not JsonArray array) return null;
            try
            {
                var parsed = array.Deserialize<FrontendOverlayItem[]>(FrontendPreferencesOwner.Json);
                if (parsed is null || FrontendOverlayItems.Issue(parsed) is not null) return null;
                return JsonSerializer.SerializeToNode(parsed, FrontendPreferencesOwner.Json)!.AsArray();
            }
            catch (JsonException) { return null; }
        }
    }

    private static string? ReadString(JsonObject source, string key, Func<string, bool> valid)
        => source[key] is JsonValue value && value.TryGetValue<string>(out var read) && valid(read) ? read : null;

    private static int? ReadInteger(JsonObject source, string key)
        => source[key] is JsonValue value && value.TryGetValue<int>(out var read) ? read : null;

    private static double? ReadNumber(JsonObject source, string key)
        => source[key] is JsonValue value && value.TryGetValue<double>(out var read) && double.IsFinite(read)
            ? read : null;

    private static JsonObject ReadSoftware(JsonObject result)
    {
        if (result["settings"] is not JsonObject settings || settings["performanceOverlay"] is not JsonObject overlay
            || overlay["software"] is not JsonObject software)
            throw new InvalidDataException("The backend did not return the performanceOverlay.software settings section.");
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, entry) in software)
            if (string.IsNullOrWhiteSpace(id) || id != id.Trim() || !ids.Add(id) || entry is not JsonObject)
                throw new InvalidDataException("The backend returned invalid overlay software entries.");
        return software;
    }

    private static string ReadRevision(JsonObject result)
        => result["revision"] is JsonValue value && value.TryGetValue<string>(out var revision)
            && !string.IsNullOrWhiteSpace(revision) ? revision
            : throw new InvalidDataException("The settings response has no revision.");

    private static async Task<JsonObject> SendAsync(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send,
        HttpRequestMessage request, CancellationToken cancellation)
    {
        using var response = await send(request, cancellation).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidDataException($"{request.Method} {request.RequestUri?.AbsolutePath} returned {(int)response.StatusCode}.");
        return JsonNode.Parse(text) as JsonObject ?? throw new InvalidDataException("The settings response is not an object.");
    }

}
