//! Standalone, sequential full-owner use benchmark.
//!
//! Each invocation selects one candidate, UID width, capacity, API path, state,
//! and trace. Run separate processes when comparing candidates. Timing mode is
//! intentionally rejected unless this module was compiled as ReleaseFast.
const std = @import("std");
const builtin = @import("builtin");
const uid_module = @import("../uid.zig");
const c2_index = @import("index.zig");
const b2_index = @import("index_b2.zig");
const notifications = @import("notifications.zig");
const owner_module = @import("owner.zig");
const types = @import("types.zig");
const values = @import("ledger_values").Bound;
const benchmark_affinity = @import("benchmark_affinity.zig");
const benchmark_options = @import("benchmark_options");
const benchmark_cache_cold = @import("benchmark_cache_cold");

const working_set: usize = 64;
const fixed_declaration_count: usize = working_set + 1;
const dynamic_uid_input_count: usize = working_set + 1;
const max_cache_address_workspace_bytes: usize = 64 * 1024 * 1024;
const fixed_uid_base: u64 = 0x1000_0000_0000_0000;
const dynamic_uid_base: u64 = 0x8000_0000_0000_0000;

const CandidateKind = enum { c2, b2 };
const CompiledCandidate = enum { both, c2, b2 };
const compiled_candidate: CompiledCandidate = if (std.mem.eql(u8, @tagName(benchmark_options.candidate), "both"))
    .both
else if (std.mem.eql(u8, @tagName(benchmark_options.candidate), "c2"))
    .c2
else if (std.mem.eql(u8, @tagName(benchmark_options.candidate), "b2"))
    .b2
else
    @compileError("benchmark_options.candidate must be both, c2, or b2");
const Path = enum { constant_fixed, runtime_fixed_uid, runtime_dynamic_uid };
const Measurement = enum { owner_use, index_only };
const State = enum { hit, notresident, loading };
const Trace = enum { ordered, shuffled, burst, single };
const CacheMode = enum { warm, cold };
const RunMode = enum { timing, correctness };

const Options = struct {
    candidate: CandidateKind,
    uid_bits: u16,
    capacity: usize,
    path: Path,
    measurement: Measurement,
    state: State,
    trace: Trace,
    cache: CacheMode,
    mode: RunMode,
    iterations: usize,
    samples: usize,
    warmup: usize,
    seed: u64,
    burst_length: usize,
    affinity_mask: ?usize,
    placement: ?benchmark_affinity.Placement = null,
};

const ParsedOptions = struct {
    candidate: ?CandidateKind = null,
    uid_bits: ?u16 = null,
    capacity: ?usize = null,
    path: ?Path = null,
    measurement: Measurement = .owner_use,
    state: ?State = null,
    trace: ?Trace = null,
    cache: CacheMode = .warm,
    mode: ?RunMode = null,
    iterations: ?usize = null,
    samples: ?usize = null,
    warmup: usize = 0,
    seed: u64 = 1,
    burst_length: usize = 8,
    affinity_mask: ?usize = null,
};

const ResultCounts = struct {
    in_memory: usize = 0,
    not_in_memory: usize = 0,
    loading: usize = 0,
};

const IndexLookupCounts = struct {
    hit: usize = 0,
    miss: usize = 0,
};

const MetadataBytes = struct {
    owner_header: usize,
    owner_rows: usize,
    owner_shells: usize,
    owner_tables: usize,
    owner_fixed_bindings: usize,
    index_nodes: usize,
    index_homes: usize,
    owner_and_index_arrays_total: usize,
    fixed_declaration_configuration: usize,
    table_declaration_configuration: usize,
    dynamic_uid_inputs: usize,
    trace_indices: usize,
    trace_builder_scratch: usize,
    cache_line_address_workspace: usize,
    timing_samples: usize,
    api_selector_table: usize,
    subscriber_slice_headers: usize,
    owner_workspace_header: usize,
    owner_workspace_arrays: usize,
    declared_array_payload_bytes_sum: usize,
};

