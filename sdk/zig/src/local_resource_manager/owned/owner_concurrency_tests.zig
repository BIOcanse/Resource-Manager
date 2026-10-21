//! Controlled full-owner concurrency traces for both index candidates.
//! All metadata and index workspaces are fixed caller-owned arrays.
const std = @import("std");
const values = @import("ledger_values").Bound;
const uid_module = @import("../uid.zig");
const c2_index = @import("index.zig");
const b2_index = @import("index_b2.zig");
const notifications = @import("notifications.zig");
const owner_module = @import("owner.zig");
const lifecycle = @import("../memory_resource.zig");
const types = @import("types.zig");

const PollLimit: usize = 1_000_000;
const CandidateFlavor = enum { c2, b2 };

const attrs = types.Attributes{
    .recoverability = .recoverable,
    .recovery_cost_coefficient = 1,
    .access_loss_impact = .unobservable_now,
};

fn policy(comptime table: usize) types.Policy {
    const prefix = std.fmt.comptimePrint("table_{d}_", .{table});
    return .{
        .score_epochs = @intCast(@field(values, prefix ++ "activity_score_epochs")),
        .idle_epochs = @intCast(@field(values, prefix ++ "idle_epochs")),
        .protection = switch (@field(values, prefix ++ "capacity_protection_method")) {
            1 => .concentrated,
            2 => .smooth,
            else => unreachable,
        },
        .concentrated_trigger_percent = @intCast(@field(values, prefix ++ "concentrated_trigger_percent")),
        .concentrated_target_percent = @intCast(@field(values, prefix ++ "concentrated_target_percent")),
        .smooth_trigger_percent = @intCast(@field(values, prefix ++ "smooth_trigger_percent")),
        .smooth_emergency_percent = @intCast(@field(values, prefix ++ "smooth_emergency_percent")),
        .smooth_interval_epochs = @intCast(@field(values, prefix ++ "smooth_interval_epochs")),
        .smooth_release_count = @intCast(@field(values, prefix ++ "smooth_release_count")),
        .tick_maximum_actions = @intCast(@field(values, prefix ++ "tick_maximum_actions")),
        .release_check = .{
            .enabled = @field(values, prefix ++ "release_check_enabled") == 2,
            .tolerance_parts_per_million = @intCast(@field(values, prefix ++ "release_tolerance_parts_per_million")),
            .tolerance_bytes = @field(values, prefix ++ "release_tolerance_bytes"),
        },
    };
}

fn key(comptime Uid: type, value: u64) Uid {
    var result = Uid{ .bytes = @splat(0) };
    std.mem.writeInt(u64, result.bytes[result.bytes.len - 8 ..][0..8], value, .big);
    return result;
}

fn Fixture(comptime bits: u16, comptime flavor: CandidateFlavor) type {
    const Uid = uid_module.Identifier(bits);
    const Index = if (flavor == .c2)
        c2_index.Candidate(Uid, .c2_chain)
    else
        b2_index.Candidate(Uid, .b2_ordered);
    const IndexScratch = if (flavor == .c2) struct {
        nodes: [64]u32 = undefined,
        publications: [64]Index.Publication = undefined,
        removals: [64]Index.RemovePlan = undefined,
        inserts: [64]Index.InsertPlan = undefined,
    } else struct {
        // This fixture has at most eleven changed entries; a 32-bucket
        // closure budget stays bounded below the 64-bucket index itself.
        indices: [32]u32 = undefined,
        operations: [32]Index.Transactions.Operation = undefined,
        segments: [64]Index.Transactions.Segment = undefined,
        versions: [32]u64 = undefined,
        removals: [16]Index.RemovePlan = undefined,
        inserts: [16]Index.InsertPlan = undefined,
        simulation_nodes: [32]u32 = undefined,
        simulation_uids: [32]Uid = undefined,
        simulation_node_ids: [48]u32 = undefined,
        simulation_backlinks: [48]u32 = undefined,
    };
    const tables = [_]types.Table{
        .{
            .capacity = 16,
            .partition_capacity = 8,
            .reserved_start = 0,
            .reserved_count = 8,
            .attributes = attrs,
            .policy = policy(0),
        },
        .{
            .capacity = 16,
            .partition_capacity = 8,
            .reserved_start = 0,
            .reserved_count = 0,
            .attributes = attrs,
            .policy = policy(1),
        },
    };
    const fixed = [_]types.For(Uid).Fixed{};
    const Ledger = owner_module.Owner(
        .{
            .uid_width = bits,
            .activity_width = @intCast(values.activity_bitmap_width),
            .index_buckets = 64,
        },
        &tables,
        &fixed,
        Index,
    );
    const Notifier = notifications.Notifier(Uid);

    return struct {
        pub const UidType = Uid;
        pub const IndexType = Index;
        pub const Owner = Ledger;

        rows: [32]Ledger.Row = undefined,
        shells: [16]Ledger.Shell = undefined,
        tables: [2]Ledger.TableState = undefined,
        bindings: [0]Ledger.FixedBinding = .{},
        nodes: [48]Index.Node = undefined,
        homes: [64]Index.Home = undefined,
        records: [2][2]Notifier.Record = undefined,

        candidates: [2][16]Ledger.Candidate = undefined,
        row_work: [2][16]Ledger.Candidate = undefined,
        shell_work: [2][8]u32 = undefined,
        owner_homes: [2][64]u32 = undefined,
        notes: [2][32]Ledger.api.Notification = undefined,
        remove_ids: [2][64]u32 = undefined,
        puts: [2][64]Index.Transactions.Put = undefined,
        index_scratch: [2]IndexScratch = .{ .{}, .{} },
        cells: [2][16]Ledger.PartitionPlanning.Cell = undefined,
        children: [2][8]Ledger.PartitionPlanning.Child = undefined,
        child_slots: [2][8]u32 = undefined,
        numbers: [2][64]u64 = undefined,
        merge_a: [2][64]u64 = undefined,
        merge_b: [2][64]u64 = undefined,
        best_numbers: [2][64]u64 = undefined,

        ledger: Ledger = undefined,
        work: [2]Ledger.Workspace = undefined,

        fn transactionWorkspace(self: *@This(), table: usize) Index.Transactions.Workspace {
            if (flavor == .c2) {
                return .{
                    .homes = &self.owner_homes[table],
                    .nodes = &self.index_scratch[table].nodes,
                    .publications = &self.index_scratch[table].publications,
                    .removals = &self.index_scratch[table].removals,
                    .inserts = &self.index_scratch[table].inserts,
                };
            }
            return .{
                .indices = &self.index_scratch[table].indices,
                .operations = &self.index_scratch[table].operations,
                .segments = &self.index_scratch[table].segments,
                .versions = &self.index_scratch[table].versions,
                .removals = &self.index_scratch[table].removals,
                .inserts = &self.index_scratch[table].inserts,
                .simulation_nodes = &self.index_scratch[table].simulation_nodes,
                .simulation_uids = &self.index_scratch[table].simulation_uids,
                .simulation_node_ids = &self.index_scratch[table].simulation_node_ids,
                .simulation_backlinks = &self.index_scratch[table].simulation_backlinks,
            };
        }

        pub fn initialize(self: *@This()) !void {
            var subscriptions = [_][]Notifier.Record{ &self.records[0], &self.records[1] };
            self.ledger = try Ledger.init(
                &self.rows,
                &self.shells,
                &self.tables,
                &self.bindings,
                &self.nodes,
                &self.homes,
                &subscriptions,
            );
            for (&self.work, 0..) |*work, table| {
                work.* = .{
                    .candidates = &self.candidates[table],
                    .rows = &self.row_work[table],
                    .shells = &self.shell_work[table],
                    .homes = &self.owner_homes[table],
                    .notes = &self.notes[table],
                    .remove_ids = &self.remove_ids[table],
                    .puts = &self.puts[table],
                    .index_workspace = self.transactionWorkspace(table),
                    .cells = &self.cells[table],
                    .children = &self.children[table],
                    .child_slots = &self.child_slots[table],
                    .numbers = &self.numbers[table],
                    .merge_a = &self.merge_a[table],
                    .merge_b = &self.merge_b[table],
                    .best_numbers = &self.best_numbers[table],
                };
            }
        }

        pub fn cleanup(self: *@This()) void {
            for (&self.rows) |*row| row.resource.forceRelease() catch @panic("concurrency fixture cleanup failed");
        }
    };
}

