const std = @import("std");
const protocol = @import("protocol.zig");
const alignment = @import("alignment.zig");
const state = @import("state.zig");
const ResultCode = @import("../../common/result_codes.zig").ResultCode;

pub fn plan(
    session: *state.Session,
    header: *protocol.PlanHeader,
    due_pointer: ?[*]protocol.DueItemOutput,
    due_capacity: u32,
    source_pointer: ?[*]protocol.SourceViewOutput,
    source_capacity: u32,
    expired_pointer: ?[*]protocol.ExpiredSourceOutput,
    expired_capacity: u32,
) ResultCode {
    state.lockMutex(&session.mutex);
    defer session.mutex.unlock();

    if (!protocol.validPlanHeader(header, &session.config)) return .abi_mismatch;
    if (due_capacity != header.due_item_capacity or
        source_capacity != header.source_view_capacity or
        expired_capacity != header.expired_source_capacity)
    {
        return .invalid_argument;
    }
    if (due_capacity < session.config.maximum_due_item_count or
        source_capacity < session.config.maximum_source_view_count or
        expired_capacity < session.config.maximum_expired_source_count)
    {
        return .buffer_too_small;
    }
    if (!session.operationAdvances(header.operation_epoch, header.command_at_milliseconds) or
        header.plan_epoch <= session.last_plan_epoch)
    {
        return .stale_frame;
    }

    const due = requireOutput(protocol.DueItemOutput, due_pointer, due_capacity) orelse
        return .invalid_argument;
    const sources = requireOutput(protocol.SourceViewOutput, source_pointer, source_capacity) orelse
        return .invalid_argument;
    const expired = requireOutput(protocol.ExpiredSourceOutput, expired_pointer, expired_capacity) orelse
        return .invalid_argument;
    @memset(due, std.mem.zeroes(protocol.DueItemOutput));
    @memset(sources, std.mem.zeroes(protocol.SourceViewOutput));
    @memset(expired, std.mem.zeroes(protocol.ExpiredSourceOutput));

    var expired_count: usize = 0;
    var any_expired = false;
    for (session.sources, 0..) |source, source_index| {
        if (!source.active or source.persistent) continue;
        if (header.command_at_milliseconds <= source.last_seen_milliseconds) continue;
        if (header.command_at_milliseconds - source.last_seen_milliseconds <=
            session.config.active_ttl_milliseconds) continue;
        expired[expired_count] = .{
            .struct_size = @sizeOf(protocol.ExpiredSourceOutput),
            .reason = @intFromEnum(protocol.ExpiryReason.ttl_elapsed),
            .source_handle = source.source_handle,
            .last_seen_milliseconds = source.last_seen_milliseconds,
            .source_flags = sourceFlags(&source),
            .item_count = session.sourceItemCount(@intCast(source_index)),
            .expired_at_milliseconds = header.command_at_milliseconds,
            .reserved = 0,
        };
        expired_count += 1;
        session.deactivateSourceAt(@intCast(source_index));
        any_expired = true;
    }
    if (any_expired) session.clearInactiveSourceMemberships();

    const active_item_count = session.rebuildItemAggregates();
    session.alignKnownItems(header.command_at_milliseconds);
    var source_count: usize = 0;
    for (session.sources, 0..) |source, source_index| {
        if (!source.active) continue;
        sources[source_count] = sourceOutput(session, &source, @intCast(source_index));
        source_count += 1;
    }

    var candidate_count: usize = 0;
    for (session.items, 0..) |*item, item_index| {
        const source_count_for_item = session.scratch_source_counts[item_index];
        if (!item.active or source_count_for_item == 0) continue;
        const interval = session.scratch_effective_intervals[item_index];
        const due_at = alignment.dueAt(
            item.last_sampled_milliseconds,
            item.scheduled_due_milliseconds,
            item.retry_at_milliseconds,
            interval,
            header.command_at_milliseconds,
        );
        if (due_at > header.command_at_milliseconds) continue;
        due[candidate_count] = .{
            .struct_size = @sizeOf(protocol.DueItemOutput),
            .flags = 0,
            .item_handle = item.item_handle,
            .effective_interval_milliseconds = interval,
            .last_sampled_milliseconds = item.last_sampled_milliseconds,
            .retry_at_milliseconds = item.retry_at_milliseconds,
            .plan_epoch = header.plan_epoch,
            .source_count = source_count_for_item,
            .reserved_u32 = 0,
            .scheduled_due_milliseconds = due_at,
        };
        candidate_count += 1;
    }

    std.sort.pdq(protocol.DueItemOutput, due[0..candidate_count], {}, dueLessThan);
    // Every item remains independently scheduled. Returning all items that reached
    // the same boundary lets the provider observe them at the same physical time;
    // publication still settles each item's own current-value slot.
    const due_count: usize = candidate_count;
    for (due[0..due_count]) |selected| {
        const item_index = session.findItem(selected.item_handle) orelse unreachable;
        session.items[item_index].last_planned_epoch = header.plan_epoch;
    }
    std.sort.pdq(protocol.SourceViewOutput, sources[0..source_count], {}, sourceLessThan);
    std.sort.pdq(protocol.ExpiredSourceOutput, expired[0..expired_count], {}, expiredLessThan);

    session.last_plan_epoch = header.plan_epoch;
    session.next_wake_milliseconds = session.nextWakeFromAggregates(
        header.command_at_milliseconds,
    );
    session.commitOperation(header.operation_epoch, header.command_at_milliseconds);

    var flags: u64 = 0;
    if (source_count != 0) flags |= protocol.PlanFlags.active;
    if (due_count != 0) flags |= protocol.PlanFlags.due_items;
    if (session.next_wake_milliseconds != 0) flags |= protocol.PlanFlags.next_wake_valid;
    if (expired_count != 0) flags |= protocol.PlanFlags.expired_sources;
    header.due_item_count = @intCast(due_count);
    header.source_view_count = @intCast(source_count);
    header.expired_source_count = @intCast(expired_count);
    header.active_source_count = @intCast(source_count);
    header.active_item_count = active_item_count;
    header.next_wake_milliseconds = session.next_wake_milliseconds;
    header.state_revision = session.state_revision;
    header.flags = flags;
    return .ok;
}

