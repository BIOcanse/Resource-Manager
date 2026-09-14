const std = @import("std");
const protocol = @import("protocol.zig");
const state = @import("state.zig");
const layout = @import("layout.zig");
const rank = @import("rank.zig");
const capacity_guard = @import("capacity_guard.zig");
const mode_cleanup = @import("mode_cleanup.zig");
const intent_merge = @import("intent_merge.zig");
const executor = @import("executor.zig");
const identity = @import("identity.zig");
const abi = @import("abi.zig");
const partition = @import("partition.zig");
const operation_sequence = @import("operation_sequence.zig");

fn config(resource_capacity: u32) protocol.Config {
    var buckets: u32 = 1;
    while (buckets < resource_capacity * 2) buckets *= 2;
    return .{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.Config),
        .configuration_generation = 1,
        .table_capacity = 4,
        .resource_capacity = resource_capacity,
        .capability_capacity = 16,
        .pending_capacity = 8,
        .uid_bucket_capacity = buckets,
        .maximum_concurrent_tables = 4,
        .smooth_release_interval_epochs = 2,
        .activity_decay_numerator = 1,
        .activity_decay_denominator = 2,
        .concentrated_trigger_free_percent = 10,
        .concentrated_target_free_percent = 20,
        .smooth_trigger_free_percent = 15,
        .smooth_emergency_free_percent = 5,
        .smooth_maximum_releases_per_interval = 1,
    };
}

fn tableSpec(id: u64, incarnation: u64, capacity: u32) protocol.TableSpec {
    return .{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.TableSpec),
        .table_id = .{ .low = id, .high = 0 },
        .table_incarnation = incarnation,
        .adapter_key = .{ .low = 0, .high = 0 },
        .topology_generation = 0,
        .capacity = capacity,
        .domain = .memory,
        .reserved0 = .{ 0, 0, 0 },
        .reserved1 = 0,
    };
}

fn partitionTableSpec(
    id: u64,
    incarnation: u64,
    capacity: u32,
    reservation_start: u32,
    reservation_capacity: u32,
) protocol.TableSpec {
    var spec = tableSpec(id, incarnation, capacity);
    spec.partition_reservation_start = reservation_start;
    spec.partition_reservation_capacity = reservation_capacity;
    return spec;
}

fn capabilitySpec(id: u64, action: u32, effects: u8, destructive: bool) protocol.CapabilitySpec {
    return .{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.CapabilitySpec),
        .capability_id = id,
        .action_code = action,
        .expected_effects = effects,
        .destructive = if (destructive) 1 else 0,
        .reserved0 = .{ 0, 0 },
        .reserved1 = 0,
    };
}

fn resourceSpec(
    uid: u64,
    bytes: u64,
    recovery_cost_coefficient: u32,
    activity_value: u32,
    impact: protocol.AccessLossImpact,
    mode: protocol.CapabilityBinding,
    capacity: protocol.CapabilityBinding,
) protocol.ResourceSpec {
    _ = activity_value;
    _ = mode;
    _ = capacity;
    return .{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.ResourceSpec),
        .resource_uid = .{ .low = uid, .high = 0 },
        .size_bytes = bytes,
        .recovery_cost_coefficient = recovery_cost_coefficient,
        .recoverability = .recoverable,
        .access_loss_impact = impact,
        .reserved0 = .{ 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 },
    };
}

fn allocateIdleManager(allocator: std.mem.Allocator, value: protocol.Config) ![]align(8) u8 {
    const bytes = try state.requiredBytes(value);
    const buffer = try allocator.alignedAlloc(u8, .fromByteUnits(state.alignment()), bytes);
    try state.initialize(buffer, value, 77);
    return buffer;
}

fn allocateManager(allocator: std.mem.Allocator, value: protocol.Config) ![]align(8) u8 {
    const buffer = try allocateIdleManager(allocator, value);
    const view = try state.open(buffer);
    _ = try operation_sequence.begin(view, .maintenance);
    return buffer;
}

fn partitionConfig(resource_capacity: u32, partition_capacity: u32) protocol.Config {
    var value = config(resource_capacity);
    value.partition_capacity = partition_capacity;
    return value;
}

fn partitionInput(
    table: protocol.TableHandle,
    parent: ?protocol.PartitionHandle,
    capacity: u32,
) protocol.PartitionCreateInput {
    return .{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.PartitionCreateInput),
        .table = table,
        .parent = parent orelse std.mem.zeroes(protocol.PartitionHandle),
        .capacity = capacity,
        .has_parent = if (parent == null) 0 else 1,
        .reserved0 = .{ 0, 0, 0 },
        .reserved1 = 0,
    };
}

fn beginFreshOperation(view: layout.View, kind: protocol.OperationKind) !void {
    if (view.header.active_operation_phase != .inactive) {
        try operation_sequence.finish(view, operation_sequence.currentToken(view));
    }
    _ = try operation_sequence.begin(view, kind);
}

fn planPartition(
    view: layout.View,
    input: protocol.PartitionCreateInput,
    victims: []protocol.PartitionHandle,
    intents: []protocol.Intent,
) !partition.AdmissionPlan {
    try beginFreshOperation(view, .partition_admission);
    var scratch: [1024]protocol.PartitionAdmissionScratch = undefined;
    if (view.partitions.len > scratch.len) return error.TestPartitionScratchTooSmall;
    return partition.planAdmission(
        view,
        input,
        victims,
        intents,
        scratch[0..view.partitions.len],
    );
}

fn createEmptyPartition(
    view: layout.View,
    table: protocol.TableHandle,
    parent: ?protocol.PartitionHandle,
    capacity: u32,
) !protocol.PartitionHandle {
    var victims: [1024]protocol.PartitionHandle = undefined;
    var intents: [1024]protocol.Intent = undefined;
    const admission = try planPartition(
        view,
        partitionInput(table, parent, capacity),
        &victims,
        &intents,
    );
    if (admission.summary.victim_count != 0 or admission.summary.intent_count != 0) {
        return error.UnexpectedPartitionReclaim;
    }
    const handle = try partition.commitAdmission(view, admission.ticket, victims[0..0]);
    try beginFreshOperation(view, .maintenance);
    return handle;
}

fn closeEmptyPartition(
    view: layout.View,
    target: protocol.PartitionHandle,
) !void {
    try beginFreshOperation(view, .partition_admission);
    var intents: [1024]protocol.Intent = undefined;
    const admission = try partition.planClose(view, target, &intents);
    if (admission.summary.intent_count != 0 or admission.summary.victim_count != 1) {
        return error.UnexpectedPartitionClosePlan;
    }
    try partition.commitClose(view, admission.ticket, target);
    try beginFreshOperation(view, .maintenance);
}

fn planPartitionResource(
    view: layout.View,
    shell: protocol.PartitionHandle,
    resource: protocol.ResourceSpec,
) !protocol.PartitionResourceAdmission {
    try beginFreshOperation(view, .partition_resource_admission);
    return partition.planResourceAdmission(view, shell, resource);
}

fn registerPartitionReleaseCapability(
    view: layout.View,
    table: protocol.TableHandle,
) !void {
    _ = try state.registerCapability(
        view,
        table,
        capabilitySpec(700, 701, protocol.effect_releases_ledger_slot, true),
    );
}

fn releasePartitionIntent(view: layout.View, intent: protocol.Intent) !void {
    const token = try executor.beginIntent(view, intent);
    _ = try executor.markEffectStarted(view, token);
    const receipt = try executor.commitEffect(view, token, appliedSlotRelease());
    if (receipt.resource_slot_released != 1 or receipt.effect_uncertain != 0) {
        return error.UnexpectedPartitionEffect;
    }
}

test "capacity policy rejects equal or inverted adjacent thresholds" {
    const Cases = [_][4]u32{
        .{ 10, 10, 15, 20 },
        .{ 11, 10, 15, 20 },
        .{ 5, 10, 10, 20 },
        .{ 5, 11, 10, 20 },
        .{ 5, 10, 20, 20 },
        .{ 5, 10, 21, 20 },
    };
    for (Cases) |case| {
        var invalid = config(4);
        invalid.smooth_emergency_free_percent = case[0];
        invalid.concentrated_trigger_free_percent = case[1];
        invalid.smooth_trigger_free_percent = case[2];
        invalid.concentrated_target_free_percent = case[3];
        try std.testing.expect(!protocol.validConfig(invalid));
    }
}

test "operation sequence rejects duplicate begin and stale owner tokens atomically" {
    const allocator = std.testing.allocator;
    const buffer = try allocateIdleManager(allocator, config(2));
    defer allocator.free(buffer);
    const view = try state.open(buffer);

    const first = try operation_sequence.begin(view, .cleanup);
    try std.testing.expectEqual(@as(u64, 1), first.operation_id);
    try std.testing.expectEqual(@as(u64, 1), first.operation_generation);
    try std.testing.expectEqual(protocol.OperationPhase.planning, operation_sequence.read(view).phase);

    const active_state = try allocator.dupe(u8, buffer);
    defer allocator.free(active_state);
    try std.testing.expectError(
        error.OperationActive,
        operation_sequence.begin(view, .maintenance),
    );
    try std.testing.expectEqualSlices(u8, active_state, buffer);

    var forged = first;
    forged.operation_generation += 1;
    try std.testing.expectError(error.StaleOperation, operation_sequence.finish(view, forged));
    try std.testing.expectEqualSlices(u8, active_state, buffer);

    try operation_sequence.finish(view, first);
    const second = try operation_sequence.begin(view, .maintenance);
    try std.testing.expectEqual(@as(u64, 2), second.operation_id);
    try std.testing.expectEqual(@as(u64, 2), second.operation_generation);
    try std.testing.expectError(
        error.StaleOperation,
        operation_sequence.requireCurrent(view, first),
    );
    try operation_sequence.finish(view, second);
    try std.testing.expectEqual(protocol.OperationPhase.inactive, operation_sequence.read(view).phase);
}

test "ABI v10 requires the exact operation token and operation kind" {
    const allocator = std.testing.allocator;
    const buffer = try allocateIdleManager(allocator, config(2));
    defer allocator.free(buffer);
    const view = try state.open(buffer);
    const maintenance = try operation_sequence.begin(view, .maintenance);
    const spec = tableSpec(7001, 1, 1);

    var output = std.mem.zeroes(protocol.TableHandle);
    output.slot_index = 0xABCD_EF01;
    const output_before = output;
    const state_before = try allocator.dupe(u8, buffer);
    defer allocator.free(state_before);
    var forged = maintenance;
    forged.operation_id += 1;
    try std.testing.expectEqual(
        @intFromEnum(protocol.ResultCode.stale_operation),
        abi.rm_local_resource_manager_register_table(
            @ptrCast(buffer.ptr),
            @intCast(buffer.len),
            &forged,
            &spec,
            &output,
        ),
    );
    try std.testing.expectEqualDeep(output_before, output);
    try std.testing.expectEqualSlices(u8, state_before, buffer);

    try std.testing.expectEqual(
        @intFromEnum(protocol.ResultCode.ok),
        abi.rm_local_resource_manager_register_table(
            @ptrCast(buffer.ptr),
            @intCast(buffer.len),
            &maintenance,
            &spec,
            &output,
        ),
    );
    try operation_sequence.finish(view, maintenance);
    const cleanup = try operation_sequence.begin(view, .cleanup);
    const cleanup_state = try allocator.dupe(u8, buffer);
    defer allocator.free(cleanup_state);
    var rejected_output = output_before;
    try std.testing.expectEqual(
        @intFromEnum(protocol.ResultCode.invalid_operation_phase),
        abi.rm_local_resource_manager_register_table(
            @ptrCast(buffer.ptr),
            @intCast(buffer.len),
            &cleanup,
            &spec,
            &rejected_output,
        ),
    );
    try std.testing.expectEqualDeep(output_before, rejected_output);
    try std.testing.expectEqualSlices(u8, cleanup_state, buffer);
    try operation_sequence.finish(view, cleanup);
}

test "table and resource registration ABI reject pointer aliasing before mutation" {
    const allocator = std.testing.allocator;
    const buffer = try allocateIdleManager(allocator, config(4));
    defer allocator.free(buffer);
    const view = try state.open(buffer);
    const operation = try operation_sequence.begin(view, .maintenance);
    const table_spec = tableSpec(7003, 1, 2);

    var table_output = std.mem.zeroes(protocol.TableHandle);
    table_output.slot_index = 0xA5A5_A5A5;
    const table_output_before = table_output;
    var state_before = try allocator.dupe(u8, buffer);
    defer allocator.free(state_before);
    const state_table_spec: *const protocol.TableSpec = @ptrCast(@alignCast(buffer.ptr));
    try std.testing.expectEqual(
        @intFromEnum(protocol.ResultCode.invalid_argument),
        abi.rm_local_resource_manager_register_table(
            @ptrCast(buffer.ptr),
            @intCast(buffer.len),
            &operation,
            state_table_spec,
            &table_output,
        ),
    );
    try std.testing.expectEqualDeep(table_output_before, table_output);
    try std.testing.expectEqualSlices(u8, state_before, buffer);

    const table_overlap_size = @max(
        @sizeOf(protocol.TableSpec),
        @sizeOf(protocol.TableHandle),
    );
    var table_overlap: [table_overlap_size]u8 align(@max(
        @alignOf(protocol.TableSpec),
        @alignOf(protocol.TableHandle),
    )) = undefined;
    @memset(&table_overlap, 0x3C);
    @memcpy(table_overlap[0..@sizeOf(protocol.TableSpec)], std.mem.asBytes(&table_spec));
    const table_overlap_before = table_overlap;
    const overlapping_table_spec: *const protocol.TableSpec = @ptrCast(&table_overlap);
    const overlapping_table_output: *protocol.TableHandle = @ptrCast(&table_overlap);
    try std.testing.expectEqual(
        @intFromEnum(protocol.ResultCode.invalid_argument),
        abi.rm_local_resource_manager_register_table(
            @ptrCast(buffer.ptr),
            @intCast(buffer.len),
            &operation,
            overlapping_table_spec,
            overlapping_table_output,
        ),
    );
    try std.testing.expectEqualDeep(table_overlap_before, table_overlap);
    try std.testing.expectEqualSlices(u8, state_before, buffer);

    try std.testing.expectEqual(
        @intFromEnum(protocol.ResultCode.ok),
        abi.rm_local_resource_manager_register_table(
            @ptrCast(buffer.ptr),
            @intCast(buffer.len),
            &operation,
            &table_spec,
            &table_output,
        ),
    );
    _ = try state.registerCapability(
        view,
        table_output,
        capabilitySpec(7003, 7003, protocol.effect_releases_ledger_slot, true),
    );
    const resource_spec = resourceSpec(
        7003,
        4096,
        1,
        0,
        .unobservable_now,
        std.mem.zeroes(protocol.CapabilityBinding),
        std.mem.zeroes(protocol.CapabilityBinding),
    );
    var resource_output = std.mem.zeroes(protocol.ResourceHandle);
    resource_output.resource_slot_index = 0x5A5A_5A5A;
    const resource_output_before = resource_output;
    allocator.free(state_before);
    state_before = try allocator.dupe(u8, buffer);
    const state_resource_spec: *const protocol.ResourceSpec = @ptrCast(@alignCast(buffer.ptr));
    try std.testing.expectEqual(
        @intFromEnum(protocol.ResultCode.invalid_argument),
        abi.rm_local_resource_manager_register_resource(
            @ptrCast(buffer.ptr),
            @intCast(buffer.len),
            &operation,
            &table_output,
            state_resource_spec,
            &resource_output,
        ),
    );
    try std.testing.expectEqualDeep(resource_output_before, resource_output);
    try std.testing.expectEqualSlices(u8, state_before, buffer);

    const resource_overlap_size = @max(
        @sizeOf(protocol.ResourceSpec),
        @sizeOf(protocol.ResourceHandle),
    );
    var resource_overlap: [resource_overlap_size]u8 align(@max(
        @alignOf(protocol.ResourceSpec),
        @alignOf(protocol.ResourceHandle),
    )) = undefined;
    @memset(&resource_overlap, 0xC3);
    @memcpy(resource_overlap[0..@sizeOf(protocol.ResourceSpec)], std.mem.asBytes(&resource_spec));
    const resource_overlap_before = resource_overlap;
    const overlapping_resource_spec: *const protocol.ResourceSpec = @ptrCast(&resource_overlap);
    const overlapping_resource_output: *protocol.ResourceHandle = @ptrCast(&resource_overlap);
    try std.testing.expectEqual(
        @intFromEnum(protocol.ResultCode.invalid_argument),
        abi.rm_local_resource_manager_register_resource(
            @ptrCast(buffer.ptr),
            @intCast(buffer.len),
            &operation,
            &table_output,
            overlapping_resource_spec,
            overlapping_resource_output,
        ),
    );
    try std.testing.expectEqualDeep(resource_overlap_before, resource_overlap);
    try std.testing.expectEqualSlices(u8, state_before, buffer);
}

test "started unknown effect keeps the original operation in recovery" {
    const allocator = std.testing.allocator;
    const buffer = try allocateManager(allocator, config(1));
    defer allocator.free(buffer);
    const view = try state.open(buffer);
    const table = try state.registerTable(view, tableSpec(7002, 1, 1));
    const release = try state.registerCapability(
        view,
        table,
        capabilitySpec(7002, 7002, protocol.effect_releases_ledger_slot, true),
    );
    const resource = try state.registerResource(
        view,
        table,
        resourceSpec(
            7002,
            4096,
            1,
            0,
            .unobservable_now,
            state.bindingFromHandle(release),
            state.bindingFromHandle(release),
        ),
    );
    try beginFreshOperation(view, .cleanup);

    const input = protocol.CapacityPlanInput{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.CapacityPlanInput),
        .strategy = .concentrated,
        .reserved0 = .{ 0, 0, 0, 0, 0, 0, 0 },
    };
    var scratch: [1]protocol.Intent = undefined;
    var intents: [1]protocol.Intent = undefined;
    var summary = try capacity_guard.plan(view, input, &scratch, &intents);
    try std.testing.expectEqual(@as(u32, 1), summary.intent_count);
    var execution = try executor.beginIntent(view, intents[0]);
    var operation_view = operation_sequence.read(view);
    try std.testing.expectEqual(@as(u32, 1), operation_view.reserved_execution_count);
    try std.testing.expectEqual(@as(u32, 0), operation_view.started_execution_count);
    _ = try executor.abortIntent(view, execution);
    try operation_sequence.finish(view, operation_view.token);

    const cleanup = try operation_sequence.begin(view, .cleanup);
    summary = try capacity_guard.plan(view, input, &scratch, &intents);
    try std.testing.expectEqual(@as(u32, 1), summary.intent_count);
    execution = try executor.beginIntent(view, intents[0]);
    _ = try executor.markEffectStarted(view, execution);
    operation_view = operation_sequence.read(view);
    try std.testing.expectEqual(@as(u32, 0), operation_view.reserved_execution_count);
    try std.testing.expectEqual(@as(u32, 1), operation_view.started_execution_count);
    try std.testing.expectError(error.StaleIntent, executor.abortIntent(view, execution));

    _ = try executor.markEffectUncertain(view, execution);
    operation_view = operation_sequence.read(view);
    try std.testing.expectEqual(protocol.OperationPhase.recovery_required, operation_view.phase);
    try std.testing.expectEqual(cleanup.operation_id, operation_view.token.operation_id);
    try std.testing.expectEqual(@as(u32, 1), operation_view.uncertain_execution_count);
    try std.testing.expectError(error.RecoveryRequired, state.touchResource(view, resource, 1));
    try std.testing.expectError(error.RecoveryRequired, operation_sequence.begin(view, .maintenance));
    try std.testing.expectError(error.RecoveryRequired, operation_sequence.finish(view, cleanup));

    _ = try executor.commitEffect(view, execution, .{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.TypedEffect),
        .size_bytes_after = 0,
        .released_bytes = 0,
        .outcome = .no_effect,
        .changes = 0,
        .reserved0 = .{ 0, 0, 0, 0, 0, 0 },
    });
    operation_view = operation_sequence.read(view);
    try std.testing.expectEqual(protocol.OperationPhase.settling, operation_view.phase);
    try std.testing.expectEqual(@as(u32, 0), operation_view.pending_count);
    try std.testing.expectEqual(@as(u32, 0), operation_view.uncertain_execution_count);
    try operation_sequence.finish(view, cleanup);

    const maintenance = try operation_sequence.begin(view, .maintenance);
    try state.touchResource(view, resource, 1);
    try operation_sequence.finish(view, maintenance);
}

