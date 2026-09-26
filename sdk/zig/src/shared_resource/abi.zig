const std = @import("std");
const types = @import("types.zig");
const wire = @import("layout.zig");
const state = @import("state.zig");
const resources = @import("resource_ops.zig");
const subscriptions = @import("subscription_ops.zig");
const tasks = @import("task_queue.zig");
const actions = @import("action_gate.zig");
const public_manager = @import("public_manager.zig");

pub export fn rm_shared_ledger_abi_version() callconv(.c) u32 {
    return types.abi_version;
}

pub export fn rm_shared_ledger_capabilities() callconv(.c) u64 {
    return types.capabilities;
}

pub export fn rm_shared_resource_layout_fingerprint() callconv(.c) u64 {
    const values = [_]usize{
        @sizeOf(types.LedgerConfig),
        @offsetOf(types.LedgerConfig, "abi_version"),
        @offsetOf(types.LedgerConfig, "struct_size"),
        @offsetOf(types.LedgerConfig, "ledger_instance_id"),
        @offsetOf(types.LedgerConfig, "owner_application_key"),
        @offsetOf(types.LedgerConfig, "owner_process_created_utc_ticks"),
        @offsetOf(types.LedgerConfig, "subscription_coefficient"),
        @offsetOf(types.LedgerConfig, "resource_capacity"),
        @offsetOf(types.LedgerConfig, "subscription_capacity"),
        @offsetOf(types.LedgerConfig, "task_capacity"),
        @offsetOf(types.LedgerConfig, "owner_process_id"),
        @offsetOf(types.LedgerConfig, "synchronization_kind"),
        @offsetOf(types.LedgerConfig, "reserved0"),
        @offsetOf(types.LedgerConfig, "lease_clock_domain"),
        @offsetOf(types.LedgerConfig, "lease_clock_frequency_hz"),
        @offsetOf(types.LedgerConfig, "maximum_subscription_ttl"),
        @offsetOf(types.LedgerConfig, "maximum_queue_ttl"),
        @offsetOf(types.LedgerConfig, "maximum_grant_ttl"),

        @sizeOf(types.SubscriptionRequest),
        @offsetOf(types.SubscriptionRequest, "abi_version"),
        @offsetOf(types.SubscriptionRequest, "struct_size"),
        @offsetOf(types.SubscriptionRequest, "resource"),
        @offsetOf(types.SubscriptionRequest, "subscriber_application_key"),
        @offsetOf(types.SubscriptionRequest, "subscriber_instance_id"),
        @offsetOf(types.SubscriptionRequest, "subscriber_session_id"),
        @offsetOf(types.SubscriptionRequest, "lease_duration"),
        @offsetOf(types.SubscriptionRequest, "subscriber_process_id"),
        @offsetOf(types.SubscriptionRequest, "intent_mask"),
        @offsetOf(types.SubscriptionRequest, "reserved0"),
        @offsetOf(types.SubscriptionRequest, "reserved1"),

        @sizeOf(types.UseRequest),
        @offsetOf(types.UseRequest, "abi_version"),
        @offsetOf(types.UseRequest, "struct_size"),
        @offsetOf(types.UseRequest, "resource"),
        @offsetOf(types.UseRequest, "request_key"),
        @offsetOf(types.UseRequest, "requester_application_key"),
        @offsetOf(types.UseRequest, "requester_instance_id"),
        @offsetOf(types.UseRequest, "requester_session_id"),
        @offsetOf(types.UseRequest, "score_plan_generation"),
        @offsetOf(types.UseRequest, "queue_lease_duration"),
        @offsetOf(types.UseRequest, "base_score"),
        @offsetOf(types.UseRequest, "requester_process_id"),
        @offsetOf(types.UseRequest, "reserved0"),

        @sizeOf(types.ResourceRef),
        @offsetOf(types.ResourceRef, "ledger_instance_id"),
        @offsetOf(types.ResourceRef, "resource_generation"),
        @offsetOf(types.ResourceRef, "public_resource_id"),
        @offsetOf(types.ResourceRef, "resource_slot"),
        @offsetOf(types.ResourceRef, "reserved"),

        @sizeOf(types.ResourcePublication),
        @offsetOf(types.ResourcePublication, "abi_version"),
        @offsetOf(types.ResourcePublication, "struct_size"),
        @offsetOf(types.ResourcePublication, "public_resource_id"),
        @offsetOf(types.ResourcePublication, "owner_application_key"),
        @offsetOf(types.ResourcePublication, "owner_instance_id"),
        @offsetOf(types.ResourcePublication, "owner_instance_id_high"),
        @offsetOf(types.ResourcePublication, "owner_context_generation"),
        @offsetOf(types.ResourcePublication, "lease_generation"),
        @offsetOf(types.ResourcePublication, "binding_generation"),
        @offsetOf(types.ResourcePublication, "capability_generation"),
        @offsetOf(types.ResourcePublication, "executor_id_low"),
        @offsetOf(types.ResourcePublication, "executor_id_high"),
        @offsetOf(types.ResourcePublication, "resource_key"),
        @offsetOf(types.ResourcePublication, "adapter_key"),
        @offsetOf(types.ResourcePublication, "size_bytes"),
        @offsetOf(types.ResourcePublication, "content_identity_hash"),
        @offsetOf(types.ResourcePublication, "payload_mapping_id"),
        @offsetOf(types.ResourcePublication, "payload_generation"),
        @offsetOf(types.ResourcePublication, "last_updated_utc_ticks"),
        @offsetOf(types.ResourcePublication, "owner_process_id"),
        @offsetOf(types.ResourcePublication, "resource_id"),
        @offsetOf(types.ResourcePublication, "max_parallel_grants"),
        @offsetOf(types.ResourcePublication, "tier"),
        @offsetOf(types.ResourcePublication, "resource_kind"),
        @offsetOf(types.ResourcePublication, "recovery_kind"),
        @offsetOf(types.ResourcePublication, "granularity"),
        @offsetOf(types.ResourcePublication, "inapplicable_actions"),
        @offsetOf(types.ResourcePublication, "action_route"),
        @offsetOf(types.ResourcePublication, "owner_demand_mask"),
        @offsetOf(types.ResourcePublication, "activity_score"),
        @offsetOf(types.ResourcePublication, "surface_state"),
        @offsetOf(types.ResourcePublication, "availability"),
        @offsetOf(types.ResourcePublication, "flags"),
        @offsetOf(types.ResourcePublication, "reserved0"),
        @offsetOf(types.ResourcePublication, "reserved1"),

        @sizeOf(types.ResourceSnapshot),
        @offsetOf(types.ResourceSnapshot, "abi_version"),
        @offsetOf(types.ResourceSnapshot, "struct_size"),
        @offsetOf(types.ResourceSnapshot, "id"),
        @offsetOf(types.ResourceSnapshot, "owner_application_key"),
        @offsetOf(types.ResourceSnapshot, "owner_instance_id"),
        @offsetOf(types.ResourceSnapshot, "owner_instance_id_high"),
        @offsetOf(types.ResourceSnapshot, "owner_context_generation"),
        @offsetOf(types.ResourceSnapshot, "lease_generation"),
        @offsetOf(types.ResourceSnapshot, "binding_generation"),
        @offsetOf(types.ResourceSnapshot, "capability_generation"),
        @offsetOf(types.ResourceSnapshot, "executor_id_low"),
        @offsetOf(types.ResourceSnapshot, "executor_id_high"),
        @offsetOf(types.ResourceSnapshot, "resource_key"),
        @offsetOf(types.ResourceSnapshot, "adapter_key"),
        @offsetOf(types.ResourceSnapshot, "size_bytes"),
        @offsetOf(types.ResourceSnapshot, "content_identity_hash"),
        @offsetOf(types.ResourceSnapshot, "payload_mapping_id"),
        @offsetOf(types.ResourceSnapshot, "payload_generation"),
        @offsetOf(types.ResourceSnapshot, "last_updated_utc_ticks"),
        @offsetOf(types.ResourceSnapshot, "subscription_multiplier"),
        @offsetOf(types.ResourceSnapshot, "owner_process_id"),
        @offsetOf(types.ResourceSnapshot, "resource_id"),
        @offsetOf(types.ResourceSnapshot, "active_subscriber_count"),
        @offsetOf(types.ResourceSnapshot, "queued_request_count"),
        @offsetOf(types.ResourceSnapshot, "reserved_grant_count"),
        @offsetOf(types.ResourceSnapshot, "active_use_count"),
        @offsetOf(types.ResourceSnapshot, "scheduling_revision"),
        @offsetOf(types.ResourceSnapshot, "gate_epoch"),
        @offsetOf(types.ResourceSnapshot, "max_parallel_grants"),
        @offsetOf(types.ResourceSnapshot, "tier"),
        @offsetOf(types.ResourceSnapshot, "resource_kind"),
        @offsetOf(types.ResourceSnapshot, "recovery_kind"),
        @offsetOf(types.ResourceSnapshot, "granularity"),
        @offsetOf(types.ResourceSnapshot, "inapplicable_actions"),
        @offsetOf(types.ResourceSnapshot, "action_route"),
        @offsetOf(types.ResourceSnapshot, "owner_demand_mask"),
        @offsetOf(types.ResourceSnapshot, "subscription_intent_mask"),
        @offsetOf(types.ResourceSnapshot, "activity_score"),
        @offsetOf(types.ResourceSnapshot, "surface_state"),
        @offsetOf(types.ResourceSnapshot, "availability"),
        @offsetOf(types.ResourceSnapshot, "flags"),
        @offsetOf(types.ResourceSnapshot, "allowed_actions"),
        @offsetOf(types.ResourceSnapshot, "consistency_state"),
        @offsetOf(types.ResourceSnapshot, "destructive_active"),
        @offsetOf(types.ResourceSnapshot, "reserved0"),

        @sizeOf(types.DestructiveExpectedRequest),
        @offsetOf(types.DestructiveExpectedRequest, "abi_version"),
        @offsetOf(types.DestructiveExpectedRequest, "struct_size"),
        @offsetOf(types.DestructiveExpectedRequest, "resource"),
        @offsetOf(types.DestructiveExpectedRequest, "scheduling_revision"),
        @offsetOf(types.DestructiveExpectedRequest, "action_attempt_id_low"),
        @offsetOf(types.DestructiveExpectedRequest, "action_attempt_id_high"),
        @offsetOf(types.DestructiveExpectedRequest, "action_mask"),
        @offsetOf(types.DestructiveExpectedRequest, "reserved0"),

        @sizeOf(types.DestructiveToken),
        @offsetOf(types.DestructiveToken, "resource"),
        @offsetOf(types.DestructiveToken, "scheduling_revision"),
        @offsetOf(types.DestructiveToken, "gate_epoch"),
        @offsetOf(types.DestructiveToken, "action_attempt_id_low"),
        @offsetOf(types.DestructiveToken, "action_attempt_id_high"),
        @offsetOf(types.DestructiveToken, "action_mask"),
        @offsetOf(types.DestructiveToken, "reserved0"),

        @sizeOf(types.DestructiveNoEffectReceipt),
        @offsetOf(types.DestructiveNoEffectReceipt, "abi_version"),
        @offsetOf(types.DestructiveNoEffectReceipt, "struct_size"),
        @offsetOf(types.DestructiveNoEffectReceipt, "action_attempt_id_low"),
        @offsetOf(types.DestructiveNoEffectReceipt, "action_attempt_id_high"),
        @offsetOf(types.DestructiveNoEffectReceipt, "receipt_id_low"),
        @offsetOf(types.DestructiveNoEffectReceipt, "receipt_id_high"),
        @offsetOf(types.DestructiveNoEffectReceipt, "observed_monotonic_timestamp"),
        @offsetOf(types.DestructiveNoEffectReceipt, "proof_mask"),
        @offsetOf(types.DestructiveNoEffectReceipt, "reason"),
        @offsetOf(types.DestructiveNoEffectReceipt, "reserved0"),
    };
    var hash: u64 = 14695981039346656037;
    for (values) |value| {
        hash = (hash ^ @as(u64, @intCast(value))) *% 1099511628211;
    }
    return hash;
}

