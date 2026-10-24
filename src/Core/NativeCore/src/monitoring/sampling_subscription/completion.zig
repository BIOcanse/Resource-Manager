const std = @import("std");
const protocol = @import("protocol.zig");
const alignment = @import("alignment.zig");
const state = @import("state.zig");
const ResultCode = @import("../../common/result_codes.zig").ResultCode;

pub fn complete(
    session: *state.Session,
    input: *const protocol.CompletionInput,
    references: []const protocol.ItemReference,
    trigger_references: []const protocol.TriggerOutput,
    output: *protocol.CompletionOutput,
) ResultCode {
    state.lockMutex(&session.mutex);
    defer session.mutex.unlock();

    if (!session.scratchReady()) return .invalid_argument;

    if (!protocol.validCompletion(input, &session.config)) return .abi_mismatch;
    if (references.len != input.item_count) return .invalid_argument;
    if (trigger_references.len != input.trigger_count) return .invalid_argument;
    if (!emptyOutput(output)) return .abi_mismatch;
    if (!session.operationAdvances(input.operation_epoch, input.command_at_milliseconds)) {
        return .stale_frame;
    }
    if (!session.observedTimeValid(input.completed_at_milliseconds, input.command_at_milliseconds)) {
        return .stale_frame;
    }
    if (input.plan_epoch > session.last_plan_epoch) return .stale_frame;
    if ((session.issued_trigger_count != 0 or trigger_references.len != 0) and
        (input.plan_epoch != session.issued_trigger_plan_epoch or
            trigger_references.len != session.issued_trigger_count))
    {
        return .stale_frame;
    }

    _ = session.rebuildItemAggregates();
    for (references, 0..) |reference, index| {
        if (!protocol.validItemReference(&reference)) return .abi_mismatch;
        session.scratch_item_handles[index] = reference.item_handle;
    }
    const handles = session.scratch_item_handles[0..references.len];
    std.sort.pdq(u64, handles, {}, lessThanU64);
    for (handles, 0..) |handle, index| {
        if (index != 0 and handles[index - 1] == handle) return .invalid_argument;
        const item_index = session.findItem(handle) orelse return .no_data;
        const item = &session.items[item_index];
        if (input.plan_epoch != 0 and item.last_planned_epoch != input.plan_epoch) {
            return .stale_frame;
        }
        if (input.status == @intFromEnum(protocol.CompletionStatus.sampled) and
            input.completed_at_milliseconds < item.last_sampled_milliseconds)
        {
            return .stale_frame;
        }
    }

    for (trigger_references, 0..) |reference, index| {
        if (!protocol.validTrigger(&reference)) return .abi_mismatch;
        if (reference.plan_epoch != input.plan_epoch) return .stale_frame;
        if (!wasIssuedTrigger(session, &reference)) return .stale_frame;
        for (trigger_references[0..index]) |prior| {
            if (prior.source_handle == reference.source_handle and
                prior.item_handle == reference.item_handle)
            {
                return .invalid_argument;
            }
        }
    }

    for (handles) |handle| {
        const item_index = session.findItem(handle) orelse unreachable;
        const item = &session.items[item_index];
        switch (@as(protocol.CompletionStatus, @enumFromInt(input.status))) {
            .sampled => {
                item.last_sampled_milliseconds = input.completed_at_milliseconds;
                item.retry_at_milliseconds = 0;
                if (session.scratch_source_counts[item_index] != 0) {
                    item.scheduled_due_milliseconds = alignment.nextDue(
                        input.completed_at_milliseconds,
                        session.scratch_effective_intervals[item_index],
                    );
                }
            },
            .failed => {
                item.retry_at_milliseconds = if (session.scratch_source_counts[item_index] == 0)
                    0
                else
                    alignment.nextDue(
                        input.completed_at_milliseconds,
                        session.scratch_effective_intervals[item_index],
                    );
            },
            .skipped => {
                item.retry_at_milliseconds = state.saturatingAdd(
                    input.command_at_milliseconds,
                    session.config.minimum_interval_milliseconds,
                );
            },
        }
        item.last_planned_epoch = 0;
    }

    for (trigger_references) |reference| {
        // A delivery-only plan also holds its item until settlement, even when
        // its source/membership was removed while the plan was in flight.
        if (session.findItem(reference.item_handle)) |item_index| {
            session.items[item_index].last_planned_epoch = 0;
        }
        if (!activeMembershipMatches(session, &reference, input.plan_epoch)) continue;
        const membership_index = session.findMembership(reference.source_handle, reference.item_handle) orelse unreachable;
        const membership = &session.memberships[membership_index];
        const next_due = state.saturatingAdd(
            membership.next_trigger_milliseconds,
            membership.period_milliseconds,
        );
        if (membership.trigger_sequence == std.math.maxInt(u64) or next_due == std.math.maxInt(u64)) {
            membership.trigger_sequence = 0;
            membership.next_trigger_milliseconds = 0;
        } else {
            membership.trigger_sequence += 1;
            membership.next_trigger_milliseconds = next_due;
        }
        membership.last_planned_epoch = 0;
    }

    @memset(session.scratch_issued_triggers, std.mem.zeroes(protocol.TriggerOutput));
    session.issued_trigger_count = 0;
    session.issued_trigger_plan_epoch = 0;

    const active_item_count = session.rebuildItemAggregates();
    session.refreshItemDeadlines(input.command_at_milliseconds);
    session.next_wake_milliseconds = session.nextWakeFromAggregates(input.command_at_milliseconds);
    session.commitOperation(input.operation_epoch, input.command_at_milliseconds);

    const active_source_count = session.activeSourceCount();
    var flags: u64 = 0;
    if (active_source_count != 0) flags |= protocol.CompletionFlags.active;
    if (session.next_wake_milliseconds != 0) flags |= protocol.CompletionFlags.next_wake_valid;
    output.* = .{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.CompletionOutput),
        .configuration_generation = session.config.generation,
        .state_revision = session.state_revision,
        .operation_epoch = input.operation_epoch,
        .next_wake_milliseconds = session.next_wake_milliseconds,
        .active_source_count = active_source_count,
        .active_item_count = active_item_count,
        .flags = flags,
        .reserved = .{ 0, 0 },
    };
    return .ok;
}