fn planStamp(
    manager_instance_id: u64,
    snapshot_generation: u64,
    kind: protocol.PlanKind,
) protocol.PlanStamp {
    return .{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.PlanStamp),
        .manager_instance_id = manager_instance_id,
        .operation_id = 1,
        .operation_generation = 1,
        .snapshot_generation = snapshot_generation,
        .kind = @intFromEnum(kind),
        .reserved0 = .{ 0, 0, 0, 0, 0, 0, 0 },
    };
}

test "ledger requires a table action and invalidates stale composite identity" {
    const allocator = std.testing.allocator;
    const buffer = try allocateManager(allocator, config(4));
    defer allocator.free(buffer);
    const view = try state.open(buffer);
    const first_table = try state.registerTable(view, tableSpec(1, 10, 1));
    const resource = resourceSpec(
        1,
        0,
        1,
        0,
        .unobservable_now,
        std.mem.zeroes(protocol.CapabilityBinding),
        std.mem.zeroes(protocol.CapabilityBinding),
    );
    try std.testing.expectError(error.InvalidArgument, state.registerResource(view, first_table, resource));
    const release = try state.registerCapability(
        view,
        first_table,
        capabilitySpec(10, 100, protocol.effect_releases_ledger_slot, true),
    );
    const old_resource = try state.registerResource(view, first_table, resource);
    try state.unregisterResource(view, old_resource);
    try state.unregisterCapability(view, release);
    try state.unregisterTable(view, first_table);

    const second_table = try state.registerTable(view, tableSpec(1, 11, 1));
    _ = second_table;
    try std.testing.expectError(error.StaleResource, state.readResource(view, old_resource));
}

test "table action roles are unique and capability replacement does not rewrite resource rows" {
    const allocator = std.testing.allocator;
    const buffer = try allocateManager(allocator, config(4));
    defer allocator.free(buffer);
    const view = try state.open(buffer);
    const table = try state.registerTable(view, tableSpec(11, 1, 2));
    const trim = try state.registerCapability(
        view,
        table,
        capabilitySpec(1, 10, protocol.effect_changes_size_bytes, false),
    );
    try std.testing.expectError(error.IntentConflict, state.registerCapability(
        view,
        table,
        capabilitySpec(2, 20, protocol.effect_changes_size_bytes, false),
    ));
    _ = try state.registerCapability(
        view,
        table,
        capabilitySpec(3, 30, protocol.effect_releases_ledger_slot, true),
    );
    const resource = try state.registerResource(
        view,
        table,
        resourceSpec(
            1,
            100,
            1,
            0,
            .observable_now,
            state.bindingFromHandle(trim),
            std.mem.zeroes(protocol.CapabilityBinding),
        ),
    );
    const row_revision = (try state.readResource(view, resource)).row_revision;

    _ = try state.replaceCapability(
        view,
        trim,
        capabilitySpec(1, 11, protocol.effect_changes_size_bytes, false),
    );

    try std.testing.expectEqual(row_revision, (try state.readResource(view, resource)).row_revision);
}

test "free slots are reused without compacting surviving resource identities" {
    const allocator = std.testing.allocator;
    const buffer = try allocateManager(allocator, config(4));
    defer allocator.free(buffer);
    const view = try state.open(buffer);
    const table = try state.registerTable(view, tableSpec(12, 1, 3));
    _ = try state.registerCapability(
        view,
        table,
        capabilitySpec(1, 10, protocol.effect_releases_ledger_slot, true),
    );
    const first = try state.registerResource(view, table, resourceSpec(
        1,
        1,
        1,
        0,
        .unobservable_now,
        std.mem.zeroes(protocol.CapabilityBinding),
        std.mem.zeroes(protocol.CapabilityBinding),
    ));
    const removed = try state.registerResource(view, table, resourceSpec(
        2,
        1,
        1,
        0,
        .unobservable_now,
        std.mem.zeroes(protocol.CapabilityBinding),
        std.mem.zeroes(protocol.CapabilityBinding),
    ));
    const third = try state.registerResource(view, table, resourceSpec(
        3,
        1,
        1,
        0,
        .unobservable_now,
        std.mem.zeroes(protocol.CapabilityBinding),
        std.mem.zeroes(protocol.CapabilityBinding),
    ));
    try state.unregisterResource(view, removed);
    const replacement = try state.registerResource(view, table, resourceSpec(
        4,
        1,
        1,
        0,
        .unobservable_now,
        std.mem.zeroes(protocol.CapabilityBinding),
        std.mem.zeroes(protocol.CapabilityBinding),
    ));

    try std.testing.expectEqual(removed.resource_slot_index, replacement.resource_slot_index);
    try std.testing.expect(replacement.resource_slot_generation != removed.resource_slot_generation);
    try std.testing.expectEqual(@as(u64, 1), (try state.readResource(view, first)).resource.resource_uid.low);
    try std.testing.expectEqual(@as(u64, 3), (try state.readResource(view, third)).resource.resource_uid.low);
}

test "recovery coefficient is weighted by current resident bytes inside a table" {
    const allocator = std.testing.allocator;
    const buffer = try allocateManager(allocator, config(4));
    defer allocator.free(buffer);
    const view = try state.open(buffer);
    const table = try state.registerTable(view, tableSpec(13, 1, 3));
    _ = try state.registerCapability(
        view,
        table,
        capabilitySpec(1, 10, protocol.effect_releases_ledger_slot, true),
    );
    const large = try state.registerResource(view, table, resourceSpec(
        1,
        100,
        2,
        0,
        .unobservable_now,
        std.mem.zeroes(protocol.CapabilityBinding),
        std.mem.zeroes(protocol.CapabilityBinding),
    ));
    _ = try state.registerResource(view, table, resourceSpec(
        2,
        10,
        10,
        0,
        .unobservable_now,
        std.mem.zeroes(protocol.CapabilityBinding),
        std.mem.zeroes(protocol.CapabilityBinding),
    ));
    _ = try state.registerResource(view, table, resourceSpec(
        3,
        100,
        3,
        0,
        .unobservable_now,
        std.mem.zeroes(protocol.CapabilityBinding),
        std.mem.zeroes(protocol.CapabilityBinding),
    ));
    var scratch: [4]protocol.Intent = undefined;
    var intents: [4]protocol.Intent = undefined;
    const input = protocol.CapacityPlanInput{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.CapacityPlanInput),
        .strategy = .concentrated,
        .reserved0 = .{ 0, 0, 0, 0, 0, 0, 0 },
    };
    _ = try capacity_guard.plan(view, input, &scratch, &intents);
    try std.testing.expectEqual(@as(u64, 2), intents[0].resource.resource_uid.low);

    try state.updateResource(view, large, resourceSpec(
        1,
        1,
        2,
        0,
        .unobservable_now,
        std.mem.zeroes(protocol.CapabilityBinding),
        std.mem.zeroes(protocol.CapabilityBinding),
    ));
    _ = try capacity_guard.plan(view, input, &scratch, &intents);
    try std.testing.expectEqual(@as(u64, 1), intents[0].resource.resource_uid.low);
}

test "concentrated capacity guard is strict at ten percent and stops at twenty percent" {
    const allocator = std.testing.allocator;
    const buffer = try allocateManager(allocator, config(16));
    defer allocator.free(buffer);
    const view = try state.open(buffer);
    const table = try state.registerTable(view, tableSpec(2, 20, 10));
    const release = try state.registerCapability(
        view,
        table,
        capabilitySpec(20, 200, protocol.effect_releases_ledger_slot, true),
    );
    const binding = state.bindingFromHandle(release);
    var handles: [10]protocol.ResourceHandle = undefined;
    var index: usize = 0;
    while (index < 9) : (index += 1) {
        handles[index] = try state.registerResource(
            view,
            table,
            resourceSpec(@intCast(index + 1), 1000 - index, @intCast(index), 0, .unobservable_now, binding, binding),
        );
    }
    var scratch: [16]protocol.Intent = undefined;
    var intents: [16]protocol.Intent = undefined;
    const input = protocol.CapacityPlanInput{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.CapacityPlanInput),
        .strategy = .concentrated,
        .reserved0 = .{ 0, 0, 0, 0, 0, 0, 0 },
    };
    var summary = try capacity_guard.plan(view, input, &scratch, &intents);
    try std.testing.expectEqual(@as(u32, 0), summary.intent_count);

    handles[9] = try state.registerResource(
        view,
        table,
        resourceSpec(10, 1, 99, 0, .unobservable_now, binding, binding),
    );
    summary = try capacity_guard.plan(view, input, &scratch, &intents);
    try std.testing.expectEqual(@as(u32, 10), summary.intent_count);
    try std.testing.expectEqual(@as(u64, 1), intents[0].resource.resource_uid.low);
    try std.testing.expectEqual(@as(u64, 10), intents[1].resource.resource_uid.low);

    var token = try executor.beginIntent(view, intents[0]);
    _ = try executor.markEffectStarted(view, token);
    _ = try executor.commitEffect(view, token, appliedSlotRelease());
    token = try executor.beginIntent(view, intents[1]);
    _ = try executor.markEffectStarted(view, token);
    _ = try executor.commitEffect(view, token, appliedSlotRelease());
    const capacity = try state.readTableCapacity(view, table);
    try std.testing.expectEqual(@as(u32, 2), capacity.free_count);
    summary = try capacity_guard.plan(view, input, &scratch, &intents);
    try std.testing.expectEqual(@as(u32, 0), summary.intent_count);
}

test "capacity ABI rejects exact and partial mutable buffer aliasing atomically" {
    const allocator = std.testing.allocator;
    const buffer = try allocateManager(allocator, config(4));
    defer allocator.free(buffer);
    const view = try state.open(buffer);
    const first_table = try state.registerTable(view, tableSpec(201, 1, 1));
    const first_release = try state.registerCapability(
        view,
        first_table,
        capabilitySpec(201, 2001, protocol.effect_releases_ledger_slot, true),
    );
    _ = try state.registerResource(
        view,
        first_table,
        resourceSpec(
            201,
            1,
            1,
            0,
            .unobservable_now,
            state.bindingFromHandle(first_release),
            state.bindingFromHandle(first_release),
        ),
    );
    const second_table = try state.registerTable(view, tableSpec(202, 1, 3));
    const second_release = try state.registerCapability(
        view,
        second_table,
        capabilitySpec(202, 2002, protocol.effect_releases_ledger_slot, true),
    );
    var uid: u64 = 202;
    while (uid <= 204) : (uid += 1) {
        _ = try state.registerResource(
            view,
            second_table,
            resourceSpec(
                uid,
                1,
                1,
                0,
                .unobservable_now,
                state.bindingFromHandle(second_release),
                state.bindingFromHandle(second_release),
            ),
        );
    }
    const input = protocol.CapacityPlanInput{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.CapacityPlanInput),
        .strategy = .concentrated,
        .reserved0 = .{ 0, 0, 0, 0, 0, 0, 0 },
    };
    try beginFreshOperation(view, .cleanup);
    const state_before = try allocator.dupe(u8, buffer);
    defer allocator.free(state_before);
    const operation = operation_sequence.currentToken(view);

    var exact_scratch: [4]protocol.Intent = undefined;
    @memset(std.mem.asBytes(&exact_scratch), 0xA5);
    const exact_scratch_before = exact_scratch;
    var exact_output: [1]protocol.Intent = undefined;
    @memset(std.mem.asBytes(&exact_output), 0x5A);
    const exact_output_before = exact_output;
    const aliased_summary: *protocol.PlanSummary = @ptrCast(&exact_output[0]);
    try std.testing.expectEqual(
        @intFromEnum(protocol.ResultCode.invalid_argument),
        abi.rm_local_resource_manager_plan_capacity(
            @ptrCast(buffer.ptr),
            @intCast(buffer.len),
            &operation,
            &input,
            exact_scratch[0..].ptr,
            exact_scratch.len,
            exact_output[0..].ptr,
            exact_output.len,
            aliased_summary,
        ),
    );
    try std.testing.expectEqualDeep(exact_scratch_before, exact_scratch);
    try std.testing.expectEqualDeep(exact_output_before, exact_output);
    try std.testing.expectEqualSlices(u8, state_before, buffer);

    var overlapping: [6]protocol.Intent = undefined;
    @memset(std.mem.asBytes(&overlapping), 0x3C);
    const overlapping_before = overlapping;
    var summary = std.mem.zeroes(protocol.PlanSummary);
    summary.snapshot_generation = 0xCAFE_BABE;
    const summary_before = summary;
    try std.testing.expectEqual(
        @intFromEnum(protocol.ResultCode.invalid_argument),
        abi.rm_local_resource_manager_plan_capacity(
            @ptrCast(buffer.ptr),
            @intCast(buffer.len),
            &operation,
            &input,
            overlapping[0..4].ptr,
            4,
            overlapping[2..6].ptr,
            4,
            &summary,
        ),
    );
    try std.testing.expectEqualDeep(overlapping_before, overlapping);
    try std.testing.expectEqualDeep(summary_before, summary);
    try std.testing.expectEqualSlices(u8, state_before, buffer);
}

test "capacity ABI BufferTooSmall does not partially publish official outputs" {
    const allocator = std.testing.allocator;
    const buffer = try allocateManager(allocator, config(2));
    defer allocator.free(buffer);
    const view = try state.open(buffer);
    var table_id: u64 = 211;
    while (table_id <= 212) : (table_id += 1) {
        const table = try state.registerTable(view, tableSpec(table_id, 1, 1));
        const release = try state.registerCapability(
            view,
            table,
            capabilitySpec(
                table_id,
                @intCast(table_id * 10),
                protocol.effect_releases_ledger_slot,
                true,
            ),
        );
        _ = try state.registerResource(
            view,
            table,
            resourceSpec(
                table_id,
                1,
                1,
                0,
                .unobservable_now,
                state.bindingFromHandle(release),
                state.bindingFromHandle(release),
            ),
        );
    }
    const input = protocol.CapacityPlanInput{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.CapacityPlanInput),
        .strategy = .concentrated,
        .reserved0 = .{ 0, 0, 0, 0, 0, 0, 0 },
    };
    try beginFreshOperation(view, .cleanup);
    const state_before = try allocator.dupe(u8, buffer);
    defer allocator.free(state_before);
    const operation = operation_sequence.currentToken(view);
    var scratch: [2]protocol.Intent = undefined;

    var short_output: [1]protocol.Intent = undefined;
    @memset(std.mem.asBytes(&short_output), 0x11);
    const short_output_before = short_output;
    var table_output: [2]protocol.CapacityTablePlanView = undefined;
    @memset(std.mem.asBytes(&table_output), 0x22);
    const table_output_before = table_output;
    var summary = std.mem.zeroes(protocol.PlanSummary);
    summary.snapshot_generation = 0x1111_2222;
    const summary_before = summary;
    try std.testing.expectEqual(
        @intFromEnum(protocol.ResultCode.buffer_too_small),
        abi.rm_local_resource_manager_plan_capacity_detailed(
            @ptrCast(buffer.ptr),
            @intCast(buffer.len),
            &operation,
            &input,
            scratch[0..].ptr,
            scratch.len,
            short_output[0..].ptr,
            short_output.len,
            table_output[0..].ptr,
            table_output.len,
            &summary,
        ),
    );
    try std.testing.expectEqualDeep(short_output_before, short_output);
    try std.testing.expectEqualDeep(table_output_before, table_output);
    try std.testing.expectEqualDeep(summary_before, summary);
    try std.testing.expectEqualSlices(u8, state_before, buffer);

    var full_output: [2]protocol.Intent = undefined;
    @memset(std.mem.asBytes(&full_output), 0x33);
    const full_output_before = full_output;
    var short_table_output: [1]protocol.CapacityTablePlanView = undefined;
    @memset(std.mem.asBytes(&short_table_output), 0x44);
    const short_table_output_before = short_table_output;
    summary = summary_before;
    try std.testing.expectEqual(
        @intFromEnum(protocol.ResultCode.buffer_too_small),
        abi.rm_local_resource_manager_plan_capacity_detailed(
            @ptrCast(buffer.ptr),
            @intCast(buffer.len),
            &operation,
            &input,
            scratch[0..].ptr,
            scratch.len,
            full_output[0..].ptr,
            full_output.len,
            short_table_output[0..].ptr,
            short_table_output.len,
            &summary,
        ),
    );
    try std.testing.expectEqualDeep(full_output_before, full_output);
    try std.testing.expectEqualDeep(short_table_output_before, short_table_output);
    try std.testing.expectEqualDeep(summary_before, summary);
    try std.testing.expectEqualSlices(u8, state_before, buffer);

    short_output = short_output_before;
    summary = summary_before;
    try std.testing.expectEqual(
        @intFromEnum(protocol.ResultCode.buffer_too_small),
        abi.rm_local_resource_manager_plan_capacity(
            @ptrCast(buffer.ptr),
            @intCast(buffer.len),
            &operation,
            &input,
            scratch[0..].ptr,
            scratch.len,
            short_output[0..].ptr,
            short_output.len,
            &summary,
        ),
    );
    try std.testing.expectEqualDeep(short_output_before, short_output);
    try std.testing.expectEqualDeep(summary_before, summary);
    try std.testing.expectEqualSlices(u8, state_before, buffer);
}

