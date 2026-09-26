const std = @import("std");
const protocol = @import("protocol.zig");
const state = @import("state.zig");
const ResultCode = @import("../../common/result_codes.zig").ResultCode;

pub fn importPersisted(
    session: *state.Session,
    input: *const protocol.ImportInput,
    rows: []const protocol.PersistenceOperation,
) ResultCode {
    if (!session.rules_ready or session.import_ready) return .unavailable;
    if (!protocol.validImport(input, &session.config)) return .abi_mismatch;
    if (input.row_count != rows.len) return .invalid_argument;
    if (!session.operationAdvances(input.operation_epoch, input.command_monotonic_milliseconds)) {
        return .stale_frame;
    }

    @memset(session.staging_sources, .{});
    @memset(session.staging_observations, .{});
    @memset(session.staging_reports, .{});
    @memset(session.staging_trusts, .{});
    @memset(session.staging_buckets, .{});
    @memset(session.staging_source_index, state.no_index);
    @memset(session.staging_observation_index, state.no_index);
    @memset(session.staging_report_index, state.no_index);
    @memset(session.staging_trust_index, state.no_index);
    @memset(session.staging_bucket_index, state.no_index);

    var source_count: u32 = 0;
    var observation_count: u32 = 0;
    var report_count: u32 = 0;
    var trust_count: u32 = 0;
    var bucket_count: u32 = 0;
    var metadata_count: u32 = 0;
    var metadata_mutation_version: u64 = 0;
    var metadata_logical_utc_ms: i64 = 0;
    var maximum_mutation: u64 = 0;
    var maximum_source_record_handle: u64 = 0;
    var maximum_observation_handle: u64 = 0;
    var maximum_bucket_handle: u64 = 0;
    var maximum_report_handle: u64 = 0;
    var maximum_trust_handle: u64 = 0;
    var rolling_observation_count: u32 = 0;

    if (rows.len > session.dirty_references.len) return .buffer_too_small;
    for (rows, session.dirty_references[0..rows.len]) |row, *reference| {
        if (!validImportedRow(session, &row)) return .invalid_argument;
        reference.* = .{
            .mutation_version = row.identity_handle,
            .index = 0,
            .kind = @enumFromInt(row.operation_kind),
        };
    }
    const import_identities = session.dirty_references[0..rows.len];
    std.sort.heap(state.DirtyReference, import_identities, {}, lessImportIdentity);
    if (import_identities.len >= 2) {
        for (
            import_identities[1..],
            import_identities[0 .. import_identities.len - 1],
        ) |current, previous| {
            if (current.kind == previous.kind and
                current.mutation_version == previous.mutation_version)
            {
                return .invalid_argument;
            }
        }
    }

    for (rows) |row| {
        maximum_mutation = @max(maximum_mutation, row.mutation_version);
        const kind = @as(protocol.PersistenceKind, @enumFromInt(row.operation_kind));
        switch (kind) {
            .source => {
                if (source_count >= session.staging_sources.len) return .buffer_too_small;
                session.staging_sources[source_count] = sourceFromRow(&row);
                maximum_source_record_handle = @max(
                    maximum_source_record_handle,
                    row.identity_handle,
                );
                source_count += 1;
            },
            .observation => {
                if (observation_count >= session.staging_observations.len) return .buffer_too_small;
                const rule_index = session.findRule(row.rule_handle) orelse
                    return .invalid_argument;
                const rolling = (session.rules[rule_index].value.flags &
                    protocol.RuleFlags.rolling) != 0;
                if (rolling) {
                    if (rolling_observation_count >=
                        session.config.maximum_rolling_observation_count)
                    {
                        return .buffer_too_small;
                    }
                    rolling_observation_count += 1;
                }
                session.staging_observations[observation_count] =
                    observationFromRow(&row, rolling);
                maximum_observation_handle = @max(maximum_observation_handle, row.identity_handle);
                observation_count += 1;
            },
            .bucket => {
                if (bucket_count >= session.staging_buckets.len) return .buffer_too_small;
                session.staging_buckets[bucket_count] = bucketFromRow(&row);
                maximum_bucket_handle = @max(maximum_bucket_handle, row.identity_handle);
                bucket_count += 1;
            },
            .report => {
                if (report_count >= session.staging_reports.len) return .buffer_too_small;
                session.staging_reports[report_count] = reportFromRow(&row);
                maximum_report_handle = @max(maximum_report_handle, row.identity_handle);
                report_count += 1;
            },
            .trust => {
                if (trust_count >= session.staging_trusts.len) return .buffer_too_small;
                session.staging_trusts[trust_count] = trustFromRow(&row);
                maximum_trust_handle = @max(maximum_trust_handle, row.identity_handle);
                trust_count += 1;
            },
            .metadata => {
                if (metadata_count != 0) return .invalid_argument;
                metadata_count = 1;
                metadata_mutation_version = row.mutation_version;
                metadata_logical_utc_ms = row.checkpoint_logical_utc_milliseconds;
            },
        }
    }
    if (rows.len != 0 and metadata_count != 1) return .invalid_argument;

    if (!state.rebuildSourceIndex(
        session.staging_sources,
        session.staging_source_index,
    ) or !state.rebuildObservationIndex(
        session.staging_observations,
        session.staging_observation_index,
    ) or !state.rebuildReportIndex(
        session.staging_reports,
        session.staging_report_index,
    ) or !state.rebuildTrustIndex(
        session.staging_trusts,
        session.staging_trust_index,
    ) or !state.rebuildBucketIndex(
        session.staging_buckets,
        session.staging_bucket_index,
    )) return .invalid_argument;

    for (session.staging_observations[0..observation_count]) |observation| {
        if (state.findSourceIn(
            session.staging_sources,
            session.staging_source_index,
            observation.source_handle,
            observation.coverage_scope_handle,
        ) == null) return .invalid_argument;
    }
    for (session.staging_buckets[0..bucket_count]) |bucket| {
        const rule_index = session.findRule(bucket.rule_handle) orelse
            return .invalid_argument;
        const coverage_scope = session.rules[rule_index].value.coverage_scope_handle;
        if (state.findSourceIn(
            session.staging_sources,
            session.staging_source_index,
            bucket.source_handle,
            coverage_scope,
        ) == null) return .invalid_argument;
        const observation_index = state.findObservationIn(
            session.staging_observations,
            session.staging_observation_index,
            bucket.source_handle,
            coverage_scope,
            bucket.rule_handle,
            bucket.target_handle,
        ) orelse return .invalid_argument;
        const observation = session.staging_observations[observation_index];
        if (observation.rule_generation != bucket.rule_generation) return .invalid_argument;
    }

    var imported_metadata_logical_utc_ms = if (metadata_count == 0)
        input.command_utc_milliseconds
    else
        metadata_logical_utc_ms;
    var imported_metadata_mutation_version = metadata_mutation_version;
    var imported_metadata_persisted_mutation_version = metadata_mutation_version;
    var imported_next_mutation_version = nextIdentity(maximum_mutation);
    if (metadata_count == 0 or
        input.command_utc_milliseconds > metadata_logical_utc_ms)
    {
        if (imported_next_mutation_version == 0 or
            imported_next_mutation_version == std.math.maxInt(u64))
        {
            return .out_of_memory;
        }
        imported_metadata_logical_utc_ms = input.command_utc_milliseconds;
        imported_metadata_mutation_version = imported_next_mutation_version;
        imported_next_mutation_version += 1;
        if (metadata_count == 0) {
            imported_metadata_persisted_mutation_version = 0;
        }
    }

    std.mem.swap([]state.SourceSlot, &session.sources, &session.staging_sources);
    std.mem.swap([]state.ObservationSlot, &session.observations, &session.staging_observations);
    std.mem.swap([]state.ReportSlot, &session.reports, &session.staging_reports);
    std.mem.swap([]state.TrustSlot, &session.trusts, &session.staging_trusts);
    std.mem.swap([]state.BucketSlot, &session.buckets, &session.staging_buckets);
    std.mem.swap([]u32, &session.source_index, &session.staging_source_index);
    std.mem.swap([]u32, &session.observation_index, &session.staging_observation_index);
    std.mem.swap([]u32, &session.report_index, &session.staging_report_index);
    std.mem.swap([]u32, &session.trust_index, &session.staging_trust_index);
    std.mem.swap([]u32, &session.bucket_index, &session.staging_bucket_index);

    session.import_generation = input.import_generation;
    session.next_mutation_version = imported_next_mutation_version;
    session.next_source_record_handle = nextIdentity(maximum_source_record_handle);
    session.next_observation_handle = nextIdentity(maximum_observation_handle);
    session.next_bucket_handle = nextIdentity(maximum_bucket_handle);
    session.next_report_handle = nextIdentity(maximum_report_handle);
    session.next_trust_handle = nextIdentity(maximum_trust_handle);
    session.next_source_slot = 0;
    session.next_observation_slot = 0;
    session.next_report_slot = 0;
    session.next_trust_slot = 0;
    session.next_bucket_slot = 0;
    session.rolling_observation_count = rolling_observation_count;
    session.metadata_logical_utc_ms = imported_metadata_logical_utc_ms;
    session.metadata_mutation_version = imported_metadata_mutation_version;
    session.metadata_persisted_mutation_version =
        imported_metadata_persisted_mutation_version;
    session.import_ready = true;
    session.logical_utc_ms = session.metadata_logical_utc_ms;
    session.commitOperation(
        input.operation_epoch,
        input.command_monotonic_milliseconds,
        input.command_utc_milliseconds,
    );
    return .ok;
}

