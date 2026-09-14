const std = @import("std");
const protocol = @import("protocol.zig");
const state = @import("state.zig");
const window = @import("window.zig");
const ResultCode = @import("../../common/result_codes.zig").ResultCode;

const ObserveCheckpoint = struct {
    next_mutation_version: u64,
    next_report_handle: u64,
    next_observation_handle: u64,
    next_bucket_handle: u64,
    next_source_record_handle: u64,
    rolling_observation_count: u32,
    next_source_slot: u32,
    next_observation_slot: u32,
    next_bucket_slot: u32,
};

pub fn replace(
    session: *state.Session,
    input: *const protocol.RuleReplaceInput,
    rules: []const protocol.RuleInput,
) ResultCode {
    if (!protocol.validRuleReplace(input, &session.config)) return .abi_mismatch;
    if (session.planned_persistence_count != 0) return .unavailable;
    if (input.rule_count != rules.len) return .invalid_argument;
    if (!session.operationAdvances(input.operation_epoch, input.command_monotonic_milliseconds)) {
        return .stale_frame;
    }
    if (!validPredicateGroups(rules)) return .invalid_argument;

    @memset(session.staging_rules, .{});
    @memset(session.staging_rule_index, state.no_index);
    for (rules, 0..) |rule, index| {
        if (!protocol.validRule(&rule)) return .invalid_argument;
        session.staging_rules[index] = .{ .occupied = true, .value = rule };
    }
    if (!state.rebuildRuleIndex(session.staging_rules, session.staging_rule_index)) {
        return .invalid_argument;
    }
    for (rules) |rule| {
        const found = state.findRuleIn(
            session.staging_rules,
            session.staging_rule_index,
            rule.rule_handle,
        ) orelse return .invalid_argument;
        if (session.staging_rules[found].value.rule_generation != rule.rule_generation) {
            return .invalid_argument;
        }
    }

    for (session.observations) |observation| {
        if (!observation.occupied or observation.delete_pending) continue;
        const replacement_index = state.findRuleIn(
            session.staging_rules,
            session.staging_rule_index,
            observation.rule_handle,
        ) orelse return .invalid_argument;
        const replacement = session.staging_rules[replacement_index].value;
        const current_index = session.findRule(observation.rule_handle) orelse
            return .invalid_argument;
        if (!sameRuleDefinition(
            &session.rules[current_index].value,
            &replacement,
        ) or replacement.rule_generation != observation.rule_generation or
            replacement.source_handle != observation.source_handle or
            replacement.coverage_scope_handle != observation.coverage_scope_handle or
            ((replacement.flags & protocol.RuleFlags.rolling) != 0) != observation.rolling)
        {
            return .invalid_argument;
        }
    }

    std.mem.swap([]state.RuleSlot, &session.rules, &session.staging_rules);
    std.mem.swap([]u32, &session.rule_index, &session.staging_rule_index);
    session.rules_ready = true;
    session.commitOperation(
        input.operation_epoch,
        input.command_monotonic_milliseconds,
        input.command_utc_milliseconds,
    );
    return .ok;
}

fn sameRuleDefinition(
    left: *const protocol.RuleInput,
    right: *const protocol.RuleInput,
) bool {
    return left.struct_size == right.struct_size and
        left.flags == right.flags and
        left.rule_handle == right.rule_handle and
        left.rule_generation == right.rule_generation and
        left.source_handle == right.source_handle and
        left.coverage_scope_handle == right.coverage_scope_handle and
        left.family_handle == right.family_handle and
        left.report_type_handle == right.report_type_handle and
        left.resource_kind_handle == right.resource_kind_handle and
        left.payload_handle == right.payload_handle and
        left.metric_selector == right.metric_selector and
        left.comparison == right.comparison and
        left.priority == right.priority and
        left.severity == right.severity and
        left.required_consecutive_hits == right.required_consecutive_hits and
        left.required_consecutive_misses == right.required_consecutive_misses and
        left.minimum_sample_duration_milliseconds ==
            right.minimum_sample_duration_milliseconds and
        left.activation_threshold == right.activation_threshold and
        left.clear_threshold == right.clear_threshold and
        left.stale_after_milliseconds == right.stale_after_milliseconds and
        left.retention_milliseconds == right.retention_milliseconds and
        left.predicate_group_handle == right.predicate_group_handle and
        left.predicate_index == right.predicate_index and
        left.predicate_count == right.predicate_count;
}

