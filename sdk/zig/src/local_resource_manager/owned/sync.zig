//! Explicit short metadata locks. No allocation or process-wide registry.
const std = @import("std");

pub const Lock = struct {
    held: std.atomic.Value(u8) = std.atomic.Value(u8).init(0),
    pub fn lock(self: *Lock) void {
        while (self.held.cmpxchgWeak(0, 1, .acquire, .monotonic) != null) std.atomic.spinLoopHint();
    }
    pub fn unlock(self: *Lock) void {
        self.held.store(0, .release);
    }
};

pub const Counter = struct {
    next: std.atomic.Value(u64) = std.atomic.Value(u64).init(1),
    pub fn take(self: *Counter) error{SpecificationLimit}!u64 {
        var current = self.next.load(.monotonic);
        while (true) {
            if (current == std.math.maxInt(u64)) return error.SpecificationLimit;
            if (self.next.cmpxchgWeak(current, current + 1, .monotonic, .monotonic)) |changed| {
                current = changed;
            } else return current;
        }
    }
};

test "allocation numbers never repeat or wrap" {
    var counter = Counter{};
    try std.testing.expectEqual(@as(u64, 1), try counter.take());
    try std.testing.expectEqual(@as(u64, 2), try counter.take());
    counter.next.store(std.math.maxInt(u64) - 1, .monotonic);
    try std.testing.expectEqual(std.math.maxInt(u64) - 1, try counter.take());
    try std.testing.expectError(error.SpecificationLimit, counter.take());
    try std.testing.expectEqual(std.math.maxInt(u64), counter.next.load(.monotonic));
}
