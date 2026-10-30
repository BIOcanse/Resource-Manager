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

test "source 14 sixty facts reserve observations and second round performs no allocation" {
    // Installed initial limit and current development initial limit. One thousand
    // process instances over the seven-day retention already occupy 2000 rows.
    for ([_]u32{ 2048, 8192 }) |limit| {
        var config = testConfig();
        config.maximum_observation_count = limit;
        config.observation_index_capacity = limit * 2;
        config.maximum_persistence_operation_count = 32768;
        config.planned_persistence_index_capacity = 65536;
        config.default_retention_milliseconds = 604800000;
        var rules = [_]protocol.RuleInput{ rule(), rule(), rule(), rule() };
        for (&rules, 0..) |*row, index| {
            row.source_handle = if (index < 2) 15 else 14;
            row.coverage_scope_handle = row.source_handle;
            row.rule_handle += index;
            row.predicate_group_handle += index;
        }
        const session = try createReadySession(&config, &rules);
        defer session.destroy();
        const process_facts = try std.testing.allocator.alloc(protocol.FactInput, limit - 48);
        defer std.testing.allocator.free(process_facts);
        for (process_facts, 0..) |*row, index| {
            row.* = factFor(rules[index % 2].rule_handle, rule_generation, 1, 20);
            row.target_handle = index / 2 + 1;
        }
        var process_input = sourceSnapshot(3, 3, 1, 100, .complete, @intCast(process_facts.len));
        process_input.source_handle = 15;
        process_input.coverage_scope_handle = 15;
        try expectCode(.ok, session_module.observe(session, &process_input, process_facts.ptr, @intCast(process_facts.len)));
        var facts: [60]protocol.FactInput = undefined;
        for (&facts, 0..) |*row, index| {
            row.* = factFor(rules[2 + index % 2].rule_handle, rule_generation, 1, 20);
            row.target_handle = index / 2 + 1;
        }
        var input = sourceSnapshot(4, 4, 1, 110, .complete, facts.len);
        input.source_handle = 14;
        input.coverage_scope_handle = 14;
        try expectCode(.buffer_too_small, session_module.observe(session, &input, &facts, facts.len));
        try std.testing.expectEqual(limit - 48, @import("growth.zig").occupied(session.observations));
        const epoch = session.last_operation_epoch;
        var required: u32 = 0;
        try expectCode(.ok, session_module.queryObservationCount(session, &input, &facts, facts.len, &required));
        try std.testing.expectEqual(limit + 12, required);
        try std.testing.expectEqual(epoch, session.last_operation_epoch);
        try expectCode(.ok, session.reserveObservations(required));
        // Reserving observations does not register a new source. Its first
        // successful snapshot also grows the shared arena's source columns.
        try std.testing.expectEqual(@as(usize, 1), session.sources.len);
        var replay_shape = session.allocationShape();
        replay_shape.maximum_source_count = 2;
        replay_shape.source_index_capacity = 4;
        try expectCode(.ok, session_module.observe(session, &input, &facts, facts.len));
        try std.testing.expectEqual(@as(usize, 2), session.sources.len);
        try std.testing.expectEqual(@as(usize, limit + 12), session.observations.len);
        try std.testing.expectEqual(try state.requiredResidentBytes(&replay_shape), session.resident_byte_count);
        const arena = session.storage.ptr;
        const resident = session.resident_byte_count;
        // Deny every allocation on the second round: pointer equality alone
        // would miss an allocation that happened to reuse the same address.
        var failing = std.testing.FailingAllocator.init(std.heap.page_allocator, .{ .fail_index = 0 });
        session.allocator = failing.allocator();
        defer session.allocator = std.heap.page_allocator;
        for (&facts) |*row| row.fact_sequence += 1;
        input.operation_epoch += 1;
        input.command_monotonic_milliseconds += 1;
        input.source_snapshot_epoch += 1;
        try expectCode(.ok, session_module.observe(session, &input, &facts, facts.len));
        try std.testing.expectEqual(arena, session.storage.ptr);
        try std.testing.expectEqual(resident, session.resident_byte_count);
        try std.testing.expectEqual(@as(usize, 0), failing.alloc_index);
        try std.testing.expect(!failing.has_induced_failure);
    }
}

test "observation reserve allocation failure preserves limits and arena" {
    var fixture = try Fixture.create();
    defer fixture.destroy();
    const arena = fixture.session.storage.ptr;
    const config = fixture.session.config;
    var failing = std.testing.FailingAllocator.init(std.heap.page_allocator, .{ .fail_index = 0 });
    fixture.session.allocator = failing.allocator();
    defer fixture.session.allocator = std.heap.page_allocator;
    try expectCode(.out_of_memory, fixture.session.reserveObservations(5));
    try std.testing.expectEqualDeep(config, fixture.session.config);
    try std.testing.expectEqual(arena, fixture.session.storage.ptr);
}

test "steady observe and plan run maintenance once without preview or replacement" {
    const report_rules = @import("rules.zig");
    const report_planner = @import("planner.zig");
    var fixture = try Fixture.create();
    defer fixture.destroy();
    try fixture.observeComplete(3, 3, 1, 100, &.{fact(1, 60)});
    try fixture.observeComplete(4, 4, 2, 110, &.{fact(2, 70)});
    // Establish exactly the high water the next Plan needs, before denying all
    // allocations. Neither steady operation may allocate or copy the arena.
    try expectCode(.ok, @import("growth.zig").persistencePlan(fixture.session, 4));
    const original = fixture.session.storage.ptr;
    var failing = std.testing.FailingAllocator.init(std.heap.page_allocator, .{ .fail_index = 0 });
    fixture.session.allocator = failing.allocator();
    defer fixture.session.allocator = std.heap.page_allocator;
    report_rules.test_maintenance_count = 0;
    report_rules.test_observe_preview_count = 0;
    try fixture.observeComplete(5, 5, 3, 120, &.{fact(3, 80)});
    try std.testing.expectEqual(@as(u64, 1), report_rules.test_maintenance_count);
    try std.testing.expectEqual(@as(u64, 0), report_rules.test_observe_preview_count);
    try std.testing.expectEqual(original, fixture.session.storage.ptr);
    try std.testing.expectEqual(@as(usize, 0), failing.alloc_index);
    report_rules.test_maintenance_count = 0;
    report_planner.test_plan_preview_count = 0;
    var reports: [4]protocol.ReportOutput = undefined;
    var operations: [32]protocol.PersistenceOperation = undefined;
    var output: protocol.PlanOutput = undefined;
    var input = planInput(6, 6, 1, 130, reports.len, operations.len);
    var report_count: u32 = 0;
    var operation_count: u32 = 0;
    try expectCode(.ok, session_module.queryPlanSize(fixture.session, &input, &report_count, &operation_count));
    try std.testing.expectEqual(@as(u64, 1), report_rules.test_maintenance_count);
    try std.testing.expectEqual(@as(u32, 1), report_count);
    try std.testing.expectEqual(@as(u32, 4), operation_count);
    try std.testing.expectEqual(original, fixture.session.storage.ptr);
    try std.testing.expectEqual(@as(usize, 0), failing.alloc_index);
    // The product's sizing query needs its rollback preview for exact output
    // counts. The following Plan must not preview that work a second time.
    report_rules.test_maintenance_count = 0;
    try expectCode(.ok, session_module.plan(fixture.session, &input, &reports, reports.len, &operations, operations.len, &output));
    try std.testing.expectEqual(@as(u64, 1), report_rules.test_maintenance_count);
    try std.testing.expectEqual(@as(u64, 0), report_planner.test_plan_preview_count);
    try std.testing.expectEqual(original, fixture.session.storage.ptr);
    try std.testing.expectEqual(@as(usize, 0), failing.alloc_index);
    try std.testing.expect(!failing.has_induced_failure);
    try std.testing.expectEqual(@as(u32, 1), output.report_count);
    try std.testing.expectEqual(@as(u32, 4), output.persistence_operation_count);
}

test "default history footprint and false facts allocate no potential reports" {
    // Counts/indexes from Configuration/HostManager/default.json host_recreate.
    var config = testConfig();
    config.maximum_source_count = 16;
    config.maximum_rule_count = 64;
    config.maximum_observation_count = 2048;
    config.maximum_report_count = 1024;
    config.maximum_trust_count = 2048;
    config.maximum_bucket_count = 21632;
    config.maximum_persistence_operation_count = 32768;
    config.maximum_report_output_count = 1024;
    config.maximum_rolling_observation_count = 128;
    config.source_index_capacity = 32;
    config.rule_index_capacity = 128;
    config.observation_index_capacity = 4096;
    config.report_index_capacity = 2048;
    config.trust_index_capacity = 4096;
    config.bucket_index_capacity = 65536;
    config.planned_persistence_index_capacity = 65536;
    const configured_bytes = try state.requiredResidentBytes(&config);
    const initial = state.initialShape(&config);
    const initial_bytes = try state.requiredResidentBytes(&initial);
    var default_rules = initial;
    default_rules.maximum_rule_count = 16; // Nine default rules, doubled from one.
    default_rules.rule_index_capacity = 32;
    const default_rules_bytes = try state.requiredResidentBytes(&default_rules);
    try std.testing.expectEqual(@as(u64, 10609968), configured_bytes);
    try std.testing.expectEqual(@as(u64, 3648), initial_bytes);
    try std.testing.expectEqual(@as(u64, 8928), default_rules_bytes);

    const rule_row = rule();
    const session = try createReadySession(&config, &.{rule_row});
    defer session.destroy();
    var facts: [300]protocol.FactInput = undefined;
    for (&facts, 0..) |*value, index| value.* = factForTarget(1, index + 100, 0);
    try observeRows(session, sourceSnapshot(3, 3, 1, 100, .complete, facts.len), &facts);
    try std.testing.expectEqual(@as(u32, 0), @import("growth.zig").occupied(session.reports));
    try std.testing.expectEqual(@as(usize, 1), session.reports.len);
    var report_count: u32 = 0;
    var operation_count: u32 = 0;
    var input = planInput(4, 4, 1, 110, 1024, 32768);
    try expectCode(.ok, session_module.queryPlanSize(session, &input, &report_count, &operation_count));
    try std.testing.expectEqual(@as(u32, 0), report_count);
    try std.testing.expectEqual(@as(u32, 302), operation_count);
    try std.testing.expectEqual(@as(usize, 1), session.reports.len);
    const false_bytes = session.resident_byte_count;
    for (&facts) |*value| {
        value.fact_sequence = 2;
        value.current_value = 60;
    }
    try observeRows(session, sourceSnapshot(4, 4, 2, 110, .complete, facts.len), &facts);
    try std.testing.expectEqual(@as(usize, 1), session.reports.len);
    for (&facts) |*value| value.fact_sequence = 3;
    try observeRows(session, sourceSnapshot(5, 5, 3, 120, .complete, facts.len), &facts);
    try std.testing.expectEqual(@as(u32, 300), @import("growth.zig").occupied(session.reports));
    try std.testing.expectEqual(@as(usize, 512), session.reports.len);
    var reports: [300]protocol.ReportOutput = undefined;
    var operations: [602]protocol.PersistenceOperation = undefined;
    var output: protocol.PlanOutput = undefined;
    input = planInput(6, 6, 1, 130, reports.len, operations.len);
    try expectCode(.ok, session_module.plan(session, &input, &reports, reports.len, &operations, operations.len, &output));
    try std.testing.expectEqual(@as(u32, 602), output.persistence_operation_count);
    try std.testing.expectEqual(@as(usize, 1024), session.planned_persistence.len);
    std.debug.print("report bytes configured={d} initial={d} default_9_rules={d} false_300={d} active_300_planned_602={d}\n", .{ configured_bytes, initial_bytes, default_rules_bytes, false_bytes, session.resident_byte_count });
}

test "content sized arena grows before observe and allocator failure leaves old state byte exact" {
    for (0..2) |failure_index| {
        var fixture = try Fixture.create();
        defer fixture.destroy();
        try fixture.observeComplete(3, 3, 1, 100, &.{fact(1, 60)});
        const old_storage = fixture.session.storage.ptr;
        const old_bytes = try std.testing.allocator.dupe(u8, std.mem.sliceAsBytes(fixture.session.storage));
        defer std.testing.allocator.free(old_bytes);
        const revision = fixture.session.state_revision;
        const mutation = fixture.session.next_mutation_version;
        const epoch = fixture.session.last_operation_epoch;
        var failing = std.testing.FailingAllocator.init(std.heap.page_allocator, .{ .fail_index = failure_index });
        fixture.session.allocator = failing.allocator();
        const facts = [_]protocol.FactInput{ factForTarget(2, 41, 70), factForTarget(1, 42, 80) };
        var input = sourceSnapshot(4, 4, 2, 110, .complete, facts.len);
        const result = session_module.observe(fixture.session, &input, &facts, facts.len);
        fixture.session.allocator = std.heap.page_allocator;
        try expectCode(.out_of_memory, result);
        try std.testing.expectEqual(old_storage, fixture.session.storage.ptr);
        try std.testing.expectEqualSlices(u8, old_bytes, std.mem.sliceAsBytes(fixture.session.storage));
        try std.testing.expectEqual(revision, fixture.session.state_revision);
        try std.testing.expectEqual(mutation, fixture.session.next_mutation_version);
        try std.testing.expectEqual(epoch, fixture.session.last_operation_epoch);
        try std.testing.expectEqual(failing.allocated_bytes, failing.freed_bytes);
        // The same command remains valid and explicitly succeeds after memory is available.
        try fixture.observeComplete(4, 4, 2, 110, &facts);
        try std.testing.expectEqual(@as(usize, 2), fixture.session.observations.len);
        try std.testing.expectEqual(@as(u64, 2), fixture.session.observations[fixture.session.findObservation(source_handle, coverage_scope_handle, rule_handle, 41).?].sample_count);
    }
}

