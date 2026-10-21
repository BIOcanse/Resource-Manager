// Generated declaration structure.
pub const ContractSlot = struct {
    kind: []const u8,
    unit: ?[]const u8,
    integer_minimum: ?i64,
    integer_maximum: ?i64,
    integer_step: ?i64,
    unsigned_minimum: ?u64,
    unsigned_maximum: ?u64,
    unsigned_step: ?u64,
    unsigned_maximum_point: ?[]const u8,
    decimal_minimum: ?f64,
    decimal_maximum: ?f64,
    options: []const []const u8,
    choice_options_unresolved: bool,
    required_selection_count: usize,
};
pub const ContractDescriptor = struct {
    complex_contract: ?[]const u8,
    slots: []const ContractSlot,
};
pub fn Bind(comptime values: anytype) type {
    return struct {
        pub const @"activity_bitmap_width": u64 = values.@"activity_bitmap_width";
        pub const @"uid_width_bits": u64 = values.@"uid_width_bits";
        pub const @"table_0_activity_score_epochs": u64 = values.@"table_0_activity_score_epochs";
        pub const @"table_0_idle_epochs": u64 = values.@"table_0_idle_epochs";
        pub const @"table_0_concentrated_trigger_percent": u64 = values.@"table_0_concentrated_trigger_percent";
        pub const @"table_0_concentrated_target_percent": u64 = values.@"table_0_concentrated_target_percent";
        pub const @"table_0_smooth_trigger_percent": u64 = values.@"table_0_smooth_trigger_percent";
        pub const @"table_0_smooth_emergency_percent": u64 = values.@"table_0_smooth_emergency_percent";
        pub const @"table_0_smooth_interval_epochs": u64 = values.@"table_0_smooth_interval_epochs";
        pub const @"table_0_smooth_release_count": u64 = values.@"table_0_smooth_release_count";
        pub const @"table_0_tick_maximum_actions": u64 = values.@"table_0_tick_maximum_actions";
        pub const @"table_0_release_check_enabled": u64 = values.@"table_0_release_check_enabled";
        pub const @"table_0_release_tolerance_parts_per_million": u64 = values.@"table_0_release_tolerance_parts_per_million";
        pub const @"table_0_release_tolerance_bytes": u64 = values.@"table_0_release_tolerance_bytes";
        pub const @"table_0_capacity_protection_method": u64 = values.@"table_0_capacity_protection_method";
        pub const @"table_1_activity_score_epochs": u64 = values.@"table_1_activity_score_epochs";
        pub const @"table_1_idle_epochs": u64 = values.@"table_1_idle_epochs";
        pub const @"table_1_concentrated_trigger_percent": u64 = values.@"table_1_concentrated_trigger_percent";
        pub const @"table_1_concentrated_target_percent": u64 = values.@"table_1_concentrated_target_percent";
        pub const @"table_1_smooth_trigger_percent": u64 = values.@"table_1_smooth_trigger_percent";
        pub const @"table_1_smooth_emergency_percent": u64 = values.@"table_1_smooth_emergency_percent";
        pub const @"table_1_smooth_interval_epochs": u64 = values.@"table_1_smooth_interval_epochs";
        pub const @"table_1_smooth_release_count": u64 = values.@"table_1_smooth_release_count";
        pub const @"table_1_tick_maximum_actions": u64 = values.@"table_1_tick_maximum_actions";
        pub const @"table_1_release_check_enabled": u64 = values.@"table_1_release_check_enabled";
        pub const @"table_1_release_tolerance_parts_per_million": u64 = values.@"table_1_release_tolerance_parts_per_million";
        pub const @"table_1_release_tolerance_bytes": u64 = values.@"table_1_release_tolerance_bytes";
        pub const @"table_1_capacity_protection_method": u64 = values.@"table_1_capacity_protection_method";
    };
}
