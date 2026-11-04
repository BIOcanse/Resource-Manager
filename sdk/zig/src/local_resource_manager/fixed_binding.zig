//! Declaration-time fixed range, independent of UID and payload addresses.
//! The state owner validates table/partition lifetime before resolving a key.
const std = @import("std");
const Resource = @import("memory_resource.zig").Resource;

pub const Binding = struct {
    first_slot: u32,
    capacity: u32,

    pub fn resourceAt(self: Binding, resources: []Resource, ordinal: u32) error{InvalidArgument}!*Resource {
        if (ordinal >= self.capacity) return error.InvalidArgument;
        const range_end = @as(u64, self.first_slot) + self.capacity;
        if (range_end > resources.len) return error.InvalidArgument;
        return &resources[@as(usize, self.first_slot) + ordinal];
    }
};

test "fixed declaration directly resolves first middle and last rows at both resource capacities" {
    const builtin = @import("builtin");
    if (builtin.os.tag != .windows) return error.SkipZigTest;
    const lifecycle = @import("memory_resource.zig");
    const capacities = [_]u32{ 1024, 1048576 };
    for (capacities) |capacity| {
        // Global capacity is metadata, not a million payload allocations.
        const resources = try std.heap.page_allocator.alloc(Resource, capacity);
        defer std.heap.page_allocator.free(resources);
        @memset(std.mem.sliceAsBytes(resources), 0);
        const binding: Binding = .{ .first_slot = 8, .capacity = capacity - 16 };
        const ordinals = [_]u32{ 0, binding.capacity / 2, binding.capacity - 1 };
        for (ordinals) |ordinal| {
            const resource = try binding.resourceAt(resources, ordinal);
            try std.testing.expectEqual(&resources[binding.first_slot + ordinal], resource);
            resource.* = try Resource.fixed(37);
            const first = (try resource.push(1, 1)).fresh;
            @memset(first.region, 0xc1);
            _ = try resource.endUse(first.lease, .complete, 1);
            const same_row = try binding.resourceAt(resources, ordinal);
            const borrowed = (try same_row.use(1)).in_memory;
            try std.testing.expectEqual(resource, same_row);
            try std.testing.expectEqual(@as(u8, 0xc1), borrowed.region[36]);
            const resized = (try resource.update(std.heap.pageSize() + 7, borrowed.lease)).resident;
            try std.testing.expectEqual(@as(u8, 0xc1), resized[36]);
            try std.testing.expectEqual(@as(u8, 0), resized[37]);
            _ = try resource.endUse(borrowed.lease, .complete, 1);
            try std.testing.expectEqual(lifecycle.CloseResult.closed, try resource.unload());
            try std.testing.expectEqual(lifecycle.UpdateResult.registration_only, try resource.update(51, null));
            const reloaded = (try (try binding.resourceAt(resources, ordinal)).push(1, 2)).fresh;
            try std.testing.expectEqual(@as(usize, 51), reloaded.region.len);
            try std.testing.expectEqual(@as(u8, 0), reloaded.region[36]);
            _ = try resource.endUse(reloaded.lease, .complete, 1);
            try std.testing.expectEqual(lifecycle.CloseResult.closed, try resource.close());
            try std.testing.expect(resource.registration != null);
            try std.testing.expect(resource.registration.?.region == null);
        }
        try std.testing.expectError(error.InvalidArgument, binding.resourceAt(resources, binding.capacity));
        const outside: Binding = .{ .first_slot = capacity - 3, .capacity = 4 };
        try std.testing.expectError(error.InvalidArgument, outside.resourceAt(resources, 0));
    }
}
