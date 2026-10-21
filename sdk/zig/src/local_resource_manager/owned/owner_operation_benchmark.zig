//! Sequential benchmark driver for full-owner operations on two explicit
//! resource-manager compositions. Fixture construction is outside every
//! operation timer; this is not a per-row push or loading benchmark.
const std = @import("std");
const builtin = @import("builtin");
const uid_module = @import("../uid.zig");
const memory_resource = @import("../memory_resource.zig");
const c2_index = @import("index.zig");
const b2_index = @import("index_b2.zig");
const notifications = @import("notifications.zig");
const owner_module = @import("owner.zig");
const types = @import("types.zig");
const values = @import("ledger_values").Bound;
const benchmark_affinity = @import("benchmark_affinity.zig");
const benchmark_options = @import("benchmark_options");
const maxima = @import("owner_maxima_tests.zig");

const table_count: usize = 6;
const product_capacities = [_]u32{ 20_480, 4_096, 36_864, 1, 18, 266_240 };
const product_resources: usize = 327_699;
const product_shells: usize = 5;
const product_fixed: usize = 11;
const product_dynamic: usize = product_resources - product_fixed;
const index_buckets: usize = 1_048_576;
const shell_uid_base: u64 = 1_000_000;
const dynamic_uid_base: u64 = 2_000_000;
const direct_fixed_uid_base: u64 = 10_000;
const nested_fixed_uid_base: u64 = 20_000;

const Candidate = maxima.CandidateFlavor;
const CompiledCandidate = enum { both, c2, b2 };
const compiled_candidate: CompiledCandidate = if (std.mem.eql(u8, @tagName(benchmark_options.candidate), "both"))
    .both
else if (std.mem.eql(u8, @tagName(benchmark_options.candidate), "c2"))
    .c2
else if (std.mem.eql(u8, @tagName(benchmark_options.candidate), "b2"))
    .b2
else
    @compileError("benchmark_options.candidate must be both, c2, or b2");
const instrumented_build: bool = if (@hasDecl(benchmark_options, "instrument_work"))
    benchmark_options.instrument_work
else
    false;

const Profile = enum { original_maximum, product_327699 };
const Operation = enum { audit, prepare_repeat, single_remove, max_table_close, full_destroy, small_churn };
const Mode = enum { correctness, work, timing };

const Options = struct {
    candidate: ?Candidate,
    both_candidates: bool,
    uid_bits: u16,
    profile: Profile,
    operation: Operation,
    mode: Mode,
    iterations: usize,
    samples: usize,
    affinity_mask: ?usize,
    placement: ?benchmark_affinity.Placement = null,
};

const AuditWork = struct {
    active_resources: usize,
    active_partitions: usize,
    free_partitions: usize,
    fixed_identity_lookups: usize,
    partition_links: usize,
    interval_events: usize,
    allocation_numbers: usize,
    allocation_sort_comparisons: usize,
    index_home_body: usize,
    index_node_body: usize,
    index_neighbor_payload_loads: usize,
};

const TransactionWork = struct {
    available: bool,
    sampled_prefix_buckets: ?usize = null,
    operation_segments: ?usize = null,
    merged_segments: ?usize = null,
    closure_buckets_enumerated: ?usize = null,
    closure_validation_passes: ?usize = null,
    closure_validation_visits: ?usize = null,
    removal_shift_prefix_visits: ?usize = null,
    simulation_bucket_visits: ?usize = null,
    closure_expansion_rounds: ?usize = null,
    closure_expansion_bucket_visits: ?usize = null,
    closure_rebuild_bucket_visits: ?usize = null,
    publication_version_visits: ?usize = null,
};

const WorkCounters = struct {
    audit: ?AuditWork,
    transaction_prepare: ?TransactionWork,
};

const MemoryReport = struct {
    fixture_metadata_bytes: usize,
    audit_scratch_bytes: usize,
    transaction_scratch_bytes: usize,
    owner_scratch_bytes: usize,
    profile_scratch_bytes: usize,
    operation_input_bytes: usize,
    closure_bucket_capacity: usize,
    profile_scan_buckets: usize,
    driver_stack_bytes: usize,
    sample_storage_bytes: usize,
    peak_payload_bytes: usize,
};

const AffinityReport = struct {
    requested_mask: usize,
    before_process_mask: usize,
    after_process_mask: usize,
    system_mask: usize,
    executing_group: u16,
    executing_processor: u8,
};

const OperationFacts = struct {
    target_table: ?usize = null,
    target_rows: ?usize = null,
    target_shells: ?usize = null,
    index_removals: ?usize = null,
    index_puts: ?usize = null,
    fixed_bindings_retained: ?usize = null,
    fixed_bindings_cleared: ?usize = null,
    notifications: usize,
};

const SampleResult = struct {
    elapsed_ns: u64,
    memory: MemoryReport,
    work_counters: ?WorkCounters,
    operation_facts: OperationFacts,
};

const BenchmarkRecord = struct {
    schema: []const u8 = "resource-manager-owner-operations-v1",
    passed: bool,
    candidate: []const u8,
    uid_bits: u16,
    profile: []const u8,
    operation: []const u8,
    run_mode: []const u8,
    build_mode: []const u8,
    instrumented_build: bool,
    iterations: usize,
    sample_count: usize,
    tables: usize,
    resources: usize,
    partitions: usize,
    index_buckets: usize,
    index_nodes: usize,
    fixed_registrations: usize,
    policy_source: []const u8 = "ledger_values.Bound.table_0",
    table_capacities: []const u32,
    operation_facts: OperationFacts,
    samples_elapsed_ns: []const u64,
    memory: MemoryReport,
    work_counters: ?WorkCounters,
    setup_timed: bool = false,
    timing_boundary: []const u8,
    affinity: ?AffinityReport,
};

