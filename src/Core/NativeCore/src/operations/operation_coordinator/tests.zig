const std = @import("std");
const protocol = @import("protocol.zig");
const state = @import("state.zig");
const ResultCode = @import("../../common/result_codes.zig").ResultCode;

fn growthConfig(maximum: u32) protocol.Config {
    var config = testConfig();
    config.maximum_operation_count = maximum;
    config.maximum_domain_count = maximum;
    config.maximum_action_count = maximum;
    config.maximum_read_count = maximum;
    config.operation_index_capacity = std.math.ceilPowerOfTwo(u32, maximum * 2) catch unreachable;
    config.domain_index_capacity = config.operation_index_capacity;
    config.maximum_persistence_byte_count = @sizeOf(protocol.PersistenceHeader) + @as(u64, maximum) * @sizeOf(protocol.PersistenceRecord);
    config.resident_byte_budget = 64 * 1024 * 1024;
    return config;
}

test "operation exact growth retains every live action record and lookup" {
    const bank_module = @import("bank.zig");
    var config = growthConfig(65);
    const growing = try state.Session.create(&config);
    defer growing.destroy();
    const full = try state.Session.create(&config);
    defer full.destroy();
    try full.ensureBankCapacity(&full.bank, 65, .operation_submit);
    try std.testing.expectEqual(@as(u32, 16), growing.workspaceCapacity().operation_capacity);
    var ordinal: u32 = 1;
    while (ordinal <= 65) : (ordinal += 1) {
        var input = submitInput(ordinal, ordinal + 1000, ordinal, 100, 100);
        var left = std.mem.zeroes(protocol.SubmitOutput);
        var right = std.mem.zeroes(protocol.SubmitOutput);
        try std.testing.expectEqual(ResultCode.ok, growing.submit(&input, &left));
        try std.testing.expectEqual(ResultCode.ok, full.submit(&input, &right));
        try std.testing.expectEqualDeep(left, right);
        if (ordinal == 1) {
            // Keep an unread, in-flight action across every subsequent growth.
            const left_plan = try runPlan(growing, 1, 101, 101, 8);
            const right_plan = try runPlan(full, 1, 101, 101, 8);
            try std.testing.expectEqualDeep(left_plan, right_plan);
        }
        var key: u32 = 1;
        while (key <= ordinal) : (key += 1) {
            const left_index = growing.bank.findOperation(handle(key)).?;
            const right_index = full.bank.findOperation(handle(key)).?;
            try std.testing.expectEqualDeep(full.bank.records[right_index], growing.bank.records[left_index]);
            try std.testing.expectEqual(left_index, growing.bank.findDomain(handle(key + 1000)).?);
        }
        try std.testing.expectEqualSlices(protocol.ActionOutput, full.bank.actions[0..full.bank.action_count], growing.bank.actions[0..growing.bank.action_count]);
    }
    const diagnostic = growing.workspaceCapacity();
    try std.testing.expectEqual(@as(u32, 65), diagnostic.operation_capacity);
    try std.testing.expectEqual(@as(u64, 3), diagnostic.growth.count);
    for ([_]u64{ 32, 64, 65 }, 0..) |new_capacity, index| {
        const event = diagnostic.growth.events[index];
        try std.testing.expectEqual(@as(u64, index + 1), event.sequence);
        try std.testing.expectEqual(@as(u32, 1), event.reason);
        try std.testing.expectEqual(@as(u32, 0), event.unit);
        try std.testing.expectEqual(new_capacity, event.new_capacity);
        try std.testing.expectEqual(if (index == 0) @as(u64, 16) else diagnostic.growth.events[index - 1].new_capacity, event.old_capacity);
    }
    var overflow = submitInput(66, 1066, 66, 100, 100);
    overflow.valid_mask = 0;
    overflow.domain_id = zeroHandle();
    var output = std.mem.zeroes(protocol.SubmitOutput);
    try std.testing.expectEqual(ResultCode.out_of_memory, growing.submit(&overflow, &output));
    try std.testing.expectEqual(@as(u32, 65), growing.bank.operation_count);
    try std.testing.expectEqual(@as(u64, 1), growing.bank.records[0].pending_action_id);
    _ = bank_module;
}

