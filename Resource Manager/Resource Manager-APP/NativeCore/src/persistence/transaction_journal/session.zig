const std = @import("std");
const codec = @import("codec.zig");
const protocol = @import("protocol.zig");
const state = @import("state.zig");

pub const Session = state.Session;

pub fn prepare(session: *Session, input: *const protocol.PrepareInput) protocol.Status {
    state.lockMutex(&session.mutex);
    defer session.mutex.unlock();
    const validity = validatePrepareUnlocked(session, input);
    if (validity != .ok) return validity;
    if (input.atomic_group_id != 0) return .invalid_argument;
    if (session.count == session.config.record_capacity) return .capacity_full;
    const next_journal_revision = incrementRevision(session.journal_revision) orelse
        return .revision_exhausted;
    const record = recordFromPrepare(input);
    const index = session.count;
    session.records[index] = record;
    session.count += 1;
    if (!session.insertIndex(index)) {
        session.count -= 1;
        session.records[index] = std.mem.zeroes(protocol.Record);
        return .capacity_full;
    }
    session.journal_revision = next_journal_revision;
    return .ok;
}

pub fn prepareBatch(
    session: *Session,
    batch: *const protocol.PrepareBatchInput,
    inputs: []const protocol.PrepareInput,
) protocol.Status {
    state.lockMutex(&session.mutex);
    defer session.mutex.unlock();
    if (batch.abi_version != protocol.abi_version or
        batch.struct_size != @sizeOf(protocol.PrepareBatchInput) or
        batch.flags != 0 or
        !reservedZero(&batch.reserved))
    {
        return .abi_mismatch;
    }
    if (batch.input_count != inputs.len or inputs.len < 2) return .invalid_argument;
    if (batch.expected_journal_revision != session.journal_revision) return .stale_revision;
    if (inputs.len > session.config.record_capacity - session.count) return .capacity_full;

    const first = &inputs[0];
    if (first.identity.software_id == 0) return .invalid_identity;
    var input_index: usize = 0;
    while (input_index < inputs.len) : (input_index += 1) {
        const input = &inputs[input_index];
        const validity = validatePrepareUnlocked(session, input);
        if (validity != .ok) return validity;
        if (input.expected_journal_revision != batch.expected_journal_revision or
            input.atomic_group_id == 0 or
            input.atomic_group_id != first.atomic_group_id or
            input.group_member_count != inputs.len or
            input.identity.configuration_generation != first.identity.configuration_generation or
            input.identity.plan_epoch != first.identity.plan_epoch or
            input.identity.host_session_incarnation != first.identity.host_session_incarnation or
            input.identity.software_id != first.identity.software_id or
            input.scope != @intFromEnum(protocol.Scope.process) or
            input.disposition != @intFromEnum(protocol.Disposition.apply) or
            input.domain_mask != protocol.DomainBits.process or
            input.process_to_grade != @intFromEnum(protocol.ProcessGrade.level_4) or
            input.now_utc_ms != first.now_utc_ms or
            input.maximum_recovery_attempts != first.maximum_recovery_attempts or
            input.recovery_deadline_utc_ms != first.recovery_deadline_utc_ms)
        {
            return .invalid_argument;
        }

        var previous_index: usize = 0;
        while (previous_index < input_index) : (previous_index += 1) {
            const previous = &inputs[previous_index];
            if (protocol.sameIdentity(&previous.identity, &input.identity) or
                previous.group_member_index == input.group_member_index)
            {
                return .duplicate_identity;
            }
        }
    }

    const next_journal_revision = incrementRevision(session.journal_revision) orelse
        return .revision_exhausted;
    const original_count: usize = session.count;
    for (inputs, 0..) |*input, offset| {
        session.records[original_count + offset] = recordFromPrepare(input);
    }
    session.count = @intCast(original_count + inputs.len);
    if (!session.rebuildIndex()) {
        @memset(
            session.records[original_count..@as(usize, session.count)],
            std.mem.zeroes(protocol.Record),
        );
        session.count = @intCast(original_count);
        if (!session.rebuildIndex()) return .corrupt_image;
        return .capacity_full;
    }
    session.journal_revision = next_journal_revision;
    return .ok;
}

