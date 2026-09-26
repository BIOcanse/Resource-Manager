const std = @import("std");
const protocol = @import("protocol.zig");
const state = @import("state.zig");
const planner = @import("planner.zig");
const feedback_contract = @import("../../contracts/action_feedback_contract.zig");

const no_slot: u32 = std.math.maxInt(u32);

pub fn applyBatch(
    session: *state.Session,
    feedback_pointer: ?[*]const protocol.Feedback,
    feedback_count: u32,
    feedback_capacity: u32,
    snapshot: *protocol.Snapshot,
) protocol.Status {
    if (feedback_count == 0) return .no_data;
    if (feedback_count > session.config.max_reservations or feedback_capacity < feedback_count) {
        return .buffer_too_small;
    }
    const feedbacks = (feedback_pointer orelse return .invalid_argument)[0..@intCast(feedback_count)];
    const validation_epoch = session.takeValidationEpoch();
    var latest_completed_at_ms = session.last_observed_at_ms;

    for (feedbacks, 0..) |*item, feedback_index| {
        const wire_status = protocol.validateFeedback(item);
        if (wire_status != .ok) return wire_status;
        const reservation_slot = matchFeedback(session, item) orelse return .feedback_mismatch;
        const reservation = &session.reservations[reservation_slot];
        if (reservation.validation_epoch == validation_epoch) return .feedback_mismatch;
        reservation.validation_epoch = validation_epoch;
        reservation.validation_feedback_index = @intCast(feedback_index);
        latest_completed_at_ms = @max(latest_completed_at_ms, item.completed_at_ms);
    }

    for (session.reservations) |*reservation| {
        if (!reservation.occupied or reservation.validation_epoch != validation_epoch) continue;
        const item = feedbacks[reservation.validation_feedback_index];
        reservation.feedback = item;
        reservation.feedback_received = true;
        if (reservation.action.atomic_group_id != 0) {
            const group = &session.atomic_groups[reservation.atomic_group_slot];
            group.feedback_count += 1;
            if (item.status != @intFromEnum(protocol.FeedbackStatus.succeeded)) group.failed_count += 1;
        }
    }

    for (session.reservations, 0..) |reservation, reservation_slot| {
        if (!reservation.occupied or reservation.validation_epoch != validation_epoch or
            reservation.action.atomic_group_id != 0) continue;
        finishReservation(session, @intCast(reservation_slot), false);
    }
    finishCompleteAtomicGroups(session);
    session.rebuildReservationIndex();
    session.rebuildAtomicGroupIndex();
    planner.resolveNextWake(session, latest_completed_at_ms);
    session.bumpRevision();
    planner.fillSnapshot(session, null, 0, snapshot);
    snapshot.state_revision = session.state_revision;
    return .ok;
}

pub fn expireReservations(session: *state.Session, now_ms: i64) protocol.Status {
    for (session.reservations) |reservation| {
        if (!reservation.occupied or reservation.feedback_received or reservation.deadline_ms > now_ms or
            reservation.action.atomic_group_id == 0) continue;
        if (reservation.atomic_group_slot == no_slot or reservation.atomic_group_slot >= session.atomic_groups.len) {
            return .unavailable;
        }
        const group = session.atomic_groups[reservation.atomic_group_slot];
        if (!group.occupied or group.group_id != reservation.action.atomic_group_id) return .unavailable;
    }
    var expired_any = false;
    for (session.reservations) |*reservation| {
        if (!reservation.occupied or reservation.feedback_received or reservation.deadline_ms > now_ms) continue;
        reservation.feedback = timeoutFeedback(reservation, now_ms);
        reservation.feedback_received = true;
        expired_any = true;
        if (reservation.action.atomic_group_id != 0) {
            const group = &session.atomic_groups[reservation.atomic_group_slot];
            group.feedback_count += 1;
            group.failed_count += 1;
        }
    }
    if (!expired_any) return .ok;

    for (session.reservations, 0..) |reservation, slot| {
        if (!reservation.occupied or !reservation.feedback_received or reservation.action.atomic_group_id != 0) continue;
        finishReservation(session, @intCast(slot), false);
    }
    finishCompleteAtomicGroups(session);
    session.last_reason_mask |= protocol.Reason.reservation_timed_out | protocol.Reason.state_uncertain;
    session.requires_authoritative_resync = true;
    session.rebuildReservationIndex();
    session.rebuildAtomicGroupIndex();
    planner.resolveNextWake(session, now_ms);
    session.bumpRevision();
    return .ok;
}

