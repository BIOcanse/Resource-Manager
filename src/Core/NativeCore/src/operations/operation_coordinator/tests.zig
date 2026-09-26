const std = @import("std");
const protocol = @import("protocol.zig");
const state = @import("state.zig");
const ResultCode = @import("../../common/result_codes.zig").ResultCode;

test "configuration is explicit and reconfiguration preserves allocated shape" {
    var invalid = testConfig();
    invalid.generation = 0;
    try std.testing.expectError(
        error.InvalidConfiguration,
        state.Session.create(&invalid),
    );

    invalid = testConfig();
    invalid.resident_byte_budget = 1;
    try std.testing.expectError(
        error.InvalidConfiguration,
        state.Session.create(&invalid),
    );

    var config = testConfig();
    var session = try state.Session.create(&config);
    defer session.destroy();
    const capacity = session.capacity();
    try std.testing.expectEqual(@as(u32, 8), capacity.operation_capacity);
    try std.testing.expect(capacity.resident_byte_count <= config.resident_byte_budget);
    var exact_budget = config;
    exact_budget.resident_byte_budget = capacity.resident_byte_count;
    var exact_session = try state.Session.create(&exact_budget);
    exact_session.destroy();
    exact_budget.resident_byte_budget -= 1;
    try std.testing.expectError(
        error.InvalidConfiguration,
        state.Session.create(&exact_budget),
    );

    try std.testing.expectEqual(ResultCode.stale_frame, session.reconfigure(&config));
    var changed_shape = config;
    changed_shape.generation = 2;
    changed_shape.maximum_operation_count = 4;
    changed_shape.maximum_domain_count = 4;
    changed_shape.maximum_action_count = 4;
    changed_shape.maximum_read_count = 4;
    changed_shape.maximum_start_actions_per_plan = 4;
    changed_shape.maximum_cancel_actions_per_plan = 4;
    changed_shape.maximum_recover_actions_per_plan = 4;
    changed_shape.maximum_persistence_byte_count =
        @sizeOf(protocol.PersistenceHeader) +
        4 * @sizeOf(protocol.PersistenceRecord);
    try std.testing.expectEqual(
        ResultCode.invalid_argument,
        session.reconfigure(&changed_shape),
    );
    var hot = config;
    hot.generation = 2;
    hot.maximum_global_running_count = 1;
    try std.testing.expectEqual(ResultCode.ok, session.reconfigure(&hot));
}

test "inflight action and attempt generations survive hot reconfiguration" {
    var config = testConfig();
    var session = try state.Session.create(&config);
    defer session.destroy();
    var submit_output = std.mem.zeroes(protocol.SubmitOutput);
    var submit = submitInput(1, 0, 1, 100, 100);
    submit.valid_mask = 0;
    submit.domain_id = zeroHandle();
    try std.testing.expectEqual(ResultCode.ok, session.submit(&submit, &submit_output));
    const plan_output = try runPlan(session, 1, 101, 101, 8);

    var hot = config;
    hot.generation = 2;
    hot.maximum_global_running_count = 1;
    try std.testing.expectEqual(ResultCode.ok, session.reconfigure(&hot));

    var actions = zeroActions();
    try readCurrentActions(session, &plan_output, &actions);
    try std.testing.expectEqual(@as(u64, 1), actions[0].configuration_generation);
    var wrong_started = feedbackInput(&actions[0], 1, .started, 102, 102);
    wrong_started.configuration_generation = 2;
    try std.testing.expectEqual(
        ResultCode.stale_frame,
        session.feedback(&wrong_started),
    );
    var started = feedbackInput(&actions[0], 1, .started, 102, 102);
    try std.testing.expectEqual(ResultCode.ok, session.feedback(&started));
    var wrong_progress = progressInput(&actions[0], 1, 103, 103);
    wrong_progress.configuration_generation = 2;
    wrong_progress.valid_mask = protocol.ProgressValid.percent_milli;
    try std.testing.expectEqual(
        ResultCode.stale_frame,
        session.reportProgress(&wrong_progress),
    );
    var progress = progressInput(&actions[0], 1, 103, 103);
    progress.valid_mask = protocol.ProgressValid.percent_milli;
    try std.testing.expectEqual(ResultCode.ok, session.reportProgress(&progress));
    var completed = completionInput(&actions[0], 1, .succeeded, 104, 104);
    try std.testing.expectEqual(ResultCode.ok, session.complete(&completed));
}

test "active domain submission returns the existing operation without duplicating state" {
    var config = testConfig();
    var session = try state.Session.create(&config);
    defer session.destroy();

    var first_output = std.mem.zeroes(protocol.SubmitOutput);
    var first = submitInput(1, 7, 1, 100, 100);
    first.request_handle = handle(500);
    first.valid_mask |= protocol.SubmitValid.request;
    try std.testing.expectEqual(ResultCode.ok, session.submit(&first, &first_output));
    try std.testing.expectEqual(
        protocol.SubmitOutputFlags.inserted,
        first_output.flags,
    );

    var duplicate_output = std.mem.zeroes(protocol.SubmitOutput);
    var duplicate = submitInput(2, 7, 2, 100, 101);
    try std.testing.expectEqual(
        ResultCode.ok,
        session.submit(&duplicate, &duplicate_output),
    );
    try std.testing.expectEqual(
        protocol.SubmitOutputFlags.active_domain_duplicate,
        duplicate_output.flags,
    );
    try std.testing.expect(protocol.equalHandle(first.operation_id, duplicate_output.operation_id));
    try std.testing.expectEqual(first_output.submit_sequence, duplicate_output.submit_sequence);

    var independent_output = std.mem.zeroes(protocol.SubmitOutput);
    var independent = submitInput(2, 0, 3, 101, 102);
    independent.valid_mask = 0;
    independent.domain_id = zeroHandle();
    try std.testing.expectEqual(
        ResultCode.ok,
        session.submit(&independent, &independent_output),
    );
    try std.testing.expectEqual(@as(u64, 2), independent_output.submit_sequence);

    var snapshot = std.mem.zeroes(protocol.SnapshotOutput);
    try std.testing.expectEqual(ResultCode.ok, session.snapshot(&snapshot));
    try std.testing.expectEqual(@as(u32, 2), snapshot.operation_count);
}

test "exact operation identity retry returns retained state and rejects payload drift" {
    var config = testConfig();
    var session = try state.Session.create(&config);
    defer session.destroy();

    var first_output = std.mem.zeroes(protocol.SubmitOutput);
    var first = submitInput(1, 7, 1, 100, 100);
    first.request_handle = handle(500);
    first.valid_mask |= protocol.SubmitValid.request;
    try std.testing.expectEqual(ResultCode.ok, session.submit(&first, &first_output));

    var retry = first;
    retry.submit_epoch = 2;
    retry.created_utc_milliseconds = 101;
    retry.created_monotonic_milliseconds = 101;
    retry.observed_utc_milliseconds = 102;
    retry.observed_monotonic_milliseconds = 102;
    var retry_output = std.mem.zeroes(protocol.SubmitOutput);
    try std.testing.expectEqual(ResultCode.ok, session.submit(&retry, &retry_output));
    try std.testing.expectEqual(
        protocol.SubmitOutputFlags.operation_id_duplicate,
        retry_output.flags,
    );
    try std.testing.expectEqual(first_output.state_revision, retry_output.state_revision);
    try std.testing.expectEqual(first_output.submit_sequence, retry_output.submit_sequence);
    try std.testing.expect(protocol.equalHandle(first.operation_id, retry_output.operation_id));

    var drift = retry;
    drift.submit_epoch = 3;
    drift.request_handle = handle(999);
    drift.observed_utc_milliseconds = 103;
    drift.observed_monotonic_milliseconds = 103;
    var drift_output = std.mem.zeroes(protocol.SubmitOutput);
    try std.testing.expectEqual(
        ResultCode.invalid_argument,
        session.submit(&drift, &drift_output),
    );

    var exact_after_rejection = retry;
    exact_after_rejection.submit_epoch = 3;
    exact_after_rejection.observed_utc_milliseconds = 104;
    exact_after_rejection.observed_monotonic_milliseconds = 104;
    try std.testing.expectEqual(
        ResultCode.ok,
        session.submit(&exact_after_rejection, &retry_output),
    );
    try std.testing.expectEqual(
        protocol.SubmitOutputFlags.operation_id_duplicate,
        retry_output.flags,
    );

    var snapshot = std.mem.zeroes(protocol.SnapshotOutput);
    try std.testing.expectEqual(ResultCode.ok, session.snapshot(&snapshot));
    try std.testing.expectEqual(@as(u32, 1), snapshot.operation_count);
}

