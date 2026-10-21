const std = @import("std");
const protocol = @import("protocol.zig");
const session_module = @import("session.zig");
const process_scores = @import("process_scores.zig");

test "each software receives one full welfare independent of software and member counts" {
    var config = testConfig();
    const session = try session_module.Session.create(&config, &.{1});
    defer session.destroy();
    var processes = [_]protocol.ProcessInput{
        processRow(1, 10, 100, 1, 100, 1, 0),
        processRow(2, 20, 200, 2, 100, 1, 0),
        processRow(3, 30, 300, 1, 100, 1, 0),
        processRow(4, 40, 400, 3, 100, 1, 0),
    };
    for ([_]usize{ 2, 3, 4 }, 1..) |count, generation| {
        var envelope = testEnvelope(generation, count, 0, 0, protocol.EnvelopeFlags.cpu_snapshot_complete);
        envelope.cpu_free_ratio = 1;
        envelope.gpu_free_ratio = 1;
        envelope.vram_free_ratio = 1;
        envelope.memory_free_ratio = 1;
        var outputs: [16]protocol.ScoreOutput = undefined;
        envelope.output_capacity = outputs.len;
        var snapshot = std.mem.zeroes(protocol.Snapshot);
        try std.testing.expectEqual(protocol.Status.ok, scoreWithScratchFixture(
            session, &envelope, &processes, @intCast(count), null, 0,
            &outputs, outputs.len, &snapshot,
        ));
        var software_count: usize = 0;
        for (outputs[0..snapshot.output_count]) |row| {
            if (row.kind != @intFromEnum(protocol.OutputKind.software_cpu)) continue;
            try std.testing.expectEqual(@as(f64, 60), row.score);
            software_count += 1;
        }
        try std.testing.expectEqual(if (count == 4) @as(usize, 3) else 2, software_count);
    }
}

test "software mean is arithmetic without a fixed welfare base or runtime weighting" {
    try std.testing.expectEqual(@as(f64, 60), try process_scores.softwareBaseMean(&.{ 80, 40 }, 100));
    try std.testing.expectEqual(@as(f64, 0), try process_scores.softwareBaseMean(&.{}, 100));
    try std.testing.expectEqual(@as(f64, 40), try process_scores.softwareBaseMean(&.{ 80, 40, 0 }, 100));
    for ([_]f64{ -1, 101, std.math.nan(f64), std.math.inf(f64) }) |bad| {
        try std.testing.expectError(error.InvalidScoreInput, process_scores.softwareBaseMean(&.{ 60, bad }, 100));
    }
}

test "welfare utilization cutoff maps 70 and 35 without a negative bonus" {
    const usages = [_]f64{ 0, 17.5, 35, 52.5, 70, 85, 100 };
    const ratios = [_]f64{ 1, 0.75, 0.5, 0.25, 0, 0, 0 };
    for (usages, ratios) |usage, ratio| {
        try std.testing.expectApproxEqAbs(ratio, try process_scores.welfareRatio(1 - usage / 100, 70), 1e-12);
    }
    try std.testing.expectApproxEqAbs(@as(f64, 0.65), try process_scores.welfareRatio(0.65, 100), 1e-12);
    for ([_]f64{ 0, -1, 101, 140, std.math.nan(f64), std.math.inf(f64) }) |bad| {
        var config = testConfig();
        config.welfare_utilization_baseline_percent = bad;
        try std.testing.expect(!protocol.validateConfig(&config));
        try std.testing.expectError(error.InvalidScoreInput, process_scores.welfareRatio(0, bad));
    }
}

test "software memory score uses CPU raw sum and only memory welfare" {
    var config = testConfig();
    config.cpu_baseline_ratio = 0.5;
    const session = try session_module.Session.create(&config, &.{1});
    defer session.destroy();
    var processes = [_]protocol.ProcessInput{
        processRow(1, 10, 100, 1, 100, 1, 10),
        processRow(2, 20, 200, 1, 100, 1, 20),
    };
    for ([_]f64{ 0.65, 0 }, 1..) |cpu_free, generation| {
        var envelope = testEnvelope(generation, processes.len, 0, 0, protocol.EnvelopeFlags.cpu_snapshot_complete);
        envelope.cpu_free_ratio = cpu_free;
        envelope.memory_free_ratio = 1;
        var outputs: [16]protocol.ScoreOutput = undefined;
        envelope.output_capacity = outputs.len;
        var snapshot = std.mem.zeroes(protocol.Snapshot);
        try std.testing.expectEqual(protocol.Status.ok, scoreWithScratchFixture(
            session, &envelope, &processes, processes.len, null, 0,
            &outputs, outputs.len, &snapshot,
        ));
        try std.testing.expectEqual(@as(u32, 1), snapshot.software_memory_output_count);
        for (outputs[0..snapshot.output_count]) |row| {
            if (row.kind == @intFromEnum(protocol.OutputKind.software_memory))
                try std.testing.expectApproxEqAbs(@as(f64, 120), row.score, 1e-10);
            if (row.kind == @intFromEnum(protocol.OutputKind.software_cpu))
                try std.testing.expectApproxEqAbs(if (generation == 1) @as(f64, 90) else 60, row.score, 1e-10);
        }
    }
}

