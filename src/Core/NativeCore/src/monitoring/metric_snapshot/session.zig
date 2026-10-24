const std = @import("std");
const protocol = @import("protocol.zig");
const catalog = @import("catalog.zig");
const state = @import("state.zig");
const planner = @import("planner.zig");
const observation = @import("observation.zig");
const inventory = @import("inventory.zig");
const ResultCode = @import("../../common/result_codes.zig").ResultCode;

pub const Session = state.Session;

pub fn reconfigure(session: *Session, config: *const protocol.Config) ResultCode {
    state.lock(&session.mutex);
    defer state.unlock(&session.mutex);
    if (!protocol.validConfig(config)) return .abi_mismatch;
    if (session.phase == .completion_open) return .unavailable;
    if (config.generation <= session.config.generation or !sameShape(&session.config, config) or
        config.resident_byte_budget < session.requiredByteCount())
    {
        return .invalid_argument;
    }
    const next_revision = session.nextRevision() orelse return .unavailable;
    session.clearPlanAuthority();
    session.clearCompletion();
    session.config = config.*;
    session.state_revision = next_revision;
    return .ok;
}

pub fn synchronizeCounterDemand(session: *Session, input: *const protocol.ControlInput, active_metrics: []const protocol.PlanMetricInput) ResultCode {
    state.lock(&session.mutex);
    defer state.unlock(&session.mutex);
    if (!validControl(session, input)) return .abi_mismatch;
    if (session.phase == .empty or session.phase == .completion_open) return .unavailable;
    if (!session.operationAdvances(input.operation_epoch)) return .stale_frame;
    if (active_metrics.len > session.config.maximum_plan_metric_count) return .invalid_argument;
    var previous: u64 = 0;
    for (active_metrics) |metric| {
        if (metric.struct_size != @sizeOf(protocol.PlanMetricInput) or metric.flags != 0 or
            metric.metric_handle <= previous or session.active.findMetric(metric.metric_handle) == null) return .invalid_argument;
        previous = metric.metric_handle;
    }
    const revision = session.nextRevision() orelse return .unavailable;
    // Catalog rules and demand are both ordered by metric handle, including source alternatives.
    var index: usize = 0;
    for (session.active.rules[0..session.active.rule_count]) |*rule| {
        while (index < active_metrics.len and active_metrics[index].metric_handle < rule.definition.metric_handle) : (index += 1) {}
        if (protocol.isCounterMetric(rule.definition.metric_kind) and
            (index == active_metrics.len or active_metrics[index].metric_handle != rule.definition.metric_handle)) rule.counter_baseline = .{};
    }
    session.last_operation_epoch = input.operation_epoch;
    session.last_command_at_milliseconds = input.command_at_milliseconds;
    session.state_revision = revision;
    return .ok;
}

pub fn reset(session: *Session, input: *const protocol.ControlInput) ResultCode {
    state.lock(&session.mutex);
    defer state.unlock(&session.mutex);
    if (!validControl(session, input)) return .abi_mismatch;
    if (session.phase == .completion_open) return .unavailable;
    if (!session.operationAdvances(input.operation_epoch)) {
        return .stale_frame;
    }
    const next_revision = session.nextRevision() orelse return .unavailable;
    var next_committed = session.committed_generation;
    if (session.semantic_fingerprint != 0) {
        next_committed = session.nextCommittedGeneration() orelse return .unavailable;
    }

    session.active.clear();
    session.staging.clear();
    @memset(session.gpu_active, .{});
    @memset(session.gpu_staging, .{});
    @memset(session.gpu_index, catalog.no_index);
    @memset(session.gpu_staging_index, catalog.no_index);
    @memset(session.gpu_luid_index, catalog.no_index);
    @memset(session.gpu_staging_luid_index, catalog.no_index);
    @memset(session.gpu_key_index, catalog.no_index);
    @memset(session.gpu_staging_key_index, catalog.no_index);
    session.gpu_staging_prepared = false;
    @memset(session.source_modes, .{});
    @memset(session.planned_rule_flags, false);
    session.clearCompletion();
    session.planned_rule_count = 0;
    session.catalog_generation = 0;
    session.catalog_fingerprint = 0;
    session.committed_generation = next_committed;
    session.last_operation_epoch = input.operation_epoch;
    session.last_plan_epoch = 0;
    session.last_command_at_milliseconds = input.command_at_milliseconds;
    session.captured_at_milliseconds = 0;
    session.semantic_fingerprint = 0;
    session.state_revision = next_revision;
    session.phase = .empty;
    return .ok;
}