pub export fn rm_shared_ledger_required_size(
    config: ?*const types.LedgerConfig,
    output_size: ?*u64,
) callconv(.c) i32 {
    const actual = config orelse return result(.invalid_argument);
    const output = output_size orelse return result(.invalid_argument);
    const computed = wire.calculate(actual) catch |err| return mapError(err);
    output.* = computed.required_size;
    return result(.ok);
}

pub export fn rm_shared_ledger_create(
    mapping: ?*anyopaque,
    mapping_size: u64,
    windows_mutex_handle: ?*anyopaque,
    config: ?*const types.LedgerConfig,
    output_handle: ?*?*anyopaque,
) callconv(.c) i32 {
    const bytes: [*]u8 = @ptrCast(mapping orelse return result(.invalid_argument));
    const actual = config orelse return result(.invalid_argument);
    const output = output_handle orelse return result(.invalid_argument);
    output.* = null;
    const context = state.Context.create(
        bytes,
        std.math.cast(usize, mapping_size) orelse return result(.mapping_too_small),
        windows_mutex_handle,
        actual,
    ) catch |err| return mapError(err);
    output.* = @ptrCast(context);
    return result(.ok);
}

pub export fn rm_shared_ledger_open(
    mapping: ?*anyopaque,
    mapping_size: u64,
    windows_mutex_handle: ?*anyopaque,
    config: ?*const types.LedgerConfig,
    writable: u8,
    output_handle: ?*?*anyopaque,
) callconv(.c) i32 {
    const bytes: [*]u8 = @ptrCast(mapping orelse return result(.invalid_argument));
    const actual = config orelse return result(.invalid_argument);
    const output = output_handle orelse return result(.invalid_argument);
    output.* = null;
    const context = state.Context.open(
        bytes,
        std.math.cast(usize, mapping_size) orelse return result(.mapping_too_small),
        windows_mutex_handle,
        actual,
        writable != 0,
    ) catch |err| return mapError(err);
    output.* = @ptrCast(context);
    return result(.ok);
}

