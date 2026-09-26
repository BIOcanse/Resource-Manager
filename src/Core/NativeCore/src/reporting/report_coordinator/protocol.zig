const std = @import("std");

pub const abi_version: u32 = 0x0006_0000;

pub const TrustFamily = struct {
    pub const all: u64 = std.math.maxInt(u64);
};

pub const ConfigFlags = struct {
    pub const known: u64 = 0;
};

pub const RuleFlags = struct {
    pub const rolling: u32 = 1 << 0;
    pub const known: u32 = rolling;
};

pub const FactFlags = struct {
    pub const current_valid: u32 = 1 << 0;
    pub const delta_valid: u32 = 1 << 1;
    pub const peak_valid: u32 = 1 << 2;
    pub const event_count_valid: u32 = 1 << 3;
    pub const request_event_count_valid: u32 = 1 << 4;
    pub const connection_event_count_valid: u32 = 1 << 5;
    pub const sample_hit_count_valid: u32 = 1 << 6;
    pub const secondary_current_valid: u32 = 1 << 7;
    pub const known: u32 = current_valid | delta_valid | peak_valid | event_count_valid |
        request_event_count_valid | connection_event_count_valid | sample_hit_count_valid |
        secondary_current_valid;
};

pub const ObservationFlags = struct {
    pub const active: u32 = 1 << 0;
    pub const current_valid: u32 = 1 << 1;
    pub const known: u32 = active | current_valid;
};

pub const ReportFlags = struct {
    pub const active: u32 = 1 << 0;
    pub const trusted_suppressed: u32 = 1 << 1;
    pub const known: u32 = active | trusted_suppressed;
};

pub const PersistenceFlags = struct {
    pub const delete: u32 = 1 << 0;
    pub const active: u32 = 1 << 1;
    pub const trusted_suppressed: u32 = 1 << 2;
    pub const current_valid: u32 = 1 << 3;
    pub const secondary_current_valid: u32 = 1 << 4;
    pub const known: u32 = delete | active | trusted_suppressed | current_valid |
        secondary_current_valid;
};

pub const PlanFlags = struct {
    pub const reports_available: u64 = 1 << 0;
    pub const persistence_available: u64 = 1 << 1;
    pub const next_wake_valid: u64 = 1 << 2;
    pub const has_more_reports: u64 = 1 << 3;
    pub const has_more_persistence: u64 = 1 << 4;
    pub const clock_rollback_observed: u64 = 1 << 5;
    pub const known: u64 = reports_available | persistence_available | next_wake_valid |
        has_more_reports | has_more_persistence | clock_rollback_observed;
};

pub const SourceSnapshotValid = struct {
    pub const command_monotonic: u64 = 1 << 0;
    pub const command_utc: u64 = 1 << 1;
    pub const observed_utc: u64 = 1 << 2;
    pub const source_identity: u64 = 1 << 3;
    pub const source_generation: u64 = 1 << 4;
    pub const coverage_scope: u64 = 1 << 5;
    pub const snapshot_identity: u64 = 1 << 6;
    pub const facts: u64 = 1 << 7;
    pub const required: u64 = command_monotonic | command_utc | observed_utc |
        source_identity | source_generation | coverage_scope | snapshot_identity | facts;
    pub const known: u64 = required;
};

pub const RuleReplaceValid = struct {
    pub const command_monotonic: u64 = 1 << 0;
    pub const command_utc: u64 = 1 << 1;
    pub const rules: u64 = 1 << 2;
    pub const required: u64 = command_monotonic | command_utc | rules;
    pub const known: u64 = required;
};

pub const TrustCommandValid = struct {
    pub const command_monotonic: u64 = 1 << 0;
    pub const command_utc: u64 = 1 << 1;
    pub const target: u64 = 1 << 2;
    pub const family: u64 = 1 << 3;
    pub const required: u64 = command_monotonic | command_utc | target | family;
    pub const known: u64 = required;
};

pub const ImportValid = struct {
    pub const command_monotonic: u64 = 1 << 0;
    pub const command_utc: u64 = 1 << 1;
    pub const rows: u64 = 1 << 2;
    pub const required: u64 = command_monotonic | command_utc | rows;
    pub const known: u64 = required;
};

