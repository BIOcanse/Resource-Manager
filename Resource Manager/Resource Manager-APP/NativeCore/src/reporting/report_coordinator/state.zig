const std = @import("std");
const protocol = @import("protocol.zig");
const ResultCode = @import("../../common/result_codes.zig").ResultCode;

pub const no_index: u32 = std.math.maxInt(u32);

pub const SourceSlot = struct {
    occupied: bool = false,
    status: protocol.SourceStatus = .skipped,
    slot_generation: u64 = 0,
    record_handle: u64 = 0,
    source_handle: u64 = 0,
    source_generation: u64 = 0,
    coverage_scope_handle: u64 = 0,
    last_snapshot_epoch: u64 = 0,
    last_observed_at_utc_ms: i64 = 0,
    last_complete_at_utc_ms: i64 = 0,
    mutation_version: u64 = 0,
    persisted_mutation_version: u64 = 0,
};

pub const RuleSlot = struct {
    occupied: bool = false,
    value: protocol.RuleInput = std.mem.zeroes(protocol.RuleInput),
};

pub const ObservationSlot = struct {
    occupied: bool = false,
    rolling: bool = false,
    slot_generation: u64 = 0,
    active: bool = false,
    current_valid: bool = false,
    secondary_current_valid: bool = false,
    delete_pending: bool = false,
    observation_handle: u64 = 0,
    source_handle: u64 = 0,
    coverage_scope_handle: u64 = 0,
    rule_handle: u64 = 0,
    rule_generation: u64 = 0,
    target_handle: u64 = 0,
    evidence_payload_handle: u64 = 0,
    first_observed_at_utc_ms: i64 = 0,
    last_observed_at_utc_ms: i64 = 0,
    current_value: f64 = 0,
    secondary_current_value: f64 = 0,
    value_sum: f64 = 0,
    peak_value: f64 = 0,
    sample_duration_milliseconds: u64 = 0,
    sample_count: u64 = 0,
    active_sample_count: u64 = 0,
    consecutive_hits: u32 = 0,
    consecutive_misses: u32 = 0,
    seen_operation_epoch: u64 = 0,
    last_fact_sequence: u64 = 0,
    window_revision: u64 = 0,
    evaluated_window_revision: u64 = 0,
    mutation_version: u64 = 0,
    persisted_mutation_version: u64 = 0,
};

pub const BucketSlot = struct {
    occupied: bool = false,
    slot_generation: u64 = 0,
    delete_pending: bool = false,
    bucket_handle: u64 = 0,
    source_handle: u64 = 0,
    rule_handle: u64 = 0,
    rule_generation: u64 = 0,
    target_handle: u64 = 0,
    bucket_start_utc_ms: i64 = 0,
    delta_value: f64 = 0,
    peak_value: f64 = 0,
    sample_duration_milliseconds: u64 = 0,
    event_count: u64 = 0,
    request_event_count: u64 = 0,
    connection_event_count: u64 = 0,
    sample_hit_count: u64 = 0,
    mutation_version: u64 = 0,
    persisted_mutation_version: u64 = 0,
};

pub const ReportSlot = struct {
    occupied: bool = false,
    slot_generation: u64 = 0,
    active: bool = false,
    trusted_suppressed: bool = false,
    delete_pending: bool = false,
    report_handle: u64 = 0,
    target_handle: u64 = 0,
    family_handle: u64 = 0,
    observation_index: u32 = no_index,
    rule_handle: u64 = 0,
    report_type_handle: u64 = 0,
    resource_kind_handle: u64 = 0,
    payload_handle: u64 = 0,
    evidence_payload_handle: u64 = 0,
    created_at_utc_ms: i64 = 0,
    updated_at_utc_ms: i64 = 0,
    last_observed_at_utc_ms: i64 = 0,
    priority: u32 = 0,
    severity: u32 = 0,
    mutation_version: u64 = 0,
    persisted_mutation_version: u64 = 0,
};

pub const TrustSlot = struct {
    occupied: bool = false,
    slot_generation: u64 = 0,
    committed: bool = false,
    delete_pending: bool = false,
    trust_handle: u64 = 0,
    target_handle: u64 = 0,
    family_handle: u64 = 0,
    payload_handle: u64 = 0,
    trusted_at_utc_ms: i64 = 0,
    mutation_version: u64 = 0,
    persisted_mutation_version: u64 = 0,
};

pub const DirtyKind = enum(u32) {
    none = 0,
    source = @intFromEnum(protocol.PersistenceKind.source),
    observation = @intFromEnum(protocol.PersistenceKind.observation),
    bucket = @intFromEnum(protocol.PersistenceKind.bucket),
    report = @intFromEnum(protocol.PersistenceKind.report),
    trust = @intFromEnum(protocol.PersistenceKind.trust),
    metadata = @intFromEnum(protocol.PersistenceKind.metadata),
};

pub const DirtyReference = struct {
    mutation_version: u64 = 0,
    index: u32 = 0,
    kind: DirtyKind = .observation,
};

pub const PlannedPersistenceReference = struct {
    plan_epoch: u64 = 0,
    mutation_version: u64 = 0,
    identity_handle: u64 = 0,
    slot_generation: u64 = 0,
    kind: u32 = 0,
    slot_index: u32 = 0,
};

pub const FactKey = struct {
    rule_handle: u64 = 0,
    target_handle: u64 = 0,
};

