const std = @import("std");
const protocol = @import("protocol.zig");
const session_module = @import("session.zig");

const empty_bytes = [_]u8{};
const empty_const_bytes = [_]u8{};
const empty_records = [_]protocol.Record{};

pub export fn rm_applied_ownership_abi_version() callconv(.c) u32 {
    return protocol.abi_version;
}

pub export fn rm_applied_ownership_create_new(
    config: ?*const protocol.CreateConfig,
    handle: ?*?*anyopaque,
) callconv(.c) i32 {
    const output = handle orelse return code(.invalid_argument);
    output.* = null;
    const session = session_module.Session.create(config orelse return code(.invalid_argument)) catch |err| switch (err) {
        error.InvalidConfiguration => return code(.abi_mismatch),
        error.OutOfMemory => return code(.out_of_memory),
        else => return code(.capacity_full),
    };
    output.* = @ptrCast(session);
    return code(.ok);
}

pub export fn rm_applied_ownership_open_existing(
    config: ?*const protocol.OpenConfig,
    image: ?[*]const u8,
    image_length: u64,
    handle: ?*?*anyopaque,
) callconv(.c) i32 {
    const output = handle orelse return code(.invalid_argument);
    output.* = null;
    if (image_length > std.math.maxInt(usize)) return code(.invalid_argument);
    const length: usize = @intCast(image_length);
    const bytes = if (length == 0)
        empty_const_bytes[0..]
    else
        (image orelse return code(.invalid_argument))[0..length];
    var session: ?*session_module.Session = null;
    const status = session_module.openExisting(
        config orelse return code(.invalid_argument),
        bytes,
        &session,
    );
    if (status != .ok) return code(status);
    output.* = @ptrCast(session.?);
    return code(.ok);
}

pub export fn rm_applied_ownership_destroy(handle: ?*anyopaque) callconv(.c) void {
    const session: *session_module.Session = @ptrCast(@alignCast(handle orelse return));
    session.destroy();
}

pub export fn rm_applied_ownership_query_capacity(
    handle: ?*anyopaque,
    output: ?*protocol.Capacity,
    output_size: u32,
) callconv(.c) i32 {
    if (output_size != @sizeOf(protocol.Capacity)) return code(.abi_mismatch);
    const session: *session_module.Session = @ptrCast(@alignCast(
        handle orelse return code(.invalid_argument),
    ));
    (output orelse return code(.invalid_argument)).* = session.capacity();
    return code(.ok);
}

pub export fn rm_applied_ownership_promote(
    handle: ?*anyopaque,
    input: ?*const protocol.PromoteInput,
) callconv(.c) i32 {
    const session: *session_module.Session = @ptrCast(@alignCast(
        handle orelse return code(.invalid_argument),
    ));
    return code(session_module.promote(session, input orelse return code(.invalid_argument)));
}

pub export fn rm_applied_ownership_transition(
    handle: ?*anyopaque,
    input: ?*const protocol.TransitionInput,
) callconv(.c) i32 {
    const session: *session_module.Session = @ptrCast(@alignCast(
        handle orelse return code(.invalid_argument),
    ));
    return code(session_module.transition(session, input orelse return code(.invalid_argument)));
}

pub export fn rm_applied_ownership_plan_transition(
    handle: ?*anyopaque,
    primary: ?*const protocol.PrimaryIdentity,
    transition_binding: ?*const protocol.OriginalBinding,
    transition_payload: ?*const protocol.DurablePayloadReference,
    output: ?*protocol.TransitionInput,
) callconv(.c) i32 {
    const session: *session_module.Session = @ptrCast(@alignCast(
        handle orelse return code(.invalid_argument),
    ));
    return code(session_module.planTransition(
        session,
        primary orelse return code(.invalid_argument),
        transition_binding orelse return code(.invalid_argument),
        transition_payload orelse return code(.invalid_argument),
        output orelse return code(.invalid_argument),
    ));
}

