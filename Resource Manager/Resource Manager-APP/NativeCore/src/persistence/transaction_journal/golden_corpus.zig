const protocol = @import("protocol.zig");

const test_resident_budget: u64 = 16 * 1024 * 1024;

pub fn createConfig(capacity: u32) protocol.CreateConfig {
    return .{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.CreateConfig),
        .journal_instance_low = 0x1111_2222_3333_4444,
        .journal_instance_high = 0xaaaa_bbbb_cccc_dddd,
        .record_capacity = capacity,
        .flags = 0,
        .maximum_resident_bytes = test_resident_budget,
        .reserved = .{ 0, 0, 0 },
    };
}

pub fn openConfig(maximum_capacity: u32) protocol.OpenConfig {
    return .{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.OpenConfig),
        .maximum_record_capacity = maximum_capacity,
        .flags = 0,
        .maximum_resident_bytes = test_resident_budget,
        .reserved = .{0},
    };
}

pub fn processIdentity(generation: u64, action_id: u64) protocol.Identity {
    return .{
        .configuration_generation = generation,
        .plan_epoch = 19,
        .action_id = action_id,
        .host_session_incarnation = 23,
        .target_id = 29 + action_id,
        .software_id = 31,
        .process_start_key = 37,
        .process_id = 41,
        .reserved = 0,
    };
}

pub fn softwareIdentity(generation: u64, action_id: u64) protocol.Identity {
    var value = processIdentity(generation, action_id);
    value.process_start_key = 0;
    value.process_id = 0;
    return value;
}

pub fn processPrepare(
    generation: u64,
    action_id: u64,
    expected_journal_revision: u64,
    now_utc_ms: u64,
    payload_kind: protocol.PayloadKind,
) protocol.PrepareInput {
    const durable = payload_kind == .durable;
    return .{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.PrepareInput),
        .expected_journal_revision = expected_journal_revision,
        .identity = processIdentity(generation, action_id),
        .scope = @intFromEnum(protocol.Scope.process),
        .disposition = @intFromEnum(protocol.Disposition.apply),
        .domain_mask = protocol.DomainBits.process,
        .grade_valid_mask = protocol.GradeValid.process,
        .process_from_grade = @intFromEnum(protocol.ProcessGrade.normal),
        .process_to_grade = @intFromEnum(protocol.ProcessGrade.level_1),
        .cpu_from_grade = 0,
        .cpu_to_grade = 0,
        .gpu_from_grade = 0,
        .gpu_to_grade = 0,
        .stable_system_status = 100,
        .stable_system_error = 0,
        .payload_kind = @intFromEnum(payload_kind),
        .payload_slot = if (durable) @intCast(100 + action_id) else 0,
        .payload_generation = if (durable) 3 else 0,
        .payload_reserved = 0,
        .payload_length = if (durable) 512 else 0,
        .payload_digest_low = if (durable) 0x1234 else 0,
        .payload_digest_high = if (durable) 0x5678 else 0,
        .now_utc_ms = now_utc_ms,
        .maximum_recovery_attempts = 3,
        .retry_policy_reserved = 0,
        .recovery_deadline_utc_ms = now_utc_ms + 10_000,
        .atomic_group_id = 0,
        .group_member_index = 0,
        .group_member_count = 0,
        .payload_provenance_digest_low = if (durable) 0x9abc else 0,
        .payload_provenance_digest_high = if (durable) 0xdef0 else 0,
    };
}

pub fn softwarePrepare(
    generation: u64,
    action_id: u64,
    expected_journal_revision: u64,
    now_utc_ms: u64,
) protocol.PrepareInput {
    var value = processPrepare(
        generation,
        action_id,
        expected_journal_revision,
        now_utc_ms,
        .none_required,
    );
    value.identity = softwareIdentity(generation, action_id);
    value.scope = @intFromEnum(protocol.Scope.software);
    value.domain_mask = protocol.DomainBits.cpu | protocol.DomainBits.gpu;
    value.grade_valid_mask = protocol.GradeValid.cpu | protocol.GradeValid.gpu;
    value.process_from_grade = 0;
    value.process_to_grade = 0;
    value.cpu_from_grade = @intFromEnum(protocol.AdapterGrade.normal);
    value.cpu_to_grade = @intFromEnum(protocol.AdapterGrade.optimize);
    value.gpu_from_grade = @intFromEnum(protocol.AdapterGrade.normal);
    value.gpu_to_grade = @intFromEnum(protocol.AdapterGrade.freeze);
    return value;
}

pub fn resourcePrepare(
    generation: u64,
    action_id: u64,
    expected_journal_revision: u64,
    now_utc_ms: u64,
) protocol.PrepareInput {
    var value = processPrepare(
        generation,
        action_id,
        expected_journal_revision,
        now_utc_ms,
        .durable,
    );
    value.identity = softwareIdentity(generation, action_id);
    value.scope = @intFromEnum(protocol.Scope.resource);
    value.domain_mask = protocol.DomainBits.physical_memory | protocol.DomainBits.shared_resource;
    value.grade_valid_mask = 0;
    value.process_from_grade = 0;
    value.process_to_grade = 0;
    return value;
}