pub const RollingValues = struct {
    sum_24h: f64 = 0,
    sum_7d: f64 = 0,
    events_24h: u64 = 0,
    events_7d: u64 = 0,
    request_events_24h: u64 = 0,
    request_events_7d: u64 = 0,
    connection_events_24h: u64 = 0,
    connection_events_7d: u64 = 0,
    sample_hits_24h: u64 = 0,
    sample_hits_7d: u64 = 0,
};

pub const Session = struct {
    allocator: std.mem.Allocator,
    storage: []u128,
    mutex: std.atomic.Mutex = .unlocked,
    config: protocol.Config,
    sources: []SourceSlot,
    staging_sources: []SourceSlot,
    rules: []RuleSlot,
    staging_rules: []RuleSlot,
    observations: []ObservationSlot,
    staging_observations: []ObservationSlot,
    reports: []ReportSlot,
    staging_reports: []ReportSlot,
    trusts: []TrustSlot,
    staging_trusts: []TrustSlot,
    buckets: []BucketSlot,
    staging_buckets: []BucketSlot,
    source_index: []u32,
    staging_source_index: []u32,
    rule_index: []u32,
    staging_rule_index: []u32,
    observation_index: []u32,
    staging_observation_index: []u32,
    report_index: []u32,
    staging_report_index: []u32,
    trust_index: []u32,
    staging_trust_index: []u32,
    bucket_index: []u32,
    staging_bucket_index: []u32,
    report_candidates: []u32,
    report_order: []u32,
    dirty_references: []DirtyReference,
    planned_persistence: []PlannedPersistenceReference,
    planned_persistence_index: []u32,
    planned_feedback_marks: []u32,
    trust_feedback_outcomes: []u8,
    fact_keys: []FactKey,
    rolling_values: []RollingValues,
    staging_rolling_values: []RollingValues,
    observation_transaction_marks: []u32,
    report_transaction_marks: []u32,
    bucket_transaction_marks: []u32,
    source_transaction_marks: []u32,
    resident_byte_count: u64,
    last_operation_epoch: u64 = 0,
    last_command_monotonic_milliseconds: u64 = 0,
    last_command_utc_ms: i64 = 0,
    logical_utc_ms: i64 = 0,
    clock_rollback_observed: bool = false,
    last_plan_epoch: u64 = 0,
    planned_persistence_count: u32 = 0,
    planned_output: protocol.PlanOutput = std.mem.zeroes(protocol.PlanOutput),
    last_feedback_epoch: u64 = 0,
    import_generation: u64 = 0,
    next_report_handle: u64 = 1,
    next_trust_handle: u64 = 1,
    next_source_record_handle: u64 = 1,
    next_observation_handle: u64 = 1,
    next_bucket_handle: u64 = 1,
    next_mutation_version: u64 = 1,
    metadata_mutation_version: u64 = 0,
    metadata_persisted_mutation_version: u64 = 0,
    metadata_logical_utc_ms: i64 = 0,
    next_source_slot: u32 = 0,
    next_observation_slot: u32 = 0,
    next_report_slot: u32 = 0,
    next_trust_slot: u32 = 0,
    next_bucket_slot: u32 = 0,
    state_revision: u64 = 1,
    rules_ready: bool = false,
    import_ready: bool = false,
    rolling_observation_count: u32 = 0,
    transaction_epoch: u32 = 0,
    transaction_active: bool = false,
    planned_feedback_epoch: u32 = 0,

    pub fn create(config: *const protocol.Config) !*Session {
        if (!protocol.validConfig(config)) return error.InvalidConfiguration;
        const allocator = std.heap.page_allocator;
        const resident = requiredResidentBytes(config) catch return error.InvalidConfiguration;
        if (resident > config.resident_byte_budget) return error.InvalidConfiguration;

        const self = try allocator.create(Session);
        errdefer allocator.destroy(self);

        const storage_word_count = try requiredStorageWordCount(config);
        const storage = try allocator.alloc(u128, @intCast(storage_word_count));
        errdefer allocator.free(storage);
        var fixed = std.heap.FixedBufferAllocator.init(std.mem.sliceAsBytes(storage));
        const storage_allocator = fixed.allocator();

        const sources = try allocZeroed(storage_allocator, SourceSlot, config.maximum_source_count);
        const staging_sources = try allocZeroed(
            storage_allocator,
            SourceSlot,
            config.maximum_source_count,
        );
        const rules = try allocZeroed(storage_allocator, RuleSlot, config.maximum_rule_count);
        const staging_rules = try allocZeroed(
            storage_allocator,
            RuleSlot,
            config.maximum_rule_count,
        );
        const observations = try allocZeroed(
            storage_allocator,
            ObservationSlot,
            config.maximum_observation_count,
        );
        const staging_observations = try allocZeroed(
            storage_allocator,
            ObservationSlot,
            config.maximum_observation_count,
        );
        const reports = try allocZeroed(
            storage_allocator,
            ReportSlot,
            config.maximum_report_count,
        );
        const staging_reports = try allocZeroed(
            storage_allocator,
            ReportSlot,
            config.maximum_report_count,
        );
        const trusts = try allocZeroed(
            storage_allocator,
            TrustSlot,
            config.maximum_trust_count,
        );
        const staging_trusts = try allocZeroed(
            storage_allocator,
            TrustSlot,
            config.maximum_trust_count,
        );
        const buckets = try allocZeroed(
            storage_allocator,
            BucketSlot,
            config.maximum_bucket_count,
        );
        const staging_buckets = try allocZeroed(
            storage_allocator,
            BucketSlot,
            config.maximum_bucket_count,
        );

        const source_index = try allocIndex(storage_allocator, config.source_index_capacity);
        const staging_source_index = try allocIndex(
            storage_allocator,
            config.source_index_capacity,
        );
        const rule_index = try allocIndex(storage_allocator, config.rule_index_capacity);
        const staging_rule_index = try allocIndex(
            storage_allocator,
            config.rule_index_capacity,
        );
        const observation_index = try allocIndex(
            storage_allocator,
            config.observation_index_capacity,
        );
        const staging_observation_index = try allocIndex(
            storage_allocator,
            config.observation_index_capacity,
        );
        const report_index = try allocIndex(storage_allocator, config.report_index_capacity);
        const staging_report_index = try allocIndex(
            storage_allocator,
            config.report_index_capacity,
        );
        const trust_index = try allocIndex(storage_allocator, config.trust_index_capacity);
        const staging_trust_index = try allocIndex(
            storage_allocator,
            config.trust_index_capacity,
        );
        const bucket_index = try allocIndex(storage_allocator, config.bucket_index_capacity);
        const staging_bucket_index = try allocIndex(
            storage_allocator,
            config.bucket_index_capacity,
        );

        const report_candidates = try allocFilled(
            storage_allocator,
            u32,
            config.maximum_report_count,
            no_index,
        );
        const report_order = try allocZeroed(
            storage_allocator,
            u32,
            config.maximum_report_count,
        );
        const dirty_capacity = try totalPersistentCapacity(config);
        const dirty_references = try allocZeroed(
            storage_allocator,
            DirtyReference,
            dirty_capacity,
        );
        const planned_persistence = try allocZeroed(
            storage_allocator,
            PlannedPersistenceReference,
            config.maximum_persistence_operation_count,
        );
        const planned_persistence_index = try allocIndex(
            storage_allocator,
            config.planned_persistence_index_capacity,
        );
        const planned_feedback_marks = try allocZeroed(
            storage_allocator,
            u32,
            config.maximum_persistence_operation_count,
        );
        const trust_feedback_outcomes = try allocZeroed(
            storage_allocator,
            u8,
            config.maximum_trust_count,
        );
        const fact_keys = try allocZeroed(
            storage_allocator,
            FactKey,
            config.maximum_observation_count,
        );
        const rolling_values = try allocZeroed(
            storage_allocator,
            RollingValues,
            config.maximum_observation_count,
        );
        const staging_rolling_values = try allocZeroed(
            storage_allocator,
            RollingValues,
            config.maximum_observation_count,
        );
        const observation_transaction_marks = try allocZeroed(
            storage_allocator,
            u32,
            config.maximum_observation_count,
        );
        const report_transaction_marks = try allocZeroed(
            storage_allocator,
            u32,
            config.maximum_report_count,
        );
        const bucket_transaction_marks = try allocZeroed(
            storage_allocator,
            u32,
            config.maximum_bucket_count,
        );
        const source_transaction_marks = try allocZeroed(
            storage_allocator,
            u32,
            config.maximum_source_count,
        );

        self.* = .{
            .allocator = allocator,
            .storage = storage,
            .config = config.*,
            .sources = sources,
            .staging_sources = staging_sources,
            .rules = rules,
            .staging_rules = staging_rules,
            .observations = observations,
            .staging_observations = staging_observations,
            .reports = reports,
            .staging_reports = staging_reports,
            .trusts = trusts,
            .staging_trusts = staging_trusts,
            .buckets = buckets,
            .staging_buckets = staging_buckets,
            .source_index = source_index,
            .staging_source_index = staging_source_index,
            .rule_index = rule_index,
            .staging_rule_index = staging_rule_index,
            .observation_index = observation_index,
            .staging_observation_index = staging_observation_index,
            .report_index = report_index,
            .staging_report_index = staging_report_index,
            .trust_index = trust_index,
            .staging_trust_index = staging_trust_index,
            .bucket_index = bucket_index,
            .staging_bucket_index = staging_bucket_index,
            .report_candidates = report_candidates,
            .report_order = report_order,
            .dirty_references = dirty_references,
            .planned_persistence = planned_persistence,
            .planned_persistence_index = planned_persistence_index,
            .planned_feedback_marks = planned_feedback_marks,
            .trust_feedback_outcomes = trust_feedback_outcomes,
            .fact_keys = fact_keys,
            .rolling_values = rolling_values,
            .staging_rolling_values = staging_rolling_values,
            .observation_transaction_marks = observation_transaction_marks,
            .report_transaction_marks = report_transaction_marks,
            .bucket_transaction_marks = bucket_transaction_marks,
            .source_transaction_marks = source_transaction_marks,
            .resident_byte_count = resident,
        };
        return self;
    }

    pub fn destroy(self: *Session) void {
        const allocator = self.allocator;
        allocator.free(self.storage);
        allocator.destroy(self);
    }

    pub fn reconfigure(self: *Session, config: *const protocol.Config) ResultCode {
        lockMutex(&self.mutex);
        defer self.mutex.unlock();
        if (!protocol.validConfig(config)) return .abi_mismatch;
        if (self.planned_persistence_count != 0) return .unavailable;
        if (config.generation <= self.config.generation) return .stale_frame;
        if (!sameShape(config, &self.config)) return .invalid_argument;
        if (self.resident_byte_count > config.resident_byte_budget) return .invalid_argument;
        self.config = config.*;
        self.advanceRevision();
        return .ok;
    }

    pub fn capacity(self: *Session) protocol.Capacity {
        lockMutex(&self.mutex);
        defer self.mutex.unlock();
        return .{
            .struct_size = @sizeOf(protocol.Capacity),
            .source_capacity = self.config.maximum_source_count,
            .rule_capacity = self.config.maximum_rule_count,
            .observation_capacity = self.config.maximum_observation_count,
            .report_capacity = self.config.maximum_report_count,
            .trust_capacity = self.config.maximum_trust_count,
            .bucket_capacity = self.config.maximum_bucket_count,
            .persistence_operation_capacity = self.config.maximum_persistence_operation_count,
            .report_output_capacity = self.config.maximum_report_output_count,
            .source_index_capacity = self.config.source_index_capacity,
            .rule_index_capacity = self.config.rule_index_capacity,
            .observation_index_capacity = self.config.observation_index_capacity,
            .report_index_capacity = self.config.report_index_capacity,
            .trust_index_capacity = self.config.trust_index_capacity,
            .bucket_index_capacity = self.config.bucket_index_capacity,
            .rolling_observation_capacity = self.config.maximum_rolling_observation_count,
            .resident_byte_count = self.resident_byte_count,
            .planned_persistence_index_capacity = self.config.planned_persistence_index_capacity,
            .reserved_u32 = 0,
            .reserved = .{ 0, 0 },
        };
    }

    pub fn operationAdvances(self: *const Session, epoch: u64, command_monotonic: u64) bool {
        return epoch > self.last_operation_epoch and
            command_monotonic >= self.last_command_monotonic_milliseconds;
    }

    pub fn commitOperation(
        self: *Session,
        epoch: u64,
        command_monotonic: u64,
        command_utc: i64,
    ) void {
        self.last_operation_epoch = epoch;
        self.last_command_monotonic_milliseconds = command_monotonic;
        self.last_command_utc_ms = command_utc;
        if (command_utc < self.logical_utc_ms) {
            self.clock_rollback_observed = true;
        } else {
            self.logical_utc_ms = command_utc;
        }
        self.advanceRevision();
    }

    pub fn observedTimeValid(self: *const Session, observed_at: i64, command_at: i64) bool {
        if (observed_at < 0 or command_at < 0) return false;
        const reference = @max(command_at, self.logical_utc_ms);
        const limit = saturatingAddI64(reference, self.config.maximum_future_skew_milliseconds);
        return observed_at <= limit;
    }

    pub fn allocateMutation(self: *Session) ?u64 {
        const value = self.next_mutation_version;
        if (value == 0 or value == std.math.maxInt(u64)) return null;
        self.next_mutation_version = value + 1;
        return value;
    }

    pub fn allocateReportHandle(self: *Session) ?u64 {
        const value = self.next_report_handle;
        if (value == 0 or value == std.math.maxInt(u64)) return null;
        self.next_report_handle = value + 1;
        return value;
    }

    pub fn allocateObservationHandle(self: *Session) ?u64 {
        const value = self.next_observation_handle;
        if (value == 0 or value == std.math.maxInt(u64)) return null;
        self.next_observation_handle = value + 1;
        return value;
    }

    pub fn allocateBucketHandle(self: *Session) ?u64 {
        const value = self.next_bucket_handle;
        if (value == 0 or value == std.math.maxInt(u64)) return null;
        self.next_bucket_handle = value + 1;
        return value;
    }

    pub fn allocateTrustHandle(self: *Session) ?u64 {
        const value = self.next_trust_handle;
        if (value == 0 or value == std.math.maxInt(u64)) return null;
        self.next_trust_handle = value + 1;
        return value;
    }

    pub fn allocateSourceRecordHandle(self: *Session) ?u64 {
        const value = self.next_source_record_handle;
        if (value == 0 or value == std.math.maxInt(u64)) return null;
        self.next_source_record_handle = value + 1;
        return value;
    }

    pub fn advanceRevision(self: *Session) void {
        if (self.state_revision != std.math.maxInt(u64)) {
            self.state_revision += 1;
        }
    }

    pub fn findSource(self: *const Session, handle: u64, coverage_scope: u64) ?u32 {
        return findPair(
            self.source_index,
            self.sources,
            handle,
            coverage_scope,
            SourceSlot,
            sourceKeys,
        );
    }

    pub fn findRule(self: *const Session, handle: u64) ?u32 {
        return findSingle(self.rule_index, self.rules, handle, RuleSlot, ruleKey);
    }

    pub fn findObservation(
        self: *const Session,
        source: u64,
        coverage_scope: u64,
        rule: u64,
        target: u64,
    ) ?u32 {
        return findQuad(
            self.observation_index,
            self.observations,
            source,
            coverage_scope,
            rule,
            target,
            ObservationSlot,
            observationKeys,
        );
    }

    pub fn findReport(self: *const Session, target: u64, family: u64) ?u32 {
        return findPair(self.report_index, self.reports, target, family, ReportSlot, reportKeys);
    }

    pub fn findTrust(self: *const Session, target: u64, family: u64) ?u32 {
        return findPair(
            self.trust_index,
            self.trusts,
            target,
            family,
            TrustSlot,
            trustKeys,
        );
    }

    pub fn findBucket(self: *const Session, source: u64, rule: u64, target: u64, start: i64) ?u32 {
        return findBucketIndex(self.bucket_index, self.buckets, source, rule, target, start);
    }

    pub fn insertSourceIndex(self: *Session, slot_index: u32) bool {
        return insertPair(self.source_index, self.sources, slot_index, SourceSlot, sourceKeys);
    }

    pub fn insertPlannedPersistenceIndex(self: *Session, planned_index: u32) bool {
        return insertPlanned(
            self.planned_persistence_index,
            self.planned_persistence,
            planned_index,
        );
    }

    pub fn findPlannedPersistence(
        self: *const Session,
        reference: PlannedPersistenceReference,
    ) ?u32 {
        return findPlanned(
            self.planned_persistence_index,
            self.planned_persistence,
            reference,
        );
    }

    pub fn insertObservationIndex(self: *Session, slot_index: u32) bool {
        return insertQuad(
            self.observation_index,
            self.observations,
            slot_index,
            ObservationSlot,
            observationKeys,
        );
    }

    pub fn insertReportIndex(self: *Session, slot_index: u32) bool {
        return insertPair(self.report_index, self.reports, slot_index, ReportSlot, reportKeys);
    }

    pub fn insertTrustIndex(self: *Session, slot_index: u32) bool {
        return insertPair(
            self.trust_index,
            self.trusts,
            slot_index,
            TrustSlot,
            trustKeys,
        );
    }

    pub fn insertBucketIndex(self: *Session, slot_index: u32) bool {
        return insertBucket(self.bucket_index, self.buckets, slot_index);
    }

    pub fn beginFactTransaction(self: *Session) void {
        if (self.transaction_epoch == std.math.maxInt(u32)) {
            @memset(self.source_transaction_marks, 0);
            @memset(self.observation_transaction_marks, 0);
            @memset(self.report_transaction_marks, 0);
            @memset(self.bucket_transaction_marks, 0);
            self.transaction_epoch = 1;
        } else {
            self.transaction_epoch += 1;
            if (self.transaction_epoch == 0) self.transaction_epoch = 1;
        }
        self.transaction_active = true;
    }

    pub fn snapshotSource(self: *Session, slot_index: u32) void {
        if (!self.transaction_active or
            self.source_transaction_marks[slot_index] == self.transaction_epoch) return;
        self.staging_sources[slot_index] = self.sources[slot_index];
        self.source_transaction_marks[slot_index] = self.transaction_epoch;
    }

    pub fn snapshotObservation(self: *Session, slot_index: u32) void {
        if (!self.transaction_active or
            self.observation_transaction_marks[slot_index] == self.transaction_epoch) return;
        self.staging_observations[slot_index] = self.observations[slot_index];
        self.observation_transaction_marks[slot_index] = self.transaction_epoch;
    }

    pub fn snapshotReport(self: *Session, slot_index: u32) void {
        if (!self.transaction_active or
            self.report_transaction_marks[slot_index] == self.transaction_epoch) return;
        self.staging_reports[slot_index] = self.reports[slot_index];
        self.report_transaction_marks[slot_index] = self.transaction_epoch;
    }

    pub fn snapshotBucket(self: *Session, slot_index: u32) void {
        if (!self.transaction_active or
            self.bucket_transaction_marks[slot_index] == self.transaction_epoch) return;
        self.staging_buckets[slot_index] = self.buckets[slot_index];
        self.bucket_transaction_marks[slot_index] = self.transaction_epoch;
    }

    pub fn commitFactTransaction(self: *Session) void {
        self.transaction_active = false;
    }

    pub fn rollbackFactTransaction(self: *Session) void {
        if (!self.transaction_active) return;
        for (self.source_transaction_marks, 0..) |mark, index| {
            if (mark == self.transaction_epoch) {
                self.sources[index] = self.staging_sources[index];
            }
        }
        for (self.observation_transaction_marks, 0..) |mark, index| {
            if (mark == self.transaction_epoch) {
                self.observations[index] = self.staging_observations[index];
            }
        }
        for (self.report_transaction_marks, 0..) |mark, index| {
            if (mark == self.transaction_epoch) {
                self.reports[index] = self.staging_reports[index];
            }
        }
        for (self.bucket_transaction_marks, 0..) |mark, index| {
            if (mark == self.transaction_epoch) {
                self.buckets[index] = self.staging_buckets[index];
            }
        }
        if (!rebuildSourceIndex(self.sources, self.source_index) or
            !rebuildObservationIndex(self.observations, self.observation_index) or
            !rebuildReportIndex(self.reports, self.report_index) or
            !rebuildBucketIndex(self.buckets, self.bucket_index)) unreachable;
        self.transaction_active = false;
    }

    pub fn beginFeedbackValidation(self: *Session) void {
        if (self.planned_feedback_epoch == std.math.maxInt(u32)) {
            @memset(self.planned_feedback_marks, 0);
            self.planned_feedback_epoch = 1;
        } else {
            self.planned_feedback_epoch += 1;
            if (self.planned_feedback_epoch == 0) self.planned_feedback_epoch = 1;
        }
        @memset(self.trust_feedback_outcomes, 0);
    }
};

