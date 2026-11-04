//! Process-local payload pages. Metadata and leases are owned by ledger state.
//! This module never caches pages or reserves an independent growth range.
const std = @import("std");
const builtin = @import("builtin");
const windows = @import("memory_region/windows.zig");
const linux = @import("memory_region/linux.zig");

pub const Error = error{ SizeOverflow, AllocationFailed, ReleaseFailed, UnsupportedPlatform };
pub const Region = RegionFor(SystemPages);

pub fn requiredBytes(allocation_bytes: u64) Error!usize {
    const length = std.math.cast(usize, allocation_bytes) orelse return error.SizeOverflow;
    const page_size = std.heap.pageSize();
    const rounded = std.math.add(usize, length, page_size - 1) catch return error.SizeOverflow;
    return rounded & ~(page_size - 1);
}

const SystemPages = struct {
    fn allocate(bytes: usize) Error![*]u8 {
        return switch (builtin.os.tag) {
            .windows => windows.allocate(bytes),
            .linux => linux.allocate(bytes),
            else => error.UnsupportedPlatform,
        };
    }

    fn allocateWriteWatch(bytes: usize, comptime enabled: bool) Error![*]u8 {
        return switch (builtin.os.tag) {
            .windows => windows.allocateWriteWatch(bytes, enabled),
            .linux => linux.allocate(bytes),
            else => error.UnsupportedPlatform,
        };
    }

    fn release(base: [*]u8, page_bytes: usize) Error!void {
        switch (builtin.os.tag) {
            .windows => try windows.release(base),
            .linux => try linux.release(base, page_bytes),
            else => return error.UnsupportedPlatform,
        }
    }
};

pub fn RegionFor(comptime Pages: type) type {
    return struct {
        const Self = @This();

        base: ?[*]u8 = null,
        allocation_bytes: usize = 0,
        page_bytes: usize = 0,

        pub fn allocate(allocation_bytes: u64) Error!Self {
            return allocateWriteWatch(allocation_bytes, false);
        }

        /// A write-watch request always reaches `Pages.allocateWriteWatch`; a page source
        /// without it is a compile error, never a silent plain allocation. (Zig 0.17's
        /// `@hasDecl` no longer sees non-pub declarations, so a reflection check here
        /// quietly dropped MEM_WRITE_WATCH for SystemPages.)
        pub fn allocateWriteWatch(allocation_bytes: u64, comptime enabled: bool) Error!Self {
            const page_bytes = try requiredBytes(allocation_bytes);
            return .{
                .base = if (page_bytes == 0) null else if (comptime enabled) try Pages.allocateWriteWatch(page_bytes, true) else try Pages.allocate(page_bytes),
                .allocation_bytes = @intCast(allocation_bytes),
                .page_bytes = page_bytes,
            };
        }

        pub fn bytes(self: Self) []u8 {
            return if (self.base) |base| base[0..self.allocation_bytes] else @constCast(&.{});
        }

        /// Caller owns exclusive access while replacing a region. Previously
        /// returned views must be refreshed after success; failure preserves them.
        pub fn resize(self: *Self, allocation_bytes: u64) Error!void {
            return self.resizeWriteWatch(allocation_bytes, false);
        }

        pub fn resizeWriteWatch(self: *Self, allocation_bytes: u64, comptime enabled: bool) Error!void {
            const page_bytes = try requiredBytes(allocation_bytes);
            const new_length: usize = @intCast(allocation_bytes);
            if (page_bytes == self.page_bytes) {
                // Same actual page range. Erase truncated bytes now so a later
                // grow cannot reveal old data, even without an OS replacement.
                if (new_length < self.allocation_bytes) {
                    @memset(self.bytes()[new_length..], 0);
                }
                self.allocation_bytes = new_length;
                return;
            }

            var replacement = try Self.allocateWriteWatch(allocation_bytes, enabled);
            const preserved = @min(self.allocation_bytes, replacement.allocation_bytes);
            @memcpy(replacement.bytes()[0..preserved], self.bytes()[0..preserved]);
            self.release() catch |err| {
                // The old region remains valid if the OS refuses its release.
                // A second OS failure is a native invariant failure, not a
                // successful update or a business "unknown" resource state.
                replacement.release() catch @panic("ledger replacement rollback could not return OS pages");
                return err;
            };
            self.* = replacement;
        }

        /// Clears the descriptor only after the OS has returned the allocation.
        pub fn release(self: *Self) Error!void {
            if (self.base) |base| try Pages.release(base, self.page_bytes);
            self.* = .{};
        }
    };
}

