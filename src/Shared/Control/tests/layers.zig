const std = @import("std");
const control = @import("rm_control");
const support = @import("support.zig");
const d = control.declarations;
const types = control.types;
const expect = std.testing.expect;
const expectEqual = std.testing.expectEqual;

var lower_calls = std.atomic.Value(u32).init(0);
var gate_calls = std.atomic.Value(u32).init(0);
var top_calls = std.atomic.Value(u32).init(0);
var unrelated_calls = std.atomic.Value(u32).init(0);
var fail_lower = std.atomic.Value(bool).init(false);
var block_lower = std.atomic.Value(bool).init(false);
var lower_entered = std.atomic.Value(bool).init(false);
var release_lower = std.atomic.Value(bool).init(false);

fn lower(input: *types.Input) anyerror!bool {
    _ = lower_calls.fetchAdd(1, .monotonic);
    if (block_lower.load(.acquire)) {
        lower_entered.store(true, .release);
        while (!release_lower.load(.acquire)) std.Thread.yield() catch {};
    }
    if (fail_lower.load(.acquire)) return error.InjectedConditionFailure;
    return (try input.read(.{ .item = 12 })).valueOr(false);
}
fn gate(input: *types.Input) anyerror!bool {
    _ = gate_calls.fetchAdd(1, .monotonic);
    return (try input.read(.{ .item = 13 })).valueOr(false);
}
fn top(input: *types.Input) anyerror!bool {
    _ = top_calls.fetchAdd(1, .monotonic);
    // Read both declared inputs even when the first is false.
    const requested = try input.condition(20);
    const allowed = try input.condition(23);
    return requested and allowed;
}
fn unrelated(_: *types.Input) anyerror!bool {
    _ = unrelated_calls.fetchAdd(1, .monotonic);
    return false;
}
const blocks = [_]d.Block{
    .{ .uid = 1, .name = "Program" }, .{ .uid = 2, .name = "Work" }, .{ .uid = 3, .name = "Peer" },
};
const conditions = [_]d.Condition{
    .{ .uid = 20, .node = 1, .per_block = false, .items = &.{.{ .item = 12, .block = null }}, .conditions = &.{}, .children = &.{}, .decides = null, .name = "Requested" },
    .{ .uid = 23, .node = 1, .per_block = false, .items = &.{.{ .item = 13, .block = null }}, .conditions = &.{}, .children = &.{}, .decides = null, .name = "Allowed" },
    .{ .uid = 21, .node = 1, .per_block = false, .items = &.{}, .conditions = &.{ 20, 23 }, .children = &.{}, .decides = 2, .name = "RunWork" },
    .{ .uid = 24, .node = 1, .per_block = false, .items = &.{}, .conditions = &.{}, .children = &.{}, .decides = 3, .name = "PeerUnused" },
};
const registry = d.Registry{ .version = "layer-fixture", .process = "test", .blocks = &blocks, .items = &support.items, .conditions = &conditions };
const tree = control.tree.Tree{
    .program = 1,
    .blocks = &.{ .{ .uid = 1, .parent = null }, .{ .uid = 2, .parent = 1 }, .{ .uid = 3, .parent = 1 } },
    .uses = &.{},
    .computes = &.{ .{ .condition = 20, .function = lower }, .{ .condition = 23, .function = gate }, .{ .condition = 21, .function = top }, .{ .condition = 24, .function = unrelated } },
};
const C = control.Controller(registry, tree);
const Host = support.TestHost(C);
fn config() types.Config {
    var value = support.config(32);
    value.host_blocks = &.{ 2, 3 };
    return value;
}
fn resetCounters() void {
    lower_calls.store(0, .release);
    gate_calls.store(0, .release);
    top_calls.store(0, .release);
    unrelated_calls.store(0, .release);
    fail_lower.store(false, .release);
    block_lower.store(false, .release);
    lower_entered.store(false, .release);
    release_lower.store(false, .release);
}
fn waitFlag(flag: *std.atomic.Value(bool)) !void {
    var turns: usize = 0;
    while (turns < 5_000_000) : (turns += 1) {
        if (flag.load(.acquire)) return;
        std.Thread.yield() catch {};
    }
    return error.TestTimeout;
}

test "layers: lower change with unchanged top neither aligns nor records a decision" {
    resetCounters();
    var host: Host = .{};
    const controller = try C.create(std.testing.allocator, config(), host.callbacks());
    host.controller = controller;
    controller.activate();
    defer {
        host.cleanup();
        controller.destroy() catch @panic("destroy failed");
    }
    const left = try controller.claimWrite(.{ .item = 12 });
    const right = try controller.claimWrite(.{ .item = 13 });
    try controller.publish(left, .{ .value = false }, support.reason, null);
    try controller.publish(right, .{ .value = false }, support.reason, null);
    controller.waitIdle();
    const sequence = controller.decisionSequence(1);
    const retained = controller.decisionCount(1);
    try controller.publish(left, .{ .value = true }, support.reason, null);
    controller.waitIdle();
    try expectEqual(sequence, controller.decisionSequence(1));
    try expectEqual(retained, controller.decisionCount(1));
    try expectEqual(@as(u32, 0), host.startCount(2));
    try expectEqual(@as(u32, 0), host.stopCount(2));
}

test "layers: only readers of the changed item and changed lower recompute" {
    resetCounters();
    var host: Host = .{};
    const controller = try C.create(std.testing.allocator, config(), host.callbacks());
    host.controller = controller;
    controller.activate();
    defer {
        host.cleanup();
        controller.destroy() catch @panic("destroy failed");
    }
    const left = try controller.claimWrite(.{ .item = 12 });
    const right = try controller.claimWrite(.{ .item = 13 });
    try controller.publish(left, .{ .value = false }, support.reason, null);
    try controller.publish(right, .{ .value = false }, support.reason, null);
    controller.waitIdle();
    const before_lower = lower_calls.load(.acquire);
    const before_top = top_calls.load(.acquire);
    const before_gate = gate_calls.load(.acquire);
    const before_peer = unrelated_calls.load(.acquire);
    try controller.publish(left, .{ .value = true }, support.reason, null);
    controller.waitIdle();
    try expectEqual(before_lower + 1, lower_calls.load(.acquire));
    try expectEqual(before_top + 1, top_calls.load(.acquire));
    try expectEqual(before_gate, gate_calls.load(.acquire));
    try expectEqual(before_peer, unrelated_calls.load(.acquire));
    // A same-value event recomputes its direct reader, but propagates no mark.
    try controller.publish(left, .{ .value = true }, support.reason, null);
    controller.waitIdle();
    try expectEqual(before_lower + 2, lower_calls.load(.acquire));
    try expectEqual(before_top + 1, top_calls.load(.acquire));
}

