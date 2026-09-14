const std = @import("std");
const state = @import("state.zig");
const resource_ops = @import("resource_ops.zig");
const subscription_ops = @import("subscription_ops.zig");
const activity = @import("activity.zig");
const types = state.types;

pub fn enqueue(
    context: *state.Context,
    request: *const types.UseRequest,
    now_timestamp: u64,
    output: *types.TaskReceipt,
) !void {
    if (!types.validUseRequest(request) or now_timestamp == 0) return error.InvalidArgument;
    var guard = try context.acquire();
    defer guard.deinit();
    if (guard.abandoned or context.consistencyState() != .stable) return error.InconsistentState;
    if (!context.resourceMatches(request.resource)) return error.StaleReference;
    const resource_slot = request.resource.resource_slot;
    if (!subscription_ops.hasActiveUseSubscriptionLocked(
        context,
        request.resource,
        request.requester_application_key,
        request.requester_instance_id,
        request.requester_session_id,
        request.requester_process_id,
        now_timestamp,
    )) return error.AccessDenied;
    if (context.destructiveActive(resource_slot) or
        context.column(u8, context.layout.resources.availability)[resource_slot] !=
            @intFromEnum(types.Availability.available))
    {
        return error.ResourceBusy;
    }
    if (findDuplicate(context, request)) |slot| {
        fillTaskReceipt(context, slot, output);
        return;
    }

    const enqueue_sequence = types.nextGeneration(context.header.enqueue_sequence) orelse
        return error.CapacityExhausted;
    const planned_slot = findFreeTask(context) orelse return error.CapacityExhausted;
    const generations = context.column(u64, context.layout.tasks.generation);
    const task_generation = types.nextGeneration(generations[planned_slot]) orelse
        return error.CapacityExhausted;
    if (context.column(u32, context.layout.resources.queued_request_count)[resource_slot] ==
        std.math.maxInt(u32))
    {
        return error.CapacityExhausted;
    }
    const next_gate_epoch = try nextGateEpochBy(context, resource_slot, 1);

    var mutation = try context.beginMutation();
    defer mutation.finish();
    const slot = takeFreeTask(context) orelse unreachable;
    std.debug.assert(slot == planned_slot);
    generations[slot] = task_generation;
    context.header.enqueue_sequence = enqueue_sequence;
    writePublicTask(context, slot, request, enqueue_sequence);
    insertSorted(context, resource_slot, slot);
    context.column(u8, context.layout.tasks.state)[slot] = @intFromEnum(types.TaskState.queued);
    context.column(u32, context.layout.resources.queued_request_count)[resource_slot] += 1;
    commitGateEpoch(context, resource_slot, next_gate_epoch);
    fillTaskReceipt(context, slot, output);
}

