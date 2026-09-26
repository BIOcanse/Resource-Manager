const std = @import("std");
const protocol = @import("protocol.zig");
const catalog = @import("catalog.zig");
const state = @import("state.zig");
const inventory = @import("inventory.zig");
const ResultCode = @import("../../common/result_codes.zig").ResultCode;

pub fn begin(session: *state.Session, header: *const protocol.CompletionHeader) ResultCode {
    state.lock(&session.mutex);
    defer state.unlock(&session.mutex);
    if (!validHeader(session, header)) return .abi_mismatch;
    if (session.phase == .completion_open) return .unavailable;
    if (!session.operationAdvances(header.operation_epoch, header.command_at_milliseconds) or
        header.plan_epoch != session.last_plan_epoch)
    {
        return .stale_frame;
    }
    const source_index = session.active.findSource(header.source_handle) orelse
        return .invalid_argument;
    const source = session.active.sources[source_index];
    const plan_state = session.source_modes[source_index];
    if (!plan_state.present or !plan_state.selected or plan_state.completion_committed or
        plan_state.plan_epoch != header.plan_epoch or
        plan_state.input.capability_generation != header.capability_generation or
        plan_state.plan_token_fingerprint != header.plan_token_fingerprint)
    {
        return .stale_frame;
    }
    if (source.source_incarnation == header.source_incarnation and
        header.source_observation_sequence <= source.last_observation_sequence)
    {
        return .stale_frame;
    }
    const planned_count = session.source_modes[source_index].planned_rule_count;
    if (source.definition.source_role == @intFromEnum(protocol.SourceRole.metrics)) {
        if (plan_state.inventory_selected) return .invalid_argument;
        if (header.requested_rule_count != planned_count or
            header.gpu_inventory_count != 0 or
            header.expected_gpu_inventory_count != 0)
        {
            return .invalid_argument;
        }
    } else {
        if (!plan_state.inventory_selected) return .invalid_argument;
        if (header.requested_rule_count != 0 or header.observation_count != 0 or
            header.cpu_counter_count != 0)
        {
            return .invalid_argument;
        }
    }
    if (!validCompletionShape(header)) return .invalid_argument;
    const next_revision = session.nextRevision() orelse return .unavailable;
    session.clearCompletion();
    session.completion = header.*;
    session.phase = .completion_open;
    session.state_revision = next_revision;
    return .ok;
}

pub fn submitRequested(
    session: *state.Session,
    requested: []const protocol.RequestedMetricInput,
) ResultCode {
    state.lock(&session.mutex);
    defer state.unlock(&session.mutex);
    if (session.phase != .completion_open) return .unavailable;
    if (requested.len != session.completion.requested_rule_count) return .invalid_argument;
    var previous_handle: u64 = 0;
    for (requested) |item| {
        if (item.struct_size != @sizeOf(protocol.RequestedMetricInput) or item.flags != 0 or
            item.rule_handle <= previous_handle or !allZero(item.reserved))
        {
            return .invalid_argument;
        }
        previous_handle = item.rule_handle;
        const rule_index = session.active.findRule(item.rule_handle) orelse
            return .invalid_argument;
        const rule = session.active.rules[rule_index];
        if (!session.planned_rule_flags[rule_index] or
            rule.definition.source_handle != session.completion.source_handle or
            session.requested_rule_flags[rule_index])
        {
            return .invalid_argument;
        }
    }
    for (requested, 0..) |item, ordinal| {
        const rule_index = session.active.findRule(item.rule_handle).?;
        session.requested_rule_indices[ordinal] = rule_index;
        session.requested_rule_flags[rule_index] = true;
    }
    session.requested_count = @intCast(requested.len);
    return .ok;
}

pub fn submitObservations(
    session: *state.Session,
    observations: []const protocol.ObservationInput,
) ResultCode {
    state.lock(&session.mutex);
    defer state.unlock(&session.mutex);
    if (session.phase != .completion_open) return .unavailable;
    if (observations.len != session.completion.observation_count or
        observations.len + session.completion.cpu_counter_count > session.observations.len)
    {
        return .invalid_argument;
    }
    var previous_handle: u64 = 0;
    for (observations) |observation| {
        const rule_index = validateObservation(session, &observation) orelse
            return .invalid_argument;
        if (observation.rule_handle <= previous_handle or
            session.observation_slot_by_rule[rule_index] != catalog.no_index)
        {
            return .invalid_argument;
        }
        const kind: protocol.MetricKind =
            @enumFromInt(session.active.rules[rule_index].definition.metric_kind);
        if (kind == .cpu_usage) return .invalid_argument;
        previous_handle = observation.rule_handle;
    }
    for (observations, 0..) |observation, ordinal| {
        const rule_index = session.active.findRule(observation.rule_handle).?;
        session.observations[ordinal] = observation;
        session.observation_slot_by_rule[rule_index] = @intCast(ordinal);
    }
    session.observation_count = @intCast(observations.len);
    return .ok;
}

