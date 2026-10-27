const std = @import("std");
const control = @import("rm_control");
const s = @import("support.zig");
const C = control.Controller(s.registry, s.tree);
const H = s.TestHost(C);
const expect = std.testing.expect;
const eq = std.testing.expectEqual;

fn make(host: *H, history: usize) !*C {
    const c = try C.create(std.testing.allocator, s.config(history), host.callbacks());
    host.controller = c;
    c.activate();
    return c;
}
fn close(host: *H, c: *C) void {
    host.cleanup();
    c.destroy() catch @panic("controller did not destroy after all runs returned");
}
fn demand(c: *C, key: []const u8, uid: u32) !void {
    try c.setDemand(key, uid, s.reason, null);
    c.waitIdle();
}
fn withdraw(c: *C, key: []const u8) !void {
    try c.clearDemand(key, s.reason, null);
    c.waitIdle();
}
fn state(c: *C, uid: u32, wanted: s.types.BlockState) !void {
    try s.waitFor(C, c, uid, wanted);
    c.waitIdle();
}

// C#: FourStatesStopCannotBeRefusedAndReadsArePure (both cases),
// WithdrawalWhileStartingImmediatelySendsStopAndWaitsForTheEntryToReturn.
test "four states and late Running never undo a dispatched stop" {
    for ([_]bool{ false, true }) |stop_while_starting| {
        var host: H = .{};
        host.auto_running[2].store(false, .release);
        host.auto_return[2].store(false, .release);
        const c = try make(&host, 128);
        defer close(&host, c);
        try demand(c, "worker", 2);
        try state(c, 2, .starting);
        const count = c.transitionCount();
        try eq(s.types.BlockState.starting, try c.readState(2));
        try eq(count, c.transitionCount());
        try eq(@as(u32, 1), host.startCount(2));
        if (!stop_while_starting) {
            try c.reportRunning(2, host.run(2));
            try state(c, 2, .running);
        }
        try withdraw(c, "worker");
        try state(c, 2, .stopping);
        try eq(@as(u32, 1), host.stopCount(2));
        try c.reportRunning(2, host.run(2));
        try eq(s.types.BlockState.stopping, try c.readState(2));
        try host.finish(2);
        try state(c, 2, .stopped);
        try eq(@as(usize, if (stop_while_starting) 3 else 4), c.transitionCount());
    }
}

// C#: ReadsDoNotConstructOrRunBlocksAndUnknownIdsFail.
test "reads are pure and unknown block and item identities fail" {
    var host: H = .{};
    const c = try make(&host, 128);
    defer close(&host, c);
    c.waitIdle();
    const transitions = c.transitionCount();
    const decisions = c.decisionSequence(1);
    try eq(s.types.BlockState.stopped, try c.readState(2));
    try eq(transitions, c.transitionCount());
    try eq(decisions, c.decisionSequence(1));
    try eq(@as(u32, 0), host.startCount(2));
    if (c.readState(999)) |_| return error.UnknownBlockWasAccepted else |_| {}
    if (c.setDemand("unknown", 999, s.reason, null)) |_| return error.UnknownDemandWasAccepted else |_| {}
    if (c.read(.{ .item = 999 })) |_| return error.UnknownItemWasAccepted else |_| {}
    if (c.read(.{ .item = 10, .block = 999 })) |_| return error.UnknownPerBlockWasAccepted else |_| {}
}

// C#: ReturningEntryTriggersItsParentToRestartWhenStillDemandedWithoutFailingAnotherBlock;
// EntryReturnIsStoppedRegardlessOfItsOutcome. The host owns outcome classification.
test "an early return restarts a demanded run and leaves its peer alone" {
    var host: H = .{};
    const c = try make(&host, 128);
    defer close(&host, c);
    try demand(c, "worker", 2);
    try demand(c, "peer", 4);
    try state(c, 2, .running);
    try state(c, 4, .running);
    const old_run = host.run(2);
    try c.reportReturned(2, old_run);
    c.waitIdle();
    try state(c, 2, .running);
    try eq(@as(u32, 2), host.startCount(2));
    try expect(host.run(2) != old_run);
    try eq(@as(u32, 1), host.startCount(4));
    try eq(@as(u32, 0), host.stopCount(4));
    try c.reportRunning(2, old_run);
    try c.reportReturned(2, old_run);
    try eq(s.types.BlockState.running, try c.readState(2));
    try eq(@as(u32, 2), host.startCount(2));
}