pub const PlanValid = struct {
    pub const command_monotonic: u64 = 1 << 0;
    pub const command_utc: u64 = 1 << 1;
    pub const output_limits: u64 = 1 << 2;
    pub const required: u64 = command_monotonic | command_utc | output_limits;
    pub const known: u64 = required;
};

pub const FeedbackValid = struct {
    pub const command_monotonic: u64 = 1 << 0;
    pub const command_utc: u64 = 1 << 1;
    pub const rows: u64 = 1 << 2;
    pub const required: u64 = command_monotonic | command_utc | rows;
    pub const known: u64 = required;
};

pub const SourceStatus = enum(u32) {
    invalid = 0,
    complete = 1,
    unavailable = 2,
    skipped = 3,
};

pub const MetricSelector = enum(u32) {
    current = 1,
    average = 2,
    peak = 3,
    sum_24h = 4,
    sum_7d = 5,
    events_24h = 6,
    events_7d = 7,
    request_events_24h = 8,
    request_events_7d = 9,
    connection_events_24h = 10,
    connection_events_7d = 11,
    sample_hits_24h = 12,
    sample_hits_7d = 13,
};

pub const Comparison = enum(u32) {
    greater_or_equal = 1,
    less_or_equal = 2,
    equal = 3,
    not_equal = 4,
    bit_all_set = 5,
    left_minus_right_greater_or_equal = 6,
    left_minus_right_less_or_equal = 7,
};

pub const TrustCommandKind = enum(u32) {
    add = 1,
    remove = 2,
};

pub const PersistenceKind = enum(u32) {
    source = 1,
    observation = 2,
    bucket = 3,
    report = 4,
    trust = 5,
    metadata = 6,
};

pub const FeedbackStatus = enum(u32) {
    persisted = 1,
    failed = 2,
};

pub const Config = extern struct {
    abi_version: u32,
    struct_size: u32,
    generation: u64,
    session_instance_low: u64,
    session_instance_high: u64,
    maximum_source_count: u32,
    maximum_rule_count: u32,
    maximum_observation_count: u32,
    maximum_report_count: u32,
    maximum_trust_count: u32,
    maximum_bucket_count: u32,
    maximum_persistence_operation_count: u32,
    maximum_report_output_count: u32,
    source_index_capacity: u32,
    rule_index_capacity: u32,
    observation_index_capacity: u32,
    report_index_capacity: u32,
    trust_index_capacity: u32,
    bucket_index_capacity: u32,
    bucket_width_milliseconds: u64,
    window_24h_milliseconds: u64,
    window_7d_milliseconds: u64,
    maximum_future_skew_milliseconds: u64,
    default_stale_after_milliseconds: u64,
    default_retention_milliseconds: u64,
    resident_byte_budget: u64,
    flags: u64,
    maximum_rolling_observation_count: u32,
    planned_persistence_index_capacity: u32,
    metadata_checkpoint_interval_milliseconds: u64,
    reserved: [6]u64,
};

pub const Capacity = extern struct {
    struct_size: u32,
    source_capacity: u32,
    rule_capacity: u32,
    observation_capacity: u32,
    report_capacity: u32,
    trust_capacity: u32,
    bucket_capacity: u32,
    persistence_operation_capacity: u32,
    report_output_capacity: u32,
    source_index_capacity: u32,
    rule_index_capacity: u32,
    observation_index_capacity: u32,
    report_index_capacity: u32,
    trust_index_capacity: u32,
    bucket_index_capacity: u32,
    rolling_observation_capacity: u32,
    resident_byte_count: u64,
    planned_persistence_index_capacity: u32,
    reserved_u32: u32,
    reserved: [2]u64,
};

pub const RuleInput = extern struct {
    struct_size: u32,
    flags: u32,
    rule_handle: u64,
    rule_generation: u64,
    source_handle: u64,
    coverage_scope_handle: u64,
    family_handle: u64,
    report_type_handle: u64,
    resource_kind_handle: u64,
    payload_handle: u64,
    metric_selector: u32,
    comparison: u32,
    priority: u32,
    severity: u32,
    required_consecutive_hits: u32,
    required_consecutive_misses: u32,
    minimum_sample_duration_milliseconds: u64,
    activation_threshold: f64,
    clear_threshold: f64,
    stale_after_milliseconds: u64,
    retention_milliseconds: u64,
    predicate_group_handle: u64,
    predicate_index: u32,
    predicate_count: u32,
    reserved: [1]u64,
};