test "virtual software shares its remaining capacity score without recipient state or baseline multipliers" {
    var config = testConfig();
    config.cpu_baseline_ratio = 0.5;
    config.cpu_state_multipliers[1] = 3;
    config.gpu_state_multipliers[1] = 2;
    var envelope = testEnvelope(1, 2, 0, 0, protocol.EnvelopeFlags.cpu_snapshot_complete);
    envelope.cpu_free_ratio = 0.5;
    envelope.gpu_free_ratio = 1;
    envelope.vram_free_ratio = 1;
    envelope.memory_free_ratio = 1;
    envelope.software_base_mean = try process_scores.softwareBaseMean(&.{ 80, 40 }, 100);
    const welfare = try process_scores.calculateWelfare(&config, &envelope);
    try std.testing.expectEqual(@as(f64, 30), welfare.budget);
    try std.testing.expectEqual(@as(f64, 15), welfare.share);
    var process = processRow(1, 10, 100, 1, 80, 2, 25);
    process.runtime_state = 1;
    try std.testing.expectEqual(@as(f64, 255), try process_scores.cpu(&config, &process, welfare.share));
    const gpu = gpuRow(process, 11, 1, 25);
    try std.testing.expectEqual(@as(f64, 55), try process_scores.gpu(&config, &process, &gpu, welfare.share));

    envelope.welfare_eligible_process_count = 0;
    try std.testing.expectEqual(@as(f64, 0), (try process_scores.calculateWelfare(&config, &envelope)).share);
    envelope.software_base_mean = 0;
    try std.testing.expectEqual(@as(f64, 0), (try process_scores.calculateWelfare(&config, &envelope)).budget);
    envelope.software_base_mean = 101;
    try std.testing.expectError(error.InvalidScoreInput, process_scores.calculateWelfare(&config, &envelope));
}

test "CPU configuration requires an explicit baseline ratio and current scoring ABI" {
    var config = testConfig();
    config.cpu_baseline_ratio = 0;
    try std.testing.expect(!protocol.validateConfig(&config));
    config.cpu_baseline_ratio = 0.5;
    try std.testing.expect(protocol.validateConfig(&config));
    config.abi_version = 0x0002_0001;
    try std.testing.expect(!protocol.validateConfig(&config));
}

test "configuration rejects finite values whose declared score range can overflow" {
    var config = testConfig();
    config.maximum_base_importance = 1.0e308;
    try std.testing.expect(!protocol.validateConfig(&config));

    config = testConfig();
    config.maximum_policy_multiplier = 1.0e200;
    try std.testing.expect(!protocol.validateConfig(&config));

    try std.testing.expect(protocol.validateConfig(&testConfig()));
}

test "all CPU scores use a shared baseline without clipping reference use" {
    for ([_]f64{ 1, 0.5, 0.25 }) |reference_ratio| {
        var config = testConfig();
        config.cpu_baseline_ratio = reference_ratio;
        const session = try session_module.Session.create(&config, &.{1});
        defer session.destroy();
        for ([_]f64{ 0, 10, 40, 80, 100 }, 0..) |occupancy, index| {
            var processes = [_]protocol.ProcessInput{
                processRow(1, 10, 100, 1, 100, 1, occupancy),
            };
            const score = try runCpuSoftwareScore(session, index + 1, &processes);
            try std.testing.expectApproxEqAbs(
                occupancy / reference_ratio,
                score,
                0.0000001,
            );
        }
    }
}

test "baseline normalization is identical for every software and preserves member sums" {
    var config = testConfig();
    config.cpu_baseline_ratio = 0.5;
    const session = try session_module.Session.create(&config, &.{1});
    defer session.destroy();
    var processes = [_]protocol.ProcessInput{
        processRow(1, 10, 100, 1, 100, 1, 30),
        processRow(2, 20, 200, 1, 100, 1, 20),
        processRow(3, 30, 300, 2, 100, 1, 10),
    };
    var envelope = testEnvelope(1, processes.len, 0, 0, protocol.EnvelopeFlags.cpu_snapshot_complete);
    var outputs: [8]protocol.ScoreOutput = undefined;
    envelope.output_capacity = outputs.len;
    var snapshot = std.mem.zeroes(protocol.Snapshot);
    try std.testing.expectEqual(protocol.Status.ok, scoreWithScratchFixture(
        session,
        &envelope,
        &processes,
        processes.len,
        null,
        0,
        &outputs,
        outputs.len,
        &snapshot,
    ));
    try std.testing.expectEqual(@as(u32, 3), snapshot.process_cpu_output_count);
    try std.testing.expectEqual(@as(u32, 2), snapshot.software_cpu_output_count);
    try std.testing.expectEqual(@as(f64, 60), outputs[0].score);
    try std.testing.expectEqual(@as(f64, 40), outputs[1].score);
    try std.testing.expectEqual(@as(f64, 20), outputs[2].score);
    try std.testing.expectEqual(@as(f64, 100), outputs[3].score);
    try std.testing.expectEqual(@as(u32, 2), outputs[3].member_count);
    try std.testing.expectEqual(@as(f64, 20), outputs[4].score);
    try std.testing.expectEqual(@as(u32, 1), outputs[4].member_count);
}

