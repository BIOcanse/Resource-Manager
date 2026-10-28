const std = @import("std");
const control = @import("rm_control");
const support = @import("support.zig");

const Engine = control.Controller(support.registry, support.tree);
const Host = support.TestHost(Engine);

fn snapshot(controller: *Engine, buffer: []u8) !std.json.Parsed(std.json.Value) {
    const size = try controller.snapshot(buffer);
    return std.json.parseFromSlice(std.json.Value, std.testing.allocator, buffer[0..size], .{});
}

fn decisionFor(root: std.json.Value, uid: i64) !std.json.Value {
    const rows = root.object.get("currentDecisions") orelse return error.MissingCurrentDecisions;
    for (rows.array.items) |row| {
        if (row.object.get("node").?.integer == uid) return row;
    }
    return error.MissingCurrentDecision;
}

fn itemFor(decision: std.json.Value, item_uid: i64, block_uid: i64) !std.json.Value {
    const rows = decision.object.get("items").?.array.items;
    for (rows) |row| {
        if (row.object.get("uid").?.integer == item_uid and row.object.get("blockUid").?.integer == block_uid) return row;
    }
    return error.MissingDecisionItem;
}

fn expectEmptyHistory(root: std.json.Value) !void {
    try std.testing.expectEqual(@as(usize, 0), root.object.get("decisions").?.array.items.len);
    try std.testing.expectEqual(@as(usize, 0), root.object.get("transitions").?.array.items.len);
}

test "current decisions remain queryable when history retention is disabled" {
    var host: Host = .{};
    const controller = try Engine.create(std.testing.allocator, support.config(0), host.callbacks());
    host.controller = controller;
    try support.initFacts(controller);
    controller.activate();
    defer {
        host.cleanup();
        controller.destroy() catch @panic("controller did not destroy after current-decision test");
    }
    controller.waitIdle();

    try support.setFact(controller, 2, true, support.reason, null);
    controller.waitIdle();
    try support.waitFor(Engine, controller, 2, .running);

    var buffer: [32768]u8 = undefined;
    var parsed = try snapshot(controller, &buffer);
    defer parsed.deinit();
    try expectEmptyHistory(parsed.value);
    const wanted = try decisionFor(parsed.value, 1);
    const wanted_children = wanted.object.get("shouldRun").?.array.items;
    try std.testing.expectEqual(@as(usize, 1), wanted_children.len);
    try std.testing.expectEqual(@as(i64, 2), wanted_children[0].integer);
    const wanted_fact = try itemFor(wanted, 10, 2);
    try std.testing.expect(wanted_fact.object.get("value").?.bool);

    try support.setFact(controller, 2, false, support.reason, null);
    controller.waitIdle();
    try support.waitFor(Engine, controller, 2, .stopped);

    parsed.deinit();
    parsed = try snapshot(controller, &buffer);
    try expectEmptyHistory(parsed.value);
    const stopped = try decisionFor(parsed.value, 1);
    try std.testing.expectEqual(@as(usize, 0), stopped.object.get("shouldRun").?.array.items.len);
    const stopped_fact = try itemFor(stopped, 10, 2);
    try std.testing.expect(!stopped_fact.object.get("value").?.bool);
}