pub fn collectDirty(
    session: *state.Session,
    logical_utc_ms: i64,
) error{OutOfMemory}![]state.DirtyReference {
    var count: usize = 0;
    appendDirty(session.sources, .source, session.dirty_references, &count);
    appendDirty(
        session.observations,
        .observation,
        session.dirty_references,
        &count,
    );
    appendDirty(session.buckets, .bucket, session.dirty_references, &count);
    appendDirty(session.reports, .report, session.dirty_references, &count);
    appendDirty(session.trusts, .trust, session.dirty_references, &count);

    const semantic_state_is_dirty = count != 0;
    if ((semantic_state_is_dirty or metadataCheckpointDue(session, logical_utc_ms)) and
        (session.metadata_mutation_version == 0 or
            logical_utc_ms > session.metadata_logical_utc_ms))
    {
        session.metadata_mutation_version = session.allocateMutation() orelse
            return error.OutOfMemory;
        session.metadata_logical_utc_ms = logical_utc_ms;
    }
    if (session.metadata_mutation_version >
        session.metadata_persisted_mutation_version)
    {
        session.dirty_references[count] = .{
            .mutation_version = session.metadata_mutation_version,
            .index = 0,
            .kind = .metadata,
        };
        count += 1;
    }

    const dirty = session.dirty_references[0..count];
    std.sort.heap(state.DirtyReference, dirty, {}, lessDirty);
    return dirty;
}

fn metadataCheckpointDue(session: *const state.Session, logical_utc_ms: i64) bool {
    if (session.metadata_mutation_version == 0) return true;
    const interval: i64 = @intCast(
        session.config.metadata_checkpoint_interval_milliseconds,
    );
    const deadline = std.math.add(
        i64,
        session.metadata_logical_utc_ms,
        interval,
    ) catch std.math.maxInt(i64);
    return logical_utc_ms >= deadline;
}

pub fn operationFor(
    session: *const state.Session,
    reference: state.DirtyReference,
    plan_epoch: u64,
) protocol.PersistenceOperation {
    return switch (reference.kind) {
        .none => unreachable,
        .source => operationForSource(session, reference.index, plan_epoch),
        .observation => operationForObservation(
            session,
            reference.index,
            plan_epoch,
        ),
        .bucket => operationForBucket(session, reference.index, plan_epoch),
        .report => operationForReport(session, reference.index, plan_epoch),
        .trust => operationForTrust(session, reference.index, plan_epoch),
        .metadata => operationForMetadata(session, plan_epoch),
    };
}