const StartGate = struct {
    ready: std.atomic.Value(u32) = std.atomic.Value(u32).init(0),
    open: std.atomic.Value(bool) = std.atomic.Value(bool).init(false),

    fn arriveAndWait(self: *@This()) void {
        _ = self.ready.fetchAdd(1, .acq_rel);
        while (!self.open.load(.acquire)) std.atomic.spinLoopHint();
    }
};

fn waitForReady(gate: *StartGate, expected: u32) bool {
    var attempts: usize = 0;
    while (gate.ready.load(.acquire) < expected and attempts < PollLimit) : (attempts += 1) {
        std.Thread.yield() catch {};
    }
    return gate.ready.load(.acquire) >= expected;
}

fn waitForDone(done: *std.atomic.Value(bool)) bool {
    var attempts: usize = 0;
    while (!done.load(.acquire) and attempts < PollLimit) : (attempts += 1) {
        std.Thread.yield() catch {};
    }
    return done.load(.acquire);
}

fn waitForFlag(flag: *std.atomic.Value(bool)) bool {
    var attempts: usize = 0;
    while (!flag.load(.acquire) and attempts < PollLimit) : (attempts += 1) {
        std.Thread.yield() catch {};
    }
    return flag.load(.acquire);
}

fn waitForWriterTables(manager: anytype) bool {
    var attempts: usize = 0;
    while ((manager.tables[0].write.held.load(.acquire) == 0 or
        manager.tables[1].write.held.load(.acquire) == 0) and attempts < PollLimit) : (attempts += 1)
    {
        std.Thread.yield() catch {};
    }
    return manager.tables[0].write.held.load(.acquire) != 0 and
        manager.tables[1].write.held.load(.acquire) != 0;
}

fn waitForTableWriter(manager: anytype, table: usize) bool {
    var attempts: usize = 0;
    while (manager.tables[table].write.held.load(.acquire) == 0 and attempts < PollLimit) : (attempts += 1) {
        std.Thread.yield() catch {};
    }
    return manager.tables[table].write.held.load(.acquire) != 0;
}

const PushKind = enum { pending, fresh, resident, loading, failed };

fn PushState(comptime Owner: type) type {
    return struct {
        manager: *Owner,
        gate: ?*StartGate = null,
        target: Owner.api.Target,
        uid: Owner.uid_type,
        bytes: u64,
        work: *Owner.Workspace,
        kind: PushKind = .pending,
        lease: Owner.api.Lease = undefined,
        allocation_number: u64 = 0,
        region: []u8 = &.{},
        done: std.atomic.Value(bool) = std.atomic.Value(bool).init(false),
    };
}

fn PushThread(comptime Owner: type) type {
    return struct {
        fn run(state: *PushState(Owner)) void {
            if (state.gate) |gate| gate.arriveAndWait();
            const result = state.manager.push(state.target, state.uid, state.bytes, state.work) catch {
                state.kind = .failed;
                state.done.store(true, .release);
                return;
            };
            switch (result) {
                .newly_allocated => |borrow| {
                    state.kind = .fresh;
                    state.lease = borrow.lease;
                    state.allocation_number = borrow.allocation_number;
                    state.region = borrow.region;
                },
                .in_memory => |borrow| {
                    state.kind = .resident;
                    state.lease = borrow.lease;
                    state.allocation_number = borrow.allocation_number;
                    state.region = borrow.region;
                },
                .loading => state.kind = .loading,
            }
            state.done.store(true, .release);
        }
    };
}

const LateEndKind = enum { pending, completed, force_released, failed };

fn LateEndState(comptime Owner: type) type {
    return struct {
        manager: *Owner,
        lease: Owner.api.Lease,
        allow: std.atomic.Value(bool) = std.atomic.Value(bool).init(false),
        ready: std.atomic.Value(bool) = std.atomic.Value(bool).init(false),
        done: std.atomic.Value(bool) = std.atomic.Value(bool).init(false),
        kind: LateEndKind = .pending,
    };
}

fn LateEndThread(comptime Owner: type) type {
    return struct {
        fn run(state: *LateEndState(Owner)) void {
            state.ready.store(true, .release);
            while (!state.allow.load(.acquire)) std.atomic.spinLoopHint();
            const result = state.manager.endUse(state.lease, .complete) catch {
                state.kind = .failed;
                state.done.store(true, .release);
                return;
            };
            state.kind = if (result == lifecycle.EndResult.force_released) .force_released else .completed;
            state.done.store(true, .release);
        }
    };
}

fn StableUseState(comptime Owner: type) type {
    return struct {
        manager: *Owner,
        uid: Owner.uid_type,
        expected_number: u64,
        expected_byte: u8,
        done: std.atomic.Value(bool) = std.atomic.Value(bool).init(false),
        found: bool = false,
        correct_region: bool = false,
    };
}

fn StableUseThread(comptime Owner: type) type {
    return struct {
        fn run(state: *StableUseState(Owner)) void {
            const result = state.manager.use(state.uid) catch {
                state.done.store(true, .release);
                return;
            };
            switch (result) {
                .in_memory => |borrow| {
                    state.found = true;
                    state.correct_region = borrow.allocation_number == state.expected_number and
                        borrow.region.len == 23 and borrow.region[0] == state.expected_byte;
                    _ = state.manager.endUse(borrow.lease, .complete) catch {
                        state.found = false;
                        state.done.store(true, .release);
                        return;
                    };
                },
                .not_in_memory, .loading => {},
            }
            state.done.store(true, .release);
        }
    };
}

fn UseAndViewState(comptime Owner: type) type {
    return struct {
        manager: *Owner,
        uid: Owner.uid_type,
        table: u32,
        expected_number: u64,
        expected_byte: u8,
        done: std.atomic.Value(bool) = std.atomic.Value(bool).init(false),
        found_correct: bool = false,
        view_completed: bool = false,
    };
}

fn UseAndViewThread(comptime Owner: type) type {
    return struct {
        fn run(state: *UseAndViewState(Owner)) void {
            const result = state.manager.use(state.uid) catch {
                state.done.store(true, .release);
                return;
            };
            switch (result) {
                .in_memory => |borrow| {
                    state.found_correct = borrow.allocation_number == state.expected_number and
                        borrow.region.len == 23 and borrow.region[0] == state.expected_byte;
                    _ = state.manager.endUse(borrow.lease, .complete) catch {
                        state.done.store(true, .release);
                        return;
                    };
                },
                .not_in_memory, .loading => {},
            }
            state.view_completed = (state.manager.view(state.table) catch null) != null;
            state.done.store(true, .release);
        }
    };
}

