const std = @import("std");
const protocol = @import("protocol.zig");
const state = @import("state.zig");
const capacity_guard = @import("capacity_guard.zig");
const mode_cleanup = @import("mode_cleanup.zig");
const intent_merge = @import("intent_merge.zig");
const executor = @import("executor.zig");
const partition = @import("partition.zig");
const operation_sequence = @import("operation_sequence.zig");
const layout = @import("layout.zig");

fn result(code: protocol.ResultCode) i32 {
    return @intFromEnum(code);
}

fn mapError(err: anyerror) i32 {
    return result(switch (err) {
        error.InvalidArgument => .invalid_argument,
        error.InvalidState => .invalid_state,
        error.BufferTooSmall => .buffer_too_small,
        error.TableFull => .table_full,
        error.ResourceFull => .resource_full,
        error.CapabilityFull => .capability_full,
        error.PendingFull => .pending_full,
        error.DuplicateTable => .duplicate_table,
        error.DuplicateResource => .duplicate_resource,
        error.DuplicateCapability => .duplicate_capability,
        error.StaleTable => .stale_table,
        error.StaleResource => .stale_resource,
        error.StaleCapability => .stale_capability,
        error.StaleIntent => .stale_intent,
        error.ResourceBusy => .resource_busy,
        error.EffectMismatch => .effect_mismatch,
        error.IntentConflict => .intent_conflict,
        error.GenerationExhausted => .generation_exhausted,
        error.NumericOverflow => .numeric_overflow,
        error.PartitionFull => .partition_full,
        error.StalePartition => .stale_partition,
        error.PartitionBusy => .partition_busy,
        error.SlotOccupied => .slot_occupied,
        error.AdmissionStale => .admission_stale,
        error.OperationActive => .operation_active,
        error.OperationRequired => .operation_required,
        error.StaleOperation => .stale_operation,
        error.RecoveryRequired => .recovery_required,
        error.InvalidOperationPhase => .invalid_operation_phase,
        else => .invalid_state,
    });
}

fn mutableBytes(pointer: ?*anyopaque, count: u64) ?[]u8 {
    if (count == 0) return null;
    const length = std.math.cast(usize, count) orelse return null;
    const bytes: [*]u8 = @ptrCast(pointer orelse return null);
    return bytes[0..length];
}

fn constSlice(comptime T: type, pointer: ?[*]const T, count: u32) ?[]const T {
    if (count == 0) return &[_]T{};
    return (pointer orelse return null)[0..count];
}

fn mutableSlice(comptime T: type, pointer: ?[*]T, count: u32) ?[]T {
    if (count == 0) return @constCast((&[_]T{})[0..]);
    return (pointer orelse return null)[0..count];
}

const MemoryRange = struct {
    start: usize,
    end: usize,
};

fn memoryRange(
    pointer: ?*const anyopaque,
    count: usize,
    item_size: usize,
) !?MemoryRange {
    if (count == 0) return null;
    const address = pointer orelse return error.InvalidArgument;
    const byte_count = std.math.mul(usize, count, item_size) catch
        return error.InvalidArgument;
    const start = @intFromPtr(address);
    const end = std.math.add(usize, start, byte_count) catch
        return error.InvalidArgument;
    if (end <= start) return error.InvalidArgument;
    return .{ .start = start, .end = end };
}

fn rangesOverlap(left: ?MemoryRange, right: ?MemoryRange) bool {
    const left_value = left orelse return false;
    const right_value = right orelse return false;
    return left_value.start < right_value.end and right_value.start < left_value.end;
}

fn validateDisjointMemory(ranges: []const ?MemoryRange) !void {
    for (ranges, 0..) |candidate, index| {
        for (ranges[index + 1 ..]) |other| {
            if (rangesOverlap(candidate, other)) return error.InvalidArgument;
        }
    }
}

fn validateCapacityPlanMemory(
    state_pointer: ?*anyopaque,
    state_size: u64,
    input_pointer: ?*const protocol.CapacityPlanInput,
    scratch_pointer: ?[*]protocol.Intent,
    scratch_capacity: u32,
    output_pointer: ?[*]protocol.Intent,
    output_capacity: u32,
    table_output_pointer: ?[*]protocol.CapacityTablePlanView,
    table_output_capacity: u32,
    summary_pointer: ?*protocol.PlanSummary,
) !void {
    const state_length = std.math.cast(usize, state_size) orelse
        return error.InvalidArgument;
    const state_range = try memoryRange(
        if (state_pointer) |pointer| @ptrCast(pointer) else null,
        state_length,
        1,
    );
    const input_range = try memoryRange(
        if (input_pointer) |pointer| @ptrCast(pointer) else null,
        1,
        @sizeOf(protocol.CapacityPlanInput),
    );
    const scratch_range = try memoryRange(
        if (scratch_pointer) |pointer| @ptrCast(pointer) else null,
        scratch_capacity,
        @sizeOf(protocol.Intent),
    );
    const output_range = try memoryRange(
        if (output_pointer) |pointer| @ptrCast(pointer) else null,
        output_capacity,
        @sizeOf(protocol.Intent),
    );
    const table_output_range = try memoryRange(
        if (table_output_pointer) |pointer| @ptrCast(pointer) else null,
        table_output_capacity,
        @sizeOf(protocol.CapacityTablePlanView),
    );
    const summary_range = try memoryRange(
        if (summary_pointer) |pointer| @ptrCast(pointer) else null,
        1,
        @sizeOf(protocol.PlanSummary),
    );
    const mutable_ranges = [_]?MemoryRange{
        scratch_range,
        output_range,
        table_output_range,
        summary_range,
    };
    for (mutable_ranges, 0..) |candidate, index| {
        if (rangesOverlap(candidate, state_range) or rangesOverlap(candidate, input_range)) {
            return error.InvalidArgument;
        }
        for (mutable_ranges[index + 1 ..]) |other| {
            if (rangesOverlap(candidate, other)) return error.InvalidArgument;
        }
    }
}

pub export fn rm_local_resource_manager_abi_version() callconv(.c) u32 {
    return protocol.abi_version;
}

