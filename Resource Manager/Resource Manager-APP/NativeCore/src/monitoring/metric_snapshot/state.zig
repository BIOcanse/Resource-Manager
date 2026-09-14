const std = @import("std");
const windows = std.os.windows;
const protocol = @import("protocol.zig");
const catalog = @import("catalog.zig");

pub const GpuRecord = struct {
    active: bool = false,
    output: protocol.GpuInventoryOutput =
        std.mem.zeroes(protocol.GpuInventoryOutput),
};

pub const GpuIdentity = struct {
    adapter_handle: u64 = 0,
    adapter_luid_low: u64 = 0,
    adapter_luid_high: u64 = 0,
    stable_key_handle: u64 = 0,
};

pub const SourceModeState = struct {
    present: bool = false,
    selected: bool = false,
    inventory_selected: bool = false,
    completion_committed: bool = false,
    input: protocol.SourceModeInput = std.mem.zeroes(protocol.SourceModeInput),
    planned_rule_count: u32 = 0,
    plan_epoch: u64 = 0,
    plan_token_fingerprint: u64 = 0,
};

pub const Session = struct {
    allocator: std.mem.Allocator,
    mutex: windows.SRWLOCK = windows.SRWLOCK_INIT,
    config: protocol.Config,
    active: catalog.Bank,
    staging: catalog.Bank,
    requested_rule_indices: []u32,
    requested_rule_flags: []bool,
    observation_slot_by_rule: []u32,
    observations: []protocol.ObservationInput,
    gpu_active: []GpuRecord,
    gpu_staging: []GpuRecord,
    gpu_index: []u32,
    gpu_staging_index: []u32,
    gpu_luid_index: []u32,
    gpu_staging_luid_index: []u32,
    gpu_key_index: []u32,
    gpu_staging_key_index: []u32,
    gpu_identity_scratch: []GpuIdentity,
    gpu_identity_index: []u32,
    gpu_identity_luid_index: []u32,
    gpu_identity_key_index: []u32,
    source_modes: []SourceModeState,
    planned_rule_handles: []u64,
    planned_rule_flags: []bool,
    planned_rule_count: u32 = 0,
    completion: protocol.CompletionHeader =
        std.mem.zeroes(protocol.CompletionHeader),
    pending_cpu_counter: protocol.CpuCounterInput =
        std.mem.zeroes(protocol.CpuCounterInput),
    pending_cpu_counter_present: bool = false,
    requested_count: u32 = 0,
    observation_count: u32 = 0,
    gpu_staging_count: u32 = 0,
    gpu_staging_prepared: bool = false,
    catalog_generation: u64 = 0,
    catalog_fingerprint: u64 = 0,
    state_revision: u64 = 1,
    committed_generation: u64 = 0,
    last_operation_epoch: u64 = 0,
    last_plan_epoch: u64 = 0,
    last_command_at_milliseconds: u64 = 0,
    captured_at_milliseconds: u64 = 0,
    semantic_fingerprint: u64 = 0,
    phase: protocol.Phase = .empty,

    pub fn create(config: *const protocol.Config) !*Session {
        if (!protocol.validConfig(config)) return error.InvalidConfiguration;
        const allocator = std.heap.page_allocator;
        const self = try allocator.create(Session);
        errdefer allocator.destroy(self);

        var allocations = AllocationSet{};
        errdefer allocations.deinit(allocator);
        try allocations.allocate(allocator, config);
        const resident = allocations.residentByteCount();
        if (resident > config.resident_byte_budget) return error.InvalidConfiguration;

        self.* = .{
            .allocator = allocator,
            .config = config.*,
            .active = allocations.takeActive(),
            .staging = allocations.takeStaging(),
            .requested_rule_indices = allocations.takeRequested(),
            .requested_rule_flags = allocations.takeRequestedFlags(),
            .observation_slot_by_rule = allocations.takeObservationSlots(),
            .observations = allocations.takeObservations(),
            .gpu_active = allocations.takeGpuActive(),
            .gpu_staging = allocations.takeGpuStaging(),
            .gpu_index = allocations.takeGpuIndex(),
            .gpu_staging_index = allocations.takeGpuStagingIndex(),
            .gpu_luid_index = allocations.takeGpuLuidIndex(),
            .gpu_staging_luid_index = allocations.takeGpuStagingLuidIndex(),
            .gpu_key_index = allocations.takeGpuKeyIndex(),
            .gpu_staging_key_index = allocations.takeGpuStagingKeyIndex(),
            .gpu_identity_scratch = allocations.takeGpuIdentityScratch(),
            .gpu_identity_index = allocations.takeGpuIdentityIndex(),
            .gpu_identity_luid_index = allocations.takeGpuIdentityLuidIndex(),
            .gpu_identity_key_index = allocations.takeGpuIdentityKeyIndex(),
            .source_modes = allocations.takeSourceModes(),
            .planned_rule_handles = allocations.takePlanMetricHandles(),
            .planned_rule_flags = allocations.takePlannedRuleFlags(),
        };
        return self;
    }

    pub fn destroy(self: *Session) void {
        const allocator = self.allocator;
        deinitBank(allocator, &self.active);
        deinitBank(allocator, &self.staging);
        allocator.free(self.requested_rule_indices);
        allocator.free(self.requested_rule_flags);
        allocator.free(self.observation_slot_by_rule);
        allocator.free(self.observations);
        allocator.free(self.gpu_active);
        allocator.free(self.gpu_staging);
        allocator.free(self.gpu_index);
        allocator.free(self.gpu_staging_index);
        allocator.free(self.gpu_luid_index);
        allocator.free(self.gpu_staging_luid_index);
        allocator.free(self.gpu_key_index);
        allocator.free(self.gpu_staging_key_index);
        allocator.free(self.gpu_identity_scratch);
        allocator.free(self.gpu_identity_index);
        allocator.free(self.gpu_identity_luid_index);
        allocator.free(self.gpu_identity_key_index);
        allocator.free(self.source_modes);
        allocator.free(self.planned_rule_handles);
        allocator.free(self.planned_rule_flags);
        allocator.destroy(self);
    }

    pub fn capacity(self: *const Session) protocol.Capacity {
        return .{
            .struct_size = @sizeOf(protocol.Capacity),
            .source_capacity = self.config.maximum_source_count,
            .metric_capacity = self.config.maximum_metric_count,
            .rule_capacity = self.config.maximum_rule_count,
            .requested_capacity = self.config.maximum_requested_count,
            .observation_capacity = self.config.maximum_observation_count,
            .gpu_adapter_capacity = self.config.maximum_gpu_adapter_count,
            .persistence_source_capacity = self.config.maximum_persistence_source_count,
            .persistence_rule_capacity = self.config.maximum_persistence_rule_count,
            .persistence_gpu_capacity = self.config.maximum_persistence_gpu_count,
            .source_index_capacity = self.config.source_index_capacity,
            .metric_index_capacity = self.config.metric_index_capacity,
            .rule_index_capacity = self.config.rule_index_capacity,
            .gpu_index_capacity = self.config.gpu_index_capacity,
            .gpu_luid_index_capacity = self.config.gpu_luid_index_capacity,
            .gpu_key_index_capacity = self.config.gpu_key_index_capacity,
            .plan_metric_capacity = self.config.maximum_plan_metric_count,
            .source_mode_capacity = self.config.maximum_source_mode_count,
            .source_plan_capacity = self.config.maximum_source_plan_count,
            .metric_plan_capacity = self.config.maximum_metric_plan_count,
            .resident_byte_count = self.residentByteCount(),
            .reserved = .{ 0, 0 },
        };
    }

    pub fn residentByteCount(self: *const Session) u64 {
        var total: u64 = @sizeOf(Session);
        total += sliceBytes(catalog.SourceRecord, self.active.sources.len) * 2;
        total += sliceBytes(catalog.RuleRecord, self.active.rules.len) * 2;
        total += sliceBytes(catalog.MetricRecord, self.active.metrics.len) * 2;
        total += sliceBytes(u32, self.active.source_index.len) * 2;
        total += sliceBytes(u32, self.active.rule_index.len) * 2;
        total += sliceBytes(u32, self.active.metric_index.len) * 2;
        total += sliceBytes(u32, self.requested_rule_indices.len);
        total += sliceBytes(bool, self.requested_rule_flags.len);
        total += sliceBytes(u32, self.observation_slot_by_rule.len);
        total += sliceBytes(protocol.ObservationInput, self.observations.len);
        total += sliceBytes(GpuRecord, self.gpu_active.len) * 2;
        total += sliceBytes(u32, self.gpu_index.len) * 2;
        total += sliceBytes(u32, self.gpu_luid_index.len) * 2;
        total += sliceBytes(u32, self.gpu_key_index.len) * 2;
        total += sliceBytes(GpuIdentity, self.gpu_identity_scratch.len);
        total += sliceBytes(u32, self.gpu_identity_index.len);
        total += sliceBytes(u32, self.gpu_identity_luid_index.len);
        total += sliceBytes(u32, self.gpu_identity_key_index.len);
        total += sliceBytes(SourceModeState, self.source_modes.len);
        total += sliceBytes(u64, self.planned_rule_handles.len);
        total += sliceBytes(bool, self.planned_rule_flags.len);
        return total;
    }

    pub fn operationAdvances(self: *const Session, epoch: u64, command_at: u64) bool {
        return epoch > self.last_operation_epoch and command_at >= self.last_command_at_milliseconds;
    }

    pub fn futureTimeValid(self: *const Session, observed_at: u64, command_at: u64) bool {
        const maximum = std.math.add(
            u64,
            command_at,
            self.config.maximum_future_skew_milliseconds,
        ) catch return false;
        return observed_at <= maximum;
    }

    pub fn nextRevision(self: *const Session) ?u64 {
        if (self.state_revision == std.math.maxInt(u64)) return null;
        return self.state_revision + 1;
    }

    pub fn nextCommittedGeneration(self: *const Session) ?u64 {
        if (self.committed_generation == std.math.maxInt(u64)) return null;
        return self.committed_generation + 1;
    }

    pub fn clearCompletion(self: *Session) void {
        self.completion = std.mem.zeroes(protocol.CompletionHeader);
        self.requested_count = 0;
        self.observation_count = 0;
        self.gpu_staging_count = 0;
        self.gpu_staging_prepared = false;
        self.pending_cpu_counter = std.mem.zeroes(protocol.CpuCounterInput);
        self.pending_cpu_counter_present = false;
        @memset(self.requested_rule_indices, 0);
        @memset(self.requested_rule_flags, false);
        @memset(self.observation_slot_by_rule, catalog.no_index);
        @memset(self.observations, std.mem.zeroes(protocol.ObservationInput));
    }

    pub fn clearPlanAuthority(self: *Session) void {
        self.planned_rule_count = 0;
        @memset(self.source_modes, .{});
        @memset(self.planned_rule_handles, 0);
        @memset(self.planned_rule_flags, false);
    }
};

