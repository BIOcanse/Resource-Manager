//! Optional, non-invasive write observation. Never protects pages or handles faults.
const builtin = @import("builtin");
const std = @import("std");

pub const supported: u32 = 1;
pub const reads_unobservable: u32 = 2;
pub const shared_attribution: u32 = 1;
pub const exceeds_scratch: u32 = 2;
pub const query_failed: u32 = 4;
pub const platform_unavailable: u32 = 8;
pub const Sample = struct { pages: u64 = 0, flags: u32 = 0 };

const Windows = struct {
    extern "kernel32" fn GetWriteWatch(flags: u32, base: *anyopaque, size: usize, addresses: [*]usize, count: *usize, granularity: *u32) callconv(.winapi) u32;
    extern "kernel32" fn ResetWriteWatch(base: *anyopaque, size: usize) callconv(.winapi) u32;
};

pub fn supportFlags() u32 {
    return reads_unobservable | (if (builtin.os.tag == .windows) supported else 0);
}

pub fn collect(base: [*]u8, page_bytes: usize, scratch: []usize) Sample {
    if (page_bytes == 0) return .{};
    if (builtin.os.tag != .windows) return .{ .flags = platform_unavailable };
    const page_count = std.math.divCeil(usize, page_bytes, std.heap.pageSize()) catch unreachable;
    if (page_count > scratch.len)
        return .{ .flags = exceeds_scratch };
    var count: usize = scratch.len;
    var granularity: u32 = 0;
    // Query and reset together: no query/reset race that loses concurrent writes.
    if (Windows.GetWriteWatch(1, @ptrCast(base), page_bytes, scratch.ptr, &count, &granularity) != 0)
        return .{ .flags = query_failed };
    return .{ .pages = @intCast(count) };
}

/// Called only for a new allocation or after the SDK copies its replacement.
pub fn reset(base: [*]u8, page_bytes: usize) u32 {
    if (page_bytes == 0) return 0;
    if (builtin.os.tag != .windows) return platform_unavailable;
    return if (Windows.ResetWriteWatch(@ptrCast(base), page_bytes) == 0) 0 else query_failed;
}

/// Windows demand-zero materialization can itself appear in WriteWatch even
/// after a pure first read (confirmed by the frozen cold/warm diagnostic).
/// Before exposing a new region, or after an exclusive SDK copy, materialize
/// each observable page while preserving bytes, then discard those SDK writes.
pub fn establishBaseline(base: [*]u8, page_bytes: usize, maximum_pages: usize) u32 {
    if (page_bytes == 0) return 0;
    if (builtin.os.tag != .windows) return platform_unavailable;
    const size = std.heap.pageSize();
    const count = std.math.divCeil(usize, page_bytes, size) catch unreachable;
    if (count > maximum_pages) return exceeds_scratch;
    const pointer: [*]volatile u8 = base;
    var offset: usize = 0;
    while (offset < page_bytes) : (offset += size) {
        const value = pointer[offset];
        pointer[offset] = value;
    }
    return reset(base, page_bytes);
}
