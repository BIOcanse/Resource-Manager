const std = @import("std");

// This adapter consumes publications from the original process subscription. It
// owns only the replay cursor for one subscription, never a second sampler.
pub const State = extern struct { last_ticks: i64 = 0, has_last: i64 = 0 };
pub const Text = extern struct { pointer: ?[*]const u16 = null, length: usize = 0 };
pub const Capture = extern struct {
    snapshot_ticks: i64,
    field_ticks: i64,
    has_field_time: u32,
    row_count: u32,
    status: Text,
};
pub const RawRow = extern struct {
    pid: i32,
    has_count: u32,
    start_key: Text,
    negative: u32,
    reserved: u32,
    high_bits: u64,
    low_bits: u64,
};
pub const Emission = extern struct {
    pid: i32,
    unavailable: u32,
    birth: u64,
    value: u64,
    key: [20]u8,
    key_length: u32,
    has_birth: u32,
};
pub const Result = extern struct {
    disposition: u32 = 0,
    available: u32 = 0,
    from_fields: u32 = 0,
    unavailable_count: u32 = 0,
};
pub const Query = *const fn (?*anyopaque, usize, *RawRow) callconv(.c) u32;
pub const Sink = *const fn (?*anyopaque, *const Emission) callconv(.c) u32;

fn slice(text: Text) []const u16 {
    return if (text.pointer) |pointer| pointer[0..text.length] else &.{};
}
fn equals(text: Text, comptime ascii: []const u8) bool {
    const value = slice(text);
    if (value.len != ascii.len) return false;
    for (value, ascii) |actual, expected| if (actual != expected) return false;
    return true;
}

// ulong.TryParse(NumberStyles.None, InvariantCulture): ASCII digits only, no
// sign or whitespace, checked unsigned64 range. .NET also accepts trailing NULs.
fn parseBirth(text: Text) ?u64 {
    const value = slice(text);
    var length = value.len;
    while (length > 0 and value[length - 1] == 0) length -= 1;
    if (length == 0) return null;
    var result: u64 = 0;
    for (value[0..length]) |character| {
        if (character < '0' or character > '9') return null;
        const multiplied = @mulWithOverflow(result, @as(u64, 10));
        if (multiplied[1] != 0) return null;
        const added = @addWithOverflow(multiplied[0], @as(u64, character - '0'));
        if (added[1] != 0) return null;
        result = added[0];
    }
    return if (result == 0) null else result;
}
fn decimal(value: u64, output: *[20]u8) u32 {
    var remaining = value;
    var reverse: [20]u8 = undefined;
    var length: usize = 0;
    while (remaining != 0) {
        reverse[length] = @as(u8, @intCast(remaining % 10)) + '0';
        length += 1;
        remaining /= 10;
    }
    for (0..length) |index| output[index] = reverse[length - index - 1];
    return @intCast(length);
}

pub fn capture(state: *State, input: Capture, context: ?*anyopaque, query: Query, sink: Sink) Result {
    if (equals(input.status, "warming")) return .{};
    if (input.has_field_time == 0 or !equals(input.status, "ready")) {
        state.has_last = 0;
        return .{ .disposition = 1 };
    }
    if (state.has_last != 0 and input.field_ticks <= state.last_ticks) return .{};
    state.last_ticks = input.field_ticks;
    state.has_last = 1;
    // Original source's declared admission bound, not an additional allocation.
    if (input.row_count > 16384) return .{ .disposition = 3 };
    var result = Result{ .disposition = 2, .from_fields = 1 };
    var samples: usize = 0;
    var index: usize = 0;
    while (index < input.row_count) : (index += 1) {
        var row: RawRow = undefined;
        if (query(context, index, &row) != 0) return .{ .disposition = 4 };
        if (row.pid <= 0) continue;
        const birth = parseBirth(row.start_key);
        var emitted = Emission{
            .pid = row.pid,
            .unavailable = @intFromBool(birth == null or row.has_count == 0 or row.negative != 0 or row.high_bits != 0),
            .birth = birth orelse 0,
            .value = row.low_bits,
            .key = @splat(0),
            .key_length = 0,
            .has_birth = @intFromBool(birth != null),
        };
        if (birth) |identity| emitted.key_length = decimal(identity, &emitted.key);
        if (emitted.unavailable != 0) result.unavailable_count += 1 else samples += 1;
        if (sink(context, &emitted) != 0) return .{ .disposition = 4 };
    }
    result.available = @intFromBool(samples > 0 or result.unavailable_count == 0);
    return result;
}
