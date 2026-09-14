const std = @import("std");
const protocol = @import("protocol.zig");
const session_module = @import("session.zig");
const ResultCode = @import("../../common/result_codes.zig").ResultCode;

pub export fn rm_public_service_coordinator_abi_version() callconv(.c) u32 {
    return protocol.abi_version;
}

pub export fn rm_public_service_coordinator_create(
    config: ?*const protocol.Config,
    handle: ?*?*anyopaque,
) callconv(.c) i32 {
    const output = handle orelse return code(.invalid_argument);
    output.* = null;
    const input = config orelse return code(.invalid_argument);
    const session = session_module.Session.create(std.heap.page_allocator, input.*) catch |err|
        return mapError(err);
    output.* = @ptrCast(session);
    return code(.ok);
}

pub export fn rm_public_service_coordinator_destroy(handle: ?*anyopaque) callconv(.c) void {
    const session = castSession(handle) orelse return;
    session.destroy();
}

pub export fn rm_public_service_coordinator_query_capacity(
    handle: ?*anyopaque,
    output: ?*protocol.Capacity,
    output_size: u32,
) callconv(.c) i32 {
    const result = output orelse return code(.invalid_argument);
    result.* = std.mem.zeroes(protocol.Capacity);
    if (output_size != @sizeOf(protocol.Capacity)) return code(.abi_mismatch);
    const session = castSession(handle) orelse return code(.invalid_argument);
    lockMutex(&session.mutex);
    defer session.mutex.unlock();
    result.* = session.capacity();
    return code(.ok);
}

pub export fn rm_public_service_coordinator_replace_catalog(
    handle: ?*anyopaque,
    input: ?*const protocol.CatalogReplaceInput,
    capability_pointer: ?[*]const protocol.CapabilityInput,
    capability_count: u32,
    route_pointer: ?[*]const protocol.RouteInput,
    route_count: u32,
    text_pointer: ?[*]const u8,
    text_byte_count: u32,
) callconv(.c) i32 {
    const session = castSession(handle) orelse return code(.invalid_argument);
    lockMutex(&session.mutex);
    defer session.mutex.unlock();
    const capabilities = constSlice(protocol.CapabilityInput, capability_pointer, capability_count) orelse
        return code(.invalid_argument);
    const routes = constSlice(protocol.RouteInput, route_pointer, route_count) orelse
        return code(.invalid_argument);
    const text = constSlice(u8, text_pointer, text_byte_count) orelse
        return code(.invalid_argument);
    session.replaceCatalog(
        (input orelse return code(.invalid_argument)).*,
        capabilities,
        routes,
        text,
    ) catch |err| return mapError(err);
    return code(.ok);
}

pub export fn rm_public_service_coordinator_replace_models(
    handle: ?*anyopaque,
    input: ?*const protocol.ModelReplaceInput,
    model_pointer: ?[*]const protocol.ModelInput,
    model_count: u32,
    alias_pointer: ?[*]const protocol.ModelAliasInput,
    alias_count: u32,
    text_pointer: ?[*]const u8,
    text_byte_count: u32,
) callconv(.c) i32 {
    const session = castSession(handle) orelse return code(.invalid_argument);
    lockMutex(&session.mutex);
    defer session.mutex.unlock();
    const models = constSlice(protocol.ModelInput, model_pointer, model_count) orelse
        return code(.invalid_argument);
    const aliases = constSlice(protocol.ModelAliasInput, alias_pointer, alias_count) orelse
        return code(.invalid_argument);
    const text = constSlice(u8, text_pointer, text_byte_count) orelse
        return code(.invalid_argument);
    session.replaceModels(
        (input orelse return code(.invalid_argument)).*,
        models,
        aliases,
        text,
    ) catch |err| return mapError(err);
    return code(.ok);
}

pub export fn rm_public_service_coordinator_plan_model_acquisition(
    handle: ?*anyopaque,
    input: ?*const protocol.ModelAcquisitionPlanInput,
    output: ?*protocol.ModelAcquisitionPlanOutput,
) callconv(.c) i32 {
    const result = output orelse return code(.invalid_argument);
    result.* = std.mem.zeroes(protocol.ModelAcquisitionPlanOutput);
    const session = castSession(handle) orelse return code(.invalid_argument);
    lockMutex(&session.mutex);
    defer session.mutex.unlock();
    result.* = session.planModelAcquisition(
        (input orelse return code(.invalid_argument)).*,
    ) catch |err| return mapError(err);
    return code(.ok);
}

pub export fn rm_public_service_coordinator_complete_model_acquisition(
    handle: ?*anyopaque,
    input: ?*const protocol.ModelAcquisitionCompletionInput,
) callconv(.c) i32 {
    const session = castSession(handle) orelse return code(.invalid_argument);
    lockMutex(&session.mutex);
    defer session.mutex.unlock();
    session.completeModelAcquisition(
        (input orelse return code(.invalid_argument)).*,
    ) catch |err| return mapError(err);
    return code(.ok);
}