fn validatePrepareUnlocked(
    session: *Session,
    input: *const protocol.PrepareInput,
) protocol.Status {
    if (input.abi_version != protocol.abi_version or
        input.struct_size != @sizeOf(protocol.PrepareInput) or
        input.retry_policy_reserved != 0)
    {
        return .abi_mismatch;
    }
    if (!protocol.validIdentityForScope(&input.identity, input.scope)) return .invalid_identity;
    if (input.expected_journal_revision != session.journal_revision) return .stale_revision;
    if (session.find(&input.identity) != null) return .duplicate_identity;
    if (input.now_utc_ms == 0 or
        input.maximum_recovery_attempts == 0 or
        input.recovery_deadline_utc_ms <= input.now_utc_ms)
    {
        return .invalid_time;
    }
    if (!protocol.validPayload(
        input.payload_kind,
        input.payload_slot,
        input.payload_generation,
        input.payload_length,
        input.payload_digest_low,
        input.payload_digest_high,
        input.payload_reserved,
    )) return .invalid_payload;
    if (!protocol.validPayloadProvenance(
        input.payload_kind,
        input.payload_provenance_digest_low,
        input.payload_provenance_digest_high,
    )) return .invalid_payload;
    const record = recordFromPrepare(input);
    return protocol.validRecord(&record);
}

fn recordFromPrepare(input: *const protocol.PrepareInput) protocol.Record {
    return .{
        .identity = input.identity,
        .scope = input.scope,
        .disposition = input.disposition,
        .domain_mask = input.domain_mask,
        .phase = @intFromEnum(protocol.Phase.prepared),
        .grade_valid_mask = input.grade_valid_mask,
        .grade_reserved = 0,
        .stable_system_status = input.stable_system_status,
        .stable_system_error = input.stable_system_error,
        .process_from_grade = input.process_from_grade,
        .process_to_grade = input.process_to_grade,
        .cpu_from_grade = input.cpu_from_grade,
        .cpu_to_grade = input.cpu_to_grade,
        .gpu_from_grade = input.gpu_from_grade,
        .gpu_to_grade = input.gpu_to_grade,
        .payload_kind = input.payload_kind,
        .payload_slot = input.payload_slot,
        .payload_generation = input.payload_generation,
        .payload_reserved = 0,
        .payload_length = input.payload_length,
        .payload_digest_low = input.payload_digest_low,
        .payload_digest_high = input.payload_digest_high,
        .prepared_at_utc_ms = input.now_utc_ms,
        .updated_at_utc_ms = input.now_utc_ms,
        .retry_not_before_utc_ms = 0,
        .entry_revision = 1,
        .feedback_valid_mask = 0,
        .feedback_flags = 0,
        .feedback_status = 0,
        .feedback_system_status = 0,
        .feedback_system_error = 0,
        .feedback_reserved = 0,
        .feedback_completed_at_utc_ms = 0,
        .actual_process_grade = 0,
        .actual_cpu_grade = 0,
        .actual_gpu_grade = 0,
        .actual_reserved = 0,
        .recovery_reason_mask = 0,
        .retry_attempt_count = 0,
        .maximum_recovery_attempts = input.maximum_recovery_attempts,
        .recovery_deadline_utc_ms = input.recovery_deadline_utc_ms,
        .atomic_group_id = input.atomic_group_id,
        .group_member_index = input.group_member_index,
        .group_member_count = input.group_member_count,
        .payload_provenance_digest_low = input.payload_provenance_digest_low,
        .payload_provenance_digest_high = input.payload_provenance_digest_high,
    };
}

pub fn mutate(session: *Session, input: *const protocol.MutationInput) protocol.Status {
    state.lockMutex(&session.mutex);
    defer session.mutex.unlock();
    if (input.abi_version != protocol.abi_version or
        input.struct_size != @sizeOf(protocol.MutationInput) or
        !reservedZero(&input.reserved))
    {
        return .abi_mismatch;
    }
    if (!protocol.validIdentity(&input.identity)) return .invalid_identity;
    if (input.expected_journal_revision != session.journal_revision) return .stale_revision;
    const record_index = session.find(&input.identity) orelse return .no_data;
    const record = &session.records[record_index];
    if (input.expected_entry_revision != record.entry_revision) return .stale_revision;
    if (!protocol.validPhase(input.expected_phase) or input.expected_phase != record.phase)
        return .phase_mismatch;
    if (input.now_utc_ms == 0 or input.now_utc_ms < record.updated_at_utc_ms)
        return .invalid_time;
    const next_phase = protocol.phaseForEvent(input.event) orelse return .invalid_argument;
    const current_phase: protocol.Phase = @enumFromInt(record.phase);
    if (!protocol.transitionAllowed(current_phase, next_phase)) return .invalid_transition;
    const next_entry_revision = incrementRevision(record.entry_revision) orelse
        return .revision_exhausted;
    const next_journal_revision = incrementRevision(session.journal_revision) orelse
        return .revision_exhausted;
    record.phase = @intFromEnum(next_phase);
    record.updated_at_utc_ms = input.now_utc_ms;
    record.retry_not_before_utc_ms = 0;
    record.stable_system_status = input.stable_system_status;
    record.stable_system_error = input.stable_system_error;
    record.entry_revision = next_entry_revision;
    session.journal_revision = next_journal_revision;
    return .ok;
}

