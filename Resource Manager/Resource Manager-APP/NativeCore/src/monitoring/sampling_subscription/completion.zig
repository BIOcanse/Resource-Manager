const std = @import("std");
const protocol = @import("protocol.zig");
const alignment = @import("alignment.zig");
const state = @import("state.zig");
const ResultCode = @import("../../common/result_codes.zig").ResultCode;

pub fn complete(
    session: *state.Session,
    input: *const protocol.CompletionInput,
    references: []const protocol.ItemReference,
    output: *protocol.CompletionOutput,
) ResultCode {
    state.lockMutex(&session.mutex);
    defer session.mutex.unlock();

    if (!protocol.validCompletion(input, &session.config)) return .abi_mismatch;
    if (references.len != input.item_count) return .invalid_argument;
    if (!emptyOutput(output)) return .abi_mismatch;
    if (!session.operationAdvances(input.operation_epoch, input.command_at_milliseconds)) {
        return .stale_frame;
    }
    if (!session.observedTimeValid(input.completed_at_milliseconds, input.command_at_milliseconds)) {
        return .stale_frame;
    }
    if (input.plan_epoch > session.last_plan_epoch) return .stale_frame;

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

    const active_item_count = session.rebuildItemAggregates();
    session.alignKnownItems(input.command_at_milliseconds);
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