fn wasIssuedTrigger(session: *const state.Session, reference: *const protocol.TriggerOutput) bool {
    for (session.scratch_issued_triggers[0..session.issued_trigger_count]) |issued| {
        if (issued.struct_size == reference.struct_size and
            issued.flags == reference.flags and
            issued.source_handle == reference.source_handle and
            issued.item_handle == reference.item_handle and
            issued.scheduled_due_milliseconds == reference.scheduled_due_milliseconds and
            issued.sequence == reference.sequence and
            issued.plan_epoch == reference.plan_epoch)
        {
            return true;
        }
    }
    return false;
}

fn activeMembershipMatches(
    session: *const state.Session,
    reference: *const protocol.TriggerOutput,
    plan_epoch: u64,
) bool {
    const membership_index = session.findMembership(reference.source_handle, reference.item_handle) orelse
        return false;
    const membership = &session.memberships[membership_index];
    const source = &session.sources[membership.source_index];
    const item = &session.items[membership.item_index];
    const expected_flags = if (membership.sampling_flags & protocol.SourceFlags.fresh_on_trigger != 0)
        protocol.TriggerFlags.fresh
    else
        0;
    return source.active and item.active and
        membership.last_planned_epoch == plan_epoch and
        membership.next_trigger_milliseconds == reference.scheduled_due_milliseconds and
        membership.trigger_sequence == reference.sequence and
        reference.flags == expected_flags;
}

fn emptyOutput(output: *const protocol.CompletionOutput) bool {
    return output.abi_version == protocol.abi_version and
        output.struct_size == @sizeOf(protocol.CompletionOutput) and
        output.configuration_generation == 0 and output.state_revision == 0 and
        output.operation_epoch == 0 and output.next_wake_milliseconds == 0 and
        output.active_source_count == 0 and output.active_item_count == 0 and
        output.flags == 0 and protocol.allZero(&output.reserved);
}

fn lessThanU64(_: void, left: u64, right: u64) bool {
    return left < right;
}