fn attributes() types.Attributes {
    return .{
        .recoverability = .recoverable,
        .recovery_cost_coefficient = 1,
        .access_loss_impact = .unobservable_now,
    };
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

fn productTables() [table_count]types.Table {
    var result: [table_count]types.Table = undefined;
    for (product_capacities, 0..) |capacity, table| {
        const partition_capacity: u32 = if (capacity > 1) 1 else 0;
        result[table] = .{
            .capacity = capacity,
            .partition_capacity = partition_capacity,
            .reserved_start = 0,
            .reserved_count = if (capacity > 1) capacity - 1 else 0,
            .attributes = attributes(),
            .policy = policy(),
        };
    }
    return result;
}

fn productFixed(comptime Uid: type) [product_fixed]types.For(Uid).Fixed {
    var result: [product_fixed]types.For(Uid).Fixed = undefined;
    var count: usize = 0;
    var shell_slot: u64 = 0;
    for (product_capacities, 0..) |capacity, table| {
        result[count] = .{
            .table = @intCast(table),
            .parent = null,
            .ordinal = capacity - 1,
            .uid = maxima.key(Uid, direct_fixed_uid_base + @as(u64, @intCast(table))),
            .allocation_bytes = 13,
            .attributes = null,
        };
        count += 1;
        if (capacity > 1) {
            result[count] = .{
                .table = @intCast(table),
                .parent = maxima.key(Uid, shell_uid_base + shell_slot),
                .ordinal = capacity - 2,
                .uid = maxima.key(Uid, nested_fixed_uid_base + @as(u64, @intCast(table))),
                .allocation_bytes = 17,
                .attributes = null,
            };
            count += 1;
            shell_slot += 1;
        }
    }
    return result;
}

fn ProductFixture(comptime bits: u16, comptime flavor: Candidate) type {
    const Uid = uid_module.Identifier(bits);
    const Index = if (flavor == .c2)
        c2_index.Candidate(Uid, .c2_chain)
    else
        b2_index.Candidate(Uid, .b2_ordered);
    const declarations = productTables();
    const fixed = productFixed(Uid);
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
            const subscriber_records = try allocator.alloc(Notifier.Record, Ledger.table_count);
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
            allocator.free(self.audit_allocations);
            allocator.free(self.audit_fixed_marks);
            allocator.free(self.audit_table_views);
            allocator.free(self.audit_stack);
            allocator.free(self.audit_events);
            allocator.free(self.audit_depth);
            allocator.free(self.audit_shell_marks);
            allocator.free(self.audit_shell_queue);
            allocator.free(self.subscriber_records);
            allocator.free(self.homes);
            allocator.free(self.nodes);
            allocator.free(self.bindings);
            allocator.free(self.table_states);
            allocator.free(self.shells);
            allocator.free(self.rows);
        }

        pub fn populateProduct(self: *Self) !void {
            var shell_global: usize = 0;
            for (product_capacities, 0..) |capacity, table| {
                const row_start = Ledger.resourceStart(table);
                const shell_start = Ledger.partitionStart(table);
                if (capacity > 1) {
                    self.table_states[table].first_partition = @intCast(shell_start);
                    self.table_states[table].free_partition = types.no_slot;
                    self.shells[shell_start] = .{
                        .uid = maxima.key(Uid, shell_uid_base + @as(u64, @intCast(shell_global))),
                        .table = @intCast(table),
                        .parent = null,
                        .start = @intCast(row_start),
                        .capacity = capacity - 1,
                        .allocation_number = @intCast(product_dynamic + shell_global + 1),
                        .attributes = attributes(),
                        .history = .{},
                        .first_child = types.no_slot,
                        .next_sibling = types.no_slot,
                    };
                    try self.ledger.index.insert(
                        @intCast(Ledger.resource_capacity + shell_start),
                        self.shells[shell_start].uid.?,
                        .{ .kind = .partition, .table = @intCast(table), .slot = @intCast(shell_start) },
                    );
                    shell_global += 1;
                }

                for (self.rows[row_start..][0..capacity], 0..) |*row, offset| {
                    row.parent = if (capacity > 1 and offset < @as(usize, capacity - 1)) @intCast(shell_start) else null;
                    if (row.fixed_declaration != null or capacity > 1 and offset == @as(usize, capacity - 2)) continue;
                    var resource = try Resource.dynamic(0);
                    resource.generation = @intCast(dynamicNumber(table, offset));
                    resource.registration.?.region = .{ .base = null, .allocation_bytes = 0, .page_bytes = 0 };
                    row.uid = maxima.key(Uid, dynamic_uid_base + @as(u64, @intCast(row_start + offset)));
                    row.resource = resource;
                    try self.ledger.index.insert(
                        @intCast(row_start + offset),
                        row.uid.?,
                        .{ .kind = .resource, .table = @intCast(table), .slot = @intCast(row_start + offset) },
                    );
                }
            }

            for (product_capacities, 0..) |capacity, table| {
                if (capacity == 1) continue;
                const row_slot = Ledger.resourceStart(table) + capacity - 2;
                const shell_slot = Ledger.partitionStart(table);
                try self.ledger.registerFixed(
                    @intCast(nestedFixedIndex(table)),
                    @intCast(row_slot),
                    @intCast(shell_slot),
                );
            }
            self.ledger.numbers.next.store(product_dynamic + product_shells + 1, .release);
        }

        fn dynamicNumber(table: usize, offset: usize) usize {
            var number: usize = 0;
            for (0..table) |previous| number += @as(usize, product_capacities[previous]) - fixedForTable(previous);
            number += offset;
            return number + 1;
        }

        fn fixedForTable(table: usize) usize {
            return if (product_capacities[table] > 1) 2 else 1;
        }

        fn nestedFixedIndex(table: usize) usize {
            var result: usize = 0;
            for (0..table) |previous| result += fixedForTable(previous);
            return result + 1;
        }
    };
}

fn TxStorage(comptime Index: type) type {
    const Transactions = Index.Transactions;
    const Workspace = Transactions.Workspace;
    return struct {
        workspace: Workspace = .{},
        bytes: usize = 0,

        pub fn create(allocator: std.mem.Allocator, removal_count: usize, closure_count: usize) !@This() {
            const operation_count = removal_count;
            var result = @This(){};
            if (@hasField(Workspace, "operations")) {
                const operations = try allocator.alloc(Transactions.Operation, operation_count);
                const segments = try allocator.alloc(Transactions.Segment, operation_count * 2);
                const indices = try allocator.alloc(u32, closure_count);
                const versions = try allocator.alloc(u64, closure_count);
                const removals = try allocator.alloc(Index.RemovePlan, removal_count);
                const inserts = try allocator.alloc(Index.InsertPlan, 0);
                const simulation_nodes = try allocator.alloc(u32, closure_count);
                const simulation_uids = try allocator.alloc(Index.uid_type, closure_count);
                const simulation_node_ids = try allocator.alloc(u32, closure_count);
                const simulation_backlinks = try allocator.alloc(u32, closure_count);
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
                    indices.len * @sizeOf(u32) + versions.len * @sizeOf(u64) +
                    removals.len * @sizeOf(Index.RemovePlan) +
                    inserts.len * @sizeOf(Index.InsertPlan) +
                    simulation_nodes.len * @sizeOf(u32) +
                    simulation_uids.len * @sizeOf(Index.uid_type) +
                    simulation_node_ids.len * @sizeOf(u32) +
                    simulation_backlinks.len * @sizeOf(u32) + @sizeOf(Workspace);
            } else {
                const homes = try allocator.alloc(u32, operation_count);
                const nodes = try allocator.alloc(u32, operation_count);
                const publications = try allocator.alloc(Index.Publication, operation_count);
                const removals = try allocator.alloc(Index.RemovePlan, removal_count);
                const inserts = try allocator.alloc(Index.InsertPlan, 0);
                result.workspace = .{
                    .homes = homes,
                    .nodes = nodes,
                    .publications = publications,
                    .removals = removals,
                    .inserts = inserts,
                };
                result.bytes = homes.len * @sizeOf(u32) + nodes.len * @sizeOf(u32) +
                    publications.len * @sizeOf(Index.Publication) +
                    removals.len * @sizeOf(Index.RemovePlan) +
                    inserts.len * @sizeOf(Index.InsertPlan) + @sizeOf(Workspace);
            }
            return result;
        }
    };
}

const ClosureProfile = struct { buckets: usize = 0, selected: usize = 0 };

fn profileB2(index: anytype, removal_ids: []const u32, marks: []u8) !ClosureProfile {
    if (marks.len < index.nodes.len) return error.WorkspaceTooSmall;
    @memset(marks[0..index.nodes.len], 0);
    for (removal_ids) |node_id| {
        if (@as(usize, node_id) >= index.nodes.len or
            !index.nodes[node_id].linked.load(.seq_cst) or marks[node_id] != 0)
            return error.InvalidRemovalFixture;
        marks[node_id] = 1;
    }
    var empty_anchor: ?usize = null;
    for (index.buckets, 0..) |bucket, i| {
        if (bucket.node_id.load(.seq_cst) == b2_index.no_bucket) {
            empty_anchor = i;
            break;
        }
    }
    const anchor = empty_anchor orelse return error.IndexFull;
    var result = ClosureProfile{};
    var run_length: usize = 0;
    var first_selected: ?usize = null;
    var cursor = if (anchor + 1 == index.buckets.len) 0 else anchor + 1;
    for (0..index.buckets.len) |_| {
        const node_id = index.buckets[cursor].node_id.load(.seq_cst);
        if (node_id == b2_index.no_bucket) {
            if (first_selected) |first| result.buckets += run_length - first + 1;
            run_length = 0;
            first_selected = null;
        } else {
            if (@as(usize, node_id) >= index.nodes.len or
                !index.nodes[node_id].linked.load(.seq_cst) or
                index.nodes[node_id].bucket_index.load(.seq_cst) != cursor)
                return error.InvalidIndexState;
            if (marks[node_id] != 0) {
                result.selected += 1;
                if (first_selected == null) first_selected = run_length;
            }
            run_length += 1;
        }
        cursor = if (cursor + 1 == index.buckets.len) 0 else cursor + 1;
    }
    if (result.selected != removal_ids.len or run_length != 0) return error.InvalidIndexState;
    return result;
}