pub fn mutation(
    identity_value: protocol.Identity,
    expected_journal_revision: u64,
    expected_entry_revision: u64,
    expected_phase: protocol.Phase,
    event: protocol.MutationEvent,
    now_utc_ms: u64,
) protocol.MutationInput {
    return .{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.MutationInput),
        .identity = identity_value,
        .expected_journal_revision = expected_journal_revision,
        .expected_entry_revision = expected_entry_revision,
        .now_utc_ms = now_utc_ms,
        .event = @intFromEnum(event),
        .expected_phase = @intFromEnum(expected_phase),
        .stable_system_status = 200 + @intFromEnum(event),
        .stable_system_error = 0,
        .reserved = .{ 0, 0, 0, 0, 0, 0 },
    };
}

pub fn processFeedback(
    identity_value: protocol.Identity,
    expected_journal_revision: u64,
    expected_entry_revision: u64,
    completed_at_utc_ms: u64,
    payload_kind: protocol.PayloadKind,
) protocol.StageFeedbackInput {
    return .{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.StageFeedbackInput),
        .identity = identity_value,
        .expected_journal_revision = expected_journal_revision,
        .expected_entry_revision = expected_entry_revision,
        .completed_at_utc_ms = completed_at_utc_ms,
        .expected_phase = @intFromEnum(protocol.Phase.effect_observed),
        .feedback_valid_mask = protocol.FeedbackValid.completed_at |
            protocol.FeedbackValid.actual_process_grade,
        .feedback_flags = protocol.FeedbackFlags.process_owned |
            (if (payload_kind == .durable) protocol.FeedbackFlags.rollback_payload_persisted else 0),
        .feedback_status = @intFromEnum(protocol.FeedbackStatus.succeeded),
        .feedback_system_status = 300,
        .feedback_system_error = 0,
        .feedback_reserved = 0,
        .actual_process_grade = @intFromEnum(protocol.ProcessGrade.level_1),
        .actual_cpu_grade = 0,
        .actual_gpu_grade = 0,
        .actual_reserved = 0,
        .reserved = .{ 0, 0, 0, 0 },
    };
}

pub fn recoveryEvidence(
    identity_value: protocol.Identity,
    expected_journal_revision: u64,
    expected_entry_revision: u64,
    expected_phase: protocol.Phase,
    outcome: protocol.RecoveryOutcome,
    observed_at_utc_ms: u64,
) protocol.RecoveryEvidenceInput {
    const retries = outcome == .retryable_failure or outcome == .unavailable;
    const terminal = outcome == .authoritative_resync_completed;
    return .{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.RecoveryEvidenceInput),
        .identity = identity_value,
        .expected_journal_revision = expected_journal_revision,
        .expected_entry_revision = expected_entry_revision,
        .observed_at_utc_ms = observed_at_utc_ms,
        .retry_not_before_utc_ms = if (retries) observed_at_utc_ms + 500 else 0,
        .outcome = @intFromEnum(outcome),
        .expected_phase = @intFromEnum(expected_phase),
        .stable_system_status = 400 + @intFromEnum(outcome),
        .stable_system_error = if (retries) 5 else 0,
        .flags = 0,
        .evidence_reserved = 0,
        .authoritative_facts_generation = if (terminal) 0xfeed_beef else 0,
        .reserved = .{ 0, 0, 0 },
    };
}

pub fn ack(
    identity_value: protocol.Identity,
    expected_journal_revision: u64,
    expected_entry_revision: u64,
    expected_phase: protocol.Phase,
    result: protocol.AckResult,
    acknowledged_at_utc_ms: u64,
) protocol.AckInput {
    return .{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.AckInput),
        .identity = identity_value,
        .expected_journal_revision = expected_journal_revision,
        .expected_entry_revision = expected_entry_revision,
        .acknowledged_at_utc_ms = acknowledged_at_utc_ms,
        .retry_not_before_utc_ms = if (result == .uncertain) acknowledged_at_utc_ms + 500 else 0,
        .result = @intFromEnum(result),
        .expected_phase = @intFromEnum(expected_phase),
        .stable_system_status = 500 + @intFromEnum(result),
        .stable_system_error = if (result == .uncertain) 6 else 0,
        .flags = 0,
        .ack_reserved = 0,
        .reserved = .{ 0, 0, 0, 0 },
    };
}

pub fn emptySnapshot() protocol.SnapshotHeader {
    return .{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.SnapshotHeader),
        .journal_revision = 0,
        .journal_instance_low = 0,
        .journal_instance_high = 0,
        .entry_count = 0,
        .capacity = 0,
        .prepared_count = 0,
        .previous_effect_restored_count = 0,
        .effect_observed_count = 0,
        .feedback_pending_count = 0,
        .reconciliation_pending_count = 0,
        .authoritative_resync_pending_count = 0,
        .recovery_retry_pending_count = 0,
        .recovery_blocked_count = 0,
        .effect_invocation_uncertain_count = 0,
        .snapshot_reserved = 0,
        .reserved = .{ 0, 0, 0, 0, 0, 0 },
    };
}
