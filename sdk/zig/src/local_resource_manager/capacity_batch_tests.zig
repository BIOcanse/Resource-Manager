const std = @import("std");
const protocol = @import("protocol.zig");
const layout = @import("layout.zig");
const state = @import("state.zig");
const operation = @import("operation_sequence.zig");
const guard = @import("capacity_guard.zig");
const batch = @import("capacity_batch.zig");
const executor = @import("executor.zig");
const partition = @import("partition.zig");
const abi = @import("abi.zig");
const planning = @import("planning.zig");

fn configuration(capacity: u32) protocol.Config {
    var buckets: u32 = 1;
    while (buckets < capacity * 2) buckets *= 2;
    return .{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.Config),
        .configuration_generation = 1,
        .table_capacity = 4,
        .resource_capacity = capacity,
        .capability_capacity = 4,
        .pending_capacity = 4,
        .uid_bucket_capacity = buckets,
        .partition_capacity = 2,
        .maximum_concurrent_tables = 4,
        .smooth_release_interval_epochs = 2,
        .activity_decay_numerator = 1,
        .activity_decay_denominator = 2,
        .concentrated_trigger_free_percent = 10,
        .concentrated_target_free_percent = 20,
        .smooth_trigger_free_percent = 15,
        .smooth_emergency_free_percent = 5,
        .smooth_maximum_releases_per_interval = 5,
    };
}

fn manager(capacity: u32) ![]align(8) u8 {
    const config = configuration(capacity);
    const buffer = try std.testing.allocator.alignedAlloc(u8, .@"8", try state.requiredBytes(config));
    try state.initialize(buffer, config, 88);
    _ = try operation.begin(try state.open(buffer), .maintenance);
    return buffer;
}

fn table(view: layout.View, id: u64, capacity: u32, reserved: bool) !protocol.TableHandle {
    return tableWithReservation(view, id, capacity, if (reserved) capacity else 0);
}

fn tableWithReservation(view: layout.View, id: u64, capacity: u32, reservation: u32) !protocol.TableHandle {
    const handle = try state.registerTable(view, .{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.TableSpec),
        .table_id = .{ .low = id, .high = 0 },
        .table_incarnation = 1,
        .adapter_key = .{ .low = 0, .high = 0 },
        .topology_generation = 0,
        .capacity = capacity,
        .partition_reservation_capacity = reservation,
        .domain = .memory,
        .reserved0 = .{ 0, 0, 0 },
        .reserved1 = 0,
    });
    _ = try state.registerCapability(view, handle, .{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.CapabilitySpec),
        .capability_id = id,
        .action_code = 1,
        .expected_effects = protocol.effect_releases_ledger_slot,
        .destructive = 1,
        .reserved0 = .{ 0, 0 },
        .reserved1 = 0,
    });
    return handle;
}

fn resource(uid: u64) protocol.ResourceSpec {
    return .{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.ResourceSpec),
        .resource_uid = .{ .low = uid, .high = 0 },
        .size_bytes = 1,
        .recovery_cost_coefficient = 1,
        .recoverability = .recoverable,
        .access_loss_impact = .unobservable_now,
        .reserved0 = @splat(0),
    };
}

fn fresh(view: layout.View, kind: protocol.OperationKind) !protocol.OperationToken {
    try operation.finish(view, operation.currentToken(view));
    return operation.begin(view, kind);
}

fn input(strategy: protocol.CapacityStrategy) protocol.CapacityPlanInput {
    return .{ .abi_version = protocol.abi_version, .struct_size = @sizeOf(protocol.CapacityPlanInput), .strategy = strategy, .reserved0 = @splat(0) };
}

fn release(view: layout.View, intent: protocol.Intent) !void {
    const token = try executor.beginIntent(view, intent);
    _ = try executor.markEffectStarted(view, token);
    _ = try executor.commitEffect(view, token, .{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.TypedEffect),
        .outcome = .applied,
        .changes = protocol.effect_releases_ledger_slot,
        .reserved0 = @splat(0),
        .size_bytes_after = 0,
        .released_bytes = intent.size_bytes,
    });
}

