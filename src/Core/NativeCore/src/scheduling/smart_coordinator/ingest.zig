const policy = @import("../../configuration/NativePolicy.g.zig");
const std = @import("std");
const protocol = @import("protocol.zig");
const state = @import("state.zig");

const no_slot: u32 = std.math.maxInt(u32);

pub fn stage(
    session: *state.Session,
    input: *const protocol.CycleInput,
    row_pointer: ?[*]const protocol.InputRow,
    row_capacity: u32,
) protocol.Status {
    const input_status = protocol.validateCycleInput(input);
    if (input_status != .ok) return input_status;
    if (input.config_generation != session.config.generation) return .stale_generation;
    if (input.cycle_sequence <= session.last_cycle_sequence) return .stale_generation;
    if (session.first_cycle_completed and input.observed_at_ms < session.last_observed_at_ms) return .stale_generation;
    if (input.input_count > session.config.max_input_rows or row_capacity < input.input_count) return .buffer_too_small;
    if (input.action_capacity == 0 or input.action_capacity > session.config.max_actions) return .invalid_argument;
    if (input.maximum_actions_this_cycle > session.config.max_actions) return .invalid_argument;
    if (!session.first_cycle_completed and (input.flags & protocol.CycleFlags.authoritative_applied_facts) == 0) {
        return .invalid_argument;
    }
    if (session.requires_authoritative_resync and
        (input.flags & protocol.CycleFlags.authoritative_applied_facts) == 0)
    {
        return .unavailable;
    }

    const rows: []const protocol.InputRow = if (input.input_count == 0)
        &[_]protocol.InputRow{}
    else
        (row_pointer orelse return .invalid_argument)[0..@intCast(input.input_count)];
    // A different stride makes the supplied memory incompatible. Fact fields,
    // including per-row flags and source indices, are validated independently.
    for (rows) |*row| {
        if (row.struct_size != @sizeOf(protocol.InputRow)) return .abi_mismatch;
    }

    session.clearStage();
    // Identity observations survive a bad metric, and every alias of an ambiguous
    // process instance is excluded before any software membership is aggregated.
    for (rows) |*row| {
        if ((row.valid_mask & protocol.InputValidity.process_identity) == 0 or
            row.target_key == 0 or row.process_id == 0 or row.process_start_key == 0) continue;
        const slot = session.findStagedIdentity(row.process_id, row.process_start_key) orelse
            (session.addStagedProcess(newStagedProcess(row)) orelse return .capacity_exceeded);
        const process = &session.staged_processes[slot];
        if (process.target_key != row.target_key) {
            if (!process.identity_conflict) session.staged_invalid_fact_count += 1;
            process.identity_conflict = true;
            session.staged_inventory_complete = false;
        }
    }
    for (rows, 0..) |*row, index| {
        if (protocol.validateInputRow(row, @intCast(index)) != .ok or !validFlagOwnership(row)) {
            session.staged_invalid_fact_count += 1;
            if (row.source_index != index or
                (row.valid_mask & protocol.InputValidity.process_identity) == 0 or
                row.target_key == 0 or row.process_id == 0 or row.process_start_key == 0)
                session.staged_inventory_complete = false;
            continue;
        }
        const metric: protocol.MetricKind = @enumFromInt(row.metric_kind);
        if ((row.valid_mask & protocol.InputValidity.process_identity) == 0) {
            if ((row.valid_mask & protocol.InputValidity.software_identity) == 0) {
                session.staged_invalid_fact_count += 1;
                session.staged_inventory_complete = false;
                continue;
            }
            const software_slot = session.findStagedSoftware(row.software_key) orelse
                (session.addStagedSoftware(newStagedSoftware(row.software_key, row.source_index)) orelse return .capacity_exceeded);
            const software_result = mergeStandaloneSoftwareRow(session, software_slot, row);
            if (software_result != .ok) return software_result;
            continue;
        }
        const process_slot = session.findStagedIdentity(row.process_id, row.process_start_key) orelse
            (session.addStagedProcess(newStagedProcess(row)) orelse return .capacity_exceeded);
        const process = &session.staged_processes[process_slot];
        if (process.identity_conflict) continue;
        const merge_result = mergeProcessRow(session, process_slot, row);
        if (merge_result != .ok) return merge_result;
        if ((row.valid_mask & protocol.InputValidity.metric) != 0 and
            (metric == .gpu_usage_percent or metric == .vram_usage_percent))
        {
            const gpu_result = mergeGpuMetric(session, process_slot, row, metric);
            if (gpu_result != .ok) return gpu_result;
        }
    }

    for (session.staged_processes[0..@intCast(session.staged_process_count)]) |*process| {
        if (process.identity_conflict) continue;
        session.staged_invalid_fact_count += @popCount(process.invalid_mask);
        process.runtime_state = classifyRuntimeState(process.valid_mask, process.input_flags);
        if ((process.valid_mask & protocol.InputValidity.software_identity) == 0) continue;
        const software_slot = session.findStagedSoftware(process.software_key) orelse
            (session.addStagedSoftware(newStagedSoftware(process.software_key, process.source_index)) orelse return .capacity_exceeded);
        process.software_slot = software_slot;
        const result = mergeProcessIntoSoftware(session, software_slot, process);
        if (result != .ok) return result;
    }

    for (session.staged_gpus[0..@intCast(session.staged_gpu_count)]) |gpu| {
        const process = session.staged_processes[gpu.process_slot];
        if (process.identity_conflict) continue;
        if (process.software_slot == no_slot) continue;
        const software_gpu_slot = session.findStagedSoftwareGpu(process.software_slot, gpu.device_index) orelse
            (session.addStagedSoftwareGpu(.{
                .occupied = true,
                .software_slot = process.software_slot,
                .device_index = gpu.device_index,
                .gpu_usage_percent = 0,
                .vram_usage_percent = 0,
                .gpu_score = 0,
                .gpu_score_member_count = 0,
                .gpu_scored_member_count = 0,
                .gpu_score_valid = false,
            }) orelse return .capacity_exceeded);
        const aggregate = &session.staged_software_gpus[software_gpu_slot];
        if ((gpu.valid_mask & state.SampleValidity.gpu) != 0) {
            aggregate.gpu_usage_percent = @min(100, aggregate.gpu_usage_percent + gpu.gpu_usage_percent);
        }
        if ((gpu.valid_mask & state.SampleValidity.vram) != 0) {
            aggregate.vram_usage_percent = @min(100, aggregate.vram_usage_percent + gpu.vram_usage_percent);
        }
        if (gpu.software_gpu_score_valid and !aggregate.score_invalid) {
            if (aggregate.gpu_score_valid and
                (aggregate.gpu_score != gpu.software_gpu_score or
                    aggregate.gpu_score_member_count != gpu.software_gpu_score_member_count))
            {
                aggregate.score_invalid = true;
                session.staged_invalid_fact_count += 1;
            }
            aggregate.gpu_score = gpu.software_gpu_score;
            aggregate.gpu_score_member_count = gpu.software_gpu_score_member_count;
            aggregate.gpu_score_valid = !aggregate.score_invalid;
            aggregate.gpu_scored_member_count += 1;
        }
    }

    validateCanonicalScoreCoverage(session);
    selectSoftwareGpuScore(session);
    for (session.staged_softwares[0..@intCast(session.staged_software_count)]) |software|
        session.staged_invalid_fact_count += @popCount(software.invalid_mask);
    return .ok;
}

