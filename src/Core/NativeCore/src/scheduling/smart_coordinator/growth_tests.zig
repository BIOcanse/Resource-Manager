const std = @import("std");
const state = @import("state.zig");
const protocol = @import("protocol.zig");
const coordinator = @import("session.zig");
const corpus = @import("golden_corpus.zig");

fn snapshotRowLessThan(_: void, left: protocol.SnapshotRow, right: protocol.SnapshotRow) bool {
    if (left.row_kind != right.row_kind) return left.row_kind < right.row_kind;
    if (left.target_key != right.target_key) return left.target_key < right.target_key;
    if (left.software_key != right.software_key) return left.software_key < right.software_key;
    if (left.process_id != right.process_id) return left.process_id < right.process_id;
    return left.process_start_key < right.process_start_key;
}

fn shapeBytes(demand: state.BankDemand) !u64 {
    var bytes: u64 = @sizeOf(state.Session);
    bytes = try std.math.add(u64, bytes, try std.math.mul(u64, demand.processes, @sizeOf(state.ProcessState)));
    bytes = try std.math.add(u64, bytes, try std.math.mul(u64, demand.softwares, @sizeOf(state.SoftwareState)));
    bytes = try std.math.add(u64, bytes, try std.math.mul(u64, demand.gpus, @sizeOf(state.GpuState)));
    bytes = try std.math.add(u64, bytes, try std.math.mul(u64, demand.reservations, @sizeOf(state.Reservation)));
    bytes = try std.math.add(u64, bytes, try std.math.mul(u64, demand.atomic_groups, @sizeOf(state.AtomicGroup)));
    for ([_]u32{ demand.processes, demand.softwares, demand.gpus, demand.reservations, demand.atomic_groups }) |count| {
        bytes = try std.math.add(u64, bytes, try std.math.mul(u64, try state.hashIndexCapacity(count), @sizeOf(u32)));
    }
    return bytes;
}

test "smart full shape byte calculations distinguish runtime maxima from build specialization ceilings" {
    // Runtime host_recreate maxima are copied by ConfigurationWriter to Config;
    // build_specialize capacity limits only constrain allowed configurations.
    const build_ceiling = state.BankDemand{ .processes = 32768, .softwares = 32768, .gpus = 262144, .reservations = 131072, .atomic_groups = 32768 };
    const runtime_maxima = state.BankDemand{ .processes = 4096, .softwares = 2048, .gpus = 32768, .reservations = 8192, .atomic_groups = 4096 };
    try std.testing.expectEqual(@as(u64, 71_828_592), try shapeBytes(build_ceiling));
    try std.testing.expectEqual(@as(u64, 6_014_064), try shapeBytes(runtime_maxima));
    // Alternate 300-process/300-software/300-GPU retained content shape:
    // each identity bank doubles to 512; no feedback actions/groups are present.
    const with_gpu = state.BankDemand{ .processes = 512, .softwares = 512, .gpus = 512, .reservations = 1, .atomic_groups = 1 };
    try std.testing.expectEqual(@as(u64, 284_088), try shapeBytes(with_gpu));
}

