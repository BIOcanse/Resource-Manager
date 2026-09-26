const std = @import("std");
const protocol = @import("protocol.zig");
const state = @import("state.zig");
const base_score_tier = @import("../base_score_tier.zig");

const no_slot: u32 = std.math.maxInt(u32);

pub fn advance(
    session: *state.Session,
    input: *const protocol.CycleInput,
    actions: []protocol.Action,
    snapshot: *protocol.Snapshot,
) protocol.Status {
    if (actions.len < session.config.max_actions) return .buffer_too_small;
    if ((input.flags & protocol.CycleFlags.authoritative_applied_facts) != 0 and session.inflightCount() != 0) {
        return .unavailable;
    }

    const prepare_result = reconcileFacts(session, input);
    if (prepare_result != .ok) return prepare_result;
    if ((input.flags & protocol.CycleFlags.authoritative_applied_facts) != 0) {
        session.requires_authoritative_resync = false;
    }

    session.plan_epoch +%= 1;
    if (session.plan_epoch == 0) session.plan_epoch = 1;
    session.last_cycle_sequence = input.cycle_sequence;
    session.last_observed_at_ms = input.observed_at_ms;
    session.invalid_fact_count = 0;
    session.last_reason_mask = 0;

    observeCriticalEvents(session, input);
    evaluateProcesses(session, input);
    resolveFreezeGroups(session);
    evaluateSoftware(session, input);

    const score_only = (input.flags & protocol.CycleFlags.score_only) != 0;
    if (!score_only) {
        updateTransitions(session, input);
    } else {
        session.last_reason_mask |= protocol.Reason.score_only;
    }

    const candidate_count = buildActions(session, input, actions, score_only);
    const group_status = prepareAtomicFreezeGroups(session, actions[0..candidate_count]);
    if (group_status != .ok) return group_status;
    sortActions(actions[0..candidate_count]);
    const action_count = selectActionsWithinLimit(
        session,
        actions[0..candidate_count],
        input.maximum_actions_this_cycle,
    ) orelse return .invalid_argument;
    const reservation_status = assignIdsAndReserve(session, input, actions[0..action_count]);
    if (reservation_status != .ok) return reservation_status;

    resolveNextWake(session, input.observed_at_ms);
    for (actions[0..action_count], 0..) |*action, index| {
        action.order_key = @intCast(index);
        action.wake_after_ms = session.wake_after_ms;
    }
    session.last_cycle_score_only = score_only;
    fillSnapshot(session, input, @intCast(action_count), snapshot);
    session.first_cycle_completed = true;
    session.bumpRevision();
    snapshot.state_revision = session.state_revision;
    return .ok;
}

pub fn fillSnapshot(
    session: *const state.Session,
    input: ?*const protocol.CycleInput,
    action_count: u32,
    snapshot: *protocol.Snapshot,
) void {
    var process_count: u32 = 0;
    var software_count: u32 = 0;
    var pending_count: u32 = 0;
    for (session.processes) |process| {
        if (!process.occupied) continue;
        process_count += 1;
        if (process.transition.pending_count > 0) pending_count += 1;
    }
    for (session.softwares) |software| {
        if (!software.occupied) continue;
        software_count += 1;
        if (software.cpu_transition.pending_count > 0) pending_count += 1;
        if (software.gpu_transition.pending_count > 0) pending_count += 1;
    }
    var flags: u32 = 0;
    if (session.invalid_fact_count > 0) flags |= protocol.SnapshotFlags.has_invalid_facts;
    if (session.last_observed_at_ms < session.event_boost_until_ms) flags |= protocol.SnapshotFlags.event_boost_active;
    if (session.last_cycle_score_only) flags |= protocol.SnapshotFlags.score_only;
    if (session.inflightCount() > 0) flags |= protocol.SnapshotFlags.awaiting_feedback;
    if (session.requires_authoritative_resync) flags |= protocol.SnapshotFlags.requires_authoritative_resync;
    snapshot.* = .{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.Snapshot),
        .snapshot_row_struct_size = @sizeOf(protocol.SnapshotRow),
        .flags = flags,
        .config_generation = session.config.generation,
        .cycle_sequence = if (input) |value| value.cycle_sequence else session.last_cycle_sequence,
        .plan_epoch = session.plan_epoch,
        .state_revision = session.state_revision,
        .observed_at_ms = session.last_observed_at_ms,
        .next_wake_at_ms = session.next_wake_at_ms,
        .wake_after_ms = session.wake_after_ms,
        .action_count = action_count,
        .process_count = process_count,
        .software_count = software_count,
        .pending_count = pending_count,
        .inflight_count = session.inflightCount(),
        .invalid_fact_count = session.invalid_fact_count,
        .snapshot_row_count = process_count + software_count,
        .reason_mask = session.last_reason_mask,
        .reserved = [_]u64{0} ** 2,
    };
}

pub fn copySnapshotRows(session: *const state.Session, rows: []protocol.SnapshotRow) protocol.Status {
    var required: usize = 0;
    for (session.processes) |item| {
        if (item.occupied) required += 1;
    }
    for (session.softwares) |item| {
        if (item.occupied) required += 1;
    }
    if (rows.len < required) return .buffer_too_small;
    var cursor: usize = 0;
    for (session.processes) |process| {
        if (!process.occupied) continue;
        rows[cursor] = processSnapshotRow(process);
        cursor += 1;
    }
    for (session.softwares) |software| {
        if (!software.occupied) continue;
        rows[cursor] = softwareSnapshotRow(software);
        cursor += 1;
    }
    return .ok;
}

fn reconcileFacts(session: *state.Session, input: *const protocol.CycleInput) protocol.Status {
    const full_snapshot = (input.flags & protocol.CycleFlags.full_process_snapshot) != 0;
    for (session.processes) |*item| item.seen_this_cycle = false;
    for (session.softwares) |*item| {
        item.seen_this_cycle = false;
        item.planning_atomic_group_id = 0;
    }
    for (session.gpus) |*item| item.seen_this_cycle = false;

    if (full_snapshot) {
        for (session.processes, 0..) |process, index| {
            if (!process.occupied or session.findStagedProcess(process.target_key, process.process_id, process.process_start_key) != null) continue;
            if (!process.transition.owned and process.transition.inflight_action_id == 0) session.releaseProcess(@intCast(index));
        }
        for (session.softwares, 0..) |software, index| {
            if (!software.occupied or session.findStagedSoftware(software.software_key) != null) continue;
            if (!software.cpu_transition.owned and !software.gpu_transition.owned and
                software.cpu_transition.inflight_action_id == 0 and software.gpu_transition.inflight_action_id == 0)
            {
                session.releaseSoftware(@intCast(index));
            }
        }
        for (session.gpus, 0..) |gpu, index| {
            if (!gpu.occupied or session.findStagedGpu(gpu.target_key, gpu.process_id, gpu.process_start_key, gpu.device_index) != null) continue;
            session.releaseGpu(@intCast(index));
        }
        session.rebuildPersistentIndices();
    }

    var new_processes: u32 = 0;
    for (session.staged_processes[0..@intCast(session.staged_process_count)]) |process| {
        if (session.findProcess(process.target_key, process.process_id, process.process_start_key) == null) new_processes += 1;
    }
    var free_processes: u32 = 0;
    for (session.processes) |process| {
        if (!process.occupied) free_processes += 1;
    }
    if (new_processes > free_processes) return .capacity_exceeded;

    var new_softwares: u32 = 0;
    for (session.staged_softwares[0..@intCast(session.staged_software_count)]) |software| {
        if (session.findSoftware(software.software_key) == null) new_softwares += 1;
    }
    var free_softwares: u32 = 0;
    for (session.softwares) |software| {
        if (!software.occupied) free_softwares += 1;
    }
    if (new_softwares > free_softwares) return .capacity_exceeded;

    var new_gpus: u32 = 0;
    for (session.staged_gpus[0..@intCast(session.staged_gpu_count)]) |gpu| {
        if (session.findGpu(gpu.target_key, gpu.process_id, gpu.process_start_key, gpu.device_index) == null) new_gpus += 1;
    }
    var free_gpus: u32 = 0;
    for (session.gpus) |gpu| {
        if (!gpu.occupied) free_gpus += 1;
    }
    if (new_gpus > free_gpus) return .capacity_exceeded;

    const authoritative = (input.flags & protocol.CycleFlags.authoritative_applied_facts) != 0;
    for (session.staged_processes[0..@intCast(session.staged_process_count)]) |staged| {
        const slot = session.findProcess(staged.target_key, staged.process_id, staged.process_start_key) orelse
            (session.allocateProcess() orelse return .capacity_exceeded);
        if (session.findProcess(staged.target_key, staged.process_id, staged.process_start_key) == null) {
            const target = &session.processes[slot];
            target.target_key = staged.target_key;
            target.process_id = staged.process_id;
            target.process_start_key = staged.process_start_key;
            session.insertProcessIndex(slot);
        }
        copyProcessFacts(session, slot, staged, input, authoritative);
    }

    for (session.staged_softwares[0..@intCast(session.staged_software_count)]) |staged| {
        const existing = session.findSoftware(staged.software_key);
        const slot = existing orelse (session.allocateSoftware() orelse return .capacity_exceeded);
        if (existing == null) {
            session.softwares[slot].software_key = staged.software_key;
            session.insertSoftwareIndex(slot);
        }
        copySoftwareFacts(session, slot, staged, input, authoritative);
    }

    for (session.staged_gpus[0..@intCast(session.staged_gpu_count)]) |staged| {
        const existing = session.findGpu(staged.target_key, staged.process_id, staged.process_start_key, staged.device_index);
        const slot = existing orelse (session.allocateGpu() orelse return .capacity_exceeded);
        if (existing == null) {
            const target = &session.gpus[slot];
            target.target_key = staged.target_key;
            target.process_id = staged.process_id;
            target.process_start_key = staged.process_start_key;
            target.device_index = staged.device_index;
            session.insertGpuIndex(slot);
        }
        copyGpuFacts(session, slot, staged, input);
    }

    if (full_snapshot) {
        for (session.processes) |*process| {
            if (!process.occupied or process.seen_this_cycle) continue;
            process.game_active = false;
            process.freeze_eligible = false;
            process.freeze_ready = false;
        }
        for (session.softwares) |*software| {
            if (!software.occupied or software.seen_this_cycle) continue;
            software.previous_critical_fact = software.critical_fact;
            software.previous_member_signature = software.member_signature;
            software.critical_fact = false;
            software.member_count = 0;
            software.member_signature = 0;
        }
    }
    return .ok;
}

