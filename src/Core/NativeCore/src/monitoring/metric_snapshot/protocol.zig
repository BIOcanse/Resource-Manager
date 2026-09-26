const std = @import("std");

pub const abi_version: u32 = 0x0003_0000;

pub const catalog_contract_version: u32 = 0x0001_0000;
pub const value_contract_version: u32 = 0x0001_0000;
pub const observation_contract_version: u32 = 0x0001_0000;
pub const inventory_contract_version: u32 = 0x0001_0000;
pub const persistence_contract_version: u32 = 0x0002_0000;
pub const cpu_counter_contract_version: u32 = 0x0001_0000;
pub const wire_contract_fingerprint: u64 = 0xa358_b712_195e_f519;

pub const Phase = enum(u32) {
    empty = 1,
    catalog_ready = 2,
    completion_open = 3,
    ready = 4,
    failed_retained = 5,
};

pub const SourceRole = enum(u32) {
    metrics = 1,
    gpu_inventory = 2,
};

pub const SourceStatus = enum(u32) {
    complete = 1,
    partial = 2,
    unavailable = 3,
    unsupported = 4,
    skipped = 5,
};

pub const ZoneMode = enum(u32) {
    normal = 1,
    low_power = 2,
    freeze = 3,
};

pub const SourceAvailability = enum(u32) {
    available = 1,
    unavailable = 2,
    unsupported = 3,
};

pub const ObservationStatus = enum(u32) {
    current = 1,
    unavailable = 2,
    unsupported = 3,
    skipped = 4,
};

pub const MetricStatus = enum(u32) {
    current = 1,
    retained = 2,
    unavailable = 3,
    unsupported = 4,
    skipped = 5,
};

pub const InventoryStatus = enum(u32) {
    current = 1,
    retained = 2,
    unavailable = 3,
    unsupported = 4,
};

pub const RetentionPolicy = enum(u32) {
    retain_last_good = 1,
    mark_unavailable = 2,
};

pub const MetricKind = enum(u32) {
    cpu_usage = 1,
    cpu_frequency = 2,
    cpu_sensor = 3,
    memory_used = 4,
    memory_total = 5,
    virtual_memory_used = 6,
    virtual_memory_total = 7,
    gpu_usage = 8,
    gpu_clock = 9,
    gpu_vram_used = 10,
    gpu_vram_total = 11,
    gpu_sensor = 12,
    custom_numeric = 13,
};

pub const ScopeKind = enum(u32) {
    host = 1,
    cpu = 2,
    memory = 3,
    gpu_adapter = 4,
};

pub const ValueKind = enum(u32) {
    float64 = 1,
    signed64 = 2,
    unsigned64 = 3,
};

pub const MetricFlags = struct {
    pub const percentage: u32 = 1 << 0;
    pub const nonnegative: u32 = 1 << 1;
    pub const used_value: u32 = 1 << 2;
    pub const total_value: u32 = 1 << 3;
    pub const known: u32 = percentage | nonnegative | used_value | total_value;
};

pub const SourceFlags = struct {
    pub const required: u32 = 1 << 0;
    pub const known: u32 = required;
};

pub const SourceResetReason = struct {
    pub const incarnation_changed: u32 = 1 << 0;
    pub const counter_regressed: u32 = 1 << 1;
    pub const monotonic_regressed: u32 = 1 << 2;
    pub const tick_frequency_changed: u32 = 1 << 3;
    pub const arithmetic_overflow: u32 = 1 << 4;
    pub const known: u32 = incarnation_changed | counter_regressed |
        monotonic_regressed | tick_frequency_changed | arithmetic_overflow;
};

pub const SnapshotFlags = struct {
    pub const catalog_loaded: u32 = 1 << 0;
    pub const committed_data: u32 = 1 << 1;
    pub const retained_data: u32 = 1 << 2;
    pub const gpu_inventory_present: u32 = 1 << 3;
    pub const completion_open: u32 = 1 << 4;
    pub const known: u32 = catalog_loaded | committed_data | retained_data |
        gpu_inventory_present | completion_open;
};

pub const PlanFlags = struct {
    pub const include_gpu_inventory: u64 = 1 << 0;
    pub const known: u64 = include_gpu_inventory;
};

pub const PlanOutputFlags = struct {
    pub const gpu_inventory_selected: u32 = 1 << 0;
    pub const gpu_inventory_unavailable: u32 = 1 << 1;
    pub const known: u32 = gpu_inventory_selected | gpu_inventory_unavailable;
};

