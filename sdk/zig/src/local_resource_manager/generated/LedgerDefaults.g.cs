#nullable enable
using System.Collections.Immutable;
namespace @ResourceManager.@Ledger.@Configuration.@Generated;
public static class @LedgerDefaults
{
    public readonly record struct __fpContractSlot(
        string Kind, string? Unit, long? IntegerMinimum, long? IntegerMaximum, long? IntegerStep,
        ulong? UnsignedMinimum, ulong? UnsignedMaximum, ulong? UnsignedStep, string? UnsignedMaximumPoint,
        double? DecimalMinimum, double? DecimalMaximum, ImmutableArray<string> Options,
        bool ChoiceOptionsUnresolved, int RequiredSelectionCount);
    public sealed class __fpContractDescriptor
    {
        public string? ComplexContract { get; }
        public ImmutableArray<__fpContractSlot> Slots { get; }
        public __fpContractDescriptor(string? complexContract, ImmutableArray<__fpContractSlot> slots)
            => (ComplexContract, Slots) = (complexContract, slots);
    }
    [global::FreedomPoint.FreedomPointBinding(0x0001000E00000000UL)]
    public const ulong @activity_bitmap_width = @LedgerDefaultsValues.@__fpValue_activity_bitmap_width;
    [global::FreedomPoint.FreedomPointBinding(0x0001000E00000001UL)]
    public const ulong @uid_width_bits = @LedgerDefaultsValues.@__fpValue_uid_width_bits;
    [global::FreedomPoint.FreedomPointBinding(0x0001000E00040000UL)]
    public const ulong @table_0_activity_score_epochs = @LedgerDefaultsValues.@__fpValue_table_0_activity_score_epochs;
    [global::FreedomPoint.FreedomPointBinding(0x0001000E00040001UL)]
    public const ulong @table_0_idle_epochs = @LedgerDefaultsValues.@__fpValue_table_0_idle_epochs;
    [global::FreedomPoint.FreedomPointBinding(0x0001000E00040002UL)]
    public const ulong @table_0_concentrated_trigger_percent = @LedgerDefaultsValues.@__fpValue_table_0_concentrated_trigger_percent;
    [global::FreedomPoint.FreedomPointBinding(0x0001000E00040003UL)]
    public const ulong @table_0_concentrated_target_percent = @LedgerDefaultsValues.@__fpValue_table_0_concentrated_target_percent;
    [global::FreedomPoint.FreedomPointBinding(0x0001000E00040004UL)]
    public const ulong @table_0_smooth_trigger_percent = @LedgerDefaultsValues.@__fpValue_table_0_smooth_trigger_percent;
    [global::FreedomPoint.FreedomPointBinding(0x0001000E00040005UL)]
    public const ulong @table_0_smooth_emergency_percent = @LedgerDefaultsValues.@__fpValue_table_0_smooth_emergency_percent;
    [global::FreedomPoint.FreedomPointBinding(0x0001000E00040006UL)]
    public const ulong @table_0_smooth_interval_epochs = @LedgerDefaultsValues.@__fpValue_table_0_smooth_interval_epochs;
    [global::FreedomPoint.FreedomPointBinding(0x0001000E00040007UL)]
    public const ulong @table_0_smooth_release_count = @LedgerDefaultsValues.@__fpValue_table_0_smooth_release_count;
    [global::FreedomPoint.FreedomPointBinding(0x0001000E00040008UL)]
    public const ulong @table_0_tick_maximum_actions = @LedgerDefaultsValues.@__fpValue_table_0_tick_maximum_actions;
    [global::FreedomPoint.FreedomPointBinding(0x0001000E00040009UL)]
    public const ulong @table_0_release_check_enabled = @LedgerDefaultsValues.@__fpValue_table_0_release_check_enabled;
    [global::FreedomPoint.FreedomPointBinding(0x0001000E0004000AUL)]
    public const ulong @table_0_release_tolerance_parts_per_million = @LedgerDefaultsValues.@__fpValue_table_0_release_tolerance_parts_per_million;
    [global::FreedomPoint.FreedomPointBinding(0x0001000E0004000BUL)]
    public const ulong @table_0_release_tolerance_bytes = @LedgerDefaultsValues.@__fpValue_table_0_release_tolerance_bytes;
    [global::FreedomPoint.FreedomPointBinding(0x0001000E0004000CUL)]
    public const ulong @table_0_capacity_protection_method = @LedgerDefaultsValues.@__fpValue_table_0_capacity_protection_method;
    [global::FreedomPoint.FreedomPointBinding(0x0001000E00080000UL)]
    public const ulong @table_1_activity_score_epochs = @LedgerDefaultsValues.@__fpValue_table_1_activity_score_epochs;
    [global::FreedomPoint.FreedomPointBinding(0x0001000E00080001UL)]
    public const ulong @table_1_idle_epochs = @LedgerDefaultsValues.@__fpValue_table_1_idle_epochs;
    [global::FreedomPoint.FreedomPointBinding(0x0001000E00080002UL)]
    public const ulong @table_1_concentrated_trigger_percent = @LedgerDefaultsValues.@__fpValue_table_1_concentrated_trigger_percent;
    [global::FreedomPoint.FreedomPointBinding(0x0001000E00080003UL)]
    public const ulong @table_1_concentrated_target_percent = @LedgerDefaultsValues.@__fpValue_table_1_concentrated_target_percent;
    [global::FreedomPoint.FreedomPointBinding(0x0001000E00080004UL)]
    public const ulong @table_1_smooth_trigger_percent = @LedgerDefaultsValues.@__fpValue_table_1_smooth_trigger_percent;
    [global::FreedomPoint.FreedomPointBinding(0x0001000E00080005UL)]
    public const ulong @table_1_smooth_emergency_percent = @LedgerDefaultsValues.@__fpValue_table_1_smooth_emergency_percent;
    [global::FreedomPoint.FreedomPointBinding(0x0001000E00080006UL)]
    public const ulong @table_1_smooth_interval_epochs = @LedgerDefaultsValues.@__fpValue_table_1_smooth_interval_epochs;
    [global::FreedomPoint.FreedomPointBinding(0x0001000E00080007UL)]
    public const ulong @table_1_smooth_release_count = @LedgerDefaultsValues.@__fpValue_table_1_smooth_release_count;
    [global::FreedomPoint.FreedomPointBinding(0x0001000E00080008UL)]
    public const ulong @table_1_tick_maximum_actions = @LedgerDefaultsValues.@__fpValue_table_1_tick_maximum_actions;
    [global::FreedomPoint.FreedomPointBinding(0x0001000E00080009UL)]
    public const ulong @table_1_release_check_enabled = @LedgerDefaultsValues.@__fpValue_table_1_release_check_enabled;
    [global::FreedomPoint.FreedomPointBinding(0x0001000E0008000AUL)]
    public const ulong @table_1_release_tolerance_parts_per_million = @LedgerDefaultsValues.@__fpValue_table_1_release_tolerance_parts_per_million;
    [global::FreedomPoint.FreedomPointBinding(0x0001000E0008000BUL)]
    public const ulong @table_1_release_tolerance_bytes = @LedgerDefaultsValues.@__fpValue_table_1_release_tolerance_bytes;
    [global::FreedomPoint.FreedomPointBinding(0x0001000E0008000CUL)]
    public const ulong @table_1_capacity_protection_method = @LedgerDefaultsValues.@__fpValue_table_1_capacity_protection_method;
}
public sealed class @LedgerDefaultsRuntime
{
    private @LedgerDefaultsRuntime()
    {
    }
    public static @LedgerDefaultsRuntime Create() => new();
}
