//! Original-maximum complete-owner fixture for the explicit C2 and B2 candidates.
//!
//! Most of this fixture constructs stable metadata directly. Calling Owner.push
//! once per row would repeat the first-empty scan and measure a different
//! workload. One final zero-byte Owner.push remains in the trace; it fills the
//! last row and proves that this counted resident allocation joins the full
//! owner state correctly.
const std = @import("std");
const values = @import("ledger_values").Bound;
const uid_module = @import("../uid.zig");
const memory_resource = @import("../memory_resource.zig");
const c2_index = @import("index.zig");
const b2_index = @import("index_b2.zig");
const notifications = @import("notifications.zig");
const owner_module = @import("owner.zig");
const types = @import("types.zig");

pub const CandidateFlavor = enum { c2, b2 };
pub const memory_region = @import("../memory_region.zig");

const table_count = 64;
const rows_per_table = 8_192;
const shells_per_table = 1_024;
const rows_per_table_total = table_count * rows_per_table;
const shells_total = table_count * shells_per_table;
const fixed_per_table = 2;
const fixed_total = table_count * fixed_per_table;
const reserve_count = rows_per_table - 1;
const leaf_depth = shells_per_table - 1;
const leaf_capacity = reserve_count - leaf_depth;
const nested_fixed_ordinal = leaf_capacity - 1;
const index_buckets = 1_048_576;
const shell_uid_base = 1_000_000;
const dynamic_uid_base = 2_000_000;
const direct_fixed_uid_base = 10_000;
const nested_fixed_uid_base = 20_000;
const api_dynamic_uid = 4_000_000;

const attrs = types.Attributes{
    .recoverability = .recoverable,
    .recovery_cost_coefficient = 1,
    .access_loss_impact = .unobservable_now,
};

pub fn key(comptime Uid: type, value: anytype) Uid {
    var result = Uid{ .bytes = @splat(0) };
    std.mem.writeInt(u64, result.bytes[result.bytes.len - 8 ..][0..8], @intCast(value), .big);
    return result;
}

