const std = @import("std");
const state = @import("state.zig");
const types = state.types;

pub fn subscribe(
    context: *state.Context,
    request: *const types.SubscriptionRequest,
    output: *types.SubscriptionReceipt,
) !void {
    if (!types.validSubscriptionRequest(request)) return error.InvalidArgument;
    var guard = try context.acquire();
    defer guard.deinit();
    if (guard.abandoned or context.consistencyState() != .stable) return error.InconsistentState;
    if (!context.resourceMatches(request.resource)) return error.StaleReference;

    if (findExisting(context, request)) |slot| {
        const old_intent = context.column(u8, context.layout.subscriptions.intent_mask)[slot];
        const scheduling_invalidation = if (old_intent != request.intent_mask)
            try prepareSchedulingInvalidation(context, request.resource.resource_slot)
        else
            null;
        var mutation = try context.beginMutation();
        defer mutation.finish();
        applyIntentDelta(context, request.resource.resource_slot, old_intent, request.intent_mask);
        writeRequest(context, slot, request);
        if (scheduling_invalidation) |invalidation| {
            commitSchedulingInvalidation(context, invalidation);
        }
        fillReceipt(context, slot, output);
        return;
    }

    const planned_slot = findFreeSubscription(context) orelse return error.CapacityExhausted;
    const generations = context.column(u64, context.layout.subscriptions.generation);
    const next_generation = types.nextGeneration(generations[planned_slot]) orelse unreachable;
    const resource_slot = request.resource.resource_slot;
    if (context.column(u32, context.layout.resources.active_subscriber_count)[resource_slot] ==
        std.math.maxInt(u32))
    {
        return error.CapacityExhausted;
    }
    const scheduling_invalidation = try prepareSchedulingInvalidation(context, resource_slot);

    var mutation = try context.beginMutation();
    defer mutation.finish();
    const slot = takeFreeSubscription(context) orelse unreachable;
    std.debug.assert(slot == planned_slot);
    generations[slot] = next_generation;
    writeRequest(context, slot, request);
    context.column(u8, context.layout.subscriptions.control)[slot] =
        @intFromEnum(types.SubscriptionControl.active);
    incrementSubscriber(context, resource_slot, request.intent_mask);
    commitSchedulingInvalidation(context, scheduling_invalidation);
    fillReceipt(context, slot, output);
}

pub fn confirm(
    context: *state.Context,
    receipt: *const types.SubscriptionReceipt,
    deadline_timestamp: u64,
    intent_mask: u8,
    output: *types.SubscriptionReceipt,
) !void {
    if (deadline_timestamp == 0 or intent_mask == 0 or intent_mask & ~types.subscription_intent_bits != 0) {
        return error.InvalidArgument;
    }
    var guard = try context.acquire();
    defer guard.deinit();
    if (guard.abandoned or context.consistencyState() != .stable) return error.InconsistentState;
    if (!context.resourceMatches(receipt.resource)) return error.StaleReference;
    const slot = receipt.subscription_slot;
    if (!receiptMatches(context, slot, receipt)) return error.StaleReference;

    const old_intent = context.column(u8, context.layout.subscriptions.intent_mask)[slot];
    const scheduling_invalidation = if (old_intent != intent_mask)
        try prepareSchedulingInvalidation(context, receipt.resource.resource_slot)
    else
        null;
    var mutation = try context.beginMutation();
    defer mutation.finish();
    applyIntentDelta(context, receipt.resource.resource_slot, old_intent, intent_mask);
    context.column(u64, context.layout.subscriptions.deadline_timestamp)[slot] = deadline_timestamp;
    context.column(u8, context.layout.subscriptions.intent_mask)[slot] = intent_mask;
    if (scheduling_invalidation) |invalidation| {
        commitSchedulingInvalidation(context, invalidation);
    }
    fillReceipt(context, slot, output);
}

pub fn release(context: *state.Context, receipt: *const types.SubscriptionReceipt) !void {
    var guard = try context.acquire();
    defer guard.deinit();
    if (guard.abandoned or context.consistencyState() != .stable) return error.InconsistentState;
    const slot = receipt.subscription_slot;
    if (!context.resourceMatches(receipt.resource) or
        !receiptMatches(context, slot, receipt))
    {
        return error.StaleReference;
    }
    if (hasTaskForSubscriptionLocked(context, slot)) return error.ResourceBusy;
    const resource_slot = receipt.resource.resource_slot;
    if (context.column(u32, context.layout.resources.active_subscriber_count)[resource_slot] == 0) {
        return error.InconsistentState;
    }
    const scheduling_invalidation = try prepareSchedulingInvalidation(context, resource_slot);

    var mutation = try context.beginMutation();
    defer mutation.finish();
    removeSlot(context, slot);
    commitSchedulingInvalidation(context, scheduling_invalidation);
}

