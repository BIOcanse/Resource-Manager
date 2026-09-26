const std = @import("std");
const protocol = @import("protocol.zig");
const identity = @import("identity.zig");

test "display coordinator v3 published layout" {
    try std.testing.expectEqual(@as(usize, 160), @sizeOf(protocol.Config));
    try std.testing.expectEqual(@as(usize, 128), @sizeOf(protocol.Capacity));
    try std.testing.expectEqual(@as(usize, 80), @sizeOf(protocol.RefreshInput));
    try std.testing.expectEqual(@as(usize, 96), @sizeOf(protocol.SourceBatchHeader));
    try std.testing.expectEqual(@as(usize, 48), @sizeOf(protocol.TextInput));
    try std.testing.expectEqual(@as(usize, 168), @sizeOf(protocol.DisplayFact));
    try std.testing.expectEqual(@as(usize, 64), @sizeOf(protocol.FinalizeInput));
    try std.testing.expectEqual(@as(usize, 64), @sizeOf(protocol.AbortInput));
    try std.testing.expectEqual(@as(usize, 56), @sizeOf(protocol.ReadInput));
    try std.testing.expectEqual(@as(usize, 160), @sizeOf(protocol.NodeOutput));
    try std.testing.expectEqual(@as(usize, 64), @sizeOf(protocol.EdgeOutput));
    try std.testing.expectEqual(@as(usize, 136), @sizeOf(protocol.DisplayCapabilityOutput));
    try std.testing.expectEqual(@as(usize, 64), @sizeOf(protocol.TextOutput));
    try std.testing.expectEqual(@as(usize, 56), @sizeOf(protocol.DiffEntry));
    try std.testing.expectEqual(@as(usize, 80), @sizeOf(protocol.UnresolvedOutput));
    try std.testing.expectEqual(@as(usize, 128), @sizeOf(protocol.SnapshotOutput));
    try std.testing.expectEqual(@as(usize, 64), @sizeOf(protocol.PersistenceInput));
    try std.testing.expectEqual(@as(usize, 184), @sizeOf(protocol.PersistenceHeader));
}