pub fn reserveNext(
    context: *state.Context,
    resource: types.ResourceRef,
    now_timestamp: u64,
    grant_deadline_timestamp: u64,
    output: *types.GrantReceipt,
) !void {
    if (now_timestamp == 0 or grant_deadline_timestamp <= now_timestamp) return error.InvalidArgument;
    var guard = try context.acquire();
    defer guard.deinit();
    if (guard.abandoned or context.consistencyState() != .stable) return error.InconsistentState;
    if (!context.resourceMatches(resource)) return error.StaleReference;
    const resource_slot = resource.resource_slot;
    if (context.destructiveActive(resource_slot)) return error.ResourceBusy;

    const expired_count = countExpiredQueuedForResource(context, resource_slot, now_timestamp);
    const reserved = context.column(u32, context.layout.resources.reserved_grant_count)[resource_slot];
    const active = context.column(u32, context.layout.resources.active_use_count)[resource_slot];
    const max_parallel = context.column(u16, context.layout.resources.max_parallel_grants)[resource_slot];
    const capacity_available = @as(u64, reserved) + @as(u64, active) < max_parallel;
    const planned_slot = firstLiveQueuedForResource(context, resource_slot, now_timestamp);
    const will_reserve = capacity_available and planned_slot != types.none_slot;
    const gate_advance_count = expired_count + @intFromBool(will_reserve);
    const final_gate_epoch = try nextGateEpochBy(
        context,
        resource_slot,
        gate_advance_count,
    );
    const next_grant_generation = if (will_reserve)
        types.nextGeneration(
            context.column(u64, context.layout.tasks.grant_generation)[planned_slot],
        ) orelse return error.CapacityExhausted
    else
        0;
    if (will_reserve and reserved == std.math.maxInt(u32)) return error.CapacityExhausted;
    const queued_count =
        context.column(u32, context.layout.resources.queued_request_count)[resource_slot];
    const required_queued_count = expired_count + @intFromBool(will_reserve);
    if (@as(u64, queued_count) < required_queued_count) return error.InconsistentState;
    if (!capacity_available and expired_count == 0) return error.ResourceBusy;
    if (planned_slot == types.none_slot and expired_count == 0) return error.QueueEmpty;

    var mutation = try context.beginMutation();
    defer mutation.finish();
    expireQueuedForResource(context, resource_slot, now_timestamp);
    if (!capacity_available) {
        if (expired_count != 0) commitGateEpoch(context, resource_slot, final_gate_epoch);
        return error.ResourceBusy;
    }
    const slot = context.column(u32, context.layout.resources.queue_head)[resource_slot];
    if (slot == types.none_slot) {
        if (expired_count != 0) commitGateEpoch(context, resource_slot, final_gate_epoch);
        return error.QueueEmpty;
    }
    std.debug.assert(slot == planned_slot);
    if (taskState(context, slot) != .queued) {
        return error.InconsistentState;
    }
    const grants = context.column(u64, context.layout.tasks.grant_generation);

    removeFromQueue(context, resource_slot, slot);
    const queued_counts = context.column(u32, context.layout.resources.queued_request_count);
    std.debug.assert(queued_counts[resource_slot] != 0);
    queued_counts[resource_slot] -= 1;
    context.column(u32, context.layout.resources.reserved_grant_count)[resource_slot] += 1;
    context.column(u8, context.layout.tasks.state)[slot] = @intFromEnum(types.TaskState.reserved);
    grants[slot] = next_grant_generation;
    context.column(u64, context.layout.tasks.grant_deadline_timestamp)[slot] = grant_deadline_timestamp;
    context.column(u64, context.layout.tasks.payload_generation)[slot] =
        context.column(u64, context.layout.resources.payload_generation)[resource_slot];
    commitGateEpoch(context, resource_slot, final_gate_epoch);
    fillGrantReceipt(context, slot, output);
}

pub fn beginGrantedUse(
    context: *state.Context,
    receipt: *const types.GrantReceipt,
    now_timestamp: u64,
    output: *types.GrantReceipt,
) !void {
    var guard = try context.acquire();
    defer guard.deinit();
    if (guard.abandoned or context.consistencyState() != .stable) return error.InconsistentState;
    const slot = receipt.task.task_slot;
    if (!grantMatches(context, slot, receipt, .reserved)) return error.StaleReference;
    if (receipt.deadline_timestamp <= now_timestamp) return error.Expired;
    const resource_slot = receipt.task.resource.resource_slot;
    if (context.destructiveActive(resource_slot)) return error.ResourceBusy;
    if (context.column(u32, context.layout.resources.active_use_count)[resource_slot] ==
        std.math.maxInt(u32))
    {
        return error.CapacityExhausted;
    }
    const next_gate_epoch = try nextGateEpochBy(context, resource_slot, 1);
    const current_activity = context.column(u8, context.layout.resources.activity_score)[resource_slot];
    const next_activity = activity.afterSuccessfulUse(current_activity);
    const activity_changed = next_activity != current_activity;
    const next_scheduling_revision = if (activity_changed)
        types.nextGeneration(
            context.column(u64, context.layout.resources.scheduling_revision)[resource_slot],
        ) orelse return error.CapacityExhausted
    else
        0;
    const next_topology_generation = if (activity_changed)
        try context.nextTopologyGeneration()
    else
        0;

    var mutation = try context.beginMutation();
    defer mutation.finish();
    const reserved_counts = context.column(u32, context.layout.resources.reserved_grant_count);
    if (reserved_counts[resource_slot] == 0) return error.InconsistentState;
    reserved_counts[resource_slot] -= 1;
    context.column(u32, context.layout.resources.active_use_count)[resource_slot] += 1;
    context.column(u8, context.layout.tasks.state)[slot] = @intFromEnum(types.TaskState.active);
    if (activity_changed) {
        context.column(u8, context.layout.resources.activity_score)[resource_slot] = next_activity;
        context.column(u64, context.layout.resources.scheduling_revision)[resource_slot] =
            next_scheduling_revision;
        context.header.topology_generation = next_topology_generation;
    }
    commitGateEpoch(context, resource_slot, next_gate_epoch);
    fillGrantReceipt(context, slot, output);
}

