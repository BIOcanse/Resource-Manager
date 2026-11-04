const std = @import("std");
const protocol = @import("protocol.zig");
const state = @import("state.zig");
const scratch = @import("scratch.zig");
const coordinator = @import("session.zig");
const corpus = @import("golden_corpus.zig");
const ingest = @import("ingest.zig");

test "smart staged scratch query reports actual typed extents and no session allocation" {
    const config = corpus.explicitConfig(protocol.FeatureFlags.process_policy);
    const session = try coordinator.create(&config);
    defer coordinator.destroy(session);
    const layout = scratch.Layout.init(&config);
    var r: scratch.Requirements = undefined;
    try std.testing.expectEqual(protocol.Status.ok, coordinator.queryScratchLayout(session, &r));
    try std.testing.expectEqual(@as(usize, 80), @sizeOf(scratch.Requirements));
    try std.testing.expectEqual(@as(u32, @sizeOf(state.StagedProcess)), r.process_size);
    try std.testing.expectEqual(@as(u32, @sizeOf(state.StagedSoftware)), r.software_size);
    try std.testing.expectEqual(@as(u32, @sizeOf(state.StagedGpu)), r.gpu_size);
    try std.testing.expectEqual(@as(u32, @sizeOf(state.StagedSoftwareGpu)), r.software_gpu_size);
    try std.testing.expectEqual(config.max_processes, r.process_count);
    try std.testing.expectEqual(config.max_software_groups, r.software_count);
    try std.testing.expectEqual(config.max_gpu_states, r.gpu_count);
    try std.testing.expectEqual(@as(u64, try state.hashIndexCapacity(config.max_processes)), r.process_index_count);
    try std.testing.expectEqual(layout.software_gpu_index + 4 * r.gpu_index_count, r.byte_count);
    try expectUnbound(session);
    try std.testing.expectEqual(protocol.Status.ok, coordinator.reset(session));
}

test "smart staged scratch rejects invalid extents before mutation and unbinds plan failures" {
    const config = corpus.explicitConfig(protocol.FeatureFlags.process_policy);
    const session = try coordinator.create(&config);
    defer coordinator.destroy(session);
    const r = scratch.Layout.initCounts(0, 0, 0).requirements;
    const bytes = try std.heap.page_allocator.alloc(u8, @intCast(r.byte_count + r.alignment));
    defer std.heap.page_allocator.free(bytes);
    var actions: [corpus.default_action_capacity]protocol.Action = undefined;
    var snapshot: protocol.Snapshot = undefined;
    var input = corpus.cycle(&config, 1, 1000, 0, true);
    const revision = session.state_revision;
    try std.testing.expectEqual(protocol.Status.invalid_argument, coordinator.plan(session, &input, null, 0, &actions, actions.len, null, r.byte_count, &r, &snapshot));
    try std.testing.expectEqual(protocol.Status.invalid_argument, coordinator.plan(session, &input, null, 0, &actions, actions.len, bytes.ptr, r.byte_count - 1, &r, &snapshot));
    try std.testing.expectEqual(protocol.Status.invalid_argument, coordinator.plan(session, &input, null, 0, &actions, actions.len, bytes[1..].ptr, r.byte_count, &r, &snapshot));
    try std.testing.expectEqual(protocol.Status.invalid_argument, coordinator.plan(session, &input, null, 0, @ptrCast(@alignCast(bytes.ptr)), actions.len, bytes.ptr, r.byte_count, &r, &snapshot));
    // A plan binds only the shape its measure reported, never a larger guess.
    var other = r;
    other.process_count = 1;
    try std.testing.expectEqual(protocol.Status.invalid_argument, coordinator.plan(session, &input, null, 0, &actions, actions.len, bytes.ptr, r.byte_count, &other, &snapshot));
    other = scratch.Layout.initCounts(config.max_processes + 1, 0, 0).requirements;
    try std.testing.expectEqual(protocol.Status.invalid_argument, coordinator.plan(session, &input, null, 0, &actions, actions.len, bytes.ptr, r.byte_count, &other, &snapshot));
    input.abi_version +%= 1;
    try std.testing.expectEqual(protocol.Status.abi_mismatch, coordinator.plan(session, &input, null, 0, &actions, actions.len, bytes.ptr, r.byte_count, &r, &snapshot));
    try std.testing.expectEqual(revision, session.state_revision);
    try expectUnbound(session);
}

