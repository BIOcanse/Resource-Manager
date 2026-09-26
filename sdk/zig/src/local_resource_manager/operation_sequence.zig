const std = @import("std");
const protocol = @import("protocol.zig");
const layout = @import("layout.zig");

pub fn begin(view: layout.View, kind: protocol.OperationKind) !protocol.OperationToken {
    if (view.header.active_operation_phase == .recovery_required) {
        return error.RecoveryRequired;
    }
    if (view.header.active_operation_phase != .inactive or
        view.header.active_operation_id != 0 or
        view.header.active_operation_generation != 0)
    {
        return error.OperationActive;
    }
    if (view.header.pending_count != 0) return error.InvalidState;
    if (view.header.next_operation_id == std.math.maxInt(u64) or
        view.header.next_operation_generation == std.math.maxInt(u64))
    {
        return error.GenerationExhausted;
    }

    const operation_id = view.header.next_operation_id;
    const operation_generation = view.header.next_operation_generation;
    view.header.next_operation_id += 1;
    view.header.next_operation_generation += 1;
    view.header.active_operation_id = operation_id;
    view.header.active_operation_generation = operation_generation;
    view.header.active_operation_start_snapshot_generation =
        view.header.snapshot_generation;
    view.header.active_operation_settled_execution_count = 0;
    view.header.active_operation_confirmed_effect_count = 0;
    view.header.active_operation_kind = kind;
    view.header.active_operation_phase = .planning;
    return currentToken(view);
}

pub fn read(view: layout.View) protocol.OperationView {
    var reserved_count: u32 = 0;
    var started_count: u32 = 0;
    var uncertain_count: u32 = 0;
    for (view.pending) |*pending| {
        if (pending.flags & layout.occupied_flag == 0) continue;
        switch (pending.state) {
            .reserved => reserved_count += 1,
            .effect_started => started_count += 1,
            .effect_uncertain => uncertain_count += 1,
        }
    }
    return .{
        .token = if (view.header.active_operation_phase == .inactive)
            std.mem.zeroes(protocol.OperationToken)
        else
            currentToken(view),
        .current_snapshot_generation = view.header.snapshot_generation,
        .settled_execution_count = view.header.active_operation_settled_execution_count,
        .confirmed_effect_count = view.header.active_operation_confirmed_effect_count,
        .pending_count = view.header.pending_count,
        .reserved_execution_count = reserved_count,
        .started_execution_count = started_count,
        .uncertain_execution_count = uncertain_count,
        .phase = view.header.active_operation_phase,
        .reserved0 = .{ 0, 0, 0 },
    };
}

pub fn requireCurrent(
    view: layout.View,
    token: protocol.OperationToken,
) !void {
    if (!protocol.validOperationTokenShape(token)) return error.StaleOperation;
    if (view.header.active_operation_phase == .inactive) return error.OperationRequired;
    if (token.manager_instance_id != view.header.manager_instance_id or
        token.operation_id != view.header.active_operation_id or
        token.operation_generation != view.header.active_operation_generation or
        token.start_snapshot_generation !=
            view.header.active_operation_start_snapshot_generation or
        token.kind != view.header.active_operation_kind)
    {
        return error.StaleOperation;
    }
}

pub fn requireCurrentImplicit(view: layout.View) !protocol.OperationToken {
    if (view.header.active_operation_phase == .inactive) return error.OperationRequired;
    return currentToken(view);
}

pub fn enterPlanning(view: layout.View, token: protocol.OperationToken) !void {
    try requireCurrent(view, token);
    if (view.header.active_operation_phase != .planning and
        view.header.active_operation_phase != .executing)
    {
        return if (view.header.active_operation_phase == .recovery_required)
            error.RecoveryRequired
        else
            error.InvalidOperationPhase;
    }
}

pub fn enterMutation(view: layout.View, token: protocol.OperationToken) !void {
    try requireCurrent(view, token);
    try enterMutationImplicit(view);
}