pub fn stageFeedback(session: *Session, input: *const protocol.StageFeedbackInput) protocol.Status {
    state.lockMutex(&session.mutex);
    defer session.mutex.unlock();
    if (input.abi_version != protocol.abi_version or
        input.struct_size != @sizeOf(protocol.StageFeedbackInput) or
        !reservedZero(&input.reserved))
    {
        return .abi_mismatch;
    }
    if (!protocol.validIdentity(&input.identity)) return .invalid_identity;
    const record_index = session.find(&input.identity) orelse return .no_data;
    if (input.expected_journal_revision != session.journal_revision) return .stale_revision;
    const record = &session.records[record_index];
    if (input.expected_entry_revision != record.entry_revision) return .stale_revision;
    const expected_phase: protocol.Phase = switch (input.expected_phase) {
        @intFromEnum(protocol.Phase.previous_effect_restored) => .previous_effect_restored,
        @intFromEnum(protocol.Phase.effect_observed) => .effect_observed,
        else => return .phase_mismatch,
    };
    if (record.phase != input.expected_phase) {
        return .phase_mismatch;
    }
    if (input.completed_at_utc_ms == 0 or input.completed_at_utc_ms < record.updated_at_utc_ms)
        return .invalid_time;
    if (expected_phase == .previous_effect_restored and
        input.feedback_status != @intFromEnum(protocol.FeedbackStatus.failed_unchanged) and
        input.feedback_status != @intFromEnum(protocol.FeedbackStatus.rejected) and
        input.feedback_status != @intFromEnum(protocol.FeedbackStatus.skipped) and
        input.feedback_status != @intFromEnum(protocol.FeedbackStatus.ownership_lost))
    {
        return .invalid_argument;
    }
    if (!protocol.validFeedback(
        record.scope,
        record.domain_mask,
        record.process_from_grade,
        record.process_to_grade,
        record.cpu_from_grade,
        record.cpu_to_grade,
        record.gpu_from_grade,
        record.gpu_to_grade,
        input.feedback_valid_mask,
        input.feedback_flags,
        input.feedback_status,
        input.actual_process_grade,
        input.actual_cpu_grade,
        input.actual_gpu_grade,
        input.actual_reserved,
    ) or input.feedback_reserved != 0) return .invalid_argument;
    const next_entry_revision = incrementRevision(record.entry_revision) orelse
        return .revision_exhausted;
    const next_journal_revision = incrementRevision(session.journal_revision) orelse
        return .revision_exhausted;
    record.phase = @intFromEnum(protocol.Phase.feedback_pending);
    record.updated_at_utc_ms = input.completed_at_utc_ms;
    record.retry_not_before_utc_ms = 0;
    record.stable_system_status = input.feedback_system_status;
    record.stable_system_error = input.feedback_system_error;
    record.feedback_valid_mask = input.feedback_valid_mask;
    record.feedback_flags = input.feedback_flags;
    record.feedback_status = input.feedback_status;
    record.feedback_system_status = input.feedback_system_status;
    record.feedback_system_error = input.feedback_system_error;
    record.feedback_reserved = 0;
    record.feedback_completed_at_utc_ms = input.completed_at_utc_ms;
    record.actual_process_grade = input.actual_process_grade;
    record.actual_cpu_grade = input.actual_cpu_grade;
    record.actual_gpu_grade = input.actual_gpu_grade;
    record.actual_reserved = input.actual_reserved;
    record.entry_revision = next_entry_revision;
    session.journal_revision = next_journal_revision;
    return .ok;
}

