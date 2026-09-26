const std = @import("std");
const root = @import("root.zig");
const protocol = root.protocol;
const coordinator = root.session;
const corpus = @import("golden_corpus.zig");
const software_aggregation = @import("../compute_scoring/software_aggregation.zig");

const ActionBuffer = [corpus.default_action_capacity]protocol.Action;

test "ABI layout is frozen for version 8" {
    try std.testing.expectEqual(@as(usize, 424), @sizeOf(protocol.Config));
    try std.testing.expectEqual(@as(usize, 80), @sizeOf(protocol.AdapterPolicyConfig));
    try std.testing.expectEqual(@as(usize, 88), @sizeOf(protocol.Capacity));
    try std.testing.expectEqual(@as(usize, 96), @sizeOf(protocol.CycleInput));
    try std.testing.expectEqual(@as(usize, 112), @sizeOf(protocol.InputRow));
    try std.testing.expectEqual(@as(usize, 136), @sizeOf(protocol.Action));
    try std.testing.expectEqual(@as(usize, 96), @sizeOf(protocol.Feedback));
    try std.testing.expectEqual(@as(usize, 120), @sizeOf(protocol.Snapshot));
    try std.testing.expectEqual(@as(usize, 192), @sizeOf(protocol.SnapshotRow));
    try std.testing.expectEqual(@as(usize, 8), @offsetOf(protocol.InputRow, "valid_mask"));
    try std.testing.expectEqual(@as(usize, 24), @offsetOf(protocol.InputRow, "target_key"));
    try std.testing.expectEqual(@as(usize, 24), @offsetOf(protocol.Action, "action_id"));
    try std.testing.expectEqual(@as(usize, 112), @offsetOf(protocol.Action, "scope"));
    try std.testing.expectEqual(@as(usize, 80), @offsetOf(protocol.Feedback, "status"));
    try std.testing.expectEqual(@as(usize, 40), @offsetOf(protocol.CycleInput, "valid_mask"));
    try std.testing.expectEqual(@as(usize, 48), @offsetOf(protocol.CycleInput, "flags"));
    try std.testing.expectEqual(@as(usize, 64), @offsetOf(protocol.CycleInput, "score_scheduling_generation"));
    try std.testing.expectEqual(@as(usize, 72), @offsetOf(protocol.CycleInput, "cpu_score_source_fingerprint"));
    try std.testing.expectEqual(@as(usize, 80), @offsetOf(protocol.CycleInput, "gpu_score_source_fingerprint"));
    try std.testing.expectEqual(@as(usize, 88), @offsetOf(protocol.CycleInput, "maximum_actions_this_cycle"));
    try std.testing.expectEqual(@as(usize, 92), @offsetOf(protocol.CycleInput, "reserved"));
    try std.testing.expectEqual(@as(usize, 76), @offsetOf(protocol.InputRow, "score_member_count"));
    try std.testing.expectEqual(@as(usize, 96), @offsetOf(protocol.InputRow, "process_score"));
    try std.testing.expectEqual(@as(usize, 104), @offsetOf(protocol.InputRow, "software_score"));
    try std.testing.expectEqual(@as(usize, 96), @offsetOf(protocol.Snapshot, "reason_mask"));
    try std.testing.expectEqual(@as(usize, 96), @offsetOf(protocol.SnapshotRow, "cpu_score"));
    try std.testing.expectEqual(@as(usize, 104), @offsetOf(protocol.SnapshotRow, "cpu_occupancy_percent"));
    try std.testing.expectEqual(@as(usize, 172), @offsetOf(protocol.SnapshotRow, "desired_process_grade"));
    try std.testing.expectEqual(@as(u64, 0x8cc98a222ba80e73), corpus.layoutFingerprint(protocol.Config));
    try std.testing.expectEqual(@as(u64, 0x822e9b7b868af1e6), corpus.layoutFingerprint(protocol.Capacity));
    try std.testing.expectEqual(@as(u64, 0x65e7ea745d6deb6f), corpus.layoutFingerprint(protocol.CycleInput));
    try std.testing.expectEqual(@as(u64, 0xc0fb77c1361e04ff), corpus.layoutFingerprint(protocol.InputRow));
    try std.testing.expectEqual(@as(u64, 0x531d6c7ff4bcf206), corpus.layoutFingerprint(protocol.Action));
    try std.testing.expectEqual(@as(u64, 0x7722db7daad0835b), corpus.layoutFingerprint(protocol.Feedback));
    try std.testing.expectEqual(@as(u64, 0x8b8bdfce5d3ff18e), corpus.layoutFingerprint(protocol.Snapshot));
    try std.testing.expectEqual(@as(u64, 0xba634188acc130a6), corpus.layoutFingerprint(protocol.SnapshotRow));
}

test "strict config and capacity query have no defaults" {
    var config = corpus.explicitConfig(protocol.FeatureFlags.process_policy);
    try std.testing.expect(protocol.isValidConfig(&config));
    var capacity: protocol.Capacity = undefined;
    try std.testing.expectEqual(protocol.Status.ok, coordinator.capacityForConfig(&config, &capacity));
    try std.testing.expectEqual(config.max_actions, capacity.action_capacity);
    try std.testing.expectEqual(config.max_reservations, capacity.feedback_capacity);
    try std.testing.expectEqual(@as(u32, @sizeOf(protocol.Action)), capacity.action_struct_size);

    config.field_mask &= ~protocol.ConfigFields.timing;
    try std.testing.expect(!protocol.isValidConfig(&config));
    try std.testing.expectEqual(protocol.Status.invalid_argument, coordinator.capacityForConfig(&config, &capacity));
    config = corpus.explicitConfig(protocol.FeatureFlags.process_policy);
    config.abi_version +%= 1;
    try std.testing.expectEqual(protocol.Status.abi_mismatch, coordinator.capacityForConfig(&config, &capacity));
}

test "configuration rejects score ranges that overflow at declared capacity" {
    var config = corpus.explicitConfig(protocol.FeatureFlags.process_policy);
    config.process_state_multipliers[0] = 1e308;
    config.cpu_adapter.state_multipliers[0] = 1e308;
    var capacity: protocol.Capacity = undefined;
    try std.testing.expectEqual(
        protocol.Status.invalid_argument,
        coordinator.capacityForConfig(&config, &capacity),
    );

    config = corpus.explicitConfig(protocol.FeatureFlags.adapter_gpu);
    config.gpu_adapter.state_multipliers[0] = 1e308;
    try std.testing.expectEqual(
        protocol.Status.invalid_argument,
        coordinator.capacityForConfig(&config, &capacity),
    );
}

test "configuration rejects noncanonical not-running multipliers" {
    var config = corpus.explicitConfig(protocol.FeatureFlags.process_policy);
    config.process_state_multipliers[@intFromEnum(protocol.RuntimeState.not_running)] = 0.01;
    try std.testing.expect(!protocol.isValidConfig(&config));

    config = corpus.explicitConfig(protocol.FeatureFlags.adapter_cpu);
    config.cpu_adapter.state_multipliers[@intFromEnum(protocol.RuntimeState.not_running)] = 0.01;
    try std.testing.expect(!protocol.isValidConfig(&config));

    config = corpus.explicitConfig(protocol.FeatureFlags.adapter_cpu);
    config.cpu_adapter.state_multipliers[0] += 0.01;
    try std.testing.expect(!protocol.isValidConfig(&config));

    config = corpus.explicitConfig(protocol.FeatureFlags.adapter_gpu);
    config.gpu_adapter.state_multipliers[@intFromEnum(protocol.RuntimeState.not_running)] = 0.01;
    try std.testing.expect(!protocol.isValidConfig(&config));
}

test "retired config score-only bit and required per-cycle fields are rejected" {
    var config = corpus.explicitConfig(protocol.FeatureFlags.process_policy);
    config.feature_flags |= 1 << 4;
    try std.testing.expect(!protocol.isValidConfig(&config));

    config = corpus.explicitConfig(protocol.FeatureFlags.process_policy);
    const session = try coordinator.create(&config);
    defer coordinator.destroy(session);
    var input = corpus.cycle(&config, 1, 1_000, 0, true);
    input.valid_mask &= ~protocol.CycleValidity.score_only_mode;
    var actions: ActionBuffer = undefined;
    var snapshot: protocol.Snapshot = undefined;
    try std.testing.expectEqual(protocol.Status.invalid_argument, corpus.plan(
        session,
        &input,
        null,
        0,
        actions[0..].ptr,
        actions.len,
        &snapshot,
    ));

    input.valid_mask |= protocol.CycleValidity.score_only_mode;
    input.valid_mask &= ~protocol.CycleValidity.maximum_actions_this_cycle;
    try std.testing.expectEqual(protocol.Status.invalid_argument, corpus.plan(
        session,
        &input,
        null,
        0,
        actions[0..].ptr,
        actions.len,
        &snapshot,
    ));

    input.valid_mask |= protocol.CycleValidity.maximum_actions_this_cycle;
    input.maximum_actions_this_cycle = config.max_actions + 1;
    try std.testing.expectEqual(protocol.Status.invalid_argument, corpus.plan(
        session,
        &input,
        null,
        0,
        actions[0..].ptr,
        actions.len,
        &snapshot,
    ));

    input.maximum_actions_this_cycle = config.max_actions;
    try std.testing.expectEqual(protocol.Status.ok, corpus.plan(
        session,
        &input,
        null,
        0,
        actions[0..].ptr,
        actions.len,
        &snapshot,
    ));
}

test "ABI enum and validity bit assignments are frozen" {
    try std.testing.expectEqual(@as(u32, 0x0008_0000), protocol.abi_version);
    try std.testing.expectEqual(@as(i32, 0), @intFromEnum(protocol.Status.ok));
    try std.testing.expectEqual(@as(i32, 11), @intFromEnum(protocol.Status.recreate_required));
    try std.testing.expectEqual(@as(u64, 0x0f), protocol.FeatureFlags.known);
    try std.testing.expectEqual(@as(u64, 0x7c), protocol.CycleValidity.known);
    try std.testing.expectEqual(@as(u64, 0x0c), protocol.CycleValidity.required);
    try std.testing.expectEqual(@as(u64, 0x1d), protocol.CycleFlags.known);
    try std.testing.expectEqual(@as(u64, 0xffff), protocol.InputValidity.known);
    try std.testing.expectEqual(@as(u8, 0x07), protocol.GradeDomains.known);
    try std.testing.expectEqual(@as(u64, 0xff), protocol.ActionValidity.known);
    try std.testing.expectEqual(@as(u32, 0x0f), protocol.ActionFlags.known);
    try std.testing.expectEqual(@as(u64, 0x0f), protocol.FeedbackValidity.known);
    try std.testing.expectEqual(@as(u32, 0x0f), protocol.FeedbackFlags.known);
    try std.testing.expectEqual(@as(u32, 0x1f), protocol.SnapshotFlags.known);
    try std.testing.expectEqual(
        ((@as(u64, 1) << 38) - 1) &
            ~((@as(u64, 1) << 5) | (@as(u64, 1) << 15) |
                (@as(u64, 1) << 16) | (@as(u64, 1) << 36)),
        protocol.Reason.known,
    );
    try std.testing.expectEqual(@as(i8, -4), protocol.ProcessGrade.level4);
    try std.testing.expectEqual(@as(i8, 1), protocol.ProcessGrade.a1);
    try std.testing.expectEqual(@as(u8, 0), @intFromEnum(protocol.AdapterGrade.freeze));
    try std.testing.expectEqual(@as(u8, 3), @intFromEnum(protocol.AdapterGrade.extreme));
}

test "authoritative facts never turn missing grades into normal" {
    var config = corpus.explicitConfig(protocol.FeatureFlags.process_policy);
    const session = try coordinator.create(&config);
    defer coordinator.destroy(session);

    var row = corpus.processFact(0, 101, 1001, 11, 111, 20, .general_application, 0, protocol.ProcessGrade.normal, .normal, .normal, 0);
    row.valid_mask &= ~protocol.InputValidity.applied_process_grade;
    var rows = [_]protocol.InputRow{row};
    var input = corpus.cycle(&config, 1, 1_000, rows.len, true);
    var actions: ActionBuffer = undefined;
    var snapshot: protocol.Snapshot = undefined;
    try std.testing.expectEqual(protocol.Status.invalid_argument, corpus.plan(
        session,
        &input,
        rows[0..].ptr,
        rows.len,
        actions[0..].ptr,
        actions.len,
        &snapshot,
    ));
}

test "software only authoritative facts preserve adapter ownership" {
    var config = corpus.explicitConfig(protocol.FeatureFlags.adapter_cpu);
    const session = try coordinator.create(&config);
    defer coordinator.destroy(session);

    var rows = [_]protocol.InputRow{corpus.softwareFact(
        0,
        9_001,
        .optimize,
        .normal,
        protocol.InputFlags.owns_cpu_grade,
        41,
    )};
    var input = corpus.cycle(&config, 1, 1_000, rows.len, true);
    var actions: ActionBuffer = undefined;
    var snapshot: protocol.Snapshot = undefined;
    try std.testing.expectEqual(protocol.Status.ok, corpus.plan(
        session,
        &input,
        rows[0..].ptr,
        rows.len,
        actions[0..].ptr,
        actions.len,
        &snapshot,
    ));

    const software_slot = session.findSoftware(9_001) orelse return error.MissingSoftwareSnapshot;
    const software = session.softwares[software_slot];
    try std.testing.expectEqual(@intFromEnum(protocol.AdapterGrade.optimize), software.cpu_transition.applied);
    try std.testing.expect(software.cpu_transition.owned);
    try std.testing.expectEqual(@intFromEnum(protocol.AdapterGrade.normal), software.gpu_transition.applied);
    try std.testing.expect(!software.gpu_transition.owned);
    try std.testing.expectEqual(@as(u32, 0), software.member_count);
}