fn seedBanks(session: *state.Session) void {
    session.processes[0] = state.ProcessState.empty();
    const process = &session.processes[0];
    process.occupied = true;
    process.target_key = 11;
    process.process_id = 12;
    process.process_start_key = 13;
    process.software_key = 14;
    process.transition.owned = true;
    process.transition.pending_count = 2;
    process.transition.last_round_number = 19;
    process.transition.retry_not_before_ms = 9000;
    process.transition.inflight_action_id = 101;
    process.transition.compensation_required = true;
    session.softwares[0] = state.SoftwareState.empty();
    session.softwares[0].occupied = true;
    session.softwares[0].software_key = 14;
    session.softwares[0].cpu_transition.owned = true;
    session.softwares[0].cpu_transition.pending_count = 2;
    session.softwares[0].gpu_transition.retry_not_before_ms = 10000;
    session.softwares[0].freeze_compensation_active = true;
    session.gpus[0] = std.mem.zeroes(state.GpuState);
    session.gpus[0].occupied = true;
    session.gpus[0].target_key = 11;
    session.gpus[0].process_id = 12;
    session.gpus[0].process_start_key = 13;
    session.gpus[0].device_index = 2;
    session.reservations[0] = std.mem.zeroes(state.Reservation);
    session.reservations[0].occupied = true;
    session.reservations[0].action.action_id = 101;
    session.reservations[0].process_slot = 0;
    session.reservations[0].software_slot = 0;
    session.reservations[0].atomic_group_slot = 0;
    session.reservations[0].feedback_received = true;
    session.reservations[0].validation_epoch = 21;
    session.reservations[0].validation_feedback_index = 3;
    session.reservations[0].feedback.action_id = 101;
    session.atomic_groups[0] = std.mem.zeroes(state.AtomicGroup);
    session.atomic_groups[0].occupied = true;
    session.atomic_groups[0].group_id = 102;
    session.atomic_groups[0].expected_count = 3;
    session.atomic_groups[0].feedback_count = 1;
    session.atomic_groups[0].failed_count = 1;
    session.inflight_count = 1;
    session.rebuildPersistentIndices();
    session.rebuildReservationIndex();
    session.rebuildAtomicGroupIndex();
}

test "smart canonical growth preserves every retained state and stable feedback slot" {
    const config = corpus.explicitConfig(protocol.FeatureFlags.known);
    const session = try state.Session.createWithAllocator(&config, std.testing.allocator);
    defer session.destroy();
    seedBanks(session);
    const process = session.processes[0];
    const software = session.softwares[0];
    const gpu = session.gpus[0];
    const reservation = session.reservations[0];
    const group = session.atomic_groups[0];
    const revision = session.state_revision;
    try std.testing.expectEqual(protocol.Status.ok, session.ensureBankCapacity(.{
        .processes = 3,
        .softwares = 3,
        .gpus = 3,
        .reservations = 3,
        .atomic_groups = 3,
    }));
    try std.testing.expectEqual(@as(usize, 4), session.processes.len);
    try std.testing.expectEqualDeep(process, session.processes[0]);
    try std.testing.expectEqualDeep(software, session.softwares[0]);
    try std.testing.expectEqualDeep(gpu, session.gpus[0]);
    try std.testing.expectEqualDeep(reservation, session.reservations[0]);
    try std.testing.expectEqualDeep(group, session.atomic_groups[0]);
    try std.testing.expectEqual(@as(?u32, 0), session.findProcess(11, 12, 13));
    try std.testing.expectEqual(@as(?u32, 0), session.findSoftware(14));
    try std.testing.expectEqual(@as(?u32, 0), session.findGpu(11, 12, 13, 2));
    try std.testing.expectEqual(@as(?u32, 0), session.findReservation(101));
    try std.testing.expectEqual(@as(?u32, 0), session.findAtomicGroup(102));
    try std.testing.expectEqual(revision, session.state_revision);
    try std.testing.expectEqual(@as(u32, 1), session.inflight_count);
}

test "smart failed allocation at each replacement leaves all old banks intact and retries" {
    const config = corpus.explicitConfig(protocol.FeatureFlags.known);
    var fail_offset: usize = 0;
    while (fail_offset < 10) : (fail_offset += 1) {
        var failing = std.testing.FailingAllocator.init(std.testing.allocator, .{});
        const session = try state.Session.createWithAllocator(&config, failing.allocator());
        defer session.destroy();
        seedBanks(session);
        const before = session.*;
        const process = session.processes[0];
        const reservation = session.reservations[0];
        failing.fail_index = failing.alloc_index + fail_offset;
        const demand = state.BankDemand{ .processes = 3, .softwares = 3, .gpus = 3, .reservations = 3, .atomic_groups = 3 };
        try std.testing.expectEqual(protocol.Status.out_of_memory, session.ensureBankCapacity(demand));
        try std.testing.expectEqual(@intFromPtr(before.processes.ptr), @intFromPtr(session.processes.ptr));
        try std.testing.expectEqual(@intFromPtr(before.softwares.ptr), @intFromPtr(session.softwares.ptr));
        try std.testing.expectEqual(@intFromPtr(before.gpus.ptr), @intFromPtr(session.gpus.ptr));
        try std.testing.expectEqual(@intFromPtr(before.reservations.ptr), @intFromPtr(session.reservations.ptr));
        try std.testing.expectEqual(@intFromPtr(before.atomic_groups.ptr), @intFromPtr(session.atomic_groups.ptr));
        try std.testing.expectEqualDeep(before, session.*);
        try std.testing.expectEqualDeep(process, session.processes[0]);
        try std.testing.expectEqualDeep(reservation, session.reservations[0]);
        failing.fail_index = std.math.maxInt(usize);
        try std.testing.expectEqual(protocol.Status.ok, session.ensureBankCapacity(demand));
        // Reusing current capacity must not call the allocator.
        failing.fail_index = failing.alloc_index;
        try std.testing.expectEqual(protocol.Status.ok, session.ensureBankCapacity(demand));
    }
}

