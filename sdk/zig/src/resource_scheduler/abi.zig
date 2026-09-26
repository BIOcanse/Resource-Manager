const std = @import("std");
const scheduler = @import("root.zig");

const types = scheduler.types;

const ResultCode = enum(i32) {
    ok = 0,
    invalid_argument = 1,
    invalid_config = 2,
    invalid_request = 3,
    invalid_target = 4,
    invalid_capacity = 5,
    invalid_pending = 6,
    buffer_too_small = 7,
    numeric_overflow = 8,
    invalid_state = 9,
    state_full = 10,
    stale_reservation = 11,
    invalid_feedback = 12,
};

pub export fn rm_resource_scheduler_abi_version() callconv(.c) u32 {
    return types.protocol_version;
}

pub export fn rm_resource_scheduler_config_size() callconv(.c) u32 {
    return @sizeOf(scheduler.config.Config);
}

pub export fn rm_resource_scheduler_plan_request_size() callconv(.c) u32 {
    return @sizeOf(types.PlanRequest);
}

pub export fn rm_resource_scheduler_capacity_size() callconv(.c) u32 {
    return @sizeOf(types.CapacityInput);
}

pub export fn rm_resource_scheduler_target_size() callconv(.c) u32 {
    return @sizeOf(types.TargetInput);
}

pub export fn rm_resource_scheduler_private_resource_size() callconv(.c) u32 {
    return @sizeOf(types.PrivateResourceInput);
}

pub export fn rm_resource_scheduler_pending_action_size() callconv(.c) u32 {
    return @sizeOf(types.PendingActionInput);
}

pub export fn rm_resource_scheduler_candidate_size() callconv(.c) u32 {
    return @sizeOf(types.CandidateOutput);
}

pub export fn rm_resource_scheduler_selection_size() callconv(.c) u32 {
    return @sizeOf(types.SelectionOutput);
}

pub export fn rm_resource_scheduler_resource_scratch_size() callconv(.c) u32 {
    return @sizeOf(types.ResourceScratch);
}

pub export fn rm_resource_scheduler_execution_authority_size() callconv(.c) u32 {
    return @sizeOf(types.ResourceExecutionAuthority);
}

pub export fn rm_resource_scheduler_fact_batch_input_size() callconv(.c) u32 {
    return @sizeOf(scheduler.fact_batch.FactBatchInput);
}

pub export fn rm_resource_scheduler_reservation_request_size() callconv(.c) u32 {
    return @sizeOf(types.ReservationRequest);
}

pub export fn rm_resource_scheduler_reservation_token_size() callconv(.c) u32 {
    return @sizeOf(types.ReservationToken);
}

pub export fn rm_resource_scheduler_revalidation_input_size() callconv(.c) u32 {
    return @sizeOf(types.RevalidationInput);
}

pub export fn rm_resource_scheduler_revalidation_output_size() callconv(.c) u32 {
    return @sizeOf(types.RevalidationOutput);
}

pub export fn rm_resource_scheduler_feedback_request_size() callconv(.c) u32 {
    return @sizeOf(types.FeedbackRequest);
}

pub export fn rm_resource_scheduler_action_feedback_size() callconv(.c) u32 {
    return @sizeOf(types.ActionFeedbackInput);
}

pub export fn rm_resource_scheduler_feedback_output_size() callconv(.c) u32 {
    return @sizeOf(types.FeedbackOutput);
}

