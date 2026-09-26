const std = @import("std");
const protocol = @import("protocol.zig");
const state = @import("state.zig");

fn config(capacity: u32) protocol.Config {
    return .{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.Config),
        .configuration_generation = 1,
        .capacity = capacity,
        .maximum_snapshot_age_milliseconds = 30_000,
        .maximum_future_skew_milliseconds = 5_000,
        .reserved0 = 0,
        .activity_settlement_interval = 5,
        .activity_increment = 64,
        .activity_decay_numerator = 3,
        .activity_decay_denominator = 4,
        .reserved1 = .{ 0, 0, 0, 0, 0 },
    };
}

fn snapshot(sequence: u64) protocol.SnapshotInput {
    return .{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.SnapshotInput),
        .owner_application_key = 11,
        .owner_process_instance_key = 12,
        .source_sequence = sequence,
        .captured_at_unix_milliseconds = 99_000 + @as(i64, @intCast(sequence)),
        .observed_at_monotonic = 100 + sequence,
        .now_unix_milliseconds = 100_000,
        .owner_process_id = 123,
        .schema_version = protocol.snapshot_schema_version,
        .surface_state = .background_window,
        .import_flags = protocol.import_activity_flag | protocol.import_demand_flag,
        .reserved0 = .{ 0, 0, 0, 0, 0, 0, 0, 0, 0 },
    };
}

fn resource(key: u64, id: u32, tier: protocol.ResourceTier) protocol.ResourceInput {
    return .{
        .resource_key = key,
        .size_bytes = 4096,
        .resource_id = id,
        .tier = tier,
        .resource_kind = if (tier == .vram) .texture else .cache,
        .recovery_kind = .built_data,
        .granularity = .partial_usable,
        .inapplicable_actions = 0,
        .action_route = .adapter_handler,
        .reserved0 = 0,
        .activity_score = 0,
        .demand_mask = 0,
        .reserved1 = .{ 0, 0, 0 },
    };
}

test "private ledger settles only real touches and preserves explicit demand" {
    const allocator = std.testing.allocator;
    const bytes = try state.requiredBytes(4);
    const buffer = try allocator.alignedAlloc(u8, .fromByteUnits(state.alignment()), bytes);
    defer allocator.free(buffer);
    try state.initialize(buffer, config(4));
    var required = resource(1001, 1, .physical_memory);
    required.demand_mask = 1;
    const imported = try state.importSnapshot(buffer, snapshot(1), &.{
        required,
        resource(1002, 2, .vram),
    });
    try std.testing.expect(imported.ledger_instance_id != 0);
    try std.testing.expectEqual(@as(u32, 2), imported.resource_count);

    _ = try state.touchResources(buffer, &.{2});
    _ = try state.settleActivity(buffer, 10);
    var views: [4]protocol.ResourceView = undefined;
    const read = try state.readSnapshot(buffer, &views);
    try std.testing.expectEqual(@as(u32, 2), read.resource_count);
    try std.testing.expectEqual(@as(u8, 0), views[0].value.activity_score);
    try std.testing.expectEqual(@as(u8, 1), views[0].value.demand_mask & 1);
    try std.testing.expectEqual(@as(u8, 48), views[1].value.activity_score);
    try std.testing.expectEqual(@as(u8, 0), views[1].value.demand_mask);
    try std.testing.expectEqual(imported.ledger_instance_id, read.ledger_instance_id);
}

test "private ledger rejects unknown touch without changing activity" {
    const allocator = std.testing.allocator;
    const bytes = try state.requiredBytes(1);
    const buffer = try allocator.alignedAlloc(u8, .fromByteUnits(state.alignment()), bytes);
    defer allocator.free(buffer);
    try state.initialize(buffer, config(1));
    _ = try state.importSnapshot(buffer, snapshot(1), &.{resource(1, 1, .physical_memory)});

    try std.testing.expectError(error.ResourceNotFound, state.touchResources(buffer, &.{ 1, 2 }));
    _ = try state.settleActivity(buffer, 10);
    var views: [1]protocol.ResourceView = undefined;
    _ = try state.readSnapshot(buffer, &views);
    try std.testing.expectEqual(@as(u8, 0), views[0].value.activity_score);
}

