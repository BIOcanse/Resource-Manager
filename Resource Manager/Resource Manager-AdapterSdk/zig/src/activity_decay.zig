const std = @import("std");

const fixed_one: u64 = 1 << 32;

pub fn decay(value: u64, generations: u64, numerator: u32, denominator: u32) u64 {
    std.debug.assert(denominator != 0);
    std.debug.assert(numerator <= denominator);
    if (value == 0 or generations == 0 or numerator == denominator) return value;
    if (numerator == 0) return 0;

    var exponent = generations;
    var factor: u64 = @intCast((@as(u128, numerator) << 32) / denominator);
    var accumulated: u64 = fixed_one;
    while (exponent != 0) {
        if (exponent & 1 != 0) accumulated = multiplyFixed(accumulated, factor);
        exponent >>= 1;
        if (exponent != 0) factor = multiplyFixed(factor, factor);
    }
    return @intCast((@as(u128, value) * accumulated) >> 32);
}

fn multiplyFixed(left: u64, right: u64) u64 {
    return @intCast((@as(u128, left) * right) >> 32);
}

test "logical activity decay is deterministic and wall-clock free" {
    try std.testing.expectEqual(@as(u64, 100), decay(100, 0, 1, 2));
    try std.testing.expectEqual(@as(u64, 50), decay(100, 1, 1, 2));
    try std.testing.expectEqual(@as(u64, 25), decay(100, 2, 1, 2));
    try std.testing.expectEqual(@as(u64, 100), decay(100, 999, 1, 1));
}