test "every persisted nonterminal state resumes through exact recovery" {
    var config = testConfig();
    var source = try state.Session.create(&config);
    defer source.destroy();
    var submit_output = std.mem.zeroes(protocol.SubmitOutput);
    var submit = submitInput(1, 0, 1, 100, 100);
    submit.valid_mask = 0;
    submit.domain_id = zeroHandle();
    try std.testing.expectEqual(ResultCode.ok, source.submit(&submit, &submit_output));
    var header = std.mem.zeroes(protocol.PersistenceHeader);
    var records = zeroOperations();
    var export_input = persistenceInput(1);
    try std.testing.expectEqual(
        ResultCode.ok,
        source.exportPersistence(&export_input, &header, &records),
    );

    var restored = try state.Session.create(&config);
    defer restored.destroy();
    var import_input = persistenceImportInput(1, 101, 101, config.clock_instance_id);
    try std.testing.expectEqual(
        ResultCode.ok,
        restored.importPersistence(&import_input, &header, records[0..1]),
    );
    var snapshot = std.mem.zeroes(protocol.SnapshotOutput);
    try std.testing.expectEqual(ResultCode.ok, restored.snapshot(&snapshot));
    var operations = zeroOperations();
    try std.testing.expectEqual(
        ResultCode.ok,
        restored.readOperations(&readInput(snapshot.state_revision, 0), &operations),
    );
    try std.testing.expectEqual(
        @intFromEnum(protocol.OperationState.recovery_pending),
        operations[0].state,
    );
    try std.testing.expectEqual(
        @intFromEnum(protocol.OperationState.queued),
        operations[0].recovery_origin_state,
    );
    try std.testing.expectEqual(@as(u32, 0), operations[0].attempt_number);

    var recovery_header = std.mem.zeroes(protocol.PersistenceHeader);
    var recovery_records = zeroOperations();
    export_input = persistenceInput(2);
    try std.testing.expectEqual(
        ResultCode.ok,
        restored.exportPersistence(
            &export_input,
            &recovery_header,
            &recovery_records,
        ),
    );
    var roundtrip = try state.Session.create(&config);
    defer roundtrip.destroy();
    import_input = persistenceImportInput(1, 102, 102, config.clock_instance_id);
    try std.testing.expectEqual(
        ResultCode.ok,
        roundtrip.importPersistence(
            &import_input,
            &recovery_header,
            recovery_records[0..1],
        ),
    );
    snapshot = std.mem.zeroes(protocol.SnapshotOutput);
    try std.testing.expectEqual(ResultCode.ok, roundtrip.snapshot(&snapshot));
    operations = zeroOperations();
    try std.testing.expectEqual(
        ResultCode.ok,
        roundtrip.readOperations(
            &readInput(snapshot.state_revision, 0),
            &operations,
        ),
    );
    try std.testing.expectEqual(
        @intFromEnum(protocol.OperationState.recovery_pending),
        operations[0].state,
    );
    try std.testing.expectEqual(
        @intFromEnum(protocol.OperationState.queued),
        operations[0].recovery_origin_state,
    );

    const recovery_plan = try runPlan(roundtrip, 1, 103, 103, 8);
    var actions = zeroActions();
    try readCurrentActions(roundtrip, &recovery_plan, &actions);
    try std.testing.expectEqual(@intFromEnum(protocol.ActionKind.recover), actions[0].kind);
    try std.testing.expect(!actions[0].attempt_token.isZero());
    try std.testing.expectEqual(@as(u32, 0), actions[0].attempt_number);
    var recovered = feedbackInput(&actions[0], 1, .recovered_queued, 104, 104);
    try std.testing.expectEqual(ResultCode.ok, roundtrip.feedback(&recovered));
    const start_plan = try runPlan(roundtrip, 2, 105, 105, 8);
    actions = zeroActions();
    try readCurrentActions(roundtrip, &start_plan, &actions);
    try std.testing.expectEqual(@intFromEnum(protocol.ActionKind.start), actions[0].kind);
    try std.testing.expectEqual(@as(u32, 1), actions[0].attempt_number);
}

test "persisted retry wait recovery remains closed across repeated persistence" {
    var config = testConfig();
    var source = try state.Session.create(&config);
    defer source.destroy();
    var submit_output = std.mem.zeroes(protocol.SubmitOutput);
    var submit = submitInput(1, 0, 1, 100, 100);
    submit.valid_mask = 0;
    submit.domain_id = zeroHandle();
    submit.maximum_attempts = 2;
    try std.testing.expectEqual(ResultCode.ok, source.submit(&submit, &submit_output));
    const start_plan = try runPlan(source, 1, 101, 101, 8);
    var actions = zeroActions();
    try readCurrentActions(source, &start_plan, &actions);
    var retry = feedbackInput(
        &actions[0],
        1,
        .start_retryable_failure,
        102,
        102,
    );
    try std.testing.expectEqual(ResultCode.ok, source.feedback(&retry));

    var header = std.mem.zeroes(protocol.PersistenceHeader);
    var records = zeroOperations();
    var export_input = persistenceInput(1);
    try std.testing.expectEqual(
        ResultCode.ok,
        source.exportPersistence(&export_input, &header, &records),
    );
    try std.testing.expectEqual(
        @intFromEnum(protocol.OperationState.retry_wait),
        records[0].state,
    );

    var restored = try state.Session.create(&config);
    defer restored.destroy();
    var import_input = persistenceImportInput(1, 103, 103, config.clock_instance_id);
    try std.testing.expectEqual(
        ResultCode.ok,
        restored.importPersistence(&import_input, &header, records[0..1]),
    );
    var recovery_header = std.mem.zeroes(protocol.PersistenceHeader);
    var recovery_records = zeroOperations();
    export_input = persistenceInput(2);
    try std.testing.expectEqual(
        ResultCode.ok,
        restored.exportPersistence(
            &export_input,
            &recovery_header,
            &recovery_records,
        ),
    );
    try std.testing.expectEqual(
        @intFromEnum(protocol.OperationState.recovery_pending),
        recovery_records[0].state,
    );
    try std.testing.expectEqual(
        @intFromEnum(protocol.OperationState.retry_wait),
        recovery_records[0].recovery_origin_state,
    );
    try std.testing.expectEqual(
        @as(u64, 0),
        recovery_records[0].retry_at_monotonic_milliseconds,
    );

    var roundtrip = try state.Session.create(&config);
    defer roundtrip.destroy();
    import_input = persistenceImportInput(1, 104, 104, config.clock_instance_id);
    try std.testing.expectEqual(
        ResultCode.ok,
        roundtrip.importPersistence(
            &import_input,
            &recovery_header,
            recovery_records[0..1],
        ),
    );
    var snapshot = std.mem.zeroes(protocol.SnapshotOutput);
    try std.testing.expectEqual(ResultCode.ok, roundtrip.snapshot(&snapshot));
    var operations = zeroOperations();
    try std.testing.expectEqual(
        ResultCode.ok,
        roundtrip.readOperations(
            &readInput(snapshot.state_revision, 0),
            &operations,
        ),
    );
    try std.testing.expectEqual(
        @intFromEnum(protocol.OperationState.recovery_pending),
        operations[0].state,
    );
    try std.testing.expectEqual(
        @intFromEnum(protocol.OperationState.retry_wait),
        operations[0].recovery_origin_state,
    );
}

test "delayed operation creation time is distinct from current submission observation" {
    var config = testConfig();
    var session = try state.Session.create(&config);
    defer session.destroy();

    var output = std.mem.zeroes(protocol.SubmitOutput);
    var first = submitInput(1, 0, 1, 100, 100);
    first.valid_mask = 0;
    first.domain_id = zeroHandle();
    first.observed_utc_milliseconds = 200;
    first.observed_monotonic_milliseconds = 200;
    try std.testing.expectEqual(ResultCode.ok, session.submit(&first, &output));

    var second = submitInput(2, 0, 2, 150, 150);
    second.valid_mask = 0;
    second.domain_id = zeroHandle();
    second.observed_utc_milliseconds = 201;
    second.observed_monotonic_milliseconds = 201;
    output = std.mem.zeroes(protocol.SubmitOutput);
    try std.testing.expectEqual(ResultCode.ok, session.submit(&second, &output));

    var snapshot = std.mem.zeroes(protocol.SnapshotOutput);
    try std.testing.expectEqual(ResultCode.ok, session.snapshot(&snapshot));
    var operations = zeroOperations();
    try std.testing.expectEqual(
        ResultCode.ok,
        session.readOperations(&readInput(snapshot.state_revision, 0), &operations),
    );
    try std.testing.expectEqual(@as(i64, 150), operations[1].created_utc_milliseconds);
    try std.testing.expectEqual(@as(i64, 201), operations[1].updated_utc_milliseconds);
}

test "priority ordering and global running capacity are deterministic" {
    var config = testConfig();
    config.maximum_global_running_count = 1;
    var session = try state.Session.create(&config);
    defer session.destroy();

    var output = std.mem.zeroes(protocol.SubmitOutput);
    var low = submitInput(1, 0, 1, 100, 100);
    low.valid_mask = 0;
    low.domain_id = zeroHandle();
    low.priority = 1;
    try std.testing.expectEqual(ResultCode.ok, session.submit(&low, &output));
    output = std.mem.zeroes(protocol.SubmitOutput);
    var high = submitInput(2, 0, 2, 101, 101);
    high.valid_mask = 0;
    high.domain_id = zeroHandle();
    high.priority = 10;
    try std.testing.expectEqual(ResultCode.ok, session.submit(&high, &output));

    const first_plan = try runPlan(session, 1, 102, 102, 8);
    try std.testing.expectEqual(@as(u32, 1), first_plan.action_count);
    var actions = zeroActions();
    try std.testing.expectEqual(
        ResultCode.ok,
        session.readActions(
            &readInput(first_plan.state_revision, first_plan.plan_epoch),
            &actions,
        ),
    );
    try std.testing.expectEqual(@intFromEnum(protocol.ActionKind.start), actions[0].kind);
    try std.testing.expect(protocol.equalHandle(high.operation_id, actions[0].operation_id));
    try std.testing.expect(!actions[0].attempt_token.isZero());

    var feedback = feedbackInput(
        &actions[0],
        1,
        .started,
        103,
        103,
    );
    try std.testing.expectEqual(ResultCode.ok, session.feedback(&feedback));
    const held_plan = try runPlan(session, 2, 104, 104, 8);
    try std.testing.expectEqual(@as(u32, 0), held_plan.action_count);
    try std.testing.expectEqual(@as(u64, 203), held_plan.next_wake_monotonic_milliseconds);

    var completion = completionInput(
        &actions[0],
        1,
        .succeeded,
        105,
        105,
    );
    try std.testing.expectEqual(ResultCode.ok, session.complete(&completion));
    const second_plan = try runPlan(session, 3, 106, 106, 8);
    try std.testing.expectEqual(@as(u32, 1), second_plan.action_count);
    actions = zeroActions();
    try std.testing.expectEqual(
        ResultCode.ok,
        session.readActions(
            &readInput(second_plan.state_revision, second_plan.plan_epoch),
            &actions,
        ),
    );
    try std.testing.expect(protocol.equalHandle(low.operation_id, actions[0].operation_id));
}

