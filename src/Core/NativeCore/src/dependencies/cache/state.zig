const std = @import("std");
const ordinal = @import("../probe/ordinal.zig");

// Persistent records contain offsets and opaque host roots, never borrowed addresses.
pub const Header = extern struct { used: u64, incarnation: u64, generation: u64, status_probes: u64, provider_probes: u64 };
const Record = extern struct { bytes: u64, source: u64, projection: u64, kind: u32, key_length: u32 };
pub const Result = extern struct {
    incarnation: u64 = 0,
    generation: u64 = 0,
    projection: u64 = 0,
    released: u64 = 0,
    required: u64 = 0,
    status_probes: u64 = 0,
    provider_probes: u64 = 0,
};
pub fn initialize(region: []align(8) u8, incarnation: u64, statuses: u64, providers: u64) bool {
    if (region.len < @sizeOf(Header)) return false;
    const header: *Header = @ptrCast(region.ptr);
    header.* = .{ .used = @sizeOf(Header), .incarnation = incarnation, .generation = 0, .status_probes = statuses, .provider_probes = providers };
    return true;
}
fn whitespace(c: u16) bool {
    return switch (c) {
        0x0009...0x000d, 0x0020, 0x0085, 0x00a0, 0x1680, 0x2000...0x200a, 0x2028, 0x2029, 0x202f, 0x205f, 0x3000 => true,
        else => false,
    };
}
fn normalized(key: []const u16, kind: u32) []const u16 {
    if (kind != 1) return key;
    var start: usize = 0;
    var end = key.len;
    while (start < end and whitespace(key[start])) : (start += 1) {}
    while (end > start and whitespace(key[end - 1])) : (end -= 1) {}
    return key[start..end];
}
fn equal(a: []const u16, b: []const u16, unicode_profile: u32) bool {
    return ordinal.equal(a, b, unicode_profile);
}

// operation: lookup=1, commit=2, invalidate=3, inspect=4. kind: statuses=1, provider=2.
// Return 1 for bounded-capacity refusal; refusal never partially mutates state.
pub fn execute(region: []align(8) u8, operation: u32, kind: u32, unicode_profile: u32, raw_key: []const u16, source: u64, incarnation: u64, generation: u64, projection: u64, result: *Result) u32 {
    if (region.len < @sizeOf(Header)) return 2;
    const header: *Header = @ptrCast(region.ptr);
    if (header.used < @sizeOf(Header) or header.used > region.len) return 2;
    result.* = .{ .incarnation = header.incarnation, .generation = header.generation, .required = header.used, .status_probes = header.status_probes, .provider_probes = header.provider_probes };
    if (operation == 4) return 0;
    if (operation == 3) {
        header.generation +%= 1;
        header.used = @sizeOf(Header);
        result.generation = header.generation;
        result.required = header.used;
        return 0;
    }
    if ((operation != 1 and operation != 2) or (kind != 1 and kind != 2)) return 2;
    const key = normalized(raw_key, kind);
    var offset: usize = @sizeOf(Header);
    var found: ?usize = null;
    while (offset < header.used) {
        if (header.used - offset < @sizeOf(Record)) return 2;
        const record: *Record = @ptrCast(@alignCast(region.ptr + offset));
        if (record.bytes < @sizeOf(Record) or record.bytes % 8 != 0 or record.bytes > header.used - offset or @as(u64, record.key_length) * 2 > record.bytes - @sizeOf(Record)) return 2;
        const stored: [*]const u16 = @ptrCast(@alignCast(region.ptr + offset + @sizeOf(Record)));
        if (record.kind == kind and equal(stored[0..record.key_length], key, unicode_profile)) {
            found = offset;
            break;
        }
        offset += @intCast(record.bytes);
    }
    if (operation == 1) {
        if (found) |at| {
            const held: *Record = @ptrCast(@alignCast(region.ptr + at));
            if (kind == 2 or held.source == source) result.projection = held.projection;
        }
        if (result.projection == 0) {
            if (kind == 1) header.status_probes +%= 1 else header.provider_probes +%= 1;
            result.status_probes = header.status_probes;
            result.provider_probes = header.provider_probes;
        }
        return 0;
    }
    // Invalidated/reclaimed results still belong to their caller, never to this cache.
    if (incarnation != header.incarnation or generation != header.generation) return 0;
    if (projection == 0) return 2;
    const bytes = std.mem.alignForward(usize, @sizeOf(Record) + key.len * 2, 8);
    var old_bytes: usize = 0;
    if (found) |at| {
        const previous: *Record = @ptrCast(@alignCast(region.ptr + at));
        old_bytes = @intCast(previous.bytes);
    }
    const required = header.used - old_bytes + bytes;
    if (required > region.len) {
        result.required = required;
        return 1;
    }
    if (found) |at| {
        const previous: *Record = @ptrCast(@alignCast(region.ptr + at));
        result.released = previous.projection;
        const end = at + old_bytes;
        std.mem.copyForwards(u8, region[at..@intCast(header.used - old_bytes)], region[end..@intCast(header.used)]);
        header.used -= old_bytes;
    }
    const destination: *Record = @ptrCast(@alignCast(region.ptr + header.used));
    destination.* = .{ .bytes = bytes, .source = source, .projection = projection, .kind = kind, .key_length = @intCast(key.len) };
    const target: [*]u16 = @ptrCast(@alignCast(region.ptr + header.used + @sizeOf(Record)));
    @memcpy(target[0..key.len], key);
    header.used += bytes;
    result.projection = projection;
    result.required = header.used;
    return 0;
}
