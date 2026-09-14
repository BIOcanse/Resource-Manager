const std = @import("std");
const wire = @import("layout.zig");
const types = @import("types.zig");

pub const View = struct {
    mapping: [*]u8,
    layout: wire.Layout,
    header: *const wire.Header,

    fn column(self: View, comptime T: type, offset: usize) [*]const T {
        return @ptrCast(@alignCast(self.mapping + offset));
    }
};

pub fn isRecoverable(view: View) bool {
    return validHeader(view) and
        validResourceFreeList(view) and
        validSubscriptionFreeList(view) and
        validTaskFreeList(view) and
        validResources(view) and
        validSubscriptions(view) and
        validTasks(view) and
        validResourceAggregates(view);
}

fn validHeader(view: View) bool {
    const header = view.header;
    if (header.magic != types.ledger_magic or
        header.version != types.abi_version or
        header.header_size != types.header_size or
        header.mapping_size != view.layout.required_size or
        header.ledger_instance_id == 0 or
        header.topology_generation == 0 or
        header.mapping_epoch == 0 or
        (header.mutation_sequence & 1) != 0 or
        header.synchronization_kind > @intFromEnum(types.SynchronizationKind.windows_mutex) or
        header.consistency_state > @intFromEnum(types.ConsistencyState.quarantined) or
        header.resource_capacity == 0 or
        header.task_capacity == 0 or
        header.resource_columns_offset != view.layout.resources.control or
        header.subscription_columns_offset != view.layout.subscriptions.control or
        header.task_columns_offset != view.layout.tasks.state or
        !std.math.isFinite(header.subscription_coefficient) or
        header.subscription_coefficient < 0 or
        header.owner_application_key == 0 or
        !std.mem.allEqual(u8, header.reserved0[0..], 0) or
        header.reserved1 != 0 or
        header.reserved2 != 0)
    {
        return false;
    }
    for (header.reserved) |value| if (value != 0) return false;
    return true;
}

fn validResourceFreeList(view: View) bool {
    const capacity = view.header.resource_capacity;
    const controls = view.column(u8, view.layout.resources.control);
    const generations = view.column(u64, view.layout.resources.generation);
    const next = view.column(u32, view.layout.resources.free_next);
    return validFreeList(capacity, view.header.resource_free_head, controls, generations, next, @intFromEnum(types.ResourceControl.free));
}

fn validSubscriptionFreeList(view: View) bool {
    const capacity = view.header.subscription_capacity;
    const controls = view.column(u8, view.layout.subscriptions.control);
    const generations = view.column(u64, view.layout.subscriptions.generation);
    const next = view.column(u32, view.layout.subscriptions.free_next);
    return validFreeList(capacity, view.header.subscription_free_head, controls, generations, next, @intFromEnum(types.SubscriptionControl.free));
}

fn validTaskFreeList(view: View) bool {
    const capacity = view.header.task_capacity;
    const controls = view.column(u8, view.layout.tasks.state);
    const generations = view.column(u64, view.layout.tasks.generation);
    const next = view.column(u32, view.layout.tasks.free_next);
    return validFreeList(capacity, view.header.task_free_head, controls, generations, next, @intFromEnum(types.TaskState.free));
}

fn validFreeList(
    capacity: u32,
    head: u32,
    controls: [*]const u8,
    generations: [*]const u64,
    next: [*]const u32,
    free_value: u8,
) bool {
    if (head != types.none_slot and head >= capacity) return false;
    var listed: u32 = 0;
    var slot = head;
    while (slot != types.none_slot) {
        if (slot >= capacity or listed >= capacity or
            controls[slot] != free_value or
            generations[slot] == std.math.maxInt(u64))
        {
            return false;
        }
        listed += 1;
        slot = next[slot];
    }

    var expected: u32 = 0;
    slot = 0;
    while (slot < capacity) : (slot += 1) {
        if (controls[slot] == free_value and generations[slot] != std.math.maxInt(u64)) expected += 1;
    }
    return listed == expected;
}