const OutputRecord = struct {
    schema: []const u8 = "resource-manager/owner-use-benchmark/v2",
    candidate: []const u8,
    uid_width_bits: u16,
    resource_capacity: usize,
    partition_capacity: usize = 0,
    index_node_capacity: usize,
    index_home_capacity: usize,
    indexed_entries_after_setup: usize,
    fixed_declaration_count: usize,
    dynamic_entry_count: usize,
    hot_working_set_size: usize,
    fixed_hot_entry_count: usize,
    dynamic_hot_entry_count: usize,
    nonresident_lookup_kind: ?[]const u8,
    fixture_construction: []const u8,
    fixture_construction_timed: bool,
    activity_bitmap_width: u8,
    path: []const u8,
    measurement: []const u8,
    measurement_boundary: []const u8,
    state: []const u8,
    trace: []const u8,
    trace_drives_target_selection: bool,
    api_selector: []const u8,
    api_selector_timed: bool = false,
    api_thunk_call_timed: bool = true,
    timed_operation: []const u8,
    cache_mode: []const u8,
    cache_cold_method: ?[]const u8,
    cache_cold_scope: ?[]const u8,
    cache_cold_cache_line_bytes: ?usize,
    cache_cold_workspace_address_count: usize,
    cache_cold_workspace_bytes: usize,
    cache_cold_workspace_limit_bytes: usize = max_cache_address_workspace_bytes,
    cache_cold_max_operation_address_count: usize,
    cache_cold_flush_count: usize,
    cache_cold_flushed_address_count: usize,
    run_mode: []const u8,
    build_mode: []const u8,
    iterations_per_sample: usize,
    sample_count: usize,
    warmup_iterations: usize,
    seed: u64,
    burst_length: usize,
    samples_elapsed_ns: []const u64,
    timing_granularity: []const u8,
    checksum: u64,
    result_counts: ?ResultCounts,
    index_lookup_counts: ?IndexLookupCounts,
    registered_bytes_after_setup: u64,
    system_page_bytes_after_setup: u64,
    metadata_bytes: MetadataBytes,
    same_home_contention_supported: bool = false,
    affinity: ?benchmark_affinity.Placement,
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

fn key(comptime Uid: type, value: u64) Uid {
    var result = Uid{ .bytes = @splat(0) };
    std.mem.writeInt(u64, result.bytes[result.bytes.len - 8 ..][0..8], value, .big);
    return result;
}

fn Driver(comptime bits: u16, comptime capacity: usize, comptime candidate: CandidateKind) type {
    @setEvalBranchQuota(10_000);
    const Uid = uid_module.Identifier(bits);
    const Index = if (candidate == .c2)
        c2_index.Candidate(Uid, .c2_chain)
    else
        b2_index.Candidate(Uid, .b2_ordered);
    const Fixed = types.For(Uid).Fixed;
    const fixed_count = fixed_declaration_count;
    const dynamic_count = capacity - fixed_count;
    const buckets = capacity * 2;

    const table_declarations = [_]types.Table{
        .{
            .capacity = @intCast(capacity),
            .partition_capacity = 0,
            .reserved_start = 0,
            .reserved_count = 0,
            .attributes = attributes(),
            .policy = policy(),
        },
    };
    const fixed_declarations = comptime blk: {
        var declarations: [fixed_count]Fixed = undefined;
        for (0..fixed_count) |i| {
            declarations[i] = .{
                .table = 0,
                .parent = null,
                .ordinal = @intCast(i),
                .uid = key(Uid, fixed_uid_base + @as(u64, @intCast(i))),
                .allocation_bytes = if (i == 1) 64 else 0,
                .attributes = null,
            };
        }
        break :blk declarations;
    };
    const Ledger = owner_module.Owner(
        .{
            .uid_width = bits,
            .activity_width = @intCast(values.activity_bitmap_width),
            .index_buckets = @intCast(buckets),
        },
        &table_declarations,
        &fixed_declarations,
        Index,
    );
    const Notifier = notifications.Notifier(Uid);

    return struct {
        const Self = @This();
        const UseFunction = *const fn (*Ledger, []const Uid, usize) anyerror!Ledger.api.UseResult;
        const ColdCollectFunction = *const fn (*Ledger, *benchmark_cache_cold.Workspace) anyerror!benchmark_cache_cold.Collection;

        const ColdStats = struct {
            cpu: ?benchmark_cache_cold.CpuInfo = null,
            flush_count: usize = 0,
            flushed_address_count: usize = 0,
        };

        const SizedColdWorkspace = struct {
            addresses: []usize,
            max_operation_address_count: usize,
        };

        const constant_fixed_functions: [fixed_count]UseFunction = blk: {
            var functions: [fixed_count]UseFunction = undefined;
            for (0..fixed_count) |declaration_index| {
                const Thunk = FixedThunk(declaration_index);
                functions[declaration_index] = &Thunk.call;
            }
            break :blk functions;
        };

        const constant_fixed_cold_collectors: [fixed_count]ColdCollectFunction = blk: {
            var collectors: [fixed_count]ColdCollectFunction = undefined;
            for (0..fixed_count) |declaration_index| {
                const Collector = FixedColdCollector(declaration_index);
                collectors[declaration_index] = &Collector.collect;
            }
            break :blk collectors;
        };

        fn FixedColdCollector(comptime declaration_index: usize) type {
            return struct {
                fn collect(ledger: *Ledger, workspace: *benchmark_cache_cold.Workspace) anyerror!benchmark_cache_cold.Collection {
                    return benchmark_cache_cold.collectUse(
                        ledger,
                        &Ledger.fixed_declarations[declaration_index].uid,
                        declaration_index,
                        workspace,
                    );
                }
            };
        }

        fn collectColdOperation(
            comptime path: Path,
            ledger: *Ledger,
            dynamic_uids: []const Uid,
            measurement: Measurement,
            target_index: usize,
            workspace: *benchmark_cache_cold.Workspace,
        ) anyerror!benchmark_cache_cold.Collection {
            if (path == .constant_fixed) {
                if (measurement != .owner_use) return error.IndexOnlyRequiresRuntimeUidPath;
                if (target_index >= fixed_count) return error.InvalidTraceIndex;
                return constant_fixed_cold_collectors[target_index](ledger, workspace);
            }

            const uid_source = queryUidSource(path, dynamic_uids, target_index);
            return benchmark_cache_cold.collectUse(ledger, uid_source, null, workspace);
        }

        fn collectColdGrowing(
            allocator: std.mem.Allocator,
            comptime path: Path,
            ledger: *Ledger,
            dynamic_uids: []const Uid,
            measurement: Measurement,
            target_index: usize,
            address_buffer: *[]usize,
        ) anyerror!benchmark_cache_cold.Collection {
            const address_limit = max_cache_address_workspace_bytes / @sizeOf(usize);
            while (true) {
                var workspace = benchmark_cache_cold.Workspace{ .addresses = address_buffer.* };
                const collection = collectColdOperation(
                    path,
                    ledger,
                    dynamic_uids,
                    measurement,
                    target_index,
                    &workspace,
                ) catch |err| {
                    if (err != error.WorkspaceTooSmall) return err;
                    const current_capacity = address_buffer.*.len;
                    if (current_capacity >= address_limit) return error.CacheColdWorkspaceExceeds64MiB;

                    const next_capacity = if (current_capacity == 0)
                        @min(address_limit, 16)
                    else if (current_capacity > address_limit / 2)
                        address_limit
                    else
                        current_capacity * 2;
                    const expanded = try allocator.alloc(usize, next_capacity);
                    if (current_capacity != 0) allocator.free(address_buffer.*);
                    address_buffer.* = expanded;
                    continue;
                };
                return collection;
            }
        }

        fn makeColdWorkspace(
            allocator: std.mem.Allocator,
            ledger: *Ledger,
            dynamic_uids: []const Uid,
            measurement: Measurement,
            state: State,
            trace_indices: []const u32,
            comptime path: Path,
        ) !SizedColdWorkspace {
            var growth_buffer: []usize = &.{};
            errdefer if (growth_buffer.len != 0) allocator.free(growth_buffer);

            var max_operation_address_count: usize = 0;
            for (trace_indices) |trace_index| {
                const target_index = targetIndex(state, trace_index);
                const collection = try collectColdGrowing(
                    allocator,
                    path,
                    ledger,
                    dynamic_uids,
                    measurement,
                    target_index,
                    &growth_buffer,
                );
                max_operation_address_count = @max(max_operation_address_count, collection.address_count);
            }
            if (max_operation_address_count == 0) return error.EmptyColdCacheTrace;

            if (growth_buffer.len != 0) allocator.free(growth_buffer);
            growth_buffer = &.{};
            const exact_addresses = try allocator.alloc(usize, max_operation_address_count);
            return .{
                .addresses = exact_addresses,
                .max_operation_address_count = max_operation_address_count,
            };
        }

        fn prepareColdOperation(
            comptime path: Path,
            ledger: *Ledger,
            dynamic_uids: []const Uid,
            measurement: Measurement,
            target_index: usize,
            workspace: *benchmark_cache_cold.Workspace,
            stats: *ColdStats,
        ) !void {
            const collection = try collectColdOperation(path, ledger, dynamic_uids, measurement, target_index, workspace);
            if (collection.address_count != workspace.count or workspace.count > workspace.addresses.len)
                return error.InvalidColdCollection;

            const cpu = try benchmark_cache_cold.flushCollected(workspace);
            if (stats.cpu) |previous| {
                if (previous.clflush_supported != cpu.clflush_supported or
                    previous.mfence_supported != cpu.mfence_supported or
                    previous.reported_line_bytes != cpu.reported_line_bytes)
                    return error.CacheCpuInfoChanged;
            } else {
                stats.cpu = cpu;
            }
            stats.flush_count = try std.math.add(usize, stats.flush_count, 1);
            stats.flushed_address_count = try std.math.add(usize, stats.flushed_address_count, workspace.count);
        }

        pub fn run(allocator: std.mem.Allocator, io: std.Io, options: Options, path: Path) !void {
            switch (path) {
                .constant_fixed => try runPath(allocator, io, options, .constant_fixed),
                .runtime_fixed_uid => try runPath(allocator, io, options, .runtime_fixed_uid),
                .runtime_dynamic_uid => try runPath(allocator, io, options, .runtime_dynamic_uid),
            }
        }

        fn runPath(allocator: std.mem.Allocator, io: std.Io, options: Options, comptime path: Path) !void {
            if (options.mode == .timing and builtin.mode != .ReleaseFast)
                return error.TimingRequiresReleaseFast;

            const rows = try allocator.alloc(Ledger.Row, capacity);
            defer allocator.free(rows);
            const shells: []Ledger.Shell = &.{};
            const table_states = try allocator.alloc(Ledger.TableState, 1);
            defer allocator.free(table_states);
            const bindings = try allocator.alloc(Ledger.FixedBinding, fixed_count);
            defer allocator.free(bindings);
            const nodes = try allocator.alloc(Index.Node, capacity);
            defer allocator.free(nodes);
            const homes = try allocator.alloc(Index.Home, buckets);
            defer allocator.free(homes);
            const dynamic_uids = try allocator.alloc(Uid, dynamic_uid_input_count);
            defer allocator.free(dynamic_uids);
            for (dynamic_uids, 0..) |*uid, i| {
                uid.* = key(Uid, dynamic_uid_base + @as(u64, @intCast(i)));
            }

            const trace_indices = try makeTrace(allocator, options.trace, options.seed, options.burst_length);
            defer allocator.free(trace_indices);

            var subscriber_rows = [_][]Notifier.Record{&.{}};
            var ledger: Ledger = undefined;
            try Ledger.init(&ledger, rows, shells, table_states, bindings, nodes, homes, &subscriber_rows);
            var work = Ledger.Workspace{
                .candidates = &.{},
                .rows = &.{},
                .shells = &.{},
                .homes = &.{},
                .notes = &.{},
            };
            defer {
                for (rows) |*row| row.resource.forceRelease() catch @panic("benchmark owner cleanup failed");
            }

            var pending_loader: ?Ledger.api.Borrow = null;
            try seedFixedPool(&ledger, &work, options.state, path, &pending_loader);
            try seedDynamicPool(&ledger, &work, dynamic_uids[0..working_set], options.state, path, &pending_loader);
            try registerDynamicRemainder(&ledger, dynamic_count);

            var actual_entries: usize = 0;
            for (0..ledger.index.nodes.len) |node_index| {
                if (try ledger.index.entryAt(@intCast(node_index))) |entry| {
                    if (entry.location.kind != .resource or entry.location.table != 0 or
                        entry.location.slot >= rows.len or rows[entry.location.slot].uid == null or
                        !rows[entry.location.slot].uid.?.eql(entry.uid))
                    {
                        return error.IndexEntryDoesNotMatchOwnerRow;
                    }
                    actual_entries += 1;
                }
            }
            const expected_entries = fixed_count + dynamic_count;
            if (actual_entries != expected_entries) return error.IncompleteIndexPopulation;
            if (expected_entries != capacity or ledger.index.nodes.len != capacity or ledger.index.homes.len != buckets)
                return error.InvalidFixtureShape;

            var cold_addresses: []usize = &.{};
            defer if (cold_addresses.len != 0) allocator.free(cold_addresses);
            var cold_max_operation_address_count: usize = 0;
            if (options.cache == .cold) {
                const sized_workspace = try makeColdWorkspace(
                    allocator,
                    &ledger,
                    dynamic_uids,
                    options.measurement,
                    options.state,
                    trace_indices,
                    path,
                );
                cold_addresses = sized_workspace.addresses;
                cold_max_operation_address_count = sized_workspace.max_operation_address_count;
            }
            var cold_workspace = benchmark_cache_cold.Workspace{ .addresses = cold_addresses };
            var cold_stats = ColdStats{};

            var observed_counts = ResultCounts{};
            var observed_index_counts = IndexLookupCounts{};
            var checksum: u64 = 0;
            var samples: []u64 = &.{};
            defer allocator.free(samples);

            if (options.mode == .timing) {
                samples = try allocator.alloc(u64, options.samples);

                if (options.measurement == .owner_use) {
                    var warmup_index: usize = 0;
                    while (warmup_index < options.warmup) : (warmup_index += 1) {
                        const trace_index = traceIndex(trace_indices, warmup_index);
                        const target_index = targetIndex(options.state, trace_index);
                        const use_function = selectUse(path, target_index);
                        if (options.cache == .cold) try prepareColdOperation(
                            path,
                            &ledger,
                            dynamic_uids,
                            options.measurement,
                            target_index,
                            &cold_workspace,
                            &cold_stats,
                        );
                        try performOne(use_function, &ledger, dynamic_uids, options.state, target_index, false, &checksum, &observed_counts);
                    }

                    for (samples) |*sample_ns| {
                        var cursor: usize = 0;
                        var elapsed: u64 = 0;
                        var iteration: usize = 0;
                        while (iteration < options.iterations) : (iteration += 1) {
                            const trace_index = traceIndexAt(trace_indices, &cursor);
                            const target_index = targetIndex(options.state, trace_index);
                            const use_function = selectUse(path, target_index);
                            if (options.cache == .cold) try prepareColdOperation(
                                path,
                                &ledger,
                                dynamic_uids,
                                options.measurement,
                                target_index,
                                &cold_workspace,
                                &cold_stats,
                            );
                            const started = std.Io.Clock.awake.now(io).nanoseconds;
                            try performOne(use_function, &ledger, dynamic_uids, options.state, target_index, false, &checksum, &observed_counts);
                            elapsed +%= elapsedNs(started, std.Io.Clock.awake.now(io).nanoseconds);
                        }
                        sample_ns.* = elapsed;
                    }
                } else {
                    var warmup_index: usize = 0;
                    while (warmup_index < options.warmup) : (warmup_index += 1) {
                        const trace_index = traceIndex(trace_indices, warmup_index);
                        const target_index = targetIndex(options.state, trace_index);
                        const uid_source = queryUidSource(path, dynamic_uids, target_index);
                        const expected_location = expectedIndexLocation(path, target_index);
                        if (options.cache == .cold) try prepareColdOperation(
                            path,
                            &ledger,
                            dynamic_uids,
                            options.measurement,
                            target_index,
                            &cold_workspace,
                            &cold_stats,
                        );
                        try performIndexOnly(&ledger.index, uid_source, expected_location, false, &checksum, &observed_index_counts);
                    }

                    for (samples) |*sample_ns| {
                        var cursor: usize = 0;
                        var elapsed: u64 = 0;
                        var iteration: usize = 0;
                        while (iteration < options.iterations) : (iteration += 1) {
                            const trace_index = traceIndexAt(trace_indices, &cursor);
                            const target_index = targetIndex(options.state, trace_index);
                            const uid_source = queryUidSource(path, dynamic_uids, target_index);
                            const expected_location = expectedIndexLocation(path, target_index);
                            if (options.cache == .cold) try prepareColdOperation(
                                path,
                                &ledger,
                                dynamic_uids,
                                options.measurement,
                                target_index,
                                &cold_workspace,
                                &cold_stats,
                            );
                            const started = std.Io.Clock.awake.now(io).nanoseconds;
                            try performIndexOnly(&ledger.index, uid_source, expected_location, false, &checksum, &observed_index_counts);
                            elapsed +%= elapsedNs(started, std.Io.Clock.awake.now(io).nanoseconds);
                        }
                        sample_ns.* = elapsed;
                    }
                }
            } else {
                if (options.measurement == .owner_use) {
                    var cursor: usize = 0;
                    var iteration: usize = 0;
                    while (iteration < options.iterations) : (iteration += 1) {
                        const trace_index = traceIndexAt(trace_indices, &cursor);
                        const target_index = targetIndex(options.state, trace_index);
                        const use_function = selectUse(path, target_index);
                        if (options.cache == .cold) try prepareColdOperation(
                            path,
                            &ledger,
                            dynamic_uids,
                            options.measurement,
                            target_index,
                            &cold_workspace,
                            &cold_stats,
                        );
                        try performOne(use_function, &ledger, dynamic_uids, options.state, target_index, true, &checksum, &observed_counts);
                    }
                } else {
                    var cursor: usize = 0;
                    var iteration: usize = 0;
                    while (iteration < options.iterations) : (iteration += 1) {
                        const trace_index = traceIndexAt(trace_indices, &cursor);
                        const target_index = targetIndex(options.state, trace_index);
                        const uid_source = queryUidSource(path, dynamic_uids, target_index);
                        const expected_location = expectedIndexLocation(path, target_index);
                        if (options.cache == .cold) try prepareColdOperation(
                            path,
                            &ledger,
                            dynamic_uids,
                            options.measurement,
                            target_index,
                            &cold_workspace,
                            &cold_stats,
                        );
                        try performIndexOnly(&ledger.index, uid_source, expected_location, true, &checksum, &observed_index_counts);
                    }
                }
            }

            if (pending_loader) |loader| {
                if (try ledger.endUse(loader.lease, .complete) != .completed) return error.LoaderReturnFailed;
                pending_loader = null;
            }

            const result = OutputRecord{
                .candidate = @tagName(candidate),
                .uid_width_bits = bits,
                .resource_capacity = capacity,
                .index_node_capacity = ledger.index.nodes.len,
                .index_home_capacity = ledger.index.homes.len,
                .indexed_entries_after_setup = expected_entries,
                .fixed_declaration_count = fixed_count,
                .dynamic_entry_count = dynamic_count,
                .hot_working_set_size = working_set,
                .fixed_hot_entry_count = working_set,
                .dynamic_hot_entry_count = working_set,
                .nonresident_lookup_kind = if (options.state == .notresident)
                    (if (path == .runtime_dynamic_uid)
                        (if (options.measurement == .index_only) "absent_dynamic_uid_index_miss" else "absent_dynamic_uid")
                    else if (options.measurement == .index_only) "registered_fixed_uid_index_hit" else "registered_fixed_declaration")
                else
                    null,
                .fixture_construction = "maxdriver_direct_zero_byte_resource_and_index_setup",
                .fixture_construction_timed = false,
                .activity_bitmap_width = @intCast(values.activity_bitmap_width),
                .path = @tagName(path),
                .measurement = @tagName(options.measurement),
                .measurement_boundary = if (options.measurement == .owner_use)
                    "owner use/useFixed result consumption; hits include lease and endUse(.complete)"
                else
                    "candidate index.find(uid) and Location-or-miss consumption only; no owner API, payload read, lease, endUse, or activity access",
                .state = @tagName(options.state),
                .trace = @tagName(options.trace),
                .trace_drives_target_selection = options.state == .hit,
                .api_selector = if (options.measurement == .owner_use)
                    "select target and signature-compatible API thunk before timestamp; constant_fixed indexes the comptime declaration thunk table, UID paths select their use thunk"
                else
                    "select runtime UID and expected location before timestamp; dispatch to the selected candidate index directly",
                .api_thunk_call_timed = options.measurement == .owner_use,
                .timed_operation = if (options.measurement == .owner_use)
                    (if (options.state == .hit)
                        "selected API thunk call, owner use/useFixed result validation and checksum, then endUse(.complete)"
                    else
                        "selected API thunk call, owner use/useFixed result validation and checksum")
                else
                    "candidate index.find(uid); consume returned Location fields or miss sentinel into checksum",
                .cache_mode = @tagName(options.cache),
                .cache_cold_method = if (options.cache == .cold) "collect exact lookup metadata address ranges; execute guarded CLFLUSH+MFENCE before each operation; does not establish an empty cache" else null,
                .cache_cold_scope = if (options.cache == .cold)
                    (if (options.measurement == .owner_use)
                        "owner-use lookup metadata collected for the selected path"
                    else
                        "index-only timing; collection includes owner header and matching resource row when present as an explicit superset")
                else
                    null,
                .cache_cold_cache_line_bytes = if (cold_stats.cpu) |cpu| cpu.reported_line_bytes else null,
                .cache_cold_workspace_address_count = cold_addresses.len,
                .cache_cold_workspace_bytes = bytesFor(cold_addresses.len, @sizeOf(usize)),
                .cache_cold_max_operation_address_count = cold_max_operation_address_count,
                .cache_cold_flush_count = cold_stats.flush_count,
                .cache_cold_flushed_address_count = cold_stats.flushed_address_count,
                .run_mode = @tagName(options.mode),
                .build_mode = @tagName(builtin.mode),
                .iterations_per_sample = options.iterations,
                .sample_count = samples.len,
                .warmup_iterations = if (options.mode == .timing) options.warmup else 0,
                .seed = options.seed,
                .burst_length = options.burst_length,
                .affinity = options.placement,
                .samples_elapsed_ns = samples,
                .timing_granularity = if (options.measurement == .owner_use)
                    "sum of per-operation intervals; trace/target/thunk selection and cache-line collection plus CLFLUSH+MFENCE are outside each interval; the selected thunk call and result handling are inside"
                else
                    "sum of per-operation intervals; trace/target/UID/expected-location selection and cache-line collection plus CLFLUSH+MFENCE are outside each interval; candidate.find and Location consumption are inside",
                .checksum = checksum,
                .result_counts = if (options.mode == .correctness and options.measurement == .owner_use) observed_counts else null,
                .index_lookup_counts = if (options.mode == .correctness and options.measurement == .index_only) observed_index_counts else null,
                .registered_bytes_after_setup = ledger.tables[0].registered_bytes,
                .system_page_bytes_after_setup = ledger.tables[0].system_page_bytes,
                .metadata_bytes = .{
                    .owner_header = @sizeOf(Ledger),
                    .owner_rows = bytesFor(capacity, @sizeOf(Ledger.Row)),
                    .owner_shells = 0,
                    .owner_tables = @sizeOf(Ledger.TableState),
                    .owner_fixed_bindings = bytesFor(fixed_count, @sizeOf(Ledger.FixedBinding)),
                    .index_nodes = bytesFor(capacity, @sizeOf(Index.Node)),
                    .index_homes = bytesFor(buckets, @sizeOf(Index.Home)),
                    .owner_and_index_arrays_total = sumArrayBytes(.{
                        bytesFor(capacity, @sizeOf(Ledger.Row)),
                        @sizeOf(Ledger.TableState),
                        bytesFor(fixed_count, @sizeOf(Ledger.FixedBinding)),
                        bytesFor(capacity, @sizeOf(Index.Node)),
                        bytesFor(buckets, @sizeOf(Index.Home)),
                    }),
                    .fixed_declaration_configuration = bytesFor(fixed_count, @sizeOf(Fixed)),
                    .table_declaration_configuration = @sizeOf(@TypeOf(table_declarations)),
                    .dynamic_uid_inputs = bytesFor(dynamic_uid_input_count, @sizeOf(Uid)),
                    .trace_indices = bytesFor(trace_indices.len, @sizeOf(u32)),
                    .trace_builder_scratch = if (options.trace == .burst) bytesFor(working_set, @sizeOf(u32)) else 0,
                    .cache_line_address_workspace = bytesFor(cold_addresses.len, @sizeOf(usize)),
                    .timing_samples = if (options.mode == .timing) bytesFor(options.samples, @sizeOf(u64)) else 0,
                    .api_selector_table = @sizeOf(@TypeOf(constant_fixed_functions)),
                    .subscriber_slice_headers = @sizeOf(@TypeOf(subscriber_rows)),
                    .owner_workspace_header = @sizeOf(Ledger.Workspace),
                    .owner_workspace_arrays = 0,
                    .declared_array_payload_bytes_sum = sumArrayBytes(.{
                        @sizeOf(Ledger),
                        bytesFor(capacity, @sizeOf(Ledger.Row)),
                        @sizeOf(Ledger.TableState),
                        bytesFor(fixed_count, @sizeOf(Ledger.FixedBinding)),
                        bytesFor(capacity, @sizeOf(Index.Node)),
                        bytesFor(buckets, @sizeOf(Index.Home)),
                        bytesFor(fixed_count, @sizeOf(Fixed)),
                        @sizeOf(@TypeOf(table_declarations)),
                        bytesFor(dynamic_uid_input_count, @sizeOf(Uid)),
                        bytesFor(trace_indices.len, @sizeOf(u32)),
                        if (options.trace == .burst) bytesFor(working_set, @sizeOf(u32)) else 0,
                        bytesFor(cold_addresses.len, @sizeOf(usize)),
                        if (options.mode == .timing) bytesFor(options.samples, @sizeOf(u64)) else 0,
                        @sizeOf(@TypeOf(constant_fixed_functions)),
                        @sizeOf(@TypeOf(subscriber_rows)),
                        @sizeOf(Ledger.Workspace),
                    }),
                },
            };
            const output = try std.json.Stringify.valueAlloc(allocator, result, .{});
            defer allocator.free(output);
            var stdout_buffer: [2048]u8 = undefined;
            var stdout_file_writer = std.Io.File.stdout().writer(io, &stdout_buffer);
            const stdout = &stdout_file_writer.interface;
            try stdout.writeAll(output);
            try stdout.writeAll("\n");
            try stdout.flush();
        }

        fn seedFixedPool(
            ledger: *Ledger,
            work: *Ledger.Workspace,
            state: State,
            path: Path,
            pending_loader: *?Ledger.api.Borrow,
        ) !void {
            for (0..working_set) |i| {
                const borrow = try pushFixed(ledger, work, i);
                if (state == .loading and path != .runtime_dynamic_uid and i == 0) {
                    pending_loader.* = borrow;
                } else if (try ledger.endUse(borrow.lease, .complete) != .completed) {
                    return error.FixedSeedEndUseFailed;
                }
            }
        }

        fn seedDynamicPool(
            ledger: *Ledger,
            work: *Ledger.Workspace,
            dynamic_uids: []const Uid,
            state: State,
            path: Path,
            pending_loader: *?Ledger.api.Borrow,
        ) !void {
            for (dynamic_uids, 0..) |uid, i| {
                const borrow = try pushDynamic(ledger, work, uid, if (i == 1) 64 else 0);
                if (state == .loading and path == .runtime_dynamic_uid and i == 0) {
                    pending_loader.* = borrow;
                } else if (try ledger.endUse(borrow.lease, .complete) != .completed) {
                    return error.DynamicSeedEndUseFailed;
                }
            }
        }

        /// Builds the cold dynamic remainder without measuring owner `push`
        /// latency or generating a capacity-sized fixed-declaration type.
        /// Each row gets a zero-byte dynamic registration, a valid zero-byte
        /// resident lifecycle generation, and a real owner-index entry before
        /// the fixture is exposed to measured calls. UID index 64 is left absent
        /// for the runtime-dynamic nonresident case.
        fn registerDynamicRemainder(ledger: *Ledger, dynamic_entries: usize) !void {
            const Resource = @TypeOf(ledger.rows[0].resource);
            const epoch = ledger.epoch.load(.acquire);
            for (working_set..dynamic_entries) |dynamic_row_index| {
                const slot = fixed_count + dynamic_row_index;
                const uid_index = dynamic_row_index + 1;
                const uid = key(Uid, dynamic_uid_base + @as(u64, @intCast(uid_index)));
                const row = &ledger.rows[slot];
                row.uid = uid;
                row.parent = null;
                row.fixed_declaration = null;
                row.fixed_parent_number = 0;
                row.attributes = table_declarations[0].attributes;
                row.resource = try Resource.dynamic(0);
                const allocation_number = try ledger.numbers.take();
                const fresh = (try row.resource.push(epoch, allocation_number)).fresh;
                if (try row.resource.endUse(fresh.lease, .complete, epoch) != .completed)
                    return error.DynamicRemainderEndUseFailed;
                try ledger.index.insert(@intCast(slot), uid, .{
                    .kind = .resource,
                    .table = 0,
                    .slot = @intCast(slot),
                });
            }
        }

        fn pushFixed(ledger: *Ledger, work: *Ledger.Workspace, declaration: usize) !Ledger.api.Borrow {
            return switch (try ledger.push(.{ .table = 0 }, Ledger.fixed_declarations[declaration].uid, null, work)) {
                .newly_allocated => |borrow| borrow,
                .in_memory => error.FixedSeedAlreadyResident,
                .loading => error.FixedSeedAlreadyLoading,
            };
        }

        fn pushDynamic(ledger: *Ledger, work: *Ledger.Workspace, uid: Uid, size: u64) !Ledger.api.Borrow {
            return switch (try ledger.push(.{ .table = 0 }, uid, size, work)) {
                .newly_allocated => |borrow| borrow,
                .in_memory => error.DynamicSeedAlreadyResident,
                .loading => error.DynamicSeedAlreadyLoading,
            };
        }

        fn FixedThunk(comptime declaration_index: usize) type {
            return struct {
                fn call(ledger: *Ledger, _: []const Uid, _: usize) anyerror!Ledger.api.UseResult {
                    return ledger.useFixed(declaration_index);
                }
            };
        }

        fn runtimeFixedThunk(ledger: *Ledger, _: []const Uid, index: usize) anyerror!Ledger.api.UseResult {
            if (index >= fixed_count) return error.InvalidTraceIndex;
            return ledger.use(Ledger.fixed_declarations[index].uid);
        }

        fn runtimeDynamicThunk(ledger: *Ledger, dynamic_uids: []const Uid, index: usize) anyerror!Ledger.api.UseResult {
            if (index >= dynamic_uids.len) return error.InvalidTraceIndex;
            return ledger.use(dynamic_uids[index]);
        }

        fn selectUse(comptime path: Path, index: usize) UseFunction {
            return switch (path) {
                .constant_fixed => if (index < fixed_count) constant_fixed_functions[index] else unreachable,
                .runtime_fixed_uid => runtimeFixedThunk,
                .runtime_dynamic_uid => runtimeDynamicThunk,
            };
        }

        fn queryUidSource(comptime path: Path, dynamic_uids: []const Uid, target_index: usize) *const Uid {
            return switch (path) {
                .constant_fixed => unreachable,
                .runtime_fixed_uid => &Ledger.fixed_declarations[target_index].uid,
                .runtime_dynamic_uid => &dynamic_uids[target_index],
            };
        }

        fn expectedIndexLocation(comptime path: Path, target_index: usize) ?Index.Location {
            return switch (path) {
                .constant_fixed => unreachable,
                .runtime_fixed_uid => .{ .kind = .resource, .table = 0, .slot = @intCast(target_index) },
                .runtime_dynamic_uid => if (target_index < working_set)
                    .{ .kind = .resource, .table = 0, .slot = @intCast(fixed_count + target_index) }
                else
                    null,
            };
        }

        fn sameLocation(left: Index.Location, right: Index.Location) bool {
            return left.kind == right.kind and left.table == right.table and left.slot == right.slot;
        }

        fn performIndexOnly(
            index: *const Index,
            uid_source: *const Uid,
            expected_location: ?Index.Location,
            comptime count_results: bool,
            checksum: *u64,
            counts: *IndexLookupCounts,
        ) !void {
            if (try index.find(uid_source.*)) |location| {
                checksum.* +%= @as(u64, @intFromEnum(location.kind));
                checksum.* ^= (@as(u64, location.table) << 32) | location.slot;
                if (count_results) {
                    if (expected_location == null) return error.UnexpectedIndexHit;
                    if (!sameLocation(location, expected_location.?)) return error.IndexLocationMismatch;
                    counts.hit += 1;
                }
            } else {
                checksum.* ^= 0x494e_4445_585f_4d49;
                if (count_results) {
                    if (expected_location != null) return error.UnexpectedIndexMiss;
                    counts.miss += 1;
                }
            }
        }

        fn targetIndex(state: State, trace_index: u32) usize {
            return if (state == .hit) @as(usize, trace_index) else if (state == .notresident) working_set else 0;
        }

        inline fn performOne(
            use_function: UseFunction,
            ledger: *Ledger,
            dynamic_uids: []const Uid,
            state: State,
            target_index: usize,
            comptime count_results: bool,
            checksum: *u64,
            counts: *ResultCounts,
        ) !void {
            switch (try use_function(ledger, dynamic_uids, target_index)) {
                .in_memory => |borrow| {
                    if (state != .hit) return error.ExpectedNonresidentOrLoading;
                    if (count_results) counts.in_memory += 1;
                    checksum.* +%= borrow.allocation_number;
                    checksum.* ^= @as(u64, @intCast(borrow.region.len));
                    checksum.* +%= borrow.lease.use_generation;
                    if (try ledger.endUse(borrow.lease, .complete) != .completed) return error.UseEndUseFailed;
                },
                .not_in_memory => {
                    if (state != .notresident) return error.ExpectedResidentOrLoading;
                    if (count_results) counts.not_in_memory += 1;
                    checksum.* ^= 0x4e4f_545f_494e_4d45;
                },
                .loading => {
                    if (state != .loading) return error.ExpectedResidentOrNonresident;
                    if (count_results) counts.loading += 1;
                    checksum.* ^= 0x4c4f_4144_494e_4700;
                },
            }
        }
    };
}

fn makeTrace(allocator: std.mem.Allocator, trace: Trace, seed: u64, burst_length: usize) ![]u32 {
    if (trace == .single) return try allocator.dupe(u32, &.{0});
    const length = if (trace == .burst)
        try std.math.mul(usize, working_set, burst_length)
    else
        working_set;
    const result = try allocator.alloc(u32, length);
    if (trace != .burst) {
        for (result, 0..) |*item, i| item.* = @intCast(i);
        if (trace == .shuffled) shuffle(result, seed);
        return result;
    }

    const order = try allocator.alloc(u32, working_set);
    defer allocator.free(order);
    for (order, 0..) |*item, i| item.* = @intCast(i);
    shuffle(order, seed);
    var cursor: usize = 0;
    for (order) |item| {
        for (0..burst_length) |_| {
            result[cursor] = item;
            cursor += 1;
        }
    }
    return result;
}

fn shuffle(items: []u32, seed: u64) void {
    var state = if (seed == 0) 0x9e37_79b9_7f4a_7c15 else seed;
    var i = items.len;
    while (i > 1) {
        i -= 1;
        const j: usize = @intCast(nextRandom(&state) % @as(u64, @intCast(i + 1)));
        std.mem.swap(u32, &items[i], &items[j]);
    }
}

fn nextRandom(state: *u64) u64 {
    var value = state.*;
    value ^= value << 13;
    value ^= value >> 7;
    value ^= value << 17;
    state.* = value;
    return value;
}

fn traceIndex(items: []const u32, iteration: usize) u32 {
    return items[iteration % items.len];
}

fn traceIndexAt(items: []const u32, cursor: *usize) u32 {
    const result = items[cursor.*];
    cursor.* += 1;
    if (cursor.* == items.len) cursor.* = 0;
    return result;
}

fn elapsedNs(started: i96, finished: i96) u64 {
    const elapsed = finished - started;
    return if (elapsed <= 0) 0 else @intCast(elapsed);
}

fn bytesFor(count: usize, element_size: usize) usize {
    return std.math.mul(usize, count, element_size) catch @panic("benchmark metadata byte count overflow");
}

fn sumArrayBytes(items: anytype) usize {
    var total: usize = 0;
    inline for (items) |item| total = std.math.add(usize, total, item) catch @panic("benchmark metadata byte sum overflow");
    return total;
}

fn parseOptions(args: []const [:0]const u8) !Options {
    var parsed = ParsedOptions{};
    var index: usize = 1;
    while (index < args.len) {
        const name = args[index];
        index += 1;
        if (index >= args.len) return error.MissingArgumentValue;
        const value = args[index];
        index += 1;

        if (std.mem.eql(u8, name, "--candidate")) {
            if (std.mem.eql(u8, value, "c2")) parsed.candidate = .c2 else if (std.mem.eql(u8, value, "b2")) parsed.candidate = .b2 else return error.InvalidCandidate;
        } else if (std.mem.eql(u8, name, "--uid-bits")) {
            parsed.uid_bits = try std.fmt.parseInt(u16, value, 10);
        } else if (std.mem.eql(u8, name, "--capacity")) {
            parsed.capacity = try std.fmt.parseInt(usize, value, 10);
        } else if (std.mem.eql(u8, name, "--path")) {
            if (std.mem.eql(u8, value, "constant-fixed")) parsed.path = .constant_fixed else if (std.mem.eql(u8, value, "runtime-fixed-uid")) parsed.path = .runtime_fixed_uid else if (std.mem.eql(u8, value, "runtime-dynamic-uid")) parsed.path = .runtime_dynamic_uid else return error.InvalidPath;
        } else if (std.mem.eql(u8, name, "--measurement")) {
            if (std.mem.eql(u8, value, "owner-use")) parsed.measurement = .owner_use else if (std.mem.eql(u8, value, "index-only")) parsed.measurement = .index_only else return error.InvalidMeasurement;
        } else if (std.mem.eql(u8, name, "--state")) {
            if (std.mem.eql(u8, value, "hit")) parsed.state = .hit else if (std.mem.eql(u8, value, "notresident")) parsed.state = .notresident else if (std.mem.eql(u8, value, "loading")) parsed.state = .loading else return error.InvalidState;
        } else if (std.mem.eql(u8, name, "--trace")) {
            if (std.mem.eql(u8, value, "ordered")) parsed.trace = .ordered else if (std.mem.eql(u8, value, "shuffled")) parsed.trace = .shuffled else if (std.mem.eql(u8, value, "burst")) parsed.trace = .burst else if (std.mem.eql(u8, value, "single")) parsed.trace = .single else return error.InvalidTrace;
        } else if (std.mem.eql(u8, name, "--cache")) {
            if (std.mem.eql(u8, value, "warm")) parsed.cache = .warm else if (std.mem.eql(u8, value, "cold")) parsed.cache = .cold else return error.InvalidCacheMode;
        } else if (std.mem.eql(u8, name, "--mode")) {
            if (std.mem.eql(u8, value, "timing")) parsed.mode = .timing else if (std.mem.eql(u8, value, "correctness")) parsed.mode = .correctness else return error.InvalidRunMode;
        } else if (std.mem.eql(u8, name, "--iterations")) {
            parsed.iterations = try std.fmt.parseInt(usize, value, 10);
        } else if (std.mem.eql(u8, name, "--samples")) {
            parsed.samples = try std.fmt.parseInt(usize, value, 10);
        } else if (std.mem.eql(u8, name, "--warmup")) {
            parsed.warmup = try std.fmt.parseInt(usize, value, 10);
        } else if (std.mem.eql(u8, name, "--seed")) {
            parsed.seed = try std.fmt.parseInt(u64, value, 10);
        } else if (std.mem.eql(u8, name, "--burst-length")) {
            parsed.burst_length = try std.fmt.parseInt(usize, value, 10);
        } else if (std.mem.eql(u8, name, "--affinity-mask")) {
            parsed.affinity_mask = try std.fmt.parseInt(usize, value, 0);
        } else {
            return error.UnknownArgument;
        }
    }

    const run_mode = parsed.mode orelse return error.MissingRunMode;
    const samples = if (run_mode == .timing)
        (parsed.samples orelse return error.MissingSamples)
    else
        (parsed.samples orelse 0);
    const options = Options{
        .candidate = parsed.candidate orelse return error.MissingCandidate,
        .uid_bits = parsed.uid_bits orelse return error.MissingUidWidth,
        .capacity = parsed.capacity orelse return error.MissingCapacity,
        .path = parsed.path orelse return error.MissingPath,
        .measurement = parsed.measurement,
        .state = parsed.state orelse return error.MissingState,
        .trace = parsed.trace orelse return error.MissingTrace,
        .cache = parsed.cache,
        .mode = run_mode,
        .iterations = parsed.iterations orelse return error.MissingIterations,
        .samples = samples,
        .warmup = parsed.warmup,
        .seed = parsed.seed,
        .burst_length = parsed.burst_length,
        .affinity_mask = parsed.affinity_mask,
    };
    if (options.uid_bits != 128 and options.uid_bits != 256) return error.InvalidUidWidth;
    if (options.capacity != 1024 and options.capacity != 1_048_576) return error.InvalidCapacity;
    if (options.iterations == 0 or options.mode == .timing and options.samples == 0 or options.mode == .correctness and options.samples != 0)
        return error.InvalidIterationCount;
    if (options.trace == .burst and options.burst_length == 0) return error.InvalidBurstLength;
    if (options.state == .hit and options.trace == .single) return error.HitNeedsMultiKeyTrace;
    if (options.state != .hit and options.trace != .single) return error.NonHitNeedsSingleTrace;
    if (options.measurement == .index_only and options.path == .constant_fixed) return error.IndexOnlyRequiresRuntimeUidPath;
    if (options.affinity_mask) |mask| {
        if (mask == 0 or mask & (mask - 1) != 0) return error.AffinityRequiresOneProcessor;
    }
    if (options.mode == .timing and options.affinity_mask == null) return error.TimingRequiresExplicitAffinity;
    if (compiled_candidate == .both and options.mode == .timing) return error.TimingRequiresSingleCandidateBuild;
    if (compiled_candidate == .c2 and options.candidate != .c2 or
        compiled_candidate == .b2 and options.candidate != .b2) return error.CandidateBuildMismatch;
    return options;
}

fn dispatchProfile(
    comptime candidate: CandidateKind,
    allocator: std.mem.Allocator,
    io: std.Io,
    options: Options,
) !void {
    if (options.uid_bits == 128) {
        if (options.capacity == 1024) {
            try Driver(128, 1024, candidate).run(allocator, io, options, options.path);
        } else {
            try Driver(128, 1_048_576, candidate).run(allocator, io, options, options.path);
        }
    } else if (options.capacity == 1024) {
        try Driver(256, 1024, candidate).run(allocator, io, options, options.path);
    } else {
        try Driver(256, 1_048_576, candidate).run(allocator, io, options, options.path);
    }
}

fn dispatchForBuild(
    comptime built: CompiledCandidate,
    allocator: std.mem.Allocator,
    io: std.Io,
    options: Options,
) !void {
    if (built == .both) {
        if (options.mode == .timing) return error.TimingRequiresSingleCandidateBuild;
        switch (options.candidate) {
            .c2 => try dispatchProfile(.c2, allocator, io, options),
            .b2 => try dispatchProfile(.b2, allocator, io, options),
        }
    } else if (built == .c2) {
        if (options.candidate != .c2) return error.CandidateBuildMismatch;
        try dispatchProfile(.c2, allocator, io, options);
    } else {
        if (options.candidate != .b2) return error.CandidateBuildMismatch;
        try dispatchProfile(.b2, allocator, io, options);
    }
}

fn dispatch(allocator: std.mem.Allocator, io: std.Io, options: Options) !void {
    try dispatchForBuild(compiled_candidate, allocator, io, options);
}

pub fn main(init: std.process.Init) !void {
    const allocator = init.gpa;
    const args = try init.minimal.args.toSlice(init.arena.allocator());
    var options = try parseOptions(args);
    if (options.mode == .timing and builtin.mode != .ReleaseFast) return error.TimingRequiresReleaseFast;
    if (options.affinity_mask) |mask| options.placement = try benchmark_affinity.apply(mask);
    try dispatch(allocator, init.io, options);
}