test "smart configured bank limit rejects without allocations or state mutation" {
    const config = corpus.explicitConfig(protocol.FeatureFlags.known);
    var failing = std.testing.FailingAllocator.init(std.testing.allocator, .{});
    const session = try state.Session.createWithAllocator(&config, failing.allocator());
    defer session.destroy();
    seedBanks(session);
    const before = session.*;
    failing.fail_index = failing.alloc_index;
    try std.testing.expectEqual(protocol.Status.capacity_exceeded, session.ensureBankCapacity(.{
        .processes = config.max_processes + 1,
        .softwares = 1,
        .gpus = 1,
        .reservations = 1,
        .atomic_groups = 1,
    }));
    try std.testing.expectEqualDeep(before, session.*);
    try std.testing.expect(!failing.has_induced_failure);
}

test "smart proven no growth score only and warm normal plans allocate nothing" {
    const config = corpus.explicitConfig(protocol.FeatureFlags.known);
    for ([_]bool{ true, false }) |score_only| {
        var failing = std.testing.FailingAllocator.init(std.testing.allocator, .{});
        const session = try state.Session.createWithAllocator(&config, failing.allocator());
        defer session.destroy();
        if (!score_only) {
            try std.testing.expectEqual(protocol.Status.ok, session.ensureBankCapacity(.{
                .processes = config.max_processes,
                .softwares = config.max_software_groups,
                .gpus = config.max_gpu_states,
                .reservations = config.max_reservations,
                .atomic_groups = config.max_atomic_groups,
            }));
        }
        var row = corpus.processFact(0, 100, 200, 300, 400, 100, .general_application, protocol.InputFlags.foreground_focused, protocol.ProcessGrade.normal, .normal, .normal, 0);
        row = corpus.metricFact(row, 0, .cpu_usage_percent, 100, 0);
        const process_pointer = session.processes.ptr;
        const reservation_pointer = session.reservations.ptr;
        failing.fail_index = failing.alloc_index;
        var sequence: u64 = 1;
        while (sequence <= 4) : (sequence += 1) {
            var input = corpus.cycle(&config, sequence, @intCast(sequence * 1000), 1, sequence == 1);
            if (score_only) input.flags |= protocol.CycleFlags.score_only;
            var actions: [corpus.default_action_capacity]protocol.Action = undefined;
            var snapshot: protocol.Snapshot = undefined;
            try std.testing.expectEqual(protocol.Status.ok, corpus.plan(session, &input, @ptrCast(&row), 1, &actions, actions.len, &snapshot));
            try std.testing.expectEqual(@intFromPtr(process_pointer), @intFromPtr(session.processes.ptr));
            try std.testing.expectEqual(@intFromPtr(reservation_pointer), @intFromPtr(session.reservations.ptr));
        }
        try std.testing.expect(!failing.has_induced_failure);
        if (score_only) {
            try std.testing.expectEqual(@as(u32, 0), session.inflight_count);
        } else {
            try std.testing.expect(session.inflight_count > 0);
        }
    }
}

