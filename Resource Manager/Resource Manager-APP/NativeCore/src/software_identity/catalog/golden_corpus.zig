const std = @import("std");
const protocol = @import("protocol.zig");

test "software identity catalog ABI v5 sizes and offsets are fixed" {
    try std.testing.expectEqual(@as(u32, 0x0005_0000), protocol.abi_version);
    try std.testing.expectEqual(@as(usize, 224), @sizeOf(protocol.Config));
    try std.testing.expectEqual(@as(usize, 80), @sizeOf(protocol.Capacity));
    try std.testing.expectEqual(@as(usize, 72), @sizeOf(protocol.EntryInput));
    try std.testing.expectEqual(@as(usize, 56), @sizeOf(protocol.AliasInput));
    try std.testing.expectEqual(@as(usize, 56), @sizeOf(protocol.RootInput));
    try std.testing.expectEqual(@as(usize, 88), @sizeOf(protocol.ReplaceInput));
    try std.testing.expectEqual(@as(usize, 80), @sizeOf(protocol.QueryInput));
    try std.testing.expectEqual(@as(usize, 48), @sizeOf(protocol.FactInput));
    try std.testing.expectEqual(@as(usize, 88), @sizeOf(protocol.KnownQueryInput));
    try std.testing.expectEqual(@as(usize, 40), @sizeOf(protocol.KnownSignalInput));
    try std.testing.expectEqual(@as(usize, 128), @sizeOf(protocol.MatchOutput));
    try std.testing.expectEqual(@as(usize, 168), @sizeOf(protocol.KnownMatchOutput));
    try std.testing.expectEqual(@as(usize, 128), @sizeOf(protocol.CatalogSummary));

    try std.testing.expectEqual(@as(usize, 8), @offsetOf(protocol.Config, "generation"));
    try std.testing.expectEqual(@as(usize, 16), @offsetOf(protocol.Config, "maximum_entry_count"));
    try std.testing.expectEqual(@as(usize, 24), @offsetOf(protocol.Config, "maximum_root_count"));
    try std.testing.expectEqual(@as(usize, 44), @offsetOf(protocol.Config, "entry_index_capacity"));
    try std.testing.expectEqual(@as(usize, 56), @offsetOf(protocol.Config, "root_index_capacity"));
    try std.testing.expectEqual(@as(usize, 80), @offsetOf(protocol.Config, "resident_byte_budget"));
    try std.testing.expectEqual(@as(usize, 128), @offsetOf(protocol.Config, "signal_weights"));
    try std.testing.expectEqual(@as(usize, 152), @offsetOf(protocol.Config, "root_signal_weights"));
    try std.testing.expectEqual(@as(usize, 184), @offsetOf(protocol.Config, "flags"));
    try std.testing.expectEqual(@as(usize, 40), @offsetOf(protocol.Capacity, "identity_index_capacity"));
    try std.testing.expectEqual(@as(usize, 48), @offsetOf(protocol.Capacity, "resident_byte_count"));
    try std.testing.expectEqual(@as(usize, 40), @offsetOf(protocol.EntryInput, "primary_name_offset"));
    try std.testing.expectEqual(@as(usize, 24), @offsetOf(protocol.RootInput, "key_offset"));
    try std.testing.expectEqual(@as(usize, 40), @offsetOf(protocol.ReplaceInput, "root_count"));
    try std.testing.expectEqual(@as(usize, 64), @offsetOf(protocol.ReplaceInput, "valid_mask"));
    try std.testing.expectEqual(@as(usize, 48), @offsetOf(protocol.QueryInput, "valid_mask"));
    try std.testing.expectEqual(@as(usize, 48), @offsetOf(protocol.KnownQueryInput, "valid_mask"));
    try std.testing.expectEqual(@as(usize, 72), @offsetOf(protocol.MatchOutput, "source"));
    try std.testing.expectEqual(@as(usize, 96), @offsetOf(protocol.KnownMatchOutput, "matched_root_handle"));
    try std.testing.expectEqual(@as(usize, 112), @offsetOf(protocol.KnownMatchOutput, "derived_identity_fingerprint_low"));
    try std.testing.expectEqual(@as(usize, 136), @offsetOf(protocol.KnownMatchOutput, "flags"));
    try std.testing.expectEqual(@as(usize, 80), @offsetOf(protocol.CatalogSummary, "catalog_fingerprint_low"));
    try std.testing.expectEqual(@as(usize, 96), @offsetOf(protocol.CatalogSummary, "resident_byte_count"));
}
