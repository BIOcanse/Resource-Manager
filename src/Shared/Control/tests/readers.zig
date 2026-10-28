const std = @import("std");
const control = @import("rm_control");
const support = @import("support.zig");
const d = control.declarations;
const types = control.types;
const expect = std.testing.expect;
const expectEqual = std.testing.expectEqual;

var compute_calls = std.atomic.Value(u32).init(0);
fn requested(input: *types.Input) anyerror!bool {
    _ = compute_calls.fetchAdd(1, .monotonic);
    return (try input.read(.{ .item = 14 })).valueOr(false);
}
const registry = d.Registry{
    .version = "runtime-readers-fixture",
    .process = "test",
    .blocks = &.{ .{ .uid = 1, .name = null }, .{ .uid = 2, .name = null }, .{ .uid = 3, .name = null } },
    .items = &.{
        .{ .uid = 11, .storage = .own, .value_type = .bool, .per_block = false, .trigger = .event, .owner_block = null, .name = null },
        .{ .uid = 12, .storage = .own, .value_type = .bool, .per_block = false, .trigger = .event, .owner_block = null, .name = null },
        .{ .uid = 13, .storage = .own, .value_type = .bool, .per_block = false, .trigger = .event, .owner_block = null, .name = null },
        .{ .uid = 14, .storage = .own, .value_type = .bool, .per_block = false, .trigger = .event, .owner_block = null, .name = null },
        .{ .uid = 15, .storage = .reference, .value_type = .bool, .per_block = true, .trigger = .event, .owner_block = null, .name = null },
        .{ .uid = 16, .storage = .reference, .value_type = .bool, .per_block = false, .trigger = .event, .owner_block = null, .name = null },
    },
    .conditions = &.{
        .{ .uid = 20, .node = 1, .per_block = false, .items = &.{.{ .item = 14, .block = null }}, .conditions = &.{}, .children = &.{}, .decides = 2, .name = null },
        .{ .uid = 21, .node = 1, .per_block = false, .items = &.{.{ .item = 14, .block = null }}, .conditions = &.{}, .children = &.{}, .decides = 3, .name = null },
    },
};
const tree = control.tree.Tree{
    .program = 1,
    .blocks = &.{ .{ .uid = 1, .parent = null }, .{ .uid = 2, .parent = 1 }, .{ .uid = 3, .parent = 1 } },
    .uses = &.{},
    .computes = &.{ .{ .condition = 20, .function = requested }, .{ .condition = 21, .function = requested } },
};
const C = control.Controller(registry, tree);
const Host = support.TestHost(C);
const why: types.Reason = .{ .kind = 2, .owner = "reader-test", .cause = "one-event", .plan_version = 12345 };
fn config() types.Config {
    var cfg = support.config(32);
    cfg.host_blocks = &.{ 2, 3 };
    cfg.reader_capacity = 16;
    cfg.reader_bytes = 4096;
    return cfg;
}

const Delivery = struct {
    ids: [8]u64 = @splat(0),
    keys: [8]types.ItemKey = @splat(.{ .item = 0 }),
    counts: [8]std.atomic.Value(u32) = @splat(.init(0)),
    publisher: std.Thread.Id,
    wrong_thread: std.atomic.Value(bool) = .init(false),
    wrong_key: std.atomic.Value(bool) = .init(false),
    wrong_reason: std.atomic.Value(bool) = .init(false),
    unknown_registration: std.atomic.Value(bool) = .init(false),
    fn changed(host: *Host, id: u64, view: types.ItemKeyView, reason: *const types.ReasonView) void {
        const self: *Delivery = @ptrCast(@alignCast(host.user_context.?));
        if (std.Thread.getCurrentId() != self.publisher) self.wrong_thread.store(true, .release);
        const key = view.toKey();
        var found = false;
        for (self.ids, 0..) |registered, index| {
            if (registered == 0 or registered != id) continue;
            found = true;
            if (key.item != self.keys[index].item or key.block != self.keys[index].block) self.wrong_key.store(true, .release);
            _ = self.counts[index].fetchAdd(1, .monotonic);
        }
        if (!found) self.unknown_registration.store(true, .release);
        const actual = reason.toReason();
        if (actual.kind != why.kind or actual.plan_version != why.plan_version or !std.mem.eql(u8, actual.owner, why.owner) or !std.mem.eql(u8, actual.cause, why.cause)) self.wrong_reason.store(true, .release);
    }
    fn register(self: *Delivery, controller: *C, index: usize, key: types.ItemKey, holder: []const u8) !void {
        self.keys[index] = key;
        self.ids[index] = try controller.registerReader(key, holder);
    }
};