pub fn replaceCatalog(
    session: *Session,
    input: *const protocol.CatalogReplaceInput,
    sources: []const protocol.SourcePolicyInput,
    rules: []const protocol.MetricDefinitionInput,
) ResultCode {
    state.lock(&session.mutex);
    defer state.unlock(&session.mutex);
    if (!validCatalogReplace(session, input, sources, rules)) return .abi_mismatch;
    if (session.phase == .completion_open) return .unavailable;
    if (!session.operationAdvances(input.operation_epoch) or
        input.catalog_generation <= session.catalog_generation)
    {
        return .stale_frame;
    }
    const expected_fingerprint = catalogFingerprint(sources, rules);
    if (expected_fingerprint != input.semantic_fingerprint) return .invalid_argument;
    const next_revision = session.nextRevision() orelse return .unavailable;
    if (!session.staging.build(&session.active, input, sources, rules)) {
        observation.synchronizeBank(&session.staging, &session.active);
        inventory.synchronizeStagingWithActive(session);
        return .invalid_argument;
    }
    const preserved_data = bankHasData(&session.staging);
    const preserve_gpu = shouldPreserveGpuInventory(session, &session.staging);
    inventory.prepareCatalogReplacement(session, preserve_gpu);
    observation.recomputeBank(&session.staging);
    const next_semantic = observation.semanticFingerprintFor(
        &session.staging,
        expected_fingerprint,
        session.gpu_staging,
    );
    var next_committed = session.committed_generation;
    if (next_semantic != session.semantic_fingerprint) {
        next_committed = session.nextCommittedGeneration() orelse {
            observation.synchronizeBank(&session.staging, &session.active);
            inventory.synchronizeStagingWithActive(session);
            return .unavailable;
        };
    }

    std.mem.swap(catalog.Bank, &session.active, &session.staging);
    observation.synchronizeBank(&session.staging, &session.active);
    inventory.commitStaged(session);
    session.catalog_generation = input.catalog_generation;
    session.catalog_fingerprint = expected_fingerprint;
    session.last_operation_epoch = input.operation_epoch;
    session.last_command_at_milliseconds = input.command_at_milliseconds;
    session.last_plan_epoch = 0;
    session.clearPlanAuthority();
    session.clearCompletion();
    session.semantic_fingerprint = next_semantic;
    session.committed_generation = next_committed;
    session.state_revision = next_revision;
    session.phase = if (preserved_data) .ready else .catalog_ready;
    return .ok;
}

pub fn plan(
    session: *Session,
    input: *const protocol.PlanInput,
    requested_metrics: []const protocol.PlanMetricInput,
    source_modes: []const protocol.SourceModeInput,
    output: *protocol.PlanOutput,
    source_plans: []protocol.SourcePlanOutput,
    metric_plans: []protocol.MetricPlanOutput,
    scratch_bytes: []u8,
) ResultCode {
    return planner.plan(
        session,
        input,
        requested_metrics,
        source_modes,
        output,
        source_plans,
        metric_plans,
        scratch_bytes,
    );
}

pub fn beginCompletion(session: *Session, input: *const protocol.CompletionHeader, scratch_bytes: []u8) ResultCode {
    return observation.begin(session, input, scratch_bytes);
}

