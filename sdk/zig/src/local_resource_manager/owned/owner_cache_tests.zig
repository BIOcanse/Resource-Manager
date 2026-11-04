//! Native window-borrow regressions. No analysis markers or payload access hooks.
const std = @import("std");
const types = @import("types.zig");
const uid_module = @import("../uid.zig");
const index_module = @import("index.zig");
const notifications = @import("notifications.zig");
const owner_module = @import("owner.zig");

const attrs: types.Attributes = .{ .recoverability = .recoverable, .recovery_cost_coefficient = 1, .access_loss_impact = .unobservable_now };
const policy: types.Policy = .{
    .score_epochs = 8,
    .idle_epochs = 3,
    .protection = .concentrated,
    .concentrated_trigger_percent = 0,
    .concentrated_target_percent = 50,
    .smooth_trigger_percent = 50,
    .smooth_emergency_percent = 10,
    .smooth_interval_epochs = 2,
    .smooth_release_count = 1,
    .tick_maximum_actions = 16,
    .release_check = .{ .enabled = false },
};

fn key(comptime U: type, number: u64) U {
    var result: U = .{ .bytes = @splat(0) };
    std.mem.writeInt(u64, result.bytes[result.bytes.len - 8 ..][0..8], number, .big);
    return result;
}

fn Fixture(comptime bits: u16, comptime enabled: bool, comptime slots: u32) type {
    return WithRegionFixture(bits, enabled, slots, @import("../memory_region.zig").Region);
}

fn WithRegionFixture(comptime bits: u16, comptime enabled: bool, comptime slots: u32, comptime Region: type) type {
    return PolicyFixture(bits, enabled, slots, Region, policy);
}

/// One Tick action per table: each pending-cleanup attempt visibly uses the whole budget.
const one_action_policy: types.Policy = blk: {
    var value = policy;
    value.tick_maximum_actions = 1;
    break :blk value;
};

fn PolicyFixture(comptime bits: u16, comptime enabled: bool, comptime slots: u32, comptime Region: type, comptime table_policy: types.Policy) type {
    const U = uid_module.Identifier(bits);
    const I = index_module.Candidate(U, .c2_chain);
    const declarations = [_]types.Table{.{ .capacity = 16, .partition_capacity = 8, .reserved_start = 0, .reserved_count = 12, .attributes = attrs, .policy = table_policy }};
    const fixed = [_]types.For(U).Fixed{
        .{ .table = 0, .parent = null, .ordinal = 15, .uid = key(U, 1), .allocation_bytes = 31, .attributes = null },
        .{ .table = 0, .parent = key(U, 20), .ordinal = 11, .uid = key(U, 21), .allocation_bytes = 31, .attributes = null },
        .{ .table = 0, .parent = key(U, 30), .ordinal = 11, .uid = key(U, 31), .allocation_bytes = 31, .attributes = null },
    };
    const O = owner_module.WithRegion(.{ .uid_width = bits, .activity_width = 8, .index_buckets = 32, .use_window_cache = enabled, .use_window_cache_slots = slots, .development_lease_analysis = false }, &declarations, &fixed, I, Region);
    return struct {
        pub const Ledger = O;
        pub const Uid = U;
        rows: [16]O.Row = undefined,
        shells: [8]O.Shell = undefined,
        states: [1]O.TableState = undefined,
        bindings: [3]O.FixedBinding = undefined,
        nodes: [24]I.Node = undefined,
        homes: [32]I.Home = undefined,
        subscriptions: [0]notifications.Notifier(U).Record = .{},
        candidates: [16]O.Candidate = undefined,
        captures: [16]O.Candidate = undefined,
        shell_work: [8]u32 = undefined,
        home_work: [64]u32 = undefined,
        node_work: [64]u32 = undefined,
        publication_work: [64]I.Publication = undefined,
        removal_work: [64]I.RemovePlan = undefined,
        insert_work: [64]I.InsertPlan = undefined,
        remove_ids: [64]u32 = undefined,
        puts: [64]I.Transactions.Put = undefined,
        notes: [64]O.api.Notification = undefined,
        cells: [12]O.PartitionPlanning.Cell = undefined,
        children: [8]O.PartitionPlanning.Child = undefined,
        child_slots: [8]u32 = undefined,
        numbers: [8]u64 = undefined,
        merge_a: [8]u64 = undefined,
        merge_b: [8]u64 = undefined,
        best_numbers: [8]u64 = undefined,
        ledger: O = undefined,
        work: [1]O.Workspace = undefined,

        fn initialize(self: *@This()) !void {
            var subscribers = [_][]notifications.Notifier(U).Record{&self.subscriptions};
            try O.init(&self.ledger, &self.rows, &self.shells, &self.states, &self.bindings, &self.nodes, &self.homes, &subscribers);
            self.work[0] = .{
                .candidates = &self.candidates,
                .rows = &self.captures,
                .shells = &self.shell_work,
                .homes = &self.home_work,
                .notes = &self.notes,
                .remove_ids = &self.remove_ids,
                .puts = &self.puts,
                .index_workspace = .{ .homes = &self.home_work, .nodes = &self.node_work, .publications = &self.publication_work, .removals = &self.removal_work, .inserts = &self.insert_work },
                .cells = &self.cells,
                .children = &self.children,
                .child_slots = &self.child_slots,
                .numbers = &self.numbers,
                .merge_a = &self.merge_a,
                .merge_b = &self.merge_b,
                .best_numbers = &self.best_numbers,
            };
        }
        fn cleanup(self: *@This()) void {
            // Only after all API calls and joined threads: no later cache use.
            for (&self.rows) |*row| row.resource.forceRelease() catch @panic("cache fixture OS cleanup failed");
        }
        fn loadFixed(self: *@This()) !O.api.Borrow {
            return (try self.ledger.push(.{ .table = 0 }, key(U, 1), null, &self.work[0])).newly_allocated;
        }
        fn publishFixed(self: *@This()) !void {
            const borrow = try self.loadFixed();
            borrow.region[0] = 0xa7;
            borrow.region[30] = 0x5c;
            _ = try self.ledger.endUse(borrow.lease, .complete);
        }
    };
}