pub const ObservationValid = struct {
    pub const value: u64 = 1 << 0;
    pub const observed_at: u64 = 1 << 1;
    pub const capability: u64 = 1 << 2;
    pub const required: u64 = observed_at | capability;
    pub const known: u64 = required | value;
};

pub const CpuCounterValid = struct {
    pub const counters: u64 = 1 << 0;
    pub const monotonic_time: u64 = 1 << 1;
    pub const observed_at: u64 = 1 << 2;
    pub const capability: u64 = 1 << 3;
    pub const required: u64 = counters | monotonic_time | observed_at | capability;
    pub const known: u64 = required;
};

pub const MetricPlanFlags = struct {
    pub const selected: u32 = 1 << 0;
    pub const unavailable: u32 = 1 << 1;
    pub const known: u32 = selected | unavailable;
};

pub const SourcePlanFlags = struct {
    pub const selected: u32 = 1 << 0;
    pub const low_power: u32 = 1 << 1;
    pub const known: u32 = selected | low_power;
};

pub const InventoryValid = struct {
    pub const adapter_identity: u64 = 1 << 0;
    pub const observed_at: u64 = 1 << 1;
    pub const capability: u64 = 1 << 2;
    pub const topology: u64 = 1 << 3;
    pub const required: u64 = adapter_identity | observed_at | capability | topology;
    pub const known: u64 = required;
};

pub const Config = extern struct {
    abi_version: u32,
    struct_size: u32,
    generation: u64,
    maximum_source_count: u32,
    maximum_metric_count: u32,
    maximum_rule_count: u32,
    maximum_requested_count: u32,
    maximum_observation_count: u32,
    maximum_gpu_adapter_count: u32,
    maximum_persistence_source_count: u32,
    maximum_persistence_rule_count: u32,
    maximum_persistence_gpu_count: u32,
    source_index_capacity: u32,
    metric_index_capacity: u32,
    rule_index_capacity: u32,
    gpu_index_capacity: u32,
    maximum_future_skew_milliseconds: u64,
    resident_byte_budget: u64,
    catalog_contract_version: u32,
    value_contract_version: u32,
    observation_contract_version: u32,
    inventory_contract_version: u32,
    persistence_contract_version: u32,
    flags: u32,
    maximum_plan_metric_count: u32,
    maximum_source_mode_count: u32,
    maximum_source_plan_count: u32,
    maximum_metric_plan_count: u32,
    gpu_luid_index_capacity: u32,
    gpu_key_index_capacity: u32,
    reserved_u32: u32,
    reserved: [2]u64,
};

pub const Capacity = extern struct {
    struct_size: u32,
    source_capacity: u32,
    metric_capacity: u32,
    rule_capacity: u32,
    requested_capacity: u32,
    observation_capacity: u32,
    gpu_adapter_capacity: u32,
    persistence_source_capacity: u32,
    persistence_rule_capacity: u32,
    persistence_gpu_capacity: u32,
    source_index_capacity: u32,
    metric_index_capacity: u32,
    rule_index_capacity: u32,
    gpu_index_capacity: u32,
    gpu_luid_index_capacity: u32,
    gpu_key_index_capacity: u32,
    plan_metric_capacity: u32,
    source_mode_capacity: u32,
    source_plan_capacity: u32,
    metric_plan_capacity: u32,
    resident_byte_count: u64,
    reserved: [2]u64,
};

pub const SourcePolicyInput = extern struct {
    struct_size: u32,
    flags: u32,
    source_handle: u64,
    source_role: u32,
    priority: u32,
    retention_policy: u32,
    reserved_u32: u32,
    capability_mask: u64,
    semantic_fingerprint: u64,
    reserved: [2]u64,
};

pub const MetricDefinitionInput = extern struct {
    struct_size: u32,
    flags: u32,
    rule_handle: u64,
    metric_handle: u64,
    source_handle: u64,
    scope_handle: u64,
    capability_mask: u64,
    metric_kind: u32,
    scope_kind: u32,
    value_kind: u32,
    retention_policy: u32,
    source_priority: u32,
    reserved_u32: u32,
    minimum_value_bits: u64,
    maximum_value_bits: u64,
    semantic_fingerprint: u64,
};