pub fn submitCpuCounter(
    session: *state.Session,
    input: *const protocol.CpuCounterInput,
) ResultCode {
    state.lock(&session.mutex);
    defer state.unlock(&session.mutex);
    if (session.phase != .completion_open or session.completion.cpu_counter_count != 1 or
        session.pending_cpu_counter_present)
    {
        return .unavailable;
    }
    if (session.observation_count != session.completion.observation_count or
        session.observation_count >= session.observations.len)
    {
        return .invalid_argument;
    }
    const rule_index = validateCpuCounter(session, input) orelse return .invalid_argument;
    if (session.observation_slot_by_rule[rule_index] != catalog.no_index) {
        return .invalid_argument;
    }
    const source_index = session.active.findSource(input.source_handle).?;
    const source = session.active.sources[source_index];
    var observation = protocol.ObservationInput{
        .struct_size = @sizeOf(protocol.ObservationInput),
        .flags = 0,
        .rule_handle = input.rule_handle,
        .metric_handle = input.metric_handle,
        .source_handle = input.source_handle,
        .scope_handle = session.active.rules[rule_index].definition.scope_handle,
        .source_observation_sequence = input.source_observation_sequence,
        .observed_at_milliseconds = input.observed_at_milliseconds,
        .value_bits = 0,
        .capability_mask = input.capability_mask,
        .valid_mask = protocol.ObservationValid.required,
        .status = @intFromEnum(protocol.ObservationStatus.skipped),
        .value_kind = @intFromEnum(protocol.ValueKind.float64),
        .quality = 0,
        .reserved_u32 = 0,
        .sample_duration_milliseconds = 0,
    };
    if (input.status == @intFromEnum(protocol.ObservationStatus.current) and
        source.cpu_baseline_valid and source.source_incarnation == input.source_incarnation and
        source.cpu_counter_contract_version == input.counter_contract_version and
        source.cpu_monotonic_ticks_per_second == input.monotonic_ticks_per_second and
        input.idle_ticks >= source.cpu_idle_ticks and
        input.kernel_ticks >= source.cpu_kernel_ticks and
        input.user_ticks >= source.cpu_user_ticks and
        input.monotonic_ticks > source.cpu_monotonic_ticks)
    {
        const idle_delta = input.idle_ticks - source.cpu_idle_ticks;
        const kernel_delta = input.kernel_ticks - source.cpu_kernel_ticks;
        const user_delta = input.user_ticks - source.cpu_user_ticks;
        const total_delta = std.math.add(u64, kernel_delta, user_delta) catch null;
        if (total_delta) |total| if (total > 0 and idle_delta <= total) {
            const busy_delta = total - idle_delta;
            const usage = @as(f64, @floatFromInt(busy_delta)) * 100.0 /
                @as(f64, @floatFromInt(total));
            const monotonic_delta = input.monotonic_ticks - source.cpu_monotonic_ticks;
            const duration = @as(u128, monotonic_delta) * 1000 /
                @as(u128, input.monotonic_ticks_per_second);
            if (duration > 0 and duration <= std.math.maxInt(u64)) {
                observation.value_bits = @bitCast(usage);
                observation.valid_mask |= protocol.ObservationValid.value;
                observation.status = @intFromEnum(protocol.ObservationStatus.current);
                observation.sample_duration_milliseconds = @intCast(duration);
            }
        };
    } else if (input.status != @intFromEnum(protocol.ObservationStatus.current)) {
        observation.status = input.status;
    }
    const slot = session.observation_count;
    session.observations[slot] = observation;
    session.observation_slot_by_rule[rule_index] = slot;
    session.observation_count += 1;
    session.pending_cpu_counter = input.*;
    session.pending_cpu_counter_present = true;
    return .ok;
}

