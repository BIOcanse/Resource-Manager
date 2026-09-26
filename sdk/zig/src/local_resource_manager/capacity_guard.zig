const protocol = @import("protocol.zig");
const layout = @import("layout.zig");
const state = @import("state.zig");
const planning = @import("planning.zig");
const rank = @import("rank.zig");
const operation_sequence = @import("operation_sequence.zig");

pub fn plan(
    view: layout.View,
    input: protocol.CapacityPlanInput,
    scratch: []protocol.Intent,
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
    return planDetailed(view, input, scratch, output, null);
}

pub fn planDetailed(
    view: layout.View,
    input: protocol.CapacityPlanInput,
    scratch: []protocol.Intent,
    output: []protocol.Intent,
    table_output: ?[]protocol.CapacityTablePlanView,
) !protocol.PlanSummary {
    if (!protocol.validCapacityPlanInput(input)) return error.InvalidArgument;
    if (scratch.len < view.resources.len) return error.BufferTooSmall;
    const requirements = planRequirements(view, input);
    if (output.len < requirements.intent_count) return error.BufferTooSmall;
    if (table_output) |values| {
        if (values.len < requirements.table_count) return error.BufferTooSmall;
    }

    var output_count: usize = 0;
    var affected_tables: u32 = 0;
    var triggered_tables: u32 = 0;
    var stalled_tables: u32 = 0;
    var emergency_tables: u32 = 0;
    var table_index: usize = 0;
    while (table_index < view.tables.len) : (table_index += 1) {
        const table = &view.tables[table_index];
        if (!state.isOccupied(table)) continue;
        const capacity = guardCapacity(table);
        const free = guardFree(table);
        const triggered = isTriggered(view, table, free, input.strategy);
        if (!triggered) continue;
        const table_output_index: usize = @intCast(triggered_tables);
        triggered_tables += 1;
        var table_flags = protocol.capacity_table_flag_triggered;
        const emergency = input.strategy == .smooth and ratioLessOrEqual(
            free,
            capacity,
            view.header.config.smooth_emergency_free_percent,
        );
        if (emergency) {
            emergency_tables += 1;
            table_flags |= protocol.capacity_table_flag_emergency;
        }
        const phase_and_limit = classify(view, table, free, input.strategy) orelse {
            try writeTablePlan(view, table_index, table_output, table_output_index, table_flags);
            continue;
        };

        var candidate_count: usize = 0;
        var resource_index = table.first_resource_slot;
        while (resource_index != layout.no_slot) {
            const resource = &view.resources[resource_index];
            const next_resource = resource.next_table_resource;
            const candidate = planning.capacityCandidate(
                view,
                resource_index,
                phase_and_limit.phase,
            );
            if (candidate) |value| {
                scratch[candidate_count] = value;
                candidate_count += 1;
            }
            resource_index = next_resource;
        }
        if (candidate_count == 0) {
            stalled_tables += 1;
            table_flags |= protocol.capacity_table_flag_stalled;
            try writeTablePlan(view, table_index, table_output, table_output_index, table_flags);
            continue;
        }
        rank.sortCapacity(scratch[0..candidate_count]);
        const selected_count = @min(candidate_count, phase_and_limit.maximum);
        if (output_count + selected_count > output.len) return error.BufferTooSmall;
        @memcpy(output[output_count .. output_count + selected_count], scratch[0..selected_count]);
        output_count += selected_count;
        affected_tables += 1;
        table_flags |= protocol.capacity_table_flag_affected;
        try writeTablePlan(view, table_index, table_output, table_output_index, table_flags);
    }
    rank.sortCapacityCanonical(output[0..output_count]);
    const operation = operation_sequence.currentToken(view);
    return .{
        .operation_id = operation.operation_id,
        .operation_generation = operation.operation_generation,
        .snapshot_generation = view.header.snapshot_generation,
        .intent_count = @intCast(output_count),
        .affected_table_count = affected_tables,
        .triggered_table_count = triggered_tables,
        .stalled_table_count = stalled_tables,
        .emergency_table_count = emergency_tables,
        .reserved0 = 0,
    };
}

const PlanRequirements = struct {
    intent_count: usize,
    table_count: usize,
};