test "layers: failed compute preserves value and actions then recovers at next event" {
    resetCounters();
    var host: Host = .{};
    const controller = try C.create(std.testing.allocator, config(), host.callbacks());
    host.controller = controller;
    controller.activate();
    defer {
        host.cleanup();
        controller.destroy() catch @panic("destroy failed");
    }
    const left = try controller.claimWrite(.{ .item = 12 });
    const right = try controller.claimWrite(.{ .item = 13 });
    try controller.publish(left, .{ .value = true }, support.reason, null);
    try controller.publish(right, .{ .value = true }, support.reason, null);
    controller.waitIdle();
    try expectEqual(types.BlockState.running, try controller.readState(2));
    const sequence = controller.decisionSequence(1);
    const starts = host.startCount(2);
    const top_before = top_calls.load(.acquire);
    fail_lower.store(true, .release);
    try controller.publish(left, .{ .value = false }, support.reason, null);
    controller.waitIdle();
    try expectEqual(sequence, controller.decisionSequence(1));
    try expectEqual(top_before, top_calls.load(.acquire));
    try expectEqual(starts, host.startCount(2));
    try expectEqual(@as(u32, 0), host.stopCount(2));
    try expectEqual(types.BlockState.running, try controller.readState(2));
    var buffer: [32768]u8 = undefined;
    const size = try controller.snapshot(&buffer);
    try expect(size <= buffer.len);
    try expect(std.mem.indexOf(u8, buffer[0..size], "InjectedConditionFailure") != null);
    fail_lower.store(false, .release);
    try controller.publish(left, .{ .value = false }, support.reason, null);
    controller.waitIdle();
    try expectEqual(types.BlockState.stopped, try controller.readState(2));
    try expectEqual(@as(u32, 1), host.stopCount(2));
    const recovered_size = try controller.snapshot(&buffer);
    try expect(std.mem.indexOf(u8, buffer[0..recovered_size], "InjectedConditionFailure") == null);
}

test "boundary: completion token waits for the readers current round" {
    resetCounters();
    var host: Host = .{};
    const controller = try C.create(std.testing.allocator, config(), host.callbacks());
    host.controller = controller;
    controller.activate();
    defer {
        release_lower.store(true, .release);
        host.cleanup();
        controller.destroy() catch @panic("destroy failed");
    }
    const left = try controller.claimWrite(.{ .item = 12 });
    try controller.publish(left, .{ .value = false }, support.reason, null);
    controller.waitIdle();
    block_lower.store(true, .release);
    try controller.publish(left, .{ .value = true }, support.reason, 987);
    try waitFlag(&lower_entered);
    try expectEqual(@as(u32, 0), host.complete_calls.load(.acquire));
    release_lower.store(true, .release);
    controller.waitIdle();
    try expectEqual(@as(u64, 987), host.completed.load(.acquire));
    try expectEqual(@as(u32, 1), host.complete_calls.load(.acquire));
}

const per_registry = d.Registry{ .version = "per-block-fixture", .process = "test", .blocks = &blocks, .items = &support.items, .conditions = &.{support.demand_condition} };
const per_tree = control.tree.Tree{
    .program = 1,
    .blocks = tree.blocks,
    .uses = &.{ .{ .node = 1, .condition = 20, .child = 2 }, .{ .node = 1, .condition = 20, .child = 3 } },
    .computes = &.{.{ .condition = 20, .function = support.demanded }},
};
const PerC = control.Controller(per_registry, per_tree);
const PerHost = support.TestHost(PerC);

test "layers: one per-block condition decides several children independently" {
    var host: PerHost = .{};
    const controller = try PerC.create(std.testing.allocator, config(), host.callbacks());
    host.controller = controller;
    controller.activate();
    defer {
        host.cleanup();
        controller.destroy() catch @panic("destroy failed");
    }
    try controller.setDemand("one", 2, support.reason, null);
    controller.waitIdle();
    try expectEqual(types.BlockState.running, try controller.readState(2));
    try expectEqual(types.BlockState.stopped, try controller.readState(3));
    try controller.setDemand("two", 3, support.reason, null);
    controller.waitIdle();
    try expectEqual(types.BlockState.running, try controller.readState(2));
    try expectEqual(types.BlockState.running, try controller.readState(3));
    try controller.clearDemand("one", support.reason, null);
    controller.waitIdle();
    try expectEqual(types.BlockState.stopped, try controller.readState(2));
    try expectEqual(types.BlockState.running, try controller.readState(3));
    try expectEqual(@as(u32, 1), host.startCount(3));
    try expectEqual(@as(u32, 0), host.stopCount(3));
}

test "boundary: reentrant callbacks report runs without a controller lock" {
    var host: PerHost = .{};
    const controller = try PerC.create(std.testing.allocator, config(), host.callbacks());
    host.controller = controller;
    controller.activate();
    defer {
        host.cleanup();
        controller.destroy() catch @panic("destroy failed");
    }
    try controller.setDemand("reentrant", 2, support.reason, null);
    controller.waitIdle();
    try expectEqual(types.BlockState.running, try controller.readState(2));
    try controller.clearDemand("reentrant", support.reason, null);
    controller.waitIdle();
    try expectEqual(types.BlockState.stopped, try controller.readState(2));
    try expect(!host.callback_failed.load(.acquire));
    try expectEqual(@as(u32, 1), host.startCount(2));
    try expectEqual(@as(u32, 1), host.stopCount(2));
}

