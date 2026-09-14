const std = @import("std");

pub const abi_version: u32 = 0x0004_0004;
pub const runtime_state_count: usize = 7;

pub const Status = enum(i32) {
    ok = 0,
    invalid_argument = 1,
    abi_mismatch = 2,
    no_data = 3,
    buffer_too_small = 4,
    stale_generation = 5,
    capacity_exceeded = 6,
    conflicting_facts = 7,
    invalid_facts = 8,
    out_of_memory = 9,
    recreate_required = 10,
};

pub const RuntimeState = enum(u8) {
    unknown = 0,
    foreground_focused = 1,
    foreground_unfocused = 2,
    background_window = 3,
    tray_only = 4,
    background_process = 5,
    not_running = 6,
};

pub const ConfigFields = struct {
    pub const capacities: u64 = 1 << 0;
    pub const cpu_multipliers: u64 = 1 << 1;
    pub const gpu_multipliers: u64 = 1 << 2;
    pub const bounds: u64 = 1 << 3;
    pub const required: u64 = capacities | cpu_multipliers | gpu_multipliers | bounds;
    pub const known: u64 = required;
};

pub const EnvelopeValidity = struct {
    pub const process_generation: u64 = 1 << 0;
    pub const process_observed_at: u64 = 1 << 1;
    pub const gpu_generation: u64 = 1 << 2;
    pub const gpu_observed_at: u64 = 1 << 3;
    pub const gpu_topology: u64 = 1 << 4;
    pub const welfare_capacity: u64 = 1 << 5;
    pub const known: u64 = process_generation | process_observed_at | gpu_generation |
        gpu_observed_at | gpu_topology | welfare_capacity;
};

pub const EnvelopeFlags = struct {
    pub const cpu_snapshot_complete: u64 = 1 << 0;
    pub const gpu_snapshot_complete: u64 = 1 << 1;
    pub const known: u64 = cpu_snapshot_complete | gpu_snapshot_complete;
};

pub const ProcessValidity = struct {
    pub const identity: u64 = 1 << 0;
    pub const software_identity: u64 = 1 << 1;
    pub const runtime_state: u64 = 1 << 2;
    pub const base_importance: u64 = 1 << 3;
    pub const cpu_policy_multiplier: u64 = 1 << 4;
    pub const cpu_occupancy: u64 = 1 << 5;
    pub const source_generation: u64 = 1 << 6;
    pub const cpu_required: u64 = identity | software_identity | runtime_state |
        base_importance | cpu_policy_multiplier | cpu_occupancy | source_generation;
    pub const known: u64 = cpu_required;
};

pub const ProcessFlags = struct {
    pub const running: u32 = 1 << 0;
    pub const cpu_metrics_complete: u32 = 1 << 1;
    pub const gpu_metrics_complete: u32 = 1 << 2;
    pub const known: u32 = running | cpu_metrics_complete | gpu_metrics_complete;
};

pub const GpuValidity = struct {
    pub const process_identity: u64 = 1 << 0;
    pub const software_identity: u64 = 1 << 1;
    pub const adapter_identity: u64 = 1 << 2;
    pub const gpu_policy_multiplier: u64 = 1 << 3;
    pub const gpu_occupancy: u64 = 1 << 4;
    pub const source_generation: u64 = 1 << 5;
    pub const required: u64 = process_identity | software_identity | adapter_identity |
        gpu_policy_multiplier | gpu_occupancy | source_generation;
    pub const known: u64 = required;
};

pub const OutputKind = enum(u8) {
    process_cpu = 1,
    process_gpu = 2,
    software_cpu = 3,
    software_gpu = 4,
    software_memory = 5,
};

pub const OutputValidity = struct {
    pub const scheduling_generation: u64 = 1 << 0;
    pub const software_identity: u64 = 1 << 1;
    pub const process_identity: u64 = 1 << 2;
    pub const adapter_identity: u64 = 1 << 3;
    pub const score: u64 = 1 << 4;
    pub const member_count: u64 = 1 << 5;
    pub const known: u64 = scheduling_generation | software_identity | process_identity |
        adapter_identity | score | member_count;
};

