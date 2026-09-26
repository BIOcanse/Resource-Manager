const std = @import("std");
const state = @import("state.zig");
const resource_ops = @import("resource_ops.zig");
const types = state.types;

pub fn filter(
    context: *state.Context,
    resource: types.ResourceRef,
    requested_actions: u8,
    allowed_actions: *u8,
) !void {
    if (requested_actions & ~types.all_action_bits != 0) return error.InvalidArgument;
    if (!context.writable) return filterReadOnly(context, resource, requested_actions, allowed_actions);
    var guard = try context.acquire();
    defer guard.deinit();
    if (guard.abandoned or context.consistencyState() != .stable) return error.InconsistentState;
    if (!context.resourceMatches(resource)) return error.StaleReference;
    allowed_actions.* = resource_ops.allowedActions(context, resource.resource_slot, requested_actions);
}

fn filterReadOnly(
    context: *const state.Context,
    resource: types.ResourceRef,
    requested_actions: u8,
    allowed_actions: *u8,
) !void {
    var attempt: u8 = 0;
    while (attempt < 8) : (attempt += 1) {
        const sequence = try context.beginStableRead();
        const matches = context.resourceMatches(resource);
        const allowed = if (matches)
            resource_ops.allowedActions(context, resource.resource_slot, requested_actions)
        else
            0;
        if (!context.stableReadFinished(sequence)) continue;
        if (!matches) return error.StaleReference;
        allowed_actions.* = allowed;
        return;
    }
    return error.SynchronizationFailed;
}

pub fn beginDestructiveExpected(
    context: *state.Context,
    request: *const types.DestructiveExpectedRequest,
    output: *types.DestructiveToken,
) !void {
    if (!types.validDestructiveExpectedRequest(request)) return error.InvalidArgument;
    var guard = try context.acquire();
    defer guard.deinit();
    if (guard.abandoned or context.consistencyState() != .stable) return error.InconsistentState;
    if (!context.resourceMatches(request.resource)) return error.StaleReference;
    const slot = request.resource.resource_slot;
    if (context.column(u64, context.layout.resources.scheduling_revision)[slot] !=
        request.scheduling_revision)
    {
        return error.StaleReference;
    }
    if (resource_ops.allowedActions(context, slot, request.action_mask) != request.action_mask) {
        return error.ResourceBusy;
    }
    if (context.hardProtectionCount(slot) != 0 or context.destructiveActive(slot)) {
        return error.ResourceBusy;
    }
    _ = types.nextGeneration(request.scheduling_revision) orelse
        return error.CapacityExhausted;
    const next_gate_epoch = try nextGateEpoch(context, slot);
    _ = types.nextGeneration(next_gate_epoch) orelse
        return error.CapacityExhausted;
    const next_topology_generation = try context.nextTopologyGeneration();
    _ = types.nextGeneration(next_topology_generation) orelse
        return error.CapacityExhausted;

    var mutation = try context.beginMutation();
    defer mutation.finish();
    context.column(u8, context.layout.resources.destructive_active)[slot] = 1;
    context.column(u8, context.layout.resources.destructive_action)[slot] =
        request.action_mask;
    context.column(u64, context.layout.resources.destructive_attempt_id_low)[slot] =
        request.action_attempt_id_low;
    context.column(u64, context.layout.resources.destructive_attempt_id_high)[slot] =
        request.action_attempt_id_high;
    commitGateEpoch(context, slot, next_gate_epoch);
    context.header.topology_generation =
        next_topology_generation;
    output.* = .{
        .resource = request.resource,
        .scheduling_revision = request.scheduling_revision,
        .gate_epoch = context.column(u64, context.layout.resources.gate_epoch)[slot],
        .action_attempt_id_low = request.action_attempt_id_low,
        .action_attempt_id_high = request.action_attempt_id_high,
        .action_mask = request.action_mask,
        .reserved0 = [_]u8{0} ** 7,
    };
}