pub export fn rm_shared_ledger_destroy(handle: ?*anyopaque) callconv(.c) void {
    const context = asContext(handle) orelse return;
    context.destroy();
}

pub export fn rm_shared_resource_publish(
    handle: ?*anyopaque,
    publication: ?*const types.ResourcePublication,
    output: ?*types.ResourceRef,
) callconv(.c) i32 {
    const context = asContext(handle) orelse return result(.invalid_argument);
    resources.publish(
        context,
        publication orelse return result(.invalid_argument),
        output orelse return result(.invalid_argument),
    ) catch |err| return mapError(err);
    return result(.ok);
}

pub export fn rm_shared_resource_update(
    handle: ?*anyopaque,
    resource: ?*const types.ResourceRef,
    publication: ?*const types.ResourcePublication,
) callconv(.c) i32 {
    const context = asContext(handle) orelse return result(.invalid_argument);
    resources.update(
        context,
        (resource orelse return result(.invalid_argument)).*,
        publication orelse return result(.invalid_argument),
    ) catch |err| return mapError(err);
    return result(.ok);
}

pub export fn rm_shared_resource_revoke(
    handle: ?*anyopaque,
    resource: ?*const types.ResourceRef,
) callconv(.c) i32 {
    const context = asContext(handle) orelse return result(.invalid_argument);
    resources.revoke(context, (resource orelse return result(.invalid_argument)).*) catch |err| return mapError(err);
    return result(.ok);
}