test "runtime readers: holder identity is per key copied and safe after idempotent unregister" {
    var host: Host = .{};
    const controller = try C.create(std.testing.allocator, config(), host.callbacks());
    host.controller = controller;
    try support.initFacts(controller);
    controller.activate();
    controller.waitIdle();
    defer {
        host.cleanup();
        controller.destroy() catch @panic("destroy failed");
    }
    var holder = "reader".*;
    const old_id = try controller.registerReader(.{ .item = 12 }, &holder);
    @memset(&holder, 'x');
    try std.testing.expectError(error.DuplicateHolder, controller.registerReader(.{ .item = 12 }, "reader"));
    const other_key = try controller.registerReader(.{ .item = 13 }, "reader");
    try std.testing.expectError(error.InvalidHolder, controller.registerReader(.{ .item = 12 }, ""));
    try controller.unregisterReader(old_id);
    try controller.unregisterReader(old_id);
    const replacement = try controller.registerReader(.{ .item = 12 }, "reader");
    try expect(replacement > old_id);
    // Repeating an old id cannot remove a newer registration occupying its slot.
    try controller.unregisterReader(old_id);
    const writer = try controller.claimWrite(.{ .item = 12 });
    try controller.publish(writer, .{ .value = true }, why, null);
    try expectEqual(@as(u32, 1), host.item_changed_calls.load(.acquire));
    try controller.unregisterReader(replacement);
    try controller.publish(writer, .{ .value = false }, why, null);
    try expectEqual(@as(u32, 1), host.item_changed_calls.load(.acquire));
    try controller.unregisterReader(other_key);
}

test "runtime readers: each matching item and block is delivered once on its publishing thread with its reason" {
    var delivery: Delivery = .{ .publisher = std.Thread.getCurrentId() };
    var host: Host = .{ .user_context = &delivery, .item_changed_hook = Delivery.changed };
    const controller = try C.create(std.testing.allocator, config(), host.callbacks());
    host.controller = controller;
    try support.initFacts(controller);
    controller.activate();
    controller.waitIdle();
    defer {
        host.cleanup();
        controller.destroy() catch @panic("destroy failed");
    }
    try delivery.register(controller, 0, .{ .item = 12 }, "left");
    try delivery.register(controller, 1, .{ .item = 12 }, "right");
    try delivery.register(controller, 2, .{ .item = 13 }, "left");
    try delivery.register(controller, 3, .{ .item = 15, .block = 2 }, "per-block");
    try delivery.register(controller, 4, .{ .item = 15, .block = 3 }, "per-block");
    try delivery.register(controller, 5, .{ .item = 16 }, "reference");
    try delivery.register(controller, 6, .{ .item = 11 }, "shutdown");
    const left = try controller.claimWrite(.{ .item = 12 });
    const right = try controller.claimWrite(.{ .item = 13 });
    const cell = try controller.claimCell(.{ .item = 16 });
    try controller.publish(left, .{ .value = true }, why, null);
    try controller.publish(left, .{ .value = true }, why, null);
    try expectEqual(@as(u32, 2), delivery.counts[0].load(.acquire));
    try expectEqual(@as(u32, 2), delivery.counts[1].load(.acquire));
    try expectEqual(@as(u32, 0), delivery.counts[2].load(.acquire));
    try controller.publish(right, .{ .value = false }, why, null);
    try expectEqual(@as(u32, 1), delivery.counts[2].load(.acquire));
    try support.setFact(controller, 3, true, why, null);
    try expectEqual(@as(u32, 0), delivery.counts[3].load(.acquire));
    try expectEqual(@as(u32, 1), delivery.counts[4].load(.acquire));
    try support.setFact(controller, 2, true, why, null);
    try support.setFact(controller, 2, false, why, null);
    try expectEqual(@as(u32, 2), delivery.counts[3].load(.acquire));
    try expectEqual(@as(u32, 1), delivery.counts[4].load(.acquire));
    try controller.publish(cell, .{ .value = true }, why, null);
    try controller.unbind(cell, why, null);
    try controller.bind(cell, why, null);
    try controller.publish(cell, .{ .value = false }, why, null);
    try expectEqual(@as(u32, 4), delivery.counts[5].load(.acquire));
    try controller.unregisterReader(delivery.ids[1]);
    try controller.publish(left, .{ .value = false }, why, null);
    try expectEqual(@as(u32, 3), delivery.counts[0].load(.acquire));
    try expectEqual(@as(u32, 2), delivery.counts[1].load(.acquire));
    try controller.shutdown(why, null);
    try expectEqual(@as(u32, 1), delivery.counts[6].load(.acquire));
    for (delivery.ids) |id| if (id != 0) try controller.unregisterReader(id);
    try controller.publish(left, .{ .value = false }, why, null);
    try expectEqual(@as(u32, 3), delivery.counts[0].load(.acquire));
    try expect(!delivery.wrong_thread.load(.acquire));
    try expect(!delivery.wrong_key.load(.acquire));
    try expect(!delivery.wrong_reason.load(.acquire));
    try expect(!delivery.unknown_registration.load(.acquire));
}

