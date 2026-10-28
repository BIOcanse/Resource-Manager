const std = @import("std");
const control = @import("rm_control");
const support = @import("support.zig");
const Engine = control.Controller(support.registry, support.tree);
test "snapshot carries reader allocator accounting and the complete registry version" {
    var host: support.TestHost(Engine) = .{};
    const controller = try Engine.create(std.testing.allocator, support.config(8), host.callbacks());
    host.controller = controller;
    try support.initFacts(controller);
    controller.activate();
    defer {
        host.cleanup();
        controller.destroy() catch @panic("evidence cleanup failed");
    }
    try support.setFact(controller, 2, true, .{ .kind = 1, .owner = "sample-owner", .cause = "sample-cause", .plan_version = 23 }, null);
    controller.waitIdle();
    var buffer: [32768]u8 = undefined;
    const size = try controller.snapshot(&buffer);
    try std.testing.expect(size <= buffer.len);
    const parsed = try std.json.parseFromSlice(std.json.Value, std.testing.allocator, buffer[0..size], .{});
    defer parsed.deinit();
    try std.testing.expectEqualStrings("fixture-registry", parsed.value.object.get("registryVersion").?.string);
    const memory = parsed.value.object.get("readerAllocator").?.object;
    const current = memory.get("current").?.integer;
    const peak = memory.get("peak").?.integer;
    const capacity = memory.get("capacity").?.integer;
    try std.testing.expectEqual(@as(i64, 0), current);
    try std.testing.expect(current == peak and peak <= capacity);
    try std.testing.expect(parsed.value.object.get("demands") == null);
    try std.testing.expect(parsed.value.object.get("allocator") == null);
    try support.setFact(controller, 2, false, support.reason, null);
    controller.waitIdle();
    const empty_size = try controller.snapshot(&buffer);
    const cleared = try std.json.parseFromSlice(std.json.Value, std.testing.allocator, buffer[0..empty_size], .{});
    defer cleared.deinit();
    try std.testing.expectEqual(@as(i64, 0), cleared.value.object.get("readerAllocator").?.object.get("current").?.integer);
    try std.testing.expectEqual(peak, cleared.value.object.get("readerAllocator").?.object.get("peak").?.integer);
}