// C#: MultipleExternalNeedsRemainFactsUntilTheLastLeaseIsReleased;
// RootAcceptsAllThreeDemandKindsAndLastWithdrawalStopsTheBlock.
test "all demand kinds retain reasons and only the last withdrawal stops a block" {
    for (0..3) |kind| {
        var host: H = .{};
        const c = try make(&host, 128);
        defer close(&host, c);
        const why = s.types.Reason{ .kind = @intCast(kind), .owner = "external-owner", .cause = "needs-output", .plan_version = 42 };
        try c.setDemand("first", 2, why, null);
        try c.setDemand("second", 2, why, null);
        c.waitIdle();
        try state(c, 2, .running);
        try withdraw(c, "first");
        var buffer: [32768]u8 = undefined;
        const used = try c.snapshot(&buffer);
        try expect(used <= buffer.len);
        const json = buffer[0..used];
        try expect(std.mem.indexOf(u8, json, "\"owner\":\"external-owner\"") != null);
        try expect(std.mem.indexOf(u8, json, "\"cause\":\"needs-output\"") != null);
        try expect(std.mem.indexOf(u8, json, "\"planVersion\":42") != null);
        try eq(s.types.BlockState.running, try c.readState(2));
        try eq(@as(u32, 1), host.startCount(2));
        try expect((try c.read(.{ .item = 10, .block = 2 })).valueOr(false));
        try withdraw(c, "second");
        try state(c, 2, .stopped);
        try expect(!(try c.read(.{ .item = 10, .block = 2 })).valueOr(true));
    }
}

// C#: ParentMustReportRunningBeforeItsChildCanStart.
test "a parent must report Running before a demanded descendant starts" {
    var host: H = .{};
    host.auto_running[2].store(false, .release);
    const c = try make(&host, 128);
    defer close(&host, c);
    try demand(c, "child", 3);
    try eq(@as(u32, 0), host.startCount(2));
    try eq(@as(u32, 0), host.startCount(3));
    try demand(c, "parent", 2);
    try state(c, 2, .starting);
    try eq(@as(u32, 0), host.startCount(3));
    try c.reportRunning(2, host.run(2));
    try state(c, 3, .running);
}

// C#: ParentReceivesStopOnlyAfterEveryDescendantHasReturned;
// StopDecisionDoesNotWaitForChildDispatchAndWaitsForItsWholeBranchToDrain.
test "a parent stop waits for the whole branch to return" {
    var host: H = .{};
    host.auto_return[6].store(false, .release);
    const c = try make(&host, 128);
    defer close(&host, c);
    try demand(c, "leaf", 6);
    try demand(c, "child", 3);
    try demand(c, "parent", 2);
    try state(c, 6, .running);
    try withdraw(c, "parent");
    try state(c, 6, .stopping);
    try eq(@as(u32, 0), host.stopCount(3));
    try eq(@as(u32, 0), host.stopCount(2));
    try host.finish(6);
    try state(c, 2, .stopped);
    try eq(@as(u32, 1), host.stopCount(3));
    try eq(@as(u32, 1), host.stopCount(2));
}
// C#: ReturnedChildDoesNotHideItsStillDrainingDescendants;
// RestartWaitsForRevocationAcknowledgementWhileAnOldDecisionAndReturnCallbackAreHeld.
test "an early returned branch cannot restart until its descendants acknowledge revocation" {
    var host: H = .{};
    host.auto_return[6].store(false, .release);
    const c = try make(&host, 128);
    defer close(&host, c);
    try demand(c, "leaf", 6);
    try demand(c, "child", 3);
    try demand(c, "parent", 2);
    try state(c, 6, .running);
    try host.finish(3);
    try state(c, 6, .stopping);
    try eq(s.types.BlockState.stopped, try c.readState(3));
    try eq(@as(u32, 1), host.startCount(3));
    try withdraw(c, "parent");
    try eq(@as(u32, 0), host.stopCount(2));
    try demand(c, "parent", 2);
    try eq(@as(u32, 1), host.startCount(2));
    try eq(@as(u32, 1), host.startCount(3));
    try host.finish(6);
    try state(c, 6, .running);
    try eq(@as(u32, 2), host.startCount(3));
    try eq(@as(u32, 2), host.startCount(6));
}