pub fn submitRequested(
    session: *Session,
    inputs: []const protocol.RequestedMetricInput,
) ResultCode {
    return observation.submitRequested(session, inputs);
}

pub fn submitObservations(
    session: *Session,
    inputs: []const protocol.ObservationInput,
) ResultCode {
    return observation.submitObservations(session, inputs);
}

pub fn submitCpuCounter(
    session: *Session,
    input: *const protocol.CpuCounterInput,
) ResultCode {
    return observation.submitCpuCounter(session, input);
}

pub fn submitGpuInventory(
    session: *Session,
    inputs: []const protocol.GpuInventoryInput,
) ResultCode {
    return inventory.submit(session, inputs);
}

pub fn finalizeCompletion(
    session: *Session,
    input: *const protocol.FinalizeInput,
) ResultCode {
    return observation.finalize(session, input);
}

pub fn abortCompletion(
    session: *Session,
    input: *const protocol.AbortInput,
) ResultCode {
    return observation.abort(session, input);
}

pub fn querySnapshotHeader(session: *Session, output: *protocol.SnapshotHeader) ResultCode {
    state.lock(&session.mutex);
    defer state.unlock(&session.mutex);
    output.* = snapshotHeader(session);
    return .ok;
}

pub fn readSnapshot(
    session: *Session,
    input: *const protocol.ReadInput,
    sources: []protocol.SourceOutput,
    metrics: []protocol.MetricOutput,
    gpu_inventory: []protocol.GpuInventoryOutput,
    rules: []protocol.RuleStateOutput,
) ResultCode {
    state.lock(&session.mutex);
    defer state.unlock(&session.mutex);
    if (!validRead(session, input)) return .stale_frame;
    const gpu_count = inventory.activeCount(session);
    if ((sources.len != 0 and sources.len < session.active.source_count) or
        (metrics.len != 0 and metrics.len < session.active.metric_count) or
        (gpu_inventory.len != 0 and gpu_inventory.len < gpu_count) or
        (rules.len != 0 and rules.len < session.active.rule_count))
    {
        return .buffer_too_small;
    }
    if (sources.len != 0) @memset(sources, std.mem.zeroes(protocol.SourceOutput));
    if (metrics.len != 0) @memset(metrics, std.mem.zeroes(protocol.MetricOutput));
    if (gpu_inventory.len != 0) @memset(gpu_inventory, std.mem.zeroes(protocol.GpuInventoryOutput));
    if (rules.len != 0) @memset(rules, std.mem.zeroes(protocol.RuleStateOutput));

    if (sources.len != 0) {
        for (session.active.sources[0..session.active.source_count], 0..) |source, ordinal| {
            sources[ordinal] = observation.sourceOutput(source);
        }
    }
    if (metrics.len != 0) {
        for (session.active.metrics[0..session.active.metric_count], 0..) |metric, ordinal| {
            metrics[ordinal] = metric.output;
        }
    }
    if (gpu_inventory.len != 0) {
        var gpu_ordinal: usize = 0;
        for (session.gpu_active) |gpu| {
            if (!gpu.active) continue;
            gpu_inventory[gpu_ordinal] = gpu.output;
            gpu_ordinal += 1;
        }
    }
    if (rules.len != 0) {
        for (session.active.rules[0..session.active.rule_count], 0..) |rule, ordinal| {
            rules[ordinal] = ruleOutput(rule);
        }
    }
    return .ok;
}

