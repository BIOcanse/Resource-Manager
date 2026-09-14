const std = @import("std");
const base_score_tier = @import("../base_score_tier.zig");

pub const abi_version: u32 = 0x0008_0000;
pub const runtime_state_count: usize = 7;
pub const adapter_grade_count: u8 = 4;

pub const Status = enum(i32) {
    ok = 0,
    invalid_argument = 1,
    abi_mismatch = 2,
    unavailable = 3,
    no_data = 4,
    buffer_too_small = 5,
    stale_generation = 6,
    out_of_memory = 7,
    capacity_exceeded = 8,
    conflicting_facts = 9,
    feedback_mismatch = 10,
    recreate_required = 11,
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

pub const SoftwareKind = enum(u8) {
    unknown = 0,
    game = 1,
    high_performance = 2,
    windows_system = 3,
    adapted = 4,
    dependency = 5,
    runtime = 6,
    controlled = 7,
    managed = 8,
    unattributed = 9,
    general_application = 10,
};

pub const MetricKind = enum(u8) {
    none = 0,
    cpu_usage_percent = 1,
    gpu_usage_percent = 2,
    vram_usage_percent = 3,
};

pub const ProcessGrade = struct {
    pub const level4: i8 = -4;
    pub const level3: i8 = -3;
    pub const level2: i8 = -2;
    pub const level1: i8 = -1;
    pub const normal: i8 = 0;
    pub const a1: i8 = 1;
};

pub const AdapterGrade = enum(u8) {
    freeze = 0,
    optimize = 1,
    normal = 2,
    extreme = 3,
};

pub const ActionScope = enum(u8) {
    process_policy = 1,
    adapter_software = 2,
};

pub const ActionDisposition = enum(u8) {
    no_op = 0,
    retain = 1,
    restore = 2,
    apply = 3,
};

pub const FeedbackStatus = enum(u8) {
    succeeded = 1,
    failed_unchanged = 2,
    rejected = 3,
    skipped = 4,
    ownership_lost = 5,
    state_uncertain = 6,
};

pub const SnapshotRowKind = enum(u8) {
    process = 1,
    software = 2,
};

pub const ConfigFields = struct {
    pub const capacities: u64 = 1 << 0;
    pub const timing: u64 = 1 << 1;
    pub const process_scoring: u64 = 1 << 2;
    pub const cpu_adapter_policy: u64 = 1 << 3;
    pub const gpu_adapter_policy: u64 = 1 << 4;
    pub const features: u64 = 1 << 5;
    pub const required: u64 = capacities | timing | process_scoring |
        cpu_adapter_policy | gpu_adapter_policy | features;
};

pub const FeatureFlags = struct {
    pub const process_policy: u64 = 1 << 0;
    pub const adapter_cpu: u64 = 1 << 1;
    pub const adapter_gpu: u64 = 1 << 2;
    pub const critical_events: u64 = 1 << 3;
    pub const known: u64 = process_policy | adapter_cpu | adapter_gpu | critical_events;
};

pub const CycleValidity = struct {
    pub const score_only_mode: u64 = 1 << 2;
    pub const maximum_actions_this_cycle: u64 = 1 << 3;
    pub const score_scheduling_generation: u64 = 1 << 4;
    pub const cpu_score_source_fingerprint: u64 = 1 << 5;
    pub const gpu_score_source_fingerprint: u64 = 1 << 6;
    pub const known: u64 = score_only_mode | maximum_actions_this_cycle | score_scheduling_generation |
        cpu_score_source_fingerprint | gpu_score_source_fingerprint;
    pub const required: u64 = score_only_mode | maximum_actions_this_cycle;
};

pub const CycleFlags = struct {
    pub const full_process_snapshot: u64 = 1 << 0;
    pub const authoritative_applied_facts: u64 = 1 << 2;
    pub const score_only: u64 = 1 << 3;
    pub const external_event: u64 = 1 << 4;
    pub const known: u64 = full_process_snapshot | authoritative_applied_facts |
        score_only | external_event;
};

pub const InputValidity = struct {
    pub const process_identity: u64 = 1 << 0;
    pub const software_identity: u64 = 1 << 1;
    pub const software_kind: u64 = 1 << 2;
    pub const base_score: u64 = 1 << 3;
    pub const surface_facts: u64 = 1 << 4;
    pub const eligibility: u64 = 1 << 5;
    pub const protection: u64 = 1 << 6;
    pub const cpu_capabilities: u64 = 1 << 7;
    pub const gpu_capabilities: u64 = 1 << 8;
    pub const metric: u64 = 1 << 9;
    pub const applied_process_grade: u64 = 1 << 10;
    pub const applied_cpu_grade: u64 = 1 << 11;
    pub const applied_gpu_grade: u64 = 1 << 12;
    pub const process_score: u64 = 1 << 13;
    pub const software_score: u64 = 1 << 14;
    pub const score_member_count: u64 = 1 << 15;
    pub const known: u64 = process_identity | software_identity | software_kind |
        base_score | surface_facts | eligibility | protection | cpu_capabilities |
        gpu_capabilities | metric | applied_process_grade | applied_cpu_grade |
        applied_gpu_grade | process_score | software_score | score_member_count;
};

pub const InputFlags = struct {
    pub const running: u64 = 1 << 0;
    pub const foreground_focused: u64 = 1 << 1;
    pub const has_visible_window: u64 = 1 << 2;
    pub const has_background_window: u64 = 1 << 3;
    pub const has_hidden_window: u64 = 1 << 4;
    pub const process_cpu_metrics_complete: u64 = 1 << 5;
    pub const process_gpu_metrics_complete: u64 = 1 << 6;
    pub const can_apply_process_policy: u64 = 1 << 7;
    pub const can_apply_adapter_policy: u64 = 1 << 8;
    pub const hardware_scheduling_eligible: u64 = 1 << 9;
    pub const owns_process_grade: u64 = 1 << 10;
    pub const owns_cpu_grade: u64 = 1 << 11;
    pub const owns_gpu_grade: u64 = 1 << 12;
    pub const known: u64 = running | foreground_focused | has_visible_window |
        has_background_window | has_hidden_window | process_cpu_metrics_complete |
        process_gpu_metrics_complete | can_apply_process_policy |
        can_apply_adapter_policy | hardware_scheduling_eligible |
        owns_process_grade | owns_cpu_grade | owns_gpu_grade;
};

pub const GradeDomains = struct {
    pub const process: u8 = 1 << 0;
    pub const cpu: u8 = 1 << 1;
    pub const gpu: u8 = 1 << 2;
    pub const known: u8 = process | cpu | gpu;
};

pub const ActionValidity = struct {
    pub const process_identity: u64 = 1 << 0;
    pub const software_identity: u64 = 1 << 1;
    pub const process_grade: u64 = 1 << 2;
    pub const cpu_grade: u64 = 1 << 3;
    pub const gpu_grade: u64 = 1 << 4;
    pub const cpu_score: u64 = 1 << 5;
    pub const atomic_group: u64 = 1 << 6;
    pub const gpu_score: u64 = 1 << 7;
    pub const known: u64 = process_identity | software_identity | process_grade |
        cpu_grade | gpu_grade | cpu_score | atomic_group | gpu_score;
};

pub const ActionFlags = struct {
    pub const requires_feedback: u32 = 1 << 0;
    pub const atomic: u32 = 1 << 1;
    pub const compensation: u32 = 1 << 2;
    pub const retry: u32 = 1 << 3;
    pub const known: u32 = requires_feedback | atomic | compensation | retry;
};

pub const FeedbackValidity = struct {
    pub const completed_at: u64 = 1 << 0;
    pub const actual_process_grade: u64 = 1 << 1;
    pub const actual_cpu_grade: u64 = 1 << 2;
    pub const actual_gpu_grade: u64 = 1 << 3;
    pub const known: u64 = completed_at | actual_process_grade |
        actual_cpu_grade | actual_gpu_grade;
};

pub const FeedbackFlags = struct {
    pub const process_owned: u32 = 1 << 0;
    pub const cpu_owned: u32 = 1 << 1;
    pub const gpu_owned: u32 = 1 << 2;
    pub const rollback_payload_persisted: u32 = 1 << 3;
    pub const known: u32 = process_owned | cpu_owned | gpu_owned |
        rollback_payload_persisted;
};

pub const SnapshotFlags = struct {
    pub const has_invalid_facts: u32 = 1 << 0;
    pub const event_boost_active: u32 = 1 << 1;
    pub const score_only: u32 = 1 << 2;
    pub const awaiting_feedback: u32 = 1 << 3;
    pub const requires_authoritative_resync: u32 = 1 << 4;
    pub const known: u32 = has_invalid_facts | event_boost_active | score_only |
        awaiting_feedback | requires_authoritative_resync;
};

pub const SnapshotRowFlags = struct {
    pub const process_owned: u32 = 1 << 0;
    pub const cpu_owned: u32 = 1 << 1;
    pub const gpu_owned: u32 = 1 << 2;
    pub const process_inflight: u32 = 1 << 3;
    pub const cpu_inflight: u32 = 1 << 4;
    pub const gpu_inflight: u32 = 1 << 5;
    pub const freeze_ready: u32 = 1 << 6;
    pub const critical_fact: u32 = 1 << 7;
    pub const known: u32 = process_owned | cpu_owned | gpu_owned |
        process_inflight | cpu_inflight | gpu_inflight | freeze_ready |
        critical_fact;
};

pub const Reason = struct {
    pub const reserved_system_reason0: u64 = 1 << 0;
    pub const missing_process_identity: u64 = 1 << 1;
    pub const missing_base_score: u64 = 1 << 2;
    pub const missing_surface_facts: u64 = 1 << 3;
    pub const missing_cpu_metric: u64 = 1 << 4;
    pub const reserved_process_reason5: u64 = 1 << 5;
    pub const incomplete_gpu_metrics: u64 = 1 << 6;
    pub const ineligible: u64 = 1 << 7;
    pub const not_running: u64 = 1 << 8;
    pub const high_tier_blocks_optimization: u64 = 1 << 9;
    pub const middle_tier_blocks_enhancement: u64 = 1 << 10;
    pub const middle_tier_blocks_freeze: u64 = 1 << 11;
    pub const low_tier_blocks_enhancement: u64 = 1 << 12;
    pub const protection_level1_clamps_freeze: u64 = 1 << 13;
    pub const protection_level2_blocks_optimization: u64 = 1 << 14;
    pub const reserved_process_reason15: u64 = 1 << 15;
    pub const reserved_process_reason16: u64 = 1 << 16;
    pub const freeze_group_blocked: u64 = 1 << 17;
    pub const awaiting_stability: u64 = 1 << 18;
    pub const applied_matches_desired: u64 = 1 << 19;
    pub const cpu_capability_missing: u64 = 1 << 20;
    pub const gpu_capability_missing: u64 = 1 << 21;
    pub const capability_clamped: u64 = 1 << 22;
    pub const critical_fact_changed: u64 = 1 << 23;
    pub const critical_membership_changed: u64 = 1 << 24;
    pub const startup_grace_active: u64 = 1 << 25;
    pub const target_disappeared: u64 = 1 << 26;
    pub const feature_disabled: u64 = 1 << 27;
    pub const feedback_failed: u64 = 1 << 28;
    pub const ownership_lost: u64 = 1 << 29;
    pub const state_uncertain: u64 = 1 << 30;
    pub const retry_backoff: u64 = 1 << 31;
    pub const score_only: u64 = 1 << 32;
    pub const no_complete_snapshot: u64 = 1 << 33;
    pub const duplicate_cycle: u64 = 1 << 34;
    pub const atomic_group_compensation: u64 = 1 << 35;
    pub const reservation_timed_out: u64 = 1 << 37;
    pub const known: u64 = ((@as(u64, 1) << 38) - 1) &
        ~(@as(u64, reserved_process_reason5) | reserved_process_reason15 |
            reserved_process_reason16 | (@as(u64, 1) << 36));
};

pub const AdapterPolicyConfig = extern struct {
    state_multipliers: [runtime_state_count]f64,
    extreme_minimum_score: f64,
    normal_minimum_score: f64,
    optimize_minimum_score: f64,
};

pub const Config = extern struct {
    abi_version: u32,
    struct_size: u32,
    generation: u64,
    field_mask: u64,
    feature_flags: u64,

    max_processes: u32,
    max_software_groups: u32,
    max_gpu_states: u32,
    max_input_rows: u32,
    max_actions: u32,
    max_reservations: u32,
    max_atomic_groups: u32,

    normal_interval_ms: u32,
    event_interval_ms: u32,
    event_boost_ms: u32,
    game_start_grace_ms: u32,
    required_consecutive_decisions: u32,
    failure_retry_ms: u32,
    reservation_timeout_ms: u32,
    reserved_timing: [3]u32,

    process_state_multipliers: [runtime_state_count]f64,
    a1_minimum_cpu_score: f64,
    default_minimum_cpu_score_scale: f64,
    level1_maximum_cpu_score_scale: f64,
    level2_maximum_cpu_score_scale: f64,
    level3_maximum_cpu_score_scale: f64,
    low_tier_level4_maximum_cpu_score_scale: f64,
    reserved_process_policy0: f64,
    high_tier_minimum_base_score: f64,
    middle_tier_minimum_base_score: f64,

    cpu_adapter: AdapterPolicyConfig,
    gpu_adapter: AdapterPolicyConfig,
    reserved: [4]u64,
};

pub const Capacity = extern struct {
    abi_version: u32,
    struct_size: u32,
    config_generation: u64,
    input_row_struct_size: u32,
    cycle_input_struct_size: u32,
    action_struct_size: u32,
    feedback_struct_size: u32,
    snapshot_struct_size: u32,
    snapshot_row_struct_size: u32,
    input_row_capacity: u32,
    action_capacity: u32,
    feedback_capacity: u32,
    snapshot_row_capacity: u32,
    process_capacity: u32,
    software_capacity: u32,
    gpu_state_capacity: u32,
    atomic_group_capacity: u32,
    reserved: [2]u64,
};

pub const CycleInput = extern struct {
    abi_version: u32,
    struct_size: u32,
    input_row_struct_size: u32,
    action_struct_size: u32,
    config_generation: u64,
    cycle_sequence: u64,
    observed_at_ms: i64,
    valid_mask: u64,
    flags: u64,
    input_count: u32,
    action_capacity: u32,
    score_scheduling_generation: u64,
    cpu_score_source_fingerprint: u64,
    gpu_score_source_fingerprint: u64,
    maximum_actions_this_cycle: u32,
    reserved: u32,
};

pub const InputRow = extern struct {
    struct_size: u32,
    metric_kind: u8,
    software_kind: u8,
    protection_level: u8,
    reserved0: u8,
    valid_mask: u64,
    flags: u64,
    target_key: u64,
    software_key: u64,
    process_start_key: u64,
    base_score: f64,
    metric_value: f64,
    source_index: u32,
    process_id: u32,
    device_index: u32,
    score_member_count: u32,
    cpu_capability_mask: u8,
    gpu_capability_mask: u8,
    applied_process_grade: i8,
    applied_cpu_grade: u8,
    applied_gpu_grade: u8,
    reserved2: [3]u8,
    applied_epoch: u64,
    process_score: f64,
    software_score: f64,
};

pub const Action = extern struct {
    struct_size: u32,
    flags: u32,
    valid_mask: u64,
    reason_mask: u64,
    action_id: u64,
    plan_epoch: u64,
    config_generation: u64,
    atomic_group_id: u64,
    target_key: u64,
    software_key: u64,
    process_start_key: u64,
    cpu_score: f64,
    source_index: u32,
    process_id: u32,
    order_key: u32,
    wake_after_ms: u32,
    group_member_index: u32,
    group_member_count: u32,
    scope: u8,
    disposition: u8,
    domain_mask: u8,
    reserved0: u8,
    from_process_grade: i8,
    to_process_grade: i8,
    from_cpu_grade: u8,
    to_cpu_grade: u8,
    from_gpu_grade: u8,
    to_gpu_grade: u8,
    reserved1: [2]u8,
    gpu_score: f64,
};

pub const Feedback = extern struct {
    struct_size: u32,
    flags: u32,
    valid_mask: u64,
    action_id: u64,
    plan_epoch: u64,
    config_generation: u64,
    target_key: u64,
    software_key: u64,
    process_start_key: u64,
    completed_at_ms: i64,
    process_id: u32,
    system_error_code: u32,
    status: u8,
    scope: u8,
    actual_process_grade: i8,
    actual_cpu_grade: u8,
    actual_gpu_grade: u8,
    reserved0: [3]u8,
    reserved1: u64,
};

pub const Snapshot = extern struct {
    abi_version: u32,
    struct_size: u32,
    snapshot_row_struct_size: u32,
    flags: u32,
    config_generation: u64,
    cycle_sequence: u64,
    plan_epoch: u64,
    state_revision: u64,
    observed_at_ms: i64,
    next_wake_at_ms: i64,
    wake_after_ms: u32,
    action_count: u32,
    process_count: u32,
    software_count: u32,
    pending_count: u32,
    inflight_count: u32,
    invalid_fact_count: u32,
    snapshot_row_count: u32,
    reason_mask: u64,
    reserved: [2]u64,
};

pub const SnapshotRow = extern struct {
    struct_size: u32,
    flags: u32,
    valid_mask: u64,
    reason_mask: u64,
    target_key: u64,
    software_key: u64,
    process_start_key: u64,
    last_seen_cycle: u64,
    pending_first_seen_ms: i64,
    pending_last_seen_ms: i64,
    last_feedback_ms: i64,
    game_started_at_ms: i64,
    base_score: f64,
    cpu_score: f64,
    cpu_occupancy_percent: f64,
    reserved_process_score0: f64,
    reserved_process_ratio0: f64,
    adapter_cpu_score: f64,
    adapter_gpu_score: f64,
    source_index: u32,
    process_id: u32,
    process_pending_count: u32,
    cpu_pending_count: u32,
    gpu_pending_count: u32,
    failure_count: u32,
    row_kind: u8,
    software_kind: u8,
    runtime_state: u8,
    protection_level: u8,
    desired_process_grade: i8,
    applied_process_grade: i8,
    pending_process_grade: i8,
    desired_cpu_grade: u8,
    applied_cpu_grade: u8,
    pending_cpu_grade: u8,
    desired_gpu_grade: u8,
    applied_gpu_grade: u8,
    pending_gpu_grade: u8,
    cpu_capability_mask: u8,
    gpu_capability_mask: u8,
    reserved0: u8,
    reserved1: u64,
};

pub fn isValidProcessGrade(value: i8) bool {
    return value >= ProcessGrade.level4 and value <= ProcessGrade.a1;
}

pub fn isValidAdapterGrade(value: u8) bool {
    return value <= @intFromEnum(AdapterGrade.extreme);
}

pub fn isValidCapabilityMask(mask: u8) bool {
    const known: u8 = (@as(u8, 1) << adapter_grade_count) - 1;
    return (mask & ~known) == 0 and
        (mask == 0 or (mask & (@as(u8, 1) << @intFromEnum(AdapterGrade.normal))) != 0);
}

pub fn isValidConfig(config: *const Config) bool {
    return validateConfig(config) == .ok;
}

pub fn validateConfig(config: *const Config) Status {
    if (config.abi_version != abi_version or config.struct_size != @sizeOf(Config)) return .abi_mismatch;
    if (config.generation == 0 or config.field_mask != ConfigFields.required or
        (config.feature_flags & ~FeatureFlags.known) != 0 or !allZero(u64, &config.reserved) or
        !allZero(u32, &config.reserved_timing))
    {
        return .invalid_argument;
    }
    if (config.max_processes == 0 or config.max_software_groups == 0 or
        config.max_gpu_states == 0 or config.max_input_rows == 0 or
        config.max_actions == 0 or config.max_reservations == 0 or config.max_atomic_groups == 0)
    {
        return .invalid_argument;
    }
    const maximum_entities = std.math.add(u32, config.max_processes, config.max_software_groups) catch return .invalid_argument;
    const maximum_adapter_reservations = std.math.mul(u32, config.max_software_groups, 2) catch return .invalid_argument;
    const maximum_reservations = std.math.add(u32, config.max_processes, maximum_adapter_reservations) catch return .invalid_argument;
    if (config.max_actions < maximum_entities or config.max_reservations < maximum_reservations or
        config.max_atomic_groups < config.max_processes) return .invalid_argument;
    if (config.normal_interval_ms == 0 or config.event_interval_ms == 0 or
        config.event_boost_ms == 0 or config.game_start_grace_ms == 0 or
        config.required_consecutive_decisions == 0 or config.failure_retry_ms == 0 or
        config.reservation_timeout_ms == 0)
    {
        return .invalid_argument;
    }
    if (!finitePositive(config.a1_minimum_cpu_score) or
        config.reserved_process_policy0 != 0 or
        !base_score_tier.isValidThresholds(
            config.middle_tier_minimum_base_score,
            config.high_tier_minimum_base_score,
        ))
    {
        return .invalid_argument;
    }
    for (config.process_state_multipliers) |value| if (!finiteNonNegative(value)) return .invalid_argument;
    if (config.process_state_multipliers[@intFromEnum(RuntimeState.not_running)] != 0) {
        return .invalid_argument;
    }
    const scales = [_]f64{
        config.default_minimum_cpu_score_scale,
        config.level1_maximum_cpu_score_scale,
        config.level2_maximum_cpu_score_scale,
        config.level3_maximum_cpu_score_scale,
        config.low_tier_level4_maximum_cpu_score_scale,
    };
    for (scales) |value| if (!finiteNonNegative(value)) return .invalid_argument;
    if (config.level1_maximum_cpu_score_scale > config.default_minimum_cpu_score_scale or
        config.level2_maximum_cpu_score_scale > config.level1_maximum_cpu_score_scale or
        config.level3_maximum_cpu_score_scale > config.level2_maximum_cpu_score_scale or
        config.low_tier_level4_maximum_cpu_score_scale > config.level3_maximum_cpu_score_scale)
    {
        return .invalid_argument;
    }
    if (!isValidAdapterPolicy(&config.cpu_adapter) or
        !isValidAdapterPolicy(&config.gpu_adapter) or
        !stateMultipliersEqual(config.cpu_adapter.state_multipliers, config.process_state_multipliers) or
        !scoreRangeRemainsFinite(config)) return .invalid_argument;
    return .ok;
}

pub fn isValidCycleInput(input: *const CycleInput) bool {
    return validateCycleInput(input) == .ok;
}

pub fn validateCycleInput(input: *const CycleInput) Status {
    if (input.abi_version != abi_version or input.struct_size != @sizeOf(CycleInput) or
        input.input_row_struct_size != @sizeOf(InputRow) or input.action_struct_size != @sizeOf(Action))
    {
        return .abi_mismatch;
    }
    if (input.config_generation == 0 or input.cycle_sequence == 0 or
        (input.valid_mask & ~CycleValidity.known) != 0 or
        (input.valid_mask & CycleValidity.required) != CycleValidity.required or
        (input.flags & ~CycleFlags.known) != 0 or
        input.reserved != 0)
    {
        return .invalid_argument;
    }
    const has_score_generation = (input.valid_mask & CycleValidity.score_scheduling_generation) != 0;
    const has_cpu_fingerprint = (input.valid_mask & CycleValidity.cpu_score_source_fingerprint) != 0;
    const has_gpu_fingerprint = (input.valid_mask & CycleValidity.gpu_score_source_fingerprint) != 0;
    if (has_score_generation != (has_cpu_fingerprint or has_gpu_fingerprint) or
        has_score_generation != (input.score_scheduling_generation != 0) or
        has_cpu_fingerprint != (input.cpu_score_source_fingerprint != 0) or
        has_gpu_fingerprint != (input.gpu_score_source_fingerprint != 0))
    {
        return .invalid_argument;
    }
    return .ok;
}

pub fn validateInputRow(row: *const InputRow, expected_source_index: u32) Status {
    if (row.struct_size != @sizeOf(InputRow) or row.reserved0 != 0 or
        !allZero(u8, &row.reserved2))
    {
        return .abi_mismatch;
    }
    if (row.source_index != expected_source_index or (row.valid_mask & ~InputValidity.known) != 0 or
        (row.flags & ~InputFlags.known) != 0 or row.metric_kind > @intFromEnum(MetricKind.vram_usage_percent) or
        row.software_kind > @intFromEnum(SoftwareKind.general_application) or row.protection_level > 2)
    {
        return .invalid_argument;
    }
    const has_process = (row.valid_mask & InputValidity.process_identity) != 0;
    if (has_process) {
        if (row.target_key == 0 or row.process_id == 0 or row.process_start_key == 0) return .invalid_argument;
    } else if (row.target_key != 0 or row.process_id != 0 or row.process_start_key != 0) {
        return .invalid_argument;
    }
    const has_software = (row.valid_mask & InputValidity.software_identity) != 0;
    if (has_software) {
        if (row.software_key == 0) return .invalid_argument;
    } else if (row.software_key != 0) {
        return .invalid_argument;
    }
    if (!has_process and has_software) {
        const process_only_validity = InputValidity.protection | InputValidity.metric |
            InputValidity.applied_process_grade;
        const process_only_flags = InputFlags.running | InputFlags.foreground_focused |
            InputFlags.has_visible_window | InputFlags.has_background_window |
            InputFlags.has_hidden_window | InputFlags.process_cpu_metrics_complete |
            InputFlags.process_gpu_metrics_complete | InputFlags.can_apply_process_policy |
            InputFlags.hardware_scheduling_eligible | InputFlags.owns_process_grade;
        if ((row.valid_mask & process_only_validity) != 0 or
            (row.flags & process_only_flags) != 0) return .invalid_argument;
    }
    if ((row.valid_mask & InputValidity.base_score) != 0 and !finitePercent(row.base_score)) return .invalid_argument;
    if ((row.valid_mask & InputValidity.cpu_capabilities) != 0 and !isValidCapabilityMask(row.cpu_capability_mask)) return .invalid_argument;
    if ((row.valid_mask & InputValidity.gpu_capabilities) != 0 and !isValidCapabilityMask(row.gpu_capability_mask)) return .invalid_argument;
    if ((row.valid_mask & InputValidity.applied_process_grade) != 0 and !isValidProcessGrade(row.applied_process_grade)) return .invalid_argument;
    if ((row.valid_mask & InputValidity.applied_cpu_grade) != 0 and !isValidAdapterGrade(row.applied_cpu_grade)) return .invalid_argument;
    if ((row.valid_mask & InputValidity.applied_gpu_grade) != 0 and !isValidAdapterGrade(row.applied_gpu_grade)) return .invalid_argument;

    const metric_valid = (row.valid_mask & InputValidity.metric) != 0;
    const metric: MetricKind = @enumFromInt(row.metric_kind);
    const has_process_score = (row.valid_mask & InputValidity.process_score) != 0;
    const has_software_score = (row.valid_mask & InputValidity.software_score) != 0;
    const cpu_score_only = !metric_valid and metric == .cpu_usage_percent and (has_process_score or has_software_score);
    if (metric_valid and metric == .none) return .invalid_argument;
    if (!metric_valid and ((!cpu_score_only and metric != .none) or row.metric_value != 0)) return .invalid_argument;
    if (metric_valid) {
        if (!std.math.isFinite(row.metric_value)) return .invalid_argument;
        switch (metric) {
            .cpu_usage_percent, .gpu_usage_percent, .vram_usage_percent => {
                if (!has_process or !finitePercent(row.metric_value)) return .invalid_argument;
            },
            .none => unreachable,
        }
    }

    const has_score_member_count = (row.valid_mask & InputValidity.score_member_count) != 0;
    if (!has_process_score and row.process_score != 0) return .invalid_argument;
    if (!has_software_score and row.software_score != 0) return .invalid_argument;
    if (!has_score_member_count and row.score_member_count != 0) return .invalid_argument;
    if (has_process_score and ((!metric_valid and !cpu_score_only) or !has_process or
        (metric != .cpu_usage_percent and metric != .gpu_usage_percent) or
        !finiteNonNegative(row.process_score))) return .invalid_argument;
    if (has_software_score != has_score_member_count) return .invalid_argument;
    if (has_software_score and ((!metric_valid and !cpu_score_only) or !has_process or !has_software or
        (metric != .cpu_usage_percent and metric != .gpu_usage_percent) or
        row.score_member_count == 0 or !finiteNonNegative(row.software_score)))
    {
        return .invalid_argument;
    }

    const owns_process = (row.flags & InputFlags.owns_process_grade) != 0;
    const owns_cpu = (row.flags & InputFlags.owns_cpu_grade) != 0;
    const owns_gpu = (row.flags & InputFlags.owns_gpu_grade) != 0;
    if (owns_process and (row.valid_mask & InputValidity.applied_process_grade) == 0) return .invalid_argument;
    if (owns_cpu and (row.valid_mask & InputValidity.applied_cpu_grade) == 0) return .invalid_argument;
    if (owns_gpu and (row.valid_mask & InputValidity.applied_gpu_grade) == 0) return .invalid_argument;
    if ((owns_process or owns_cpu or owns_gpu) and row.applied_epoch == 0) return .invalid_argument;
    if (!owns_process and !owns_cpu and !owns_gpu and row.applied_epoch != 0) return .invalid_argument;
    if ((row.valid_mask & (InputValidity.applied_cpu_grade | InputValidity.applied_gpu_grade)) != 0 and !has_software) return .invalid_argument;
    return .ok;
}

pub fn isValidFeedback(feedback: *const Feedback) bool {
    return validateFeedback(feedback) == .ok;
}

pub fn validateFeedback(feedback: *const Feedback) Status {
    if (feedback.struct_size != @sizeOf(Feedback)) return .abi_mismatch;
    if (feedback.action_id == 0 or feedback.plan_epoch == 0 or feedback.config_generation == 0 or
        (feedback.flags & ~FeedbackFlags.known) != 0 or
        (feedback.valid_mask & ~FeedbackValidity.known) != 0 or
        feedback.status < @intFromEnum(FeedbackStatus.succeeded) or
        feedback.status > @intFromEnum(FeedbackStatus.state_uncertain) or
        feedback.scope < @intFromEnum(ActionScope.process_policy) or
        feedback.scope > @intFromEnum(ActionScope.adapter_software) or
        !allZero(u8, &feedback.reserved0) or feedback.reserved1 != 0)
    {
        return .invalid_argument;
    }
    if ((feedback.valid_mask & FeedbackValidity.actual_process_grade) != 0 and
        !isValidProcessGrade(feedback.actual_process_grade)) return .invalid_argument;
    if ((feedback.valid_mask & FeedbackValidity.actual_cpu_grade) != 0 and
        !isValidAdapterGrade(feedback.actual_cpu_grade)) return .invalid_argument;
    if ((feedback.valid_mask & FeedbackValidity.actual_gpu_grade) != 0 and
        !isValidAdapterGrade(feedback.actual_gpu_grade)) return .invalid_argument;
    return .ok;
}

pub fn fillCapacity(config: *const Config, capacity: *Capacity) void {
    capacity.* = .{
        .abi_version = abi_version,
        .struct_size = @sizeOf(Capacity),
        .config_generation = config.generation,
        .input_row_struct_size = @sizeOf(InputRow),
        .cycle_input_struct_size = @sizeOf(CycleInput),
        .action_struct_size = @sizeOf(Action),
        .feedback_struct_size = @sizeOf(Feedback),
        .snapshot_struct_size = @sizeOf(Snapshot),
        .snapshot_row_struct_size = @sizeOf(SnapshotRow),
        .input_row_capacity = config.max_input_rows,
        .action_capacity = config.max_actions,
        .feedback_capacity = config.max_reservations,
        .snapshot_row_capacity = config.max_processes +| config.max_software_groups,
        .process_capacity = config.max_processes,
        .software_capacity = config.max_software_groups,
        .gpu_state_capacity = config.max_gpu_states,
        .atomic_group_capacity = config.max_atomic_groups,
        .reserved = [_]u64{0} ** 2,
    };
}

fn isValidAdapterPolicy(policy: *const AdapterPolicyConfig) bool {
    for (policy.state_multipliers) |value| if (!finiteNonNegative(value)) return false;
    return policy.state_multipliers[@intFromEnum(RuntimeState.not_running)] == 0 and
        finiteNonNegative(policy.extreme_minimum_score) and
        finiteNonNegative(policy.normal_minimum_score) and
        finiteNonNegative(policy.optimize_minimum_score) and
        policy.optimize_minimum_score <= policy.normal_minimum_score and
        policy.normal_minimum_score <= policy.extreme_minimum_score;
}

fn stateMultipliersEqual(left: [runtime_state_count]f64, right: [runtime_state_count]f64) bool {
    for (left, right) |left_value, right_value| {
        if (left_value != right_value) return false;
    }
    return true;
}

fn scoreRangeRemainsFinite(config: *const Config) bool {
    var maximum_cpu_state_multiplier: f64 = 0;
    for (config.process_state_multipliers) |value| {
        maximum_cpu_state_multiplier = @max(maximum_cpu_state_multiplier, value);
    }
    var maximum_gpu_state_multiplier: f64 = 0;
    for (config.gpu_adapter.state_multipliers) |value| {
        maximum_gpu_state_multiplier = @max(maximum_gpu_state_multiplier, value);
    }
    const maximum_cpu_process_score = 100 * maximum_cpu_state_multiplier;
    const maximum_gpu_process_score = 100 * maximum_gpu_state_multiplier;
    if (!finiteNonNegative(maximum_cpu_process_score) or
        !finiteNonNegative(maximum_gpu_process_score)) return false;
    const maximum_cpu_software_score = maximum_cpu_process_score *
        @as(f64, @floatFromInt(config.max_processes));
    const maximum_gpu_software_score = maximum_gpu_process_score *
        @as(f64, @floatFromInt(config.max_processes));
    if (!finiteNonNegative(maximum_cpu_software_score) or
        !finiteNonNegative(maximum_gpu_software_score)) return false;
    return finiteNonNegative(maximum_cpu_software_score) and
        finiteNonNegative(maximum_gpu_software_score);
}

fn finitePercent(value: f64) bool {
    return std.math.isFinite(value) and value >= 0 and value <= 100;
}

fn finitePositive(value: f64) bool {
    return std.math.isFinite(value) and value > 0;
}

fn finiteNonNegative(value: f64) bool {
    return std.math.isFinite(value) and value >= 0;
}

fn allZero(comptime T: type, values: []const T) bool {
    for (values) |value| if (value != 0) return false;
    return true;
}
