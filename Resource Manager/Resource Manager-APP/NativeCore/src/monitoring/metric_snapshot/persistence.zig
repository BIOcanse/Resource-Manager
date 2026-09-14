const std = @import("std");
const protocol = @import("protocol.zig");
const catalog = @import("catalog.zig");
const state = @import("state.zig");
const inventory = @import("inventory.zig");
const observation = @import("observation.zig");
const session_module = @import("session.zig");
const ResultCode = @import("../../common/result_codes.zig").ResultCode;

pub fn exportState(
    session: *state.Session,
    input: *const protocol.ReadInput,
    header: *protocol.PersistenceHeader,
    sources: []protocol.SourcePersistenceOutput,
    rules: []protocol.RuleStateOutput,
    gpu_inventory: []protocol.GpuInventoryOutput,
) ResultCode {
    state.lock(&session.mutex);
    defer state.unlock(&session.mutex);
    if (!validRead(session, input)) return .stale_frame;
    if (session.phase == .completion_open) return .unavailable;
    const gpu_count = inventory.activeCount(session);
    if (sources.len < session.active.source_count or rules.len < session.active.rule_count or
        gpu_inventory.len < gpu_count)
    {
        return .buffer_too_small;
    }

    @memset(sources, std.mem.zeroes(protocol.SourcePersistenceOutput));
    @memset(rules, std.mem.zeroes(protocol.RuleStateOutput));
    @memset(gpu_inventory, std.mem.zeroes(protocol.GpuInventoryOutput));
    for (session.active.sources[0..session.active.source_count], 0..) |source, ordinal| {
        sources[ordinal] = sourceOutput(source);
    }
    for (session.active.rules[0..session.active.rule_count], 0..) |rule, ordinal| {
        rules[ordinal] = session_module.ruleOutput(rule);
    }
    var gpu_ordinal: usize = 0;
    for (session.gpu_active) |gpu| {
        if (!gpu.active) continue;
        gpu_inventory[gpu_ordinal] = gpu.output;
        gpu_ordinal += 1;
    }
    header.* = .{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.PersistenceHeader),
        .configuration_generation = session.config.generation,
        .catalog_generation = session.catalog_generation,
        .state_revision = session.state_revision,
        .committed_generation = session.committed_generation,
        .last_operation_epoch = session.last_operation_epoch,
        .last_plan_epoch = session.last_plan_epoch,
        .last_command_at_milliseconds = session.last_command_at_milliseconds,
        .captured_at_milliseconds = session.captured_at_milliseconds,
        .catalog_fingerprint = session.catalog_fingerprint,
        .rule_state_count = session.active.rule_count,
        .source_state_count = session.active.source_count,
        .gpu_adapter_count = gpu_count,
        .phase = @intFromEnum(session.phase),
        .semantic_fingerprint = session.semantic_fingerprint,
        .checksum = 0,
        .flags = 0,
        .reserved = .{ 0, 0, 0 },
    };
    header.checksum = checksum(
        header,
        sources[0..session.active.source_count],
        rules[0..session.active.rule_count],
        gpu_inventory[0..gpu_count],
    );
    return .ok;
}

