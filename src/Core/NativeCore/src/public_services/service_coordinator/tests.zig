const std = @import("std");
const abi = @import("abi.zig");
const index = @import("index.zig");
const protocol = @import("protocol.zig");
const session_module = @import("session.zig");
const ResultCode = @import("../../common/result_codes.zig").ResultCode;

test "module local C ABI carries one opaque session through routing lifecycle" {
    var config = testConfig();
    var handle: ?*anyopaque = null;
    try std.testing.expectEqual(
        @as(i32, 0),
        abi.rm_public_service_coordinator_create(&config, &handle),
    );
    defer abi.rm_public_service_coordinator_destroy(handle);

    var capacity = std.mem.zeroes(protocol.Capacity);
    try std.testing.expectEqual(
        @as(i32, 0),
        abi.rm_public_service_coordinator_query_capacity(
            handle,
            &capacity,
            @sizeOf(protocol.Capacity),
        ),
    );
    try std.testing.expectEqual(config.maximum_route_count, capacity.route_capacity);

    const text = "/api/public/v1/index";
    const capabilities = [_]protocol.CapabilityInput{
        .{
            .struct_size = @sizeOf(protocol.CapabilityInput),
            .flags = protocol.CapabilityFlags.enabled | protocol.CapabilityFlags.available,
            .capability_handle = 10,
            .payload_handle = 100,
            .reserved = 0,
        },
    };
    const routes = [_]protocol.RouteInput{
        route(2, 10, .{ .offset = 0, .length = text.len }, protocol.MethodMask.get, 0),
    };
    var replace = catalogHeader(1, 10, 1, capabilities.len, routes.len, text.len, true);
    try std.testing.expectEqual(
        @as(i32, 0),
        abi.rm_public_service_coordinator_replace_catalog(
            handle,
            &replace,
            capabilities[0..].ptr,
            capabilities.len,
            routes[0..].ptr,
            routes.len,
            text.ptr,
            text.len,
        ),
    );

    const path = "/api/public/v1/index/status";
    var access = accessInput(2, 20, 99, .loopback, .get, path);
    var access_output = std.mem.zeroes(protocol.AccessOutput);
    try std.testing.expectEqual(
        @as(i32, 0),
        abi.rm_public_service_coordinator_admit_request(
            handle,
            &access,
            path.ptr,
            path.len,
            &access_output,
        ),
    );
    try std.testing.expect(toBool(access_output.allowed));

    var complete = requestComplete(3, 30, access_output.request_handle);
    var completion_output = std.mem.zeroes(protocol.RequestCompleteOutput);
    try std.testing.expectEqual(
        @as(i32, 0),
        abi.rm_public_service_coordinator_complete_request(
            handle,
            &complete,
            &completion_output,
        ),
    );
    try std.testing.expectEqual(
        @intFromEnum(protocol.RequestCompletionDisposition.completed),
        completion_output.disposition,
    );
    var snapshot = std.mem.zeroes(protocol.Snapshot);
    try std.testing.expectEqual(
        @as(i32, 0),
        abi.rm_public_service_coordinator_snapshot(
            handle,
            &snapshot,
            @sizeOf(protocol.Snapshot),
        ),
    );
    try std.testing.expectEqual(@as(u32, 0), snapshot.request_count);
}

test "module local C ABI fails cleanly without output pollution" {
    var invalid_config = testConfig();
    invalid_config.resident_byte_budget = 1;
    var invalid_handle: ?*anyopaque = @ptrFromInt(16);
    try std.testing.expectEqual(
        @intFromEnum(ResultCode.unavailable),
        abi.rm_public_service_coordinator_create(&invalid_config, &invalid_handle),
    );
    try std.testing.expectEqual(@as(?*anyopaque, null), invalid_handle);

    var config = testConfig();
    var handle: ?*anyopaque = null;
    try std.testing.expectEqual(
        @intFromEnum(ResultCode.ok),
        abi.rm_public_service_coordinator_create(&config, &handle),
    );
    defer abi.rm_public_service_coordinator_destroy(handle);

    var capacity: protocol.Capacity = undefined;
    @memset(std.mem.asBytes(&capacity), 0xA5);
    try std.testing.expectEqual(
        @intFromEnum(ResultCode.abi_mismatch),
        abi.rm_public_service_coordinator_query_capacity(
            handle,
            &capacity,
            @sizeOf(protocol.Capacity) - 1,
        ),
    );
    try std.testing.expect(std.mem.allEqual(u8, std.mem.asBytes(&capacity), 0));

    var snapshot: protocol.Snapshot = undefined;
    @memset(std.mem.asBytes(&snapshot), 0xA5);
    try std.testing.expectEqual(
        @intFromEnum(ResultCode.abi_mismatch),
        abi.rm_public_service_coordinator_snapshot(
            handle,
            &snapshot,
            @sizeOf(protocol.Snapshot) - 1,
        ),
    );
    try std.testing.expect(std.mem.allEqual(u8, std.mem.asBytes(&snapshot), 0));

    const path = "/api";
    var input = accessInput(1, 1, 1, .loopback, .get, path);
    var access_output: protocol.AccessOutput = undefined;
    @memset(std.mem.asBytes(&access_output), 0xA5);
    try std.testing.expectEqual(
        @intFromEnum(ResultCode.invalid_argument),
        abi.rm_public_service_coordinator_admit_request(
            null,
            &input,
            path.ptr,
            path.len,
            &access_output,
        ),
    );
    try std.testing.expect(std.mem.allEqual(u8, std.mem.asBytes(&access_output), 0));
}