test "smart staged scratch refills all rows and preserves reservations feedback and snapshots after reuse" {
    const config = corpus.explicitConfig(protocol.FeatureFlags.process_policy);
    const session = try coordinator.create(&config);
    defer coordinator.destroy(session);
    const base = corpus.processFact(0, 101, 1001, 11, 111, 20, .adapted, 0, protocol.ProcessGrade.normal, 0);
    const rows = [_]protocol.InputRow{ base, corpus.metricFact(base, 1, .cpu_usage_percent, 50, 0), corpus.metricFact(base, 2, .gpu_usage_percent, 25, 1), corpus.metricFact(base, 3, .vram_usage_percent, 20, 1) };
    // One process, one software and one GPU device: each column holds one row.
    const layout = scratch.Layout.initCounts(1, 1, 1);
    const r = layout.requirements;
    const bytes = try std.heap.page_allocator.alloc(u8, @intCast(r.byte_count));
    defer std.heap.page_allocator.free(bytes);
    var actions: [corpus.default_action_capacity]protocol.Action = undefined;
    var snapshot: protocol.Snapshot = undefined;
    var sequence: u64 = 1;
    while (sequence <= 3) : (sequence += 1) {
        @memset(bytes, 0xa5);
        const input = corpus.cycle(&config, sequence, @as(i64, @intCast(sequence)) * 1000, rows.len, sequence == 1);
        var measured: scratch.Requirements = undefined;
        var action_rows: u32 = 0;
        try measure(session, &input, &rows, &measured, &action_rows);
        try std.testing.expectEqualDeep(r, measured);
        try std.testing.expectEqual(@as(u32, 1), action_rows);
        try std.testing.expectEqual(protocol.Status.ok, coordinator.plan(session, &input, &rows, rows.len, &actions, actions.len, bytes.ptr, r.byte_count, &r, &snapshot));
        try expectUnbound(session);
        const staged_gpu: *const state.StagedGpu = @ptrCast(@alignCast(&bytes[@intCast(layout.gpus)]));
        const staged_software_gpu: *const state.StagedSoftwareGpu = @ptrCast(@alignCast(&bytes[@intCast(layout.software_gpus)]));
        try std.testing.expect(staged_gpu.occupied);
        try std.testing.expect(staged_software_gpu.occupied);
    }
    var feedbacks: [corpus.default_action_capacity]protocol.Feedback = undefined;
    var count: u32 = 0;
    for (actions[0..snapshot.action_count]) |action| {
        if ((action.flags & protocol.ActionFlags.requires_feedback) == 0) continue;
        feedbacks[count] = corpus.feedbackFor(action, .succeeded, 3100);
        count += 1;
    }
    try std.testing.expect(count > 0);
    @memset(bytes, 0xdd); // Native authoritative state must not read borrowed staging now.
    try std.testing.expectEqual(protocol.Status.ok, coordinator.applyFeedback(session, &feedbacks, count, feedbacks.len, &snapshot));
    var snapshots: [corpus.default_process_capacity + corpus.default_software_capacity]protocol.SnapshotRow = undefined;
    try std.testing.expectEqual(protocol.Status.ok, coordinator.getSnapshot(session, &snapshot, &snapshots, snapshots.len));
    try std.testing.expect(snapshot.snapshot_row_count > 0);
    try std.testing.expectEqual(@as(u32, 0), snapshot.inflight_count);
    try expectUnbound(session);
    // Successful feedback retains owned effects, so Reset must still refuse.
    try std.testing.expectEqual(protocol.Status.unavailable, coordinator.reset(session));
}