fn validResources(view: View) bool {
    const capacity = view.header.resource_capacity;
    const controls = view.column(u8, view.layout.resources.control);
    var slot: u32 = 0;
    while (slot < capacity) : (slot += 1) {
        const control = controls[slot];
        if (control > @intFromEnum(types.ResourceControl.active)) return false;
        if (control == @intFromEnum(types.ResourceControl.free)) continue;
        if (!validActiveResource(view, slot)) return false;

        const public_id = view.column(u64, view.layout.resources.public_resource_id)[slot];
        var other = slot + 1;
        while (other < capacity) : (other += 1) {
            if (controls[other] == @intFromEnum(types.ResourceControl.active) and
                view.column(u64, view.layout.resources.public_resource_id)[other] == public_id)
            {
                return false;
            }
        }
    }
    return true;
}

fn validActiveResource(view: View, slot: u32) bool {
    const adapter_key = view.column(u64, view.layout.resources.adapter_key)[slot];
    const flags = view.column(u8, view.layout.resources.flags)[slot];
    if (view.column(u64, view.layout.resources.generation)[slot] == 0 or
        view.column(u64, view.layout.resources.public_resource_id)[slot] == 0 or
        view.column(u64, view.layout.resources.owner_application_key)[slot] == 0 or
        view.column(u64, view.layout.resources.owner_instance_id)[slot] == 0 or
        view.column(i32, view.layout.resources.owner_process_id)[slot] < 0 or
        view.column(u64, view.layout.resources.resource_key)[slot] == 0 or
        view.column(u32, view.layout.resources.resource_id)[slot] == 0 or
        view.column(u16, view.layout.resources.max_parallel_grants)[slot] == 0 or
        view.column(u8, view.layout.resources.tier)[slot] > 2 or
        view.column(u8, view.layout.resources.resource_kind)[slot] > 12 or
        view.column(u8, view.layout.resources.recovery_kind)[slot] > 2 or
        view.column(u8, view.layout.resources.granularity)[slot] > 2 or
        view.column(u8, view.layout.resources.action_route)[slot] > 1 or
        view.column(u8, view.layout.resources.availability)[slot] > @intFromEnum(types.Availability.unavailable) or
        view.column(u8, view.layout.resources.inapplicable_actions)[slot] & ~types.all_action_bits != 0 or
        view.column(u8, view.layout.resources.owner_demand_mask)[slot] & ~types.all_action_bits != 0 or
        flags & ~types.all_resource_flag_bits != 0 or
        ((flags & types.gpu_backed_flag != 0) != (adapter_key != 0)) or
        view.column(u8, view.layout.resources.destructive_active)[slot] > 1)
    {
        return false;
    }
    return true;
}

fn validSubscriptions(view: View) bool {
    const capacity = view.header.subscription_capacity;
    const controls = view.column(u8, view.layout.subscriptions.control);
    var slot: u32 = 0;
    while (slot < capacity) : (slot += 1) {
        const control = controls[slot];
        if (control > @intFromEnum(types.SubscriptionControl.active)) return false;
        if (control == @intFromEnum(types.SubscriptionControl.free)) continue;
        if (!validActiveSubscription(view, slot)) return false;
        var other = slot + 1;
        while (other < capacity) : (other += 1) {
            if (controls[other] == @intFromEnum(types.SubscriptionControl.active) and
                sameSubscriptionIdentity(view, slot, other))
            {
                return false;
            }
        }
    }
    return true;
}

fn validActiveSubscription(view: View, slot: u32) bool {
    const resource_slot = view.column(u32, view.layout.subscriptions.target_resource_slot)[slot];
    const intent = view.column(u8, view.layout.subscriptions.intent_mask)[slot];
    return view.column(u64, view.layout.subscriptions.generation)[slot] != 0 and
        resource_slot < view.header.resource_capacity and
        view.column(u8, view.layout.resources.control)[resource_slot] == @intFromEnum(types.ResourceControl.active) and
        view.column(u64, view.layout.subscriptions.target_resource_generation)[slot] ==
            view.column(u64, view.layout.resources.generation)[resource_slot] and
        view.column(u64, view.layout.subscriptions.subscriber_application_key)[slot] != 0 and
        view.column(u64, view.layout.subscriptions.subscriber_instance_id)[slot] != 0 and
        view.column(u64, view.layout.subscriptions.subscriber_session_id)[slot] != 0 and
        view.column(u64, view.layout.subscriptions.deadline_timestamp)[slot] != 0 and
        view.column(i32, view.layout.subscriptions.subscriber_process_id)[slot] >= 0 and
        intent != 0 and
        intent & ~types.subscription_intent_bits == 0;
}