const Reentrant = struct {
    outer_id: u64 = 0,
    nested_id: std.atomic.Value(u64) = .init(0),
    outer_calls: std.atomic.Value(u32) = .init(0),
    nested_calls: std.atomic.Value(u32) = .init(0),
    failed: std.atomic.Value(bool) = .init(false),
    fn changed(host: *Host, id: u64, view: types.ItemKeyView, _: *const types.ReasonView) void {
        const self: *Reentrant = @ptrCast(@alignCast(host.user_context.?));
        const controller = host.controller.?;
        const value = controller.read(view.toKey()) catch {
            self.failed.store(true, .release);
            return;
        };
        if (!value.valueOr(false)) self.failed.store(true, .release);
        if (id == self.outer_id) {
            _ = self.outer_calls.fetchAdd(1, .monotonic);
            const nested = controller.registerReader(.{ .item = 13 }, "nested-from-callback") catch {
                self.failed.store(true, .release);
                return;
            };
            self.nested_id.store(nested, .release);
            controller.unregisterReader(id) catch {
                self.failed.store(true, .release);
            };
            controller.unregisterReader(id) catch {
                self.failed.store(true, .release);
            };
        } else if (id == self.nested_id.load(.acquire)) {
            _ = self.nested_calls.fetchAdd(1, .monotonic);
        } else self.failed.store(true, .release);
    }
};
test "runtime readers: callbacks can read register and unregister without holding controller locks" {
    var probe: Reentrant = .{};
    var host: Host = .{ .user_context = &probe, .item_changed_hook = Reentrant.changed };
    const controller = try C.create(std.testing.allocator, config(), host.callbacks());
    host.controller = controller;
    try support.initFacts(controller);
    controller.activate();
    controller.waitIdle();
    defer {
        host.cleanup();
        controller.destroy() catch @panic("destroy failed");
    }
    probe.outer_id = try controller.registerReader(.{ .item = 12 }, "reentrant");
    const left = try controller.claimWrite(.{ .item = 12 });
    const right = try controller.claimWrite(.{ .item = 13 });
    try controller.publish(left, .{ .value = true }, why, null);
    try expectEqual(@as(u32, 1), probe.outer_calls.load(.acquire));
    const nested = probe.nested_id.load(.acquire);
    try expect(nested != 0);
    try controller.publish(left, .{ .value = true }, why, null);
    try expectEqual(@as(u32, 1), probe.outer_calls.load(.acquire));
    try controller.publish(right, .{ .value = true }, why, null);
    try expectEqual(@as(u32, 1), probe.nested_calls.load(.acquire));
    try controller.unregisterReader(nested);
    try controller.unregisterReader(nested);
    try expect(!probe.failed.load(.acquire));
}

