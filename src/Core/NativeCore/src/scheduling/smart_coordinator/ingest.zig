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
    if (input.action_capacity < session.config.max_actions) return .buffer_too_small;
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
    for (rows, 0..) |*row, index| {
        const result = protocol.validateInputRow(row, @intCast(index));
        if (result != .ok) return result;
        if (!validFlagOwnership(row)) return .invalid_argument;
        const has_score = (row.valid_mask & (protocol.InputValidity.process_score |
            protocol.InputValidity.software_score)) != 0;
        if (has_score) {
            const metric: protocol.MetricKind = @enumFromInt(row.metric_kind);
            const required_fingerprint = switch (metric) {
                .cpu_usage_percent => protocol.CycleValidity.cpu_score_source_fingerprint,
                .gpu_usage_percent => protocol.CycleValidity.gpu_score_source_fingerprint,
                else => return .invalid_argument,
            };
            if ((input.valid_mask & required_fingerprint) == 0) return .invalid_argument;
        }
    }

    session.clearStage();
    for (rows) |*row| {
        const metric: protocol.MetricKind = @enumFromInt(row.metric_kind);
        if ((row.valid_mask & protocol.InputValidity.process_identity) == 0) {
            if ((row.valid_mask & protocol.InputValidity.software_identity) == 0) return .invalid_argument;
            const software_slot = session.findStagedSoftware(row.software_key) orelse
                (session.addStagedSoftware(newStagedSoftware(row.software_key, row.source_index)) orelse return .capacity_exceeded);
            const software_result = mergeStandaloneSoftwareRow(session, software_slot, row);
            if (software_result != .ok) return software_result;
            continue;
        }
        const process_slot = session.findStagedProcess(row.target_key, row.process_id, row.process_start_key) orelse
            (session.addStagedProcess(newStagedProcess(row)) orelse return .capacity_exceeded);
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
        if (gpu.gpu_score_valid) {
            if (aggregate.gpu_score_valid and
                (aggregate.gpu_score != gpu.software_gpu_score or
                    aggregate.gpu_score_member_count != gpu.software_gpu_score_member_count))
            {
                return .conflicting_facts;
            }
            aggregate.gpu_score = gpu.software_gpu_score;
            aggregate.gpu_score_member_count = gpu.software_gpu_score_member_count;
            aggregate.gpu_score_valid = true;
            aggregate.gpu_scored_member_count += 1;
        }
    }

    const score_result = validateCanonicalScoreCoverage(session);
    if (score_result != .ok) return score_result;
    selectSoftwareGpuScore(session);
    if ((input.flags & protocol.CycleFlags.authoritative_applied_facts) != 0 and
        !hasCompleteAuthoritativeAppliedFacts(session))
    {
        return .invalid_argument;
    }
    return .ok;
}

fn hasCompleteAuthoritativeAppliedFacts(session: *const state.Session) bool {
    for (session.staged_processes[0..@intCast(session.staged_process_count)]) |process| {
        if ((process.valid_mask & protocol.InputValidity.applied_process_grade) == 0) return false;
    }
    for (session.staged_softwares[0..@intCast(session.staged_software_count)]) |software| {
        if ((software.valid_mask & protocol.InputValidity.applied_cpu_grade) == 0) return false;
        if ((software.valid_mask & protocol.InputValidity.applied_gpu_grade) == 0) return false;
    }
    return true;
}