pub fn lockMutex(mutex: *std.atomic.Mutex) void {
    while (!mutex.tryLock()) std.atomic.spinLoopHint();
}

pub fn firstFreeFrom(comptime T: type, slots: []T, cursor: u32) ?u32 {
    if (slots.len == 0) return null;
    const start: usize = @intCast(cursor % @as(u32, @intCast(slots.len)));
    for (0..slots.len) |offset| {
        const index = (start + offset) % slots.len;
        if (slots[index].occupied) continue;
        return @intCast(index);
    }
    return null;
}

pub fn advanceFreeCursor(cursor: *u32, slot_count: usize, claimed_index: u32) void {
    cursor.* = @intCast((@as(usize, claimed_index) + 1) % slot_count);
}

pub fn nextSlotGeneration(current: u64) ?u64 {
    if (current == std.math.maxInt(u64)) return null;
    const next = current + 1;
    return if (next == 0) null else next;
}

pub fn rebuildRuleIndex(slots: []const RuleSlot, index: []u32) bool {
    @memset(index, no_index);
    for (slots, 0..) |slot, slot_index| {
        if (!slot.occupied) continue;
        if (!insertSingle(index, slots, @intCast(slot_index), RuleSlot, ruleKey)) return false;
    }
    return true;
}

pub fn rebuildSourceIndex(slots: []const SourceSlot, index: []u32) bool {
    @memset(index, no_index);
    for (slots, 0..) |slot, slot_index| {
        if (!slot.occupied) continue;
        if (!insertPair(index, slots, @intCast(slot_index), SourceSlot, sourceKeys)) return false;
    }
    return true;
}