pub const Measure = struct {
    processes: u32 = 0,
    softwares: u32 = 0,
    gpus: u32 = 0,
    actions: u32 = 0,
};

// Counts what stage() and the candidate pass will hold before any column exists,
// so each column is opened at its own entity count. The predicates mirror
// stage(): identities by (pid, start), keys of accepted software rows, devices of
// accepted process GPU metrics. Rows stage() drops later (identity conflicts,
// disagreeing keys) only make a count larger, never smaller. `actions` is exactly
// requiredActionCapacity() after staging. `table` holds hashIndexCapacity(rows)
// slots and is reused by each pass.
pub fn measure(session: *const state.Session, rows: []const protocol.InputRow, table: []u32) Measure {
    var result: Measure = .{};
    var new_processes: usize = 0;
    @memset(table, 0);
    for (rows, 0..) |*row, index| {
        if ((row.valid_mask & protocol.InputValidity.process_identity) == 0 or
            row.target_key == 0 or row.process_id == 0 or row.process_start_key == 0) continue;
        if (!insertDistinct(table, rows, @intCast(index), state.processHash(0, row.process_id, row.process_start_key), sameIdentity)) continue;
        result.processes += 1;
        // stage() keeps the first row's target key; requiredActionCapacity looks it up.
        if (session.findProcess(row.target_key, row.process_id, row.process_start_key) == null) new_processes += 1;
    }
    @memset(table, 0);
    for (rows, 0..) |*row, index| {
        if ((row.valid_mask & protocol.InputValidity.software_identity) == 0 or !stageAccepts(row, index)) continue;
        if (insertDistinct(table, rows, @intCast(index), state.softwareHash(row.software_key), sameSoftware)) result.softwares += 1;
    }
    @memset(table, 0);
    for (rows, 0..) |*row, index| {
        if (!isProcessGpuMetric(row) or !stageAccepts(row, index)) continue;
        if (insertDistinct(table, rows, @intCast(index), state.gpuHash(0, row.process_id, row.process_start_key, row.device_index), sameGpu)) result.gpus += 1;
    }
    var retained: usize = 0;
    for (session.processes) |process| if (process.occupied) {
        retained += 1;
    };
    result.actions = @intCast(@min(retained + new_processes, session.config.max_processes));
    return result;
}

