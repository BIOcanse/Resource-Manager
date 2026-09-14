const protocol = @import("protocol.zig");
const state = @import("state.zig");
const ResultCode = @import("../../common/result_codes.zig").ResultCode;

pub const Session = state.Session;

pub fn importPersisted(
    session: *Session,
    input: *const protocol.ImportInput,
    rows_pointer: ?[*]const protocol.PersistedPathInput,
    row_count: u32,
    key_pointer: ?[*]const u8,
    key_byte_count: u32,
) ResultCode {
    const rows = constSlice(protocol.PersistedPathInput, rows_pointer, row_count) orelse return .invalid_argument;
    const key_bytes = constSlice(u8, key_pointer, key_byte_count) orelse return .invalid_argument;
    return session.importPersisted(input, rows, key_bytes);
}

pub fn observe(
    session: *Session,
    input: *const protocol.ObserveInput,
    key_pointer: ?[*]const u8,
    key_byte_count: u32,
) ResultCode {
    const key_bytes = constSlice(u8, key_pointer, key_byte_count) orelse return .invalid_argument;
    return session.observe(input, key_bytes);
}

pub fn confirmRoot(
    session: *Session,
    input: *const protocol.ConfirmRootInput,
    key_pointer: ?[*]const u8,
    key_byte_count: u32,
    confirmed_count: *u32,
) ResultCode {
    const key_bytes = constSlice(u8, key_pointer, key_byte_count) orelse return .invalid_argument;
    return session.confirmRoot(input, key_bytes, confirmed_count);
}

pub fn planPersistence(
    session: *Session,
    input: *const protocol.PlanPersistenceInput,
    operations_pointer: ?[*]protocol.PersistenceOperation,
    operation_capacity: u32,
    output: *protocol.PersistencePlanOutput,
) ResultCode {
    const operations = mutableSlice(protocol.PersistenceOperation, operations_pointer, operation_capacity) orelse
        return .invalid_argument;
    return session.planPersistence(input, operations, output);
}

pub fn applyPersistenceFeedback(
    session: *Session,
    input: *const protocol.PersistenceFeedbackInput,
    feedback_pointer: ?[*]const protocol.PersistenceFeedback,
    feedback_count: u32,
) ResultCode {
    const feedbacks = constSlice(protocol.PersistenceFeedback, feedback_pointer, feedback_count) orelse
        return .invalid_argument;
    return session.applyPersistenceFeedback(input, feedbacks);
}

pub fn snapshot(
    session: *Session,
    input: *const protocol.SnapshotInput,
    registrations_pointer: ?[*]protocol.RegistrationSnapshot,
    registration_capacity: u32,
    paths_pointer: ?[*]protocol.PathSnapshot,
    path_capacity: u32,
    output: *protocol.SnapshotOutput,
) ResultCode {
    const registrations = mutableSlice(
        protocol.RegistrationSnapshot,
        registrations_pointer,
        registration_capacity,
    ) orelse return .invalid_argument;
    const paths = mutableSlice(protocol.PathSnapshot, paths_pointer, path_capacity) orelse return .invalid_argument;
    return session.snapshot(input, registrations, paths, output);
}

fn constSlice(comptime T: type, pointer: ?[*]const T, count: u32) ?[]const T {
    if (count == 0) return &.{};
    return (pointer orelse return null)[0..count];
}

fn mutableSlice(comptime T: type, pointer: ?[*]T, count: u32) ?[]T {
    if (count == 0) return &.{};
    return (pointer orelse return null)[0..count];
}
