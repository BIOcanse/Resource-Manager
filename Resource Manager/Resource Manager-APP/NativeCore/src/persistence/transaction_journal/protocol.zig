const std = @import("std");
const feedback_contract = @import("../../contracts/action_feedback_contract.zig");

pub const abi_version: u32 = 0x0005_0000;
pub const image_magic: u64 = 0x524d_5458_4a4e_4c31;

pub const Status = enum(i32) {
    ok = 0,
    invalid_argument = 1,
    abi_mismatch = 2,
    capacity_full = 3,
    no_data = 4,
    stale_revision = 5,
    phase_mismatch = 6,
    invalid_transition = 7,
    invalid_identity = 8,
    invalid_payload = 9,
    invalid_time = 10,
    corrupt_image = 11,
    truncated_image = 12,
    duplicate_identity = 13,
    buffer_too_small = 14,
    unknown_phase = 15,
    checksum_mismatch = 16,
    non_canonical_image = 17,
    configuration_mismatch = 18,
    revision_exhausted = 19,
    out_of_memory = 20,
};

pub const Phase = enum(u32) {
    prepared = 1,
    previous_effect_restored = 2,
    effect_observed = 3,
    feedback_pending = 4,
    reconciliation_pending = 5,
    authoritative_resync_pending = 6,
    recovery_retry_pending = 7,
    recovery_blocked = 8,
    effect_invocation_uncertain = 9,
};

pub const MutationEvent = enum(u32) {
    confirm_previous_effect_restored = 1,
    confirm_effect_observed = 2,
};

pub const RecoveryOutcome = enum(u32) {
    restored = 1,
    already_restored = 2,
    ownership_lost = 3,
    retryable_failure = 4,
    invalid_proof = 5,
    unavailable = 6,
    process_exited = 7,
    effect_invocation_uncertain = 8,
    authoritative_resync_completed = 9,
};

pub const AckResult = enum(u32) {
    accepted = 1,
    uncertain = 2,
    stale = 3,
    rejected = 4,
};

pub const FeedbackStatus = enum(u32) {
    succeeded = 1,
    failed_unchanged = 2,
    rejected = 3,
    skipped = 4,
    ownership_lost = 5,
    state_uncertain = 6,
};

pub const FeedbackFlags = struct {
    pub const process_owned: u32 = 1 << 0;
    pub const cpu_owned: u32 = 1 << 1;
    pub const gpu_owned: u32 = 1 << 2;
    pub const rollback_payload_persisted: u32 = 1 << 3;
    pub const memory_owned: u32 = 1 << 4;
    pub const known: u32 = process_owned |
        cpu_owned |
        gpu_owned |
        rollback_payload_persisted |
        memory_owned;
};

pub const FeedbackValid = struct {
    pub const completed_at: u32 = 1 << 0;
    pub const actual_process_grade: u32 = 1 << 1;
    pub const actual_cpu_grade: u32 = 1 << 2;
    pub const actual_gpu_grade: u32 = 1 << 3;
    pub const actual_memory_priority: u32 = 1 << 4;
    pub const required: u32 = completed_at;
    pub const known: u32 = completed_at |
        actual_process_grade |
        actual_cpu_grade |
        actual_gpu_grade |
        actual_memory_priority;
};

pub const RecoveryReason = struct {
    pub const restored: u64 = 1 << 0;
    pub const already_restored: u64 = 1 << 1;
    pub const ownership_lost: u64 = 1 << 2;
    pub const retryable_failure: u64 = 1 << 3;
    pub const invalid_proof: u64 = 1 << 4;
    pub const unavailable: u64 = 1 << 5;
    pub const process_exited: u64 = 1 << 6;
    pub const ack_uncertain: u64 = 1 << 7;
    pub const ack_stale: u64 = 1 << 8;
    pub const ack_rejected: u64 = 1 << 9;
    pub const accepted_state_uncertain: u64 = 1 << 10;
    pub const accepted_ownership_lost: u64 = 1 << 11;
    pub const effect_invocation_uncertain: u64 = 1 << 12;
    pub const retry_budget_exhausted: u64 = 1 << 13;
    pub const known: u64 = (1 << 14) - 1;
};

