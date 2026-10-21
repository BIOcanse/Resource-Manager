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
    // Each item owns its cadence. Equal intervals on different items must not
    // move either query onto an unrelated wall-clock boundary.
    return std.math.add(u64, completed_at_milliseconds, interval_milliseconds) catch
        std.math.maxInt(u64);
}

test "next due keeps the item's own completion phase" {
    try std.testing.expectEqual(@as(u64, 2_001), nextDue(1_001, 1_000));
    try std.testing.expectEqual(@as(u64, 2_999), nextDue(1_999, 1_000));
    try std.testing.expectEqual(@as(u64, 6_001), nextDue(1_001, 5_000));
    try std.testing.expectEqual(@as(u64, 119_999), nextDue(59_999, 60_000));
}

test "next due uses the full interval and saturates overflow" {
    const completed: u64 = 12_345;
    const interval: u64 = 5_000;
    const due = nextDue(completed, interval);
    try std.testing.expectEqual(completed + interval, due);
    try std.testing.expectEqual(std.math.maxInt(u64), nextDue(std.math.maxInt(u64) - 1, 5));
}
