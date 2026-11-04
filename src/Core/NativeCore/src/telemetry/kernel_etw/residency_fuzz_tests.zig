const std = @import("std");
const residency = @import("residency.zig");
const Owner = residency.Owner;
const a = std.testing.allocator;

const Rng = struct {
    state: u64,
    fn next(self: *Rng) u64 {
        // splitmix64
        self.state +%= 0x9e3779b97f4a7c15;
        var z = self.state;
        z = (z ^ (z >> 30)) *% 0xbf58476d1ce4e5b9;
        z = (z ^ (z >> 27)) *% 0x94d049bb133111eb;
        return z ^ (z >> 31);
    }
    fn below(self: *Rng, n: u64) u64 {
        return self.next() % n;
    }
};

const Shape = struct {
    processors: usize,
    tids: usize,
    pids: usize,
    max_pairs: usize,
    max_processes: usize,
    max_threads: usize,
    max_name_bytes: usize,
    qpc_frequency: i64 = 1000,
    /// Window, loss and processor-set changes happen once per `rare` draws of their slot.
    rare: u64 = 1,
    window_ms: i64,
    slot_choices: []const u64,
    check_every: usize,
    step: u64,
};
const small_slots = [_]u64{ 0, 1, 2, 3, 4, 5, 7, 8 };
const max_shape_processors = 32;
const max_shape_tids = 4096;

/// Every reference the owner counts must be one it can name: a thread is held by
/// being current, by each processor running it and by a non-empty pair chain; a
/// process by being current and by each retained thread. Every pair record is in
/// exactly one chain or on the free list.
fn checkInvariants(o: *Owner) !void {
    for (o.threads.items, 0..) |thread, position| {
        try std.testing.expectEqual(position, thread.index);
        var expected: usize = if (thread.current) 1 else 0;
        for (o.processors) |processor| {
            if (processor.known and processor.running.thread == thread) expected += 1;
        }
        if (thread.window_pairs != 0) expected += 1;
        try std.testing.expectEqual(expected, thread.references);
        var found = false;
        for (o.processes.items) |process| {
            if (process == thread.process) found = true;
        }
        try std.testing.expect(found);
    }
    for (o.processes.items, 0..) |process, position| {
        try std.testing.expectEqual(position, process.index);
        var expected: usize = if (process.current) 1 else 0;
        for (o.threads.items) |thread| {
            if (thread.process == process) expected += 1;
        }
        try std.testing.expectEqual(expected, process.references);
    }
    for (o.processors) |processor| {
        if (!processor.known) continue;
        if (processor.running.thread) |thread| {
            var found = false;
            for (o.threads.items) |candidate| {
                if (candidate == thread) found = true;
            }
            try std.testing.expect(found);
        }
    }
    var iterator = o.current_threads.iterator();
    while (iterator.next()) |entry| {
        try std.testing.expect(entry.value_ptr.*.current);
        try std.testing.expectEqual(entry.key_ptr.*, entry.value_ptr.*.thread_id);
    }
    var processes = o.current_processes.iterator();
    while (processes.next()) |entry| {
        try std.testing.expect(entry.value_ptr.*.current);
        try std.testing.expectEqual(entry.key_ptr.*, entry.value_ptr.*.process_id);
    }
    var seen = try a.alloc(bool, o.record_count);
    defer a.free(seen);
    @memset(seen, false);
    var chained: usize = 0;
    for (o.threads.items) |thread| {
        var link = thread.window_pairs;
        while (link != 0) {
            const pair = link - 1;
            try std.testing.expect(pair < o.record_count);
            try std.testing.expect(!seen[pair]);
            seen[pair] = true;
            chained += 1;
            const value = &o.record_chunks.items[pair / residency.records_per_chunk][pair % residency.records_per_chunk];
            try std.testing.expect(value.processor < o.processors.len);
            link = value.next;
        }
    }
    try std.testing.expectEqual(o.live_pairs, chained);
    var free_count: usize = 0;
    var link = o.free_records;
    while (link != 0) {
        const pair = link - 1;
        try std.testing.expect(pair < o.record_count);
        try std.testing.expect(!seen[pair]);
        seen[pair] = true;
        free_count += 1;
        link = o.record_chunks.items[pair / residency.records_per_chunk][pair % residency.records_per_chunk].next;
    }
    try std.testing.expectEqual(@as(usize, o.record_count), chained + free_count);
}

