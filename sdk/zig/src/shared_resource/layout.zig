const std = @import("std");
const types = @import("types.zig");

pub const Header = extern struct {
    magic: u64,
    version: u32,
    header_size: u32,
    mapping_size: u64,
    ledger_instance_id: u64,
    topology_generation: u64,
    mutation_sequence: u64,
    synchronization_kind: u8,
    consistency_state: u8,
    reserved0: [2]u8,
    resource_capacity: u32,
    subscription_capacity: u32,
    task_capacity: u32,
    resource_columns_offset: u64,
    subscription_columns_offset: u64,
    task_columns_offset: u64,
    subscription_coefficient: f64,
    owner_application_key: u64,
    owner_process_created_utc_ticks: i64,
    owner_process_id: i32,
    reserved1: u32,
    enqueue_sequence: u64,
    next_public_resource_id: u64,
    mapping_epoch: u64,
    resource_free_head: u32,
    subscription_free_head: u32,
    task_free_head: u32,
    reserved2: u32,
    lease_clock_domain: u32,
    reserved3: u32,
    lease_clock_frequency_hz: u64,
    maximum_subscription_ttl: u64,
    maximum_queue_ttl: u64,
    maximum_grant_ttl: u64,
    activity_generation: u64,
    reserved: [48]u8,
};

pub const ResourceLayout = struct {
    control: usize,
    free_next: usize,
    generation: usize,
    public_resource_id: usize,
    owner_application_key: usize,
    owner_instance_id: usize,
    owner_instance_id_high: usize,
    owner_context_generation: usize,
    lease_generation: usize,
    binding_generation: usize,
    capability_generation: usize,
    executor_id_low: usize,
    executor_id_high: usize,
    owner_process_id: usize,
    resource_key: usize,
    adapter_key: usize,
    resource_id: usize,
    size_bytes: usize,
    content_identity_hash: usize,
    payload_mapping_id: usize,
    payload_generation: usize,
    last_updated_utc_ticks: usize,
    subscription_multiplier: usize,
    active_subscriber_count: usize,
    subscription_intent_mask: usize,
    intent_ready_soon_count: usize,
    intent_preload_eager_count: usize,
    intent_preload_opportunistic_count: usize,
    queued_request_count: usize,
    reserved_grant_count: usize,
    active_use_count: usize,
    scheduling_revision: usize,
    gate_epoch: usize,
    destructive_active: usize,
    destructive_action: usize,
    destructive_attempt_id_low: usize,
    destructive_attempt_id_high: usize,
    max_parallel_grants: usize,
    queue_head: usize,
    queue_tail: usize,
    tier: usize,
    resource_kind: usize,
    recovery_kind: usize,
    granularity: usize,
    inapplicable_actions: usize,
    action_route: usize,
    owner_demand_mask: usize,
    activity_score: usize,
    surface_state: usize,
    availability: usize,
    flags: usize,
    end: usize,
};

pub const SubscriptionLayout = struct {
    control: usize,
    free_next: usize,
    generation: usize,
    target_resource_slot: usize,
    target_resource_generation: usize,
    subscriber_application_key: usize,
    subscriber_instance_id: usize,
    subscriber_session_id: usize,
    deadline_timestamp: usize,
    subscriber_process_id: usize,
    intent_mask: usize,
    end: usize,
};

pub const TaskLayout = struct {
    state: usize,
    free_next: usize,
    generation: usize,
    resource_slot: usize,
    resource_generation: usize,
    request_key: usize,
    requester_application_key: usize,
    requester_instance_id: usize,
    requester_session_id: usize,
    requester_process_id: usize,
    score_plan_generation: usize,
    queue_deadline_timestamp: usize,
    base_score: usize,
    enqueue_sequence: usize,
    previous: usize,
    next: usize,
    grant_generation: usize,
    grant_deadline_timestamp: usize,
    payload_generation: usize,
    end: usize,
};

pub const Layout = struct {
    resources: ResourceLayout,
    subscriptions: SubscriptionLayout,
    tasks: TaskLayout,
    required_size: usize,
};