pub const CatalogReplaceInput = extern struct {
    abi_version: u32,
    struct_size: u32,
    configuration_generation: u64,
    catalog_generation: u64,
    operation_epoch: u64,
    command_at_milliseconds: u64,
    source_count: u32,
    rule_count: u32,
    metric_count: u32,
    gpu_inventory_source_count: u32,
    source_index_capacity: u32,
    metric_index_capacity: u32,
    rule_index_capacity: u32,
    flags: u32,
    semantic_fingerprint: u64,
    reserved: [2]u64,
};

pub const CompletionHeader = extern struct {
    abi_version: u32,
    struct_size: u32,
    configuration_generation: u64,
    catalog_generation: u64,
    operation_epoch: u64,
    plan_epoch: u64,
    source_handle: u64,
    source_incarnation: u64,
    source_observation_sequence: u64,
    capability_generation: u64,
    plan_token_fingerprint: u64,
    command_at_milliseconds: u64,
    captured_at_milliseconds: u64,
    requested_rule_count: u32,
    observation_count: u32,
    gpu_inventory_count: u32,
    expected_gpu_inventory_count: u32,
    cpu_counter_count: u32,
    overflow_count: u32,
    status: u32,
    flags: u32,
    reserved: [2]u64,
};

pub const PlanInput = extern struct {
    abi_version: u32,
    struct_size: u32,
    configuration_generation: u64,
    catalog_generation: u64,
    operation_epoch: u64,
    plan_epoch: u64,
    command_at_milliseconds: u64,
    requested_metric_count: u32,
    source_mode_count: u32,
    source_plan_capacity: u32,
    metric_plan_capacity: u32,
    flags: u64,
    reserved: [2]u64,
};

pub const PlanOutput = extern struct {
    abi_version: u32,
    struct_size: u32,
    configuration_generation: u64,
    catalog_generation: u64,
    operation_epoch: u64,
    plan_epoch: u64,
    state_revision: u64,
    source_plan_count: u32,
    metric_plan_count: u32,
    unavailable_metric_count: u32,
    flags: u32,
    semantic_fingerprint: u64,
    reserved: [2]u64,
};

pub const PlanMetricInput = extern struct {
    struct_size: u32,
    flags: u32,
    metric_handle: u64,
    reserved: [2]u64,
};

pub const SourceModeInput = extern struct {
    struct_size: u32,
    flags: u32,
    source_handle: u64,
    capability_generation: u64,
    zone_mode: u32,
    availability: u32,
    reserved: [2]u64,
};

pub const SourcePlanOutput = extern struct {
    struct_size: u32,
    flags: u32,
    source_handle: u64,
    capability_generation: u64,
    plan_epoch: u64,
    plan_token_fingerprint: u64,
    requested_rule_count: u32,
    zone_mode: u32,
    availability: u32,
    source_role: u32,
    reserved: [2]u64,
};

pub const MetricPlanOutput = extern struct {
    struct_size: u32,
    flags: u32,
    rule_handle: u64,
    metric_handle: u64,
    source_handle: u64,
    scope_handle: u64,
    plan_epoch: u64,
    metric_kind: u32,
    value_kind: u32,
    reserved: [2]u64,
};

pub const RequestedMetricInput = extern struct {
    struct_size: u32,
    flags: u32,
    rule_handle: u64,
    reserved: [2]u64,
};

pub const ObservationInput = extern struct {
    struct_size: u32,
    flags: u32,
    rule_handle: u64,
    metric_handle: u64,
    source_handle: u64,
    scope_handle: u64,
    source_observation_sequence: u64,
    observed_at_milliseconds: u64,
    value_bits: u64,
    capability_mask: u64,
    valid_mask: u64,
    status: u32,
    value_kind: u32,
    quality: u32,
    reserved_u32: u32,
    sample_duration_milliseconds: u64,
};

pub const CpuCounterInput = extern struct {
    struct_size: u32,
    flags: u32,
    rule_handle: u64,
    metric_handle: u64,
    source_handle: u64,
    source_incarnation: u64,
    source_observation_sequence: u64,
    observed_at_milliseconds: u64,
    monotonic_ticks: u64,
    monotonic_ticks_per_second: u64,
    idle_ticks: u64,
    kernel_ticks: u64,
    user_ticks: u64,
    capability_mask: u64,
    valid_mask: u64,
    status: u32,
    counter_contract_version: u32,
    reserved: [2]u64,
};

