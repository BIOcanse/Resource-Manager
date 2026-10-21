//! Linux payload pages: exact anonymous mapping, no pool or growth reserve.
const std = @import("std");
const builtin = @import("builtin");

pub fn allocate(page_bytes: usize) error{AllocationFailed}![*]u8 {
    std.debug.assert(page_bytes > 0 and page_bytes % std.heap.pageSize() == 0);
    const region = std.posix.mmap(
        null,
        page_bytes,
        .{ .READ = true, .WRITE = true },
        .{ .TYPE = .PRIVATE, .ANONYMOUS = true },
        -1,
        0,
    ) catch return error.AllocationFailed;
    // Materialize the writable pages before returning, rather than returning
    // only an untouched virtual range. Linux OOM policy still belongs to Linux.
    @memset(region, 0);
    return region.ptr;
}

pub fn release(base: [*]u8, page_bytes: usize) error{ReleaseFailed}!void {
    std.debug.assert(page_bytes > 0 and page_bytes % std.heap.pageSize() == 0);
    if (std.os.linux.errno(std.os.linux.munmap(base, page_bytes)) != .SUCCESS)
        return error.ReleaseFailed;
}

test "Linux payload pages are resident, zeroed and actually unmapped on release" {
    if (builtin.os.tag != .linux) return error.SkipZigTest;
    const page_size = std.heap.pageSize();
    const base = try allocate(page_size * 2);
    var live = true;
    defer if (live) release(base, page_size * 2) catch @panic("Linux page cleanup failed");
    for (base[0 .. page_size * 2]) |value| try std.testing.expectEqual(@as(u8, 0), value);
    var residency: [2]u8 = undefined;
    try std.posix.mincore(@alignCast(base), page_size * 2, &residency);
    for (residency) |value| try std.testing.expect(value & 1 == 1);
    try std.testing.expectError(error.ReleaseFailed, release(base + 1, page_size * 2));
    for (base[0 .. page_size * 2]) |value| try std.testing.expectEqual(@as(u8, 0), value);
    @memset(base[0 .. page_size * 2], 0x5a);
    try release(base, page_size * 2);
    live = false;
    try std.testing.expectError(error.OutOfMemory, std.posix.mincore(@alignCast(base), page_size * 2, &residency));
}