fn matchFeedback(session: *state.Session, item: *const protocol.Feedback) ?u32 {
    if ((item.valid_mask & protocol.FeedbackValidity.completed_at) == 0) {
        return null;
    }
    const reservation_slot = session.findReservation(item.action_id) orelse return null;
    const reservation = &session.reservations[reservation_slot];
    if (reservation.feedback_received or item.plan_epoch != reservation.action.plan_epoch or
        item.config_generation != reservation.action.config_generation or
        item.scope != reservation.action.scope or item.completed_at_ms < reservation.created_at_ms)
    {
        return null;
    }
    if (!matchesIdentity(&reservation.action, item)) return null;
    if (!feedback_contract.validate(.{
        .scope = reservation.action.scope,
        .domain_mask = reservation.action.domain_mask,
        .process_from_grade = reservation.action.from_process_grade,
        .process_to_grade = reservation.action.to_process_grade,
        .cpu_from_grade = reservation.action.from_cpu_grade,
        .cpu_to_grade = reservation.action.to_cpu_grade,
        .gpu_from_grade = reservation.action.from_gpu_grade,
        .gpu_to_grade = reservation.action.to_gpu_grade,
        .valid_mask = item.valid_mask,
        .flags = item.flags,
        .status = item.status,
        .actual_process_grade = item.actual_process_grade,
        .actual_cpu_grade = item.actual_cpu_grade,
        .actual_gpu_grade = item.actual_gpu_grade,
    })) return null;
    if (reservation.action.atomic_group_id != 0) {
        if (reservation.atomic_group_slot == no_slot or reservation.atomic_group_slot >= session.atomic_groups.len) return null;
        const group = session.atomic_groups[reservation.atomic_group_slot];
        if (!group.occupied or group.group_id != reservation.action.atomic_group_id) return null;
    } else if (reservation.atomic_group_slot != no_slot) return null;

    return reservation_slot;
}

fn matchesIdentity(action: *const protocol.Action, item: *const protocol.Feedback) bool {
    if (action.scope == @intFromEnum(protocol.ActionScope.process_policy)) {
        return item.target_key == action.target_key and item.software_key == action.software_key and
            item.process_id == action.process_id and item.process_start_key == action.process_start_key;
    }
    return item.target_key == action.target_key and item.software_key == action.software_key and
        item.process_id == 0 and item.process_start_key == 0;
}

fn finishCompleteAtomicGroups(session: *state.Session) void {
    for (session.reservations, 0..) |reservation, reservation_slot| {
        if (!reservation.occupied or reservation.action.atomic_group_id == 0 or
            reservation.atomic_group_slot == no_slot) continue;
        const group = session.atomic_groups[reservation.atomic_group_slot];
        if (!group.occupied or group.feedback_count < group.expected_count) continue;
        const group_failed = group.failed_count != 0;
        if (group_failed and reservation.feedback.status == @intFromEnum(protocol.FeedbackStatus.succeeded) and
            reservation.action.scope == @intFromEnum(protocol.ActionScope.process_policy) and
            reservation.action.to_process_grade == protocol.ProcessGrade.level4 and
            reservation.action.software_key != 0)
        {
            if (session.findSoftware(reservation.action.software_key)) |software_slot| {
                session.softwares[software_slot].freeze_compensation_active = true;
            }
        }
        finishReservation(session, @intCast(reservation_slot), group_failed);
    }
    for (session.atomic_groups, 0..) |group, group_slot| {
        if (group.occupied and group.feedback_count >= group.expected_count) {
            session.releaseAtomicGroup(@intCast(group_slot));
        }
    }
}

fn finishReservation(session: *state.Session, reservation_slot: u32, atomic_group_failed: bool) void {
    const reservation = &session.reservations[reservation_slot];
    const action = reservation.action;
    const item = reservation.feedback;
    const status: protocol.FeedbackStatus = @enumFromInt(item.status);

    if (action.scope == @intFromEnum(protocol.ActionScope.process_policy)) {
        if (reservation.process_slot != no_slot and session.processes[reservation.process_slot].occupied) {
            const process = &session.processes[reservation.process_slot];
            switch (status) {
                .succeeded => {
                    commitProcessSuccess(&process.transition, action, item);
                    if (atomic_group_failed and action.to_process_grade == protocol.ProcessGrade.level4) {
                        armProcessCompensation(&process.transition, item.completed_at_ms, session.config.required_consecutive_decisions);
                        process.reason_mask |= protocol.Reason.atomic_group_compensation;
                        session.last_reason_mask |= protocol.Reason.atomic_group_compensation;
                    }
                },
                .failed_unchanged, .rejected, .skipped => recordProcessFailure(session, process, action, item.completed_at_ms, protocol.Reason.feedback_failed),
                .ownership_lost => recordProcessOwnershipLost(session, process, item),
                .state_uncertain => recordProcessUncertain(session, process, action, item.completed_at_ms),
            }
        }
    } else if (reservation.software_slot != no_slot and session.softwares[reservation.software_slot].occupied) {
        const software = &session.softwares[reservation.software_slot];
        switch (status) {
            .succeeded => commitSoftwareSuccess(software, action, item),
            .failed_unchanged, .rejected, .skipped => recordSoftwareFailure(session, software, action, item.completed_at_ms, protocol.Reason.feedback_failed),
            .ownership_lost => recordSoftwareOwnershipLost(session, software, action, item),
            .state_uncertain => recordSoftwareUncertain(session, software, action, item.completed_at_ms),
        }
    }
    session.releaseReservation(reservation_slot);
}

