const std = @import("std");
const protocol = @import("protocol.zig");
const ResultCode = @import("../../common/result_codes.zig").ResultCode;

const no_index = std.math.maxInt(u32);

const PolicySlot = struct {
    source_id: u32 = 0,
    priority: u32 = 0,
};

const Fingerprint = struct {
    low: u64 = 0,
    high: u64 = 0,
};

const PolicyBank = struct {
    policies: []PolicySlot,
    source_index: []u32,
    count: u32 = 0,
    fingerprint: Fingerprint = .{},

    fn init(allocator: std.mem.Allocator, config: *const protocol.Config) !PolicyBank {
        const policies = try allocator.alloc(PolicySlot, config.maximum_policy_count);
        errdefer allocator.free(policies);
        const source_index = try allocator.alloc(u32, config.policy_index_capacity);
        errdefer allocator.free(source_index);
        var bank = PolicyBank{
            .policies = policies,
            .source_index = source_index,
        };
        bank.clear();
        return bank;
    }

    fn deinit(self: *PolicyBank, allocator: std.mem.Allocator) void {
        allocator.free(self.source_index);
        allocator.free(self.policies);
        self.* = undefined;
    }

    fn clear(self: *PolicyBank) void {
        @memset(self.policies, .{});
        @memset(self.source_index, no_index);
        self.count = 0;
        self.fingerprint = .{};
    }

    fn load(
        self: *PolicyBank,
        policies: []const protocol.SourcePolicyInput,
        required_source_mask: u64,
    ) ResultCode {
        self.clear();
        if (policies.len > self.policies.len) return .out_of_memory;

        var actual_source_mask: u64 = 0;
        var previous_priority: u32 = 0;
        for (policies, 0..) |policy, index| {
            if (!protocol.validPolicy(&policy) or policy.priority <= previous_priority) {
                return .invalid_argument;
            }
            previous_priority = policy.priority;
            const bit = protocol.sourceBit(policy.source_id) orelse return .invalid_argument;
            if ((actual_source_mask & bit) != 0) return .invalid_argument;
            actual_source_mask |= bit;
            self.policies[index] = .{
                .source_id = policy.source_id,
                .priority = policy.priority,
            };
            if (!insertSourceIndex(self, policy.source_id, @intCast(index))) {
                return .invalid_argument;
            }
        }
        if (actual_source_mask != required_source_mask) return .invalid_argument;
        self.count = @intCast(policies.len);
        self.fingerprint = policyFingerprint(policies);
        return .ok;
    }

    fn findSource(self: *const PolicyBank, source_id: u32) ?u32 {
        const mask = self.source_index.len - 1;
        var probe = hashSource(source_id) & mask;
        var remaining = self.source_index.len;
        while (remaining != 0) : (remaining -= 1) {
            const slot_index = self.source_index[probe];
            if (slot_index == no_index) return null;
            if (self.policies[slot_index].source_id == source_id) return slot_index;
            probe = (probe + 1) & mask;
        }
        return null;
    }
};