pub const PayloadKind = enum(u32) {
    none_required = 1,
    durable = 2,
};

pub const Scope = enum(u32) {
    process = 1,
    software = 2,
    resource = 3,
};

pub const Disposition = enum(u32) {
    apply = 1,
    restore = 2,
};

pub const DomainBits = struct {
    pub const process: u32 = 1 << 0;
    pub const cpu: u32 = 1 << 1;
    pub const gpu: u32 = 1 << 2;
    pub const physical_memory: u32 = 1 << 3;
    pub const virtual_memory: u32 = 1 << 4;
    pub const video_memory: u32 = 1 << 5;
    pub const placement: u32 = 1 << 6;
    pub const shared_resource: u32 = 1 << 7;
    pub const software_known: u32 = cpu | gpu;
    pub const resource_known: u32 = physical_memory |
        virtual_memory |
        video_memory |
        placement |
        shared_resource;
    pub const known: u32 = process | software_known | resource_known;
};

pub const GradeValid = struct {
    pub const process: u32 = 1 << 0;
    pub const cpu: u32 = 1 << 1;
    pub const gpu: u32 = 1 << 2;
    pub const memory: u32 = 1 << 3;
    pub const known: u32 = process | cpu | gpu | memory;
};

pub const ProcessGrade = enum(i32) {
    level_4 = -4,
    level_3 = -3,
    level_2 = -2,
    level_1 = -1,
    normal = 0,
    a1 = 1,
};

pub const AdapterGrade = enum(i32) {
    freeze = 0,
    optimize = 1,
    normal = 2,
    extreme = 3,
};

pub const CreateConfig = extern struct {
    abi_version: u32,
    struct_size: u32,
    journal_instance_low: u64,
    journal_instance_high: u64,
    record_capacity: u32,
    flags: u32,
    maximum_resident_bytes: u64,
    reserved: [3]u64,
};

pub const OpenConfig = extern struct {
    abi_version: u32,
    struct_size: u32,
    maximum_record_capacity: u32,
    flags: u32,
    maximum_resident_bytes: u64,
    reserved: [1]u64,
};

pub const Capacity = extern struct {
    struct_size: u32,
    record_capacity: u32,
    maximum_image_length: u64,
    resident_bytes: u64,
    reserved: [1]u64,
};

pub const Identity = extern struct {
    configuration_generation: u64,
    plan_epoch: u64,
    action_id: u64,
    host_session_incarnation: u64,
    target_id: u64,
    software_id: u64,
    process_start_key: u64,
    process_id: u32,
    reserved: u32,
};

pub const PrepareInput = extern struct {
    abi_version: u32,
    struct_size: u32,
    expected_journal_revision: u64,
    identity: Identity,
    scope: u32,
    disposition: u32,
    domain_mask: u32,
    grade_valid_mask: u32,
    process_from_grade: i32,
    process_to_grade: i32,
    cpu_from_grade: i32,
    cpu_to_grade: i32,
    gpu_from_grade: i32,
    gpu_to_grade: i32,
    stable_system_status: u32,
    stable_system_error: u32,
    payload_kind: u32,
    payload_slot: u32,
    payload_generation: u32,
    payload_reserved: u32,
    payload_length: u64,
    payload_digest_low: u64,
    payload_digest_high: u64,
    now_utc_ms: u64,
    maximum_recovery_attempts: u32,
    retry_policy_reserved: u32,
    recovery_deadline_utc_ms: u64,
    atomic_group_id: u64,
    group_member_index: u32,
    group_member_count: u32,
    payload_provenance_digest_low: u64,
    payload_provenance_digest_high: u64,
};

pub const PrepareBatchInput = extern struct {
    abi_version: u32,
    struct_size: u32,
    expected_journal_revision: u64,
    input_count: u32,
    flags: u32,
    reserved: [3]u64,
};

pub const MutationInput = extern struct {
    abi_version: u32,
    struct_size: u32,
    identity: Identity,
    expected_journal_revision: u64,
    expected_entry_revision: u64,
    now_utc_ms: u64,
    event: u32,
    expected_phase: u32,
    stable_system_status: u32,
    stable_system_error: u32,
    reserved: [6]u64,
};