test "software only row rejects process facts" {
    var row = corpus.softwareFact(0, 9_002, .optimize, .normal, protocol.InputFlags.owns_cpu_grade, 42);
    row.valid_mask |= protocol.InputValidity.applied_process_grade;
    try std.testing.expectEqual(protocol.Status.invalid_argument, protocol.validateInputRow(&row, 0));

    row.valid_mask &= ~protocol.InputValidity.applied_process_grade;
    row.flags |= protocol.InputFlags.running;
    row.valid_mask |= protocol.InputValidity.surface_facts;
    try std.testing.expectEqual(protocol.Status.invalid_argument, protocol.validateInputRow(&row, 0));
}

test "input rows reject base scores and utilization outside the percentage domain" {
    var base = corpus.processFact(0, 9_102, 90_102, 92, 902, 100.000_001, .general_application, 0, protocol.ProcessGrade.normal, .normal, .normal, 0);
    try std.testing.expectEqual(protocol.Status.invalid_argument, protocol.validateInputRow(&base, 0));

    base.base_score = 100;
    var metric = corpus.metricFact(base, 1, .cpu_usage_percent, 100.000_001, 0);
    try std.testing.expectEqual(protocol.Status.invalid_argument, protocol.validateInputRow(&metric, 1));

    metric.metric_value = 100;
    try std.testing.expectEqual(protocol.Status.ok, protocol.validateInputRow(&metric, 1));
}

test "CPU score validity is independent from legacy raw metric validity" {
    const base = corpus.processFact(0, 9_104, 90_104, 94, 904, 80, .general_application, 0, protocol.ProcessGrade.normal, .normal, .normal, 0);
    var row = corpus.metricFact(base, 1, .cpu_usage_percent, 50, 0);
    row.valid_mask &= ~protocol.InputValidity.metric;
    row.metric_value = 0;
    try std.testing.expectEqual(protocol.Status.ok, protocol.validateInputRow(&row, 1));

    var process_only = row;
    process_only.valid_mask &= ~(protocol.InputValidity.software_score | protocol.InputValidity.score_member_count);
    process_only.software_score = 0;
    process_only.score_member_count = 0;
    try std.testing.expectEqual(protocol.Status.ok, protocol.validateInputRow(&process_only, 1));

    var software_only = row;
    software_only.valid_mask &= ~protocol.InputValidity.process_score;
    software_only.process_score = 0;
    try std.testing.expectEqual(protocol.Status.ok, protocol.validateInputRow(&software_only, 1));

    var invalid = row;
    invalid.metric_value = 50;
    try std.testing.expectEqual(protocol.Status.invalid_argument, protocol.validateInputRow(&invalid, 1));
    invalid = row;
    invalid.metric_kind = @intFromEnum(protocol.MetricKind.gpu_usage_percent);
    try std.testing.expectEqual(protocol.Status.invalid_argument, protocol.validateInputRow(&invalid, 1));
    invalid = row;
    invalid.process_score = std.math.nan(f64);
    try std.testing.expectEqual(protocol.Status.invalid_argument, protocol.validateInputRow(&invalid, 1));
    invalid = row;
    invalid.score_member_count = 0;
    try std.testing.expectEqual(protocol.Status.invalid_argument, protocol.validateInputRow(&invalid, 1));
}

test "CPU score-only facts reach native process and software state without raw occupancy" {
    var config = corpus.explicitConfig(protocol.FeatureFlags.process_policy | protocol.FeatureFlags.adapter_cpu);
    const session = try coordinator.create(&config);
    defer coordinator.destroy(session);
    const base = corpus.processFact(0, 9_105, 90_105, 95, 905, 80, .adapted, 0, protocol.ProcessGrade.normal, .normal, .normal, 0);
    var process_score = corpus.metricFact(base, 1, .cpu_usage_percent, 0, 0);
    process_score.valid_mask &= ~(protocol.InputValidity.metric | protocol.InputValidity.software_score | protocol.InputValidity.score_member_count);
    process_score.process_score = 27;
    process_score.software_score = 0;
    process_score.score_member_count = 0;
    var software_score = corpus.metricFact(base, 2, .cpu_usage_percent, 0, 0);
    software_score.valid_mask &= ~(protocol.InputValidity.metric | protocol.InputValidity.process_score);
    software_score.process_score = 0;
    software_score.software_score = 27;
    var rows = [_]protocol.InputRow{ base, process_score, software_score };
    var input = corpus.cycle(&config, 1, 1_000, rows.len, true);
    input.flags |= protocol.CycleFlags.score_only;
    var actions: ActionBuffer = undefined;
    var snapshot: protocol.Snapshot = undefined;
    try std.testing.expectEqual(protocol.Status.ok, corpus.plan(session, &input,
        rows[0..].ptr, rows.len, actions[0..].ptr, actions.len, &snapshot));
    const process_slot = session.findProcess(9_105, 95, 905) orelse return error.MissingProcessSnapshot;
    const process = session.processes[process_slot];
    try std.testing.expect(process.cpu_score_valid);
    try std.testing.expectEqual(@as(f64, 27), process.cpu_score);
    try std.testing.expectEqual(@as(f64, 0), process.cpu_usage_percent);
    try std.testing.expect((process.valid_mask & protocol.InputValidity.metric) == 0);
    const software_slot = session.findSoftware(90_105) orelse return error.MissingSoftwareSnapshot;
    try std.testing.expect(session.softwares[software_slot].member_cpu_score_valid);
    try std.testing.expectEqual(@as(f64, 27), session.softwares[software_slot].member_cpu_score);
}

test "standalone and process adapter facts merge in either order" {
    var config = corpus.explicitConfig(protocol.FeatureFlags.adapter_cpu);
    var actions: ActionBuffer = undefined;
    var first_snapshot: protocol.Snapshot = undefined;
    var second_snapshot: protocol.Snapshot = undefined;

    const first_session = try coordinator.create(&config);
    defer coordinator.destroy(first_session);
    const standalone_first = corpus.softwareFact(
        0,
        9_003,
        .optimize,
        .normal,
        protocol.InputFlags.owns_cpu_grade,
        43,
    );
    var process_second = corpus.processFact(
        1,
        9_103,
        9_003,
        93,
        903,
        20,
        .adapted,
        0,
        protocol.ProcessGrade.normal,
        .optimize,
        .normal,
        protocol.InputFlags.owns_cpu_grade,
    );
    process_second.applied_epoch = 43;
    var first_rows = [_]protocol.InputRow{ standalone_first, process_second };
    var first_input = corpus.cycle(&config, 1, 1_000, first_rows.len, true);
    try std.testing.expectEqual(protocol.Status.ok, corpus.plan(
        first_session,
        &first_input,
        first_rows[0..].ptr,
        first_rows.len,
        actions[0..].ptr,
        actions.len,
        &first_snapshot,
    ));

    const second_session = try coordinator.create(&config);
    defer coordinator.destroy(second_session);
    var process_first = process_second;
    process_first.source_index = 0;
    var standalone_second = standalone_first;
    standalone_second.source_index = 1;
    var second_rows = [_]protocol.InputRow{ process_first, standalone_second };
    var second_input = corpus.cycle(&config, 1, 1_000, second_rows.len, true);
    try std.testing.expectEqual(protocol.Status.ok, corpus.plan(
        second_session,
        &second_input,
        second_rows[0..].ptr,
        second_rows.len,
        actions[0..].ptr,
        actions.len,
        &second_snapshot,
    ));

    const first_software = first_session.softwares[first_session.findSoftware(9_003) orelse return error.MissingSoftwareSnapshot];
    const second_software = second_session.softwares[second_session.findSoftware(9_003) orelse return error.MissingSoftwareSnapshot];
    try std.testing.expectEqual(first_software.cpu_transition.applied, second_software.cpu_transition.applied);
    try std.testing.expectEqual(first_software.cpu_transition.owned, second_software.cpu_transition.owned);
    try std.testing.expectEqual(first_software.member_count, second_software.member_count);
    try std.testing.expectEqual(@as(u32, 1), first_software.member_count);
}

test "standalone and process adapter ownership conflicts reject the cycle" {
    var config = corpus.explicitConfig(protocol.FeatureFlags.adapter_cpu);
    const session = try coordinator.create(&config);
    defer coordinator.destroy(session);

    var process = corpus.processFact(
        1,
        9_104,
        9_004,
        94,
        904,
        20,
        .adapted,
        0,
        protocol.ProcessGrade.normal,
        .extreme,
        .normal,
        protocol.InputFlags.owns_cpu_grade,
    );
    process.applied_epoch = 44;
    var rows = [_]protocol.InputRow{
        corpus.softwareFact(0, 9_004, .optimize, .normal, protocol.InputFlags.owns_cpu_grade, 44),
        process,
    };
    var input = corpus.cycle(&config, 1, 1_000, rows.len, true);
    var actions: ActionBuffer = undefined;
    var snapshot: protocol.Snapshot = undefined;
    try std.testing.expectEqual(protocol.Status.conflicting_facts, corpus.plan(
        session,
        &input,
        rows[0..].ptr,
        rows.len,
        actions[0..].ptr,
        actions.len,
        &snapshot,
    ));
}

test "raw fact conflicts reject the whole cycle" {
    var config = corpus.explicitConfig(protocol.FeatureFlags.process_policy);
    const session = try coordinator.create(&config);
    defer coordinator.destroy(session);

    const base = corpus.processFact(0, 101, 1001, 11, 111, 20, .general_application, 0, protocol.ProcessGrade.normal, .normal, .normal, 0);
    var conflict = base;
    conflict.source_index = 1;
    conflict.base_score = 21;
    var rows = [_]protocol.InputRow{ base, conflict };
    var input = corpus.cycle(&config, 1, 1_000, rows.len, true);
    var actions: ActionBuffer = undefined;
    var snapshot: protocol.Snapshot = undefined;
    try std.testing.expectEqual(protocol.Status.conflicting_facts, corpus.plan(
        session,
        &input,
        rows[0..].ptr,
        rows.len,
        actions[0..].ptr,
        actions.len,
        &snapshot,
    ));
    try std.testing.expectEqual(@as(u64, 0), session.last_cycle_sequence);
}

test "process policy merges metrics waits three rounds and advances only on feedback" {
    var config = corpus.explicitConfig(protocol.FeatureFlags.process_policy);
    const session = try coordinator.create(&config);
    defer coordinator.destroy(session);

    var rows = lowScoreProcessRows(101, 1001, 11, 111);
    var actions: ActionBuffer = undefined;
    var snapshot: protocol.Snapshot = undefined;
    var sequence: u64 = 1;
    while (sequence <= 3) : (sequence += 1) {
        var input = corpus.cycle(&config, sequence, @as(i64, @intCast(sequence)) * 1_000, rows.len, sequence == 1);
        try std.testing.expectEqual(protocol.Status.ok, corpus.plan(
            session,
            &input,
            rows[0..].ptr,
            rows.len,
            actions[0..].ptr,
            actions.len,
            &snapshot,
        ));
        const process_action = findProcessAction(actions[0..snapshot.action_count], 101) orelse return error.MissingProcessAction;
        if (sequence < 3) {
            try std.testing.expectEqual(@as(u32, 0), process_action.flags & protocol.ActionFlags.requires_feedback);
        } else {
            try std.testing.expectEqual(protocol.ProcessGrade.level4, process_action.to_process_grade);
            try std.testing.expectEqual(@intFromEnum(protocol.ActionDisposition.apply), process_action.disposition);
            try std.testing.expect((process_action.flags & protocol.ActionFlags.requires_feedback) != 0);
            try std.testing.expectEqual(@as(u32, 0), process_action.flags & protocol.ActionFlags.atomic);
            try std.testing.expectEqual(@as(u64, 0), process_action.valid_mask & protocol.ActionValidity.atomic_group);
            try std.testing.expectEqual(@as(u64, 0), process_action.atomic_group_id);
            try std.testing.expectEqual(@as(u32, 0), process_action.group_member_index);
            try std.testing.expectEqual(@as(u32, 0), process_action.group_member_count);
            const feedback = corpus.feedbackFor(process_action.*, .succeeded, 3_100);
            try std.testing.expectEqual(protocol.Status.ok, coordinator.applyFeedback(
                session,
                @as([*]const protocol.Feedback, @ptrCast(&feedback)),
                1,
                1,
                &snapshot,
            ));
        }
    }

    var snapshot_rows: [corpus.default_process_capacity + corpus.default_software_capacity]protocol.SnapshotRow = undefined;
    try std.testing.expectEqual(protocol.Status.ok, coordinator.getSnapshot(
        session,
        &snapshot,
        snapshot_rows[0..].ptr,
        snapshot_rows.len,
    ));
    const process = findProcessSnapshot(snapshot_rows[0..snapshot.snapshot_row_count], 101) orelse return error.MissingProcessSnapshot;
    try std.testing.expectEqual(protocol.ProcessGrade.level4, process.applied_process_grade);
    try std.testing.expect((process.flags & protocol.SnapshotRowFlags.process_owned) != 0);
    try std.testing.expectEqual(@as(u32, 0), snapshot.inflight_count);
}

