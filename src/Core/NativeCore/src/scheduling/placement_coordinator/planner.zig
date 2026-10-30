const std = @import("std");
const builtin = @import("builtin");
const protocol = @import("protocol.zig");
const state = @import("state.zig");
const ResultCode = @import("../../common/result_codes.zig").ResultCode;

const empty_desired = [_]protocol.DesiredInput{};
const empty_applied = [_]protocol.AppliedInput{};
const empty_actions = [_]protocol.ActionOutput{};

pub fn plan(
    session: *state.Session,
    cycle: *protocol.CycleInput,
    desired_pointer: ?[*]const protocol.DesiredInput,
    desired_capacity: u32,
    applied_pointer: ?[*]const protocol.AppliedInput,
    applied_capacity: u32,
    action_pointer: ?[*]protocol.ActionOutput,
    action_capacity: u32,
) ResultCode {
    state.lockMutex(&session.mutex);
    defer session.mutex.unlock();

    if (!protocol.validCycleHeader(cycle, &session.config)) return .abi_mismatch;
    if (cycle.cycle_epoch <= session.last_cycle_epoch) return .stale_frame;
    if (addTime(cycle.observed_at_milliseconds, session.config.action_timeout_milliseconds) == null or
        addTime(cycle.observed_at_milliseconds, session.config.retry_delay_milliseconds) == null)
        return .invalid_argument;
    _ = std.math.add(u32, cycle.desired_count, cycle.applied_count) catch return .invalid_argument;
    if (desired_capacity < cycle.desired_count or
        applied_capacity < cycle.applied_count or
        action_capacity < cycle.action_capacity)
    {
        return .buffer_too_small;
    }

    const desired_rows = rowsOrEmpty(
        protocol.DesiredInput,
        desired_pointer,
        cycle.desired_count,
        &empty_desired,
    ) orelse return .invalid_argument;
    const applied_rows = rowsOrEmpty(
        protocol.AppliedInput,
        applied_pointer,
        cycle.applied_count,
        &empty_applied,
    ) orelse return .invalid_argument;
    const actions = outputRowsOrEmpty(
        protocol.ActionOutput,
        action_pointer,
        cycle.action_capacity,
        &empty_actions,
    ) orelse return .invalid_argument;

    if (!session.rebuildStateIndex()) return .invalid_argument;
    var required_actions: u32 = 0;
    const validation = validateInputs(session, cycle, desired_rows, applied_rows, &required_actions);
    if (validation != .ok) return validation;
    // The caller sizes actions by this cycle's real count. A short buffer is
    // reported with that count before any slot changes, so the same cycle can
    // run again with exactly that many rows.
    if (required_actions > actions.len) {
        cycle.action_count = required_actions;
        return .buffer_too_small;
    }

    // Validation leaves the current desired/applied identities in scratch.
    for (session.slots) |*slot| {
        if (isSettledOwnershipLoss(slot.*) and session.findScratch(slot.target_key, slot.record_key) == null)
            slot.* = .{};
    }
    _ = session.rebuildStateIndex();

    for (session.slots) |*slot| {
        slot.desired_seen = false;
        slot.applied_seen = false;
        slot.override_external_state = false;
    }

    for (applied_rows) |row| {
        const slot_index = session.findOrCreateSlot(row.target_key, row.record_key) orelse
            return .buffer_too_small;
        if (!ingestApplied(session, &session.slots[slot_index], row, cycle)) return .invalid_argument;
    }

    if (!session.rebuildStateIndex()) return .invalid_argument;
    clearAbsentAppliedState(session);
    if (!session.rebuildStateIndex()) return .invalid_argument;

    for (desired_rows) |row| {
        const slot_index = session.findOrCreateSlot(row.target_key, row.record_key) orelse
            return .buffer_too_small;
        ingestDesired(&session.slots[slot_index], row, cycle);
    }

    @memset(actions, std.mem.zeroes(protocol.ActionOutput));
    session.next_wake_milliseconds = 0;
    var action_count: usize = 0;

    for (session.slots) |*slot| {
        if (!slot.active or !slot.applied_seen) continue;
        switch (slot.state) {
            .applied => {
                if (!slot.desired_seen or slot.desired_digest != slot.applied_digest) {
                    const reason = if (slot.desired_seen)
                        protocol.ActionReason.desired_changed
                    else
                        protocol.ActionReason.desired_missing;
                    if (!issueAction(session, slot, .restore, reason, cycle, actions, &action_count))
                        return .buffer_too_small;
                }
            },
            .pending_restore => {
                if (!issueRetryIfDue(session, slot, .restore, cycle, actions, &action_count))
                    return .buffer_too_small;
            },
            .retry_wait => {
                if (slot.pending_disposition == .restore and
                    !issueRetryIfDue(session, slot, .restore, cycle, actions, &action_count))
                    return .buffer_too_small;
            },
            .blocked => {
                updateNextWake(session, slot.retry_at_milliseconds);
                if (slot.applied_seen and
                    slot.pending_reason_mask & (protocol.ActionReason.ownership_lost |
                        protocol.ActionReason.already_restored) != 0 and
                    !issueAction(
                        session,
                        slot,
                        .restore,
                        slot.pending_reason_mask,
                        cycle,
                        actions,
                        &action_count,
                    )) return .buffer_too_small;
            },
            else => {},
        }
    }

    for (session.slots) |*slot| {
        if (!slot.active or !slot.desired_seen) continue;
        switch (slot.state) {
            .empty => {
                if (!issueAction(
                    session,
                    slot,
                    .apply,
                    protocol.ActionReason.receipt_missing,
                    cycle,
                    actions,
                    &action_count,
                )) return .buffer_too_small;
            },
            .blocked => {
                if (!slot.applied_seen and slot.override_external_state and
                    !issueAction(
                        session,
                        slot,
                        .apply,
                        protocol.ActionReason.ownership_lost,
                        cycle,
                        actions,
                        &action_count,
                    )) return .buffer_too_small;
                updateNextWake(session, slot.retry_at_milliseconds);
            },
            .pending_apply => {
                if (!issueRetryIfDue(session, slot, .apply, cycle, actions, &action_count))
                    return .buffer_too_small;
            },
            .retry_wait => {
                if (slot.pending_disposition == .apply and
                    !issueRetryIfDue(session, slot, .apply, cycle, actions, &action_count))
                    return .buffer_too_small;
            },
            else => {},
        }
    }

    // actionsFor mirrors the two passes above; every plan in the tests checks it.
    if (builtin.mode == .Debug) std.debug.assert(action_count == required_actions);
    for (session.slots) |*slot| {
        if (slot.active and !slot.desired_seen and slot.state == .empty) slot.* = .{};
    }
    sortActions(actions[0..action_count]);
    _ = session.rebuildStateIndex();
    session.last_cycle_epoch = cycle.cycle_epoch;
    session.advanceRevision();
    cycle.action_count = @intCast(action_count);
    cycle.next_wake_milliseconds = session.next_wake_milliseconds;
    cycle.state_revision = session.state_revision;
    return .ok;
}