pub const GpuInventoryInput = extern struct {
    struct_size: u32,
    flags: u32,
    adapter_handle: u64,
    source_handle: u64,
    source_observation_sequence: u64,
    observed_at_milliseconds: u64,
    adapter_luid_low: u64,
    adapter_luid_high: u64,
    stable_key_handle: u64,
    capability_mask: u64,
    topology_fingerprint: u64,
    valid_mask: u64,
    status: u32,
    reserved_u32: u32,
    reserved: [2]u64,
};

pub const FinalizeInput = extern struct {
    abi_version: u32,
    struct_size: u32,
    configuration_generation: u64,
    catalog_generation: u64,
    operation_epoch: u64,
    plan_epoch: u64,
    source_handle: u64,
    command_at_milliseconds: u64,
    flags: u64,
    reserved: [2]u64,
};

pub const AbortInput = FinalizeInput;

pub const ControlInput = extern struct {
    abi_version: u32,
    struct_size: u32,
    configuration_generation: u64,
    operation_epoch: u64,
    command_at_milliseconds: u64,
    flags: u64,
    reserved: [2]u64,
};

pub const ReadInput = extern struct {
    abi_version: u32,
    struct_size: u32,
    configuration_generation: u64,
    catalog_generation: u64,
    catalog_fingerprint: u64,
    state_revision: u64,
    committed_generation: u64,
    flags: u64,
    reserved: [2]u64,
};

pub const SnapshotHeader = extern struct {
    abi_version: u32,
    struct_size: u32,
    configuration_generation: u64,
    catalog_generation: u64,
    catalog_fingerprint: u64,
    state_revision: u64,
    committed_generation: u64,
    last_operation_epoch: u64,
    last_plan_epoch: u64,
    last_command_at_milliseconds: u64,
    captured_at_milliseconds: u64,
    source_count: u32,
    metric_count: u32,
    rule_count: u32,
    gpu_adapter_count: u32,
    current_metric_count: u32,
    retained_metric_count: u32,
    unavailable_metric_count: u32,
    unsupported_metric_count: u32,
    skipped_metric_count: u32,
    flags: u32,
    phase: u32,
    reserved_u32: u32,
    semantic_fingerprint: u64,
    reserved: [2]u64,
};

pub const MetricOutput = extern struct {
    struct_size: u32,
    flags: u32,
    metric_handle: u64,
    winning_rule_handle: u64,
    source_handle: u64,
    scope_handle: u64,
    source_generation: u64,
    observed_at_milliseconds: u64,
    value_bits: u64,
    capability_mask: u64,
    metric_kind: u32,
    scope_kind: u32,
    value_kind: u32,
    status: u32,
    quality: u32,
    reserved_u32: u32,
    semantic_fingerprint: u64,
    sample_duration_milliseconds: u64,
};

pub const SourceOutput = extern struct {
    struct_size: u32,
    flags: u32,
    source_handle: u64,
    source_incarnation: u64,
    source_generation: u64,
    source_observation_sequence: u64,
    capability_generation: u64,
    last_attempt_at_milliseconds: u64,
    last_current_at_milliseconds: u64,
    capability_mask: u64,
    source_role: u32,
    priority: u32,
    status: u32,
    retention_policy: u32,
    requested_count: u32,
    observed_count: u32,
    skipped_count: u32,
    overflow_count: u32,
    reset_count: u32,
    last_reset_reason_mask: u32,
    definition_fingerprint: u64,
    state_fingerprint: u64,
};

pub const GpuInventoryOutput = extern struct {
    struct_size: u32,
    flags: u32,
    adapter_handle: u64,
    source_handle: u64,
    source_generation: u64,
    observed_at_milliseconds: u64,
    adapter_luid_low: u64,
    adapter_luid_high: u64,
    stable_key_handle: u64,
    capability_mask: u64,
    topology_fingerprint: u64,
    valid_mask: u64,
    status: u32,
    reserved_u32: u32,
    semantic_fingerprint: u64,
    reserved: [2]u64,
};

pub const RuleStateOutput = extern struct {
    struct_size: u32,
    flags: u32,
    rule_handle: u64,
    metric_handle: u64,
    source_handle: u64,
    source_generation: u64,
    observed_at_milliseconds: u64,
    value_bits: u64,
    capability_mask: u64,
    status: u32,
    value_kind: u32,
    quality: u32,
    has_last_good: u32,
    sample_duration_milliseconds: u64,
    definition_fingerprint: u64,
    state_fingerprint: u64,
    capability_generation: u64,
    unsupported_until_capability_generation: u64,
};

