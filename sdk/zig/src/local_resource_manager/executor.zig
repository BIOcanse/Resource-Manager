const std = @import("std");
const protocol = @import("protocol.zig");
const layout = @import("layout.zig");
const state = @import("state.zig");
const activity = @import("activity.zig");
const capacity_guard = @import("capacity_guard.zig");
const planning = @import("planning.zig");
const partition = @import("partition.zig");
const operation_sequence = @import("operation_sequence.zig");

pub fn beginIntent(view: layout.View, intent: protocol.Intent) !protocol.ExecutionToken {
    if (!planning.validIntentShape(intent)) return error.InvalidArgument;
    if (!operation_sequence.intentBelongsToCurrent(view, intent)) return error.StaleOperation;
    const operation = operation_sequence.currentToken(view);
    try operation_sequence.enterPlanning(view, operation);
    const resource = try state.resolveResource(view, intent.resource);
    const table = &view.tables[resource.table_slot_index];
    if (resource.pending_slot_index != layout.no_slot or resource.protected_use != 0 or
        table.active_pending_count != 0)
    {
        return error.ResourceBusy;
    }
    try validateCurrentIntent(view, table, resource, intent);
    if (intent.expected_effects & protocol.effect_releases_ledger_slot != 0 and
        table.revision == std.math.maxInt(u64))
    {
        return error.GenerationExhausted;
    }
    if (view.header.pending_free_head == layout.no_slot) return error.PendingFull;
    if (view.header.snapshot_generation == std.math.maxInt(u64) or
        view.header.next_pending_generation == std.math.maxInt(u64) or
        view.header.next_attempt_id == std.math.maxInt(u64))
    {
        return error.GenerationExhausted;
    }
    try operation_sequence.enterExecution(view, operation);

    const pending_index = view.header.pending_free_head;
    const pending = &view.pending[pending_index];
    view.header.pending_free_head = pending.next_free;
    const pending_generation = view.header.next_pending_generation;
    const attempt_id = view.header.next_attempt_id;
    view.header.next_pending_generation += 1;
    view.header.next_attempt_id += 1;
    pending.* = .{
        .intent = intent,
        .generation = pending_generation,
        .attempt_id = attempt_id,
        .next_free = layout.no_slot,
        .flags = layout.occupied_flag,
        .state = .reserved,
        .reserved0 = .{ 0, 0, 0, 0, 0, 0, 0 },
    };
    resource.pending_slot_index = pending_index;
    resource.pending_generation = pending_generation;
    table.active_pending_count += 1;
    if (intent.capacity_phase == .smooth_paced) {
        if (table.has_paced_epoch == 0 or
            table.paced_snapshot_generation != intent.snapshot_generation)
        {
            table.last_paced_epoch = view.header.activity_epoch;
            table.paced_snapshot_generation = intent.snapshot_generation;
            table.has_paced_epoch = 1;
        }
    }
    view.header.pending_count += 1;
    view.header.snapshot_generation += 1;
    return .{
        .resource = intent.resource,
        .operation_id = intent.operation_id,
        .operation_generation = intent.operation_generation,
        .pending_slot_generation = pending_generation,
        .row_revision = intent.row_revision,
        .activity_revision = intent.activity_revision,
        .use_generation = intent.use_generation,
        .capability_generation = intent.capability_generation,
        .attempt_id = attempt_id,
        .pending_slot_index = pending_index,
        .reserved0 = 0,
    };
}

pub fn markEffectStarted(
    view: layout.View,
    token: protocol.ExecutionToken,
) !protocol.CommitReceipt {
    const resolved = try resolveExecution(view, token);
    if (resolved.pending.state != .reserved) return error.StaleIntent;
    if (view.header.active_operation_phase == .recovery_required) {
        return error.RecoveryRequired;
    }
    if (view.header.snapshot_generation == std.math.maxInt(u64)) {
        return error.GenerationExhausted;
    }
    resolved.pending.state = .effect_started;
    view.header.snapshot_generation += 1;
    return receipt(view, resolved.table, false, false);
}