pub export fn rm_local_resource_manager_layout_fingerprint() callconv(.c) u64 {
    const values = [_]usize{
        @sizeOf(protocol.Config),
        @offsetOf(protocol.Config, "configuration_generation"),
        @offsetOf(protocol.Config, "uid_bucket_capacity"),
        @offsetOf(protocol.Config, "partition_capacity"),
        @offsetOf(protocol.Config, "concentrated_trigger_free_percent"),
        @sizeOf(protocol.TableSpec),
        @offsetOf(protocol.TableSpec, "capacity"),
        @offsetOf(protocol.TableSpec, "partition_reservation_start"),
        @offsetOf(protocol.TableSpec, "partition_reservation_capacity"),
        @sizeOf(protocol.TableHandle),
        @offsetOf(protocol.TableHandle, "table_id"),
        @offsetOf(protocol.TableHandle, "table_incarnation"),
        @sizeOf(protocol.CapabilityBinding),
        @sizeOf(protocol.ResourceSpec),
        @offsetOf(protocol.ResourceSpec, "size_bytes"),
        @offsetOf(protocol.ResourceSpec, "recovery_cost_coefficient"),
        @offsetOf(protocol.ResourceSpec, "recoverability"),
        @sizeOf(protocol.ResourceHandle),
        @offsetOf(protocol.ResourceHandle, "resource_uid"),
        @sizeOf(protocol.PartitionHandle),
        @offsetOf(protocol.PartitionHandle, "partition_slot_generation"),
        @offsetOf(protocol.PartitionHandle, "partition_slot_index"),
        @sizeOf(protocol.OperationToken),
        @offsetOf(protocol.OperationToken, "operation_id"),
        @offsetOf(protocol.OperationToken, "operation_generation"),
        @sizeOf(protocol.OperationView),
        @offsetOf(protocol.OperationView, "phase"),
        @sizeOf(protocol.Intent),
        @offsetOf(protocol.Intent, "operation_id"),
        @offsetOf(protocol.Intent, "snapshot_generation"),
        @offsetOf(protocol.Intent, "capability_generation"),
        @offsetOf(protocol.Intent, "reason_flags"),
        @offsetOf(protocol.Intent, "reclaim_partition_slot_generation"),
        @sizeOf(protocol.PlanStamp),
        @offsetOf(protocol.PlanStamp, "manager_instance_id"),
        @offsetOf(protocol.PlanStamp, "operation_id"),
        @offsetOf(protocol.PlanStamp, "snapshot_generation"),
        @offsetOf(protocol.PlanStamp, "kind"),
        @sizeOf(protocol.ExecutionToken),
        @offsetOf(protocol.ExecutionToken, "attempt_id"),
        @sizeOf(protocol.TypedEffect),
        @offsetOf(protocol.TypedEffect, "outcome"),
        @offsetOf(protocol.TypedEffect, "changes"),
        @sizeOf(protocol.CapacityTablePlanView),
        @offsetOf(protocol.CapacityTablePlanView, "flags"),
        @sizeOf(protocol.TableCapacityView),
        @offsetOf(protocol.TableCapacityView, "direct_capacity"),
        @offsetOf(protocol.TableCapacityView, "partition_reservation_start"),
        @sizeOf(protocol.PartitionCreateInput),
        @offsetOf(protocol.PartitionCreateInput, "parent"),
        @sizeOf(protocol.PartitionAdmissionTicket),
        @offsetOf(protocol.PartitionAdmissionTicket, "admission_id"),
        @offsetOf(protocol.PartitionAdmissionTicket, "victim_hash"),
        @offsetOf(protocol.PartitionAdmissionTicket, "kind"),
        @sizeOf(protocol.PartitionAdmissionScratch),
        @offsetOf(protocol.PartitionAdmissionScratch, "selection_rank"),
        @sizeOf(protocol.PartitionView),
        @sizeOf(protocol.PartitionCellView),
        @sizeOf(protocol.PartitionResourceAdmission),
    };
    var hash: u64 = 14695981039346656037;
    for (values) |value| hash = (hash ^ @as(u64, @intCast(value))) *% 1099511628211;
    return hash;
}

pub export fn rm_local_resource_manager_config_size() callconv(.c) u32 {
    return @sizeOf(protocol.Config);
}

pub export fn rm_local_resource_manager_table_spec_size() callconv(.c) u32 {
    return @sizeOf(protocol.TableSpec);
}

pub export fn rm_local_resource_manager_table_handle_size() callconv(.c) u32 {
    return @sizeOf(protocol.TableHandle);
}

pub export fn rm_local_resource_manager_capability_spec_size() callconv(.c) u32 {
    return @sizeOf(protocol.CapabilitySpec);
}

pub export fn rm_local_resource_manager_capability_handle_size() callconv(.c) u32 {
    return @sizeOf(protocol.CapabilityHandle);
}

pub export fn rm_local_resource_manager_resource_spec_size() callconv(.c) u32 {
    return @sizeOf(protocol.ResourceSpec);
}

pub export fn rm_local_resource_manager_resource_handle_size() callconv(.c) u32 {
    return @sizeOf(protocol.ResourceHandle);
}

pub export fn rm_local_resource_manager_partition_handle_size() callconv(.c) u32 {
    return @sizeOf(protocol.PartitionHandle);
}

pub export fn rm_local_resource_manager_partition_create_input_size() callconv(.c) u32 {
    return @sizeOf(protocol.PartitionCreateInput);
}

pub export fn rm_local_resource_manager_partition_admission_ticket_size() callconv(.c) u32 {
    return @sizeOf(protocol.PartitionAdmissionTicket);
}

pub export fn rm_local_resource_manager_partition_admission_summary_size() callconv(.c) u32 {
    return @sizeOf(protocol.PartitionAdmissionSummary);
}

pub export fn rm_local_resource_manager_partition_admission_scratch_size() callconv(.c) u32 {
    return @sizeOf(protocol.PartitionAdmissionScratch);
}

pub export fn rm_local_resource_manager_partition_view_size() callconv(.c) u32 {
    return @sizeOf(protocol.PartitionView);
}

pub export fn rm_local_resource_manager_partition_cell_view_size() callconv(.c) u32 {
    return @sizeOf(protocol.PartitionCellView);
}

pub export fn rm_local_resource_manager_partition_resource_admission_size() callconv(.c) u32 {
    return @sizeOf(protocol.PartitionResourceAdmission);
}

pub export fn rm_local_resource_manager_use_lease_size() callconv(.c) u32 {
    return @sizeOf(protocol.UseLease);
}

pub export fn rm_local_resource_manager_operation_token_size() callconv(.c) u32 {
    return @sizeOf(protocol.OperationToken);
}

pub export fn rm_local_resource_manager_operation_view_size() callconv(.c) u32 {
    return @sizeOf(protocol.OperationView);
}

pub export fn rm_local_resource_manager_capacity_plan_input_size() callconv(.c) u32 {
    return @sizeOf(protocol.CapacityPlanInput);
}

pub export fn rm_local_resource_manager_mode_plan_input_size() callconv(.c) u32 {
    return @sizeOf(protocol.ModePlanInput);
}

pub export fn rm_local_resource_manager_intent_size() callconv(.c) u32 {
    return @sizeOf(protocol.Intent);
}

pub export fn rm_local_resource_manager_plan_stamp_size() callconv(.c) u32 {
    return @sizeOf(protocol.PlanStamp);
}

pub export fn rm_local_resource_manager_execution_token_size() callconv(.c) u32 {
    return @sizeOf(protocol.ExecutionToken);
}

pub export fn rm_local_resource_manager_typed_effect_size() callconv(.c) u32 {
    return @sizeOf(protocol.TypedEffect);
}

pub export fn rm_local_resource_manager_plan_summary_size() callconv(.c) u32 {
    return @sizeOf(protocol.PlanSummary);
}

pub export fn rm_local_resource_manager_commit_receipt_size() callconv(.c) u32 {
    return @sizeOf(protocol.CommitReceipt);
}

pub export fn rm_local_resource_manager_table_capacity_view_size() callconv(.c) u32 {
    return @sizeOf(protocol.TableCapacityView);
}