test "operation failed growth leaves every old allocation and live state untouched" {
    var fail_offset: usize = 0;
    while (fail_offset < 6) : (fail_offset += 1) {
        var config = growthConfig(64);
        const owner = try state.Session.create(&config);
        defer owner.destroy();
        var ordinal: u32 = 1;
        while (ordinal <= 16) : (ordinal += 1) {
            var input = submitInput(ordinal, ordinal + 1000, ordinal, 100, 100);
            var output = std.mem.zeroes(protocol.SubmitOutput);
            try std.testing.expectEqual(ResultCode.ok, owner.submit(&input, &output));
        }
        _ = try runPlan(owner, 1, 101, 101, 8);
        const old_allocator = owner.bank.allocator;
        var failing = std.testing.FailingAllocator.init(old_allocator, .{ .fail_index = fail_offset });
        owner.bank.allocator = failing.allocator();
        const old_records = owner.bank.records.ptr;
        const old_index = owner.bank.operation_index.ptr;
        const old_free = owner.bank.free_stack.ptr;
        const old_actions = owner.bank.actions.ptr;
        const before = owner.bank.records[0];
        const old_revision = owner.state_revision;
        const old_resident = owner.resident_byte_count;
        var input = submitInput(17, 1017, 17, 100, 100);
        var output = std.mem.zeroes(protocol.SubmitOutput);
        try std.testing.expectEqual(ResultCode.out_of_memory, owner.submit(&input, &output));
        try std.testing.expectEqual(@as(u64, 0), owner.workspaceCapacity().growth.count);
        owner.bank.allocator = old_allocator;
        try std.testing.expectEqual(old_records, owner.bank.records.ptr);
        try std.testing.expectEqual(old_index, owner.bank.operation_index.ptr);
        try std.testing.expectEqual(old_free, owner.bank.free_stack.ptr);
        try std.testing.expectEqual(old_actions, owner.bank.actions.ptr);
        try std.testing.expectEqualDeep(before, owner.bank.records[0]);
        try std.testing.expectEqual(old_revision, owner.state_revision);
        try std.testing.expectEqual(old_resident, owner.resident_byte_count);
        try std.testing.expectEqual(@as(usize, 0), failing.allocated_bytes - failing.freed_bytes);
        try std.testing.expectEqual(ResultCode.ok, owner.submit(&input, &output));
    }
}

test "operation bank bytes for content and configured machine shape" {
    var config = growthConfig(8192);
    config.maximum_action_count = 1024; // Production default action ceiling.
    const bank_module = @import("bank.zig");
    const page: u64 = @intCast(std.heap.pageSize());
    const initial = try bank_module.sizedConfig(&config, 16);
    const session_bytes = ((@sizeOf(state.Session) + page - 1) / page) * page;
    const before = session_bytes + 2 * try bank_module.committedByteCount(&config, page);
    const after = session_bytes + 2 * try bank_module.committedByteCount(&initial, page);
    try std.testing.expect(after < before);
    try std.testing.expectEqual(@as(u64, 9_146_368), before);
    try std.testing.expectEqual(@as(u64, 61_440), after);
    const owner = try state.Session.create(&config);
    defer owner.destroy();
    try owner.ensureBankCapacity(&owner.bank, 300, .operation_submit);
    try std.testing.expectEqual(@as(u64, 372_736), owner.resident_byte_count);
    std.debug.print("operation shape: record={d} index={d} session={d} production8192={d} idle16={d} stress300={d}\n", .{ @sizeOf(bank_module.Record), @sizeOf(bank_module.IndexSlot), @sizeOf(state.Session), before, after, owner.resident_byte_count });
}

