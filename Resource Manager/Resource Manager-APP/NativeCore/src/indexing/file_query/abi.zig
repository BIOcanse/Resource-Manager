const protocol = @import("protocol.zig");
const session_module = @import("session.zig");
const ResultCode = @import("../../common/result_codes.zig").ResultCode;

pub export fn rm_file_query_abi_version() callconv(.c) u32 {
    return protocol.abi_version;
}

pub export fn rm_file_query_create(
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

pub export fn rm_file_query_destroy(handle: ?*anyopaque) callconv(.c) void {
    const session: *session_module.Session = @ptrCast(@alignCast(handle orelse return));
    session.destroy();
}

pub export fn rm_file_query_reconfigure(
    handle: ?*anyopaque,
    config: ?*const protocol.Config,
) callconv(.c) i32 {
    const session = castSession(handle) orelse return code(.invalid_argument);
    return code(session.reconfigure(config orelse return code(.invalid_argument)));
}

pub export fn rm_file_query_query_capacity(
    handle: ?*anyopaque,
    output: ?*protocol.Capacity,
    output_size: u32,
) callconv(.c) i32 {
    if (output_size != @sizeOf(protocol.Capacity)) return code(.abi_mismatch);
    const session = castSession(handle) orelse return code(.invalid_argument);
    (output orelse return code(.invalid_argument)).* = session.capacity();
    return code(.ok);
}

pub export fn rm_file_query_begin(
    handle: ?*anyopaque,
    input: ?*const protocol.BeginInput,
    query_pointer: ?[*]const u8,
    query_byte_count: u32,
    output: ?*protocol.PlanOutput,
    source_pointer: ?[*]protocol.SourcePlanOutput,
    source_capacity: u32,
    plan_pointer: ?[*]u8,
    plan_capacity: u32,
) callconv(.c) i32 {
    const session = castSession(handle) orelse return code(.invalid_argument);
    return code(session_module.begin(
        session,
        input orelse return code(.invalid_argument),
        query_pointer,
        query_byte_count,
        output orelse return code(.invalid_argument),
        source_pointer,
        source_capacity,
        plan_pointer,
        plan_capacity,
    ));
}

pub export fn rm_file_query_submit_candidates(
    handle: ?*anyopaque,
    input: ?*const protocol.SubmitInput,
    candidate_pointer: ?[*]const protocol.CandidateInput,
    candidate_count: u32,
    byte_pointer: ?[*]const u8,
    byte_count: u32,
    output: ?*protocol.SubmitOutput,
) callconv(.c) i32 {
    const session = castSession(handle) orelse return code(.invalid_argument);
    return code(session_module.submit(
        session,
        input orelse return code(.invalid_argument),
        candidate_pointer,
        candidate_count,
        byte_pointer,
        byte_count,
        output orelse return code(.invalid_argument),
    ));
}

pub export fn rm_file_query_finalize(
    handle: ?*anyopaque,
    input: ?*const protocol.FinalizeInput,
    result_pointer: ?[*]protocol.ResultOutput,
    result_capacity: u32,
    output: ?*protocol.FinalizeOutput,
) callconv(.c) i32 {
    const session = castSession(handle) orelse return code(.invalid_argument);
    return code(session_module.finalize(
        session,
        input orelse return code(.invalid_argument),
        result_pointer,
        result_capacity,
        output orelse return code(.invalid_argument),
    ));
}

pub export fn rm_file_query_reset(
    handle: ?*anyopaque,
    input: ?*const protocol.ResetInput,
) callconv(.c) i32 {
    const session = castSession(handle) orelse return code(.invalid_argument);
    return code(session.reset(input orelse return code(.invalid_argument)));
}

pub export fn rm_file_query_snapshot(
    handle: ?*anyopaque,
    output: ?*protocol.SnapshotOutput,
) callconv(.c) i32 {
    const session = castSession(handle) orelse return code(.invalid_argument);
    return code(session.snapshot(output orelse return code(.invalid_argument)));
}

fn castSession(handle: ?*anyopaque) ?*session_module.Session {
    return @ptrCast(@alignCast(handle orelse return null));
}

fn code(result: ResultCode) i32 {
    return @intFromEnum(result);
}

comptime {
    if (@sizeOf(protocol.Config) != 168) @compileError("file query Config export size changed");
    if (@sizeOf(protocol.Capacity) != 104) @compileError("file query Capacity export size changed");
    if (@sizeOf(protocol.BeginInput) != 72) @compileError("file query BeginInput export size changed");
    if (@sizeOf(protocol.SourcePlanOutput) != 48) @compileError("file query SourcePlanOutput export size changed");
    if (@sizeOf(protocol.PlanOutput) != 120) @compileError("file query PlanOutput export size changed");
    if (@sizeOf(protocol.CandidateInput) != 72) @compileError("file query CandidateInput export size changed");
    if (@sizeOf(protocol.SubmitInput) != 80) @compileError("file query SubmitInput export size changed");
    if (@sizeOf(protocol.SubmitOutput) != 88) @compileError("file query SubmitOutput export size changed");
    if (@sizeOf(protocol.FinalizeInput) != 72) @compileError("file query FinalizeInput export size changed");
    if (@sizeOf(protocol.ResultOutput) != 48) @compileError("file query ResultOutput export size changed");
    if (@sizeOf(protocol.FinalizeOutput) != 80) @compileError("file query FinalizeOutput export size changed");
    if (@sizeOf(protocol.ResetInput) != 56) @compileError("file query ResetInput export size changed");
    if (@sizeOf(protocol.SnapshotOutput) != 128) @compileError("file query SnapshotOutput export size changed");
}
