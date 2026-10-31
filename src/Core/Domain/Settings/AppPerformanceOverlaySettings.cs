using System.Text.Json.Serialization;

namespace ResourceManager.App.Domain.Settings;

/// <summary>
/// 性能浮窗 per software (docs/frontend_block_trees_2026-10-26.md 「浮窗设置归后端」). The backend owns the whole
/// setting; the frontend page and the resident read it from <c>/api/settings/app</c>. A software without an
/// entry has no overlay. Software ids are compared ignoring case.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AppPerformanceOverlaySettings(
    IReadOnlyDictionary<string, AppPerformanceOverlaySoftwareSettings> Software);

/// <summary>
/// One software's overlay. On the wire every field is present after normalization: a field a writer leaves
/// out (null here) is filled from the build's <c>performance_overlay</c> freedom point. Items use the targeted
/// recording item contract (kind frames, metric or software) and are drawn in this order.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AppPerformanceOverlaySoftwareSettings(
    bool? Enabled = null,
    string? Mode = null,
    IReadOnlyList<TargetedRecordingItem>? Items = null,
    string? SizeMode = null,
    int? FontSizePx = null,
    double? RegionWidthRatio = null,
    double? RegionHeightRatio = null,
    string? Anchor = null,
    int? MarginPx = null);

public static class AppPerformanceOverlayModes
{
    public const string External = "external";
    public const string Injected = "injected";
    public static readonly IReadOnlyList<string> All = [External, Injected];
}

public static class AppPerformanceOverlaySizeModes
{
    public const string AbsolutePixels = "absolutePixels";
    public const string WindowRatio = "windowRatio";
    public static readonly IReadOnlyList<string> All = [AbsolutePixels, WindowRatio];
}

public static class AppPerformanceOverlayAnchors
{
    public static readonly IReadOnlyList<string> All = ["topLeft", "topRight", "bottomLeft", "bottomRight"];
}

/// <summary>The build's overlay choices (freedom point <c>monitoring/performance_overlay</c>): the values an
/// omitted field takes, the accepted ranges, and the toggle hotkey's initial state.</summary>
public sealed record PerformanceOverlayDefaults(
    bool Enabled,
    string Mode,
    IReadOnlyList<TargetedRecordingItem> Items,
    string SizeMode,
    int FontSizePx,
    double RegionWidthRatio,
    double RegionHeightRatio,
    string Anchor,
    int MarginPx,
    int FontSizePxMinimum,
    int FontSizePxMaximum,
    int MarginPxMinimum,
    int MarginPxMaximum,
    double RegionRatioMinimum,
    double RegionRatioMaximum,
    double RegionRatioStep,
    int MaximumSoftware,
    int MaximumItems,
    bool ToggleHotkeyEnabled,
    IReadOnlyList<int> ToggleHotkeyEncoding)
{
    public AppPerformanceOverlaySoftwareSettings Entry() => new(Enabled, Mode, Items.ToArray(), SizeMode, FontSizePx,
        RegionWidthRatio, RegionHeightRatio, Anchor, MarginPx);

    /// <summary>The omitted fields of <paramref name="entry"/> filled from these defaults.</summary>
    public AppPerformanceOverlaySoftwareSettings Complete(AppPerformanceOverlaySoftwareSettings? entry) => new(
        entry?.Enabled ?? Enabled,
        entry?.Mode ?? Mode,
        (entry?.Items ?? Items).ToArray(),
        entry?.SizeMode ?? SizeMode,
        entry?.FontSizePx ?? FontSizePx,
        entry?.RegionWidthRatio ?? RegionWidthRatio,
        entry?.RegionHeightRatio ?? RegionHeightRatio,
        entry?.Anchor ?? Anchor,
        entry?.MarginPx ?? MarginPx);

    /// <summary>Why a normalized section cannot be saved, or null. Reason codes are
    /// <c>performance-overlay-*</c>; the settings write gate returns them before commit.</summary>
    public string? Validate(AppPerformanceOverlaySettings? settings)
    {
        if (settings is null) return null;
        if (settings.Software is null) return "performance-overlay-invalid";
        if (settings.Software.Count > MaximumSoftware) return "performance-overlay-too-many-software";
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, entry) in settings.Software)
        {
            if (string.IsNullOrWhiteSpace(id) || id != id.Trim()) return "performance-overlay-invalid-software-id";
            if (!ids.Add(id)) return "performance-overlay-duplicate-software-id";
            if (ValidateEntry(entry) is { } reason) return reason;
        }
        return null;
    }

    public string? ValidateEntry(AppPerformanceOverlaySoftwareSettings? entry)
    {
        if (entry is null || entry.Enabled is null || entry.Items is null || entry.FontSizePx is null || entry.MarginPx is null
            || entry.RegionWidthRatio is null || entry.RegionHeightRatio is null)
            return "performance-overlay-incomplete";
        if (!AppPerformanceOverlayModes.All.Contains(entry.Mode, StringComparer.Ordinal)) return "performance-overlay-invalid-mode";
        if (!AppPerformanceOverlaySizeModes.All.Contains(entry.SizeMode, StringComparer.Ordinal)) return "performance-overlay-invalid-size-mode";
        if (!AppPerformanceOverlayAnchors.All.Contains(entry.Anchor, StringComparer.Ordinal)) return "performance-overlay-invalid-anchor";
        if (entry.FontSizePx < FontSizePxMinimum || entry.FontSizePx > FontSizePxMaximum) return "performance-overlay-font-size-out-of-range";
        if (entry.MarginPx < MarginPxMinimum || entry.MarginPx > MarginPxMaximum) return "performance-overlay-margin-out-of-range";
        if (!InRatio(entry.RegionWidthRatio.Value) || !InRatio(entry.RegionHeightRatio.Value)) return "performance-overlay-region-out-of-range";
        if (entry.Items.Count > MaximumItems) return "performance-overlay-too-many-items";
        var keys = new HashSet<(string, string)>();
        foreach (var item in entry.Items)
        {
            if (TargetedRecordingProfileShape.ValidateItem(item) is not null) return "performance-overlay-invalid-item";
            if (!keys.Add((item.Kind, item.Id.ToLowerInvariant()))) return "performance-overlay-duplicate-item";
        }
        return null;
    }

    private bool InRatio(double value) => double.IsFinite(value) && value >= RegionRatioMinimum && value <= RegionRatioMaximum;
}