pub fn commitDestructive(
    context: *state.Context,
    token: *const types.DestructiveToken,
    post_state: *const types.ResourcePublication,
) !void {
    if (!types.validPublication(post_state)) return error.InvalidArgument;
    var guard = try context.acquire();
    defer guard.deinit();
    if (guard.abandoned or context.consistencyState() != .stable) return error.InconsistentState;
    const slot = try validateToken(context, token);
    try validatePostState(context, slot, token.action_mask, post_state);
    const next_revision = types.nextGeneration(token.scheduling_revision) orelse
        return error.CapacityExhausted;
    const next_gate_epoch = try nextGateEpoch(context, slot);
    const next_topology_generation =
        try context.nextTopologyGeneration();

    var mutation = try context.beginMutation();
    defer mutation.finish();
    resource_ops.writePublication(
        context,
        slot,
        post_state,
        token.resource.public_resource_id,
    );
    context.column(u64, context.layout.resources.scheduling_revision)[slot] =
        next_revision;
    clearDestructive(context, slot);
    commitGateEpoch(context, slot, next_gate_epoch);
    context.header.topology_generation =
        next_topology_generation;
}

pub fn abortDestructive(
    context: *state.Context,
    token: *const types.DestructiveToken,
    receipt: *const types.DestructiveNoEffectReceipt,
) !void {
    if (!types.validDestructiveNoEffectReceipt(receipt) or
        receipt.action_attempt_id_low != token.action_attempt_id_low or
        receipt.action_attempt_id_high != token.action_attempt_id_high)
    {
        return error.InvalidArgument;
    }
    var guard = try context.acquire();
    defer guard.deinit();
    if (guard.abandoned or context.consistencyState() != .stable) return error.InconsistentState;
    const slot = try validateToken(context, token);
    const next_gate_epoch = try nextGateEpoch(context, slot);
    const next_topology_generation =
        try context.nextTopologyGeneration();

    var mutation = try context.beginMutation();
    defer mutation.finish();
    clearDestructive(context, slot);
    commitGateEpoch(context, slot, next_gate_epoch);
    context.header.topology_generation =
        next_topology_generation;
}

pub fn beginRecallExpected(
    context: *state.Context,
    request: *const types.RecallExpectedRequest,
    output: *types.RecallToken,
) !void {
    if (!types.validRecallExpectedRequest(request)) return error.InvalidArgument;
    var guard = try context.acquire();
    defer guard.deinit();
    if (guard.abandoned or context.consistencyState() != .stable) return error.InconsistentState;
    if (!context.resourceMatches(request.resource)) return error.StaleReference;
    const slot = request.resource.resource_slot;
    if (context.column(u64, context.layout.resources.scheduling_revision)[slot] !=
        request.scheduling_revision or
        context.column(u64, context.layout.resources.gate_epoch)[slot] != request.gate_epoch or
        context.column(u64, context.layout.resources.payload_generation)[slot] !=
            request.payload_generation or
        context.column(u32, context.layout.resources.active_subscriber_count)[slot] !=
            request.subscriber_count or
        context.column(u8, context.layout.resources.activity_score)[slot] != request.activity_score or
        context.column(u8, context.layout.resources.flags)[slot] != request.flags)
    {
        return error.StaleReference;
    }
    if (context.destructiveActive(slot) or
        context.hardProtectionCount(slot) != 0 or
        context.column(u8, context.layout.resources.availability)[slot] !=
            @intFromEnum(types.Availability.unavailable) or
        context.column(u64, context.layout.resources.size_bytes)[slot] != 0 or
        context.column(u64, context.layout.resources.payload_mapping_id)[slot] != 0)
    {
        return error.ResourceBusy;
    }

    _ = types.nextGeneration(request.payload_generation) orelse
        return error.CapacityExhausted;
    const next_revision = types.nextGeneration(request.scheduling_revision) orelse
        return error.CapacityExhausted;
    const next_gate_epoch = try nextGateEpoch(context, slot);
    _ = types.nextGeneration(next_gate_epoch) orelse
        return error.CapacityExhausted;
    const next_topology_generation = try context.nextTopologyGeneration();
    _ = types.nextGeneration(next_topology_generation) orelse
        return error.CapacityExhausted;

    var mutation = try context.beginMutation();
    defer mutation.finish();
    context.column(u8, context.layout.resources.destructive_active)[slot] = 1;
    context.column(u8, context.layout.resources.destructive_action)[slot] = 0;
    context.column(u64, context.layout.resources.destructive_attempt_id_low)[slot] =
        request.action_attempt_id_low;
    context.column(u64, context.layout.resources.destructive_attempt_id_high)[slot] =
        request.action_attempt_id_high;
    context.column(u8, context.layout.resources.availability)[slot] =
        @intFromEnum(types.Availability.preparing);
    context.column(u64, context.layout.resources.scheduling_revision)[slot] = next_revision;
    commitGateEpoch(context, slot, next_gate_epoch);
    context.header.topology_generation = next_topology_generation;
    output.* = .{
        .resource = request.resource,
        .scheduling_revision = next_revision,
        .gate_epoch = next_gate_epoch,
        .action_attempt_id_low = request.action_attempt_id_low,
        .action_attempt_id_high = request.action_attempt_id_high,
        .payload_generation = request.payload_generation,
    };
}

