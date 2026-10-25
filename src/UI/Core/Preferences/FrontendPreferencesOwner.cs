using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace ResourceManager.NativeUi.Preferences;

/// <summary>One native frontend owner shared by the WebView, startup appearance and overlay.</summary>
internal sealed partial class FrontendPreferencesOwner
{
    internal const string LegacyBackupSuffix = ".frontend-preferences-v1.bak";
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    private static readonly Lazy<FrontendPreferencesOwner> Instance = new(() => new(
        NativeUiPaths.FrontendPreferencesPath,
        NativeUiPaths.AppSettingsPath,
        Path.Combine(NativeUiPaths.PackageRoot, "UserData", "SoftwareProfiles", "performance-overlay.local.json"),
        Path.Combine(NativeUiPaths.PackageRoot, "Config", "dashboard-settings.json")));

    internal static FrontendPreferencesOwner Current => Instance.Value;
    internal static FrontendPreferencesOwner ForCache(string path) =>
        Path.GetFullPath(path).Equals(Path.GetFullPath(NativeUiPaths.FrontendPreferencesPath), StringComparison.OrdinalIgnoreCase)
            ? Current : new(path);

    private readonly object gate = new();
    private readonly string cachePath;
    private readonly string importMarkerPath;
    private readonly string? legacyAppPath;
    private readonly string? legacyOverlayPath;
    private bool initialized;
    private bool overlayChoicesInitialized;
    private readonly FrontendPreferences pointValues = new();
    private long revision;
    private FrontendPreferences? cached;
    private (long Ticks, long Length)? version;

    internal FrontendPreferencesOwner(string cachePath, string? legacyAppPath = null, string? legacyOverlayPath = null,
        string? legacyDashboardPath = null)
    {
        this.cachePath = Path.GetFullPath(cachePath);
        importMarkerPath = this.cachePath + ".imported-v1";
        this.legacyAppPath = legacyAppPath;
        this.legacyOverlayPath = legacyOverlayPath;
        this.legacyDashboardPath = legacyDashboardPath;
        dashboardImportMarkerPath = this.cachePath + ".dashboard-imported-v1";
    }

    internal FrontendPreferences Read()
    {
        lock (gate)
        {
            RemoveRetiredCacheFields();
            InitializeImport();
            ImportLegacyOverlayChoices();
            InitializeDashboardImport();
            var file = new FileInfo(cachePath);
            if (!file.Exists)
            {
                if (!ReferenceEquals(cached, pointValues)) revision++;
                cached = pointValues;
                version = null;
                return pointValues; // No cached user choice: use this build's generated points.
            }
            var currentVersion = (file.LastWriteTimeUtc.Ticks, file.Length);
            if (cached is not null && version == currentVersion) return cached;
            using var stream = Configuration.SettingsFileReader.Open(cachePath);
            var value = JsonSerializer.Deserialize<FrontendPreferences>(stream, Json)
                ?? throw new InvalidDataException("Frontend preferences cache is empty.");
            value.Validate();
            cached = value;
            version = currentVersion;
            revision++;
            return value;
        }
    }

    internal FrontendPreferences Save(FrontendPreferences value)
    {
        ArgumentNullException.ThrowIfNull(value);
        value.Validate();
        lock (gate)
        {
            // Initialization must never import over an explicit user update.
            WriteAtomic(cachePath, JsonSerializer.SerializeToUtf8Bytes(value, Json));
            WriteImportMarker();
            WriteDashboardImportMarker();
            overlayChoicesInitialized = true;
            initialized = true;
            cached = value;
            var file = new FileInfo(cachePath);
            version = (file.LastWriteTimeUtc.Ticks, file.Length);
            revision++;
            return value;
        }
    }

    internal FrontendPreferences SaveGlobal(FrontendPreferences value)
    {
        lock (gate)
        {
            var current = Read();
            return Save(current with { Appearance = value.Appearance, Refresh = value.Refresh, Notifications = value.Notifications });
        }
    }

    internal FrontendPreferences SaveOverlay(string softwareId, FrontendPreferences value)
    {
        if (string.IsNullOrWhiteSpace(softwareId)) throw new InvalidDataException("Missing overlay cache key.");
        lock (gate)
        {
            var current = Read();
            var overlays = new Dictionary<string, FrontendOverlayPresentation>(current.Overlays, StringComparer.OrdinalIgnoreCase);
            var presentation = value.Overlays.FirstOrDefault(pair => pair.Key.Equals(softwareId, StringComparison.OrdinalIgnoreCase)).Value;
            if (presentation is not null) overlays[softwareId] = presentation;
            else overlays.Remove(softwareId);
            return Save(current with { Overlays = overlays });
        }
    }