pub fn finalize(session: *state.Session, input: *const protocol.FinalizeInput) ResultCode {
    state.lock(&session.mutex);
    defer state.unlock(&session.mutex);
    if (!validFinalize(session, input)) return .abi_mismatch;
    const counts = completionCounts(session) orelse return .invalid_argument;

    const source_index = session.active.findSource(session.completion.source_handle).?;
    const active_source = &session.active.sources[source_index];
    const reset_reason_mask = sourceResetReason(active_source, session);
    if (active_source.source_generation == std.math.maxInt(u64) or
        (reset_reason_mask != 0 and active_source.reset_count == std.math.maxInt(u32)))
    {
        return .unavailable;
    }
    const next_revision = session.nextRevision() orelse return .unavailable;
    const next_source_generation = active_source.source_generation + 1;
    const before = session.semantic_fingerprint;
    stageFinalizedSource(
        session,
        source_index,
        next_source_generation,
        reset_reason_mask,
        counts,
    );
    const after = semanticFingerprintFor(
        &session.staging,
        session.catalog_fingerprint,
        inventory.finalizedRows(session),
    );
    var next_committed = session.committed_generation;
    if (after != before) {
        next_committed = session.nextCommittedGeneration() orelse return .unavailable;
    }

    commitFinalizedSource(
        session,
        input,
        source_index,
        next_revision,
        next_committed,
        after,
    );
    return .ok;
}

const CompletionCounts = struct {
    observed: u32,
    skipped: u32,
};

fn completionCounts(session: *const state.Session) ?CompletionCounts {
    const supplied_count = std.math.add(
        u32,
        session.completion.observation_count,
        session.completion.cpu_counter_count,
    ) catch return null;
    if (session.requested_count != session.completion.requested_rule_count or
        session.observation_count != supplied_count or
        (session.completion.cpu_counter_count == 1 and !session.pending_cpu_counter_present) or
        session.gpu_staging_count != session.completion.gpu_inventory_count or
        !allRequestedAccountedFor(session))
    {
        return null;
    }

    var counts = CompletionCounts{
        .observed = if (session.completion.gpu_inventory_count != 0)
            session.gpu_staging_count
        else
            0,
        .skipped = 0,
    };
    for (session.observations[0..session.completion.observation_count]) |observation| {
        if (observation.status == @intFromEnum(protocol.ObservationStatus.current)) {
            counts.observed = std.math.add(u32, counts.observed, 1) catch return null;
        } else if (observation.status == @intFromEnum(protocol.ObservationStatus.skipped)) {
            counts.skipped = std.math.add(u32, counts.skipped, 1) catch return null;
        }
    }
    if (session.pending_cpu_counter_present) {
        if (session.pending_cpu_counter.status ==
            @intFromEnum(protocol.ObservationStatus.current))
        {
            counts.observed = std.math.add(u32, counts.observed, 1) catch return null;
        } else if (session.pending_cpu_counter.status ==
            @intFromEnum(protocol.ObservationStatus.skipped))
        {
            counts.skipped = std.math.add(u32, counts.skipped, 1) catch return null;
        }
    }
    if (session.completion.status == @intFromEnum(protocol.SourceStatus.skipped)) {
        counts.skipped = session.completion.requested_rule_count;
    }
    return counts;
}

fn stageFinalizedSource(
    session: *state.Session,
    source_index: usize,
    next_source_generation: u64,
    reset_reason_mask: u32,
    counts: CompletionCounts,
) void {
    synchronizeBank(&session.staging, &session.active);
    if ((reset_reason_mask & protocol.SourceResetReason.incarnation_changed) != 0) {
        invalidateCurrentRulesForSource(
            &session.staging,
            session.completion.source_handle,
            session.completion.capability_generation,
        );
    }
    applyRequestedRules(&session.staging, session, next_source_generation);
    inventory.prepareFinalized(session, next_source_generation);
    const source = &session.staging.sources[source_index];
    if ((reset_reason_mask & protocol.SourceResetReason.incarnation_changed) != 0) {
        clearCpuBaseline(source);
    }
    if (session.pending_cpu_counter_present and
        session.pending_cpu_counter.status ==
            @intFromEnum(protocol.ObservationStatus.current))
    {
        source.cpu_baseline_valid = true;
        source.cpu_idle_ticks = session.pending_cpu_counter.idle_ticks;
        source.cpu_kernel_ticks = session.pending_cpu_counter.kernel_ticks;
        source.cpu_user_ticks = session.pending_cpu_counter.user_ticks;
        source.cpu_monotonic_ticks = session.pending_cpu_counter.monotonic_ticks;
        source.cpu_monotonic_ticks_per_second =
            session.pending_cpu_counter.monotonic_ticks_per_second;
        source.cpu_counter_contract_version =
            session.pending_cpu_counter.counter_contract_version;
    }
    source.source_incarnation = session.completion.source_incarnation;
    source.last_observation_sequence = session.completion.source_observation_sequence;
    source.capability_generation = session.completion.capability_generation;
    source.source_generation = next_source_generation;
    source.last_attempt_at_milliseconds = session.completion.captured_at_milliseconds;
    source.requested_count = session.completion.requested_rule_count;
    source.observed_count = counts.observed;
    source.skipped_count = counts.skipped;
    source.overflow_count = session.completion.overflow_count;
    if (source.definition.source_role == @intFromEnum(protocol.SourceRole.gpu_inventory)) {
        source.gpu_inventory_count = session.gpu_staging_count;
        source.gpu_retained_count = inventory.stagedRetainedCount(session);
    } else {
        source.gpu_inventory_count = 0;
        source.gpu_retained_count = 0;
    }
    source.status = @enumFromInt(session.completion.status);
    source.last_reset_reason_mask = reset_reason_mask;
    if (reset_reason_mask != 0) source.reset_count += 1;
    if (session.completion.status == @intFromEnum(protocol.SourceStatus.complete) or
        counts.observed != 0)
    {
        source.last_current_at_milliseconds = session.completion.captured_at_milliseconds;
    }

    recomputeAffectedMetrics(
        &session.staging,
        session,
        (reset_reason_mask & protocol.SourceResetReason.incarnation_changed) != 0,
    );
}

