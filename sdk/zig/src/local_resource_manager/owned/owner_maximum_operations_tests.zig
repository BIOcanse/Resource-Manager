//! Original-maximum tree close and full-owner destroy traces.
//!
//! The fixture constructs stable metadata directly; it does not count that
//! setup as Owner API latency. The only counted push is the zero-byte resident
//! row used by the existing maximum fixture. Every operation workspace below
//! is caller-owned and sized before the API call.
const std = @import("std");
const fixtures = @import("owner_maxima_tests.zig");
const types = @import("types.zig");
const c2_index = @import("index.zig");
const b2_index = @import("index_b2.zig");

const table_count = 64;
const rows_per_table = 8_192;
const shells_per_table = 1_024;
const fixed_per_table = 2;
const rows_total = 524_288;
const shells_total = 65_536;
const node_total = rows_total + shells_total;
const bucket_total = 1_048_576;
const rows_per_table_dynamic = rows_per_table - fixed_per_table;
const nested_fixed_ordinal = 7_167;
const tree_rows = 8_191;
const tree_shells = shells_per_table;
const tree_removals = tree_rows + tree_shells;
const notifications_per_table = rows_per_table_dynamic + shells_per_table;
const destruction_notifications = notifications_per_table * table_count;
const dynamic_uid_base = 2_000_000;
const shell_uid_base = 1_000_000;
const api_dynamic_uid = 4_000_000;
const first_shell_allocation = 524_160;
const api_dynamic_allocation = 589_696;

fn TransactionStorage(comptime Index: type) type {
    const Transactions = Index.Transactions;
    const Workspace = Transactions.Workspace;

    return struct {
        const Self = @This();

        workspace: Workspace = .{},
        bytes: usize = 0,

        pub fn create(
            allocator: std.mem.Allocator,
            removal_count: usize,
            put_count: usize,
            closed_bucket_count: usize,
        ) !Self {
            const operation_count = std.math.add(usize, removal_count, put_count) catch
                return error.WorkspaceTooSmall;
            var result = Self{};

            if (@hasField(Workspace, "homes")) {
                const homes = try allocator.alloc(u32, operation_count);
                const nodes = try allocator.alloc(u32, operation_count);
                const publications = try allocator.alloc(Index.Publication, operation_count);
                const removals = try allocator.alloc(Index.RemovePlan, removal_count);
                const inserts = try allocator.alloc(Index.InsertPlan, put_count);
                result.workspace = .{
                    .homes = homes,
                    .nodes = nodes,
                    .publications = publications,
                    .removals = removals,
                    .inserts = inserts,
                };
                result.bytes = homes.len * @sizeOf(u32) +
                    nodes.len * @sizeOf(u32) +
                    publications.len * @sizeOf(Index.Publication) +
                    removals.len * @sizeOf(Index.RemovePlan) +
                    inserts.len * @sizeOf(Index.InsertPlan);
            } else if (@hasField(Workspace, "operations")) {
                const operations = try allocator.alloc(Transactions.Operation, operation_count);
                const segments = try allocator.alloc(Transactions.Segment, operation_count * 2);
                const indices = try allocator.alloc(u32, closed_bucket_count);
                const versions = try allocator.alloc(u64, closed_bucket_count);
                const removals = try allocator.alloc(Index.RemovePlan, removal_count);
                const inserts = try allocator.alloc(Index.InsertPlan, put_count);
                const simulation_nodes = try allocator.alloc(u32, closed_bucket_count);
                const simulation_uids = try allocator.alloc(Index.uid_type, closed_bucket_count);
                const simulation_node_ids = try allocator.alloc(u32, closed_bucket_count + put_count);
                const simulation_backlinks = try allocator.alloc(u32, closed_bucket_count + put_count);
                result.workspace = .{
                    .operations = operations,
                    .segments = segments,
                    .indices = indices,
                    .versions = versions,
                    .removals = removals,
                    .inserts = inserts,
                    .simulation_nodes = simulation_nodes,
                    .simulation_uids = simulation_uids,
                    .simulation_node_ids = simulation_node_ids,
                    .simulation_backlinks = simulation_backlinks,
                };
                result.bytes = operations.len * @sizeOf(Transactions.Operation) +
                    segments.len * @sizeOf(Transactions.Segment) +
                    indices.len * @sizeOf(u32) +
                    versions.len * @sizeOf(u64) +
                    removals.len * @sizeOf(Index.RemovePlan) +
                    inserts.len * @sizeOf(Index.InsertPlan) +
                    simulation_nodes.len * @sizeOf(u32) +
                    simulation_uids.len * @sizeOf(Index.uid_type) +
                    simulation_node_ids.len * @sizeOf(u32) +
                    simulation_backlinks.len * @sizeOf(u32);
            } else {
                return error.UnsupportedTransactionWorkspace;
            }
            return result;
        }
    };
}