test "table capacity reservations cannot overcommit the global resource pool" {
    const allocator = std.testing.allocator;
    const buffer = try allocateManager(allocator, config(10));
    defer allocator.free(buffer);
    const view = try state.open(buffer);
    const first = try state.registerTable(view, tableSpec(301, 1, 6));

    try std.testing.expectError(
        error.ResourceFull,
        state.registerTable(view, tableSpec(302, 1, 5)),
    );

    try state.unregisterTable(view, first);
    _ = try state.registerTable(view, tableSpec(302, 1, 5));
}

test "open rejects a corrupted aggregate table capacity reservation" {
    const allocator = std.testing.allocator;
    const buffer = try allocateManager(allocator, config(10));
    defer allocator.free(buffer);
    const view = try state.open(buffer);
    _ = try state.registerTable(view, tableSpec(311, 1, 5));
    const second = try state.registerTable(view, tableSpec(312, 1, 5));

    view.tables[second.slot_index].capacity = 6;

    try std.testing.expectError(error.InvalidState, state.open(buffer));
}

test "capacity ranking handles 4096 reverse ordered candidates with fixed memory" {
    const allocator = std.testing.allocator;
    const intents = try allocator.alloc(protocol.Intent, 4096);
    defer allocator.free(intents);
    for (intents, 0..) |*intent, index| {
        intent.* = std.mem.zeroes(protocol.Intent);
        intent.resource.resource_uid.low = @intCast(intents.len - index);
        intent.access_loss_impact = .unobservable_now;
        intent.settled_activity = 1;
        intent.recovery_cost_coefficient = 1;
    }

    rank.sortCapacity(intents);

    for (intents, 0..) |intent, index| {
        try std.testing.expectEqual(@as(u64, @intCast(index + 1)), intent.resource.resource_uid.low);
    }
}

test "capacity ranking uses semantic traits independently inside each table" {
    const allocator = std.testing.allocator;
    const buffer = try allocateManager(allocator, config(16));
    defer allocator.free(buffer);
    const view = try state.open(buffer);
    const first_table = try state.registerTable(view, tableSpec(101, 1, 4));
    const second_table = try state.registerTable(view, tableSpec(202, 1, 4));
    const first_release = try state.registerCapability(
        view,
        first_table,
        capabilitySpec(101, 1001, protocol.effect_releases_ledger_slot, true),
    );
    const second_release = try state.registerCapability(
        view,
        second_table,
        capabilitySpec(202, 2002, protocol.effect_releases_ledger_slot, true),
    );
    const no_mode = std.mem.zeroes(protocol.CapabilityBinding);
    const first_binding = state.bindingFromHandle(first_release);
    const second_binding = state.bindingFromHandle(second_release);

    _ = try state.registerResource(view, first_table, resourceSpec(
        11,
        4096,
        0,
        7,
        .observable_now,
        no_mode,
        first_binding,
    ));
    _ = try state.registerResource(view, first_table, resourceSpec(
        12,
        4096,
        900,
        7,
        .unobservable_now,
        no_mode,
        first_binding,
    ));
    _ = try state.registerResource(view, first_table, resourceSpec(
        13,
        4096,
        100,
        7,
        .unobservable_now,
        no_mode,
        first_binding,
    ));
    _ = try state.registerResource(view, first_table, resourceSpec(
        14,
        4096,
        0,
        7,
        .fatal,
        no_mode,
        first_binding,
    ));

    _ = try state.registerResource(view, second_table, resourceSpec(
        21,
        4096,
        500,
        7,
        .unobservable_now,
        no_mode,
        second_binding,
    ));
    _ = try state.registerResource(view, second_table, resourceSpec(
        22,
        4096,
        1,
        7,
        .observable_now,
        no_mode,
        second_binding,
    ));
    _ = try state.registerResource(view, second_table, resourceSpec(
        23,
        4096,
        5,
        7,
        .unobservable_now,
        no_mode,
        second_binding,
    ));
    _ = try state.registerResource(view, second_table, resourceSpec(
        24,
        4096,
        0,
        7,
        .fatal,
        no_mode,
        second_binding,
    ));

    var scratch: [16]protocol.Intent = undefined;
    var intents: [16]protocol.Intent = undefined;
    const summary = try capacity_guard.plan(view, .{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.CapacityPlanInput),
        .strategy = .concentrated,
        .reserved0 = .{ 0, 0, 0, 0, 0, 0, 0 },
    }, &scratch, &intents);

    try std.testing.expectEqual(@as(u32, 6), summary.intent_count);
    try std.testing.expectEqual(@as(u32, 2), summary.affected_table_count);
    try std.testing.expectEqual(@as(u64, 13), intents[0].resource.resource_uid.low);
    try std.testing.expectEqual(@as(u64, 12), intents[1].resource.resource_uid.low);
    try std.testing.expectEqual(@as(u64, 11), intents[2].resource.resource_uid.low);
    try std.testing.expectEqual(@as(u64, 23), intents[3].resource.resource_uid.low);
    try std.testing.expectEqual(@as(u64, 21), intents[4].resource.resource_uid.low);
    try std.testing.expectEqual(@as(u64, 22), intents[5].resource.resource_uid.low);
}

test "capacity thresholds and smooth pace are explicit manager configuration" {
    const allocator = std.testing.allocator;
    var custom = config(32);
    custom.concentrated_trigger_free_percent = 30;
    custom.concentrated_target_free_percent = 40;
    custom.smooth_trigger_free_percent = 35;
    custom.smooth_emergency_free_percent = 5;
    custom.smooth_maximum_releases_per_interval = 3;
    const buffer = try allocateManager(allocator, custom);
    defer allocator.free(buffer);
    const view = try state.open(buffer);
    const table = try state.registerTable(view, tableSpec(9, 90, 10));
    const release = try state.registerCapability(
        view,
        table,
        capabilitySpec(90, 900, protocol.effect_releases_ledger_slot, true),
    );
    const binding = state.bindingFromHandle(release);
    var index: u64 = 1;
    while (index <= 8) : (index += 1) {
        _ = try state.registerResource(
            view,
            table,
            resourceSpec(index, 1, @intCast(index), 0, .unobservable_now, binding, binding),
        );
    }

    var scratch: [32]protocol.Intent = undefined;
    var intents: [32]protocol.Intent = undefined;
    var summary = try capacity_guard.plan(view, .{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.CapacityPlanInput),
        .strategy = .concentrated,
        .reserved0 = .{ 0, 0, 0, 0, 0, 0, 0 },
    }, &scratch, &intents);
    try std.testing.expectEqual(@as(u32, 8), summary.intent_count);

    summary = try capacity_guard.plan(view, .{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.CapacityPlanInput),
        .strategy = .smooth,
        .reserved0 = .{ 0, 0, 0, 0, 0, 0, 0 },
    }, &scratch, &intents);
    try std.testing.expectEqual(@as(u32, 3), summary.intent_count);
    try std.testing.expectEqual(protocol.CapacityPhase.smooth_paced, intents[0].capacity_phase);
    for (intents[0..summary.intent_count]) |intent| {
        const token = try executor.beginIntent(view, intent);
        _ = try executor.markEffectStarted(view, token);
        _ = try executor.commitEffect(view, token, .{
            .abi_version = protocol.abi_version,
            .struct_size = @sizeOf(protocol.TypedEffect),
            .size_bytes_after = 0,
            .released_bytes = 0,
            .outcome = .no_effect,
            .changes = 0,
            .reserved0 = .{ 0, 0, 0, 0, 0, 0 },
        });
    }
}

test "smooth emergency executes every still-legal candidate from the trigger snapshot" {
    const allocator = std.testing.allocator;
    const buffer = try allocateManager(allocator, config(32));
    defer allocator.free(buffer);
    const view = try state.open(buffer);
    const table = try state.registerTable(view, tableSpec(3, 30, 20));
    const release = try state.registerCapability(
        view,
        table,
        capabilitySpec(30, 300, protocol.effect_releases_ledger_slot, true),
    );
    const binding = state.bindingFromHandle(release);
    var index: u64 = 1;
    while (index <= 19) : (index += 1) {
        _ = try state.registerResource(
            view,
            table,
            resourceSpec(index, 1, @intCast(index), 0, .unobservable_now, binding, binding),
        );
    }
    var scratch: [32]protocol.Intent = undefined;
    var intents: [32]protocol.Intent = undefined;
    const summary = try capacity_guard.plan(view, .{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.CapacityPlanInput),
        .strategy = .smooth,
        .reserved0 = .{ 0, 0, 0, 0, 0, 0, 0 },
    }, &scratch, &intents);
    try std.testing.expectEqual(@as(u32, 19), summary.intent_count);
    try std.testing.expectEqual(protocol.CapacityPhase.smooth_emergency, intents[0].capacity_phase);

    for (intents[0..summary.intent_count]) |intent| {
        const token = try executor.beginIntent(view, intent);
        _ = try executor.markEffectStarted(view, token);
        _ = try executor.commitEffect(view, token, appliedSlotRelease());
    }
    try std.testing.expectEqual(@as(u32, 20), (try state.readTableCapacity(view, table)).free_count);
    const paced = try capacity_guard.plan(view, .{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.CapacityPlanInput),
        .strategy = .smooth,
        .reserved0 = .{ 0, 0, 0, 0, 0, 0, 0 },
    }, &scratch, &intents);
    try std.testing.expectEqual(@as(u32, 0), paced.intent_count);
}

test "capacity and mode intents merge once and bytes-only effect does not release a slot" {
    const allocator = std.testing.allocator;
    const buffer = try allocateManager(allocator, config(4));
    defer allocator.free(buffer);
    const view = try state.open(buffer);
    const table = try state.registerTable(view, tableSpec(4, 40, 1));
    const release = try state.registerCapability(
        view,
        table,
        capabilitySpec(
            40,
            400,
            protocol.effect_releases_ledger_slot | protocol.effect_changes_size_bytes,
            true,
        ),
    );
    const binding = state.bindingFromHandle(release);
    const resource = try state.registerResource(
        view,
        table,
        resourceSpec(1, 4096, 1, 0, .unobservable_now, binding, binding),
    );
    var scratch: [4]protocol.Intent = undefined;
    var capacity_intents: [4]protocol.Intent = undefined;
    const capacity_summary = try capacity_guard.plan(view, .{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.CapacityPlanInput),
        .strategy = .concentrated,
        .reserved0 = .{ 0, 0, 0, 0, 0, 0, 0 },
    }, &scratch, &capacity_intents);
    var mode_intents: [4]protocol.Intent = undefined;
    const mode_summary = try mode_cleanup.plan(view, .{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.ModePlanInput),
        .table = table,
        .maximum_intents = 1,
        .mode = .optimize,
        .reserved0 = .{ 0, 0, 0 },
    }, &mode_intents);
    var merged: [4]protocol.Intent = undefined;
    const merged_summary = try intent_merge.merge(
        planStamp(view.header.manager_instance_id, capacity_summary.snapshot_generation, .capacity),
        planStamp(view.header.manager_instance_id, capacity_summary.snapshot_generation, .mode),
        capacity_intents[0..capacity_summary.intent_count],
        mode_intents[0..mode_summary.intent_count],
        &merged,
    );
    try std.testing.expectEqual(@as(u32, 1), merged_summary.intent_count);
    try std.testing.expectEqual(
        protocol.intent_reason_capacity | protocol.intent_reason_mode,
        merged[0].reason_flags,
    );

    var two_capacity = [_]protocol.Intent{ capacity_intents[0], capacity_intents[0] };
    two_capacity[1].resource.resource_uid.low += 1;
    two_capacity[1].resource.resource_slot_generation += 1;
    two_capacity[1].resource.resource_slot_index += 1;
    var two_mode = [_]protocol.Intent{ mode_intents[0], mode_intents[0] };
    two_mode[0].resource = two_capacity[1].resource;
    two_mode[1].resource.table_id.low += 1;
    two_mode[1].resource.table_incarnation += 1;
    two_mode[1].resource.table_slot_generation += 1;
    two_mode[1].resource.table_slot_index += 1;
    var bounded_union: [3]protocol.Intent = undefined;
    const bounded_summary = try intent_merge.merge(
        planStamp(view.header.manager_instance_id, capacity_summary.snapshot_generation, .capacity),
        planStamp(view.header.manager_instance_id, capacity_summary.snapshot_generation, .mode),
        &two_capacity,
        &two_mode,
        &bounded_union,
    );
    try std.testing.expectEqual(@as(u32, 3), bounded_summary.intent_count);
    try std.testing.expectEqual(@as(u32, 2), bounded_summary.affected_table_count);
    try std.testing.expectEqual(
        protocol.intent_reason_capacity | protocol.intent_reason_mode,
        bounded_union[1].reason_flags,
    );

    const token = try executor.beginIntent(view, mode_intents[0]);
    _ = try executor.markEffectStarted(view, token);
    _ = try executor.commitEffect(view, token, .{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.TypedEffect),
        .size_bytes_after = 1024,
        .released_bytes = 3072,
        .outcome = .applied,
        .changes = protocol.effect_changes_size_bytes,
        .reserved0 = .{ 0, 0, 0, 0, 0, 0 },
    });
    const after = try state.readTableCapacity(view, table);
    try std.testing.expectEqual(@as(u32, 0), after.free_count);
    try std.testing.expectEqual(@as(u64, 1024), (try state.readResource(view, resource)).size_bytes);
}

test "merge keeps every complete resource handle identity field distinct" {
    const allocator = std.testing.allocator;
    const buffer = try allocateManager(allocator, config(4));
    defer allocator.free(buffer);
    const view = try state.open(buffer);
    const table = try state.registerTable(view, tableSpec(64, 640, 1));
    const release = try state.registerCapability(
        view,
        table,
        capabilitySpec(
            64,
            6400,
            protocol.effect_releases_ledger_slot | protocol.effect_changes_size_bytes,
            true,
        ),
    );
    const resource = try state.registerResource(
        view,
        table,
        resourceSpec(
            64,
            4096,
            1,
            0,
            .unobservable_now,
            state.bindingFromHandle(release),
            state.bindingFromHandle(release),
        ),
    );
    _ = resource;
    var scratch: [4]protocol.Intent = undefined;
    var capacity_intents: [4]protocol.Intent = undefined;
    const capacity_summary = try capacity_guard.plan(view, .{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.CapacityPlanInput),
        .strategy = .concentrated,
        .reserved0 = .{ 0, 0, 0, 0, 0, 0, 0 },
    }, &scratch, &capacity_intents);
    var mode_intents: [4]protocol.Intent = undefined;
    const mode_summary = try mode_cleanup.plan(view, .{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.ModePlanInput),
        .table = table,
        .maximum_intents = 1,
        .mode = .optimize,
        .reserved0 = .{ 0, 0, 0 },
    }, &mode_intents);
    try std.testing.expectEqual(@as(u32, 1), capacity_summary.intent_count);
    try std.testing.expectEqual(@as(u32, 1), mode_summary.intent_count);

    for (0..9) |field_index| {
        var changed = mode_intents[0];
        switch (field_index) {
            0 => changed.resource.table_slot_index += 1,
            1 => changed.resource.table_slot_generation += 1,
            2 => changed.resource.table_id.low += 1,
            3 => changed.resource.table_id.high += 1,
            4 => changed.resource.table_incarnation += 1,
            5 => changed.resource.resource_slot_index += 1,
            6 => changed.resource.resource_slot_generation += 1,
            7 => changed.resource.resource_uid.low += 1,
            8 => changed.resource.resource_uid.high += 1,
            else => unreachable,
        }
        const changed_mode = [_]protocol.Intent{changed};
        var merged: [2]protocol.Intent = undefined;
        const summary = try intent_merge.merge(
            planStamp(view.header.manager_instance_id, capacity_summary.snapshot_generation, .capacity),
            planStamp(view.header.manager_instance_id, capacity_summary.snapshot_generation, .mode),
            capacity_intents[0..1],
            &changed_mode,
            &merged,
        );
        try std.testing.expectEqual(@as(u32, 2), summary.intent_count);
        try std.testing.expectEqual(
            @as(u32, if (field_index < 5) 2 else 1),
            summary.affected_table_count,
        );
        try std.testing.expectEqual(field_index >= 5, identity.sameTable(
            capacity_intents[0].resource,
            changed.resource,
        ));
        const forward = identity.compareResource(capacity_intents[0].resource, changed.resource);
        const reverse = identity.compareResource(changed.resource, capacity_intents[0].resource);
        try std.testing.expect(forward != .eq);
        try std.testing.expectEqual(
            if (forward == .lt) std.math.Order.gt else std.math.Order.lt,
            reverse,
        );
        try std.testing.expect(!identity.sameResource(merged[0].resource, merged[1].resource));
    }
}

