const std = @import("std");
const calibration = @import("calibration.zig");
const corpus = @import("calibration_corpus.zig");
const observations = @import("calibration_observations.zig");

const valid_input = observations.schema_header ++ "\n" ++
    "1\t1\tmemory\t0\t1\tstrongest_required\n" ++
    "1\t2\tmemory\t1000\t1\toptimize_required\n" ++
    "1\t3\tmemory\t3000\t1\tnormal_sufficient\n" ++
    "1\t4\tmemory\t6000\t1\tunrestricted_safe\n" ++
    "2\t1\tmemory\t0\t1\tstrongest_required\n" ++
    "2\t2\tmemory\t1000\t1\toptimize_required\n" ++
    "2\t3\tmemory\t3000\t1\tnormal_sufficient\n" ++
    "2\t4\tmemory\t6000\t1\tunrestricted_safe\n";

test "ordinal observations assemble whole cases into reproducible partitions" {
    var parsed_storage: [8]observations.Observation = undefined;
    const parsed = try observations.parse(.memory, valid_input, &parsed_storage);
    var sample_storage: [8]calibration.Sample = undefined;
    const first = try observations.assemble(.memory, parsed, .{
        .seed = 7,
        .validation_percent = 20,
    }, &sample_storage);

    try std.testing.expectEqual(@as(u32, 8), first.stats.observation_count);
    try std.testing.expectEqual(@as(u32, 1), first.stats.training_case_count);
    try std.testing.expectEqual(@as(u32, 1), first.stats.validation_case_count);
    try std.testing.expectEqual(@as(u32, 4), first.stats.training_observation_count);
    try std.testing.expectEqual(@as(u32, 4), first.stats.validation_observation_count);
    try std.testing.expectEqual(calibration.Partition.training, first.samples[0].partition);
    try std.testing.expectEqual(calibration.Partition.training, first.samples[3].partition);
    try std.testing.expectEqual(calibration.Partition.validation, first.samples[4].partition);
    try std.testing.expectEqual(calibration.Partition.validation, first.samples[7].partition);
    try std.testing.expect(first.samples[0].strongest_required);
    try std.testing.expect(!first.samples[1].strongest_required);
    try std.testing.expect(first.samples[2].normal_sufficient);
    try std.testing.expect(first.samples[3].unrestricted_safe);

    var rendered: std.Io.Writer.Allocating = .init(std.testing.allocator);
    defer rendered.deinit();
    try observations.renderCanonical(.memory, first.samples, &rendered.writer);
    var reparsed_storage: [8]calibration.Sample = undefined;
    const reparsed = try corpus.parse(.memory, rendered.written(), &reparsed_storage);
    try std.testing.expectEqualSlices(calibration.Sample, first.samples, reparsed);

    var repeated_storage: [8]calibration.Sample = undefined;
    const repeated = try observations.assemble(.memory, parsed, .{
        .seed = 7,
        .validation_percent = 20,
    }, &repeated_storage);
    try std.testing.expectEqualSlices(calibration.Sample, first.samples, repeated.samples);
}

test "case split mapping has fixed cross-implementation vectors" {
    try std.testing.expect(!observations.isValidationCase(7, 1, 20));
    try std.testing.expect(observations.isValidationCase(7, 2, 20));
    try std.testing.expect(observations.isValidationCase(7, 5, 20));
    try std.testing.expect(!observations.isValidationCase(7, 6, 20));
    try std.testing.expect(observations.isValidationCase(7, 12, 20));
}

test "observation parser rejects ambiguous ordering labels and domains" {
    var output: [2]observations.Observation = undefined;
    const leading_zero = observations.schema_header ++
        "\n01\t1\tmemory\t0\t1\tstrongest_required\n";
    try std.testing.expectError(
        error.NonCanonicalInteger,
        observations.parse(.memory, leading_zero, &output),
    );

    const reordered = observations.schema_header ++
        "\n2\t1\tmemory\t0\t1\tstrongest_required\n" ++
        "1\t1\tmemory\t0\t1\tstrongest_required\n";
    try std.testing.expectError(
        error.NonCanonicalObservations,
        observations.parse(.memory, reordered, &output),
    );

    const invalid_outcome = observations.schema_header ++
        "\n1\t1\tmemory\t0\t1\tmaybe_safe\n";
    try std.testing.expectError(
        error.InvalidOutcome,
        observations.parse(.memory, invalid_outcome, &output),
    );

    const mixed = observations.schema_header ++
        "\n1\t1\tvram\t0\t1\tstrongest_required\n";
    try std.testing.expectError(error.InvalidDomain, observations.parse(.memory, mixed, &output));
}

test "assembler refuses partition leakage and incomplete outcome evidence" {
    var parsed_storage: [8]observations.Observation = undefined;
    const parsed = try observations.parse(.memory, valid_input, &parsed_storage);
    var sample_storage: [8]calibration.Sample = undefined;
    try std.testing.expectError(
        error.IncompleteValidationOutcomes,
        observations.assemble(.memory, parsed[0..4], .{
            .seed = 7,
            .validation_percent = 20,
        }, &sample_storage),
    );
    try std.testing.expectError(
        error.InvalidSplitPolicy,
        observations.assemble(.memory, parsed, .{
            .seed = 7,
            .validation_percent = 100,
        }, &sample_storage),
    );

    var invalid = parsed_storage;
    invalid[0].case_id = 0;
    try std.testing.expectError(
        error.NonCanonicalObservations,
        observations.assemble(.memory, &invalid, .{
            .seed = 7,
            .validation_percent = 20,
        }, &sample_storage),
    );

    invalid = parsed_storage;
    invalid[1].free_ratio_units = calibration.ratio_units_max + 1;
    try std.testing.expectError(
        error.InvalidObservation,
        observations.assemble(.memory, &invalid, .{
            .seed = 7,
            .validation_percent = 20,
        }, &sample_storage),
    );
}
