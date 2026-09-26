const std = @import("std");
const builtin = @import("builtin");
pub const types = @import("types.zig");
pub const wire = @import("layout.zig");
const integrity = @import("integrity.zig");

const WAIT_OBJECT_0: u32 = 0x00000000;
const WAIT_ABANDONED: u32 = 0x00000080;
const INFINITE: u32 = 0xffffffff;

extern "kernel32" fn WaitForSingleObject(handle: ?*anyopaque, milliseconds: u32) callconv(.winapi) u32;
extern "kernel32" fn ReleaseMutex(handle: ?*anyopaque) callconv(.winapi) i32;
extern "kernel32" fn QueryPerformanceCounter(value: *i64) callconv(.winapi) i32;
extern "kernel32" fn QueryPerformanceFrequency(value: *i64) callconv(.winapi) i32;

pub fn leaseClockFrequency() !u64 {
    if (builtin.os.tag != .windows) return error.UnsupportedLeaseClock;
    var frequency: i64 = 0;
    if (QueryPerformanceFrequency(&frequency) == 0 or frequency <= 0) {
        return error.LeaseClockUnavailable;
    }
    return @intCast(frequency);
}

pub fn leaseClockNow() !u64 {
    if (builtin.os.tag != .windows) return error.UnsupportedLeaseClock;
    var timestamp: i64 = 0;
    if (QueryPerformanceCounter(&timestamp) == 0 or timestamp <= 0) {
        return error.LeaseClockUnavailable;
    }
    return @intCast(timestamp);
}

pub fn boundedDeadline(now_timestamp: u64, duration: u64, maximum_duration: u64) !u64 {
    if (now_timestamp == 0 or duration == 0 or duration > maximum_duration) {
        return error.InvalidArgument;
    }
    return std.math.add(u64, now_timestamp, duration) catch error.CapacityExhausted;
}

pub const Mutation = struct {
    context: *Context,
    sequence: u64,
    finished: bool = false,

    pub fn finish(self: *Mutation) void {
        if (self.finished) return;
        const completed = types.nextGeneration(self.sequence) orelse unreachable;
        @atomicStore(u64, &self.context.header.mutation_sequence, completed, .release);
        self.finished = true;
    }
};

pub const LockGuard = struct {
    context: *Context,
    windows_owned: bool,
    abandoned: bool,

    pub fn deinit(self: *LockGuard) void {
        switch (self.context.synchronization_kind) {
            .process_local => self.context.local_mutex.unlock(),
            .windows_mutex => {
                if (self.windows_owned) _ = ReleaseMutex(self.context.windows_mutex_handle);
            },
        }
    }
};