pub fn applyFeedback(
    session: *state.Session,
    input: *const protocol.PersistenceFeedbackInput,
    feedbacks: []const protocol.PersistenceFeedback,
) ResultCode {
    if (!session.import_ready) return .unavailable;
    if (!protocol.validFeedbackInput(input, &session.config)) return .abi_mismatch;
    if (input.feedback_count != feedbacks.len) return .invalid_argument;
    if (feedbacks.len == 0 or feedbacks.len != session.planned_persistence_count) {
        return .invalid_argument;
    }
    if (!session.operationAdvances(input.operation_epoch, input.command_monotonic_milliseconds) or
        input.feedback_epoch <= session.last_feedback_epoch) return .stale_frame;

    session.beginFeedbackValidation();
    if (!protocol.validFeedbackStatus(feedbacks[0].status)) return .invalid_argument;
    const batch_status: protocol.FeedbackStatus = @enumFromInt(feedbacks[0].status);
    for (feedbacks) |feedback| {
        if (!protocol.validFeedbackStatus(feedback.status)) return .invalid_argument;
        if (@as(protocol.FeedbackStatus, @enumFromInt(feedback.status)) != batch_status) {
            return .invalid_argument;
        }
        const planned_index = validFeedbackRow(session, &feedback) orelse
            return .invalid_argument;
        if (session.planned_feedback_marks[planned_index] ==
            session.planned_feedback_epoch) return .invalid_argument;
        session.planned_feedback_marks[planned_index] = session.planned_feedback_epoch;
        if (@as(protocol.FeedbackStatus, @enumFromInt(feedback.status)) == .persisted and
            @as(protocol.PersistenceKind, @enumFromInt(feedback.operation_kind)) == .trust)
        {
            const trust_slot = session.trusts[feedback.slot_index];
            session.trust_feedback_outcomes[feedback.slot_index] =
                if (trust_slot.delete_pending) 2 else 1;
        }
    }
    const suppression_change_count = suppressionChangesAfterFeedback(session);
    if (suppression_change_count > std.math.maxInt(u64) - session.next_mutation_version) {
        return .out_of_memory;
    }

    const logical_utc = @max(session.logical_utc_ms, input.command_utc_milliseconds);
    var persisted_trust_change = false;
    for (feedbacks) |feedback| {
        if (batch_status == .failed) continue;
        if (@as(protocol.PersistenceKind, @enumFromInt(feedback.operation_kind)) == .trust) {
            persisted_trust_change = true;
        }
        commitFeedbackRow(session, &feedback);
    }
    consumePlannedFeedback(session);
    if (persisted_trust_change) {
        const suppression = syncTrustSuppression(session, logical_utc);
        if (suppression != .ok) unreachable;
    }
    session.last_feedback_epoch = input.feedback_epoch;
    session.commitOperation(
        input.operation_epoch,
        input.command_monotonic_milliseconds,
        input.command_utc_milliseconds,
    );
    return .ok;
}

fn suppressionChangesAfterFeedback(
    session: *const state.Session,
) u64 {
    var count: u64 = 0;
    for (session.reports) |report| {
        if (!report.occupied or report.delete_pending) continue;
        const trusted = reportTrustCommittedAfterFeedback(
            session,
            report.target_handle,
            report.family_handle,
        );
        if (report.trusted_suppressed != trusted) count += 1;
    }
    return count;
}

fn reportTrustCommittedAfterFeedback(
    session: *const state.Session,
    target_handle: u64,
    family_handle: u64,
) bool {
    if (trustCommittedAfterFeedback(session, target_handle, family_handle)) return true;
    return trustCommittedAfterFeedback(session, target_handle, protocol.TrustFamily.all);
}

fn trustCommittedAfterFeedback(
    session: *const state.Session,
    target_handle: u64,
    family_handle: u64,
) bool {
    const slot_index = session.findTrust(target_handle, family_handle) orelse return false;
    const slot = session.trusts[slot_index];
    return switch (session.trust_feedback_outcomes[slot_index]) {
        1 => true,
        2 => false,
        else => slot.committed,
    };
}

fn validImportedRow(session: *const state.Session, row: *const protocol.PersistenceOperation) bool {
    if (!protocol.validPersistenceRow(row) or
        (row.flags & protocol.PersistenceFlags.delete) != 0 or
        row.slot_generation == 0) return false;
    const kind = @as(protocol.PersistenceKind, @enumFromInt(row.operation_kind));
    return switch (kind) {
        .source => validImportedSource(row),
        .observation => validImportedObservation(session, row),
        .bucket => validImportedBucket(session, row),
        .report => validImportedReport(session, row),
        .trust => validImportedTrust(row),
        .metadata => validImportedMetadata(row),
    };
}

fn validImportedSource(row: *const protocol.PersistenceOperation) bool {
    return row.flags == 0 and
        row.source_handle != 0 and row.source_generation != 0 and
        row.source_snapshot_epoch != 0 and
        protocol.validSourceStatus(row.source_status) and
        row.source_reserved_u32 == 0 and
        row.coverage_scope_handle != 0 and
        row.rule_handle == 0 and row.rule_generation == 0 and
        row.target_handle == 0 and row.family_handle == 0 and
        row.report_handle == 0 and row.report_type_handle == 0 and
        row.resource_kind_handle == 0 and row.payload_handle == 0 and
        row.evidence_payload_handle == 0 and
        row.first_observed_at_utc_milliseconds == 0 and
        row.last_observed_at_utc_milliseconds >= 0 and
        row.source_last_complete_at_utc_milliseconds >= 0 and
        row.source_last_complete_at_utc_milliseconds <=
            row.last_observed_at_utc_milliseconds and
        row.bucket_start_utc_milliseconds == 0 and
        row.current_value == 0 and row.secondary_current_value == 0 and
        row.value_sum == 0 and
        row.peak_value == 0 and row.window_delta_value == 0 and
        row.sample_duration_milliseconds == 0 and row.sample_count == 0 and
        row.active_sample_count == 0 and row.event_count == 0 and
        row.request_event_count == 0 and row.connection_event_count == 0 and
        row.sample_hit_count == 0 and row.consecutive_hits == 0 and
        row.consecutive_misses == 0 and row.priority == 0 and
        row.severity == 0 and row.fact_sequence == 0 and
        row.report_observed_at_utc_milliseconds == 0 and
        row.checkpoint_schema_version == 0 and
        row.checkpoint_reserved_u32 == 0 and
        row.checkpoint_logical_utc_milliseconds == 0;
}

