const std = @import("std");

// Classic kernel MOF layouts: Microsoft PerfView KernelTraceEventParser.cs.
// All slices borrow the ETW buffer and expire when the native sink returns.
pub const Guid = extern struct {
    data1: u32,
    data2: u16,
    data3: u16,
    data4: [8]u8,

    pub fn eql(a: Guid, b: Guid) bool {
        return a.data1 == b.data1 and a.data2 == b.data2 and a.data3 == b.data3 and std.mem.eql(u8, &a.data4, &b.data4);
    }
};

pub const process_provider: Guid = .{ .data1 = 0x3d6fa8d0, .data2 = 0xfe05, .data3 = 0x11d0, .data4 = .{ 0x9d, 0xda, 0, 0xc0, 0x4f, 0xd7, 0xba, 0x7c } };
pub const thread_provider: Guid = .{ .data1 = 0x3d6fa8d1, .data2 = 0xfe05, .data3 = 0x11d0, .data4 = process_provider.data4 };
pub const perfinfo_provider: Guid = .{ .data1 = 0xce1dbfb4, .data2 = 0x137e, .data3 = 0x4da6, .data4 = .{ 0x87, 0xb0, 0x3f, 0x59, 0xaa, 0x10, 0x2c, 0xbc } };
pub const image_provider: Guid = .{ .data1 = 0x2cb15d1d, .data2 = 0x5fc1, .data3 = 0x11d2, .data4 = .{ 0xab, 0xe1, 0, 0xa0, 0xc9, 0x11, 0xf5, 0x18 } };
pub const lost_provider: Guid = .{ .data1 = 0x6a399ae0, .data2 = 0x4bc6, .data3 = 0x4de9, .data4 = .{ 0x87, 0x0b, 0x36, 0x57, 0xf8, 0x94, 0x7e, 0x7e } };

pub const RawEvent = struct {
    provider: Guid,
    opcode: u8,
    version: u8,
    pointer_size: u8,
    qpc: i64,
    processor: u16,
    header_process_id: u32,
    header_thread_id: u32,
    payload: []const u8,
};
pub const Lifecycle = enum { start, end, rundown_start, rundown_end };
pub const Process = struct {
    lifecycle: Lifecycle,
    process_id: u32,
    parent_id: u32,
    unique_key: ?u64,
    session_id: ?u32,
    exit_status: ?u32,
    image_name: []const u8,
    command_line_utf16le: []const u8,
};
pub const Thread = struct { lifecycle: Lifecycle, process_id: u32, thread_id: u32 };
pub const CSwitch = struct { new_thread_id: u32, old_thread_id: u32 };
pub const InterruptKind = enum { isr, dpc, threaded_dpc, timer_dpc };
pub const Interrupt = struct { kind: InterruptKind, initial_qpc: i64, routine: u64 };
pub const Image = struct {
    lifecycle: Lifecycle,
    process_id: u32,
    base: u64,
    size: u64,
    // Raw kernel path. Device-to-DOS mapping belongs to the native owner.
    path_utf16le: []const u8,
};
pub const Event = struct {
    qpc: i64,
    processor: u16,
    data: union(enum) { process: Process, thread: Thread, cswitch: CSwitch, interrupt: Interrupt, image: Image, lost: void },
};
pub const DecodeError = error{ UnsupportedVersion, InvalidPointerSize, TruncatedPayload, InvalidPayload, InvalidTimestamp };

