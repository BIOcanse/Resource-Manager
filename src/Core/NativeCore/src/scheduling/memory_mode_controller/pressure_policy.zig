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

pub const EligibleCounts = struct {
    optimize: u32 = 0,
    strongest: u32 = 0,
};

pub fn plan(
    free_ratio_units: u32,
    eligible: EligibleCounts,
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

    std.debug.assert(eligible.strongest <= eligible.optimize);
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
            .optimize_count = ceilShare(eligible.optimize, severity, width),
            .strongest_count = 0,
        };
    }

    const strongest_severity = thresholds.strong_begin_free_ratio_units -
        free_ratio_units;
    const strongest_count = ceilShare(
        eligible.strongest,
        strongest_severity,
        thresholds.strong_begin_free_ratio_units,
    );
    return .{
        .baseline = .normal,
        .optimize_count = eligible.optimize - strongest_count,
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

    const ample = plan(7_000, allEligible(10), thresholds, true);
    try std.testing.expectEqual(Intensity.unrestricted, ample.baseline);
    try std.testing.expectEqual(@as(u32, 0), ample.optimize_count);

    const normal = plan(4_000, allEligible(10), thresholds, true);
    try std.testing.expectEqual(Intensity.normal, normal.baseline);

    const optimizing = plan(2_000, allEligible(10), thresholds, true);
    try std.testing.expectEqual(@as(u32, 5), optimizing.optimize_count);
    try std.testing.expectEqual(Intensity.optimize, optimizing.intensityAtRank(4));
    try std.testing.expectEqual(Intensity.normal, optimizing.intensityAtRank(5));

    const critical = plan(500, allEligible(10), thresholds, true);
    try std.testing.expectEqual(@as(u32, 5), critical.strongest_count);
    try std.testing.expectEqual(@as(u32, 5), critical.optimize_count);
    try std.testing.expectEqual(Intensity.strongest, critical.intensityAtRank(4));
    try std.testing.expectEqual(Intensity.optimize, critical.intensityAtRank(5));

    const empty = plan(0, allEligible(0), thresholds, true);
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
                    allEligible(software_count),
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
    try std.testing.expectEqual(Intensity.strongest, plan(999, allEligible(10), thresholds, true).intensityAtRank(0));
    try std.testing.expectEqual(@as(u32, 0), plan(1_000, allEligible(10), thresholds, true).strongest_count);
    try std.testing.expect(plan(2_999, allEligible(10), thresholds, true).optimize_count > 0);
    try std.testing.expectEqual(@as(u32, 0), plan(3_000, allEligible(10), thresholds, true).optimize_count);
    try std.testing.expectEqual(Intensity.normal, plan(5_999, allEligible(10), thresholds, true).baseline);
    try std.testing.expectEqual(Intensity.unrestricted, plan(6_000, allEligible(10), thresholds, true).baseline);
}

test "each pressure quota uses only its eligible population" {
    const thresholds = Thresholds{
        .ratio_units_maximum = 10_000,
        .unrestricted_minimum_free_ratio_units = 6_000,
        .normal_minimum_free_ratio_units = 3_000,
        .strong_begin_free_ratio_units = 1_000,
    };
    const eligible = EligibleCounts{ .optimize = 59, .strongest = 20 };
    const moderate = plan(2_380, eligible, thresholds, false);
    try std.testing.expectEqual(@as(u32, 19), moderate.optimize_count);
    try std.testing.expectEqual(@as(u32, 0), moderate.strongest_count);
    const critical = plan(500, eligible, thresholds, false);
    try std.testing.expectEqual(@as(u32, 10), critical.strongest_count);
    try std.testing.expectEqual(@as(u32, 49), critical.optimize_count);
    const full = plan(0, eligible, thresholds, false);
    try std.testing.expectEqual(@as(u32, 20), full.strongest_count);
    try std.testing.expectEqual(@as(u32, 39), full.optimize_count);
    const middle_only = plan(0, .{ .optimize = 59 }, thresholds, false);
    try std.testing.expectEqual(@as(u32, 0), middle_only.strongest_count);
    try std.testing.expectEqual(@as(u32, 59), middle_only.optimize_count);
    const protected_only = plan(0, .{}, thresholds, false);
    try std.testing.expectEqual(@as(u32, 0), protected_only.strongest_count);
    try std.testing.expectEqual(@as(u32, 0), protected_only.optimize_count);
    try std.testing.expectEqual(Intensity.unrestricted, plan(8_000, .{}, thresholds, true).baseline);
}

fn allEligible(count: u32) EligibleCounts {
    return .{ .optimize = count, .strongest = count };
}