pub const StageFeedbackInput = extern struct {
    abi_version: u32,
    struct_size: u32,
    identity: Identity,
    expected_journal_revision: u64,
    expected_entry_revision: u64,
    completed_at_utc_ms: u64,
    expected_phase: u32,
    feedback_valid_mask: u32,
    feedback_flags: u32,
    feedback_status: u32,
    feedback_system_status: u32,
    feedback_system_error: u32,
    feedback_reserved: u32,
    actual_process_grade: i32,
    actual_cpu_grade: i32,
    actual_gpu_grade: i32,
    actual_reserved: u32,
    reserved: [4]u64,
};

pub const RecoveryEvidenceInput = extern struct {
    abi_version: u32,
    struct_size: u32,
    identity: Identity,
    expected_journal_revision: u64,
    expected_entry_revision: u64,
    observed_at_utc_ms: u64,
    retry_not_before_utc_ms: u64,
    outcome: u32,
    expected_phase: u32,
    stable_system_status: u32,
    stable_system_error: u32,
    flags: u32,
    evidence_reserved: u32,
    authoritative_facts_generation: u64,
    reserved: [3]u64,
};

pub const AckInput = extern struct {
    abi_version: u32,
    struct_size: u32,
    identity: Identity,
    expected_journal_revision: u64,
    expected_entry_revision: u64,
    acknowledged_at_utc_ms: u64,
    retry_not_before_utc_ms: u64,
    result: u32,
    expected_phase: u32,
    stable_system_status: u32,
    stable_system_error: u32,
    flags: u32,
    ack_reserved: u32,
    reserved: [4]u64,
};

pub const Record = extern struct {
    identity: Identity,
    scope: u32,
    disposition: u32,
    domain_mask: u32,
    phase: u32,
    grade_valid_mask: u32,
    grade_reserved: u32,
    stable_system_status: u32,
    stable_system_error: u32,
    process_from_grade: i32,
    process_to_grade: i32,
    cpu_from_grade: i32,
    cpu_to_grade: i32,
    gpu_from_grade: i32,
    gpu_to_grade: i32,
    payload_kind: u32,
    payload_slot: u32,
    payload_generation: u32,
    payload_reserved: u32,
    payload_length: u64,
    payload_digest_low: u64,
    payload_digest_high: u64,
    prepared_at_utc_ms: u64,
    updated_at_utc_ms: u64,
    retry_not_before_utc_ms: u64,
    entry_revision: u64,
    feedback_valid_mask: u32,
    feedback_flags: u32,
    feedback_status: u32,
    feedback_system_status: u32,
    feedback_system_error: u32,
    feedback_reserved: u32,
    feedback_completed_at_utc_ms: u64,
    actual_process_grade: i32,
    actual_cpu_grade: i32,
    actual_gpu_grade: i32,
    actual_reserved: u32,
    recovery_reason_mask: u64,
    retry_attempt_count: u32,
    maximum_recovery_attempts: u32,
    recovery_deadline_utc_ms: u64,
    atomic_group_id: u64,
    group_member_index: u32,
    group_member_count: u32,
    payload_provenance_digest_low: u64,
    payload_provenance_digest_high: u64,
};

pub const ImageHeader = extern struct {
    magic: u64,
    abi_version: u32,
    header_size: u32,
    record_size: u32,
    flags: u32,
    journal_instance_low: u64,
    journal_instance_high: u64,
    journal_revision: u64,
    entry_count: u32,
    capacity: u32,
    image_length: u64,
    crc64_ecma: u64,
    reserved: [7]u64,
};

pub const SnapshotHeader = extern struct {
    abi_version: u32,
    struct_size: u32,
    journal_revision: u64,
    journal_instance_low: u64,
    journal_instance_high: u64,
    entry_count: u32,
    capacity: u32,
    prepared_count: u32,
    previous_effect_restored_count: u32,
    effect_observed_count: u32,
    feedback_pending_count: u32,
    reconciliation_pending_count: u32,
    authoritative_resync_pending_count: u32,
    recovery_retry_pending_count: u32,
    recovery_blocked_count: u32,
    effect_invocation_uncertain_count: u32,
    snapshot_reserved: u32,
    reserved: [6]u64,
};

