const protocol = @import("protocol.zig");
const session_module = @import("session.zig");

const empty_bytes = [_]u8{};
const empty_const_bytes = [_]u8{};
const empty_records = [_]protocol.Record{};

pub export fn rm_transaction_journal_abi_version() callconv(.c) u32 {
    return protocol.abi_version;
}

pub export fn rm_transaction_journal_create_new(
    config: ?*const protocol.CreateConfig,
    handle: ?*?*anyopaque,
) callconv(.c) i32 {
    const output = handle orelse return code(.invalid_argument);
    output.* = null;
    const actual_config = config orelse return code(.invalid_argument);
    const session = session_module.Session.create(actual_config) catch |err| switch (err) {
        error.InvalidConfiguration => return code(.abi_mismatch),
        error.OutOfMemory => return code(.out_of_memory),
        else => return code(.capacity_full),
    };
    output.* = @ptrCast(session);
    return code(.ok);
}

pub export fn rm_transaction_journal_open_existing(
    open_config: ?*const protocol.OpenConfig,
    image: ?[*]const u8,
    image_length: u64,
    handle: ?*?*anyopaque,
) callconv(.c) i32 {
    const output = handle orelse return code(.invalid_argument);
    output.* = null;
    if (image_length > @import("std").math.maxInt(usize)) return code(.invalid_argument);
    const length: usize = @intCast(image_length);
    const bytes = if (length == 0)
        empty_const_bytes[0..]
    else
        (image orelse return code(.invalid_argument))[0..length];
    var opened: ?*session_module.Session = null;
    const result = session_module.openExisting(
        open_config orelse return code(.invalid_argument),
        bytes,
        &opened,
    );
    if (result != .ok) return code(result);
    output.* = @ptrCast(opened.?);
    return code(.ok);
}

pub export fn rm_transaction_journal_destroy(handle: ?*anyopaque) callconv(.c) void {
    const session: *session_module.Session = @ptrCast(@alignCast(handle orelse return));
    session.destroy();
}