fn validImportedObservation(
    session: *const state.Session,
    row: *const protocol.PersistenceOperation,
) bool {
    if (!sourceFieldsZero(row) or
        (row.flags & ~(protocol.PersistenceFlags.active |
            protocol.PersistenceFlags.current_valid |
            protocol.PersistenceFlags.secondary_current_valid)) != 0 or
        row.source_handle == 0 or row.coverage_scope_handle == 0 or
        row.rule_handle == 0 or row.rule_generation == 0 or row.target_handle == 0 or
        row.family_handle != 0 or row.report_handle != 0 or
        row.report_type_handle != 0 or row.resource_kind_handle != 0 or
        row.payload_handle != 0 or row.bucket_start_utc_milliseconds != 0 or
        row.window_delta_value != 0 or row.event_count != 0 or
        row.request_event_count != 0 or row.connection_event_count != 0 or
        row.sample_hit_count != 0 or row.priority != 0 or row.severity != 0 or
        row.fact_sequence == 0 or
        row.report_observed_at_utc_milliseconds != 0 or
        row.first_observed_at_utc_milliseconds < 0 or
        row.last_observed_at_utc_milliseconds < row.first_observed_at_utc_milliseconds or
        row.sample_count == 0 or row.sample_duration_milliseconds == 0 or
        row.active_sample_count != row.consecutive_hits or
        (row.consecutive_hits != 0 and row.consecutive_misses != 0) or
        ((row.flags & protocol.PersistenceFlags.current_valid) == 0 and
            row.current_value != 0) or
        ((row.flags & protocol.PersistenceFlags.secondary_current_valid) == 0 and
            row.secondary_current_value != 0)) return false;
    const rule_index = session.findRule(row.rule_handle) orelse return false;
    const rule = session.rules[rule_index].value;
    // Complete snapshots can keep counting absence after a report is cleared.
    return ruleMatches(session, row) and
        row.active_sample_count <= row.sample_count and
        row.consecutive_hits <= row.sample_count and
        ((row.flags & protocol.PersistenceFlags.active) == 0 or
            row.consecutive_misses < rule.required_consecutive_misses);
}

fn validImportedBucket(
    session: *const state.Session,
    row: *const protocol.PersistenceOperation,
) bool {
    if (!sourceFieldsZero(row) or
        row.flags != 0 or row.source_handle == 0 or row.coverage_scope_handle != 0 or
        row.rule_handle == 0 or row.rule_generation == 0 or row.target_handle == 0 or
        row.family_handle != 0 or row.report_handle != 0 or
        row.report_type_handle != 0 or row.resource_kind_handle != 0 or
        row.payload_handle != 0 or row.evidence_payload_handle != 0 or
        row.first_observed_at_utc_milliseconds != 0 or
        row.last_observed_at_utc_milliseconds != 0 or
        row.current_value != 0 or row.secondary_current_value != 0 or
        row.value_sum != 0 or
        row.sample_count != 0 or row.active_sample_count != 0 or
        row.consecutive_hits != 0 or row.consecutive_misses != 0 or
        row.priority != 0 or row.severity != 0 or
        row.fact_sequence != 0 or
        row.report_observed_at_utc_milliseconds != 0 or
        row.bucket_start_utc_milliseconds < 0 or
        row.sample_duration_milliseconds == 0 or
        @rem(
            row.bucket_start_utc_milliseconds,
            @as(i64, @intCast(session.config.bucket_width_milliseconds)),
        ) != 0) return false;
    return ruleMatches(session, row);
}

fn validImportedReport(
    session: *const state.Session,
    row: *const protocol.PersistenceOperation,
) bool {
    if (!sourceFieldsZero(row) or
        (row.flags & ~(protocol.PersistenceFlags.active |
            protocol.PersistenceFlags.trusted_suppressed)) != 0 or
        row.report_handle != row.identity_handle or row.source_handle == 0 or
        row.coverage_scope_handle == 0 or row.rule_handle == 0 or
        row.rule_generation == 0 or row.target_handle == 0 or row.family_handle == 0 or
        row.report_type_handle == 0 or row.resource_kind_handle == 0 or
        row.bucket_start_utc_milliseconds != 0 or row.current_value != 0 or
        row.secondary_current_value != 0 or
        row.value_sum != 0 or row.peak_value != 0 or row.window_delta_value != 0 or
        row.sample_duration_milliseconds != 0 or row.sample_count != 0 or
        row.active_sample_count != 0 or row.event_count != 0 or
        row.request_event_count != 0 or row.connection_event_count != 0 or
        row.sample_hit_count != 0 or row.consecutive_hits != 0 or
        row.consecutive_misses != 0 or
        row.fact_sequence != 0 or
        row.first_observed_at_utc_milliseconds < 0 or
        row.report_observed_at_utc_milliseconds <
            row.first_observed_at_utc_milliseconds or
        row.last_observed_at_utc_milliseconds <
            row.report_observed_at_utc_milliseconds) return false;
    return ruleMatches(session, row);
}

fn validImportedTrust(row: *const protocol.PersistenceOperation) bool {
    return sourceFieldsZero(row) and
        row.flags == protocol.PersistenceFlags.active and
        row.source_handle == 0 and row.coverage_scope_handle == 0 and
        row.rule_handle == 0 and row.rule_generation == 0 and
        row.target_handle != 0 and row.family_handle != 0 and
        row.report_handle == 0 and row.report_type_handle == 0 and
        row.resource_kind_handle == 0 and row.evidence_payload_handle == 0 and
        row.first_observed_at_utc_milliseconds >= 0 and
        row.last_observed_at_utc_milliseconds == row.first_observed_at_utc_milliseconds and
        row.bucket_start_utc_milliseconds == 0 and
        row.current_value == 0 and row.secondary_current_value == 0 and
        row.value_sum == 0 and row.peak_value == 0 and
        row.window_delta_value == 0 and row.sample_duration_milliseconds == 0 and
        row.sample_count == 0 and row.active_sample_count == 0 and
        row.event_count == 0 and row.request_event_count == 0 and
        row.connection_event_count == 0 and row.sample_hit_count == 0 and
        row.consecutive_hits == 0 and row.consecutive_misses == 0 and
        row.priority == 0 and row.severity == 0 and
        row.fact_sequence == 0 and
        row.report_observed_at_utc_milliseconds == 0;
}

