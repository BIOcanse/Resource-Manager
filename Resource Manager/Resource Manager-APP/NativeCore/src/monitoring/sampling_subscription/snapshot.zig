const std = @import("std");
const protocol = @import("protocol.zig");
const planner = @import("planner.zig");
const alignment = @import("alignment.zig");
const state = @import("state.zig");
const ResultCode = @import("../../common/result_codes.zig").ResultCode;

pub fn snapshot(
    session: *state.Session,
    header: *protocol.SnapshotHeader,
    source_pointer: ?[*]protocol.SourceStateOutput,
    source_capacity: u32,
    item_pointer: ?[*]protocol.ItemStateOutput,
    item_capacity: u32,
) ResultCode {
    state.lockMutex(&session.mutex);
    defer session.mutex.unlock();

    if (!protocol.validEmptySnapshotHeader(header)) return .abi_mismatch;
    if (source_capacity < session.config.maximum_source_count or
        item_capacity < session.config.maximum_item_count)
    {
        return .buffer_too_small;
    }
    const sources = requireOutput(protocol.SourceStateOutput, source_pointer, source_capacity) orelse
        return .invalid_argument;
    const items = requireOutput(protocol.ItemStateOutput, item_pointer, item_capacity) orelse
        return .invalid_argument;
    @memset(sources, std.mem.zeroes(protocol.SourceStateOutput));
    @memset(items, std.mem.zeroes(protocol.ItemStateOutput));

    const active_item_count = session.rebuildItemAggregates();
    _ = active_item_count;
    var source_count: usize = 0;
    var persistent_source_count: u32 = 0;
    var explicit_interval_source_count: u32 = 0;
    for (session.sources, 0..) |source, source_index| {
        if (!source.active) continue;
        sources[source_count] = planner.sourceOutput(session, &source, @intCast(source_index));
        source_count += 1;
        if (source.persistent) persistent_source_count += 1;
        if (source.explicit_interval) explicit_interval_source_count += 1;
    }

    var item_count: usize = 0;
    for (session.items, 0..) |item, item_index| {
        if (!item.active) continue;
        const source_count_for_item = session.scratch_source_counts[item_index];
        const effective_interval = if (source_count_for_item == 0)
            0
        else
            session.scratch_effective_intervals[item_index];
        items[item_count] = .{
            .struct_size = @sizeOf(protocol.ItemStateOutput),
            .flags = 0,
            .item_handle = item.item_handle,
            .last_sampled_milliseconds = item.last_sampled_milliseconds,
            .retry_at_milliseconds = item.retry_at_milliseconds,
            .effective_interval_milliseconds = effective_interval,
            .last_planned_epoch = item.last_planned_epoch,
            .source_count = source_count_for_item,
            .reserved_u32 = 0,
            .scheduled_due_milliseconds = if (source_count_for_item == 0)
                0
            else
                alignment.dueAt(
                    item.last_sampled_milliseconds,
                    item.scheduled_due_milliseconds,
                    item.retry_at_milliseconds,
                    effective_interval,
                    session.last_command_at_milliseconds,
                ),
        };
        item_count += 1;
    }

    std.sort.pdq(protocol.SourceStateOutput, sources[0..source_count], {}, sourceLessThan);
    std.sort.pdq(protocol.ItemStateOutput, items[0..item_count], {}, itemLessThan);

    var flags: u64 = 0;
    if (source_count != 0) flags |= protocol.SnapshotFlags.active;
    if (session.next_wake_milliseconds != 0) flags |= protocol.SnapshotFlags.next_wake_valid;
    header.* = .{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.SnapshotHeader),
        .configuration_generation = session.config.generation,
        .state_revision = session.state_revision,
        .last_operation_epoch = session.last_operation_epoch,
        .last_command_at_milliseconds = session.last_command_at_milliseconds,
        .last_plan_epoch = session.last_plan_epoch,
        .next_wake_milliseconds = session.next_wake_milliseconds,
        .active_source_count = @intCast(source_count),
        .known_item_count = @intCast(item_count),
        .membership_count = session.membershipCount(),
        .persistent_source_count = persistent_source_count,
        .explicit_interval_source_count = explicit_interval_source_count,
        .source_output_count = @intCast(source_count),
        .item_output_count = @intCast(item_count),
        .reserved_u32 = 0,
        .flags = flags,
        .reserved = .{ 0, 0 },
    };
    return .ok;
}

fn requireOutput(comptime T: type, pointer: ?[*]T, capacity: u32) ?[]T {
    if (capacity == 0) return null;
    return (pointer orelse return null)[0..capacity];
}

fn sourceLessThan(_: void, left: protocol.SourceStateOutput, right: protocol.SourceStateOutput) bool {
    return left.source_handle < right.source_handle;
}

fn itemLessThan(_: void, left: protocol.ItemStateOutput, right: protocol.ItemStateOutput) bool {
    return left.item_handle < right.item_handle;
}