pub fn findRuleIn(slots: []const RuleSlot, index: []const u32, handle: u64) ?u32 {
    return findSingle(index, slots, handle, RuleSlot, ruleKey);
}

pub fn findSourceIn(
    slots: []const SourceSlot,
    index: []const u32,
    source: u64,
    coverage_scope: u64,
) ?u32 {
    return findPair(index, slots, source, coverage_scope, SourceSlot, sourceKeys);
}

pub fn rebuildObservationIndex(slots: []const ObservationSlot, index: []u32) bool {
    @memset(index, no_index);
    for (slots, 0..) |slot, slot_index| {
        if (!slot.occupied) continue;
        if (!insertQuad(
            index,
            slots,
            @intCast(slot_index),
            ObservationSlot,
            observationKeys,
        )) return false;
    }
    return true;
}

pub fn findObservationIn(
    slots: []const ObservationSlot,
    index: []const u32,
    source: u64,
    coverage_scope: u64,
    rule: u64,
    target: u64,
) ?u32 {
    return findQuad(
        index,
        slots,
        source,
        coverage_scope,
        rule,
        target,
        ObservationSlot,
        observationKeys,
    );
}

pub fn rebuildReportIndex(slots: []const ReportSlot, index: []u32) bool {
    @memset(index, no_index);
    for (slots, 0..) |slot, slot_index| {
        if (!slot.occupied) continue;
        if (!insertPair(index, slots, @intCast(slot_index), ReportSlot, reportKeys)) return false;
    }
    return true;
}

