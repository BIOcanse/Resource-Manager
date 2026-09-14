const std = @import("std");

pub const abi_version: u32 = 10;

pub const ResultCode = enum(i32) {
    ok = 0,
    invalid_argument = 1,
    invalid_state = 2,
    buffer_too_small = 3,
    table_full = 4,
    resource_full = 5,
    capability_full = 6,
    pending_full = 7,
    duplicate_table = 8,
    duplicate_resource = 9,
    duplicate_capability = 10,
    stale_table = 11,
    stale_resource = 12,
    stale_capability = 13,
    stale_intent = 14,
    resource_busy = 15,
    effect_mismatch = 16,
    intent_conflict = 17,
    generation_exhausted = 18,
    numeric_overflow = 19,
    partition_full = 20,
    stale_partition = 21,
    partition_busy = 22,
    slot_occupied = 23,
    admission_stale = 24,
    operation_active = 25,
    operation_required = 26,
    stale_operation = 27,
    recovery_required = 28,
    invalid_operation_phase = 29,
};

pub const DomainKind = enum(u8) {
    memory = 0,
    gpu = 1,
};

pub const CapacityStrategy = enum(u8) {
    concentrated = 0,
    smooth = 1,
};

pub const CleanupMode = enum(u8) {
    unrestricted = 0,
    normal = 1,
    optimize = 2,
    release_all = 3,
};

pub const Recoverability = enum(u8) {
    not_recoverable = 0,
    recoverable = 1,
};

pub const AccessLossImpact = enum(u8) {
    fatal = 0,
    observable_now = 1,
    unobservable_now = 2,
};

pub const CapacityPhase = enum(u8) {
    none = 0,
    concentrated_recovery = 1,
    smooth_paced = 2,
    smooth_emergency = 3,
};

pub const PlanKind = enum(u8) {
    capacity = 1,
    mode = 2,
    merged = 3,
    partition = 4,
};

pub const OperationKind = enum(u8) {
    maintenance = 1,
    cleanup = 2,
    partition_admission = 3,
    partition_resource_admission = 4,
};

pub const OperationPhase = enum(u8) {
    inactive = 0,
    planning = 1,
    executing = 2,
    settling = 3,
    recovery_required = 4,
};

pub const PartitionCellKind = enum(u8) {
    empty = 0,
    resource = 1,
    child_partition = 2,
};

pub const PartitionAdmissionKind = enum(u8) {
    create = 1,
    close = 2,
};

pub const PendingState = enum(u8) {
    reserved = 1,
    effect_started = 2,
    effect_uncertain = 3,
};

pub const EffectOutcome = enum(u8) {
    applied = 0,
    no_effect = 1,
    rejected = 2,
    busy = 3,
    failed = 4,
    effect_unknown = 5,
};

pub const effect_releases_ledger_slot: u8 = 1 << 0;
pub const effect_changes_size_bytes: u8 = 1 << 1;
pub const effect_changes_tier: u8 = 1 << 2;
pub const known_effect_mask: u8 = effect_releases_ledger_slot |
    effect_changes_size_bytes |
    effect_changes_tier;
pub const supported_effect_mask: u8 = effect_releases_ledger_slot |
    effect_changes_size_bytes;

pub const intent_reason_capacity: u8 = 1 << 0;
pub const intent_reason_mode: u8 = 1 << 1;
pub const intent_reason_partition: u8 = 1 << 2;
pub const known_intent_reason_mask: u8 = intent_reason_capacity |
    intent_reason_mode |
    intent_reason_partition;

pub const capacity_table_flag_triggered: u32 = 1 << 0;
pub const capacity_table_flag_affected: u32 = 1 << 1;
pub const capacity_table_flag_stalled: u32 = 1 << 2;
pub const capacity_table_flag_emergency: u32 = 1 << 3;
pub const known_capacity_table_flag_mask: u32 = capacity_table_flag_triggered |
    capacity_table_flag_affected |
    capacity_table_flag_stalled |
    capacity_table_flag_emergency;

pub const Identifier128 = extern struct {
    low: u64,
    high: u64,
};

