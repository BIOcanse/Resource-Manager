using System.Text.Json;

namespace ResourceManager.App.Infrastructure.Settings;

/// <summary>
/// One-time file migrations frontend-preferences-v1 and frontend-dashboard-v1.
/// Retire each removal/import pair once supported upgrades no longer begin at
/// app schema 1.0.31 or earlier, overlay v1/v2, or dashboard schema 16 or earlier.
/// The backend never reads the frontend cache or its import marker. Keep the raw
/// backup until the supported frontend upgrade paths no longer need the importer.
/// </summary>
internal static class PresentationPreferenceRemoval
{
    internal const string BackupSuffix = ".frontend-preferences-v1.bak";
    internal const string DashboardBackupSuffix = ".frontend-dashboard-v1.bak";
    internal static readonly string[] DashboardFields =
        ["cards", "resourceBars", "resourceTableColumns", "resourceTableProcessColumns", "usesFactoryDefaults"];
    internal static bool HasDashboardFields(JsonElement root) => root.ValueKind == JsonValueKind.Object
        && DashboardFields.Any(field => root.TryGetProperty(field, out _));
    internal static readonly string[] AppPerformanceFields =
        ["smartMonitoringMode", "monitorRefreshIntervalMs", "resourceTableRefreshIntervalMs", "localSystemRefreshIntervalMs"];
    internal static readonly string[] OverlayFields =
        ["metrics", "sizeMode", "fontSizePx", "regionWidthRatio", "regionHeightRatio", "anchor", "marginPx"];

    internal static bool HasAppFields(JsonElement root) => root.ValueKind == JsonValueKind.Object
        && (root.TryGetProperty("appearance", out _)
            || root.TryGetProperty("performance", out var performance) && performance.ValueKind == JsonValueKind.Object
                && AppPerformanceFields.Any(field => performance.TryGetProperty(field, out _)));

    internal static bool HasOverlayFields(JsonElement root) => root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty("software", out var software) && software.ValueKind == JsonValueKind.Array
        && software.EnumerateArray().Any(item => item.ValueKind == JsonValueKind.Object
            && OverlayFields.Any(field => item.TryGetProperty(field, out _)));

    internal static void PreserveLegacyImage(string path, byte[] bytes, Func<JsonElement, bool> containsLegacy,
        string suffix = BackupSuffix)
    {
        using var document = JsonDocument.Parse(bytes);
        if (!containsLegacy(document.RootElement)) return;
        var backup = path + suffix;
        if (File.Exists(backup)) return;
        var temporary = backup + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            try { File.Move(temporary, backup); }
            catch (IOException) when (File.Exists(backup)) { }
            // The raw backup is committed before the owner's normal atomic replacement.
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
