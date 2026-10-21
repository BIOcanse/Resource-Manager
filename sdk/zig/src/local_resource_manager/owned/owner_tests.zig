const std = @import("std");
const values = @import("ledger_values").Bound;
const types = @import("types.zig");
const uid = @import("../uid.zig");
const index = @import("index.zig");
const owner = @import("owner.zig");
const notify = @import("notifications.zig");

const attrs = types.Attributes{ .recoverability = .recoverable, .recovery_cost_coefficient = 1, .access_loss_impact = .unobservable_now };
// These policies consume actual SDK38 output, including different table scopes.
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
        .release_check = .{ .enabled = @field(values, prefix ++ "release_check_enabled") == 2, .tolerance_parts_per_million = @intCast(@field(values, prefix ++ "release_tolerance_parts_per_million")), .tolerance_bytes = @field(values, prefix ++ "release_tolerance_bytes") },
    };
}
pub fn key(comptime U: type, value: u64) U {
    var result = U{ .bytes = @splat(0) };
    std.mem.writeInt(u64, result.bytes[result.bytes.len - 8 ..][0..8], value, .big);
    return result;
}
pub fn Fixture(comptime bits: u16) type {
    return FixtureWithPolicies(bits, .{ policy(0), policy(1) });
}
fn FixtureWithPolicies(comptime bits: u16, comptime policies: [2]types.Policy) type {
    return FixtureConfiguration(bits, policies, false, false);
}
pub fn FixtureWithStatistics(comptime bits: u16, comptime enabled: bool) type {
    return FixtureConfiguration(bits, .{ policy(0), policy(1) }, enabled, false);
}
pub fn FixtureWithStatisticsPartitions(comptime bits: u16) type {
    return FixtureConfiguration(bits, .{ policy(0), policy(1) }, true, true);
}
fn FixtureConfiguration(comptime bits: u16, comptime policies: [2]types.Policy, comptime statistics: bool, comptime partitions: bool) type {
    const U = uid.Identifier(bits);
    const I = index.Candidate(U, .c2_chain);
    const tables = [_]types.Table{
        .{ .capacity = 8, .partition_capacity = 4, .reserved_start = 0, .reserved_count = 0, .attributes = attrs, .policy = policies[0] },
        .{ .capacity = 8, .partition_capacity = 4, .reserved_start = 0, .reserved_count = if (partitions) 8 else 0, .attributes = attrs, .policy = policies[1] },
    };
    const fixed = [_]types.For(U).Fixed{.{ .table = 0, .parent = null, .ordinal = 2, .uid = key(U, 1), .allocation_bytes = 31, .attributes = null }};
    const O = owner.Owner(.{ .uid_width = bits, .activity_width = @intCast(values.activity_bitmap_width), .index_buckets = 32, .development_allocation_statistics = statistics }, &tables, &fixed, I);
    return struct {
        pub const Ledger = O;
        pub const Uid = U;
        rows: [16]O.Row = undefined,
        shells: [8]O.Shell = undefined,
        tables: [2]O.TableState = undefined,
        bindings: [1]O.FixedBinding = undefined,
        nodes: [24]I.Node = undefined,
        homes: [32]I.Home = undefined,
        records: [2][2]notify.Notifier(U).Record = undefined,
        candidates: [2][64]O.Candidate = undefined,
        captures: [2][8]O.Candidate = undefined,
        shell_work: [2][4]u32 = undefined,
        home_work: [2][12]u32 = undefined,
        publication_work: [2][12]I.Publication = undefined,
        removal_work: [2][12]I.RemovePlan = undefined,
        insert_work: [2][12]I.InsertPlan = undefined,
        node_work: [2][12]u32 = undefined,
        remove_id_work: [2][12]u32 = undefined,
        put_work: [2][12]I.Transactions.Put = undefined,
        notes: [2][64]O.api.Notification = undefined,
        cells: [2][if (partitions) 8 else 0]O.PartitionPlanning.Cell = undefined,
        children: [2][if (partitions) 4 else 0]O.PartitionPlanning.Child = undefined,
        child_slots: [2][if (partitions) 4 else 0]u32 = undefined,
        numbers: [2][if (partitions) 4 else 0]u64 = undefined,
        merge_a: [2][if (partitions) 4 else 0]u64 = undefined,
        merge_b: [2][if (partitions) 4 else 0]u64 = undefined,
        best_numbers: [2][if (partitions) 4 else 0]u64 = undefined,
        ledger: O = undefined,
        work: [2]O.Workspace = undefined,
        pub fn initialize(self: *@This()) !void {
            var subscriptions = [_][]notify.Notifier(U).Record{ &self.records[0], &self.records[1] };
            self.ledger = try O.init(&self.rows, &self.shells, &self.tables, &self.bindings, &self.nodes, &self.homes, &subscriptions);
            for (&self.work, 0..) |*work, i| work.* = .{ .candidates = &self.candidates[i], .rows = &self.captures[i], .shells = &self.shell_work[i], .homes = &self.home_work[i], .notes = &self.notes[i], .remove_ids = &self.remove_id_work[i], .puts = &self.put_work[i], .index_workspace = .{ .homes = &self.home_work[i], .nodes = &self.node_work[i], .publications = &self.publication_work[i], .removals = &self.removal_work[i], .inserts = &self.insert_work[i] }, .cells = &self.cells[i], .children = &self.children[i], .child_slots = &self.child_slots[i], .numbers = &self.numbers[i], .merge_a = &self.merge_a[i], .merge_b = &self.merge_b[i], .best_numbers = &self.best_numbers[i] };
        }
        pub fn cleanup(self: *@This()) void {
            for (&self.rows) |*row| row.resource.forceRelease() catch @panic("fixture OS cleanup failed");
        }
    };
}

