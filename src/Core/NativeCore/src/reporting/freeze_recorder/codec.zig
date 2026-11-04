const std = @import("std");
const t = @import("types.zig");

// Versioned, little endian, independent of Zig padding/optional representation.
pub const legacy_payload_bytes = 104 + 24 * t.disk_capacity +
    (16 + t.image_name_capacity) * t.process_capacity;
const legacy_record_bytes = std.mem.alignForward(usize, legacy_payload_bytes, 128);
// Two disk reasons per byte keep the default extension at 28 bytes, inside
// v1's 88 padding bytes. Every supported capacity still reserves its full tail.
pub const extension_bytes = 20 + (t.disk_capacity + 1) / 2;
pub const record_bytes = std.mem.alignForward(usize, legacy_payload_bytes + extension_bytes, 128);
const extension_magic: u32 = 0x31504147; // "GAP1", extension version 1.
const magic: u64 = 0x524D465245455A45;
// 16/3/48 identify the original v1 wire layout, not policy defaults. Encode
// capacity changes even when alignment leaves the record byte length unchanged.
const version: u32 = 1 | ((t.disk_capacity ^ 16) << 24) |
    ((t.process_capacity ^ 3) << 16) | ((t.image_name_capacity ^ 48) << 8) |
    (if (record_bytes != legacy_record_bytes) @as(u32, 0x80) else 0);
const checksum_offset = 32;

pub fn encode(bytes: *[record_bytes]u8, record: *const t.Record) !void {
    const s = &record.sample;
    if (record.sequence == 0 or s.disk_count > t.disk_capacity or s.process_count > t.process_capacity)
        return error.InvalidRecord;
    @memset(bytes, 0);
    var w: Writer = .{ .bytes = bytes };
    w.put(u64, magic);
    w.put(u32, version);
    w.put(u32, record_bytes);
    w.put(u64, record.sequence);
    w.put(i64, s.timestamp_ms);
    w.put(u32, 0); // CRC excludes itself.
    var mask: u32 = 0;
    const memory = .{ s.commit_bytes, s.commit_limit_bytes, s.physical_total_bytes, s.physical_available_bytes, s.paged_pool_bytes, s.nonpaged_pool_bytes };
    inline for (memory, 0..) |value, bit| {
        if (value != null) mask |= @as(u32, 1) << bit;
    }
    if (s.interrupt_cpu_percent != null) mask |= 1 << 6;
    if (s.longest_interrupt_us != null) mask |= 1 << 7;
    if (s.kernel_leak_active != null) mask |= 1 << 8;
    w.put(u32, mask);
    w.put(u8, s.disk_count);
    w.put(u8, s.process_count);
    w.put(u8, @as(u8, @intFromBool(s.disks_complete)) | (@as(u8, @intFromBool(s.processes_complete)) << 1));
    w.put(u8, @intFromBool(s.kernel_leak_active orelse false));
    inline for (memory) |value| w.put(u64, value orelse 0);
    w.put(u32, @bitCast(s.interrupt_cpu_percent orelse 0));
    w.put(u64, s.longest_interrupt_us orelse 0);
    var i: usize = 0;
    while (i < t.disk_capacity) : (i += 1) {
        const d = s.disks[i];
        w.put(u32, d.physical_disk_id);
        w.put(u32, @as(u32, @intFromBool(d.queue_depth != null)) |
            (@as(u32, @intFromBool(d.latency_ms != null)) << 1) |
            (@as(u32, @intFromBool(d.completed_operations != null)) << 2));
        w.put(u32, @bitCast(d.queue_depth orelse 0));
        w.put(u32, @bitCast(d.latency_ms orelse 0));
        w.put(u64, d.completed_operations orelse 0);
    }
    i = 0;
    while (i < t.process_capacity) : (i += 1) {
        const p = s.top_processes[i];
        if (p.image_name_len > t.image_name_capacity) return error.InvalidRecord;
        w.put(u32, p.pid);
        w.put(u8, p.image_name_len);
        w.put(u8, 0);
        w.put(u16, 0);
        w.put(u64, p.private_commit_bytes);
        @memcpy(bytes[w.at..][0..t.image_name_capacity], &p.image_name);
        w.at += t.image_name_capacity;
    }
    w.put(u32, extension_magic);
    w.put(u8, @as(u8, @intFromBool(s.interrupt_timestamp_ms != null)) |
        (@as(u8, @intFromBool(s.interrupt_window_ms != null)) << 1));
    w.put(u8, reasonTag(s.interrupt_unavailable_reason));
    w.put(u8, reasonTag(s.kernel_leak_unavailable_reason));
    w.put(u8, 0);
    w.put(i64, s.interrupt_timestamp_ms orelse 0);
    w.put(u32, s.interrupt_window_ms orelse 0);
    i = 0;
    while (i < t.disk_capacity) : (i += 2) {
        const low = reasonTag(s.disks[i].completed_operations_unavailable_reason);
        const high = if (i + 1 < t.disk_capacity)
            reasonTag(s.disks[i + 1].completed_operations_unavailable_reason)
        else
            @as(u8, 0);
        w.put(u8, low | (high << 4));
    }
    std.mem.writeInt(u32, bytes[checksum_offset..][0..4], checksum(bytes), .little);
}