fn CallbackContext(comptime Uid: type) type {
    return struct {
        count: usize = 0,
        checksum: u64 = 0,
        failed: bool = false,

        pub fn callback(raw: ?*anyopaque, uid: Uid, allocation_number: u64) callconv(.c) void {
            const self: *@This() = @ptrCast(@alignCast(raw orelse return));
            self.count += 1;
            self.checksum +%= std.mem.readInt(u64, uid.bytes[uid.bytes.len - 8 ..][0..8], .big) ^ allocation_number;
        }
    };
}

fn parseOptions(args: []const [:0]const u8) !Options {
    var candidate: ?Candidate = null;
    var both = false;
    var uid_bits: ?u16 = null;
    var profile: ?Profile = null;
    var operation: ?Operation = null;
    var mode: ?Mode = null;
    var iterations: ?usize = null;
    var samples: ?usize = null;
    var affinity_mask: ?usize = null;
    var i: usize = 1;
    while (i < args.len) {
        const name = args[i];
        i += 1;
        if (i >= args.len) return error.MissingArgumentValue;
        const value = args[i];
        i += 1;
        if (std.mem.eql(u8, name, "--candidate")) {
            if (std.mem.eql(u8, value, "both")) {
                both = true;
            } else if (std.mem.eql(u8, value, "c2")) {
                candidate = .c2;
            } else if (std.mem.eql(u8, value, "b2")) {
                candidate = .b2;
            } else return error.InvalidCandidate;
        } else if (std.mem.eql(u8, name, "--uid-bits")) {
            uid_bits = try std.fmt.parseInt(u16, value, 10);
        } else if (std.mem.eql(u8, name, "--profile")) {
            if (std.mem.eql(u8, value, "original-maximum")) profile = .original_maximum else if (std.mem.eql(u8, value, "product-327699")) profile = .product_327699 else return error.InvalidProfile;
        } else if (std.mem.eql(u8, name, "--operation")) {
            if (std.mem.eql(u8, value, "audit")) operation = .audit else if (std.mem.eql(u8, value, "prepare-repeat")) operation = .prepare_repeat else if (std.mem.eql(u8, value, "single-remove")) operation = .single_remove else if (std.mem.eql(u8, value, "max-table-close")) operation = .max_table_close else if (std.mem.eql(u8, value, "full-destroy")) operation = .full_destroy else if (std.mem.eql(u8, value, "small-churn")) operation = .small_churn else return error.InvalidOperation;
        } else if (std.mem.eql(u8, name, "--mode")) {
            if (std.mem.eql(u8, value, "correctness")) mode = .correctness else if (std.mem.eql(u8, value, "work")) mode = .work else if (std.mem.eql(u8, value, "timing")) mode = .timing else return error.InvalidMode;
        } else if (std.mem.eql(u8, name, "--iterations")) {
            iterations = try std.fmt.parseInt(usize, value, 10);
        } else if (std.mem.eql(u8, name, "--samples")) {
            samples = try std.fmt.parseInt(usize, value, 10);
        } else if (std.mem.eql(u8, name, "--affinity-mask")) {
            affinity_mask = try std.fmt.parseInt(usize, value, 0);
        } else return error.UnknownArgument;
    }
    const selected_mode = mode orelse return error.MissingMode;
    const selected_candidate = candidate;
    if (both == (selected_candidate != null)) return error.InvalidCandidate;
    const selected_samples = if (samples) |count|
        count
    else if (selected_mode == .timing)
        return error.MissingSamples
    else
        1;
    const result = Options{
        .candidate = selected_candidate,
        .both_candidates = both,
        .uid_bits = uid_bits orelse return error.MissingUidWidth,
        .profile = profile orelse return error.MissingProfile,
        .operation = operation orelse return error.MissingOperation,
        .mode = selected_mode,
        .iterations = iterations orelse return error.MissingIterations,
        .samples = selected_samples,
        .affinity_mask = affinity_mask,
    };
    if (result.uid_bits != 128 and result.uid_bits != 256) return error.InvalidUidWidth;
    if (result.iterations == 0 or result.samples == 0) return error.InvalidIterationCount;
    if (result.affinity_mask) |mask| {
        if (mask == 0 or mask & (mask - 1) != 0) return error.AffinityRequiresOneProcessor;
    }
    if (result.mode == .timing and result.affinity_mask == null) return error.TimingRequiresExplicitAffinity;
    if (result.mode == .timing and result.both_candidates) return error.TimingRequiresSingleCandidateBuild;
    if (result.operation == .single_remove or result.operation == .max_table_close or result.operation == .full_destroy) {
        if (result.iterations != 1) return error.OneShotOperationRequiresOneIteration;
    }
    return result;
}

fn auditWorkspace(comptime F: type, fixture: *F) F.Owner.Audit.Workspace {
    return .{
        .shell_queue = fixture.audit_shell_queue,
        .shell_marks = fixture.audit_shell_marks,
        .depth = fixture.audit_depth,
        .events = fixture.audit_events,
        .stack = fixture.audit_stack,
        .table_views = fixture.audit_table_views,
        .fixed_marks = fixture.audit_fixed_marks,
        .allocations = fixture.audit_allocations,
    };
}

fn verifyStats(comptime F: type, stats: anytype, expected_resources: usize, expected_shells: usize) !void {
    if (stats.resources != F.Owner.resource_capacity or stats.partitions != F.Owner.partition_capacity or
        stats.fixed_declarations != F.Owner.fixed_declarations.len or stats.tables != F.Owner.table_count or
        stats.active_resources != expected_resources or stats.active_partitions != expected_shells)
        return error.OwnerAuditMismatch;
}

fn printStats(candidate: Candidate, bits: u16, profile: Profile, stats: anytype) void {
    std.debug.print(
        "owner-operation work candidate={s} uid={d} profile={s} resources={d}/{d} partitions={d}/{d} free={d} fixed={d} links={d} events={d} allocations={d} registered_bytes={d} pages={d} index_body={d}+{d} neighbor_payload_loads={d}\n",
        .{
            @tagName(candidate),    bits,                               @tagName(profile),
            stats.active_resources, stats.resources,                    stats.active_partitions,
            stats.partitions,       stats.free_partitions,              stats.fixed_rows,
            stats.partition_links,  stats.interval_events,              stats.allocation_numbers,
            stats.registered_bytes, stats.system_page_bytes,            stats.index_home_body,
            stats.index_node_body,  stats.index_neighbor_payload_loads,
        },
    );
}

fn fillAllIds(comptime F: type, fixture: *F, output: []u32) !usize {
    if (output.len < F.Owner.resource_capacity + F.Owner.partition_capacity) return error.WorkspaceTooSmall;
    var count: usize = 0;
    for (fixture.rows, 0..) |row, slot| {
        if (row.uid != null) {
            output[count] = @intCast(slot);
            count += 1;
        }
    }
    for (fixture.shells, 0..) |shell, slot| {
        if (shell.uid != null) {
            output[count] = @intCast(F.Owner.resource_capacity + slot);
            count += 1;
        }
    }
    return count;
}