const ClosureProfile = struct {
    occupied_entries: usize = 0,
    selected_nodes: usize = 0,
    selected_clusters: usize = 0,
    union_buckets: usize = 0,
    individual_prefix_sum_buckets: usize = 0,
    maximum_sample_buckets: usize = 0,
};

/// Computes the union of remove prefixes in one physical bucket scan. For a
/// contiguous occupied run, each selected node's remove prefix ends at the
/// first empty bucket after the run. Their union is the suffix beginning at
/// the earliest selected node, including that empty endpoint.
fn profileB2Removals(index: anytype, removal_ids: []const u32, marks: []u8) !ClosureProfile {
    if (marks.len < index.nodes.len) return error.WorkspaceTooSmall;
    @memset(marks[0..index.nodes.len], 0);
    for (removal_ids) |node_id| {
        if (@as(usize, node_id) >= index.nodes.len or
            !index.nodes[node_id].linked.load(.seq_cst) or marks[node_id] != 0)
            return error.InvalidRemovalFixture;
        marks[node_id] = 1;
    }

    var anchor: ?usize = null;
    for (index.buckets, 0..) |bucket, bucket_index| {
        if (bucket.node_id.load(.seq_cst) == b2_index.no_bucket) {
            anchor = bucket_index;
            break;
        }
    }
    const empty_anchor = anchor orelse return error.IndexFull;

    var profile = ClosureProfile{};
    var run_length: usize = 0;
    var run_selected: usize = 0;
    var first_selected_offset: usize = 0;
    var selected_offset_sum: usize = 0;
    var cursor = if (empty_anchor + 1 == index.buckets.len) 0 else empty_anchor + 1;

    for (0..index.buckets.len) |_| {
        const node_id = index.buckets[cursor].node_id.load(.seq_cst);
        if (node_id == b2_index.no_bucket) {
            if (run_selected != 0) {
                const sample_sum = run_selected * (run_length + 1) - selected_offset_sum;
                const suffix = run_length - first_selected_offset + 1;
                profile.selected_clusters += 1;
                profile.union_buckets += suffix;
                profile.individual_prefix_sum_buckets += sample_sum;
                profile.maximum_sample_buckets = @max(profile.maximum_sample_buckets, suffix);
            }
            run_length = 0;
            run_selected = 0;
            first_selected_offset = 0;
            selected_offset_sum = 0;
        } else {
            if (@as(usize, node_id) >= index.nodes.len or
                !index.nodes[node_id].linked.load(.seq_cst) or
                index.nodes[node_id].bucket_index.load(.seq_cst) != cursor)
                return error.InvalidIndexState;
            profile.occupied_entries += 1;
            if (marks[node_id] != 0) {
                if (run_selected == 0) first_selected_offset = run_length;
                run_selected += 1;
                selected_offset_sum += run_length;
                profile.selected_nodes += 1;
            }
            run_length += 1;
        }
        cursor = if (cursor + 1 == index.buckets.len) 0 else cursor + 1;
    }

    if (run_length != 0 or profile.selected_nodes != removal_ids.len)
        return error.InvalidIndexState;
    return profile;
}

fn assertGroupedSampleBound(profile: ClosureProfile, stats: anytype) !void {
    const maximum = std.math.add(
        usize,
        profile.union_buckets,
        profile.maximum_sample_buckets,
    ) catch return error.InvalidClosureProfile;
    try std.testing.expect(stats.sampled_prefix_buckets >= profile.union_buckets);
    try std.testing.expect(stats.sampled_prefix_buckets <= maximum);
}

fn AuditWorkspace(comptime F: type) type {
    return F.Owner.Audit.Workspace;
}

fn audit(comptime F: type, fixture: *F) !F.Owner.Audit.Stats {
    var work = AuditWorkspace(F){
        .shell_queue = fixture.audit_shell_queue,
        .shell_marks = fixture.audit_shell_marks,
        .depth = fixture.audit_depth,
        .events = fixture.audit_events,
        .stack = fixture.audit_stack,
        .table_views = fixture.audit_table_views,
        .fixed_marks = fixture.audit_fixed_marks,
        .allocations = fixture.audit_allocations,
    };
    return fixture.ledger.audit(&work);
}