pub fn validCreateConfig(value: *const CreateConfig) bool {
    return value.abi_version == abi_version and
        value.struct_size == @sizeOf(CreateConfig) and
        (value.journal_instance_low != 0 or value.journal_instance_high != 0) and
        value.record_capacity != 0 and
        value.flags == 0 and
        value.maximum_resident_bytes != 0 and
        allZero(&value.reserved);
}

pub fn validOpenConfig(value: *const OpenConfig) bool {
    return value.abi_version == abi_version and
        value.struct_size == @sizeOf(OpenConfig) and
        value.maximum_record_capacity != 0 and
        value.flags == 0 and
        value.maximum_resident_bytes != 0 and
        allZero(&value.reserved);
}

pub fn validIdentity(value: *const Identity) bool {
    return value.configuration_generation != 0 and
        value.plan_epoch != 0 and
        value.action_id != 0 and
        value.host_session_incarnation != 0 and
        value.target_id != 0 and
        value.reserved == 0;
}

pub fn validIdentityForScope(value: *const Identity, scope_raw: u32) bool {
    if (!validIdentity(value)) return false;
    return switch (scope_raw) {
        @intFromEnum(Scope.process) => value.process_start_key != 0 and value.process_id != 0,
        @intFromEnum(Scope.software), @intFromEnum(Scope.resource) => value.software_id != 0 and value.process_start_key == 0 and value.process_id == 0,
        else => false,
    };
}

pub fn sameIdentity(left: *const Identity, right: *const Identity) bool {
    return left.configuration_generation == right.configuration_generation and
        left.plan_epoch == right.plan_epoch and
        left.action_id == right.action_id and
        left.host_session_incarnation == right.host_session_incarnation and
        left.target_id == right.target_id and
        left.software_id == right.software_id and
        left.process_start_key == right.process_start_key and
        left.process_id == right.process_id and
        left.reserved == right.reserved;
}

pub fn identityLessThan(left: *const Identity, right: *const Identity) bool {
    if (left.configuration_generation != right.configuration_generation)
        return left.configuration_generation < right.configuration_generation;
    if (left.plan_epoch != right.plan_epoch) return left.plan_epoch < right.plan_epoch;
    if (left.action_id != right.action_id) return left.action_id < right.action_id;
    if (left.host_session_incarnation != right.host_session_incarnation)
        return left.host_session_incarnation < right.host_session_incarnation;
    if (left.target_id != right.target_id) return left.target_id < right.target_id;
    if (left.software_id != right.software_id) return left.software_id < right.software_id;
    if (left.process_start_key != right.process_start_key)
        return left.process_start_key < right.process_start_key;
    if (left.process_id != right.process_id) return left.process_id < right.process_id;
    return left.reserved < right.reserved;
}

pub fn validRecord(value: *const Record) Status {
    if (!validIdentityForScope(&value.identity, value.scope)) return .invalid_identity;
    if (!validDisposition(value.disposition) or !validDomainForScope(value.scope, value.domain_mask))
        return .invalid_argument;
    if (!validGradeSet(
        value.scope,
        value.disposition,
        value.domain_mask,
        value.grade_valid_mask,
        value.grade_reserved,
        value.process_from_grade,
        value.process_to_grade,
        value.cpu_from_grade,
        value.cpu_to_grade,
        value.gpu_from_grade,
        value.gpu_to_grade,
    )) return .invalid_argument;
    if (!validAtomicGroup(
        value.atomic_group_id,
        value.group_member_index,
        value.group_member_count,
    )) return .invalid_argument;
    if (!validPhase(value.phase)) return .unknown_phase;
    if (!validPayload(
        value.payload_kind,
        value.payload_slot,
        value.payload_generation,
        value.payload_length,
        value.payload_digest_low,
        value.payload_digest_high,
        value.payload_reserved,
    )) return .invalid_payload;
    if (!validPayloadProvenance(
        value.payload_kind,
        value.payload_provenance_digest_low,
        value.payload_provenance_digest_high,
    )) return .invalid_payload;
    if (value.prepared_at_utc_ms == 0 or
        value.updated_at_utc_ms == 0 or
        value.updated_at_utc_ms < value.prepared_at_utc_ms or
        value.entry_revision == 0 or
        value.maximum_recovery_attempts == 0 or
        value.retry_attempt_count > value.maximum_recovery_attempts or
        value.recovery_deadline_utc_ms <= value.prepared_at_utc_ms)
    {
        return .invalid_time;
    }
    const phase: Phase = @enumFromInt(value.phase);
    if ((phase == .recovery_retry_pending) != (value.retry_not_before_utc_ms != 0))
        return .invalid_time;
    if (phase == .recovery_retry_pending and
        (value.retry_not_before_utc_ms < value.updated_at_utc_ms or
            value.retry_not_before_utc_ms > value.recovery_deadline_utc_ms))
    {
        return .invalid_time;
    }
    if (!validPersistedFeedback(value)) return .invalid_argument;
    if ((value.recovery_reason_mask & ~RecoveryReason.known) != 0) return .invalid_argument;
    if (!validRecoveryReasons(phase, value.recovery_reason_mask)) return .invalid_argument;
    return .ok;
}