test "action batch must be read completely before another plan can replace it" {
    var config = testConfig();
    var session = try state.Session.create(&config);
    defer session.destroy();
    var output = std.mem.zeroes(protocol.SubmitOutput);
    var first = submitInput(1, 0, 1, 100, 100);
    first.valid_mask = 0;
    first.domain_id = zeroHandle();
    try std.testing.expectEqual(ResultCode.ok, session.submit(&first, &output));
    var second = submitInput(2, 0, 2, 101, 101);
    second.valid_mask = 0;
    second.domain_id = zeroHandle();
    output = std.mem.zeroes(protocol.SubmitOutput);
    try std.testing.expectEqual(ResultCode.ok, session.submit(&second, &output));

    const first_plan = try runPlan(session, 1, 102, 102, 8);
    try std.testing.expectEqual(@as(u32, 2), first_plan.action_count);
    var short = [_]protocol.ActionOutput{std.mem.zeroes(protocol.ActionOutput)};
    short[0].action_id = 999;
    try std.testing.expectEqual(
        ResultCode.buffer_too_small,
        session.readActions(
            &readInput(first_plan.state_revision, first_plan.plan_epoch),
            &short,
        ),
    );
    try std.testing.expectEqual(@as(u64, 999), short[0].action_id);

    var blocked_input = protocol.PlanInput{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.PlanInput),
        .configuration_generation = 1,
        .plan_epoch = 2,
        .observed_utc_milliseconds = 103,
        .observed_monotonic_milliseconds = 103,
        .action_capacity = 8,
        .flags = 0,
        .reserved = [_]u64{0} ** 4,
    };
    var blocked_output = std.mem.zeroes(protocol.PlanOutput);
    try std.testing.expectEqual(
        ResultCode.unavailable,
        session.plan(&blocked_input, &blocked_output),
    );

    var actions = zeroActions();
    try readCurrentActions(session, &first_plan, &actions);
    const second_plan = try runPlan(session, 2, 103, 103, 8);
    try std.testing.expectEqual(@as(u32, 0), second_plan.action_count);
}

test "retryable cancellation rotates fairly across pending operations" {
    var config = testConfig();
    config.maximum_cancel_actions_per_plan = 1;
    var session = try state.Session.create(&config);
    defer session.destroy();
    var output = std.mem.zeroes(protocol.SubmitOutput);
    var first = submitInput(1, 0, 1, 100, 100);
    first.valid_mask = 0;
    first.domain_id = zeroHandle();
    try std.testing.expectEqual(ResultCode.ok, session.submit(&first, &output));
    var second = submitInput(2, 0, 2, 101, 101);
    second.valid_mask = 0;
    second.domain_id = zeroHandle();
    output = std.mem.zeroes(protocol.SubmitOutput);
    try std.testing.expectEqual(ResultCode.ok, session.submit(&second, &output));

    const start_plan = try runPlan(session, 1, 102, 102, 8);
    var actions = zeroActions();
    try readCurrentActions(session, &start_plan, &actions);
    var started = feedbackInput(&actions[0], 1, .started, 103, 103);
    try std.testing.expectEqual(ResultCode.ok, session.feedback(&started));
    started = feedbackInput(&actions[1], 2, .started, 104, 104);
    try std.testing.expectEqual(ResultCode.ok, session.feedback(&started));

    var cancel_output = std.mem.zeroes(protocol.CancelOutput);
    var cancel = cancelInput(first.operation_id, 1, 105, 105);
    try std.testing.expectEqual(ResultCode.ok, session.cancel(&cancel, &cancel_output));
    cancel = cancelInput(second.operation_id, 2, 106, 106);
    cancel_output = std.mem.zeroes(protocol.CancelOutput);
    try std.testing.expectEqual(ResultCode.ok, session.cancel(&cancel, &cancel_output));

    const first_cancel_plan = try runPlan(session, 2, 107, 107, 8);
    actions = zeroActions();
    try readCurrentActions(session, &first_cancel_plan, &actions);
    try std.testing.expect(
        protocol.equalHandle(first.operation_id, actions[0].operation_id),
    );
    var retry = feedbackInput(
        &actions[0],
        3,
        .cancel_retryable_failure,
        108,
        108,
    );
    try std.testing.expectEqual(ResultCode.ok, session.feedback(&retry));

    const second_cancel_plan = try runPlan(session, 3, 109, 109, 8);
    actions = zeroActions();
    try readCurrentActions(session, &second_cancel_plan, &actions);
    try std.testing.expect(
        protocol.equalHandle(second.operation_id, actions[0].operation_id),
    );
}

test "independent delayed events do not share a global timestamp rejection gate" {
    var config = testConfig();
    var session = try state.Session.create(&config);
    defer session.destroy();
    var output = std.mem.zeroes(protocol.SubmitOutput);
    var first = submitInput(1, 0, 1, 100, 100);
    first.valid_mask = 0;
    first.domain_id = zeroHandle();
    try std.testing.expectEqual(ResultCode.ok, session.submit(&first, &output));
    var second = submitInput(2, 0, 2, 101, 101);
    second.valid_mask = 0;
    second.domain_id = zeroHandle();
    output = std.mem.zeroes(protocol.SubmitOutput);
    try std.testing.expectEqual(ResultCode.ok, session.submit(&second, &output));
    const plan_output = try runPlan(session, 1, 102, 102, 8);
    var actions = zeroActions();
    try readCurrentActions(session, &plan_output, &actions);
    var started = feedbackInput(&actions[0], 1, .started, 103, 103);
    try std.testing.expectEqual(ResultCode.ok, session.feedback(&started));
    started = feedbackInput(&actions[1], 2, .started, 104, 104);
    try std.testing.expectEqual(ResultCode.ok, session.feedback(&started));

    var first_progress = progressInput(&actions[0], 1, 200, 200);
    first_progress.valid_mask = protocol.ProgressValid.percent_milli;
    first_progress.percent_milli = 50_000;
    try std.testing.expectEqual(
        ResultCode.ok,
        session.reportProgress(&first_progress),
    );
    var second_progress = progressInput(&actions[1], 1, 150, 150);
    second_progress.valid_mask = protocol.ProgressValid.percent_milli;
    second_progress.percent_milli = 25_000;
    try std.testing.expectEqual(
        ResultCode.ok,
        session.reportProgress(&second_progress),
    );
    second_progress.progress_sequence = 2;
    second_progress.observed_utc_milliseconds = 50;
    second_progress.observed_monotonic_milliseconds = 151;
    second_progress.percent_milli = 30_000;
    try std.testing.expectEqual(
        ResultCode.ok,
        session.reportProgress(&second_progress),
    );

    var snapshot = std.mem.zeroes(protocol.SnapshotOutput);
    try std.testing.expectEqual(ResultCode.ok, session.snapshot(&snapshot));
    var operations = zeroOperations();
    try std.testing.expectEqual(
        ResultCode.ok,
        session.readOperations(&readInput(snapshot.state_revision, 0), &operations),
    );
    try std.testing.expect(operations[0].state_revision != operations[1].state_revision);
    try std.testing.expectEqual(snapshot.state_revision, operations[1].state_revision);
}

test "action identity exhaustion fails before operation state mutation" {
    var config = testConfig();
    var session = try state.Session.create(&config);
    defer session.destroy();
    var output = std.mem.zeroes(protocol.SubmitOutput);
    var submit = submitInput(1, 0, 1, 100, 100);
    submit.valid_mask = 0;
    submit.domain_id = zeroHandle();
    try std.testing.expectEqual(ResultCode.ok, session.submit(&submit, &output));

    session.next_action_id = std.math.maxInt(u64) - 1;
    var input = protocol.PlanInput{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.PlanInput),
        .configuration_generation = 1,
        .plan_epoch = 1,
        .observed_utc_milliseconds = 101,
        .observed_monotonic_milliseconds = 101,
        .action_capacity = 8,
        .flags = 0,
        .reserved = [_]u64{0} ** 4,
    };
    var plan_output = std.mem.zeroes(protocol.PlanOutput);
    try std.testing.expectEqual(ResultCode.out_of_memory, session.plan(&input, &plan_output));

    var snapshot = std.mem.zeroes(protocol.SnapshotOutput);
    try std.testing.expectEqual(ResultCode.ok, session.snapshot(&snapshot));
    try std.testing.expectEqual(@as(u64, 0), snapshot.last_plan_epoch);
    try std.testing.expectEqual(@as(u32, 0), snapshot.action_count);
    var operations = zeroOperations();
    try std.testing.expectEqual(
        ResultCode.ok,
        session.readOperations(&readInput(snapshot.state_revision, 0), &operations),
    );
    try std.testing.expectEqual(
        @intFromEnum(protocol.OperationState.queued),
        operations[0].state,
    );
    try std.testing.expectEqual(@as(u32, 0), operations[0].attempt_number);
}

