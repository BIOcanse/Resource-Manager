// Generated from the consumer owner declaration. Do not edit.
// Source SHA-256: 4ec6e648844fd0f6a6eae9e1d9b2973c5ffbf8b05926a6626611681c4201019f
const std = @import("std");
const ledger_sdk = @import("ledger_sdk");
const ledger_values = @import("ledger_values").Bound;
const types = ledger_sdk.types;
const uid_module = ledger_sdk.uid;

pub const source_sha256 = "4ec6e648844fd0f6a6eae9e1d9b2973c5ffbf8b05926a6626611681c4201019f";
pub const uid_width_bits: u16 = 256;
pub const Uid = uid_module.Identifier(uid_width_bits);
pub const Fixed = types.For(Uid).Fixed;
pub const config: types.Config = .{
    .uid_width = uid_width_bits,
    .activity_width = @intCast(ledger_values.activity_bitmap_width),
    .development_allocation_statistics = ledger_values.development_allocation_statistics != 0,
    .development_lease_analysis = ledger_values.development_lease_analysis != 0,
    .development_lease_analysis_capacity = @intCast(ledger_values.development_lease_analysis_capacity),
    .development_lease_analysis_write_watch_pages = @intCast(ledger_values.development_lease_analysis_write_watch_pages),
    .use_window_cache = ledger_values.use_window_cache != 0,
    .use_window_cache_slots = @intCast(ledger_values.use_window_cache_slots),
    .index_buckets = 64,
};
comptime {
    if (ledger_values.uid_width_bits != uid_width_bits)
        @compileError("owner UID width must match the selected FreedomPoint values");
}

pub const tables = [_]types.Table{
    .{
        .capacity = 16,
        .partition_capacity = 0,
        .reserved_start = 0,
        .reserved_count = 0,
        .attributes = types.Attributes{ .recoverability = .recoverable, .recovery_cost_coefficient = 2, .access_loss_impact = .observable_now },
        .policy = .{
            .score_epochs = @intCast(ledger_values.table_0_activity_score_epochs),
            .idle_epochs = @intCast(ledger_values.table_0_idle_epochs),
            .protection = switch (ledger_values.table_0_capacity_protection_method) {
                1 => .concentrated,
                2 => .smooth,
                else => @compileError("selected capacity protection method is unsupported"),
            },
            .concentrated_trigger_percent = @intCast(ledger_values.table_0_concentrated_trigger_percent),
            .concentrated_target_percent = @intCast(ledger_values.table_0_concentrated_target_percent),
            .smooth_trigger_percent = @intCast(ledger_values.table_0_smooth_trigger_percent),
            .smooth_emergency_percent = @intCast(ledger_values.table_0_smooth_emergency_percent),
            .smooth_interval_epochs = @intCast(ledger_values.table_0_smooth_interval_epochs),
            .smooth_release_count = @intCast(ledger_values.table_0_smooth_release_count),
            .tick_maximum_actions = @intCast(ledger_values.table_0_tick_maximum_actions),
            .release_check = .{
                .enabled = ledger_values.table_0_release_check_enabled == 2,
                .tolerance_parts_per_million = @intCast(ledger_values.table_0_release_tolerance_parts_per_million),
                .tolerance_bytes = ledger_values.table_0_release_tolerance_bytes,
            },
        },
    },
    .{
        .capacity = 8,
        .partition_capacity = 0,
        .reserved_start = 0,
        .reserved_count = 0,
        .attributes = types.Attributes{ .recoverability = .recoverable, .recovery_cost_coefficient = 1, .access_loss_impact = .unobservable_now },
        .policy = .{
            .score_epochs = @intCast(ledger_values.table_1_activity_score_epochs),
            .idle_epochs = @intCast(ledger_values.table_1_idle_epochs),
            .protection = switch (ledger_values.table_1_capacity_protection_method) {
                1 => .concentrated,
                2 => .smooth,
                else => @compileError("selected capacity protection method is unsupported"),
            },
            .concentrated_trigger_percent = @intCast(ledger_values.table_1_concentrated_trigger_percent),
            .concentrated_target_percent = @intCast(ledger_values.table_1_concentrated_target_percent),
            .smooth_trigger_percent = @intCast(ledger_values.table_1_smooth_trigger_percent),
            .smooth_emergency_percent = @intCast(ledger_values.table_1_smooth_emergency_percent),
            .smooth_interval_epochs = @intCast(ledger_values.table_1_smooth_interval_epochs),
            .smooth_release_count = @intCast(ledger_values.table_1_smooth_release_count),
            .tick_maximum_actions = @intCast(ledger_values.table_1_tick_maximum_actions),
            .release_check = .{
                .enabled = ledger_values.table_1_release_check_enabled == 2,
                .tolerance_parts_per_million = @intCast(ledger_values.table_1_release_tolerance_parts_per_million),
                .tolerance_bytes = ledger_values.table_1_release_tolerance_bytes,
            },
        },
    },
};

pub const fixed = [_]Fixed{
};