fn validPredicateGroups(rules: []const protocol.RuleInput) bool {
    var previous_group: u64 = 0;
    var expected_index: u32 = 0;
    var group_primary: protocol.RuleInput = std.mem.zeroes(protocol.RuleInput);
    for (rules) |rule| {
        if (rule.predicate_group_handle != previous_group) {
            if (previous_group != 0 and expected_index != group_primary.predicate_count) {
                return false;
            }
            if (rule.predicate_group_handle <= previous_group or rule.predicate_index != 0) {
                return false;
            }
            previous_group = rule.predicate_group_handle;
            expected_index = 0;
            group_primary = rule;
        }
        if (rule.predicate_index != expected_index or
            rule.predicate_count != group_primary.predicate_count or
            rule.family_handle != group_primary.family_handle or
            rule.report_type_handle != group_primary.report_type_handle or
            rule.resource_kind_handle != group_primary.resource_kind_handle or
            rule.payload_handle != group_primary.payload_handle or
            rule.priority != group_primary.priority or
            rule.severity != group_primary.severity) return false;
        expected_index += 1;
    }
    return previous_group == 0 or expected_index == group_primary.predicate_count;
}

pub fn observe(
    session: *state.Session,
    input: *const protocol.SourceSnapshotInput,
    facts: []const protocol.FactInput,
) ResultCode {
    if (!session.rules_ready or !session.import_ready) return .unavailable;
    if (!protocol.validSourceSnapshot(input, &session.config)) return .abi_mismatch;
    if (session.planned_persistence_count != 0) return .unavailable;
    if (input.fact_count != facts.len) return .invalid_argument;
    if (!session.operationAdvances(input.operation_epoch, input.command_monotonic_milliseconds)) {
        return .stale_frame;
    }
    if (!session.observedTimeValid(
        input.observed_at_utc_milliseconds,
        input.command_utc_milliseconds,
    )) return .invalid_argument;

    const status: protocol.SourceStatus = @enumFromInt(input.status);
    if (status != .complete and facts.len != 0) return .invalid_argument;
    if (!validateFacts(session, input, facts)) return .invalid_argument;

    const source_index = session.findSource(input.source_handle, input.coverage_scope_handle);
    if (source_index) |index| {
        const source = session.sources[index];
        if (input.source_generation < source.source_generation or
            (source.source_generation == input.source_generation and
                input.source_snapshot_epoch <= source.last_snapshot_epoch)) return .stale_frame;
    }
    var effective_input = input.*;
    effective_input.observed_at_utc_milliseconds = @max(
        input.observed_at_utc_milliseconds,
        session.logical_utc_ms,
    );
    if (source_index) |index| {
        effective_input.observed_at_utc_milliseconds = @max(
            effective_input.observed_at_utc_milliseconds,
            session.sources[index].last_observed_at_utc_ms,
        );
    }
    const free_source_index = if (source_index == null)
        state.firstFreeFrom(
            state.SourceSlot,
            session.sources,
            session.next_source_slot,
        ) orelse return .buffer_too_small
    else
        null;

    const checkpoint = ObserveCheckpoint{
        .next_mutation_version = session.next_mutation_version,
        .next_report_handle = session.next_report_handle,
        .next_observation_handle = session.next_observation_handle,
        .next_bucket_handle = session.next_bucket_handle,
        .next_source_record_handle = session.next_source_record_handle,
        .rolling_observation_count = session.rolling_observation_count,
        .next_source_slot = session.next_source_slot,
        .next_observation_slot = session.next_observation_slot,
        .next_bucket_slot = session.next_bucket_slot,
    };
    session.beginFactTransaction();
    const target_source_index = source_index orelse free_source_index.?;
    session.snapshotSource(target_source_index);
    if (source_index == null) {
        const slot_generation = state.nextSlotGeneration(
            session.sources[target_source_index].slot_generation,
        ) orelse {
            rollbackObserve(session, &checkpoint);
            return .out_of_memory;
        };
        const record_handle = session.allocateSourceRecordHandle() orelse {
            rollbackObserve(session, &checkpoint);
            return .out_of_memory;
        };
        session.sources[target_source_index] = .{
            .occupied = true,
            .slot_generation = slot_generation,
            .record_handle = record_handle,
            .source_handle = input.source_handle,
            .coverage_scope_handle = input.coverage_scope_handle,
        };
        if (!session.insertSourceIndex(target_source_index)) {
            rollbackObserve(session, &checkpoint);
            return .buffer_too_small;
        }
        state.advanceFreeCursor(
            &session.next_source_slot,
            session.sources.len,
            target_source_index,
        );
    }
    const source_mutation = session.allocateMutation() orelse {
        rollbackObserve(session, &checkpoint);
        return .out_of_memory;
    };
    var source = &session.sources[target_source_index];
    source.status = status;
    source.source_generation = input.source_generation;
    source.last_snapshot_epoch = input.source_snapshot_epoch;
    source.last_observed_at_utc_ms = effective_input.observed_at_utc_milliseconds;
    if (status == .complete) {
        source.last_complete_at_utc_ms = effective_input.observed_at_utc_milliseconds;
    }
    source.mutation_version = source_mutation;

    if (status == .complete) {
        const result = observeComplete(session, &effective_input, facts);
        if (result != .ok) {
            rollbackObserve(session, &checkpoint);
            return result;
        }
    }

    session.commitFactTransaction();
    session.commitOperation(
        input.operation_epoch,
        input.command_monotonic_milliseconds,
        input.command_utc_milliseconds,
    );
    return .ok;
}

