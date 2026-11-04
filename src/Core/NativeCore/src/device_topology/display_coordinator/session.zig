const protocol = @import("protocol.zig");
const state = @import("state.zig");
const ResultCode = @import("../../common/result_codes.zig").ResultCode;

pub const Session = state.Session;

pub fn submitSource(
    session: *Session,
    header: *const protocol.SourceBatchHeader,
    fact_pointer: ?[*]const protocol.DisplayFact,
    fact_count: u32,
    text_pointer: ?[*]const protocol.TextInput,
    text_count: u32,
    byte_pointer: ?[*]const u8,
    byte_count: u32,
) ResultCode {
    const facts = constSlice(protocol.DisplayFact, fact_pointer, fact_count) orelse
        return .invalid_argument;
    const texts = constSlice(protocol.TextInput, text_pointer, text_count) orelse
        return .invalid_argument;
    const bytes = constByteSlice(byte_pointer, byte_count) orelse return .invalid_argument;
    return session.submitSource(header, facts, texts, bytes);
}

pub fn readNodes(
    session: *Session,
    input: *const protocol.ReadInput,
    pointer: ?[*]protocol.NodeOutput,
    count: u32,
) ResultCode {
    return session.readNodes(
        input,
        mutableSlice(protocol.NodeOutput, pointer, count) orelse return .invalid_argument,
    );
}

pub fn readEdges(
    session: *Session,
    input: *const protocol.ReadInput,
    pointer: ?[*]protocol.EdgeOutput,
    count: u32,
) ResultCode {
    return session.readEdges(
        input,
        mutableSlice(protocol.EdgeOutput, pointer, count) orelse return .invalid_argument,
    );
}

pub fn readCapabilities(
    session: *Session,
    input: *const protocol.ReadInput,
    pointer: ?[*]protocol.DisplayCapabilityOutput,
    count: u32,
) ResultCode {
    return session.readCapabilities(
        input,
        mutableSlice(protocol.DisplayCapabilityOutput, pointer, count) orelse
            return .invalid_argument,
    );
}

pub fn readTexts(
    session: *Session,
    input: *const protocol.ReadInput,
    text_pointer: ?[*]protocol.TextOutput,
    text_count: u32,
    byte_pointer: ?[*]u8,
    byte_count: u32,
) ResultCode {
    return session.readTexts(
        input,
        mutableSlice(protocol.TextOutput, text_pointer, text_count) orelse
            return .invalid_argument,
        mutableByteSlice(byte_pointer, byte_count) orelse return .invalid_argument,
    );
}

pub fn readDiff(
    session: *Session,
    input: *const protocol.ReadInput,
    pointer: ?[*]protocol.DiffEntry,
    count: u32,
) ResultCode {
    return session.readDiff(
        input,
        mutableSlice(protocol.DiffEntry, pointer, count) orelse return .invalid_argument,
    );
}

pub fn readUnresolved(
    session: *Session,
    input: *const protocol.ReadInput,
    pointer: ?[*]protocol.UnresolvedOutput,
    count: u32,
) ResultCode {
    return session.readUnresolved(
        input,
        mutableSlice(protocol.UnresolvedOutput, pointer, count) orelse
            return .invalid_argument,
    );
}

pub fn exportPersistence(
    session: *Session,
    input: *const protocol.PersistenceInput,
    header: *protocol.PersistenceHeader,
    fact_pointer: ?[*]protocol.DisplayFact,
    fact_count: u32,
    text_pointer: ?[*]protocol.TextInput,
    text_count: u32,
    byte_pointer: ?[*]u8,
    byte_count: u32,
) ResultCode {
    return session.exportPersistence(
        input,
        header,
        mutableSlice(protocol.DisplayFact, fact_pointer, fact_count) orelse
            return .invalid_argument,
        mutableSlice(protocol.TextInput, text_pointer, text_count) orelse
            return .invalid_argument,
        mutableByteSlice(byte_pointer, byte_count) orelse return .invalid_argument,
    );
}

pub fn importPersistence(
    session: *Session,
    input: *const protocol.PersistenceInput,
    header: *const protocol.PersistenceHeader,
    fact_pointer: ?[*]const protocol.DisplayFact,
    fact_count: u32,
    text_pointer: ?[*]const protocol.TextInput,
    text_count: u32,
    byte_pointer: ?[*]const u8,
    byte_count: u32,
) ResultCode {
    return session.importPersistence(
        input,
        header,
        constSlice(protocol.DisplayFact, fact_pointer, fact_count) orelse
            return .invalid_argument,
        constSlice(protocol.TextInput, text_pointer, text_count) orelse
            return .invalid_argument,
        constByteSlice(byte_pointer, byte_count) orelse return .invalid_argument,
    );
}

fn constSlice(comptime T: type, pointer: ?[*]const T, count: u32) ?[]const T {
    if (count == 0) return &.{};
    return (pointer orelse return null)[0..count];
}

fn mutableSlice(comptime T: type, pointer: ?[*]T, count: u32) ?[]T {
    if (count == 0) return &.{};
    return (pointer orelse return null)[0..count];
}

fn constByteSlice(pointer: ?[*]const u8, count: u32) ?[]const u8 {
    if (count == 0) return &.{};
    return (pointer orelse return null)[0..count];
}

fn mutableByteSlice(pointer: ?[*]u8, count: u32) ?[]u8 {
    if (count == 0) return &.{};
    return (pointer orelse return null)[0..count];
}