test "exact report growth failure after preview restores the entire live observation state" {
    var fixture = try Fixture.create();
    defer fixture.destroy();
    const first = [_]protocol.FactInput{ factForTarget(1, 41, 60), factForTarget(1, 42, 70), factForTarget(1, 43, 80) };
    try fixture.observeComplete(3, 3, 1, 100, &first);
    try std.testing.expectEqual(@as(usize, 1), fixture.session.reports.len);
    const observations = try std.testing.allocator.dupe(state.ObservationSlot, fixture.session.observations);
    defer std.testing.allocator.free(observations);
    const sources = try std.testing.allocator.dupe(state.SourceSlot, fixture.session.sources);
    defer std.testing.allocator.free(sources);
    const old_storage = fixture.session.storage.ptr;
    const revision = fixture.session.state_revision;
    const mutation = fixture.session.next_mutation_version;
    const next_observation = fixture.session.next_observation_handle;
    const next_report = fixture.session.next_report_handle;
    const report_cursor = fixture.session.next_report_slot;
    var failing = std.testing.FailingAllocator.init(std.heap.page_allocator, .{ .fail_index = 1 });
    fixture.session.allocator = failing.allocator();
    const facts = [_]protocol.FactInput{ factForTarget(2, 41, 70), factForTarget(2, 42, 80), factForTarget(2, 43, 90) };
    var input = sourceSnapshot(4, 4, 2, 110, .complete, facts.len);
    const result = session_module.observe(fixture.session, &input, &facts, facts.len);
    fixture.session.allocator = std.heap.page_allocator;
    try expectCode(.out_of_memory, result);
    try std.testing.expectEqual(old_storage, fixture.session.storage.ptr);
    try std.testing.expectEqualDeep(observations, fixture.session.observations);
    try std.testing.expectEqualDeep(sources, fixture.session.sources);
    try std.testing.expectEqual(revision, fixture.session.state_revision);
    try std.testing.expectEqual(mutation, fixture.session.next_mutation_version);
    try std.testing.expectEqual(next_observation, fixture.session.next_observation_handle);
    try std.testing.expectEqual(next_report, fixture.session.next_report_handle);
    try std.testing.expectEqual(report_cursor, fixture.session.next_report_slot);
    try std.testing.expectEqual(@as(u64, 3), fixture.session.last_operation_epoch);
    try std.testing.expectEqual(failing.allocated_bytes, failing.freed_bytes);
    try fixture.observeComplete(4, 4, 2, 110, &facts);
    try std.testing.expectEqual(@as(usize, 4), fixture.session.reports.len);
    try std.testing.expectEqual(@as(u32, 3), @import("growth.zig").occupied(fixture.session.reports));
}

test "observe fault matrix covers every bucket and report growth allocation" {
    // This operation needs two replacements: observations/buckets, then exact
    // reports discovered by the rollback preview. Each allocates header+arena.
    for (0..5) |failure_index| {
        var config = testConfig();
        var rolling_rule = rule();
        rolling_rule.flags = protocol.RuleFlags.rolling;
        rolling_rule.metric_selector = @intFromEnum(protocol.MetricSelector.sum_24h);
        rolling_rule.required_consecutive_hits = 1;
        rolling_rule.activation_threshold = 1;
        rolling_rule.clear_threshold = 0.5;
        const session = try createReadySession(&config, &.{rolling_rule});
        defer session.destroy();
        try observeRows(session, sourceSnapshot(3, 3, 1, 1_000, .complete, 1), &.{rollingFact(1, 0)});
        const source = session.sources[0];
        const observation = session.observations[0];
        const bucket = session.buckets[0];
        const rolling = session.rolling_values[0];
        const report = session.reports[0];
        const pending = session.planned_persistence[0];
        const planned_output = session.planned_output;
        const revision = session.state_revision;
        const mutation = session.next_mutation_version;
        const next_observation = session.next_observation_handle;
        const next_bucket = session.next_bucket_handle;
        const next_report = session.next_report_handle;
        const observation_cursor = session.next_observation_slot;
        const bucket_cursor = session.next_bucket_slot;
        const report_cursor = session.next_report_slot;
        const original = session.storage.ptr;
        const old_bytes = session.resident_byte_count;
        var failing = std.testing.FailingAllocator.init(std.heap.page_allocator, .{ .fail_index = failure_index });
        session.allocator = failing.allocator();
        var second = rollingFact(1, 6);
        second.target_handle = target_handle + 1;
        const facts = [_]protocol.FactInput{ rollingFact(2, 4), second };
        var input = sourceSnapshot(4, 4, 2, 2_000, .complete, facts.len);
        const result = session_module.observe(session, &input, &facts, facts.len);
        session.allocator = std.heap.page_allocator;
        if (failure_index == 4) {
            try expectCode(.ok, result);
            try std.testing.expect(!failing.has_induced_failure);
            try std.testing.expectEqual(@as(usize, 4), failing.alloc_index);
            try std.testing.expectEqual(@as(usize, 2), session.reports.len);
            try std.testing.expectEqual(@as(u32, 2), @import("growth.zig").occupied(session.reports));
            continue;
        }
        try expectCode(.out_of_memory, result);
        try std.testing.expect(failing.has_induced_failure);
        try std.testing.expectEqualDeep(source, session.sources[0]);
        try std.testing.expectEqualDeep(observation, session.observations[0]);
        try std.testing.expectEqualDeep(bucket, session.buckets[0]);
        try std.testing.expectEqualDeep(rolling, session.rolling_values[0]);
        try std.testing.expectEqualDeep(report, session.reports[0]);
        try std.testing.expectEqualDeep(pending, session.planned_persistence[0]);
        try std.testing.expectEqualDeep(planned_output, session.planned_output);
        try std.testing.expectEqual(@as(u32, 0), session.planned_persistence_count);
        try std.testing.expectEqual(revision, session.state_revision);
        try std.testing.expectEqual(mutation, session.next_mutation_version);
        try std.testing.expectEqual(next_observation, session.next_observation_handle);
        try std.testing.expectEqual(next_bucket, session.next_bucket_handle);
        try std.testing.expectEqual(next_report, session.next_report_handle);
        try std.testing.expectEqual(observation_cursor, session.next_observation_slot);
        try std.testing.expectEqual(bucket_cursor, session.next_bucket_slot);
        try std.testing.expectEqual(report_cursor, session.next_report_slot);
        try std.testing.expectEqual(@as(u64, 3), session.last_operation_epoch);
        try std.testing.expectEqual(@as(u32, 0), session.findSource(source_handle, coverage_scope_handle).?);
        try std.testing.expectEqual(@as(u32, 0), session.findObservation(source_handle, coverage_scope_handle, rule_handle, target_handle).?);
        try std.testing.expectEqual(@as(u32, 0), session.findBucket(source_handle, rule_handle, target_handle, 1_000).?);
        try std.testing.expect(session.findObservation(source_handle, coverage_scope_handle, rule_handle, target_handle + 1) == null);
        try std.testing.expect(session.findBucket(source_handle, rule_handle, target_handle, 2_000) == null);
        try std.testing.expect(session.findReport(target_handle, family_handle) == null);
        for (session.observations[1..]) |slot| try std.testing.expect(!slot.occupied);
        for (session.buckets[1..]) |slot| try std.testing.expect(!slot.occupied);
        if (failure_index < 2) {
            try std.testing.expectEqual(original, session.storage.ptr);
            try std.testing.expectEqual(old_bytes, session.resident_byte_count);
            try std.testing.expectEqual(@as(usize, 1), session.observations.len);
            try std.testing.expectEqual(@as(usize, 1), session.buckets.len);
        } else {
            // The first successful replacement is an allocation high water;
            // the failed second replacement never publishes or changes it.
            try std.testing.expect(original != session.storage.ptr);
            try std.testing.expect(session.resident_byte_count > old_bytes);
            try std.testing.expectEqual(@as(usize, 2), session.observations.len);
            try std.testing.expectEqual(@as(usize, 4), session.buckets.len);
            try std.testing.expectEqual(@as(usize, 1), session.reports.len);
        }
        try observeRows(session, input, &facts);
        try std.testing.expectEqual(@as(u32, 2), @import("growth.zig").occupied(session.reports));
    }
}

test "content sized arena obeys configured limit and steady byte budget before publication" {
    var fixture = try Fixture.create();
    defer fixture.destroy();
    const original = fixture.session.storage.ptr;
    const old_bytes = fixture.session.resident_byte_count;
    const old_arena_bytes = fixture.session.storage.len * @sizeOf(u128);
    const old_growth = fixture.session.workspaceCapacity().growth;
    var demand = fixture.session.allocationShape();
    demand.maximum_bucket_count = fixture.config.maximum_bucket_count + 1;
    try expectCode(.buffer_too_small, fixture.session.ensureShape(demand, .report_reports));
    try std.testing.expectEqual(original, fixture.session.storage.ptr);
    demand = fixture.session.allocationShape();
    demand.maximum_report_count = 2;
    demand.report_index_capacity = 4;
    const required = try state.requiredResidentBytes(&demand);
    fixture.session.config.resident_byte_budget = required - 1;
    try expectCode(.out_of_memory, fixture.session.ensureShape(demand, .report_reports));
    try std.testing.expectEqual(original, fixture.session.storage.ptr);
    try std.testing.expectEqual(old_bytes, fixture.session.resident_byte_count);
    try std.testing.expectEqualDeep(old_growth, fixture.session.workspaceCapacity().growth);
    fixture.session.config.resident_byte_budget = required;
    try expectCode(.ok, fixture.session.ensureShape(demand, .report_reports));
    try std.testing.expectEqual(required, fixture.session.resident_byte_count);
    const diagnostic = fixture.session.workspaceCapacity();
    try std.testing.expectEqual(old_growth.count + 1, diagnostic.growth.count);
    const event = diagnostic.growth.events[old_growth.count % diagnostic.growth.events.len];
    try std.testing.expectEqual(diagnostic.growth.count, event.sequence);
    try std.testing.expectEqual(@intFromEnum(@import("../../common/workspace_growth.zig").Reason.report_reports), event.reason);
    try std.testing.expectEqual(@as(u32, 1), event.unit);
    try std.testing.expectEqual(old_arena_bytes, event.old_capacity);
    try std.testing.expectEqual(fixture.session.storage.len * @sizeOf(u128), event.new_capacity);
    const grown = fixture.session.storage.ptr;
    try expectCode(.ok, fixture.session.ensureShape(demand, .report_reports));
    try std.testing.expectEqual(grown, fixture.session.storage.ptr);
    try std.testing.expectEqualDeep(diagnostic.growth, fixture.session.workspaceCapacity().growth);
    try std.testing.expectEqual(fixture.config.maximum_report_count, fixture.session.capacity().report_capacity);
    try std.testing.expectEqual(@as(u32, 2), fixture.session.workspaceCapacity().report_capacity);
}

test "arena replacement preserves pending persistence references replay and exact feedback" {
    var fixture = try Fixture.create();
    defer fixture.destroy();
    try fixture.observeComplete(3, 3, 1, 100, &.{fact(1, 60)});
    try fixture.observeComplete(4, 4, 2, 110, &.{fact(2, 70)});
    var reports: [4]protocol.ReportOutput = undefined;
    var operations: [32]protocol.PersistenceOperation = undefined;
    var output: protocol.PlanOutput = undefined;
    var input = planInput(5, 5, 1, 120, reports.len, operations.len);
    try expectCode(.ok, session_module.plan(fixture.session, &input, &reports, reports.len, &operations, operations.len, &output));
    const observation_index = fixture.session.findObservation(source_handle, coverage_scope_handle, rule_handle, target_handle).?;
    const saved_observation = fixture.session.observations[observation_index];
    const saved_revision = fixture.session.state_revision;
    const saved_next_handle = fixture.session.next_observation_handle;
    var demand = fixture.session.allocationShape();
    demand.maximum_bucket_count = 9;
    demand.maximum_trust_count = 3;
    state.lockMutex(&fixture.session.mutex);
    const result = fixture.session.ensureShape(demand, .report_import);
    fixture.session.mutex.unlock();
    try expectCode(.ok, result);
    try std.testing.expectEqual(saved_revision, fixture.session.state_revision);
    try std.testing.expectEqual(saved_next_handle, fixture.session.next_observation_handle);
    try std.testing.expectEqualDeep(saved_observation, fixture.session.observations[observation_index]);
    var replay_reports: [4]protocol.ReportOutput = undefined;
    var replay_operations: [32]protocol.PersistenceOperation = undefined;
    var replay: protocol.PlanOutput = undefined;
    try expectCode(.ok, session_module.plan(fixture.session, &input, &replay_reports, replay_reports.len, &replay_operations, replay_operations.len, &replay));
    try std.testing.expectEqualDeep(output, replay);
    try std.testing.expectEqualDeep(reports, replay_reports);
    try std.testing.expectEqualDeep(operations, replay_operations);
    var feedbacks: [32]protocol.PersistenceFeedback = undefined;
    for (operations[0..output.persistence_operation_count], feedbacks[0..output.persistence_operation_count]) |operation, *feedback| feedback.* = feedbackFor(operation, .persisted);
    var feedback_input = persistenceFeedbackBatchInput(6, 6, 1, 130, output.persistence_operation_count);
    try expectCode(.ok, session_module.applyFeedback(fixture.session, &feedback_input, &feedbacks, output.persistence_operation_count));
    try std.testing.expectEqual(@as(u32, 0), fixture.session.planned_persistence_count);
    try std.testing.expectEqual(fixture.session.observations[observation_index].mutation_version, fixture.session.observations[observation_index].persisted_mutation_version);
}

