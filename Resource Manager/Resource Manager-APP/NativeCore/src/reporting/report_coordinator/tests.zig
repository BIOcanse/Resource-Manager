const std = @import("std");
const protocol = @import("protocol.zig");
const state = @import("state.zig");
const persistence = @import("persistence.zig");
const session_module = @import("session.zig");
const abi = @import("abi.zig");
const ResultCode = @import("../../common/result_codes.zig").ResultCode;

const source_handle: u64 = 11;
const source_generation: u64 = 12;
const coverage_scope_handle: u64 = 13;
const rule_handle: u64 = 21;
const rule_generation: u64 = 22;
const family_handle: u64 = 31;
const target_handle: u64 = 41;

test "configuration rejects insufficient explicit bucket capacity" {
    var config = testConfig();
    config.maximum_bucket_count -= 1;
    try std.testing.expect(!protocol.validConfig(&config));
}

test "configuration rejects persistence capacity that cannot hold every dirty row" {
    var config = testConfig();
    config.maximum_persistence_operation_count =
        1 +
        config.maximum_source_count +
        config.maximum_observation_count +
        config.maximum_bucket_count +
        config.maximum_report_count +
        config.maximum_trust_count -
        1;
    try std.testing.expect(!protocol.validConfig(&config));
}

test "configuration rejects undersized planned persistence index" {
    var config = testConfig();
    config.planned_persistence_index_capacity =
        config.maximum_persistence_operation_count * 2 - 1;
    try std.testing.expect(!protocol.validConfig(&config));
}

test "retired ABI configuration and metadata checkpoint are rejected" {
    var retired_config = testConfig();
    retired_config.abi_version = 0x0005_0000;
    try std.testing.expect(!protocol.validConfig(&retired_config));

    var config = testConfig();
    const session = try state.Session.create(&config);
    defer session.destroy();
    var rule_row = rule();
    var replace = ruleReplaceInput(1, 1, 100, 1);
    try expectCode(.ok, session_module.replaceRules(
        session,
        &replace,
        @ptrCast(&rule_row),
        1,
    ));

    var retired_metadata = persistedMetadataRow(1, 100);
    retired_metadata.checkpoint_schema_version = 0x0005_0000;
    const rows = [_]protocol.PersistenceOperation{retired_metadata};
    var import_input = importInput(2, 2, 100, 1, rows.len);
    try expectCode(.invalid_argument, session_module.importPersisted(
        session,
        &import_input,
        &rows,
        rows.len,
    ));
    try std.testing.expect(!session.import_ready);
}

test "resident budget exactly covers the single fixed storage backing" {
    var config = testConfig();
    const required = try state.requiredResidentBytes(&config);
    config.resident_byte_budget = required;
    const session = try state.Session.create(&config);
    defer session.destroy();
    try std.testing.expectEqual(required, session.resident_byte_count);
    try std.testing.expectEqual(required, session.capacity().resident_byte_count);

    var insufficient = config;
    insufficient.resident_byte_budget = required - 1;
    try std.testing.expectError(
        error.InvalidConfiguration,
        state.Session.create(&insufficient),
    );
}

test "hot reconfigure preserves session incarnation and revision never wraps" {
    var config = testConfig();
    const session = try state.Session.create(&config);
    defer session.destroy();

    var changed_incarnation = config;
    changed_incarnation.generation = 2;
    changed_incarnation.session_instance_low += 1;
    try expectCode(.invalid_argument, session.reconfigure(&changed_incarnation));
    try std.testing.expectEqual(config.session_instance_low, session.config.session_instance_low);

    var hot = config;
    hot.generation = 2;
    hot.default_stale_after_milliseconds += 1;
    hot.default_retention_milliseconds += 1;
    try expectCode(.ok, session.reconfigure(&hot));
    session.state_revision = std.math.maxInt(u64);
    session.advanceRevision();
    try std.testing.expectEqual(std.math.maxInt(u64), session.state_revision);
}

test "complete snapshots own hit and miss state while unavailable preserves it" {
    var fixture = try Fixture.create();
    defer fixture.destroy();

    try fixture.observeComplete(3, 3, 1, 100, &.{fact(1, 60)});
    const observation_index = fixture.session.findObservation(
        source_handle,
        coverage_scope_handle,
        rule_handle,
        target_handle,
    ).?;
    try std.testing.expect(!fixture.session.observations[observation_index].active);
    try std.testing.expectEqual(
        @as(u64, 1),
        fixture.session.observations[observation_index].active_sample_count,
    );

    try fixture.observeUnavailable(4, 4, 2, 110);
    try std.testing.expect(!fixture.session.observations[observation_index].active);
    try std.testing.expectEqual(
        @as(u64, 1),
        fixture.session.observations[observation_index].active_sample_count,
    );

    try fixture.observeComplete(5, 5, 3, 120, &.{fact(2, 70)});
    try std.testing.expect(fixture.session.observations[observation_index].active);
    const report_index = fixture.session.findReport(target_handle, family_handle).?;
    const stable_report_handle = fixture.session.reports[report_index].report_handle;

    try fixture.observeComplete(6, 6, 4, 130, &.{});
    try std.testing.expect(fixture.session.observations[observation_index].active);
    try std.testing.expectEqual(
        @as(u32, 1),
        fixture.session.observations[observation_index].consecutive_misses,
    );
    try fixture.observeComplete(7, 7, 5, 140, &.{});
    try std.testing.expect(!fixture.session.observations[observation_index].active);
    try std.testing.expectEqual(
        stable_report_handle,
        fixture.session.reports[report_index].report_handle,
    );
}

test "complete absence remains importable before and after report activation" {
    for ([_]bool{ false, true }) |initially_active| {
        var fixture = try Fixture.create();
        defer fixture.destroy();
        const sample_count: u64 = if (initially_active) 2 else 1;
        var operation: u64 = 3;
        var snapshot: u64 = 1;
        var utc: i64 = 100;
        var sequence: u64 = 1;
        while (sequence <= sample_count) : (sequence += 1) {
            try fixture.observeComplete(operation, operation, snapshot, utc, &.{
                fact(sequence, if (initially_active) 60 else 10),
            });
            operation += 1;
            snapshot += 1;
            utc += 10;
        }
        const index = fixture.session.findObservation(
            source_handle,
            coverage_scope_handle,
            rule_handle,
            target_handle,
        ).?;
        try std.testing.expectEqual(initially_active, fixture.session.observations[index].active);
        for (0..7) |_| {
            try fixture.observeComplete(operation, operation, snapshot, utc, &.{});
            operation += 1;
            snapshot += 1;
            utc += 10;
        }
        for ([_]protocol.SourceStatus{ .unavailable, .skipped }) |status| {
            try observeRows(fixture.session, sourceSnapshot(
                operation,
                operation,
                snapshot,
                utc,
                status,
                0,
            ), &.{});
            operation += 1;
            snapshot += 1;
            utc += 10;
        }
        try std.testing.expectEqual(@as(u32, 7), fixture.session.observations[index].consecutive_misses);
        try std.testing.expect(!fixture.session.observations[index].active);
        try std.testing.expectEqual(sample_count, fixture.session.observations[index].sample_count);

        var reports: [4]protocol.ReportOutput = undefined;
        var operations: [64]protocol.PersistenceOperation = undefined;
        var output: protocol.PlanOutput = undefined;
        var plan_input = planInput(operation, operation, 1, utc, reports.len, operations.len);
        try expectCode(.ok, session_module.plan(
            fixture.session,
            &plan_input,
            &reports,
            reports.len,
            &operations,
            operations.len,
            &output,
        ));
        const rows = operations[0..output.persistence_operation_count];
        var observation_rows: usize = 0;
        for (rows) |row| {
            if (row.operation_kind != @intFromEnum(protocol.PersistenceKind.observation)) continue;
            observation_rows += 1;
            try std.testing.expectEqual(@as(u32, 7), row.consecutive_misses);
        }
        try std.testing.expectEqual(@as(usize, 1), observation_rows);

        const restored = try state.Session.create(&fixture.config);
        defer restored.destroy();
        var rule_row = rule();
        var replace = ruleReplaceInput(1, 1, utc, 1);
        try expectCode(.ok, session_module.replaceRules(restored, &replace, @ptrCast(&rule_row), 1));
        var import_input = importInput(2, 2, utc, 1, @intCast(rows.len));
        try expectCode(.ok, session_module.importPersisted(restored, &import_input, rows.ptr, @intCast(rows.len)));
        const restored_index = restored.findObservation(
            source_handle,
            coverage_scope_handle,
            rule_handle,
            target_handle,
        ).?;
        try std.testing.expectEqual(@as(u32, 7), restored.observations[restored_index].consecutive_misses);
        try std.testing.expect(!restored.observations[restored_index].active);
        try std.testing.expectEqual(sample_count, restored.observations[restored_index].sample_count);

        try observeRows(restored, sourceSnapshot(3, 3, snapshot, utc + 10, .complete, 1), &.{fact(sequence, 60)});
        try std.testing.expectEqual(@as(u32, 0), restored.observations[restored_index].consecutive_misses);
        try std.testing.expect(!restored.observations[restored_index].active);
        try observeRows(restored, sourceSnapshot(4, 4, snapshot + 1, utc + 20, .complete, 1), &.{fact(sequence + 1, 60)});
        try std.testing.expect(restored.observations[restored_index].active);
    }
}

test "complete absence import rejects active state at or beyond the clear threshold" {
    for ([_]u32{ 2, 7, std.math.maxInt(u32) }) |misses| {
        var config = testConfig();
        const session = try state.Session.create(&config);
        defer session.destroy();
        var rule_row = rule();
        var replace = ruleReplaceInput(1, 1, 100, 1);
        try expectCode(.ok, session_module.replaceRules(session, &replace, @ptrCast(&rule_row), 1));
        var observation = persistedObservationRow();
        observation.consecutive_hits = 0;
        observation.active_sample_count = 0;
        observation.consecutive_misses = misses;
        var rows = [_]protocol.PersistenceOperation{
            persistedMetadataRow(3, 110),
            persistedSourceRow(),
            observation,
        };
        var import_input = importInput(2, 2, 110, 1, rows.len);
        try expectCode(.invalid_argument, session_module.importPersisted(session, &import_input, &rows, rows.len));
        try std.testing.expect(!session.import_ready);
        try std.testing.expect(session.findObservation(source_handle, coverage_scope_handle, rule_handle, target_handle) == null);

        rows[2].flags &= ~protocol.PersistenceFlags.active;
        try expectCode(.ok, session_module.importPersisted(session, &import_input, &rows, rows.len));
        const index = session.findObservation(source_handle, coverage_scope_handle, rule_handle, target_handle).?;
        try std.testing.expectEqual(misses, session.observations[index].consecutive_misses);
        try std.testing.expect(!session.observations[index].active);
    }
}

