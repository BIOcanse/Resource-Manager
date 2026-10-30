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
    // This process's owner is the cache's only writer (the resident host only reads it on its
    // preferences-changed event), so the cache is read from disk once and every Save or Reset
    // replaces this copy; later reads never stat or reopen the file.
    private FrontendPreferences? cached;

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

    [System.ComponentModel.Description("RuntimeRead: EventDriven(\"Frontend preferences saved or reset by this owner\")")]
    internal FrontendPreferences Read()
    {
        lock (gate)
        {
            if (cached is not null) return cached;
            MigrateCacheFields();
            InitializeImport();
            ImportLegacyOverlayChoices();
            InitializeDashboardImport();
            if (!File.Exists(cachePath))
            {
                revision++;
                cached = pointValues;
                return pointValues; // No cached user choice: use this build's generated points.
            }
            using var stream = Configuration.SettingsFileReader.Open(cachePath);
            var value = JsonSerializer.Deserialize<FrontendPreferences>(stream, Json)
                ?? throw new InvalidDataException("Frontend preferences cache is empty.");
            value.Validate();
            cached = value;
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

    // Read-time cache migrations, applied once per owner and written back atomically when they
    // change something; every other value, and a cache that does not parse, is left to the
    // normal strict read.
    // - frontend-preferences-retired-v1: the user retired the smart monitoring switch on
    //   2026-10-25; caches written before that still carry refresh.smartMonitoringMode, which the
    //   strict reader rejects. Delete together with frontend-preferences-v1 once every supported
    //   upgrade starts after this release.
    // - frontend-preferences-animation-style-v1: see MigrateAnimationAppearance.
    // - frontend-preferences-nvml-fb-used-v1: see MigrateNvmlFbUsedField.
    // - frontend-preferences-overlay-items-v1: overlay metrics and metricSources become items, after the NVML rename so
    //   converted sources carry the new field name (FrontendOverlayItems.MigrateLegacyPresentation).
    // - frontend-preferences-notified-version-v1: notifications.updateInAppNotifiedVersion recorded which version the
    //   in-app update notice had shown; since 2026-10-29 both channels follow the notification sequence
    //   (docs/notifications_2026-10-29.md), so the record is dropped. Delete once every supported upgrade starts after
    //   that release.
    // - frontend-preferences-self-scheduling-options-v1: on 2026-10-29 the user removed Resource Manager
    //   self-scheduling and the frontend option it triggered: appearance.autoDisableAnimations (turn animation off by
    //   self-scheduling) is dropped, and animation follows only the selected style. The other removed option, the
    //   automatic refresh background preset, was a build point and was never written to this cache. Delete once every
    //   supported upgrade starts after the 2026-10-29 release.
    private bool cacheFieldsMigrated;

    private void MigrateCacheFields()
    {
        if (cacheFieldsMigrated) return;
        if (File.Exists(cachePath))
        {
            JsonNode? root = null;
            try
            {
                using var stream = Configuration.SettingsFileReader.Open(cachePath);
                root = JsonNode.Parse(stream);
            }
            catch (JsonException) { } // The strict read below reports a damaged cache.
            var changed = root?["refresh"] is JsonObject refresh && refresh.Remove("smartMonitoringMode");
            if (root?["appearance"] is JsonObject appearance)
            {
                // 2026-10-26 删除读数来源设置，在严格读取前丢弃旧字段。
                changed |= appearance.Remove("readingSourceMode");
                changed |= MigrateAnimationAppearance(appearance);
                changed |= appearance.Remove("autoDisableAnimations");
            }
            changed |= MigrateNvmlFbUsedField(root);
            if (root?["overlays"] is JsonObject overlays)
                foreach (var (softwareId, overlay) in overlays.ToArray())
                    if (overlay is JsonObject presentation)
                        changed |= FrontendOverlayItems.MigrateLegacyPresentation(presentation, softwareId);
            changed |= root?["notifications"] is JsonObject notifications && notifications.Remove("updateInAppNotifiedVersion");
            if (changed) WriteAtomic(cachePath, JsonSerializer.SerializeToUtf8Bytes(root, Json));
        }
        cacheFieldsMigrated = true;
    }

    // Migration frontend-preferences-animation-style-v1 (docs/animation_style_2026-10-26.md).
    // Appearance written before 2026-10-26 carries animations (auto, normal, none, ultra) and
    // motionFeel (standard, smooth, crisp). They become animationStyle: none and ultra are copied
    // as the style; auto and normal take the style from the feel (smooth -> smooth; standard,
    // crisp or no feel -> desktop). A value that already has animationStyle only loses the old
    // fields. The frontend's browser-storage reader applies the same mapping. Delete with the next
    // release that no longer reads caches older than this one.
    private static bool MigrateAnimationAppearance(JsonObject appearance)
    {
        if (!appearance.ContainsKey("animations") && !appearance.ContainsKey("motionFeel")) return false;
        var animations = appearance["animations"] is JsonValue a && a.TryGetValue<string>(out var mode) ? mode : null;
        var feel = appearance["motionFeel"] is JsonValue f && f.TryGetValue<string>(out var feelId) ? feelId : null;
        appearance.Remove("animations");
        appearance.Remove("motionFeel");
        if (appearance.ContainsKey("animationStyle")) return true;
        appearance["animationStyle"] = animations is "none" or "ultra" ? animations
            : feel == "smooth" ? "smooth" : "desktop";
        return true;
    }

    // Migration frontend-preferences-nvml-fb-used-v1. On 2026-10-29 the backend renamed the NVML v2
    // used framebuffer field gpu_nvidia_fb_used_including_reserved_bytes to
    // gpu_nvidia_fb_used_excluding_reserved_bytes: the value never included the reserved part.
    // Dashboard card slots ("<field>@<device identity>") and overlay metric sources (fieldName)
    // saved before that carry the old name and take the new one; nothing else changes. The
    // frontend's browser-storage reader applies the same rename. Delete with the next release
    // that no longer reads caches older than this one.
    internal const string RetiredNvmlFbUsedField = "gpu_nvidia_fb_used_including_reserved_bytes";
    internal const string NvmlFbUsedField = "gpu_nvidia_fb_used_excluding_reserved_bytes";

    private static bool MigrateNvmlFbUsedField(JsonNode? root)
    {
        var changed = false;
        if (root?["dashboard"]?["cards"] is JsonArray cards)
        {
            foreach (var slot in cards.OfType<JsonObject>().Select(static card => card["slots"]).OfType<JsonArray>()
                .SelectMany(static slots => slots).OfType<JsonObject>())
            {
                if (slot["metricId"] is not JsonValue id || !id.TryGetValue<string>(out var metricId)
                    || !metricId.StartsWith(RetiredNvmlFbUsedField + "@", StringComparison.Ordinal)) continue;
                slot["metricId"] = NvmlFbUsedField + metricId[RetiredNvmlFbUsedField.Length..];
                changed = true;
            }
        }
        if (root?["overlays"] is JsonObject overlays)
        {
            foreach (var source in overlays.Select(static pair => pair.Value?["metricSources"]).OfType<JsonObject>()
                .SelectMany(static sources => sources).Select(static pair => pair.Value).OfType<JsonObject>())
            {
                if (source["fieldName"] is not JsonValue name || !name.TryGetValue<string>(out var fieldName)
                    || fieldName != RetiredNvmlFbUsedField) continue;
                source["fieldName"] = NvmlFbUsedField;
                changed = true;
            }
        }
        return changed;
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
                appearance.Remove("readingSourceMode");
                MigrateAnimationAppearance(appearance);
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
                // The legacy file stores metrics (metric IDs); they become items like the cache's old metrics.
                var legacy = item.DeepClone().AsObject();
                FrontendOverlayItems.MigrateLegacyPresentation(legacy, id);
                CopyKnown(legacy, presentation);
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