test "operation decode grows staging without changing recovery semantics and allocation failure is atomic" {
    var config = growthConfig(64);
    const source = try state.Session.create(&config);
    defer source.destroy();
    var ordinal: u32 = 1;
    while (ordinal <= 33) : (ordinal += 1) {
        var input = submitInput(ordinal, ordinal + 1000, ordinal, 100, 100);
        var output = std.mem.zeroes(protocol.SubmitOutput);
        try std.testing.expectEqual(ResultCode.ok, source.submit(&input, &output));
    }
    _ = try runPlan(source, 1, 101, 101, 8);
    var header: protocol.PersistenceHeader = undefined;
    var records: [33]protocol.PersistenceRecord = undefined;
    const export_input = persistenceInput(1);
    try std.testing.expectEqual(ResultCode.ok, source.exportPersistence(&export_input, &header, &records));
    var fail_offset: usize = 0;
    while (fail_offset < 6) : (fail_offset += 1) {
        const restored = try state.Session.create(&config);
        defer restored.destroy();
        const old_allocator = restored.staging.allocator;
        var failing = std.testing.FailingAllocator.init(old_allocator, .{ .fail_index = fail_offset });
        restored.staging.allocator = failing.allocator();
        const old_bank = restored.bank.records.ptr;
        const old_staging = restored.staging.records.ptr;
        const old_bytes = restored.resident_byte_count;
        const import_input = persistenceImportInput(1, 102, 102, config.clock_instance_id);
        try std.testing.expectEqual(ResultCode.out_of_memory, restored.importPersistence(&import_input, &header, &records));
        try std.testing.expectEqual(@as(u64, 0), restored.workspaceCapacity().growth.count);
        restored.staging.allocator = old_allocator;
        try std.testing.expectEqual(old_bank, restored.bank.records.ptr);
        try std.testing.expectEqual(old_staging, restored.staging.records.ptr);
        try std.testing.expectEqual(old_bytes, restored.resident_byte_count);
        try std.testing.expectEqual(@as(u64, 1), restored.state_revision);
        try std.testing.expectEqual(@as(usize, 0), failing.allocated_bytes - failing.freed_bytes);
        try std.testing.expectEqual(ResultCode.ok, restored.importPersistence(&import_input, &header, &records));
        try std.testing.expectEqual(@as(u32, 64), restored.workspaceCapacity().operation_capacity);
        const event = restored.workspaceCapacity().growth.events[0];
        try std.testing.expectEqual(@as(u64, 1), event.sequence);
        try std.testing.expectEqual(@as(u32, 2), event.reason);
        try std.testing.expectEqual(@as(u64, 16), event.old_capacity);
        try std.testing.expectEqual(@as(u64, 64), event.new_capacity);
        for (records) |persisted| {
            const slot = restored.bank.findOperation(persisted.operation_id).?;
            const record = restored.bank.records[slot];
            try std.testing.expectEqual(protocol.OperationState.recovery_pending, record.state);
            try std.testing.expectEqual(persisted.state, record.recovery_origin_state);
            try std.testing.expectEqual(@as(u64, 0), record.pending_action_id);
            try std.testing.expectEqual(slot, restored.bank.findDomain(record.domain_id).?);
        }
    }
}

test "operation growth preserves retired slot reuse order and pending actions" {
    var config = growthConfig(64);
    config.maximum_recent_terminal_count = 0;
    const growing = try state.Session.create(&config);
    defer growing.destroy();
    const full = try state.Session.create(&config);
    defer full.destroy();
    try full.ensureBankCapacity(&full.bank, 32, .operation_submit);
    for ([_]*state.Session{ growing, full }) |owner| {
        var ordinal: u64 = 1;
        while (ordinal <= 16) : (ordinal += 1) {
            var input = submitInput(ordinal, ordinal + 1000, ordinal, 100, 100);
            var output = std.mem.zeroes(protocol.SubmitOutput);
            try std.testing.expectEqual(ResultCode.ok, owner.submit(&input, &output));
        }
        for ([_]u64{ 3, 8, 14 }, 0..) |retired, epoch| {
            var input = cancelInput(handle(retired), epoch + 1, 101, 101);
            var output = std.mem.zeroes(protocol.CancelOutput);
            try std.testing.expectEqual(ResultCode.ok, owner.cancel(&input, &output));
            try std.testing.expectEqual(@as(?u32, null), owner.bank.findOperation(handle(retired)));
            try std.testing.expectEqual(@as(?u32, null), owner.bank.findDomain(handle(retired + 1000)));
        }
        _ = try runPlan(owner, 1, 102, 102, 8);
    }
    try std.testing.expectEqual(@as(u32, 3), growing.bank.free_count);
    const old_free = [_]u32{
        growing.bank.free_stack[0],
        growing.bank.free_stack[1],
        growing.bank.free_stack[2],
    };
    const pending = growing.bank.records[0];
    try growing.ensureBankCapacity(&growing.bank, 17, .operation_submit);
    try std.testing.expectEqualSlices(u32, &old_free, growing.bank.free_stack[16..19]);
    try std.testing.expectEqualDeep(pending, growing.bank.records[0]);
    var ordinal: u64 = 17;
    while (ordinal <= 20) : (ordinal += 1) {
        var input = submitInput(ordinal, ordinal + 1000, ordinal, 103, 103);
        var left = std.mem.zeroes(protocol.SubmitOutput);
        var right = std.mem.zeroes(protocol.SubmitOutput);
        try std.testing.expectEqual(ResultCode.ok, growing.submit(&input, &left));
        try std.testing.expectEqual(ResultCode.ok, full.submit(&input, &right));
        try std.testing.expectEqualDeep(left, right);
        const slot = growing.bank.findOperation(handle(ordinal)).?;
        try std.testing.expectEqual(full.bank.findOperation(handle(ordinal)).?, slot);
        const expected_slot = if (ordinal < 20) old_free[@intCast(19 - ordinal)] else 16;
        try std.testing.expectEqual(expected_slot, slot);
        try std.testing.expectEqual(slot, growing.bank.findDomain(handle(ordinal + 1000)).?);
    }
    try std.testing.expectEqualDeep(full.bank.records, growing.bank.records);
    try std.testing.expectEqualSlices(protocol.ActionOutput, full.bank.actions[0..full.bank.action_count], growing.bank.actions[0..growing.bank.action_count]);
    var final_pending = pending;
    final_pending.state_revision = growing.state_revision;
    try std.testing.expectEqualDeep(final_pending, growing.bank.records[0]);
}

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

