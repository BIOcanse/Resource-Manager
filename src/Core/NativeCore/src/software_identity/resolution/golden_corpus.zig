const std = @import("std");
const protocol = @import("protocol.zig");

test "software identity resolution ABI sizes and offsets are fixed" {
    try std.testing.expectEqual(@as(u32, 0x0001_0000), protocol.abi_version);
    try std.testing.expectEqual(@as(usize, 104), @sizeOf(protocol.Config));
    try std.testing.expectEqual(@as(usize, 64), @sizeOf(protocol.Capacity));
    try std.testing.expectEqual(@as(usize, 48), @sizeOf(protocol.SourcePolicyInput));
    try std.testing.expectEqual(@as(usize, 72), @sizeOf(protocol.PolicyReplaceInput));
    try std.testing.expectEqual(@as(usize, 96), @sizeOf(protocol.ResolveInput));
    try std.testing.expectEqual(@as(usize, 80), @sizeOf(protocol.ObservationInput));
    try std.testing.expectEqual(@as(usize, 176), @sizeOf(protocol.ResolutionOutput));
    try std.testing.expectEqual(@as(usize, 128), @sizeOf(protocol.ResolutionSummary));

    try std.testing.expectEqual(@as(usize, 8), @offsetOf(protocol.Config, "generation"));
    try std.testing.expectEqual(@as(usize, 16), @offsetOf(protocol.Config, "maximum_policy_count"));
    try std.testing.expectEqual(@as(usize, 32), @offsetOf(protocol.Config, "required_source_mask"));
    try std.testing.expectEqual(@as(usize, 56), @offsetOf(protocol.Config, "resident_byte_budget"));
    try std.testing.expectEqual(@as(usize, 64), @offsetOf(protocol.Config, "flags"));
    try std.testing.expectEqual(@as(usize, 16), @offsetOf(protocol.Capacity, "resident_byte_count"));
    try std.testing.expectEqual(@as(usize, 4), @offsetOf(protocol.SourcePolicyInput, "source_id"));
    try std.testing.expectEqual(@as(usize, 40), @offsetOf(protocol.PolicyReplaceInput, "valid_mask"));
    try std.testing.expectEqual(@as(usize, 32), @offsetOf(protocol.ResolveInput, "command_utc_ms"));
    try std.testing.expectEqual(@as(usize, 56), @offsetOf(protocol.ResolveInput, "valid_mask"));
    try std.testing.expectEqual(@as(usize, 16), @offsetOf(protocol.ObservationInput, "identity_handle"));
    try std.testing.expectEqual(@as(usize, 56), @offsetOf(protocol.ObservationInput, "observation_generation"));
    try std.testing.expectEqual(@as(usize, 56), @offsetOf(protocol.ResolutionOutput, "selected_source_id"));
    try std.testing.expectEqual(@as(usize, 72), @offsetOf(protocol.ResolutionOutput, "identity_handle"));
    try std.testing.expectEqual(@as(usize, 112), @offsetOf(protocol.ResolutionOutput, "observed_source_mask"));
    try std.testing.expectEqual(@as(usize, 88), @offsetOf(protocol.ResolutionSummary, "resident_byte_count"));
}