pub fn sourceOutput(
    session: *const state.Session,
    source: *const state.SourceSlot,
    source_index: u32,
) protocol.SourceViewOutput {
    return .{
        .struct_size = @sizeOf(protocol.SourceViewOutput),
        .flags = sourceFlags(source),
        .source_handle = source.source_handle,
        .last_seen_milliseconds = source.last_seen_milliseconds,
        .observed_interval_milliseconds = source.observed_interval_milliseconds,
        .effective_interval_milliseconds = session.effectiveSourceInterval(source),
        .explicit_interval_milliseconds = source.explicit_interval_milliseconds,
        .item_count = session.sourceItemCount(source_index),
        .reserved_u32 = 0,
        .reserved = 0,
    };
}

fn sourceFlags(source: *const state.SourceSlot) u32 {
    var flags: u32 = 0;
    if (source.persistent) flags |= protocol.SourceFlags.persistent;
    if (source.explicit_interval) flags |= protocol.SourceFlags.explicit_interval;
    return flags;
}

fn requireOutput(comptime T: type, pointer: ?[*]T, capacity: u32) ?[]T {
    if (capacity == 0) return null;
    return (pointer orelse return null)[0..capacity];
}

fn dueLessThan(_: void, left: protocol.DueItemOutput, right: protocol.DueItemOutput) bool {
    if (left.scheduled_due_milliseconds != right.scheduled_due_milliseconds) {
        return left.scheduled_due_milliseconds < right.scheduled_due_milliseconds;
    }
    return left.item_handle < right.item_handle;
}

fn sourceLessThan(_: void, left: protocol.SourceViewOutput, right: protocol.SourceViewOutput) bool {
    return left.source_handle < right.source_handle;
}

fn expiredLessThan(_: void, left: protocol.ExpiredSourceOutput, right: protocol.ExpiredSourceOutput) bool {
    return left.source_handle < right.source_handle;
}
