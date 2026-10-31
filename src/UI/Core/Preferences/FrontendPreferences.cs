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
    public string CrashReportNotify { get; init; } = FrontendPoints.CrashReportNotify;
    public string CrashReportInApp { get; init; } = FrontendPoints.CrashReportInApp;
    public string CrashReportSystem { get; init; } = FrontendPoints.CrashReportSystem;

    internal void Validate()
    {
        if (new[] { UpdateNotify, UpdateInApp, UpdateSystem, AccessRequestNotify, AccessRequestInApp, AccessRequestSystem,
                MemoryProtectionNotify, MemoryProtectionInApp, MemoryProtectionSystem,
                CrashReportNotify, CrashReportInApp, CrashReportSystem }.Any(value => value is not ("on" or "off")))
            throw new InvalidDataException("Invalid frontend notification cache.");
    }
}

// The per-software overlay settings are not frontend preferences: since 2026-10-31 they are the backend setting
// performanceOverlay (user: 「前端几乎不拥有任何功能，他只是一个使用后端的软件而已」). Overlay entries an older cache
// still carries are kept out of this record by FrontendPreferencesOwner until the window process has written them to
// the backend (Migration frontend-preferences-overlays-to-backend-v1, FrontendPreferencesOwner.OverlayMigration.cs).
internal sealed record FrontendPreferences
{
    // Marker of the frontend-preferences-v1 import of the backend-era overlay file (ImportLegacyOverlayChoices): a cache
    // written by this owner always carries 1, so that import never runs again over a cache the user has used.
    public int OverlaySelectionVersion { get; init; } = 1;
    public FrontendAppearance Appearance { get; init; } = new();
    public FrontendRefresh Refresh { get; init; } = new();
    public FrontendNotifications Notifications { get; init; } = new();
    public JsonElement? Dashboard { get; init; }
    public JsonElement? TargetedReportViewTemplates { get; init; }

    internal void Validate()
    {
        if (OverlaySelectionVersion != 1 || Appearance is null || Refresh is null || Notifications is null)
            throw new InvalidDataException("Invalid frontend preferences cache.");
        Appearance.Validate();
        Refresh.Validate();
        Notifications.Validate();
        FrontendDashboardCache.Validate(Dashboard);
        FrontendReportViewTemplatesCache.Validate(TargetedReportViewTemplates);
    }
}
