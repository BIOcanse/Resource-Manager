//! Standalone owner concurrency comparison driver.
//! Fixture setup is separate from the reported operation intervals.
const std = @import("std");
const builtin = @import("builtin");
const uid_module = @import("../uid.zig");
const c2_index = @import("index.zig");
const b2_index = @import("index_b2.zig");
const notifications = @import("notifications.zig");
const owner_module = @import("owner.zig");
const types = @import("types.zig");
const memory_region = @import("../memory_region.zig");
const values = @import("ledger_values").Bound;
const benchmark_options = @import("benchmark_options");

const CandidateKind = enum { c2, b2 };
const CompiledCandidate = enum { both, c2, b2 };
const Scenario = enum {
    table_write_unrelated_use,
    full_audit_unrelated_use,
    capacity_scan_unrelated_use,
    release_unrelated_use,
    notification_unrelated_use,
    same_home_publication,
};
const RunMode = enum { correctness, timing };
const HomeZone = enum { any, avoid_one, avoid_reader_window };

const compiled_candidate: CompiledCandidate = if (std.mem.eql(u8, @tagName(benchmark_options.candidate), "both"))
    .both
else if (std.mem.eql(u8, @tagName(benchmark_options.candidate), "c2"))
    .c2
else if (std.mem.eql(u8, @tagName(benchmark_options.candidate), "b2"))
    .b2
else
    @compileError("benchmark_options.candidate must be both, c2, or b2");

const samples_for_timing: usize = 5;
const samples_for_correctness: usize = 5;
const warmup_uses_per_sample: usize = 128;
const minimum_reader_uses: usize = 256;
const maximum_reader_uses: usize = 16_384;
const fixed_notification_reader_uses: usize = minimum_reader_uses;
const writer_operations_per_sample: usize = 16;
const same_home_rounds_per_sample: usize = 16;
const reader_poll_limit: usize = 10_000_000;
const uid_domain: u8 = 0x52;
const reader_home_exclusion_radius: usize = 128;

const Options = struct {
    candidate: CandidateKind,
    scenario: Scenario,
    uid_bits: u16,
    capacity: usize,
    mode: RunMode,
    samples: usize,
    affinity_mask: ?usize,
};

const Interval = struct {
    start_ns: i96,
    end_ns: i96,
    completed: bool = true,
    table0_lock_at_start: bool = false,
    table0_lock_at_end: bool = false,
};

const WorkerCpu = struct {
    requested_mask: usize = 0,
    thread_pinned: bool = false,
    entry_group: u16 = 0,
    entry_processor: u8 = 0,
    exit_group: u16 = 0,
    exit_processor: u8 = 0,
    entry_matches_assignment: ?bool = null,
    exit_matches_assignment: ?bool = null,
};

const ProcessPlacement = struct {
    requested_mask: usize,
    before_process_mask: usize,
    after_process_mask: usize,
    system_mask: usize,
    active_group_count: u16,
    processor_group: u16 = 0,
    first_processor: u8,
    second_processor: u8,
    topology: []const u8,
};

const AuditSummary = struct {
    resources: usize,
    tables: usize,
    active_resources: usize,
    index_home_body: usize,
    index_node_body: usize,
    index_neighbor_payload_loads: usize,
};

const TickSummary = struct {
    released: usize,
    scanned: usize,
    expected_table0_rows: usize,
    expected_total_scanned_rows: usize,
    reader_guard_lease_held: bool,
    reader_bucket_isolated_at_setup: ?bool,
    reader_bucket_isolated_after_warm_tick: ?bool,
    reader_bucket_isolated_before: ?bool,
    reader_bucket_isolated_after: ?bool,
    table0_write_held_completion_count: usize,
    reader_home_version_before: u64,
    reader_home_version_after: u64,
};

const SameHomeSummary = struct {
    home: u32,
    uid_a_hex: []const u8,
    uid_b_hex: []const u8,
};

const SampleRecord = struct {
    sample_index: usize,
    sample_start_ns: i96,
    sample_end_ns: i96,
    operation_intervals: []const Interval,
    second_operation_intervals: []const Interval,
    reader_intervals: []const Interval,
    reader_use_count: usize,
    reader_writer_done_at_stop: ?bool,
    reader_cap_reached: bool,
    reader_stop_reason: []const u8,
    callback_interval: ?Interval = null,
    callback_timed_out: bool = false,
    audit: ?AuditSummary = null,
    tick: ?TickSummary = null,
    operation_reader_overlap_count: usize,
    reader_completed_during_operation_count: usize,
    left_right_operation_overlap_count: usize,
    callback_reader_overlap_count: usize,
    callback_reader_completion_count: usize,
    overlap_accepted: bool,
    overlap_rejection_reason: ?[]const u8 = null,
    writer_cpu: WorkerCpu,
    reader_cpu: WorkerCpu,
    second_writer_cpu: WorkerCpu = .{},
    checksum: u64,
};

const MetadataBytes = struct {
    owner_header: usize,
    owner_rows: usize,
    owner_table_states: usize,
    owner_subscriber_records: usize,
    index_nodes: usize,
    index_homes_or_buckets: usize,
    owner_index_arrays_total: usize,
    tick_candidate_workspace: usize,
    audit_workspace: usize,
    worker_workspace_headers: usize,
    worker_interval_scratch_peak: usize,
    interval_size: usize,
    sample_header_size: usize,
    output_interval_storage: usize,
};

const OutputRecord = struct {
    schema: []const u8 = "resource-manager/owner-concurrent-benchmark/v2",
    workload_revision: u8,
    reader_minimum_uses_per_sample: usize,
    reader_maximum_uses_per_sample: usize,
    candidate: []const u8,
    scenario: []const u8,
    uid_width_bits: u16,
    resource_capacity: usize,
    table_resource_capacities: [4]usize,
    partition_capacity: usize = 0,
    index_node_capacity: usize,
    index_home_or_bucket_capacity: usize,
    indexed_entries_after_setup: usize,
    reader_table: ?u32,
    reader_home: ?u32,
    same_home_uid_pair: ?SameHomeSummary,
    capacity_scan_prepopulated_rows: usize = 0,
    capacity_scan_guard_lease_count: usize = 0,
    reader_home_exclusion_radius: usize = 0,
    reader_bucket_isolated_at_setup: ?bool = null,
    reader_bucket_isolated_after_warm_tick: ?bool = null,
    mode: []const u8,
    build_mode: []const u8,
    cache_mode: []const u8 = "warm",
    cache_cold_claim: bool = false,
    timing_boundary: []const u8,
    process_affinity: ?ProcessPlacement,
    metadata_bytes: MetadataBytes,
    tick_policy_trigger_percent: u8,
    tick_policy_target_percent: u8,
    tick_policy_maximum_actions: usize,
    samples: []const SampleRecord,
    all_samples_retained: bool = true,
    filtering_or_replacement_applied: bool = false,
};

fn attributes() types.Attributes {
    return .{
        .recoverability = .recoverable,
        .recovery_cost_coefficient = 1,
        .access_loss_impact = .unobservable_now,
    };
}