pub fn rebuildTrustIndex(slots: []const TrustSlot, index: []u32) bool {
    @memset(index, no_index);
    for (slots, 0..) |slot, slot_index| {
        if (!slot.occupied) continue;
        if (!insertPair(index, slots, @intCast(slot_index), TrustSlot, trustKeys)) return false;
    }
    return true;
}

pub fn rebuildBucketIndex(slots: []const BucketSlot, index: []u32) bool {
    @memset(index, no_index);
    for (slots, 0..) |slot, slot_index| {
        if (!slot.occupied) continue;
        if (!insertBucket(index, slots, @intCast(slot_index))) return false;
    }
    return true;
}

pub fn requiredResidentBytes(config: *const protocol.Config) !u64 {
    const words = try requiredStorageWordCount(config);
    const storage_bytes = try std.math.mul(u64, words, @sizeOf(u128));
    return std.math.add(u64, @sizeOf(Session), storage_bytes);
}

fn requiredStorageWordCount(config: *const protocol.Config) !u64 {
    var total: u64 = 0;
    total = try addArrayBytes(total, SourceSlot, try doubled(config.maximum_source_count));
    total = try addArrayBytes(total, RuleSlot, try doubled(config.maximum_rule_count));
    total = try addArrayBytes(
        total,
        ObservationSlot,
        try doubled(config.maximum_observation_count),
    );
    total = try addArrayBytes(total, ReportSlot, try doubled(config.maximum_report_count));
    total = try addArrayBytes(total, TrustSlot, try doubled(config.maximum_trust_count));
    total = try addArrayBytes(total, BucketSlot, try doubled(config.maximum_bucket_count));
    total = try addArrayBytes(total, u32, try doubled(config.source_index_capacity));
    total = try addArrayBytes(total, u32, try doubled(config.rule_index_capacity));
    total = try addArrayBytes(total, u32, try doubled(config.observation_index_capacity));
    total = try addArrayBytes(total, u32, try doubled(config.report_index_capacity));
    total = try addArrayBytes(total, u32, try doubled(config.trust_index_capacity));
    total = try addArrayBytes(total, u32, try doubled(config.bucket_index_capacity));
    total = try addArrayBytes(total, u32, try doubled(config.maximum_report_count));
    total = try addArrayBytes(total, DirtyReference, try totalPersistentCapacity(config));
    total = try addArrayBytes(
        total,
        PlannedPersistenceReference,
        config.maximum_persistence_operation_count,
    );
    total = try addArrayBytes(
        total,
        u32,
        config.planned_persistence_index_capacity,
    );
    total = try addArrayBytes(
        total,
        u32,
        config.maximum_persistence_operation_count,
    );
    total = try addArrayBytes(total, u8, config.maximum_trust_count);
    total = try addArrayBytes(total, FactKey, config.maximum_observation_count);
    total = try addArrayBytes(
        total,
        RollingValues,
        try doubled(config.maximum_observation_count),
    );
    total = try addArrayBytes(total, u32, config.maximum_observation_count);
    total = try addArrayBytes(total, u32, config.maximum_report_count);
    total = try addArrayBytes(total, u32, config.maximum_bucket_count);
    total = try addArrayBytes(total, u32, config.maximum_source_count);
    const padded = try std.math.add(u64, total, @sizeOf(u128) - 1);
    return padded / @sizeOf(u128);
}

