const std = @import("std");
const layout = @import("layout.zig");
const activity_decay = @import("activity_decay");

pub fn settledValue(slot: *const layout.ResourceSlot, header: *const layout.Header) u64 {
    return activity_decay.decay(
        slot.activity_value,
        header.activity_epoch - slot.last_activity_epoch,
        header.config.activity_decay_numerator,
        header.config.activity_decay_denominator,
    );
}

pub fn touch(slot: *layout.ResourceSlot, header: *const layout.Header, weight: u32) !void {
    if (weight == 0) return error.InvalidArgument;
    if (slot.activity_revision == std.math.maxInt(u64)) return error.GenerationExhausted;
    const current = settledValue(slot, header);
    slot.activity_value = std.math.add(u64, current, weight) catch std.math.maxInt(u64);
    slot.last_activity_epoch = header.activity_epoch;
    slot.activity_revision += 1;
}

test "logical activity decay is deterministic and wall-clock free" {
    try std.testing.expectEqual(@as(u64, 100), activity_decay.decay(100, 0, 1, 2));
    try std.testing.expectEqual(@as(u64, 50), activity_decay.decay(100, 1, 1, 2));
    try std.testing.expectEqual(@as(u64, 25), activity_decay.decay(100, 2, 1, 2));
    try std.testing.expectEqual(@as(u64, 100), activity_decay.decay(100, 999, 1, 1));
}
