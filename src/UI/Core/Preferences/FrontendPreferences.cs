using ResourceManager.Shared.Appearance;
using System.Text.Json;

namespace ResourceManager.NativeUi.Preferences;

// These are frontend cache data, not backend settings or new points per software.
internal sealed record FrontendAppearance
{
    public string UiStyle { get; init; } = FrontendPoints.UiStyle;
    public string Theme { get; init; } = FrontendPoints.Theme;
    public string AnimationStyle { get; init; } = FrontendPoints.AnimationStyle;
    public string ResourceBarFrameRateMode { get; init; } = FrontendPoints.BarFrameRateMode;
    public int ResourceBarFrameRate { get; init; } = FrontendPoints.BarFrameRate;
    public string BarColorMode { get; init; } = FrontendPoints.BarColorMode;
    public string FontSmoothing { get; init; } = FrontendPoints.FontSmoothing;
    public string Language { get; init; } = FrontendPoints.Language;
    public string ByteUnitMode { get; init; } = FrontendPoints.ByteUnitMode;

    internal void Validate()
    {
        var style = UiStyleCatalog.Normalize(UiStyle, Theme);
        if (style.UiStyle != UiStyle || style.Theme != Theme
            || AnimationStyle is not ("smooth" or "desktop" or "terminal" or "none" or "ultra")
            || ResourceBarFrameRateMode is not ("display" or "custom")
            || ResourceBarFrameRate is < 10 or > 240
            || BarColorMode is not ("type" or "distinct")
            || FontSmoothing is not ("auto" or "system" or "grayscale" or "disabled")
            || string.IsNullOrWhiteSpace(Language) || Language.Length > 32
            || ByteUnitMode is not ("native" or "binary" or "decimal"))
            throw new InvalidDataException("Invalid frontend appearance cache.");
    }
}

internal sealed record FrontendRefreshSetting(string Mode, string Preset, int CustomValue)
{
    internal void Validate()
    {
        if (Mode is not ("aotu" or "preset" or "custom")
            || Preset is not ("responsive" or "balanced" or "lowPower" or "quiet")
            || CustomValue is < 500 or > 300000)
            throw new InvalidDataException("Invalid frontend refresh cache.");
    }
}

internal sealed record FrontendRefresh
{
    public FrontendRefreshSetting Monitor { get; init; } = new(
        FrontendPoints.Monitor.Mode, FrontendPoints.Monitor.Preset, FrontendPoints.Monitor.CustomValue);
    public FrontendRefreshSetting ResourceTable { get; init; } = new(
        FrontendPoints.ResourceTable.Mode, FrontendPoints.ResourceTable.Preset, FrontendPoints.ResourceTable.CustomValue);
    public FrontendRefreshSetting LocalSystem { get; init; } = new(
        FrontendPoints.LocalSystem.Mode, FrontendPoints.LocalSystem.Preset, FrontendPoints.LocalSystem.CustomValue);

    internal void Validate()
    {
        if (Monitor is null || ResourceTable is null || LocalSystem is null)
            throw new InvalidDataException("Invalid frontend refresh cache.");
        Monitor.Validate();
        ResourceTable.Validate();
        LocalSystem.Validate();
    }
}

// The source an overlay item reads instead of its part address: one backend raw field on one
// device (docs/metric_item_source_choice_2026-10-25.md). Absent means automatic.
internal sealed record FrontendOverlayMetricSource(string FieldName, string DeviceIdentity);

internal sealed record FrontendOverlayPresentation
{
    public bool Enabled { get; init; }
    public string Mode { get; init; } = "external";
    public IReadOnlyList<string> Metrics { get; init; } = FrontendPoints.MetricOrder;
    // Per-item source choices keyed by the item's metric ID; items without one are automatic.
    public IReadOnlyDictionary<string, FrontendOverlayMetricSource> MetricSources { get; init; } =
        new Dictionary<string, FrontendOverlayMetricSource>(StringComparer.Ordinal);
    public string SizeMode { get; init; } = FrontendPoints.SizeMode;
    public int FontSizePx { get; init; } = FrontendPoints.FontSizePx;
    public double RegionWidthRatio { get; init; } = FrontendPoints.RegionWidthRatio;
    public double RegionHeightRatio { get; init; } = FrontendPoints.RegionHeightRatio;
    public string Anchor { get; init; } = FrontendPoints.Anchor;
    public int MarginPx { get; init; } = FrontendPoints.MarginPx;

