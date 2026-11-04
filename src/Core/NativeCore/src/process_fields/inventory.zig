//! Borrow one original SystemProcessInformation table. No allocation, current, token or handle owner.
//! The synchronous visitor builds the original owner's candidate and rejects duplicate PIDs.
//! Nothing is published until the whole walk and the visitor have both succeeded.
const std = @import("std");

pub const Status = enum(i32) { ok = 0, invalid_argument = 1, invalid_table = 2, visitor_rejected = 3 };
pub const Limits = extern struct {
    maximum_bytes: usize,
    maximum_processes: usize,
    maximum_threads_per_process: u32,
};
pub const Flags = struct {
    pub const creation: u32 = 1;
    pub const parent: u32 = 2;
    pub const name: u32 = 4;
    pub const thread_count: u32 = 8;
};
pub const Row = extern struct {
    process_id: i32,
    parent_process_id: i32,
    session_id: u32,
    flags: u32,
    creation_filetime_100ns: i64,
    name: ?[*]align(1) const u16,
    name_length: usize,
    kernel_time_100ns: u64,
    user_time_100ns: u64,
    working_set: u64,
    private_commit: u64,
    peak_private_commit: u64,
    private_working_set: u64,
    read_operations: u64,
    write_operations: u64,
    other_operations: u64,
    read_bytes: u64,
    write_bytes: u64,
    other_bytes: u64,
    handle_count: u32,
    thread_count: u32,
};
pub const Visitor = *const fn (?*anyopaque, *const Row) callconv(.c) i32;

// Windows ABI offsets, not product choices. Matches the existing managed header (256/184 bytes).
// x64: phnt ntexapi.h _SYSTEM_PROCESS_INFORMATION; x86 keeps its eight-byte LARGE_INTEGER fields.
pub const Layout = struct {
    pointer_bytes: usize,
    header_bytes: usize,
    name_pointer: usize,
    process_id: usize,
    parent_process_id: usize,
    handle_count: usize,
    session_id: usize,
    working_set: usize,
    private_commit: usize,
    peak_private_commit: usize,
    io: usize,

    pub fn forPointerBytes(bytes: usize) ?Layout {
        return switch (bytes) {
            8 => .{ .pointer_bytes = 8, .header_bytes = 256, .name_pointer = 64, .process_id = 80, .parent_process_id = 88, .handle_count = 96, .session_id = 100, .working_set = 144, .private_commit = 184, .peak_private_commit = 192, .io = 208 },
            4 => .{ .pointer_bytes = 4, .header_bytes = 184, .name_pointer = 60, .process_id = 68, .parent_process_id = 72, .handle_count = 76, .session_id = 80, .working_set = 104, .private_commit = 124, .peak_private_commit = 128, .io = 136 },
            else => null,
        };
    }
};

fn unsigned(comptime T: type, bytes: []const u8, offset: usize) T {
    return std.mem.readInt(T, bytes[offset..][0..@sizeOf(T)], .little);
}
fn pointer(bytes: []const u8, offset: usize, layout: Layout) u64 {
    return if (layout.pointer_bytes == 8) unsigned(u64, bytes, offset) else unsigned(u32, bytes, offset);
}

fn shape(data: []const u8, limits: Limits, layout: Layout) bool {
    if (limits.maximum_processes == 0 or limits.maximum_bytes == 0 or limits.maximum_threads_per_process == 0 or
        data.len == 0 or data.len > limits.maximum_bytes) return false;
    var offset: usize = 0;
    var count: usize = 0;
    while (offset < data.len and count < limits.maximum_processes) : (count += 1) {
        if (offset % layout.pointer_bytes != 0 or data.len - offset < layout.header_bytes) return false;
        const record = data[offset..];
        const next = unsigned(u32, record, 0);
        if (pointer(record, layout.process_id, layout) > std.math.maxInt(i32)) return false;
        if (next == 0) return true;
        if (next < layout.header_bytes or next > record.len or next % layout.pointer_bytes != 0) return false;
        offset += next;
    }
    return false;
}

fn optionalName(data: []const u8, record: []const u8, layout: Layout, row: *Row) void {
    const bytes = unsigned(u16, record, 56);
    const maximum = unsigned(u16, record, 58);
    if (bytes == 0 or bytes % 2 != 0 or bytes > maximum) return;
    const address = pointer(record, layout.name_pointer, layout);
    const base: u64 = @intFromPtr(data.ptr);
    if (address < base or address - base > data.len) return;
    const offset: usize = @intCast(address - base);
    if (bytes > data.len - offset) return;
    var length: usize = bytes / 2;
    if (length >= 4) {
        const suffix = data[offset + bytes - 8 ..][0..8];
        if (unsigned(u16, suffix, 0) == '.' and
            (unsigned(u16, suffix, 2) | 0x20) == 'e' and
            (unsigned(u16, suffix, 4) | 0x20) == 'x' and
            (unsigned(u16, suffix, 6) | 0x20) == 'e') length -= 4;
    }
    row.name = @ptrCast(data.ptr + offset);
    row.name_length = length;
    row.flags |= Flags.name;
}

pub fn visit(data: []const u8, limits: Limits, layout: Layout, context: ?*anyopaque, visitor: Visitor, count_out: *usize) Status {
    count_out.* = 0;
    if (!shape(data, limits, layout)) return .invalid_table;
    var offset: usize = 0;
    var count: usize = 0;
    // shape has bounded and validated the whole offset chain before the first callback.
    while (count < limits.maximum_processes) {
        const record = data[offset..];
        const parent = pointer(record, layout.parent_process_id, layout);
        const creation: i64 = @bitCast(unsigned(u64, record, 32));
        const threads = unsigned(u32, record, 4);
        var row: Row = .{
            .process_id = @intCast(pointer(record, layout.process_id, layout)),
            .parent_process_id = if (parent <= std.math.maxInt(i32)) @intCast(parent) else 0,
            .session_id = unsigned(u32, record, layout.session_id),
            .flags = (if (creation > 0) Flags.creation else @as(u32, 0)) |
                (if (parent <= std.math.maxInt(i32)) Flags.parent else @as(u32, 0)) |
                (if (threads <= limits.maximum_threads_per_process) Flags.thread_count else @as(u32, 0)),
            .creation_filetime_100ns = if (creation > 0) creation else 0,
            .name = null,
            .name_length = 0,
            .kernel_time_100ns = unsigned(u64, record, 48),
            .user_time_100ns = unsigned(u64, record, 40),
            .working_set = pointer(record, layout.working_set, layout),
            .private_commit = pointer(record, layout.private_commit, layout),
            .peak_private_commit = pointer(record, layout.peak_private_commit, layout),
            .private_working_set = unsigned(u64, record, 8),
            .read_operations = unsigned(u64, record, layout.io),
            .write_operations = unsigned(u64, record, layout.io + 8),
            .other_operations = unsigned(u64, record, layout.io + 16),
            .read_bytes = unsigned(u64, record, layout.io + 24),
            .write_bytes = unsigned(u64, record, layout.io + 32),
            .other_bytes = unsigned(u64, record, layout.io + 40),
            .handle_count = unsigned(u32, record, layout.handle_count),
            .thread_count = threads,
        };
        optionalName(data, record, layout, &row);
        if (visitor(context, &row) != 0) return .visitor_rejected;
        count += 1;
        const next = unsigned(u32, record, 0);
        if (next == 0) {
            count_out.* = count;
            return .ok;
        }
        offset += next;
    }
    return .invalid_table;
}