fn rollbackObserve(session: *state.Session, checkpoint: *const ObserveCheckpoint) void {
    session.rollbackFactTransaction();
    restoreAggregateAfterRollback(session);
    session.next_mutation_version = checkpoint.next_mutation_version;
    session.next_report_handle = checkpoint.next_report_handle;
    session.next_observation_handle = checkpoint.next_observation_handle;
    session.next_bucket_handle = checkpoint.next_bucket_handle;
    session.next_source_record_handle = checkpoint.next_source_record_handle;
    session.rolling_observation_count = checkpoint.rolling_observation_count;
    session.next_source_slot = checkpoint.next_source_slot;
    session.next_observation_slot = checkpoint.next_observation_slot;
    session.next_bucket_slot = checkpoint.next_bucket_slot;
}

pub fn maintain(session: *state.Session, now_utc_ms: i64) ResultCode {
    const prune_result = window.prune(session, now_utc_ms);
    if (prune_result != .ok) return prune_result;
    const aggregate_result = window.aggregate(session, now_utc_ms);
    if (aggregate_result != .ok) return aggregate_result;
    const window_evaluation_result = evaluateChangedRollingWindows(session);
    if (window_evaluation_result != .ok) return window_evaluation_result;
    const stale_result = updateStaleObservations(session, now_utc_ms);
    if (stale_result != .ok) return stale_result;
    return recomputeReports(session, now_utc_ms);
}

pub fn metricValue(
    session: *const state.Session,
    observation_index: u32,
    rule: *const protocol.RuleInput,
) f64 {
    const observation = &session.observations[observation_index];
    const selector: protocol.MetricSelector = @enumFromInt(rule.metric_selector);
    const comparison: protocol.Comparison = @enumFromInt(rule.comparison);
    if (comparison == .left_minus_right_greater_or_equal or
        comparison == .left_minus_right_less_or_equal)
    {
        if (!observation.current_valid or !observation.secondary_current_valid) return 0;
        return observation.current_value - observation.secondary_current_value;
    }
    const windows = window.values(session, observation_index);
    return switch (selector) {
        .current => if (observation.current_valid) observation.current_value else 0,
        .average => if (observation.sample_count == 0)
            0
        else
            observation.value_sum / @as(f64, @floatFromInt(observation.sample_count)),
        .peak => observation.peak_value,
        .sum_24h => windows.sum_24h,
        .sum_7d => windows.sum_7d,
        .events_24h => @floatFromInt(windows.events_24h),
        .events_7d => @floatFromInt(windows.events_7d),
        .request_events_24h => @floatFromInt(windows.request_events_24h),
        .request_events_7d => @floatFromInt(windows.request_events_7d),
        .connection_events_24h => @floatFromInt(windows.connection_events_24h),
        .connection_events_7d => @floatFromInt(windows.connection_events_7d),
        .sample_hits_24h => @floatFromInt(windows.sample_hits_24h),
        .sample_hits_7d => @floatFromInt(windows.sample_hits_7d),
    };
}