    internal void Validate()
    {
        if (Mode is not ("external" or "injected") || Metrics is null || Metrics.Count > 64
            || Metrics.Any(static id => string.IsNullOrWhiteSpace(id) || id.Length > 128)
            || Metrics.Distinct(StringComparer.OrdinalIgnoreCase).Count() != Metrics.Count
            || SizeMode is not ("absolutePixels" or "windowRatio")
            || FontSizePx is < 8 or > 72 || MarginPx is < 0 or > 256
            || !double.IsFinite(RegionWidthRatio) || RegionWidthRatio is < 0.05 or > 1
            || !double.IsFinite(RegionHeightRatio) || RegionHeightRatio is < 0.05 or > 1
            || Anchor is not ("topLeft" or "topRight" or "bottomLeft" or "bottomRight")
            || MetricSources is null
            || MetricSources.Any(pair => !Metrics.Contains(pair.Key, StringComparer.Ordinal) || pair.Value is null
                || string.IsNullOrWhiteSpace(pair.Value.FieldName) || string.IsNullOrWhiteSpace(pair.Value.DeviceIdentity)))
            throw new InvalidDataException("Invalid frontend overlay cache.");
    }

    internal FrontendOverlayMetricSource? SourceFor(string metricId) =>
        MetricSources.TryGetValue(metricId, out var source) ? source : null;
}

// 每类通知三项（docs/notifications_2026-10-29.md“设置在前端”）：是否弹（Notify）、Windows 通知（System，托盘读）、
// 软件内提示（InApp，网页前端读）。总开关关掉时两条渠道都不弹，两条渠道各自的选择保留。
internal sealed record FrontendNotifications
{
    public string UpdateNotify { get; init; } = FrontendPoints.UpdateNotify;
    public string UpdateInApp { get; init; } = FrontendPoints.UpdateInApp;
    public string UpdateSystem { get; init; } = FrontendPoints.UpdateSystem;
    public string AccessRequestNotify { get; init; } = FrontendPoints.AccessRequestNotify;
    public string AccessRequestInApp { get; init; } = FrontendPoints.AccessRequestInApp;
    public string AccessRequestSystem { get; init; } = FrontendPoints.AccessRequestSystem;
    public string MemoryProtectionNotify { get; init; } = FrontendPoints.MemoryProtectionNotify;
    public string MemoryProtectionInApp { get; init; } = FrontendPoints.MemoryProtectionInApp;
    public string MemoryProtectionSystem { get; init; } = FrontendPoints.MemoryProtectionSystem;

    internal void Validate()
    {
        if (new[] { UpdateNotify, UpdateInApp, UpdateSystem, AccessRequestNotify, AccessRequestInApp, AccessRequestSystem,
                MemoryProtectionNotify, MemoryProtectionInApp, MemoryProtectionSystem }.Any(value => value is not ("on" or "off")))
            throw new InvalidDataException("Invalid frontend notification cache.");
    }
}

internal sealed record FrontendPreferences
{
    public int OverlaySelectionVersion { get; init; } = 1;
    public FrontendAppearance Appearance { get; init; } = new();
    public FrontendRefresh Refresh { get; init; } = new();
    public FrontendNotifications Notifications { get; init; } = new();
    public JsonElement? Dashboard { get; init; }
    public IReadOnlyDictionary<string, FrontendOverlayPresentation> Overlays { get; init; } =
        new Dictionary<string, FrontendOverlayPresentation>(StringComparer.OrdinalIgnoreCase);

    internal void Validate()
    {
        if (OverlaySelectionVersion != 1 || Appearance is null || Refresh is null || Notifications is null || Overlays is null)
            throw new InvalidDataException("Invalid frontend preferences cache.");
        Appearance.Validate();
        Refresh.Validate();
        Notifications.Validate();
        FrontendDashboardCache.Validate(Dashboard);
        foreach (var (id, overlay) in Overlays)
        {
            if (string.IsNullOrWhiteSpace(id) || overlay is null)
                throw new InvalidDataException("Invalid frontend overlay cache key.");
            overlay.Validate();
        }
    }
}
