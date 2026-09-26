const std = @import("std");
const state = @import("state.zig");
const types = state.types;

pub fn publish(
    context: *state.Context,
    publication: *const types.ResourcePublication,
    output: *types.ResourceRef,
) !void {
    if (!types.validPublication(publication)) return error.InvalidArgument;
    var guard = try context.acquire();
    defer guard.deinit();
    if (guard.abandoned or context.consistencyState() != .stable) return error.InconsistentState;
    const plan = try preparePublishLocked(context, publication);
    var mutation = try context.beginMutation();
    defer mutation.finish();
    commitPublishLocked(context, publication, plan, output);
}

pub fn update(
    context: *state.Context,
    resource: types.ResourceRef,
    publication: *const types.ResourcePublication,
) !void {
    if (!types.validPublication(publication)) return error.InvalidArgument;
    var guard = try context.acquire();
    defer guard.deinit();
    if (guard.abandoned or context.consistencyState() != .stable) return error.InconsistentState;
    if (!context.resourceMatches(resource)) return error.StaleReference;
    const slot = resource.resource_slot;
    if (context.destructiveActive(slot)) return error.ResourceBusy;
    if (context.column(u8, context.layout.resources.availability)[slot] !=
        @intFromEnum(types.Availability.available) or
        publication.availability != @intFromEnum(types.Availability.available))
    {
        return error.Conflict;
    }
    if (publication.public_resource_id != 0 and publication.public_resource_id != resource.public_resource_id) {
        return error.Conflict;
    }
    if (context.column(u64, context.layout.resources.adapter_key)[slot] != publication.adapter_key or
        (context.column(u8, context.layout.resources.flags)[slot] & types.gpu_backed_flag != 0) !=
            (publication.flags & types.gpu_backed_flag != 0))
    {
        return error.Conflict;
    }
    const payload_changed =
        context.column(u64, context.layout.resources.payload_mapping_id)[slot] != publication.payload_mapping_id or
        context.column(u64, context.layout.resources.payload_generation)[slot] != publication.payload_generation;
    if (payload_changed and context.hardProtectionCount(slot) != 0) return error.ResourceBusy;
    const current_grants = context.column(u32, context.layout.resources.reserved_grant_count)[slot] +
        context.column(u32, context.layout.resources.active_use_count)[slot];
    if (publication.max_parallel_grants < current_grants) return error.ResourceBusy;
    const scheduling_changed = schedulingFactsChanged(context, slot, publication);
    const next_scheduling_revision = if (scheduling_changed)
        types.nextGeneration(
            context.column(u64, context.layout.resources.scheduling_revision)[slot],
        ) orelse return error.CapacityExhausted
    else
        0;
    const next_topology_generation = if (scheduling_changed)
        try context.nextTopologyGeneration()
    else
        0;

    var mutation = try context.beginMutation();
    defer mutation.finish();
    writePublication(context, slot, publication, resource.public_resource_id);
    if (scheduling_changed) {
        context.column(u64, context.layout.resources.scheduling_revision)[slot] =
            next_scheduling_revision;
        context.header.topology_generation = next_topology_generation;
    }
}

pub fn revoke(context: *state.Context, resource: types.ResourceRef) !void {
    var guard = try context.acquire();
    defer guard.deinit();
    if (guard.abandoned or context.consistencyState() != .stable) return error.InconsistentState;
    if (!context.resourceMatches(resource)) return error.StaleReference;
    const slot = resource.resource_slot;
    if (context.column(u32, context.layout.resources.active_subscriber_count)[slot] != 0 or
        context.hardProtectionCount(slot) != 0 or
        context.destructiveActive(slot))
    {
        return error.ResourceBusy;
    }
    _ = try context.nextTopologyGeneration();

    var mutation = try context.beginMutation();
    defer mutation.finish();
    try recycleLocked(context, slot);
}

pub fn snapshotOne(
    context: *state.Context,
    resource: types.ResourceRef,
    output: *types.ResourceSnapshot,
) !void {
    if (!context.writable) return snapshotOneReadOnly(context, resource, output);
    var guard = try context.acquire();
    defer guard.deinit();
    if (guard.abandoned or context.consistencyState() != .stable) return error.InconsistentState;
    if (!context.resourceMatches(resource)) return error.StaleReference;
    fillSnapshot(context, resource.resource_slot, output);
}