test "catalog routing rate limiting and exact request completion are stateful" {
    var session = try session_module.Session.create(std.testing.allocator, testConfig());
    defer session.destroy();

    const catalog_text = "/api/public/v1/api/public/v1/index";
    const capabilities = [_]protocol.CapabilityInput{
        .{
            .struct_size = @sizeOf(protocol.CapabilityInput),
            .flags = protocol.CapabilityFlags.enabled | protocol.CapabilityFlags.available,
            .capability_handle = 10,
            .payload_handle = 100,
            .reserved = 0,
        },
    };
    const routes = [_]protocol.RouteInput{
        route(1, 0, .{ .offset = 0, .length = 14 }, protocol.MethodMask.get | protocol.MethodMask.head, protocol.RouteFlags.exact_path | protocol.RouteFlags.catalog_route),
        route(2, 10, .{ .offset = 14, .length = 20 }, protocol.MethodMask.get | protocol.MethodMask.head, 0),
    };
    try session.replaceCatalog(
        catalogHeader(1, 100, 1, capabilities.len, routes.len, catalog_text.len, true),
        &capabilities,
        &routes,
        catalog_text,
    );

    const remote = try session.admitRequest(
        accessInput(2, 110, 7, .remote, .get, "/api/public/v1/index/status"),
        "/api/public/v1/index/status",
    );
    try std.testing.expect(!toBool(remote.allowed));
    try std.testing.expectEqual(@as(u32, 403), remote.status_code);

    const unsupported = try session.admitRequest(
        accessInput(3, 120, 7, .loopback, .post, "/api/public/v1/index/status"),
        "/api/public/v1/index/status",
    );
    try std.testing.expectEqual(@as(u32, 405), unsupported.status_code);

    const unknown_method = try session.admitRequest(
        accessInput(4, 125, 7, .loopback, .other, "/api/public/v1/index/status"),
        "/api/public/v1/index/status",
    );
    try std.testing.expectEqual(@as(u32, 405), unknown_method.status_code);

    const first = try session.admitRequest(
        accessInput(5, 130, 7, .loopback, .get, "/api/public/v1/index/status"),
        "/api/public/v1/index/status",
    );
    try std.testing.expect(toBool(first.allowed));
    try std.testing.expect(first.request_handle != 0);

    const second = try session.admitRequest(
        accessInput(6, 140, 7, .loopback, .get, "/api/public/v1/index/status"),
        "/api/public/v1/index/status",
    );
    try std.testing.expect(toBool(second.allowed));

    const limited = try session.admitRequest(
        accessInput(7, 150, 7, .loopback, .get, "/api/public/v1/index/status"),
        "/api/public/v1/index/status",
    );
    try std.testing.expectEqual(@as(u32, 429), limited.status_code);
    try std.testing.expectEqual(
        @intFromEnum(protocol.AccessReason.rate_limited),
        limited.reason,
    );

    _ = try session.completeRequest(requestComplete(8, 160, first.request_handle));
    _ = try session.completeRequest(requestComplete(9, 170, second.request_handle));
    try std.testing.expectEqual(@as(u32, 0), session.snapshot().request_count);
}

test "expired request keeps exact identity until caller completion" {
    var session = try session_module.Session.create(std.testing.allocator, testConfig());
    defer session.destroy();

    const path = "/api/public/v1/index";
    const capabilities = [_]protocol.CapabilityInput{.{
        .struct_size = @sizeOf(protocol.CapabilityInput),
        .flags = protocol.CapabilityFlags.enabled | protocol.CapabilityFlags.available,
        .capability_handle = 10,
        .payload_handle = 100,
        .reserved = 0,
    }};
    const routes = [_]protocol.RouteInput{
        route(2, 10, .{ .offset = 0, .length = path.len }, protocol.MethodMask.get, 0),
    };
    try session.replaceCatalog(
        catalogHeader(1, 10, 1, capabilities.len, routes.len, path.len, true),
        &capabilities,
        &routes,
        path,
    );

    const access = try session.admitRequest(
        accessInput(2, 20, 7, .loopback, .get, path),
        path,
    );
    try std.testing.expect(toBool(access.allowed));
    const completed = try session.completeRequest(
        requestComplete(3, access.expires_at_monotonic_milliseconds, access.request_handle),
    );
    try std.testing.expectEqual(
        @intFromEnum(protocol.RequestCompletionDisposition.expired),
        completed.disposition,
    );
    try std.testing.expectEqual(access.request_handle, completed.request_handle);
    try std.testing.expectEqual(@as(u32, 0), session.snapshot().request_count);
}

test "route policy alone controls rate limit bypass and request flags stay empty" {
    var session = try session_module.Session.create(std.testing.allocator, testConfig());
    defer session.destroy();

    const catalog_text = "/api/public/v1/unlimited";
    const capabilities = [_]protocol.CapabilityInput{.{
        .struct_size = @sizeOf(protocol.CapabilityInput),
        .flags = protocol.CapabilityFlags.enabled | protocol.CapabilityFlags.available,
        .capability_handle = 10,
        .payload_handle = 100,
        .reserved = 0,
    }};
    const routes = [_]protocol.RouteInput{
        route(
            2,
            10,
            .{ .offset = 0, .length = catalog_text.len },
            protocol.MethodMask.get,
            protocol.RouteFlags.bypass_rate_limit,
        ),
    };
    try session.replaceCatalog(
        catalogHeader(1, 100, 1, capabilities.len, routes.len, catalog_text.len, true),
        &capabilities,
        &routes,
        catalog_text,
    );

    var handles: [4]u64 = undefined;
    for (&handles, 0..) |*handle, index_in_batch| {
        const access = try session.admitRequest(
            accessInput(
                @as(u64, @intCast(index_in_batch)) + 2,
                @as(u64, @intCast(index_in_batch)) + 110,
                7,
                .loopback,
                .get,
                catalog_text,
            ),
            catalog_text,
        );
        try std.testing.expect(toBool(access.allowed));
        handle.* = access.request_handle;
    }

    var invalid_flags = accessInput(6, 120, 7, .loopback, .get, catalog_text);
    invalid_flags.flags = 1;
    try std.testing.expectError(
        error.InvalidArgument,
        session.admitRequest(invalid_flags, catalog_text),
    );
    try std.testing.expectEqual(@as(u64, 5), session.snapshot().last_command_epoch);
}

