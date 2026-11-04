const protocol = @import("protocol.zig");
const state = @import("state.zig");
const ResultCode = @import("../../common/result_codes.zig").ResultCode;

pub const Session = state.Session;

pub fn readOperations(
    session: *Session,
    input: *const protocol.ReadInput,
    pointer: ?[*]protocol.OperationOutput,
    count: u32,
) ResultCode {
    return session.readOperations(
        input,
        mutableSlice(protocol.OperationOutput, pointer, count) orelse
            return .invalid_argument,
    );
}

pub fn readActions(
    session: *Session,
    input: *const protocol.ReadInput,
    pointer: ?[*]protocol.ActionOutput,
    count: u32,
) ResultCode {
    return session.readActions(
        input,
        mutableSlice(protocol.ActionOutput, pointer, count) orelse
            return .invalid_argument,
    );
}

pub fn exportPersistence(
    session: *Session,
    input: *const protocol.PersistenceInput,
    header: *protocol.PersistenceHeader,
    pointer: ?[*]protocol.PersistenceRecord,
    count: u32,
) ResultCode {
    return session.exportPersistence(
        input,
        header,
        mutableSlice(protocol.PersistenceRecord, pointer, count) orelse
            return .invalid_argument,
    );
}

pub fn importPersistence(
    session: *Session,
    input: *const protocol.PersistenceImportInput,
    header: *const protocol.PersistenceHeader,
    pointer: ?[*]const protocol.PersistenceRecord,
    count: u32,
) ResultCode {
    return session.importPersistence(
        input,
        header,
        constSlice(protocol.PersistenceRecord, pointer, count) orelse
            return .invalid_argument,
    );
}

fn mutableSlice(comptime T: type, pointer: ?[*]T, count: u32) ?[]T {
    if (count == 0) return &[_]T{};
    return (pointer orelse return null)[0..count];
}

fn constSlice(comptime T: type, pointer: ?[*]const T, count: u32) ?[]const T {
    if (count == 0) return &[_]T{};
    return (pointer orelse return null)[0..count];
}