test "owned ledger actual pages fixed binding counted leases and allocation numbers at both UID widths" {
    inline for (.{ 128, 256 }) |bits| {
        var fixture = Fixture(bits){};
        try fixture.initialize();
        defer fixture.cleanup();
        const manager = &fixture.ledger;
        const U = Fixture(bits).Uid;
        try std.testing.expectEqual(@as(u64, 31), (try manager.view(0)).registered_bytes);
        try std.testing.expectEqual(Fixture(bits).Ledger.api.UseResult.not_in_memory, try manager.useFixed(0));
        const first = (try manager.push(.{ .table = 0 }, key(U, 1), null, &fixture.work[0])).newly_allocated;
        for (first.region) |byte| try std.testing.expectEqual(@as(u8, 0), byte);
        first.region[17] = 0x52;
        try std.testing.expectEqual(Fixture(bits).Ledger.api.UseResult.loading, try manager.use(key(U, 1)));
        _ = try manager.endUse(first.lease, .complete);
        const direct = (try manager.useFixed(0)).in_memory;
        const runtime = (try manager.use(key(U, 1))).in_memory;
        try std.testing.expectEqual(@as(u8, 0x52), runtime.region[17]);
        try std.testing.expectError(error.InUse, manager.close(key(U, 1), &fixture.work[0]));
        try std.testing.expectEqual(Fixture(bits).Ledger.api.UpdateResult.in_use, try manager.updateLease(direct.lease, .{ .allocation_bytes = std.heap.pageSize() + 3 }));
        _ = try manager.endUse(runtime.lease, .complete);
        const grown = (try manager.updateLease(direct.lease, .{ .allocation_bytes = std.heap.pageSize() + 3 })).updated.?;
        try std.testing.expectEqual(@as(u8, 0x52), grown[17]);
        try std.testing.expectEqual(@as(u8, 0), grown[31]);
        _ = try manager.endUse(direct.lease, .complete);
        try manager.close(key(U, 1), &fixture.work[0]);
        try std.testing.expectEqual(@as(u64, std.heap.pageSize() + 3), (try manager.view(0)).registered_bytes);
        try std.testing.expectEqual(@as(u64, 0), (try manager.view(0)).system_page_bytes);
        _ = try manager.updateUid(key(U, 1), .{ .allocation_bytes = 19 });
        try std.testing.expectEqual(@as(u64, 0), (try manager.view(0)).system_page_bytes);
        const replacement = (try manager.push(.{ .table = 0 }, key(U, 1), null, &fixture.work[0])).newly_allocated;
        try std.testing.expect(replacement.allocation_number > first.allocation_number);
        try std.testing.expectEqual(@as(usize, 19), replacement.region.len);
        try manager.forceRelease(key(U, 1), &fixture.work[0]);
        try std.testing.expectEqual(lifecycleEndForced(), try manager.endUse(replacement.lease, .complete));
        try std.testing.expectEqual(Fixture(bits).Ledger.api.UpdateResult.force_released, try manager.updateLease(replacement.lease, .{ .attributes = .{ .access_loss_impact = .fatal } }));
        try std.testing.expectError(error.InvalidArgument, manager.push(.{ .table = 1 }, key(U, 2), null, &fixture.work[1]));
    }
}
fn lifecycleEndForced() @import("../memory_resource.zig").EndResult {
    return .force_released;
}

