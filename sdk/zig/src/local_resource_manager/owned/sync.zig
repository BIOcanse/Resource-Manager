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

/// Writer admission closes before draining the short shared metadata readers.
/// A user lease never owns this gate. Pending writers cannot starve behind hits.
pub const SharedLock = struct {
    writer: Lock = .{},
    state: std.atomic.Value(u32) = std.atomic.Value(u32).init(0),
    const closed: u32 = 1 << 31;
    pub fn lockShared(self: *SharedLock) void {
        var value = self.state.load(.monotonic);
        while (true) {
            if (value >= closed - 1) {
                std.atomic.spinLoopHint();
                value = self.state.load(.monotonic);
            } else if (self.state.cmpxchgWeak(value, value + 1, .acquire, .monotonic)) |changed| {
                value = changed;
            } else return;
        }
    }
    pub fn unlockShared(self: *SharedLock) void {
        _ = self.state.fetchSub(1, .release);
    }
    pub fn lock(self: *SharedLock) void {
        self.writer.lock();
        _ = self.state.fetchOr(closed, .acquire);
        while (self.state.load(.acquire) != closed) std.atomic.spinLoopHint();
    }
    pub fn unlock(self: *SharedLock) void {
        self.state.store(0, .release);
        self.writer.unlock();
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