pub fn importState(
    session: *state.Session,
    input: *const protocol.PersistenceInput,
    header: *const protocol.PersistenceHeader,
    sources: []const protocol.SourcePersistenceOutput,
    rules: []const protocol.RuleStateOutput,
    gpu_inventory: []const protocol.GpuInventoryOutput,
) ResultCode {
    state.lock(&session.mutex);
    defer state.unlock(&session.mutex);
    if (!validImportHeader(session, input, header, sources, rules, gpu_inventory)) {
        return .abi_mismatch;
    }
    if (session.phase == .completion_open) return .unavailable;
    if (!session.operationAdvances(input.operation_epoch, input.command_at_milliseconds) or
        input.operation_epoch <= header.last_operation_epoch or
        input.command_at_milliseconds < header.last_command_at_milliseconds or
        header.state_revision < session.state_revision or
        header.last_plan_epoch < session.last_plan_epoch or
        header.captured_at_milliseconds < session.captured_at_milliseconds)
    {
        return .stale_frame;
    }
    if (checksum(header, sources, rules, gpu_inventory) != header.checksum) {
        return .invalid_argument;
    }

    observation.synchronizeBank(&session.staging, &session.active);
    var published = false;
    defer if (!published) {
        observation.synchronizeBank(&session.staging, &session.active);
        inventory.synchronizeStagingWithActive(session);
    };
    var inventory_source_ordinal: ?usize = null;
    for (sources, 0..) |source_input, ordinal| {
        const definition = session.active.sources[ordinal].definition;
        const active_source = session.active.sources[ordinal];
        if (!validSourceState(
            &source_input,
            &definition,
            &active_source,
            header.captured_at_milliseconds,
            session.config.maximum_gpu_adapter_count,
        )) {
            return .invalid_argument;
        }
        session.staging.sources[ordinal] = sourceRecord(definition, source_input);
        if (definition.source_role == @intFromEnum(protocol.SourceRole.gpu_inventory)) {
            inventory_source_ordinal = ordinal;
        }
    }
    for (rules, 0..) |rule_input, ordinal| {
        const definition = session.active.rules[ordinal].definition;
        const source_index = session.staging.findSource(definition.source_handle) orelse
            return .invalid_argument;
        if (!validRuleState(
            &rule_input,
            &definition,
            &session.active.rules[ordinal],
            &session.staging.sources[source_index],
            header.captured_at_milliseconds,
        )) {
            return .invalid_argument;
        }
        session.staging.rules[ordinal] = ruleRecord(definition, rule_input);
    }
    if (inventory_source_ordinal) |gpu_source_ordinal| {
        if (inventory.stageOutputs(
            session,
            gpu_inventory,
            &sources[gpu_source_ordinal],
            &session.active.sources[gpu_source_ordinal].definition,
            header.captured_at_milliseconds,
        ) != .ok) {
            return .invalid_argument;
        }
    } else {
        if (gpu_inventory.len != 0) return .invalid_argument;
        @memset(session.gpu_staging, .{});
        @memset(session.gpu_staging_index, catalog.no_index);
        @memset(session.gpu_staging_luid_index, catalog.no_index);
        @memset(session.gpu_staging_key_index, catalog.no_index);
        session.gpu_staging_count = 0;
        session.gpu_staging_prepared = true;
    }
    observation.recomputeBank(&session.staging);
    if (!validImportedPhase(
        @enumFromInt(header.phase),
        &session.staging,
        inventory.finalizedRows(session),
    )) return .invalid_argument;
    const restored_fingerprint = observation.semanticFingerprintFor(
        &session.staging,
        session.catalog_fingerprint,
        session.gpu_staging,
    );
    if (restored_fingerprint != header.semantic_fingerprint) return .invalid_argument;
    const next_revision = std.math.add(u64, header.state_revision, 1) catch
        return .unavailable;

    std.mem.swap(catalog.Bank, &session.active, &session.staging);
    inventory.commitStaged(session);
    session.state_revision = next_revision;
    session.committed_generation = header.committed_generation;
    session.last_operation_epoch = input.operation_epoch;
    session.last_plan_epoch = header.last_plan_epoch;
    session.last_command_at_milliseconds = input.command_at_milliseconds;
    session.captured_at_milliseconds = header.captured_at_milliseconds;
    session.semantic_fingerprint = restored_fingerprint;
    session.phase = @enumFromInt(header.phase);
    session.clearPlanAuthority();
    session.clearCompletion();
    published = true;
    return .ok;
}