pub fn confirmGrantedUse(
    context: *state.Context,
    receipt: *const types.GrantReceipt,
    now_timestamp: u64,
    new_deadline_timestamp: u64,
    output: *types.GrantReceipt,
) !void {
    if (new_deadline_timestamp <= now_timestamp) return error.InvalidArgument;
    var guard = try context.acquire();
    defer guard.deinit();
    if (guard.abandoned or context.consistencyState() != .stable) return error.InconsistentState;
    const slot = receipt.task.task_slot;
    if (!grantMatches(context, slot, receipt, .active)) return error.StaleReference;
    if (receipt.deadline_timestamp <= now_timestamp) return error.Expired;

    var mutation = try context.beginMutation();
    defer mutation.finish();
    context.column(u64, context.layout.tasks.grant_deadline_timestamp)[slot] = new_deadline_timestamp;
    fillGrantReceipt(context, slot, output);
}

pub fn completeGrantedUse(context: *state.Context, receipt: *const types.GrantReceipt) !void {
    var guard = try context.acquire();
    defer guard.deinit();
    if (guard.abandoned or context.consistencyState() != .stable) return error.InconsistentState;
    const slot = receipt.task.task_slot;
    if (!grantMatches(context, slot, receipt, .active)) return error.StaleReference;
    const resource_slot = receipt.task.resource.resource_slot;
    const next_gate_epoch = try nextGateEpochBy(context, resource_slot, 1);

    var mutation = try context.beginMutation();
    defer mutation.finish();
    const active_counts = context.column(u32, context.layout.resources.active_use_count);
    if (active_counts[resource_slot] == 0) return error.InconsistentState;
    active_counts[resource_slot] -= 1;
    releaseTaskSlot(context, slot);
    commitGateEpoch(context, resource_slot, next_gate_epoch);
}

pub fn cancel(context: *state.Context, receipt: *const types.TaskReceipt) !void {
    var guard = try context.acquire();
    defer guard.deinit();
    if (guard.abandoned or context.consistencyState() != .stable) return error.InconsistentState;
    const slot = receipt.task_slot;
    if (!taskReceiptMatches(context, slot, receipt)) return error.StaleReference;
    const current_state = taskState(context, slot);
    if (current_state == .active or current_state == .free) {
        return error.InvalidState;
    }
    const resource_slot = receipt.resource.resource_slot;
    const next_gate_epoch = try nextGateEpochBy(context, resource_slot, 1);
    if (current_state == .queued and
        context.column(u32, context.layout.resources.queued_request_count)[resource_slot] == 0)
    {
        return error.InconsistentState;
    }
    if (current_state == .reserved and
        context.column(u32, context.layout.resources.reserved_grant_count)[resource_slot] == 0)
    {
        return error.InconsistentState;
    }

    var mutation = try context.beginMutation();
    defer mutation.finish();
    if (current_state == .queued) {
        removeFromQueue(context, resource_slot, slot);
        const counts = context.column(u32, context.layout.resources.queued_request_count);
        std.debug.assert(counts[resource_slot] != 0);
        counts[resource_slot] -= 1;
    } else {
        const counts = context.column(u32, context.layout.resources.reserved_grant_count);
        std.debug.assert(counts[resource_slot] != 0);
        counts[resource_slot] -= 1;
    }
    releaseTaskSlot(context, slot);
    commitGateEpoch(context, resource_slot, next_gate_epoch);
}

