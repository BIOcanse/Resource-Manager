const std = @import("std");
const protocol = @import("protocol.zig");
const state = @import("state.zig");
const session_module = @import("session.zig");
const persistence = @import("persistence.zig");
const ResultCode = @import("../../common/result_codes.zig").ResultCode;

const source_count = 3;
const rule_count = 3;
const metric_count = 2;

test "catalog semantic fingerprint is field-ordered and stable" {
    const sources = testSources();
    const rules = testRules();
    try std.testing.expectEqual(
        @as(u64, 18_431_652_591_665_447_221),
        session_module.catalogFingerprint(&sources, &rules),
    );
}

test "catalog plan CPU baseline GPU inventory and exact snapshot are one state machine" {
    var config = testConfig(1);
    const session = try state.Session.create(&config);
    defer session.destroy();
    var sources = testSources();
    var rules = testRules();
    try std.testing.expectEqual(
        ResultCode.ok,
        replaceCatalog(session, 1, 1, 100, &sources, &rules),
    );

    var source_plans = std.mem.zeroes([4]protocol.SourcePlanOutput);
    var metric_plans = std.mem.zeroes([4]protocol.MetricPlanOutput);
    var output = std.mem.zeroes(protocol.PlanOutput);
    const requested = [_]protocol.PlanMetricInput{
        planMetric(100),
        planMetric(200),
    };
    const modes = testModes();
    try std.testing.expectEqual(
        ResultCode.ok,
        runPlan(
            session,
            2,
            1,
            200,
            protocol.PlanFlags.include_gpu_inventory,
            &requested,
            &modes,
            &output,
            &source_plans,
            &metric_plans,
        ),
    );
    try std.testing.expectEqual(@as(u32, 2), output.source_plan_count);
    try std.testing.expectEqual(
        protocol.PlanOutputFlags.gpu_inventory_selected,
        output.flags,
    );
    try std.testing.expectEqual(@as(u64, 10), source_plans[0].source_handle);
    try std.testing.expectEqual(@as(u64, 30), source_plans[1].source_handle);

    const requested_rules = [_]protocol.RequestedMetricInput{
        requestedRule(1_001),
        requestedRule(2_001),
    };
    const memory = [_]protocol.ObservationInput{
        unsignedObservation(2_001, 200, 10, 2, 300, 4096),
    };
    var first_header = metricCompletion(3, 1, 10, 1, 1, 300, 2, 1, 1);
    first_header.capability_generation = source_plans[0].capability_generation;
    first_header.plan_token_fingerprint = source_plans[0].plan_token_fingerprint;
    try std.testing.expectEqual(ResultCode.ok, session_module.beginCompletion(session, &first_header));
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.submitRequested(session, &requested_rules),
    );
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.submitObservations(session, &memory),
    );
    var first_counter = cpuCounter(1, 300, 100, 500, 500, 1_000);
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.submitCpuCounter(session, &first_counter),
    );
    var first_finalize = finalizeInput(3, 1, 10, 300);
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.finalizeCompletion(session, &first_finalize),
    );

    var header = std.mem.zeroes(protocol.SnapshotHeader);
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.querySnapshotHeader(session, &header),
    );
    try std.testing.expectEqual(@as(u32, 1), header.current_metric_count);
    try std.testing.expectEqual(@as(u32, 1), header.skipped_metric_count);

    output = std.mem.zeroes(protocol.PlanOutput);
    source_plans = std.mem.zeroes([4]protocol.SourcePlanOutput);
    metric_plans = std.mem.zeroes([4]protocol.MetricPlanOutput);
    try std.testing.expectEqual(
        ResultCode.ok,
        runPlan(
            session,
            4,
            2,
            400,
            protocol.PlanFlags.include_gpu_inventory,
            &requested,
            &modes,
            &output,
            &source_plans,
            &metric_plans,
        ),
    );
    var second_header = metricCompletion(5, 2, 10, 1, 2, 500, 2, 1, 1);
    second_header.capability_generation = source_plans[0].capability_generation;
    second_header.plan_token_fingerprint = source_plans[0].plan_token_fingerprint;
    try std.testing.expectEqual(ResultCode.ok, session_module.beginCompletion(session, &second_header));
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.submitRequested(session, &requested_rules),
    );
    const memory_second = [_]protocol.ObservationInput{
        unsignedObservation(2_001, 200, 10, 2, 500, 8192),
    };
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.submitObservations(session, &memory_second),
    );
    var second_counter = cpuCounter(2, 500, 150, 650, 650, 1_200);
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.submitCpuCounter(session, &second_counter),
    );
    var second_finalize = finalizeInput(5, 2, 10, 500);
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.finalizeCompletion(session, &second_finalize),
    );

    var gpu_header = gpuCompletion(6, 2, 30, 1, 1, 600, 2);
    gpu_header.capability_generation = source_plans[1].capability_generation;
    gpu_header.plan_token_fingerprint = source_plans[1].plan_token_fingerprint;
    try std.testing.expectEqual(ResultCode.ok, session_module.beginCompletion(session, &gpu_header));
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.submitRequested(session, &.{}),
    );
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.submitObservations(session, &.{}),
    );
    const gpus = [_]protocol.GpuInventoryInput{
        gpuInput(101, 1, 11, 1_001, 1, 600),
        gpuInput(102, 2, 22, 1_002, 1, 600),
    };
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.submitGpuInventory(session, &gpus),
    );
    var gpu_finalize = finalizeInput(6, 2, 30, 600);
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.finalizeCompletion(session, &gpu_finalize),
    );

    header = std.mem.zeroes(protocol.SnapshotHeader);
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.querySnapshotHeader(session, &header),
    );
    try std.testing.expectEqual(@as(u32, 2), header.current_metric_count);
    try std.testing.expectEqual(@as(u32, 2), header.gpu_adapter_count);

    var source_outputs = std.mem.zeroes([source_count]protocol.SourceOutput);
    var metric_outputs = std.mem.zeroes([metric_count]protocol.MetricOutput);
    var gpu_outputs = std.mem.zeroes([2]protocol.GpuInventoryOutput);
    var rule_outputs = std.mem.zeroes([rule_count]protocol.RuleStateOutput);
    var read = readInput(header);
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.readSnapshot(
            session,
            &read,
            &source_outputs,
            &metric_outputs,
            &gpu_outputs,
            &rule_outputs,
        ),
    );
    try std.testing.expectEqual(@as(u64, 2), source_outputs[0].source_generation);
    try std.testing.expectEqual(@as(u64, 2), source_outputs[0].source_observation_sequence);
    try std.testing.expectEqual(@as(u32, 0), source_outputs[0].reset_count);
    try std.testing.expectEqual(@as(u64, 200), metric_outputs[0].sample_duration_milliseconds);
    try std.testing.expectEqual(@as(u64, 101), gpu_outputs[0].adapter_handle);
}

test "catalog replacement is atomic and permits priority independent handle ordering" {
    var config = testConfig(1);
    const session = try state.Session.create(&config);
    defer session.destroy();
    var sources = testSources();
    sources[0].priority = 3;
    sources[1].priority = 1;
    sources[2].priority = 2;
    var rules = testRules();
    rules[0].source_priority = 3;
    rules[1].source_priority = 1;
    rules[2].source_priority = 3;
    std.mem.swap(protocol.MetricDefinitionInput, &rules[0], &rules[1]);
    rules[0].rule_handle = 1_002;
    rules[1].rule_handle = 1_001;
    try std.testing.expectEqual(
        ResultCode.ok,
        replaceCatalog(session, 1, 1, 100, &sources, &rules),
    );
    const original_generation = session.catalog_generation;
    const original_fingerprint = session.catalog_fingerprint;

    var invalid_rules = rules;
    invalid_rules[1].source_handle = 999;
    try std.testing.expectEqual(
        ResultCode.invalid_argument,
        replaceCatalog(session, 2, 2, 200, &sources, &invalid_rules),
    );
    try std.testing.expectEqual(original_generation, session.catalog_generation);
    try std.testing.expectEqual(original_fingerprint, session.catalog_fingerprint);
}

test "hot reconfigure invalidates old plan authority and preserves the epoch lower bound" {
    var config = testConfig(1);
    const session = try state.Session.create(&config);
    defer session.destroy();
    var sources = testSources();
    var rules = testRules();
    try std.testing.expectEqual(
        ResultCode.ok,
        replaceCatalog(session, 1, 1, 100, &sources, &rules),
    );

    var source_plans = std.mem.zeroes([4]protocol.SourcePlanOutput);
    var metric_plans = std.mem.zeroes([4]protocol.MetricPlanOutput);
    var plan_output = std.mem.zeroes(protocol.PlanOutput);
    const requested = [_]protocol.PlanMetricInput{planMetric(100)};
    const modes = testModes();
    try std.testing.expectEqual(
        ResultCode.ok,
        runPlan(
            session,
            2,
            1,
            200,
            0,
            &requested,
            &modes,
            &plan_output,
            &source_plans,
            &metric_plans,
        ),
    );
    const old_source_plan = source_plans[0];

    config.generation = 2;
    try std.testing.expectEqual(ResultCode.ok, session_module.reconfigure(session, &config));
    try std.testing.expectEqual(@as(u64, 1), session.last_plan_epoch);

    var old_header = metricCompletion(3, 1, 10, 1, 1, 300, 1, 0, 1);
    old_header.configuration_generation = 2;
    old_header.capability_generation = old_source_plan.capability_generation;
    old_header.plan_token_fingerprint = old_source_plan.plan_token_fingerprint;
    try std.testing.expectEqual(
        ResultCode.stale_frame,
        session_module.beginCompletion(session, &old_header),
    );

    source_plans = std.mem.zeroes([4]protocol.SourcePlanOutput);
    metric_plans = std.mem.zeroes([4]protocol.MetricPlanOutput);
    try std.testing.expectEqual(
        ResultCode.ok,
        runPlan(
            session,
            3,
            2,
            300,
            0,
            &requested,
            &modes,
            &plan_output,
            &source_plans,
            &metric_plans,
        ),
    );
    var new_header = metricCompletion(4, 2, 10, 1, 1, 400, 1, 0, 1);
    new_header.configuration_generation = 2;
    new_header.capability_generation = source_plans[0].capability_generation;
    new_header.plan_token_fingerprint = source_plans[0].plan_token_fingerprint;
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.beginCompletion(session, &new_header),
    );
    var abort = finalizeInput(4, 2, 10, 400);
    abort.configuration_generation = 2;
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.abortCompletion(session, &abort),
    );
}