fn stageAccepts(row: *const protocol.InputRow, index: usize) bool {
    return protocol.validateInputRow(row, @intCast(index)) == .ok and validFlagOwnership(row);
}

fn isProcessGpuMetric(row: *const protocol.InputRow) bool {
    return (row.valid_mask & protocol.InputValidity.process_identity) != 0 and
        (row.valid_mask & protocol.InputValidity.metric) != 0 and
        (row.metric_kind == @intFromEnum(protocol.MetricKind.gpu_usage_percent) or
            row.metric_kind == @intFromEnum(protocol.MetricKind.vram_usage_percent));
}

// Returns true for the first row of its key. The table keeps at least half of
// its slots empty, so an empty slot always ends the probe; a probe that found
// none would only count the row again, which keeps the measure an upper bound.
fn insertDistinct(
    table: []u32,
    rows: []const protocol.InputRow,
    index: u32,
    hash: u64,
    comptime same: fn (*const protocol.InputRow, *const protocol.InputRow) bool,
) bool {
    const mask = table.len - 1;
    var cursor: usize = @intCast(hash & @as(u64, @intCast(mask)));
    var probes: usize = 0;
    while (probes < table.len) : (probes += 1) {
        const encoded = table[cursor];
        if (encoded == 0) {
            table[cursor] = index + 1;
            return true;
        }
        if (same(&rows[encoded - 1], &rows[index])) return false;
        cursor = (cursor + 1) & mask;
    }
    return true;
}

fn sameIdentity(left: *const protocol.InputRow, right: *const protocol.InputRow) bool {
    return left.process_id == right.process_id and left.process_start_key == right.process_start_key;
}

fn sameSoftware(left: *const protocol.InputRow, right: *const protocol.InputRow) bool {
    return left.software_key == right.software_key;
}

fn sameGpu(left: *const protocol.InputRow, right: *const protocol.InputRow) bool {
    return sameIdentity(left, right) and left.device_index == right.device_index;
}