pub export fn rm_resource_scheduler_layout_fingerprint() callconv(.c) u64 {
    const values = [_]usize{
        @sizeOf(types.ResourceExecutionAuthority),
        @offsetOf(types.ResourceExecutionAuthority, "source"),
        @offsetOf(types.ResourceExecutionAuthority, "action"),
        @offsetOf(types.ResourceExecutionAuthority, "action_route"),
        @offsetOf(types.ResourceExecutionAuthority, "flags"),
        @offsetOf(types.ResourceExecutionAuthority, "resource_slot"),
        @offsetOf(types.ResourceExecutionAuthority, "resource_id"),
        @offsetOf(types.ResourceExecutionAuthority, "reserved0"),
        @offsetOf(types.ResourceExecutionAuthority, "ledger_instance_id"),
        @offsetOf(types.ResourceExecutionAuthority, "source_snapshot_generation"),
        @offsetOf(types.ResourceExecutionAuthority, "resource_generation"),
        @offsetOf(types.ResourceExecutionAuthority, "resource_key"),
        @offsetOf(types.ResourceExecutionAuthority, "owner_application_key"),
        @offsetOf(types.ResourceExecutionAuthority, "owner_instance_id_low"),
        @offsetOf(types.ResourceExecutionAuthority, "owner_instance_id_high"),
        @offsetOf(types.ResourceExecutionAuthority, "owner_context_generation"),
        @offsetOf(types.ResourceExecutionAuthority, "lease_generation"),
        @offsetOf(types.ResourceExecutionAuthority, "binding_generation"),
        @offsetOf(types.ResourceExecutionAuthority, "capability_generation"),
        @offsetOf(types.ResourceExecutionAuthority, "scheduling_revision"),
        @offsetOf(types.ResourceExecutionAuthority, "executor_id_low"),
        @offsetOf(types.ResourceExecutionAuthority, "executor_id_high"),
        @offsetOf(types.ResourceExecutionAuthority, "action_attempt_id_low"),
        @offsetOf(types.ResourceExecutionAuthority, "action_attempt_id_high"),
        @offsetOf(types.ResourceExecutionAuthority, "projection_epoch"),
        @sizeOf(types.PrivateResourceInput),
        @offsetOf(types.PrivateResourceInput, "discard_authority"),
        @offsetOf(types.PrivateResourceInput, "trim_authority"),
        @offsetOf(types.PrivateResourceInput, "move_down_authority"),
        @sizeOf(types.PendingActionInput),
        @offsetOf(types.PendingActionInput, "abi_version"),
        @offsetOf(types.PendingActionInput, "struct_size"),
        @offsetOf(types.PendingActionInput, "authority"),
        @offsetOf(types.PendingActionInput, "journal_transaction_id_low"),
        @offsetOf(types.PendingActionInput, "journal_transaction_id_high"),
        @offsetOf(types.PendingActionInput, "target_key"),
        @offsetOf(types.PendingActionInput, "size_bytes"),
        @offsetOf(types.PendingActionInput, "deadline_timestamp"),
        @offsetOf(types.PendingActionInput, "pending_generation"),
        @offsetOf(types.PendingActionInput, "state"),
        @offsetOf(types.PendingActionInput, "tier"),
        @offsetOf(types.PendingActionInput, "flags"),
        @offsetOf(types.PendingActionInput, "reserved0"),
        @sizeOf(scheduler.fact_batch.FactBatchInput),
        @offsetOf(scheduler.fact_batch.FactBatchInput, "abi_version"),
        @offsetOf(scheduler.fact_batch.FactBatchInput, "struct_size"),
        @offsetOf(scheduler.fact_batch.FactBatchInput, "projection_epoch"),
        @offsetOf(scheduler.fact_batch.FactBatchInput, "configuration_generation"),
        @offsetOf(scheduler.fact_batch.FactBatchInput, "target_count"),
        @offsetOf(scheduler.fact_batch.FactBatchInput, "private_resource_count"),
        @offsetOf(scheduler.fact_batch.FactBatchInput, "journal_pending_count"),
        @offsetOf(scheduler.fact_batch.FactBatchInput, "action_budget"),
        @offsetOf(scheduler.fact_batch.FactBatchInput, "flags"),
        @offsetOf(scheduler.fact_batch.FactBatchInput, "reserved0"),
        @offsetOf(scheduler.fact_batch.FactBatchInput, "reserved1"),
        @sizeOf(types.CandidateOutput),
        @offsetOf(types.CandidateOutput, "authority"),
        @offsetOf(types.CandidateOutput, "size_bytes"),
        @sizeOf(types.SelectionOutput),
        @offsetOf(types.SelectionOutput, "request_id"),
        @offsetOf(types.SelectionOutput, "authority"),
        @offsetOf(types.SelectionOutput, "configuration_generation"),
        @sizeOf(types.RevalidationInput),
        @offsetOf(types.RevalidationInput, "authority"),
        @offsetOf(types.RevalidationInput, "target_key"),
        @offsetOf(types.RevalidationInput, "reserved0"),
        @sizeOf(types.ActionFeedbackInput),
        @offsetOf(types.ActionFeedbackInput, "authority"),
        @offsetOf(types.ActionFeedbackInput, "request_id"),
        @offsetOf(types.ActionFeedbackInput, "released_bytes"),
        @offsetOf(types.ActionFeedbackInput, "resident_bytes"),
        @offsetOf(types.ActionFeedbackInput, "action_gate_epoch"),
        @offsetOf(types.ActionFeedbackInput, "detail_code"),
        @offsetOf(types.ActionFeedbackInput, "status"),
        @offsetOf(types.ActionFeedbackInput, "previous_tier"),
        @offsetOf(types.ActionFeedbackInput, "current_tier"),
        @offsetOf(types.ActionFeedbackInput, "reserved0"),
        @sizeOf(types.FeedbackOutput),
        @offsetOf(types.FeedbackOutput, "projected_capacity"),
        @offsetOf(types.FeedbackOutput, "applied"),
        @offsetOf(types.FeedbackOutput, "result_code"),
        @offsetOf(types.FeedbackOutput, "danger_flags"),
        @offsetOf(types.FeedbackOutput, "current_tier"),
        @offsetOf(types.FeedbackOutput, "error_code"),
        @offsetOf(types.FeedbackOutput, "reservation_disposition"),
        @offsetOf(types.FeedbackOutput, "reserved0"),
        @offsetOf(types.FeedbackOutput, "reserved1"),
        @sizeOf(types.ResourceScratch),
    };
    var hash: u64 = 14695981039346656037;
    for (values) |value| {
        hash = (hash ^ @as(u64, @intCast(value))) *% 1099511628211;
    }
    return hash;
}

