const std = @import("std");
const control = @import("rm_control");
pub const d = control.declarations;
pub const t = control.tree;
pub const types = control.types;
pub const reason: types.Reason = .{ .kind = 1, .owner = "test", .cause = "explicit", .plan_version = 7 };

/// Fixtures own their facts directly; there are no lease keys or controller
/// fact aggregation in these tests. Run before activating workers.
pub fn initFacts(controller: anytype) !void {
    for (controller.authority.entries) |*entry| {
        if (!entry.declaration.per_block or entry.declaration.value_type != .bool) continue;
        const claim = try controller.claimCell(entry.key);
        _ = try controller.authority.publish(claim, .{ .value = false });
    }
}
pub fn setFact(controller: anytype, uid: u32, value: bool, why: types.Reason, token: ?u64) !void {
    for (controller.authority.entries) |*entry| {
        if (entry.key.block == uid and entry.declaration.per_block and entry.declaration.value_type == .bool) {
            const claim: control.state.Claim = @ptrCast(entry);
            return controller.publish(claim, .{ .value = value }, why, token);
        }
    }
    return error.UnregisteredItem;
}

pub const blocks = [_]d.Block{
    .{ .uid = 1, .name = "Program" },   .{ .uid = 2, .name = "Parent" },
    .{ .uid = 3, .name = "Child" },     .{ .uid = 4, .name = "Peer" },
    .{ .uid = 5, .name = "PeerChild" }, .{ .uid = 6, .name = "Leaf" },
};
pub const items = [_]d.Item{
    .{ .uid = 10, .storage = .reference, .value_type = .bool, .per_block = true, .trigger = .event, .owner_block = null, .name = "FixtureFact" },
    .{ .uid = 11, .storage = .own, .value_type = .bool, .per_block = false, .trigger = .event, .owner_block = null, .name = "ShuttingDown" },
    .{ .uid = 12, .storage = .own, .value_type = .bool, .per_block = false, .trigger = .event, .owner_block = null, .name = "Requested" },
    .{ .uid = 13, .storage = .own, .value_type = .bool, .per_block = false, .trigger = .event, .owner_block = null, .name = "Right" },
    .{ .uid = 14, .storage = .reference, .value_type = .bool, .per_block = false, .trigger = .event, .owner_block = null, .name = "Mode" },
};
pub const fact_condition = d.Condition{ .uid = 20, .node = null, .per_block = true, .items = &.{.{ .item = 10, .block = null }}, .conditions = &.{}, .children = &.{}, .decides = null, .name = "Fact" };
pub const registry = d.Registry{ .version = "fixture-registry", .process = "test", .blocks = &blocks, .items = &items, .conditions = &.{fact_condition} };
pub const tree = t.Tree{
    .program = 1,
    .blocks = &.{ .{ .uid = 1, .parent = null }, .{ .uid = 2, .parent = 1 }, .{ .uid = 3, .parent = 2 }, .{ .uid = 4, .parent = 1 }, .{ .uid = 5, .parent = 4, .inputs = &.{6} }, .{ .uid = 6, .parent = 3 } },
    .uses = &.{ .{ .node = 1, .condition = 20, .child = 2 }, .{ .node = 1, .condition = 20, .child = 4 }, .{ .node = 2, .condition = 20, .child = 3 }, .{ .node = 3, .condition = 20, .child = 6 }, .{ .node = 4, .condition = 20, .child = 5 } },
    .computes = &.{.{ .condition = 20, .function = fact }},
};
pub fn fact(input: *types.Input) anyerror!bool {
    return (try input.read(.{ .item = 10, .block = input.block })).valueOr(false);
}
pub fn truth(_: *types.Input) anyerror!bool {
    return true;
}
pub fn requested(input: *types.Input) anyerror!bool {
    return (try input.read(.{ .item = 12 })).valueOr(false);
}
pub fn right(input: *types.Input) anyerror!bool {
    return (try input.read(.{ .item = 13 })).valueOr(false);
}