fn commitFinalizedSource(
    session: *state.Session,
    input: *const protocol.FinalizeInput,
    source_index: usize,
    next_revision: u64,
    next_committed: u64,
    semantic_fingerprint: u64,
) void {
    std.mem.swap(catalog.Bank, &session.active, &session.staging);
    inventory.commitStaged(session);
    session.committed_generation = next_committed;
    session.semantic_fingerprint = semantic_fingerprint;
    session.state_revision = next_revision;
    session.last_operation_epoch = input.operation_epoch;
    session.last_command_at_milliseconds = input.command_at_milliseconds;
    session.captured_at_milliseconds = session.completion.captured_at_milliseconds;
    session.phase = if (hasRetained(session) and
        session.completion.status != @intFromEnum(protocol.SourceStatus.complete))
        .failed_retained
    else
        .ready;
    session.source_modes[source_index].completion_committed = true;
    session.clearCompletion();
}

pub fn abort(session: *state.Session, input: *const protocol.AbortInput) ResultCode {
    state.lock(&session.mutex);
    defer state.unlock(&session.mutex);
    if (!validFinalize(session, input)) return .abi_mismatch;
    const next_revision = session.nextRevision() orelse return .unavailable;
    session.last_operation_epoch = input.operation_epoch;
    session.last_command_at_milliseconds = input.command_at_milliseconds;
    session.state_revision = next_revision;
    session.phase = if (session.committed_generation == 0) .catalog_ready else .ready;
    session.clearCompletion();
    return .ok;
}

pub fn recomputeMetrics(session: *state.Session) void {
    recomputeBank(&session.active);
}

pub fn recomputeBank(bank: *catalog.Bank) void {
    for (bank.metrics[0..bank.metric_count]) |*metric| {
        recomputeMetric(bank, metric);
    }
}

pub fn semanticFingerprint(session: *const state.Session) u64 {
    return semanticFingerprintFor(
        &session.active,
        session.catalog_fingerprint,
        session.gpu_active,
    );
}

pub fn semanticFingerprintFor(
    bank: *const catalog.Bank,
    catalog_fingerprint: u64,
    gpu_records: []const state.GpuRecord,
) u64 {
    var hash = std.hash.Wyhash.init(0x726d_6d65_7472_7374);
    hash.update(std.mem.asBytes(&catalog_fingerprint));
    for (bank.metrics[0..bank.metric_count]) |metric| {
        hash.update(std.mem.asBytes(&metric.output.semantic_fingerprint));
    }
    for (gpu_records) |gpu| if (gpu.active) {
        hash.update(std.mem.asBytes(&gpu.output.semantic_fingerprint));
    };
    const result = hash.final();
    return if (result == 0) 1 else result;
}

pub fn sourceOutput(source: catalog.SourceRecord) protocol.SourceOutput {
    var output = protocol.SourceOutput{
        .struct_size = @sizeOf(protocol.SourceOutput),
        .flags = source.definition.flags,
        .source_handle = source.definition.source_handle,
        .source_incarnation = source.source_incarnation,
        .source_generation = source.source_generation,
        .source_observation_sequence = source.last_observation_sequence,
        .capability_generation = source.capability_generation,
        .last_attempt_at_milliseconds = source.last_attempt_at_milliseconds,
        .last_current_at_milliseconds = source.last_current_at_milliseconds,
        .capability_mask = source.definition.capability_mask,
        .source_role = source.definition.source_role,
        .priority = source.definition.priority,
        .status = @intFromEnum(source.status),
        .retention_policy = source.definition.retention_policy,
        .requested_count = source.requested_count,
        .observed_count = source.observed_count,
        .skipped_count = source.skipped_count,
        .overflow_count = source.overflow_count,
        .reset_count = source.reset_count,
        .last_reset_reason_mask = source.last_reset_reason_mask,
        .definition_fingerprint = source.definition.semantic_fingerprint,
        .state_fingerprint = 0,
    };
    const value = std.hash.Wyhash.hash(
        0x726d_6d65_7472_7372,
        std.mem.asBytes(&output),
    );
    output.state_fingerprint = if (value == 0) 1 else value;
    return output;
}