pub fn sourceOutput(source: catalog.SourceRecord) protocol.SourcePersistenceOutput {
    var output = protocol.SourcePersistenceOutput{
        .struct_size = @sizeOf(protocol.SourcePersistenceOutput),
        .flags = source.definition.flags,
        .source_handle = source.definition.source_handle,
        .definition_fingerprint = source.definition.semantic_fingerprint,
        .source_incarnation = source.source_incarnation,
        .source_generation = source.source_generation,
        .source_observation_sequence = source.last_observation_sequence,
        .capability_generation = source.capability_generation,
        .last_attempt_at_milliseconds = source.last_attempt_at_milliseconds,
        .last_current_at_milliseconds = source.last_current_at_milliseconds,
        .capability_mask = source.definition.capability_mask,
        .cpu_idle_ticks = source.cpu_idle_ticks,
        .cpu_kernel_ticks = source.cpu_kernel_ticks,
        .cpu_user_ticks = source.cpu_user_ticks,
        .cpu_monotonic_ticks = source.cpu_monotonic_ticks,
        .cpu_monotonic_ticks_per_second = source.cpu_monotonic_ticks_per_second,
        .requested_count = source.requested_count,
        .observed_count = source.observed_count,
        .skipped_count = source.skipped_count,
        .overflow_count = source.overflow_count,
        .reset_count = source.reset_count,
        .last_reset_reason_mask = source.last_reset_reason_mask,
        .status = @intFromEnum(source.status),
        .cpu_baseline_valid = @intFromBool(source.cpu_baseline_valid),
        .cpu_counter_contract_version = source.cpu_counter_contract_version,
        .reserved_u32 = 0,
        .state_fingerprint = 0,
        .gpu_inventory_count = source.gpu_inventory_count,
        .gpu_retained_count = source.gpu_retained_count,
        .reserved = 0,
    };
    output.state_fingerprint = sourceFingerprint(&output);
    return output;
}

fn validRead(session: *const state.Session, input: *const protocol.ReadInput) bool {
    return input.abi_version == protocol.abi_version and
        input.struct_size == @sizeOf(protocol.ReadInput) and
        input.configuration_generation == session.config.generation and
        input.catalog_generation == session.catalog_generation and
        input.catalog_fingerprint == session.catalog_fingerprint and
        input.state_revision == session.state_revision and
        input.committed_generation == session.committed_generation and
        input.flags == 0 and allZero(input.reserved);
}

fn validImportHeader(
    session: *const state.Session,
    input: *const protocol.PersistenceInput,
    header: *const protocol.PersistenceHeader,
    sources: []const protocol.SourcePersistenceOutput,
    rules: []const protocol.RuleStateOutput,
    gpu_inventory: []const protocol.GpuInventoryOutput,
) bool {
    if (input.abi_version != protocol.abi_version or
        input.struct_size != @sizeOf(protocol.PersistenceInput) or
        input.configuration_generation != session.config.generation or
        input.catalog_generation != session.catalog_generation or
        input.operation_epoch == 0 or input.flags != 0 or !allZero(input.reserved))
    {
        return false;
    }
    if (header.abi_version != protocol.abi_version or
        header.struct_size != @sizeOf(protocol.PersistenceHeader) or
        header.configuration_generation != session.config.generation or
        header.catalog_generation != session.catalog_generation or
        header.catalog_fingerprint != session.catalog_fingerprint or
        header.state_revision == 0 or header.committed_generation == 0 or
        header.committed_generation < session.committed_generation or
        header.semantic_fingerprint == 0 or
        header.checksum == 0 or header.flags != 0 or !allZero(header.reserved) or
        header.source_state_count != sources.len or
        header.rule_state_count != rules.len or
        header.gpu_adapter_count != gpu_inventory.len or
        sources.len != session.active.source_count or
        rules.len != session.active.rule_count or
        gpu_inventory.len > session.config.maximum_persistence_gpu_count or
        sources.len > session.config.maximum_persistence_source_count or
        rules.len > session.config.maximum_persistence_rule_count or
        !protocol.knownEnum(protocol.Phase, header.phase))
    {
        return false;
    }
    if (header.committed_generation == session.committed_generation and
        session.semantic_fingerprint != 0 and
        header.semantic_fingerprint != session.semantic_fingerprint)
    {
        return false;
    }
    const phase: protocol.Phase = @enumFromInt(header.phase);
    if (phase != .catalog_ready and phase != .ready and phase != .failed_retained) {
        return false;
    }
    return session.futureTimeValid(header.captured_at_milliseconds, input.command_at_milliseconds);
}

