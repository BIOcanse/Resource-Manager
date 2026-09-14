const std = @import("std");

pub const empty: u32 = 0;
pub const occupied: u32 = 1;
pub const tombstone: u32 = 2;

pub const HandleEntry = struct {
    key: u64 = 0,
    slot_plus_one: u32 = 0,
    state: u32 = empty,
};

pub const PairEntry = struct {
    first: u64 = 0,
    second: u64 = 0,
    slot_plus_one: u32 = 0,
    state: u32 = empty,
};

pub const HashEntry = struct {
    hash: u64 = 0,
    slot_plus_one: u32 = 0,
    state: u32 = empty,
};

pub fn clear(comptime T: type, entries: []T) void {
    @memset(entries, .{});
}

pub fn hashBytes(bytes: []const u8) u64 {
    return std.hash.Wyhash.hash(0x726d_7075_626c_6963, bytes);
}

pub fn findHandle(entries: []const HandleEntry, key: u64) ?usize {
    if (key == 0 or entries.len == 0) return null;
    const mask = entries.len - 1;
    var position = @as(usize, @intCast(std.hash.Wyhash.hash(0, std.mem.asBytes(&key)))) & mask;
    var visited: usize = 0;
    while (visited < entries.len) : (visited += 1) {
        const entry = entries[position];
        if (entry.state == empty) return null;
        if (entry.state == occupied and entry.key == key) {
            return @as(usize, entry.slot_plus_one - 1);
        }
        position = (position + 1) & mask;
    }
    return null;
}

pub fn insertHandle(entries: []HandleEntry, key: u64, slot: usize) bool {
    if (key == 0 or entries.len == 0 or slot >= std.math.maxInt(u32)) return false;
    const mask = entries.len - 1;
    var position = @as(usize, @intCast(std.hash.Wyhash.hash(0, std.mem.asBytes(&key)))) & mask;
    var first_tombstone: ?usize = null;
    var visited: usize = 0;
    while (visited < entries.len) : (visited += 1) {
        const entry = entries[position];
        if (entry.state == occupied and entry.key == key) return false;
        if (entry.state == tombstone and first_tombstone == null) first_tombstone = position;
        if (entry.state == empty) {
            const target = first_tombstone orelse position;
            entries[target] = .{
                .key = key,
                .slot_plus_one = @intCast(slot + 1),
                .state = occupied,
            };
            return true;
        }
        position = (position + 1) & mask;
    }
    if (first_tombstone) |target| {
        entries[target] = .{
            .key = key,
            .slot_plus_one = @intCast(slot + 1),
            .state = occupied,
        };
        return true;
    }
    return false;
}

pub fn removeHandle(entries: []HandleEntry, key: u64) bool {
    if (key == 0 or entries.len == 0) return false;
    const mask = entries.len - 1;
    var position = @as(usize, @intCast(std.hash.Wyhash.hash(0, std.mem.asBytes(&key)))) & mask;
    var visited: usize = 0;
    while (visited < entries.len) : (visited += 1) {
        const entry = entries[position];
        if (entry.state == empty) return false;
        if (entry.state == occupied and entry.key == key) {
            entries[position] = .{ .state = tombstone };
            return true;
        }
        position = (position + 1) & mask;
    }
    return false;
}

pub fn findPair(entries: []const PairEntry, first: u64, second: u64) ?usize {
    if (first == 0 or second == 0 or entries.len == 0) return null;
    const hash = pairHash(first, second);
    const mask = entries.len - 1;
    var position = @as(usize, @intCast(hash)) & mask;
    var visited: usize = 0;
    while (visited < entries.len) : (visited += 1) {
        const entry = entries[position];
        if (entry.state == empty) return null;
        if (entry.state == occupied and entry.first == first and entry.second == second) {
            return @as(usize, entry.slot_plus_one - 1);
        }
        position = (position + 1) & mask;
    }
    return null;
}