fn validHeader(session: *const state.Session, header: *const protocol.CompletionHeader) bool {
    return header.abi_version == protocol.abi_version and
        header.struct_size == @sizeOf(protocol.CompletionHeader) and
        header.configuration_generation == session.config.generation and
        header.catalog_generation == session.catalog_generation and
        header.operation_epoch != 0 and header.plan_epoch != 0 and
        header.source_handle != 0 and header.source_incarnation != 0 and
        header.source_observation_sequence != 0 and
        header.capability_generation != 0 and header.plan_token_fingerprint != 0 and
        header.requested_rule_count <= session.config.maximum_requested_count and
        header.observation_count <= session.config.maximum_observation_count and
        header.gpu_inventory_count <= session.config.maximum_gpu_adapter_count and
        header.expected_gpu_inventory_count <= session.config.maximum_gpu_adapter_count and
        header.cpu_counter_count <= 1 and
        header.captured_at_milliseconds != 0 and
        header.captured_at_milliseconds >= session.captured_at_milliseconds and
        protocol.knownEnum(protocol.SourceStatus, header.status) and
        header.flags == 0 and allZero(header.reserved) and
        session.futureTimeValid(
            header.captured_at_milliseconds,
            header.command_at_milliseconds,
        );
}

fn validCompletionShape(header: *const protocol.CompletionHeader) bool {
    const status: protocol.SourceStatus = @enumFromInt(header.status);
    const supplied = std.math.add(
        u32,
        header.observation_count,
        header.cpu_counter_count,
    ) catch return false;
    const metric_shape = header.expected_gpu_inventory_count == 0 and
        header.gpu_inventory_count == 0;
    return switch (status) {
        .complete => header.overflow_count == 0 and
            if (metric_shape)
                supplied == header.requested_rule_count
            else
                header.requested_rule_count == 0 and
                    header.gpu_inventory_count == header.expected_gpu_inventory_count,
        .partial => header.overflow_count != 0 and
            if (metric_shape)
                (std.math.add(u32, supplied, header.overflow_count) catch return false) ==
                    header.requested_rule_count
            else
                header.requested_rule_count == 0 and
                    (std.math.add(
                        u32,
                        header.gpu_inventory_count,
                        header.overflow_count,
                    ) catch return false) ==
                        header.expected_gpu_inventory_count,
        .unavailable, .unsupported, .skipped => supplied == 0 and
            header.gpu_inventory_count == 0 and
            header.expected_gpu_inventory_count == 0 and
            header.overflow_count == 0,
    };
}

fn validateObservation(
    session: *const state.Session,
    observation: *const protocol.ObservationInput,
) ?u32 {
    if (observation.struct_size != @sizeOf(protocol.ObservationInput) or
        observation.flags != 0 or observation.rule_handle == 0 or
        observation.metric_handle == 0 or observation.source_handle == 0 or
        observation.scope_handle == 0 or observation.reserved_u32 != 0 or
        observation.source_handle != session.completion.source_handle or
        observation.source_observation_sequence !=
            session.completion.source_observation_sequence or
        observation.observed_at_milliseconds == 0 or
        observation.observed_at_milliseconds > session.completion.captured_at_milliseconds or
        !session.futureTimeValid(
            observation.observed_at_milliseconds,
            session.completion.command_at_milliseconds,
        ) or
        !protocol.knownEnum(protocol.ObservationStatus, observation.status) or
        !protocol.knownEnum(protocol.ValueKind, observation.value_kind) or
        (observation.valid_mask & ~protocol.ObservationValid.known) != 0 or
        (observation.valid_mask & protocol.ObservationValid.required) !=
            protocol.ObservationValid.required)
    {
        return null;
    }
    const rule_index = session.active.findRule(observation.rule_handle) orelse return null;
    if (!session.requested_rule_flags[rule_index]) return null;
    const active_rule = session.active.rules[rule_index];
    if (active_rule.observed_at_milliseconds != 0 and
        observation.observed_at_milliseconds < active_rule.observed_at_milliseconds)
    {
        return null;
    }
    const definition = active_rule.definition;
    if (definition.metric_handle != observation.metric_handle or
        definition.source_handle != observation.source_handle or
        definition.scope_handle != observation.scope_handle or
        definition.value_kind != observation.value_kind or
        (observation.capability_mask & ~definition.capability_mask) != 0)
    {
        return null;
    }
    const current = observation.status == @intFromEnum(protocol.ObservationStatus.current);
    if (current) {
        if ((observation.valid_mask & protocol.ObservationValid.value) == 0 or
            !protocol.validValue(
                definition.value_kind,
                definition.flags,
                definition.minimum_value_bits,
                definition.maximum_value_bits,
                observation.value_bits,
            ))
        {
            return null;
        }
    } else if ((observation.valid_mask & protocol.ObservationValid.value) != 0 or
        observation.value_bits != 0 or observation.sample_duration_milliseconds != 0)
    {
        return null;
    }
    return rule_index;
}