test "cache disabled compiles its row and owner cache metadata away" {
    const Off = Fixture(128, false, 64).Ledger;
    const On = Fixture(128, true, 64).Ledger;
    try std.testing.expect(!Off.window_cache_enabled);
    try std.testing.expect(On.window_cache_enabled);
    try std.testing.expect(@sizeOf(Off.Row) < @sizeOf(On.Row));
    try std.testing.expect(@sizeOf(Off) < @sizeOf(On));
}

test "same-window UID hits and fixed hits skip index and exclusive row work" {
    inline for (.{ 128, 256 }) |bits| {
        const F = Fixture(bits, true, 64);
        var f = F{};
        try f.initialize();
        defer f.cleanup();
        try f.publishFixed();
        // First ready acquisition establishes the reusable physical borrow.
        const warm = (try f.ledger.use(key(F.Uid, 1))).in_memory;
        _ = try f.ledger.endUse(warm.lease, .complete);
        const before = f.ledger.cacheDiagnostics();
        const history = f.rows[15].resource.activity_history;
        var previous_stamp = warm.lease.use_generation;
        var i: usize = 0;
        while (i < 32) : (i += 1) {
            const borrow = if (i % 2 == 0) (try f.ledger.use(key(F.Uid, 1))).in_memory else (try f.ledger.useFixed(0)).in_memory;
            try std.testing.expect(borrow.lease.use_generation > previous_stamp);
            previous_stamp = borrow.lease.use_generation;
            try std.testing.expectEqual(@as(u8, 0xa7), borrow.region[0]);
            _ = try f.ledger.endUse(borrow.lease, .complete);
        }
        const after = f.ledger.cacheDiagnostics();
        try std.testing.expectEqual(before.index_lookups, after.index_lookups);
        try std.testing.expectEqual(before.exclusive_entries, after.exclusive_entries);
        try std.testing.expect(after.hits >= before.hits + 32);
        try std.testing.expectEqualDeep(history, f.rows[15].resource.activity_history);
        try f.ledger.close(key(F.Uid, 1), &f.work[0]);
        try std.testing.expectEqual(F.Ledger.api.UseResult.not_in_memory, try f.ledger.useFixed(0));
    }
}

test "public stamp exhaustion cannot wrap or alter outstanding logical users" {
    const F = Fixture(128, true, 64);
    var f = F{};
    try f.initialize();
    defer f.cleanup();
    try f.publishFixed();
    const held = (try f.ledger.useFixed(0)).in_memory;
    // Fixture owns all callers here; injecting the boundary changes only the
    // next-stamp source, never the active count or allocation identity.
    f.rows[15].cache.stamps.next.store(std.math.maxInt(u64) - 1, .monotonic);
    const final = (try f.ledger.useFixed(0)).in_memory;
    try std.testing.expectEqual(std.math.maxInt(u64) - 1, final.lease.use_generation);
    const before = f.rows[15].cache.logical.load(.acquire);
    try std.testing.expectError(error.SpecificationLimit, f.ledger.useFixed(0));
    try std.testing.expectEqual(before, f.rows[15].cache.logical.load(.acquire));
    try std.testing.expectEqual(@as(u8, 0xa7), held.region[0]);
    _ = try f.ledger.endUse(final.lease, .complete);
    _ = try f.ledger.endUse(held.lease, .complete);
    try f.ledger.close(key(F.Uid, 1), &f.work[0]);
}

test "a logical lease survives many ticks and final end does not invent future activity" {
    const F = Fixture(128, true, 1);
    var f = F{};
    try f.initialize();
    defer f.cleanup();
    try f.publishFixed();
    const held = (try f.ledger.useFixed(0)).in_memory;
    var i: usize = 0;
    while (i < 36) : (i += 1) {
        try std.testing.expectEqual(@as(usize, 0), (try f.ledger.tick(&f.work)).released);
        try std.testing.expectEqual(@as(u8, 0xa7), held.region[0]);
    }
    const end_epoch = f.ledger.epoch.load(.acquire);
    _ = try f.ledger.endUse(held.lease, .complete);
    _ = try f.ledger.tick(&f.work); // retire unused pin before epoch advance
    try std.testing.expectEqual(end_epoch, f.rows[15].resource.activity_history.last_activity_epoch);
    try std.testing.expectEqual(@as(u8, 8), try f.rows[15].resource.activity_history.score(end_epoch, .{}));
    try std.testing.expectEqual(@as(u64, 0), f.rows[15].resource.use_count);
    try std.testing.expectEqual(@as(usize, 0), (try f.ledger.tick(&f.work)).released);
    try std.testing.expectEqual(@as(usize, 0), (try f.ledger.tick(&f.work)).released);
    try std.testing.expectEqual(@as(usize, 1), (try f.ledger.tick(&f.work)).released);
    try std.testing.expectEqual(F.Ledger.api.UseResult.not_in_memory, try f.ledger.useFixed(0));
}

