const std = @import("std");
const runtime = @import("lookup.zig");
const core = @import("../core/table.zig");

const Fake = struct {
    enters: u32 = 0,
    leaves: u32 = 0,
    borrows: u32 = 0,
    returns: u32 = 0,
    queries: u32 = 0,
    admit: bool = true,
    region_allowed: bool = true,
    fill_status: u32 = 0,
    returned: u32 = 28,
    bytes: [28]u8 = @splat(0),
    fn get(ctx: ?*anyopaque) *Fake {
        return @ptrCast(@alignCast(ctx.?));
    }
    fn enter(ctx: ?*anyopaque) callconv(.c) u32 {
        const self = get(ctx);
        self.enters += 1;
        return @intFromBool(self.admit);
    }
    fn leave(ctx: ?*anyopaque) callconv(.c) void {
        get(ctx).leaves += 1;
    }
    fn region(ctx: ?*anyopaque, _: u32, work: runtime.Work, operation: ?*anyopaque) callconv(.c) u32 {
        const self = get(ctx);
        if (!self.region_allowed) return 0;
        self.borrows += 1;
        defer self.returns += 1;
        return work(operation, &self.bytes, self.bytes.len);
    }
    fn query(ctx: ?*anyopaque, memory: ?[*]u8, length: *u32, _: u16) u32 {
        const self = get(ctx);
        self.queries += 1;
        length.* = if (memory == null) self.bytes.len else self.returned;
        return if (memory == null) 122 else self.fill_status;
    }
    fn find(self: *Fake, maximum: u32) runtime.Result {
        const endpoint = core.Endpoint{ .address = @splat(0), .scope = 0, .port = 1, .family = core.ipv4 };
        return runtime.find(.{ .context = self, .enter = enter, .leave = leave, .with_region = region }, .{ .context = self, .call = query }, .{ .client = endpoint, .server = endpoint }, maximum);
    }
};

test "empty complete table returns no identity and returns both capabilities" {
    var fake: Fake = .{};
    const result = fake.find(28);
    try std.testing.expectEqual(runtime.Status.ok, result.status);
    try std.testing.expectEqual(@as(u32, 0), result.pid);
    try std.testing.expectEqual(@as(u32, 2), fake.queries);
    try std.testing.expectEqual(@as(u32, 1), fake.leaves);
    try std.testing.expectEqual(fake.borrows, fake.returns);
    try std.testing.expectEqual(@as(u64, 28), result.workspace_bytes);
}

test "capacity and retired admission do not borrow or execute a fill" {
    var fake: Fake = .{};
    try std.testing.expectEqual(runtime.Status.capacity, fake.find(27).status);
    try std.testing.expectEqual(@as(u32, 1), fake.queries);
    try std.testing.expectEqual(@as(u32, 0), fake.borrows);
    try std.testing.expectEqual(@as(u32, 1), fake.leaves);
    fake = .{ .admit = false };
    try std.testing.expectEqual(runtime.Status.retired, fake.find(28).status);
    try std.testing.expectEqual(@as(u32, 0), fake.queries);
    try std.testing.expectEqual(@as(u32, 0), fake.leaves);
}

test "region refusal query growth and malformed fill always settle once" {
    var fake: Fake = .{ .region_allowed = false };
    try std.testing.expectEqual(runtime.Status.unavailable, fake.find(28).status);
    try std.testing.expectEqual(@as(u32, 1), fake.leaves);
    fake = .{ .fill_status = 122 };
    try std.testing.expectEqual(runtime.Status.unavailable, fake.find(28).status);
    try std.testing.expectEqual(@as(u32, 1), fake.returns);
    try std.testing.expectEqual(@as(u32, 1), fake.leaves);
    fake = .{ .returned = 29 };
    try std.testing.expectEqual(runtime.Status.invalid_table, fake.find(28).status);
    try std.testing.expectEqual(@as(u32, 1), fake.returns);
    fake = .{ .returned = 3 };
    try std.testing.expectEqual(runtime.Status.invalid_table, fake.find(28).status);
    try std.testing.expectEqual(@as(u32, 1), fake.returns);
}
