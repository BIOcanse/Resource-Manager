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
    trigger_pointer: ?[*]protocol.TriggerOutput,
    trigger_capacity: u32,
) ResultCode {
    state.lockMutex(&session.mutex);
    defer session.mutex.unlock();

    if (!session.scratchReady()) return .invalid_argument;

    if (!protocol.validPlanHeader(header, &session.config)) return .abi_mismatch;
    if (due_capacity != header.due_item_capacity or
        source_capacity != header.source_view_capacity or
        expired_capacity != header.expired_source_capacity or
        trigger_capacity != header.trigger_capacity)
    {
        return .invalid_argument;
    }
    if (due_capacity < session.knownItemCount() or
        source_capacity < session.activeSourceCount() or
        expired_capacity < session.activeSourceCount() or
        trigger_capacity < session.membershipCount())
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
    const triggers = requireOutput(protocol.TriggerOutput, trigger_pointer, trigger_capacity) orelse
        return .invalid_argument;
    // Build exact replacements while failure can still leave all native state
    // and the previous issued authorization unchanged.
    var surviving_memberships: usize = 0;
    for (session.memberships) |membership| {
        if (membership.active and !sourceExpired(session, &session.sources[membership.source_index], header.command_at_milliseconds))
            surviving_memberships += 1;
    }
    var membership_candidate: ?[]state.MembershipSlot = null;
    defer if (membership_candidate) |candidate| session.allocator.free(candidate);
    if (surviving_memberships != session.memberships.len) {
        const candidate = session.allocator.alloc(state.MembershipSlot, surviving_memberships) catch return .out_of_memory;
        membership_candidate = candidate;
        var index: usize = 0;
        for (session.memberships) |membership| {
            if (!membership.active or sourceExpired(session, &session.sources[membership.source_index], header.command_at_milliseconds)) continue;
            candidate[index] = membership;
            index += 1;
        }
    }

    @memset(session.scratch_trigger_due_milliseconds, std.math.maxInt(u64));
    for (session.memberships) |membership| {
        if (!membership.active or
            membership.sampling_flags & protocol.SourceFlags.periodic_triggers == 0 or
            membership.next_trigger_milliseconds == 0 or
            membership.next_trigger_milliseconds > header.command_at_milliseconds)
        {
            continue;
        }
        const source = &session.sources[membership.source_index];
        const item = &session.items[membership.item_index];
        if (!source.active or sourceExpired(session, source, header.command_at_milliseconds) or !item.active) continue;
        const item_index: usize = @intCast(membership.item_index);
        session.scratch_trigger_due_milliseconds[item_index] = @min(
            session.scratch_trigger_due_milliseconds[item_index],
            membership.next_trigger_milliseconds,
        );
    }

    var required_triggers: usize = 0;
    for (session.memberships) |membership| {
        if (!membership.active or membership.sampling_flags & protocol.SourceFlags.periodic_triggers == 0 or
            membership.next_trigger_milliseconds == 0 or membership.next_trigger_milliseconds > header.command_at_milliseconds) continue;
        const source = &session.sources[membership.source_index];
        const item = &session.items[membership.item_index];
        if (!source.active or sourceExpired(session, source, header.command_at_milliseconds) or !item.active) continue;
        if (membership.next_trigger_milliseconds == session.scratch_trigger_due_milliseconds[membership.item_index]) required_triggers += 1;
    }
    var issued_candidate: ?[]protocol.TriggerOutput = null;
    defer if (issued_candidate) |candidate| session.allocator.free(candidate);
    if (required_triggers != session.scratch_issued_triggers.len)
        issued_candidate = session.allocator.alloc(protocol.TriggerOutput, required_triggers) catch return .out_of_memory;

    @memset(due, std.mem.zeroes(protocol.DueItemOutput));
    @memset(sources, std.mem.zeroes(protocol.SourceViewOutput));
    @memset(expired, std.mem.zeroes(protocol.ExpiredSourceOutput));
    @memset(triggers, std.mem.zeroes(protocol.TriggerOutput));

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
    if (membership_candidate) |candidate| {
        session.replaceMemberships(candidate, "source-expiry");
        membership_candidate = null;
    } else if (any_expired) session.clearInactiveSourceMemberships();

    const active_item_count = session.rebuildItemAggregates();
    session.refreshItemDeadlines(header.command_at_milliseconds);
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
        const has_sampling_demand = session.scratch_sampling_counts[item_index] != 0;
        const interval = if (has_sampling_demand)
            session.scratch_effective_intervals[item_index]
        else
            0;
        const sample_due_at = if (has_sampling_demand)
            alignment.dueAt(
                item.last_sampled_milliseconds,
                item.scheduled_due_milliseconds,
                item.retry_at_milliseconds,
                interval,
                header.command_at_milliseconds,
            )
        else
            std.math.maxInt(u64);
        const trigger_due_at = session.scratch_trigger_due_milliseconds[item_index];
        const fresh_due_at = if (trigger_due_at != std.math.maxInt(u64) and hasFreshTriggerDue(
            session,
            @intCast(item_index),
            trigger_due_at,
        ))
            trigger_due_at
        else
            std.math.maxInt(u64);
        const due_at = @min(sample_due_at, fresh_due_at);
        if (due_at == std.math.maxInt(u64) or due_at > header.command_at_milliseconds) continue;
        const effective_interval = if (interval != 0)
            interval
        else
            freshIntervalForDue(session, @intCast(item_index), fresh_due_at);
        due[candidate_count] = .{
            .struct_size = @sizeOf(protocol.DueItemOutput),
            .flags = 0,
            .item_handle = item.item_handle,
            .effective_interval_milliseconds = effective_interval,
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
    // A fresh trigger makes its item due. The chosen trigger group is the
    // earliest source-item boundary; later overdue periods remain for the next
    // plan and are not collapsed into this query.
    const due_count: usize = candidate_count;
    for (due[0..due_count]) |selected| {
        const item_index = session.findItem(selected.item_handle) orelse unreachable;
        session.items[item_index].last_planned_epoch = header.plan_epoch;
        if (session.items[item_index].scheduled_due_milliseconds == 0)
            session.items[item_index].scheduled_due_milliseconds = selected.scheduled_due_milliseconds;
    }

    var trigger_count: usize = 0;
    for (session.memberships) |*membership| {
        if (!membership.active or
            membership.sampling_flags & protocol.SourceFlags.periodic_triggers == 0 or
            membership.next_trigger_milliseconds == 0 or
            membership.next_trigger_milliseconds > header.command_at_milliseconds)
        {
            continue;
        }
        const source = &session.sources[membership.source_index];
        const item = &session.items[membership.item_index];
        if (!source.active or !item.active) continue;
        const item_index: usize = @intCast(membership.item_index);
        if (membership.next_trigger_milliseconds != session.scratch_trigger_due_milliseconds[item_index]) {
            continue;
        }
        triggers[trigger_count] = .{
            .struct_size = @sizeOf(protocol.TriggerOutput),
            .flags = if (membership.sampling_flags & protocol.SourceFlags.fresh_on_trigger != 0)
                protocol.TriggerFlags.fresh
            else
                0,
            .source_handle = source.source_handle,
            .item_handle = item.item_handle,
            .scheduled_due_milliseconds = membership.next_trigger_milliseconds,
            .sequence = membership.trigger_sequence,
            .plan_epoch = header.plan_epoch,
        };
        membership.last_planned_epoch = header.plan_epoch;
        item.last_planned_epoch = header.plan_epoch;
        trigger_count += 1;
    }

    std.sort.pdq(protocol.TriggerOutput, triggers[0..trigger_count], {}, triggerLessThan);
    std.sort.pdq(protocol.DueItemOutput, due[0..due_count], {}, dueLessThan);
    std.sort.pdq(protocol.SourceViewOutput, sources[0..source_count], {}, sourceLessThan);
    std.sort.pdq(protocol.ExpiredSourceOutput, expired[0..expired_count], {}, expiredLessThan);

    std.debug.assert(trigger_count == required_triggers);
    if (issued_candidate) |candidate| {
        session.replaceIssuedTriggers(candidate, "plan-issued");
        issued_candidate = null;
    }
    @memcpy(session.scratch_issued_triggers, triggers[0..trigger_count]);
    session.issued_trigger_count = @intCast(trigger_count);
    session.issued_trigger_plan_epoch = header.plan_epoch;
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
    if (trigger_count != 0) flags |= protocol.PlanFlags.triggers;
    header.due_item_count = @intCast(due_count);
    header.source_view_count = @intCast(source_count);
    header.expired_source_count = @intCast(expired_count);
    header.trigger_count = @intCast(trigger_count);
    header.active_source_count = @intCast(source_count);
    header.active_item_count = active_item_count;
    header.next_wake_milliseconds = session.next_wake_milliseconds;
    header.state_revision = session.state_revision;
    header.flags = flags;
    return .ok;
}

fn sourceExpired(session: *const state.Session, source: *const state.SourceSlot, now: u64) bool {
    return source.active and !source.persistent and now > source.last_seen_milliseconds and
        now - source.last_seen_milliseconds > session.config.active_ttl_milliseconds;
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
    return flags | source.sampling_flags;
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

fn triggerLessThan(_: void, left: protocol.TriggerOutput, right: protocol.TriggerOutput) bool {
    if (left.scheduled_due_milliseconds != right.scheduled_due_milliseconds) {
        return left.scheduled_due_milliseconds < right.scheduled_due_milliseconds;
    }
    if (left.item_handle != right.item_handle) return left.item_handle < right.item_handle;
    if (left.source_handle != right.source_handle) return left.source_handle < right.source_handle;
    return left.sequence < right.sequence;
}

fn hasFreshTriggerDue(session: *const state.Session, item_index: u32, due_at: u64) bool {
    for (session.memberships) |membership| {
        if (membership.active and membership.item_index == item_index and
            membership.next_trigger_milliseconds == due_at and
            membership.sampling_flags & protocol.SourceFlags.fresh_on_trigger != 0)
        {
            return true;
        }
    }
    return false;
}

fn freshIntervalForDue(session: *const state.Session, item_index: u32, due_at: u64) u64 {
    var interval: u64 = std.math.maxInt(u64);
    for (session.memberships) |membership| {
        if (membership.active and membership.item_index == item_index and
            membership.next_trigger_milliseconds == due_at and
            membership.sampling_flags & protocol.SourceFlags.fresh_on_trigger != 0)
        {
            interval = @min(interval, membership.period_milliseconds);
        }
    }
    return if (interval == std.math.maxInt(u64)) 0 else interval;
}