test "configuration output capacity covers every process and software score" {
    var config = testConfig();
    const required_output_count = 3 * config.maximum_process_count + 2 * config.maximum_gpu_row_count;

    config.maximum_output_count = required_output_count;
    try std.testing.expect(protocol.validateConfig(&config));

    config.maximum_output_count = required_output_count - 1;
    try std.testing.expect(!protocol.validateConfig(&config));

    config.maximum_process_count = std.math.maxInt(u32);
    config.maximum_gpu_row_count = 1;
    config.maximum_output_count = std.math.maxInt(u32);
    try std.testing.expect(!protocol.validateConfig(&config));
}

test "zero occupancy is exact zero and software CPU is the member sum" {
    var config = testConfig();
    const session = try session_module.Session.create(&config, &.{1});
    defer session.destroy();
    var processes = [_]protocol.ProcessInput{
        processRow(2, 20, 200, 1, 100, 1, 0),
        processRow(1, 10, 100, 1, 100, 1, 50),
    };
    var envelope = testEnvelope(1, processes.len, 0, 0, protocol.EnvelopeFlags.cpu_snapshot_complete);
    var outputs: [8]protocol.ScoreOutput = undefined;
    envelope.output_capacity = outputs.len;
    var snapshot = std.mem.zeroes(protocol.Snapshot);
    try std.testing.expectEqual(protocol.Status.ok, scoreWithScratchFixture(
        session,
        &envelope,
        &processes,
        processes.len,
        null,
        0,
        &outputs,
        outputs.len,
        &snapshot,
    ));
    try std.testing.expectEqual(@as(u32, 2), snapshot.process_cpu_output_count);
    try std.testing.expectEqual(@as(u32, 1), snapshot.software_cpu_output_count);
    try std.testing.expectEqual(@as(f64, 50), outputs[0].score);
    try std.testing.expectEqual(@as(f64, 0), outputs[1].score);
    try std.testing.expect(!std.math.signbit(outputs[1].score));
    try std.testing.expectEqual(@as(f64, 50), outputs[2].score);
    try std.testing.expectEqual(@as(u32, 2), outputs[2].member_count);
}

test "per adapter GPU scores are isolated and aggregate by stable adapter key" {
    var config = testConfig();
    config.cpu_baseline_ratio = 0.5;
    const session = try session_module.Session.create(&config, &.{1});
    defer session.destroy();
    var processes = [_]protocol.ProcessInput{
        processRow(1, 10, 100, 1, 80, 1, 20),
        processRow(2, 20, 200, 1, 80, 1, 30),
    };
    var gpus = [_]protocol.GpuInput{
        gpuRow(processes[1], 22, 2, 0),
        gpuRow(processes[0], 11, 1, 25),
        gpuRow(processes[0], 22, 1, 50),
        gpuRow(processes[1], 11, 2, 25),
    };
    var envelope = testEnvelope(
        1,
        processes.len,
        gpus.len,
        2,
        protocol.EnvelopeFlags.cpu_snapshot_complete | protocol.EnvelopeFlags.gpu_snapshot_complete,
    );
    var outputs: [16]protocol.ScoreOutput = undefined;
    envelope.output_capacity = outputs.len;
    var snapshot = std.mem.zeroes(protocol.Snapshot);
    try std.testing.expectEqual(protocol.Status.ok, scoreWithScratchFixture(
        session,
        &envelope,
        &processes,
        processes.len,
        &gpus,
        gpus.len,
        &outputs,
        outputs.len,
        &snapshot,
    ));
    try std.testing.expectEqual(@as(u32, 4), snapshot.process_gpu_output_count);
    try std.testing.expectEqual(@as(u32, 2), snapshot.software_gpu_output_count);
    try std.testing.expectEqual(@as(u64, 303), snapshot.gpu_topology_generation);
    const software_gpu_start = snapshot.process_cpu_output_count + snapshot.software_cpu_output_count +
        snapshot.process_gpu_output_count;
    try std.testing.expectEqual(@as(u64, 11), outputs[software_gpu_start].adapter_key);
    try std.testing.expectEqual(@as(f64, 40), outputs[software_gpu_start].score);
    try std.testing.expectEqual(@as(u64, 22), outputs[software_gpu_start + 1].adapter_key);
    try std.testing.expectEqual(@as(f64, 40), outputs[software_gpu_start + 1].score);
}

test "complete GPU scoring rejects a missing topology generation" {
    var config = testConfig();
    const session = try session_module.Session.create(&config, &.{1});
    defer session.destroy();
    var processes = [_]protocol.ProcessInput{processRow(1, 10, 100, 1, 80, 1, 20)};
    var gpus = [_]protocol.GpuInput{gpuRow(processes[0], 11, 1, 25)};
    var envelope = testEnvelope(1, 1, 1, 1, protocol.EnvelopeFlags.gpu_snapshot_complete);
    envelope.gpu_topology_generation = 0;
    var outputs: [4]protocol.ScoreOutput = undefined;
    envelope.output_capacity = outputs.len;
    var snapshot = std.mem.zeroes(protocol.Snapshot);

    try std.testing.expectEqual(protocol.Status.invalid_facts, scoreWithScratchFixture(
        session,
        &envelope,
        &processes,
        processes.len,
        &gpus,
        gpus.len,
        &outputs,
        outputs.len,
        &snapshot,
    ));
}

