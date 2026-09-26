const std = @import("std");
const protocol = @import("protocol.zig");

test "report coordinator ABI v6 layout is fixed" {
    try std.testing.expectEqual(@as(u32, 0x0006_0000), protocol.abi_version);
    try std.testing.expectEqual(@as(usize, 216), @sizeOf(protocol.Config));
    try std.testing.expectEqual(@as(usize, 96), @sizeOf(protocol.Capacity));
    try std.testing.expectEqual(@as(usize, 160), @sizeOf(protocol.RuleInput));
    try std.testing.expectEqual(@as(usize, 72), @sizeOf(protocol.RuleReplaceInput));
    try std.testing.expectEqual(@as(usize, 128), @sizeOf(protocol.FactInput));
    try std.testing.expectEqual(@as(usize, 112), @sizeOf(protocol.SourceSnapshotInput));
    try std.testing.expectEqual(@as(usize, 88), @sizeOf(protocol.TrustCommandInput));
    try std.testing.expectEqual(@as(usize, 72), @sizeOf(protocol.ImportInput));
    try std.testing.expectEqual(@as(usize, 80), @sizeOf(protocol.PlanInput));
    try std.testing.expectEqual(@as(usize, 192), @sizeOf(protocol.ReportOutput));
    try std.testing.expectEqual(@as(usize, 352), @sizeOf(protocol.PersistenceOperation));
    try std.testing.expectEqual(
        @as(usize, 72),
        @sizeOf(protocol.PersistenceFeedbackInput),
    );
    try std.testing.expectEqual(@as(usize, 80), @sizeOf(protocol.PersistenceFeedback));
    try std.testing.expectEqual(@as(usize, 176), @sizeOf(protocol.PlanOutput));

    try std.testing.expectEqual(@as(usize, 8), @offsetOf(protocol.Config, "generation"));
    try std.testing.expectEqual(
        @as(usize, 32),
        @offsetOf(protocol.Config, "maximum_source_count"),
    );
    try std.testing.expectEqual(
        @as(usize, 88),
        @offsetOf(protocol.Config, "bucket_width_milliseconds"),
    );
    try std.testing.expectEqual(@as(usize, 144), @offsetOf(protocol.Config, "flags"));
    try std.testing.expectEqual(
        @as(usize, 152),
        @offsetOf(protocol.Config, "maximum_rolling_observation_count"),
    );
    try std.testing.expectEqual(
        @as(usize, 156),
        @offsetOf(protocol.Config, "planned_persistence_index_capacity"),
    );
    try std.testing.expectEqual(
        @as(usize, 160),
        @offsetOf(protocol.Config, "metadata_checkpoint_interval_milliseconds"),
    );
    try std.testing.expectEqual(
        @as(usize, 24),
        @offsetOf(protocol.SourceSnapshotInput, "command_monotonic_milliseconds"),
    );
    try std.testing.expectEqual(
        @as(usize, 40),
        @offsetOf(protocol.PersistenceOperation, "identity_handle"),
    );
    try std.testing.expectEqual(
        @as(usize, 64),
        @offsetOf(protocol.PersistenceOperation, "source_generation"),
    );
    try std.testing.expectEqual(
        @as(usize, 320),
        @offsetOf(protocol.PersistenceOperation, "fact_sequence"),
    );
    try std.testing.expectEqual(
        @as(usize, 328),
        @offsetOf(protocol.PersistenceOperation, "report_observed_at_utc_milliseconds"),
    );
    try std.testing.expectEqual(
        @as(usize, 336),
        @offsetOf(protocol.PersistenceOperation, "checkpoint_schema_version"),
    );
    try std.testing.expectEqual(
        @as(usize, 344),
        @offsetOf(protocol.PersistenceOperation, "checkpoint_logical_utc_milliseconds"),
    );
    try std.testing.expectEqual(
        @as(usize, 136),
        @offsetOf(protocol.RuleInput, "predicate_group_handle"),
    );
    try std.testing.expectEqual(
        @as(usize, 112),
        @offsetOf(protocol.FactInput, "secondary_current_value"),
    );
    try std.testing.expectEqual(
        @as(usize, 48),
        @offsetOf(protocol.TrustCommandInput, "family_handle"),
    );
    try std.testing.expectEqual(
        @as(usize, 112),
        @offsetOf(protocol.PlanOutput, "logical_utc_milliseconds"),
    );

    try std.testing.expectEqual(
        protocol.SourceSnapshotValid.required,
        protocol.SourceSnapshotValid.known,
    );
    try std.testing.expectEqual(protocol.PlanValid.required, protocol.PlanValid.known);
    try std.testing.expectEqual(@as(u64, 0), protocol.ConfigFlags.known);
}