test "small growing report arena matches fixed capacity decisions and persistence rows" {
    var config = testConfig();
    var growing = try Fixture.create();
    defer growing.destroy();
    const fixed_session = try state.Session.createWithShape(std.heap.page_allocator, &config, config);
    defer fixed_session.destroy();
    var rule_row = rule();
    var replace = ruleReplaceInput(1, 1, 100, 1);
    try expectCode(.ok, session_module.replaceRules(fixed_session, &replace, @ptrCast(&rule_row), 1));
    var import_input = importInput(2, 2, 100, 1, 0);
    try expectCode(.ok, session_module.importPersisted(fixed_session, &import_input, null, 0));
    var fixed = Fixture{ .config = config, .session = fixed_session };
    for ([_]*Fixture{ &growing, &fixed }) |fixture| {
        try fixture.observeComplete(3, 3, 1, 100, &.{factForTarget(1, 41, 60)});
        try fixture.observeComplete(4, 4, 2, 110, &.{ factForTarget(2, 41, 70), factForTarget(1, 42, 80) });
        try fixture.observeComplete(5, 5, 3, 120, &.{ factForTarget(3, 41, 80), factForTarget(2, 42, 90), factForTarget(1, 43, 100) });
    }
    var reports: [2][4]protocol.ReportOutput = undefined;
    var operations: [2][32]protocol.PersistenceOperation = undefined;
    var outputs: [2]protocol.PlanOutput = undefined;
    var input = planInput(6, 6, 1, 130, 4, 32);
    for ([_]*state.Session{ growing.session, fixed_session }, 0..) |session, index| {
        try expectCode(.ok, session_module.plan(session, &input, &reports[index], 4, &operations[index], 32, &outputs[index]));
        outputs[index].resident_byte_count = 0;
    }
    try std.testing.expectEqualDeep(outputs[0], outputs[1]);
    try std.testing.expectEqualDeep(reports[0], reports[1]);
    try std.testing.expectEqualDeep(operations[0], operations[1]);
    try std.testing.expect(growing.session.resident_byte_count < fixed_session.resident_byte_count);
}

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

test "resident budget exactly covers the initial content sized storage backing" {
    var config = testConfig();
    const shape = state.initialShape(&config);
    const required = try state.requiredResidentBytes(&shape);
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

test "unavailable breaks pending hits without asserting a safe measurement" {
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
    try std.testing.expectEqual(@as(u32, 0), fixture.session.observations[observation_index].consecutive_hits);
    try fixture.observeComplete(5, 5, 3, 120, &.{fact(2, 70)});
    try std.testing.expect(!fixture.session.observations[observation_index].active);
    try fixture.observeComplete(6, 6, 4, 130, &.{fact(3, 70)});
    try std.testing.expect(fixture.session.observations[observation_index].active);
    try fixture.observeComplete(7, 7, 5, 140, &.{});
    try fixture.observeUnavailable(8, 8, 6, 150);
    try std.testing.expect(!fixture.session.observations[observation_index].active);
    try std.testing.expectEqual(@as(u32, 0), fixture.session.observations[observation_index].consecutive_misses);
    try fixture.observeComplete(9, 9, 7, 160, &.{});
    try std.testing.expect(!fixture.session.observations[observation_index].active);
    try fixture.observeComplete(10, 10, 8, 170, &.{});
    try std.testing.expect(!fixture.session.observations[observation_index].active);
}

test "stale gap breaks pending hits even when no unavailable frame arrives" {
    var fixture = try Fixture.create();
    defer fixture.destroy();
    try fixture.observeComplete(3, 3, 1, 100, &.{fact(1, 60)});
    const index = fixture.session.findObservation(source_handle, coverage_scope_handle, rule_handle, target_handle).?;
    try fixture.observeComplete(4, 4, 2, 10_101, &.{fact(2, 60)});
    try std.testing.expect(!fixture.session.observations[index].active);
    try std.testing.expectEqual(@as(u32, 1), fixture.session.observations[index].consecutive_hits);
    try fixture.observeComplete(5, 5, 3, 10_102, &.{fact(3, 60)});
    try std.testing.expect(fixture.session.observations[index].active);
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
        try std.testing.expectEqual(@as(u32, 0), fixture.session.observations[index].consecutive_misses);
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
            try std.testing.expectEqual(@as(u32, 0), row.consecutive_misses);
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
        try std.testing.expectEqual(@as(u32, 0), restored.observations[restored_index].consecutive_misses);
        try std.testing.expect(!restored.observations[restored_index].active);
        try std.testing.expectEqual(sample_count, restored.observations[restored_index].sample_count);

        try observeRows(restored, sourceSnapshot(3, 3, snapshot, utc + 10, .complete, 1), &.{fact(sequence, 60)});
        try std.testing.expectEqual(@as(u32, 0), restored.observations[restored_index].consecutive_misses);
        try std.testing.expect(!restored.observations[restored_index].active);
        try observeRows(restored, sourceSnapshot(4, 4, snapshot + 1, utc + 20, .complete, 1), &.{fact(sequence + 1, 60)});
        try std.testing.expect(restored.observations[restored_index].active);
    }
}

test "repeated resets persist only the source frame and checkpoint until stored content changes" {
    const Reset = enum { missing, fact_unavailable, source_unavailable };
    for ([_]Reset{ .missing, .fact_unavailable, .source_unavailable }) |reset| {
        var fixture = try Fixture.create();
        defer fixture.destroy();
        // The durable table the managed store keeps: one row per kind and identity.
        var table: [16]protocol.PersistenceOperation = undefined;
        var table_count: usize = 0;
        var epoch: u64 = 3;
        var plan_epoch: u64 = 1;
        var utc: i64 = 100;
        var sequence: u64 = 1;
        var round: u64 = 0;
        while (round < 5) : (round += 1) {
            if (round == 0) {
                try fixture.observeComplete(epoch, epoch, round + 1, utc, &.{fact(sequence, 60)});
            } else switch (reset) {
                .missing => try fixture.observeComplete(epoch, epoch, round + 1, utc, &.{}),
                .fact_unavailable => {
                    sequence += 1;
                    var unavailable = fact(sequence, 0);
                    unavailable.flags = 0;
                    try fixture.observeComplete(epoch, epoch, round + 1, utc, &.{unavailable});
                },
                .source_unavailable => try fixture.observeUnavailable(epoch, epoch, round + 1, utc),
            }
            const planned = try lifecyclePlan(fixture.session, epoch + 1, plan_epoch, utc + 1);
            const rows = planned.operations[0..planned.output.persistence_operation_count];
            var observation_rows: usize = 0;
            for (rows) |row| {
                switch (@as(protocol.PersistenceKind, @enumFromInt(row.operation_kind))) {
                    .source, .metadata => {},
                    .observation => observation_rows += 1,
                    else => return error.UnexpectedPersistenceRow,
                }
            }
            // The first settle stores the sample and the first reset stores the
            // zeroed counters; identical resets after that store nothing new.
            try std.testing.expectEqual(@as(usize, if (round < 2) 1 else 0), observation_rows);
            try storeRows(&table, &table_count, rows);
            try settlePlan(fixture.session, rows, epoch + 2, epoch + 2, plan_epoch, utc + 2);
            epoch += 3;
            plan_epoch += 1;
            utc += 10;
        }
        const live_index = fixture.session.findObservation(
            source_handle,
            coverage_scope_handle,
            rule_handle,
            target_handle,
        ).?;
        const live = fixture.session.observations[live_index];
        try std.testing.expectEqual(live.persisted_mutation_version, live.mutation_version);

        // Restart from the durable table restores the same stored observation;
        // the next managed fact sequence follows the stored cursor.
        const restored = try state.Session.create(&fixture.config);
        defer restored.destroy();
        var rule_row = rule();
        var replace = ruleReplaceInput(1, 1, utc, 1);
        try expectCode(.ok, session_module.replaceRules(restored, &replace, @ptrCast(&rule_row), 1));
        var import_input = importInput(2, 2, utc, 1, @intCast(table_count));
        try expectCode(.ok, session_module.importPersisted(restored, &import_input, &table, @intCast(table_count)));
        const restored_index = restored.findObservation(
            source_handle,
            coverage_scope_handle,
            rule_handle,
            target_handle,
        ).?;
        const back = restored.observations[restored_index];
        try std.testing.expectEqual(live.sample_count, back.sample_count);
        try std.testing.expectEqual(live.value_sum, back.value_sum);
        try std.testing.expectEqual(live.last_observed_at_utc_ms, back.last_observed_at_utc_ms);
        try std.testing.expectEqual(@as(u32, 0), back.consecutive_hits);
        try std.testing.expectEqual(@as(u32, 0), back.consecutive_misses);
        try std.testing.expect(!back.active);
        try std.testing.expect(back.last_fact_sequence <= live.last_fact_sequence);
        try observeRows(restored, sourceSnapshot(3, 3, round + 1, utc, .complete, 1), &.{fact(back.last_fact_sequence + 1, 60)});
        try std.testing.expectEqual(live.sample_count + 1, restored.observations[restored_index].sample_count);

        // A returning fact is a stored change in the running session.
        try fixture.observeComplete(epoch, epoch, round + 1, utc, &.{fact(sequence + 1, 60)});
        const returned = try lifecyclePlan(fixture.session, epoch + 1, plan_epoch, utc + 1);
        const returned_row = try findOperation(returned.operations[0..returned.output.persistence_operation_count], .observation);
        try std.testing.expectEqual(live.sample_count + 1, returned_row.sample_count);
        try std.testing.expectEqual(@as(u32, 1), returned_row.consecutive_hits);
        try std.testing.expectEqual(sequence + 1, returned_row.fact_sequence);
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
        const expected_misses: u32 = 0;
        try std.testing.expectEqual(expected_misses, fixture.session.observations[index].consecutive_misses);
        try std.testing.expectEqual(operation < 8, fixture.session.observations[index].active);
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
        try std.testing.expectEqual(@as(u32, 0), observation.consecutive_misses);
        try std.testing.expectEqual(@as(u64, 2), observation.sample_count);
        try std.testing.expect(!observation.active);
    }
}