pub fn sweep(context: *state.Context, now_timestamp: u64, expired_count: *u32) !void {
    var guard = try context.acquire();
    defer guard.deinit();
    if (guard.abandoned or context.consistencyState() != .stable) return error.InconsistentState;
    if (!hasSweepRemoval(context, now_timestamp)) {
        expired_count.* = 0;
        return;
    }
    @memset(context.resource_sweep_markers, 0);
    var preflight_slot: u32 = 0;
    while (preflight_slot < context.header.subscription_capacity) : (preflight_slot += 1) {
        if (sweepRemovalResource(context, preflight_slot, now_timestamp)) |resource_slot| {
            if (resource_slot != types.none_slot) {
                context.resource_sweep_markers[resource_slot] = 1;
            }
        }
    }
    var scheduling_changed = false;
    var resource_slot: u32 = 0;
    while (resource_slot < context.header.resource_capacity) : (resource_slot += 1) {
        if (context.resource_sweep_markers[resource_slot] == 0) continue;
        _ = types.nextGeneration(
            context.column(u64, context.layout.resources.scheduling_revision)[resource_slot],
        ) orelse return error.CapacityExhausted;
        scheduling_changed = true;
    }
    if (scheduling_changed) _ = try context.nextTopologyGeneration();

    var mutation = try context.beginMutation();
    defer mutation.finish();

    if (scheduling_changed) {
        var changed_resource_slot: u32 = 0;
        while (changed_resource_slot < context.header.resource_capacity) : (changed_resource_slot += 1) {
            if (context.resource_sweep_markers[changed_resource_slot] == 0) continue;
            const revisions = context.column(u64, context.layout.resources.scheduling_revision);
            revisions[changed_resource_slot] = types.nextGeneration(revisions[changed_resource_slot]) orelse
                unreachable;
        }
        context.header.topology_generation = types.nextGeneration(context.header.topology_generation) orelse
            unreachable;
    }

    var expired: u32 = 0;
    var slot: u32 = 0;
    while (slot < context.header.subscription_capacity) : (slot += 1) {
        if (sweepRemovalResource(context, slot, now_timestamp) == null) continue;
        removeSlot(context, slot);
        expired += 1;
    }
    expired_count.* = expired;
}

fn hasSweepRemoval(context: *const state.Context, now_timestamp: u64) bool {
    var slot: u32 = 0;
    while (slot < context.header.subscription_capacity) : (slot += 1) {
        if (sweepRemovalResource(context, slot, now_timestamp) != null) return true;
    }
    return false;
}

fn sweepRemovalResource(
    context: *const state.Context,
    slot: u32,
    now_timestamp: u64,
) ?u32 {
    if (context.column(u8, context.layout.subscriptions.control)[slot] !=
        @intFromEnum(types.SubscriptionControl.active))
    {
        return null;
    }
    const resource_slot =
        context.column(u32, context.layout.subscriptions.target_resource_slot)[slot];
    const resource_generation =
        context.column(u64, context.layout.subscriptions.target_resource_generation)[slot];
    const deadline =
        context.column(u64, context.layout.subscriptions.deadline_timestamp)[slot];
    const stale_resource = resource_slot >= context.header.resource_capacity or
        context.resourceControl(resource_slot) != .active or
        context.column(u64, context.layout.resources.generation)[resource_slot] !=
            resource_generation;
    if (deadline > now_timestamp and !stale_resource) return null;
    if (!stale_resource and hasTaskForSubscriptionLocked(context, slot)) return null;
    return if (stale_resource) types.none_slot else resource_slot;
}