fn sourceFieldsZero(row: *const protocol.PersistenceOperation) bool {
    return row.source_generation == 0 and
        row.source_snapshot_epoch == 0 and
        row.source_last_complete_at_utc_milliseconds == 0 and
        row.source_status == 0 and
        row.source_reserved_u32 == 0 and
        row.checkpoint_schema_version == 0 and
        row.checkpoint_reserved_u32 == 0 and
        row.checkpoint_logical_utc_milliseconds == 0;
}

fn validImportedMetadata(row: *const protocol.PersistenceOperation) bool {
    return row.flags == 0 and row.identity_handle == 1 and
        row.slot_generation == 1 and
        row.source_handle == 0 and row.source_generation == 0 and
        row.source_snapshot_epoch == 0 and
        row.source_last_complete_at_utc_milliseconds == 0 and
        row.source_status == 0 and row.source_reserved_u32 == 0 and
        row.coverage_scope_handle == 0 and row.rule_handle == 0 and
        row.rule_generation == 0 and row.target_handle == 0 and
        row.family_handle == 0 and row.report_handle == 0 and
        row.report_type_handle == 0 and row.resource_kind_handle == 0 and
        row.payload_handle == 0 and row.evidence_payload_handle == 0 and
        row.first_observed_at_utc_milliseconds == 0 and
        row.last_observed_at_utc_milliseconds == 0 and
        row.bucket_start_utc_milliseconds == 0 and
        row.current_value == 0 and row.secondary_current_value == 0 and
        row.value_sum == 0 and
        row.peak_value == 0 and row.window_delta_value == 0 and
        row.sample_duration_milliseconds == 0 and row.sample_count == 0 and
        row.active_sample_count == 0 and row.event_count == 0 and
        row.request_event_count == 0 and row.connection_event_count == 0 and
        row.sample_hit_count == 0 and row.consecutive_hits == 0 and
        row.consecutive_misses == 0 and row.priority == 0 and
        row.severity == 0 and row.slot_index == 0 and
        row.fact_sequence == 0 and
        row.report_observed_at_utc_milliseconds == 0 and
        row.checkpoint_schema_version == protocol.abi_version and
        row.checkpoint_reserved_u32 == 0 and
        row.checkpoint_logical_utc_milliseconds >= 0;
}

fn ruleMatches(session: *const state.Session, row: *const protocol.PersistenceOperation) bool {
    const rule_index = session.findRule(row.rule_handle) orelse return false;
    const rule = session.rules[rule_index].value;
    return rule.rule_generation == row.rule_generation and
        (row.source_handle == 0 or rule.source_handle == row.source_handle) and
        (row.coverage_scope_handle == 0 or
            rule.coverage_scope_handle == row.coverage_scope_handle) and
        (row.family_handle == 0 or rule.family_handle == row.family_handle);
}

fn sourceFromRow(row: *const protocol.PersistenceOperation) state.SourceSlot {
    return .{
        .occupied = true,
        .status = @enumFromInt(row.source_status),
        .slot_generation = row.slot_generation,
        .record_handle = row.identity_handle,
        .source_handle = row.source_handle,
        .source_generation = row.source_generation,
        .coverage_scope_handle = row.coverage_scope_handle,
        .last_snapshot_epoch = row.source_snapshot_epoch,
        .last_observed_at_utc_ms = row.last_observed_at_utc_milliseconds,
        .last_complete_at_utc_ms = row.source_last_complete_at_utc_milliseconds,
        .mutation_version = row.mutation_version,
        .persisted_mutation_version = row.mutation_version,
    };
}

fn observationFromRow(
    row: *const protocol.PersistenceOperation,
    rolling: bool,
) state.ObservationSlot {
    return .{
        .occupied = true,
        .rolling = rolling,
        .slot_generation = row.slot_generation,
        .active = (row.flags & protocol.PersistenceFlags.active) != 0,
        .current_valid = (row.flags & protocol.PersistenceFlags.current_valid) != 0,
        .secondary_current_valid = (row.flags & protocol.PersistenceFlags.secondary_current_valid) != 0,
        .observation_handle = row.identity_handle,
        .source_handle = row.source_handle,
        .coverage_scope_handle = row.coverage_scope_handle,
        .rule_handle = row.rule_handle,
        .rule_generation = row.rule_generation,
        .target_handle = row.target_handle,
        .evidence_payload_handle = row.evidence_payload_handle,
        .first_observed_at_utc_ms = row.first_observed_at_utc_milliseconds,
        .last_observed_at_utc_ms = row.last_observed_at_utc_milliseconds,
        .current_value = row.current_value,
        .secondary_current_value = row.secondary_current_value,
        .value_sum = row.value_sum,
        .peak_value = row.peak_value,
        .sample_duration_milliseconds = row.sample_duration_milliseconds,
        .sample_count = row.sample_count,
        .active_sample_count = row.active_sample_count,
        .consecutive_hits = row.consecutive_hits,
        .consecutive_misses = row.consecutive_misses,
        .last_fact_sequence = row.fact_sequence,
        .mutation_version = row.mutation_version,
        .persisted_mutation_version = row.mutation_version,
    };
}

fn bucketFromRow(row: *const protocol.PersistenceOperation) state.BucketSlot {
    return .{
        .occupied = true,
        .slot_generation = row.slot_generation,
        .bucket_handle = row.identity_handle,
        .source_handle = row.source_handle,
        .rule_handle = row.rule_handle,
        .rule_generation = row.rule_generation,
        .target_handle = row.target_handle,
        .bucket_start_utc_ms = row.bucket_start_utc_milliseconds,
        .delta_value = row.window_delta_value,
        .peak_value = row.peak_value,
        .sample_duration_milliseconds = row.sample_duration_milliseconds,
        .event_count = row.event_count,
        .request_event_count = row.request_event_count,
        .connection_event_count = row.connection_event_count,
        .sample_hit_count = row.sample_hit_count,
        .mutation_version = row.mutation_version,
        .persisted_mutation_version = row.mutation_version,
    };
}

