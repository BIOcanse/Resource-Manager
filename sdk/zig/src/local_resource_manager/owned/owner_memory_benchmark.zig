//! Standalone payload allocation and update benchmark for the owned ledger.
//!
//! Each process uses one index candidate, one UID width, and one declared
//! payload scale. Correctness mode records no timings. Timing mode requires a
//! ReleaseFast, single-candidate build and an explicit one-processor mask.
const std = @import("std");
const builtin = @import("builtin");
const uid_module = @import("../uid.zig");
const memory_region = @import("../memory_region.zig");
const c2_index = @import("index.zig");
const b2_index = @import("index_b2.zig");
const notifications = @import("notifications.zig");
const owner_module = @import("owner.zig");
const types = @import("types.zig");
const values = @import("ledger_values").Bound;
const benchmark_affinity = @import("benchmark_affinity.zig");
const benchmark_options = @import("benchmark_options");

const resource_capacity: usize = 8;
const index_bucket_count: usize = resource_capacity * 2;
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

const RunMode = enum { correctness, timing };
const PayloadScale = enum { small, medium, large };

const Options = struct {
    candidate: CandidateKind,
    uid_bits: u16,
    payload_scale: PayloadScale,
    mode: RunMode,
    samples: usize,
    affinity_mask: ?usize,
    placement: ?benchmark_affinity.Placement = null,
};

const ParsedOptions = struct {
    candidate: ?CandidateKind = null,
    uid_bits: ?u16 = null,
    payload_scale: ?PayloadScale = null,
    mode: ?RunMode = null,
    samples: ?usize = null,
    affinity_mask: ?usize = null,
};

const Timings = struct {
    structural_bind_push_ns: u64,
    structural_bind_discard_ns: u64,
    isolated_region_allocate_ns: u64,
    isolated_region_first_zero_scan_ns: u64,
    isolated_region_release_ns: u64,
    owner_nonzero_push_ns: u64,
    owner_full_zero_scan_ns: u64,
    initial_user_write_ns: u64,
    loading_publish_ns: u64,
    unique_holder_acquire_ns: u64,
    copy_migration_update_ns: u64,
    migration_verify_ns: u64,
    growth_user_write_ns: u64,
    final_end_use_ns: u64,
    owner_close_page_release_ns: u64,
};

const Verification = struct {
    initial_payload_bytes: u64,
    grown_payload_bytes: u64,
    initial_page_bytes: usize,
    grown_page_bytes: usize,
    update_peak_old_plus_new_page_bytes: usize,
    metadata_and_scratch_bytes: usize,
    initial_zero_bytes_checked: usize,
    growth_zero_bytes_checked: usize,
    final_payload_checksum: u64,
    structural_binding_used_zero_pages: bool,
    closed_registered_bytes: u64,
    closed_system_page_bytes: u64,
};

const SampleRecord = struct {
    sample_index: usize,
    timings_ns: ?Timings,
    verification: Verification,
};