test "display coordinator v3 published offsets" {
    try expectOffset(protocol.Config, "abi_version", 0);
    try expectOffset(protocol.Config, "struct_size", 4);
    try std.testing.expectEqual(@as(usize, 8), @offsetOf(protocol.Config, "generation"));
    try std.testing.expectEqual(
        @as(usize, 16),
        @offsetOf(protocol.Config, "maximum_source_count"),
    );
    try std.testing.expectEqual(
        @as(usize, 64),
        @offsetOf(protocol.Config, "resident_byte_budget"),
    );
    try std.testing.expectEqual(
        @as(usize, 72),
        @offsetOf(protocol.Config, "required_source_mask"),
    );
    try std.testing.expectEqual(
        @as(usize, 88),
        @offsetOf(protocol.Config, "identity_source_priority_order"),
    );
    try expectOffset(protocol.Config, "projection_source_priority_order", 112);
    try expectOffset(protocol.Config, "reserved", 136);
    try expectOffset(protocol.Capacity, "maximum_observation_count", 8);
    try expectOffset(protocol.Capacity, "identity_index_capacity", 44);
    try expectOffset(protocol.Capacity, "resident_byte_count", 56);
    try expectOffset(protocol.Capacity, "reserved", 64);
    try expectOffset(protocol.RefreshInput, "refresh_epoch", 16);
    try expectOffset(protocol.RefreshInput, "captured_utc_ms", 24);
    try expectOffset(protocol.RefreshInput, "requested_source_mask", 48);
    try expectOffset(protocol.RefreshInput, "valid_mask", 56);
    try expectOffset(protocol.RefreshInput, "reserved", 72);
    try std.testing.expectEqual(
        @as(usize, 40),
        @offsetOf(protocol.SourceBatchHeader, "source_id"),
    );
    try std.testing.expectEqual(
        @as(usize, 64),
        @offsetOf(protocol.SourceBatchHeader, "valid_mask"),
    );
    try expectOffset(protocol.SourceBatchHeader, "source_generation", 24);
    try expectOffset(protocol.SourceBatchHeader, "fact_count", 48);
    try expectOffset(protocol.SourceBatchHeader, "reserved", 80);
    try expectOffset(protocol.TextInput, "byte_offset", 8);
    try expectOffset(protocol.TextInput, "valid_mask", 16);
    try expectOffset(protocol.TextInput, "reserved", 32);
    try expectOffset(protocol.DisplayFact, "valid_mask", 8);
    try expectOffset(protocol.DisplayFact, "identity_mask", 16);
    try expectOffset(protocol.DisplayFact, "source_record_ordinal", 24);
    try std.testing.expectEqual(
        @as(usize, 56),
        @offsetOf(protocol.DisplayFact, "monitor_path_text_index"),
    );
    try expectOffset(protocol.DisplayFact, "match_text_index", 76);
    try expectOffset(protocol.DisplayFact, "match_flags", 80);
    try expectOffset(protocol.DisplayFact, "reserved_u32", 84);
    try expectOffset(protocol.DisplayFact, "position_x", 88);
    try expectOffset(protocol.DisplayFact, "refresh_numerator", 104);
    try std.testing.expectEqual(
        @as(usize, 128),
        @offsetOf(protocol.DisplayFact, "capability_flags"),
    );
    try expectOffset(protocol.DisplayFact, "observed_at_utc_ms", 136);
    try expectOffset(protocol.DisplayFact, "source_object_key", 144);
    try expectOffset(protocol.DisplayFact, "payload_handle", 152);
    try expectOffset(protocol.FinalizeInput, "refresh_epoch", 16);
    try expectOffset(protocol.FinalizeInput, "expected_source_mask", 24);
    try expectOffset(protocol.FinalizeInput, "reserved", 48);
    try expectOffset(protocol.AbortInput, "refresh_epoch", 16);
    try expectOffset(protocol.AbortInput, "valid_mask", 24);
    try expectOffset(protocol.AbortInput, "reserved", 40);
    try expectOffset(protocol.ReadInput, "state_revision", 16);
    try expectOffset(protocol.ReadInput, "content_generation", 24);
    try expectOffset(protocol.ReadInput, "valid_mask", 32);
    try expectOffset(protocol.ReadInput, "reserved", 48);
    try expectOffset(protocol.NodeOutput, "node_handle", 8);
    try expectOffset(protocol.NodeOutput, "canonical_identity_handle", 32);
    try expectOffset(protocol.NodeOutput, "status_flags", 72);
    try expectOffset(protocol.NodeOutput, "valid_mask", 96);
    try expectOffset(protocol.NodeOutput, "primary_source_id", 112);
    try expectOffset(protocol.NodeOutput, "primary_source_record_ordinal", 120);
    try expectOffset(protocol.NodeOutput, "primary_payload_handle", 128);
    try expectOffset(protocol.NodeOutput, "oem_profile_payload_handle", 144);
    try expectOffset(protocol.EdgeOutput, "parent_handle", 8);
    try expectOffset(protocol.EdgeOutput, "child_handle", 24);
    try expectOffset(protocol.EdgeOutput, "source_mask", 40);
    try expectOffset(protocol.EdgeOutput, "reserved", 56);
    try expectOffset(protocol.DisplayCapabilityOutput, "node_handle", 8);
    try expectOffset(protocol.DisplayCapabilityOutput, "display_identity_handle", 32);
    try expectOffset(protocol.DisplayCapabilityOutput, "friendly_name_handle", 64);
    try expectOffset(protocol.DisplayCapabilityOutput, "left", 80);
    try expectOffset(protocol.DisplayCapabilityOutput, "bits_per_color_channel", 96);
    try expectOffset(protocol.DisplayCapabilityOutput, "refresh_numerator", 120);
    try expectOffset(protocol.DisplayCapabilityOutput, "valid_mask", 128);
    try expectOffset(protocol.TextOutput, "handle", 8);
    try expectOffset(protocol.TextOutput, "byte_offset", 24);
    try expectOffset(protocol.TextOutput, "flags", 32);
    try expectOffset(protocol.TextOutput, "reserved", 40);
    try expectOffset(protocol.DiffEntry, "change_kind", 8);
    try expectOffset(protocol.DiffEntry, "entity_handle", 16);
    try expectOffset(protocol.DiffEntry, "old_generation", 32);
    try expectOffset(protocol.DiffEntry, "changed_field_mask", 48);
    try expectOffset(protocol.UnresolvedOutput, "source_record_ordinal", 8);
    try expectOffset(protocol.UnresolvedOutput, "first_identity", 24);
    try expectOffset(protocol.UnresolvedOutput, "identity_kind", 56);
    try expectOffset(protocol.UnresolvedOutput, "reserved", 64);
    try expectOffset(protocol.SnapshotOutput, "state_revision", 16);
    try expectOffset(protocol.SnapshotOutput, "content_generation", 32);
    try expectOffset(protocol.SnapshotOutput, "phase", 56);
    try expectOffset(protocol.SnapshotOutput, "current_source_mask", 64);
    try expectOffset(protocol.SnapshotOutput, "node_count", 96);
    try std.testing.expectEqual(
        @as(usize, 120),
        @offsetOf(protocol.SnapshotOutput, "resident_byte_count"),
    );
    try expectOffset(protocol.PersistenceInput, "operation_epoch", 16);
    try expectOffset(protocol.PersistenceInput, "valid_mask", 24);
    try expectOffset(protocol.PersistenceInput, "reserved", 40);
    try std.testing.expectEqual(
        @as(usize, 96),
        @offsetOf(protocol.PersistenceHeader, "source_generations"),
    );
    try expectOffset(protocol.PersistenceHeader, "phase", 148);
    try std.testing.expectEqual(
        @as(usize, 152),
        @offsetOf(protocol.PersistenceHeader, "checksum"),
    );
    try expectOffset(protocol.PersistenceHeader, "last_monotonic_ms", 168);
    try expectOffset(protocol.PersistenceHeader, "reserved", 176);
}