fn totalPersistentCapacity(config: *const protocol.Config) !u32 {
    var total: u64 = 1;
    total += config.maximum_source_count;
    total += config.maximum_observation_count;
    total += config.maximum_bucket_count;
    total += config.maximum_report_count;
    total += config.maximum_trust_count;
    if (total > std.math.maxInt(u32)) return error.Overflow;
    return @intCast(total);
}

fn sameShape(left: *const protocol.Config, right: *const protocol.Config) bool {
    return left.session_instance_low == right.session_instance_low and
        left.session_instance_high == right.session_instance_high and
        left.maximum_source_count == right.maximum_source_count and
        left.maximum_rule_count == right.maximum_rule_count and
        left.maximum_observation_count == right.maximum_observation_count and
        left.maximum_rolling_observation_count == right.maximum_rolling_observation_count and
        left.maximum_report_count == right.maximum_report_count and
        left.maximum_trust_count == right.maximum_trust_count and
        left.maximum_bucket_count == right.maximum_bucket_count and
        left.maximum_persistence_operation_count == right.maximum_persistence_operation_count and
        left.maximum_report_output_count == right.maximum_report_output_count and
        left.source_index_capacity == right.source_index_capacity and
        left.planned_persistence_index_capacity ==
            right.planned_persistence_index_capacity and
        left.rule_index_capacity == right.rule_index_capacity and
        left.observation_index_capacity == right.observation_index_capacity and
        left.report_index_capacity == right.report_index_capacity and
        left.trust_index_capacity == right.trust_index_capacity and
        left.bucket_index_capacity == right.bucket_index_capacity and
        left.bucket_width_milliseconds == right.bucket_width_milliseconds and
        left.window_24h_milliseconds == right.window_24h_milliseconds and
        left.window_7d_milliseconds == right.window_7d_milliseconds;
}