const OutputRecord = struct {
    schema: []const u8 = "resource-manager/owned-memory-payload-benchmark/v1",
    passed: bool = true,
    candidate: []const u8,
    compiled_candidate: []const u8,
    uid_width_bits: u16,
    payload_scale: []const u8,
    resource_capacity: usize = resource_capacity,
    partition_capacity: usize = 0,
    index_nodes: usize = resource_capacity,
    index_buckets: usize = index_bucket_count,
    fixed_declarations: usize = 0,
    policy_source: []const u8 = "ledger_values.Bound.table_0",
    run_mode: []const u8,
    build_mode: []const u8,
    warmup_count: usize,
    timing_requires_release_fast_single_candidate_and_affinity: bool = true,
    setup_timed: bool = false,
    payload_sizes_are: []const u8 = "small=64KiB, medium=1MiB, large=8MiB; growth adds one page plus one byte",
    timing_boundaries: []const u8 = "zero-byte owner binding; isolated Region allocate/first-touch/release; nonzero owner push; full zero scan; initial write; endUse complete publication; unique-holder page-moving update; prefix/tail verification; growth write; final endUse; normal owner close and page release",
    payload_peak_model: []const u8 = "update allocation overlaps old and new committed page ranges until copy completes; report records old_page_bytes + new_page_bytes",
    samples: []const SampleRecord,
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

fn payloadBytes(scale: PayloadScale) u64 {
    return switch (scale) {
        .small => 64 * 1024,
        .medium => 1024 * 1024,
        .large => 8 * 1024 * 1024,
    };
}

fn patternByte(offset: usize) u8 {
    return @intCast(offset % 251 + 1);
}

fn fillPattern(bytes: []u8, start_offset: usize) void {
    for (bytes, 0..) |*byte, index| byte.* = patternByte(start_offset + index);
}

fn verifyZero(bytes: []const u8) !void {
    for (bytes) |byte| {
        if (byte != 0) return error.NonzeroNewMemory;
    }
}

fn verifyPattern(bytes: []const u8, start_offset: usize) !u64 {
    var checksum: u64 = 0;
    for (bytes, 0..) |byte, index| {
        if (byte != patternByte(start_offset + index)) return error.PayloadPatternMismatch;
        checksum +%= byte;
    }
    return checksum;
}

fn startClock(io: std.Io, enabled: bool) i96 {
    return if (enabled) std.Io.Clock.awake.now(io).nanoseconds else 0;
}

fn elapsedNs(io: std.Io, enabled: bool, started: i96) u64 {
    if (!enabled) return 0;
    const elapsed = std.Io.Clock.awake.now(io).nanoseconds - started;
    return if (elapsed <= 0) 0 else @intCast(elapsed);
}

fn TransactionBuffers(comptime Index: type) type {
    const Transactions = Index.Transactions;
    const Workspace = Transactions.Workspace;
    const Storage = if (@hasField(Workspace, "operations")) struct {
        operations: []Transactions.Operation = &.{},
        segments: []Transactions.Segment = &.{},
        indices: []u32 = &.{},
        versions: []u64 = &.{},
        removals: []Index.RemovePlan = &.{},
        simulation_nodes: []u32 = &.{},
        simulation_uids: []Index.uid_type = &.{},
        simulation_node_ids: []u32 = &.{},
        simulation_backlinks: []u32 = &.{},
    } else struct {
        homes: []u32 = &.{},
        nodes: []u32 = &.{},
        publications: []Index.Publication = &.{},
        removals: []Index.RemovePlan = &.{},
    };

    return struct {
        const Self = @This();
        storage: Storage = .{},
        workspace: Workspace = .{},

        pub fn create(allocator: std.mem.Allocator) !Self {
            var result = Self{};
            errdefer result.deinit(allocator);
            if (comptime @hasField(Workspace, "operations")) {
                result.storage.operations = try allocator.alloc(Transactions.Operation, 1);
                result.storage.segments = try allocator.alloc(Transactions.Segment, 2);
                result.storage.indices = try allocator.alloc(u32, index_bucket_count);
                result.storage.versions = try allocator.alloc(u64, index_bucket_count);
                result.storage.removals = try allocator.alloc(Index.RemovePlan, 1);
                result.storage.simulation_nodes = try allocator.alloc(u32, index_bucket_count);
                result.storage.simulation_uids = try allocator.alloc(Index.uid_type, index_bucket_count);
                result.storage.simulation_node_ids = try allocator.alloc(u32, index_bucket_count + 1);
                result.storage.simulation_backlinks = try allocator.alloc(u32, index_bucket_count + 1);
                result.workspace = .{
                    .operations = result.storage.operations,
                    .segments = result.storage.segments,
                    .indices = result.storage.indices,
                    .versions = result.storage.versions,
                    .removals = result.storage.removals,
                    .inserts = &.{},
                    .simulation_nodes = result.storage.simulation_nodes,
                    .simulation_uids = result.storage.simulation_uids,
                    .simulation_node_ids = result.storage.simulation_node_ids,
                    .simulation_backlinks = result.storage.simulation_backlinks,
                };
            } else {
                result.storage.homes = try allocator.alloc(u32, 1);
                result.storage.nodes = try allocator.alloc(u32, 1);
                result.storage.publications = try allocator.alloc(Index.Publication, 1);
                result.storage.removals = try allocator.alloc(Index.RemovePlan, 1);
                result.workspace = .{
                    .homes = result.storage.homes,
                    .nodes = result.storage.nodes,
                    .publications = result.storage.publications,
                    .removals = result.storage.removals,
                    .inserts = &.{},
                };
            }
            return result;
        }

        pub fn deinit(self: *Self, allocator: std.mem.Allocator) void {
            inline for (@typeInfo(Storage).@"struct".field_names) |field_name| {
                const slice = @field(self.storage, field_name);
                if (slice.len != 0) allocator.free(slice);
            }
            self.* = .{};
        }

        pub fn byteCount(self: *const Self) usize {
            if (comptime @hasField(Workspace, "operations")) {
                return self.storage.operations.len * @sizeOf(Transactions.Operation) +
                    self.storage.segments.len * @sizeOf(Transactions.Segment) +
                    self.storage.indices.len * @sizeOf(u32) +
                    self.storage.versions.len * @sizeOf(u64) +
                    self.storage.removals.len * @sizeOf(Index.RemovePlan) +
                    self.storage.simulation_nodes.len * @sizeOf(u32) +
                    self.storage.simulation_uids.len * @sizeOf(Index.uid_type) +
                    self.storage.simulation_node_ids.len * @sizeOf(u32) +
                    self.storage.simulation_backlinks.len * @sizeOf(u32);
            }
            return self.storage.homes.len * @sizeOf(u32) +
                self.storage.nodes.len * @sizeOf(u32) +
                self.storage.publications.len * @sizeOf(Index.Publication) +
                self.storage.removals.len * @sizeOf(Index.RemovePlan);
        }
    };
}

fn Driver(comptime bits: u16, comptime candidate: CandidateKind) type {
    const Uid = uid_module.Identifier(bits);
    const Index = if (candidate == .c2)
        c2_index.Candidate(Uid, .c2_chain)
    else
        b2_index.Candidate(Uid, .b2_ordered);
    const Fixed = types.For(Uid).Fixed;
    const table_declarations = [_]types.Table{
        .{
            .capacity = @intCast(resource_capacity),
            .partition_capacity = 0,
            .reserved_start = 0,
            .reserved_count = 0,
            .attributes = attributes(),
            .policy = policy(),
        },
    };
    const fixed_declarations: [0]Fixed = .{};
    const Ledger = owner_module.Owner(
        .{
            .uid_width = bits,
            .activity_width = @intCast(values.activity_bitmap_width),
            .index_buckets = @intCast(index_bucket_count),
        },
        &table_declarations,
        &fixed_declarations,
        Index,
    );
    const Notifier = notifications.Notifier(Uid);
    const IndexStorage = if (comptime candidate == .c2)
        struct { items: [index_bucket_count]Index.Home = undefined }
    else
        struct { items: [index_bucket_count]Index.Bucket = undefined };

    return struct {
        fn metadataBytes(tx: *const TransactionBuffers(Index)) usize {
            const index_payload_bytes = if (comptime candidate == .c2)
                index_bucket_count * @sizeOf(Index.Home)
            else
                index_bucket_count * @sizeOf(Index.Bucket);
            return @sizeOf(Ledger) +
                resource_capacity * @sizeOf(Ledger.Row) +
                @sizeOf(Ledger.TableState) +
                resource_capacity * @sizeOf(Index.Node) +
                index_payload_bytes +
                resource_capacity * @sizeOf(Ledger.Candidate) * 2 +
                @sizeOf(Ledger.Workspace) +
                @sizeOf(memory_region.Region) +
                2 * @sizeOf(u32) +
                @sizeOf([]Notifier.Record) +
                @sizeOf(@TypeOf(table_declarations)) +
                tx.byteCount();
        }

        fn runSample(
            allocator: std.mem.Allocator,
            io: std.Io,
            options: Options,
            sample_index: usize,
            measure: bool,
        ) !SampleRecord {
            var rows: [resource_capacity]Ledger.Row = undefined;
            var shells: [0]Ledger.Shell = .{};
            var table_states: [1]Ledger.TableState = undefined;
            var bindings: [0]Ledger.FixedBinding = .{};
            var nodes: [resource_capacity]Index.Node = undefined;
            var index_storage: IndexStorage = .{};
            var records: [0]Notifier.Record = .{};
            const subscriber_rows = [_][]Notifier.Record{records[0..]};
            var candidate_scratch: [resource_capacity]Ledger.Candidate = undefined;
            var row_scratch: [resource_capacity]Ledger.Candidate = undefined;
            var owner_home_scratch: [1]u32 = undefined;
            var remove_ids: [1]u32 = undefined;
            var no_shell_scratch: [0]u32 = .{};
            var no_notes: [0]Ledger.api.Notification = .{};

            var tx_buffers = try TransactionBuffers(Index).create(allocator);
            defer tx_buffers.deinit(allocator);
            var work = Ledger.Workspace{
                .candidates = &candidate_scratch,
                .rows = &row_scratch,
                .shells = no_shell_scratch[0..],
                .homes = &owner_home_scratch,
                .notes = no_notes[0..],
                .remove_ids = &remove_ids,
                .puts = &.{},
                .index_workspace = tx_buffers.workspace,
            };

            var ledger: Ledger = undefined;
            try Ledger.init(&ledger, rows[0..], shells[0..], table_states[0..], bindings[0..], nodes[0..], index_storage.items[0..], subscriber_rows[0..]);
            defer {
                for (&rows) |*row| row.resource.forceRelease() catch @panic("payload benchmark cleanup failed");
            }

            var timing = Timings{
                .structural_bind_push_ns = 0,
                .structural_bind_discard_ns = 0,
                .isolated_region_allocate_ns = 0,
                .isolated_region_first_zero_scan_ns = 0,
                .isolated_region_release_ns = 0,
                .owner_nonzero_push_ns = 0,
                .owner_full_zero_scan_ns = 0,
                .initial_user_write_ns = 0,
                .loading_publish_ns = 0,
                .unique_holder_acquire_ns = 0,
                .copy_migration_update_ns = 0,
                .migration_verify_ns = 0,
                .growth_user_write_ns = 0,
                .final_end_use_ns = 0,
                .owner_close_page_release_ns = 0,
            };
            const initial_size = payloadBytes(options.payload_scale);
            const initial_len = std.math.cast(usize, initial_size) orelse return error.PayloadSizeOverflow;
            const page_size = std.heap.pageSize();
            const growth_delta = std.math.add(usize, page_size, 1) catch return error.PayloadSizeOverflow;
            const grown_size = std.math.add(u64, initial_size, @intCast(growth_delta)) catch return error.PayloadSizeOverflow;
            const grown_len = std.math.cast(usize, grown_size) orelse return error.PayloadSizeOverflow;
            const initial_page_bytes = try memory_region.requiredBytes(initial_size);
            const grown_page_bytes = try memory_region.requiredBytes(grown_size);
            const peak_update_page_bytes = std.math.add(usize, initial_page_bytes, grown_page_bytes) catch return error.PayloadSizeOverflow;
            const captured_metadata_bytes = metadataBytes(&tx_buffers);

            // A zero-byte Owner.push isolates initial UID binding and row
            // registration. It is discarded without being counted as payload
            // loading or allocation performance.
            const binding_uid = key(Uid, 1);
            const bind_started = startClock(io, measure);
            const binding_result = try ledger.push(.{ .table = 0 }, binding_uid, 0, &work);
            timing.structural_bind_push_ns = elapsedNs(io, measure, bind_started);
            const binding = switch (binding_result) {
                .newly_allocated => |borrow| borrow,
                .in_memory, .loading => return error.StructuralBindWasNotFresh,
            };
            if (binding.region.len != 0) return error.ZeroBindingHadPayload;
            const binding_view = try ledger.view(0);
            if (binding_view.registered_bytes != 0 or binding_view.system_page_bytes != 0)
                return error.ZeroBindingAllocatedPages;
            const discard_started = startClock(io, measure);
            if (try ledger.endUse(binding.lease, .discard) != .completed) return error.StructuralBindingDiscardFailed;
            timing.structural_bind_discard_ns = elapsedNs(io, measure, discard_started);
            switch (try ledger.use(binding_uid)) {
                .not_in_memory => {},
                .in_memory, .loading => return error.DiscardedBindingStillVisible,
            }

            // This control calls the production page-region allocator directly,
            // so OS allocation, first page touch, and release have distinct
            // boundaries. The direct region is released before Owner.push.
            var region_control = memory_region.Region{};
            defer region_control.release() catch @panic("isolated region control cleanup failed");
            const control_alloc_started = startClock(io, measure);
            region_control = try memory_region.Region.allocate(initial_size);
            timing.isolated_region_allocate_ns = elapsedNs(io, measure, control_alloc_started);
            const control_touch_started = startClock(io, measure);
            try verifyZero(region_control.bytes());
            timing.isolated_region_first_zero_scan_ns = elapsedNs(io, measure, control_touch_started);
            const control_release_started = startClock(io, measure);
            try region_control.release();
            timing.isolated_region_release_ns = elapsedNs(io, measure, control_release_started);

            const payload_uid = key(Uid, 2);
            const push_started = startClock(io, measure);
            const push_result = try ledger.push(.{ .table = 0 }, payload_uid, initial_size, &work);
            timing.owner_nonzero_push_ns = elapsedNs(io, measure, push_started);
            const fresh = switch (push_result) {
                .newly_allocated => |borrow| borrow,
                .in_memory => return error.PayloadPushWasNotFresh,
                .loading => return error.PayloadPushAlreadyLoading,
            };
            if (fresh.region.len != initial_len) return error.InitialPayloadLengthMismatch;
            const initial_view = try ledger.view(0);
            if (initial_view.registered_bytes != initial_size or initial_view.system_page_bytes != @as(u64, @intCast(initial_page_bytes)))
                return error.InitialOwnerAccountingMismatch;

            const zero_scan_started = startClock(io, measure);
            try verifyZero(fresh.region);
            timing.owner_full_zero_scan_ns = elapsedNs(io, measure, zero_scan_started);
            const initial_write_started = startClock(io, measure);
            fillPattern(fresh.region, 0);
            timing.initial_user_write_ns = elapsedNs(io, measure, initial_write_started);

            const publish_started = startClock(io, measure);
            if (try ledger.endUse(fresh.lease, .complete) != .completed) return error.InitialLoadingPublishFailed;
            timing.loading_publish_ns = elapsedNs(io, measure, publish_started);

            const use_started = startClock(io, measure);
            const held = switch (try ledger.use(payload_uid)) {
                .in_memory => |borrow| borrow,
                .not_in_memory => return error.PayloadNotResidentAfterPublish,
                .loading => return error.PayloadStillLoadingAfterPublish,
            };
            timing.unique_holder_acquire_ns = elapsedNs(io, measure, use_started);
            if (held.allocation_number != fresh.allocation_number) return error.PayloadNumberChangedBeforeUpdate;

            const update_started = startClock(io, measure);
            const update_result = try ledger.updateLease(held.lease, .{ .allocation_bytes = grown_size });
            timing.copy_migration_update_ns = elapsedNs(io, measure, update_started);
            const grown_region = switch (update_result) {
                .updated => |maybe_bytes| maybe_bytes orelse return error.MigrationDidNotReturnRegion,
                .registration_only => return error.MigrationWasNotResident,
                .in_use => return error.MigrationRejectedUniqueHolder,
                .force_released => return error.MigrationLeaseWasReleased,
            };
            if (grown_region.len != grown_len) return error.GrownPayloadLengthMismatch;
            const grown_view = try ledger.view(0);
            if (grown_view.registered_bytes != grown_size or grown_view.system_page_bytes != @as(u64, @intCast(grown_page_bytes)))
                return error.GrownOwnerAccountingMismatch;

            const verify_started = startClock(io, measure);
            _ = try verifyPattern(grown_region[0..initial_len], 0);
            try verifyZero(grown_region[initial_len..]);
            timing.migration_verify_ns = elapsedNs(io, measure, verify_started);
            const growth_write_started = startClock(io, measure);
            fillPattern(grown_region[initial_len..], initial_len);
            timing.growth_user_write_ns = elapsedNs(io, measure, growth_write_started);
            const final_checksum = try verifyPattern(grown_region, 0);

            const final_end_started = startClock(io, measure);
            if (try ledger.endUse(held.lease, .complete) != .completed) return error.FinalLeaseDidNotEnd;
            timing.final_end_use_ns = elapsedNs(io, measure, final_end_started);
            const close_started = startClock(io, measure);
            try ledger.close(payload_uid, &work);
            timing.owner_close_page_release_ns = elapsedNs(io, measure, close_started);
            const closed_view = try ledger.view(0);
            if (closed_view.registered_bytes != 0 or closed_view.system_page_bytes != 0)
                return error.CloseDidNotReturnPayloadPages;
            switch (try ledger.use(payload_uid)) {
                .not_in_memory => {},
                .in_memory, .loading => return error.ClosedPayloadStillVisible,
            }

            return .{
                .sample_index = sample_index,
                .timings_ns = if (measure) timing else null,
                .verification = .{
                    .initial_payload_bytes = initial_size,
                    .grown_payload_bytes = grown_size,
                    .initial_page_bytes = initial_page_bytes,
                    .grown_page_bytes = grown_page_bytes,
                    .update_peak_old_plus_new_page_bytes = peak_update_page_bytes,
                    .metadata_and_scratch_bytes = captured_metadata_bytes,
                    .initial_zero_bytes_checked = initial_len,
                    .growth_zero_bytes_checked = grown_len - initial_len,
                    .final_payload_checksum = final_checksum,
                    .structural_binding_used_zero_pages = true,
                    .closed_registered_bytes = closed_view.registered_bytes,
                    .closed_system_page_bytes = closed_view.system_page_bytes,
                },
            };
        }

        pub fn run(allocator: std.mem.Allocator, io: std.Io, options: Options) !void {
            const sample_count = if (options.mode == .timing) options.samples else 1;
            const samples = try allocator.alloc(SampleRecord, sample_count);
            defer allocator.free(samples);
            if (options.mode == .timing) {
                _ = try runSample(allocator, io, options, 0, false);
            }
            for (samples, 0..) |*sample, index| {
                sample.* = try runSample(allocator, io, options, index, options.mode == .timing);
            }
            const record = OutputRecord{
                .candidate = @tagName(candidate),
                .compiled_candidate = @tagName(compiled_candidate),
                .uid_width_bits = bits,
                .payload_scale = @tagName(options.payload_scale),
                .run_mode = @tagName(options.mode),
                .build_mode = @tagName(builtin.mode),
                .warmup_count = if (options.mode == .timing) 1 else 0,
                .samples = samples,
                .affinity = options.placement,
            };
            try writeJson(io, allocator, record);
        }
    };
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
        } else if (std.mem.eql(u8, name, "--size")) {
            if (std.mem.eql(u8, value, "small")) parsed.payload_scale = .small else if (std.mem.eql(u8, value, "medium")) parsed.payload_scale = .medium else if (std.mem.eql(u8, value, "large")) parsed.payload_scale = .large else return error.InvalidPayloadScale;
        } else if (std.mem.eql(u8, name, "--mode")) {
            if (std.mem.eql(u8, value, "correctness")) parsed.mode = .correctness else if (std.mem.eql(u8, value, "timing")) parsed.mode = .timing else return error.InvalidRunMode;
        } else if (std.mem.eql(u8, name, "--samples")) {
            parsed.samples = try std.fmt.parseInt(usize, value, 10);
        } else if (std.mem.eql(u8, name, "--affinity-mask")) {
            parsed.affinity_mask = try std.fmt.parseInt(usize, value, 0);
        } else {
            return error.UnknownArgument;
        }
    }
    const mode = parsed.mode orelse return error.MissingRunMode;
    const candidate = parsed.candidate orelse return error.MissingCandidate;
    const bits = parsed.uid_bits orelse return error.MissingUidWidth;
    if (bits != 128 and bits != 256) return error.InvalidUidWidth;
    const samples = if (mode == .timing) parsed.samples orelse return error.MissingSamples else 0;
    if (mode == .timing and (samples == 0 or samples > 20)) return error.InvalidSampleCount;
    if (mode == .correctness and parsed.samples != null) return error.CorrectnessModeDoesNotAcceptSamples;
    if (parsed.affinity_mask) |mask| {
        if (mask == 0 or mask & (mask - 1) != 0) return error.AffinityRequiresOneProcessor;
    }
    if (mode == .timing and parsed.affinity_mask == null) return error.TimingRequiresExplicitAffinity;
    if (compiled_candidate == .both and mode == .timing) return error.TimingRequiresSingleCandidateBuild;
    if (compiled_candidate == .c2 and candidate != .c2 or compiled_candidate == .b2 and candidate != .b2)
        return error.CandidateBuildMismatch;
    return .{
        .candidate = candidate,
        .uid_bits = bits,
        .payload_scale = parsed.payload_scale orelse return error.MissingPayloadScale,
        .mode = mode,
        .samples = samples,
        .affinity_mask = parsed.affinity_mask,
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

fn dispatchCandidate(comptime candidate: CandidateKind, options: Options, allocator: std.mem.Allocator, io: std.Io) !void {
    if (options.uid_bits == 128) {
        try Driver(128, candidate).run(allocator, io, options);
    } else {
        try Driver(256, candidate).run(allocator, io, options);
    }
}

fn dispatchForBuild(
    comptime built: CompiledCandidate,
    options: Options,
    allocator: std.mem.Allocator,
    io: std.Io,
) !void {
    if (built == .c2) {
        if (options.candidate != .c2) return error.CandidateBuildMismatch;
        try dispatchCandidate(.c2, options, allocator, io);
    } else if (built == .b2) {
        if (options.candidate != .b2) return error.CandidateBuildMismatch;
        try dispatchCandidate(.b2, options, allocator, io);
    } else {
        if (options.mode == .timing) return error.TimingRequiresSingleCandidateBuild;
        switch (options.candidate) {
            .c2 => try dispatchCandidate(.c2, options, allocator, io),
            .b2 => try dispatchCandidate(.b2, options, allocator, io),
        }
    }
}

fn dispatch(options: Options, allocator: std.mem.Allocator, io: std.Io) !void {
    try dispatchForBuild(compiled_candidate, options, allocator, io);
}

pub fn main(init: std.process.Init) !void {
    const allocator = init.gpa;
    const args = try init.minimal.args.toSlice(init.arena.allocator());
    var options = try parseOptions(args);
    if (options.mode == .timing and builtin.mode != .fast) return error.TimingRequiresReleaseFast;
    if (options.affinity_mask) |mask| options.placement = try benchmark_affinity.apply(mask);
    try dispatch(options, allocator, init.io);
}