test "smart row sized staging accepts actual candidate capacity and rejects undersized actions before mutation" {
    const config = corpus.explicitConfig(protocol.FeatureFlags.process_policy);
    const session = try coordinator.create(&config);
    defer coordinator.destroy(session);
    const base = corpus.processFact(0, 101, 1001, 11, 111, 20, .general_application, 0, protocol.ProcessGrade.normal, 0);
    const second = corpus.processFact(1, 102, 1002, 12, 112, 20, .general_application, 0, protocol.ProcessGrade.normal, 0);
    const rows = [_]protocol.InputRow{ corpus.metricFact(base, 0, .cpu_usage_percent, 50, 0), corpus.metricFact(second, 1, .cpu_usage_percent, 50, 0) };
    var input = corpus.cycle(&config, 1, 1000, rows.len, true);
    var requirements: scratch.Requirements = undefined;
    var action_rows: u32 = 0;
    try measure(session, &input, &rows, &requirements, &action_rows);
    try std.testing.expectEqual(@as(u32, 2), requirements.process_count);
    try std.testing.expectEqual(@as(u32, 2), requirements.software_count);
    try std.testing.expectEqual(@as(u32, 0), requirements.gpu_count);
    try std.testing.expectEqual(@as(u32, 2), action_rows);
    try std.testing.expect(requirements.byte_count < scratch.Layout.init(&config).requirements.byte_count);
    const bytes = try std.heap.page_allocator.alloc(u8, @intCast(requirements.byte_count));
    defer std.heap.page_allocator.free(bytes);
    var actions: [2]protocol.Action = undefined;
    var snapshot: protocol.Snapshot = undefined;
    input.action_capacity = 1;
    const revision = session.state_revision;
    try std.testing.expectEqual(protocol.Status.buffer_too_small, coordinator.plan(session, &input, &rows, rows.len, &actions, 1, bytes.ptr, requirements.byte_count, &requirements, &snapshot));
    try std.testing.expectEqual(revision, session.state_revision);
    try std.testing.expectEqual(@as(u32, 0), session.last_cycle_sequence);
    input.action_capacity = actions.len;
    try std.testing.expectEqual(protocol.Status.ok, coordinator.plan(session, &input, &rows, rows.len, &actions, actions.len, bytes.ptr, requirements.byte_count, &requirements, &snapshot));
    try std.testing.expectEqual(@as(u32, 2), snapshot.action_count);
    var sequence: u64 = 2;
    while (sequence <= 3) : (sequence += 1) {
        input = corpus.cycle(&config, sequence, @intCast(sequence * 1000), rows.len, false);
        input.action_capacity = actions.len;
        try std.testing.expectEqual(protocol.Status.ok, coordinator.plan(session, &input, &rows, rows.len, &actions, actions.len, bytes.ptr, requirements.byte_count, &requirements, &snapshot));
    }
    try std.testing.expect(snapshot.inflight_count > 0);
    const inflight = session.inflight_count;
    const reserved_revision = session.state_revision;
    input = corpus.cycle(&config, 4, 100_000, rows.len, false);
    try measure(session, &input, &rows, &requirements, &action_rows);
    try std.testing.expectEqual(@as(u32, 2), action_rows);
    input.action_capacity = 1;
    try std.testing.expectEqual(protocol.Status.buffer_too_small, coordinator.plan(session, &input, &rows, rows.len, &actions, 1, bytes.ptr, requirements.byte_count, &requirements, &snapshot));
    try std.testing.expectEqual(reserved_revision, session.state_revision);
    try std.testing.expectEqual(inflight, session.inflight_count);
    try expectUnbound(session);
}

