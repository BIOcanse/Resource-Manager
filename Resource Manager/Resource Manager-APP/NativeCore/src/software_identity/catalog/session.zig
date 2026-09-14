const protocol = @import("protocol.zig");
const state = @import("session_state.zig");
const ResultCode = @import("../../common/result_codes.zig").ResultCode;

pub const Session = state.Session;

pub fn replace(
    session: *Session,
    input: *const protocol.ReplaceInput,
    entry_pointer: ?[*]const protocol.EntryInput,
    entry_count: u32,
    alias_pointer: ?[*]const protocol.AliasInput,
    alias_count: u32,
    root_pointer: ?[*]const protocol.RootInput,
    root_count: u32,
    key_pointer: ?[*]const u8,
    key_byte_count: u32,
) ResultCode {
    const entries = itemSlice(protocol.EntryInput, entry_pointer, entry_count) orelse
        return .invalid_argument;
    const aliases = itemSlice(protocol.AliasInput, alias_pointer, alias_count) orelse
        return .invalid_argument;
    const roots = itemSlice(protocol.RootInput, root_pointer, root_count) orelse
        return .invalid_argument;
    const key_bytes = byteSlice(key_pointer, key_byte_count) orelse return .invalid_argument;
    return session.replace(input, entries, aliases, roots, key_bytes);
}

pub fn matchKnown(
    session: *Session,
    input: *const protocol.KnownQueryInput,
    signal_pointer: ?[*]const protocol.KnownSignalInput,
    signal_count: u32,
    key_pointer: ?[*]const u8,
    key_byte_count: u32,
    output: *protocol.KnownMatchOutput,
) ResultCode {
    const signals = itemSlice(protocol.KnownSignalInput, signal_pointer, signal_count) orelse
        return .invalid_argument;
    const key_bytes = byteSlice(key_pointer, key_byte_count) orelse return .invalid_argument;
    return session.matchKnown(input, signals, key_bytes, output);
}

pub fn match(
    session: *Session,
    input: *const protocol.QueryInput,
    fact_pointer: ?[*]const protocol.FactInput,
    fact_count: u32,
    key_pointer: ?[*]const u8,
    key_byte_count: u32,
    output: *protocol.MatchOutput,
) ResultCode {
    const facts = itemSlice(protocol.FactInput, fact_pointer, fact_count) orelse
        return .invalid_argument;
    const key_bytes = byteSlice(key_pointer, key_byte_count) orelse return .invalid_argument;
    return session.match(input, facts, key_bytes, output);
}

fn itemSlice(comptime T: type, pointer: ?[*]const T, count: u32) ?[]const T {
    if (count == 0) return &.{};
    return (pointer orelse return null)[0..count];
}

fn byteSlice(pointer: ?[*]const u8, count: u32) ?[]const u8 {
    if (count == 0) return &.{};
    return (pointer orelse return null)[0..count];
}