test "model acquisition owns retry timeout attempt identity and last good lifetime" {
    var session = try session_module.Session.create(std.testing.allocator, testConfig());
    defer session.destroy();

    const first = try session.planModelAcquisition(modelAcquisitionPlan(1, 10));
    try std.testing.expectEqual(
        @intFromEnum(protocol.ModelAcquisitionPlanStatus.start),
        first.status,
    );
    const running = try session.planModelAcquisition(modelAcquisitionPlan(2, 11));
    try std.testing.expectEqual(
        @intFromEnum(protocol.ModelAcquisitionPlanStatus.running),
        running.status,
    );
    try std.testing.expectEqual(first.attempt_handle, running.attempt_handle);

    try std.testing.expectError(
        error.InvalidState,
        session.completeModelAcquisition(modelAcquisitionCompletion(
            3,
            12,
            first.attempt_handle + 1,
            .failed,
        )),
    );
    try std.testing.expectEqual(@as(u64, 2), session.snapshot().last_command_epoch);

    try session.completeModelAcquisition(modelAcquisitionCompletion(
        3,
        12,
        first.attempt_handle,
        .failed,
    ));
    const retry_wait = try session.planModelAcquisition(modelAcquisitionPlan(4, 13));
    try std.testing.expectEqual(
        @intFromEnum(protocol.ModelAcquisitionPlanStatus.not_due),
        retry_wait.status,
    );
    try std.testing.expectEqual(@as(u64, 1_012), retry_wait.next_wake_monotonic_milliseconds);

    const retry = try session.planModelAcquisition(modelAcquisitionPlan(5, 1_012));
    try std.testing.expectEqual(
        @intFromEnum(protocol.ModelAcquisitionPlanStatus.start),
        retry.status,
    );
    try std.testing.expect(retry.attempt_handle != first.attempt_handle);

    const text = "open-model";
    const models = [_]protocol.ModelInput{model(41, 1, 101)};
    const aliases = [_]protocol.ModelAliasInput{alias(41, 0, text.len)};
    try std.testing.expectError(
        error.InvalidArgument,
        session.replaceModels(
            modelHeader(6, 1_013, 1, models.len, aliases.len, text.len, first.attempt_handle),
            &models,
            &aliases,
            text,
        ),
    );
    try session.replaceModels(
        modelHeader(6, 1_013, 1, models.len, aliases.len, text.len, retry.attempt_handle),
        &models,
        &aliases,
        text,
    );

    const usable = try session.resolveModel(modelResolve(7, 1_014, text), text);
    try std.testing.expectEqual(
        @intFromEnum(protocol.ModelResolveStatus.matched),
        usable.status,
    );
    const refresh = try session.planModelAcquisition(modelAcquisitionPlan(8, 1_018));
    try std.testing.expectEqual(
        @intFromEnum(protocol.ModelAcquisitionPlanStatus.start),
        refresh.status,
    );
    try session.completeModelAcquisition(modelAcquisitionCompletion(
        9,
        1_019,
        refresh.attempt_handle,
        .unavailable,
    ));
    const still_usable = try session.resolveModel(modelResolve(10, 1_020, text), text);
    try std.testing.expectEqual(
        @intFromEnum(protocol.ModelResolveStatus.matched),
        still_usable.status,
    );
    const expired = try session.resolveModel(modelResolve(11, 31_014, text), text);
    try std.testing.expectEqual(
        @intFromEnum(protocol.ModelResolveStatus.catalog_unavailable),
        expired.status,
    );
}

test "task planning never inherits model acquisition retry wake" {
    var session = try session_module.Session.create(std.testing.allocator, testConfig());
    defer session.destroy();

    const acquisition = try session.planModelAcquisition(modelAcquisitionPlan(1, 10));
    try session.completeModelAcquisition(modelAcquisitionCompletion(
        2,
        11,
        acquisition.attempt_handle,
        .failed,
    ));

    var planned: [1]protocol.TaskOutput = undefined;
    const before_due = try session.planTasks(taskPlan(3, 12, planned.len), &planned);
    try std.testing.expectEqual(@as(u32, 0), before_due.output_count);
    try std.testing.expectEqual(@as(u64, 0), before_due.next_wake_monotonic_milliseconds);
    try std.testing.expectEqual(@as(u64, 0), before_due.flags);

    const at_due = try session.planTasks(taskPlan(4, 1_011, planned.len), &planned);
    try std.testing.expectEqual(@as(u32, 0), at_due.output_count);
    try std.testing.expectEqual(@as(u64, 0), at_due.next_wake_monotonic_milliseconds);
    try std.testing.expectEqual(@as(u64, 0), at_due.flags);

    const retry = try session.planModelAcquisition(modelAcquisitionPlan(5, 1_011));
    try std.testing.expectEqual(
        @intFromEnum(protocol.ModelAcquisitionPlanStatus.start),
        retry.status,
    );
}

test "model aliases leases subscriptions and task ordering share one authority" {
    var session = try session_module.Session.create(std.testing.allocator, testConfig());
    defer session.destroy();
    try installModels(session, 1, 10);

    const resolved = try session.resolveModel(
        modelResolve(3, 20, " OPEN-MODEL "),
        " OPEN-MODEL ",
    );
    try std.testing.expectEqual(@intFromEnum(protocol.ModelResolveStatus.matched), resolved.status);
    try std.testing.expectEqual(@as(u64, 41), resolved.model_handle);

    const lease = try session.beginLease(.{
        .struct_size = @sizeOf(protocol.LeaseBeginInput),
        .reserved_u32 = 0,
        .command_epoch = 4,
        .command_monotonic_milliseconds = 30,
        .caller_handle = 9,
        .model_handle = 41,
        .requested_timeout_milliseconds = 1_000,
        .reserved = .{ 0, 0 },
    });

    const unload = try session.enqueueTask(taskInput(5, 40, 3, 41, 501, 500, .unload));
    const load = try session.enqueueTask(taskInput(6, 50, 4, 42, 502, 100, .load));
    var planned: [2]protocol.TaskOutput = undefined;
    const blocked_plan = try session.planTasks(taskPlan(7, 60, 2), &planned);
    try std.testing.expectEqual(@as(u32, 1), blocked_plan.output_count);
    try std.testing.expectEqual(load.task_handle, planned[0].task_handle);
    try std.testing.expectError(
        error.InvalidState,
        session.completeTask(taskComplete(8, 65, load.task_handle, 2, .succeeded)),
    );

    const load_completion = try session.completeTask(
        taskComplete(9, 70, load.task_handle, 1, .succeeded),
    );
    try std.testing.expectEqual(
        @intFromEnum(protocol.TaskCompletionDisposition.succeeded),
        load_completion.disposition,
    );
    try session.endLease(.{
        .struct_size = @sizeOf(protocol.LeaseEndInput),
        .reserved_u32 = 0,
        .command_epoch = 10,
        .command_monotonic_milliseconds = 80,
        .lease_handle = lease.lease_handle,
        .reserved = .{ 0, 0, 0 },
    });
    const unblocked_plan = try session.planTasks(taskPlan(11, 90, 2), &planned);
    try std.testing.expectEqual(@as(u32, 1), unblocked_plan.output_count);
    try std.testing.expectEqual(unload.task_handle, planned[0].task_handle);

    const retry_completion = try session.completeTask(
        taskComplete(12, 100, unload.task_handle, 1, .provider_unavailable),
    );
    try std.testing.expectEqual(
        @intFromEnum(protocol.TaskCompletionDisposition.retry_scheduled),
        retry_completion.disposition,
    );
    try std.testing.expectEqual(@as(u64, 1_100), retry_completion.next_wake_monotonic_milliseconds);
    const retry_blocked = try session.planTasks(taskPlan(13, 110, 2), &planned);
    try std.testing.expectEqual(@as(u32, 0), retry_blocked.output_count);
    const retried = try session.planTasks(taskPlan(14, 2_100, 2), &planned);
    try std.testing.expectEqual(@as(u32, 1), retried.output_count);
    try std.testing.expectEqual(unload.task_handle, planned[0].task_handle);
    try std.testing.expectEqual(@as(u32, 2), planned[0].attempt);
}

