//! Complete-owner source traces for the explicit C2 and B2 index candidates.
//! The tests use caller-owned bounded arrays and actual SDK-bound policy data.
const std = @import("std");
const values = @import("ledger_values").Bound;
const uid_module = @import("../uid.zig");
const c2_index = @import("index.zig");
const b2_index = @import("index_b2.zig");
const notifications = @import("notifications.zig");
const owner_module = @import("owner.zig");
const types = @import("types.zig");

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
        indices: [64]u32 = undefined,
        operations: [128]Index.Transactions.Operation = undefined,
        segments: [256]Index.Transactions.Segment = undefined,
        versions: [64]u64 = undefined,
        removals: [64]Index.RemovePlan = undefined,
        inserts: [64]Index.InsertPlan = undefined,
        simulation_nodes: [64]u32 = undefined,
        simulation_uids: [64]Uid = undefined,
        simulation_backlinks: [128]u32 = undefined,
        simulation_node_ids: [128]u32 = undefined,
    };
    const tables = [_]types.Table{
        .{
            .capacity = 8,
            .partition_capacity = 4,
            .reserved_start = 0,
            .reserved_count = 4,
            .attributes = attrs,
            .policy = policy(0),
        },
        .{
            .capacity = 8,
            .partition_capacity = 4,
            .reserved_start = 0,
            .reserved_count = 0,
            .attributes = attrs,
            .policy = policy(1),
        },
    };
    const fixed = [_]types.For(Uid).Fixed{
        .{
            .table = 0,
            .parent = null,
            .ordinal = 6,
            .uid = key(Uid, 1),
            .allocation_bytes = 31,
            .attributes = null,
        },
        .{
            .table = 0,
            .parent = key(Uid, 11),
            .ordinal = 2,
            .uid = key(Uid, 12),
            .allocation_bytes = 19,
            .attributes = null,
        },
    };
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

        rows: [16]Ledger.Row = undefined,
        shells: [8]Ledger.Shell = undefined,
        tables: [2]Ledger.TableState = undefined,
        bindings: [2]Ledger.FixedBinding = undefined,
        nodes: [24]Index.Node = undefined,
        homes: [64]Index.Home = undefined,
        records: [2][4]Notifier.Record = undefined,

        candidates: [2][64]Ledger.Candidate = undefined,
        row_work: [2][64]Ledger.Candidate = undefined,
        shell_work: [2][8]u32 = undefined,
        owner_homes: [2][64]u32 = undefined,
        notes: [2][64]Ledger.api.Notification = undefined,
        remove_ids: [2][64]u32 = undefined,
        puts: [2][64]Index.Transactions.Put = undefined,
        index_scratch: [2]IndexScratch = .{ .{}, .{} },
        cells: [2][8]Ledger.PartitionPlanning.Cell = undefined,
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
                .simulation_backlinks = &self.index_scratch[table].simulation_backlinks,
                .simulation_node_ids = &self.index_scratch[table].simulation_node_ids,
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
            for (&self.rows) |*row| row.resource.forceRelease() catch @panic("candidate owner fixture cleanup failed");
        }
    };
}

const TraceResult = struct {
    direct_number: u64,
    dynamic_number: u64,
    first_root_number: u64,
    child_number: u64,
    replacement_number: u64,
    rebound_root_number: u64,
    fixed_resource_slot: u32,
    dynamic_resource_slot: u32,
    first_root_shell_slot: u32,
    replacement_shell_slot: u32,
    rebound_fixed_slot: u32,
    final_live_nodes: usize,
    final_node_audit: usize,
    final_table0: types.TableView,
    final_table1: types.TableView,
};

