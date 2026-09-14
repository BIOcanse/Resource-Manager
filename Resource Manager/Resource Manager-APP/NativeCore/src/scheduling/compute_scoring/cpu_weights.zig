const std = @import("std");
const protocol = @import("protocol.zig");

pub fn compile(allocator: std.mem.Allocator, raw: []const f64, count: u32) ![]f64 {
    if (raw.len != count) return error.InvalidConfiguration;
    var maximum: f64 = 0;
    for (raw) |weight| {
        if (!protocol.finitePositive(weight)) return error.InvalidConfiguration;
        maximum = @max(maximum, weight);
    }
    const shares = try allocator.alloc(f64, raw.len);
    errdefer allocator.free(shares);
    var total: f64 = 0;
    for (raw, shares) |weight, *share| {
        share.* = weight / maximum;
        total += share.*;
    }
    for (shares) |*share| {
        share.* /= total;
    }
    return shares;
}

pub fn calculate(shares: []const f64, rows: []const protocol.CpuCoreInput, output: []f64) protocol.Status {
    if (shares.len == 0) return .no_data;
    var previous: ?protocol.CpuCoreInput = null;
    for (rows) |row| {
        if (row.process_index >= output.len or row.core_index >= shares.len
            or !protocol.validPercent(row.usage_percent)) return .invalid_facts;
        if (previous) |old| {
            if (row.process_index < old.process_index or
                (row.process_index == old.process_index and row.core_index <= old.core_index))
                return .conflicting_facts;
        }
        previous = row;
    }
    @memset(output, 0);
    for (rows) |row| output[row.process_index] += shares[row.core_index] * row.usage_percent;
    // Exact convex sums are <= 100; remove only floating-point overshoot at the endpoint.
    for (output) |*value| value.* = @min(value.*, 100);
    return .ok;
}