test "smart plan growth failure precedes reservation expiry and confirmation mutation" {
    const config = corpus.explicitConfig(protocol.FeatureFlags.known);
    var failing = std.testing.FailingAllocator.init(std.testing.allocator, .{});
    const session = try state.Session.createWithAllocator(&config, failing.allocator());
    defer session.destroy();
    seedBanks(session);
    session.first_cycle_completed = true;
    session.last_cycle_sequence = 1;
    session.last_observed_at_ms = 1000;
    const before_process = session.processes[0];
    const before_reservation = session.reservations[0];
    const revision = session.state_revision;
    var row = corpus.processFact(0, 100, 200, 300, 400, 20, .general_application, 0, protocol.ProcessGrade.normal, .normal, .normal, 0);
    row = corpus.metricFact(row, 0, .gpu_usage_percent, 80, 2);
    var input = corpus.cycle(&config, 2, 100000, 1, false);
    input.flags &= ~protocol.CycleFlags.full_process_snapshot;
    var actions: [corpus.default_action_capacity]protocol.Action = undefined;
    var snapshot: protocol.Snapshot = undefined;
    failing.fail_index = failing.alloc_index + 9;
    try std.testing.expectEqual(protocol.Status.out_of_memory, corpus.plan(session, &input, @ptrCast(&row), 1, &actions, actions.len, &snapshot));
    try std.testing.expectEqual(revision, session.state_revision);
    try std.testing.expectEqual(@as(u64, 1), session.last_cycle_sequence);
    try std.testing.expectEqual(@as(i64, 1000), session.last_observed_at_ms);
    try std.testing.expectEqualDeep(before_process, session.processes[0]);
    try std.testing.expectEqualDeep(before_reservation, session.reservations[0]);
    try std.testing.expectEqual(@as(usize, 1), session.processes.len);
    try std.testing.expectEqual(@as(usize, 1), session.atomic_groups.len);
}

test "smart late reservation growth failure rolls back confirmations and issued action IDs" {
    var config = corpus.explicitConfig(protocol.FeatureFlags.process_policy);
    config.required_consecutive_decisions = 1;
    var fail_offset: usize = 0;
    while (fail_offset < 16) : (fail_offset += 1) {
        var failing = std.testing.FailingAllocator.init(std.testing.allocator, .{});
        const session = try state.Session.createWithAllocator(&config, failing.allocator());
        defer session.destroy();
        const before = session.*;
        var rows: [4]protocol.InputRow = undefined;
        for (0..2) |i| {
            rows[2 * i] = corpus.processFact(@intCast(2 * i), 100 + i, 200 + i, @intCast(300 + i), 400 + i, 100, .general_application, protocol.InputFlags.foreground_focused, protocol.ProcessGrade.normal, .normal, .normal, 0);
            rows[2 * i + 1] = corpus.metricFact(rows[2 * i], @intCast(2 * i + 1), .cpu_usage_percent, 100, 0);
        }
        const input = corpus.cycle(&config, 1, 1000, rows.len, true);
        var actions: [corpus.default_action_capacity]protocol.Action = undefined;
        var snapshot: protocol.Snapshot = undefined;
        failing.fail_index = failing.alloc_index + fail_offset;
        try std.testing.expectEqual(protocol.Status.out_of_memory, corpus.plan(session, &input, &rows, rows.len, &actions, actions.len, &snapshot));
        try std.testing.expect(failing.has_induced_failure);
        try std.testing.expectEqualDeep(before, session.*);
        try std.testing.expect(!session.processes[0].occupied);
        try std.testing.expect(!session.reservations[0].occupied);
        failing.fail_index = std.math.maxInt(usize);
        try std.testing.expectEqual(protocol.Status.ok, corpus.plan(session, &input, &rows, rows.len, &actions, actions.len, &snapshot));
        try std.testing.expectEqual(@as(u32, 2), session.inflight_count);
        try std.testing.expectEqual(@as(usize, 2), session.reservations.len);
        try std.testing.expectEqual(@as(usize, 1), session.atomic_groups.len);
    }
}

