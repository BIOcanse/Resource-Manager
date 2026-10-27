const std = @import("std");

/// Owner-local requested byte budget. The backing allocator is injected so
/// tests retain leak/double-free detection. Failed growth changes no accounting.
pub const Budget = struct {
    backing: std.mem.Allocator,
    cap: usize,
    current: usize = 0,
    peak: usize = 0,
    failures: u64 = 0,

    pub fn allocator(self: *Budget) std.mem.Allocator {
        return .{ .ptr = self, .vtable = &.{ .alloc = alloc, .resize = resize, .remap = remap, .free = free } };
    }
    fn admit(self: *Budget, old: usize, new: usize) bool {
        if (new > old and new - old > self.cap - self.current) {
            self.failures +|= 1;
            return false;
        }
        return true;
    }
    fn account(self: *Budget, old: usize, new: usize) void {
        self.current = self.current - old + new;
        self.peak = @max(self.peak, self.current);
    }
    fn alloc(ctx: *anyopaque, len: usize, alignment: std.mem.Alignment, ret: usize) ?[*]u8 {
        const self: *Budget = @ptrCast(@alignCast(ctx));
        if (!self.admit(0, len)) return null;
        const p = self.backing.rawAlloc(len, alignment, ret) orelse {
            self.failures +|= 1;
            return null;
        };
        self.account(0, len);
        return p;
    }
    fn resize(ctx: *anyopaque, memory: []u8, alignment: std.mem.Alignment, len: usize, ret: usize) bool {
        const self: *Budget = @ptrCast(@alignCast(ctx));
        if (!self.admit(memory.len, len)) return false;
        if (!self.backing.rawResize(memory, alignment, len, ret)) return false;
        self.account(memory.len, len);
        return true;
    }
    fn remap(ctx: *anyopaque, memory: []u8, alignment: std.mem.Alignment, len: usize, ret: usize) ?[*]u8 {
        const self: *Budget = @ptrCast(@alignCast(ctx));
        if (!self.admit(memory.len, len)) return null;
        const p = self.backing.rawRemap(memory, alignment, len, ret) orelse return null;
        self.account(memory.len, len);
        return p;
    }
    fn free(ctx: *anyopaque, memory: []u8, alignment: std.mem.Alignment, ret: usize) void {
        const self: *Budget = @ptrCast(@alignCast(ctx));
        self.backing.rawFree(memory, alignment, ret);
        self.account(memory.len, 0);
    }
};

test "owner budget rejects growth and returns all bytes" {
    var budget: Budget = .{ .backing = std.testing.allocator, .cap = 64 };
    const a = budget.allocator();
    const p = try a.alloc(u8, 32);
    try std.testing.expectError(error.OutOfMemory, a.alloc(u8, 33));
    try std.testing.expectEqual(@as(usize, 32), budget.current);
    try std.testing.expectEqual(@as(usize, 32), budget.peak);
    a.free(p);
    try std.testing.expectEqual(@as(usize, 0), budget.current);
}