pub const Config = extern struct {
    abi_version: u32,
    struct_size: u32,
    configuration_generation: u64,
    table_capacity: u32,
    resource_capacity: u32,
    capability_capacity: u32,
    pending_capacity: u32,
    uid_bucket_capacity: u32,
    partition_capacity: u32 = 1,
    maximum_concurrent_tables: u32,
    smooth_release_interval_epochs: u32,
    activity_decay_numerator: u32,
    activity_decay_denominator: u32,
    concentrated_trigger_free_percent: u32,
    concentrated_target_free_percent: u32,
    smooth_trigger_free_percent: u32,
    smooth_emergency_free_percent: u32,
    smooth_maximum_releases_per_interval: u32,
};

pub const TableSpec = extern struct {
    abi_version: u32,
    struct_size: u32,
    table_id: Identifier128,
    table_incarnation: u64,
    adapter_key: Identifier128,
    topology_generation: u64,
    capacity: u32,
    partition_reservation_start: u32 = 0,
    partition_reservation_capacity: u32 = 0,
    domain: DomainKind,
    reserved0: [3]u8,
    reserved1: u64,
};

pub const TableHandle = extern struct {
    manager_instance_id: u64,
    slot_generation: u64,
    table_id: Identifier128,
    table_incarnation: u64,
    slot_index: u32,
    reserved0: u32,
};

pub const CapabilitySpec = extern struct {
    abi_version: u32,
    struct_size: u32,
    capability_id: u64,
    action_code: u32,
    expected_effects: u8,
    destructive: u8,
    reserved0: [2]u8,
    reserved1: u64,
};

pub const CapabilityHandle = extern struct {
    manager_instance_id: u64,
    slot_generation: u64,
    capability_id: u64,
    table_slot_index: u32,
    slot_index: u32,
};

pub const CapabilityBinding = extern struct {
    capability_id: u64,
    generation: u64,
    slot_index: u32,
    reserved0: u32,
};

pub const ResourceSpec = extern struct {
    abi_version: u32,
    struct_size: u32,
    resource_uid: Identifier128,
    size_bytes: u64,
    recovery_cost_coefficient: u32,
    recoverability: Recoverability,
    access_loss_impact: AccessLossImpact,
    reserved0: [10]u8,
};

pub const ResourceHandle = extern struct {
    manager_instance_id: u64,
    table_slot_generation: u64,
    resource_slot_generation: u64,
    table_id: Identifier128,
    table_incarnation: u64,
    resource_uid: Identifier128,
    table_slot_index: u32,
    resource_slot_index: u32,
};

pub const PartitionHandle = extern struct {
    manager_instance_id: u64,
    table_slot_generation: u64,
    partition_slot_generation: u64,
    table_id: Identifier128,
    table_incarnation: u64,
    table_slot_index: u32,
    partition_slot_index: u32,
};

pub const PartitionCreateInput = extern struct {
    abi_version: u32,
    struct_size: u32,
    table: TableHandle,
    parent: PartitionHandle,
    capacity: u32,
    has_parent: u8,
    reserved0: [3]u8,
    reserved1: u64,
};

pub const UseLease = extern struct {
    resource: ResourceHandle,
    lease_generation: u64,
};

pub const OperationToken = extern struct {
    abi_version: u32,
    struct_size: u32,
    manager_instance_id: u64,
    operation_id: u64,
    operation_generation: u64,
    start_snapshot_generation: u64,
    kind: OperationKind,
    reserved0: [7]u8,
};

pub const OperationView = extern struct {
    token: OperationToken,
    current_snapshot_generation: u64,
    settled_execution_count: u32,
    confirmed_effect_count: u32,
    pending_count: u32,
    reserved_execution_count: u32,
    started_execution_count: u32,
    uncertain_execution_count: u32,
    phase: OperationPhase,
    reserved0: [3]u8,
};

pub const CapacityPlanInput = extern struct {
    abi_version: u32,
    struct_size: u32,
    strategy: CapacityStrategy,
    reserved0: [7]u8,
};