pub export fn rm_local_resource_manager_capacity_table_plan_view_size() callconv(.c) u32 {
    return @sizeOf(protocol.CapacityTablePlanView);
}

pub export fn rm_local_resource_manager_resource_view_size() callconv(.c) u32 {
    return @sizeOf(protocol.ResourceView);
}

pub export fn rm_local_resource_manager_state_alignment() callconv(.c) u32 {
    return @intCast(state.alignment());
}

pub export fn rm_local_resource_manager_state_required_size(
    config_pointer: ?*const protocol.Config,
    output_size: ?*u64,
) callconv(.c) i32 {
    const config = config_pointer orelse return result(.invalid_argument);
    const output = output_size orelse return result(.invalid_argument);
    output.* = @intCast(state.requiredBytes(config.*) catch |err| return mapError(err));
    return result(.ok);
}

pub export fn rm_local_resource_manager_state_initialize(
    state_pointer: ?*anyopaque,
    state_size: u64,
    config_pointer: ?*const protocol.Config,
    manager_instance_id: u64,
) callconv(.c) i32 {
    const buffer = mutableBytes(state_pointer, state_size) orelse return result(.invalid_argument);
    const config = config_pointer orelse return result(.invalid_argument);
    state.initialize(buffer, config.*, manager_instance_id) catch |err| return mapError(err);
    return result(.ok);
}

pub export fn rm_local_resource_manager_begin_operation(
    state_pointer: ?*anyopaque,
    state_size: u64,
    kind_value: u8,
    output_pointer: ?*protocol.OperationToken,
) callconv(.c) i32 {
    const state_length = std.math.cast(usize, state_size) orelse return result(.invalid_argument);
    const state_range = memoryRange(state_pointer, state_length, 1) catch
        return result(.invalid_argument);
    const output_range = memoryRange(output_pointer, 1, @sizeOf(protocol.OperationToken)) catch
        return result(.invalid_argument);
    if (rangesOverlap(state_range, output_range)) return result(.invalid_argument);
    if (kind_value < @intFromEnum(protocol.OperationKind.maintenance) or
        kind_value > @intFromEnum(protocol.OperationKind.partition_resource_admission))
    {
        return result(.invalid_argument);
    }
    const kind: protocol.OperationKind = @enumFromInt(kind_value);
    const view = openState(state_pointer, state_size) catch |err| return mapError(err);
    const output = output_pointer orelse return result(.invalid_argument);
    output.* = operation_sequence.begin(view, kind) catch |err| return mapError(err);
    return result(.ok);
}

pub export fn rm_local_resource_manager_read_operation(
    state_pointer: ?*anyopaque,
    state_size: u64,
    output_pointer: ?*protocol.OperationView,
) callconv(.c) i32 {
    const state_length = std.math.cast(usize, state_size) orelse return result(.invalid_argument);
    const state_range = memoryRange(state_pointer, state_length, 1) catch
        return result(.invalid_argument);
    const output_range = memoryRange(output_pointer, 1, @sizeOf(protocol.OperationView)) catch
        return result(.invalid_argument);
    if (rangesOverlap(state_range, output_range)) return result(.invalid_argument);
    const view = openState(state_pointer, state_size) catch |err| return mapError(err);
    const output = output_pointer orelse return result(.invalid_argument);
    output.* = operation_sequence.read(view);
    return result(.ok);
}

pub export fn rm_local_resource_manager_finish_operation(
    state_pointer: ?*anyopaque,
    state_size: u64,
    token_pointer: ?*const protocol.OperationToken,
) callconv(.c) i32 {
    const state_length = std.math.cast(usize, state_size) orelse return result(.invalid_argument);
    const state_range = memoryRange(state_pointer, state_length, 1) catch
        return result(.invalid_argument);
    const token_range = memoryRange(token_pointer, 1, @sizeOf(protocol.OperationToken)) catch
        return result(.invalid_argument);
    if (rangesOverlap(state_range, token_range)) return result(.invalid_argument);
    const view = openState(state_pointer, state_size) catch |err| return mapError(err);
    const token = token_pointer orelse return result(.invalid_argument);
    operation_sequence.finish(view, token.*) catch |err| return mapError(err);
    return result(.ok);
}

pub export fn rm_local_resource_manager_register_table(
    state_pointer: ?*anyopaque,
    state_size: u64,
    operation_pointer: ?*const protocol.OperationToken,
    spec_pointer: ?*const protocol.TableSpec,
    handle_pointer: ?*protocol.TableHandle,
) callconv(.c) i32 {
    const state_length = std.math.cast(usize, state_size) orelse return result(.invalid_argument);
    const ranges = [_]?MemoryRange{
        memoryRange(state_pointer, state_length, 1) catch return result(.invalid_argument),
        memoryRange(operation_pointer, 1, @sizeOf(protocol.OperationToken)) catch
            return result(.invalid_argument),
        memoryRange(spec_pointer, 1, @sizeOf(protocol.TableSpec)) catch
            return result(.invalid_argument),
        memoryRange(handle_pointer, 1, @sizeOf(protocol.TableHandle)) catch
            return result(.invalid_argument),
    };
    validateDisjointMemory(&ranges) catch return result(.invalid_argument);
    const view = openOperationState(state_pointer, state_size, operation_pointer, .maintenance) catch |err|
        return mapError(err);
    const spec = spec_pointer orelse return result(.invalid_argument);
    const output = handle_pointer orelse return result(.invalid_argument);
    output.* = state.registerTable(view, spec.*) catch |err| return mapError(err);
    return result(.ok);
}

pub export fn rm_local_resource_manager_unregister_table(
    state_pointer: ?*anyopaque,
    state_size: u64,
    operation_pointer: ?*const protocol.OperationToken,
    handle_pointer: ?*const protocol.TableHandle,
) callconv(.c) i32 {
    const view = openOperationState(state_pointer, state_size, operation_pointer, .maintenance) catch |err|
        return mapError(err);
    const handle = handle_pointer orelse return result(.invalid_argument);
    state.unregisterTable(view, handle.*) catch |err| return mapError(err);
    return result(.ok);
}

pub export fn rm_local_resource_manager_register_capability(
    state_pointer: ?*anyopaque,
    state_size: u64,
    operation_pointer: ?*const protocol.OperationToken,
    table_pointer: ?*const protocol.TableHandle,
    spec_pointer: ?*const protocol.CapabilitySpec,
    handle_pointer: ?*protocol.CapabilityHandle,
) callconv(.c) i32 {
    const view = openOperationState(state_pointer, state_size, operation_pointer, .maintenance) catch |err|
        return mapError(err);
    const table = table_pointer orelse return result(.invalid_argument);
    const spec = spec_pointer orelse return result(.invalid_argument);
    const output = handle_pointer orelse return result(.invalid_argument);
    output.* = state.registerCapability(view, table.*, spec.*) catch |err| return mapError(err);
    return result(.ok);
}