pub fn snapshotByPublicId(
    context: *state.Context,
    public_resource_id: u64,
    output: *types.ResourceSnapshot,
) !void {
    if (public_resource_id == 0) return error.InvalidArgument;
    if (!context.writable) return snapshotByPublicIdReadOnly(context, public_resource_id, output);
    var guard = try context.acquire();
    defer guard.deinit();
    if (guard.abandoned or context.consistencyState() != .stable) return error.InconsistentState;

    var slot: u32 = 0;
    while (slot < context.header.resource_capacity) : (slot += 1) {
        if (context.resourceControl(slot) == .active and
            context.column(u64, context.layout.resources.public_resource_id)[slot] == public_resource_id)
        {
            fillSnapshot(context, slot, output);
            return;
        }
    }
    return error.NotFound;
}

pub fn snapshotBatch(
    context: *state.Context,
    output: [*]types.ResourceSnapshot,
    capacity: u32,
    output_count: *u32,
    topology_generation: *u64,
) !void {
    if (!context.writable) return snapshotBatchReadOnly(context, output, capacity, output_count, topology_generation);
    var guard = try context.acquire();
    defer guard.deinit();
    if (guard.abandoned or context.consistencyState() != .stable) return error.InconsistentState;

    var count: u32 = 0;
    var slot: u32 = 0;
    while (slot < context.header.resource_capacity) : (slot += 1) {
        if (context.resourceControl(slot) != .active) continue;
        if (count == capacity) return error.BufferTooSmall;
        fillSnapshot(context, slot, &output[count]);
        count += 1;
    }
    output_count.* = count;
    topology_generation.* = context.header.topology_generation;
}

fn snapshotOneReadOnly(
    context: *const state.Context,
    resource: types.ResourceRef,
    output: *types.ResourceSnapshot,
) !void {
    var attempt: u8 = 0;
    while (attempt < 8) : (attempt += 1) {
        const sequence = try context.beginStableRead();
        const matches = context.resourceMatches(resource);
        var snapshot = std.mem.zeroes(types.ResourceSnapshot);
        if (matches) fillSnapshot(context, resource.resource_slot, &snapshot);
        if (!context.stableReadFinished(sequence)) continue;
        if (!matches) return error.StaleReference;
        output.* = snapshot;
        return;
    }
    return error.SynchronizationFailed;
}

fn snapshotByPublicIdReadOnly(
    context: *const state.Context,
    public_resource_id: u64,
    output: *types.ResourceSnapshot,
) !void {
    var attempt: u8 = 0;
    while (attempt < 8) : (attempt += 1) {
        const sequence = try context.beginStableRead();
        var found = false;
        var snapshot = std.mem.zeroes(types.ResourceSnapshot);
        var slot: u32 = 0;
        while (slot < context.header.resource_capacity) : (slot += 1) {
            if (context.column(u8, context.layout.resources.control)[slot] == @intFromEnum(types.ResourceControl.active) and
                context.column(u64, context.layout.resources.public_resource_id)[slot] == public_resource_id)
            {
                fillSnapshot(context, slot, &snapshot);
                found = true;
                break;
            }
        }
        if (!context.stableReadFinished(sequence)) continue;
        if (!found) return error.NotFound;
        output.* = snapshot;
        return;
    }
    return error.SynchronizationFailed;
}

fn snapshotBatchReadOnly(
    context: *const state.Context,
    output: [*]types.ResourceSnapshot,
    capacity: u32,
    output_count: *u32,
    topology_generation: *u64,
) !void {
    var attempt: u8 = 0;
    while (attempt < 8) : (attempt += 1) {
        const sequence = try context.beginStableRead();
        const generation = context.header.topology_generation;
        var count: u32 = 0;
        var overflow = false;
        var slot: u32 = 0;
        while (slot < context.header.resource_capacity) : (slot += 1) {
            if (context.column(u8, context.layout.resources.control)[slot] != @intFromEnum(types.ResourceControl.active)) continue;
            if (count == capacity) {
                overflow = true;
                break;
            }
            fillSnapshot(context, slot, &output[count]);
            count += 1;
        }
        if (!context.stableReadFinished(sequence)) continue;
        if (overflow) return error.BufferTooSmall;
        output_count.* = count;
        topology_generation.* = generation;
        return;
    }
    return error.SynchronizationFailed;
}