pub fn sweep(context: *state.Context, now_timestamp: u64, removed_count: *u32) !void {
    var guard = try context.acquire();
    defer guard.deinit();
    if (guard.abandoned or context.consistencyState() != .stable) return error.InconsistentState;
    if (!hasSweepRemoval(context, now_timestamp)) {
        removed_count.* = 0;
        return;
    }
    @memset(context.resource_sweep_markers, 0);
    var preflight_slot: u32 = 0;
    while (preflight_slot < context.header.task_capacity) : (preflight_slot += 1) {
        if (sweepRemovalResource(context, preflight_slot, now_timestamp)) |resource_slot| {
            context.resource_sweep_markers[resource_slot] = 1;
        }
    }
    var checked_resource_slot: u32 = 0;
    while (checked_resource_slot < context.header.resource_capacity) : (checked_resource_slot += 1) {
        if (context.resource_sweep_markers[checked_resource_slot] != 0) {
            _ = try nextGateEpochBy(context, checked_resource_slot, 1);
        }
    }
    var mutation = try context.beginMutation();
    defer mutation.finish();

    var removed: u32 = 0;
    var slot: u32 = 0;
    while (slot < context.header.task_capacity) : (slot += 1) {
        const current_state = taskState(context, slot);
        if (current_state == .free) continue;
        const resource_slot = context.column(u32, context.layout.tasks.resource_slot)[slot];
        const resource_generation = context.column(u64, context.layout.tasks.resource_generation)[slot];
        const stale_resource = resource_slot >= context.header.resource_capacity or
            context.resourceControl(resource_slot) != .active or
            context.column(u64, context.layout.resources.generation)[resource_slot] != resource_generation;
        const deadline = if (current_state == .queued)
            context.column(u64, context.layout.tasks.queue_deadline_timestamp)[slot]
        else
            context.column(u64, context.layout.tasks.grant_deadline_timestamp)[slot];
        if (!stale_resource and deadline > now_timestamp) continue;
        if (!stale_resource) removeTaskProtection(context, resource_slot, slot, current_state);
        releaseTaskSlot(context, slot);
        removed += 1;
    }
    var affected_resource_slot: u32 = 0;
    while (affected_resource_slot < context.header.resource_capacity) : (affected_resource_slot += 1) {
        if (context.resource_sweep_markers[affected_resource_slot] == 0) continue;
        const next_gate_epoch = types.nextGeneration(
            context.column(u64, context.layout.resources.gate_epoch)[affected_resource_slot],
        ) orelse unreachable;
        commitGateEpoch(context, affected_resource_slot, next_gate_epoch);
    }
    removed_count.* = removed;
}

fn hasSweepRemoval(context: *const state.Context, now_timestamp: u64) bool {
    var slot: u32 = 0;
    while (slot < context.header.task_capacity) : (slot += 1) {
        if (isSweepRemoval(context, slot, now_timestamp)) return true;
    }
    return false;
}

fn sweepRemovalResource(
    context: *const state.Context,
    slot: u32,
    now_timestamp: u64,
) ?u32 {
    if (!isSweepRemoval(context, slot, now_timestamp)) return null;
    const resource_slot = context.column(u32, context.layout.tasks.resource_slot)[slot];
    const resource_generation =
        context.column(u64, context.layout.tasks.resource_generation)[slot];
    if (resource_slot >= context.header.resource_capacity or
        context.resourceControl(resource_slot) != .active or
        context.column(u64, context.layout.resources.generation)[resource_slot] !=
            resource_generation)
    {
        return null;
    }
    return resource_slot;
}

fn isSweepRemoval(
    context: *const state.Context,
    slot: u32,
    now_timestamp: u64,
) bool {
    const current_state = taskState(context, slot);
    if (current_state == .free) return false;
    const resource_slot = context.column(u32, context.layout.tasks.resource_slot)[slot];
    const resource_generation =
        context.column(u64, context.layout.tasks.resource_generation)[slot];
    const stale_resource = resource_slot >= context.header.resource_capacity or
        context.resourceControl(resource_slot) != .active or
        context.column(u64, context.layout.resources.generation)[resource_slot] !=
            resource_generation;
    const deadline = if (current_state == .queued)
        context.column(u64, context.layout.tasks.queue_deadline_timestamp)[slot]
    else
        context.column(u64, context.layout.tasks.grant_deadline_timestamp)[slot];
    return stale_resource or deadline <= now_timestamp;
}

