const std = @import("std");
const protocol = @import("protocol.zig");
const identity = @import("identity.zig");
const planning = @import("planning.zig");
const rank = @import("rank.zig");

pub fn merge(
    capacity_stamp: protocol.PlanStamp,
    mode_stamp: protocol.PlanStamp,
    capacity: []const protocol.Intent,
    mode: []const protocol.Intent,
    output: []protocol.Intent,
) !protocol.PlanSummary {
    const required_count = try validateInputs(
        capacity_stamp,
        mode_stamp,
        capacity,
        mode,
    );
    if (required_count > output.len) return error.BufferTooSmall;
    var count: usize = 0;
    for (capacity) |intent| {
        if (findResourceLinear(output[0..count], intent.resource)) |index| {
            output[index].reason_flags |= intent.reason_flags;
            continue;
        }
        output[count] = intent;
        count += 1;
    }
    rank.sortCanonical(output[0..count]);
    const capacity_count = count;

    for (mode) |intent| {
        if (findSortedResource(output[0..capacity_count], intent.resource)) |index| {
            output[index].reason_flags |= protocol.intent_reason_mode;
            continue;
        }
        if (findResourceLinear(output[capacity_count..count], intent.resource)) |relative_index| {
            output[capacity_count + relative_index].reason_flags |= intent.reason_flags;
            continue;
        }
        output[count] = intent;
        count += 1;
    }
    rank.sortCanonical(output[0..count]);
    std.debug.assert(count == required_count);
    return .{
        .operation_id = capacity_stamp.operation_id,
        .operation_generation = capacity_stamp.operation_generation,
        .snapshot_generation = capacity_stamp.snapshot_generation,
        .intent_count = @intCast(count),
        .affected_table_count = countTables(output[0..count]),
        .triggered_table_count = 0,
        .stalled_table_count = 0,
        .emergency_table_count = 0,
        .reserved0 = 0,
    };
}

fn findResourceLinear(
    values: []const protocol.Intent,
    resource: protocol.ResourceHandle,
) ?usize {
    for (values, 0..) |value, index| {
        if (identity.sameResource(value.resource, resource)) return index;
    }
    return null;
}

fn validateInputs(
    capacity_stamp: protocol.PlanStamp,
    mode_stamp: protocol.PlanStamp,
    capacity: []const protocol.Intent,
    mode: []const protocol.Intent,
) !usize {
    if (!validStamp(capacity_stamp, .capacity) or
        !validStamp(mode_stamp, .mode) or
        capacity_stamp.manager_instance_id != mode_stamp.manager_instance_id or
        capacity_stamp.operation_id != mode_stamp.operation_id or
        capacity_stamp.operation_generation != mode_stamp.operation_generation or
        capacity_stamp.snapshot_generation != mode_stamp.snapshot_generation)
    {
        return error.InvalidArgument;
    }

    var required_count: usize = 0;
    for (capacity, 0..) |intent, index| {
        if (!validOwnedIntent(intent, capacity_stamp) or
            intent.reason_flags != protocol.intent_reason_capacity or
            intent.capacity_phase == .none or
            intent.expected_effects & protocol.effect_releases_ledger_slot == 0)
        {
            return error.InvalidArgument;
        }
        var duplicate = false;
        for (capacity[0..index]) |previous| {
            if (!identity.sameResource(previous.resource, intent.resource)) continue;
            if (!samePhasePayloadExceptReason(previous, intent)) return error.IntentConflict;
            duplicate = true;
        }
        if (!duplicate) required_count += 1;
    }

    for (mode, 0..) |intent, index| {
        if (!validOwnedIntent(intent, mode_stamp) or
            intent.reason_flags != protocol.intent_reason_mode or
            intent.capacity_phase != .none)
        {
            return error.InvalidArgument;
        }
        var duplicate = false;
        for (mode[0..index]) |previous| {
            if (!identity.sameResource(previous.resource, intent.resource)) continue;
            if (!samePhasePayloadExceptReason(previous, intent)) return error.IntentConflict;
            duplicate = true;
        }
        for (capacity) |capacity_intent| {
            if (identity.sameResource(capacity_intent.resource, intent.resource)) {
                if (!sameResourceSnapshot(capacity_intent, intent)) return error.IntentConflict;
                duplicate = true;
                break;
            }
        }
        if (!duplicate) required_count += 1;
    }
    return required_count;
}

