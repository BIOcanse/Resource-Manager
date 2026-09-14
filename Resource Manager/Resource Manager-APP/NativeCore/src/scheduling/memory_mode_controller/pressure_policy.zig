const std = @import("std");

pub const Intensity = enum(u8) {
    unrestricted,
    normal,
    optimize,
    strongest,
};

pub const Thresholds = struct {
    ratio_units_maximum: u32,
    unrestricted_minimum_free_ratio_units: u32,
    normal_minimum_free_ratio_units: u32,
    strong_begin_free_ratio_units: u32,
};

pub const Plan = struct {
    baseline: Intensity,
    optimize_count: u32,
    strongest_count: u32,

    pub fn intensityAtRank(self: Plan, rank: u32) Intensity {
        if (rank < self.strongest_count) return .strongest;
        if (rank < self.strongest_count + self.optimize_count) return .optimize;
        return self.baseline;
    }
};

pub fn plan(
    free_ratio_units: u32,
    software_count: u32,
    thresholds: Thresholds,
    allow_unrestricted: bool,
) Plan {
    std.debug.assert(thresholds.ratio_units_maximum != 0);
    std.debug.assert(free_ratio_units <= thresholds.ratio_units_maximum);
    std.debug.assert(thresholds.strong_begin_free_ratio_units > 0);
    std.debug.assert(thresholds.strong_begin_free_ratio_units <
        thresholds.normal_minimum_free_ratio_units);
    std.debug.assert(thresholds.normal_minimum_free_ratio_units <
        thresholds.unrestricted_minimum_free_ratio_units);
    std.debug.assert(thresholds.unrestricted_minimum_free_ratio_units <=
        thresholds.ratio_units_maximum);

    if (software_count == 0) {
        return .{ .baseline = .normal, .optimize_count = 0, .strongest_count = 0 };
    }
    if (allow_unrestricted and free_ratio_units >=
        thresholds.unrestricted_minimum_free_ratio_units)
    {
        return .{ .baseline = .unrestricted, .optimize_count = 0, .strongest_count = 0 };
    }
    if (free_ratio_units >= thresholds.normal_minimum_free_ratio_units) {
        return .{ .baseline = .normal, .optimize_count = 0, .strongest_count = 0 };
    }
    if (free_ratio_units >= thresholds.strong_begin_free_ratio_units) {
        const width = thresholds.normal_minimum_free_ratio_units -
            thresholds.strong_begin_free_ratio_units;
        const severity = thresholds.normal_minimum_free_ratio_units -
            free_ratio_units;
        return .{
            .baseline = .normal,
            .optimize_count = ceilShare(software_count, severity, width),
            .strongest_count = 0,
        };
    }

    const strongest_severity = thresholds.strong_begin_free_ratio_units -
        free_ratio_units;
    const strongest_count = ceilShare(
        software_count,
        strongest_severity,
        thresholds.strong_begin_free_ratio_units,
    );
    return .{
        .baseline = .normal,
        .optimize_count = software_count - strongest_count,
        .strongest_count = strongest_count,
    };
}

fn ceilShare(count: u32, numerator: u32, denominator: u32) u32 {
    if (numerator == 0 or count == 0) return 0;
    if (numerator >= denominator) return count;
    const scaled = @as(u64, count) * numerator;
    const result = (scaled + denominator - 1) / denominator;
    return @min(count, @as(u32, @intCast(result)));
}

test "pressure policy expands optimize and strongest sets without a fixed score floor" {
    const thresholds = Thresholds{
        .ratio_units_maximum = 10_000,
        .unrestricted_minimum_free_ratio_units = 6_000,
        .normal_minimum_free_ratio_units = 3_000,
        .strong_begin_free_ratio_units = 1_000,
    };

    const ample = plan(7_000, 10, thresholds, true);
    try std.testing.expectEqual(Intensity.unrestricted, ample.baseline);
    try std.testing.expectEqual(@as(u32, 0), ample.optimize_count);

    const normal = plan(4_000, 10, thresholds, true);
    try std.testing.expectEqual(Intensity.normal, normal.baseline);

    const optimizing = plan(2_000, 10, thresholds, true);
    try std.testing.expectEqual(@as(u32, 5), optimizing.optimize_count);
    try std.testing.expectEqual(Intensity.optimize, optimizing.intensityAtRank(4));
    try std.testing.expectEqual(Intensity.normal, optimizing.intensityAtRank(5));

    const critical = plan(500, 10, thresholds, true);
    try std.testing.expectEqual(@as(u32, 5), critical.strongest_count);
    try std.testing.expectEqual(@as(u32, 5), critical.optimize_count);
    try std.testing.expectEqual(Intensity.strongest, critical.intensityAtRank(4));
    try std.testing.expectEqual(Intensity.optimize, critical.intensityAtRank(5));

    const empty = plan(0, 0, thresholds, true);
    try std.testing.expectEqual(@as(u32, 0), empty.strongest_count);
}

test "full pressure grid is monotonic and always protects higher score ranks last" {
    const thresholds = Thresholds{
        .ratio_units_maximum = 10_000,
        .unrestricted_minimum_free_ratio_units = 6_000,
        .normal_minimum_free_ratio_units = 3_000,
        .strong_begin_free_ratio_units = 1_000,
    };
    const software_counts = [_]u32{ 1, 2, 3, 7, 16, 64 };
    const unrestricted_options = [_]bool{ false, true };

    for (unrestricted_options) |allow_unrestricted| {
        for (software_counts) |software_count| {
            var previous_restricted_count: u32 = 0;
            var previous_strongest_count: u32 = 0;
            var step: u32 = 10_000;
            while (true) {
                const current = plan(
                    step,
                    software_count,
                    thresholds,
                    allow_unrestricted,
                );
                const restricted_count = current.optimize_count + current.strongest_count;

                try std.testing.expect(current.strongest_count <= restricted_count);
                try std.testing.expect(restricted_count <= software_count);
                try std.testing.expect(restricted_count >= previous_restricted_count);
                try std.testing.expect(current.strongest_count >= previous_strongest_count);

                var rank: u32 = 0;
                var previous_intensity: u8 = @intFromEnum(Intensity.strongest);
                while (rank < software_count) : (rank += 1) {
                    const intensity: u8 = @intFromEnum(current.intensityAtRank(rank));
                    try std.testing.expect(intensity <= previous_intensity);
                    previous_intensity = intensity;
                }

                previous_restricted_count = restricted_count;
                previous_strongest_count = current.strongest_count;
                if (step == 0) break;
                step -= 1;
            }
        }
    }
}

test "fixed point boundaries are exact" {
    const thresholds = Thresholds{
        .ratio_units_maximum = 10_000,
        .unrestricted_minimum_free_ratio_units = 6_000,
        .normal_minimum_free_ratio_units = 3_000,
        .strong_begin_free_ratio_units = 1_000,
    };
    try std.testing.expectEqual(Intensity.strongest, plan(999, 10, thresholds, true).intensityAtRank(0));
    try std.testing.expectEqual(@as(u32, 0), plan(1_000, 10, thresholds, true).strongest_count);
    try std.testing.expect(plan(2_999, 10, thresholds, true).optimize_count > 0);
    try std.testing.expectEqual(@as(u32, 0), plan(3_000, 10, thresholds, true).optimize_count);
    try std.testing.expectEqual(Intensity.normal, plan(5_999, 10, thresholds, true).baseline);
    try std.testing.expectEqual(Intensity.unrestricted, plan(6_000, 10, thresholds, true).baseline);
}