fn expireQueuedForResource(context: *state.Context, resource_slot: u32, now_timestamp: u64) void {
    var slot = context.column(u32, context.layout.resources.queue_head)[resource_slot];
    while (slot != types.none_slot) {
        const next = context.column(u32, context.layout.tasks.next)[slot];
        if (context.column(u64, context.layout.tasks.queue_deadline_timestamp)[slot] <= now_timestamp) {
            removeFromQueue(context, resource_slot, slot);
            const counts = context.column(u32, context.layout.resources.queued_request_count);
            if (counts[resource_slot] > 0) counts[resource_slot] -= 1;
            releaseTaskSlot(context, slot);
        }
        slot = next;
    }
}

fn removeTaskProtection(
    context: *state.Context,
    resource_slot: u32,
    task_slot: u32,
    current_state: types.TaskState,
) void {
    switch (current_state) {
        .queued => {
            removeFromQueue(context, resource_slot, task_slot);
            const counts = context.column(u32, context.layout.resources.queued_request_count);
            if (counts[resource_slot] > 0) counts[resource_slot] -= 1;
        },
        .reserved => {
            const counts = context.column(u32, context.layout.resources.reserved_grant_count);
            if (counts[resource_slot] > 0) counts[resource_slot] -= 1;
        },
        .active => {
            const counts = context.column(u32, context.layout.resources.active_use_count);
            if (counts[resource_slot] > 0) counts[resource_slot] -= 1;
        },
        .free => {},
    }
}

fn findDuplicate(context: *const state.Context, request: *const types.UseRequest) ?u32 {
    var slot: u32 = 0;
    while (slot < context.header.task_capacity) : (slot += 1) {
        if (taskState(context, slot) == .free) continue;
        if (context.column(u32, context.layout.tasks.resource_slot)[slot] == request.resource.resource_slot and
            context.column(u64, context.layout.tasks.resource_generation)[slot] == request.resource.resource_generation and
            context.column(u64, context.layout.tasks.request_key)[slot] == request.request_key and
            context.column(u64, context.layout.tasks.requester_application_key)[slot] == request.requester_application_key and
            context.column(u64, context.layout.tasks.requester_instance_id)[slot] == request.requester_instance_id and
            context.column(u64, context.layout.tasks.requester_session_id)[slot] == request.requester_session_id)
        {
            return slot;
        }
    }
    return null;
}

fn takeFreeTask(context: *state.Context) ?u32 {
    while (context.header.task_free_head != types.none_slot) {
        const slot = context.header.task_free_head;
        context.header.task_free_head = context.column(u32, context.layout.tasks.free_next)[slot];
        if (context.column(u64, context.layout.tasks.generation)[slot] != std.math.maxInt(u64)) {
            return slot;
        }
    }
    return null;
}

fn findFreeTask(context: *const state.Context) ?u32 {
    var slot = context.header.task_free_head;
    while (slot != types.none_slot) {
        if (context.column(u64, context.layout.tasks.generation)[slot] !=
            std.math.maxInt(u64))
        {
            return slot;
        }
        slot = context.column(u32, context.layout.tasks.free_next)[slot];
    }
    return null;
}

fn writePublicTask(
    context: *state.Context,
    slot: u32,
    request: *const types.UseRequest,
    sequence: u64,
) void {
    context.column(u32, context.layout.tasks.resource_slot)[slot] = request.resource.resource_slot;
    context.column(u64, context.layout.tasks.resource_generation)[slot] = request.resource.resource_generation;
    context.column(u64, context.layout.tasks.request_key)[slot] = request.request_key;
    context.column(u64, context.layout.tasks.requester_application_key)[slot] = request.requester_application_key;
    context.column(u64, context.layout.tasks.requester_instance_id)[slot] = request.requester_instance_id;
    context.column(u64, context.layout.tasks.requester_session_id)[slot] = request.requester_session_id;
    context.column(i32, context.layout.tasks.requester_process_id)[slot] = request.requester_process_id;
    context.column(u64, context.layout.tasks.score_plan_generation)[slot] = request.score_plan_generation;
    context.column(u64, context.layout.tasks.queue_deadline_timestamp)[slot] =
        request.queue_lease_duration;
    context.column(f64, context.layout.tasks.base_score)[slot] = request.base_score;
    context.column(u64, context.layout.tasks.enqueue_sequence)[slot] = sequence;
    context.column(u32, context.layout.tasks.previous)[slot] = types.none_slot;
    context.column(u32, context.layout.tasks.next)[slot] = types.none_slot;
    context.column(u64, context.layout.tasks.grant_deadline_timestamp)[slot] = 0;
    context.column(u64, context.layout.tasks.payload_generation)[slot] = 0;
}

