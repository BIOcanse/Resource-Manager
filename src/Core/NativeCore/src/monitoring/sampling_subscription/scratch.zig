const std = @import("std");

// Five disposable columns only. Issued triggers are cross-call authorization,
// owned by Session and deliberately excluded from this layout.
pub const alignment = 8;

pub const View = struct {
    handles: []u64,
    intervals: []u64,
    source_counts: []u32,
    sampling_counts: []u32,
    trigger_due: []u64,

    pub fn bind(bytes: []u8, count: usize) ?View {
        const length = std.math.mul(usize, count, 32) catch return null;
        if (count == 0 or bytes.len < length or @intFromPtr(bytes.ptr) % alignment != 0)
            return null;
        _ = std.math.add(usize, @intFromPtr(bytes.ptr), length) catch return null;
        return .{
            .handles = column(u64, bytes, 0, count),
            .intervals = column(u64, bytes, count * 8, count),
            .source_counts = column(u32, bytes, count * 16, count),
            .sampling_counts = column(u32, bytes, count * 20, count),
            .trigger_due = column(u64, bytes, count * 24, count),
        };
    }
};

fn column(comptime T: type, bytes: []u8, offset: usize, count: usize) []T {
    const pointer: [*]T = @ptrCast(@alignCast(bytes.ptr + offset));
    return pointer[0..count];
}
