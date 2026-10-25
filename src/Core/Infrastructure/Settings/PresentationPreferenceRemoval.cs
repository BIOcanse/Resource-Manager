using System.Text.Json;

namespace ResourceManager.App.Infrastructure.Settings;

/// <summary>
/// One-time file migration frontend-preferences-v1. Delete after the minimum
/// supported settings schema is newer than 1.0.31 and legacy overlay v1 is retired.
/// The backend never reads the frontend cache or its import marker. Keep the raw
/// backup until the supported frontend upgrade paths no longer need the importer.
/// </summary>
internal static class PresentationPreferenceRemoval
{
    internal const string BackupSuffix = ".frontend-preferences-v1.bak";
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

    // Metrics removal awaits the protected projection owner; the six layout
    // fields can leave independently without changing the subscription.
    internal static bool HasOverlayLayoutFields(JsonElement root) => root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty("software", out var software) && software.ValueKind == JsonValueKind.Array
        && software.EnumerateArray().Any(item => item.ValueKind == JsonValueKind.Object
            && OverlayFields.Skip(1).Any(field => item.TryGetProperty(field, out _)));

    internal static void PreserveLegacyImage(string path, byte[] bytes, Func<JsonElement, bool> containsLegacy)
    {
        using var document = JsonDocument.Parse(bytes);
        if (!containsLegacy(document.RootElement)) return;
        var backup = path + BackupSuffix;
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
