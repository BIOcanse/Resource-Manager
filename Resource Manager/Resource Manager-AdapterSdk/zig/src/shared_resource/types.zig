const std = @import("std");

pub const abi_version: u32 = 11;
pub const ledger_magic: u64 = 0x3247444c534d5252;
pub const header_size: usize = 256;
pub const none_slot: u32 = std.math.maxInt(u32);

pub const ResultCode = enum(i32) {
    ok = 0,
    invalid_argument = 1,
    abi_mismatch = 2,
    mapping_too_small = 3,
    invalid_mapping = 4,
    capacity_exhausted = 5,
    not_found = 6,
    stale_reference = 7,
    resource_busy = 8,
    queue_empty = 9,
    access_denied = 10,
    expired = 11,
    invalid_state = 12,
    inconsistent_state = 13,
    synchronization_failed = 14,
    buffer_too_small = 15,
    conflict = 16,
};

pub const SynchronizationKind = enum(u8) {
    process_local = 0,
    windows_mutex = 1,
};

pub const LeaseClockDomain = enum(u32) {
    windows_performance_counter = 1,
};

pub const Availability = enum(u8) {
    preparing = 0,
    available = 1,
    revoking = 2,
    unavailable = 3,
};

pub const ResourceControl = enum(u8) {
    free = 0,
    active = 1,
};

pub const SubscriptionControl = enum(u8) {
    free = 0,
    active = 1,
};

pub const TaskState = enum(u8) {
    free = 0,
    queued = 1,
    reserved = 2,
    active = 3,
};

pub const ConsistencyState = enum(u8) {
    stable = 0,
    unstable = 1,
    quarantined = 2,
};

pub const SubscriptionIntent = packed struct(u8) {
    reserved_required_now: bool = false,
    ready_soon: bool = false,
    preload_eager: bool = false,
    preload_opportunistic: bool = false,
    reserved: u4 = 0,
};

pub const ActionMask = packed struct(u8) {
    discard: bool = false,
    trim: bool = false,
    move_down: bool = false,
    move_up: bool = false,
    reserved: u4 = 0,
};

pub const destructive_action_bits: u8 = 0b0000_0111;
pub const all_action_bits: u8 = 0b0000_1111;
pub const capability_host_public_recall_transactions: u64 = 1 << 0;
pub const capabilities: u64 = capability_host_public_recall_transactions;
pub const subscription_intent_bits: u8 = 0b0000_1110;
pub const read_only_flag: u8 = 0b0000_0001;
pub const gpu_backed_flag: u8 = 0b0000_0100;
pub const all_resource_flag_bits: u8 = read_only_flag | gpu_backed_flag;

pub const LedgerConfig = extern struct {
    abi_version: u32,
    struct_size: u32,
    ledger_instance_id: u64,
    owner_application_key: u64,
    owner_process_created_utc_ticks: i64,
    subscription_coefficient: f64,
    resource_capacity: u32,
    subscription_capacity: u32,
    task_capacity: u32,
    owner_process_id: i32,
    synchronization_kind: u8,
    reserved0: [3]u8,
    lease_clock_domain: u32,
    lease_clock_frequency_hz: u64,
    maximum_subscription_ttl: u64,
    maximum_queue_ttl: u64,
    maximum_grant_ttl: u64,
};

pub const ResourceRef = extern struct {
    ledger_instance_id: u64,
    resource_generation: u64,
    public_resource_id: u64,
    resource_slot: u32,
    reserved: u32,
};

pub const ResourcePublication = extern struct {
    abi_version: u32,
    struct_size: u32,
    public_resource_id: u64,
    owner_application_key: u64,
    owner_instance_id: u64,
    owner_instance_id_high: u64,
    owner_context_generation: u64,
    lease_generation: u64,
    binding_generation: u64,
    capability_generation: u64,
    executor_id_low: u64,
    executor_id_high: u64,
    resource_key: u64,
    adapter_key: u64,
    size_bytes: u64,
    content_identity_hash: u64,
    payload_mapping_id: u64,
    payload_generation: u64,
    last_updated_utc_ticks: i64,
    owner_process_id: i32,
    resource_id: u32,
    max_parallel_grants: u16,
    tier: u8,
    resource_kind: u8,
    recovery_kind: u8,
    granularity: u8,
    inapplicable_actions: u8,
    action_route: u8,
    owner_demand_mask: u8,
    activity_score: u8,
    surface_state: u8,
    availability: u8,
    flags: u8,
    reserved0: u8,
    reserved1: u32,
};