fn validateCpuCounter(
    session: *const state.Session,
    input: *const protocol.CpuCounterInput,
) ?u32 {
    if (input.struct_size != @sizeOf(protocol.CpuCounterInput) or input.flags != 0 or
        input.rule_handle == 0 or input.metric_handle == 0 or input.source_handle == 0 or
        input.source_incarnation != session.completion.source_incarnation or
        input.source_observation_sequence != session.completion.source_observation_sequence or
        input.observed_at_milliseconds != session.completion.captured_at_milliseconds or
        input.counter_contract_version != protocol.cpu_counter_contract_version or
        !allZero(input.reserved) or
        !protocol.knownEnum(protocol.ObservationStatus, input.status) or
        (input.valid_mask & ~protocol.CpuCounterValid.known) != 0)
    {
        return null;
    }
    const rule_index = session.active.findRule(input.rule_handle) orelse return null;
    if (!session.requested_rule_flags[rule_index]) return null;
    const rule = session.active.rules[rule_index].definition;
    if (rule.metric_handle != input.metric_handle or
        rule.source_handle != input.source_handle or
        rule.metric_kind != @intFromEnum(protocol.MetricKind.cpu_usage) or
        rule.value_kind != @intFromEnum(protocol.ValueKind.float64) or
        (input.capability_mask & ~rule.capability_mask) != 0)
    {
        return null;
    }
    if (input.status == @intFromEnum(protocol.ObservationStatus.current)) {
        if (input.valid_mask != protocol.CpuCounterValid.required or
            input.monotonic_ticks == 0 or input.monotonic_ticks_per_second == 0 or
            input.kernel_ticks < input.idle_ticks)
        {
            return null;
        }
    } else if (input.valid_mask != 0 or input.monotonic_ticks != 0 or
        input.monotonic_ticks_per_second != 0 or input.idle_ticks != 0 or
        input.kernel_ticks != 0 or input.user_ticks != 0 or input.capability_mask != 0)
    {
        return null;
    }
    return rule_index;
}

fn validFinalize(session: *const state.Session, input: *const protocol.FinalizeInput) bool {
    return session.phase == .completion_open and
        input.abi_version == protocol.abi_version and
        input.struct_size == @sizeOf(protocol.FinalizeInput) and
        input.configuration_generation == session.config.generation and
        input.catalog_generation == session.catalog_generation and
        input.operation_epoch == session.completion.operation_epoch and
        input.plan_epoch == session.completion.plan_epoch and
        input.source_handle == session.completion.source_handle and
        input.command_at_milliseconds >= session.completion.command_at_milliseconds and
        input.flags == 0 and allZero(input.reserved);
}

fn allRequestedAccountedFor(session: *const state.Session) bool {
    const status: protocol.SourceStatus = @enumFromInt(session.completion.status);
    const source_index = session.active.findSource(session.completion.source_handle) orelse
        return false;
    if (session.active.sources[source_index].definition.source_role ==
        @intFromEnum(protocol.SourceRole.gpu_inventory))
    {
        return session.requested_count == 0;
    }
    var ordinal: u32 = 0;
    var missing: u32 = 0;
    while (ordinal < session.requested_count) : (ordinal += 1) {
        const rule_index = session.requested_rule_indices[ordinal];
        if (session.observation_slot_by_rule[rule_index] == catalog.no_index) missing += 1;
    }
    return switch (status) {
        .complete => missing == 0,
        .partial => missing == session.completion.overflow_count,
        .unavailable, .unsupported, .skipped => missing == session.requested_count,
    };
}

