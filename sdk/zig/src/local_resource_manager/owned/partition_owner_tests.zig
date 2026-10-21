//! Owner-level composition tests for bounded partition placement and teardown.
const std = @import("std");
const values = @import("ledger_values").Bound;
const uid_module = @import("../uid.zig");
const index = @import("index.zig");
const notify = @import("notifications.zig");
const owner_module = @import("owner.zig");
const types = @import("types.zig");

const attrs = types.Attributes{
    .recoverability = .recoverable,
    .recovery_cost_coefficient = 1,
    .access_loss_impact = .unobservable_now,
};
const fatal_attrs = types.Attributes{
    .recoverability = .recoverable,
    .recovery_cost_coefficient = 1,
    .access_loss_impact = .fatal,
};

fn key(comptime Uid: type, value: u64) Uid {
    var result = Uid{ .bytes = @splat(0) };
    std.mem.writeInt(u64, result.bytes[result.bytes.len - 8 ..][0..8], value, .big);
    return result;
}

fn policy() types.Policy {
    return .{
        .score_epochs = @intCast(values.table_0_activity_score_epochs),
        .idle_epochs = @intCast(values.table_0_idle_epochs),
        .protection = switch (values.table_0_capacity_protection_method) {
            1 => .concentrated,
            2 => .smooth,
            else => unreachable,
        },
        .concentrated_trigger_percent = @intCast(values.table_0_concentrated_trigger_percent),
        .concentrated_target_percent = @intCast(values.table_0_concentrated_target_percent),
        .smooth_trigger_percent = @intCast(values.table_0_smooth_trigger_percent),
        .smooth_emergency_percent = @intCast(values.table_0_smooth_emergency_percent),
        .smooth_interval_epochs = @intCast(values.table_0_smooth_interval_epochs),
        .smooth_release_count = @intCast(values.table_0_smooth_release_count),
        .tick_maximum_actions = @intCast(values.table_0_tick_maximum_actions),
        .release_check = .{
            .enabled = values.table_0_release_check_enabled == 2,
            .tolerance_parts_per_million = @intCast(values.table_0_release_tolerance_parts_per_million),
            .tolerance_bytes = values.table_0_release_tolerance_bytes,
        },
    };
}