pub fn validAtomicGroup(group_id: u64, member_index: u32, member_count: u32) bool {
    if (group_id == 0) return member_index == 0 and member_count == 0;
    return member_count > 1 and member_index < member_count;
}

pub fn validPayload(
    kind_raw: u32,
    slot: u32,
    generation: u32,
    length: u64,
    digest_low: u64,
    digest_high: u64,
    reserved: u32,
) bool {
    if (reserved != 0) return false;
    return switch (kind_raw) {
        @intFromEnum(PayloadKind.none_required) => slot == 0 and generation == 0 and length == 0 and digest_low == 0 and digest_high == 0,
        @intFromEnum(PayloadKind.durable) => slot != 0 and generation != 0 and length != 0 and digest_low != 0 and digest_high != 0,
        else => false,
    };
}

pub fn validPayloadProvenance(
    kind_raw: u32,
    digest_low: u64,
    digest_high: u64,
) bool {
    return switch (kind_raw) {
        @intFromEnum(PayloadKind.none_required) => digest_low == 0 and digest_high == 0,
        @intFromEnum(PayloadKind.durable) => digest_low != 0 and digest_high != 0,
        else => false,
    };
}

pub fn phaseForEvent(event_raw: u32) ?Phase {
    return switch (event_raw) {
        @intFromEnum(MutationEvent.confirm_previous_effect_restored) => .previous_effect_restored,
        @intFromEnum(MutationEvent.confirm_effect_observed) => .effect_observed,
        else => null,
    };
}

pub fn transitionAllowed(from: Phase, to: Phase) bool {
    return switch (from) {
        .prepared => to == .previous_effect_restored or
            to == .effect_observed,
        .previous_effect_restored => to == .effect_observed,
        else => false,
    };
}

pub fn validPhase(value: u32) bool {
    return switch (value) {
        @intFromEnum(Phase.prepared),
        @intFromEnum(Phase.previous_effect_restored),
        @intFromEnum(Phase.effect_observed),
        @intFromEnum(Phase.feedback_pending),
        @intFromEnum(Phase.reconciliation_pending),
        @intFromEnum(Phase.authoritative_resync_pending),
        @intFromEnum(Phase.recovery_retry_pending),
        @intFromEnum(Phase.recovery_blocked),
        @intFromEnum(Phase.effect_invocation_uncertain),
        => true,
        else => false,
    };
}

pub fn validRecoveryOutcomeForScope(scope_raw: u32, outcome_raw: u32) bool {
    return switch (outcome_raw) {
        @intFromEnum(RecoveryOutcome.restored),
        @intFromEnum(RecoveryOutcome.already_restored),
        @intFromEnum(RecoveryOutcome.ownership_lost),
        @intFromEnum(RecoveryOutcome.retryable_failure),
        @intFromEnum(RecoveryOutcome.invalid_proof),
        @intFromEnum(RecoveryOutcome.unavailable),
        @intFromEnum(RecoveryOutcome.effect_invocation_uncertain),
        @intFromEnum(RecoveryOutcome.authoritative_resync_completed),
        => validScope(scope_raw),
        @intFromEnum(RecoveryOutcome.process_exited) => scope_raw == @intFromEnum(Scope.process),
        else => false,
    };
}

