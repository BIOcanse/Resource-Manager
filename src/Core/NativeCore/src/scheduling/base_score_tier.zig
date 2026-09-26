const std = @import("std");

pub const Tier = enum(u8) {
    low = 0,
    middle = 1,
    high = 2,
};

pub fn isValidThresholds(middle_minimum: f64, high_minimum: f64) bool {
    return std.math.isFinite(middle_minimum) and
        std.math.isFinite(high_minimum) and
        middle_minimum > 0 and
        middle_minimum < high_minimum and
        high_minimum <= 100;
}

pub fn resolve(base_score: f64, middle_minimum: f64, high_minimum: f64) Tier {
    std.debug.assert(std.math.isFinite(base_score) and base_score >= 0);
    std.debug.assert(isValidThresholds(middle_minimum, high_minimum));
    if (base_score >= high_minimum) return .high;
    if (base_score >= middle_minimum) return .middle;
    return .low;
}

test "three tiers use exact inclusive minimum boundaries" {
    try std.testing.expectEqual(Tier.low, resolve(20, 21, 81));
    try std.testing.expectEqual(Tier.middle, resolve(21, 21, 81));
    try std.testing.expectEqual(Tier.middle, resolve(80, 21, 81));
    try std.testing.expectEqual(Tier.high, resolve(81, 21, 81));
}

test "threshold validation keeps every tier reachable in the base score domain" {
    try std.testing.expect(isValidThresholds(21, 81));
    try std.testing.expect(isValidThresholds(0.01, 100));
    try std.testing.expect(!isValidThresholds(0, 81));
    try std.testing.expect(!isValidThresholds(21, 101));
    try std.testing.expect(!isValidThresholds(81, 81));
}