fn accept(result: anyerror!void) !void {
    result catch |err| switch (err) {
        error.CapacityExceeded, error.InvalidConfig, error.InvalidInput, error.InvalidWindowBoundary => {},
        else => return err,
    };
}

const Stats = struct { complete: usize = 0, rows: usize = 0, resets: i64 = 0, ring: usize = 0, pairs: usize = 0 };
var stats: Stats = .{};

fn runCase(seed: u64, operations: usize, shape: Shape) !void {
    var rng: Rng = .{ .state = seed };
    const processor_count: usize = 1 + @as(usize, @intCast(rng.below(shape.processors)));
    var all_processors: [max_shape_processors]i32 = undefined;
    for (&all_processors, 0..) |*id, i| id.* = @intCast(i);
    var o = try Owner.init(a, .{
        .qpc_frequency = shape.qpc_frequency,
        .window_ms = shape.window_ms,
        .max_pairs = shape.max_pairs,
        .max_processes = shape.max_processes,
        .max_threads = shape.max_threads,
        .max_processors = shape.processors,
        .max_name_bytes = shape.max_name_bytes,
        .processors = all_processors[0..processor_count],
    });
    defer o.deinit();
    if (rng.below(2) == 0) try o.beginWindowAt(0, shape.slot_choices[@intCast(rng.below(shape.slot_choices.len))]);
    const max_tid = shape.tids;
    const max_pid = shape.pids;

    var thread_pid: [max_shape_tids + 1]i32 = @splat(0);
    var running: [max_shape_processors]i32 = @splat(0);
    var running_pid: [max_shape_processors]i32 = @splat(0);
    var qpc: i64 = 0;
    var unique: u64 = 1;
    const names: [3][]const u8 = .{ "alpha", " beta process name that is a little longer ", "g" };

    for (0..operations) |operation| {
        var choice = rng.below(100);
        if (choice >= 89 and choice <= 94 and rng.below(shape.rare) != 0) choice = 99;
        const lp: usize = @intCast(rng.below(shape.processors));
        if (choice < 50) {
            qpc += @intCast(rng.below(shape.step));
            if (rng.below(40) == 0) qpc -= 5;
            if (rng.below(60) == 0) qpc += 120;
            var old_tid = running[lp];
            var old_pid = running_pid[lp];
            if (rng.below(25) == 0) {
                old_tid = @intCast(rng.below(max_tid + 1));
                old_pid = @intCast(rng.below(max_pid + 1));
            }
            var new_tid: i32 = 0;
            var new_pid: i32 = 0;
            const pick = rng.below(10);
            if (pick >= 2) {
                new_tid = @intCast(1 + rng.below(max_tid));
                new_pid = thread_pid[@intCast(new_tid)];
                if (pick == 9) new_pid = @intCast(rng.below(max_pid + 1));
            }
            const name: ?[]const u8 = if (rng.below(8) == 0) names[@intCast(rng.below(3))] else null;
            try accept(o.observeContextSwitch(qpc, qpc * 10, @intCast(lp), old_pid, old_tid, new_pid, name, new_tid));
            running[lp] = new_tid;
            running_pid[lp] = new_pid;
        } else if (choice < 58) {
            const tid: usize = @intCast(1 + rng.below(max_tid));
            const pid: i32 = @intCast(1 + rng.below(max_pid));
            thread_pid[tid] = pid;
            try accept(o.observeThreadStart(qpc, 0, pid, @intCast(tid)));
        } else if (choice < 64) {
            const tid: usize = @intCast(1 + rng.below(max_tid));
            try accept(o.observeThreadEnd(qpc, 0, thread_pid[tid], @intCast(tid)));
        } else if (choice < 68) {
            const pid: i32 = @intCast(1 + rng.below(max_pid));
            unique += 1;
            const start: ?i64 = if (rng.below(2) == 0) @as(i64, @intCast(unique)) else null;
            try accept(o.observeProcessStart(qpc, 0, pid, if (rng.below(3) == 0) 0 else unique, start, names[@intCast(rng.below(3))]));
        } else if (choice < 71) {
            const pid: i32 = @intCast(1 + rng.below(max_pid));
            try accept(o.observeProcessEnd(qpc, 0, pid, if (rng.below(2) == 0) 0 else unique));
        } else if (choice < 75) {
            const tid: usize = @intCast(1 + rng.below(max_tid));
            const pid: i32 = @intCast(1 + rng.below(max_pid));
            thread_pid[tid] = pid;
            try accept(o.seedProcess(pid, @intCast(1 + rng.below(4)), names[@intCast(rng.below(3))]));
            try accept(o.seedThread(pid, @intCast(tid)));
        } else if (choice < 79) {
            const tid: usize = @intCast(1 + rng.below(max_tid));
            const pid: i32 = @intCast(1 + rng.below(max_pid));
            thread_pid[tid] = pid;
            const inventory = o.captureInventorySeed();
            const seeded = o.seedInventoryProcess(inventory, pid, @intCast(1 + rng.below(4)), names[@intCast(rng.below(3))]) catch |err| switch (err) {
                error.CapacityExceeded => null,
                else => return err,
            };
            if (seeded) |process| try accept(o.seedInventoryThread(process, @intCast(tid)));
        } else if (choice < 89) {
            var snapshot = if (rng.below(2) == 0) try o.snapshot(a) else try o.snapshotBorrowedNames(a);
            if (snapshot.is_complete) stats.complete += 1;
            stats.rows += snapshot.records.len;
            for (snapshot.records) |row| {
                try std.testing.expect(row.execution_time_qpc >= 0);
                try std.testing.expect(row.process_name.len <= 4096);
            }
            snapshot.deinit(a);
        } else if (choice == 89) {
            if (rng.below(4) == 0) try o.resetAfterEventLoss();
        } else if (choice < 92) {
            const floor = @max(0, qpc - @as(i64, @intCast(rng.below(50))));
            try accept(o.beginWindowAt(floor, shape.slot_choices[@intCast(rng.below(shape.slot_choices.len))]));
        } else if (choice == 92) {
            const windows = [_]i64{ 50, 100, 200 };
            try accept(o.configureWindow(windows[@intCast(rng.below(3))], 1 + @as(usize, @intCast(rng.below(shape.max_pairs)))));
        } else if (choice == 93) {
            if (rng.below(4) == 0) {
                var ids = all_processors;
                var i: usize = shape.processors - 1;
                while (i > 0) : (i -= 1) {
                    const j: usize = @intCast(rng.below(i + 1));
                    std.mem.swap(i32, &ids[i], &ids[j]);
                }
                try accept(o.setProcessors(ids[0 .. 1 + @as(usize, @intCast(rng.below(shape.processors)))]));
            }
        } else if (choice == 94) {
            if (rng.below(20) == 0) {
                try o.clear();
                qpc = 0;
            }
        } else {
            qpc += @intCast(rng.below(40));
        }
        if (operation % shape.check_every == 0) try checkInvariants(&o);
        stats.pairs = @max(stats.pairs, o.live_pairs);
        if (o.last_capacity_reset.reason == @intFromEnum(residency.CapacityResetReason.ring_span)) stats.ring += 1;
    }
    try checkInvariants(&o);
    stats.resets += o.capacity_reset_count;
}