test "merge validates ownership conflicts and bounds before touching output" {
    const allocator = std.testing.allocator;
    const buffer = try allocateManager(allocator, config(4));
    defer allocator.free(buffer);
    const view = try state.open(buffer);
    const table = try state.registerTable(view, tableSpec(65, 650, 1));
    const release = try state.registerCapability(
        view,
        table,
        capabilitySpec(
            65,
            6500,
            protocol.effect_releases_ledger_slot | protocol.effect_changes_size_bytes,
            true,
        ),
    );
    _ = try state.registerResource(
        view,
        table,
        resourceSpec(
            65,
            4096,
            1,
            0,
            .unobservable_now,
            state.bindingFromHandle(release),
            state.bindingFromHandle(release),
        ),
    );
    var scratch: [4]protocol.Intent = undefined;
    var capacity_intents: [4]protocol.Intent = undefined;
    const capacity_summary = try capacity_guard.plan(view, .{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.CapacityPlanInput),
        .strategy = .concentrated,
        .reserved0 = .{ 0, 0, 0, 0, 0, 0, 0 },
    }, &scratch, &capacity_intents);
    var mode_intents: [4]protocol.Intent = undefined;
    const mode_summary = try mode_cleanup.plan(view, .{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.ModePlanInput),
        .table = table,
        .maximum_intents = 1,
        .mode = .optimize,
        .reserved0 = .{ 0, 0, 0 },
    }, &mode_intents);
    try std.testing.expectEqual(@as(u32, 1), capacity_summary.intent_count);
    try std.testing.expectEqual(@as(u32, 1), mode_summary.intent_count);

    var sentinel = capacity_intents[0];
    sentinel.action_code = 0xFFFF_FFFE;
    var output = [_]protocol.Intent{ sentinel, sentinel };
    const output_before = output;

    var foreign = mode_intents[0];
    foreign.resource.manager_instance_id += 1;
    const foreign_mode = [_]protocol.Intent{foreign};
    try std.testing.expectError(
        error.InvalidArgument,
        intent_merge.merge(
            planStamp(view.header.manager_instance_id, capacity_summary.snapshot_generation, .capacity),
            planStamp(view.header.manager_instance_id, capacity_summary.snapshot_generation, .mode),
            capacity_intents[0..1],
            &foreign_mode,
            &output,
        ),
    );
    try std.testing.expectEqualDeep(output_before, output);

    try std.testing.expectError(
        error.InvalidArgument,
        intent_merge.merge(
            planStamp(view.header.manager_instance_id, capacity_summary.snapshot_generation, .capacity),
            planStamp(view.header.manager_instance_id, capacity_summary.snapshot_generation + 1, .mode),
            capacity_intents[0..1],
            mode_intents[0..1],
            &output,
        ),
    );
    try std.testing.expectEqualDeep(output_before, output);

    var conflicting_capacity = [_]protocol.Intent{ capacity_intents[0], capacity_intents[0] };
    conflicting_capacity[1].capability_id += 1;
    try std.testing.expectError(
        error.IntentConflict,
        intent_merge.merge(
            planStamp(view.header.manager_instance_id, capacity_summary.snapshot_generation, .capacity),
            planStamp(view.header.manager_instance_id, capacity_summary.snapshot_generation, .mode),
            &conflicting_capacity,
            mode_intents[0..0],
            &output,
        ),
    );
    try std.testing.expectEqualDeep(output_before, output);

    var distinct_mode = mode_intents[0];
    distinct_mode.resource.resource_slot_index += 1;
    distinct_mode.resource.resource_slot_generation += 1;
    distinct_mode.resource.resource_uid.low += 1;
    const distinct_modes = [_]protocol.Intent{distinct_mode};
    try std.testing.expectError(
        error.BufferTooSmall,
        intent_merge.merge(
            planStamp(view.header.manager_instance_id, capacity_summary.snapshot_generation, .capacity),
            planStamp(view.header.manager_instance_id, capacity_summary.snapshot_generation, .mode),
            capacity_intents[0..1],
            &distinct_modes,
            output[0..1],
        ),
    );
    try std.testing.expectEqualDeep(output_before, output);

    var wrong_kind = planStamp(
        view.header.manager_instance_id,
        capacity_summary.snapshot_generation,
        .merged,
    );
    try std.testing.expectError(
        error.InvalidArgument,
        intent_merge.merge(
            wrong_kind,
            planStamp(view.header.manager_instance_id, capacity_summary.snapshot_generation, .mode),
            capacity_intents[0..0],
            mode_intents[0..0],
            &output,
        ),
    );
    try std.testing.expectEqualDeep(output_before, output);

    wrong_kind = planStamp(
        view.header.manager_instance_id + 1,
        capacity_summary.snapshot_generation,
        .capacity,
    );
    try std.testing.expectError(
        error.InvalidArgument,
        intent_merge.merge(
            wrong_kind,
            planStamp(view.header.manager_instance_id, capacity_summary.snapshot_generation, .mode),
            capacity_intents[0..0],
            mode_intents[0..0],
            &output,
        ),
    );
    try std.testing.expectEqualDeep(output_before, output);

    var invalid_phase = capacity_intents[0];
    invalid_phase.reason_flags |= protocol.intent_reason_mode;
    const invalid_capacity = [_]protocol.Intent{invalid_phase};
    try std.testing.expectError(
        error.InvalidArgument,
        intent_merge.merge(
            planStamp(view.header.manager_instance_id, capacity_summary.snapshot_generation, .capacity),
            planStamp(view.header.manager_instance_id, capacity_summary.snapshot_generation, .mode),
            &invalid_capacity,
            mode_intents[0..0],
            &output,
        ),
    );
    try std.testing.expectEqualDeep(output_before, output);

    for (0..15) |field_index| {
        var changed = capacity_intents[0];
        switch (field_index) {
            0 => changed.table_revision += 1,
            1 => changed.row_revision += 1,
            2 => changed.activity_revision += 1,
            3 => changed.use_generation += 1,
            4 => changed.capability_id += 1,
            5 => changed.capability_generation += 1,
            6 => changed.size_bytes += 1,
            7 => changed.settled_activity += 1,
            8 => changed.recovery_cost_coefficient += 1,
            9 => changed.action_code += 1,
            10 => changed.capability_slot_index += 1,
            11 => changed.expected_effects = protocol.effect_releases_ledger_slot,
            12 => changed.destructive = if (changed.destructive == 0) 1 else 0,
            13 => changed.capacity_phase = .smooth_paced,
            14 => changed.access_loss_impact = .observable_now,
            else => unreachable,
        }
        const duplicate = [_]protocol.Intent{ capacity_intents[0], changed };
        try std.testing.expectError(
            error.IntentConflict,
            intent_merge.merge(
                planStamp(view.header.manager_instance_id, capacity_summary.snapshot_generation, .capacity),
                planStamp(view.header.manager_instance_id, capacity_summary.snapshot_generation, .mode),
                &duplicate,
                mode_intents[0..0],
                &output,
            ),
        );
        try std.testing.expectEqualDeep(output_before, output);
    }

    for (0..14) |field_index| {
        var changed = mode_intents[0];
        switch (field_index) {
            0 => changed.table_revision += 1,
            1 => changed.row_revision += 1,
            2 => changed.activity_revision += 1,
            3 => changed.use_generation += 1,
            4 => changed.capability_id += 1,
            5 => changed.capability_generation += 1,
            6 => changed.size_bytes += 1,
            7 => changed.settled_activity += 1,
            8 => changed.recovery_cost_coefficient += 1,
            9 => changed.action_code += 1,
            10 => changed.capability_slot_index += 1,
            11 => changed.expected_effects = protocol.effect_changes_size_bytes,
            12 => changed.destructive = if (changed.destructive == 0) 1 else 0,
            13 => changed.access_loss_impact = .observable_now,
            else => unreachable,
        }
        const duplicate = [_]protocol.Intent{ mode_intents[0], changed };
        try std.testing.expectError(
            error.IntentConflict,
            intent_merge.merge(
                planStamp(view.header.manager_instance_id, capacity_summary.snapshot_generation, .capacity),
                planStamp(view.header.manager_instance_id, capacity_summary.snapshot_generation, .mode),
                capacity_intents[0..0],
                &duplicate,
                &output,
            ),
        );
        try std.testing.expectEqualDeep(output_before, output);
    }

    for (0..2) |side| {
        for (0..8) |field_index| {
            var changed = if (side == 0) capacity_intents[0] else mode_intents[0];
            switch (field_index) {
                0 => changed.abi_version += 1,
                1 => changed.struct_size -= 1,
                2 => changed.snapshot_generation += 1,
                3 => changed.reason_flags = if (side == 0) protocol.intent_reason_mode else protocol.intent_reason_capacity,
                4 => changed.reserved0[0] = 1,
                5 => changed.reserved0[1] = 1,
                6 => changed.reserved0[2] = 1,
                7 => changed.capacity_phase = if (side == 0) .none else .smooth_paced,
                else => unreachable,
            }
            const changed_values = [_]protocol.Intent{changed};
            try std.testing.expectError(
                error.InvalidArgument,
                intent_merge.merge(
                    planStamp(view.header.manager_instance_id, capacity_summary.snapshot_generation, .capacity),
                    planStamp(view.header.manager_instance_id, capacity_summary.snapshot_generation, .mode),
                    if (side == 0) &changed_values else capacity_intents[0..0],
                    if (side == 0) mode_intents[0..0] else &changed_values,
                    &output,
                ),
            );
            try std.testing.expectEqualDeep(output_before, output);
        }
    }

    for (0..9) |field_index| {
        var changed = mode_intents[0];
        switch (field_index) {
            0 => changed.snapshot_generation += 1,
            1 => changed.table_revision += 1,
            2 => changed.row_revision += 1,
            3 => changed.activity_revision += 1,
            4 => changed.use_generation += 1,
            5 => changed.size_bytes += 1,
            6 => changed.settled_activity += 1,
            7 => changed.recovery_cost_coefficient += 1,
            8 => changed.access_loss_impact = .observable_now,
            else => unreachable,
        }
        const changed_mode = [_]protocol.Intent{changed};
        const expected_error = if (field_index == 0) error.InvalidArgument else error.IntentConflict;
        try std.testing.expectError(
            expected_error,
            intent_merge.merge(
                planStamp(view.header.manager_instance_id, capacity_summary.snapshot_generation, .capacity),
                planStamp(view.header.manager_instance_id, capacity_summary.snapshot_generation, .mode),
                capacity_intents[0..1],
                &changed_mode,
                &output,
            ),
        );
        try std.testing.expectEqualDeep(output_before, output);
    }

    for (0..5) |field_index| {
        var changed = mode_intents[0];
        switch (field_index) {
            0 => changed.capability_id += 1,
            1 => changed.capability_generation += 1,
            2 => changed.action_code += 1,
            3 => changed.capability_slot_index += 1,
            4 => {
                changed.expected_effects = protocol.effect_changes_size_bytes;
                changed.destructive = 0;
            },
            else => unreachable,
        }
        const changed_mode = [_]protocol.Intent{changed};
        var precedence_output: [1]protocol.Intent = undefined;
        const summary = try intent_merge.merge(
            planStamp(view.header.manager_instance_id, capacity_summary.snapshot_generation, .capacity),
            planStamp(view.header.manager_instance_id, capacity_summary.snapshot_generation, .mode),
            capacity_intents[0..1],
            &changed_mode,
            &precedence_output,
        );
        var expected = capacity_intents[0];
        expected.reason_flags |= protocol.intent_reason_mode;
        try std.testing.expectEqual(@as(u32, 1), summary.intent_count);
        try std.testing.expectEqualDeep(expected, precedence_output[0]);
    }

    const empty = [_]protocol.Intent{};
    const empty_summary = try intent_merge.merge(
        planStamp(view.header.manager_instance_id, capacity_summary.snapshot_generation, .capacity),
        planStamp(view.header.manager_instance_id, capacity_summary.snapshot_generation, .mode),
        &empty,
        &empty,
        &output,
    );
    try std.testing.expectEqual(capacity_summary.snapshot_generation, empty_summary.snapshot_generation);
    try std.testing.expectEqual(@as(u32, 0), empty_summary.intent_count);
    try std.testing.expectEqualDeep(output_before, output);
}

test "merge v5 ABI rejects every empty-plan stamp mutation atomically" {
    const valid_capacity = planStamp(77, 1, .capacity);
    const valid_mode = planStamp(77, 1, .mode);
    var output = [_]protocol.Intent{std.mem.zeroes(protocol.Intent)};
    output[0].action_code = 0xA5A5_A5A5;
    const output_before = output;
    var summary_sentinel = std.mem.zeroes(protocol.PlanSummary);
    summary_sentinel.snapshot_generation = 0xDEAD_BEEF;
    summary_sentinel.intent_count = 0xFFFF;

    for (0..2) |side| {
        for (0..14) |field_index| {
            var capacity_stamp = valid_capacity;
            var mode_stamp = valid_mode;
            const stamp = if (side == 0) &capacity_stamp else &mode_stamp;
            switch (field_index) {
                0 => stamp.abi_version += 1,
                1 => stamp.struct_size -= 1,
                2 => stamp.manager_instance_id = 0,
                3 => stamp.snapshot_generation = 0,
                4 => stamp.kind = @intFromEnum(if (side == 0) protocol.PlanKind.mode else protocol.PlanKind.capacity),
                5 => stamp.kind = @intFromEnum(protocol.PlanKind.merged),
                6 => stamp.kind = 0xFF,
                7...13 => stamp.reserved0[field_index - 7] = 1,
                else => unreachable,
            }
            var summary = summary_sentinel;
            try std.testing.expectEqual(
                @intFromEnum(protocol.ResultCode.invalid_argument),
                abi.rm_local_resource_manager_merge_plans_v8(
                    &capacity_stamp,
                    &mode_stamp,
                    null,
                    0,
                    null,
                    0,
                    output[0..].ptr,
                    1,
                    &summary,
                ),
            );
            try std.testing.expectEqualDeep(output_before, output);
            try std.testing.expectEqualDeep(summary_sentinel, summary);
        }
    }
}

test "merge v5 ABI rejects pointer aliasing before output or summary mutation" {
    const allocator = std.testing.allocator;
    const buffer = try allocateManager(allocator, config(4));
    defer allocator.free(buffer);
    const view = try state.open(buffer);
    const table = try state.registerTable(view, tableSpec(66, 660, 1));
    const release = try state.registerCapability(
        view,
        table,
        capabilitySpec(
            66,
            6600,
            protocol.effect_releases_ledger_slot | protocol.effect_changes_size_bytes,
            true,
        ),
    );
    _ = try state.registerResource(
        view,
        table,
        resourceSpec(
            66,
            4096,
            1,
            0,
            .unobservable_now,
            state.bindingFromHandle(release),
            state.bindingFromHandle(release),
        ),
    );
    var scratch: [4]protocol.Intent = undefined;
    var capacity_intents: [4]protocol.Intent = undefined;
    const capacity_summary = try capacity_guard.plan(view, .{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.CapacityPlanInput),
        .strategy = .concentrated,
        .reserved0 = .{ 0, 0, 0, 0, 0, 0, 0 },
    }, &scratch, &capacity_intents);
    var mode_intents: [4]protocol.Intent = undefined;
    const mode_summary = try mode_cleanup.plan(view, .{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.ModePlanInput),
        .table = table,
        .maximum_intents = 1,
        .mode = .optimize,
        .reserved0 = .{ 0, 0, 0 },
    }, &mode_intents);
    try std.testing.expectEqual(@as(u32, 1), capacity_summary.intent_count);
    try std.testing.expectEqual(@as(u32, 1), mode_summary.intent_count);
    var capacity_stamp = planStamp(
        view.header.manager_instance_id,
        capacity_summary.snapshot_generation,
        .capacity,
    );
    var mode_stamp = planStamp(
        view.header.manager_instance_id,
        capacity_summary.snapshot_generation,
        .mode,
    );

    var valid_output: [1]protocol.Intent = undefined;
    var valid_summary: protocol.PlanSummary = undefined;
    try std.testing.expectEqual(
        @intFromEnum(protocol.ResultCode.ok),
        abi.rm_local_resource_manager_merge_plans_v8(
            &capacity_stamp,
            &mode_stamp,
            capacity_intents[0..1].ptr,
            1,
            mode_intents[0..1].ptr,
            1,
            valid_output[0..].ptr,
            1,
            &valid_summary,
        ),
    );
    try std.testing.expectEqual(@as(u32, 1), valid_summary.intent_count);

    var tail_sentinel = capacity_intents[0];
    tail_sentinel.action_code = 0xF0F0_F0F0;
    var oversized_output = [_]protocol.Intent{ tail_sentinel, tail_sentinel, tail_sentinel };
    const oversized_tail_before = oversized_output[1..3].*;
    var oversized_summary: protocol.PlanSummary = undefined;
    try std.testing.expectEqual(
        @intFromEnum(protocol.ResultCode.ok),
        abi.rm_local_resource_manager_merge_plans_v8(
            &capacity_stamp,
            &mode_stamp,
            capacity_intents[0..1].ptr,
            1,
            mode_intents[0..1].ptr,
            1,
            oversized_output[0..].ptr,
            oversized_output.len,
            &oversized_summary,
        ),
    );
    var expected_merged = capacity_intents[0];
    expected_merged.reason_flags |= protocol.intent_reason_mode;
    try std.testing.expectEqualDeep(expected_merged, oversized_output[0]);
    try std.testing.expectEqualDeep(oversized_tail_before, oversized_output[1..3].*);

    var summary_sentinel = std.mem.zeroes(protocol.PlanSummary);
    summary_sentinel.snapshot_generation = 0xDEAD_BEEF;
    summary_sentinel.intent_count = 0xFFFF;
    var exact_capacity_stamp = capacity_stamp;
    const exact_capacity_before = exact_capacity_stamp;
    var alias_summary = summary_sentinel;
    const exact_stamp_output: [*]protocol.Intent = @ptrCast(&exact_capacity_stamp);
    try std.testing.expectEqual(
        @intFromEnum(protocol.ResultCode.invalid_argument),
        abi.rm_local_resource_manager_merge_plans_v8(
            &exact_capacity_stamp,
            &mode_stamp,
            null,
            0,
            null,
            0,
            exact_stamp_output,
            1,
            &alias_summary,
        ),
    );
    try std.testing.expectEqualDeep(exact_capacity_before, exact_capacity_stamp);
    try std.testing.expectEqualDeep(summary_sentinel, alias_summary);

    const StampOverlap = extern struct {
        prefix: u64,
        stamp: protocol.PlanStamp,
        tail: [@sizeOf(protocol.Intent)]u8,
    };
    var partial_mode_storage = std.mem.zeroes(StampOverlap);
    partial_mode_storage.stamp = mode_stamp;
    const partial_mode_before = partial_mode_storage;
    alias_summary = summary_sentinel;
    const partial_stamp_output: [*]protocol.Intent = @ptrCast(&partial_mode_storage.prefix);
    try std.testing.expectEqual(
        @intFromEnum(protocol.ResultCode.invalid_argument),
        abi.rm_local_resource_manager_merge_plans_v8(
            &capacity_stamp,
            &partial_mode_storage.stamp,
            null,
            0,
            null,
            0,
            partial_stamp_output,
            1,
            &alias_summary,
        ),
    );
    try std.testing.expectEqualDeep(partial_mode_before, partial_mode_storage);
    try std.testing.expectEqualDeep(summary_sentinel, alias_summary);

    var exact_mode_stamp = mode_stamp;
    const exact_mode_before = exact_mode_stamp;
    const exact_stamp_summary: *protocol.PlanSummary = @ptrCast(&exact_mode_stamp);
    try std.testing.expectEqual(
        @intFromEnum(protocol.ResultCode.invalid_argument),
        abi.rm_local_resource_manager_merge_plans_v8(
            &capacity_stamp,
            &exact_mode_stamp,
            null,
            0,
            null,
            0,
            null,
            0,
            exact_stamp_summary,
        ),
    );
    try std.testing.expectEqualDeep(exact_mode_before, exact_mode_stamp);

    var partial_capacity_storage = std.mem.zeroes(StampOverlap);
    partial_capacity_storage.stamp = capacity_stamp;
    const partial_capacity_before = partial_capacity_storage;
    const partial_stamp_summary: *protocol.PlanSummary = @ptrCast(&partial_capacity_storage.prefix);
    try std.testing.expectEqual(
        @intFromEnum(protocol.ResultCode.invalid_argument),
        abi.rm_local_resource_manager_merge_plans_v8(
            &partial_capacity_storage.stamp,
            &mode_stamp,
            null,
            0,
            null,
            0,
            null,
            0,
            partial_stamp_summary,
        ),
    );
    try std.testing.expectEqualDeep(partial_capacity_before, partial_capacity_storage);

    var alias_capacity = [_]protocol.Intent{ capacity_intents[0], capacity_intents[0] };
    const alias_capacity_before = alias_capacity;
    var summary = summary_sentinel;
    try std.testing.expectEqual(
        @intFromEnum(protocol.ResultCode.invalid_argument),
        abi.rm_local_resource_manager_merge_plans_v8(
            &capacity_stamp,
            &mode_stamp,
            alias_capacity[0..1].ptr,
            1,
            mode_intents[0..1].ptr,
            1,
            alias_capacity[0..1].ptr,
            1,
            &summary,
        ),
    );
    try std.testing.expectEqualDeep(alias_capacity_before, alias_capacity);
    try std.testing.expectEqualDeep(summary_sentinel, summary);

    var alias_mode = [_]protocol.Intent{ mode_intents[0], mode_intents[0] };
    const alias_mode_before = alias_mode;
    summary = summary_sentinel;
    try std.testing.expectEqual(
        @intFromEnum(protocol.ResultCode.invalid_argument),
        abi.rm_local_resource_manager_merge_plans_v8(
            &capacity_stamp,
            &mode_stamp,
            null,
            0,
            alias_mode[0..1].ptr,
            1,
            alias_mode[0..1].ptr,
            1,
            &summary,
        ),
    );
    try std.testing.expectEqualDeep(alias_mode_before, alias_mode);
    try std.testing.expectEqualDeep(summary_sentinel, summary);

    summary = summary_sentinel;
    try std.testing.expectEqual(
        @intFromEnum(protocol.ResultCode.invalid_argument),
        abi.rm_local_resource_manager_merge_plans_v8(
            &capacity_stamp,
            &mode_stamp,
            null,
            0,
            alias_mode[0..].ptr,
            2,
            alias_mode[1..].ptr,
            1,
            &summary,
        ),
    );
    try std.testing.expectEqualDeep(alias_mode_before, alias_mode);
    try std.testing.expectEqualDeep(summary_sentinel, summary);

    summary = summary_sentinel;
    try std.testing.expectEqual(
        @intFromEnum(protocol.ResultCode.invalid_argument),
        abi.rm_local_resource_manager_merge_plans_v8(
            &capacity_stamp,
            &mode_stamp,
            alias_capacity[0..].ptr,
            2,
            null,
            0,
            alias_capacity[1..].ptr,
            1,
            &summary,
        ),
    );
    try std.testing.expectEqualDeep(alias_capacity_before, alias_capacity);
    try std.testing.expectEqualDeep(summary_sentinel, summary);

    var output = [_]protocol.Intent{capacity_intents[0]};
    const output_before = output;
    const output_summary_alias: *protocol.PlanSummary = @ptrCast(&output[0]);
    try std.testing.expectEqual(
        @intFromEnum(protocol.ResultCode.invalid_argument),
        abi.rm_local_resource_manager_merge_plans_v8(
            &capacity_stamp,
            &mode_stamp,
            capacity_intents[0..1].ptr,
            1,
            mode_intents[0..1].ptr,
            1,
            output[0..].ptr,
            1,
            output_summary_alias,
        ),
    );
    try std.testing.expectEqualDeep(output_before, output);

    summary = summary_sentinel;
    const input_summary_alias: *protocol.PlanSummary = @ptrCast(&alias_capacity[0]);
    try std.testing.expectEqual(
        @intFromEnum(protocol.ResultCode.invalid_argument),
        abi.rm_local_resource_manager_merge_plans_v8(
            &capacity_stamp,
            &mode_stamp,
            alias_capacity[0..1].ptr,
            1,
            mode_intents[0..1].ptr,
            1,
            output[0..].ptr,
            1,
            input_summary_alias,
        ),
    );
    try std.testing.expectEqualDeep(alias_capacity_before, alias_capacity);
    try std.testing.expectEqualDeep(output_before, output);

    summary = summary_sentinel;
    try std.testing.expectEqual(
        @intFromEnum(protocol.ResultCode.invalid_argument),
        abi.rm_local_resource_manager_merge_plans_v8(
            &capacity_stamp,
            &mode_stamp,
            capacity_intents[0..1].ptr,
            1,
            null,
            0,
            null,
            1,
            &summary,
        ),
    );
    try std.testing.expectEqualDeep(summary_sentinel, summary);

    output = output_before;
    try std.testing.expectEqual(
        @intFromEnum(protocol.ResultCode.invalid_argument),
        abi.rm_local_resource_manager_merge_plans_v8(
            &capacity_stamp,
            &mode_stamp,
            capacity_intents[0..1].ptr,
            1,
            mode_intents[0..1].ptr,
            1,
            output[0..].ptr,
            1,
            null,
        ),
    );
    try std.testing.expectEqualDeep(output_before, output);

    summary = summary_sentinel;
    output = output_before;
    try std.testing.expectEqual(
        @intFromEnum(protocol.ResultCode.invalid_argument),
        abi.rm_local_resource_manager_merge_plans_v8(
            null,
            &mode_stamp,
            capacity_intents[0..1].ptr,
            1,
            mode_intents[0..1].ptr,
            1,
            output[0..].ptr,
            1,
            &summary,
        ),
    );
    try std.testing.expectEqualDeep(output_before, output);
    try std.testing.expectEqualDeep(summary_sentinel, summary);

    const overflow_address = std.math.maxInt(usize) - (@alignOf(protocol.Intent) - 1);
    const overflow_output: [*]protocol.Intent = @ptrFromInt(overflow_address);
    summary = summary_sentinel;
    try std.testing.expectEqual(
        @intFromEnum(protocol.ResultCode.invalid_argument),
        abi.rm_local_resource_manager_merge_plans_v8(
            &capacity_stamp,
            &mode_stamp,
            null,
            0,
            null,
            0,
            overflow_output,
            1,
            &summary,
        ),
    );
    try std.testing.expectEqualDeep(summary_sentinel, summary);

    summary = summary_sentinel;
    try std.testing.expectEqual(
        @intFromEnum(protocol.ResultCode.invalid_argument),
        abi.rm_local_resource_manager_merge_plans_v8(
            &capacity_stamp,
            &mode_stamp,
            null,
            1,
            null,
            0,
            output[0..].ptr,
            1,
            &summary,
        ),
    );
    try std.testing.expectEqualDeep(output_before, output);
    try std.testing.expectEqualDeep(summary_sentinel, summary);

    var empty_summary = summary_sentinel;
    try std.testing.expectEqual(
        @intFromEnum(protocol.ResultCode.ok),
        abi.rm_local_resource_manager_merge_plans_v8(
            &capacity_stamp,
            &mode_stamp,
            null,
            0,
            null,
            0,
            null,
            0,
            &empty_summary,
        ),
    );
    try std.testing.expectEqual(@as(u32, 0), empty_summary.intent_count);
    try std.testing.expectEqual(capacity_summary.snapshot_generation, empty_summary.snapshot_generation);
}