test "smart measure reports each staging column exactly as staging fills it" {
    const config = corpus.explicitConfig(protocol.FeatureFlags.process_policy);
    const session = try coordinator.create(&config);
    defer coordinator.destroy(session);
    // Three processes, two shared software keys, two GPU devices for one
    // process (usage and VRAM on device 0 are one GPU state).
    const a = corpus.processFact(0, 101, 1001, 11, 111, 20, .general_application, 0, protocol.ProcessGrade.normal, 0);
    const b = corpus.processFact(5, 102, 1001, 12, 112, 20, .general_application, 0, protocol.ProcessGrade.normal, 0);
    const c = corpus.processFact(7, 103, 1003, 13, 113, 20, .general_application, 0, protocol.ProcessGrade.normal, 0);
    const rows = [_]protocol.InputRow{
        a,
        corpus.metricFact(a, 1, .cpu_usage_percent, 50, 0),
        corpus.metricFact(a, 2, .gpu_usage_percent, 25, 0),
        corpus.metricFact(a, 3, .vram_usage_percent, 20, 0),
        corpus.metricFact(a, 4, .gpu_usage_percent, 10, 1),
        b,
        corpus.metricFact(b, 6, .cpu_usage_percent, 30, 0),
        c,
    };
    const input = corpus.cycle(&config, 1, 1000, rows.len, true);
    const revision = session.state_revision;
    var requirements: scratch.Requirements = undefined;
    var action_rows: u32 = 0;
    try measure(session, &input, &rows, &requirements, &action_rows);
    try std.testing.expectEqual(revision, session.state_revision);
    try expectUnbound(session);
    try std.testing.expectEqual(@as(u32, 3), requirements.process_count);
    try std.testing.expectEqual(@as(u32, 2), requirements.software_count);
    try std.testing.expectEqual(@as(u32, 2), requirements.gpu_count);
    try std.testing.expectEqual(@as(u32, 3), action_rows);
    try std.testing.expectEqualDeep(scratch.Layout.initCounts(3, 2, 2).requirements, requirements);
    const bytes = try std.heap.page_allocator.alloc(u8, @intCast(requirements.byte_count));
    defer std.heap.page_allocator.free(bytes);
    // Staging fills every measured row and needs no more.
    const layout = scratch.Layout.initMeasured(&config, &requirements).?;
    try std.testing.expect(layout.bind(session, bytes.ptr, requirements.byte_count));
    try std.testing.expectEqual(protocol.Status.ok, ingest.stage(session, &input, &rows, rows.len));
    try std.testing.expectEqual(@as(u32, 3), session.staged_process_count);
    try std.testing.expectEqual(@as(u32, 2), session.staged_software_count);
    try std.testing.expectEqual(@as(u32, 2), session.staged_gpu_count);
    try std.testing.expectEqual(@as(u32, 2), session.staged_software_gpu_count);
    scratch.unbind(session);
    // One row fewer in any column is too small for the same facts.
    for ([_][3]u32{ .{ 2, 2, 2 }, .{ 3, 1, 2 }, .{ 3, 2, 1 } }) |counts| {
        const short = scratch.Layout.initCounts(counts[0], counts[1], counts[2]);
        try std.testing.expect(short.bind(session, bytes.ptr, short.requirements.byte_count));
        try std.testing.expectEqual(protocol.Status.capacity_exceeded, ingest.stage(session, &input, &rows, rows.len));
        scratch.unbind(session);
    }
    try expectUnbound(session);
}