fn ForceReleaseState(comptime Owner: type) type {
    return struct {
        manager: *Owner,
        uid: Owner.uid_type,
        work: *Owner.Workspace,
        done: std.atomic.Value(bool) = std.atomic.Value(bool).init(false),
        failed: bool = false,
    };
}

fn ForceReleaseThread(comptime Owner: type) type {
    return struct {
        fn run(state: *ForceReleaseState(Owner)) void {
            state.manager.forceRelease(state.uid, state.work) catch {
                state.failed = true;
                state.done.store(true, .release);
                return;
            };
            state.done.store(true, .release);
        }
    };
}

fn RelocationUseState(comptime Owner: type) type {
    return struct {
        manager: *Owner,
        uid: Owner.uid_type,
        gate: *StartGate,
        entered: std.atomic.Value(bool) = std.atomic.Value(bool).init(false),
        done: std.atomic.Value(bool) = std.atomic.Value(bool).init(false),
        failed: bool = false,
        found: bool = false,
        not_resident: bool = false,
        ended: bool = false,
        force_released: bool = false,
        slot: u32 = types.no_slot,
        allocation_number: u64 = 0,
        region_length: usize = 0,
        first_byte: u8 = 0,
    };
}

fn RelocationUseThread(comptime Owner: type) type {
    return struct {
        fn run(state: *RelocationUseState(Owner)) void {
            state.gate.arriveAndWait();
            state.entered.store(true, .release);
            const result = state.manager.use(state.uid) catch {
                state.failed = true;
                state.done.store(true, .release);
                return;
            };
            switch (result) {
                .in_memory => |borrow| {
                    state.found = true;
                    state.slot = borrow.lease.slot;
                    state.allocation_number = borrow.allocation_number;
                    state.region_length = borrow.region.len;
                    if (borrow.region.len != 0) state.first_byte = borrow.region[0];
                    const ended = state.manager.endUse(borrow.lease, .complete) catch {
                        state.failed = true;
                        state.done.store(true, .release);
                        return;
                    };
                    state.ended = ended == lifecycle.EndResult.completed;
                    state.force_released = ended == lifecycle.EndResult.force_released;
                },
                .not_in_memory => state.not_resident = true,
                .loading => state.not_resident = true,
            }
            state.done.store(true, .release);
        }
    };
}

fn beginIndexPublication(comptime flavor: CandidateFlavor, transaction_value: anytype) void {
    if (flavor == .c2) {
        for (transaction_value.work.publications[0..transaction_value.home_count]) |publication| {
            transaction_value.index.beginPublishLocked(publication);
        }
    } else {
        transaction_value.index.beginPublishLocked(transaction_value.indices, transaction_value.versions);
        transaction_value.index.waitReadersLocked(transaction_value.indices);
    }
}

fn commitIndexDelta(comptime flavor: CandidateFlavor, transaction_value: anytype) void {
    if (flavor == .c2) {
        for (transaction_value.work.removals[0..transaction_value.removal_count]) |plan| {
            transaction_value.index.commitRemoveRetainingNodeLocked(plan);
        }
        for (transaction_value.work.inserts[0..transaction_value.insert_count]) |plan| {
            transaction_value.index.commitInsertRetainingNodeLocked(plan);
        }
    } else {
        for (transaction_value.work.removals[0..transaction_value.removal_count]) |plan| {
            transaction_value.index.commitRemoveLocked(plan);
        }
        for (transaction_value.work.inserts[0..transaction_value.insert_count]) |plan| {
            transaction_value.index.commitInsertRecomputedLocked(plan, transaction_value.indices);
        }
    }
}

fn finishIndexPublication(comptime flavor: CandidateFlavor, transaction_value: anytype) void {
    if (flavor == .c2) {
        for (transaction_value.work.publications[0..transaction_value.home_count]) |publication| {
            transaction_value.index.finishPublishLocked(publication);
        }
    } else {
        transaction_value.index.finishPublishLocked(transaction_value.indices, transaction_value.versions);
    }
}

fn indexPublicationIsOdd(comptime flavor: CandidateFlavor, transaction_value: anytype) bool {
    if (flavor == .c2) {
        if (transaction_value.home_count == 0) return false;
        for (transaction_value.work.publications[0..transaction_value.home_count]) |publication| {
            if (transaction_value.index.homes[publication.home_id].version.load(.seq_cst) & 1 == 0)
                return false;
        }
    } else {
        if (transaction_value.indices.len == 0) return false;
        for (transaction_value.indices) |index| {
            if (transaction_value.index.buckets[index].version.load(.seq_cst) & 1 == 0)
                return false;
        }
    }
    return true;
}

fn NotificationCapture(comptime Uid: type) type {
    return struct {
        const Self = @This();
        count: usize = 0,
        overflow: bool = false,
        notes: [32]types.For(Uid).Notification = undefined,

        fn callback(context: ?*anyopaque, uid: Uid, number: u64) callconv(.c) void {
            const self: *Self = @ptrCast(@alignCast(context orelse unreachable));
            if (self.count == self.notes.len) {
                self.overflow = true;
                return;
            }
            self.notes[self.count] = .{ .uid = uid, .allocation_number = number };
            self.count += 1;
        }

        fn saw(self: *const Self, uid: Uid, number: u64) bool {
            for (self.notes[0..self.count]) |note| {
                if (note.uid.eql(uid) and note.allocation_number == number) return true;
            }
            return false;
        }
    };
}

fn HeldUseState(comptime Owner: type) type {
    return struct {
        manager: *Owner,
        uid: Owner.uid_type,
        acquired: std.atomic.Value(bool) = std.atomic.Value(bool).init(false),
        release: std.atomic.Value(bool) = std.atomic.Value(bool).init(false),
        done: std.atomic.Value(bool) = std.atomic.Value(bool).init(false),
        failed: bool = false,
        ended: bool = false,
        lease: Owner.api.Lease = undefined,
        allocation_number: u64 = 0,
        region_length: usize = 0,
    };
}

fn HeldUseThread(comptime Owner: type) type {
    return struct {
        fn run(state: *HeldUseState(Owner)) void {
            const result = state.manager.use(state.uid) catch {
                state.failed = true;
                state.acquired.store(true, .release);
                state.done.store(true, .release);
                return;
            };
            switch (result) {
                .in_memory => |borrow| {
                    state.lease = borrow.lease;
                    state.allocation_number = borrow.allocation_number;
                    state.region_length = borrow.region.len;
                    state.acquired.store(true, .release);
                    while (!state.release.load(.acquire)) std.atomic.spinLoopHint();
                    const ended = state.manager.endUse(borrow.lease, .complete) catch {
                        state.failed = true;
                        state.done.store(true, .release);
                        return;
                    };
                    state.ended = ended == lifecycle.EndResult.completed;
                },
                .not_in_memory, .loading => {
                    state.failed = true;
                    state.acquired.store(true, .release);
                    state.done.store(true, .release);
                    return;
                },
            }
            state.done.store(true, .release);
        }
    };
}

const MutationOutcome = enum { pending, success, in_use, blocked, failed };

fn CloseMutationState(comptime Owner: type) type {
    return struct {
        manager: *Owner,
        uid: Owner.uid_type,
        work: *Owner.Workspace,
        done: std.atomic.Value(bool) = std.atomic.Value(bool).init(false),
        outcome: MutationOutcome = .pending,
    };
}

