const std = @import("std");
const calibration = @import("calibration.zig");
const observation_format = @import("calibration_observations.zig");

pub const Options = struct {
    domain: calibration.Domain,
    input_path: []const u8,
    output_path: []const u8,
    split_policy: observation_format.SplitPolicy,
};

pub const ArgumentError = error{
    InvalidArgumentCount,
    InvalidDomain,
    InvalidInteger,
    NonCanonicalInteger,
    InvalidSplitPolicy,
    InputEqualsOutput,
};

pub fn main(init: std.process.Init) !void {
    run(init) catch |err| {
        std.log.err(
            "calibration corpus assembly failed: {s}; expected: <memory> <observations.tsv> <corpus.tsv> <validation-percent> <split-seed>",
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

    const input = try cwd.readFileAlloc(
        io,
        options.input_path,
        arena,
        .limited(observation_format.maximum_input_bytes + 1),
    );
    const maximum_observations = maximumPossibleObservations(input);
    const observation_storage = try arena.alloc(
        observation_format.Observation,
        maximum_observations,
    );
    const parsed = try observation_format.parse(options.domain, input, observation_storage);
    const sample_storage = try arena.alloc(calibration.Sample, parsed.len);
    const assembly = try observation_format.assemble(
        options.domain,
        parsed,
        options.split_policy,
        sample_storage,
    );

    var rendered: std.Io.Writer.Allocating = try .initCapacity(arena, input.len);
    try observation_format.renderCanonical(options.domain, assembly.samples, &rendered.writer);
    const output_bytes = rendered.written();
    var source_sha256: [32]u8 = undefined;
    var corpus_sha256: [32]u8 = undefined;
    std.crypto.hash.sha2.Sha256.hash(input, &source_sha256, .{});
    std.crypto.hash.sha2.Sha256.hash(output_bytes, &corpus_sha256, .{});

    var output = try cwd.createFileAtomic(io, options.output_path, .{});
    defer output.deinit(io);
    try output.file.writeStreamingAll(io, output_bytes);
    try output.file.sync(io);
    try output.link(io);

    const source_hex = std.fmt.bytesToHex(source_sha256, .upper);
    const corpus_hex = std.fmt.bytesToHex(corpus_sha256, .upper);
    var stdout_buffer: [1024]u8 = undefined;
    var stdout_file_writer = std.Io.File.stdout().writer(io, &stdout_buffer);
    const stdout = &stdout_file_writer.interface;
    try stdout.print(
        "domain={s} observations={d} training_observations={d} validation_observations={d} training_cases={d} validation_cases={d} validation_percent={d} split_seed={d} source_sha256={s} corpus_sha256={s} bytes={d}\n",
        .{
            @tagName(options.domain),
            assembly.stats.observation_count,
            assembly.stats.training_observation_count,
            assembly.stats.validation_observation_count,
            assembly.stats.training_case_count,
            assembly.stats.validation_case_count,
            options.split_policy.validation_percent,
            options.split_policy.seed,
            &source_hex,
            &corpus_hex,
            output_bytes.len,
        },
    );
    try stdout.flush();
}

pub fn parseArguments(args: []const []const u8) ArgumentError!Options {
    if (args.len != 6) return error.InvalidArgumentCount;
    const domain: calibration.Domain = if (std.mem.eql(u8, args[1], "memory"))
        .memory
    else
        return error.InvalidDomain;
    if (std.mem.eql(u8, args[2], args[3])) return error.InputEqualsOutput;
    const validation_percent = try parseCanonicalUnsigned(u32, args[4]);
    if (validation_percent == 0 or validation_percent >= 100) {
        return error.InvalidSplitPolicy;
    }
    return .{
        .domain = domain,
        .input_path = args[2],
        .output_path = args[3],
        .split_policy = .{
            .seed = try parseCanonicalUnsigned(u64, args[5]),
            .validation_percent = validation_percent,
        },
    };
}

fn parseCanonicalUnsigned(comptime T: type, value: []const u8) ArgumentError!T {
    if (value.len == 0) return error.InvalidInteger;
    if (value.len > 1 and value[0] == '0') return error.NonCanonicalInteger;
    for (value) |character| {
        if (character < '0' or character > '9') return error.InvalidInteger;
    }
    return std.fmt.parseUnsigned(T, value, 10) catch error.InvalidInteger;
}

fn maximumPossibleObservations(bytes: []const u8) usize {
    var newline_count: usize = 0;
    for (bytes) |byte| {
        if (byte == '\n') newline_count += 1;
    }
    return @min(
        newline_count -| 1,
        bytes.len / observation_format.minimum_canonical_row_bytes,
    );
}

test "assembly CLI parses strict reproducible split arguments" {
    const args = [_][]const u8{
        "assemble-calibration-corpus",
        "memory",
        "observations.tsv",
        "corpus.tsv",
        "20",
        "7",
    };
    const options = try parseArguments(&args);
    try std.testing.expectEqual(calibration.Domain.memory, options.domain);
    try std.testing.expectEqual(@as(u32, 20), options.split_policy.validation_percent);
    try std.testing.expectEqual(@as(u64, 7), options.split_policy.seed);
}

test "assembly CLI rejects ambiguous split arguments" {
    const invalid_percent = [_][]const u8{
        "tool", "memory", "in.tsv", "out.tsv", "100", "7",
    };
    try std.testing.expectError(error.InvalidSplitPolicy, parseArguments(&invalid_percent));
    const leading_zero = [_][]const u8{
        "tool", "memory", "in.tsv", "out.tsv", "20", "07",
    };
    try std.testing.expectError(error.NonCanonicalInteger, parseArguments(&leading_zero));
}