test "completion and observation timestamps are nonzero bounded and monotonic" {
    var config = testConfig(1);
    const session = try state.Session.create(&config);
    defer session.destroy();
    var sources = testSources();
    var rules = testRules();
    try std.testing.expectEqual(
        ResultCode.ok,
        replaceCatalog(session, 1, 1, 100, &sources, &rules),
    );

    var source_plans = std.mem.zeroes([4]protocol.SourcePlanOutput);
    var metric_plans = std.mem.zeroes([4]protocol.MetricPlanOutput);
    var plan_output = std.mem.zeroes(protocol.PlanOutput);
    const requested = [_]protocol.PlanMetricInput{planMetric(200)};
    const modes = testModes();
    try std.testing.expectEqual(
        ResultCode.ok,
        runPlan(
            session,
            2,
            1,
            200,
            0,
            &requested,
            &modes,
            &plan_output,
            &source_plans,
            &metric_plans,
        ),
    );
    const revision_before_zero = session.state_revision;
    var zero_header = metricCompletion(3, 1, 10, 1, 1, 0, 1, 1, 0);
    zero_header.capability_generation = source_plans[0].capability_generation;
    zero_header.plan_token_fingerprint = source_plans[0].plan_token_fingerprint;
    try std.testing.expectEqual(
        ResultCode.abi_mismatch,
        session_module.beginCompletion(session, &zero_header),
    );
    try std.testing.expectEqual(revision_before_zero, session.state_revision);

    var first_header = metricCompletion(3, 1, 10, 1, 1, 300, 1, 1, 0);
    first_header.capability_generation = source_plans[0].capability_generation;
    first_header.plan_token_fingerprint = source_plans[0].plan_token_fingerprint;
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.beginCompletion(session, &first_header),
    );
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.submitRequested(session, &.{requestedRule(2_001)}),
    );
    const future = [_]protocol.ObservationInput{
        unsignedObservation(2_001, 200, 10, 2, 301, 4096),
    };
    try std.testing.expectEqual(
        ResultCode.invalid_argument,
        session_module.submitObservations(session, &future),
    );
    const current = [_]protocol.ObservationInput{
        unsignedObservation(2_001, 200, 10, 2, 300, 4096),
    };
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.submitObservations(session, &current),
    );
    var first_finalize = finalizeInput(3, 1, 10, 300);
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.finalizeCompletion(session, &first_finalize),
    );

    try std.testing.expectEqual(
        ResultCode.ok,
        runPlan(
            session,
            4,
            2,
            400,
            0,
            &requested,
            &modes,
            &plan_output,
            &source_plans,
            &metric_plans,
        ),
    );
    var second_header = metricCompletion(5, 2, 10, 1, 2, 500, 1, 1, 0);
    second_header.capability_generation = source_plans[0].capability_generation;
    second_header.plan_token_fingerprint = source_plans[0].plan_token_fingerprint;
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.beginCompletion(session, &second_header),
    );
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.submitRequested(session, &.{requestedRule(2_001)}),
    );
    const regressed = [_]protocol.ObservationInput{
        unsignedObservation(2_001, 200, 10, 2, 299, 8192),
    };
    try std.testing.expectEqual(
        ResultCode.invalid_argument,
        session_module.submitObservations(session, &regressed),
    );
    var abort = finalizeInput(5, 2, 10, 500);
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.abortCompletion(session, &abort),
    );
}

test "mark unavailable preserves observation ordering authority" {
    var config = testConfig(1);
    const session = try state.Session.create(&config);
    defer session.destroy();
    var sources = testSources();
    var rules = testRules();
    rules[2].retention_policy = @intFromEnum(protocol.RetentionPolicy.mark_unavailable);
    try std.testing.expectEqual(
        ResultCode.ok,
        replaceCatalog(session, 1, 1, 100, &sources, &rules),
    );

    var source_plans = std.mem.zeroes([4]protocol.SourcePlanOutput);
    var metric_plans = std.mem.zeroes([4]protocol.MetricPlanOutput);
    var plan_output = std.mem.zeroes(protocol.PlanOutput);
    const requested = [_]protocol.PlanMetricInput{planMetric(200)};
    const modes = testModes();
    try std.testing.expectEqual(
        ResultCode.ok,
        runPlan(
            session,
            2,
            1,
            200,
            0,
            &requested,
            &modes,
            &plan_output,
            &source_plans,
            &metric_plans,
        ),
    );
    var current_header = metricCompletion(3, 1, 10, 1, 1, 300, 1, 1, 0);
    current_header.capability_generation = source_plans[0].capability_generation;
    current_header.plan_token_fingerprint = source_plans[0].plan_token_fingerprint;
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.beginCompletion(session, &current_header),
    );
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.submitRequested(session, &.{requestedRule(2_001)}),
    );
    const current = [_]protocol.ObservationInput{
        unsignedObservation(2_001, 200, 10, 2, 300, 4096),
    };
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.submitObservations(session, &current),
    );
    var current_finalize = finalizeInput(3, 1, 10, 300);
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.finalizeCompletion(session, &current_finalize),
    );

    try std.testing.expectEqual(
        ResultCode.ok,
        runPlan(
            session,
            4,
            2,
            400,
            0,
            &requested,
            &modes,
            &plan_output,
            &source_plans,
            &metric_plans,
        ),
    );
    var unavailable_header = metricCompletion(5, 2, 10, 1, 2, 500, 1, 0, 0);
    unavailable_header.status = @intFromEnum(protocol.SourceStatus.unavailable);
    unavailable_header.capability_generation = source_plans[0].capability_generation;
    unavailable_header.plan_token_fingerprint = source_plans[0].plan_token_fingerprint;
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.beginCompletion(session, &unavailable_header),
    );
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.submitRequested(session, &.{requestedRule(2_001)}),
    );
    var unavailable_finalize = finalizeInput(5, 2, 10, 500);
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.finalizeCompletion(session, &unavailable_finalize),
    );
    try std.testing.expect(!session.active.rules[2].has_last_good);
    try std.testing.expectEqual(
        @as(u64, 300),
        session.active.rules[2].observed_at_milliseconds,
    );

    try std.testing.expectEqual(
        ResultCode.ok,
        runPlan(
            session,
            6,
            3,
            600,
            0,
            &requested,
            &modes,
            &plan_output,
            &source_plans,
            &metric_plans,
        ),
    );
    var stale_header = metricCompletion(7, 3, 10, 1, 3, 700, 1, 1, 0);
    stale_header.capability_generation = source_plans[0].capability_generation;
    stale_header.plan_token_fingerprint = source_plans[0].plan_token_fingerprint;
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.beginCompletion(session, &stale_header),
    );
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.submitRequested(session, &.{requestedRule(2_001)}),
    );
    var stale = unsignedObservation(2_001, 200, 10, 2, 299, 8192);
    stale.source_observation_sequence = 3;
    try std.testing.expectEqual(
        ResultCode.invalid_argument,
        session_module.submitObservations(session, &.{stale}),
    );
    var abort = finalizeInput(7, 3, 10, 700);
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.abortCompletion(session, &abort),
    );
}

test "late catalog rejection restores staging rows indexes and GPU scratch" {
    var config = testConfig(1);
    const session = try state.Session.create(&config);
    defer session.destroy();
    var sources = testSources();
    var rules = testRules();
    try std.testing.expectEqual(
        ResultCode.ok,
        replaceCatalog(session, 1, 1, 100, &sources, &rules),
    );

    var invalid_sources = sources;
    invalid_sources[0].source_handle = 110;
    invalid_sources[1].source_handle = 120;
    invalid_sources[2].source_handle = 130;
    var invalid_rules = rules;
    invalid_rules[0].source_handle = 110;
    invalid_rules[1].source_handle = 120;
    invalid_rules[2].source_handle = 110;
    invalid_rules[2].source_priority = 2;
    try std.testing.expectEqual(
        ResultCode.invalid_argument,
        replaceCatalog(session, 2, 2, 200, &invalid_sources, &invalid_rules),
    );
    try std.testing.expect(session.staging.findSource(10) != null);
    try std.testing.expect(session.staging.findSource(110) == null);
    try std.testing.expect(session.staging.findRule(1_001) != null);
    try std.testing.expect(session.staging.findRule(1_001 + 100) == null);

    try completeCpuCycle(session, 3, 4, 1, 300, 400, 1, 100, 500, 500, 1_000);
    try std.testing.expectEqual(@as(u64, 1), session.active.sources[0].source_generation);
}

test "persistence restores one canonical image and rejects corruption" {
    var config = testConfig(1);
    const source_session = try state.Session.create(&config);
    defer source_session.destroy();
    var sources = testSources();
    var rules = testRules();
    try std.testing.expectEqual(
        ResultCode.ok,
        replaceCatalog(source_session, 1, 1, 100, &sources, &rules),
    );

    var source_outputs = std.mem.zeroes([source_count]protocol.SourcePersistenceOutput);
    var rule_outputs = std.mem.zeroes([rule_count]protocol.RuleStateOutput);
    var gpu_outputs = std.mem.zeroes([0]protocol.GpuInventoryOutput);
    var persisted = std.mem.zeroes(protocol.PersistenceHeader);
    var snapshot = std.mem.zeroes(protocol.SnapshotHeader);
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.querySnapshotHeader(source_session, &snapshot),
    );
    var read = readInput(snapshot);
    try std.testing.expectEqual(
        ResultCode.ok,
        persistence.exportState(
            source_session,
            &read,
            &persisted,
            &source_outputs,
            &rule_outputs,
            &gpu_outputs,
        ),
    );

    const restored_session = try state.Session.create(&config);
    defer restored_session.destroy();
    try std.testing.expectEqual(
        ResultCode.ok,
        replaceCatalog(restored_session, 1, 1, 100, &sources, &rules),
    );
    var import_input = persistenceInput(2, 200);
    var corrupt = persisted;
    corrupt.checksum ^= 1;
    const before_revision = restored_session.state_revision;
    try std.testing.expectEqual(
        ResultCode.invalid_argument,
        persistence.importState(
            restored_session,
            &import_input,
            &corrupt,
            &source_outputs,
            &rule_outputs,
            &gpu_outputs,
        ),
    );
    try std.testing.expectEqual(before_revision, restored_session.state_revision);
    try std.testing.expectEqual(
        ResultCode.ok,
        persistence.importState(
            restored_session,
            &import_input,
            &persisted,
            &source_outputs,
            &rule_outputs,
            &gpu_outputs,
        ),
    );
    try std.testing.expectEqual(
        source_session.semantic_fingerprint,
        restored_session.semantic_fingerprint,
    );
}

test "diagnostic-only CPU progression preserves committed generation and reset is monotonic" {
    var config = testConfig(1);
    const session = try state.Session.create(&config);
    defer session.destroy();
    var sources = testSources();
    var rules = testRules();
    try std.testing.expectEqual(
        ResultCode.ok,
        replaceCatalog(session, 1, 1, 100, &sources, &rules),
    );

    try completeCpuCycle(session, 2, 3, 1, 200, 300, 1, 100, 500, 500, 1_000);
    try completeCpuCycle(session, 4, 5, 2, 400, 500, 2, 150, 650, 650, 1_200);
    const semantic_generation = session.committed_generation;
    const revision_before = session.state_revision;
    try completeCpuCycle(session, 6, 7, 3, 600, 700, 3, 200, 800, 800, 1_400);
    try std.testing.expectEqual(semantic_generation, session.committed_generation);
    try std.testing.expect(session.state_revision > revision_before);

    var reset_input = controlInput(8, 800);
    try std.testing.expectEqual(ResultCode.ok, session_module.reset(session, &reset_input));
    try std.testing.expectEqual(semantic_generation + 1, session.committed_generation);
    const generation_after_reset = session.committed_generation;
    reset_input.operation_epoch = 9;
    reset_input.command_at_milliseconds = 900;
    try std.testing.expectEqual(ResultCode.ok, session_module.reset(session, &reset_input));
    try std.testing.expectEqual(generation_after_reset, session.committed_generation);
}

