const std = @import("std");
const protocol = @import("protocol.zig");
const session_module = @import("session.zig");
const corpus = @import("golden_corpus.zig");
const ResultCode = @import("../../common/result_codes.zig").ResultCode;

test "placement coordinator accepts distinct shim policy kind and rejects the next unknown kind" {
    var desired = corpus.desired(101, 201, 301);
    desired.resource_kind = @intFromEnum(protocol.ResourceKind.gpu);
    desired.placement_kind = @intFromEnum(protocol.PlacementKind.gpu_shim_policy);
    var applied = corpus.applied(101, 201, 301, 111, 301);
    applied.resource_kind = desired.resource_kind;
    applied.placement_kind = desired.placement_kind;
    try std.testing.expect(protocol.validDesired(&desired));
    try std.testing.expect(protocol.validApplied(&applied));
    desired.placement_kind += 1;
    applied.placement_kind += 1;
    try std.testing.expect(!protocol.validDesired(&desired));
    try std.testing.expect(!protocol.validApplied(&applied));
}

test "placement coordinator applies retains restores and clears exact receipt" {
    var config = corpus.config(7);
    const session = try session_module.Session.create(&config);
    defer session.destroy();
    var actions = [_]protocol.ActionOutput{std.mem.zeroes(protocol.ActionOutput)} ** 8;
    var desired = [_]protocol.DesiredInput{corpus.desired(101, 201, 301)};
    var cycle = corpus.cycle(7, 1, 1_000, 1, 0);
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.plan(session, &cycle, &desired, 1, null, 0, &actions, 8),
    );
    try std.testing.expectEqual(@as(u32, 1), cycle.action_count);
    try std.testing.expectEqual(
        @intFromEnum(protocol.ActionDisposition.apply),
        actions[0].disposition,
    );

    var apply_feedback = [_]protocol.FeedbackInput{
        corpus.feedback(actions[0], .applied, 1_010),
    };
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.applyFeedback(session, &apply_feedback, 1),
    );

    var applied = [_]protocol.AppliedInput{corpus.applied(101, 201, 301, 111, 301)};
    cycle = corpus.cycle(7, 2, 1_100, 1, 1);
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.plan(session, &cycle, &desired, 1, &applied, 1, &actions, 8),
    );
    try std.testing.expectEqual(@as(u32, 0), cycle.action_count);

    cycle = corpus.cycle(7, 3, 1_200, 0, 1);
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.plan(session, &cycle, null, 0, &applied, 1, &actions, 8),
    );
    try std.testing.expectEqual(@as(u32, 1), cycle.action_count);
    try std.testing.expectEqual(
        @intFromEnum(protocol.ActionDisposition.restore),
        actions[0].disposition,
    );
    var restore_feedback = [_]protocol.FeedbackInput{
        corpus.feedback(actions[0], .restored, 1_210),
    };
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.applyFeedback(session, &restore_feedback, 1),
    );
    var snapshot_header = emptySnapshotHeader();
    var states = [_]protocol.StateOutput{std.mem.zeroes(protocol.StateOutput)} ** 8;
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.snapshot(session, &snapshot_header, &states, 8),
    );
    try std.testing.expectEqual(@as(u32, 0), snapshot_header.active_state_count);
}

test "placement coordinator orders restore before apply" {
    var config = corpus.config(9);
    const session = try session_module.Session.create(&config);
    defer session.destroy();
    var actions = [_]protocol.ActionOutput{std.mem.zeroes(protocol.ActionOutput)} ** 8;
    var old_applied = [_]protocol.AppliedInput{corpus.applied(1, 2, 10, 9, 10)};
    var old_desired = [_]protocol.DesiredInput{corpus.desired(1, 2, 10)};
    var cycle = corpus.cycle(9, 1, 2_000, 1, 1);
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.plan(session, &cycle, &old_desired, 1, &old_applied, 1, &actions, 8),
    );

    var new_desired = [_]protocol.DesiredInput{corpus.desired(3, 4, 20)};
    cycle = corpus.cycle(9, 2, 2_100, 1, 1);
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.plan(session, &cycle, &new_desired, 1, &old_applied, 1, &actions, 8),
    );
    try std.testing.expectEqual(@as(u32, 2), cycle.action_count);
    try std.testing.expectEqual(@intFromEnum(protocol.ActionDisposition.restore), actions[0].disposition);
    try std.testing.expectEqual(@intFromEnum(protocol.ActionDisposition.apply), actions[1].disposition);
}