test "capability replacement invalidates planned work and uncertain effect blocks repeats" {
    const allocator = std.testing.allocator;
    const buffer = try allocateManager(allocator, config(4));
    defer allocator.free(buffer);
    const view = try state.open(buffer);
    const table = try state.registerTable(view, tableSpec(5, 50, 1));
    const original = try state.registerCapability(
        view,
        table,
        capabilitySpec(50, 500, protocol.effect_changes_size_bytes, false),
    );
    const resource = try state.registerResource(
        view,
        table,
        resourceSpec(
            1,
            10,
            1,
            0,
            .observable_now,
            state.bindingFromHandle(original),
            std.mem.zeroes(protocol.CapabilityBinding),
        ),
    );
    var intents: [4]protocol.Intent = undefined;
    var summary = try mode_cleanup.plan(view, .{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.ModePlanInput),
        .table = table,
        .maximum_intents = 1,
        .mode = .optimize,
        .reserved0 = .{ 0, 0, 0 },
    }, &intents);
    const replacement = try state.replaceCapability(
        view,
        original,
        capabilitySpec(50, 501, protocol.effect_changes_size_bytes, false),
    );
    try std.testing.expectError(error.StaleCapability, executor.beginIntent(view, intents[0]));

    const updated = resourceSpec(
        1,
        10,
        1,
        0,
        .observable_now,
        state.bindingFromHandle(replacement),
        std.mem.zeroes(protocol.CapabilityBinding),
    );
    try state.updateResource(view, resource, updated);
    summary = try mode_cleanup.plan(view, .{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.ModePlanInput),
        .table = table,
        .maximum_intents = 1,
        .mode = .optimize,
        .reserved0 = .{ 0, 0, 0 },
    }, &intents);
    try std.testing.expectEqual(@as(u32, 1), summary.intent_count);
    const token = try executor.beginIntent(view, intents[0]);
    _ = try executor.markEffectStarted(view, token);
    _ = try executor.markEffectUncertain(view, token);
    try std.testing.expectError(error.RecoveryRequired, executor.beginIntent(view, intents[0]));
}

test "pending capacity rejection does not mutate the rejected attempt" {
    const allocator = std.testing.allocator;
    var manager_config = config(2);
    manager_config.pending_capacity = 1;
    const buffer = try allocateManager(allocator, manager_config);
    defer allocator.free(buffer);
    const view = try state.open(buffer);
    const first_table = try state.registerTable(view, tableSpec(61, 1, 1));
    const second_table = try state.registerTable(view, tableSpec(62, 1, 1));
    const first_release = try state.registerCapability(
        view,
        first_table,
        capabilitySpec(61, 610, protocol.effect_releases_ledger_slot, true),
    );
    const second_release = try state.registerCapability(
        view,
        second_table,
        capabilitySpec(62, 620, protocol.effect_releases_ledger_slot, true),
    );
    const first_resource = try state.registerResource(
        view,
        first_table,
        resourceSpec(
            1,
            1,
            1,
            0,
            .unobservable_now,
            state.bindingFromHandle(first_release),
            state.bindingFromHandle(first_release),
        ),
    );
    const second_resource = try state.registerResource(
        view,
        second_table,
        resourceSpec(
            2,
            1,
            1,
            0,
            .unobservable_now,
            state.bindingFromHandle(second_release),
            state.bindingFromHandle(second_release),
        ),
    );
    _ = first_resource;

    var first_intents: [1]protocol.Intent = undefined;
    var second_intents: [1]protocol.Intent = undefined;
    const first_summary = try mode_cleanup.plan(view, .{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.ModePlanInput),
        .table = first_table,
        .maximum_intents = 1,
        .mode = .release_all,
        .reserved0 = .{ 0, 0, 0 },
    }, &first_intents);
    const second_summary = try mode_cleanup.plan(view, .{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.ModePlanInput),
        .table = second_table,
        .maximum_intents = 1,
        .mode = .release_all,
        .reserved0 = .{ 0, 0, 0 },
    }, &second_intents);
    try std.testing.expectEqual(@as(u32, 1), first_summary.intent_count);
    try std.testing.expectEqual(@as(u32, 1), second_summary.intent_count);

    const first_token = try executor.beginIntent(view, first_intents[0]);
    const snapshot_generation = view.header.snapshot_generation;
    const next_pending_generation = view.header.next_pending_generation;
    const next_attempt_id = view.header.next_attempt_id;
    const pending_free_head = view.header.pending_free_head;
    try std.testing.expectEqual(@as(u32, 1), view.header.pending_count);
    try std.testing.expectEqual(layout.no_slot, pending_free_head);

    try std.testing.expectError(
        error.PendingFull,
        executor.beginIntent(view, second_intents[0]),
    );

    try std.testing.expectEqual(snapshot_generation, view.header.snapshot_generation);
    try std.testing.expectEqual(next_pending_generation, view.header.next_pending_generation);
    try std.testing.expectEqual(next_attempt_id, view.header.next_attempt_id);
    try std.testing.expectEqual(pending_free_head, view.header.pending_free_head);
    try std.testing.expectEqual(@as(u32, 1), view.header.pending_count);
    try std.testing.expectEqual(
        @as(u32, 0),
        view.tables[second_table.slot_index].active_pending_count,
    );
    try std.testing.expectEqual(
        layout.no_slot,
        view.resources[second_resource.resource_slot_index].pending_slot_index,
    );
    try std.testing.expectEqual(
        @as(u64, 0),
        view.resources[second_resource.resource_slot_index].pending_generation,
    );

    _ = try executor.abortIntent(view, first_token);
    const second_token = try executor.beginIntent(view, second_intents[0]);
    _ = try executor.abortIntent(view, second_token);
}

test "touch rejects a pending resource without mutating execution identity" {
    const allocator = std.testing.allocator;
    const buffer = try allocateManager(allocator, config(4));
    defer allocator.free(buffer);
    const view = try state.open(buffer);
    const table = try state.registerTable(view, tableSpec(63, 1, 4));
    const trim = try state.registerCapability(
        view,
        table,
        capabilitySpec(63, 630, protocol.effect_changes_size_bytes, false),
    );
    const resource = try state.registerResource(
        view,
        table,
        resourceSpec(
            1,
            4096,
            1,
            0,
            .unobservable_now,
            state.bindingFromHandle(trim),
            std.mem.zeroes(protocol.CapabilityBinding),
        ),
    );
    var intents: [1]protocol.Intent = undefined;
    const summary = try mode_cleanup.plan(view, .{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.ModePlanInput),
        .table = table,
        .maximum_intents = 1,
        .mode = .optimize,
        .reserved0 = .{ 0, 0, 0 },
    }, &intents);
    try std.testing.expectEqual(@as(u32, 1), summary.intent_count);
    const token = try executor.beginIntent(view, intents[0]);

    const header_before = view.header.*;
    const table_before = view.tables[table.slot_index];
    const resource_before = view.resources[resource.resource_slot_index];
    const pending_before = view.pending[token.pending_slot_index];

    try std.testing.expectError(error.ResourceBusy, state.touchResource(view, resource, 1));

    try std.testing.expectEqualDeep(header_before, view.header.*);
    try std.testing.expectEqualDeep(table_before, view.tables[table.slot_index]);
    try std.testing.expectEqualDeep(
        resource_before,
        view.resources[resource.resource_slot_index],
    );
    try std.testing.expectEqualDeep(
        pending_before,
        view.pending[token.pending_slot_index],
    );

    _ = try executor.abortIntent(view, token);
}

test "dangerous table reports stalled when no row can release a ledger slot" {
    const allocator = std.testing.allocator;
    const buffer = try allocateManager(allocator, config(16));
    defer allocator.free(buffer);
    const view = try state.open(buffer);
    const table = try state.registerTable(view, tableSpec(6, 60, 10));
    const trim = try state.registerCapability(
        view,
        table,
        capabilitySpec(60, 600, protocol.effect_changes_size_bytes, false),
    );
    const binding = state.bindingFromHandle(trim);
    var index: u64 = 1;
    while (index <= 10) : (index += 1) {
        _ = try state.registerResource(
            view,
            table,
            resourceSpec(index, 10, 1, 0, .unobservable_now, binding, std.mem.zeroes(protocol.CapabilityBinding)),
        );
    }
    var scratch: [16]protocol.Intent = undefined;
    var intents: [16]protocol.Intent = undefined;
    var tables: [16]protocol.CapacityTablePlanView = undefined;
    const summary = try capacity_guard.planDetailed(view, .{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.CapacityPlanInput),
        .strategy = .concentrated,
        .reserved0 = .{ 0, 0, 0, 0, 0, 0, 0 },
    }, &scratch, &intents, &tables);
    try std.testing.expectEqual(@as(u32, 1), summary.triggered_table_count);
    try std.testing.expectEqual(@as(u32, 1), summary.stalled_table_count);
    try std.testing.expectEqual(@as(u32, 0), summary.intent_count);
    try std.testing.expectEqual(table.table_id, tables[0].capacity.table.table_id);
    try std.testing.expectEqual(table.table_incarnation, tables[0].capacity.table.table_incarnation);
    try std.testing.expectEqual(@as(u32, 0), tables[0].capacity.free_count);
    try std.testing.expectEqual(
        protocol.capacity_table_flag_triggered | protocol.capacity_table_flag_stalled,
        tables[0].flags,
    );
}

test "fatal and protected rows are never capacity candidates" {
    const allocator = std.testing.allocator;
    const buffer = try allocateManager(allocator, config(4));
    defer allocator.free(buffer);
    const view = try state.open(buffer);
    const table = try state.registerTable(view, tableSpec(7, 70, 2));
    const release = try state.registerCapability(
        view,
        table,
        capabilitySpec(70, 700, protocol.effect_releases_ledger_slot, true),
    );
    const binding = state.bindingFromHandle(release);
    _ = try state.registerResource(
        view,
        table,
        resourceSpec(1, 1, 1, 0, .fatal, binding, binding),
    );
    const protected = try state.registerResource(
        view,
        table,
        resourceSpec(2, 1, 1, 0, .unobservable_now, binding, binding),
    );
    const lease = try state.beginUse(view, protected);
    var scratch: [4]protocol.Intent = undefined;
    var intents: [4]protocol.Intent = undefined;
    var summary = try capacity_guard.plan(view, .{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.CapacityPlanInput),
        .strategy = .concentrated,
        .reserved0 = .{ 0, 0, 0, 0, 0, 0, 0 },
    }, &scratch, &intents);
    try std.testing.expectEqual(@as(u32, 0), summary.intent_count);
    try std.testing.expectEqual(@as(u32, 1), summary.stalled_table_count);
    try state.endUse(view, lease);
    summary = try capacity_guard.plan(view, .{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.CapacityPlanInput),
        .strategy = .concentrated,
        .reserved0 = .{ 0, 0, 0, 0, 0, 0, 0 },
    }, &scratch, &intents);
    try std.testing.expectEqual(@as(u32, 1), summary.intent_count);
    try std.testing.expectEqual(@as(u64, 2), intents[0].resource.resource_uid.low);
}

test "smooth thresholds are exact and release all prefers the slot-release capability" {
    const allocator = std.testing.allocator;
    const buffer = try allocateManager(allocator, config(32));
    defer allocator.free(buffer);
    const view = try state.open(buffer);
    const table = try state.registerTable(view, tableSpec(8, 80, 20));
    const trim = try state.registerCapability(
        view,
        table,
        capabilitySpec(80, 800, protocol.effect_changes_size_bytes, false),
    );
    const release = try state.registerCapability(
        view,
        table,
        capabilitySpec(81, 801, protocol.effect_releases_ledger_slot, true),
    );
    const trim_binding = state.bindingFromHandle(trim);
    const release_binding = state.bindingFromHandle(release);
    var index: u64 = 1;
    while (index <= 17) : (index += 1) {
        _ = try state.registerResource(
            view,
            table,
            resourceSpec(index, index, 1, 0, .unobservable_now, trim_binding, release_binding),
        );
    }
    var scratch: [32]protocol.Intent = undefined;
    var intents: [32]protocol.Intent = undefined;
    const smooth_input = protocol.CapacityPlanInput{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.CapacityPlanInput),
        .strategy = .smooth,
        .reserved0 = .{ 0, 0, 0, 0, 0, 0, 0 },
    };
    var summary = try capacity_guard.plan(view, smooth_input, &scratch, &intents);
    try std.testing.expectEqual(@as(u32, 0), summary.triggered_table_count);

    _ = try state.registerResource(
        view,
        table,
        resourceSpec(18, 18, 1, 0, .unobservable_now, trim_binding, release_binding),
    );
    summary = try capacity_guard.plan(view, smooth_input, &scratch, &intents);
    try std.testing.expectEqual(@as(u32, 1), summary.intent_count);
    try std.testing.expectEqual(protocol.CapacityPhase.smooth_paced, intents[0].capacity_phase);

    var mode_intents: [32]protocol.Intent = undefined;
    const mode_summary = try mode_cleanup.plan(view, .{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.ModePlanInput),
        .table = table,
        .maximum_intents = 1,
        .mode = .release_all,
        .reserved0 = .{ 0, 0, 0 },
    }, &mode_intents);
    try std.testing.expectEqual(@as(u32, 1), mode_summary.intent_count);
    try std.testing.expectEqual(@as(u32, 801), mode_intents[0].action_code);
    try std.testing.expect(
        mode_intents[0].expected_effects & protocol.effect_releases_ledger_slot != 0,
    );
}