fn validScope(value: u32) bool {
    return value >= @intFromEnum(Scope.process) and value <= @intFromEnum(Scope.resource);
}

fn validDisposition(value: u32) bool {
    return value == @intFromEnum(Disposition.apply) or value == @intFromEnum(Disposition.restore);
}

pub fn validDomainMask(value: u32) bool {
    if (value == 0 or (value & ~DomainBits.known) != 0) return false;
    return true;
}

pub fn validDomainForScope(scope_raw: u32, domain_mask: u32) bool {
    if (!validDomainMask(domain_mask)) return false;
    return switch (scope_raw) {
        @intFromEnum(Scope.process) => domain_mask == DomainBits.process or
            domain_mask == DomainBits.physical_memory,
        @intFromEnum(Scope.software) => (domain_mask & ~DomainBits.software_known) == 0,
        @intFromEnum(Scope.resource) => (domain_mask & ~DomainBits.resource_known) == 0,
        else => false,
    };
}

pub fn validGradeSet(
    scope_raw: u32,
    disposition_raw: u32,
    domain_mask: u32,
    valid_mask: u32,
    reserved: u32,
    process_from: i32,
    process_to: i32,
    cpu_from: i32,
    cpu_to: i32,
    gpu_from: i32,
    gpu_to: i32,
) bool {
    if (reserved != 0 or (valid_mask & ~GradeValid.known) != 0 or
        !validDisposition(disposition_raw)) return false;
    return switch (scope_raw) {
        @intFromEnum(Scope.process) => cpu_from == 0 and cpu_to == 0 and
            gpu_from == 0 and gpu_to == 0 and
            validProcessScopedGradePair(
                disposition_raw,
                domain_mask,
                valid_mask,
                process_from,
                process_to,
            ),
        @intFromEnum(Scope.software) => blk: {
            const required = (if ((domain_mask & DomainBits.cpu) != 0) GradeValid.cpu else 0) |
                (if ((domain_mask & DomainBits.gpu) != 0) GradeValid.gpu else 0);
            break :blk valid_mask == required and
                process_from == 0 and process_to == 0 and
                validAdapterGradePair((required & GradeValid.cpu) != 0, cpu_from, cpu_to) and
                validAdapterGradePair((required & GradeValid.gpu) != 0, gpu_from, gpu_to);
        },
        @intFromEnum(Scope.resource) => valid_mask == 0 and
            process_from == 0 and process_to == 0 and
            cpu_from == 0 and cpu_to == 0 and
            gpu_from == 0 and gpu_to == 0,
        else => false,
    };
}

fn validProcessScopedGradePair(
    disposition_raw: u32,
    domain_mask: u32,
    valid_mask: u32,
    from: i32,
    to: i32,
) bool {
    return switch (domain_mask) {
        DomainBits.process => valid_mask == GradeValid.process and
            validProcessGradePair(from, to),
        DomainBits.physical_memory => valid_mask == GradeValid.memory and
            validMemoryPriorityTransition(disposition_raw, from, to),
        else => false,
    };
}

fn validProcessGradePair(from: i32, to: i32) bool {
    return from >= @intFromEnum(ProcessGrade.level_4) and
        from <= @intFromEnum(ProcessGrade.a1) and
        to >= @intFromEnum(ProcessGrade.level_4) and
        to <= @intFromEnum(ProcessGrade.a1) and
        from != to;
}

fn validMemoryPriorityTransition(disposition_raw: u32, from: i32, to: i32) bool {
    return switch (disposition_raw) {
        @intFromEnum(Disposition.apply) => validMemoryPriorityState(from) and
            validOwnedMemoryPriority(to) and from != to,
        @intFromEnum(Disposition.restore) => validOwnedMemoryPriority(from) and to == 0,
        else => false,
    };
}

fn validMemoryPriorityState(value: i32) bool {
    return value >= 0 and value <= 5;
}

fn validOwnedMemoryPriority(value: i32) bool {
    return value >= 1 and value <= 5;
}

