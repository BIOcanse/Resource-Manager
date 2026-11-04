//! A registration survives fixed-resource unload. The ledger state owner is
//! responsible for addressing, leases, table totals and mutation serialization.
const std = @import("std");
const builtin = @import("builtin");
const memory = @import("memory_region.zig");

pub const Registration = Configured(false);

pub fn Configured(comptime write_watch: bool) type {
    return WithRegion(write_watch, memory.Region);
}

/// Compile-time page descriptor injection, also used by deterministic OS-failure tests.
pub fn WithRegion(comptime write_watch: bool, comptime Region: type) type {
    return struct {
        const Self = @This();
        allocation_bytes: u64,
        region: ?Region = null,

        pub fn fixed(allocation_bytes: u64) memory.Error!Self {
            _ = try memory.requiredBytes(allocation_bytes);
            return .{ .allocation_bytes = allocation_bytes };
        }

        pub fn dynamic(allocation_bytes: u64) memory.Error!Self {
            return fixed(allocation_bytes);
        }

        pub fn enterMemory(self: *Self) memory.Error![]u8 {
            if (self.region) |region| return region.bytes();
            const region = try Region.allocateWriteWatch(self.allocation_bytes, write_watch);
            self.region = region;
            return region.bytes();
        }

        pub fn update(self: *Self, allocation_bytes: u64) memory.Error!?[]u8 {
            _ = try memory.requiredBytes(allocation_bytes);
            if (self.region) |*region| try region.resizeWriteWatch(allocation_bytes, write_watch);
            self.allocation_bytes = allocation_bytes;
            return if (self.region) |region| region.bytes() else null;
        }

        pub fn unload(self: *Self) memory.Error!void {
            if (self.region) |*region| try region.release();
            self.region = null;
        }

        pub fn allocatedPageBytes(self: Self) usize {
            return if (self.region) |region| region.page_bytes else 0;
        }
    };
}

test "fixed unload keeps registration and update changes the next actual allocation" {
    if (builtin.os.tag != .windows and builtin.os.tag != .linux) return error.SkipZigTest;
    const page_size = std.heap.pageSize();
    var registration = try Registration.fixed(17);
    defer registration.unload() catch @panic("registration cleanup failed");
    const first = try registration.enterMemory();
    @memset(first, 0xb4);
    try registration.unload();
    try std.testing.expectEqual(@as(u64, 17), registration.allocation_bytes);
    try std.testing.expect(registration.region == null);
    try std.testing.expect((try registration.update(page_size + 37)) == null);
    try std.testing.expectEqual(@as(usize, 0), registration.allocatedPageBytes());
    const reloaded = try registration.enterMemory();
    try std.testing.expectEqual(page_size + 37, reloaded.len);
    try std.testing.expectEqual(page_size * 2, registration.allocatedPageBytes());
    for (reloaded) |byte| try std.testing.expectEqual(@as(u8, 0), byte);
    const hit = try registration.enterMemory();
    try std.testing.expectEqual(reloaded.ptr, hit.ptr);
    @memset(reloaded, 0x61);
    const resized = (try registration.update(page_size * 3 + 9)).?;
    for (resized[0..reloaded.len]) |byte| try std.testing.expectEqual(@as(u8, 0x61), byte);
    for (resized[reloaded.len..]) |byte| try std.testing.expectEqual(@as(u8, 0), byte);
    const before = registration;
    try std.testing.expectError(error.SizeOverflow, registration.update(std.math.maxInt(u64)));
    try std.testing.expectEqualDeep(before, registration);
}

test "dynamic allocation requires one explicit value, including zero" {
    if (builtin.os.tag != .windows and builtin.os.tag != .linux) return error.SkipZigTest;
    var explicit = try Registration.dynamic(11);
    defer explicit.unload() catch @panic("registration cleanup failed");
    var empty = try Registration.dynamic(0);
    defer empty.unload() catch @panic("registration cleanup failed");
    try std.testing.expectEqual(@as(usize, 11), (try explicit.enterMemory()).len);
    try std.testing.expectEqual(@as(usize, 0), (try empty.enterMemory()).len);
    try std.testing.expect(empty.region != null);
    try std.testing.expectEqual(@as(usize, 0), empty.allocatedPageBytes());
    try empty.unload();
    try std.testing.expect(empty.region == null);
}
