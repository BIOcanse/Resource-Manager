const std = @import("std");
const calibration = @import("calibration.zig");
const corpus = @import("calibration_corpus.zig");

const valid_memory_corpus = corpus.schema_header ++ "\n" ++
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

test "canonical corpus parses without allocation and calibrates reproducibly" {
    var samples: [16]calibration.Sample = undefined;
    const parsed = try corpus.parse(.memory, valid_memory_corpus, &samples);
    const scratch = try std.testing.allocator.alloc(u64, calibration.required_scratch_words);
    defer std.testing.allocator.free(scratch);

    const first = try calibration.calibrate(.memory, parsed, scratch);
    const second = try calibration.calibrate(.memory, parsed, scratch);

    try std.testing.expectEqual(@as(usize, 16), parsed.len);
    try std.testing.expectEqual(first, second);
    try std.testing.expectEqual(@as(u32, 1000), first.thresholds.strong_begin_free_ratio);
    try std.testing.expectEqual(@as(u32, 3000), first.thresholds.normal_minimum_free_ratio);
    try std.testing.expectEqual(@as(u32, 6000), first.thresholds.unrestricted_minimum_free_ratio);
}

test "canonical corpus rejects ambiguous encodings and unknown domains" {
    var output: [2]calibration.Sample = undefined;
    const crlf = corpus.schema_header ++ "\r\n1\tmemory\ttraining\t0\t1\t0\t0\t1\r\n";
    try std.testing.expectError(error.InvalidEncoding, corpus.parse(.memory, crlf, &output));

    const no_final_newline = corpus.schema_header ++ "\n1\tmemory\ttraining\t0\t1\t0\t0\t1";
    try std.testing.expectError(error.MissingFinalNewline, corpus.parse(.memory, no_final_newline, &output));

    const leading_zero = corpus.schema_header ++ "\n01\tmemory\ttraining\t0\t1\t0\t0\t1\n";
    try std.testing.expectError(error.NonCanonicalInteger, corpus.parse(.memory, leading_zero, &output));

    const unknown = corpus.schema_header ++ "\n1\tvram\ttraining\t0\t1\t0\t0\t1\n";
    try std.testing.expectError(error.InvalidDomain, corpus.parse(.memory, unknown, &output));

    const duplicate = corpus.schema_header ++ "\n1\tmemory\ttraining\t0\t1\t0\t0\t1\n" ++
        "1\tmemory\tvalidation\t10000\t1\t1\t1\t0\n";
    try std.testing.expectError(error.NonCanonicalSamples, corpus.parse(.memory, duplicate, &output));

    const too_small = corpus.schema_header ++ "\n1\tmemory\ttraining\t0\t1\t0\t0\t1\n" ++
        "2\tmemory\tvalidation\t10000\t1\t1\t1\t0\n";
    try std.testing.expectError(error.OutputTooSmall, corpus.parse(.memory, too_small, output[0..1]));
}