const AllocationSet = struct {
    active: ?catalog.Bank = null,
    staging: ?catalog.Bank = null,
    requested: ?[]u32 = null,
    requested_flags: ?[]bool = null,
    observation_slots: ?[]u32 = null,
    observations: ?[]protocol.ObservationInput = null,
    gpu_active: ?[]GpuRecord = null,
    gpu_staging: ?[]GpuRecord = null,
    gpu_index: ?[]u32 = null,
    gpu_staging_index: ?[]u32 = null,
    gpu_luid_index: ?[]u32 = null,
    gpu_staging_luid_index: ?[]u32 = null,
    gpu_key_index: ?[]u32 = null,
    gpu_staging_key_index: ?[]u32 = null,
    gpu_identity_scratch: ?[]GpuIdentity = null,
    gpu_identity_index: ?[]u32 = null,
    gpu_identity_luid_index: ?[]u32 = null,
    gpu_identity_key_index: ?[]u32 = null,
    source_modes: ?[]SourceModeState = null,
    plan_metric_handles: ?[]u64 = null,
    planned_rule_flags: ?[]bool = null,

    fn allocate(
        self: *AllocationSet,
        allocator: std.mem.Allocator,
        config: *const protocol.Config,
    ) !void {
        self.active = try allocateBank(allocator, config);
        self.staging = try allocateBank(allocator, config);
        self.requested = try allocator.alloc(u32, config.maximum_requested_count);
        @memset(self.requested.?, 0);
        self.requested_flags = try allocator.alloc(bool, config.maximum_rule_count);
        @memset(self.requested_flags.?, false);
        self.observation_slots = try allocator.alloc(u32, config.maximum_rule_count);
        @memset(self.observation_slots.?, catalog.no_index);
        self.observations = try allocator.alloc(
            protocol.ObservationInput,
            config.maximum_observation_count,
        );
        @memset(self.observations.?, std.mem.zeroes(protocol.ObservationInput));
        self.gpu_active = try allocator.alloc(GpuRecord, config.maximum_gpu_adapter_count);
        @memset(self.gpu_active.?, .{});
        self.gpu_staging = try allocator.alloc(GpuRecord, config.maximum_gpu_adapter_count);
        @memset(self.gpu_staging.?, .{});
        self.gpu_index = try allocator.alloc(u32, config.gpu_index_capacity);
        @memset(self.gpu_index.?, catalog.no_index);
        self.gpu_staging_index = try allocator.alloc(u32, config.gpu_index_capacity);
        @memset(self.gpu_staging_index.?, catalog.no_index);
        self.gpu_luid_index = try allocator.alloc(u32, config.gpu_luid_index_capacity);
        @memset(self.gpu_luid_index.?, catalog.no_index);
        self.gpu_staging_luid_index = try allocator.alloc(u32, config.gpu_luid_index_capacity);
        @memset(self.gpu_staging_luid_index.?, catalog.no_index);
        self.gpu_key_index = try allocator.alloc(u32, config.gpu_key_index_capacity);
        @memset(self.gpu_key_index.?, catalog.no_index);
        self.gpu_staging_key_index = try allocator.alloc(u32, config.gpu_key_index_capacity);
        @memset(self.gpu_staging_key_index.?, catalog.no_index);
        self.gpu_identity_scratch =
            try allocator.alloc(GpuIdentity, config.maximum_gpu_adapter_count);
        @memset(self.gpu_identity_scratch.?, .{});
        self.gpu_identity_index = try allocator.alloc(u32, config.gpu_index_capacity);
        @memset(self.gpu_identity_index.?, catalog.no_index);
        self.gpu_identity_luid_index =
            try allocator.alloc(u32, config.gpu_luid_index_capacity);
        @memset(self.gpu_identity_luid_index.?, catalog.no_index);
        self.gpu_identity_key_index = try allocator.alloc(u32, config.gpu_key_index_capacity);
        @memset(self.gpu_identity_key_index.?, catalog.no_index);
        self.source_modes = try allocator.alloc(SourceModeState, config.maximum_source_count);
        @memset(self.source_modes.?, .{});
        self.plan_metric_handles = try allocator.alloc(u64, config.maximum_plan_metric_count);
        @memset(self.plan_metric_handles.?, 0);
        self.planned_rule_flags = try allocator.alloc(bool, config.maximum_rule_count);
        @memset(self.planned_rule_flags.?, false);
    }

    fn residentByteCount(self: *const AllocationSet) u64 {
        var total: u64 = @sizeOf(Session);
        if (self.active) |bank| total += bankBytes(&bank);
        if (self.staging) |bank| total += bankBytes(&bank);
        if (self.requested) |value| total += sliceBytes(u32, value.len);
        if (self.requested_flags) |value| total += sliceBytes(bool, value.len);
        if (self.observation_slots) |value| total += sliceBytes(u32, value.len);
        if (self.observations) |value| {
            total += sliceBytes(protocol.ObservationInput, value.len);
        }
        if (self.gpu_active) |value| total += sliceBytes(GpuRecord, value.len);
        if (self.gpu_staging) |value| total += sliceBytes(GpuRecord, value.len);
        if (self.gpu_index) |value| total += sliceBytes(u32, value.len);
        if (self.gpu_staging_index) |value| total += sliceBytes(u32, value.len);
        if (self.gpu_luid_index) |value| total += sliceBytes(u32, value.len);
        if (self.gpu_staging_luid_index) |value| total += sliceBytes(u32, value.len);
        if (self.gpu_key_index) |value| total += sliceBytes(u32, value.len);
        if (self.gpu_staging_key_index) |value| total += sliceBytes(u32, value.len);
        if (self.gpu_identity_scratch) |value| total += sliceBytes(GpuIdentity, value.len);
        if (self.gpu_identity_index) |value| total += sliceBytes(u32, value.len);
        if (self.gpu_identity_luid_index) |value| total += sliceBytes(u32, value.len);
        if (self.gpu_identity_key_index) |value| total += sliceBytes(u32, value.len);
        if (self.source_modes) |value| total += sliceBytes(SourceModeState, value.len);
        if (self.plan_metric_handles) |value| total += sliceBytes(u64, value.len);
        if (self.planned_rule_flags) |value| total += sliceBytes(bool, value.len);
        return total;
    }

    fn deinit(self: *AllocationSet, allocator: std.mem.Allocator) void {
        if (self.active) |*bank| deinitBank(allocator, bank);
        if (self.staging) |*bank| deinitBank(allocator, bank);
        if (self.requested) |value| allocator.free(value);
        if (self.requested_flags) |value| allocator.free(value);
        if (self.observation_slots) |value| allocator.free(value);
        if (self.observations) |value| allocator.free(value);
        if (self.gpu_active) |value| allocator.free(value);
        if (self.gpu_staging) |value| allocator.free(value);
        if (self.gpu_index) |value| allocator.free(value);
        if (self.gpu_staging_index) |value| allocator.free(value);
        if (self.gpu_luid_index) |value| allocator.free(value);
        if (self.gpu_staging_luid_index) |value| allocator.free(value);
        if (self.gpu_key_index) |value| allocator.free(value);
        if (self.gpu_staging_key_index) |value| allocator.free(value);
        if (self.gpu_identity_scratch) |value| allocator.free(value);
        if (self.gpu_identity_index) |value| allocator.free(value);
        if (self.gpu_identity_luid_index) |value| allocator.free(value);
        if (self.gpu_identity_key_index) |value| allocator.free(value);
        if (self.source_modes) |value| allocator.free(value);
        if (self.plan_metric_handles) |value| allocator.free(value);
        if (self.planned_rule_flags) |value| allocator.free(value);
        self.* = .{};
    }

    fn takeActive(self: *AllocationSet) catalog.Bank {
        const value = self.active.?;
        self.active = null;
        return value;
    }

    fn takeStaging(self: *AllocationSet) catalog.Bank {
        const value = self.staging.?;
        self.staging = null;
        return value;
    }

    fn takeRequested(self: *AllocationSet) []u32 {
        const value = self.requested.?;
        self.requested = null;
        return value;
    }

    fn takeObservations(self: *AllocationSet) []protocol.ObservationInput {
        const value = self.observations.?;
        self.observations = null;
        return value;
    }

    fn takeRequestedFlags(self: *AllocationSet) []bool {
        const value = self.requested_flags.?;
        self.requested_flags = null;
        return value;
    }

    fn takeObservationSlots(self: *AllocationSet) []u32 {
        const value = self.observation_slots.?;
        self.observation_slots = null;
        return value;
    }

    fn takeGpuActive(self: *AllocationSet) []GpuRecord {
        const value = self.gpu_active.?;
        self.gpu_active = null;
        return value;
    }

    fn takeGpuStaging(self: *AllocationSet) []GpuRecord {
        const value = self.gpu_staging.?;
        self.gpu_staging = null;
        return value;
    }

    fn takeGpuIndex(self: *AllocationSet) []u32 {
        const value = self.gpu_index.?;
        self.gpu_index = null;
        return value;
    }

    fn takeGpuStagingIndex(self: *AllocationSet) []u32 {
        const value = self.gpu_staging_index.?;
        self.gpu_staging_index = null;
        return value;
    }

    fn takeGpuLuidIndex(self: *AllocationSet) []u32 {
        const value = self.gpu_luid_index.?;
        self.gpu_luid_index = null;
        return value;
    }

    fn takeGpuStagingLuidIndex(self: *AllocationSet) []u32 {
        const value = self.gpu_staging_luid_index.?;
        self.gpu_staging_luid_index = null;
        return value;
    }

    fn takeGpuKeyIndex(self: *AllocationSet) []u32 {
        const value = self.gpu_key_index.?;
        self.gpu_key_index = null;
        return value;
    }

    fn takeGpuStagingKeyIndex(self: *AllocationSet) []u32 {
        const value = self.gpu_staging_key_index.?;
        self.gpu_staging_key_index = null;
        return value;
    }

    fn takeGpuIdentityScratch(self: *AllocationSet) []GpuIdentity {
        const value = self.gpu_identity_scratch.?;
        self.gpu_identity_scratch = null;
        return value;
    }

    fn takeGpuIdentityIndex(self: *AllocationSet) []u32 {
        const value = self.gpu_identity_index.?;
        self.gpu_identity_index = null;
        return value;
    }

    fn takeGpuIdentityLuidIndex(self: *AllocationSet) []u32 {
        const value = self.gpu_identity_luid_index.?;
        self.gpu_identity_luid_index = null;
        return value;
    }

    fn takeGpuIdentityKeyIndex(self: *AllocationSet) []u32 {
        const value = self.gpu_identity_key_index.?;
        self.gpu_identity_key_index = null;
        return value;
    }

    fn takeSourceModes(self: *AllocationSet) []SourceModeState {
        const value = self.source_modes.?;
        self.source_modes = null;
        return value;
    }

    fn takePlanMetricHandles(self: *AllocationSet) []u64 {
        const value = self.plan_metric_handles.?;
        self.plan_metric_handles = null;
        return value;
    }

    fn takePlannedRuleFlags(self: *AllocationSet) []bool {
        const value = self.planned_rule_flags.?;
        self.planned_rule_flags = null;
        return value;
    }
};