test "SDK-selected UID width and activity policy reach an actual owner allocation" {
    const F = Fixture(@intCast(values.uid_width_bits));
    var fixture = F{};
    try fixture.initialize();
    defer fixture.cleanup();
    try std.testing.expectEqual(@as(usize, @intCast(values.uid_width_bits / 8)), @sizeOf(F.Uid));
    const loaded = (try fixture.ledger.push(.{ .table = 1 }, key(F.Uid, 99), 17, &fixture.work[1])).newly_allocated;
    try std.testing.expectEqual(@as(usize, 17), loaded.region.len);
    _ = try fixture.ledger.endUse(loaded.lease, .complete);
    const located = (try fixture.ledger.use(key(F.Uid, 99))).in_memory;
    try std.testing.expectEqual(loaded.allocation_number, located.allocation_number);
    _ = try fixture.ledger.endUse(located.lease, .complete);
}

test "owned ledger discard retention UID-only update zero allocation and Tick before epoch advance" {
    var fixture = Fixture(128){};
    try fixture.initialize();
    defer fixture.cleanup();
    const manager = &fixture.ledger;
    const U = Fixture(128).Uid;
    const discarded = (try manager.push(.{ .table = 0 }, key(U, 1), null, &fixture.work[0])).newly_allocated;
    _ = try manager.endUse(discarded.lease, .discard);
    try std.testing.expectEqual(@as(u64, 31), (try manager.view(0)).registered_bytes);
    const dynamic = (try manager.push(.{ .table = 1 }, key(U, 2), 0, &fixture.work[1])).newly_allocated;
    try std.testing.expectEqual(@as(usize, 0), dynamic.region.len);
    _ = try manager.endUse(dynamic.lease, .discard);
    try std.testing.expectEqual(Fixture(128).Ledger.api.UseResult.not_in_memory, try manager.use(key(U, 2)));
    try std.testing.expectError(error.Nonexistent, manager.updateUid(key(U, 2), .{ .allocation_bytes = 7 }));
    const loaded = (try manager.push(.{ .table = 0 }, key(U, 1), 47, &fixture.work[0])).newly_allocated;
    try std.testing.expectEqual(@as(usize, 47), loaded.region.len);
    try std.testing.expectEqual(@as(u64, 47), (try manager.view(0)).registered_bytes);
    manager.epoch.store(5, .release);
    _ = try manager.endUse(loaded.lease, .complete);
    try std.testing.expectEqual(@as(u64, 5), fixture.rows[2].resource.activity_history.last_activity_epoch);
    const updated = try manager.updateUid(key(U, 1), .{ .allocation_bytes = 53 });
    try std.testing.expect(updated.updated == null);
    try std.testing.expectEqual(@as(u64, 53), (try manager.view(0)).registered_bytes);
    try manager.setMode(0, .unrestricted);
    manager.epoch.store(5 + policy(0).idle_epochs, .release);
    _ = try manager.tick(&fixture.work);
    const current = (try manager.useFixed(0)).in_memory;
    _ = try manager.endUse(current.lease, .complete);
    try manager.setMode(1, .optimize);
    const victim = (try manager.push(.{ .table = 1 }, key(U, 3), 11, &fixture.work[1])).newly_allocated;
    _ = try manager.endUse(victim.lease, .complete);
    const before = manager.epoch.load(.acquire);
    const cleaned = try manager.tick(&fixture.work);
    try std.testing.expect(cleaned.released >= 1);
    try std.testing.expectEqual(before + 1, manager.epoch.load(.acquire));
    try std.testing.expectEqual(Fixture(128).Ledger.api.UseResult.not_in_memory, try manager.use(key(U, 3)));
}