pub const SnapshotFlags = struct {
    pub const cpu_scores_valid: u64 = 1 << 0;
    pub const gpu_scores_valid: u64 = 1 << 1;
    pub const known: u64 = cpu_scores_valid | gpu_scores_valid;
};

pub const Config = extern struct {
    abi_version: u32,
    struct_size: u32,
    generation: u64,
    field_mask: u64,
    maximum_process_count: u32,
    maximum_gpu_row_count: u32,
    maximum_output_count: u32,
    cpu_core_count: u32,
    cpu_state_multipliers: [runtime_state_count]f64,
    gpu_state_multipliers: [runtime_state_count]f64,
    maximum_base_importance: f64,
    maximum_policy_multiplier: f64,
    cpu_baseline_ratio: f64,
    welfare_utilization_baseline_percent: f64,
    reserved: [2]u64,
};

pub const Capacity = extern struct {
    abi_version: u32,
    struct_size: u32,
    configuration_generation: u64,
    process_input_struct_size: u32,
    gpu_input_struct_size: u32,
    output_struct_size: u32,
    process_capacity: u32,
    gpu_capacity: u32,
    output_capacity: u32,
    reserved: [4]u64,
};

pub const GenerationEnvelope = extern struct {
    abi_version: u32,
    struct_size: u32,
    process_input_struct_size: u32,
    gpu_input_struct_size: u32,
    output_struct_size: u32,
    reserved0: u32,
    configuration_generation: u64,
    scheduling_generation: u64,
    process_source_generation: u64,
    gpu_source_generation: u64,
    gpu_topology_generation: u64,
    gpu_topology_fingerprint: u64,
    process_observed_at_milliseconds: i64,
    gpu_observed_at_milliseconds: i64,
    valid_mask: u64,
    flags: u64,
    process_count: u32,
    gpu_row_count: u32,
    gpu_adapter_count: u32,
    output_capacity: u32,
    cpu_free_ratio: f64,
    gpu_free_ratio: f64,
    vram_free_ratio: f64,
    memory_free_ratio: f64,
    welfare_eligible_process_count: u32,
    reserved1: u32,
    software_base_mean: f64,
};

pub const ProcessInput = extern struct {
    struct_size: u32,
    flags: u32,
    valid_mask: u64,
    target_key: u64,
    software_key: u64,
    process_start_key: u64,
    source_generation: u64,
    base_importance: f64,
    cpu_policy_multiplier: f64,
    weighted_cpu_use_percent: f64,
    source_index: u32,
    process_id: u32,
    runtime_state: u8,
    reserved0: [7]u8,
    reserved: u64,
};

pub const GpuInput = extern struct {
    struct_size: u32,
    flags: u32,
    valid_mask: u64,
    target_key: u64,
    software_key: u64,
    process_start_key: u64,
    source_generation: u64,
    adapter_key: u64,
    gpu_policy_multiplier: f64,
    gpu_occupancy_percent: f64,
    source_index: u32,
    process_id: u32,
    reserved: [2]u64,
};

pub const CpuCoreInput = extern struct {
    process_index: u32,
    core_index: u32,
    usage_percent: f64,
};

pub const ScoreOutput = extern struct {
    struct_size: u32,
    kind: u8,
    runtime_state: u8,
    reserved0: u16,
    valid_mask: u64,
    scheduling_generation: u64,
    target_key: u64,
    software_key: u64,
    process_start_key: u64,
    adapter_key: u64,
    score: f64,
    source_index: u32,
    process_id: u32,
    member_count: u32,
    flags: u32,
    reserved: [2]u64,
};

