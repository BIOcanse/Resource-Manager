const std = @import("std");
const protocol = @import("protocol.zig");
const state = @import("state.zig");
const scratch = @import("scratch.zig");
const coordinator = @import("session.zig");
const corpus = @import("golden_corpus.zig");

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
    const r = scratch.Layout.init(&config).requirements;
    const bytes = try std.heap.page_allocator.alloc(u8, @intCast(r.byte_count + r.alignment));
    defer std.heap.page_allocator.free(bytes);
    var actions: [corpus.default_action_capacity]protocol.Action = undefined;
    var snapshot: protocol.Snapshot = undefined;
    var input = corpus.cycle(&config, 1, 1000, 0, true);
    const revision = session.state_revision;
    try std.testing.expectEqual(protocol.Status.invalid_argument, coordinator.plan(session, &input, null, 0, &actions, actions.len, null, r.byte_count, &snapshot));
    try std.testing.expectEqual(protocol.Status.invalid_argument, coordinator.plan(session, &input, null, 0, &actions, actions.len, bytes.ptr, r.byte_count - 1, &snapshot));
    try std.testing.expectEqual(protocol.Status.invalid_argument, coordinator.plan(session, &input, null, 0, &actions, actions.len, bytes[1..].ptr, r.byte_count, &snapshot));
    try std.testing.expectEqual(protocol.Status.invalid_argument, coordinator.plan(session, &input, null, 0, @ptrCast(@alignCast(bytes.ptr)), actions.len, bytes.ptr, r.byte_count, &snapshot));
    input.abi_version +%= 1;
    try std.testing.expectEqual(protocol.Status.abi_mismatch, coordinator.plan(session, &input, null, 0, &actions, actions.len, bytes.ptr, r.byte_count, &snapshot));
    try std.testing.expectEqual(revision, session.state_revision);
    try expectUnbound(session);
}

test "smart staged scratch refills all rows and preserves reservations feedback and snapshots after reuse" {
    const config = corpus.explicitConfig(protocol.FeatureFlags.process_policy | protocol.FeatureFlags.adapter_gpu);
    const session = try coordinator.create(&config);
    defer coordinator.destroy(session);
    const layout = scratch.Layout.init(&config);
    const r = layout.requirements;
    const bytes = try std.heap.page_allocator.alloc(u8, @intCast(r.byte_count));
    defer std.heap.page_allocator.free(bytes);
    const base = corpus.processFact(0, 101, 1001, 11, 111, 20, .adapted, 0, protocol.ProcessGrade.normal, .normal, .normal, 0);
    const rows = [_]protocol.InputRow{ base, corpus.metricFact(base, 1, .cpu_usage_percent, 50, 0), corpus.metricFact(base, 2, .gpu_usage_percent, 25, 1), corpus.metricFact(base, 3, .vram_usage_percent, 20, 1) };
    var actions: [corpus.default_action_capacity]protocol.Action = undefined;
    var snapshot: protocol.Snapshot = undefined;
    var sequence: u64 = 1;
    while (sequence <= 3) : (sequence += 1) {
        @memset(bytes, 0xa5);
        const input = corpus.cycle(&config, sequence, @as(i64, @intCast(sequence)) * 1000, rows.len, sequence == 1);
        try std.testing.expectEqual(protocol.Status.ok, coordinator.plan(session, &input, &rows, rows.len, &actions, actions.len, bytes.ptr, r.byte_count, &snapshot));
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