test "release all falls back to a legal non-destructive mode capability" {
    const allocator = std.testing.allocator;
    const buffer = try allocateManager(allocator, config(4));
    defer allocator.free(buffer);
    const view = try state.open(buffer);
    const table = try state.registerTable(view, tableSpec(9, 90, 4));
    const trim = try state.registerCapability(
        view,
        table,
        capabilitySpec(90, 900, protocol.effect_changes_size_bytes, false),
    );
    _ = try state.registerCapability(
        view,
        table,
        capabilitySpec(91, 901, protocol.effect_releases_ledger_slot, true),
    );
    var resource = resourceSpec(
        1,
        4096,
        1,
        0,
        .unobservable_now,
        state.bindingFromHandle(trim),
        std.mem.zeroes(protocol.CapabilityBinding),
    );
    resource.recoverability = .not_recoverable;
    _ = try state.registerResource(view, table, resource);

    var intents: [1]protocol.Intent = undefined;
    const optimize = try mode_cleanup.plan(view, .{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.ModePlanInput),
        .table = table,
        .maximum_intents = 1,
        .mode = .optimize,
        .reserved0 = .{ 0, 0, 0 },
    }, &intents);
    try std.testing.expectEqual(@as(u32, 1), optimize.intent_count);
    try std.testing.expectEqual(@as(u32, 900), intents[0].action_code);

    const release_all = try mode_cleanup.plan(view, .{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.ModePlanInput),
        .table = table,
        .maximum_intents = 1,
        .mode = .release_all,
        .reserved0 = .{ 0, 0, 0 },
    }, &intents);
    try std.testing.expectEqual(@as(u32, 1), release_all.intent_count);
    try std.testing.expectEqual(@as(u32, 900), intents[0].action_code);
    try std.testing.expectEqual(@as(u8, 0), intents[0].destructive);
    try std.testing.expectEqual(protocol.effect_changes_size_bytes, intents[0].expected_effects);
}

test "mode maximum intents bounds output and preserves too-small output" {
    const allocator = std.testing.allocator;
    const buffer = try allocateManager(allocator, config(4));
    defer allocator.free(buffer);
    const view = try state.open(buffer);
    const table = try state.registerTable(view, tableSpec(10, 100, 4));
    const trim = try state.registerCapability(
        view,
        table,
        capabilitySpec(100, 1000, protocol.effect_changes_size_bytes, false),
    );
    const binding = state.bindingFromHandle(trim);
    _ = try state.registerResource(
        view,
        table,
        resourceSpec(30, 1, 1, 0, .unobservable_now, binding, std.mem.zeroes(protocol.CapabilityBinding)),
    );
    _ = try state.registerResource(
        view,
        table,
        resourceSpec(20, 1, 1, 0, .unobservable_now, binding, std.mem.zeroes(protocol.CapabilityBinding)),
    );
    _ = try state.registerResource(
        view,
        table,
        resourceSpec(10, 1, 1, 0, .unobservable_now, binding, std.mem.zeroes(protocol.CapabilityBinding)),
    );

    var top_two: [2]protocol.Intent = undefined;
    const two = try mode_cleanup.plan(view, .{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.ModePlanInput),
        .table = table,
        .maximum_intents = 2,
        .mode = .optimize,
        .reserved0 = .{ 0, 0, 0 },
    }, &top_two);
    try std.testing.expectEqual(@as(u32, 2), two.intent_count);
    try std.testing.expectEqual(@as(u64, 10), top_two[0].resource.resource_uid.low);
    try std.testing.expectEqual(@as(u64, 20), top_two[1].resource.resource_uid.low);

    var output: [1]protocol.Intent = undefined;
    const one = try mode_cleanup.plan(view, .{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.ModePlanInput),
        .table = table,
        .maximum_intents = 1,
        .mode = .optimize,
        .reserved0 = .{ 0, 0, 0 },
    }, &output);
    try std.testing.expectEqual(@as(u32, 1), one.intent_count);
    try std.testing.expectEqual(@as(u64, 10), output[0].resource.resource_uid.low);

    output[0].action_code = 0xFFFF_FFFE;
    const before = output;
    try std.testing.expectError(error.BufferTooSmall, mode_cleanup.plan(view, .{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.ModePlanInput),
        .table = table,
        .maximum_intents = 2,
        .mode = .optimize,
        .reserved0 = .{ 0, 0, 0 },
    }, &output));
    try std.testing.expectEqualDeep(before, output);
}

fn appliedSlotRelease() protocol.TypedEffect {
    return .{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.TypedEffect),
        .size_bytes_after = 0,
        .released_bytes = 0,
        .outcome = .applied,
        .changes = protocol.effect_releases_ledger_slot,
        .reserved0 = .{ 0, 0, 0, 0, 0, 0 },
    };
}

test "predeclared partition reservation permanently excludes direct resources" {
    const allocator = std.testing.allocator;
    const buffer = try allocateManager(allocator, partitionConfig(6, 4));
    defer allocator.free(buffer);
    const view = try state.open(buffer);
    const table = try state.registerTable(view, partitionTableSpec(120, 1, 6, 2, 3));
    try registerPartitionReleaseCapability(view, table);

    const first = try state.registerResource(
        view,
        table,
        resourceSpec(1200, 10, 1, 0, .unobservable_now, std.mem.zeroes(protocol.CapabilityBinding), std.mem.zeroes(protocol.CapabilityBinding)),
    );
    const second = try state.registerResource(
        view,
        table,
        resourceSpec(1201, 10, 1, 0, .unobservable_now, std.mem.zeroes(protocol.CapabilityBinding), std.mem.zeroes(protocol.CapabilityBinding)),
    );
    const third = try state.registerResource(
        view,
        table,
        resourceSpec(1202, 10, 1, 0, .unobservable_now, std.mem.zeroes(protocol.CapabilityBinding), std.mem.zeroes(protocol.CapabilityBinding)),
    );
    try std.testing.expectEqual(@as(u32, 0), view.ledger_cells[view.resources[first.resource_slot_index].ledger_cell_index].table_local_ordinal);
    try std.testing.expectEqual(@as(u32, 1), view.ledger_cells[view.resources[second.resource_slot_index].ledger_cell_index].table_local_ordinal);
    try std.testing.expectEqual(@as(u32, 5), view.ledger_cells[view.resources[third.resource_slot_index].ledger_cell_index].table_local_ordinal);
    try std.testing.expectError(error.TableFull, state.registerResource(
        view,
        table,
        resourceSpec(1203, 10, 1, 0, .unobservable_now, std.mem.zeroes(protocol.CapabilityBinding), std.mem.zeroes(protocol.CapabilityBinding)),
    ));

    const before_partition = try state.readTableCapacity(view, table);
    try std.testing.expectEqual(@as(u32, 6), before_partition.capacity);
    try std.testing.expectEqual(@as(u32, 3), before_partition.direct_capacity);
    try std.testing.expectEqual(@as(u32, 0), before_partition.direct_free_count);
    try std.testing.expectEqual(@as(u32, 3), before_partition.partition_free_count);

    const shell = try createEmptyPartition(view, table, null, 3);
    const shell_view = try partition.readPartition(view, shell);
    try std.testing.expectEqual(@as(u32, 2), shell_view.local_start);
    const partition_resource = try partition.registerResourceAt(
        view,
        shell,
        1,
        resourceSpec(1204, 10, 1, 0, .unobservable_now, std.mem.zeroes(protocol.CapabilityBinding), std.mem.zeroes(protocol.CapabilityBinding)),
    );
    const occupied = try state.readTableCapacity(view, table);
    try std.testing.expectEqual(@as(u32, 4), occupied.occupied_count);
    try std.testing.expectEqual(@as(u32, 3), occupied.direct_occupied_count);
    try std.testing.expectEqual(@as(u32, 1), occupied.partition_occupied_count);

    try state.unregisterResource(view, partition_resource);
    try closeEmptyPartition(view, shell);
    _ = try state.open(buffer);
    try std.testing.expectError(error.TableFull, state.registerResource(
        view,
        table,
        resourceSpec(1205, 10, 1, 0, .unobservable_now, std.mem.zeroes(protocol.CapabilityBinding), std.mem.zeroes(protocol.CapabilityBinding)),
    ));
}

test "root partition requires a predeclared reservation" {
    const allocator = std.testing.allocator;
    const buffer = try allocateManager(allocator, partitionConfig(4, 4));
    defer allocator.free(buffer);
    const view = try state.open(buffer);
    const table = try state.registerTable(view, tableSpec(121, 1, 4));
    try registerPartitionReleaseCapability(view, table);
    var victims: [4]protocol.PartitionHandle = undefined;
    var intents: [4]protocol.Intent = undefined;

    try std.testing.expectError(
        error.PartitionFull,
        planPartition(view, partitionInput(table, null, 1), &victims, &intents),
    );
}

test "partition cell registration requires exact live partition authority" {
    const allocator = std.testing.allocator;
    const buffer = try allocateManager(allocator, partitionConfig(4, 4));
    defer allocator.free(buffer);
    const view = try state.open(buffer);
    const table = try state.registerTable(view, partitionTableSpec(123, 1, 4, 0, 4));
    try registerPartitionReleaseCapability(view, table);
    const first = try createEmptyPartition(view, table, null, 2);
    const second = try createEmptyPartition(view, table, null, 2);
    const second_view = try partition.readPartition(view, second);
    const second_cell = try state.tableCellAtOrdinal(
        view,
        try state.resolveTable(view, table),
        second_view.local_start,
    );
    const before = try allocator.dupe(u8, buffer);
    defer allocator.free(before);

    try std.testing.expectError(
        error.SlotOccupied,
        state.registerPartitionResourceAtLedgerCell(
            view,
            first,
            second_cell,
            resourceSpec(1230, 10, 1, 0, .unobservable_now, std.mem.zeroes(protocol.CapabilityBinding), std.mem.zeroes(protocol.CapabilityBinding)),
        ),
    );
    try std.testing.expectEqualSlices(u8, before, buffer);

    var stale = second;
    stale.partition_slot_generation += 1;
    try std.testing.expectError(
        error.StalePartition,
        state.registerPartitionResourceAtLedgerCell(
            view,
            stale,
            second_cell,
            resourceSpec(1231, 10, 1, 0, .unobservable_now, std.mem.zeroes(protocol.CapabilityBinding), std.mem.zeroes(protocol.CapabilityBinding)),
        ),
    );
    try std.testing.expectEqualSlices(u8, before, buffer);

    const registered = try state.registerPartitionResourceAtLedgerCell(
        view,
        second,
        second_cell,
        resourceSpec(1232, 10, 1, 0, .unobservable_now, std.mem.zeroes(protocol.CapabilityBinding), std.mem.zeroes(protocol.CapabilityBinding)),
    );
    try std.testing.expectEqual(second, (try partition.readCell(view, second, 0)).partition);
    try std.testing.expectEqual(registered, (try partition.readCell(view, second, 0)).resource);
}

test "reopen rejects root partition beyond reservation without unsigned underflow" {
    const allocator = std.testing.allocator;
    const buffer = try allocateManager(allocator, partitionConfig(4, 4));
    defer allocator.free(buffer);
    const view = try state.open(buffer);
    const table = try state.registerTable(view, partitionTableSpec(124, 1, 4, 1, 2));
    const root_partition = try createEmptyPartition(view, table, null, 2);
    view.partitions[root_partition.partition_slot_index].local_start = 4;
    const corrupted = try allocator.dupe(u8, buffer);
    defer allocator.free(corrupted);

    try std.testing.expectError(error.InvalidState, state.open(buffer));
    try std.testing.expectEqualSlices(u8, corrupted, buffer);
}

test "capacity guard uses direct pressure without hiding partition resource rows" {
    const allocator = std.testing.allocator;
    const buffer = try allocateManager(allocator, partitionConfig(6, 4));
    defer allocator.free(buffer);
    const view = try state.open(buffer);
    const table = try state.registerTable(view, partitionTableSpec(122, 1, 6, 1, 3));
    try registerPartitionReleaseCapability(view, table);
    const shell = try createEmptyPartition(view, table, null, 3);
    _ = try partition.registerResourceAt(
        view,
        shell,
        0,
        resourceSpec(1220, 10, 1, 0, .unobservable_now, std.mem.zeroes(protocol.CapabilityBinding), std.mem.zeroes(protocol.CapabilityBinding)),
    );
    var uid: u64 = 1221;
    while (uid < 1224) : (uid += 1) {
        _ = try state.registerResource(
            view,
            table,
            resourceSpec(uid, 10, 1, 0, .unobservable_now, std.mem.zeroes(protocol.CapabilityBinding), std.mem.zeroes(protocol.CapabilityBinding)),
        );
    }
    try beginFreshOperation(view, .cleanup);
    var scratch: [6]protocol.Intent = undefined;
    var output: [6]protocol.Intent = undefined;
    var table_output: [1]protocol.CapacityTablePlanView = undefined;
    const summary = try capacity_guard.planDetailed(
        view,
        .{
            .abi_version = protocol.abi_version,
            .struct_size = @sizeOf(protocol.CapacityPlanInput),
            .strategy = .concentrated,
            .reserved0 = .{ 0, 0, 0, 0, 0, 0, 0 },
        },
        &scratch,
        &output,
        &table_output,
    );

    try std.testing.expectEqual(@as(u32, 4), summary.intent_count);
    try std.testing.expectEqual(@as(u32, 3), table_output[0].capacity.direct_capacity);
    try std.testing.expectEqual(@as(u32, 0), table_output[0].capacity.direct_free_count);
    var reserved_candidate_count: u32 = 0;
    for (output[0..summary.intent_count]) |intent| {
        const resource = &view.resources[intent.resource.resource_slot_index];
        if (state.isPartitionReservedCell(&view.ledger_cells[resource.ledger_cell_index])) {
            reserved_candidate_count += 1;
        }
    }
    try std.testing.expectEqual(@as(u32, 1), reserved_candidate_count);
}

test "partition shell keeps stable local ordinals while resources become sparse" {
    const allocator = std.testing.allocator;
    const buffer = try allocateManager(allocator, partitionConfig(8, 8));
    defer allocator.free(buffer);
    const view = try state.open(buffer);
    const table = try state.registerTable(view, partitionTableSpec(100, 1, 4, 0, 4));
    try registerPartitionReleaseCapability(view, table);
    const shell = try createEmptyPartition(view, table, null, 4);

    const first = try partition.registerResourceAt(
        view,
        shell,
        0,
        resourceSpec(1000, 10, 1, 0, .unobservable_now, std.mem.zeroes(protocol.CapabilityBinding), std.mem.zeroes(protocol.CapabilityBinding)),
    );
    const last = try partition.registerResourceAt(
        view,
        shell,
        3,
        resourceSpec(1003, 10, 1, 0, .unobservable_now, std.mem.zeroes(protocol.CapabilityBinding), std.mem.zeroes(protocol.CapabilityBinding)),
    );
    try std.testing.expectEqual(protocol.PartitionCellKind.resource, (try partition.readCell(view, shell, 0)).kind);
    try std.testing.expectEqual(protocol.PartitionCellKind.empty, (try partition.readCell(view, shell, 1)).kind);
    try std.testing.expectEqual(last, (try partition.readCell(view, shell, 3)).resource);

    try state.unregisterResource(view, first);
    const sparse = try partition.readPartition(view, shell);
    try std.testing.expectEqual(@as(u32, 1), sparse.descendant_resource_count);
    try std.testing.expectEqual(@as(u32, 4), sparse.capacity);
    try std.testing.expectEqual(protocol.PartitionCellKind.empty, (try partition.readCell(view, shell, 0)).kind);
    try std.testing.expectEqual(last, (try partition.readCell(view, shell, 3)).resource);

    const replacement_spec = resourceSpec(1004, 10, 1, 0, .unobservable_now, std.mem.zeroes(protocol.CapabilityBinding), std.mem.zeroes(protocol.CapabilityBinding));
    const admission = try planPartitionResource(view, shell, replacement_spec);
    try std.testing.expectEqual(@as(u8, 0), admission.requires_release);
    try std.testing.expectEqual(@as(u32, 0), admission.local_ordinal);
    const replacement = try partition.commitResourceAdmission(view, admission);
    try std.testing.expectEqual(replacement, (try partition.readCell(view, shell, 0)).resource);
    try std.testing.expectEqual(last, (try partition.readCell(view, shell, 3)).resource);
    try std.testing.expectError(error.StaleResource, state.readResource(view, first));

    try state.unregisterResource(view, replacement);
    try state.unregisterResource(view, last);
    try std.testing.expectEqual(@as(u32, 0), (try partition.readPartition(view, shell)).descendant_resource_count);
    try std.testing.expectError(error.PartitionBusy, state.unregisterTable(view, table));
    _ = try state.open(buffer);
}

test "partition activity is descendant maximum and use protects whole subtree only" {
    const allocator = std.testing.allocator;
    const buffer = try allocateManager(allocator, partitionConfig(8, 8));
    defer allocator.free(buffer);
    const view = try state.open(buffer);
    const table = try state.registerTable(view, partitionTableSpec(101, 1, 8, 0, 8));
    try registerPartitionReleaseCapability(view, table);
    const root_partition = try createEmptyPartition(view, table, null, 8);
    const child = try createEmptyPartition(view, table, root_partition, 4);
    const child_resource = try partition.registerResourceAt(
        view,
        child,
        1,
        resourceSpec(1011, 10, 1, 0, .unobservable_now, std.mem.zeroes(protocol.CapabilityBinding), std.mem.zeroes(protocol.CapabilityBinding)),
    );
    const root_resource = try partition.registerResourceAt(
        view,
        root_partition,
        7,
        resourceSpec(1017, 10, 1, 0, .unobservable_now, std.mem.zeroes(protocol.CapabilityBinding), std.mem.zeroes(protocol.CapabilityBinding)),
    );
    try state.touchResource(view, root_resource, 5);
    try state.touchResource(view, child_resource, 20);
    try std.testing.expectEqual(@as(u64, 20), (try partition.readPartition(view, child)).settled_activity);
    try std.testing.expectEqual(@as(u64, 20), (try partition.readPartition(view, root_partition)).settled_activity);

    const lease = try state.beginUse(view, child_resource);
    try std.testing.expectEqual(@as(u8, 1), (try partition.readPartition(view, root_partition)).reclaim_protected);
    var victims: [8]protocol.PartitionHandle = undefined;
    var intents: [8]protocol.Intent = undefined;
    try std.testing.expectError(error.PartitionFull, planPartition(
        view,
        partitionInput(table, root_partition, 4),
        &victims,
        &intents,
    ));
    try state.endUse(view, lease);

    const admission = try planPartition(
        view,
        partitionInput(table, root_partition, 4),
        &victims,
        &intents,
    );
    try std.testing.expectEqual(@as(u32, 1), admission.summary.victim_count);
    try std.testing.expectEqual(@as(u32, 1), admission.summary.intent_count);
    try std.testing.expectEqual(child, victims[0]);
    try std.testing.expectEqual(child_resource, intents[0].resource);
    try std.testing.expectEqual(root_resource, (try partition.readCell(view, root_partition, 7)).resource);
}