fn validFlagOwnership(row: *const protocol.InputRow) bool {
    const surface_flags = protocol.InputFlags.running | protocol.InputFlags.foreground_focused |
        protocol.InputFlags.has_visible_window | protocol.InputFlags.has_background_window |
        protocol.InputFlags.has_hidden_window;
    if ((row.valid_mask & protocol.InputValidity.surface_facts) != 0 and
        (row.flags & protocol.InputFlags.running) == 0 and
        (row.flags & (surface_flags & ~protocol.InputFlags.running)) != 0) return false;
    const eligibility_flags = protocol.InputFlags.can_apply_process_policy |
        protocol.InputFlags.hardware_scheduling_eligible;
    if ((row.flags & surface_flags) != 0 and (row.valid_mask & protocol.InputValidity.surface_facts) == 0) return false;
    if ((row.flags & eligibility_flags) != 0 and (row.valid_mask & protocol.InputValidity.eligibility) == 0) return false;
    if ((row.flags & protocol.InputFlags.owns_process_grade) != 0 and
        (row.valid_mask & protocol.InputValidity.applied_process_grade) == 0) return false;
    return true;
}

fn newStagedProcess(row: *const protocol.InputRow) state.StagedProcess {
    return .{
        .occupied = true,
        .target_key = row.target_key,
        .software_key = 0,
        .process_start_key = row.process_start_key,
        .process_id = row.process_id,
        .source_index = row.source_index,
        .valid_mask = protocol.InputValidity.process_identity,
        .input_flags = 0,
        .software_kind = @intFromEnum(protocol.SoftwareKind.unknown),
        .protection_level = 0,
        .runtime_state = @intFromEnum(protocol.RuntimeState.unknown),
        .base_score = 0,
        .cpu_usage_percent = 0,
        .cpu_metric_valid = false,
        .cpu_score = 0,
        .cpu_score_valid = false,
        .software_cpu_score = 0,
        .software_cpu_score_member_count = 0,
        .software_cpu_score_valid = false,
        .software_slot = no_slot,
        .applied_process_grade = protocol.ProcessGrade.normal,
    };
}

fn mergeProcessRow(session: *state.Session, slot: u32, row: *const protocol.InputRow) protocol.Status {
    const process = &session.staged_processes[slot];
    process.source_index = @min(process.source_index, row.source_index);
    process.input_flags |= row.flags & (protocol.InputFlags.process_cpu_metrics_complete | protocol.InputFlags.process_gpu_metrics_complete);

    var result = mergeU64Fact(
        &process.valid_mask,
        &process.invalid_mask,
        protocol.InputValidity.software_identity,
        &process.software_key,
        row.software_key,
        row.valid_mask,
    );
    if (result != .ok) return result;
    result = mergeU8Fact(&process.valid_mask, &process.invalid_mask, protocol.InputValidity.software_kind, &process.software_kind, row.software_kind, row.valid_mask);
    if (result != .ok) return result;
    result = mergeF64Fact(&process.valid_mask, &process.invalid_mask, protocol.InputValidity.base_score, &process.base_score, row.base_score, row.valid_mask);
    if (result != .ok) return result;
    result = mergeFlagFact(
        &process.valid_mask,
        &process.invalid_mask,
        protocol.InputValidity.surface_facts,
        &process.input_flags,
        row.flags,
        protocol.InputFlags.running | protocol.InputFlags.foreground_focused |
            protocol.InputFlags.has_visible_window | protocol.InputFlags.has_background_window |
            protocol.InputFlags.has_hidden_window,
        row.valid_mask,
    );
    if (result != .ok) return result;
    result = mergeFlagFact(
        &process.valid_mask,
        &process.invalid_mask,
        protocol.InputValidity.eligibility,
        &process.input_flags,
        row.flags,
        protocol.InputFlags.can_apply_process_policy | protocol.InputFlags.hardware_scheduling_eligible,
        row.valid_mask,
    );
    if (result != .ok) return result;
    result = mergeU8Fact(&process.valid_mask, &process.invalid_mask, protocol.InputValidity.protection, &process.protection_level, row.protection_level, row.valid_mask);
    if (result != .ok) return result;
    result = mergeAppliedProcessFact(process, row);
    if (result != .ok) return result;

    const metric: protocol.MetricKind = @enumFromInt(row.metric_kind);
    const metric_valid = (row.valid_mask & protocol.InputValidity.metric) != 0;
    if (metric_valid or metric == .cpu_usage_percent) {
        switch (metric) {
            .cpu_usage_percent => {
                if (metric_valid) {
                    process.cpu_usage_percent = if (process.cpu_metric_valid)
                        @max(process.cpu_usage_percent, row.metric_value)
                    else
                        row.metric_value;
                    process.cpu_metric_valid = true;
                }
                if ((row.valid_mask & protocol.InputValidity.process_score) != 0 and !process.cpu_score_invalid) {
                    if (process.cpu_score_valid and process.cpu_score != row.process_score) {
                        process.cpu_score_invalid = true;
                        session.staged_invalid_fact_count += 1;
                    }
                    process.cpu_score = row.process_score;
                    process.cpu_score_valid = !process.cpu_score_invalid;
                }
                if ((row.valid_mask & protocol.InputValidity.software_score) != 0 and !process.software_cpu_score_invalid) {
                    if (process.software_cpu_score_valid and
                        (process.software_cpu_score != row.software_score or
                            process.software_cpu_score_member_count != row.score_member_count))
                    {
                        process.software_cpu_score_invalid = true;
                        session.staged_invalid_fact_count += 1;
                    }
                    process.software_cpu_score = row.software_score;
                    process.software_cpu_score_member_count = row.score_member_count;
                    process.software_cpu_score_valid = !process.software_cpu_score_invalid;
                }
            },
            .gpu_usage_percent, .vram_usage_percent => {},
            .none => return .invalid_argument,
        }
    }
    return .ok;
}

