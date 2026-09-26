const std = @import("std");
const protocol = @import("protocol.zig");

test "portable registry ABI v1 sizes offsets masks and enums are fixed" {
    try std.testing.expectEqual(@as(u32, 0x0001_0000), protocol.abi_version);
    try expectSize(protocol.Config, 112);
    try expectSize(protocol.Capacity, 88);
    try expectSize(protocol.ImportInput, 80);
    try expectSize(protocol.PersistedPathInput, 96);
    try expectSize(protocol.ObserveInput, 144);
    try expectSize(protocol.ConfirmRootInput, 88);
    try expectSize(protocol.MarkMissingInput, 80);
    try expectSize(protocol.PlanPersistenceInput, 64);
    try expectSize(protocol.PersistenceOperation, 88);
    try expectSize(protocol.PersistencePlanOutput, 96);
    try expectSize(protocol.PersistenceFeedbackInput, 64);
    try expectSize(protocol.PersistenceFeedback, 48);
    try expectSize(protocol.SnapshotInput, 72);
    try expectSize(protocol.RegistrationSnapshot, 80);
    try expectSize(protocol.PathSnapshot, 80);
    try expectSize(protocol.SnapshotOutput, 144);

    try expectOffset(protocol.Config, "generation", 8);
    try expectOffset(protocol.Config, "resident_byte_budget", 64);
    try expectOffset(protocol.Capacity, "resident_byte_count", 48);
    try expectOffset(protocol.ImportInput, "valid_mask", 48);
    try expectOffset(protocol.PersistedPathInput, "executable_path_offset", 64);
    try expectOffset(protocol.ObserveInput, "valid_mask", 112);
    try expectOffset(protocol.ConfirmRootInput, "valid_mask", 56);
    try expectOffset(protocol.MarkMissingInput, "valid_mask", 48);
    try expectOffset(protocol.PlanPersistenceInput, "valid_mask", 32);
    try expectOffset(protocol.PersistenceOperation, "mutation_version", 8);
    try expectOffset(protocol.PersistencePlanOutput, "first_mutation_version", 48);
    try expectOffset(protocol.PersistenceFeedbackInput, "valid_mask", 32);
    try expectOffset(protocol.PersistenceFeedback, "mutation_version", 8);
    try expectOffset(protocol.SnapshotInput, "valid_mask", 40);
    try expectOffset(protocol.RegistrationSnapshot, "first_observed_utc_ms", 40);
    try expectOffset(protocol.PathSnapshot, "first_observed_utc_ms", 40);
    try expectOffset(protocol.SnapshotOutput, "resident_byte_count", 120);

    try std.testing.expectEqual(@as(u32, 3), protocol.PathFlags.known);
    try std.testing.expectEqual(@as(u32, 7), protocol.PersistenceFlags.known);
    try std.testing.expectEqual(@as(u32, 15), protocol.SnapshotFlags.known);
    try std.testing.expectEqual(@as(u32, 3), protocol.PlanOutputFlags.known);
    try std.testing.expectEqual(@as(u32, 7), protocol.SnapshotOutputFlags.known);
    try std.testing.expectEqual(@as(u64, 31), protocol.ImportValid.required);
    try std.testing.expectEqual(@as(u64, 63), protocol.ObserveValid.required);
    try std.testing.expectEqual(@as(u64, 15), protocol.ConfirmRootValid.required);
    try std.testing.expectEqual(@as(u64, 15), protocol.MarkMissingValid.required);
    try std.testing.expectEqual(@as(u64, 3), protocol.PlanValid.required);
    try std.testing.expectEqual(@as(u64, 3), protocol.FeedbackValid.required);
    try std.testing.expectEqual(@as(u64, 7), protocol.SnapshotValid.required);
}

fn expectSize(comptime T: type, expected: usize) !void {
    try std.testing.expectEqual(expected, @sizeOf(T));
}

fn expectOffset(comptime T: type, comptime field: []const u8, expected: usize) !void {
    try std.testing.expectEqual(expected, @offsetOf(T, field));
}