fn CloseMutationThread(comptime Owner: type) type {
    return struct {
        fn run(state: *CloseMutationState(Owner)) void {
            state.manager.close(state.uid, state.work) catch |err| {
                state.outcome = if (err == error.InUse) .in_use else if (err == error.Blocked) .blocked else .failed;
                state.done.store(true, .release);
                return;
            };
            state.outcome = .success;
            state.done.store(true, .release);
        }
    };
}

fn ReplaceMutationState(comptime Owner: type) type {
    return struct {
        manager: *Owner,
        uid: Owner.uid_type,
        attributes: types.Attributes,
        work: *Owner.Workspace,
        done: std.atomic.Value(bool) = std.atomic.Value(bool).init(false),
        outcome: MutationOutcome = .pending,
        created: bool = false,
    };
}

fn ReplaceMutationThread(comptime Owner: type) type {
    return struct {
        fn run(state: *ReplaceMutationState(Owner)) void {
            const result = state.manager.pushPartition(.{ .table = 0 }, state.uid, 8, state.attributes, state.work) catch |err| {
                state.outcome = if (err == error.InUse) .in_use else if (err == error.Blocked) .blocked else .failed;
                state.done.store(true, .release);
                return;
            };
            state.outcome = .success;
            state.created = result.created;
            state.done.store(true, .release);
        }
    };
}

const BlockingOwnerCallbackState = struct {
    entered: std.atomic.Value(bool) = std.atomic.Value(bool).init(false),
    release: std.atomic.Value(bool) = std.atomic.Value(bool).init(false),
    calls: std.atomic.Value(u32) = std.atomic.Value(u32).init(0),
};

fn ownerBlockingCallback(comptime Uid: type) *const fn (?*anyopaque, Uid, u64) callconv(.c) void {
    return struct {
        fn run(context: ?*anyopaque, _: Uid, _: u64) callconv(.c) void {
            const state: *BlockingOwnerCallbackState = @ptrCast(@alignCast(context orelse unreachable));
            _ = state.calls.fetchAdd(1, .release);
            state.entered.store(true, .release);
            while (!state.release.load(.acquire)) std.atomic.spinLoopHint();
        }
    }.run;
}

test "simultaneous same UID pushes across tables converge on one loading resource" {
    inline for (.{
        .{ .bits = 128, .flavor = CandidateFlavor.c2 },
        .{ .bits = 256, .flavor = CandidateFlavor.c2 },
        .{ .bits = 128, .flavor = CandidateFlavor.b2 },
        .{ .bits = 256, .flavor = CandidateFlavor.b2 },
    }) |case| {
        const F = Fixture(case.bits, case.flavor);
        var fixture = F{};
        try fixture.initialize();
        defer fixture.cleanup();

        const uid = key(F.UidType, 0x4455);
        var gate = StartGate{};
        var first = PushState(F.Owner){
            .manager = &fixture.ledger,
            .gate = &gate,
            .target = .{ .table = 0 },
            .uid = uid,
            .bytes = 37,
            .work = &fixture.work[0],
        };
        var second = PushState(F.Owner){
            .manager = &fixture.ledger,
            .gate = &gate,
            .target = .{ .table = 1 },
            .uid = uid,
            .bytes = 91,
            .work = &fixture.work[1],
        };

        var first_thread = try std.Thread.spawn(.{}, PushThread(F.Owner).run, .{&first});
        var second_thread = std.Thread.spawn(.{}, PushThread(F.Owner).run, .{&second}) catch |err| {
            gate.open.store(true, .release);
            first_thread.join();
            return err;
        };
        const both_ready = waitForReady(&gate, 2);
        gate.open.store(true, .release);
        first_thread.join();
        second_thread.join();

        const fresh_count: usize = @intFromBool(first.kind == .fresh) + @intFromBool(second.kind == .fresh);
        const loading_count: usize = @intFromBool(first.kind == .loading) + @intFromBool(second.kind == .loading);
        const fresh = if (first.kind == .fresh) &first else if (second.kind == .fresh) &second else null;
        const location = (try fixture.ledger.index.find(uid)) orelse return error.TestExpectedSingleMapping;
        const row = &fixture.ledger.rows[location.slot];
        const one_row = blk: {
            var matches: usize = 0;
            for (fixture.rows) |candidate| if (candidate.uid) |candidate_uid| {
                if (candidate_uid.eql(uid)) matches += 1;
            };
            break :blk matches == 1;
        };
        const matches_fresh = if (fresh) |borrow| location.table == @as(u32, if (borrow == &first) 0 else 1) and
            row.resource.generation == borrow.allocation_number and row.resource.loading and row.resource.use_count == 1 else false;
        if (fresh) |borrow| _ = try fixture.ledger.endUse(borrow.lease, .complete);
        const resident = try fixture.ledger.use(uid);
        var resident_correct = false;
        switch (resident) {
            .in_memory => |borrow| {
                resident_correct = if (fresh) |winner|
                    borrow.allocation_number == winner.allocation_number and borrow.region.len == winner.bytes
                else
                    false;
                _ = try fixture.ledger.endUse(borrow.lease, .complete);
            },
            .not_in_memory, .loading => {},
        }

        try std.testing.expect(both_ready);
        try std.testing.expectEqual(@as(usize, 1), fresh_count);
        try std.testing.expectEqual(@as(usize, 1), loading_count);
        try std.testing.expect(one_row and matches_fresh and resident_correct);
    }
}

test "late old endUse cannot decrement the replacement after forceRelease" {
    inline for (.{
        .{ .bits = 128, .flavor = CandidateFlavor.c2 },
        .{ .bits = 256, .flavor = CandidateFlavor.c2 },
        .{ .bits = 128, .flavor = CandidateFlavor.b2 },
        .{ .bits = 256, .flavor = CandidateFlavor.b2 },
    }) |case| {
        const F = Fixture(case.bits, case.flavor);
        var fixture = F{};
        try fixture.initialize();
        defer fixture.cleanup();

        const uid = key(F.UidType, 0x6677);
        const old = switch (try fixture.ledger.push(.{ .table = 0 }, uid, 41, &fixture.work[0])) {
            .newly_allocated => |borrow| borrow,
            .in_memory => return error.TestExpectedFreshLoad,
            .loading => return error.TestExpectedFreshLoad,
        };
        var late = LateEndState(F.Owner){ .manager = &fixture.ledger, .lease = old.lease };
        var late_thread = try std.Thread.spawn(.{}, LateEndThread(F.Owner).run, .{&late});

        const ready = waitForDone(&late.ready);
        fixture.ledger.forceRelease(uid, &fixture.work[0]) catch |err| {
            late.allow.store(true, .release);
            late_thread.join();
            return err;
        };
        var replacement: ?F.Owner.api.Borrow = null;
        const pushed = fixture.ledger.push(.{ .table = 0 }, uid, 29, &fixture.work[0]) catch |err| {
            late.allow.store(true, .release);
            late_thread.join();
            return err;
        };
        switch (pushed) {
            .newly_allocated => |borrow| replacement = borrow,
            .in_memory, .loading => {},
        }

        late.allow.store(true, .release);
        late_thread.join();

        try std.testing.expect(ready);
        const new_borrow = replacement orelse return error.TestExpectedReplacementLoad;
        try std.testing.expect(new_borrow.allocation_number > old.allocation_number);
        try std.testing.expectEqual(old.lease.slot, new_borrow.lease.slot);
        try std.testing.expectEqual(LateEndKind.force_released, late.kind);
        const new_row = &fixture.ledger.rows[new_borrow.lease.slot];
        try std.testing.expectEqual(new_borrow.allocation_number, new_row.resource.generation);
        try std.testing.expectEqual(@as(u64, 1), new_row.resource.use_count);
        try std.testing.expect(new_row.resource.loading);
        _ = try fixture.ledger.endUse(new_borrow.lease, .complete);
        const current = try fixture.ledger.use(uid);
        switch (current) {
            .in_memory => |borrow| {
                try std.testing.expectEqual(new_borrow.allocation_number, borrow.allocation_number);
                try std.testing.expectEqual(@as(usize, 29), borrow.region.len);
                _ = try fixture.ledger.endUse(borrow.lease, .complete);
            },
            .not_in_memory, .loading => return error.TestExpectedReplacementResident,
        }
    }
}