fn validSourceState(
    input: *const protocol.SourcePersistenceOutput,
    definition: *const protocol.SourcePolicyInput,
    active: *const catalog.SourceRecord,
    captured_at_milliseconds: u64,
    maximum_gpu_adapter_count: u32,
) bool {
    if (!validSourceEnvelope(input, definition) or
        !validSourceMonotonicity(input, active, captured_at_milliseconds))
    {
        return false;
    }
    if (input.source_generation == 0) {
        return validEmptySourceState(input);
    }
    if (input.source_incarnation == 0 or input.source_observation_sequence == 0 or
        input.last_attempt_at_milliseconds == 0 or input.capability_generation == 0 or
        input.last_current_at_milliseconds > input.last_attempt_at_milliseconds)
    {
        return false;
    }
    const source_role: protocol.SourceRole = @enumFromInt(definition.source_role);
    return switch (source_role) {
        .metrics => validMetricSourceState(input) and validCpuBaseline(input),
        .gpu_inventory => validGpuSourceState(
            input,
            definition,
            maximum_gpu_adapter_count,
        ),
    };
}

fn validSourceEnvelope(
    input: *const protocol.SourcePersistenceOutput,
    definition: *const protocol.SourcePolicyInput,
) bool {
    return input.struct_size == @sizeOf(protocol.SourcePersistenceOutput) and
        input.flags == definition.flags and input.source_handle == definition.source_handle and
        input.definition_fingerprint == definition.semantic_fingerprint and
        input.capability_mask == definition.capability_mask and
        (input.last_reset_reason_mask & ~protocol.SourceResetReason.known) == 0 and
        input.cpu_baseline_valid <= 1 and protocol.knownEnum(protocol.SourceStatus, input.status) and
        input.reserved_u32 == 0 and input.reserved == 0 and
        input.state_fingerprint != 0 and sourceFingerprint(input) == input.state_fingerprint;
}

fn validSourceMonotonicity(
    input: *const protocol.SourcePersistenceOutput,
    active: *const catalog.SourceRecord,
    captured_at_milliseconds: u64,
) bool {
    if (input.source_generation < active.source_generation or
        input.capability_generation < active.capability_generation or
        input.reset_count < active.reset_count or
        input.last_attempt_at_milliseconds > captured_at_milliseconds or
        input.last_current_at_milliseconds > captured_at_milliseconds)
    {
        return false;
    }
    if (input.source_generation == active.source_generation and
        input.state_fingerprint != sourceOutput(active.*).state_fingerprint)
    {
        return false;
    }
    if (input.source_incarnation == active.source_incarnation and
        input.source_observation_sequence < active.last_observation_sequence)
    {
        return false;
    }
    const source_generation_delta = input.source_generation - active.source_generation;
    const reset_count_delta = input.reset_count - active.reset_count;
    if ((active.source_generation == 0 and reset_count_delta != 0) or
        reset_count_delta > source_generation_delta or
        (input.last_reset_reason_mask != 0 and reset_count_delta == 0) or
        (source_generation_delta == 1 and
            (reset_count_delta == 1) != (input.last_reset_reason_mask != 0)))
    {
        return false;
    }
    return active.source_incarnation == 0 or
        input.source_incarnation == active.source_incarnation or
        (input.reset_count > active.reset_count and
            (input.last_reset_reason_mask & protocol.SourceResetReason.incarnation_changed) != 0);
}