fn assertMaximumStats(stats: anytype) !void {
    try std.testing.expectEqual(rows_total, stats.resources);
    try std.testing.expectEqual(shells_total, stats.partitions);
    try std.testing.expectEqual(@as(usize, 128), stats.fixed_declarations);
    try std.testing.expectEqual(table_count, stats.tables);
    try std.testing.expectEqual(rows_total, stats.active_resources);
    try std.testing.expectEqual(shells_total, stats.active_partitions);
    try std.testing.expectEqual(@as(usize, 0), stats.free_partitions);
    try std.testing.expectEqual(@as(usize, 128), stats.fixed_rows);
    try std.testing.expectEqual(table_count, stats.fixed_identity_lookups);
    try std.testing.expectEqual(shells_total, stats.partition_links);
    try std.testing.expectEqual(shells_total * 2, stats.interval_events);
    try std.testing.expectEqual(node_total - 128, stats.allocation_numbers);
    try std.testing.expectEqual(@as(u128, 1_920), stats.registered_bytes);
    try std.testing.expectEqual(@as(u128, 0), stats.system_page_bytes);
    try std.testing.expectEqual(bucket_total, stats.index_home_body);
    try std.testing.expectEqual(node_total, stats.index_node_body);
    try std.testing.expect(stats.event_sort_comparisons > 0);
    try std.testing.expect(stats.allocation_sort_comparisons > 0);
}

fn assertPostCloseStats(stats: anytype) !void {
    try std.testing.expectEqual(rows_total, stats.resources);
    try std.testing.expectEqual(shells_total, stats.partitions);
    try std.testing.expectEqual(@as(usize, 128), stats.fixed_declarations);
    try std.testing.expectEqual(table_count, stats.tables);
    try std.testing.expectEqual(@as(usize, 516_097), stats.active_resources);
    try std.testing.expectEqual(@as(usize, 64_512), stats.active_partitions);
    try std.testing.expectEqual(@as(usize, 1_024), stats.free_partitions);
    try std.testing.expectEqual(@as(usize, 127), stats.fixed_rows);
    try std.testing.expectEqual(table_count, stats.fixed_identity_lookups);
    try std.testing.expectEqual(@as(usize, 64_512), stats.partition_links);
    try std.testing.expectEqual(@as(usize, 129_024), stats.interval_events);
    try std.testing.expectEqual(@as(usize, 580_482), stats.allocation_numbers);
    try std.testing.expectEqual(@as(u128, 1_903), stats.registered_bytes);
    try std.testing.expectEqual(@as(u128, 0), stats.system_page_bytes);
    try std.testing.expectEqual(bucket_total, stats.index_home_body);
    // Audit visits the complete backing array, even after table 0 is closed.
    try std.testing.expectEqual(node_total, stats.index_node_body);
    try std.testing.expect(stats.event_sort_comparisons > 0);
    try std.testing.expect(stats.allocation_sort_comparisons > 0);
}

fn checkFixedBindings(comptime F: type, fixture: *F, root_closed: bool) !void {
    for (0..table_count) |table| {
        const direct_id = table * fixed_per_table;
        const nested_id = direct_id + 1;
        const direct_slot: u32 = @intCast(table * rows_per_table + rows_per_table - 1);
        const nested_slot: u32 = @intCast(table * rows_per_table + nested_fixed_ordinal);
        try std.testing.expectEqual(direct_slot, fixture.bindings[direct_id].slot.load(.acquire));
        try std.testing.expectEqual(@as(u64, 0), fixture.bindings[direct_id].parent_number.load(.acquire));

        if (root_closed and table == 0) {
            try std.testing.expectEqual(types.no_slot, fixture.bindings[nested_id].slot.load(.acquire));
            // The no-slot publication invalidates this binding. Its previous
            // parent number is not a live identity and need not be zeroed.
        } else {
            try std.testing.expectEqual(nested_slot, fixture.bindings[nested_id].slot.load(.acquire));
            const shell_slot = table * shells_per_table + shells_per_table - 1;
            try std.testing.expectEqual(
                fixture.shells[shell_slot].allocation_number,
                fixture.bindings[nested_id].parent_number.load(.acquire),
            );
        }
    }
}