test "source presence survives snapshot epoch reuse after provider restart" {
    var fixture = try Fixture.create();
    defer fixture.destroy();
    try fixture.observeComplete(3, 3, 1, 100, &.{fact(1, 60)});
    try fixture.observeComplete(4, 4, 2, 110, &.{fact(2, 60)});
    var restarted = sourceSnapshot(5, 5, 1, 120, .complete, 1);
    restarted.source_generation += 1;
    try observeRows(fixture.session, restarted, &.{fact(3, 60)});
    const index = fixture.session.findObservation(source_handle, coverage_scope_handle, rule_handle, target_handle).?;
    try std.testing.expect(fixture.session.observations[index].active);

    var operation: u64 = 6;
    var generation = restarted.source_generation + 1;
    for ([_]protocol.SourceStatus{ .unavailable, .skipped, .complete, .complete }) |status| {
        var input = sourceSnapshot(operation, operation, 1, @as(i64, @intCast(operation)) * 10 + 100, status, 0);
        input.source_generation = generation;
        try observeRows(fixture.session, input, &.{});
        const expected_misses: u32 = if (operation < 8) 0 else @intCast(operation - 7);
        try std.testing.expectEqual(expected_misses, fixture.session.observations[index].consecutive_misses);
        try std.testing.expectEqual(expected_misses < 2, fixture.session.observations[index].active);
        operation += 1;
        generation += 1;
    }
    var reappeared = sourceSnapshot(10, 10, 1, 210, .complete, 1);
    reappeared.source_generation = generation;
    try observeRows(fixture.session, reappeared, &.{fact(4, 60)});
    try std.testing.expectEqual(@as(u32, 0), fixture.session.observations[index].consecutive_misses);
    try std.testing.expect(!fixture.session.observations[index].active);
    reappeared.operation_epoch += 1;
    reappeared.command_monotonic_milliseconds += 1;
    reappeared.source_snapshot_epoch += 1;
    try observeRows(fixture.session, reappeared, &.{fact(5, 60)});
    try std.testing.expect(fixture.session.observations[index].active);
}

test "source presence rolls back before the same failed operation is retried" {
    var fixture = try Fixture.create();
    defer fixture.destroy();
    const targets = [_]u64{ 41, 42, 43, 44 };
    var facts: [4]protocol.FactInput = undefined;
    for (targets, 0..) |target, index| facts[index] = factForTarget(1, target, 60);
    try fixture.observeComplete(3, 3, 1, 100, &facts);
    for (&facts) |*row| row.fact_sequence = 2;
    try fixture.observeComplete(4, 4, 2, 110, &facts);
    const index = fixture.session.findObservation(source_handle, coverage_scope_handle, rule_handle, targets[0]).?;
    const before = fixture.session.observations[index];
    const source_index = fixture.session.findSource(source_handle, coverage_scope_handle).?;
    const source_before = fixture.session.sources[source_index];
    var input = sourceSnapshot(5, 5, 1, 120, .complete, 2);
    input.source_generation += 1;
    const overflow = [_]protocol.FactInput{
        factForTarget(3, targets[0], 80),
        factForTarget(1, 45, 80),
    };
    try expectCode(.buffer_too_small, session_module.observe(fixture.session, &input, &overflow, overflow.len));
    try std.testing.expectEqualDeep(before, fixture.session.observations[index]);
    try std.testing.expectEqualDeep(source_before, fixture.session.sources[source_index]);
    try std.testing.expectEqual(@as(u64, 4), fixture.session.last_operation_epoch);

    input.fact_count = 0;
    try observeRows(fixture.session, input, &.{});
    for (fixture.session.observations) |observation| {
        try std.testing.expect(observation.occupied);
        try std.testing.expectEqual(@as(u32, 1), observation.consecutive_misses);
        try std.testing.expectEqual(@as(u64, 2), observation.sample_count);
        try std.testing.expect(observation.active);
    }
}

test "trust suppresses reports only after exact persisted feedback" {
    var fixture = try Fixture.create();
    defer fixture.destroy();

    try fixture.observeComplete(3, 3, 1, 100, &.{fact(1, 60)});
    try fixture.observeComplete(4, 4, 2, 110, &.{fact(2, 70)});
    const report_index = fixture.session.findReport(target_handle, family_handle).?;
    try std.testing.expect(!fixture.session.reports[report_index].trusted_suppressed);

    var command = trustCommand(5, 5, .add);
    try expectCode(.ok, session_module.commandTrust(fixture.session, &command));
    try std.testing.expect(!fixture.session.reports[report_index].trusted_suppressed);

    var reports: [4]protocol.ReportOutput = undefined;
    var operations: [32]protocol.PersistenceOperation = undefined;
    var output: protocol.PlanOutput = undefined;
    var plan_input = planInput(6, 6, 1, 120, reports.len, operations.len);
    try expectCode(.ok, session_module.plan(
        fixture.session,
        &plan_input,
        &reports,
        reports.len,
        &operations,
        operations.len,
        &output,
    ));

    var trust_operation: ?protocol.PersistenceOperation = null;
    for (operations[0..output.persistence_operation_count]) |operation| {
        if (operation.operation_kind == @intFromEnum(protocol.PersistenceKind.trust)) {
            trust_operation = operation;
            break;
        }
    }
    _ = trust_operation orelse return error.MissingTrustOperation;
    try settlePlan(
        fixture.session,
        operations[0..output.persistence_operation_count],
        7,
        7,
        1,
        130,
    );
    try std.testing.expect(fixture.session.reports[report_index].trusted_suppressed);
    const trust_index = fixture.session.findTrust(target_handle, family_handle).?;
    try std.testing.expect(fixture.session.trusts[trust_index].committed);
}

test "trust feedback fails atomically when suppression mutation space is exhausted" {
    var fixture = try Fixture.create();
    defer fixture.destroy();
    try fixture.observeComplete(3, 3, 1, 100, &.{fact(1, 60)});
    try fixture.observeComplete(4, 4, 2, 110, &.{fact(2, 70)});
    const report_index = fixture.session.findReport(target_handle, family_handle).?;

    var command = trustCommand(5, 5, .add);
    try expectCode(.ok, session_module.commandTrust(fixture.session, &command));
    var reports: [4]protocol.ReportOutput = undefined;
    var operations: [32]protocol.PersistenceOperation = undefined;
    var output: protocol.PlanOutput = undefined;
    var plan_input = planInput(6, 6, 1, 120, reports.len, operations.len);
    try expectCode(.ok, session_module.plan(
        fixture.session,
        &plan_input,
        &reports,
        reports.len,
        &operations,
        operations.len,
        &output,
    ));
    var trust_operation: ?protocol.PersistenceOperation = null;
    for (operations[0..output.persistence_operation_count]) |operation| {
        if (operation.operation_kind == @intFromEnum(protocol.PersistenceKind.trust)) {
            trust_operation = operation;
            break;
        }
    }
    _ = trust_operation orelse return error.MissingTrustOperation;
    var feedbacks: [32]protocol.PersistenceFeedback = undefined;
    for (
        operations[0..output.persistence_operation_count],
        feedbacks[0..output.persistence_operation_count],
    ) |operation, *feedback| {
        feedback.* = feedbackFor(operation, .persisted);
    }
    var feedback_input = persistenceFeedbackBatchInput(
        7,
        7,
        1,
        130,
        output.persistence_operation_count,
    );
    fixture.session.next_mutation_version = std.math.maxInt(u64);
    try expectCode(.out_of_memory, session_module.applyFeedback(
        fixture.session,
        &feedback_input,
        feedbacks[0..output.persistence_operation_count].ptr,
        output.persistence_operation_count,
    ));
    const trust_index = fixture.session.findTrust(target_handle, family_handle).?;
    try std.testing.expect(!fixture.session.trusts[trust_index].committed);
    try std.testing.expectEqual(
        @as(u64, 0),
        fixture.session.trusts[trust_index].persisted_mutation_version,
    );
    try std.testing.expect(!fixture.session.reports[report_index].trusted_suppressed);
}

test "same source generation and snapshot epoch cannot replay" {
    var fixture = try Fixture.create();
    defer fixture.destroy();
    try fixture.observeComplete(3, 3, 1, 100, &.{fact(1, 60)});
    var input = sourceSnapshot(4, 4, 1, 110, .complete, 0);
    try expectCode(.stale_frame, session_module.observe(fixture.session, &input, null, 0));
}

test "source generation and fact sequence cannot roll back while UTC remains logical" {
    var fixture = try Fixture.create();
    defer fixture.destroy();
    try fixture.observeComplete(3, 3, 1, 100, &.{fact(1, 60)});

    var lower_generation = sourceSnapshot(4, 4, 2, 110, .complete, 1);
    lower_generation.source_generation = source_generation - 1;
    var repeated_fact = fact(2, 60);
    try expectCode(.stale_frame, session_module.observe(
        fixture.session,
        &lower_generation,
        @ptrCast(&repeated_fact),
        1,
    ));

    var newer_generation = sourceSnapshot(4, 4, 1, 90, .complete, 1);
    newer_generation.source_generation = source_generation + 1;
    repeated_fact.fact_sequence = 1;
    try expectCode(.stale_frame, session_module.observe(
        fixture.session,
        &newer_generation,
        @ptrCast(&repeated_fact),
        1,
    ));

    repeated_fact.fact_sequence = 2;
    try expectCode(.ok, session_module.observe(
        fixture.session,
        &newer_generation,
        @ptrCast(&repeated_fact),
        1,
    ));
    const observation_index = fixture.session.findObservation(
        source_handle,
        coverage_scope_handle,
        rule_handle,
        target_handle,
    ).?;
    try std.testing.expectEqual(
        @as(i64, 100),
        fixture.session.observations[observation_index].last_observed_at_utc_ms,
    );
    const source_index = fixture.session.findSource(
        source_handle,
        coverage_scope_handle,
    ).?;
    try std.testing.expectEqual(
        source_generation + 1,
        fixture.session.sources[source_index].source_generation,
    );
}

test "persisted source watermark rejects replay after session recreation" {
    var config = testConfig();
    const session = try state.Session.create(&config);
    defer session.destroy();
    var rule_row = rule();
    var replace = ruleReplaceInput(1, 1, 100, 1);
    try expectCode(.ok, session_module.replaceRules(
        session,
        &replace,
        @ptrCast(&rule_row),
        1,
    ));
    const rows = [_]protocol.PersistenceOperation{
        persistedMetadataRow(2, 110),
        persistedSourceRow(),
    };
    var import_input = importInput(2, 2, 110, 1, rows.len);
    try expectCode(.ok, session_module.importPersisted(
        session,
        &import_input,
        &rows,
        rows.len,
    ));

    var replay = sourceSnapshot(3, 3, 2, 120, .complete, 0);
    try expectCode(.stale_frame, session_module.observe(
        session,
        &replay,
        null,
        0,
    ));
    var next = sourceSnapshot(3, 3, 3, 120, .complete, 0);
    try expectCode(.ok, session_module.observe(
        session,
        &next,
        null,
        0,
    ));
}

