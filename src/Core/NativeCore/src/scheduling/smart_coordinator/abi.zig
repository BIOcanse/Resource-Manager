const protocol = @import("protocol.zig");
const coordinator = @import("session.zig");

fn code(status: protocol.Status) i32 {
    return @intFromEnum(status);
}

pub export fn rm_smart_coordinator_abi_version() callconv(.c) u32 {
    return protocol.abi_version;
}

pub export fn rm_smart_coordinator_capacity_for_config(
    config: ?*const protocol.Config,
    capacity: ?*protocol.Capacity,
) callconv(.c) i32 {
    return code(coordinator.capacityForConfig(
        config orelse return code(.invalid_argument),
        capacity orelse return code(.invalid_argument),
    ));
}

pub export fn rm_smart_coordinator_create(
    config: ?*const protocol.Config,
    handle: ?*?*anyopaque,
) callconv(.c) i32 {
    const actual_config = config orelse return code(.invalid_argument);
    const output = handle orelse return code(.invalid_argument);
    output.* = null;
    const validation = protocol.validateConfig(actual_config);
    if (validation != .ok) return code(validation);
    const session = coordinator.create(actual_config) catch |err| switch (err) {
        error.InvalidConfiguration => return code(.invalid_argument),
        else => return code(.out_of_memory),
    };
    output.* = @ptrCast(session);
    return code(.ok);
}

pub export fn rm_smart_coordinator_destroy(handle: ?*anyopaque) callconv(.c) void {
    const session: *coordinator.Session = @ptrCast(@alignCast(handle orelse return));
    coordinator.destroy(session);
}

pub export fn rm_smart_coordinator_query_capacity(
    handle: ?*anyopaque,
    capacity: ?*protocol.Capacity,
) callconv(.c) i32 {
    const session: *coordinator.Session = @ptrCast(@alignCast(handle orelse return code(.invalid_argument)));
    return code(coordinator.queryCapacity(session, capacity orelse return code(.invalid_argument)));
}

pub export fn rm_smart_coordinator_reconfigure(
    handle: ?*anyopaque,
    config: ?*const protocol.Config,
) callconv(.c) i32 {
    const session: *coordinator.Session = @ptrCast(@alignCast(handle orelse return code(.invalid_argument)));
    return code(coordinator.reconfigure(session, config orelse return code(.invalid_argument)));
}

pub export fn rm_smart_coordinator_reset(handle: ?*anyopaque) callconv(.c) i32 {
    const session: *coordinator.Session = @ptrCast(@alignCast(handle orelse return code(.invalid_argument)));
    return code(coordinator.reset(session));
}

pub export fn rm_smart_coordinator_plan(
    handle: ?*anyopaque,
    input: ?*const protocol.CycleInput,
    input_rows: ?[*]const protocol.InputRow,
    input_row_capacity: u32,
    actions: ?[*]protocol.Action,
    action_capacity: u32,
    snapshot: ?*protocol.Snapshot,
) callconv(.c) i32 {
    const session: *coordinator.Session = @ptrCast(@alignCast(handle orelse return code(.invalid_argument)));
    return code(coordinator.plan(
        session,
        input orelse return code(.invalid_argument),
        input_rows,
        input_row_capacity,
        actions,
        action_capacity,
        snapshot orelse return code(.invalid_argument),
    ));
}

pub export fn rm_smart_coordinator_feedback(
    handle: ?*anyopaque,
    feedback_rows: ?[*]const protocol.Feedback,
    feedback_count: u32,
    feedback_capacity: u32,
    snapshot: ?*protocol.Snapshot,
) callconv(.c) i32 {
    const session: *coordinator.Session = @ptrCast(@alignCast(handle orelse return code(.invalid_argument)));
    return code(coordinator.applyFeedback(
        session,
        feedback_rows,
        feedback_count,
        feedback_capacity,
        snapshot orelse return code(.invalid_argument),
    ));
}

pub export fn rm_smart_coordinator_snapshot(
    handle: ?*anyopaque,
    snapshot: ?*protocol.Snapshot,
    rows: ?[*]protocol.SnapshotRow,
    row_capacity: u32,
) callconv(.c) i32 {
    const session: *coordinator.Session = @ptrCast(@alignCast(handle orelse return code(.invalid_argument)));
    return code(coordinator.getSnapshot(
        session,
        snapshot orelse return code(.invalid_argument),
        rows,
        row_capacity,
    ));
}