test "display coordinator v3 published enum and flag values" {
    try std.testing.expectEqual(@as(u32, 1), @intFromEnum(protocol.SourceStatus.complete));
    try std.testing.expectEqual(@as(u32, 2), @intFromEnum(protocol.SourceStatus.retained));
    try std.testing.expectEqual(@as(u32, 3), @intFromEnum(protocol.SourceStatus.unavailable));
    try std.testing.expectEqual(@as(u32, 5), @intFromEnum(protocol.SourceStatus.unsupported));
    try std.testing.expectEqual(@as(u32, 1), @intFromEnum(protocol.ChangeKind.added));
    try std.testing.expectEqual(@as(u32, 2), @intFromEnum(protocol.ChangeKind.changed));
    try std.testing.expectEqual(@as(u32, 3), @intFromEnum(protocol.ChangeKind.removed));
    try std.testing.expectEqual(@as(u32, 4), @intFromEnum(protocol.EntityKind.text));
    try std.testing.expectEqual(@as(u32, 5), @intFromEnum(protocol.EntityKind.unresolved));
    try std.testing.expectEqual(@as(u32, 5), @intFromEnum(protocol.Phase.failed_no_data));
    try std.testing.expectEqual(@as(u64, 0x1f), protocol.IdentityMask.strong);
    try std.testing.expectEqual(@as(u64, 0x1f), protocol.SourceMask.known);
    try std.testing.expectEqual(@as(u32, 0x0f), protocol.SnapshotFlags.known);
    try std.testing.expectEqual(
        @as(u32, 1),
        protocol.OemMatchFlags.allow_any_active_adapter,
    );
}

test "display coordinator v3 canonical monitor path handle" {
    const raw = "\\\\?\\display#del40a9#5&10abc&0&uid4357";
    var buffer: [128]u8 = undefined;
    const normalized = identity.normalize(.monitor_device_path, raw, &buffer).?;
    try std.testing.expectEqualStrings(
        "DISPLAY\\DEL40A9\\5&10ABC&0&UID4357",
        normalized,
    );
    const handle = identity.textHandle(.monitor_device_path, normalized);
    try std.testing.expectEqual(@as(u64, 0xA24262DA99872968), handle.high);
    try std.testing.expectEqual(@as(u64, 0xF8BA73BD4CE39079), handle.low);
}

fn expectOffset(
    comptime T: type,
    comptime field: []const u8,
    expected: usize,
) !void {
    try std.testing.expectEqual(expected, @offsetOf(T, field));
}
