//! Contract 13.8: statistics observe actual owner allocation and release paths.
const std = @import("std");
const fixtures = @import("owner_tests.zig");
const types = @import("types.zig");
const statistics = @import("allocation_statistics.zig");
const key = fixtures.key;
const attrs = types.Attributes{ .recoverability = .recoverable, .recovery_cost_coefficient = 1, .access_loss_impact = .unobservable_now };

fn expectStatistics(manager: anytype, table: u32, expected: statistics.Counters) !void {
    const Ledger = @TypeOf(manager.*);
    try std.testing.expectEqualDeep(expected, try Ledger.DevelopmentStatistics.get(manager, table));
}

test "development allocation statistics compile out storage and query at both UID widths" {
    inline for (.{ 128, 256 }) |bits| {
        const Disabled = fixtures.FixtureWithStatistics(bits, false).Ledger;
        const Enabled = fixtures.FixtureWithStatistics(bits, true).Ledger;
        try std.testing.expect(!Disabled.development_statistics_enabled);
        try std.testing.expect(Enabled.development_statistics_enabled);
        try std.testing.expect(!@hasDecl(Disabled.DevelopmentStatistics, "get"));
        try std.testing.expect(@hasDecl(Enabled.DevelopmentStatistics, "get"));
        try std.testing.expectEqual(@as(usize, 0), @sizeOf(@FieldType(Disabled.Row, "allocation_birth")));
        try std.testing.expectEqual(@as(usize, 0), @sizeOf(@FieldType(Disabled.TableState, "allocation_statistics")));
        try std.testing.expectEqual(@sizeOf(statistics.Birth), @sizeOf(@FieldType(Enabled.Row, "allocation_birth")));
        try std.testing.expectEqual(@as(usize, 40), @sizeOf(@FieldType(Enabled.TableState, "allocation_statistics")));
        try std.testing.expectEqual(@as(usize, 8), @alignOf(statistics.Counters));
        try std.testing.expectEqual(@as(usize, 0), @offsetOf(statistics.Counters, "new_allocations"));
        try std.testing.expectEqual(@as(usize, 8), @offsetOf(statistics.Counters, "same_epoch_releases"));
        try std.testing.expectEqual(@as(usize, 16), @offsetOf(statistics.Counters, "same_epoch_allocation_bytes"));
        try std.testing.expectEqual(@as(usize, 24), @offsetOf(statistics.Counters, "subpage_allocations"));
        try std.testing.expectEqual(@as(usize, 32), @offsetOf(statistics.Counters, "subpage_allocation_bytes"));
        const default_config = types.Config{ .uid_width = bits, .activity_width = 8, .index_buckets = 32 };
        try std.testing.expect(!default_config.development_allocation_statistics);
        try std.testing.expect(@sizeOf(Disabled.Row) < @sizeOf(Enabled.Row));
        try std.testing.expect(@sizeOf(Disabled.TableState) < @sizeOf(Enabled.TableState));

        // Disabled owners still perform ordinary allocation, resize and release.
        var fixture = fixtures.FixtureWithStatistics(bits, false){};
        try fixture.initialize();
        defer fixture.cleanup();
        const loaded = (try fixture.ledger.push(.{ .table = 1 }, key(Disabled.uid_type, 2), 7, &fixture.work[1])).newly_allocated;
        _ = try fixture.ledger.endUse(loaded.lease, .complete);
        _ = try fixture.ledger.updateUid(key(Disabled.uid_type, 2), .{ .allocation_bytes = 9 });
        try fixture.ledger.close(key(Disabled.uid_type, 2), &fixture.work[1]);
        try std.testing.expectEqual(@as(u64, 0), (try fixture.ledger.view(1)).registered_bytes);
    }
}

