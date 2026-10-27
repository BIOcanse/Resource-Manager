// Generated declaration structure.
pub const ContractSlot = struct {
    kind: []const u8,
    unit: ?[]const u8,
    integer_bit_width: ?u8,
    enumeration: ?EnumContract,
    integer_minimum: ?i64,
    integer_maximum: ?i64,
    integer_step: ?i64,
    integer_minimum_point: ?[]const u8,
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
pub const EnumMember = struct { name: []const u8, literal: []const u8 };
pub const EnumContract = struct { is_unsigned: bool, bit_width: u8, members: []const EnumMember };
pub const __fpEnum_window_stop_timeout_policy = enum(u8) {
    @"terminate" = 0,
    @"wait" = 1,
};
pub fn Bind(comptime values: anytype) type {
    return struct {
        pub const @"controller_workers": i32 = values.@"controller_workers";
        pub const @"history_capacity": i32 = values.@"history_capacity";
        pub const @"worker_arena_bytes": i32 = values.@"worker_arena_bytes";
        pub const @"thread_stack_bytes": i32 = values.@"thread_stack_bytes";
        pub const @"backend_probe_interval_ms": i32 = values.@"backend_probe_interval_ms";
        pub const @"backend_timeout_ms": i32 = values.@"backend_timeout_ms";
        pub const @"window_cancel_poll_ms": i32 = values.@"window_cancel_poll_ms";
        pub const @"instance_connect_timeout_ms": i32 = values.@"instance_connect_timeout_ms";
        pub const @"instance_connect_poll_ms": i32 = values.@"instance_connect_poll_ms";
        pub const @"window_stop_deadline_ms": i32 = values.@"window_stop_deadline_ms";
        pub const @"window_stop_timeout_policy": __fpEnum_window_stop_timeout_policy = values.@"window_stop_timeout_policy";
    };
}
