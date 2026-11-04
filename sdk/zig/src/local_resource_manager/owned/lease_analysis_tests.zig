//! Focused SDK-internal diagnostics checks, with one concrete C ABI fixture.
//! Use src/ledger_lease_analysis_tests.zig as the root to retain SDK module bounds.
//! Filter on "lease analysis" to exclude unrelated transitive lifecycle tests.
const std = @import("std");
const native = @import("native.zig");
const types = @import("types.zig");
const owner_module = @import("owner.zig");
const index_module = @import("index.zig");
const analysis = types.LeaseAnalysis;
const uid_module = @import("../uid.zig");

const Lease = types.For(uid_module.Identifier(128)).Lease;
const sample_uid = @as([16]u8, @splat(0x35));
const a = Lease{ .slot = 0, .allocation_number = 1, .use_generation = 1 };
const b = Lease{ .slot = 0, .allocation_number = 1, .use_generation = 2 };
const c = Lease{ .slot = 1, .allocation_number = 2, .use_generation = 1 };

fn key(comptime Uid: type, value: u8) Uid {
    var result = Uid{ .bytes = @splat(0) };
    result.bytes[result.bytes.len - 1] = value;
    return result;
}

const attributes = types.Attributes{ .recoverability = .recoverable, .recovery_cost_coefficient = 1, .access_loss_impact = .unobservable_now };
const tables = [_]types.Table{.{
    .capacity = 8,
    .partition_capacity = 2,
    .reserved_start = 0,
    .reserved_count = 4,
    .attributes = attributes,
    .policy = .{
        .score_epochs = 8,
        .idle_epochs = 8,
        .protection = .concentrated,
        .concentrated_trigger_percent = 50,
        .concentrated_target_percent = 75,
        .smooth_trigger_percent = 80,
        .smooth_emergency_percent = 60,
        .smooth_interval_epochs = 1,
        .smooth_release_count = 1,
        .tick_maximum_actions = 8,
        .release_check = .{ .enabled = false, .tolerance_parts_per_million = 0, .tolerance_bytes = 0 },
    },
}};