fn runCompleteTrace(comptime F: type) !TraceResult {
    const Uid = F.UidType;
    var fixture = F{};
    try fixture.initialize();
    defer fixture.cleanup();
    const manager = &fixture.ledger;
    const work0 = &fixture.work[0];
    const work1 = &fixture.work[1];
    const direct_uid = key(Uid, 1);
    const root_uid = key(Uid, 11);
    const fixed_child_uid = key(Uid, 12);
    const dynamic_uid = key(Uid, 100);

    try std.testing.expectEqual(@as(u64, 31), (try manager.view(0)).registered_bytes);
    try std.testing.expectEqual(@as(u64, 0), (try manager.view(1)).registered_bytes);
    try std.testing.expectEqual(F.Owner.api.UseResult.not_in_memory, try manager.useFixed(0));
    try std.testing.expectError(error.InvalidArgument, manager.useFixedId(2));
    try std.testing.expectEqual(F.Owner.api.UseResult.not_in_memory, try manager.useFixedId(1));

    const first_direct = switch (try manager.push(.{ .table = 1 }, direct_uid, 31, work0)) {
        .newly_allocated => |borrow| borrow,
        .in_memory => return error.TestExpectedFreshFixedLoad,
        .loading => return error.TestExpectedFreshFixedLoad,
    };
    try std.testing.expectEqual(F.Owner.api.UseResult.loading, try manager.use(direct_uid));
    try std.testing.expectEqual(@import("../memory_resource.zig").EndResult.completed, try manager.endUse(first_direct.lease, .discard));
    try std.testing.expectEqual(@as(u64, 31), (try manager.view(0)).registered_bytes);
    try std.testing.expectEqual(F.Owner.api.UseResult.not_in_memory, try manager.useFixed(0));

    const direct = switch (try manager.push(.{ .table = 0 }, direct_uid, null, work0)) {
        .newly_allocated => |borrow| borrow,
        .in_memory => return error.TestExpectedFixedReload,
        .loading => return error.TestExpectedFixedReload,
    };
    const direct_number = direct.allocation_number;
    try std.testing.expect(direct_number > first_direct.allocation_number);
    _ = try manager.endUse(direct.lease, .complete);
    const fixed_hit = switch (try manager.push(.{ .table = 1 }, direct_uid, std.math.maxInt(u64), work0)) {
        .in_memory => |borrow| borrow,
        .newly_allocated => return error.TestExpectedExistingFixedHit,
        .loading => return error.TestExpectedExistingFixedHit,
    };
    try std.testing.expectEqual(direct_number, fixed_hit.allocation_number);
    try std.testing.expectEqual(@as(usize, 31), fixed_hit.region.len);
    _ = try manager.endUse(fixed_hit.lease, .complete);
    const fixed_runtime = (try manager.use(direct_uid)).in_memory;
    const fixed_bound = (try manager.useFixed(0)).in_memory;
    try std.testing.expectEqual(direct_number, fixed_runtime.allocation_number);
    try std.testing.expectEqual(direct_number, fixed_bound.allocation_number);
    _ = try manager.endUse(fixed_runtime.lease, .complete);
    _ = try manager.endUse(fixed_bound.lease, .complete);
    const direct_position = (try manager.index.find(direct_uid)) orelse return error.TestExpectedFixedIndexEntry;
    try std.testing.expectEqual(@as(u32, 0), direct_position.table);
    try std.testing.expectEqual(@as(u32, 6), direct_position.slot);

    try std.testing.expectError(error.InvalidArgument, manager.push(.{ .table = 1 }, dynamic_uid, null, work1));
    try std.testing.expect((try manager.index.find(dynamic_uid)) == null);
    try std.testing.expectEqual(@as(u64, 0), (try manager.view(1)).registered_bytes);

    const discarded_uid = key(Uid, 99);
    const discarded = switch (try manager.push(.{ .table = 1 }, discarded_uid, 23, work1)) {
        .newly_allocated => |borrow| borrow,
        .in_memory => return error.TestExpectedFreshDynamicLoad,
        .loading => return error.TestExpectedFreshDynamicLoad,
    };
    try std.testing.expectEqual(F.Owner.api.UseResult.loading, try manager.use(discarded_uid));
    try std.testing.expectEqual(F.Owner.api.PushResult.loading, try manager.push(.{ .table = 1 }, discarded_uid, 999, work1));
    _ = try manager.endUse(discarded.lease, .discard);
    try std.testing.expect((try manager.index.find(discarded_uid)) == null);
    try std.testing.expectEqual(@as(u64, 0), (try manager.view(1)).registered_bytes);
    try std.testing.expectEqual(@as(u64, 0), (try manager.view(1)).system_page_bytes);

    const dynamic = switch (try manager.push(.{ .table = 1 }, dynamic_uid, 17, work1)) {
        .newly_allocated => |borrow| borrow,
        .in_memory => return error.TestExpectedFreshDynamicLoad,
        .loading => return error.TestExpectedFreshDynamicLoad,
    };
    const dynamic_number = dynamic.allocation_number;
    _ = try manager.endUse(dynamic.lease, .complete);
    const dynamic_hit = switch (try manager.push(.{ .table = 0 }, dynamic_uid, std.math.maxInt(u64), work1)) {
        .in_memory => |borrow| borrow,
        .newly_allocated => return error.TestExpectedExistingDynamicHit,
        .loading => return error.TestExpectedExistingDynamicHit,
    };
    try std.testing.expectEqual(dynamic_number, dynamic_hit.allocation_number);
    try std.testing.expectEqual(@as(usize, 17), dynamic_hit.region.len);
    _ = try manager.endUse(dynamic_hit.lease, .complete);
    const dynamic_position = (try manager.index.find(dynamic_uid)) orelse return error.TestExpectedDynamicIndexEntry;
    try std.testing.expectEqual(@as(u32, 1), dynamic_position.table);
    try std.testing.expectEqual(@as(u32, 8), dynamic_position.slot);

    _ = try manager.updateUid(dynamic_uid, .{ .attributes = .{ .recovery_cost_coefficient = 7 } });
    try std.testing.expectEqual(.recoverable, manager.rows[dynamic_position.slot].attributes.recoverability);
    try std.testing.expectEqual(@as(u32, 7), manager.rows[dynamic_position.slot].attributes.recovery_cost_coefficient);
    try std.testing.expectEqual(.unobservable_now, manager.rows[dynamic_position.slot].attributes.access_loss_impact);

    const grow = (try manager.use(dynamic_uid)).in_memory;
    grow.region[11] = 0xa7;
    const grown = switch (try manager.updateLease(grow.lease, .{ .allocation_bytes = std.heap.pageSize() + 1 })) {
        .updated => |bytes| bytes orelse return error.TestExpectedResidentGrowth,
        .registration_only => return error.TestExpectedResidentGrowth,
        .in_use => return error.TestExpectedResidentGrowth,
        .force_released => return error.TestExpectedResidentGrowth,
    };
    try std.testing.expectEqual(@as(usize, std.heap.pageSize() + 1), grown.len);
    try std.testing.expectEqual(@as(u8, 0xa7), grown[11]);
    try std.testing.expectEqual(@as(u64, std.heap.pageSize() + 1), (try manager.view(1)).registered_bytes);
    try std.testing.expectEqual(@as(u64, 2 * std.heap.pageSize()), (try manager.view(1)).system_page_bytes);
    _ = try manager.endUse(grow.lease, .complete);

    const shared_a = (try manager.use(dynamic_uid)).in_memory;
    const shared_b = (try manager.use(dynamic_uid)).in_memory;
    const before_protected_close = try manager.view(1);
    try std.testing.expectError(error.InUse, manager.close(dynamic_uid, work1));
    try std.testing.expectEqual(before_protected_close, try manager.view(1));
    try std.testing.expect((try manager.index.find(dynamic_uid)) != null);
    _ = try manager.endUse(shared_a.lease, .complete);
    _ = try manager.endUse(shared_b.lease, .complete);
    _ = try manager.updateUid(dynamic_uid, .{ .attributes = .{ .access_loss_impact = .fatal } });
    const before_fatal_close = try manager.view(1);
    try std.testing.expectError(error.Blocked, manager.close(dynamic_uid, work1));
    try std.testing.expectEqual(before_fatal_close, try manager.view(1));
    try std.testing.expect((try manager.index.find(dynamic_uid)) != null);
    const late = (try manager.use(dynamic_uid)).in_memory;
    try manager.forceRelease(dynamic_uid, work1);
    try std.testing.expectEqual(@import("../memory_resource.zig").EndResult.force_released, try manager.endUse(late.lease, .complete));
    try std.testing.expect((try manager.index.find(dynamic_uid)) == null);
    try std.testing.expectEqual(@as(u64, 0), (try manager.view(1)).registered_bytes);
    try std.testing.expectEqual(@as(u64, 0), (try manager.view(1)).system_page_bytes);

    const first_root = try manager.pushPartition(.{ .table = 0 }, root_uid, 4, attrs, work0);
    try std.testing.expect(first_root.created);
    const first_root_position = (try manager.index.find(root_uid)) orelse return error.TestExpectedRootShell;
    try std.testing.expectEqual(.partition, first_root_position.kind);
    const first_root_slot = first_root_position.slot;
    try std.testing.expectEqual(@as(u32, 0), manager.shells[first_root_slot].start);
    try std.testing.expectEqual(@as(u32, 4), manager.shells[first_root_slot].capacity);
    try std.testing.expectEqual(first_root.allocation_number, manager.shells[first_root_slot].allocation_number);
    try std.testing.expectEqual(@as(u32, 2), manager.bindings[1].slot.load(.acquire));
    try std.testing.expectEqual(first_root.allocation_number, manager.bindings[1].parent_number.load(.acquire));
    try std.testing.expectEqual(@as(u64, 50), (try manager.view(0)).registered_bytes);
    try std.testing.expectEqual(F.Owner.api.UseResult.not_in_memory, try manager.useFixed(1));

    _ = manager.epoch.fetchAdd(1, .acq_rel);
    const root_hit = try manager.pushPartition(.{ .table = 1 }, root_uid, 0, .{
        .recoverability = .not_recoverable,
        .recovery_cost_coefficient = 99,
        .access_loss_impact = .fatal,
    }, work0);
    try std.testing.expect(!root_hit.created);
    try std.testing.expectEqual(first_root.allocation_number, root_hit.allocation_number);
    try std.testing.expectEqual(@as(u64, 2), manager.shells[first_root_slot].history.last_activity_epoch);
    try std.testing.expectEqual(attrs, manager.shells[first_root_slot].attributes);

    const fixed_child = switch (try manager.push(.{ .partition = root_uid }, fixed_child_uid, 19, work0)) {
        .newly_allocated => |borrow| borrow,
        .in_memory => return error.TestExpectedFreshNestedFixedLoad,
        .loading => return error.TestExpectedFreshNestedFixedLoad,
    };
    const fixed_child_number = fixed_child.allocation_number;
    _ = try manager.endUse(fixed_child.lease, .complete);
    const fixed_child_use = (try manager.useFixed(1)).in_memory;
    try std.testing.expectEqual(fixed_child_number, fixed_child_use.allocation_number);
    _ = try manager.endUse(fixed_child_use.lease, .complete);
    const fixed_child_id_use = (try manager.useFixedId(1)).in_memory;
    try std.testing.expectEqual(fixed_child_use.lease.slot, fixed_child_id_use.lease.slot);
    try std.testing.expectEqual(fixed_child_number, fixed_child_id_use.allocation_number);
    _ = try manager.endUse(fixed_child_id_use.lease, .complete);
    _ = try manager.updateUid(root_uid, .{ .attributes = .{ .recovery_cost_coefficient = 7 } });
    try std.testing.expectEqual(@as(u32, 7), manager.shells[first_root_slot].attributes.recovery_cost_coefficient);
    try std.testing.expectEqual(.unobservable_now, manager.shells[first_root_slot].attributes.access_loss_impact);
    try std.testing.expectEqual(@as(u32, 7), manager.rows[2].attributes.recovery_cost_coefficient);
    try std.testing.expectEqual(.unobservable_now, manager.rows[2].attributes.access_loss_impact);
    try std.testing.expectEqual(@as(u64, 50), (try manager.view(0)).registered_bytes);
    try manager.close(fixed_child_uid, work0);
    try std.testing.expectEqual(F.Owner.api.UseResult.not_in_memory, try manager.useFixed(1));
    try std.testing.expectEqual(@as(u64, 50), (try manager.view(0)).registered_bytes);

    const first_child_uid = key(Uid, 22);
    const nested_uid = key(Uid, 33);
    const replacement_uid = key(Uid, 44);
    const first_child = try manager.pushPartition(.{ .partition = root_uid }, first_child_uid, 2, attrs, work0);
    try std.testing.expect(first_child.created);
    const first_child_slot = (try manager.index.find(first_child_uid)).?.slot;
    try std.testing.expectEqual(@as(u32, 0), manager.shells[first_child_slot].start);
    const nested = try manager.pushPartition(.{ .partition = first_child_uid }, nested_uid, 1, attrs, work0);
    try std.testing.expect(nested.created);
    const nested_slot = (try manager.index.find(nested_uid)).?.slot;
    try std.testing.expectEqual(@as(u32, 0), manager.shells[nested_slot].start);
    try std.testing.expectError(error.InvalidArgument, manager.updateUid(root_uid, .{ .allocation_bytes = 64 }));

    const replacement = try manager.pushPartition(.{ .partition = root_uid }, replacement_uid, 2, attrs, work0);
    try std.testing.expect(replacement.created);
    const replacement_position = (try manager.index.find(replacement_uid)) orelse return error.TestExpectedReplacementShell;
    const replacement_slot = replacement_position.slot;
    try std.testing.expectEqual(.partition, replacement_position.kind);
    try std.testing.expectEqual(@as(u32, 0), manager.shells[replacement_slot].start);
    try std.testing.expect((try manager.index.find(first_child_uid)) == null);
    try std.testing.expect((try manager.index.find(nested_uid)) == null);

    _ = try manager.updateUid(root_uid, .{ .attributes = .{ .access_loss_impact = .fatal } });
    try std.testing.expectEqual(.fatal, manager.shells[first_root_slot].attributes.access_loss_impact);
    try std.testing.expectEqual(.fatal, manager.shells[replacement_slot].attributes.access_loss_impact);
    try manager.close(root_uid, work0);
    try std.testing.expect((try manager.index.find(root_uid)) == null);
    try std.testing.expect((try manager.index.find(fixed_child_uid)) == null);
    try std.testing.expect((try manager.index.find(replacement_uid)) == null);
    try std.testing.expectEqual(types.no_slot, manager.bindings[1].slot.load(.acquire));
    try std.testing.expectEqual(F.Owner.api.UseResult.not_in_memory, try manager.useFixedId(1));
    try std.testing.expectEqual(@as(u64, 31), (try manager.view(0)).registered_bytes);

    const rebound = try manager.pushPartition(.{ .table = 0 }, root_uid, 4, attrs, work0);
    try std.testing.expect(rebound.created);
    try std.testing.expect(rebound.allocation_number > first_root.allocation_number);
    const rebound_root_position = (try manager.index.find(root_uid)) orelse return error.TestExpectedReboundRoot;
    const rebound_root_slot = rebound_root_position.slot;
    try std.testing.expectEqual(@as(u32, 2), manager.bindings[1].slot.load(.acquire));
    try std.testing.expectEqual(rebound.allocation_number, manager.bindings[1].parent_number.load(.acquire));
    const rebound_fixed_position = (try manager.index.find(fixed_child_uid)) orelse return error.TestExpectedReboundFixed;
    try std.testing.expectEqual(.resource, rebound_fixed_position.kind);
    try std.testing.expectEqual(@as(u32, 2), rebound_fixed_position.slot);
    try std.testing.expectEqual(@as(u64, 50), (try manager.view(0)).registered_bytes);
    const reused_shell = rebound_root_slot == first_root_slot or
        rebound_root_slot == first_child_slot or
        rebound_root_slot == nested_slot or
        rebound_root_slot == replacement_slot;
    try std.testing.expect(reused_shell);
    try manager.close(root_uid, work0);
    try std.testing.expect((try manager.index.find(fixed_child_uid)) == null);
    try std.testing.expectEqual(types.no_slot, manager.bindings[1].slot.load(.acquire));

    const final_fixed_position = (try manager.index.find(direct_uid)) orelse return error.TestExpectedFinalFixed;
    try std.testing.expectEqual(.resource, final_fixed_position.kind);
    try std.testing.expectEqual(@as(u32, 0), final_fixed_position.table);
    try std.testing.expectEqual(@as(u32, 6), final_fixed_position.slot);
    try std.testing.expect((try manager.index.find(dynamic_uid)) == null);
    try std.testing.expect((try manager.index.find(root_uid)) == null);
    const audit = try manager.index.audit();
    try std.testing.expectEqual(@as(usize, 24), audit.node_body);
    var final_live_nodes: usize = 0;
    for (0..manager.index.nodes.len) |node| {
        if ((try manager.index.entryAt(@intCast(node))) != null) final_live_nodes += 1;
    }
    try std.testing.expectEqual(@as(usize, 1), final_live_nodes);
    const final_table0 = try manager.view(0);
    const final_table1 = try manager.view(1);
    try std.testing.expectEqual(@as(u64, 31), final_table0.registered_bytes);
    try std.testing.expectEqual(@as(u64, std.heap.pageSize()), final_table0.system_page_bytes);
    try std.testing.expectEqual(@as(u64, 0), final_table1.registered_bytes);
    try std.testing.expectEqual(@as(u64, 0), final_table1.system_page_bytes);

    return .{
        .direct_number = direct_number,
        .dynamic_number = dynamic_number,
        .first_root_number = first_root.allocation_number,
        .child_number = first_child.allocation_number,
        .replacement_number = replacement.allocation_number,
        .rebound_root_number = rebound.allocation_number,
        .fixed_resource_slot = final_fixed_position.slot,
        .dynamic_resource_slot = dynamic_position.slot,
        .first_root_shell_slot = first_root_slot,
        .replacement_shell_slot = replacement_slot,
        .rebound_fixed_slot = rebound_fixed_position.slot,
        .final_live_nodes = final_live_nodes,
        .final_node_audit = audit.node_body,
        .final_table0 = final_table0,
        .final_table1 = final_table1,
    };
}