const TreeIds = struct { count: usize, rows: usize, shells: usize, root: u32 };

fn fillTreeIds(comptime F: type, fixture: *F, table: usize, output: []u32) !TreeIds {
    const shell_start = F.Owner.partitionStart(table);
    if (shell_start >= fixture.shells.len or fixture.shells[shell_start].uid == null) return error.MissingTargetRoot;
    const root = @as(u32, @intCast(shell_start));
    const shell = fixture.shells[shell_start];
    const row_start: usize = shell.start;
    const row_end = row_start + shell.capacity;
    if (output.len < (row_end - row_start) + F.Owner.table_declarations[table].partition_capacity) return error.WorkspaceTooSmall;
    var count: usize = 0;
    for (row_start..row_end) |slot| {
        if (fixture.rows[slot].uid == null) return error.InvalidFixture;
        output[count] = @intCast(slot);
        count += 1;
    }
    const rows_count = count;
    const table_shell_start = F.Owner.partitionStart(table);
    for (0..F.Owner.table_declarations[table].partition_capacity) |offset| {
        const slot = table_shell_start + offset;
        if (fixture.shells[slot].uid == null) return error.InvalidFixture;
        output[count] = @intCast(F.Owner.resource_capacity + slot);
        count += 1;
    }
    return .{ .count = count, .rows = rows_count, .shells = count - rows_count, .root = root };
}

const ClosureStorage = struct { count: usize, bytes: usize };

fn profileTransaction(comptime Index: type, index: *Index, ids: []const u32, allocator: std.mem.Allocator) !ClosureStorage {
    if (@hasField(Index.Transactions.Workspace, "operations")) {
        const marks = try allocator.alloc(u8, index.nodes.len);
        const result = try profileB2(index, ids, marks);
        return .{ .count = result.buckets, .bytes = marks.len };
    }
    return .{ .count = 0, .bytes = 0 };
}

fn preflight(comptime Index: type, index: *Index, ids: []const u32, storage: anytype, comptime candidate: Candidate, bits: u16, profile: Profile) !TransactionWork {
    var mutable_storage = storage;
    if (candidate == .b2) {
        var stats = Index.Transactions.PrepareStats{};
        var prepared = try Index.Transactions.prepareWithStats(index, ids, &.{}, &mutable_storage.workspace, &stats);
        const enumerated = prepared.indices.len;
        prepared.abort();
        if (enumerated != stats.closure_buckets_enumerated) return error.IndexWorkCounterMismatch;
        std.debug.print(
            "owner-operation transaction-work candidate=b2 uid={d} profile={s} selected={d} operation_segments={d} merged_segments={d} closure={d} validation={d}/{d} shift={d} simulation={d} expansion={d}/{d}/{d} publication_versions={d}\n",
            .{
                bits,                             @tagName(profile),                     ids.len,                             stats.operation_segments,          stats.merged_segments,
                stats.closure_buckets_enumerated, stats.closure_validation_passes,       stats.closure_validation_visits,     stats.removal_shift_prefix_visits, stats.simulation_bucket_visits,
                stats.closure_expansion_rounds,   stats.closure_expansion_bucket_visits, stats.closure_rebuild_bucket_visits, stats.publication_version_visits,
            },
        );
        return .{
            .available = true,
            .sampled_prefix_buckets = stats.sampled_prefix_buckets,
            .operation_segments = stats.operation_segments,
            .merged_segments = stats.merged_segments,
            .closure_buckets_enumerated = stats.closure_buckets_enumerated,
            .closure_validation_passes = stats.closure_validation_passes,
            .closure_validation_visits = stats.closure_validation_visits,
            .removal_shift_prefix_visits = stats.removal_shift_prefix_visits,
            .simulation_bucket_visits = stats.simulation_bucket_visits,
            .closure_expansion_rounds = stats.closure_expansion_rounds,
            .closure_expansion_bucket_visits = stats.closure_expansion_bucket_visits,
            .closure_rebuild_bucket_visits = stats.closure_rebuild_bucket_visits,
            .publication_version_visits = stats.publication_version_visits,
        };
    } else {
        var prepared = try Index.Transactions.prepare(index, ids, &.{}, &mutable_storage.workspace);
        prepared.abort();
        std.debug.print("owner-operation transaction-work candidate=c2 uid={d} profile={s} selected={d} counters=not_exposed\n", .{ bits, @tagName(profile), ids.len });
        return .{ .available = false };
    }
}

fn auditWork(stats: anytype) AuditWork {
    return .{
        .active_resources = stats.active_resources,
        .active_partitions = stats.active_partitions,
        .free_partitions = stats.free_partitions,
        .fixed_identity_lookups = stats.fixed_identity_lookups,
        .partition_links = stats.partition_links,
        .interval_events = stats.interval_events,
        .allocation_numbers = stats.allocation_numbers,
        .allocation_sort_comparisons = stats.allocation_sort_comparisons,
        .index_home_body = stats.index_home_body,
        .index_node_body = stats.index_node_body,
        .index_neighbor_payload_loads = stats.index_neighbor_payload_loads,
    };
}

fn writeJson(io: std.Io, allocator: std.mem.Allocator, record: anytype) !void {
    const output = try std.json.Stringify.valueAlloc(allocator, record, .{});
    defer allocator.free(output);
    var stdout_buffer: [4096]u8 = undefined;
    var stdout_file_writer = std.Io.File.stdout().writer(io, &stdout_buffer);
    const stdout = &stdout_file_writer.interface;
    try stdout.writeAll(output);
    try stdout.writeAll("\n");
    try stdout.flush();
}

fn profileName(profile: Profile) []const u8 {
    return switch (profile) {
        .original_maximum => "original-maximum",
        .product_327699 => "product-327699",
    };
}

fn operationName(operation: Operation) []const u8 {
    return switch (operation) {
        .audit => "audit",
        .prepare_repeat => "prepare-repeat",
        .single_remove => "single-remove",
        .max_table_close => "max-table-close",
        .full_destroy => "full-destroy",
        .small_churn => "small-churn",
    };
}

fn fixtureMetadataBytes(comptime F: type, fixture: *F) usize {
    return @sizeOf(F) +
        fixture.rows.len * @sizeOf(F.Owner.Row) +
        fixture.shells.len * @sizeOf(F.Owner.Shell) +
        fixture.table_states.len * @sizeOf(F.Owner.TableState) +
        fixture.bindings.len * @sizeOf(F.Owner.FixedBinding) +
        fixture.nodes.len * @sizeOf(F.IndexType.Node) +
        fixture.homes.len * @sizeOf(F.IndexType.Home) +
        fixture.subscriber_records.len * @sizeOf(@TypeOf(fixture.subscriber_records[0]));
}

fn auditScratchBytes(comptime F: type, fixture: *F) usize {
    return fixture.audit_shell_queue.len * @sizeOf(u32) +
        fixture.audit_shell_marks.len * @sizeOf(u8) +
        fixture.audit_depth.len * @sizeOf(u32) +
        fixture.audit_events.len * @sizeOf(F.Owner.Audit.Event) +
        fixture.audit_stack.len * @sizeOf(u32) +
        fixture.audit_table_views.len * @sizeOf(types.TableView) +
        fixture.audit_fixed_marks.len * @sizeOf(u8) +
        fixture.audit_allocations.len * @sizeOf(u64);
}

fn OwnerWork(comptime F: type) type {
    return struct { work: F.Owner.Workspace, bytes: usize };
}