pub const RuleReplaceInput = extern struct {
    abi_version: u32,
    struct_size: u32,
    configuration_generation: u64,
    operation_epoch: u64,
    command_monotonic_milliseconds: u64,
    command_utc_milliseconds: i64,
    valid_mask: u64,
    rule_count: u32,
    flags: u32,
    reserved: [2]u64,
};

pub const FactInput = extern struct {
    struct_size: u32,
    flags: u32,
    rule_handle: u64,
    rule_generation: u64,
    target_handle: u64,
    evidence_payload_handle: u64,
    fact_sequence: u64,
    current_value: f64,
    delta_value: f64,
    peak_value: f64,
    event_count: u64,
    request_event_count: u64,
    connection_event_count: u64,
    sample_hit_count: u64,
    sample_duration_milliseconds: u64,
    secondary_current_value: f64,
    reserved: [1]u64,
};

pub const SourceSnapshotInput = extern struct {
    abi_version: u32,
    struct_size: u32,
    configuration_generation: u64,
    operation_epoch: u64,
    command_monotonic_milliseconds: u64,
    command_utc_milliseconds: i64,
    observed_at_utc_milliseconds: i64,
    source_handle: u64,
    source_generation: u64,
    coverage_scope_handle: u64,
    source_snapshot_epoch: u64,
    valid_mask: u64,
    fact_count: u32,
    status: u32,
    flags: u64,
    reserved: [1]u64,
};

pub const TrustCommandInput = extern struct {
    abi_version: u32,
    struct_size: u32,
    configuration_generation: u64,
    operation_epoch: u64,
    command_monotonic_milliseconds: u64,
    command_utc_milliseconds: i64,
    target_handle: u64,
    family_handle: u64,
    payload_handle: u64,
    valid_mask: u64,
    command_kind: u32,
    flags: u32,
    reserved: [1]u64,
};

pub const ImportInput = extern struct {
    abi_version: u32,
    struct_size: u32,
    configuration_generation: u64,
    import_generation: u64,
    operation_epoch: u64,
    command_monotonic_milliseconds: u64,
    command_utc_milliseconds: i64,
    valid_mask: u64,
    row_count: u32,
    flags: u32,
    reserved: [1]u64,
};

pub const PlanInput = extern struct {
    abi_version: u32,
    struct_size: u32,
    configuration_generation: u64,
    operation_epoch: u64,
    plan_epoch: u64,
    command_monotonic_milliseconds: u64,
    command_utc_milliseconds: i64,
    valid_mask: u64,
    report_limit: u32,
    persistence_limit: u32,
    flags: u64,
    reserved: [1]u64,
};

pub const ReportOutput = extern struct {
    struct_size: u32,
    flags: u32,
    report_handle: u64,
    slot_generation: u64,
    target_handle: u64,
    family_handle: u64,
    rule_handle: u64,
    report_type_handle: u64,
    resource_kind_handle: u64,
    payload_handle: u64,
    evidence_payload_handle: u64,
    created_at_utc_milliseconds: i64,
    updated_at_utc_milliseconds: i64,
    last_observed_at_utc_milliseconds: i64,
    current_value: f64,
    average_value: f64,
    peak_value: f64,
    window_24h_value: f64,
    window_7d_value: f64,
    sample_count: u64,
    active_sample_count: u64,
    priority: u32,
    severity: u32,
    slot_index: u32,
    reserved_u32: u32,
    reserved: [2]u64,
};