test "source and topology generations remain monotonic across accepted scores" {
    var config = testConfig();
    const session = try session_module.Session.create(&config, &.{1});
    defer session.destroy();
    var processes = [_]protocol.ProcessInput{processRow(1, 10, 100, 1, 80, 1, 20)};
    var gpus = [_]protocol.GpuInput{gpuRow(processes[0], 11, 1, 25)};
    var envelope = testEnvelope(1, 1, 1, 1, protocol.EnvelopeFlags.gpu_snapshot_complete);
    var outputs: [4]protocol.ScoreOutput = undefined;
    envelope.output_capacity = outputs.len;
    var snapshot = std.mem.zeroes(protocol.Snapshot);

    try std.testing.expectEqual(protocol.Status.ok, scoreWithScratchFixture(
        session,
        &envelope,
        &processes,
        processes.len,
        &gpus,
        gpus.len,
        &outputs,
        outputs.len,
        &snapshot,
    ));

    envelope.scheduling_generation = 2;
    envelope.process_source_generation = 100;
    processes[0].source_generation = 100;
    try std.testing.expectEqual(protocol.Status.stale_generation, scoreWithScratchFixture(
        session,
        &envelope,
        &processes,
        processes.len,
        &gpus,
        gpus.len,
        &outputs,
        outputs.len,
        &snapshot,
    ));

    envelope.process_source_generation = 102;
    processes[0].source_generation = 102;
    envelope.gpu_source_generation = 201;
    gpus[0].source_generation = 201;
    try std.testing.expectEqual(protocol.Status.stale_generation, scoreWithScratchFixture(
        session,
        &envelope,
        &processes,
        processes.len,
        &gpus,
        gpus.len,
        &outputs,
        outputs.len,
        &snapshot,
    ));

    envelope.gpu_source_generation = 203;
    gpus[0].source_generation = 203;
    envelope.gpu_topology_generation = 302;
    try std.testing.expectEqual(protocol.Status.stale_generation, scoreWithScratchFixture(
        session,
        &envelope,
        &processes,
        processes.len,
        &gpus,
        gpus.len,
        &outputs,
        outputs.len,
        &snapshot,
    ));

    envelope.gpu_topology_generation = 303;
    envelope.gpu_topology_fingerprint = 405;
    try std.testing.expectEqual(protocol.Status.conflicting_facts, scoreWithScratchFixture(
        session,
        &envelope,
        &processes,
        processes.len,
        &gpus,
        gpus.len,
        &outputs,
        outputs.len,
        &snapshot,
    ));

    envelope.gpu_topology_fingerprint = 404;
    try std.testing.expectEqual(protocol.Status.ok, scoreWithScratchFixture(
        session,
        &envelope,
        &processes,
        processes.len,
        &gpus,
        gpus.len,
        &outputs,
        outputs.len,
        &snapshot,
    ));
}

test "sparse GPU rows accept different observed adapter sets" {
    var config = testConfig();
    const session = try session_module.Session.create(&config, &.{1});
    defer session.destroy();
    var processes = [_]protocol.ProcessInput{
        processRow(1, 10, 100, 1, 80, 1, 20),
        processRow(2, 20, 200, 1, 80, 1, 30),
    };
    var gpus = [_]protocol.GpuInput{
        gpuRow(processes[0], 11, 1, 25),
        gpuRow(processes[0], 22, 1, 50),
        gpuRow(processes[1], 11, 1, 25),
        gpuRow(processes[1], 33, 1, 50),
    };
    var envelope = testEnvelope(
        1,
        processes.len,
        gpus.len,
        2,
        protocol.EnvelopeFlags.gpu_snapshot_complete,
    );
    var outputs: [16]protocol.ScoreOutput = undefined;
    envelope.output_capacity = outputs.len;
    var snapshot = std.mem.zeroes(protocol.Snapshot);

    try std.testing.expectEqual(protocol.Status.ok, scoreWithScratchFixture(
        session,
        &envelope,
        &processes,
        processes.len,
        &gpus,
        gpus.len,
        &outputs,
        outputs.len,
        &snapshot,
    ));
}

test "sparse GPU rows reject a process without an observed adapter" {
    var config = testConfig();
    const session = try session_module.Session.create(&config, &.{1});
    defer session.destroy();
    var processes = [_]protocol.ProcessInput{
        processRow(1, 10, 100, 1, 80, 1, 20),
        processRow(2, 20, 200, 1, 80, 1, 30),
    };
    var gpus = [_]protocol.GpuInput{
        gpuRow(processes[0], 11, 1, 25),
    };
    var envelope = testEnvelope(
        1,
        processes.len,
        gpus.len,
        2,
        protocol.EnvelopeFlags.gpu_snapshot_complete,
    );
    var outputs: [16]protocol.ScoreOutput = undefined;
    envelope.output_capacity = outputs.len;
    var snapshot = std.mem.zeroes(protocol.Snapshot);

    try std.testing.expectEqual(protocol.Status.conflicting_facts, scoreWithScratchFixture(
        session,
        &envelope,
        &processes,
        processes.len,
        &gpus,
        gpus.len,
        &outputs,
        outputs.len,
        &snapshot,
    ));
}

