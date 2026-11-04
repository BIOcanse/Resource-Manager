const std = @import("std");
const protocol = @import("protocol.zig");
const ResultCode = @import("../../common/result_codes.zig").ResultCode;

// One external allocation. No pointer survives Plan; completion keeps four slices.
pub const alignment: u32 = 8;
pub const Layout = struct {
    observations: usize,
    plan_handles: usize,
    requested: usize,
    observation_slots: usize,
    requested_flags: usize,
    observation_count: u32,
    plan_metric_count: u32,
    requested_count: u32,
    rule_count: u32,
    byte_count: usize,

    pub fn init(config: *const protocol.Config, rule_count: u32, metric_count: u32) !Layout {
        const observation_count = @min(config.maximum_observation_count, rule_count);
        const plan_metric_count = @min(config.maximum_plan_metric_count, metric_count);
        const requested_count = @min(config.maximum_requested_count, rule_count);
        var offset: usize = 0;
        const observations = try take(&offset, protocol.ObservationInput, observation_count);
        const plan_handles = try take(&offset, u64, plan_metric_count);
        const requested = try take(&offset, u32, requested_count);
        const observation_slots = try take(&offset, u32, rule_count);
        const requested_flags = try take(&offset, bool, rule_count);
        const byte_count = @max(alignment, try alignOffset(offset, alignment));
        return .{ .observations = observations, .plan_handles = plan_handles, .requested = requested, .observation_slots = observation_slots, .requested_flags = requested_flags, .observation_count = observation_count, .plan_metric_count = plan_metric_count, .requested_count = requested_count, .rule_count = rule_count, .byte_count = byte_count };
    }

    pub fn validate(self: Layout, bytes: []u8) ResultCode {
        if (bytes.len < self.byte_count) return .buffer_too_small;
        if (@intFromPtr(bytes.ptr) % alignment != 0) return .invalid_argument;
        _ = std.math.add(usize, @intFromPtr(bytes.ptr), self.byte_count) catch return .invalid_argument;
        return .ok;
    }

    pub fn column(self: Layout, comptime T: type, bytes: []u8, offset: usize, count: usize) []T {
        std.debug.assert(self.validate(bytes) == .ok);
        const pointer: [*]T = @ptrCast(@alignCast(bytes.ptr + offset));
        return pointer[0..count];
    }
};

fn take(offset: *usize, comptime T: type, count: usize) !usize {
    const start = try alignOffset(offset.*, @alignOf(T));
    offset.* = try std.math.add(usize, start, try std.math.mul(usize, @sizeOf(T), count));
    return start;
}

fn alignOffset(value: usize, boundary: usize) !usize {
    return (try std.math.add(usize, value, boundary - 1)) & ~(boundary - 1);
}

// All extents describe actual reads/writes, never an estimated or shadow region.
pub const Range = struct {
    start: usize,
    end: usize,

    pub fn init(pointer: anytype, byte_count: usize) ?Range {
        const start = @intFromPtr(pointer);
        return .{ .start = start, .end = std.math.add(usize, start, byte_count) catch return null };
    }

    pub fn slice(comptime T: type, values: []const T) ?Range {
        return init(values.ptr, std.math.mul(usize, values.len, @sizeOf(T)) catch return null);
    }

    pub fn overlaps(a: Range, b: Range) bool {
        return a.start != a.end and b.start != b.end and a.start < b.end and b.start < a.end;
    }
};

pub fn disjoint(ranges: []const Range) bool {
    for (ranges, 0..) |range, index| {
        for (ranges[index + 1 ..]) |other| {
            if (Range.overlaps(range, other)) return false;
        }
    }
    return true;
}
