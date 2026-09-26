const protocol = @import("protocol.zig");
const session_module = @import("session.zig");
const ResultCode = @import("../../common/result_codes.zig").ResultCode;

pub export fn rm_placement_coordinator_abi_version() callconv(.c) u32 {
    return protocol.abi_version;
}

pub export fn rm_placement_coordinator_create(
    config: ?*const protocol.Config,
    handle: ?*?*anyopaque,
) callconv(.c) i32 {
    const output = handle orelse return code(.invalid_argument);
    output.* = null;
    const actual_config = config orelse return code(.invalid_argument);
    const session = session_module.Session.create(actual_config) catch |err| switch (err) {
        error.InvalidConfiguration => return code(.abi_mismatch),
        else => return code(.out_of_memory),
    };
    output.* = @ptrCast(session);
    return code(.ok);
}

pub export fn rm_placement_coordinator_destroy(handle: ?*anyopaque) callconv(.c) void {
    const session: *session_module.Session = @ptrCast(@alignCast(handle orelse return));
    session.destroy();
}

pub export fn rm_placement_coordinator_reconfigure(
    handle: ?*anyopaque,
    config: ?*const protocol.Config,
) callconv(.c) i32 {
    const session: *session_module.Session = @ptrCast(@alignCast(
        handle orelse return code(.invalid_argument),
    ));
    return code(session.reconfigure(config orelse return code(.invalid_argument)));
}

pub export fn rm_placement_coordinator_reset(handle: ?*anyopaque) callconv(.c) i32 {
    const session: *session_module.Session = @ptrCast(@alignCast(
        handle orelse return code(.invalid_argument),
    ));
    return code(session.reset());
}

pub export fn rm_placement_coordinator_query_capacity(
    handle: ?*anyopaque,
    output: ?*protocol.Capacity,
    output_size: u32,
) callconv(.c) i32 {
    if (output_size != @sizeOf(protocol.Capacity)) return code(.abi_mismatch);
    const session: *session_module.Session = @ptrCast(@alignCast(
        handle orelse return code(.invalid_argument),
    ));
    const actual_output = output orelse return code(.invalid_argument);
    actual_output.* = session.capacity();
    return code(.ok);
}

pub export fn rm_placement_coordinator_plan(
    handle: ?*anyopaque,
    cycle: ?*protocol.CycleInput,
    desired_rows: ?[*]const protocol.DesiredInput,
    desired_capacity: u32,
    applied_rows: ?[*]const protocol.AppliedInput,
    applied_capacity: u32,
    actions: ?[*]protocol.ActionOutput,
    action_capacity: u32,
) callconv(.c) i32 {
    const session: *session_module.Session = @ptrCast(@alignCast(
        handle orelse return code(.invalid_argument),
    ));
    return code(session_module.plan(
        session,
        cycle orelse return code(.invalid_argument),
        desired_rows,
        desired_capacity,
        applied_rows,
        applied_capacity,
        actions,
        action_capacity,
    ));
}

pub export fn rm_placement_coordinator_feedback(
    handle: ?*anyopaque,
    rows: ?[*]const protocol.FeedbackInput,
    row_count: u32,
) callconv(.c) i32 {
    const session: *session_module.Session = @ptrCast(@alignCast(
        handle orelse return code(.invalid_argument),
    ));
    return code(session_module.applyFeedback(session, rows, row_count));
}

pub export fn rm_placement_coordinator_snapshot(
    handle: ?*anyopaque,
    header: ?*protocol.SnapshotHeader,
    states: ?[*]protocol.StateOutput,
    state_capacity: u32,
) callconv(.c) i32 {
    const session: *session_module.Session = @ptrCast(@alignCast(
        handle orelse return code(.invalid_argument),
    ));
    return code(session_module.snapshot(
        session,
        header orelse return code(.invalid_argument),
        states,
        state_capacity,
    ));
}

fn code(result: ResultCode) i32 {
    return @intFromEnum(result);
}

comptime {
    if (@sizeOf(protocol.Config) != 80) @compileError("placement Config export size changed");
    if (@sizeOf(protocol.Capacity) != 48) @compileError("placement Capacity export size changed");
    if (@sizeOf(protocol.CycleInput) != 88) @compileError("placement CycleInput export size changed");
    if (@sizeOf(protocol.DesiredInput) != 72) @compileError("placement DesiredInput export size changed");
    if (@sizeOf(protocol.AppliedInput) != 88) @compileError("placement AppliedInput export size changed");
    if (@sizeOf(protocol.ActionOutput) != 96) @compileError("placement ActionOutput export size changed");
    if (@sizeOf(protocol.FeedbackInput) != 64) @compileError("placement FeedbackInput export size changed");
    if (@sizeOf(protocol.SnapshotHeader) != 96) @compileError("placement SnapshotHeader export size changed");
    if (@sizeOf(protocol.StateOutput) != 112) @compileError("placement StateOutput export size changed");
}