fn copyProcessFacts(
    session: *state.Session,
    slot: u32,
    staged: state.StagedProcess,
    input: *const protocol.CycleInput,
    authoritative: bool,
) void {
    const target = &session.processes[slot];
    const previous_game_active = target.game_active;
    target.seen_this_cycle = true;
    target.software_key = staged.software_key;
    target.source_index = staged.source_index;
    target.last_seen_cycle = input.cycle_sequence;
    target.last_seen_at_ms = input.observed_at_ms;
    target.valid_mask = staged.valid_mask;
    target.input_flags = staged.input_flags;
    target.software_kind = staged.software_kind;
    target.protection_level = staged.protection_level;
    target.runtime_state = staged.runtime_state;
    target.cpu_capability_mask = staged.cpu_capability_mask;
    target.gpu_capability_mask = staged.gpu_capability_mask;
    target.base_score = staged.base_score;
    target.cpu_usage_percent = staged.cpu_usage_percent;
    target.cpu_score = staged.cpu_score;
    target.cpu_score_valid = staged.cpu_score_valid;
    target.reason_mask = 0;
    target.candidate_valid = false;
    target.candidate_grade = target.transition.desired;
    target.freeze_eligible = false;
    target.freeze_ready = false;

    const is_game = (staged.valid_mask & protocol.InputValidity.software_kind) != 0 and
        staged.software_kind == @intFromEnum(protocol.SoftwareKind.game) and
        (staged.valid_mask & protocol.InputValidity.eligibility) != 0 and
        (staged.input_flags & protocol.InputFlags.hardware_scheduling_eligible) != 0;
    target.game_active = is_game;
    if (is_game and !previous_game_active) target.game_started_at_ms = input.observed_at_ms;
    if (!is_game) target.game_started_at_ms = 0;

    if (authoritative) {
        const owned = (staged.input_flags & protocol.InputFlags.owns_process_grade) != 0;
        const grade = staged.applied_process_grade;
        target.transition.applied = grade;
        target.transition.owned = owned;
        target.transition.inflight_action_id = 0;
        target.transition.desired = grade;
        target.transition.compensation_required = false;
        target.transition.clearPending();
        target.transition.last_source_fingerprint = 0;
    }
}

fn copySoftwareFacts(
    session: *state.Session,
    slot: u32,
    staged: state.StagedSoftware,
    input: *const protocol.CycleInput,
    authoritative: bool,
) void {
    const target = &session.softwares[slot];
    target.previous_critical_fact = target.critical_fact;
    target.previous_member_signature = target.member_signature;
    target.seen_this_cycle = true;
    target.source_index = staged.source_index;
    target.last_seen_cycle = input.cycle_sequence;
    target.last_seen_at_ms = input.observed_at_ms;
    target.valid_mask = staged.valid_mask;
    target.input_flags = staged.input_flags;
    target.software_kind = staged.software_kind;
    target.runtime_state = staged.runtime_state;
    target.cpu_capability_mask = staged.cpu_capability_mask;
    target.gpu_capability_mask = staged.gpu_capability_mask;
    target.base_score = staged.base_score;
    target.member_cpu_score = staged.cpu_score;
    target.member_gpu_score = staged.selected_gpu_score;
    target.member_cpu_score_valid = staged.cpu_score_valid;
    target.member_gpu_score_valid = staged.selected_gpu_score_valid;
    target.scored_cpu_member_count = staged.cpu_score_member_count;
    target.scored_gpu_member_count = staged.selected_gpu_score_member_count;
    target.member_count = staged.member_count;
    target.member_signature = staged.member_signature;
    target.critical_fact = staged.critical_fact;
    target.reason_mask = 0;
    target.cpu_candidate_valid = false;
    target.gpu_candidate_valid = false;
    target.cpu_candidate_grade = target.cpu_transition.desired;
    target.gpu_candidate_grade = target.gpu_transition.desired;
    target.planning_atomic_group_id = 0;
    if (authoritative) {
        syncAuthoritativeAdapterTransition(
            &target.cpu_transition,
            staged.valid_mask,
            protocol.InputValidity.applied_cpu_grade,
            staged.input_flags,
            protocol.InputFlags.owns_cpu_grade,
            staged.applied_cpu_grade,
        );
        syncAuthoritativeAdapterTransition(
            &target.gpu_transition,
            staged.valid_mask,
            protocol.InputValidity.applied_gpu_grade,
            staged.input_flags,
            protocol.InputFlags.owns_gpu_grade,
            staged.applied_gpu_grade,
        );
    }
}

fn syncAuthoritativeAdapterTransition(
    transition: *state.AdapterTransition,
    valid_mask: u64,
    valid_bit: u64,
    flags: u64,
    owned_flag: u64,
    grade: u8,
) void {
    std.debug.assert((valid_mask & valid_bit) != 0);
    transition.applied = grade;
    transition.owned = (flags & owned_flag) != 0;
    transition.inflight_action_id = 0;
    transition.desired = transition.applied;
    transition.clearPending();
    transition.last_source_fingerprint = 0;
}

fn copyGpuFacts(session: *state.Session, slot: u32, staged: state.StagedGpu, input: *const protocol.CycleInput) void {
    const target = &session.gpus[slot];
    target.seen_this_cycle = true;
    target.last_seen_cycle = input.cycle_sequence;
    target.valid_mask = staged.valid_mask;
    target.gpu_usage_percent = staged.gpu_usage_percent;
    target.vram_usage_percent = staged.vram_usage_percent;
}