pub const ModePlanInput = extern struct {
    abi_version: u32,
    struct_size: u32,
    table: TableHandle,
    maximum_intents: u32,
    mode: CleanupMode,
    reserved0: [3]u8,
};

pub const Intent = extern struct {
    abi_version: u32,
    struct_size: u32,
    resource: ResourceHandle,
    operation_id: u64,
    operation_generation: u64,
    snapshot_generation: u64,
    table_revision: u64,
    row_revision: u64,
    activity_revision: u64,
    use_generation: u64,
    capability_id: u64,
    capability_generation: u64,
    size_bytes: u64,
    settled_activity: u64,
    recovery_cost_coefficient: u32,
    action_code: u32,
    capability_slot_index: u32,
    reason_flags: u8,
    expected_effects: u8,
    destructive: u8,
    capacity_phase: CapacityPhase,
    access_loss_impact: AccessLossImpact,
    reserved0: [3]u8,
    reclaim_partition_slot_generation: u64 = 0,
    reclaim_partition_slot_index: u32 = std.math.maxInt(u32),
    reserved1: u32 = 0,
};

pub const PlanStamp = extern struct {
    abi_version: u32,
    struct_size: u32,
    manager_instance_id: u64,
    operation_id: u64,
    operation_generation: u64,
    snapshot_generation: u64,
    kind: u8,
    reserved0: [7]u8,
};

pub const PlanSummary = extern struct {
    operation_id: u64,
    operation_generation: u64,
    snapshot_generation: u64,
    intent_count: u32,
    affected_table_count: u32,
    triggered_table_count: u32,
    stalled_table_count: u32,
    emergency_table_count: u32,
    reserved0: u32,
};

pub const ExecutionToken = extern struct {
    resource: ResourceHandle,
    operation_id: u64,
    operation_generation: u64,
    pending_slot_generation: u64,
    row_revision: u64,
    activity_revision: u64,
    use_generation: u64,
    capability_generation: u64,
    attempt_id: u64,
    pending_slot_index: u32,
    reserved0: u32,
};

pub const TypedEffect = extern struct {
    abi_version: u32,
    struct_size: u32,
    size_bytes_after: u64,
    released_bytes: u64,
    outcome: EffectOutcome,
    changes: u8,
    reserved0: [6]u8,
};

pub const CommitReceipt = extern struct {
    operation_id: u64,
    operation_generation: u64,
    snapshot_generation: u64,
    table_revision: u64,
    resource_slot_released: u8,
    effect_uncertain: u8,
    reserved0: [6]u8,
};

pub const TableCapacityView = extern struct {
    table: TableHandle,
    capacity: u32,
    occupied_count: u32,
    free_count: u32,
    active_pending_count: u32,
    direct_capacity: u32,
    direct_occupied_count: u32,
    direct_free_count: u32,
    partition_reservation_start: u32,
    partition_reservation_capacity: u32,
    partition_occupied_count: u32,
    partition_free_count: u32,
    reserved0: u32,
    revision: u64,
};

pub const CapacityTablePlanView = extern struct {
    capacity: TableCapacityView,
    flags: u32,
    reserved0: u32,
};

pub const ResourceView = extern struct {
    resource: ResourceHandle,
    size_bytes: u64,
    settled_activity: u64,
    row_revision: u64,
    activity_revision: u64,
    recovery_cost_coefficient: u32,
    recoverability: Recoverability,
    access_loss_impact: AccessLossImpact,
    protected_use: u8,
    pending: u8,
    reserved0: [4]u8,
};

pub const PartitionAdmissionTicket = extern struct {
    abi_version: u32,
    struct_size: u32,
    table: TableHandle,
    parent: PartitionHandle,
    manager_instance_id: u64,
    operation_id: u64,
    operation_generation: u64,
    admission_id: u64,
    parent_structure_revision: u64,
    victim_hash: u64,
    local_start: u32,
    capacity: u32,
    victim_count: u32,
    has_parent: u8,
    kind: PartitionAdmissionKind,
    reserved0: [2]u8,
};