fn validStamp(stamp: protocol.PlanStamp, expected_kind: protocol.PlanKind) bool {
    if (stamp.abi_version != protocol.abi_version or
        stamp.struct_size != @sizeOf(protocol.PlanStamp) or
        stamp.manager_instance_id == 0 or
        stamp.operation_id == 0 or stamp.operation_generation == 0 or
        stamp.snapshot_generation == 0 or
        stamp.kind != @intFromEnum(expected_kind))
    {
        return false;
    }
    for (stamp.reserved0) |value| {
        if (value != 0) return false;
    }
    return true;
}

fn validOwnedIntent(
    intent: protocol.Intent,
    stamp: protocol.PlanStamp,
) bool {
    return planning.validIntentShape(intent) and
        intent.resource.manager_instance_id == stamp.manager_instance_id and
        intent.operation_id == stamp.operation_id and
        intent.operation_generation == stamp.operation_generation and
        intent.snapshot_generation == stamp.snapshot_generation;
}

fn findSortedResource(
    values: []const protocol.Intent,
    resource: protocol.ResourceHandle,
) ?usize {
    var low: usize = 0;
    var high = values.len;
    while (low < high) {
        const middle = low + (high - low) / 2;
        switch (identity.compareResource(values[middle].resource, resource)) {
            .lt => low = middle + 1,
            .gt => high = middle,
            .eq => return middle,
        }
    }
    return null;
}

fn samePhasePayloadExceptReason(left: protocol.Intent, right: protocol.Intent) bool {
    var normalized_left = left;
    var normalized_right = right;
    normalized_left.reason_flags = 0;
    normalized_right.reason_flags = 0;
    return std.meta.eql(normalized_left, normalized_right);
}

fn sameResourceSnapshot(left: protocol.Intent, right: protocol.Intent) bool {
    return left.snapshot_generation == right.snapshot_generation and
        left.table_revision == right.table_revision and
        left.row_revision == right.row_revision and
        left.activity_revision == right.activity_revision and
        left.use_generation == right.use_generation and
        left.size_bytes == right.size_bytes and
        left.settled_activity == right.settled_activity and
        left.recovery_cost_coefficient == right.recovery_cost_coefficient and
        left.access_loss_impact == right.access_loss_impact;
}

fn countTables(values: []const protocol.Intent) u32 {
    var count: u32 = 0;
    var capacity_end: usize = 0;
    while (capacity_end < values.len and
        values[capacity_end].reason_flags & protocol.intent_reason_capacity != 0)
    {
        if (capacity_end == 0 or
            !identity.sameTable(values[capacity_end - 1].resource, values[capacity_end].resource))
        {
            count += 1;
        }
        capacity_end += 1;
    }
    for (values[capacity_end..], 0..) |value, relative_index| {
        if (relative_index != 0 and
            identity.sameTable(values[capacity_end + relative_index - 1].resource, value.resource))
        {
            continue;
        }
        if (!containsTable(values[0..capacity_end], value.resource)) count += 1;
    }
    return count;
}

fn containsTable(
    values: []const protocol.Intent,
    resource: protocol.ResourceHandle,
) bool {
    var low: usize = 0;
    var high = values.len;
    while (low < high) {
        const middle = low + (high - low) / 2;
        const table_order = identity.compareTable(values[middle].resource, resource);
        switch (table_order) {
            .lt => low = middle + 1,
            .gt => high = middle,
            .eq => return true,
        }
    }
    return false;
}