test "GPU partial completion retains the full image and identity drift is retryable" {
    var config = testConfig(1);
    const session = try state.Session.create(&config);
    defer session.destroy();
    var sources = testSources();
    var rules = testRules();
    try std.testing.expectEqual(
        ResultCode.ok,
        replaceCatalog(session, 1, 1, 100, &sources, &rules),
    );

    var source_plans = std.mem.zeroes([4]protocol.SourcePlanOutput);
    var metric_plans = std.mem.zeroes([4]protocol.MetricPlanOutput);
    var plan_output = std.mem.zeroes(protocol.PlanOutput);
    const requested = [_]protocol.PlanMetricInput{planMetric(200)};
    const modes = testModes();
    try std.testing.expectEqual(
        ResultCode.ok,
        runPlan(
            session,
            2,
            1,
            200,
            protocol.PlanFlags.include_gpu_inventory,
            &requested,
            &modes,
            &plan_output,
            &source_plans,
            &metric_plans,
        ),
    );
    try completeGpuCycle(
        session,
        3,
        1,
        1,
        300,
        source_plans[1],
        &.{
            gpuInput(101, 1, 11, 1_001, 1, 250),
            gpuInput(102, 2, 22, 1_002, 1, 250),
        },
        .complete,
        2,
    );

    source_plans = std.mem.zeroes([4]protocol.SourcePlanOutput);
    metric_plans = std.mem.zeroes([4]protocol.MetricPlanOutput);
    try std.testing.expectEqual(
        ResultCode.ok,
        runPlan(
            session,
            4,
            2,
            400,
            protocol.PlanFlags.include_gpu_inventory,
            &requested,
            &modes,
            &plan_output,
            &source_plans,
            &metric_plans,
        ),
    );
    try completeGpuCycle(
        session,
        5,
        2,
        2,
        500,
        source_plans[1],
        &.{gpuInput(101, 1, 11, 1_001, 2, 500)},
        .partial,
        2,
    );
    try std.testing.expectEqual(@as(u32, 2), @import("inventory.zig").activeCount(session));
    for (session.gpu_active) |gpu| if (gpu.active) {
        try std.testing.expectEqual(
            @intFromEnum(protocol.InventoryStatus.retained),
            gpu.output.status,
        );
    };

    source_plans = std.mem.zeroes([4]protocol.SourcePlanOutput);
    metric_plans = std.mem.zeroes([4]protocol.MetricPlanOutput);
    try std.testing.expectEqual(
        ResultCode.ok,
        runPlan(
            session,
            6,
            3,
            600,
            protocol.PlanFlags.include_gpu_inventory,
            &requested,
            &modes,
            &plan_output,
            &source_plans,
            &metric_plans,
        ),
    );
    var final_header = gpuCompletion(7, 3, 30, 1, 3, 700, 2);
    final_header.capability_generation = source_plans[1].capability_generation;
    final_header.plan_token_fingerprint = source_plans[1].plan_token_fingerprint;
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.beginCompletion(session, &final_header),
    );
    try std.testing.expectEqual(ResultCode.ok, session_module.submitRequested(session, &.{}));
    try std.testing.expectEqual(ResultCode.ok, session_module.submitObservations(session, &.{}));
    const drifted = [_]protocol.GpuInventoryInput{
        gpuInput(101, 99, 11, 1_001, 3, 700),
        gpuInput(102, 2, 22, 1_002, 3, 700),
    };
    try std.testing.expectEqual(
        ResultCode.invalid_argument,
        session_module.submitGpuInventory(session, &drifted),
    );
    try std.testing.expectEqual(@as(u32, 0), session.gpu_staging_count);
    const exact = [_]protocol.GpuInventoryInput{
        gpuInput(101, 1, 11, 1_001, 3, 650),
        gpuInput(102, 2, 22, 1_002, 3, 650),
    };
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.submitGpuInventory(session, &exact),
    );
    try std.testing.expectEqual(
        @as(u64, 650),
        session.gpu_staging[0].output.observed_at_milliseconds,
    );
    const accepted_first = session.gpu_staging[0].output.semantic_fingerprint;
    const accepted_second = session.gpu_staging[1].output.semantic_fingerprint;
    var duplicate_identity = exact;
    duplicate_identity[1].stable_key_handle = duplicate_identity[0].stable_key_handle;
    try std.testing.expectEqual(
        ResultCode.invalid_argument,
        session_module.submitGpuInventory(session, &duplicate_identity),
    );
    try std.testing.expectEqual(@as(u32, 2), session.gpu_staging_count);
    try std.testing.expectEqual(
        accepted_first,
        session.gpu_staging[0].output.semantic_fingerprint,
    );
    try std.testing.expectEqual(
        accepted_second,
        session.gpu_staging[1].output.semantic_fingerprint,
    );
    var unsupported_capability = exact;
    unsupported_capability[1].capability_mask = 0x08;
    try std.testing.expectEqual(
        ResultCode.invalid_argument,
        session_module.submitGpuInventory(session, &unsupported_capability),
    );
    try std.testing.expectEqual(@as(u32, 2), session.gpu_staging_count);
    var final_input = finalizeInput(7, 3, 30, 700);
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.finalizeCompletion(session, &final_input),
    );
}

test "source incarnation change invalidates CPU baseline and old current facts" {
    var config = testConfig(1);
    const session = try state.Session.create(&config);
    defer session.destroy();
    var sources = testSources();
    var rules = testRules();
    try std.testing.expectEqual(
        ResultCode.ok,
        replaceCatalog(session, 1, 1, 100, &sources, &rules),
    );
    try completeCpuCycle(session, 2, 3, 1, 200, 300, 1, 100, 500, 500, 1_000);
    try completeCpuCycle(session, 4, 5, 2, 400, 500, 2, 150, 650, 650, 1_200);
    const cpu_rule_index = session.active.findRule(1_001).?;
    try std.testing.expectEqual(
        protocol.MetricStatus.current,
        session.active.rules[cpu_rule_index].status,
    );

    var source_plans = std.mem.zeroes([4]protocol.SourcePlanOutput);
    var metric_plans = std.mem.zeroes([4]protocol.MetricPlanOutput);
    var plan_output = std.mem.zeroes(protocol.PlanOutput);
    const memory_request = [_]protocol.PlanMetricInput{planMetric(200)};
    const modes = testModes();
    try std.testing.expectEqual(
        ResultCode.ok,
        runPlan(
            session,
            6,
            3,
            600,
            0,
            &memory_request,
            &modes,
            &plan_output,
            &source_plans,
            &metric_plans,
        ),
    );
    var restart_header = metricCompletion(7, 3, 10, 2, 1, 700, 1, 1, 0);
    restart_header.capability_generation = source_plans[0].capability_generation;
    restart_header.plan_token_fingerprint = source_plans[0].plan_token_fingerprint;
    var memory = unsignedObservation(2_001, 200, 10, 2, 700, 8_192);
    memory.source_observation_sequence = 1;
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.beginCompletion(session, &restart_header),
    );
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.submitRequested(session, &.{requestedRule(2_001)}),
    );
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.submitObservations(session, &.{memory}),
    );
    var restart_finalize = finalizeInput(7, 3, 10, 700);
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.finalizeCompletion(session, &restart_finalize),
    );
    const source_index = session.active.findSource(10).?;
    try std.testing.expect(!session.active.sources[source_index].cpu_baseline_valid);
    try std.testing.expectEqual(
        protocol.MetricStatus.retained,
        session.active.rules[cpu_rule_index].status,
    );

    source_plans = std.mem.zeroes([4]protocol.SourcePlanOutput);
    metric_plans = std.mem.zeroes([4]protocol.MetricPlanOutput);
    const cpu_request = [_]protocol.PlanMetricInput{planMetric(100)};
    try std.testing.expectEqual(
        ResultCode.ok,
        runPlan(
            session,
            8,
            4,
            800,
            0,
            &cpu_request,
            &modes,
            &plan_output,
            &source_plans,
            &metric_plans,
        ),
    );
    var warm_header = metricCompletion(9, 4, 10, 2, 2, 900, 1, 0, 1);
    warm_header.capability_generation = source_plans[0].capability_generation;
    warm_header.plan_token_fingerprint = source_plans[0].plan_token_fingerprint;
    var warm_counter = cpuCounter(2, 900, 200, 800, 800, 1_400);
    warm_counter.source_incarnation = 2;
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.beginCompletion(session, &warm_header),
    );
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.submitRequested(session, &.{requestedRule(1_001)}),
    );
    try std.testing.expectEqual(ResultCode.ok, session_module.submitObservations(session, &.{}));
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.submitCpuCounter(session, &warm_counter),
    );
    var warm_finalize = finalizeInput(9, 4, 10, 900);
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.finalizeCompletion(session, &warm_finalize),
    );
    try std.testing.expect(session.active.sources[source_index].cpu_baseline_valid);
    try std.testing.expectEqual(
        protocol.MetricStatus.retained,
        session.active.rules[cpu_rule_index].status,
    );
}

test "capability generation is monotonic and unsupported cannot revive in place" {
    var config = testConfig(1);
    const session = try state.Session.create(&config);
    defer session.destroy();
    var sources = testSources();
    var rules = testRules();
    try std.testing.expectEqual(
        ResultCode.ok,
        replaceCatalog(session, 1, 1, 100, &sources, &rules),
    );

    var source_plans = std.mem.zeroes([4]protocol.SourcePlanOutput);
    var metric_plans = std.mem.zeroes([4]protocol.MetricPlanOutput);
    var plan_output = std.mem.zeroes(protocol.PlanOutput);
    const requested = [_]protocol.PlanMetricInput{planMetric(100)};
    var modes = testModes();
    try std.testing.expectEqual(
        ResultCode.ok,
        runPlan(
            session,
            2,
            1,
            200,
            0,
            &requested,
            &modes,
            &plan_output,
            &source_plans,
            &metric_plans,
        ),
    );
    var unsupported = metricCompletion(3, 1, 10, 1, 1, 300, 1, 0, 0);
    unsupported.capability_generation = source_plans[0].capability_generation;
    unsupported.plan_token_fingerprint = source_plans[0].plan_token_fingerprint;
    unsupported.status = @intFromEnum(protocol.SourceStatus.unsupported);
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.beginCompletion(session, &unsupported),
    );
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.submitRequested(session, &.{requestedRule(1_001)}),
    );
    try std.testing.expectEqual(ResultCode.ok, session_module.submitObservations(session, &.{}));
    var unsupported_finalize = finalizeInput(3, 1, 10, 300);
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.finalizeCompletion(session, &unsupported_finalize),
    );

    try std.testing.expectEqual(
        ResultCode.stale_frame,
        runPlan(
            session,
            4,
            2,
            400,
            0,
            &requested,
            &modes,
            &plan_output,
            &source_plans,
            &metric_plans,
        ),
    );
    modes[0].capability_generation = 2;
    try std.testing.expectEqual(
        ResultCode.ok,
        runPlan(
            session,
            4,
            2,
            400,
            0,
            &requested,
            &modes,
            &plan_output,
            &source_plans,
            &metric_plans,
        ),
    );
    var unsupported_generation_two = metricCompletion(5, 2, 10, 1, 2, 500, 1, 0, 0);
    unsupported_generation_two.capability_generation =
        source_plans[0].capability_generation;
    unsupported_generation_two.plan_token_fingerprint =
        source_plans[0].plan_token_fingerprint;
    unsupported_generation_two.status = @intFromEnum(protocol.SourceStatus.unsupported);
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.beginCompletion(session, &unsupported_generation_two),
    );
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.submitRequested(session, &.{requestedRule(1_001)}),
    );
    try std.testing.expectEqual(ResultCode.ok, session_module.submitObservations(session, &.{}));
    var unsupported_generation_two_finalize = finalizeInput(5, 2, 10, 500);
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.finalizeCompletion(session, &unsupported_generation_two_finalize),
    );

    modes[0].capability_generation = 1;
    try std.testing.expectEqual(
        ResultCode.stale_frame,
        runPlan(
            session,
            6,
            3,
            600,
            0,
            &requested,
            &modes,
            &plan_output,
            &source_plans,
            &metric_plans,
        ),
    );
}

test "field unsupported remains closed until a newer accepted capability generation" {
    var config = testConfig(1);
    const session = try state.Session.create(&config);
    defer session.destroy();
    var sources = testSources();
    var rules = testRules();
    try std.testing.expectEqual(
        ResultCode.ok,
        replaceCatalog(session, 1, 1, 100, &sources, &rules),
    );

    var source_plans = std.mem.zeroes([4]protocol.SourcePlanOutput);
    var metric_plans = std.mem.zeroes([4]protocol.MetricPlanOutput);
    var plan_output = std.mem.zeroes(protocol.PlanOutput);
    const requested = [_]protocol.PlanMetricInput{planMetric(200)};
    var modes = testModes();
    try std.testing.expectEqual(
        ResultCode.ok,
        runPlan(
            session,
            2,
            1,
            200,
            0,
            &requested,
            &modes,
            &plan_output,
            &source_plans,
            &metric_plans,
        ),
    );

    var header = metricCompletion(3, 1, 10, 1, 1, 300, 1, 1, 0);
    header.capability_generation = source_plans[0].capability_generation;
    header.plan_token_fingerprint = source_plans[0].plan_token_fingerprint;
    try std.testing.expectEqual(ResultCode.ok, session_module.beginCompletion(session, &header));
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.submitRequested(session, &.{requestedRule(2_001)}),
    );
    var unsupported = unsignedObservation(2_001, 200, 10, 2, 300, 0);
    unsupported.valid_mask = protocol.ObservationValid.required;
    unsupported.status = @intFromEnum(protocol.ObservationStatus.unsupported);
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.submitObservations(session, &.{unsupported}),
    );
    var finalize = finalizeInput(3, 1, 10, 300);
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.finalizeCompletion(session, &finalize),
    );

    try std.testing.expectEqual(
        ResultCode.ok,
        runPlan(
            session,
            4,
            2,
            400,
            0,
            &requested,
            &modes,
            &plan_output,
            &source_plans,
            &metric_plans,
        ),
    );
    try std.testing.expectEqual(@as(u64, 0), metric_plans[0].rule_handle);
    try std.testing.expectEqual(@as(u32, 1), plan_output.unavailable_metric_count);

    modes[0].capability_generation = 2;
    try std.testing.expectEqual(
        ResultCode.ok,
        runPlan(
            session,
            5,
            3,
            500,
            0,
            &requested,
            &modes,
            &plan_output,
            &source_plans,
            &metric_plans,
        ),
    );
    try std.testing.expectEqual(@as(u64, 2_001), metric_plans[0].rule_handle);
    modes[0].capability_generation = 1;
    try std.testing.expectEqual(
        ResultCode.stale_frame,
        runPlan(
            session,
            6,
            4,
            600,
            0,
            &requested,
            &modes,
            &plan_output,
            &source_plans,
            &metric_plans,
        ),
    );
}