test "boundary: old run reports cannot change a newer run or undo dispatched stop" {
    var host: PerHost = .{};
    const controller = try PerC.create(std.testing.allocator, config(), host.callbacks());
    host.controller = controller;
    controller.activate();
    defer {
        host.cleanup();
        controller.destroy() catch @panic("destroy failed");
    }
    try controller.setDemand("run", 2, support.reason, null);
    controller.waitIdle();
    const old_run = host.run(2);
    try controller.clearDemand("run", support.reason, null);
    controller.waitIdle();
    try controller.setDemand("run", 2, support.reason, null);
    controller.waitIdle();
    const new_run = host.run(2);
    try expect(new_run > old_run);
    try controller.reportReturned(2, old_run);
    try controller.reportRunning(2, old_run);
    controller.waitIdle();
    try expectEqual(types.BlockState.running, try controller.readState(2));
    try expectEqual(new_run, host.run(2));
    host.auto_return[2].store(false, .release);
    try controller.clearDemand("run", support.reason, null);
    controller.waitIdle();
    try expectEqual(types.BlockState.stopping, try controller.readState(2));
    try controller.reportRunning(2, new_run);
    try expectEqual(types.BlockState.stopping, try controller.readState(2));
    try host.finish(2);
    controller.waitIdle();
    try expectEqual(types.BlockState.stopped, try controller.readState(2));
}

test "boundary: full demand allocator returns failure without replacing its entries" {
    var host: PerHost = .{};
    var cfg = config();
    cfg.demand_capacity = 1;
    cfg.demand_bytes = 128;
    const controller = try PerC.create(std.testing.allocator, cfg, host.callbacks());
    host.controller = controller;
    controller.activate();
    defer {
        host.cleanup();
        controller.destroy() catch @panic("destroy failed");
    }
    try controller.setDemand("one", 2, support.reason, null);
    controller.waitIdle();
    try std.testing.expectError(error.DemandFull, controller.setDemand("two", 3, support.reason, null));
    controller.waitIdle();
    try expectEqual(types.BlockState.running, try controller.readState(2));
    try expectEqual(types.BlockState.stopped, try controller.readState(3));
}

test "boundary: destroy joins workers after all runs return during shutdown" {
    var host: PerHost = .{};
    const controller = try PerC.create(std.testing.allocator, config(), host.callbacks());
    host.controller = controller;
    controller.activate();
    errdefer {
        host.cleanup();
        controller.destroy() catch {};
    }
    try controller.setDemand("shutdown", 2, support.reason, null);
    try controller.setDemand("shutdown-peer", 3, support.reason, null);
    controller.waitIdle();
    try expectEqual(types.BlockState.running, try controller.readState(2));
    try expectEqual(types.BlockState.running, try controller.readState(3));
    try controller.shutdown(support.reason, 1234);
    controller.waitIdle();
    try expectEqual(types.BlockState.stopped, try controller.readState(2));
    try expectEqual(types.BlockState.stopped, try controller.readState(3));
    try expectEqual(@as(u64, 1234), host.completed.load(.acquire));
    try controller.destroy();
}

test "layers: shutdown overrides a custom true top without changing its inputs" {
    resetCounters();
    var host: Host = .{};
    const controller = try C.create(std.testing.allocator, config(), host.callbacks());
    host.controller = controller;
    controller.activate();
    defer {
        host.cleanup();
        controller.destroy() catch @panic("destroy failed");
    }
    const left = try controller.claimWrite(.{ .item = 12 });
    const right = try controller.claimWrite(.{ .item = 13 });
    try controller.publish(left, .{ .value = true }, support.reason, null);
    try controller.publish(right, .{ .value = true }, support.reason, null);
    controller.waitIdle();
    try expectEqual(types.BlockState.running, try controller.readState(2));
    try controller.shutdown(support.reason, null);
    controller.waitIdle();
    try expectEqual(types.BlockState.stopped, try controller.readState(2));
    try expect((try controller.read(.{ .item = 12 })).valueOr(false));
    try expect((try controller.read(.{ .item = 13 })).valueOr(false));
}

test "layers: an explicit condition treats each no-value reason as false" {
    resetCounters();
    var host: Host = .{};
    const controller = try C.create(std.testing.allocator, config(), host.callbacks());
    host.controller = controller;
    controller.activate();
    defer {
        host.cleanup();
        controller.destroy() catch @panic("destroy failed");
    }
    const left = try controller.claimWrite(.{ .item = 12 });
    const right = try controller.claimWrite(.{ .item = 13 });
    try controller.publish(right, .{ .value = true }, support.reason, null);
    controller.waitIdle();
    // The first round reads the unpublished item as false.
    try expectEqual(types.BlockState.stopped, try controller.readState(2));
    for ([_]types.Read{ .not_published, .owner_stopped, .read_failed }) |no_value| {
        try controller.publish(left, .{ .value = true }, support.reason, null);
        controller.waitIdle();
        try expectEqual(types.BlockState.running, try controller.readState(2));
        try controller.publish(left, no_value, support.reason, null);
        controller.waitIdle();
        try expectEqual(types.BlockState.stopped, try controller.readState(2));
    }
}

test "layers: a custom condition that omits mode has no hidden freeze filter" {
    resetCounters();
    var host: Host = .{};
    const controller = try C.create(std.testing.allocator, config(), host.callbacks());
    host.controller = controller;
    controller.activate();
    defer {
        host.cleanup();
        controller.destroy() catch @panic("destroy failed");
    }
    const left = try controller.claimWrite(.{ .item = 12 });
    const right = try controller.claimWrite(.{ .item = 13 });
    const mode = try controller.claimCell(.{ .item = 14 });
    try controller.publish(left, .{ .value = true }, support.reason, null);
    try controller.publish(right, .{ .value = true }, support.reason, null);
    try controller.publish(mode, .{ .value = true }, support.reason, null);
    controller.waitIdle();
    const sequence = controller.decisionSequence(1);
    try controller.publish(mode, .{ .value = false }, support.reason, null);
    controller.waitIdle();
    try expectEqual(types.BlockState.running, try controller.readState(2));
    try expectEqual(sequence, controller.decisionSequence(1));
    try expectEqual(@as(u32, 0), host.stopCount(2));
}