fn PartitionFixture() type {
    // Exercise UID256; the global activity bitmap and table policy come from
    // the actual FreedomPoint Bound projection.
    const Uid = uid_module.Identifier(256);
    const Index = index.Candidate(Uid, .c2_chain);
    const tables = [_]types.Table{.{
        .capacity = 32,
        .partition_capacity = 16,
        .reserved_start = 0,
        .reserved_count = 24,
        .attributes = attrs,
        .policy = policy(),
    }};
    const fixed = [_]types.For(Uid).Fixed{
        .{
            .table = 0,
            .parent = null,
            .ordinal = 31,
            .uid = key(Uid, 1),
            .allocation_bytes = 13,
            .attributes = null,
        },
        .{
            .table = 0,
            .parent = key(Uid, 22),
            .ordinal = 23,
            .uid = key(Uid, 23),
            .allocation_bytes = 31,
            .attributes = fatal_attrs,
        },
        .{
            .table = 0,
            .parent = key(Uid, 30),
            .ordinal = 23,
            .uid = key(Uid, 31),
            .allocation_bytes = 51,
            .attributes = null,
        },
    };
    const Ledger = owner_module.Owner(
        .{
            .uid_width = 256,
            .activity_width = @intCast(values.activity_bitmap_width),
            .index_buckets = 32,
        },
        &tables,
        &fixed,
        Index,
    );
    const Notifier = notify.Notifier(Uid);

    return struct {
        pub const UidType = Uid;
        pub const IndexType = Index;
        pub const Owner = Ledger;
        pub const Capture = NotificationCapture(Uid);

        rows: [32]Ledger.Row = undefined,
        shells: [16]Ledger.Shell = undefined,
        tables: [1]Ledger.TableState = undefined,
        bindings: [3]Ledger.FixedBinding = undefined,
        nodes: [48]Index.Node = undefined,
        homes: [32]Index.Home = undefined,
        records: [2]Notifier.Record = undefined,

        candidates: [32]Ledger.Candidate = undefined,
        row_work: [32]Ledger.Candidate = undefined,
        shell_work: [16]u32 = undefined,
        home_work: [64]u32 = undefined,
        publication_work: [64]Index.Publication = undefined,
        removal_work: [64]Index.RemovePlan = undefined,
        insert_work: [64]Index.InsertPlan = undefined,
        node_work: [64]u32 = undefined,
        remove_id_work: [64]u32 = undefined,
        put_work: [64]Index.Transactions.Put = undefined,
        note_work: [64]Ledger.api.Notification = undefined,
        cells: [24]Ledger.PartitionPlanning.Cell = undefined,
        children: [16]Ledger.PartitionPlanning.Child = undefined,
        child_slots: [16]u32 = undefined,
        numbers: [16]u64 = undefined,
        merge_a: [16]u64 = undefined,
        merge_b: [16]u64 = undefined,
        best_numbers: [16]u64 = undefined,

        ledger: Ledger = undefined,
        work: Ledger.Workspace = undefined,

        pub fn initialize(self: *@This()) !void {
            var subscriptions = [_][]Notifier.Record{self.records[0..]};
            self.ledger = try Ledger.init(
                &self.rows,
                &self.shells,
                &self.tables,
                &self.bindings,
                &self.nodes,
                &self.homes,
                &subscriptions,
            );
            self.work = .{
                .candidates = &self.candidates,
                .rows = &self.row_work,
                .shells = &self.shell_work,
                .homes = &self.home_work,
                .notes = &self.note_work,
                .remove_ids = &self.remove_id_work,
                .puts = &self.put_work,
                .index_workspace = .{
                    .homes = &self.home_work,
                    .nodes = &self.node_work,
                    .publications = &self.publication_work,
                    .removals = &self.removal_work,
                    .inserts = &self.insert_work,
                },
                .cells = &self.cells,
                .children = &self.children,
                .child_slots = &self.child_slots,
                .numbers = &self.numbers,
                .merge_a = &self.merge_a,
                .merge_b = &self.merge_b,
                .best_numbers = &self.best_numbers,
            };
        }

        pub fn cleanup(self: *@This()) void {
            for (&self.rows) |*row| row.resource.forceRelease() catch @panic("partition fixture OS cleanup failed");
        }
    };
}

fn NotificationCapture(comptime Uid: type) type {
    return struct {
        pub const State = struct {
            count: usize = 0,
            notes: [128]types.For(Uid).Notification = undefined,

            pub fn clear(self: *@This()) void {
                self.count = 0;
            }

            pub fn saw(self: *const @This(), uid: Uid, allocation_number: u64) bool {
                for (self.notes[0..self.count]) |note| {
                    if (note.uid.eql(uid) and note.allocation_number == allocation_number) return true;
                }
                return false;
            }
        };

        pub fn callback(context: ?*anyopaque, uid: Uid, allocation_number: u64) callconv(.c) void {
            const state: *State = @ptrCast(@alignCast(context orelse unreachable));
            std.debug.assert(state.count < state.notes.len);
            state.notes[state.count] = .{ .uid = uid, .allocation_number = allocation_number };
            state.count += 1;
        }
    };
}

fn partitionSlot(manager: anytype, uid: anytype) !u32 {
    const found = (try manager.index.find(uid)) orelse return error.TestExpectedPartition;
    if (found.kind != .partition) return error.TestExpectedPartition;
    return found.slot;
}

fn resourceSlot(manager: anytype, uid: anytype) !u32 {
    const found = (try manager.index.find(uid)) orelse return error.TestExpectedResource;
    if (found.kind != .resource) return error.TestExpectedResource;
    return found.slot;
}