const Cursor = struct {
    value: usize,

    fn add(self: *Cursor, comptime T: type, count: u32) !usize {
        self.value = std.mem.alignForward(usize, self.value, @alignOf(T));
        const offset = self.value;
        self.value = try std.math.add(
            usize,
            self.value,
            try std.math.mul(usize, @sizeOf(T), @as(usize, count)),
        );
        return offset;
    }
};

pub fn calculate(config: *const types.LedgerConfig) !Layout {
    if (!types.validConfig(config)) return error.InvalidConfig;

    var cursor = Cursor{ .value = types.header_size };
    const resources_start = cursor.value;
    const resources = ResourceLayout{
        .control = try cursor.add(u8, config.resource_capacity),
        .free_next = try cursor.add(u32, config.resource_capacity),
        .generation = try cursor.add(u64, config.resource_capacity),
        .public_resource_id = try cursor.add(u64, config.resource_capacity),
        .owner_application_key = try cursor.add(u64, config.resource_capacity),
        .owner_instance_id = try cursor.add(u64, config.resource_capacity),
        .owner_instance_id_high = try cursor.add(u64, config.resource_capacity),
        .owner_context_generation = try cursor.add(u64, config.resource_capacity),
        .lease_generation = try cursor.add(u64, config.resource_capacity),
        .binding_generation = try cursor.add(u64, config.resource_capacity),
        .capability_generation = try cursor.add(u64, config.resource_capacity),
        .executor_id_low = try cursor.add(u64, config.resource_capacity),
        .executor_id_high = try cursor.add(u64, config.resource_capacity),
        .owner_process_id = try cursor.add(i32, config.resource_capacity),
        .resource_key = try cursor.add(u64, config.resource_capacity),
        .adapter_key = try cursor.add(u64, config.resource_capacity),
        .resource_id = try cursor.add(u32, config.resource_capacity),
        .size_bytes = try cursor.add(u64, config.resource_capacity),
        .content_identity_hash = try cursor.add(u64, config.resource_capacity),
        .payload_mapping_id = try cursor.add(u64, config.resource_capacity),
        .payload_generation = try cursor.add(u64, config.resource_capacity),
        .last_updated_utc_ticks = try cursor.add(i64, config.resource_capacity),
        .subscription_multiplier = try cursor.add(f64, config.resource_capacity),
        .active_subscriber_count = try cursor.add(u32, config.resource_capacity),
        .subscription_intent_mask = try cursor.add(u8, config.resource_capacity),
        .intent_ready_soon_count = try cursor.add(u32, config.resource_capacity),
        .intent_preload_eager_count = try cursor.add(u32, config.resource_capacity),
        .intent_preload_opportunistic_count = try cursor.add(u32, config.resource_capacity),
        .queued_request_count = try cursor.add(u32, config.resource_capacity),
        .reserved_grant_count = try cursor.add(u32, config.resource_capacity),
        .active_use_count = try cursor.add(u32, config.resource_capacity),
        .scheduling_revision = try cursor.add(u64, config.resource_capacity),
        .gate_epoch = try cursor.add(u64, config.resource_capacity),
        .destructive_active = try cursor.add(u8, config.resource_capacity),
        .destructive_action = try cursor.add(u8, config.resource_capacity),
        .destructive_attempt_id_low = try cursor.add(u64, config.resource_capacity),
        .destructive_attempt_id_high = try cursor.add(u64, config.resource_capacity),
        .max_parallel_grants = try cursor.add(u16, config.resource_capacity),
        .queue_head = try cursor.add(u32, config.resource_capacity),
        .queue_tail = try cursor.add(u32, config.resource_capacity),
        .tier = try cursor.add(u8, config.resource_capacity),
        .resource_kind = try cursor.add(u8, config.resource_capacity),
        .recovery_kind = try cursor.add(u8, config.resource_capacity),
        .granularity = try cursor.add(u8, config.resource_capacity),
        .inapplicable_actions = try cursor.add(u8, config.resource_capacity),
        .action_route = try cursor.add(u8, config.resource_capacity),
        .owner_demand_mask = try cursor.add(u8, config.resource_capacity),
        .activity_score = try cursor.add(u8, config.resource_capacity),
        .surface_state = try cursor.add(u8, config.resource_capacity),
        .availability = try cursor.add(u8, config.resource_capacity),
        .flags = try cursor.add(u8, config.resource_capacity),
        .end = cursor.value,
    };
    _ = resources_start;

    const subscriptions = SubscriptionLayout{
        .control = try cursor.add(u8, config.subscription_capacity),
        .free_next = try cursor.add(u32, config.subscription_capacity),
        .generation = try cursor.add(u64, config.subscription_capacity),
        .target_resource_slot = try cursor.add(u32, config.subscription_capacity),
        .target_resource_generation = try cursor.add(u64, config.subscription_capacity),
        .subscriber_application_key = try cursor.add(u64, config.subscription_capacity),
        .subscriber_instance_id = try cursor.add(u64, config.subscription_capacity),
        .subscriber_session_id = try cursor.add(u64, config.subscription_capacity),
        .deadline_timestamp = try cursor.add(u64, config.subscription_capacity),
        .subscriber_process_id = try cursor.add(i32, config.subscription_capacity),
        .intent_mask = try cursor.add(u8, config.subscription_capacity),
        .end = cursor.value,
    };

    const tasks = TaskLayout{
        .state = try cursor.add(u8, config.task_capacity),
        .free_next = try cursor.add(u32, config.task_capacity),
        .generation = try cursor.add(u64, config.task_capacity),
        .resource_slot = try cursor.add(u32, config.task_capacity),
        .resource_generation = try cursor.add(u64, config.task_capacity),
        .request_key = try cursor.add(u64, config.task_capacity),
        .requester_application_key = try cursor.add(u64, config.task_capacity),
        .requester_instance_id = try cursor.add(u64, config.task_capacity),
        .requester_session_id = try cursor.add(u64, config.task_capacity),
        .requester_process_id = try cursor.add(i32, config.task_capacity),
        .score_plan_generation = try cursor.add(u64, config.task_capacity),
        .queue_deadline_timestamp = try cursor.add(u64, config.task_capacity),
        .base_score = try cursor.add(f64, config.task_capacity),
        .enqueue_sequence = try cursor.add(u64, config.task_capacity),
        .previous = try cursor.add(u32, config.task_capacity),
        .next = try cursor.add(u32, config.task_capacity),
        .grant_generation = try cursor.add(u64, config.task_capacity),
        .grant_deadline_timestamp = try cursor.add(u64, config.task_capacity),
        .payload_generation = try cursor.add(u64, config.task_capacity),
        .end = cursor.value,
    };

    const required_size = std.mem.alignForward(usize, cursor.value, 64);
    var result = Layout{
        .resources = resources,
        .subscriptions = subscriptions,
        .tasks = tasks,
        .required_size = required_size,
    };
    result.resources.end = subscriptions.control;
    result.subscriptions.end = tasks.state;
    return result;
}