fn validEmptySourceState(input: *const protocol.SourcePersistenceOutput) bool {
    return input.status == @intFromEnum(protocol.SourceStatus.unavailable) and
        input.source_incarnation == 0 and input.source_observation_sequence == 0 and
        input.last_attempt_at_milliseconds == 0 and input.last_current_at_milliseconds == 0 and
        input.requested_count == 0 and input.capability_generation == 0 and
        input.observed_count == 0 and input.skipped_count == 0 and input.overflow_count == 0 and
        input.reset_count == 0 and input.last_reset_reason_mask == 0 and
        input.cpu_baseline_valid == 0 and input.gpu_inventory_count == 0 and
        input.gpu_retained_count == 0 and cpuBaselineEmpty(input);
}

fn validMetricSourceState(input: *const protocol.SourcePersistenceOutput) bool {
    if (input.gpu_inventory_count != 0 or input.gpu_retained_count != 0) return false;
    const observed_and_skipped = std.math.add(
        u32,
        input.observed_count,
        input.skipped_count,
    ) catch return false;
    const accounted = std.math.add(
        u32,
        observed_and_skipped,
        input.overflow_count,
    ) catch return false;
    if (accounted > input.requested_count) return false;
    const status: protocol.SourceStatus = @enumFromInt(input.status);
    return switch (status) {
        .complete => input.overflow_count == 0,
        .partial => input.overflow_count != 0,
        .unavailable, .unsupported => input.observed_count == 0 and
            input.skipped_count == 0 and input.overflow_count == 0,
        .skipped => input.observed_count == 0 and input.overflow_count == 0 and
            input.skipped_count == input.requested_count,
    };
}

fn validGpuSourceState(
    input: *const protocol.SourcePersistenceOutput,
    definition: *const protocol.SourcePolicyInput,
    maximum_gpu_adapter_count: u32,
) bool {
    if (input.requested_count != 0 or input.skipped_count != 0 or
        input.cpu_baseline_valid != 0 or !cpuBaselineEmpty(input) or
        input.gpu_retained_count > input.gpu_inventory_count)
    {
        return false;
    }
    const retained = definition.retention_policy ==
        @intFromEnum(protocol.RetentionPolicy.retain_last_good);
    const status: protocol.SourceStatus = @enumFromInt(input.status);
    const accounted = std.math.add(
        u32,
        input.observed_count,
        input.overflow_count,
    ) catch return false;
    if (input.observed_count > maximum_gpu_adapter_count or
        input.overflow_count > maximum_gpu_adapter_count or
        accounted > maximum_gpu_adapter_count)
    {
        return false;
    }
    return switch (status) {
        .complete => input.overflow_count == 0 and
            input.observed_count == input.gpu_inventory_count and
            input.gpu_retained_count == 0 and
            input.last_current_at_milliseconds == input.last_attempt_at_milliseconds,
        .partial => input.overflow_count != 0 and
            input.gpu_retained_count == input.gpu_inventory_count and
            (retained or input.gpu_inventory_count == 0),
        .unavailable, .unsupported, .skipped => input.observed_count == 0 and
            input.overflow_count == 0 and
            input.gpu_retained_count == input.gpu_inventory_count and
            (retained or input.gpu_inventory_count == 0),
    };
}

fn validCpuBaseline(input: *const protocol.SourcePersistenceOutput) bool {
    if (input.cpu_baseline_valid == 0) return cpuBaselineEmpty(input);
    return input.cpu_monotonic_ticks != 0 and input.cpu_monotonic_ticks_per_second != 0 and
        input.cpu_counter_contract_version == protocol.cpu_counter_contract_version and
        input.cpu_kernel_ticks >= input.cpu_idle_ticks;
}

fn cpuBaselineEmpty(input: *const protocol.SourcePersistenceOutput) bool {
    return input.cpu_idle_ticks == 0 and input.cpu_kernel_ticks == 0 and
        input.cpu_user_ticks == 0 and input.cpu_monotonic_ticks == 0 and
        input.cpu_monotonic_ticks_per_second == 0 and
        input.cpu_counter_contract_version == 0;
}

