const protocol = @import("protocol.zig");
const std = @import("std");
const session_module = @import("session.zig");
const ResultCode = @import("../../common/result_codes.zig").ResultCode;

pub export fn rm_sampling_subscription_abi_version() callconv(.c) u32 {
    return protocol.abi_version;
}

pub export fn rm_sampling_subscription_create(
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

pub export fn rm_sampling_subscription_destroy(handle: ?*anyopaque) callconv(.c) void {
    const session: *session_module.Session = @ptrCast(@alignCast(handle orelse return));
    session.destroy();
}

// The caller serializes bind/operation/unbind for this session and keeps the
// supplied allocation alive until unbind returns. No slice survives unbind.
pub export fn rm_sampling_subscription_create_external_scratch(
    config: ?*const protocol.Config,
    handle: ?*?*anyopaque,
) callconv(.c) i32 {
    const output = handle orelse return code(.invalid_argument);
    output.* = null;
    const actual_config = config orelse return code(.invalid_argument);
    const session = session_module.Session.createExternalScratch(actual_config) catch |err| switch (err) {
        error.InvalidConfiguration => return code(.abi_mismatch),
        else => return code(.out_of_memory),
    };
    output.* = @ptrCast(session);
    return code(.ok);
}

pub export fn rm_sampling_subscription_bind_scratch(
    handle: ?*anyopaque,
    pointer: ?[*]u8,
    byte_count: u64,
) callconv(.c) i32 {
    const session: *session_module.Session = @ptrCast(@alignCast(
        handle orelse return code(.invalid_argument),
    ));
    const bytes = pointer orelse return code(.invalid_argument);
    const length = std.math.cast(usize, byte_count) orelse return code(.invalid_argument);
    _ = std.math.add(usize, @intFromPtr(bytes), length) catch return code(.invalid_argument);
    return code(session.bindScratch(bytes[0..length]));
}

pub export fn rm_sampling_subscription_unbind_scratch(handle: ?*anyopaque) callconv(.c) i32 {
    const session: *session_module.Session = @ptrCast(@alignCast(
        handle orelse return code(.invalid_argument),
    ));
    return code(session.unbindScratch());
}

pub export fn rm_sampling_subscription_reconfigure(
    handle: ?*anyopaque,
    config: ?*const protocol.Config,
) callconv(.c) i32 {
    const session: *session_module.Session = @ptrCast(@alignCast(
        handle orelse return code(.invalid_argument),
    ));
    return code(session.reconfigure(config orelse return code(.invalid_argument)));
}

pub export fn rm_sampling_subscription_reset(
    handle: ?*anyopaque,
    input: ?*const protocol.ControlInput,
) callconv(.c) i32 {
    const session: *session_module.Session = @ptrCast(@alignCast(
        handle orelse return code(.invalid_argument),
    ));
    return code(session.reset(input orelse return code(.invalid_argument)));
}

pub export fn rm_sampling_subscription_query_capacity(
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

// Rows this session holds now; the caller sizes each scratch borrow from
// item_rows and reports the rows to the workspace diagnostics.
pub export fn rm_sampling_subscription_query_workspace_capacity(
    handle: ?*anyopaque,
    output: ?*protocol.WorkspaceCapacity,
    output_size: u32,
) callconv(.c) i32 {
    if (output_size != @sizeOf(protocol.WorkspaceCapacity)) return code(.abi_mismatch);
    const session: *session_module.Session = @ptrCast(@alignCast(
        handle orelse return code(.invalid_argument),
    ));
    const actual_output = output orelse return code(.invalid_argument);
    actual_output.* = session.queryWorkspaceCapacity();
    return code(.ok);
}

pub export fn rm_sampling_subscription_track(
    handle: ?*anyopaque,
    input: ?*const protocol.TrackInput,
    items: ?[*]const protocol.ItemReference,
    item_count: u32,
) callconv(.c) i32 {
    const session: *session_module.Session = @ptrCast(@alignCast(
        handle orelse return code(.invalid_argument),
    ));
    return code(session_module.track(
        session,
        input orelse return code(.invalid_argument),
        items,
        item_count,
    ));
}

pub export fn rm_sampling_subscription_remove(
    handle: ?*anyopaque,
    input: ?*const protocol.RemoveInput,
) callconv(.c) i32 {
    const session: *session_module.Session = @ptrCast(@alignCast(
        handle orelse return code(.invalid_argument),
    ));
    return code(session_module.remove(
        session,
        input orelse return code(.invalid_argument),
    ));
}

pub export fn rm_sampling_subscription_plan(
    handle: ?*anyopaque,
    header: ?*protocol.PlanHeader,
    due_items: ?[*]protocol.DueItemOutput,
    due_capacity: u32,
    source_views: ?[*]protocol.SourceViewOutput,
    source_capacity: u32,
    expired_sources: ?[*]protocol.ExpiredSourceOutput,
    expired_capacity: u32,
    triggers: ?[*]protocol.TriggerOutput,
    trigger_capacity: u32,
) callconv(.c) i32 {
    const session: *session_module.Session = @ptrCast(@alignCast(
        handle orelse return code(.invalid_argument),
    ));
    return code(session_module.plan(
        session,
        header orelse return code(.invalid_argument),
        due_items,
        due_capacity,
        source_views,
        source_capacity,
        expired_sources,
        expired_capacity,
        triggers,
        trigger_capacity,
    ));
}

pub export fn rm_sampling_subscription_complete(
    handle: ?*anyopaque,
    input: ?*const protocol.CompletionInput,
    items: ?[*]const protocol.ItemReference,
    item_count: u32,
    triggers: ?[*]const protocol.TriggerOutput,
    trigger_count: u32,
    output: ?*protocol.CompletionOutput,
) callconv(.c) i32 {
    const session: *session_module.Session = @ptrCast(@alignCast(
        handle orelse return code(.invalid_argument),
    ));
    return code(session_module.complete(
        session,
        input orelse return code(.invalid_argument),
        items,
        item_count,
        triggers,
        trigger_count,
        output orelse return code(.invalid_argument),
    ));
}

pub export fn rm_sampling_subscription_snapshot(
    handle: ?*anyopaque,
    header: ?*protocol.SnapshotHeader,
    sources: ?[*]protocol.SourceStateOutput,
    source_capacity: u32,
    items: ?[*]protocol.ItemStateOutput,
    item_capacity: u32,
) callconv(.c) i32 {
    const session: *session_module.Session = @ptrCast(@alignCast(
        handle orelse return code(.invalid_argument),
    ));
    return code(session_module.snapshot(
        session,
        header orelse return code(.invalid_argument),
        sources,
        source_capacity,
        items,
        item_capacity,
    ));
}

fn code(result: ResultCode) i32 {
    return @intFromEnum(result);
}

comptime {
    if (@sizeOf(protocol.Config) != 112) @compileError("sampling subscription Config export size changed");
    if (@sizeOf(protocol.Capacity) != 72) @compileError("sampling subscription Capacity export size changed");
    if (@sizeOf(protocol.WorkspaceCapacity) != 40) @compileError("sampling subscription WorkspaceCapacity export size changed");
    if (@sizeOf(protocol.ItemReference) != 32) @compileError("sampling subscription ItemReference export size changed");
    if (@sizeOf(protocol.TrackInput) != 96) @compileError("sampling subscription TrackInput export size changed");
    if (@sizeOf(protocol.RemoveInput) != 72) @compileError("sampling subscription RemoveInput export size changed");
    if (@sizeOf(protocol.ControlInput) != 64) @compileError("sampling subscription ControlInput export size changed");
    if (@sizeOf(protocol.PlanHeader) != 128) @compileError("sampling subscription PlanHeader export size changed");
    if (@sizeOf(protocol.TriggerOutput) != 48) @compileError("sampling subscription TriggerOutput export size changed");
    if (@sizeOf(protocol.DueItemOutput) != 64) @compileError("sampling subscription DueItemOutput export size changed");
    if (@sizeOf(protocol.SourceViewOutput) != 64) @compileError("sampling subscription SourceViewOutput export size changed");
    if (@sizeOf(protocol.ExpiredSourceOutput) != 48) @compileError("sampling subscription ExpiredSourceOutput export size changed");
    if (@sizeOf(protocol.CompletionInput) != 96) @compileError("sampling subscription CompletionInput export size changed");
    if (@sizeOf(protocol.CompletionOutput) != 72) @compileError("sampling subscription CompletionOutput export size changed");
    if (@sizeOf(protocol.SnapshotHeader) != 112) @compileError("sampling subscription SnapshotHeader export size changed");
    if (@sizeOf(protocol.SourceStateOutput) != 64) @compileError("sampling subscription SourceStateOutput export size changed");
    if (@sizeOf(protocol.ItemStateOutput) != 64) @compileError("sampling subscription ItemStateOutput export size changed");
}