fn uint(comptime T: type, bytes: []const u8, offset: usize) DecodeError!T {
    if (offset > bytes.len or bytes.len - offset < @sizeOf(T)) return error.TruncatedPayload;
    return std.mem.readInt(T, bytes[offset..][0..@sizeOf(T)], .little);
}
fn pointer(raw: RawEvent, offset: usize) DecodeError!u64 {
    return if (raw.pointer_size == 4) try uint(u32, raw.payload, offset) else try uint(u64, raw.payload, offset);
}
fn minimum(bytes: []const u8, size: usize) DecodeError!void {
    if (bytes.len < size) return error.TruncatedPayload;
}
fn exact(bytes: []const u8, size: usize) DecodeError!void {
    try minimum(bytes, size);
    if (bytes.len != size) return error.InvalidPayload;
}
fn lifecycle(opcode: u8) ?Lifecycle {
    return switch (opcode) {
        1, 10 => .start,
        2 => .end,
        3 => .rundown_start,
        4 => .rundown_end,
        else => null,
    };
}
const Text = struct { bytes: []const u8, next: usize };
fn ansi(bytes: []const u8, offset: usize) DecodeError!Text {
    if (offset >= bytes.len) return error.TruncatedPayload;
    const count = std.mem.indexOfScalar(u8, bytes[offset..], 0) orelse return error.InvalidPayload;
    return .{ .bytes = bytes[offset..][0..count], .next = offset + count + 1 };
}
fn wide(bytes: []const u8, offset: usize) DecodeError!Text {
    var end = offset;
    while (end < bytes.len and bytes.len - end >= 2) : (end += 2) {
        if (bytes[end] == 0 and bytes[end + 1] == 0) return .{ .bytes = bytes[offset..end], .next = end + 2 };
    }
    return error.InvalidPayload;
}
fn skipSid(raw: RawEvent, offset: usize) DecodeError!usize {
    if (try uint(u32, raw.payload, offset) == 0) return offset + 4;
    const sid = offset + @as(usize, raw.pointer_size) * 2;
    try minimum(raw.payload, sid + 8);
    if (raw.payload[sid] != 1 or raw.payload[sid + 1] > 15) return error.InvalidPayload;
    const end = sid + 8 + @as(usize, raw.payload[sid + 1]) * 4;
    try minimum(raw.payload, end);
    return end;
}