test "wall clock jumps do not reject operation plans feedback progress or completion" {
    for ([_]i64{ -120_000, 120_000 }) |jump| {
        var config = testConfig();
        const session = try state.Session.create(&config);
        defer session.destroy();
        var output = std.mem.zeroes(protocol.SubmitOutput);
        var submit = submitInput(1, 0, 1, 1_000_000, 100);
        submit.valid_mask = 0;
        submit.domain_id = zeroHandle();
        submit.observed_utc_milliseconds += jump;
        try std.testing.expectEqual(ResultCode.ok, session.submit(&submit, &output));

        const plan = try runPlan(session, 1, 1_000_000 + 2 * jump, 101, 8);
        try std.testing.expectEqual(@as(u32, 1), plan.action_count);
        var actions = zeroActions();
        try readCurrentActions(session, &plan, &actions);
        var started = feedbackInput(&actions[0], 1, .started, 1_000_000 + 3 * jump, 102);
        try std.testing.expectEqual(ResultCode.ok, session.feedback(&started));
        var progress = progressInput(&actions[0], 1, 1_000_000 + 4 * jump, 103);
        progress.valid_mask = protocol.ProgressValid.percent_milli;
        progress.percent_milli = 50_000;
        try std.testing.expectEqual(ResultCode.ok, session.reportProgress(&progress));
        var completed = completionInput(&actions[0], 1, .succeeded, 1_000_000 + 5 * jump, 102);
        try std.testing.expectEqual(ResultCode.stale_frame, session.complete(&completed));
        completed.observed_monotonic_milliseconds = 104;
        try std.testing.expectEqual(ResultCode.ok, session.complete(&completed));

        var snapshot = std.mem.zeroes(protocol.SnapshotOutput);
        try std.testing.expectEqual(ResultCode.ok, session.snapshot(&snapshot));
        var operations = zeroOperations();
        try std.testing.expectEqual(ResultCode.ok, session.readOperations(&readInput(snapshot.state_revision, 0), &operations));
        try std.testing.expectEqual(@intFromEnum(protocol.OperationState.succeeded), operations[0].state);
        try std.testing.expectEqual(completed.observed_utc_milliseconds, operations[0].updated_utc_milliseconds);

        submit = submitInput(2, 0, 2, 1_000_000, 105);
        submit.valid_mask = 0;
        submit.domain_id = zeroHandle();
        output = std.mem.zeroes(protocol.SubmitOutput);
        try std.testing.expectEqual(ResultCode.ok, session.submit(&submit, &output));
        var cancel = cancelInput(submit.operation_id, 1, 1_000_000 + jump, 106);
        var cancel_output = std.mem.zeroes(protocol.CancelOutput);
        try std.testing.expectEqual(ResultCode.ok, session.cancel(&cancel, &cancel_output));
        _ = try runPlan(session, 2, 1_000_000 + 2 * jump, 107, 8);
    }
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

test "owned completion cancellation renews grace and retains its slot until completion" {
    var config = testConfig();
    config.maximum_global_running_count = 3;
    var session = try state.Session.create(&config);
    defer session.destroy();

    var submit_output = std.mem.zeroes(protocol.SubmitOutput);
    var owned_submit = submitInput(1, 0, 1, 100, 100);
    owned_submit.valid_mask = 0;
    owned_submit.domain_id = zeroHandle();
    owned_submit.cancel_grace_milliseconds = 5;
    owned_submit.flags = protocol.SubmitFlags.retain_owned_completion;
    try std.testing.expectEqual(
        ResultCode.ok,
        session.submit(&owned_submit, &submit_output),
    );

    var invalid_flags = submitInput(9, 9, 9, 100, 100);
    invalid_flags.flags = 1 << 1;
    try std.testing.expectEqual(
        ResultCode.abi_mismatch,
        session.submit(&invalid_flags, &submit_output),
    );
    var mismatched_duplicate = owned_submit;
    mismatched_duplicate.submit_epoch = 9;
    mismatched_duplicate.flags = 0;
    try std.testing.expectEqual(
        ResultCode.invalid_argument,
        session.submit(&mismatched_duplicate, &submit_output),
    );

    const owned_start_plan = try runPlan(session, 1, 101, 101, 8);
    var actions = zeroActions();
    try readCurrentActions(session, &owned_start_plan, &actions);
    const owned_start_action = actions[0];
    try std.testing.expectEqual(
        @intFromEnum(protocol.ActionKind.start),
        owned_start_action.kind,
    );
    const started = feedbackInput(&owned_start_action, 1, .started, 102, 102);
    try std.testing.expectEqual(ResultCode.ok, session.feedback(&started));

    const unrelated_a = submitInput(2, 2, 2, 103, 103);
    const unrelated_b = submitInput(3, 3, 3, 103, 103);
    try std.testing.expectEqual(
        ResultCode.ok,
        session.submit(&unrelated_a, &submit_output),
    );
    try std.testing.expectEqual(
        ResultCode.ok,
        session.submit(&unrelated_b, &submit_output),
    );
    const unrelated_plan = try runPlan(session, 2, 104, 104, 8);
    try std.testing.expectEqual(@as(u32, 2), unrelated_plan.action_count);
    actions = zeroActions();
    try readCurrentActions(session, &unrelated_plan, &actions);
    for (actions[0..2], 0..) |*action, index| {
        const feedback = feedbackInput(
            action,
            @as(u64, @intCast(index)) + 2,
            .started,
            105 + @as(i64, @intCast(index)),
            105 + @as(u64, @intCast(index)),
        );
        try std.testing.expectEqual(ResultCode.ok, session.feedback(&feedback));
    }

    var cancel_output = std.mem.zeroes(protocol.CancelOutput);
    const cancel = cancelInput(owned_submit.operation_id, 1, 107, 107);
    try std.testing.expectEqual(ResultCode.ok, session.cancel(&cancel, &cancel_output));
    const cancel_plan = try runPlan(session, 3, 108, 108, 8);
    try std.testing.expectEqual(@as(u32, 1), cancel_plan.action_count);
    actions = zeroActions();
    try readCurrentActions(session, &cancel_plan, &actions);
    const cancel_action = actions[0];
    try std.testing.expectEqual(
        @intFromEnum(protocol.ActionKind.cancel),
        cancel_action.kind,
    );
    try std.testing.expect(
        (cancel_action.flags & protocol.ActionFlags.retain_owned_completion) != 0,
    );
    const owned_record_index = session.bank.findOperation(owned_submit.operation_id).?;
    const original_cancel_action_id = session.bank.records[
        owned_record_index
    ].pending_action_id;
    const next_action_id_after_cancel = session.next_action_id;

    var queued_unrelated = submitInput(4, 4, 4, 109, 109);
    try std.testing.expectEqual(
        ResultCode.ok,
        session.submit(&queued_unrelated, &submit_output),
    );
    const before_grace = try runPlan(session, 4, 110, 110, 8);
    try std.testing.expectEqual(@as(u32, 0), before_grace.action_count);

    const first_renewal = try runPlan(session, 5, 113, 113, 8);
    try std.testing.expectEqual(@as(u32, 0), first_renewal.action_count);
    try std.testing.expectEqual(@as(u32, 3), first_renewal.running_operation_count);
    try std.testing.expectEqual(@as(u64, 118), first_renewal.next_wake_monotonic_milliseconds);
    try std.testing.expectEqual(
        original_cancel_action_id,
        session.bank.records[owned_record_index].pending_action_id,
    );
    try std.testing.expectEqual(next_action_id_after_cancel, session.next_action_id);

    const second_renewal = try runPlan(session, 6, 118, 118, 8);
    try std.testing.expectEqual(@as(u32, 0), second_renewal.action_count);
    try std.testing.expectEqual(@as(u32, 3), second_renewal.running_operation_count);
    try std.testing.expectEqual(@as(u32, 0), second_renewal.terminal_operation_count);
    try std.testing.expectEqual(@as(u64, 123), second_renewal.next_wake_monotonic_milliseconds);
    try std.testing.expectEqual(
        original_cancel_action_id,
        session.bank.records[owned_record_index].pending_action_id,
    );
    try std.testing.expectEqual(next_action_id_after_cancel, session.next_action_id);

    var header = std.mem.zeroes(protocol.PersistenceHeader);
    var records = zeroOperations();
    var export_input = persistenceInput(1);
    try std.testing.expectEqual(
        ResultCode.ok,
        session.exportPersistence(&export_input, &header, &records),
    );
    try std.testing.expect(
        (records[0].flags
            & protocol.OperationFlags.retain_owned_completion) != 0,
    );

    var restored = try state.Session.create(&config);
    defer restored.destroy();
    var import_input = persistenceImportInput(
        1,
        119,
        119,
        config.clock_instance_id,
    );
    try std.testing.expectEqual(
        ResultCode.ok,
        restored.importPersistence(
            &import_input,
            &header,
            records[0..@intCast(header.operation_count)],
        ),
    );
    var restored_snapshot = std.mem.zeroes(protocol.SnapshotOutput);
    try std.testing.expectEqual(ResultCode.ok, restored.snapshot(&restored_snapshot));
    var restored_records = zeroOperations();
    try std.testing.expectEqual(
        ResultCode.ok,
        restored.readOperations(
            &readInput(restored_snapshot.state_revision, 0),
            &restored_records,
        ),
    );
    try std.testing.expect(
        (restored_records[0].flags
            & protocol.OperationFlags.retain_owned_completion) != 0,
    );

    const completed = completionInput(
        &owned_start_action,
        1,
        .succeeded,
        119,
        119,
    );
    try std.testing.expectEqual(ResultCode.ok, session.complete(&completed));
    const released_slot = try runPlan(session, 7, 120, 120, 8);
    try std.testing.expectEqual(@as(u32, 1), released_slot.action_count);
    actions = zeroActions();
    try readCurrentActions(session, &released_slot, &actions);
    try std.testing.expectEqual(
        queued_unrelated.operation_id.high,
        actions[0].operation_id.high,
    );
    try std.testing.expectEqual(
        queued_unrelated.operation_id.low,
        actions[0].operation_id.low,
    );
}

test "owned completion renews an execution-timeout cancel until actual completion" {
    var config = testConfig();
    var session = try state.Session.create(&config);
    defer session.destroy();

    var submit_output = std.mem.zeroes(protocol.SubmitOutput);
    var submit = submitInput(1, 0, 1, 100, 100);
    submit.valid_mask = 0;
    submit.domain_id = zeroHandle();
    submit.execution_timeout_milliseconds = 5;
    submit.cancel_grace_milliseconds = 5;
    submit.flags = protocol.SubmitFlags.retain_owned_completion;
    try std.testing.expectEqual(ResultCode.ok, session.submit(&submit, &submit_output));

    const start_plan = try runPlan(session, 1, 101, 101, 8);
    var actions = zeroActions();
    try readCurrentActions(session, &start_plan, &actions);
    const start_action = actions[0];
    const started = feedbackInput(&start_action, 1, .started, 102, 102);
    try std.testing.expectEqual(ResultCode.ok, session.feedback(&started));

    const timeout_cancel = try runPlan(session, 2, 107, 107, 8);
    try std.testing.expectEqual(@as(u32, 1), timeout_cancel.action_count);
    actions = zeroActions();
    try readCurrentActions(session, &timeout_cancel, &actions);
    const cancel_action = actions[0];
    try std.testing.expectEqual(
        @intFromEnum(protocol.ActionKind.cancel),
        cancel_action.kind,
    );
    try std.testing.expect(
        (cancel_action.flags & protocol.ActionFlags.execution_timeout) != 0,
    );
    try std.testing.expect(
        (cancel_action.flags & protocol.ActionFlags.retain_owned_completion) != 0,
    );
    const pending_action_id = session.bank.records[0].pending_action_id;

    const first_renewal = try runPlan(session, 3, 112, 112, 8);
    try std.testing.expectEqual(@as(u32, 0), first_renewal.action_count);
    try std.testing.expectEqual(@as(u32, 1), first_renewal.running_operation_count);
    try std.testing.expectEqual(@as(u32, 0), first_renewal.terminal_operation_count);
    try std.testing.expectEqual(@as(u64, 117), first_renewal.next_wake_monotonic_milliseconds);
    try std.testing.expectEqual(pending_action_id, session.bank.records[0].pending_action_id);

    const second_renewal = try runPlan(session, 4, 117, 117, 8);
    try std.testing.expectEqual(@as(u32, 0), second_renewal.action_count);
    try std.testing.expectEqual(@as(u32, 1), second_renewal.running_operation_count);
    try std.testing.expectEqual(@as(u32, 0), second_renewal.terminal_operation_count);
    try std.testing.expectEqual(@as(u64, 122), second_renewal.next_wake_monotonic_milliseconds);
    try std.testing.expectEqual(pending_action_id, session.bank.records[0].pending_action_id);

    const actual_result = completionInput(
        &start_action,
        1,
        .state_uncertain,
        118,
        118,
    );
    try std.testing.expectEqual(ResultCode.ok, session.complete(&actual_result));
    const completed = try runPlan(session, 5, 119, 119, 8);
    try std.testing.expectEqual(@as(u32, 1), completed.terminal_operation_count);
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

test "empty persistence has no observed clock baseline until its first import" {
    var config = testConfig();
    var source = try state.Session.create(&config);
    defer source.destroy();
    var header = std.mem.zeroes(protocol.PersistenceHeader);
    var records = zeroOperations();
    var export_input = persistenceInput(1);
    try std.testing.expectEqual(
        ResultCode.ok,
        source.exportPersistence(&export_input, &header, &records),
    );
    try std.testing.expectEqual(@as(u32, 0), header.operation_count);
    try std.testing.expectEqual(@as(i64, 0), header.last_observed_utc_milliseconds);
    try std.testing.expectEqual(@as(u64, 0), header.last_observed_monotonic_milliseconds);

    var restored = try state.Session.create(&config);
    defer restored.destroy();
    var input = persistenceImportInput(1, 1_700_000_000_000, 100_000, config.clock_instance_id);
    try std.testing.expectEqual(
        ResultCode.ok,
        restored.importPersistence(&input, &header, records[0..0]),
    );
    var snapshot = std.mem.zeroes(protocol.SnapshotOutput);
    try std.testing.expectEqual(ResultCode.ok, restored.snapshot(&snapshot));
    try std.testing.expectEqual(@as(u32, 0), snapshot.operation_count);
    try std.testing.expectEqual(input.observed_utc_milliseconds, snapshot.last_observed_utc_milliseconds);
    try std.testing.expectEqual(input.observed_monotonic_milliseconds, snapshot.last_observed_monotonic_milliseconds);

    export_input.operation_epoch = 2;
    try std.testing.expectEqual(
        ResultCode.ok,
        restored.exportPersistence(&export_input, &header, &records),
    );
    input.operation_epoch = 3;
    input.observed_utc_milliseconds += @intCast(config.maximum_future_skew_milliseconds + 2);
    input.observed_monotonic_milliseconds += 1;
    try std.testing.expectEqual(
        ResultCode.ok,
        restored.importPersistence(&input, &header, records[0..0]),
    );
    try std.testing.expectEqual(ResultCode.ok, restored.snapshot(&snapshot));
    try std.testing.expectEqual(input.observed_utc_milliseconds, snapshot.last_observed_utc_milliseconds);
    try std.testing.expectEqual(input.observed_monotonic_milliseconds, snapshot.last_observed_monotonic_milliseconds);

    // A wall-clock rollback is accepted too; the monotonic clock still orders imports.
    input.operation_epoch = 4;
    input.observed_utc_milliseconds = header.last_observed_utc_milliseconds - 60_000;
    input.observed_monotonic_milliseconds += 1;
    try std.testing.expectEqual(
        ResultCode.ok,
        restored.importPersistence(&input, &header, records[0..0]),
    );
    input.operation_epoch = 5;
    input.observed_monotonic_milliseconds = header.last_observed_monotonic_milliseconds - 1;
    try std.testing.expectEqual(
        ResultCode.stale_frame,
        restored.importPersistence(&input, &header, records[0..0]),
    );
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

test "retiring a terminal record preserves the newer same-domain owner through persistence" {
    var config = testConfig();
    var source = try state.Session.create(&config);
    defer source.destroy();

    var output = std.mem.zeroes(protocol.SubmitOutput);
    var terminal_a = submitInput(1, 9, 1, 100, 100);
    terminal_a.terminal_retention_milliseconds = 50;
    try std.testing.expectEqual(ResultCode.ok, source.submit(&terminal_a, &output));
    const plan_a = try runPlan(source, 1, 101, 101, 8);
    var actions = zeroActions();
    try readCurrentActions(source, &plan_a, &actions);
    var started = feedbackInput(&actions[0], 1, .started, 102, 102);
    try std.testing.expectEqual(ResultCode.ok, source.feedback(&started));
    var completed = completionInput(&actions[0], 1, .succeeded, 103, 103);
    try std.testing.expectEqual(ResultCode.ok, source.complete(&completed));

    output = std.mem.zeroes(protocol.SubmitOutput);
    var active_b = submitInput(2, 9, 2, 104, 104);
    active_b.execution_timeout_milliseconds = 1_000_000;
    try std.testing.expectEqual(ResultCode.ok, source.submit(&active_b, &output));
    try std.testing.expectEqual(protocol.SubmitOutputFlags.inserted, output.flags);
    const plan_b = try runPlan(source, 2, 105, 105, 8);
    actions = zeroActions();
    try readCurrentActions(source, &plan_b, &actions);
    started = feedbackInput(&actions[0], 2, .started, 106, 106);
    try std.testing.expectEqual(ResultCode.ok, source.feedback(&started));

    output = std.mem.zeroes(protocol.SubmitOutput);
    var duplicate_before_retirement = submitInput(30, 9, 3, 107, 107);
    try std.testing.expectEqual(
        ResultCode.ok,
        source.submit(&duplicate_before_retirement, &output),
    );
    try std.testing.expectEqual(
        protocol.SubmitOutputFlags.active_domain_duplicate,
        output.flags,
    );
    try std.testing.expect(
        protocol.equalHandle(active_b.operation_id, output.operation_id),
    );

    const prune_plan = try runPlan(source, 3, 160, 160, 8);
    try std.testing.expectEqual(@as(u32, 1), prune_plan.running_operation_count);
    try std.testing.expectEqual(@as(u32, 0), prune_plan.terminal_operation_count);
    var snapshot = std.mem.zeroes(protocol.SnapshotOutput);
    try std.testing.expectEqual(ResultCode.ok, source.snapshot(&snapshot));
    try std.testing.expectEqual(@as(u32, 1), snapshot.operation_count);

    output = std.mem.zeroes(protocol.SubmitOutput);
    var duplicate_after_retirement = submitInput(3, 9, 4, 161, 161);
    try std.testing.expectEqual(
        ResultCode.ok,
        source.submit(&duplicate_after_retirement, &output),
    );
    try std.testing.expectEqual(
        protocol.SubmitOutputFlags.active_domain_duplicate,
        output.flags,
    );
    try std.testing.expect(
        protocol.equalHandle(active_b.operation_id, output.operation_id),
    );

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
    var import_input = persistenceImportInput(
        1,
        162,
        162,
        config.clock_instance_id,
    );
    try std.testing.expectEqual(
        ResultCode.ok,
        restored.importPersistence(
            &import_input,
            &header,
            records[0..@intCast(header.operation_count)],
        ),
    );
    snapshot = std.mem.zeroes(protocol.SnapshotOutput);
    try std.testing.expectEqual(ResultCode.ok, restored.snapshot(&snapshot));
    try std.testing.expectEqual(@as(u32, 1), snapshot.operation_count);

    output = std.mem.zeroes(protocol.SubmitOutput);
    var duplicate_after_import = submitInput(4, 9, 5, 163, 163);
    try std.testing.expectEqual(
        ResultCode.ok,
        restored.submit(&duplicate_after_import, &output),
    );
    try std.testing.expectEqual(
        protocol.SubmitOutputFlags.active_domain_duplicate,
        output.flags,
    );
    try std.testing.expect(
        protocol.equalHandle(active_b.operation_id, output.operation_id),
    );
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
        .subject_handle = zeroHandle(),
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
        .stage_fact_handle = zeroHandle(),
        .detail_fact_handle = zeroHandle(),
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