pub fn resourceRef(context: *const state.Context, slot: u32) types.ResourceRef {
    return .{
        .ledger_instance_id = context.header.ledger_instance_id,
        .resource_generation = context.column(u64, context.layout.resources.generation)[slot],
        .public_resource_id = context.column(u64, context.layout.resources.public_resource_id)[slot],
        .resource_slot = slot,
        .reserved = 0,
    };
}

pub fn allowedActions(context: *const state.Context, slot: u32, requested: u8) u8 {
    var allowed = requested & types.all_action_bits &
        ~context.column(u8, context.layout.resources.inapplicable_actions)[slot];
    const owner_demand = context.column(u8, context.layout.resources.owner_demand_mask)[slot];
    const unavailable = context.column(u8, context.layout.resources.availability)[slot] !=
        @intFromEnum(types.Availability.available);
    if (context.consistencyState() != .stable or
        unavailable or
        context.hardProtectionCount(slot) != 0 or
        context.destructiveActive(slot) or
        (owner_demand & 0b0000_0001) != 0)
    {
        allowed &= ~types.destructive_action_bits;
    }
    return allowed;
}

fn fillSnapshot(context: *const state.Context, slot: u32, output: *types.ResourceSnapshot) void {
    output.* = std.mem.zeroes(types.ResourceSnapshot);
    output.abi_version = types.abi_version;
    output.struct_size = @sizeOf(types.ResourceSnapshot);
    output.id = resourceRef(context, slot);
    output.owner_application_key = context.column(u64, context.layout.resources.owner_application_key)[slot];
    output.owner_instance_id = context.column(u64, context.layout.resources.owner_instance_id)[slot];
    output.owner_instance_id_high = context.column(u64, context.layout.resources.owner_instance_id_high)[slot];
    output.owner_context_generation = context.column(u64, context.layout.resources.owner_context_generation)[slot];
    output.lease_generation = context.column(u64, context.layout.resources.lease_generation)[slot];
    output.binding_generation = context.column(u64, context.layout.resources.binding_generation)[slot];
    output.capability_generation = context.column(u64, context.layout.resources.capability_generation)[slot];
    output.executor_id_low = context.column(u64, context.layout.resources.executor_id_low)[slot];
    output.executor_id_high = context.column(u64, context.layout.resources.executor_id_high)[slot];
    output.resource_key = context.column(u64, context.layout.resources.resource_key)[slot];
    output.adapter_key = context.column(u64, context.layout.resources.adapter_key)[slot];
    output.size_bytes = context.column(u64, context.layout.resources.size_bytes)[slot];
    output.content_identity_hash = context.column(u64, context.layout.resources.content_identity_hash)[slot];
    output.payload_mapping_id = context.column(u64, context.layout.resources.payload_mapping_id)[slot];
    output.payload_generation = context.column(u64, context.layout.resources.payload_generation)[slot];
    output.last_updated_utc_ticks = context.column(i64, context.layout.resources.last_updated_utc_ticks)[slot];
    output.subscription_multiplier = context.column(f64, context.layout.resources.subscription_multiplier)[slot];
    output.owner_process_id = context.column(i32, context.layout.resources.owner_process_id)[slot];
    output.resource_id = context.column(u32, context.layout.resources.resource_id)[slot];
    output.active_subscriber_count = context.column(u32, context.layout.resources.active_subscriber_count)[slot];
    output.queued_request_count = context.column(u32, context.layout.resources.queued_request_count)[slot];
    output.reserved_grant_count = context.column(u32, context.layout.resources.reserved_grant_count)[slot];
    output.active_use_count = context.column(u32, context.layout.resources.active_use_count)[slot];
    output.scheduling_revision = context.column(u64, context.layout.resources.scheduling_revision)[slot];
    output.gate_epoch = context.column(u64, context.layout.resources.gate_epoch)[slot];
    output.max_parallel_grants = context.column(u16, context.layout.resources.max_parallel_grants)[slot];
    output.tier = context.column(u8, context.layout.resources.tier)[slot];
    output.resource_kind = context.column(u8, context.layout.resources.resource_kind)[slot];
    output.recovery_kind = context.column(u8, context.layout.resources.recovery_kind)[slot];
    output.granularity = context.column(u8, context.layout.resources.granularity)[slot];
    output.inapplicable_actions = context.column(u8, context.layout.resources.inapplicable_actions)[slot];
    output.action_route = context.column(u8, context.layout.resources.action_route)[slot];
    output.owner_demand_mask = context.column(u8, context.layout.resources.owner_demand_mask)[slot];
    output.subscription_intent_mask = context.column(u8, context.layout.resources.subscription_intent_mask)[slot];
    output.activity_score = context.column(u8, context.layout.resources.activity_score)[slot];
    output.surface_state = context.column(u8, context.layout.resources.surface_state)[slot];
    output.availability = context.column(u8, context.layout.resources.availability)[slot];
    output.flags = context.column(u8, context.layout.resources.flags)[slot];
    output.allowed_actions = allowedActions(context, slot, types.all_action_bits);
    output.consistency_state = @intFromEnum(context.consistencyState());
    output.destructive_active = @intFromBool(context.destructiveActive(slot));
}