pub export fn rm_shared_resource_snapshot(
    handle: ?*anyopaque,
    resource: ?*const types.ResourceRef,
    output: ?*types.ResourceSnapshot,
) callconv(.c) i32 {
    const context = asContext(handle) orelse return result(.invalid_argument);
    resources.snapshotOne(
        context,
        (resource orelse return result(.invalid_argument)).*,
        output orelse return result(.invalid_argument),
    ) catch |err| return mapError(err);
    return result(.ok);
}

pub export fn rm_shared_resource_snapshot_by_public_id(
    handle: ?*anyopaque,
    public_resource_id: u64,
    output: ?*types.ResourceSnapshot,
) callconv(.c) i32 {
    const context = asContext(handle) orelse return result(.invalid_argument);
    resources.snapshotByPublicId(
        context,
        public_resource_id,
        output orelse return result(.invalid_argument),
    ) catch |err| return mapError(err);
    return result(.ok);
}

pub export fn rm_shared_resource_snapshot_batch(
    handle: ?*anyopaque,
    output: ?[*]types.ResourceSnapshot,
    capacity: u32,
    output_count: ?*u32,
    topology_generation: ?*u64,
) callconv(.c) i32 {
    const context = asContext(handle) orelse return result(.invalid_argument);
    if (capacity > 0 and output == null) return result(.invalid_argument);
    var empty = std.mem.zeroes(types.ResourceSnapshot);
    const actual_output: [*]types.ResourceSnapshot = output orelse @ptrCast(&empty);
    resources.snapshotBatch(
        context,
        actual_output,
        capacity,
        output_count orelse return result(.invalid_argument),
        topology_generation orelse return result(.invalid_argument),
    ) catch |err| return mapError(err);
    return result(.ok);
}