pub fn waitFor(comptime C: type, controller: *C, uid: u32, wanted: types.BlockState) !void {
    var turns: usize = 0;
    while (turns < 5_000_000) : (turns += 1) {
        if (try controller.readState(uid) == wanted) return;
        std.Thread.yield() catch {};
    }
    return error.TestTimeout;
}
pub fn config(history: usize) types.Config {
    return .{ .worker_count = 4, .history_capacity = history, .completion_capacity = 128, .reader_capacity = 16, .reader_bytes = 4096, .worker_arena_bytes = 65536, .shutdown_item = 11, .host_blocks = &.{ 2, 3, 4, 5, 6 } };
}

/// A host that immediately reports transitions, unless a test holds a run.
/// Its callbacks reenter the controller, exercising the no-lock callback boundary.
pub fn TestHost(comptime C: type) type {
    return struct {
        const Self = @This();
        controller: ?*C = null,
        runs: [16]std.atomic.Value(u64) = @as([16]std.atomic.Value(u64), @splat(std.atomic.Value(u64).init(0))),
        starts: [16]std.atomic.Value(u32) = @as([16]std.atomic.Value(u32), @splat(std.atomic.Value(u32).init(0))),
        stops: [16]std.atomic.Value(u32) = @as([16]std.atomic.Value(u32), @splat(std.atomic.Value(u32).init(0))),
        auto_running: [16]std.atomic.Value(bool) = @as([16]std.atomic.Value(bool), @splat(std.atomic.Value(bool).init(true))),
        auto_return: [16]std.atomic.Value(bool) = @as([16]std.atomic.Value(bool), @splat(std.atomic.Value(bool).init(true))),
        completed: std.atomic.Value(u64) = std.atomic.Value(u64).init(0),
        complete_calls: std.atomic.Value(u32) = std.atomic.Value(u32).init(0),
        logs: std.atomic.Value(u32) = std.atomic.Value(u32).init(0),
        item_changed_calls: std.atomic.Value(u32) = std.atomic.Value(u32).init(0),
        item_changed_hook: ?*const fn (*Self, u64, types.ItemKeyView, *const types.ReasonView) void = null,
        callback_failed: std.atomic.Value(bool) = std.atomic.Value(bool).init(false),
        start_hook: ?*const fn (*Self, u32, u64) void = null,
        stop_hook: ?*const fn (*Self, u32, u64) void = null,
        log_hook: ?*const fn (*Self, u32, []const u8) void = null,
        complete_hook: ?*const fn (*Self, u64) void = null,
        user_context: ?*anyopaque = null,

        pub fn callbacks(self: *Self) types.Host {
            return .{ .context = self, .start = onStart, .stop = onStop, .complete = onComplete, .log = onLog, .item_changed = onItemChanged };
        }
        fn ptr(context: ?*anyopaque) *Self {
            return @ptrCast(@alignCast(context.?));
        }
        fn onStart(context: ?*anyopaque, uid: u32, run_number: u64) callconv(.c) void {
            const self = ptr(context);
            self.runs[uid].store(run_number, .release);
            _ = self.starts[uid].fetchAdd(1, .monotonic);
            if (self.start_hook) |hook| hook(self, uid, run_number);
            if (self.auto_running[uid].load(.acquire)) self.controller.?.reportRunning(uid, run_number) catch {
                self.callback_failed.store(true, .release);
            };
        }
        fn onStop(context: ?*anyopaque, uid: u32, run_number: u64) callconv(.c) void {
            const self = ptr(context);
            _ = self.stops[uid].fetchAdd(1, .monotonic);
            if (self.stop_hook) |hook| hook(self, uid, run_number);
            if (self.auto_return[uid].load(.acquire)) self.controller.?.reportReturned(uid, run_number) catch {
                self.callback_failed.store(true, .release);
            };
        }
        fn onComplete(context: ?*anyopaque, token: u64) callconv(.c) void {
            const self = ptr(context);
            self.completed.store(token, .release);
            _ = self.complete_calls.fetchAdd(1, .monotonic);
            if (self.complete_hook) |hook| hook(self, token);
        }
        fn onLog(context: ?*anyopaque, level: u32, text: [*]const u8, length: usize) callconv(.c) void {
            const self = ptr(context);
            _ = self.logs.fetchAdd(1, .monotonic);
            if (self.log_hook) |hook| hook(self, level, text[0..length]);
        }
        fn onItemChanged(context: ?*anyopaque, id: u64, key: types.ItemKeyView, why: *const types.ReasonView) callconv(.c) void {
            const self = ptr(context);
            _ = self.item_changed_calls.fetchAdd(1, .monotonic);
            if (self.item_changed_hook) |hook| hook(self, id, key, why);
        }
        pub fn run(self: *Self, uid: u32) u64 {
            return self.runs[uid].load(.acquire);
        }
        pub fn startCount(self: *Self, uid: u32) u32 {
            return self.starts[uid].load(.acquire);
        }
        pub fn stopCount(self: *Self, uid: u32) u32 {
            return self.stops[uid].load(.acquire);
        }
        pub fn finish(self: *Self, uid: u32) !void {
            try self.controller.?.reportReturned(uid, self.run(uid));
        }
        pub fn cleanup(self: *Self) void {
            for (&self.auto_return) |*value| value.store(true, .release);
            self.controller.?.shutdown(.{ .kind = 1, .owner = "test", .cause = "exit", .plan_version = 0 }, null) catch {};
            // A previously delivered stop callback will not be sent again.
            var uid: u32 = 2;
            while (uid <= 6) : (uid += 1) {
                if ((self.controller.?.readState(uid) catch .stopped) == .stopping) self.finish(uid) catch {};
            }
            self.controller.?.waitIdle();
        }
    };
}