pub const Session = struct {
    allocator: std.mem.Allocator,
    mutex: std.atomic.Mutex = .unlocked,
    config: protocol.Config,
    active: PolicyBank,
    staging: PolicyBank,
    resident_byte_count: u64,
    policy_generation: u64 = 0,
    last_operation_epoch: u64 = 0,
    last_frame_epoch: u64 = 0,
    last_command_utc_ms: i64 = 0,
    state_revision: u64 = 1,

    pub fn create(config: *const protocol.Config) !*Session {
        if (!protocol.validConfig(config)) return error.InvalidConfiguration;
        const resident = residentByteCount(config) catch return error.InvalidConfiguration;
        if (resident > config.resident_byte_budget) return error.InvalidConfiguration;
        const allocator = std.heap.page_allocator;
        const self = try allocator.create(Session);
        errdefer allocator.destroy(self);
        var active = try PolicyBank.init(allocator, config);
        errdefer active.deinit(allocator);
        var staging = try PolicyBank.init(allocator, config);
        errdefer staging.deinit(allocator);
        self.* = .{
            .allocator = allocator,
            .config = config.*,
            .active = active,
            .staging = staging,
            .resident_byte_count = resident,
        };
        return self;
    }

    pub fn destroy(self: *Session) void {
        const allocator = self.allocator;
        self.staging.deinit(allocator);
        self.active.deinit(allocator);
        allocator.destroy(self);
    }

    pub fn reconfigure(self: *Session, config: *const protocol.Config) ResultCode {
        lockMutex(&self.mutex);
        defer self.mutex.unlock();
        if (!protocol.validConfig(config)) return .abi_mismatch;
        if (config.generation <= self.config.generation) return .stale_frame;
        if (!sameShape(config, &self.config)) return .invalid_argument;
        if (self.resident_byte_count > config.resident_byte_budget) return .invalid_argument;
        self.config = config.*;
        self.advanceRevision();
        return .ok;
    }

    pub fn capacity(self: *const Session) protocol.Capacity {
        return .{
            .struct_size = @sizeOf(protocol.Capacity),
            .policy_capacity = self.config.maximum_policy_count,
            .observation_capacity = self.config.maximum_observation_count,
            .policy_index_capacity = self.config.policy_index_capacity,
            .resident_byte_count = self.resident_byte_count,
            .reserved = .{ 0, 0, 0, 0, 0 },
        };
    }

    pub fn replacePolicy(
        self: *Session,
        input: *const protocol.PolicyReplaceInput,
        policies: []const protocol.SourcePolicyInput,
    ) ResultCode {
        lockMutex(&self.mutex);
        defer self.mutex.unlock();
        if (!protocol.validPolicyReplace(input, &self.config)) return .abi_mismatch;
        if (policies.len != input.policy_count) return .invalid_argument;
        if (input.policy_generation <= self.policy_generation or
            input.operation_epoch <= self.last_operation_epoch)
        {
            return .stale_frame;
        }
        const load_result = self.staging.load(policies, self.config.required_source_mask);
        if (load_result != .ok) return load_result;
        const previous = self.active;
        self.active = self.staging;
        self.staging = previous;
        self.policy_generation = input.policy_generation;
        self.last_operation_epoch = input.operation_epoch;
        self.advanceRevision();
        return .ok;
    }

    pub fn resolve(
        self: *Session,
        input: *const protocol.ResolveInput,
        observations: []const protocol.ObservationInput,
        output: *protocol.ResolutionOutput,
    ) ResultCode {
        lockMutex(&self.mutex);
        defer self.mutex.unlock();
        if (!protocol.validResolve(input, &self.config, self.policy_generation)) return .abi_mismatch;
        if (observations.len != input.observation_count) return .invalid_argument;
        if (!protocol.emptyResolutionOutput(output)) return .abi_mismatch;
        if (input.frame_epoch <= self.last_frame_epoch or
            input.command_utc_ms < self.last_command_utc_ms)
        {
            return .stale_frame;
        }

        var aggregates = std.mem.zeroes([protocol.maximum_source_count]SourceAggregate);
        const validation = validateObservations(
            &self.active,
            self.config.required_source_mask,
            observations,
            &aggregates,
        );
        if (validation.code != .ok) return validation.code;

        var decision = Decision.unattributed();
        for (self.active.policies[0..self.active.count]) |policy| {
            const aggregate = &aggregates[policy.source_id - 1];
            switch (aggregate.status) {
                .none => return .invalid_argument,
                .no_match => {},
                .unavailable => {
                    const bit = protocol.sourceBit(policy.source_id).?;
                    if ((self.config.stop_on_unavailable_source_mask & bit) != 0) {
                        decision = Decision.unavailable(policy.source_id);
                        break;
                    }
                },
                .matched => {
                    if (aggregate.conflict) {
                        decision = Decision.conflict(policy.source_id, aggregate.conflictCount());
                    } else {
                        decision = Decision.matched(policy.source_id, aggregate);
                    }
                    break;
                },
            }
        }

        self.last_frame_epoch = input.frame_epoch;
        self.last_command_utc_ms = input.command_utc_ms;
        self.advanceRevision();
        output.* = decision.toOutput(self, input.frame_epoch, &validation);
        return .ok;
    }

    pub fn snapshot(self: *Session, output: *protocol.ResolutionSummary) ResultCode {
        lockMutex(&self.mutex);
        defer self.mutex.unlock();
        if (!protocol.emptySummary(output)) return .abi_mismatch;
        output.* = .{
            .abi_version = protocol.abi_version,
            .struct_size = @sizeOf(protocol.ResolutionSummary),
            .configuration_generation = self.config.generation,
            .policy_generation = self.policy_generation,
            .state_revision = self.state_revision,
            .last_operation_epoch = self.last_operation_epoch,
            .last_frame_epoch = self.last_frame_epoch,
            .last_command_utc_ms = self.last_command_utc_ms,
            .required_source_mask = self.config.required_source_mask,
            .stop_on_unavailable_source_mask = self.config.stop_on_unavailable_source_mask,
            .policy_fingerprint_low = self.active.fingerprint.low,
            .policy_fingerprint_high = self.active.fingerprint.high,
            .resident_byte_count = self.resident_byte_count,
            .policy_count = self.active.count,
            .flags = 0,
            .reserved = .{ 0, 0, 0 },
        };
        return .ok;
    }

    fn advanceRevision(self: *Session) void {
        self.state_revision +%= 1;
        if (self.state_revision == 0) self.state_revision = 1;
    }
};