fn sourceResetReason(source: *const catalog.SourceRecord, session: *const state.Session) u32 {
    var reason: u32 = 0;
    if (source.source_incarnation != 0 and
        source.source_incarnation != session.completion.source_incarnation)
    {
        reason |= protocol.SourceResetReason.incarnation_changed;
    }
    if (!session.pending_cpu_counter_present or
        session.pending_cpu_counter.status != @intFromEnum(protocol.ObservationStatus.current) or
        !source.cpu_baseline_valid or source.source_incarnation !=
        session.pending_cpu_counter.source_incarnation)
    {
        return reason;
    }
    const input = session.pending_cpu_counter;
    if (input.idle_ticks < source.cpu_idle_ticks or
        input.kernel_ticks < source.cpu_kernel_ticks or
        input.user_ticks < source.cpu_user_ticks)
    {
        reason |= protocol.SourceResetReason.counter_regressed;
    }
    if (input.monotonic_ticks <= source.cpu_monotonic_ticks) {
        reason |= protocol.SourceResetReason.monotonic_regressed;
    }
    if (source.cpu_counter_contract_version != 0 and
        source.cpu_counter_contract_version != input.counter_contract_version or
        source.cpu_monotonic_ticks_per_second != 0 and
            source.cpu_monotonic_ticks_per_second != input.monotonic_ticks_per_second)
    {
        reason |= protocol.SourceResetReason.tick_frequency_changed;
    }
    _ = std.math.add(
        u64,
        input.kernel_ticks -| source.cpu_kernel_ticks,
        input.user_ticks -| source.cpu_user_ticks,
    ) catch {
        reason |= protocol.SourceResetReason.arithmetic_overflow;
    };
    return reason;
}

fn applyRequestedRules(
    bank: *catalog.Bank,
    session: *const state.Session,
    source_generation: u64,
) void {
    var ordinal: u32 = 0;
    while (ordinal < session.requested_count) : (ordinal += 1) {
        const rule_index = session.requested_rule_indices[ordinal];
        const rule = &bank.rules[rule_index];
        const slot = session.observation_slot_by_rule[rule_index];
        if (slot == catalog.no_index) {
            applyMissing(
                rule,
                .unavailable,
                session.completion.capability_generation,
            );
            continue;
        }
        const observation = session.observations[slot];
        const status: protocol.ObservationStatus = @enumFromInt(observation.status);
        if (status == .current) {
            rule.source_generation = source_generation;
            rule.observed_at_milliseconds = observation.observed_at_milliseconds;
            rule.value_bits = observation.value_bits;
            rule.capability_mask = observation.capability_mask;
            rule.quality = observation.quality;
            rule.sample_duration_milliseconds = observation.sample_duration_milliseconds;
            rule.capability_generation = session.completion.capability_generation;
            rule.unsupported_until_capability_generation = 0;
            rule.status = .current;
            rule.has_last_good = true;
        } else {
            applyMissing(rule, switch (status) {
                .unavailable => .unavailable,
                .unsupported => .unsupported,
                .skipped => .skipped,
                .current => unreachable,
            }, session.completion.capability_generation);
        }
    }
}

pub fn synchronizeBank(target: *catalog.Bank, source: *const catalog.Bank) void {
    @memcpy(target.sources, source.sources);
    @memcpy(target.rules, source.rules);
    @memcpy(target.metrics, source.metrics);
    @memcpy(target.source_index, source.source_index);
    @memcpy(target.rule_index, source.rule_index);
    @memcpy(target.metric_index, source.metric_index);
    target.source_count = source.source_count;
    target.rule_count = source.rule_count;
    target.metric_count = source.metric_count;
    target.gpu_inventory_source_count = source.gpu_inventory_source_count;
}

fn recomputeAffectedMetrics(
    bank: *catalog.Bank,
    session: *const state.Session,
    source_reset: bool,
) void {
    if (source_reset) {
        for (bank.metrics[0..bank.metric_count]) |*metric| {
            var offset: u32 = 0;
            while (offset < metric.rule_count) : (offset += 1) {
                const rule = bank.rules[metric.first_rule_index + offset];
                if (rule.definition.source_handle == session.completion.source_handle) {
                    recomputeMetric(bank, metric);
                    break;
                }
            }
        }
        return;
    }

    var ordinal: u32 = 0;
    while (ordinal < session.requested_count) : (ordinal += 1) {
        const rule = bank.rules[session.requested_rule_indices[ordinal]];
        const metric_index = bank.findMetric(rule.definition.metric_handle).?;
        recomputeMetric(bank, &bank.metrics[metric_index]);
    }
}

fn recomputeMetric(bank: *const catalog.Bank, metric: *catalog.MetricRecord) void {
    var selected: ?*const catalog.RuleRecord = null;
    var retained: ?*const catalog.RuleRecord = null;
    var fallback: ?*const catalog.RuleRecord = null;
    var offset: u32 = 0;
    while (offset < metric.rule_count) : (offset += 1) {
        const rule = &bank.rules[metric.first_rule_index + offset];
        if (fallback == null) fallback = rule;
        if (rule.status == .current) {
            selected = rule;
            break;
        }
        if (retained == null and rule.status == .retained) retained = rule;
    }
    const winner = selected orelse retained orelse fallback.?;
    metric.output = outputFromRule(winner);
}