test "successful model task makes provider catalog acquisition immediately due" {
    var session = try session_module.Session.create(std.testing.allocator, testConfig());
    defer session.destroy();
    try installModels(session, 1, 10);

    const queued = try session.enqueueTask(taskInput(3, 20, 3, 41, 501, 500, .load));
    var planned: [1]protocol.TaskOutput = undefined;
    const task_plan = try session.planTasks(taskPlan(4, 21, 1), &planned);
    try std.testing.expectEqual(@as(u32, 1), task_plan.output_count);
    const completion = try session.completeTask(taskComplete(
        5,
        22,
        queued.task_handle,
        planned[0].attempt,
        .succeeded,
    ));
    try std.testing.expectEqual(
        @intFromEnum(protocol.TaskCompletionDisposition.succeeded),
        completion.disposition,
    );

    const acquisition = try session.planModelAcquisition(modelAcquisitionPlan(6, 22));
    try std.testing.expectEqual(
        @intFromEnum(protocol.ModelAcquisitionPlanStatus.start),
        acquisition.status,
    );
}

test "task outcome policy alone decides retry versus terminal failure" {
    var config = testConfig();
    config.retryable_task_outcome_mask = 0;
    config.retryable_http_status_policy_mask =
        protocol.TaskHttpRetryPolicyMask.throttled;
    var session = try session_module.Session.create(std.testing.allocator, config);
    defer session.destroy();
    try installModels(session, 1, 10);

    const retry_task = try session.enqueueTask(taskInput(3, 20, 3, 41, 501, 500, .load));
    var planned: [1]protocol.TaskOutput = undefined;
    _ = try session.planTasks(taskPlan(4, 21, 1), &planned);
    const retry = try session.completeTask(taskCompleteHttp(
        5,
        22,
        retry_task.task_handle,
        planned[0].attempt,
        429,
    ));
    try std.testing.expectEqual(
        @intFromEnum(protocol.TaskCompletionDisposition.retry_scheduled),
        retry.disposition,
    );

    const terminal_task = try session.enqueueTask(taskInput(6, 23, 3, 41, 502, 500, .load));
    const terminal_plan = try session.planTasks(taskPlan(7, 24, 1), &planned);
    try std.testing.expectEqual(@as(u32, 1), terminal_plan.output_count);
    const terminal = try session.completeTask(taskComplete(
        8,
        25,
        terminal_task.task_handle,
        planned[0].attempt,
        .failed,
    ));
    try std.testing.expectEqual(
        @intFromEnum(protocol.TaskCompletionDisposition.terminal_failure),
        terminal.disposition,
    );
    try std.testing.expectEqual(@as(u64, 0), terminal.next_wake_monotonic_milliseconds);
}

test "exact task completion settles a provider effect after its recovery deadline" {
    var session = try session_module.Session.create(std.testing.allocator, testConfig());
    defer session.destroy();
    try installModels(session, 1, 10);

    const queued = try session.enqueueTask(taskInput(3, 20, 3, 41, 501, 500, .load));
    var planned: [1]protocol.TaskOutput = undefined;
    const task_plan = try session.planTasks(taskPlan(4, 21, 1), &planned);
    try std.testing.expectEqual(@as(u32, 1), task_plan.output_count);
    const after_deadline = try session.planTasks(taskPlan(5, 10_000, 1), &planned);
    try std.testing.expectEqual(@as(u32, 0), after_deadline.output_count);
    try std.testing.expectEqual(@as(u32, 1), session.snapshot().running_task_count);

    const completion = try session.completeTask(taskComplete(
        6,
        10_001,
        queued.task_handle,
        planned[0].attempt,
        .succeeded,
    ));
    try std.testing.expectEqual(
        @intFromEnum(protocol.TaskCompletionDisposition.succeeded),
        completion.disposition,
    );

    const snapshot = session.snapshot();
    try std.testing.expectEqual(@as(u32, 0), snapshot.queued_task_count);
    try std.testing.expectEqual(@as(u32, 0), snapshot.running_task_count);
}

test "task retry policy stops at the explicit maximum attempt count" {
    var config = testConfig();
    config.maximum_task_attempt_count = 3;
    var session = try session_module.Session.create(std.testing.allocator, config);
    defer session.destroy();
    try installModels(session, 1, 10);

    const queued = try session.enqueueTask(taskInput(3, 20, 3, 41, 501, 500, .load));
    var planned: [1]protocol.TaskOutput = undefined;
    var command_epoch: u64 = 4;
    var now: u64 = 21;
    var expected_attempt: u32 = 1;
    while (expected_attempt <= config.maximum_task_attempt_count) : (expected_attempt += 1) {
        const plan = try session.planTasks(taskPlan(command_epoch, now, 1), &planned);
        try std.testing.expectEqual(@as(u32, 1), plan.output_count);
        try std.testing.expectEqual(expected_attempt, planned[0].attempt);
        command_epoch += 1;
        now += 1;
        const completion = try session.completeTask(taskComplete(
            command_epoch,
            now,
            queued.task_handle,
            expected_attempt,
            .provider_unavailable,
        ));
        command_epoch += 1;
        if (expected_attempt < config.maximum_task_attempt_count) {
            try std.testing.expectEqual(
                @intFromEnum(protocol.TaskCompletionDisposition.retry_scheduled),
                completion.disposition,
            );
            now = completion.next_wake_monotonic_milliseconds;
        } else {
            try std.testing.expectEqual(
                @intFromEnum(protocol.TaskCompletionDisposition.terminal_failure),
                completion.disposition,
            );
            try std.testing.expectEqual(@as(u64, 0), completion.next_wake_monotonic_milliseconds);
        }
    }
    try std.testing.expectEqual(@as(u32, 0), session.snapshot().queued_task_count);
    try std.testing.expectEqual(@as(u32, 0), session.snapshot().running_task_count);
}