test "bounded capacity ABI handles 512 partition rows with only 64 scratch and output" {
    const buffer = try manager(512);
    defer std.testing.allocator.free(buffer);
    const view = try state.open(buffer);
    const owner = try table(view, 1, 512, true);
    _ = try fresh(view, .partition_admission);
    var victims: [2]protocol.PartitionHandle = undefined;
    var partition_scratch: [2]protocol.PartitionAdmissionScratch = undefined;
    var full: [512]protocol.Intent = undefined;
    const admission = try partition.planAdmission(view, .{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.PartitionCreateInput),
        .table = owner,
        .parent = std.mem.zeroes(protocol.PartitionHandle),
        .capacity = 512,
        .has_parent = 0,
        .reserved0 = @splat(0),
        .reserved1 = 0,
    }, &victims, &full, &partition_scratch);
    const shell = try partition.commitAdmission(view, admission.ticket, &.{});
    _ = try fresh(view, .maintenance);
    for (0..512) |ordinal| _ = try partition.registerResourceAt(view, shell, @intCast(ordinal), resource(ordinal + 1));
    var token = try fresh(view, .cleanup);
    var request = input(.concentrated);
    var scratch: [64]protocol.Intent = undefined;
    var output: [64]protocol.Intent = undefined;
    const before = try std.testing.allocator.dupe(u8, buffer);
    defer std.testing.allocator.free(before);
    try std.testing.expectError(error.BufferTooSmall, guard.plan(view, request, &full, &output));
    var full_scratch: [512]protocol.Intent = undefined;
    const baseline = try guard.plan(view, request, &full_scratch, &full);
    try std.testing.expectEqual(@as(u32, 512), baseline.intent_count);
    var cursor: protocol.CapacityPlanCursor = .{};
    var summary: protocol.PlanSummary = undefined;
    try std.testing.expectEqual(@as(i32, 0), abi.rm_local_resource_manager_plan_capacity_bounded(
        buffer.ptr,
        buffer.len,
        &token,
        &request,
        64,
        &cursor,
        &scratch,
        scratch.len,
        &output,
        output.len,
        &summary,
    ));
    try std.testing.expectEqual(@as(u32, 64), summary.intent_count);
    try std.testing.expectEqualDeep(full[0..64], output[0..64]);
    try std.testing.expectEqualSlices(u8, before, buffer);
    try std.testing.expectEqual(@as(u32, 512), (try partition.readPartition(view, shell)).descendant_resource_count);
}

test "bounded capacity chunks match full semantic ranking and filter use fatal and unrecoverable rows" {
    const buffer = try manager(64);
    defer std.testing.allocator.free(buffer);
    const view = try state.open(buffer);
    for (0..3) |table_number| {
        const owner = try table(view, 10 - table_number, 16, false);
        for (0..16) |ordinal| {
            var spec = resource(table_number * 100 + ordinal + 1);
            spec.size_bytes = (ordinal * 17 % 23) + 1;
            spec.recovery_cost_coefficient = @intCast(ordinal % 5);
            spec.access_loss_impact = if (ordinal % 7 == 0) .fatal else if (ordinal % 3 == 0) .observable_now else .unobservable_now;
            if (ordinal == 2) spec.recoverability = .not_recoverable;
            const handle = try state.registerResource(view, owner, spec);
            if (ordinal == 3) _ = try state.beginUse(view, handle);
            try state.touchResource(view, handle, @intCast(ordinal % 4 + 1));
        }
    }
    _ = try fresh(view, .cleanup);
    var full_scratch: [64]protocol.Intent = undefined;
    var full: [64]protocol.Intent = undefined;
    const baseline = try guard.plan(view, input(.concentrated), &full_scratch, &full);
    for ([_]u32{ 1, 5, 64 }) |maximum| {
        var scratch: [64]protocol.Intent = undefined;
        var output: [64]protocol.Intent = undefined;
        var cursor: protocol.CapacityPlanCursor = .{};
        var offset: usize = 0;
        var calls: usize = 0;
        while (cursor.state != .completed and calls < 64) : (calls += 1) {
            const summary = try batch.plan(view, input(.concentrated), maximum, &cursor, scratch[0..maximum], output[0..maximum]);
            const count: usize = summary.intent_count;
            try std.testing.expectEqualDeep(full[offset .. offset + count], output[0..count]);
            offset += count;
        }
        try std.testing.expectEqual(protocol.CapacityCursorState.completed, cursor.state);
        try std.testing.expectEqual(@as(usize, baseline.intent_count), offset);
    }
}