// Unit fixtures instantiate the private owner, not multiple namespaces of C exports.
// The public ABI test below separately creates exactly one concrete Native binding.
fn Fixture(comptime bits: u16, comptime enabled: bool, comptime capacity: u32) type {
    return ConfiguredFixture(bits, enabled, capacity, false);
}
fn ConfiguredFixture(comptime bits: u16, comptime enabled: bool, comptime capacity: u32, comptime cached: bool) type {
    return SizedFixture(bits, enabled, capacity, cached, 16384, 64);
}
fn SizedFixture(comptime bits: u16, comptime enabled: bool, comptime capacity: u32, comptime cached: bool, comptime pages: u32, comptime slots: u32) type {
    const Uid = uid_module.Identifier(bits);
    const fixed = [_]types.For(Uid).Fixed{.{ .table = 0, .parent = null, .ordinal = 7, .uid = key(Uid, 1), .allocation_bytes = 17, .attributes = null }};
    const Index = index_module.Candidate(Uid, .c2_chain);
    const Ledger = owner_module.Owner(.{ .uid_width = bits, .activity_width = 8, .index_buckets = 16, .development_lease_analysis = enabled, .development_lease_analysis_capacity = capacity, .development_lease_analysis_write_watch_pages = pages, .use_window_cache = cached, .use_window_cache_slots = slots }, &tables, &fixed, Index);
    const Notifier = @FieldType(Ledger.TableState, "notifier");
    const State = struct {
        rows: [8]Ledger.Row = undefined,
        shells: [2]Ledger.Shell = undefined,
        tables: [1]Ledger.TableState = undefined,
        bindings: [1]Ledger.FixedBinding = undefined,
        nodes: [10]Index.Node = undefined,
        homes: [16]Index.Home = undefined,
        records: [1][2]Notifier.Record = undefined,
        ledger: Ledger = undefined,
    };
    const Scratch = struct {
        candidates: [8]Ledger.Candidate = undefined,
        rows: [8]Ledger.Candidate = undefined,
        shells: [2]u32 = undefined,
        owner_homes: [2]u32 = undefined,
        notes: [10]Ledger.api.Notification = undefined,
        remove_ids: [10]u32 = undefined,
        puts: [2]Index.Transactions.Put = undefined,
        transaction_homes: [12]u32 = undefined,
        transaction_nodes: [12]u32 = undefined,
        transaction_publications: [12]Index.Publication = undefined,
        transaction_removals: [12]Index.RemovePlan = undefined,
        transaction_inserts: [12]Index.InsertPlan = undefined,
        cells: [8]Ledger.PartitionPlanning.Cell = undefined,
        children: [2]Ledger.PartitionPlanning.Child = undefined,
        child_slots: [2]u32 = undefined,
        numbers: [2]u64 = undefined,
        merge_a: [2]u64 = undefined,
        merge_b: [2]u64 = undefined,
        best_numbers: [2]u64 = undefined,
    };
    const DestructionScratch = struct {
        remove_ids: [10]u32 = undefined,
        notices: [10]Ledger.Destruction.Notice = undefined,
        table_views: [1]types.TableView = undefined,
        transaction_homes: [10]u32 = undefined,
        transaction_nodes: [10]u32 = undefined,
        transaction_publications: [10]Index.Publication = undefined,
        transaction_removals: [10]Index.RemovePlan = undefined,
    };
    return struct {
        pub const Owner = Ledger;
        pub const U = Uid;
        state: State = .{},
        scratch: Scratch = .{},
        destruction: DestructionScratch = .{},

        fn initialize(self: *@This()) !void {
            const subscribers = [_][]Notifier.Record{&self.state.records[0]};
            try Ledger.init(&self.state.ledger, &self.state.rows, &self.state.shells, &self.state.tables, &self.state.bindings, &self.state.nodes, &self.state.homes, &subscribers);
        }

        fn workspace(self: *@This()) Ledger.Workspace {
            const s = &self.scratch;
            return .{ .candidates = &s.candidates, .rows = &s.rows, .shells = &s.shells, .homes = &s.owner_homes, .notes = &s.notes, .remove_ids = &s.remove_ids, .puts = &s.puts, .index_workspace = .{ .homes = &s.transaction_homes, .nodes = &s.transaction_nodes, .publications = &s.transaction_publications, .removals = &s.transaction_removals, .inserts = &s.transaction_inserts }, .cells = &s.cells, .children = &s.children, .child_slots = &s.child_slots, .numbers = &s.numbers, .merge_a = &s.merge_a, .merge_b = &s.merge_b, .best_numbers = &s.best_numbers };
        }

        fn destroyWorkspace(self: *@This()) Ledger.Destruction.Workspace {
            const s = &self.destruction;
            return .{ .remove_ids = &s.remove_ids, .notices = &s.notices, .table_views = &s.table_views, .index_workspace = .{ .homes = &s.transaction_homes, .nodes = &s.transaction_nodes, .publications = &s.transaction_publications, .removals = &s.transaction_removals } };
        }

        fn cleanup(self: *@This()) void {
            var i: usize = 0;
            while (i < self.state.rows.len) : (i += 1)
                self.state.rows[i].resource.forceRelease() catch @panic("lease analysis fixture cleanup failed");
        }
    };
}

test "lease analysis disabled storage is void and no query is fabricated" {
    inline for (.{ 128, 256 }) |bits| {
        const Off = Fixture(bits, false, 4);
        const On = Fixture(bits, true, 4);
        try std.testing.expect(@FieldType(Off.Owner, "lease_analysis") == void);
        try std.testing.expect(@sizeOf(Off.Owner) < @sizeOf(On.Owner));
        var f = Off{};
        try f.initialize();
        defer f.cleanup();
        try std.testing.expectError(error.LeaseAnalysisDisabled, f.state.ledger.getLeaseAnalysis(&.{}));
    }
}

test "lease analysis automatic samples dedupe windows and preserve no-write gaps" {
    var tracker = analysis.Tracker(4){};
    tracker.begin(&sample_uid, a, 2, 100);
    tracker.observe(1, 2, 2, 0, 120);
    tracker.observe(1, 2, 3, 0, 130);
    tracker.observe(1, 3, 1, 0, 160);
    tracker.finish(a, 5, 200);
    var output: [4]analysis.Record = undefined;
    const summary = tracker.snapshot(&output, 300);
    try std.testing.expectEqual(@as(u64, 1), summary.completed_count);
    try std.testing.expectEqual(@as(u64, 2), output[0].write_window_count);
    try std.testing.expectEqual(@as(u64, 6), output[0].dirty_page_samples);
    try std.testing.expectEqual(@as(u64, 160), output[0].last_observed_write_ms);
    try std.testing.expectEqual(@as(u64, 40), output[0].maximum_no_write_gap_ms);
    try std.testing.expect(output[0].support_flags & analysis.write_watch.reads_unobservable != 0);
    // Ended and unknown credentials are not an observer capability, even after overflow.
    tracker.observe(1, 6, 100, 0, 400);
    _ = tracker.snapshot(&output, 400);
    try std.testing.expectEqual(@as(u64, 6), output[0].dirty_page_samples);
}