test "retained unsupported suppression survives persistence until capability advances" {
    var config = testConfig(1);
    const source_session = try state.Session.create(&config);
    defer source_session.destroy();
    var sources = testSources();
    var rules = testRules();
    try std.testing.expectEqual(
        ResultCode.ok,
        replaceCatalog(source_session, 1, 1, 100, &sources, &rules),
    );

    var source_plans = std.mem.zeroes([4]protocol.SourcePlanOutput);
    var metric_plans = std.mem.zeroes([4]protocol.MetricPlanOutput);
    var plan_output = std.mem.zeroes(protocol.PlanOutput);
    const requested = [_]protocol.PlanMetricInput{planMetric(200)};
    var modes = testModes();
    try std.testing.expectEqual(
        ResultCode.ok,
        runPlan(
            source_session,
            2,
            1,
            200,
            0,
            &requested,
            &modes,
            &plan_output,
            &source_plans,
            &metric_plans,
        ),
    );
    var current_header = metricCompletion(3, 1, 10, 1, 1, 300, 1, 1, 0);
    current_header.capability_generation = source_plans[0].capability_generation;
    current_header.plan_token_fingerprint = source_plans[0].plan_token_fingerprint;
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.beginCompletion(source_session, &current_header),
    );
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.submitRequested(source_session, &.{requestedRule(2_001)}),
    );
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.submitObservations(
            source_session,
            &.{unsignedObservation(2_001, 200, 10, 2, 300, 4096)},
        ),
    );
    var current_finalize = finalizeInput(3, 1, 10, 300);
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.finalizeCompletion(source_session, &current_finalize),
    );

    try std.testing.expectEqual(
        ResultCode.ok,
        runPlan(
            source_session,
            4,
            2,
            400,
            0,
            &requested,
            &modes,
            &plan_output,
            &source_plans,
            &metric_plans,
        ),
    );
    var unsupported_header = metricCompletion(5, 2, 10, 1, 2, 500, 1, 1, 0);
    unsupported_header.capability_generation = source_plans[0].capability_generation;
    unsupported_header.plan_token_fingerprint = source_plans[0].plan_token_fingerprint;
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.beginCompletion(source_session, &unsupported_header),
    );
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.submitRequested(source_session, &.{requestedRule(2_001)}),
    );
    var unsupported = unsignedObservation(2_001, 200, 10, 2, 500, 0);
    unsupported.valid_mask = protocol.ObservationValid.required;
    unsupported.status = @intFromEnum(protocol.ObservationStatus.unsupported);
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.submitObservations(source_session, &.{unsupported}),
    );
    var unsupported_finalize = finalizeInput(5, 2, 10, 500);
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.finalizeCompletion(source_session, &unsupported_finalize),
    );
    const retained_rule = source_session.active.rules[2];
    try std.testing.expectEqual(protocol.MetricStatus.retained, retained_rule.status);
    try std.testing.expectEqual(@as(u64, 1), retained_rule.unsupported_until_capability_generation);

    try std.testing.expectEqual(
        ResultCode.ok,
        runPlan(
            source_session,
            6,
            3,
            600,
            0,
            &requested,
            &modes,
            &plan_output,
            &source_plans,
            &metric_plans,
        ),
    );
    try std.testing.expectEqual(@as(u64, 0), metric_plans[0].rule_handle);

    var source_outputs = std.mem.zeroes([source_count]protocol.SourcePersistenceOutput);
    var rule_outputs = std.mem.zeroes([rule_count]protocol.RuleStateOutput);
    var gpu_outputs = std.mem.zeroes([0]protocol.GpuInventoryOutput);
    var persisted = std.mem.zeroes(protocol.PersistenceHeader);
    var snapshot = std.mem.zeroes(protocol.SnapshotHeader);
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.querySnapshotHeader(source_session, &snapshot),
    );
    var read = readInput(snapshot);
    try std.testing.expectEqual(
        ResultCode.ok,
        persistence.exportState(
            source_session,
            &read,
            &persisted,
            &source_outputs,
            &rule_outputs,
            &gpu_outputs,
        ),
    );
    try std.testing.expectEqual(
        @as(u64, 1),
        rule_outputs[2].unsupported_until_capability_generation,
    );

    const target = try state.Session.create(&config);
    defer target.destroy();
    try std.testing.expectEqual(
        ResultCode.ok,
        replaceCatalog(target, 1, 1, 100, &sources, &rules),
    );
    var import_input = persistenceInput(7, 700);
    try std.testing.expectEqual(
        ResultCode.ok,
        persistence.importState(
            target,
            &import_input,
            &persisted,
            &source_outputs,
            &rule_outputs,
            &gpu_outputs,
        ),
    );
    try std.testing.expectEqual(
        ResultCode.ok,
        runPlan(
            target,
            8,
            4,
            800,
            0,
            &requested,
            &modes,
            &plan_output,
            &source_plans,
            &metric_plans,
        ),
    );
    try std.testing.expectEqual(@as(u64, 0), metric_plans[0].rule_handle);
    modes[0].capability_generation = 2;
    try std.testing.expectEqual(
        ResultCode.ok,
        runPlan(
            target,
            9,
            5,
            900,
            0,
            &requested,
            &modes,
            &plan_output,
            &source_plans,
            &metric_plans,
        ),
    );
    try std.testing.expectEqual(@as(u64, 2_001), metric_plans[0].rule_handle);
}

test "persistence rejects source rule and time closure drift atomically" {
    var config = testConfig(1);
    const source_session = try state.Session.create(&config);
    defer source_session.destroy();
    var sources = testSources();
    var rules = testRules();
    try std.testing.expectEqual(
        ResultCode.ok,
        replaceCatalog(source_session, 1, 1, 100, &sources, &rules),
    );
    try completeCpuCycle(source_session, 2, 3, 1, 200, 300, 1, 100, 500, 500, 1_000);
    try completeCpuCycle(source_session, 4, 5, 2, 400, 500, 2, 150, 650, 650, 1_200);

    var source_outputs = std.mem.zeroes([source_count]protocol.SourcePersistenceOutput);
    var rule_outputs = std.mem.zeroes([rule_count]protocol.RuleStateOutput);
    var gpu_outputs = std.mem.zeroes([0]protocol.GpuInventoryOutput);
    var persisted = std.mem.zeroes(protocol.PersistenceHeader);
    var snapshot = std.mem.zeroes(protocol.SnapshotHeader);
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.querySnapshotHeader(source_session, &snapshot),
    );
    var read = readInput(snapshot);
    try std.testing.expectEqual(
        ResultCode.ok,
        persistence.exportState(
            source_session,
            &read,
            &persisted,
            &source_outputs,
            &rule_outputs,
            &gpu_outputs,
        ),
    );

    rule_outputs[0].source_generation = source_outputs[0].source_generation + 1;
    rule_outputs[0].state_fingerprint = ruleStateFingerprint(&rule_outputs[0]);
    persisted.checksum = persistenceChecksum(
        &persisted,
        &source_outputs,
        &rule_outputs,
        &gpu_outputs,
    );
    const target = try state.Session.create(&config);
    defer target.destroy();
    try std.testing.expectEqual(
        ResultCode.ok,
        replaceCatalog(target, 1, 1, 100, &sources, &rules),
    );
    const before_revision = target.state_revision;
    var import_input = persistenceInput(6, 600);
    try std.testing.expectEqual(
        ResultCode.invalid_argument,
        persistence.importState(
            target,
            &import_input,
            &persisted,
            &source_outputs,
            &rule_outputs,
            &gpu_outputs,
        ),
    );
    try std.testing.expectEqual(before_revision, target.state_revision);
}

test "persistence import invalidates local plan authority even at the same plan epoch" {
    var config = testConfig(1);
    const source_session = try state.Session.create(&config);
    defer source_session.destroy();
    const target = try state.Session.create(&config);
    defer target.destroy();
    var sources = testSources();
    var rules = testRules();
    try std.testing.expectEqual(
        ResultCode.ok,
        replaceCatalog(source_session, 1, 1, 100, &sources, &rules),
    );
    try std.testing.expectEqual(
        ResultCode.ok,
        replaceCatalog(target, 1, 1, 100, &sources, &rules),
    );

    var source_plans = std.mem.zeroes([4]protocol.SourcePlanOutput);
    var target_source_plans = std.mem.zeroes([4]protocol.SourcePlanOutput);
    var metric_plans = std.mem.zeroes([4]protocol.MetricPlanOutput);
    var target_metric_plans = std.mem.zeroes([4]protocol.MetricPlanOutput);
    var plan_output = std.mem.zeroes(protocol.PlanOutput);
    const requested = [_]protocol.PlanMetricInput{planMetric(100)};
    const modes = testModes();
    try std.testing.expectEqual(
        ResultCode.ok,
        runPlan(
            source_session,
            2,
            1,
            200,
            0,
            &requested,
            &modes,
            &plan_output,
            &source_plans,
            &metric_plans,
        ),
    );
    try std.testing.expectEqual(
        ResultCode.ok,
        runPlan(
            target,
            2,
            1,
            200,
            0,
            &requested,
            &modes,
            &plan_output,
            &target_source_plans,
            &target_metric_plans,
        ),
    );

    var source_outputs = std.mem.zeroes([source_count]protocol.SourcePersistenceOutput);
    var rule_outputs = std.mem.zeroes([rule_count]protocol.RuleStateOutput);
    var gpu_outputs = std.mem.zeroes([0]protocol.GpuInventoryOutput);
    var persisted = std.mem.zeroes(protocol.PersistenceHeader);
    var snapshot = std.mem.zeroes(protocol.SnapshotHeader);
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.querySnapshotHeader(source_session, &snapshot),
    );
    var read = readInput(snapshot);
    try std.testing.expectEqual(
        ResultCode.ok,
        persistence.exportState(
            source_session,
            &read,
            &persisted,
            &source_outputs,
            &rule_outputs,
            &gpu_outputs,
        ),
    );
    var import_input = persistenceInput(3, 300);
    try std.testing.expectEqual(
        ResultCode.ok,
        persistence.importState(
            target,
            &import_input,
            &persisted,
            &source_outputs,
            &rule_outputs,
            &gpu_outputs,
        ),
    );

    var stale_header = metricCompletion(4, 1, 10, 1, 1, 400, 1, 0, 1);
    stale_header.capability_generation = target_source_plans[0].capability_generation;
    stale_header.plan_token_fingerprint = target_source_plans[0].plan_token_fingerprint;
    try std.testing.expectEqual(
        ResultCode.stale_frame,
        session_module.beginCompletion(target, &stale_header),
    );
    try std.testing.expectEqual(
        ResultCode.ok,
        runPlan(
            target,
            5,
            2,
            500,
            0,
            &requested,
            &modes,
            &plan_output,
            &target_source_plans,
            &target_metric_plans,
        ),
    );
}