fn observeCriticalEvents(session: *state.Session, input: *const protocol.CycleInput) void {
    if ((session.config.feature_flags & protocol.FeatureFlags.critical_events) == 0) {
        session.event_boost_until_ms = 0;
        return;
    }
    var stable_changed = false;
    var membership_changed = false;
    for (session.softwares) |software| {
        if (!software.occupied) continue;
        if (software.previous_critical_fact != software.critical_fact) stable_changed = true else if (software.critical_fact and software.previous_member_signature != software.member_signature) membership_changed = true;
    }
    for (session.processes) |*process| {
        if (!process.occupied) continue;
        const previous = process.critical_fact;
        var current = false;
        if (process.seen_this_cycle and (process.valid_mask & protocol.InputValidity.software_identity) == 0 and
            (process.valid_mask & protocol.InputValidity.eligibility) != 0 and
            (process.input_flags & protocol.InputFlags.hardware_scheduling_eligible) != 0)
        {
            current = isCriticalProcessState(session, process);
        }
        process.critical_fact = current;
        if (previous != current) stable_changed = true;
        if (process.game_active and input.observed_at_ms >= process.game_started_at_ms and
            input.observed_at_ms - process.game_started_at_ms <= @as(i64, @intCast(session.config.game_start_grace_ms)))
        {
            process.reason_mask |= protocol.Reason.startup_grace_active;
        }
    }
    if ((input.flags & protocol.CycleFlags.external_event) != 0) stable_changed = true;
    if (stable_changed) {
        session.event_boost_until_ms = saturatingAdd(input.observed_at_ms, session.config.event_boost_ms);
        session.last_reason_mask |= protocol.Reason.critical_fact_changed;
    }
    if (membership_changed) session.last_reason_mask |= protocol.Reason.critical_membership_changed;
}

fn evaluateProcesses(session: *state.Session, input: *const protocol.CycleInput) void {
    const full_snapshot = (input.flags & protocol.CycleFlags.full_process_snapshot) != 0;
    const feature_enabled = (session.config.feature_flags & protocol.FeatureFlags.process_policy) != 0;
    for (session.processes) |*process| {
        if (!process.occupied) continue;
        process.reason_mask &= protocol.Reason.startup_grace_active;
        process.candidate_valid = false;
        process.freeze_eligible = false;
        process.freeze_ready = false;
        if (!process.seen_this_cycle) {
            if (full_snapshot) {
                process.reason_mask |= protocol.Reason.target_disappeared;
                process.candidate_valid = true;
                process.candidate_grade = protocol.ProcessGrade.normal;
            } else {
                process.reason_mask |= protocol.Reason.no_complete_snapshot;
                session.invalid_fact_count += 1;
            }
            session.last_reason_mask |= process.reason_mask;
            continue;
        }
        if (!feature_enabled) {
            process.reason_mask |= protocol.Reason.feature_disabled;
            process.candidate_valid = true;
            process.candidate_grade = protocol.ProcessGrade.normal;
            session.last_reason_mask |= process.reason_mask;
            continue;
        }
        if (process.runtime_state == @intFromEnum(protocol.RuntimeState.not_running)) {
            process.reason_mask |= protocol.Reason.not_running;
            process.candidate_valid = true;
            process.candidate_grade = protocol.ProcessGrade.normal;
            session.last_reason_mask |= process.reason_mask;
            continue;
        }
        const eligibility_current =
            (process.valid_mask & protocol.InputValidity.eligibility) != 0;
        if (eligibility_current and
            (process.input_flags & protocol.InputFlags.can_apply_process_policy) == 0)
        {
            process.reason_mask |= protocol.Reason.ineligible;
            process.candidate_valid = true;
            process.candidate_grade = protocol.ProcessGrade.normal;
            session.last_reason_mask |= process.reason_mask;
            continue;
        }
        if (!eligibility_current) {
            process.reason_mask |= protocol.Reason.ineligible;
            session.invalid_fact_count += 1;
            session.last_reason_mask |= process.reason_mask;
            continue;
        }

        var score_missing = false;
        if ((process.valid_mask & protocol.InputValidity.base_score) == 0) {
            process.reason_mask |= protocol.Reason.missing_base_score;
            score_missing = true;
        }
        if ((process.valid_mask & protocol.InputValidity.surface_facts) == 0) {
            process.reason_mask |= protocol.Reason.missing_surface_facts;
            score_missing = true;
        }
        if ((process.input_flags & protocol.InputFlags.process_cpu_metrics_complete) == 0 or
            !process.cpu_score_valid)
        {
            process.reason_mask |= protocol.Reason.missing_cpu_metric;
            score_missing = true;
        }
        if (score_missing) {
            session.invalid_fact_count += 1;
            session.last_reason_mask |= process.reason_mask;
            continue;
        }

        var candidate = theoreticalProcessGrade(session, process.base_score, process.cpu_score);
        candidate = clampProcessByBaseAndProtection(session, process, candidate);
        process.freeze_eligible = candidate == protocol.ProcessGrade.level4;
        process.candidate_valid = true;
        process.candidate_grade = candidate;
        session.last_reason_mask |= process.reason_mask;
    }
}

fn resolveFreezeGroups(session: *state.Session) void {
    for (session.softwares) |*software| {
        software.planning_process_member_count = 0;
        software.planning_freeze_eligible_count = 0;
        software.planning_compensation_pending_count = 0;
    }
    for (session.processes) |*process| {
        if (!process.occupied or (process.valid_mask & protocol.InputValidity.software_identity) == 0) continue;
        const software_slot = session.findSoftware(process.software_key) orelse continue;
        const software = &session.softwares[software_slot];
        if (software.freeze_compensation_active) {
            if (process.transition.owned or process.transition.applied != protocol.ProcessGrade.normal or
                process.transition.inflight_action_id != 0)
            {
                software.planning_compensation_pending_count += 1;
                process.transition.compensation_required = true;
            }
            process.freeze_eligible = false;
            process.freeze_ready = false;
            process.candidate_valid = true;
            process.candidate_grade = protocol.ProcessGrade.normal;
            process.reason_mask |= protocol.Reason.atomic_group_compensation;
        }
        software.planning_process_member_count += 1;
        if (process.seen_this_cycle and process.freeze_eligible) software.planning_freeze_eligible_count += 1;
    }
    for (session.softwares) |*software| {
        if (software.occupied and software.freeze_compensation_active and
            software.planning_compensation_pending_count == 0)
        {
            software.freeze_compensation_active = false;
        }
    }
    for (session.processes) |*process| {
        if (!process.occupied or !process.seen_this_cycle or !process.freeze_eligible) continue;
        if ((process.valid_mask & protocol.InputValidity.software_identity) == 0) {
            process.freeze_ready = true;
            continue;
        }
        const software_slot = session.findSoftware(process.software_key) orelse continue;
        const software = session.softwares[software_slot];
        process.freeze_ready = software.planning_process_member_count > 0 and
            software.planning_freeze_eligible_count == software.planning_process_member_count;
    }
    for (session.processes) |*process| {
        if (!process.occupied or !process.seen_this_cycle or !process.freeze_eligible or process.freeze_ready) continue;
        process.candidate_grade = protocol.ProcessGrade.level3;
        process.reason_mask |= protocol.Reason.freeze_group_blocked;
        session.last_reason_mask |= protocol.Reason.freeze_group_blocked;
    }
}