test "smart measure rejects short tables and oversize input before reading rows" {
    var config = corpus.explicitConfig(protocol.FeatureFlags.process_policy);
    config.max_input_rows = 8;
    const session = try coordinator.create(&config);
    defer coordinator.destroy(session);
    var table_count: u64 = 0;
    try std.testing.expectEqual(protocol.Status.ok, coordinator.queryMeasureTable(session, 0, &table_count));
    try std.testing.expectEqual(@as(u64, try state.hashIndexCapacity(1)), table_count);
    try std.testing.expectEqual(protocol.Status.ok, coordinator.queryMeasureTable(session, 5, &table_count));
    try std.testing.expectEqual(@as(u64, try state.hashIndexCapacity(5)), table_count);
    try std.testing.expectEqual(protocol.Status.capacity_exceeded, coordinator.queryMeasureTable(session, 9, &table_count));
    const base = corpus.processFact(0, 101, 1001, 11, 111, 20, .general_application, 0, protocol.ProcessGrade.normal, 0);
    var rows: [5]protocol.InputRow = undefined;
    for (&rows, 0..) |*row, i| row.* = corpus.metricFact(base, @intCast(i), .cpu_usage_percent, 10, 0);
    var table: [32]u32 = undefined;
    var requirements: scratch.Requirements = undefined;
    var action_rows: u32 = 0;
    var input = corpus.cycle(&config, 1, 1000, rows.len, true);
    try std.testing.expectEqual(protocol.Status.buffer_too_small, coordinator.measurePlan(session, &input, &rows, rows.len, &table, table_count - 1, &requirements, &action_rows));
    try std.testing.expectEqual(protocol.Status.invalid_argument, coordinator.measurePlan(session, &input, &rows, rows.len, &table, table_count + 1, &requirements, &action_rows));
    try std.testing.expectEqual(protocol.Status.invalid_argument, coordinator.measurePlan(session, &input, &rows, rows.len, null, table_count, &requirements, &action_rows));
    try std.testing.expectEqual(protocol.Status.buffer_too_small, coordinator.measurePlan(session, &input, &rows, rows.len - 1, &table, table_count, &requirements, &action_rows));
    try std.testing.expectEqual(protocol.Status.invalid_argument, coordinator.measurePlan(session, &input, &rows, rows.len, @ptrCast(@alignCast(&rows)), table_count, &requirements, &action_rows));
    input.input_count = 9;
    try std.testing.expectEqual(protocol.Status.capacity_exceeded, coordinator.measurePlan(session, &input, &rows, rows.len, &table, table_count, &requirements, &action_rows));
    input.input_count = rows.len;
    try std.testing.expectEqual(protocol.Status.ok, coordinator.measurePlan(session, &input, &rows, rows.len, &table, table_count, &requirements, &action_rows));
    try std.testing.expectEqual(@as(u32, 1), requirements.process_count);
    try std.testing.expectEqual(@as(u32, 1), action_rows);
    try expectUnbound(session);
}