pub export fn rm_shared_subscription_subscribe(
    handle: ?*anyopaque,
    request: ?*const types.SubscriptionRequest,
    output: ?*types.SubscriptionReceipt,
) callconv(.c) i32 {
    const context = asContext(handle) orelse return result(.invalid_argument);
    var stamped_request = (request orelse return result(.invalid_argument)).*;
    stamped_request.lease_duration = state.boundedDeadline(
        state.leaseClockNow() catch |err| return mapError(err),
        stamped_request.lease_duration,
        context.header.maximum_subscription_ttl,
    ) catch |err| return mapError(err);
    subscriptions.subscribe(
        context,
        &stamped_request,
        output orelse return result(.invalid_argument),
    ) catch |err| return mapError(err);
    return result(.ok);
}

pub export fn rm_shared_subscription_confirm(
    handle: ?*anyopaque,
    receipt: ?*const types.SubscriptionReceipt,
    lease_duration: u64,
    intent_mask: u8,
    output: ?*types.SubscriptionReceipt,
) callconv(.c) i32 {
    const context = asContext(handle) orelse return result(.invalid_argument);
    const deadline_timestamp = state.boundedDeadline(
        state.leaseClockNow() catch |err| return mapError(err),
        lease_duration,
        context.header.maximum_subscription_ttl,
    ) catch |err| return mapError(err);
    subscriptions.confirm(
        context,
        receipt orelse return result(.invalid_argument),
        deadline_timestamp,
        intent_mask,
        output orelse return result(.invalid_argument),
    ) catch |err| return mapError(err);
    return result(.ok);
}

pub export fn rm_shared_subscription_release(
    handle: ?*anyopaque,
    receipt: ?*const types.SubscriptionReceipt,
) callconv(.c) i32 {
    const context = asContext(handle) orelse return result(.invalid_argument);
    subscriptions.release(context, receipt orelse return result(.invalid_argument)) catch |err| return mapError(err);
    return result(.ok);
}

pub export fn rm_shared_subscription_sweep(
    handle: ?*anyopaque,
    expired_count: ?*u32,
) callconv(.c) i32 {
    const context = asContext(handle) orelse return result(.invalid_argument);
    subscriptions.sweep(
        context,
        state.leaseClockNow() catch |err| return mapError(err),
        expired_count orelse return result(.invalid_argument),
    ) catch |err| return mapError(err);
    return result(.ok);
}

pub export fn rm_shared_use_enqueue(
    handle: ?*anyopaque,
    request: ?*const types.UseRequest,
    output: ?*types.TaskReceipt,
) callconv(.c) i32 {
    const context = asContext(handle) orelse return result(.invalid_argument);
    const now_timestamp = state.leaseClockNow() catch |err| return mapError(err);
    var stamped_request = (request orelse return result(.invalid_argument)).*;
    stamped_request.queue_lease_duration = state.boundedDeadline(
        now_timestamp,
        stamped_request.queue_lease_duration,
        context.header.maximum_queue_ttl,
    ) catch |err| return mapError(err);
    tasks.enqueue(
        context,
        &stamped_request,
        now_timestamp,
        output orelse return result(.invalid_argument),
    ) catch |err| return mapError(err);
    return result(.ok);
}

pub export fn rm_shared_use_reserve_next(
    handle: ?*anyopaque,
    resource: ?*const types.ResourceRef,
    grant_lease_duration: u64,
    output: ?*types.GrantReceipt,
) callconv(.c) i32 {
    const context = asContext(handle) orelse return result(.invalid_argument);
    const now_timestamp = state.leaseClockNow() catch |err| return mapError(err);
    const grant_deadline_timestamp = state.boundedDeadline(
        now_timestamp,
        grant_lease_duration,
        context.header.maximum_grant_ttl,
    ) catch |err| return mapError(err);
    tasks.reserveNext(
        context,
        (resource orelse return result(.invalid_argument)).*,
        now_timestamp,
        grant_deadline_timestamp,
        output orelse return result(.invalid_argument),
    ) catch |err| return mapError(err);
    return result(.ok);
}

