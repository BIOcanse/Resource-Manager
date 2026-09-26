const std = @import("std");

pub fn dueAt(
    last_sampled_milliseconds: u64,
    scheduled_due_milliseconds: u64,
    retry_at_milliseconds: u64,
    interval_milliseconds: u64,
    now_milliseconds: u64,
) u64 {
    var due = if (last_sampled_milliseconds == 0)
        now_milliseconds
    else if (scheduled_due_milliseconds != 0)
        scheduled_due_milliseconds
    else
        nextDue(last_sampled_milliseconds, interval_milliseconds);
    if (retry_at_milliseconds > due) due = retry_at_milliseconds;
    return due;
}

pub fn nextDue(completed_at_milliseconds: u64, interval_milliseconds: u64) u64 {
    std.debug.assert(interval_milliseconds != 0);
    const boundary = completed_at_milliseconds / interval_milliseconds;
    const next_boundary = std.math.add(u64, boundary, 1) catch return std.math.maxInt(u64);
    return std.math.mul(u64, next_boundary, interval_milliseconds) catch std.math.maxInt(u64);
}

test "same interval settles on the same future boundary" {
    try std.testing.expectEqual(@as(u64, 2_000), nextDue(1_001, 1_000));
    try std.testing.expectEqual(@as(u64, 2_000), nextDue(1_999, 1_000));
    try std.testing.expectEqual(@as(u64, 5_000), nextDue(1_001, 5_000));
}

test "alignment never delays a future sample beyond its interval" {
    const completed: u64 = 12_345;
    const interval: u64 = 5_000;
    const due = nextDue(completed, interval);
    try std.testing.expect(due > completed);
    try std.testing.expect(due <= completed + interval);
}