test "domain and per-plan start capacities are explicit" {
    var domain_config = testConfig();
    domain_config.maximum_domain_count = 1;
    var domain_session = try state.Session.create(&domain_config);
    defer domain_session.destroy();
    var output = std.mem.zeroes(protocol.SubmitOutput);
    var first_domain = submitInput(1, 1, 1, 100, 100);
    try std.testing.expectEqual(
        ResultCode.ok,
        domain_session.submit(&first_domain, &output),
    );
    var second_domain = submitInput(2, 2, 2, 101, 101);
    output = std.mem.zeroes(protocol.SubmitOutput);
    try std.testing.expectEqual(
        ResultCode.out_of_memory,
        domain_session.submit(&second_domain, &output),
    );
    var snapshot = std.mem.zeroes(protocol.SnapshotOutput);
    try std.testing.expectEqual(ResultCode.ok, domain_session.snapshot(&snapshot));
    try std.testing.expectEqual(@as(u32, 1), snapshot.operation_count);

    var plan_config = testConfig();
    plan_config.maximum_start_actions_per_plan = 1;
    var plan_session = try state.Session.create(&plan_config);
    defer plan_session.destroy();
    var first = submitInput(1, 0, 1, 100, 100);
    first.valid_mask = 0;
    first.domain_id = zeroHandle();
    output = std.mem.zeroes(protocol.SubmitOutput);
    try std.testing.expectEqual(ResultCode.ok, plan_session.submit(&first, &output));
    var second = submitInput(2, 0, 2, 101, 101);
    second.valid_mask = 0;
    second.domain_id = zeroHandle();
    output = std.mem.zeroes(protocol.SubmitOutput);
    try std.testing.expectEqual(ResultCode.ok, plan_session.submit(&second, &output));
    const plan_output = try runPlan(plan_session, 1, 102, 102, 8);
    try std.testing.expectEqual(@as(u32, 1), plan_output.action_count);
}

test "progress uses exact attempt and preserves valid zero values" {
    var config = testConfig();
    var session = try state.Session.create(&config);
    defer session.destroy();
    var submit_output = std.mem.zeroes(protocol.SubmitOutput);
    var submit = submitInput(1, 0, 1, 100, 100);
    submit.valid_mask = 0;
    submit.domain_id = zeroHandle();
    try std.testing.expectEqual(ResultCode.ok, session.submit(&submit, &submit_output));
    const plan_output = try runPlan(session, 1, 101, 101, 8);
    var actions = zeroActions();
    try readCurrentActions(session, &plan_output, &actions);
    var feedback = feedbackInput(&actions[0], 1, .started, 102, 102);
    try std.testing.expectEqual(ResultCode.ok, session.feedback(&feedback));

    var progress = progressInput(&actions[0], 1, 103, 103);
    progress.valid_mask = protocol.ProgressValid.percent_milli |
        protocol.ProgressValid.bytes_done |
        protocol.ProgressValid.bytes_total;
    try std.testing.expectEqual(ResultCode.ok, session.reportProgress(&progress));

    progress.progress_sequence = 2;
    progress.observed_utc_milliseconds = 104;
    progress.observed_monotonic_milliseconds = 104;
    progress.percent_milli = 50_000;
    progress.bytes_done = 50;
    progress.bytes_total = 100;
    try std.testing.expectEqual(ResultCode.ok, session.reportProgress(&progress));

    progress.progress_sequence = 3;
    progress.observed_utc_milliseconds = 105;
    progress.observed_monotonic_milliseconds = 105;
    progress.percent_milli = 49_999;
    try std.testing.expectEqual(
        ResultCode.invalid_argument,
        session.reportProgress(&progress),
    );

    var operations = zeroOperations();
    var snapshot = std.mem.zeroes(protocol.SnapshotOutput);
    try std.testing.expectEqual(ResultCode.ok, session.snapshot(&snapshot));
    try std.testing.expectEqual(
        ResultCode.ok,
        session.readOperations(&readInput(snapshot.state_revision, 0), &operations),
    );
    try std.testing.expectEqual(@as(u64, 2), operations[0].progress_sequence);
    try std.testing.expectEqual(@as(u32, 50_000), operations[0].percent_milli);
    try std.testing.expectEqual(@as(u64, 50), operations[0].bytes_done);
}

test "retry scheduling and cancellation grace are explicit" {
    var config = testConfig();
    var session = try state.Session.create(&config);
    defer session.destroy();
    var submit_output = std.mem.zeroes(protocol.SubmitOutput);
    var submit = submitInput(1, 0, 1, 100, 100);
    submit.valid_mask = 0;
    submit.domain_id = zeroHandle();
    submit.maximum_attempts = 2;
    submit.retry_delay_milliseconds = 10;
    submit.cancel_grace_milliseconds = 5;
    try std.testing.expectEqual(ResultCode.ok, session.submit(&submit, &submit_output));

    const first_plan = try runPlan(session, 1, 101, 101, 8);
    var actions = zeroActions();
    try readCurrentActions(session, &first_plan, &actions);
    var retry = feedbackInput(
        &actions[0],
        1,
        .start_retryable_failure,
        102,
        102,
    );
    try std.testing.expectEqual(ResultCode.ok, session.feedback(&retry));
    const early = try runPlan(session, 2, 111, 111, 8);
    try std.testing.expectEqual(@as(u32, 0), early.action_count);
    const due = try runPlan(session, 3, 112, 112, 8);
    try std.testing.expectEqual(@as(u32, 1), due.action_count);
    actions = zeroActions();
    try readCurrentActions(session, &due, &actions);
    try std.testing.expectEqual(@as(u32, 2), actions[0].attempt_number);
    var started = feedbackInput(&actions[0], 2, .started, 113, 113);
    try std.testing.expectEqual(ResultCode.ok, session.feedback(&started));

    var cancel_output = std.mem.zeroes(protocol.CancelOutput);
    var cancel = cancelInput(submit.operation_id, 1, 114, 114);
    try std.testing.expectEqual(ResultCode.ok, session.cancel(&cancel, &cancel_output));
    const cancel_plan = try runPlan(session, 4, 115, 115, 8);
    try std.testing.expectEqual(@as(u32, 1), cancel_plan.action_count);
    actions = zeroActions();
    try readCurrentActions(session, &cancel_plan, &actions);
    try std.testing.expectEqual(@intFromEnum(protocol.ActionKind.cancel), actions[0].kind);

    const expired = try runPlan(session, 5, 120, 120, 8);
    try std.testing.expectEqual(@as(u32, 0), expired.action_count);
    var operations = zeroOperations();
    try std.testing.expectEqual(
        ResultCode.ok,
        session.readOperations(&readInput(expired.state_revision, 0), &operations),
    );
    try std.testing.expectEqual(
        @intFromEnum(protocol.OperationState.state_uncertain),
        operations[0].state,
    );
    var late = feedbackInput(&actions[0], 3, .cancel_completed, 121, 121);
    try std.testing.expectEqual(ResultCode.stale_frame, session.feedback(&late));
}

test "cancel requested during start prevents every later retry" {
    var config = testConfig();
    var session = try state.Session.create(&config);
    defer session.destroy();
    var submit_output = std.mem.zeroes(protocol.SubmitOutput);
    var submit = submitInput(1, 0, 1, 100, 100);
    submit.valid_mask = 0;
    submit.domain_id = zeroHandle();
    try std.testing.expectEqual(ResultCode.ok, session.submit(&submit, &submit_output));
    const start_plan = try runPlan(session, 1, 101, 101, 8);
    var actions = zeroActions();
    try readCurrentActions(session, &start_plan, &actions);

    var cancel_output = std.mem.zeroes(protocol.CancelOutput);
    var cancel = cancelInput(submit.operation_id, 1, 102, 102);
    try std.testing.expectEqual(ResultCode.ok, session.cancel(&cancel, &cancel_output));
    var retry = feedbackInput(
        &actions[0],
        1,
        .start_retryable_failure,
        103,
        103,
    );
    try std.testing.expectEqual(ResultCode.ok, session.feedback(&retry));
    const after_retry_deadline = try runPlan(session, 2, 200, 200, 8);
    try std.testing.expectEqual(@as(u32, 0), after_retry_deadline.action_count);
    try std.testing.expectEqual(@as(u32, 0), after_retry_deadline.active_operation_count);
    try std.testing.expectEqual(@as(u32, 1), after_retry_deadline.terminal_operation_count);
    var operations = zeroOperations();
    try std.testing.expectEqual(
        ResultCode.ok,
        session.readOperations(
            &readInput(after_retry_deadline.state_revision, 0),
            &operations,
        ),
    );
    try std.testing.expectEqual(
        @intFromEnum(protocol.OperationState.canceled),
        operations[0].state,
    );
}