fn DestroyWork(comptime F: type) type {
    return struct { work: F.Owner.Destruction.Workspace, bytes: usize };
}

fn treeCloseWorkspace(comptime F: type, allocator: std.mem.Allocator, row_count: usize, shell_count: usize, note_count: usize, removal_count: usize, index_workspace: anytype) !OwnerWork(F) {
    const rows = try allocator.alloc(F.Owner.Candidate, row_count);
    const shells = try allocator.alloc(u32, shell_count);
    const homes = try allocator.alloc(u32, 1);
    const notes = try allocator.alloc(F.Owner.api.Notification, note_count);
    const remove_ids = try allocator.alloc(u32, removal_count);
    return .{
        .work = .{
            .candidates = &.{},
            .rows = rows,
            .shells = shells,
            .homes = homes,
            .notes = notes,
            .remove_ids = remove_ids,
            .puts = &.{},
            .index_workspace = index_workspace,
        },
        .bytes = rows.len * @sizeOf(F.Owner.Candidate) + shells.len * @sizeOf(u32) +
            homes.len * @sizeOf(u32) + notes.len * @sizeOf(F.Owner.api.Notification) +
            remove_ids.len * @sizeOf(u32) + @sizeOf(F.Owner.Workspace),
    };
}

fn directCloseWorkspace(comptime F: type, allocator: std.mem.Allocator) !OwnerWork(F) {
    const notes = try allocator.alloc(F.Owner.api.Notification, 1);
    return .{
        .work = .{
            .candidates = &.{},
            .rows = &.{},
            .shells = &.{},
            .homes = &.{},
            .notes = notes,
        },
        .bytes = notes.len * @sizeOf(F.Owner.api.Notification) + @sizeOf(F.Owner.Workspace),
    };
}

fn destructionWorkspace(comptime F: type, allocator: std.mem.Allocator, remove_ids: []u32, notice_count: usize, index_workspace: anytype) !DestroyWork(F) {
    const notices = try allocator.alloc(F.Owner.Destruction.Notice, notice_count);
    const table_views = try allocator.alloc(types.TableView, F.Owner.table_count);
    return .{
        .work = .{
            .remove_ids = remove_ids,
            .notices = notices,
            .table_views = table_views,
            .index_workspace = index_workspace,
        },
        .bytes = remove_ids.len * @sizeOf(u32) + notices.len * @sizeOf(F.Owner.Destruction.Notice) +
            table_views.len * @sizeOf(types.TableView) + @sizeOf(F.Owner.Destruction.Workspace),
    };
}

fn subscribeTable(comptime F: type, fixture: *F, contexts: []CallbackContext(F.UidType), subscriptions: []?F.Owner.Subscription, table: usize) !void {
    subscriptions[table] = try fixture.ledger.subscribe(
        @intCast(table),
        CallbackContext(F.UidType).callback,
        @ptrCast(&contexts[table]),
    );
}

fn unsubscribeAll(comptime F: type, fixture: *F, subscriptions: []?F.Owner.Subscription) !void {
    for (subscriptions) |subscription| {
        if (subscription) |value| try fixture.ledger.unsubscribe(value);
    }
}

fn checkAllCallbacks(comptime F: type, contexts: []const CallbackContext(F.UidType), subscriptions: []const ?F.Owner.Subscription, expected_total: usize) !void {
    var total: usize = 0;
    for (contexts, subscriptions) |context, subscription| {
        if (context.failed) return error.NotificationMismatch;
        if (subscription != null) total += context.count;
    }
    if (total != expected_total) return error.NotificationCountMismatch;
}

fn checkDestroyed(comptime F: type, fixture: *F) !void {
    for (fixture.rows) |row| {
        if (row.uid != null or row.resource.registration != null or row.fixed_declaration != null or
            row.resource.use_count != 0 or row.resource.loading) return error.DestroyDidNotClearRows;
    }
    for (fixture.shells) |shell| {
        if (shell.uid != null or shell.capacity != 0) return error.DestroyDidNotClearShells;
    }
    for (0..fixture.ledger.index.nodes.len) |node| {
        if (try fixture.ledger.index.entryAt(@intCast(node)) != null) return error.DestroyLeftIndexEntry;
    }
    for (fixture.bindings) |binding| {
        if (binding.slot.load(.acquire) != types.no_slot) return error.DestroyLeftFixedBinding;
    }
}

fn checkFixedAfterTreeClose(comptime F: type, fixture: *F, table: usize) !void {
    for (F.Owner.fixed_declarations, 0..) |declaration, i| {
        if (declaration.table != table) continue;
        const expected_slot = if (declaration.parent != null)
            types.no_slot
        else
            @as(u32, @intCast(F.Owner.resourceStart(table) + declaration.ordinal));
        if (fixture.bindings[i].slot.load(.acquire) != expected_slot) return error.FixedBindingAfterCloseMismatch;
    }
}

fn auditCurrent(comptime F: type, fixture: *F) !F.Owner.Audit.Stats {
    var work = auditWorkspace(F, fixture);
    return fixture.ledger.audit(&work);
}

fn printProfile(comptime F: type, candidate: Candidate, bits: u16, comptime profile: Profile, operation: Operation, mode: Mode, iterations: usize, samples: usize, affinity: ?benchmark_affinity.Placement, metadata_bytes: usize, audit_bytes: usize, transaction_bytes: usize, owner_bytes: usize, profile_bytes: usize, input_bytes: usize, closure_capacity: usize, profile_scan_buckets: usize) void {
    const driver_stack_bytes = @sizeOf([F.Owner.table_count]CallbackContext(F.UidType)) +
        @sizeOf([F.Owner.table_count]?F.Owner.Subscription);
    const sample_storage_bytes = if (mode == .timing) samples * @sizeOf(u64) else 0;
    const peak_payload_bytes = metadata_bytes + audit_bytes + transaction_bytes + owner_bytes +
        profile_bytes + input_bytes + driver_stack_bytes + sample_storage_bytes;
    std.debug.print(
        "owner-operation config candidate={s} uid_bits={d} profile={s} operation={s} mode={s} build={s} iterations={d} samples={d} tables={d} resources={d} shells={d} nodes={d} buckets={d} fixed={d} policy=ledger_values.Bound.table_0 fixture_metadata_bytes={d} audit_scratch_bytes={d} transaction_scratch_bytes={d} owner_scratch_bytes={d} profile_scratch_bytes={d} operation_input_bytes={d} closure_bucket_capacity={d} profile_scan_buckets={d} driver_stack_bytes={d} sample_storage_bytes={d} peak_payload_bytes={d} setup_timed=false",
        .{
            @tagName(candidate), bits,                           @tagName(profile),   @tagName(operation),       @tagName(mode),             @tagName(builtin.mode),
            iterations,          samples,                        F.Owner.table_count, F.Owner.resource_capacity, F.Owner.partition_capacity, F.Owner.resource_capacity + F.Owner.partition_capacity,
            index_buckets,       F.Owner.fixed_declarations.len, metadata_bytes,      audit_bytes,               transaction_bytes,          owner_bytes,
            profile_bytes,       input_bytes,                    closure_capacity,    profile_scan_buckets,      driver_stack_bytes,         sample_storage_bytes,
            peak_payload_bytes,
        },
    );
    if (comptime profile == .original_maximum) {
        std.debug.print(" shape=64x8192_rows+65536_shells zero_byte_api_push_in_setup=true", .{});
    } else {
        std.debug.print(" shape=20,480/4,096/36,864/1/18/266,240 fixed=11 dynamic=327,688 shell_layout=one_root_per_table_capacity_gt_1 top_fixed_last_ordinal_nested_fixed_penultimate api_push_in_setup=false", .{});
    }
    if (affinity) |placement| {
        std.debug.print(
            " affinity=requested:{x},before:{x},after:{x},system:{x},group:{d},processor:{d}\n",
            .{ placement.requested_mask, placement.before_process_mask, placement.after_process_mask, placement.system_mask, placement.executing_group, placement.executing_processor },
        );
    } else {
        std.debug.print(" affinity=not_requested\n", .{});
    }
    std.debug.print("owner-operation table-capacities=[", .{});
    for (F.Owner.table_declarations, 0..) |declaration, i| {
        if (i != 0) std.debug.print(",", .{});
        std.debug.print("{d}", .{declaration.capacity});
    }
    std.debug.print("]\n", .{});
    const boundary: []const u8 = switch (operation) {
        .audit => "Owner.audit with caller audit scratch",
        .prepare_repeat => "Index.Transactions.prepare plus abort for the full removal list",
        .single_remove => "Owner.close on one preselected resident resource UID",
        .max_table_close => "Owner.close on the selected root subtree, including commit and notification dispatch; a direct top-level fixed binding remains registered",
        .full_destroy => "Owner.destroy(false), including commit, payload release, and notification dispatch",
        .small_churn => "Owner.close, Owner.push, and Owner.endUse(.complete) with result checks",
    };
    std.debug.print("owner-operation timing-boundary={s} fixture-construction=outside\n", .{boundary});
}