pub export fn rm_shared_use_begin(
    handle: ?*anyopaque,
    receipt: ?*const types.GrantReceipt,
    output: ?*types.GrantReceipt,
) callconv(.c) i32 {
    const context = asContext(handle) orelse return result(.invalid_argument);
    tasks.beginGrantedUse(
        context,
        receipt orelse return result(.invalid_argument),
        state.leaseClockNow() catch |err| return mapError(err),
        output orelse return result(.invalid_argument),
    ) catch |err| return mapError(err);
    return result(.ok);
}

pub export fn rm_shared_use_confirm(
    handle: ?*anyopaque,
    receipt: ?*const types.GrantReceipt,
    lease_duration: u64,
    output: ?*types.GrantReceipt,
) callconv(.c) i32 {
    const context = asContext(handle) orelse return result(.invalid_argument);
    const now_timestamp = state.leaseClockNow() catch |err| return mapError(err);
    const new_deadline_timestamp = state.boundedDeadline(
        now_timestamp,
        lease_duration,
        context.header.maximum_grant_ttl,
    ) catch |err| return mapError(err);
    tasks.confirmGrantedUse(
        context,
        receipt orelse return result(.invalid_argument),
        now_timestamp,
        new_deadline_timestamp,
        output orelse return result(.invalid_argument),
    ) catch |err| return mapError(err);
    return result(.ok);
}

pub export fn rm_shared_use_complete(
    handle: ?*anyopaque,
    receipt: ?*const types.GrantReceipt,
) callconv(.c) i32 {
    const context = asContext(handle) orelse return result(.invalid_argument);
    tasks.completeGrantedUse(context, receipt orelse return result(.invalid_argument)) catch |err| return mapError(err);
    return result(.ok);
}

pub export fn rm_shared_use_cancel(
    handle: ?*anyopaque,
    receipt: ?*const types.TaskReceipt,
) callconv(.c) i32 {
    const context = asContext(handle) orelse return result(.invalid_argument);
    tasks.cancel(context, receipt orelse return result(.invalid_argument)) catch |err| return mapError(err);
    return result(.ok);
}

pub export fn rm_shared_use_sweep(
    handle: ?*anyopaque,
    removed_count: ?*u32,
) callconv(.c) i32 {
    const context = asContext(handle) orelse return result(.invalid_argument);
    tasks.sweep(
        context,
        state.leaseClockNow() catch |err| return mapError(err),
        removed_count orelse return result(.invalid_argument),
    ) catch |err| return mapError(err);
    return result(.ok);
}

pub export fn rm_shared_action_filter(
    handle: ?*anyopaque,
    resource: ?*const types.ResourceRef,
    requested_actions: u8,
    allowed_actions: ?*u8,
) callconv(.c) i32 {
    const context = asContext(handle) orelse return result(.invalid_argument);
    actions.filter(
        context,
        (resource orelse return result(.invalid_argument)).*,
        requested_actions,
        allowed_actions orelse return result(.invalid_argument),
    ) catch |err| return mapError(err);
    return result(.ok);
}

pub export fn rm_shared_action_begin_destructive_expected(
    handle: ?*anyopaque,
    request: ?*const types.DestructiveExpectedRequest,
    output: ?*types.DestructiveToken,
) callconv(.c) i32 {
    const context = asContext(handle) orelse return result(.invalid_argument);
    actions.beginDestructiveExpected(
        context,
        request orelse return result(.invalid_argument),
        output orelse return result(.invalid_argument),
    ) catch |err| return mapError(err);
    return result(.ok);
}

pub export fn rm_shared_action_commit_destructive(
    handle: ?*anyopaque,
    token: ?*const types.DestructiveToken,
    post_state: ?*const types.ResourcePublication,
) callconv(.c) i32 {
    const context = asContext(handle) orelse return result(.invalid_argument);
    actions.commitDestructive(
        context,
        token orelse return result(.invalid_argument),
        post_state orelse return result(.invalid_argument),
    ) catch |err| return mapError(err);
    return result(.ok);
}

