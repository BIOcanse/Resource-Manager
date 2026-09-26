const std = @import("std");
const catalog = @import("catalog.zig");
const index = @import("index.zig");
const protocol = @import("protocol.zig");

pub const Error = error{
    InvalidArgument,
    InvalidState,
    CapacityExceeded,
    CounterExhausted,
    BufferTooSmall,
};

const RequestSlot = struct {
    active: bool = false,
    expired: bool = false,
    rate_limited: bool = false,
    handle: u64 = 0,
    caller_handle: u64 = 0,
    route_handle: u64 = 0,
    bucket_slot: usize = 0,
    expires_at: u64 = 0,
};

const RateBucketSlot = struct {
    active: bool = false,
    caller_handle: u64 = 0,
    route_handle: u64 = 0,
    window_started_at: u64 = 0,
    request_count: u32 = 0,
    inflight_count: u32 = 0,
};

const LeaseSlot = struct {
    active: bool = false,
    handle: u64 = 0,
    caller_handle: u64 = 0,
    model_handle: u64 = 0,
    expires_at: u64 = 0,
};

const SubscriptionSlot = struct {
    active: bool = false,
    handle: u64 = 0,
    caller_handle: u64 = 0,
    model_handle: u64 = 0,
    base_score: i64 = 0,
    expires_at: u64 = 0,
    flags: u32 = 0,
};

const TaskSlot = struct {
    active: bool = false,
    row: protocol.TaskOutput = std.mem.zeroes(protocol.TaskOutput),
    retry_at: u64 = 0,
};

pub const request_slot_size: u64 = @sizeOf(RequestSlot);
pub const rate_bucket_slot_size: u64 = @sizeOf(RateBucketSlot);
pub const lease_slot_size: u64 = @sizeOf(LeaseSlot);
pub const subscription_slot_size: u64 = @sizeOf(SubscriptionSlot);
pub const task_slot_size: u64 = @sizeOf(TaskSlot);