fn sameSubscriptionIdentity(view: View, left: u32, right: u32) bool {
    return view.column(u32, view.layout.subscriptions.target_resource_slot)[left] ==
        view.column(u32, view.layout.subscriptions.target_resource_slot)[right] and
        view.column(u64, view.layout.subscriptions.target_resource_generation)[left] ==
            view.column(u64, view.layout.subscriptions.target_resource_generation)[right] and
        view.column(u64, view.layout.subscriptions.subscriber_application_key)[left] ==
            view.column(u64, view.layout.subscriptions.subscriber_application_key)[right] and
        view.column(u64, view.layout.subscriptions.subscriber_instance_id)[left] ==
            view.column(u64, view.layout.subscriptions.subscriber_instance_id)[right] and
        view.column(u64, view.layout.subscriptions.subscriber_session_id)[left] ==
            view.column(u64, view.layout.subscriptions.subscriber_session_id)[right];
}

fn validTasks(view: View) bool {
    const capacity = view.header.task_capacity;
    var slot: u32 = 0;
    while (slot < capacity) : (slot += 1) {
        const raw_state = view.column(u8, view.layout.tasks.state)[slot];
        if (raw_state > @intFromEnum(types.TaskState.active)) return false;
        const task_state: types.TaskState = @enumFromInt(raw_state);
        if (task_state == .free) {
            if (view.column(u32, view.layout.tasks.previous)[slot] != types.none_slot or
                view.column(u32, view.layout.tasks.next)[slot] != types.none_slot)
            {
                return false;
            }
            continue;
        }
        if (!validActiveTask(view, slot, task_state)) return false;
    }
    return true;
}

fn validActiveTask(view: View, slot: u32, task_state: types.TaskState) bool {
    const resource_slot = view.column(u32, view.layout.tasks.resource_slot)[slot];
    if (view.column(u64, view.layout.tasks.generation)[slot] == 0 or
        resource_slot >= view.header.resource_capacity or
        view.column(u8, view.layout.resources.control)[resource_slot] != @intFromEnum(types.ResourceControl.active) or
        view.column(u64, view.layout.tasks.resource_generation)[slot] !=
            view.column(u64, view.layout.resources.generation)[resource_slot])
    {
        return false;
    }
    if (view.column(u64, view.layout.tasks.request_key)[slot] == 0 or
        view.column(u64, view.layout.tasks.requester_application_key)[slot] == 0 or
        view.column(u64, view.layout.tasks.requester_instance_id)[slot] == 0 or
        view.column(u64, view.layout.tasks.requester_session_id)[slot] == 0 or
        view.column(i32, view.layout.tasks.requester_process_id)[slot] < 0 or
        view.column(u64, view.layout.tasks.queue_deadline_timestamp)[slot] == 0 or
        !std.math.isFinite(view.column(f64, view.layout.tasks.base_score)[slot]) or
        view.column(f64, view.layout.tasks.base_score)[slot] < 0 or
        view.column(u64, view.layout.tasks.enqueue_sequence)[slot] == 0 or
        view.column(u64, view.layout.tasks.enqueue_sequence)[slot] > view.header.enqueue_sequence)
    {
        return false;
    }
    return switch (task_state) {
        .queued => view.column(u64, view.layout.tasks.grant_deadline_timestamp)[slot] == 0,
        .reserved, .active => view.column(u64, view.layout.tasks.grant_generation)[slot] != 0 and
            view.column(u64, view.layout.tasks.grant_deadline_timestamp)[slot] != 0 and
            view.column(u32, view.layout.tasks.previous)[slot] == types.none_slot and
            view.column(u32, view.layout.tasks.next)[slot] == types.none_slot,
        .free => false,
    };
}

fn validResourceAggregates(view: View) bool {
    var resource_slot: u32 = 0;
    while (resource_slot < view.header.resource_capacity) : (resource_slot += 1) {
        if (view.column(u8, view.layout.resources.control)[resource_slot] != @intFromEnum(types.ResourceControl.active)) continue;
        if (!validSubscriptionAggregate(view, resource_slot) or
            !validTaskAggregate(view, resource_slot) or
            !validQueue(view, resource_slot))
        {
            return false;
        }
    }
    return true;
}