pub const ResourceSnapshot = extern struct {
    abi_version: u32,
    struct_size: u32,
    id: ResourceRef,
    owner_application_key: u64,
    owner_instance_id: u64,
    owner_instance_id_high: u64,
    owner_context_generation: u64,
    lease_generation: u64,
    binding_generation: u64,
    capability_generation: u64,
    executor_id_low: u64,
    executor_id_high: u64,
    resource_key: u64,
    adapter_key: u64,
    size_bytes: u64,
    content_identity_hash: u64,
    payload_mapping_id: u64,
    payload_generation: u64,
    last_updated_utc_ticks: i64,
    subscription_multiplier: f64,
    owner_process_id: i32,
    resource_id: u32,
    active_subscriber_count: u32,
    queued_request_count: u32,
    reserved_grant_count: u32,
    active_use_count: u32,
    scheduling_revision: u64,
    gate_epoch: u64,
    max_parallel_grants: u16,
    tier: u8,
    resource_kind: u8,
    recovery_kind: u8,
    granularity: u8,
    inapplicable_actions: u8,
    action_route: u8,
    owner_demand_mask: u8,
    subscription_intent_mask: u8,
    activity_score: u8,
    surface_state: u8,
    availability: u8,
    flags: u8,
    allowed_actions: u8,
    consistency_state: u8,
    destructive_active: u8,
    reserved0: u8,
};

pub const SubscriptionRequest = extern struct {
    abi_version: u32,
    struct_size: u32,
    resource: ResourceRef,
    subscriber_application_key: u64,
    subscriber_instance_id: u64,
    subscriber_session_id: u64,
    lease_duration: u64,
    subscriber_process_id: i32,
    intent_mask: u8,
    reserved0: [3]u8,
    reserved1: u32,
};

pub const SubscriptionReceipt = extern struct {
    resource: ResourceRef,
    subscriber_instance_id: u64,
    subscriber_session_id: u64,
    deadline_timestamp: u64,
    subscription_generation: u64,
    subscription_slot: u32,
    subscriber_process_id: i32,
    intent_mask: u8,
    reserved0: [7]u8,
};

pub const UseRequest = extern struct {
    abi_version: u32,
    struct_size: u32,
    resource: ResourceRef,
    request_key: u64,
    requester_application_key: u64,
    requester_instance_id: u64,
    requester_session_id: u64,
    score_plan_generation: u64,
    queue_lease_duration: u64,
    base_score: f64,
    requester_process_id: i32,
    reserved0: u32,
};

pub const TaskReceipt = extern struct {
    resource: ResourceRef,
    request_key: u64,
    requester_application_key: u64,
    requester_instance_id: u64,
    requester_session_id: u64,
    task_generation: u64,
    task_slot: u32,
    state: u8,
    reserved0: [3]u8,
};

pub const GrantReceipt = extern struct {
    task: TaskReceipt,
    grant_generation: u64,
    payload_generation: u64,
    deadline_timestamp: u64,
};

pub const DestructiveExpectedRequest = extern struct {
    abi_version: u32,
    struct_size: u32,
    resource: ResourceRef,
    scheduling_revision: u64,
    action_attempt_id_low: u64,
    action_attempt_id_high: u64,
    action_mask: u8,
    reserved0: [7]u8,
};

pub const DestructiveToken = extern struct {
    resource: ResourceRef,
    scheduling_revision: u64,
    gate_epoch: u64,
    action_attempt_id_low: u64,
    action_attempt_id_high: u64,
    action_mask: u8,
    reserved0: [7]u8,
};

pub const RecallExpectedRequest = extern struct {
    abi_version: u32,
    struct_size: u32,
    resource: ResourceRef,
    scheduling_revision: u64,
    gate_epoch: u64,
    action_attempt_id_low: u64,
    action_attempt_id_high: u64,
    payload_generation: u64,
    subscriber_count: u32,
    activity_score: u8,
    flags: u8,
    reserved0: [2]u8,
};