pub fn commitEffect(
    view: layout.View,
    token: protocol.ExecutionToken,
    effect: protocol.TypedEffect,
) !protocol.CommitReceipt {
    if (!protocol.validTypedEffect(effect)) return error.InvalidArgument;
    const resolved = try resolveExecution(view, token);
    if (view.header.snapshot_generation == std.math.maxInt(u64)) return error.GenerationExhausted;

    if (effect.outcome == .effect_unknown) {
        if (resolved.pending.state == .effect_uncertain) {
            return receipt(view, resolved.table, false, true);
        }
        if (resolved.pending.state != .effect_started) return error.InvalidOperationPhase;
        resolved.pending.state = .effect_uncertain;
        try operation_sequence.markRecoveryRequired(view);
        view.header.snapshot_generation += 1;
        return receipt(view, resolved.table, false, true);
    }

    if (resolved.pending.state == .reserved) return error.InvalidOperationPhase;
    if (resolved.pending.state == .effect_started) {
        if (resolved.resource.row_revision != token.row_revision or
            resolved.resource.activity_revision != token.activity_revision or
            resolved.resource.use_generation != token.use_generation)
        {
            resolved.pending.state = .effect_uncertain;
            try operation_sequence.markRecoveryRequired(view);
            view.header.snapshot_generation += 1;
            return error.StaleIntent;
        }
    }

    if (effect.outcome != .applied) {
        clearPending(view, resolved);
        operation_sequence.noteSettledExecution(view, false);
        view.header.snapshot_generation += 1;
        return receipt(view, resolved.table, false, false);
    }
    if (effect.changes & ~resolved.pending.intent.expected_effects != 0 or
        effect.changes & protocol.effect_changes_tier != 0 or
        (resolved.pending.intent.reason_flags &
            (protocol.intent_reason_capacity | protocol.intent_reason_partition) != 0 and
            effect.changes & protocol.effect_releases_ledger_slot == 0))
    {
        return error.EffectMismatch;
    }

    const releases_slot = effect.changes & protocol.effect_releases_ledger_slot != 0;
    const changes_bytes = effect.changes & protocol.effect_changes_size_bytes != 0;
    if (releases_slot and resolved.table.revision == std.math.maxInt(u64)) {
        return error.GenerationExhausted;
    }
    if (!releases_slot and changes_bytes and resolved.resource.row_revision == std.math.maxInt(u64)) {
        return error.GenerationExhausted;
    }

    const resource_slot_index = token.resource.resource_slot_index;
    clearPending(view, resolved);
    if (releases_slot) {
        try state.freeResource(view, resource_slot_index);
    } else if (changes_bytes) {
        resolved.resource.size_bytes = effect.size_bytes_after;
        resolved.resource.row_revision += 1;
    }
    operation_sequence.noteSettledExecution(view, true);
    view.header.snapshot_generation += 1;
    return receipt(view, resolved.table, releases_slot, false);
}

pub fn abortIntent(view: layout.View, token: protocol.ExecutionToken) !protocol.CommitReceipt {
    const resolved = try resolveExecution(view, token);
    if (resolved.pending.state != .reserved) return error.StaleIntent;
    if (view.header.snapshot_generation == std.math.maxInt(u64)) return error.GenerationExhausted;
    clearPending(view, resolved);
    operation_sequence.noteSettledExecution(view, false);
    view.header.snapshot_generation += 1;
    return receipt(view, resolved.table, false, false);
}

pub fn markEffectUncertain(
    view: layout.View,
    token: protocol.ExecutionToken,
) !protocol.CommitReceipt {
    return commitEffect(view, token, .{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.TypedEffect),
        .size_bytes_after = 0,
        .released_bytes = 0,
        .outcome = .effect_unknown,
        .changes = 0,
        .reserved0 = .{ 0, 0, 0, 0, 0, 0 },
    });
}

const ResolvedExecution = struct {
    pending: *layout.PendingSlot,
    resource: *layout.ResourceSlot,
    table: *layout.TableSlot,
};

fn resolveExecution(view: layout.View, token: protocol.ExecutionToken) !ResolvedExecution {
    if (token.reserved0 != 0 or token.pending_slot_index >= view.pending.len or
        token.operation_id == 0 or token.operation_generation == 0 or
        token.pending_slot_generation == 0 or token.attempt_id == 0)
    {
        return error.StaleIntent;
    }
    const pending = &view.pending[token.pending_slot_index];
    if (!state.isOccupied(pending) or pending.generation != token.pending_slot_generation or
        pending.attempt_id != token.attempt_id or
        (pending.state != .reserved and pending.state != .effect_started and
            pending.state != .effect_uncertain))
    {
        return error.StaleIntent;
    }
    if (token.operation_id != view.header.active_operation_id or
        token.operation_generation != view.header.active_operation_generation or
        pending.intent.operation_id != token.operation_id or
        pending.intent.operation_generation != token.operation_generation)
    {
        return error.StaleOperation;
    }
    const resource = state.resolveResource(view, token.resource) catch return error.StaleIntent;
    if (resource.pending_slot_index != token.pending_slot_index or
        resource.pending_generation != token.pending_slot_generation or
        token.row_revision != pending.intent.row_revision or
        token.activity_revision != pending.intent.activity_revision or
        token.use_generation != pending.intent.use_generation or
        token.capability_generation != pending.intent.capability_generation)
    {
        return error.StaleIntent;
    }
    return .{
        .pending = pending,
        .resource = resource,
        .table = &view.tables[resource.table_slot_index],
    };
}