test "queued task cancellation drains exact payloads and rejects running effects" {
    var session = try session_module.Session.create(std.testing.allocator, testConfig());
    defer session.destroy();
    try installModels(session, 1, 10);

    const first = try session.enqueueTask(taskInput(3, 20, 3, 41, 501, 500, .load));
    const second = try session.enqueueTask(taskInput(4, 21, 4, 42, 502, 100, .load));
    var cancelled: [2]protocol.TaskOutput = undefined;
    const output = try session.cancelQueuedTasks(
        taskCancel(5, 22, cancelled.len),
        &cancelled,
    );
    try std.testing.expectEqual(@as(u32, 2), output.output_count);
    try std.testing.expectEqual(@as(u32, 0), output.remaining_task_count);
    try std.testing.expectEqual(first.task_handle, cancelled[0].task_handle);
    try std.testing.expectEqual(second.task_handle, cancelled[1].task_handle);
    try std.testing.expectEqual(@as(u64, 501), cancelled[0].payload_handle);
    try std.testing.expectEqual(@as(u64, 502), cancelled[1].payload_handle);

    _ = try session.enqueueTask(taskInput(6, 23, 3, 41, 503, 500, .load));
    var planned: [1]protocol.TaskOutput = undefined;
    _ = try session.planTasks(taskPlan(7, 24, 1), &planned);
    try std.testing.expectError(
        error.InvalidState,
        session.cancelQueuedTasks(taskCancel(8, 25, cancelled.len), &cancelled),
    );
}

test "task attempt exhaustion rejects the whole plan without consuming command identity" {
    var session = try session_module.Session.create(std.testing.allocator, testConfig());
    defer session.destroy();
    try installModels(session, 1, 10);

    const first = try session.enqueueTask(taskInput(3, 20, 3, 41, 501, 500, .load));
    _ = try session.enqueueTask(taskInput(4, 30, 4, 42, 502, 100, .load));
    const exhausted_slot = index.findHandle(session.runtime.task_index, first.task_handle).?;
    session.runtime.tasks[exhausted_slot].row.attempt = std.math.maxInt(u32);

    var planned: [2]protocol.TaskOutput = undefined;
    try std.testing.expectError(
        error.CounterExhausted,
        session.planTasks(taskPlan(5, 40, 2), &planned),
    );

    const snapshot = session.snapshot();
    try std.testing.expectEqual(@as(u64, 4), snapshot.last_command_epoch);
    try std.testing.expectEqual(@as(u32, 2), snapshot.queued_task_count);
    try std.testing.expectEqual(@as(u32, 0), snapshot.running_task_count);
}

test "expired lease and subscription stop blocking model unload before task selection" {
    var session = try session_module.Session.create(std.testing.allocator, testConfig());
    defer session.destroy();
    try installModels(session, 1, 10);

    _ = try session.beginLease(.{
        .struct_size = @sizeOf(protocol.LeaseBeginInput),
        .reserved_u32 = 0,
        .command_epoch = 3,
        .command_monotonic_milliseconds = 20,
        .caller_handle = 8,
        .model_handle = 41,
        .requested_timeout_milliseconds = 5,
        .reserved = .{ 0, 0 },
    });
    _ = try session.upsertSubscription(subscriptionInput(4, 21, 700, 8, 41, 300, 5));
    const unload = try session.enqueueTask(taskInput(5, 22, 3, 41, 501, 500, .unload));

    var planned: [1]protocol.TaskOutput = undefined;
    const blocked = try session.planTasks(taskPlan(6, 24, 1), &planned);
    try std.testing.expectEqual(@as(u32, 0), blocked.output_count);

    const unblocked = try session.planTasks(taskPlan(7, 26, 1), &planned);
    try std.testing.expectEqual(@as(u32, 1), unblocked.output_count);
    try std.testing.expectEqual(unload.task_handle, planned[0].task_handle);
    try std.testing.expectEqual(@as(u32, 0), session.snapshot().subscription_count);
}

test "stale epoch and monotonic rollback do not mutate the accepted command frontier" {
    var session = try session_module.Session.create(std.testing.allocator, testConfig());
    defer session.destroy();
    try installModels(session, 1, 100);

    try std.testing.expectError(
        error.InvalidArgument,
        session.resolveModel(modelResolve(3, 99, "open-model"), "open-model"),
    );
    try std.testing.expectError(
        error.InvalidArgument,
        session.resolveModel(modelResolve(2, 101, "open-model"), "open-model"),
    );

    var snapshot = session.snapshot();
    try std.testing.expectEqual(@as(u64, 2), snapshot.last_command_epoch);
    try std.testing.expectEqual(@as(u64, 101), snapshot.last_command_monotonic_milliseconds);

    const resolved = try session.resolveModel(modelResolve(3, 101, "open-model"), "open-model");
    try std.testing.expectEqual(@intFromEnum(protocol.ModelResolveStatus.matched), resolved.status);
    snapshot = session.snapshot();
    try std.testing.expectEqual(@as(u64, 3), snapshot.last_command_epoch);
    try std.testing.expectEqual(@as(u64, 101), snapshot.last_command_monotonic_milliseconds);
}

test "fixed task slots are reused without reusing task identity across long lifecycle" {
    var session = try session_module.Session.create(std.testing.allocator, testConfig());
    defer session.destroy();
    try installModels(session, 1, 10);

    var epoch: u64 = 3;
    var now: u64 = 20;
    var previous_handle: u64 = 0;
    var planned: [1]protocol.TaskOutput = undefined;
    var iteration: usize = 0;
    while (iteration < 1_000) : (iteration += 1) {
        const queued = try session.enqueueTask(taskInput(
            epoch,
            now,
            3,
            41,
            @as(u64, @intCast(iteration)) + 1,
            500,
            .load,
        ));
        try std.testing.expect(queued.task_handle > previous_handle);
        previous_handle = queued.task_handle;
        epoch += 1;
        now += 1;

        const plan = try session.planTasks(taskPlan(epoch, now, 1), &planned);
        try std.testing.expectEqual(@as(u32, 1), plan.output_count);
        try std.testing.expectEqual(queued.task_handle, planned[0].task_handle);
        epoch += 1;
        now += 1;

        _ = try session.completeTask(taskComplete(
            epoch,
            now,
            queued.task_handle,
            planned[0].attempt,
            .succeeded,
        ));
        epoch += 1;
        now += 1;
    }

    const snapshot = session.snapshot();
    try std.testing.expectEqual(@as(u32, 0), snapshot.queued_task_count);
    try std.testing.expectEqual(@as(u32, 0), snapshot.running_task_count);
}