fn validAdapterGradePair(present: bool, from: i32, to: i32) bool {
    if (!present) return from == 0 and to == 0;
    return from >= @intFromEnum(AdapterGrade.freeze) and
        from <= @intFromEnum(AdapterGrade.extreme) and
        to >= @intFromEnum(AdapterGrade.freeze) and
        to <= @intFromEnum(AdapterGrade.extreme) and
        from != to;
}

pub fn validActualGrades(
    scope_raw: u32,
    domain_mask: u32,
    valid_mask: u32,
    process_grade: i32,
    cpu_grade: i32,
    gpu_grade: i32,
    reserved: u32,
) bool {
    if (reserved != 0 or (valid_mask & ~GradeValid.known) != 0) return false;
    return switch (scope_raw) {
        @intFromEnum(Scope.process) => cpu_grade == 0 and gpu_grade == 0 and
            switch (domain_mask) {
                DomainBits.process => valid_mask == GradeValid.process and
                    process_grade >= @intFromEnum(ProcessGrade.level_4) and
                    process_grade <= @intFromEnum(ProcessGrade.a1),
                DomainBits.physical_memory => valid_mask == GradeValid.memory and
                    validMemoryPriorityState(process_grade),
                else => false,
            },
        @intFromEnum(Scope.software) => blk: {
            const required = (if ((domain_mask & DomainBits.cpu) != 0) GradeValid.cpu else 0) |
                (if ((domain_mask & DomainBits.gpu) != 0) GradeValid.gpu else 0);
            break :blk valid_mask == required and
                process_grade == 0 and
                validActualAdapterGrade((required & GradeValid.cpu) != 0, cpu_grade) and
                validActualAdapterGrade((required & GradeValid.gpu) != 0, gpu_grade);
        },
        @intFromEnum(Scope.resource) => valid_mask == 0 and
            process_grade == 0 and cpu_grade == 0 and gpu_grade == 0,
        else => false,
    };
}

pub fn validFeedback(
    scope_raw: u32,
    domain_mask: u32,
    process_from_grade: i32,
    process_to_grade: i32,
    cpu_from_grade: i32,
    cpu_to_grade: i32,
    gpu_from_grade: i32,
    gpu_to_grade: i32,
    valid_mask: u32,
    flags: u32,
    status_raw: u32,
    process_grade: i32,
    cpu_grade: i32,
    gpu_grade: i32,
    reserved: u32,
) bool {
    if (reserved != 0 or
        (valid_mask & FeedbackValid.required) != FeedbackValid.required or
        (valid_mask & ~FeedbackValid.known) != 0 or
        (flags & ~FeedbackFlags.known) != 0 or
        !validFeedbackStatus(status_raw))
    {
        return false;
    }
    return feedback_contract.validate(.{
        .scope = scope_raw,
        .domain_mask = domain_mask,
        .process_from_grade = process_from_grade,
        .process_to_grade = process_to_grade,
        .cpu_from_grade = cpu_from_grade,
        .cpu_to_grade = cpu_to_grade,
        .gpu_from_grade = gpu_from_grade,
        .gpu_to_grade = gpu_to_grade,
        .valid_mask = valid_mask,
        .flags = flags,
        .status = status_raw,
        .actual_process_grade = process_grade,
        .actual_cpu_grade = cpu_grade,
        .actual_gpu_grade = gpu_grade,
    });
}

fn validActualAdapterGrade(present: bool, value: i32) bool {
    if (!present) return value == 0;
    return value >= @intFromEnum(AdapterGrade.freeze) and
        value <= @intFromEnum(AdapterGrade.extreme);
}

fn validActualProcessGrade(present: bool, value: i32) bool {
    if (!present) return value == 0;
    return value >= @intFromEnum(ProcessGrade.level_4) and
        value <= @intFromEnum(ProcessGrade.a1);
}

fn validFeedbackStatus(value: u32) bool {
    return value >= @intFromEnum(FeedbackStatus.succeeded) and
        value <= @intFromEnum(FeedbackStatus.state_uncertain);
}