    internal FrontendPreferences Reset()
    {
        lock (gate)
        {
            var current = Read();
            var choices = current.Overlays.ToDictionary(static pair => pair.Key,
                static pair => new FrontendOverlayPresentation { Enabled = pair.Value.Enabled, Mode = pair.Value.Mode },
                StringComparer.OrdinalIgnoreCase);
            if (choices.Count != 0) return Save(pointValues with { Overlays = choices });
            WriteImportMarker();
            WriteDashboardImportMarker();
            File.Delete(cachePath);
            initialized = overlayChoicesInitialized = true;
            cached = pointValues;
            version = null;
            revision++;
            return pointValues;
        }
    }

    internal FrontendOverlayPresentation Overlay(string softwareId)
    {
        var value = Read();
        return value.Overlays.FirstOrDefault(pair =>
            pair.Key.Equals(softwareId, StringComparison.OrdinalIgnoreCase)).Value ?? new();
    }

    internal long Revision
    {
        get { Read(); lock (gate) return revision; }
    }

    // Migration frontend-preferences-retired-v1. The user retired the smart monitoring switch on
    // 2026-10-25; caches written before that still carry refresh.smartMonitoringMode, which the
    // strict reader rejects. Remove that one field once and rewrite the cache atomically; every
    // other value, and a cache that does not parse, is left to the normal read. Delete together
    // with frontend-preferences-v1 once every supported upgrade starts after this release.
    private bool retiredFieldsRemoved;

    private void RemoveRetiredCacheFields()
    {
        if (retiredFieldsRemoved) return;
        if (File.Exists(cachePath))
        {
            JsonNode? root = null;
            try
            {
                using var stream = Configuration.SettingsFileReader.Open(cachePath);
                root = JsonNode.Parse(stream);
            }
            catch (JsonException) { } // The strict read below reports a damaged cache.
            if (root?["refresh"] is JsonObject refresh && refresh.Remove("smartMonitoringMode"))
                WriteAtomic(cachePath, JsonSerializer.SerializeToUtf8Bytes(root, Json));
        }
        retiredFieldsRemoved = true;
    }

    // Migration frontend-preferences-v1 (frontend half). Delete only when every supported
    // upgrade starts after the legacy file schema. The marker is NOT cache data: clearing
    // the cache/reset must never resurrect choices from an old backup.
    private void InitializeImport()
    {
        if (initialized) return;
        if (File.Exists(cachePath) || File.Exists(importMarkerPath))
        {
            WriteImportMarker();
            initialized = true;
            return;
        }
        var node = JsonSerializer.SerializeToNode(new FrontendPreferences(), Json)!.AsObject();
        var imported = false;
        var app = ReadLegacy(legacyAppPath, static value => value["appearance"] is JsonObject
            || value["performance"] is JsonObject performance && performance.ContainsKey("monitorRefreshIntervalMs"));
        if (app is not null)
        {
            if (app["appearance"] is JsonObject appearance)
            {
                CopyKnown(appearance, node["appearance"]!.AsObject());
                imported = true;
            }
            if (app["performance"] is JsonObject performance)
            {
                var refresh = node["refresh"]!.AsObject();
                // The retired smart monitoring switch (user ruling 2026-10-25) is not imported.
                foreach (var (source, destination) in new[] {
                    ("monitorRefreshIntervalMs", "monitor"),
                    ("resourceTableRefreshIntervalMs", "resourceTable"), ("localSystemRefreshIntervalMs", "localSystem") })
                {
                    if (performance.TryGetPropertyValue(source, out var value) && value is not null)
                    {
                        refresh[destination] = value.DeepClone();
                        imported = true;
                    }
                }
            }
        }
        var overlay = ReadLegacy(legacyOverlayPath, static value => value["software"] is JsonArray entries
            && entries.OfType<JsonObject>().Any(static item => item.ContainsKey("fontSizePx") || item.ContainsKey("metrics") || item.ContainsKey("enabled")));
        if (overlay?["software"] is JsonArray software)
        {
            var overlays = node["overlays"]!.AsObject();
            foreach (var item in software.OfType<JsonObject>())
            {
                if (item["softwareId"] is not JsonValue idValue || !idValue.TryGetValue<string>(out var id)
                    || string.IsNullOrWhiteSpace(id)) continue;
                var presentation = JsonSerializer.SerializeToNode(new FrontendOverlayPresentation(), Json)!.AsObject();
                CopyKnown(item, presentation);
                overlays[id] = presentation;
                imported = true;
            }
        }
        if (imported)
        {
            try
            {
                var value = node.Deserialize<FrontendPreferences>(Json)!;
                value.Validate();
                WriteAtomic(cachePath, JsonSerializer.SerializeToUtf8Bytes(value, Json));
            }
            catch (Exception error) when (error is JsonException or InvalidDataException)
            {
                // An invalid legacy document is not an existing frontend cache. Keep
                // the original/backup for diagnosis; do not make an upgrade unstartable.
                Trace.TraceWarning("Frontend preference legacy values were rejected: {0}", error.Message);
            }
        }
        WriteImportMarker();
        initialized = true;
    }