pub export fn rm_resource_scheduler_state_alignment() callconv(.c) u32 {
    return @alignOf(scheduler.state.Header);
}

pub export fn rm_resource_scheduler_state_required_size(
    capacity: u32,
    output_size: ?*u64,
) callconv(.c) i32 {
    const output = output_size orelse return result(.invalid_argument);
    output.* = scheduler.state.requiredBytes(capacity) catch |err| return mapError(err);
    return result(.ok);
}

pub export fn rm_resource_scheduler_state_initialize(
    state_buffer: ?*anyopaque,
    state_size: u64,
    capacity: u32,
    generation: u64,
) callconv(.c) i32 {
    const bytes = mutableBytes(state_buffer, state_size) orelse return result(.invalid_argument);
    scheduler.state.initialize(bytes, capacity, generation) catch |err| return mapError(err);
    return result(.ok);
}

pub export fn rm_resource_scheduler_reserve_selection(
    state_buffer: ?*anyopaque,
    state_size: u64,
    request: ?*const types.ReservationRequest,
    selection: ?*const types.SelectionOutput,
    token: ?*types.ReservationToken,
) callconv(.c) i32 {
    const view = scheduler.state.open(mutableBytes(state_buffer, state_size) orelse
        return result(.invalid_argument)) catch |err| return mapError(err);
    scheduler.state.reserve(
        view,
        request orelse return result(.invalid_argument),
        selection orelse return result(.invalid_argument),
        token orelse return result(.invalid_argument),
    ) catch |err| return mapError(err);
    return result(.ok);
}