fn validateFacts(
    session: *state.Session,
    input: *const protocol.SourceSnapshotInput,
    facts: []const protocol.FactInput,
) bool {
    if (facts.len > session.fact_keys.len) return false;
    for (facts, 0..) |fact, index| {
        if (!protocol.validFact(&fact)) return false;
        const rule_index = session.findRule(fact.rule_handle) orelse return false;
        const rule = session.rules[rule_index].value;
        if (rule.rule_generation != fact.rule_generation or
            rule.source_handle != input.source_handle or
            rule.coverage_scope_handle != input.coverage_scope_handle or
            !factShapeMatchesRule(&fact, &rule)) return false;
        session.fact_keys[index] = .{
            .rule_handle = fact.rule_handle,
            .target_handle = fact.target_handle,
        };
    }
    const keys = session.fact_keys[0..facts.len];
    std.sort.heap(state.FactKey, keys, {}, lessFactKey);
    if (keys.len < 2) return true;
    for (keys[1..], keys[0..keys.len -| 1]) |current, previous| {
        if (current.rule_handle == previous.rule_handle and
            current.target_handle == previous.target_handle) return false;
    }
    return true;
}

fn factShapeMatchesRule(fact: *const protocol.FactInput, rule: *const protocol.RuleInput) bool {
    if (fact.sample_duration_milliseconds < rule.minimum_sample_duration_milliseconds) return false;
    const selector: protocol.MetricSelector = @enumFromInt(rule.metric_selector);
    const comparison: protocol.Comparison = @enumFromInt(rule.comparison);
    if (comparison == .left_minus_right_greater_or_equal or
        comparison == .left_minus_right_less_or_equal)
    {
        return selector == .current and
            (fact.flags & protocol.FactFlags.current_valid) != 0 and
            (fact.flags & protocol.FactFlags.secondary_current_valid) != 0;
    }
    return switch (selector) {
        .current, .average => (fact.flags & protocol.FactFlags.current_valid) != 0,
        .peak => (fact.flags & protocol.FactFlags.peak_valid) != 0,
        .sum_24h, .sum_7d => (fact.flags & protocol.FactFlags.delta_valid) != 0,
        .events_24h, .events_7d => (fact.flags & protocol.FactFlags.event_count_valid) != 0,
        .request_events_24h, .request_events_7d => (fact.flags &
            protocol.FactFlags.request_event_count_valid) != 0,
        .connection_events_24h, .connection_events_7d => (fact.flags &
            protocol.FactFlags.connection_event_count_valid) != 0,
        .sample_hits_24h, .sample_hits_7d => (fact.flags &
            protocol.FactFlags.sample_hit_count_valid) != 0,
    };
}