test "one hint slot collisions recheck the complete UID at both widths" {
    inline for (.{ 128, 256 }) |bits| {
        const F = Fixture(bits, true, 1);
        var f = F{};
        try f.initialize();
        defer f.cleanup();
        const first_uid = key(F.Uid, 50);
        // Equal trailing 64 bits, distinct leading byte: no shortened UID check.
        var second_uid = first_uid;
        second_uid.bytes[0] = 0x80;
        const a = (try f.ledger.push(.{ .table = 0 }, first_uid, 7, &f.work[0])).newly_allocated;
        a.region[0] = 0x19;
        _ = try f.ledger.endUse(a.lease, .complete);
        const b = (try f.ledger.push(.{ .table = 0 }, second_uid, 9, &f.work[0])).newly_allocated;
        b.region[0] = 0x83;
        _ = try f.ledger.endUse(b.lease, .complete);
        var i: usize = 0;
        while (i < 16) : (i += 1) {
            const left = (try f.ledger.use(first_uid)).in_memory;
            const right = (try f.ledger.use(second_uid)).in_memory;
            try std.testing.expectEqual(@as(usize, 7), left.region.len);
            try std.testing.expectEqual(@as(usize, 9), right.region.len);
            try std.testing.expectEqual(@as(u8, 0x19), left.region[0]);
            try std.testing.expectEqual(@as(u8, 0x83), right.region[0]);
            try std.testing.expect(left.allocation_number != right.allocation_number);
            _ = try f.ledger.endUse(right.lease, .complete);
            _ = try f.ledger.endUse(left.lease, .complete);
        }
        try f.ledger.close(first_uid, &f.work[0]);
        try f.ledger.close(second_uid, &f.work[0]);
    }
}

test "one user blocks close and UID moving resize while shared resize cannot move" {
    const F = Fixture(256, true, 64);
    var f = F{};
    try f.initialize();
    defer f.cleanup();
    try f.publishFixed();
    const first = (try f.ledger.useFixed(0)).in_memory;
    const before = try f.ledger.view(0);
    try std.testing.expectError(error.InUse, f.ledger.close(key(F.Uid, 1), &f.work[0]));
    try std.testing.expectEqual(F.Ledger.api.UpdateResult.in_use, try f.ledger.updateUid(key(F.Uid, 1), .{ .allocation_bytes = std.heap.pageSize() + 7 }));
    const second = (try f.ledger.use(key(F.Uid, 1))).in_memory;
    try std.testing.expectEqual(F.Ledger.api.UpdateResult.in_use, try f.ledger.updateLease(first.lease, .{ .allocation_bytes = std.heap.pageSize() + 7, .attributes = .{ .recovery_cost_coefficient = 99 } }));
    try std.testing.expectEqualDeep(before, try f.ledger.view(0));
    try std.testing.expectEqual(@as(u32, 1), f.rows[15].attributes.recovery_cost_coefficient);
    // Existing same-page contract remains legal with several logical readers.
    _ = try f.ledger.updateLease(first.lease, .{ .allocation_bytes = 37 });
    _ = try f.ledger.updateUid(key(F.Uid, 1), .{ .attributes = .{ .recovery_cost_coefficient = 7 } });
    _ = try f.ledger.endUse(second.lease, .discard); // ordinary discard is normal end
    const refreshed = (try f.ledger.updateLease(first.lease, .{ .allocation_bytes = std.heap.pageSize() + 7 })).updated.?;
    try std.testing.expectEqual(@as(u8, 0xa7), refreshed[0]);
    try std.testing.expectEqual(@as(u8, 0x5c), refreshed[30]);
    try std.testing.expectEqual(@as(u8, 0), refreshed[std.heap.pageSize()]);
    const third = (try f.ledger.useFixed(0)).in_memory;
    try std.testing.expectEqual(@intFromPtr(refreshed.ptr), @intFromPtr(third.region.ptr));
    try std.testing.expectEqual(refreshed.len, third.region.len);
    _ = try f.ledger.endUse(third.lease, .complete);
    _ = try f.ledger.endUse(first.lease, .complete);
    try f.ledger.close(key(F.Uid, 1), &f.work[0]);
}

test "loading publish discard and forced late ends cannot decrement a replacement" {
    const F = Fixture(128, true, 1);
    var f = F{};
    try f.initialize();
    defer f.cleanup();
    const discarded = try f.loadFixed();
    try std.testing.expectEqual(F.Ledger.api.UseResult.loading, try f.ledger.useFixed(0));
    _ = try f.ledger.endUse(discarded.lease, .discard);
    try std.testing.expectEqual(@as(u64, 31), (try f.ledger.view(0)).registered_bytes);
    try std.testing.expectEqual(F.Ledger.api.UseResult.not_in_memory, try f.ledger.useFixed(0));
    const dynamic_uid = key(F.Uid, 50);
    const dynamic = (try f.ledger.push(.{ .table = 0 }, dynamic_uid, 7, &f.work[0])).newly_allocated;
    _ = try f.ledger.endUse(dynamic.lease, .discard);
    try std.testing.expect((try f.ledger.index.find(dynamic_uid)) == null);
    try f.publishFixed();
    const old = (try f.ledger.useFixed(0)).in_memory;
    // Forced close requires payload quiescence: never read old.region again.
    try f.ledger.forceRelease(key(F.Uid, 1), &f.work[0]);
    try f.publishFixed();
    const current = (try f.ledger.useFixed(0)).in_memory;
    try std.testing.expect(current.allocation_number > old.allocation_number);
    try std.testing.expect(current.lease.use_generation > old.lease.use_generation);
    try std.testing.expectEqual(@import("../memory_resource.zig").EndResult.force_released, try f.ledger.endUse(old.lease, .complete));
    try std.testing.expectError(error.InUse, f.ledger.close(key(F.Uid, 1), &f.work[0]));
    try std.testing.expectEqual(@as(u8, 0xa7), current.region[0]);
    _ = try f.ledger.endUse(current.lease, .complete);
    try f.ledger.close(key(F.Uid, 1), &f.work[0]);
    const abandoned_loading = try f.loadFixed();
    try f.ledger.forceRelease(key(F.Uid, 1), &f.work[0]);
    const replacement_loading = try f.loadFixed();
    try std.testing.expectEqual(@import("../memory_resource.zig").EndResult.force_released, try f.ledger.endUse(abandoned_loading.lease, .discard));
    try std.testing.expectEqual(F.Ledger.api.UseResult.loading, try f.ledger.useFixed(0));
    _ = try f.ledger.endUse(replacement_loading.lease, .complete);
    try f.ledger.close(key(F.Uid, 1), &f.work[0]);
}