test "smart late atomic group growth failure leaves live grouping and confirmations unchanged" {
    var config = corpus.explicitConfig(protocol.FeatureFlags.process_policy);
    config.required_consecutive_decisions = 1;
    var failing = std.testing.FailingAllocator.init(std.testing.allocator, .{});
    const session = try state.Session.createWithAllocator(&config, failing.allocator());
    defer session.destroy();
    const before = session.*;
    var rows: [8]protocol.InputRow = undefined;
    for (0..4) |i| {
        rows[2 * i] = corpus.processFact(@intCast(2 * i), 100 + i, 200 + i / 2, @intCast(300 + i), 400 + i, 20, .general_application, 0, protocol.ProcessGrade.normal, .normal, .normal, 0);
        rows[2 * i + 1] = corpus.metricFact(rows[2 * i], @intCast(2 * i + 1), .cpu_usage_percent, 0, 0);
        corpus.setSoftwareScore(&rows[2 * i + 1], 0, 2);
    }
    const input = corpus.cycle(&config, 1, 1000, rows.len, true);
    var actions: [corpus.default_action_capacity]protocol.Action = undefined;
    var snapshot: protocol.Snapshot = undefined;
    // Ten cloned arrays, two grown identity banks, then the second group index.
    failing.fail_index = failing.alloc_index + 15;
    try std.testing.expectEqual(protocol.Status.out_of_memory, corpus.plan(session, &input, &rows, rows.len, &actions, actions.len, &snapshot));
    try std.testing.expect(failing.has_induced_failure);
    try std.testing.expectEqualDeep(before, session.*);
    try std.testing.expect(!session.atomic_groups[0].occupied);
    failing.fail_index = std.math.maxInt(usize);
    try std.testing.expectEqual(protocol.Status.ok, corpus.plan(session, &input, &rows, rows.len, &actions, actions.len, &snapshot));
    try std.testing.expectEqual(@as(u32, 4), session.inflight_count);
    try std.testing.expectEqual(@as(usize, 2), session.atomic_groups.len);
}