var denied_reads = std.atomic.Value(u32).init(0);
fn deniedItem(input: *types.Input) anyerror!bool {
    _ = input.read(.{ .item = 14 }) catch |err| {
        _ = denied_reads.fetchOr(1, .monotonic);
        return err;
    };
    return true;
}
fn deniedCondition(input: *types.Input) anyerror!bool {
    _ = input.condition(30) catch |err| {
        _ = denied_reads.fetchOr(2, .monotonic);
        return err;
    };
    return true;
}
fn deniedChild(input: *types.Input) anyerror!bool {
    _ = input.childState(3) catch |err| {
        _ = denied_reads.fetchOr(4, .monotonic);
        return err;
    };
    return true;
}
const denied_registry = d.Registry{
    .version = "restricted-input-fixture",
    .process = "test",
    .blocks = &blocks,
    .items = &support.items,
    .conditions = &.{
        .{ .uid = 30, .node = 1, .per_block = false, .items = &.{}, .conditions = &.{}, .children = &.{}, .decides = null, .name = "DeniedItem" },
        .{ .uid = 31, .node = 1, .per_block = false, .items = &.{}, .conditions = &.{}, .children = &.{}, .decides = null, .name = "DeniedCondition" },
        .{ .uid = 32, .node = 1, .per_block = false, .items = &.{}, .conditions = &.{}, .children = &.{}, .decides = 2, .name = "DeniedChildState" },
        .{ .uid = 33, .node = 1, .per_block = false, .items = &.{}, .conditions = &.{}, .children = &.{}, .decides = 3, .name = "UnusedPeer" },
    },
};
const denied_tree = control.tree.Tree{
    .program = 1,
    .blocks = tree.blocks,
    .uses = &.{},
    .computes = &.{ .{ .condition = 30, .function = deniedItem }, .{ .condition = 31, .function = deniedCondition }, .{ .condition = 32, .function = deniedChild }, .{ .condition = 33, .function = unrelated } },
};
test "layers: condition input rejects undeclared items lower conditions and child states" {
    denied_reads.store(0, .release);
    const D = control.Controller(denied_registry, denied_tree);
    var host: support.TestHost(D) = .{};
    const controller = try D.create(std.testing.allocator, config(), host.callbacks());
    host.controller = controller;
    controller.activate();
    defer {
        host.cleanup();
        controller.destroy() catch @panic("destroy failed");
    }
    controller.waitIdle();
    try expectEqual(@as(u32, 7), denied_reads.load(.acquire));
    try expectEqual(@as(u32, 0), host.startCount(2));
    try expectEqual(@as(u32, 0), host.startCount(3));
}

fn enabledDemand(input: *types.Input) anyerror!bool {
    const demand = (try input.read(.{ .item = 10, .block = input.block })).valueOr(false);
    const enabled = (try input.read(.{ .item = 12 })).valueOr(false);
    return demand and enabled;
}
const enabled_registry = d.Registry{
    .version = "enabled-demand-fixture",
    .process = "test",
    .blocks = &blocks,
    .items = &support.items,
    .conditions = &.{.{ .uid = 20, .node = null, .per_block = true, .items = &.{ .{ .item = 10, .block = null }, .{ .item = 12, .block = null } }, .conditions = &.{}, .children = &.{}, .decides = null, .name = "DemandAndEnabled" }},
};
const enabled_tree = control.tree.Tree{
    .program = 1,
    .blocks = tree.blocks,
    .uses = per_tree.uses,
    .computes = &.{.{ .condition = 20, .function = enabledDemand }},
};
test "layers: explicit enable read creates no demand and disable preserves demand" {
    const E = control.Controller(enabled_registry, enabled_tree);
    var host: support.TestHost(E) = .{};
    const controller = try E.create(std.testing.allocator, config(), host.callbacks());
    host.controller = controller;
    controller.activate();
    defer {
        host.cleanup();
        controller.destroy() catch @panic("destroy failed");
    }
    const enabled = try controller.claimWrite(.{ .item = 12 });
    try controller.publish(enabled, .{ .value = true }, support.reason, null);
    controller.waitIdle();
    try expectEqual(types.BlockState.stopped, try controller.readState(2));
    try controller.setDemand("held", 2, support.reason, null);
    controller.waitIdle();
    try expectEqual(types.BlockState.running, try controller.readState(2));
    try controller.publish(enabled, .{ .value = false }, support.reason, null);
    controller.waitIdle();
    try expectEqual(types.BlockState.stopped, try controller.readState(2));
    try expect((try controller.read(.{ .item = 10, .block = 2 })).valueOr(false));
    try controller.publish(enabled, .{ .value = true }, support.reason, null);
    controller.waitIdle();
    try expectEqual(types.BlockState.running, try controller.readState(2));
    try expectEqual(@as(u32, 2), host.startCount(2));
}

const anonymous_blocks = blk: {
    var value = blocks;
    for (&value) |*block| block.name = null;
    break :blk value;
};
const anonymous_items = blk: {
    var value = support.items;
    for (&value) |*item| item.name = null;
    break :blk value;
};
const anonymous_conditions = blk: {
    var value = conditions;
    for (&value) |*condition| condition.name = null;
    break :blk value;
};
const anonymous_registry = d.Registry{
    .version = "anonymous-fixture",
    .process = "test",
    .blocks = &anonymous_blocks,
    .items = &anonymous_items,
    .conditions = &anonymous_conditions,
};
test "snapshot: condition errors and UIDs remain available without names" {
    resetCounters();
    const Anonymous = control.Controller(anonymous_registry, tree);
    var host: support.TestHost(Anonymous) = .{};
    const controller = try Anonymous.create(std.testing.allocator, config(), host.callbacks());
    host.controller = controller;
    controller.activate();
    defer {
        host.cleanup();
        controller.destroy() catch @panic("destroy failed");
    }
    controller.waitIdle();
    var empty: [0]u8 = .{};
    const needed = try controller.snapshot(&empty);
    try expect(needed > 0);
    var buffer: [32768]u8 = undefined;
    const size = try controller.snapshot(&buffer);
    try expectEqual(needed, size);
    try expect(size <= buffer.len);
    const json = buffer[0..size];
    try expect(std.mem.indexOf(u8, json, "anonymous-fixture") != null);
    try expect(std.mem.indexOf(u8, json, "\"conditions\"") != null);
    try expect(std.mem.indexOf(u8, json, "\"uid\":20") != null or std.mem.indexOf(u8, json, "\"uid\": 20") != null);
    try expect(std.mem.indexOf(u8, json, "\"name\"") == null);
    const left = try controller.claimWrite(.{ .item = 12 });
    fail_lower.store(true, .release);
    try controller.publish(left, .{ .value = true }, support.reason, null);
    controller.waitIdle();
    const failed_size = try controller.snapshot(&buffer);
    try expect(std.mem.indexOf(u8, buffer[0..failed_size], "InjectedConditionFailure") != null);
    fail_lower.store(false, .release);
}