test "fixed parent incarnation and subtree replacement keep failed transitions atomic" {
    const F = Fixture(256, true, 1);
    var f = F{};
    try f.initialize();
    defer f.cleanup();
    const root_uid = key(F.Uid, 10);
    const child_uid = key(F.Uid, 20);
    _ = try f.ledger.pushPartition(.{ .table = 0 }, root_uid, 12, attrs, &f.work[0]);
    const first_parent = try f.ledger.pushPartition(.{ .partition = root_uid }, child_uid, 12, attrs, &f.work[0]);
    const loading = (try f.ledger.push(.{ .partition = child_uid }, key(F.Uid, 21), null, &f.work[0])).newly_allocated;
    loading.region[0] = 0x43;
    _ = try f.ledger.endUse(loading.lease, .complete);
    const held = (try f.ledger.useFixed(1)).in_memory;
    const before = try f.ledger.view(0);
    try std.testing.expectError(error.InUse, f.ledger.close(root_uid, &f.work[0]));
    const failed = f.ledger.pushPartition(.{ .partition = root_uid }, key(F.Uid, 30), 12, attrs, &f.work[0]);
    if (failed) |_| return error.TestExpectedBlockedReplacement else |err| try std.testing.expect(err == error.InUse or err == error.Blocked);
    try std.testing.expectEqualDeep(before, try f.ledger.view(0));
    try std.testing.expect((try f.ledger.index.find(child_uid)) != null);
    try std.testing.expect((try f.ledger.index.find(key(F.Uid, 30))) == null);
    try std.testing.expectEqual(@as(u8, 0x43), held.region[0]);
    _ = try f.ledger.endUse(held.lease, .complete);
    try f.ledger.close(child_uid, &f.work[0]);
    try std.testing.expectEqual(F.Ledger.api.UseResult.not_in_memory, try f.ledger.useFixed(1));
    const new_parent = try f.ledger.pushPartition(.{ .partition = root_uid }, child_uid, 12, attrs, &f.work[0]);
    try std.testing.expect(new_parent.allocation_number > first_parent.allocation_number);
    try std.testing.expectEqual(new_parent.allocation_number, f.bindings[1].parent_number.load(.acquire));
    try std.testing.expectEqual(F.Ledger.api.UseResult.not_in_memory, try f.ledger.useFixed(1));
    const next_load = (try f.ledger.push(.{ .partition = child_uid }, key(F.Uid, 21), null, &f.work[0])).newly_allocated;
    _ = try f.ledger.endUse(next_load.lease, .complete);
    const replacement = try f.ledger.pushPartition(.{ .partition = root_uid }, key(F.Uid, 30), 12, attrs, &f.work[0]);
    try std.testing.expect(replacement.created);
    try std.testing.expectEqual(F.Ledger.api.UseResult.not_in_memory, try f.ledger.useFixed(1));
    try std.testing.expectEqual(types.no_slot, f.bindings[1].slot.load(.acquire));
    try std.testing.expectEqual(replacement.allocation_number, f.bindings[2].parent_number.load(.acquire));
    try f.ledger.close(root_uid, &f.work[0]);
    try std.testing.expectEqual(types.no_slot, f.bindings[2].slot.load(.acquire));
}

fn DestructionStorage(comptime O: type) type {
    const I = @TypeOf(@as(O, undefined).index);
    const D = O.Destruction;
    const count = O.resource_capacity + O.partition_capacity;
    return struct {
        ids: [count]u32 = undefined,
        notices: [count]D.Notice = undefined,
        views: [O.table_count]types.TableView = undefined,
        homes: [count]u32 = undefined,
        nodes: [count]u32 = undefined,
        publications: [count]I.Publication = undefined,
        removals: [count]I.RemovePlan = undefined,
        fn workspace(self: *@This()) D.Workspace {
            return .{ .remove_ids = &self.ids, .notices = &self.notices, .table_views = &self.views, .index_workspace = .{ .homes = &self.homes, .nodes = &self.nodes, .publications = &self.publications, .removals = &self.removals } };
        }
    };
}