test "bounded concentrated recovery crosses ten percent and still reaches twenty percent" {
    const buffer = try manager(512);
    defer std.testing.allocator.free(buffer);
    const view = try state.open(buffer);
    const owner = try table(view, 1, 512, false);
    for (0..512) |ordinal| _ = try state.registerResource(view, owner, resource(ordinal + 1));
    _ = try fresh(view, .cleanup);
    var scratch: [64]protocol.Intent = undefined;
    var output: [64]protocol.Intent = undefined;
    var cursor: protocol.CapacityPlanCursor = .{};
    var summary = try batch.plan(view, input(.concentrated), 64, &cursor, &scratch, &output);
    for (output[0..summary.intent_count]) |intent| try release(view, intent);
    try std.testing.expectEqual(@as(u32, 64), (try state.readTableCapacity(view, owner)).free_count);
    summary = try batch.plan(view, input(.concentrated), 64, &cursor, &scratch, &output);
    try std.testing.expectEqual(@as(u32, 39), summary.intent_count);
    var surplus: ?protocol.Intent = null;
    for (view.resources, 0..) |row, slot| {
        if (row.resource_uid.low == 104) surplus = planning.capacityCandidate(view, @intCast(slot), .concentrated_recovery);
    }
    for (output[0..39]) |intent| try release(view, intent);
    try std.testing.expectError(error.StaleIntent, executor.beginIntent(view, surplus.?));
    try std.testing.expectEqual(@as(u32, 103), (try state.readTableCapacity(view, owner)).free_count);
    summary = try batch.plan(view, input(.concentrated), 64, &cursor, &scratch, &output);
    try std.testing.expectEqual(@as(u32, 0), summary.intent_count);
    try std.testing.expectEqual(protocol.CapacityCursorState.completed, cursor.state);
    _ = try state.open(buffer);
}

test "bounded smooth pacing shares original five-intent budget and snapshot across slices" {
    const buffer = try manager(100);
    defer std.testing.allocator.free(buffer);
    const view = try state.open(buffer);
    const owner = try table(view, 1, 100, false);
    for (0..91) |ordinal| _ = try state.registerResource(view, owner, resource(ordinal + 1));
    _ = try fresh(view, .cleanup);
    const snapshot = view.header.snapshot_generation;
    var cursor: protocol.CapacityPlanCursor = .{};
    var scratch: [2]protocol.Intent = undefined;
    var output: [2]protocol.Intent = undefined;
    var total: u32 = 0;
    for ([_]u32{ 2, 2, 1 }) |expected| {
        const summary = try batch.plan(view, input(.smooth), 2, &cursor, &scratch, &output);
        try std.testing.expectEqual(expected, summary.intent_count);
        for (output[0..summary.intent_count]) |intent| {
            try std.testing.expectEqual(snapshot, intent.snapshot_generation);
            try std.testing.expectEqual(protocol.CapacityPhase.smooth_paced, intent.capacity_phase);
            try release(view, intent);
            total += 1;
        }
    }
    try std.testing.expectEqual(@as(u32, 5), total);
    try std.testing.expectEqual(@as(u32, 0), (try batch.plan(view, input(.smooth), 2, &cursor, &scratch, &output)).intent_count);
    cursor = .{};
    try std.testing.expectEqual(@as(u32, 0), (try batch.plan(view, input(.smooth), 2, &cursor, &scratch, &output)).intent_count);
    _ = try state.open(buffer);
}

test "bounded concentrated output stops at projected target and failed actions keep recovery alive" {
    const buffer = try manager(100);
    defer std.testing.allocator.free(buffer);
    const view = try state.open(buffer);
    const owner = try table(view, 1, 100, false);
    for (0..100) |ordinal| _ = try state.registerResource(view, owner, resource(ordinal + 1));
    _ = try fresh(view, .cleanup);
    var cursor: protocol.CapacityPlanCursor = .{};
    var scratch: [32]protocol.Intent = undefined;
    var output: [32]protocol.Intent = undefined;
    var summary = try batch.plan(view, input(.concentrated), 32, &cursor, &scratch, &output);
    try std.testing.expectEqual(@as(u32, 20), summary.intent_count);
    try std.testing.expectEqual(protocol.CapacityCursorState.active, cursor.state);
    for (output[0..10]) |intent| try release(view, intent);
    for (output[10..20]) |intent| {
        const reserved = try executor.beginIntent(view, intent);
        _ = try executor.abortIntent(view, reserved);
    }
    summary = try batch.plan(view, input(.concentrated), 32, &cursor, &scratch, &output);
    try std.testing.expectEqual(@as(u32, 10), summary.intent_count);
    try std.testing.expectEqual(@as(u64, 21), output[0].resource.resource_uid.low);
    for (output[0..10]) |intent| try release(view, intent);
    summary = try batch.plan(view, input(.concentrated), 32, &cursor, &scratch, &output);
    try std.testing.expectEqual(@as(u32, 0), summary.intent_count);
    try std.testing.expectEqual(@as(u32, 20), (try state.readTableCapacity(view, owner)).free_count);
    _ = try state.open(buffer);
}