fn reportFromRow(row: *const protocol.PersistenceOperation) state.ReportSlot {
    return .{
        .occupied = true,
        .slot_generation = row.slot_generation,
        .active = (row.flags & protocol.PersistenceFlags.active) != 0,
        .trusted_suppressed = (row.flags & protocol.PersistenceFlags.trusted_suppressed) != 0,
        .report_handle = row.identity_handle,
        .target_handle = row.target_handle,
        .family_handle = row.family_handle,
        .rule_handle = row.rule_handle,
        .report_type_handle = row.report_type_handle,
        .resource_kind_handle = row.resource_kind_handle,
        .payload_handle = row.payload_handle,
        .evidence_payload_handle = row.evidence_payload_handle,
        .created_at_utc_ms = row.first_observed_at_utc_milliseconds,
        .updated_at_utc_ms = row.last_observed_at_utc_milliseconds,
        .last_observed_at_utc_ms = row.report_observed_at_utc_milliseconds,
        .priority = row.priority,
        .severity = row.severity,
        .mutation_version = row.mutation_version,
        .persisted_mutation_version = row.mutation_version,
    };
}

fn trustFromRow(row: *const protocol.PersistenceOperation) state.TrustSlot {
    return .{
        .occupied = true,
        .slot_generation = row.slot_generation,
        .committed = true,
        .trust_handle = row.identity_handle,
        .target_handle = row.target_handle,
        .family_handle = row.family_handle,
        .payload_handle = row.payload_handle,
        .trusted_at_utc_ms = row.first_observed_at_utc_milliseconds,
        .mutation_version = row.mutation_version,
        .persisted_mutation_version = row.mutation_version,
    };
}

fn operationForSource(
    session: *const state.Session,
    slot_index: u32,
    plan_epoch: u64,
) protocol.PersistenceOperation {
    const slot = session.sources[slot_index];
    var output = baseOperation(
        session,
        plan_epoch,
        slot.mutation_version,
        slot.record_handle,
        slot.slot_generation,
        .source,
        slot_index,
        0,
    );
    output.source_handle = slot.source_handle;
    output.source_generation = slot.source_generation;
    output.source_snapshot_epoch = slot.last_snapshot_epoch;
    output.source_last_complete_at_utc_milliseconds = slot.last_complete_at_utc_ms;
    output.source_status = @intFromEnum(slot.status);
    output.coverage_scope_handle = slot.coverage_scope_handle;
    output.last_observed_at_utc_milliseconds = slot.last_observed_at_utc_ms;
    return output;
}

fn operationForMetadata(
    session: *const state.Session,
    plan_epoch: u64,
) protocol.PersistenceOperation {
    var output = baseOperation(
        session,
        plan_epoch,
        session.metadata_mutation_version,
        1,
        1,
        .metadata,
        0,
        0,
    );
    output.checkpoint_schema_version = protocol.abi_version;
    output.checkpoint_logical_utc_milliseconds = session.metadata_logical_utc_ms;
    return output;
}

fn operationForObservation(
    session: *const state.Session,
    slot_index: u32,
    plan_epoch: u64,
) protocol.PersistenceOperation {
    const slot = session.observations[slot_index];
    var flags: u32 = 0;
    if (slot.delete_pending) flags |= protocol.PersistenceFlags.delete;
    if (slot.active) flags |= protocol.PersistenceFlags.active;
    if (slot.current_valid) flags |= protocol.PersistenceFlags.current_valid;
    if (slot.secondary_current_valid) {
        flags |= protocol.PersistenceFlags.secondary_current_valid;
    }
    return withObservation(
        baseOperation(
            session,
            plan_epoch,
            slot.mutation_version,
            slot.observation_handle,
            slot.slot_generation,
            .observation,
            slot_index,
            flags,
        ),
        slot,
    );
}

fn operationForBucket(
    session: *const state.Session,
    slot_index: u32,
    plan_epoch: u64,
) protocol.PersistenceOperation {
    const slot = session.buckets[slot_index];
    var output = baseOperation(
        session,
        plan_epoch,
        slot.mutation_version,
        slot.bucket_handle,
        slot.slot_generation,
        .bucket,
        slot_index,
        if (slot.delete_pending) protocol.PersistenceFlags.delete else 0,
    );
    output.source_handle = slot.source_handle;
    output.rule_handle = slot.rule_handle;
    output.rule_generation = slot.rule_generation;
    output.target_handle = slot.target_handle;
    output.bucket_start_utc_milliseconds = slot.bucket_start_utc_ms;
    output.peak_value = slot.peak_value;
    output.window_delta_value = slot.delta_value;
    output.sample_duration_milliseconds = slot.sample_duration_milliseconds;
    output.event_count = slot.event_count;
    output.request_event_count = slot.request_event_count;
    output.connection_event_count = slot.connection_event_count;
    output.sample_hit_count = slot.sample_hit_count;
    return output;
}

fn operationForReport(
    session: *const state.Session,
    slot_index: u32,
    plan_epoch: u64,
) protocol.PersistenceOperation {
    const slot = session.reports[slot_index];
    var flags: u32 = 0;
    if (slot.delete_pending) flags |= protocol.PersistenceFlags.delete;
    if (slot.active) flags |= protocol.PersistenceFlags.active;
    if (slot.trusted_suppressed) flags |= protocol.PersistenceFlags.trusted_suppressed;
    var output = baseOperation(
        session,
        plan_epoch,
        slot.mutation_version,
        slot.report_handle,
        slot.slot_generation,
        .report,
        slot_index,
        flags,
    );
    output.target_handle = slot.target_handle;
    output.family_handle = slot.family_handle;
    output.report_handle = slot.report_handle;
    output.rule_handle = slot.rule_handle;
    if (session.findRule(slot.rule_handle)) |rule_index| {
        const rule = session.rules[rule_index].value;
        output.source_handle = rule.source_handle;
        output.coverage_scope_handle = rule.coverage_scope_handle;
        output.rule_generation = rule.rule_generation;
    }
    output.report_type_handle = slot.report_type_handle;
    output.resource_kind_handle = slot.resource_kind_handle;
    output.payload_handle = slot.payload_handle;
    output.evidence_payload_handle = slot.evidence_payload_handle;
    output.first_observed_at_utc_milliseconds = slot.created_at_utc_ms;
    output.last_observed_at_utc_milliseconds = slot.updated_at_utc_ms;
    output.report_observed_at_utc_milliseconds = slot.last_observed_at_utc_ms;
    output.priority = slot.priority;
    output.severity = slot.severity;
    return output;
}