fn mergeAppliedProcessFact(process: *state.StagedProcess, row: *const protocol.InputRow) protocol.Status {
    const bit = protocol.InputValidity.applied_process_grade;
    if ((row.valid_mask & bit) == 0 or (process.invalid_mask & bit) != 0) return .ok;
    const owned = (row.flags & protocol.InputFlags.owns_process_grade) != 0;
    if ((process.valid_mask & bit) != 0) {
        const current_owned = (process.input_flags & protocol.InputFlags.owns_process_grade) != 0;
        if (process.applied_process_grade != row.applied_process_grade or current_owned != owned) {
            process.valid_mask &= ~bit;
            process.invalid_mask |= bit;
            process.input_flags &= ~protocol.InputFlags.owns_process_grade;
        }
        return .ok;
    }
    process.valid_mask |= bit;
    process.applied_process_grade = row.applied_process_grade;
    if (owned) process.input_flags |= protocol.InputFlags.owns_process_grade;
    return .ok;
}

fn mergeGpuMetric(session: *state.Session, process_slot: u32, row: *const protocol.InputRow, metric: protocol.MetricKind) protocol.Status {
    const process = session.staged_processes[process_slot];
    const slot = session.findStagedGpu(process.target_key, process.process_id, process.process_start_key, row.device_index) orelse
        (session.addStagedGpu(.{
            .occupied = true,
            .process_slot = process_slot,
            .target_key = process.target_key,
            .process_start_key = process.process_start_key,
            .process_id = process.process_id,
            .device_index = row.device_index,
            .valid_mask = 0,
            .gpu_usage_percent = 0,
            .vram_usage_percent = 0,
            .gpu_score = 0,
            .gpu_score_valid = false,
            .software_gpu_score = 0,
            .software_gpu_score_member_count = 0,
            .software_gpu_score_valid = false,
        }) orelse return .capacity_exceeded);
    const gpu = &session.staged_gpus[slot];
    if (metric == .gpu_usage_percent) {
        gpu.gpu_usage_percent = if ((gpu.valid_mask & state.SampleValidity.gpu) != 0)
            @max(gpu.gpu_usage_percent, row.metric_value)
        else
            row.metric_value;
        gpu.valid_mask |= state.SampleValidity.gpu;
        if ((row.valid_mask & protocol.InputValidity.process_score) != 0 and !gpu.score_invalid) {
            if (gpu.gpu_score_valid and gpu.gpu_score != row.process_score) {
                gpu.score_invalid = true;
                session.staged_invalid_fact_count += 1;
            }
            gpu.gpu_score = row.process_score;
            gpu.gpu_score_valid = !gpu.score_invalid;
        }
        if ((row.valid_mask & protocol.InputValidity.software_score) != 0 and !gpu.software_score_invalid) {
            if (gpu.software_gpu_score_valid and
                (gpu.software_gpu_score != row.software_score or
                    gpu.software_gpu_score_member_count != row.score_member_count))
            {
                gpu.software_score_invalid = true;
                session.staged_invalid_fact_count += 1;
            }
            gpu.software_gpu_score = row.software_score;
            gpu.software_gpu_score_member_count = row.score_member_count;
            gpu.software_gpu_score_valid = !gpu.software_score_invalid;
        }
    } else {
        gpu.vram_usage_percent = if ((gpu.valid_mask & state.SampleValidity.vram) != 0)
            @max(gpu.vram_usage_percent, row.metric_value)
        else
            row.metric_value;
        gpu.valid_mask |= state.SampleValidity.vram;
    }
    return .ok;
}