test "identity-less level4 apply remains a non-atomic single action" {
    var config = corpus.explicitConfig(protocol.FeatureFlags.process_policy);
    const session = try coordinator.create(&config);
    defer coordinator.destroy(session);

    var rows = lowScoreProcessRows(151, 1501, 15, 1151);
    for (&rows) |*row| {
        row.valid_mask &= ~(protocol.InputValidity.software_identity |
            protocol.InputValidity.software_score |
            protocol.InputValidity.score_member_count |
            protocol.InputValidity.applied_cpu_grade |
            protocol.InputValidity.applied_gpu_grade);
        row.software_key = 0;
        row.software_score = 0;
        row.score_member_count = 0;
    }
    var actions: ActionBuffer = undefined;
    var snapshot: protocol.Snapshot = undefined;
    var sequence: u64 = 1;
    while (sequence <= 3) : (sequence += 1) {
        var input = corpus.cycle(&config, sequence, @as(i64, @intCast(sequence)) * 1_000, rows.len, sequence == 1);
        try std.testing.expectEqual(protocol.Status.ok, corpus.plan(
            session,
            &input,
            rows[0..].ptr,
            rows.len,
            actions[0..].ptr,
            actions.len,
            &snapshot,
        ));
    }

    const action = findProcessAction(actions[0..snapshot.action_count], 151) orelse return error.MissingProcessAction;
    try std.testing.expectEqual(protocol.ProcessGrade.level4, action.to_process_grade);
    try std.testing.expectEqual(@intFromEnum(protocol.ActionDisposition.apply), action.disposition);
    try std.testing.expect((action.flags & protocol.ActionFlags.requires_feedback) != 0);
    try std.testing.expectEqual(@as(u32, 0), action.flags & protocol.ActionFlags.atomic);
    try std.testing.expectEqual(@as(u64, 0), action.valid_mask & protocol.ActionValidity.atomic_group);
    try std.testing.expectEqual(@as(u64, 0), action.valid_mask & protocol.ActionValidity.software_identity);
    try std.testing.expectEqual(@as(u64, 0), action.software_key);
    try std.testing.expectEqual(@as(u64, 0), action.atomic_group_id);
    try std.testing.expectEqual(@as(u32, 0), action.group_member_index);
    try std.testing.expectEqual(@as(u32, 0), action.group_member_count);
}

test "per-cycle action limit advances independent pending work one action at a time" {
    var config = corpus.explicitConfig(protocol.FeatureFlags.process_policy);
    const session = try coordinator.create(&config);
    defer coordinator.destroy(session);

    var rows = twoIndependentProcessRows();
    var actions: ActionBuffer = undefined;
    var snapshot: protocol.Snapshot = undefined;
    var sequence: u64 = 1;
    while (sequence <= 2) : (sequence += 1) {
        var input = corpus.cycle(&config, sequence, @as(i64, @intCast(sequence)) * 1_000, rows.len, sequence == 1);
        try std.testing.expectEqual(protocol.Status.ok, corpus.plan(
            session,
            &input,
            rows[0..].ptr,
            rows.len,
            actions[0..].ptr,
            actions.len,
            &snapshot,
        ));
    }

    var limited = corpus.cycle(&config, 3, 3_000, rows.len, false);
    limited.maximum_actions_this_cycle = 1;
    try std.testing.expectEqual(protocol.Status.ok, corpus.plan(
        session,
        &limited,
        rows[0..].ptr,
        rows.len,
        actions[0..].ptr,
        actions.len,
        &snapshot,
    ));
    try std.testing.expectEqual(@as(u32, 1), snapshot.action_count);
    try std.testing.expectEqual(@as(u32, 1), snapshot.inflight_count);
    const first_action = actions[0];
    try std.testing.expectEqual(@intFromEnum(protocol.ActionScope.process_policy), first_action.scope);
    try std.testing.expect((first_action.flags & protocol.ActionFlags.requires_feedback) != 0);

    const first_slot = session.findProcess(161, 16, 1_161) orelse return error.MissingProcessSnapshot;
    const second_slot = session.findProcess(162, 17, 1_162) orelse return error.MissingProcessSnapshot;
    const first_inflight = session.processes[first_slot].transition.inflight_action_id != 0;
    const second_inflight = session.processes[second_slot].transition.inflight_action_id != 0;
    try std.testing.expect(first_inflight != second_inflight);
    try std.testing.expect(session.processes[first_slot].transition.pending_count >= config.required_consecutive_decisions);
    try std.testing.expect(session.processes[second_slot].transition.pending_count >= config.required_consecutive_decisions);

    limited = corpus.cycle(&config, 4, 4_000, rows.len, false);
    limited.maximum_actions_this_cycle = 1;
    try std.testing.expectEqual(protocol.Status.ok, corpus.plan(
        session,
        &limited,
        rows[0..].ptr,
        rows.len,
        actions[0..].ptr,
        actions.len,
        &snapshot,
    ));
    try std.testing.expectEqual(@as(u32, 1), snapshot.action_count);
    try std.testing.expectEqual(@as(u32, 2), snapshot.inflight_count);
    try std.testing.expectEqual(@intFromEnum(protocol.ActionScope.process_policy), actions[0].scope);
    try std.testing.expect(actions[0].target_key != first_action.target_key);
    try std.testing.expect((actions[0].flags & protocol.ActionFlags.requires_feedback) != 0);
}

test "per-cycle action limit defers an atomic freeze group without splitting or leaking it" {
    var config = corpus.explicitConfig(protocol.FeatureFlags.process_policy);
    const session = try coordinator.create(&config);
    defer coordinator.destroy(session);

    var rows = twoProcessRows();
    var actions: ActionBuffer = undefined;
    var snapshot: protocol.Snapshot = undefined;
    var sequence: u64 = 1;
    while (sequence <= 2) : (sequence += 1) {
        var input = corpus.cycle(&config, sequence, @as(i64, @intCast(sequence)) * 1_000, rows.len, sequence == 1);
        try std.testing.expectEqual(protocol.Status.ok, corpus.plan(
            session,
            &input,
            rows[0..].ptr,
            rows.len,
            actions[0..].ptr,
            actions.len,
            &snapshot,
        ));
    }

    var limited = corpus.cycle(&config, 3, 3_000, rows.len, false);
    limited.maximum_actions_this_cycle = 1;
    try std.testing.expectEqual(protocol.Status.ok, corpus.plan(
        session,
        &limited,
        rows[0..].ptr,
        rows.len,
        actions[0..].ptr,
        actions.len,
        &snapshot,
    ));
    try std.testing.expectEqual(@as(u32, 1), snapshot.action_count);
    try std.testing.expectEqual(@as(u32, 0), snapshot.inflight_count);
    try std.testing.expect(findProcessAction(actions[0..snapshot.action_count], 101) == null);
    try std.testing.expect(findProcessAction(actions[0..snapshot.action_count], 102) == null);
    try std.testing.expectEqual(@as(usize, 0), occupiedAtomicGroupCount(session));

    var full = corpus.cycle(&config, 4, 4_000, rows.len, false);
    try std.testing.expectEqual(protocol.Status.ok, corpus.plan(
        session,
        &full,
        rows[0..].ptr,
        rows.len,
        actions[0..].ptr,
        actions.len,
        &snapshot,
    ));
    const first = findProcessAction(actions[0..snapshot.action_count], 101) orelse return error.MissingProcessAction;
    const second = findProcessAction(actions[0..snapshot.action_count], 102) orelse return error.MissingProcessAction;
    try std.testing.expect(first.atomic_group_id != 0);
    try std.testing.expectEqual(first.atomic_group_id, second.atomic_group_id);
    try std.testing.expectEqual(@as(u32, 2), first.group_member_count);
    try std.testing.expectEqual(@as(u32, 2), second.group_member_count);
    try std.testing.expectEqual(@as(u32, 2), snapshot.inflight_count);
    try std.testing.expectEqual(@as(usize, 1), occupiedAtomicGroupCount(session));
}

test "partial atomic freeze failure arms whole-software compensation" {
    var config = corpus.explicitConfig(protocol.FeatureFlags.process_policy);
    const session = try coordinator.create(&config);
    defer coordinator.destroy(session);

    var rows = twoProcessRows();
    var actions: ActionBuffer = undefined;
    var snapshot: protocol.Snapshot = undefined;
    var sequence: u64 = 1;
    while (sequence <= 3) : (sequence += 1) {
        var input = corpus.cycle(&config, sequence, @as(i64, @intCast(sequence)) * 1_000, rows.len, sequence == 1);
        try std.testing.expectEqual(protocol.Status.ok, corpus.plan(
            session,
            &input,
            rows[0..].ptr,
            rows.len,
            actions[0..].ptr,
            actions.len,
            &snapshot,
        ));
    }
    const first = findProcessAction(actions[0..snapshot.action_count], 101) orelse return error.MissingProcessAction;
    const second = findProcessAction(actions[0..snapshot.action_count], 102) orelse return error.MissingProcessAction;
    try std.testing.expectEqual(first.atomic_group_id, second.atomic_group_id);
    try std.testing.expect(first.atomic_group_id != 0);
    try std.testing.expect((first.flags & protocol.ActionFlags.atomic) != 0);
    try std.testing.expect((second.flags & protocol.ActionFlags.atomic) != 0);
    try std.testing.expect((first.valid_mask & protocol.ActionValidity.atomic_group) != 0);
    try std.testing.expect((second.valid_mask & protocol.ActionValidity.atomic_group) != 0);
    try std.testing.expectEqual(@as(u32, 2), first.group_member_count);
    try std.testing.expectEqual(@as(u32, 2), second.group_member_count);
    var feedbacks = [_]protocol.Feedback{
        corpus.feedbackFor(second.*, .failed_unchanged, 3_100),
        corpus.feedbackFor(first.*, .succeeded, 3_100),
    };
    try std.testing.expectEqual(protocol.Status.ok, coordinator.applyFeedback(
        session,
        feedbacks[0..].ptr,
        feedbacks.len,
        feedbacks.len,
        &snapshot,
    ));

    var next_input = corpus.cycle(&config, 4, 4_000, rows.len, false);
    try std.testing.expectEqual(protocol.Status.ok, corpus.plan(
        session,
        &next_input,
        rows[0..].ptr,
        rows.len,
        actions[0..].ptr,
        actions.len,
        &snapshot,
    ));
    const compensation = findProcessAction(actions[0..snapshot.action_count], 101) orelse return error.MissingProcessAction;
    try std.testing.expectEqual(@intFromEnum(protocol.ActionDisposition.restore), compensation.disposition);
    try std.testing.expectEqual(protocol.ProcessGrade.normal, compensation.to_process_grade);
    try std.testing.expect((compensation.flags & protocol.ActionFlags.compensation) != 0);
    const blocked = findProcessAction(actions[0..snapshot.action_count], 102) orelse return error.MissingProcessAction;
    try std.testing.expectEqual(@as(u32, 0), blocked.flags & protocol.ActionFlags.requires_feedback);
}

test "adapter capability filtering also waits three rounds" {
    var config = corpus.explicitConfig(protocol.FeatureFlags.adapter_cpu);
    const session = try coordinator.create(&config);
    defer coordinator.destroy(session);

    var static = corpus.processFact(0, 201, 2001, 21, 211, 30, .adapted, 0, protocol.ProcessGrade.normal, .normal, .normal, 0);
    static.cpu_capability_mask = (@as(u8, 1) << @intFromEnum(protocol.AdapterGrade.optimize)) |
        (@as(u8, 1) << @intFromEnum(protocol.AdapterGrade.normal)) |
        (@as(u8, 1) << @intFromEnum(protocol.AdapterGrade.extreme));
    var rows = [_]protocol.InputRow{
        static,
        corpus.metricFact(static, 1, .cpu_usage_percent, 100, 0),
    };
    var actions: ActionBuffer = undefined;
    var snapshot: protocol.Snapshot = undefined;
    var sequence: u64 = 1;
    while (sequence <= 3) : (sequence += 1) {
        var input = corpus.cycle(&config, sequence, @as(i64, @intCast(sequence)) * 1_000, rows.len, sequence == 1);
        try std.testing.expectEqual(protocol.Status.ok, corpus.plan(
            session,
            &input,
            rows[0..].ptr,
            rows.len,
            actions[0..].ptr,
            actions.len,
            &snapshot,
        ));
    }
    const software = findSoftwareAction(actions[0..snapshot.action_count], 2001) orelse return error.MissingSoftwareAction;
    try std.testing.expectEqual(@intFromEnum(protocol.AdapterGrade.optimize), software.to_cpu_grade);
    try std.testing.expectEqual(protocol.GradeDomains.cpu, software.domain_mask);
    try std.testing.expect((software.reason_mask & protocol.Reason.capability_clamped) != 0);
    try std.testing.expect((software.flags & protocol.ActionFlags.requires_feedback) != 0);
}