test "partition protection does not block ordinary capacity cleanup or shield a cold row" {
    const allocator = std.testing.allocator;
    const buffer = try allocateManager(allocator, partitionConfig(10, 4));
    defer allocator.free(buffer);
    const view = try state.open(buffer);
    const table = try state.registerTable(view, partitionTableSpec(105, 1, 10, 0, 2));
    try registerPartitionReleaseCapability(view, table);
    const shell = try createEmptyPartition(view, table, null, 2);
    const cold = try partition.registerResourceAt(
        view,
        shell,
        0,
        resourceSpec(1050, 10, 1, 0, .unobservable_now, std.mem.zeroes(protocol.CapabilityBinding), std.mem.zeroes(protocol.CapabilityBinding)),
    );
    const hot = try partition.registerResourceAt(
        view,
        shell,
        1,
        resourceSpec(1051, 10, 1, 0, .unobservable_now, std.mem.zeroes(protocol.CapabilityBinding), std.mem.zeroes(protocol.CapabilityBinding)),
    );
    try state.touchResource(view, cold, 1);
    try state.touchResource(view, hot, 100);
    const hot_lease = try state.beginUse(view, hot);

    var uid: u64 = 1052;
    while (uid < 1060) : (uid += 1) {
        const resource = try state.registerResource(
            view,
            table,
            resourceSpec(uid, 10, 1, 0, .unobservable_now, std.mem.zeroes(protocol.CapabilityBinding), std.mem.zeroes(protocol.CapabilityBinding)),
        );
        try state.touchResource(view, resource, 10);
    }

    try std.testing.expectEqual(@as(u8, 1), (try partition.readPartition(view, shell)).reclaim_protected);
    var scratch: [10]protocol.Intent = undefined;
    var intents: [10]protocol.Intent = undefined;
    const planned = try capacity_guard.plan(view, .{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.CapacityPlanInput),
        .strategy = .concentrated,
        .reserved0 = .{ 0, 0, 0, 0, 0, 0, 0 },
    }, &scratch, &intents);
    try std.testing.expectEqual(@as(u32, 9), planned.intent_count);
    try std.testing.expectEqual(cold, intents[0].resource);
    for (intents[0..planned.intent_count]) |intent| {
        try std.testing.expect(!protocol.sameId(intent.resource.resource_uid, hot.resource_uid));
    }

    try releasePartitionIntent(view, intents[0]);
    try std.testing.expectEqual(protocol.PartitionCellKind.empty, (try partition.readCell(view, shell, 0)).kind);
    try std.testing.expectEqual(hot, (try partition.readCell(view, shell, 1)).resource);
    try state.endUse(view, hot_lease);
    _ = try state.open(buffer);
}

test "whole partition replacement chooses lowest activity and invalidates stale handle" {
    const allocator = std.testing.allocator;
    const buffer = try allocateManager(allocator, partitionConfig(6, 6));
    defer allocator.free(buffer);
    const view = try state.open(buffer);
    const table = try state.registerTable(view, partitionTableSpec(102, 1, 6, 0, 6));
    try registerPartitionReleaseCapability(view, table);
    const root_partition = try createEmptyPartition(view, table, null, 6);
    const first_child = try createEmptyPartition(view, table, root_partition, 3);
    const second_child = try createEmptyPartition(view, table, root_partition, 3);
    const first_resource = try partition.registerResourceAt(
        view,
        first_child,
        0,
        resourceSpec(1020, 10, 1, 0, .unobservable_now, std.mem.zeroes(protocol.CapabilityBinding), std.mem.zeroes(protocol.CapabilityBinding)),
    );
    const second_resource = try partition.registerResourceAt(
        view,
        second_child,
        0,
        resourceSpec(1021, 10, 1, 0, .unobservable_now, std.mem.zeroes(protocol.CapabilityBinding), std.mem.zeroes(protocol.CapabilityBinding)),
    );
    try state.touchResource(view, first_resource, 10);
    try state.touchResource(view, second_resource, 20);

    var victims: [6]protocol.PartitionHandle = undefined;
    var intents: [6]protocol.Intent = undefined;
    const admission = try planPartition(
        view,
        partitionInput(table, root_partition, 3),
        &victims,
        &intents,
    );
    try std.testing.expectEqual(@as(u32, 1), admission.summary.victim_count);
    try std.testing.expectEqual(@as(u32, 1), admission.summary.intent_count);
    try std.testing.expectEqual(first_child, victims[0]);
    try releasePartitionIntent(view, intents[0]);
    const replacement = try partition.commitAdmission(
        view,
        admission.ticket,
        victims[0..admission.summary.victim_count],
    );

    try std.testing.expectEqual(first_child.partition_slot_index, replacement.partition_slot_index);
    try std.testing.expect(first_child.partition_slot_generation != replacement.partition_slot_generation);
    try std.testing.expectEqual(@as(u32, 0), (try partition.readPartition(view, replacement)).local_start);
    try std.testing.expectError(error.StalePartition, partition.readPartition(view, first_child));
    try std.testing.expectEqual(
        second_resource,
        (try partition.readCell(view, second_child, 0)).resource,
    );
    _ = try state.open(buffer);
}

test "partition empty child replacement uses stable local order" {
    const allocator = std.testing.allocator;
    const buffer = try allocateManager(allocator, partitionConfig(4, 3));
    defer allocator.free(buffer);
    const view = try state.open(buffer);
    const table = try state.registerTable(view, partitionTableSpec(106, 1, 4, 0, 4));
    const root_partition = try createEmptyPartition(view, table, null, 4);
    const first_child = try createEmptyPartition(view, table, root_partition, 2);
    const second_child = try createEmptyPartition(view, table, root_partition, 2);

    var victims: [3]protocol.PartitionHandle = undefined;
    var intents: [4]protocol.Intent = undefined;
    const admission = try planPartition(
        view,
        partitionInput(table, root_partition, 2),
        &victims,
        &intents,
    );
    try std.testing.expectEqual(@as(u32, 1), admission.summary.victim_count);
    try std.testing.expectEqual(@as(u32, 0), admission.summary.intent_count);
    try std.testing.expectEqual(first_child, victims[0]);
    const replacement = try partition.commitAdmission(view, admission.ticket, victims[0..1]);

    try std.testing.expectEqual(@as(u32, 0), (try partition.readPartition(view, replacement)).local_start);
    try std.testing.expectError(error.StalePartition, partition.readPartition(view, first_child));
    try std.testing.expectEqual(@as(u32, 2), (try partition.readPartition(view, second_child)).local_start);
    _ = try state.open(buffer);
}

test "partition admission rejects a stale parent revision without partial mutation" {
    const allocator = std.testing.allocator;
    const buffer = try allocateManager(allocator, partitionConfig(4, 4));
    defer allocator.free(buffer);
    const view = try state.open(buffer);
    const table = try state.registerTable(view, partitionTableSpec(107, 1, 4, 0, 4));
    const root_partition = try createEmptyPartition(view, table, null, 4);

    var stale_victims: [4]protocol.PartitionHandle = undefined;
    var stale_intents: [4]protocol.Intent = undefined;
    const stale = try planPartition(
        view,
        partitionInput(table, root_partition, 1),
        &stale_victims,
        &stale_intents,
    );
    const current_child = try createEmptyPartition(view, table, root_partition, 1);
    const snapshot_before = view.header.snapshot_generation;
    const partition_count_before = view.header.partition_count;

    try std.testing.expectError(
        error.StaleOperation,
        partition.commitAdmission(view, stale.ticket, stale_victims[0..0]),
    );
    try std.testing.expectEqual(snapshot_before, view.header.snapshot_generation);
    try std.testing.expectEqual(partition_count_before, view.header.partition_count);
    try std.testing.expectEqual(current_child, (try partition.readCell(view, root_partition, 0)).child_partition);
    try std.testing.expectEqual(protocol.PartitionCellKind.empty, (try partition.readCell(view, root_partition, 1)).kind);
    _ = try state.open(buffer);
}

test "partition replacement remains isolated to the selected table" {
    const allocator = std.testing.allocator;
    const buffer = try allocateManager(allocator, partitionConfig(4, 4));
    defer allocator.free(buffer);
    const view = try state.open(buffer);
    const first_table = try state.registerTable(view, partitionTableSpec(108, 1, 2, 0, 2));
    const second_table = try state.registerTable(view, partitionTableSpec(109, 1, 2, 0, 2));
    try registerPartitionReleaseCapability(view, first_table);
    try registerPartitionReleaseCapability(view, second_table);
    const first_root = try createEmptyPartition(view, first_table, null, 2);
    const first_child = try createEmptyPartition(view, first_table, first_root, 2);
    const second_root = try createEmptyPartition(view, second_table, null, 2);
    const second_child = try createEmptyPartition(view, second_table, second_root, 2);
    const first_resource = try partition.registerResourceAt(
        view,
        first_child,
        0,
        resourceSpec(1080, 10, 1, 0, .unobservable_now, std.mem.zeroes(protocol.CapabilityBinding), std.mem.zeroes(protocol.CapabilityBinding)),
    );
    const second_resource = try partition.registerResourceAt(
        view,
        second_child,
        0,
        resourceSpec(1090, 10, 1, 0, .unobservable_now, std.mem.zeroes(protocol.CapabilityBinding), std.mem.zeroes(protocol.CapabilityBinding)),
    );

    var victims: [4]protocol.PartitionHandle = undefined;
    var intents: [4]protocol.Intent = undefined;
    const admission = try planPartition(
        view,
        partitionInput(first_table, first_root, 2),
        &victims,
        &intents,
    );
    try std.testing.expectEqual(first_child, victims[0]);
    try std.testing.expectEqual(first_resource, intents[0].resource);
    try releasePartitionIntent(view, intents[0]);
    _ = try partition.commitAdmission(view, admission.ticket, victims[0..1]);

    try std.testing.expectEqual(second_resource, (try partition.readCell(view, second_child, 0)).resource);
    try std.testing.expectEqual(@as(u32, 1), (try partition.readPartition(view, second_root)).descendant_resource_count);
    _ = try state.open(buffer);
}

test "partition resource admission fills holes then rotates lowest direct activity" {
    const allocator = std.testing.allocator;
    const buffer = try allocateManager(allocator, partitionConfig(4, 4));
    defer allocator.free(buffer);
    const view = try state.open(buffer);
    const table = try state.registerTable(view, partitionTableSpec(103, 1, 2, 0, 2));
    try registerPartitionReleaseCapability(view, table);
    const shell = try createEmptyPartition(view, table, null, 2);
    const first = try partition.registerResourceAt(
        view,
        shell,
        0,
        resourceSpec(1030, 10, 1, 0, .unobservable_now, std.mem.zeroes(protocol.CapabilityBinding), std.mem.zeroes(protocol.CapabilityBinding)),
    );
    const second = try partition.registerResourceAt(
        view,
        shell,
        1,
        resourceSpec(1031, 10, 1, 0, .unobservable_now, std.mem.zeroes(protocol.CapabilityBinding), std.mem.zeroes(protocol.CapabilityBinding)),
    );
    try state.touchResource(view, first, 10);
    try state.touchResource(view, second, 20);

    const replacement_spec = resourceSpec(1032, 10, 1, 0, .unobservable_now, std.mem.zeroes(protocol.CapabilityBinding), std.mem.zeroes(protocol.CapabilityBinding));
    const rotate = try planPartitionResource(view, shell, replacement_spec);
    try std.testing.expectEqual(@as(u8, 1), rotate.requires_release);
    try std.testing.expectEqual(@as(u32, 0), rotate.local_ordinal);
    try std.testing.expectEqual(first, rotate.intent.resource);
    try releasePartitionIntent(view, rotate.intent);
    const replacement = try partition.commitResourceAdmission(view, rotate);
    try std.testing.expectEqual(replacement, (try partition.readCell(view, shell, 0)).resource);
    try std.testing.expectEqual(second, (try partition.readCell(view, shell, 1)).resource);

    const first_lease = try state.beginUse(view, replacement);
    const second_lease = try state.beginUse(view, second);
    try std.testing.expectError(
        error.PartitionFull,
        planPartitionResource(
            view,
            shell,
            resourceSpec(1033, 10, 1, 0, .unobservable_now, std.mem.zeroes(protocol.CapabilityBinding), std.mem.zeroes(protocol.CapabilityBinding)),
        ),
    );
    try state.endUse(view, second_lease);
    try state.endUse(view, first_lease);
}

test "partition close releases the complete subtree before atomically deleting it" {
    const allocator = std.testing.allocator;
    const buffer = try allocateManager(allocator, partitionConfig(4, 4));
    defer allocator.free(buffer);
    const view = try state.open(buffer);
    const table = try state.registerTable(view, partitionTableSpec(1040, 1, 4, 0, 4));
    try registerPartitionReleaseCapability(view, table);
    const root_partition = try createEmptyPartition(view, table, null, 4);
    const child_partition = try createEmptyPartition(view, table, root_partition, 2);
    const child_resource = try partition.registerResourceAt(
        view,
        child_partition,
        0,
        resourceSpec(10400, 10, 1, 0, .unobservable_now, std.mem.zeroes(protocol.CapabilityBinding), std.mem.zeroes(protocol.CapabilityBinding)),
    );
    const root_resource = try partition.registerResourceAt(
        view,
        root_partition,
        3,
        resourceSpec(10401, 20, 1, 0, .unobservable_now, std.mem.zeroes(protocol.CapabilityBinding), std.mem.zeroes(protocol.CapabilityBinding)),
    );

    try beginFreshOperation(view, .partition_admission);
    var intents: [4]protocol.Intent = undefined;
    const admission = try partition.planClose(view, root_partition, &intents);
    try std.testing.expectEqual(protocol.PartitionAdmissionKind.close, admission.ticket.kind);
    try std.testing.expectEqual(@as(u32, 2), admission.summary.intent_count);
    try std.testing.expectEqual(@as(u32, 1), admission.summary.victim_count);
    for (intents[0..admission.summary.intent_count]) |intent| {
        try std.testing.expectEqual(root_partition.partition_slot_index, intent.reclaim_partition_slot_index);
        try std.testing.expectEqual(root_partition.partition_slot_generation, intent.reclaim_partition_slot_generation);
        try releasePartitionIntent(view, intent);
    }
    try partition.commitClose(view, admission.ticket, root_partition);

    try std.testing.expectError(error.StalePartition, partition.readPartition(view, root_partition));
    try std.testing.expectError(error.StalePartition, partition.readPartition(view, child_partition));
    try std.testing.expectError(error.StaleResource, state.readResource(view, child_resource));
    try std.testing.expectError(error.StaleResource, state.readResource(view, root_resource));
    try std.testing.expectEqual(@as(u32, 0), view.header.partition_count);
    _ = try state.open(buffer);
}

test "partition close preflight blocks occupied and absolutely undeletable resources" {
    const allocator = std.testing.allocator;
    const buffer = try allocateManager(allocator, partitionConfig(2, 2));
    defer allocator.free(buffer);
    const view = try state.open(buffer);
    const table = try state.registerTable(view, partitionTableSpec(1041, 1, 2, 0, 2));
    try registerPartitionReleaseCapability(view, table);
    const root_partition = try createEmptyPartition(view, table, null, 2);
    const occupied = try partition.registerResourceAt(
        view,
        root_partition,
        0,
        resourceSpec(10410, 10, 1, 0, .unobservable_now, std.mem.zeroes(protocol.CapabilityBinding), std.mem.zeroes(protocol.CapabilityBinding)),
    );
    var undeletable_spec = resourceSpec(10411, 10, 1, 0, .unobservable_now, std.mem.zeroes(protocol.CapabilityBinding), std.mem.zeroes(protocol.CapabilityBinding));
    undeletable_spec.recoverability = .not_recoverable;
    const undeletable = try partition.registerResourceAt(view, root_partition, 1, undeletable_spec);
    const lease = try state.beginUse(view, occupied);

    try beginFreshOperation(view, .partition_admission);
    var intents: [2]protocol.Intent = undefined;
    try std.testing.expectError(
        error.PartitionBusy,
        partition.planClose(view, root_partition, &intents),
    );
    try std.testing.expectEqual(occupied, (try partition.readCell(view, root_partition, 0)).resource);
    try std.testing.expectEqual(undeletable, (try partition.readCell(view, root_partition, 1)).resource);

    try beginFreshOperation(view, .maintenance);
    try state.endUse(view, lease);
    try beginFreshOperation(view, .partition_admission);
    try std.testing.expectError(
        error.PartitionBusy,
        partition.planClose(view, root_partition, &intents),
    );
    try std.testing.expectEqual(@as(u32, 2), (try partition.readPartition(view, root_partition)).descendant_resource_count);
    _ = try state.open(buffer);
}

test "partition create and close admission tickets cannot cross commit paths" {
    const allocator = std.testing.allocator;
    const buffer = try allocateManager(allocator, partitionConfig(4, 4));
    defer allocator.free(buffer);
    const view = try state.open(buffer);
    const table = try state.registerTable(view, partitionTableSpec(1042, 1, 4, 0, 4));
    try registerPartitionReleaseCapability(view, table);
    const target = try createEmptyPartition(view, table, null, 1);

    var victims: [4]protocol.PartitionHandle = undefined;
    var intents: [4]protocol.Intent = undefined;
    const create_admission = try planPartition(
        view,
        partitionInput(table, null, 1),
        &victims,
        &intents,
    );
    const create_state = try allocator.dupe(u8, buffer);
    defer allocator.free(create_state);
    try std.testing.expectError(
        error.InvalidArgument,
        partition.commitClose(view, create_admission.ticket, target),
    );
    try std.testing.expectEqualSlices(u8, create_state, buffer);
    const sibling = try partition.commitAdmission(
        view,
        create_admission.ticket,
        victims[0..create_admission.summary.victim_count],
    );

    try beginFreshOperation(view, .partition_admission);
    const close_admission = try partition.planClose(view, target, &intents);
    const close_state = try allocator.dupe(u8, buffer);
    defer allocator.free(close_state);
    const close_victim = [_]protocol.PartitionHandle{target};
    try std.testing.expectError(
        error.InvalidArgument,
        partition.commitAdmission(view, close_admission.ticket, &close_victim),
    );
    try std.testing.expectEqualSlices(u8, close_state, buffer);
    try partition.commitClose(view, close_admission.ticket, target);

    try std.testing.expectError(error.StalePartition, partition.readPartition(view, target));
    try std.testing.expectEqual(sibling, (try partition.readPartition(view, sibling)).partition);
    _ = try state.open(buffer);
}