test "complete coverage only resets observations in the same scope" {
    var fixture = try Fixture.create();
    defer fixture.destroy();
    try fixture.observeComplete(3, 3, 1, 100, &.{fact(1, 60)});
    const observation_index = fixture.session.findObservation(
        source_handle,
        coverage_scope_handle,
        rule_handle,
        target_handle,
    ).?;

    var other_scope = sourceSnapshot(4, 4, 1, 110, .complete, 0);
    other_scope.coverage_scope_handle = coverage_scope_handle + 1;
    try expectCode(.ok, session_module.observe(fixture.session, &other_scope, null, 0));
    try std.testing.expectEqual(
        @as(u64, 1),
        fixture.session.observations[observation_index].active_sample_count,
    );
    try std.testing.expectEqual(
        @as(u32, 0),
        fixture.session.observations[observation_index].consecutive_misses,
    );
}

test "clock rollback is surfaced without moving logical UTC backward" {
    var fixture = try Fixture.create();
    defer fixture.destroy();
    var reports: [4]protocol.ReportOutput = undefined;
    var operations: [32]protocol.PersistenceOperation = undefined;
    var output: protocol.PlanOutput = undefined;

    var first = planInput(3, 3, 1, 200, reports.len, operations.len);
    try expectCode(.ok, session_module.plan(
        fixture.session,
        &first,
        &reports,
        reports.len,
        &operations,
        operations.len,
        &output,
    ));
    try std.testing.expectEqual(@as(i64, 200), output.logical_utc_milliseconds);
    try settlePlan(
        fixture.session,
        operations[0..output.persistence_operation_count],
        4,
        4,
        1,
        200,
    );

    var rollback = planInput(5, 5, 2, 150, reports.len, operations.len);
    try expectCode(.ok, session_module.plan(
        fixture.session,
        &rollback,
        &reports,
        reports.len,
        &operations,
        operations.len,
        &output,
    ));
    try std.testing.expectEqual(@as(i64, 200), output.logical_utc_milliseconds);
    try std.testing.expect(
        (output.flags & protocol.PlanFlags.clock_rollback_observed) != 0,
    );
}

test "stable plans persist metadata only at the explicit checkpoint interval" {
    var fixture = try Fixture.create();
    defer fixture.destroy();
    var reports: [4]protocol.ReportOutput = undefined;
    var operations: [32]protocol.PersistenceOperation = undefined;
    var output: protocol.PlanOutput = undefined;

    var initial = planInput(3, 3, 1, 100, reports.len, operations.len);
    try expectCode(.ok, session_module.plan(
        fixture.session,
        &initial,
        &reports,
        reports.len,
        &operations,
        operations.len,
        &output,
    ));
    try std.testing.expectEqual(@as(u32, 1), output.persistence_operation_count);
    try std.testing.expectEqual(
        protocol.PersistenceKind.metadata,
        @as(protocol.PersistenceKind, @enumFromInt(operations[0].operation_kind)),
    );
    try settlePlan(
        fixture.session,
        operations[0..output.persistence_operation_count],
        4,
        4,
        1,
        100,
    );

    var before_checkpoint = planInput(5, 5, 2, 199, reports.len, operations.len);
    try expectCode(.ok, session_module.plan(
        fixture.session,
        &before_checkpoint,
        &reports,
        reports.len,
        &operations,
        operations.len,
        &output,
    ));
    try std.testing.expectEqual(@as(u32, 0), output.persistence_operation_count);

    var at_checkpoint = planInput(6, 6, 3, 200, reports.len, operations.len);
    try expectCode(.ok, session_module.plan(
        fixture.session,
        &at_checkpoint,
        &reports,
        reports.len,
        &operations,
        operations.len,
        &output,
    ));
    try std.testing.expectEqual(@as(u32, 1), output.persistence_operation_count);
    try std.testing.expectEqual(
        protocol.PersistenceKind.metadata,
        @as(protocol.PersistenceKind, @enumFromInt(operations[0].operation_kind)),
    );
}

test "persisted logical UTC survives recreation and a lower wall clock" {
    var config = testConfig();
    const first_session = try createReadySession(&config, &.{rule()});
    defer first_session.destroy();

    var reports: [4]protocol.ReportOutput = undefined;
    var operations: [32]protocol.PersistenceOperation = undefined;
    var output: protocol.PlanOutput = undefined;
    var first_plan = planInput(3, 3, 1, 500, reports.len, operations.len);
    try expectCode(.ok, session_module.plan(
        first_session,
        &first_plan,
        &reports,
        reports.len,
        &operations,
        operations.len,
        &output,
    ));
    const metadata = try findOperation(
        operations[0..output.persistence_operation_count],
        .metadata,
    );
    try settlePlan(
        first_session,
        operations[0..output.persistence_operation_count],
        4,
        4,
        1,
        500,
    );

    const recreated = try state.Session.create(&config);
    defer recreated.destroy();
    var rule_row = rule();
    var replace = ruleReplaceInput(1, 1, 100, 1);
    try expectCode(.ok, session_module.replaceRules(
        recreated,
        &replace,
        @ptrCast(&rule_row),
        1,
    ));
    const persisted = [_]protocol.PersistenceOperation{metadata};
    var import_input = importInput(2, 2, 100, 1, persisted.len);
    try expectCode(.ok, session_module.importPersisted(
        recreated,
        &import_input,
        &persisted,
        persisted.len,
    ));
    try std.testing.expectEqual(@as(i64, 500), recreated.logical_utc_ms);

    var rollback_plan = planInput(3, 3, 1, 150, reports.len, operations.len);
    try expectCode(.ok, session_module.plan(
        recreated,
        &rollback_plan,
        &reports,
        reports.len,
        &operations,
        operations.len,
        &output,
    ));
    try std.testing.expectEqual(@as(i64, 500), output.logical_utc_milliseconds);
    try std.testing.expect(
        (output.flags & protocol.PlanFlags.clock_rollback_observed) != 0,
    );
}

test "persistence feedback batch validates completely before mutation" {
    var fixture = try Fixture.create();
    defer fixture.destroy();
    try fixture.observeComplete(3, 3, 1, 100, &.{fact(1, 60)});
    try fixture.observeComplete(4, 4, 2, 110, &.{fact(2, 70)});

    var reports: [4]protocol.ReportOutput = undefined;
    var operations: [32]protocol.PersistenceOperation = undefined;
    var output: protocol.PlanOutput = undefined;
    var plan_input = planInput(5, 5, 1, 120, reports.len, operations.len);
    try expectCode(.ok, session_module.plan(
        fixture.session,
        &plan_input,
        &reports,
        reports.len,
        &operations,
        operations.len,
        &output,
    ));
    try std.testing.expect(output.persistence_operation_count >= 2);

    var feedbacks: [32]protocol.PersistenceFeedback = undefined;
    for (
        operations[0..output.persistence_operation_count],
        feedbacks[0..output.persistence_operation_count],
    ) |operation, *feedback| {
        feedback.* = feedbackFor(operation, .persisted);
    }
    feedbacks[1].slot_generation += 1;
    const first_kind: protocol.PersistenceKind = @enumFromInt(feedbacks[0].operation_kind);
    const first_persisted_before = persistedMutation(
        fixture.session,
        first_kind,
        feedbacks[0].slot_index,
    );
    var invalid_input = persistenceFeedbackInput(6, 6, 1, 130);
    invalid_input.feedback_count = output.persistence_operation_count;
    try expectCode(.invalid_argument, session_module.applyFeedback(
        fixture.session,
        &invalid_input,
        feedbacks[0..output.persistence_operation_count].ptr,
        output.persistence_operation_count,
    ));
    try std.testing.expectEqual(
        first_persisted_before,
        persistedMutation(fixture.session, first_kind, feedbacks[0].slot_index),
    );

    feedbacks[1] = feedbackFor(operations[1], .persisted);
    var incomplete_input = persistenceFeedbackBatchInput(
        6,
        6,
        1,
        130,
        output.persistence_operation_count - 1,
    );
    try expectCode(.invalid_argument, session_module.applyFeedback(
        fixture.session,
        &incomplete_input,
        feedbacks[0 .. output.persistence_operation_count - 1].ptr,
        output.persistence_operation_count - 1,
    ));

    feedbacks[1].status = @intFromEnum(protocol.FeedbackStatus.failed);
    try expectCode(.invalid_argument, session_module.applyFeedback(
        fixture.session,
        &invalid_input,
        feedbacks[0..output.persistence_operation_count].ptr,
        output.persistence_operation_count,
    ));
    feedbacks[1] = feedbackFor(operations[1], .persisted);
    try expectCode(.ok, session_module.applyFeedback(
        fixture.session,
        &invalid_input,
        feedbacks[0..output.persistence_operation_count].ptr,
        output.persistence_operation_count,
    ));
    try std.testing.expectEqual(
        feedbacks[0].mutation_version,
        persistedMutation(fixture.session, first_kind, feedbacks[0].slot_index),
    );
}

test "persistence plan refuses to truncate an atomic dirty batch" {
    var fixture = try Fixture.create();
    defer fixture.destroy();
    try fixture.observeComplete(3, 3, 1, 100, &.{fact(1, 60)});

    var reports: [4]protocol.ReportOutput = undefined;
    var one_operation: [1]protocol.PersistenceOperation = undefined;
    var output: protocol.PlanOutput = undefined;
    var too_small = planInput(4, 4, 1, 110, reports.len, one_operation.len);
    try expectCode(.buffer_too_small, session_module.plan(
        fixture.session,
        &too_small,
        &reports,
        reports.len,
        &one_operation,
        one_operation.len,
        &output,
    ));
    try std.testing.expectEqual(@as(u32, 0), fixture.session.planned_persistence_count);
    try std.testing.expectEqual(@as(u64, 0), fixture.session.last_plan_epoch);

    var operations: [32]protocol.PersistenceOperation = undefined;
    var retry = planInput(4, 4, 1, 110, reports.len, operations.len);
    try expectCode(.ok, session_module.plan(
        fixture.session,
        &retry,
        &reports,
        reports.len,
        &operations,
        operations.len,
        &output,
    ));
    try std.testing.expect(output.persistence_operation_count > 1);
}

test "persistence feedback can settle only an operation emitted by the latest plan" {
    var fixture = try Fixture.create();
    defer fixture.destroy();
    try fixture.observeComplete(3, 3, 1, 100, &.{fact(1, 60)});
    try fixture.observeComplete(4, 4, 2, 110, &.{fact(2, 70)});

    var reports: [4]protocol.ReportOutput = undefined;
    var operations: [32]protocol.PersistenceOperation = undefined;
    var output: protocol.PlanOutput = undefined;
    var plan_input = planInput(5, 5, 1, 120, reports.len, operations.len);
    try expectCode(.ok, session_module.plan(
        fixture.session,
        &plan_input,
        &reports,
        reports.len,
        &operations,
        operations.len,
        &output,
    ));
    try std.testing.expect(output.persistence_operation_count > 1);

    var feedbacks: [32]protocol.PersistenceFeedback = undefined;
    for (
        operations[0..output.persistence_operation_count],
        feedbacks[0..output.persistence_operation_count],
    ) |operation, *feedback| {
        feedback.* = feedbackFor(operation, .persisted);
    }
    feedbacks[0].mutation_version += 1;
    var feedback_input = persistenceFeedbackBatchInput(
        6,
        6,
        1,
        130,
        output.persistence_operation_count,
    );
    try expectCode(.invalid_argument, session_module.applyFeedback(
        fixture.session,
        &feedback_input,
        feedbacks[0..output.persistence_operation_count].ptr,
        output.persistence_operation_count,
    ));

    feedbacks[0] = feedbackFor(operations[0], .persisted);
    try expectCode(.ok, session_module.applyFeedback(
        fixture.session,
        &feedback_input,
        feedbacks[0..output.persistence_operation_count].ptr,
        output.persistence_operation_count,
    ));
}