pub const PersistenceOperation = extern struct {
    struct_size: u32,
    flags: u32,
    session_instance_low: u64,
    session_instance_high: u64,
    plan_epoch: u64,
    mutation_version: u64,
    identity_handle: u64,
    slot_generation: u64,
    source_handle: u64,
    source_generation: u64,
    source_snapshot_epoch: u64,
    source_last_complete_at_utc_milliseconds: i64,
    source_status: u32,
    source_reserved_u32: u32,
    coverage_scope_handle: u64,
    rule_handle: u64,
    rule_generation: u64,
    target_handle: u64,
    family_handle: u64,
    report_handle: u64,
    report_type_handle: u64,
    resource_kind_handle: u64,
    payload_handle: u64,
    evidence_payload_handle: u64,
    first_observed_at_utc_milliseconds: i64,
    last_observed_at_utc_milliseconds: i64,
    bucket_start_utc_milliseconds: i64,
    current_value: f64,
    secondary_current_value: f64,
    value_sum: f64,
    peak_value: f64,
    window_delta_value: f64,
    sample_duration_milliseconds: u64,
    sample_count: u64,
    active_sample_count: u64,
    event_count: u64,
    request_event_count: u64,
    connection_event_count: u64,
    sample_hit_count: u64,
    consecutive_hits: u32,
    consecutive_misses: u32,
    priority: u32,
    severity: u32,
    operation_kind: u32,
    slot_index: u32,
    fact_sequence: u64,
    report_observed_at_utc_milliseconds: i64,
    checkpoint_schema_version: u32,
    checkpoint_reserved_u32: u32,
    checkpoint_logical_utc_milliseconds: i64,
};

pub const PersistenceFeedbackInput = extern struct {
    abi_version: u32,
    struct_size: u32,
    configuration_generation: u64,
    operation_epoch: u64,
    feedback_epoch: u64,
    command_monotonic_milliseconds: u64,
    command_utc_milliseconds: i64,
    valid_mask: u64,
    feedback_count: u32,
    flags: u32,
    reserved: [1]u64,
};

pub const PersistenceFeedback = extern struct {
    struct_size: u32,
    status: u32,
    session_instance_low: u64,
    session_instance_high: u64,
    plan_epoch: u64,
    mutation_version: u64,
    identity_handle: u64,
    slot_generation: u64,
    operation_kind: u32,
    slot_index: u32,
    flags: u32,
    reserved_u32: u32,
    reserved: [1]u64,
};

pub const PlanOutput = extern struct {
    struct_size: u32,
    report_count: u32,
    persistence_operation_count: u32,
    active_report_count: u32,
    trusted_report_count: u32,
    source_count: u32,
    observation_count: u32,
    bucket_count: u32,
    report_total_count: u32,
    trust_count: u32,
    reserved_u32: u32,
    first_mutation_version: u64,
    last_mutation_version: u64,
    session_instance_low: u64,
    session_instance_high: u64,
    last_operation_epoch: u64,
    last_plan_epoch: u64,
    last_feedback_epoch: u64,
    last_command_monotonic_milliseconds: u64,
    logical_utc_milliseconds: i64,
    next_wake_utc_milliseconds: i64,
    state_revision: u64,
    import_generation: u64,
    flags: u64,
    resident_byte_count: u64,
    reserved: [2]u64,
};