test "sparse GPU rows reject duplicate process adapter identity" {
    var config = testConfig();
    const session = try session_module.Session.create(&config, &.{1});
    defer session.destroy();
    var processes = [_]protocol.ProcessInput{
        processRow(1, 10, 100, 1, 80, 1, 20),
    };
    var gpus = [_]protocol.GpuInput{
        gpuRow(processes[0], 11, 1, 25),
        gpuRow(processes[0], 11, 1, 50),
    };
    var envelope = testEnvelope(
        1,
        processes.len,
        gpus.len,
        2,
        protocol.EnvelopeFlags.gpu_snapshot_complete,
    );
    var outputs: [16]protocol.ScoreOutput = undefined;
    envelope.output_capacity = outputs.len;
    var snapshot = std.mem.zeroes(protocol.Snapshot);

    try std.testing.expectEqual(protocol.Status.conflicting_facts, scoreWithScratchFixture(
        session,
        &envelope,
        &processes,
        processes.len,
        &gpus,
        gpus.len,
        &outputs,
        outputs.len,
        &snapshot,
    ));
}

test "split process occupancy does not add a process floor" {
    var config = testConfig();
    const first = try session_module.Session.create(&config, &.{1});
    defer first.destroy();
    const second = try session_module.Session.create(&config, &.{1});
    defer second.destroy();
    var one = [_]protocol.ProcessInput{processRow(1, 10, 100, 1, 90, 1, 60)};
    var split = [_]protocol.ProcessInput{
        processRow(1, 10, 100, 1, 90, 1, 20),
        processRow(2, 20, 200, 1, 90, 1, 40),
    };
    const one_score = try runCpuSoftwareScore(first, 1, &one);
    const split_score = try runCpuSoftwareScore(second, 1, &split);
    try std.testing.expectEqual(one_score, split_score);
    try std.testing.expectEqual(@as(f64, 54), one_score);
}

test "duplicate process instance is rejected even when software differs" {
    var config = testConfig();
    const session = try session_module.Session.create(&config, &.{1});
    defer session.destroy();
    var processes = [_]protocol.ProcessInput{
        processRow(1, 10, 100, 1, 100, 1, 10),
        processRow(2, 10, 100, 2, 100, 1, 20),
    };
    var envelope = testEnvelope(1, processes.len, 0, 0, protocol.EnvelopeFlags.cpu_snapshot_complete);
    var outputs: [8]protocol.ScoreOutput = undefined;
    envelope.output_capacity = outputs.len;
    var snapshot = std.mem.zeroes(protocol.Snapshot);
    try std.testing.expectEqual(protocol.Status.conflicting_facts, scoreWithScratchFixture(
        session,
        &envelope,
        &processes,
        processes.len,
        null,
        0,
        &outputs,
        outputs.len,
        &snapshot,
    ));
}

test "incomplete CPU domain publishes no score instead of a default" {
    var config = testConfig();
    const session = try session_module.Session.create(&config, &.{1});
    defer session.destroy();
    var processes = [_]protocol.ProcessInput{processRow(1, 10, 100, 1, 100, 1, 50)};
    var gpus = [_]protocol.GpuInput{gpuRow(processes[0], 11, 1, 25)};
    var envelope = testEnvelope(1, 1, 1, 1, protocol.EnvelopeFlags.gpu_snapshot_complete);
    var outputs: [8]protocol.ScoreOutput = undefined;
    envelope.output_capacity = outputs.len;
    var snapshot = std.mem.zeroes(protocol.Snapshot);
    try std.testing.expectEqual(protocol.Status.ok, scoreWithScratchFixture(
        session,
        &envelope,
        &processes,
        processes.len,
        &gpus,
        gpus.len,
        &outputs,
        outputs.len,
        &snapshot,
    ));
    try std.testing.expectEqual(@as(u32, 0), snapshot.process_cpu_output_count);
    try std.testing.expectEqual(@as(u32, 0), snapshot.software_cpu_output_count);
    try std.testing.expectEqual(@as(u32, 1), snapshot.process_gpu_output_count);
    try std.testing.expectEqual(@as(u32, 1), snapshot.software_gpu_output_count);
}

fn runCpuSoftwareScore(
    session: *session_module.Session,
    generation: u64,
    processes: []protocol.ProcessInput,
) !f64 {
    var envelope = testEnvelope(generation, processes.len, 0, 0, protocol.EnvelopeFlags.cpu_snapshot_complete);
    var outputs: [16]protocol.ScoreOutput = undefined;
    envelope.output_capacity = outputs.len;
    var snapshot = std.mem.zeroes(protocol.Snapshot);
    try std.testing.expectEqual(protocol.Status.ok, scoreWithScratchFixture(
        session,
        &envelope,
        processes.ptr,
        @intCast(processes.len),
        null,
        0,
        &outputs,
        outputs.len,
        &snapshot,
    ));
    for (outputs[0..snapshot.output_count]) |row| {
        if (row.kind == @intFromEnum(protocol.OutputKind.software_cpu)) return row.score;
    }
    return error.MissingSoftwareCpuScore;
}