pub fn headerFrom(mapping: [*]u8) *Header {
    return @ptrCast(@alignCast(mapping));
}

test "header is exactly one fixed cache-line group" {
    try std.testing.expectEqual(types.header_size, @sizeOf(Header));
    try std.testing.expectEqual(@as(usize, 8), @alignOf(Header));
}

test "layout is deterministic and aligned" {
    var config = std.mem.zeroes(types.LedgerConfig);
    config.abi_version = types.abi_version;
    config.struct_size = @sizeOf(types.LedgerConfig);
    config.ledger_instance_id = 1;
    config.owner_application_key = 2;
    config.resource_capacity = 8;
    config.subscription_capacity = 16;
    config.task_capacity = 32;
    config.subscription_coefficient = 0.25;
    config.lease_clock_domain =
        @intFromEnum(types.LeaseClockDomain.windows_performance_counter);
    config.lease_clock_frequency_hz = 10_000_000;
    config.maximum_subscription_ttl = 10_000;
    config.maximum_queue_ttl = 10_000;
    config.maximum_grant_ttl = 10_000;
    const first = try calculate(&config);
    const second = try calculate(&config);
    try std.testing.expectEqual(first.required_size, second.required_size);
    try std.testing.expect(first.required_size > types.header_size);
    try std.testing.expectEqual(@as(usize, 0), first.required_size % 64);
}