test "trust suppresses reports only after exact persisted feedback" {
    var fixture = try Fixture.create();
    defer fixture.destroy();

    try fixture.observeComplete(3, 3, 1, 100, &.{fact(1, 60)});
    try fixture.observeComplete(4, 4, 2, 110, &.{fact(2, 70)});
    const report_index = fixture.session.findReport(target_handle, family_handle).?;
    try std.testing.expect(!fixture.session.reports[report_index].trusted);

    var command = trustCommand(5, 5, .add);
    try expectCode(.ok, session_module.commandTrust(fixture.session, &command));
    try std.testing.expect(!fixture.session.reports[report_index].trusted);

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
    try std.testing.expect(fixture.session.reports[report_index].trusted);
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
    try std.testing.expect(!fixture.session.reports[report_index].trusted);
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

test "plan sizing previews maintenance without committing and supports exact full batch replay" {
    var fixture = try Fixture.create();
    defer fixture.destroy();
    try fixture.observeComplete(3, 3, 1, 100, &.{ factForTarget(1, 41, 60), factForTarget(1, 42, 70) });
    try fixture.observeComplete(4, 4, 2, 110, &.{ factForTarget(2, 41, 60), factForTarget(2, 42, 70) });
    const before_reports = try std.testing.allocator.dupe(state.ReportSlot, fixture.session.reports);
    defer std.testing.allocator.free(before_reports);
    const before_observations = try std.testing.allocator.dupe(state.ObservationSlot, fixture.session.observations);
    defer std.testing.allocator.free(before_observations);
    const before_mutation = fixture.session.next_mutation_version;
    const before_report = fixture.session.next_report_handle;
    const before_slot = fixture.session.next_report_slot;
    const before_revision = fixture.session.state_revision;
    const before_metadata_mutation = fixture.session.metadata_mutation_version;

    // Stale evidence does not remove reports, and sizing rolls maintenance back.
    var expired = planInput(5, 5, 1, 20_000, 4, 32);
    var report_count: u32 = 99;
    var operation_count: u32 = 99;
    try expectCode(.ok, session_module.queryPlanSize(fixture.session, &expired, &report_count, &operation_count));
    try std.testing.expectEqual(@as(u32, 2), report_count);
    try std.testing.expectEqualDeep(before_reports, fixture.session.reports);
    try std.testing.expectEqualDeep(before_observations, fixture.session.observations);
    try std.testing.expectEqual(before_mutation, fixture.session.next_mutation_version);
    try std.testing.expectEqual(before_report, fixture.session.next_report_handle);
    try std.testing.expectEqual(before_slot, fixture.session.next_report_slot);
    try std.testing.expectEqual(before_revision, fixture.session.state_revision);
    try std.testing.expectEqual(before_metadata_mutation, fixture.session.metadata_mutation_version);
    try std.testing.expectEqual(@as(u64, 0), fixture.session.last_plan_epoch);

    var input = planInput(5, 5, 1, 120, 4, 32);
    try expectCode(.ok, session_module.queryPlanSize(fixture.session, &input, &report_count, &operation_count));
    try std.testing.expectEqual(@as(u32, 2), report_count);
    try std.testing.expect(operation_count > 1 and operation_count < 32);
    var reports: [4]protocol.ReportOutput = undefined;
    var operations: [32]protocol.PersistenceOperation = undefined;
    var output: protocol.PlanOutput = undefined;
    input.report_limit = report_count;
    input.persistence_limit = 1;
    try expectCode(.buffer_too_small, session_module.plan(fixture.session, &input, &reports, report_count, &operations, 1, &output));
    try std.testing.expectEqual(@as(u64, 0), fixture.session.last_plan_epoch);
    input.persistence_limit = operation_count;
    try expectCode(.ok, session_module.plan(fixture.session, &input, &reports, report_count, &operations, operation_count, &output));
    try std.testing.expectEqual(report_count, output.report_count);
    try std.testing.expectEqual(operation_count, output.persistence_operation_count);
    try std.testing.expectEqual(@as(u64, 0), output.flags &
        (protocol.PlanFlags.has_more_reports | protocol.PlanFlags.has_more_persistence));
    var replay_reports: u32 = 0;
    var replay_operations: u32 = 0;
    try expectCode(.ok, session_module.queryPlanSize(fixture.session, &input, &replay_reports, &replay_operations));
    try std.testing.expectEqual(report_count, replay_reports);
    try std.testing.expectEqual(operation_count, replay_operations);
    try expectCode(.ok, session_module.plan(fixture.session, &input, &reports, replay_reports, &operations, replay_operations, &output));
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
    try std.testing.expectApproxEqAbs(@as(f64, 10), reports[0].window_24h_value, 0.001);
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
    try std.testing.expectEqual(@as(u32, 1), output.report_count);
    try std.testing.expectEqual(@as(u32, 0), output.active_report_count);
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
    const before_observations = try std.testing.allocator.dupe(state.ObservationSlot, session.observations);
    defer std.testing.allocator.free(before_observations);
    const before_buckets = try std.testing.allocator.dupe(state.BucketSlot, session.buckets);
    defer std.testing.allocator.free(before_buckets);
    const before_rolling = try std.testing.allocator.dupe(state.RollingValues, session.rolling_values);
    defer std.testing.allocator.free(before_rolling);
    var report_count: u32 = 99;
    var operation_count: u32 = 99;
    try expectCode(.ok, session_module.queryPlanSize(session, &expired, &report_count, &operation_count));
    try std.testing.expectEqual(@as(u32, 1), report_count);
    try std.testing.expectEqualDeep(before_observations, session.observations);
    try std.testing.expectEqualDeep(before_buckets, session.buckets);
    try std.testing.expectEqualDeep(before_rolling, session.rolling_values);
    try expectCode(.ok, session_module.plan(
        session,
        &expired,
        &reports,
        reports.len,
        &operations,
        operations.len,
        &output,
    ));
    try std.testing.expectEqual(@as(u32, 1), output.report_count);
    try std.testing.expectEqual(@as(u32, 0), output.active_report_count);
    try std.testing.expectEqual(operation_count, output.persistence_operation_count);
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
    try std.testing.expect(session.reports[first_report].trusted);
    try std.testing.expect(!session.reports[second_report].trusted);

    var wildcard = trustCommand(7, 7, .add);
    wildcard.family_handle = protocol.TrustFamily.all;
    try expectCode(.ok, session_module.commandTrust(session, &wildcard));
    try settleCurrentPlan(session, 8, 9, 2, 120);
    try std.testing.expect(session.reports[first_report].trusted);
    try std.testing.expect(session.reports[second_report].trusted);
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
    try std.testing.expect(session.reports[report_index].trusted);
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
    try std.testing.expect(fixture.session.reports[report_index].trusted);

    var remove_command = trustCommand(8, 8, .remove);
    try expectCode(.ok, session_module.commandTrust(fixture.session, &remove_command));
    try std.testing.expect(fixture.session.reports[report_index].trusted);

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
    try std.testing.expect(fixture.session.reports[report_index].trusted);

    try settlePlan(
        fixture.session,
        operations[0..output.persistence_operation_count],
        10,
        10,
        2,
        150,
    );
    try std.testing.expect(!fixture.session.reports[report_index].trusted);
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

test "unavailable fact affects only its target and expires without renewing evidence" {
    var fixture = try Fixture.create();
    defer fixture.destroy();
    try fixture.observeComplete(3, 3, 1, 100, &.{ factForTarget(1, 41, 60), factForTarget(1, 42, 60) });
    try fixture.observeComplete(4, 4, 2, 110, &.{ factForTarget(2, 41, 60), factForTarget(2, 42, 60) });
    var missing = factForTarget(3, 41, 0);
    missing.flags = 0;
    try fixture.observeComplete(5, 5, 3, 120, &.{ missing, factForTarget(3, 42, 0) });
    missing.fact_sequence = 4;
    try fixture.observeComplete(6, 6, 4, 130, &.{ missing, factForTarget(4, 42, 0) });
    const a = fixture.session.findObservation(source_handle, coverage_scope_handle, rule_handle, 41).?;
    const b = fixture.session.findObservation(source_handle, coverage_scope_handle, rule_handle, 42).?;
    try std.testing.expect(fixture.session.observations[a].active);
    try std.testing.expect(!fixture.session.observations[b].active);
    try std.testing.expectEqual(@as(i64, 110), fixture.session.observations[a].last_observed_at_utc_ms);
    try std.testing.expectEqual(@as(u64, 2), fixture.session.observations[a].sample_count);
    try std.testing.expectEqual(@as(u32, 0), fixture.session.observations[a].consecutive_hits);
    missing.fact_sequence = 5;
    try fixture.observeComplete(7, 7, 5, 10_111, &.{missing});
    try std.testing.expect(!fixture.session.observations[a].active);
}

test "unavailable fact resets pending hits and complete absence still clears" {
    var fixture = try Fixture.create();
    defer fixture.destroy();
    try fixture.observeComplete(3, 3, 1, 100, &.{fact(1, 60)});
    var missing = fact(2, 0);
    missing.flags = 0;
    try fixture.observeComplete(4, 4, 2, 110, &.{missing});
    try fixture.observeComplete(5, 5, 3, 120, &.{fact(3, 60)});
    const index = fixture.session.findObservation(source_handle, coverage_scope_handle, rule_handle, target_handle).?;
    try std.testing.expect(!fixture.session.observations[index].active);
    try fixture.observeComplete(6, 6, 4, 130, &.{fact(4, 60)});
    try std.testing.expect(fixture.session.observations[index].active);
    try fixture.observeComplete(7, 7, 5, 140, &.{});
    try fixture.observeComplete(8, 8, 6, 150, &.{});
    try std.testing.expect(!fixture.session.observations[index].active);
}

test "report lifecycle freezes last hit through safe misses cleanup restart and reactivation" {
    var fixture = try Fixture.create();
    defer fixture.destroy();
    try fixture.observeComplete(3, 3, 1, 100, &.{fact(1, 60)});
    try fixture.observeComplete(4, 4, 2, 110, &.{fact(2, 70)});
    const index = fixture.session.findReport(target_handle, family_handle).?;
    const hit = fixture.session.reports[index];
    try fixture.observeComplete(5, 5, 3, 120, &.{fact(3, 10)});
    try std.testing.expectEqualDeep(hit, fixture.session.reports[index]);
    try fixture.observeComplete(6, 6, 4, 130, &.{fact(4, 20)});
    const history = fixture.session.reports[index];
    try std.testing.expect(!history.active);
    try std.testing.expectEqual(hit.current_value, history.current_value);
    try std.testing.expectEqual(hit.sample_count, history.sample_count);
    try std.testing.expectEqual(hit.last_observed_at_utc_ms, history.last_observed_at_utc_ms);
    const old = try lifecyclePlan(fixture.session, 7, 1, 40_000);
    try std.testing.expectEqual(@as(u32, 1), old.output.report_count);
    try std.testing.expectEqual(@as(u32, 0), old.output.active_report_count);
    try std.testing.expectEqual(@as(u32, 0), old.reports[0].flags & protocol.ReportFlags.active);
    try std.testing.expectEqual(@as(f64, 70), old.reports[0].current_value);
    try std.testing.expect(fixture.session.observations[0].delete_pending);
    const report_row = try findOperation(old.operations[0..old.output.persistence_operation_count], .report);
    try std.testing.expectEqual(@as(f64, 70), report_row.current_value);
    const rows = [_]protocol.PersistenceOperation{
        try findOperation(old.operations[0..old.output.persistence_operation_count], .metadata),
        report_row,
    };
    const restored = try state.Session.create(&fixture.config);
    defer restored.destroy();
    const rules = [_]protocol.RuleInput{rule()};
    var replace = ruleReplaceInput(1, 1, 40_000, 1);
    try expectCode(.ok, session_module.replaceRules(restored, &replace, &rules, 1));
    var input = importInput(2, 2, 40_000, 1, rows.len);
    try expectCode(.ok, session_module.importPersisted(restored, &input, &rows, rows.len));
    const revived_index = restored.findReport(target_handle, family_handle).?;
    try std.testing.expectEqualDeep(history, blk: {
        var copy = restored.reports[revived_index];
        copy.persisted_mutation_version = history.persisted_mutation_version;
        copy.observation_index = history.observation_index;
        break :blk copy;
    });
    try observeRows(restored, sourceSnapshot(3, 3, 1, 40_010, .complete, 1), &.{fact(1, 80)});
    try std.testing.expect(!restored.reports[revived_index].active);
    try observeRows(restored, sourceSnapshot(4, 4, 2, 40_020, .complete, 1), &.{fact(2, 90)});
    try std.testing.expect(restored.reports[revived_index].active);
    try std.testing.expectEqual(hit.report_handle, restored.reports[revived_index].report_handle);
    try std.testing.expectEqual(hit.created_at_utc_ms, restored.reports[revived_index].created_at_utc_ms);
    try std.testing.expectEqual(@as(usize, 1), occupiedReports(restored));
    try std.testing.expectEqual(@as(f64, 90), restored.reports[revived_index].current_value);
}

test "active report survives unavailable skipped target unavailable stale and observation cleanup" {
    var fixture = try Fixture.create();
    defer fixture.destroy();
    try fixture.observeComplete(3, 3, 1, 100, &.{fact(1, 60)});
    try fixture.observeComplete(4, 4, 2, 110, &.{fact(2, 70)});
    const index = fixture.session.findReport(target_handle, family_handle).?;
    const hit = fixture.session.reports[index];
    try fixture.observeUnavailable(5, 5, 3, 120);
    try observeRows(fixture.session, sourceSnapshot(6, 6, 4, 130, .skipped, 0), &.{});
    var unavailable = fact(3, 0);
    unavailable.flags = 0;
    try fixture.observeComplete(7, 7, 5, 140, &.{unavailable});
    try std.testing.expectEqualDeep(hit, fixture.session.reports[index]);
    const snapshot = try lifecyclePlan(fixture.session, 8, 1, 40_000);
    try std.testing.expectEqualDeep(hit, fixture.session.reports[index]);
    try std.testing.expectEqual(@as(u32, 1), snapshot.output.active_report_count);
    try std.testing.expectEqual(@as(u32, 1), snapshot.output.report_count);
    try std.testing.expectEqual(@as(f64, 70), snapshot.reports[0].current_value);
    try settlePlan(fixture.session, snapshot.operations[0..snapshot.output.persistence_operation_count], 9, 9, 1, 40_001);
    const persisted_hit = fixture.session.reports[index];
    try std.testing.expect(fixture.session.findObservation(source_handle, coverage_scope_handle, rule_handle, target_handle) == null);
    const empty_observations = try lifecyclePlan(fixture.session, 10, 2, 40_200);
    try std.testing.expectEqual(@as(u32, 1), empty_observations.output.active_report_count);
    try std.testing.expectEqualDeep(persisted_hit, fixture.session.reports[index]);
    try settlePlan(fixture.session, empty_observations.operations[0..empty_observations.output.persistence_operation_count], 11, 11, 2, 40_201);
    try fixture.observeComplete(12, 12, 6, 40_210, &.{});
    try std.testing.expect(!fixture.session.reports[index].active);
    try std.testing.expectEqual(@as(f64, 70), fixture.session.reports[index].current_value);
}

test "rule generation change resets observations while retaining active and history reports" {
    for ([_]bool{ false, true }) |make_history| {
        var fixture = try Fixture.create();
        defer fixture.destroy();
        try fixture.observeComplete(3, 3, 1, 100, &.{fact(1, 60)});
        try fixture.observeComplete(4, 4, 2, 110, &.{fact(2, 70)});
        if (make_history) try fixture.observeComplete(5, 5, 3, 120, &.{});
        const index = fixture.session.findReport(target_handle, family_handle).?;
        const retained = fixture.session.reports[index];
        var changed = rule();
        changed.rule_generation += 1;
        changed.activation_threshold = 90;
        changed.clear_threshold = 80;
        var replace = ruleReplaceInput(6, 6, 130, 1);
        try expectCode(.ok, session_module.replaceRules(fixture.session, &replace, @ptrCast(&changed), 1));
        try std.testing.expectEqualDeep(retained, fixture.session.reports[index]);
        const observation = fixture.session.observations[0];
        try std.testing.expectEqual(@as(u64, 0), observation.sample_count);
        try std.testing.expectEqual(@as(u32, 0), observation.consecutive_hits);
        try std.testing.expectEqual(@as(u32, 0), observation.consecutive_misses);
        const planned = try lifecyclePlan(fixture.session, 7, 1, 140);
        try std.testing.expectEqual(@as(u32, 1), planned.output.report_count);
        try std.testing.expectEqual(@as(u32, if (make_history) 0 else 1), planned.output.active_report_count);
        try std.testing.expectEqualDeep(retained, fixture.session.reports[index]);
        const reset_row = try findOperation(planned.operations[0..planned.output.persistence_operation_count], .observation);
        try std.testing.expectEqual(changed.rule_generation, reset_row.rule_generation);
        try std.testing.expectEqual(@as(u64, 0), reset_row.sample_count);
        try settlePlan(fixture.session, planned.operations[0..planned.output.persistence_operation_count], 8, 8, 1, 141);
        const settled_report = fixture.session.reports[index];
        try observeRows(fixture.session, sourceSnapshot(9, 9, 4, 150, .complete, 1), &.{factFor(rule_handle, changed.rule_generation, 1, 100)});
        try std.testing.expectEqualDeep(settled_report, fixture.session.reports[index]);
        try observeRows(fixture.session, sourceSnapshot(10, 10, 5, 160, .complete, 1), &.{factFor(rule_handle, changed.rule_generation, 2, 100)});
        try std.testing.expect(fixture.session.reports[index].active);
        try std.testing.expectEqual(retained.report_handle, fixture.session.reports[index].report_handle);
    }
}

test "legacy inactive report restores independently of observations and does not expire" {
    for ([_]bool{ false, true }) |active| {
        var config = testConfig();
        const session = try state.Session.create(&config);
        defer session.destroy();
        const rules = [_]protocol.RuleInput{rule()};
        var replace = ruleReplaceInput(1, 1, 100, 1);
        try expectCode(.ok, session_module.replaceRules(session, &replace, &rules, 1));
        var row = persistedReportRow();
        row.flags = protocol.PersistenceFlags.trusted | (if (active) protocol.PersistenceFlags.active else @as(u32, 0));
        const rows = [_]protocol.PersistenceOperation{ persistedMetadataRow(6, 150), row, persistedTrustRow(71, 5, 1) };
        var input = importInput(2, 2, 150, 1, rows.len);
        try expectCode(.ok, session_module.importPersisted(session, &input, &rows, rows.len));
        const planned = try lifecyclePlan(session, 3, 1, 100_000);
        try std.testing.expectEqual(@as(u32, 1), planned.output.report_count);
        try std.testing.expectEqual(@as(u32, if (active) 1 else 0), planned.output.active_report_count);
        try std.testing.expectEqual(protocol.ReportFlags.trusted | (if (active) protocol.ReportFlags.active else @as(u32, 0)), planned.reports[0].flags);
        try std.testing.expectEqual(row.identity_handle, planned.reports[0].report_handle);
        try std.testing.expectEqual(row.report_observed_at_utc_milliseconds, planned.reports[0].last_observed_at_utc_milliseconds);
    }
}

test "full report capacity retains all identities and counts current unrecorded candidates once per family" {
    var config = testConfig();
    config.maximum_report_count = 1;
    config.maximum_observation_count = 8;
    config.observation_index_capacity = 16;
    var first = rule();
    first.required_consecutive_hits = 1;
    first.required_consecutive_misses = 1;
    var second = first;
    second.rule_handle += 1;
    second.predicate_group_handle += 1;
    const rules = [_]protocol.RuleInput{ first, second };
    const session = try createReadySession(&config, &rules);
    defer session.destroy();
    var facts: [6]protocol.FactInput = undefined;
    for (0..3) |i| {
        facts[i * 2] = factFor(first.rule_handle, first.rule_generation, 1, 60);
        facts[i * 2 + 1] = factFor(second.rule_handle, second.rule_generation, 1, 60);
        facts[i * 2].target_handle = 41 + i;
        facts[i * 2 + 1].target_handle = 41 + i;
    }
    try observeRows(session, sourceSnapshot(3, 3, 1, 100, .complete, facts.len), &facts);
    try std.testing.expectEqual(@as(usize, 1), occupiedReports(session));
    try std.testing.expectEqual(@as(u32, 2), @import("rules.zig").unrecordedReportCount(session));
    var input = planInput(4, 4, 1, 110, 4, 32);
    var report_count: u32 = 0;
    var operation_count: u32 = 0;
    try expectCode(.ok, session_module.queryPlanSize(session, &input, &report_count, &operation_count));
    try std.testing.expectEqual(@as(u32, 1), report_count);
    const full = try lifecyclePlan(session, 4, 1, 110);
    try std.testing.expectEqual(operation_count, full.output.persistence_operation_count);
    try std.testing.expectEqual(@as(u32, 2), full.output.unrecorded_report_count);
    try std.testing.expectEqual(@as(u32, 1), full.output.active_report_count);
    try settlePlan(session, full.operations[0..full.output.persistence_operation_count], 5, 5, 1, 111);
    const identity = full.reports[0].report_handle;
    try observeRows(session, sourceSnapshot(6, 6, 2, 120, .unavailable, 0), &.{});
    const unavailable = try lifecyclePlan(session, 7, 2, 130);
    try std.testing.expectEqual(@as(u32, 0), unavailable.output.unrecorded_report_count);
    try std.testing.expectEqual(identity, unavailable.reports[0].report_handle);
    try std.testing.expectEqual(@as(u32, 1), unavailable.output.active_report_count);
    try settlePlan(session, unavailable.operations[0..unavailable.output.persistence_operation_count], 8, 8, 2, 131);
    try observeRows(session, sourceSnapshot(9, 9, 3, 140, .complete, 0), &.{});
    for (&facts) |*value| value.fact_sequence = 2;
    try observeRows(session, sourceSnapshot(10, 10, 4, 150, .complete, facts.len), &facts);
    const again = try lifecyclePlan(session, 11, 3, 160);
    try std.testing.expectEqual(identity, again.reports[0].report_handle);
    try std.testing.expectEqual(@as(u32, 2), again.output.unrecorded_report_count);
    try std.testing.expectEqual(@as(u32, 1), again.output.report_total_count);
    try settlePlan(session, again.operations[0..again.output.persistence_operation_count], 12, 12, 3, 161);
    const stale = try lifecyclePlan(session, 13, 4, 40_000);
    try std.testing.expectEqual(@as(u32, 0), stale.output.unrecorded_report_count);
    try std.testing.expectEqual(@as(u32, 1), stale.output.active_report_count);
    try std.testing.expectEqual(identity, stale.reports[0].report_handle);
}

test "import preserves full capacity candidates and committed unknown clear and stale outcomes without source replay" {
    const Scenario = enum { active, not_latched, clear_pending, clear, source_unavailable, fact_unavailable, stale };
    for ([_]Scenario{ .active, .not_latched, .clear_pending, .clear, .source_unavailable, .fact_unavailable, .stale }) |scenario| {
        var config = testConfig();
        config.maximum_report_count = 1;
        const report_rules = [_]protocol.RuleInput{rule()};
        const original = try createReadySession(&config, &report_rules);
        defer original.destroy();
        var facts = [_]protocol.FactInput{ fact(1, 60), fact(1, 60), fact(1, 60) };
        for (&facts, 0..) |*value, index| value.target_handle += @intCast(index);
        try observeRows(original, sourceSnapshot(3, 3, 1, 100, .complete, facts.len), &facts);
        if (scenario != .not_latched) {
            for (&facts) |*value| value.fact_sequence = 2;
            try observeRows(original, sourceSnapshot(4, 4, 2, 110, .complete, facts.len), &facts);
        }
        if (scenario == .clear_pending or scenario == .clear) {
            for (&facts) |*value| {
                value.fact_sequence = 3;
                value.current_value = 10;
                value.peak_value = 10;
            }
            try observeRows(original, sourceSnapshot(5, 5, 3, 120, .complete, facts.len), &facts);
            if (scenario == .clear) {
                for (&facts) |*value| value.fact_sequence = 4;
                try observeRows(original, sourceSnapshot(6, 6, 4, 130, .complete, facts.len), &facts);
            }
        } else if (scenario == .source_unavailable) {
            try observeRows(original, sourceSnapshot(5, 5, 3, 120, .unavailable, 0), &.{});
        } else if (scenario == .fact_unavailable) {
            for (&facts) |*value| {
                value.fact_sequence = 3;
                value.flags = 0;
            }
            try observeRows(original, sourceSnapshot(5, 5, 3, 120, .complete, facts.len), &facts);
        }
        const checkpoint = try lifecyclePlan(original, 10, 1, 150);
        const active_decision = scenario == .active or scenario == .stale;
        try std.testing.expectEqual(@as(u32, if (active_decision) 2 else 0), checkpoint.output.unrecorded_report_count);
        const rows = checkpoint.operations[0..checkpoint.output.persistence_operation_count];
        const source = try findOperation(rows, .source);
        const restored = try state.Session.create(&config);
        defer restored.destroy();
        const now: i64 = if (scenario == .stale) 10_150 else 150;
        var replace = ruleReplaceInput(1, 1, now, report_rules.len);
        try expectCode(.ok, session_module.replaceRules(restored, &replace, &report_rules, report_rules.len));
        var input = importInput(2, 2, now, 1, @intCast(rows.len));
        try expectCode(.ok, session_module.importPersisted(restored, &input, rows.ptr, @intCast(rows.len)));
        for (rows) |row| {
            if (row.operation_kind != @intFromEnum(protocol.PersistenceKind.observation)) continue;
            const index = restored.findObservation(row.source_handle, row.coverage_scope_handle, row.rule_handle, row.target_handle).?;
            const observation = restored.observations[index];
            try std.testing.expectEqual(row.sample_count, observation.sample_count);
            try std.testing.expectEqual(row.fact_sequence, observation.last_fact_sequence);
            if (active_decision) {
                try std.testing.expect(observation.decision == .active);
            } else if (scenario == .clear) {
                try std.testing.expect(observation.decision == .clear);
            } else {
                try std.testing.expect(observation.decision == .unknown);
            }
        }
        // A restored source watermark still rejects the exact old publication.
        var replay = sourceSnapshot(3, 3, source.source_snapshot_epoch, now, .complete, 0);
        replay.source_generation = source.source_generation;
        try expectCode(.stale_frame, session_module.observe(restored, &replay, null, 0));
        const planned = try lifecyclePlan(restored, 3, 1, now);
        try std.testing.expectEqual(@as(u32, if (scenario == .active) 2 else 0), planned.output.unrecorded_report_count);
        if (scenario == .active) {
            try std.testing.expectEqual(checkpoint.reports[0].updated_at_utc_milliseconds, planned.reports[0].updated_at_utc_milliseconds);
            try std.testing.expectEqual(checkpoint.reports[0].last_observed_at_utc_milliseconds, planned.reports[0].last_observed_at_utc_milliseconds);
        }
        for (rows) |row| {
            if (row.operation_kind != @intFromEnum(protocol.PersistenceKind.observation)) continue;
            const index = restored.findObservation(row.source_handle, row.coverage_scope_handle, row.rule_handle, row.target_handle).?;
            try std.testing.expectEqual(row.sample_count, restored.observations[index].sample_count);
            if (scenario == .stale) try std.testing.expect(restored.observations[index].decision == .unknown);
        }
    }
}

test "rule generation change clears rolling buckets without clearing the report snapshot" {
    var config = testConfig();
    var rolling = rule();
    rolling.flags = protocol.RuleFlags.rolling;
    rolling.metric_selector = @intFromEnum(protocol.MetricSelector.sum_24h);
    rolling.required_consecutive_hits = 1;
    rolling.activation_threshold = 1;
    rolling.clear_threshold = 0.5;
    const session = try createReadySession(&config, &.{rolling});
    defer session.destroy();
    try observeRows(session, sourceSnapshot(3, 3, 1, 1_000, .complete, 1), &.{rollingFact(1, 4)});
    const retained = session.reports[0];
    rolling.rule_generation += 1;
    rolling.activation_threshold = 8;
    var replace = ruleReplaceInput(4, 4, 1_010, 1);
    try expectCode(.ok, session_module.replaceRules(session, &replace, @ptrCast(&rolling), 1));
    try std.testing.expectEqualDeep(retained, session.reports[0]);
    try std.testing.expectEqual(@as(f64, 0), session.buckets[0].delta_value);
    try std.testing.expectEqual(@as(u64, 0), session.buckets[0].sample_duration_milliseconds);
    try std.testing.expectEqual(@as(f64, 0), session.rolling_values[0].sum_24h);
    const planned = try lifecyclePlan(session, 5, 1, 1_020);
    try std.testing.expectEqual(@as(u32, 1), planned.output.active_report_count);
    try std.testing.expectEqual(@as(f64, 4), planned.reports[0].window_24h_value);
    const restored = try state.Session.create(&config);
    defer restored.destroy();
    var restore_rules = ruleReplaceInput(1, 1, 1_020, 1);
    try expectCode(.ok, session_module.replaceRules(restored, &restore_rules, @ptrCast(&rolling), 1));
    const count = planned.output.persistence_operation_count;
    var contaminated = planned.operations;
    for (contaminated[0..count]) |*row| {
        if (row.operation_kind == @intFromEnum(protocol.PersistenceKind.bucket)) row.window_delta_value = 1;
    }
    var restore_input = importInput(2, 2, 1_020, 1, count);
    try expectCode(.invalid_argument, session_module.importPersisted(restored, &restore_input, &contaminated, count));
    try std.testing.expect(!restored.import_ready);
    try expectCode(.ok, session_module.importPersisted(restored, &restore_input, &planned.operations, count));
    const after_restart = try lifecyclePlan(restored, 3, 1, 1_030);
    try std.testing.expectEqual(@as(u32, 1), after_restart.output.active_report_count);
    try std.testing.expectEqual(@as(f64, 4), after_restart.reports[0].window_24h_value);
}

test "complete absence marks history even when stale observation awaits persisted deletion" {
    var fixture = try Fixture.create();
    defer fixture.destroy();
    try fixture.observeComplete(3, 3, 1, 100, &.{fact(1, 60)});
    try fixture.observeComplete(4, 4, 2, 110, &.{fact(2, 70)});
    const index = fixture.session.findReport(target_handle, family_handle).?;
    var unavailable = fact(3, 0);
    unavailable.flags = 0;
    try fixture.observeComplete(5, 5, 3, 40_000, &.{unavailable});
    try std.testing.expect(fixture.session.observations[0].delete_pending);
    try std.testing.expect(fixture.session.reports[index].active);
    try fixture.observeComplete(6, 6, 4, 40_010, &.{});
    try std.testing.expect(!fixture.session.reports[index].active);
    try std.testing.expectEqual(@as(f64, 70), fixture.session.reports[index].current_value);
}

test "import reconciles committed exact and wildcard trust independently of lifecycle and evidence" {
    for ([_]bool{ false, true }) |active| {
        for ([_]bool{ false, true }) |trusted| {
            for ([_]bool{ false, true }) |wildcard| {
                var config = testConfig();
                const session = try state.Session.create(&config);
                defer session.destroy();
                const rules = [_]protocol.RuleInput{rule()};
                var replace = ruleReplaceInput(1, 1, 150, 1);
                try expectCode(.ok, session_module.replaceRules(session, &replace, &rules, 1));
                var report = persistedReportRow();
                report.flags = (if (active) protocol.PersistenceFlags.active else @as(u32, 0)) |
                    (if (!trusted) protocol.PersistenceFlags.trusted else @as(u32, 0));
                report.current_value = 70;
                report.value_sum = 65;
                report.peak_value = 70;
                report.window_delta_value = 4;
                report.secondary_current_value = 6;
                report.sample_count = 2;
                report.active_sample_count = 2;
                var trust_row = persistedTrustRow(71, 5, 1);
                if (wildcard) trust_row.family_handle = protocol.TrustFamily.all;
                const rows = [_]protocol.PersistenceOperation{ persistedMetadataRow(6, 150), report, trust_row };
                const count: u32 = if (trusted) 3 else 2;
                var input = importInput(2, 2, 150, 1, count);
                try expectCode(.ok, session_module.importPersisted(session, &input, &rows, count));
                const restored = session.reports[session.findReport(target_handle, family_handle).?];
                try std.testing.expectEqual(active, restored.active);
                try std.testing.expectEqual(trusted, restored.trusted);
                try std.testing.expectEqual(report.mutation_version, restored.persisted_mutation_version);
                try std.testing.expect(restored.mutation_version > restored.persisted_mutation_version);
                const planned = try lifecyclePlan(session, 3, 1, 150);
                try std.testing.expectEqual(@as(u32, if (active) 1 else 0), planned.output.active_report_count);
                try std.testing.expectEqual(@as(u32, if (active and trusted) 1 else 0), planned.output.trusted_report_count);
                const exported = try findOperation(planned.operations[0..planned.output.persistence_operation_count], .report);
                var expected = report;
                expected.flags = (if (active) protocol.PersistenceFlags.active else @as(u32, 0)) |
                    (if (trusted) protocol.PersistenceFlags.trusted else @as(u32, 0));
                expected.mutation_version = exported.mutation_version;
                expected.session_instance_low = exported.session_instance_low;
                expected.session_instance_high = exported.session_instance_high;
                expected.plan_epoch = exported.plan_epoch;
                expected.slot_index = exported.slot_index;
                try std.testing.expectEqualDeep(expected, exported);
                try std.testing.expectEqual(@as(f64, 70), planned.reports[0].current_value);
                try std.testing.expectEqual(@as(f64, 65), planned.reports[0].average_value);
                try std.testing.expectEqual(@as(f64, 4), planned.reports[0].window_24h_value);
                try std.testing.expectEqual(@as(f64, 6), planned.reports[0].window_7d_value);
            }
        }
    }
}

test "import trust reconciliation exhausts mutations before publishing any semantic state" {
    var config = testConfig();
    const session = try state.Session.create(&config);
    defer session.destroy();
    const rules = [_]protocol.RuleInput{rule()};
    var replace = ruleReplaceInput(1, 1, 150, 1);
    try expectCode(.ok, session_module.replaceRules(session, &replace, &rules, 1));
    var report = persistedReportRow();
    report.flags |= protocol.PersistenceFlags.trusted;
    report.mutation_version = std.math.maxInt(u64) - 1;
    var rows = [_]protocol.PersistenceOperation{ persistedMetadataRow(4, 150), report };
    var input = importInput(2, 2, 150, 1, rows.len);
    const revision = session.state_revision;
    const next_mutation = session.next_mutation_version;
    try expectCode(.out_of_memory, session_module.importPersisted(session, &input, &rows, rows.len));
    try std.testing.expect(!session.import_ready);
    try std.testing.expectEqual(@as(usize, 0), occupiedReports(session));
    try std.testing.expectEqual(revision, session.state_revision);
    try std.testing.expectEqual(next_mutation, session.next_mutation_version);
    try std.testing.expectEqual(@as(u64, 1), session.last_operation_epoch);
    rows[1].mutation_version = 3;
    try expectCode(.ok, session_module.importPersisted(session, &input, &rows, rows.len));
    try std.testing.expect(session.import_ready);
    try std.testing.expect(!session.reports[0].trusted);
    try std.testing.expect(session.reports[0].active);
}

const LifecyclePlan = struct {
    reports: [4]protocol.ReportOutput,
    operations: [32]protocol.PersistenceOperation,
    output: protocol.PlanOutput,
};

fn lifecyclePlan(session: *state.Session, epoch: u64, plan_epoch: u64, utc: i64) !LifecyclePlan {
    var result: LifecyclePlan = undefined;
    var input = planInput(epoch, epoch, plan_epoch, utc, result.reports.len, result.operations.len);
    try expectCode(.ok, session_module.plan(session, &input, &result.reports, result.reports.len, &result.operations, result.operations.len, &result.output));
    return result;
}

test "clear history persists every historical deletion together and leaves active reports and trust untouched" {
    var config = testConfig();
    const fast = clearTestRule();
    const session = try createReadySession(&config, &.{fast});
    defer session.destroy();
    try observeRows(session, sourceSnapshot(3, 3, 1, 100, .complete, 3), &.{ factForTarget(1, 41, 60), factForTarget(1, 42, 70), factForTarget(1, 43, 80) });
    var trust_input = trustCommand(4, 4, .add);
    try expectCode(.ok, session_module.commandTrust(session, &trust_input));
    try settleCurrentPlan(session, 5, 6, 1, 110);
    try observeRows(session, sourceSnapshot(7, 7, 2, 120, .complete, 3), &.{ factForTarget(2, 41, 0), factForTarget(2, 42, 0), factForTarget(2, 43, 90) });
    try settleCurrentPlan(session, 8, 9, 2, 130);
    const active_index = session.findReport(43, family_handle).?;
    const active = session.reports[active_index];
    const trust_index = session.findTrust(41, family_handle).?;
    const trusted = session.trusts[trust_index];
    const old_handle = session.reports[session.findReport(41, family_handle).?].report_handle;
    const observations = try std.testing.allocator.dupe(state.ObservationSlot, session.observations);
    defer std.testing.allocator.free(observations);
    var input = clearHistoryInput(10, 140);
    var removed: u32 = 999;
    try std.testing.expectEqual(@intFromEnum(ResultCode.ok), abi.rm_report_coordinator_clear_history(@ptrCast(session), &input, &removed));
    try std.testing.expectEqual(@as(u32, 2), removed);
    try std.testing.expectEqualDeep(active, session.reports[active_index]);
    try std.testing.expectEqualDeep(trusted, session.trusts[trust_index]);
    try std.testing.expectEqualDeep(observations, session.observations);
    const planned = try lifecyclePlan(session, 11, 3, 150);
    try std.testing.expectEqual(@as(u32, 1), planned.output.report_count);
    try std.testing.expectEqual(@as(u64, 43), planned.reports[0].target_handle);
    var deletes: u32 = 0;
    for (planned.operations[0..planned.output.persistence_operation_count]) |operation| {
        if (operation.operation_kind != @intFromEnum(protocol.PersistenceKind.report)) continue;
        try std.testing.expectEqual(@as(u64, 3), operation.plan_epoch);
        try std.testing.expect((operation.flags & protocol.PersistenceFlags.delete) != 0);
        try std.testing.expect(operation.target_handle == 41 or operation.target_handle == 42);
        deletes += 1;
    }
    try std.testing.expectEqual(@as(u32, 2), deletes);
    input = clearHistoryInput(12, 151);
    try expectCode(.unavailable, session_module.clearHistory(session, &input, &removed));
    try std.testing.expectEqual(@as(u32, 0), removed);
    try settlePlan(session, planned.operations[0..planned.output.persistence_operation_count], 12, 12, 3, 151);
    try std.testing.expect(session.findReport(41, family_handle) == null);
    try std.testing.expect(session.findReport(42, family_handle) == null);
    const settled = try lifecyclePlan(session, 13, 4, 160);
    try std.testing.expectEqual(@as(u32, 1), settled.output.report_count);
    try std.testing.expectEqualDeep(active, session.reports[active_index]);
    try std.testing.expectEqualDeep(trusted, session.trusts[trust_index]);
    try observeRows(session, sourceSnapshot(14, 14, 3, 170, .complete, 2), &.{ factForTarget(3, 41, 100), factForTarget(3, 43, 95) });
    const recreated = session.reports[session.findReport(41, family_handle).?];
    try std.testing.expect(recreated.report_handle > old_handle);
    try std.testing.expect(recreated.active);
    try std.testing.expect(recreated.trusted);
    try std.testing.expectEqual(@as(f64, 100), recreated.current_value);
}

test "clear history failed persistence retries the same deletions and restart restores only committed rows" {
    var config = testConfig();
    const fast = clearTestRule();
    const session = try createReadySession(&config, &.{fast});
    defer session.destroy();
    try observeRows(session, sourceSnapshot(3, 3, 1, 100, .complete, 1), &.{fact(1, 60)});
    try observeRows(session, sourceSnapshot(4, 4, 2, 110, .complete, 0), &.{});
    const baseline = try lifecyclePlan(session, 5, 1, 120);
    const baseline_rows = baseline.operations[0..baseline.output.persistence_operation_count];
    const identity = baseline.reports[0].report_handle;
    try settlePlan(session, baseline_rows, 6, 6, 1, 121);
    var input = clearHistoryInput(7, 130);
    var removed: u32 = 0;
    try expectCode(.ok, session_module.clearHistory(session, &input, &removed));
    try std.testing.expectEqual(@as(u32, 1), removed);
    const first = try lifecyclePlan(session, 8, 2, 140);
    const first_delete = try findOperation(first.operations[0..first.output.persistence_operation_count], .report);
    var feedbacks: [32]protocol.PersistenceFeedback = undefined;
    for (first.operations[0..first.output.persistence_operation_count], feedbacks[0..first.output.persistence_operation_count]) |operation, *feedback| feedback.* = feedbackFor(operation, .failed);
    var feedback_input = persistenceFeedbackBatchInput(9, 9, 2, 141, first.output.persistence_operation_count);
    try expectCode(.ok, session_module.applyFeedback(session, &feedback_input, &feedbacks, first.output.persistence_operation_count));
    try std.testing.expect(session.reports[session.findReport(target_handle, family_handle).?].delete_pending);
    input = clearHistoryInput(10, 142);
    try expectCode(.ok, session_module.clearHistory(session, &input, &removed));
    try std.testing.expectEqual(@as(u32, 0), removed);
    const retry = try lifecyclePlan(session, 11, 3, 150);
    const retry_delete = try findOperation(retry.operations[0..retry.output.persistence_operation_count], .report);
    try std.testing.expectEqual(first_delete.identity_handle, retry_delete.identity_handle);
    try std.testing.expectEqual(first_delete.mutation_version, retry_delete.mutation_version);
    try std.testing.expect((retry_delete.flags & protocol.PersistenceFlags.delete) != 0);
    {
        const restored = try state.Session.create(&config);
        defer restored.destroy();
        var replace = ruleReplaceInput(1, 1, 121, 1);
        try expectCode(.ok, session_module.replaceRules(restored, &replace, @ptrCast(&fast), 1));
        var restore = importInput(2, 2, 121, 1, @intCast(baseline_rows.len));
        try expectCode(.ok, session_module.importPersisted(restored, &restore, baseline_rows.ptr, @intCast(baseline_rows.len)));
        const recovered = try lifecyclePlan(restored, 3, 1, 122);
        try std.testing.expectEqual(@as(u32, 1), recovered.output.report_count);
        try std.testing.expectEqual(identity, recovered.reports[0].report_handle);
        try std.testing.expectEqual(@as(u32, 0), recovered.output.active_report_count);
    }
    try settlePlan(session, retry.operations[0..retry.output.persistence_operation_count], 12, 12, 3, 151);
    const final = try lifecyclePlan(session, 13, 4, 160);
    try std.testing.expectEqual(@as(u32, 0), final.output.report_count);
    {
        const restored = try state.Session.create(&config);
        defer restored.destroy();
        var replace = ruleReplaceInput(1, 1, 151, 1);
        try expectCode(.ok, session_module.replaceRules(restored, &replace, @ptrCast(&fast), 1));
        const rows = [_]protocol.PersistenceOperation{
            try findOperation(retry.operations[0..retry.output.persistence_operation_count], .metadata),
            try findOperation(baseline_rows, .source),
            try findOperation(baseline_rows, .observation),
        };
        var restore = importInput(2, 2, 151, 1, rows.len);
        try expectCode(.ok, session_module.importPersisted(restored, &restore, &rows, rows.len));
        const recovered = try lifecyclePlan(restored, 3, 1, 152);
        try std.testing.expectEqual(@as(u32, 0), recovered.output.report_count);
        try settlePlan(restored, recovered.operations[0..recovered.output.persistence_operation_count], 4, 4, 1, 153);
        try observeRows(restored, sourceSnapshot(5, 5, 3, 160, .complete, 1), &.{fact(2, 70)});
        const recreated = restored.reports[restored.findReport(target_handle, family_handle).?];
        try std.testing.expect(recreated.report_handle > identity);
    }
}

test "clear history validates ABI and command ordering and preflights mutations atomically" {
    var config = testConfig();
    const fast = clearTestRule();
    const session = try createReadySession(&config, &.{fast});
    defer session.destroy();
    try observeRows(session, sourceSnapshot(3, 3, 1, 100, .complete, 2), &.{ factForTarget(1, 41, 60), factForTarget(1, 42, 70) });
    try observeRows(session, sourceSnapshot(4, 4, 2, 110, .complete, 0), &.{});
    const before = try std.testing.allocator.dupe(state.ReportSlot, session.reports);
    defer std.testing.allocator.free(before);
    const revision = session.state_revision;
    const mutation = session.next_mutation_version;
    var removed: u32 = 999;
    for (0..9) |field| {
        var input = clearHistoryInput(5, 120);
        switch (field) {
            0 => input.abi_version = 0x0007_0000,
            1 => input.struct_size -= 1,
            2 => input.configuration_generation += 1,
            3 => input.operation_epoch = 0,
            4 => input.command_monotonic_milliseconds = 0,
            5 => input.command_utc_milliseconds = -1,
            6 => input.valid_mask = 0,
            7 => input.flags = 1,
            8 => input.reserved[0] = 1,
            else => unreachable,
        }
        try expectCode(.abi_mismatch, session_module.clearHistory(session, &input, &removed));
        try std.testing.expectEqual(@as(u32, 0), removed);
        try std.testing.expectEqualDeep(before, session.reports);
    }
    var input = clearHistoryInput(5, 120);
    try std.testing.expectEqual(@intFromEnum(ResultCode.invalid_argument), abi.rm_report_coordinator_clear_history(null, &input, &removed));
    try std.testing.expectEqual(@as(u32, 0), removed);
    try std.testing.expectEqual(@intFromEnum(ResultCode.invalid_argument), abi.rm_report_coordinator_clear_history(@ptrCast(session), null, &removed));
    try std.testing.expectEqual(@intFromEnum(ResultCode.invalid_argument), abi.rm_report_coordinator_clear_history(@ptrCast(session), &input, null));
    session.next_mutation_version = std.math.maxInt(u64) - 2;
    try expectCode(.out_of_memory, session_module.clearHistory(session, &input, &removed));
    try std.testing.expectEqual(@as(u32, 0), removed);
    try std.testing.expectEqualDeep(before, session.reports);
    try std.testing.expectEqual(revision, session.state_revision);
    try std.testing.expectEqual(@as(u64, 4), session.last_operation_epoch);
    session.next_mutation_version = mutation;
    try expectCode(.ok, session_module.clearHistory(session, &input, &removed));
    try std.testing.expectEqual(@as(u32, 2), removed);
    try expectCode(.stale_frame, session_module.clearHistory(session, &input, &removed));
    try std.testing.expectEqual(@as(u32, 0), removed);
}

test "clear history frees capacity only after persistence and old candidates wait for a new observation" {
    var config = testConfig();
    config.maximum_report_count = 1;
    const fast = clearTestRule();
    const session = try createReadySession(&config, &.{fast});
    defer session.destroy();
    try observeRows(session, sourceSnapshot(3, 3, 1, 100, .complete, 1), &.{factForTarget(1, 41, 60)});
    const old = session.reports[0].report_handle;
    try observeRows(session, sourceSnapshot(4, 4, 2, 110, .complete, 2), &.{ factForTarget(2, 41, 0), factForTarget(1, 42, 60) });
    try std.testing.expectEqual(@as(u32, 1), @import("rules.zig").unrecordedReportCount(session));
    try settleCurrentPlan(session, 5, 6, 1, 120);
    var input = clearHistoryInput(7, 130);
    var removed: u32 = 0;
    try expectCode(.ok, session_module.clearHistory(session, &input, &removed));
    const clearing = try lifecyclePlan(session, 8, 2, 140);
    try std.testing.expectEqual(@as(u32, 0), clearing.output.report_count);
    try std.testing.expectEqual(@as(u32, 1), clearing.output.unrecorded_report_count);
    try std.testing.expect(session.findReport(42, family_handle) == null);
    try settlePlan(session, clearing.operations[0..clearing.output.persistence_operation_count], 9, 9, 2, 141);
    const settled = try lifecyclePlan(session, 10, 3, 160);
    try std.testing.expectEqual(@as(u32, 0), settled.output.report_count);
    try std.testing.expectEqual(@as(u32, 1), settled.output.unrecorded_report_count);
    try std.testing.expect(session.findReport(42, family_handle) == null);
    try observeRows(session, sourceSnapshot(11, 11, 3, 170, .complete, 2), &.{ factForTarget(3, 41, 0), factForTarget(2, 42, 70) });
    const evaluated = try lifecyclePlan(session, 12, 4, 180);
    try std.testing.expectEqual(@as(u32, 1), evaluated.output.report_count);
    try std.testing.expectEqual(@as(u32, 0), evaluated.output.unrecorded_report_count);
    try std.testing.expectEqual(@as(u64, 42), evaluated.reports[0].target_handle);
    try std.testing.expect(evaluated.reports[0].report_handle > old);
}

test "clear history persists issued report highwater at identical UTC and imports both lagging and exhausted checkpoints" {
    for ([_]u64{ 0, 500, std.math.maxInt(u64) }) |highwater| {
        var config = testConfig();
        const session = try state.Session.create(&config);
        defer session.destroy();
        const fast = clearTestRule();
        var replace = ruleReplaceInput(1, 1, 150, 1);
        try expectCode(.ok, session_module.replaceRules(session, &replace, @ptrCast(&fast), 1));
        var metadata = persistedMetadataRow(4, 150);
        metadata.report_handle = highwater;
        var report = persistedReportRow();
        report.flags = 0;
        const rows = [_]protocol.PersistenceOperation{ metadata, report };
        var restore = importInput(2, 2, 150, 1, rows.len);
        try expectCode(.ok, session_module.importPersisted(session, &restore, &rows, rows.len));
        const highest = @max(highwater, report.identity_handle);
        try std.testing.expectEqual(if (highest == std.math.maxInt(u64)) @as(u64, 0) else highest + 1, session.next_report_handle);
        var input = clearHistoryInput(3, 150);
        var removed: u32 = 0;
        try expectCode(.ok, session_module.clearHistory(session, &input, &removed));
        try std.testing.expectEqual(@as(u32, 1), removed);
        const clearing = try lifecyclePlan(session, 4, 1, 150);
        const checkpoint = try findOperation(clearing.operations[0..clearing.output.persistence_operation_count], .metadata);
        const deletion = try findOperation(clearing.operations[0..clearing.output.persistence_operation_count], .report);
        try std.testing.expectEqual(highest, checkpoint.report_handle);
        try std.testing.expect(checkpoint.mutation_version > metadata.mutation_version);
        try std.testing.expectEqual(report.identity_handle, deletion.identity_handle);
        try std.testing.expect((deletion.flags & protocol.PersistenceFlags.delete) != 0);
        try std.testing.expectEqual(@as(u32, 2), clearing.output.persistence_operation_count);
        const restarted = try state.Session.create(&config);
        defer restarted.destroy();
        replace = ruleReplaceInput(1, 1, 150, 1);
        try expectCode(.ok, session_module.replaceRules(restarted, &replace, @ptrCast(&fast), 1));
        restore = importInput(2, 2, 150, 1, 1);
        try expectCode(.ok, session_module.importPersisted(restarted, &restore, @ptrCast(&checkpoint), 1));
        if (highest == std.math.maxInt(u64)) {
            try std.testing.expectEqual(@as(?u64, null), restarted.allocateReportHandle());
        } else {
            try observeRows(restarted, sourceSnapshot(3, 3, 1, 160, .complete, 1), &.{fact(1, 70)});
            try std.testing.expect(restarted.reports[0].report_handle > highest);
        }
    }
}

test "clear settlement waits for a real rolling judgment and stale windows cannot recreate candidates" {
    for ([_]bool{ false, true }) |stale| {
        var config = testConfig();
        config.maximum_report_count = 1;
        var rolling = clearTestRule();
        rolling.flags = protocol.RuleFlags.rolling;
        rolling.metric_selector = @intFromEnum(protocol.MetricSelector.sum_24h);
        rolling.activation_threshold = 1;
        rolling.clear_threshold = 0.5;
        const session = try createReadySession(&config, &.{rolling});
        defer session.destroy();
        const first = rollingFact(1, 4);
        var candidate = first;
        candidate.target_handle = 42;
        try observeRows(session, sourceSnapshot(3, 3, 1, 1_000, .complete, 2), &.{ first, candidate });
        candidate.fact_sequence = 2;
        candidate.delta_value = 6;
        try observeRows(session, sourceSnapshot(4, 4, 2, 2_000, .complete, 1), &.{candidate});
        try settleCurrentPlan(session, 5, 6, 1, 2_001);
        var input = clearHistoryInput(7, 2_010);
        var removed: u32 = 0;
        try expectCode(.ok, session_module.clearHistory(session, &input, &removed));
        const clearing = try lifecyclePlan(session, 8, 2, 2_020);
        try std.testing.expectEqual(@as(u32, 0), clearing.output.report_count);
        try settlePlan(session, clearing.operations[0..clearing.output.persistence_operation_count], 9, 9, 2, 2_021);
        const settled = try lifecyclePlan(session, 10, 3, 2_030);
        try std.testing.expectEqual(@as(u32, 0), settled.output.report_count);
        const evaluated = try lifecyclePlan(session, 11, 4, if (stale) 40_000 else 4_001);
        try std.testing.expectEqual(@as(u32, if (stale) 0 else 1), evaluated.output.report_count);
        try std.testing.expectEqual(@as(u32, 0), evaluated.output.unrecorded_report_count);
        if (!stale) {
            try std.testing.expectEqual(@as(u64, 42), evaluated.reports[0].target_handle);
            try std.testing.expectEqual(@as(f64, 6), evaluated.reports[0].window_24h_value);
        }
    }
}

test "report capacity expansion measures actual initial and full configured allocations" {
    var initial: [2]u64 = undefined;
    var full: [2]u64 = undefined;
    for (0..2) |index| {
        const multiplier: u32 = if (index == 0) 1 else 4;
        var config = testConfig();
        config.maximum_source_count = 16;
        config.maximum_rule_count = 64;
        config.maximum_observation_count = 2048 * multiplier;
        config.maximum_report_count = 1024 * multiplier;
        config.maximum_trust_count = 2048 * multiplier;
        config.maximum_bucket_count = 21632 * multiplier;
        config.maximum_persistence_operation_count = 32768 * multiplier;
        config.maximum_report_output_count = 1024 * multiplier;
        config.maximum_rolling_observation_count = 128 * multiplier;
        config.source_index_capacity = 32;
        config.rule_index_capacity = 128;
        config.observation_index_capacity = 4096 * multiplier;
        config.report_index_capacity = 2048 * multiplier;
        config.trust_index_capacity = 4096 * multiplier;
        config.bucket_index_capacity = 65536 * multiplier;
        config.planned_persistence_index_capacity = 65536 * multiplier;
        config.bucket_width_milliseconds = 3_600_000;
        config.window_24h_milliseconds = 86_400_000;
        config.window_7d_milliseconds = 604_800_000;
        config.maximum_future_skew_milliseconds = 300_000;
        config.default_stale_after_milliseconds = 900_000;
        config.default_retention_milliseconds = 604_800_000;
        config.metadata_checkpoint_interval_milliseconds = 3_600_000;
        config.resident_byte_budget = 128 * 1024 * 1024;
        const session = try state.Session.create(&config);
        defer session.destroy();
        initial[index] = session.workspaceCapacity().resident_byte_count;
        try expectCode(.ok, session.ensureShape(config, .report_reports));
        const capacity = session.workspaceCapacity();
        full[index] = capacity.resident_byte_count;
        try std.testing.expectEqual(config.maximum_source_count, capacity.source_capacity);
        try std.testing.expectEqual(config.maximum_rule_count, capacity.rule_capacity);
        try std.testing.expectEqual(config.maximum_observation_count, capacity.observation_capacity);
        try std.testing.expectEqual(config.maximum_report_count, capacity.report_capacity);
        try std.testing.expectEqual(config.maximum_trust_count, capacity.trust_capacity);
        try std.testing.expectEqual(config.maximum_bucket_count, capacity.bucket_capacity);
        try std.testing.expectEqual(config.maximum_persistence_operation_count, capacity.persistence_operation_capacity);
        try std.testing.expectEqual(try state.requiredResidentBytes(&config), full[index]);
    }
    try std.testing.expectEqual(initial[0], initial[1]);
    try std.testing.expect(full[1] > full[0]);
    std.debug.print("REPORT_CAPACITY_MEMORY oldInitial={d} newInitial={d} oldFull={d} newFull={d} difference={d}\n", .{ initial[0], initial[1], full[0], full[1], full[1] - full[0] });
}

fn clearTestRule() protocol.RuleInput {
    var value = rule();
    value.required_consecutive_hits = 1;
    value.required_consecutive_misses = 1;
    return value;
}

fn clearHistoryInput(epoch: u64, utc: i64) protocol.ClearHistoryInput {
    var input = std.mem.zeroes(protocol.ClearHistoryInput);
    input.abi_version = protocol.abi_version;
    input.struct_size = @sizeOf(protocol.ClearHistoryInput);
    input.configuration_generation = 1;
    input.operation_epoch = epoch;
    input.command_monotonic_milliseconds = epoch;
    input.command_utc_milliseconds = utc;
    input.valid_mask = protocol.ClearHistoryValid.required;
    return input;
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

// Mirror the managed store: upsert by kind and identity, remove on delete.
fn storeRows(
    table: []protocol.PersistenceOperation,
    count: *usize,
    rows: []const protocol.PersistenceOperation,
) !void {
    for (rows) |row| {
        var index: usize = 0;
        while (index < count.* and (table[index].operation_kind != row.operation_kind or
            table[index].identity_handle != row.identity_handle)) : (index += 1)
        {}
        if ((row.flags & protocol.PersistenceFlags.delete) != 0) {
            if (index < count.*) {
                count.* -= 1;
                table[index] = table[count.*];
            }
            continue;
        }
        if (index == count.*) {
            if (count.* == table.len) return error.PersistedTableFull;
            count.* += 1;
        }
        table[index] = row;
    }
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

const CheckpointRows = struct {
    source: usize = 0,
    observation: usize = 0,
    bucket: usize = 0,
    other: usize = 0,
};

const CheckpointPlan = struct {
    rows: CheckpointRows,
    flags: u64,
};

// Drives observe -> plan -> persisted feedback and keeps the durable table.
const CheckpointDriver = struct {
    session: *state.Session,
    table: [32]protocol.PersistenceOperation = undefined,
    table_count: usize = 0,
    epoch: u64 = 3,
    plan_epoch: u64 = 1,
    snapshot_epoch: u64 = 1,

    fn observe(self: *CheckpointDriver, utc: i64, facts: []const protocol.FactInput) !void {
        try observeRows(self.session, sourceSnapshot(
            self.epoch,
            self.epoch,
            self.snapshot_epoch,
            utc,
            .complete,
            @intCast(facts.len),
        ), facts);
        self.epoch += 1;
        self.snapshot_epoch += 1;
    }

    fn observeUnavailable(self: *CheckpointDriver, utc: i64) !void {
        try observeRows(self.session, sourceSnapshot(
            self.epoch,
            self.epoch,
            self.snapshot_epoch,
            utc,
            .unavailable,
            0,
        ), &.{});
        self.epoch += 1;
        self.snapshot_epoch += 1;
    }

    fn plan(self: *CheckpointDriver, utc: i64, input_flags: u64) !CheckpointPlan {
        var result: LifecyclePlan = undefined;
        var input = planInput(self.epoch, self.epoch, self.plan_epoch, utc, result.reports.len, result.operations.len);
        input.flags = input_flags;
        try expectCode(.ok, session_module.plan(self.session, &input, &result.reports, result.reports.len, &result.operations, result.operations.len, &result.output));
        self.epoch += 1;
        const rows = result.operations[0..result.output.persistence_operation_count];
        var counts: CheckpointRows = .{};
        for (rows) |row| switch (@as(protocol.PersistenceKind, @enumFromInt(row.operation_kind))) {
            .source => counts.source += 1,
            .observation => counts.observation += 1,
            .bucket => counts.bucket += 1,
            .metadata => {},
            else => counts.other += 1,
        };
        if (rows.len != 0) {
            try storeRows(&self.table, &self.table_count, rows);
            try settlePlan(self.session, rows, self.epoch, self.epoch, self.plan_epoch, utc + 1);
            self.epoch += 1;
        }
        self.plan_epoch += 1;
        return .{ .rows = counts, .flags = result.output.flags };
    }
};

fn expectCheckpointRows(expected: CheckpointRows, actual: CheckpointPlan) !void {
    try std.testing.expectEqualDeep(expected, actual.rows);
}

fn checkpointRule() protocol.RuleInput {
    var value = rule();
    // Rolling so every fact also changes a bucket; never activates.
    value.flags = protocol.RuleFlags.rolling;
    value.metric_selector = @intFromEnum(protocol.MetricSelector.sum_24h);
    value.activation_threshold = 1_000_000;
    value.clear_threshold = 999_999;
    return value;
}

test "counter-only rows wait for the observation checkpoint and go together when it is due" {
    var config = testConfig();
    config.observation_checkpoint_interval_milliseconds = 1_000;
    config.metadata_checkpoint_interval_milliseconds = 1_000_000;
    const session = try createReadySession(&config, &.{checkpointRule()});
    defer session.destroy();
    var driver: CheckpointDriver = .{ .session = session };
    var sequence: u64 = 1;

    // New rows are semantic: they go on the first plan.
    try driver.observe(100, &.{rollingFact(sequence, 1)});
    try expectCheckpointRows(.{ .source = 1, .observation = 1, .bucket = 1 }, try driver.plan(101, 0));

    // Growing a miss streak, cursors and bucket sums are counter-only.
    var utc: i64 = 110;
    while (utc <= 130) : (utc += 10) {
        sequence += 1;
        try driver.observe(utc, &.{rollingFact(sequence, 1)});
        const planned = try driver.plan(utc + 1, 0);
        try expectCheckpointRows(.{}, planned);
        try std.testing.expectEqual(@as(u64, 0), planned.flags & protocol.PlanFlags.counter_checkpoint);
    }

    // A sealed bucket and a new bucket go before the checkpoint; the
    // observation and source keep waiting.
    sequence += 1;
    try driver.observe(1_010, &.{rollingFact(sequence, 1)});
    try expectCheckpointRows(.{ .bucket = 2 }, try driver.plan(1_011, 0));

    // The checkpoint is due 1000 ms after import (logical 100): all
    // counter-only rows go together.
    sequence += 1;
    try driver.observe(1_100, &.{rollingFact(sequence, 1)});
    const due = try driver.plan(1_101, 0);
    try expectCheckpointRows(.{ .source = 1, .observation = 1, .bucket = 1 }, due);
    try std.testing.expect((due.flags & protocol.PlanFlags.counter_checkpoint) != 0);

    sequence += 1;
    try driver.observe(1_110, &.{rollingFact(sequence, 1)});
    try expectCheckpointRows(.{}, try driver.plan(1_111, 0));

    // Changing the source status is semantic and goes on the next plan.
    try driver.observeUnavailable(1_120);
    const unavailable = try driver.plan(1_121, 0);
    try std.testing.expectEqual(@as(usize, 1), unavailable.rows.source);
}

test "a broken streak goes on the next plan while a growing one waits" {
    var config = testConfig();
    config.observation_checkpoint_interval_milliseconds = 1_000;
    config.metadata_checkpoint_interval_milliseconds = 1_000_000;
    var rule_row = rule();
    rule_row.required_consecutive_hits = 3;
    const session = try createReadySession(&config, &.{rule_row});
    defer session.destroy();
    var driver: CheckpointDriver = .{ .session = session };

    try driver.observe(100, &.{fact(1, 0)});
    try expectCheckpointRows(.{ .source = 1, .observation = 1 }, try driver.plan(101, 0));
    try driver.observe(110, &.{fact(2, 0)});
    try expectCheckpointRows(.{}, try driver.plan(111, 0));
    // The miss streak breaks: the stored row must not join both streaks.
    try driver.observe(120, &.{fact(3, 60)});
    try expectCheckpointRows(.{ .observation = 1 }, try driver.plan(121, 0));
    try driver.observe(130, &.{fact(4, 60)});
    try expectCheckpointRows(.{}, try driver.plan(131, 0));
    // The third hit activates the observation and creates a report.
    try driver.observe(140, &.{fact(5, 60)});
    const activated = try driver.plan(141, 0);
    try std.testing.expectEqual(@as(usize, 1), activated.rows.observation);
    try std.testing.expect(activated.rows.other != 0);
}

test "observation checkpoint interval zero writes every counter change on each plan" {
    var config = testConfig();
    try std.testing.expectEqual(@as(u64, 0), config.observation_checkpoint_interval_milliseconds);
    const session = try createReadySession(&config, &.{checkpointRule()});
    defer session.destroy();
    var driver: CheckpointDriver = .{ .session = session };
    var sequence: u64 = 1;
    var utc: i64 = 100;
    while (utc <= 140) : (utc += 10) {
        try driver.observe(utc, &.{rollingFact(sequence, 1)});
        const planned = try driver.plan(utc + 1, 0);
        try expectCheckpointRows(.{ .source = 1, .observation = 1, .bucket = 1 }, planned);
        try std.testing.expect((planned.flags & protocol.PlanFlags.counter_checkpoint) != 0);
        sequence += 1;
    }
}

test "flush writes pending counters before the checkpoint and restart restores the live rows" {
    var config = testConfig();
    config.observation_checkpoint_interval_milliseconds = 1_000;
    config.metadata_checkpoint_interval_milliseconds = 1_000_000;
    const session = try createReadySession(&config, &.{checkpointRule()});
    defer session.destroy();
    var driver: CheckpointDriver = .{ .session = session };
    var sequence: u64 = 1;
    var utc: i64 = 100;
    while (utc <= 140) : (utc += 10) {
        try driver.observe(utc, &.{rollingFact(sequence, 1)});
        _ = try driver.plan(utc + 1, 0);
        sequence += 1;
    }
    const flushed = try driver.plan(151, protocol.PlanInputFlags.flush_counters);
    try expectCheckpointRows(.{ .source = 1, .observation = 1, .bucket = 1 }, flushed);
    try std.testing.expect((flushed.flags & protocol.PlanFlags.counter_checkpoint) != 0);
    // The flush is a checkpoint: the interval restarts from it.
    try driver.observe(160, &.{rollingFact(sequence, 1)});
    try expectCheckpointRows(.{}, try driver.plan(161, 0));
    try expectCheckpointRows(.{ .source = 1, .observation = 1, .bucket = 1 }, try driver.plan(171, protocol.PlanInputFlags.flush_counters));

    const restored = try state.Session.create(&config);
    defer restored.destroy();
    var rule_row = checkpointRule();
    var replace = ruleReplaceInput(1, 1, 200, 1);
    try expectCode(.ok, session_module.replaceRules(restored, &replace, @ptrCast(&rule_row), 1));
    var import_input = importInput(2, 2, 200, 1, @intCast(driver.table_count));
    try expectCode(.ok, session_module.importPersisted(restored, &import_input, &driver.table, @intCast(driver.table_count)));
    const live = session.observations[session.findObservation(source_handle, coverage_scope_handle, rule_handle, target_handle).?];
    const back = restored.observations[restored.findObservation(source_handle, coverage_scope_handle, rule_handle, target_handle).?];
    try std.testing.expectEqual(live.sample_count, back.sample_count);
    try std.testing.expectEqual(live.value_sum, back.value_sum);
    try std.testing.expectEqual(live.consecutive_misses, back.consecutive_misses);
    try std.testing.expectEqual(live.last_fact_sequence, back.last_fact_sequence);
    try std.testing.expectEqual(live.last_observed_at_utc_ms, back.last_observed_at_utc_ms);
    const live_bucket = session.buckets[0];
    const back_bucket = restored.buckets[0];
    try std.testing.expectEqual(live_bucket.delta_value, back_bucket.delta_value);
    try std.testing.expectEqual(live_bucket.sample_duration_milliseconds, back_bucket.sample_duration_milliseconds);
}

test "a failed checkpoint batch keeps the counter checkpoint due" {
    var config = testConfig();
    config.observation_checkpoint_interval_milliseconds = 1_000;
    config.metadata_checkpoint_interval_milliseconds = 1_000_000;
    const session = try createReadySession(&config, &.{checkpointRule()});
    defer session.destroy();
    var driver: CheckpointDriver = .{ .session = session };
    try driver.observe(100, &.{rollingFact(1, 1)});
    _ = try driver.plan(101, 0);
    try driver.observe(110, &.{rollingFact(2, 1)});

    var result: LifecyclePlan = undefined;
    var input = planInput(driver.epoch, driver.epoch, driver.plan_epoch, 1_101, result.reports.len, result.operations.len);
    try expectCode(.ok, session_module.plan(session, &input, &result.reports, result.reports.len, &result.operations, result.operations.len, &result.output));
    driver.epoch += 1;
    const rows = result.operations[0..result.output.persistence_operation_count];
    try std.testing.expect(rows.len != 0);
    var feedbacks: [32]protocol.PersistenceFeedback = undefined;
    for (rows, feedbacks[0..rows.len]) |row, *feedback| feedback.* = feedbackFor(row, .failed);
    var feedback_input = persistenceFeedbackBatchInput(driver.epoch, driver.epoch, driver.plan_epoch, 1_102, @intCast(rows.len));
    try expectCode(.ok, session_module.applyFeedback(session, &feedback_input, feedbacks[0..rows.len].ptr, @intCast(rows.len)));
    driver.epoch += 1;
    driver.plan_epoch += 1;
    try std.testing.expectEqual(@as(i64, 100), session.counter_checkpoint_utc_ms);

    const retry = try driver.plan(1_111, 0);
    try expectCheckpointRows(.{ .source = 1, .observation = 1, .bucket = 1 }, retry);
    try std.testing.expectEqual(@as(i64, 1_111), session.counter_checkpoint_utc_ms);
}