pub const SourcePersistenceOutput = extern struct {
    struct_size: u32,
    flags: u32,
    source_handle: u64,
    definition_fingerprint: u64,
    source_incarnation: u64,
    source_generation: u64,
    source_observation_sequence: u64,
    last_attempt_at_milliseconds: u64,
    last_current_at_milliseconds: u64,
    capability_mask: u64,
    cpu_idle_ticks: u64,
    cpu_kernel_ticks: u64,
    cpu_user_ticks: u64,
    cpu_monotonic_ticks: u64,
    cpu_monotonic_ticks_per_second: u64,
    capability_generation: u64,
    requested_count: u32,
    observed_count: u32,
    skipped_count: u32,
    overflow_count: u32,
    reset_count: u32,
    last_reset_reason_mask: u32,
    status: u32,
    cpu_baseline_valid: u32,
    cpu_counter_contract_version: u32,
    reserved_u32: u32,
    state_fingerprint: u64,
    gpu_inventory_count: u32,
    gpu_retained_count: u32,
    reserved: u64,
};

pub const PersistenceInput = extern struct {
    abi_version: u32,
    struct_size: u32,
    configuration_generation: u64,
    catalog_generation: u64,
    operation_epoch: u64,
    command_at_milliseconds: u64,
    flags: u64,
    reserved: [2]u64,
};

pub const PersistenceHeader = extern struct {
    abi_version: u32,
    struct_size: u32,
    configuration_generation: u64,
    catalog_generation: u64,
    state_revision: u64,
    committed_generation: u64,
    last_operation_epoch: u64,
    last_plan_epoch: u64,
    last_command_at_milliseconds: u64,
    captured_at_milliseconds: u64,
    catalog_fingerprint: u64,
    rule_state_count: u32,
    source_state_count: u32,
    gpu_adapter_count: u32,
    phase: u32,
    semantic_fingerprint: u64,
    checksum: u64,
    flags: u64,
    reserved: [3]u64,
};

pub fn validConfig(config: *const Config) bool {
    if (config.abi_version != abi_version or config.struct_size != @sizeOf(Config)) return false;
    if (config.generation == 0) return false;
    if (config.maximum_source_count == 0 or config.maximum_metric_count == 0 or
        config.maximum_rule_count == 0 or config.maximum_requested_count == 0 or
        config.maximum_observation_count == 0 or config.maximum_gpu_adapter_count == 0 or
        config.maximum_plan_metric_count == 0 or config.maximum_source_mode_count == 0 or
        config.maximum_source_plan_count == 0 or config.maximum_metric_plan_count == 0)
    {
        return false;
    }
    if (config.maximum_source_plan_count > config.maximum_source_count or
        config.maximum_metric_plan_count > config.maximum_rule_count or
        config.maximum_requested_count > config.maximum_rule_count or
        config.maximum_observation_count < config.maximum_requested_count or
        config.maximum_plan_metric_count < config.maximum_requested_count or
        config.maximum_source_mode_count < config.maximum_source_count or
        config.maximum_source_plan_count < config.maximum_source_count or
        config.maximum_metric_plan_count < config.maximum_requested_count)
    {
        return false;
    }
    if (config.maximum_persistence_source_count < config.maximum_source_count or
        config.maximum_persistence_rule_count < config.maximum_rule_count or
        config.maximum_persistence_gpu_count < config.maximum_gpu_adapter_count)
    {
        return false;
    }
    if (!validIndexCapacity(config.source_index_capacity, config.maximum_source_count) or
        !validIndexCapacity(config.metric_index_capacity, config.maximum_metric_count) or
        !validIndexCapacity(config.rule_index_capacity, config.maximum_rule_count) or
        !validIndexCapacity(config.gpu_index_capacity, config.maximum_gpu_adapter_count) or
        !validIndexCapacity(config.gpu_luid_index_capacity, config.maximum_gpu_adapter_count) or
        !validIndexCapacity(config.gpu_key_index_capacity, config.maximum_gpu_adapter_count))
    {
        return false;
    }
    if (config.maximum_future_skew_milliseconds == 0 or config.resident_byte_budget == 0) {
        return false;
    }
    if (config.catalog_contract_version != catalog_contract_version or
        config.value_contract_version != value_contract_version or
        config.observation_contract_version != observation_contract_version or
        config.inventory_contract_version != inventory_contract_version or
        config.persistence_contract_version != persistence_contract_version)
    {
        return false;
    }
    if (config.flags != 0 or config.reserved_u32 != 0 or !allZero(config.reserved)) return false;
    return true;
}