pub export fn rm_local_resource_manager_replace_capability(
    state_pointer: ?*anyopaque,
    state_size: u64,
    operation_pointer: ?*const protocol.OperationToken,
    handle_pointer: ?*const protocol.CapabilityHandle,
    spec_pointer: ?*const protocol.CapabilitySpec,
    replacement_pointer: ?*protocol.CapabilityHandle,
) callconv(.c) i32 {
    const view = openOperationState(state_pointer, state_size, operation_pointer, .maintenance) catch |err|
        return mapError(err);
    const handle = handle_pointer orelse return result(.invalid_argument);
    const spec = spec_pointer orelse return result(.invalid_argument);
    const output = replacement_pointer orelse return result(.invalid_argument);
    output.* = state.replaceCapability(view, handle.*, spec.*) catch |err| return mapError(err);
    return result(.ok);
}

pub export fn rm_local_resource_manager_unregister_capability(
    state_pointer: ?*anyopaque,
    state_size: u64,
    operation_pointer: ?*const protocol.OperationToken,
    handle_pointer: ?*const protocol.CapabilityHandle,
) callconv(.c) i32 {
    const view = openOperationState(state_pointer, state_size, operation_pointer, .maintenance) catch |err|
        return mapError(err);
    const handle = handle_pointer orelse return result(.invalid_argument);
    state.unregisterCapability(view, handle.*) catch |err| return mapError(err);
    return result(.ok);
}

pub export fn rm_local_resource_manager_register_resource(
    state_pointer: ?*anyopaque,
    state_size: u64,
    operation_pointer: ?*const protocol.OperationToken,
    table_pointer: ?*const protocol.TableHandle,
    spec_pointer: ?*const protocol.ResourceSpec,
    handle_pointer: ?*protocol.ResourceHandle,
) callconv(.c) i32 {
    const state_length = std.math.cast(usize, state_size) orelse return result(.invalid_argument);
    const ranges = [_]?MemoryRange{
        memoryRange(state_pointer, state_length, 1) catch return result(.invalid_argument),
        memoryRange(operation_pointer, 1, @sizeOf(protocol.OperationToken)) catch
            return result(.invalid_argument),
        memoryRange(table_pointer, 1, @sizeOf(protocol.TableHandle)) catch
            return result(.invalid_argument),
        memoryRange(spec_pointer, 1, @sizeOf(protocol.ResourceSpec)) catch
            return result(.invalid_argument),
        memoryRange(handle_pointer, 1, @sizeOf(protocol.ResourceHandle)) catch
            return result(.invalid_argument),
    };
    validateDisjointMemory(&ranges) catch return result(.invalid_argument);
    const view = openOperationState(state_pointer, state_size, operation_pointer, .maintenance) catch |err|
        return mapError(err);
    const table = table_pointer orelse return result(.invalid_argument);
    const spec = spec_pointer orelse return result(.invalid_argument);
    const output = handle_pointer orelse return result(.invalid_argument);
    output.* = state.registerResource(view, table.*, spec.*) catch |err| return mapError(err);
    return result(.ok);
}

pub export fn rm_local_resource_manager_register_partition_resource(
    state_pointer: ?*anyopaque,
    state_size: u64,
    operation_pointer: ?*const protocol.OperationToken,
    partition_pointer: ?*const protocol.PartitionHandle,
    local_ordinal: u32,
    spec_pointer: ?*const protocol.ResourceSpec,
    handle_pointer: ?*protocol.ResourceHandle,
) callconv(.c) i32 {
    const state_length = std.math.cast(usize, state_size) orelse return result(.invalid_argument);
    const ranges = [_]?MemoryRange{
        memoryRange(state_pointer, state_length, 1) catch return result(.invalid_argument),
        memoryRange(operation_pointer, 1, @sizeOf(protocol.OperationToken)) catch
            return result(.invalid_argument),
        memoryRange(partition_pointer, 1, @sizeOf(protocol.PartitionHandle)) catch
            return result(.invalid_argument),
        memoryRange(spec_pointer, 1, @sizeOf(protocol.ResourceSpec)) catch
            return result(.invalid_argument),
        memoryRange(handle_pointer, 1, @sizeOf(protocol.ResourceHandle)) catch
            return result(.invalid_argument),
    };
    validateDisjointMemory(&ranges) catch return result(.invalid_argument);
    const view = openOperationState(state_pointer, state_size, operation_pointer, .maintenance) catch |err|
        return mapError(err);
    const partition_handle = partition_pointer orelse return result(.invalid_argument);
    const spec = spec_pointer orelse return result(.invalid_argument);
    const output = handle_pointer orelse return result(.invalid_argument);
    output.* = partition.registerResourceAt(
        view,
        partition_handle.*,
        local_ordinal,
        spec.*,
    ) catch |err| return mapError(err);
    return result(.ok);
}

pub export fn rm_local_resource_manager_update_resource(
    state_pointer: ?*anyopaque,
    state_size: u64,
    operation_pointer: ?*const protocol.OperationToken,
    handle_pointer: ?*const protocol.ResourceHandle,
    spec_pointer: ?*const protocol.ResourceSpec,
) callconv(.c) i32 {
    const view = openOperationState(state_pointer, state_size, operation_pointer, .maintenance) catch |err|
        return mapError(err);
    const handle = handle_pointer orelse return result(.invalid_argument);
    const spec = spec_pointer orelse return result(.invalid_argument);
    state.updateResource(view, handle.*, spec.*) catch |err| return mapError(err);
    return result(.ok);
}

pub export fn rm_local_resource_manager_unregister_resource(
    state_pointer: ?*anyopaque,
    state_size: u64,
    operation_pointer: ?*const protocol.OperationToken,
    handle_pointer: ?*const protocol.ResourceHandle,
) callconv(.c) i32 {
    const view = openOperationState(state_pointer, state_size, operation_pointer, .maintenance) catch |err|
        return mapError(err);
    const handle = handle_pointer orelse return result(.invalid_argument);
    state.unregisterResource(view, handle.*) catch |err| return mapError(err);
    return result(.ok);
}

pub export fn rm_local_resource_manager_touch_resource(
    state_pointer: ?*anyopaque,
    state_size: u64,
    operation_pointer: ?*const protocol.OperationToken,
    handle_pointer: ?*const protocol.ResourceHandle,
    weight: u32,
) callconv(.c) i32 {
    const view = openOperationState(state_pointer, state_size, operation_pointer, .maintenance) catch |err|
        return mapError(err);
    const handle = handle_pointer orelse return result(.invalid_argument);
    state.touchResource(view, handle.*, weight) catch |err| return mapError(err);
    return result(.ok);
}

pub export fn rm_local_resource_manager_advance_activity_epoch(
    state_pointer: ?*anyopaque,
    state_size: u64,
    operation_pointer: ?*const protocol.OperationToken,
    epoch_count: u64,
) callconv(.c) i32 {
    const view = openOperationState(state_pointer, state_size, operation_pointer, null) catch |err|
        return mapError(err);
    if (view.header.active_operation_kind != .maintenance and
        view.header.active_operation_kind != .cleanup)
    {
        return result(.invalid_operation_phase);
    }
    state.advanceActivityEpoch(view, epoch_count) catch |err| return mapError(err);
    return result(.ok);
}