pub const RecallToken = extern struct {
    resource: ResourceRef,
    scheduling_revision: u64,
    gate_epoch: u64,
    action_attempt_id_low: u64,
    action_attempt_id_high: u64,
    payload_generation: u64,
};

pub const NoEffectReason = enum(u8) {
    executor_rejected_before_effect = 1,
    source_unavailable_before_effect = 2,
    readback_matches_expected = 3,
};

pub const no_effect_proof_not_invoked: u8 = 1 << 0;
pub const no_effect_proof_readback_matches_expected: u8 = 1 << 1;
pub const all_no_effect_proof_bits: u8 =
    no_effect_proof_not_invoked | no_effect_proof_readback_matches_expected;

pub const DestructiveNoEffectReceipt = extern struct {
    abi_version: u32,
    struct_size: u32,
    action_attempt_id_low: u64,
    action_attempt_id_high: u64,
    receipt_id_low: u64,
    receipt_id_high: u64,
    observed_monotonic_timestamp: u64,
    proof_mask: u8,
    reason: u8,
    reserved0: [6]u8,
};

pub fn actionValue(mask: ActionMask) u8 {
    return @bitCast(mask);
}

pub fn intentValue(mask: SubscriptionIntent) u8 {
    return @bitCast(mask);
}

pub fn nextGeneration(current: u64) ?u64 {
    return if (current == std.math.maxInt(u64)) null else current + 1;
}

pub fn validConfig(config: *const LedgerConfig) bool {
    return config.abi_version == abi_version and
        config.struct_size == @sizeOf(LedgerConfig) and
        config.ledger_instance_id != 0 and
        config.owner_application_key != 0 and
        config.resource_capacity > 0 and
        config.task_capacity > 0 and
        std.math.isFinite(config.subscription_coefficient) and
        config.subscription_coefficient >= 0 and
        config.synchronization_kind <= @intFromEnum(SynchronizationKind.windows_mutex) and
        std.mem.allEqual(u8, config.reserved0[0..], 0) and
        config.lease_clock_domain ==
            @intFromEnum(LeaseClockDomain.windows_performance_counter) and
        config.lease_clock_frequency_hz != 0 and
        config.maximum_subscription_ttl != 0 and
        config.maximum_queue_ttl != 0 and
        config.maximum_grant_ttl != 0;
}

pub fn validPublication(publication: *const ResourcePublication) bool {
    return publication.abi_version == abi_version and
        publication.struct_size == @sizeOf(ResourcePublication) and
        publication.owner_application_key != 0 and
        (publication.owner_instance_id != 0 or publication.owner_instance_id_high != 0) and
        publication.owner_context_generation != 0 and
        publication.lease_generation != 0 and
        publication.binding_generation != 0 and
        publication.capability_generation != 0 and
        (publication.executor_id_low != 0 or publication.executor_id_high != 0) and
        publication.owner_process_id >= 0 and
        publication.resource_key != 0 and
        publication.resource_id != 0 and
        publication.max_parallel_grants > 0 and
        publication.tier <= 2 and
        publication.resource_kind <= 12 and
        publication.recovery_kind <= 2 and
        publication.granularity <= 2 and
        publication.action_route <= 1 and
        publication.availability <= @intFromEnum(Availability.unavailable) and
        publication.activity_score == 0 and
        publication.inapplicable_actions & ~all_action_bits == 0 and
        publication.owner_demand_mask & ~all_action_bits == 0 and
        publication.flags & ~all_resource_flag_bits == 0 and
        ((publication.flags & gpu_backed_flag != 0) ==
            (publication.adapter_key != 0)) and
        publication.reserved0 == 0 and
        publication.reserved1 == 0;
}

pub fn validSubscriptionRequest(request: *const SubscriptionRequest) bool {
    return request.abi_version == abi_version and
        request.struct_size == @sizeOf(SubscriptionRequest) and
        request.subscriber_application_key != 0 and
        request.subscriber_instance_id != 0 and
        request.subscriber_session_id != 0 and
        request.subscriber_process_id >= 0 and
        request.lease_duration != 0 and
        request.intent_mask & ~subscription_intent_bits == 0 and
        request.intent_mask != 0;
}