test "shared-home writers across tables leave an unrelated stable resource usable" {
    inline for (.{
        .{ .bits = 128, .flavor = CandidateFlavor.c2 },
        .{ .bits = 256, .flavor = CandidateFlavor.c2 },
        .{ .bits = 128, .flavor = CandidateFlavor.b2 },
        .{ .bits = 256, .flavor = CandidateFlavor.b2 },
    }) |case| {
        const F = Fixture(case.bits, case.flavor);
        var fixture = F{};
        try fixture.initialize();
        defer fixture.cleanup();
        const manager = &fixture.ledger;

        const first_uid = key(F.UidType, 1001);
        const shared_home = try manager.index.homeFor(first_uid);
        var second_uid: F.UidType = undefined;
        var found_second = false;
        for (0..4096) |offset| {
            const candidate = key(F.UidType, 1002 + @as(u64, @intCast(offset)));
            if (candidate.eql(first_uid)) continue;
            if (try manager.index.homeFor(candidate) == shared_home) {
                second_uid = candidate;
                found_second = true;
                break;
            }
        }
        try std.testing.expect(found_second);

        var stable_uid: F.UidType = undefined;
        var found_stable = false;
        for (0..4096) |offset| {
            const candidate = key(F.UidType, 20_000 + @as(u64, @intCast(offset)));
            const home = try manager.index.homeFor(candidate);
            const distance = (home + 64 - shared_home) % 64;
            if (distance > 4 and distance < 60) {
                stable_uid = candidate;
                found_stable = true;
                break;
            }
        }
        try std.testing.expect(found_stable);

        const stable_load = switch (try manager.push(.{ .table = 0 }, stable_uid, 23, &fixture.work[0])) {
            .newly_allocated => |borrow| borrow,
            .in_memory => return error.TestExpectedStableFresh,
            .loading => return error.TestExpectedStableFresh,
        };
        stable_load.region[0] = 0x6d;
        _ = try manager.endUse(stable_load.lease, .complete);

        const first_blocked_slot: usize = 9;
        const second_blocked_slot: usize = 16;
        manager.rows[first_blocked_slot].lock.lock();
        manager.rows[second_blocked_slot].lock.lock();
        var first = PushState(F.Owner){
            .manager = manager,
            .target = .{ .table = 0 },
            .uid = first_uid,
            .bytes = 31,
            .work = &fixture.work[0],
        };
        var second = PushState(F.Owner){
            .manager = manager,
            .target = .{ .table = 1 },
            .uid = second_uid,
            .bytes = 47,
            .work = &fixture.work[1],
        };
        var first_thread = std.Thread.spawn(.{}, PushThread(F.Owner).run, .{&first}) catch |err| {
            manager.rows[second_blocked_slot].lock.unlock();
            manager.rows[first_blocked_slot].lock.unlock();
            return err;
        };
        var second_thread = std.Thread.spawn(.{}, PushThread(F.Owner).run, .{&second}) catch |err| {
            manager.rows[second_blocked_slot].lock.unlock();
            manager.rows[first_blocked_slot].lock.unlock();
            first_thread.join();
            return err;
        };

        const writers_entered_tables = waitForWriterTables(manager);
        manager.index.lockHome(shared_home) catch |err| {
            manager.rows[second_blocked_slot].lock.unlock();
            manager.rows[first_blocked_slot].lock.unlock();
            first_thread.join();
            second_thread.join();
            return err;
        };
        manager.rows[second_blocked_slot].lock.unlock();
        manager.rows[first_blocked_slot].lock.unlock();

        var stable = StableUseState(F.Owner){
            .manager = manager,
            .uid = stable_uid,
            .expected_number = stable_load.allocation_number,
            .expected_byte = 0x6d,
        };
        var stable_thread = std.Thread.spawn(.{}, StableUseThread(F.Owner).run, .{&stable}) catch |err| {
            manager.index.unlockHome(shared_home);
            first_thread.join();
            second_thread.join();
            return err;
        };
        const stable_finished_while_home_locked = waitForDone(&stable.done);
        const writers_stayed_blocked = !first.done.load(.acquire) and !second.done.load(.acquire);
        manager.index.unlockHome(shared_home);
        first_thread.join();
        second_thread.join();
        stable_thread.join();

        try std.testing.expect(writers_entered_tables);
        try std.testing.expect(writers_stayed_blocked);
        try std.testing.expect(stable_finished_while_home_locked);
        try std.testing.expect(stable.found and stable.correct_region);
        try std.testing.expectEqual(PushKind.fresh, first.kind);
        try std.testing.expectEqual(PushKind.fresh, second.kind);

        first.region[0] = 0x31;
        second.region[0] = 0x42;
        const first_location = (try manager.index.find(first_uid)) orelse return error.TestExpectedFirstWriter;
        const second_location = (try manager.index.find(second_uid)) orelse return error.TestExpectedSecondWriter;
        try std.testing.expectEqual(@as(u32, 0), first_location.table);
        try std.testing.expectEqual(@as(u32, 1), second_location.table);
        try std.testing.expectEqual(first.allocation_number, manager.rows[first_location.slot].resource.generation);
        try std.testing.expectEqual(second.allocation_number, manager.rows[second_location.slot].resource.generation);
        _ = try manager.endUse(first.lease, .complete);
        _ = try manager.endUse(second.lease, .complete);

        switch (try manager.use(first_uid)) {
            .in_memory => |borrow| {
                try std.testing.expectEqual(@as(u8, 0x31), borrow.region[0]);
                _ = try manager.endUse(borrow.lease, .complete);
            },
            .not_in_memory, .loading => return error.TestExpectedFirstWriterResident,
        }
        switch (try manager.use(second_uid)) {
            .in_memory => |borrow| {
                try std.testing.expectEqual(@as(u8, 0x42), borrow.region[0]);
                _ = try manager.endUse(borrow.lease, .complete);
            },
            .not_in_memory, .loading => return error.TestExpectedSecondWriterResident,
        }
    }
}