fn evaluateSoftware(session: *state.Session, input: *const protocol.CycleInput) void {
    const full_snapshot = (input.flags & protocol.CycleFlags.full_process_snapshot) != 0;
    for (session.softwares) |*software| {
        if (!software.occupied) continue;
        software.reason_mask = 0;
        software.cpu_candidate_valid = false;
        software.gpu_candidate_valid = false;
        software.adapter_cpu_score = 0;
        software.adapter_gpu_score = 0;
        software.adapter_cpu_score_valid = false;
        software.adapter_gpu_score_valid = false;
        if (!software.seen_this_cycle) {
            if (full_snapshot) {
                software.reason_mask |= protocol.Reason.target_disappeared;
                software.cpu_candidate_valid = true;
                software.gpu_candidate_valid = true;
                software.cpu_candidate_grade = @intFromEnum(protocol.AdapterGrade.normal);
                software.gpu_candidate_grade = @intFromEnum(protocol.AdapterGrade.normal);
            } else {
                software.reason_mask |= protocol.Reason.no_complete_snapshot;
                session.invalid_fact_count += 1;
            }
            session.last_reason_mask |= software.reason_mask;
            continue;
        }
        const explicitly_ineligible = (software.valid_mask & protocol.InputValidity.eligibility) != 0 and
            (software.input_flags & protocol.InputFlags.can_apply_adapter_policy) == 0;
        const not_running = software.runtime_state == @intFromEnum(protocol.RuntimeState.not_running);
        if (explicitly_ineligible or not_running) {
            software.reason_mask |= if (not_running) protocol.Reason.not_running else protocol.Reason.ineligible;
            software.cpu_candidate_valid = true;
            software.gpu_candidate_valid = true;
            software.cpu_candidate_grade = @intFromEnum(protocol.AdapterGrade.normal);
            software.gpu_candidate_grade = @intFromEnum(protocol.AdapterGrade.normal);
            session.last_reason_mask |= software.reason_mask;
            continue;
        }
        if ((software.valid_mask & protocol.InputValidity.base_score) == 0 or
            software.runtime_state == @intFromEnum(protocol.RuntimeState.unknown) or
            (software.valid_mask & protocol.InputValidity.eligibility) == 0)
        {
            if ((software.valid_mask & protocol.InputValidity.base_score) == 0) software.reason_mask |= protocol.Reason.missing_base_score;
            if (software.runtime_state == @intFromEnum(protocol.RuntimeState.unknown)) software.reason_mask |= protocol.Reason.missing_surface_facts;
            if ((software.valid_mask & protocol.InputValidity.eligibility) == 0) software.reason_mask |= protocol.Reason.ineligible;
            session.invalid_fact_count += 1;
            session.last_reason_mask |= software.reason_mask;
            continue;
        }

        if ((session.config.feature_flags & protocol.FeatureFlags.adapter_cpu) == 0) {
            software.cpu_candidate_valid = true;
            software.cpu_candidate_grade = @intFromEnum(protocol.AdapterGrade.normal);
            software.reason_mask |= protocol.Reason.feature_disabled;
        } else if ((software.valid_mask & protocol.InputValidity.cpu_capabilities) == 0) {
            software.reason_mask |= protocol.Reason.cpu_capability_missing;
            session.invalid_fact_count += 1;
        } else if (software.cpu_capability_mask == 0) {
            software.cpu_candidate_valid = true;
            software.cpu_candidate_grade = @intFromEnum(protocol.AdapterGrade.normal);
            software.reason_mask |= protocol.Reason.cpu_capability_missing;
        } else if (!software.member_cpu_score_valid) {
            software.reason_mask |= protocol.Reason.missing_cpu_metric;
            session.invalid_fact_count += 1;
        } else {
            const cpu = adapterDecision(
                &session.config,
                &session.config.cpu_adapter,
                software.member_cpu_score,
                software.base_score,
                software.cpu_capability_mask,
            );
            software.adapter_cpu_score = cpu.score;
            software.adapter_cpu_score_valid = true;
            software.cpu_candidate_grade = cpu.grade;
            software.cpu_candidate_valid = true;
            if (cpu.clamped) software.reason_mask |= protocol.Reason.capability_clamped;
        }

        if ((session.config.feature_flags & protocol.FeatureFlags.adapter_gpu) == 0) {
            software.gpu_candidate_valid = true;
            software.gpu_candidate_grade = @intFromEnum(protocol.AdapterGrade.normal);
            software.reason_mask |= protocol.Reason.feature_disabled;
        } else if ((software.valid_mask & protocol.InputValidity.gpu_capabilities) == 0) {
            software.reason_mask |= protocol.Reason.gpu_capability_missing;
            session.invalid_fact_count += 1;
        } else if (software.gpu_capability_mask == 0) {
            software.gpu_candidate_valid = true;
            software.gpu_candidate_grade = @intFromEnum(protocol.AdapterGrade.normal);
            software.reason_mask |= protocol.Reason.gpu_capability_missing;
        } else if (!software.member_gpu_score_valid) {
            software.reason_mask |= protocol.Reason.incomplete_gpu_metrics;
            session.invalid_fact_count += 1;
        } else {
            const gpu = adapterDecision(
                &session.config,
                &session.config.gpu_adapter,
                software.member_gpu_score,
                software.base_score,
                software.gpu_capability_mask,
            );
            software.adapter_gpu_score = gpu.score;
            software.adapter_gpu_score_valid = true;
            software.gpu_candidate_grade = gpu.grade;
            software.gpu_candidate_valid = true;
            if (gpu.clamped) software.reason_mask |= protocol.Reason.capability_clamped;
        }
        session.last_reason_mask |= software.reason_mask;
    }
}

fn updateTransitions(session: *state.Session, input: *const protocol.CycleInput) void {
    for (session.processes) |*process| {
        if (!process.occupied or !process.candidate_valid) continue;
        if (process.transition.compensation_required) {
            process.candidate_grade = protocol.ProcessGrade.normal;
            process.reason_mask |= protocol.Reason.atomic_group_compensation;
        }
        const immediate = process.transition.compensation_required or !process.seen_this_cycle or
            (process.reason_mask & (protocol.Reason.target_disappeared | protocol.Reason.feature_disabled |
                protocol.Reason.not_running | protocol.Reason.ineligible)) != 0;
        updateProcessTransition(
            &process.transition,
            process.candidate_grade,
            input.cpu_score_source_fingerprint,
            input.observed_at_ms,
            session.config.required_consecutive_decisions,
            immediate,
        );
    }
    for (session.softwares) |*software| {
        if (!software.occupied) continue;
        const common_immediate = !software.seen_this_cycle or
            (software.reason_mask & (protocol.Reason.target_disappeared |
                protocol.Reason.not_running | protocol.Reason.ineligible)) != 0;
        const cpu_immediate = common_immediate or
            (session.config.feature_flags & protocol.FeatureFlags.adapter_cpu) == 0 or
            (software.cpu_candidate_valid and software.cpu_capability_mask == 0);
        const gpu_immediate = common_immediate or
            (session.config.feature_flags & protocol.FeatureFlags.adapter_gpu) == 0 or
            (software.gpu_candidate_valid and software.gpu_capability_mask == 0);
        if (software.cpu_candidate_valid) updateAdapterTransition(
            &software.cpu_transition,
            software.cpu_candidate_grade,
            input.cpu_score_source_fingerprint,
            input.observed_at_ms,
            session.config.required_consecutive_decisions,
            cpu_immediate,
        );
        if (software.gpu_candidate_valid) updateAdapterTransition(
            &software.gpu_transition,
            software.gpu_candidate_grade,
            input.gpu_score_source_fingerprint,
            input.observed_at_ms,
            session.config.required_consecutive_decisions,
            gpu_immediate,
        );
    }
}

fn updateProcessTransition(
    transition: *state.ProcessTransition,
    candidate: i8,
    source_fingerprint: u64,
    now_ms: i64,
    required: u32,
    immediate: bool,
) void {
    transition.desired = candidate;
    if (candidate == transition.applied) {
        transition.clearPending();
        transition.last_source_fingerprint = source_fingerprint;
        return;
    }
    if (transition.inflight_action_id != 0) return;
    if (immediate) {
        transition.pending_from = transition.applied;
        transition.pending_to = candidate;
        transition.pending_count = required;
        transition.pending_first_seen_ms = now_ms;
        transition.pending_last_seen_ms = now_ms;
        transition.last_source_fingerprint = source_fingerprint;
        return;
    }
    if (source_fingerprint == 0) return;
    if (transition.last_source_fingerprint == source_fingerprint) {
        if (transition.pending_count > 0 and transition.pending_to != candidate) {
            transition.pending_from = transition.applied;
            transition.pending_to = candidate;
            transition.pending_count = 0;
            transition.pending_first_seen_ms = 0;
            transition.pending_last_seen_ms = 0;
        }
        return;
    }
    transition.last_source_fingerprint = source_fingerprint;
    if (transition.pending_count > 0 and transition.pending_from == transition.applied and transition.pending_to == candidate) {
        transition.pending_count +|= 1;
        transition.pending_last_seen_ms = now_ms;
    } else {
        transition.pending_from = transition.applied;
        transition.pending_to = candidate;
        transition.pending_count = 1;
        transition.pending_first_seen_ms = now_ms;
        transition.pending_last_seen_ms = now_ms;
    }
}