fn invalidateCurrentRulesForSource(
    bank: *catalog.Bank,
    source_handle: u64,
    capability_generation: u64,
) void {
    for (bank.rules[0..bank.rule_count]) |*rule| {
        if (rule.definition.source_handle == source_handle and rule.status == .current) {
            applyMissing(rule, .unavailable, capability_generation);
        }
    }
}

fn clearCpuBaseline(source: *catalog.SourceRecord) void {
    source.cpu_baseline_valid = false;
    source.cpu_idle_ticks = 0;
    source.cpu_kernel_ticks = 0;
    source.cpu_user_ticks = 0;
    source.cpu_monotonic_ticks = 0;
    source.cpu_monotonic_ticks_per_second = 0;
    source.cpu_counter_contract_version = 0;
}

fn applyMissing(
    rule: *catalog.RuleRecord,
    missing_status: protocol.MetricStatus,
    capability_generation: u64,
) void {
    rule.capability_generation = capability_generation;
    rule.unsupported_until_capability_generation =
        if (missing_status == .unsupported) capability_generation else 0;
    if (rule.definition.retention_policy ==
        @intFromEnum(protocol.RetentionPolicy.retain_last_good) and rule.has_last_good)
    {
        rule.status = .retained;
        return;
    }
    rule.source_generation = 0;
    rule.value_bits = 0;
    rule.capability_mask = 0;
    rule.quality = 0;
    rule.sample_duration_milliseconds = 0;
    rule.status = missing_status;
    rule.has_last_good = false;
}

fn outputFromRule(rule: *const catalog.RuleRecord) protocol.MetricOutput {
    const definition = rule.definition;
    const has_value = rule.has_last_good;
    var output = protocol.MetricOutput{
        .struct_size = @sizeOf(protocol.MetricOutput),
        .flags = definition.flags,
        .metric_handle = definition.metric_handle,
        .winning_rule_handle = definition.rule_handle,
        .source_handle = definition.source_handle,
        .scope_handle = definition.scope_handle,
        .source_generation = rule.source_generation,
        .observed_at_milliseconds = if (has_value) rule.observed_at_milliseconds else 0,
        .value_bits = rule.value_bits,
        .capability_mask = rule.capability_mask,
        .metric_kind = definition.metric_kind,
        .scope_kind = definition.scope_kind,
        .value_kind = definition.value_kind,
        .status = @intFromEnum(rule.status),
        .quality = rule.quality,
        .reserved_u32 = 0,
        .semantic_fingerprint = 0,
        .sample_duration_milliseconds = rule.sample_duration_milliseconds,
    };
    const semantic_fields = [_]u64{
        output.flags,
        output.metric_handle,
        output.winning_rule_handle,
        output.source_handle,
        output.scope_handle,
        output.value_bits,
        output.capability_mask,
        output.metric_kind,
        output.scope_kind,
        output.value_kind,
        output.status,
        output.quality,
    };
    const value = std.hash.Wyhash.hash(
        0x726d_6d65_7472_6f75,
        std.mem.asBytes(&semantic_fields),
    );
    output.semantic_fingerprint = if (value == 0) 1 else value;
    return output;
}

fn hasRetained(session: *const state.Session) bool {
    for (session.active.metrics[0..session.active.metric_count]) |metric| {
        if (metric.output.status == @intFromEnum(protocol.MetricStatus.retained)) return true;
    }
    for (session.gpu_active) |gpu| if (gpu.active and
        gpu.output.status == @intFromEnum(protocol.InventoryStatus.retained))
    {
        return true;
    };
    return false;
}

fn allZero(values: anytype) bool {
    for (values) |value| if (value != 0) return false;
    return true;
}

test "completion shape rejects overflowing supplied and inventory counts" {
    var metric = std.mem.zeroes(protocol.CompletionHeader);
    metric.status = @intFromEnum(protocol.SourceStatus.partial);
    metric.observation_count = std.math.maxInt(u32);
    metric.cpu_counter_count = 1;
    metric.overflow_count = 1;
    try std.testing.expect(!validCompletionShape(&metric));

    var inventory_input = std.mem.zeroes(protocol.CompletionHeader);
    inventory_input.status = @intFromEnum(protocol.SourceStatus.partial);
    inventory_input.gpu_inventory_count = std.math.maxInt(u32);
    inventory_input.overflow_count = 1;
    inventory_input.expected_gpu_inventory_count = 0;
    try std.testing.expect(!validCompletionShape(&inventory_input));
}