test "persistence rejects impossible source accounting and phase images atomically" {
    var config = testConfig(1);
    const source_session = try state.Session.create(&config);
    defer source_session.destroy();
    var sources = testSources();
    var rules = testRules();
    try std.testing.expectEqual(
        ResultCode.ok,
        replaceCatalog(source_session, 1, 1, 100, &sources, &rules),
    );
    try completeCpuCycle(source_session, 2, 3, 1, 200, 300, 1, 100, 500, 500, 1_000);

    var source_outputs = std.mem.zeroes([source_count]protocol.SourcePersistenceOutput);
    var rule_outputs = std.mem.zeroes([rule_count]protocol.RuleStateOutput);
    var gpu_outputs = std.mem.zeroes([0]protocol.GpuInventoryOutput);
    var persisted = std.mem.zeroes(protocol.PersistenceHeader);
    var snapshot = std.mem.zeroes(protocol.SnapshotHeader);
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.querySnapshotHeader(source_session, &snapshot),
    );
    var read = readInput(snapshot);
    try std.testing.expectEqual(
        ResultCode.ok,
        persistence.exportState(
            source_session,
            &read,
            &persisted,
            &source_outputs,
            &rule_outputs,
            &gpu_outputs,
        ),
    );

    const target = try state.Session.create(&config);
    defer target.destroy();
    try std.testing.expectEqual(
        ResultCode.ok,
        replaceCatalog(target, 1, 1, 100, &sources, &rules),
    );
    const before_revision = target.state_revision;
    const before_semantic = target.semantic_fingerprint;
    var import_input = persistenceInput(4, 400);

    var invalid_sources = source_outputs;
    invalid_sources[0].status = @intFromEnum(protocol.SourceStatus.partial);
    invalid_sources[0].overflow_count = 0;
    invalid_sources[0].state_fingerprint = sourceStateFingerprint(&invalid_sources[0]);
    var invalid_header = persisted;
    invalid_header.checksum = persistenceChecksum(
        &invalid_header,
        &invalid_sources,
        &rule_outputs,
        &gpu_outputs,
    );
    try std.testing.expectEqual(
        ResultCode.invalid_argument,
        persistence.importState(
            target,
            &import_input,
            &invalid_header,
            &invalid_sources,
            &rule_outputs,
            &gpu_outputs,
        ),
    );

    invalid_sources = source_outputs;
    invalid_sources[1].status = @intFromEnum(protocol.SourceStatus.complete);
    invalid_sources[1].state_fingerprint = sourceStateFingerprint(&invalid_sources[1]);
    invalid_header = persisted;
    invalid_header.checksum = persistenceChecksum(
        &invalid_header,
        &invalid_sources,
        &rule_outputs,
        &gpu_outputs,
    );
    try std.testing.expectEqual(
        ResultCode.invalid_argument,
        persistence.importState(
            target,
            &import_input,
            &invalid_header,
            &invalid_sources,
            &rule_outputs,
            &gpu_outputs,
        ),
    );

    invalid_sources = source_outputs;
    invalid_sources[0].reset_count = 1;
    invalid_sources[0].last_reset_reason_mask = 0;
    invalid_sources[0].state_fingerprint = sourceStateFingerprint(&invalid_sources[0]);
    invalid_header = persisted;
    invalid_header.checksum = persistenceChecksum(
        &invalid_header,
        &invalid_sources,
        &rule_outputs,
        &gpu_outputs,
    );
    try std.testing.expectEqual(
        ResultCode.invalid_argument,
        persistence.importState(
            target,
            &import_input,
            &invalid_header,
            &invalid_sources,
            &rule_outputs,
            &gpu_outputs,
        ),
    );

    invalid_sources = source_outputs;
    invalid_sources[0].reset_count = 1;
    invalid_sources[0].last_reset_reason_mask =
        protocol.SourceResetReason.incarnation_changed;
    invalid_sources[0].state_fingerprint = sourceStateFingerprint(&invalid_sources[0]);
    invalid_header = persisted;
    invalid_header.checksum = persistenceChecksum(
        &invalid_header,
        &invalid_sources,
        &rule_outputs,
        &gpu_outputs,
    );
    try std.testing.expectEqual(
        ResultCode.invalid_argument,
        persistence.importState(
            target,
            &import_input,
            &invalid_header,
            &invalid_sources,
            &rule_outputs,
            &gpu_outputs,
        ),
    );

    invalid_header = persisted;
    invalid_header.phase = @intFromEnum(protocol.Phase.failed_retained);
    invalid_header.checksum = persistenceChecksum(
        &invalid_header,
        &source_outputs,
        &rule_outputs,
        &gpu_outputs,
    );
    try std.testing.expectEqual(
        ResultCode.invalid_argument,
        persistence.importState(
            target,
            &import_input,
            &invalid_header,
            &source_outputs,
            &rule_outputs,
            &gpu_outputs,
        ),
    );
    try std.testing.expectEqual(before_revision, target.state_revision);
    try std.testing.expectEqual(before_semantic, target.semantic_fingerprint);
    try std.testing.expect(target.staging.findSource(10) != null);
}

test "persistence requires incarnation reset proof and a closed GPU retained image" {
    var config = testConfig(1);
    const source_session = try state.Session.create(&config);
    defer source_session.destroy();
    var sources = testSources();
    var rules = testRules();
    try std.testing.expectEqual(
        ResultCode.ok,
        replaceCatalog(source_session, 1, 1, 100, &sources, &rules),
    );

    var source_plans = std.mem.zeroes([4]protocol.SourcePlanOutput);
    var metric_plans = std.mem.zeroes([4]protocol.MetricPlanOutput);
    var plan_output = std.mem.zeroes(protocol.PlanOutput);
    const requested = [_]protocol.PlanMetricInput{planMetric(200)};
    const modes = testModes();
    try std.testing.expectEqual(
        ResultCode.ok,
        runPlan(
            source_session,
            2,
            1,
            200,
            protocol.PlanFlags.include_gpu_inventory,
            &requested,
            &modes,
            &plan_output,
            &source_plans,
            &metric_plans,
        ),
    );
    try completeGpuCycle(
        source_session,
        3,
        1,
        1,
        300,
        source_plans[1],
        &.{
            gpuInput(101, 1, 11, 1_001, 1, 250),
            gpuInput(102, 2, 22, 1_002, 1, 250),
        },
        .complete,
        2,
    );
    try std.testing.expectEqual(
        ResultCode.ok,
        runPlan(
            source_session,
            4,
            2,
            400,
            protocol.PlanFlags.include_gpu_inventory,
            &requested,
            &modes,
            &plan_output,
            &source_plans,
            &metric_plans,
        ),
    );
    try completeGpuCycle(
        source_session,
        5,
        2,
        2,
        500,
        source_plans[1],
        &.{gpuInput(101, 1, 11, 1_001, 2, 500)},
        .partial,
        2,
    );

    var source_outputs = std.mem.zeroes([source_count]protocol.SourcePersistenceOutput);
    var rule_outputs = std.mem.zeroes([rule_count]protocol.RuleStateOutput);
    var gpu_outputs = std.mem.zeroes([2]protocol.GpuInventoryOutput);
    var persisted = std.mem.zeroes(protocol.PersistenceHeader);
    var snapshot = std.mem.zeroes(protocol.SnapshotHeader);
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.querySnapshotHeader(source_session, &snapshot),
    );
    var read = readInput(snapshot);
    try std.testing.expectEqual(
        ResultCode.ok,
        persistence.exportState(
            source_session,
            &read,
            &persisted,
            &source_outputs,
            &rule_outputs,
            &gpu_outputs,
        ),
    );
    try std.testing.expectEqual(@as(u32, 2), source_outputs[2].gpu_inventory_count);
    try std.testing.expectEqual(@as(u32, 2), source_outputs[2].gpu_retained_count);
    try std.testing.expectEqual(@as(u64, 250), gpu_outputs[0].observed_at_milliseconds);

    const target = try state.Session.create(&config);
    defer target.destroy();
    try std.testing.expectEqual(
        ResultCode.ok,
        replaceCatalog(target, 1, 1, 100, &sources, &rules),
    );
    var import_input = persistenceInput(6, 600);
    const before_revision = target.state_revision;

    var invalid_sources = source_outputs;
    invalid_sources[2].gpu_inventory_count = 1;
    invalid_sources[2].gpu_retained_count = 1;
    invalid_sources[2].state_fingerprint = sourceStateFingerprint(&invalid_sources[2]);
    var invalid_header = persisted;
    invalid_header.checksum = persistenceChecksum(
        &invalid_header,
        &invalid_sources,
        &rule_outputs,
        &gpu_outputs,
    );
    try std.testing.expectEqual(
        ResultCode.invalid_argument,
        persistence.importState(
            target,
            &import_input,
            &invalid_header,
            &invalid_sources,
            &rule_outputs,
            &gpu_outputs,
        ),
    );

    invalid_sources = source_outputs;
    invalid_sources[2].observed_count = std.math.maxInt(u32);
    invalid_sources[2].overflow_count = 1;
    invalid_sources[2].state_fingerprint = sourceStateFingerprint(&invalid_sources[2]);
    invalid_header = persisted;
    invalid_header.checksum = persistenceChecksum(
        &invalid_header,
        &invalid_sources,
        &rule_outputs,
        &gpu_outputs,
    );
    try std.testing.expectEqual(
        ResultCode.invalid_argument,
        persistence.importState(
            target,
            &import_input,
            &invalid_header,
            &invalid_sources,
            &rule_outputs,
            &gpu_outputs,
        ),
    );

    invalid_sources = source_outputs;
    invalid_sources[2].observed_count = config.maximum_gpu_adapter_count;
    invalid_sources[2].overflow_count = 1;
    invalid_sources[2].state_fingerprint = sourceStateFingerprint(&invalid_sources[2]);
    invalid_header = persisted;
    invalid_header.checksum = persistenceChecksum(
        &invalid_header,
        &invalid_sources,
        &rule_outputs,
        &gpu_outputs,
    );
    try std.testing.expectEqual(
        ResultCode.invalid_argument,
        persistence.importState(
            target,
            &import_input,
            &invalid_header,
            &invalid_sources,
            &rule_outputs,
            &gpu_outputs,
        ),
    );

    var invalid_gpu = gpu_outputs;
    invalid_gpu[0].status = @intFromEnum(protocol.InventoryStatus.current);
    invalid_gpu[0].semantic_fingerprint = gpuOutputFingerprint(&invalid_gpu[0]);
    invalid_header = persisted;
    invalid_header.checksum = persistenceChecksum(
        &invalid_header,
        &source_outputs,
        &rule_outputs,
        &invalid_gpu,
    );
    try std.testing.expectEqual(
        ResultCode.invalid_argument,
        persistence.importState(
            target,
            &import_input,
            &invalid_header,
            &source_outputs,
            &rule_outputs,
            &invalid_gpu,
        ),
    );
    try std.testing.expectEqual(before_revision, target.state_revision);
    try std.testing.expectEqual(
        ResultCode.ok,
        persistence.importState(
            target,
            &import_input,
            &persisted,
            &source_outputs,
            &rule_outputs,
            &gpu_outputs,
        ),
    );

    var resetless_sources = source_outputs;
    resetless_sources[2].source_incarnation += 1;
    resetless_sources[2].source_generation += 1;
    resetless_sources[2].source_observation_sequence += 1;
    resetless_sources[2].state_fingerprint = sourceStateFingerprint(&resetless_sources[2]);
    invalid_header = persisted;
    invalid_header.state_revision = target.state_revision;
    invalid_header.last_operation_epoch = import_input.operation_epoch;
    invalid_header.last_command_at_milliseconds = import_input.command_at_milliseconds;
    invalid_header.checksum = persistenceChecksum(
        &invalid_header,
        &resetless_sources,
        &rule_outputs,
        &gpu_outputs,
    );
    import_input.operation_epoch = 7;
    import_input.command_at_milliseconds = 700;
    try std.testing.expectEqual(
        ResultCode.invalid_argument,
        persistence.importState(
            target,
            &import_input,
            &invalid_header,
            &resetless_sources,
            &rule_outputs,
            &gpu_outputs,
        ),
    );
}