test "owned ledger Tick mode phases share each table budget before epoch advance" {
    inline for (.{ 128, 256 }) |bits| {
        const p = comptime blk: {
            var selected = policy(0);
            selected.score_epochs = 4;
            selected.idle_epochs = 1;
            selected.protection = .concentrated;
            selected.concentrated_trigger_percent = 40;
            selected.concentrated_target_percent = 50;
            selected.tick_maximum_actions = 3;
            selected.release_check.enabled = false;
            break :blk selected;
        };
        const F = FixtureWithPolicies(bits, .{ p, p });
        const U = F.Uid;
        const Capture = struct {
            ids: [8]u64 = @splat(0),
            numbers: [8]u64 = @splat(0),
            count: usize = 0,
            fn receive(raw: ?*anyopaque, resource: U, number: u64) callconv(.c) void {
                const self: *@This() = @ptrCast(@alignCast(raw.?));
                std.debug.assert(self.count < self.ids.len);
                self.ids[self.count] = std.mem.readInt(u64, resource.bytes[resource.bytes.len - 8 ..][0..8], .big);
                self.numbers[self.count] = number;
                self.count += 1;
            }
        };
        inline for (.{ types.Mode.unrestricted, types.Mode.normal, types.Mode.optimize }) |mode| {
            var fixture = F{};
            try fixture.initialize();
            defer fixture.cleanup();
            const manager = &fixture.ledger;
            var captures = [_]Capture{ .{}, .{} };
            const first_subscription = try manager.subscribe(0, Capture.receive, &captures[0]);
            defer manager.unsubscribe(first_subscription) catch unreachable;
            const second_subscription = try manager.subscribe(1, Capture.receive, &captures[1]);
            defer manager.unsubscribe(second_subscription) catch unreachable;
            const fixed = (try manager.push(.{ .table = 0 }, key(U, 1), 0, &fixture.work[0])).newly_allocated;
            _ = try manager.endUse(fixed.lease, .complete);
            var dynamic_numbers: [5]u64 = undefined;
            for (&dynamic_numbers, 0..) |*number, i| {
                const item = (try manager.push(.{ .table = 0 }, key(U, 10 + i), 1, &fixture.work[0])).newly_allocated;
                number.* = item.allocation_number;
                _ = try manager.endUse(item.lease, .complete);
            }
            const old = (try manager.push(.{ .table = 1 }, key(U, 20), 17, &fixture.work[1])).newly_allocated;
            _ = try manager.endUse(old.lease, .complete);
            manager.epoch.store(2, .release);
            const fresh = (try manager.push(.{ .table = 1 }, key(U, 21), 0, &fixture.work[1])).newly_allocated;
            _ = try manager.endUse(fresh.lease, .complete);
            try manager.setMode(0, mode);
            try manager.setMode(1, mode);
            const result = try manager.tick(&fixture.work);
            const expected_releases: usize = switch (mode) {
                .unrestricted => 2,
                .normal => 4,
                .optimize => 5,
            };
            try std.testing.expectEqual(expected_releases, result.released);
            try std.testing.expect(result.scanned >= result.released);
            try std.testing.expect(result.scanned <= 3 * fixture.rows.len);
            try std.testing.expectEqual(@as(u64, 3), manager.epoch.load(.acquire));
            const first_count: usize = if (mode == .unrestricted) 2 else 3;
            try std.testing.expectEqual(first_count, captures[0].count);
            try std.testing.expectEqualSlices(u64, &.{ 10, 11 }, captures[0].ids[0..2]);
            try std.testing.expectEqualSlices(u64, dynamic_numbers[0..2], captures[0].numbers[0..2]);
            if (mode != .unrestricted) {
                try std.testing.expectEqual(@as(u64, 1), captures[0].ids[2]);
                try std.testing.expectEqual(fixed.allocation_number, captures[0].numbers[2]);
                try std.testing.expectEqual(F.Ledger.api.UseResult.not_in_memory, try manager.useFixed(0));
            } else {
                const retained = (try manager.useFixed(0)).in_memory;
                _ = try manager.endUse(retained.lease, .complete);
            }
            switch (mode) {
                .unrestricted => try std.testing.expectEqual(@as(usize, 0), captures[1].count),
                .normal => {
                    try std.testing.expectEqual(@as(usize, 1), captures[1].count);
                    try std.testing.expectEqual(old.allocation_number, captures[1].numbers[0]);
                    try std.testing.expectEqual(@as(u64, 20), captures[1].ids[0]);
                },
                .optimize => {
                    try std.testing.expectEqualSlices(u64, &.{ 21, 20 }, captures[1].ids[0..captures[1].count]);
                    try std.testing.expectEqualSlices(u64, &.{ fresh.allocation_number, old.allocation_number }, captures[1].numbers[0..captures[1].count]);
                },
            }
            for ([_]u64{ 20, 21 }) |id| {
                const survives = mode == .unrestricted or (mode == .normal and id == 21);
                const found = try manager.use(key(U, id));
                if (survives) {
                    _ = try manager.endUse(found.in_memory.lease, .complete);
                } else try std.testing.expectEqual(F.Ledger.api.UseResult.not_in_memory, found);
            }
            try std.testing.expectEqual(@as(u64, 3), (try manager.view(0)).registered_bytes);
        }
    }
}