test "smart measured action rows grow with a later round and match full-capacity planning" {
    var config = corpus.explicitConfig(protocol.FeatureFlags.process_policy);
    config.required_consecutive_decisions = 1;
    const exact = try coordinator.create(&config);
    defer coordinator.destroy(exact);
    const full = try coordinator.create(&config);
    defer coordinator.destroy(full);
    var rows: [8]protocol.InputRow = undefined;
    for (0..4) |i| {
        const base = corpus.processFact(@intCast(2 * i), 100 + i, 200 + i, @intCast(300 + i), 400 + i, 100, .general_application, protocol.InputFlags.foreground_focused, protocol.ProcessGrade.normal, 0);
        rows[2 * i] = base;
        rows[2 * i + 1] = corpus.metricFact(base, @intCast(2 * i + 1), .cpu_usage_percent, 100, 0);
    }
    var full_actions: [corpus.default_action_capacity]protocol.Action = undefined;
    var previous_rows: u32 = 0;
    var sequence: u64 = 1;
    while (sequence <= 5) : (sequence += 1) {
        // A fourth process arrives in round 4; earlier processes stay retained.
        const round_rows = rows[0..if (sequence < 4) 6 else 8];
        var input = corpus.cycle(&config, sequence, @intCast(sequence * 1000), @intCast(round_rows.len), sequence == 1);
        var requirements: scratch.Requirements = undefined;
        var action_rows: u32 = 0;
        try measure(exact, &input, round_rows, &requirements, &action_rows);
        try std.testing.expectEqual(@as(u32, if (sequence < 4) 3 else 4), action_rows);
        try std.testing.expectEqual(@as(u32, if (sequence < 4) 3 else 4), requirements.process_count);
        const bytes = try std.heap.page_allocator.alloc(u8, @intCast(requirements.byte_count));
        defer std.heap.page_allocator.free(bytes);
        const actions = try std.testing.allocator.alloc(protocol.Action, action_rows);
        defer std.testing.allocator.free(actions);
        var left: protocol.Snapshot = undefined;
        if (previous_rows != 0 and previous_rows < action_rows) {
            // The previous round action buffer is refused before any mutation.
            const revision = exact.state_revision;
            input.action_capacity = previous_rows;
            try std.testing.expectEqual(protocol.Status.buffer_too_small, coordinator.plan(exact, &input, round_rows.ptr, @intCast(round_rows.len), actions.ptr, previous_rows, bytes.ptr, requirements.byte_count, &requirements, &left));
            try std.testing.expectEqual(revision, exact.state_revision);
            try std.testing.expectEqual(sequence - 1, exact.last_cycle_sequence);
        }
        input.action_capacity = action_rows;
        try std.testing.expectEqual(protocol.Status.ok, coordinator.plan(exact, &input, round_rows.ptr, @intCast(round_rows.len), actions.ptr, action_rows, bytes.ptr, requirements.byte_count, &requirements, &left));
        var right: protocol.Snapshot = undefined;
        input.action_capacity = full_actions.len;
        try std.testing.expectEqual(protocol.Status.ok, corpus.plan(full, &input, round_rows.ptr, @intCast(round_rows.len), &full_actions, full_actions.len, &right));
        try std.testing.expectEqualDeep(right, left);
        try std.testing.expectEqualDeep(full_actions[0..right.action_count], actions[0..left.action_count]);
        var feedback: [4]protocol.Feedback = undefined;
        var feedback_count: u32 = 0;
        for (actions[0..left.action_count]) |action| {
            if ((action.flags & protocol.ActionFlags.requires_feedback) == 0) continue;
            feedback[feedback_count] = corpus.feedbackFor(action, .succeeded, input.observed_at_ms + 1);
            feedback_count += 1;
        }
        if (feedback_count != 0) {
            try std.testing.expectEqual(protocol.Status.ok, coordinator.applyFeedback(exact, &feedback, feedback_count, feedback_count, &left));
            try std.testing.expectEqual(protocol.Status.ok, coordinator.applyFeedback(full, &feedback, feedback_count, feedback_count, &right));
            try std.testing.expectEqualDeep(right, left);
            try std.testing.expectEqual(@as(u32, 0), left.inflight_count);
        }
        previous_rows = action_rows;
    }
    try expectUnbound(exact);
}

fn measure(session: *state.Session, input: *const protocol.CycleInput, rows: []const protocol.InputRow, requirements: *scratch.Requirements, action_rows: *u32) !void {
    var table_count: u64 = 0;
    try std.testing.expectEqual(protocol.Status.ok, coordinator.queryMeasureTable(session, @intCast(rows.len), &table_count));
    const table = try std.testing.allocator.alloc(u32, @intCast(table_count));
    defer std.testing.allocator.free(table);
    try std.testing.expectEqual(protocol.Status.ok, coordinator.measurePlan(session, input, rows.ptr, @intCast(rows.len), table.ptr, table_count, requirements, action_rows));
}

fn expectUnbound(session: *state.Session) !void {
    try std.testing.expectEqual(@as(usize, 0), session.staged_processes.len);
    try std.testing.expectEqual(@as(usize, 0), session.staged_softwares.len);
    try std.testing.expectEqual(@as(usize, 0), session.staged_gpus.len);
    try std.testing.expectEqual(@as(usize, 0), session.staged_software_gpus.len);
    try std.testing.expectEqual(@as(usize, 0), session.staged_process_index.len);
    try std.testing.expectEqual(@as(usize, 0), session.staged_software_index.len);
    try std.testing.expectEqual(@as(usize, 0), session.staged_gpu_index.len);
    try std.testing.expectEqual(@as(usize, 0), session.staged_software_gpu_index.len);
}