test "discard failure is terminal and pending pages index and accounting survive for explicit cleanup" {
    const fault = @import("end_failure_pages.zig");
    inline for (.{ 128, 256 }) |bits| inline for (.{ false, true }) |enabled| {
        const F = WithRegionFixture(bits, enabled, 64, fault.Region);
        var f = F{};
        try f.initialize();
        defer f.cleanup();
        defer fault.failures = 0;
        const uid = key(F.Uid, 91);
        const loading = (try f.ledger.push(.{ .table = 0 }, uid, 37, &f.work[0])).newly_allocated;
        loading.region[0] = 0x9b;
        const before = try f.ledger.view(0);
        fault.failures = 2;
        try std.testing.expectEqual(.cleanup_failed, try f.ledger.endUse(loading.lease, .discard));
        const row = &f.rows[loading.lease.slot];
        try std.testing.expectEqual(@as(u64, 0), row.resource.use_count);
        try std.testing.expectEqual(@as(u64, 0), row.resource.active_use_xor);
        try std.testing.expect(row.resource.discardPending());
        try std.testing.expect(f.nodes[loading.lease.slot].writer.tryLock());
        f.nodes[loading.lease.slot].writer.unlock();
        const home = f.nodes[loading.lease.slot].home_index.load(.seq_cst);
        try std.testing.expect(f.homes[home].writer.tryLock());
        f.homes[home].writer.unlock();
        try std.testing.expectEqual(@as(u8, 0x9b), row.resource.registration.?.region.?.bytes()[0]);
        try std.testing.expectEqualDeep(before, try f.ledger.view(0));
        try std.testing.expect((try f.ledger.index.find(uid)) != null);
        try std.testing.expectEqual(.not_in_memory, try f.ledger.use(uid));
        try std.testing.expectError(error.CleanupPending, f.ledger.push(.{ .table = 0 }, uid, 37, &f.work[0]));
        try std.testing.expectError(error.CleanupPending, f.ledger.updateUid(uid, .{ .allocation_bytes = std.heap.pageSize() * 2 }));
        try std.testing.expectEqualDeep(before, try f.ledger.view(0));
        try std.testing.expectError(error.InvalidLease, f.ledger.endUse(loading.lease, .discard));
        try std.testing.expectError(error.CleanupPending, f.ledger.close(uid, &f.work[0]));
        try std.testing.expectEqualDeep(before, try f.ledger.view(0));
        try std.testing.expectEqual(@as(u32, 1), f.states[0].pending_discards);
        var destruction_storage = DestructionStorage(F.Ledger){};
        var destruction_work = destruction_storage.workspace();
        try std.testing.expectError(error.CleanupPending, f.ledger.destroy(false, &destruction_work));
        try std.testing.expectEqualDeep(before, try f.ledger.view(0));
        try f.ledger.close(uid, &f.work[0]);
        try std.testing.expectEqual(@as(u32, 0), f.states[0].pending_discards);
        try std.testing.expect((try f.ledger.index.find(uid)) == null);
        try std.testing.expectEqual(@as(u64, 0), (try f.ledger.view(0)).system_page_bytes);
    };
}

test "abandon does not call failing OS release and Tick retries without a holder or Fatal bypass" {
    const fault = @import("end_failure_pages.zig");
    inline for (.{ 128, 256 }) |bits| inline for (.{ false, true }) |enabled| {
        const F = WithRegionFixture(bits, enabled, 64, fault.Region);
        var f = F{};
        try f.initialize();
        defer f.cleanup();
        defer fault.failures = 0;
        const loading = try f.loadFixed();
        f.rows[loading.lease.slot].attributes = .{ .recoverability = .not_recoverable, .recovery_cost_coefficient = 1, .access_loss_impact = .fatal };
        fault.failures = 1;
        const attempts = fault.attempts;
        try std.testing.expectEqual(.abandoned, try f.ledger.endUse(loading.lease, .abandon));
        try std.testing.expectEqual(attempts, fault.attempts);
        try std.testing.expectEqual(@as(u64, 0), f.rows[loading.lease.slot].resource.use_count);
        try std.testing.expectEqual(@as(usize, 0), (try f.ledger.tick(&f.work)).released);
        try std.testing.expectEqual(@as(u32, 1), f.states[0].pending_discards);
        try std.testing.expectEqual(@as(usize, 1), (try f.ledger.tick(&f.work)).released);
        try std.testing.expectEqual(@as(u32, 0), f.states[0].pending_discards);
        try std.testing.expectEqual(@as(u64, 31), (try f.ledger.view(0)).registered_bytes);
        try std.testing.expectEqual(@as(u64, 0), (try f.ledger.view(0)).system_page_bytes);
        const fresh = try f.loadFixed();
        _ = try f.ledger.endUse(fresh.lease, .complete);
        try std.testing.expectError(error.Blocked, f.ledger.close(key(F.Uid, 1), &f.work[0]));
    };
}

test "Tick resumes pending cleanup after the row it tried last and each attempt is one action" {
    const fault = @import("end_failure_pages.zig");
    inline for (.{ 128, 256 }) |bits| inline for (.{ false, true }) |enabled| {
        const F = PolicyFixture(bits, enabled, 64, fault.Region, one_action_policy);
        var f = F{};
        try f.initialize();
        defer f.cleanup();
        defer fault.failures = 0;
        // A published safe row that optimize mode releases whenever an action is left.
        try f.publishFixed();
        try f.ledger.setMode(0, .optimize);
        const fixed_slot = 15;
        const a = (try f.ledger.push(.{ .table = 0 }, key(F.Uid, 91), 37, &f.work[0])).newly_allocated;
        const b = (try f.ledger.push(.{ .table = 0 }, key(F.Uid, 92), 37, &f.work[0])).newly_allocated;
        try std.testing.expectEqual(.abandoned, try f.ledger.endUse(a.lease, .abandon));
        try std.testing.expectEqual(.abandoned, try f.ledger.endUse(b.lease, .abandon));
        try std.testing.expectEqual(@as(u32, 2), f.states[0].pending_discards);
        const first = @min(a.lease.slot, b.lease.slot);
        const second = @max(a.lease.slot, b.lease.slot);

        // The first pending row fails; that failed attempt is the table's only action.
        fault.failures = 1;
        var attempts = fault.attempts;
        try std.testing.expectEqual(@as(usize, 0), (try f.ledger.tick(&f.work)).released);
        try std.testing.expectEqual(attempts + 1, fault.attempts);
        try std.testing.expect(f.rows[first].resource.discardPending());
        try std.testing.expect(f.rows[second].resource.discardPending());
        try std.testing.expect(f.rows[fixed_slot].resource.registration.?.region != null);

        // The next Tick resumes at the second row, although the first would now succeed.
        attempts = fault.attempts;
        try std.testing.expectEqual(@as(usize, 1), (try f.ledger.tick(&f.work)).released);
        try std.testing.expectEqual(attempts + 1, fault.attempts);
        try std.testing.expect(f.rows[first].resource.discardPending());
        try std.testing.expect(!f.rows[second].resource.discardPending());
        try std.testing.expect(f.rows[fixed_slot].resource.registration.?.region != null);

        // The first row keeps failing and still gets its turn, then is cleaned.
        fault.failures = 1;
        try std.testing.expectEqual(@as(usize, 0), (try f.ledger.tick(&f.work)).released);
        try std.testing.expect(f.rows[first].resource.discardPending());
        try std.testing.expectEqual(@as(usize, 1), (try f.ledger.tick(&f.work)).released);
        try std.testing.expectEqual(@as(u32, 0), f.states[0].pending_discards);
        try std.testing.expect(f.rows[fixed_slot].resource.registration.?.region != null);

        // With nothing pending, the same single action reaches the mode stage.
        try std.testing.expectEqual(@as(usize, 1), (try f.ledger.tick(&f.work)).released);
        try std.testing.expect(f.rows[fixed_slot].resource.registration.?.region == null);
    };
}