test "lease analysis overflow never replaces active records and ended FIFO is bounded" {
    var tracker = analysis.Tracker(2){};
    tracker.begin(&sample_uid, a, 1, 10);
    tracker.begin(&sample_uid, b, 1, 10);
    tracker.begin(&sample_uid, c, 1, 10);
    tracker.finish(c, 2, 15); // untracked credential: cannot complete another record
    tracker.observe(2, 2, 12, 0, 16);
    var output: [2]analysis.Record = undefined;
    var summary = tracker.snapshot(&output, 20);
    try std.testing.expectEqual(@as(u64, 1), summary.dropped_records);
    try std.testing.expectEqual(@as(u64, 2), summary.active_count);
    try std.testing.expectEqual(@as(u64, 0), summary.unavailable_write_samples);
    try std.testing.expectEqual(@as(u64, 0), output[0].dirty_page_samples);
    tracker.finish(b, 2, 20);
    tracker.finish(a, 2, 20);
    tracker.begin(&sample_uid, c, 3, 21);
    summary = tracker.snapshot(&output, 25);
    try std.testing.expectEqual(@as(u64, 1), summary.active_count);
    try std.testing.expectEqual(a.use_generation, output[0].use_generation);
    try std.testing.expectEqual(c.allocation_number, output[1].allocation_number);
}

test "lease analysis shared writes flags unavailable saturated counters and forced time" {
    var tracker = analysis.Tracker(2){};
    tracker.begin(&sample_uid, a, 1, 0);
    tracker.begin(&sample_uid, b, 1, 0);
    tracker.observe(1, 1, std.math.maxInt(u64), 0, 1);
    tracker.observe(1, 2, 1, analysis.write_watch.query_failed, 2);
    tracker.observe(1, 3, 0, analysis.write_watch.shared_attribution, 3);
    tracker.invalidate(1, 9, 30);
    var output: [2]analysis.Record = undefined;
    const summary = tracker.snapshot(&output, 100);
    try std.testing.expectEqual(@as(u64, 1), summary.unavailable_write_samples);
    for (output) |r| {
        try std.testing.expectEqual(analysis.forced, r.state);
        try std.testing.expectEqual(@as(u64, 30), r.ended_ms);
        try std.testing.expectEqual(std.math.maxInt(u64), r.dirty_page_samples);
        try std.testing.expect(r.observation_flags & analysis.write_watch.shared_attribution != 0);
        try std.testing.expect(r.observation_flags & analysis.write_watch.query_failed != 0);
    }
}

test "lease analysis Windows write watch observes plain writes not reads without markers" {
    if (@import("builtin").os.tag != .windows) return error.SkipZigTest;
    const memory = @import("../memory_region.zig");
    var region = try memory.Region.allocateWriteWatch(std.heap.pageSize() * 2, true);
    defer region.release() catch unreachable;
    var scratch: [2]usize = undefined;
    try std.testing.expectEqual(@as(u32, 0), analysis.write_watch.establishBaseline(region.base.?, region.page_bytes, scratch.len));
    // Volatile reads produce no dirty pages.
    const ptr: [*]volatile u8 = region.base.?;
    std.mem.doNotOptimizeAway(ptr[0]);
    try std.testing.expectEqual(@as(u64, 0), analysis.write_watch.collect(region.base.?, region.page_bytes, &scratch).pages);
    ptr[0] = 7;
    ptr[std.heap.pageSize()] = 9;
    const first = analysis.write_watch.collect(region.base.?, region.page_bytes, &scratch);
    try std.testing.expectEqual(@as(u32, 0), first.flags);
    try std.testing.expectEqual(@as(u64, 2), first.pages);
    try std.testing.expectEqual(@as(u64, 0), analysis.write_watch.collect(region.base.?, region.page_bytes, &scratch).pages);
    ptr[0] = 8;
    try std.testing.expectEqual(analysis.write_watch.exceeds_scratch, analysis.write_watch.collect(region.base.?, region.page_bytes, scratch[0..1]).flags);
    try std.testing.expectEqual(@as(u64, 1), analysis.write_watch.collect(region.base.?, region.page_bytes, &scratch).pages);
}