/// Fixed declarations for a broad sibling set. UIDs are fixture-local.
pub fn Flat(comptime count: usize) type {
    return struct {
        pub const all = blk: {
            var bs: [count + 1]d.Block = undefined;
            var ts: [count + 1]t.Block = undefined;
            var us: [count]t.PerBlockUse = undefined;
            var hs: [count]u32 = undefined;
            for (0..count + 1) |index| {
                bs[index] = .{ .uid = @intCast(index + 1), .name = null };
                ts[index] = .{ .uid = @intCast(index + 1), .parent = if (index == 0) null else 1 };
                if (index > 0) {
                    us[index - 1] = .{ .node = 1, .condition = 200, .child = @intCast(index + 1) };
                    hs[index - 1] = @intCast(index + 1);
                }
            }
            break :blk .{ .blocks = bs, .tree_blocks = ts, .uses = us, .host_blocks = hs };
        };
        pub const flat_blocks = all.blocks;
        pub const flat_tree_blocks = all.tree_blocks;
        pub const flat_uses = all.uses;
        pub const flat_host_blocks = all.host_blocks;
        pub const flat_items = [_]d.Item{
            .{ .uid = 100, .storage = .reference, .value_type = .bool, .per_block = true, .trigger = .event, .owner_block = null, .name = null },
            .{ .uid = 101, .storage = .own, .value_type = .bool, .per_block = false, .trigger = .event, .owner_block = null, .name = null },
        };
        pub const flat_condition = d.Condition{ .uid = 200, .node = null, .per_block = true, .items = &.{.{ .item = 100, .block = null }}, .conditions = &.{}, .children = &.{}, .decides = null, .name = null };
        pub fn flatFact(input: *types.Input) anyerror!bool {
            return (try input.read(.{ .item = 100, .block = input.block })).valueOr(false);
        }
        pub const registry = d.Registry{ .version = "flat-fixture", .process = "test", .blocks = &flat_blocks, .items = &flat_items, .conditions = &.{flat_condition} };
        pub const tree = t.Tree{ .program = 1, .blocks = &flat_tree_blocks, .uses = &flat_uses, .computes = &.{.{ .condition = 200, .function = flatFact }} };
    };
}
