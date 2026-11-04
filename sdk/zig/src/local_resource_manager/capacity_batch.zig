const std = @import("std");
const protocol = @import("protocol.zig");
const layout = @import("layout.zig");
const state = @import("state.zig");
const planning = @import("planning.zig");
const rank = @import("rank.zig");
const guard = @import("capacity_guard.zig");
const operation_sequence = @import("operation_sequence.zig");

pub fn plan(
    view: layout.View,
    input: protocol.CapacityPlanInput,
    maximum_intents: u32,
    cursor: *protocol.CapacityPlanCursor,
    scratch: []protocol.Intent,
    output: []protocol.Intent,
) !protocol.PlanSummary {
    const operation = try operation_sequence.requireCurrentImplicit(view);
    try operation_sequence.enterPlanning(view, operation);
    if (!protocol.validCapacityPlanInput(input) or maximum_intents == 0) return error.InvalidArgument;
    try validateCursor(view, input.strategy, cursor.*);
    if (view.header.pending_count != 0) return error.ResourceBusy;
    if (scratch.len < maximum_intents or output.len < maximum_intents) return error.BufferTooSmall;

    var next = cursor.*;
    next.operation = operation;
    next.activity_epoch = view.header.activity_epoch;
    next.strategy = input.strategy;
    var summary = protocol.PlanSummary{
        .operation_id = operation.operation_id,
        .operation_generation = operation.operation_generation,
        .snapshot_generation = view.header.snapshot_generation,
        .intent_count = 0,
        .affected_table_count = 0,
        .triggered_table_count = 0,
        .stalled_table_count = 0,
        .emergency_table_count = 0,
        .reserved0 = 0,
    };
    if (next.state == .completed) return summary;

    var table_index: usize = if (next.state == .active) next.last_intent.resource.table_slot_index else 0;
    if (next.state == .active and next.remaining_in_table == 0) table_index += 1;
    while (table_index < view.tables.len and summary.intent_count < maximum_intents) {
        const table = &view.tables[table_index];
        if (!state.isOccupied(table)) {
            table_index += 1;
            continue;
        }
        const continuing = next.state == .active and next.remaining_in_table != 0 and
            table_index == next.last_intent.resource.table_slot_index;
        const phase_and_limit = if (continuing)
            continuingPhase(view, table, next)
        else
            guard.classify(view, table, guard.guardFree(table), input.strategy);
        const phase = phase_and_limit orelse {
            table_index += 1;
            continue;
        };
        summary.triggered_table_count += 1;
        if (phase.phase == .smooth_emergency) summary.emergency_table_count += 1;
        const available: usize = maximum_intents - summary.intent_count;
        const selected = scratch[0..@min(available, phase.maximum)];
        var count: usize = 0;
        var eligible_count: usize = 0;
        var resource_index = table.first_resource_slot;
        while (resource_index != layout.no_slot) {
            const resource = &view.resources[resource_index];
            const following = resource.next_table_resource;
            if (planning.capacityCandidate(view, resource_index, phase.phase)) |original| {
                var candidate = original;
                // The original paced snapshot is the executor's existing batch witness.
                if (continuing) candidate.snapshot_generation = next.last_intent.snapshot_generation;
                if (!continuing or rank.capacityLess(next.last_intent, candidate)) {
                    eligible_count += 1;
                    rank.retainCapacity(selected, &count, candidate);
                }
            }
            resource_index = following;
        }
        if (count == 0) {
            summary.stalled_table_count += 1;
            next.remaining_in_table = 0;
            table_index += 1;
            continue;
        }
        rank.sortCapacity(selected[0..count]);
        const target_prefix: TargetPrefix = if (phase.phase == .concentrated_recovery)
            concentratedPrefix(view, table, selected[0..count])
        else
            .{ .count = count, .reaches_target = false };
        const delivered = target_prefix.count;
        const start: usize = summary.intent_count;
        @memcpy(output[start .. start + delivered], selected[0..delivered]);
        summary.intent_count += @intCast(delivered);
        summary.affected_table_count += 1;
        next.last_intent = selected[delivered - 1];
        next.remaining_in_table = @intCast(phase.maximum - delivered);
        next.state = .active;
        if (eligible_count == delivered or next.remaining_in_table == 0) {
            next.remaining_in_table = 0;
            table_index += 1;
        }
        // The next call observes real outcomes. A projected goal must not latch
        // completion when a Handler may reject or fail to release one of these rows.
        if (target_prefix.reaches_target) break;
    }
    if (table_index == view.tables.len) {
        next.remaining_in_table = 0;
        next.state = .completed;
    }
    cursor.* = next;
    return summary;
}

