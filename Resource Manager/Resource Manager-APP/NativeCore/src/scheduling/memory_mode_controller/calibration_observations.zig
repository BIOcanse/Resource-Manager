const std = @import("std");
const calibration = @import("calibration.zig");
const corpus = @import("calibration_corpus.zig");

pub const schema_header =
    "case_id\tobservation_id\tdomain\tfree_ratio_units\tweight\toutcome";
pub const maximum_input_bytes: usize = corpus.maximum_corpus_bytes;
pub const maximum_row_bytes: usize = 256;
pub const minimum_canonical_row_bytes: usize = 30;

pub const Outcome = enum(u8) {
    strongest_required,
    optimize_required,
    normal_sufficient,
    unrestricted_safe,
};

pub const Observation = struct {
    case_id: u64,
    observation_id: u64,
    domain: calibration.Domain,
    free_ratio_units: u32,
    weight: u32,
    outcome: Outcome,
};

pub const SplitPolicy = struct {
    seed: u64,
    validation_percent: u32,
};

pub const AssemblyStats = struct {
    observation_count: u32,
    training_observation_count: u32,
    validation_observation_count: u32,
    training_case_count: u32,
    validation_case_count: u32,
};

pub const Assembly = struct {
    samples: []calibration.Sample,
    stats: AssemblyStats,
};

pub const ParseError = error{
    EmptyInput,
    InputTooLarge,
    InvalidEncoding,
    InvalidHeader,
    MissingFinalNewline,
    EmptyRow,
    RowTooLong,
    InvalidColumnCount,
    InvalidInteger,
    NonCanonicalInteger,
    InvalidDomain,
    MixedDomain,
    InvalidOutcome,
    InvalidObservation,
    NonCanonicalObservations,
    OutputTooSmall,
};

pub const AssemblyError = error{
    EmptyObservations,
    InvalidSplitPolicy,
    MixedDomain,
    InvalidObservation,
    NonCanonicalObservations,
    OutputTooSmall,
    TooManyObservations,
    IncompleteTrainingOutcomes,
    IncompleteValidationOutcomes,
};

pub const RenderError = std.Io.Writer.Error || error{
    EmptyObservations,
    MixedDomain,
    InvalidSample,
    NonCanonicalSamples,
    InconsistentLabels,
};

/// Parses the low-marking-cost observation format. One ordinal outcome
/// replaces three booleans, making inconsistent labels unrepresentable.
pub fn parse(
    domain: calibration.Domain,
    bytes: []const u8,
    output: []Observation,
) ParseError![]Observation {
    if (bytes.len == 0) return error.EmptyInput;
    if (bytes.len > maximum_input_bytes) return error.InputTooLarge;
    if (std.mem.indexOfScalar(u8, bytes, '\r') != null or
        std.mem.indexOfScalar(u8, bytes, 0) != null)
    {
        return error.InvalidEncoding;
    }
    if (bytes[bytes.len - 1] != '\n') return error.MissingFinalNewline;

    var lines = std.mem.splitScalar(u8, bytes, '\n');
    const header = lines.next() orelse return error.EmptyInput;
    if (!std.mem.eql(u8, header, schema_header)) return error.InvalidHeader;

    var count: usize = 0;
    var previous_case_id: u64 = 0;
    var previous_observation_id: u64 = 0;
    while (lines.next()) |row| {
        if (row.len == 0 and lines.peek() == null) break;
        if (row.len == 0) return error.EmptyRow;
        if (row.len > maximum_row_bytes) return error.RowTooLong;
        if (count == output.len) return error.OutputTooSmall;

        var fields = std.mem.splitScalar(u8, row, '\t');
        var columns: [6][]const u8 = undefined;
        for (&columns) |*column| {
            column.* = fields.next() orelse return error.InvalidColumnCount;
        }
        if (fields.next() != null) return error.InvalidColumnCount;

        const case_id = try parseCanonicalUnsigned(u64, columns[0]);
        const observation_id = try parseCanonicalUnsigned(u64, columns[1]);
        if (case_id == 0 or observation_id == 0 or
            case_id < previous_case_id or
            (case_id == previous_case_id and observation_id <= previous_observation_id))
        {
            return error.NonCanonicalObservations;
        }
        previous_observation_id = if (case_id == previous_case_id) observation_id else observation_id;
        previous_case_id = case_id;

        const row_domain = try parseDomain(columns[2]);
        if (row_domain != domain) return error.MixedDomain;
        const free_ratio_units = try parseCanonicalUnsigned(u32, columns[3]);
        const weight = try parseCanonicalUnsigned(u32, columns[4]);
        if (free_ratio_units > calibration.ratio_units_max or weight == 0) {
            return error.InvalidObservation;
        }
        output[count] = .{
            .case_id = case_id,
            .observation_id = observation_id,
            .domain = row_domain,
            .free_ratio_units = free_ratio_units,
            .weight = weight,
            .outcome = try parseOutcome(columns[5]),
        };
        count += 1;
    }
    if (count == 0) return error.EmptyInput;
    return output[0..count];
}