fn validateInputs(
    session: *state.Session,
    cycle: *const protocol.CycleInput,
    desired_rows: []const protocol.DesiredInput,
    applied_rows: []const protocol.AppliedInput,
    required_actions: *u32,
) ResultCode {
    session.beginScratch();
    for (desired_rows, 0..) |*row, row_index| {
        if (!protocol.validDesired(row)) return .abi_mismatch;
        if (!session.insertScratch(row.target_key, row.record_key, @intCast(row_index)))
            return .invalid_argument;
        if (session.findSlot(row.target_key, row.record_key)) |slot_index| {
            const slot = session.slots[slot_index];
            if (slot.active and (slot.resource_kind != 0 and
                (slot.resource_kind != row.resource_kind or slot.placement_kind != row.placement_kind)))
                return .invalid_argument;
        }
    }

    for (applied_rows) |*row| {
        if (!protocol.validApplied(row)) return .abi_mismatch;
        var desired_match: ?*const protocol.DesiredInput = null;
        if (session.findScratch(row.target_key, row.record_key)) |desired_index| {
            const desired = desired_rows[desired_index];
            if (desired.resource_kind != row.resource_kind or desired.placement_kind != row.placement_kind)
                return .invalid_argument;
            desired_match = &desired_rows[desired_index];
        }
        if (session.findSlot(row.target_key, row.record_key)) |slot_index| {
            const slot = session.slots[slot_index];
            if (slot.active and (slot.resource_kind != 0 and
                (slot.resource_kind != row.resource_kind or slot.placement_kind != row.placement_kind)))
                return .invalid_argument;
        }
        required_actions.* += actionsFor(session, cycle, row, desired_match);
    }

    session.beginScratch();
    for (applied_rows, 0..) |*row, row_index| {
        if (!session.insertScratch(row.target_key, row.record_key, @intCast(row_index)))
            return .invalid_argument;
    }
    for (desired_rows) |*row| {
        if (session.findScratch(row.target_key, row.record_key) == null)
            required_actions.* += actionsFor(session, cycle, null, row);
    }
    session.beginScratch();
    var union_count: u32 = 0;
    for (desired_rows) |row| {
        if (session.findScratch(row.target_key, row.record_key) != null) continue;
        if (union_count >= session.config.maximum_state_count or
            !session.insertScratch(row.target_key, row.record_key, union_count))
            return .buffer_too_small;
        union_count += 1;
    }
    for (applied_rows) |row| {
        if (session.findScratch(row.target_key, row.record_key) != null) continue;
        if (union_count >= session.config.maximum_state_count or
            !session.insertScratch(row.target_key, row.record_key, union_count))
            return .buffer_too_small;
        union_count += 1;
    }
    for (session.slots) |slot| {
        if (!slot.active or session.findScratch(slot.target_key, slot.record_key) != null) continue;
        if (isSettledOwnershipLoss(slot)) continue;
        if (union_count >= session.config.maximum_state_count) return .buffer_too_small;
        union_count += 1;
    }
    return .ok;
}