fn operationForTrust(
    session: *const state.Session,
    slot_index: u32,
    plan_epoch: u64,
) protocol.PersistenceOperation {
    const slot = session.trusts[slot_index];
    var output = baseOperation(
        session,
        plan_epoch,
        slot.mutation_version,
        slot.trust_handle,
        slot.slot_generation,
        .trust,
        slot_index,
        if (slot.delete_pending) protocol.PersistenceFlags.delete else protocol.PersistenceFlags.active,
    );
    output.target_handle = slot.target_handle;
    output.family_handle = slot.family_handle;
    output.payload_handle = slot.payload_handle;
    output.first_observed_at_utc_milliseconds = slot.trusted_at_utc_ms;
    output.last_observed_at_utc_milliseconds = slot.trusted_at_utc_ms;
    return output;
}

fn baseOperation(
    session: *const state.Session,
    plan_epoch: u64,
    mutation_version: u64,
    identity_handle: u64,
    slot_generation: u64,
    kind: protocol.PersistenceKind,
    slot_index: u32,
    flags: u32,
) protocol.PersistenceOperation {
    var output = std.mem.zeroes(protocol.PersistenceOperation);
    output.struct_size = @sizeOf(protocol.PersistenceOperation);
    output.flags = flags;
    output.session_instance_low = session.config.session_instance_low;
    output.session_instance_high = session.config.session_instance_high;
    output.plan_epoch = plan_epoch;
    output.mutation_version = mutation_version;
    output.identity_handle = identity_handle;
    output.slot_generation = slot_generation;
    output.operation_kind = @intFromEnum(kind);
    output.slot_index = slot_index;
    return output;
}

fn withObservation(
    base: protocol.PersistenceOperation,
    slot: state.ObservationSlot,
) protocol.PersistenceOperation {
    var output = base;
    output.source_handle = slot.source_handle;
    output.coverage_scope_handle = slot.coverage_scope_handle;
    output.rule_handle = slot.rule_handle;
    output.rule_generation = slot.rule_generation;
    output.target_handle = slot.target_handle;
    output.evidence_payload_handle = slot.evidence_payload_handle;
    output.first_observed_at_utc_milliseconds = slot.first_observed_at_utc_ms;
    output.last_observed_at_utc_milliseconds = slot.last_observed_at_utc_ms;
    output.current_value = slot.current_value;
    output.secondary_current_value = slot.secondary_current_value;
    output.value_sum = slot.value_sum;
    output.peak_value = slot.peak_value;
    output.sample_duration_milliseconds = slot.sample_duration_milliseconds;
    output.sample_count = slot.sample_count;
    output.active_sample_count = slot.active_sample_count;
    output.consecutive_hits = slot.consecutive_hits;
    output.consecutive_misses = slot.consecutive_misses;
    output.fact_sequence = slot.last_fact_sequence;
    return output;
}

fn validFeedbackRow(
    session: *const state.Session,
    row: *const protocol.PersistenceFeedback,
) ?u32 {
    if (row.struct_size != @sizeOf(protocol.PersistenceFeedback) or
        row.session_instance_low != session.config.session_instance_low or
        row.session_instance_high != session.config.session_instance_high or
        row.plan_epoch != session.last_plan_epoch or
        row.mutation_version == 0 or row.identity_handle == 0 or
        row.slot_generation == 0 or row.flags != 0 or row.reserved_u32 != 0 or
        row.reserved[0] != 0) return null;
    if (!protocol.validFeedbackStatus(row.status) or
        !protocol.validPersistenceKind(row.operation_kind)) return null;
    const planned_index = session.findPlannedPersistence(.{
        .plan_epoch = row.plan_epoch,
        .mutation_version = row.mutation_version,
        .identity_handle = row.identity_handle,
        .slot_generation = row.slot_generation,
        .kind = row.operation_kind,
        .slot_index = row.slot_index,
    }) orelse return null;
    const kind: protocol.PersistenceKind = @enumFromInt(row.operation_kind);
    const matches = switch (kind) {
        .source => matchesSourceFeedback(session, row),
        .observation => matchesObservationFeedback(session, row),
        .bucket => matchesBucketFeedback(session, row),
        .report => matchesReportFeedback(session, row),
        .trust => matchesTrustFeedback(session, row),
        .metadata => matchesMetadataFeedback(session, row),
    };
    return if (matches) planned_index else null;
}

fn consumePlannedFeedback(session: *state.Session) void {
    var write_index: usize = 0;
    for (
        session.planned_persistence[0..session.planned_persistence_count],
        0..,
    ) |planned, planned_index| {
        if (session.planned_feedback_marks[planned_index] ==
            session.planned_feedback_epoch) continue;
        session.planned_persistence[write_index] = planned;
        write_index += 1;
    }
    session.planned_persistence_count = @intCast(write_index);
    if (write_index == 0) {
        session.planned_output = std.mem.zeroes(protocol.PlanOutput);
    }
    @memset(session.planned_persistence_index, state.no_index);
    for (0..write_index) |planned_index| {
        if (!session.insertPlannedPersistenceIndex(@intCast(planned_index))) unreachable;
    }
}

fn commitFeedbackRow(session: *state.Session, row: *const protocol.PersistenceFeedback) void {
    const kind = @as(protocol.PersistenceKind, @enumFromInt(row.operation_kind));
    switch (kind) {
        .source => {
            session.sources[row.slot_index].persisted_mutation_version =
                row.mutation_version;
        },
        .observation => {
            var slot = &session.observations[row.slot_index];
            slot.persisted_mutation_version = row.mutation_version;
            if (slot.delete_pending) releaseObservation(session, row.slot_index);
        },
        .bucket => {
            var slot = &session.buckets[row.slot_index];
            slot.persisted_mutation_version = row.mutation_version;
            if (slot.delete_pending) releaseBucket(session, row.slot_index);
        },
        .report => {
            var slot = &session.reports[row.slot_index];
            slot.persisted_mutation_version = row.mutation_version;
            if (slot.delete_pending) releaseReport(session, row.slot_index);
        },
        .trust => {
            var slot = &session.trusts[row.slot_index];
            slot.persisted_mutation_version = row.mutation_version;
            if (slot.delete_pending) {
                releaseTrust(session, row.slot_index);
            } else {
                slot.committed = true;
            }
        },
        .metadata => {
            session.metadata_persisted_mutation_version = row.mutation_version;
        },
    }
}