test "C2 and B2 complete-owner traces agree at both UID widths" {
    var reference: ?TraceResult = null;
    inline for (.{
        .{ .bits = 128, .flavor = CandidateFlavor.c2 },
        .{ .bits = 256, .flavor = CandidateFlavor.c2 },
        .{ .bits = 128, .flavor = CandidateFlavor.b2 },
        .{ .bits = 256, .flavor = CandidateFlavor.b2 },
    }) |case| {
        const F = Fixture(case.bits, case.flavor);
        const result = try runCompleteTrace(F);
        if (reference) |expected| {
            try std.testing.expectEqualDeep(expected, result);
        } else {
            reference = result;
        }
    }
}

const BlockingNotification = struct {
    entered: std.atomic.Value(bool) = std.atomic.Value(bool).init(false),
    release: std.atomic.Value(bool) = std.atomic.Value(bool).init(false),
    calls: std.atomic.Value(u32) = std.atomic.Value(u32).init(0),
};

fn BlockingCallback(comptime Uid: type) type {
    return struct {
        fn callback(context: ?*anyopaque, _: Uid, _: u64) callconv(.c) void {
            const state: *BlockingNotification = @ptrCast(@alignCast(context orelse unreachable));
            _ = state.calls.fetchAdd(1, .release);
            state.entered.store(true, .release);
            while (!state.release.load(.acquire)) std.atomic.spinLoopHint();
        }
    };
}