pub const Snapshot = extern struct {
    abi_version: u32,
    struct_size: u32,
    configuration_generation: u64,
    scheduling_generation: u64,
    process_source_generation: u64,
    gpu_source_generation: u64,
    gpu_topology_generation: u64,
    gpu_topology_fingerprint: u64,
    flags: u64,
    process_input_count: u32,
    gpu_input_count: u32,
    process_cpu_output_count: u32,
    process_gpu_output_count: u32,
    software_cpu_output_count: u32,
    software_gpu_output_count: u32,
    output_count: u32,
    invalid_fact_count: u32,
    cpu_free_ratio: f64,
    gpu_free_ratio: f64,
    vram_free_ratio: f64,
    memory_free_ratio: f64,
    welfare_multiplier: f64,
    system_pressure: f64,
    welfare_budget: f64,
    welfare_share: f64,
    welfare_eligible_process_count: u32,
    reserved0: u32,
    reserved: u64,
    software_base_mean: f64,
    cpu_welfare_multiplier: f64,
    memory_welfare_multiplier: f64,
    cpu_welfare_bonus: f64,
    memory_welfare_bonus: f64,
    software_memory_output_count: u32,
    reserved1: u32,
};

pub fn validateConfig(config: *const Config) bool {
    const required_output_count = 3 * @as(u64, config.maximum_process_count) +
        2 * @as(u64, config.maximum_gpu_row_count);
    if (config.abi_version != abi_version or config.struct_size != @sizeOf(Config) or
        config.generation == 0 or config.field_mask != ConfigFields.required or
        config.maximum_process_count == 0 or config.maximum_gpu_row_count == 0 or
        config.maximum_output_count == 0 or
        @as(u64, config.maximum_output_count) < required_output_count or
        !finitePositive(config.maximum_base_importance) or
        !finitePositive(config.maximum_policy_multiplier) or
        !finitePositive(config.cpu_baseline_ratio) or config.cpu_baseline_ratio > 1 or
        !finitePositive(config.welfare_utilization_baseline_percent) or
        config.welfare_utilization_baseline_percent > 100 or
        !finitePositive(1 / config.cpu_baseline_ratio) or
        !allZero(config.reserved[0..])) return false;
    for (config.cpu_state_multipliers) |value| {
        if (!finiteNonNegative(value) or value > config.maximum_policy_multiplier) return false;
    }
    for (config.gpu_state_multipliers) |value| {
        if (!finiteNonNegative(value) or value > config.maximum_policy_multiplier) return false;
    }
    return finiteDeclaredScoreRange(config);
}

fn finiteDeclaredScoreRange(config: *const Config) bool {
    const maximum_weighted_base = config.maximum_base_importance *
        config.maximum_policy_multiplier;
    if (!finiteNonNegative(maximum_weighted_base)) return false;
    const maximum_gpu_raw_score = maximum_weighted_base *
        config.maximum_policy_multiplier;
    if (!finiteNonNegative(maximum_gpu_raw_score)) return false;
    const maximum_cpu_raw_score = maximum_gpu_raw_score / config.cpu_baseline_ratio;
    if (!finiteNonNegative(maximum_cpu_raw_score)) return false;

    const maximum_cpu_process_score = maximum_cpu_raw_score + config.maximum_base_importance;
    const maximum_gpu_process_score = maximum_gpu_raw_score + config.maximum_base_importance;
    if (!finiteNonNegative(maximum_cpu_process_score) or
        !finiteNonNegative(maximum_gpu_process_score)) return false;

    const maximum_members: f64 = @floatFromInt(config.maximum_process_count);
    return finiteNonNegative(maximum_cpu_process_score * maximum_members) and
        finiteNonNegative(maximum_gpu_process_score * maximum_members);
}

pub fn finiteNonNegative(value: f64) bool {
    return std.math.isFinite(value) and value >= 0;
}

pub fn finitePositive(value: f64) bool {
    return std.math.isFinite(value) and value > 0;
}

pub fn validPercent(value: f64) bool {
    return std.math.isFinite(value) and value >= 0 and value <= 100;
}

pub fn validRatio(value: f64) bool {
    return std.math.isFinite(value) and value >= 0 and value <= 1;
}

fn allZero(values: []const u64) bool {
    for (values) |value| if (value != 0) return false;
    return true;
}