pub export fn rm_transaction_journal_query_capacity(
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

pub export fn rm_transaction_journal_prepare(
    handle: ?*anyopaque,
    input: ?*const protocol.PrepareInput,
) callconv(.c) i32 {
    const session: *session_module.Session = @ptrCast(@alignCast(
        handle orelse return code(.invalid_argument),
    ));
    return code(session_module.prepare(session, input orelse return code(.invalid_argument)));
}

pub export fn rm_transaction_journal_prepare_batch(
    handle: ?*anyopaque,
    batch: ?*const protocol.PrepareBatchInput,
    inputs: ?[*]const protocol.PrepareInput,
    input_count: u32,
) callconv(.c) i32 {
    const session: *session_module.Session = @ptrCast(@alignCast(
        handle orelse return code(.invalid_argument),
    ));
    const actual_batch = batch orelse return code(.invalid_argument);
    if (input_count == 0) return code(.invalid_argument);
    const actual_inputs = (inputs orelse return code(.invalid_argument))[0..input_count];
    return code(session_module.prepareBatch(session, actual_batch, actual_inputs));
}

pub export fn rm_transaction_journal_mutate(
    handle: ?*anyopaque,
    input: ?*const protocol.MutationInput,
) callconv(.c) i32 {
    const session: *session_module.Session = @ptrCast(@alignCast(
        handle orelse return code(.invalid_argument),
    ));
    return code(session_module.mutate(session, input orelse return code(.invalid_argument)));
}

pub export fn rm_transaction_journal_stage_feedback(
    handle: ?*anyopaque,
    input: ?*const protocol.StageFeedbackInput,
) callconv(.c) i32 {
    const session: *session_module.Session = @ptrCast(@alignCast(
        handle orelse return code(.invalid_argument),
    ));
    return code(session_module.stageFeedback(session, input orelse return code(.invalid_argument)));
}

pub export fn rm_transaction_journal_recovery_evidence(
    handle: ?*anyopaque,
    input: ?*const protocol.RecoveryEvidenceInput,
) callconv(.c) i32 {
    const session: *session_module.Session = @ptrCast(@alignCast(
        handle orelse return code(.invalid_argument),
    ));
    return code(session_module.applyRecoveryEvidence(
        session,
        input orelse return code(.invalid_argument),
    ));
}

pub export fn rm_transaction_journal_acknowledge(
    handle: ?*anyopaque,
    input: ?*const protocol.AckInput,
) callconv(.c) i32 {
    const session: *session_module.Session = @ptrCast(@alignCast(
        handle orelse return code(.invalid_argument),
    ));
    return code(session_module.acknowledge(session, input orelse return code(.invalid_argument)));
}

pub export fn rm_transaction_journal_get(
    handle: ?*anyopaque,
    identity: ?*const protocol.Identity,
    output: ?*protocol.Record,
) callconv(.c) i32 {
    const session: *session_module.Session = @ptrCast(@alignCast(
        handle orelse return code(.invalid_argument),
    ));
    return code(session_module.get(
        session,
        identity orelse return code(.invalid_argument),
        output orelse return code(.invalid_argument),
    ));
}

pub export fn rm_transaction_journal_snapshot(
    handle: ?*anyopaque,
    header: ?*protocol.SnapshotHeader,
    records: ?[*]protocol.Record,
    record_capacity: u32,
) callconv(.c) i32 {
    const session: *session_module.Session = @ptrCast(@alignCast(
        handle orelse return code(.invalid_argument),
    ));
    const output = if (record_capacity == 0)
        @constCast(empty_records[0..])
    else
        (records orelse return code(.invalid_argument))[0..record_capacity];
    return code(session_module.snapshot(
        session,
        header orelse return code(.invalid_argument),
        output,
    ));
}

pub export fn rm_transaction_journal_encode(
    handle: ?*anyopaque,
    output: ?[*]u8,
    output_capacity: u64,
    written: ?*u64,
) callconv(.c) i32 {
    const session: *session_module.Session = @ptrCast(@alignCast(
        handle orelse return code(.invalid_argument),
    ));
    if (output_capacity > @import("std").math.maxInt(usize)) return code(.invalid_argument);
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

fn code(status: protocol.Status) i32 {
    return @intFromEnum(status);
}

comptime {
    if (@sizeOf(protocol.CreateConfig) != 64) @compileError("transaction journal CreateConfig export size changed");
    if (@sizeOf(protocol.OpenConfig) != 32) @compileError("transaction journal OpenConfig export size changed");
    if (@sizeOf(protocol.Capacity) != 32) @compileError("transaction journal Capacity export size changed");
    if (@sizeOf(protocol.Identity) != 64) @compileError("transaction journal Identity export size changed");
    if (@sizeOf(protocol.PrepareInput) != 224) @compileError("transaction journal PrepareInput export size changed");
    if (@sizeOf(protocol.PrepareBatchInput) != 48) @compileError("transaction journal PrepareBatchInput export size changed");
    if (@sizeOf(protocol.MutationInput) != 160) @compileError("transaction journal MutationInput export size changed");
    if (@sizeOf(protocol.StageFeedbackInput) != 176) @compileError("transaction journal StageFeedbackInput export size changed");
    if (@sizeOf(protocol.RecoveryEvidenceInput) != 160) @compileError("transaction journal RecoveryEvidenceInput export size changed");
    if (@sizeOf(protocol.AckInput) != 160) @compileError("transaction journal AckInput export size changed");
    if (@sizeOf(protocol.Record) != 296) @compileError("transaction journal Record export size changed");
    if (@sizeOf(protocol.ImageHeader) != 128) @compileError("transaction journal ImageHeader export size changed");
    if (@sizeOf(protocol.SnapshotHeader) != 128) @compileError("transaction journal SnapshotHeader export size changed");
}
