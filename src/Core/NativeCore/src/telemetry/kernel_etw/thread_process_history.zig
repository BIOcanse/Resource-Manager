const std = @import("std");
const Budget = @import("budget.zig").Budget;

// TraceEvent 3.2.4 KernelTraceEventParser ThreadToProcess tracking, not
// residency's independent thread lifetime identity. Starts deliberately have
// timestamp zero in that parser. Recorded ends supply circular-buffer evidence
// only when a start is absent; live ends release the numeric TID immediately.
// One consumer flow owns this state. No method starts/stops work or takes a lock.
pub const History = struct {
    const End = struct { qpc: i64, pid: u32 };
    budget: Budget,
    max_entries: usize,
    realtime: bool,
    starts: std.AutoHashMapUnmanaged(u32, u32) = .empty,
    ends: std.AutoHashMapUnmanaged(u32, std.ArrayList(End)) = .empty,
    end_count: usize = 0,

    pub fn init(backing: std.mem.Allocator, max_entries: usize, byte_cap: usize, realtime: bool) !History {
        if (max_entries == 0 or byte_cap == 0) return error.InvalidConfiguration;
        return .{ .budget = .{ .backing = backing, .cap = byte_cap }, .max_entries = max_entries, .realtime = realtime };
    }
    pub fn deinit(self: *History) void {
        self.clear();
    }
    pub fn clear(self: *History) void {
        const a = self.budget.allocator();
        var values = self.ends.valueIterator();
        while (values.next()) |list| list.deinit(a);
        self.ends.clearAndFree(a);
        self.starts.clearAndFree(a);
        self.end_count = 0;
    }
    pub fn start(self: *History, tid: u32, pid: u32) !void {
        if (tid == std.math.maxInt(u32) or pid == std.math.maxInt(u32)) return error.InvalidIdentity;
        if (!self.starts.contains(tid) and self.starts.count() + self.end_count >= self.max_entries) return error.CapacityExceeded;
        try self.starts.put(self.budget.allocator(), tid, pid);
    }
    pub fn end(self: *History, tid: u32, pid: u32, qpc: i64) !void {
        if (tid == std.math.maxInt(u32) or pid == std.math.maxInt(u32) or qpc < 0) return error.InvalidIdentity;
        if (self.realtime) {
            _ = self.starts.remove(tid);
            return;
        }
        if (self.starts.contains(tid)) return;
        const a = self.budget.allocator();
        if (self.ends.getPtr(tid)) |list| {
            var lo: usize = 0;
            var hi = list.items.len;
            while (lo < hi) {
                const mid = lo + (hi - lo) / 2;
                if (list.items[mid].qpc < qpc) lo = mid + 1 else hi = mid;
            }
            if (lo < list.items.len and list.items[lo].qpc == qpc) {
                list.items[lo].pid = pid;
                return;
            }
            if (self.starts.count() + self.end_count >= self.max_entries) return error.CapacityExceeded;
            try list.insert(a, lo, .{ .qpc = qpc, .pid = pid });
        } else {
            if (self.starts.count() + self.end_count >= self.max_entries) return error.CapacityExceeded;
            var list: std.ArrayList(End) = .empty;
            errdefer list.deinit(a);
            try list.append(a, .{ .qpc = qpc, .pid = pid });
            try self.ends.put(a, tid, list);
        }
        self.end_count += 1;
    }
    pub fn lookup(self: *const History, tid: u32, qpc: i64) ?u32 {
        if (self.starts.get(tid)) |pid| return pid;
        if (self.realtime or qpc < 0) return null;
        const list = self.ends.get(tid) orelse return null;
        var lo: usize = 0;
        var hi = list.items.len;
        while (lo < hi) {
            const mid = lo + (hi - lo) / 2;
            if (list.items[mid].qpc < qpc) lo = mid + 1 else hi = mid;
        }
        return if (lo < list.items.len) list.items[lo].pid else null;
    }
};

test "recorded thread end supplies nearest later circular-buffer identity" {
    var history = try History.init(std.testing.allocator, 8, 16384, false);
    defer history.deinit();
    try std.testing.expectEqual(null, history.lookup(7, 10));
    try history.end(7, 21, 200);
    try history.end(7, 11, 100);
    try std.testing.expectEqual(@as(?u32, 11), history.lookup(7, 0));
    try std.testing.expectEqual(@as(?u32, 11), history.lookup(7, 100));
    try std.testing.expectEqual(@as(?u32, 21), history.lookup(7, 101));
    try std.testing.expectEqual(null, history.lookup(7, 201));
    // Pinned parser's ThreadStartGroup stores timestamp zero; do not silently
    // replace that established behavior with a different residency identity rule.
    try history.start(7, 31);
    try history.end(7, 31, 300);
    try std.testing.expectEqual(@as(?u32, 31), history.lookup(7, 1));
    try history.start(7, 41);
    try std.testing.expectEqual(@as(?u32, 41), history.lookup(7, 400));
    history.clear();
    try std.testing.expectEqual(@as(usize, 0), history.budget.current);
    try std.testing.expect(history.budget.peak > 0);
    try std.testing.expectEqual(null, history.lookup(7, 400));
}

test "live thread identity retires at end and numeric reuse is explicit" {
    var history = try History.init(std.testing.allocator, 2, 16384, true);
    defer history.deinit();
    try history.start(7, 11);
    try history.end(7, 11, 100);
    try std.testing.expectEqual(null, history.lookup(7, 50));
    try history.end(8, 22, 200);
    try std.testing.expectEqual(null, history.lookup(8, 50));
    try history.start(7, 33);
    try std.testing.expectEqual(@as(?u32, 33), history.lookup(7, 201));
}

test "thread identity caps reject growth without dropping prior evidence" {
    var history = try History.init(std.testing.allocator, 1, 16384, false);
    defer history.deinit();
    try history.end(7, 11, 100);
    try std.testing.expectError(error.CapacityExceeded, history.start(8, 22));
    try std.testing.expectError(error.CapacityExceeded, history.end(7, 22, 200));
    try std.testing.expectEqual(@as(?u32, 11), history.lookup(7, 100));
    var no_memory = try History.init(std.testing.allocator, 1, 1, false);
    defer no_memory.deinit();
    try std.testing.expectError(error.OutOfMemory, no_memory.start(1, 2));
    try std.testing.expectEqual(@as(usize, 0), no_memory.budget.current);
    try std.testing.expect(no_memory.budget.failures > 0);
}
