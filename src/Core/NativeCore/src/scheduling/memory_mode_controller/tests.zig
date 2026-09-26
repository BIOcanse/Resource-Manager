const std = @import("std");
const protocol = @import("protocol.zig");
const desired_state = @import("desired_state.zig");

test "single memory domain ranks low CPU scores first" {
    var config = testConfig(1, 4);
    const session = try desired_state.Session.create(&config);
    defer session.destroy();

    var inputs = [_]protocol.SoftwareInput{
        input(1, 1, 40, 0),
        input(2, 1, 10, 1),
        input(3, 1, 30, 2),
        input(4, 1, 20, 3),
    };
    var outputs: [4]protocol.DesiredSoftwareOutput = undefined;
    var snapshot: protocol.Snapshot = undefined;
    var envelope = testEnvelope(1, 1, 1, 1, 500, 4, false);

    try std.testing.expectEqual(
        protocol.Status.ok,
        desired_state.plan(session, &envelope, &inputs, 4, &outputs, 4, &snapshot),
    );
    try std.testing.expectEqual(@as(u32, 2), snapshot.strongest_count);
    try std.testing.expectEqual(@as(u32, 2), snapshot.optimize_count);
    try std.testing.expectEqual(protocol.MemoryMode.optimize, mode(outputs[0]));
    try std.testing.expectEqual(protocol.MemoryMode.paged_frozen, mode(outputs[1]));
    try std.testing.expectEqual(protocol.MemoryMode.optimize, mode(outputs[2]));
    try std.testing.expectEqual(protocol.MemoryMode.paged_frozen, mode(outputs[3]));
    try std.testing.expectEqual(@as(u32, 0), outputs[1].rank);
    try std.testing.expectEqual(@as(u32, 1), outputs[3].rank);
    try std.testing.expectEqual(@as(u32, 2), outputs[2].rank);
    try std.testing.expectEqual(@as(u32, 3), outputs[0].rank);
}

test "equal scores use stable software identity and unrestricted is explicit" {
    var config = testConfig(1, 3);
    const session = try desired_state.Session.create(&config);
    defer session.destroy();
    var inputs = [_]protocol.SoftwareInput{
        inputWithBase(11, 1, 7, 81, 0),
        inputWithBase(12, 1, 7, 81, 1),
        inputWithBase(13, 1, 7, 81, 2),
    };
    var outputs: [3]protocol.DesiredSoftwareOutput = undefined;
    var snapshot: protocol.Snapshot = undefined;
    var envelope = testEnvelope(1, 1, 1, 1, 8_000, 3, true);

    try std.testing.expectEqual(
        protocol.Status.ok,
        desired_state.plan(session, &envelope, &inputs, 3, &outputs, 3, &snapshot),
    );
    for (outputs, 0..) |output, index| {
        try std.testing.expectEqual(protocol.MemoryMode.unrestricted, mode(output));
        try std.testing.expectEqual(@as(u32, @intCast(index)), output.rank);
    }
    try std.testing.expectEqual(
        protocol.SnapshotFlags.full_replacement | protocol.SnapshotFlags.unrestricted_allowed,
        snapshot.flags,
    );
}