fn clearResourceState(context: *state.Context, slot: u32) void {
    context.column(u32, context.layout.resources.active_subscriber_count)[slot] = 0;
    context.column(u8, context.layout.resources.subscription_intent_mask)[slot] = 0;
    context.column(u32, context.layout.resources.intent_ready_soon_count)[slot] = 0;
    context.column(u32, context.layout.resources.intent_preload_eager_count)[slot] = 0;
    context.column(u32, context.layout.resources.intent_preload_opportunistic_count)[slot] = 0;
    context.column(u32, context.layout.resources.queued_request_count)[slot] = 0;
    context.column(u32, context.layout.resources.reserved_grant_count)[slot] = 0;
    context.column(u32, context.layout.resources.active_use_count)[slot] = 0;
    context.column(u8, context.layout.resources.activity_score)[slot] = 0;
    context.column(u64, context.layout.resources.scheduling_revision)[slot] = 0;
    context.column(u64, context.layout.resources.gate_epoch)[slot] = 1;
    context.column(u8, context.layout.resources.destructive_active)[slot] = 0;
    context.column(u8, context.layout.resources.destructive_action)[slot] = 0;
    context.column(u64, context.layout.resources.destructive_attempt_id_low)[slot] = 0;
    context.column(u64, context.layout.resources.destructive_attempt_id_high)[slot] = 0;
    context.column(u32, context.layout.resources.queue_head)[slot] = types.none_slot;
    context.column(u32, context.layout.resources.queue_tail)[slot] = types.none_slot;
    context.column(f64, context.layout.resources.subscription_multiplier)[slot] = 1.0;
}

pub fn writePublication(
    context: *state.Context,
    slot: u32,
    publication: *const types.ResourcePublication,
    public_id: u64,
) void {
    context.column(u64, context.layout.resources.public_resource_id)[slot] = public_id;
    context.column(u64, context.layout.resources.owner_application_key)[slot] = publication.owner_application_key;
    context.column(u64, context.layout.resources.owner_instance_id)[slot] = publication.owner_instance_id;
    context.column(u64, context.layout.resources.owner_instance_id_high)[slot] = publication.owner_instance_id_high;
    context.column(u64, context.layout.resources.owner_context_generation)[slot] = publication.owner_context_generation;
    context.column(u64, context.layout.resources.lease_generation)[slot] = publication.lease_generation;
    context.column(u64, context.layout.resources.binding_generation)[slot] = publication.binding_generation;
    context.column(u64, context.layout.resources.capability_generation)[slot] = publication.capability_generation;
    context.column(u64, context.layout.resources.executor_id_low)[slot] = publication.executor_id_low;
    context.column(u64, context.layout.resources.executor_id_high)[slot] = publication.executor_id_high;
    context.column(i32, context.layout.resources.owner_process_id)[slot] = publication.owner_process_id;
    context.column(u64, context.layout.resources.resource_key)[slot] = publication.resource_key;
    context.column(u64, context.layout.resources.adapter_key)[slot] = publication.adapter_key;
    context.column(u32, context.layout.resources.resource_id)[slot] = publication.resource_id;
    context.column(u64, context.layout.resources.size_bytes)[slot] = publication.size_bytes;
    context.column(u64, context.layout.resources.content_identity_hash)[slot] = publication.content_identity_hash;
    context.column(u64, context.layout.resources.payload_mapping_id)[slot] = publication.payload_mapping_id;
    context.column(u64, context.layout.resources.payload_generation)[slot] = publication.payload_generation;
    context.column(i64, context.layout.resources.last_updated_utc_ticks)[slot] = publication.last_updated_utc_ticks;
    context.column(u16, context.layout.resources.max_parallel_grants)[slot] = publication.max_parallel_grants;
    context.column(u8, context.layout.resources.tier)[slot] = publication.tier;
    context.column(u8, context.layout.resources.resource_kind)[slot] = publication.resource_kind;
    context.column(u8, context.layout.resources.recovery_kind)[slot] = publication.recovery_kind;
    context.column(u8, context.layout.resources.granularity)[slot] = publication.granularity;
    context.column(u8, context.layout.resources.inapplicable_actions)[slot] = publication.inapplicable_actions;
    context.column(u8, context.layout.resources.action_route)[slot] = publication.action_route;
    context.column(u8, context.layout.resources.owner_demand_mask)[slot] = publication.owner_demand_mask;
    context.column(u8, context.layout.resources.surface_state)[slot] = publication.surface_state;
    context.column(u8, context.layout.resources.availability)[slot] = publication.availability;
    context.column(u8, context.layout.resources.flags)[slot] = publication.flags;
}

