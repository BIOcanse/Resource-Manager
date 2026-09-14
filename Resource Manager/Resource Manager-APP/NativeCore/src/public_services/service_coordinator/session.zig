const std = @import("std");
const catalog = @import("catalog.zig");
const index = @import("index.zig");
const protocol = @import("protocol.zig");
const state = @import("state.zig");

pub const Error = catalog.Error || state.Error || error{
    OutOfMemory,
    ConfigurationMismatch,
};

pub const Session = struct {
    allocator: std.mem.Allocator,
    mutex: std.atomic.Mutex = .unlocked,
    config: protocol.Config,
    active_catalog: catalog.CatalogBank,
    staging_catalog: catalog.CatalogBank,
    active_models: catalog.ModelBank,
    staging_models: catalog.ModelBank,
    runtime: state.RuntimeState,
    last_command_epoch: u64 = 0,
    last_command_monotonic_milliseconds: u64 = 0,

    pub fn create(allocator: std.mem.Allocator, config: protocol.Config) Error!*Session {
        const resident_bytes = try requiredResidentBytes(config);
        try validateConfig(config, resident_bytes);
        const self = try allocator.create(Session);
        errdefer allocator.destroy(self);
        const active_catalog = try catalog.CatalogBank.init(allocator, config);
        errdefer {
            var value = active_catalog;
            value.deinit(allocator);
        }
        const staging_catalog = try catalog.CatalogBank.init(allocator, config);
        errdefer {
            var value = staging_catalog;
            value.deinit(allocator);
        }
        const active_models = try catalog.ModelBank.init(allocator, config);
        errdefer {
            var value = active_models;
            value.deinit(allocator);
        }
        const staging_models = try catalog.ModelBank.init(allocator, config);
        errdefer {
            var value = staging_models;
            value.deinit(allocator);
        }
        const runtime = try state.RuntimeState.init(allocator, config);
        errdefer {
            var value = runtime;
            value.deinit(allocator);
        }
        self.* = .{
            .allocator = allocator,
            .config = config,
            .active_catalog = active_catalog,
            .staging_catalog = staging_catalog,
            .active_models = active_models,
            .staging_models = staging_models,
            .runtime = runtime,
        };
        return self;
    }

    pub fn destroy(self: *Session) void {
        const allocator = self.allocator;
        self.active_catalog.deinit(allocator);
        self.staging_catalog.deinit(allocator);
        self.active_models.deinit(allocator);
        self.staging_models.deinit(allocator);
        self.runtime.deinit(allocator);
        allocator.destroy(self);
    }

    pub fn capacity(self: *const Session) protocol.Capacity {
        return .{
            .struct_size = @sizeOf(protocol.Capacity),
            .capability_capacity = self.config.maximum_capability_count,
            .route_capacity = self.config.maximum_route_count,
            .model_capacity = self.config.maximum_model_count,
            .model_alias_capacity = self.config.maximum_model_alias_count,
            .request_capacity = self.config.maximum_request_count,
            .rate_bucket_capacity = self.config.maximum_rate_bucket_count,
            .lease_capacity = self.config.maximum_lease_count,
            .subscription_capacity = self.config.maximum_subscription_count,
            .task_capacity = self.config.maximum_task_count,
            .capability_index_capacity = self.config.capability_index_capacity,
            .model_index_capacity = self.config.model_index_capacity,
            .alias_index_capacity = self.config.alias_index_capacity,
            .request_index_capacity = self.config.request_index_capacity,
            .rate_bucket_index_capacity = self.config.rate_bucket_index_capacity,
            .lease_index_capacity = self.config.lease_index_capacity,
            .subscription_index_capacity = self.config.subscription_index_capacity,
            .task_index_capacity = self.config.task_index_capacity,
            .catalog_text_capacity = self.config.maximum_catalog_text_bytes,
            .model_text_capacity = self.config.maximum_model_text_bytes,
            .resident_byte_count = requiredResidentBytes(self.config) catch unreachable,
            .reserved = .{ 0, 0, 0 },
        };
    }

    pub fn replaceCatalog(
        self: *Session,
        header: protocol.CatalogReplaceInput,
        capabilities: []const protocol.CapabilityInput,
        routes: []const protocol.RouteInput,
        text: []const u8,
    ) Error!void {
        try self.validateCommand(header.command_epoch, header.command_monotonic_milliseconds);
        if (header.catalog_generation <= self.active_catalog.generation) return error.InvalidArgument;
        try self.staging_catalog.stage(header, capabilities, routes, text);
        self.commitCommand(header.command_epoch, header.command_monotonic_milliseconds);
        std.mem.swap(catalog.CatalogBank, &self.active_catalog, &self.staging_catalog);
        self.staging_catalog.reset();
    }

    pub fn replaceModels(
        self: *Session,
        header: protocol.ModelReplaceInput,
        models: []const protocol.ModelInput,
        aliases: []const protocol.ModelAliasInput,
        text: []const u8,
    ) Error!void {
        try self.validateCommand(header.command_epoch, header.command_monotonic_milliseconds);
        if (header.model_generation <= self.active_models.generation or
            !self.runtime.acceptsModelReplacement(
                header.acquisition_attempt_handle,
                header.command_monotonic_milliseconds,
            ))
        {
            return error.InvalidArgument;
        }
        try self.staging_models.stage(header, models, aliases, text);
        if (!self.runtime.canReplaceModelsAt(
            &self.staging_models,
            header.command_monotonic_milliseconds,
        )) {
            return error.InvalidState;
        }
        self.commitCommand(header.command_epoch, header.command_monotonic_milliseconds);
        self.runtime.expire(self.config, header.command_monotonic_milliseconds);
        std.mem.swap(catalog.ModelBank, &self.active_models, &self.staging_models);
        self.staging_models.reset();
        self.runtime.completeModelAcquisitionSuccess(
            self.config,
            header.acquisition_attempt_handle,
            header.command_monotonic_milliseconds,
            header.model_generation,
        ) catch unreachable;
    }

    pub fn planModelAcquisition(
        self: *Session,
        input: protocol.ModelAcquisitionPlanInput,
    ) Error!protocol.ModelAcquisitionPlanOutput {
        if (input.struct_size != @sizeOf(protocol.ModelAcquisitionPlanInput) or
            input.reserved_u32 != 0 or
            !protocol.allZero([3]u64, input.reserved))
        {
            return error.InvalidArgument;
        }
        try self.beginCommand(input.command_epoch, input.command_monotonic_milliseconds);
        return self.runtime.planModelAcquisition(
            self.config,
            input,
            self.active_models.generation,
        );
    }

    pub fn completeModelAcquisition(
        self: *Session,
        input: protocol.ModelAcquisitionCompletionInput,
    ) Error!void {
        if (input.struct_size != @sizeOf(protocol.ModelAcquisitionCompletionInput) or
            input.status == @intFromEnum(protocol.ModelAcquisitionCompletionStatus.invalid) or
            input.status > @intFromEnum(protocol.ModelAcquisitionCompletionStatus.failed) or
            input.attempt_handle == 0 or
            !protocol.allZero([3]u64, input.reserved))
        {
            return error.InvalidArgument;
        }
        try self.validateCommand(input.command_epoch, input.command_monotonic_milliseconds);
        try self.runtime.completeModelAcquisitionFailure(
            self.config,
            input,
        );
        self.commitCommand(input.command_epoch, input.command_monotonic_milliseconds);
        self.runtime.expire(self.config, input.command_monotonic_milliseconds);
    }

    pub fn admitRequest(
        self: *Session,
        input: protocol.AccessInput,
        path: []const u8,
    ) Error!protocol.AccessOutput {
        if (input.struct_size != @sizeOf(protocol.AccessInput) or
            input.flags & ~protocol.RequestFlags.known != 0 or
            input.caller_handle == 0 or
            input.remote_scope == @intFromEnum(protocol.RemoteScope.invalid) or
            input.remote_scope > @intFromEnum(protocol.RemoteScope.unknown) or
            input.method == @intFromEnum(protocol.HttpMethod.invalid) or
            input.method > @intFromEnum(protocol.HttpMethod.other) or
            input.path.offset != 0 or
            input.path.length != path.len or
            !protocol.allZero([3]u64, input.reserved) or
            !validRequestPath(path))
        {
            return error.InvalidArgument;
        }
        try self.beginCommand(input.command_epoch, input.command_monotonic_milliseconds);
        return self.runtime.admitRequest(self.config, &self.active_catalog, input, path);
    }

    pub fn completeRequest(
        self: *Session,
        input: protocol.RequestCompleteInput,
    ) Error!protocol.RequestCompleteOutput {
        if (input.struct_size != @sizeOf(protocol.RequestCompleteInput) or
            input.reserved_u32 != 0 or
            input.request_handle == 0 or
            !protocol.allZero([3]u64, input.reserved))
        {
            return error.InvalidArgument;
        }
        try self.beginCommand(input.command_epoch, input.command_monotonic_milliseconds);
        return self.runtime.completeRequest(input.request_handle);
    }

    pub fn resolveModel(
        self: *Session,
        input: protocol.ModelResolveInput,
        alias: []const u8,
    ) Error!protocol.ModelResolveOutput {
        if (input.struct_size != @sizeOf(protocol.ModelResolveInput) or
            input.reserved_u32 != 0 or
            input.alias.offset != 0 or
            input.alias.length != alias.len or
            !protocol.allZero([3]u64, input.reserved) or
            alias.len == 0 or
            !std.unicode.utf8ValidateSlice(alias))
        {
            return error.InvalidArgument;
        }
        try self.beginCommand(input.command_epoch, input.command_monotonic_milliseconds);
        var output = std.mem.zeroes(protocol.ModelResolveOutput);
        output.struct_size = @sizeOf(protocol.ModelResolveOutput);
        output.model_generation = self.active_models.generation;
        if (self.active_models.generation == 0 or
            !self.runtime.modelCatalogUsable(input.command_monotonic_milliseconds))
        {
            output.status = @intFromEnum(protocol.ModelResolveStatus.catalog_unavailable);
            return output;
        }
        const model = self.active_models.resolveAlias(alias) orelse {
            output.status = @intFromEnum(protocol.ModelResolveStatus.not_found);
            return output;
        };
        output.status = @intFromEnum(protocol.ModelResolveStatus.matched);
        output.model_handle = model.model_handle;
        output.provider_handle = model.provider_handle;
        output.payload_handle = model.payload_handle;
        output.model_flags = model.flags;
        return output;
    }

    pub fn beginLease(
        self: *Session,
        input: protocol.LeaseBeginInput,
    ) Error!protocol.LeaseOutput {
        if (input.struct_size != @sizeOf(protocol.LeaseBeginInput) or
            input.reserved_u32 != 0 or
            !protocol.allZero([2]u64, input.reserved))
        {
            return error.InvalidArgument;
        }
        try self.beginCommand(input.command_epoch, input.command_monotonic_milliseconds);
        return self.runtime.beginLease(self.config, &self.active_models, input);
    }

    pub fn endLease(self: *Session, input: protocol.LeaseEndInput) Error!void {
        if (input.struct_size != @sizeOf(protocol.LeaseEndInput) or
            input.reserved_u32 != 0 or
            input.lease_handle == 0 or
            !protocol.allZero([3]u64, input.reserved))
        {
            return error.InvalidArgument;
        }
        try self.beginCommand(input.command_epoch, input.command_monotonic_milliseconds);
        try self.runtime.endLease(input.lease_handle);
    }

    pub fn upsertSubscription(
        self: *Session,
        input: protocol.SubscriptionUpsertInput,
    ) Error!protocol.SubscriptionOutput {
        if (input.struct_size != @sizeOf(protocol.SubscriptionUpsertInput) or
            !protocol.allZero([2]u64, input.reserved))
        {
            return error.InvalidArgument;
        }
        try self.beginCommand(input.command_epoch, input.command_monotonic_milliseconds);
        return self.runtime.upsertSubscription(self.config, &self.active_models, input);
    }

    pub fn removeSubscription(self: *Session, input: protocol.SubscriptionRemoveInput) Error!void {
        if (input.struct_size != @sizeOf(protocol.SubscriptionRemoveInput) or
            input.reserved_u32 != 0 or
            input.subscription_handle == 0 or
            !protocol.allZero([3]u64, input.reserved))
        {
            return error.InvalidArgument;
        }
        try self.beginCommand(input.command_epoch, input.command_monotonic_milliseconds);
        try self.runtime.removeSubscription(input.subscription_handle);
    }

    pub fn enqueueTask(
        self: *Session,
        input: protocol.TaskEnqueueInput,
    ) Error!protocol.TaskOutput {
        if (input.struct_size != @sizeOf(protocol.TaskEnqueueInput) or
            input.reserved_u32 != 0 or
            !protocol.allZero([2]u64, input.reserved))
        {
            return error.InvalidArgument;
        }
        try self.beginCommand(input.command_epoch, input.command_monotonic_milliseconds);
        return self.runtime.enqueueTask(&self.active_models, input);
    }

    pub fn planTasks(
        self: *Session,
        input: protocol.TaskPlanInput,
        output: []protocol.TaskOutput,
    ) Error!protocol.TaskPlanOutput {
        if (input.struct_size != @sizeOf(protocol.TaskPlanInput) or
            input.maximum_output_count == 0 or
            input.maximum_output_count > output.len or
            input.maximum_output_count > self.config.maximum_concurrent_model_tasks or
            !protocol.allZero([3]u64, input.reserved))
        {
            return error.InvalidArgument;
        }
        try self.validateCommand(input.command_epoch, input.command_monotonic_milliseconds);
        try self.runtime.validateTaskPlan(self.config, input, output.len);
        self.commitCommand(input.command_epoch, input.command_monotonic_milliseconds);
        self.runtime.expire(self.config, input.command_monotonic_milliseconds);
        return self.runtime.planTasks(self.config, input, output) catch unreachable;
    }

    pub fn completeTask(
        self: *Session,
        input: protocol.TaskCompletionInput,
    ) Error!protocol.TaskCompletionOutput {
        if (input.struct_size != @sizeOf(protocol.TaskCompletionInput) or
            input.task_handle == 0 or
            input.attempt == 0 or
            input.outcome == @intFromEnum(protocol.TaskEffectOutcome.invalid) or
            input.outcome > @intFromEnum(protocol.TaskEffectOutcome.cancelled) or
            !protocol.allZero([2]u64, input.reserved))
        {
            return error.InvalidArgument;
        }
        try self.validateCommand(input.command_epoch, input.command_monotonic_milliseconds);
        const output = try self.runtime.completeTask(self.config, input);
        self.commitCommand(input.command_epoch, input.command_monotonic_milliseconds);
        self.runtime.expire(self.config, input.command_monotonic_milliseconds);
        return output;
    }

    pub fn cancelQueuedTasks(
        self: *Session,
        input: protocol.TaskCancelInput,
        output: []protocol.TaskOutput,
    ) Error!protocol.TaskCancelOutput {
        if (input.struct_size != @sizeOf(protocol.TaskCancelInput) or
            input.maximum_output_count == 0 or
            input.maximum_output_count > output.len or
            input.maximum_output_count > self.config.maximum_task_count or
            !protocol.allZero([3]u64, input.reserved))
        {
            return error.InvalidArgument;
        }
        try self.validateCommand(input.command_epoch, input.command_monotonic_milliseconds);
        try self.runtime.validateTaskCancellation(input, output.len);
        self.commitCommand(input.command_epoch, input.command_monotonic_milliseconds);
        self.runtime.expire(self.config, input.command_monotonic_milliseconds);
        return self.runtime.cancelQueuedTasks(input, output) catch unreachable;
    }

    pub fn readCapabilities(
        self: *const Session,
        output: []protocol.CapabilityOutput,
    ) Error!usize {
        if (output.len < self.active_catalog.capability_count) return error.BufferTooSmall;
        var count: usize = 0;
        for (self.active_catalog.capabilities[0..self.active_catalog.capability_count]) |slot| {
            output[count] = .{
                .struct_size = @sizeOf(protocol.CapabilityOutput),
                .flags = slot.row.flags,
                .capability_handle = slot.row.capability_handle,
                .payload_handle = slot.row.payload_handle,
                .catalog_generation = self.active_catalog.generation,
                .reserved = .{ 0, 0 },
            };
            count += 1;
        }
        std.mem.sort(protocol.CapabilityOutput, output[0..count], {}, capabilityBefore);
        return count;
    }

    pub fn snapshot(self: *const Session) protocol.Snapshot {
        return self.runtime.snapshot(
            self.config,
            self.last_command_epoch,
            self.last_command_monotonic_milliseconds,
            &self.active_catalog,
            &self.active_models,
        );
    }

    fn validateCommand(self: *const Session, epoch: u64, monotonic: u64) Error!void {
        if (epoch == 0 or epoch <= self.last_command_epoch) return error.InvalidArgument;
        if (monotonic < self.last_command_monotonic_milliseconds) return error.InvalidArgument;
    }

    fn beginCommand(self: *Session, epoch: u64, monotonic: u64) Error!void {
        try self.validateCommand(epoch, monotonic);
        self.commitCommand(epoch, monotonic);
        self.runtime.expire(self.config, monotonic);
    }

    fn commitCommand(self: *Session, epoch: u64, monotonic: u64) void {
        self.last_command_epoch = epoch;
        self.last_command_monotonic_milliseconds = monotonic;
    }
};