pub export fn rm_local_resource_manager_begin_use(
    state_pointer: ?*anyopaque,
    state_size: u64,
    operation_pointer: ?*const protocol.OperationToken,
    handle_pointer: ?*const protocol.ResourceHandle,
    lease_pointer: ?*protocol.UseLease,
) callconv(.c) i32 {
    const view = openOperationState(state_pointer, state_size, operation_pointer, .maintenance) catch |err|
        return mapError(err);
    const handle = handle_pointer orelse return result(.invalid_argument);
    const output = lease_pointer orelse return result(.invalid_argument);
    output.* = state.beginUse(view, handle.*) catch |err| return mapError(err);
    return result(.ok);
}

pub export fn rm_local_resource_manager_end_use(
    state_pointer: ?*anyopaque,
    state_size: u64,
    operation_pointer: ?*const protocol.OperationToken,
    lease_pointer: ?*const protocol.UseLease,
) callconv(.c) i32 {
    const view = openOperationState(state_pointer, state_size, operation_pointer, .maintenance) catch |err|
        return mapError(err);
    const lease = lease_pointer orelse return result(.invalid_argument);
    state.endUse(view, lease.*) catch |err| return mapError(err);
    return result(.ok);
}

pub export fn rm_local_resource_manager_plan_capacity(
    state_pointer: ?*anyopaque,
    state_size: u64,
    operation_pointer: ?*const protocol.OperationToken,
    input_pointer: ?*const protocol.CapacityPlanInput,
    scratch_pointer: ?[*]protocol.Intent,
    scratch_capacity: u32,
    output_pointer: ?[*]protocol.Intent,
    output_capacity: u32,
    summary_pointer: ?*protocol.PlanSummary,
) callconv(.c) i32 {
    validateCapacityPlanMemory(
        state_pointer,
        state_size,
        input_pointer,
        scratch_pointer,
        scratch_capacity,
        output_pointer,
        output_capacity,
        null,
        0,
        summary_pointer,
    ) catch return result(.invalid_argument);
    const view = openOperationState(state_pointer, state_size, operation_pointer, .cleanup) catch |err|
        return mapError(err);
    const input = input_pointer orelse return result(.invalid_argument);
    const scratch = mutableSlice(protocol.Intent, scratch_pointer, scratch_capacity) orelse
        return result(.invalid_argument);
    const output = mutableSlice(protocol.Intent, output_pointer, output_capacity) orelse
        return result(.invalid_argument);
    const summary = summary_pointer orelse return result(.invalid_argument);
    summary.* = capacity_guard.plan(view, input.*, scratch, output) catch |err| return mapError(err);
    return result(.ok);
}

pub export fn rm_local_resource_manager_plan_capacity_detailed(
    state_pointer: ?*anyopaque,
    state_size: u64,
    operation_pointer: ?*const protocol.OperationToken,
    input_pointer: ?*const protocol.CapacityPlanInput,
    scratch_pointer: ?[*]protocol.Intent,
    scratch_capacity: u32,
    output_pointer: ?[*]protocol.Intent,
    output_capacity: u32,
    table_output_pointer: ?[*]protocol.CapacityTablePlanView,
    table_output_capacity: u32,
    summary_pointer: ?*protocol.PlanSummary,
) callconv(.c) i32 {
    validateCapacityPlanMemory(
        state_pointer,
        state_size,
        input_pointer,
        scratch_pointer,
        scratch_capacity,
        output_pointer,
        output_capacity,
        table_output_pointer,
        table_output_capacity,
        summary_pointer,
    ) catch return result(.invalid_argument);
    const view = openOperationState(state_pointer, state_size, operation_pointer, .cleanup) catch |err|
        return mapError(err);
    const input = input_pointer orelse return result(.invalid_argument);
    const scratch = mutableSlice(protocol.Intent, scratch_pointer, scratch_capacity) orelse
        return result(.invalid_argument);
    const output = mutableSlice(protocol.Intent, output_pointer, output_capacity) orelse
        return result(.invalid_argument);
    const table_output = mutableSlice(
        protocol.CapacityTablePlanView,
        table_output_pointer,
        table_output_capacity,
    ) orelse return result(.invalid_argument);
    const summary = summary_pointer orelse return result(.invalid_argument);
    summary.* = capacity_guard.planDetailed(
        view,
        input.*,
        scratch,
        output,
        table_output,
    ) catch |err| return mapError(err);
    return result(.ok);
}

pub export fn rm_local_resource_manager_plan_mode(
    state_pointer: ?*anyopaque,
    state_size: u64,
    operation_pointer: ?*const protocol.OperationToken,
    input_pointer: ?*const protocol.ModePlanInput,
    output_pointer: ?[*]protocol.Intent,
    output_capacity: u32,
    summary_pointer: ?*protocol.PlanSummary,
) callconv(.c) i32 {
    const view = openOperationState(state_pointer, state_size, operation_pointer, .cleanup) catch |err|
        return mapError(err);
    const input = input_pointer orelse return result(.invalid_argument);
    const output = mutableSlice(protocol.Intent, output_pointer, output_capacity) orelse
        return result(.invalid_argument);
    const summary = summary_pointer orelse return result(.invalid_argument);
    summary.* = mode_cleanup.plan(view, input.*, output) catch |err| return mapError(err);
    return result(.ok);
}

pub export fn rm_local_resource_manager_merge_plans_v8(
    capacity_stamp_pointer: ?*const protocol.PlanStamp,
    mode_stamp_pointer: ?*const protocol.PlanStamp,
    capacity_pointer: ?[*]const protocol.Intent,
    capacity_count: u32,
    mode_pointer: ?[*]const protocol.Intent,
    mode_count: u32,
    output_pointer: ?[*]protocol.Intent,
    output_capacity: u32,
    summary_pointer: ?*protocol.PlanSummary,
) callconv(.c) i32 {
    const capacity_stamp_range = memoryRange(
        if (capacity_stamp_pointer) |pointer| @ptrCast(pointer) else null,
        1,
        @sizeOf(protocol.PlanStamp),
    ) catch return result(.invalid_argument);
    const mode_stamp_range = memoryRange(
        if (mode_stamp_pointer) |pointer| @ptrCast(pointer) else null,
        1,
        @sizeOf(protocol.PlanStamp),
    ) catch return result(.invalid_argument);
    const capacity_range = memoryRange(
        if (capacity_pointer) |pointer| @ptrCast(pointer) else null,
        capacity_count,
        @sizeOf(protocol.Intent),
    ) catch return result(.invalid_argument);
    const mode_range = memoryRange(
        if (mode_pointer) |pointer| @ptrCast(pointer) else null,
        mode_count,
        @sizeOf(protocol.Intent),
    ) catch return result(.invalid_argument);
    const output_range = memoryRange(
        if (output_pointer) |pointer| @ptrCast(pointer) else null,
        output_capacity,
        @sizeOf(protocol.Intent),
    ) catch return result(.invalid_argument);
    const summary_range = memoryRange(
        if (summary_pointer) |pointer| @ptrCast(pointer) else null,
        1,
        @sizeOf(protocol.PlanSummary),
    ) catch return result(.invalid_argument);
    if (rangesOverlap(output_range, capacity_range) or
        rangesOverlap(output_range, mode_range) or
        rangesOverlap(output_range, capacity_stamp_range) or
        rangesOverlap(output_range, mode_stamp_range) or
        rangesOverlap(output_range, summary_range) or
        rangesOverlap(summary_range, capacity_range) or
        rangesOverlap(summary_range, mode_range) or
        rangesOverlap(summary_range, capacity_stamp_range) or
        rangesOverlap(summary_range, mode_stamp_range))
    {
        return result(.invalid_argument);
    }

    const capacity_stamp = (capacity_stamp_pointer orelse
        return result(.invalid_argument)).*;
    const mode_stamp = (mode_stamp_pointer orelse
        return result(.invalid_argument)).*;
    const capacity = constSlice(protocol.Intent, capacity_pointer, capacity_count) orelse
        return result(.invalid_argument);
    const mode = constSlice(protocol.Intent, mode_pointer, mode_count) orelse
        return result(.invalid_argument);
    const output = mutableSlice(protocol.Intent, output_pointer, output_capacity) orelse
        return result(.invalid_argument);
    const summary = summary_pointer orelse return result(.invalid_argument);
    const merged_summary = intent_merge.merge(
        capacity_stamp,
        mode_stamp,
        capacity,
        mode,
        output,
    ) catch |err| return mapError(err);
    summary.* = merged_summary;
    return result(.ok);
}