fn planRequirements(
    view: layout.View,
    input: protocol.CapacityPlanInput,
) PlanRequirements {
    var intent_count: usize = 0;
    var table_count: usize = 0;
    var table_index: usize = 0;
    while (table_index < view.tables.len) : (table_index += 1) {
        const table = &view.tables[table_index];
        if (!state.isOccupied(table)) continue;
        const free = guardFree(table);
        if (!isTriggered(view, table, free, input.strategy)) continue;
        table_count += 1;
        const phase_and_limit = classify(view, table, free, input.strategy) orelse continue;

        var candidate_count: usize = 0;
        var resource_index = table.first_resource_slot;
        while (resource_index != layout.no_slot) {
            const resource = &view.resources[resource_index];
            const next_resource = resource.next_table_resource;
            if (planning.capacityCandidate(view, resource_index, phase_and_limit.phase) != null) {
                candidate_count += 1;
            }
            resource_index = next_resource;
        }
        intent_count += @min(candidate_count, phase_and_limit.maximum);
    }
    return .{ .intent_count = intent_count, .table_count = table_count };
}

fn isTriggered(
    view: layout.View,
    table: *const layout.TableSlot,
    free: u32,
    strategy: protocol.CapacityStrategy,
) bool {
    return switch (strategy) {
        .concentrated => ratioLess(
            free,
            guardCapacity(table),
            view.header.config.concentrated_trigger_free_percent,
        ),
        .smooth => ratioLess(
            free,
            guardCapacity(table),
            view.header.config.smooth_trigger_free_percent,
        ),
    };
}

fn writeTablePlan(
    view: layout.View,
    table_index: usize,
    table_output: ?[]protocol.CapacityTablePlanView,
    output_index: usize,
    flags: u32,
) !void {
    const values = table_output orelse return;
    if (output_index >= values.len) return error.BufferTooSmall;
    const handle = state.tableHandle(view, @intCast(table_index));
    values[output_index] = .{
        .capacity = try state.readTableCapacity(view, handle),
        .flags = flags,
        .reserved0 = 0,
    };
}

const PhaseAndLimit = struct {
    phase: protocol.CapacityPhase,
    maximum: usize,
};

fn classify(
    view: layout.View,
    table: *const layout.TableSlot,
    free: u32,
    strategy: protocol.CapacityStrategy,
) ?PhaseAndLimit {
    return switch (strategy) {
        .concentrated => blk: {
            if (!ratioLess(
                free,
                guardCapacity(table),
                view.header.config.concentrated_trigger_free_percent,
            )) break :blk null;
            const target_free = ceilPercent(
                guardCapacity(table),
                view.header.config.concentrated_target_free_percent,
            );
            break :blk .{
                .phase = .concentrated_recovery,
                .maximum = if (target_free > free) view.resources.len else 0,
            };
        },
        .smooth => blk: {
            if (!ratioLess(
                free,
                guardCapacity(table),
                view.header.config.smooth_trigger_free_percent,
            )) break :blk null;
            if (ratioLessOrEqual(
                free,
                guardCapacity(table),
                view.header.config.smooth_emergency_free_percent,
            )) {
                break :blk .{ .phase = .smooth_emergency, .maximum = view.resources.len };
            }
            const paced_ready = table.has_paced_epoch == 0 or
                view.header.activity_epoch - table.last_paced_epoch >=
                    view.header.config.smooth_release_interval_epochs;
            if (!paced_ready) break :blk null;
            break :blk .{
                .phase = .smooth_paced,
                .maximum = @intCast(view.header.config.smooth_maximum_releases_per_interval),
            };
        },
    };
}

fn guardCapacity(table: *const layout.TableSlot) u32 {
    const direct_capacity = table.capacity - table.partition_reservation_capacity;
    return if (direct_capacity == 0) table.capacity else direct_capacity;
}

fn guardFree(table: *const layout.TableSlot) u32 {
    return if (table.partition_reservation_capacity == table.capacity)
        table.capacity - table.occupied_count
    else
        guardCapacity(table) - table.direct_occupied_count;
}

pub fn ratioLess(free: u32, capacity: u32, percent: u32) bool {
    return @as(u64, free) * 100 < @as(u64, capacity) * percent;
}

pub fn ratioLessOrEqual(free: u32, capacity: u32, percent: u32) bool {
    return @as(u64, free) * 100 <= @as(u64, capacity) * percent;
}

pub fn ceilPercent(capacity: u32, percent: u32) usize {
    return @intCast((@as(u64, capacity) * percent + 99) / 100);
}