pub export fn rm_public_service_coordinator_admit_request(
    handle: ?*anyopaque,
    input: ?*const protocol.AccessInput,
    path_pointer: ?[*]const u8,
    path_byte_count: u32,
    output: ?*protocol.AccessOutput,
) callconv(.c) i32 {
    const result = output orelse return code(.invalid_argument);
    result.* = std.mem.zeroes(protocol.AccessOutput);
    const session = castSession(handle) orelse return code(.invalid_argument);
    lockMutex(&session.mutex);
    defer session.mutex.unlock();
    const path = constSlice(u8, path_pointer, path_byte_count) orelse
        return code(.invalid_argument);
    result.* = session.admitRequest(
        (input orelse return code(.invalid_argument)).*,
        path,
    ) catch |err| return mapError(err);
    return code(.ok);
}

pub export fn rm_public_service_coordinator_complete_request(
    handle: ?*anyopaque,
    input: ?*const protocol.RequestCompleteInput,
    output: ?*protocol.RequestCompleteOutput,
) callconv(.c) i32 {
    const result = output orelse return code(.invalid_argument);
    result.* = std.mem.zeroes(protocol.RequestCompleteOutput);
    const session = castSession(handle) orelse return code(.invalid_argument);
    lockMutex(&session.mutex);
    defer session.mutex.unlock();
    result.* = session.completeRequest(
        (input orelse return code(.invalid_argument)).*,
    ) catch |err|
        return mapError(err);
    return code(.ok);
}

pub export fn rm_public_service_coordinator_resolve_model(
    handle: ?*anyopaque,
    input: ?*const protocol.ModelResolveInput,
    alias_pointer: ?[*]const u8,
    alias_byte_count: u32,
    output: ?*protocol.ModelResolveOutput,
) callconv(.c) i32 {
    const result = output orelse return code(.invalid_argument);
    result.* = std.mem.zeroes(protocol.ModelResolveOutput);
    const session = castSession(handle) orelse return code(.invalid_argument);
    lockMutex(&session.mutex);
    defer session.mutex.unlock();
    const alias = constSlice(u8, alias_pointer, alias_byte_count) orelse
        return code(.invalid_argument);
    result.* = session.resolveModel(
        (input orelse return code(.invalid_argument)).*,
        alias,
    ) catch |err| return mapError(err);
    return code(.ok);
}

pub export fn rm_public_service_coordinator_begin_lease(
    handle: ?*anyopaque,
    input: ?*const protocol.LeaseBeginInput,
    output: ?*protocol.LeaseOutput,
) callconv(.c) i32 {
    const result = output orelse return code(.invalid_argument);
    result.* = std.mem.zeroes(protocol.LeaseOutput);
    const session = castSession(handle) orelse return code(.invalid_argument);
    lockMutex(&session.mutex);
    defer session.mutex.unlock();
    result.* = session.beginLease((input orelse return code(.invalid_argument)).*) catch |err|
        return mapError(err);
    return code(.ok);
}

pub export fn rm_public_service_coordinator_end_lease(
    handle: ?*anyopaque,
    input: ?*const protocol.LeaseEndInput,
) callconv(.c) i32 {
    const session = castSession(handle) orelse return code(.invalid_argument);
    lockMutex(&session.mutex);
    defer session.mutex.unlock();
    session.endLease((input orelse return code(.invalid_argument)).*) catch |err|
        return mapError(err);
    return code(.ok);
}

pub export fn rm_public_service_coordinator_upsert_subscription(
    handle: ?*anyopaque,
    input: ?*const protocol.SubscriptionUpsertInput,
    output: ?*protocol.SubscriptionOutput,
) callconv(.c) i32 {
    const result = output orelse return code(.invalid_argument);
    result.* = std.mem.zeroes(protocol.SubscriptionOutput);
    const session = castSession(handle) orelse return code(.invalid_argument);
    lockMutex(&session.mutex);
    defer session.mutex.unlock();
    result.* = session.upsertSubscription(
        (input orelse return code(.invalid_argument)).*,
    ) catch |err| return mapError(err);
    return code(.ok);
}

pub export fn rm_public_service_coordinator_remove_subscription(
    handle: ?*anyopaque,
    input: ?*const protocol.SubscriptionRemoveInput,
) callconv(.c) i32 {
    const session = castSession(handle) orelse return code(.invalid_argument);
    lockMutex(&session.mutex);
    defer session.mutex.unlock();
    session.removeSubscription((input orelse return code(.invalid_argument)).*) catch |err|
        return mapError(err);
    return code(.ok);
}

pub export fn rm_public_service_coordinator_enqueue_task(
    handle: ?*anyopaque,
    input: ?*const protocol.TaskEnqueueInput,
    output: ?*protocol.TaskOutput,
) callconv(.c) i32 {
    const result = output orelse return code(.invalid_argument);
    result.* = std.mem.zeroes(protocol.TaskOutput);
    const session = castSession(handle) orelse return code(.invalid_argument);
    lockMutex(&session.mutex);
    defer session.mutex.unlock();
    result.* = session.enqueueTask((input orelse return code(.invalid_argument)).*) catch |err|
        return mapError(err);
    return code(.ok);
}