// C#: SlowBranchDrainDoesNotDelaySiblingRevocationOrStop;
// SiblingsDoNotWaitForAnotherSiblingsPreparationOrCleanup.
test "a slow branch never delays an independent sibling's preparation or stop" {
    var host: H = .{};
    host.auto_running[2].store(false, .release);
    host.auto_return[5].store(false, .release);
    const c = try make(&host, 128);
    defer close(&host, c);
    try demand(c, "left", 2);
    try demand(c, "right", 4);
    try demand(c, "right-child", 5);
    try state(c, 2, .starting);
    try state(c, 5, .running);
    try c.reportRunning(2, host.run(2));
    try state(c, 2, .running);
    try c.shutdown(s.reason, null);
    c.waitIdle();
    try state(c, 5, .stopping);
    try state(c, 2, .stopped);
    try eq(s.types.BlockState.running, try c.readState(4));
    try demand(c, "late-left-child", 3);
    try eq(@as(u32, 0), host.startCount(3));
    try host.finish(5);
    try state(c, 4, .stopped);
}

// C#: CrossBranchDataInputNeverStartsItsProducerOrParent;
// GraphCopiesDeclarationsAndAllowsCrossBranchDataCycles (inputs are comptime immutable).
test "data inputs describe the snapshot but never create control demand" {
    var host: H = .{};
    const c = try make(&host, 128);
    defer close(&host, c);
    try demand(c, "consumer", 5);
    try demand(c, "consumer-parent", 4);
    try state(c, 5, .running);
    try eq(s.types.BlockState.stopped, try c.readState(2));
    try eq(s.types.BlockState.stopped, try c.readState(3));
    try eq(s.types.BlockState.stopped, try c.readState(6));
    try eq(@as(u32, 0), host.startCount(6));
    try eq(@as(u32, 6), s.tree.blocks[4].inputs[0]);
}

// C#: ConcurrentChildReturnAndDemandWithdrawalAlwaysDrainBeforeTheNextParentRun.
test "repeated child returns and demand withdrawals drain before the next parent run" {
    var host: H = .{};
    const c = try make(&host, 128);
    defer close(&host, c);
    for (0..48) |_| {
        try demand(c, "child", 3);
        try demand(c, "parent", 2);
        try state(c, 3, .running);
        const run = host.run(3);
        const Op = struct {
            fn clear(controller: *C, key: []const u8) void {
                controller.clearDemand(key, s.reason, null) catch @panic("clear failed");
            }
            fn returned(controller: *C, number: u64) void {
                controller.reportReturned(3, number) catch @panic("return failed");
            }
        };
        const a = try std.Thread.spawn(.{}, Op.clear, .{ c, "parent" });
        const b = try std.Thread.spawn(.{}, Op.clear, .{ c, "child" });
        const z = try std.Thread.spawn(.{}, Op.returned, .{ c, run });
        a.join();
        b.join();
        z.join();
        c.waitIdle();
        try state(c, 2, .stopped);
        try state(c, 3, .stopped);
    }
    try eq(@as(u32, 48), host.startCount(2));
}

// C#: ConcurrentRetargetsOfOneDemandLeaveOnlyTheFinalTargetsFactAndExecutionActive.
test "concurrent retargets leave only the final target's demand and execution" {
    var host: H = .{};
    const c = try make(&host, 128);
    defer close(&host, c);
    const Op = struct {
        fn set(controller: *C, target: u32) void {
            controller.setDemand("one-owner", target, s.reason, null) catch @panic("retarget failed");
        }
    };
    for (0..32) |_| {
        var threads: [12]std.Thread = undefined;
        for (&threads, 0..) |*thread, index| thread.* = try std.Thread.spawn(.{}, Op.set, .{ c, @as(u32, if (index % 2 == 0) 2 else 4) });
        for (threads) |thread| thread.join();
        c.waitIdle();
        const left = (try c.read(.{ .item = 10, .block = 2 })).valueOr(false);
        const right = (try c.read(.{ .item = 10, .block = 4 })).valueOr(false);
        try expect(left != right);
        try state(c, 2, if (left) .running else .stopped);
        try state(c, 4, if (right) .running else .stopped);
    }
    try withdraw(c, "one-owner");
    try state(c, 2, .stopped);
    try state(c, 4, .stopped);
}