/// null means a different provider/opcode; errors mean a recognized record cannot
/// safely contribute to an authoritative aggregate. No zero-valued substitutes.
pub fn decode(raw: RawEvent) DecodeError!?Event {
    const is_process = raw.provider.eql(process_provider) and raw.opcode >= 1 and raw.opcode <= 4;
    const is_thread = raw.provider.eql(thread_provider) and raw.opcode >= 1 and raw.opcode <= 4;
    const is_switch = raw.provider.eql(thread_provider) and raw.opcode == 36;
    // TraceEvent's PerfInfoISR binds both classic opcode 67 and opcode 50.
    const is_interrupt = raw.provider.eql(perfinfo_provider) and (raw.opcode == 50 or (raw.opcode >= 66 and raw.opcode <= 69));
    const is_image = raw.provider.eql(image_provider) and (raw.opcode == 10 or (raw.opcode >= 2 and raw.opcode <= 4));
    const is_lost = raw.provider.eql(lost_provider) and raw.opcode == 32;
    if (!is_process and !is_thread and !is_switch and !is_interrupt and !is_image and !is_lost) return null;
    if (raw.pointer_size != 4 and raw.pointer_size != 8) return error.InvalidPointerSize;
    if (raw.qpc < 0) return error.InvalidTimestamp;
    const p: usize = raw.pointer_size;
    const delta = p - 4;
    var event: Event = .{ .qpc = raw.qpc, .processor = raw.processor, .data = undefined };
    if (is_process) {
        if (raw.version > 5) return error.UnsupportedVersion;
        const sid_offset: usize = switch (raw.version) {
            0 => p * 2,
            1, 2 => 20 + delta,
            3 => 24 + delta * 2,
            4, 5 => 28 + delta * 2,
            else => unreachable,
        };
        const image = try ansi(raw.payload, try skipSid(raw, sid_offset));
        var end = image.next;
        var command: []const u8 = &.{};
        if (raw.version >= 2) {
            const text = try wide(raw.payload, end);
            command = text.bytes;
            end = text.next;
        }
        if (raw.version >= 4) {
            end = (try wide(raw.payload, end)).next;
            end = (try wide(raw.payload, end)).next;
        }
        if (raw.version >= 2 and raw.version <= 4) try exact(raw.payload, end);
        if (raw.version == 5) try minimum(raw.payload, end + 8);
        const pid: u32 = if (raw.version == 0) std.math.cast(u32, try pointer(raw, 0)) orelse return error.InvalidPayload else try uint(u32, raw.payload, p);
        const parent: u32 = if (raw.version == 0) std.math.cast(u32, try pointer(raw, p)) orelse return error.InvalidPayload else try uint(u32, raw.payload, p + 4);
        event.data = .{ .process = .{
            .lifecycle = lifecycle(raw.opcode).?,
            .process_id = pid,
            .parent_id = parent,
            .unique_key = if (raw.version >= 2) try pointer(raw, 0) else null,
            .session_id = if (raw.version >= 1) try uint(u32, raw.payload, p + 8) else null,
            .exit_status = if (raw.version >= 1) try uint(u32, raw.payload, p + 12) else null,
            .image_name = image.bytes,
            .command_line_utf16le = command,
        } };
    } else if (is_thread) {
        switch (raw.version) {
            0 => try exact(raw.payload, 8),
            1 => try minimum(raw.payload, 8),
            2 => try exact(raw.payload, 40 + delta * 7),
            3 => {
                const fixed = 40 + delta * 7;
                try minimum(raw.payload, fixed);
                // Priority extension is optional on older v3 recordings.
                if (raw.payload.len > fixed and raw.payload.len < fixed + 4) return error.TruncatedPayload;
                if (raw.payload.len > fixed + 4) try exact(raw.payload, (try wide(raw.payload, fixed + 4)).next);
            },
            else => return error.UnsupportedVersion,
        }
        event.data = .{ .thread = .{ .lifecycle = lifecycle(raw.opcode).?, .process_id = try uint(u32, raw.payload, if (raw.version == 0) 4 else 0), .thread_id = try uint(u32, raw.payload, if (raw.version == 0) 0 else 4) } };
    } else if (is_switch) {
        // PerfView uses the same first 24 bytes for v2 and newer records.
        // Current Windows v5 adds a four-byte tail (observed in real ETL).
        // Accept only these verified versions and their complete lengths.
        try exact(raw.payload, switch (raw.version) {
            2 => 24,
            5 => 28,
            else => return error.UnsupportedVersion,
        });
        event.data = .{ .cswitch = .{ .new_thread_id = try uint(u32, raw.payload, 0), .old_thread_id = try uint(u32, raw.payload, 4) } };
    } else if (is_interrupt) {
        if (raw.version != 2) return error.UnsupportedVersion;
        // Opcode 50 v2 includes the four-byte message tail: observed x64
        // payload is 24 bytes. Opcode 67 v2 uses the 20-byte x64 ISR layout.
        try exact(raw.payload, switch (raw.opcode) {
            50 => 16 + p,
            67 => 12 + p,
            else => 8 + p,
        });
        const initial: i64 = @bitCast(try uint(u64, raw.payload, 0));
        if (initial < 0 or initial > raw.qpc) return error.InvalidTimestamp;
        event.data = .{ .interrupt = .{ .kind = switch (raw.opcode) {
            66 => .threaded_dpc,
            50, 67 => .isr,
            68 => .dpc,
            69 => .timer_dpc,
            else => unreachable,
        }, .initial_qpc = initial, .routine = try pointer(raw, 8) } };
    } else if (is_image) {
        const offset: usize = switch (raw.version) {
            0 => p + 4,
            1 => 12 + delta * 2,
            2, 3 => 44 + delta * 3,
            else => return error.UnsupportedVersion,
        };
        const path = try wide(raw.payload, offset);
        try exact(raw.payload, path.next);
        const size = if (raw.version == 0) try uint(u32, raw.payload, p) else try pointer(raw, p);
        const base = try pointer(raw, 0);
        if (size > std.math.maxInt(u64) - base) return error.InvalidPayload;
        event.data = .{ .image = .{ .lifecycle = lifecycle(raw.opcode).?, .process_id = if (raw.version == 0) raw.header_process_id else try uint(u32, raw.payload, p * 2), .base = base, .size = size, .path_utf16le = path.bytes } };
    } else {
        event.data = .{ .lost = {} };
    }
    return event;
}