fn runSample(comptime F: type, comptime profile: Profile, comptime candidate: Candidate, bits: u16, options: Options, sample_index: usize, io: std.Io) !SampleResult {
    var arena = std.heap.ArenaAllocator.init(std.heap.page_allocator);
    defer arena.deinit();
    const allocator = arena.allocator();
    var fixture = try F.create(allocator);
    defer fixture.deinit(allocator);
    if (comptime profile == .original_maximum) {
        try fixture.populateMaximum();
    } else {
        try fixture.populateProduct();
    }

    const expected_resource_count = F.Owner.resource_capacity;
    const expected_shell_count = F.Owner.partition_capacity;
    var before: ?F.Owner.Audit.Stats = null;
    if (options.mode != .timing and options.operation != .audit) {
        const stats = try auditCurrent(F, &fixture);
        try verifyStats(F, stats, expected_resource_count, expected_shell_count);
        before = stats;
        printStats(candidate, bits, profile, stats);
    }

    const target_table: usize = if (profile == .original_maximum) 0 else 5;
    var contexts: [F.Owner.table_count]CallbackContext(F.UidType) = @splat(.{});
    var subscriptions: [F.Owner.table_count]?F.Owner.Subscription = @splat(null);
    if (options.operation == .single_remove or options.operation == .max_table_close or options.operation == .full_destroy or options.operation == .small_churn) {
        if (options.operation == .full_destroy) {
            for (0..F.Owner.table_count) |table| try subscribeTable(F, &fixture, &contexts, &subscriptions, table);
        } else {
            const subscription_table = if (options.operation == .max_table_close) target_table else 0;
            try subscribeTable(F, &fixture, &contexts, &subscriptions, subscription_table);
        }
    }
    defer unsubscribeAll(F, &fixture, &subscriptions) catch {};

    var ids: []u32 = &.{};
    var id_count: usize = 0;
    var tree: ?TreeIds = null;
    var transaction_count: usize = 0;
    var profile_scratch_bytes: usize = 0;
    var transaction_work: ?TransactionWork = null;
    if (options.operation == .prepare_repeat or options.operation == .full_destroy) {
        ids = try allocator.alloc(u32, F.Owner.resource_capacity + F.Owner.partition_capacity);
        id_count = try fillAllIds(F, &fixture, ids);
    } else if (options.operation == .max_table_close) {
        ids = try allocator.alloc(u32, F.Owner.resource_capacity + F.Owner.partition_capacity);
        tree = try fillTreeIds(F, &fixture, target_table, ids);
        id_count = tree.?.count;
    } else if (options.operation == .single_remove) {
        ids = try allocator.alloc(u32, 1);
        const row_slot = F.Owner.resourceStart(0);
        if (fixture.rows[row_slot].uid == null) return error.InvalidFixture;
        ids[0] = @intCast(row_slot);
        id_count = 1;
    }

    var closure_profile: ?ClosureStorage = null;
    if (candidate == .b2 and id_count != 0 and
        (options.operation == .prepare_repeat or options.operation == .max_table_close or options.operation == .full_destroy))
    {
        closure_profile = try profileTransaction(F.IndexType, &fixture.ledger.index, ids[0..id_count], allocator);
        transaction_count = closure_profile.?.count;
        profile_scratch_bytes = closure_profile.?.bytes;
    }
    var transactions = TxStorage(F.IndexType){};
    if (options.operation == .prepare_repeat or options.operation == .max_table_close or options.operation == .full_destroy) {
        transactions = try TxStorage(F.IndexType).create(allocator, id_count, transaction_count);
    }
    const transaction_storage_bytes = transactions.bytes;

    var owner_scratch_bytes: usize = 0;
    var close_work: ?OwnerWork(F) = null;
    var direct_work: ?OwnerWork(F) = null;
    var destroy_work: ?DestroyWork(F) = null;
    if (options.operation == .max_table_close) {
        const tree_info = tree.?;
        const resident_dynamic = tree_info.rows - 1;
        const notes = resident_dynamic + tree_info.shells;
        close_work = try treeCloseWorkspace(
            F,
            allocator,
            tree_info.rows,
            F.Owner.table_declarations[target_table].partition_capacity,
            notes,
            tree_info.count,
            transactions.workspace,
        );
        owner_scratch_bytes = close_work.?.bytes;
    } else if (options.operation == .single_remove or options.operation == .small_churn) {
        direct_work = try directCloseWorkspace(F, allocator);
        owner_scratch_bytes = direct_work.?.bytes;
    } else if (options.operation == .full_destroy) {
        const resident_count = F.Owner.resource_capacity - F.Owner.fixed_declarations.len + F.Owner.partition_capacity;
        destroy_work = try destructionWorkspace(
            F,
            allocator,
            ids,
            resident_count,
            transactions.workspace,
        );
        owner_scratch_bytes = destroy_work.?.bytes;
        ids = destroy_work.?.work.remove_ids;
        id_count = try fillAllIds(F, &fixture, ids);
    }

    const metadata_bytes = fixtureMetadataBytes(F, &fixture);
    const audit_bytes = auditScratchBytes(F, &fixture);
    printProfile(
        F,
        candidate,
        bits,
        profile,
        options.operation,
        options.mode,
        options.iterations,
        options.samples,
        options.placement,
        metadata_bytes,
        audit_bytes,
        transaction_storage_bytes,
        owner_scratch_bytes,
        profile_scratch_bytes,
        if (options.operation == .full_destroy) 0 else ids.len * @sizeOf(u32),
        transaction_count,
        if (candidate == .b2 and transaction_count != 0) index_buckets else 0,
    );
    const driver_stack_bytes = @sizeOf([F.Owner.table_count]CallbackContext(F.UidType)) +
        @sizeOf([F.Owner.table_count]?F.Owner.Subscription);
    const sample_storage_bytes = if (options.mode == .timing) options.samples * @sizeOf(u64) else 0;
    const memory_report = MemoryReport{
        .fixture_metadata_bytes = metadata_bytes,
        .audit_scratch_bytes = audit_bytes,
        .transaction_scratch_bytes = transaction_storage_bytes,
        .owner_scratch_bytes = owner_scratch_bytes,
        .profile_scratch_bytes = profile_scratch_bytes,
        .operation_input_bytes = if (options.operation == .full_destroy) 0 else ids.len * @sizeOf(u32),
        .closure_bucket_capacity = transaction_count,
        .profile_scan_buckets = if (candidate == .b2 and transaction_count != 0) index_buckets else 0,
        .driver_stack_bytes = driver_stack_bytes,
        .sample_storage_bytes = sample_storage_bytes,
        .peak_payload_bytes = metadata_bytes + audit_bytes + transaction_storage_bytes + owner_scratch_bytes +
            profile_scratch_bytes + (if (options.operation == .full_destroy) @as(usize, 0) else ids.len * @sizeOf(u32)) +
            driver_stack_bytes + sample_storage_bytes,
    };
    if (options.operation == .max_table_close) {
        std.debug.print(
            "owner-operation target table={d} rows={d} shells={d} index_removals={d} resident_notifications={d}\n",
            .{ target_table, tree.?.rows, tree.?.shells, tree.?.count, close_work.?.work.notes.len },
        );
    } else if (options.operation == .full_destroy) {
        std.debug.print("owner-operation target full-owner index_removals={d} resident_notifications={d}\n", .{ id_count, destroy_work.?.work.notices.len });
    }

    if (comptime instrumented_build) {
        if (options.mode == .work and (options.operation == .prepare_repeat or options.operation == .max_table_close or options.operation == .full_destroy)) {
            transaction_work = try preflight(F.IndexType, &fixture.ledger.index, ids[0..id_count], transactions, candidate, bits, profile);
        }
    }

    const single_uid: ?F.UidType = if (options.operation == .single_remove)
        fixture.rows[ids[0]].uid orelse return error.InvalidFixture
    else
        null;
    const tree_root_uid: ?F.UidType = if (options.operation == .max_table_close)
        fixture.shells[tree.?.root].uid orelse return error.MissingTargetRoot
    else
        null;
    const churn_row_slot: ?u32 = if (options.operation == .small_churn)
        @intCast(F.Owner.resourceStart(0))
    else
        null;
    const churn_uid: ?F.UidType = if (churn_row_slot) |slot|
        fixture.rows[slot].uid orelse return error.InvalidFixture
    else
        null;
    const churn_parent_uid: ?F.UidType = if (churn_row_slot) |slot| blk: {
        const parent_slot = fixture.rows[slot].parent orelse return error.MissingChurnParent;
        break :blk fixture.shells[parent_slot].uid orelse return error.MissingChurnParent;
    } else null;

    var operation_stats: ?F.Owner.Audit.Stats = null;
    const start_time = if (options.mode == .timing) std.Io.Clock.awake.now(io).nanoseconds else 0;
    switch (options.operation) {
        .audit => {
            var iteration: usize = 0;
            while (iteration < options.iterations) : (iteration += 1) {
                const stats = try auditCurrent(F, &fixture);
                try verifyStats(F, stats, expected_resource_count, expected_shell_count);
                operation_stats = stats;
            }
        },
        .prepare_repeat => {
            var iteration: usize = 0;
            while (iteration < options.iterations) : (iteration += 1) {
                var prepared = try F.IndexType.Transactions.prepare(
                    &fixture.ledger.index,
                    ids[0..id_count],
                    &.{},
                    &transactions.workspace,
                );
                prepared.abort();
            }
        },
        .single_remove => {
            try fixture.ledger.close(single_uid.?, &direct_work.?.work);
        },
        .max_table_close => {
            try fixture.ledger.close(tree_root_uid.?, &close_work.?.work);
        },
        .full_destroy => {
            try fixture.ledger.destroy(false, &destroy_work.?.work);
        },
        .small_churn => {
            var iteration: usize = 0;
            while (iteration < options.iterations) : (iteration += 1) {
                try fixture.ledger.close(churn_uid.?, &direct_work.?.work);
                const borrow = switch (try fixture.ledger.push(.{ .partition = churn_parent_uid.? }, churn_uid.?, 0, &direct_work.?.work)) {
                    .newly_allocated => |value| value,
                    .in_memory, .loading => return error.ChurnPushDidNotAllocate,
                };
                if (borrow.lease.slot != churn_row_slot.?) return error.ChurnSlotChanged;
                if (try fixture.ledger.endUse(borrow.lease, .complete) != .completed) return error.ChurnLeaseDidNotEnd;
            }
        },
    }
    const elapsed = if (options.mode == .timing) elapsedNs(start_time, std.Io.Clock.awake.now(io).nanoseconds) else 0;
    if (options.operation == .max_table_close) try checkFixedAfterTreeClose(F, &fixture, target_table);

    const expected_callbacks: usize = switch (options.operation) {
        .single_remove => 1,
        .max_table_close => tree.?.rows - 1 + tree.?.shells,
        .full_destroy => F.Owner.resource_capacity - F.Owner.fixed_declarations.len + F.Owner.partition_capacity,
        .small_churn => options.iterations,
        .audit, .prepare_repeat => 0,
    };
    try checkAllCallbacks(F, &contexts, &subscriptions, expected_callbacks);
    if (options.operation == .full_destroy) {
        try checkDestroyed(F, &fixture);
    } else if (options.mode != .timing and options.operation == .audit) {
        if (operation_stats) |stats| printStats(candidate, bits, profile, stats);
    } else if (options.mode != .timing) {
        const after_resources = switch (options.operation) {
            .single_remove => expected_resource_count - 1,
            .max_table_close => expected_resource_count - tree.?.rows,
            else => expected_resource_count,
        };
        const after_shells = if (options.operation == .max_table_close)
            expected_shell_count - tree.?.shells
        else
            expected_shell_count;
        const after = try auditCurrent(F, &fixture);
        try verifyStats(F, after, after_resources, after_shells);
        if (options.operation == .prepare_repeat or options.operation == .audit) {
            if (before) |baseline| {
                if (baseline.active_resources != after.active_resources or
                    baseline.active_partitions != after.active_partitions or
                    baseline.allocation_numbers != after.allocation_numbers or
                    baseline.registered_bytes != after.registered_bytes)
                    return error.ReadOnlyOperationMutatedOwner;
            }
        }
        printStats(candidate, bits, profile, after);
    }
    if (options.mode == .timing) {
        std.debug.print(
            "owner-operation raw-sample index={d} elapsed_ns={d} operation_count={d} elapsed_ns_per_operation={d} callbacks={d} callback_checksum={x}\n",
            .{
                sample_index,
                elapsed,
                options.iterations,
                if (options.iterations == 0) 0 else elapsed / options.iterations,
                expected_callbacks,
                callbackChecksum(&contexts),
            },
        );
    } else {
        std.debug.print(
            "owner-operation run mode={s} operation_count={d} callbacks={d} callback_checksum={x} verified=true\n",
            .{ @tagName(options.mode), options.iterations, expected_callbacks, callbackChecksum(&contexts) },
        );
    }
    var work_counters: ?WorkCounters = null;
    if (comptime instrumented_build) {
        if (options.mode == .work) {
            const audit_stats = if (before) |stats|
                stats
            else if (operation_stats) |stats|
                stats
            else
                null;
            work_counters = .{
                .audit = if (audit_stats) |stats| auditWork(stats) else null,
                .transaction_prepare = transaction_work,
            };
        }
    }
    const operation_facts = OperationFacts{
        .target_table = switch (options.operation) {
            .single_remove => 0,
            .max_table_close => target_table,
            else => null,
        },
        .target_rows = if (options.operation == .max_table_close) tree.?.rows else null,
        .target_shells = if (options.operation == .max_table_close) tree.?.shells else null,
        .index_removals = switch (options.operation) {
            .prepare_repeat, .max_table_close, .full_destroy => id_count,
            .single_remove => 1,
            .small_churn => options.iterations,
            .audit => null,
        },
        .index_puts = if (options.operation == .small_churn) options.iterations else null,
        .fixed_bindings_retained = if (options.operation == .max_table_close)
            countFixedBindings(F, target_table, false)
        else
            null,
        .fixed_bindings_cleared = if (options.operation == .max_table_close)
            countFixedBindings(F, target_table, true)
        else
            null,
        .notifications = expected_callbacks,
    };
    return .{
        .elapsed_ns = elapsed,
        .memory = memory_report,
        .work_counters = work_counters,
        .operation_facts = operation_facts,
    };
}