var observed_child_states = std.atomic.Value(u32).init(0);
fn readsChildren(input: *types.Input) anyerror!bool {
    const first = try input.childState(2);
    const next = try input.childState(3);
    const first_bit: u5 = @intCast(@intFromEnum(first));
    const next_bit: u5 = @intCast(8 + @intFromEnum(next));
    _ = observed_child_states.fetchOr((@as(u32, 1) << first_bit) | (@as(u32, 1) << next_bit), .monotonic);
    return (try input.read(.{ .item = 12 })).valueOr(false);
}
const state_registry = d.Registry{
    .version = "child-state-fixture",
    .process = "test",
    .blocks = &blocks,
    .items = &support.items,
    .conditions = &.{
        .{ .uid = 20, .node = 1, .per_block = false, .items = &.{.{ .item = 12, .block = null }}, .conditions = &.{}, .children = &.{ 2, 3 }, .decides = 2, .name = "FirstFromCurrentState" },
        .{ .uid = 21, .node = 1, .per_block = false, .items = &.{.{ .item = 12, .block = null }}, .conditions = &.{}, .children = &.{ 2, 3 }, .decides = 3, .name = "NextFromCurrentState" },
    },
};
const state_tree = control.tree.Tree{
    .program = 1,
    .blocks = tree.blocks,
    .uses = &.{},
    .computes = &.{ .{ .condition = 20, .function = readsChildren }, .{ .condition = 21, .function = readsChildren } },
};
test "layers: declared direct child state reads do not order sibling startup" {
    observed_child_states.store(0, .release);
    const S = control.Controller(state_registry, state_tree);
    var host: support.TestHost(S) = .{};
    host.auto_running[2].store(false, .release);
    const controller = try S.create(std.testing.allocator, config(), host.callbacks());
    host.controller = controller;
    controller.activate();
    defer {
        host.cleanup();
        controller.destroy() catch @panic("destroy failed");
    }
    const requested = try controller.claimWrite(.{ .item = 12 });
    try controller.publish(requested, .{ .value = true }, support.reason, null);
    controller.waitIdle();
    try expectEqual(types.BlockState.starting, try controller.readState(2));
    try expectEqual(types.BlockState.running, try controller.readState(3));
    try expectEqual(@as(u32, 1), host.startCount(2));
    try expectEqual(@as(u32, 1), host.startCount(3));
    const seen = observed_child_states.load(.acquire);
    try expect(seen & (@as(u32, 1) << @intFromEnum(types.BlockState.starting)) != 0);
    try expect(seen & (@as(u32, 1) << (8 + @intFromEnum(types.BlockState.running))) != 0);
    try controller.reportRunning(2, host.run(2));
    controller.waitIdle();
    try controller.publish(requested, .{ .value = true }, support.reason, null);
    controller.waitIdle();
    try expectEqual(@as(u32, 1), host.startCount(2));
    try expectEqual(@as(u32, 1), host.startCount(3));
    try expectEqual(@as(u32, 0), host.stopCount(2));
    try expectEqual(@as(u32, 0), host.stopCount(3));
}

fn branchTop(input: *types.Input) anyerror!bool {
    return input.condition(30);
}
const branch_failure_registry = d.Registry{
    .version = "branch-failure-fixture",
    .process = "test",
    .blocks = &blocks,
    .items = &support.items,
    .conditions = &.{
        support.demand_condition,
        .{ .uid = 30, .node = 2, .per_block = false, .items = &.{.{ .item = 12, .block = null }}, .conditions = &.{}, .children = &.{}, .decides = null, .name = "BranchRequested" },
        .{ .uid = 31, .node = 2, .per_block = false, .items = &.{}, .conditions = &.{30}, .children = &.{}, .decides = 3, .name = "RunBranchChild" },
    },
};
const branch_failure_tree = control.tree.Tree{
    .program = 1,
    .blocks = &.{ .{ .uid = 1, .parent = null }, .{ .uid = 2, .parent = 1 }, .{ .uid = 3, .parent = 2 } },
    .uses = &.{.{ .node = 1, .condition = 20, .child = 2 }},
    .computes = &.{ .{ .condition = 20, .function = support.demanded }, .{ .condition = 30, .function = lower }, .{ .condition = 31, .function = branchTop } },
};
test "layers: persistent compute failure cannot prevent a revoked branch draining" {
    resetCounters();
    const B = control.Controller(branch_failure_registry, branch_failure_tree);
    var host: support.TestHost(B) = .{};
    const controller = try B.create(std.testing.allocator, config(), host.callbacks());
    host.controller = controller;
    controller.activate();
    defer {
        fail_lower.store(false, .release);
        host.cleanup();
        controller.destroy() catch @panic("destroy failed");
    }
    const requested = try controller.claimWrite(.{ .item = 12 });
    try controller.publish(requested, .{ .value = true }, support.reason, null);
    try controller.setDemand("branch", 2, support.reason, null);
    controller.waitIdle();
    try expectEqual(types.BlockState.running, try controller.readState(2));
    try expectEqual(types.BlockState.running, try controller.readState(3));
    fail_lower.store(true, .release);
    try controller.publish(requested, .{ .value = false }, support.reason, null);
    controller.waitIdle();
    try expectEqual(types.BlockState.running, try controller.readState(3));
    try controller.clearDemand("branch", support.reason, null);
    controller.waitIdle();
    try expectEqual(types.BlockState.stopped, try controller.readState(3));
    try expectEqual(types.BlockState.stopped, try controller.readState(2));
    try expectEqual(@as(u32, 1), host.stopCount(3));
    try expectEqual(@as(u32, 1), host.stopCount(2));
    try expectEqual(@as(u32, 1), host.startCount(2));
}