pub export fn rm_local_resource_manager_plan_partition_admission(
    state_pointer: ?*anyopaque,
    state_size: u64,
    operation_pointer: ?*const protocol.OperationToken,
    input_pointer: ?*const protocol.PartitionCreateInput,
    victim_output_pointer: ?[*]protocol.PartitionHandle,
    victim_output_capacity: u32,
    intent_output_pointer: ?[*]protocol.Intent,
    intent_output_capacity: u32,
    scratch_pointer: ?[*]protocol.PartitionAdmissionScratch,
    scratch_capacity: u32,
    summary_pointer: ?*protocol.PartitionAdmissionSummary,
    ticket_pointer: ?*protocol.PartitionAdmissionTicket,
) callconv(.c) i32 {
    const state_length = std.math.cast(usize, state_size) orelse return result(.invalid_argument);
    const state_range = memoryRange(state_pointer, state_length, 1) catch
        return result(.invalid_argument);
    const input_range = memoryRange(input_pointer, 1, @sizeOf(protocol.PartitionCreateInput)) catch
        return result(.invalid_argument);
    const victim_range = memoryRange(
        victim_output_pointer,
        victim_output_capacity,
        @sizeOf(protocol.PartitionHandle),
    ) catch return result(.invalid_argument);
    const intent_range = memoryRange(
        intent_output_pointer,
        intent_output_capacity,
        @sizeOf(protocol.Intent),
    ) catch return result(.invalid_argument);
    const scratch_range = memoryRange(
        scratch_pointer,
        scratch_capacity,
        @sizeOf(protocol.PartitionAdmissionScratch),
    ) catch return result(.invalid_argument);
    const summary_range = memoryRange(
        summary_pointer,
        1,
        @sizeOf(protocol.PartitionAdmissionSummary),
    ) catch return result(.invalid_argument);
    const ticket_range = memoryRange(
        ticket_pointer,
        1,
        @sizeOf(protocol.PartitionAdmissionTicket),
    ) catch return result(.invalid_argument);
    const ranges = [_]?MemoryRange{
        state_range,
        input_range,
        victim_range,
        intent_range,
        scratch_range,
        summary_range,
        ticket_range,
    };
    validateDisjointMemory(&ranges) catch return result(.invalid_argument);

    const view = openOperationState(state_pointer, state_size, operation_pointer, .partition_admission) catch |err|
        return mapError(err);
    const input = input_pointer orelse return result(.invalid_argument);
    const victims = mutableSlice(
        protocol.PartitionHandle,
        victim_output_pointer,
        victim_output_capacity,
    ) orelse return result(.invalid_argument);
    const intents = mutableSlice(
        protocol.Intent,
        intent_output_pointer,
        intent_output_capacity,
    ) orelse return result(.invalid_argument);
    const scratch = mutableSlice(
        protocol.PartitionAdmissionScratch,
        scratch_pointer,
        scratch_capacity,
    ) orelse return result(.invalid_argument);
    const summary = summary_pointer orelse return result(.invalid_argument);
    const ticket = ticket_pointer orelse return result(.invalid_argument);
    const planned = partition.planAdmission(view, input.*, victims, intents, scratch) catch |err|
        return mapError(err);
    summary.* = planned.summary;
    ticket.* = planned.ticket;
    return result(.ok);
}

pub export fn rm_local_resource_manager_commit_partition_admission(
    state_pointer: ?*anyopaque,
    state_size: u64,
    operation_pointer: ?*const protocol.OperationToken,
    ticket_pointer: ?*const protocol.PartitionAdmissionTicket,
    victim_pointer: ?[*]const protocol.PartitionHandle,
    victim_count: u32,
    handle_pointer: ?*protocol.PartitionHandle,
) callconv(.c) i32 {
    const state_length = std.math.cast(usize, state_size) orelse return result(.invalid_argument);
    const ranges = [_]?MemoryRange{
        memoryRange(state_pointer, state_length, 1) catch return result(.invalid_argument),
        memoryRange(ticket_pointer, 1, @sizeOf(protocol.PartitionAdmissionTicket)) catch
            return result(.invalid_argument),
        memoryRange(victim_pointer, victim_count, @sizeOf(protocol.PartitionHandle)) catch
            return result(.invalid_argument),
        memoryRange(handle_pointer, 1, @sizeOf(protocol.PartitionHandle)) catch
            return result(.invalid_argument),
    };
    validateDisjointMemory(&ranges) catch return result(.invalid_argument);
    const view = openOperationState(state_pointer, state_size, operation_pointer, .partition_admission) catch |err|
        return mapError(err);
    const ticket = ticket_pointer orelse return result(.invalid_argument);
    const victims = constSlice(protocol.PartitionHandle, victim_pointer, victim_count) orelse
        return result(.invalid_argument);
    const output = handle_pointer orelse return result(.invalid_argument);
    output.* = partition.commitAdmission(view, ticket.*, victims) catch |err| return mapError(err);
    return result(.ok);
}