fn validFlagOwnership(row: *const protocol.InputRow) bool {
    const surface_flags = protocol.InputFlags.running | protocol.InputFlags.foreground_focused |
        protocol.InputFlags.has_visible_window | protocol.InputFlags.has_background_window |
        protocol.InputFlags.has_hidden_window;
    const eligibility_flags = protocol.InputFlags.can_apply_process_policy |
        protocol.InputFlags.can_apply_adapter_policy | protocol.InputFlags.hardware_scheduling_eligible;
    if ((row.flags & surface_flags) != 0 and (row.valid_mask & protocol.InputValidity.surface_facts) == 0) return false;
    if ((row.flags & eligibility_flags) != 0 and (row.valid_mask & protocol.InputValidity.eligibility) == 0) return false;
    if ((row.flags & protocol.InputFlags.owns_process_grade) != 0 and
        (row.valid_mask & protocol.InputValidity.applied_process_grade) == 0) return false;
    if ((row.flags & protocol.InputFlags.owns_cpu_grade) != 0 and
        (row.valid_mask & protocol.InputValidity.applied_cpu_grade) == 0) return false;
    if ((row.flags & protocol.InputFlags.owns_gpu_grade) != 0 and
        (row.valid_mask & protocol.InputValidity.applied_gpu_grade) == 0) return false;
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
        .input_flags = row.flags & (protocol.InputFlags.process_cpu_metrics_complete | protocol.InputFlags.process_gpu_metrics_complete),
        .software_kind = @intFromEnum(protocol.SoftwareKind.unknown),
        .protection_level = 0,
        .runtime_state = @intFromEnum(protocol.RuntimeState.unknown),
        .cpu_capability_mask = 0,
        .gpu_capability_mask = 0,
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
        .applied_cpu_grade = @intFromEnum(protocol.AdapterGrade.normal),
        .applied_gpu_grade = @intFromEnum(protocol.AdapterGrade.normal),
        .applied_epoch = 0,
    };
}

