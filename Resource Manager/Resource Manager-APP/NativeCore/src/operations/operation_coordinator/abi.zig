const protocol = @import("protocol.zig");
const abi_layout = @import("abi_layout.zig");
const session_module = @import("session.zig");
const ResultCode = @import("../../common/result_codes.zig").ResultCode;

comptime {
    abi_layout.assert();
}

pub export fn rm_operation_coordinator_abi_version() callconv(.c) u32 {
    return protocol.abi_version;
}

pub export fn rm_operation_coordinator_create(
    config: ?*const protocol.Config,
    handle: ?*?*anyopaque,
) callconv(.c) i32 {
    const output = handle orelse return code(.invalid_argument);
    output.* = null;
    const session = session_module.Session.create(
        config orelse return code(.invalid_argument),
    ) catch |err| switch (err) {
        error.InvalidConfiguration => return code(.abi_mismatch),
        else => return code(.out_of_memory),
    };
    output.* = @ptrCast(session);
    return code(.ok);
}

pub export fn rm_operation_coordinator_destroy(handle: ?*anyopaque) callconv(.c) void {
    const session = castSession(handle) orelse return;
    session.destroy();
}

pub export fn rm_operation_coordinator_query_capacity(
    handle: ?*anyopaque,
    output: ?*protocol.Capacity,
    output_size: u32,
) callconv(.c) i32 {
    if (output_size != @sizeOf(protocol.Capacity)) return code(.abi_mismatch);
    const session = castSession(handle) orelse return code(.invalid_argument);
    (output orelse return code(.invalid_argument)).* = session.capacity();
    return code(.ok);
}

pub export fn rm_operation_coordinator_reconfigure(
    handle: ?*anyopaque,
    config: ?*const protocol.Config,
) callconv(.c) i32 {
    const session = castSession(handle) orelse return code(.invalid_argument);
    return code(session.reconfigure(config orelse return code(.invalid_argument)));
}

pub export fn rm_operation_coordinator_submit(
    handle: ?*anyopaque,
    input: ?*const protocol.SubmitInput,
    output: ?*protocol.SubmitOutput,
) callconv(.c) i32 {
    const session = castSession(handle) orelse return code(.invalid_argument);
    return code(session.submit(
        input orelse return code(.invalid_argument),
        output orelse return code(.invalid_argument),
    ));
}

pub export fn rm_operation_coordinator_cancel(
    handle: ?*anyopaque,
    input: ?*const protocol.CancelInput,
    output: ?*protocol.CancelOutput,
) callconv(.c) i32 {
    const session = castSession(handle) orelse return code(.invalid_argument);
    return code(session.cancel(
        input orelse return code(.invalid_argument),
        output orelse return code(.invalid_argument),
    ));
}

pub export fn rm_operation_coordinator_plan(
    handle: ?*anyopaque,
    input: ?*const protocol.PlanInput,
    output: ?*protocol.PlanOutput,
) callconv(.c) i32 {
    const session = castSession(handle) orelse return code(.invalid_argument);
    return code(session.plan(
        input orelse return code(.invalid_argument),
        output orelse return code(.invalid_argument),
    ));
}

pub export fn rm_operation_coordinator_feedback(
    handle: ?*anyopaque,
    input: ?*const protocol.ActionFeedbackInput,
) callconv(.c) i32 {
    const session = castSession(handle) orelse return code(.invalid_argument);
    return code(session.feedback(input orelse return code(.invalid_argument)));
}

pub export fn rm_operation_coordinator_complete(
    handle: ?*anyopaque,
    input: ?*const protocol.CompletionInput,
) callconv(.c) i32 {
    const session = castSession(handle) orelse return code(.invalid_argument);
    return code(session.complete(input orelse return code(.invalid_argument)));
}

pub export fn rm_operation_coordinator_report_progress(
    handle: ?*anyopaque,
    input: ?*const protocol.ProgressInput,
) callconv(.c) i32 {
    const session = castSession(handle) orelse return code(.invalid_argument);
    return code(session.reportProgress(input orelse return code(.invalid_argument)));
}

pub export fn rm_operation_coordinator_snapshot(
    handle: ?*anyopaque,
    output: ?*protocol.SnapshotOutput,
) callconv(.c) i32 {
    const session = castSession(handle) orelse return code(.invalid_argument);
    return code(session.snapshot(output orelse return code(.invalid_argument)));
}

pub export fn rm_operation_coordinator_read_operations(
    handle: ?*anyopaque,
    input: ?*const protocol.ReadInput,
    pointer: ?[*]protocol.OperationOutput,
    count: u32,
) callconv(.c) i32 {
    const session = castSession(handle) orelse return code(.invalid_argument);
    return code(session_module.readOperations(
        session,
        input orelse return code(.invalid_argument),
        pointer,
        count,
    ));
}

pub export fn rm_operation_coordinator_read_actions(
    handle: ?*anyopaque,
    input: ?*const protocol.ReadInput,
    pointer: ?[*]protocol.ActionOutput,
    count: u32,
) callconv(.c) i32 {
    const session = castSession(handle) orelse return code(.invalid_argument);
    return code(session_module.readActions(
        session,
        input orelse return code(.invalid_argument),
        pointer,
        count,
    ));
}

pub export fn rm_operation_coordinator_export_persistence(
    handle: ?*anyopaque,
    input: ?*const protocol.PersistenceInput,
    header: ?*protocol.PersistenceHeader,
    pointer: ?[*]protocol.PersistenceRecord,
    count: u32,
) callconv(.c) i32 {
    const session = castSession(handle) orelse return code(.invalid_argument);
    return code(session_module.exportPersistence(
        session,
        input orelse return code(.invalid_argument),
        header orelse return code(.invalid_argument),
        pointer,
        count,
    ));
}

pub export fn rm_operation_coordinator_import_persistence(
    handle: ?*anyopaque,
    input: ?*const protocol.PersistenceImportInput,
    header: ?*const protocol.PersistenceHeader,
    pointer: ?[*]const protocol.PersistenceRecord,
    count: u32,
) callconv(.c) i32 {
    const session = castSession(handle) orelse return code(.invalid_argument);
    return code(session_module.importPersistence(
        session,
        input orelse return code(.invalid_argument),
        header orelse return code(.invalid_argument),
        pointer,
        count,
    ));
}

fn castSession(handle: ?*anyopaque) ?*session_module.Session {
    return @ptrCast(@alignCast(handle orelse return null));
}

fn code(result: ResultCode) i32 {
    return @intFromEnum(result);
}