test "lease analysis real owner plain writes are collected at end and query is passive" {
    if (@import("builtin").os.tag != .windows) return error.SkipZigTest;
    inline for (.{ 128, 256 }) |bits| {
        const F = Fixture(bits, true, 4);
        var f = F{};
        try f.initialize();
        defer f.cleanup();
        const manager = &f.state.ledger;
        var work = f.workspace();
        const load = (try manager.push(.{ .table = 0 }, key(F.U, 1), null, &work)).newly_allocated;
        const p: [*]volatile u8 = load.region.ptr;
        p[0] = 7;
        _ = try manager.endUse(load.lease, .complete);
        var output: [4]analysis.Record = undefined;
        _ = try manager.getLeaseAnalysis(&output);
        try std.testing.expectEqual(analysis.ended, output[0].state);
        try std.testing.expect(output[0].dirty_page_samples > 0);
        const borrow = (try manager.useFixed(0)).in_memory;
        std.mem.doNotOptimizeAway(@as([*]volatile u8, borrow.region.ptr)[0]);
        _ = try manager.endUse(borrow.lease, .complete);
        _ = try manager.getLeaseAnalysis(&output);
        try std.testing.expectEqual(@as(u64, 0), output[1].dirty_page_samples);
    }
}

test "lease analysis cached logical holders force discard and SDK resize baseline are automatic" {
    if (@import("builtin").os.tag != .windows) return error.SkipZigTest;
    inline for (.{ 128, 256 }) |bits| {
        const F = ConfiguredFixture(bits, true, 8, true);
        var f = F{};
        try f.initialize();
        defer f.cleanup();
        const manager = &f.state.ledger;
        var work = f.workspace();
        const uid = key(F.U, 1);
        const load = (try manager.push(.{ .table = 0 }, uid, null, &work)).newly_allocated;
        _ = try manager.endUse(load.lease, .complete);
        const a_hold = (try manager.useFixed(0)).in_memory;
        const b_hold = (try manager.useFixed(0)).in_memory;
        @as([*]volatile u8, a_hold.region.ptr)[0] = 0x43;
        var ticks = [_]F.Owner.Workspace{f.workspace()};
        _ = try manager.tick(&ticks);
        var out: [8]analysis.Record = undefined;
        var summary = try manager.getLeaseAnalysis(&out);
        try std.testing.expectEqual(@as(u64, 2), summary.active_count);
        try std.testing.expectEqual(@as(u64, 0), summary.unavailable_write_samples);
        for (out[1..3]) |record| {
            try std.testing.expect(record.dirty_page_samples > 0);
            try std.testing.expect(record.observation_flags & analysis.write_watch.shared_attribution != 0);
        }
        // Caller is explicitly quiescent: no read follows force, only returns.
        try manager.forceRelease(uid, &work);
        try std.testing.expect((try manager.endUse(a_hold.lease, .complete)) == .force_released);
        try std.testing.expect((try manager.endUse(b_hold.lease, .complete)) == .force_released);
        summary = try manager.getLeaseAnalysis(&out);
        try std.testing.expectEqual(@as(u64, 0), summary.active_count);
        try std.testing.expectEqual(analysis.forced, out[1].state);
        try std.testing.expectEqual(analysis.forced, out[2].state);
        const failed = (try manager.push(.{ .table = 0 }, uid, null, &work)).newly_allocated;
        @as([*]volatile u8, failed.region.ptr)[0] = 0x79;
        _ = try manager.endUse(failed.lease, .discard);
        summary = try manager.getLeaseAnalysis(&out);
        try std.testing.expectEqual(analysis.ended, out[3].state);
        try std.testing.expect(out[3].dirty_page_samples > 0);
        try std.testing.expect((try manager.useFixed(0)) == .not_in_memory);
        const replacement = (try manager.push(.{ .table = 0 }, uid, null, &work)).newly_allocated;
        _ = try manager.endUse(replacement.lease, .complete);
        const resize = (try manager.useFixed(0)).in_memory;
        @as([*]volatile u8, resize.region.ptr)[0] = 0x51;
        const enlarged = (try manager.updateLease(resize.lease, .{ .allocation_bytes = std.heap.pageSize() * 2 + 3 })).updated.?;
        try std.testing.expectEqual(@as(u8, 0x51), enlarged[0]);
        _ = try manager.endUse(resize.lease, .complete);
        summary = try manager.getLeaseAnalysis(&out);
        try std.testing.expectEqual(analysis.ended, out[5].state);
        // Only the user's old-page write is observed. SDK copy, fresh pages and
        // baseline materialization do not turn into additional dirty samples.
        try std.testing.expectEqual(@as(u64, 1), out[5].dirty_page_samples);
        try std.testing.expectEqual(@as(u64, 1), out[5].write_window_count);
    }
}

