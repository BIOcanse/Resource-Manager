const protocol = @import("protocol.zig");
const state = @import("state.zig");

fn result(code: protocol.ResultCode) i32 {
    return @intFromEnum(code);
}

fn mapError(err: anyerror) i32 {
    return result(switch (err) {
        error.InvalidArgument => .invalid_argument,
        error.InvalidState => .invalid_state,
        error.BufferTooSmall => .buffer_too_small,
        error.DuplicateResourceKey => .duplicate_resource_key,
        error.DuplicateResourceId => .duplicate_resource_id,
        error.SequenceRegression => .sequence_regression,
        error.TimestampRegression => .timestamp_regression,
        error.SnapshotTooOld => .snapshot_too_old,
        error.SnapshotFromFuture => .snapshot_from_future,
        error.SameSequenceConflict => .same_sequence_conflict,
        error.StaleResourceReference => .stale_resource_reference,
        error.ResourceNotFound => .resource_not_found,
        error.ConfigurationRegression => .configuration_regression,
        error.InvalidEnum => .invalid_enum,
        error.NumericOverflow => .numeric_overflow,
        else => .invalid_argument,
    });
}

fn mutableBytes(pointer: ?*anyopaque, count: u64) ?[]u8 {
    if (count == 0) return null;
    const length = @import("std").math.cast(usize, count) orelse return null;
    const bytes: [*]u8 = @ptrCast(pointer orelse return null);
    return bytes[0..length];
}

fn constSlice(comptime T: type, pointer: ?[*]const T, count: u32) ?[]const T {
    if (count == 0) return &[_]T{};
    return (pointer orelse return null)[0..count];
}

fn mutableSlice(comptime T: type, pointer: ?[*]T, count: u32) ?[]T {
    if (count == 0) return @constCast((&[_]T{})[0..]);
    return (pointer orelse return null)[0..count];
}

pub export fn rm_private_resource_ledger_abi_version() callconv(.c) u32 {
    return protocol.abi_version;
}

pub export fn rm_private_resource_ledger_config_size() callconv(.c) u32 {
    return @sizeOf(protocol.Config);
}

pub export fn rm_private_resource_ledger_snapshot_input_size() callconv(.c) u32 {
    return @sizeOf(protocol.SnapshotInput);
}

pub export fn rm_private_resource_ledger_resource_input_size() callconv(.c) u32 {
    return @sizeOf(protocol.ResourceInput);
}

pub export fn rm_private_resource_ledger_resource_view_size() callconv(.c) u32 {
    return @sizeOf(protocol.ResourceView);
}

pub export fn rm_private_resource_ledger_action_feedback_size() callconv(.c) u32 {
    return @sizeOf(protocol.ActionFeedback);
}

pub export fn rm_private_resource_ledger_summary_size() callconv(.c) u32 {
    return @sizeOf(protocol.SnapshotSummary);
}

pub export fn rm_private_resource_ledger_layout_fingerprint() callconv(.c) u64 {
    const values = [_]usize{
        @sizeOf(protocol.Config),
        @offsetOf(protocol.Config, "configuration_generation"),
        @offsetOf(protocol.Config, "capacity"),
        @sizeOf(protocol.ResourceRef),
        @offsetOf(protocol.ResourceRef, "slot_index"),
        @offsetOf(protocol.ResourceRef, "slot_generation"),
        @offsetOf(protocol.ResourceRef, "resource_key"),
        @sizeOf(protocol.ResourceView),
        @offsetOf(protocol.ResourceView, "resource"),
        @offsetOf(protocol.ResourceView, "ledger_generation"),
        @offsetOf(protocol.ResourceView, "value"),
        @sizeOf(protocol.SnapshotSummary),
        @offsetOf(protocol.SnapshotSummary, "ledger_generation"),
        @offsetOf(protocol.SnapshotSummary, "configuration_generation"),
        @offsetOf(protocol.SnapshotSummary, "ledger_instance_id"),
        @offsetOf(protocol.SnapshotSummary, "owner_application_key"),
        @offsetOf(protocol.SnapshotSummary, "resource_count"),
        @offsetOf(protocol.SnapshotSummary, "surface_state"),
    };
    var hash: u64 = 14695981039346656037;
    for (values) |value| {
        hash = (hash ^ @as(u64, @intCast(value))) *% 1099511628211;
    }
    return hash;
}

pub export fn rm_private_resource_ledger_state_alignment() callconv(.c) u32 {
    return @intCast(state.alignment());
}