const AggregateStatus = enum {
    none,
    no_match,
    unavailable,
    matched,
};

const SourceAggregate = struct {
    status: AggregateStatus = .none,
    observation_generation: u64 = 0,
    identity_handle: u64 = 0,
    display_name_handle: u64 = 0,
    software_kind: u32 = 0,
    root_handle: u64 = 0,
    evidence_mask: u64 = 0,
    current_identity_handle: u64 = 0,
    current_display_name_handle: u64 = 0,
    current_software_kind: u32 = 0,
    current_root_handle: u64 = 0,
    distinct_identity_count: u32 = 0,
    matched_row_count: u32 = 0,
    conflict: bool = false,

    fn conflictCount(self: *const SourceAggregate) u32 {
        return if (self.distinct_identity_count > 1)
            self.distinct_identity_count
        else
            @max(self.matched_row_count, 2);
    }
};

const ObservationValidation = struct {
    code: ResultCode = .ok,
    observed_mask: u64 = 0,
    unavailable_mask: u64 = 0,
    no_match_mask: u64 = 0,
    matched_mask: u64 = 0,
};

fn validateObservations(
    policy: *const PolicyBank,
    required_source_mask: u64,
    observations: []const protocol.ObservationInput,
    aggregates: *[protocol.maximum_source_count]SourceAggregate,
) ObservationValidation {
    var result = ObservationValidation{};
    var previous_source_id: u32 = 0;
    var previous_identity_handle: u64 = 0;
    var previous_evidence_mask: u64 = 0;
    for (observations) |observation| {
        if (!protocol.validObservation(&observation) or
            policy.findSource(observation.source_id) == null or
            observation.source_id < previous_source_id)
        {
            result.code = .invalid_argument;
            return result;
        }
        const source_changed = observation.source_id != previous_source_id;
        if (source_changed) {
            previous_source_id = observation.source_id;
            previous_identity_handle = 0;
            previous_evidence_mask = 0;
        }
        const status = protocol.observationStatus(observation.status).?;
        if (status == .matched) {
            if (observation.identity_handle < previous_identity_handle or
                (observation.identity_handle == previous_identity_handle and
                    observation.evidence_mask <= previous_evidence_mask))
            {
                result.code = .invalid_argument;
                return result;
            }
            previous_identity_handle = observation.identity_handle;
            previous_evidence_mask = observation.evidence_mask;
        } else if (!source_changed) {
            result.code = .invalid_argument;
            return result;
        }

        const bit = protocol.sourceBit(observation.source_id).?;
        result.observed_mask |= bit;
        var aggregate = &aggregates[observation.source_id - 1];
        if (aggregate.status == .none) {
            aggregate.status = switch (status) {
                .no_match => .no_match,
                .unavailable => .unavailable,
                .matched => .matched,
            };
            aggregate.observation_generation = observation.observation_generation;
        } else if (aggregate.observation_generation != observation.observation_generation or
            aggregate.status != .matched or status != .matched)
        {
            result.code = .invalid_argument;
            return result;
        }

        switch (status) {
            .no_match => result.no_match_mask |= bit,
            .unavailable => result.unavailable_mask |= bit,
            .matched => {
                result.matched_mask |= bit;
                aggregate.matched_row_count += 1;
                if (aggregate.current_identity_handle != observation.identity_handle) {
                    aggregate.current_identity_handle = observation.identity_handle;
                    aggregate.current_display_name_handle = observation.display_name_handle;
                    aggregate.current_software_kind = observation.software_kind;
                    aggregate.current_root_handle = observation.root_handle;
                    if (aggregate.identity_handle == 0) {
                        aggregate.identity_handle = observation.identity_handle;
                        aggregate.display_name_handle = observation.display_name_handle;
                        aggregate.software_kind = observation.software_kind;
                        aggregate.root_handle = observation.root_handle;
                        aggregate.evidence_mask = observation.evidence_mask;
                        aggregate.distinct_identity_count = 1;
                    } else {
                        aggregate.distinct_identity_count += 1;
                        aggregate.conflict = true;
                    }
                } else {
                    if (aggregate.current_display_name_handle != observation.display_name_handle or
                        aggregate.current_software_kind != observation.software_kind or
                        aggregate.current_root_handle != observation.root_handle)
                    {
                        aggregate.conflict = true;
                    }
                    if (aggregate.identity_handle == observation.identity_handle) {
                        aggregate.evidence_mask |= observation.evidence_mask;
                    }
                }
            },
        }
    }
    if (result.observed_mask != required_source_mask) result.code = .invalid_argument;
    return result;
}