test "state uncertain preserves last-good and requires explicit authoritative resync" {
    var config = corpus.explicitConfig(protocol.FeatureFlags.process_policy);
    const session = try coordinator.create(&config);
    defer coordinator.destroy(session);
    var rows = lowScoreProcessRows(301, 3001, 31, 311);
    var actions: ActionBuffer = undefined;
    var snapshot: protocol.Snapshot = undefined;
    var sequence: u64 = 1;
    while (sequence <= 3) : (sequence += 1) {
        var input = corpus.cycle(&config, sequence, @as(i64, @intCast(sequence)) * 1_000, rows.len, sequence == 1);
        try std.testing.expectEqual(protocol.Status.ok, corpus.plan(
            session,
            &input,
            rows[0..].ptr,
            rows.len,
            actions[0..].ptr,
            actions.len,
            &snapshot,
        ));
    }
    const action = findProcessAction(actions[0..snapshot.action_count], 301) orelse return error.MissingProcessAction;
    const uncertain = corpus.feedbackFor(action.*, .state_uncertain, 3_100);
    try std.testing.expectEqual(protocol.Status.ok, coordinator.applyFeedback(
        session,
        @as([*]const protocol.Feedback, @ptrCast(&uncertain)),
        1,
        1,
        &snapshot,
    ));
    try std.testing.expect((snapshot.flags & protocol.SnapshotFlags.requires_authoritative_resync) != 0);

    var input = corpus.cycle(&config, 4, 4_000, rows.len, false);
    try std.testing.expectEqual(protocol.Status.unavailable, corpus.plan(
        session,
        &input,
        rows[0..].ptr,
        rows.len,
        actions[0..].ptr,
        actions.len,
        &snapshot,
    ));
    input.flags |= protocol.CycleFlags.authoritative_applied_facts;
    try std.testing.expectEqual(protocol.Status.ok, corpus.plan(
        session,
        &input,
        rows[0..].ptr,
        rows.len,
        actions[0..].ptr,
        actions.len,
        &snapshot,
    ));
    try std.testing.expectEqual(@as(u32, 0), snapshot.flags & protocol.SnapshotFlags.requires_authoritative_resync);
}

test "ownership lost without observed grade never invents actual state" {
    var config = corpus.explicitConfig(protocol.FeatureFlags.process_policy);
    const session = try coordinator.create(&config);
    defer coordinator.destroy(session);
    var rows = lowScoreProcessRows(302, 3002, 32, 312);
    var actions: ActionBuffer = undefined;
    var snapshot: protocol.Snapshot = undefined;
    var sequence: u64 = 1;
    while (sequence <= 3) : (sequence += 1) {
        var input = corpus.cycle(&config, sequence, @as(i64, @intCast(sequence)) * 1_000, rows.len, sequence == 1);
        try std.testing.expectEqual(protocol.Status.ok, corpus.plan(
            session,
            &input,
            rows[0..].ptr,
            rows.len,
            actions[0..].ptr,
            actions.len,
            &snapshot,
        ));
    }
    const action = findProcessAction(actions[0..snapshot.action_count], 302) orelse return error.MissingProcessAction;
    var ownership_lost = corpus.feedbackFor(action.*, .ownership_lost, 3_100);
    ownership_lost.actual_process_grade = protocol.ProcessGrade.normal;
    var invalid_ownership_lost = ownership_lost;
    invalid_ownership_lost.valid_mask |= protocol.FeedbackValidity.actual_process_grade;
    try std.testing.expectEqual(protocol.Status.feedback_mismatch, coordinator.applyFeedback(
        session,
        @as([*]const protocol.Feedback, @ptrCast(&invalid_ownership_lost)),
        1,
        1,
        &snapshot,
    ));
    try std.testing.expectEqual(protocol.Status.ok, coordinator.applyFeedback(
        session,
        @as([*]const protocol.Feedback, @ptrCast(&ownership_lost)),
        1,
        1,
        &snapshot,
    ));
    try std.testing.expect((snapshot.flags & protocol.SnapshotFlags.requires_authoritative_resync) != 0);

    var snapshot_rows: [corpus.default_process_capacity + corpus.default_software_capacity]protocol.SnapshotRow = undefined;
    try std.testing.expectEqual(protocol.Status.ok, coordinator.getSnapshot(
        session,
        &snapshot,
        snapshot_rows[0..].ptr,
        snapshot_rows.len,
    ));
    const process = findProcessSnapshot(snapshot_rows[0..snapshot.snapshot_row_count], 302) orelse return error.MissingProcessSnapshot;
    try std.testing.expectEqual(protocol.ProcessGrade.normal, process.applied_process_grade);
    try std.testing.expectEqual(@as(u32, 0), process.flags & protocol.SnapshotRowFlags.process_owned);
}

test "critical membership changes do not extend the stable event boost" {
    var config = corpus.explicitConfig(protocol.FeatureFlags.critical_events);
    const session = try coordinator.create(&config);
    defer coordinator.destroy(session);
    var actions: ActionBuffer = undefined;
    var snapshot: protocol.Snapshot = undefined;

    const first = corpus.processFact(0, 401, 4001, 41, 411, 85, .high_performance, 0, protocol.ProcessGrade.normal, .normal, .normal, 0);
    var first_rows = [_]protocol.InputRow{first};
    var input = corpus.cycle(&config, 1, 1_000, first_rows.len, true);
    try std.testing.expectEqual(protocol.Status.ok, corpus.plan(
        session,
        &input,
        first_rows[0..].ptr,
        first_rows.len,
        actions[0..].ptr,
        actions.len,
        &snapshot,
    ));
    try std.testing.expectEqual(@as(i64, 11_000), session.event_boost_until_ms);
    try std.testing.expectEqual(@as(u32, 1_000), snapshot.wake_after_ms);
    try std.testing.expect((snapshot.reason_mask & protocol.Reason.critical_fact_changed) != 0);

    const second = corpus.processFact(1, 402, 4001, 42, 412, 85, .high_performance, 0, protocol.ProcessGrade.normal, .normal, .normal, 0);
    var joined_rows = [_]protocol.InputRow{ first, second };
    joined_rows[0].source_index = 0;
    input = corpus.cycle(&config, 2, 2_000, joined_rows.len, false);
    try std.testing.expectEqual(protocol.Status.ok, corpus.plan(
        session,
        &input,
        joined_rows[0..].ptr,
        joined_rows.len,
        actions[0..].ptr,
        actions.len,
        &snapshot,
    ));
    try std.testing.expectEqual(@as(i64, 11_000), session.event_boost_until_ms);
    try std.testing.expect((snapshot.reason_mask & protocol.Reason.critical_membership_changed) != 0);
    try std.testing.expectEqual(@as(u64, 0), snapshot.reason_mask & protocol.Reason.critical_fact_changed);
}

test "game grace is inclusive and participates in next wake" {
    var config = corpus.explicitConfig(protocol.FeatureFlags.critical_events);
    config.event_boost_ms = 20_000;
    config.event_interval_ms = 20_000;
    const session = try coordinator.create(&config);
    defer coordinator.destroy(session);
    var actions: ActionBuffer = undefined;
    var snapshot: protocol.Snapshot = undefined;

    const game = corpus.processFact(0, 501, 5001, 51, 511, 95, .game, 0, protocol.ProcessGrade.normal, .normal, .normal, 0);
    var rows = [_]protocol.InputRow{game};
    var input = corpus.cycle(&config, 1, 1_000, rows.len, true);
    try std.testing.expectEqual(protocol.Status.ok, corpus.plan(
        session,
        &input,
        rows[0..].ptr,
        rows.len,
        actions[0..].ptr,
        actions.len,
        &snapshot,
    ));
    try std.testing.expectEqual(@as(u32, 10_001), snapshot.wake_after_ms);

    input = corpus.cycle(&config, 2, 11_000, rows.len, false);
    try std.testing.expectEqual(protocol.Status.ok, corpus.plan(
        session,
        &input,
        rows[0..].ptr,
        rows.len,
        actions[0..].ptr,
        actions.len,
        &snapshot,
    ));
    const process = findProcessAction(actions[0..snapshot.action_count], 501) orelse return error.MissingProcessAction;
    try std.testing.expect((process.reason_mask & protocol.Reason.startup_grace_active) != 0);
    try std.testing.expectEqual(@as(u32, 1), snapshot.wake_after_ms);

    input = corpus.cycle(&config, 3, 11_001, rows.len, false);
    try std.testing.expectEqual(protocol.Status.ok, corpus.plan(
        session,
        &input,
        rows[0..].ptr,
        rows.len,
        actions[0..].ptr,
        actions.len,
        &snapshot,
    ));
    const expired = findProcessAction(actions[0..snapshot.action_count], 501) orelse return error.MissingProcessAction;
    try std.testing.expectEqual(@as(u64, 0), expired.reason_mask & protocol.Reason.startup_grace_active);
}

test "game and general application paired inputs differ only in startup grace" {
    const cases = [_]struct {
        kind: protocol.SoftwareKind,
        grace_expected: bool,
    }{
        .{ .kind = .game, .grace_expected = true },
        .{ .kind = .general_application, .grace_expected = false },
    };

    for (cases) |case| {
        var config = corpus.explicitConfig(protocol.FeatureFlags.critical_events);
        config.event_boost_ms = 20_000;
        config.event_interval_ms = 20_000;
        const session = try coordinator.create(&config);
        defer coordinator.destroy(session);
        var actions: ActionBuffer = undefined;
        var snapshot: protocol.Snapshot = undefined;
        const process_fact = corpus.processFact(
            0,
            502,
            5002,
            52,
            512,
            35,
            case.kind,
            0,
            protocol.ProcessGrade.normal,
            .normal,
            .normal,
            0,
        );
        var rows = [_]protocol.InputRow{process_fact};
        var input = corpus.cycle(&config, 1, 1_000, rows.len, true);
        try std.testing.expectEqual(protocol.Status.ok, corpus.plan(
            session,
            &input,
            rows[0..].ptr,
            rows.len,
            actions[0..].ptr,
            actions.len,
            &snapshot,
        ));

        input = corpus.cycle(&config, 2, 11_000, rows.len, false);
        try std.testing.expectEqual(protocol.Status.ok, corpus.plan(
            session,
            &input,
            rows[0..].ptr,
            rows.len,
            actions[0..].ptr,
            actions.len,
            &snapshot,
        ));
        const active = findProcessAction(actions[0..snapshot.action_count], 502) orelse return error.MissingProcessAction;
        try std.testing.expectEqual(
            case.grace_expected,
            (active.reason_mask & protocol.Reason.startup_grace_active) != 0,
        );
        if (case.grace_expected) {
            try std.testing.expectEqual(@as(u32, 1), snapshot.wake_after_ms);
        } else {
            try std.testing.expect(snapshot.wake_after_ms > 1);
        }

        input = corpus.cycle(&config, 3, 11_001, rows.len, false);
        try std.testing.expectEqual(protocol.Status.ok, corpus.plan(
            session,
            &input,
            rows[0..].ptr,
            rows.len,
            actions[0..].ptr,
            actions.len,
            &snapshot,
        ));
        const expired = findProcessAction(actions[0..snapshot.action_count], 502) orelse return error.MissingProcessAction;
        try std.testing.expectEqual(
            @as(u64, 0),
            expired.reason_mask & protocol.Reason.startup_grace_active,
        );
    }
}

test "GPU adapter grade uses only the canonical score" {
    var config = corpus.explicitConfig(protocol.FeatureFlags.adapter_gpu);
    const session = try coordinator.create(&config);
    defer coordinator.destroy(session);
    var actions: ActionBuffer = undefined;
    var snapshot: protocol.Snapshot = undefined;

    const base = corpus.processFact(0, 601, 6001, 61, 611, 20, .adapted, 0, protocol.ProcessGrade.normal, .normal, .normal, 0);
    var rows = [_]protocol.InputRow{
        base,
        corpus.metricFact(base, 1, .gpu_usage_percent, 2, 3),
        corpus.metricFact(base, 2, .vram_usage_percent, 1, 3),
    };
    var sequence: u64 = 1;
    while (sequence <= 3) : (sequence += 1) {
        var input = corpus.cycle(&config, sequence, @as(i64, @intCast(sequence)) * 1_000, rows.len, sequence == 1);
        try std.testing.expectEqual(protocol.Status.ok, corpus.plan(
            session,
            &input,
            rows[0..].ptr,
            rows.len,
            actions[0..].ptr,
            actions.len,
            &snapshot,
        ));
    }
    const software = findSoftwareAction(actions[0..snapshot.action_count], 6001) orelse return error.MissingSoftwareAction;
    try std.testing.expectEqual(protocol.GradeDomains.gpu, software.domain_mask);
    try std.testing.expectEqual(@intFromEnum(protocol.AdapterGrade.freeze), software.to_gpu_grade);
}