pub export fn rm_public_service_coordinator_plan_tasks(
    handle: ?*anyopaque,
    input: ?*const protocol.TaskPlanInput,
    output_pointer: ?[*]protocol.TaskOutput,
    output_capacity: u32,
    output: ?*protocol.TaskPlanOutput,
) callconv(.c) i32 {
    const result = output orelse return code(.invalid_argument);
    result.* = std.mem.zeroes(protocol.TaskPlanOutput);
    const session = castSession(handle) orelse return code(.invalid_argument);
    lockMutex(&session.mutex);
    defer session.mutex.unlock();
    const rows = mutableSlice(protocol.TaskOutput, output_pointer, output_capacity) orelse
        return code(.invalid_argument);
    result.* = session.planTasks(
        (input orelse return code(.invalid_argument)).*,
        rows,
    ) catch |err| return mapError(err);
    return code(.ok);
}

pub export fn rm_public_service_coordinator_complete_task(
    handle: ?*anyopaque,
    input: ?*const protocol.TaskCompletionInput,
    output: ?*protocol.TaskCompletionOutput,
) callconv(.c) i32 {
    const result = output orelse return code(.invalid_argument);
    result.* = std.mem.zeroes(protocol.TaskCompletionOutput);
    const session = castSession(handle) orelse return code(.invalid_argument);
    lockMutex(&session.mutex);
    defer session.mutex.unlock();
    result.* = session.completeTask(
        (input orelse return code(.invalid_argument)).*,
    ) catch |err|
        return mapError(err);
    return code(.ok);
}

pub export fn rm_public_service_coordinator_cancel_queued_tasks(
    handle: ?*anyopaque,
    input: ?*const protocol.TaskCancelInput,
    output_pointer: ?[*]protocol.TaskOutput,
    output_capacity: u32,
    output: ?*protocol.TaskCancelOutput,
) callconv(.c) i32 {
    const result = output orelse return code(.invalid_argument);
    result.* = std.mem.zeroes(protocol.TaskCancelOutput);
    const session = castSession(handle) orelse return code(.invalid_argument);
    lockMutex(&session.mutex);
    defer session.mutex.unlock();
    const rows = mutableSlice(protocol.TaskOutput, output_pointer, output_capacity) orelse
        return code(.invalid_argument);
    result.* = session.cancelQueuedTasks(
        (input orelse return code(.invalid_argument)).*,
        rows,
    ) catch |err| return mapError(err);
    return code(.ok);
}

pub export fn rm_public_service_coordinator_read_capabilities(
    handle: ?*anyopaque,
    output_pointer: ?[*]protocol.CapabilityOutput,
    output_capacity: u32,
    written_count: ?*u32,
) callconv(.c) i32 {
    const written = written_count orelse return code(.invalid_argument);
    written.* = 0;
    const session = castSession(handle) orelse return code(.invalid_argument);
    lockMutex(&session.mutex);
    defer session.mutex.unlock();
    const output = mutableSlice(protocol.CapabilityOutput, output_pointer, output_capacity) orelse
        return code(.invalid_argument);
    const count = session.readCapabilities(output) catch |err| return mapError(err);
    written.* = @intCast(count);
    return code(.ok);
}

pub export fn rm_public_service_coordinator_snapshot(
    handle: ?*anyopaque,
    output: ?*protocol.Snapshot,
    output_size: u32,
) callconv(.c) i32 {
    const result = output orelse return code(.invalid_argument);
    result.* = std.mem.zeroes(protocol.Snapshot);
    if (output_size != @sizeOf(protocol.Snapshot)) return code(.abi_mismatch);
    const session = castSession(handle) orelse return code(.invalid_argument);
    lockMutex(&session.mutex);
    defer session.mutex.unlock();
    result.* = session.snapshot();
    return code(.ok);
}

fn castSession(handle: ?*anyopaque) ?*session_module.Session {
    return @ptrCast(@alignCast(handle orelse return null));
}

fn lockMutex(mutex: *std.atomic.Mutex) void {
    while (!mutex.tryLock()) std.atomic.spinLoopHint();
}

fn constSlice(comptime T: type, pointer: ?[*]const T, count: u32) ?[]const T {
    if (count == 0) return &.{};
    return (pointer orelse return null)[0..count];
}

fn mutableSlice(comptime T: type, pointer: ?[*]T, count: u32) ?[]T {
    if (count == 0) return &.{};
    return (pointer orelse return null)[0..count];
}

fn mapError(err: anyerror) i32 {
    return switch (err) {
        error.InvalidArgument => code(.invalid_argument),
        error.InvalidState => code(.stale_frame),
        error.BufferTooSmall, error.CapacityExceeded => code(.buffer_too_small),
        error.OutOfMemory => code(.out_of_memory),
        error.CounterExhausted, error.ConfigurationMismatch, error.ResidentBudgetExceeded => code(.unavailable),
        else => code(.unavailable),
    };
}

fn code(result: ResultCode) i32 {
    return @intFromEnum(result);
}
