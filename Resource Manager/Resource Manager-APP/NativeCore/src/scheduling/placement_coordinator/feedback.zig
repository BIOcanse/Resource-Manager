const std = @import("std");
const protocol = @import("protocol.zig");
const state = @import("state.zig");
const ResultCode = @import("../../common/result_codes.zig").ResultCode;

const empty_feedback = [_]protocol.FeedbackInput{};

pub fn apply(
    session: *state.Session,
    feedback_pointer: ?[*]const protocol.FeedbackInput,
    feedback_count: u32,
) ResultCode {
    state.lockMutex(&session.mutex);
    defer session.mutex.unlock();
    if (feedback_count > session.config.maximum_action_count) return .buffer_too_small;
    const rows = if (feedback_count == 0)
        empty_feedback[0..]
    else
        (feedback_pointer orelse return .invalid_argument)[0..feedback_count];
    if (!session.rebuildStateIndex()) return .invalid_argument;

    session.beginScratch();
    for (rows, 0..) |*row, row_index| {
        if (!protocol.validFeedback(row)) return .abi_mismatch;
        if (!session.insertScratch(row.target_key, row.record_key, @intCast(row_index)))
            return .invalid_argument;
        const slot_index = session.findSlot(row.target_key, row.record_key) orelse
            return .stale_frame;
        const slot = session.slots[slot_index];
        if (slot.pending_action_id != row.action_id or slot.pending_disposition == null)
            return .stale_frame;
        if (!feedbackCompatible(slot.pending_disposition.?, @enumFromInt(row.status)))
            return .invalid_argument;
        const issued_at = slot.retry_at_milliseconds -| session.config.action_timeout_milliseconds;
        const latest = std.math.add(
            u64,
            slot.retry_at_milliseconds,
            session.config.maximum_future_skew_milliseconds,
        ) catch return .invalid_argument;
        if (row.completed_at_milliseconds < issued_at or row.completed_at_milliseconds > latest)
            return .stale_frame;
        if (@as(protocol.FeedbackStatus, @enumFromInt(row.status)) == .retryable_failure) {
            _ = std.math.add(
                u64,
                row.completed_at_milliseconds,
                session.config.retry_delay_milliseconds,
            ) catch return .invalid_argument;
        }
    }

    for (rows) |row| {
        const slot_index = session.findSlot(row.target_key, row.record_key).?;
        var slot = &session.slots[slot_index];
        if (row.valid_mask & protocol.FeedbackValid.observed_digest != 0)
            slot.current_digest = row.observed_digest;
        const disposition = slot.pending_disposition.?;
        switch (@as(protocol.FeedbackStatus, @enumFromInt(row.status))) {
            .applied => settleSuccess(session, slot_index, slot, disposition),
            .restored => settleSuccess(session, slot_index, slot, disposition),
            .already_satisfied => settleSuccess(session, slot_index, slot, disposition),
            .ownership_lost => {
                if (disposition == .restore and slot.desired_seen and
                    !slot.override_external_state)
                {
                    slot.state = .blocked;
                    slot.applied_seen = false;
                    slot.pending_action_id = 0;
                    slot.pending_disposition = null;
                    slot.pending_reason_mask = protocol.ActionReason.ownership_lost;
                    slot.retry_at_milliseconds = 0;
                } else {
                    session.clearSlot(slot_index);
                }
            },
            .retryable_failure => {
                slot.state = .retry_wait;
                slot.pending_action_id = 0;
                slot.pending_reason_mask = protocol.ActionReason.invalid_receipt;
                slot.retry_at_milliseconds = std.math.add(
                    u64,
                    row.completed_at_milliseconds,
                    session.config.retry_delay_milliseconds,
                ) catch return .invalid_argument;
                slot.retry_count +|= 1;
            },
            .invalid_receipt => {
                slot.state = .blocked;
                slot.pending_action_id = 0;
                slot.pending_disposition = null;
                slot.retry_at_milliseconds = 0;
            },
        }
    }
    if (!session.rebuildStateIndex()) return .invalid_argument;
    recomputeNextWake(session);
    session.advanceRevision();
    return .ok;
}

fn feedbackCompatible(
    disposition: protocol.ActionDisposition,
    status: protocol.FeedbackStatus,
) bool {
    return switch (disposition) {
        .apply => status != .restored,
        .restore => status != .applied,
    };
}

fn settleSuccess(
    session: *state.Session,
    slot_index: u32,
    slot: *state.Slot,
    disposition: protocol.ActionDisposition,
) void {
    switch (disposition) {
        .apply => {
            slot.state = .applied;
            slot.applied_digest = slot.desired_digest;
            slot.pending_action_id = 0;
            slot.pending_disposition = null;
            slot.retry_at_milliseconds = 0;
            slot.retry_count = 0;
        },
        .restore => session.clearSlot(slot_index),
    }
}

fn recomputeNextWake(session: *state.Session) void {
    session.next_wake_milliseconds = 0;
    for (session.slots) |slot| {
        if (!slot.active or slot.retry_at_milliseconds == 0) continue;
        if (session.next_wake_milliseconds == 0 or
            slot.retry_at_milliseconds < session.next_wake_milliseconds)
            session.next_wake_milliseconds = slot.retry_at_milliseconds;
    }
}
