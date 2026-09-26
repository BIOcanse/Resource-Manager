const std = @import("std");
const calibration = @import("calibration.zig");
const corpus = @import("calibration_corpus.zig");
const artifact = @import("calibration_artifact.zig");

const valid_corpus = corpus.schema_header ++ "\n" ++
    "1\tmemory\ttraining\t0\t1\t0\t0\t1\n" ++
    "2\tmemory\ttraining\t999\t1\t0\t0\t1\n" ++
    "3\tmemory\ttraining\t1000\t1\t0\t0\t0\n" ++
    "4\tmemory\ttraining\t2999\t1\t0\t0\t0\n" ++
    "5\tmemory\ttraining\t3000\t1\t0\t1\t0\n" ++
    "6\tmemory\ttraining\t5999\t1\t0\t1\t0\n" ++
    "7\tmemory\ttraining\t6000\t1\t1\t1\t0\n" ++
    "8\tmemory\ttraining\t10000\t1\t1\t1\t0\n" ++
    "9\tmemory\tvalidation\t0\t1\t0\t0\t1\n" ++
    "10\tmemory\tvalidation\t999\t1\t0\t0\t1\n" ++
    "11\tmemory\tvalidation\t1000\t1\t0\t0\t0\n" ++
    "12\tmemory\tvalidation\t2999\t1\t0\t0\t0\n" ++
    "13\tmemory\tvalidation\t3000\t1\t0\t1\t0\n" ++
    "14\tmemory\tvalidation\t5999\t1\t0\t1\t0\n" ++
    "15\tmemory\tvalidation\t6000\t1\t1\t1\t0\n" ++
    "16\tmemory\tvalidation\t10000\t1\t1\t1\t0\n";

const accepting_policy = calibration.AcceptancePolicy{
    .minimum_training_samples = 8,
    .minimum_validation_samples = 8,
    .maximum_training_mismatch_per_10k = 0,
    .maximum_validation_mismatch_per_10k = 0,
};

test "calibration artifact seals the exact corpus policy and result" {
    var samples: [16]calibration.Sample = undefined;
    const scratch = try std.testing.allocator.alloc(u64, calibration.required_scratch_words);
    defer std.testing.allocator.free(scratch);

    const first = try artifact.create(.memory, valid_corpus, &samples, scratch, accepting_policy);
    const second = try artifact.create(.memory, valid_corpus, &samples, scratch, accepting_policy);

    try std.testing.expectEqual(artifact.schema_version, first.version);
    try std.testing.expectEqual(@as(u64, valid_corpus.len), first.corpus_length);
    try std.testing.expectEqual(first, second);
    try std.testing.expect(!allZero(&first.corpus_sha256));
    try std.testing.expect(!allZero(&first.artifact_sha256));

    var encoded: [artifact.encoded_length]u8 = undefined;
    const bytes = try artifact.encode(first, &encoded);
    try std.testing.expectEqual(@as(usize, artifact.encoded_length), bytes.len);
    try std.testing.expectEqual(first, try artifact.decode(bytes));

    encoded[72] ^= 1;
    try std.testing.expectError(error.InvalidArtifact, artifact.decode(&encoded));
}

test "calibration artifact refuses to seal rejected evidence" {
    var samples: [16]calibration.Sample = undefined;
    const scratch = try std.testing.allocator.alloc(u64, calibration.required_scratch_words);
    defer std.testing.allocator.free(scratch);
    const rejecting_policy = calibration.AcceptancePolicy{
        .minimum_training_samples = 9,
        .minimum_validation_samples = 8,
        .maximum_training_mismatch_per_10k = 0,
        .maximum_validation_mismatch_per_10k = 0,
    };

    try std.testing.expectError(
        error.Rejected,
        artifact.create(.memory, valid_corpus, &samples, scratch, rejecting_policy),
    );
}

fn allZero(value: *const [32]u8) bool {
    for (value) |byte| {
        if (byte != 0) return false;
    }
    return true;
}