pub fn commitRecall(
    context: *state.Context,
    token: *const types.RecallToken,
    post_state: *const types.ResourcePublication,
) !void {
    if (!types.validPublication(post_state)) return error.InvalidArgument;
    var guard = try context.acquire();
    defer guard.deinit();
    if (guard.abandoned or context.consistencyState() != .stable) return error.InconsistentState;
    const slot = try validateRecallToken(context, token);
    try validateRecallPostState(context, slot, token, post_state);
    if (context.hardProtectionCount(slot) != 0) {
        return error.ResourceBusy;
    }
    const next_revision = types.nextGeneration(token.scheduling_revision) orelse
        return error.CapacityExhausted;
    const next_gate_epoch = try nextGateEpoch(context, slot);
    const next_topology_generation = try context.nextTopologyGeneration();

    var mutation = try context.beginMutation();
    defer mutation.finish();
    resource_ops.writePublication(
        context,
        slot,
        post_state,
        token.resource.public_resource_id,
    );
    context.column(u64, context.layout.resources.scheduling_revision)[slot] = next_revision;
    clearDestructive(context, slot);
    commitGateEpoch(context, slot, next_gate_epoch);
    context.header.topology_generation = next_topology_generation;
}

pub fn abortRecall(
    context: *state.Context,
    token: *const types.RecallToken,
    receipt: *const types.DestructiveNoEffectReceipt,
) !void {
    if (!types.validDestructiveNoEffectReceipt(receipt) or
        receipt.action_attempt_id_low != token.action_attempt_id_low or
        receipt.action_attempt_id_high != token.action_attempt_id_high)
    {
        return error.InvalidArgument;
    }
    var guard = try context.acquire();
    defer guard.deinit();
    if (guard.abandoned or context.consistencyState() != .stable) return error.InconsistentState;
    const slot = try validateRecallToken(context, token);
    const next_revision = types.nextGeneration(token.scheduling_revision) orelse
        return error.CapacityExhausted;
    const next_gate_epoch = try nextGateEpoch(context, slot);
    const next_topology_generation = try context.nextTopologyGeneration();

    var mutation = try context.beginMutation();
    defer mutation.finish();
    context.column(u8, context.layout.resources.availability)[slot] =
        @intFromEnum(types.Availability.unavailable);
    context.column(u64, context.layout.resources.scheduling_revision)[slot] = next_revision;
    clearDestructive(context, slot);
    commitGateEpoch(context, slot, next_gate_epoch);
    context.header.topology_generation = next_topology_generation;
}

fn validateToken(
    context: *const state.Context,
    token: *const types.DestructiveToken,
) !u32 {
    if (token.scheduling_revision == 0 or
        token.gate_epoch == 0 or
        (token.action_attempt_id_low == 0 and token.action_attempt_id_high == 0) or
        token.action_mask == 0 or
        token.action_mask & ~types.destructive_action_bits != 0 or
        token.action_mask & (token.action_mask - 1) != 0 or
        !std.mem.allEqual(u8, token.reserved0[0..], 0))
    {
        return error.InvalidArgument;
    }
    if (!context.resourceMatches(token.resource)) return error.StaleReference;
    const slot = token.resource.resource_slot;
    if (!context.destructiveActive(slot) or
        context.column(u64, context.layout.resources.scheduling_revision)[slot] !=
            token.scheduling_revision or
        context.column(u64, context.layout.resources.gate_epoch)[slot] != token.gate_epoch or
        context.column(u8, context.layout.resources.destructive_action)[slot] !=
            token.action_mask or
        context.column(u64, context.layout.resources.destructive_attempt_id_low)[slot] !=
            token.action_attempt_id_low or
        context.column(u64, context.layout.resources.destructive_attempt_id_high)[slot] !=
            token.action_attempt_id_high)
    {
        return error.StaleReference;
    }
    return slot;
}