pub const RuntimeState = struct {
    requests: []RequestSlot,
    rate_buckets: []RateBucketSlot,
    leases: []LeaseSlot,
    subscriptions: []SubscriptionSlot,
    tasks: []TaskSlot,
    request_index: []index.HandleEntry,
    rate_bucket_index: []index.PairEntry,
    lease_index: []index.HandleEntry,
    subscription_index: []index.HandleEntry,
    task_index: []index.HandleEntry,
    request_count: usize = 0,
    rate_bucket_count: usize = 0,
    lease_count: usize = 0,
    subscription_count: usize = 0,
    task_count: usize = 0,
    next_request_handle: u64 = 1,
    next_lease_handle: u64 = 1,
    next_task_handle: u64 = 1,
    next_model_acquisition_attempt_handle: u64 = 1,
    model_acquisition_attempt_handle: u64 = 0,
    model_acquisition_started_at: u64 = 0,
    model_acquisition_deadline: u64 = 0,
    model_generation_at_acquisition_start: u64 = 0,
    next_model_acquisition_due: u64 = 0,
    model_catalog_usable_until: u64 = 0,

    pub fn init(allocator: std.mem.Allocator, config: protocol.Config) !RuntimeState {
        const requests = try allocator.alloc(RequestSlot, config.maximum_request_count);
        errdefer allocator.free(requests);
        const rate_buckets = try allocator.alloc(RateBucketSlot, config.maximum_rate_bucket_count);
        errdefer allocator.free(rate_buckets);
        const leases = try allocator.alloc(LeaseSlot, config.maximum_lease_count);
        errdefer allocator.free(leases);
        const subscriptions = try allocator.alloc(SubscriptionSlot, config.maximum_subscription_count);
        errdefer allocator.free(subscriptions);
        const tasks = try allocator.alloc(TaskSlot, config.maximum_task_count);
        errdefer allocator.free(tasks);
        const request_index = try allocator.alloc(index.HandleEntry, config.request_index_capacity);
        errdefer allocator.free(request_index);
        const rate_bucket_index = try allocator.alloc(index.PairEntry, config.rate_bucket_index_capacity);
        errdefer allocator.free(rate_bucket_index);
        const lease_index = try allocator.alloc(index.HandleEntry, config.lease_index_capacity);
        errdefer allocator.free(lease_index);
        const subscription_index = try allocator.alloc(index.HandleEntry, config.subscription_index_capacity);
        errdefer allocator.free(subscription_index);
        const task_index = try allocator.alloc(index.HandleEntry, config.task_index_capacity);
        errdefer allocator.free(task_index);

        @memset(requests, .{});
        @memset(rate_buckets, .{});
        @memset(leases, .{});
        @memset(subscriptions, .{});
        @memset(tasks, .{});
        index.clear(index.HandleEntry, request_index);
        index.clear(index.PairEntry, rate_bucket_index);
        index.clear(index.HandleEntry, lease_index);
        index.clear(index.HandleEntry, subscription_index);
        index.clear(index.HandleEntry, task_index);
        return .{
            .requests = requests,
            .rate_buckets = rate_buckets,
            .leases = leases,
            .subscriptions = subscriptions,
            .tasks = tasks,
            .request_index = request_index,
            .rate_bucket_index = rate_bucket_index,
            .lease_index = lease_index,
            .subscription_index = subscription_index,
            .task_index = task_index,
        };
    }

    pub fn deinit(self: *RuntimeState, allocator: std.mem.Allocator) void {
        allocator.free(self.requests);
        allocator.free(self.rate_buckets);
        allocator.free(self.leases);
        allocator.free(self.subscriptions);
        allocator.free(self.tasks);
        allocator.free(self.request_index);
        allocator.free(self.rate_bucket_index);
        allocator.free(self.lease_index);
        allocator.free(self.subscription_index);
        allocator.free(self.task_index);
        self.* = undefined;
    }

    pub fn reset(self: *RuntimeState) void {
        @memset(self.requests, .{});
        @memset(self.rate_buckets, .{});
        @memset(self.leases, .{});
        @memset(self.subscriptions, .{});
        @memset(self.tasks, .{});
        index.clear(index.HandleEntry, self.request_index);
        index.clear(index.PairEntry, self.rate_bucket_index);
        index.clear(index.HandleEntry, self.lease_index);
        index.clear(index.HandleEntry, self.subscription_index);
        index.clear(index.HandleEntry, self.task_index);
        self.request_count = 0;
        self.rate_bucket_count = 0;
        self.lease_count = 0;
        self.subscription_count = 0;
        self.task_count = 0;
        self.model_acquisition_attempt_handle = 0;
        self.model_acquisition_started_at = 0;
        self.model_acquisition_deadline = 0;
        self.model_generation_at_acquisition_start = 0;
        self.next_model_acquisition_due = 0;
        self.model_catalog_usable_until = 0;
    }

    pub fn expire(self: *RuntimeState, config: protocol.Config, now: u64) void {
        for (self.requests, 0..) |slot, slot_index| {
            if (slot.active and !slot.expired and slot.expires_at <= now) {
                self.markRequestExpired(slot_index);
            }
        }
        for (self.leases, 0..) |slot, slot_index| {
            if (slot.active and slot.expires_at <= now) self.removeLease(slot_index);
        }
        for (self.subscriptions, 0..) |slot, slot_index| {
            if (slot.active and slot.expires_at <= now) self.releaseSubscriptionSlot(slot_index);
        }
        if (self.model_acquisition_attempt_handle != 0 and
            self.model_acquisition_deadline <= now)
        {
            self.clearModelAcquisition();
            self.next_model_acquisition_due = saturatingAdd(
                now,
                config.retry_delay_milliseconds,
            );
        }
        self.reclaimBuckets(config, now);
    }

    pub fn planModelAcquisition(
        self: *RuntimeState,
        config: protocol.Config,
        input: protocol.ModelAcquisitionPlanInput,
        model_generation: u64,
    ) Error!protocol.ModelAcquisitionPlanOutput {
        var output = std.mem.zeroes(protocol.ModelAcquisitionPlanOutput);
        output.struct_size = @sizeOf(protocol.ModelAcquisitionPlanOutput);
        if (self.model_acquisition_attempt_handle != 0) {
            output.status = @intFromEnum(protocol.ModelAcquisitionPlanStatus.running);
            output.attempt_handle = self.model_acquisition_attempt_handle;
            output.started_at_monotonic_milliseconds = self.model_acquisition_started_at;
            output.deadline_monotonic_milliseconds = self.model_acquisition_deadline;
            output.next_wake_monotonic_milliseconds = self.model_acquisition_deadline;
            output.model_generation_at_start = self.model_generation_at_acquisition_start;
            return output;
        }
        if (self.next_model_acquisition_due != 0 and
            input.command_monotonic_milliseconds < self.next_model_acquisition_due)
        {
            output.status = @intFromEnum(protocol.ModelAcquisitionPlanStatus.not_due);
            output.next_wake_monotonic_milliseconds = self.next_model_acquisition_due;
            output.model_generation_at_start = model_generation;
            return output;
        }

        const attempt_handle = try nextHandle(
            &self.next_model_acquisition_attempt_handle,
        );
        self.model_acquisition_attempt_handle = attempt_handle;
        self.model_acquisition_started_at = input.command_monotonic_milliseconds;
        self.model_acquisition_deadline = saturatingAdd(
            input.command_monotonic_milliseconds,
            config.model_catalog_acquisition_timeout_milliseconds,
        );
        self.model_generation_at_acquisition_start = model_generation;
        output.status = @intFromEnum(protocol.ModelAcquisitionPlanStatus.start);
        output.attempt_handle = attempt_handle;
        output.started_at_monotonic_milliseconds = self.model_acquisition_started_at;
        output.deadline_monotonic_milliseconds = self.model_acquisition_deadline;
        output.next_wake_monotonic_milliseconds = self.model_acquisition_deadline;
        output.model_generation_at_start = model_generation;
        return output;
    }

    pub fn completeModelAcquisitionFailure(
        self: *RuntimeState,
        config: protocol.Config,
        input: protocol.ModelAcquisitionCompletionInput,
    ) Error!void {
        if (self.model_acquisition_attempt_handle == 0 or
            input.attempt_handle != self.model_acquisition_attempt_handle or
            input.command_monotonic_milliseconds > self.model_acquisition_deadline)
        {
            return error.InvalidState;
        }
        const status: protocol.ModelAcquisitionCompletionStatus = @enumFromInt(input.status);
        switch (status) {
            .unavailable, .failed => {
                self.next_model_acquisition_due = saturatingAdd(
                    input.command_monotonic_milliseconds,
                    config.retry_delay_milliseconds,
                );
            },
            .invalid => return error.InvalidArgument,
        }
        self.clearModelAcquisition();
    }

    pub fn completeModelAcquisitionSuccess(
        self: *RuntimeState,
        config: protocol.Config,
        attempt_handle: u64,
        now: u64,
        model_generation: u64,
    ) Error!void {
        if (!self.acceptsModelReplacement(attempt_handle, now) or
            model_generation <= self.model_generation_at_acquisition_start)
        {
            return error.InvalidState;
        }
        self.model_catalog_usable_until = saturatingAdd(
            now,
            config.model_catalog_last_good_lifetime_milliseconds,
        );
        self.next_model_acquisition_due = saturatingAdd(
            now,
            config.model_catalog_acquisition_interval_milliseconds,
        );
        self.clearModelAcquisition();
    }

    pub fn acceptsModelReplacement(
        self: *const RuntimeState,
        attempt_handle: u64,
        now: u64,
    ) bool {
        return attempt_handle != 0 and
            attempt_handle == self.model_acquisition_attempt_handle and
            now <= self.model_acquisition_deadline;
    }

    pub fn modelCatalogUsable(self: *const RuntimeState, now: u64) bool {
        return self.model_catalog_usable_until != 0 and
            now <= self.model_catalog_usable_until;
    }

    pub fn admitRequest(
        self: *RuntimeState,
        config: protocol.Config,
        active_catalog: *const catalog.CatalogBank,
        input: protocol.AccessInput,
        path: []const u8,
    ) Error!protocol.AccessOutput {
        var output = std.mem.zeroes(protocol.AccessOutput);
        output.struct_size = @sizeOf(protocol.AccessOutput);
        output.catalog_generation = active_catalog.generation;

        if (config.flags & protocol.ConfigFlags.loopback_only != 0 and
            input.remote_scope != @intFromEnum(protocol.RemoteScope.loopback))
        {
            return denied(output, 403, .remote_forbidden);
        }
        if (!active_catalog.service_enabled) return denied(output, 404, .service_disabled);

        const route = active_catalog.route(path) orelse
            return denied(output, 404, .route_not_found);
        output.route_handle = route.route_handle;
        output.capability_handle = route.capability_handle;

        if (route.flags & protocol.RouteFlags.catalog_route == 0) {
            const capability = active_catalog.capability(route.capability_handle) orelse
                return denied(output, 404, .capability_disabled);
            const required = protocol.CapabilityFlags.enabled | protocol.CapabilityFlags.available;
            if (capability.flags & required != required) {
                return denied(output, 404, .capability_disabled);
            }
        }

        const method: protocol.HttpMethod = @enumFromInt(input.method);
        const method_mask = protocol.methodMask(method);
        if (method_mask == 0 or route.method_mask & method_mask == 0) {
            return denied(output, 405, .method_not_supported);
        }

        const bypass_rate_limit =
            route.flags & protocol.RouteFlags.bypass_rate_limit != 0;
        var bucket_slot: usize = 0;
        if (!bypass_rate_limit) {
            bucket_slot = self.getOrCreateBucket(
                config,
                input.caller_handle,
                route.route_handle,
                input.command_monotonic_milliseconds,
            ) catch |err| switch (err) {
                error.CapacityExceeded => return denied(output, 503, .coordinator_capacity),
                else => return err,
            };
            const bucket = &self.rate_buckets[bucket_slot];
            if (bucket.request_count >= config.maximum_requests_per_rate_window) {
                return denied(output, 429, .rate_limited);
            }
            if (bucket.inflight_count >= config.maximum_inflight_requests_per_caller) {
                return denied(output, 429, .caller_inflight_limit);
            }
        }

        const slot = findFree(RequestSlot, self.requests) orelse
            return denied(output, 503, .coordinator_capacity);
        const handle = try nextHandle(&self.next_request_handle);
        if (!index.insertHandle(self.request_index, handle, slot)) return error.CapacityExceeded;
        const expires_at = saturatingAdd(
            input.command_monotonic_milliseconds,
            config.request_timeout_milliseconds,
        );
        self.requests[slot] = .{
            .active = true,
            .rate_limited = !bypass_rate_limit,
            .handle = handle,
            .caller_handle = input.caller_handle,
            .route_handle = route.route_handle,
            .bucket_slot = bucket_slot,
            .expires_at = expires_at,
        };
        self.request_count += 1;
        if (!bypass_rate_limit) {
            self.rate_buckets[bucket_slot].request_count += 1;
            self.rate_buckets[bucket_slot].inflight_count += 1;
        }
        output.allowed = 1;
        output.status_code = 200;
        output.reason = @intFromEnum(protocol.AccessReason.allowed);
        output.request_handle = handle;
        output.expires_at_monotonic_milliseconds = expires_at;
        return output;
    }

    pub fn completeRequest(
        self: *RuntimeState,
        request_handle: u64,
    ) Error!protocol.RequestCompleteOutput {
        const slot = index.findHandle(self.request_index, request_handle) orelse
            return error.InvalidState;
        if (slot >= self.requests.len or !self.requests[slot].active) return error.InvalidState;
        const disposition: protocol.RequestCompletionDisposition =
            if (self.requests[slot].expired) .expired else .completed;
        self.removeRequest(slot);
        return .{
            .struct_size = @sizeOf(protocol.RequestCompleteOutput),
            .disposition = @intFromEnum(disposition),
            .request_handle = request_handle,
            .reserved = .{ 0, 0, 0 },
        };
    }

    pub fn beginLease(
        self: *RuntimeState,
        config: protocol.Config,
        models: *const catalog.ModelBank,
        input: protocol.LeaseBeginInput,
    ) Error!protocol.LeaseOutput {
        if (input.caller_handle == 0 or
            input.model_handle == 0 or
            !models.containsModel(input.model_handle) or
            input.requested_timeout_milliseconds == 0 or
            input.requested_timeout_milliseconds > config.lease_timeout_milliseconds)
        {
            return error.InvalidArgument;
        }
        const slot = findFree(LeaseSlot, self.leases) orelse return error.CapacityExceeded;
        const handle = try nextHandle(&self.next_lease_handle);
        if (!index.insertHandle(self.lease_index, handle, slot)) return error.CapacityExceeded;
        const expires_at = saturatingAdd(
            input.command_monotonic_milliseconds,
            input.requested_timeout_milliseconds,
        );
        self.leases[slot] = .{
            .active = true,
            .handle = handle,
            .caller_handle = input.caller_handle,
            .model_handle = input.model_handle,
            .expires_at = expires_at,
        };
        self.lease_count += 1;
        return .{
            .struct_size = @sizeOf(protocol.LeaseOutput),
            .reserved_u32 = 0,
            .lease_handle = handle,
            .model_handle = input.model_handle,
            .caller_handle = input.caller_handle,
            .expires_at_monotonic_milliseconds = expires_at,
            .reserved = .{ 0, 0 },
        };
    }

    pub fn endLease(self: *RuntimeState, lease_handle: u64) Error!void {
        const slot = index.findHandle(self.lease_index, lease_handle) orelse
            return error.InvalidState;
        if (slot >= self.leases.len or !self.leases[slot].active) return error.InvalidState;
        self.removeLease(slot);
    }

    pub fn upsertSubscription(
        self: *RuntimeState,
        config: protocol.Config,
        models: *const catalog.ModelBank,
        input: protocol.SubscriptionUpsertInput,
    ) Error!protocol.SubscriptionOutput {
        if (input.subscription_handle == 0 or
            input.caller_handle == 0 or
            input.flags & ~protocol.SubscriptionFlags.known != 0 or
            input.requested_timeout_milliseconds == 0 or
            input.requested_timeout_milliseconds > config.subscription_timeout_milliseconds or
            (input.model_handle != 0 and !models.containsModel(input.model_handle)))
        {
            return error.InvalidArgument;
        }
        const expires_at = saturatingAdd(
            input.command_monotonic_milliseconds,
            input.requested_timeout_milliseconds,
        );
        const slot = if (index.findHandle(self.subscription_index, input.subscription_handle)) |existing| blk: {
            if (existing >= self.subscriptions.len or
                !self.subscriptions[existing].active or
                self.subscriptions[existing].caller_handle != input.caller_handle)
            {
                return error.InvalidState;
            }
            break :blk existing;
        } else blk: {
            const available = findFree(SubscriptionSlot, self.subscriptions) orelse
                return error.CapacityExceeded;
            if (!index.insertHandle(self.subscription_index, input.subscription_handle, available)) {
                return error.CapacityExceeded;
            }
            self.subscription_count += 1;
            break :blk available;
        };
        self.subscriptions[slot] = .{
            .active = true,
            .handle = input.subscription_handle,
            .caller_handle = input.caller_handle,
            .model_handle = input.model_handle,
            .base_score = input.base_score,
            .expires_at = expires_at,
            .flags = input.flags,
        };
        return .{
            .struct_size = @sizeOf(protocol.SubscriptionOutput),
            .flags = input.flags,
            .subscription_handle = input.subscription_handle,
            .caller_handle = input.caller_handle,
            .model_handle = input.model_handle,
            .base_score = input.base_score,
            .expires_at_monotonic_milliseconds = expires_at,
            .reserved = .{ 0, 0 },
        };
    }

    pub fn removeSubscription(self: *RuntimeState, subscription_handle: u64) Error!void {
        const slot = index.findHandle(self.subscription_index, subscription_handle) orelse
            return error.InvalidState;
        if (slot >= self.subscriptions.len or !self.subscriptions[slot].active) {
            return error.InvalidState;
        }
        self.releaseSubscriptionSlot(slot);
    }

    pub fn enqueueTask(
        self: *RuntimeState,
        models: *const catalog.ModelBank,
        input: protocol.TaskEnqueueInput,
    ) Error!protocol.TaskOutput {
        const kind: protocol.TaskKind = @enumFromInt(input.kind);
        if (kind == .invalid or
            input.flags & ~protocol.TaskFlags.known != 0 or
            input.caller_handle == 0 or
            input.model_handle == 0 or
            input.payload_handle == 0 or
            !models.containsModel(input.model_handle))
        {
            return error.InvalidArgument;
        }
        const slot = findFree(TaskSlot, self.tasks) orelse return error.CapacityExceeded;
        const handle = try nextHandle(&self.next_task_handle);
        if (!index.insertHandle(self.task_index, handle, slot)) return error.CapacityExceeded;
        const row = protocol.TaskOutput{
            .struct_size = @sizeOf(protocol.TaskOutput),
            .flags = input.flags,
            .task_handle = handle,
            .caller_handle = input.caller_handle,
            .model_handle = input.model_handle,
            .payload_handle = input.payload_handle,
            .base_score = input.base_score,
            .enqueued_at_monotonic_milliseconds = input.command_monotonic_milliseconds,
            .deadline_monotonic_milliseconds = 0,
            .kind = input.kind,
            .state = @intFromEnum(protocol.TaskState.queued),
            .attempt = 0,
            .reserved_u32 = 0,
            .reserved = .{ 0, 0 },
        };
        self.tasks[slot] = .{ .active = true, .row = row };
        self.task_count += 1;
        return row;
    }

    pub fn planTasks(
        self: *RuntimeState,
        config: protocol.Config,
        input: protocol.TaskPlanInput,
        output: []protocol.TaskOutput,
    ) Error!protocol.TaskPlanOutput {
        try self.validateTaskPlan(config, input, output.len);
        var running_count = self.runningTaskCount();
        var output_count: usize = 0;
        while (output_count < input.maximum_output_count and
            running_count < config.maximum_concurrent_model_tasks)
        {
            const slot = self.selectReadyTask(input.command_monotonic_milliseconds) orelse break;
            const task = &self.tasks[slot];
            task.row.state = @intFromEnum(protocol.TaskState.running);
            task.row.deadline_monotonic_milliseconds = saturatingAdd(
                input.command_monotonic_milliseconds,
                config.task_timeout_milliseconds,
            );
            task.row.attempt += 1;
            task.retry_at = 0;
            output[output_count] = task.row;
            output_count += 1;
            running_count += 1;
        }

        const next_wake = self.nextTaskWake();
        var flags: u64 = 0;
        if (next_wake != 0) flags |= protocol.TaskPlanFlags.next_wake_valid;
        if (self.hasReadyTask(input.command_monotonic_milliseconds)) {
            flags |= protocol.TaskPlanFlags.more_ready;
        }
        return .{
            .struct_size = @sizeOf(protocol.TaskPlanOutput),
            .output_count = @intCast(output_count),
            .next_wake_monotonic_milliseconds = next_wake,
            .flags = flags,
            .reserved = .{ 0, 0, 0 },
        };
    }

    pub fn validateTaskPlan(
        self: *const RuntimeState,
        config: protocol.Config,
        input: protocol.TaskPlanInput,
        output_capacity: usize,
    ) Error!void {
        if (input.maximum_output_count == 0 or
            input.maximum_output_count > output_capacity or
            input.maximum_output_count > config.maximum_concurrent_model_tasks or
            !protocol.allZero([3]u64, input.reserved))
        {
            return error.InvalidArgument;
        }
        if (self.hasExhaustedReadyTaskAt(input.command_monotonic_milliseconds)) {
            return error.CounterExhausted;
        }
    }

    pub fn completeTask(
        self: *RuntimeState,
        config: protocol.Config,
        input: protocol.TaskCompletionInput,
    ) Error!protocol.TaskCompletionOutput {
        const outcome: protocol.TaskEffectOutcome = @enumFromInt(input.outcome);
        if (outcome == .invalid or
            input.attempt == 0 or
            !validHttpStatus(outcome, input.http_status_code) or
            !protocol.allZero([2]u64, input.reserved))
        {
            return error.InvalidArgument;
        }
        const slot = index.findHandle(self.task_index, input.task_handle) orelse
            return error.InvalidState;
        if (slot >= self.tasks.len or
            !self.tasks[slot].active or
            self.tasks[slot].row.state != @intFromEnum(protocol.TaskState.running) or
            self.tasks[slot].row.attempt != input.attempt)
        {
            return error.InvalidState;
        }
        var disposition = protocol.TaskCompletionDisposition.terminal_failure;
        var next_wake: u64 = 0;
        switch (outcome) {
            .succeeded => {
                self.next_model_acquisition_due = input.command_monotonic_milliseconds;
                self.removeTask(slot);
                disposition = .succeeded;
            },
            else => {
                const retryable = input.attempt < config.maximum_task_attempt_count and
                    shouldRetryTaskOutcome(config, outcome, input.http_status_code);
                if (retryable) {
                    self.tasks[slot].row.state = @intFromEnum(protocol.TaskState.queued);
                    self.tasks[slot].row.deadline_monotonic_milliseconds = 0;
                    self.tasks[slot].retry_at = saturatingAdd(
                        input.command_monotonic_milliseconds,
                        config.retry_delay_milliseconds,
                    );
                    disposition = .retry_scheduled;
                    next_wake = self.tasks[slot].retry_at;
                } else {
                    self.removeTask(slot);
                }
            },
        }
        return .{
            .struct_size = @sizeOf(protocol.TaskCompletionOutput),
            .disposition = @intFromEnum(disposition),
            .task_handle = input.task_handle,
            .attempt = input.attempt,
            .reserved_u32 = 0,
            .next_wake_monotonic_milliseconds = next_wake,
            .reserved = .{ 0, 0 },
        };
    }

    pub fn cancelQueuedTasks(
        self: *RuntimeState,
        input: protocol.TaskCancelInput,
        output: []protocol.TaskOutput,
    ) Error!protocol.TaskCancelOutput {
        try self.validateTaskCancellation(input, output.len);

        var output_count: usize = 0;
        for (self.tasks, 0..) |slot, slot_index| {
            if (!slot.active) continue;
            if (slot.row.state != @intFromEnum(protocol.TaskState.queued)) {
                return error.InvalidState;
            }
            output[output_count] = slot.row;
            output_count += 1;
            self.removeTask(slot_index);
        }
        return .{
            .struct_size = @sizeOf(protocol.TaskCancelOutput),
            .output_count = @intCast(output_count),
            .remaining_task_count = @intCast(self.task_count),
            .reserved_u32 = 0,
            .reserved = .{ 0, 0, 0, 0 },
        };
    }

    pub fn validateTaskCancellation(
        self: *const RuntimeState,
        input: protocol.TaskCancelInput,
        output_capacity: usize,
    ) Error!void {
        if (self.runningTaskCount() != 0) return error.InvalidState;
        const queued_count = self.queuedTaskCount();
        if (queued_count > input.maximum_output_count or queued_count > output_capacity) {
            return error.BufferTooSmall;
        }
    }

    pub fn snapshot(
        self: *const RuntimeState,
        config: protocol.Config,
        last_command_epoch: u64,
        last_command_monotonic: u64,
        active_catalog: *const catalog.CatalogBank,
        active_models: *const catalog.ModelBank,
    ) protocol.Snapshot {
        const next_wake = self.nextWake();
        var flags: u64 = 0;
        if (active_catalog.service_enabled) flags |= protocol.SnapshotFlags.service_enabled;
        if (next_wake != 0) flags |= protocol.SnapshotFlags.next_wake_valid;
        if (self.modelCatalogUsable(last_command_monotonic)) {
            flags |= protocol.SnapshotFlags.model_catalog_usable;
        }
        if (self.model_acquisition_attempt_handle != 0) {
            flags |= protocol.SnapshotFlags.model_acquisition_running;
        }
        return .{
            .struct_size = @sizeOf(protocol.Snapshot),
            .reserved_u32 = 0,
            .generation = config.generation,
            .session_instance_low = config.session_instance_low,
            .session_instance_high = config.session_instance_high,
            .last_command_epoch = last_command_epoch,
            .last_command_monotonic_milliseconds = last_command_monotonic,
            .catalog_generation = active_catalog.generation,
            .model_generation = active_models.generation,
            .capability_count = @intCast(active_catalog.capability_count),
            .route_count = @intCast(active_catalog.route_count),
            .model_count = @intCast(active_models.model_count),
            .alias_count = @intCast(active_models.alias_count),
            .request_count = @intCast(self.request_count),
            .rate_bucket_count = @intCast(self.rate_bucket_count),
            .lease_count = @intCast(self.lease_count),
            .subscription_count = @intCast(self.subscription_count),
            .queued_task_count = @intCast(self.queuedTaskCount()),
            .running_task_count = @intCast(self.runningTaskCount()),
            .next_wake_monotonic_milliseconds = next_wake,
            .flags = flags,
            .model_acquisition_attempt_handle = self.model_acquisition_attempt_handle,
            .model_catalog_usable_until_monotonic_milliseconds = self.model_catalog_usable_until,
            .next_model_acquisition_due_monotonic_milliseconds = self.next_model_acquisition_due,
        };
    }

    pub fn canReplaceModelsAt(
        self: *const RuntimeState,
        replacement: *const catalog.ModelBank,
        now: u64,
    ) bool {
        for (self.leases) |slot| {
            if (slot.active and
                slot.expires_at > now and
                !replacement.containsModel(slot.model_handle))
            {
                return false;
            }
        }
        for (self.subscriptions) |slot| {
            if (slot.active and
                slot.expires_at > now and
                slot.model_handle != 0 and
                !replacement.containsModel(slot.model_handle))
            {
                return false;
            }
        }
        for (self.tasks) |slot| {
            if (slot.active and !replacement.containsModel(slot.row.model_handle)) return false;
        }
        return true;
    }

    fn getOrCreateBucket(
        self: *RuntimeState,
        config: protocol.Config,
        caller_handle: u64,
        route_handle: u64,
        now: u64,
    ) Error!usize {
        if (index.findPair(self.rate_bucket_index, caller_handle, route_handle)) |slot| {
            if (slot >= self.rate_buckets.len or !self.rate_buckets[slot].active) {
                return error.InvalidState;
            }
            const bucket = &self.rate_buckets[slot];
            if (elapsedAtLeast(now, bucket.window_started_at, config.rate_window_milliseconds)) {
                bucket.window_started_at = now;
                bucket.request_count = 0;
            }
            return slot;
        }
        const slot = findFree(RateBucketSlot, self.rate_buckets) orelse
            self.findReusableBucket(config, now) orelse return error.CapacityExceeded;
        if (self.rate_buckets[slot].active) {
            _ = index.removePair(
                self.rate_bucket_index,
                self.rate_buckets[slot].caller_handle,
                self.rate_buckets[slot].route_handle,
            );
        } else {
            self.rate_bucket_count += 1;
        }
        if (!index.insertPair(self.rate_bucket_index, caller_handle, route_handle, slot)) {
            return error.CapacityExceeded;
        }
        self.rate_buckets[slot] = .{
            .active = true,
            .caller_handle = caller_handle,
            .route_handle = route_handle,
            .window_started_at = now,
        };
        return slot;
    }

    fn findReusableBucket(
        self: *const RuntimeState,
        config: protocol.Config,
        now: u64,
    ) ?usize {
        for (self.rate_buckets, 0..) |slot, slot_index| {
            if (slot.active and
                slot.inflight_count == 0 and
                elapsedAtLeast(now, slot.window_started_at, config.rate_window_milliseconds))
            {
                return slot_index;
            }
        }
        return null;
    }

    fn reclaimBuckets(self: *RuntimeState, config: protocol.Config, now: u64) void {
        for (self.rate_buckets, 0..) |slot, slot_index| {
            if (!slot.active or slot.inflight_count != 0) continue;
            if (!elapsedAtLeast(now, slot.window_started_at, config.rate_window_milliseconds)) continue;
            _ = index.removePair(self.rate_bucket_index, slot.caller_handle, slot.route_handle);
            self.rate_buckets[slot_index] = .{};
            self.rate_bucket_count -= 1;
        }
    }

    fn removeRequest(self: *RuntimeState, slot: usize) void {
        const request = self.requests[slot];
        if (!request.active) return;
        self.releaseRequestAccounting(slot);
        _ = index.removeHandle(self.request_index, request.handle);
        self.requests[slot] = .{};
        self.request_count -= 1;
    }

    fn markRequestExpired(self: *RuntimeState, slot: usize) void {
        if (!self.requests[slot].active or self.requests[slot].expired) return;
        self.releaseRequestAccounting(slot);
        self.requests[slot].expired = true;
    }

    fn releaseRequestAccounting(self: *RuntimeState, slot: usize) void {
        const request = &self.requests[slot];
        if (request.rate_limited and
            request.bucket_slot < self.rate_buckets.len and
            self.rate_buckets[request.bucket_slot].active and
            self.rate_buckets[request.bucket_slot].caller_handle == request.caller_handle and
            self.rate_buckets[request.bucket_slot].route_handle == request.route_handle and
            self.rate_buckets[request.bucket_slot].inflight_count != 0)
        {
            self.rate_buckets[request.bucket_slot].inflight_count -= 1;
        }
        request.rate_limited = false;
    }

    fn removeLease(self: *RuntimeState, slot: usize) void {
        const lease = self.leases[slot];
        if (!lease.active) return;
        _ = index.removeHandle(self.lease_index, lease.handle);
        self.leases[slot] = .{};
        self.lease_count -= 1;
    }

    fn clearModelAcquisition(self: *RuntimeState) void {
        self.model_acquisition_attempt_handle = 0;
        self.model_acquisition_started_at = 0;
        self.model_acquisition_deadline = 0;
        self.model_generation_at_acquisition_start = 0;
    }

    fn releaseSubscriptionSlot(self: *RuntimeState, slot: usize) void {
        const subscription = self.subscriptions[slot];
        if (!subscription.active) return;
        _ = index.removeHandle(self.subscription_index, subscription.handle);
        self.subscriptions[slot] = .{};
        self.subscription_count -= 1;
    }

    fn removeTask(self: *RuntimeState, slot: usize) void {
        const task = self.tasks[slot];
        if (!task.active) return;
        _ = index.removeHandle(self.task_index, task.row.task_handle);
        self.tasks[slot] = .{};
        self.task_count -= 1;
    }

    fn modelInUse(self: *const RuntimeState, model_handle: u64) bool {
        for (self.leases) |slot| {
            if (slot.active and slot.model_handle == model_handle) return true;
        }
        for (self.subscriptions) |slot| {
            if (!slot.active) continue;
            if (slot.model_handle == 0 or slot.model_handle == model_handle) return true;
        }
        return false;
    }

    fn modelInUseAt(self: *const RuntimeState, model_handle: u64, now: u64) bool {
        for (self.leases) |slot| {
            if (slot.active and slot.expires_at > now and slot.model_handle == model_handle) {
                return true;
            }
        }
        for (self.subscriptions) |slot| {
            if (!slot.active or slot.expires_at <= now) continue;
            if (slot.model_handle == 0 or slot.model_handle == model_handle) return true;
        }
        return false;
    }

    fn selectReadyTask(self: *const RuntimeState, now: u64) ?usize {
        var best: ?usize = null;
        for (self.tasks, 0..) |slot, slot_index| {
            if (!slot.active or
                slot.row.state != @intFromEnum(protocol.TaskState.queued) or
                slot.retry_at > now)
            {
                continue;
            }
            if (slot.row.kind == @intFromEnum(protocol.TaskKind.unload) and
                self.modelInUse(slot.row.model_handle))
            {
                continue;
            }
            if (best == null or taskComesBefore(slot.row, self.tasks[best.?].row)) best = slot_index;
        }
        return best;
    }

    fn hasReadyTask(self: *const RuntimeState, now: u64) bool {
        return self.selectReadyTask(now) != null;
    }

    fn hasExhaustedReadyTaskAt(self: *const RuntimeState, now: u64) bool {
        for (self.tasks) |slot| {
            if (!slot.active or
                slot.row.state != @intFromEnum(protocol.TaskState.queued) or
                slot.retry_at > now or
                slot.row.attempt != std.math.maxInt(u32))
            {
                continue;
            }
            if (slot.row.kind == @intFromEnum(protocol.TaskKind.unload) and
                self.modelInUseAt(slot.row.model_handle, now))
            {
                continue;
            }
            return true;
        }
        return false;
    }

    fn nextWake(self: *const RuntimeState) u64 {
        var next = self.nextTaskWake();
        if (self.model_acquisition_attempt_handle != 0) {
            setMinimum(&next, self.model_acquisition_deadline);
        } else if (self.next_model_acquisition_due != 0) {
            setMinimum(&next, self.next_model_acquisition_due);
        }
        return next;
    }

    fn nextTaskWake(self: *const RuntimeState) u64 {
        var next: u64 = 0;
        for (self.requests) |slot| {
            if (slot.active and !slot.expired) setMinimum(&next, slot.expires_at);
        }
        for (self.leases) |slot| if (slot.active) setMinimum(&next, slot.expires_at);
        for (self.subscriptions) |slot| if (slot.active) setMinimum(&next, slot.expires_at);
        for (self.tasks) |slot| {
            if (!slot.active) continue;
            if (slot.row.state == @intFromEnum(protocol.TaskState.queued) and slot.retry_at != 0) {
                setMinimum(&next, slot.retry_at);
            }
        }
        return next;
    }

    fn queuedTaskCount(self: *const RuntimeState) usize {
        var count: usize = 0;
        for (self.tasks) |slot| {
            if (slot.active and slot.row.state == @intFromEnum(protocol.TaskState.queued)) count += 1;
        }
        return count;
    }

    fn validHttpStatus(outcome: protocol.TaskEffectOutcome, status_code: u32) bool {
        return if (outcome == .http_response)
            status_code >= 100 and status_code <= 599
        else
            status_code == 0;
    }

    fn shouldRetryTaskOutcome(
        config: protocol.Config,
        outcome: protocol.TaskEffectOutcome,
        http_status_code: u32,
    ) bool {
        if (outcome != .http_response) {
            return config.retryable_task_outcome_mask &
                protocol.TaskOutcomeMask.forOutcome(outcome) != 0;
        }
        const policy = config.retryable_http_status_policy_mask;
        return (http_status_code == 408 and
            policy & protocol.TaskHttpRetryPolicyMask.request_timeout != 0) or
            (http_status_code == 429 and
                policy & protocol.TaskHttpRetryPolicyMask.throttled != 0) or
            (http_status_code >= 500 and http_status_code <= 599 and
                policy & protocol.TaskHttpRetryPolicyMask.server_error != 0);
    }

    fn runningTaskCount(self: *const RuntimeState) usize {
        var count: usize = 0;
        for (self.tasks) |slot| {
            if (slot.active and slot.row.state == @intFromEnum(protocol.TaskState.running)) count += 1;
        }
        return count;
    }
};