test "an in-flight persistence plan replays exactly and rejects replacement" {
    var fixture = try Fixture.create();
    defer fixture.destroy();
    try fixture.observeComplete(3, 3, 1, 100, &.{fact(1, 60)});

    var reports: [4]protocol.ReportOutput = undefined;
    var operations: [32]protocol.PersistenceOperation = undefined;
    var output: protocol.PlanOutput = undefined;
    var first = planInput(4, 4, 1, 110, reports.len, operations.len);
    try expectCode(.ok, session_module.plan(
        fixture.session,
        &first,
        &reports,
        reports.len,
        &operations,
        operations.len,
        &output,
    ));
    try std.testing.expect(output.persistence_operation_count > 1);

    var replay_reports: [4]protocol.ReportOutput = undefined;
    var replay_operations: [32]protocol.PersistenceOperation = undefined;
    var replay_output: protocol.PlanOutput = undefined;
    try expectCode(.ok, session_module.plan(
        fixture.session,
        &first,
        &replay_reports,
        replay_reports.len,
        &replay_operations,
        replay_operations.len,
        &replay_output,
    ));
    try std.testing.expect(std.meta.eql(output, replay_output));
    try std.testing.expect(std.mem.eql(
        u8,
        std.mem.sliceAsBytes(operations[0..output.persistence_operation_count]),
        std.mem.sliceAsBytes(
            replay_operations[0..replay_output.persistence_operation_count],
        ),
    ));

    var blocked = planInput(5, 5, 2, 120, reports.len, operations.len);
    try expectCode(.stale_frame, session_module.plan(
        fixture.session,
        &blocked,
        &reports,
        reports.len,
        &operations,
        operations.len,
        &output,
    ));
    var blocked_observation = sourceSnapshot(5, 5, 2, 120, .complete, 0);
    try expectCode(.unavailable, session_module.observe(
        fixture.session,
        &blocked_observation,
        null,
        0,
    ));
    var blocked_reconfigure = fixture.session.config;
    blocked_reconfigure.generation += 1;
    try expectCode(.unavailable, fixture.session.reconfigure(&blocked_reconfigure));

    try settlePlan(
        fixture.session,
        operations[0..fixture.session.planned_persistence_count],
        5,
        5,
        1,
        120,
    );
    var next = planInput(6, 6, 2, 130, reports.len, operations.len);
    try expectCode(.ok, session_module.plan(
        fixture.session,
        &next,
        &reports,
        reports.len,
        &operations,
        operations.len,
        &output,
    ));
}

test "released observation slot reuses storage with a new generation" {
    var fixture = try Fixture.create();
    defer fixture.destroy();
    try fixture.observeComplete(3, 3, 1, 100, &.{fact(1, 60)});
    const old_index = fixture.session.findObservation(
        source_handle,
        coverage_scope_handle,
        rule_handle,
        target_handle,
    ).?;
    const old_generation = fixture.session.observations[old_index].slot_generation;

    var reports: [4]protocol.ReportOutput = undefined;
    var operations: [32]protocol.PersistenceOperation = undefined;
    var output: protocol.PlanOutput = undefined;
    var expiry_plan = planInput(4, 4, 1, 20_101, reports.len, operations.len);
    try expectCode(.ok, session_module.plan(
        fixture.session,
        &expiry_plan,
        &reports,
        reports.len,
        &operations,
        operations.len,
        &output,
    ));
    var delete_operation: ?protocol.PersistenceOperation = null;
    for (operations[0..output.persistence_operation_count]) |operation| {
        if (operation.operation_kind == @intFromEnum(protocol.PersistenceKind.observation) and
            (operation.flags & protocol.PersistenceFlags.delete) != 0)
        {
            delete_operation = operation;
            break;
        }
    }
    _ = delete_operation orelse return error.MissingObservationDelete;
    try settlePlan(
        fixture.session,
        operations[0..output.persistence_operation_count],
        5,
        5,
        1,
        20_102,
    );
    try std.testing.expect(fixture.session.findObservation(
        source_handle,
        coverage_scope_handle,
        rule_handle,
        target_handle,
    ) == null);

    try fixture.observeComplete(6, 6, 2, 20_103, &.{fact(2, 60)});
    const new_index = fixture.session.findObservation(
        source_handle,
        coverage_scope_handle,
        rule_handle,
        target_handle,
    ).?;
    try std.testing.expectEqual(old_index, new_index);
    try std.testing.expectEqual(
        old_generation + 1,
        fixture.session.observations[new_index].slot_generation,
    );
}

test "bucket aligned windows retain only buckets overlapping the configured window" {
    var config = testConfig();
    var rolling_rule = rule();
    rolling_rule.flags = protocol.RuleFlags.rolling;
    rolling_rule.metric_selector = @intFromEnum(protocol.MetricSelector.sum_24h);
    rolling_rule.required_consecutive_hits = 1;
    rolling_rule.activation_threshold = 1;
    rolling_rule.clear_threshold = 0.5;
    const session = try createReadySession(&config, &.{rolling_rule});
    defer session.destroy();

    try observeRows(session, sourceSnapshot(3, 3, 1, 1_000, .complete, 1), &.{
        rollingFact(1, 4),
    });
    try observeRows(session, sourceSnapshot(4, 4, 2, 2_000, .complete, 1), &.{
        rollingFact(2, 6),
    });

    var reports: [4]protocol.ReportOutput = undefined;
    var operations: [32]protocol.PersistenceOperation = undefined;
    var output: protocol.PlanOutput = undefined;
    var first_plan = planInput(5, 5, 1, 3_001, reports.len, operations.len);
    try expectCode(.ok, session_module.plan(
        session,
        &first_plan,
        &reports,
        reports.len,
        &operations,
        operations.len,
        &output,
    ));
    try std.testing.expectEqual(@as(u32, 1), output.report_count);
    try std.testing.expectApproxEqAbs(@as(f64, 10), reports[0].window_24h_value, 0.001);
    try settlePlan(
        session,
        operations[0..output.persistence_operation_count],
        6,
        6,
        1,
        3_002,
    );

    var second_plan = planInput(7, 7, 2, 4_001, reports.len, operations.len);
    try expectCode(.ok, session_module.plan(
        session,
        &second_plan,
        &reports,
        reports.len,
        &operations,
        operations.len,
        &output,
    ));
    try std.testing.expectApproxEqAbs(@as(f64, 6), reports[0].window_24h_value, 0.001);
    try std.testing.expectApproxEqAbs(@as(f64, 10), reports[0].window_7d_value, 0.001);
}

test "a 24h-only boundary evaluates once while the 7d aggregate remains" {
    var config = testConfig();
    var rolling_rule = rule();
    rolling_rule.flags = protocol.RuleFlags.rolling;
    rolling_rule.metric_selector = @intFromEnum(protocol.MetricSelector.sum_24h);
    rolling_rule.required_consecutive_hits = 1;
    rolling_rule.required_consecutive_misses = 2;
    rolling_rule.activation_threshold = 8;
    rolling_rule.clear_threshold = 7;
    const session = try createReadySession(&config, &.{rolling_rule});
    defer session.destroy();

    try observeRows(session, sourceSnapshot(3, 3, 1, 1_000, .complete, 1), &.{
        rollingFact(1, 4),
    });
    try observeRows(session, sourceSnapshot(4, 4, 2, 2_000, .complete, 1), &.{
        rollingFact(2, 6),
    });

    var reports: [4]protocol.ReportOutput = undefined;
    var operations: [32]protocol.PersistenceOperation = undefined;
    var output: protocol.PlanOutput = undefined;
    var active_plan = planInput(5, 5, 1, 3_001, reports.len, operations.len);
    try expectCode(.ok, session_module.plan(
        session,
        &active_plan,
        &reports,
        reports.len,
        &operations,
        operations.len,
        &output,
    ));
    try std.testing.expectEqual(@as(u32, 1), output.report_count);
    try settlePlan(
        session,
        operations[0..output.persistence_operation_count],
        6,
        6,
        1,
        3_002,
    );

    var boundary = planInput(7, 7, 2, 4_001, reports.len, operations.len);
    try expectCode(.ok, session_module.plan(
        session,
        &boundary,
        &reports,
        reports.len,
        &operations,
        operations.len,
        &output,
    ));
    const observation_index = session.findObservation(
        source_handle,
        coverage_scope_handle,
        rule_handle,
        target_handle,
    ).?;
    try std.testing.expectEqual(@as(u32, 1), output.report_count);
    try std.testing.expectEqual(
        @as(u32, 1),
        session.observations[observation_index].consecutive_misses,
    );
    const boundary_revision = session.observations[observation_index].window_revision;
    try settlePlan(
        session,
        operations[0..output.persistence_operation_count],
        8,
        8,
        2,
        4_002,
    );

    var repeated = planInput(9, 9, 3, 4_001, reports.len, operations.len);
    try expectCode(.ok, session_module.plan(
        session,
        &repeated,
        &reports,
        reports.len,
        &operations,
        operations.len,
        &output,
    ));
    try std.testing.expectEqual(
        @as(u32, 1),
        session.observations[observation_index].consecutive_misses,
    );
    try std.testing.expectEqual(
        boundary_revision,
        session.observations[observation_index].window_revision,
    );
    try std.testing.expectApproxEqAbs(@as(f64, 6), reports[0].window_24h_value, 0.001);
    try std.testing.expectApproxEqAbs(@as(f64, 10), reports[0].window_7d_value, 0.001);
}