const HeldReader = struct {
    entered: std.atomic.Value(bool) = .init(false),
    release: std.atomic.Value(bool) = .init(false),
    returned: std.atomic.Value(bool) = .init(false),
    completed: std.atomic.Value(bool) = .init(false),
    fn changed(host: *Host, _: u64, _: types.ItemKeyView, _: *const types.ReasonView) void {
        const self: *HeldReader = @ptrCast(@alignCast(host.user_context.?));
        self.entered.store(true, .release);
        // This test gate deliberately holds a callback to prove it is excluded
        // from the controller's declared-reader completion token.
        while (!self.release.load(.acquire)) std.Thread.yield() catch {};
        self.returned.store(true, .release);
    }
    fn complete(host: *Host, token: u64) void {
        const self: *HeldReader = @ptrCast(@alignCast(host.user_context.?));
        if (token == 303) self.completed.store(true, .release);
    }
};
const Publisher = struct {
    controller: *C,
    claim: control.state.Claim,
    failed: std.atomic.Value(bool) = .init(false),
    fn run(self: *Publisher) void {
        self.controller.publish(self.claim, .{ .value = true }, why, 303) catch {
            self.failed.store(true, .release);
        };
    }
};
fn waitFlag(flag: *std.atomic.Value(bool)) !void {
    var turns: usize = 0;
    while (turns < 5_000_000) : (turns += 1) {
        if (flag.load(.acquire)) return;
        std.Thread.yield() catch {};
    }
    return error.TestTimeout;
}
test "runtime readers: completion covers declared rounds while a runtime callback is still held" {
    compute_calls.store(0, .release);
    var probe: HeldReader = .{};
    var host: Host = .{ .user_context = &probe, .item_changed_hook = HeldReader.changed, .complete_hook = HeldReader.complete };
    const controller = try C.create(std.testing.allocator, config(), host.callbacks());
    host.controller = controller;
    try support.initFacts(controller);
    controller.activate();
    controller.waitIdle();
    defer {
        host.cleanup();
        controller.destroy() catch @panic("destroy failed");
    }
    const baseline = compute_calls.load(.acquire);
    const writer = try controller.claimWrite(.{ .item = 14 });
    const reader = try controller.registerReader(.{ .item = 14 }, "held-runtime-reader");
    defer controller.unregisterReader(reader) catch {};
    var publication: Publisher = .{ .controller = controller, .claim = writer };
    var publisher: ?std.Thread = null;
    defer {
        probe.release.store(true, .release);
        if (publisher) |thread| thread.join();
    }
    publisher = try std.Thread.spawn(.{}, Publisher.run, .{&publication});
    try waitFlag(&probe.entered);
    try waitFlag(&probe.completed);
    try expect(compute_calls.load(.acquire) > baseline);
    try expect(!probe.returned.load(.acquire));
    try expectEqual(types.BlockState.running, try controller.readState(2));
    try expectEqual(types.BlockState.running, try controller.readState(3));
    probe.release.store(true, .release);
    publisher.?.join();
    publisher = null;
    controller.waitIdle();
    try expect(probe.returned.load(.acquire));
    try expect(!publication.failed.load(.acquire));
    try expectEqual(@as(u32, 1), host.complete_calls.load(.acquire));
}

test "runtime readers: bounded holder storage is reused without any allocation after creation" {
    var allocator = std.testing.FailingAllocator.init(std.testing.allocator, .{});
    var cfg = config();
    cfg.reader_capacity = 1;
    cfg.reader_bytes = 4;
    var host: Host = .{};
    const controller = try C.create(allocator.allocator(), cfg, host.callbacks());
    host.controller = controller;
    try support.initFacts(controller);
    controller.activate();
    controller.waitIdle();
    defer {
        host.cleanup();
        controller.destroy() catch @panic("destroy failed");
    }
    const allocations = allocator.alloc_index;
    const resizes = allocator.resize_index;
    allocator.fail_index = allocations;
    allocator.resize_fail_index = resizes;
    const first = try controller.registerReader(.{ .item = 12 }, "abcd");
    try std.testing.expectError(error.ReaderFull, controller.registerReader(.{ .item = 13 }, "x"));
    try controller.unregisterReader(first);
    try std.testing.expectError(error.ReaderFull, controller.registerReader(.{ .item = 13 }, "abcde"));
    const replacement = try controller.registerReader(.{ .item = 13 }, "xy");
    try expect(replacement > first);
    try controller.unregisterReader(first);
    const writer = try controller.claimWrite(.{ .item = 13 });
    try controller.publish(writer, .{ .value = true }, why, null);
    try expectEqual(@as(u32, 1), host.item_changed_calls.load(.acquire));
    try controller.unregisterReader(replacement);
    try expectEqual(allocations, allocator.alloc_index);
    try expectEqual(resizes, allocator.resize_index);
    try expect(!allocator.has_induced_failure);
}