test "bounded target count does not treat reserved partition slots as ordinary free slots" {
    const buffer = try manager(10);
    defer std.testing.allocator.free(buffer);
    const view = try state.open(buffer);
    const owner = try tableWithReservation(view, 1, 10, 4);
    _ = try fresh(view, .partition_admission);
    var victims: [2]protocol.PartitionHandle = undefined;
    var partition_scratch: [2]protocol.PartitionAdmissionScratch = undefined;
    var output: [32]protocol.Intent = undefined;
    const admission = try partition.planAdmission(view, .{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.PartitionCreateInput),
        .table = owner,
        .parent = std.mem.zeroes(protocol.PartitionHandle),
        .capacity = 4,
        .has_parent = 0,
        .reserved0 = @splat(0),
        .reserved1 = 0,
    }, &victims, &output, &partition_scratch);
    const shell = try partition.commitAdmission(view, admission.ticket, &.{});
    _ = try fresh(view, .maintenance);
    for (0..4) |ordinal| _ = try partition.registerResourceAt(view, shell, @intCast(ordinal), resource(ordinal + 1));
    for (0..6) |ordinal| _ = try state.registerResource(view, owner, resource(ordinal + 11));
    _ = try fresh(view, .cleanup);
    var cursor: protocol.CapacityPlanCursor = .{};
    var scratch: [32]protocol.Intent = undefined;
    const summary = try batch.plan(view, input(.concentrated), 32, &cursor, &scratch, &output);
    // Four ranked reserved rows precede the two ordinary rows needed for ceil(6*20%).
    try std.testing.expectEqual(@as(u32, 6), summary.intent_count);
    try std.testing.expectEqual(@as(u64, 11), output[4].resource.resource_uid.low);
    try std.testing.expectEqual(@as(u64, 12), output[5].resource.resource_uid.low);
}

test "bounded smooth emergency preserves all-candidate phase after first slice leaves emergency" {
    const buffer = try manager(100);
    defer std.testing.allocator.free(buffer);
    const view = try state.open(buffer);
    const owner = try table(view, 1, 100, false);
    for (0..100) |ordinal| _ = try state.registerResource(view, owner, resource(ordinal + 1));
    _ = try fresh(view, .cleanup);
    var cursor: protocol.CapacityPlanCursor = .{};
    var scratch: [64]protocol.Intent = undefined;
    var output: [64]protocol.Intent = undefined;
    for ([_]u32{ 64, 36 }) |expected| {
        const summary = try batch.plan(view, input(.smooth), 64, &cursor, &scratch, &output);
        try std.testing.expectEqual(expected, summary.intent_count);
        for (output[0..summary.intent_count]) |intent| {
            try std.testing.expectEqual(protocol.CapacityPhase.smooth_emergency, intent.capacity_phase);
            try release(view, intent);
        }
    }
    try std.testing.expectEqual(@as(u32, 100), (try state.readTableCapacity(view, owner)).free_count);
    try std.testing.expectEqual(protocol.CapacityCursorState.completed, cursor.state);
    _ = try state.open(buffer);
}