fn isSettledOwnershipLoss(slot: state.Slot) bool {
    return slot.active and slot.state == .blocked and slot.pending_action_id == 0 and
        slot.pending_disposition == null and slot.pending_reason_mask == protocol.ActionReason.ownership_lost;
}

fn clearAbsentAppliedState(session: *state.Session) void {
    for (session.slots) |*slot| {
        if (!slot.active or slot.applied_seen) continue;
        if (clearsWhenAbsent(slot.*)) slot.* = .{};
    }
}

fn clearsWhenAbsent(slot: state.Slot) bool {
    return switch (slot.state) {
        .applied => true,
        .blocked => slot.pending_reason_mask == protocol.ActionReason.observation_unavailable,
        else => false,
    };
}

// Whether this cycle issues an action for one identity. Its rows land on a copy
// of its slot exactly as plan lands them on the live slot; at most one of the
// two passes issues for a slot, because their states and conditions are disjoint.
fn actionsFor(
    session: *const state.Session,
    cycle: *const protocol.CycleInput,
    applied: ?*const protocol.AppliedInput,
    desired: ?*const protocol.DesiredInput,
) u32 {
    const target_key = if (applied) |row| row.target_key else desired.?.target_key;
    const record_key = if (applied) |row| row.record_key else desired.?.record_key;
    const created: state.Slot = .{ .active = true, .target_key = target_key, .record_key = record_key };
    var slot = if (session.findSlot(target_key, record_key)) |slot_index| session.slots[slot_index] else created;
    slot.desired_seen = false;
    slot.applied_seen = false;
    slot.override_external_state = false;
    if (applied) |row| {
        if (!ingestApplied(session, &slot, row.*, cycle)) return 0;
    } else if (clearsWhenAbsent(slot)) {
        slot = created;
    }
    if (desired) |row| ingestDesired(&slot, row.*, cycle);
    if (slot.applied_seen and issuesRestore(slot, cycle)) return 1;
    if (slot.desired_seen and issuesApply(slot, cycle)) return 1;
    return 0;
}

// The applied pass issues a restore for this slot.
fn issuesRestore(slot: state.Slot, cycle: *const protocol.CycleInput) bool {
    return switch (slot.state) {
        .applied => !slot.desired_seen or slot.desired_digest != slot.applied_digest,
        .pending_restore => retryIsDue(slot, cycle),
        .retry_wait => slot.pending_disposition == .restore and retryIsDue(slot, cycle),
        .blocked => slot.pending_reason_mask & (protocol.ActionReason.ownership_lost |
            protocol.ActionReason.already_restored) != 0,
        else => false,
    };
}

