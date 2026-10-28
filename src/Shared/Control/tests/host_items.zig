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
    .version = "host-items-fixture",
    .process = "test",
    .blocks = &.{ .{ .uid = 1, .name = null }, .{ .uid = 2, .name = null }, .{ .uid = 3, .name = null } },
    .items = &.{
        .{ .uid = 11, .storage = .own, .value_type = .bool, .per_block = false, .trigger = .event, .owner_block = null, .name = null },
        .{ .uid = 12, .storage = .own, .value_type = .host, .per_block = false, .trigger = .event, .owner_block = null, .name = null },
        .{ .uid = 13, .storage = .reference, .value_type = .host, .per_block = false, .trigger = .event, .owner_block = null, .name = null },
        .{ .uid = 14, .storage = .own, .value_type = .bool, .per_block = false, .trigger = .event, .owner_block = null, .name = null },
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
fn config() types.Config {
    var cfg = support.config(16);
    cfg.host_blocks = &.{ 2, 3 };
    return cfg;
}
fn expectPresence(expected: std.meta.Tag(types.Presence), actual: types.Presence) !void {
    try expectEqual(expected, std.meta.activeTag(actual));
}
fn rowFor(root: std.json.Value, uid: i64) !std.json.Value {
    const rows = root.object.get("items").?.array.items;
    for (rows) |row| if (row.object.get("uid").?.integer == uid) return row;
    return error.MissingItem;
}

test "host items: own presence has one owner and rejects boolean access" {
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
    const owner = try controller.claimWrite(.{ .item = 12 });
    try std.testing.expectError(error.AlreadyClaimed, controller.claimWrite(.{ .item = 12 }));
    try expectPresence(.not_published, try controller.readPresence(.{ .item = 12 }));
    try std.testing.expectError(error.TypeMismatch, controller.read(.{ .item = 12 }));
    try std.testing.expectError(error.TypeMismatch, controller.publish(owner, .{ .value = true }, support.reason, null));
    try expectPresence(.not_published, try controller.readPresence(.{ .item = 12 }));
    for ([_]types.Presence{ .present, .not_published, .owner_stopped, .read_failed }) |presence| {
        try controller.publishPresence(owner, presence, support.reason, null);
        try expectPresence(std.meta.activeTag(presence), try controller.readPresence(.{ .item = 12 }));
    }
    const boolean = try controller.claimWrite(.{ .item = 14 });
    try std.testing.expectError(error.TypeMismatch, controller.readPresence(.{ .item = 14 }));
    try std.testing.expectError(error.TypeMismatch, controller.publishPresence(boolean, .present, support.reason, null));
    try expectEqual(std.meta.Tag(types.Read).not_published, std.meta.activeTag(try controller.read(.{ .item = 14 })));
}

const Probe = struct {
    calls: std.atomic.Value(u32) = .init(0),
    fn changed(host: *Host, _: u64, key: types.ItemKeyView, _: *const types.ReasonView) void {
        const self: *Probe = @ptrCast(@alignCast(host.user_context.?));
        _ = self.calls.fetchAdd(1, .monotonic);
        _ = host.controller.?.readPresence(key.toKey()) catch {
            host.callback_failed.store(true, .release);
        };
    }
};
test "host items: reference bind and unbind notify named runtime readers" {
    var probe: Probe = .{};
    var host: Host = .{ .user_context = &probe, .item_changed_hook = Probe.changed };
    const controller = try C.create(std.testing.allocator, config(), host.callbacks());
    host.controller = controller;
    try support.initFacts(controller);
    controller.activate();
    controller.waitIdle();
    defer {
        host.cleanup();
        controller.destroy() catch @panic("destroy failed");
    }
    const cell = try controller.claimCell(.{ .item = 13 });
    try std.testing.expectError(error.AlreadyClaimed, controller.claimCell(.{ .item = 13 }));
    const reader = try controller.registerReader(.{ .item = 13 }, "host-presence-reader");
    defer controller.unregisterReader(reader) catch {};
    try expectPresence(.not_published, try controller.readPresence(.{ .item = 13 }));
    try controller.publishPresence(cell, .present, support.reason, 101);
    try expectPresence(.present, try controller.readPresence(.{ .item = 13 }));
    try controller.unbind(cell, support.reason, 102);
    try expectPresence(.owner_stopped, try controller.readPresence(.{ .item = 13 }));
    try expectEqual(@as(u32, 2), probe.calls.load(.acquire));
    // A replacement while unbound stays private until its owner binds again.
    try controller.publishPresence(cell, .present, support.reason, null);
    try expectPresence(.owner_stopped, try controller.readPresence(.{ .item = 13 }));
    try expectEqual(@as(u32, 2), probe.calls.load(.acquire));
    try controller.bind(cell, support.reason, 103);
    try expectPresence(.present, try controller.readPresence(.{ .item = 13 }));
    try controller.publishPresence(cell, .read_failed, support.reason, 104);
    try expectPresence(.read_failed, try controller.readPresence(.{ .item = 13 }));
    controller.waitIdle();
    try expectEqual(@as(u32, 4), probe.calls.load(.acquire));
    try expectEqual(@as(u32, 4), host.complete_calls.load(.acquire));
    try expect(!host.callback_failed.load(.acquire));
}

test "host items: presence does not mark conditions and JSON exposes no host payload" {
    compute_calls.store(0, .release);
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
    controller.waitIdle();
    const calls = compute_calls.load(.acquire);
    const sequence = controller.decisionSequence(1);
    const owner = try controller.claimWrite(.{ .item = 12 });
    const cell = try controller.claimCell(.{ .item = 13 });
    try controller.publishPresence(owner, .present, support.reason, null);
    try controller.publishPresence(cell, .present, support.reason, null);
    try controller.unbind(cell, support.reason, null);
    controller.waitIdle();
    try expectEqual(calls, compute_calls.load(.acquire));
    try expectEqual(sequence, controller.decisionSequence(1));
    try expectEqual(@as(u32, 0), host.startCount(2));
    try expectEqual(@as(u32, 0), host.startCount(3));
    var buffer: [32768]u8 = undefined;
    const size = try controller.snapshot(&buffer);
    const parsed = try std.json.parseFromSlice(std.json.Value, std.testing.allocator, buffer[0..size], .{});
    defer parsed.deinit();
    const own_row = try rowFor(parsed.value, 12);
    const ref_row = try rowFor(parsed.value, 13);
    try expect(own_row.object.get("hasValue").?.bool);
    try expect(own_row.object.get("noValueReason").? == .null);
    try expect(!ref_row.object.get("hasValue").?.bool);
    try std.testing.expectEqualStrings("owner_stopped", ref_row.object.get("noValueReason").?.string);
    try expect(own_row.object.get("value") == null);
    try expect(ref_row.object.get("value") == null);
    try expect(own_row.object.get("name") == null);
    try expect(ref_row.object.get("name") == null);
}

const HeldMark = struct {
    entered: std.atomic.Value(bool) = .init(false),
    release: std.atomic.Value(bool) = .init(false),
    calls: std.atomic.Value(u32) = .init(0),
    held_completed: std.atomic.Value(bool) = .init(false),
    other_completed: std.atomic.Value(bool) = .init(false),
    completed_before_reader_return: std.atomic.Value(bool) = .init(false),
    fn changed(host: *Host, _: u64, _: types.ItemKeyView, _: *const types.ReasonView) void {
        const self: *HeldMark = @ptrCast(@alignCast(host.user_context.?));
        self.entered.store(true, .release);
        // The test deliberately holds this callback. Completion tokens cover
        // only declared conditions and must still return while it is held.
        while (!self.release.load(.acquire)) std.Thread.yield() catch {};
        _ = self.calls.fetchAdd(1, .monotonic);
    }
    fn complete(host: *Host, token: u64) void {
        const self: *HeldMark = @ptrCast(@alignCast(host.user_context.?));
        if (token == 501) {
            if (self.calls.load(.acquire) == 0) self.completed_before_reader_return.store(true, .release);
            self.held_completed.store(true, .release);
        }
        if (token == 502) self.other_completed.store(true, .release);
    }
};
const PresencePublisher = struct {
    controller: *C,
    claim: control.state.Claim,
    failed: std.atomic.Value(bool) = .init(false),
    fn run(self: *PresencePublisher) void {
        self.controller.publishPresence(self.claim, .present, support.reason, 501) catch {
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
test "host items: a held runtime reader does not delay declared-round completion" {
    var probe: HeldMark = .{};
    var host: Host = .{ .user_context = &probe, .complete_hook = HeldMark.complete, .item_changed_hook = HeldMark.changed };
    const controller = try C.create(std.testing.allocator, config(), host.callbacks());
    host.controller = controller;
    try support.initFacts(controller);
    controller.activate();
    controller.waitIdle();
    defer {
        host.cleanup();
        controller.destroy() catch @panic("destroy failed");
    }
    const host_owner = try controller.claimWrite(.{ .item = 12 });
    const boolean_owner = try controller.claimWrite(.{ .item = 14 });
    const reader = try controller.registerReader(.{ .item = 12 }, "held-host-reader");
    defer controller.unregisterReader(reader) catch {};
    var publication: PresencePublisher = .{ .controller = controller, .claim = host_owner };
    var publisher: ?std.Thread = null;
    defer {
        probe.release.store(true, .release);
        if (publisher) |thread| thread.join();
    }
    publisher = try std.Thread.spawn(.{}, PresencePublisher.run, .{&publication});
    try waitFlag(&probe.entered);
    // This unrelated bool item has declared condition readers. Its token proves
    // a worker finished a round and checked completion while the host event's
    // runtime reader callback is still held.
    try controller.publish(boolean_owner, .{ .value = true }, support.reason, 502);
    try waitFlag(&probe.other_completed);
    try expectEqual(@as(u32, 0), probe.calls.load(.acquire));
    try waitFlag(&probe.held_completed);
    try expect(probe.completed_before_reader_return.load(.acquire));
    probe.release.store(true, .release);
    publisher.?.join();
    publisher = null;
    controller.waitIdle();
    try expect(!publication.failed.load(.acquire));
    try expectEqual(@as(u32, 1), probe.calls.load(.acquire));
    try expect(probe.held_completed.load(.acquire));
    try expect(probe.completed_before_reader_return.load(.acquire));
    try expectEqual(@as(u32, 2), host.complete_calls.load(.acquire));
}