test "restore sorts before A1 and retained rows" {
    var config = corpus.explicitConfig(protocol.FeatureFlags.process_policy);
    const session = try coordinator.create(&config);
    defer coordinator.destroy(session);
    var actions: ActionBuffer = undefined;
    var snapshot: protocol.Snapshot = undefined;

    const restore_base = corpus.processFact(0, 701, 7001, 71, 711, 85, .general_application, 0, protocol.ProcessGrade.level4, .normal, .normal, protocol.InputFlags.owns_process_grade);
    const a1_base = corpus.processFact(2, 702, 7002, 72, 712, 90, .general_application, protocol.InputFlags.foreground_focused, protocol.ProcessGrade.normal, .normal, .normal, 0);
    var rows = [_]protocol.InputRow{
        restore_base,
        corpus.metricFact(restore_base, 1, .cpu_usage_percent, 0, 0),
        a1_base,
        corpus.metricFact(a1_base, 3, .cpu_usage_percent, 100, 0),
    };
    var sequence: u64 = 1;
    while (sequence <= 3) : (sequence += 1) {
        var input = corpus.cycle(&config, sequence, @as(i64, @intCast(sequence)) * 1_000, rows.len, sequence == 1);
        try std.testing.expectEqual(protocol.Status.ok, corpus.plan(
            session,
            &input,
            rows[0..].ptr,
            rows.len,
            actions[0..].ptr,
            actions.len,
            &snapshot,
        ));
    }
    try std.testing.expectEqual(@as(u64, 701), actions[0].target_key);
    try std.testing.expectEqual(@intFromEnum(protocol.ActionDisposition.restore), actions[0].disposition);
    try std.testing.expectEqual(@as(u64, 702), actions[1].target_key);
    try std.testing.expectEqual(protocol.ProcessGrade.a1, actions[1].to_process_grade);
}

test "failed unchanged keeps last-good and retries only after backoff" {
    var config = corpus.explicitConfig(protocol.FeatureFlags.process_policy);
    const session = try coordinator.create(&config);
    defer coordinator.destroy(session);
    var rows = lowScoreProcessRows(801, 8001, 81, 811);
    var actions: ActionBuffer = undefined;
    var snapshot: protocol.Snapshot = undefined;
    var sequence: u64 = 1;
    while (sequence <= 3) : (sequence += 1) {
        var input = corpus.cycle(&config, sequence, @as(i64, @intCast(sequence)) * 1_000, rows.len, sequence == 1);
        try std.testing.expectEqual(protocol.Status.ok, corpus.plan(
            session,
            &input,
            rows[0..].ptr,
            rows.len,
            actions[0..].ptr,
            actions.len,
            &snapshot,
        ));
    }
    const first = findProcessAction(actions[0..snapshot.action_count], 801) orelse return error.MissingProcessAction;
    const failed = corpus.feedbackFor(first.*, .failed_unchanged, 3_100);
    try std.testing.expectEqual(protocol.Status.ok, coordinator.applyFeedback(
        session,
        @as([*]const protocol.Feedback, @ptrCast(&failed)),
        1,
        1,
        &snapshot,
    ));
    var input = corpus.cycle(&config, 4, 4_000, rows.len, false);
    try std.testing.expectEqual(protocol.Status.ok, corpus.plan(
        session,
        &input,
        rows[0..].ptr,
        rows.len,
        actions[0..].ptr,
        actions.len,
        &snapshot,
    ));
    const backed_off = findProcessAction(actions[0..snapshot.action_count], 801) orelse return error.MissingProcessAction;
    try std.testing.expectEqual(@as(u32, 0), backed_off.flags & protocol.ActionFlags.requires_feedback);
    try std.testing.expect((backed_off.reason_mask & protocol.Reason.retry_backoff) != 0);

    input = corpus.cycle(&config, 5, 8_100, rows.len, false);
    try std.testing.expectEqual(protocol.Status.ok, corpus.plan(
        session,
        &input,
        rows[0..].ptr,
        rows.len,
        actions[0..].ptr,
        actions.len,
        &snapshot,
    ));
    const retry = findProcessAction(actions[0..snapshot.action_count], 801) orelse return error.MissingProcessAction;
    try std.testing.expect((retry.flags & protocol.ActionFlags.requires_feedback) != 0);
    try std.testing.expect((retry.flags & protocol.ActionFlags.retry) != 0);
    try std.testing.expectEqual(protocol.ProcessGrade.normal, retry.from_process_grade);
}

test "reservation timeout is a state-uncertain resync boundary" {
    var config = corpus.explicitConfig(protocol.FeatureFlags.process_policy);
    config.normal_interval_ms = 60_000;
    const session = try coordinator.create(&config);
    defer coordinator.destroy(session);
    var rows = lowScoreProcessRows(901, 9001, 91, 911);
    var actions: ActionBuffer = undefined;
    var snapshot: protocol.Snapshot = undefined;
    var sequence: u64 = 1;
    while (sequence <= 3) : (sequence += 1) {
        var input = corpus.cycle(&config, sequence, @as(i64, @intCast(sequence)) * 1_000, rows.len, sequence == 1);
        try std.testing.expectEqual(protocol.Status.ok, corpus.plan(
            session,
            &input,
            rows[0..].ptr,
            rows.len,
            actions[0..].ptr,
            actions.len,
            &snapshot,
        ));
    }
    try std.testing.expectEqual(@as(u32, 30_000), snapshot.wake_after_ms);
    var input = corpus.cycle(&config, 4, 33_001, rows.len, false);
    try std.testing.expectEqual(protocol.Status.unavailable, corpus.plan(
        session,
        &input,
        rows[0..].ptr,
        rows.len,
        actions[0..].ptr,
        actions.len,
        &snapshot,
    ));
    try std.testing.expect(session.requires_authoritative_resync);
    try std.testing.expectEqual(@as(u32, 0), session.inflightCount());
    try std.testing.expect((session.last_reason_mask & protocol.Reason.reservation_timed_out) != 0);

    input.flags |= protocol.CycleFlags.authoritative_applied_facts;
    try std.testing.expectEqual(protocol.Status.ok, corpus.plan(
        session,
        &input,
        rows[0..].ptr,
        rows.len,
        actions[0..].ptr,
        actions.len,
        &snapshot,
    ));
}

test "missing surface facts remain unknown rather than becoming background" {
    var config = corpus.explicitConfig(protocol.FeatureFlags.process_policy);
    const session = try coordinator.create(&config);
    defer coordinator.destroy(session);
    var row = corpus.processFact(0, 1001, 10_001, 101, 1_011, 20, .general_application, 0, protocol.ProcessGrade.normal, .normal, .normal, 0);
    row.valid_mask &= ~protocol.InputValidity.surface_facts;
    row.flags &= ~(protocol.InputFlags.running | protocol.InputFlags.foreground_focused |
        protocol.InputFlags.has_visible_window | protocol.InputFlags.has_background_window |
        protocol.InputFlags.has_hidden_window);
    var rows = [_]protocol.InputRow{row};
    var input = corpus.cycle(&config, 1, 1_000, rows.len, true);
    var actions: ActionBuffer = undefined;
    var snapshot: protocol.Snapshot = undefined;
    try std.testing.expectEqual(protocol.Status.ok, corpus.plan(
        session,
        &input,
        rows[0..].ptr,
        rows.len,
        actions[0..].ptr,
        actions.len,
        &snapshot,
    ));
    var snapshot_rows: [corpus.default_process_capacity + corpus.default_software_capacity]protocol.SnapshotRow = undefined;
    try std.testing.expectEqual(protocol.Status.ok, coordinator.getSnapshot(
        session,
        &snapshot,
        snapshot_rows[0..].ptr,
        snapshot_rows.len,
    ));
    const process = findProcessSnapshot(snapshot_rows[0..snapshot.snapshot_row_count], 1001) orelse return error.MissingProcessSnapshot;
    try std.testing.expectEqual(@intFromEnum(protocol.RuntimeState.unknown), process.runtime_state);
    try std.testing.expect((process.reason_mask & protocol.Reason.missing_surface_facts) != 0);
}

test "module-local C ABI creates queries and destroys an opaque session" {
    var config = corpus.explicitConfig(protocol.FeatureFlags.process_policy);
    var handle: ?*anyopaque = null;
    try std.testing.expectEqual(@as(i32, @intFromEnum(protocol.Status.ok)), root.abi.rm_smart_coordinator_create(&config, &handle));
    defer root.abi.rm_smart_coordinator_destroy(handle);
    try std.testing.expect(handle != null);
    var capacity: protocol.Capacity = undefined;
    try std.testing.expectEqual(@as(i32, @intFromEnum(protocol.Status.ok)), root.abi.rm_smart_coordinator_query_capacity(handle, &capacity));
    try std.testing.expectEqual(config.max_input_rows, capacity.input_row_capacity);
    try std.testing.expectEqual(protocol.abi_version, root.abi.rm_smart_coordinator_abi_version());
}

test "feedback batches validate transactionally before advancing any action" {
    var config = corpus.explicitConfig(protocol.FeatureFlags.process_policy);
    const session = try coordinator.create(&config);
    defer coordinator.destroy(session);
    var rows = lowScoreProcessRows(1201, 12_001, 121, 1_211);
    var actions: ActionBuffer = undefined;
    var snapshot: protocol.Snapshot = undefined;
    var sequence: u64 = 1;
    while (sequence <= 3) : (sequence += 1) {
        var input = corpus.cycle(&config, sequence, @as(i64, @intCast(sequence)) * 1_000, rows.len, sequence == 1);
        try std.testing.expectEqual(protocol.Status.ok, corpus.plan(
            session,
            &input,
            rows[0..].ptr,
            rows.len,
            actions[0..].ptr,
            actions.len,
            &snapshot,
        ));
    }
    const action = findProcessAction(actions[0..snapshot.action_count], 1201) orelse return error.MissingProcessAction;
    const item = corpus.feedbackFor(action.*, .succeeded, 3_100);
    var duplicates = [_]protocol.Feedback{ item, item };
    try std.testing.expectEqual(protocol.Status.feedback_mismatch, coordinator.applyFeedback(
        session,
        duplicates[0..].ptr,
        duplicates.len,
        duplicates.len,
        &snapshot,
    ));
    try std.testing.expectEqual(@as(u32, 1), session.inflightCount());

    var bad_abi = item;
    bad_abi.struct_size -= 1;
    try std.testing.expectEqual(protocol.Status.abi_mismatch, coordinator.applyFeedback(
        session,
        @as([*]const protocol.Feedback, @ptrCast(&bad_abi)),
        1,
        1,
        &snapshot,
    ));
    try std.testing.expectEqual(@as(u32, 1), session.inflightCount());
    try std.testing.expectEqual(protocol.Status.ok, coordinator.applyFeedback(
        session,
        @as([*]const protocol.Feedback, @ptrCast(&item)),
        1,
        1,
        &snapshot,
    ));
    try std.testing.expectEqual(@as(u32, 0), session.inflightCount());
}

test "CPU and GPU adapter domains may hold independent inflight reservations" {
    var config = corpus.explicitConfig(protocol.FeatureFlags.adapter_cpu | protocol.FeatureFlags.adapter_gpu);
    const session = try coordinator.create(&config);
    defer coordinator.destroy(session);
    const base = corpus.processFact(0, 1301, 13_001, 131, 1_311, 60, .adapted, 0, protocol.ProcessGrade.normal, .normal, .normal, 0);
    var cpu_rows = [_]protocol.InputRow{
        base,
        corpus.metricFact(base, 1, .cpu_usage_percent, 100, 0),
    };
    var actions: ActionBuffer = undefined;
    var snapshot: protocol.Snapshot = undefined;
    var sequence: u64 = 1;
    while (sequence <= 3) : (sequence += 1) {
        var input = corpus.cycle(&config, sequence, @as(i64, @intCast(sequence)) * 1_000, cpu_rows.len, sequence == 1);
        try std.testing.expectEqual(protocol.Status.ok, corpus.plan(
            session,
            &input,
            cpu_rows[0..].ptr,
            cpu_rows.len,
            actions[0..].ptr,
            actions.len,
            &snapshot,
        ));
    }
    const cpu_action_pointer = findSoftwareAction(actions[0..snapshot.action_count], 13_001) orelse return error.MissingSoftwareAction;
    try std.testing.expectEqual(protocol.GradeDomains.cpu, cpu_action_pointer.domain_mask);
    const cpu_action = cpu_action_pointer.*;

    var gpu_rows = [_]protocol.InputRow{
        base,
        corpus.metricFact(base, 1, .cpu_usage_percent, 100, 0),
        corpus.metricFact(base, 2, .gpu_usage_percent, 2, 5),
        corpus.metricFact(base, 3, .vram_usage_percent, 1, 5),
    };
    sequence = 4;
    while (sequence <= 6) : (sequence += 1) {
        var input = corpus.cycle(&config, sequence, @as(i64, @intCast(sequence)) * 1_000, gpu_rows.len, false);
        try std.testing.expectEqual(protocol.Status.ok, corpus.plan(
            session,
            &input,
            gpu_rows[0..].ptr,
            gpu_rows.len,
            actions[0..].ptr,
            actions.len,
            &snapshot,
        ));
    }
    const gpu_action_pointer = findSoftwareAction(actions[0..snapshot.action_count], 13_001) orelse return error.MissingSoftwareAction;
    try std.testing.expectEqual(protocol.GradeDomains.gpu, gpu_action_pointer.domain_mask);
    const gpu_action = gpu_action_pointer.*;
    try std.testing.expectEqual(@as(u32, 0), gpu_action.valid_mask & protocol.ActionValidity.cpu_score);
    try std.testing.expect((gpu_action.valid_mask & protocol.ActionValidity.gpu_score) != 0);
    try std.testing.expectEqual(@as(u32, 2), session.inflightCount());

    var feedbacks = [_]protocol.Feedback{
        corpus.feedbackFor(gpu_action, .succeeded, 6_100),
        corpus.feedbackFor(cpu_action, .succeeded, 6_100),
    };
    try std.testing.expectEqual(protocol.Status.ok, coordinator.applyFeedback(
        session,
        feedbacks[0..].ptr,
        feedbacks.len,
        feedbacks.len,
        &snapshot,
    ));
    try std.testing.expectEqual(@as(u32, 0), session.inflightCount());
}