fn newStagedSoftware(software_key: u64, source_index: u32) state.StagedSoftware {
    return .{
        .occupied = true,
        .software_key = software_key,
        .source_index = source_index,
        .valid_mask = protocol.InputValidity.software_identity,
        .input_flags = 0,
        .software_kind = @intFromEnum(protocol.SoftwareKind.unknown),
        .protection_level = 0,
        .runtime_state = @intFromEnum(protocol.RuntimeState.unknown),
        .base_score = 0,
        .cpu_score = 0,
        .cpu_score_member_count = 0,
        .cpu_scored_member_count = 0,
        .cpu_score_valid = false,
        .member_count = 0,
        .member_signature = 0,
        .critical_fact = false,
        .selected_gpu_present = false,
        .selected_gpu_index = 0,
        .selected_gpu_usage = 0,
        .selected_vram_usage = 0,
        .selected_gpu_score = 0,
        .selected_gpu_score_member_count = 0,
        .selected_gpu_score_valid = false,
    };
}

fn mergeStandaloneSoftwareRow(
    session: *state.Session,
    slot: u32,
    row: *const protocol.InputRow,
) protocol.Status {
    const software = &session.staged_softwares[slot];
    software.source_index = @min(software.source_index, row.source_index);

    var result = mergeU8Fact(
        &software.valid_mask,
        &software.invalid_mask,
        protocol.InputValidity.software_kind,
        &software.software_kind,
        row.software_kind,
        row.valid_mask,
    );
    if (result != .ok) return result;
    result = mergeF64Fact(
        &software.valid_mask,
        &software.invalid_mask,
        protocol.InputValidity.base_score,
        &software.base_score,
        row.base_score,
        row.valid_mask,
    );
    if (result != .ok) return result;
    if ((row.valid_mask & protocol.InputValidity.surface_facts) != 0) {
        software.valid_mask |= protocol.InputValidity.surface_facts;
        software.runtime_state = higherRuntimeState(
            software.runtime_state,
            classifyRuntimeState(row.valid_mask, row.flags),
        );
    }
    return .ok;
}