test "failed revision and generation exhaustion commands leave authoritative state unchanged" {
    var config = testConfig(1);
    const begin_session = try state.Session.create(&config);
    defer begin_session.destroy();
    var sources = testSources();
    var rules = testRules();
    try std.testing.expectEqual(
        ResultCode.ok,
        replaceCatalog(begin_session, 1, 1, 100, &sources, &rules),
    );

    var source_plans = std.mem.zeroes([4]protocol.SourcePlanOutput);
    var metric_plans = std.mem.zeroes([4]protocol.MetricPlanOutput);
    var plan_output = std.mem.zeroes(protocol.PlanOutput);
    const requested = [_]protocol.PlanMetricInput{planMetric(100)};
    const modes = testModes();
    try std.testing.expectEqual(
        ResultCode.ok,
        runPlan(
            begin_session,
            2,
            1,
            200,
            0,
            &requested,
            &modes,
            &plan_output,
            &source_plans,
            &metric_plans,
        ),
    );

    var header = metricCompletion(3, 1, 10, 1, 1, 300, 1, 0, 1);
    header.capability_generation = source_plans[0].capability_generation;
    header.plan_token_fingerprint = source_plans[0].plan_token_fingerprint;
    const phase_before_begin = begin_session.phase;
    const completion_before_begin = begin_session.completion;
    header.plan_token_fingerprint ^= 1;
    try std.testing.expectEqual(
        ResultCode.stale_frame,
        session_module.beginCompletion(begin_session, &header),
    );
    try std.testing.expectEqual(phase_before_begin, begin_session.phase);
    try std.testing.expectEqualDeep(completion_before_begin, begin_session.completion);
    header.plan_token_fingerprint ^= 1;
    begin_session.state_revision = std.math.maxInt(u64);
    try std.testing.expectEqual(
        ResultCode.unavailable,
        session_module.beginCompletion(begin_session, &header),
    );
    try std.testing.expectEqual(phase_before_begin, begin_session.phase);
    try std.testing.expectEqualDeep(completion_before_begin, begin_session.completion);
    try std.testing.expectEqual(std.math.maxInt(u64), begin_session.state_revision);

    const abort_session = try state.Session.create(&config);
    defer abort_session.destroy();
    try std.testing.expectEqual(
        ResultCode.ok,
        replaceCatalog(abort_session, 1, 1, 100, &sources, &rules),
    );
    source_plans = std.mem.zeroes([4]protocol.SourcePlanOutput);
    metric_plans = std.mem.zeroes([4]protocol.MetricPlanOutput);
    try std.testing.expectEqual(
        ResultCode.ok,
        runPlan(
            abort_session,
            2,
            1,
            200,
            0,
            &requested,
            &modes,
            &plan_output,
            &source_plans,
            &metric_plans,
        ),
    );
    header.capability_generation = source_plans[0].capability_generation;
    header.plan_token_fingerprint = source_plans[0].plan_token_fingerprint;
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.beginCompletion(abort_session, &header),
    );
    const completion_before_abort = abort_session.completion;
    abort_session.state_revision = std.math.maxInt(u64);
    var abort_input = finalizeInput(3, 1, 10, 300);
    try std.testing.expectEqual(
        ResultCode.unavailable,
        session_module.abortCompletion(abort_session, &abort_input),
    );
    try std.testing.expectEqual(protocol.Phase.completion_open, abort_session.phase);
    try std.testing.expectEqualDeep(completion_before_abort, abort_session.completion);

    const reset_session = try state.Session.create(&config);
    defer reset_session.destroy();
    try std.testing.expectEqual(
        ResultCode.ok,
        replaceCatalog(reset_session, 1, 1, 100, &sources, &rules),
    );
    const catalog_before_reset = reset_session.catalog_fingerprint;
    const semantic_before_reset = reset_session.semantic_fingerprint;
    reset_session.committed_generation = std.math.maxInt(u64);
    var reset_input = controlInput(2, 200);
    try std.testing.expectEqual(
        ResultCode.unavailable,
        session_module.reset(reset_session, &reset_input),
    );
    try std.testing.expectEqual(catalog_before_reset, reset_session.catalog_fingerprint);
    try std.testing.expectEqual(semantic_before_reset, reset_session.semantic_fingerprint);
    try std.testing.expectEqual(std.math.maxInt(u64), reset_session.committed_generation);
}

test "CPU counter reset reasons cover overflow regression and frequency changes" {
    var config = testConfig(1);
    const session = try state.Session.create(&config);
    defer session.destroy();
    var sources = testSources();
    var rules = testRules();
    try std.testing.expectEqual(
        ResultCode.ok,
        replaceCatalog(session, 1, 1, 100, &sources, &rules),
    );

    try completeCpuCycleWithFrequency(session, 2, 3, 1, 200, 300, 1, 0, 0, 0, 100, 1_000);
    try completeCpuCycleWithFrequency(
        session,
        4,
        5,
        2,
        400,
        500,
        2,
        0,
        std.math.maxInt(u64),
        std.math.maxInt(u64),
        200,
        1_000,
    );
    try std.testing.expectEqual(
        protocol.SourceResetReason.arithmetic_overflow,
        session.active.sources[0].last_reset_reason_mask,
    );
    try std.testing.expectEqual(@as(u32, 1), session.active.sources[0].reset_count);

    try completeCpuCycleWithFrequency(
        session,
        6,
        7,
        3,
        600,
        700,
        3,
        0,
        1,
        1,
        150,
        2_000,
    );
    const expected = protocol.SourceResetReason.counter_regressed |
        protocol.SourceResetReason.monotonic_regressed |
        protocol.SourceResetReason.tick_frequency_changed;
    try std.testing.expectEqual(expected, session.active.sources[0].last_reset_reason_mask);
    try std.testing.expectEqual(@as(u32, 2), session.active.sources[0].reset_count);
}

test "catalog replacement preserves only the exact GPU inventory source definition" {
    var config = testConfig(1);
    const session = try state.Session.create(&config);
    defer session.destroy();
    var sources = testSources();
    var rules = testRules();
    try std.testing.expectEqual(
        ResultCode.ok,
        replaceCatalog(session, 1, 1, 100, &sources, &rules),
    );

    var source_plans = std.mem.zeroes([4]protocol.SourcePlanOutput);
    var metric_plans = std.mem.zeroes([4]protocol.MetricPlanOutput);
    var plan_output = std.mem.zeroes(protocol.PlanOutput);
    const requested = [_]protocol.PlanMetricInput{planMetric(200)};
    const modes = testModes();
    try std.testing.expectEqual(
        ResultCode.ok,
        runPlan(
            session,
            2,
            1,
            200,
            protocol.PlanFlags.include_gpu_inventory,
            &requested,
            &modes,
            &plan_output,
            &source_plans,
            &metric_plans,
        ),
    );
    try completeGpuCycle(
        session,
        3,
        1,
        1,
        300,
        source_plans[1],
        &.{
            gpuInput(101, 1, 11, 1_001, 1, 300),
            gpuInput(102, 2, 22, 1_002, 1, 300),
        },
        .complete,
        2,
    );
    try std.testing.expectEqual(@as(u32, 2), @import("inventory.zig").activeCount(session));

    try std.testing.expectEqual(
        ResultCode.ok,
        replaceCatalog(session, 4, 2, 400, &sources, &rules),
    );
    try std.testing.expectEqual(@as(u32, 2), @import("inventory.zig").activeCount(session));

    sources[2].capability_mask = 0x40;
    sources[2].semantic_fingerprint += 1;
    try std.testing.expectEqual(
        ResultCode.ok,
        replaceCatalog(session, 5, 3, 500, &sources, &rules),
    );
    try std.testing.expectEqual(@as(u32, 0), @import("inventory.zig").activeCount(session));
}

test "catalog generation exhaustion restores prepared GPU staging exactly" {
    var config = testConfig(1);
    const session = try state.Session.create(&config);
    defer session.destroy();
    var sources = testSources();
    var rules = testRules();
    try std.testing.expectEqual(
        ResultCode.ok,
        replaceCatalog(session, 1, 1, 100, &sources, &rules),
    );

    var source_plans = std.mem.zeroes([4]protocol.SourcePlanOutput);
    var metric_plans = std.mem.zeroes([4]protocol.MetricPlanOutput);
    var plan_output = std.mem.zeroes(protocol.PlanOutput);
    const requested = [_]protocol.PlanMetricInput{planMetric(200)};
    const modes = testModes();
    try std.testing.expectEqual(
        ResultCode.ok,
        runPlan(
            session,
            2,
            1,
            200,
            protocol.PlanFlags.include_gpu_inventory,
            &requested,
            &modes,
            &plan_output,
            &source_plans,
            &metric_plans,
        ),
    );
    try completeGpuCycle(
        session,
        3,
        1,
        1,
        300,
        source_plans[1],
        &.{
            gpuInput(101, 1, 11, 1_001, 1, 300),
            gpuInput(102, 2, 22, 1_002, 1, 300),
        },
        .complete,
        2,
    );
    const first_fingerprint = session.gpu_active[0].output.semantic_fingerprint;
    const second_fingerprint = session.gpu_active[1].output.semantic_fingerprint;
    const active_semantic = session.semantic_fingerprint;

    sources[2].capability_mask = 0x40;
    sources[2].semantic_fingerprint += 1;
    session.committed_generation = std.math.maxInt(u64);
    try std.testing.expectEqual(
        ResultCode.unavailable,
        replaceCatalog(session, 4, 2, 400, &sources, &rules),
    );
    try std.testing.expectEqual(active_semantic, session.semantic_fingerprint);
    try std.testing.expectEqual(@as(u32, 2), @import("inventory.zig").activeCount(session));
    try std.testing.expectEqual(@as(u32, 2), session.gpu_staging_count);
    try std.testing.expectEqual(
        first_fingerprint,
        session.gpu_staging[0].output.semantic_fingerprint,
    );
    try std.testing.expectEqual(
        second_fingerprint,
        session.gpu_staging[1].output.semantic_fingerprint,
    );
}

test "persistence rejects rollback drift and revision exhaustion without publishing staging" {
    var config = testConfig(1);
    const source_session = try state.Session.create(&config);
    defer source_session.destroy();
    var sources = testSources();
    var rules = testRules();
    try std.testing.expectEqual(
        ResultCode.ok,
        replaceCatalog(source_session, 1, 1, 100, &sources, &rules),
    );
    source_session.state_revision = std.math.maxInt(u64);

    var source_outputs = std.mem.zeroes([source_count]protocol.SourcePersistenceOutput);
    var rule_outputs = std.mem.zeroes([rule_count]protocol.RuleStateOutput);
    var gpu_outputs = std.mem.zeroes([0]protocol.GpuInventoryOutput);
    var persisted = std.mem.zeroes(protocol.PersistenceHeader);
    var snapshot = std.mem.zeroes(protocol.SnapshotHeader);
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.querySnapshotHeader(source_session, &snapshot),
    );
    var read = readInput(snapshot);
    try std.testing.expectEqual(
        ResultCode.ok,
        persistence.exportState(
            source_session,
            &read,
            &persisted,
            &source_outputs,
            &rule_outputs,
            &gpu_outputs,
        ),
    );

    const target = try state.Session.create(&config);
    defer target.destroy();
    try std.testing.expectEqual(
        ResultCode.ok,
        replaceCatalog(target, 1, 1, 100, &sources, &rules),
    );
    var import_input = persistenceInput(2, 200);
    const target_revision = target.state_revision;
    const target_phase = target.phase;
    const target_semantic = target.semantic_fingerprint;

    var rollback = persisted;
    rollback.committed_generation = 0;
    try std.testing.expectEqual(
        ResultCode.abi_mismatch,
        persistence.importState(
            target,
            &import_input,
            &rollback,
            &source_outputs,
            &rule_outputs,
            &gpu_outputs,
        ),
    );
    var drift = persisted;
    drift.committed_generation = target.committed_generation;
    drift.semantic_fingerprint ^= 1;
    try std.testing.expectEqual(
        ResultCode.abi_mismatch,
        persistence.importState(
            target,
            &import_input,
            &drift,
            &source_outputs,
            &rule_outputs,
            &gpu_outputs,
        ),
    );
    try std.testing.expectEqual(
        ResultCode.unavailable,
        persistence.importState(
            target,
            &import_input,
            &persisted,
            &source_outputs,
            &rule_outputs,
            &gpu_outputs,
        ),
    );
    try std.testing.expectEqual(target_revision, target.state_revision);
    try std.testing.expectEqual(target_phase, target.phase);
    try std.testing.expectEqual(target_semantic, target.semantic_fingerprint);
}