fn updateAdapterTransition(
    transition: *state.AdapterTransition,
    candidate: u8,
    source_fingerprint: u64,
    now_ms: i64,
    required: u32,
    immediate: bool,
) void {
    transition.desired = candidate;
    if (candidate == transition.applied) {
        transition.clearPending();
        transition.last_source_fingerprint = source_fingerprint;
        return;
    }
    if (transition.inflight_action_id != 0) return;
    if (immediate) {
        transition.pending_from = transition.applied;
        transition.pending_to = candidate;
        transition.pending_count = required;
        transition.pending_first_seen_ms = now_ms;
        transition.pending_last_seen_ms = now_ms;
        transition.last_source_fingerprint = source_fingerprint;
        return;
    }
    if (source_fingerprint == 0) return;
    if (transition.last_source_fingerprint == source_fingerprint) {
        if (transition.pending_count > 0 and transition.pending_to != candidate) {
            transition.pending_from = transition.applied;
            transition.pending_to = candidate;
            transition.pending_count = 0;
            transition.pending_first_seen_ms = 0;
            transition.pending_last_seen_ms = 0;
        }
        return;
    }
    transition.last_source_fingerprint = source_fingerprint;
    if (transition.pending_count > 0 and transition.pending_from == transition.applied and transition.pending_to == candidate) {
        transition.pending_count +|= 1;
        transition.pending_last_seen_ms = now_ms;
    } else {
        transition.pending_from = transition.applied;
        transition.pending_to = candidate;
        transition.pending_count = 1;
        transition.pending_first_seen_ms = now_ms;
        transition.pending_last_seen_ms = now_ms;
    }
}

fn buildActions(session: *state.Session, input: *const protocol.CycleInput, actions: []protocol.Action, score_only: bool) usize {
    var count: usize = 0;
    for (session.processes) |*process| {
        if (!process.occupied) continue;
        actions[count] = processAction(session, process, input, score_only);
        count += 1;
    }
    for (session.softwares) |*software| {
        if (!software.occupied) continue;
        actions[count] = softwareAction(session, software, input, score_only);
        count += 1;
    }
    return count;
}

fn processAction(session: *state.Session, process: *state.ProcessState, input: *const protocol.CycleInput, score_only: bool) protocol.Action {
    const target_grade = if (score_only and process.candidate_valid)
        process.candidate_grade
    else
        process.transition.desired;
    var disposition: protocol.ActionDisposition = if (process.transition.owned) .retain else .no_op;
    var flags: u32 = 0;
    var reason_mask = process.reason_mask;
    if (score_only) {
        reason_mask |= protocol.Reason.score_only;
    } else if (process.transition.inflight_action_id != 0) {
        reason_mask |= protocol.Reason.awaiting_stability;
    } else if (!process.candidate_valid) {
        reason_mask |= protocol.Reason.awaiting_stability;
    } else if (process.transition.desired == process.transition.applied) {
        reason_mask |= protocol.Reason.applied_matches_desired;
    } else if (input.observed_at_ms < process.transition.retry_not_before_ms) {
        reason_mask |= protocol.Reason.retry_backoff;
    } else if (process.transition.pending_count < session.config.required_consecutive_decisions) {
        reason_mask |= protocol.Reason.awaiting_stability;
    } else {
        disposition = if (process.transition.desired == protocol.ProcessGrade.normal) .restore else .apply;
        flags |= protocol.ActionFlags.requires_feedback;
        if (process.transition.compensation_required) flags |= protocol.ActionFlags.compensation;
        if (process.transition.failure_count > 0) flags |= protocol.ActionFlags.retry;
    }
    var valid_mask = protocol.ActionValidity.process_identity | protocol.ActionValidity.process_grade;
    if ((process.valid_mask & protocol.InputValidity.software_identity) != 0) valid_mask |= protocol.ActionValidity.software_identity;
    if (process.cpu_score_valid) valid_mask |= protocol.ActionValidity.cpu_score;
    return .{
        .struct_size = @sizeOf(protocol.Action),
        .flags = flags,
        .valid_mask = valid_mask,
        .reason_mask = reason_mask,
        .action_id = 0,
        .plan_epoch = session.plan_epoch,
        .config_generation = session.config.generation,
        .atomic_group_id = 0,
        .target_key = process.target_key,
        .software_key = process.software_key,
        .process_start_key = process.process_start_key,
        .cpu_score = process.cpu_score,
        .source_index = process.source_index,
        .process_id = process.process_id,
        .order_key = 0,
        .wake_after_ms = 0,
        .group_member_index = 0,
        .group_member_count = 0,
        .scope = @intFromEnum(protocol.ActionScope.process_policy),
        .disposition = @intFromEnum(disposition),
        .domain_mask = protocol.GradeDomains.process,
        .reserved0 = 0,
        .from_process_grade = process.transition.applied,
        .to_process_grade = target_grade,
        .from_cpu_grade = @intFromEnum(protocol.AdapterGrade.normal),
        .to_cpu_grade = @intFromEnum(protocol.AdapterGrade.normal),
        .from_gpu_grade = @intFromEnum(protocol.AdapterGrade.normal),
        .to_gpu_grade = @intFromEnum(protocol.AdapterGrade.normal),
        .reserved1 = [_]u8{0} ** 2,
        .gpu_score = 0,
    };
}

fn softwareAction(session: *state.Session, software: *state.SoftwareState, input: *const protocol.CycleInput, score_only: bool) protocol.Action {
    const normal = @intFromEnum(protocol.AdapterGrade.normal);
    const target_cpu_grade = if (score_only and software.cpu_candidate_valid)
        software.cpu_candidate_grade
    else
        software.cpu_transition.desired;
    const target_gpu_grade = if (score_only and software.gpu_candidate_valid)
        software.gpu_candidate_grade
    else
        software.gpu_transition.desired;
    var domain_mask: u8 = 0;
    var executable_mask: u8 = 0;
    var reason_mask = software.reason_mask;
    if (software.cpu_candidate_valid or software.cpu_transition.owned) domain_mask |= protocol.GradeDomains.cpu;
    if (software.gpu_candidate_valid or software.gpu_transition.owned) domain_mask |= protocol.GradeDomains.gpu;
    if (!score_only and software.cpu_candidate_valid and readyAdapterTransition(&software.cpu_transition, input.observed_at_ms, session.config.required_consecutive_decisions)) {
        executable_mask |= protocol.GradeDomains.cpu;
    }
    if (!score_only and software.gpu_candidate_valid and readyAdapterTransition(&software.gpu_transition, input.observed_at_ms, session.config.required_consecutive_decisions)) {
        executable_mask |= protocol.GradeDomains.gpu;
    }
    var disposition: protocol.ActionDisposition = if (software.cpu_transition.owned or software.gpu_transition.owned) .retain else .no_op;
    var flags: u32 = 0;
    if (score_only) {
        reason_mask |= protocol.Reason.score_only;
    } else if (executable_mask != 0) {
        const cpu_restores = (executable_mask & protocol.GradeDomains.cpu) == 0 or software.cpu_transition.desired == normal;
        const gpu_restores = (executable_mask & protocol.GradeDomains.gpu) == 0 or software.gpu_transition.desired == normal;
        disposition = if (cpu_restores and gpu_restores) .restore else .apply;
        flags |= protocol.ActionFlags.requires_feedback;
        if (software.cpu_transition.failure_count > 0 or software.gpu_transition.failure_count > 0) flags |= protocol.ActionFlags.retry;
        domain_mask = executable_mask;
    } else if ((software.cpu_transition.pending_count > 0 or software.gpu_transition.pending_count > 0) and
        software.cpu_transition.inflight_action_id == 0 and software.gpu_transition.inflight_action_id == 0)
    {
        reason_mask |= protocol.Reason.awaiting_stability;
    } else {
        reason_mask |= protocol.Reason.applied_matches_desired;
    }
    var valid_mask = protocol.ActionValidity.software_identity;
    if ((domain_mask & protocol.GradeDomains.cpu) != 0) valid_mask |= protocol.ActionValidity.cpu_grade;
    if ((domain_mask & protocol.GradeDomains.gpu) != 0) valid_mask |= protocol.ActionValidity.gpu_grade;
    if ((domain_mask & protocol.GradeDomains.cpu) != 0 and software.adapter_cpu_score_valid) valid_mask |= protocol.ActionValidity.cpu_score;
    if ((domain_mask & protocol.GradeDomains.gpu) != 0 and software.adapter_gpu_score_valid) valid_mask |= protocol.ActionValidity.gpu_score;
    return .{
        .struct_size = @sizeOf(protocol.Action),
        .flags = flags,
        .valid_mask = valid_mask,
        .reason_mask = reason_mask,
        .action_id = 0,
        .plan_epoch = session.plan_epoch,
        .config_generation = session.config.generation,
        .atomic_group_id = 0,
        .target_key = software.software_key,
        .software_key = software.software_key,
        .process_start_key = 0,
        .cpu_score = software.adapter_cpu_score,
        .source_index = software.source_index,
        .process_id = 0,
        .order_key = 0,
        .wake_after_ms = 0,
        .group_member_index = 0,
        .group_member_count = 0,
        .scope = @intFromEnum(protocol.ActionScope.adapter_software),
        .disposition = @intFromEnum(disposition),
        .domain_mask = domain_mask,
        .reserved0 = 0,
        .from_process_grade = protocol.ProcessGrade.normal,
        .to_process_grade = protocol.ProcessGrade.normal,
        .from_cpu_grade = software.cpu_transition.applied,
        .to_cpu_grade = target_cpu_grade,
        .from_gpu_grade = software.gpu_transition.applied,
        .to_gpu_grade = target_gpu_grade,
        .reserved1 = [_]u8{0} ** 2,
        .gpu_score = software.adapter_gpu_score,
    };
}