pub export fn rm_resource_scheduler_revalidate_and_begin_reservation(
    state_buffer: ?*anyopaque,
    state_size: u64,
    config: ?*const scheduler.config.Config,
    token: ?*const types.ReservationToken,
    selection: ?*const types.SelectionOutput,
    input: ?*const types.RevalidationInput,
    output: ?*types.RevalidationOutput,
) callconv(.c) i32 {
    const view = scheduler.state.open(mutableBytes(state_buffer, state_size) orelse
        return result(.invalid_argument)) catch |err| return mapError(err);
    scheduler.revalidation.revalidateAndBegin(
        config orelse return result(.invalid_argument),
        view,
        token orelse return result(.invalid_argument),
        selection orelse return result(.invalid_argument),
        input orelse return result(.invalid_argument),
        output orelse return result(.invalid_argument),
    ) catch |err| return mapError(err);
    return result(.ok);
}

pub export fn rm_resource_scheduler_complete_reservation(
    state_buffer: ?*anyopaque,
    state_size: u64,
    token: ?*const types.ReservationToken,
) callconv(.c) i32 {
    const view = scheduler.state.open(mutableBytes(state_buffer, state_size) orelse
        return result(.invalid_argument)) catch |err| return mapError(err);
    scheduler.state.complete(view, token orelse return result(.invalid_argument)) catch |err| return mapError(err);
    return result(.ok);
}

pub export fn rm_resource_scheduler_cancel_reservation(
    state_buffer: ?*anyopaque,
    state_size: u64,
    token: ?*const types.ReservationToken,
) callconv(.c) i32 {
    const view = scheduler.state.open(mutableBytes(state_buffer, state_size) orelse
        return result(.invalid_argument)) catch |err| return mapError(err);
    scheduler.state.cancel(view, token orelse return result(.invalid_argument)) catch |err| return mapError(err);
    return result(.ok);
}

pub export fn rm_resource_scheduler_bind_reservation_journal(
    state_buffer: ?*anyopaque,
    state_size: u64,
    token: ?*const types.ReservationToken,
    transaction_id_low: u64,
    transaction_id_high: u64,
) callconv(.c) i32 {
    const view = scheduler.state.open(mutableBytes(state_buffer, state_size) orelse
        return result(.invalid_argument)) catch |err| return mapError(err);
    scheduler.state.bindJournal(
        view,
        token orelse return result(.invalid_argument),
        transaction_id_low,
        transaction_id_high,
    ) catch |err| return mapError(err);
    return result(.ok);
}

pub export fn rm_resource_scheduler_abandon_reservation(
    state_buffer: ?*anyopaque,
    state_size: u64,
    token: ?*const types.ReservationToken,
) callconv(.c) i32 {
    const view = scheduler.state.open(mutableBytes(state_buffer, state_size) orelse
        return result(.invalid_argument)) catch |err| return mapError(err);
    scheduler.state.abandon(view, token orelse return result(.invalid_argument)) catch |err| return mapError(err);
    return result(.ok);
}

pub export fn rm_resource_scheduler_apply_feedback(
    state_buffer: ?*anyopaque,
    state_size: u64,
    token: ?*const types.ReservationToken,
    request: ?*const types.FeedbackRequest,
    selection: ?*const types.SelectionOutput,
    capacity: ?*const types.CapacityInput,
    action: ?*const types.ActionFeedbackInput,
    output: ?*types.FeedbackOutput,
) callconv(.c) i32 {
    const view = scheduler.state.open(mutableBytes(state_buffer, state_size) orelse
        return result(.invalid_argument)) catch |err| return mapError(err);
    const actual_token = token orelse return result(.invalid_argument);
    const actual_selection = selection orelse return result(.invalid_argument);
    const slot = scheduler.state.validateTerminalSelection(view, actual_token, actual_selection) catch |err| return mapError(err);
    const disposition = scheduler.feedback.apply(
        slot.danger_min_physical_after_vram_move_ratio,
        slot.danger_min_virtual_after_physical_move_ratio,
        slot.action_gate_epoch,
        request orelse return result(.invalid_argument),
        actual_selection,
        capacity orelse return result(.invalid_argument),
        action orelse return result(.invalid_argument),
        output orelse return result(.invalid_argument),
    ) catch |err| return mapError(err);
    if (disposition == .complete) {
        scheduler.state.complete(view, actual_token) catch |err| return mapError(err);
    }
    return result(.ok);
}