// C#: ShutdownKeepsEveryDecisionEmptyForeverWhileLaterDemandFactsRemainRecorded.
test "shutdown dominates later demand and remains an owned state item" {
    var host: H = .{};
    const c = try make(&host, 128);
    defer close(&host, c);
    try demand(c, "worker", 2);
    try state(c, 2, .running);
    try c.shutdown(s.reason, null);
    c.waitIdle();
    try state(c, 2, .stopped);
    const runs = host.startCount(2);
    try demand(c, "late", 2);
    try c.shutdown(s.reason, null);
    c.waitIdle();
    try expect((try c.read(.{ .item = 11 })).valueOr(false));
    try expect((try c.read(.{ .item = 10, .block = 2 })).valueOr(false));
    try eq(runs, host.startCount(2));
    try eq(s.types.BlockState.stopped, try c.readState(2));
    if (c.claimWrite(.{ .item = 11 })) |_| return error.ShutdownOwnerWasDuplicated else |_| {}
    if (c.claimCell(.{ .item = 10, .block = 2 })) |_| return error.DemandOwnerWasDuplicated else |_| {}
}

// C#: ControlHistoryIsBoundedAndZeroRetentionStillLogsTransitions.
test "fixed transition rings retain only their configured capacity and zero still logs" {
    for ([_]usize{ 0, 3 }) |capacity| {
        var host: H = .{};
        const c = try make(&host, capacity);
        defer close(&host, c);
        for (0..4) |_| {
            try demand(c, "worker", 2);
            try state(c, 2, .running);
            try withdraw(c, "worker");
            try state(c, 2, .stopped);
        }
        try eq(capacity, c.transitionCount());
        try expect(host.logs.load(.acquire) >= 16);
        try eq(@as(u32, 4), host.startCount(2));
        try eq(@as(u32, 4), host.stopCount(2));
    }
}

