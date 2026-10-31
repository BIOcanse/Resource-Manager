#nullable enable
using System.Collections.Immutable;
namespace @ResourceManager.@NativeUi.@Preferences;
public static class @FrontendPoints
{
    public readonly record struct __fpContractSlot(
        string Kind, string? Unit, long? IntegerMinimum, long? IntegerMaximum, long? IntegerStep, string? IntegerMinimumPoint,
        ulong? UnsignedMinimum, ulong? UnsignedMaximum, ulong? UnsignedStep, string? UnsignedMaximumPoint,
        double? DecimalMinimum, double? DecimalMaximum, ImmutableArray<string> Options,
        bool ChoiceOptionsUnresolved, int RequiredSelectionCount, int? IntegerBitWidth, __fpEnumContract? Enumeration);
    public readonly record struct __fpEnumMember(string Name, string Literal);
    public readonly record struct __fpEnumContract(bool IsUnsigned, int BitWidth, ImmutableArray<__fpEnumMember> Members);
    public sealed class __fpContractDescriptor
    {
        public string? ComplexContract { get; }
        public ImmutableArray<__fpContractSlot> Slots { get; }
        public __fpContractDescriptor(string? complexContract, ImmutableArray<__fpContractSlot> slots)
            => (ComplexContract, Slots) = (complexContract, slots);
    }
    [global::FreedomPoint.FreedomPointBinding(0x00010002003C0000UL)]
    public const int @BarFrameRate = @FrontendPointsValues.@__fpValue_BarFrameRate;
    [global::FreedomPoint.FreedomPointBinding(0x00010002003D0000UL)]
    public static readonly string @UiStyle = @FrontendPointsValues.@__fpValue_UiStyle;
    [global::FreedomPoint.FreedomPointBinding(0x00010002003D0001UL)]
    public static readonly string @Theme = @FrontendPointsValues.@__fpValue_Theme;
    [global::FreedomPoint.FreedomPointBinding(0x00010002003D0002UL)]
    public static readonly string @AnimationStyle = @FrontendPointsValues.@__fpValue_AnimationStyle;
    [global::FreedomPoint.FreedomPointBinding(0x00010002003D0003UL)]
    public static readonly string @BarFrameRateMode = @FrontendPointsValues.@__fpValue_BarFrameRateMode;
    [global::FreedomPoint.FreedomPointBinding(0x00010002003D0004UL)]
    public static readonly string @BarColorMode = @FrontendPointsValues.@__fpValue_BarColorMode;
    [global::FreedomPoint.FreedomPointBinding(0x00010002003D0005UL)]
    public static readonly string @FontSmoothing = @FrontendPointsValues.@__fpValue_FontSmoothing;
    [global::FreedomPoint.FreedomPointBinding(0x00010002003D0006UL)]
    public static readonly string @Language = @FrontendPointsValues.@__fpValue_Language;
    [global::FreedomPoint.FreedomPointBinding(0x00010002003D0007UL)]
    public static readonly string @ByteUnitMode = @FrontendPointsValues.@__fpValue_ByteUnitMode;
    [global::FreedomPoint.FreedomPointBinding(0x0001000200400000UL)]
    public const int @VisibilityCooldownMs = @FrontendPointsValues.@__fpValue_VisibilityCooldownMs;
    [global::FreedomPoint.FreedomPointBinding(0x0001000200400001UL)]
    public const int @ShellOpenTimeoutMs = @FrontendPointsValues.@__fpValue_ShellOpenTimeoutMs;
    [global::FreedomPoint.FreedomPointBinding(0x0001000200410000UL)]
    public static readonly PointRefreshSetting @Monitor = @FrontendPointsValues.@__fpValue_Monitor;
    [global::FreedomPoint.FreedomPointBinding(0x0001000200410001UL)]
    public static readonly PointRefreshSetting @ResourceTable = @FrontendPointsValues.@__fpValue_ResourceTable;
    [global::FreedomPoint.FreedomPointBinding(0x0001000200410002UL)]
    public static readonly PointRefreshSetting @LocalSystem = @FrontendPointsValues.@__fpValue_LocalSystem;
    [global::FreedomPoint.FreedomPointBinding(0x0001000200410003UL)]
    public static readonly PointRefreshPresets @MonitorPresets = @FrontendPointsValues.@__fpValue_MonitorPresets;
    [global::FreedomPoint.FreedomPointBinding(0x0001000200410004UL)]
    public static readonly PointRefreshPresets @ResourceTablePresets = @FrontendPointsValues.@__fpValue_ResourceTablePresets;
    [global::FreedomPoint.FreedomPointBinding(0x0001000200410005UL)]
    public static readonly PointRefreshPresets @LocalSystemPresets = @FrontendPointsValues.@__fpValue_LocalSystemPresets;
    [global::FreedomPoint.FreedomPointBinding(0x0001000200410006UL)]
    public static readonly string @AutoForegroundPreset = @FrontendPointsValues.@__fpValue_AutoForegroundPreset;
    [global::FreedomPoint.FreedomPointBinding(0x0001000200410007UL)]
    public static readonly ImmutableArray<string> @KeptPages = @FrontendPointsValues.@__fpValue_KeptPages;
    [global::FreedomPoint.FreedomPointBinding(0x0001000200480000UL)]
    public const int @TableColumnResizeKeyStep = @FrontendPointsValues.@__fpValue_TableColumnResizeKeyStep;
    [global::FreedomPoint.FreedomPointBinding(0x0001000200480001UL)]
    public const int @TableColumnFitPadding = @FrontendPointsValues.@__fpValue_TableColumnFitPadding;
    [global::FreedomPoint.FreedomPointBinding(0x0001000200490000UL)]
    public static readonly global::System.Text.Json.JsonElement @DashboardLayoutDefaults = @FrontendPointsValues.@__fpValue_DashboardLayoutDefaults;
    [global::FreedomPoint.FreedomPointBinding(0x0001000200490001UL)]
    public static readonly PointDashboardLayoutLimits @DashboardLayoutLimits = @FrontendPointsValues.@__fpValue_DashboardLayoutLimits;
    [global::FreedomPoint.FreedomPointBinding(0x0001000200490002UL)]
    public static readonly string @TableColumnNewVisible = @FrontendPointsValues.@__fpValue_TableColumnNewVisible;
    [global::FreedomPoint.FreedomPointBinding(0x0001000200490003UL)]
    public static readonly string @TableColumnSourceSelection = @FrontendPointsValues.@__fpValue_TableColumnSourceSelection;
    [global::FreedomPoint.FreedomPointBinding(0x00010002004D0000UL)]
    public static readonly string @UpdateInApp = @FrontendPointsValues.@__fpValue_UpdateInApp;
    [global::FreedomPoint.FreedomPointBinding(0x00010002004D0001UL)]
    public static readonly string @UpdateSystem = @FrontendPointsValues.@__fpValue_UpdateSystem;
    [global::FreedomPoint.FreedomPointBinding(0x00010002004D0002UL)]
    public static readonly string @AccessRequestSystem = @FrontendPointsValues.@__fpValue_AccessRequestSystem;
    [global::FreedomPoint.FreedomPointBinding(0x00010002004D0003UL)]
    public static readonly string @MemoryProtectionSystem = @FrontendPointsValues.@__fpValue_MemoryProtectionSystem;
    [global::FreedomPoint.FreedomPointBinding(0x00010002004D0004UL)]
    public static readonly string @UpdateNotify = @FrontendPointsValues.@__fpValue_UpdateNotify;
    [global::FreedomPoint.FreedomPointBinding(0x00010002004D0005UL)]
    public static readonly string @AccessRequestNotify = @FrontendPointsValues.@__fpValue_AccessRequestNotify;
    [global::FreedomPoint.FreedomPointBinding(0x00010002004D0006UL)]
    public static readonly string @AccessRequestInApp = @FrontendPointsValues.@__fpValue_AccessRequestInApp;
    [global::FreedomPoint.FreedomPointBinding(0x00010002004D0007UL)]
    public static readonly string @MemoryProtectionNotify = @FrontendPointsValues.@__fpValue_MemoryProtectionNotify;
    [global::FreedomPoint.FreedomPointBinding(0x00010002004D0008UL)]
    public static readonly string @MemoryProtectionInApp = @FrontendPointsValues.@__fpValue_MemoryProtectionInApp;
    [global::FreedomPoint.FreedomPointBinding(0x00010002004D0009UL)]
    public static readonly string @CrashReportNotify = @FrontendPointsValues.@__fpValue_CrashReportNotify;
    [global::FreedomPoint.FreedomPointBinding(0x00010002004D000AUL)]
    public static readonly string @CrashReportSystem = @FrontendPointsValues.@__fpValue_CrashReportSystem;
    [global::FreedomPoint.FreedomPointBinding(0x00010002004D000BUL)]
    public static readonly string @CrashReportInApp = @FrontendPointsValues.@__fpValue_CrashReportInApp;
}
public sealed class @FrontendPointsRuntime
{
    private @FrontendPointsRuntime()
    {
    }
    public static @FrontendPointsRuntime Create() => new();
}
public sealed record PointDashboardLayoutLimits(int SmallMetricLimit, int CardUnits, int MinimumColumnWidth, int MaximumColumnWidth, int DefaultColumnWidth);
public sealed record PointRefreshPresets(int Responsive, int Balanced, int LowPower, int Quiet);
public sealed record PointRefreshSetting(string Mode, string Preset, int CustomValue);