test "base score tiers constrain modes after pressure ranking" {
    var config = testConfig(1, 3);
    const session = try desired_state.Session.create(&config);
    defer session.destroy();
    var inputs = [_]protocol.SoftwareInput{
        inputWithBase(1, 1, 1, 20, 0),
        inputWithBase(2, 1, 2, 21, 1),
        inputWithBase(3, 1, 3, 81, 2),
    };
    var outputs: [3]protocol.DesiredSoftwareOutput = undefined;
    var snapshot: protocol.Snapshot = undefined;
    var envelope = testEnvelope(1, 1, 1, 1, 0, 3, true);

    try std.testing.expectEqual(
        protocol.Status.ok,
        desired_state.plan(session, &envelope, &inputs, 3, &outputs, 3, &snapshot),
    );
    try std.testing.expectEqual(protocol.MemoryMode.paged_frozen, mode(outputs[0]));
    try std.testing.expectEqual(protocol.MemoryMode.optimize, mode(outputs[1]));
    try std.testing.expectEqual(protocol.MemoryMode.normal, mode(outputs[2]));
    try std.testing.expectEqual(@as(u8, 0), outputs[0].flags);
    try std.testing.expectEqual(
        protocol.DesiredSoftwareFlags.base_score_clamped,
        outputs[1].flags,
    );
    try std.testing.expectEqual(
        protocol.DesiredSoftwareFlags.base_score_clamped,
        outputs[2].flags,
    );
    try std.testing.expectEqual(@as(u32, 1), snapshot.strongest_count);
    try std.testing.expectEqual(@as(u32, 1), snapshot.optimize_count);
}

test "protected low CPU rows do not consume optimize pressure quota" {
    var config = testConfig(1, 2);
    const session = try desired_state.Session.create(&config);
    defer session.destroy();
    var inputs = [_]protocol.SoftwareInput{
        inputWithBase(1, 1, 0, 81, 0),
        inputWithBase(2, 1, 1, 21, 1),
    };
    var outputs: [2]protocol.DesiredSoftwareOutput = undefined;
    var snapshot: protocol.Snapshot = undefined;
    var envelope = testEnvelope(1, 1, 1, 1, 2_000, 2, false);

    try std.testing.expectEqual(
        protocol.Status.ok,
        desired_state.plan(session, &envelope, &inputs, 2, &outputs, 2, &snapshot),
    );
    try std.testing.expectEqual(protocol.MemoryMode.normal, mode(outputs[0]));
    try std.testing.expectEqual(protocol.MemoryMode.optimize, mode(outputs[1]));
    try std.testing.expectEqual(
        protocol.DesiredSoftwareFlags.base_score_clamped,
        outputs[0].flags,
    );
    try std.testing.expectEqual(@as(u32, 1), snapshot.optimize_count);
    try std.testing.expectEqual(@as(u32, 0), snapshot.strongest_count);
}

test "strongest and remaining restricted quotas refill over eligible tiers" {
    var config = testConfig(1, 4);
    const session = try desired_state.Session.create(&config);
    defer session.destroy();
    var inputs = [_]protocol.SoftwareInput{
        inputWithBase(1, 1, 0, 81, 0),
        inputWithBase(2, 1, 1, 21, 1),
        inputWithBase(3, 1, 2, 20, 2),
        inputWithBase(4, 1, 3, 20, 3),
    };
    var outputs: [4]protocol.DesiredSoftwareOutput = undefined;
    var snapshot: protocol.Snapshot = undefined;
    var envelope = testEnvelope(1, 1, 1, 1, 500, 4, false);

    try std.testing.expectEqual(
        protocol.Status.ok,
        desired_state.plan(session, &envelope, &inputs, 4, &outputs, 4, &snapshot),
    );
    try std.testing.expectEqual(protocol.MemoryMode.normal, mode(outputs[0]));
    try std.testing.expectEqual(protocol.MemoryMode.optimize, mode(outputs[1]));
    try std.testing.expectEqual(protocol.MemoryMode.paged_frozen, mode(outputs[2]));
    try std.testing.expectEqual(protocol.MemoryMode.paged_frozen, mode(outputs[3]));
    try std.testing.expectEqual(@as(u32, 1), snapshot.optimize_count);
    try std.testing.expectEqual(@as(u32, 2), snapshot.strongest_count);
}