test "placement coordinator owns apply priority and identity ordering" {
    var config = corpus.config(10);
    const session = try session_module.Session.create(&config);
    defer session.destroy();
    var actions = [_]protocol.ActionOutput{std.mem.zeroes(protocol.ActionOutput)} ** 8;
    var desired = [_]protocol.DesiredInput{
        corpus.desired(50, 2, 10),
        corpus.desired(40, 4, 20),
        corpus.desired(30, 6, 30),
    };
    desired[0].priority = 1;
    desired[1].priority = 10;
    desired[2].priority = 10;
    var cycle = corpus.cycle(10, 1, 2_500, 3, 0);
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.plan(session, &cycle, &desired, 3, null, 0, &actions, 8),
    );
    try std.testing.expectEqual(@as(u32, 3), cycle.action_count);
    try std.testing.expectEqual(@as(u64, 30), actions[0].target_key);
    try std.testing.expectEqual(@as(u64, 40), actions[1].target_key);
    try std.testing.expectEqual(@as(u64, 50), actions[2].target_key);
    try std.testing.expectEqual(@as(u32, 10), actions[0].priority);
    try std.testing.expectEqual(@as(u32, 10), actions[1].priority);
    try std.testing.expectEqual(@as(u32, 1), actions[2].priority);
}

test "placement coordinator rejects duplicate desired rows without advancing epoch" {
    var config = corpus.config(11);
    const session = try session_module.Session.create(&config);
    defer session.destroy();
    var actions = [_]protocol.ActionOutput{std.mem.zeroes(protocol.ActionOutput)} ** 8;
    const row = corpus.desired(1, 2, 3);
    var desired = [_]protocol.DesiredInput{ row, row };
    var cycle = corpus.cycle(11, 1, 3_000, 2, 0);
    try std.testing.expectEqual(
        ResultCode.invalid_argument,
        session_module.plan(session, &cycle, &desired, 2, null, 0, &actions, 8),
    );
    try std.testing.expectEqual(@as(u64, 0), session.last_cycle_epoch);
}

test "placement coordinator retry feedback schedules exact next wake" {
    var config = corpus.config(13);
    const session = try session_module.Session.create(&config);
    defer session.destroy();
    var actions = [_]protocol.ActionOutput{std.mem.zeroes(protocol.ActionOutput)} ** 8;
    var desired = [_]protocol.DesiredInput{corpus.desired(5, 6, 7)};
    var cycle = corpus.cycle(13, 1, 4_000, 1, 0);
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.plan(session, &cycle, &desired, 1, null, 0, &actions, 8),
    );
    var retry_feedback = [_]protocol.FeedbackInput{
        corpus.feedback(actions[0], .retryable_failure, 4_010),
    };
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.applyFeedback(session, &retry_feedback, 1),
    );
    var snapshot_header = emptySnapshotHeader();
    var states = [_]protocol.StateOutput{std.mem.zeroes(protocol.StateOutput)} ** 8;
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.snapshot(session, &snapshot_header, &states, 8),
    );
    try std.testing.expectEqual(@as(u64, 4_110), snapshot_header.next_wake_milliseconds);
    try std.testing.expectEqual(@as(u32, 1), snapshot_header.retry_wait_count);
}

test "placement coordinator omitted pending restore stays unchanged until resubmitted" {
    try expectOmittedRestoreUnchanged(false);
}

test "placement coordinator omitted retry restore stays unchanged until resubmitted" {
    try expectOmittedRestoreUnchanged(true);
}

