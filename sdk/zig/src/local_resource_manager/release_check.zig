//! Pure pre-release decision check. The state owner must recheck identity and
//! lease protection before freeing its own memory, irrespective of this policy.
const std = @import("std");
const defaults = @import("ledger_values");

pub const Policy = struct {
    enabled: bool = defaults.Bound.table_0_release_check_enabled == 2,
    // One percent is 10,000 parts per million; the default is exactly zero.
    tolerance_parts_per_million: u32 = @intCast(defaults.Bound.table_0_release_tolerance_parts_per_million),
    tolerance_bytes: u64 = defaults.Bound.table_0_release_tolerance_bytes,
};

pub const Result = enum { allowed, changed_beyond_tolerance };

pub fn check(policy: Policy, decision_bytes: u64, current_bytes: u64, forced: bool) Result {
    if (forced or !policy.enabled) return .allowed;
    const difference = @max(decision_bytes, current_bytes) - @min(decision_bytes, current_bytes);
    // Scaling the comparison avoids rounding a fractional tolerance and keeps
    // every u64 size and u32 relative tolerance within exact u128 arithmetic.
    const scaled_difference = @as(u128, difference) * 1_000_000;
    const tolerance = @as(u128, decision_bytes) * policy.tolerance_parts_per_million +
        @as(u128, policy.tolerance_bytes) * 1_000_000;
    return if (scaled_difference <= tolerance) .allowed else .changed_beyond_tolerance;
}

test "safe release defaults off, zero tolerance requires equality and force bypasses checking" {
    try std.testing.expectEqual(Result.allowed, check(.{}, 0, std.math.maxInt(u64), false));
    const strict: Policy = .{ .enabled = true };
    try std.testing.expectEqual(Result.allowed, check(strict, 23, 23, false));
    try std.testing.expectEqual(Result.changed_beyond_tolerance, check(strict, 23, 24, false));
    try std.testing.expectEqual(Result.allowed, check(strict, 23, 24, true));
}

test "release tolerance uses the decision size and exact inclusive percentage plus bytes" {
    const policy: Policy = .{ .enabled = true, .tolerance_parts_per_million = 100_000, .tolerance_bytes = 3 };
    try std.testing.expectEqual(Result.allowed, check(policy, 100, 113, false));
    try std.testing.expectEqual(Result.changed_beyond_tolerance, check(policy, 100, 114, false));
    try std.testing.expectEqual(Result.allowed, check(policy, 100, 87, false));
    try std.testing.expectEqual(Result.changed_beyond_tolerance, check(policy, 100, 86, false));
    // Scaling cannot round up a fractional tolerance to permit an extra byte.
    try std.testing.expectEqual(Result.allowed, check(policy, 9, 12, false));
    try std.testing.expectEqual(Result.changed_beyond_tolerance, check(policy, 9, 13, false));
    try std.testing.expectEqual(Result.allowed, check(policy, 0, 3, false));
    try std.testing.expectEqual(Result.changed_beyond_tolerance, check(policy, 0, 4, false));
}

test "maximum sizes and tolerance do not overflow" {
    const maximum = std.math.maxInt(u64);
    const full_percent: Policy = .{ .enabled = true, .tolerance_parts_per_million = 1_000_000 };
    try std.testing.expectEqual(Result.allowed, check(full_percent, maximum, 0, false));
    try std.testing.expectEqual(Result.changed_beyond_tolerance, check(full_percent, 0, maximum, false));
    const maximum_policy: Policy = .{
        .enabled = true,
        .tolerance_parts_per_million = std.math.maxInt(u32),
        .tolerance_bytes = maximum,
    };
    try std.testing.expectEqual(Result.allowed, check(maximum_policy, 0, maximum, false));
    try std.testing.expectEqual(Result.allowed, check(maximum_policy, maximum, 0, false));
}

test "a registration changed after decision stays resident on rejection and can be reconsidered" {
    const builtin = @import("builtin");
    if (builtin.os.tag != .windows) return error.SkipZigTest;
    const Registration = @import("allocation_registration.zig").Registration;
    var registration = try Registration.fixed(100);
    defer registration.unload() catch @panic("pre-release check cleanup failed");
    _ = try registration.enterMemory();
    const decision_bytes = registration.allocation_bytes;
    _ = try registration.update(114);
    const before = registration;
    const policy: Policy = .{ .enabled = true, .tolerance_parts_per_million = 100_000, .tolerance_bytes = 3 };
    if (check(policy, decision_bytes, registration.allocation_bytes, false) == .allowed) try registration.unload();
    try std.testing.expectEqualDeep(before, registration);
    try std.testing.expect(registration.region != null);
    const next_decision_bytes = registration.allocation_bytes;
    _ = try registration.update(115);
    try std.testing.expectEqual(Result.allowed, check(policy, next_decision_bytes, registration.allocation_bytes, false));
    try registration.unload();
    try std.testing.expect(registration.region == null);
    try std.testing.expectEqual(@as(u64, 115), registration.allocation_bytes);
}