test "partition close refuses exhausted table revision without reserving or releasing" {
    const allocator = std.testing.allocator;
    const buffer = try allocateManager(allocator, partitionConfig(1, 1));
    defer allocator.free(buffer);
    const view = try state.open(buffer);
    const table = try state.registerTable(view, partitionTableSpec(1043, 1, 1, 0, 1));
    try registerPartitionReleaseCapability(view, table);
    const target = try createEmptyPartition(view, table, null, 1);
    const resource = try partition.registerResourceAt(
        view,
        target,
        0,
        resourceSpec(10430, 10, 1, 0, .unobservable_now, std.mem.zeroes(protocol.CapabilityBinding), std.mem.zeroes(protocol.CapabilityBinding)),
    );

    try beginFreshOperation(view, .partition_admission);
    var intents: [1]protocol.Intent = undefined;
    const admission = try partition.planClose(view, target, &intents);
    try std.testing.expectEqual(@as(u32, 1), admission.summary.intent_count);
    view.tables[table.slot_index].revision = std.math.maxInt(u64);
    const state_before = try allocator.dupe(u8, buffer);
    defer allocator.free(state_before);

    try std.testing.expectError(error.GenerationExhausted, executor.beginIntent(view, intents[0]));
    try std.testing.expectEqualSlices(u8, state_before, buffer);
    try std.testing.expectEqual(@as(u32, 0), view.header.pending_count);
    try std.testing.expectEqual(resource, (try partition.readCell(view, target, 0)).resource);

    try std.testing.expectError(
        error.GenerationExhausted,
        state.freeResource(view, resource.resource_slot_index),
    );
    try std.testing.expectEqualSlices(u8, state_before, buffer);
    _ = try state.open(buffer);
}

test "partition tree supports deep iterative reopen and deletion" {
    const allocator = std.testing.allocator;
    const buffer = try allocateManager(allocator, partitionConfig(1, 1024));
    defer allocator.free(buffer);
    const view = try state.open(buffer);
    const table = try state.registerTable(view, partitionTableSpec(104, 1, 1, 0, 1));
    try registerPartitionReleaseCapability(view, table);
    const root_partition = try createEmptyPartition(view, table, null, 1);
    var leaf = root_partition;
    var depth: u32 = 1;
    while (depth < 1024) : (depth += 1) {
        leaf = try createEmptyPartition(view, table, leaf, 1);
    }
    try std.testing.expectEqual(@as(u32, 1024), view.header.partition_count);
    try std.testing.expectEqual(@as(u32, 0), (try partition.readPartition(view, root_partition)).descendant_resource_count);
    try std.testing.expectEqual(protocol.PartitionCellKind.child_partition, (try partition.readCell(view, root_partition, 0)).kind);
    _ = try state.open(buffer);

    try closeEmptyPartition(view, root_partition);
    try std.testing.expectEqual(@as(u32, 0), view.header.partition_count);
    try std.testing.expectError(error.StalePartition, partition.readPartition(view, leaf));
    _ = try state.open(buffer);
}

test "partition ABI rejects state and input output aliasing atomically" {
    const allocator = std.testing.allocator;
    const buffer = try allocateManager(allocator, partitionConfig(4, 8));
    defer allocator.free(buffer);
    const view = try state.open(buffer);
    const table = try state.registerTable(view, partitionTableSpec(110, 1, 4, 0, 4));
    try registerPartitionReleaseCapability(view, table);
    const root_partition = try createEmptyPartition(view, table, null, 4);
    var operation = operation_sequence.currentToken(view);
    const invalid_argument = @intFromEnum(protocol.ResultCode.invalid_argument);
    const state_before = try allocator.alloc(u8, buffer.len);
    defer allocator.free(state_before);

    var register_io: [256]u8 align(8) = undefined;
    @memset(&register_io, 0xA1);
    const register_spec: *protocol.ResourceSpec = @ptrCast(&register_io);
    register_spec.* = resourceSpec(1100, 10, 1, 0, .unobservable_now, std.mem.zeroes(protocol.CapabilityBinding), std.mem.zeroes(protocol.CapabilityBinding));
    const register_output: *protocol.ResourceHandle = @ptrCast(&register_io);
    const register_before = register_io;
    @memcpy(state_before, buffer);
    try std.testing.expectEqual(
        invalid_argument,
        abi.rm_local_resource_manager_register_partition_resource(
            @ptrCast(buffer.ptr),
            @intCast(buffer.len),
            &operation,
            &root_partition,
            0,
            register_spec,
            register_output,
        ),
    );
    try std.testing.expectEqualDeep(register_before, register_io);
    try std.testing.expectEqualSlices(u8, state_before, buffer);

    var partial_io: [1024]u8 align(8) = undefined;
    @memset(&partial_io, 0xB2);
    const partial_intents: [*]protocol.Intent = @ptrCast(partial_io[0..].ptr);
    const partial_victims: [*]protocol.PartitionHandle =
        @ptrCast(@alignCast(partial_io[8..].ptr));
    var scratch: [8]protocol.PartitionAdmissionScratch = undefined;
    @memset(std.mem.asBytes(&scratch), 0xC3);
    var summary = std.mem.zeroes(protocol.PartitionAdmissionSummary);
    @memset(std.mem.asBytes(&summary), 0xD4);
    var ticket = std.mem.zeroes(protocol.PartitionAdmissionTicket);
    @memset(std.mem.asBytes(&ticket), 0xE5);
    const partial_before = partial_io;
    const scratch_before = scratch;
    const summary_before = summary;
    const ticket_before = ticket;
    const input = partitionInput(table, root_partition, 1);
    @memcpy(state_before, buffer);
    try std.testing.expectEqual(
        invalid_argument,
        abi.rm_local_resource_manager_plan_partition_admission(
            @ptrCast(buffer.ptr),
            @intCast(buffer.len),
            &operation,
            &input,
            partial_victims,
            1,
            partial_intents,
            1,
            scratch[0..].ptr,
            scratch.len,
            &summary,
            &ticket,
        ),
    );
    try std.testing.expectEqualDeep(partial_before, partial_io);
    try std.testing.expectEqualDeep(scratch_before, scratch);
    try std.testing.expectEqualDeep(summary_before, summary);
    try std.testing.expectEqualDeep(ticket_before, ticket);
    try std.testing.expectEqualSlices(u8, state_before, buffer);

    var victims: [8]protocol.PartitionHandle = undefined;
    var intents: [8]protocol.Intent = undefined;
    const admission = try planPartition(view, input, &victims, &intents);
    operation = operation_sequence.currentToken(view);
    var commit_io: [512]u8 align(8) = undefined;
    @memset(&commit_io, 0xF6);
    const commit_ticket: *protocol.PartitionAdmissionTicket = @ptrCast(&commit_io);
    commit_ticket.* = admission.ticket;
    const commit_output: *protocol.PartitionHandle = @ptrCast(&commit_io);
    const commit_before = commit_io;
    @memcpy(state_before, buffer);
    try std.testing.expectEqual(
        invalid_argument,
        abi.rm_local_resource_manager_commit_partition_admission(
            @ptrCast(buffer.ptr),
            @intCast(buffer.len),
            &operation,
            commit_ticket,
            null,
            0,
            commit_output,
        ),
    );
    try std.testing.expectEqualDeep(commit_before, commit_io);
    try std.testing.expectEqualSlices(u8, state_before, buffer);

    var resource_plan_io: [1024]u8 align(8) = undefined;
    @memset(&resource_plan_io, 0x17);
    const resource_plan_spec: *protocol.ResourceSpec = @ptrCast(&resource_plan_io);
    resource_plan_spec.* = resourceSpec(1101, 10, 1, 0, .unobservable_now, std.mem.zeroes(protocol.CapabilityBinding), std.mem.zeroes(protocol.CapabilityBinding));
    const resource_plan_output: *protocol.PartitionResourceAdmission = @ptrCast(&resource_plan_io);
    const resource_plan_before = resource_plan_io;
    @memcpy(state_before, buffer);
    try std.testing.expectEqual(
        invalid_argument,
        abi.rm_local_resource_manager_plan_partition_resource_admission(
            @ptrCast(buffer.ptr),
            @intCast(buffer.len),
            &operation,
            &root_partition,
            resource_plan_spec,
            resource_plan_output,
        ),
    );
    try std.testing.expectEqualDeep(resource_plan_before, resource_plan_io);
    try std.testing.expectEqualSlices(u8, state_before, buffer);

    const resource_admission = try planPartitionResource(
        view,
        root_partition,
        resource_plan_spec.*,
    );
    operation = operation_sequence.currentToken(view);
    var resource_commit_io: [1024]u8 align(8) = undefined;
    @memset(&resource_commit_io, 0x28);
    const resource_commit_input: *protocol.PartitionResourceAdmission =
        @ptrCast(&resource_commit_io);
    resource_commit_input.* = resource_admission;
    const resource_commit_output: *protocol.ResourceHandle = @ptrCast(&resource_commit_io);
    const resource_commit_before = resource_commit_io;
    @memcpy(state_before, buffer);
    try std.testing.expectEqual(
        invalid_argument,
        abi.rm_local_resource_manager_commit_partition_resource_admission(
            @ptrCast(buffer.ptr),
            @intCast(buffer.len),
            &operation,
            resource_commit_input,
            resource_commit_output,
        ),
    );
    try std.testing.expectEqualDeep(resource_commit_before, resource_commit_io);
    try std.testing.expectEqualSlices(u8, state_before, buffer);

    const state_partition_alias: *const protocol.PartitionHandle = @ptrCast(buffer.ptr);
    var close_intents: [8]protocol.Intent = undefined;
    var close_summary = std.mem.zeroes(protocol.PartitionAdmissionSummary);
    var close_ticket = std.mem.zeroes(protocol.PartitionAdmissionTicket);
    @memcpy(state_before, buffer);
    try std.testing.expectEqual(
        invalid_argument,
        abi.rm_local_resource_manager_plan_partition_close(
            @ptrCast(buffer.ptr),
            @intCast(buffer.len),
            &operation,
            state_partition_alias,
            &close_intents,
            close_intents.len,
            &close_summary,
            &close_ticket,
        ),
    );
    try std.testing.expectEqualSlices(u8, state_before, buffer);

    var read_partition_io: [512]u8 align(8) = undefined;
    @memset(&read_partition_io, 0x39);
    const read_partition_input: *protocol.PartitionHandle = @ptrCast(&read_partition_io);
    read_partition_input.* = root_partition;
    const read_partition_output: *protocol.PartitionView = @ptrCast(&read_partition_io);
    const read_partition_before = read_partition_io;
    @memcpy(state_before, buffer);
    try std.testing.expectEqual(
        invalid_argument,
        abi.rm_local_resource_manager_read_partition(
            @ptrCast(buffer.ptr),
            @intCast(buffer.len),
            read_partition_input,
            read_partition_output,
        ),
    );
    try std.testing.expectEqualDeep(read_partition_before, read_partition_io);
    try std.testing.expectEqualSlices(u8, state_before, buffer);

    var read_cell_io: [512]u8 align(8) = undefined;
    @memset(&read_cell_io, 0x4A);
    const read_cell_input: *protocol.PartitionHandle = @ptrCast(&read_cell_io);
    read_cell_input.* = root_partition;
    const read_cell_output: *protocol.PartitionCellView = @ptrCast(&read_cell_io);
    const read_cell_before = read_cell_io;
    @memcpy(state_before, buffer);
    try std.testing.expectEqual(
        invalid_argument,
        abi.rm_local_resource_manager_read_partition_cell(
            @ptrCast(buffer.ptr),
            @intCast(buffer.len),
            read_cell_input,
            0,
            read_cell_output,
        ),
    );
    try std.testing.expectEqualDeep(read_cell_before, read_cell_io);
    try std.testing.expectEqualSlices(u8, state_before, buffer);
}

test "partition state validation rejects cycles parent corruption and free list damage" {
    const allocator = std.testing.allocator;
    const buffer = try allocateManager(allocator, partitionConfig(4, 8));
    defer allocator.free(buffer);
    const view = try state.open(buffer);
    const table = try state.registerTable(view, partitionTableSpec(111, 1, 4, 0, 4));
    const root_partition = try createEmptyPartition(view, table, null, 4);
    const first_child = try createEmptyPartition(view, table, root_partition, 1);
    const second_child = try createEmptyPartition(view, table, root_partition, 1);

    const second_slot = &view.partitions[second_child.partition_slot_index];
    const original_next_sibling = second_slot.next_sibling_partition;
    second_slot.next_sibling_partition = second_child.partition_slot_index;
    try std.testing.expectError(error.InvalidState, state.open(buffer));
    second_slot.next_sibling_partition = original_next_sibling;
    _ = try state.open(buffer);

    const original_free_head = view.header.partition_free_head;
    try std.testing.expect(original_free_head != layout.no_slot);
    second_slot.next_sibling_partition = original_free_head;
    try std.testing.expectError(error.InvalidState, state.open(buffer));
    second_slot.next_sibling_partition = original_next_sibling;
    _ = try state.open(buffer);

    const first_slot = &view.partitions[first_child.partition_slot_index];
    const original_parent_index = first_slot.parent_partition_slot_index;
    const original_parent_generation = first_slot.parent_partition_generation;
    first_slot.parent_partition_slot_index = first_child.partition_slot_index;
    first_slot.parent_partition_generation = first_child.partition_slot_generation;
    try std.testing.expectError(error.InvalidState, state.open(buffer));
    first_slot.parent_partition_slot_index = original_parent_index;
    first_slot.parent_partition_generation = original_parent_generation;
    _ = try state.open(buffer);

    view.header.partition_free_head = root_partition.partition_slot_index;
    try std.testing.expectError(error.InvalidState, state.open(buffer));
    view.header.partition_free_head = original_free_head;
    _ = try state.open(buffer);

    const free_slot = &view.partitions[original_free_head];
    const original_free_next = free_slot.next_free;
    try std.testing.expect(original_free_next != layout.no_slot);
    free_slot.next_free = original_free_head;
    try std.testing.expectError(error.InvalidState, state.open(buffer));
    free_slot.next_free = original_free_next;
    _ = try state.open(buffer);

    view.header.partition_free_head = original_free_next;
    try std.testing.expectError(error.InvalidState, state.open(buffer));
    view.header.partition_free_head = original_free_head;
    _ = try state.open(buffer);
}

test "partition admission rejects altered victim tickets without partial deletion" {
    const allocator = std.testing.allocator;
    const buffer = try allocateManager(allocator, partitionConfig(4, 6));
    defer allocator.free(buffer);
    const view = try state.open(buffer);
    const table = try state.registerTable(view, partitionTableSpec(112, 1, 4, 0, 4));
    const root_partition = try createEmptyPartition(view, table, null, 4);
    var children: [4]protocol.PartitionHandle = undefined;
    for (&children) |*child| child.* = try createEmptyPartition(view, table, root_partition, 1);

    var victims: [6]protocol.PartitionHandle = undefined;
    var intents: [4]protocol.Intent = undefined;
    const admission = try planPartition(
        view,
        partitionInput(table, root_partition, 2),
        &victims,
        &intents,
    );
    try std.testing.expectEqual(@as(u32, 2), admission.summary.victim_count);
    try std.testing.expectEqual(@as(u32, 0), admission.summary.intent_count);
    const state_before = try allocator.dupe(u8, buffer);
    defer allocator.free(state_before);

    const reordered = [_]protocol.PartitionHandle{ victims[1], victims[0] };
    try std.testing.expectError(
        error.InvalidArgument,
        partition.commitAdmission(view, admission.ticket, &reordered),
    );
    try std.testing.expectEqualSlices(u8, state_before, buffer);

    const duplicated = [_]protocol.PartitionHandle{ victims[0], victims[0] };
    try std.testing.expectError(
        error.InvalidArgument,
        partition.commitAdmission(view, admission.ticket, &duplicated),
    );
    try std.testing.expectEqualSlices(u8, state_before, buffer);

    const extended = [_]protocol.PartitionHandle{ victims[0], victims[1], children[2] };
    try std.testing.expectError(
        error.InvalidArgument,
        partition.commitAdmission(view, admission.ticket, &extended),
    );
    try std.testing.expectEqualSlices(u8, state_before, buffer);

    var altered_ticket = admission.ticket;
    altered_ticket.local_start += 1;
    try std.testing.expectError(
        error.AdmissionStale,
        partition.commitAdmission(view, altered_ticket, victims[0..2]),
    );
    try std.testing.expectEqualSlices(u8, state_before, buffer);

    const replacement = try partition.commitAdmission(view, admission.ticket, victims[0..2]);
    try std.testing.expectEqual(@as(u32, 0), (try partition.readPartition(view, replacement)).local_start);
    try std.testing.expectError(error.StalePartition, partition.readPartition(view, children[0]));
    try std.testing.expectError(error.StalePartition, partition.readPartition(view, children[1]));
    _ = try state.open(buffer);
}

test "wide partition admission remains bounded and preserves local ordering" {
    const allocator = std.testing.allocator;
    const width: u32 = 512;
    const buffer = try allocateManager(allocator, partitionConfig(width, width + 1));
    defer allocator.free(buffer);
    const view = try state.open(buffer);
    const table = try state.registerTable(view, partitionTableSpec(113, 1, width, 0, width));
    try registerPartitionReleaseCapability(view, table);
    const root_partition = try createEmptyPartition(view, table, null, width);

    var ordinal: u32 = 0;
    while (ordinal < width) : (ordinal += 1) {
        const child = try createEmptyPartition(view, table, root_partition, 1);
        _ = try partition.registerResourceAt(
            view,
            child,
            0,
            resourceSpec(
                20_000 + ordinal,
                1,
                1,
                0,
                .unobservable_now,
                std.mem.zeroes(protocol.CapabilityBinding),
                std.mem.zeroes(protocol.CapabilityBinding),
            ),
        );
    }

    var victims: [width]protocol.PartitionHandle = undefined;
    var intents: [width]protocol.Intent = undefined;
    var scratch: [width + 1]protocol.PartitionAdmissionScratch = undefined;
    const admission = try partition.planAdmission(
        view,
        partitionInput(table, root_partition, width),
        &victims,
        &intents,
        &scratch,
    );

    try std.testing.expectEqual(width, admission.summary.victim_count);
    try std.testing.expectEqual(width, admission.summary.intent_count);
    try std.testing.expectEqual(@as(u32, 0), admission.summary.local_start);
    ordinal = 0;
    while (ordinal < width) : (ordinal += 1) {
        try std.testing.expectEqual(ordinal, (try partition.readPartition(view, victims[ordinal])).local_start);
        try std.testing.expectEqual(
            victims[ordinal].partition_slot_generation,
            intents[ordinal].reclaim_partition_slot_generation,
        );
        try std.testing.expectEqual(
            victims[ordinal].partition_slot_index,
            intents[ordinal].reclaim_partition_slot_index,
        );
    }
    _ = try state.open(buffer);
}