test "memory planning has no GPU completeness dependency" {
    var config = testConfig(1, 2);
    const session = try desired_state.Session.create(&config);
    defer session.destroy();
    var inputs = [_]protocol.SoftwareInput{
        input(1, 1, 0, 0),
        input(2, 1, 1, 1),
    };
    var outputs: [2]protocol.DesiredSoftwareOutput = undefined;
    var snapshot: protocol.Snapshot = undefined;
    var envelope = testEnvelope(1, 1, 9, 1, 2_000, 2, false);

    try std.testing.expectEqual(
        protocol.Status.ok,
        desired_state.plan(session, &envelope, &inputs, 2, &outputs, 2, &snapshot),
    );
    try std.testing.expectEqual(@as(u64, 9), snapshot.memory_source_committed_generation);
    try std.testing.expectEqual(@as(u64, 1), snapshot.memory_source_workspace_identity);
    try std.testing.expectEqual(@as(u32, 1), snapshot.optimize_count);
    try std.testing.expectEqual(protocol.MemoryMode.optimize, mode(outputs[0]));
    try std.testing.expectEqual(protocol.MemoryMode.normal, mode(outputs[1]));
}

test "controller rejects stale generations and noncanonical facts" {
    var config = testConfig(1, 2);
    const session = try desired_state.Session.create(&config);
    defer session.destroy();
    var inputs = [_]protocol.SoftwareInput{
        input(1, 1, 1, 0),
        input(2, 1, 2, 1),
    };
    var outputs: [2]protocol.DesiredSoftwareOutput = undefined;
    var snapshot: protocol.Snapshot = undefined;
    var envelope = testEnvelope(1, 1, 4, 1, 4_000, 2, false);
    try std.testing.expectEqual(
        protocol.Status.ok,
        desired_state.plan(session, &envelope, &inputs, 2, &outputs, 2, &snapshot),
    );
    try std.testing.expectEqual(
        protocol.Status.stale_generation,
        desired_state.plan(session, &envelope, &inputs, 2, &outputs, 2, &snapshot),
    );

    envelope.scheduling_generation = 2;
    envelope.snapshot_generation = 2;
    envelope.memory_source_committed_generation = 3;
    inputs[0].scheduling_generation = 2;
    inputs[1].scheduling_generation = 2;
    try std.testing.expectEqual(
        protocol.Status.stale_generation,
        desired_state.plan(session, &envelope, &inputs, 2, &outputs, 2, &snapshot),
    );

    envelope.memory_source_committed_generation = 5;
    inputs[1].software_key = 1;
    try std.testing.expectEqual(
        protocol.Status.invalid_facts,
        desired_state.plan(session, &envelope, &inputs, 2, &outputs, 2, &snapshot),
    );
}

test "memory source lineage accepts reset and rejects regressions without advancing state" {
    var config = testConfig(1, 2);
    const session = try desired_state.Session.create(&config);
    defer session.destroy();
    var inputs = [_]protocol.SoftwareInput{
        input(1, 1, 1, 0),
        input(2, 1, 2, 1),
    };
    var outputs: [2]protocol.DesiredSoftwareOutput = undefined;
    var snapshot: protocol.Snapshot = undefined;
    var envelope = testEnvelope(1, 1, 20, 1, 4_000, 2, false);
    envelope.memory_source_workspace_identity = 10;

    try std.testing.expectEqual(
        protocol.Status.ok,
        desired_state.plan(session, &envelope, &inputs, 2, &outputs, 2, &snapshot),
    );

    envelope.scheduling_generation = 2;
    envelope.snapshot_generation = 2;
    envelope.memory_source_committed_generation = 19;
    for (&inputs) |*row| row.scheduling_generation = 2;
    try std.testing.expectEqual(
        protocol.Status.stale_generation,
        desired_state.plan(session, &envelope, &inputs, 2, &outputs, 2, &snapshot),
    );

    envelope.memory_source_committed_generation = 20;
    try std.testing.expectEqual(
        protocol.Status.ok,
        desired_state.plan(session, &envelope, &inputs, 2, &outputs, 2, &snapshot),
    );

    envelope.scheduling_generation = 3;
    envelope.snapshot_generation = 3;
    envelope.memory_source_workspace_identity = 11;
    envelope.memory_source_committed_generation = 1;
    for (&inputs) |*row| row.scheduling_generation = 3;
    try std.testing.expectEqual(
        protocol.Status.ok,
        desired_state.plan(session, &envelope, &inputs, 2, &outputs, 2, &snapshot),
    );
    try std.testing.expectEqual(@as(u64, 11), snapshot.memory_source_workspace_identity);
    try std.testing.expectEqual(@as(u64, 1), snapshot.memory_source_committed_generation);

    envelope.scheduling_generation = 4;
    envelope.snapshot_generation = 4;
    envelope.memory_source_workspace_identity = 10;
    envelope.memory_source_committed_generation = 99;
    for (&inputs) |*row| row.scheduling_generation = 4;
    try std.testing.expectEqual(
        protocol.Status.stale_generation,
        desired_state.plan(session, &envelope, &inputs, 2, &outputs, 2, &snapshot),
    );

    envelope.memory_source_workspace_identity = 11;
    envelope.memory_source_committed_generation = 1;
    try std.testing.expectEqual(
        protocol.Status.ok,
        desired_state.plan(session, &envelope, &inputs, 2, &outputs, 2, &snapshot),
    );
}