test "simultaneous rolling window changes advance one evaluation revision" {
    var config = testConfig();
    var rolling_rule = rule();
    rolling_rule.flags = protocol.RuleFlags.rolling;
    rolling_rule.metric_selector = @intFromEnum(protocol.MetricSelector.sum_24h);
    rolling_rule.required_consecutive_hits = 1;
    rolling_rule.required_consecutive_misses = 1;
    rolling_rule.activation_threshold = 1;
    rolling_rule.clear_threshold = 0.5;
    const session = try createReadySession(&config, &.{rolling_rule});
    defer session.destroy();

    try observeRows(session, sourceSnapshot(3, 3, 1, 1_000, .complete, 1), &.{
        rollingFact(1, 4),
    });
    var reports: [4]protocol.ReportOutput = undefined;
    var operations: [32]protocol.PersistenceOperation = undefined;
    var output: protocol.PlanOutput = undefined;
    var active_plan = planInput(4, 4, 1, 1_001, reports.len, operations.len);
    try expectCode(.ok, session_module.plan(
        session,
        &active_plan,
        &reports,
        reports.len,
        &operations,
        operations.len,
        &output,
    ));
    const observation_index = session.findObservation(
        source_handle,
        coverage_scope_handle,
        rule_handle,
        target_handle,
    ).?;
    const active_revision = session.observations[observation_index].window_revision;
    try settlePlan(
        session,
        operations[0..output.persistence_operation_count],
        5,
        5,
        1,
        1_002,
    );

    var expired = planInput(6, 6, 2, 6_001, reports.len, operations.len);
    try expectCode(.ok, session_module.plan(
        session,
        &expired,
        &reports,
        reports.len,
        &operations,
        operations.len,
        &output,
    ));
    try std.testing.expectEqual(
        active_revision + 1,
        session.observations[observation_index].window_revision,
    );
    try std.testing.expectEqual(
        session.observations[observation_index].window_revision,
        session.observations[observation_index].evaluated_window_revision,
    );
    try std.testing.expectEqual(@as(u32, 0), output.report_count);
}

test "rolling report deactivates when every contributing bucket expires" {
    var config = testConfig();
    var rolling_rule = rule();
    rolling_rule.flags = protocol.RuleFlags.rolling;
    rolling_rule.metric_selector = @intFromEnum(protocol.MetricSelector.sum_24h);
    rolling_rule.required_consecutive_hits = 1;
    rolling_rule.required_consecutive_misses = 1;
    rolling_rule.activation_threshold = 5;
    rolling_rule.clear_threshold = 0.5;
    const session = try createReadySession(&config, &.{rolling_rule});
    defer session.destroy();

    try observeRows(session, sourceSnapshot(3, 3, 1, 1_000, .complete, 1), &.{
        rollingFact(1, 4),
    });
    try observeRows(session, sourceSnapshot(4, 4, 2, 2_000, .complete, 1), &.{
        rollingFact(2, 2),
    });

    var reports: [4]protocol.ReportOutput = undefined;
    var operations: [32]protocol.PersistenceOperation = undefined;
    var output: protocol.PlanOutput = undefined;
    var first = planInput(5, 5, 1, 3_001, reports.len, operations.len);
    try expectCode(.ok, session_module.plan(
        session,
        &first,
        &reports,
        reports.len,
        &operations,
        operations.len,
        &output,
    ));
    try std.testing.expectEqual(@as(u32, 1), output.report_count);
    try settlePlan(
        session,
        operations[0..output.persistence_operation_count],
        6,
        6,
        1,
        3_002,
    );

    var expired = planInput(7, 7, 2, 7_001, reports.len, operations.len);
    try expectCode(.ok, session_module.plan(
        session,
        &expired,
        &reports,
        reports.len,
        &operations,
        operations.len,
        &output,
    ));
    try std.testing.expectEqual(@as(u32, 0), output.report_count);
    const observation_index = session.findObservation(
        source_handle,
        coverage_scope_handle,
        rule_handle,
        target_handle,
    ).?;
    try std.testing.expect(!session.observations[observation_index].active);
    try std.testing.expectEqual(
        session.observations[observation_index].window_revision,
        session.observations[observation_index].evaluated_window_revision,
    );
}

test "module local C ABI owns one opaque session and rejects output size drift" {
    var config = testConfig();
    var handle: ?*anyopaque = null;
    try std.testing.expectEqual(
        @intFromEnum(ResultCode.ok),
        abi.rm_report_coordinator_create(&config, &handle),
    );
    defer abi.rm_report_coordinator_destroy(handle);
    try std.testing.expect(handle != null);

    var capacity = std.mem.zeroes(protocol.Capacity);
    try std.testing.expectEqual(
        @intFromEnum(ResultCode.abi_mismatch),
        abi.rm_report_coordinator_query_capacity(handle, &capacity, @sizeOf(protocol.Capacity) - 1),
    );
    try std.testing.expectEqual(
        @intFromEnum(ResultCode.ok),
        abi.rm_report_coordinator_query_capacity(handle, &capacity, @sizeOf(protocol.Capacity)),
    );
    try std.testing.expectEqual(config.maximum_observation_count, capacity.observation_capacity);
}

test "higher priority rule replaces report payload in one stable family slot" {
    var config = testConfig();
    const low = rule();
    var high = rule();
    high.rule_handle = rule_handle + 1;
    high.predicate_group_handle = high.rule_handle;
    high.rule_generation = rule_generation + 1;
    high.report_type_handle = 52;
    high.resource_kind_handle = 53;
    high.payload_handle = 54;
    high.priority = low.priority + 10;
    const session = try createReadySession(&config, &.{low});
    defer session.destroy();

    try observeRows(session, sourceSnapshot(3, 3, 1, 100, .complete, 1), &.{
        factFor(low.rule_handle, low.rule_generation, 1, 60),
    });
    try observeRows(session, sourceSnapshot(4, 4, 2, 110, .complete, 1), &.{
        factFor(low.rule_handle, low.rule_generation, 2, 60),
    });
    const report_index = session.findReport(target_handle, family_handle).?;
    const stable_handle = session.reports[report_index].report_handle;
    try std.testing.expectEqual(low.rule_handle, session.reports[report_index].rule_handle);

    const expanded_rules = [_]protocol.RuleInput{ low, high };
    var replace = ruleReplaceInput(5, 5, 115, expanded_rules.len);
    try expectCode(.ok, session_module.replaceRules(
        session,
        &replace,
        &expanded_rules,
        expanded_rules.len,
    ));
    try observeRows(session, sourceSnapshot(6, 6, 3, 120, .complete, 2), &.{
        factFor(low.rule_handle, low.rule_generation, 3, 60),
        factFor(high.rule_handle, high.rule_generation, 1, 60),
    });
    try observeRows(session, sourceSnapshot(7, 7, 4, 130, .complete, 2), &.{
        factFor(low.rule_handle, low.rule_generation, 4, 60),
        factFor(high.rule_handle, high.rule_generation, 2, 60),
    });
    try std.testing.expectEqual(stable_handle, session.reports[report_index].report_handle);
    try std.testing.expectEqual(high.rule_handle, session.reports[report_index].rule_handle);
    try std.testing.expectEqual(high.payload_handle, session.reports[report_index].payload_handle);
}

test "predicate groups require every indexed predicate while groups in one family are OR branches" {
    var config = testConfig();
    var primary = rule();
    primary.required_consecutive_hits = 1;
    primary.required_consecutive_misses = 1;
    primary.predicate_count = 2;
    var guard = primary;
    guard.rule_handle += 1;
    guard.predicate_index = 1;
    guard.activation_threshold = 90;
    guard.clear_threshold = 80;
    const rules = [_]protocol.RuleInput{ primary, guard };
    const session = try createReadySession(&config, &rules);
    defer session.destroy();

    try observeRows(session, sourceSnapshot(3, 3, 1, 100, .complete, 2), &.{
        factFor(primary.rule_handle, primary.rule_generation, 1, 60),
        factFor(guard.rule_handle, guard.rule_generation, 1, 70),
    });
    try std.testing.expect(session.findReport(target_handle, family_handle) == null);

    try observeRows(session, sourceSnapshot(4, 4, 2, 110, .complete, 2), &.{
        factFor(primary.rule_handle, primary.rule_generation, 2, 60),
        factFor(guard.rule_handle, guard.rule_generation, 2, 95),
    });
    const report_index = session.findReport(target_handle, family_handle).?;
    try std.testing.expect(session.reports[report_index].active);
    try std.testing.expectEqual(primary.rule_handle, session.reports[report_index].rule_handle);

    try observeRows(session, sourceSnapshot(5, 5, 3, 120, .complete, 1), &.{
        factFor(primary.rule_handle, primary.rule_generation, 3, 60),
    });
    try std.testing.expect(!session.reports[report_index].active);
}

test "relative predicates compare two raw scalar values inside Zig" {
    var config = testConfig();
    var relative = rule();
    relative.comparison = @intFromEnum(
        protocol.Comparison.left_minus_right_greater_or_equal,
    );
    relative.activation_threshold = -5;
    relative.clear_threshold = -5;
    relative.required_consecutive_hits = 1;
    relative.required_consecutive_misses = 1;
    const session = try createReadySession(&config, &.{relative});
    defer session.destroy();

    try observeRows(session, sourceSnapshot(3, 3, 1, 100, .complete, 1), &.{
        factWithSecondary(1, 95, 100),
    });
    const report_index = session.findReport(target_handle, family_handle).?;
    try std.testing.expect(session.reports[report_index].active);
    try observeRows(session, sourceSnapshot(4, 4, 2, 110, .complete, 1), &.{
        factWithSecondary(2, 94, 100),
    });
    try std.testing.expect(!session.reports[report_index].active);
}

test "trust is family scoped and the explicit wildcard covers every family" {
    var config = testConfig();
    var first = rule();
    first.required_consecutive_hits = 1;
    first.required_consecutive_misses = 1;
    var second = first;
    second.rule_handle += 1;
    second.predicate_group_handle += 1;
    second.family_handle += 1;
    second.report_type_handle += 1;
    second.resource_kind_handle += 1;
    second.payload_handle += 1;
    const rules = [_]protocol.RuleInput{ first, second };
    const session = try createReadySession(&config, &rules);
    defer session.destroy();
    try observeRows(session, sourceSnapshot(3, 3, 1, 100, .complete, 2), &.{
        factFor(first.rule_handle, first.rule_generation, 1, 60),
        factFor(second.rule_handle, second.rule_generation, 1, 60),
    });
    const first_report = session.findReport(target_handle, first.family_handle).?;
    const second_report = session.findReport(target_handle, second.family_handle).?;

    var exact = trustCommand(4, 4, .add);
    exact.family_handle = first.family_handle;
    try expectCode(.ok, session_module.commandTrust(session, &exact));
    try settleCurrentPlan(session, 5, 6, 1, 110);
    try std.testing.expect(session.reports[first_report].trusted_suppressed);
    try std.testing.expect(!session.reports[second_report].trusted_suppressed);

    var wildcard = trustCommand(7, 7, .add);
    wildcard.family_handle = protocol.TrustFamily.all;
    try expectCode(.ok, session_module.commandTrust(session, &wildcard));
    try settleCurrentPlan(session, 8, 9, 2, 120);
    try std.testing.expect(session.reports[first_report].trusted_suppressed);
    try std.testing.expect(session.reports[second_report].trusted_suppressed);
}

test "live rule identity cannot drift under the same handle and generation" {
    var fixture = try Fixture.create();
    defer fixture.destroy();
    try fixture.observeComplete(3, 3, 1, 100, &.{fact(1, 60)});
    var changed = rule();
    changed.activation_threshold += 1;
    var replace = ruleReplaceInput(4, 4, 110, 1);
    try expectCode(.invalid_argument, session_module.replaceRules(
        fixture.session,
        &replace,
        @ptrCast(&changed),
        1,
    ));
    const active_rule_index = fixture.session.findRule(rule_handle).?;
    try std.testing.expectEqual(
        @as(f64, 50),
        fixture.session.rules[active_rule_index].value.activation_threshold,
    );
    try std.testing.expectEqual(@as(u64, 3), fixture.session.last_operation_epoch);
}

