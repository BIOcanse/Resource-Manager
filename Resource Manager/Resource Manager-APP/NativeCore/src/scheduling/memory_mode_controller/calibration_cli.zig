const std = @import("std");
const calibration = @import("calibration.zig");
const corpus = @import("calibration_corpus.zig");
const artifact = @import("calibration_artifact.zig");

// The shortest valid canonical row is 25 bytes including its final LF.
// This keeps malformed newline-dense input from amplifying an 8 MiB file into
// an unnecessarily large Sample allocation before the strict parser rejects it.
const minimum_canonical_row_bytes: usize = 25;

pub const Options = struct {
    domain: calibration.Domain,
    input_path: []const u8,
    output_path: []const u8,
    policy: calibration.AcceptancePolicy,
};

pub const ArgumentError = error{
    InvalidArgumentCount,
    InvalidDomain,
    InvalidInteger,
    NonCanonicalInteger,
    InvalidPolicy,
    InputEqualsOutput,
};

pub fn main(init: std.process.Init) !void {
    run(init) catch |err| {
        std.log.err(
            "memory-mode calibration failed: {s}; expected: <memory> <input.tsv> <output.bin> <min-training> <min-validation> <max-training-mismatch-per-10k> <max-validation-mismatch-per-10k>",
            .{@errorName(err)},
        );
        std.process.exit(2);
    };
}

fn run(init: std.process.Init) !void {
    const arena = init.arena.allocator();
    const args = try init.minimal.args.toSlice(arena);
    const options = try parseArguments(args);
    const io = init.io;
    const cwd = std.Io.Dir.cwd();

    const corpus_bytes = try cwd.readFileAlloc(
        io,
        options.input_path,
        arena,
        .limited(corpus.maximum_corpus_bytes + 1),
    );
    const samples = try arena.alloc(calibration.Sample, maximumPossibleSamples(corpus_bytes));
    const scratch = try arena.alloc(u64, calibration.required_scratch_words);
    const seal = try artifact.create(
        options.domain,
        corpus_bytes,
        samples,
        scratch,
        options.policy,
    );

    var encoded_storage: [artifact.encoded_length]u8 = undefined;
    const encoded = try artifact.encode(seal, &encoded_storage);
    var encoded_sha256: [32]u8 = undefined;
    std.crypto.hash.sha2.Sha256.hash(encoded, &encoded_sha256, .{});

    var output = try cwd.createFileAtomic(io, options.output_path, .{});
    defer output.deinit(io);
    try output.file.writeStreamingAll(io, encoded);
    try output.file.sync(io);
    try output.link(io);

    const corpus_hex = std.fmt.bytesToHex(seal.corpus_sha256, .upper);
    const seal_hex = std.fmt.bytesToHex(seal.artifact_sha256, .upper);
    const encoded_hex = std.fmt.bytesToHex(encoded_sha256, .upper);
    var stdout_buffer: [1024]u8 = undefined;
    var stdout_file_writer = std.Io.File.stdout().writer(io, &stdout_buffer);
    const stdout = &stdout_file_writer.interface;
    try stdout.print(
        "domain={s} samples={d} thresholds={d},{d},{d} training_mismatch={d}/{d} validation_mismatch={d}/{d} corpus_sha256={s} seal_sha256={s} encoded_sha256={s} bytes={d}\n",
        .{
            @tagName(seal.domain),
            seal.result.training.sample_count + seal.result.validation.sample_count,
            seal.result.thresholds.strong_begin_free_ratio,
            seal.result.thresholds.normal_minimum_free_ratio,
            seal.result.thresholds.unrestricted_minimum_free_ratio,
            seal.result.training.weighted_mismatch,
            seal.result.training.weighted_label_count,
            seal.result.validation.weighted_mismatch,
            seal.result.validation.weighted_label_count,
            &corpus_hex,
            &seal_hex,
            &encoded_hex,
            encoded.len,
        },
    );
    try stdout.flush();
}

pub fn parseArguments(args: []const []const u8) ArgumentError!Options {
    if (args.len != 8) return error.InvalidArgumentCount;
    const domain: calibration.Domain = if (std.mem.eql(u8, args[1], "memory"))
        .memory
    else
        return error.InvalidDomain;
    if (std.mem.eql(u8, args[2], args[3])) return error.InputEqualsOutput;

    const policy = calibration.AcceptancePolicy{
        .minimum_training_samples = try parseCanonicalU32(args[4]),
        .minimum_validation_samples = try parseCanonicalU32(args[5]),
        .maximum_training_mismatch_per_10k = try parseCanonicalU32(args[6]),
        .maximum_validation_mismatch_per_10k = try parseCanonicalU32(args[7]),
    };
    if (policy.minimum_training_samples == 0 or
        policy.minimum_validation_samples == 0 or
        policy.maximum_training_mismatch_per_10k > 10_000 or
        policy.maximum_validation_mismatch_per_10k > 10_000)
    {
        return error.InvalidPolicy;
    }
    return .{
        .domain = domain,
        .input_path = args[2],
        .output_path = args[3],
        .policy = policy,
    };
}

fn parseCanonicalU32(value: []const u8) ArgumentError!u32 {
    if (value.len == 0) return error.InvalidInteger;
    if (value.len > 1 and value[0] == '0') return error.NonCanonicalInteger;
    for (value) |character| {
        if (character < '0' or character > '9') return error.InvalidInteger;
    }
    return std.fmt.parseUnsigned(u32, value, 10) catch error.InvalidInteger;
}

fn maximumPossibleSamples(bytes: []const u8) usize {
    var newline_count: usize = 0;
    for (bytes) |byte| {
        if (byte == '\n') newline_count += 1;
    }
    return @min(newline_count -| 1, bytes.len / minimum_canonical_row_bytes);
}

test "calibration CLI parses a strict deterministic policy" {
    const args = [_][]const u8{
        "memory-mode-calibrate",
        "memory",
        "corpus.tsv",
        "seal.bin",
        "128",
        "64",
        "250",
        "500",
    };
    const options = try parseArguments(&args);
    try std.testing.expectEqual(calibration.Domain.memory, options.domain);
    try std.testing.expectEqual(@as(u32, 128), options.policy.minimum_training_samples);
    try std.testing.expectEqual(@as(u32, 500), options.policy.maximum_validation_mismatch_per_10k);
}

test "calibration CLI rejects ambiguous and unsafe arguments" {
    const leading_zero = [_][]const u8{
        "tool", "memory", "in.tsv", "out.bin", "01", "1", "0", "0",
    };
    try std.testing.expectError(error.NonCanonicalInteger, parseArguments(&leading_zero));

    const overwrite_input = [_][]const u8{
        "tool", "memory", "same", "same", "1", "1", "0", "0",
    };
    try std.testing.expectError(error.InputEqualsOutput, parseArguments(&overwrite_input));

    const invalid_rate = [_][]const u8{
        "tool", "memory", "in.tsv", "out.bin", "1", "1", "10001", "0",
    };
    try std.testing.expectError(error.InvalidPolicy, parseArguments(&invalid_rate));
}

test "calibration CLI bounds malformed newline-dense sample allocation" {
    try std.testing.expectEqual(@as(usize, 0), maximumPossibleSamples("\n" ** 24));
    try std.testing.expectEqual(@as(usize, 1), maximumPossibleSamples("\n" ** 25));
}
