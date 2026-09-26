const std = @import("std");
const protocol = @import("protocol.zig");

test "sampling subscription ABI sizes and offsets are fixed" {
    try std.testing.expectEqual(@as(usize, 112), @sizeOf(protocol.Config));
    try std.testing.expectEqual(@as(usize, 64), @sizeOf(protocol.Capacity));
    try std.testing.expectEqual(@as(usize, 32), @sizeOf(protocol.ItemReference));
    try std.testing.expectEqual(@as(usize, 96), @sizeOf(protocol.TrackInput));
    try std.testing.expectEqual(@as(usize, 72), @sizeOf(protocol.RemoveInput));
    try std.testing.expectEqual(@as(usize, 64), @sizeOf(protocol.ControlInput));
    try std.testing.expectEqual(@as(usize, 120), @sizeOf(protocol.PlanHeader));
    try std.testing.expectEqual(@as(usize, 64), @sizeOf(protocol.DueItemOutput));
    try std.testing.expectEqual(@as(usize, 64), @sizeOf(protocol.SourceViewOutput));
    try std.testing.expectEqual(@as(usize, 48), @sizeOf(protocol.ExpiredSourceOutput));
    try std.testing.expectEqual(@as(usize, 88), @sizeOf(protocol.CompletionInput));
    try std.testing.expectEqual(@as(usize, 72), @sizeOf(protocol.CompletionOutput));
    try std.testing.expectEqual(@as(usize, 112), @sizeOf(protocol.SnapshotHeader));
    try std.testing.expectEqual(@as(usize, 64), @sizeOf(protocol.ItemStateOutput));

    try std.testing.expectEqual(@as(usize, 8), @offsetOf(protocol.Config, "generation"));
    try std.testing.expectEqual(@as(usize, 16), @offsetOf(protocol.Config, "maximum_source_count"));
    try std.testing.expectEqual(@as(usize, 40), @offsetOf(protocol.Config, "default_interval_milliseconds"));
    try std.testing.expectEqual(@as(usize, 72), @offsetOf(protocol.Config, "flags"));
    try std.testing.expectEqual(@as(usize, 80), @offsetOf(protocol.Config, "reserved_u64_0"));
    try std.testing.expectEqual(@as(usize, 108), @offsetOf(protocol.Config, "reserved_u32_1"));
    try std.testing.expectEqual(@as(usize, 40), @offsetOf(protocol.TrackInput, "source_handle"));
    try std.testing.expectEqual(@as(usize, 56), @offsetOf(protocol.TrackInput, "valid_mask"));
    try std.testing.expectEqual(@as(usize, 48), @offsetOf(protocol.PlanHeader, "due_item_capacity"));
    try std.testing.expectEqual(@as(usize, 80), @offsetOf(protocol.PlanHeader, "next_wake_milliseconds"));
    try std.testing.expectEqual(@as(usize, 104), @offsetOf(protocol.PlanHeader, "request_flags"));
    try std.testing.expectEqual(@as(usize, 56), @offsetOf(protocol.DueItemOutput, "scheduled_due_milliseconds"));
    try std.testing.expectEqual(@as(usize, 48), @offsetOf(protocol.CompletionInput, "valid_mask"));
    try std.testing.expectEqual(@as(usize, 56), @offsetOf(protocol.SnapshotHeader, "active_source_count"));
    try std.testing.expectEqual(@as(usize, 56), @offsetOf(protocol.ItemStateOutput, "scheduled_due_milliseconds"));
}