fn checkTableAccounting(comptime F: type, fixture: *F, root_closed: bool) !void {
    for (0..table_count) |table| {
        const view = try fixture.ledger.view(@intCast(table));
        const expected: u64 = if (root_closed and table == 0) 13 else 30;
        try std.testing.expectEqual(expected, view.registered_bytes);
        try std.testing.expectEqual(@as(u64, 0), view.system_page_bytes);
    }
}

fn fillTreeRemovalIds(comptime F: type, fixture: *F, output: []u32) !usize {
    if (output.len < tree_removals) return error.WorkspaceTooSmall;
    var count: usize = 0;
    for (0..tree_rows) |offset| {
        const slot = F.Owner.resourceStart(0) + offset;
        if (fixture.rows[slot].uid == null) return error.InvalidMaximumFixture;
        output[count] = @intCast(slot);
        count += 1;
    }
    for (0..tree_shells) |offset| {
        const slot = F.Owner.partitionStart(0) + offset;
        if (fixture.shells[slot].uid == null) return error.InvalidMaximumFixture;
        output[count] = @intCast(rows_total + slot);
        count += 1;
    }
    return count;
}

fn fillDestroyRemovalIds(comptime F: type, fixture: *F, output: []u32) !usize {
    if (output.len < node_total) return error.WorkspaceTooSmall;
    var count: usize = 0;
    for (0..table_count) |table| {
        const row_start = F.Owner.resourceStart(table);
        for (0..rows_per_table) |offset| {
            const slot = row_start + offset;
            if (fixture.rows[slot].uid == null) return error.InvalidMaximumFixture;
            output[count] = @intCast(slot);
            count += 1;
        }
        const shell_start = F.Owner.partitionStart(table);
        for (0..shells_per_table) |offset| {
            const slot = shell_start + offset;
            if (fixture.shells[slot].uid == null) return error.InvalidMaximumFixture;
            output[count] = @intCast(rows_total + slot);
            count += 1;
        }
    }
    return count;
}

fn EventVerifier(comptime Uid: type) type {
    return struct {
        pub const Context = struct {
            table: usize,
            seen: usize = 0,
            failed: bool = false,
        };
        const Identity = struct { uid: Uid, allocation_number: u64 };

        fn uid(value: u64) Uid {
            var result: Uid = .{ .bytes = @splat(0) };
            std.mem.writeInt(u64, result.bytes[result.bytes.len - 8 ..][0..8], value, .big);
            return result;
        }

        fn expected(context: Context, index: usize) Identity {
            if (index < rows_per_table_dynamic) {
                const offset = if (index < nested_fixed_ordinal) index else index + 1;
                if (context.table == 0 and offset == 0) {
                    return .{ .uid = uid(api_dynamic_uid), .allocation_number = api_dynamic_allocation };
                }
                const global_slot = context.table * rows_per_table + offset;
                var number: u64 = context.table * rows_per_table_dynamic + offset;
                if (offset > nested_fixed_ordinal) number -= 1;
                return .{
                    .uid = uid(dynamic_uid_base + global_slot),
                    .allocation_number = number,
                };
            }

            const shell_offset = index - rows_per_table_dynamic;
            const global_shell = context.table * shells_per_table + shell_offset;
            return .{
                .uid = uid(shell_uid_base + global_shell),
                .allocation_number = first_shell_allocation + global_shell,
            };
        }

        pub fn callback(raw_context: ?*anyopaque, actual_uid: Uid, actual_number: u64) callconv(.c) void {
            const context: *Context = @ptrCast(@alignCast(raw_context orelse return));
            if (context.seen >= notifications_per_table) {
                context.failed = true;
                return;
            }
            const want = expected(context.*, context.seen);
            if (!want.uid.eql(actual_uid) or want.allocation_number != actual_number)
                context.failed = true;
            context.seen += 1;
        }
    };
}

fn assertNoCallbacks(Verifier: type, contexts: []const Verifier.Context) !void {
    for (contexts) |context| {
        try std.testing.expectEqual(@as(usize, 0), context.seen);
        try std.testing.expect(!context.failed);
    }
}

fn assertCallbacks(Verifier: type, contexts: []const Verifier.Context) !void {
    for (contexts) |context| {
        try std.testing.expectEqual(notifications_per_table, context.seen);
        try std.testing.expect(!context.failed);
    }
}