test "active model references make catalog replacement fail closed" {
    var session = try session_module.Session.create(std.testing.allocator, testConfig());
    defer session.destroy();
    try installModels(session, 1, 10);
    _ = try session.upsertSubscription(.{
        .struct_size = @sizeOf(protocol.SubscriptionUpsertInput),
        .flags = protocol.SubscriptionFlags.continuous_use,
        .command_epoch = 3,
        .command_monotonic_milliseconds = 20,
        .subscription_handle = 700,
        .caller_handle = 8,
        .model_handle = 41,
        .base_score = 300,
        .requested_timeout_milliseconds = 5_000,
        .reserved = .{ 0, 0 },
    });

    const text = "second-model";
    const models = [_]protocol.ModelInput{model(42, 2, 102)};
    const aliases = [_]protocol.ModelAliasInput{alias(42, 0, text.len)};
    const acquisition = try session.planModelAcquisition(
        modelAcquisitionPlan(4, 21),
    );
    try std.testing.expectError(
        error.InvalidState,
        session.replaceModels(
            modelHeader(
                5,
                30,
                2,
                models.len,
                aliases.len,
                text.len,
                acquisition.attempt_handle,
            ),
            &models,
            &aliases,
            text,
        ),
    );

    const still_present = try session.resolveModel(modelResolve(6, 40, "open-model"), "open-model");
    try std.testing.expectEqual(@as(u64, 41), still_present.model_handle);
}

test "duplicate folded model aliases are rejected atomically" {
    var session = try session_module.Session.create(std.testing.allocator, testConfig());
    defer session.destroy();
    try installModels(session, 1, 10);

    const text = "MODELmodel";
    const models = [_]protocol.ModelInput{
        model(41, 1, 101),
        model(42, 1, 102),
    };
    const aliases = [_]protocol.ModelAliasInput{
        alias(41, 0, 5),
        alias(42, 5, 5),
    };
    const acquisition = try session.planModelAcquisition(
        modelAcquisitionPlan(3, 20),
    );
    try std.testing.expectError(
        error.InvalidArgument,
        session.replaceModels(
            modelHeader(
                4,
                21,
                2,
                models.len,
                aliases.len,
                text.len,
                acquisition.attempt_handle,
            ),
            &models,
            &aliases,
            text,
        ),
    );

    const snapshot = session.snapshot();
    try std.testing.expectEqual(@as(u64, 3), snapshot.last_command_epoch);
    try std.testing.expectEqual(@as(u64, 1), snapshot.model_generation);
    const resolved = try session.resolveModel(modelResolve(4, 21, "open-model"), "open-model");
    try std.testing.expectEqual(@as(u64, 41), resolved.model_handle);
}

test "subscription handle cannot be reassigned to another caller" {
    var session = try session_module.Session.create(std.testing.allocator, testConfig());
    defer session.destroy();
    try installModels(session, 1, 10);
    _ = try session.upsertSubscription(subscriptionInput(3, 20, 700, 8, 41, 300, 5_000));

    var reassigned = subscriptionInput(4, 30, 700, 9, 42, 400, 5_000);
    try std.testing.expectError(error.InvalidState, session.upsertSubscription(reassigned));
    reassigned.caller_handle = 8;
    reassigned.command_epoch = 5;
    const updated = try session.upsertSubscription(reassigned);
    try std.testing.expectEqual(@as(u64, 8), updated.caller_handle);
    try std.testing.expectEqual(@as(u64, 42), updated.model_handle);
}

test "rejected model replacement does not expire runtime state or consume command" {
    var session = try session_module.Session.create(std.testing.allocator, testConfig());
    defer session.destroy();
    try installModels(session, 1, 10);
    _ = try session.upsertSubscription(.{
        .struct_size = @sizeOf(protocol.SubscriptionUpsertInput),
        .flags = protocol.SubscriptionFlags.continuous_use,
        .command_epoch = 3,
        .command_monotonic_milliseconds = 20,
        .subscription_handle = 700,
        .caller_handle = 8,
        .model_handle = 41,
        .base_score = 300,
        .requested_timeout_milliseconds = 5,
        .reserved = .{ 0, 0 },
    });
    _ = try session.enqueueTask(taskInput(4, 21, 3, 41, 501, 500, .unload));

    const text = "second-model";
    const models = [_]protocol.ModelInput{model(42, 2, 102)};
    const aliases = [_]protocol.ModelAliasInput{alias(42, 0, text.len)};
    const acquisition = try session.planModelAcquisition(
        modelAcquisitionPlan(5, 22),
    );
    try std.testing.expectError(
        error.InvalidState,
        session.replaceModels(
            modelHeader(
                6,
                30,
                2,
                models.len,
                aliases.len,
                text.len,
                acquisition.attempt_handle,
            ),
            &models,
            &aliases,
            text,
        ),
    );

    const snapshot = session.snapshot();
    try std.testing.expectEqual(@as(u64, 5), snapshot.last_command_epoch);
    try std.testing.expectEqual(@as(u32, 1), snapshot.subscription_count);
    try std.testing.expectEqual(@as(u32, 1), snapshot.queued_task_count);
    try std.testing.expectEqual(@as(u64, 1), snapshot.model_generation);
}

test "overlapping exact and prefix routes are rejected atomically" {
    var session = try session_module.Session.create(std.testing.allocator, testConfig());
    defer session.destroy();
    const text = "/api";
    const capabilities = [_]protocol.CapabilityInput{
        .{
            .struct_size = @sizeOf(protocol.CapabilityInput),
            .flags = protocol.CapabilityFlags.enabled | protocol.CapabilityFlags.available,
            .capability_handle = 10,
            .payload_handle = 100,
            .reserved = 0,
        },
    };
    const initial_routes = [_]protocol.RouteInput{
        route(1, 10, .{ .offset = 0, .length = text.len }, protocol.MethodMask.get, 0),
    };
    try session.replaceCatalog(
        catalogHeader(1, 10, 1, capabilities.len, initial_routes.len, text.len, true),
        &capabilities,
        &initial_routes,
        text,
    );

    const conflicting_routes = [_]protocol.RouteInput{
        route(2, 10, .{ .offset = 0, .length = text.len }, protocol.MethodMask.get, 0),
        route(
            3,
            10,
            .{ .offset = 0, .length = text.len },
            protocol.MethodMask.get,
            protocol.RouteFlags.exact_path,
        ),
    };
    try std.testing.expectError(
        error.InvalidArgument,
        session.replaceCatalog(
            catalogHeader(2, 20, 2, capabilities.len, conflicting_routes.len, text.len, true),
            &capabilities,
            &conflicting_routes,
            text,
        ),
    );

    const snapshot = session.snapshot();
    try std.testing.expectEqual(@as(u64, 1), snapshot.last_command_epoch);
    try std.testing.expectEqual(@as(u64, 1), snapshot.catalog_generation);
    const access = try session.admitRequest(
        accessInput(2, 20, 7, .loopback, .get, "/api/status"),
        "/api/status",
    );
    try std.testing.expect(toBool(access.allowed));
    try std.testing.expectEqual(@as(u64, 1), access.route_handle);
}

