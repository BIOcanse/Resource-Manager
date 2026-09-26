const protocol = @import("protocol.zig");
const layout = @import("layout.zig");
const state = @import("state.zig");
const planning = @import("planning.zig");
const rank = @import("rank.zig");
const operation_sequence = @import("operation_sequence.zig");

pub fn plan(
    view: layout.View,
    input: protocol.ModePlanInput,
    output: []protocol.Intent,
) !protocol.PlanSummary {
    _ = try operation_sequence.requireCurrentImplicit(view);
    if (view.header.active_operation_phase != .planning and
        view.header.active_operation_phase != .executing)
    {
        return if (view.header.active_operation_phase == .recovery_required)
            error.RecoveryRequired
        else
            error.InvalidOperationPhase;
    }
    if (!protocol.validModePlanInput(input)) return error.InvalidArgument;
    const table = try state.resolveTable(view, input.table);
    if (input.mode == .unrestricted or input.mode == .normal) {
        return emptySummary(view);
    }

    const maximum: usize = @intCast(input.maximum_intents);
    if (output.len < maximum) return error.BufferTooSmall;
    const selected = output[0..maximum];
    var count: usize = 0;
    var resource_index = table.first_resource_slot;
    while (resource_index != layout.no_slot) {
        const resource = &view.resources[resource_index];
        const next_resource = resource.next_table_resource;
        const candidate = planning.modeCandidate(view, resource_index, input.mode);
        if (candidate) |value| {
            retainModeCandidate(selected, &count, value);
        }
        resource_index = next_resource;
    }
    rank.sortMode(selected[0..count]);
    const operation = operation_sequence.currentToken(view);
    return .{
        .operation_id = operation.operation_id,
        .operation_generation = operation.operation_generation,
        .snapshot_generation = view.header.snapshot_generation,
        .intent_count = @intCast(count),
        .affected_table_count = if (count == 0) 0 else 1,
        .triggered_table_count = 0,
        .stalled_table_count = 0,
        .emergency_table_count = 0,
        .reserved0 = 0,
    };
}

fn retainModeCandidate(
    selected: []protocol.Intent,
    count: *usize,
    candidate: protocol.Intent,
) void {
    if (count.* < selected.len) {
        selected[count.*] = candidate;
        siftWorstUp(selected[0 .. count.* + 1], count.*);
        count.* += 1;
        return;
    }
    if (!rank.modeLess(candidate, selected[0])) return;
    selected[0] = candidate;
    siftWorstDown(selected);
}

fn siftWorstUp(values: []protocol.Intent, start_index: usize) void {
    var index = start_index;
    while (index != 0) {
        const parent = (index - 1) / 2;
        if (!modeWorse(values[index], values[parent])) return;
        const temporary = values[parent];
        values[parent] = values[index];
        values[index] = temporary;
        index = parent;
    }
}

fn siftWorstDown(values: []protocol.Intent) void {
    var index: usize = 0;
    while (true) {
        const left = index * 2 + 1;
        if (left >= values.len) return;
        const right = left + 1;
        var worst = left;
        if (right < values.len and modeWorse(values[right], values[left])) worst = right;
        if (!modeWorse(values[worst], values[index])) return;
        const temporary = values[index];
        values[index] = values[worst];
        values[worst] = temporary;
        index = worst;
    }
}

fn modeWorse(left: protocol.Intent, right: protocol.Intent) bool {
    return rank.modeLess(right, left);
}

fn emptySummary(view: layout.View) protocol.PlanSummary {
    const operation = operation_sequence.currentToken(view);
    return .{
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
}