test "OS pages are fully committed, initially zero and returned without a pool" {
    if (builtin.os.tag != .windows) return error.SkipZigTest;
    const page_size = std.heap.pageSize();
    var region = try Region.allocate(page_size + 7);
    defer region.release() catch @panic("OS region cleanup failed");
    const base = region.base.?;
    try std.testing.expectEqual(page_size * 2, region.page_bytes);
    try std.testing.expectEqual(page_size + 7, region.bytes().len);
    for (region.bytes()) |byte| try std.testing.expectEqual(@as(u8, 0), byte);
    const mapped = try windows.query(base);
    try std.testing.expectEqual(windows.mem_commit, mapped.State);
    try std.testing.expectEqual(region.page_bytes, mapped.RegionSize);
    try std.testing.expectEqual(@intFromPtr(base), @intFromPtr(mapped.AllocationBase.?));
    const trailing = try windows.query(base + region.page_bytes);
    // AllocationBase is unspecified for MEM_FREE; inspect it only otherwise.
    try std.testing.expect(trailing.State == windows.mem_free or trailing.AllocationBase != mapped.AllocationBase);
    // Partial loading does not shrink the allocation.
    region.bytes()[0] = 123;
    try std.testing.expectEqual(page_size * 2, region.page_bytes);
    try region.release();
    const returned = try windows.query(base);
    try std.testing.expectEqual(windows.mem_free, returned.State);
    try std.testing.expectEqual(@as(usize, 0), region.page_bytes);
    try region.release(); // Empty descriptor does not double-free.
}

test "write-watch regions are created with MEM_WRITE_WATCH and plain regions without it" {
    if (builtin.os.tag != .windows) return error.SkipZigTest;
    const Kernel = struct {
        extern "kernel32" fn ResetWriteWatch(base: *anyopaque, size: usize) callconv(.winapi) u32;
    };
    var watched = try Region.allocateWriteWatch(std.heap.pageSize(), true);
    defer watched.release() catch @panic("OS region cleanup failed");
    try std.testing.expectEqual(@as(u32, 0), Kernel.ResetWriteWatch(@ptrCast(watched.base.?), watched.page_bytes));
    var plain = try Region.allocate(std.heap.pageSize());
    defer plain.release() catch @panic("OS region cleanup failed");
    try std.testing.expect(Kernel.ResetWriteWatch(@ptrCast(plain.base.?), plain.page_bytes) != 0);
}