test "statistics count only successful new allocations and preserve pre-resize allocation bytes" {
    inline for (.{ 128, 256 }) |bits| {
        const F = fixtures.FixtureWithStatistics(bits, true);
        var fixture = F{};
        try fixture.initialize();
        defer fixture.cleanup();
        const manager = &fixture.ledger;
        const id = key(F.Uid, 2);
        try expectStatistics(manager, 0, .{}); // Offline fixed registration is not an allocation.
        try expectStatistics(manager, 1, .{});
        try std.testing.expectError(error.InvalidArgument, F.Ledger.DevelopmentStatistics.get(manager, 2));
        try std.testing.expectError(error.SizeOverflow, manager.push(.{ .table = 1 }, id, std.math.maxInt(u64), &fixture.work[1]));
        try expectStatistics(manager, 1, .{});
        const loaded = (try manager.push(.{ .table = 1 }, id, 17, &fixture.work[1])).newly_allocated;
        const allocated = statistics.Counters{ .new_allocations = 1, .subpage_allocations = 1, .subpage_allocation_bytes = 17 };
        try expectStatistics(manager, 1, allocated);
        try std.testing.expectEqual(F.Ledger.api.PushResult.loading, try manager.push(.{ .table = 1 }, id, 99, &fixture.work[1]));
        try std.testing.expectError(error.InUse, manager.close(id, &fixture.work[1]));
        _ = try manager.endUse(loaded.lease, .complete);
        const hit = (try manager.push(.{ .table = 0 }, id, 99, &fixture.work[0])).in_memory;
        const passive = (try manager.use(id)).in_memory;
        _ = try manager.endUse(passive.lease, .complete);
        _ = try manager.updateLease(hit.lease, .{ .allocation_bytes = std.heap.pageSize() + 7 });
        try std.testing.expectError(error.AllocationTooSmall, manager.updateLease(hit.lease, .{ .allocation_bytes = 3 }));
        _ = try manager.endUse(hit.lease, .complete);
        try std.testing.expectError(error.SizeOverflow, manager.updateUid(id, .{ .allocation_bytes = std.math.maxInt(u64) }));
        _ = try manager.updateUid(id, .{ .attributes = .{ .recovery_cost_coefficient = 2 } });
        try expectStatistics(manager, 1, allocated);
        try manager.close(id, &fixture.work[1]);
        const released = statistics.Counters{ .new_allocations = 1, .same_epoch_releases = 1, .same_epoch_allocation_bytes = 17, .subpage_allocations = 1, .subpage_allocation_bytes = 17 };
        try expectStatistics(manager, 1, released);
        try std.testing.expectError(error.Nonexistent, manager.close(id, &fixture.work[1]));
        _ = try manager.endUse(hit.lease, .complete);
        try expectStatistics(manager, 1, released);
        try expectStatistics(manager, 0, .{});
    }
}

test "statistics subpage boundary includes zero and excludes an exact system page" {
    inline for (.{ 128, 256 }) |bits| {
        const F = fixtures.FixtureWithStatistics(bits, true);
        var fixture = F{};
        try fixture.initialize();
        defer fixture.cleanup();
        const manager = &fixture.ledger;
        const page: u64 = @intCast(std.heap.pageSize());
        for ([_]u64{ 0, page - 1, page, page + 1 }, 0..) |size, i| {
            const id = key(F.Uid, 20 + i);
            const loaded = (try manager.push(.{ .table = 1 }, id, size, &fixture.work[1])).newly_allocated;
            try std.testing.expectEqual(@as(usize, @intCast(size)), loaded.region.len);
            _ = try manager.endUse(loaded.lease, .complete);
            try manager.close(id, &fixture.work[1]);
        }
        try expectStatistics(manager, 1, .{ .new_allocations = 4, .same_epoch_releases = 4, .same_epoch_allocation_bytes = 3 * page, .subpage_allocations = 2, .subpage_allocation_bytes = page - 1 });
    }
}

test "statistics fixed reload discard and repeated offline close stay isolated by table" {
    inline for (.{ 128, 256 }) |bits| {
        const F = fixtures.FixtureWithStatistics(bits, true);
        var fixture = F{};
        try fixture.initialize();
        defer fixture.cleanup();
        const manager = &fixture.ledger;
        const fixed_id = key(F.Uid, 1);
        try manager.close(fixed_id, &fixture.work[0]);
        try expectStatistics(manager, 0, .{});
        const fixed_first = (try manager.push(.{ .table = 0 }, fixed_id, null, &fixture.work[0])).newly_allocated;
        _ = try manager.endUse(fixed_first.lease, .discard);
        try manager.close(fixed_id, &fixture.work[0]);
        _ = try manager.updateUid(fixed_id, .{ .allocation_bytes = 19 });
        const fixed_second = (try manager.push(.{ .table = 0 }, fixed_id, null, &fixture.work[0])).newly_allocated;
        try std.testing.expect(fixed_second.allocation_number > fixed_first.allocation_number);
        _ = try manager.endUse(fixed_second.lease, .complete);
        try manager.forceRelease(fixed_id, &fixture.work[0]);
        try manager.forceRelease(fixed_id, &fixture.work[0]);
        try expectStatistics(manager, 0, .{ .new_allocations = 2, .same_epoch_releases = 2, .same_epoch_allocation_bytes = 50, .subpage_allocations = 2, .subpage_allocation_bytes = 50 });
        const dynamic_id = key(F.Uid, 2);
        const dynamic = (try manager.push(.{ .table = 1 }, dynamic_id, 0, &fixture.work[1])).newly_allocated;
        _ = try manager.endUse(dynamic.lease, .discard);
        _ = try manager.endUse(dynamic.lease, .discard);
        try expectStatistics(manager, 1, .{ .new_allocations = 1, .same_epoch_releases = 1, .subpage_allocations = 1 });
        try expectStatistics(manager, 0, .{ .new_allocations = 2, .same_epoch_releases = 2, .same_epoch_allocation_bytes = 50, .subpage_allocations = 2, .subpage_allocation_bytes = 50 });
    }
}

