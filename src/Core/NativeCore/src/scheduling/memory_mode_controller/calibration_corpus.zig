const std = @import("std");
const calibration = @import("calibration.zig");

pub const schema_header =
    "sample_id\tdomain\tpartition\tfree_ratio_units\tweight\tunrestricted_safe\tnormal_sufficient\tstrongest_required";
pub const maximum_corpus_bytes: usize = 8 * 1024 * 1024;
pub const maximum_row_bytes: usize = 256;

pub const ParseError = error{
    EmptyCorpus,
    CorpusTooLarge,
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
    InvalidPartition,
    InvalidBoolean,
    InvalidSample,
    NonCanonicalSamples,
    OutputTooSmall,
};

/// Parses the canonical calibration corpus without allocation. The returned
/// slice aliases `output`; callers can feed it directly to `calibrate`.
pub fn parse(
    domain: calibration.Domain,
    bytes: []const u8,
    output: []calibration.Sample,
) ParseError![]calibration.Sample {
    if (bytes.len == 0) return error.EmptyCorpus;
    if (bytes.len > maximum_corpus_bytes) return error.CorpusTooLarge;
    if (std.mem.indexOfScalar(u8, bytes, '\r') != null or
        std.mem.indexOfScalar(u8, bytes, 0) != null)
    {
        return error.InvalidEncoding;
    }
    if (bytes[bytes.len - 1] != '\n') return error.MissingFinalNewline;

    var lines = std.mem.splitScalar(u8, bytes, '\n');
    const header = lines.next() orelse return error.EmptyCorpus;
    if (!std.mem.eql(u8, header, schema_header)) return error.InvalidHeader;

    var count: usize = 0;
    var previous_id: u64 = 0;
    while (lines.next()) |row| {
        // The required final newline produces one trailing empty split item.
        if (row.len == 0 and lines.peek() == null) break;
        if (row.len == 0) return error.EmptyRow;
        if (row.len > maximum_row_bytes) return error.RowTooLong;
        if (count == output.len) return error.OutputTooSmall;

        var fields = std.mem.splitScalar(u8, row, '\t');
        var columns: [8][]const u8 = undefined;
        for (&columns) |*column| {
            column.* = fields.next() orelse return error.InvalidColumnCount;
        }
        if (fields.next() != null) return error.InvalidColumnCount;

        const sample_id = try parseCanonicalUnsigned(u64, columns[0]);
        if (sample_id == 0 or sample_id <= previous_id) {
            return error.NonCanonicalSamples;
        }
        previous_id = sample_id;

        const row_domain = try parseDomain(columns[1]);
        if (row_domain != domain) return error.MixedDomain;
        const free_ratio_units = try parseCanonicalUnsigned(u32, columns[3]);
        const weight = try parseCanonicalUnsigned(u32, columns[4]);
        if (free_ratio_units > calibration.ratio_units_max or weight == 0) {
            return error.InvalidSample;
        }

        output[count] = .{
            .sample_id = sample_id,
            .domain = row_domain,
            .partition = try parsePartition(columns[2]),
            .free_ratio_units = free_ratio_units,
            .weight = weight,
            .unrestricted_safe = try parseBoolean(columns[5]),
            .normal_sufficient = try parseBoolean(columns[6]),
            .strongest_required = try parseBoolean(columns[7]),
        };
        count += 1;
    }
    if (count == 0) return error.EmptyCorpus;
    return output[0..count];
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

fn parsePartition(value: []const u8) ParseError!calibration.Partition {
    if (std.mem.eql(u8, value, "training")) return .training;
    if (std.mem.eql(u8, value, "validation")) return .validation;
    return error.InvalidPartition;
}

fn parseBoolean(value: []const u8) ParseError!bool {
    if (std.mem.eql(u8, value, "0")) return false;
    if (std.mem.eql(u8, value, "1")) return true;
    return error.InvalidBoolean;
}