pub fn snapshotHeader(session: *const Session) protocol.SnapshotHeader {
    var current: u32 = 0;
    var unavailable: u32 = 0;
    var unsupported: u32 = 0;
    var skipped: u32 = 0;
    for (session.active.metrics[0..session.active.metric_count]) |metric| {
        switch (@as(protocol.MetricStatus, @enumFromInt(metric.output.status))) {
            .current => current += 1,
            .unavailable => unavailable += 1,
            .unsupported => unsupported += 1,
            .skipped => skipped += 1,
        }
    }
    var flags: u32 = 0;
    if (session.catalog_generation != 0) flags |= protocol.SnapshotFlags.catalog_loaded;
    if (bankHasData(&session.active) or inventory.activeCount(session) != 0) {
        flags |= protocol.SnapshotFlags.committed_data;
    }
    if (inventory.activeCount(session) != 0) flags |= protocol.SnapshotFlags.gpu_inventory_present;
    if (session.phase == .completion_open) flags |= protocol.SnapshotFlags.completion_open;
    return .{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.SnapshotHeader),
        .configuration_generation = session.config.generation,
        .catalog_generation = session.catalog_generation,
        .catalog_fingerprint = session.catalog_fingerprint,
        .state_revision = session.state_revision,
        .committed_generation = session.committed_generation,
        .last_operation_epoch = session.last_operation_epoch,
        .last_plan_epoch = session.last_plan_epoch,
        .last_command_at_milliseconds = session.last_command_at_milliseconds,
        .captured_at_milliseconds = session.captured_at_milliseconds,
        .source_count = session.active.source_count,
        .metric_count = session.active.metric_count,
        .rule_count = session.active.rule_count,
        .gpu_adapter_count = inventory.activeCount(session),
        .current_metric_count = current,
        .unavailable_metric_count = unavailable,
        .unsupported_metric_count = unsupported,
        .skipped_metric_count = skipped,
        .flags = flags,
        .phase = @intFromEnum(session.phase),
        .reserved_u32 = 0,
        .semantic_fingerprint = session.semantic_fingerprint,
        .reserved = .{ 0, 0 },
    };
}

pub fn catalogFingerprint(
    sources: []const protocol.SourcePolicyInput,
    rules: []const protocol.MetricDefinitionInput,
) u64 {
    var hash = std.hash.Wyhash.init(0x726d_6d65_7472_6361);
    const counts = [_]u64{ sources.len, rules.len };
    hash.update(std.mem.asBytes(&counts));
    for (sources) |source| {
        const fields = [_]u64{
            source.flags,
            source.source_handle,
            source.source_role,
            source.priority,
            source.capability_mask,
            source.semantic_fingerprint,
        };
        hash.update(std.mem.asBytes(&fields));
    }
    for (rules) |rule| {
        const fields = [_]u64{
            rule.flags,
            rule.rule_handle,
            rule.metric_handle,
            rule.source_handle,
            rule.scope_handle,
            rule.capability_mask,
            rule.metric_kind,
            rule.scope_kind,
            rule.value_kind,
            rule.source_priority,
            rule.minimum_value_bits,
            rule.maximum_value_bits,
            rule.semantic_fingerprint,
        };
        hash.update(std.mem.asBytes(&fields));
    }
    const value = hash.final();
    return if (value == 0) 1 else value;
}

fn validCatalogReplace(
    session: *const Session,
    input: *const protocol.CatalogReplaceInput,
    sources: []const protocol.SourcePolicyInput,
    rules: []const protocol.MetricDefinitionInput,
) bool {
    return input.abi_version == protocol.abi_version and
        input.struct_size == @sizeOf(protocol.CatalogReplaceInput) and
        input.configuration_generation == session.config.generation and
        input.catalog_generation != 0 and input.operation_epoch != 0 and
        input.source_count == sources.len and input.rule_count == rules.len and
        input.source_count <= session.config.maximum_source_count and
        input.rule_count <= session.config.maximum_rule_count and
        input.metric_count <= session.config.maximum_metric_count and
        input.gpu_inventory_source_count <= 1 and
        input.source_index_capacity == session.config.source_index_capacity and
        input.metric_index_capacity == session.config.metric_index_capacity and
        input.rule_index_capacity == session.config.rule_index_capacity and
        input.flags == 0 and input.semantic_fingerprint != 0 and
        allZero(input.reserved);
}