fn printAudit(comptime F: type, comptime flavor: fixtures.CandidateFlavor, bits: u16, phase: []const u8, stats: anytype) void {
    std.debug.print(
        "max-owner {s}/{d} {s}: active={d}+{d} free={d} fixed={d} links={d} events={d} allocs={d} bytes={d}/{d} index-body={d}+{d} audit-compare={d}/{d} neighbor-loads={d}\n",
        .{
            @tagName(flavor),                  bits,                               phase,
            stats.active_resources,            stats.active_partitions,            stats.free_partitions,
            stats.fixed_rows,                  stats.partition_links,              stats.interval_events,
            stats.allocation_numbers,          stats.registered_bytes,             stats.system_page_bytes,
            stats.index_home_body,             stats.index_node_body,              stats.event_sort_comparisons,
            stats.allocation_sort_comparisons, stats.index_neighbor_payload_loads,
        },
    );
    const footprint = F.Footprint{};
    const subscriber_bytes = F.Owner.table_count * @sizeOf(@typeInfo(@FieldType(F, "subscriber_records")).pointer.child);
    std.debug.print(
        "max-owner {s}/{d} fixture: rows={d} shells={d} nodes={d} homes={d} audit={d} subscribers={d} total={d}\n",
        .{
            @tagName(flavor),                     bits,
            @sizeOf(F.Owner.Row),                 @sizeOf(F.Owner.Shell),
            @sizeOf(F.IndexType.Node),            @sizeOf(F.IndexType.Home),
            footprint.audit_scratch,              subscriber_bytes,
            footprint.total() + subscriber_bytes,
        },
    );
}

fn closeWorkspace(comptime F: type, allocator: std.mem.Allocator, index_workspace: anytype) !F.Owner.Workspace {
    return .{
        .candidates = &.{},
        .rows = try allocator.alloc(F.Owner.Candidate, tree_rows),
        .shells = try allocator.alloc(u32, tree_shells),
        .homes = try allocator.alloc(u32, 1),
        .notes = try allocator.alloc(F.Owner.api.Notification, notifications_per_table),
        .remove_ids = try allocator.alloc(u32, tree_removals),
        .puts = &.{},
        .index_workspace = index_workspace,
    };
}

fn destroyWorkspace(comptime F: type, allocator: std.mem.Allocator, index_workspace: anytype) !F.Owner.Destruction.Workspace {
    return .{
        .remove_ids = try allocator.alloc(u32, node_total),
        .notices = try allocator.alloc(F.Owner.Destruction.Notice, destruction_notifications),
        .table_views = try allocator.alloc(types.TableView, table_count),
        .index_workspace = index_workspace,
    };
}

fn printOperationScratch(comptime F: type, comptime flavor: fixtures.CandidateFlavor, bits: u16, label: []const u8, transaction_bytes: usize, owner_bytes: usize) void {
    const footprint = F.Footprint{};
    const subscriber_bytes = table_count * @sizeOf(@typeInfo(@FieldType(F, "subscriber_records")).pointer.child);
    std.debug.print(
        "max-owner {s}/{d} {s} scratch: index={d} owner={d} fixture+audit+subscribers={d} peak={d}\n",
        .{
            @tagName(flavor),                     bits,                                                                   label, transaction_bytes, owner_bytes,
            footprint.total() + subscriber_bytes, footprint.total() + subscriber_bytes + transaction_bytes + owner_bytes,
        },
    );
}