test "private ledger instance identity is stable per session and changes after recreation" {
    const allocator = std.testing.allocator;
    const bytes = try state.requiredBytes(1);
    const first = try allocator.alignedAlloc(u8, .fromByteUnits(state.alignment()), bytes);
    defer allocator.free(first);
    const second = try allocator.alignedAlloc(u8, .fromByteUnits(state.alignment()), bytes);
    defer allocator.free(second);

    try state.initialize(first, config(1));
    try state.initialize(second, config(1));
    const first_import = try state.importSnapshot(first, snapshot(1), &.{resource(1, 1, .physical_memory)});
    var first_views: [1]protocol.ResourceView = undefined;
    const first_read = try state.readSnapshot(first, &first_views);
    const second_import = try state.importSnapshot(second, snapshot(1), &.{resource(1, 1, .physical_memory)});

    try std.testing.expect(first_import.ledger_instance_id != 0);
    try std.testing.expectEqual(first_import.ledger_instance_id, first_read.ledger_instance_id);
    try std.testing.expect(first_import.ledger_instance_id != second_import.ledger_instance_id);
}

test "private ledger rejects conflicting replay and preserves last good snapshot" {
    const allocator = std.testing.allocator;
    const bytes = try state.requiredBytes(2);
    const buffer = try allocator.alignedAlloc(u8, .fromByteUnits(state.alignment()), bytes);
    defer allocator.free(buffer);
    try state.initialize(buffer, config(2));
    _ = try state.importSnapshot(buffer, snapshot(7), &.{resource(1, 1, .physical_memory)});
    try std.testing.expectError(
        error.SameSequenceConflict,
        state.importSnapshot(buffer, snapshot(7), &.{resource(2, 2, .physical_memory)}),
    );
    var views: [2]protocol.ResourceView = undefined;
    const read = try state.readSnapshot(buffer, &views);
    try std.testing.expectEqual(@as(u64, 1), views[0].value.resource_key);
    try std.testing.expect(read.fingerprint != 0);
}

test "private ledger accepts declared non-resident resources" {
    const allocator = std.testing.allocator;
    const bytes = try state.requiredBytes(1);
    const buffer = try allocator.alignedAlloc(u8, .fromByteUnits(state.alignment()), bytes);
    defer allocator.free(buffer);
    try state.initialize(buffer, config(1));
    var declared = resource(1, 1, .virtual_memory);
    declared.size_bytes = 0;
    _ = try state.importSnapshot(buffer, snapshot(1), &.{declared});
    var views: [1]protocol.ResourceView = undefined;
    _ = try state.readSnapshot(buffer, &views);
    try std.testing.expectEqual(@as(u64, 0), views[0].value.size_bytes);
}

test "private ledger invalidates a resource reference after slot reuse" {
    const allocator = std.testing.allocator;
    const bytes = try state.requiredBytes(1);
    const buffer = try allocator.alignedAlloc(u8, .fromByteUnits(state.alignment()), bytes);
    defer allocator.free(buffer);
    try state.initialize(buffer, config(1));
    _ = try state.importSnapshot(buffer, snapshot(1), &.{resource(1, 1, .vram)});
    var views: [1]protocol.ResourceView = undefined;
    _ = try state.readSnapshot(buffer, &views);
    const stale = views[0].resource;
    _ = try state.importSnapshot(buffer, snapshot(2), &.{resource(2, 2, .physical_memory)});
    try std.testing.expectError(error.StaleResourceReference, state.applyActionFeedback(buffer, .{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.ActionFeedback),
        .resource = stale,
        .action = .move_down,
        .status = .completed,
        .current_tier = .physical_memory,
        .preferred_gpu_kind = .texture,
        .reserved0 = 0,
        .resident_bytes = 4096,
        .released_bytes = 0,
    }));
}

test "private ledger applies move feedback in Zig" {
    const allocator = std.testing.allocator;
    const bytes = try state.requiredBytes(1);
    const buffer = try allocator.alignedAlloc(u8, .fromByteUnits(state.alignment()), bytes);
    defer allocator.free(buffer);
    try state.initialize(buffer, config(1));
    _ = try state.importSnapshot(buffer, snapshot(1), &.{resource(1, 1, .vram)});
    var views: [1]protocol.ResourceView = undefined;
    _ = try state.readSnapshot(buffer, &views);
    _ = try state.applyActionFeedback(buffer, .{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.ActionFeedback),
        .resource = views[0].resource,
        .action = .move_down,
        .status = .completed,
        .current_tier = .physical_memory,
        .preferred_gpu_kind = .texture,
        .reserved0 = 0,
        .resident_bytes = 2048,
        .released_bytes = 2048,
    });
    _ = try state.readSnapshot(buffer, &views);
    try std.testing.expectEqual(protocol.ResourceTier.physical_memory, views[0].value.tier);
    try std.testing.expectEqual(protocol.ResourceKind.staging_buffer, views[0].value.resource_kind);
    try std.testing.expectEqual(@as(u64, 2048), views[0].value.size_bytes);
}