// The desired pass issues an apply for this slot.
fn issuesApply(slot: state.Slot, cycle: *const protocol.CycleInput) bool {
    return switch (slot.state) {
        .empty => true,
        .blocked => !slot.applied_seen and slot.override_external_state,
        .pending_apply => retryIsDue(slot, cycle),
        .retry_wait => slot.pending_disposition == .apply and retryIsDue(slot, cycle),
        else => false,
    };
}

fn retryIsDue(slot: state.Slot, cycle: *const protocol.CycleInput) bool {
    return cycle.observed_at_milliseconds >= slot.retry_at_milliseconds;
}

fn populateIdentity(slot: *state.Slot, resource_kind: u32, placement_kind: u32) void {
    slot.resource_kind = resource_kind;
    slot.placement_kind = placement_kind;
}
// One applied row's receipt and observation on its slot. plan applies it to
// the live slot and actionsFor to a copy, so both see the same state.
fn ingestApplied(
    session: *const state.Session,
    slot: *state.Slot,
    row: protocol.AppliedInput,
    cycle: *const protocol.CycleInput,
) bool {
    populateIdentity(slot, row.resource_kind, row.placement_kind);
    slot.applied_seen = true;
    slot.applied_digest = row.receipt_digest;
    slot.previous_digest = row.previous_digest;
    slot.current_digest = row.current_digest;
    slot.applied_process_id = row.process_id;
    slot.applied_process_start_key = row.process_start_key;
    slot.last_cycle_epoch = cycle.cycle_epoch;

    if (row.flags & protocol.AppliedFlags.payload_valid == 0) {
        slot.state = .blocked;
        slot.pending_action_id = 0;
        slot.pending_disposition = null;
        slot.pending_reason_mask = protocol.ActionReason.invalid_receipt;
        slot.retry_at_milliseconds = 0;
        return true;
    }

    switch (@as(protocol.ObservationStatus, @enumFromInt(row.observation_status))) {
        .unchecked => {
            slot.state = .applied;
            slot.pending_action_id = 0;
            slot.pending_disposition = null;
            slot.pending_reason_mask = 0;
            slot.retry_at_milliseconds = 0;
        },
        .unavailable => {
            slot.state = .blocked;
            slot.pending_action_id = 0;
            slot.pending_disposition = null;
            slot.pending_reason_mask = protocol.ActionReason.observation_unavailable;
            slot.retry_at_milliseconds = addTime(
                cycle.observed_at_milliseconds,
                session.config.retry_delay_milliseconds,
            ) orelse return false;
        },
        .not_found_or_exited => {
            slot.state = .blocked;
            slot.pending_action_id = 0;
            slot.pending_disposition = null;
            slot.pending_reason_mask = protocol.ActionReason.ownership_lost;
            slot.retry_at_milliseconds = 0;
        },
        .found => {
            if (row.current_digest == row.receipt_digest) {
                slot.state = .applied;
                slot.pending_action_id = 0;
                slot.pending_disposition = null;
                slot.pending_reason_mask = 0;
                slot.retry_at_milliseconds = 0;
                slot.retry_count = 0;
            } else if (row.current_digest == row.previous_digest) {
                slot.state = .blocked;
                slot.pending_action_id = 0;
                slot.pending_disposition = null;
                slot.pending_reason_mask = protocol.ActionReason.already_restored;
                slot.retry_at_milliseconds = 0;
            } else {
                slot.state = .blocked;
                slot.pending_action_id = 0;
                slot.pending_disposition = null;
                slot.pending_reason_mask = protocol.ActionReason.ownership_lost;
                slot.retry_at_milliseconds = 0;
            }
        },
    }
    return true;
}

fn ingestDesired(slot: *state.Slot, row: protocol.DesiredInput, cycle: *const protocol.CycleInput) void {
    populateIdentity(slot, row.resource_kind, row.placement_kind);
    slot.desired_seen = true;
    slot.override_external_state = row.flags & protocol.DesiredFlags.override_external_state != 0;
    slot.desired_digest = row.desired_digest;
    slot.desired_process_id = row.process_id;
    slot.desired_process_start_key = row.process_start_key;
    slot.desired_priority = row.priority;
    slot.last_cycle_epoch = cycle.cycle_epoch;
}