pub fn validUseRequest(request: *const UseRequest) bool {
    return request.abi_version == abi_version and
        request.struct_size == @sizeOf(UseRequest) and
        request.request_key != 0 and
        request.requester_application_key != 0 and
        request.requester_instance_id != 0 and
        request.requester_session_id != 0 and
        request.requester_process_id >= 0 and
        request.queue_lease_duration != 0 and
        std.math.isFinite(request.base_score) and
        request.base_score >= 0;
}

pub fn validDestructiveExpectedRequest(request: *const DestructiveExpectedRequest) bool {
    return request.abi_version == abi_version and
        request.struct_size == @sizeOf(DestructiveExpectedRequest) and
        request.scheduling_revision != 0 and
        (request.action_attempt_id_low != 0 or request.action_attempt_id_high != 0) and
        request.action_mask != 0 and
        request.action_mask & ~destructive_action_bits == 0 and
        request.action_mask & (request.action_mask - 1) == 0 and
        std.mem.allEqual(u8, request.reserved0[0..], 0);
}

pub fn validRecallExpectedRequest(request: *const RecallExpectedRequest) bool {
    return request.abi_version == abi_version and
        request.struct_size == @sizeOf(RecallExpectedRequest) and
        request.scheduling_revision != 0 and
        request.gate_epoch != 0 and
        (request.action_attempt_id_low != 0 or request.action_attempt_id_high != 0) and
        request.subscriber_count != 0 and
        request.flags & ~all_resource_flag_bits == 0 and
        std.mem.allEqual(u8, request.reserved0[0..], 0);
}

pub fn validDestructiveNoEffectReceipt(receipt: *const DestructiveNoEffectReceipt) bool {
    if (receipt.abi_version != abi_version or
        receipt.struct_size != @sizeOf(DestructiveNoEffectReceipt) or
        (receipt.action_attempt_id_low == 0 and receipt.action_attempt_id_high == 0) or
        (receipt.receipt_id_low == 0 and receipt.receipt_id_high == 0) or
        receipt.observed_monotonic_timestamp == 0 or
        receipt.proof_mask == 0 or
        receipt.proof_mask & ~all_no_effect_proof_bits != 0 or
        receipt.reason < @intFromEnum(NoEffectReason.executor_rejected_before_effect) or
        receipt.reason > @intFromEnum(NoEffectReason.readback_matches_expected) or
        !std.mem.allEqual(u8, receipt.reserved0[0..], 0))
    {
        return false;
    }
    return switch (@as(NoEffectReason, @enumFromInt(receipt.reason))) {
        .executor_rejected_before_effect, .source_unavailable_before_effect => receipt.proof_mask == no_effect_proof_not_invoked,
        .readback_matches_expected => receipt.proof_mask == no_effect_proof_readback_matches_expected,
    };
}

test "wire records remain fixed width" {
    try std.testing.expectEqual(@as(usize, 96), @sizeOf(LedgerConfig));
    try std.testing.expectEqual(@as(usize, 32), @sizeOf(ResourceRef));
    try std.testing.expectEqual(@as(usize, 176), @sizeOf(ResourcePublication));
    try std.testing.expectEqual(@as(usize, 240), @sizeOf(ResourceSnapshot));
    try std.testing.expectEqual(@as(usize, 88), @sizeOf(SubscriptionRequest));
    try std.testing.expectEqual(@as(usize, 80), @sizeOf(SubscriptionReceipt));
    try std.testing.expectEqual(@as(usize, 104), @sizeOf(UseRequest));
    try std.testing.expectEqual(@as(usize, 80), @sizeOf(TaskReceipt));
    try std.testing.expectEqual(@as(usize, 104), @sizeOf(GrantReceipt));
    try std.testing.expectEqual(@as(usize, 72), @sizeOf(DestructiveExpectedRequest));
    try std.testing.expectEqual(@as(usize, 72), @sizeOf(DestructiveToken));
    try std.testing.expectEqual(@as(usize, 56), @sizeOf(DestructiveNoEffectReceipt));
}