fn allocZeroed(allocator: std.mem.Allocator, comptime T: type, count: u32) ![]T {
    const values = try allocator.alloc(T, count);
    @memset(values, std.mem.zeroes(T));
    return values;
}

fn allocFilled(
    allocator: std.mem.Allocator,
    comptime T: type,
    count: u32,
    value: T,
) ![]T {
    const values = try allocator.alloc(T, count);
    @memset(values, value);
    return values;
}

fn allocIndex(allocator: std.mem.Allocator, count: u32) ![]u32 {
    return allocFilled(allocator, u32, count, no_index);
}

fn addArrayBytes(current: u64, comptime T: type, count: u32) !u64 {
    if (@alignOf(T) > @alignOf(u128)) return error.Overflow;
    const mask: u64 = @alignOf(T) - 1;
    const padded = try std.math.add(u64, current, mask);
    const aligned = padded & ~mask;
    const bytes = try std.math.mul(u64, @sizeOf(T), count);
    return std.math.add(u64, aligned, bytes);
}

fn doubled(value: u32) !u32 {
    return std.math.mul(u32, value, 2);
}

fn sourceKeys(slot: SourceSlot) [2]u64 {
    return .{ slot.source_handle, slot.coverage_scope_handle };
}

fn ruleKey(slot: RuleSlot) u64 {
    return slot.value.rule_handle;
}

fn observationKeys(slot: ObservationSlot) [4]u64 {
    return .{
        slot.source_handle,
        slot.coverage_scope_handle,
        slot.rule_handle,
        slot.target_handle,
    };
}

fn reportKeys(slot: ReportSlot) [2]u64 {
    return .{ slot.target_handle, slot.family_handle };
}

fn trustKeys(slot: TrustSlot) [2]u64 {
    return .{ slot.target_handle, slot.family_handle };
}

fn bucketHash(source: u64, rule: u64, target: u64, start: i64) u64 {
    return mix(mix(mix(source, rule), target), @bitCast(start));
}

fn findSingle(
    index: []const u32,
    slots: anytype,
    key: u64,
    comptime T: type,
    comptime keyFn: fn (T) u64,
) ?u32 {
    var position: usize = @intCast(hashValue(key) & (index.len - 1));
    for (0..index.len) |_| {
        const slot_index = index[position];
        if (slot_index == no_index) return null;
        const slot = slots[slot_index];
        if (slot.occupied and keyFn(slot) == key) return slot_index;
        position = (position + 1) & (index.len - 1);
    }
    return null;
}

fn insertSingle(
    index: []u32,
    slots: anytype,
    slot_index: u32,
    comptime T: type,
    comptime keyFn: fn (T) u64,
) bool {
    const key = keyFn(slots[slot_index]);
    if (findSingle(index, slots, key, T, keyFn) != null) return false;
    return insertAtHash(index, hashValue(key), slot_index);
}

fn findPair(
    index: []const u32,
    slots: anytype,
    first: u64,
    second: u64,
    comptime T: type,
    comptime keyFn: fn (T) [2]u64,
) ?u32 {
    var position: usize = @intCast(mix(first, second) & (index.len - 1));
    for (0..index.len) |_| {
        const slot_index = index[position];
        if (slot_index == no_index) return null;
        const slot = slots[slot_index];
        const keys = keyFn(slot);
        if (slot.occupied and keys[0] == first and keys[1] == second) return slot_index;
        position = (position + 1) & (index.len - 1);
    }
    return null;
}