pub fn hasActiveUseSubscriptionLocked(
    context: *const state.Context,
    resource: types.ResourceRef,
    subscriber_application_key: u64,
    subscriber_instance_id: u64,
    subscriber_session_id: u64,
    subscriber_process_id: i32,
    now_timestamp: u64,
) bool {
    var slot: u32 = 0;
    while (slot < context.header.subscription_capacity) : (slot += 1) {
        if (context.column(u8, context.layout.subscriptions.control)[slot] !=
            @intFromEnum(types.SubscriptionControl.active))
        {
            continue;
        }
        if (context.column(u32, context.layout.subscriptions.target_resource_slot)[slot] == resource.resource_slot and
            context.column(u64, context.layout.subscriptions.target_resource_generation)[slot] == resource.resource_generation and
            context.column(u64, context.layout.subscriptions.subscriber_application_key)[slot] == subscriber_application_key and
            context.column(u64, context.layout.subscriptions.subscriber_instance_id)[slot] == subscriber_instance_id and
            context.column(u64, context.layout.subscriptions.subscriber_session_id)[slot] == subscriber_session_id and
            context.column(i32, context.layout.subscriptions.subscriber_process_id)[slot] == subscriber_process_id and
            context.column(u64, context.layout.subscriptions.deadline_timestamp)[slot] > now_timestamp)
        {
            return true;
        }
    }
    return false;
}

fn hasTaskForSubscriptionLocked(context: *const state.Context, subscription_slot: u32) bool {
    const resource_slot =
        context.column(u32, context.layout.subscriptions.target_resource_slot)[subscription_slot];
    const resource_generation =
        context.column(u64, context.layout.subscriptions.target_resource_generation)[subscription_slot];
    const application_key =
        context.column(u64, context.layout.subscriptions.subscriber_application_key)[subscription_slot];
    const instance_id =
        context.column(u64, context.layout.subscriptions.subscriber_instance_id)[subscription_slot];
    const session_id =
        context.column(u64, context.layout.subscriptions.subscriber_session_id)[subscription_slot];
    const process_id =
        context.column(i32, context.layout.subscriptions.subscriber_process_id)[subscription_slot];

    var task_slot: u32 = 0;
    while (task_slot < context.header.task_capacity) : (task_slot += 1) {
        if (context.column(u8, context.layout.tasks.state)[task_slot] ==
            @intFromEnum(types.TaskState.free))
        {
            continue;
        }
        if (context.column(u32, context.layout.tasks.resource_slot)[task_slot] == resource_slot and
            context.column(u64, context.layout.tasks.resource_generation)[task_slot] == resource_generation and
            context.column(u64, context.layout.tasks.requester_application_key)[task_slot] == application_key and
            context.column(u64, context.layout.tasks.requester_instance_id)[task_slot] == instance_id and
            context.column(u64, context.layout.tasks.requester_session_id)[task_slot] == session_id and
            context.column(i32, context.layout.tasks.requester_process_id)[task_slot] == process_id)
        {
            return true;
        }
    }
    return false;
}

pub fn updateCoefficient(context: *state.Context, coefficient: f64) !void {
    if (!std.math.isFinite(coefficient) or coefficient < 0) return error.InvalidArgument;
    var guard = try context.acquire();
    defer guard.deinit();
    if (guard.abandoned or context.consistencyState() != .stable) return error.InconsistentState;
    var mutation = try context.beginMutation();
    defer mutation.finish();

    try updateCoefficientLocked(context, coefficient);
}

pub fn updateCoefficientLocked(context: *state.Context, coefficient: f64) !void {
    if (!std.math.isFinite(coefficient) or coefficient < 0) return error.InvalidArgument;
    context.header.subscription_coefficient = coefficient;
    var resource_slot: u32 = 0;
    while (resource_slot < context.header.resource_capacity) : (resource_slot += 1) {
        if (context.resourceControl(resource_slot) == .active) {
            refreshAggregate(context, resource_slot);
        }
    }
}

fn findExisting(context: *const state.Context, request: *const types.SubscriptionRequest) ?u32 {
    var slot: u32 = 0;
    while (slot < context.header.subscription_capacity) : (slot += 1) {
        if (context.column(u8, context.layout.subscriptions.control)[slot] !=
            @intFromEnum(types.SubscriptionControl.active))
        {
            continue;
        }
        if (context.column(u32, context.layout.subscriptions.target_resource_slot)[slot] == request.resource.resource_slot and
            context.column(u64, context.layout.subscriptions.target_resource_generation)[slot] == request.resource.resource_generation and
            context.column(u64, context.layout.subscriptions.subscriber_application_key)[slot] == request.subscriber_application_key and
            context.column(u64, context.layout.subscriptions.subscriber_instance_id)[slot] == request.subscriber_instance_id and
            context.column(u64, context.layout.subscriptions.subscriber_session_id)[slot] == request.subscriber_session_id)
        {
            return slot;
        }
    }
    return null;
}

