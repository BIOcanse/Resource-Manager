const protocol = @import("protocol.zig");
const session_module = @import("session.zig");
const ResultCode = @import("../../common/result_codes.zig").ResultCode;

pub export fn rm_software_identity_catalog_abi_version() callconv(.c) u32 {
    return protocol.abi_version;
}

pub export fn rm_software_identity_catalog_create(
    config: ?*const protocol.Config,
    handle: ?*?*anyopaque,
) callconv(.c) i32 {
    const output = handle orelse return code(.invalid_argument);
    output.* = null;
    const session = session_module.Session.create(config orelse return code(.invalid_argument)) catch |err| switch (err) {
        error.InvalidConfiguration => return code(.abi_mismatch),
        else => return code(.out_of_memory),
    };
    output.* = @ptrCast(session);
    return code(.ok);
}

pub export fn rm_software_identity_catalog_destroy(handle: ?*anyopaque) callconv(.c) void {
    const session: *session_module.Session = @ptrCast(@alignCast(handle orelse return));
    session.destroy();
}

pub export fn rm_software_identity_catalog_reconfigure(
    handle: ?*anyopaque,
    config: ?*const protocol.Config,
) callconv(.c) i32 {
    const session: *session_module.Session = @ptrCast(@alignCast(
        handle orelse return code(.invalid_argument),
    ));
    return code(session.reconfigure(config orelse return code(.invalid_argument)));
}

pub export fn rm_software_identity_catalog_query_capacity(
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

pub export fn rm_software_identity_catalog_replace(
    handle: ?*anyopaque,
    input: ?*const protocol.ReplaceInput,
    entries: ?[*]const protocol.EntryInput,
    entry_count: u32,
    aliases: ?[*]const protocol.AliasInput,
    alias_count: u32,
    roots: ?[*]const protocol.RootInput,
    root_count: u32,
    key_bytes: ?[*]const u8,
    key_byte_count: u32,
) callconv(.c) i32 {
    const session: *session_module.Session = @ptrCast(@alignCast(
        handle orelse return code(.invalid_argument),
    ));
    return code(session_module.replace(
        session,
        input orelse return code(.invalid_argument),
        entries,
        entry_count,
        aliases,
        alias_count,
        roots,
        root_count,
        key_bytes,
        key_byte_count,
    ));
}

pub export fn rm_software_identity_catalog_match_known(
    handle: ?*anyopaque,
    input: ?*const protocol.KnownQueryInput,
    signals: ?[*]const protocol.KnownSignalInput,
    signal_count: u32,
    key_bytes: ?[*]const u8,
    key_byte_count: u32,
    output: ?*protocol.KnownMatchOutput,
) callconv(.c) i32 {
    const session: *session_module.Session = @ptrCast(@alignCast(
        handle orelse return code(.invalid_argument),
    ));
    return code(session_module.matchKnown(
        session,
        input orelse return code(.invalid_argument),
        signals,
        signal_count,
        key_bytes,
        key_byte_count,
        output orelse return code(.invalid_argument),
    ));
}

pub export fn rm_software_identity_catalog_match(
    handle: ?*anyopaque,
    input: ?*const protocol.QueryInput,
    facts: ?[*]const protocol.FactInput,
    fact_count: u32,
    key_bytes: ?[*]const u8,
    key_byte_count: u32,
    output: ?*protocol.MatchOutput,
) callconv(.c) i32 {
    const session: *session_module.Session = @ptrCast(@alignCast(
        handle orelse return code(.invalid_argument),
    ));
    return code(session_module.match(
        session,
        input orelse return code(.invalid_argument),
        facts,
        fact_count,
        key_bytes,
        key_byte_count,
        output orelse return code(.invalid_argument),
    ));
}

pub export fn rm_software_identity_catalog_snapshot(
    handle: ?*anyopaque,
    output: ?*protocol.CatalogSummary,
) callconv(.c) i32 {
    const session: *session_module.Session = @ptrCast(@alignCast(
        handle orelse return code(.invalid_argument),
    ));
    return code(session.snapshot(output orelse return code(.invalid_argument)));
}

fn code(result: ResultCode) i32 {
    return @intFromEnum(result);
}

comptime {
    _ = protocol.Config;
    _ = protocol.Capacity;
    _ = protocol.EntryInput;
    _ = protocol.AliasInput;
    _ = protocol.RootInput;
    _ = protocol.ReplaceInput;
    _ = protocol.QueryInput;
    _ = protocol.FactInput;
    _ = protocol.MatchOutput;
    _ = protocol.KnownQueryInput;
    _ = protocol.KnownSignalInput;
    _ = protocol.KnownMatchOutput;
    _ = protocol.CatalogSummary;
}