test "bounded capacity ABI rejects bad buffers aliases and identity without publishing" {
    const buffer = try manager(16);
    defer std.testing.allocator.free(buffer);
    const view = try state.open(buffer);
    const owner = try table(view, 1, 16, false);
    for (0..16) |ordinal| _ = try state.registerResource(view, owner, resource(ordinal + 1));
    const original_token = try fresh(view, .cleanup);
    const before = try std.testing.allocator.dupe(u8, buffer);
    defer std.testing.allocator.free(before);
    var scratch: [2]protocol.Intent = undefined;
    var output: [2]protocol.Intent = undefined;
    var summary: protocol.PlanSummary = undefined;
    @memset(std.mem.asBytes(&scratch), 0xA5);
    @memset(std.mem.asBytes(&output), 0xB6);
    @memset(std.mem.asBytes(&summary), 0xC7);
    const scratch_before = scratch;
    const output_before = output;
    const summary_before = summary;
    for (0..11) |case| {
        var token = original_token;
        var request = input(.concentrated);
        var cursor: protocol.CapacityPlanCursor = .{};
        var maximum: u32 = 2;
        var scratch_size: u32 = 2;
        var output_size: u32 = 2;
        var cursor_pointer: ?*protocol.CapacityPlanCursor = &cursor;
        var output_pointer: ?[*]protocol.Intent = &output;
        var summary_pointer: ?*protocol.PlanSummary = &summary;
        var expected = protocol.ResultCode.invalid_argument;
        switch (case) {
            0 => maximum = 0,
            1 => {
                scratch_size = 1;
                expected = .buffer_too_small;
            },
            2 => {
                output_size = 1;
                expected = .buffer_too_small;
            },
            3 => cursor.reserved0[0] = 1,
            4 => request.reserved0[0] = 1,
            5 => {
                token.operation_id += 1;
                expected = .stale_operation;
            },
            6 => output_pointer = @ptrCast(&cursor),
            7 => cursor_pointer = @ptrCast(buffer.ptr),
            8 => summary_pointer = @ptrCast(&scratch),
            9 => output_pointer = @ptrCast(&token),
            10 => cursor_pointer = null,
            else => unreachable,
        }
        const cursor_before = cursor;
        try std.testing.expectEqual(@intFromEnum(expected), abi.rm_local_resource_manager_plan_capacity_bounded(
            buffer.ptr,
            buffer.len,
            &token,
            &request,
            maximum,
            cursor_pointer,
            &scratch,
            scratch_size,
            output_pointer,
            output_size,
            summary_pointer,
        ));
        try std.testing.expectEqualDeep(cursor_before, cursor);
        try std.testing.expectEqualDeep(scratch_before, scratch);
        try std.testing.expectEqualDeep(output_before, output);
        try std.testing.expectEqualDeep(summary_before, summary);
        try std.testing.expectEqualSlices(u8, before, buffer);
    }
}

test "bounded capacity continuation stops for pending recovery stale operations and changed epoch" {
    const buffer = try manager(16);
    defer std.testing.allocator.free(buffer);
    const view = try state.open(buffer);
    const owner = try table(view, 1, 16, false);
    for (0..16) |ordinal| _ = try state.registerResource(view, owner, resource(ordinal + 1));
    _ = try fresh(view, .cleanup);
    var scratch: [2]protocol.Intent = undefined;
    var output: [2]protocol.Intent = undefined;
    var cursor: protocol.CapacityPlanCursor = .{};
    _ = try batch.plan(view, input(.concentrated), 2, &cursor, &scratch, &output);
    const cursor_before = cursor;
    const output_before = output;
    const execution = try executor.beginIntent(view, output[0]);
    try std.testing.expectError(error.ResourceBusy, batch.plan(view, input(.concentrated), 2, &cursor, &scratch, &output));
    _ = try executor.markEffectStarted(view, execution);
    _ = try executor.commitEffect(view, execution, .{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.TypedEffect),
        .outcome = .effect_unknown,
        .changes = 0,
        .reserved0 = @splat(0),
        .size_bytes_after = 0,
        .released_bytes = 0,
    });
    try std.testing.expectError(error.RecoveryRequired, batch.plan(view, input(.concentrated), 2, &cursor, &scratch, &output));
    try std.testing.expectEqualDeep(cursor_before, cursor);
    try std.testing.expectEqualDeep(output_before, output);
    _ = try executor.commitEffect(view, execution, .{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.TypedEffect),
        .outcome = .applied,
        .changes = protocol.effect_releases_ledger_slot,
        .reserved0 = @splat(0),
        .size_bytes_after = 0,
        .released_bytes = 1,
    });
    _ = try fresh(view, .cleanup);
    try std.testing.expectError(error.StaleOperation, batch.plan(view, input(.concentrated), 2, &cursor, &scratch, &output));
    cursor = .{};
    _ = try batch.plan(view, input(.concentrated), 2, &cursor, &scratch, &output);
    try state.advanceActivityEpoch(view, 1);
    const after_epoch = try std.testing.allocator.dupe(u8, buffer);
    defer std.testing.allocator.free(after_epoch);
    try std.testing.expectError(error.StaleIntent, batch.plan(view, input(.concentrated), 2, &cursor, &scratch, &output));
    try std.testing.expectEqualSlices(u8, after_epoch, buffer);
    try std.testing.expectEqual(@as(u32, 15), (try state.readTableCapacity(view, owner)).occupied_count);
    _ = try state.open(buffer);
}