pub export fn rm_applied_ownership_remove(
    handle: ?*anyopaque,
    input: ?*const protocol.RemoveInput,
) callconv(.c) i32 {
    const session: *session_module.Session = @ptrCast(@alignCast(
        handle orelse return code(.invalid_argument),
    ));
    return code(session_module.remove(session, input orelse return code(.invalid_argument)));
}

pub export fn rm_applied_ownership_get(
    handle: ?*anyopaque,
    primary: ?*const protocol.PrimaryIdentity,
    output: ?*protocol.Record,
) callconv(.c) i32 {
    const session: *session_module.Session = @ptrCast(@alignCast(
        handle orelse return code(.invalid_argument),
    ));
    return code(session_module.get(
        session,
        primary orelse return code(.invalid_argument),
        output orelse return code(.invalid_argument),
    ));
}

pub export fn rm_applied_ownership_snapshot(
    handle: ?*anyopaque,
    header: ?*protocol.SnapshotHeader,
    records: ?[*]protocol.Record,
    record_capacity: u32,
) callconv(.c) i32 {
    const session: *session_module.Session = @ptrCast(@alignCast(
        handle orelse return code(.invalid_argument),
    ));
    const outputs = if (record_capacity == 0)
        @constCast(empty_records[0..])
    else
        (records orelse return code(.invalid_argument))[0..record_capacity];
    return code(session_module.snapshot(
        session,
        header orelse return code(.invalid_argument),
        outputs,
    ));
}

pub export fn rm_applied_ownership_encode(
    handle: ?*anyopaque,
    output: ?[*]u8,
    output_capacity: u64,
    written: ?*u64,
) callconv(.c) i32 {
    const session: *session_module.Session = @ptrCast(@alignCast(
        handle orelse return code(.invalid_argument),
    ));
    if (output_capacity > std.math.maxInt(usize)) return code(.invalid_argument);
    const capacity: usize = @intCast(output_capacity);
    const bytes = if (capacity == 0)
        @constCast(empty_bytes[0..])
    else
        (output orelse return code(.invalid_argument))[0..capacity];
    return code(session_module.encode(
        session,
        bytes,
        written orelse return code(.invalid_argument),
    ));
}

pub export fn rm_applied_ownership_decode_replace(
    handle: ?*anyopaque,
    image: ?[*]const u8,
    image_length: u64,
) callconv(.c) i32 {
    const session: *session_module.Session = @ptrCast(@alignCast(
        handle orelse return code(.invalid_argument),
    ));
    if (image_length > std.math.maxInt(usize)) return code(.invalid_argument);
    const length: usize = @intCast(image_length);
    const bytes = if (length == 0)
        empty_const_bytes[0..]
    else
        (image orelse return code(.invalid_argument))[0..length];
    return code(session_module.decodeReplace(session, bytes));
}

fn code(status: protocol.Status) i32 {
    return @intFromEnum(status);
}

comptime {
    if (@sizeOf(protocol.CreateConfig) != 96 or @sizeOf(protocol.OpenConfig) != 72 or
        @sizeOf(protocol.Capacity) != 64 or @sizeOf(protocol.PrimaryIdentity) != 40 or
        @sizeOf(protocol.ActionIdentity) != 64 or @sizeOf(protocol.OriginalBinding) != 160 or
        @sizeOf(protocol.DurablePayloadReference) != 32 or @sizeOf(protocol.CurrentGrades) != 24 or
        @sizeOf(protocol.OwnershipCas) != 192 or @sizeOf(protocol.Record) != 320 or
        @sizeOf(protocol.PromoteInput) != 320 or @sizeOf(protocol.TransitionInput) != 448 or
        @sizeOf(protocol.RemoveInput) != 224 or @sizeOf(protocol.SnapshotHeader) != 96 or
        @sizeOf(protocol.ImageHeader) != 128)
    {
        @compileError("applied ownership C ABI layout drifted");
    }
}