test "reconfigure changes thresholds but capacity changes require recreation" {
    var config = testConfig(1, 2);
    const session = try desired_state.Session.create(&config);
    defer session.destroy();
    var inputs = [_]protocol.SoftwareInput{
        input(1, 1, 1, 0),
        input(2, 1, 2, 1),
    };
    var outputs: [2]protocol.DesiredSoftwareOutput = undefined;
    var snapshot: protocol.Snapshot = undefined;
    var envelope = testEnvelope(1, 1, 20, 1, 4_000, 2, false);
    envelope.memory_source_workspace_identity = 10;
    try std.testing.expectEqual(
        protocol.Status.ok,
        desired_state.plan(session, &envelope, &inputs, 2, &outputs, 2, &snapshot),
    );

    var next = config;
    next.generation = 2;
    next.normal_minimum_free_ratio_units = 4_000;
    try std.testing.expectEqual(protocol.Status.ok, session.reconfigure(&next));
    const capacity = session.capacity();
    try std.testing.expectEqual(@as(u64, 2), capacity.configuration_generation);
    envelope.configuration_generation = 2;
    envelope.memory_source_workspace_identity = 1;
    envelope.memory_source_committed_generation = 1;
    try std.testing.expectEqual(
        protocol.Status.ok,
        desired_state.plan(session, &envelope, &inputs, 2, &outputs, 2, &snapshot),
    );

    next.generation = 3;
    next.maximum_output_count = 3;
    try std.testing.expectEqual(protocol.Status.recreate_required, session.reconfigure(&next));
}

test "v5 configuration and envelope are rejected without fallback" {
    var old_config = testConfig(1, 2);
    old_config.abi_version = 0x0005_0000;
    try std.testing.expectError(
        error.InvalidConfiguration,
        desired_state.Session.create(&old_config),
    );

    var config = testConfig(1, 2);
    const session = try desired_state.Session.create(&config);
    defer session.destroy();
    var inputs = [_]protocol.SoftwareInput{
        input(1, 1, 1, 0),
        input(2, 1, 2, 1),
    };
    var outputs: [2]protocol.DesiredSoftwareOutput = undefined;
    var snapshot: protocol.Snapshot = undefined;
    var envelope = testEnvelope(1, 1, 1, 1, 4_000, 2, false);
    envelope.abi_version = 0x0005_0000;

    try std.testing.expectEqual(
        protocol.Status.abi_mismatch,
        desired_state.plan(session, &envelope, &inputs, 2, &outputs, 2, &snapshot),
    );
}