fn validPersistedFeedback(value: *const Record) bool {
    if (value.feedback_valid_mask == 0) {
        return value.feedback_flags == 0 and
            value.feedback_status == 0 and
            value.feedback_system_status == 0 and
            value.feedback_system_error == 0 and
            value.feedback_reserved == 0 and
            value.feedback_completed_at_utc_ms == 0 and
            value.actual_process_grade == 0 and
            value.actual_cpu_grade == 0 and
            value.actual_gpu_grade == 0 and
            value.actual_reserved == 0 and
            value.phase != @intFromEnum(Phase.feedback_pending);
    }
    if (value.feedback_completed_at_utc_ms == 0 or
        value.feedback_completed_at_utc_ms < value.prepared_at_utc_ms or
        value.feedback_completed_at_utc_ms > value.updated_at_utc_ms)
    {
        return false;
    }
    const phase: Phase = @enumFromInt(value.phase);
    if (phase == .prepared or phase == .previous_effect_restored or phase == .effect_observed)
        return false;
    return validFeedback(
        value.scope,
        value.domain_mask,
        value.process_from_grade,
        value.process_to_grade,
        value.cpu_from_grade,
        value.cpu_to_grade,
        value.gpu_from_grade,
        value.gpu_to_grade,
        value.feedback_valid_mask,
        value.feedback_flags,
        value.feedback_status,
        value.actual_process_grade,
        value.actual_cpu_grade,
        value.actual_gpu_grade,
        value.actual_reserved,
    );
}

fn validRecoveryReasons(phase: Phase, reason_mask: u64) bool {
    if ((reason_mask & ~RecoveryReason.known) != 0) return false;
    return switch (phase) {
        .prepared,
        .previous_effect_restored,
        .effect_observed,
        .feedback_pending,
        => reason_mask == 0,
        .effect_invocation_uncertain => reason_mask == RecoveryReason.effect_invocation_uncertain,
        .reconciliation_pending => (reason_mask & RecoveryReason.accepted_state_uncertain) != 0,
        .authoritative_resync_pending => (reason_mask & (RecoveryReason.restored |
            RecoveryReason.already_restored |
            RecoveryReason.ownership_lost |
            RecoveryReason.process_exited |
            RecoveryReason.ack_stale |
            RecoveryReason.accepted_ownership_lost)) != 0,
        .recovery_retry_pending => (reason_mask & (RecoveryReason.retryable_failure |
            RecoveryReason.unavailable |
            RecoveryReason.ack_uncertain)) != 0,
        .recovery_blocked => (reason_mask & (RecoveryReason.invalid_proof |
            RecoveryReason.ack_rejected |
            RecoveryReason.retry_budget_exhausted)) != 0,
    };
}

fn allZero(values: []const u64) bool {
    for (values) |value| if (value != 0) return false;
    return true;
}

comptime {
    if (@sizeOf(CreateConfig) != 64) @compileError("transaction journal CreateConfig size changed");
    if (@sizeOf(OpenConfig) != 32) @compileError("transaction journal OpenConfig size changed");
    if (@sizeOf(Capacity) != 32) @compileError("transaction journal Capacity size changed");
    if (@sizeOf(Identity) != 64) @compileError("transaction journal Identity size changed");
    if (@sizeOf(PrepareInput) != 224) @compileError("transaction journal PrepareInput size changed");
    if (@sizeOf(PrepareBatchInput) != 48) @compileError("transaction journal PrepareBatchInput size changed");
    if (@sizeOf(MutationInput) != 160) @compileError("transaction journal MutationInput size changed");
    if (@sizeOf(StageFeedbackInput) != 176) @compileError("transaction journal StageFeedbackInput size changed");
    if (@sizeOf(RecoveryEvidenceInput) != 160) @compileError("transaction journal RecoveryEvidenceInput size changed");
    if (@sizeOf(AckInput) != 160) @compileError("transaction journal AckInput size changed");
    if (@sizeOf(Record) != 296) @compileError("transaction journal Record size changed");
    if (@sizeOf(ImageHeader) != 128) @compileError("transaction journal ImageHeader size changed");
    if (@sizeOf(SnapshotHeader) != 128) @compileError("transaction journal SnapshotHeader size changed");
}