fn validRuleState(
    input: *const protocol.RuleStateOutput,
    definition: *const protocol.MetricDefinitionInput,
    active: *const catalog.RuleRecord,
    source: *const catalog.SourceRecord,
    captured_at_milliseconds: u64,
) bool {
    if (input.struct_size != @sizeOf(protocol.RuleStateOutput) or
        input.flags != definition.flags or input.rule_handle != definition.rule_handle or
        input.metric_handle != definition.metric_handle or
        input.source_handle != definition.source_handle or
        input.value_kind != definition.value_kind or
        input.definition_fingerprint != definition.semantic_fingerprint or
        (input.capability_mask & ~definition.capability_mask) != 0 or
        (input.capability_mask & ~source.definition.capability_mask) != 0 or
        input.capability_generation > source.capability_generation or
        input.has_last_good > 1 or !protocol.knownEnum(protocol.MetricStatus, input.status) or
        input.state_fingerprint == 0)
    {
        return false;
    }
    const record = ruleRecord(definition.*, input.*);
    if (session_module.ruleOutput(record).state_fingerprint != input.state_fingerprint) {
        return false;
    }
    const status: protocol.MetricStatus = @enumFromInt(input.status);
    if (input.capability_generation < active.capability_generation or
        input.unsupported_until_capability_generation > input.capability_generation)
    {
        return false;
    }
    switch (status) {
        .unsupported => {
            if (input.capability_generation == 0 or
                input.unsupported_until_capability_generation != input.capability_generation)
            {
                return false;
            }
        },
        .retained => {
            if (input.unsupported_until_capability_generation != 0 and
                input.unsupported_until_capability_generation != input.capability_generation)
            {
                return false;
            }
        },
        .current, .unavailable, .skipped => {
            if (input.unsupported_until_capability_generation != 0) return false;
        },
    }
    if (active.unsupported_until_capability_generation != 0 and
        input.capability_generation <= active.unsupported_until_capability_generation and
        (input.unsupported_until_capability_generation !=
            active.unsupported_until_capability_generation or
            input.state_fingerprint != session_module.ruleOutput(active.*).state_fingerprint))
    {
        return false;
    }
    if (input.has_last_good != 0) {
        if (status != .current and status != .retained) return false;
        if (input.source_generation == 0 or input.capability_generation == 0 or
            input.source_generation > source.source_generation or
            input.observed_at_milliseconds == 0 or
            input.observed_at_milliseconds > captured_at_milliseconds or
            input.observed_at_milliseconds > source.last_current_at_milliseconds or
            !protocol.validValue(
                definition.value_kind,
                definition.flags,
                definition.minimum_value_bits,
                definition.maximum_value_bits,
                input.value_bits,
            ))
        {
            return false;
        }
        if (active.has_last_good) {
            if (input.source_generation < active.source_generation or
                input.observed_at_milliseconds < active.observed_at_milliseconds)
            {
                return false;
            }
            if (input.source_generation == active.source_generation and
                input.observed_at_milliseconds == active.observed_at_milliseconds and
                input.state_fingerprint != session_module.ruleOutput(active.*).state_fingerprint)
            {
                return false;
            }
        }
        return true;
    }
    if (status == .current or status == .retained or
        input.source_generation != 0 or input.value_bits != 0)
    {
        return false;
    }
    if (input.capability_mask != 0 or input.quality != 0 or
        input.sample_duration_milliseconds != 0)
    {
        return false;
    }
    if (input.observed_at_milliseconds > captured_at_milliseconds or
        input.observed_at_milliseconds > source.last_current_at_milliseconds or
        input.observed_at_milliseconds < active.observed_at_milliseconds)
    {
        return false;
    }
    return true;
}