pub export fn rm_resource_scheduler_validate_config(
    config: ?*const scheduler.config.Config,
) callconv(.c) i32 {
    const actual = config orelse return result(.invalid_argument);
    return result(if (scheduler.config.validate(actual)) .ok else .invalid_config);
}

pub export fn rm_resource_scheduler_required_candidate_capacity(
    private_resource_count: u32,
    output_capacity: ?*u32,
) callconv(.c) i32 {
    const output = output_capacity orelse return result(.invalid_argument);
    const capacity = scheduler.planner.requiredCandidateCapacity(private_resource_count) catch |err| {
        return mapError(err);
    };
    output.* = std.math.cast(u32, capacity) orelse return result(.numeric_overflow);
    return result(.ok);
}

pub export fn rm_resource_scheduler_plan(
    config: ?*const scheduler.config.Config,
    request: ?*const types.PlanRequest,
    targets: ?[*]const types.TargetInput,
    target_count: u32,
    resources: ?[*]const types.PrivateResourceInput,
    resource_count: u32,
    state_buffer: ?*anyopaque,
    state_size: u64,
    pending_input: ?[*]types.PendingActionInput,
    pending_input_capacity: u32,
    journal_pending: ?[*]const types.PendingActionInput,
    journal_pending_count: u32,
    fact_batch: ?*const scheduler.fact_batch.FactBatchInput,
    candidates: ?[*]types.CandidateOutput,
    candidate_capacity: u32,
    selections: ?[*]types.SelectionOutput,
    selection_capacity: u32,
    target_plans: ?[*]types.TargetPlanOutput,
    target_plan_capacity: u32,
    resource_scratch: ?[*]types.ResourceScratch,
    resource_scratch_capacity: u32,
    target_scratch: ?[*]types.TargetScratch,
    target_scratch_capacity: u32,
    resource_order: ?[*]u32,
    resource_order_capacity: u32,
    pending_scratch: ?[*]types.PendingActionInput,
    pending_scratch_capacity: u32,
    global: ?*types.GlobalSelectionOutput,
    summary: ?*types.PlanSummary,
) callconv(.c) i32 {
    const actual_config = config orelse return result(.invalid_argument);
    const actual_request = request orelse return result(.invalid_argument);
    const actual_fact_batch = fact_batch orelse return result(.invalid_argument);
    const actual_global = global orelse return result(.invalid_argument);
    const actual_summary = summary orelse return result(.invalid_argument);

    const state_view = scheduler.state.open(mutableBytes(state_buffer, state_size) orelse
        return result(.invalid_argument)) catch |err| return mapError(err);
    const pending_input_slice = mutableSlice(types.PendingActionInput, pending_input, pending_input_capacity) orelse
        return result(.invalid_argument);
    const pending_count = scheduler.state.snapshot(
        state_view,
        actual_request.now_monotonic_timestamp,
        pending_input_slice,
    ) catch |err| return mapError(err);

    var buffers = types.PlannerBuffers{
        .candidates = mutableSlice(types.CandidateOutput, candidates, candidate_capacity) orelse
            return result(.invalid_argument),
        .selections = mutableSlice(types.SelectionOutput, selections, selection_capacity) orelse
            return result(.invalid_argument),
        .target_plans = mutableSlice(types.TargetPlanOutput, target_plans, target_plan_capacity) orelse
            return result(.invalid_argument),
        .resource_scratch = mutableSlice(types.ResourceScratch, resource_scratch, resource_scratch_capacity) orelse
            return result(.invalid_argument),
        .target_scratch = mutableSlice(types.TargetScratch, target_scratch, target_scratch_capacity) orelse
            return result(.invalid_argument),
        .resource_order = mutableSlice(u32, resource_order, resource_order_capacity) orelse
            return result(.invalid_argument),
        .pending_scratch = mutableSlice(types.PendingActionInput, pending_scratch, pending_scratch_capacity) orelse
            return result(.invalid_argument),
    };

    const planned = scheduler.planner.plan(
        actual_config,
        actual_request,
        constSlice(types.TargetInput, targets, target_count) orelse return result(.invalid_argument),
        constSlice(types.PrivateResourceInput, resources, resource_count) orelse return result(.invalid_argument),
        pending_input_slice[0..pending_count],
        constSlice(types.PendingActionInput, journal_pending, journal_pending_count) orelse
            return result(.invalid_argument),
        actual_fact_batch,
        &buffers,
        actual_global,
    ) catch |err| return mapError(err);
    actual_summary.* = planned;
    return result(.ok);
}