fn testConfig() protocol.Config {
    return .{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.Config),
        .generation = 1,
        .field_mask = protocol.ConfigFields.required,
        .maximum_process_count = 16,
        .maximum_gpu_row_count = 32,
        .maximum_output_count = 112,
        .cpu_core_count = 1,
        .cpu_baseline_ratio = 1,
        .welfare_utilization_baseline_percent = 70,
        .cpu_state_multipliers = .{ 1, 1, 1, 1, 1, 1, 0 },
        .gpu_state_multipliers = .{ 1, 1, 1, 1, 1, 1, 0 },
        .maximum_base_importance = 100,
        .maximum_policy_multiplier = 4,
        .reserved = .{ 0, 0 },
    };
}

fn testEnvelope(
    generation: u64,
    process_count: usize,
    gpu_count: usize,
    adapter_count: u32,
    flags: u64,
) protocol.GenerationEnvelope {
    const gpu_complete = (flags & protocol.EnvelopeFlags.gpu_snapshot_complete) != 0;
    return .{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.GenerationEnvelope),
        .process_input_struct_size = @sizeOf(protocol.ProcessInput),
        .gpu_input_struct_size = @sizeOf(protocol.GpuInput),
        .output_struct_size = @sizeOf(protocol.ScoreOutput),
        .reserved0 = 0,
        .configuration_generation = 1,
        .scheduling_generation = generation,
        .process_source_generation = 101,
        .gpu_source_generation = if (gpu_complete) 202 else 0,
        .gpu_topology_generation = if (gpu_complete) 303 else 0,
        .gpu_topology_fingerprint = if (gpu_complete) 404 else 0,
        .process_observed_at_milliseconds = 1000,
        .gpu_observed_at_milliseconds = if (gpu_complete) 1001 else 0,
        .valid_mask = protocol.EnvelopeValidity.process_generation |
            protocol.EnvelopeValidity.process_observed_at |
            protocol.EnvelopeValidity.welfare_capacity |
            (if ((flags & protocol.EnvelopeFlags.gpu_snapshot_complete) != 0)
                protocol.EnvelopeValidity.gpu_generation | protocol.EnvelopeValidity.gpu_observed_at |
                    protocol.EnvelopeValidity.gpu_topology
            else
                0),
        .flags = flags,
        .process_count = @intCast(process_count),
        .gpu_row_count = @intCast(gpu_count),
        .gpu_adapter_count = adapter_count,
        .output_capacity = 1,
        .cpu_free_ratio = 0,
        .gpu_free_ratio = 0,
        .vram_free_ratio = 0,
        .memory_free_ratio = 0,
        .welfare_eligible_process_count = @intCast(process_count),
        .reserved1 = 0,
        .software_base_mean = 60,
    };
}

fn processRow(
    source_index: u32,
    process_id: u32,
    start_key: u64,
    software_key: u64,
    base_importance: f64,
    policy_multiplier: f64,
    occupancy: f64,
) protocol.ProcessInput {
    return .{
        .struct_size = @sizeOf(protocol.ProcessInput),
        .flags = protocol.ProcessFlags.running | protocol.ProcessFlags.cpu_metrics_complete |
            protocol.ProcessFlags.gpu_metrics_complete,
        .valid_mask = protocol.ProcessValidity.cpu_required,
        .target_key = 1000 + source_index,
        .software_key = software_key,
        .process_start_key = start_key,
        .source_generation = 101,
        .base_importance = base_importance,
        .cpu_policy_multiplier = policy_multiplier,
        .weighted_cpu_use_percent = occupancy,
        .source_index = source_index,
        .process_id = process_id,
        .runtime_state = @intFromEnum(protocol.RuntimeState.unknown),
        .reserved0 = .{ 0, 0, 0, 0, 0, 0, 0 },
        .reserved = 0,
    };
}

fn gpuRow(
    process: protocol.ProcessInput,
    adapter_key: u64,
    source_index: u32,
    occupancy: f64,
) protocol.GpuInput {
    return .{
        .struct_size = @sizeOf(protocol.GpuInput),
        .flags = 0,
        .valid_mask = protocol.GpuValidity.required,
        .target_key = process.target_key,
        .software_key = process.software_key,
        .process_start_key = process.process_start_key,
        .source_generation = 202,
        .adapter_key = adapter_key,
        .gpu_policy_multiplier = 1,
        .gpu_occupancy_percent = occupancy,
        .source_index = source_index,
        .process_id = process.process_id,
        .reserved = .{ 0, 0 },
    };
}