// Seeded random events run against real host entry threads; host callbacks only
// create entries or set their stop flag, as the cross-language boundary requires.
test "seeded threaded demand and early-return stress respects the tree and shutdown" {
    const Entry = struct {
        controller: *C,
        uid: u32,
        run: u64,
        stop: std.atomic.Value(bool),
        early: std.atomic.Value(bool),
        fn execute(entry: *@This()) void {
            entry.controller.reportRunning(entry.uid, entry.run) catch @panic("running report failed");
            while (!entry.stop.load(.acquire) and !entry.early.load(.acquire)) std.Thread.yield() catch {};
            entry.controller.reportReturned(entry.uid, entry.run) catch @panic("returned report failed");
            // A delayed report for a completed run cannot resurrect it.
            entry.controller.reportRunning(entry.uid, entry.run) catch @panic("late report failed");
        }
    };
    const Threaded = struct {
        entries: [2048]Entry = undefined,
        threads: [2048]std.Thread = undefined,
        count: std.atomic.Value(usize) = std.atomic.Value(usize).init(0),
        locked: std.atomic.Value(bool) = std.atomic.Value(bool).init(false),
        parent_violation: std.atomic.Value(bool) = std.atomic.Value(bool).init(false),
        fn from(host: *H) *@This() {
            return @ptrCast(@alignCast(host.user_context.?));
        }
        fn lock(self: *@This()) void {
            while (self.locked.cmpxchgWeak(false, true, .acquire, .monotonic) != null) std.Thread.yield() catch {};
        }
        fn unlock(self: *@This()) void {
            self.locked.store(false, .release);
        }
        fn start(host: *H, uid: u32, run: u64) void {
            const self = from(host);
            const parent: u32 = switch (uid) {
                2, 4 => 1,
                3 => 2,
                5 => 4,
                6 => 3,
                else => unreachable,
            };
            if (parent != 1 and (host.controller.?.readState(parent) catch .stopped) != .running) self.parent_violation.store(true, .release);
            self.lock();
            const index = self.count.load(.acquire);
            if (index >= self.entries.len) @panic("stress host entry capacity exceeded");
            self.entries[index] = .{ .controller = host.controller.?, .uid = uid, .run = run, .stop = std.atomic.Value(bool).init(false), .early = std.atomic.Value(bool).init(false) };
            self.count.store(index + 1, .release);
            self.unlock();
            self.threads[index] = std.Thread.spawn(.{}, Entry.execute, .{&self.entries[index]}) catch @panic("entry thread could not start");
        }
        fn stop(host: *H, uid: u32, run: u64) void {
            const self = from(host);
            const count = self.count.load(.acquire);
            for (self.entries[0..count]) |*entry| if (entry.uid == uid and entry.run == run) {
                entry.stop.store(true, .release);
                return;
            };
            @panic("stop arrived without its host entry");
        }
        fn early(self: *@This(), uid: u32, run: u64) void {
            const count = self.count.load(.acquire);
            for (self.entries[0..count]) |*entry| if (entry.uid == uid and entry.run == run) {
                entry.early.store(true, .release);
                return;
            };
        }
        fn join(self: *@This()) void {
            for (self.threads[0..self.count.load(.acquire)]) |thread| thread.join();
        }
    };
    var threaded: Threaded = .{};
    var host: H = .{ .user_context = &threaded, .start_hook = Threaded.start, .stop_hook = Threaded.stop };
    for (&host.auto_running) |*flag| flag.store(false, .release);
    for (&host.auto_return) |*flag| flag.store(false, .release);
    const c = try make(&host, 128);
    defer {
        c.shutdown(s.reason, null) catch {};
        for (threaded.entries[0..threaded.count.load(.acquire)]) |*entry| entry.stop.store(true, .release);
        threaded.join();
        c.waitIdle();
        c.destroy() catch @panic("threaded controller did not destroy");
    }
    const writer = try c.claimWrite(.{ .item = 12 });
    var random = std.Random.DefaultPrng.init(0x726d_636f_6e74_726f);
    const rng = random.random();
    const keys = [_][]const u8{ "parent", "child", "peer", "peer-child", "leaf" };
    const ids = [_]u32{ 2, 3, 4, 5, 6 };
    for (0..512) |_| {
        const index = rng.uintLessThan(usize, ids.len);
        switch (rng.uintLessThan(u8, 5)) {
            0 => try c.setDemand(keys[index], ids[index], s.reason, null),
            1 => try c.clearDemand(keys[index], s.reason, null),
            2 => try c.publish(writer, .{ .value = rng.boolean() }, s.reason, null),
            3 => {
                const uid: u32 = if (rng.boolean()) 5 else 6;
                if ((try c.readState(uid)) == .running) threaded.early(uid, host.run(uid));
            },
            4 => {
                const run = host.run(ids[index]);
                if (run > 0) try c.reportRunning(ids[index], run - 1);
            },
            else => unreachable,
        }
        c.waitIdle();
        try expect(!threaded.parent_violation.load(.acquire));
        try expect(!host.callback_failed.load(.acquire));
    }
    try c.shutdown(s.reason, null);
    for (ids) |uid| try state(c, uid, .stopped);
    try expect(!threaded.parent_violation.load(.acquire));
}

// C#: ReportsControllerAndDefaultHistoryMemoryCost. Creation uses a 2 MiB
// testing-allocator-backed slab; all 128 retained decision rounds use no new
// backing memory, below the original 512 KiB retained-history ceiling.
test "forty block declarations and full default history stay within the C sharp memory ceilings" {
    const fixture = s.Flat(40);
    const Many = control.Controller(fixture.registry, fixture.tree);
    var host: s.TestHost(Many) = .{};
    const storage = try std.testing.allocator.alloc(u8, 2 * 1024 * 1024);
    defer std.testing.allocator.free(storage);
    var bounded = std.heap.FixedBufferAllocator.init(storage);
    var options = s.config(128);
    options.host_blocks = &fixture.flat_host_blocks;
    options.demand_item = 100;
    options.shutdown_item = 101;
    const c = try Many.create(bounded.allocator(), options, host.callbacks());
    host.controller = c;
    c.activate();
    defer {
        host.cleanup();
        c.destroy() catch @panic("memory fixture did not destroy");
    }
    c.waitIdle();
    const constructed = bounded.end_index;
    try expect(constructed <= 2 * 1024 * 1024);
    for (0..128) |index| {
        if (index % 2 == 0) try c.setDemand("memory-probe", 2, s.reason, null) else try c.clearDemand("memory-probe", s.reason, null);
        c.waitIdle();
    }
    const history_retained = bounded.end_index - constructed;
    try expect(history_retained <= 512 * 1024);
    const fixed_history_bytes = c.transitions.len * @sizeOf(Many.Transition) + c.decisions.len * @sizeOf(Many.Decision) + c.history_items.len * @sizeOf(Many.ItemValue) + c.history_reason_bytes.len;
    try expect(fixed_history_bytes <= 512 * 1024);
    try eq(@as(usize, 128), c.decisionCount(1));
    try eq(constructed, bounded.end_index);
}