test "persisted trust imports before ready and suppresses the first rebuilt report" {
    var config = testConfig();
    const session = try state.Session.create(&config);
    defer session.destroy();
    var rule_row = rule();
    var replace = ruleReplaceInput(1, 1, 100, 1);
    try expectCode(.ok, session_module.replaceRules(
        session,
        &replace,
        @ptrCast(&rule_row),
        1,
    ));
    const rows = [_]protocol.PersistenceOperation{
        persistedMetadataRow(2, 100),
        persistedTrustRow(71, 1, 1),
    };
    var import_input = importInput(2, 2, 100, 1, rows.len);
    try expectCode(.ok, session_module.importPersisted(
        session,
        &import_input,
        &rows,
        rows.len,
    ));
    const trust_index = session.findTrust(target_handle, family_handle).?;
    try std.testing.expect(session.trusts[trust_index].committed);

    try observeRows(session, sourceSnapshot(3, 3, 1, 110, .complete, 1), &.{fact(1, 60)});
    try observeRows(session, sourceSnapshot(4, 4, 2, 120, .complete, 1), &.{fact(2, 60)});
    const report_index = session.findReport(target_handle, family_handle).?;
    try std.testing.expect(session.reports[report_index].trusted_suppressed);
}

test "trust removal remains effective until exact persisted delete feedback" {
    var fixture = try Fixture.create();
    defer fixture.destroy();
    try fixture.observeComplete(3, 3, 1, 100, &.{fact(1, 60)});
    try fixture.observeComplete(4, 4, 2, 110, &.{fact(2, 70)});
    const report_index = fixture.session.findReport(target_handle, family_handle).?;

    var add_command = trustCommand(5, 5, .add);
    try expectCode(.ok, session_module.commandTrust(fixture.session, &add_command));
    var reports: [4]protocol.ReportOutput = undefined;
    var operations: [32]protocol.PersistenceOperation = undefined;
    var output: protocol.PlanOutput = undefined;
    var add_plan = planInput(6, 6, 1, 120, reports.len, operations.len);
    try expectCode(.ok, session_module.plan(
        fixture.session,
        &add_plan,
        &reports,
        reports.len,
        &operations,
        operations.len,
        &output,
    ));
    const add_operation = try findOperation(
        operations[0..output.persistence_operation_count],
        .trust,
    );
    _ = add_operation;
    try settlePlan(
        fixture.session,
        operations[0..output.persistence_operation_count],
        7,
        7,
        1,
        130,
    );
    try std.testing.expect(fixture.session.reports[report_index].trusted_suppressed);

    var remove_command = trustCommand(8, 8, .remove);
    try expectCode(.ok, session_module.commandTrust(fixture.session, &remove_command));
    try std.testing.expect(fixture.session.reports[report_index].trusted_suppressed);

    var remove_plan = planInput(9, 9, 2, 140, reports.len, operations.len);
    try expectCode(.ok, session_module.plan(
        fixture.session,
        &remove_plan,
        &reports,
        reports.len,
        &operations,
        operations.len,
        &output,
    ));
    const remove_operation = try findOperation(
        operations[0..output.persistence_operation_count],
        .trust,
    );
    try std.testing.expect(
        (remove_operation.flags & protocol.PersistenceFlags.delete) != 0,
    );
    try std.testing.expect(fixture.session.reports[report_index].trusted_suppressed);

    try settlePlan(
        fixture.session,
        operations[0..output.persistence_operation_count],
        10,
        10,
        2,
        150,
    );
    try std.testing.expect(!fixture.session.reports[report_index].trusted_suppressed);
    try std.testing.expect(fixture.session.findTrust(target_handle, family_handle) == null);
}

test "invalid import leaves active state untouched and can be retried" {
    var config = testConfig();
    const session = try state.Session.create(&config);
    defer session.destroy();
    var rule_row = rule();
    var replace = ruleReplaceInput(1, 1, 100, 1);
    try expectCode(.ok, session_module.replaceRules(
        session,
        &replace,
        @ptrCast(&rule_row),
        1,
    ));
    const duplicate_rows = [_]protocol.PersistenceOperation{
        persistedTrustRow(71, 1, 1),
        persistedTrustRow(71, 2, 2),
    };
    var rejected_import = importInput(2, 2, 100, 1, duplicate_rows.len);
    try expectCode(.invalid_argument, session_module.importPersisted(
        session,
        &rejected_import,
        &duplicate_rows,
        duplicate_rows.len,
    ));
    try std.testing.expect(!session.import_ready);
    try std.testing.expect(session.findTrust(target_handle, family_handle) == null);

    var empty_import = importInput(2, 2, 100, 1, 0);
    try expectCode(.ok, session_module.importPersisted(session, &empty_import, null, 0));
    try std.testing.expect(session.import_ready);
}

test "import rejects distinct identities that reuse one stable trust key" {
    var config = testConfig();
    const session = try state.Session.create(&config);
    defer session.destroy();
    var rule_row = rule();
    var replace = ruleReplaceInput(1, 1, 100, 1);
    try expectCode(.ok, session_module.replaceRules(
        session,
        &replace,
        @ptrCast(&rule_row),
        1,
    ));
    const duplicate_key_rows = [_]protocol.PersistenceOperation{
        persistedTrustRow(71, 1, 1),
        persistedTrustRow(72, 1, 2),
    };
    var import_input = importInput(2, 2, 100, 1, duplicate_key_rows.len);
    try expectCode(.invalid_argument, session_module.importPersisted(
        session,
        &import_input,
        &duplicate_key_rows,
        duplicate_key_rows.len,
    ));
    try std.testing.expect(!session.import_ready);
}

test "rule replacement rejects duplicate rule handles" {
    var config = testConfig();
    const session = try state.Session.create(&config);
    defer session.destroy();
    const first = rule();
    var second = rule();
    second.rule_generation += 1;
    const rows = [_]protocol.RuleInput{ first, second };
    var replace = ruleReplaceInput(1, 1, 100, rows.len);
    try expectCode(.invalid_argument, session_module.replaceRules(
        session,
        &replace,
        &rows,
        rows.len,
    ));
    try std.testing.expect(!session.rules_ready);
}

test "rolling observation count is enforced independently from slot capacity" {
    var config = testConfig();
    config.maximum_rolling_observation_count = 1;
    var rolling_rule = rule();
    rolling_rule.flags = protocol.RuleFlags.rolling;
    rolling_rule.metric_selector = @intFromEnum(protocol.MetricSelector.sum_24h);
    rolling_rule.required_consecutive_hits = 1;
    rolling_rule.activation_threshold = 1;
    rolling_rule.clear_threshold = 0.5;
    const session = try createReadySession(&config, &.{rolling_rule});
    defer session.destroy();

    var first = rollingFact(1, 4);
    first.target_handle = 41;
    try observeRows(session, sourceSnapshot(3, 3, 1, 1_000, .complete, 1), &.{first});

    var second = rollingFact(2, 4);
    second.target_handle = 42;
    var input = sourceSnapshot(4, 4, 2, 2_000, .complete, 1);
    try expectCode(.buffer_too_small, session_module.observe(
        session,
        &input,
        @ptrCast(&second),
        1,
    ));
    try std.testing.expect(session.findObservation(
        source_handle,
        coverage_scope_handle,
        rule_handle,
        41,
    ) != null);
    try std.testing.expect(session.findObservation(
        source_handle,
        coverage_scope_handle,
        rule_handle,
        42,
    ) == null);
}

test "canonical import preserves report observed time independently from update time" {
    var config = testConfig();
    const session = try state.Session.create(&config);
    defer session.destroy();
    var rule_row = rule();
    var replace = ruleReplaceInput(1, 1, 100, 1);
    try expectCode(.ok, session_module.replaceRules(
        session,
        &replace,
        @ptrCast(&rule_row),
        1,
    ));
    const rows = [_]protocol.PersistenceOperation{
        persistedMetadataRow(4, 150),
        persistedSourceRow(),
        persistedObservationRow(),
        persistedReportRow(),
    };
    var import_input = importInput(2, 2, 150, 1, rows.len);
    try expectCode(.ok, session_module.importPersisted(
        session,
        &import_input,
        &rows,
        rows.len,
    ));
    const report_index = session.findReport(target_handle, family_handle).?;
    try std.testing.expectEqual(
        @as(i64, 110),
        session.reports[report_index].last_observed_at_utc_ms,
    );
    try std.testing.expectEqual(@as(i64, 150), session.reports[report_index].updated_at_utc_ms);
}

test "nonempty import requires one metadata checkpoint and remains atomic on exhaustion" {
    var config = testConfig();
    const session = try state.Session.create(&config);
    defer session.destroy();
    var rule_row = rule();
    var replace = ruleReplaceInput(1, 1, 100, 1);
    try expectCode(.ok, session_module.replaceRules(
        session,
        &replace,
        @ptrCast(&rule_row),
        1,
    ));

    const missing_metadata = [_]protocol.PersistenceOperation{persistedSourceRow()};
    var missing_input = importInput(2, 2, 100, 1, missing_metadata.len);
    try expectCode(.invalid_argument, session_module.importPersisted(
        session,
        &missing_input,
        &missing_metadata,
        missing_metadata.len,
    ));
    try std.testing.expect(!session.import_ready);
    try std.testing.expect(session.findSource(source_handle, coverage_scope_handle) == null);

    const duplicate_metadata = [_]protocol.PersistenceOperation{
        persistedMetadataRow(1, 100),
        persistedMetadataRow(2, 100),
    };
    var duplicate_input = importInput(2, 2, 100, 1, duplicate_metadata.len);
    try expectCode(.invalid_argument, session_module.importPersisted(
        session,
        &duplicate_input,
        &duplicate_metadata,
        duplicate_metadata.len,
    ));
    try std.testing.expect(!session.import_ready);

    const exhausted = [_]protocol.PersistenceOperation{
        persistedMetadataRow(std.math.maxInt(u64), 100),
        persistedSourceRow(),
    };
    var exhausted_input = importInput(2, 2, 200, 1, exhausted.len);
    try expectCode(.out_of_memory, session_module.importPersisted(
        session,
        &exhausted_input,
        &exhausted,
        exhausted.len,
    ));
    try std.testing.expect(!session.import_ready);
    try std.testing.expect(session.findSource(source_handle, coverage_scope_handle) == null);

    var retry = importInput(2, 2, 100, 1, 0);
    try expectCode(.ok, session_module.importPersisted(session, &retry, null, 0));
    try std.testing.expect(session.import_ready);
}