fn policy(comptime table: usize) types.Policy {
    const prefix = std.fmt.comptimePrint("table_{d}_", .{table});
    return .{
        .score_epochs = @intCast(@field(values, prefix ++ "activity_score_epochs")),
        .idle_epochs = @intCast(@field(values, prefix ++ "idle_epochs")),
        .protection = switch (@field(values, prefix ++ "capacity_protection_method")) {
            1 => .concentrated,
            2 => .smooth,
            else => @compileError("selected SDK38 capacity protection method is unsupported"),
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

fn makeTables() [table_count]types.Table {
    var result: [table_count]types.Table = undefined;
    for (&result) |*declaration| {
        declaration.* = .{
            .capacity = rows_per_table,
            .partition_capacity = shells_per_table,
            .reserved_start = 0,
            .reserved_count = reserve_count,
            .attributes = attrs,
            .policy = policy(0),
        };
    }
    return result;
}

pub fn makeGeneratedTables() [table_count]types.Table {
    @setEvalBranchQuota(1_000_000);
    var result: [table_count]types.Table = undefined;
    inline for (&result, 0..) |*declaration, table| {
        declaration.* = .{
            .capacity = rows_per_table,
            .partition_capacity = shells_per_table,
            .reserved_start = 0,
            .reserved_count = reserve_count,
            .attributes = attrs,
            .policy = policy(table),
        };
    }
    return result;
}

fn makeFixed(comptime Uid: type) [fixed_total]types.For(Uid).Fixed {
    var result: [fixed_total]types.For(Uid).Fixed = undefined;
    for (0..table_count) |table| {
        const shell_start = table * shells_per_table;
        result[table * fixed_per_table] = .{
            .table = @intCast(table),
            .parent = null,
            .ordinal = rows_per_table - 1,
            .uid = key(Uid, direct_fixed_uid_base + table),
            .allocation_bytes = 13,
            .attributes = null,
        };
        result[table * fixed_per_table + 1] = .{
            .table = @intCast(table),
            .parent = key(Uid, shell_uid_base + shell_start + leaf_depth),
            .ordinal = nested_fixed_ordinal,
            .uid = key(Uid, nested_fixed_uid_base + table),
            .allocation_bytes = 17,
            .attributes = null,
        };
    }
    return result;
}

pub fn Fixture(comptime bits: u16, comptime flavor: CandidateFlavor) type {
    return FixtureWithTables(bits, flavor, makeTables());
}

pub fn FixtureWithTables(
    comptime bits: u16,
    comptime flavor: CandidateFlavor,
    comptime declarations: [table_count]types.Table,
) type {
    const Uid = uid_module.Identifier(bits);
    const Index = if (flavor == .c2)
        c2_index.Candidate(Uid, .c2_chain)
    else
        b2_index.Candidate(Uid, .b2_ordered);
    const fixed = makeFixed(Uid);
    const Ledger = owner_module.Owner(
        .{
            .uid_width = bits,
            .activity_width = @intCast(values.activity_bitmap_width),
            .index_buckets = index_buckets,
        },
        &declarations,
        &fixed,
        Index,
    );
    const Notifier = notifications.Notifier(Uid);
    const Resource = memory_resource.Configured(.{
        .width = @intCast(values.activity_bitmap_width),
        .score_epochs = @intCast(values.activity_bitmap_width),
        .idle_epochs = @intCast(values.activity_bitmap_width),
    });

    return struct {
        const Self = @This();

        pub const Owner = Ledger;
        pub const UidType = Uid;
        pub const IndexType = Index;

        rows: []Ledger.Row,
        shells: []Ledger.Shell,
        table_states: []Ledger.TableState,
        bindings: []Ledger.FixedBinding,
        nodes: []Index.Node,
        homes: []Index.Home,
        audit_shell_queue: []u32,
        audit_shell_marks: []u8,
        audit_depth: []u32,
        audit_events: []Ledger.Audit.Event,
        audit_stack: []u32,
        audit_table_views: []types.TableView,
        audit_fixed_marks: []u8,
        audit_allocations: []u64,
        subscriber_records: []Notifier.Record,
        ledger: Ledger,
        owner_work: Ledger.Workspace = .{
            .candidates = &.{},
            .rows = &.{},
            .shells = &.{},
            .homes = &.{},
            .notes = &.{},
        },
        zero_byte_api_slot: ?u32 = null,

        pub const Footprint = struct {
            row_metadata: usize = Ledger.resource_capacity * @sizeOf(Ledger.Row),
            shell_metadata: usize = Ledger.partition_capacity * @sizeOf(Ledger.Shell),
            table_metadata: usize = Ledger.table_count * @sizeOf(Ledger.TableState),
            fixed_bindings: usize = Ledger.fixed_declarations.len * @sizeOf(Ledger.FixedBinding),
            index_nodes: usize = (Ledger.resource_capacity + Ledger.partition_capacity) * @sizeOf(Index.Node),
            index_homes: usize = index_buckets * @sizeOf(Index.Home),
            audit_scratch: usize =
                Ledger.partition_capacity * (@sizeOf(u32) * 3 + @sizeOf(u8)) +
                Ledger.partition_capacity * 2 * @sizeOf(Ledger.Audit.Event) +
                Ledger.table_count * @sizeOf(types.TableView) +
                Ledger.fixed_declarations.len * @sizeOf(u8) +
                (Ledger.resource_capacity + Ledger.partition_capacity) * @sizeOf(u64),
            pub fn ownerMetadata(self: @This()) usize {
                return self.row_metadata + self.shell_metadata + self.table_metadata + self.fixed_bindings;
            }
            pub fn total(self: @This()) usize {
                return self.ownerMetadata() + self.index_nodes + self.index_homes + self.audit_scratch;
            }
        };

        pub fn create(allocator: std.mem.Allocator) !Self {
            const rows = try allocator.alloc(Ledger.Row, Ledger.resource_capacity);
            errdefer allocator.free(rows);
            const shells = try allocator.alloc(Ledger.Shell, Ledger.partition_capacity);
            errdefer allocator.free(shells);
            const table_states = try allocator.alloc(Ledger.TableState, Ledger.table_count);
            errdefer allocator.free(table_states);
            const bindings = try allocator.alloc(Ledger.FixedBinding, Ledger.fixed_declarations.len);
            errdefer allocator.free(bindings);
            const nodes = try allocator.alloc(Index.Node, Ledger.resource_capacity + Ledger.partition_capacity);
            errdefer allocator.free(nodes);
            const homes = try allocator.alloc(Index.Home, index_buckets);
            errdefer allocator.free(homes);
            const subscriber_records = try allocator.alloc(Notifier.Record, table_count);
            errdefer allocator.free(subscriber_records);

            const audit_shell_queue = try allocator.alloc(u32, Ledger.partition_capacity);
            errdefer allocator.free(audit_shell_queue);
            const audit_shell_marks = try allocator.alloc(u8, Ledger.partition_capacity);
            errdefer allocator.free(audit_shell_marks);
            const audit_depth = try allocator.alloc(u32, Ledger.partition_capacity);
            errdefer allocator.free(audit_depth);
            const audit_events = try allocator.alloc(Ledger.Audit.Event, Ledger.partition_capacity * 2);
            errdefer allocator.free(audit_events);
            const audit_stack = try allocator.alloc(u32, Ledger.partition_capacity);
            errdefer allocator.free(audit_stack);
            const audit_table_views = try allocator.alloc(types.TableView, Ledger.table_count);
            errdefer allocator.free(audit_table_views);
            const audit_fixed_marks = try allocator.alloc(u8, Ledger.fixed_declarations.len);
            errdefer allocator.free(audit_fixed_marks);
            const audit_allocations = try allocator.alloc(u64, Ledger.resource_capacity + Ledger.partition_capacity);
            errdefer allocator.free(audit_allocations);

            var subscribers: [table_count][]Notifier.Record = undefined;
            for (&subscribers, 0..) |*records, table| records.* = subscriber_records[table .. table + 1];
            const ledger = try Ledger.init(rows, shells, table_states, bindings, nodes, homes, &subscribers);
            return .{
                .rows = rows,
                .shells = shells,
                .table_states = table_states,
                .bindings = bindings,
                .nodes = nodes,
                .homes = homes,
                .audit_shell_queue = audit_shell_queue,
                .audit_shell_marks = audit_shell_marks,
                .audit_depth = audit_depth,
                .audit_events = audit_events,
                .audit_stack = audit_stack,
                .audit_table_views = audit_table_views,
                .audit_fixed_marks = audit_fixed_marks,
                .audit_allocations = audit_allocations,
                .subscriber_records = subscriber_records,
                .ledger = ledger,
            };
        }

        pub fn deinit(self: *Self, allocator: std.mem.Allocator) void {
            if (self.zero_byte_api_slot) |slot| {
                if (self.rows[slot].resource.registration != null)
                    self.rows[slot].resource.forceRelease() catch @panic("zero-byte owner fixture cleanup failed");
            }
            allocator.free(self.subscriber_records);
            allocator.free(self.audit_allocations);
            allocator.free(self.audit_fixed_marks);
            allocator.free(self.audit_table_views);
            allocator.free(self.audit_stack);
            allocator.free(self.audit_events);
            allocator.free(self.audit_depth);
            allocator.free(self.audit_shell_marks);
            allocator.free(self.audit_shell_queue);
            allocator.free(self.homes);
            allocator.free(self.nodes);
            allocator.free(self.bindings);
            allocator.free(self.table_states);
            allocator.free(self.shells);
            allocator.free(self.rows);
        }

        /// Populate all 524,288 resource rows and 65,536 shell records without
        /// the quadratic repeated first-empty scan of Owner.push. Index entries
        /// are still published through the selected candidate's real insert.
        pub fn populateMaximum(self: *Self) !void {
            const dynamic_rows_before_api_push = Ledger.resource_capacity - fixed_total - 1;
            var next_number: u64 = 1;

            for (0..table_count) |table| {
                const resource_start = Ledger.resourceStart(table);
                const shell_start = Ledger.partitionStart(table);
                self.table_states[table].first_partition = @intCast(shell_start);
                self.table_states[table].free_partition = types.no_slot;

                for (0..rows_per_table) |offset| {
                    const slot = resource_start + offset;
                    if (offset == rows_per_table - 1) {
                        self.rows[slot].parent = null;
                    } else {
                        const depth = @min(leaf_depth, reserve_count - 1 - offset);
                        self.rows[slot].parent = @intCast(shell_start + depth);
                    }
                }

                for (0..shells_per_table) |depth| {
                    const slot = shell_start + depth;
                    self.shells[slot] = .{
                        .uid = key(Uid, shell_uid_base + slot),
                        .table = @intCast(table),
                        .parent = if (depth == 0) null else @intCast(slot - 1),
                        .start = @intCast(resource_start),
                        .capacity = @intCast(reserve_count - depth),
                        .allocation_number = @intCast(dynamic_rows_before_api_push + 1 + slot),
                        .attributes = attrs,
                        .history = .{},
                        .first_child = if (depth + 1 == shells_per_table) types.no_slot else @intCast(slot + 1),
                        .next_sibling = types.no_slot,
                    };
                }

                for (0..shells_per_table) |depth| {
                    const slot = shell_start + depth;
                    try self.ledger.index.insert(
                        @intCast(Ledger.resource_capacity + slot),
                        self.shells[slot].uid.?,
                        .{ .kind = .partition, .table = @intCast(table), .slot = @intCast(slot) },
                    );
                }
            }

            const api_slot = @as(usize, Ledger.resourceStart(0));
            for (0..Ledger.resource_capacity) |slot| {
                if (self.rows[slot].fixed_declaration != null or isNestedFixedSlot(slot) or slot == api_slot) continue;

                var resource = try Resource.dynamic(0);
                resource.generation = next_number;
                resource.registration.?.region = .{ .base = null, .allocation_bytes = 0, .page_bytes = 0 };
                self.rows[slot].uid = key(Uid, dynamic_uid_base + slot);
                self.rows[slot].resource = resource;
                try self.ledger.index.insert(
                    @intCast(slot),
                    self.rows[slot].uid.?,
                    .{ .kind = .resource, .table = self.rows[slot].table, .slot = @intCast(slot) },
                );
                next_number += 1;
            }
            if (next_number != dynamic_rows_before_api_push + 1) return error.InvalidFixtureCount;

            for (0..table_count) |table| {
                const resource_slot = Ledger.resourceStart(table) + nested_fixed_ordinal;
                const parent_slot = Ledger.partitionStart(table) + leaf_depth;
                try self.ledger.registerFixed(
                    @intCast(table * fixed_per_table + 1),
                    @intCast(resource_slot),
                    @intCast(parent_slot),
                );
            }

            self.ledger.numbers.next.store(dynamic_rows_before_api_push + 1 + shells_total, .release);

            const leaf_slot = Ledger.partitionStart(0) + leaf_depth;
            const leaf_uid = key(Uid, shell_uid_base + leaf_slot);
            const fresh = switch (try self.ledger.push(
                .{ .partition = leaf_uid },
                key(Uid, api_dynamic_uid),
                0,
                &self.owner_work,
            )) {
                .newly_allocated => |borrow| borrow,
                .in_memory => return error.ExpectedFreshZeroByteResource,
                .loading => return error.ExpectedFreshZeroByteResource,
            };
            if (fresh.region.len != 0 or fresh.lease.slot != api_slot) return error.InvalidFixturePlacement;
            self.zero_byte_api_slot = fresh.lease.slot;
            _ = try self.ledger.endUse(fresh.lease, .complete);
        }

        fn isNestedFixedSlot(slot: usize) bool {
            const table = slot / rows_per_table;
            const offset = slot % rows_per_table;
            return offset == nested_fixed_ordinal and table < table_count;
        }

        fn auditWorkspace(self: *Self) Ledger.Audit.Workspace {
            return .{
                .shell_queue = self.audit_shell_queue,
                .shell_marks = self.audit_shell_marks,
                .depth = self.audit_depth,
                .events = self.audit_events,
                .stack = self.audit_stack,
                .table_views = self.audit_table_views,
                .fixed_marks = self.audit_fixed_marks,
                .allocations = self.audit_allocations,
            };
        }

        fn expectedNeighborLoads(self: *const Self) usize {
            if (flavor == .c2) {
                var nonempty_homes: usize = 0;
                for (self.ledger.index.homes) |home| {
                    if (home.head.load(.seq_cst) != c2_index.no_node) nonempty_homes += 1;
                }
                return (Ledger.resource_capacity + Ledger.partition_capacity) - nonempty_homes;
            }

            var loads: usize = 0;
            for (self.ledger.index.buckets, 0..) |bucket, index| {
                if (bucket.node_id.load(.seq_cst) == b2_index.no_bucket) continue;
                const previous = if (index == 0) self.ledger.index.buckets.len - 1 else index - 1;
                if (self.ledger.index.buckets[previous].node_id.load(.seq_cst) != b2_index.no_bucket) loads += 1;
            }
            return loads;
        }

        pub fn verifyMaximum(self: *Self) !void {
            try std.testing.expectEqual(rows_per_table_total, self.ledger.rows.len);
            try std.testing.expectEqual(shells_total, self.ledger.shells.len);
            try std.testing.expectEqual(index_buckets, self.ledger.index.homes.len);
            try std.testing.expectEqual(rows_per_table_total + shells_total, self.ledger.index.nodes.len);
            try std.testing.expectEqual(@as(u32, 0), self.zero_byte_api_slot.?);

            var fixed_allocation_numbers: usize = 0;
            inline for (0..fixed_total) |fixed_id| {
                try std.testing.expectEqual(Owner.api.UseResult.not_in_memory, try self.ledger.useFixed(fixed_id));
                const binding = &self.bindings[fixed_id];
                const table = fixed_id / fixed_per_table;
                const expected_slot = Ledger.resourceStart(table) +
                    (if (fixed_id % fixed_per_table == 0) rows_per_table - 1 else nested_fixed_ordinal);
                try std.testing.expectEqual(@as(u32, @intCast(expected_slot)), binding.slot.load(.acquire));
                if (self.rows[expected_slot].resource.generation != 0) fixed_allocation_numbers += 1;
                if (fixed_id % fixed_per_table == 0) {
                    try std.testing.expectEqual(@as(u64, 0), binding.parent_number.load(.acquire));
                } else {
                    const shell_slot = Ledger.partitionStart(table) + leaf_depth;
                    try std.testing.expectEqual(self.shells[shell_slot].allocation_number, binding.parent_number.load(.acquire));
                }
            }

            for (self.table_states, 0..) |_, table| {
                const view = try self.ledger.view(@intCast(table));
                try std.testing.expectEqual(@as(u64, 30), view.registered_bytes);
                try std.testing.expectEqual(@as(u64, 0), view.system_page_bytes);
            }

            var live_entries: usize = 0;
            for (self.rows, 0..) |row, slot| {
                const uid = row.uid orelse return error.MissingMaximumResource;
                const position = (try self.ledger.index.find(uid)) orelse return error.MaximumIndexMiss;
                if (position.kind != .resource or position.table != row.table or position.slot != slot)
                    return error.MaximumIndexLocationMismatch;
                live_entries += 1;
            }
            for (self.shells, 0..) |shell, slot| {
                const uid = shell.uid orelse return error.MissingMaximumPartition;
                const position = (try self.ledger.index.find(uid)) orelse return error.MaximumIndexMiss;
                if (position.kind != .partition or position.table != shell.table or position.slot != slot)
                    return error.MaximumIndexLocationMismatch;
                live_entries += 1;
            }
            try std.testing.expectEqual(rows_per_table_total + shells_total, live_entries);

            var workspace = self.auditWorkspace();
            const stats = try self.ledger.audit(&workspace);
            try std.testing.expectEqual(rows_per_table_total, stats.resources);
            try std.testing.expectEqual(shells_total, stats.partitions);
            try std.testing.expectEqual(fixed_total, stats.fixed_declarations);
            try std.testing.expectEqual(table_count, stats.tables);
            try std.testing.expectEqual(rows_per_table_total, stats.active_resources);
            try std.testing.expectEqual(shells_total, stats.active_partitions);
            try std.testing.expectEqual(@as(usize, 0), stats.free_partitions);
            try std.testing.expectEqual(fixed_total, stats.fixed_rows);
            try std.testing.expectEqual(table_count, stats.fixed_identity_lookups);
            try std.testing.expectEqual(shells_total, stats.partition_links);
            try std.testing.expectEqual(shells_total * 2, stats.interval_events);
            try std.testing.expectEqual(
                rows_per_table_total + shells_total - fixed_total + fixed_allocation_numbers,
                stats.allocation_numbers,
            );
            try std.testing.expectEqual(@as(u128, table_count * 30), stats.registered_bytes);
            try std.testing.expectEqual(@as(u128, 0), stats.system_page_bytes);
            try std.testing.expectEqual(index_buckets, stats.index_home_body);
            try std.testing.expectEqual(rows_per_table_total + shells_total, stats.index_node_body);
            try std.testing.expectEqual(self.expectedNeighborLoads(), stats.index_neighbor_payload_loads);
            try std.testing.expect(stats.event_sort_comparisons > 0);
            try std.testing.expect(stats.allocation_sort_comparisons > 0);

            const zero_row = &self.rows[self.zero_byte_api_slot.?];
            try std.testing.expectEqual(@as(u64, 0), zero_row.resource.registration.?.allocation_bytes);
            try std.testing.expect(zero_row.resource.registration.?.region != null);
            try std.testing.expectEqual(@as(u64, 589_696), zero_row.resource.generation);
            try std.testing.expectEqual(@as(usize, 0), zero_row.resource.registration.?.allocatedPageBytes());
        }
    };
}

fn runMaximum(comptime bits: u16, comptime flavor: CandidateFlavor) !void {
    const F = Fixture(bits, flavor);
    var fixture = try F.create(std.heap.page_allocator);
    defer fixture.deinit(std.heap.page_allocator);

    try fixture.populateMaximum();
    try fixture.verifyMaximum();

    const footprint = F.Footprint{};
    std.debug.print(
        "owner maxima {s}/{d}: row={d} shell={d} node={d} home={d} audit_scratch={d} total={d} bytes\n",
        .{
            @tagName(flavor),
            bits,
            @sizeOf(F.Owner.Row),
            @sizeOf(F.Owner.Shell),
            @sizeOf(F.IndexType.Node),
            @sizeOf(F.IndexType.Home),
            footprint.audit_scratch,
            footprint.total(),
        },
    );
}

test "original complete-owner maxima audit both candidates and UID widths" {
    inline for (.{ 128, 256 }) |bits| {
        try runMaximum(bits, .c2);
        try runMaximum(bits, .b2);
    }
}