/// Assigns whole workload cases to one partition using a fixed SplitMix64
/// mapping. Observations from one case can never leak across train/validation.
pub fn assemble(
    domain: calibration.Domain,
    observations: []const Observation,
    split_policy: SplitPolicy,
    sample_output: []calibration.Sample,
) AssemblyError!Assembly {
    if (observations.len == 0) return error.EmptyObservations;
    if (split_policy.validation_percent == 0 or split_policy.validation_percent >= 100) {
        return error.InvalidSplitPolicy;
    }
    if (sample_output.len < observations.len) return error.OutputTooSmall;
    if (observations.len > std.math.maxInt(u32)) return error.TooManyObservations;

    var stats = AssemblyStats{
        .observation_count = @intCast(observations.len),
        .training_observation_count = 0,
        .validation_observation_count = 0,
        .training_case_count = 0,
        .validation_case_count = 0,
    };
    var training_outcomes: u8 = 0;
    var validation_outcomes: u8 = 0;
    var current_case_id: u64 = 0;
    var previous_case_id: u64 = 0;
    var previous_observation_id: u64 = 0;
    var current_partition: calibration.Partition = undefined;

    for (observations, 0..) |observation, index| {
        if (observation.domain != domain) return error.MixedDomain;
        if (observation.case_id == 0 or observation.observation_id == 0 or
            observation.case_id < previous_case_id or
            (observation.case_id == previous_case_id and
                observation.observation_id <= previous_observation_id))
        {
            return error.NonCanonicalObservations;
        }
        if (observation.free_ratio_units > calibration.ratio_units_max or
            observation.weight == 0)
        {
            return error.InvalidObservation;
        }
        previous_case_id = observation.case_id;
        previous_observation_id = observation.observation_id;
        if (observation.case_id != current_case_id) {
            current_case_id = observation.case_id;
            current_partition = if (isValidationCase(
                split_policy.seed,
                current_case_id,
                split_policy.validation_percent,
            )) .validation else .training;
            switch (current_partition) {
                .training => stats.training_case_count += 1,
                .validation => stats.validation_case_count += 1,
            }
        }
        switch (current_partition) {
            .training => {
                stats.training_observation_count += 1;
                training_outcomes |= outcomeBit(observation.outcome);
            },
            .validation => {
                stats.validation_observation_count += 1;
                validation_outcomes |= outcomeBit(observation.outcome);
            },
        }
        sample_output[index] = sampleFromObservation(
            @as(u64, @intCast(index)) + 1,
            current_partition,
            observation,
        );
    }
    const all_outcomes: u8 = 0b1111;
    if (training_outcomes != all_outcomes) return error.IncompleteTrainingOutcomes;
    if (validation_outcomes != all_outcomes) return error.IncompleteValidationOutcomes;
    return .{ .samples = sample_output[0..observations.len], .stats = stats };
}

pub fn renderCanonical(
    domain: calibration.Domain,
    samples: []const calibration.Sample,
    writer: *std.Io.Writer,
) RenderError!void {
    if (samples.len == 0) return error.EmptyObservations;
    try writer.print("{s}\n", .{corpus.schema_header});
    var previous_id: u64 = 0;
    for (samples) |sample| {
        if (sample.domain != domain) return error.MixedDomain;
        if (sample.sample_id == 0 or sample.sample_id <= previous_id) {
            return error.NonCanonicalSamples;
        }
        if (sample.free_ratio_units > calibration.ratio_units_max or sample.weight == 0) {
            return error.InvalidSample;
        }
        if ((sample.unrestricted_safe and !sample.normal_sufficient) or
            (sample.normal_sufficient and sample.strongest_required))
        {
            return error.InconsistentLabels;
        }
        previous_id = sample.sample_id;
        try writer.print(
            "{d}\t{s}\t{s}\t{d}\t{d}\t{d}\t{d}\t{d}\n",
            .{
                sample.sample_id,
                @tagName(sample.domain),
                @tagName(sample.partition),
                sample.free_ratio_units,
                sample.weight,
                @intFromBool(sample.unrestricted_safe),
                @intFromBool(sample.normal_sufficient),
                @intFromBool(sample.strongest_required),
            },
        );
    }
}

pub fn isValidationCase(seed: u64, case_id: u64, validation_percent: u32) bool {
    if (validation_percent == 0) return false;
    if (validation_percent >= 100) return true;
    return splitMix64(seed ^ case_id) % 100 < validation_percent;
}

fn splitMix64(value: u64) u64 {
    var mixed = value +% 0x9E3779B97F4A7C15;
    mixed = (mixed ^ (mixed >> 30)) *% 0xBF58476D1CE4E5B9;
    mixed = (mixed ^ (mixed >> 27)) *% 0x94D049BB133111EB;
    return mixed ^ (mixed >> 31);
}

fn sampleFromObservation(
    sample_id: u64,
    partition: calibration.Partition,
    observation: Observation,
) calibration.Sample {
    const labels = switch (observation.outcome) {
        .strongest_required => .{ false, false, true },
        .optimize_required => .{ false, false, false },
        .normal_sufficient => .{ false, true, false },
        .unrestricted_safe => .{ true, true, false },
    };
    return .{
        .sample_id = sample_id,
        .domain = observation.domain,
        .partition = partition,
        .free_ratio_units = observation.free_ratio_units,
        .weight = observation.weight,
        .unrestricted_safe = labels[0],
        .normal_sufficient = labels[1],
        .strongest_required = labels[2],
    };
}

fn outcomeBit(outcome: Outcome) u8 {
    return @as(u8, 1) << @as(u3, @intCast(@intFromEnum(outcome)));
}

fn parseCanonicalUnsigned(comptime T: type, value: []const u8) ParseError!T {
    if (value.len == 0) return error.InvalidInteger;
    if (value.len > 1 and value[0] == '0') return error.NonCanonicalInteger;
    for (value) |character| {
        if (character < '0' or character > '9') return error.InvalidInteger;
    }
    return std.fmt.parseUnsigned(T, value, 10) catch error.InvalidInteger;
}

fn parseDomain(value: []const u8) ParseError!calibration.Domain {
    if (std.mem.eql(u8, value, "memory")) return .memory;
    return error.InvalidDomain;
}

fn parseOutcome(value: []const u8) ParseError!Outcome {
    inline for (std.meta.fields(Outcome)) |field| {
        if (std.mem.eql(u8, value, field.name)) return @enumFromInt(field.value);
    }
    return error.InvalidOutcome;
}