const Gate = struct {
    entered: std.atomic.Value(bool) = std.atomic.Value(bool).init(false),
    release: std.atomic.Value(bool) = std.atomic.Value(bool).init(false),
    fn awaitEntered(self: *Gate) !void {
        var turns: usize = 0;
        while (!self.entered.load(.acquire) and turns < 5_000_000) : (turns += 1) std.Thread.yield() catch {};
        try expect(self.entered.load(.acquire));
    }
    fn hold(self: *Gate) void {
        self.entered.store(true, .release);
        while (!self.release.load(.acquire)) std.Thread.yield() catch {};
    }
};
var compute_gate: ?*Gate = null;
fn blockedRequested(input: *s.types.Input) anyerror!bool {
    const requested = (try input.read(.{ .item = 12 })).valueOr(false);
    if (requested) if (compute_gate) |gate| gate.hold();
    return requested;
}
const isolated_registry = s.d.Registry{ .version = "isolated-fixture", .process = "test", .blocks = &s.blocks, .items = &s.items, .conditions = &.{
    s.demand_condition,
    .{ .uid = 21, .node = 2, .per_block = false, .items = &.{.{ .item = 12, .block = null }}, .conditions = &.{}, .children = &.{}, .decides = 3, .name = "LeftRequested" },
    .{ .uid = 22, .node = 4, .per_block = false, .items = &.{.{ .item = 13, .block = null }}, .conditions = &.{}, .children = &.{}, .decides = 5, .name = "RightRequested" },
} };
const isolated_tree = s.t.Tree{ .program = 1, .blocks = s.tree.blocks, .uses = &.{ .{ .node = 1, .condition = 20, .child = 2 }, .{ .node = 1, .condition = 20, .child = 4 }, .{ .node = 3, .condition = 20, .child = 6 } }, .computes = &.{ .{ .condition = 20, .function = s.demanded }, .{ .condition = 21, .function = blockedRequested }, .{ .condition = 22, .function = s.right } } };

// C#: BlockedDecisionInOneBranchDoesNotDelayAnotherBranch.
test "a held compute in one branch leaves another branch free to decide" {
    const Isolated = control.Controller(isolated_registry, isolated_tree);
    var host: s.TestHost(Isolated) = .{};
    var gate: Gate = .{};
    compute_gate = &gate;
    defer compute_gate = null;
    const c = try Isolated.create(std.testing.allocator, s.config(128), host.callbacks());
    host.controller = c;
    c.activate();
    defer {
        gate.release.store(true, .release);
        host.cleanup();
        c.destroy() catch @panic("isolated controller did not destroy");
    }
    const left = try c.claimWrite(.{ .item = 12 });
    const right = try c.claimWrite(.{ .item = 13 });
    try c.setDemand("left-parent", 2, s.reason, null);
    try c.setDemand("right-parent", 4, s.reason, null);
    c.waitIdle();
    try c.publish(left, .{ .value = true }, s.reason, null);
    try gate.awaitEntered();
    try c.publish(right, .{ .value = true }, s.reason, null);
    try s.waitFor(Isolated, c, 5, .running);
    try expect(!gate.release.load(.acquire));
    try eq(s.types.BlockState.running, try c.readState(5));
    gate.release.store(true, .release);
    c.waitIdle();
    try s.waitFor(Isolated, c, 3, .running);
}