test "new attempt clears prior progress and checkpoint state" {
    var config = testConfig();
    var session = try state.Session.create(&config);
    defer session.destroy();
    var submit_output = std.mem.zeroes(protocol.SubmitOutput);
    var submit = submitInput(1, 0, 1, 100, 100);
    submit.valid_mask = 0;
    submit.domain_id = zeroHandle();
    try std.testing.expectEqual(ResultCode.ok, session.submit(&submit, &submit_output));
    const first_plan = try runPlan(session, 1, 101, 101, 8);
    var actions = zeroActions();
    try readCurrentActions(session, &first_plan, &actions);
    var started = feedbackInput(&actions[0], 1, .started, 102, 102);
    try std.testing.expectEqual(ResultCode.ok, session.feedback(&started));

    var progress = progressInput(&actions[0], 1, 103, 103);
    progress.valid_mask = protocol.ProgressValid.percent_milli |
        protocol.ProgressValid.checkpoint;
    progress.percent_milli = 50_000;
    progress.checkpoint_handle = handle(700);
    try std.testing.expectEqual(ResultCode.ok, session.reportProgress(&progress));
    var retry = completionInput(&actions[0], 1, .retryable_failure, 104, 104);
    try std.testing.expectEqual(ResultCode.ok, session.complete(&retry));

    const second_plan = try runPlan(session, 2, 114, 114, 8);
    try std.testing.expectEqual(@as(u32, 1), second_plan.action_count);
    var operations = zeroOperations();
    try std.testing.expectEqual(
        ResultCode.ok,
        session.readOperations(&readInput(second_plan.state_revision, 0), &operations),
    );
    try std.testing.expectEqual(@as(u32, 2), operations[0].attempt_number);
    try std.testing.expectEqual(@as(u64, 0), operations[0].progress_sequence);
    try std.testing.expectEqual(@as(u64, 0), operations[0].progress_valid_mask);
    try std.testing.expect(operations[0].checkpoint_handle.isZero());
    try std.testing.expectEqual(
        @as(u64, 0),
        operations[0].flags & protocol.OperationFlags.progress_valid,
    );
}

test "older compatible durable generation imports after hot configuration" {
    var config = testConfig();
    var source = try state.Session.create(&config);
    defer source.destroy();
    var submit_output = std.mem.zeroes(protocol.SubmitOutput);
    var submit = submitInput(1, 0, 1, 100, 100);
    submit.valid_mask = 0;
    submit.domain_id = zeroHandle();
    try std.testing.expectEqual(ResultCode.ok, source.submit(&submit, &submit_output));
    var header = std.mem.zeroes(protocol.PersistenceHeader);
    var records = zeroOperations();
    var export_input = persistenceInput(1);
    try std.testing.expectEqual(
        ResultCode.ok,
        source.exportPersistence(&export_input, &header, &records),
    );
    try std.testing.expectEqual(@as(u64, 1), header.configuration_generation);

    var current = config;
    current.generation = 2;
    var restored = try state.Session.create(&current);
    defer restored.destroy();
    var import_input = persistenceImportInput(1, 101, 101, current.clock_instance_id);
    import_input.configuration_generation = current.generation;
    try std.testing.expectEqual(
        ResultCode.ok,
        restored.importPersistence(&import_input, &header, records[0..1]),
    );
    var snapshot = std.mem.zeroes(protocol.SnapshotOutput);
    try std.testing.expectEqual(ResultCode.ok, restored.snapshot(&snapshot));
    try std.testing.expectEqual(current.generation, snapshot.configuration_generation);
    var operations = zeroOperations();
    var read = readInput(snapshot.state_revision, 0);
    read.configuration_generation = current.generation;
    try std.testing.expectEqual(
        ResultCode.ok,
        restored.readOperations(&read, &operations),
    );
    try std.testing.expectEqual(
        @intFromEnum(protocol.OperationState.recovery_pending),
        operations[0].state,
    );
}

test "older durable terminal set is pruned by the current hot limit on import" {
    var config = testConfig();
    var source = try state.Session.create(&config);
    defer source.destroy();
    var submit_output = std.mem.zeroes(protocol.SubmitOutput);
    var submit = submitInput(1, 0, 1, 100, 100);
    submit.valid_mask = 0;
    submit.domain_id = zeroHandle();
    try std.testing.expectEqual(ResultCode.ok, source.submit(&submit, &submit_output));
    const start_plan = try runPlan(source, 1, 101, 101, 8);
    var actions = zeroActions();
    try readCurrentActions(source, &start_plan, &actions);
    var started = feedbackInput(&actions[0], 1, .started, 102, 102);
    try std.testing.expectEqual(ResultCode.ok, source.feedback(&started));
    var completed = completionInput(&actions[0], 1, .succeeded, 103, 103);
    try std.testing.expectEqual(ResultCode.ok, source.complete(&completed));
    var header = std.mem.zeroes(protocol.PersistenceHeader);
    var records = zeroOperations();
    var export_input = persistenceInput(1);
    try std.testing.expectEqual(
        ResultCode.ok,
        source.exportPersistence(&export_input, &header, &records),
    );

    var current = config;
    current.generation = 2;
    current.maximum_recent_terminal_count = 0;
    var restored = try state.Session.create(&current);
    defer restored.destroy();
    var import_input = persistenceImportInput(1, 104, 104, current.clock_instance_id);
    import_input.configuration_generation = current.generation;
    try std.testing.expectEqual(
        ResultCode.ok,
        restored.importPersistence(&import_input, &header, records[0..1]),
    );
    var snapshot = std.mem.zeroes(protocol.SnapshotOutput);
    try std.testing.expectEqual(ResultCode.ok, restored.snapshot(&snapshot));
    try std.testing.expectEqual(@as(u32, 0), snapshot.operation_count);
    try std.testing.expectEqual(@as(u32, 0), snapshot.terminal_operation_count);
}

test "persistence reclassifies inflight work and rejects altered payload" {
    var config = testConfig();
    var source = try state.Session.create(&config);
    defer source.destroy();
    var submit_output = std.mem.zeroes(protocol.SubmitOutput);
    var submit = submitInput(1, 5, 1, 100, 100);
    try std.testing.expectEqual(ResultCode.ok, source.submit(&submit, &submit_output));
    const plan_output = try runPlan(source, 1, 101, 101, 8);
    var actions = zeroActions();
    try readCurrentActions(source, &plan_output, &actions);
    var started = feedbackInput(&actions[0], 1, .started, 102, 102);
    try std.testing.expectEqual(ResultCode.ok, source.feedback(&started));

    var header = std.mem.zeroes(protocol.PersistenceHeader);
    var records = zeroOperations();
    var export_input = persistenceInput(1);
    try std.testing.expectEqual(
        ResultCode.ok,
        source.exportPersistence(&export_input, &header, &records),
    );
    try std.testing.expectEqual(@as(u32, 1), header.operation_count);

    var restored = try state.Session.create(&config);
    defer restored.destroy();
    var import_input = persistenceImportInput(1, 103, 103, config.clock_instance_id);
    try std.testing.expectEqual(
        ResultCode.ok,
        restored.importPersistence(&import_input, &header, records[0..1]),
    );
    var snapshot = std.mem.zeroes(protocol.SnapshotOutput);
    try std.testing.expectEqual(ResultCode.ok, restored.snapshot(&snapshot));
    var restored_operations = zeroOperations();
    try std.testing.expectEqual(
        ResultCode.ok,
        restored.readOperations(
            &readInput(snapshot.state_revision, 0),
            &restored_operations,
        ),
    );
    try std.testing.expectEqual(
        @intFromEnum(protocol.OperationState.recovery_pending),
        restored_operations[0].state,
    );
    try std.testing.expectEqual(
        @intFromEnum(protocol.OperationState.running),
        restored_operations[0].recovery_origin_state,
    );
    const recovery_plan = try runPlan(restored, 2, 103, 103, 8);
    try std.testing.expectEqual(@as(u32, 1), recovery_plan.action_count);
    actions = zeroActions();
    try readCurrentActions(restored, &recovery_plan, &actions);
    try std.testing.expectEqual(@intFromEnum(protocol.ActionKind.recover), actions[0].kind);

    var tampered = records;
    tampered[0].priority += 1;
    const revision_before_rejected_replace = recovery_plan.state_revision;
    import_input.operation_epoch = 2;
    import_input.observed_utc_milliseconds = 104;
    import_input.observed_monotonic_milliseconds = 104;
    try std.testing.expectEqual(
        ResultCode.invalid_argument,
        restored.importPersistence(&import_input, &header, tampered[0..1]),
    );
    snapshot = std.mem.zeroes(protocol.SnapshotOutput);
    try std.testing.expectEqual(ResultCode.ok, restored.snapshot(&snapshot));
    try std.testing.expectEqual(revision_before_rejected_replace, snapshot.state_revision);
    try std.testing.expectEqual(@as(u32, 1), snapshot.operation_count);
}