fn observeComplete(
    session: *state.Session,
    input: *const protocol.SourceSnapshotInput,
    facts: []const protocol.FactInput,
) ResultCode {
    const effective_now_utc_ms = @max(
        session.logical_utc_ms,
        input.command_utc_milliseconds,
    );
    for (facts) |fact| {
        const rule_index = session.findRule(fact.rule_handle) orelse return .invalid_argument;
        const rule = &session.rules[rule_index].value;
        const existing = session.findObservation(
            input.source_handle,
            input.coverage_scope_handle,
            fact.rule_handle,
            fact.target_handle,
        );
        const slot_index = existing orelse state.firstFreeFrom(
            state.ObservationSlot,
            session.observations,
            session.next_observation_slot,
        ) orelse return .buffer_too_small;
        session.snapshotObservation(slot_index);
        if (existing == null) {
            const rolling = (rule.flags & protocol.RuleFlags.rolling) != 0;
            if (rolling and session.rolling_observation_count >=
                session.config.maximum_rolling_observation_count) return .buffer_too_small;
            const generation = state.nextSlotGeneration(
                session.observations[slot_index].slot_generation,
            ) orelse return .out_of_memory;
            const handle = session.allocateObservationHandle() orelse return .out_of_memory;
            session.observations[slot_index] = .{
                .occupied = true,
                .rolling = rolling,
                .slot_generation = generation,
                .observation_handle = handle,
                .source_handle = input.source_handle,
                .coverage_scope_handle = input.coverage_scope_handle,
                .rule_handle = fact.rule_handle,
                .rule_generation = fact.rule_generation,
                .target_handle = fact.target_handle,
                .first_observed_at_utc_ms = input.observed_at_utc_milliseconds,
            };
            if (!session.insertObservationIndex(slot_index)) return .buffer_too_small;
            state.advanceFreeCursor(
                &session.next_observation_slot,
                session.observations.len,
                slot_index,
            );
            if (rolling) session.rolling_observation_count += 1;
        }
        var observation = &session.observations[slot_index];
        if (observation.delete_pending or
            observation.rule_generation != fact.rule_generation) return .stale_frame;
        if (fact.fact_sequence <= observation.last_fact_sequence) return .stale_frame;

        if ((rule.flags & protocol.RuleFlags.rolling) != 0) {
            const window_result = window.update(session, input, &fact);
            if (window_result != .ok) return window_result;
        }

        observation.evidence_payload_handle = fact.evidence_payload_handle;
        observation.last_observed_at_utc_ms = input.observed_at_utc_milliseconds;
        observation.seen_operation_epoch = input.operation_epoch;
        observation.last_fact_sequence = fact.fact_sequence;
        observation.sample_count = addSaturating(observation.sample_count, 1);
        observation.sample_duration_milliseconds = addSaturating(
            observation.sample_duration_milliseconds,
            fact.sample_duration_milliseconds,
        );
        if ((fact.flags & protocol.FactFlags.current_valid) != 0) {
            observation.current_valid = true;
            observation.current_value = fact.current_value;
            const sum = observation.value_sum + fact.current_value;
            if (!std.math.isFinite(sum)) return .invalid_argument;
            observation.value_sum = sum;
        }
        if ((fact.flags & protocol.FactFlags.secondary_current_valid) != 0) {
            observation.secondary_current_valid = true;
            observation.secondary_current_value = fact.secondary_current_value;
        } else {
            observation.secondary_current_valid = false;
            observation.secondary_current_value = 0;
        }
        if ((fact.flags & protocol.FactFlags.peak_valid) != 0) {
            observation.peak_value = @max(observation.peak_value, fact.peak_value);
        } else if ((fact.flags & protocol.FactFlags.current_valid) != 0) {
            observation.peak_value = @max(observation.peak_value, fact.current_value);
        }
        observation.mutation_version = session.allocateMutation() orelse return .out_of_memory;
    }

    const aggregate_result = window.aggregate(session, effective_now_utc_ms);
    if (aggregate_result != .ok) return aggregate_result;
    for (facts) |fact| {
        const rule_index = session.findRule(fact.rule_handle) orelse return .invalid_argument;
        const rule = &session.rules[rule_index].value;
        const observation_index = session.findObservation(
            input.source_handle,
            input.coverage_scope_handle,
            fact.rule_handle,
            fact.target_handle,
        ) orelse return .invalid_argument;
        applyMetricOutcome(
            &session.observations[observation_index],
            rule,
            metricValue(session, observation_index, rule),
        );
        session.observations[observation_index].evaluated_window_revision =
            session.observations[observation_index].window_revision;
    }

    for (session.observations, 0..) |*observation, observation_index| {
        if (!observation.occupied or observation.delete_pending or
            observation.source_handle != input.source_handle or
            observation.coverage_scope_handle != input.coverage_scope_handle or
            observation.seen_operation_epoch == input.operation_epoch) continue;
        session.snapshotObservation(@intCast(observation_index));
        observation.consecutive_hits = 0;
        observation.active_sample_count = 0;
        observation.consecutive_misses = addSaturatingU32(observation.consecutive_misses, 1);
        const rule_index = session.findRule(observation.rule_handle) orelse return .invalid_argument;
        const rule = session.rules[rule_index].value;
        if (observation.active and
            observation.consecutive_misses >= rule.required_consecutive_misses)
        {
            observation.active = false;
        }
        observation.mutation_version = session.allocateMutation() orelse return .out_of_memory;
    }

    const maintenance = maintain(session, effective_now_utc_ms);
    if (maintenance != .ok) return maintenance;
    return .ok;
}