fn CloseThreadState(comptime Owner: type) type {
    return struct {
        manager: *Owner,
        uid: Owner.uid_type,
        work: *Owner.Workspace,
        done: std.atomic.Value(bool) = std.atomic.Value(bool).init(false),
        ok: bool = false,
    };
}

fn CloseThread(comptime Owner: type) type {
    return struct {
        fn run(state: *CloseThreadState(Owner)) void {
            state.manager.close(state.uid, state.work) catch {
                state.done.store(true, .release);
                return;
            };
            state.ok = true;
            state.done.store(true, .release);
        }
    };
}

fn UseThreadState(comptime Owner: type) type {
    return struct {
        manager: *Owner,
        uid: Owner.uid_type,
        done: std.atomic.Value(bool) = std.atomic.Value(bool).init(false),
        ok: bool = false,
        allocation_number: u64 = 0,
    };
}

fn UseThread(comptime Owner: type) type {
    return struct {
        fn run(state: *UseThreadState(Owner)) void {
            const result = state.manager.use(state.uid) catch {
                state.done.store(true, .release);
                return;
            };
            switch (result) {
                .in_memory => |borrow| {
                    state.allocation_number = borrow.allocation_number;
                    _ = state.manager.endUse(borrow.lease, .complete) catch {
                        state.done.store(true, .release);
                        return;
                    };
                    state.ok = true;
                },
                .not_in_memory, .loading => {},
            }
            state.done.store(true, .release);
        }
    };
}