test "partition owner binds fixed declarations, replaces a shell, and preserves fixed registration" {
    const F = PartitionFixture();
    const Uid = F.UidType;
    var fixture = F{};
    try fixture.initialize();
    defer fixture.cleanup();
    const manager = &fixture.ledger;
    const work = &fixture.work;

    const direct_uid = key(Uid, 1);
    try std.testing.expectEqual(@as(u32, 31), manager.bindings[0].slot.load(.acquire));
    try std.testing.expectEqual(F.Owner.api.UseResult.not_in_memory, try manager.useFixed(0));
    const direct = (try manager.push(.{ .table = 0 }, direct_uid, null, work)).newly_allocated;
    _ = try manager.endUse(direct.lease, .complete);
    const fixed_access = (try manager.useFixed(0)).in_memory;
    const uid_access = (try manager.use(direct_uid)).in_memory;
    try std.testing.expectEqual(direct.lease.slot, fixed_access.lease.slot);
    try std.testing.expectEqual(direct.lease.slot, uid_access.lease.slot);
    _ = try manager.endUse(fixed_access.lease, .complete);
    _ = try manager.endUse(uid_access.lease, .complete);

    const root_uid = key(Uid, 10);
    const parent_uid = key(Uid, 20);
    const old_shell_uid = key(Uid, 22);
    const old_fixed_uid = key(Uid, 23);
    const new_shell_uid = key(Uid, 30);
    const new_fixed_uid = key(Uid, 31);
    const root = try manager.pushPartition(.{ .table = 0 }, root_uid, 24, attrs, work);
    const root_slot = try partitionSlot(manager, root_uid);
    try std.testing.expect(root.created);
    try std.testing.expectEqual(@as(u32, 0), manager.shells[root_slot].start);
    try std.testing.expectEqual(@as(u32, 24), manager.shells[root_slot].capacity);

    const parent = try manager.pushPartition(.{ .partition = root_uid }, parent_uid, 24, attrs, work);
    const parent_slot = try partitionSlot(manager, parent_uid);
    try std.testing.expect(parent.created);
    try std.testing.expectEqual(manager.shells[root_slot].capacity, manager.shells[parent_slot].capacity);

    const old = try manager.pushPartition(.{ .partition = parent_uid }, old_shell_uid, 24, attrs, work);
    const old_slot = try partitionSlot(manager, old_shell_uid);
    try std.testing.expect(old.created);
    try std.testing.expectEqual(manager.shells[parent_slot].capacity, manager.shells[old_slot].capacity);
    try std.testing.expectEqual(@as(u32, 23), manager.bindings[1].slot.load(.acquire));
    try std.testing.expectEqual(old.allocation_number, manager.bindings[1].parent_number.load(.acquire));
    try std.testing.expectEqual(old.allocation_number, manager.rows[23].fixed_parent_number);
    try std.testing.expect(manager.rows[23].attributes.access_loss_impact == .fatal);
    try std.testing.expectEqual(F.Owner.api.UseResult.not_in_memory, try manager.useFixed(1));
    try std.testing.expectEqual(F.Owner.api.UseResult.not_in_memory, try manager.use(old_fixed_uid));

    // Fatal metadata does not protect an offline fixed registration from tree close.
    try manager.close(old_shell_uid, work);
    try std.testing.expect((try manager.index.find(old_shell_uid)) == null);
    try std.testing.expect((try manager.index.find(old_fixed_uid)) == null);
    try std.testing.expectEqual(types.no_slot, manager.bindings[1].slot.load(.acquire));
    try std.testing.expectEqual(root_slot, try partitionSlot(manager, root_uid));
    try std.testing.expectEqual(parent_slot, try partitionSlot(manager, parent_uid));

    // The just-freed shell is reused for an identical nested declaration.
    _ = try manager.pushPartition(.{ .partition = parent_uid }, old_shell_uid, 24, attrs, work);
    try std.testing.expectEqual(old_slot, try partitionSlot(manager, old_shell_uid));
    try std.testing.expectEqual(@as(u32, 23), manager.bindings[1].slot.load(.acquire));

    // Replacing that complete child removes the old constant-UID mapping and
    // publishes the new shell plus its start+ordinal fixed row together.
    const replacement = try manager.pushPartition(.{ .partition = parent_uid }, new_shell_uid, 24, attrs, work);
    try std.testing.expect(replacement.created);
    const new_slot = try partitionSlot(manager, new_shell_uid);
    try std.testing.expect((try manager.index.find(old_shell_uid)) == null);
    try std.testing.expect((try manager.index.find(old_fixed_uid)) == null);
    try std.testing.expectEqual(@as(u32, 0), manager.shells[new_slot].start);
    try std.testing.expectEqual(@as(u32, 24), manager.shells[new_slot].capacity);
    try std.testing.expectEqual(@as(u32, 23), manager.bindings[2].slot.load(.acquire));
    try std.testing.expectEqual(replacement.allocation_number, manager.bindings[2].parent_number.load(.acquire));
    try std.testing.expectEqual(replacement.allocation_number, manager.rows[23].fixed_parent_number);
    try std.testing.expectEqual(@as(u32, 2), manager.rows[23].fixed_declaration.?);
    try std.testing.expectEqual(@as(u32, 23), try resourceSlot(manager, new_fixed_uid));
    try std.testing.expectEqual(F.Owner.api.UseResult.not_in_memory, try manager.useFixed(1));
    try std.testing.expectEqual(F.Owner.api.UseResult.not_in_memory, try manager.useFixed(2));

    // A repeat partition push ignores even invalid new parameters and records
    // activity on the existing shell.
    manager.epoch.store(5, .release);
    const repeated = try manager.pushPartition(
        .{ .table = 99 },
        new_shell_uid,
        0,
        fatal_attrs,
        work,
    );
    try std.testing.expect(!repeated.created);
    try std.testing.expectEqual(replacement.allocation_number, repeated.allocation_number);
    try std.testing.expectEqual(@as(u64, 5), manager.shells[new_slot].history.last_activity_epoch);
    try std.testing.expect(manager.shells[new_slot].attributes.access_loss_impact == .unobservable_now);

    // Tree attribute updates are partial, and a partition cannot take a size.
    _ = try manager.updateUid(new_shell_uid, .{ .attributes = .{ .recovery_cost_coefficient = 9 } });
    try std.testing.expectEqual(@as(u32, 9), manager.shells[new_slot].attributes.recovery_cost_coefficient);
    try std.testing.expect(manager.shells[new_slot].attributes.access_loss_impact == .unobservable_now);
    try std.testing.expectEqual(@as(u32, 9), manager.rows[23].attributes.recovery_cost_coefficient);
    const registered_before_invalid_update = manager.tables[0].registered_bytes;
    try std.testing.expectError(error.InvalidArgument, manager.updateUid(new_shell_uid, .{ .allocation_bytes = 7 }));
    try std.testing.expectEqual(registered_before_invalid_update, manager.tables[0].registered_bytes);
    try std.testing.expectEqual(@as(u32, 9), manager.rows[23].attributes.recovery_cost_coefficient);

    // A dynamic registration must supply its size. Zero is a valid dynamic size.
    const discarded_uid = key(Uid, 55);
    try std.testing.expectError(
        error.InvalidArgument,
        manager.push(.{ .partition = new_shell_uid }, discarded_uid, null, work),
    );
    try std.testing.expect((try manager.index.find(discarded_uid)) == null);
    const dynamic = (try manager.push(.{ .partition = new_shell_uid }, discarded_uid, 0, work)).newly_allocated;
    try std.testing.expectEqual(@as(usize, 0), dynamic.region.len);
    _ = try manager.endUse(dynamic.lease, .discard);
    try std.testing.expectEqual(F.Owner.api.UseResult.not_in_memory, try manager.use(discarded_uid));
    try std.testing.expect((try manager.index.find(discarded_uid)) == null);

    // Loading discard on a fixed declaration keeps its UID and registration.
    const fixed_load = (try manager.push(.{ .table = 99 }, new_fixed_uid, null, work)).newly_allocated;
    try std.testing.expectEqual(@as(usize, 51), fixed_load.region.len);
    _ = try manager.endUse(fixed_load.lease, .discard);
    const new_fixed_slot = try resourceSlot(manager, new_fixed_uid);
    try std.testing.expect(manager.rows[new_fixed_slot].resource.registration != null);
    try std.testing.expectEqual(@as(u64, 51), manager.rows[new_fixed_slot].resource.registration.?.allocation_bytes);
    try std.testing.expectEqual(F.Owner.api.UseResult.not_in_memory, try manager.useFixed(2));
    try std.testing.expectEqual(F.Owner.api.UseResult.not_in_memory, try manager.use(new_fixed_uid));

    // Direct fixed access and UID access resolve to the same runtime row.
    const fixed_reload = (try manager.push(.{ .table = 99 }, new_fixed_uid, null, work)).newly_allocated;
    try std.testing.expectEqual(F.Owner.api.UseResult.loading, try manager.useFixed(2));
    try std.testing.expectEqual(F.Owner.api.UseResult.loading, try manager.use(new_fixed_uid));
    _ = try manager.endUse(fixed_reload.lease, .complete);
    const fixed_runtime_access = (try manager.useFixed(2)).in_memory;
    const fixed_uid_access = (try manager.use(new_fixed_uid)).in_memory;
    try std.testing.expectEqual(fixed_runtime_access.lease.slot, fixed_uid_access.lease.slot);
    _ = try manager.endUse(fixed_runtime_access.lease, .complete);
    _ = try manager.endUse(fixed_uid_access.lease, .complete);

    // Make the fixed resource offline and Fatal. It still cannot block close.
    try manager.close(new_fixed_uid, work);
    _ = try manager.updateUid(new_shell_uid, .{ .attributes = .{ .access_loss_impact = .fatal } });
    try std.testing.expect(manager.rows[23].attributes.access_loss_impact == .fatal);
    try manager.close(new_shell_uid, work);
    try std.testing.expect((try manager.index.find(new_shell_uid)) == null);
    try std.testing.expect((try manager.index.find(new_fixed_uid)) == null);
    try std.testing.expectEqual(types.no_slot, manager.bindings[2].slot.load(.acquire));
}