pub export fn rm_local_resource_manager_plan_partition_close(
    state_pointer: ?*anyopaque,
    state_size: u64,
    operation_pointer: ?*const protocol.OperationToken,
    partition_pointer: ?*const protocol.PartitionHandle,
    intent_output_pointer: ?[*]protocol.Intent,
    intent_output_capacity: u32,
    summary_pointer: ?*protocol.PartitionAdmissionSummary,
    ticket_pointer: ?*protocol.PartitionAdmissionTicket,
) callconv(.c) i32 {
    const state_length = std.math.cast(usize, state_size) orelse return result(.invalid_argument);
    const ranges = [_]?MemoryRange{
        memoryRange(state_pointer, state_length, 1) catch return result(.invalid_argument),
        memoryRange(partition_pointer, 1, @sizeOf(protocol.PartitionHandle)) catch
            return result(.invalid_argument),
        memoryRange(intent_output_pointer, intent_output_capacity, @sizeOf(protocol.Intent)) catch
            return result(.invalid_argument),
        memoryRange(summary_pointer, 1, @sizeOf(protocol.PartitionAdmissionSummary)) catch
            return result(.invalid_argument),
        memoryRange(ticket_pointer, 1, @sizeOf(protocol.PartitionAdmissionTicket)) catch
            return result(.invalid_argument),
    };
    validateDisjointMemory(&ranges) catch return result(.invalid_argument);
    const view = openOperationState(state_pointer, state_size, operation_pointer, .partition_admission) catch |err|
        return mapError(err);
    const target = partition_pointer orelse return result(.invalid_argument);
    const intents = mutableSlice(
        protocol.Intent,
        intent_output_pointer,
        intent_output_capacity,
    ) orelse return result(.invalid_argument);
    const summary = summary_pointer orelse return result(.invalid_argument);
    const ticket = ticket_pointer orelse return result(.invalid_argument);
    const planned = partition.planClose(view, target.*, intents) catch |err| return mapError(err);
    summary.* = planned.summary;
    ticket.* = planned.ticket;
    return result(.ok);
}

pub export fn rm_local_resource_manager_commit_partition_close(
    state_pointer: ?*anyopaque,
    state_size: u64,
    operation_pointer: ?*const protocol.OperationToken,
    ticket_pointer: ?*const protocol.PartitionAdmissionTicket,
    partition_pointer: ?*const protocol.PartitionHandle,
) callconv(.c) i32 {
    const state_length = std.math.cast(usize, state_size) orelse return result(.invalid_argument);
    const ranges = [_]?MemoryRange{
        memoryRange(state_pointer, state_length, 1) catch return result(.invalid_argument),
        memoryRange(ticket_pointer, 1, @sizeOf(protocol.PartitionAdmissionTicket)) catch
            return result(.invalid_argument),
        memoryRange(partition_pointer, 1, @sizeOf(protocol.PartitionHandle)) catch
            return result(.invalid_argument),
    };
    validateDisjointMemory(&ranges) catch return result(.invalid_argument);
    const view = openOperationState(state_pointer, state_size, operation_pointer, .partition_admission) catch |err|
        return mapError(err);
    const ticket = ticket_pointer orelse return result(.invalid_argument);
    const target = partition_pointer orelse return result(.invalid_argument);
    partition.commitClose(view, ticket.*, target.*) catch |err| return mapError(err);
    return result(.ok);
}

pub export fn rm_local_resource_manager_plan_partition_resource_admission(
    state_pointer: ?*anyopaque,
    state_size: u64,
    operation_pointer: ?*const protocol.OperationToken,
    partition_pointer: ?*const protocol.PartitionHandle,
    spec_pointer: ?*const protocol.ResourceSpec,
    output_pointer: ?*protocol.PartitionResourceAdmission,
) callconv(.c) i32 {
    const state_length = std.math.cast(usize, state_size) orelse return result(.invalid_argument);
    const ranges = [_]?MemoryRange{
        memoryRange(state_pointer, state_length, 1) catch return result(.invalid_argument),
        memoryRange(partition_pointer, 1, @sizeOf(protocol.PartitionHandle)) catch
            return result(.invalid_argument),
        memoryRange(spec_pointer, 1, @sizeOf(protocol.ResourceSpec)) catch
            return result(.invalid_argument),
        memoryRange(output_pointer, 1, @sizeOf(protocol.PartitionResourceAdmission)) catch
            return result(.invalid_argument),
    };
    validateDisjointMemory(&ranges) catch return result(.invalid_argument);
    const view = openOperationState(state_pointer, state_size, operation_pointer, .partition_resource_admission) catch |err|
        return mapError(err);
    const partition_handle = partition_pointer orelse return result(.invalid_argument);
    const spec = spec_pointer orelse return result(.invalid_argument);
    const output = output_pointer orelse return result(.invalid_argument);
    output.* = partition.planResourceAdmission(view, partition_handle.*, spec.*) catch |err|
        return mapError(err);
    return result(.ok);
}

pub export fn rm_local_resource_manager_commit_partition_resource_admission(
    state_pointer: ?*anyopaque,
    state_size: u64,
    operation_pointer: ?*const protocol.OperationToken,
    admission_pointer: ?*const protocol.PartitionResourceAdmission,
    handle_pointer: ?*protocol.ResourceHandle,
) callconv(.c) i32 {
    const state_length = std.math.cast(usize, state_size) orelse return result(.invalid_argument);
    const ranges = [_]?MemoryRange{
        memoryRange(state_pointer, state_length, 1) catch return result(.invalid_argument),
        memoryRange(admission_pointer, 1, @sizeOf(protocol.PartitionResourceAdmission)) catch
            return result(.invalid_argument),
        memoryRange(handle_pointer, 1, @sizeOf(protocol.ResourceHandle)) catch
            return result(.invalid_argument),
    };
    validateDisjointMemory(&ranges) catch return result(.invalid_argument);
    const view = openOperationState(state_pointer, state_size, operation_pointer, .partition_resource_admission) catch |err|
        return mapError(err);
    const admission = admission_pointer orelse return result(.invalid_argument);
    const output = handle_pointer orelse return result(.invalid_argument);
    output.* = partition.commitResourceAdmission(view, admission.*) catch |err|
        return mapError(err);
    return result(.ok);
}

pub export fn rm_local_resource_manager_read_partition(
    state_pointer: ?*anyopaque,
    state_size: u64,
    partition_pointer: ?*const protocol.PartitionHandle,
    output_pointer: ?*protocol.PartitionView,
) callconv(.c) i32 {
    const state_length = std.math.cast(usize, state_size) orelse return result(.invalid_argument);
    const ranges = [_]?MemoryRange{
        memoryRange(state_pointer, state_length, 1) catch return result(.invalid_argument),
        memoryRange(partition_pointer, 1, @sizeOf(protocol.PartitionHandle)) catch
            return result(.invalid_argument),
        memoryRange(output_pointer, 1, @sizeOf(protocol.PartitionView)) catch
            return result(.invalid_argument),
    };
    validateDisjointMemory(&ranges) catch return result(.invalid_argument);
    const view = openState(state_pointer, state_size) catch |err| return mapError(err);
    const partition_handle = partition_pointer orelse return result(.invalid_argument);
    const output = output_pointer orelse return result(.invalid_argument);
    output.* = partition.readPartition(view, partition_handle.*) catch |err| return mapError(err);
    return result(.ok);
}

