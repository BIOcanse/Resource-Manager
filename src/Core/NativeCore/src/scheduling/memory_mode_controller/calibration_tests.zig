const std = @import("std");
const calibration = @import("calibration.zig");

test "calibration recovers exact conservative boundaries from split workload evidence" {
    const samples = [_]calibration.Sample{
        sample(1, .training, 0),
        sample(2, .training, 999),
        sample(3, .training, 1000),
        sample(4, .training, 2999),
        sample(5, .training, 3000),
        sample(6, .training, 5999),
        sample(7, .training, 6000),
        sample(8, .training, 10_000),
        sample(9, .validation, 0),
        sample(10, .validation, 999),
        sample(11, .validation, 1000),
        sample(12, .validation, 2999),
        sample(13, .validation, 3000),
        sample(14, .validation, 5999),
        sample(15, .validation, 6000),
        sample(16, .validation, 10_000),
    };
    const scratch = try std.testing.allocator.alloc(u64, calibration.required_scratch_words);
    defer std.testing.allocator.free(scratch);

    const result = try calibration.calibrate(.memory, &samples, scratch);

    try std.testing.expectEqual(calibration.Domain.memory, result.domain);
    try std.testing.expectEqual(@as(u32, 1000), result.thresholds.strong_begin_free_ratio);
    try std.testing.expectEqual(@as(u32, 3000), result.thresholds.normal_minimum_free_ratio);
    try std.testing.expectEqual(@as(u32, 6000), result.thresholds.unrestricted_minimum_free_ratio);
    try std.testing.expectEqual(@as(u64, 0), result.training.weighted_mismatch);
    try std.testing.expectEqual(@as(u64, 0), result.validation.weighted_mismatch);
    const policy = result.thresholds.toPressurePolicy();
    try std.testing.expectEqual(@as(u32, 10_000), policy.ratio_units_maximum);
    try std.testing.expectEqual(@as(u32, 1_000), policy.strong_begin_free_ratio_units);
    try std.testing.expectEqual(@as(u32, 3_000), policy.normal_minimum_free_ratio_units);
    try std.testing.expectEqual(@as(u32, 6_000), policy.unrestricted_minimum_free_ratio_units);
    try std.testing.expect(calibration.accepts(result, .{
        .minimum_training_samples = 8,
        .minimum_validation_samples = 8,
        .maximum_training_mismatch_per_10k = 0,
        .maximum_validation_mismatch_per_10k = 0,
    }));
}

test "calibration rejects noncanonical incomplete and inconsistent evidence" {
    var samples = [_]calibration.Sample{
        sample(2, .training, 999),
        sample(1, .validation, 6000),
    };
    const scratch = try std.testing.allocator.alloc(u64, calibration.required_scratch_words);
    defer std.testing.allocator.free(scratch);

    try std.testing.expectError(error.NonCanonicalSamples, calibration.calibrate(.memory, &samples, scratch));
    samples[0].sample_id = 1;
    samples[1].sample_id = 2;
    samples[0].unrestricted_safe = true;
    samples[0].normal_sufficient = false;
    try std.testing.expectError(error.InconsistentLabels, calibration.calibrate(.memory, &samples, scratch));
    samples[0] = sample(1, .training, 999);
    try std.testing.expectError(error.IncompleteTrainingCoverage, calibration.calibrate(.memory, &samples, scratch));
    try std.testing.expectError(
        error.ScratchTooSmall,
        calibration.calibrate(.memory, &samples, scratch[0 .. calibration.required_scratch_words - 1]),
    );
}

test "calibration uses fixed scratch for a repeatable 4096 observation corpus" {
    var samples: [4096]calibration.Sample = undefined;
    var pair: usize = 0;
    while (pair < samples.len / 2) : (pair += 1) {
        const boundary_points = [_]u32{ 0, 999, 1000, 2999, 3000, 5999, 6000, 10_000 };
        const units = if (pair < boundary_points.len)
            boundary_points[pair]
        else
            @as(u32, @intCast((pair * 997) % 10_001));
        samples[pair * 2] = sample(@intCast(pair * 2 + 1), .training, units);
        samples[pair * 2 + 1] = sample(@intCast(pair * 2 + 2), .validation, units);
    }
    const scratch = try std.testing.allocator.alloc(u64, calibration.required_scratch_words);
    defer std.testing.allocator.free(scratch);

    const first = try calibration.calibrate(.memory, &samples, scratch);
    const second = try calibration.calibrate(.memory, &samples, scratch);

    try std.testing.expectEqual(first.thresholds, second.thresholds);
    try std.testing.expectEqual(@as(u64, 0), first.training.weighted_mismatch);
    try std.testing.expectEqual(@as(u64, 0), first.validation.weighted_mismatch);
}

test "calibration acceptance gate rejects insufficient or mismatched evidence" {
    const evaluation = calibration.Evaluation{
        .sample_count = 10,
        .weighted_mismatch = 4,
        .weighted_label_count = 30,
    };
    const result = calibration.Result{
        .domain = .memory,
        .thresholds = .{
            .unrestricted_minimum_free_ratio = 6000,
            .normal_minimum_free_ratio = 3000,
            .strong_begin_free_ratio = 1000,
        },
        .training = evaluation,
        .validation = evaluation,
    };

    try std.testing.expect(!calibration.accepts(result, .{
        .minimum_training_samples = 11,
        .minimum_validation_samples = 10,
        .maximum_training_mismatch_per_10k = 10_000,
        .maximum_validation_mismatch_per_10k = 10_000,
    }));
    try std.testing.expect(!calibration.accepts(result, .{
        .minimum_training_samples = 10,
        .minimum_validation_samples = 10,
        .maximum_training_mismatch_per_10k = 1000,
        .maximum_validation_mismatch_per_10k = 1000,
    }));
    try std.testing.expect(calibration.accepts(result, .{
        .minimum_training_samples = 10,
        .minimum_validation_samples = 10,
        .maximum_training_mismatch_per_10k = 1334,
        .maximum_validation_mismatch_per_10k = 1334,
    }));
}

fn sample(
    sample_id: u64,
    partition: calibration.Partition,
    free_ratio_units: u32,
) calibration.Sample {
    return .{
        .sample_id = sample_id,
        .domain = .memory,
        .partition = partition,
        .free_ratio_units = free_ratio_units,
        .weight = 1,
        .unrestricted_safe = free_ratio_units >= 6000,
        .normal_sufficient = free_ratio_units >= 3000,
        .strongest_required = free_ratio_units < 1000,
    };
}