// C#: BlockedControlLogInOneBranchDoesNotBlockAnotherBlocksStateOrStart;
// StopDecisionDoesNotWaitForChildDispatch... . A deliberately held host callback
// is the same boundary: no controller lock may be held across it.
test "a held callback in one branch does not hold the controller lock or another node" {
    const Hook = struct {
        fn start(host: *H, uid: u32, _: u64) void {
            if (uid != 3) return;
            const gate: *Gate = @ptrCast(@alignCast(host.user_context.?));
            gate.hold();
        }
    };
    var gate: Gate = .{};
    var host: H = .{ .user_context = &gate, .start_hook = Hook.start };
    const c = try make(&host, 128);
    defer {
        gate.release.store(true, .release);
        close(&host, c);
    }
    try demand(c, "left", 2);
    try demand(c, "right", 4);
    try c.setDemand("left-child", 3, s.reason, null);
    try gate.awaitEntered();
    try c.setDemand("right-child", 5, s.reason, null);
    try s.waitFor(C, c, 5, .running);
    try eq(s.types.BlockState.starting, try c.readState(3));
    gate.release.store(true, .release);
    c.waitIdle();
    try state(c, 3, .running);
}

// C#: ParentReturnDuringDecisionConvergesToAnEntirelyStoppedBranch.
test "a parent returning during its condition round converges to a stopped branch" {
    const Isolated = control.Controller(isolated_registry, isolated_tree);
    var host: s.TestHost(Isolated) = .{};
    var gate: Gate = .{};
    compute_gate = &gate;
    defer compute_gate = null;
    const c = try Isolated.create(std.testing.allocator, s.config(128), host.callbacks());
    host.controller = c;
    c.activate();
    defer {
        gate.release.store(true, .release);
        host.cleanup();
        c.destroy() catch @panic("return-during-compute controller did not destroy");
    }
    const left = try c.claimWrite(.{ .item = 12 });
    try c.setDemand("parent", 2, s.reason, null);
    c.waitIdle();
    try c.publish(left, .{ .value = true }, s.reason, null);
    try gate.awaitEntered();
    try c.clearDemand("parent", s.reason, null);
    try host.finish(2);
    gate.release.store(true, .release);
    c.waitIdle();
    try s.waitFor(Isolated, c, 2, .stopped);
    try s.waitFor(Isolated, c, 3, .stopped);
    try eq(host.startCount(3), host.stopCount(3));
}

fn heldDemand(input: *s.types.Input) anyerror!bool {
    if ((try input.read(.{ .item = 12 })).valueOr(false)) if (compute_gate) |gate| gate.hold();
    return (try input.read(.{ .item = 10, .block = 3 })).valueOr(false);
}
const held_registry = s.d.Registry{ .version = "held-demand-fixture", .process = "test", .blocks = &s.blocks, .items = &s.items, .conditions = &.{
    s.demand_condition,
    .{ .uid = 21, .node = 2, .per_block = false, .items = &.{ .{ .item = 10, .block = 3 }, .{ .item = 12, .block = null } }, .conditions = &.{}, .children = &.{}, .decides = 3, .name = null },
} };
const held_tree = s.t.Tree{ .program = 1, .blocks = s.tree.blocks, .uses = &.{ .{ .node = 1, .condition = 20, .child = 2 }, .{ .node = 1, .condition = 20, .child = 4 }, .{ .node = 3, .condition = 20, .child = 6 }, .{ .node = 4, .condition = 20, .child = 5 } }, .computes = &.{ .{ .condition = 20, .function = s.demanded }, .{ .condition = 21, .function = heldDemand } } };

// C#: WithdrawalAndReacquireBeforeDecisionUseCurrentFactsWithoutRestart.
test "withdrawal and reacquire before a condition read use current state without restart" {
    const Held = control.Controller(held_registry, held_tree);
    var host: s.TestHost(Held) = .{};
    var gate: Gate = .{};
    compute_gate = &gate;
    defer compute_gate = null;
    const c = try Held.create(std.testing.allocator, s.config(128), host.callbacks());
    host.controller = c;
    c.activate();
    defer {
        gate.release.store(true, .release);
        host.cleanup();
        c.destroy() catch @panic("held demand controller did not destroy");
    }
    const pulse = try c.claimWrite(.{ .item = 12 });
    try c.setDemand("parent", 2, s.reason, null);
    try c.setDemand("child", 3, s.reason, null);
    c.waitIdle();
    try s.waitFor(Held, c, 3, .running);
    try c.publish(pulse, .{ .value = true }, s.reason, null);
    try gate.awaitEntered();
    try c.clearDemand("child", s.reason, null);
    try c.setDemand("child", 3, s.reason, null);
    gate.release.store(true, .release);
    c.waitIdle();
    try eq(@as(u32, 1), host.startCount(3));
    try eq(@as(u32, 0), host.stopCount(3));
    try eq(s.types.BlockState.running, try c.readState(3));
}