// The largest owner the FreedomPoint ranges allow: 65536 records, 1048576
// write-watch pages and 65536 cache hints, about 18 MiB of owner state.
const Maximal = SizedFixture(128, true, 65536, true, 1048576, 65536);
const normal_stack_bytes = 1 << 20;

const StackProbe = struct {
    fixture: *Maximal,
    result: anyerror!void = {},

    fn exercise(f: *Maximal) !void {
        try f.initialize();
        defer f.cleanup();
        const manager = &f.state.ledger;
        var work = f.workspace();
        const load = (try manager.push(.{ .table = 0 }, key(Maximal.U, 1), null, &work)).newly_allocated;
        @as([*]volatile u8, load.region.ptr)[0] = 0x6d;
        _ = try manager.endUse(load.lease, .complete);
        var output: [1]analysis.Record = undefined;
        const summary = try manager.getLeaseAnalysis(&output);
        try std.testing.expectEqual(@as(u32, 65536), summary.record_capacity);
        try std.testing.expectEqual(@as(u64, 1), summary.completed_count);
        try std.testing.expectEqual(analysis.ended, output[0].state);
    }
    fn runWindows(raw: ?*anyopaque) callconv(.winapi) u32 {
        const probe: *StackProbe = @ptrCast(@alignCast(raw.?));
        probe.result = exercise(probe.fixture);
        return 0;
    }
    fn runPortable(probe: *StackProbe) void {
        probe.result = exercise(probe.fixture);
    }
};

const ThreadApi = struct {
    const stack_size_param_is_a_reservation: u32 = 0x00010000;
    const infinite: u32 = 0xFFFFFFFF;
    extern "kernel32" fn CreateThread(attributes: ?*anyopaque, stack_bytes: usize, start: *const fn (?*anyopaque) callconv(.winapi) u32, parameter: ?*anyopaque, flags: u32, id: ?*u32) callconv(.winapi) ?*anyopaque;
    extern "kernel32" fn WaitForSingleObject(handle: *anyopaque, milliseconds: u32) callconv(.winapi) u32;
    extern "kernel32" fn CloseHandle(handle: *anyopaque) callconv(.winapi) i32;
};

test "lease analysis owner at the allowed maxima initializes in place on a 1 MiB thread stack" {
    // A by-value owner would not fit the whole thread stack many times over.
    try std.testing.expect(@sizeOf(Maximal.Owner) > 16 * normal_stack_bytes);
    const fixture = try std.heap.page_allocator.create(Maximal);
    defer std.heap.page_allocator.destroy(fixture);
    var probe = StackProbe{ .fixture = fixture };
    if (@import("builtin").os.tag == .windows) {
        // A reservation, not a commit size: the thread has exactly 1 MiB of stack.
        const thread = ThreadApi.CreateThread(null, normal_stack_bytes, &StackProbe.runWindows, &probe, ThreadApi.stack_size_param_is_a_reservation, null) orelse
            return error.ThreadCreateFailed;
        defer _ = ThreadApi.CloseHandle(thread);
        if (ThreadApi.WaitForSingleObject(thread, ThreadApi.infinite) != 0) return error.ThreadWaitFailed;
    } else {
        const thread = try std.Thread.spawn(.{ .stack_size = normal_stack_bytes }, StackProbe.runPortable, .{&probe});
        thread.join();
    }
    try probe.result;
}
