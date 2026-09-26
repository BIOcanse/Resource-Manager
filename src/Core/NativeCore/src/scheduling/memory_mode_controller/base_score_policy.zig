const std = @import("std");
const protocol = @import("protocol.zig");
const base_score_tier = @import("../base_score_tier.zig");

pub const Thresholds = struct {
    middle_minimum: f64,
    high_minimum: f64,
};

pub const Decision = struct {
    mode: protocol.MemoryMode,
    clamped: bool,
};

pub fn allowedGrades(base_score: f64, thresholds: Thresholds) u8 {
    std.debug.assert(protocol.validScore(base_score));
    return switch (base_score_tier.resolve(
        base_score,
        thresholds.middle_minimum,
        thresholds.high_minimum,
    )) {
        .high => protocol.MemoryGradeSet.normal,
        .middle => protocol.MemoryGradeSet.normal | protocol.MemoryGradeSet.optimization,
        .low => protocol.MemoryGradeSet.all,
    };
}

pub fn apply(
    requested: protocol.MemoryMode,
    base_score: f64,
    thresholds: Thresholds,
) Decision {
    return applyAllowed(requested, allowedGrades(base_score, thresholds));
}

pub fn applyAllowed(requested: protocol.MemoryMode, allowed: u8) Decision {
    std.debug.assert(allowed == protocol.MemoryGradeSet.normal or
        allowed == (protocol.MemoryGradeSet.normal | protocol.MemoryGradeSet.optimization) or
        allowed == protocol.MemoryGradeSet.all);
    const can_optimize = (allowed & protocol.MemoryGradeSet.optimization) != 0;
    const effective = switch (requested) {
        .normal => protocol.MemoryMode.normal,
        .optimize => if (can_optimize) protocol.MemoryMode.optimize else protocol.MemoryMode.normal,
        .paged_frozen => if (protocol.MemoryGradeSet.contains(allowed, .level4))
            protocol.MemoryMode.paged_frozen
        else if (can_optimize)
            protocol.MemoryMode.optimize
        else
            protocol.MemoryMode.normal,
        // Preserve the separate legacy adapted mode; this is not a memory A1 grade.
        .unrestricted => if (allowed == protocol.MemoryGradeSet.normal)
            protocol.MemoryMode.unrestricted
        else
            protocol.MemoryMode.normal,
        .invalid => unreachable,
    };

    return .{ .mode = effective, .clamped = effective != requested };
}

test "memory grade eligibility uses shared fractional and configurable boundaries" {
    const thresholds = Thresholds{ .middle_minimum = 21, .high_minimum = 81 };
    for ([_]f64{ 0, 10, 20, 20.999 }) |score| {
        try std.testing.expectEqual(protocol.MemoryGradeSet.all, allowedGrades(score, thresholds));
    }
    for ([_]f64{ 21, 50, 80, 80.999 }) |score| {
        try std.testing.expectEqual(protocol.MemoryGradeSet.normal | protocol.MemoryGradeSet.optimization, allowedGrades(score, thresholds));
    }
    for ([_]f64{ 81, 95, 100 }) |score| {
        try std.testing.expectEqual(protocol.MemoryGradeSet.normal, allowedGrades(score, thresholds));
    }
    const changed = Thresholds{ .middle_minimum = 30, .high_minimum = 90 };
    try std.testing.expectEqual(protocol.MemoryGradeSet.all, allowedGrades(21, changed));
    try std.testing.expectEqual(protocol.MemoryGradeSet.normal | protocol.MemoryGradeSet.optimization, allowedGrades(81, changed));
    try std.testing.expectEqual(protocol.MemoryGradeSet.normal, allowedGrades(90, changed));
}

test "every memory grade is checked against the same eligibility set" {
    const thresholds = Thresholds{ .middle_minimum = 21, .high_minimum = 81 };
    for ([_]f64{ 10, 50, 95 }) |score| {
        const allowed = allowedGrades(score, thresholds);
        inline for (std.meta.tags(protocol.MemoryGrade)) |grade| {
            const expected = grade == .normal or (score < 81 and (grade != .level4 or score < 21));
            try std.testing.expectEqual(expected, protocol.MemoryGradeSet.contains(allowed, grade));
        }
        inline for (.{ protocol.MemoryMode.normal, .optimize, .paged_frozen, .unrestricted }) |requested| {
            try std.testing.expectEqual(apply(requested, score, thresholds), applyAllowed(requested, allowed));
        }
    }
}

test "base score tiers use exact shared boundaries" {
    const thresholds = Thresholds{
        .middle_minimum = 21,
        .high_minimum = 81,
    };

    try std.testing.expectEqual(protocol.MemoryMode.paged_frozen, apply(.paged_frozen, 20, thresholds).mode);
    try std.testing.expectEqual(protocol.MemoryMode.normal, apply(.unrestricted, 20, thresholds).mode);

    try std.testing.expectEqual(protocol.MemoryMode.optimize, apply(.paged_frozen, 21, thresholds).mode);
    try std.testing.expectEqual(protocol.MemoryMode.normal, apply(.unrestricted, 80, thresholds).mode);

    try std.testing.expectEqual(protocol.MemoryMode.normal, apply(.optimize, 81, thresholds).mode);
    try std.testing.expectEqual(protocol.MemoryMode.unrestricted, apply(.unrestricted, 81, thresholds).mode);
}