test "large pressure grid preserves count summaries and score ordering" {
    const count: usize = 64;
    var config = testConfig(1, count);
    const session = try desired_state.Session.create(&config);
    defer session.destroy();
    var inputs: [count]protocol.SoftwareInput = undefined;
    var outputs: [count]protocol.DesiredSoftwareOutput = undefined;
    for (&inputs, 0..) |*row, index| {
        row.* = input(index + 1, 1, @floatFromInt(count - index), index);
    }

    var generation: u64 = 1;
    var step: u32 = 10_000;
    var previous_restricted: u32 = 0;
    while (true) {
        var snapshot: protocol.Snapshot = undefined;
        var envelope = testEnvelope(
            1,
            generation,
            generation,
            generation,
            step,
            count,
            true,
        );
        for (&inputs) |*row| row.scheduling_generation = generation;
        try std.testing.expectEqual(
            protocol.Status.ok,
            desired_state.plan(session, &envelope, &inputs, count, &outputs, count, &snapshot),
        );
        const restricted = snapshot.optimize_count + snapshot.strongest_count;
        try std.testing.expect(restricted >= previous_restricted);
        try std.testing.expect(restricted <= count);
        var seen: [count]bool = [_]bool{false} ** count;
        for (outputs) |output| {
            try std.testing.expect(output.rank < count);
            try std.testing.expect(!seen[output.rank]);
            seen[output.rank] = true;
        }
        previous_restricted = restricted;
        if (step == 0) break;
        step -= 1;
        generation += 1;
    }
}

fn testConfig(generation: u64, count: usize) protocol.Config {
    return .{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.Config),
        .generation = generation,
        .maximum_software_count = @intCast(count),
        .maximum_output_count = @intCast(count),
        .ratio_units_maximum = 10_000,
        .unrestricted_minimum_free_ratio_units = 6_000,
        .normal_minimum_free_ratio_units = 3_000,
        .strong_begin_free_ratio_units = 1_000,
        .middle_tier_minimum_base_score = 21,
        .high_tier_minimum_base_score = 81,
        .reserved = .{ 0, 0, 0 },
    };
}

fn testEnvelope(
    configuration_generation: u64,
    scheduling_generation: u64,
    memory_source_committed_generation: u64,
    snapshot_generation: u64,
    free_ratio_units: u32,
    count: usize,
    allow_unrestricted: bool,
) protocol.GenerationEnvelope {
    return .{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.GenerationEnvelope),
        .software_input_struct_size = @sizeOf(protocol.SoftwareInput),
        .software_output_struct_size = @sizeOf(protocol.DesiredSoftwareOutput),
        .configuration_generation = configuration_generation,
        .scheduling_generation = scheduling_generation,
        .memory_source_workspace_identity = 1,
        .memory_source_committed_generation = memory_source_committed_generation,
        .snapshot_generation = snapshot_generation,
        .valid_mask = protocol.EnvelopeValidity.required,
        .flags = protocol.EnvelopeFlags.required |
            (if (allow_unrestricted) protocol.EnvelopeFlags.allow_unrestricted else 0),
        .memory_free_ratio_units = free_ratio_units,
        .reserved0 = 0,
        .software_count = @intCast(count),
        .output_capacity = @intCast(count),
        .reserved = .{ 0, 0, 0 },
    };
}

fn input(
    software_key: anytype,
    scheduling_generation: u64,
    score: f64,
    source_index: anytype,
) protocol.SoftwareInput {
    return inputWithBase(software_key, scheduling_generation, score, 0, source_index);
}

fn inputWithBase(
    software_key: anytype,
    scheduling_generation: u64,
    score: f64,
    base_score: f64,
    source_index: anytype,
) protocol.SoftwareInput {
    return .{
        .struct_size = @sizeOf(protocol.SoftwareInput),
        .flags = 0,
        .valid_mask = protocol.SoftwareValidity.required,
        .software_key = @intCast(software_key),
        .scheduling_generation = scheduling_generation,
        .cpu_score = score,
        .base_score = base_score,
        .source_index = @intCast(source_index),
        .reserved0 = 0,
        .reserved = .{0},
    };
}

fn mode(output: protocol.DesiredSoftwareOutput) protocol.MemoryMode {
    return @enumFromInt(output.mode);
}