pub fn applyRecoveryEvidence(
    session: *Session,
    input: *const protocol.RecoveryEvidenceInput,
) protocol.Status {
    state.lockMutex(&session.mutex);
    defer session.mutex.unlock();
    if (input.abi_version != protocol.abi_version or
        input.struct_size != @sizeOf(protocol.RecoveryEvidenceInput) or
        input.flags != 0 or
        input.evidence_reserved != 0 or
        !reservedZero(&input.reserved))
    {
        return .abi_mismatch;
    }
    if (!protocol.validIdentity(&input.identity)) return .invalid_identity;
    const record_index = session.find(&input.identity) orelse return .no_data;
    if (input.expected_journal_revision != session.journal_revision) return .stale_revision;
    const record = &session.records[record_index];
    if (input.expected_entry_revision != record.entry_revision) return .stale_revision;
    if (!protocol.validPhase(input.expected_phase) or input.expected_phase != record.phase)
        return .phase_mismatch;
    if (input.observed_at_utc_ms == 0 or input.observed_at_utc_ms < record.updated_at_utc_ms)
        return .invalid_time;

    const classification = classifyRecovery(input.outcome) orelse return .invalid_argument;
    if (!protocol.validRecoveryOutcomeForScope(record.scope, input.outcome)) return .invalid_argument;
    const current_phase: protocol.Phase = @enumFromInt(record.phase);
    if (!recoveryTransitionAllowed(current_phase, classification)) return .invalid_transition;
    if (classification.terminal != (input.authoritative_facts_generation != 0))
        return .invalid_argument;
    if (classification.phase == .recovery_retry_pending) {
        if (input.retry_not_before_utc_ms < input.observed_at_utc_ms) return .invalid_time;
    } else if (input.retry_not_before_utc_ms != 0) return .invalid_time;

    const next_journal_revision = incrementRevision(session.journal_revision) orelse
        return .revision_exhausted;
    if (classification.terminal) {
        if (!session.removeAt(record_index)) return .corrupt_image;
        session.journal_revision = next_journal_revision;
        return .ok;
    }
    const next_entry_revision = incrementRevision(record.entry_revision) orelse
        return .revision_exhausted;
    const retry = applyRetryBudget(
        record,
        classification.phase,
        input.observed_at_utc_ms,
        input.retry_not_before_utc_ms,
        classification.reason,
    );
    record.phase = @intFromEnum(retry.phase);
    record.updated_at_utc_ms = input.observed_at_utc_ms;
    record.retry_not_before_utc_ms = retry.retry_not_before_utc_ms;
    record.stable_system_status = input.stable_system_status;
    record.stable_system_error = input.stable_system_error;
    record.recovery_reason_mask |= retry.reason;
    record.retry_attempt_count = retry.attempt_count;
    record.entry_revision = next_entry_revision;
    session.journal_revision = next_journal_revision;
    return .ok;
}