fn insertSorted(context: *state.Context, resource_slot: u32, task_slot: u32) void {
    const heads = context.column(u32, context.layout.resources.queue_head);
    const tails = context.column(u32, context.layout.resources.queue_tail);
    var current = heads[resource_slot];
    const new_score = context.column(f64, context.layout.tasks.base_score)[task_slot];
    const new_sequence = context.column(u64, context.layout.tasks.enqueue_sequence)[task_slot];
    while (current != types.none_slot) {
        const current_score = context.column(f64, context.layout.tasks.base_score)[current];
        const current_sequence = context.column(u64, context.layout.tasks.enqueue_sequence)[current];
        if (new_score > current_score or (new_score == current_score and new_sequence < current_sequence)) break;
        current = context.column(u32, context.layout.tasks.next)[current];
    }

    if (current == types.none_slot) {
        const previous = tails[resource_slot];
        context.column(u32, context.layout.tasks.previous)[task_slot] = previous;
        if (previous == types.none_slot) {
            heads[resource_slot] = task_slot;
        } else {
            context.column(u32, context.layout.tasks.next)[previous] = task_slot;
        }
        tails[resource_slot] = task_slot;
        return;
    }

    const previous = context.column(u32, context.layout.tasks.previous)[current];
    context.column(u32, context.layout.tasks.next)[task_slot] = current;
    context.column(u32, context.layout.tasks.previous)[task_slot] = previous;
    context.column(u32, context.layout.tasks.previous)[current] = task_slot;
    if (previous == types.none_slot) {
        heads[resource_slot] = task_slot;
    } else {
        context.column(u32, context.layout.tasks.next)[previous] = task_slot;
    }
}

fn removeFromQueue(context: *state.Context, resource_slot: u32, task_slot: u32) void {
    const previous = context.column(u32, context.layout.tasks.previous)[task_slot];
    const next = context.column(u32, context.layout.tasks.next)[task_slot];
    if (previous == types.none_slot) {
        context.column(u32, context.layout.resources.queue_head)[resource_slot] = next;
    } else {
        context.column(u32, context.layout.tasks.next)[previous] = next;
    }
    if (next == types.none_slot) {
        context.column(u32, context.layout.resources.queue_tail)[resource_slot] = previous;
    } else {
        context.column(u32, context.layout.tasks.previous)[next] = previous;
    }
    context.column(u32, context.layout.tasks.previous)[task_slot] = types.none_slot;
    context.column(u32, context.layout.tasks.next)[task_slot] = types.none_slot;
}

fn releaseTaskSlot(context: *state.Context, slot: u32) void {
    context.column(u8, context.layout.tasks.state)[slot] = @intFromEnum(types.TaskState.free);
    context.column(u32, context.layout.tasks.previous)[slot] = types.none_slot;
    context.column(u32, context.layout.tasks.next)[slot] = types.none_slot;
    context.column(u64, context.layout.tasks.grant_deadline_timestamp)[slot] = 0;
    if (context.column(u64, context.layout.tasks.generation)[slot] != std.math.maxInt(u64)) {
        context.column(u32, context.layout.tasks.free_next)[slot] = context.header.task_free_head;
        context.header.task_free_head = slot;
    }
}

fn fillTaskReceipt(context: *const state.Context, slot: u32, output: *types.TaskReceipt) void {
    const resource_slot = context.column(u32, context.layout.tasks.resource_slot)[slot];
    output.* = std.mem.zeroes(types.TaskReceipt);
    output.resource = resource_ops.resourceRef(context, resource_slot);
    output.request_key = context.column(u64, context.layout.tasks.request_key)[slot];
    output.requester_application_key = context.column(u64, context.layout.tasks.requester_application_key)[slot];
    output.requester_instance_id = context.column(u64, context.layout.tasks.requester_instance_id)[slot];
    output.requester_session_id = context.column(u64, context.layout.tasks.requester_session_id)[slot];
    output.task_generation = context.column(u64, context.layout.tasks.generation)[slot];
    output.task_slot = slot;
    output.state = context.column(u8, context.layout.tasks.state)[slot];
}

