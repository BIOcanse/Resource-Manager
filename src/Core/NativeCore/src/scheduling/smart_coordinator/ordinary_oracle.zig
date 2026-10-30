const std = @import("std");
const protocol = @import("protocol.zig");
const coordinator = @import("session.zig");
const corpus = @import("golden_corpus.zig");

// Printed retained fields are the pre-removal ordinary-process oracle. Layout,
// adapter rows and IDs consumed by those retired rows are deliberately omitted.
test "ordinary process behavior projection oracle" {
    const cases = [_]struct { base: f64, usage: f64, protection: u8 }{
        .{ .base = 20, .usage = 0, .protection = 0 },
        .{ .base = 20, .usage = 50, .protection = 1 },
        .{ .base = 20, .usage = 50, .protection = 2 },
        .{ .base = 40, .usage = 100, .protection = 0 },
        .{ .base = 90, .usage = 100, .protection = 0 },
    };
    for (cases, 0..) |case, case_index| {
        const config = corpus.explicitConfig(protocol.FeatureFlags.process_policy);
        const session = try coordinator.create(&config);
        defer coordinator.destroy(session);
        var base = corpus.processFact(0, 101, 201, 301, 401, case.base, .general_application, protocol.InputFlags.foreground_focused, protocol.ProcessGrade.normal, 0);
        base.protection_level = case.protection;
        var rows = [_]protocol.InputRow{ base, corpus.metricFact(base, 1, .cpu_usage_percent, case.usage, 0) };
        var actions: [corpus.default_action_capacity]protocol.Action = undefined;
        var snapshot: protocol.Snapshot = undefined;
        var round: u64 = 1;
        while (round <= 4) : (round += 1) {
            var input = corpus.cycle(&config, round, @as(i64, @intCast(round)) * 1000, rows.len, round == 1);
            try std.testing.expectEqual(protocol.Status.ok, corpus.plan(session, &input, &rows, rows.len, &actions, actions.len, &snapshot));
            for (actions[0..snapshot.action_count]) |action| {
                if (action.scope != @intFromEnum(protocol.ActionScope.process_policy)) continue;
                std.debug.print("ORACLE {d} {d} {d} {d} {d} {d} {d} {d} {d}\n", .{ case_index, round, action.from_process_grade, action.to_process_grade, action.disposition, action.flags, action.cpu_score, action.reason_mask, action.wake_after_ms });
                std.debug.print("ORACLE_ACTION {any}\n", .{.{ action.flags, action.valid_mask, action.reason_mask, action.plan_epoch, action.config_generation, action.atomic_group_id, action.target_key, action.software_key, action.process_start_key, @as(u64, @bitCast(action.cpu_score)), action.source_index, action.process_id, action.order_key, action.wake_after_ms, action.group_member_index, action.group_member_count, action.scope, action.disposition, action.domain_mask, action.from_process_grade, action.to_process_grade }});
                std.debug.print("RETIRED_ROW_EFFECT {d} {d} action_id={d} action_count={d} global_reason={d}\n", .{ case_index, round, action.action_id, snapshot.action_count, snapshot.reason_mask });
                if ((action.flags & protocol.ActionFlags.requires_feedback) != 0) {
                    var item = corpus.feedbackFor(action, .succeeded, input.observed_at_ms + 1);
                    try std.testing.expectEqual(protocol.Status.ok, coordinator.applyFeedback(session, @ptrCast(&item), 1, 1, &snapshot));
                }
            }
            std.debug.print("ORACLE_GLOBAL {any}\n", .{.{ snapshot.flags, snapshot.config_generation, snapshot.cycle_sequence, snapshot.plan_epoch, snapshot.state_revision, snapshot.observed_at_ms, snapshot.next_wake_at_ms, snapshot.wake_after_ms, snapshot.process_count, snapshot.software_count, snapshot.pending_count, snapshot.inflight_count, snapshot.invalid_fact_count, snapshot.snapshot_row_count, snapshot.event_boost_rounds_remaining, snapshot.early_wake_after_ms }});
            var snapshot_rows: [corpus.default_process_capacity + corpus.default_software_capacity]protocol.SnapshotRow = undefined;
            try std.testing.expectEqual(protocol.Status.ok, coordinator.getSnapshot(session, &snapshot, &snapshot_rows, snapshot_rows.len));
            for (snapshot_rows[0..snapshot.snapshot_row_count]) |row| {
                if (row.row_kind != @intFromEnum(protocol.SnapshotRowKind.process)) continue;
                // Bits 7/8/11/12 belonged solely to removed adapter capability/grade facts.
                const retained_validity = row.valid_mask & ~@as(u64, (1 << 7) | (1 << 8) | (1 << 11) | (1 << 12));
                std.debug.print("ORACLE_PROCESS_ROW {any}\n", .{.{ row.flags, retained_validity, row.reason_mask, row.target_key, row.software_key, row.process_start_key, row.last_seen_cycle, row.pending_first_seen_ms, row.pending_last_seen_ms, row.last_feedback_ms, row.game_started_at_ms, @as(u64, @bitCast(row.base_score)), @as(u64, @bitCast(row.cpu_score)), @as(u64, @bitCast(row.cpu_occupancy_percent)), @as(u64, @bitCast(row.reserved_process_score0)), @as(u64, @bitCast(row.reserved_process_ratio0)), row.source_index, row.process_id, row.process_pending_count, row.failure_count, row.row_kind, row.software_kind, row.runtime_state, row.protection_level, row.desired_process_grade, row.applied_process_grade, row.pending_process_grade }});
            }
        }
    }
}
