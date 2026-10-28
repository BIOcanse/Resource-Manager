const std = @import("std");
const control = @import("rm_control");
const support = @import("support.zig");
const d = control.declarations;
const types = control.types;
const expect = std.testing.expect;
const eq = std.testing.expectEqual;

fn current(input: *types.Input) anyerror!bool {
    const first = try input.read(.{ .item = 14 });
    const second = try input.read(.{ .item = 14 });
    if (!control.state.eql(first, second)) return error.ReadChangedWithinRound;
    return first.valueOr(false);
}
const registry = d.Registry{
    .version = "read-function-fixture",
    .process = "test",
    .blocks = &.{ .{ .uid = 1, .name = null }, .{ .uid = 2, .name = null }, .{ .uid = 3, .name = null } },
    .items = &.{ support.items[1], support.items[4] },
    .conditions = &.{
        .{ .uid = 20, .node = 1, .per_block = false, .items = &.{.{ .item = 14, .block = null }}, .conditions = &.{}, .children = &.{}, .decides = 2, .name = null },
        .{ .uid = 21, .node = 1, .per_block = false, .items = &.{.{ .item = 14, .block = null }}, .conditions = &.{}, .children = &.{}, .decides = 3, .name = null },
    },
};
const tree = control.tree.Tree{
    .program = 1,
    .blocks = &.{ .{ .uid = 1, .parent = null }, .{ .uid = 2, .parent = 1 }, .{ .uid = 3, .parent = 1 } },
    .uses = &.{},
    .computes = &.{ .{ .condition = 20, .function = current }, .{ .condition = 21, .function = current } },
};
const C = control.Controller(registry, tree);
const Owner = struct {
    controller: ?*C = null,
    calls: std.atomic.Value(u32) = .init(0),
    now: std.atomic.Value(u32) = .init(0),
    expires: std.atomic.Value(u32) = .init(10),
    kind: std.atomic.Value(u32) = .init(3),
    fn read(context: ?*anyopaque, output: *types.ReadView) callconv(.c) void {
        const self: *@This() = @ptrCast(@alignCast(context.?));
        // The callback can reenter a locking controller read: invocation must
        // not hold the controller mutex. It never samples or takes owner locks.
        _ = self.controller.?.readState(2) catch @panic("reentrant read failed");
        _ = self.calls.fetchAdd(1, .monotonic);
        output.* = .{ .kind = self.kind.load(.acquire), .value = @intFromBool(self.now.load(.acquire) < self.expires.load(.acquire)) };
    }
};
fn config() types.Config {
    var result = support.config(16);
    result.host_blocks = &.{ 2, 3 };
    return result;
}

test "reference read functions have one owner and each round captures one current read" {
    var owner: Owner = .{};
    var host: support.TestHost(C) = .{};
    const controller = try C.create(std.testing.allocator, config(), host.callbacks());
    host.controller = controller;
    owner.controller = controller;
    const claim = try controller.claimReadFunction(.{ .item = 14 }, .{ .context = &owner, .read = Owner.read });
    try std.testing.expectError(error.AlreadyClaimed, controller.claimCell(.{ .item = 14 }));
    try std.testing.expectError(error.AlreadyClaimed, controller.claimReadFunction(.{ .item = 14 }, .{ .context = &owner, .read = Owner.read }));
    try std.testing.expectError(error.StorageMismatch, controller.publish(claim, .{ .value = false }, support.reason, null));
    controller.activate();
    defer {
        host.cleanup();
        controller.destroy() catch @panic("read-function cleanup failed");
    }
    controller.waitIdle();
    try eq(types.BlockState.running, try controller.readState(2));
    try eq(types.BlockState.running, try controller.readState(3));
    const calls = owner.calls.load(.acquire);
    try controller.notifyChanged(claim, support.reason, 42);
    controller.waitIdle();
    // Two conditions each read twice, but only one host read occurs per round.
    try eq(calls + 1, owner.calls.load(.acquire));
    try eq(@as(u64, 42), host.completed.load(.acquire));

    // An expired source is excluded by its owner at read time, even with no
    // cleanup or event between expiry and the read (the sampling block may stop).
    owner.now.store(10, .release);
    try expect(!(try controller.read(.{ .item = 14 })).valueOr(true));
    try controller.notifyChanged(claim, support.reason, null);
    controller.waitIdle();
    try eq(types.BlockState.stopped, try controller.readState(2));
    try eq(types.BlockState.stopped, try controller.readState(3));

    try controller.unbind(claim, support.reason, null);
    controller.waitIdle();
    const unbound_calls = owner.calls.load(.acquire);
    try eq(types.Read.owner_stopped, try controller.read(.{ .item = 14 }));
    try controller.notifyChanged(claim, support.reason, null);
    controller.waitIdle();
    try eq(unbound_calls, owner.calls.load(.acquire));
    owner.expires.store(20, .release);
    try controller.bind(claim, support.reason, null);
    controller.waitIdle();
    try eq(types.BlockState.running, try controller.readState(2));

    owner.kind.store(99, .release);
    try controller.notifyChanged(claim, support.reason, null);
    controller.waitIdle();
    try eq(types.Read.read_failed, try controller.read(.{ .item = 14 }));
    try eq(types.BlockState.stopped, try controller.readState(2));
}