fn runIndependentTableTrace(comptime F: type) !void {
    var fixture = F{};
    try fixture.initialize();
    defer fixture.cleanup();
    const Uid = F.UidType;
    const manager = &fixture.ledger;
    const close_uid = key(Uid, 701);
    const use_uid = key(Uid, 702);

    var gate = BlockingNotification{};
    _ = try manager.subscribe(1, BlockingCallback(Uid).callback, &gate);
    const table1_load = switch (try manager.push(.{ .table = 1 }, close_uid, 17, &fixture.work[1])) {
        .newly_allocated => |borrow| borrow,
        .in_memory => return error.TestExpectedTable1FreshLoad,
        .loading => return error.TestExpectedTable1FreshLoad,
    };
    _ = try manager.endUse(table1_load.lease, .complete);
    const table0_load = switch (try manager.push(.{ .table = 0 }, use_uid, 17, &fixture.work[0])) {
        .newly_allocated => |borrow| borrow,
        .in_memory => return error.TestExpectedTable0FreshLoad,
        .loading => return error.TestExpectedTable0FreshLoad,
    };
    const table0_number = table0_load.allocation_number;
    _ = try manager.endUse(table0_load.lease, .complete);

    var close_state = CloseThreadState(F.Owner){ .manager = manager, .uid = close_uid, .work = &fixture.work[1] };
    var closer = try std.Thread.spawn(.{}, CloseThread(F.Owner).run, .{&close_state});
    var entry_polls: usize = 0;
    while (!gate.entered.load(.acquire) and !close_state.done.load(.acquire) and entry_polls < 1_000_000) : (entry_polls += 1) {
        std.Thread.yield() catch {};
    }
    const callback_entered = gate.entered.load(.acquire);
    if (!callback_entered) {
        gate.release.store(true, .release);
        closer.join();
        try std.testing.expect(callback_entered);
        return;
    }

    var use_state = UseThreadState(F.Owner){ .manager = manager, .uid = use_uid };
    var user = std.Thread.spawn(.{}, UseThread(F.Owner).run, .{&use_state}) catch |err| {
        gate.release.store(true, .release);
        closer.join();
        return err;
    };
    var use_polls: usize = 0;
    while (!use_state.done.load(.acquire) and use_polls < 1_000_000) : (use_polls += 1) {
        std.Thread.yield() catch {};
    }
    const use_finished_before_release = use_state.done.load(.acquire);
    gate.release.store(true, .release);
    closer.join();
    user.join();

    try std.testing.expect(use_finished_before_release);
    try std.testing.expect(close_state.ok);
    try std.testing.expect(use_state.ok);
    try std.testing.expectEqual(table0_number, use_state.allocation_number);
    try std.testing.expectEqual(@as(u32, 1), gate.calls.load(.acquire));
    try std.testing.expect((try manager.index.find(close_uid)) == null);
    try std.testing.expect((try manager.index.find(use_uid)) != null);
}