fn countFixedBindings(comptime F: type, table: usize, parented: bool) usize {
    var count: usize = 0;
    for (F.Owner.fixed_declarations) |declaration| {
        if (declaration.table == table and (declaration.parent != null) == parented) count += 1;
    }
    return count;
}

fn callbackChecksum(contexts: anytype) u64 {
    var checksum: u64 = 0;
    for (contexts) |context| checksum +%= context.checksum;
    return checksum;
}

fn elapsedNs(started: i96, finished: i96) u64 {
    const elapsed = finished - started;
    return if (elapsed <= 0) 0 else @intCast(elapsed);
}

fn runFixture(comptime F: type, comptime profile: Profile, comptime candidate: Candidate, bits: u16, allocator: std.mem.Allocator, io: std.Io, options: Options) !void {
    const count = if (options.mode == .timing) options.samples else 1;
    const raw = try allocator.alloc(u64, if (options.mode == .timing) count else 0);
    defer allocator.free(raw);
    var memory_report: ?MemoryReport = null;
    var work_counters: ?WorkCounters = null;
    var operation_facts: ?OperationFacts = null;
    for (0..count) |index| {
        const result = try runSample(F, profile, candidate, bits, options, index, io);
        if (options.mode == .timing) raw[index] = result.elapsed_ns;
        memory_report = result.memory;
        work_counters = result.work_counters;
        operation_facts = result.operation_facts;
    }
    if (options.mode == .timing) {
        std.debug.print("owner-operation raw-samples candidate={s} uid_bits={d} profile={s} operation={s} elapsed_ns=[", .{
            @tagName(candidate), bits, @tagName(profile), @tagName(options.operation),
        });
        for (raw, 0..) |value, i| {
            if (i != 0) std.debug.print(",", .{});
            std.debug.print("{d}", .{value});
        }
        std.debug.print("]\n", .{});
    }
    var table_capacities: [F.Owner.table_count]u32 = undefined;
    for (F.Owner.table_declarations, 0..) |declaration, table| table_capacities[table] = declaration.capacity;
    const affinity: ?AffinityReport = if (options.placement) |placement|
        .{
            .requested_mask = placement.requested_mask,
            .before_process_mask = placement.before_process_mask,
            .after_process_mask = placement.after_process_mask,
            .system_mask = placement.system_mask,
            .executing_group = placement.executing_group,
            .executing_processor = placement.executing_processor,
        }
    else
        null;
    const record = BenchmarkRecord{
        .passed = true,
        .candidate = @tagName(candidate),
        .uid_bits = bits,
        .profile = profileName(profile),
        .operation = operationName(options.operation),
        .run_mode = @tagName(options.mode),
        .build_mode = @tagName(builtin.mode),
        .instrumented_build = instrumented_build,
        .iterations = options.iterations,
        .sample_count = raw.len,
        .tables = F.Owner.table_count,
        .resources = F.Owner.resource_capacity,
        .partitions = F.Owner.partition_capacity,
        .index_buckets = index_buckets,
        .index_nodes = F.Owner.resource_capacity + F.Owner.partition_capacity,
        .fixed_registrations = F.Owner.fixed_declarations.len,
        .table_capacities = &table_capacities,
        .operation_facts = operation_facts.?,
        .samples_elapsed_ns = raw,
        .memory = memory_report.?,
        .work_counters = if (options.mode == .timing) null else work_counters,
        .timing_boundary = timingBoundary(options.operation),
        .affinity = affinity,
    };
    try writeJson(io, allocator, record);
}