const Decision = struct {
    status: protocol.ResolutionStatus,
    selected_source_id: u32 = 0,
    decision_source_id: u32 = 0,
    identity_handle: u64 = 0,
    display_name_handle: u64 = 0,
    software_kind: u32 = 0,
    conflict_count: u32 = 0,
    root_handle: u64 = 0,
    evidence_mask: u64 = 0,
    next_required_source_id: u32 = 0,

    fn unattributed() Decision {
        return .{ .status = .unattributed };
    }

    fn unavailable(source_id: u32) Decision {
        return .{
            .status = .unavailable,
            .decision_source_id = source_id,
            .next_required_source_id = source_id,
        };
    }

    fn conflict(source_id: u32, count: u32) Decision {
        return .{
            .status = .conflict,
            .decision_source_id = source_id,
            .conflict_count = count,
        };
    }

    fn matched(source_id: u32, aggregate: *const SourceAggregate) Decision {
        return .{
            .status = .matched,
            .selected_source_id = source_id,
            .decision_source_id = source_id,
            .identity_handle = aggregate.identity_handle,
            .display_name_handle = aggregate.display_name_handle,
            .software_kind = aggregate.software_kind,
            .root_handle = aggregate.root_handle,
            .evidence_mask = aggregate.evidence_mask,
        };
    }

    fn toOutput(
        self: *const Decision,
        session: *const Session,
        frame_epoch: u64,
        validation: *const ObservationValidation,
    ) protocol.ResolutionOutput {
        return .{
            .abi_version = protocol.abi_version,
            .struct_size = @sizeOf(protocol.ResolutionOutput),
            .configuration_generation = session.config.generation,
            .policy_generation = session.policy_generation,
            .policy_fingerprint_low = session.active.fingerprint.low,
            .policy_fingerprint_high = session.active.fingerprint.high,
            .frame_epoch = frame_epoch,
            .state_revision = session.state_revision,
            .selected_source_id = self.selected_source_id,
            .decision_source_id = self.decision_source_id,
            .status = @intFromEnum(self.status),
            .flags = 0,
            .identity_handle = self.identity_handle,
            .display_name_handle = self.display_name_handle,
            .software_kind = self.software_kind,
            .conflict_count = self.conflict_count,
            .root_handle = self.root_handle,
            .evidence_mask = self.evidence_mask,
            .observed_source_mask = validation.observed_mask,
            .unavailable_source_mask = validation.unavailable_mask,
            .no_match_source_mask = validation.no_match_mask,
            .matched_source_mask = validation.matched_mask,
            .next_required_source_id = self.next_required_source_id,
            .reserved_u32 = 0,
            .reserved = .{ 0, 0, 0 },
        };
    }
};