pub fn acknowledge(session: *Session, input: *const protocol.AckInput) protocol.Status {
    state.lockMutex(&session.mutex);
    defer session.mutex.unlock();
    if (input.abi_version != protocol.abi_version or
        input.struct_size != @sizeOf(protocol.AckInput) or
        input.flags != 0 or
        input.ack_reserved != 0 or
        !reservedZero(&input.reserved))
    {
        return .abi_mismatch;
    }
    if (!protocol.validIdentity(&input.identity)) return .invalid_identity;
    const record_index = session.find(&input.identity) orelse return .no_data;
    if (input.expected_journal_revision != session.journal_revision) return .stale_revision;
    const record = &session.records[record_index];
    if (input.expected_entry_revision != record.entry_revision) return .stale_revision;
    if (!protocol.validPhase(input.expected_phase) or
        input.expected_phase != @intFromEnum(protocol.Phase.feedback_pending) or
        input.expected_phase != record.phase)
        return .phase_mismatch;
    if (!protocol.validFeedback(
        record.scope,
        record.domain_mask,
        record.process_from_grade,
        record.process_to_grade,
        record.cpu_from_grade,
        record.cpu_to_grade,
        record.gpu_from_grade,
        record.gpu_to_grade,
        record.feedback_valid_mask,
        record.feedback_flags,
        record.feedback_status,
        record.actual_process_grade,
        record.actual_cpu_grade,
        record.actual_gpu_grade,
        record.actual_reserved,
    ) or record.feedback_reserved != 0) return .corrupt_image;
    if (input.acknowledged_at_utc_ms == 0 or input.acknowledged_at_utc_ms < record.updated_at_utc_ms)
        return .invalid_time;
    const result = ackClassification(input.result, record.feedback_status) orelse
        return .invalid_argument;
    if (result.terminal) {
        if (input.retry_not_before_utc_ms != 0) return .invalid_time;
        const next_journal_revision = incrementRevision(session.journal_revision) orelse
            return .revision_exhausted;
        if (!session.removeAt(record_index)) return .corrupt_image;
        session.journal_revision = next_journal_revision;
        return .ok;
    }
    if ((result.phase == .recovery_retry_pending) != (input.retry_not_before_utc_ms != 0) or
        (result.phase == .recovery_retry_pending and
            input.retry_not_before_utc_ms < input.acknowledged_at_utc_ms))
        return .invalid_time;
    const next_entry_revision = incrementRevision(record.entry_revision) orelse
        return .revision_exhausted;
    const next_journal_revision = incrementRevision(session.journal_revision) orelse
        return .revision_exhausted;
    const retry = applyRetryBudget(
        record,
        result.phase,
        input.acknowledged_at_utc_ms,
        input.retry_not_before_utc_ms,
        result.reason,
    );
    record.phase = @intFromEnum(retry.phase);
    record.updated_at_utc_ms = input.acknowledged_at_utc_ms;
    record.retry_not_before_utc_ms = retry.retry_not_before_utc_ms;
    record.stable_system_status = input.stable_system_status;
    record.stable_system_error = input.stable_system_error;
    record.recovery_reason_mask |= retry.reason;
    record.retry_attempt_count = retry.attempt_count;
    record.entry_revision = next_entry_revision;
    session.journal_revision = next_journal_revision;
    return .ok;
}

pub fn get(
    session: *Session,
    identity: *const protocol.Identity,
    output: *protocol.Record,
) protocol.Status {
    state.lockMutex(&session.mutex);
    defer session.mutex.unlock();
    if (!protocol.validIdentity(identity)) return .invalid_identity;
    const record_index = session.find(identity) orelse return .no_data;
    output.* = session.records[record_index];
    return .ok;
}

pub fn snapshot(
    session: *Session,
    header: *protocol.SnapshotHeader,
    output: []protocol.Record,
) protocol.Status {
    state.lockMutex(&session.mutex);
    defer session.mutex.unlock();
    if (header.abi_version != protocol.abi_version or
        header.struct_size != @sizeOf(protocol.SnapshotHeader) or
        header.journal_revision != 0 or
        header.journal_instance_low != 0 or
        header.journal_instance_high != 0 or
        header.entry_count != 0 or
        header.capacity != 0 or
        header.prepared_count != 0 or
        header.previous_effect_restored_count != 0 or
        header.effect_observed_count != 0 or
        header.feedback_pending_count != 0 or
        header.reconciliation_pending_count != 0 or
        header.authoritative_resync_pending_count != 0 or
        header.recovery_retry_pending_count != 0 or
        header.recovery_blocked_count != 0 or
        header.effect_invocation_uncertain_count != 0 or
        header.snapshot_reserved != 0 or
        !reservedZero(&header.reserved))
    {
        return .abi_mismatch;
    }
    if (output.len < session.count) return .buffer_too_small;
    @memset(output, std.mem.zeroes(protocol.Record));
    const indices = session.prepareSortIndices();
    for (indices, 0..) |record_index, output_index| {
        const record = session.records[record_index];
        output[output_index] = record;
        switch (@as(protocol.Phase, @enumFromInt(record.phase))) {
            .prepared => header.prepared_count += 1,
            .previous_effect_restored => header.previous_effect_restored_count += 1,
            .effect_observed => header.effect_observed_count += 1,
            .feedback_pending => header.feedback_pending_count += 1,
            .reconciliation_pending => header.reconciliation_pending_count += 1,
            .authoritative_resync_pending => header.authoritative_resync_pending_count += 1,
            .recovery_retry_pending => header.recovery_retry_pending_count += 1,
            .recovery_blocked => header.recovery_blocked_count += 1,
            .effect_invocation_uncertain => header.effect_invocation_uncertain_count += 1,
        }
    }
    header.journal_revision = session.journal_revision;
    header.journal_instance_low = session.config.journal_instance_low;
    header.journal_instance_high = session.config.journal_instance_high;
    header.entry_count = session.count;
    header.capacity = session.config.record_capacity;
    return .ok;
}