pub export fn rm_private_resource_ledger_state_required_size(
    capacity: u32,
    output_size: ?*u64,
) callconv(.c) i32 {
    const target = output_size orelse return result(.invalid_argument);
    const size = state.requiredBytes(capacity) catch |err| return mapError(err);
    target.* = @intCast(size);
    return result(.ok);
}

pub export fn rm_private_resource_ledger_state_initialize(
    state_pointer: ?*anyopaque,
    state_size: u64,
    config_pointer: ?*const protocol.Config,
) callconv(.c) i32 {
    const buffer = mutableBytes(state_pointer, state_size) orelse return result(.invalid_argument);
    const config = config_pointer orelse return result(.invalid_argument);
    state.initialize(buffer, config.*) catch |err| return mapError(err);
    return result(.ok);
}

pub export fn rm_private_resource_ledger_apply_config(
    state_pointer: ?*anyopaque,
    state_size: u64,
    config_pointer: ?*const protocol.Config,
) callconv(.c) i32 {
    const buffer = mutableBytes(state_pointer, state_size) orelse return result(.invalid_argument);
    const config = config_pointer orelse return result(.invalid_argument);
    state.applyConfig(buffer, config.*) catch |err| return mapError(err);
    return result(.ok);
}

pub export fn rm_private_resource_ledger_import_snapshot(
    state_pointer: ?*anyopaque,
    state_size: u64,
    snapshot_pointer: ?*const protocol.SnapshotInput,
    resources_pointer: ?[*]const protocol.ResourceInput,
    resource_count: u32,
    summary_pointer: ?*protocol.SnapshotSummary,
) callconv(.c) i32 {
    const buffer = mutableBytes(state_pointer, state_size) orelse return result(.invalid_argument);
    const snapshot = snapshot_pointer orelse return result(.invalid_argument);
    const resources = constSlice(protocol.ResourceInput, resources_pointer, resource_count) orelse
        return result(.invalid_argument);
    const output = summary_pointer orelse return result(.invalid_argument);
    output.* = state.importSnapshot(buffer, snapshot.*, resources) catch |err| return mapError(err);
    return result(.ok);
}

pub export fn rm_private_resource_ledger_touch_resources(
    state_pointer: ?*anyopaque,
    state_size: u64,
    resource_ids_pointer: ?[*]const u32,
    resource_id_count: u32,
    summary_pointer: ?*protocol.SnapshotSummary,
) callconv(.c) i32 {
    const buffer = mutableBytes(state_pointer, state_size) orelse return result(.invalid_argument);
    const ids = constSlice(u32, resource_ids_pointer, resource_id_count) orelse return result(.invalid_argument);
    const output = summary_pointer orelse return result(.invalid_argument);
    output.* = state.touchResources(buffer, ids) catch |err| return mapError(err);
    return result(.ok);
}

pub export fn rm_private_resource_ledger_settle_activity(
    state_pointer: ?*anyopaque,
    state_size: u64,
    now_timestamp: u64,
    summary_pointer: ?*protocol.SnapshotSummary,
) callconv(.c) i32 {
    const buffer = mutableBytes(state_pointer, state_size) orelse return result(.invalid_argument);
    const output = summary_pointer orelse return result(.invalid_argument);
    output.* = state.settleActivity(buffer, now_timestamp) catch |err| return mapError(err);
    return result(.ok);
}

pub export fn rm_private_resource_ledger_apply_action_feedback(
    state_pointer: ?*anyopaque,
    state_size: u64,
    feedback_pointer: ?*const protocol.ActionFeedback,
    summary_pointer: ?*protocol.SnapshotSummary,
) callconv(.c) i32 {
    const buffer = mutableBytes(state_pointer, state_size) orelse return result(.invalid_argument);
    const feedback = feedback_pointer orelse return result(.invalid_argument);
    const output = summary_pointer orelse return result(.invalid_argument);
    output.* = state.applyActionFeedback(buffer, feedback.*) catch |err| return mapError(err);
    return result(.ok);
}

pub export fn rm_private_resource_ledger_read_snapshot(
    state_pointer: ?*anyopaque,
    state_size: u64,
    output_pointer: ?[*]protocol.ResourceView,
    output_capacity: u32,
    summary_pointer: ?*protocol.SnapshotSummary,
) callconv(.c) i32 {
    const buffer = mutableBytes(state_pointer, state_size) orelse return result(.invalid_argument);
    const output = mutableSlice(protocol.ResourceView, output_pointer, output_capacity) orelse
        return result(.invalid_argument);
    const summary_output = summary_pointer orelse return result(.invalid_argument);
    summary_output.* = state.readSnapshot(buffer, output) catch |err| return mapError(err);
    return result(.ok);
}