test "declared capacities and resident budget are exact production gates" {
    var config = testConfig(1);
    config.maximum_source_count = source_count;
    config.maximum_metric_count = metric_count;
    config.maximum_rule_count = rule_count;
    config.maximum_requested_count = metric_count;
    config.maximum_observation_count = rule_count;
    config.maximum_gpu_adapter_count = 2;
    config.maximum_persistence_source_count = source_count;
    config.maximum_persistence_rule_count = rule_count;
    config.maximum_persistence_gpu_count = 2;
    config.source_index_capacity = 4;
    config.metric_index_capacity = 2;
    config.rule_index_capacity = 4;
    config.gpu_index_capacity = 2;
    config.maximum_plan_metric_count = metric_count;
    config.maximum_source_mode_count = source_count;
    config.maximum_source_plan_count = source_count;
    config.maximum_metric_plan_count = rule_count;
    config.gpu_luid_index_capacity = 2;
    config.gpu_key_index_capacity = 2;

    const probe = try state.Session.create(&config);
    const exact_resident = probe.residentByteCount();
    probe.destroy();
    config.resident_byte_budget = exact_resident;
    const session = try state.Session.create(&config);
    defer session.destroy();
    try std.testing.expectEqual(exact_resident, session.residentByteCount());
    try std.testing.expectEqual(exact_resident, session.capacity().resident_byte_count);

    var sources = testSources();
    var rules = testRules();
    try std.testing.expectEqual(
        ResultCode.ok,
        replaceCatalog(session, 1, 1, 100, &sources, &rules),
    );
    var source_plans = std.mem.zeroes([source_count]protocol.SourcePlanOutput);
    var metric_plans = std.mem.zeroes([rule_count]protocol.MetricPlanOutput);
    var plan_output = std.mem.zeroes(protocol.PlanOutput);
    const requested = [_]protocol.PlanMetricInput{
        planMetric(100),
        planMetric(200),
    };
    const modes = testModes();
    try std.testing.expectEqual(
        ResultCode.ok,
        runPlan(
            session,
            2,
            1,
            200,
            protocol.PlanFlags.include_gpu_inventory,
            &requested,
            &modes,
            &plan_output,
            &source_plans,
            &metric_plans,
        ),
    );
    try std.testing.expectEqual(@as(u32, 2), plan_output.metric_plan_count);
    try completeGpuCycle(
        session,
        3,
        1,
        1,
        300,
        source_plans[1],
        &.{
            gpuInput(101, 1, 11, 1_001, 1, 300),
            gpuInput(102, 2, 22, 1_002, 1, 300),
        },
        .complete,
        2,
    );
    try std.testing.expectEqual(@as(u32, 2), @import("inventory.zig").activeCount(session));
    try std.testing.expect(!session.gpu_staging_prepared);
    const first_gpu_fingerprint = session.gpu_active[0].output.semantic_fingerprint;
    const second_gpu_fingerprint = session.gpu_active[1].output.semantic_fingerprint;

    var cpu_header = metricCompletion(4, 1, 10, 1, 2, 400, 2, 1, 1);
    cpu_header.capability_generation = source_plans[0].capability_generation;
    cpu_header.plan_token_fingerprint = source_plans[0].plan_token_fingerprint;
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.beginCompletion(session, &cpu_header),
    );
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.submitRequested(
            session,
            &.{ requestedRule(1_001), requestedRule(2_001) },
        ),
    );
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.submitObservations(
            session,
            &.{unsignedObservation(2_001, 200, 10, 2, 400, 4096)},
        ),
    );
    var counter = cpuCounter(2, 400, 100, 500, 500, 1_000);
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.submitCpuCounter(session, &counter),
    );
    var cpu_finalize = finalizeInput(4, 1, 10, 400);
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.finalizeCompletion(session, &cpu_finalize),
    );
    try std.testing.expect(!session.gpu_staging_prepared);
    try std.testing.expectEqual(@as(u32, 2), @import("inventory.zig").activeCount(session));
    try std.testing.expectEqual(
        first_gpu_fingerprint,
        session.gpu_active[0].output.semantic_fingerprint,
    );
    try std.testing.expectEqual(
        second_gpu_fingerprint,
        session.gpu_active[1].output.semantic_fingerprint,
    );

    config.resident_byte_budget = exact_resident - 1;
    try std.testing.expectError(error.InvalidConfiguration, state.Session.create(&config));
}

fn completeCpuCycle(
    session: *state.Session,
    plan_operation_epoch: u64,
    completion_operation_epoch: u64,
    plan_epoch: u64,
    plan_at: u64,
    captured_at: u64,
    sequence: u64,
    idle_ticks: u64,
    kernel_ticks: u64,
    user_ticks: u64,
    monotonic_ticks: u64,
) !void {
    return completeCpuCycleWithFrequency(
        session,
        plan_operation_epoch,
        completion_operation_epoch,
        plan_epoch,
        plan_at,
        captured_at,
        sequence,
        idle_ticks,
        kernel_ticks,
        user_ticks,
        monotonic_ticks,
        1_000,
    );
}

fn completeCpuCycleWithFrequency(
    session: *state.Session,
    plan_operation_epoch: u64,
    completion_operation_epoch: u64,
    plan_epoch: u64,
    plan_at: u64,
    captured_at: u64,
    sequence: u64,
    idle_ticks: u64,
    kernel_ticks: u64,
    user_ticks: u64,
    monotonic_ticks: u64,
    monotonic_ticks_per_second: u64,
) !void {
    var source_plans = std.mem.zeroes([4]protocol.SourcePlanOutput);
    var metric_plans = std.mem.zeroes([4]protocol.MetricPlanOutput);
    var plan_output = std.mem.zeroes(protocol.PlanOutput);
    const requested = [_]protocol.PlanMetricInput{planMetric(100)};
    const modes = testModes();
    try std.testing.expectEqual(
        ResultCode.ok,
        runPlan(
            session,
            plan_operation_epoch,
            plan_epoch,
            plan_at,
            0,
            &requested,
            &modes,
            &plan_output,
            &source_plans,
            &metric_plans,
        ),
    );
    var header = metricCompletion(
        completion_operation_epoch,
        plan_epoch,
        10,
        1,
        sequence,
        captured_at,
        1,
        0,
        1,
    );
    header.capability_generation = source_plans[0].capability_generation;
    header.plan_token_fingerprint = source_plans[0].plan_token_fingerprint;
    try std.testing.expectEqual(ResultCode.ok, session_module.beginCompletion(session, &header));
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.submitRequested(session, &.{requestedRule(1_001)}),
    );
    try std.testing.expectEqual(ResultCode.ok, session_module.submitObservations(session, &.{}));
    var counter = cpuCounter(
        sequence,
        captured_at,
        idle_ticks,
        kernel_ticks,
        user_ticks,
        monotonic_ticks,
    );
    counter.monotonic_ticks_per_second = monotonic_ticks_per_second;
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.submitCpuCounter(session, &counter),
    );
    var finalize = finalizeInput(
        completion_operation_epoch,
        plan_epoch,
        10,
        captured_at,
    );
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.finalizeCompletion(session, &finalize),
    );
}

fn completeGpuCycle(
    session: *state.Session,
    completion_operation_epoch: u64,
    plan_epoch: u64,
    sequence: u64,
    captured_at: u64,
    source_plan: protocol.SourcePlanOutput,
    inputs: []const protocol.GpuInventoryInput,
    status: protocol.SourceStatus,
    expected_count: u32,
) !void {
    var header = gpuCompletion(
        completion_operation_epoch,
        plan_epoch,
        30,
        1,
        sequence,
        captured_at,
        @intCast(inputs.len),
    );
    header.capability_generation = source_plan.capability_generation;
    header.plan_token_fingerprint = source_plan.plan_token_fingerprint;
    header.expected_gpu_inventory_count = expected_count;
    header.status = @intFromEnum(status);
    header.overflow_count = expected_count - @as(u32, @intCast(inputs.len));
    try std.testing.expectEqual(ResultCode.ok, session_module.beginCompletion(session, &header));
    try std.testing.expectEqual(ResultCode.ok, session_module.submitRequested(session, &.{}));
    try std.testing.expectEqual(ResultCode.ok, session_module.submitObservations(session, &.{}));
    try std.testing.expectEqual(ResultCode.ok, session_module.submitGpuInventory(session, inputs));
    var finalize = finalizeInput(
        completion_operation_epoch,
        plan_epoch,
        30,
        captured_at,
    );
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.finalizeCompletion(session, &finalize),
    );
}

fn controlInput(operation_epoch: u64, command_at: u64) protocol.ControlInput {
    return .{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.ControlInput),
        .configuration_generation = 1,
        .operation_epoch = operation_epoch,
        .command_at_milliseconds = command_at,
        .flags = 0,
        .reserved = .{ 0, 0 },
    };
}

fn testConfig(generation: u64) protocol.Config {
    return .{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.Config),
        .generation = generation,
        .maximum_source_count = 4,
        .maximum_metric_count = 4,
        .maximum_rule_count = 8,
        .maximum_requested_count = 4,
        .maximum_observation_count = 8,
        .maximum_gpu_adapter_count = 4,
        .maximum_persistence_source_count = 4,
        .maximum_persistence_rule_count = 8,
        .maximum_persistence_gpu_count = 4,
        .source_index_capacity = 8,
        .metric_index_capacity = 8,
        .rule_index_capacity = 16,
        .gpu_index_capacity = 8,
        .maximum_future_skew_milliseconds = 1_000,
        .resident_byte_budget = 1 << 20,
        .catalog_contract_version = protocol.catalog_contract_version,
        .value_contract_version = protocol.value_contract_version,
        .observation_contract_version = protocol.observation_contract_version,
        .inventory_contract_version = protocol.inventory_contract_version,
        .persistence_contract_version = protocol.persistence_contract_version,
        .flags = 0,
        .maximum_plan_metric_count = 4,
        .maximum_source_mode_count = 4,
        .maximum_source_plan_count = 4,
        .maximum_metric_plan_count = 8,
        .gpu_luid_index_capacity = 8,
        .gpu_key_index_capacity = 8,
        .reserved_u32 = 0,
        .reserved = .{ 0, 0 },
    };
}

fn testSources() [source_count]protocol.SourcePolicyInput {
    return .{
        sourcePolicy(10, .metrics, 1, 0x01),
        sourcePolicy(20, .metrics, 2, 0x02),
        sourcePolicy(30, .gpu_inventory, 3, 0x04),
    };
}

fn sourcePolicy(
    handle: u64,
    role: protocol.SourceRole,
    priority: u32,
    capability: u64,
) protocol.SourcePolicyInput {
    return .{
        .struct_size = @sizeOf(protocol.SourcePolicyInput),
        .flags = 0,
        .source_handle = handle,
        .source_role = @intFromEnum(role),
        .priority = priority,
        .retention_policy = @intFromEnum(protocol.RetentionPolicy.retain_last_good),
        .reserved_u32 = 0,
        .capability_mask = capability,
        .semantic_fingerprint = handle * 100 + priority,
        .reserved = .{ 0, 0 },
    };
}