fn fillGrantReceipt(context: *const state.Context, slot: u32, output: *types.GrantReceipt) void {
    output.* = std.mem.zeroes(types.GrantReceipt);
    fillTaskReceipt(context, slot, &output.task);
    output.grant_generation = context.column(u64, context.layout.tasks.grant_generation)[slot];
    output.payload_generation = context.column(u64, context.layout.tasks.payload_generation)[slot];
    output.deadline_timestamp = context.column(u64, context.layout.tasks.grant_deadline_timestamp)[slot];
}

fn taskReceiptMatches(context: *const state.Context, slot: u32, receipt: *const types.TaskReceipt) bool {
    if (!context.resourceMatches(receipt.resource) or
        slot >= context.header.task_capacity or
        taskState(context, slot) == .free)
    {
        return false;
    }
    return context.column(u64, context.layout.tasks.generation)[slot] == receipt.task_generation and
        context.column(u32, context.layout.tasks.resource_slot)[slot] == receipt.resource.resource_slot and
        context.column(u64, context.layout.tasks.resource_generation)[slot] == receipt.resource.resource_generation and
        context.column(u64, context.layout.tasks.request_key)[slot] == receipt.request_key and
        context.column(u64, context.layout.tasks.requester_application_key)[slot] == receipt.requester_application_key and
        context.column(u64, context.layout.tasks.requester_instance_id)[slot] == receipt.requester_instance_id and
        context.column(u64, context.layout.tasks.requester_session_id)[slot] == receipt.requester_session_id;
}

fn grantMatches(
    context: *const state.Context,
    slot: u32,
    receipt: *const types.GrantReceipt,
    expected_state: types.TaskState,
) bool {
    if (!taskReceiptMatches(context, slot, &receipt.task) or
        taskState(context, slot) != expected_state)
    {
        return false;
    }
    const resource_slot = receipt.task.resource.resource_slot;
    return context.resourceMatches(receipt.task.resource) and
        context.column(u64, context.layout.tasks.grant_generation)[slot] == receipt.grant_generation and
        context.column(u64, context.layout.tasks.payload_generation)[slot] == receipt.payload_generation and
        context.column(u64, context.layout.resources.payload_generation)[resource_slot] == receipt.payload_generation;
}

fn taskState(context: *const state.Context, slot: u32) types.TaskState {
    return @enumFromInt(context.column(u8, context.layout.tasks.state)[slot]);
}

fn nextGateEpochBy(
    context: *const state.Context,
    resource_slot: u32,
    increment_count: u64,
) !u64 {
    const epochs = context.column(u64, context.layout.resources.gate_epoch);
    return std.math.add(u64, epochs[resource_slot], increment_count) catch
        error.CapacityExhausted;
}

fn commitGateEpoch(
    context: *state.Context,
    resource_slot: u32,
    next_gate_epoch: u64,
) void {
    const epochs = context.column(u64, context.layout.resources.gate_epoch);
    std.debug.assert(next_gate_epoch > epochs[resource_slot]);
    epochs[resource_slot] = next_gate_epoch;
}

fn countExpiredQueuedForResource(
    context: *const state.Context,
    resource_slot: u32,
    now_timestamp: u64,
) u64 {
    var count: u64 = 0;
    var slot = context.column(u32, context.layout.resources.queue_head)[resource_slot];
    while (slot != types.none_slot) {
        if (context.column(u64, context.layout.tasks.queue_deadline_timestamp)[slot] <=
            now_timestamp)
        {
            count += 1;
        }
        slot = context.column(u32, context.layout.tasks.next)[slot];
    }
    return count;
}

fn firstLiveQueuedForResource(
    context: *const state.Context,
    resource_slot: u32,
    now_timestamp: u64,
) u32 {
    var slot = context.column(u32, context.layout.resources.queue_head)[resource_slot];
    while (slot != types.none_slot) {
        if (context.column(u64, context.layout.tasks.queue_deadline_timestamp)[slot] >
            now_timestamp)
        {
            return slot;
        }
        slot = context.column(u32, context.layout.tasks.next)[slot];
    }
    return types.none_slot;
}