fn issueRetryIfDue(
    session: *state.Session,
    slot: *state.Slot,
    disposition: protocol.ActionDisposition,
    cycle: *const protocol.CycleInput,
    actions: []protocol.ActionOutput,
    action_count: *usize,
) bool {
    if (!retryIsDue(slot.*, cycle)) {
        updateNextWake(session, slot.retry_at_milliseconds);
        return true;
    }
    slot.retry_count +|= 1;
    return issueAction(
        session,
        slot,
        disposition,
        protocol.ActionReason.retry_due,
        cycle,
        actions,
        action_count,
    );
}

fn issueAction(
    session: *state.Session,
    slot: *state.Slot,
    disposition: protocol.ActionDisposition,
    reason_mask: u64,
    cycle: *const protocol.CycleInput,
    actions: []protocol.ActionOutput,
    action_count: *usize,
) bool {
    if (action_count.* >= actions.len) return false;
    const deadline = addTime(
        cycle.observed_at_milliseconds,
        session.config.action_timeout_milliseconds,
    ) orelse return false;
    const action_id = session.allocateActionId();
    const process_id = switch (disposition) {
        .apply => slot.desired_process_id,
        .restore => slot.applied_process_id,
    };
    const process_start_key = switch (disposition) {
        .apply => slot.desired_process_start_key,
        .restore => slot.applied_process_start_key,
    };
    actions[action_count.*] = .{
        .struct_size = @sizeOf(protocol.ActionOutput),
        .disposition = @intFromEnum(disposition),
        .action_id = action_id,
        .target_key = slot.target_key,
        .record_key = slot.record_key,
        .resource_kind = slot.resource_kind,
        .placement_kind = slot.placement_kind,
        .desired_digest = slot.desired_digest,
        .previous_digest = slot.previous_digest,
        .process_start_key = process_start_key,
        .process_id = process_id,
        .flags = if (process_id != 0 and process_start_key != 0)
            protocol.ActionFlags.process_identity_valid
        else
            0,
        .reason_mask = reason_mask,
        .deadline_milliseconds = deadline,
        .priority = if (disposition == .apply) slot.desired_priority else 0,
        .reserved = 0,
    };
    action_count.* += 1;
    slot.pending_action_id = action_id;
    slot.pending_disposition = disposition;
    slot.pending_reason_mask = reason_mask;
    slot.retry_at_milliseconds = deadline;
    slot.state = switch (disposition) {
        .apply => .pending_apply,
        .restore => .pending_restore,
    };
    updateNextWake(session, deadline);
    return true;
}

fn sortActions(actions: []protocol.ActionOutput) void {
    std.sort.pdq(protocol.ActionOutput, actions, {}, actionLessThan);
}

fn actionLessThan(_: void, left: protocol.ActionOutput, right: protocol.ActionOutput) bool {
    const left_restore = left.disposition == @intFromEnum(protocol.ActionDisposition.restore);
    const right_restore = right.disposition == @intFromEnum(protocol.ActionDisposition.restore);
    if (left_restore != right_restore) return left_restore;
    if (!left_restore and left.priority != right.priority) return left.priority > right.priority;
    if (left.resource_kind != right.resource_kind) return left.resource_kind < right.resource_kind;
    if (left.placement_kind != right.placement_kind) return left.placement_kind < right.placement_kind;
    if (left.target_key != right.target_key) return left.target_key < right.target_key;
    if (left.record_key != right.record_key) return left.record_key < right.record_key;
    return left.action_id < right.action_id;
}

fn updateNextWake(session: *state.Session, candidate: u64) void {
    if (candidate == 0) return;
    if (session.next_wake_milliseconds == 0 or candidate < session.next_wake_milliseconds)
        session.next_wake_milliseconds = candidate;
}

fn addTime(value: u64, delta: u64) ?u64 {
    return std.math.add(u64, value, delta) catch null;
}

fn rowsOrEmpty(
    comptime T: type,
    pointer: ?[*]const T,
    count: u32,
    empty: *const [0]T,
) ?[]const T {
    if (count == 0) return empty;
    return (pointer orelse return null)[0..count];
}

fn outputRowsOrEmpty(
    comptime T: type,
    pointer: ?[*]T,
    count: u32,
    empty: *const [0]T,
) ?[]T {
    if (count == 0) return @constCast(empty);
    return (pointer orelse return null)[0..count];
}