pub fn insertPair(entries: []PairEntry, first: u64, second: u64, slot: usize) bool {
    if (first == 0 or second == 0 or entries.len == 0 or slot >= std.math.maxInt(u32)) return false;
    const mask = entries.len - 1;
    var position = @as(usize, @intCast(pairHash(first, second))) & mask;
    var first_tombstone: ?usize = null;
    var visited: usize = 0;
    while (visited < entries.len) : (visited += 1) {
        const entry = entries[position];
        if (entry.state == occupied and entry.first == first and entry.second == second) return false;
        if (entry.state == tombstone and first_tombstone == null) first_tombstone = position;
        if (entry.state == empty) {
            const target = first_tombstone orelse position;
            entries[target] = .{
                .first = first,
                .second = second,
                .slot_plus_one = @intCast(slot + 1),
                .state = occupied,
            };
            return true;
        }
        position = (position + 1) & mask;
    }
    if (first_tombstone) |target| {
        entries[target] = .{
            .first = first,
            .second = second,
            .slot_plus_one = @intCast(slot + 1),
            .state = occupied,
        };
        return true;
    }
    return false;
}

pub fn removePair(entries: []PairEntry, first: u64, second: u64) bool {
    if (first == 0 or second == 0 or entries.len == 0) return false;
    const mask = entries.len - 1;
    var position = @as(usize, @intCast(pairHash(first, second))) & mask;
    var visited: usize = 0;
    while (visited < entries.len) : (visited += 1) {
        const entry = entries[position];
        if (entry.state == empty) return false;
        if (entry.state == occupied and entry.first == first and entry.second == second) {
            entries[position] = .{ .state = tombstone };
            return true;
        }
        position = (position + 1) & mask;
    }
    return false;
}

pub fn findHash(
    entries: []const HashEntry,
    hash: u64,
    context: anytype,
    comptime equals: fn (@TypeOf(context), usize) bool,
) ?usize {
    if (entries.len == 0) return null;
    const mask = entries.len - 1;
    var position = @as(usize, @intCast(hash)) & mask;
    var visited: usize = 0;
    while (visited < entries.len) : (visited += 1) {
        const entry = entries[position];
        if (entry.state == empty) return null;
        if (entry.state == occupied and entry.hash == hash) {
            const slot = @as(usize, entry.slot_plus_one - 1);
            if (equals(context, slot)) return slot;
        }
        position = (position + 1) & mask;
    }
    return null;
}

pub fn insertHash(
    entries: []HashEntry,
    hash: u64,
    slot: usize,
    context: anytype,
    comptime equals: fn (@TypeOf(context), usize) bool,
) bool {
    if (entries.len == 0 or slot >= std.math.maxInt(u32)) return false;
    const mask = entries.len - 1;
    var position = @as(usize, @intCast(hash)) & mask;
    var first_tombstone: ?usize = null;
    var visited: usize = 0;
    while (visited < entries.len) : (visited += 1) {
        const entry = entries[position];
        if (entry.state == occupied and entry.hash == hash) {
            const existing = @as(usize, entry.slot_plus_one - 1);
            if (equals(context, existing)) return false;
        }
        if (entry.state == tombstone and first_tombstone == null) first_tombstone = position;
        if (entry.state == empty) {
            const target = first_tombstone orelse position;
            entries[target] = .{
                .hash = hash,
                .slot_plus_one = @intCast(slot + 1),
                .state = occupied,
            };
            return true;
        }
        position = (position + 1) & mask;
    }
    if (first_tombstone) |target| {
        entries[target] = .{
            .hash = hash,
            .slot_plus_one = @intCast(slot + 1),
            .state = occupied,
        };
        return true;
    }
    return false;
}

fn pairHash(first: u64, second: u64) u64 {
    var values = [_]u64{ first, second };
    return std.hash.Wyhash.hash(0x726d_7061_6972, std.mem.asBytes(&values));
}