fn commitProcessSuccess(transition: *state.ProcessTransition, action: protocol.Action, item: protocol.Feedback) void {
    transition.applied = item.actual_process_grade;
    transition.owned = (item.flags & protocol.FeedbackFlags.process_owned) != 0;
    transition.inflight_action_id = 0;
    transition.retry_not_before_ms = 0;
    transition.last_feedback_ms = item.completed_at_ms;
    transition.failure_count = 0;
    if (action.to_process_grade == protocol.ProcessGrade.normal) transition.compensation_required = false;
    if (transition.desired == transition.applied) transition.clearPending();
}

fn commitSoftwareSuccess(software: *state.SoftwareState, action: protocol.Action, item: protocol.Feedback) void {
    if ((action.domain_mask & protocol.GradeDomains.cpu) != 0) {
        commitAdapterSuccess(&software.cpu_transition, item.actual_cpu_grade, (item.flags & protocol.FeedbackFlags.cpu_owned) != 0, item.completed_at_ms);
    }
    if ((action.domain_mask & protocol.GradeDomains.gpu) != 0) {
        commitAdapterSuccess(&software.gpu_transition, item.actual_gpu_grade, (item.flags & protocol.FeedbackFlags.gpu_owned) != 0, item.completed_at_ms);
    }
}

fn commitAdapterSuccess(transition: *state.AdapterTransition, actual: u8, owned: bool, completed_at_ms: i64) void {
    transition.applied = actual;
    transition.owned = owned;
    transition.inflight_action_id = 0;
    transition.retry_not_before_ms = 0;
    transition.last_feedback_ms = completed_at_ms;
    transition.failure_count = 0;
    if (transition.desired == transition.applied) transition.clearPending();
}

fn recordProcessFailure(
    session: *state.Session,
    process: *state.ProcessState,
    action: protocol.Action,
    completed_at_ms: i64,
    reason: u64,
) void {
    const transition = &process.transition;
    transition.inflight_action_id = 0;
    transition.last_feedback_ms = completed_at_ms;
    transition.failure_count +|= 1;
    transition.retry_not_before_ms = saturatingAdd(completed_at_ms, session.config.failure_retry_ms);
    armProcessPending(transition, action.to_process_grade, completed_at_ms, session.config.required_consecutive_decisions);
    process.reason_mask |= reason;
    session.last_reason_mask |= reason;
}

fn recordSoftwareFailure(
    session: *state.Session,
    software: *state.SoftwareState,
    action: protocol.Action,
    completed_at_ms: i64,
    reason: u64,
) void {
    if ((action.domain_mask & protocol.GradeDomains.cpu) != 0) {
        recordAdapterFailure(&software.cpu_transition, action.to_cpu_grade, completed_at_ms, session.config);
    }
    if ((action.domain_mask & protocol.GradeDomains.gpu) != 0) {
        recordAdapterFailure(&software.gpu_transition, action.to_gpu_grade, completed_at_ms, session.config);
    }
    software.reason_mask |= reason;
    session.last_reason_mask |= reason;
}

fn recordAdapterFailure(
    transition: *state.AdapterTransition,
    target: u8,
    completed_at_ms: i64,
    config: protocol.Config,
) void {
    transition.inflight_action_id = 0;
    transition.last_feedback_ms = completed_at_ms;
    transition.failure_count +|= 1;
    transition.retry_not_before_ms = saturatingAdd(completed_at_ms, config.failure_retry_ms);
    transition.pending_from = transition.applied;
    transition.pending_to = target;
    transition.pending_count = config.required_consecutive_decisions;
    transition.pending_first_seen_ms = completed_at_ms;
    transition.pending_last_seen_ms = completed_at_ms;
}