fn insertSourceIndex(bank: *PolicyBank, source_id: u32, slot_index: u32) bool {
    const mask = bank.source_index.len - 1;
    var probe = hashSource(source_id) & mask;
    var remaining = bank.source_index.len;
    while (remaining != 0) : (remaining -= 1) {
        if (bank.source_index[probe] == no_index) {
            bank.source_index[probe] = slot_index;
            return true;
        }
        if (bank.policies[bank.source_index[probe]].source_id == source_id) return false;
        probe = (probe + 1) & mask;
    }
    return false;
}

fn hashSource(source_id: u32) usize {
    var value: u64 = source_id;
    value ^= value >> 33;
    value *%= 0xff51_afd7_ed55_8ccd;
    value ^= value >> 33;
    return @truncate(value);
}

fn policyFingerprint(policies: []const protocol.SourcePolicyInput) Fingerprint {
    var low: u64 = 0xcbf2_9ce4_8422_2325;
    var high: u64 = 0x6c62_272e_07bb_0142;
    mix(&low, &high, policies.len);
    for (policies) |policy| {
        mix(&low, &high, policy.source_id);
        mix(&low, &high, policy.priority);
    }
    return .{ .low = low, .high = high };
}

fn mix(low: *u64, high: *u64, value: anytype) void {
    const converted: u64 = @intCast(value);
    low.* = (low.* ^ converted) *% 0x0000_0100_0000_01b3;
    high.* = (high.* ^ std.math.rotl(u64, converted +% 0x9e37_79b9_7f4a_7c15, 23)) *%
        0xc2b2_ae3d_27d4_eb4f;
}

fn residentByteCount(config: *const protocol.Config) !u64 {
    var total: u64 = @sizeOf(Session);
    total = try std.math.add(
        u64,
        total,
        try std.math.mul(u64, config.maximum_policy_count, @sizeOf(PolicySlot) * 2),
    );
    total = try std.math.add(
        u64,
        total,
        try std.math.mul(u64, config.policy_index_capacity, @sizeOf(u32) * 2),
    );
    return total;
}

fn sameShape(left: *const protocol.Config, right: *const protocol.Config) bool {
    return left.maximum_policy_count == right.maximum_policy_count and
        left.maximum_observation_count == right.maximum_observation_count and
        left.policy_index_capacity == right.policy_index_capacity and
        left.required_source_mask == right.required_source_mask;
}

pub fn lockMutex(mutex: *std.atomic.Mutex) void {
    while (!mutex.tryLock()) std.atomic.spinLoopHint();
}