fn mergeProcessRow(session: *state.Session, slot: u32, row: *const protocol.InputRow) protocol.Status {
    const process = &session.staged_processes[slot];
    process.source_index = @min(process.source_index, row.source_index);
    process.input_flags |= row.flags & (protocol.InputFlags.process_cpu_metrics_complete | protocol.InputFlags.process_gpu_metrics_complete);

    var result = mergeU64Fact(
        &process.valid_mask,
        protocol.InputValidity.software_identity,
        &process.software_key,
        row.software_key,
        row.valid_mask,
    );
    if (result != .ok) return result;
    result = mergeU8Fact(&process.valid_mask, protocol.InputValidity.software_kind, &process.software_kind, row.software_kind, row.valid_mask);
    if (result != .ok) return result;
    result = mergeF64Fact(&process.valid_mask, protocol.InputValidity.base_score, &process.base_score, row.base_score, row.valid_mask);
    if (result != .ok) return result;
    result = mergeFlagFact(
        &process.valid_mask,
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
        protocol.InputValidity.eligibility,
        &process.input_flags,
        row.flags,
        protocol.InputFlags.can_apply_process_policy | protocol.InputFlags.can_apply_adapter_policy |
            protocol.InputFlags.hardware_scheduling_eligible,
        row.valid_mask,
    );
    if (result != .ok) return result;
    result = mergeU8Fact(&process.valid_mask, protocol.InputValidity.protection, &process.protection_level, row.protection_level, row.valid_mask);
    if (result != .ok) return result;
    result = mergeU8Fact(&process.valid_mask, protocol.InputValidity.cpu_capabilities, &process.cpu_capability_mask, row.cpu_capability_mask, row.valid_mask);
    if (result != .ok) return result;
    result = mergeU8Fact(&process.valid_mask, protocol.InputValidity.gpu_capabilities, &process.gpu_capability_mask, row.gpu_capability_mask, row.valid_mask);
    if (result != .ok) return result;
    result = mergeAppliedProcessFact(process, row);
    if (result != .ok) return result;
    result = mergeAppliedAdapterFact(process, row, true);
    if (result != .ok) return result;
    result = mergeAppliedAdapterFact(process, row, false);
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
                if ((row.valid_mask & protocol.InputValidity.process_score) != 0) {
                    if (process.cpu_score_valid and process.cpu_score != row.process_score) {
                        return .conflicting_facts;
                    }
                    process.cpu_score = row.process_score;
                    process.cpu_score_valid = true;
                }
                if ((row.valid_mask & protocol.InputValidity.software_score) != 0) {
                    if (process.software_cpu_score_valid and
                        (process.software_cpu_score != row.software_score or
                            process.software_cpu_score_member_count != row.score_member_count))
                    {
                        return .conflicting_facts;
                    }
                    process.software_cpu_score = row.software_score;
                    process.software_cpu_score_member_count = row.score_member_count;
                    process.software_cpu_score_valid = true;
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
    if ((row.valid_mask & bit) == 0) return .ok;
    const owned = (row.flags & protocol.InputFlags.owns_process_grade) != 0;
    if ((process.valid_mask & bit) != 0) {
        const current_owned = (process.input_flags & protocol.InputFlags.owns_process_grade) != 0;
        if (process.applied_process_grade != row.applied_process_grade or current_owned != owned or
            (owned and process.applied_epoch != row.applied_epoch)) return .conflicting_facts;
        return .ok;
    }
    process.valid_mask |= bit;
    process.applied_process_grade = row.applied_process_grade;
    process.applied_epoch = row.applied_epoch;
    if (owned) process.input_flags |= protocol.InputFlags.owns_process_grade;
    return .ok;
}

fn mergeAppliedAdapterFact(process: *state.StagedProcess, row: *const protocol.InputRow, comptime cpu: bool) protocol.Status {
    const bit = if (cpu) protocol.InputValidity.applied_cpu_grade else protocol.InputValidity.applied_gpu_grade;
    const owned_flag = if (cpu) protocol.InputFlags.owns_cpu_grade else protocol.InputFlags.owns_gpu_grade;
    if ((row.valid_mask & bit) == 0) return .ok;
    const grade = if (cpu) row.applied_cpu_grade else row.applied_gpu_grade;
    const owned = (row.flags & owned_flag) != 0;
    if ((process.valid_mask & bit) != 0) {
        const current_grade = if (cpu) process.applied_cpu_grade else process.applied_gpu_grade;
        const current_owned = (process.input_flags & owned_flag) != 0;
        if (current_grade != grade or current_owned != owned or
            (owned and process.applied_epoch != row.applied_epoch)) return .conflicting_facts;
        return .ok;
    }
    process.valid_mask |= bit;
    if (cpu) process.applied_cpu_grade = grade else process.applied_gpu_grade = grade;
    if (process.applied_epoch != 0 and owned and process.applied_epoch != row.applied_epoch) return .conflicting_facts;
    if (owned) {
        process.input_flags |= owned_flag;
        process.applied_epoch = row.applied_epoch;
    }
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
        if ((row.valid_mask & protocol.InputValidity.process_score) != 0) {
            if (gpu.gpu_score_valid and gpu.gpu_score != row.process_score) {
                return .conflicting_facts;
            }
            gpu.gpu_score = row.process_score;
            gpu.gpu_score_valid = true;
        }
        if ((row.valid_mask & protocol.InputValidity.software_score) != 0) {
            if (gpu.software_gpu_score_valid and
                (gpu.software_gpu_score != row.software_score or
                    gpu.software_gpu_score_member_count != row.score_member_count))
            {
                return .conflicting_facts;
            }
            gpu.software_gpu_score = row.software_score;
            gpu.software_gpu_score_member_count = row.score_member_count;
            gpu.software_gpu_score_valid = true;
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
        .runtime_state = @intFromEnum(protocol.RuntimeState.unknown),
        .cpu_capability_mask = 0,
        .gpu_capability_mask = 0,
        .base_score = 0,
        .cpu_score = 0,
        .cpu_score_member_count = 0,
        .cpu_scored_member_count = 0,
        .cpu_score_valid = false,
        .member_count = 0,
        .member_signature = 0,
        .critical_fact = false,
        .applied_cpu_grade = @intFromEnum(protocol.AdapterGrade.normal),
        .applied_gpu_grade = @intFromEnum(protocol.AdapterGrade.normal),
        .applied_epoch = 0,
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
        protocol.InputValidity.software_kind,
        &software.software_kind,
        row.software_kind,
        row.valid_mask,
    );
    if (result != .ok) return result;
    result = mergeF64Fact(
        &software.valid_mask,
        protocol.InputValidity.base_score,
        &software.base_score,
        row.base_score,
        row.valid_mask,
    );
    if (result != .ok) return result;
    result = mergeU8Fact(
        &software.valid_mask,
        protocol.InputValidity.cpu_capabilities,
        &software.cpu_capability_mask,
        row.cpu_capability_mask,
        row.valid_mask,
    );
    if (result != .ok) return result;
    result = mergeU8Fact(
        &software.valid_mask,
        protocol.InputValidity.gpu_capabilities,
        &software.gpu_capability_mask,
        row.gpu_capability_mask,
        row.valid_mask,
    );
    if (result != .ok) return result;
    result = mergeFlagFact(
        &software.valid_mask,
        protocol.InputValidity.eligibility,
        &software.input_flags,
        row.flags,
        protocol.InputFlags.can_apply_adapter_policy,
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
    result = mergeStandaloneSoftwareAppliedFact(software, row, true);
    if (result != .ok) return result;
    return mergeStandaloneSoftwareAppliedFact(software, row, false);
}

fn mergeStandaloneSoftwareAppliedFact(
    software: *state.StagedSoftware,
    row: *const protocol.InputRow,
    comptime cpu: bool,
) protocol.Status {
    const bit = if (cpu) protocol.InputValidity.applied_cpu_grade else protocol.InputValidity.applied_gpu_grade;
    const owned_flag = if (cpu) protocol.InputFlags.owns_cpu_grade else protocol.InputFlags.owns_gpu_grade;
    if ((row.valid_mask & bit) == 0) return .ok;
    const grade = if (cpu) row.applied_cpu_grade else row.applied_gpu_grade;
    const owned = (row.flags & owned_flag) != 0;
    if ((software.valid_mask & bit) != 0) {
        const current_grade = if (cpu) software.applied_cpu_grade else software.applied_gpu_grade;
        const current_owned = (software.input_flags & owned_flag) != 0;
        if (current_grade != grade or current_owned != owned or
            (owned and software.applied_epoch != row.applied_epoch)) return .conflicting_facts;
        return .ok;
    }
    software.valid_mask |= bit;
    if (cpu) software.applied_cpu_grade = grade else software.applied_gpu_grade = grade;
    if (software.applied_epoch != 0 and owned and software.applied_epoch != row.applied_epoch) return .conflicting_facts;
    if (owned) {
        software.input_flags |= owned_flag;
        software.applied_epoch = row.applied_epoch;
    }
    return .ok;
}

fn mergeProcessIntoSoftware(session: *state.Session, slot: u32, process: *const state.StagedProcess) protocol.Status {
    const software = &session.staged_softwares[slot];
    software.source_index = @min(software.source_index, process.source_index);
    software.member_count += 1;
    software.member_signature ^= state.processHash(process.target_key, process.process_id, process.process_start_key);
    software.runtime_state = higherRuntimeState(software.runtime_state, process.runtime_state);
    if (process.software_cpu_score_valid) {
        if (software.cpu_score_valid and
            (software.cpu_score != process.software_cpu_score or
                software.cpu_score_member_count != process.software_cpu_score_member_count))
        {
            return .conflicting_facts;
        }
        software.cpu_score = process.software_cpu_score;
        software.cpu_score_member_count = process.software_cpu_score_member_count;
        software.cpu_score_valid = true;
        software.cpu_scored_member_count += 1;
    }
    if ((process.valid_mask & protocol.InputValidity.base_score) != 0) {
        if ((software.valid_mask & protocol.InputValidity.base_score) == 0) software.base_score = process.base_score else software.base_score = @max(software.base_score, process.base_score);
        software.valid_mask |= protocol.InputValidity.base_score;
    }

    var result = mergeU8Fact(&software.valid_mask, protocol.InputValidity.software_kind, &software.software_kind, process.software_kind, process.valid_mask);
    if (result != .ok) return result;
    result = mergeU8Fact(&software.valid_mask, protocol.InputValidity.cpu_capabilities, &software.cpu_capability_mask, process.cpu_capability_mask, process.valid_mask);
    if (result != .ok) return result;
    result = mergeU8Fact(&software.valid_mask, protocol.InputValidity.gpu_capabilities, &software.gpu_capability_mask, process.gpu_capability_mask, process.valid_mask);
    if (result != .ok) return result;
    result = mergeFlagFact(
        &software.valid_mask,
        protocol.InputValidity.eligibility,
        &software.input_flags,
        process.input_flags,
        protocol.InputFlags.can_apply_adapter_policy | protocol.InputFlags.hardware_scheduling_eligible,
        process.valid_mask,
    );
    if (result != .ok) return result;
    result = mergeSoftwareAppliedFact(software, process, true);
    if (result != .ok) return result;
    result = mergeSoftwareAppliedFact(software, process, false);
    if (result != .ok) return result;

    const hardware_eligible = (process.valid_mask & protocol.InputValidity.eligibility) != 0 and
        (process.input_flags & protocol.InputFlags.hardware_scheduling_eligible) != 0;
    if (hardware_eligible and isCriticalProcess(session, process)) software.critical_fact = true;
    return .ok;
}

fn mergeSoftwareAppliedFact(software: *state.StagedSoftware, process: *const state.StagedProcess, comptime cpu: bool) protocol.Status {
    const bit = if (cpu) protocol.InputValidity.applied_cpu_grade else protocol.InputValidity.applied_gpu_grade;
    const owned_flag = if (cpu) protocol.InputFlags.owns_cpu_grade else protocol.InputFlags.owns_gpu_grade;
    if ((process.valid_mask & bit) == 0) return .ok;
    const grade = if (cpu) process.applied_cpu_grade else process.applied_gpu_grade;
    const owned = (process.input_flags & owned_flag) != 0;
    if ((software.valid_mask & bit) != 0) {
        const current_grade = if (cpu) software.applied_cpu_grade else software.applied_gpu_grade;
        const current_owned = (software.input_flags & owned_flag) != 0;
        if (current_grade != grade or current_owned != owned or
            (owned and software.applied_epoch != process.applied_epoch)) return .conflicting_facts;
        return .ok;
    }
    software.valid_mask |= bit;
    if (cpu) software.applied_cpu_grade = grade else software.applied_gpu_grade = grade;
    if (software.applied_epoch != 0 and owned and software.applied_epoch != process.applied_epoch) return .conflicting_facts;
    if (owned) {
        software.input_flags |= owned_flag;
        software.applied_epoch = process.applied_epoch;
    }
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

fn validateCanonicalScoreCoverage(session: *const state.Session) protocol.Status {
    for (session.staged_softwares[0..@intCast(session.staged_software_count)]) |software| {
        if (software.member_count == 0) continue;
        if (software.cpu_score_valid and
            (software.cpu_scored_member_count != software.cpu_score_member_count or
                software.cpu_score_member_count == 0))
        {
            return .invalid_argument;
        }
    }
    for (session.staged_software_gpus[0..@intCast(session.staged_software_gpu_count)]) |gpu| {
        if (gpu.gpu_usage_percent == 0 and gpu.gpu_scored_member_count == 0) continue;
        if (gpu.gpu_score_valid and
            (gpu.gpu_scored_member_count != gpu.gpu_score_member_count or
                gpu.gpu_score_member_count == 0))
        {
            return .invalid_argument;
        }
    }
    return .ok;
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

fn runtimeRank(value: u8) u8 {
    return switch (@as(protocol.RuntimeState, @enumFromInt(value))) {
        .foreground_focused => 5,
        .foreground_unfocused => 4,
        .background_window => 3,
        .tray_only => 2,
        .background_process => 1,
        .unknown, .not_running => 0,
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

fn mergeU64Fact(target_mask: *u64, bit: u64, target: *u64, value: u64, source_mask: u64) protocol.Status {
    if ((source_mask & bit) == 0) return .ok;
    if ((target_mask.* & bit) != 0 and target.* != value) return .conflicting_facts;
    target.* = value;
    target_mask.* |= bit;
    return .ok;
}

fn mergeU8Fact(target_mask: *u64, bit: u64, target: *u8, value: u8, source_mask: u64) protocol.Status {
    if ((source_mask & bit) == 0) return .ok;
    if ((target_mask.* & bit) != 0 and target.* != value) return .conflicting_facts;
    target.* = value;
    target_mask.* |= bit;
    return .ok;
}

fn mergeF64Fact(target_mask: *u64, bit: u64, target: *f64, value: f64, source_mask: u64) protocol.Status {
    if ((source_mask & bit) == 0) return .ok;
    if ((target_mask.* & bit) != 0 and target.* != value) return .conflicting_facts;
    target.* = value;
    target_mask.* |= bit;
    return .ok;
}

fn mergeFlagFact(
    target_mask: *u64,
    bit: u64,
    target_flags: *u64,
    source_flags: u64,
    field_flags: u64,
    source_mask: u64,
) protocol.Status {
    if ((source_mask & bit) == 0) return .ok;
    const value = source_flags & field_flags;
    if ((target_mask.* & bit) != 0 and (target_flags.* & field_flags) != value) return .conflicting_facts;
    target_flags.* = (target_flags.* & ~field_flags) | value;
    target_mask.* |= bit;
    return .ok;
}