const LogProbe = struct {
    named_block: std.atomic.Value(bool) = .init(false),
    uid_only_block: std.atomic.Value(bool) = .init(false),
    saw_block_name: std.atomic.Value(bool) = .init(false),
    fn capture(self: *LogProbe, text: []const u8) void {
        if (std.mem.indexOf(u8, text, "Business block 2 (Work):") != null) self.named_block.store(true, .release);
        if (std.mem.indexOf(u8, text, "Business block 2:") != null) self.uid_only_block.store(true, .release);
        if (std.mem.indexOf(u8, text, "Work") != null) self.saw_block_name.store(true, .release);
    }
};
fn namedLog(host: *Host, _: u32, text: []const u8) void {
    const probe: *LogProbe = @ptrCast(@alignCast(host.user_context.?));
    probe.capture(text);
}
const AnonymousC = control.Controller(anonymous_registry, tree);
const AnonymousHost = support.TestHost(AnonymousC);
fn anonymousLog(host: *AnonymousHost, _: u32, text: []const u8) void {
    const probe: *LogProbe = @ptrCast(@alignCast(host.user_context.?));
    probe.capture(text);
}
test "snapshot and logs: names are emitted only when supplied by the registry" {
    resetCounters();
    {
        var probe: LogProbe = .{};
        var host: Host = .{ .user_context = &probe, .log_hook = namedLog };
        const controller = try C.create(std.testing.allocator, config(), host.callbacks());
        host.controller = controller;
        controller.activate();
        defer {
            host.cleanup();
            controller.destroy() catch @panic("destroy failed");
        }
        const left = try controller.claimWrite(.{ .item = 12 });
        const right = try controller.claimWrite(.{ .item = 13 });
        try controller.publish(left, .{ .value = true }, support.reason, null);
        try controller.publish(right, .{ .value = true }, support.reason, null);
        controller.waitIdle();
        try expect(probe.named_block.load(.acquire));
        try expect(!probe.uid_only_block.load(.acquire));
        var buffer: [32768]u8 = undefined;
        const size = try controller.snapshot(&buffer);
        try expect(std.mem.indexOf(u8, buffer[0..size], "\"Work\"") != null);
        try expect(std.mem.indexOf(u8, buffer[0..size], "\"RunWork\"") != null);
        try expect(std.mem.indexOf(u8, buffer[0..size], "\"registryVersion\":\"layer-fixture\"") != null);
    }
    {
        var probe: LogProbe = .{};
        var host: AnonymousHost = .{ .user_context = &probe, .log_hook = anonymousLog };
        const controller = try AnonymousC.create(std.testing.allocator, config(), host.callbacks());
        host.controller = controller;
        controller.activate();
        defer {
            host.cleanup();
            controller.destroy() catch @panic("destroy failed");
        }
        const left = try controller.claimWrite(.{ .item = 12 });
        const right = try controller.claimWrite(.{ .item = 13 });
        try controller.publish(left, .{ .value = true }, support.reason, null);
        try controller.publish(right, .{ .value = true }, support.reason, null);
        controller.waitIdle();
        try expect(probe.uid_only_block.load(.acquire));
        try expect(!probe.saw_block_name.load(.acquire));
        var buffer: [32768]u8 = undefined;
        const size = try controller.snapshot(&buffer);
        try expect(std.mem.indexOf(u8, buffer[0..size], "\"name\"") == null);
        try expect(std.mem.indexOf(u8, buffer[0..size], "\"RunWork\"") == null);
        try expect(std.mem.indexOf(u8, buffer[0..size], "\"registryVersion\":\"anonymous-fixture\"") != null);
    }
}

var capture_once = std.atomic.Value(bool).init(false);
var capture_entered = std.atomic.Value(bool).init(false);
var capture_release = std.atomic.Value(bool).init(false);
var capture_first = std.atomic.Value(bool).init(true);
var capture_same = std.atomic.Value(bool).init(false);
var capture_next_true = std.atomic.Value(bool).init(false);
fn repeatedRead(input: *types.Input) anyerror!bool {
    const before = (try input.read(.{ .item = 12 })).valueOr(false);
    if (capture_once.swap(false, .acq_rel)) {
        capture_first.store(before, .release);
        capture_entered.store(true, .release);
        while (!capture_release.load(.acquire)) std.Thread.yield() catch {};
        const after = (try input.read(.{ .item = 12 })).valueOr(false);
        capture_same.store(before == after, .release);
        return after;
    }
    if (before) capture_next_true.store(true, .release);
    return before;
}
const captured_registry = d.Registry{
    .version = "captured-item-fixture",
    .process = "test",
    .blocks = &blocks,
    .items = &support.items,
    .conditions = &.{
        .{ .uid = 20, .node = 1, .per_block = false, .items = &.{.{ .item = 12, .block = null }}, .conditions = &.{}, .children = &.{}, .decides = 2, .name = "RepeatedRead" },
        .{ .uid = 21, .node = 1, .per_block = false, .items = &.{}, .conditions = &.{}, .children = &.{}, .decides = 3, .name = "UnusedPeer" },
    },
};
const captured_tree = control.tree.Tree{
    .program = 1,
    .blocks = tree.blocks,
    .uses = &.{},
    .computes = &.{ .{ .condition = 20, .function = repeatedRead }, .{ .condition = 21, .function = unrelated } },
};
test "layers: repeated item reads use one captured value until the next round" {
    capture_once.store(false, .release);
    capture_entered.store(false, .release);
    capture_release.store(false, .release);
    capture_first.store(true, .release);
    capture_same.store(false, .release);
    capture_next_true.store(false, .release);
    const Captured = control.Controller(captured_registry, captured_tree);
    var host: support.TestHost(Captured) = .{};
    const controller = try Captured.create(std.testing.allocator, config(), host.callbacks());
    host.controller = controller;
    controller.activate();
    defer {
        capture_release.store(true, .release);
        host.cleanup();
        controller.destroy() catch @panic("destroy failed");
    }
    const writer = try controller.claimWrite(.{ .item = 12 });
    try controller.publish(writer, .{ .value = false }, support.reason, null);
    controller.waitIdle();
    capture_once.store(true, .release);
    try controller.publish(writer, .{ .value = false }, support.reason, null);
    try waitFlag(&capture_entered);
    try controller.publish(writer, .{ .value = true }, support.reason, null);
    // Publishing changes authority immediately, while this round retains its basis.
    try expect((try controller.read(.{ .item = 12 })).valueOr(false));
    capture_release.store(true, .release);
    controller.waitIdle();
    try expect(!capture_first.load(.acquire));
    try expect(capture_same.load(.acquire));
    try expect(capture_next_true.load(.acquire));
    try expectEqual(types.BlockState.running, try controller.readState(2));
    try expectEqual(@as(u32, 1), host.startCount(2));
}