test "process grade boundary corpus uses only direct CPU occupancy scores" {
    try expectFirstRoundDesiredGrade(20, 0, 50, 0, protocol.ProcessGrade.level4);
    try expectFirstRoundDesiredGrade(21, 0, 50, 0, protocol.ProcessGrade.level3);
    try expectFirstRoundDesiredGrade(30, 0, 20, 0, protocol.ProcessGrade.level3);
    try expectFirstRoundDesiredGrade(80, 0, 100, 0, protocol.ProcessGrade.level2);
    try expectFirstRoundDesiredGrade(40, protocol.InputFlags.foreground_focused, 100, 0, protocol.ProcessGrade.level1);
    try expectFirstRoundDesiredGrade(60, protocol.InputFlags.foreground_focused, 100, 0, protocol.ProcessGrade.normal);
    try expectFirstRoundDesiredGrade(90, protocol.InputFlags.foreground_focused, 100, 0, protocol.ProcessGrade.a1);
    try expectFirstRoundDesiredGrade(80, protocol.InputFlags.foreground_focused, 100, 0, protocol.ProcessGrade.normal);
    try expectFirstRoundDesiredGrade(20, 0, 50, 1, protocol.ProcessGrade.level3);
    try expectFirstRoundDesiredGrade(20, 0, 50, 2, protocol.ProcessGrade.normal);
}

test "process CPU score remains stable across cycles" {
    var config = corpus.explicitConfig(protocol.FeatureFlags.process_policy);
    const session = try coordinator.create(&config);
    defer coordinator.destroy(session);
    const base = corpus.processFact(0, 17_001, 170_001, 171, 1_711, 20, .general_application, 0, protocol.ProcessGrade.normal, .normal, .normal, 0);
    var rows = [_]protocol.InputRow{
        base,
        corpus.metricFact(base, 1, .cpu_usage_percent, 50, 0),
    };
    var actions: ActionBuffer = undefined;
    var snapshot: protocol.Snapshot = undefined;
    var input = corpus.cycle(&config, 1, 1_000, rows.len, true);
    input.flags |= protocol.CycleFlags.score_only;
    try std.testing.expectEqual(protocol.Status.ok, corpus.plan(
        session,
        &input,
        rows[0..].ptr,
        rows.len,
        actions[0..].ptr,
        actions.len,
        &snapshot,
    ));
    const first = findProcessAction(actions[0..snapshot.action_count], 17_001) orelse return error.MissingProcessAction;
    try std.testing.expectApproxEqAbs(@as(f64, 4.5), first.cpu_score, 0.000_000_1);
    try std.testing.expect((first.valid_mask & protocol.ActionValidity.cpu_score) != 0);

    input = corpus.cycle(&config, 2, 2_000, rows.len, false);
    input.flags |= protocol.CycleFlags.score_only;
    try std.testing.expectEqual(protocol.Status.ok, corpus.plan(
        session,
        &input,
        rows[0..].ptr,
        rows.len,
        actions[0..].ptr,
        actions.len,
        &snapshot,
    ));
    const second = findProcessAction(actions[0..snapshot.action_count], 17_001) orelse return error.MissingProcessAction;
    try std.testing.expectApproxEqAbs(first.cpu_score, second.cpu_score, 0.000_000_1);
    try std.testing.expectEqual(first.to_process_grade, second.to_process_grade);
}

test "zero CPU occupancy produces an exact zero score" {
    var config = corpus.explicitConfig(protocol.FeatureFlags.process_policy);
    const session = try coordinator.create(&config);
    defer coordinator.destroy(session);
    const base = corpus.processFact(0, 17_002, 170_002, 172, 1_712, 80, .general_application, protocol.InputFlags.foreground_focused, protocol.ProcessGrade.normal, .normal, .normal, 0);
    var rows = [_]protocol.InputRow{
        base,
        corpus.metricFact(base, 1, .cpu_usage_percent, 0, 0),
    };
    var input = corpus.cycle(&config, 1, 1_000, rows.len, true);
    input.flags |= protocol.CycleFlags.score_only;
    var actions: ActionBuffer = undefined;
    var snapshot: protocol.Snapshot = undefined;
    try std.testing.expectEqual(protocol.Status.ok, corpus.plan(
        session,
        &input,
        rows[0..].ptr,
        rows.len,
        actions[0..].ptr,
        actions.len,
        &snapshot,
    ));
    const action = findProcessAction(actions[0..snapshot.action_count], 17_002) orelse return error.MissingProcessAction;
    try std.testing.expectEqual(@as(f64, 0), action.cpu_score);
    try std.testing.expect((action.valid_mask & protocol.ActionValidity.cpu_score) != 0);
}

test "adapter CPU score sums member process scores without process-count inflation" {
    const single_base = corpus.processFact(0, 18_001, 180_001, 181, 1_811, 80, .adapted, 0, protocol.ProcessGrade.normal, .normal, .normal, 0);
    var single_rows = [_]protocol.InputRow{
        single_base,
        corpus.metricFact(single_base, 1, .cpu_usage_percent, 50, 0),
    };
    const split_first = corpus.processFact(0, 18_101, 180_101, 182, 1_812, 80, .adapted, 0, protocol.ProcessGrade.normal, .normal, .normal, 0);
    const split_second = corpus.processFact(2, 18_102, 180_101, 183, 1_813, 80, .adapted, 0, protocol.ProcessGrade.normal, .normal, .normal, 0);
    var split_first_cpu = corpus.metricFact(split_first, 1, .cpu_usage_percent, 25, 0);
    var split_second_cpu = corpus.metricFact(split_second, 3, .cpu_usage_percent, 25, 0);
    corpus.setSoftwareScore(&split_first_cpu, 18, 2);
    corpus.setSoftwareScore(&split_second_cpu, 18, 2);
    var split_rows = [_]protocol.InputRow{
        split_first,
        split_first_cpu,
        split_second,
        split_second_cpu,
    };

    const single = try scoreSoftwareCpu(single_rows[0..], 180_001);
    const split = try scoreSoftwareCpu(split_rows[0..], 180_101);
    try std.testing.expectApproxEqAbs(@as(f64, 18), single.member_score, 0.000_000_1);
    try std.testing.expectApproxEqAbs(single.member_score, split.member_score, 0.000_000_1);
    try std.testing.expectApproxEqAbs(single.action_score, split.action_score, 0.000_000_1);
}

test "adapter CPU consumes the canonical software score" {
    const base = corpus.processFact(0, 18_201, 180_201, 184, 1_814, 80, .adapted, 0, protocol.ProcessGrade.normal, .normal, .normal, 0);
    var cpu = corpus.metricFact(base, 1, .cpu_usage_percent, 50, 0);
    corpus.setCanonicalScores(&cpu, 10, 10, 1);
    var rows = [_]protocol.InputRow{
        base,
        cpu,
    };
    var config = corpus.explicitConfig(protocol.FeatureFlags.adapter_cpu);
    const background_process = @intFromEnum(protocol.RuntimeState.background_process);
    config.process_state_multipliers[background_process] = 0.25;
    config.cpu_adapter.state_multipliers[background_process] = 0.25;
    config.gpu_adapter.state_multipliers[background_process] = 1.50;

    const scored = try scoreSoftwareCpuWithConfig(rows[0..], 180_201, config);
    try std.testing.expectApproxEqAbs(@as(f64, 10), scored.member_score, 0.000_000_1);
}

test "adapter GPU consumes the canonical software score" {
    const base = corpus.processFact(0, 18_251, 180_251, 189, 1_819, 80, .adapted, 0, protocol.ProcessGrade.normal, .normal, .normal, 0);
    var gpu = corpus.metricFact(base, 1, .gpu_usage_percent, 50, 7);
    corpus.setCanonicalScores(&gpu, 60, 60, 1);
    var rows = [_]protocol.InputRow{
        base,
        gpu,
        corpus.metricFact(base, 2, .vram_usage_percent, 1, 7),
    };
    var config = corpus.explicitConfig(protocol.FeatureFlags.adapter_gpu);
    const background_process = @intFromEnum(protocol.RuntimeState.background_process);
    config.process_state_multipliers[background_process] = 0.25;
    config.cpu_adapter.state_multipliers[background_process] = 0.25;
    config.gpu_adapter.state_multipliers[background_process] = 1.50;

    const session = try coordinator.create(&config);
    defer coordinator.destroy(session);
    var input = corpus.cycle(&config, 1, 1_000, rows.len, true);
    input.flags |= protocol.CycleFlags.score_only;
    var actions: ActionBuffer = undefined;
    var snapshot: protocol.Snapshot = undefined;
    try std.testing.expectEqual(protocol.Status.ok, corpus.plan(
        session,
        &input,
        rows[0..].ptr,
        rows.len,
        actions[0..].ptr,
        actions.len,
        &snapshot,
    ));
    const software_slot = session.findSoftware(180_251) orelse
        return error.MissingSoftwareSnapshot;
    const software = session.softwares[software_slot];
    try std.testing.expect(software.member_gpu_score_valid);
    try std.testing.expect(!software.adapter_cpu_score_valid);
    try std.testing.expect(software.adapter_gpu_score_valid);
    try std.testing.expectApproxEqAbs(@as(f64, 60), software.member_gpu_score, 0.000_000_1);
    const action = findSoftwareAction(actions[0..snapshot.action_count], 180_251) orelse
        return error.MissingSoftwareAction;
    try std.testing.expect((action.valid_mask & protocol.ActionValidity.gpu_score) != 0);
    try std.testing.expectEqual(@as(u64, 0), action.valid_mask & protocol.ActionValidity.cpu_score);
    try std.testing.expectApproxEqAbs(software.adapter_gpu_score, action.gpu_score, 0.000_000_1);
}

test "adapter action preserves independent CPU and GPU scores" {
    const base = corpus.processFact(0, 18_271, 180_271, 190, 1_820, 80, .adapted, 0, protocol.ProcessGrade.normal, .normal, .normal, 0);
    var cpu = corpus.metricFact(base, 1, .cpu_usage_percent, 25, 0);
    corpus.setCanonicalScores(&cpu, 10, 10, 1);
    var gpu = corpus.metricFact(base, 2, .gpu_usage_percent, 75, 7);
    corpus.setCanonicalScores(&gpu, 60, 60, 1);
    var rows = [_]protocol.InputRow{
        base,
        cpu,
        gpu,
        corpus.metricFact(base, 3, .vram_usage_percent, 1, 7),
    };
    var config = corpus.explicitConfig(
        protocol.FeatureFlags.adapter_cpu | protocol.FeatureFlags.adapter_gpu,
    );
    const session = try coordinator.create(&config);
    defer coordinator.destroy(session);
    var input = corpus.cycle(&config, 1, 1_000, rows.len, true);
    input.flags |= protocol.CycleFlags.score_only;
    var actions: ActionBuffer = undefined;
    var snapshot: protocol.Snapshot = undefined;
    try std.testing.expectEqual(protocol.Status.ok, corpus.plan(
        session,
        &input,
        rows[0..].ptr,
        rows.len,
        actions[0..].ptr,
        actions.len,
        &snapshot,
    ));
    const software_slot = session.findSoftware(180_271) orelse
        return error.MissingSoftwareSnapshot;
    const software = session.softwares[software_slot];
    try std.testing.expect(software.adapter_cpu_score_valid);
    try std.testing.expect(software.adapter_gpu_score_valid);
    const action = findSoftwareAction(actions[0..snapshot.action_count], 180_271) orelse
        return error.MissingSoftwareAction;
    try std.testing.expect((action.valid_mask & protocol.ActionValidity.cpu_score) != 0);
    try std.testing.expect((action.valid_mask & protocol.ActionValidity.gpu_score) != 0);
    try std.testing.expectApproxEqAbs(software.adapter_cpu_score, action.cpu_score, 0.000_000_1);
    try std.testing.expectApproxEqAbs(software.adapter_gpu_score, action.gpu_score, 0.000_000_1);
    try std.testing.expect(action.cpu_score != action.gpu_score);
}