test "last borrowed resource blocks close and whole-tree replacement without partial release" {
    inline for (.{
        .{ .bits = 128, .flavor = CandidateFlavor.c2 },
        .{ .bits = 256, .flavor = CandidateFlavor.c2 },
        .{ .bits = 128, .flavor = CandidateFlavor.b2 },
        .{ .bits = 256, .flavor = CandidateFlavor.b2 },
    }) |case| {
        const F = Fixture(case.bits, case.flavor);
        var fixture = F{};
        try fixture.initialize();
        defer fixture.cleanup();
        const manager = &fixture.ledger;
        var capture = NotificationCapture(F.UidType){};
        _ = try manager.subscribe(0, NotificationCapture(F.UidType).callback, &capture);

        const root_uid = key(F.UidType, 301);
        const child_uid = key(F.UidType, 302);
        const replacement_uid = key(F.UidType, 303);
        const root = try manager.pushPartition(.{ .table = 0 }, root_uid, 8, attrs, &fixture.work[0]);
        const child = try manager.pushPartition(.{ .partition = root_uid }, child_uid, 8, attrs, &fixture.work[0]);
        try std.testing.expect(root.created and child.created);

        var resource_uids: [8]F.UidType = undefined;
        var expected: [10]F.Owner.api.Notification = undefined;
        expected[0] = .{ .uid = root_uid, .allocation_number = root.allocation_number };
        expected[1] = .{ .uid = child_uid, .allocation_number = child.allocation_number };
        for (0..resource_uids.len) |i| {
            const resource_uid = key(F.UidType, 400 + @as(u64, @intCast(i)));
            resource_uids[i] = resource_uid;
            const loaded = switch (try manager.push(.{ .partition = child_uid }, resource_uid, 17, &fixture.work[0])) {
                .newly_allocated => |borrow| borrow,
                .in_memory => return error.TestExpectedFreshTreeResource,
                .loading => return error.TestExpectedFreshTreeResource,
            };
            loaded.region[0] = @intCast(i + 1);
            expected[i + 2] = .{ .uid = resource_uid, .allocation_number = loaded.allocation_number };
            _ = try manager.endUse(loaded.lease, .complete);
        }
        const before = try manager.view(0);

        var held = HeldUseState(F.Owner){ .manager = manager, .uid = resource_uids[resource_uids.len - 1] };
        var holder_thread = try std.Thread.spawn(.{}, HeldUseThread(F.Owner).run, .{&held});
        const holder_ready = waitForDone(&held.acquired);
        if (!holder_ready) {
            held.release.store(true, .release);
            holder_thread.join();
            return error.TestExpectedHeldResourceBorrow;
        }

        var close = CloseMutationState(F.Owner){ .manager = manager, .uid = root_uid, .work = &fixture.work[0] };
        var close_thread = std.Thread.spawn(.{}, CloseMutationThread(F.Owner).run, .{&close}) catch |err| {
            held.release.store(true, .release);
            holder_thread.join();
            return err;
        };
        const close_finished = waitForDone(&close.done);
        if (!close_finished) {
            held.release.store(true, .release);
            close_thread.join();
            holder_thread.join();
            return error.TestExpectedCloseToReturnWhileBorrowed;
        }
        close_thread.join();

        var replace = ReplaceMutationState(F.Owner){
            .manager = manager,
            .uid = replacement_uid,
            .attributes = attrs,
            .work = &fixture.work[0],
        };
        var replace_thread = std.Thread.spawn(.{}, ReplaceMutationThread(F.Owner).run, .{&replace}) catch |err| {
            held.release.store(true, .release);
            holder_thread.join();
            return err;
        };
        const replace_finished = waitForDone(&replace.done);
        if (!replace_finished) {
            held.release.store(true, .release);
            replace_thread.join();
            holder_thread.join();
            return error.TestExpectedReplacementToReturnWhileBorrowed;
        }
        replace_thread.join();

        var retained_resources = true;
        for (resource_uids[0 .. resource_uids.len - 1], 0..) |resource_uid, i| {
            const use = manager.use(resource_uid) catch {
                retained_resources = false;
                continue;
            };
            switch (use) {
                .in_memory => |borrow| {
                    if (borrow.region[0] != @as(u8, @intCast(i + 1))) retained_resources = false;
                    const ended = manager.endUse(borrow.lease, .complete) catch {
                        retained_resources = false;
                        continue;
                    };
                    if (ended != lifecycle.EndResult.completed) retained_resources = false;
                },
                .not_in_memory, .loading => retained_resources = false,
            }
        }
        const after_blocked_mutations = manager.view(0) catch before;
        const root_present = (manager.index.find(root_uid) catch null) != null;
        const child_present = (manager.index.find(child_uid) catch null) != null;
        const replacement_absent = (manager.index.find(replacement_uid) catch null) == null;
        const final_borrow_matches = !held.failed and held.lease.slot < manager.rows.len and
            held.allocation_number == expected[expected.len - 1].allocation_number and held.region_length == 17;
        const no_notices_before_release = capture.count == 0 and fixture.work[0].note_count == 0;
        held.release.store(true, .release);
        holder_thread.join();

        try std.testing.expect(close_finished and replace_finished);
        try std.testing.expectEqual(MutationOutcome.in_use, close.outcome);
        try std.testing.expectEqual(MutationOutcome.blocked, replace.outcome);
        try std.testing.expect(!replace.created);
        try std.testing.expect(final_borrow_matches and held.ended and !held.failed);
        try std.testing.expect(retained_resources);
        try std.testing.expectEqualDeep(before, after_blocked_mutations);
        try std.testing.expect(root_present and child_present and replacement_absent);
        try std.testing.expect(no_notices_before_release);

        const replacement = try manager.pushPartition(.{ .table = 0 }, replacement_uid, 8, attrs, &fixture.work[0]);
        try std.testing.expect(replacement.created);
        try std.testing.expect((try manager.index.find(root_uid)) == null);
        try std.testing.expect((try manager.index.find(child_uid)) == null);
        for (resource_uids, 0..) |resource_uid, i| {
            try std.testing.expect((try manager.index.find(resource_uid)) == null);
            try std.testing.expect(capture.saw(expected[i + 2].uid, expected[i + 2].allocation_number));
        }
        try std.testing.expect(capture.saw(root_uid, root.allocation_number));
        try std.testing.expect(capture.saw(child_uid, child.allocation_number));
        try std.testing.expectEqual(expected.len, capture.count);
        try std.testing.expect(!capture.overflow);
        try std.testing.expectEqual(@as(u64, 0), (try manager.view(0)).system_page_bytes);
        try std.testing.expectEqual(@as(u64, 0), (try manager.view(0)).registered_bytes);
    }
}