fn validSubscriptionAggregate(view: View, resource_slot: u32) bool {
    var count: u32 = 0;
    var ready: u32 = 0;
    var eager: u32 = 0;
    var opportunistic: u32 = 0;
    var slot: u32 = 0;
    while (slot < view.header.subscription_capacity) : (slot += 1) {
        if (view.column(u8, view.layout.subscriptions.control)[slot] != @intFromEnum(types.SubscriptionControl.active) or
            view.column(u32, view.layout.subscriptions.target_resource_slot)[slot] != resource_slot)
        {
            continue;
        }
        count += 1;
        const intent = view.column(u8, view.layout.subscriptions.intent_mask)[slot];
        if (intent & 0b0010 != 0) ready += 1;
        if (intent & 0b0100 != 0) eager += 1;
        if (intent & 0b1000 != 0) opportunistic += 1;
    }
    var aggregate_intent: u8 = 0;
    if (ready > 0) aggregate_intent |= 0b0010;
    if (eager > 0) aggregate_intent |= 0b0100;
    if (opportunistic > 0) aggregate_intent |= 0b1000;
    const expected_multiplier = 1.0 + view.header.subscription_coefficient *
        @log2(1.0 + @as(f64, @floatFromInt(count)));
    return view.column(u32, view.layout.resources.active_subscriber_count)[resource_slot] == count and
        view.column(u32, view.layout.resources.intent_ready_soon_count)[resource_slot] == ready and
        view.column(u32, view.layout.resources.intent_preload_eager_count)[resource_slot] == eager and
        view.column(u32, view.layout.resources.intent_preload_opportunistic_count)[resource_slot] == opportunistic and
        view.column(u8, view.layout.resources.subscription_intent_mask)[resource_slot] == aggregate_intent and
        view.column(f64, view.layout.resources.subscription_multiplier)[resource_slot] == expected_multiplier;
}

fn validTaskAggregate(view: View, resource_slot: u32) bool {
    var queued: u32 = 0;
    var reserved: u32 = 0;
    var active: u32 = 0;
    var slot: u32 = 0;
    while (slot < view.header.task_capacity) : (slot += 1) {
        if (view.column(u8, view.layout.tasks.state)[slot] == @intFromEnum(types.TaskState.free) or
            view.column(u32, view.layout.tasks.resource_slot)[slot] != resource_slot)
        {
            continue;
        }
        const task_state: types.TaskState = @enumFromInt(view.column(u8, view.layout.tasks.state)[slot]);
        switch (task_state) {
            .queued => queued += 1,
            .reserved => reserved += 1,
            .active => active += 1,
            .free => unreachable,
        }
    }
    const destructive = view.column(u8, view.layout.resources.destructive_active)[resource_slot] != 0;
    return view.column(u32, view.layout.resources.queued_request_count)[resource_slot] == queued and
        view.column(u32, view.layout.resources.reserved_grant_count)[resource_slot] == reserved and
        view.column(u32, view.layout.resources.active_use_count)[resource_slot] == active and
        reserved + active <= view.column(u16, view.layout.resources.max_parallel_grants)[resource_slot] and
        (!destructive or queued + reserved + active == 0);
}

fn validQueue(view: View, resource_slot: u32) bool {
    const expected_count = view.column(u32, view.layout.resources.queued_request_count)[resource_slot];
    const head = view.column(u32, view.layout.resources.queue_head)[resource_slot];
    const tail = view.column(u32, view.layout.resources.queue_tail)[resource_slot];
    if (expected_count == 0) return head == types.none_slot and tail == types.none_slot;
    if (head >= view.header.task_capacity or tail >= view.header.task_capacity) return false;

    var count: u32 = 0;
    var previous = types.none_slot;
    var slot = head;
    var previous_score: f64 = 0;
    var previous_sequence: u64 = 0;
    while (slot != types.none_slot) {
        if (slot >= view.header.task_capacity or count >= expected_count or
            view.column(u8, view.layout.tasks.state)[slot] != @intFromEnum(types.TaskState.queued) or
            view.column(u32, view.layout.tasks.resource_slot)[slot] != resource_slot or
            view.column(u32, view.layout.tasks.previous)[slot] != previous)
        {
            return false;
        }
        const score = view.column(f64, view.layout.tasks.base_score)[slot];
        const sequence = view.column(u64, view.layout.tasks.enqueue_sequence)[slot];
        if (count != 0 and (score > previous_score or (score == previous_score and sequence <= previous_sequence))) {
            return false;
        }
        count += 1;
        previous = slot;
        previous_score = score;
        previous_sequence = sequence;
        slot = view.column(u32, view.layout.tasks.next)[slot];
    }
    return count == expected_count and previous == tail;
}