test "a blocked table-one notification does not block a table-zero use" {
    inline for (.{
        .{ .bits = 128, .flavor = CandidateFlavor.c2 },
        .{ .bits = 256, .flavor = CandidateFlavor.c2 },
        .{ .bits = 128, .flavor = CandidateFlavor.b2 },
        .{ .bits = 256, .flavor = CandidateFlavor.b2 },
    }) |case| {
        try runIndependentTableTrace(Fixture(case.bits, case.flavor));
    }
}

fn runOsAllocationRefusal(comptime F: type) !void {
    var fixture = F{};
    try fixture.initialize();
    defer fixture.cleanup();
    const manager = &fixture.ledger;
    const resource_uid = key(F.UidType, 1);
    const loader = (try manager.push(.{ .table = 0 }, resource_uid, null, &fixture.work[0])).newly_allocated;
    @memset(loader.region, 73);
    try std.testing.expect((try manager.endUse(loader.lease, .complete)) == .completed);
    const held = (try manager.use(resource_uid)).in_memory;
    const row = &manager.rows[held.lease.slot];
    const before_resource = row.resource;
    const before_attributes = row.attributes;
    const before_view = try manager.view(0);
    const before_position = (try manager.index.find(resource_uid)).?;
    const page_size = std.heap.pageSize();
    const impossible_size = std.math.maxInt(usize) & ~(page_size - 1);
    try std.testing.expectEqual(impossible_size, try @import("../memory_region.zig").requiredBytes(impossible_size));
    try std.testing.expectError(error.AllocationFailed, manager.updateLease(held.lease, .{
        .allocation_bytes = impossible_size,
        .attributes = .{ .recovery_cost_coefficient = 99 },
    }));
    try std.testing.expectEqualDeep(before_resource, row.resource);
    try std.testing.expectEqualDeep(before_attributes, row.attributes);
    try std.testing.expectEqualDeep(before_view, try manager.view(0));
    try std.testing.expectEqualDeep(before_position, (try manager.index.find(resource_uid)).?);
    try std.testing.expectEqual(@as(usize, 31), held.region.len);
    for (held.region) |byte| try std.testing.expectEqual(@as(u8, 73), byte);
    try std.testing.expect((try manager.endUse(held.lease, .complete)) == .completed);
}

test "complete owner preserves lease region registration attributes and accounting on OS allocation refusal" {
    inline for (.{
        .{ .bits = 128, .flavor = CandidateFlavor.c2 },
        .{ .bits = 256, .flavor = CandidateFlavor.c2 },
        .{ .bits = 128, .flavor = CandidateFlavor.b2 },
        .{ .bits = 256, .flavor = CandidateFlavor.b2 },
    }) |case| {
        try runOsAllocationRefusal(Fixture(case.bits, case.flavor));
    }
}