pub export fn rm_shared_action_abort_destructive(
    handle: ?*anyopaque,
    token: ?*const types.DestructiveToken,
    receipt: ?*const types.DestructiveNoEffectReceipt,
) callconv(.c) i32 {
    const context = asContext(handle) orelse return result(.invalid_argument);
    actions.abortDestructive(
        context,
        token orelse return result(.invalid_argument),
        receipt orelse return result(.invalid_argument),
    ) catch |err| return mapError(err);
    return result(.ok);
}

pub export fn rm_shared_action_begin_recall_expected(
    handle: ?*anyopaque,
    request: ?*const types.RecallExpectedRequest,
    output: ?*types.RecallToken,
) callconv(.c) i32 {
    const context = asContext(handle) orelse return result(.invalid_argument);
    actions.beginRecallExpected(
        context,
        request orelse return result(.invalid_argument),
        output orelse return result(.invalid_argument),
    ) catch |err| return mapError(err);
    return result(.ok);
}

pub export fn rm_shared_action_commit_recall(
    handle: ?*anyopaque,
    token: ?*const types.RecallToken,
    post_state: ?*const types.ResourcePublication,
) callconv(.c) i32 {
    const context = asContext(handle) orelse return result(.invalid_argument);
    actions.commitRecall(
        context,
        token orelse return result(.invalid_argument),
        post_state orelse return result(.invalid_argument),
    ) catch |err| return mapError(err);
    return result(.ok);
}

pub export fn rm_shared_action_abort_recall(
    handle: ?*anyopaque,
    token: ?*const types.RecallToken,
    receipt: ?*const types.DestructiveNoEffectReceipt,
) callconv(.c) i32 {
    const context = asContext(handle) orelse return result(.invalid_argument);
    actions.abortRecall(
        context,
        token orelse return result(.invalid_argument),
        receipt orelse return result(.invalid_argument),
    ) catch |err| return mapError(err);
    return result(.ok);
}

pub export fn rm_shared_public_manager_plan(
    handle: ?*anyopaque,
    request: ?*const public_manager.PlanRequest,
    output: ?[*]public_manager.Candidate,
    output_capacity: u32,
    summary: ?*public_manager.PlanSummary,
) callconv(.c) i32 {
    const context = asContext(handle) orelse return result(.invalid_argument);
    var empty_candidate = std.mem.zeroes(public_manager.Candidate);
    public_manager.plan(
        context,
        request orelse return result(.invalid_argument),
        output orelse @ptrCast(&empty_candidate),
        output_capacity,
        summary orelse return result(.invalid_argument),
    ) catch |err| return mapError(err);
    return result(.ok);
}

fn asContext(handle: ?*anyopaque) ?*state.Context {
    return @ptrCast(@alignCast(handle orelse return null));
}

fn result(code: types.ResultCode) i32 {
    return @intFromEnum(code);
}

fn mapError(err: anyerror) i32 {
    return result(switch (err) {
        error.InvalidConfig, error.InvalidArgument => .invalid_argument,
        error.MappingTooSmall => .mapping_too_small,
        error.InvalidMapping => .invalid_mapping,
        error.CapacityExhausted => .capacity_exhausted,
        error.NotFound => .not_found,
        error.StaleReference => .stale_reference,
        error.ResourceBusy => .resource_busy,
        error.QueueEmpty => .queue_empty,
        error.ReadOnly, error.AccessDenied => .access_denied,
        error.Expired => .expired,
        error.InvalidState => .invalid_state,
        error.InconsistentState => .inconsistent_state,
        error.SynchronizationFailed,
        error.MissingMutexHandle,
        error.UnsupportedSynchronization,
        error.UnsupportedLeaseClock,
        error.LeaseClockUnavailable,
        => .synchronization_failed,
        error.BufferTooSmall => .buffer_too_small,
        error.Conflict => .conflict,
        else => .invalid_argument,
    });
}

test "shared resource authority layouts have a fixed v11 fingerprint" {
    try std.testing.expectEqual(
        @as(u64, 0x8DF79DD673189108),
        rm_shared_resource_layout_fingerprint(),
    );
}

test {
    _ = @import("tests.zig");
    _ = @import("public_manager.zig");
}