pub fn validSourcePolicy(source: *const SourcePolicyInput) bool {
    if (source.struct_size != @sizeOf(SourcePolicyInput) or source.source_handle == 0) return false;
    if ((source.flags & ~SourceFlags.known) != 0 or source.reserved_u32 != 0 or
        !allZero(source.reserved))
    {
        return false;
    }
    if (!knownEnum(SourceRole, source.source_role) or
        !knownEnum(RetentionPolicy, source.retention_policy))
    {
        return false;
    }
    if (source.priority == 0 or source.semantic_fingerprint == 0) return false;
    return true;
}

pub fn validMetricDefinition(metric: *const MetricDefinitionInput) bool {
    if (metric.struct_size != @sizeOf(MetricDefinitionInput) or metric.rule_handle == 0 or
        metric.metric_handle == 0 or metric.source_handle == 0 or metric.scope_handle == 0)
    {
        return false;
    }
    if ((metric.flags & ~MetricFlags.known) != 0 or metric.reserved_u32 != 0) return false;
    if (!knownEnum(MetricKind, metric.metric_kind) or !knownEnum(ScopeKind, metric.scope_kind) or
        !knownEnum(ValueKind, metric.value_kind) or
        !knownEnum(RetentionPolicy, metric.retention_policy))
    {
        return false;
    }
    if (metric.source_priority == 0 or metric.semantic_fingerprint == 0) return false;
    if ((metric.flags & MetricFlags.percentage) != 0 and
        @as(ValueKind, @enumFromInt(metric.value_kind)) != .float64)
    {
        return false;
    }
    return validRange(metric.value_kind, metric.minimum_value_bits, metric.maximum_value_bits);
}

pub fn validValue(kind_raw: u32, flags: u32, minimum_bits: u64, maximum_bits: u64, value: u64) bool {
    if (!knownEnum(ValueKind, kind_raw)) return false;
    const kind: ValueKind = @enumFromInt(kind_raw);
    switch (kind) {
        .float64 => {
            const actual: f64 = @bitCast(value);
            const minimum: f64 = @bitCast(minimum_bits);
            const maximum: f64 = @bitCast(maximum_bits);
            if (!std.math.isFinite(actual) or actual < minimum or actual > maximum) return false;
            if ((flags & MetricFlags.nonnegative) != 0 and actual < 0) return false;
            if ((flags & MetricFlags.percentage) != 0 and (actual < 0 or actual > 100)) return false;
        },
        .signed64 => {
            const actual: i64 = @bitCast(value);
            const minimum: i64 = @bitCast(minimum_bits);
            const maximum: i64 = @bitCast(maximum_bits);
            if (actual < minimum or actual > maximum) return false;
            if ((flags & MetricFlags.nonnegative) != 0 and actual < 0) return false;
        },
        .unsigned64 => {
            if (value < minimum_bits or value > maximum_bits) return false;
        },
    }
    return true;
}

pub fn knownEnum(comptime T: type, raw: u32) bool {
    return std.enums.fromInt(T, raw) != null;
}

fn validRange(kind_raw: u32, minimum_bits: u64, maximum_bits: u64) bool {
    const kind: ValueKind = @enumFromInt(kind_raw);
    return switch (kind) {
        .float64 => blk: {
            const minimum: f64 = @bitCast(minimum_bits);
            const maximum: f64 = @bitCast(maximum_bits);
            break :blk std.math.isFinite(minimum) and std.math.isFinite(maximum) and minimum <= maximum;
        },
        .signed64 => @as(i64, @bitCast(minimum_bits)) <= @as(i64, @bitCast(maximum_bits)),
        .unsigned64 => minimum_bits <= maximum_bits,
    };
}

fn validIndexCapacity(capacity: u32, item_capacity: u32) bool {
    return capacity >= item_capacity and std.math.isPowerOfTwo(capacity);
}

fn allZero(values: anytype) bool {
    for (values) |value| if (value != 0) return false;
    return true;
}
