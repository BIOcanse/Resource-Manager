const std = @import("std");
const calibration = @import("calibration.zig");
const corpus = @import("calibration_corpus.zig");

pub const schema_version: u32 = 1;
pub const encoded_length: usize = 156;
const magic = "RMCAL001";

pub const Seal = struct {
    version: u32,
    domain: calibration.Domain,
    corpus_length: u64,
    corpus_sha256: [32]u8,
    policy: calibration.AcceptancePolicy,
    result: calibration.Result,
    artifact_sha256: [32]u8,
};

pub const ArtifactError = corpus.ParseError || calibration.CalibrationError || error{
    Rejected,
    OutputTooSmall,
    InvalidArtifact,
};

/// Parses, calibrates, validates, and seals one exact corpus in a single
/// allocation-free operation. The seal is an offline artifact, not runtime ABI.
pub fn create(
    domain: calibration.Domain,
    corpus_bytes: []const u8,
    sample_output: []calibration.Sample,
    scratch: []u64,
    policy: calibration.AcceptancePolicy,
) ArtifactError!Seal {
    const samples = try corpus.parse(domain, corpus_bytes, sample_output);
    const result = try calibration.calibrate(domain, samples, scratch);
    if (!calibration.accepts(result, policy)) return error.Rejected;

    var corpus_sha256: [32]u8 = undefined;
    std.crypto.hash.sha2.Sha256.hash(corpus_bytes, &corpus_sha256, .{});
    var seal = Seal{
        .version = schema_version,
        .domain = domain,
        .corpus_length = corpus_bytes.len,
        .corpus_sha256 = corpus_sha256,
        .policy = policy,
        .result = result,
        .artifact_sha256 = undefined,
    };
    seal.artifact_sha256 = artifactDigest(seal);
    return seal;
}

pub fn encode(seal: Seal, output: []u8) ArtifactError![]const u8 {
    if (!validSeal(seal)) return error.InvalidArtifact;
    if (output.len < encoded_length) return error.OutputTooSmall;
    const bytes = output[0..encoded_length];
    @memset(bytes, 0);
    @memcpy(bytes[0..magic.len], magic);
    writeInteger(bytes, 8, u32, seal.version);
    bytes[12] = @intFromEnum(seal.domain);
    writeInteger(bytes, 16, u64, seal.corpus_length);
    @memcpy(bytes[24..56], &seal.corpus_sha256);
    writeInteger(bytes, 56, u32, seal.policy.minimum_training_samples);
    writeInteger(bytes, 60, u32, seal.policy.minimum_validation_samples);
    writeInteger(bytes, 64, u32, seal.policy.maximum_training_mismatch_per_10k);
    writeInteger(bytes, 68, u32, seal.policy.maximum_validation_mismatch_per_10k);
    writeInteger(bytes, 72, u32, seal.result.thresholds.unrestricted_minimum_free_ratio);
    writeInteger(bytes, 76, u32, seal.result.thresholds.normal_minimum_free_ratio);
    writeInteger(bytes, 80, u32, seal.result.thresholds.strong_begin_free_ratio);
    writeEvaluation(bytes, 84, seal.result.training);
    writeEvaluation(bytes, 104, seal.result.validation);
    @memcpy(bytes[124..156], &seal.artifact_sha256);
    return bytes;
}

pub fn decode(bytes: []const u8) ArtifactError!Seal {
    if (bytes.len != encoded_length or !std.mem.eql(u8, bytes[0..magic.len], magic)) {
        return error.InvalidArtifact;
    }
    if (!allZero(bytes[13..16])) return error.InvalidArtifact;
    const domain: calibration.Domain = switch (bytes[12]) {
        @intFromEnum(calibration.Domain.memory) => .memory,
        else => return error.InvalidArtifact,
    };
    const seal = Seal{
        .version = readInteger(bytes, 8, u32),
        .domain = domain,
        .corpus_length = readInteger(bytes, 16, u64),
        .corpus_sha256 = bytes[24..56].*,
        .policy = .{
            .minimum_training_samples = readInteger(bytes, 56, u32),
            .minimum_validation_samples = readInteger(bytes, 60, u32),
            .maximum_training_mismatch_per_10k = readInteger(bytes, 64, u32),
            .maximum_validation_mismatch_per_10k = readInteger(bytes, 68, u32),
        },
        .result = .{
            .domain = domain,
            .thresholds = .{
                .unrestricted_minimum_free_ratio = readInteger(bytes, 72, u32),
                .normal_minimum_free_ratio = readInteger(bytes, 76, u32),
                .strong_begin_free_ratio = readInteger(bytes, 80, u32),
            },
            .training = readEvaluation(bytes, 84),
            .validation = readEvaluation(bytes, 104),
        },
        .artifact_sha256 = bytes[124..156].*,
    };
    if (!validSeal(seal)) return error.InvalidArtifact;
    return seal;
}