test "randomized residency event streams keep every counted reference and pair record accounted for" {
    stats = .{};
    var seed: u64 = 1;
    while (seed <= 400) : (seed += 1) {
        runCase(seed, 3000, .{ .processors = 4, .tids = 48, .pids = 10, .max_pairs = 4 + @as(usize, @intCast(seed % 60)), .max_processes = 6 + @as(usize, @intCast(seed % 12)), .max_threads = 12 + @as(usize, @intCast(seed % 48)), .max_name_bytes = 64 + @as(usize, @intCast(seed % 256)), .window_ms = 100, .slot_choices = &small_slots, .check_every = 1, .step = 15 }) catch |err| {
            std.debug.print("residency fuzz failed at seed {d}: {s}\n", .{ seed, @errorName(err) });
            return err;
        };
    }
    // The streams must actually reach complete windows, capacity resets and ring-span rejections.
    try std.testing.expect(stats.complete > 0 and stats.resets > 0 and stats.ring > 0);
}

/// A machine-shaped stream: 32 processors, 4000 seeded threads in 200 processes,
/// mostly consistent context switches, occasional thread churn and window begins.
fn runProductionLike(seed: u64, operations: usize, slots: u64, max_pairs: usize) !void {
    var rng: Rng = .{ .state = seed };
    var processors: [32]i32 = undefined;
    for (&processors, 0..) |*id, i| id.* = @intCast(i);
    var o = try Owner.init(a, .{ .qpc_frequency = 10_000_000, .window_ms = 500, .max_pairs = max_pairs, .max_processes = 4096, .max_threads = 16384, .max_processors = 32, .max_name_bytes = 1 << 20, .processors = &processors });
    defer o.deinit();
    try o.beginWindowAt(0, slots);
    const tids = 4000;
    var thread_pid: [tids + 1]i32 = undefined;
    for (1..201) |pid| try o.seedProcess(@intCast(pid), @intCast(pid), "process");
    for (1..tids + 1) |tid| {
        thread_pid[tid] = @intCast(1 + tid % 200);
        try o.seedThread(thread_pid[tid], @intCast(tid));
    }
    var running: [32]i32 = @splat(0);
    var qpc: i64 = 1;
    for (0..operations) |operation| {
        const choice = rng.below(1000);
        if (choice < 960) {
            const lp: usize = @intCast(rng.below(32));
            qpc += @intCast(rng.below(2000));
            const old = running[lp];
            const new: i32 = if (rng.below(5) == 0) 0 else @intCast(1 + rng.below(tids));
            try accept(o.observeContextSwitch(qpc, qpc, @intCast(lp), if (old == 0) 0 else thread_pid[@intCast(old)], old, if (new == 0) 0 else thread_pid[@intCast(new)], null, new));
            running[lp] = new;
        } else if (choice < 975) {
            const tid: usize = @intCast(1 + rng.below(tids));
            try accept(o.observeThreadEnd(qpc, 0, thread_pid[tid], @intCast(tid)));
            try accept(o.observeThreadStart(qpc, 0, thread_pid[tid], @intCast(tid)));
        } else if (choice < 995) {
            var snapshot = try o.snapshotBorrowedNames(a);
            if (snapshot.is_complete) stats.complete += 1;
            stats.rows += snapshot.records.len;
            snapshot.deinit(a);
        } else if (choice < 999) {
            const pid: i32 = @intCast(1 + rng.below(200));
            try accept(o.observeProcessEnd(qpc, 0, pid, 0));
            try accept(o.observeProcessStart(qpc, 0, pid, 0, qpc, "restarted"));
            var tid: usize = @intCast(pid);
            while (tid <= tids) : (tid += 200) if (thread_pid[tid] == pid) try accept(o.observeThreadStart(qpc, 0, pid, @intCast(tid)));
        } else {
            if (rng.below(20) == 0) try o.beginWindowAt(qpc, slots);
        }
        stats.pairs = @max(stats.pairs, o.live_pairs);
        if (operation % 20_000 == 0) try checkInvariants(&o);
    }
    try checkInvariants(&o);
    stats.resets += o.capacity_reset_count;
}

test "machine-shaped residency streams cross pair and ring chunk boundaries" {
    stats = .{};
    const cases = [_]struct { slots: u64, pairs: usize }{ .{ .slots = 0, .pairs = 65536 }, .{ .slots = 0, .pairs = 3000 }, .{ .slots = 5, .pairs = 65536 }, .{ .slots = 50, .pairs = 65536 }, .{ .slots = 2047, .pairs = 2000 } };
    for (cases, 0..) |case, index| {
        runProductionLike(2000 + index, 300_000, case.slots, case.pairs) catch |err| {
            std.debug.print("machine-shaped residency fuzz failed at case {d}: {s}\n", .{ index, @errorName(err) });
            return err;
        };
    }
    // More live pairs than one 64 KiB record chunk holds, and complete windows with rows.
    try std.testing.expect(stats.pairs > residency.records_per_chunk and stats.complete > 0 and stats.rows > 0);
}