fn expectOmittedRestoreUnchanged(retry_feedback: bool) !void {
    var config = corpus.config(14);
    const session = try session_module.Session.create(&config);
    defer session.destroy();
    var actions = [_]protocol.ActionOutput{std.mem.zeroes(protocol.ActionOutput)} ** 8;
    var old_applied = [_]protocol.AppliedInput{corpus.applied(1, 2, 10, 9, 10)};
    var cycle = corpus.cycle(14, 1, 1_000, 0, 1);
    try std.testing.expectEqual(ResultCode.ok,
        session_module.plan(session, &cycle, null, 0, &old_applied, 1, &actions, 8));
    try std.testing.expectEqual(@as(u32, 1), cycle.action_count);
    try std.testing.expectEqual(@intFromEnum(protocol.ActionDisposition.restore), actions[0].disposition);
    if (retry_feedback) {
        var feedback = [_]protocol.FeedbackInput{corpus.feedback(actions[0], .retryable_failure, 1_010)};
        try std.testing.expectEqual(ResultCode.ok, session_module.applyFeedback(session, &feedback, 1));
    }
    const before = session.slots[session.findSlot(1, 2).?];
    var other = [_]protocol.DesiredInput{corpus.desired(3, 4, 20)};
    cycle = corpus.cycle(14, 2, 2_000, 1, 0);
    try std.testing.expectEqual(ResultCode.ok,
        session_module.plan(session, &cycle, &other, 1, null, 0, &actions, 8));
    try std.testing.expectEqual(@as(u32, 1), cycle.action_count);
    try std.testing.expectEqual(@as(u64, 3), actions[0].target_key);
    try std.testing.expectEqual(@intFromEnum(protocol.ActionDisposition.apply), actions[0].disposition);
    const omitted = session.slots[session.findSlot(1, 2).?];
    try std.testing.expectEqual(before.state, omitted.state);
    try std.testing.expectEqual(before.pending_action_id, omitted.pending_action_id);
    try std.testing.expectEqual(before.pending_disposition, omitted.pending_disposition);
    try std.testing.expectEqual(before.retry_at_milliseconds, omitted.retry_at_milliseconds);
    try std.testing.expectEqual(before.retry_count, omitted.retry_count);
    var applied_feedback = [_]protocol.FeedbackInput{corpus.feedback(actions[0], .applied, 2_010)};
    try std.testing.expectEqual(ResultCode.ok, session_module.applyFeedback(session, &applied_feedback, 1));
    cycle = corpus.cycle(14, 3, 3_000, 0, 1);
    try std.testing.expectEqual(ResultCode.ok,
        session_module.plan(session, &cycle, null, 0, &old_applied, 1, &actions, 8));
    try std.testing.expectEqual(@as(u32, 1), cycle.action_count);
    try std.testing.expectEqual(@as(u64, 1), actions[0].target_key);
    try std.testing.expectEqual(@intFromEnum(protocol.ActionDisposition.restore), actions[0].disposition);
    try std.testing.expect(actions[0].action_id != before.pending_action_id);
}

test "placement coordinator rejects late feedback without consuming pending action" {
    var config = corpus.config(15);
    const session = try session_module.Session.create(&config);
    defer session.destroy();
    var actions = [_]protocol.ActionOutput{std.mem.zeroes(protocol.ActionOutput)} ** 8;
    var desired = [_]protocol.DesiredInput{corpus.desired(8, 9, 10)};
    var cycle = corpus.cycle(15, 1, 5_000, 1, 0);
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.plan(session, &cycle, &desired, 1, null, 0, &actions, 8),
    );
    var late = [_]protocol.FeedbackInput{corpus.feedback(actions[0], .applied, 5_056)};
    try std.testing.expectEqual(
        ResultCode.stale_frame,
        session_module.applyFeedback(session, &late, 1),
    );
    var exact = [_]protocol.FeedbackInput{corpus.feedback(actions[0], .applied, 5_010)};
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.applyFeedback(session, &exact, 1),
    );
}

test "placement coordinator retains invalid and unavailable receipts as explicit blocked state" {
    var config = corpus.config(17);
    const session = try session_module.Session.create(&config);
    defer session.destroy();
    var actions = [_]protocol.ActionOutput{std.mem.zeroes(protocol.ActionOutput)} ** 8;
    var invalid = corpus.applied(12, 13, 14, 15, 14);
    invalid.flags = 0;
    invalid.receipt_digest = 0;
    invalid.previous_digest = 0;
    invalid.current_digest = 0;
    invalid.process_id = 0;
    invalid.process_start_key = 0;
    invalid.resource_kind = @intFromEnum(protocol.ResourceKind.unknown);
    invalid.placement_kind = @intFromEnum(protocol.PlacementKind.unknown);
    invalid.observation_status = @intFromEnum(protocol.ObservationStatus.unavailable);
    invalid.valid_mask = protocol.AppliedValid.identity;
    var invalid_rows = [_]protocol.AppliedInput{invalid};
    var cycle = corpus.cycle(17, 1, 6_000, 0, 1);
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.plan(session, &cycle, null, 0, &invalid_rows, 1, &actions, 8),
    );
    try std.testing.expectEqual(@as(u32, 0), cycle.action_count);
    var snapshot_header = emptySnapshotHeader();
    var states = [_]protocol.StateOutput{std.mem.zeroes(protocol.StateOutput)} ** 8;
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.snapshot(session, &snapshot_header, &states, 8),
    );
    try std.testing.expectEqual(@as(u32, 1), snapshot_header.blocked_count);

    var unavailable = corpus.applied(12, 13, 14, 15, 14);
    unavailable.observation_status = @intFromEnum(protocol.ObservationStatus.unavailable);
    var unavailable_rows = [_]protocol.AppliedInput{unavailable};
    cycle = corpus.cycle(17, 2, 6_100, 0, 1);
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.plan(session, &cycle, null, 0, &unavailable_rows, 1, &actions, 8),
    );
    try std.testing.expectEqual(@as(u32, 0), cycle.action_count);
    try std.testing.expectEqual(@as(u64, 6_200), cycle.next_wake_milliseconds);
}

