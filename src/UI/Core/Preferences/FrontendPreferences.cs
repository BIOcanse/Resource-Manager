using ResourceManager.Shared.Appearance;
using System.Text.Json;

namespace ResourceManager.NativeUi.Preferences;

// These are frontend cache data, not backend settings or new points per software.
internal sealed record FrontendAppearance
{
    public string UiStyle { get; init; } = FrontendPoints.UiStyle;
    public string Theme { get; init; } = FrontendPoints.Theme;
    public string Animations { get; init; } = FrontendPoints.AnimationMode;
    public string MotionFeel { get; init; } = FrontendPoints.MotionFeel;
    public string ResourceBarFrameRateMode { get; init; } = FrontendPoints.BarFrameRateMode;
    public int ResourceBarFrameRate { get; init; } = FrontendPoints.BarFrameRate;
    public string BarColorMode { get; init; } = FrontendPoints.BarColorMode;
    public string FontSmoothing { get; init; } = FrontendPoints.FontSmoothing;
    public string Language { get; init; } = FrontendPoints.Language;
    public string ByteUnitMode { get; init; } = FrontendPoints.ByteUnitMode;
    public string ReadingSourceMode { get; init; } = FrontendPoints.ReadingSourceMode;

    internal void Validate()
    {
        var style = UiStyleCatalog.Normalize(UiStyle, Theme);
        if (style.UiStyle != UiStyle || style.Theme != Theme
            || Animations is not ("auto" or "none" or "normal" or "ultra")
            || MotionFeel is not ("standard" or "smooth" or "crisp")
            || ResourceBarFrameRateMode is not ("display" or "custom")
            || ResourceBarFrameRate is < 10 or > 240
            || BarColorMode is not ("type" or "distinct")
            || FontSmoothing is not ("auto" or "system" or "grayscale" or "disabled")
            || string.IsNullOrWhiteSpace(Language) || Language.Length > 32
            || ByteUnitMode is not ("native" or "binary" or "decimal")
            || ReadingSourceMode is not ("preferred" or "all"))
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

// 更新与访问请求的通知开关，以及界面内更新提示已提示过的版本。
// 已提示版本只由网页前端写入；原生壳校验、保存并读取系统通知开关。
internal sealed record FrontendNotifications
{
    public string UpdateInApp { get; init; } = FrontendPoints.UpdateInApp;
    public string UpdateSystem { get; init; } = FrontendPoints.UpdateSystem;
    public string AccessRequestSystem { get; init; } = FrontendPoints.AccessRequestSystem;
    public string? UpdateInAppNotifiedVersion { get; init; }

    internal void Validate()
    {
        if (UpdateInApp is not ("on" or "off") || UpdateSystem is not ("on" or "off")
            || AccessRequestSystem is not ("on" or "off")
            || UpdateInAppNotifiedVersion is not null
                && (string.IsNullOrWhiteSpace(UpdateInAppNotifiedVersion) || UpdateInAppNotifiedVersion.Length > 64))
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