    // Frontend-only upgrade from the last backend-owned operational writer. Existing
    // cache choices win; the cache migration version makes resets and explicit saves final.
    private void ImportLegacyOverlayChoices()
    {
        if (overlayChoicesInitialized) return;
        if (File.Exists(cachePath))
        {
            using var input = Configuration.SettingsFileReader.Open(cachePath);
            var node = JsonSerializer.Deserialize<JsonObject>(input, Json)
                ?? throw new InvalidDataException("Frontend preferences cache must be an object.");
            // Missing fields use the same defaults as Read; malformed committed values
            // must fail before a migration can replace the cache.
            var current = node.Deserialize<FrontendPreferences>(Json)!;
            current.Validate();
            if (node["overlaySelectionVersion"]?.GetValue<int>() == 1) { overlayChoicesInitialized = true; return; }
            var entries = (node["overlays"] ??= new JsonObject()).AsObject();
            JsonObject? legacy = null;
            foreach (var source in new[] { legacyOverlayPath, legacyOverlayPath is null ? null : legacyOverlayPath + LegacyBackupSuffix })
            {
                if (source is null) continue;
                try
                {
                    using var stream = Configuration.SettingsFileReader.Open(source);
                    if (JsonNode.Parse(stream) is JsonObject candidate && candidate["software"] is JsonArray)
                    { legacy = candidate; break; }
                }
                catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { }
            }
            node["overlaySelectionVersion"] = 1;
            if (legacy?["software"] is JsonArray software)
                foreach (var item in software.OfType<JsonObject>())
                {
                    var id = item["softwareId"]?.GetValue<string>();
                    if (string.IsNullOrWhiteSpace(id)) continue;
                    var key = entries.Select(static pair => pair.Key).FirstOrDefault(key => key.Equals(id, StringComparison.OrdinalIgnoreCase)) ?? id;
                    var existing = entries[key] as JsonObject;
                    if (existing?.ContainsKey("enabled") == true && existing.ContainsKey("mode")) continue;
                    var target = existing ?? JsonSerializer.SerializeToNode(new FrontendOverlayPresentation(), Json)!.AsObject();
                    if (existing?.ContainsKey("enabled") != true && item["enabled"] is JsonValue enabled && enabled.TryGetValue<bool>(out var value))
                        target["enabled"] = value;
                    if (existing?.ContainsKey("mode") != true && item["mode"] is JsonValue mode && mode.TryGetValue<string>(out var text))
                        target["mode"] = text.Equals("injected", StringComparison.OrdinalIgnoreCase) ? "injected" : "external";
                    if (existing is null) entries[key] = target;
                }
            var migrated = node.Deserialize<FrontendPreferences>(Json)!;
            migrated.Validate();
            WriteAtomic(cachePath, JsonSerializer.SerializeToUtf8Bytes(migrated, Json));
        }
        overlayChoicesInitialized = true;
    }

    private static void CopyKnown(JsonObject source, JsonObject destination)
    {
        foreach (var key in destination.Select(static item => item.Key).ToArray())
            if (source.TryGetPropertyValue(key, out var value) && value is not null)
                destination[key] = value.DeepClone();
    }

    private static JsonObject? ReadLegacy(string? path, Func<JsonObject, bool> hasPresentation)
    {
        if (path is null) return null;
        // Backup is published BEFORE the backend's atomic replacement. A handle to the
        // old file stays valid through replacement; if we saw the new file, recheck backup.
        foreach (var candidate in new[] { path + LegacyBackupSuffix, path, path + LegacyBackupSuffix })
        {
            try
            {
                using var stream = Configuration.SettingsFileReader.Open(candidate);
                if (JsonNode.Parse(stream) is JsonObject value && hasPresentation(value)) return value;
            }
            catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { }
            catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException)
            {
                // Invalid legacy input is not a cache and must not prevent startup. Keep
                // the original file, report the exact failed input, and leave values absent.
                Trace.TraceWarning("Frontend preference import failed for {0}: {1}", candidate, error.Message);
            }
        }
        return null;
    }

    private void WriteImportMarker()
    {
        if (!File.Exists(importMarkerPath)) WriteAtomic(importMarkerPath, "frontend-preferences-v1"u8.ToArray());
    }

    private static void WriteAtomic(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