fn readyAdapterTransition(transition: *const state.AdapterTransition, now_ms: i64, required: u32) bool {
    return transition.inflight_action_id == 0 and transition.desired != transition.applied and
        transition.pending_count >= required and now_ms >= transition.retry_not_before_ms;
}

fn prepareAtomicFreezeGroups(session: *state.Session, actions: []protocol.Action) protocol.Status {
    for (actions) |*action| {
        if (!isFreezeApplyAction(action)) continue;
        action.atomic_group_id = 0;
        action.group_member_index = 0;
        action.group_member_count = 0;
        action.flags &= ~protocol.ActionFlags.atomic;
        action.valid_mask &= ~protocol.ActionValidity.atomic_group;
        const process_slot = session.findProcess(action.target_key, action.process_id, action.process_start_key) orelse return .invalid_argument;
        const process = &session.processes[process_slot];
        if ((process.valid_mask & protocol.InputValidity.software_identity) == 0 or
            process.software_key == 0) continue;
        const software_slot = session.findSoftware(process.software_key) orelse return .invalid_argument;
        const software = &session.softwares[software_slot];
        software.planning_atomic_group_id += 1;
    }
    for (session.softwares) |*software| {
        if (!software.occupied) continue;
        software.planning_atomic_group_id = if (software.planning_atomic_group_id > 1)
            session.takeGroupId()
        else
            0;
    }
    for (actions) |*action| {
        if (!isFreezeApplyAction(action)) continue;
        const process_slot = session.findProcess(action.target_key, action.process_id, action.process_start_key) orelse return .invalid_argument;
        const process = &session.processes[process_slot];
        if ((process.valid_mask & protocol.InputValidity.software_identity) == 0 or
            process.software_key == 0) continue;
        const software_slot = session.findSoftware(process.software_key) orelse return .invalid_argument;
        const group_id = session.softwares[software_slot].planning_atomic_group_id;
        if (group_id == 0) continue;
        action.atomic_group_id = group_id;
        action.valid_mask |= protocol.ActionValidity.atomic_group;
        action.flags |= protocol.ActionFlags.atomic;
        const group_slot = session.findAtomicGroup(group_id) orelse blk: {
            const allocated = session.allocateAtomicGroup() orelse return .capacity_exceeded;
            session.atomic_groups[allocated] = .{
                .occupied = true,
                .group_id = group_id,
                .expected_count = 0,
                .feedback_count = 0,
                .failed_count = 0,
                .order_score = action.cpu_score,
            };
            session.insertAtomicGroupIndex(allocated);
            break :blk allocated;
        };
        const group = &session.atomic_groups[group_slot];
        group.expected_count += 1;
        group.order_score = @min(group.order_score, action.cpu_score);
    }
    for (actions) |*action| {
        if ((action.flags & protocol.ActionFlags.atomic) == 0) continue;
        const group_slot = session.findAtomicGroup(action.atomic_group_id) orelse return .invalid_argument;
        const group = &session.atomic_groups[group_slot];
        action.group_member_index = @intCast(group.feedback_count);
        action.group_member_count = @intCast(group.expected_count);
        action.cpu_score = group.order_score;
        group.feedback_count += 1;
    }
    for (session.atomic_groups) |*group| {
        if (group.occupied and group.feedback_count == group.expected_count and group.failed_count == 0) group.feedback_count = 0;
    }
    return .ok;
}

fn isFreezeApplyAction(action: *const protocol.Action) bool {
    return action.scope == @intFromEnum(protocol.ActionScope.process_policy) and
        action.disposition == @intFromEnum(protocol.ActionDisposition.apply) and
        action.to_process_grade == protocol.ProcessGrade.level4 and
        (action.flags & protocol.ActionFlags.requires_feedback) != 0;
}

fn sortActions(actions: []protocol.Action) void {
    std.sort.pdq(protocol.Action, actions, {}, actionLessThan);
}

fn selectActionsWithinLimit(
    session: *state.Session,
    actions: []protocol.Action,
    maximum_actions: u32,
) ?usize {
    const limit: usize = @min(actions.len, @as(usize, maximum_actions));
    var read_index: usize = 0;
    var write_index: usize = 0;
    var released_group = false;

    while (read_index < actions.len) {
        const action = actions[read_index];
        if ((action.flags & protocol.ActionFlags.atomic) == 0) {
            if (write_index < limit) {
                actions[write_index] = action;
                write_index += 1;
            }
            read_index += 1;
            continue;
        }

        const group_id = action.atomic_group_id;
        const group_count: usize = action.group_member_count;
        if (group_id == 0 or group_count < 2 or group_count > actions.len - read_index) return null;
        for (actions[read_index .. read_index + group_count]) |member| {
            if ((member.flags & protocol.ActionFlags.atomic) == 0 or
                member.atomic_group_id != group_id or
                member.group_member_count != group_count)
            {
                return null;
            }
        }

        if (group_count <= limit - write_index) {
            var member_index: usize = 0;
            while (member_index < group_count) : (member_index += 1) {
                actions[write_index + member_index] = actions[read_index + member_index];
            }
            write_index += group_count;
        } else {
            const group_slot = session.findAtomicGroup(group_id) orelse return null;
            session.releaseAtomicGroup(group_slot);
            released_group = true;
        }
        read_index += group_count;
    }

    if (released_group) session.rebuildAtomicGroupIndex();
    @memset(actions[write_index..], std.mem.zeroes(protocol.Action));
    return write_index;
}

fn actionLessThan(_: void, left: protocol.Action, right: protocol.Action) bool {
    const left_phase = actionPhase(left);
    const right_phase = actionPhase(right);
    if (left_phase != right_phase) return left_phase < right_phase;
    if (left_phase == 1) {
        if (left.cpu_score != right.cpu_score) return left.cpu_score > right.cpu_score;
    } else if (left_phase >= 2 and left_phase <= 5) {
        if (left.cpu_score != right.cpu_score) return left.cpu_score < right.cpu_score;
    }
    if (left.atomic_group_id != right.atomic_group_id and (left.atomic_group_id != 0 or right.atomic_group_id != 0)) {
        return left.atomic_group_id < right.atomic_group_id;
    }
    if (left.target_key != right.target_key) return left.target_key < right.target_key;
    if (left.process_start_key != right.process_start_key) return left.process_start_key < right.process_start_key;
    return left.process_id < right.process_id;
}