test "placement coordinator can defer receipt observation to the conditional executor" {
    var config = corpus.config(18);
    const session = try session_module.Session.create(&config);
    defer session.destroy();
    var actions = [_]protocol.ActionOutput{std.mem.zeroes(protocol.ActionOutput)} ** 8;
    var applied = corpus.applied(31, 32, 33, 34, 33);
    applied.observation_status = @intFromEnum(protocol.ObservationStatus.unchecked);
    applied.current_digest = 0;
    applied.valid_mask &= ~protocol.AppliedValid.current_digest;
    var applied_rows = [_]protocol.AppliedInput{applied};
    var cycle = corpus.cycle(18, 1, 6_500, 0, 1);
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.plan(session, &cycle, null, 0, &applied_rows, 1, &actions, 8),
    );
    try std.testing.expectEqual(@as(u32, 1), cycle.action_count);
    try std.testing.expectEqual(
        @intFromEnum(protocol.ActionDisposition.restore),
        actions[0].disposition,
    );
}

test "placement coordinator requires explicit override after ownership loss" {
    var config = corpus.config(19);
    const session = try session_module.Session.create(&config);
    defer session.destroy();
    var actions = [_]protocol.ActionOutput{std.mem.zeroes(protocol.ActionOutput)} ** 8;
    var desired = [_]protocol.DesiredInput{corpus.desired(21, 22, 23)};
    var ownership_lost = [_]protocol.AppliedInput{corpus.applied(21, 22, 24, 25, 26)};
    var cycle = corpus.cycle(19, 1, 7_000, 1, 1);
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.plan(session, &cycle, &desired, 1, &ownership_lost, 1, &actions, 8),
    );
    try std.testing.expectEqual(@as(u32, 1), cycle.action_count);
    try std.testing.expectEqual(@intFromEnum(protocol.ActionDisposition.restore), actions[0].disposition);
    var settlement = [_]protocol.FeedbackInput{
        corpus.feedback(actions[0], .ownership_lost, 7_010),
    };
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.applyFeedback(session, &settlement, 1),
    );

    cycle = corpus.cycle(19, 2, 7_100, 1, 0);
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.plan(session, &cycle, &desired, 1, null, 0, &actions, 8),
    );
    try std.testing.expectEqual(@as(u32, 0), cycle.action_count);

    desired[0].flags = protocol.DesiredFlags.override_external_state;
    cycle = corpus.cycle(19, 3, 7_200, 1, 0);
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.plan(session, &cycle, &desired, 1, null, 0, &actions, 8),
    );
    try std.testing.expectEqual(@as(u32, 1), cycle.action_count);
    try std.testing.expectEqual(@intFromEnum(protocol.ActionDisposition.apply), actions[0].disposition);
}

test "placement coordinator reconfigure is strict and capacity changes require recreate" {
    var config = corpus.config(21);
    const session = try session_module.Session.create(&config);
    defer session.destroy();
    try std.testing.expectEqual(ResultCode.stale_frame, session.reconfigure(&config));
    var changed_capacity = corpus.config(22);
    changed_capacity.maximum_state_count = 9;
    changed_capacity.maximum_action_count = 9;
    try std.testing.expectEqual(ResultCode.invalid_argument, session.reconfigure(&changed_capacity));
    var hot = corpus.config(22);
    hot.retry_delay_milliseconds = 200;
    try std.testing.expectEqual(ResultCode.ok, session.reconfigure(&hot));
}