pub fn encode(session: *Session, output: []u8, written: *u64) protocol.Status {
    state.lockMutex(&session.mutex);
    defer session.mutex.unlock();
    return codec.encodeUnlocked(session, output, written);
}

pub fn decode(session: *Session, image: []const u8) protocol.Status {
    state.lockMutex(&session.mutex);
    defer session.mutex.unlock();
    return codec.decodeUnlocked(session, image);
}

pub fn openExisting(
    open_config: *const protocol.OpenConfig,
    image: []const u8,
    output: *?*Session,
) protocol.Status {
    return openExistingWithAllocator(open_config, image, std.heap.page_allocator, output);
}

pub fn openExistingWithAllocator(
    open_config: *const protocol.OpenConfig,
    image: []const u8,
    allocator: std.mem.Allocator,
    output: *?*Session,
) protocol.Status {
    output.* = null;
    if (!protocol.validOpenConfig(open_config)) return .abi_mismatch;
    var metadata: codec.ImageMetadata = undefined;
    const inspection = codec.inspectImage(image, &metadata);
    if (inspection != .ok) return inspection;
    if (metadata.capacity > open_config.maximum_record_capacity) return .capacity_full;
    const resident_bytes = state.requiredResidentBytes(metadata.capacity) catch return .capacity_full;
    if (resident_bytes > open_config.maximum_resident_bytes) return .capacity_full;
    var create_config: protocol.CreateConfig = .{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.CreateConfig),
        .journal_instance_low = metadata.journal_instance_low,
        .journal_instance_high = metadata.journal_instance_high,
        .record_capacity = metadata.capacity,
        .flags = 0,
        .maximum_resident_bytes = open_config.maximum_resident_bytes,
        .reserved = .{ 0, 0, 0 },
    };
    const opened = Session.createWithAllocator(&create_config, allocator) catch return .out_of_memory;
    const decoded = decode(opened, image);
    if (decoded != .ok) {
        opened.destroy();
        return decoded;
    }
    output.* = opened;
    return .ok;
}

fn incrementRevision(value: u64) ?u64 {
    if (value == std.math.maxInt(u64)) return null;
    return value + 1;
}

const RecoveryClassification = struct {
    terminal: bool,
    phase: protocol.Phase,
    reason: u64,
};

fn classifyRecovery(outcome_raw: u32) ?RecoveryClassification {
    return switch (outcome_raw) {
        @intFromEnum(protocol.RecoveryOutcome.restored) => .{
            .terminal = false,
            .phase = .authoritative_resync_pending,
            .reason = protocol.RecoveryReason.restored,
        },
        @intFromEnum(protocol.RecoveryOutcome.already_restored) => .{
            .terminal = false,
            .phase = .authoritative_resync_pending,
            .reason = protocol.RecoveryReason.already_restored,
        },
        @intFromEnum(protocol.RecoveryOutcome.ownership_lost) => .{
            .terminal = false,
            .phase = .authoritative_resync_pending,
            .reason = protocol.RecoveryReason.ownership_lost,
        },
        @intFromEnum(protocol.RecoveryOutcome.retryable_failure) => .{
            .terminal = false,
            .phase = .recovery_retry_pending,
            .reason = protocol.RecoveryReason.retryable_failure,
        },
        @intFromEnum(protocol.RecoveryOutcome.invalid_proof) => .{
            .terminal = false,
            .phase = .recovery_blocked,
            .reason = protocol.RecoveryReason.invalid_proof,
        },
        @intFromEnum(protocol.RecoveryOutcome.unavailable) => .{
            .terminal = false,
            .phase = .recovery_retry_pending,
            .reason = protocol.RecoveryReason.unavailable,
        },
        @intFromEnum(protocol.RecoveryOutcome.process_exited) => .{
            .terminal = false,
            .phase = .authoritative_resync_pending,
            .reason = protocol.RecoveryReason.process_exited,
        },
        @intFromEnum(protocol.RecoveryOutcome.effect_invocation_uncertain) => .{
            .terminal = false,
            .phase = .effect_invocation_uncertain,
            .reason = protocol.RecoveryReason.effect_invocation_uncertain,
        },
        @intFromEnum(protocol.RecoveryOutcome.authoritative_resync_completed) => .{
            .terminal = true,
            .phase = .authoritative_resync_pending,
            .reason = 0,
        },
        else => null,
    };
}

