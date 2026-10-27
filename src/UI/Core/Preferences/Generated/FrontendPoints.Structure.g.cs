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
    [global::FreedomPoint.FreedomPointBinding(0x0001000200280000UL)]
    public const int @BarFrameRate = @FrontendPointsValues.@__fpValue_BarFrameRate;
    [global::FreedomPoint.FreedomPointBinding(0x0001000200290000UL)]
    public static readonly string @UiStyle = @FrontendPointsValues.@__fpValue_UiStyle;
    [global::FreedomPoint.FreedomPointBinding(0x0001000200290001UL)]
    public static readonly string @Theme = @FrontendPointsValues.@__fpValue_Theme;
    [global::FreedomPoint.FreedomPointBinding(0x0001000200290002UL)]
    public static readonly string @AnimationStyle = @FrontendPointsValues.@__fpValue_AnimationStyle;
    [global::FreedomPoint.FreedomPointBinding(0x0001000200290003UL)]
    public static readonly bool @AutoDisableAnimations = @FrontendPointsValues.@__fpValue_AutoDisableAnimations;
    [global::FreedomPoint.FreedomPointBinding(0x0001000200290004UL)]
    public static readonly string @BarFrameRateMode = @FrontendPointsValues.@__fpValue_BarFrameRateMode;
    [global::FreedomPoint.FreedomPointBinding(0x0001000200290005UL)]
    public static readonly string @BarColorMode = @FrontendPointsValues.@__fpValue_BarColorMode;
    [global::FreedomPoint.FreedomPointBinding(0x0001000200290006UL)]
    public static readonly string @FontSmoothing = @FrontendPointsValues.@__fpValue_FontSmoothing;
    [global::FreedomPoint.FreedomPointBinding(0x0001000200290007UL)]
    public static readonly string @Language = @FrontendPointsValues.@__fpValue_Language;
    [global::FreedomPoint.FreedomPointBinding(0x0001000200290008UL)]
    public static readonly string @ByteUnitMode = @FrontendPointsValues.@__fpValue_ByteUnitMode;
    [global::FreedomPoint.FreedomPointBinding(0x00010002002C0000UL)]
    public const int @VisibilityCooldownMs = @FrontendPointsValues.@__fpValue_VisibilityCooldownMs;
    [global::FreedomPoint.FreedomPointBinding(0x00010002002D0000UL)]
    public static readonly PointRefreshSetting @Monitor = @FrontendPointsValues.@__fpValue_Monitor;
    [global::FreedomPoint.FreedomPointBinding(0x00010002002D0001UL)]
    public static readonly PointRefreshSetting @ResourceTable = @FrontendPointsValues.@__fpValue_ResourceTable;
    [global::FreedomPoint.FreedomPointBinding(0x00010002002D0002UL)]
    public static readonly PointRefreshSetting @LocalSystem = @FrontendPointsValues.@__fpValue_LocalSystem;
    [global::FreedomPoint.FreedomPointBinding(0x00010002002D0003UL)]
    public static readonly PointRefreshPresets @MonitorPresets = @FrontendPointsValues.@__fpValue_MonitorPresets;
    [global::FreedomPoint.FreedomPointBinding(0x00010002002D0004UL)]
    public static readonly PointRefreshPresets @ResourceTablePresets = @FrontendPointsValues.@__fpValue_ResourceTablePresets;
    [global::FreedomPoint.FreedomPointBinding(0x00010002002D0005UL)]
    public static readonly PointRefreshPresets @LocalSystemPresets = @FrontendPointsValues.@__fpValue_LocalSystemPresets;
    [global::FreedomPoint.FreedomPointBinding(0x00010002002D0006UL)]
    public static readonly string @AutoForegroundPreset = @FrontendPointsValues.@__fpValue_AutoForegroundPreset;
    [global::FreedomPoint.FreedomPointBinding(0x00010002002D0007UL)]
    public static readonly string @AutoBackgroundPreset = @FrontendPointsValues.@__fpValue_AutoBackgroundPreset;
    [global::FreedomPoint.FreedomPointBinding(0x0001000200300000UL)]
    public const int @FontSizePx = @FrontendPointsValues.@__fpValue_FontSizePx;
    [global::FreedomPoint.FreedomPointBinding(0x0001000200300001UL)]
    public const double @RegionWidthRatio = @FrontendPointsValues.@__fpValue_RegionWidthRatio;
    [global::FreedomPoint.FreedomPointBinding(0x0001000200300002UL)]
    public const double @RegionHeightRatio = @FrontendPointsValues.@__fpValue_RegionHeightRatio;
    [global::FreedomPoint.FreedomPointBinding(0x0001000200300003UL)]
    public const int @MarginPx = @FrontendPointsValues.@__fpValue_MarginPx;
    [global::FreedomPoint.FreedomPointBinding(0x0001000200310000UL)]
    public static readonly string @SizeMode = @FrontendPointsValues.@__fpValue_SizeMode;
    [global::FreedomPoint.FreedomPointBinding(0x0001000200310001UL)]
    public static readonly string @Anchor = @FrontendPointsValues.@__fpValue_Anchor;
    [global::FreedomPoint.FreedomPointBinding(0x0001000200310002UL)]
    public static readonly ImmutableArray<string> @MetricOrder = @FrontendPointsValues.@__fpValue_MetricOrder;
    [global::FreedomPoint.FreedomPointBinding(0x0001000200350000UL)]
    public static readonly global::System.Text.Json.JsonElement @DashboardLayoutDefaults = @FrontendPointsValues.@__fpValue_DashboardLayoutDefaults;
    [global::FreedomPoint.FreedomPointBinding(0x0001000200350001UL)]
    public static readonly PointDashboardLayoutLimits @DashboardLayoutLimits = @FrontendPointsValues.@__fpValue_DashboardLayoutLimits;
    [global::FreedomPoint.FreedomPointBinding(0x0001000200390000UL)]
    public static readonly string @UpdateInApp = @FrontendPointsValues.@__fpValue_UpdateInApp;
    [global::FreedomPoint.FreedomPointBinding(0x0001000200390001UL)]
    public static readonly string @UpdateSystem = @FrontendPointsValues.@__fpValue_UpdateSystem;
    [global::FreedomPoint.FreedomPointBinding(0x0001000200390002UL)]
    public static readonly string @AccessRequestSystem = @FrontendPointsValues.@__fpValue_AccessRequestSystem;
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