pub const PublishPlan = struct {
    selected_slot: u32,
    next_resource_generation: u64,
    next_topology_generation: u64,
    public_id_selection: PublicResourceIdSelection,
};

pub fn preparePublishLocked(
    context: *const state.Context,
    publication: *const types.ResourcePublication,
) !PublishPlan {
    const next_topology_generation = try context.nextTopologyGeneration();
    const selected_slot = findReusableResourceSlot(context) orelse
        return error.CapacityExhausted;
    const generations = context.column(u64, context.layout.resources.generation);
    const next_resource_generation = types.nextGeneration(generations[selected_slot]) orelse
        unreachable;
    const public_id_selection = if (publication.public_resource_id == 0)
        try selectPublicResourceId(context)
    else
        PublicResourceIdSelection{
            .public_id = publication.public_resource_id,
            .allocator_sequence = context.header.next_public_resource_id,
            .advances_allocator = false,
        };
    const public_id = public_id_selection.public_id;
    if (containsPublicResourceId(context, public_id)) return error.Conflict;
    return .{
        .selected_slot = selected_slot,
        .next_resource_generation = next_resource_generation,
        .next_topology_generation = next_topology_generation,
        .public_id_selection = public_id_selection,
    };
}

pub fn commitPublishLocked(
    context: *state.Context,
    publication: *const types.ResourcePublication,
    plan: PublishPlan,
    output: *types.ResourceRef,
) void {
    const selected_slot = plan.selected_slot;
    const public_id_selection = plan.public_id_selection;
    const public_id = public_id_selection.public_id;
    const generations = context.column(u64, context.layout.resources.generation);
    context.header.resource_free_head =
        context.column(u32, context.layout.resources.free_next)[selected_slot];
    if (public_id_selection.advances_allocator) {
        context.header.next_public_resource_id = public_id_selection.allocator_sequence;
    }
    generations[selected_slot] = plan.next_resource_generation;
    clearResourceState(context, selected_slot);
    writePublication(context, selected_slot, publication, public_id);
    context.column(u64, context.layout.resources.scheduling_revision)[selected_slot] = 1;
    context.column(u8, context.layout.resources.control)[selected_slot] =
        @intFromEnum(types.ResourceControl.active);
    context.header.topology_generation = plan.next_topology_generation;
    output.* = resourceRef(context, selected_slot);
}

pub fn recycleLocked(context: *state.Context, slot: u32) !void {
    const next_topology_generation = try context.nextTopologyGeneration();
    context.column(u8, context.layout.resources.availability)[slot] = @intFromEnum(types.Availability.unavailable);
    context.column(u8, context.layout.resources.control)[slot] = @intFromEnum(types.ResourceControl.free);
    context.column(u32, context.layout.resources.active_subscriber_count)[slot] = 0;
    context.column(u8, context.layout.resources.subscription_intent_mask)[slot] = 0;
    context.column(f64, context.layout.resources.subscription_multiplier)[slot] = 1.0;
    if (context.column(u64, context.layout.resources.generation)[slot] != std.math.maxInt(u64)) {
        context.column(u32, context.layout.resources.free_next)[slot] = context.header.resource_free_head;
        context.header.resource_free_head = slot;
    }
    context.header.topology_generation = next_topology_generation;
}

pub fn bumpSchedulingRevision(context: *state.Context, slot: u32) !void {
    const revisions = context.column(u64, context.layout.resources.scheduling_revision);
    revisions[slot] = types.nextGeneration(revisions[slot]) orelse
        return error.CapacityExhausted;
}