test "resize preserves a prefix, zeroes growth, returns old pages and removes truncation" {
    if (builtin.os.tag != .windows and builtin.os.tag != .linux) return error.SkipZigTest;
    const page_size = std.heap.pageSize();
    var region = try Region.allocate(page_size + 19);
    defer region.release() catch @panic("OS region cleanup failed");
    @memset(region.bytes(), 0x5a);
    const old_base = region.base.?;
    try region.resize(page_size * 3 + 1);
    try std.testing.expect(region.base.? != old_base);
    if (builtin.os.tag == .windows) {
        try std.testing.expectEqual(windows.mem_free, (try windows.query(old_base)).State);
    } else {
        var residency: [1]u8 = undefined;
        try std.testing.expectError(error.OutOfMemory, std.posix.mincore(@alignCast(old_base), page_size, &residency));
    }
    for (region.bytes()[0 .. page_size + 19]) |byte| try std.testing.expectEqual(@as(u8, 0x5a), byte);
    for (region.bytes()[page_size + 19 ..]) |byte| try std.testing.expectEqual(@as(u8, 0), byte);
    try region.resize(9);
    try std.testing.expectEqual(page_size, region.page_bytes);
    const same_page_base = region.base.?;
    try region.resize(29);
    try std.testing.expectEqual(same_page_base, region.base.?);
    for (region.bytes()[0..9]) |byte| try std.testing.expectEqual(@as(u8, 0x5a), byte);
    for (region.bytes()[9..]) |byte| try std.testing.expectEqual(@as(u8, 0), byte);
    @memset(region.bytes(), 0xa3);
    try region.resize(3);
    try region.resize(29);
    for (region.bytes()[0..3]) |byte| try std.testing.expectEqual(@as(u8, 0xa3), byte);
    for (region.bytes()[3..]) |byte| try std.testing.expectEqual(@as(u8, 0), byte);
    try region.resize(0);
    try std.testing.expect(region.base == null);
    try std.testing.expectEqual(@as(usize, 0), region.bytes().len);
}

const ControlledPages = struct {
    var fail_allocation = false;
    var fail_release_once = false;
    var live_pages: usize = 0;
    var peak_pages: usize = 0;

    fn allocate(bytes: usize) Error![*]u8 {
        if (fail_allocation) return error.AllocationFailed;
        const base = try SystemPages.allocate(bytes);
        live_pages += bytes;
        peak_pages = @max(peak_pages, live_pages);
        return base;
    }

    fn release(base: [*]u8, page_bytes: usize) Error!void {
        if (fail_release_once) {
            fail_release_once = false;
            return error.ReleaseFailed;
        }
        try SystemPages.release(base, page_bytes);
        live_pages -= page_bytes;
    }
};

test "allocation and known release failures preserve the old region without hidden payload debt" {
    if (builtin.os.tag != .windows and builtin.os.tag != .linux) return error.SkipZigTest;
    ControlledPages.fail_allocation = false;
    ControlledPages.fail_release_once = false;
    ControlledPages.live_pages = 0;
    ControlledPages.peak_pages = 0;
    const ControlledRegion = RegionFor(ControlledPages);
    const page_size = std.heap.pageSize();
    var region = try ControlledRegion.allocate(31);
    defer region.release() catch @panic("controlled OS cleanup failed");
    @memset(region.bytes(), 0x83);
    const before = region;
    ControlledPages.fail_allocation = true;
    try std.testing.expectError(error.AllocationFailed, region.resize(page_size + 3));
    ControlledPages.fail_allocation = false;
    try std.testing.expectEqualDeep(before, region);
    ControlledPages.fail_release_once = true;
    try std.testing.expectError(error.ReleaseFailed, region.resize(page_size + 3));
    try std.testing.expectEqualDeep(before, region);
    try std.testing.expectEqual(page_size, ControlledPages.live_pages);
    try std.testing.expectEqual(page_size * 3, ControlledPages.peak_pages);
    for (region.bytes()) |byte| try std.testing.expectEqual(@as(u8, 0x83), byte);
    try std.testing.expectError(error.SizeOverflow, region.resize(std.math.maxInt(u64)));
    try std.testing.expectEqualDeep(before, region);
    try region.release();
    try std.testing.expectEqual(@as(usize, 0), ControlledPages.live_pages);
}

test "zero length is an empty region and page rounding overflow is rejected" {
    var empty = try Region.allocate(0);
    try std.testing.expect(empty.base == null);
    try std.testing.expectEqual(@as(usize, 0), empty.page_bytes);
    try empty.release();
    try std.testing.expectError(error.SizeOverflow, requiredBytes(std.math.maxInt(u64)));
}