fn validControl(session: *const Session, input: *const protocol.ControlInput) bool {
    return input.abi_version == protocol.abi_version and
        input.struct_size == @sizeOf(protocol.ControlInput) and
        input.configuration_generation == session.config.generation and
        input.operation_epoch != 0 and input.flags == 0 and allZero(input.reserved);
}

fn validRead(session: *const Session, input: *const protocol.ReadInput) bool {
    return input.abi_version == protocol.abi_version and
        input.struct_size == @sizeOf(protocol.ReadInput) and
        input.configuration_generation == session.config.generation and
        input.catalog_generation == session.catalog_generation and
        input.catalog_fingerprint == session.catalog_fingerprint and
        input.state_revision == session.state_revision and
        input.committed_generation == session.committed_generation and
        input.flags == 0 and allZero(input.reserved);
}

fn sameShape(left: *const protocol.Config, right: *const protocol.Config) bool {
    var expected = left.*;
    expected.generation = right.generation;
    expected.maximum_future_skew_milliseconds = right.maximum_future_skew_milliseconds;
    expected.resident_byte_budget = right.resident_byte_budget;
    return std.mem.eql(u8, std.mem.asBytes(&expected), std.mem.asBytes(right));
}

fn bankHasData(bank: *const catalog.Bank) bool {
    for (bank.sources[0..bank.source_count]) |source| {
        if (source.source_generation != 0) return true;
    }
    for (bank.rules[0..bank.rule_count]) |rule| {
        if (rule.status == .current) return true;
    }
    return false;
}

fn shouldPreserveGpuInventory(
    session: *const Session,
    candidate: *const catalog.Bank,
) bool {
    var old_source: ?catalog.SourceRecord = null;
    for (session.active.sources[0..session.active.source_count]) |source| {
        if (source.definition.source_role ==
            @intFromEnum(protocol.SourceRole.gpu_inventory))
        {
            old_source = source;
            break;
        }
    }
    var new_source: ?catalog.SourceRecord = null;
    for (candidate.sources[0..candidate.source_count]) |source| {
        if (source.definition.source_role ==
            @intFromEnum(protocol.SourceRole.gpu_inventory))
        {
            new_source = source;
            break;
        }
    }
    if (old_source == null or new_source == null) return false;
    return catalog.sameSourceDefinition(
        &old_source.?.definition,
        &new_source.?.definition,
    );
}

pub fn ruleOutput(rule: catalog.RuleRecord) protocol.RuleStateOutput {
    var output = protocol.RuleStateOutput{
        .struct_size = @sizeOf(protocol.RuleStateOutput),
        .flags = rule.definition.flags,
        .rule_handle = rule.definition.rule_handle,
        .metric_handle = rule.definition.metric_handle,
        .source_handle = rule.definition.source_handle,
        .source_generation = rule.source_generation,
        .observed_at_milliseconds = rule.observed_at_milliseconds,
        .value_bits = rule.value_bits,
        .value_high_bits = rule.value_high_bits,
        .byte_count = rule.byte_count,
        .payload_reserved = 0,
        .value_bytes = rule.value_bytes,
        .capability_mask = rule.capability_mask,
        .status = @intFromEnum(rule.status),
        .value_kind = rule.definition.value_kind,
        .quality = rule.quality,
        .sample_duration_milliseconds = rule.sample_duration_milliseconds,
        .definition_fingerprint = rule.definition.semantic_fingerprint,
        .state_fingerprint = 0,
        .capability_generation = rule.capability_generation,
        .unsupported_until_capability_generation = rule.unsupported_until_capability_generation,
    };
    const value = std.hash.Wyhash.hash(
        0x726d_6d65_7472_7275,
        std.mem.asBytes(&output),
    );
    output.state_fingerprint = if (value == 0) 1 else value;
    return output;
}

fn allZero(values: anytype) bool {
    for (values) |value| if (value != 0) return false;
    return true;
}