fn validateRecallToken(
    context: *const state.Context,
    token: *const types.RecallToken,
) !u32 {
    if (token.scheduling_revision == 0 or
        token.gate_epoch == 0 or
        (token.action_attempt_id_low == 0 and token.action_attempt_id_high == 0))
    {
        return error.InvalidArgument;
    }
    if (!context.resourceMatches(token.resource)) return error.StaleReference;
    const slot = token.resource.resource_slot;
    if (!context.destructiveActive(slot) or
        context.column(u8, context.layout.resources.destructive_action)[slot] != 0 or
        context.column(u64, context.layout.resources.scheduling_revision)[slot] !=
            token.scheduling_revision or
        context.column(u64, context.layout.resources.gate_epoch)[slot] != token.gate_epoch or
        context.column(u64, context.layout.resources.destructive_attempt_id_low)[slot] !=
            token.action_attempt_id_low or
        context.column(u64, context.layout.resources.destructive_attempt_id_high)[slot] !=
            token.action_attempt_id_high or
        context.column(u64, context.layout.resources.payload_generation)[slot] !=
            token.payload_generation or
        context.column(u8, context.layout.resources.availability)[slot] !=
            @intFromEnum(types.Availability.preparing) or
        context.column(u64, context.layout.resources.size_bytes)[slot] != 0 or
        context.column(u64, context.layout.resources.payload_mapping_id)[slot] != 0)
    {
        return error.StaleReference;
    }
    return slot;
}

fn validateRecallPostState(
    context: *const state.Context,
    slot: u32,
    token: *const types.RecallToken,
    post_state: *const types.ResourcePublication,
) !void {
    if ((post_state.public_resource_id != 0 and
        post_state.public_resource_id !=
            context.column(u64, context.layout.resources.public_resource_id)[slot]) or
        post_state.owner_application_key !=
            context.column(u64, context.layout.resources.owner_application_key)[slot] or
        post_state.owner_instance_id !=
            context.column(u64, context.layout.resources.owner_instance_id)[slot] or
        post_state.owner_instance_id_high !=
            context.column(u64, context.layout.resources.owner_instance_id_high)[slot] or
        post_state.owner_context_generation !=
            context.column(u64, context.layout.resources.owner_context_generation)[slot] or
        post_state.lease_generation !=
            context.column(u64, context.layout.resources.lease_generation)[slot] or
        post_state.binding_generation !=
            context.column(u64, context.layout.resources.binding_generation)[slot] or
        post_state.capability_generation !=
            context.column(u64, context.layout.resources.capability_generation)[slot] or
        post_state.executor_id_low !=
            context.column(u64, context.layout.resources.executor_id_low)[slot] or
        post_state.executor_id_high !=
            context.column(u64, context.layout.resources.executor_id_high)[slot] or
        post_state.owner_process_id !=
            context.column(i32, context.layout.resources.owner_process_id)[slot] or
        post_state.resource_key !=
            context.column(u64, context.layout.resources.resource_key)[slot] or
        post_state.adapter_key !=
            context.column(u64, context.layout.resources.adapter_key)[slot] or
        post_state.resource_id !=
            context.column(u32, context.layout.resources.resource_id)[slot] or
        post_state.content_identity_hash !=
            context.column(u64, context.layout.resources.content_identity_hash)[slot] or
        post_state.max_parallel_grants !=
            context.column(u16, context.layout.resources.max_parallel_grants)[slot] or
        post_state.tier != context.column(u8, context.layout.resources.tier)[slot] or
        post_state.resource_kind !=
            context.column(u8, context.layout.resources.resource_kind)[slot] or
        post_state.recovery_kind !=
            context.column(u8, context.layout.resources.recovery_kind)[slot] or
        post_state.granularity !=
            context.column(u8, context.layout.resources.granularity)[slot] or
        post_state.inapplicable_actions !=
            context.column(u8, context.layout.resources.inapplicable_actions)[slot] or
        post_state.action_route !=
            context.column(u8, context.layout.resources.action_route)[slot] or
        post_state.owner_demand_mask !=
            context.column(u8, context.layout.resources.owner_demand_mask)[slot] or
        post_state.surface_state !=
            context.column(u8, context.layout.resources.surface_state)[slot] or
        post_state.flags != context.column(u8, context.layout.resources.flags)[slot])
    {
        return error.Conflict;
    }
    const next_payload_generation = types.nextGeneration(token.payload_generation) orelse
        return error.CapacityExhausted;
    if (post_state.availability != @intFromEnum(types.Availability.available) or
        post_state.size_bytes == 0 or
        post_state.payload_generation != next_payload_generation)
    {
        return error.InvalidArgument;
    }
}