fn findFreeSubscription(context: *const state.Context) ?u32 {
    var slot = context.header.subscription_free_head;
    while (slot != types.none_slot) {
        if (context.column(u64, context.layout.subscriptions.generation)[slot] !=
            std.math.maxInt(u64))
        {
            return slot;
        }
        slot = context.column(u32, context.layout.subscriptions.free_next)[slot];
    }
    return null;
}

fn takeFreeSubscription(context: *state.Context) ?u32 {
    while (context.header.subscription_free_head != types.none_slot) {
        const slot = context.header.subscription_free_head;
        context.header.subscription_free_head =
            context.column(u32, context.layout.subscriptions.free_next)[slot];
        if (context.column(u64, context.layout.subscriptions.generation)[slot] !=
            std.math.maxInt(u64))
        {
            return slot;
        }
    }
    return null;
}

fn writeRequest(context: *state.Context, slot: u32, request: *const types.SubscriptionRequest) void {
    context.column(u32, context.layout.subscriptions.target_resource_slot)[slot] = request.resource.resource_slot;
    context.column(u64, context.layout.subscriptions.target_resource_generation)[slot] = request.resource.resource_generation;
    context.column(u64, context.layout.subscriptions.subscriber_application_key)[slot] = request.subscriber_application_key;
    context.column(u64, context.layout.subscriptions.subscriber_instance_id)[slot] = request.subscriber_instance_id;
    context.column(u64, context.layout.subscriptions.subscriber_session_id)[slot] = request.subscriber_session_id;
    context.column(u64, context.layout.subscriptions.deadline_timestamp)[slot] = request.lease_duration;
    context.column(i32, context.layout.subscriptions.subscriber_process_id)[slot] = request.subscriber_process_id;
    context.column(u8, context.layout.subscriptions.intent_mask)[slot] = request.intent_mask;
}

fn receiptMatches(
    context: *const state.Context,
    slot: u32,
    receipt: *const types.SubscriptionReceipt,
) bool {
    if (slot >= context.header.subscription_capacity or
        context.column(u8, context.layout.subscriptions.control)[slot] !=
            @intFromEnum(types.SubscriptionControl.active))
    {
        return false;
    }
    return context.column(u64, context.layout.subscriptions.generation)[slot] == receipt.subscription_generation and
        context.column(u32, context.layout.subscriptions.target_resource_slot)[slot] == receipt.resource.resource_slot and
        context.column(u64, context.layout.subscriptions.target_resource_generation)[slot] == receipt.resource.resource_generation and
        context.column(u64, context.layout.subscriptions.subscriber_instance_id)[slot] == receipt.subscriber_instance_id and
        context.column(u64, context.layout.subscriptions.subscriber_session_id)[slot] == receipt.subscriber_session_id;
}

fn fillReceipt(context: *const state.Context, slot: u32, output: *types.SubscriptionReceipt) void {
    output.* = std.mem.zeroes(types.SubscriptionReceipt);
    const resource_slot = context.column(u32, context.layout.subscriptions.target_resource_slot)[slot];
    output.resource = .{
        .ledger_instance_id = context.header.ledger_instance_id,
        .resource_generation = context.column(u64, context.layout.subscriptions.target_resource_generation)[slot],
        .public_resource_id = context.column(u64, context.layout.resources.public_resource_id)[resource_slot],
        .resource_slot = resource_slot,
        .reserved = 0,
    };
    output.subscriber_instance_id = context.column(u64, context.layout.subscriptions.subscriber_instance_id)[slot];
    output.subscriber_session_id = context.column(u64, context.layout.subscriptions.subscriber_session_id)[slot];
    output.deadline_timestamp = context.column(u64, context.layout.subscriptions.deadline_timestamp)[slot];
    output.subscription_generation = context.column(u64, context.layout.subscriptions.generation)[slot];
    output.subscription_slot = slot;
    output.subscriber_process_id = context.column(i32, context.layout.subscriptions.subscriber_process_id)[slot];
    output.intent_mask = context.column(u8, context.layout.subscriptions.intent_mask)[slot];
}

fn incrementSubscriber(context: *state.Context, resource_slot: u32, intent_mask: u8) void {
    context.column(u32, context.layout.resources.active_subscriber_count)[resource_slot] += 1;
    applyIntentCounts(context, resource_slot, intent_mask, true);
    refreshAggregate(context, resource_slot);
}

