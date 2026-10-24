const std = @import("std");

pub const Reason = enum(u32) {
    none = 0,
    operation_submit = 1,
    operation_decode = 2,
    ownership_promote = 3,
    ownership_decode = 4,
    journal_prepare = 5,
    journal_batch = 6,
    journal_decode = 7,
    smart_process = 8,
    smart_software = 9,
    smart_gpu = 10,
    smart_reservation = 11,
    smart_atomic_group = 12,
    report_rules = 13,
    report_import = 14,
    report_facts = 15,
    report_trust = 16,
    report_reports = 17,
    report_persistence = 18,
};

pub const Unit = enum(u32) { rows = 0, bytes = 1 };

pub const Event = extern struct {
    sequence: u64 = 0,
    reason: u32 = 0,
    unit: u32 = 0,
    old_capacity: u64 = 0,
    new_capacity: u64 = 0,
};

/// Fixed diagnostics storage. A consumer reads after each growth-capable native
/// call; eight slots preserve all physical owner growths within one call.
pub const Log = extern struct {
    count: u64 = 0,
    events: [8]Event = @splat(.{}),

    pub fn record(self: *Log, reason: Reason, unit: Unit, old_capacity: u64, new_capacity: u64) void {
        if (new_capacity <= old_capacity) return;
        const slot = self.count % self.events.len;
        self.count += 1;
        self.events[slot] = .{
            .sequence = self.count,
            .reason = @intFromEnum(reason),
            .unit = @intFromEnum(unit),
            .old_capacity = old_capacity,
            .new_capacity = new_capacity,
        };
    }
};

test "growth diagnostics preserve bounded sequence and actual growth only" {
    try std.testing.expectEqual(@as(usize, 32), @sizeOf(Event));
    try std.testing.expectEqual(@as(usize, 264), @sizeOf(Log));
    var log: Log = .{};
    log.record(.operation_submit, .rows, 16, 16);
    try std.testing.expectEqual(@as(u64, 0), log.count);
    for (0..10) |index| log.record(.report_facts, .bytes, index + 1, index + 2);
    try std.testing.expectEqual(@as(u64, 10), log.count);
    for (2..10) |index| {
        const event = log.events[index % log.events.len];
        try std.testing.expectEqual(@as(u64, index + 1), event.sequence);
        try std.testing.expectEqual(@as(u32, 15), event.reason);
        try std.testing.expectEqual(@as(u32, 1), event.unit);
        try std.testing.expectEqual(@as(u64, index + 1), event.old_capacity);
        try std.testing.expectEqual(@as(u64, index + 2), event.new_capacity);
    }
}

comptime {
    if (@sizeOf(Event) != 32 or @sizeOf(Log) != 264) @compileError("workspace growth diagnostics ABI mismatch");
}