pub fn enterMutationImplicit(view: layout.View) !void {
    switch (view.header.active_operation_phase) {
        .planning => view.header.active_operation_phase = .executing,
        .executing, .settling => {},
        .recovery_required => return error.RecoveryRequired,
        .inactive => return error.OperationRequired,
    }
}

pub fn enterExecution(view: layout.View, token: protocol.OperationToken) !void {
    try requireCurrent(view, token);
    switch (view.header.active_operation_phase) {
        .planning => view.header.active_operation_phase = .executing,
        .executing => {},
        .recovery_required => return error.RecoveryRequired,
        else => return error.InvalidOperationPhase,
    }
}

pub fn enterSettlement(view: layout.View, token: protocol.OperationToken) !void {
    try requireSettlementEligible(view, token);
    if (view.header.active_operation_phase != .settling) {
        view.header.active_operation_phase = .settling;
    }
}

pub fn requireSettlementEligible(view: layout.View, token: protocol.OperationToken) !void {
    try requireCurrent(view, token);
    if (view.header.pending_count != 0) return error.ResourceBusy;
    switch (view.header.active_operation_phase) {
        .planning, .executing, .settling => {},
        .recovery_required => return error.RecoveryRequired,
        .inactive => return error.OperationRequired,
    }
}

pub fn markRecoveryRequired(view: layout.View) !void {
    _ = try requireCurrentImplicit(view);
    if (view.header.active_operation_phase == .settling) {
        return error.InvalidOperationPhase;
    }
    view.header.active_operation_phase = .recovery_required;
}

pub fn noteSettledExecution(view: layout.View, confirmed_effect: bool) void {
    view.header.active_operation_settled_execution_count +|= 1;
    if (confirmed_effect) view.header.active_operation_confirmed_effect_count +|= 1;
    if (view.header.active_operation_phase == .recovery_required and
        view.header.pending_count == 0)
    {
        view.header.active_operation_phase = .settling;
    }
}

pub fn finish(view: layout.View, token: protocol.OperationToken) !void {
    try requireCurrent(view, token);
    if (view.header.active_operation_phase == .recovery_required) {
        return error.RecoveryRequired;
    }
    if (view.header.pending_count != 0) return error.ResourceBusy;

    var table_index: usize = 0;
    while (table_index < view.tables.len) : (table_index += 1) {
        const table = &view.tables[table_index];
        if (table.active_partition_operation_id != token.operation_id or
            table.active_partition_operation_generation != token.operation_generation)
        {
            continue;
        }
        table.active_partition_admission_id = 0;
        table.active_partition_admission_hash = 0;
        table.active_partition_operation_id = 0;
        table.active_partition_operation_generation = 0;
    }

    view.header.active_operation_id = 0;
    view.header.active_operation_generation = 0;
    view.header.active_operation_start_snapshot_generation = 0;
    view.header.active_operation_settled_execution_count = 0;
    view.header.active_operation_confirmed_effect_count = 0;
    view.header.active_operation_kind = .maintenance;
    view.header.active_operation_phase = .inactive;
    @memset(view.header.active_operation_reserved0[0..], 0);
}

pub fn currentToken(view: layout.View) protocol.OperationToken {
    return .{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.OperationToken),
        .manager_instance_id = view.header.manager_instance_id,
        .operation_id = view.header.active_operation_id,
        .operation_generation = view.header.active_operation_generation,
        .start_snapshot_generation = view.header.active_operation_start_snapshot_generation,
        .kind = view.header.active_operation_kind,
        .reserved0 = .{ 0, 0, 0, 0, 0, 0, 0 },
    };
}

pub fn intentBelongsToCurrent(view: layout.View, intent: protocol.Intent) bool {
    return view.header.active_operation_phase != .inactive and
        intent.operation_id == view.header.active_operation_id and
        intent.operation_generation == view.header.active_operation_generation;
}