test "reader released during index publication gets the relocated resource consistently" {
    inline for (.{
        .{ .bits = 128, .flavor = CandidateFlavor.c2 },
        .{ .bits = 256, .flavor = CandidateFlavor.c2 },
        .{ .bits = 128, .flavor = CandidateFlavor.b2 },
        .{ .bits = 256, .flavor = CandidateFlavor.b2 },
    }) |case| {
        const F = Fixture(case.bits, case.flavor);
        var fixture = F{};
        try fixture.initialize();
        defer fixture.cleanup();

        const manager = &fixture.ledger;
        const uid = key(F.UidType, 0x9a77);
        const original = switch (try manager.push(.{ .table = 0 }, uid, 23, &fixture.work[0])) {
            .newly_allocated => |borrow| borrow,
            .in_memory => return error.TestExpectedFreshRelocationResource,
            .loading => return error.TestExpectedFreshRelocationResource,
        };
        @memset(original.region, 0x5c);
        _ = try manager.endUse(original.lease, .complete);

        const old_location = (try manager.index.find(uid)) orelse return error.TestExpectedRelocationMapping;
        if (old_location.kind != .resource or old_location.table != 0)
            return error.TestExpectedTableZeroResource;
        const old_slot = old_location.slot;
        const new_slot = old_slot + 1;
        if (new_slot >= 16) return error.TestExpectedRelocationDestination;
        const old_row = &manager.rows[old_slot];
        const new_row = &manager.rows[new_slot];
        if (old_row.uid == null or !old_row.uid.?.eql(uid) or new_row.uid != null or
            new_row.fixed_declaration != null or new_row.resource.registration != null)
        {
            return error.TestExpectedEmptyRelocationDestination;
        }

        manager.tables[0].write.lock();
        old_row.lock.lock();
        new_row.lock.lock();
        var owner_locks_held = true;
        defer if (owner_locks_held) {
            new_row.lock.unlock();
            old_row.lock.unlock();
            manager.tables[0].write.unlock();
        };

        var empty_resource = try @TypeOf(old_row.resource).dynamic(0);
        empty_resource.registration = null;
        const work = &fixture.work[0];
        work.remove_ids[0] = old_slot;
        work.puts[0] = .{
            .node_id = new_slot,
            .uid = uid,
            .location = .{ .kind = .resource, .table = 0, .slot = new_slot },
        };
        var transaction_value = try F.IndexType.Transactions.prepare(
            &manager.index,
            work.remove_ids[0..1],
            work.puts[0..1],
            &work.index_workspace,
        );
        defer transaction_value.abort();

        var gate = StartGate{};
        var reader = RelocationUseState(F.Owner){ .manager = manager, .uid = uid, .gate = &gate };
        var reader_thread = try std.Thread.spawn(.{}, RelocationUseThread(F.Owner).run, .{&reader});
        const reader_reached_gate = waitForReady(&gate, 1);
        if (!reader_reached_gate) {
            // A delayed reader may enter use as soon as the gate opens.
            // Return every lock before joining that reader on this failure path.
            transaction_value.abort();
            new_row.lock.unlock();
            old_row.lock.unlock();
            manager.tables[0].write.unlock();
            owner_locks_held = false;
            gate.open.store(true, .release);
            reader_thread.join();
            return error.TestExpectedRelocationReaderGate;
        }

        beginIndexPublication(case.flavor, &transaction_value);
        const publication_was_odd = indexPublicationIsOdd(case.flavor, &transaction_value);
        gate.open.store(true, .release);
        const reader_entered_use_while_publication_held = waitForDone(&reader.entered);
        const reader_remained_pending_while_publication_odd = publication_was_odd and
            !reader.done.load(.acquire) and indexPublicationIsOdd(case.flavor, &transaction_value);

        const moved_resource = old_row.resource;
        const moved_parent = old_row.parent;
        const moved_attributes = old_row.attributes;
        new_row.uid = uid;
        new_row.parent = moved_parent;
        new_row.fixed_declaration = null;
        new_row.fixed_parent_number = old_row.fixed_parent_number;
        new_row.attributes = moved_attributes;
        new_row.resource = moved_resource;
        old_row.uid = null;
        old_row.parent = null;
        old_row.fixed_declaration = null;
        old_row.fixed_parent_number = 0;
        old_row.attributes = F.Owner.table_declarations[0].attributes;
        old_row.resource = empty_resource;

        commitIndexDelta(case.flavor, &transaction_value);
        finishIndexPublication(case.flavor, &transaction_value);
        transaction_value.abort();
        new_row.lock.unlock();
        old_row.lock.unlock();
        manager.tables[0].write.unlock();
        owner_locks_held = false;
        reader_thread.join();

        const current_location = (try manager.index.find(uid)) orelse return error.TestExpectedRelocatedMapping;
        var row_matches = current_location.kind == .resource and current_location.table == 0 and
            current_location.slot == new_slot and new_row.uid != null and new_row.uid.?.eql(uid) and
            new_row.resource.generation == original.allocation_number and old_row.uid == null;
        var duplicate_rows: usize = 0;
        for (manager.rows) |candidate| {
            if (candidate.uid) |candidate_uid| {
                if (candidate_uid.eql(uid)) duplicate_rows += 1;
            }
        }
        row_matches = row_matches and duplicate_rows == 1;

        try std.testing.expect(reader_entered_use_while_publication_held);
        try std.testing.expect(reader_remained_pending_while_publication_odd);
        try std.testing.expect(row_matches);
        try std.testing.expect(reader.found and !reader.not_resident and !reader.failed);
        try std.testing.expectEqual(new_slot, reader.slot);
        try std.testing.expectEqual(original.allocation_number, reader.allocation_number);
        try std.testing.expectEqual(@as(usize, 23), reader.region_length);
        try std.testing.expectEqual(@as(u8, 0x5c), reader.first_byte);
        try std.testing.expect(reader.ended and !reader.force_released);
    }
}