fn runTreeClose(comptime bits: u16, comptime flavor: fixtures.CandidateFlavor) !void {
    const F = fixtures.Fixture(bits, flavor);
    const Uid = F.UidType;
    const Verifier = EventVerifier(Uid);
    var fixture = try F.create(std.heap.page_allocator);
    defer fixture.deinit(std.heap.page_allocator);
    try fixture.populateMaximum();

    const baseline = try audit(F, &fixture);
    try assertMaximumStats(baseline);
    printAudit(F, flavor, bits, "before-tree-close", baseline);

    var arena = std.heap.ArenaAllocator.init(std.heap.page_allocator);
    defer arena.deinit();
    const allocator = arena.allocator();
    var owner_work: F.Owner.Workspace = undefined;
    var transaction: TransactionStorage(F.IndexType) = undefined;

    if (flavor == .b2) {
        const profile_marks = try std.heap.page_allocator.alloc(u8, fixture.ledger.index.nodes.len);
        defer std.heap.page_allocator.free(profile_marks);
        owner_work = try closeWorkspace(F, allocator, F.IndexType.Transactions.Workspace{});
        const ids = owner_work.remove_ids;
        const id_count = try fillTreeRemovalIds(F, &fixture, ids);
        try std.testing.expectEqual(tree_removals, id_count);
        const profile = try profileB2Removals(&fixture.ledger.index, ids, profile_marks);
        try std.testing.expectEqual(tree_removals, profile.selected_nodes);
        try std.testing.expectEqual(node_total, profile.occupied_entries);
        transaction = try TransactionStorage(F.IndexType).create(allocator, tree_removals, 0, profile.union_buckets);
        owner_work.index_workspace = transaction.workspace;
        const tx = F.IndexType.Transactions;
        var stats = tx.PrepareStats{};
        var prepared = try tx.prepareWithStats(
            &fixture.ledger.index,
            ids,
            &.{},
            &transaction.workspace,
            &stats,
        );
        const prepared_count = prepared.indices.len;
        prepared.abort();
        try std.testing.expectEqual(profile.union_buckets, prepared_count);
        try std.testing.expectEqual(profile.union_buckets, stats.closure_buckets_enumerated);
        try assertGroupedSampleBound(profile, stats);
        std.debug.print(
            "max-owner b2/{d} tree-close profile: selected={d} occupied={d} clusters={d} union={d} individual-prefix-sum={d} max-sample={d} stats-prefix={d} segments={d}/{d} enumerated={d} validate={d}/{d} shift={d} simulate={d}\n",
            .{
                bits,                            profile.selected_nodes,                profile.occupied_entries,         profile.selected_clusters,
                profile.union_buckets,           profile.individual_prefix_sum_buckets, profile.maximum_sample_buckets,   stats.sampled_prefix_buckets,
                stats.operation_segments,        stats.merged_segments,                 stats.closure_buckets_enumerated, stats.closure_validation_passes,
                stats.closure_validation_visits, stats.removal_shift_prefix_visits,     stats.simulation_bucket_visits,
            },
        );
        printOperationScratch(F, flavor, bits, "tree-close", transaction.bytes, tree_rows * @sizeOf(F.Owner.Candidate) + tree_shells * @sizeOf(u32) + @sizeOf(u32) +
            notifications_per_table * @sizeOf(F.Owner.api.Notification) + tree_removals * @sizeOf(u32) +
            profile_marks.len * @sizeOf(u8));
    } else {
        transaction = try TransactionStorage(F.IndexType).create(allocator, tree_removals, 0, 0);
        owner_work = try closeWorkspace(F, allocator, transaction.workspace);
        const ids = owner_work.remove_ids;
        const id_count = try fillTreeRemovalIds(F, &fixture, ids);
        try std.testing.expectEqual(tree_removals, id_count);
        printOperationScratch(F, flavor, bits, "tree-close", transaction.bytes, tree_rows * @sizeOf(F.Owner.Candidate) + tree_shells * @sizeOf(u32) + @sizeOf(u32) +
            notifications_per_table * @sizeOf(F.Owner.api.Notification) + tree_removals * @sizeOf(u32));
    }

    var callback_context = Verifier.Context{ .table = 0 };
    const subscription = try fixture.ledger.subscribe(0, Verifier.callback, @ptrCast(&callback_context));
    const fatal_slot = F.Owner.resourceStart(0) + tree_rows - 1;
    const saved_attributes = fixture.rows[fatal_slot].attributes;
    var fatal_attributes = saved_attributes;
    fatal_attributes.access_loss_impact = .fatal;
    fixture.rows[fatal_slot].attributes = fatal_attributes;

    try std.testing.expectError(
        error.Blocked,
        fixture.ledger.close(fixtures.key(Uid, shell_uid_base), &owner_work),
    );
    try std.testing.expectEqual(@as(usize, 0), owner_work.note_count);
    try assertNoCallbacks(Verifier, &.{callback_context});
    const after_block = try audit(F, &fixture);
    try std.testing.expect(std.meta.eql(baseline, after_block));
    fixture.rows[fatal_slot].attributes = saved_attributes;

    try fixture.ledger.close(fixtures.key(Uid, shell_uid_base), &owner_work);
    try assertCallbacks(Verifier, &.{callback_context});
    try checkFixedBindings(F, &fixture, true);
    try checkTableAccounting(F, &fixture, true);
    const after_close = try audit(F, &fixture);
    try assertPostCloseStats(after_close);
    printAudit(F, flavor, bits, "after-tree-close", after_close);
    try std.testing.expectEqual(@as(usize, 580_609), after_close.active_resources + after_close.active_partitions);

    for (0..tree_rows) |offset| {
        const slot = F.Owner.resourceStart(0) + offset;
        try std.testing.expect(fixture.rows[slot].uid == null);
        try std.testing.expect(fixture.rows[slot].resource.registration == null);
    }
    for (0..tree_shells) |offset| {
        const slot = F.Owner.partitionStart(0) + offset;
        try std.testing.expect(fixture.shells[slot].uid == null);
        try std.testing.expectEqual(@as(u32, 0), fixture.shells[slot].capacity);
    }
    for (1..table_count) |table| {
        const row_start = F.Owner.resourceStart(table);
        for (0..rows_per_table) |offset| try std.testing.expect(fixture.rows[row_start + offset].uid != null);
        const shell_start = F.Owner.partitionStart(table);
        for (0..shells_per_table) |offset| try std.testing.expect(fixture.shells[shell_start + offset].uid != null);
    }
    try fixture.ledger.unsubscribe(subscription);
}