pub fn requiredResidentBytes(config: protocol.Config) Error!u64 {
    var total: u64 = @sizeOf(Session);
    try addArray(&total, 2, config.maximum_capability_count, catalog.capability_slot_size);
    try addArray(&total, 2, config.maximum_route_count, catalog.route_slot_size);
    try addArray(&total, 2, config.capability_index_capacity, @sizeOf(index.HandleEntry));
    try addArray(&total, 2, config.maximum_catalog_text_bytes, @sizeOf(u8));
    try addArray(&total, 2, config.maximum_model_count, catalog.model_slot_size);
    try addArray(&total, 2, config.maximum_model_alias_count, catalog.alias_slot_size);
    try addArray(&total, 2, config.model_index_capacity, @sizeOf(index.HandleEntry));
    try addArray(&total, 2, config.alias_index_capacity, @sizeOf(index.HashEntry));
    try addArray(&total, 2, config.maximum_model_text_bytes, @sizeOf(u8));
    try addArray(&total, 1, config.maximum_request_count, state.request_slot_size);
    try addArray(&total, 1, config.maximum_rate_bucket_count, state.rate_bucket_slot_size);
    try addArray(&total, 1, config.maximum_lease_count, state.lease_slot_size);
    try addArray(&total, 1, config.maximum_subscription_count, state.subscription_slot_size);
    try addArray(&total, 1, config.maximum_task_count, state.task_slot_size);
    try addArray(&total, 1, config.request_index_capacity, @sizeOf(index.HandleEntry));
    try addArray(&total, 1, config.rate_bucket_index_capacity, @sizeOf(index.PairEntry));
    try addArray(&total, 1, config.lease_index_capacity, @sizeOf(index.HandleEntry));
    try addArray(&total, 1, config.subscription_index_capacity, @sizeOf(index.HandleEntry));
    try addArray(&total, 1, config.task_index_capacity, @sizeOf(index.HandleEntry));
    return total;
}