fn applyMetricOutcome(
    observation: *state.ObservationSlot,
    rule: *const protocol.RuleInput,
    metric: f64,
) void {
    const comparison: protocol.Comparison = @enumFromInt(rule.comparison);
    const activation_hit = compare(metric, rule.activation_threshold, comparison);
    const clear_safe = !compare(metric, rule.clear_threshold, comparison);
    if (!observation.active) {
        observation.consecutive_misses = 0;
        if (activation_hit) {
            observation.consecutive_hits = addSaturatingU32(observation.consecutive_hits, 1);
            observation.active_sample_count = observation.consecutive_hits;
            if (observation.consecutive_hits >= rule.required_consecutive_hits) {
                observation.active = true;
            }
        } else {
            observation.consecutive_hits = 0;
            observation.active_sample_count = 0;
        }
        return;
    }

    if (clear_safe) {
        observation.consecutive_hits = 0;
        observation.active_sample_count = 0;
        observation.consecutive_misses = addSaturatingU32(observation.consecutive_misses, 1);
        if (observation.consecutive_misses >= rule.required_consecutive_misses) {
            observation.active = false;
        }
    } else {
        observation.consecutive_misses = 0;
        observation.consecutive_hits = addSaturatingU32(observation.consecutive_hits, 1);
        observation.active_sample_count = observation.consecutive_hits;
    }
}

fn evaluateChangedRollingWindows(session: *state.Session) ResultCode {
    for (session.observations, 0..) |*observation, observation_index| {
        if (!observation.occupied or observation.delete_pending or !observation.rolling or
            observation.evaluated_window_revision == observation.window_revision) continue;
        const rule_index = session.findRule(observation.rule_handle) orelse
            return .invalid_argument;
        const rule = &session.rules[rule_index].value;
        const was_active = observation.active;
        const previous_hits = observation.consecutive_hits;
        const previous_misses = observation.consecutive_misses;
        const previous_active_samples = observation.active_sample_count;
        session.snapshotObservation(@intCast(observation_index));
        applyMetricOutcome(
            observation,
            rule,
            metricValue(session, @intCast(observation_index), rule),
        );
        observation.evaluated_window_revision = observation.window_revision;
        if (observation.active != was_active or
            observation.consecutive_hits != previous_hits or
            observation.consecutive_misses != previous_misses or
            observation.active_sample_count != previous_active_samples)
        {
            observation.mutation_version = session.allocateMutation() orelse
                return .out_of_memory;
        }
    }
    return .ok;
}

fn updateStaleObservations(session: *state.Session, now_utc_ms: i64) ResultCode {
    for (session.observations, 0..) |*observation, observation_index| {
        if (!observation.occupied or observation.delete_pending) continue;
        const rule_index = session.findRule(observation.rule_handle) orelse return .invalid_argument;
        const rule = session.rules[rule_index].value;
        const stale_after = if (rule.stale_after_milliseconds != 0)
            rule.stale_after_milliseconds
        else
            session.config.default_stale_after_milliseconds;
        const retention = if (rule.retention_milliseconds != 0)
            rule.retention_milliseconds
        else
            session.config.default_retention_milliseconds;
        if (observation.active and elapsedAtLeast(
            now_utc_ms,
            observation.last_observed_at_utc_ms,
            stale_after,
        )) {
            session.snapshotObservation(@intCast(observation_index));
            observation.active = false;
            observation.consecutive_hits = 0;
            observation.active_sample_count = 0;
            observation.mutation_version = session.allocateMutation() orelse return .out_of_memory;
        }
        if (!observation.active and elapsedAtLeast(
            now_utc_ms,
            observation.last_observed_at_utc_ms,
            retention,
        )) {
            for (session.buckets, 0..) |*bucket, bucket_index| {
                if (!bucket.occupied or bucket.delete_pending or
                    bucket.source_handle != observation.source_handle or
                    bucket.rule_handle != observation.rule_handle or
                    bucket.target_handle != observation.target_handle) continue;
                session.snapshotBucket(@intCast(bucket_index));
                bucket.delete_pending = true;
                bucket.mutation_version = session.allocateMutation() orelse return .out_of_memory;
            }
            session.snapshotObservation(@intCast(observation_index));
            observation.delete_pending = true;
            observation.mutation_version = session.allocateMutation() orelse return .out_of_memory;
        }
    }
    return .ok;
}