fn actionPhase(action: protocol.Action) u8 {
    const disposition: protocol.ActionDisposition = @enumFromInt(action.disposition);
    if (disposition == .restore) return 0;
    if (disposition == .apply and action.scope == @intFromEnum(protocol.ActionScope.process_policy)) {
        return switch (action.to_process_grade) {
            protocol.ProcessGrade.a1 => 1,
            protocol.ProcessGrade.level3 => 2,
            protocol.ProcessGrade.level2 => 3,
            protocol.ProcessGrade.level1 => 4,
            protocol.ProcessGrade.level4 => 5,
            else => 6,
        };
    }
    if (disposition == .apply) return 6;
    if (disposition == .retain) return 7;
    return 8;
}

fn assignIdsAndReserve(session: *state.Session, input: *const protocol.CycleInput, actions: []protocol.Action) protocol.Status {
    for (actions) |*action| {
        action.action_id = session.takeActionId();
        if ((action.flags & protocol.ActionFlags.requires_feedback) == 0) continue;
        const reservation_slot = session.allocateReservation() orelse return .capacity_exceeded;
        var process_slot = no_slot;
        var software_slot = no_slot;
        var atomic_group_slot = no_slot;
        if (action.scope == @intFromEnum(protocol.ActionScope.process_policy)) {
            process_slot = session.findProcess(action.target_key, action.process_id, action.process_start_key) orelse return .invalid_argument;
            session.processes[process_slot].transition.inflight_action_id = action.action_id;
        } else {
            software_slot = session.findSoftware(action.software_key) orelse return .invalid_argument;
            const software = &session.softwares[software_slot];
            if ((action.domain_mask & protocol.GradeDomains.cpu) != 0) software.cpu_transition.inflight_action_id = action.action_id;
            if ((action.domain_mask & protocol.GradeDomains.gpu) != 0) software.gpu_transition.inflight_action_id = action.action_id;
        }
        if (action.atomic_group_id != 0) {
            atomic_group_slot = session.findAtomicGroup(action.atomic_group_id) orelse return .invalid_argument;
        }
        session.reservations[reservation_slot] = .{
            .occupied = true,
            .action = action.*,
            .process_slot = process_slot,
            .software_slot = software_slot,
            .atomic_group_slot = atomic_group_slot,
            .created_at_ms = input.observed_at_ms,
            .deadline_ms = saturatingAdd(input.observed_at_ms, session.config.reservation_timeout_ms),
            .validation_epoch = 0,
            .validation_feedback_index = 0,
            .feedback_received = false,
            .feedback = std.mem.zeroes(protocol.Feedback),
        };
        session.inflight_count += 1;
        session.insertReservationIndex(reservation_slot);
    }
    return .ok;
}

pub fn resolveNextWake(session: *state.Session, now_ms: i64) void {
    var delay = if (now_ms < session.event_boost_until_ms)
        session.config.event_interval_ms
    else
        session.config.normal_interval_ms;
    for (session.reservations) |reservation| {
        if (!reservation.occupied) continue;
        const remaining = nonNegativeDelay(now_ms, reservation.deadline_ms);
        delay = @min(delay, remaining);
    }
    for (session.processes) |process| {
        if (!process.occupied or process.transition.retry_not_before_ms <= now_ms) continue;
        delay = @min(delay, nonNegativeDelay(now_ms, process.transition.retry_not_before_ms));
    }
    for (session.processes) |process| {
        if (!process.occupied or !process.game_active) continue;
        const grace_end = saturatingAdd(process.game_started_at_ms, session.config.game_start_grace_ms);
        if (grace_end >= now_ms) {
            const first_expired_ms = std.math.add(i64, grace_end, 1) catch std.math.maxInt(i64);
            delay = @min(delay, nonNegativeDelay(now_ms, first_expired_ms));
        }
    }
    for (session.softwares) |software| {
        if (!software.occupied) continue;
        if (software.cpu_transition.retry_not_before_ms > now_ms) delay = @min(delay, nonNegativeDelay(now_ms, software.cpu_transition.retry_not_before_ms));
        if (software.gpu_transition.retry_not_before_ms > now_ms) delay = @min(delay, nonNegativeDelay(now_ms, software.gpu_transition.retry_not_before_ms));
    }
    session.wake_after_ms = delay;
    session.next_wake_at_ms = saturatingAdd(now_ms, delay);
}

fn theoreticalProcessGrade(session: *const state.Session, base_score: f64, cpu_score: f64) i8 {
    if (cpu_score >= session.config.a1_minimum_cpu_score) return protocol.ProcessGrade.a1;
    if (cpu_score >= session.config.default_minimum_cpu_score_scale) return protocol.ProcessGrade.normal;
    const tier = resolveBaseScoreTier(session, base_score);
    if (tier == .low and
        cpu_score < session.config.low_tier_level4_maximum_cpu_score_scale)
    {
        return protocol.ProcessGrade.level4;
    }
    if (cpu_score < session.config.level3_maximum_cpu_score_scale) return protocol.ProcessGrade.level3;
    if (cpu_score < session.config.level2_maximum_cpu_score_scale) return protocol.ProcessGrade.level2;
    if (cpu_score < session.config.level1_maximum_cpu_score_scale) return protocol.ProcessGrade.level1;
    return protocol.ProcessGrade.normal;
}

fn clampProcessByBaseAndProtection(session: *const state.Session, process: *state.ProcessState, initial: i8) i8 {
    var grade = initial;
    switch (resolveBaseScoreTier(session, process.base_score)) {
        .high => if (isOptimizationGrade(grade)) {
            grade = protocol.ProcessGrade.normal;
            process.reason_mask |= protocol.Reason.high_tier_blocks_optimization;
        },
        .middle => if (grade == protocol.ProcessGrade.a1) {
            grade = protocol.ProcessGrade.normal;
            process.reason_mask |= protocol.Reason.middle_tier_blocks_enhancement;
        } else if (grade == protocol.ProcessGrade.level4) {
            grade = protocol.ProcessGrade.level3;
            process.reason_mask |= protocol.Reason.middle_tier_blocks_freeze;
        },
        .low => if (grade == protocol.ProcessGrade.a1) {
            grade = protocol.ProcessGrade.normal;
            process.reason_mask |= protocol.Reason.low_tier_blocks_enhancement;
        },
    }
    if ((process.valid_mask & protocol.InputValidity.protection) != 0) {
        if (process.protection_level == 1 and grade == protocol.ProcessGrade.level4) {
            grade = protocol.ProcessGrade.level3;
            process.reason_mask |= protocol.Reason.protection_level1_clamps_freeze;
        } else if (process.protection_level >= 2 and isOptimizationGrade(grade)) {
            grade = protocol.ProcessGrade.normal;
            process.reason_mask |= protocol.Reason.protection_level2_blocks_optimization;
        }
    }
    return grade;
}

const AdapterDecision = struct {
    score: f64,
    grade: u8,
    clamped: bool,
};

fn adapterDecision(
    config: *const protocol.Config,
    policy: *const protocol.AdapterPolicyConfig,
    aggregate_score: f64,
    base_score: f64,
    capabilities: u8,
) AdapterDecision {
    const score = aggregate_score;
    var requested: u8 = undefined;
    const normal = @intFromEnum(protocol.AdapterGrade.normal);
    const optimize = @intFromEnum(protocol.AdapterGrade.optimize);
    if (capabilities == ((@as(u8, 1) << normal) | (@as(u8, 1) << optimize))) {
        requested = if (score >= policy.normal_minimum_score) normal else optimize;
    } else if (score >= policy.extreme_minimum_score) {
        requested = @intFromEnum(protocol.AdapterGrade.extreme);
    } else if (score >= policy.normal_minimum_score) {
        requested = normal;
    } else if (score >= policy.optimize_minimum_score) {
        requested = optimize;
    } else {
        requested = @intFromEnum(protocol.AdapterGrade.freeze);
    }
    var base_clamped = requested;
    switch (base_score_tier.resolve(
        base_score,
        config.middle_tier_minimum_base_score,
        config.high_tier_minimum_base_score,
    )) {
        .high => base_clamped = std.math.clamp(
            base_clamped,
            normal,
            @intFromEnum(protocol.AdapterGrade.extreme),
        ),
        .middle => base_clamped = std.math.clamp(base_clamped, optimize, normal),
        .low => base_clamped = std.math.clamp(
            base_clamped,
            @intFromEnum(protocol.AdapterGrade.freeze),
            normal,
        ),
    }
    const effective = nearestCapability(base_clamped, capabilities);
    return .{ .score = score, .grade = effective, .clamped = effective != requested };
}