fn constSlice(comptime T: type, pointer: ?[*]const T, count: u32) ?[]const T {
    if (count == 0) return &[_]T{};
    return (pointer orelse return null)[0..count];
}

fn mutableSlice(comptime T: type, pointer: ?[*]T, count: u32) ?[]T {
    if (count == 0) return @constCast((&[_]T{})[0..]);
    return (pointer orelse return null)[0..count];
}

fn mutableBytes(pointer: ?*anyopaque, count: u64) ?[]u8 {
    const length = std.math.cast(usize, count) orelse return null;
    if (length == 0) return null;
    const bytes: [*]u8 = @ptrCast(pointer orelse return null);
    return bytes[0..length];
}

fn result(code: ResultCode) i32 {
    return @intFromEnum(code);
}

fn mapError(err: anyerror) i32 {
    return result(switch (err) {
        error.InvalidConfig => .invalid_config,
        error.InvalidRequest => .invalid_request,
        error.InvalidTarget => .invalid_target,
        error.InvalidCapacity => .invalid_capacity,
        error.InvalidPending => .invalid_pending,
        error.BufferTooSmall => .buffer_too_small,
        error.NumericOverflow => .numeric_overflow,
        error.InvalidState => .invalid_state,
        error.StateFull => .state_full,
        error.StaleReservation => .stale_reservation,
        error.InvalidFeedback => .invalid_feedback,
        else => .invalid_argument,
    });
}

test "resource scheduler ABI reports stable version and config size" {
    try std.testing.expectEqual(types.protocol_version, rm_resource_scheduler_abi_version());
    try std.testing.expectEqual(@as(u32, @sizeOf(scheduler.config.Config)), rm_resource_scheduler_config_size());
    try std.testing.expectEqual(@as(u32, @sizeOf(types.PlanRequest)), rm_resource_scheduler_plan_request_size());
    try std.testing.expectEqual(@as(u32, @sizeOf(types.CapacityInput)), rm_resource_scheduler_capacity_size());
    try std.testing.expectEqual(@as(u32, @sizeOf(types.TargetInput)), rm_resource_scheduler_target_size());
    try std.testing.expectEqual(@as(u32, @sizeOf(types.PrivateResourceInput)), rm_resource_scheduler_private_resource_size());
    try std.testing.expectEqual(@as(u32, @sizeOf(types.PendingActionInput)), rm_resource_scheduler_pending_action_size());
    try std.testing.expectEqual(@as(u32, @sizeOf(types.CandidateOutput)), rm_resource_scheduler_candidate_size());
    try std.testing.expectEqual(@as(u32, @sizeOf(types.SelectionOutput)), rm_resource_scheduler_selection_size());
    try std.testing.expectEqual(@as(u32, @sizeOf(types.ResourceScratch)), rm_resource_scheduler_resource_scratch_size());
    try std.testing.expectEqual(@as(u32, @sizeOf(types.ResourceExecutionAuthority)), rm_resource_scheduler_execution_authority_size());
    try std.testing.expectEqual(
        @as(u32, @sizeOf(scheduler.fact_batch.FactBatchInput)),
        rm_resource_scheduler_fact_batch_input_size(),
    );
    try std.testing.expectEqual(@as(u32, @sizeOf(types.ReservationRequest)), rm_resource_scheduler_reservation_request_size());
    try std.testing.expectEqual(@as(u32, @sizeOf(types.ReservationToken)), rm_resource_scheduler_reservation_token_size());
    try std.testing.expectEqual(@as(u32, @sizeOf(types.RevalidationInput)), rm_resource_scheduler_revalidation_input_size());
    try std.testing.expectEqual(@as(u32, @sizeOf(types.RevalidationOutput)), rm_resource_scheduler_revalidation_output_size());
    try std.testing.expectEqual(@as(u32, @sizeOf(types.FeedbackRequest)), rm_resource_scheduler_feedback_request_size());
    try std.testing.expectEqual(@as(u32, @sizeOf(types.ActionFeedbackInput)), rm_resource_scheduler_action_feedback_size());
    try std.testing.expectEqual(@as(u32, @sizeOf(types.FeedbackOutput)), rm_resource_scheduler_feedback_output_size());
    try std.testing.expectEqual(
        @as(u64, 0x4605066A76753B1D),
        rm_resource_scheduler_layout_fingerprint(),
    );
    var capacity: u32 = 0;
    try std.testing.expectEqual(
        result(.ok),
        rm_resource_scheduler_required_candidate_capacity(4, &capacity),
    );
    try std.testing.expectEqual(@as(u32, 12), capacity);
}