test "smart growth produces identical actions feedback and snapshots to full banks" {
    const config = corpus.explicitConfig(protocol.FeatureFlags.known);
    const dynamic = try state.Session.createWithAllocator(&config, std.testing.allocator);
    defer dynamic.destroy();
    const full = try state.Session.createWithAllocator(&config, std.testing.allocator);
    defer full.destroy();
    try std.testing.expectEqual(protocol.Status.ok, full.ensureBankCapacity(.{
        .processes = config.max_processes,
        .softwares = config.max_software_groups,
        .gpus = config.max_gpu_states,
        .reservations = config.max_reservations,
        .atomic_groups = config.max_atomic_groups,
    }));
    var rows: [8]protocol.InputRow = undefined;
    var sequence: u64 = 1;
    while (sequence <= 8) : (sequence += 1) {
        // Successive batches grow all canonical identity banks. Earlier owned
        // identities and their transition history remain when input disappears.
        const count: usize = @min(sequence, 4);
        for (0..count) |i| {
            const base = corpus.processFact(@intCast(2 * i), 100 + i, 200 + i, @intCast(300 + i), 400 + i, 20, .general_application, 0, protocol.ProcessGrade.level1, .optimize, .optimize, protocol.InputFlags.owns_process_grade | protocol.InputFlags.owns_cpu_grade | protocol.InputFlags.owns_gpu_grade);
            rows[2 * i] = corpus.metricFact(base, @intCast(2 * i), .cpu_usage_percent, 80, 0);
            rows[2 * i + 1] = corpus.metricFact(base, @intCast(2 * i + 1), .gpu_usage_percent, 80, 2);
        }
        var input = corpus.cycle(&config, sequence, @intCast(sequence * 1000), @intCast(2 * count), sequence == 1);
        // A missing complete inventory is meaningful input, not an empty reset.
        if (sequence == 5) input.input_count = 0;
        var left_actions: [corpus.default_action_capacity]protocol.Action = undefined;
        var right_actions: [corpus.default_action_capacity]protocol.Action = undefined;
        var left: protocol.Snapshot = undefined;
        var right: protocol.Snapshot = undefined;
        try std.testing.expectEqual(protocol.Status.ok, corpus.plan(dynamic, &input, &rows, rows.len, &left_actions, left_actions.len, &left));
        try std.testing.expectEqual(protocol.Status.ok, corpus.plan(full, &input, &rows, rows.len, &right_actions, right_actions.len, &right));
        try std.testing.expectEqualDeep(left, right);
        try std.testing.expectEqualDeep(left_actions[0..left.action_count], right_actions[0..right.action_count]);
        var left_rows: [12]protocol.SnapshotRow = undefined;
        var right_rows: [12]protocol.SnapshotRow = undefined;
        try std.testing.expectEqual(protocol.Status.ok, coordinator.getSnapshot(dynamic, &left, &left_rows, left_rows.len));
        try std.testing.expectEqual(protocol.Status.ok, coordinator.getSnapshot(full, &right, &right_rows, right_rows.len));
        // Snapshot enumeration is slot order, not identity order. A full bank
        // can reuse a different empty slot; compare each complete row by identity.
        std.sort.pdq(protocol.SnapshotRow, left_rows[0..left.snapshot_row_count], {}, snapshotRowLessThan);
        std.sort.pdq(protocol.SnapshotRow, right_rows[0..right.snapshot_row_count], {}, snapshotRowLessThan);
        try std.testing.expectEqualDeep(left_rows[0..left.snapshot_row_count], right_rows[0..right.snapshot_row_count]);
        var feedback: [12]protocol.Feedback = undefined;
        var feedback_count: u32 = 0;
        for (left_actions[0..left.action_count]) |action| {
            if ((action.flags & protocol.ActionFlags.requires_feedback) == 0) continue;
            feedback[feedback_count] = corpus.feedbackFor(action, .succeeded, input.observed_at_ms + 1);
            feedback_count += 1;
        }
        if (feedback_count != 0) {
            try std.testing.expectEqual(protocol.Status.ok, coordinator.applyFeedback(dynamic, &feedback, feedback_count, feedback.len, &left));
            try std.testing.expectEqual(protocol.Status.ok, coordinator.applyFeedback(full, &feedback, feedback_count, feedback.len, &right));
            try std.testing.expectEqualDeep(left, right);
        }
    }
}

test "smart actual diagnostics count 300 owned processes as 300 states" {
    var config = corpus.explicitConfig(protocol.FeatureFlags.process_policy);
    config.max_processes = 1024;
    config.max_software_groups = 1024;
    config.max_gpu_states = 1024;
    config.max_input_rows = 4096;
    config.max_actions = 2048;
    config.max_reservations = 3072;
    config.max_atomic_groups = 1024;
    const session = try coordinator.create(&config);
    defer coordinator.destroy(session);
    var before: protocol.WorkspaceCapacity = undefined;
    try std.testing.expectEqual(protocol.Status.ok, coordinator.workspaceCapacity(session, &before));
    try std.testing.expectEqual(@as(u32, 1), before.process_capacity);
    var rows: [300]protocol.InputRow = undefined;
    for (&rows, 0..) |*row, i| {
        row.* = corpus.processFact(@intCast(i), 1000 + i, 2000 + i, @intCast(3000 + i), 4000 + i, 20, .general_application, 0, protocol.ProcessGrade.level1, .normal, .normal, protocol.InputFlags.owns_process_grade);
    }
    var input = corpus.cycle(&config, 1, 1000, rows.len, true);
    var actions: [2048]protocol.Action = undefined;
    var snapshot: protocol.Snapshot = undefined;
    try std.testing.expectEqual(protocol.Status.ok, corpus.plan(session, &input, &rows, rows.len, &actions, actions.len, &snapshot));
    var actual: protocol.WorkspaceCapacity = undefined;
    try std.testing.expectEqual(protocol.Status.ok, coordinator.workspaceCapacity(session, &actual));
    try std.testing.expectEqual(@as(u32, 300), snapshot.process_count);
    try std.testing.expectEqual(@as(u32, 512), actual.process_capacity);
    try std.testing.expectEqual(@as(u32, 512), actual.software_capacity);
    try std.testing.expectEqual(@as(u32, 1), actual.gpu_state_capacity);
    try std.testing.expectEqual(@as(u32, 1), actual.reservation_capacity);
    try std.testing.expectEqual(@as(u32, 1), actual.atomic_group_capacity);
    try std.testing.expectEqual(@as(u32, 0), snapshot.inflight_count);
    const expected_bytes = @sizeOf(state.Session) + 512 * (@sizeOf(state.ProcessState) + @sizeOf(state.SoftwareState)) +
        2 * 1024 * @sizeOf(u32) + @sizeOf(state.GpuState) + @sizeOf(state.Reservation) + @sizeOf(state.AtomicGroup) + 3 * 2 * @sizeOf(u32);
    try std.testing.expectEqual(@as(u64, expected_bytes), actual.resident_bytes);
    std.debug.print("300-owned-process evidence process_size={d} software_size={d} gpu_size={d} reservation_size={d} group_size={d} session_size={d} initial_bytes={d} actual_bytes={d}\n", .{ @sizeOf(state.ProcessState), @sizeOf(state.SoftwareState), @sizeOf(state.GpuState), @sizeOf(state.Reservation), @sizeOf(state.AtomicGroup), @sizeOf(state.Session), before.resident_bytes, actual.resident_bytes });
}