fn recordProcessOwnershipLost(
    session: *state.Session,
    process: *state.ProcessState,
    item: protocol.Feedback,
) void {
    const transition = &process.transition;
    session.requires_authoritative_resync = true;
    process.reason_mask |= protocol.Reason.state_uncertain;
    session.last_reason_mask |= protocol.Reason.state_uncertain;
    transition.owned = false;
    transition.inflight_action_id = 0;
    transition.retry_not_before_ms = 0;
    transition.last_feedback_ms = item.completed_at_ms;
    transition.failure_count = 0;
    transition.compensation_required = false;
    transition.clearPending();
    process.reason_mask |= protocol.Reason.ownership_lost;
    session.last_reason_mask |= protocol.Reason.ownership_lost;
}

fn recordSoftwareOwnershipLost(session: *state.Session, software: *state.SoftwareState, action: protocol.Action, item: protocol.Feedback) void {
    if ((action.domain_mask & protocol.GradeDomains.cpu) != 0) {
        recordAdapterOwnershipLost(session, software, &software.cpu_transition, item.completed_at_ms);
    }
    if ((action.domain_mask & protocol.GradeDomains.gpu) != 0) {
        recordAdapterOwnershipLost(session, software, &software.gpu_transition, item.completed_at_ms);
    }
    software.reason_mask |= protocol.Reason.ownership_lost;
    session.last_reason_mask |= protocol.Reason.ownership_lost;
}

fn recordAdapterOwnershipLost(
    session: *state.Session,
    software: *state.SoftwareState,
    transition: *state.AdapterTransition,
    completed_at_ms: i64,
) void {
    session.requires_authoritative_resync = true;
    software.reason_mask |= protocol.Reason.state_uncertain;
    session.last_reason_mask |= protocol.Reason.state_uncertain;
    transition.owned = false;
    transition.inflight_action_id = 0;
    transition.retry_not_before_ms = 0;
    transition.last_feedback_ms = completed_at_ms;
    transition.failure_count = 0;
    transition.clearPending();
}

fn recordProcessUncertain(session: *state.Session, process: *state.ProcessState, action: protocol.Action, completed_at_ms: i64) void {
    recordProcessFailure(session, process, action, completed_at_ms, protocol.Reason.state_uncertain);
    session.requires_authoritative_resync = true;
}

fn recordSoftwareUncertain(session: *state.Session, software: *state.SoftwareState, action: protocol.Action, completed_at_ms: i64) void {
    recordSoftwareFailure(session, software, action, completed_at_ms, protocol.Reason.state_uncertain);
    session.requires_authoritative_resync = true;
}

fn armProcessCompensation(transition: *state.ProcessTransition, now_ms: i64, required: u32) void {
    transition.compensation_required = true;
    armProcessPending(transition, protocol.ProcessGrade.normal, now_ms, required);
}

fn armProcessPending(transition: *state.ProcessTransition, target: i8, now_ms: i64, required: u32) void {
    transition.desired = target;
    transition.pending_from = transition.applied;
    transition.pending_to = target;
    transition.pending_count = required;
    transition.pending_first_seen_ms = now_ms;
    transition.pending_last_seen_ms = now_ms;
}

fn timeoutFeedback(reservation: *const state.Reservation, now_ms: i64) protocol.Feedback {
    return .{
        .struct_size = @sizeOf(protocol.Feedback),
        .flags = 0,
        .valid_mask = protocol.FeedbackValidity.completed_at,
        .action_id = reservation.action.action_id,
        .plan_epoch = reservation.action.plan_epoch,
        .config_generation = reservation.action.config_generation,
        .target_key = reservation.action.target_key,
        .software_key = reservation.action.software_key,
        .process_start_key = reservation.action.process_start_key,
        .completed_at_ms = now_ms,
        .process_id = reservation.action.process_id,
        .system_error_code = 0,
        .status = @intFromEnum(protocol.FeedbackStatus.state_uncertain),
        .scope = reservation.action.scope,
        .actual_process_grade = protocol.ProcessGrade.normal,
        .actual_cpu_grade = @intFromEnum(protocol.AdapterGrade.normal),
        .actual_gpu_grade = @intFromEnum(protocol.AdapterGrade.normal),
        .reserved0 = [_]u8{0} ** 3,
        .reserved1 = 0,
    };
}

fn saturatingAdd(value: i64, milliseconds: u32) i64 {
    return std.math.add(i64, value, @as(i64, @intCast(milliseconds))) catch std.math.maxInt(i64);
}