pub fn validConfig(config: *const Config) bool {
    if (config.abi_version != abi_version or config.struct_size != @sizeOf(Config)) return false;
    if (config.generation == 0 or
        config.session_instance_low == 0 or
        config.session_instance_high == 0 or
        config.flags != ConfigFlags.known) return false;
    if (!allZero(config.reserved[0..])) return false;
    if (config.maximum_source_count == 0 or
        config.maximum_rule_count == 0 or
        config.maximum_observation_count == 0 or
        config.maximum_report_count == 0 or
        config.maximum_trust_count == 0 or
        config.maximum_bucket_count == 0 or
        config.maximum_persistence_operation_count == 0 or
        config.maximum_report_output_count == 0) return false;
    if (config.maximum_rolling_observation_count == 0 or
        config.maximum_rolling_observation_count > config.maximum_observation_count) return false;
    var maximum_dirty_count: u64 = 1;
    maximum_dirty_count += config.maximum_source_count;
    maximum_dirty_count += config.maximum_observation_count;
    maximum_dirty_count += config.maximum_bucket_count;
    maximum_dirty_count += config.maximum_report_count;
    maximum_dirty_count += config.maximum_trust_count;
    if (maximum_dirty_count > std.math.maxInt(u32) or
        config.maximum_persistence_operation_count < maximum_dirty_count) return false;
    if (!validIndexCapacity(config.source_index_capacity, config.maximum_source_count) or
        !validIndexCapacity(config.rule_index_capacity, config.maximum_rule_count) or
        !validIndexCapacity(config.observation_index_capacity, config.maximum_observation_count) or
        !validIndexCapacity(config.report_index_capacity, config.maximum_report_count) or
        !validIndexCapacity(config.trust_index_capacity, config.maximum_trust_count) or
        !validIndexCapacity(config.bucket_index_capacity, config.maximum_bucket_count) or
        !validIndexCapacity(
            config.planned_persistence_index_capacity,
            config.maximum_persistence_operation_count,
        )) return false;
    if (config.bucket_width_milliseconds == 0 or
        config.bucket_width_milliseconds > std.math.maxInt(i64) or
        config.window_24h_milliseconds == 0 or
        config.window_7d_milliseconds <= config.window_24h_milliseconds or
        config.window_24h_milliseconds % config.bucket_width_milliseconds != 0 or
        config.window_7d_milliseconds % config.bucket_width_milliseconds != 0) return false;
    const buckets_per_rolling_observation = std.math.add(
        u64,
        config.window_7d_milliseconds / config.bucket_width_milliseconds,
        1,
    ) catch return false;
    const minimum_bucket_capacity = std.math.mul(
        u64,
        config.maximum_rolling_observation_count,
        buckets_per_rolling_observation,
    ) catch return false;
    if (minimum_bucket_capacity > std.math.maxInt(u32) or
        config.maximum_bucket_count < minimum_bucket_capacity) return false;
    if (config.maximum_future_skew_milliseconds == 0 or
        config.default_stale_after_milliseconds == 0 or
        config.default_retention_milliseconds < config.default_stale_after_milliseconds or
        config.metadata_checkpoint_interval_milliseconds == 0 or
        config.metadata_checkpoint_interval_milliseconds > std.math.maxInt(i64) or
        config.resident_byte_budget == 0) return false;
    return true;
}

pub fn validRule(rule: *const RuleInput) bool {
    if (rule.struct_size != @sizeOf(RuleInput) or
        (rule.flags & ~RuleFlags.known) != 0 or
        !allZero(rule.reserved[0..])) return false;
    if (rule.rule_handle == 0 or rule.rule_generation == 0 or
        rule.source_handle == 0 or rule.coverage_scope_handle == 0 or rule.family_handle == 0 or
        rule.report_type_handle == 0 or rule.resource_kind_handle == 0 or
        rule.predicate_group_handle == 0 or
        rule.predicate_count == 0 or rule.predicate_index >= rule.predicate_count or
        rule.required_consecutive_hits == 0 or rule.required_consecutive_misses == 0) return false;
    if (!validMetricSelector(rule.metric_selector) or !validComparison(rule.comparison)) {
        return false;
    }
    const selector: MetricSelector = @enumFromInt(rule.metric_selector);
    const rolling = selector != .current and selector != .average and selector != .peak;
    if (rolling != ((rule.flags & RuleFlags.rolling) != 0)) return false;
    const comparison: Comparison = @enumFromInt(rule.comparison);
    if ((comparison == .left_minus_right_greater_or_equal or
        comparison == .left_minus_right_less_or_equal) and selector != .current) return false;
    if ((comparison == .equal or comparison == .not_equal or comparison == .bit_all_set) and
        rule.activation_threshold != rule.clear_threshold) return false;
    if (comparison == .bit_all_set and
        (!validU32Scalar(rule.activation_threshold) or rule.activation_threshold == 0)) return false;
    if (!std.math.isFinite(rule.activation_threshold) or !std.math.isFinite(rule.clear_threshold)) {
        return false;
    }
    if (rule.minimum_sample_duration_milliseconds == 0) return false;
    return true;
}