// Explicit allocation belongs to this test fixture, never to Session.score.
fn scoreWithScratchFixture(
    session: *session_module.Session, envelope: *const protocol.GenerationEnvelope,
    processes: ?[*]const protocol.ProcessInput, process_capacity: u32,
    gpus: ?[*]const protocol.GpuInput, gpu_capacity: u32,
    outputs: ?[*]protocol.ScoreOutput, output_capacity: u32, snapshot: *protocol.Snapshot,
) protocol.Status {
    const capacity = session.capacity();
    const words = std.testing.allocator.alloc(u64, @intCast((capacity.scratch_byte_count + 7) / 8)) catch @panic("test scratch allocation failed");
    defer std.testing.allocator.free(words);
    const bytes = std.mem.sliceAsBytes(words);
    @memset(bytes, 0xa5);
    return session_module.score(session, envelope, processes, process_capacity, gpus, gpu_capacity,
        outputs, output_capacity, @ptrCast(bytes.ptr), capacity.scratch_byte_count, snapshot);
}

test "scratch capacity is actual typed extent with unchanged ABI offsets" {
    const scratch = @import("scratch.zig");
    var config = testConfig();
    const session = try session_module.Session.create(&config, &.{1});
    defer session.destroy();
    const actual = session.capacity();
    try std.testing.expectEqual(@as(u32, 0x00050000), actual.abi_version);
    try std.testing.expectEqual(@as(usize, 72), @sizeOf(protocol.Capacity));
    try std.testing.expectEqual(@as(usize, 40), @offsetOf(protocol.Capacity, "scratch_byte_count"));
    try std.testing.expectEqual(@as(usize, 48), @offsetOf(protocol.Capacity, "scratch_alignment"));
    try std.testing.expectEqual(@as(usize, 52), @offsetOf(protocol.Capacity, "reserved0"));
    try std.testing.expectEqual(@as(usize, 56), @offsetOf(protocol.Capacity, "reserved"));
    try std.testing.expectEqual(@as(u64, 16) * (@sizeOf(scratch.ProcessWork) + 8) +
        @as(u64, 32) * (@sizeOf(scratch.GpuWork) + 8), actual.scratch_byte_count);
    try std.testing.expectEqual(@as(u32, scratch.alignment), actual.scratch_alignment);
    try std.testing.expectEqual(@as(u32, 0), actual.reserved0);
    try std.testing.expectEqualSlices(u64, &.{ 0, 0 }, &actual.reserved);
    // Layout only: no large allocation and no invented Int32 byte-count gate.
    config.maximum_process_count = std.math.maxInt(u32);
    config.maximum_gpu_row_count = std.math.maxInt(u32);
    try std.testing.expect(scratch.Layout.init(&config).byte_count > std.math.maxInt(u32));
}

test "bad scratch rejects before writes and cannot commit a generation" {
    var config = testConfig();
    const session = try session_module.Session.create(&config, &.{1});
    defer session.destroy();
    const capacity = session.capacity();
    const words = try std.testing.allocator.alloc(u64, @intCast((capacity.scratch_byte_count + 7) / 8));
    defer std.testing.allocator.free(words);
    const bytes = std.mem.sliceAsBytes(words);
    @memset(bytes, 0xa5);
    var processes = [_]protocol.ProcessInput{processRow(1, 10, 100, 1, 100, 1, 50)};
    var outputs = std.mem.zeroes([4]protocol.ScoreOutput);
    outputs[0].score = 987;
    var snapshot = std.mem.zeroes(protocol.Snapshot);
    snapshot.scheduling_generation = 999;
    var envelope = testEnvelope(1, 1, 0, 0, protocol.EnvelopeFlags.cpu_snapshot_complete);
    envelope.output_capacity = outputs.len;
    const bad = [_]struct { pointer: ?*anyopaque, length: u64, status: protocol.Status }{
        .{ .pointer = null, .length = capacity.scratch_byte_count, .status = .invalid_argument },
        .{ .pointer = @ptrCast(bytes.ptr), .length = capacity.scratch_byte_count - 1, .status = .buffer_too_small },
        .{ .pointer = @ptrCast(bytes.ptr + 1), .length = capacity.scratch_byte_count, .status = .invalid_argument },
        .{ .pointer = @ptrCast(bytes.ptr), .length = std.math.maxInt(u64), .status = .invalid_argument },
        .{ .pointer = @ptrFromInt(std.math.maxInt(usize) - 7), .length = capacity.scratch_byte_count, .status = .invalid_argument },
    };
    for (bad) |item| {
        try std.testing.expectEqual(item.status, session_module.score(session, &envelope, &processes, 1,
            null, 0, &outputs, outputs.len, item.pointer, item.length, &snapshot));
        try std.testing.expectEqual(@as(u64, 0), session.last_scheduling_generation);
        try std.testing.expectEqual(@as(u64, 0), session.last_process_source_generation);
        try std.testing.expectEqual(@as(f64, 987), outputs[0].score);
        try std.testing.expectEqual(@as(u64, 999), snapshot.scheduling_generation);
        for (bytes) |byte| try std.testing.expectEqual(@as(u8, 0xa5), byte);
    }
    try std.testing.expectEqual(protocol.Status.ok, session_module.score(session, &envelope, &processes, 1,
        null, 0, &outputs, outputs.len, @ptrCast(bytes.ptr), capacity.scratch_byte_count, &snapshot));
    try std.testing.expectEqual(@as(u64, 1), session.last_scheduling_generation);
    try std.testing.expectEqual(@as(f64, 50), outputs[0].score);
}