test "feedback at a pending deadline never wins a plan expiration race" {
    var config = testConfig();

    var start_session = try state.Session.create(&config);
    defer start_session.destroy();
    var submit_output = std.mem.zeroes(protocol.SubmitOutput);
    var submit = submitInput(1, 0, 1, 100, 100);
    submit.valid_mask = 0;
    submit.domain_id = zeroHandle();
    submit.execution_timeout_milliseconds = 5;
    try std.testing.expectEqual(
        ResultCode.ok,
        start_session.submit(&submit, &submit_output),
    );
    const start_plan = try runPlan(start_session, 1, 101, 101, 8);
    var actions = zeroActions();
    try readCurrentActions(start_session, &start_plan, &actions);
    var late_start = feedbackInput(&actions[0], 1, .started, 106, 106);
    try std.testing.expectEqual(
        ResultCode.stale_frame,
        start_session.feedback(&late_start),
    );
    const expired_start = try runPlan(start_session, 2, 106, 106, 8);
    try std.testing.expectEqual(@as(u32, 1), expired_start.terminal_operation_count);

    var cancel_session = try state.Session.create(&config);
    defer cancel_session.destroy();
    submit_output = std.mem.zeroes(protocol.SubmitOutput);
    submit = submitInput(2, 0, 1, 200, 200);
    submit.valid_mask = 0;
    submit.domain_id = zeroHandle();
    submit.cancel_grace_milliseconds = 5;
    try std.testing.expectEqual(
        ResultCode.ok,
        cancel_session.submit(&submit, &submit_output),
    );
    const cancel_start_plan = try runPlan(cancel_session, 1, 201, 201, 8);
    actions = zeroActions();
    try readCurrentActions(cancel_session, &cancel_start_plan, &actions);
    var started = feedbackInput(&actions[0], 1, .started, 202, 202);
    try std.testing.expectEqual(ResultCode.ok, cancel_session.feedback(&started));
    var cancel_output = std.mem.zeroes(protocol.CancelOutput);
    var cancel = cancelInput(submit.operation_id, 1, 203, 203);
    try std.testing.expectEqual(
        ResultCode.ok,
        cancel_session.cancel(&cancel, &cancel_output),
    );
    const cancel_plan = try runPlan(cancel_session, 2, 204, 204, 8);
    actions = zeroActions();
    try readCurrentActions(cancel_session, &cancel_plan, &actions);
    var late_cancel = feedbackInput(
        &actions[0],
        2,
        .cancel_completed,
        209,
        209,
    );
    try std.testing.expectEqual(
        ResultCode.stale_frame,
        cancel_session.feedback(&late_cancel),
    );
    const expired_cancel = try runPlan(cancel_session, 3, 209, 209, 8);
    try std.testing.expectEqual(@as(u32, 1), expired_cancel.terminal_operation_count);

    var recovery_source = try state.Session.create(&config);
    defer recovery_source.destroy();
    submit_output = std.mem.zeroes(protocol.SubmitOutput);
    submit = submitInput(3, 0, 1, 300, 300);
    submit.valid_mask = 0;
    submit.domain_id = zeroHandle();
    submit.execution_timeout_milliseconds = 5;
    try std.testing.expectEqual(
        ResultCode.ok,
        recovery_source.submit(&submit, &submit_output),
    );
    var header = std.mem.zeroes(protocol.PersistenceHeader);
    var records = zeroOperations();
    var export_input = persistenceInput(1);
    try std.testing.expectEqual(
        ResultCode.ok,
        recovery_source.exportPersistence(&export_input, &header, &records),
    );
    var recovery_session = try state.Session.create(&config);
    defer recovery_session.destroy();
    var import_input = persistenceImportInput(1, 301, 301, config.clock_instance_id);
    try std.testing.expectEqual(
        ResultCode.ok,
        recovery_session.importPersistence(&import_input, &header, records[0..1]),
    );
    const recovery_plan = try runPlan(recovery_session, 1, 302, 302, 8);
    actions = zeroActions();
    try readCurrentActions(recovery_session, &recovery_plan, &actions);
    const recovery_deadline = actions[0].deadline_monotonic_milliseconds;
    var late_recovery = feedbackInput(
        &actions[0],
        1,
        .recovered_queued,
        @intCast(recovery_deadline),
        recovery_deadline,
    );
    try std.testing.expectEqual(
        ResultCode.stale_frame,
        recovery_session.feedback(&late_recovery),
    );
    const expired_recovery = try runPlan(
        recovery_session,
        2,
        @intCast(recovery_deadline),
        recovery_deadline,
        8,
    );
    try std.testing.expectEqual(
        @as(u32, 1),
        expired_recovery.terminal_operation_count,
    );
}

test "unacknowledged start action expires to uncertain without a duplicate action" {
    var config = testConfig();
    var session = try state.Session.create(&config);
    defer session.destroy();
    var output = std.mem.zeroes(protocol.SubmitOutput);
    var submit = submitInput(1, 0, 1, 100, 100);
    submit.valid_mask = 0;
    submit.domain_id = zeroHandle();
    submit.execution_timeout_milliseconds = 5;
    try std.testing.expectEqual(ResultCode.ok, session.submit(&submit, &output));
    const started_plan = try runPlan(session, 1, 101, 101, 8);
    try std.testing.expectEqual(@as(u32, 1), started_plan.action_count);
    var actions = zeroActions();
    try readCurrentActions(session, &started_plan, &actions);
    const before_deadline = try runPlan(session, 2, 105, 105, 8);
    try std.testing.expectEqual(@as(u32, 0), before_deadline.action_count);
    const expired = try runPlan(session, 3, 106, 106, 8);
    try std.testing.expectEqual(@as(u32, 0), expired.action_count);
    var operations = zeroOperations();
    var read = readInput(expired.state_revision, 0);
    try std.testing.expectEqual(
        ResultCode.ok,
        session.readOperations(&read, &operations),
    );
    try std.testing.expectEqual(
        @intFromEnum(protocol.OperationState.state_uncertain),
        operations[0].state,
    );
}

test "cross-clock persistence uses one coherent wall and monotonic anchor" {
    var source_config = testConfig();
    var source = try state.Session.create(&source_config);
    defer source.destroy();
    var submit_output = std.mem.zeroes(protocol.SubmitOutput);
    var first = submitInput(1, 0, 1, 1_000, 100);
    first.valid_mask = 0;
    first.domain_id = zeroHandle();
    try std.testing.expectEqual(ResultCode.ok, source.submit(&first, &submit_output));
    var second = submitInput(2, 0, 2, 900, 200);
    second.valid_mask = 0;
    second.domain_id = zeroHandle();
    second.terminal_retention_milliseconds = 50;
    submit_output = std.mem.zeroes(protocol.SubmitOutput);
    try std.testing.expectEqual(ResultCode.ok, source.submit(&second, &submit_output));
    const start_plan = try runPlan(source, 1, 900, 201, 8);
    var actions = zeroActions();
    try readCurrentActions(source, &start_plan, &actions);
    var first_started = feedbackInput(&actions[0], 1, .started, 1_000, 202);
    try std.testing.expectEqual(ResultCode.ok, source.feedback(&first_started));
    var second_started = feedbackInput(&actions[1], 2, .started, 900, 203);
    try std.testing.expectEqual(ResultCode.ok, source.feedback(&second_started));
    var completed = completionInput(&actions[1], 1, .succeeded, 900, 204);
    try std.testing.expectEqual(ResultCode.ok, source.complete(&completed));

    var header = std.mem.zeroes(protocol.PersistenceHeader);
    var records = zeroOperations();
    var export_input = persistenceInput(1);
    try std.testing.expectEqual(
        ResultCode.ok,
        source.exportPersistence(&export_input, &header, &records),
    );
    try std.testing.expectEqual(@as(i64, 900), header.last_observed_utc_milliseconds);
    try std.testing.expectEqual(
        @as(u64, 204),
        header.last_observed_monotonic_milliseconds,
    );

    var restored_config = source_config;
    restored_config.clock_instance_id = handle(0x9999);
    var restored = try state.Session.create(&restored_config);
    defer restored.destroy();
    var import_input = persistenceImportInput(
        1,
        1_000,
        10,
        restored_config.clock_instance_id,
    );
    try std.testing.expectEqual(
        ResultCode.ok,
        restored.importPersistence(&import_input, &header, records[0..2]),
    );
    var snapshot = std.mem.zeroes(protocol.SnapshotOutput);
    try std.testing.expectEqual(ResultCode.ok, restored.snapshot(&snapshot));
    try std.testing.expectEqual(@as(u64, 10), snapshot.next_wake_monotonic_milliseconds);
    const expired = try runPlan(restored, 2, 1_000, 10, 8);
    try std.testing.expectEqual(@as(u32, 0), expired.terminal_operation_count);
}

test "durable queue order survives cross-clock historical clamping" {
    var source_config = testConfig();
    source_config.maximum_global_running_count = 1;
    var source = try state.Session.create(&source_config);
    defer source.destroy();
    var submit_output = std.mem.zeroes(protocol.SubmitOutput);
    var later_created = submitInput(1, 0, 1, 1_000, 1_000);
    later_created.valid_mask = 0;
    later_created.domain_id = zeroHandle();
    later_created.observed_utc_milliseconds = 2_000;
    later_created.observed_monotonic_milliseconds = 2_000;
    try std.testing.expectEqual(
        ResultCode.ok,
        source.submit(&later_created, &submit_output),
    );
    var earlier_created = submitInput(2, 0, 2, 900, 900);
    earlier_created.valid_mask = 0;
    earlier_created.domain_id = zeroHandle();
    earlier_created.observed_utc_milliseconds = 2_001;
    earlier_created.observed_monotonic_milliseconds = 2_001;
    submit_output = std.mem.zeroes(protocol.SubmitOutput);
    try std.testing.expectEqual(
        ResultCode.ok,
        source.submit(&earlier_created, &submit_output),
    );

    var header = std.mem.zeroes(protocol.PersistenceHeader);
    var records = zeroOperations();
    var export_input = persistenceInput(1);
    try std.testing.expectEqual(
        ResultCode.ok,
        source.exportPersistence(&export_input, &header, &records),
    );
    try std.testing.expectEqual(@as(u64, 2), records[0].queue_order);
    try std.testing.expectEqual(@as(u64, 1), records[1].queue_order);

    var restored_config = source_config;
    restored_config.clock_instance_id = handle(0x9999);
    var restored = try state.Session.create(&restored_config);
    defer restored.destroy();
    var import_input = persistenceImportInput(
        1,
        20_000,
        1,
        restored_config.clock_instance_id,
    );
    try std.testing.expectEqual(
        ResultCode.ok,
        restored.importPersistence(&import_input, &header, records[0..2]),
    );
    const recovery_plan = try runPlan(restored, 1, 20_001, 2, 8);
    var actions = zeroActions();
    try readCurrentActions(restored, &recovery_plan, &actions);
    var recovered = feedbackInput(
        &actions[0],
        1,
        .recovered_queued,
        20_002,
        3,
    );
    try std.testing.expectEqual(ResultCode.ok, restored.feedback(&recovered));
    recovered = feedbackInput(
        &actions[1],
        2,
        .recovered_queued,
        20_003,
        4,
    );
    try std.testing.expectEqual(ResultCode.ok, restored.feedback(&recovered));
    const start_plan = try runPlan(restored, 2, 20_004, 5, 8);
    actions = zeroActions();
    try readCurrentActions(restored, &start_plan, &actions);
    try std.testing.expectEqual(@as(u32, 1), start_plan.action_count);
    try std.testing.expect(
        protocol.equalHandle(earlier_created.operation_id, actions[0].operation_id),
    );
}