fn mergeProcessIntoSoftware(session: *state.Session, slot: u32, process: *const state.StagedProcess) protocol.Status {
    const software = &session.staged_softwares[slot];
    software.source_index = @min(software.source_index, process.source_index);
    software.member_count += 1;
    software.member_signature ^= state.processHash(process.target_key, process.process_id, process.process_start_key);
    software.runtime_state = higherRuntimeState(software.runtime_state, process.runtime_state);
    if (process.software_cpu_score_valid and !software.cpu_score_invalid) {
        if (software.cpu_score_valid and
            (software.cpu_score != process.software_cpu_score or
                software.cpu_score_member_count != process.software_cpu_score_member_count))
        {
            software.cpu_score_invalid = true;
            session.staged_invalid_fact_count += 1;
        }
        software.cpu_score = process.software_cpu_score;
        software.cpu_score_member_count = process.software_cpu_score_member_count;
        software.cpu_score_valid = !software.cpu_score_invalid;
        software.cpu_scored_member_count += 1;
    }
    if ((process.valid_mask & protocol.InputValidity.base_score) != 0) {
        if ((software.valid_mask & protocol.InputValidity.base_score) == 0) software.base_score = process.base_score else software.base_score = @max(software.base_score, process.base_score);
        software.valid_mask |= protocol.InputValidity.base_score;
    }
    if ((process.valid_mask & protocol.InputValidity.protection) != 0) {
        software.protection_level = @max(software.protection_level, process.protection_level);
        software.valid_mask |= protocol.InputValidity.protection;
    }

    var result = mergeU8Fact(&software.valid_mask, &software.invalid_mask, protocol.InputValidity.software_kind, &software.software_kind, process.software_kind, process.valid_mask);
    if (result != .ok) return result;
    result = mergeFlagFact(
        &software.valid_mask,
        &software.invalid_mask,
        protocol.InputValidity.eligibility,
        &software.input_flags,
        process.input_flags,
        protocol.InputFlags.hardware_scheduling_eligible,
        process.valid_mask,
    );
    if (result != .ok) return result;

    const hardware_eligible = (process.valid_mask & protocol.InputValidity.eligibility) != 0 and
        (process.input_flags & protocol.InputFlags.hardware_scheduling_eligible) != 0;
    if (hardware_eligible and isCriticalProcess(session, process)) software.critical_fact = true;
    return .ok;
}

fn selectSoftwareGpuScore(session: *state.Session) void {
    for (session.staged_software_gpus[0..@intCast(session.staged_software_gpu_count)]) |candidate| {
        const software = &session.staged_softwares[candidate.software_slot];
        if (!software.selected_gpu_present or candidate.gpu_usage_percent > software.selected_gpu_usage or
            (candidate.gpu_usage_percent == software.selected_gpu_usage and candidate.vram_usage_percent > software.selected_vram_usage) or
            (candidate.gpu_usage_percent == software.selected_gpu_usage and
                candidate.vram_usage_percent == software.selected_vram_usage and candidate.device_index < software.selected_gpu_index))
        {
            software.selected_gpu_present = true;
            software.selected_gpu_index = candidate.device_index;
            software.selected_gpu_usage = candidate.gpu_usage_percent;
            software.selected_vram_usage = candidate.vram_usage_percent;
            software.selected_gpu_score = candidate.gpu_score;
            software.selected_gpu_score_member_count = candidate.gpu_score_member_count;
            software.selected_gpu_score_valid = candidate.gpu_score_valid;
        }
    }
}

fn validateCanonicalScoreCoverage(session: *state.Session) void {
    for (session.staged_softwares[0..@intCast(session.staged_software_count)]) |*software| {
        if (software.member_count == 0) continue;
        if (software.cpu_score_valid and
            (software.cpu_scored_member_count != software.cpu_score_member_count or
                software.cpu_score_member_count == 0))
        {
            software.cpu_score_valid = false;
            session.staged_invalid_fact_count += 1;
        }
    }
    for (session.staged_software_gpus[0..@intCast(session.staged_software_gpu_count)]) |*gpu| {
        if (gpu.gpu_usage_percent == 0 and gpu.gpu_scored_member_count == 0) continue;
        if (gpu.gpu_score_valid and
            (gpu.gpu_scored_member_count != gpu.gpu_score_member_count or
                gpu.gpu_score_member_count == 0))
        {
            gpu.gpu_score_valid = false;
            session.staged_invalid_fact_count += 1;
        }
    }
}