pub fn decode(bytes: *const [record_bytes]u8) !t.Record {
    var r: Reader = .{ .bytes = bytes };
    if (r.get(u64) != magic or r.get(u32) != version or r.get(u32) != record_bytes)
        return error.InvalidRecord;
    var record: t.Record = .{ .sequence = r.get(u64), .sample = .{ .timestamp_ms = r.get(i64) } };
    if (record.sequence == 0 or r.get(u32) != checksum(bytes)) return error.InvalidRecord;
    const s = &record.sample;
    const mask = r.get(u32);
    if (mask & ~@as(u32, 0x1ff) != 0) return error.InvalidRecord;
    s.disk_count = r.get(u8);
    s.process_count = r.get(u8);
    if (s.disk_count > t.disk_capacity or s.process_count > t.process_capacity) return error.InvalidRecord;
    const flags = r.get(u8);
    if (flags & ~@as(u8, 3) != 0) return error.InvalidRecord;
    s.disks_complete = flags & 1 != 0;
    s.processes_complete = flags & 2 != 0;
    const leak = r.get(u8);
    if (leak > 1) return error.InvalidRecord;
    s.kernel_leak_active = if (mask & (1 << 8) != 0) leak != 0 else null;
    const memory = .{ &s.commit_bytes, &s.commit_limit_bytes, &s.physical_total_bytes, &s.physical_available_bytes, &s.paged_pool_bytes, &s.nonpaged_pool_bytes };
    inline for (memory, 0..) |value, bit| {
        const raw = r.get(u64);
        value.* = if (mask & (@as(u32, 1) << bit) != 0) raw else null;
    }
    const interrupt: f32 = @bitCast(r.get(u32));
    const longest = r.get(u64);
    s.interrupt_cpu_percent = if (mask & (1 << 6) != 0) interrupt else null;
    s.longest_interrupt_us = if (mask & (1 << 7) != 0) longest else null;
    var i: usize = 0;
    while (i < t.disk_capacity) : (i += 1) {
        const d = &s.disks[i];
        d.physical_disk_id = r.get(u32);
        const valid = r.get(u32);
        if (valid & ~@as(u32, 7) != 0) return error.InvalidRecord;
        const queue: f32 = @bitCast(r.get(u32));
        const latency: f32 = @bitCast(r.get(u32));
        const completed = r.get(u64);
        d.queue_depth = if (valid & 1 != 0) queue else null;
        d.latency_ms = if (valid & 2 != 0) latency else null;
        d.completed_operations = if (valid & 4 != 0) completed else null;
    }
    i = 0;
    while (i < t.process_capacity) : (i += 1) {
        const p = &s.top_processes[i];
        p.pid = r.get(u32);
        p.image_name_len = r.get(u8);
        _ = r.get(u8);
        _ = r.get(u16);
        p.private_commit_bytes = r.get(u64);
        if (p.image_name_len > t.image_name_capacity) return error.InvalidRecord;
        @memcpy(&p.image_name, bytes[r.at..][0..t.image_name_capacity]);
        r.at += t.image_name_capacity;
    }
    const marker = r.get(u32);
    if (marker == 0) {
        // The only markerless layout is the old v1 all-zero padding. Do not
        // silently reinterpret an unknown extension as missing provenance.
        try requireZero(bytes[r.at..]);
        return record;
    }
    if (marker != extension_magic) return error.InvalidRecord;
    const extension_flags = r.get(u8);
    if (extension_flags & ~@as(u8, 3) != 0) return error.InvalidRecord;
    s.interrupt_unavailable_reason = try decodeReason(r.get(u8));
    s.kernel_leak_unavailable_reason = try decodeReason(r.get(u8));
    if (r.get(u8) != 0) return error.InvalidRecord;
    const interrupt_timestamp = r.get(i64);
    const interrupt_window = r.get(u32);
    s.interrupt_timestamp_ms = if (extension_flags & 1 != 0) interrupt_timestamp else null;
    s.interrupt_window_ms = if (extension_flags & 2 != 0) interrupt_window else null;
    i = 0;
    while (i < t.disk_capacity) : (i += 2) {
        const reasons = r.get(u8);
        s.disks[i].completed_operations_unavailable_reason = try decodeReason(reasons & 0xf);
        if (i + 1 < t.disk_capacity) {
            s.disks[i + 1].completed_operations_unavailable_reason = try decodeReason(reasons >> 4);
        } else if (reasons >> 4 != 0) return error.InvalidRecord;
    }
    try requireZero(bytes[r.at..]);
    return record;
}

fn reasonTag(reason: ?t.GapReason) u8 {
    return if (reason) |value| @backingInt(value) else 0;
}

fn decodeReason(raw: u8) !?t.GapReason {
    return switch (raw) {
        0 => null,
        1...8 => @as(t.GapReason, @fromBackingInt(@intCast(raw))),
        else => error.InvalidRecord,
    };
}

fn requireZero(bytes: []const u8) !void {
    var i: usize = 0;
    while (i < bytes.len) : (i += 1) {
        if (bytes[i] != 0) return error.InvalidRecord;
    }
}

fn checksum(bytes: *const [record_bytes]u8) u32 {
    var crc = std.hash.Crc32.init();
    crc.update(bytes[0..checksum_offset]);
    crc.update(bytes[checksum_offset + 4 ..]);
    return crc.final();
}

const Writer = struct {
    bytes: *[record_bytes]u8,
    at: usize = 0,
    fn put(self: *Writer, comptime T: type, value: T) void {
        std.mem.writeInt(T, self.bytes[self.at..][0..@sizeOf(T)], value, .little);
        self.at += @sizeOf(T);
    }
};
const Reader = struct {
    bytes: *const [record_bytes]u8,
    at: usize = 0,
    fn get(self: *Reader, comptime T: type) T {
        const value = std.mem.readInt(T, self.bytes[self.at..][0..@sizeOf(T)], .little);
        self.at += @sizeOf(T);
        return value;
    }
};