fn denied(
    output: protocol.AccessOutput,
    status_code: u32,
    reason: protocol.AccessReason,
) protocol.AccessOutput {
    var next = output;
    next.status_code = status_code;
    next.reason = @intFromEnum(reason);
    return next;
}

fn findFree(comptime T: type, slots: []const T) ?usize {
    for (slots, 0..) |slot, slot_index| {
        if (!slot.active) return slot_index;
    }
    return null;
}

fn nextHandle(value: *u64) Error!u64 {
    if (value.* == 0 or value.* == std.math.maxInt(u64)) return error.CounterExhausted;
    const result = value.*;
    value.* += 1;
    return result;
}

fn taskComesBefore(left: protocol.TaskOutput, right: protocol.TaskOutput) bool {
    if (left.base_score != right.base_score) return left.base_score > right.base_score;
    if (left.enqueued_at_monotonic_milliseconds != right.enqueued_at_monotonic_milliseconds) {
        return left.enqueued_at_monotonic_milliseconds < right.enqueued_at_monotonic_milliseconds;
    }
    return left.task_handle < right.task_handle;
}

fn setMinimum(target: *u64, value: u64) void {
    if (value == 0) return;
    if (target.* == 0 or value < target.*) target.* = value;
}

fn elapsedAtLeast(now: u64, start: u64, duration: u64) bool {
    return now >= start and now - start >= duration;
}

fn saturatingAdd(left: u64, right: u64) u64 {
    return std.math.add(u64, left, right) catch std.math.maxInt(u64);
}
