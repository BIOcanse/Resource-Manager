const std = @import("std");

/// Owner-local requested byte budget. The backing allocator is injected so
/// tests retain leak/double-free detection. Failed growth changes no accounting.
pub const Budget = struct {
    backing: std.mem.Allocator,
    transient_backing: ?std.mem.Allocator = null,
    cap: usize,
    current: usize = 0,
    peak: usize = 0,
    failures: u64 = 0,

    pub fn allocator(self: *Budget) std.mem.Allocator {
        return .{ .ptr = self, .vtable = &.{ .alloc = alloc, .resize = resize, .remap = remap, .free = free } };
    }
    /// Alternate placement shares this owner's complete accounting and cap.
    pub fn transientAllocator(self: *Budget) std.mem.Allocator {
        if (self.transient_backing == null) return self.allocator();
        return .{ .ptr = self, .vtable = &.{ .alloc = allocTransient, .resize = resizeTransient, .remap = remapTransient, .free = freeTransient } };
    }
    fn selectBacking(self: *Budget, comptime transient: bool) std.mem.Allocator {
        return if (transient) self.transient_backing orelse self.backing else self.backing;
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
        return allocWith(ctx, len, alignment, ret, false);
    }
    fn allocTransient(ctx: *anyopaque, len: usize, alignment: std.mem.Alignment, ret: usize) ?[*]u8 {
        return allocWith(ctx, len, alignment, ret, true);
    }
    fn allocWith(ctx: *anyopaque, len: usize, alignment: std.mem.Alignment, ret: usize, comptime transient: bool) ?[*]u8 {
        const self: *Budget = @ptrCast(@alignCast(ctx));
        if (!self.admit(0, len)) return null;
        const p = self.selectBacking(transient).rawAlloc(len, alignment, ret) orelse {
            self.failures +|= 1;
            return null;
        };
        self.account(0, len);
        return p;
    }
    fn resize(ctx: *anyopaque, memory: []u8, alignment: std.mem.Alignment, len: usize, ret: usize) bool {
        return resizeWith(ctx, memory, alignment, len, ret, false);
    }
    fn resizeTransient(ctx: *anyopaque, memory: []u8, alignment: std.mem.Alignment, len: usize, ret: usize) bool {
        return resizeWith(ctx, memory, alignment, len, ret, true);
    }
    fn resizeWith(ctx: *anyopaque, memory: []u8, alignment: std.mem.Alignment, len: usize, ret: usize, comptime transient: bool) bool {
        const self: *Budget = @ptrCast(@alignCast(ctx));
        if (!self.admit(memory.len, len)) return false;
        if (!self.selectBacking(transient).rawResize(memory, alignment, len, ret)) return false;
        self.account(memory.len, len);
        return true;
    }
    fn remap(ctx: *anyopaque, memory: []u8, alignment: std.mem.Alignment, len: usize, ret: usize) ?[*]u8 {
        return remapWith(ctx, memory, alignment, len, ret, false);
    }
    fn remapTransient(ctx: *anyopaque, memory: []u8, alignment: std.mem.Alignment, len: usize, ret: usize) ?[*]u8 {
        return remapWith(ctx, memory, alignment, len, ret, true);
    }
    fn remapWith(ctx: *anyopaque, memory: []u8, alignment: std.mem.Alignment, len: usize, ret: usize, comptime transient: bool) ?[*]u8 {
        const self: *Budget = @ptrCast(@alignCast(ctx));
        if (!self.admit(memory.len, len)) return null;
        const p = self.selectBacking(transient).rawRemap(memory, alignment, len, ret) orelse return null;
        self.account(memory.len, len);
        return p;
    }
    fn free(ctx: *anyopaque, memory: []u8, alignment: std.mem.Alignment, ret: usize) void {
        freeWith(ctx, memory, alignment, ret, false);
    }
    fn freeTransient(ctx: *anyopaque, memory: []u8, alignment: std.mem.Alignment, ret: usize) void {
        freeWith(ctx, memory, alignment, ret, true);
    }
    fn freeWith(ctx: *anyopaque, memory: []u8, alignment: std.mem.Alignment, ret: usize, comptime transient: bool) void {
        const self: *Budget = @ptrCast(@alignCast(ctx));
        self.selectBacking(transient).rawFree(memory, alignment, ret);
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

test "transient arena growth avoids working pins and preserves nested owner budgets" {
    const Region = @import("region_allocator.zig").RegionAllocator;
    const backing = try std.testing.allocator.alloc(u8, 8 * 1024 * 1024);
    defer std.testing.allocator.free(backing);
    var region = try Region.init(backing);
    defer region.deinit();
    var shared: Budget = .{ .backing = region.allocator(), .transient_backing = region.transientAllocator(), .cap = backing.len };
    var cpu: Budget = .{ .backing = shared.allocator(), .transient_backing = shared.transientAllocator(), .cap = 65536 };
    var irq: Budget = .{ .backing = shared.allocator(), .transient_backing = shared.transientAllocator(), .cap = backing.len };
    const working = cpu.allocator();
    var arena = std.heap.ArenaAllocator.init(irq.transientAllocator());
    defer arena.deinit();
    var pins: [3][]u8 = undefined;
    var count: usize = 0;
    defer for (pins[0..count]) |pin| working.free(pin);
    for (&pins, 0..) |*pin, iteration| {
        const temporary = try arena.allocator().alloc(u8, (iteration + 1) * 1024 * 1024);
        temporary[0] = 11;
        temporary[temporary.len - 1] = 22;
        pin.* = try working.alloc(u8, 64);
        @memset(pin.*, @intCast(iteration));
        count += 1;
        try std.testing.expectEqual(irq.current + cpu.current, shared.current);
        try std.testing.expectEqual(@as(u8, 11), temporary[0]);
        try std.testing.expectEqual(@as(u8, 22), temporary[temporary.len - 1]);
        try std.testing.expect(arena.reset(.free_all));
        try std.testing.expectEqual(@as(usize, 0), irq.current);
        try std.testing.expectEqual(cpu.current, shared.current);
    }
    for (pins, 0..) |pin, iteration| for (pin) |byte| try std.testing.expectEqual(@as(u8, @intCast(iteration)), byte);
    var limited: Budget = .{ .backing = shared.allocator(), .transient_backing = shared.transientAllocator(), .cap = 16 };
    try std.testing.expectError(error.OutOfMemory, limited.transientAllocator().alloc(u8, 32));
    try std.testing.expectEqual(@as(u64, 1), limited.failures);
    try std.testing.expectEqual(@as(u64, 0), shared.failures);
    try std.testing.expectEqual(@as(u64, 0), region.failures);
    try std.testing.expect(irq.peak > 3 * 1024 * 1024);
    for (pins) |pin| working.free(pin);
    count = 0;
    try std.testing.expectEqual(@as(usize, 0), cpu.current);
    try std.testing.expectEqual(@as(usize, 0), shared.current);
    try std.testing.expectEqual(@as(usize, 0), region.current);
    try std.testing.expectEqual(@as(usize, 0), region.live_allocations);
}