fn testRules() [rule_count]protocol.MetricDefinitionInput {
    return .{
        metricDefinition(
            1_001,
            100,
            10,
            1,
            1,
            .cpu_usage,
            .cpu,
            .float64,
            protocol.MetricFlags.percentage | protocol.MetricFlags.nonnegative,
            @bitCast(@as(f64, 0)),
            @bitCast(@as(f64, 100)),
        ),
        metricDefinition(
            1_002,
            100,
            20,
            1,
            2,
            .cpu_usage,
            .cpu,
            .float64,
            protocol.MetricFlags.percentage | protocol.MetricFlags.nonnegative,
            @bitCast(@as(f64, 0)),
            @bitCast(@as(f64, 100)),
        ),
        metricDefinition(
            2_001,
            200,
            10,
            2,
            1,
            .memory_used,
            .memory,
            .unsigned64,
            protocol.MetricFlags.nonnegative | protocol.MetricFlags.used_value,
            0,
            1 << 40,
        ),
    };
}

fn metricDefinition(
    rule: u64,
    metric: u64,
    source: u64,
    scope: u64,
    priority: u32,
    metric_kind: protocol.MetricKind,
    scope_kind: protocol.ScopeKind,
    value_kind: protocol.ValueKind,
    flags: u32,
    minimum: u64,
    maximum: u64,
) protocol.MetricDefinitionInput {
    return .{
        .struct_size = @sizeOf(protocol.MetricDefinitionInput),
        .flags = flags,
        .rule_handle = rule,
        .metric_handle = metric,
        .source_handle = source,
        .scope_handle = scope,
        .capability_mask = if (source == 20) 0x02 else 0x01,
        .metric_kind = @intFromEnum(metric_kind),
        .scope_kind = @intFromEnum(scope_kind),
        .value_kind = @intFromEnum(value_kind),
        .retention_policy = @intFromEnum(protocol.RetentionPolicy.retain_last_good),
        .source_priority = priority,
        .reserved_u32 = 0,
        .minimum_value_bits = minimum,
        .maximum_value_bits = maximum,
        .semantic_fingerprint = rule * 100 + metric,
    };
}

fn replaceCatalog(
    session: *state.Session,
    operation_epoch: u64,
    catalog_generation: u64,
    command_at: u64,
    sources: []const protocol.SourcePolicyInput,
    rules: []const protocol.MetricDefinitionInput,
) ResultCode {
    var input = protocol.CatalogReplaceInput{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.CatalogReplaceInput),
        .configuration_generation = session.config.generation,
        .catalog_generation = catalog_generation,
        .operation_epoch = operation_epoch,
        .command_at_milliseconds = command_at,
        .source_count = @intCast(sources.len),
        .rule_count = @intCast(rules.len),
        .metric_count = metric_count,
        .gpu_inventory_source_count = 1,
        .source_index_capacity = session.config.source_index_capacity,
        .metric_index_capacity = session.config.metric_index_capacity,
        .rule_index_capacity = session.config.rule_index_capacity,
        .flags = 0,
        .semantic_fingerprint = session_module.catalogFingerprint(sources, rules),
        .reserved = .{ 0, 0 },
    };
    return session_module.replaceCatalog(session, &input, sources, rules);
}

fn planMetric(handle: u64) protocol.PlanMetricInput {
    return .{
        .struct_size = @sizeOf(protocol.PlanMetricInput),
        .flags = 0,
        .metric_handle = handle,
        .reserved = .{ 0, 0 },
    };
}

fn testModes() [source_count]protocol.SourceModeInput {
    return .{
        sourceMode(10),
        sourceMode(20),
        sourceMode(30),
    };
}

fn sourceMode(handle: u64) protocol.SourceModeInput {
    return .{
        .struct_size = @sizeOf(protocol.SourceModeInput),
        .flags = 0,
        .source_handle = handle,
        .capability_generation = 1,
        .zone_mode = @intFromEnum(protocol.ZoneMode.normal),
        .availability = @intFromEnum(protocol.SourceAvailability.available),
        .reserved = .{ 0, 0 },
    };
}

fn runPlan(
    session: *state.Session,
    operation_epoch: u64,
    plan_epoch: u64,
    command_at: u64,
    flags: u64,
    requested: []const protocol.PlanMetricInput,
    modes: []const protocol.SourceModeInput,
    output: *protocol.PlanOutput,
    source_plans: []protocol.SourcePlanOutput,
    metric_plans: []protocol.MetricPlanOutput,
) ResultCode {
    var input = protocol.PlanInput{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.PlanInput),
        .configuration_generation = session.config.generation,
        .catalog_generation = session.catalog_generation,
        .operation_epoch = operation_epoch,
        .plan_epoch = plan_epoch,
        .command_at_milliseconds = command_at,
        .requested_metric_count = @intCast(requested.len),
        .source_mode_count = @intCast(modes.len),
        .source_plan_capacity = @intCast(source_plans.len),
        .metric_plan_capacity = @intCast(metric_plans.len),
        .flags = flags,
        .reserved = .{ 0, 0 },
    };
    return session_module.plan(
        session,
        &input,
        requested,
        modes,
        output,
        source_plans,
        metric_plans,
    );
}

fn requestedRule(handle: u64) protocol.RequestedMetricInput {
    return .{
        .struct_size = @sizeOf(protocol.RequestedMetricInput),
        .flags = 0,
        .rule_handle = handle,
        .reserved = .{ 0, 0 },
    };
}

fn unsignedObservation(
    rule: u64,
    metric: u64,
    source: u64,
    scope: u64,
    observed_at: u64,
    value: u64,
) protocol.ObservationInput {
    return .{
        .struct_size = @sizeOf(protocol.ObservationInput),
        .flags = 0,
        .rule_handle = rule,
        .metric_handle = metric,
        .source_handle = source,
        .scope_handle = scope,
        .source_observation_sequence = if (observed_at == 300) 1 else 2,
        .observed_at_milliseconds = observed_at,
        .value_bits = value,
        .capability_mask = 0x01,
        .valid_mask = protocol.ObservationValid.required | protocol.ObservationValid.value,
        .status = @intFromEnum(protocol.ObservationStatus.current),
        .value_kind = @intFromEnum(protocol.ValueKind.unsigned64),
        .quality = 100,
        .reserved_u32 = 0,
        .sample_duration_milliseconds = 0,
    };
}

fn cpuCounter(
    sequence: u64,
    observed_at: u64,
    idle: u64,
    kernel: u64,
    user: u64,
    monotonic: u64,
) protocol.CpuCounterInput {
    return .{
        .struct_size = @sizeOf(protocol.CpuCounterInput),
        .flags = 0,
        .rule_handle = 1_001,
        .metric_handle = 100,
        .source_handle = 10,
        .source_incarnation = 1,
        .source_observation_sequence = sequence,
        .observed_at_milliseconds = observed_at,
        .monotonic_ticks = monotonic,
        .monotonic_ticks_per_second = 1_000,
        .idle_ticks = idle,
        .kernel_ticks = kernel,
        .user_ticks = user,
        .capability_mask = 0x01,
        .valid_mask = protocol.CpuCounterValid.required,
        .status = @intFromEnum(protocol.ObservationStatus.current),
        .counter_contract_version = protocol.cpu_counter_contract_version,
        .reserved = .{ 0, 0 },
    };
}

fn metricCompletion(
    operation_epoch: u64,
    plan_epoch: u64,
    source_handle: u64,
    incarnation: u64,
    sequence: u64,
    captured_at: u64,
    requested_count: u32,
    observation_count: u32,
    cpu_count: u32,
) protocol.CompletionHeader {
    return .{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.CompletionHeader),
        .configuration_generation = 1,
        .catalog_generation = 1,
        .operation_epoch = operation_epoch,
        .plan_epoch = plan_epoch,
        .source_handle = source_handle,
        .source_incarnation = incarnation,
        .source_observation_sequence = sequence,
        .capability_generation = 0,
        .plan_token_fingerprint = 0,
        .command_at_milliseconds = captured_at,
        .captured_at_milliseconds = captured_at,
        .requested_rule_count = requested_count,
        .observation_count = observation_count,
        .gpu_inventory_count = 0,
        .expected_gpu_inventory_count = 0,
        .cpu_counter_count = cpu_count,
        .overflow_count = 0,
        .status = @intFromEnum(protocol.SourceStatus.complete),
        .flags = 0,
        .reserved = .{ 0, 0 },
    };
}

fn gpuCompletion(
    operation_epoch: u64,
    plan_epoch: u64,
    source_handle: u64,
    incarnation: u64,
    sequence: u64,
    captured_at: u64,
    count: u32,
) protocol.CompletionHeader {
    var output = metricCompletion(
        operation_epoch,
        plan_epoch,
        source_handle,
        incarnation,
        sequence,
        captured_at,
        0,
        0,
        0,
    );
    output.gpu_inventory_count = count;
    output.expected_gpu_inventory_count = count;
    return output;
}

fn gpuInput(
    adapter: u64,
    luid_low: u64,
    luid_high: u64,
    stable_key: u64,
    sequence: u64,
    observed_at: u64,
) protocol.GpuInventoryInput {
    return .{
        .struct_size = @sizeOf(protocol.GpuInventoryInput),
        .flags = 0,
        .adapter_handle = adapter,
        .source_handle = 30,
        .source_observation_sequence = sequence,
        .observed_at_milliseconds = observed_at,
        .adapter_luid_low = luid_low,
        .adapter_luid_high = luid_high,
        .stable_key_handle = stable_key,
        .capability_mask = 0x04,
        .topology_fingerprint = adapter * 10,
        .valid_mask = protocol.InventoryValid.required,
        .status = @intFromEnum(protocol.InventoryStatus.current),
        .reserved_u32 = 0,
        .reserved = .{ 0, 0 },
    };
}

fn finalizeInput(
    operation_epoch: u64,
    plan_epoch: u64,
    source_handle: u64,
    command_at: u64,
) protocol.FinalizeInput {
    return .{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.FinalizeInput),
        .configuration_generation = 1,
        .catalog_generation = 1,
        .operation_epoch = operation_epoch,
        .plan_epoch = plan_epoch,
        .source_handle = source_handle,
        .command_at_milliseconds = command_at,
        .flags = 0,
        .reserved = .{ 0, 0 },
    };
}

fn readInput(header: protocol.SnapshotHeader) protocol.ReadInput {
    return .{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.ReadInput),
        .configuration_generation = header.configuration_generation,
        .catalog_generation = header.catalog_generation,
        .catalog_fingerprint = header.catalog_fingerprint,
        .state_revision = header.state_revision,
        .committed_generation = header.committed_generation,
        .flags = 0,
        .reserved = .{ 0, 0 },
    };
}

fn ruleStateFingerprint(input: *const protocol.RuleStateOutput) u64 {
    var copy = input.*;
    copy.state_fingerprint = 0;
    const value = std.hash.Wyhash.hash(
        0x726d_6d65_7472_7275,
        std.mem.asBytes(&copy),
    );
    return if (value == 0) 1 else value;
}

fn sourceStateFingerprint(input: *const protocol.SourcePersistenceOutput) u64 {
    var copy = input.*;
    copy.state_fingerprint = 0;
    const value = std.hash.Wyhash.hash(
        0x726d_6d65_7472_7073,
        std.mem.asBytes(&copy),
    );
    return if (value == 0) 1 else value;
}

fn gpuOutputFingerprint(input: *const protocol.GpuInventoryOutput) u64 {
    const semantic_fields = [_]u64{
        input.flags,
        input.adapter_handle,
        input.source_handle,
        input.adapter_luid_low,
        input.adapter_luid_high,
        input.stable_key_handle,
        input.capability_mask,
        input.topology_fingerprint,
        input.valid_mask,
        input.status,
    };
    const value = std.hash.Wyhash.hash(
        0x726d_6770_756f_7574,
        std.mem.asBytes(&semantic_fields),
    );
    return if (value == 0) 1 else value;
}

fn persistenceChecksum(
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

fn persistenceInput(operation_epoch: u64, command_at: u64) protocol.PersistenceInput {
    return .{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.PersistenceInput),
        .configuration_generation = 1,
        .catalog_generation = 1,
        .operation_epoch = operation_epoch,
        .command_at_milliseconds = command_at,
        .flags = 0,
        .reserved = .{ 0, 0 },
    };
}