fn nearestCapability(requested: u8, capabilities: u8) u8 {
    if ((capabilities & (@as(u8, 1) << @intCast(requested))) != 0) return requested;
    var best: u8 = @intFromEnum(protocol.AdapterGrade.normal);
    var best_distance: u8 = std.math.maxInt(u8);
    var candidate: u8 = 0;
    while (candidate < protocol.adapter_grade_count) : (candidate += 1) {
        if ((capabilities & (@as(u8, 1) << @intCast(candidate))) == 0) continue;
        const distance = if (candidate > requested) candidate - requested else requested - candidate;
        if (distance < best_distance or (distance == best_distance and candidate > best)) {
            best = candidate;
            best_distance = distance;
        }
    }
    return best;
}

fn isOptimizationGrade(grade: i8) bool {
    return grade >= protocol.ProcessGrade.level4 and grade <= protocol.ProcessGrade.level1;
}

fn isCriticalProcessState(session: *const state.Session, process: *const state.ProcessState) bool {
    if ((process.valid_mask & protocol.InputValidity.software_kind) != 0) {
        const kind: protocol.SoftwareKind = @enumFromInt(process.software_kind);
        if (kind == .game or kind == .high_performance) return true;
    }
    return (process.valid_mask & protocol.InputValidity.base_score) != 0 and
        process.base_score >= session.config.high_tier_minimum_base_score;
}

fn resolveBaseScoreTier(session: *const state.Session, base_score: f64) base_score_tier.Tier {
    return base_score_tier.resolve(
        base_score,
        session.config.middle_tier_minimum_base_score,
        session.config.high_tier_minimum_base_score,
    );
}

fn processSnapshotRow(process: state.ProcessState) protocol.SnapshotRow {
    var flags: u32 = 0;
    if (process.transition.owned) flags |= protocol.SnapshotRowFlags.process_owned;
    if (process.transition.inflight_action_id != 0) flags |= protocol.SnapshotRowFlags.process_inflight;
    if (process.freeze_ready) flags |= protocol.SnapshotRowFlags.freeze_ready;
    if (process.critical_fact) flags |= protocol.SnapshotRowFlags.critical_fact;
    return .{
        .struct_size = @sizeOf(protocol.SnapshotRow),
        .flags = flags,
        .valid_mask = process.valid_mask,
        .reason_mask = process.reason_mask,
        .target_key = process.target_key,
        .software_key = process.software_key,
        .process_start_key = process.process_start_key,
        .last_seen_cycle = process.last_seen_cycle,
        .pending_first_seen_ms = process.transition.pending_first_seen_ms,
        .pending_last_seen_ms = process.transition.pending_last_seen_ms,
        .last_feedback_ms = process.transition.last_feedback_ms,
        .game_started_at_ms = process.game_started_at_ms,
        .base_score = process.base_score,
        .cpu_score = process.cpu_score,
        .cpu_occupancy_percent = process.cpu_usage_percent,
        .reserved_process_score0 = 0,
        .reserved_process_ratio0 = 0,
        .adapter_cpu_score = 0,
        .adapter_gpu_score = 0,
        .source_index = process.source_index,
        .process_id = process.process_id,
        .process_pending_count = process.transition.pending_count,
        .cpu_pending_count = 0,
        .gpu_pending_count = 0,
        .failure_count = process.transition.failure_count,
        .row_kind = @intFromEnum(protocol.SnapshotRowKind.process),
        .software_kind = process.software_kind,
        .runtime_state = process.runtime_state,
        .protection_level = process.protection_level,
        .desired_process_grade = process.transition.desired,
        .applied_process_grade = process.transition.applied,
        .pending_process_grade = process.transition.pending_to,
        .desired_cpu_grade = @intFromEnum(protocol.AdapterGrade.normal),
        .applied_cpu_grade = @intFromEnum(protocol.AdapterGrade.normal),
        .pending_cpu_grade = @intFromEnum(protocol.AdapterGrade.normal),
        .desired_gpu_grade = @intFromEnum(protocol.AdapterGrade.normal),
        .applied_gpu_grade = @intFromEnum(protocol.AdapterGrade.normal),
        .pending_gpu_grade = @intFromEnum(protocol.AdapterGrade.normal),
        .cpu_capability_mask = process.cpu_capability_mask,
        .gpu_capability_mask = process.gpu_capability_mask,
        .reserved0 = 0,
        .reserved1 = 0,
    };
}

fn softwareSnapshotRow(software: state.SoftwareState) protocol.SnapshotRow {
    var flags: u32 = 0;
    if (software.cpu_transition.owned) flags |= protocol.SnapshotRowFlags.cpu_owned;
    if (software.gpu_transition.owned) flags |= protocol.SnapshotRowFlags.gpu_owned;
    if (software.cpu_transition.inflight_action_id != 0) flags |= protocol.SnapshotRowFlags.cpu_inflight;
    if (software.gpu_transition.inflight_action_id != 0) flags |= protocol.SnapshotRowFlags.gpu_inflight;
    if (software.critical_fact) flags |= protocol.SnapshotRowFlags.critical_fact;
    return .{
        .struct_size = @sizeOf(protocol.SnapshotRow),
        .flags = flags,
        .valid_mask = software.valid_mask,
        .reason_mask = software.reason_mask,
        .target_key = software.software_key,
        .software_key = software.software_key,
        .process_start_key = 0,
        .last_seen_cycle = software.last_seen_cycle,
        .pending_first_seen_ms = minNonZero(software.cpu_transition.pending_first_seen_ms, software.gpu_transition.pending_first_seen_ms),
        .pending_last_seen_ms = @max(software.cpu_transition.pending_last_seen_ms, software.gpu_transition.pending_last_seen_ms),
        .last_feedback_ms = @max(software.cpu_transition.last_feedback_ms, software.gpu_transition.last_feedback_ms),
        .game_started_at_ms = 0,
        .base_score = software.base_score,
        .cpu_score = software.member_cpu_score,
        .cpu_occupancy_percent = 0,
        .reserved_process_score0 = 0,
        .reserved_process_ratio0 = 0,
        .adapter_cpu_score = software.adapter_cpu_score,
        .adapter_gpu_score = software.adapter_gpu_score,
        .source_index = software.source_index,
        .process_id = 0,
        .process_pending_count = 0,
        .cpu_pending_count = software.cpu_transition.pending_count,
        .gpu_pending_count = software.gpu_transition.pending_count,
        .failure_count = software.cpu_transition.failure_count +| software.gpu_transition.failure_count,
        .row_kind = @intFromEnum(protocol.SnapshotRowKind.software),
        .software_kind = software.software_kind,
        .runtime_state = software.runtime_state,
        .protection_level = 0,
        .desired_process_grade = protocol.ProcessGrade.normal,
        .applied_process_grade = protocol.ProcessGrade.normal,
        .pending_process_grade = protocol.ProcessGrade.normal,
        .desired_cpu_grade = software.cpu_transition.desired,
        .applied_cpu_grade = software.cpu_transition.applied,
        .pending_cpu_grade = software.cpu_transition.pending_to,
        .desired_gpu_grade = software.gpu_transition.desired,
        .applied_gpu_grade = software.gpu_transition.applied,
        .pending_gpu_grade = software.gpu_transition.pending_to,
        .cpu_capability_mask = software.cpu_capability_mask,
        .gpu_capability_mask = software.gpu_capability_mask,
        .reserved0 = 0,
        .reserved1 = 0,
    };
}

fn nonNegativeDelay(now_ms: i64, target_ms: i64) u32 {
    if (target_ms <= now_ms) return 0;
    const difference: u64 = @intCast(target_ms - now_ms);
    return @intCast(@min(difference, @as(u64, std.math.maxInt(u32))));
}

fn saturatingAdd(value: i64, milliseconds: u32) i64 {
    return std.math.add(i64, value, @as(i64, @intCast(milliseconds))) catch std.math.maxInt(i64);
}

fn minNonZero(left: i64, right: i64) i64 {
    if (left == 0) return right;
    if (right == 0) return left;
    return @min(left, right);
}
