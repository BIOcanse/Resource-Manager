const protocol = @import("protocol.zig");
const session_module = @import("session.zig");
const ResultCode = @import("../../common/result_codes.zig").ResultCode;

pub export fn rm_display_coordinator_abi_version() callconv(.c) u32 {
    return protocol.abi_version;
}

pub export fn rm_display_coordinator_create(
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

pub export fn rm_display_coordinator_destroy(handle: ?*anyopaque) callconv(.c) void {
    const session = castSession(handle) orelse return;
    session.destroy();
}

pub export fn rm_display_coordinator_query_capacity(
    handle: ?*anyopaque,
    output: ?*protocol.Capacity,
    output_size: u32,
) callconv(.c) i32 {
    if (output_size != @sizeOf(protocol.Capacity)) return code(.abi_mismatch);
    const session = castSession(handle) orelse return code(.invalid_argument);
    (output orelse return code(.invalid_argument)).* = session.capacity();
    return code(.ok);
}

pub export fn rm_display_coordinator_begin_refresh(
    handle: ?*anyopaque,
    input: ?*const protocol.RefreshInput,
) callconv(.c) i32 {
    const session = castSession(handle) orelse return code(.invalid_argument);
    return code(session.beginRefresh(input orelse return code(.invalid_argument)));
}

pub export fn rm_display_coordinator_submit_source(
    handle: ?*anyopaque,
    header: ?*const protocol.SourceBatchHeader,
    fact_pointer: ?[*]const protocol.DisplayFact,
    fact_count: u32,
    text_pointer: ?[*]const protocol.TextInput,
    text_count: u32,
    byte_pointer: ?[*]const u8,
    byte_count: u32,
) callconv(.c) i32 {
    const session = castSession(handle) orelse return code(.invalid_argument);
    return code(session_module.submitSource(
        session,
        header orelse return code(.invalid_argument),
        fact_pointer,
        fact_count,
        text_pointer,
        text_count,
        byte_pointer,
        byte_count,
    ));
}

pub export fn rm_display_coordinator_finalize(
    handle: ?*anyopaque,
    input: ?*const protocol.FinalizeInput,
) callconv(.c) i32 {
    const session = castSession(handle) orelse return code(.invalid_argument);
    return code(session.finalize(input orelse return code(.invalid_argument)));
}

pub export fn rm_display_coordinator_abort_refresh(
    handle: ?*anyopaque,
    input: ?*const protocol.AbortInput,
) callconv(.c) i32 {
    const session = castSession(handle) orelse return code(.invalid_argument);
    return code(session.abortRefresh(input orelse return code(.invalid_argument)));
}

pub export fn rm_display_coordinator_snapshot(
    handle: ?*anyopaque,
    output: ?*protocol.SnapshotOutput,
) callconv(.c) i32 {
    const session = castSession(handle) orelse return code(.invalid_argument);
    return code(session.snapshot(output orelse return code(.invalid_argument)));
}

pub export fn rm_display_coordinator_read_nodes(
    handle: ?*anyopaque,
    input: ?*const protocol.ReadInput,
    pointer: ?[*]protocol.NodeOutput,
    count: u32,
) callconv(.c) i32 {
    const session = castSession(handle) orelse return code(.invalid_argument);
    return code(session_module.readNodes(
        session,
        input orelse return code(.invalid_argument),
        pointer,
        count,
    ));
}

pub export fn rm_display_coordinator_read_edges(
    handle: ?*anyopaque,
    input: ?*const protocol.ReadInput,
    pointer: ?[*]protocol.EdgeOutput,
    count: u32,
) callconv(.c) i32 {
    const session = castSession(handle) orelse return code(.invalid_argument);
    return code(session_module.readEdges(
        session,
        input orelse return code(.invalid_argument),
        pointer,
        count,
    ));
}

pub export fn rm_display_coordinator_read_capabilities(
    handle: ?*anyopaque,
    input: ?*const protocol.ReadInput,
    pointer: ?[*]protocol.DisplayCapabilityOutput,
    count: u32,
) callconv(.c) i32 {
    const session = castSession(handle) orelse return code(.invalid_argument);
    return code(session_module.readCapabilities(
        session,
        input orelse return code(.invalid_argument),
        pointer,
        count,
    ));
}

pub export fn rm_display_coordinator_read_texts(
    handle: ?*anyopaque,
    input: ?*const protocol.ReadInput,
    text_pointer: ?[*]protocol.TextOutput,
    text_count: u32,
    byte_pointer: ?[*]u8,
    byte_count: u32,
) callconv(.c) i32 {
    const session = castSession(handle) orelse return code(.invalid_argument);
    return code(session_module.readTexts(
        session,
        input orelse return code(.invalid_argument),
        text_pointer,
        text_count,
        byte_pointer,
        byte_count,
    ));
}

pub export fn rm_display_coordinator_read_diff(
    handle: ?*anyopaque,
    input: ?*const protocol.ReadInput,
    pointer: ?[*]protocol.DiffEntry,
    count: u32,
) callconv(.c) i32 {
    const session = castSession(handle) orelse return code(.invalid_argument);
    return code(session_module.readDiff(
        session,
        input orelse return code(.invalid_argument),
        pointer,
        count,
    ));
}

pub export fn rm_display_coordinator_read_unresolved(
    handle: ?*anyopaque,
    input: ?*const protocol.ReadInput,
    pointer: ?[*]protocol.UnresolvedOutput,
    count: u32,
) callconv(.c) i32 {
    const session = castSession(handle) orelse return code(.invalid_argument);
    return code(session_module.readUnresolved(
        session,
        input orelse return code(.invalid_argument),
        pointer,
        count,
    ));
}

pub export fn rm_display_coordinator_export_persistence(
    handle: ?*anyopaque,
    input: ?*const protocol.PersistenceInput,
    header: ?*protocol.PersistenceHeader,
    fact_pointer: ?[*]protocol.DisplayFact,
    fact_count: u32,
    text_pointer: ?[*]protocol.TextInput,
    text_count: u32,
    byte_pointer: ?[*]u8,
    byte_count: u32,
) callconv(.c) i32 {
    const session = castSession(handle) orelse return code(.invalid_argument);
    return code(session_module.exportPersistence(
        session,
        input orelse return code(.invalid_argument),
        header orelse return code(.invalid_argument),
        fact_pointer,
        fact_count,
        text_pointer,
        text_count,
        byte_pointer,
        byte_count,
    ));
}

pub export fn rm_display_coordinator_import_persistence(
    handle: ?*anyopaque,
    input: ?*const protocol.PersistenceInput,
    header: ?*const protocol.PersistenceHeader,
    fact_pointer: ?[*]const protocol.DisplayFact,
    fact_count: u32,
    text_pointer: ?[*]const protocol.TextInput,
    text_count: u32,
    byte_pointer: ?[*]const u8,
    byte_count: u32,
) callconv(.c) i32 {
    const session = castSession(handle) orelse return code(.invalid_argument);
    return code(session_module.importPersistence(
        session,
        input orelse return code(.invalid_argument),
        header orelse return code(.invalid_argument),
        fact_pointer,
        fact_count,
        text_pointer,
        text_count,
        byte_pointer,
        byte_count,
    ));
}

fn castSession(handle: ?*anyopaque) ?*session_module.Session {
    return @ptrCast(@alignCast(handle orelse return null));
}

fn code(result: ResultCode) i32 {
    return @intFromEnum(result);
}

comptime {
    if (@sizeOf(protocol.Config) != 160) @compileError("display coordinator Config size changed");
    if (@sizeOf(protocol.Capacity) != 128) @compileError("display coordinator Capacity size changed");
    if (@sizeOf(protocol.RefreshInput) != 80) @compileError("display coordinator RefreshInput size changed");
    if (@sizeOf(protocol.SourceBatchHeader) != 96) @compileError("display coordinator SourceBatchHeader size changed");
    if (@sizeOf(protocol.TextInput) != 48) @compileError("display coordinator TextInput size changed");
    if (@sizeOf(protocol.DisplayFact) != 168) @compileError("display coordinator DisplayFact size changed");
    if (@sizeOf(protocol.FinalizeInput) != 64) @compileError("display coordinator FinalizeInput size changed");
    if (@sizeOf(protocol.AbortInput) != 64) @compileError("display coordinator AbortInput size changed");
    if (@sizeOf(protocol.ReadInput) != 56) @compileError("display coordinator ReadInput size changed");
    if (@sizeOf(protocol.NodeOutput) != 160) @compileError("display coordinator NodeOutput size changed");
    if (@sizeOf(protocol.EdgeOutput) != 64) @compileError("display coordinator EdgeOutput size changed");
    if (@sizeOf(protocol.DisplayCapabilityOutput) != 136) @compileError("display coordinator DisplayCapabilityOutput size changed");
    if (@sizeOf(protocol.TextOutput) != 64) @compileError("display coordinator TextOutput size changed");
    if (@sizeOf(protocol.DiffEntry) != 56) @compileError("display coordinator DiffEntry size changed");
    if (@sizeOf(protocol.UnresolvedOutput) != 80) @compileError("display coordinator UnresolvedOutput size changed");
    if (@sizeOf(protocol.SnapshotOutput) != 128) @compileError("display coordinator SnapshotOutput size changed");
    if (@sizeOf(protocol.PersistenceInput) != 64) @compileError("display coordinator PersistenceInput size changed");
    if (@sizeOf(protocol.PersistenceHeader) != 184) @compileError("display coordinator PersistenceHeader size changed");
}