fn validatePostState(
    context: *const state.Context,
    slot: u32,
    action_mask: u8,
    post_state: *const types.ResourcePublication,
) !void {
    if ((post_state.public_resource_id != 0 and
        post_state.public_resource_id !=
            context.column(u64, context.layout.resources.public_resource_id)[slot]) or
        post_state.owner_application_key !=
            context.column(u64, context.layout.resources.owner_application_key)[slot] or
        post_state.owner_instance_id !=
            context.column(u64, context.layout.resources.owner_instance_id)[slot] or
        post_state.owner_instance_id_high !=
            context.column(u64, context.layout.resources.owner_instance_id_high)[slot] or
        post_state.owner_context_generation !=
            context.column(u64, context.layout.resources.owner_context_generation)[slot] or
        post_state.lease_generation !=
            context.column(u64, context.layout.resources.lease_generation)[slot] or
        post_state.binding_generation !=
            context.column(u64, context.layout.resources.binding_generation)[slot] or
        post_state.capability_generation !=
            context.column(u64, context.layout.resources.capability_generation)[slot] or
        post_state.executor_id_low !=
            context.column(u64, context.layout.resources.executor_id_low)[slot] or
        post_state.executor_id_high !=
            context.column(u64, context.layout.resources.executor_id_high)[slot] or
        post_state.owner_process_id !=
            context.column(i32, context.layout.resources.owner_process_id)[slot] or
        post_state.resource_key !=
            context.column(u64, context.layout.resources.resource_key)[slot] or
        post_state.adapter_key !=
            context.column(u64, context.layout.resources.adapter_key)[slot] or
        post_state.resource_id !=
            context.column(u32, context.layout.resources.resource_id)[slot] or
        post_state.action_route !=
            context.column(u8, context.layout.resources.action_route)[slot])
    {
        return error.Conflict;
    }

    const previous_size = context.column(u64, context.layout.resources.size_bytes)[slot];
    const previous_tier = context.column(u8, context.layout.resources.tier)[slot];
    switch (action_mask) {
        1 => {
            if (post_state.size_bytes >= previous_size and
                post_state.availability == @intFromEnum(types.Availability.available))
            {
                return error.InvalidArgument;
            }
        },
        2 => if (post_state.size_bytes > previous_size) return error.InvalidArgument,
        4 => if (post_state.tier <= previous_tier) return error.InvalidArgument,
        else => return error.InvalidArgument,
    }

    const current_grants =
        context.column(u32, context.layout.resources.reserved_grant_count)[slot] +
        context.column(u32, context.layout.resources.active_use_count)[slot];
    if (post_state.max_parallel_grants < current_grants) return error.ResourceBusy;
}

fn clearDestructive(context: *state.Context, slot: u32) void {
    context.column(u8, context.layout.resources.destructive_active)[slot] = 0;
    context.column(u8, context.layout.resources.destructive_action)[slot] = 0;
    context.column(u64, context.layout.resources.destructive_attempt_id_low)[slot] = 0;
    context.column(u64, context.layout.resources.destructive_attempt_id_high)[slot] = 0;
}

pub fn nextGateEpoch(context: *const state.Context, resource_slot: u32) !u64 {
    const epochs = context.column(u64, context.layout.resources.gate_epoch);
    return types.nextGeneration(epochs[resource_slot]) orelse
        error.CapacityExhausted;
}

pub fn commitGateEpoch(
    context: *state.Context,
    resource_slot: u32,
    next_gate_epoch: u64,
) void {
    const epochs = context.column(u64, context.layout.resources.gate_epoch);
    std.debug.assert(types.nextGeneration(epochs[resource_slot]) == next_gate_epoch);
    epochs[resource_slot] = next_gate_epoch;
}