test "smart diagnostics record each committed bank once and discard candidate growth" {
    const config = corpus.explicitConfig(protocol.FeatureFlags.known);
    const live = try state.Session.createWithAllocator(&config, std.testing.allocator);
    defer live.destroy();
    const demand = state.BankDemand{ .processes = 3, .softwares = 3, .gpus = 3, .reservations = 3, .atomic_groups = 3 };
    {
        var discarded = try live.cloneBanks();
        defer discarded.freeBanks();
        try std.testing.expectEqual(protocol.Status.ok, discarded.ensureBankCapacity(demand));
        try std.testing.expectEqual(@as(u64, 0), live.growth.count);
        try std.testing.expectEqual(@as(u64, 0), discarded.growth.count);
    }
    var candidate = try live.cloneBanks();
    try std.testing.expectEqual(protocol.Status.ok, candidate.ensureBankCapacity(demand));
    live.commitBanks(&candidate);
    var diagnostic: protocol.WorkspaceCapacity = undefined;
    live.fillWorkspaceCapacity(&diagnostic);
    try std.testing.expectEqual(@as(u64, 5), diagnostic.growth.count);
    for (diagnostic.growth.events[0..5], 0..) |event, index| {
        try std.testing.expectEqual(@as(u64, index + 1), event.sequence);
        try std.testing.expectEqual(@as(u32, @intCast(index + 8)), event.reason);
        try std.testing.expectEqual(@as(u32, 0), event.unit);
        try std.testing.expectEqual(@as(u64, 1), event.old_capacity);
        try std.testing.expectEqual(@as(u64, 4), event.new_capacity);
    }
    var unchanged = try live.cloneBanks();
    live.commitBanks(&unchanged);
    try std.testing.expectEqual(@as(u64, 5), live.growth.count);
    try std.testing.expectEqual(protocol.Status.ok, live.reset());
    try std.testing.expectEqual(protocol.Status.ok, live.reconfigure(&config));
    try std.testing.expectEqual(@as(u64, 5), live.growth.count);
    var next = try live.cloneBanks();
    try std.testing.expectEqual(protocol.Status.ok, next.ensureBankCapacity(.{ .processes = 5, .softwares = 4, .gpus = 4, .reservations = 4, .atomic_groups = 4 }));
    live.commitBanks(&next);
    try std.testing.expectEqual(@as(u64, 6), live.growth.count);
    try std.testing.expectEqual(@as(u64, 6), live.growth.events[5].sequence);
    try std.testing.expectEqual(@as(u32, 8), live.growth.events[5].reason);
    try std.testing.expectEqual(@as(u64, 4), live.growth.events[5].old_capacity);
    try std.testing.expectEqual(@as(u64, 8), live.growth.events[5].new_capacity);
}