test "adapter member aggregation is reproducible across input order" {
    var forward: [6]protocol.InputRow = undefined;
    appendProcessScoreRows(&forward, 0, 18_301, 180_301, 185, 1_815, 100);
    appendProcessScoreRows(&forward, 2, 18_302, 180_301, 186, 1_816, 0.000_000_000_000_01);
    appendProcessScoreRows(&forward, 4, 18_303, 180_301, 187, 1_817, 0.000_000_000_000_01);

    var reverse: [6]protocol.InputRow = undefined;
    appendProcessScoreRows(&reverse, 0, 18_303, 180_301, 187, 1_817, 0.000_000_000_000_01);
    appendProcessScoreRows(&reverse, 2, 18_302, 180_301, 186, 1_816, 0.000_000_000_000_01);
    appendProcessScoreRows(&reverse, 4, 18_301, 180_301, 185, 1_815, 100);

    try sealSoftwareCpuScore(forward[0..]);
    try sealSoftwareCpuScore(reverse[0..]);

    const first = try scoreSoftwareCpu(forward[0..], 180_301);
    const second = try scoreSoftwareCpu(reverse[0..], 180_301);
    try std.testing.expectEqual(first.member_score, second.member_score);
    try std.testing.expectEqual(first.action_score, second.action_score);
}

test "CPU and GPU adapters share the exact three base score tier boundaries" {
    try expectFirstRoundAdapterGrades(20, 0, .freeze);
    try expectFirstRoundAdapterGrades(21, 0, .optimize);
    try expectFirstRoundAdapterGrades(80, 0, .optimize);
    try expectFirstRoundAdapterGrades(81, 0, .normal);
    try expectFirstRoundAdapterGrades(80, protocol.InputFlags.foreground_focused, .normal);
    try expectFirstRoundAdapterGrades(90, protocol.InputFlags.foreground_focused, .extreme);
}

test "capacity-scale corpus keeps allocation grouping and reservation paths bounded" {
    const process_count: u32 = 1_024;
    const software_count: u32 = 1_024;
    const row_count: u32 = process_count * 2;
    var config = corpus.explicitConfig(protocol.FeatureFlags.process_policy);
    config.max_processes = process_count;
    config.max_software_groups = software_count;
    config.max_gpu_states = 1;
    config.max_input_rows = row_count;
    config.max_actions = process_count + software_count;
    config.max_reservations = process_count + 2 * software_count;
    config.max_atomic_groups = process_count;
    try std.testing.expect(protocol.isValidConfig(&config));

    const session = try coordinator.create(&config);
    defer coordinator.destroy(session);
    const rows = try std.testing.allocator.alloc(protocol.InputRow, row_count);
    defer std.testing.allocator.free(rows);
    const actions = try std.testing.allocator.alloc(protocol.Action, config.max_actions);
    defer std.testing.allocator.free(actions);

    var process_index: u32 = 0;
    while (process_index < process_count) : (process_index += 1) {
        const source_index = process_index * 2;
        const target_key = @as(u64, 100_000) + process_index;
        const software_key = @as(u64, 200_000) + process_index;
        const process_id = process_index + 1;
        const start_key = @as(u64, 300_000) + process_index;
        const base = corpus.processFact(source_index, target_key, software_key, process_id, start_key, 20, .general_application, 0, protocol.ProcessGrade.normal, .normal, .normal, 0);
        rows[source_index] = base;
        rows[source_index + 1] = corpus.metricFact(base, source_index + 1, .cpu_usage_percent, 50, 0);
    }

    var snapshot: protocol.Snapshot = undefined;
    var sequence: u64 = 1;
    while (sequence <= 3) : (sequence += 1) {
        var input = corpus.cycle(&config, sequence, @as(i64, @intCast(sequence)) * 1_000, row_count, sequence == 1);
        try std.testing.expectEqual(protocol.Status.ok, corpus.plan(
            session,
            &input,
            rows.ptr,
            row_count,
            actions.ptr,
            config.max_actions,
            &snapshot,
        ));
    }
    try std.testing.expectEqual(config.max_actions, snapshot.action_count);
    try std.testing.expectEqual(process_count, snapshot.inflight_count);
}

test "score-only exposes candidate grades without mutating transition state" {
    var config = corpus.explicitConfig(protocol.FeatureFlags.process_policy);
    const session = try coordinator.create(&config);
    defer coordinator.destroy(session);
    var rows = lowScoreProcessRows(15_001, 150_001, 151, 1_511);
    var input = corpus.cycle(&config, 1, 1_000, rows.len, true);
    input.flags |= protocol.CycleFlags.score_only;
    var actions: ActionBuffer = undefined;
    var snapshot: protocol.Snapshot = undefined;
    try std.testing.expectEqual(protocol.Status.ok, corpus.plan(
        session,
        &input,
        rows[0..].ptr,
        rows.len,
        actions[0..].ptr,
        actions.len,
        &snapshot,
    ));
    const action = findProcessAction(actions[0..snapshot.action_count], 15_001) orelse return error.MissingProcessAction;
    try std.testing.expectEqual(protocol.ProcessGrade.level4, action.to_process_grade);
    try std.testing.expectEqual(@as(u32, 0), action.flags & protocol.ActionFlags.requires_feedback);
    try std.testing.expect((action.reason_mask & protocol.Reason.score_only) != 0);
    const process_slot = session.findProcess(15_001, 151, 1_511) orelse return error.MissingProcessSnapshot;
    try std.testing.expectEqual(protocol.ProcessGrade.normal, session.processes[process_slot].transition.desired);
    try std.testing.expectEqual(@as(u32, 0), session.processes[process_slot].transition.pending_count);
    try std.testing.expectEqual(@as(u32, 0), snapshot.inflight_count);
    try std.testing.expect((snapshot.flags & protocol.SnapshotFlags.score_only) != 0);
}

test "replaying one canonical score snapshot does not advance hysteresis" {
    var config = corpus.explicitConfig(protocol.FeatureFlags.process_policy);
    const session = try coordinator.create(&config);
    defer coordinator.destroy(session);
    var rows = lowScoreProcessRows(15_101, 151_001, 152, 1_512);
    var actions: ActionBuffer = undefined;
    var snapshot: protocol.Snapshot = undefined;

    var sequence: u64 = 1;
    while (sequence <= 3) : (sequence += 1) {
        var input = corpus.cycle(
            &config,
            sequence,
            @as(i64, @intCast(sequence)) * 1_000,
            rows.len,
            sequence == 1,
        );
        input.score_scheduling_generation = 1;
        input.cpu_score_source_fingerprint = 0xC101;
        input.gpu_score_source_fingerprint = 0xD101;
        try std.testing.expectEqual(protocol.Status.ok, corpus.plan(
            session,
            &input,
            rows[0..].ptr,
            rows.len,
            actions[0..].ptr,
            actions.len,
            &snapshot,
        ));
        const slot = session.findProcess(15_101, 152, 1_512) orelse return error.MissingProcessSnapshot;
        try std.testing.expectEqual(@as(u32, 1), session.processes[slot].transition.pending_count);
        const action = findProcessAction(actions[0..snapshot.action_count], 15_101) orelse return error.MissingProcessAction;
        try std.testing.expectEqual(@as(u32, 0), action.flags & protocol.ActionFlags.requires_feedback);
    }

    var input = corpus.cycle(&config, 4, 4_000, rows.len, false);
    input.score_scheduling_generation = 2;
    input.cpu_score_source_fingerprint = 0xC102;
    input.gpu_score_source_fingerprint = 0xD102;
    try std.testing.expectEqual(protocol.Status.ok, corpus.plan(
        session,
        &input,
        rows[0..].ptr,
        rows.len,
        actions[0..].ptr,
        actions.len,
        &snapshot,
    ));
    var slot = session.findProcess(15_101, 152, 1_512) orelse return error.MissingProcessSnapshot;
    try std.testing.expectEqual(@as(u32, 2), session.processes[slot].transition.pending_count);

    input = corpus.cycle(&config, 5, 5_000, rows.len, false);
    input.score_scheduling_generation = 3;
    input.cpu_score_source_fingerprint = 0xC103;
    input.gpu_score_source_fingerprint = 0xD103;
    try std.testing.expectEqual(protocol.Status.ok, corpus.plan(
        session,
        &input,
        rows[0..].ptr,
        rows.len,
        actions[0..].ptr,
        actions.len,
        &snapshot,
    ));
    slot = session.findProcess(15_101, 152, 1_512) orelse return error.MissingProcessSnapshot;
    try std.testing.expectEqual(config.required_consecutive_decisions, session.processes[slot].transition.pending_count);
    const ready = findProcessAction(actions[0..snapshot.action_count], 15_101) orelse return error.MissingProcessAction;
    try std.testing.expect((ready.flags & protocol.ActionFlags.requires_feedback) != 0);
}

test "missing canonical score excludes only the corresponding candidate" {
    var config = corpus.explicitConfig(protocol.FeatureFlags.process_policy | protocol.FeatureFlags.adapter_cpu);
    const session = try coordinator.create(&config);
    defer coordinator.destroy(session);
    var rows = twoIndependentProcessRows();
    rows[1].valid_mask &= ~(protocol.InputValidity.process_score |
        protocol.InputValidity.software_score | protocol.InputValidity.score_member_count);
    rows[1].process_score = 0;
    rows[1].software_score = 0;
    rows[1].score_member_count = 0;
    var input = corpus.cycle(&config, 1, 1_000, rows.len, true);
    input.flags |= protocol.CycleFlags.score_only;
    var actions: ActionBuffer = undefined;
    var snapshot: protocol.Snapshot = undefined;

    try std.testing.expectEqual(protocol.Status.ok, corpus.plan(
        session,
        &input,
        rows[0..].ptr,
        rows.len,
        actions[0..].ptr,
        actions.len,
        &snapshot,
    ));

    const missing_process_slot = session.findProcess(161, 16, 1_161) orelse return error.MissingProcessSnapshot;
    const complete_process_slot = session.findProcess(162, 17, 1_162) orelse return error.MissingProcessSnapshot;
    try std.testing.expect(!session.processes[missing_process_slot].cpu_score_valid);
    try std.testing.expect(!session.processes[missing_process_slot].candidate_valid);
    try std.testing.expect(session.processes[complete_process_slot].cpu_score_valid);
    try std.testing.expect(session.processes[complete_process_slot].candidate_valid);

    const missing_software_slot = session.findSoftware(1601) orelse return error.MissingSoftwareSnapshot;
    const complete_software_slot = session.findSoftware(1602) orelse return error.MissingSoftwareSnapshot;
    try std.testing.expect(!session.softwares[missing_software_slot].member_cpu_score_valid);
    try std.testing.expect(!session.softwares[missing_software_slot].cpu_candidate_valid);
    try std.testing.expect(session.softwares[complete_software_slot].member_cpu_score_valid);
    try std.testing.expect(session.softwares[complete_software_slot].cpu_candidate_valid);
}

test "missing canonical score does not block mandatory owned process restoration" {
    const cases = [_]MandatoryProcessRestoreCase{
        .feature_disabled,
        .not_running,
        .explicitly_ineligible,
    };
    for (cases) |case| try expectMissingScoreOwnedProcessRestore(case);
}

test "process CPU grade is independent from missing or saturated GPU metrics" {
    var config = corpus.explicitConfig(protocol.FeatureFlags.process_policy);
    const session = try coordinator.create(&config);
    defer coordinator.destroy(session);

    var missing_gpu = corpus.processFact(0, 16_001, 160_001, 161, 1_611, 20, .general_application, 0, protocol.ProcessGrade.normal, .normal, .normal, 0);
    missing_gpu.flags &= ~protocol.InputFlags.process_gpu_metrics_complete;
    const saturated_gpu = corpus.processFact(2, 16_002, 160_002, 162, 1_612, 20, .general_application, 0, protocol.ProcessGrade.normal, .normal, .normal, 0);
    var rows = [_]protocol.InputRow{
        missing_gpu,
        corpus.metricFact(missing_gpu, 1, .cpu_usage_percent, 50, 0),
        saturated_gpu,
        corpus.metricFact(saturated_gpu, 3, .cpu_usage_percent, 50, 0),
        corpus.metricFact(saturated_gpu, 4, .gpu_usage_percent, 100, 7),
        corpus.metricFact(saturated_gpu, 5, .vram_usage_percent, 100, 7),
    };
    var input = corpus.cycle(&config, 1, 1_000, rows.len, true);
    input.flags |= protocol.CycleFlags.score_only;
    var actions: ActionBuffer = undefined;
    var snapshot: protocol.Snapshot = undefined;

    try std.testing.expectEqual(protocol.Status.ok, corpus.plan(
        session,
        &input,
        rows[0..].ptr,
        rows.len,
        actions[0..].ptr,
        actions.len,
        &snapshot,
    ));

    const missing_gpu_action = findProcessAction(actions[0..snapshot.action_count], 16_001) orelse return error.MissingProcessAction;
    const saturated_gpu_action = findProcessAction(actions[0..snapshot.action_count], 16_002) orelse return error.MissingProcessAction;
    try std.testing.expectEqual(protocol.ProcessGrade.level4, missing_gpu_action.to_process_grade);
    try std.testing.expectEqual(protocol.ProcessGrade.level4, saturated_gpu_action.to_process_grade);
    try std.testing.expectEqual(@as(u64, 0), missing_gpu_action.reason_mask &
        protocol.Reason.incomplete_gpu_metrics);
    try std.testing.expectEqual(@as(u64, 0), saturated_gpu_action.reason_mask &
        protocol.Reason.incomplete_gpu_metrics);
}