test "history: retained demand reason and item basis survive caller mutation and compaction" {
    var host: PerHost = .{};
    const controller = try PerC.create(std.testing.allocator, config(), host.callbacks());
    host.controller = controller;
    controller.activate();
    defer {
        host.cleanup();
        controller.destroy() catch @panic("destroy failed");
    }
    var key = "mutable-key".*;
    var owner = "owner-one".*;
    var cause = "capture-one".*;
    const why: types.Reason = .{ .kind = 2, .owner = &owner, .cause = &cause, .plan_version = 123 };
    try controller.setDemand(&key, 2, why, null);
    @memset(&key, 'k');
    @memset(&owner, 'o');
    @memset(&cause, 'c');
    controller.waitIdle();
    try expectEqual(types.BlockState.running, try controller.readState(2));
    // Retargeting compacts/replaces the demand allocator's original texts.
    try controller.setDemand("mutable-key", 3, support.reason, null);
    controller.waitIdle();
    try controller.clearDemand("mutable-key", support.reason, null);
    controller.waitIdle();
    try expectEqual(types.BlockState.stopped, try controller.readState(2));
    try expectEqual(types.BlockState.stopped, try controller.readState(3));
    const count = controller.decisionCount(1);
    const last = controller.decisionSequence(1);
    var found = false;
    for (0..count) |offset| {
        const sequence = last - count + offset + 1;
        const row = controller.decisions[@intCast((sequence - 1) % controller.config.history_capacity)];
        const retained_reason = row.reason orelse continue;
        if (!std.mem.eql(u8, retained_reason.owner, "owner-one")) continue;
        found = true;
        try expectEqual(@as(u32, 2), retained_reason.kind);
        try std.testing.expectEqualStrings("capture-one", retained_reason.cause);
        try expectEqual(@as(u64, 123), retained_reason.plan_version);
        try expectEqual(types.EventCause.demand, row.source.cause);
        try expectEqual(@as(u32, 10), row.source.uid);
        try expectEqual(@as(?u32, 2), row.source.block);
        var had_demand_basis = false;
        for (row.items) |item| {
            if (item.key.item == 10 and item.key.block == 2) {
                had_demand_basis = item.value.valueOr(false);
            }
        }
        try expect(had_demand_basis);
    }
    try expect(found);
    try expect(!(try controller.read(.{ .item = 10, .block = 2 })).valueOr(true));
}

test "boundary: overlong decision reasons fail before demand or completion mutation" {
    var host: PerHost = .{};
    var cfg = config();
    cfg.reason_text_capacity = 8;
    const controller = try PerC.create(std.testing.allocator, cfg, host.callbacks());
    host.controller = controller;
    controller.activate();
    defer {
        host.cleanup();
        controller.destroy() catch @panic("destroy failed");
    }
    const short: types.Reason = .{ .kind = 1, .owner = "a", .cause = "b", .plan_version = 9 };
    try controller.setDemand("kept", 2, short, null);
    controller.waitIdle();
    const sequence = controller.decisionSequence(1);
    const long: types.Reason = .{ .kind = 1, .owner = "owner-too-long", .cause = "cause", .plan_version = 10 };
    try std.testing.expectError(error.ReasonTooLong, controller.setDemand("kept", 3, long, 55));
    try std.testing.expectError(error.ReasonTooLong, controller.clearDemand("kept", long, 56));
    controller.waitIdle();
    try expectEqual(sequence, controller.decisionSequence(1));
    try expectEqual(types.BlockState.running, try controller.readState(2));
    try expectEqual(types.BlockState.stopped, try controller.readState(3));
    try expect((try controller.read(.{ .item = 10, .block = 2 })).valueOr(false));
    try expect(!(try controller.read(.{ .item = 10, .block = 3 })).valueOr(true));
    try expectEqual(@as(u32, 0), host.complete_calls.load(.acquire));
    var buffer: [32768]u8 = undefined;
    const size = try controller.snapshot(&buffer);
    try expect(std.mem.indexOf(u8, buffer[0..size], "\"key\":\"kept\",\"blockUid\":2,\"kind\":1,\"owner\":\"a\",\"cause\":\"b\",\"planVersion\":9") != null);
    try expect(std.mem.indexOf(u8, buffer[0..size], "owner-too-long") == null);
}