test "statistics Tick releases before advancing epoch and queries never clear cumulative values" {
    inline for (.{ 128, 256 }) |bits| {
        const F = fixtures.FixtureWithStatistics(bits, true);
        var fixture = F{};
        try fixture.initialize();
        defer fixture.cleanup();
        const manager = &fixture.ledger;
        const old_id = key(F.Uid, 2);
        const old = (try manager.push(.{ .table = 1 }, old_id, 7, &fixture.work[1])).newly_allocated;
        _ = try manager.endUse(old.lease, .complete);
        try manager.setMode(0, .unrestricted);
        try manager.setMode(1, .unrestricted);
        _ = try manager.tick(&fixture.work);
        try manager.close(old_id, &fixture.work[1]);
        try expectStatistics(manager, 1, .{ .new_allocations = 1, .subpage_allocations = 1, .subpage_allocation_bytes = 7 });
        const fresh = (try manager.push(.{ .table = 1 }, key(F.Uid, 3), 11, &fixture.work[1])).newly_allocated;
        _ = try manager.endUse(fresh.lease, .complete);
        try manager.setMode(1, .optimize);
        const before_epoch = manager.epoch.load(.acquire);
        const cleaned = try manager.tick(&fixture.work);
        try std.testing.expectEqual(@as(usize, 1), cleaned.released);
        try std.testing.expectEqual(before_epoch + 1, manager.epoch.load(.acquire));
        const expected = statistics.Counters{ .new_allocations = 2, .same_epoch_releases = 1, .same_epoch_allocation_bytes = 11, .subpage_allocations = 2, .subpage_allocation_bytes = 18 };
        try expectStatistics(manager, 1, expected);
        try expectStatistics(manager, 1, expected);
        _ = try manager.tick(&fixture.work);
        try expectStatistics(manager, 1, expected);
    }
}

test "statistics tree rotation replacement close and force release count payloads only" {
    inline for (.{ 128, 256 }) |bits| {
        const F = fixtures.FixtureWithStatisticsPartitions(bits);
        var fixture = F{};
        try fixture.initialize();
        defer fixture.cleanup();
        const manager = &fixture.ledger;
        const work = &fixture.work[1];
        const root_id = key(F.Uid, 10);
        _ = try manager.pushPartition(.{ .table = 1 }, root_id, 8, attrs, work);
        const child_id = key(F.Uid, 11);
        _ = try manager.pushPartition(.{ .partition = root_id }, child_id, 2, attrs, work);
        try expectStatistics(manager, 1, .{});
        for ([_]u64{ 20, 21, 22 }) |id| {
            const loaded = (try manager.push(.{ .partition = child_id }, key(F.Uid, id), 7, work)).newly_allocated;
            _ = try manager.endUse(loaded.lease, .complete);
        }
        try expectStatistics(manager, 1, .{ .new_allocations = 3, .same_epoch_releases = 1, .same_epoch_allocation_bytes = 7, .subpage_allocations = 3, .subpage_allocation_bytes = 21 });
        // Replacing the only direct child with an 8-slot shell releases its two payloads.
        _ = try manager.pushPartition(.{ .partition = root_id }, key(F.Uid, 12), 8, attrs, work);
        try expectStatistics(manager, 1, .{ .new_allocations = 3, .same_epoch_releases = 3, .same_epoch_allocation_bytes = 21, .subpage_allocations = 3, .subpage_allocation_bytes = 21 });
        const resident = (try manager.push(.{ .partition = key(F.Uid, 12) }, key(F.Uid, 23), 13, work)).newly_allocated;
        try std.testing.expectError(error.InUse, manager.close(root_id, work));
        try manager.forceRelease(root_id, work);
        _ = try manager.endUse(resident.lease, .complete);
        try expectStatistics(manager, 1, .{ .new_allocations = 4, .same_epoch_releases = 4, .same_epoch_allocation_bytes = 34, .subpage_allocations = 4, .subpage_allocation_bytes = 34 });
        _ = try manager.pushPartition(.{ .table = 1 }, root_id, 8, attrs, work);
        const last = (try manager.push(.{ .partition = root_id }, key(F.Uid, 24), 5, work)).newly_allocated;
        _ = try manager.endUse(last.lease, .complete);
        try manager.close(root_id, work);
        try expectStatistics(manager, 1, .{ .new_allocations = 5, .same_epoch_releases = 5, .same_epoch_allocation_bytes = 39, .subpage_allocations = 5, .subpage_allocation_bytes = 39 });
        try expectStatistics(manager, 0, .{});
    }
}