fn assertDestroyedStorage(comptime F: type, fixture: *F) !void {
    for (fixture.rows) |row| {
        try std.testing.expect(row.uid == null);
        try std.testing.expect(row.resource.registration == null);
        try std.testing.expectEqual(@as(u64, 0), row.resource.use_count);
        try std.testing.expect(!row.resource.loading);
        try std.testing.expect(row.fixed_declaration == null);
    }
    for (fixture.shells, 0..) |shell, slot| {
        try std.testing.expect(shell.uid == null);
        try std.testing.expectEqual(@as(u32, 0), shell.capacity);
        try std.testing.expectEqual(types.no_slot, shell.first_child);
        const table_end = ((slot / shells_per_table) + 1) * shells_per_table;
        const expected_next: u32 = if (slot + 1 == table_end) types.no_slot else @intCast(slot + 1);
        try std.testing.expectEqual(expected_next, shell.next_sibling);
    }
    for (fixture.bindings) |binding| {
        try std.testing.expectEqual(types.no_slot, binding.slot.load(.acquire));
        try std.testing.expectEqual(@as(u64, 0), binding.parent_number.load(.acquire));
    }
    for (fixture.table_states, 0..) |state, table| {
        try std.testing.expectEqual(@as(u64, 0), state.registered_bytes);
        try std.testing.expectEqual(@as(u64, 0), state.system_page_bytes);
        try std.testing.expectEqual(types.no_slot, state.first_partition);
        try std.testing.expectEqual(@as(u32, @intCast(table * shells_per_table)), state.free_partition);
    }
    for (fixture.nodes) |node| {
        try std.testing.expect(!node.linked.load(.seq_cst));
        if (@hasField(F.IndexType.Node, "bucket_index")) {
            try std.testing.expectEqual(b2_index.no_bucket, node.bucket_index.load(.seq_cst));
        } else {
            try std.testing.expectEqual(c2_index.no_node, node.home_index.load(.seq_cst));
        }
    }

    if (@hasField(F.IndexType.Home, "head")) {
        for (fixture.homes) |home|
            try std.testing.expectEqual(c2_index.no_node, home.head.load(.seq_cst));
    } else {
        for (fixture.homes) |home|
            try std.testing.expectEqual(b2_index.no_bucket, home.node_id.load(.seq_cst));
    }
}