fn artifactDigest(seal: Seal) [32]u8 {
    var hasher = std.crypto.hash.sha2.Sha256.init(.{});
    hasher.update("rm-memory-mode-calibration-seal-v1");
    updateInteger(&hasher, u32, seal.version);
    updateInteger(&hasher, u8, @intFromEnum(seal.domain));
    updateInteger(&hasher, u64, seal.corpus_length);
    hasher.update(&seal.corpus_sha256);
    updateInteger(&hasher, u32, seal.policy.minimum_training_samples);
    updateInteger(&hasher, u32, seal.policy.minimum_validation_samples);
    updateInteger(&hasher, u32, seal.policy.maximum_training_mismatch_per_10k);
    updateInteger(&hasher, u32, seal.policy.maximum_validation_mismatch_per_10k);
    updateInteger(&hasher, u32, seal.result.thresholds.unrestricted_minimum_free_ratio);
    updateInteger(&hasher, u32, seal.result.thresholds.normal_minimum_free_ratio);
    updateInteger(&hasher, u32, seal.result.thresholds.strong_begin_free_ratio);
    updateEvaluation(&hasher, seal.result.training);
    updateEvaluation(&hasher, seal.result.validation);
    var digest: [32]u8 = undefined;
    hasher.final(&digest);
    return digest;
}

fn validSeal(seal: Seal) bool {
    const thresholds = seal.result.thresholds;
    if (seal.version != schema_version or
        seal.result.domain != seal.domain or
        seal.corpus_length == 0 or
        seal.corpus_length > corpus.maximum_corpus_bytes or
        thresholds.strong_begin_free_ratio == 0 or
        thresholds.strong_begin_free_ratio >= thresholds.normal_minimum_free_ratio or
        thresholds.normal_minimum_free_ratio >= thresholds.unrestricted_minimum_free_ratio or
        thresholds.unrestricted_minimum_free_ratio > calibration.ratio_units_max or
        !calibration.accepts(seal.result, seal.policy))
    {
        return false;
    }
    const expected = artifactDigest(seal);
    return std.mem.eql(u8, &expected, &seal.artifact_sha256);
}

fn updateEvaluation(
    hasher: *std.crypto.hash.sha2.Sha256,
    evaluation: calibration.Evaluation,
) void {
    updateInteger(hasher, u32, evaluation.sample_count);
    updateInteger(hasher, u64, evaluation.weighted_mismatch);
    updateInteger(hasher, u64, evaluation.weighted_label_count);
}

fn updateInteger(
    hasher: *std.crypto.hash.sha2.Sha256,
    comptime T: type,
    value: T,
) void {
    var bytes: [@sizeOf(T)]u8 = undefined;
    std.mem.writeInt(T, &bytes, value, .little);
    hasher.update(&bytes);
}

fn writeEvaluation(bytes: []u8, offset: usize, evaluation: calibration.Evaluation) void {
    writeInteger(bytes, offset, u32, evaluation.sample_count);
    writeInteger(bytes, offset + 4, u64, evaluation.weighted_mismatch);
    writeInteger(bytes, offset + 12, u64, evaluation.weighted_label_count);
}

fn readEvaluation(bytes: []const u8, offset: usize) calibration.Evaluation {
    return .{
        .sample_count = readInteger(bytes, offset, u32),
        .weighted_mismatch = readInteger(bytes, offset + 4, u64),
        .weighted_label_count = readInteger(bytes, offset + 12, u64),
    };
}

fn writeInteger(bytes: []u8, offset: usize, comptime T: type, value: T) void {
    const destination: *[@sizeOf(T)]u8 = @ptrCast(bytes[offset..].ptr);
    std.mem.writeInt(T, destination, value, .little);
}

fn readInteger(bytes: []const u8, offset: usize, comptime T: type) T {
    const source: *const [@sizeOf(T)]u8 = @ptrCast(bytes[offset..].ptr);
    return std.mem.readInt(T, source, .little);
}

fn allZero(bytes: []const u8) bool {
    for (bytes) |byte| {
        if (byte != 0) return false;
    }
    return true;
}