fn validateConfig(config: protocol.Config, resident_bytes: u64) Error!void {
    if (config.abi_version != protocol.abi_version or
        config.struct_size != @sizeOf(protocol.Config) or
        config.generation == 0 or
        (config.session_instance_low == 0 and config.session_instance_high == 0) or
        config.maximum_capability_count == 0 or
        config.maximum_route_count == 0 or
        config.maximum_model_count == 0 or
        config.maximum_model_alias_count == 0 or
        config.maximum_request_count == 0 or
        config.maximum_rate_bucket_count == 0 or
        config.maximum_lease_count == 0 or
        config.maximum_subscription_count == 0 or
        config.maximum_task_count == 0 or
        config.maximum_catalog_text_bytes == 0 or
        config.maximum_model_text_bytes == 0 or
        config.maximum_concurrent_model_tasks == 0 or
        config.maximum_concurrent_model_tasks > config.maximum_task_count or
        config.maximum_requests_per_rate_window == 0 or
        config.maximum_inflight_requests_per_caller == 0 or
        config.retryable_task_outcome_mask & ~protocol.TaskOutcomeMask.known != 0 or
        config.retryable_http_status_policy_mask &
            ~protocol.TaskHttpRetryPolicyMask.known != 0 or
        config.maximum_task_attempt_count == 0 or
        config.rate_window_milliseconds == 0 or
        config.request_timeout_milliseconds == 0 or
        config.lease_timeout_milliseconds == 0 or
        config.subscription_timeout_milliseconds == 0 or
        config.task_timeout_milliseconds == 0 or
        config.retry_delay_milliseconds == 0 or
        config.model_catalog_acquisition_interval_milliseconds == 0 or
        config.model_catalog_last_good_lifetime_milliseconds <
            config.model_catalog_acquisition_interval_milliseconds or
        config.model_catalog_acquisition_timeout_milliseconds == 0 or
        config.flags & ~protocol.ConfigFlags.known != 0 or
        config.flags & protocol.ConfigFlags.loopback_only == 0 or
        !protocol.validPowerOfTwoCapacity(config.capability_index_capacity, config.maximum_capability_count) or
        !protocol.validPowerOfTwoCapacity(config.model_index_capacity, config.maximum_model_count) or
        !protocol.validPowerOfTwoCapacity(config.alias_index_capacity, config.maximum_model_alias_count) or
        !protocol.validPowerOfTwoCapacity(config.request_index_capacity, config.maximum_request_count) or
        !protocol.validPowerOfTwoCapacity(config.rate_bucket_index_capacity, config.maximum_rate_bucket_count) or
        !protocol.validPowerOfTwoCapacity(config.lease_index_capacity, config.maximum_lease_count) or
        !protocol.validPowerOfTwoCapacity(config.subscription_index_capacity, config.maximum_subscription_count) or
        !protocol.validPowerOfTwoCapacity(config.task_index_capacity, config.maximum_task_count))
    {
        return error.InvalidArgument;
    }
    if (config.resident_byte_budget < resident_bytes) return error.ResidentBudgetExceeded;
}

fn validRequestPath(path: []const u8) bool {
    if (path.len == 0 or path[0] != '/' or !std.unicode.utf8ValidateSlice(path)) return false;
    for (path) |byte| {
        if (byte == 0 or byte == '?' or byte == '#') return false;
    }
    return true;
}

fn capabilityBefore(
    _: void,
    left: protocol.CapabilityOutput,
    right: protocol.CapabilityOutput,
) bool {
    return left.capability_handle < right.capability_handle;
}

fn addArray(total: *u64, multiplier: u64, count: u32, size: u64) Error!void {
    const first = std.math.mul(u64, multiplier, count) catch return error.CapacityExceeded;
    const bytes = std.math.mul(u64, first, size) catch return error.CapacityExceeded;
    total.* = std.math.add(u64, total.*, bytes) catch return error.CapacityExceeded;
}