test "unrelated resident use progresses while allocation release and tree close hold table writer" {
    inline for (.{
        .{ .bits = 128, .flavor = CandidateFlavor.c2 },
        .{ .bits = 256, .flavor = CandidateFlavor.c2 },
        .{ .bits = 128, .flavor = CandidateFlavor.b2 },
        .{ .bits = 256, .flavor = CandidateFlavor.b2 },
    }) |case| {
        const F = Fixture(case.bits, case.flavor);
        var fixture = F{};
        try fixture.initialize();
        defer fixture.cleanup();
        const manager = &fixture.ledger;

        const stable_uid = key(F.UidType, 80_001);
        const stable = switch (try manager.push(.{ .table = 0 }, stable_uid, 23, &fixture.work[0])) {
            .newly_allocated => |borrow| borrow,
            .in_memory => return error.TestExpectedStableFresh,
            .loading => return error.TestExpectedStableFresh,
        };
        stable.region[0] = 0x72;
        _ = try manager.endUse(stable.lease, .complete);

        const allocation_uid = key(F.UidType, 80_002);
        const allocation_slot: usize = 9;
        manager.rows[allocation_slot].lock.lock();
        var allocation = PushState(F.Owner){
            .manager = manager,
            .target = .{ .table = 0 },
            .uid = allocation_uid,
            .bytes = 19,
            .work = &fixture.work[0],
        };
        var allocation_thread = std.Thread.spawn(.{}, PushThread(F.Owner).run, .{&allocation}) catch |err| {
            manager.rows[allocation_slot].lock.unlock();
            return err;
        };
        const allocation_owns_table = waitForTableWriter(manager, 0);
        var allocation_probe = StableUseState(F.Owner){
            .manager = manager,
            .uid = stable_uid,
            .expected_number = stable.allocation_number,
            .expected_byte = 0x72,
        };
        var allocation_probe_thread = std.Thread.spawn(.{}, StableUseThread(F.Owner).run, .{&allocation_probe}) catch |err| {
            manager.rows[allocation_slot].lock.unlock();
            allocation_thread.join();
            return err;
        };
        const allocation_probe_completed_while_writer_held = waitForDone(&allocation_probe.done);
        manager.rows[allocation_slot].lock.unlock();
        allocation_thread.join();
        allocation_probe_thread.join();
        const allocation_was_fresh = allocation.kind == .fresh;
        if (allocation_was_fresh) _ = try manager.endUse(allocation.lease, .complete);
        try std.testing.expect(allocation_owns_table);
        try std.testing.expect(allocation_probe_completed_while_writer_held);
        try std.testing.expect(allocation_probe.found and allocation_probe.correct_region);
        try std.testing.expect(allocation_was_fresh);

        manager.rows[allocation_slot].lock.lock();
        var release = ForceReleaseState(F.Owner){
            .manager = manager,
            .uid = allocation_uid,
            .work = &fixture.work[0],
        };
        var release_thread = std.Thread.spawn(.{}, ForceReleaseThread(F.Owner).run, .{&release}) catch |err| {
            manager.rows[allocation_slot].lock.unlock();
            return err;
        };
        const release_owns_table = waitForTableWriter(manager, 0);
        var release_probe = StableUseState(F.Owner){
            .manager = manager,
            .uid = stable_uid,
            .expected_number = stable.allocation_number,
            .expected_byte = 0x72,
        };
        var release_probe_thread = std.Thread.spawn(.{}, StableUseThread(F.Owner).run, .{&release_probe}) catch |err| {
            manager.rows[allocation_slot].lock.unlock();
            release_thread.join();
            return err;
        };
        const release_probe_completed_while_writer_held = waitForDone(&release_probe.done);
        manager.rows[allocation_slot].lock.unlock();
        release_thread.join();
        release_probe_thread.join();
        try std.testing.expect(release_owns_table);
        try std.testing.expect(release_probe_completed_while_writer_held);
        try std.testing.expect(release_probe.found and release_probe.correct_region);
        try std.testing.expect(release.done.load(.acquire) and !release.failed);
        try std.testing.expect((try manager.index.find(allocation_uid)) == null);

        const root_uid = key(F.UidType, 80_003);
        const child_uid = key(F.UidType, 80_004);
        const nested_resource_uid = key(F.UidType, 80_005);
        _ = try manager.pushPartition(.{ .table = 0 }, root_uid, 8, attrs, &fixture.work[0]);
        _ = try manager.pushPartition(.{ .partition = root_uid }, child_uid, 4, attrs, &fixture.work[0]);
        const nested_resource = switch (try manager.push(.{ .partition = child_uid }, nested_resource_uid, 17, &fixture.work[0])) {
            .newly_allocated => |borrow| borrow,
            .in_memory => return error.TestExpectedNestedFresh,
            .loading => return error.TestExpectedNestedFresh,
        };
        nested_resource.region[0] = 0x45;
        const nested_slot = nested_resource.lease.slot;
        _ = try manager.endUse(nested_resource.lease, .complete);

        manager.rows[nested_slot].lock.lock();
        var tree_close = CloseMutationState(F.Owner){ .manager = manager, .uid = root_uid, .work = &fixture.work[0] };
        var tree_close_thread = std.Thread.spawn(.{}, CloseMutationThread(F.Owner).run, .{&tree_close}) catch |err| {
            manager.rows[nested_slot].lock.unlock();
            return err;
        };
        const tree_owns_table = waitForTableWriter(manager, 0);
        var tree_probe = StableUseState(F.Owner){
            .manager = manager,
            .uid = stable_uid,
            .expected_number = stable.allocation_number,
            .expected_byte = 0x72,
        };
        var tree_probe_thread = std.Thread.spawn(.{}, StableUseThread(F.Owner).run, .{&tree_probe}) catch |err| {
            manager.rows[nested_slot].lock.unlock();
            tree_close_thread.join();
            return err;
        };
        const tree_probe_completed_while_writer_held = waitForDone(&tree_probe.done);
        manager.rows[nested_slot].lock.unlock();
        tree_close_thread.join();
        tree_probe_thread.join();

        try std.testing.expect(tree_owns_table);
        try std.testing.expect(tree_probe_completed_while_writer_held);
        try std.testing.expect(tree_probe.found and tree_probe.correct_region);
        try std.testing.expectEqual(MutationOutcome.success, tree_close.outcome);
        try std.testing.expect((try manager.index.find(root_uid)) == null);
        try std.testing.expect((try manager.index.find(child_uid)) == null);
        try std.testing.expect((try manager.index.find(nested_resource_uid)) == null);
    }
}

test "owner dispatch runs outside table row and shared index home locks" {
    inline for (.{
        .{ .bits = 128, .flavor = CandidateFlavor.c2 },
        .{ .bits = 256, .flavor = CandidateFlavor.c2 },
        .{ .bits = 128, .flavor = CandidateFlavor.b2 },
        .{ .bits = 256, .flavor = CandidateFlavor.b2 },
    }) |case| {
        const F = Fixture(case.bits, case.flavor);
        var fixture = F{};
        try fixture.initialize();
        defer fixture.cleanup();
        const manager = &fixture.ledger;

        const removed_uid = key(F.UidType, 90_001);
        const shared_home = try manager.index.homeFor(removed_uid);
        var stable_uid: F.UidType = undefined;
        var found_stable = false;
        for (0..4096) |offset| {
            const candidate = key(F.UidType, 90_002 + @as(u64, @intCast(offset)));
            if (try manager.index.homeFor(candidate) == shared_home) {
                stable_uid = candidate;
                found_stable = true;
                break;
            }
        }
        try std.testing.expect(found_stable);

        const removed = switch (try manager.push(.{ .table = 0 }, removed_uid, 19, &fixture.work[0])) {
            .newly_allocated => |borrow| borrow,
            .in_memory => return error.TestExpectedRemovedFresh,
            .loading => return error.TestExpectedRemovedFresh,
        };
        removed.region[0] = 0xa1;
        _ = try manager.endUse(removed.lease, .complete);
        const stable = switch (try manager.push(.{ .table = 1 }, stable_uid, 23, &fixture.work[1])) {
            .newly_allocated => |borrow| borrow,
            .in_memory => return error.TestExpectedSharedHomeFresh,
            .loading => return error.TestExpectedSharedHomeFresh,
        };
        stable.region[0] = 0xb2;
        _ = try manager.endUse(stable.lease, .complete);

        var callback = BlockingOwnerCallbackState{};
        const subscription = try manager.subscribe(0, ownerBlockingCallback(F.UidType), &callback);
        var release = ForceReleaseState(F.Owner){ .manager = manager, .uid = removed_uid, .work = &fixture.work[0] };
        var release_thread = try std.Thread.spawn(.{}, ForceReleaseThread(F.Owner).run, .{&release});
        if (!waitForFlag(&callback.entered)) {
            callback.release.store(true, .release);
            release_thread.join();
            try manager.unsubscribe(subscription);
            return error.TestExpectedBlockingNotification;
        }

        var probe = UseAndViewState(F.Owner){
            .manager = manager,
            .uid = stable_uid,
            .table = 0,
            .expected_number = stable.allocation_number,
            .expected_byte = 0xb2,
        };
        var probe_thread = std.Thread.spawn(.{}, UseAndViewThread(F.Owner).run, .{&probe}) catch |err| {
            callback.release.store(true, .release);
            release_thread.join();
            try manager.unsubscribe(subscription);
            return err;
        };
        const probe_completed_during_callback = waitForDone(&probe.done);
        callback.release.store(true, .release);
        release_thread.join();
        probe_thread.join();
        try manager.unsubscribe(subscription);

        try std.testing.expect(probe_completed_during_callback);
        try std.testing.expect(probe.found_correct and probe.view_completed);
        try std.testing.expectEqual(@as(u32, 1), callback.calls.load(.acquire));
        try std.testing.expect(release.done.load(.acquire) and !release.failed);
        try std.testing.expect((try manager.index.find(removed_uid)) == null);
        const stable_location = (try manager.index.find(stable_uid)) orelse return error.TestExpectedSharedHomeMapping;
        try std.testing.expectEqual(@as(u32, 1), stable_location.table);
        try std.testing.expectEqual(stable.lease.slot, stable_location.slot);
    }
}