fn recoveryTransitionAllowed(
    current: protocol.Phase,
    classification: RecoveryClassification,
) bool {
    if (classification.terminal) return current == .authoritative_resync_pending;
    const classified = classification.phase;
    if (classified == .effect_invocation_uncertain) {
        return current == .prepared or current == .previous_effect_restored;
    }
    return switch (current) {
        .authoritative_resync_pending => classified == .recovery_retry_pending or
            classified == .recovery_blocked,
        .prepared,
        .previous_effect_restored,
        .effect_observed,
        .feedback_pending,
        .reconciliation_pending,
        .recovery_retry_pending,
        .recovery_blocked,
        .effect_invocation_uncertain,
        => switch (classified) {
            .authoritative_resync_pending,
            .recovery_retry_pending,
            .recovery_blocked,
            => true,
            else => false,
        },
    };
}

const AckClassification = struct {
    terminal: bool,
    phase: protocol.Phase,
    reason: u64,
};

const RetryBudgetDecision = struct {
    phase: protocol.Phase,
    retry_not_before_utc_ms: u64,
    attempt_count: u32,
    reason: u64,
};

fn applyRetryBudget(
    record: *const protocol.Record,
    requested_phase: protocol.Phase,
    observed_at_utc_ms: u64,
    requested_retry_not_before_utc_ms: u64,
    reason: u64,
) RetryBudgetDecision {
    if (requested_phase != .recovery_retry_pending) {
        return .{
            .phase = requested_phase,
            .retry_not_before_utc_ms = 0,
            .attempt_count = record.retry_attempt_count,
            .reason = reason,
        };
    }
    const exhausted = record.retry_attempt_count >= record.maximum_recovery_attempts or
        observed_at_utc_ms >= record.recovery_deadline_utc_ms or
        requested_retry_not_before_utc_ms > record.recovery_deadline_utc_ms;
    if (exhausted) {
        return .{
            .phase = .recovery_blocked,
            .retry_not_before_utc_ms = 0,
            .attempt_count = record.retry_attempt_count,
            .reason = reason | protocol.RecoveryReason.retry_budget_exhausted,
        };
    }
    return .{
        .phase = .recovery_retry_pending,
        .retry_not_before_utc_ms = requested_retry_not_before_utc_ms,
        .attempt_count = record.retry_attempt_count + 1,
        .reason = reason,
    };
}

fn ackClassification(result_raw: u32, feedback_status_raw: u32) ?AckClassification {
    return switch (result_raw) {
        @intFromEnum(protocol.AckResult.accepted) => switch (feedback_status_raw) {
            @intFromEnum(protocol.FeedbackStatus.state_uncertain) => .{
                .terminal = false,
                .phase = .reconciliation_pending,
                .reason = protocol.RecoveryReason.accepted_state_uncertain,
            },
            @intFromEnum(protocol.FeedbackStatus.ownership_lost) => .{
                .terminal = false,
                .phase = .authoritative_resync_pending,
                .reason = protocol.RecoveryReason.accepted_ownership_lost,
            },
            @intFromEnum(protocol.FeedbackStatus.succeeded),
            @intFromEnum(protocol.FeedbackStatus.failed_unchanged),
            @intFromEnum(protocol.FeedbackStatus.rejected),
            @intFromEnum(protocol.FeedbackStatus.skipped),
            => .{
                .terminal = true,
                .phase = .feedback_pending,
                .reason = 0,
            },
            else => return null,
        },
        @intFromEnum(protocol.AckResult.uncertain) => .{
            .terminal = false,
            .phase = .recovery_retry_pending,
            .reason = protocol.RecoveryReason.ack_uncertain,
        },
        @intFromEnum(protocol.AckResult.stale) => .{
            .terminal = false,
            .phase = .authoritative_resync_pending,
            .reason = protocol.RecoveryReason.ack_stale,
        },
        @intFromEnum(protocol.AckResult.rejected) => .{
            .terminal = false,
            .phase = .recovery_blocked,
            .reason = protocol.RecoveryReason.ack_rejected,
        },
        else => null,
    };
}

fn reservedZero(values: []const u64) bool {
    for (values) |value| if (value != 0) return false;
    return true;
}