pub export fn rm_local_resource_manager_read_partition_cell(
    state_pointer: ?*anyopaque,
    state_size: u64,
    partition_pointer: ?*const protocol.PartitionHandle,
    local_ordinal: u32,
    output_pointer: ?*protocol.PartitionCellView,
) callconv(.c) i32 {
    const state_length = std.math.cast(usize, state_size) orelse return result(.invalid_argument);
    const ranges = [_]?MemoryRange{
        memoryRange(state_pointer, state_length, 1) catch return result(.invalid_argument),
        memoryRange(partition_pointer, 1, @sizeOf(protocol.PartitionHandle)) catch
            return result(.invalid_argument),
        memoryRange(output_pointer, 1, @sizeOf(protocol.PartitionCellView)) catch
            return result(.invalid_argument),
    };
    validateDisjointMemory(&ranges) catch return result(.invalid_argument);
    const view = openState(state_pointer, state_size) catch |err| return mapError(err);
    const partition_handle = partition_pointer orelse return result(.invalid_argument);
    const output = output_pointer orelse return result(.invalid_argument);
    output.* = partition.readCell(view, partition_handle.*, local_ordinal) catch |err|
        return mapError(err);
    return result(.ok);
}

pub export fn rm_local_resource_manager_begin_intent(
    state_pointer: ?*anyopaque,
    state_size: u64,
    operation_pointer: ?*const protocol.OperationToken,
    intent_pointer: ?*const protocol.Intent,
    token_pointer: ?*protocol.ExecutionToken,
) callconv(.c) i32 {
    const view = openOperationState(state_pointer, state_size, operation_pointer, null) catch |err|
        return mapError(err);
    const intent = intent_pointer orelse return result(.invalid_argument);
    if (intent.reason_flags == protocol.intent_reason_partition) {
        if (view.header.active_operation_kind != .partition_admission and
            view.header.active_operation_kind != .partition_resource_admission)
        {
            return result(.invalid_operation_phase);
        }
    } else if (view.header.active_operation_kind != .cleanup) {
        return result(.invalid_operation_phase);
    }
    const output = token_pointer orelse return result(.invalid_argument);
    output.* = executor.beginIntent(view, intent.*) catch |err| return mapError(err);
    return result(.ok);
}

pub export fn rm_local_resource_manager_commit_effect(
    state_pointer: ?*anyopaque,
    state_size: u64,
    operation_pointer: ?*const protocol.OperationToken,
    token_pointer: ?*const protocol.ExecutionToken,
    effect_pointer: ?*const protocol.TypedEffect,
    receipt_pointer: ?*protocol.CommitReceipt,
) callconv(.c) i32 {
    const view = openOperationState(state_pointer, state_size, operation_pointer, null) catch |err|
        return mapError(err);
    const token = token_pointer orelse return result(.invalid_argument);
    const effect = effect_pointer orelse return result(.invalid_argument);
    const output = receipt_pointer orelse return result(.invalid_argument);
    output.* = executor.commitEffect(view, token.*, effect.*) catch |err| return mapError(err);
    return result(.ok);
}

pub export fn rm_local_resource_manager_mark_effect_started(
    state_pointer: ?*anyopaque,
    state_size: u64,
    operation_pointer: ?*const protocol.OperationToken,
    token_pointer: ?*const protocol.ExecutionToken,
    output_pointer: ?*protocol.CommitReceipt,
) callconv(.c) i32 {
    const view = openOperationState(state_pointer, state_size, operation_pointer, null) catch |err|
        return mapError(err);
    const token = token_pointer orelse return result(.invalid_argument);
    const output = output_pointer orelse return result(.invalid_argument);
    output.* = executor.markEffectStarted(view, token.*) catch |err| return mapError(err);
    return result(.ok);
}

pub export fn rm_local_resource_manager_abort_intent(
    state_pointer: ?*anyopaque,
    state_size: u64,
    operation_pointer: ?*const protocol.OperationToken,
    token_pointer: ?*const protocol.ExecutionToken,
    receipt_pointer: ?*protocol.CommitReceipt,
) callconv(.c) i32 {
    const view = openOperationState(state_pointer, state_size, operation_pointer, null) catch |err|
        return mapError(err);
    const token = token_pointer orelse return result(.invalid_argument);
    const output = receipt_pointer orelse return result(.invalid_argument);
    output.* = executor.abortIntent(view, token.*) catch |err| return mapError(err);
    return result(.ok);
}

pub export fn rm_local_resource_manager_mark_effect_uncertain(
    state_pointer: ?*anyopaque,
    state_size: u64,
    operation_pointer: ?*const protocol.OperationToken,
    token_pointer: ?*const protocol.ExecutionToken,
    receipt_pointer: ?*protocol.CommitReceipt,
) callconv(.c) i32 {
    const view = openOperationState(state_pointer, state_size, operation_pointer, null) catch |err|
        return mapError(err);
    const token = token_pointer orelse return result(.invalid_argument);
    const output = receipt_pointer orelse return result(.invalid_argument);
    output.* = executor.markEffectUncertain(view, token.*) catch |err| return mapError(err);
    return result(.ok);
}

pub export fn rm_local_resource_manager_read_table_capacity(
    state_pointer: ?*anyopaque,
    state_size: u64,
    handle_pointer: ?*const protocol.TableHandle,
    output_pointer: ?*protocol.TableCapacityView,
) callconv(.c) i32 {
    const view = openState(state_pointer, state_size) catch |err| return mapError(err);
    const handle = handle_pointer orelse return result(.invalid_argument);
    const output = output_pointer orelse return result(.invalid_argument);
    output.* = state.readTableCapacity(view, handle.*) catch |err| return mapError(err);
    return result(.ok);
}

pub export fn rm_local_resource_manager_read_resource(
    state_pointer: ?*anyopaque,
    state_size: u64,
    handle_pointer: ?*const protocol.ResourceHandle,
    output_pointer: ?*protocol.ResourceView,
) callconv(.c) i32 {
    const view = openState(state_pointer, state_size) catch |err| return mapError(err);
    const handle = handle_pointer orelse return result(.invalid_argument);
    const output = output_pointer orelse return result(.invalid_argument);
    output.* = state.readResource(view, handle.*) catch |err| return mapError(err);
    return result(.ok);
}

fn openState(pointer: ?*anyopaque, count: u64) !layout.View {
    const buffer = mutableBytes(pointer, count) orelse return error.InvalidArgument;
    return state.open(buffer);
}

fn openOperationState(
    state_pointer: ?*anyopaque,
    state_size: u64,
    operation_pointer: ?*const protocol.OperationToken,
    expected_kind: ?protocol.OperationKind,
) !layout.View {
    const state_length = std.math.cast(usize, state_size) orelse return error.InvalidArgument;
    const state_range = try memoryRange(state_pointer, state_length, 1);
    const operation_range = try memoryRange(
        operation_pointer,
        1,
        @sizeOf(protocol.OperationToken),
    );
    if (rangesOverlap(state_range, operation_range)) return error.InvalidArgument;
    const view = try openState(state_pointer, state_size);
    const operation = operation_pointer orelse return error.InvalidArgument;
    try operation_sequence.requireCurrent(view, operation.*);
    if (expected_kind) |kind| {
        if (operation.kind != kind) return error.InvalidOperationPhase;
    }
    return view;
}

test "local resource manager ABI layout fingerprint is stable and nonzero" {
    try std.testing.expect(rm_local_resource_manager_layout_fingerprint() != 0);
}