test "capability output is stable and buffer size is explicit" {
    var session = try session_module.Session.create(std.testing.allocator, testConfig());
    defer session.destroy();
    const text = "/z/a";
    const capabilities = [_]protocol.CapabilityInput{
        .{ .struct_size = @sizeOf(protocol.CapabilityInput), .flags = 3, .capability_handle = 9, .payload_handle = 90, .reserved = 0 },
        .{ .struct_size = @sizeOf(protocol.CapabilityInput), .flags = 3, .capability_handle = 2, .payload_handle = 20, .reserved = 0 },
    };
    const routes = [_]protocol.RouteInput{
        route(1, 9, .{ .offset = 0, .length = 2 }, protocol.MethodMask.get, 0),
        route(2, 2, .{ .offset = 2, .length = 2 }, protocol.MethodMask.get, 0),
    };
    try session.replaceCatalog(
        catalogHeader(1, 1, 1, capabilities.len, routes.len, text.len, true),
        &capabilities,
        &routes,
        text,
    );
    var too_small: [1]protocol.CapabilityOutput = undefined;
    try std.testing.expectError(error.BufferTooSmall, session.readCapabilities(&too_small));
    var output: [2]protocol.CapabilityOutput = undefined;
    try std.testing.expectEqual(@as(usize, 2), try session.readCapabilities(&output));
    try std.testing.expectEqual(@as(u64, 2), output[0].capability_handle);
    try std.testing.expectEqual(@as(u64, 9), output[1].capability_handle);
}

fn testConfig() protocol.Config {
    return .{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.Config),
        .generation = 1,
        .session_instance_low = 11,
        .session_instance_high = 12,
        .maximum_capability_count = 8,
        .maximum_route_count = 16,
        .maximum_model_count = 8,
        .maximum_model_alias_count = 16,
        .maximum_request_count = 8,
        .maximum_rate_bucket_count = 8,
        .maximum_lease_count = 8,
        .maximum_subscription_count = 8,
        .maximum_task_count = 8,
        .capability_index_capacity = 16,
        .model_index_capacity = 16,
        .alias_index_capacity = 32,
        .request_index_capacity = 16,
        .rate_bucket_index_capacity = 16,
        .lease_index_capacity = 16,
        .subscription_index_capacity = 16,
        .task_index_capacity = 16,
        .maximum_catalog_text_bytes = 512,
        .maximum_model_text_bytes = 512,
        .maximum_concurrent_model_tasks = 2,
        .maximum_requests_per_rate_window = 2,
        .maximum_inflight_requests_per_caller = 2,
        .retryable_task_outcome_mask = protocol.TaskOutcomeMask.provider_unavailable |
            protocol.TaskOutcomeMask.timeout |
            protocol.TaskOutcomeMask.transport_failure,
        .rate_window_milliseconds = 1_000,
        .request_timeout_milliseconds = 2_000,
        .lease_timeout_milliseconds = 10_000,
        .subscription_timeout_milliseconds = 10_000,
        .task_timeout_milliseconds = 5_000,
        .retry_delay_milliseconds = 1_000,
        .resident_byte_budget = std.math.maxInt(u64),
        .flags = protocol.ConfigFlags.loopback_only,
        .model_catalog_acquisition_interval_milliseconds = 5,
        .model_catalog_last_good_lifetime_milliseconds = 30_000,
        .model_catalog_acquisition_timeout_milliseconds = 2_000,
        .maximum_task_attempt_count = 3,
        .retryable_http_status_policy_mask = protocol.TaskHttpRetryPolicyMask.request_timeout |
            protocol.TaskHttpRetryPolicyMask.throttled |
            protocol.TaskHttpRetryPolicyMask.server_error,
    };
}

fn route(
    route_handle: u64,
    capability_handle: u64,
    path: protocol.TextSpan,
    method_mask: u32,
    flags: u32,
) protocol.RouteInput {
    return .{
        .struct_size = @sizeOf(protocol.RouteInput),
        .flags = flags,
        .route_handle = route_handle,
        .capability_handle = capability_handle,
        .path = path,
        .method_mask = method_mask,
        .reserved_u32 = 0,
        .payload_handle = route_handle + 1000,
        .reserved = 0,
    };
}

fn catalogHeader(
    epoch: u64,
    now: u64,
    generation: u64,
    capability_count: usize,
    route_count: usize,
    text_count: usize,
    enabled: bool,
) protocol.CatalogReplaceInput {
    return .{
        .struct_size = @sizeOf(protocol.CatalogReplaceInput),
        .service_enabled = @intFromBool(enabled),
        .command_epoch = epoch,
        .command_monotonic_milliseconds = now,
        .catalog_generation = generation,
        .capability_count = @intCast(capability_count),
        .route_count = @intCast(route_count),
        .text_byte_count = @intCast(text_count),
        .reserved_u32 = 0,
        .reserved = .{ 0, 0, 0 },
    };
}

fn accessInput(
    epoch: u64,
    now: u64,
    caller: u64,
    scope: protocol.RemoteScope,
    method: protocol.HttpMethod,
    path: []const u8,
) protocol.AccessInput {
    return .{
        .struct_size = @sizeOf(protocol.AccessInput),
        .flags = 0,
        .command_epoch = epoch,
        .command_monotonic_milliseconds = now,
        .caller_handle = caller,
        .remote_scope = @intFromEnum(scope),
        .method = @intFromEnum(method),
        .path = .{ .offset = 0, .length = @intCast(path.len) },
        .reserved = .{ 0, 0, 0 },
    };
}

fn requestComplete(epoch: u64, now: u64, handle: u64) protocol.RequestCompleteInput {
    return .{
        .struct_size = @sizeOf(protocol.RequestCompleteInput),
        .reserved_u32 = 0,
        .command_epoch = epoch,
        .command_monotonic_milliseconds = now,
        .request_handle = handle,
        .reserved = .{ 0, 0, 0 },
    };
}