test "Tick pending cleanup stops after the last pending row" {
    const fault = @import("end_failure_pages.zig");
    inline for (.{ false, true }) |enabled| {
        const F = WithRegionFixture(128, enabled, 64, fault.Region);
        var f = F{};
        try f.initialize();
        defer f.cleanup();
        defer fault.failures = 0;
        // Unrestricted mode leaves only the pending-cleanup scan in this Tick.
        try f.ledger.setMode(0, .unrestricted);
        const loading = (try f.ledger.push(.{ .table = 0 }, key(F.Uid, 94), 37, &f.work[0])).newly_allocated;
        try std.testing.expectEqual(.abandoned, try f.ledger.endUse(loading.lease, .abandon));
        fault.failures = 1;
        const failed = try f.ledger.tick(&f.work);
        try std.testing.expectEqual(@as(usize, 0), failed.released);
        try std.testing.expectEqual(@as(usize, loading.lease.slot) + 1, failed.scanned);
        try std.testing.expect(failed.scanned < f.rows.len);
        // The retry starts just after the failed row and wraps around to it.
        const cleaned = try f.ledger.tick(&f.work);
        try std.testing.expectEqual(@as(usize, 1), cleaned.released);
        try std.testing.expectEqual(f.rows.len, cleaned.scanned);
        try std.testing.expectEqual(@as(u32, 0), f.states[0].pending_discards);
    }
}

test "close reports CleanupPending only for a failed page release, not for an index failure" {
    const fault = @import("end_failure_pages.zig");
    inline for (.{ false, true }) |enabled| {
        const F = WithRegionFixture(128, enabled, 64, fault.Region);
        var f = F{};
        try f.initialize();
        defer f.cleanup();
        defer fault.failures = 0;
        const uid = key(F.Uid, 93);
        const loading = (try f.ledger.push(.{ .table = 0 }, uid, 37, &f.work[0])).newly_allocated;
        try std.testing.expectEqual(.abandoned, try f.ledger.endUse(loading.lease, .abandon));
        const slot = loading.lease.slot;
        const before = try f.ledger.view(0);
        fault.failures = 1;
        try std.testing.expectError(error.CleanupPending, f.ledger.close(uid, &f.work[0]));
        try std.testing.expectEqualDeep(before, try f.ledger.view(0));
        // A home whose publication version is exhausted fails the index removal
        // before any page release. Retrying close cannot fix that, so it is not 22.
        const home = f.nodes[slot].home_index.load(.seq_cst);
        const version = f.homes[home].version.load(.seq_cst);
        f.homes[home].version.store(std.math.maxInt(u64) - 1, .seq_cst);
        const attempts = fault.attempts;
        try std.testing.expectError(error.VersionExhausted, f.ledger.close(uid, &f.work[0]));
        try std.testing.expectEqual(attempts, fault.attempts);
        try std.testing.expectEqualDeep(before, try f.ledger.view(0));
        try std.testing.expect(f.rows[slot].resource.discardPending());
        f.homes[home].version.store(version, .seq_cst);
        try f.ledger.close(uid, &f.work[0]);
        try std.testing.expectEqual(@as(u32, 0), f.states[0].pending_discards);
        try std.testing.expect((try f.ledger.index.find(uid)) == null);
    }
}

test "abandon only ends its own published use and invalid end can be retried with the valid credential" {
    inline for (.{ false, true }) |enabled| {
        const F = Fixture(128, enabled, 64);
        var f = F{};
        try f.initialize();
        defer f.cleanup();
        try f.publishFixed();
        const a = (try f.ledger.use(key(F.Uid, 1))).in_memory;
        const b = (try f.ledger.use(key(F.Uid, 1))).in_memory;
        try std.testing.expectEqual(.abandoned, try f.ledger.endUse(a.lease, .abandon));
        try std.testing.expectError(error.InUse, f.ledger.close(key(F.Uid, 1), &f.work[0]));
        var invalid = b.lease;
        invalid.use_generation = 0;
        try std.testing.expectError(error.InvalidLease, f.ledger.endUse(invalid, .complete));
        try std.testing.expectEqual(@as(u8, 0xa7), b.region[0]);
        try std.testing.expectEqual(.completed, try f.ledger.endUse(b.lease, .complete));
        try f.ledger.close(key(F.Uid, 1), &f.work[0]);
    }
}