test "persistence rebases terminal deadline across a new monotonic clock instance" {
    var source_config = testConfig();
    var source = try state.Session.create(&source_config);
    defer source.destroy();
    var submit_output = std.mem.zeroes(protocol.SubmitOutput);
    var submit = submitInput(1, 0, 1, 100, 100_000);
    submit.valid_mask = 0;
    submit.domain_id = zeroHandle();
    submit.terminal_retention_milliseconds = 1_000;
    try std.testing.expectEqual(ResultCode.ok, source.submit(&submit, &submit_output));
    const start_plan = try runPlan(source, 1, 101, 100_001, 8);
    var actions = zeroActions();
    try readCurrentActions(source, &start_plan, &actions);
    var started = feedbackInput(&actions[0], 1, .started, 102, 100_002);
    try std.testing.expectEqual(ResultCode.ok, source.feedback(&started));
    var completed = completionInput(&actions[0], 1, .succeeded, 103, 100_003);
    try std.testing.expectEqual(ResultCode.ok, source.complete(&completed));

    var header = std.mem.zeroes(protocol.PersistenceHeader);
    var records = zeroOperations();
    var export_input = persistenceInput(1);
    try std.testing.expectEqual(
        ResultCode.ok,
        source.exportPersistence(&export_input, &header, &records),
    );

    var restored_config = source_config;
    restored_config.clock_instance_id = handle(0x9999);
    var restored = try state.Session.create(&restored_config);
    defer restored.destroy();
    var import_input = persistenceImportInput(
        1,
        603,
        10,
        restored_config.clock_instance_id,
    );
    try std.testing.expectEqual(
        ResultCode.ok,
        restored.importPersistence(&import_input, &header, records[0..1]),
    );
    var snapshot = std.mem.zeroes(protocol.SnapshotOutput);
    try std.testing.expectEqual(ResultCode.ok, restored.snapshot(&snapshot));
    try std.testing.expectEqual(@as(u64, 510), snapshot.next_wake_monotonic_milliseconds);

    var rebased_header = std.mem.zeroes(protocol.PersistenceHeader);
    var rebased_records = zeroOperations();
    var rebased_export = persistenceInput(2);
    try std.testing.expectEqual(
        ResultCode.ok,
        restored.exportPersistence(
            &rebased_export,
            &rebased_header,
            &rebased_records,
        ),
    );
    var roundtrip = try state.Session.create(&restored_config);
    defer roundtrip.destroy();
    var roundtrip_import = persistenceImportInput(
        2,
        604,
        11,
        restored_config.clock_instance_id,
    );
    try std.testing.expectEqual(
        ResultCode.ok,
        roundtrip.importPersistence(
            &roundtrip_import,
            &rebased_header,
            rebased_records[0..1],
        ),
    );

    const retained = try runPlan(restored, 2, 1_102, 509, 8);
    try std.testing.expectEqual(@as(u32, 1), retained.terminal_operation_count);
    const retired = try runPlan(restored, 3, 1_103, 510, 8);
    try std.testing.expectEqual(@as(u32, 0), retired.terminal_operation_count);
}

test "terminal count pruning releases domain identity for later work" {
    var config = testConfig();
    config.maximum_recent_terminal_count = 1;
    var session = try state.Session.create(&config);
    defer session.destroy();
    var output = std.mem.zeroes(protocol.SubmitOutput);
    var first = submitInput(1, 9, 1, 100, 100);
    try std.testing.expectEqual(ResultCode.ok, session.submit(&first, &output));
    const plan_one = try runPlan(session, 1, 101, 101, 8);
    var actions = zeroActions();
    try readCurrentActions(session, &plan_one, &actions);
    var started = feedbackInput(&actions[0], 1, .started, 102, 102);
    try std.testing.expectEqual(ResultCode.ok, session.feedback(&started));
    var completed = completionInput(&actions[0], 1, .succeeded, 103, 103);
    try std.testing.expectEqual(ResultCode.ok, session.complete(&completed));

    output = std.mem.zeroes(protocol.SubmitOutput);
    var second = submitInput(2, 9, 2, 104, 104);
    try std.testing.expectEqual(ResultCode.ok, session.submit(&second, &output));
    try std.testing.expectEqual(protocol.SubmitOutputFlags.inserted, output.flags);
    const plan_two = try runPlan(session, 2, 105, 105, 8);
    actions = zeroActions();
    try readCurrentActions(session, &plan_two, &actions);
    started = feedbackInput(&actions[0], 2, .started, 106, 106);
    try std.testing.expectEqual(ResultCode.ok, session.feedback(&started));
    completed = completionInput(&actions[0], 2, .succeeded, 107, 107);
    try std.testing.expectEqual(ResultCode.ok, session.complete(&completed));

    const prune_plan = try runPlan(session, 3, 108, 108, 8);
    try std.testing.expectEqual(@as(u32, 1), prune_plan.terminal_operation_count);
    var snapshot = std.mem.zeroes(protocol.SnapshotOutput);
    try std.testing.expectEqual(ResultCode.ok, session.snapshot(&snapshot));
    try std.testing.expectEqual(@as(u32, 1), snapshot.operation_count);
}

test "terminal retention count is enforced without waiting for another plan" {
    var config = testConfig();
    config.maximum_recent_terminal_count = 0;
    var session = try state.Session.create(&config);
    defer session.destroy();
    var output = std.mem.zeroes(protocol.SubmitOutput);
    var submit = submitInput(1, 7, 1, 100, 100);
    try std.testing.expectEqual(ResultCode.ok, session.submit(&submit, &output));
    const plan_output = try runPlan(session, 1, 101, 101, 8);
    var actions = zeroActions();
    try readCurrentActions(session, &plan_output, &actions);
    var started = feedbackInput(&actions[0], 1, .started, 102, 102);
    try std.testing.expectEqual(ResultCode.ok, session.feedback(&started));
    var completed = completionInput(&actions[0], 1, .succeeded, 103, 103);
    try std.testing.expectEqual(ResultCode.ok, session.complete(&completed));

    var snapshot = std.mem.zeroes(protocol.SnapshotOutput);
    try std.testing.expectEqual(ResultCode.ok, session.snapshot(&snapshot));
    try std.testing.expectEqual(@as(u32, 0), snapshot.operation_count);
    try std.testing.expectEqual(@as(u32, 0), snapshot.terminal_operation_count);
    try std.testing.expectEqual(@as(u64, 0), snapshot.next_wake_monotonic_milliseconds);

    output = std.mem.zeroes(protocol.SubmitOutput);
    var replacement = submitInput(2, 7, 2, 104, 104);
    try std.testing.expectEqual(ResultCode.ok, session.submit(&replacement, &output));
}

test "hot reconfiguration prunes terminal records to the new limit atomically" {
    var config = testConfig();
    var session = try state.Session.create(&config);
    defer session.destroy();
    var output = std.mem.zeroes(protocol.SubmitOutput);
    var submit = submitInput(1, 7, 1, 100, 100);
    try std.testing.expectEqual(ResultCode.ok, session.submit(&submit, &output));
    const plan_output = try runPlan(session, 1, 101, 101, 8);
    var actions = zeroActions();
    try readCurrentActions(session, &plan_output, &actions);
    var started = feedbackInput(&actions[0], 1, .started, 102, 102);
    try std.testing.expectEqual(ResultCode.ok, session.feedback(&started));
    var completed = completionInput(&actions[0], 1, .succeeded, 103, 103);
    try std.testing.expectEqual(ResultCode.ok, session.complete(&completed));
    var before = std.mem.zeroes(protocol.SnapshotOutput);
    try std.testing.expectEqual(ResultCode.ok, session.snapshot(&before));
    try std.testing.expectEqual(@as(u32, 1), before.terminal_operation_count);

    var hot = config;
    hot.generation = 2;
    hot.maximum_recent_terminal_count = 0;
    try std.testing.expectEqual(ResultCode.ok, session.reconfigure(&hot));
    var after = std.mem.zeroes(protocol.SnapshotOutput);
    try std.testing.expectEqual(ResultCode.ok, session.snapshot(&after));
    try std.testing.expectEqual(@as(u32, 0), after.operation_count);
    try std.testing.expect(after.state_revision > before.state_revision);
    try std.testing.expectEqual(@as(u64, 0), after.next_wake_monotonic_milliseconds);
}