test "partition owner replacement failures are silent and candidates rank by subtree OR then shell numbers" {
    const F = PartitionFixture();
    const Uid = F.UidType;
    var fixture = F{};
    try fixture.initialize();
    defer fixture.cleanup();
    const manager = &fixture.ledger;
    const work = &fixture.work;

    const root_uid = key(Uid, 100);
    const parent_uid = key(Uid, 101);
    _ = try manager.pushPartition(.{ .table = 0 }, root_uid, 24, attrs, work);
    _ = try manager.pushPartition(.{ .partition = root_uid }, parent_uid, 24, attrs, work);
    const parent_slot = try partitionSlot(manager, parent_uid);
    const child_uids = [_]Uid{ key(Uid, 110), key(Uid, 111), key(Uid, 112), key(Uid, 113) };
    var child_numbers: [4]u64 = undefined;
    var child_starts: [4]u32 = undefined;
    var child_slots: [4]u32 = undefined;
    var child_next: [4]u32 = undefined;
    for (child_uids, 0..) |child_uid, i| {
        const result = try manager.pushPartition(.{ .partition = parent_uid }, child_uid, 6, attrs, work);
        child_numbers[i] = result.allocation_number;
        const slot = try partitionSlot(manager, child_uid);
        child_slots[i] = slot;
        child_next[i] = manager.shells[slot].next_sibling;
        child_starts[i] = manager.shells[slot].start;
        try std.testing.expectEqual(@as(u32, @intCast(i * 6)), child_starts[i]);
    }
    const subtree_uid = key(Uid, 119);
    const subtree = try manager.pushPartition(.{ .partition = child_uids[0] }, subtree_uid, 1, attrs, work);
    const subtree_slot = try partitionSlot(manager, subtree_uid);
    try std.testing.expectEqual(child_starts[0], manager.shells[subtree_slot].start);

    const epoch = manager.epoch.load(.acquire);
    const or_bits = [_]F.Owner.PartitionPlanning.BitmapType{ 1, 1, 2, 2 };
    for (child_uids, 0..) |child_uid, i| {
        const slot = try partitionSlot(manager, child_uid);
        manager.shells[slot].history = .{ .bits = or_bits[i], .last_activity_epoch = epoch };
    }
    manager.shells[subtree_slot].history = .{ .bits = 2, .last_activity_epoch = epoch };

    var notifications = F.Capture.State{};
    const subscription = try manager.subscribe(0, F.Capture.callback, &notifications);
    defer manager.unsubscribe(subscription) catch {};

    // Overflow in one affected index home fails before tree or row mutation,
    // even though shell notifications have already been staged in workspace.
    const failed_version_uid = key(Uid, 900);
    const first_child_before_failure = manager.shells[parent_slot].first_child;
    const registered_before_failure = manager.tables[0].registered_bytes;
    const failed_home = try manager.index.homeFor(failed_version_uid);
    const original_version = manager.index.homes[failed_home].version.load(.seq_cst);
    manager.index.homes[failed_home].version.store(std.math.maxInt(u64) - 1, .seq_cst);
    const version_attempt = manager.pushPartition(.{ .partition = parent_uid }, failed_version_uid, 9, attrs, work);
    manager.index.homes[failed_home].version.store(original_version, .seq_cst);
    try std.testing.expectError(error.VersionExhausted, version_attempt);
    try std.testing.expectEqual(@as(usize, 0), notifications.count);
    try std.testing.expectEqual(@as(usize, 0), work.note_count);
    try std.testing.expectEqual(first_child_before_failure, manager.shells[parent_slot].first_child);
    try std.testing.expectEqual(registered_before_failure, manager.tables[0].registered_bytes);
    try std.testing.expect((try manager.index.find(failed_version_uid)) == null);
    for (child_uids, 0..) |child_uid, i| {
        const slot = try partitionSlot(manager, child_uid);
        try std.testing.expectEqual(child_slots[i], slot);
        try std.testing.expectEqual(child_starts[i], manager.shells[slot].start);
        try std.testing.expectEqual(@as(u32, 6), manager.shells[slot].capacity);
        try std.testing.expectEqual(child_numbers[i], manager.shells[slot].allocation_number);
        try std.testing.expectEqual(child_next[i], manager.shells[slot].next_sibling);
        try std.testing.expectEqual(parent_slot, manager.shells[slot].parent.?);
    }

    // A short insertion slice fails after note staging, but the notes are
    // discarded and every old shell remains indexed at the same interval.
    const insertion_workspace = work.index_workspace.inserts;
    work.index_workspace.inserts = &.{};
    const workspace_attempt = manager.pushPartition(.{ .partition = parent_uid }, key(Uid, 901), 9, attrs, work);
    work.index_workspace.inserts = insertion_workspace;
    try std.testing.expectError(error.WorkspaceTooSmall, workspace_attempt);
    try std.testing.expectEqual(@as(usize, 0), notifications.count);
    try std.testing.expectEqual(@as(usize, 0), work.note_count);
    try std.testing.expectEqual(first_child_before_failure, manager.shells[parent_slot].first_child);
    try std.testing.expectEqual(registered_before_failure, manager.tables[0].registered_bytes);
    try std.testing.expect((try manager.index.find(key(Uid, 901))) == null);
    for (child_uids, 0..) |child_uid, i| {
        const slot = try partitionSlot(manager, child_uid);
        try std.testing.expectEqual(child_slots[i], slot);
        try std.testing.expectEqual(child_starts[i], manager.shells[slot].start);
        try std.testing.expectEqual(child_numbers[i], manager.shells[slot].allocation_number);
        try std.testing.expectEqual(child_next[i], manager.shells[slot].next_sibling);
        try std.testing.expectEqual(parent_slot, manager.shells[slot].parent.?);
    }

    // The 2-shell windows have equal score/latest-access. Their ORed aligned
    // histories are 1, 3, and 2, so the rightmost pair wins on the low bit.
    const or_winner_uid = key(Uid, 120);
    const or_winner = try manager.pushPartition(.{ .partition = parent_uid }, or_winner_uid, 9, attrs, work);
    try std.testing.expect(or_winner.created);
    const or_winner_slot = try partitionSlot(manager, or_winner_uid);
    try std.testing.expectEqual(@as(u32, 12), manager.shells[or_winner_slot].start);
    try std.testing.expectEqual(@as(u32, 9), manager.shells[or_winner_slot].capacity);
    try std.testing.expect((try manager.index.find(child_uids[0])) != null);
    try std.testing.expect((try manager.index.find(child_uids[1])) != null);
    try std.testing.expect((try manager.index.find(child_uids[2])) == null);
    try std.testing.expect((try manager.index.find(child_uids[3])) == null);
    try std.testing.expectEqual(@as(usize, 2), notifications.count);
    try std.testing.expect(notifications.saw(child_uids[2], child_numbers[2]));
    try std.testing.expect(notifications.saw(child_uids[3], child_numbers[3]));

    try manager.close(or_winner_uid, work);
    notifications.clear();
    const later_uids = [_]Uid{ key(Uid, 114), key(Uid, 115) };
    var later_numbers: [2]u64 = undefined;
    for (later_uids, 0..) |child_uid, i| {
        const result = try manager.pushPartition(.{ .partition = parent_uid }, child_uid, 6, attrs, work);
        later_numbers[i] = result.allocation_number;
        const slot = try partitionSlot(manager, child_uid);
        try std.testing.expectEqual(@as(u32, 12 + @as(u32, @intCast(i)) * 6), manager.shells[slot].start);
    }

    // With equal score and OR history, the earlier latest-access epoch wins.
    manager.epoch.store(4, .release);
    for (child_uids[0..2]) |child_uid| {
        const slot = try partitionSlot(manager, child_uid);
        manager.shells[slot].history = .{ .bits = 0, .last_activity_epoch = 2 };
    }
    manager.shells[subtree_slot].history = .{ .bits = 0, .last_activity_epoch = 2 };
    for (later_uids) |child_uid| {
        const slot = try partitionSlot(manager, child_uid);
        manager.shells[slot].history = .{ .bits = 0, .last_activity_epoch = 4 };
    }
    const latest_winner_uid = key(Uid, 122);
    const latest_winner = try manager.pushPartition(.{ .partition = parent_uid }, latest_winner_uid, 9, attrs, work);
    try std.testing.expect(latest_winner.created);
    const latest_winner_slot = try partitionSlot(manager, latest_winner_uid);
    try std.testing.expectEqual(@as(u32, 0), manager.shells[latest_winner_slot].start);
    try std.testing.expect((try manager.index.find(child_uids[0])) == null);
    try std.testing.expect((try manager.index.find(child_uids[1])) == null);
    try std.testing.expect((try manager.index.find(later_uids[0])) != null);
    try std.testing.expect((try manager.index.find(later_uids[1])) != null);
    try std.testing.expectEqual(@as(usize, 3), notifications.count);
    try std.testing.expect(notifications.saw(child_uids[0], child_numbers[0]));
    try std.testing.expect(notifications.saw(child_uids[1], child_numbers[1]));
    try std.testing.expect(notifications.saw(subtree_uid, subtree.allocation_number));
    try std.testing.expect((try manager.index.find(subtree_uid)) == null);

    try manager.close(latest_winner_uid, work);
    notifications.clear();
    const newest_uids = [_]Uid{ key(Uid, 116), key(Uid, 117) };
    for (newest_uids, 0..) |child_uid, i| {
        _ = try manager.pushPartition(.{ .partition = parent_uid }, child_uid, 6, attrs, work);
        const slot = try partitionSlot(manager, child_uid);
        try std.testing.expectEqual(@as(u32, @intCast(i * 6)), manager.shells[slot].start);
    }

    // Equal score, bitmap, and last-access summaries now make the allocation-
    // number lists decide. The older later-child pair wins at start 12.
    const all_equal = [_]Uid{ newest_uids[0], newest_uids[1], later_uids[0], later_uids[1] };
    for (all_equal) |child_uid| {
        const slot = try partitionSlot(manager, child_uid);
        manager.shells[slot].history = .{ .bits = 0, .last_activity_epoch = 4 };
    }
    const number_winner_uid = key(Uid, 121);
    const number_winner = try manager.pushPartition(.{ .partition = parent_uid }, number_winner_uid, 9, attrs, work);
    try std.testing.expect(number_winner.created);
    const number_winner_slot = try partitionSlot(manager, number_winner_uid);
    try std.testing.expectEqual(@as(u32, 12), manager.shells[number_winner_slot].start);
    try std.testing.expectEqual(@as(u32, 9), manager.shells[number_winner_slot].capacity);
    try std.testing.expect((try manager.index.find(newest_uids[0])) != null);
    try std.testing.expect((try manager.index.find(newest_uids[1])) != null);
    try std.testing.expect((try manager.index.find(later_uids[0])) == null);
    try std.testing.expect((try manager.index.find(later_uids[1])) == null);
    try std.testing.expectEqual(@as(usize, 2), notifications.count);
    try std.testing.expect(notifications.saw(later_uids[0], later_numbers[0]));
    try std.testing.expect(notifications.saw(later_uids[1], later_numbers[1]));

    // A resident lease blocks a normal whole-tree close without partial state
    // or notification changes. Fatal metadata keeps blocking until force.
    notifications.clear();
    const dynamic_uid = key(Uid, 130);
    try std.testing.expectError(
        error.InvalidArgument,
        manager.push(.{ .partition = number_winner_uid }, dynamic_uid, null, work),
    );
    const dynamic = (try manager.push(.{ .partition = number_winner_uid }, dynamic_uid, 0, work)).newly_allocated;
    _ = try manager.endUse(dynamic.lease, .complete);
    const active = (try manager.use(dynamic_uid)).in_memory;
    const root_slot_before_close = try partitionSlot(manager, root_uid);
    const parent_slot_before_close = try partitionSlot(manager, parent_uid);
    const winner_slot_before_close = try partitionSlot(manager, number_winner_uid);
    const newest_a_slot_before_close = try partitionSlot(manager, newest_uids[0]);
    const newest_b_slot_before_close = try partitionSlot(manager, newest_uids[1]);
    const dynamic_generation_before_close = manager.rows[active.lease.slot].resource.generation;
    const before_in_use_registered = manager.tables[0].registered_bytes;
    try std.testing.expectError(error.InUse, manager.close(root_uid, work));
    try std.testing.expectEqual(before_in_use_registered, manager.tables[0].registered_bytes);
    try std.testing.expectEqual(@as(usize, 0), notifications.count);
    try std.testing.expectEqual(root_slot_before_close, try partitionSlot(manager, root_uid));
    try std.testing.expectEqual(parent_slot_before_close, try partitionSlot(manager, parent_uid));
    try std.testing.expectEqual(winner_slot_before_close, try partitionSlot(manager, number_winner_uid));
    try std.testing.expectEqual(newest_a_slot_before_close, try partitionSlot(manager, newest_uids[0]));
    try std.testing.expectEqual(newest_b_slot_before_close, try partitionSlot(manager, newest_uids[1]));
    try std.testing.expect((try manager.index.find(dynamic_uid)) != null);
    try std.testing.expectEqual(dynamic_generation_before_close, manager.rows[active.lease.slot].resource.generation);
    try std.testing.expectEqual(@as(u64, 1), manager.rows[active.lease.slot].resource.use_count);
    _ = try manager.endUse(active.lease, .complete);

    _ = try manager.updateUid(number_winner_uid, .{ .attributes = .{ .access_loss_impact = .fatal } });
    const before_blocked_registered = manager.tables[0].registered_bytes;
    try std.testing.expectError(error.Blocked, manager.close(root_uid, work));
    try std.testing.expectEqual(before_blocked_registered, manager.tables[0].registered_bytes);
    try std.testing.expectEqual(@as(usize, 0), notifications.count);
    try std.testing.expect((try manager.index.find(root_uid)) != null);
    try std.testing.expect((try manager.index.find(parent_uid)) != null);
    try std.testing.expect((try manager.index.find(number_winner_uid)) != null);
    try std.testing.expect((try manager.index.find(dynamic_uid)) != null);
    try std.testing.expectEqual(winner_slot_before_close, try partitionSlot(manager, number_winner_uid));
    try std.testing.expectEqual(dynamic_generation_before_close, manager.rows[active.lease.slot].resource.generation);
    try std.testing.expectEqual(@as(u64, 0), manager.rows[active.lease.slot].resource.use_count);

    const late_lease = (try manager.use(dynamic_uid)).in_memory.lease;
    try manager.forceRelease(root_uid, work);
    try std.testing.expectEqual(@import("../memory_resource.zig").EndResult.force_released, try manager.endUse(late_lease, .complete));
    try std.testing.expect((try manager.index.find(root_uid)) == null);
    try std.testing.expect((try manager.index.find(parent_uid)) == null);
    try std.testing.expect((try manager.index.find(number_winner_uid)) == null);
    try std.testing.expect((try manager.index.find(dynamic_uid)) == null);
    try std.testing.expect(notifications.saw(dynamic_uid, dynamic.allocation_number));
}
