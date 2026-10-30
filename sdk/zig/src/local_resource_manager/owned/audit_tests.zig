const std = @import("std");
const types = @import("types.zig");
const uid = @import("../uid.zig");
const index = @import("index.zig");
const ledger_owner = @import("owner.zig");
const notifications = @import("notifications.zig");
const audit = @import("audit.zig");

const attrs = types.Attributes{
    .recoverability = .recoverable,
    .recovery_cost_coefficient = 1,
    .access_loss_impact = .unobservable_now,
};
const policy = types.Policy{
    .score_epochs = 1,
    .idle_epochs = 0,
    .protection = .concentrated,
    .concentrated_trigger_percent = 10,
    .concentrated_target_percent = 20,
    .smooth_trigger_percent = 15,
    .smooth_emergency_percent = 5,
    .smooth_interval_epochs = 1,
    .smooth_release_count = 1,
    .tick_maximum_actions = 1,
    .release_check = .{ .enabled = false, .tolerance_parts_per_million = 0, .tolerance_bytes = 0 },
};

fn key(comptime Uid: type, value: u64) Uid {
    var result = Uid{ .bytes = @splat(0) };
    std.mem.writeInt(u64, result.bytes[result.bytes.len - 8 ..][0..8], value, .big);
    return result;
}

fn Fixture() type {
    const Uid = uid.Identifier(128);
    const Index = index.Candidate(Uid, .c2_chain);
    const parent_uid = key(Uid, 100);
    const tables = [_]types.Table{.{
        .capacity = 8,
        .partition_capacity = 4,
        .reserved_start = 0,
        .reserved_count = 4,
        .attributes = attrs,
        .policy = policy,
    }};
    const fixed = [_]types.For(Uid).Fixed{
        .{ .table = 0, .parent = null, .ordinal = 6, .uid = key(Uid, 1), .allocation_bytes = 31, .attributes = null },
        .{ .table = 0, .parent = parent_uid, .ordinal = 2, .uid = key(Uid, 2), .allocation_bytes = 17, .attributes = null },
    };
    const Owner = ledger_owner.Owner(.{ .uid_width = 128, .activity_width = 8, .index_buckets = 16 }, &tables, &fixed, Index);
    const Audit = audit.For(Owner);

    return struct {
        pub const Ledger = Owner;
        pub const UidType = Uid;
        pub const AuditType = Audit;

        rows: [8]Owner.Row = undefined,
        shells: [4]Owner.Shell = undefined,
        tables: [1]Owner.TableState = undefined,
        bindings: [2]Owner.FixedBinding = undefined,
        nodes: [12]Index.Node = undefined,
        homes: [16]Index.Home = undefined,
        records: [0]notifications.Notifier(Uid).Record = .{},
        queue: [4]u32 = undefined,
        marks: [4]u8 = undefined,
        depths: [4]u32 = undefined,
        events: [8]Audit.Event = undefined,
        stack: [4]u32 = undefined,
        table_views: [1]types.TableView = undefined,
        fixed_marks: [2]u8 = undefined,
        allocations: [12]u64 = undefined,
        ledger: Owner = undefined,
        workspace: Audit.Workspace = undefined,

        pub fn initialize(self: *@This()) !void {
            var subscriptions = [_][]notifications.Notifier(Uid).Record{&self.records};
            try Owner.init(
                &self.ledger,
                &self.rows,
                &self.shells,
                &self.tables,
                &self.bindings,
                &self.nodes,
                &self.homes,
                &subscriptions,
            );

            self.workspace = .{
                .shell_queue = &self.queue,
                .shell_marks = &self.marks,
                .depth = &self.depths,
                .events = &self.events,
                .stack = &self.stack,
                .table_views = &self.table_views,
                .fixed_marks = &self.fixed_marks,
                .allocations = &self.allocations,
            };

            const shell_parent_uid = key(Uid, 100);
            const nested_uid = key(Uid, 101);
            self.shells[0] = .{
                .uid = shell_parent_uid,
                .table = 0,
                .start = 0,
                .capacity = 4,
                .allocation_number = 1,
                .attributes = attrs,
                .history = .{ .bits = 1, .last_activity_epoch = 1 },
                .first_child = 1,
                .next_sibling = types.no_slot,
            };
            self.shells[1] = .{
                .uid = nested_uid,
                .table = 0,
                .parent = 0,
                .start = 0,
                .capacity = 1,
                .allocation_number = 2,
                .attributes = attrs,
                .history = .{ .bits = 1, .last_activity_epoch = 1 },
                .first_child = types.no_slot,
                .next_sibling = types.no_slot,
            };
            self.tables[0].first_partition = 0;
            self.tables[0].free_partition = 2;
            for (0..4) |slot| self.rows[slot].parent = 0;
            self.rows[0].parent = 1;

            var fixed_resource = self.rows[2].resource;
            fixed_resource = try @TypeOf(fixed_resource).fixed(17);
            fixed_resource.generation = 3;
            fixed_resource.next_use_generation = 2;
            fixed_resource.activity_history = .{ .bits = 1, .last_activity_epoch = 1 };
            self.rows[2].uid = key(Uid, 2);
            self.rows[2].fixed_declaration = 1;
            self.rows[2].fixed_parent_number = 1;
            self.rows[2].attributes = attrs;
            self.rows[2].resource = fixed_resource;
            self.tables[0].registered_bytes += 17;
            self.bindings[1].parent_number.store(1, .release);
            self.bindings[1].slot.store(2, .release);

            try self.ledger.index.insert(2, key(Uid, 2), .{ .kind = .resource, .table = 0, .slot = 2 });
            try self.ledger.index.insert(8, shell_parent_uid, .{ .kind = .partition, .table = 0, .slot = 0 });
            try self.ledger.index.insert(9, nested_uid, .{ .kind = .partition, .table = 0, .slot = 1 });
            self.ledger.numbers.next.store(4, .release);
        }

        pub fn cleanup(self: *@This()) void {
            for (&self.rows) |*row| row.resource.forceRelease() catch @panic("audit fixture cleanup failed");
        }
    };
}