pub fn validFact(fact: *const FactInput) bool {
    if (fact.struct_size != @sizeOf(FactInput) or
        (fact.flags & ~FactFlags.known) != 0 or
        !allZero(fact.reserved[0..])) return false;
    if (fact.rule_handle == 0 or fact.rule_generation == 0 or
        fact.target_handle == 0 or fact.fact_sequence == 0 or
        fact.sample_duration_milliseconds == 0) return false;
    if ((fact.flags & FactFlags.current_valid) != 0 and !std.math.isFinite(fact.current_value)) return false;
    if ((fact.flags & FactFlags.delta_valid) != 0 and !std.math.isFinite(fact.delta_value)) return false;
    if ((fact.flags & FactFlags.peak_valid) != 0 and !std.math.isFinite(fact.peak_value)) return false;
    if ((fact.flags & FactFlags.secondary_current_valid) != 0 and
        !std.math.isFinite(fact.secondary_current_value)) return false;
    return true;
}

pub fn validRuleReplace(input: *const RuleReplaceInput, config: *const Config) bool {
    return input.abi_version == abi_version and
        input.struct_size == @sizeOf(RuleReplaceInput) and
        input.configuration_generation == config.generation and
        input.operation_epoch != 0 and
        input.command_monotonic_milliseconds != 0 and
        input.command_utc_milliseconds >= 0 and
        input.valid_mask == RuleReplaceValid.required and
        input.rule_count <= config.maximum_rule_count and
        input.flags == 0 and
        allZero(input.reserved[0..]);
}

pub fn validSourceSnapshot(input: *const SourceSnapshotInput, config: *const Config) bool {
    if (input.abi_version != abi_version or
        input.struct_size != @sizeOf(SourceSnapshotInput) or
        input.configuration_generation != config.generation or
        input.operation_epoch == 0 or
        input.command_monotonic_milliseconds == 0 or
        input.command_utc_milliseconds < 0 or
        input.observed_at_utc_milliseconds < 0 or
        input.source_handle == 0 or input.source_generation == 0 or
        input.coverage_scope_handle == 0 or
        input.source_snapshot_epoch == 0 or
        input.valid_mask != SourceSnapshotValid.required or
        input.flags != 0 or
        !allZero(input.reserved[0..])) return false;
    return validSourceStatus(input.status) and
        input.fact_count <= config.maximum_observation_count;
}

pub fn validTrustCommand(input: *const TrustCommandInput, config: *const Config) bool {
    if (input.abi_version != abi_version or
        input.struct_size != @sizeOf(TrustCommandInput) or
        input.configuration_generation != config.generation or
        input.operation_epoch == 0 or
        input.command_monotonic_milliseconds == 0 or
        input.command_utc_milliseconds < 0 or
        input.target_handle == 0 or input.family_handle == 0 or
        input.valid_mask != TrustCommandValid.required or
        input.flags != 0 or !allZero(input.reserved[0..])) return false;
    return validTrustCommandKind(input.command_kind);
}

pub fn validImport(input: *const ImportInput, config: *const Config) bool {
    return input.abi_version == abi_version and
        input.struct_size == @sizeOf(ImportInput) and
        input.configuration_generation == config.generation and
        input.import_generation != 0 and
        input.operation_epoch != 0 and
        input.command_monotonic_milliseconds != 0 and
        input.command_utc_milliseconds >= 0 and
        input.valid_mask == ImportValid.required and
        input.flags == 0 and
        allZero(input.reserved[0..]);
}

pub fn validPlan(input: *const PlanInput, config: *const Config) bool {
    return input.abi_version == abi_version and
        input.struct_size == @sizeOf(PlanInput) and
        input.configuration_generation == config.generation and
        input.operation_epoch != 0 and
        input.plan_epoch != 0 and
        input.command_monotonic_milliseconds != 0 and
        input.command_utc_milliseconds >= 0 and
        input.valid_mask == PlanValid.required and
        input.report_limit > 0 and input.report_limit <= config.maximum_report_output_count and
        input.persistence_limit > 0 and
        input.persistence_limit <= config.maximum_persistence_operation_count and
        input.flags == 0 and
        allZero(input.reserved[0..]);
}

