const protocol = @import("protocol.zig");
const layout = @import("layout.zig");
const state = @import("state.zig");
const activity = @import("activity.zig");
const operation_sequence = @import("operation_sequence.zig");

pub fn capacityCandidate(
    view: layout.View,
    resource_slot_index: u32,
    phase: protocol.CapacityPhase,
) ?protocol.Intent {
    const resource = &view.resources[resource_slot_index];
    const table = &view.tables[resource.table_slot_index];
    if (!state.isOccupied(resource) or resource.protected_use != 0 or
        resource.pending_slot_index != layout.no_slot or
        resource.recoverability != .recoverable or resource.access_loss_impact == .fatal)
    {
        return null;
    }
    const capability = state.resolveBinding(
        view,
        resource.table_slot_index,
        table.capacity_capability,
    ) catch return null;
    if (capability.expected_effects & protocol.effect_releases_ledger_slot == 0 or
        capability.destructive == 0)
    {
        return null;
    }
    return makeIntent(
        view,
        resource_slot_index,
        capability,
        protocol.intent_reason_capacity,
        phase,
    );
}

pub fn modeCandidate(
    view: layout.View,
    resource_slot_index: u32,
    mode: protocol.CleanupMode,
) ?protocol.Intent {
    const resource = &view.resources[resource_slot_index];
    const table = &view.tables[resource.table_slot_index];
    if (!state.isOccupied(resource) or resource.protected_use != 0 or
        resource.pending_slot_index != layout.no_slot)
    {
        return null;
    }
    const capability = blk: {
        if (mode == .release_all and !protocol.isEmptyBinding(table.capacity_capability)) {
            const preferred = state.resolveBinding(
                view,
                resource.table_slot_index,
                table.capacity_capability,
            ) catch return null;
            if (eligibleForResource(preferred, resource)) break :blk preferred;
        }

        const fallback = state.resolveBinding(
            view,
            resource.table_slot_index,
            table.mode_capability,
        ) catch return null;
        if (!eligibleForResource(fallback, resource)) return null;
        break :blk fallback;
    };
    return makeIntent(
        view,
        resource_slot_index,
        capability,
        protocol.intent_reason_mode,
        .none,
    );
}

pub fn partitionCandidate(
    view: layout.View,
    resource_slot_index: u32,
    victim: protocol.PartitionHandle,
) ?protocol.Intent {
    const resource = &view.resources[resource_slot_index];
    const table = &view.tables[resource.table_slot_index];
    if (!state.isOccupied(resource) or resource.protected_use != 0 or
        resource.pending_slot_index != layout.no_slot or
        resource.recoverability != .recoverable or resource.access_loss_impact == .fatal)
    {
        return null;
    }
    const capability = state.resolveBinding(
        view,
        resource.table_slot_index,
        table.capacity_capability,
    ) catch return null;
    if (capability.expected_effects & protocol.effect_releases_ledger_slot == 0 or
        capability.destructive == 0)
    {
        return null;
    }
    var intent = makeIntent(
        view,
        resource_slot_index,
        capability,
        protocol.intent_reason_partition,
        .none,
    );
    intent.reclaim_partition_slot_generation = victim.partition_slot_generation;
    intent.reclaim_partition_slot_index = victim.partition_slot_index;
    return intent;
}

fn eligibleForResource(
    capability: *const layout.CapabilitySlot,
    resource: *const layout.ResourceSlot,
) bool {
    return capability.destructive == 0 or
        (resource.recoverability == .recoverable and resource.access_loss_impact != .fatal);
}

pub fn validIntentShape(intent: protocol.Intent) bool {
    return intent.abi_version == protocol.abi_version and
        intent.struct_size == @sizeOf(protocol.Intent) and intent.operation_id != 0 and
        intent.operation_generation != 0 and intent.snapshot_generation != 0 and
        intent.table_revision != 0 and intent.row_revision != 0 and
        intent.activity_revision != 0 and intent.use_generation != 0 and
        intent.capability_id != 0 and intent.capability_generation != 0 and
        intent.action_code != 0 and intent.reason_flags != 0 and
        intent.reason_flags & ~protocol.known_intent_reason_mask == 0 and
        intent.expected_effects != 0 and intent.expected_effects & ~protocol.supported_effect_mask == 0 and
        intent.destructive <= 1 and
        @intFromEnum(intent.capacity_phase) <= @intFromEnum(protocol.CapacityPhase.smooth_emergency) and
        @intFromEnum(intent.access_loss_impact) <= @intFromEnum(protocol.AccessLossImpact.unobservable_now) and
        allZero(intent.reserved0[0..]) and
        intent.reserved1 == 0 and
        validIntentReasonShape(intent);
}

fn validIntentReasonShape(intent: protocol.Intent) bool {
    if (intent.reason_flags == protocol.intent_reason_partition) {
        return intent.expected_effects & protocol.effect_releases_ledger_slot != 0 and
            intent.destructive == 1 and intent.capacity_phase == .none and
            intent.reclaim_partition_slot_generation != 0 and
            intent.reclaim_partition_slot_index != layout.no_slot;
    }
    return intent.reclaim_partition_slot_generation == 0 and
        intent.reclaim_partition_slot_index == layout.no_slot and
        (intent.reason_flags & protocol.intent_reason_capacity == 0 or
            (intent.expected_effects & protocol.effect_releases_ledger_slot != 0 and
                intent.capacity_phase != .none));
}

fn makeIntent(
    view: layout.View,
    resource_slot_index: u32,
    capability: *const layout.CapabilitySlot,
    reason_flags: u8,
    phase: protocol.CapacityPhase,
) protocol.Intent {
    const resource = &view.resources[resource_slot_index];
    const table = &view.tables[resource.table_slot_index];
    const operation = operation_sequence.currentToken(view);
    return .{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.Intent),
        .resource = state.resourceHandle(view, resource_slot_index),
        .operation_id = operation.operation_id,
        .operation_generation = operation.operation_generation,
        .snapshot_generation = view.header.snapshot_generation,
        .table_revision = table.revision,
        .row_revision = resource.row_revision,
        .activity_revision = resource.activity_revision,
        .use_generation = resource.use_generation,
        .capability_id = capability.capability_id,
        .capability_generation = capability.generation,
        .size_bytes = resource.size_bytes,
        .settled_activity = activity.settledValue(resource, view.header),
        .recovery_cost_coefficient = resource.recovery_cost_coefficient,
        .action_code = capability.action_code,
        .capability_slot_index = @intCast((@intFromPtr(capability) - @intFromPtr(view.capabilities.ptr)) /
            @sizeOf(layout.CapabilitySlot)),
        .reason_flags = reason_flags,
        .expected_effects = capability.expected_effects,
        .destructive = capability.destructive,
        .capacity_phase = phase,
        .access_loss_impact = resource.access_loss_impact,
        .reserved0 = .{ 0, 0, 0 },
        .reclaim_partition_slot_generation = 0,
        .reclaim_partition_slot_index = layout.no_slot,
        .reserved1 = 0,
    };
}

fn allZero(values: []const u8) bool {
    for (values) |value| if (value != 0) return false;
    return true;
}