fn syncTrustSuppression(session: *state.Session, now_utc_ms: i64) ResultCode {
    for (session.reports) |*report| {
        if (!report.occupied or report.delete_pending) continue;
        const exact = if (session.findTrust(report.target_handle, report.family_handle)) |index|
            session.trusts[index].committed
        else
            false;
        const wildcard = if (session.findTrust(
            report.target_handle,
            protocol.TrustFamily.all,
        )) |index|
            session.trusts[index].committed
        else
            false;
        const trusted = exact or wildcard;
        if (report.trusted_suppressed == trusted) continue;
        report.trusted_suppressed = trusted;
        report.updated_at_utc_ms = now_utc_ms;
        report.mutation_version = session.allocateMutation() orelse return .out_of_memory;
    }
    return .ok;
}

fn matchesSourceFeedback(
    session: *const state.Session,
    row: *const protocol.PersistenceFeedback,
) bool {
    if (row.slot_index >= session.sources.len) return false;
    const slot = session.sources[row.slot_index];
    return slot.occupied and slot.slot_generation == row.slot_generation and
        slot.record_handle == row.identity_handle and
        slot.mutation_version == row.mutation_version;
}

fn matchesObservationFeedback(
    session: *const state.Session,
    row: *const protocol.PersistenceFeedback,
) bool {
    if (row.slot_index >= session.observations.len) return false;
    const slot = session.observations[row.slot_index];
    return slot.occupied and slot.slot_generation == row.slot_generation and
        slot.observation_handle == row.identity_handle and
        slot.mutation_version == row.mutation_version;
}

fn matchesBucketFeedback(
    session: *const state.Session,
    row: *const protocol.PersistenceFeedback,
) bool {
    if (row.slot_index >= session.buckets.len) return false;
    const slot = session.buckets[row.slot_index];
    return slot.occupied and slot.slot_generation == row.slot_generation and
        slot.bucket_handle == row.identity_handle and
        slot.mutation_version == row.mutation_version;
}

fn matchesReportFeedback(
    session: *const state.Session,
    row: *const protocol.PersistenceFeedback,
) bool {
    if (row.slot_index >= session.reports.len) return false;
    const slot = session.reports[row.slot_index];
    return slot.occupied and slot.slot_generation == row.slot_generation and
        slot.report_handle == row.identity_handle and
        slot.mutation_version == row.mutation_version;
}

fn matchesTrustFeedback(
    session: *const state.Session,
    row: *const protocol.PersistenceFeedback,
) bool {
    if (row.slot_index >= session.trusts.len) return false;
    const slot = session.trusts[row.slot_index];
    return slot.occupied and slot.slot_generation == row.slot_generation and
        slot.trust_handle == row.identity_handle and
        slot.mutation_version == row.mutation_version;
}

fn matchesMetadataFeedback(
    session: *const state.Session,
    row: *const protocol.PersistenceFeedback,
) bool {
    return row.slot_index == 0 and row.slot_generation == 1 and
        row.identity_handle == 1 and
        session.metadata_mutation_version == row.mutation_version;
}

fn releaseObservation(session: *state.Session, index: u32) void {
    if (session.observations[index].rolling) {
        session.rolling_observation_count -= 1;
    }
    const generation = session.observations[index].slot_generation;
    session.observations[index] = .{ .slot_generation = generation };
    session.next_observation_slot = index;
    if (!state.rebuildObservationIndex(
        session.observations,
        session.observation_index,
    )) unreachable;
}

fn releaseBucket(session: *state.Session, index: u32) void {
    const generation = session.buckets[index].slot_generation;
    session.buckets[index] = .{ .slot_generation = generation };
    session.next_bucket_slot = index;
    if (!state.rebuildBucketIndex(session.buckets, session.bucket_index)) unreachable;
}

fn releaseReport(session: *state.Session, index: u32) void {
    const generation = session.reports[index].slot_generation;
    session.reports[index] = .{ .slot_generation = generation };
    session.next_report_slot = index;
    if (!state.rebuildReportIndex(session.reports, session.report_index)) unreachable;
}

fn releaseTrust(session: *state.Session, index: u32) void {
    const generation = session.trusts[index].slot_generation;
    session.trusts[index] = .{ .slot_generation = generation };
    session.next_trust_slot = index;
    if (!state.rebuildTrustIndex(session.trusts, session.trust_index)) unreachable;
}

fn appendDirty(
    slots: anytype,
    kind: state.DirtyKind,
    output: []state.DirtyReference,
    count: *usize,
) void {
    for (slots, 0..) |slot, index| {
        if (!slot.occupied or slot.mutation_version <= slot.persisted_mutation_version) continue;
        output[count.*] = .{
            .mutation_version = slot.mutation_version,
            .index = @intCast(index),
            .kind = kind,
        };
        count.* += 1;
    }
}

fn lessDirty(_: void, left: state.DirtyReference, right: state.DirtyReference) bool {
    if (left.mutation_version != right.mutation_version) {
        return left.mutation_version < right.mutation_version;
    }
    if (@intFromEnum(left.kind) != @intFromEnum(right.kind)) {
        return @intFromEnum(left.kind) < @intFromEnum(right.kind);
    }
    return left.index < right.index;
}

fn lessImportIdentity(_: void, left: state.DirtyReference, right: state.DirtyReference) bool {
    if (@intFromEnum(left.kind) != @intFromEnum(right.kind)) {
        return @intFromEnum(left.kind) < @intFromEnum(right.kind);
    }
    return left.mutation_version < right.mutation_version;
}

fn nextIdentity(maximum: u64) u64 {
    if (maximum == std.math.maxInt(u64)) return maximum;
    return maximum + 1;
}