test "import rejects cross-kind field contamination" {
    var config = testConfig();
    const session = try state.Session.create(&config);
    defer session.destroy();
    var rule_row = rule();
    var replace = ruleReplaceInput(1, 1, 100, 1);
    try expectCode(.ok, session_module.replaceRules(
        session,
        &replace,
        @ptrCast(&rule_row),
        1,
    ));
    var contaminated = persistedTrustRow(71, 1, 1);
    contaminated.current_value = 1;
    var import_input = importInput(2, 2, 100, 1, 1);
    try expectCode(.invalid_argument, session_module.importPersisted(
        session,
        &import_input,
        @ptrCast(&contaminated),
        1,
    ));
    try std.testing.expect(!session.import_ready);
}

test "full observation capacity rejects a new target without changing existing reports" {
    var fixture = try Fixture.create();
    defer fixture.destroy();
    const first = [_]protocol.FactInput{
        factForTarget(1, 41, 60),
        factForTarget(2, 42, 60),
        factForTarget(3, 43, 60),
        factForTarget(4, 44, 60),
    };
    const second = [_]protocol.FactInput{
        factForTarget(5, 41, 60),
        factForTarget(6, 42, 60),
        factForTarget(7, 43, 60),
        factForTarget(8, 44, 60),
    };
    try fixture.observeComplete(3, 3, 1, 100, &first);
    try fixture.observeComplete(4, 4, 2, 110, &second);
    try std.testing.expectEqual(@as(usize, 4), occupiedReports(fixture.session));

    var input = sourceSnapshot(5, 5, 3, 120, .complete, 1);
    var overflow_fact = factForTarget(9, 45, 60);
    try expectCode(.buffer_too_small, session_module.observe(
        fixture.session,
        &input,
        @ptrCast(&overflow_fact),
        1,
    ));
    try std.testing.expectEqual(@as(usize, 4), occupiedReports(fixture.session));
    try std.testing.expect(fixture.session.findObservation(
        source_handle,
        coverage_scope_handle,
        rule_handle,
        45,
    ) == null);
    for (fixture.session.observations) |observation| {
        if (!observation.occupied) continue;
        try std.testing.expect(observation.active);
        try std.testing.expectEqual(@as(u32, 0), observation.consecutive_misses);
    }
}

const Fixture = struct {
    config: protocol.Config,
    session: *state.Session,

    fn create() !Fixture {
        var config = testConfig();
        var rule_row = rule();
        const session = try createReadySession(&config, @as(*const [1]protocol.RuleInput, @ptrCast(&rule_row)));
        return .{ .config = config, .session = session };
    }

    fn destroy(self: *Fixture) void {
        self.session.destroy();
    }

    fn observeComplete(
        self: *Fixture,
        operation_epoch: u64,
        monotonic: u64,
        snapshot_epoch: u64,
        utc: i64,
        facts: []const protocol.FactInput,
    ) !void {
        var input = sourceSnapshot(
            operation_epoch,
            monotonic,
            snapshot_epoch,
            utc,
            .complete,
            @intCast(facts.len),
        );
        const pointer: ?[*]const protocol.FactInput = if (facts.len == 0) null else facts.ptr;
        try expectCode(.ok, session_module.observe(
            self.session,
            &input,
            pointer,
            @intCast(facts.len),
        ));
    }

    fn observeUnavailable(
        self: *Fixture,
        operation_epoch: u64,
        monotonic: u64,
        snapshot_epoch: u64,
        utc: i64,
    ) !void {
        var input = sourceSnapshot(
            operation_epoch,
            monotonic,
            snapshot_epoch,
            utc,
            .unavailable,
            0,
        );
        try expectCode(.ok, session_module.observe(self.session, &input, null, 0));
    }
};

fn createReadySession(
    config: *const protocol.Config,
    rule_rows: []const protocol.RuleInput,
) !*state.Session {
    const session = try state.Session.create(config);
    errdefer session.destroy();
    var replace = ruleReplaceInput(1, 1, 100, @intCast(rule_rows.len));
    try expectCode(.ok, session_module.replaceRules(
        session,
        &replace,
        if (rule_rows.len == 0) null else rule_rows.ptr,
        @intCast(rule_rows.len),
    ));
    var import_input = importInput(2, 2, 100, 1, 0);
    try expectCode(.ok, session_module.importPersisted(session, &import_input, null, 0));
    return session;
}

fn observeRows(
    session: *state.Session,
    source_input: protocol.SourceSnapshotInput,
    facts: []const protocol.FactInput,
) !void {
    var input = source_input;
    try expectCode(.ok, session_module.observe(
        session,
        &input,
        if (facts.len == 0) null else facts.ptr,
        @intCast(facts.len),
    ));
}

fn testConfig() protocol.Config {
    var value = std.mem.zeroes(protocol.Config);
    value.abi_version = protocol.abi_version;
    value.struct_size = @sizeOf(protocol.Config);
    value.generation = 1;
    value.session_instance_low = 101;
    value.session_instance_high = 102;
    value.maximum_source_count = 4;
    value.maximum_rule_count = 4;
    value.maximum_observation_count = 4;
    value.maximum_report_count = 4;
    value.maximum_trust_count = 4;
    value.maximum_bucket_count = 20;
    value.maximum_persistence_operation_count = 64;
    value.maximum_report_output_count = 4;
    value.maximum_rolling_observation_count = 4;
    value.source_index_capacity = 8;
    value.rule_index_capacity = 8;
    value.observation_index_capacity = 8;
    value.report_index_capacity = 8;
    value.trust_index_capacity = 8;
    value.bucket_index_capacity = 64;
    value.planned_persistence_index_capacity = 128;
    value.bucket_width_milliseconds = 1_000;
    value.window_24h_milliseconds = 2_000;
    value.window_7d_milliseconds = 4_000;
    value.maximum_future_skew_milliseconds = 1_000;
    value.default_stale_after_milliseconds = 10_000;
    value.default_retention_milliseconds = 20_000;
    value.metadata_checkpoint_interval_milliseconds = 100;
    value.resident_byte_budget = std.math.maxInt(u64);
    return value;
}

fn rule() protocol.RuleInput {
    var value = std.mem.zeroes(protocol.RuleInput);
    value.struct_size = @sizeOf(protocol.RuleInput);
    value.rule_handle = rule_handle;
    value.rule_generation = rule_generation;
    value.source_handle = source_handle;
    value.coverage_scope_handle = coverage_scope_handle;
    value.family_handle = family_handle;
    value.report_type_handle = 32;
    value.resource_kind_handle = 33;
    value.payload_handle = 34;
    value.metric_selector = @intFromEnum(protocol.MetricSelector.current);
    value.comparison = @intFromEnum(protocol.Comparison.greater_or_equal);
    value.priority = 10;
    value.severity = 20;
    value.required_consecutive_hits = 2;
    value.required_consecutive_misses = 2;
    value.minimum_sample_duration_milliseconds = 1;
    value.activation_threshold = 50;
    value.clear_threshold = 40;
    value.predicate_group_handle = rule_handle;
    value.predicate_count = 1;
    return value;
}

fn fact(sequence: u64, current: f64) protocol.FactInput {
    return factFor(rule_handle, rule_generation, sequence, current);
}

fn factWithSecondary(
    sequence: u64,
    current: f64,
    secondary: f64,
) protocol.FactInput {
    var value = fact(sequence, current);
    value.flags |= protocol.FactFlags.secondary_current_valid;
    value.secondary_current_value = secondary;
    return value;
}

fn factFor(
    fact_rule_handle: u64,
    fact_rule_generation: u64,
    sequence: u64,
    current: f64,
) protocol.FactInput {
    var value = std.mem.zeroes(protocol.FactInput);
    value.struct_size = @sizeOf(protocol.FactInput);
    value.flags = protocol.FactFlags.current_valid | protocol.FactFlags.peak_valid;
    value.rule_handle = fact_rule_handle;
    value.rule_generation = fact_rule_generation;
    value.target_handle = target_handle;
    value.evidence_payload_handle = 42;
    value.fact_sequence = sequence;
    value.current_value = current;
    value.peak_value = current;
    value.sample_duration_milliseconds = 1;
    return value;
}

fn factForTarget(sequence: u64, target: u64, current: f64) protocol.FactInput {
    var value = fact(sequence, current);
    value.target_handle = target;
    return value;
}

fn persistedTrustRow(
    identity_handle: u64,
    slot_generation: u64,
    mutation_version: u64,
) protocol.PersistenceOperation {
    var value = std.mem.zeroes(protocol.PersistenceOperation);
    value.struct_size = @sizeOf(protocol.PersistenceOperation);
    value.flags = protocol.PersistenceFlags.active;
    value.mutation_version = mutation_version;
    value.identity_handle = identity_handle;
    value.slot_generation = slot_generation;
    value.target_handle = target_handle;
    value.family_handle = family_handle;
    value.payload_handle = 99;
    value.first_observed_at_utc_milliseconds = 90;
    value.last_observed_at_utc_milliseconds = 90;
    value.operation_kind = @intFromEnum(protocol.PersistenceKind.trust);
    return value;
}

fn persistedMetadataRow(
    mutation_version: u64,
    logical_utc_milliseconds: i64,
) protocol.PersistenceOperation {
    var value = std.mem.zeroes(protocol.PersistenceOperation);
    value.struct_size = @sizeOf(protocol.PersistenceOperation);
    value.mutation_version = mutation_version;
    value.identity_handle = 1;
    value.slot_generation = 1;
    value.operation_kind = @intFromEnum(protocol.PersistenceKind.metadata);
    value.checkpoint_schema_version = protocol.abi_version;
    value.checkpoint_logical_utc_milliseconds = logical_utc_milliseconds;
    return value;
}

fn persistedObservationRow() protocol.PersistenceOperation {
    var value = std.mem.zeroes(protocol.PersistenceOperation);
    value.struct_size = @sizeOf(protocol.PersistenceOperation);
    value.flags = protocol.PersistenceFlags.active | protocol.PersistenceFlags.current_valid;
    value.mutation_version = 2;
    value.identity_handle = 81;
    value.slot_generation = 1;
    value.source_handle = source_handle;
    value.coverage_scope_handle = coverage_scope_handle;
    value.rule_handle = rule_handle;
    value.rule_generation = rule_generation;
    value.target_handle = target_handle;
    value.evidence_payload_handle = 42;
    value.first_observed_at_utc_milliseconds = 100;
    value.last_observed_at_utc_milliseconds = 110;
    value.current_value = 60;
    value.value_sum = 120;
    value.peak_value = 60;
    value.sample_duration_milliseconds = 2;
    value.sample_count = 2;
    value.active_sample_count = 2;
    value.consecutive_hits = 2;
    value.fact_sequence = 2;
    value.operation_kind = @intFromEnum(protocol.PersistenceKind.observation);
    return value;
}

fn persistedReportRow() protocol.PersistenceOperation {
    var value = std.mem.zeroes(protocol.PersistenceOperation);
    value.struct_size = @sizeOf(protocol.PersistenceOperation);
    value.flags = protocol.PersistenceFlags.active;
    value.mutation_version = 3;
    value.identity_handle = 82;
    value.slot_generation = 1;
    value.source_handle = source_handle;
    value.coverage_scope_handle = coverage_scope_handle;
    value.rule_handle = rule_handle;
    value.rule_generation = rule_generation;
    value.target_handle = target_handle;
    value.family_handle = family_handle;
    value.report_handle = value.identity_handle;
    value.report_type_handle = 32;
    value.resource_kind_handle = 33;
    value.payload_handle = 34;
    value.evidence_payload_handle = 42;
    value.first_observed_at_utc_milliseconds = 100;
    value.last_observed_at_utc_milliseconds = 150;
    value.priority = 10;
    value.severity = 20;
    value.operation_kind = @intFromEnum(protocol.PersistenceKind.report);
    value.report_observed_at_utc_milliseconds = 110;
    return value;
}