test "explicit forced subtree and instance cleanup clear pending-discard accounting" {
    const fault = @import("end_failure_pages.zig");
    inline for (.{ false, true }) |enabled| {
        const F = WithRegionFixture(128, enabled, 64, fault.Region);
        var f = F{};
        try f.initialize();
        defer f.cleanup();
        _ = try f.ledger.pushPartition(.{ .table = 0 }, key(F.Uid, 20), 12, attrs, &f.work[0]);
        const child = (try f.ledger.push(.{ .partition = key(F.Uid, 20) }, key(F.Uid, 21), null, &f.work[0])).newly_allocated;
        _ = try f.ledger.endUse(child.lease, .abandon);
        try std.testing.expectEqual(@as(u32, 1), f.states[0].pending_discards);
        try std.testing.expectError(error.CleanupPending, f.ledger.close(key(F.Uid, 20), &f.work[0]));
        try f.ledger.forceRelease(key(F.Uid, 20), &f.work[0]);
        try std.testing.expectEqual(@as(u32, 0), f.states[0].pending_discards);
        const root = try f.loadFixed();
        _ = try f.ledger.endUse(root.lease, .abandon);
        var storage = DestructionStorage(F.Ledger){};
        var work = storage.workspace();
        try f.ledger.destroy(true, &work);
        try std.testing.expectEqual(@as(u32, 0), f.states[0].pending_discards);
        try std.testing.expectEqual(@as(u64, 0), f.states[0].system_page_bytes);
    }
}

test "root destruction preflight preserves active users and retires only unused internal pins" {
    const F = Fixture(128, true, 64);
    var f = F{};
    try f.initialize();
    defer f.cleanup();
    try f.publishFixed();
    const held = (try f.ledger.useFixed(0)).in_memory;
    const other_uid = key(F.Uid, 60);
    const other = (try f.ledger.push(.{ .table = 0 }, other_uid, 17, &f.work[0])).newly_allocated;
    _ = try f.ledger.endUse(other.lease, .complete);
    const cached_other = (try f.ledger.use(other_uid)).in_memory;
    _ = try f.ledger.endUse(cached_other.lease, .complete);
    var storage = DestructionStorage(F.Ledger){};
    var work = storage.workspace();
    const before = try f.ledger.view(0);
    try std.testing.expectError(error.InUse, f.ledger.destroy(false, &work));
    try std.testing.expectEqualDeep(before, try f.ledger.view(0));
    try std.testing.expectEqual(@as(u8, 0xa7), held.region[0]);
    try std.testing.expect((try f.ledger.index.find(other_uid)) != null);
    _ = try f.ledger.endUse(held.lease, .complete);
    // This succeeds with quiescent cached pins present and no fake InUse.
    try f.ledger.destroy(false, &work);
    for (&f.rows) |*row| try std.testing.expect(row.uid == null and row.resource.registration == null);
}

const CanaryFixture = Fixture(256, true, 1);
const CanaryState = struct {
    fixture: *CanaryFixture,
    ready: std.atomic.Value(u32) = std.atomic.Value(u32).init(0),
    held: std.atomic.Value(u32) = std.atomic.Value(u32).init(0),
    start: std.atomic.Value(bool) = std.atomic.Value(bool).init(false),
    initial_done: std.atomic.Value(bool) = std.atomic.Value(bool).init(false),
    abort: std.atomic.Value(bool) = std.atomic.Value(bool).init(false),
    errors: std.atomic.Value(u32) = std.atomic.Value(u32).init(0),
    reads: [4]u64 = @splat(0),
    checksum: [4]u64 = @splat(0),
    busy_close: u32 = 0,
    busy_resize: u32 = 0,
    successful_close: u32 = 0,
    successful_resize: u32 = 0,

    fn fail(self: *@This()) void {
        _ = self.errors.fetchAdd(1, .monotonic);
    }
    fn waitFlag(self: *@This(), flag: *const std.atomic.Value(bool)) bool {
        var spins: usize = 0;
        while (spins < 10_000_000) : (spins += 1) {
            if (self.abort.load(.acquire)) return false;
            if (flag.load(.acquire)) return true;
            std.atomic.spinLoopHint();
        }
        self.fail();
        self.abort.store(true, .release);
        return false;
    }
    fn waitCount(self: *@This(), counter: *const std.atomic.Value(u32), target: u32) bool {
        var spins: usize = 0;
        while (spins < 10_000_000) : (spins += 1) {
            if (self.abort.load(.acquire)) return false;
            if (counter.load(.acquire) >= target) return true;
            std.atomic.spinLoopHint();
        }
        self.fail();
        self.abort.store(true, .release);
        return false;
    }
    fn check(self: *@This(), reader_id: usize, borrow: CanaryFixture.Ledger.api.Borrow) void {
        // These are actual payload loads protected by this exact local lease.
        if (borrow.region.len < 31 or borrow.region[0] != 0xa7 or borrow.region[30] != 0x5c) {
            self.fail();
            return;
        }
        self.reads[reader_id] += 1;
        self.checksum[reader_id] +%= @as(u64, borrow.region[0]) + borrow.region[30];
    }
    fn reader(self: *@This(), reader_number: usize) void {
        _ = self.ready.fetchAdd(1, .release);
        if (!self.waitFlag(&self.start)) return;
        const manager = &self.fixture.ledger;
        const initial = manager.useFixed(0) catch {
            self.fail();
            _ = self.held.fetchAdd(1, .release);
            return;
        };
        switch (initial) {
            .in_memory => |borrow| {
                self.check(reader_number, borrow);
                _ = self.held.fetchAdd(1, .release);
                // The writer must try real close/resize while all four local
                // leases are held, before any reader is allowed to return.
                _ = self.waitFlag(&self.initial_done);
                self.check(reader_number, borrow);
                _ = manager.endUse(borrow.lease, .complete) catch {
                    self.fail();
                    return;
                };
            },
            else => {
                self.fail();
                _ = self.held.fetchAdd(1, .release);
            },
        }
        var i: usize = 0;
        while (i < 1024 and !self.abort.load(.acquire)) : (i += 1) {
            const result = if (i % 2 == 0) manager.useFixed(0) else manager.use(key(CanaryFixture.Uid, 1));
            const current = result catch {
                self.fail();
                return;
            };
            switch (current) {
                .in_memory => |borrow| {
                    self.check(reader_number, borrow);
                    _ = manager.endUse(borrow.lease, .complete) catch {
                        self.fail();
                        return;
                    };
                },
                .loading, .not_in_memory => {}, // valid publication/close gap
            }
        }
    }
    fn writer(self: *@This()) void {
        _ = self.ready.fetchAdd(1, .release);
        defer self.initial_done.store(true, .release);
        if (!self.waitFlag(&self.start) or !self.waitCount(&self.held, 4)) return;
        const manager = &self.fixture.ledger;
        const resource_uid = key(CanaryFixture.Uid, 1);
        manager.close(resource_uid, &self.fixture.work[0]) catch |err| {
            if (err == error.InUse) self.busy_close += 1 else self.fail();
        };
        const blocked = manager.updateUid(resource_uid, .{ .allocation_bytes = std.heap.pageSize() + 1 }) catch {
            self.fail();
            return;
        };
        if (blocked == .in_use) self.busy_resize += 1 else self.fail();
        self.initial_done.store(true, .release);
        var i: usize = 0;
        while (i < 256 and !self.abort.load(.acquire)) : (i += 1) {
            if (manager.close(resource_uid, &self.fixture.work[0])) |_| {
                self.successful_close += 1;
                // Publish immutable canary bytes only through a loading lease.
                self.fixture.publishFixed() catch {
                    self.fail();
                    return;
                };
            } else |err| {
                if (err == error.InUse) self.busy_close += 1 else {
                    self.fail();
                    return;
                }
            }
            // Native regions grow monotonically until a successful close. A
            // fresh publication starts at 31 bytes; no unsupported shrink.
            const changed = manager.updateUid(resource_uid, .{ .allocation_bytes = std.heap.pageSize() + 1 + i, .attributes = .{ .recovery_cost_coefficient = @intCast(1 + i % 7) } }) catch {
                self.fail();
                return;
            };
            switch (changed) {
                .in_use => self.busy_resize += 1,
                .updated, .registration_only => self.successful_resize += 1,
                .force_released => {
                    self.fail();
                    return;
                },
            }
            if (i % 8 == 0) _ = manager.tick(&self.fixture.work) catch {
                self.fail();
                return;
            };
        }
    }
};