pub const Context = struct {
    mapping: [*]u8,
    mapping_size: usize,
    layout: wire.Layout,
    header: *wire.Header,
    writable: bool,
    synchronization_kind: types.SynchronizationKind,
    windows_mutex_handle: ?*anyopaque,
    local_mutex: std.atomic.Mutex = .unlocked,
    resource_sweep_markers: []u8,

    pub fn create(
        mapping: [*]u8,
        mapping_size: usize,
        windows_mutex_handle: ?*anyopaque,
        config: *const types.LedgerConfig,
    ) !*Context {
        const computed = try wire.calculate(config);
        if (mapping_size < computed.required_size) return error.MappingTooSmall;
        if (@intFromEnum(@as(types.SynchronizationKind, @enumFromInt(config.synchronization_kind))) ==
            @intFromEnum(types.SynchronizationKind.windows_mutex) and windows_mutex_handle == null)
        {
            return error.MissingMutexHandle;
        }
        if (try leaseClockFrequency() != config.lease_clock_frequency_hz) {
            return error.InvalidConfig;
        }

        const resource_sweep_markers = try std.heap.page_allocator.alloc(
            u8,
            config.resource_capacity,
        );
        errdefer std.heap.page_allocator.free(resource_sweep_markers);
        @memset(resource_sweep_markers, 0);
        @memset(mapping[0..computed.required_size], 0);
        const header = wire.headerFrom(mapping);
        header.* = std.mem.zeroes(wire.Header);
        header.magic = types.ledger_magic;
        header.version = types.abi_version;
        header.header_size = types.header_size;
        header.mapping_size = computed.required_size;
        header.ledger_instance_id = config.ledger_instance_id;
        header.topology_generation = 1;
        header.mutation_sequence = 0;
        header.synchronization_kind = config.synchronization_kind;
        header.consistency_state = @intFromEnum(types.ConsistencyState.stable);
        header.resource_capacity = config.resource_capacity;
        header.subscription_capacity = config.subscription_capacity;
        header.task_capacity = config.task_capacity;
        header.resource_columns_offset = computed.resources.control;
        header.subscription_columns_offset = computed.subscriptions.control;
        header.task_columns_offset = computed.tasks.state;
        header.subscription_coefficient = config.subscription_coefficient;
        header.owner_application_key = config.owner_application_key;
        header.owner_process_created_utc_ticks = config.owner_process_created_utc_ticks;
        header.owner_process_id = config.owner_process_id;
        header.enqueue_sequence = 0;
        header.next_public_resource_id = 0;
        header.mapping_epoch = 1;
        header.resource_free_head = if (config.resource_capacity == 0) types.none_slot else 0;
        header.subscription_free_head = if (config.subscription_capacity == 0) types.none_slot else 0;
        header.task_free_head = if (config.task_capacity == 0) types.none_slot else 0;
        header.lease_clock_domain = config.lease_clock_domain;
        header.lease_clock_frequency_hz = config.lease_clock_frequency_hz;
        header.maximum_subscription_ttl = config.maximum_subscription_ttl;
        header.maximum_queue_ttl = config.maximum_queue_ttl;
        header.maximum_grant_ttl = config.maximum_grant_ttl;
        header.activity_generation = 1;

        const context = try std.heap.page_allocator.create(Context);
        context.* = .{
            .mapping = mapping,
            .mapping_size = mapping_size,
            .layout = computed,
            .header = header,
            .writable = true,
            .synchronization_kind = @enumFromInt(config.synchronization_kind),
            .windows_mutex_handle = windows_mutex_handle,
            .resource_sweep_markers = resource_sweep_markers,
        };
        context.initializeColumns();
        return context;
    }

    pub fn open(
        mapping: [*]u8,
        mapping_size: usize,
        windows_mutex_handle: ?*anyopaque,
        config: *const types.LedgerConfig,
        writable: bool,
    ) !*Context {
        const computed = try wire.calculate(config);
        if (mapping_size < computed.required_size) return error.MappingTooSmall;
        const header = wire.headerFrom(mapping);
        if (!headerMatches(header, config, computed.required_size)) return error.InvalidMapping;
        const synchronization_kind: types.SynchronizationKind = @enumFromInt(config.synchronization_kind);
        if (synchronization_kind == .windows_mutex and windows_mutex_handle == null) {
            return error.MissingMutexHandle;
        }
        if (try leaseClockFrequency() != config.lease_clock_frequency_hz) {
            return error.InvalidConfig;
        }

        const resource_sweep_markers = try std.heap.page_allocator.alloc(
            u8,
            config.resource_capacity,
        );
        errdefer std.heap.page_allocator.free(resource_sweep_markers);
        @memset(resource_sweep_markers, 0);
        const context = try std.heap.page_allocator.create(Context);
        context.* = .{
            .mapping = mapping,
            .mapping_size = mapping_size,
            .layout = computed,
            .header = header,
            .writable = writable,
            .synchronization_kind = synchronization_kind,
            .windows_mutex_handle = windows_mutex_handle,
            .resource_sweep_markers = resource_sweep_markers,
        };
        return context;
    }

    pub fn destroy(self: *Context) void {
        std.heap.page_allocator.free(self.resource_sweep_markers);
        std.heap.page_allocator.destroy(self);
    }

    pub fn acquire(self: *Context) !LockGuard {
        if (!self.writable) return error.ReadOnly;
        switch (self.synchronization_kind) {
            .process_local => {
                while (!self.local_mutex.tryLock()) std.atomic.spinLoopHint();
                return .{ .context = self, .windows_owned = false, .abandoned = false };
            },
            .windows_mutex => {
                if (builtin.os.tag != .windows) return error.UnsupportedSynchronization;
                const result = WaitForSingleObject(self.windows_mutex_handle, INFINITE);
                if (result != WAIT_OBJECT_0 and result != WAIT_ABANDONED) {
                    return error.SynchronizationFailed;
                }
                var unresolved_abandon = result == WAIT_ABANDONED;
                if (unresolved_abandon and self.recoverAbandonedLocked()) unresolved_abandon = false;
                return .{ .context = self, .windows_owned = true, .abandoned = unresolved_abandon };
            },
        }
    }

    pub fn recoverAbandonedLocked(self: *Context) bool {
        if (!self.writable) return false;
        const next_mapping_epoch = types.nextGeneration(self.header.mapping_epoch) orelse {
            @atomicStore(
                u8,
                &self.header.consistency_state,
                @intFromEnum(types.ConsistencyState.quarantined),
                .release,
            );
            return false;
        };
        @atomicStore(
            u8,
            &self.header.consistency_state,
            @intFromEnum(types.ConsistencyState.unstable),
            .release,
        );
        if (!integrity.isRecoverable(.{
            .mapping = self.mapping,
            .layout = self.layout,
            .header = self.header,
        })) {
            @atomicStore(
                u8,
                &self.header.consistency_state,
                @intFromEnum(types.ConsistencyState.quarantined),
                .release,
            );
            return false;
        }
        self.header.mapping_epoch = next_mapping_epoch;
        @atomicStore(
            u8,
            &self.header.consistency_state,
            @intFromEnum(types.ConsistencyState.stable),
            .release,
        );
        return true;
    }

    pub fn beginMutation(self: *Context) !Mutation {
        try self.requireMutationCapacity();
        const current = @atomicLoad(u64, &self.header.mutation_sequence, .acquire);
        const started = types.nextGeneration(current) orelse unreachable;
        @atomicStore(u64, &self.header.mutation_sequence, started, .release);
        return .{ .context = self, .sequence = started };
    }

    pub fn requireMutationCapacity(self: *const Context) !void {
        if (!self.writable) return error.ReadOnly;
        if (self.consistencyState() != .stable) return error.InconsistentState;
        const current = @atomicLoad(u64, &self.header.mutation_sequence, .acquire);
        if ((current & 1) != 0) {
            @atomicStore(
                u8,
                &self.header.consistency_state,
                @intFromEnum(types.ConsistencyState.unstable),
                .release,
            );
            return error.InconsistentState;
        }
        const started = types.nextGeneration(current) orelse return error.CapacityExhausted;
        if (types.nextGeneration(started) == null) return error.CapacityExhausted;
    }

    pub fn beginStableRead(self: *const Context) !u64 {
        var attempt: u8 = 0;
        while (attempt < 64) : (attempt += 1) {
            if (self.consistencyState() != .stable) return error.InconsistentState;
            const sequence = @atomicLoad(u64, &self.header.mutation_sequence, .acquire);
            if ((sequence & 1) == 0) return sequence;
            std.atomic.spinLoopHint();
        }
        return error.SynchronizationFailed;
    }

    pub fn stableReadFinished(self: *const Context, sequence: u64) bool {
        return self.consistencyState() == .stable and
            @atomicLoad(u64, &self.header.mutation_sequence, .acquire) == sequence;
    }

    pub fn consistencyState(self: *const Context) types.ConsistencyState {
        const raw = @atomicLoad(u8, &self.header.consistency_state, .acquire);
        return switch (raw) {
            @intFromEnum(types.ConsistencyState.stable) => .stable,
            @intFromEnum(types.ConsistencyState.unstable) => .unstable,
            else => .quarantined,
        };
    }

    pub fn nextTopologyGeneration(self: *const Context) !u64 {
        return types.nextGeneration(self.header.topology_generation) orelse
            error.CapacityExhausted;
    }

    pub fn incrementTopology(self: *Context) !void {
        self.header.topology_generation = try self.nextTopologyGeneration();
    }

    pub fn column(self: *const Context, comptime T: type, offset: usize) [*]T {
        return @ptrCast(@alignCast(self.mapping + offset));
    }

    pub fn resourceControl(self: *const Context, slot: u32) types.ResourceControl {
        return @enumFromInt(self.column(u8, self.layout.resources.control)[slot]);
    }

    pub fn resourceMatches(self: *const Context, resource: types.ResourceRef) bool {
        if (resource.ledger_instance_id != self.header.ledger_instance_id or
            resource.resource_slot >= self.header.resource_capacity or
            self.resourceControl(resource.resource_slot) != .active)
        {
            return false;
        }
        return self.column(u64, self.layout.resources.generation)[resource.resource_slot] == resource.resource_generation and
            self.column(u64, self.layout.resources.public_resource_id)[resource.resource_slot] == resource.public_resource_id;
    }

    pub fn hardProtectionCount(self: *const Context, slot: u32) u64 {
        return @as(u64, self.column(u32, self.layout.resources.queued_request_count)[slot]) +
            @as(u64, self.column(u32, self.layout.resources.reserved_grant_count)[slot]) +
            @as(u64, self.column(u32, self.layout.resources.active_use_count)[slot]);
    }

    pub fn destructiveActive(self: *const Context, slot: u32) bool {
        return self.column(u8, self.layout.resources.destructive_active)[slot] != 0;
    }

    fn initializeColumns(self: *Context) void {
        const resource_count = self.header.resource_capacity;
        const task_count = self.header.task_capacity;
        var resource_slot: u32 = 0;
        while (resource_slot < resource_count) : (resource_slot += 1) {
            self.column(u32, self.layout.resources.free_next)[resource_slot] =
                if (resource_slot + 1 < resource_count) resource_slot + 1 else types.none_slot;
            self.column(f64, self.layout.resources.subscription_multiplier)[resource_slot] = 1.0;
            self.column(u32, self.layout.resources.queue_head)[resource_slot] = types.none_slot;
            self.column(u32, self.layout.resources.queue_tail)[resource_slot] = types.none_slot;
        }
        var task_slot: u32 = 0;
        while (task_slot < task_count) : (task_slot += 1) {
            self.column(u32, self.layout.tasks.free_next)[task_slot] =
                if (task_slot + 1 < task_count) task_slot + 1 else types.none_slot;
            self.column(u32, self.layout.tasks.previous)[task_slot] = types.none_slot;
            self.column(u32, self.layout.tasks.next)[task_slot] = types.none_slot;
        }
        const subscription_count = self.header.subscription_capacity;
        var subscription_slot: u32 = 0;
        while (subscription_slot < subscription_count) : (subscription_slot += 1) {
            self.column(u32, self.layout.subscriptions.free_next)[subscription_slot] =
                if (subscription_slot + 1 < subscription_count) subscription_slot + 1 else types.none_slot;
        }
    }
};