pub fn recomputeReports(session: *state.Session, now_utc_ms: i64) ResultCode {
    @memset(session.report_candidates, state.no_index);
    for (session.observations, 0..) |observation, observation_index| {
        if (!observation.occupied or observation.delete_pending or !observation.active) continue;
        const rule_index = session.findRule(observation.rule_handle) orelse return .invalid_argument;
        const rule = session.rules[rule_index].value;
        if (rule.predicate_index != 0 or
            !predicateGroupActive(session, observation.target_handle, &rule)) continue;
        const existing = session.findReport(observation.target_handle, rule.family_handle);
        const report_index = existing orelse state.firstFreeFrom(
            state.ReportSlot,
            session.reports,
            session.next_report_slot,
        ) orelse return .buffer_too_small;
        if (existing == null) {
            session.snapshotReport(report_index);
            const generation = state.nextSlotGeneration(
                session.reports[report_index].slot_generation,
            ) orelse return .out_of_memory;
            const handle = session.allocateReportHandle() orelse return .out_of_memory;
            session.reports[report_index] = .{
                .occupied = true,
                .slot_generation = generation,
                .report_handle = handle,
                .target_handle = observation.target_handle,
                .family_handle = rule.family_handle,
                .created_at_utc_ms = observation.first_observed_at_utc_ms,
                .updated_at_utc_ms = now_utc_ms,
            };
            if (!session.insertReportIndex(report_index)) return .buffer_too_small;
            state.advanceFreeCursor(
                &session.next_report_slot,
                session.reports.len,
                report_index,
            );
        }
        const current = session.report_candidates[report_index];
        if (current == state.no_index or candidateBetter(
            session,
            @intCast(observation_index),
            current,
        )) {
            session.report_candidates[report_index] = @intCast(observation_index);
        }
    }

    for (session.reports, 0..) |*report, report_index| {
        if (!report.occupied or report.delete_pending) continue;
        const candidate = session.report_candidates[report_index];
        if (candidate == state.no_index) {
            if (report.active) {
                session.snapshotReport(@intCast(report_index));
                report.active = false;
                report.updated_at_utc_ms = now_utc_ms;
                report.mutation_version = session.allocateMutation() orelse return .out_of_memory;
            } else {
                const retention = reportRetention(session, report);
                if (elapsedAtLeast(now_utc_ms, report.updated_at_utc_ms, retention)) {
                    session.snapshotReport(@intCast(report_index));
                    report.delete_pending = true;
                    report.mutation_version = session.allocateMutation() orelse return .out_of_memory;
                }
            }
            continue;
        }

        const observation = session.observations[candidate];
        const rule_index = session.findRule(observation.rule_handle) orelse return .invalid_argument;
        const rule = session.rules[rule_index].value;
        const trusted = reportTrusted(session, report.target_handle, report.family_handle);
        const changed = !report.active or report.observation_index != candidate or
            report.rule_handle != rule.rule_handle or
            report.report_type_handle != rule.report_type_handle or
            report.resource_kind_handle != rule.resource_kind_handle or
            report.payload_handle != rule.payload_handle or
            report.evidence_payload_handle != observation.evidence_payload_handle or
            report.last_observed_at_utc_ms != observation.last_observed_at_utc_ms or
            report.trusted_suppressed != trusted or
            report.priority != rule.priority or
            report.severity != rule.severity;
        if (changed) session.snapshotReport(@intCast(report_index));
        report.active = true;
        report.trusted_suppressed = trusted;
        report.observation_index = candidate;
        report.rule_handle = rule.rule_handle;
        report.report_type_handle = rule.report_type_handle;
        report.resource_kind_handle = rule.resource_kind_handle;
        report.payload_handle = rule.payload_handle;
        report.evidence_payload_handle = observation.evidence_payload_handle;
        report.last_observed_at_utc_ms = observation.last_observed_at_utc_ms;
        report.priority = rule.priority;
        report.severity = rule.severity;
        if (changed) {
            report.updated_at_utc_ms = now_utc_ms;
            report.mutation_version = session.allocateMutation() orelse return .out_of_memory;
        }
    }
    return .ok;
}