fn validateCurrentIntent(
    view: layout.View,
    table: *layout.TableSlot,
    resource: *layout.ResourceSlot,
    intent: protocol.Intent,
) !void {
    const binding = if (intent.reason_flags &
        (protocol.intent_reason_capacity | protocol.intent_reason_partition) != 0)
        table.capacity_capability
    else if (matchesBinding(table.mode_capability, intent))
        table.mode_capability
    else
        table.capacity_capability;
    if (!matchesBinding(binding, intent)) return error.StaleCapability;
    const capability = state.resolveBinding(view, resource.table_slot_index, binding) catch
        return error.StaleCapability;
    if (capability.action_code != intent.action_code or
        capability.expected_effects != intent.expected_effects or
        capability.destructive != intent.destructive)
    {
        return error.StaleCapability;
    }
    if (resource.row_revision != intent.row_revision or
        resource.activity_revision != intent.activity_revision or
        resource.use_generation != intent.use_generation or
        resource.size_bytes != intent.size_bytes or
        resource.recovery_cost_coefficient != intent.recovery_cost_coefficient or
        resource.access_loss_impact != intent.access_loss_impact or
        activity.settledValue(resource, view.header) != intent.settled_activity)
    {
        return error.StaleIntent;
    }
    if (capability.destructive != 0 and
        (resource.recoverability != .recoverable or resource.access_loss_impact == .fatal))
    {
        return error.StaleIntent;
    }

    if (intent.reason_flags == protocol.intent_reason_partition) {
        const reclaim_handle = protocol.PartitionHandle{
            .manager_instance_id = intent.resource.manager_instance_id,
            .table_slot_generation = intent.resource.table_slot_generation,
            .partition_slot_generation = intent.reclaim_partition_slot_generation,
            .table_id = intent.resource.table_id,
            .table_incarnation = intent.resource.table_incarnation,
            .table_slot_index = intent.resource.table_slot_index,
            .partition_slot_index = intent.reclaim_partition_slot_index,
        };
        if (capability.expected_effects & protocol.effect_releases_ledger_slot == 0 or
            !partition.resourceBelongsToPartition(view, resource, reclaim_handle))
        {
            return error.StaleIntent;
        }
    } else if (intent.reason_flags & protocol.intent_reason_capacity != 0) {
        if (resource.recoverability != .recoverable or resource.access_loss_impact == .fatal or
            capability.expected_effects & protocol.effect_releases_ledger_slot == 0)
        {
            return error.StaleIntent;
        }
        const free = table.capacity - table.occupied_count;
        switch (intent.capacity_phase) {
            .concentrated_recovery => if (!capacity_guard.ratioLess(
                free,
                table.capacity,
                view.header.config.concentrated_target_free_percent,
            )) {
                return error.StaleIntent;
            },
            .smooth_paced => {
                if (!capacity_guard.ratioLess(
                    free,
                    table.capacity,
                    view.header.config.smooth_trigger_free_percent,
                ) or capacity_guard.ratioLessOrEqual(
                    free,
                    table.capacity,
                    view.header.config.smooth_emergency_free_percent,
                )) {
                    return error.StaleIntent;
                }
                const same_paced_batch = table.has_paced_epoch != 0 and
                    table.paced_snapshot_generation == intent.snapshot_generation;
                if (!same_paced_batch and table.has_paced_epoch != 0 and
                    view.header.activity_epoch - table.last_paced_epoch <
                        view.header.config.smooth_release_interval_epochs)
                {
                    return error.StaleIntent;
                }
            },
            .smooth_emergency => {},
            .none => return error.StaleIntent,
        }
    } else if (table.revision != intent.table_revision) {
        return error.StaleIntent;
    }
}

fn matchesBinding(binding: protocol.CapabilityBinding, intent: protocol.Intent) bool {
    return !protocol.isEmptyBinding(binding) and binding.capability_id == intent.capability_id and
        binding.generation == intent.capability_generation and
        binding.slot_index == intent.capability_slot_index;
}

fn clearPending(view: layout.View, resolved: ResolvedExecution) void {
    resolved.resource.pending_slot_index = layout.no_slot;
    resolved.resource.pending_generation = 0;
    resolved.table.active_pending_count -= 1;
    const generation = resolved.pending.generation;
    const index: u32 = @intCast((@intFromPtr(resolved.pending) - @intFromPtr(view.pending.ptr)) /
        @sizeOf(layout.PendingSlot));
    resolved.pending.* = std.mem.zeroes(layout.PendingSlot);
    resolved.pending.generation = generation;
    resolved.pending.next_free = view.header.pending_free_head;
    view.header.pending_free_head = index;
    view.header.pending_count -= 1;
}

fn receipt(
    view: layout.View,
    table: *const layout.TableSlot,
    released: bool,
    uncertain: bool,
) protocol.CommitReceipt {
    return .{
        .operation_id = view.header.active_operation_id,
        .operation_generation = view.header.active_operation_generation,
        .snapshot_generation = view.header.snapshot_generation,
        .table_revision = table.revision,
        .resource_slot_released = if (released) 1 else 0,
        .effect_uncertain = if (uncertain) 1 else 0,
        .reserved0 = .{ 0, 0, 0, 0, 0, 0 },
    };
}