fn installModels(session: *session_module.Session, epoch: u64, now: u64) !void {
    const text = "open-modelsecond-modelopen-model-loaded";
    const models = [_]protocol.ModelInput{
        model(41, 1, 101),
        model(42, 1, 102),
    };
    const aliases = [_]protocol.ModelAliasInput{
        alias(41, 0, 10),
        alias(42, 10, 12),
        alias(41, 22, 17),
    };
    const acquisition = try session.planModelAcquisition(
        modelAcquisitionPlan(epoch, now),
    );
    try std.testing.expectEqual(
        @intFromEnum(protocol.ModelAcquisitionPlanStatus.start),
        acquisition.status,
    );
    try session.replaceModels(
        modelHeader(
            epoch + 1,
            now + 1,
            1,
            models.len,
            aliases.len,
            text.len,
            acquisition.attempt_handle,
        ),
        &models,
        &aliases,
        text,
    );
}

fn model(handle: u64, provider: u64, payload: u64) protocol.ModelInput {
    return .{
        .struct_size = @sizeOf(protocol.ModelInput),
        .flags = protocol.ModelFlags.available,
        .model_handle = handle,
        .provider_handle = provider,
        .payload_handle = payload,
        .reserved = 0,
    };
}

fn alias(model_handle: u64, offset: u32, length: usize) protocol.ModelAliasInput {
    return .{
        .struct_size = @sizeOf(protocol.ModelAliasInput),
        .flags = 0,
        .model_handle = model_handle,
        .alias = .{ .offset = offset, .length = @intCast(length) },
        .reserved = .{ 0, 0 },
    };
}

fn modelHeader(
    epoch: u64,
    now: u64,
    generation: u64,
    model_count: usize,
    alias_count: usize,
    text_count: usize,
    acquisition_attempt_handle: u64,
) protocol.ModelReplaceInput {
    return .{
        .struct_size = @sizeOf(protocol.ModelReplaceInput),
        .reserved_u32 = 0,
        .command_epoch = epoch,
        .command_monotonic_milliseconds = now,
        .model_generation = generation,
        .model_count = @intCast(model_count),
        .alias_count = @intCast(alias_count),
        .text_byte_count = @intCast(text_count),
        .reserved_u32_2 = 0,
        .acquisition_attempt_handle = acquisition_attempt_handle,
        .reserved = .{ 0, 0 },
    };
}

fn modelAcquisitionPlan(
    epoch: u64,
    now: u64,
) protocol.ModelAcquisitionPlanInput {
    return .{
        .struct_size = @sizeOf(protocol.ModelAcquisitionPlanInput),
        .reserved_u32 = 0,
        .command_epoch = epoch,
        .command_monotonic_milliseconds = now,
        .reserved = .{ 0, 0, 0 },
    };
}

fn modelAcquisitionCompletion(
    epoch: u64,
    now: u64,
    attempt_handle: u64,
    status: protocol.ModelAcquisitionCompletionStatus,
) protocol.ModelAcquisitionCompletionInput {
    return .{
        .struct_size = @sizeOf(protocol.ModelAcquisitionCompletionInput),
        .status = @intFromEnum(status),
        .command_epoch = epoch,
        .command_monotonic_milliseconds = now,
        .attempt_handle = attempt_handle,
        .reserved = .{ 0, 0, 0 },
    };
}

fn modelResolve(epoch: u64, now: u64, value: []const u8) protocol.ModelResolveInput {
    return .{
        .struct_size = @sizeOf(protocol.ModelResolveInput),
        .reserved_u32 = 0,
        .command_epoch = epoch,
        .command_monotonic_milliseconds = now,
        .alias = .{ .offset = 0, .length = @intCast(value.len) },
        .reserved = .{ 0, 0, 0 },
    };
}

fn subscriptionInput(
    epoch: u64,
    now: u64,
    handle: u64,
    caller: u64,
    model_handle: u64,
    score: i64,
    timeout: u64,
) protocol.SubscriptionUpsertInput {
    return .{
        .struct_size = @sizeOf(protocol.SubscriptionUpsertInput),
        .flags = protocol.SubscriptionFlags.continuous_use,
        .command_epoch = epoch,
        .command_monotonic_milliseconds = now,
        .subscription_handle = handle,
        .caller_handle = caller,
        .model_handle = model_handle,
        .base_score = score,
        .requested_timeout_milliseconds = timeout,
        .reserved = .{ 0, 0 },
    };
}

fn taskInput(
    epoch: u64,
    now: u64,
    caller: u64,
    model_handle: u64,
    payload: u64,
    score: i64,
    kind: protocol.TaskKind,
) protocol.TaskEnqueueInput {
    return .{
        .struct_size = @sizeOf(protocol.TaskEnqueueInput),
        .flags = 0,
        .command_epoch = epoch,
        .command_monotonic_milliseconds = now,
        .caller_handle = caller,
        .model_handle = model_handle,
        .payload_handle = payload,
        .base_score = score,
        .kind = @intFromEnum(kind),
        .reserved_u32 = 0,
        .reserved = .{ 0, 0 },
    };
}

fn taskCompleteHttp(
    epoch: u64,
    now: u64,
    handle: u64,
    attempt: u32,
    status_code: u32,
) protocol.TaskCompletionInput {
    var input = taskComplete(epoch, now, handle, attempt, .http_response);
    input.http_status_code = status_code;
    return input;
}

fn taskPlan(epoch: u64, now: u64, maximum: u32) protocol.TaskPlanInput {
    return .{
        .struct_size = @sizeOf(protocol.TaskPlanInput),
        .maximum_output_count = maximum,
        .command_epoch = epoch,
        .command_monotonic_milliseconds = now,
        .reserved = .{ 0, 0, 0 },
    };
}

fn taskCancel(epoch: u64, now: u64, maximum: usize) protocol.TaskCancelInput {
    return .{
        .struct_size = @sizeOf(protocol.TaskCancelInput),
        .maximum_output_count = @intCast(maximum),
        .command_epoch = epoch,
        .command_monotonic_milliseconds = now,
        .reserved = .{ 0, 0, 0 },
    };
}

fn taskComplete(
    epoch: u64,
    now: u64,
    handle: u64,
    attempt: u32,
    outcome: protocol.TaskEffectOutcome,
) protocol.TaskCompletionInput {
    return .{
        .struct_size = @sizeOf(protocol.TaskCompletionInput),
        .outcome = @intFromEnum(outcome),
        .command_epoch = epoch,
        .command_monotonic_milliseconds = now,
        .task_handle = handle,
        .attempt = attempt,
        .http_status_code = 0,
        .reserved = .{ 0, 0 },
    };
}

fn toBool(value: u32) bool {
    return value != 0;
}