pub fn validFeedbackInput(input: *const PersistenceFeedbackInput, config: *const Config) bool {
    return input.abi_version == abi_version and
        input.struct_size == @sizeOf(PersistenceFeedbackInput) and
        input.configuration_generation == config.generation and
        input.operation_epoch != 0 and
        input.feedback_epoch != 0 and
        input.command_monotonic_milliseconds != 0 and
        input.command_utc_milliseconds >= 0 and
        input.valid_mask == FeedbackValid.required and
        input.feedback_count > 0 and
        input.feedback_count <= config.maximum_persistence_operation_count and
        input.flags == 0 and
        allZero(input.reserved[0..]);
}

pub fn validPersistenceRow(row: *const PersistenceOperation) bool {
    if (row.struct_size != @sizeOf(PersistenceOperation) or
        (row.flags & ~PersistenceFlags.known) != 0 or
        row.mutation_version == 0 or row.identity_handle == 0) return false;
    if (!validPersistenceKind(row.operation_kind)) return false;
    if (!std.math.isFinite(row.current_value) or
        !std.math.isFinite(row.secondary_current_value) or
        !std.math.isFinite(row.value_sum) or
        !std.math.isFinite(row.peak_value) or
        !std.math.isFinite(row.window_delta_value)) return false;
    return true;
}

fn validIndexCapacity(actual: u32, minimum: u32) bool {
    const required = std.math.mul(u32, minimum, 2) catch return false;
    return actual >= required and std.math.isPowerOfTwo(actual);
}

pub fn validSourceStatus(value: u32) bool {
    return value >= @intFromEnum(SourceStatus.complete) and
        value <= @intFromEnum(SourceStatus.skipped);
}

pub fn validMetricSelector(value: u32) bool {
    return value >= @intFromEnum(MetricSelector.current) and
        value <= @intFromEnum(MetricSelector.sample_hits_7d);
}

pub fn validComparison(value: u32) bool {
    return value >= @intFromEnum(Comparison.greater_or_equal) and
        value <= @intFromEnum(Comparison.left_minus_right_less_or_equal);
}

pub fn validTrustCommandKind(value: u32) bool {
    return value == @intFromEnum(TrustCommandKind.add) or
        value == @intFromEnum(TrustCommandKind.remove);
}

pub fn validPersistenceKind(value: u32) bool {
    return value >= @intFromEnum(PersistenceKind.source) and
        value <= @intFromEnum(PersistenceKind.metadata);
}

pub fn validFeedbackStatus(value: u32) bool {
    return value == @intFromEnum(FeedbackStatus.persisted) or
        value == @intFromEnum(FeedbackStatus.failed);
}

fn allZero(values: []const u64) bool {
    for (values) |value| if (value != 0) return false;
    return true;
}

fn validU32Scalar(value: f64) bool {
    return std.math.isFinite(value) and value >= 0 and
        value <= @as(f64, @floatFromInt(std.math.maxInt(u32))) and
        @floor(value) == value;
}

comptime {
    if (@sizeOf(Config) != 216) @compileError("report Config ABI drift");
    if (@sizeOf(Capacity) != 96) @compileError("report Capacity ABI drift");
    if (@sizeOf(RuleInput) != 160) @compileError("report RuleInput ABI drift");
    if (@sizeOf(RuleReplaceInput) != 72) @compileError("report RuleReplaceInput ABI drift");
    if (@sizeOf(FactInput) != 128) @compileError("report FactInput ABI drift");
    if (@sizeOf(SourceSnapshotInput) != 112) @compileError("report SourceSnapshotInput ABI drift");
    if (@sizeOf(TrustCommandInput) != 88) @compileError("report TrustCommandInput ABI drift");
    if (@sizeOf(ImportInput) != 72) @compileError("report ImportInput ABI drift");
    if (@sizeOf(PlanInput) != 80) @compileError("report PlanInput ABI drift");
    if (@sizeOf(ReportOutput) != 192) @compileError("report ReportOutput ABI drift");
    if (@sizeOf(PersistenceOperation) != 352) @compileError("report PersistenceOperation ABI drift");
    if (@sizeOf(PersistenceFeedbackInput) != 72) @compileError("report PersistenceFeedbackInput ABI drift");
    if (@sizeOf(PersistenceFeedback) != 80) @compileError("report PersistenceFeedback ABI drift");
    if (@sizeOf(PlanOutput) != 176) @compileError("report PlanOutput ABI drift");
}