const TargetPrefix = struct { count: usize, reaches_target: bool };

fn concentratedPrefix(view: layout.View, table: *const layout.TableSlot, candidates: []const protocol.Intent) TargetPrefix {
    var free = guard.guardFree(table);
    const target = guard.ceilPercent(guard.guardCapacity(table), view.header.config.concentrated_target_free_percent);
    for (candidates, 0..) |candidate, index| {
        const row = &view.resources[candidate.resource.resource_slot_index];
        if (table.partition_reservation_capacity == table.capacity or
            !state.isPartitionReservedCell(&view.ledger_cells[row.ledger_cell_index])) free += 1;
        if (free >= target) return .{ .count = index + 1, .reaches_target = true };
    }
    return .{ .count = candidates.len, .reaches_target = false };
}

fn continuingPhase(
    view: layout.View,
    table: *const layout.TableSlot,
    cursor: protocol.CapacityPlanCursor,
) ?guard.PhaseAndLimit {
    if (cursor.last_intent.capacity_phase == .concentrated_recovery and !guard.ratioLess(
        guard.guardFree(table),
        guard.guardCapacity(table),
        view.header.config.concentrated_target_free_percent,
    )) return null;
    if (cursor.last_intent.capacity_phase == .smooth_paced and !guard.ratioLess(
        guard.guardFree(table),
        guard.guardCapacity(table),
        view.header.config.smooth_trigger_free_percent,
    )) return null;
    return .{ .phase = cursor.last_intent.capacity_phase, .maximum = cursor.remaining_in_table };
}

fn validateCursor(view: layout.View, strategy: protocol.CapacityStrategy, cursor: protocol.CapacityPlanCursor) !void {
    if (cursor.abi_version != protocol.abi_version or cursor.struct_size != @sizeOf(protocol.CapacityPlanCursor) or
        @intFromEnum(cursor.state) > @intFromEnum(protocol.CapacityCursorState.completed) or
        cursor.reserved0[0] != 0 or cursor.reserved0[1] != 0) return error.InvalidArgument;
    if (cursor.state == .initial) {
        if (!allZero(std.mem.asBytes(&cursor.operation)) or !allZero(std.mem.asBytes(&cursor.last_intent)) or
            cursor.activity_epoch != 0 or cursor.remaining_in_table != 0 or cursor.strategy != .concentrated) return error.InvalidArgument;
        return;
    }
    try operation_sequence.requireCurrent(view, cursor.operation);
    if (cursor.activity_epoch != view.header.activity_epoch) return error.StaleIntent;
    if (cursor.strategy != strategy) return error.InvalidArgument;
    if (cursor.state == .completed and cursor.remaining_in_table != 0) return error.InvalidArgument;
    if (cursor.state == .completed and allZero(std.mem.asBytes(&cursor.last_intent))) return;
    const anchor = cursor.last_intent;
    if (!planning.validIntentShape(anchor) or !operation_sequence.intentBelongsToCurrent(view, anchor) or
        anchor.reason_flags != protocol.intent_reason_capacity or
        anchor.snapshot_generation < cursor.operation.start_snapshot_generation or
        anchor.snapshot_generation > view.header.snapshot_generation or
        (strategy == .concentrated and anchor.capacity_phase != .concentrated_recovery) or
        (strategy == .smooth and anchor.capacity_phase != .smooth_paced and anchor.capacity_phase != .smooth_emergency))
        return error.InvalidArgument;
    _ = try state.resolveTable(view, .{
        .manager_instance_id = anchor.resource.manager_instance_id,
        .slot_generation = anchor.resource.table_slot_generation,
        .table_id = anchor.resource.table_id,
        .table_incarnation = anchor.resource.table_incarnation,
        .slot_index = anchor.resource.table_slot_index,
        .reserved0 = 0,
    });
    const maximum = if (anchor.capacity_phase == .smooth_paced)
        view.header.config.smooth_maximum_releases_per_interval
    else
        view.header.config.resource_capacity;
    if (cursor.remaining_in_table > maximum) return error.InvalidArgument;
}

fn allZero(bytes: []const u8) bool {
    for (bytes) |value| if (value != 0) return false;
    return true;
}