fn allocateBank(
    allocator: std.mem.Allocator,
    config: *const protocol.Config,
) !catalog.Bank {
    const sources = try allocator.alloc(catalog.SourceRecord, config.maximum_source_count);
    errdefer allocator.free(sources);
    @memset(sources, .{});
    const rules = try allocator.alloc(catalog.RuleRecord, config.maximum_rule_count);
    errdefer allocator.free(rules);
    @memset(rules, .{});
    const metrics = try allocator.alloc(catalog.MetricRecord, config.maximum_metric_count);
    errdefer allocator.free(metrics);
    @memset(metrics, .{});
    const source_index = try allocator.alloc(u32, config.source_index_capacity);
    errdefer allocator.free(source_index);
    @memset(source_index, catalog.no_index);
    const rule_index = try allocator.alloc(u32, config.rule_index_capacity);
    errdefer allocator.free(rule_index);
    @memset(rule_index, catalog.no_index);
    const metric_index = try allocator.alloc(u32, config.metric_index_capacity);
    errdefer allocator.free(metric_index);
    @memset(metric_index, catalog.no_index);
    return .{
        .sources = sources,
        .rules = rules,
        .metrics = metrics,
        .source_index = source_index,
        .rule_index = rule_index,
        .metric_index = metric_index,
    };
}

fn deinitBank(allocator: std.mem.Allocator, bank: *catalog.Bank) void {
    allocator.free(bank.metric_index);
    allocator.free(bank.rule_index);
    allocator.free(bank.source_index);
    allocator.free(bank.metrics);
    allocator.free(bank.rules);
    allocator.free(bank.sources);
}

fn bankBytes(bank: *const catalog.Bank) u64 {
    return sliceBytes(catalog.SourceRecord, bank.sources.len) +
        sliceBytes(catalog.RuleRecord, bank.rules.len) +
        sliceBytes(catalog.MetricRecord, bank.metrics.len) +
        sliceBytes(u32, bank.source_index.len) +
        sliceBytes(u32, bank.rule_index.len) +
        sliceBytes(u32, bank.metric_index.len);
}

fn sliceBytes(comptime T: type, count: usize) u64 {
    return @as(u64, @sizeOf(T)) * @as(u64, @intCast(count));
}

pub fn lock(mutex: *windows.SRWLOCK) void {
    windows.ntdll.RtlAcquireSRWLockExclusive(mutex);
}

pub fn unlock(mutex: *windows.SRWLOCK) void {
    windows.ntdll.RtlReleaseSRWLockExclusive(mutex);
}