fn timingBoundary(operation: Operation) []const u8 {
    return switch (operation) {
        .audit => "Owner.audit with caller audit scratch",
        .prepare_repeat => "Index.Transactions.prepare plus abort for the full removal list",
        .single_remove => "Owner.close on one preselected resident resource UID",
        .max_table_close => "Owner.close on the selected root subtree, including commit and notification dispatch; a direct top-level fixed binding remains registered",
        .full_destroy => "Owner.destroy(false), including commit, payload release, and notification dispatch",
        .small_churn => "Owner.close, Owner.push, and Owner.endUse(.complete) with result checks",
    };
}

fn dispatchProfile(comptime candidate: Candidate, comptime bits: u16, allocator: std.mem.Allocator, io: std.Io, options: Options) !void {
    if (options.profile == .original_maximum) {
        const F = maxima.Fixture(bits, candidate);
        try runFixture(F, .original_maximum, candidate, bits, allocator, io, options);
    } else {
        const F = ProductFixture(bits, candidate);
        try runFixture(F, .product_327699, candidate, bits, allocator, io, options);
    }
}

fn dispatchCandidate(comptime candidate: Candidate, allocator: std.mem.Allocator, io: std.Io, options: Options) !void {
    if (options.uid_bits == 128) {
        try dispatchProfile(candidate, 128, allocator, io, options);
    } else {
        try dispatchProfile(candidate, 256, allocator, io, options);
    }
}

fn checkBuildCandidate(options: Options) !void {
    if (compiled_candidate == .both) {
        if (options.mode == .timing and options.both_candidates) return error.TimingRequiresSingleCandidateBuild;
        if (options.mode == .timing and options.candidate == null) return error.TimingRequiresSingleCandidateBuild;
        return;
    }
    if (options.both_candidates) return error.CandidateBuildMismatch;
    if (compiled_candidate == .c2 and options.candidate.? != .c2 or
        compiled_candidate == .b2 and options.candidate.? != .b2)
        return error.CandidateBuildMismatch;
}

fn dispatch(options: Options, allocator: std.mem.Allocator, io: std.Io) !void {
    try checkBuildCandidate(options);
    if (compiled_candidate == .c2) {
        try dispatchCandidate(.c2, allocator, io, options);
    } else if (compiled_candidate == .b2) {
        try dispatchCandidate(.b2, allocator, io, options);
    } else if (options.both_candidates) {
        try dispatchCandidate(.c2, allocator, io, options);
        try dispatchCandidate(.b2, allocator, io, options);
    } else {
        switch (options.candidate.?) {
            .c2 => try dispatchCandidate(.c2, allocator, io, options),
            .b2 => try dispatchCandidate(.b2, allocator, io, options),
        }
    }
}

pub fn main(init: std.process.Init) !void {
    const allocator = init.gpa;
    const args = try init.minimal.args.toSlice(init.arena.allocator());
    var options = try parseOptions(args);
    if (options.mode == .timing and builtin.mode != .ReleaseFast) return error.TimingRequiresReleaseFast;
    if (options.mode == .work and !instrumented_build) return error.WorkModeRequiresInstrumentedBuild;
    if (options.mode == .timing and instrumented_build) return error.TimingRequiresCleanBuild;
    if (options.mode == .timing and compiled_candidate == .both) return error.TimingRequiresSingleCandidateBuild;
    if (options.affinity_mask) |mask| options.placement = try benchmark_affinity.apply(mask);
    try dispatch(options, allocator, init.io);
}