const peer_failure_registry = d.Registry{
    .version = "peer-failure-fixture",
    .process = "test",
    .blocks = &blocks,
    .items = &support.items,
    .conditions = &.{
        .{ .uid = 30, .node = 1, .per_block = false, .items = &.{.{ .item = 13, .block = null }}, .conditions = &.{}, .children = &.{}, .decides = 2, .name = "RunA" },
        .{ .uid = 31, .node = 1, .per_block = false, .items = &.{.{ .item = 12, .block = null }}, .conditions = &.{}, .children = &.{}, .decides = 3, .name = "RunB" },
    },
};
const peer_failure_tree = control.tree.Tree{
    .program = 1,
    .blocks = tree.blocks,
    .uses = &.{},
    .computes = &.{ .{ .condition = 30, .function = support.right }, .{ .condition = 31, .function = lower } },
};
const PeerFailure = control.Controller(peer_failure_registry, peer_failure_tree);
var peer_failure_logged = std.atomic.Value(bool).init(false);
fn peerFailureLog(_: *support.TestHost(PeerFailure), level: u32, text: []const u8) void {
    if (level == 4 and std.mem.indexOf(u8, text, "Condition 31 (RunB): InjectedConditionFailure") != null)
        peer_failure_logged.store(true, .release);
}
test "layers: a failed peer condition does not freeze restart of a still-wanted child" {
    resetCounters();
    peer_failure_logged.store(false, .release);
    var host: support.TestHost(PeerFailure) = .{ .log_hook = peerFailureLog };
    const controller = try PeerFailure.create(std.testing.allocator, config(), host.callbacks());
    host.controller = controller;
    controller.activate();
    defer {
        fail_lower.store(false, .release);
        host.cleanup();
        controller.destroy() catch @panic("destroy failed");
    }
    const a = try controller.claimWrite(.{ .item = 13 });
    const b = try controller.claimWrite(.{ .item = 12 });
    try controller.publish(a, .{ .value = true }, support.reason, null);
    try controller.publish(b, .{ .value = true }, support.reason, null);
    controller.waitIdle();
    try expectEqual(types.BlockState.running, try controller.readState(2));
    try expectEqual(types.BlockState.running, try controller.readState(3));
    const a_run = host.run(2);
    const b_run = host.run(3);
    const sequence = controller.decisionSequence(1);
    fail_lower.store(true, .release);
    try controller.publish(b, .{ .value = false }, support.reason, null);
    controller.waitIdle();
    // Failure itself propagates nothing and does not align or act.
    try expectEqual(sequence, controller.decisionSequence(1));
    try expectEqual(@as(u32, 1), host.startCount(2));
    try expectEqual(@as(u32, 1), host.startCount(3));
    try expect(peer_failure_logged.load(.acquire));
    try host.finish(2);
    controller.waitIdle();
    try expectEqual(types.BlockState.running, try controller.readState(2));
    try expectEqual(a_run + 1, host.run(2));
    try expectEqual(@as(u32, 2), host.startCount(2));
    try expectEqual(types.BlockState.running, try controller.readState(3));
    try expectEqual(b_run, host.run(3));
    try expectEqual(@as(u32, 1), host.startCount(3));
    try expectEqual(@as(u32, 0), host.stopCount(2));
    try expectEqual(@as(u32, 0), host.stopCount(3));
    var buffer: [32768]u8 = undefined;
    const size = try controller.snapshot(&buffer);
    try expect(std.mem.indexOf(u8, buffer[0..size], "InjectedConditionFailure") != null);
}

var child_capture_once = std.atomic.Value(bool).init(false);
var child_capture_entered = std.atomic.Value(bool).init(false);
var child_capture_release = std.atomic.Value(bool).init(false);
var child_capture_first = std.atomic.Value(types.BlockState).init(.stopped);
var child_capture_second = std.atomic.Value(types.BlockState).init(.stopped);
var child_capture_next_running = std.atomic.Value(bool).init(false);
var child_capture_calls = std.atomic.Value(u32).init(0);
fn repeatedChildRead(input: *types.Input) anyerror!bool {
    _ = child_capture_calls.fetchAdd(1, .monotonic);
    const before = try input.childState(2);
    if (child_capture_once.swap(false, .acq_rel)) {
        child_capture_first.store(before, .release);
        child_capture_entered.store(true, .release);
        while (!child_capture_release.load(.acquire)) std.Thread.yield() catch {};
        child_capture_second.store(try input.childState(2), .release);
    } else if (before == .running) child_capture_next_running.store(true, .release);
    return (try input.read(.{ .item = 12 })).valueOr(false);
}
const captured_child_registry = d.Registry{
    .version = "captured-child-fixture",
    .process = "test",
    .blocks = &blocks,
    .items = &support.items,
    .conditions = &.{
        .{ .uid = 20, .node = 1, .per_block = false, .items = &.{.{ .item = 12, .block = null }}, .conditions = &.{}, .children = &.{2}, .decides = 2, .name = "RepeatedChildRead" },
        .{ .uid = 21, .node = 1, .per_block = false, .items = &.{}, .conditions = &.{}, .children = &.{}, .decides = 3, .name = "UnusedPeer" },
    },
};
const captured_child_tree = control.tree.Tree{
    .program = 1,
    .blocks = tree.blocks,
    .uses = &.{},
    .computes = &.{ .{ .condition = 20, .function = repeatedChildRead }, .{ .condition = 21, .function = unrelated } },
};
test "layers: repeated child reads use one captured state and a concurrent report schedules another round" {
    child_capture_once.store(false, .release);
    child_capture_entered.store(false, .release);
    child_capture_release.store(false, .release);
    child_capture_first.store(.stopped, .release);
    child_capture_second.store(.stopped, .release);
    child_capture_next_running.store(false, .release);
    child_capture_calls.store(0, .release);
    const CapturedChild = control.Controller(captured_child_registry, captured_child_tree);
    var host: support.TestHost(CapturedChild) = .{};
    host.auto_running[2].store(false, .release);
    const controller = try CapturedChild.create(std.testing.allocator, config(), host.callbacks());
    host.controller = controller;
    controller.activate();
    defer {
        child_capture_release.store(true, .release);
        host.cleanup();
        controller.destroy() catch @panic("destroy failed");
    }
    const requested = try controller.claimWrite(.{ .item = 12 });
    try controller.publish(requested, .{ .value = true }, support.reason, null);
    controller.waitIdle();
    try expectEqual(types.BlockState.starting, try controller.readState(2));
    const calls = child_capture_calls.load(.acquire);
    child_capture_once.store(true, .release);
    try controller.publish(requested, .{ .value = true }, support.reason, null);
    try waitFlag(&child_capture_entered);
    // The controller accepts the report while the compute is held between reads.
    try controller.reportRunning(2, host.run(2));
    try expectEqual(types.BlockState.running, try controller.readState(2));
    child_capture_release.store(true, .release);
    controller.waitIdle();
    try expectEqual(types.BlockState.starting, child_capture_first.load(.acquire));
    try expectEqual(types.BlockState.starting, child_capture_second.load(.acquire));
    try expect(child_capture_next_running.load(.acquire));
    try expectEqual(calls + 2, child_capture_calls.load(.acquire));
    try expectEqual(@as(u32, 1), host.startCount(2));
    try expectEqual(@as(u32, 0), host.stopCount(2));
}
