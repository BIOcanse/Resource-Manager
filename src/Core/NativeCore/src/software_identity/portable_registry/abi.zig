const protocol = @import("protocol.zig");
const session_module = @import("session.zig");
const ResultCode = @import("../../common/result_codes.zig").ResultCode;

pub export fn rm_software_identity_portable_registry_abi_version() callconv(.c) u32 {
    return protocol.abi_version;
}

pub export fn rm_software_identity_portable_registry_create(
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

pub export fn rm_software_identity_portable_registry_destroy(handle: ?*anyopaque) callconv(.c) void {
    const session: *session_module.Session = @ptrCast(@alignCast(handle orelse return));
    session.destroy();
}

pub export fn rm_software_identity_portable_registry_reconfigure(
    handle: ?*anyopaque,
    config: ?*const protocol.Config,
) callconv(.c) i32 {
    const session: *session_module.Session = @ptrCast(@alignCast(handle orelse return code(.invalid_argument)));
    return code(session.reconfigure(config orelse return code(.invalid_argument)));
}

pub export fn rm_software_identity_portable_registry_query_capacity(
    handle: ?*anyopaque,
    output: ?*protocol.Capacity,
    output_size: u32,
) callconv(.c) i32 {
    if (output_size != @sizeOf(protocol.Capacity)) return code(.abi_mismatch);
    const session: *session_module.Session = @ptrCast(@alignCast(handle orelse return code(.invalid_argument)));
    (output orelse return code(.invalid_argument)).* = session.capacity();
    return code(.ok);
}

pub export fn rm_software_identity_portable_registry_import(
    handle: ?*anyopaque,
    input: ?*const protocol.ImportInput,
    rows: ?[*]const protocol.PersistedPathInput,
    row_count: u32,
    key_bytes: ?[*]const u8,
    key_byte_count: u32,
) callconv(.c) i32 {
    const session: *session_module.Session = @ptrCast(@alignCast(handle orelse return code(.invalid_argument)));
    return code(session_module.importPersisted(
        session,
        input orelse return code(.invalid_argument),
        rows,
        row_count,
        key_bytes,
        key_byte_count,
    ));
}

pub export fn rm_software_identity_portable_registry_observe(
    handle: ?*anyopaque,
    input: ?*const protocol.ObserveInput,
    key_bytes: ?[*]const u8,
    key_byte_count: u32,
) callconv(.c) i32 {
    const session: *session_module.Session = @ptrCast(@alignCast(handle orelse return code(.invalid_argument)));
    return code(session_module.observe(
        session,
        input orelse return code(.invalid_argument),
        key_bytes,
        key_byte_count,
    ));
}

pub export fn rm_software_identity_portable_registry_confirm_root(
    handle: ?*anyopaque,
    input: ?*const protocol.ConfirmRootInput,
    key_bytes: ?[*]const u8,
    key_byte_count: u32,
    confirmed_count: ?*u32,
) callconv(.c) i32 {
    const session: *session_module.Session = @ptrCast(@alignCast(handle orelse return code(.invalid_argument)));
    return code(session_module.confirmRoot(
        session,
        input orelse return code(.invalid_argument),
        key_bytes,
        key_byte_count,
        confirmed_count orelse return code(.invalid_argument),
    ));
}

pub export fn rm_software_identity_portable_registry_mark_missing(
    handle: ?*anyopaque,
    input: ?*const protocol.MarkMissingInput,
) callconv(.c) i32 {
    const session: *session_module.Session = @ptrCast(@alignCast(handle orelse return code(.invalid_argument)));
    return code(session.markMissing(input orelse return code(.invalid_argument)));
}

pub export fn rm_software_identity_portable_registry_plan_persistence(
    handle: ?*anyopaque,
    input: ?*const protocol.PlanPersistenceInput,
    operations: ?[*]protocol.PersistenceOperation,
    operation_capacity: u32,
    output: ?*protocol.PersistencePlanOutput,
) callconv(.c) i32 {
    const session: *session_module.Session = @ptrCast(@alignCast(handle orelse return code(.invalid_argument)));
    return code(session_module.planPersistence(
        session,
        input orelse return code(.invalid_argument),
        operations,
        operation_capacity,
        output orelse return code(.invalid_argument),
    ));
}

pub export fn rm_software_identity_portable_registry_apply_feedback(
    handle: ?*anyopaque,
    input: ?*const protocol.PersistenceFeedbackInput,
    feedbacks: ?[*]const protocol.PersistenceFeedback,
    feedback_count: u32,
) callconv(.c) i32 {
    const session: *session_module.Session = @ptrCast(@alignCast(handle orelse return code(.invalid_argument)));
    return code(session_module.applyPersistenceFeedback(
        session,
        input orelse return code(.invalid_argument),
        feedbacks,
        feedback_count,
    ));
}

pub export fn rm_software_identity_portable_registry_snapshot(
    handle: ?*anyopaque,
    input: ?*const protocol.SnapshotInput,
    registrations: ?[*]protocol.RegistrationSnapshot,
    registration_capacity: u32,
    paths: ?[*]protocol.PathSnapshot,
    path_capacity: u32,
    output: ?*protocol.SnapshotOutput,
) callconv(.c) i32 {
    const session: *session_module.Session = @ptrCast(@alignCast(handle orelse return code(.invalid_argument)));
    return code(session_module.snapshot(
        session,
        input orelse return code(.invalid_argument),
        registrations,
        registration_capacity,
        paths,
        path_capacity,
        output orelse return code(.invalid_argument),
    ));
}

fn code(result: ResultCode) i32 {
    return @intFromEnum(result);
}

comptime {
    _ = protocol.Config;
    _ = protocol.Capacity;
    _ = protocol.ImportInput;
    _ = protocol.PersistedPathInput;
    _ = protocol.ObserveInput;
    _ = protocol.ConfirmRootInput;
    _ = protocol.MarkMissingInput;
    _ = protocol.PlanPersistenceInput;
    _ = protocol.PersistenceOperation;
    _ = protocol.PersistencePlanOutput;
    _ = protocol.PersistenceFeedbackInput;
    _ = protocol.PersistenceFeedback;
    _ = protocol.SnapshotInput;
    _ = protocol.RegistrationSnapshot;
    _ = protocol.PathSnapshot;
    _ = protocol.SnapshotOutput;
}