pub const PartitionAdmissionScratch = extern struct {
    settled_activity: u64,
    resource_count: u32,
    local_start: u32,
    selection_rank: u32,
    flags: u32,
};

pub const PartitionAdmissionSummary = extern struct {
    operation_id: u64,
    operation_generation: u64,
    snapshot_generation: u64,
    intent_count: u32,
    victim_count: u32,
    local_start: u32,
    capacity: u32,
};

pub const PartitionView = extern struct {
    partition: PartitionHandle,
    parent: PartitionHandle,
    settled_activity: u64,
    structure_revision: u64,
    local_start: u32,
    capacity: u32,
    direct_child_count: u32,
    descendant_resource_count: u32,
    has_parent: u8,
    reclaim_protected: u8,
    reserved0: [6]u8,
};

pub const PartitionCellView = extern struct {
    partition: PartitionHandle,
    resource: ResourceHandle,
    child_partition: PartitionHandle,
    local_ordinal: u32,
    kind: PartitionCellKind,
    reserved0: [3]u8,
};

pub const PartitionResourceAdmission = extern struct {
    partition: PartitionHandle,
    intent: Intent,
    resource: ResourceSpec,
    operation_id: u64,
    operation_generation: u64,
    snapshot_generation: u64,
    structure_revision: u64,
    local_ordinal: u32,
    requires_release: u8,
    reserved0: [3]u8,
};

pub fn validConfig(config: Config) bool {
    if (config.abi_version != abi_version or config.struct_size != @sizeOf(Config) or
        config.configuration_generation == 0 or config.table_capacity == 0 or
        config.resource_capacity == 0 or config.capability_capacity == 0 or
        config.pending_capacity == 0 or config.uid_bucket_capacity == 0 or
        config.partition_capacity == 0 or
        config.maximum_concurrent_tables == 0 or
        config.maximum_concurrent_tables > config.table_capacity or
        config.smooth_release_interval_epochs == 0 or
        config.activity_decay_denominator == 0 or
        config.activity_decay_numerator > config.activity_decay_denominator or
        config.concentrated_trigger_free_percent == 0 or
        config.smooth_emergency_free_percent >= config.concentrated_trigger_free_percent or
        config.concentrated_trigger_free_percent >= config.smooth_trigger_free_percent or
        config.smooth_trigger_free_percent >= config.concentrated_target_free_percent or
        config.concentrated_target_free_percent > 100 or
        config.smooth_maximum_releases_per_interval == 0)
    {
        return false;
    }
    if (config.uid_bucket_capacity & (config.uid_bucket_capacity - 1) != 0) return false;
    const minimum_buckets = std.math.mul(u32, config.resource_capacity, 2) catch return false;
    return config.uid_bucket_capacity >= minimum_buckets;
}

pub fn validPartitionCreateInput(input: PartitionCreateInput) bool {
    if (input.abi_version != abi_version or input.struct_size != @sizeOf(PartitionCreateInput) or
        input.capacity == 0 or input.has_parent > 1 or !allZero(input.reserved0[0..]) or
        input.reserved1 != 0)
    {
        return false;
    }
    if (input.has_parent == 0) return isZeroPartitionHandle(input.parent);
    return !isZeroPartitionHandle(input.parent);
}

pub fn validTableSpec(spec: TableSpec) bool {
    if (spec.abi_version != abi_version or spec.struct_size != @sizeOf(TableSpec) or
        isZeroId(spec.table_id) or spec.table_incarnation == 0 or spec.capacity == 0 or
        @intFromEnum(spec.domain) > @intFromEnum(DomainKind.gpu) or
        !allZero(spec.reserved0[0..]) or spec.reserved1 != 0)
    {
        return false;
    }
    if (spec.partition_reservation_capacity == 0) {
        if (spec.partition_reservation_start != 0) return false;
    } else if (spec.partition_reservation_start >= spec.capacity or
        spec.partition_reservation_capacity > spec.capacity - spec.partition_reservation_start)
    {
        return false;
    }
    return switch (spec.domain) {
        .memory => isZeroId(spec.adapter_key) and spec.topology_generation == 0,
        .gpu => !isZeroId(spec.adapter_key) and spec.topology_generation != 0,
    };
}