test "feedback identity mismatch never consumes active reservation" {
    const slot_capacity: u32 = 1;
    var storage: [try scheduler.state.requiredBytes(slot_capacity)]u8 align(@alignOf(scheduler.state.Header)) =
        undefined;
    try scheduler.state.initialize(storage[0..], slot_capacity, 701);
    const view = try scheduler.state.open(storage[0..]);

    var authority = std.mem.zeroes(types.ResourceExecutionAuthority);
    authority.source = @intFromEnum(types.Source.adapted_private);
    authority.action = types.action_discard;
    authority.action_route = @intFromEnum(types.ActionRoute.adapter_handler);
    authority.flags = types.authority_flag_typed_executor_proof;
    authority.resource_slot = 7;
    authority.resource_id = 8;
    authority.ledger_instance_id = 9;
    authority.source_snapshot_generation = 10;
    authority.resource_generation = 11;
    authority.resource_key = 12;
    authority.owner_application_key = 13;
    authority.owner_instance_id_low = 14;
    authority.owner_instance_id_high = 15;
    authority.owner_context_generation = 16;
    authority.lease_generation = 17;
    authority.binding_generation = 18;
    authority.capability_generation = 19;
    authority.scheduling_revision = 20;
    authority.executor_id_low = 21;
    authority.executor_id_high = 22;
    authority.action_attempt_id_low = 23;
    authority.action_attempt_id_high = 24;
    authority.projection_epoch = 25;

    var selection = std.mem.zeroes(types.SelectionOutput);
    selection.target_key = 31;
    selection.request_id = 32;
    selection.authority = authority;
    selection.size_bytes = 4096;
    selection.estimated_release_bytes = 4096;
    selection.configuration_generation = 33;
    selection.tier = @intFromEnum(types.Tier.physical_memory);

    var reservation = std.mem.zeroes(types.ReservationRequest);
    reservation.abi_version = types.protocol_version;
    reservation.struct_size = @sizeOf(types.ReservationRequest);
    reservation.now_monotonic_timestamp = 100;
    reservation.deadline_timestamp = 200;
    reservation.pending_generation = 34;
    reservation.configuration_generation = selection.configuration_generation;
    reservation.danger_min_physical_after_vram_move_ratio = 0.1;
    reservation.danger_min_virtual_after_physical_move_ratio = 0.1;
    reservation.maximum_in_flight = 1;
    reservation.maximum_in_flight_per_target = 1;
    var token = std.mem.zeroes(types.ReservationToken);
    try scheduler.state.reserve(view, &reservation, &selection, &token);
    try scheduler.state.bindJournal(view, &token, 36, 37);
    try scheduler.state.begin(view, &token, 0);

    var capacity = std.mem.zeroes(types.CapacityInput);
    capacity.total_physical_bytes = 8192;
    capacity.free_physical_bytes = 4096;
    capacity.fallback_physical_free_ratio = 0.5;
    var request = std.mem.zeroes(types.FeedbackRequest);
    request.abi_version = types.protocol_version;
    request.struct_size = @sizeOf(types.FeedbackRequest);
    request.expected_request_id = selection.request_id;
    request.policy_accepted = 1;
    request.action_result_count = 1;
    var action = std.mem.zeroes(types.ActionFeedbackInput);
    action.authority = selection.authority;
    action.request_id = selection.request_id;
    action.action_gate_epoch = 0;
    action.status = 5;
    action.previous_tier = selection.tier;
    action.current_tier = selection.tier;
    var output = std.mem.zeroes(types.FeedbackOutput);

    request.expected_request_id += 1;
    action.request_id += 1;
    try std.testing.expectEqual(
        result(.invalid_feedback),
        rm_resource_scheduler_apply_feedback(
            storage[0..].ptr,
            storage.len,
            &token,
            &request,
            &selection,
            &capacity,
            &action,
            &output,
        ),
    );
    _ = try scheduler.state.validateActiveSelection(view, &token, &selection);

    request.expected_request_id = selection.request_id;
    action.request_id = selection.request_id;
    action.authority.action_attempt_id_low += 1;
    try std.testing.expectEqual(
        result(.invalid_feedback),
        rm_resource_scheduler_apply_feedback(
            storage[0..].ptr,
            storage.len,
            &token,
            &request,
            &selection,
            &capacity,
            &action,
            &output,
        ),
    );
    _ = try scheduler.state.validateActiveSelection(view, &token, &selection);

    action.authority = selection.authority;
    request.policy_accepted = 0;
    request.action_result_count = 0;
    action.action_gate_epoch += 1;
    try std.testing.expectEqual(
        result(.invalid_feedback),
        rm_resource_scheduler_apply_feedback(
            storage[0..].ptr,
            storage.len,
            &token,
            &request,
            &selection,
            &capacity,
            &action,
            &output,
        ),
    );
    _ = try scheduler.state.validateActiveSelection(view, &token, &selection);

    action.action_gate_epoch = 0;
    request.policy_accepted = 1;
    request.action_result_count = 1;
    try std.testing.expectEqual(
        result(.ok),
        rm_resource_scheduler_apply_feedback(
            storage[0..].ptr,
            storage.len,
            &token,
            &request,
            &selection,
            &capacity,
            &action,
            &output,
        ),
    );
    try std.testing.expectEqual(
        @intFromEnum(scheduler.feedback.ErrorCode.action_result_not_completed),
        output.error_code,
    );
    try std.testing.expectEqual(
        @intFromEnum(scheduler.feedback.Disposition.retain),
        output.reservation_disposition,
    );
    _ = try scheduler.state.validateActiveSelection(view, &token, &selection);
    try scheduler.state.abandon(view, &token);
    const reopened = try scheduler.state.open(storage[0..]);
    try std.testing.expectEqual(
        @intFromEnum(types.PendingState.effect_uncertain),
        reopened.slots[token.slot_index].pending.state,
    );
    try std.testing.expectEqual(@as(u64, 0), reopened.slots[token.slot_index].action_gate_epoch);
    try std.testing.expectEqual(@as(u64, 36), reopened.slots[token.slot_index].journal_transaction_id_low);
    try std.testing.expectEqual(@as(u64, 37), reopened.slots[token.slot_index].journal_transaction_id_high);
    action.status = 1;
    try std.testing.expectEqual(
        result(.ok),
        rm_resource_scheduler_apply_feedback(
            storage[0..].ptr,
            storage.len,
            &token,
            &request,
            &selection,
            &capacity,
            &action,
            &output,
        ),
    );
    try std.testing.expectEqual(
        @intFromEnum(scheduler.feedback.Disposition.complete),
        output.reservation_disposition,
    );
    try std.testing.expectEqual(@as(u32, 0), reopened.header.active_count);
}