test "placement coordinator golden corpus produces exact action and state vector" {
    var config = corpus.config(23);
    const session = try session_module.Session.create(&config);
    defer session.destroy();
    var desired = [_]protocol.DesiredInput{corpus.desired(41, 42, 43)};
    var actions = [_]protocol.ActionOutput{std.mem.zeroes(protocol.ActionOutput)} ** 8;
    var cycle = corpus.cycle(23, 1, 8_000, 1, 0);
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.plan(session, &cycle, &desired, 1, null, 0, &actions, 8),
    );
    try std.testing.expectEqualDeep(protocol.ActionOutput{
        .struct_size = @sizeOf(protocol.ActionOutput),
        .disposition = @intFromEnum(protocol.ActionDisposition.apply),
        .action_id = 1,
        .target_key = 41,
        .record_key = 42,
        .resource_kind = @intFromEnum(protocol.ResourceKind.cpu),
        .placement_kind = @intFromEnum(protocol.PlacementKind.cpu_sets),
        .desired_digest = 43,
        .previous_digest = 0,
        .process_start_key = 22,
        .process_id = 11,
        .flags = protocol.ActionFlags.process_identity_valid,
        .reason_mask = protocol.ActionReason.receipt_missing,
        .deadline_milliseconds = 8_050,
        .priority = 0,
        .reserved = 0,
    }, actions[0]);
    try std.testing.expectEqual(@as(u32, 1), cycle.action_count);
    try std.testing.expectEqual(@as(u64, 8_050), cycle.next_wake_milliseconds);
    try std.testing.expectEqual(@as(u64, 2), cycle.state_revision);

    var header = emptySnapshotHeader();
    var states = [_]protocol.StateOutput{std.mem.zeroes(protocol.StateOutput)} ** 8;
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.snapshot(session, &header, &states, 8),
    );
    try std.testing.expectEqual(@as(u64, 23), header.configuration_generation);
    try std.testing.expectEqual(@as(u64, 2), header.state_revision);
    try std.testing.expectEqual(@as(u64, 1), header.last_cycle_epoch);
    try std.testing.expectEqual(@as(u64, 2), header.next_action_id);
    try std.testing.expectEqual(@as(u32, 1), header.active_state_count);
    try std.testing.expectEqual(@as(u32, 1), header.pending_apply_count);
    try std.testing.expectEqual(@as(u64, protocol.SnapshotFlags.next_wake_valid), header.flags);
    try std.testing.expectEqual(@as(u64, 8_050), header.next_wake_milliseconds);
    try std.testing.expectEqual(@intFromEnum(protocol.SlotState.pending_apply), states[0].state);
    try std.testing.expectEqual(@as(u64, 1), states[0].pending_action_id);
    try std.testing.expectEqual(@as(u64, 8_050), states[0].retry_at_milliseconds);
}

test "placement coordinator ABI layout is exact" {
    try std.testing.expectEqual(@as(usize, 80), @sizeOf(protocol.Config));
    try std.testing.expectEqual(@as(usize, 48), @sizeOf(protocol.Capacity));
    try std.testing.expectEqual(@as(usize, 88), @sizeOf(protocol.CycleInput));
    try std.testing.expectEqual(@as(usize, 72), @sizeOf(protocol.DesiredInput));
    try std.testing.expectEqual(@as(usize, 88), @sizeOf(protocol.AppliedInput));
    try std.testing.expectEqual(@as(usize, 96), @sizeOf(protocol.ActionOutput));
    try std.testing.expectEqual(@as(usize, 64), @sizeOf(protocol.FeedbackInput));
    try std.testing.expectEqual(@as(usize, 96), @sizeOf(protocol.SnapshotHeader));
    try std.testing.expectEqual(@as(usize, 112), @sizeOf(protocol.StateOutput));
}

fn emptySnapshotHeader() protocol.SnapshotHeader {
    return .{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.SnapshotHeader),
        .configuration_generation = 0,
        .state_revision = 0,
        .last_cycle_epoch = 0,
        .next_action_id = 0,
        .active_state_count = 0,
        .pending_apply_count = 0,
        .pending_restore_count = 0,
        .blocked_count = 0,
        .retry_wait_count = 0,
        .state_output_count = 0,
        .flags = 0,
        .next_wake_milliseconds = 0,
        .reserved = .{ 0, 0 },
    };
}