fn productionPolicy() types.Policy {
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

fn key(comptime Uid: type, domain: u8, value: u64) Uid {
    var result = Uid{ .bytes = @splat(0) };
    result.bytes[0] = domain;
    std.mem.writeInt(u64, result.bytes[result.bytes.len - 8 ..][0..8], value, .big);
    return result;
}

fn UidGenerator(comptime Uid: type) type {
    return struct {
        next_value: u64 = 1,

        fn next(self: *@This(), domain: u8, bucket_count: usize, zone: HomeZone, excluded_home: ?u32) !Uid {
            const attempt_limit = std.math.add(usize, bucket_count * 16, 1024) catch return error.UidSearchBoundOverflow;
            var attempt: usize = 0;
            while (attempt < attempt_limit) : (attempt += 1) {
                const value = self.next_value;
                self.next_value = std.math.add(u64, self.next_value, 1) catch return error.UidSequenceExhausted;
                const uid = key(Uid, domain, value);
                const home: u32 = @intCast(uid.hash() % @as(u64, @intCast(bucket_count)));
                const accepted = switch (zone) {
                    .any => true,
                    .avoid_one => home != excluded_home.?,
                    .avoid_reader_window => !withinHomeRadius(home, excluded_home.?, bucket_count, reader_home_exclusion_radius),
                };
                if (accepted) return uid;
            }
            return error.UidSearchExhausted;
        }
    };
}

fn withinHomeRadius(home: u32, center: u32, bucket_count: usize, radius: usize) bool {
    const home_index: usize = @intCast(home);
    const center_index: usize = @intCast(center);
    const direct = if (home_index >= center_index) home_index - center_index else center_index - home_index;
    return @min(direct, bucket_count - direct) <= radius;
}

fn StartGate() type {
    return struct {
        ready: std.atomic.Value(u32) = std.atomic.Value(u32).init(0),
        open: std.atomic.Value(bool) = std.atomic.Value(bool).init(false),

        fn arriveAndWait(self: *@This()) void {
            _ = self.ready.fetchAdd(1, .acq_rel);
            while (!self.open.load(.acquire)) std.atomic.spinLoopHint();
        }
    };
}

fn nowNs(io: std.Io) i96 {
    return std.Io.Clock.awake.now(io).nanoseconds;
}

fn intervalOverlap(left: Interval, right: Interval) bool {
    return left.start_ns < right.end_ns and right.start_ns < left.end_ns;
}

fn intervalContained(inner: Interval, outer: Interval) bool {
    return inner.completed and inner.start_ns >= outer.start_ns and inner.end_ns <= outer.end_ns;
}

fn waitUntilReady(gate: anytype, expected: u32) bool {
    var attempts: usize = 0;
    while (gate.ready.load(.acquire) < expected and attempts < reader_poll_limit) : (attempts += 1) {
        std.Thread.yield() catch {};
    }
    return gate.ready.load(.acquire) >= expected;
}

fn waitForFlag(flag: *std.atomic.Value(bool)) bool {
    var attempts: usize = 0;
    while (!flag.load(.acquire) and attempts < reader_poll_limit) : (attempts += 1) {
        std.Thread.yield() catch {};
    }
    return flag.load(.acquire);
}

const ProcessorNumber = extern struct { group: u16, number: u8, reserved: u8 };
const GroupAffinity = extern struct { mask: usize, group: u16, reserved: [3]u16 };

extern "kernel32" fn GetCurrentProcess() callconv(.winapi) *anyopaque;
extern "kernel32" fn GetCurrentThread() callconv(.winapi) *anyopaque;
extern "kernel32" fn GetProcessAffinityMask(process: *anyopaque, process_mask: *usize, system_mask: *usize) callconv(.winapi) i32;
extern "kernel32" fn SetProcessAffinityMask(process: *anyopaque, mask: usize) callconv(.winapi) i32;
extern "kernel32" fn SetThreadAffinityMask(thread: *anyopaque, mask: usize) callconv(.winapi) usize;
extern "kernel32" fn GetActiveProcessorGroupCount() callconv(.winapi) u16;
extern "kernel32" fn GetCurrentProcessorNumberEx(number: *ProcessorNumber) callconv(.winapi) void;
extern "kernel32" fn GetLogicalProcessorInformationEx(relationship: u32, buffer: ?[*]u8, returned_length: *u32) callconv(.winapi) i32;
extern "kernel32" fn GetLastError() callconv(.winapi) u32;

const ProcessMaskReadback = struct {
    placement: ProcessPlacement,
    first_mask: usize,
    second_mask: usize,
};

fn relationContainsPair(allocator: std.mem.Allocator, relationship: u32, requested_mask: usize) !bool {
    var byte_length: u32 = 0;
    if (GetLogicalProcessorInformationEx(relationship, null, &byte_length) != 0 or GetLastError() != 122)
        return error.ProcessorTopologyQueryFailed;
    if (byte_length < 40) return error.ProcessorTopologyMalformed;
    const length: usize = @intCast(byte_length);
    const words = try allocator.alloc(u64, (length + 7) / 8);
    defer allocator.free(words);
    const bytes = std.mem.sliceAsBytes(words)[0..length];
    if (GetLogicalProcessorInformationEx(relationship, bytes.ptr, &byte_length) == 0)
        return error.ProcessorTopologyQueryFailed;

    var cursor: usize = 0;
    while (cursor < length) {
        if (cursor + 32 > length) return error.ProcessorTopologyMalformed;
        const item_relationship = std.mem.readInt(u32, bytes[cursor..][0..4], .little);
        const item_size: usize = @intCast(std.mem.readInt(u32, bytes[cursor + 4 ..][0..4], .little));
        if (item_size < 40 or cursor + item_size > length or item_relationship != relationship)
            return error.ProcessorTopologyMalformed;
        const group_count: usize = @intCast(std.mem.readInt(u16, bytes[cursor + 30 ..][0..2], .little));
        const affinity_start = cursor + 32;
        const affinity_size = @sizeOf(GroupAffinity);
        if (affinity_start + group_count * affinity_size > cursor + item_size)
            return error.ProcessorTopologyMalformed;
        var group_index: usize = 0;
        while (group_index < group_count) : (group_index += 1) {
            const affinity_offset = affinity_start + group_index * affinity_size;
            const processor_mask = std.mem.readInt(usize, bytes[affinity_offset..][0..@sizeOf(usize)], .little);
            const group = std.mem.readInt(u16, bytes[affinity_offset + @sizeOf(usize) ..][0..2], .little);
            if (group == 0 and processor_mask & requested_mask == requested_mask) return true;
        }
        cursor += item_size;
    }
    return false;
}

fn applyProcessMask(allocator: std.mem.Allocator, mask: usize) !ProcessMaskReadback {
    if (builtin.os.tag != .windows or builtin.cpu.arch != .x86_64) return error.UnsupportedAffinityPlatform;
    if (@popCount(mask) != 2) return error.AffinityRequiresTwoProcessors;
    const groups = GetActiveProcessorGroupCount();
    if (groups != 1) return error.AffinityRequiresOneProcessorGroup;
    const process = GetCurrentProcess();
    var before: usize = 0;
    var system: usize = 0;
    if (GetProcessAffinityMask(process, &before, &system) == 0) return error.AffinityQueryFailed;
    if (mask & before != mask or mask & system != mask) return error.AffinityProcessorUnavailable;

    const siblings = try relationContainsPair(allocator, 0, mask);
    const same_numa = if (siblings) false else try relationContainsPair(allocator, 1, mask);
    if (!siblings and !same_numa) return error.AffinityRequiresSiblingOrSameNumaPair;
    if (SetProcessAffinityMask(process, mask) == 0) return error.AffinitySetFailed;
    var after: usize = 0;
    var after_system: usize = 0;
    if (GetProcessAffinityMask(process, &after, &after_system) == 0) return error.AffinityReadbackFailed;
    if (after != mask or after_system != system) return error.AffinityReadbackMismatch;

    const first_processor: u8 = @intCast(@ctz(mask));
    const remaining = mask & (mask - 1);
    const second_processor: u8 = @intCast(@ctz(remaining));
    var current = ProcessorNumber{ .group = 0, .number = 0, .reserved = 0 };
    GetCurrentProcessorNumberEx(&current);
    if (current.group != 0 or (current.number != first_processor and current.number != second_processor))
        return error.AffinityExecutionMismatch;
    return .{
        .first_mask = @as(usize, 1) << @intCast(first_processor),
        .second_mask = @as(usize, 1) << @intCast(second_processor),
        .placement = .{
            .requested_mask = mask,
            .before_process_mask = before,
            .after_process_mask = after,
            .system_mask = system,
            .active_group_count = groups,
            .processor_group = 0,
            .first_processor = first_processor,
            .second_processor = second_processor,
            .topology = if (siblings) "same_physical_core_siblings" else "same_numa_node",
        },
    };
}

fn pinWorker(mask: usize) !WorkerCpu {
    var result = WorkerCpu{ .requested_mask = mask, .thread_pinned = mask != 0 };
    if (mask != 0) {
        if (SetThreadAffinityMask(GetCurrentThread(), mask) == 0) return error.WorkerAffinitySetFailed;
    }
    var entry = ProcessorNumber{ .group = 0, .number = 0, .reserved = 0 };
    GetCurrentProcessorNumberEx(&entry);
    result.entry_group = entry.group;
    result.entry_processor = entry.number;
    if (mask != 0) {
        result.entry_matches_assignment = entry.group == 0 and entry.number < @bitSizeOf(usize) and mask == (@as(usize, 1) << @intCast(entry.number));
        if (!result.entry_matches_assignment.?) return error.WorkerAffinityEntryMismatch;
    }
    return result;
}

fn finishWorker(cpu: *WorkerCpu) !void {
    var exit = ProcessorNumber{ .group = 0, .number = 0, .reserved = 0 };
    GetCurrentProcessorNumberEx(&exit);
    cpu.exit_group = exit.group;
    cpu.exit_processor = exit.number;
    if (cpu.requested_mask != 0) {
        cpu.exit_matches_assignment = exit.group == 0 and exit.number < @bitSizeOf(usize) and cpu.requested_mask == (@as(usize, 1) << @intCast(exit.number));
        if (!cpu.exit_matches_assignment.?) return error.WorkerAffinityExitMismatch;
    }
}

fn Driver(comptime bits: u16, comptime capacity: usize, comptime candidate: CandidateKind) type {
    @setEvalBranchQuota(20_000);
    const Uid = uid_module.Identifier(bits);
    const Index = if (candidate == .c2)
        c2_index.Candidate(Uid, .c2_chain)
    else
        b2_index.Candidate(Uid, .b2_ordered);
    const Fixed = types.For(Uid).Fixed;
    const bucket_count = capacity * 2;
    const row_capacities = [4]usize{ capacity - 3, 1, 1, 1 };
    const declarations = makeDeclarations(row_capacities);
    const fixed_declarations: [0]Fixed = .{};
    const Ledger = owner_module.Owner(
        .{
            .uid_width = bits,
            .activity_width = @intCast(values.activity_bitmap_width),
            .index_buckets = @intCast(bucket_count),
        },
        &declarations,
        &fixed_declarations,
        Index,
    );
    const Notifier = notifications.Notifier(Uid);

    return struct {
        const SampleGate = StartGate();
        const CallbackGate = struct {
            io: std.Io,
            entered: std.atomic.Value(bool) = std.atomic.Value(bool).init(false),
            reader_done: std.atomic.Value(bool) = std.atomic.Value(bool).init(false),
            started_ns: i96 = 0,
            ended_ns: i96 = 0,
            timed_out: bool = false,
        };

        const ReaderState = struct {
            manager: *Ledger,
            scenario: Scenario,
            uid: Uid,
            io: std.Io,
            gate: *SampleGate,
            writer_done: *std.atomic.Value(bool),
            callback_gate: *CallbackGate,
            callback_wait: bool,
            affinity_mask: usize,
            intervals: []Interval,
            used_interval_count: usize = 0,
            writer_done_at_stop: ?bool = null,
            cap_reached: bool = false,
            stop_reason: []const u8 = "not_applicable",
            cpu: WorkerCpu = .{},
            checksum: u64 = 0,
            failure: ?anyerror = null,
        };

        const WriterState = struct {
            manager: *Ledger,
            scenario: Scenario,
            io: std.Io,
            gate: *SampleGate,
            writer_done: *std.atomic.Value(bool),
            callback_gate: *CallbackGate,
            affinity_mask: usize,
            workspace: *Ledger.Workspace,
            tick_workspaces: []Ledger.Workspace,
            audit_workspace: ?*Ledger.Audit.Workspace,
            operation_uids: []const Uid,
            intervals: []Interval,
            cpu: WorkerCpu = .{},
            checksum: u64 = 0,
            audit_summary: ?AuditSummary = null,
            tick_summary: ?types.TickResult = null,
            failure: ?anyerror = null,
        };

        const SameWriterState = struct {
            manager: *Ledger,
            io: std.Io,
            start_gate: *SampleGate,
            phase: *std.atomic.Value(u32),
            done_count: *std.atomic.Value(u32),
            affinity_mask: usize,
            uid: Uid,
            target_table: u32,
            workspace: *Ledger.Workspace,
            intervals: []Interval,
            cpu: WorkerCpu = .{},
            checksum: u64 = 0,
            failure: ?anyerror = null,
        };

        fn runReader(state: *ReaderState) void {
            state.cpu = pinWorker(state.affinity_mask) catch |err| {
                state.failure = err;
                state.callback_gate.reader_done.store(true, .release);
                _ = state.gate.ready.fetchAdd(1, .acq_rel);
                while (!state.gate.open.load(.acquire)) std.atomic.spinLoopHint();
                return;
            };
            state.gate.arriveAndWait();
            defer state.callback_gate.reader_done.store(true, .release);

            if (state.callback_wait and !waitForFlag(&state.callback_gate.entered)) {
                state.failure = error.CallbackDidNotEnter;
                return;
            }
            const continues_until_writer_done = readerContinuesUntilWriterDone(state.scenario);
            var interval_index: usize = 0;
            while (true) {
                if (continues_until_writer_done and interval_index >= minimum_reader_uses) {
                    const writer_done = state.writer_done.load(.acquire);
                    if (writer_done) {
                        state.writer_done_at_stop = true;
                        state.cap_reached = interval_index == maximum_reader_uses;
                        state.stop_reason = "writer_done";
                        break;
                    }
                }
                if (interval_index == state.intervals.len) {
                    if (continues_until_writer_done) {
                        const writer_done = state.writer_done.load(.acquire);
                        state.writer_done_at_stop = writer_done;
                        state.cap_reached = true;
                        state.stop_reason = if (writer_done) "writer_done" else "reader_cap";
                    } else {
                        state.stop_reason = "fixed_notification_count";
                    }
                    break;
                }

                const interval = &state.intervals[interval_index];
                const lock_at_start = if (state.scenario == .capacity_scan_unrelated_use)
                    state.manager.tables[0].write.held.load(.acquire) != 0
                else
                    false;
                const started = nowNs(state.io);
                const result = state.manager.use(state.uid) catch |err| {
                    state.failure = err;
                    return;
                };
                var completed = false;
                switch (result) {
                    .in_memory => |borrow| {
                        state.checksum +%= borrow.allocation_number;
                        state.checksum +%= borrow.lease.use_generation;
                        if (state.manager.endUse(borrow.lease, .complete) catch |err| {
                            state.failure = err;
                            return;
                        } != .completed) {
                            state.failure = error.ReaderEndUseFailed;
                            return;
                        }
                        completed = true;
                    },
                    .not_in_memory => {
                        state.failure = error.ReaderResourceNotResident;
                        return;
                    },
                    .loading => {
                        state.failure = error.ReaderResourceLoading;
                        return;
                    },
                }
                const ended = nowNs(state.io);
                interval.* = .{
                    .start_ns = started,
                    .end_ns = ended,
                    .completed = completed,
                    .table0_lock_at_start = lock_at_start,
                    .table0_lock_at_end = if (state.scenario == .capacity_scan_unrelated_use)
                        state.manager.tables[0].write.held.load(.acquire) != 0
                    else
                        false,
                };
                interval_index += 1;
            }
            state.used_interval_count = interval_index;
            finishWorker(&state.cpu) catch |err| {
                state.failure = err;
            };
        }

        fn ownerCallback(context: ?*anyopaque, _: Uid, _: u64) callconv(.c) void {
            const callback_state: *CallbackGate = @ptrCast(@alignCast(context orelse unreachable));
            callback_state.started_ns = nowNs(callback_state.io);
            callback_state.entered.store(true, .release);
            var attempts: usize = 0;
            while (!callback_state.reader_done.load(.acquire) and attempts < reader_poll_limit) : (attempts += 1)
                std.atomic.spinLoopHint();
            callback_state.timed_out = !callback_state.reader_done.load(.acquire);
            callback_state.ended_ns = nowNs(callback_state.io);
        }

        fn runWriter(state: *WriterState) void {
            defer state.writer_done.store(true, .release);
            state.cpu = pinWorker(state.affinity_mask) catch |err| {
                state.failure = err;
                _ = state.gate.ready.fetchAdd(1, .acq_rel);
                while (!state.gate.open.load(.acquire)) std.atomic.spinLoopHint();
                return;
            };
            state.gate.arriveAndWait();
            for (state.intervals, 0..) |*interval, operation_index| {
                const started = nowNs(state.io);
                switch (state.scenario) {
                    .table_write_unrelated_use => {
                        const borrow = switch (state.manager.push(.{ .table = 0 }, state.operation_uids[operation_index], 64, state.workspace) catch |err| {
                            state.failure = err;
                            return;
                        }) {
                            .newly_allocated => |borrow| borrow,
                            .in_memory => {
                                state.failure = error.ExpectedFreshPush;
                                return;
                            },
                            .loading => {
                                state.failure = error.UnexpectedLoadingPush;
                                return;
                            },
                        };
                        state.checksum +%= borrow.allocation_number;
                        if (state.manager.endUse(borrow.lease, .complete) catch |err| {
                            state.failure = err;
                            return;
                        } != .completed) {
                            state.failure = error.WriterEndUseFailed;
                            return;
                        }
                    },
                    .full_audit_unrelated_use => {
                        const stats = state.manager.audit(state.audit_workspace orelse unreachable) catch |err| {
                            state.failure = err;
                            return;
                        };
                        state.audit_summary = .{
                            .resources = stats.resources,
                            .tables = stats.tables,
                            .active_resources = stats.active_resources,
                            .index_home_body = stats.index_home_body,
                            .index_node_body = stats.index_node_body,
                            .index_neighbor_payload_loads = stats.index_neighbor_payload_loads,
                        };
                    },
                    .capacity_scan_unrelated_use => {
                        state.tick_summary = state.manager.tick(state.tick_workspaces) catch |err| {
                            state.failure = err;
                            return;
                        };
                    },
                    .release_unrelated_use, .notification_unrelated_use => {
                        state.manager.forceRelease(state.operation_uids[operation_index], state.workspace) catch |err| {
                            state.failure = err;
                            return;
                        };
                    },
                    .same_home_publication => unreachable,
                }
                interval.* = .{ .start_ns = started, .end_ns = nowNs(state.io) };
            }
            finishWorker(&state.cpu) catch |err| {
                state.failure = err;
            };
        }

        fn runSameWriter(state: *SameWriterState) void {
            state.cpu = pinWorker(state.affinity_mask) catch |err| {
                state.failure = err;
                _ = state.start_gate.ready.fetchAdd(1, .acq_rel);
                return;
            };
            state.start_gate.arriveAndWait();
            for (state.intervals, 0..) |*interval, index| {
                while (state.phase.load(.acquire) != index + 1) {
                    if (state.phase.load(.acquire) == std.math.maxInt(u32)) return;
                    std.atomic.spinLoopHint();
                }
                const started = nowNs(state.io);
                const borrow = switch (state.manager.push(.{ .table = state.target_table }, state.uid, 64, state.workspace) catch |err| {
                    state.failure = err;
                    return;
                }) {
                    .newly_allocated => |borrow| borrow,
                    .in_memory => {
                        state.failure = error.ExpectedFreshSameHomePush;
                        return;
                    },
                    .loading => {
                        state.failure = error.UnexpectedSameHomeLoading;
                        return;
                    },
                };
                state.checksum +%= borrow.allocation_number;
                if (state.manager.endUse(borrow.lease, .complete) catch |err| {
                    state.failure = err;
                    return;
                } != .completed) {
                    state.failure = error.SameHomeEndUseFailed;
                    return;
                }
                interval.* = .{ .start_ns = started, .end_ns = nowNs(state.io) };
                _ = state.done_count.fetchAdd(1, .acq_rel);
                while (state.phase.load(.acquire) == index + 1) std.atomic.spinLoopHint();
            }
            finishWorker(&state.cpu) catch |err| {
                state.failure = err;
            };
        }

        pub fn run(allocator: std.mem.Allocator, io: std.Io, options: Options, placement: ?ProcessMaskReadback) !void {
            const scenario = options.scenario;
            if (options.mode == .timing and builtin.mode != .ReleaseFast) return error.TimingRequiresReleaseFast;
            if (capacity < 1024 or bucket_count != capacity * 2) return error.InvalidProfile;

            const rows = try allocator.alloc(Ledger.Row, capacity);
            defer allocator.free(rows);
            const table_states = try allocator.alloc(Ledger.TableState, 4);
            defer allocator.free(table_states);
            const nodes = try allocator.alloc(Index.Node, capacity);
            defer allocator.free(nodes);
            const homes = try allocator.alloc(Index.Home, bucket_count);
            defer allocator.free(homes);
            var table0_subscribers = [_]Notifier.Record{.{}};
            var table1_subscribers = [_]Notifier.Record{.{}};
            var table2_subscribers = [_]Notifier.Record{.{}};
            var table3_subscribers = [_]Notifier.Record{.{}};
            const subscriber_slices = [_][]Notifier.Record{ &table0_subscribers, &table1_subscribers, &table2_subscribers, &table3_subscribers };
            var ledger = try Ledger.init(rows, &.{}, table_states, &.{}, nodes, homes, &subscriber_slices);
            defer for (rows) |*row| row.resource.forceRelease() catch @panic("concurrent benchmark cleanup failed");

            const action_capacity: usize = @intCast(values.table_0_tick_maximum_actions);
            var tick_candidate_arrays: [4][]Ledger.Candidate = undefined;
            var tick_workspaces: [4]Ledger.Workspace = undefined;
            var tick_candidate_bytes: usize = 0;
            if (scenario == .capacity_scan_unrelated_use) {
                for (row_capacities, 0..) |table_capacity, table| {
                    const length = @min(action_capacity, table_capacity);
                    tick_candidate_arrays[table] = try allocator.alloc(Ledger.Candidate, length);
                    tick_candidate_bytes += length * @sizeOf(Ledger.Candidate);
                    tick_workspaces[table] = emptyWorkspace(tick_candidate_arrays[table]);
                    try ledger.setMode(@intCast(table), .unrestricted);
                }
            } else {
                for (&tick_candidate_arrays) |*array| array.* = &.{};
                for (&tick_workspaces) |*work| work.* = emptyWorkspace(&.{});
            }
            defer if (scenario == .capacity_scan_unrelated_use) {
                for (tick_candidate_arrays) |array| allocator.free(array);
            };

            var audit_workspace_storage: ?Ledger.Audit.Workspace = null;
            var audit_workspace_bytes: usize = 0;
            if (scenario == .full_audit_unrelated_use) {
                const table_views = try allocator.alloc(types.TableView, 4);
                const allocations = try allocator.alloc(u64, capacity);
                audit_workspace_storage = .{
                    .shell_queue = &.{},
                    .shell_marks = &.{},
                    .depth = &.{},
                    .events = &.{},
                    .stack = &.{},
                    .table_views = table_views,
                    .fixed_marks = &.{},
                    .allocations = allocations,
                };
                audit_workspace_bytes = @sizeOf(Ledger.Audit.Workspace) +
                    table_views.len * @sizeOf(types.TableView) + allocations.len * @sizeOf(u64);
            }
            defer if (audit_workspace_storage) |work| {
                allocator.free(work.table_views);
                allocator.free(work.allocations);
            };

            var reader_generator = UidGenerator(Uid){};
            var reader_uid: Uid = undefined;
            var reader_home: u32 = 0;
            const reader_table: usize = if (scenario == .full_audit_unrelated_use) 3 else 1;
            var seeded_entries: usize = 0;
            if (scenario != .same_home_publication) {
                reader_uid = try reader_generator.next(uid_domain, bucket_count, .any, null);
                reader_home = @intCast(reader_uid.hash() % bucket_count);
                _ = try registerResident(&ledger, reader_table, reader_uid, 0);
                seeded_entries += 1;
            }

            var reader_guard_borrow: ?types.For(Uid).Borrow = null;
            defer if (reader_guard_borrow) |borrow| {
                _ = ledger.endUse(borrow.lease, .complete) catch @panic("concurrent benchmark reader guard cleanup failed");
            };
            if (scenario == .capacity_scan_unrelated_use) {
                reader_guard_borrow = switch (try ledger.use(reader_uid)) {
                    .in_memory => |borrow| borrow,
                    .not_in_memory => return error.CapacityScanReaderGuardNotResident,
                    .loading => return error.CapacityScanReaderGuardLoading,
                };
            }

            var table0_generator = UidGenerator(Uid){ .next_value = 0x1000 };
            if (scenario == .capacity_scan_unrelated_use) {
                for (0..row_capacities[0]) |ordinal| {
                    const uid = try table0_generator.next(uid_domain + 1, bucket_count, .avoid_reader_window, reader_home);
                    try registerResidentAt(&ledger, Ledger.resourceStart(0) + ordinal, uid, 0);
                    seeded_entries += 1;
                }
            }

            var release_uids: [writer_operations_per_sample]Uid = undefined;
            const release_uid_count: usize = if (scenario == .release_unrelated_use) writer_operations_per_sample else if (scenario == .notification_unrelated_use) 1 else 0;
            for (release_uids[0..release_uid_count]) |*uid| {
                uid.* = try table0_generator.next(uid_domain + 1, bucket_count, .avoid_reader_window, reader_home);
                _ = try registerResident(&ledger, 0, uid.*, 64);
                seeded_entries += 1;
            }

            var same_home_uids: [2]Uid = undefined;
            var same_home_id: u32 = 0;
            var warm_uid: Uid = undefined;
            if (scenario == .same_home_publication) {
                var same_generator = UidGenerator(Uid){ .next_value = 0x9000 };
                same_home_uids[0] = try same_generator.next(uid_domain + 2, bucket_count, .any, null);
                same_home_id = try ledger.index.homeFor(same_home_uids[0]);
                var pair_attempts: usize = 0;
                var found_same_home = false;
                while (pair_attempts < bucket_count * 16 and !found_same_home) : (pair_attempts += 1) {
                    same_home_uids[1] = try same_generator.next(uid_domain + 2, bucket_count, .any, null);
                    if (try ledger.index.homeFor(same_home_uids[1]) == same_home_id and
                        !same_home_uids[0].eql(same_home_uids[1])) found_same_home = true;
                }
                if (!found_same_home) return error.SameHomePairSearchExhausted;
                warm_uid = try same_generator.next(uid_domain + 3, bucket_count, .avoid_one, same_home_id);
                _ = try registerResident(&ledger, 3, warm_uid, 0);
                seeded_entries += 1;
            }

            var actual_index_entries: usize = 0;
            for (0..ledger.index.nodes.len) |node_id| {
                if (try ledger.index.entryAt(@intCast(node_id))) |entry| {
                    const location = entry.location;
                    if (location.kind != .resource or location.table >= ledger.tables.len or
                        location.slot >= ledger.rows.len or ledger.rows[location.slot].uid == null or
                        !ledger.rows[location.slot].uid.?.eql(entry.uid)) return error.IndexEntryDoesNotMatchOwnerRow;
                    actual_index_entries += 1;
                }
            }
            if (actual_index_entries != seeded_entries) return error.IncompleteIndexPopulation;
            const reader_bucket_isolated_at_setup = if (scenario == .capacity_scan_unrelated_use)
                try readerBucketIsolated(&ledger, reader_uid, reader_home)
            else
                null;

            var samples = try allocator.alloc(SampleRecord, options.samples);
            defer allocator.free(samples);
            var initialized_samples: usize = 0;
            defer for (samples[0..initialized_samples]) |sample| {
                if (sample.operation_intervals.len != 0) allocator.free(sample.operation_intervals);
                if (sample.second_operation_intervals.len != 0) allocator.free(sample.second_operation_intervals);
                if (sample.reader_intervals.len != 0) allocator.free(sample.reader_intervals);
            };
            var output_interval_storage: usize = 0;
            var global_checksum: u64 = 0;
            var reader_bucket_isolated_after_warm_tick: ?bool = null;
            var reader_bucket_isolated_before_sample = reader_bucket_isolated_at_setup;

            if (scenario == .capacity_scan_unrelated_use) {
                const warm_tick = try ledger.tick(&tick_workspaces);
                if (warm_tick.scanned != row_capacities[0] + 1 or warm_tick.released != action_capacity)
                    return error.CapacityScanWarmupMismatch;
                reader_bucket_isolated_after_warm_tick = try readerBucketIsolated(&ledger, reader_uid, reader_home);
                try refillCapacityRows(&ledger, warm_tick.released, &table0_generator, bucket_count, reader_home);
                reader_bucket_isolated_before_sample = try readerBucketIsolated(&ledger, reader_uid, reader_home);
            }

            var release_workspace = emptyWorkspace(&.{});
            var table_write_generator = UidGenerator(Uid){ .next_value = 0x1_0000 };
            var callback_records = [_]types.For(Uid).Notification{undefined};
            if (scenario == .notification_unrelated_use) release_workspace.notes = &callback_records;
            var notification_record: ?Ledger.Subscription = null;
            var callback_gate = CallbackGate{ .io = io };
            if (scenario == .notification_unrelated_use)
                notification_record = try ledger.subscribe(0, ownerCallback, &callback_gate);

            for (samples, 0..) |*sample, sample_index| {
                if (scenario != .same_home_publication) {
                    var warmup_index: usize = 0;
                    while (warmup_index < warmup_uses_per_sample) : (warmup_index += 1)
                        try useAndComplete(&ledger, reader_uid, &global_checksum);
                } else {
                    var warmup_index: usize = 0;
                    while (warmup_index < warmup_uses_per_sample) : (warmup_index += 1)
                        try useAndComplete(&ledger, warm_uid, &global_checksum);
                }

                if (scenario == .same_home_publication) {
                    sample.* = try runSameHomeSample(
                        allocator,
                        io,
                        &ledger,
                        sample_index,
                        options.mode,
                        placement,
                        same_home_uids,
                        same_home_id,
                        &global_checksum,
                    );
                    output_interval_storage += sampleIntervalsBytes(sample.*);
                    initialized_samples += 1;
                    continue;
                }

                const operation_count = if (scenario == .table_write_unrelated_use or scenario == .release_unrelated_use)
                    writer_operations_per_sample
                else
                    1;
                const operations = try allocator.alloc(Interval, operation_count);
                const reader_interval_capacity = if (readerContinuesUntilWriterDone(scenario))
                    maximum_reader_uses
                else
                    fixed_notification_reader_uses;
                const readers = try allocator.alloc(Interval, reader_interval_capacity);
                defer allocator.free(operations);
                defer allocator.free(readers);
                var table_write_uids: [writer_operations_per_sample]Uid = undefined;
                if (scenario == .table_write_unrelated_use) {
                    for (&table_write_uids) |*uid|
                        uid.* = try table_write_generator.next(uid_domain, bucket_count, .avoid_reader_window, reader_home);
                }
                @memset(operations, .{ .start_ns = 0, .end_ns = 0 });
                @memset(readers, .{ .start_ns = 0, .end_ns = 0, .completed = false });

                var gate = SampleGate{};
                var writer_done = std.atomic.Value(bool).init(false);
                callback_gate.io = io;
                callback_gate.entered.store(false, .release);
                callback_gate.reader_done.store(false, .release);
                callback_gate.started_ns = 0;
                callback_gate.ended_ns = 0;
                callback_gate.timed_out = false;
                const writer_mask = if (placement) |p| p.first_mask else 0;
                const reader_mask = if (placement) |p| p.second_mask else 0;
                const operation_uid_slice = if (scenario == .table_write_unrelated_use)
                    table_write_uids[0..operation_count]
                else if (scenario == .release_unrelated_use or scenario == .notification_unrelated_use)
                    release_uids[0..operation_count]
                else
                    &[_]Uid{};
                var writer = WriterState{
                    .manager = &ledger,
                    .scenario = scenario,
                    .io = io,
                    .gate = &gate,
                    .writer_done = &writer_done,
                    .callback_gate = &callback_gate,
                    .affinity_mask = writer_mask,
                    .workspace = &release_workspace,
                    .tick_workspaces = &tick_workspaces,
                    .audit_workspace = if (audit_workspace_storage) |*workspace| workspace else null,
                    .operation_uids = operation_uid_slice,
                    .intervals = operations,
                };
                var reader = ReaderState{
                    .manager = &ledger,
                    .scenario = scenario,
                    .uid = reader_uid,
                    .io = io,
                    .gate = &gate,
                    .writer_done = &writer_done,
                    .callback_gate = &callback_gate,
                    .callback_wait = scenario == .notification_unrelated_use,
                    .affinity_mask = reader_mask,
                    .intervals = readers,
                };
                const version_before = if (scenario == .capacity_scan_unrelated_use) try homeVersion(&ledger, reader_home) else 0;
                var writer_thread = try std.Thread.spawn(.{}, runWriter, .{&writer});
                var reader_thread = std.Thread.spawn(.{}, runReader, .{&reader}) catch |err| {
                    gate.open.store(true, .release);
                    writer_thread.join();
                    return err;
                };
                const both_ready = waitUntilReady(&gate, 2);
                const sample_started = nowNs(io);
                gate.open.store(true, .release);
                if (!both_ready) {
                    writer_thread.join();
                    reader_thread.join();
                    return error.WorkerStartTimeout;
                }
                writer_thread.join();
                reader_thread.join();
                const sample_ended = nowNs(io);
                if (writer.failure) |err| return err;
                if (reader.failure) |err| return err;
                global_checksum +%= writer.checksum;
                global_checksum +%= reader.checksum;
                const used_readers = readers[0..reader.used_interval_count];

                const version_after = if (scenario == .capacity_scan_unrelated_use) try homeVersion(&ledger, reader_home) else 0;
                const overlap = countOverlaps(operations, used_readers);
                const complete_inside = countContained(used_readers, operations);
                const locked_complete = if (scenario == .capacity_scan_unrelated_use) countLockedComplete(used_readers) else 0;
                const reader_bucket_isolated_after = if (scenario == .capacity_scan_unrelated_use)
                    try readerBucketIsolated(&ledger, reader_uid, reader_home)
                else
                    null;
                const callback_interval = if (scenario == .notification_unrelated_use)
                    Interval{ .start_ns = callback_gate.started_ns, .end_ns = callback_gate.ended_ns }
                else
                    null;
                const callback_overlap = if (callback_interval) |interval| countOverlaps(&.{interval}, used_readers) else 0;
                const callback_complete = if (callback_interval) |interval| countContained(used_readers, &.{interval}) else 0;
                const tick_result = writer.tick_summary;
                const tick = if (tick_result) |result|
                    TickSummary{
                        .released = result.released,
                        .scanned = result.scanned,
                        .expected_table0_rows = row_capacities[0],
                        .expected_total_scanned_rows = row_capacities[0] + 1,
                        .reader_guard_lease_held = reader_guard_borrow != null,
                        .reader_bucket_isolated_at_setup = reader_bucket_isolated_at_setup,
                        .reader_bucket_isolated_after_warm_tick = reader_bucket_isolated_after_warm_tick,
                        .reader_bucket_isolated_before = reader_bucket_isolated_before_sample,
                        .reader_bucket_isolated_after = reader_bucket_isolated_after,
                        .table0_write_held_completion_count = locked_complete,
                        .reader_home_version_before = version_before,
                        .reader_home_version_after = version_after,
                    }
                else
                    null;
                const audit = writer.audit_summary;
                const accepted = acceptedOverlap(scenario, overlap, complete_inside, callback_overlap, callback_complete, tick, version_before, version_after);
                sample.* = .{
                    .sample_index = sample_index,
                    .sample_start_ns = sample_started,
                    .sample_end_ns = sample_ended,
                    .operation_intervals = try allocator.dupe(Interval, operations),
                    .second_operation_intervals = &.{},
                    .reader_intervals = try allocator.dupe(Interval, readers[0..reader.used_interval_count]),
                    .reader_use_count = reader.used_interval_count,
                    .reader_writer_done_at_stop = reader.writer_done_at_stop,
                    .reader_cap_reached = reader.cap_reached,
                    .reader_stop_reason = reader.stop_reason,
                    .callback_interval = callback_interval,
                    .callback_timed_out = callback_gate.timed_out,
                    .audit = audit,
                    .tick = tick,
                    .operation_reader_overlap_count = overlap,
                    .reader_completed_during_operation_count = complete_inside,
                    .left_right_operation_overlap_count = 0,
                    .callback_reader_overlap_count = callback_overlap,
                    .callback_reader_completion_count = callback_complete,
                    .overlap_accepted = accepted,
                    .overlap_rejection_reason = if (accepted) null else rejectionReason(scenario),
                    .writer_cpu = writer.cpu,
                    .reader_cpu = reader.cpu,
                    .checksum = global_checksum,
                };
                output_interval_storage += sampleIntervalsBytes(sample.*);
                initialized_samples += 1;

                if (scenario == .capacity_scan_unrelated_use) {
                    const result = tick_result orelse return error.MissingTickResult;
                    if (result.scanned != row_capacities[0] + 1 or result.released != action_capacity)
                        return error.CapacityScanDidNotVisitExpectedRows;
                    if (sample_index + 1 < samples.len) {
                        try refillCapacityRows(&ledger, result.released, &table0_generator, bucket_count, reader_home);
                        reader_bucket_isolated_before_sample = try readerBucketIsolated(&ledger, reader_uid, reader_home);
                    }
                } else if ((scenario == .release_unrelated_use or scenario == .notification_unrelated_use) and sample_index + 1 < samples.len) {
                    try reseedReleaseSet(&ledger, release_uids[0..release_uid_count]);
                }
            }

            if (notification_record) |subscription| try ledger.unsubscribe(subscription);
            const same_home_summary = if (scenario == .same_home_publication) blk: {
                break :blk SameHomeSummary{
                    .home = same_home_id,
                    .uid_a_hex = try uidHex(allocator, same_home_uids[0]),
                    .uid_b_hex = try uidHex(allocator, same_home_uids[1]),
                };
            } else null;
            defer if (same_home_summary) |summary| {
                allocator.free(summary.uid_a_hex);
                allocator.free(summary.uid_b_hex);
            };
            const output = OutputRecord{
                .workload_revision = 2,
                .reader_minimum_uses_per_sample = minimum_reader_uses,
                .reader_maximum_uses_per_sample = maximum_reader_uses,
                .candidate = @tagName(candidate),
                .scenario = @tagName(scenario),
                .uid_width_bits = bits,
                .resource_capacity = capacity,
                .table_resource_capacities = row_capacities,
                .index_node_capacity = ledger.index.nodes.len,
                .index_home_or_bucket_capacity = if (candidate == .c2) ledger.index.homes.len else ledger.index.buckets.len,
                .indexed_entries_after_setup = seeded_entries,
                .reader_table = if (scenario == .same_home_publication) null else @intCast(reader_table),
                .reader_home = if (scenario == .same_home_publication) null else reader_home,
                .same_home_uid_pair = same_home_summary,
                .capacity_scan_prepopulated_rows = if (scenario == .capacity_scan_unrelated_use) row_capacities[0] else 0,
                .capacity_scan_guard_lease_count = if (reader_guard_borrow != null) 1 else 0,
                .reader_home_exclusion_radius = if (scenario == .same_home_publication) 0 else reader_home_exclusion_radius,
                .reader_bucket_isolated_at_setup = reader_bucket_isolated_at_setup,
                .reader_bucket_isolated_after_warm_tick = reader_bucket_isolated_after_warm_tick,
                .mode = @tagName(options.mode),
                .build_mode = @tagName(builtin.mode),
                .timing_boundary = timingBoundary(scenario),
                .process_affinity = if (placement) |p| p.placement else null,
                .metadata_bytes = .{
                    .owner_header = @sizeOf(Ledger),
                    .owner_rows = capacity * @sizeOf(Ledger.Row),
                    .owner_table_states = 4 * @sizeOf(Ledger.TableState),
                    .owner_subscriber_records = 4 * @sizeOf(Notifier.Record),
                    .index_nodes = capacity * @sizeOf(Index.Node),
                    .index_homes_or_buckets = bucket_count * @sizeOf(Index.Home),
                    .owner_index_arrays_total = capacity * @sizeOf(Ledger.Row) +
                        4 * @sizeOf(Ledger.TableState) + capacity * @sizeOf(Index.Node) + bucket_count * @sizeOf(Index.Home),
                    .tick_candidate_workspace = tick_candidate_bytes,
                    .audit_workspace = audit_workspace_bytes,
                    .worker_workspace_headers = (if (scenario == .same_home_publication) @as(usize, 7) else 5) * @sizeOf(Ledger.Workspace),
                    .worker_interval_scratch_peak = workerIntervalScratchPeakBytes(scenario),
                    .interval_size = @sizeOf(Interval),
                    .sample_header_size = @sizeOf(SampleRecord),
                    .output_interval_storage = output_interval_storage + options.samples * @sizeOf(SampleRecord),
                },
                .tick_policy_trigger_percent = @intCast(values.table_0_concentrated_trigger_percent),
                .tick_policy_target_percent = @intCast(values.table_0_concentrated_target_percent),
                .tick_policy_maximum_actions = action_capacity,
                .samples = samples,
            };
            const json = try std.json.Stringify.valueAlloc(allocator, output, .{});
            defer allocator.free(json);
            var stdout_buffer: [2048]u8 = undefined;
            var stdout_file_writer = std.Io.File.stdout().writer(io, &stdout_buffer);
            try stdout_file_writer.interface.writeAll(json);
            try stdout_file_writer.interface.writeAll("\n");
            try stdout_file_writer.interface.flush();
        }

        fn runSameHomeSample(
            allocator: std.mem.Allocator,
            io: std.Io,
            ledger: *Ledger,
            sample_index: usize,
            mode: RunMode,
            placement: ?ProcessMaskReadback,
            uids: [2]Uid,
            home_id: u32,
            checksum: *u64,
        ) !SampleRecord {
            _ = home_id;
            const left_intervals = try allocator.alloc(Interval, same_home_rounds_per_sample);
            const right_intervals = try allocator.alloc(Interval, same_home_rounds_per_sample);
            @memset(left_intervals, .{ .start_ns = 0, .end_ns = 0 });
            @memset(right_intervals, .{ .start_ns = 0, .end_ns = 0 });
            var left_workspace = emptyWorkspace(&.{});
            var right_workspace = emptyWorkspace(&.{});
            var start_gate = SampleGate{};
            var phase = std.atomic.Value(u32).init(0);
            var done_count = std.atomic.Value(u32).init(0);
            const left_mask = if (placement) |p| p.first_mask else 0;
            const right_mask = if (placement) |p| p.second_mask else 0;
            var left = SameWriterState{
                .manager = ledger,
                .io = io,
                .start_gate = &start_gate,
                .phase = &phase,
                .done_count = &done_count,
                .affinity_mask = left_mask,
                .uid = uids[0],
                .target_table = 0,
                .workspace = &left_workspace,
                .intervals = left_intervals,
            };
            var right = SameWriterState{
                .manager = ledger,
                .io = io,
                .start_gate = &start_gate,
                .phase = &phase,
                .done_count = &done_count,
                .affinity_mask = right_mask,
                .uid = uids[1],
                .target_table = 1,
                .workspace = &right_workspace,
                .intervals = right_intervals,
            };
            var left_thread = try std.Thread.spawn(.{}, runSameWriter, .{&left});
            var right_thread = std.Thread.spawn(.{}, runSameWriter, .{&right}) catch |err| {
                phase.store(std.math.maxInt(u32), .release);
                start_gate.open.store(true, .release);
                left_thread.join();
                return err;
            };
            const both_ready = waitUntilReady(&start_gate, 2);
            const sample_started = nowNs(io);
            start_gate.open.store(true, .release);
            if (!both_ready) {
                phase.store(std.math.maxInt(u32), .release);
                left_thread.join();
                right_thread.join();
                return error.SameHomeWorkerStartTimeout;
            }
            var round: usize = 0;
            while (round < same_home_rounds_per_sample) : (round += 1) {
                phase.store(@intCast(round + 1), .release);
                var polls: usize = 0;
                while (done_count.load(.acquire) < 2 and polls < reader_poll_limit) : (polls += 1) std.Thread.yield() catch {};
                if (done_count.load(.acquire) != 2) {
                    phase.store(std.math.maxInt(u32), .release);
                    left_thread.join();
                    right_thread.join();
                    return error.SameHomeWorkerTimeout;
                }
                done_count.store(0, .release);
                try ledger.forceRelease(uids[0], &left_workspace);
                try ledger.forceRelease(uids[1], &right_workspace);
            }
            phase.store(@intCast(same_home_rounds_per_sample + 1), .release);
            left_thread.join();
            right_thread.join();
            const sample_ended = nowNs(io);
            if (left.failure) |err| return err;
            if (right.failure) |err| return err;
            checksum.* +%= left.checksum;
            checksum.* +%= right.checksum;
            const overlap = countOverlaps(left_intervals, right_intervals);
            const accepted = overlap != 0;
            return .{
                .sample_index = sample_index,
                .sample_start_ns = sample_started,
                .sample_end_ns = sample_ended,
                .operation_intervals = left_intervals,
                .second_operation_intervals = right_intervals,
                .reader_intervals = &.{},
                .reader_use_count = 0,
                .reader_writer_done_at_stop = null,
                .reader_cap_reached = false,
                .reader_stop_reason = "not_applicable",
                .operation_reader_overlap_count = 0,
                .reader_completed_during_operation_count = 0,
                .left_right_operation_overlap_count = overlap,
                .callback_reader_overlap_count = 0,
                .callback_reader_completion_count = 0,
                .overlap_accepted = mode == .correctness or accepted,
                .overlap_rejection_reason = if (mode == .timing and !accepted) "no observed overlapping same-home push intervals" else null,
                .writer_cpu = left.cpu,
                .reader_cpu = .{},
                .second_writer_cpu = right.cpu,
                .checksum = checksum.*,
            };
        }

        fn emptyWorkspace(candidates: []Ledger.Candidate) Ledger.Workspace {
            return .{ .candidates = candidates, .rows = &.{}, .shells = &.{}, .homes = &.{}, .notes = &.{} };
        }

        fn registerResident(ledger: *Ledger, table: usize, uid: Uid, bytes: u64) !u32 {
            const start = Ledger.resourceStart(table);
            for (ledger.rows[start..][0..row_capacities[table]], start..) |*row, slot| {
                if (row.uid == null) {
                    try registerResidentAt(ledger, slot, uid, bytes);
                    return @intCast(slot);
                }
            }
            return error.TableHasNoFreeResourceRow;
        }

        fn registerResidentAt(ledger: *Ledger, slot: usize, uid: Uid, bytes: u64) !void {
            const row = &ledger.rows[slot];
            if (row.uid != null) return error.ResourceRowAlreadyOccupied;
            const Resource = @TypeOf(row.resource);
            const epoch = ledger.epoch.load(.acquire);
            const allocation_number = try ledger.numbers.take();
            var resource = try Resource.dynamic(bytes);
            const fresh = (try resource.push(epoch, allocation_number)).fresh;
            if (try resource.endUse(fresh.lease, .complete, epoch) != .completed)
                return error.SeedEndUseFailed;
            row.uid = uid;
            row.parent = null;
            row.fixed_declaration = null;
            row.fixed_parent_number = 0;
            row.resource = resource;
            try ledger.index.insert(@intCast(slot), uid, .{
                .kind = .resource,
                .table = row.table,
                .slot = @intCast(slot),
            });
            ledger.tables[row.table].registered_bytes += bytes;
            ledger.tables[row.table].system_page_bytes += @intCast(try memory_region.requiredBytes(bytes));
        }

        fn refillCapacityRows(ledger: *Ledger, count: usize, generator: *UidGenerator(Uid), buckets: usize, reader_home: u32) !void {
            var filled: usize = 0;
            for (ledger.rows[Ledger.resourceStart(0)..][0..row_capacities[0]], Ledger.resourceStart(0)..) |*row, slot| {
                if (row.uid != null) continue;
                const uid = try generator.next(uid_domain + 1, buckets, .avoid_reader_window, reader_home);
                try registerResidentAt(ledger, slot, uid, 0);
                filled += 1;
                if (filled == count) break;
            }
            if (filled != count) return error.CapacityScanRefillMismatch;
        }

        fn reseedReleaseSet(ledger: *Ledger, uids: []const Uid) !void {
            for (uids) |uid| _ = try registerResident(ledger, 0, uid, 64);
        }

        fn useAndComplete(ledger: *Ledger, uid: Uid, checksum: *u64) !void {
            switch (try ledger.use(uid)) {
                .in_memory => |borrow| {
                    checksum.* +%= borrow.allocation_number;
                    checksum.* +%= borrow.lease.use_generation;
                    if (try ledger.endUse(borrow.lease, .complete) != .completed) return error.WarmupEndUseFailed;
                },
                .not_in_memory => return error.WarmupReaderNotResident,
                .loading => return error.WarmupReaderLoading,
            }
        }

        fn homeVersion(ledger: *const Ledger, home: u32) !u64 {
            if (candidate == .c2) {
                if (home >= ledger.index.homes.len) return error.InvalidReaderHome;
                return ledger.index.homes[home].version.load(.acquire);
            }
            if (home >= ledger.index.buckets.len) return error.InvalidReaderHome;
            return ledger.index.buckets[home].version.load(.acquire);
        }

        fn readerBucketIsolated(ledger: *const Ledger, uid: Uid, expected_home: u32) !?bool {
            if (candidate == .c2) return null;
            const location = (try ledger.index.find(uid)) orelse return false;
            if (location.kind != .resource or location.table != 1 or
                @as(usize, location.slot) >= ledger.index.nodes.len)
            {
                return false;
            }
            const node_id = location.slot;
            const bucket_index = ledger.index.nodes[node_id].bucket_index.load(.seq_cst);
            if (bucket_index != expected_home or
                ledger.index.buckets[bucket_index].node_id.load(.seq_cst) != node_id)
            {
                return false;
            }
            const bucket_index_usize: usize = @intCast(bucket_index);
            const previous = if (bucket_index_usize == 0) ledger.index.buckets.len - 1 else bucket_index_usize - 1;
            const next = (bucket_index_usize + 1) % ledger.index.buckets.len;
            return ledger.index.buckets[previous].node_id.load(.seq_cst) == Index.no_bucket and
                ledger.index.buckets[next].node_id.load(.seq_cst) == Index.no_bucket;
        }

        fn countLockedComplete(readers: []const Interval) usize {
            var count: usize = 0;
            for (readers) |reader| if (reader.completed and reader.table0_lock_at_start and reader.table0_lock_at_end) {
                count += 1;
            };
            return count;
        }

        fn acceptedOverlap(
            selected: Scenario,
            overlap: usize,
            completed: usize,
            callback_overlap: usize,
            callback_completed: usize,
            tick: ?TickSummary,
            version_before: u64,
            version_after: u64,
        ) bool {
            return switch (selected) {
                .full_audit_unrelated_use => overlap != 0,
                .notification_unrelated_use => callback_overlap != 0 and callback_completed != 0,
                .capacity_scan_unrelated_use => overlap != 0 and completed != 0 and
                    (tick != null and tick.?.expected_table0_rows == row_capacities[0] and
                        tick.?.scanned == tick.?.expected_total_scanned_rows and
                        tick.?.reader_guard_lease_held and tick.?.table0_write_held_completion_count != 0 and
                        (candidate == .c2 or
                            (tick.?.reader_bucket_isolated_at_setup == true and
                                tick.?.reader_bucket_isolated_after_warm_tick == true and
                                tick.?.reader_bucket_isolated_before == true and tick.?.reader_bucket_isolated_after == true)) and
                        version_before == version_after),
                .same_home_publication => false,
                else => overlap != 0,
            };
        }

        fn rejectionReason(selected: Scenario) []const u8 {
            return switch (selected) {
                .table_write_unrelated_use => "no observed use/push interval overlap",
                .full_audit_unrelated_use => "no observed use/audit interval overlap; diagnostic cell only",
                .capacity_scan_unrelated_use => "missing full scan, table-0-lock use completion, unrelated home stability, or operation overlap",
                .release_unrelated_use => "no observed use/release interval overlap",
                .notification_unrelated_use => "no observed callback/use overlap with use completion during callback",
                .same_home_publication => "no observed overlapping same-home push intervals",
            };
        }

        fn timingBoundary(selected: Scenario) []const u8 {
            return switch (selected) {
                .table_write_unrelated_use => "Owner.push plus endUse(.complete), measured per newly registered 64-byte dynamic resource; reader interval is Owner.use plus endUse(.complete)",
                .full_audit_unrelated_use => "one complete Owner.audit call; reader interval is Owner.use plus endUse(.complete); audit is diagnostic and takes whole-owner locks",
                .capacity_scan_unrelated_use => "one complete Owner.tick call; reader interval is Owner.use plus endUse(.complete); table-0 lock state is sampled around each completed use",
                .release_unrelated_use => "one Owner.forceRelease call per resident dynamic UID; reader interval is Owner.use plus endUse(.complete)",
                .notification_unrelated_use => "one Owner.forceRelease call including synchronous subscriber dispatch; callback interval reported separately; reader interval is Owner.use plus endUse(.complete)",
                .same_home_publication => "Owner.push plus endUse(.complete) for each writer; forceRelease needed to reuse the same UIDs occurs after both measured pushes",
            };
        }

        fn sampleIntervalsBytes(sample: SampleRecord) usize {
            return (sample.operation_intervals.len + sample.second_operation_intervals.len + sample.reader_intervals.len) * @sizeOf(Interval);
        }

        fn readerContinuesUntilWriterDone(selected: Scenario) bool {
            return switch (selected) {
                .table_write_unrelated_use,
                .full_audit_unrelated_use,
                .capacity_scan_unrelated_use,
                .release_unrelated_use,
                => true,
                .notification_unrelated_use, .same_home_publication => false,
            };
        }

        fn workerIntervalScratchPeakBytes(selected: Scenario) usize {
            const operation_count: usize = switch (selected) {
                .table_write_unrelated_use, .release_unrelated_use => writer_operations_per_sample,
                .full_audit_unrelated_use, .capacity_scan_unrelated_use, .notification_unrelated_use => 1,
                .same_home_publication => 0,
            };
            const reader_capacity: usize = if (readerContinuesUntilWriterDone(selected))
                maximum_reader_uses
            else if (selected == .notification_unrelated_use)
                fixed_notification_reader_uses
            else
                0;
            return (operation_count + reader_capacity) * @sizeOf(Interval);
        }
    };
}

fn makeDeclarations(capacities: [4]usize) [4]types.Table {
    const attrs = attributes();
    const policy = productionPolicy();
    var result: [4]types.Table = undefined;
    for (&result, capacities) |*table, capacity| {
        table.* = .{
            .capacity = @intCast(capacity),
            .partition_capacity = 0,
            .reserved_start = 0,
            .reserved_count = 0,
            .attributes = attrs,
            .policy = policy,
        };
    }
    return result;
}

fn countOverlaps(operations: []const Interval, readers: []const Interval) usize {
    var count: usize = 0;
    for (operations) |operation| for (readers) |reader| {
        if (intervalOverlap(operation, reader)) count += 1;
    };
    return count;
}

fn countContained(readers: []const Interval, operations: []const Interval) usize {
    var count: usize = 0;
    for (readers) |reader| for (operations) |operation| {
        if (intervalContained(reader, operation)) {
            count += 1;
            break;
        }
    };
    return count;
}

fn uidHex(allocator: std.mem.Allocator, uid: anytype) ![]u8 {
    const digits = "0123456789abcdef";
    const output = try allocator.alloc(u8, uid.bytes.len * 2);
    for (uid.bytes, 0..) |byte, index| {
        output[index * 2] = digits[byte >> 4];
        output[index * 2 + 1] = digits[byte & 0x0f];
    }
    return output;
}

fn parseOptions(args: []const [:0]const u8) !Options {
    var candidate: ?CandidateKind = null;
    var scenario: ?Scenario = null;
    var uid_bits: ?u16 = null;
    var capacity: ?usize = null;
    var mode: ?RunMode = null;
    var samples: ?usize = null;
    var affinity_mask: ?usize = null;
    var index: usize = 1;
    while (index < args.len) {
        const name = args[index];
        index += 1;
        if (index >= args.len) return error.MissingArgumentValue;
        const value = args[index];
        index += 1;
        if (std.mem.eql(u8, name, "--candidate")) {
            if (std.mem.eql(u8, value, "c2")) candidate = .c2 else if (std.mem.eql(u8, value, "b2")) candidate = .b2 else return error.InvalidCandidate;
        } else if (std.mem.eql(u8, name, "--scenario")) {
            inline for (@typeInfo(Scenario).@"enum".fields) |field| {
                if (std.mem.eql(u8, value, field.name)) scenario = @enumFromInt(field.value);
            }
            if (scenario == null) return error.InvalidScenario;
        } else if (std.mem.eql(u8, name, "--uid-bits")) {
            uid_bits = try std.fmt.parseInt(u16, value, 10);
        } else if (std.mem.eql(u8, name, "--capacity")) {
            capacity = try std.fmt.parseInt(usize, value, 10);
        } else if (std.mem.eql(u8, name, "--mode")) {
            if (std.mem.eql(u8, value, "correctness")) mode = .correctness else if (std.mem.eql(u8, value, "timing")) mode = .timing else return error.InvalidRunMode;
        } else if (std.mem.eql(u8, name, "--samples")) {
            samples = try std.fmt.parseInt(usize, value, 10);
        } else if (std.mem.eql(u8, name, "--affinity-mask")) {
            affinity_mask = try std.fmt.parseInt(usize, value, 0);
        } else return error.UnknownArgument;
    }

    const selected_mode = mode orelse return error.MissingRunMode;
    const result = Options{
        .candidate = candidate orelse return error.MissingCandidate,
        .scenario = scenario orelse return error.MissingScenario,
        .uid_bits = uid_bits orelse return error.MissingUidWidth,
        .capacity = capacity orelse return error.MissingCapacity,
        .mode = selected_mode,
        .samples = if (selected_mode == .timing) samples orelse return error.MissingSamples else if (samples) |count| count else samples_for_correctness,
        .affinity_mask = affinity_mask,
    };
    if (result.uid_bits != 128 and result.uid_bits != 256) return error.InvalidUidWidth;
    if (result.capacity != 1024 and result.capacity != 1_048_576) return error.InvalidCapacity;
    if (result.mode == .timing and result.samples != samples_for_timing) return error.InvalidSampleCount;
    if (result.mode == .correctness and result.samples != samples_for_correctness) return error.InvalidSampleCount;
    if (result.mode == .timing and result.affinity_mask == null) return error.TimingRequiresExplicitAffinity;
    if (result.mode == .correctness and result.affinity_mask != null) return error.AffinityOnlyForTiming;
    if (result.affinity_mask) |mask| if (@popCount(mask) != 2) return error.AffinityRequiresTwoProcessors;
    if (compiled_candidate == .both and result.mode == .timing) return error.TimingRequiresSingleCandidateBuild;
    if (compiled_candidate == .c2 and result.candidate != .c2 or
        compiled_candidate == .b2 and result.candidate != .b2) return error.CandidateBuildMismatch;
    return result;
}

fn dispatchProfile(
    comptime selected_candidate: CandidateKind,
    allocator: std.mem.Allocator,
    io: std.Io,
    options: Options,
    placement: ?ProcessMaskReadback,
) !void {
    if (options.uid_bits == 128) {
        if (options.capacity == 1024) {
            try Driver(128, 1024, selected_candidate).run(allocator, io, options, placement);
        } else {
            try Driver(128, 1_048_576, selected_candidate).run(allocator, io, options, placement);
        }
    } else if (options.capacity == 1024) {
        try Driver(256, 1024, selected_candidate).run(allocator, io, options, placement);
    } else {
        try Driver(256, 1_048_576, selected_candidate).run(allocator, io, options, placement);
    }
}

fn dispatchForBuild(
    comptime built: CompiledCandidate,
    allocator: std.mem.Allocator,
    io: std.Io,
    options: Options,
    placement: ?ProcessMaskReadback,
) !void {
    if (built == .both) {
        if (options.mode == .timing) return error.TimingRequiresSingleCandidateBuild;
        switch (options.candidate) {
            .c2 => try dispatchProfile(.c2, allocator, io, options, placement),
            .b2 => try dispatchProfile(.b2, allocator, io, options, placement),
        }
    } else if (built == .c2) {
        if (options.candidate != .c2) return error.CandidateBuildMismatch;
        try dispatchProfile(.c2, allocator, io, options, placement);
    } else {
        if (options.candidate != .b2) return error.CandidateBuildMismatch;
        try dispatchProfile(.b2, allocator, io, options, placement);
    }
}

pub fn main(init: std.process.Init) !void {
    const allocator = init.gpa;
    const args = try init.minimal.args.toSlice(init.arena.allocator());
    const options = try parseOptions(args);
    if (options.mode == .timing and builtin.mode != .ReleaseFast) return error.TimingRequiresReleaseFast;
    const placement = if (options.mode == .timing)
        try applyProcessMask(allocator, options.affinity_mask.?)
    else
        null;
    try dispatchForBuild(compiled_candidate, allocator, init.io, options, placement);
}