fn headerMatches(header: *const wire.Header, config: *const types.LedgerConfig, required_size: usize) bool {
    return header.magic == types.ledger_magic and
        header.version == types.abi_version and
        header.header_size == types.header_size and
        header.mapping_size == required_size and
        header.ledger_instance_id == config.ledger_instance_id and
        header.synchronization_kind == config.synchronization_kind and
        header.resource_capacity == config.resource_capacity and
        header.subscription_capacity == config.subscription_capacity and
        header.task_capacity == config.task_capacity and
        header.owner_application_key == config.owner_application_key and
        header.owner_process_id == config.owner_process_id and
        header.owner_process_created_utc_ticks == config.owner_process_created_utc_ticks and
        header.lease_clock_domain == config.lease_clock_domain and
        header.lease_clock_frequency_hz == config.lease_clock_frequency_hz and
        header.maximum_subscription_ttl == config.maximum_subscription_ttl and
        header.maximum_queue_ttl == config.maximum_queue_ttl and
        header.maximum_grant_ttl == config.maximum_grant_ttl;
}

test "context initializes fixed sentinels without heap metadata in the mapping" {
    var config = std.mem.zeroes(types.LedgerConfig);
    config.abi_version = types.abi_version;
    config.struct_size = @sizeOf(types.LedgerConfig);
    config.ledger_instance_id = 11;
    config.owner_application_key = 22;
    config.resource_capacity = 2;
    config.subscription_capacity = 4;
    config.task_capacity = 8;
    config.subscription_coefficient = 0.25;
    config.synchronization_kind = @intFromEnum(types.SynchronizationKind.process_local);
    config.lease_clock_domain =
        @intFromEnum(types.LeaseClockDomain.windows_performance_counter);
    config.lease_clock_frequency_hz = try leaseClockFrequency();
    config.maximum_subscription_ttl = 10_000;
    config.maximum_queue_ttl = 10_000;
    config.maximum_grant_ttl = 10_000;
    const computed = try wire.calculate(&config);
    const memory = try std.testing.allocator.alignedAlloc(u8, .of(u64), computed.required_size);
    defer std.testing.allocator.free(memory);
    const context = try Context.create(memory.ptr, memory.len, null, &config);
    defer context.destroy();
    try std.testing.expectEqual(types.none_slot, context.column(u32, context.layout.resources.queue_head)[0]);
    try std.testing.expectEqual(@as(f64, 1.0), context.column(f64, context.layout.resources.subscription_multiplier)[1]);
}

test "bounded lease deadline rejects zero over-limit and overflow durations" {
    try std.testing.expectError(error.InvalidArgument, boundedDeadline(0, 1, 10));
    try std.testing.expectError(error.InvalidArgument, boundedDeadline(100, 0, 10));
    try std.testing.expectEqual(@as(u64, 110), try boundedDeadline(100, 10, 10));
    try std.testing.expectError(error.InvalidArgument, boundedDeadline(100, 11, 10));
    try std.testing.expectError(
        error.CapacityExhausted,
        boundedDeadline(std.math.maxInt(u64) - 4, 5, 5),
    );
}