fn insertPair(
    index: []u32,
    slots: anytype,
    slot_index: u32,
    comptime T: type,
    comptime keyFn: fn (T) [2]u64,
) bool {
    const keys = keyFn(slots[slot_index]);
    if (findPair(index, slots, keys[0], keys[1], T, keyFn) != null) return false;
    return insertAtHash(index, mix(keys[0], keys[1]), slot_index);
}

fn findQuad(
    index: []const u32,
    slots: anytype,
    first: u64,
    second: u64,
    third: u64,
    fourth: u64,
    comptime T: type,
    comptime keyFn: fn (T) [4]u64,
) ?u32 {
    const hash = mix(mix(mix(first, second), third), fourth);
    var position: usize = @intCast(hash & (index.len - 1));
    for (0..index.len) |_| {
        const slot_index = index[position];
        if (slot_index == no_index) return null;
        const slot = slots[slot_index];
        const keys = keyFn(slot);
        if (slot.occupied and keys[0] == first and keys[1] == second and
            keys[2] == third and keys[3] == fourth) return slot_index;
        position = (position + 1) & (index.len - 1);
    }
    return null;
}

fn insertQuad(
    index: []u32,
    slots: anytype,
    slot_index: u32,
    comptime T: type,
    comptime keyFn: fn (T) [4]u64,
) bool {
    const keys = keyFn(slots[slot_index]);
    if (findQuad(index, slots, keys[0], keys[1], keys[2], keys[3], T, keyFn) != null) {
        return false;
    }
    return insertAtHash(index, mix(mix(mix(keys[0], keys[1]), keys[2]), keys[3]), slot_index);
}

fn findBucketIndex(
    index: []const u32,
    slots: []const BucketSlot,
    source: u64,
    rule: u64,
    target: u64,
    start: i64,
) ?u32 {
    var position: usize = @intCast(bucketHash(source, rule, target, start) & (index.len - 1));
    for (0..index.len) |_| {
        const slot_index = index[position];
        if (slot_index == no_index) return null;
        const slot = slots[slot_index];
        if (slot.occupied and slot.source_handle == source and slot.rule_handle == rule and
            slot.target_handle == target and slot.bucket_start_utc_ms == start) return slot_index;
        position = (position + 1) & (index.len - 1);
    }
    return null;
}

fn insertBucket(index: []u32, slots: []const BucketSlot, slot_index: u32) bool {
    const slot = slots[slot_index];
    if (findBucketIndex(
        index,
        slots,
        slot.source_handle,
        slot.rule_handle,
        slot.target_handle,
        slot.bucket_start_utc_ms,
    ) != null) return false;
    return insertAtHash(
        index,
        bucketHash(slot.source_handle, slot.rule_handle, slot.target_handle, slot.bucket_start_utc_ms),
        slot_index,
    );
}

fn findPlanned(
    index: []const u32,
    planned: []const PlannedPersistenceReference,
    expected: PlannedPersistenceReference,
) ?u32 {
    var position: usize = @intCast(plannedHash(expected) & (index.len - 1));
    for (0..index.len) |_| {
        const planned_index = index[position];
        if (planned_index == no_index) return null;
        if (plannedReferenceEqual(planned[planned_index], expected)) return planned_index;
        position = (position + 1) & (index.len - 1);
    }
    return null;
}

fn insertPlanned(
    index: []u32,
    planned: []const PlannedPersistenceReference,
    planned_index: u32,
) bool {
    const value = planned[planned_index];
    if (findPlanned(index, planned, value) != null) return false;
    return insertAtHash(index, plannedHash(value), planned_index);
}

fn plannedReferenceEqual(
    left: PlannedPersistenceReference,
    right: PlannedPersistenceReference,
) bool {
    return left.plan_epoch == right.plan_epoch and
        left.mutation_version == right.mutation_version and
        left.identity_handle == right.identity_handle and
        left.slot_generation == right.slot_generation and
        left.kind == right.kind and
        left.slot_index == right.slot_index;
}

fn plannedHash(value: PlannedPersistenceReference) u64 {
    return mix(
        mix(
            mix(value.plan_epoch, value.mutation_version),
            mix(value.identity_handle, value.slot_generation),
        ),
        mix(value.kind, value.slot_index),
    );
}

fn insertAtHash(index: []u32, hash: u64, slot_index: u32) bool {
    var position: usize = @intCast(hash & (index.len - 1));
    for (0..index.len) |_| {
        const current = index[position];
        if (current == no_index) {
            index[position] = slot_index;
            return true;
        }
        position = (position + 1) & (index.len - 1);
    }
    return false;
}

fn hashValue(value: u64) u64 {
    var x = value;
    x ^= x >> 30;
    x *%= 0xbf58476d1ce4e5b9;
    x ^= x >> 27;
    x *%= 0x94d049bb133111eb;
    x ^= x >> 31;
    return x;
}

fn mix(left: u64, right: u64) u64 {
    return hashValue(left ^ std.math.rotl(u64, right, 29));
}

fn saturatingAddI64(value: i64, increment: u64) i64 {
    if (increment > std.math.maxInt(i64)) return std.math.maxInt(i64);
    return std.math.add(i64, value, @intCast(increment)) catch std.math.maxInt(i64);
}