fn DestructionStorage(comptime Ledger: type) type {
    const Index = @TypeOf(@as(Ledger, undefined).index);
    const count = Ledger.resource_capacity + Ledger.partition_capacity;
    return struct {
        ids: [count]u32 = undefined,
        notices: [count]Ledger.Destruction.Notice = undefined,
        views: [Ledger.table_count]types.TableView = undefined,
        homes: [count]u32 = undefined,
        nodes: [count]u32 = undefined,
        publications: [count]Index.Publication = undefined,
        removals: [count]Index.RemovePlan = undefined,
        fn workspace(self: *@This()) Ledger.Destruction.Workspace {
            return .{ .remove_ids = &self.ids, .notices = &self.notices, .table_views = &self.views, .index_workspace = .{ .homes = &self.homes, .nodes = &self.nodes, .publications = &self.publications, .removals = &self.removals } };
        }
    };
}

test "statistics normal and forced destruction settle per-table payloads after preflight" {
    inline for (.{ 128, 256 }) |bits| {
        inline for (.{ false, true }) |forced| {
            const F = fixtures.FixtureWithStatistics(bits, true);
            var fixture = F{};
            try fixture.initialize();
            defer fixture.cleanup();
            const manager = &fixture.ledger;
            var storage = DestructionStorage(F.Ledger){};
            var work = storage.workspace();
            const first = (try manager.push(.{ .table = 0 }, key(F.Uid, 1), null, &fixture.work[0])).newly_allocated;
            const second = (try manager.push(.{ .table = 1 }, key(F.Uid, 2), 9, &fixture.work[1])).newly_allocated;
            try std.testing.expectError(error.InUse, manager.destroy(false, &work));
            try expectStatistics(manager, 0, .{ .new_allocations = 1, .subpage_allocations = 1, .subpage_allocation_bytes = 31 });
            try expectStatistics(manager, 1, .{ .new_allocations = 1, .subpage_allocations = 1, .subpage_allocation_bytes = 9 });
            if (!forced) {
                _ = try manager.endUse(first.lease, .complete);
                _ = try manager.endUse(second.lease, .complete);
            }
            try manager.destroy(forced, &work);
            // Inspect caller-owned storage: no instance operation follows destruction.
            try std.testing.expectEqualDeep(statistics.Counters{ .new_allocations = 1, .same_epoch_releases = 1, .same_epoch_allocation_bytes = 31, .subpage_allocations = 1, .subpage_allocation_bytes = 31 }, fixture.tables[0].allocation_statistics);
            try std.testing.expectEqualDeep(statistics.Counters{ .new_allocations = 1, .same_epoch_releases = 1, .same_epoch_allocation_bytes = 9, .subpage_allocations = 1, .subpage_allocation_bytes = 9 }, fixture.tables[1].allocation_statistics);
        }
    }
}

test "statistics saturate every counter without rejecting successful allocation or release" {
    inline for (.{ 128, 256 }) |bits| {
        const F = fixtures.FixtureWithStatistics(bits, true);
        var fixture = F{};
        try fixture.initialize();
        defer fixture.cleanup();
        const manager = &fixture.ledger;
        const maximum = std.math.maxInt(u64);
        // Seed the bounded fixture near the limit, then exercise real mutations.
        fixture.tables[1].allocation_statistics = .{ .new_allocations = maximum - 1, .same_epoch_releases = maximum - 1, .same_epoch_allocation_bytes = maximum - 2, .subpage_allocations = maximum - 1, .subpage_allocation_bytes = maximum - 2 };
        for ([_]u64{ 20, 21 }) |value| {
            const id = key(F.Uid, value);
            const loaded = (try manager.push(.{ .table = 1 }, id, 7, &fixture.work[1])).newly_allocated;
            _ = try manager.endUse(loaded.lease, .complete);
            try manager.close(id, &fixture.work[1]);
        }
        try expectStatistics(manager, 1, .{ .new_allocations = maximum, .same_epoch_releases = maximum, .same_epoch_allocation_bytes = maximum, .subpage_allocations = maximum, .subpage_allocation_bytes = maximum });
        try std.testing.expectEqual(@as(u64, 0), (try manager.view(1)).registered_bytes);
        try expectStatistics(manager, 0, .{});
    }
}