pub fn validCapabilitySpec(spec: CapabilitySpec) bool {
    return spec.abi_version == abi_version and spec.struct_size == @sizeOf(CapabilitySpec) and
        spec.capability_id != 0 and spec.action_code != 0 and spec.expected_effects != 0 and
        spec.expected_effects & ~supported_effect_mask == 0 and spec.destructive <= 1 and
        allZero(spec.reserved0[0..]) and spec.reserved1 == 0 and
        (spec.expected_effects & effect_releases_ledger_slot == 0 or spec.destructive == 1);
}

pub fn validResourceSpec(spec: ResourceSpec) bool {
    return spec.abi_version == abi_version and spec.struct_size == @sizeOf(ResourceSpec) and
        !isZeroId(spec.resource_uid) and
        @intFromEnum(spec.recoverability) <= @intFromEnum(Recoverability.recoverable) and
        @intFromEnum(spec.access_loss_impact) <= @intFromEnum(AccessLossImpact.unobservable_now) and
        allZero(spec.reserved0[0..]);
}

pub fn validCapacityPlanInput(input: CapacityPlanInput) bool {
    return input.abi_version == abi_version and input.struct_size == @sizeOf(CapacityPlanInput) and
        @intFromEnum(input.strategy) <= @intFromEnum(CapacityStrategy.smooth) and
        allZero(input.reserved0[0..]);
}

pub fn validModePlanInput(input: ModePlanInput) bool {
    return input.abi_version == abi_version and input.struct_size == @sizeOf(ModePlanInput) and
        @intFromEnum(input.mode) <= @intFromEnum(CleanupMode.release_all) and
        (input.mode == .unrestricted or input.mode == .normal or input.maximum_intents != 0) and
        allZero(input.reserved0[0..]);
}

pub fn validOperationTokenShape(token: OperationToken) bool {
    return token.abi_version == abi_version and
        token.struct_size == @sizeOf(OperationToken) and
        token.manager_instance_id != 0 and token.operation_id != 0 and
        token.operation_generation != 0 and token.start_snapshot_generation != 0 and
        @intFromEnum(token.kind) >= @intFromEnum(OperationKind.maintenance) and
        @intFromEnum(token.kind) <= @intFromEnum(OperationKind.partition_resource_admission) and
        allZero(token.reserved0[0..]);
}

pub fn validTypedEffect(effect: TypedEffect) bool {
    if (effect.abi_version != abi_version or effect.struct_size != @sizeOf(TypedEffect) or
        @intFromEnum(effect.outcome) > @intFromEnum(EffectOutcome.effect_unknown) or
        effect.changes & ~supported_effect_mask != 0 or !allZero(effect.reserved0[0..]))
    {
        return false;
    }
    return switch (effect.outcome) {
        .applied => effect.changes != 0,
        .no_effect, .rejected, .busy, .failed, .effect_unknown => effect.changes == 0 and
            effect.size_bytes_after == 0 and effect.released_bytes == 0,
    };
}

pub fn isZeroId(value: Identifier128) bool {
    return (value.low | value.high) == 0;
}

pub fn sameId(left: Identifier128, right: Identifier128) bool {
    return left.low == right.low and left.high == right.high;
}

pub fn isEmptyBinding(binding: CapabilityBinding) bool {
    return binding.capability_id == 0 and binding.generation == 0 and
        binding.slot_index == 0 and binding.reserved0 == 0;
}

pub fn isZeroPartitionHandle(handle: PartitionHandle) bool {
    return handle.manager_instance_id == 0 and handle.table_slot_generation == 0 and
        handle.partition_slot_generation == 0 and isZeroId(handle.table_id) and
        handle.table_incarnation == 0 and handle.table_slot_index == 0 and
        handle.partition_slot_index == 0;
}

fn allZero(values: []const u8) bool {
    for (values) |value| if (value != 0) return false;
    return true;
}