test "scratch cannot alias actual input output or envelope" {
    var config = testConfig();
    const session = try session_module.Session.create(&config, &.{1});
    defer session.destroy();
    const capacity = session.capacity();
    var processes = [_]protocol.ProcessInput{processRow(1, 10, 100, 1, 100, 1, 50)};
    var outputs = std.mem.zeroes([4]protocol.ScoreOutput);
    var snapshot = std.mem.zeroes(protocol.Snapshot);
    var envelope = testEnvelope(1, 1, 0, 0, protocol.EnvelopeFlags.cpu_snapshot_complete);
    envelope.output_capacity = outputs.len;
    for ([_]*anyopaque{ @ptrCast(&processes), @ptrCast(&outputs), @ptrCast(&envelope), @ptrCast(&snapshot) }) |pointer| {
        try std.testing.expectEqual(protocol.Status.invalid_argument, session_module.score(session, &envelope, &processes, 1,
            null, 0, &outputs, outputs.len, pointer, capacity.scratch_byte_count, &snapshot));
        try std.testing.expectEqual(@as(u64, 0), session.last_scheduling_generation);
        try std.testing.expectEqual(@as(f64, 50), processes[0].weighted_cpu_use_percent);
        try std.testing.expectEqual(@as(u64, 0), snapshot.scheduling_generation);
    }
}

test "one poisoned region is sequentially reused by independent CPU GPU sessions" {
    var config = testConfig();
    const cpu = try session_module.Session.create(&config, &.{1});
    defer cpu.destroy();
    const gpu = try session_module.Session.create(&config, &.{1});
    defer gpu.destroy();
    const capacity = cpu.capacity();
    try std.testing.expectEqual(capacity.scratch_byte_count, gpu.capacity().scratch_byte_count);
    const words = try std.testing.allocator.alloc(u64, @intCast((capacity.scratch_byte_count + 7) / 8));
    defer std.testing.allocator.free(words);
    const bytes = std.mem.sliceAsBytes(words);
    var processes = [_]protocol.ProcessInput{processRow(1, 10, 100, 1, 100, 1, 50)};
    var gpus = [_]protocol.GpuInput{gpuRow(processes[0], 11, 1, 25)};
    var cpu_outputs: [4]protocol.ScoreOutput = undefined;
    var gpu_outputs: [4]protocol.ScoreOutput = undefined;
    var cpu_snapshot = std.mem.zeroes(protocol.Snapshot);
    var gpu_snapshot = std.mem.zeroes(protocol.Snapshot);
    var cpu_envelope = testEnvelope(1, 1, 0, 0, protocol.EnvelopeFlags.cpu_snapshot_complete);
    var gpu_envelope = testEnvelope(1, 1, 1, 1, protocol.EnvelopeFlags.gpu_snapshot_complete);
    cpu_envelope.output_capacity = cpu_outputs.len;
    gpu_envelope.output_capacity = gpu_outputs.len;
    @memset(bytes, 0xa5);
    try std.testing.expectEqual(protocol.Status.ok, session_module.score(cpu, &cpu_envelope, &processes, 1,
        null, 0, &cpu_outputs, cpu_outputs.len, @ptrCast(bytes.ptr), capacity.scratch_byte_count, &cpu_snapshot));
    const saved = cpu_outputs;
    @memset(bytes, 0x5a);
    try std.testing.expectEqual(protocol.Status.ok, session_module.score(gpu, &gpu_envelope, &processes, 1,
        &gpus, 1, &gpu_outputs, gpu_outputs.len, @ptrCast(bytes.ptr), capacity.scratch_byte_count, &gpu_snapshot));
    try std.testing.expectEqual(@as(f64, 50), cpu_outputs[0].score);
    try std.testing.expectEqual(@as(f64, 25), gpu_outputs[0].score);
    try std.testing.expectEqualSlices(u8, std.mem.asBytes(&saved), std.mem.asBytes(&cpu_outputs));
    try std.testing.expectEqual(@as(u64, 1), cpu.last_scheduling_generation);
    try std.testing.expectEqual(@as(u64, 1), gpu.last_scheduling_generation);
    try std.testing.expectEqual(@as(u64, 0), cpu.last_gpu_source_generation);
    try std.testing.expectEqual(@as(u64, 202), gpu.last_gpu_source_generation);
    cpu_envelope.scheduling_generation = 2;
    @memset(bytes, 0xff);
    try std.testing.expectEqual(protocol.Status.ok, session_module.score(cpu, &cpu_envelope, &processes, 1,
        null, 0, &cpu_outputs, cpu_outputs.len, @ptrCast(bytes.ptr), capacity.scratch_byte_count, &cpu_snapshot));
    try std.testing.expectEqual(@as(u64, 1), gpu.last_scheduling_generation);
    try std.testing.expectEqual(protocol.Status.stale_generation, session_module.score(gpu, &gpu_envelope, &processes, 1,
        &gpus, 1, &gpu_outputs, gpu_outputs.len, @ptrCast(bytes.ptr), capacity.scratch_byte_count, &gpu_snapshot));
}