fn lowScoreProcessRows(target_key: u64, software_key: u64, process_id: u32, start_key: u64) [2]protocol.InputRow {
    const base = corpus.processFact(0, target_key, software_key, process_id, start_key, 20, .general_application, 0, protocol.ProcessGrade.normal, .normal, .normal, 0);
    return .{
        base,
        corpus.metricFact(base, 1, .cpu_usage_percent, 50, 0),
    };
}

const MandatoryProcessRestoreCase = enum {
    feature_disabled,
    not_running,
    explicitly_ineligible,
};

fn expectMissingScoreOwnedProcessRestore(case: MandatoryProcessRestoreCase) !void {
    var config = corpus.explicitConfig(protocol.FeatureFlags.process_policy);
    const session = try coordinator.create(&config);
    defer coordinator.destroy(session);

    const case_index: u64 = @intFromEnum(case);
    const target_key: u64 = 17_001 + case_index;
    const software_key: u64 = 170_001 + case_index;
    const process_id: u32 = 171 + @as(u32, @intCast(case_index));
    const process_start_key: u64 = 1_711 + case_index;
    var rows = lowScoreProcessRows(
        target_key,
        software_key,
        process_id,
        process_start_key,
    );
    var actions: ActionBuffer = undefined;
    var snapshot: protocol.Snapshot = undefined;
    var sequence: u64 = 1;
    while (sequence <= 3) : (sequence += 1) {
        var input = corpus.cycle(
            &config,
            sequence,
            @as(i64, @intCast(sequence)) * 1_000,
            rows.len,
            sequence == 1,
        );
        try std.testing.expectEqual(protocol.Status.ok, corpus.plan(
            session,
            &input,
            rows[0..].ptr,
            rows.len,
            actions[0..].ptr,
            actions.len,
            &snapshot,
        ));
    }

    const apply_action = findProcessAction(
        actions[0..snapshot.action_count],
        target_key,
    ) orelse return error.MissingProcessAction;
    try std.testing.expectEqual(
        @intFromEnum(protocol.ActionDisposition.apply),
        apply_action.disposition,
    );
    const feedback = corpus.feedbackFor(apply_action.*, .succeeded, 3_100);
    try std.testing.expectEqual(protocol.Status.ok, coordinator.applyFeedback(
        session,
        @as([*]const protocol.Feedback, @ptrCast(&feedback)),
        1,
        1,
        &snapshot,
    ));
    const process_slot = session.findProcess(
        target_key,
        process_id,
        process_start_key,
    ) orelse return error.MissingProcessSnapshot;
    try std.testing.expect(session.processes[process_slot].transition.owned);
    try std.testing.expectEqual(
        protocol.ProcessGrade.level4,
        session.processes[process_slot].transition.applied,
    );

    for (&rows) |*row| {
        row.valid_mask &= ~(protocol.InputValidity.process_score |
            protocol.InputValidity.software_score |
            protocol.InputValidity.score_member_count);
        row.process_score = 0;
        row.software_score = 0;
        row.score_member_count = 0;
        switch (case) {
            .feature_disabled => {},
            .not_running => row.flags &= ~protocol.InputFlags.running,
            .explicitly_ineligible =>
                row.flags &= ~protocol.InputFlags.can_apply_process_policy,
        }
    }
    if (case == .feature_disabled) {
        session.config.feature_flags &= ~protocol.FeatureFlags.process_policy;
    }

    var restore_input = corpus.cycle(&config, 4, 4_000, rows.len, false);
    try std.testing.expectEqual(protocol.Status.ok, corpus.plan(
        session,
        &restore_input,
        rows[0..].ptr,
        rows.len,
        actions[0..].ptr,
        actions.len,
        &snapshot,
    ));
    const restore = findProcessAction(
        actions[0..snapshot.action_count],
        target_key,
    ) orelse return error.MissingProcessAction;
    try std.testing.expectEqual(
        @intFromEnum(protocol.ActionDisposition.restore),
        restore.disposition,
    );
    try std.testing.expectEqual(protocol.ProcessGrade.normal, restore.to_process_grade);
    try std.testing.expect((restore.flags & protocol.ActionFlags.requires_feedback) != 0);
    try std.testing.expectEqual(
        @as(u64, 0),
        restore.valid_mask & protocol.ActionValidity.cpu_score,
    );
    const expected_reason = switch (case) {
        .feature_disabled => protocol.Reason.feature_disabled,
        .not_running => protocol.Reason.not_running,
        .explicitly_ineligible => protocol.Reason.ineligible,
    };
    try std.testing.expect((restore.reason_mask & expected_reason) != 0);
}

fn twoProcessRows() [4]protocol.InputRow {
    const first = corpus.processFact(0, 101, 1001, 11, 111, 20, .general_application, 0, protocol.ProcessGrade.normal, .normal, .normal, 0);
    const second = corpus.processFact(2, 102, 1001, 12, 112, 20, .general_application, 0, protocol.ProcessGrade.normal, .normal, .normal, 0);
    var first_cpu = corpus.metricFact(first, 1, .cpu_usage_percent, 50, 0);
    var second_cpu = corpus.metricFact(second, 3, .cpu_usage_percent, 50, 0);
    corpus.setSoftwareScore(&first_cpu, 9, 2);
    corpus.setSoftwareScore(&second_cpu, 9, 2);
    return .{
        first,
        first_cpu,
        second,
        second_cpu,
    };
}

fn twoIndependentProcessRows() [4]protocol.InputRow {
    const first = corpus.processFact(0, 161, 1601, 16, 1_161, 20, .general_application, 0, protocol.ProcessGrade.normal, .normal, .normal, 0);
    const second = corpus.processFact(2, 162, 1602, 17, 1_162, 20, .general_application, 0, protocol.ProcessGrade.normal, .normal, .normal, 0);
    return .{
        first,
        corpus.metricFact(first, 1, .cpu_usage_percent, 50, 0),
        second,
        corpus.metricFact(second, 3, .cpu_usage_percent, 50, 0),
    };
}

fn occupiedAtomicGroupCount(session: anytype) usize {
    var count: usize = 0;
    for (session.atomic_groups) |group| {
        if (group.occupied) count += 1;
    }
    return count;
}

fn findProcessAction(actions: []protocol.Action, target_key: u64) ?*const protocol.Action {
    for (actions) |*action| {
        if (action.scope == @intFromEnum(protocol.ActionScope.process_policy) and action.target_key == target_key) return action;
    }
    return null;
}

fn findSoftwareAction(actions: []protocol.Action, software_key: u64) ?*const protocol.Action {
    for (actions) |*action| {
        if (action.scope == @intFromEnum(protocol.ActionScope.adapter_software) and action.software_key == software_key) return action;
    }
    return null;
}

fn findProcessSnapshot(rows: []protocol.SnapshotRow, target_key: u64) ?*const protocol.SnapshotRow {
    for (rows) |*row| {
        if (row.row_kind == @intFromEnum(protocol.SnapshotRowKind.process) and row.target_key == target_key) return row;
    }
    return null;
}

const SoftwareCpuScore = struct {
    member_score: f64,
    action_score: f64,
};

fn scoreSoftwareCpu(rows: []protocol.InputRow, software_key: u64) !SoftwareCpuScore {
    const config = corpus.explicitConfig(protocol.FeatureFlags.adapter_cpu);
    return scoreSoftwareCpuWithConfig(rows, software_key, config);
}

fn scoreSoftwareCpuWithConfig(
    rows: []protocol.InputRow,
    software_key: u64,
    initial_config: protocol.Config,
) !SoftwareCpuScore {
    var config = initial_config;
    const session = try coordinator.create(&config);
    defer coordinator.destroy(session);
    var input = corpus.cycle(&config, 1, 1_000, @intCast(rows.len), true);
    input.flags |= protocol.CycleFlags.score_only;
    var actions: ActionBuffer = undefined;
    var snapshot: protocol.Snapshot = undefined;
    try std.testing.expectEqual(protocol.Status.ok, corpus.plan(
        session,
        &input,
        rows.ptr,
        @intCast(rows.len),
        actions[0..].ptr,
        @intCast(actions.len),
        &snapshot,
    ));
    const software_slot = session.findSoftware(software_key) orelse return error.MissingSoftwareSnapshot;
    const software = session.softwares[software_slot];
    try std.testing.expect(software.member_cpu_score_valid);
    const action = findSoftwareAction(actions[0..snapshot.action_count], software_key) orelse return error.MissingSoftwareAction;
    return .{
        .member_score = software.member_cpu_score,
        .action_score = action.cpu_score,
    };
}

fn appendProcessScoreRows(
    rows: *[6]protocol.InputRow,
    source_index: u32,
    target_key: u64,
    software_key: u64,
    process_id: u32,
    start_key: u64,
    base_score: f64,
) void {
    const base = corpus.processFact(
        source_index,
        target_key,
        software_key,
        process_id,
        start_key,
        base_score,
        .adapted,
        0,
        protocol.ProcessGrade.normal,
        .normal,
        .normal,
        0,
    );
    rows[source_index] = base;
    rows[source_index + 1] = corpus.metricFact(base, source_index + 1, .cpu_usage_percent, 100, 0);
}

fn sealSoftwareCpuScore(rows: []protocol.InputRow) !void {
    var sum = software_aggregation.DeterministicSum{};
    var member_count: u32 = 0;
    for (rows) |row| {
        if ((row.valid_mask & protocol.InputValidity.metric) == 0 or
            row.metric_kind != @intFromEnum(protocol.MetricKind.cpu_usage_percent))
        {
            continue;
        }
        if (!sum.add(row.process_score)) return error.InvalidScoreFixture;
        member_count += 1;
    }
    const total = sum.total() orelse return error.InvalidScoreFixture;
    for (rows) |*row| {
        if ((row.valid_mask & protocol.InputValidity.metric) == 0 or
            row.metric_kind != @intFromEnum(protocol.MetricKind.cpu_usage_percent))
        {
            continue;
        }
        corpus.setSoftwareScore(row, total, member_count);
    }
}

fn expectFirstRoundDesiredGrade(
    base_score: f64,
    surface_flags: u64,
    cpu_usage: f64,
    protection_level: u8,
    expected: i8,
) !void {
    var config = corpus.explicitConfig(protocol.FeatureFlags.process_policy);
    const session = try coordinator.create(&config);
    defer coordinator.destroy(session);
    var base = corpus.processFact(0, 14_001, 140_001, 141, 1_411, base_score, .general_application, surface_flags, protocol.ProcessGrade.normal, .normal, .normal, 0);
    base.protection_level = protection_level;
    var rows = [_]protocol.InputRow{
        base,
        corpus.metricFact(base, 1, .cpu_usage_percent, cpu_usage, 0),
    };
    var input = corpus.cycle(&config, 1, 1_000, rows.len, true);
    var actions: ActionBuffer = undefined;
    var snapshot: protocol.Snapshot = undefined;
    try std.testing.expectEqual(protocol.Status.ok, corpus.plan(
        session,
        &input,
        rows[0..].ptr,
        rows.len,
        actions[0..].ptr,
        actions.len,
        &snapshot,
    ));
    const process_slot = session.findProcess(14_001, 141, 1_411) orelse return error.MissingProcessSnapshot;
    try std.testing.expectEqual(expected, session.processes[process_slot].transition.desired);
}

fn expectFirstRoundAdapterGrades(
    base_score: f64,
    surface_flags: u64,
    expected: protocol.AdapterGrade,
) !void {
    var config = corpus.explicitConfig(
        protocol.FeatureFlags.adapter_cpu | protocol.FeatureFlags.adapter_gpu,
    );
    const session = try coordinator.create(&config);
    defer coordinator.destroy(session);
    const base = corpus.processFact(
        0,
        15_001,
        150_001,
        151,
        1_511,
        base_score,
        .adapted,
        surface_flags,
        protocol.ProcessGrade.normal,
        .normal,
        .normal,
        0,
    );
    var rows = [_]protocol.InputRow{
        base,
        corpus.metricFact(base, 1, .cpu_usage_percent, 100, 0),
        corpus.metricFact(base, 2, .gpu_usage_percent, 100, 7),
        corpus.metricFact(base, 3, .vram_usage_percent, 1, 7),
    };
    var input = corpus.cycle(&config, 1, 1_000, rows.len, true);
    var actions: ActionBuffer = undefined;
    var snapshot: protocol.Snapshot = undefined;
    try std.testing.expectEqual(protocol.Status.ok, corpus.plan(
        session,
        &input,
        rows[0..].ptr,
        rows.len,
        actions[0..].ptr,
        actions.len,
        &snapshot,
    ));
    const software_slot = session.findSoftware(150_001) orelse
        return error.MissingSoftwareSnapshot;
    const software = session.softwares[software_slot];
    try std.testing.expect(software.cpu_candidate_valid);
    try std.testing.expect(software.gpu_candidate_valid);
    try std.testing.expectEqual(@intFromEnum(expected), software.cpu_candidate_grade);
    try std.testing.expectEqual(@intFromEnum(expected), software.gpu_candidate_grade);
}