fn sourceRecord(
    definition: protocol.SourcePolicyInput,
    input: protocol.SourcePersistenceOutput,
) catalog.SourceRecord {
    return .{
        .active = true,
        .definition = definition,
        .source_incarnation = input.source_incarnation,
        .source_generation = input.source_generation,
        .last_observation_sequence = input.source_observation_sequence,
        .capability_generation = input.capability_generation,
        .last_attempt_at_milliseconds = input.last_attempt_at_milliseconds,
        .last_current_at_milliseconds = input.last_current_at_milliseconds,
        .requested_count = input.requested_count,
        .observed_count = input.observed_count,
        .skipped_count = input.skipped_count,
        .overflow_count = input.overflow_count,
        .gpu_inventory_count = input.gpu_inventory_count,
        .gpu_retained_count = input.gpu_retained_count,
        .reset_count = input.reset_count,
        .last_reset_reason_mask = input.last_reset_reason_mask,
        .status = @enumFromInt(input.status),
        .cpu_baseline_valid = input.cpu_baseline_valid != 0,
        .cpu_idle_ticks = input.cpu_idle_ticks,
        .cpu_kernel_ticks = input.cpu_kernel_ticks,
        .cpu_user_ticks = input.cpu_user_ticks,
        .cpu_monotonic_ticks = input.cpu_monotonic_ticks,
        .cpu_monotonic_ticks_per_second = input.cpu_monotonic_ticks_per_second,
        .cpu_counter_contract_version = input.cpu_counter_contract_version,
    };
}

fn ruleRecord(
    definition: protocol.MetricDefinitionInput,
    input: protocol.RuleStateOutput,
) catalog.RuleRecord {
    return .{
        .active = true,
        .definition = definition,
        .source_generation = input.source_generation,
        .observed_at_milliseconds = input.observed_at_milliseconds,
        .value_bits = input.value_bits,
        .capability_mask = input.capability_mask,
        .quality = input.quality,
        .sample_duration_milliseconds = input.sample_duration_milliseconds,
        .capability_generation = input.capability_generation,
        .unsupported_until_capability_generation = input.unsupported_until_capability_generation,
        .status = @enumFromInt(input.status),
        .has_last_good = input.has_last_good != 0,
    };
}

fn validImportedPhase(
    phase: protocol.Phase,
    bank: *const catalog.Bank,
    gpu_inventory: []const state.GpuRecord,
) bool {
    var committed = false;
    var retained = false;
    for (bank.sources[0..bank.source_count]) |source| {
        if (source.source_generation != 0) committed = true;
    }
    for (bank.rules[0..bank.rule_count]) |rule| {
        if (rule.has_last_good) committed = true;
        if (rule.status == .retained) retained = true;
    }
    for (gpu_inventory) |gpu| {
        if (!gpu.active) continue;
        committed = true;
        if (gpu.output.status == @intFromEnum(protocol.InventoryStatus.retained)) {
            retained = true;
        }
    }
    return switch (phase) {
        .catalog_ready => !committed,
        .ready => committed,
        .failed_retained => committed and retained,
        .empty, .completion_open => false,
    };
}

fn sourceFingerprint(input: *const protocol.SourcePersistenceOutput) u64 {
    var copy = input.*;
    copy.state_fingerprint = 0;
    const value = std.hash.Wyhash.hash(0x726d_6d65_7472_7073, std.mem.asBytes(&copy));
    return if (value == 0) 1 else value;
}

fn checksum(
    header: *const protocol.PersistenceHeader,
    sources: []const protocol.SourcePersistenceOutput,
    rules: []const protocol.RuleStateOutput,
    gpu_inventory: []const protocol.GpuInventoryOutput,
) u64 {
    var copy = header.*;
    copy.checksum = 0;
    var hash = std.hash.Wyhash.init(0x726d_6d65_7472_7063);
    hash.update(std.mem.asBytes(&copy));
    hash.update(std.mem.sliceAsBytes(sources));
    hash.update(std.mem.sliceAsBytes(rules));
    hash.update(std.mem.sliceAsBytes(gpu_inventory));
    const value = hash.final();
    return if (value == 0) 1 else value;
}

fn allZero(values: anytype) bool {
    for (values) |value| if (value != 0) return false;
    return true;
}