fn runFullDestroy(comptime bits: u16, comptime flavor: fixtures.CandidateFlavor) !void {
    const F = fixtures.Fixture(bits, flavor);
    const Uid = F.UidType;
    const Verifier = EventVerifier(Uid);
    var fixture = try F.create(std.heap.page_allocator);
    defer fixture.deinit(std.heap.page_allocator);
    try fixture.populateMaximum();

    const baseline = try audit(F, &fixture);
    try assertMaximumStats(baseline);
    printAudit(F, flavor, bits, "before-destroy", baseline);

    var arena = std.heap.ArenaAllocator.init(std.heap.page_allocator);
    defer arena.deinit();
    const allocator = arena.allocator();
    var transaction: TransactionStorage(F.IndexType) = undefined;
    var destroy_work: F.Owner.Destruction.Workspace = undefined;

    if (flavor == .b2) {
        destroy_work = try destroyWorkspace(F, allocator, F.IndexType.Transactions.Workspace{});
        const id_count = try fillDestroyRemovalIds(F, &fixture, destroy_work.remove_ids);
        try std.testing.expectEqual(node_total, id_count);
        const marks = try std.heap.page_allocator.alloc(u8, fixture.ledger.index.nodes.len);
        defer std.heap.page_allocator.free(marks);
        const profile = try profileB2Removals(&fixture.ledger.index, destroy_work.remove_ids, marks);
        try std.testing.expectEqual(node_total, profile.selected_nodes);
        try std.testing.expectEqual(node_total, profile.occupied_entries);
        const b2_scratch = try TransactionStorage(F.IndexType).create(allocator, node_total, 0, profile.union_buckets);
        transaction = b2_scratch;
        destroy_work.index_workspace = transaction.workspace;
        var stats = F.IndexType.Transactions.PrepareStats{};
        var prepared = try F.IndexType.Transactions.prepareWithStats(
            &fixture.ledger.index,
            destroy_work.remove_ids,
            &.{},
            &destroy_work.index_workspace,
            &stats,
        );
        const prepared_count = prepared.indices.len;
        prepared.abort();
        try std.testing.expectEqual(profile.union_buckets, prepared_count);
        try std.testing.expectEqual(profile.union_buckets, stats.closure_buckets_enumerated);
        try assertGroupedSampleBound(profile, stats);
        std.debug.print(
            "max-owner b2/{d} destroy profile: selected={d} occupied={d} clusters={d} union={d} individual-prefix-sum={d} max-sample={d} stats-prefix={d} segments={d}/{d} enumerated={d} validate={d}/{d} shift={d} simulate={d}\n",
            .{
                bits,                            profile.selected_nodes,                profile.occupied_entries,         profile.selected_clusters,
                profile.union_buckets,           profile.individual_prefix_sum_buckets, profile.maximum_sample_buckets,   stats.sampled_prefix_buckets,
                stats.operation_segments,        stats.merged_segments,                 stats.closure_buckets_enumerated, stats.closure_validation_passes,
                stats.closure_validation_visits, stats.removal_shift_prefix_visits,     stats.simulation_bucket_visits,
            },
        );
        printOperationScratch(F, flavor, bits, "destroy", transaction.bytes, node_total * @sizeOf(u32) + destruction_notifications * @sizeOf(F.Owner.Destruction.Notice) +
            table_count * @sizeOf(types.TableView) + marks.len * @sizeOf(u8));
    } else {
        transaction = try TransactionStorage(F.IndexType).create(allocator, node_total, 0, 0);
        destroy_work = try destroyWorkspace(F, allocator, transaction.workspace);
        const id_count = try fillDestroyRemovalIds(F, &fixture, destroy_work.remove_ids);
        try std.testing.expectEqual(node_total, id_count);
        printOperationScratch(F, flavor, bits, "destroy", transaction.bytes, node_total * @sizeOf(u32) + destruction_notifications * @sizeOf(F.Owner.Destruction.Notice) +
            table_count * @sizeOf(types.TableView));
    }

    var contexts: [table_count]Verifier.Context = undefined;
    for (0..table_count) |table| {
        contexts[table] = .{ .table = table };
        _ = try fixture.ledger.subscribe(
            @intCast(table),
            Verifier.callback,
            @ptrCast(&contexts[table]),
        );
    }

    const fatal_slot = F.Owner.resourceStart(table_count - 1) + tree_rows - 1;
    const saved_attributes = fixture.rows[fatal_slot].attributes;
    var fatal_attributes = saved_attributes;
    fatal_attributes.access_loss_impact = .fatal;
    fixture.rows[fatal_slot].attributes = fatal_attributes;
    try std.testing.expectError(error.Blocked, fixture.ledger.destroy(false, &destroy_work));
    try assertNoCallbacks(Verifier, &contexts);
    const after_block = try audit(F, &fixture);
    try std.testing.expect(std.meta.eql(baseline, after_block));
    fixture.rows[fatal_slot].attributes = saved_attributes;

    try fixture.ledger.destroy(false, &destroy_work);
    try assertCallbacks(Verifier, &contexts);
    try assertDestroyedStorage(F, &fixture);
    std.debug.print(
        "max-owner {s}/{d} destroy committed: notices={d} fixed_offline={d}; no owner/index calls after commit\n",
        .{ @tagName(flavor), bits, destruction_notifications, 128 },
    );
}

fn runMaximumOperations(comptime bits: u16, comptime flavor: fixtures.CandidateFlavor) !void {
    try runTreeClose(bits, flavor);
    try runFullDestroy(bits, flavor);
}

test "original-maximum tree close and whole-owner destroy are atomic for both candidates" {
    inline for (.{ 128, 256 }) |bits| {
        try runMaximumOperations(bits, .c2);
        try runMaximumOperations(bits, .b2);
    }
}