fn classifyRuntimeState(valid_mask: u64, flags: u64) u8 {
    if ((valid_mask & protocol.InputValidity.surface_facts) == 0) return @intFromEnum(protocol.RuntimeState.unknown);
    if ((flags & protocol.InputFlags.running) == 0) return @intFromEnum(protocol.RuntimeState.not_running);
    if ((flags & protocol.InputFlags.foreground_focused) != 0) return @intFromEnum(protocol.RuntimeState.foreground_focused);
    if ((flags & protocol.InputFlags.has_visible_window) != 0) return @intFromEnum(protocol.RuntimeState.foreground_unfocused);
    if ((flags & protocol.InputFlags.has_background_window) != 0) return @intFromEnum(protocol.RuntimeState.background_window);
    if ((flags & protocol.InputFlags.has_hidden_window) != 0) return @intFromEnum(protocol.RuntimeState.tray_only);
    return @intFromEnum(protocol.RuntimeState.background_process);
}

fn higherRuntimeState(left: u8, right: u8) u8 {
    return if (runtimeRank(right) > runtimeRank(left)) right else left;
}

fn runtimeRank(value: u8) u32 {
    return switch (@as(protocol.RuntimeState, @enumFromInt(value))) {
        .foreground_focused => policy.runtime_rank_focused,
        .foreground_unfocused => policy.runtime_rank_unfocused,
        .background_window => policy.runtime_rank_background_window,
        .tray_only => policy.runtime_rank_tray,
        .background_process => policy.runtime_rank_background_process,
        .unknown, .not_running => policy.runtime_rank_not_running,
    };
}

fn isCriticalProcess(session: *const state.Session, process: *const state.StagedProcess) bool {
    if ((process.valid_mask & protocol.InputValidity.software_kind) != 0) {
        const kind: protocol.SoftwareKind = @enumFromInt(process.software_kind);
        if (kind == .game or kind == .high_performance) return true;
    }
    return (process.valid_mask & protocol.InputValidity.base_score) != 0 and
        process.base_score >= session.config.high_tier_minimum_base_score;
}

fn mergeU64Fact(target_mask: *u64, invalid_mask: *u64, bit: u64, target: *u64, value: u64, source_mask: u64) protocol.Status {
    if ((source_mask & bit) == 0 or (invalid_mask.* & bit) != 0) return .ok;
    if ((target_mask.* & bit) != 0 and target.* != value) {
        target_mask.* &= ~bit;
        invalid_mask.* |= bit;
        return .ok;
    }
    target.* = value;
    target_mask.* |= bit;
    return .ok;
}

fn mergeU8Fact(target_mask: *u64, invalid_mask: *u64, bit: u64, target: *u8, value: u8, source_mask: u64) protocol.Status {
    if ((source_mask & bit) == 0 or (invalid_mask.* & bit) != 0) return .ok;
    if ((target_mask.* & bit) != 0 and target.* != value) {
        target_mask.* &= ~bit;
        invalid_mask.* |= bit;
        return .ok;
    }
    target.* = value;
    target_mask.* |= bit;
    return .ok;
}

fn mergeF64Fact(target_mask: *u64, invalid_mask: *u64, bit: u64, target: *f64, value: f64, source_mask: u64) protocol.Status {
    if ((source_mask & bit) == 0 or (invalid_mask.* & bit) != 0) return .ok;
    if ((target_mask.* & bit) != 0 and target.* != value) {
        target_mask.* &= ~bit;
        invalid_mask.* |= bit;
        return .ok;
    }
    target.* = value;
    target_mask.* |= bit;
    return .ok;
}

fn mergeFlagFact(
    target_mask: *u64,
    invalid_mask: *u64,
    bit: u64,
    target_flags: *u64,
    source_flags: u64,
    field_flags: u64,
    source_mask: u64,
) protocol.Status {
    if ((source_mask & bit) == 0 or (invalid_mask.* & bit) != 0) return .ok;
    const value = source_flags & field_flags;
    if ((target_mask.* & bit) != 0 and (target_flags.* & field_flags) != value) {
        target_mask.* &= ~bit;
        target_flags.* &= ~field_flags;
        invalid_mask.* |= bit;
        return .ok;
    }
    target_flags.* = (target_flags.* & ~field_flags) | value;
    target_mask.* |= bit;
    return .ok;
}