fn candidateBetter(session: *const state.Session, candidate: u32, current: u32) bool {
    const candidate_observation = session.observations[candidate];
    const current_observation = session.observations[current];
    const candidate_rule = session.rules[session.findRule(candidate_observation.rule_handle).?].value;
    const current_rule = session.rules[session.findRule(current_observation.rule_handle).?].value;
    if (candidate_rule.priority != current_rule.priority) {
        return candidate_rule.priority > current_rule.priority;
    }
    if (candidate_rule.severity != current_rule.severity) {
        return candidate_rule.severity > current_rule.severity;
    }
    return candidate_rule.rule_handle < current_rule.rule_handle;
}

fn predicateGroupActive(
    session: *const state.Session,
    target_handle: u64,
    primary: *const protocol.RuleInput,
) bool {
    var matched: u32 = 0;
    for (session.rules) |slot| {
        if (!slot.occupied or
            slot.value.predicate_group_handle != primary.predicate_group_handle) continue;
        const rule = slot.value;
        const observation_index = session.findObservation(
            rule.source_handle,
            rule.coverage_scope_handle,
            rule.rule_handle,
            target_handle,
        ) orelse return false;
        const observation = session.observations[observation_index];
        if (!observation.occupied or observation.delete_pending or !observation.active) return false;
        matched += 1;
    }
    return matched == primary.predicate_count;
}

fn reportTrusted(session: *const state.Session, target: u64, family: u64) bool {
    if (session.findTrust(target, family)) |trust_index| {
        if (session.trusts[trust_index].committed) return true;
    }
    if (session.findTrust(target, protocol.TrustFamily.all)) |trust_index| {
        return session.trusts[trust_index].committed;
    }
    return false;
}

fn reportRetention(session: *const state.Session, report: *const state.ReportSlot) u64 {
    const rule_index = session.findRule(report.rule_handle) orelse
        return session.config.default_retention_milliseconds;
    const configured = session.rules[rule_index].value.retention_milliseconds;
    return if (configured == 0) session.config.default_retention_milliseconds else configured;
}

fn compare(value: f64, threshold: f64, comparison: protocol.Comparison) bool {
    return switch (comparison) {
        .greater_or_equal => value >= threshold,
        .less_or_equal => value <= threshold,
        .equal => value == threshold,
        .not_equal => value != threshold,
        .bit_all_set => bitAllSet(value, threshold),
        .left_minus_right_greater_or_equal => value >= threshold,
        .left_minus_right_less_or_equal => value <= threshold,
    };
}

fn bitAllSet(value: f64, mask: f64) bool {
    if (!std.math.isFinite(value) or !std.math.isFinite(mask) or
        value < 0 or mask < 0 or
        value > @as(f64, @floatFromInt(std.math.maxInt(u32))) or
        mask > @as(f64, @floatFromInt(std.math.maxInt(u32))) or
        @floor(value) != value or @floor(mask) != mask) return false;
    const integer_value: u32 = @intFromFloat(value);
    const integer_mask: u32 = @intFromFloat(mask);
    return (integer_value & integer_mask) == integer_mask;
}

fn elapsedAtLeast(now: i64, then: i64, duration: u64) bool {
    if (now < then or duration > std.math.maxInt(i64)) return false;
    return now - then >= @as(i64, @intCast(duration));
}

fn lessFactKey(_: void, left: state.FactKey, right: state.FactKey) bool {
    return left.rule_handle < right.rule_handle or
        (left.rule_handle == right.rule_handle and left.target_handle < right.target_handle);
}

fn addSaturating(value: u64, increment: u64) u64 {
    return std.math.add(u64, value, increment) catch std.math.maxInt(u64);
}

fn addSaturatingU32(value: u32, increment: u32) u32 {
    return std.math.add(u32, value, increment) catch std.math.maxInt(u32);
}

fn restoreAggregateAfterRollback(session: *state.Session) void {
    if (window.rebuildAggregate(session, session.logical_utc_ms) != .ok) unreachable;
}