fn removeSlot(context: *state.Context, slot: u32) void {
    const resource_slot = context.column(u32, context.layout.subscriptions.target_resource_slot)[slot];
    const resource_generation = context.column(u64, context.layout.subscriptions.target_resource_generation)[slot];
    const intent_mask = context.column(u8, context.layout.subscriptions.intent_mask)[slot];
    if (resource_slot < context.header.resource_capacity and
        context.resourceControl(resource_slot) == .active and
        context.column(u64, context.layout.resources.generation)[resource_slot] == resource_generation)
    {
        const counts = context.column(u32, context.layout.resources.active_subscriber_count);
        if (counts[resource_slot] > 0) counts[resource_slot] -= 1;
        applyIntentCounts(context, resource_slot, intent_mask, false);
        refreshAggregate(context, resource_slot);
    }
    context.column(u8, context.layout.subscriptions.control)[slot] = @intFromEnum(types.SubscriptionControl.free);
    context.column(u64, context.layout.subscriptions.deadline_timestamp)[slot] = 0;
    context.column(u8, context.layout.subscriptions.intent_mask)[slot] = 0;
    if (context.column(u64, context.layout.subscriptions.generation)[slot] != std.math.maxInt(u64)) {
        context.column(u32, context.layout.subscriptions.free_next)[slot] = context.header.subscription_free_head;
        context.header.subscription_free_head = slot;
    }
}

fn applyIntentDelta(context: *state.Context, resource_slot: u32, old_mask: u8, new_mask: u8) void {
    if (old_mask == new_mask) return;
    applyIntentCounts(context, resource_slot, old_mask, false);
    applyIntentCounts(context, resource_slot, new_mask, true);
    refreshAggregate(context, resource_slot);
}

fn applyIntentCounts(context: *state.Context, resource_slot: u32, mask: u8, increment: bool) void {
    updateCount(context.column(u32, context.layout.resources.intent_ready_soon_count), resource_slot, mask & 0b0010 != 0, increment);
    updateCount(context.column(u32, context.layout.resources.intent_preload_eager_count), resource_slot, mask & 0b0100 != 0, increment);
    updateCount(context.column(u32, context.layout.resources.intent_preload_opportunistic_count), resource_slot, mask & 0b1000 != 0, increment);
}

fn updateCount(values: [*]u32, slot: u32, applies: bool, increment: bool) void {
    if (!applies) return;
    if (increment) {
        values[slot] += 1;
    } else if (values[slot] > 0) {
        values[slot] -= 1;
    }
}

fn refreshAggregate(context: *state.Context, resource_slot: u32) void {
    var intent: u8 = 0;
    if (context.column(u32, context.layout.resources.intent_ready_soon_count)[resource_slot] > 0) intent |= 0b0010;
    if (context.column(u32, context.layout.resources.intent_preload_eager_count)[resource_slot] > 0) intent |= 0b0100;
    if (context.column(u32, context.layout.resources.intent_preload_opportunistic_count)[resource_slot] > 0) intent |= 0b1000;
    context.column(u8, context.layout.resources.subscription_intent_mask)[resource_slot] = intent;
    const subscriber_count = context.column(u32, context.layout.resources.active_subscriber_count)[resource_slot];
    context.column(f64, context.layout.resources.subscription_multiplier)[resource_slot] =
        1.0 + context.header.subscription_coefficient * @log2(1.0 + @as(f64, @floatFromInt(subscriber_count)));
}

const SchedulingInvalidation = struct {
    resource_slot: u32,
    scheduling_revision: u64,
    topology_generation: u64,
};

fn prepareSchedulingInvalidation(
    context: *const state.Context,
    resource_slot: u32,
) !SchedulingInvalidation {
    return .{
        .resource_slot = resource_slot,
        .scheduling_revision = types.nextGeneration(
            context.column(u64, context.layout.resources.scheduling_revision)[resource_slot],
        ) orelse return error.CapacityExhausted,
        .topology_generation = try context.nextTopologyGeneration(),
    };
}

fn commitSchedulingInvalidation(
    context: *state.Context,
    invalidation: SchedulingInvalidation,
) void {
    context.column(u64, context.layout.resources.scheduling_revision)[invalidation.resource_slot] =
        invalidation.scheduling_revision;
    context.header.topology_generation = invalidation.topology_generation;
}