fn schedulingFactsChanged(
    context: *const state.Context,
    slot: u32,
    publication: *const types.ResourcePublication,
) bool {
    return context.column(u64, context.layout.resources.owner_application_key)[slot] != publication.owner_application_key or
        context.column(u64, context.layout.resources.owner_instance_id)[slot] != publication.owner_instance_id or
        context.column(u64, context.layout.resources.owner_instance_id_high)[slot] != publication.owner_instance_id_high or
        context.column(u64, context.layout.resources.owner_context_generation)[slot] != publication.owner_context_generation or
        context.column(u64, context.layout.resources.lease_generation)[slot] != publication.lease_generation or
        context.column(u64, context.layout.resources.binding_generation)[slot] != publication.binding_generation or
        context.column(u64, context.layout.resources.capability_generation)[slot] != publication.capability_generation or
        context.column(u64, context.layout.resources.executor_id_low)[slot] != publication.executor_id_low or
        context.column(u64, context.layout.resources.executor_id_high)[slot] != publication.executor_id_high or
        context.column(i32, context.layout.resources.owner_process_id)[slot] != publication.owner_process_id or
        context.column(u64, context.layout.resources.resource_key)[slot] != publication.resource_key or
        context.column(u64, context.layout.resources.adapter_key)[slot] != publication.adapter_key or
        context.column(u32, context.layout.resources.resource_id)[slot] != publication.resource_id or
        context.column(u64, context.layout.resources.size_bytes)[slot] != publication.size_bytes or
        context.column(u64, context.layout.resources.content_identity_hash)[slot] != publication.content_identity_hash or
        context.column(u64, context.layout.resources.payload_mapping_id)[slot] != publication.payload_mapping_id or
        context.column(u64, context.layout.resources.payload_generation)[slot] != publication.payload_generation or
        context.column(u16, context.layout.resources.max_parallel_grants)[slot] != publication.max_parallel_grants or
        context.column(u8, context.layout.resources.tier)[slot] != publication.tier or
        context.column(u8, context.layout.resources.resource_kind)[slot] != publication.resource_kind or
        context.column(u8, context.layout.resources.recovery_kind)[slot] != publication.recovery_kind or
        context.column(u8, context.layout.resources.granularity)[slot] != publication.granularity or
        context.column(u8, context.layout.resources.inapplicable_actions)[slot] != publication.inapplicable_actions or
        context.column(u8, context.layout.resources.action_route)[slot] != publication.action_route or
        context.column(u8, context.layout.resources.owner_demand_mask)[slot] != publication.owner_demand_mask or
        context.column(u8, context.layout.resources.surface_state)[slot] != publication.surface_state or
        context.column(u8, context.layout.resources.availability)[slot] != publication.availability or
        context.column(u8, context.layout.resources.flags)[slot] != publication.flags;
}

fn containsPublicResourceId(context: *const state.Context, public_id: u64) bool {
    var slot: u32 = 0;
    while (slot < context.header.resource_capacity) : (slot += 1) {
        if (context.resourceControl(slot) == .active and
            context.column(u64, context.layout.resources.public_resource_id)[slot] == public_id)
        {
            return true;
        }
    }
    return false;
}

pub const PublicResourceIdSelection = struct {
    public_id: u64,
    allocator_sequence: u64,
    advances_allocator: bool,
};

fn selectPublicResourceId(context: *const state.Context) !PublicResourceIdSelection {
    var allocator_sequence = context.header.next_public_resource_id;
    var attempts: u32 = 0;
    while (attempts < context.header.resource_capacity + 1) : (attempts += 1) {
        allocator_sequence = types.nextGeneration(allocator_sequence) orelse
            return error.CapacityExhausted;
        var candidate = context.header.ledger_instance_id *% 0x9e3779b97f4a7c15;
        candidate ^= allocator_sequence;
        if (candidate == 0) candidate = allocator_sequence;
        if (!containsPublicResourceId(context, candidate)) {
            return .{
                .public_id = candidate,
                .allocator_sequence = allocator_sequence,
                .advances_allocator = true,
            };
        }
    }
    return error.CapacityExhausted;
}

fn findReusableResourceSlot(context: *const state.Context) ?u32 {
    var slot = context.header.resource_free_head;
    while (slot != types.none_slot) {
        if (context.column(u64, context.layout.resources.generation)[slot] !=
            std.math.maxInt(u64))
        {
            return slot;
        }
        slot = context.column(u32, context.layout.resources.free_next)[slot];
    }
    return null;
}