test "four real readers safely overlap close resize attributes and tick churn" {
    var f = CanaryFixture{};
    try f.initialize();
    defer f.cleanup();
    try f.publishFixed();
    // Idle expiry is covered separately; this canary isolates metadata churn.
    try f.ledger.setMode(0, .unrestricted);
    var state: CanaryState = .{ .fixture = &f };
    var readers: [4]std.Thread = undefined;
    var reader_count: usize = 0;
    var joined = false;
    var writer = try std.Thread.spawn(.{}, CanaryState.writer, .{&state});
    errdefer {
        if (!joined) {
            state.abort.store(true, .release);
            state.start.store(true, .release);
            state.initial_done.store(true, .release);
            for (readers[0..reader_count]) |thread| thread.join();
            writer.join();
        }
    }
    while (reader_count < readers.len) : (reader_count += 1)
        readers[reader_count] = try std.Thread.spawn(.{}, CanaryState.reader, .{ &state, reader_count });
    const all_ready = state.waitCount(&state.ready, 5);
    state.start.store(true, .release);
    for (&readers) |*thread| thread.join();
    writer.join();
    joined = true;
    try std.testing.expect(all_ready);
    try std.testing.expectEqual(@as(u32, 0), state.errors.load(.acquire));
    try std.testing.expect(state.busy_close >= 1 and state.busy_resize >= 1);
    for (state.reads, state.checksum) |reads, checksum| {
        try std.testing.expect(reads >= 2);
        try std.testing.expectEqual(reads * (0xa7 + 0x5c), checksum);
    }
}

test "late cache retirement uses the last logical end not a later global epoch" {
    const F = Fixture(128, true, 64);
    var f = F{};
    try f.initialize();
    defer f.cleanup();
    try f.publishFixed();
    const held = (try f.ledger.useFixed(0)).in_memory;
    _ = try f.ledger.endUse(held.lease, .complete);
    // Model an entry whose busy scan preceded the last return, and whose
    // retirement runs after epoch publication. No actual user exists in 2..5.
    f.ledger.epoch.store(5, .release);
    f.states[0].write.lock();
    f.rows[15].lock.lock();
    f.ledger.flushWindowCache(&f.rows[15]);
    f.rows[15].lock.unlock();
    f.states[0].write.unlock();
    const history = f.rows[15].resource.activity_history;
    try std.testing.expectEqual(@as(u64, 1), history.last_activity_epoch);
    try std.testing.expectEqual(@as(u8, 16), try history.aligned(5));
}

test "normal retirement rejects duplicate end while actual force invalidates live users" {
    const F = Fixture(128, true, 64);
    var f = F{};
    try f.initialize();
    defer f.cleanup();
    try f.publishFixed();
    const first = (try f.ledger.useFixed(0)).in_memory;
    _ = try f.ledger.endUse(first.lease, .complete);
    _ = try f.ledger.tick(&f.work);
    try std.testing.expectError(error.InvalidLease, f.ledger.endUse(first.lease, .complete));
    const second = (try f.ledger.useFixed(0)).in_memory;
    try f.ledger.forceRelease(key(F.Uid, 1), &f.work[0]);
    try std.testing.expect((try f.ledger.endUse(second.lease, .complete)) == .force_released);
}