fn persistedSourceRow() protocol.PersistenceOperation {
    var value = std.mem.zeroes(protocol.PersistenceOperation);
    value.struct_size = @sizeOf(protocol.PersistenceOperation);
    value.mutation_version = 1;
    value.identity_handle = 80;
    value.slot_generation = 1;
    value.source_handle = source_handle;
    value.source_generation = source_generation;
    value.source_snapshot_epoch = 2;
    value.source_last_complete_at_utc_milliseconds = 110;
    value.source_status = @intFromEnum(protocol.SourceStatus.complete);
    value.coverage_scope_handle = coverage_scope_handle;
    value.last_observed_at_utc_milliseconds = 110;
    value.operation_kind = @intFromEnum(protocol.PersistenceKind.source);
    return value;
}

fn rollingFact(sequence: u64, delta: f64) protocol.FactInput {
    var value = std.mem.zeroes(protocol.FactInput);
    value.struct_size = @sizeOf(protocol.FactInput);
    value.flags = protocol.FactFlags.delta_valid | protocol.FactFlags.peak_valid;
    value.rule_handle = rule_handle;
    value.rule_generation = rule_generation;
    value.target_handle = target_handle;
    value.evidence_payload_handle = 42;
    value.fact_sequence = sequence;
    value.delta_value = delta;
    value.peak_value = delta;
    value.sample_duration_milliseconds = 1;
    return value;
}

fn ruleReplaceInput(
    operation_epoch: u64,
    monotonic: u64,
    utc: i64,
    count: u32,
) protocol.RuleReplaceInput {
    var value = std.mem.zeroes(protocol.RuleReplaceInput);
    value.abi_version = protocol.abi_version;
    value.struct_size = @sizeOf(protocol.RuleReplaceInput);
    value.configuration_generation = 1;
    value.operation_epoch = operation_epoch;
    value.command_monotonic_milliseconds = monotonic;
    value.command_utc_milliseconds = utc;
    value.valid_mask = protocol.RuleReplaceValid.required;
    value.rule_count = count;
    return value;
}

fn importInput(
    operation_epoch: u64,
    monotonic: u64,
    utc: i64,
    generation: u64,
    count: u32,
) protocol.ImportInput {
    var value = std.mem.zeroes(protocol.ImportInput);
    value.abi_version = protocol.abi_version;
    value.struct_size = @sizeOf(protocol.ImportInput);
    value.configuration_generation = 1;
    value.import_generation = generation;
    value.operation_epoch = operation_epoch;
    value.command_monotonic_milliseconds = monotonic;
    value.command_utc_milliseconds = utc;
    value.valid_mask = protocol.ImportValid.required;
    value.row_count = count;
    return value;
}

fn sourceSnapshot(
    operation_epoch: u64,
    monotonic: u64,
    snapshot_epoch: u64,
    utc: i64,
    status: protocol.SourceStatus,
    fact_count: u32,
) protocol.SourceSnapshotInput {
    var value = std.mem.zeroes(protocol.SourceSnapshotInput);
    value.abi_version = protocol.abi_version;
    value.struct_size = @sizeOf(protocol.SourceSnapshotInput);
    value.configuration_generation = 1;
    value.operation_epoch = operation_epoch;
    value.command_monotonic_milliseconds = monotonic;
    value.command_utc_milliseconds = utc;
    value.observed_at_utc_milliseconds = utc;
    value.source_handle = source_handle;
    value.source_generation = source_generation;
    value.coverage_scope_handle = coverage_scope_handle;
    value.source_snapshot_epoch = snapshot_epoch;
    value.valid_mask = protocol.SourceSnapshotValid.required;
    value.fact_count = fact_count;
    value.status = @intFromEnum(status);
    return value;
}

fn trustCommand(
    operation_epoch: u64,
    monotonic: u64,
    kind: protocol.TrustCommandKind,
) protocol.TrustCommandInput {
    var value = std.mem.zeroes(protocol.TrustCommandInput);
    value.abi_version = protocol.abi_version;
    value.struct_size = @sizeOf(protocol.TrustCommandInput);
    value.configuration_generation = 1;
    value.operation_epoch = operation_epoch;
    value.command_monotonic_milliseconds = monotonic;
    value.command_utc_milliseconds = 120;
    value.target_handle = target_handle;
    value.family_handle = family_handle;
    value.payload_handle = 99;
    value.valid_mask = protocol.TrustCommandValid.required;
    value.command_kind = @intFromEnum(kind);
    return value;
}

fn planInput(
    operation_epoch: u64,
    monotonic: u64,
    plan_epoch: u64,
    utc: i64,
    report_limit: usize,
    persistence_limit: usize,
) protocol.PlanInput {
    var value = std.mem.zeroes(protocol.PlanInput);
    value.abi_version = protocol.abi_version;
    value.struct_size = @sizeOf(protocol.PlanInput);
    value.configuration_generation = 1;
    value.operation_epoch = operation_epoch;
    value.plan_epoch = plan_epoch;
    value.command_monotonic_milliseconds = monotonic;
    value.command_utc_milliseconds = utc;
    value.valid_mask = protocol.PlanValid.required;
    value.report_limit = @intCast(report_limit);
    value.persistence_limit = @intCast(persistence_limit);
    return value;
}

fn feedbackFor(
    operation: protocol.PersistenceOperation,
    status: protocol.FeedbackStatus,
) protocol.PersistenceFeedback {
    var value = std.mem.zeroes(protocol.PersistenceFeedback);
    value.struct_size = @sizeOf(protocol.PersistenceFeedback);
    value.status = @intFromEnum(status);
    value.session_instance_low = operation.session_instance_low;
    value.session_instance_high = operation.session_instance_high;
    value.plan_epoch = operation.plan_epoch;
    value.mutation_version = operation.mutation_version;
    value.identity_handle = operation.identity_handle;
    value.slot_generation = operation.slot_generation;
    value.operation_kind = operation.operation_kind;
    value.slot_index = operation.slot_index;
    return value;
}

fn persistenceFeedbackInput(
    operation_epoch: u64,
    monotonic: u64,
    feedback_epoch: u64,
    utc: i64,
) protocol.PersistenceFeedbackInput {
    var value = std.mem.zeroes(protocol.PersistenceFeedbackInput);
    value.abi_version = protocol.abi_version;
    value.struct_size = @sizeOf(protocol.PersistenceFeedbackInput);
    value.configuration_generation = 1;
    value.operation_epoch = operation_epoch;
    value.feedback_epoch = feedback_epoch;
    value.command_monotonic_milliseconds = monotonic;
    value.command_utc_milliseconds = utc;
    value.valid_mask = protocol.FeedbackValid.required;
    value.feedback_count = 1;
    return value;
}

fn persistenceFeedbackBatchInput(
    operation_epoch: u64,
    monotonic: u64,
    feedback_epoch: u64,
    utc: i64,
    feedback_count: u32,
) protocol.PersistenceFeedbackInput {
    var value = persistenceFeedbackInput(
        operation_epoch,
        monotonic,
        feedback_epoch,
        utc,
    );
    value.feedback_count = feedback_count;
    return value;
}

fn settleCurrentPlan(
    session: *state.Session,
    plan_operation_epoch: u64,
    feedback_operation_epoch: u64,
    plan_epoch: u64,
    utc: i64,
) !void {
    var reports: [4]protocol.ReportOutput = undefined;
    var operations: [32]protocol.PersistenceOperation = undefined;
    var output: protocol.PlanOutput = undefined;
    var input = planInput(
        plan_operation_epoch,
        plan_operation_epoch,
        plan_epoch,
        utc,
        reports.len,
        operations.len,
    );
    try expectCode(.ok, session_module.plan(
        session,
        &input,
        &reports,
        reports.len,
        &operations,
        operations.len,
        &output,
    ));
    try settlePlan(
        session,
        operations[0..output.persistence_operation_count],
        feedback_operation_epoch,
        feedback_operation_epoch,
        plan_epoch,
        utc + 1,
    );
}

fn settlePlan(
    session: *state.Session,
    operations: []const protocol.PersistenceOperation,
    operation_epoch: u64,
    monotonic: u64,
    feedback_epoch: u64,
    utc: i64,
) !void {
    var feedbacks: [32]protocol.PersistenceFeedback = undefined;
    try std.testing.expect(operations.len > 0);
    try std.testing.expect(operations.len <= feedbacks.len);
    for (operations, feedbacks[0..operations.len]) |operation, *feedback| {
        feedback.* = feedbackFor(operation, .persisted);
    }
    var input = persistenceFeedbackBatchInput(
        operation_epoch,
        monotonic,
        feedback_epoch,
        utc,
        @intCast(operations.len),
    );
    try expectCode(.ok, session_module.applyFeedback(
        session,
        &input,
        feedbacks[0..operations.len].ptr,
        @intCast(operations.len),
    ));
}

fn expectCode(expected: ResultCode, actual: ResultCode) !void {
    try std.testing.expectEqual(expected, actual);
}

fn findOperation(
    operations: []const protocol.PersistenceOperation,
    kind: protocol.PersistenceKind,
) !protocol.PersistenceOperation {
    for (operations) |operation| {
        if (operation.operation_kind == @intFromEnum(kind)) return operation;
    }
    return error.MissingPersistenceOperation;
}

fn persistedMutation(
    session: *const state.Session,
    kind: protocol.PersistenceKind,
    slot_index: u32,
) u64 {
    return switch (kind) {
        .source => session.sources[slot_index].persisted_mutation_version,
        .observation => session.observations[slot_index].persisted_mutation_version,
        .bucket => session.buckets[slot_index].persisted_mutation_version,
        .report => session.reports[slot_index].persisted_mutation_version,
        .trust => session.trusts[slot_index].persisted_mutation_version,
        .metadata => session.metadata_persisted_mutation_version,
    };
}

fn occupiedReports(session: *const state.Session) usize {
    var count: usize = 0;
    for (session.reports) |report| {
        if (report.occupied) count += 1;
    }
    return count;
}

test "persistence row rejects non-finite secondary scalar" {
    var row = std.mem.zeroes(protocol.PersistenceOperation);
    row.struct_size = @sizeOf(protocol.PersistenceOperation);
    row.flags = protocol.PersistenceFlags.secondary_current_valid;
    row.mutation_version = 1;
    row.identity_handle = 1;
    row.operation_kind = @intFromEnum(protocol.PersistenceKind.observation);
    row.secondary_current_value = std.math.nan(f64);

    try std.testing.expect(!protocol.validPersistenceRow(&row));
}