// C#: ConcurrentTransitionsRetainTheNewestBoundedHistory. Starts and returns
// are generated by distinct host threads; the ring must retain the newest four.
test "concurrent transitions retain the newest bounded history" {
    const flat = s.Flat(32);
    const Parallel = control.Controller(flat.registry, flat.tree);
    const Host = struct {
        controller: ?*Parallel = null,
        runs: [34]std.atomic.Value(u64) = [_]std.atomic.Value(u64){std.atomic.Value(u64).init(0)} ** 34,
        fn start(context: ?*anyopaque, uid: u32, run: u64) callconv(.c) void {
            const self: *@This() = @ptrCast(@alignCast(context.?));
            self.runs[uid].store(run, .release);
            self.controller.?.reportRunning(uid, run) catch @panic("running failed");
        }
        fn stop(context: ?*anyopaque, uid: u32, run: u64) callconv(.c) void {
            const self: *@This() = @ptrCast(@alignCast(context.?));
            self.controller.?.reportReturned(uid, run) catch @panic("return failed");
        }
        fn complete(_: ?*anyopaque, _: u64) callconv(.c) void {}
        fn log(_: ?*anyopaque, _: u32, _: [*]const u8, _: usize) callconv(.c) void {}
        fn itemChanged(_: ?*anyopaque, _: u64, _: s.types.ItemKeyView, _: *const s.types.ReasonView) callconv(.c) void {}
    };
    for (0..16) |_| {
        var host: Host = .{};
        var cfg = s.config(4);
        cfg.host_blocks = &flat.flat_host_blocks;
        cfg.demand_item = 100;
        cfg.shutdown_item = 101;
        const c = try Parallel.create(std.testing.allocator, cfg, .{ .context = &host, .start = Host.start, .stop = Host.stop, .complete = Host.complete, .log = Host.log, .item_changed = Host.itemChanged });
        host.controller = c;
        c.activate();
        defer {
            c.shutdown(s.reason, null) catch {};
            c.waitIdle();
            c.destroy() catch @panic("parallel fixture did not destroy");
        }
        const Op = struct {
            fn set(controller: *Parallel, uid: u32) void {
                var key: [8]u8 = undefined;
                const text = std.fmt.bufPrint(&key, "{d}", .{uid}) catch unreachable;
                controller.setDemand(text, uid, s.reason, null) catch @panic("parallel demand failed");
            }
        };
        var threads: [32]std.Thread = undefined;
        for (&threads, 0..) |*thread, index| thread.* = try std.Thread.spawn(.{}, Op.set, .{ c, @as(u32, @intCast(index + 2)) });
        for (threads) |thread| thread.join();
        c.waitIdle();
        try c.shutdown(s.reason, null);
        c.waitIdle();
        try eq(@as(usize, 4), c.transitionCount());
        try eq(@as(u64, 128), c.transition_sequence);
        for (c.transitions) |row| try expect(row.sequence >= 125 and row.sequence <= 128);
        for (flat.flat_host_blocks) |uid| try eq(s.types.BlockState.stopped, try c.readState(uid));
    }
}

// The earlier C# log test expected an Information record for every rule wake.
// Layered conditions intentionally log only when a decision performs an action.
test "an unchanged top condition neither records nor logs another decision" {
    var host: H = .{};
    const c = try make(&host, 128);
    defer close(&host, c);
    try demand(c, "worker", 2);
    try state(c, 2, .running);
    const decisions = c.decisionSequence(1);
    const logs = host.logs.load(.acquire);
    for (0..8) |_| try demand(c, "worker", 2);
    try eq(decisions, c.decisionSequence(1));
    try eq(logs, host.logs.load(.acquire));
    try eq(@as(u32, 1), host.startCount(2));
    try eq(@as(u32, 0), host.stopCount(2));
}
