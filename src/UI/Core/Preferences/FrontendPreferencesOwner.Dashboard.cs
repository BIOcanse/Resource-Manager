using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ResourceManager.NativeUi.Preferences;

internal sealed partial class FrontendPreferencesOwner
{
    internal const string DashboardBackupSuffix = ".frontend-dashboard-v1.bak";
    private readonly string? legacyDashboardPath;
    private readonly string dashboardImportMarkerPath;
    private bool dashboardInitialized;

    internal FrontendPreferences SaveDashboard(FrontendPreferences value)
    {
        lock (gate) return Save(Read() with { Dashboard = value.Dashboard });
    }

    // Migration frontend-dashboard-v1. Retire with the backend file migration
    // after all supported upgrades start beyond dashboard schema 16.
    private void InitializeDashboardImport()
    {
        if (dashboardInitialized) return;
        var current = pointValues;
        if (File.Exists(cachePath))
        {
            using var stream = Configuration.SettingsFileReader.Open(cachePath);
            current = JsonSerializer.Deserialize<FrontendPreferences>(stream, Json)
                ?? throw new InvalidDataException("Frontend preferences cache is empty.");
            current.Validate();
        }
        if (!File.Exists(dashboardImportMarkerPath) && current.Dashboard is null && legacyDashboardPath is not null)
        {
            var image = ReadLegacyDashboard(legacyDashboardPath);
            if (image is null)
            {
                var lastGood = Path.Combine(Path.GetDirectoryName(legacyDashboardPath)!,
                    Path.GetFileNameWithoutExtension(legacyDashboardPath) + ".last-good.json");
                image = ReadLegacyDashboard(lastGood);
                if (image is not null) Trace.TraceWarning("Imported legacy dashboard last-known-good: {0}", lastGood);
            }
            if (image is not null)
            {
                try
                {
                    using var document = JsonDocument.Parse(image.ToJsonString());
                    var next = current with { Dashboard = document.RootElement.Clone() };
                    next.Validate();
                    WriteAtomic(cachePath, JsonSerializer.SerializeToUtf8Bytes(next, Json));
                }
                catch (Exception error) when (error is JsonException or InvalidDataException)
                {
                    Trace.TraceWarning("Legacy dashboard import rejected; original is preserved: {0}", error.Message);
                }
            }
        }
        WriteDashboardImportMarker();
        dashboardInitialized = true;
    }

    private static JsonObject? ReadLegacyDashboard(string path)
    {
        foreach (var candidate in new[] { path + DashboardBackupSuffix, path, path + DashboardBackupSuffix })
        {
            try
            {
                using var stream = Configuration.SettingsFileReader.Open(candidate);
                if (JsonNode.Parse(stream) is JsonObject image && new[] { "cards", "resourceBars", "resourceTableColumns", "resourceTableProcessColumns" }
                    .Any(image.ContainsKey)) return image;
            }
            catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { }
            catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
            { Trace.TraceWarning("Legacy dashboard import could not read {0}: {1}", candidate, error.Message); }
        }
        return null;
    }

    private void WriteDashboardImportMarker()
    {
        if (!File.Exists(dashboardImportMarkerPath)) WriteAtomic(dashboardImportMarkerPath, "frontend-dashboard-v1"u8.ToArray());
    }
}