test "epochs, read tokens, and output initialization fail closed" {
    var config = testConfig();
    var session = try state.Session.create(&config);
    defer session.destroy();
    var submit_output = std.mem.zeroes(protocol.SubmitOutput);
    var submit = submitInput(1, 0, 1, 100, 100);
    submit.valid_mask = 0;
    submit.domain_id = zeroHandle();
    try std.testing.expectEqual(ResultCode.ok, session.submit(&submit, &submit_output));
    var stale_output = std.mem.zeroes(protocol.SubmitOutput);
    try std.testing.expectEqual(
        ResultCode.stale_frame,
        session.submit(&submit, &stale_output),
    );

    const plan_output = try runPlan(session, 1, 101, 101, 8);
    var operations = zeroOperations();
    var stale_read = readInput(plan_output.state_revision - 1, 0);
    try std.testing.expectEqual(
        ResultCode.stale_frame,
        session.readOperations(&stale_read, &operations),
    );
    var wrong_plan = readInput(plan_output.state_revision, plan_output.plan_epoch + 1);
    var actions = zeroActions();
    try std.testing.expectEqual(
        ResultCode.stale_frame,
        session.readActions(&wrong_plan, &actions),
    );
    var exact_read = readInput(plan_output.state_revision, plan_output.plan_epoch);
    try std.testing.expectEqual(
        ResultCode.ok,
        session.readActions(&exact_read, &actions),
    );
    var wrong_feedback = feedbackInput(&actions[0], 1, .started, 102, 102);
    wrong_feedback.plan_epoch += 1;
    try std.testing.expectEqual(
        ResultCode.stale_frame,
        session.feedback(&wrong_feedback),
    );
    var started = feedbackInput(&actions[0], 1, .started, 102, 102);
    try std.testing.expectEqual(ResultCode.ok, session.feedback(&started));
    var bad_progress = progressInput(&actions[0], 1, 103, 103);
    bad_progress.reserved_u32 = 1;
    try std.testing.expectEqual(
        ResultCode.abi_mismatch,
        session.reportProgress(&bad_progress),
    );
    var bad_completion = completionInput(
        &actions[0],
        1,
        .succeeded,
        103,
        103,
    );
    bad_completion.reserved_u32 = 1;
    try std.testing.expectEqual(
        ResultCode.abi_mismatch,
        session.complete(&bad_completion),
    );

    var dirty_snapshot = std.mem.zeroes(protocol.SnapshotOutput);
    dirty_snapshot.flags = 1;
    try std.testing.expectEqual(
        ResultCode.ok,
        session.snapshot(&dirty_snapshot),
    );
    try std.testing.expectEqual(@as(u64, 0), dirty_snapshot.flags & ~protocol.SnapshotFlags.known);
}

fn testConfig() protocol.Config {
    return .{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.Config),
        .generation = 1,
        .session_instance_id = handle(0x1234),
        .clock_instance_id = handle(0x5678),
        .maximum_operation_count = 8,
        .maximum_domain_count = 8,
        .maximum_action_count = 8,
        .operation_index_capacity = 16,
        .domain_index_capacity = 16,
        .maximum_global_running_count = 2,
        .maximum_recent_terminal_count = 4,
        .maximum_read_count = 8,
        .maximum_start_actions_per_plan = 8,
        .maximum_cancel_actions_per_plan = 8,
        .maximum_recover_actions_per_plan = 8,
        .reserved_u32 = 0,
        .maximum_future_skew_milliseconds = 5_000,
        .maximum_persistence_byte_count = @sizeOf(protocol.PersistenceHeader) +
            8 * @sizeOf(protocol.PersistenceRecord),
        .resident_byte_budget = 1024 * 1024,
        .flags = 0,
        .reserved = [_]u64{0} ** 5,
    };
}

fn submitInput(
    operation: u64,
    domain: u64,
    epoch: u64,
    utc: i64,
    monotonic: u64,
) protocol.SubmitInput {
    return .{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.SubmitInput),
        .configuration_generation = 1,
        .submit_epoch = epoch,
        .operation_id = handle(operation),
        .domain_id = handle(domain),
        .kind_handle = handle(100),
        .title_handle = zeroHandle(),
        .request_handle = zeroHandle(),
        .priority = 0,
        .maximum_attempts = 3,
        .retry_delay_milliseconds = 10,
        .execution_timeout_milliseconds = 100,
        .cancel_grace_milliseconds = 20,
        .terminal_retention_milliseconds = 1_000,
        .created_utc_milliseconds = utc,
        .created_monotonic_milliseconds = monotonic,
        .observed_utc_milliseconds = utc,
        .observed_monotonic_milliseconds = monotonic,
        .valid_mask = protocol.SubmitValid.domain,
        .flags = 0,
        .reserved = [_]u64{0} ** 4,
    };
}

fn cancelInput(
    operation_id: protocol.Handle128,
    epoch: u64,
    utc: i64,
    monotonic: u64,
) protocol.CancelInput {
    return .{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.CancelInput),
        .configuration_generation = 1,
        .request_epoch = epoch,
        .operation_id = operation_id,
        .observed_utc_milliseconds = utc,
        .observed_monotonic_milliseconds = monotonic,
        .flags = 0,
        .reserved = [_]u64{0} ** 4,
    };
}

fn runPlan(
    session: *state.Session,
    epoch: u64,
    utc: i64,
    monotonic: u64,
    capacity: u32,
) !protocol.PlanOutput {
    var input = protocol.PlanInput{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.PlanInput),
        .configuration_generation = 1,
        .plan_epoch = epoch,
        .observed_utc_milliseconds = utc,
        .observed_monotonic_milliseconds = monotonic,
        .action_capacity = capacity,
        .flags = 0,
        .reserved = [_]u64{0} ** 4,
    };
    var output = std.mem.zeroes(protocol.PlanOutput);
    try std.testing.expectEqual(ResultCode.ok, session.plan(&input, &output));
    return output;
}

fn feedbackInput(
    action: *const protocol.ActionOutput,
    epoch: u64,
    outcome: protocol.ActionFeedbackOutcome,
    utc: i64,
    monotonic: u64,
) protocol.ActionFeedbackInput {
    return .{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.ActionFeedbackInput),
        .configuration_generation = action.configuration_generation,
        .feedback_epoch = epoch,
        .action_id = action.action_id,
        .plan_epoch = action.plan_epoch,
        .operation_id = action.operation_id,
        .attempt_token = action.attempt_token,
        .action_kind = action.kind,
        .outcome = @intFromEnum(outcome),
        .observed_utc_milliseconds = utc,
        .observed_monotonic_milliseconds = monotonic,
        .result_handle = zeroHandle(),
        .error_handle = zeroHandle(),
        .valid_mask = 0,
        .flags = 0,
        .reserved = [_]u64{0} ** 3,
    };
}

fn completionInput(
    action: *const protocol.ActionOutput,
    epoch: u64,
    outcome: protocol.CompletionOutcome,
    utc: i64,
    monotonic: u64,
) protocol.CompletionInput {
    return .{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.CompletionInput),
        .configuration_generation = action.configuration_generation,
        .completion_epoch = epoch,
        .operation_id = action.operation_id,
        .attempt_token = action.attempt_token,
        .outcome = @intFromEnum(outcome),
        .reserved_u32 = 0,
        .observed_utc_milliseconds = utc,
        .observed_monotonic_milliseconds = monotonic,
        .result_handle = zeroHandle(),
        .error_handle = zeroHandle(),
        .valid_mask = 0,
        .flags = 0,
        .reserved = [_]u64{0} ** 3,
    };
}

fn progressInput(
    action: *const protocol.ActionOutput,
    sequence: u64,
    utc: i64,
    monotonic: u64,
) protocol.ProgressInput {
    return .{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.ProgressInput),
        .configuration_generation = action.configuration_generation,
        .operation_id = action.operation_id,
        .attempt_token = action.attempt_token,
        .progress_sequence = sequence,
        .observed_utc_milliseconds = utc,
        .observed_monotonic_milliseconds = monotonic,
        .percent_milli = 0,
        .reserved_u32 = 0,
        .bytes_done = 0,
        .bytes_total = 0,
        .speed_bytes_per_second = 0,
        .stage_handle = zeroHandle(),
        .message_handle = zeroHandle(),
        .checkpoint_handle = zeroHandle(),
        .valid_mask = 0,
        .flags = 0,
        .reserved = [_]u64{0} ** 3,
    };
}

fn persistenceInput(epoch: u64) protocol.PersistenceInput {
    return .{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.PersistenceInput),
        .configuration_generation = 1,
        .operation_epoch = epoch,
        .flags = 0,
        .reserved = [_]u64{0} ** 4,
    };
}

fn persistenceImportInput(
    epoch: u64,
    utc: i64,
    monotonic: u64,
    clock_instance_id: protocol.Handle128,
) protocol.PersistenceImportInput {
    return .{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.PersistenceImportInput),
        .configuration_generation = 1,
        .operation_epoch = epoch,
        .observed_utc_milliseconds = utc,
        .observed_monotonic_milliseconds = monotonic,
        .clock_instance_id = clock_instance_id,
        .flags = 0,
        .reserved = [_]u64{0} ** 4,
    };
}

fn readInput(state_revision: u64, plan_epoch: u64) protocol.ReadInput {
    return .{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.ReadInput),
        .configuration_generation = 1,
        .state_revision = state_revision,
        .plan_epoch = plan_epoch,
        .flags = 0,
        .reserved = [_]u64{0} ** 3,
    };
}

fn readCurrentActions(
    session: *state.Session,
    plan_output: *const protocol.PlanOutput,
    actions: []protocol.ActionOutput,
) !void {
    var input = readInput(plan_output.state_revision, plan_output.plan_epoch);
    input.configuration_generation = plan_output.configuration_generation;
    try std.testing.expectEqual(ResultCode.ok, session.readActions(&input, actions));
}

fn zeroActions() [8]protocol.ActionOutput {
    return [_]protocol.ActionOutput{std.mem.zeroes(protocol.ActionOutput)} ** 8;
}

fn zeroOperations() [8]protocol.OperationOutput {
    return [_]protocol.OperationOutput{std.mem.zeroes(protocol.OperationOutput)} ** 8;
}

fn handle(value: u64) protocol.Handle128 {
    return .{ .high = 0, .low = value };
}

fn zeroHandle() protocol.Handle128 {
    return .{ .high = 0, .low = 0 };
}