test "full owner audit proves nested ranges fixed bindings bytes and index mapping" {
    var fixture = Fixture(){};
    try fixture.initialize();
    defer fixture.cleanup();

    const stats = try Fixture().AuditType.run(&fixture.ledger, &fixture.workspace);
    try std.testing.expectEqual(@as(usize, 8), stats.resources);
    try std.testing.expectEqual(@as(usize, 4), stats.partitions);
    try std.testing.expectEqual(@as(usize, 2), stats.active_resources);
    try std.testing.expectEqual(@as(usize, 2), stats.active_partitions);
    try std.testing.expectEqual(@as(usize, 2), stats.free_partitions);
    try std.testing.expectEqual(@as(usize, 2), stats.fixed_rows);
    try std.testing.expectEqual(@as(usize, 1), stats.fixed_identity_lookups);
    try std.testing.expectEqual(@as(usize, 4), stats.interval_events);
    try std.testing.expectEqual(@as(usize, 3), stats.allocation_numbers);
    try std.testing.expectEqual(@as(u128, 48), stats.registered_bytes);
    try std.testing.expectEqual(@as(u128, 0), stats.system_page_bytes);
    try std.testing.expectEqual(@as(usize, 16), stats.index_home_body);
    try std.testing.expectEqual(@as(usize, 12), stats.index_node_body);
}

test "full owner audit rejects wrong row containment and stale table accounting" {
    var fixture = Fixture(){};
    try fixture.initialize();
    defer fixture.cleanup();

    fixture.rows[0].parent = 0;
    try std.testing.expectError(error.InvalidState, Fixture().AuditType.run(&fixture.ledger, &fixture.workspace));
    fixture.rows[0].parent = 1;

    fixture.tables[0].registered_bytes += 1;
    try std.testing.expectError(error.InvalidState, Fixture().AuditType.run(&fixture.ledger, &fixture.workspace));
}

test "full owner audit accepts an updated fixed registration without treating its declaration as current size" {
    var fixture = Fixture(){};
    try fixture.initialize();
    defer fixture.cleanup();
    _ = try fixture.ledger.updateUid(key(Fixture().UidType, 1), .{ .allocation_bytes = 19 });
    _ = try fixture.ledger.updateUid(key(Fixture().UidType, 2), .{ .allocation_bytes = 23 });
    const stats = try fixture.ledger.audit(&fixture.workspace);
    try std.testing.expectEqual(@as(u128, 42), stats.registered_bytes);
    try std.testing.expectEqual(@as(u128, 0), stats.system_page_bytes);
}

test "full owner audit rejects duplicate owner allocation numbers" {
    var fixture = Fixture(){};
    try fixture.initialize();
    defer fixture.cleanup();

    fixture.rows[2].resource.generation = 1;
    try std.testing.expectError(error.InvalidState, Fixture().AuditType.run(&fixture.ledger, &fixture.workspace));
}

test "undersized audit workspace is rejected before owner state changes" {
    var fixture = Fixture(){};
    try fixture.initialize();
    defer fixture.cleanup();

    fixture.workspace.events = fixture.events[0..7];
    try std.testing.expectError(error.WorkspaceTooSmall, Fixture().AuditType.run(&fixture.ledger, &fixture.workspace));
    try std.testing.expect(fixture.shells[0].uid != null);
    try std.testing.expectEqual(@as(u64, 48), fixture.tables[0].registered_bytes);
}
