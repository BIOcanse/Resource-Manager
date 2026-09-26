const std = @import("std");

pub const abi_version: u32 = 0x0001_0000;

pub const ResourceKind = enum(u32) {
    unknown = 0,
    cpu = 1,
    gpu = 2,
};

pub const PlacementKind = enum(u32) {
    unknown = 0,
    cpu_sets = 1,
    cpu_affinity = 2,
    process_priority = 3,
    process_memory_priority = 4,
    gpu_preference = 5,
    gpu_runtime_rebuild = 6,
    gpu_shim_policy = 7,
};

pub const ObservationStatus = enum(u32) {
    found = 1,
    not_found_or_exited = 2,
    unavailable = 3,
    unchecked = 4,
};

pub const ActionDisposition = enum(u32) {
    apply = 1,
    restore = 2,
};

pub const FeedbackStatus = enum(u32) {
    applied = 1,
    restored = 2,
    already_satisfied = 3,
    ownership_lost = 4,
    retryable_failure = 5,
    invalid_receipt = 6,
};

pub const SlotState = enum(u32) {
    empty = 0,
    applied = 1,
    pending_apply = 2,
    pending_restore = 3,
    retry_wait = 4,
    blocked = 5,
};

pub const ConfigFlags = struct {
    pub const known: u64 = 0;
};

pub const CycleValid = struct {
    pub const observed_at: u64 = 1 << 0;
    pub const desired_complete: u64 = 1 << 1;
    pub const applied_complete: u64 = 1 << 2;
    pub const required: u64 = observed_at | desired_complete | applied_complete;
    pub const known: u64 = required;
};

pub const DesiredValid = struct {
    pub const identity: u64 = 1 << 0;
    pub const desired_digest: u64 = 1 << 1;
    pub const process_identity: u64 = 1 << 2;
    pub const required: u64 = identity | desired_digest;
    pub const known: u64 = identity | desired_digest | process_identity;
};

pub const DesiredFlags = struct {
    pub const override_external_state: u32 = 1 << 0;
    pub const known: u32 = override_external_state;
};

pub const AppliedValid = struct {
    pub const identity: u64 = 1 << 0;
    pub const receipt_digest: u64 = 1 << 1;
    pub const previous_digest: u64 = 1 << 2;
    pub const current_digest: u64 = 1 << 3;
    pub const process_identity: u64 = 1 << 4;
    pub const required: u64 = identity | receipt_digest | previous_digest;
    pub const known: u64 = identity | receipt_digest | previous_digest | current_digest | process_identity;
};

pub const AppliedFlags = struct {
    pub const payload_valid: u32 = 1 << 0;
    pub const known: u32 = payload_valid;
};

pub const FeedbackValid = struct {
    pub const observed_digest: u64 = 1 << 0;
    pub const known: u64 = observed_digest;
};

pub const ActionFlags = struct {
    pub const process_identity_valid: u32 = 1 << 0;
};

pub const ActionReason = struct {
    pub const desired_missing: u64 = 1 << 0;
    pub const desired_changed: u64 = 1 << 1;
    pub const receipt_missing: u64 = 1 << 2;
    pub const retry_due: u64 = 1 << 3;
    pub const already_restored: u64 = 1 << 4;
    pub const ownership_lost: u64 = 1 << 5;
    pub const observation_unavailable: u64 = 1 << 6;
    pub const invalid_receipt: u64 = 1 << 7;
};

pub const SnapshotFlags = struct {
    pub const next_wake_valid: u64 = 1 << 0;
};

pub const Config = extern struct {
    abi_version: u32,
    struct_size: u32,
    generation: u64,
    maximum_desired_count: u32,
    maximum_applied_count: u32,
    maximum_action_count: u32,
    maximum_state_count: u32,
    retry_delay_milliseconds: u64,
    action_timeout_milliseconds: u64,
    maximum_future_skew_milliseconds: u64,
    flags: u64,
    reserved: [2]u64,
};

pub const Capacity = extern struct {
    struct_size: u32,
    state_capacity: u32,
    action_capacity: u32,
    snapshot_state_capacity: u32,
    reserved: [4]u64,
};

pub const CycleInput = extern struct {
    abi_version: u32,
    struct_size: u32,
    configuration_generation: u64,
    cycle_epoch: u64,
    observed_at_milliseconds: u64,
    valid_mask: u64,
    desired_count: u32,
    applied_count: u32,
    action_capacity: u32,
    action_count: u32,
    next_wake_milliseconds: u64,
    state_revision: u64,
    reserved: [2]u64,
};

pub const DesiredInput = extern struct {
    struct_size: u32,
    flags: u32,
    target_key: u64,
    record_key: u64,
    resource_kind: u32,
    placement_kind: u32,
    desired_digest: u64,
    process_start_key: u64,
    process_id: u32,
    priority: u32,
    valid_mask: u64,
    reserved: u64,
};

pub const AppliedInput = extern struct {
    struct_size: u32,
    flags: u32,
    target_key: u64,
    record_key: u64,
    resource_kind: u32,
    placement_kind: u32,
    receipt_digest: u64,
    previous_digest: u64,
    current_digest: u64,
    process_start_key: u64,
    process_id: u32,
    observation_status: u32,
    valid_mask: u64,
    reserved: u64,
};

pub const ActionOutput = extern struct {
    struct_size: u32,
    disposition: u32,
    action_id: u64,
    target_key: u64,
    record_key: u64,
    resource_kind: u32,
    placement_kind: u32,
    desired_digest: u64,
    previous_digest: u64,
    process_start_key: u64,
    process_id: u32,
    flags: u32,
    reason_mask: u64,
    deadline_milliseconds: u64,
    priority: u32,
    reserved: u32,
};

pub const FeedbackInput = extern struct {
    struct_size: u32,
    status: u32,
    action_id: u64,
    target_key: u64,
    record_key: u64,
    completed_at_milliseconds: u64,
    system_error_code: u32,
    flags: u32,
    observed_digest: u64,
    valid_mask: u64,
};

pub const SnapshotHeader = extern struct {
    abi_version: u32,
    struct_size: u32,
    configuration_generation: u64,
    state_revision: u64,
    last_cycle_epoch: u64,
    next_action_id: u64,
    active_state_count: u32,
    pending_apply_count: u32,
    pending_restore_count: u32,
    blocked_count: u32,
    retry_wait_count: u32,
    state_output_count: u32,
    flags: u64,
    next_wake_milliseconds: u64,
    reserved: [2]u64,
};

pub const StateOutput = extern struct {
    struct_size: u32,
    state: u32,
    target_key: u64,
    record_key: u64,
    resource_kind: u32,
    placement_kind: u32,
    desired_digest: u64,
    applied_digest: u64,
    previous_digest: u64,
    pending_action_id: u64,
    retry_at_milliseconds: u64,
    last_cycle_epoch: u64,
    process_start_key: u64,
    process_id: u32,
    retry_count: u32,
    reserved: [2]u64,
};

pub fn validConfig(config: *const Config) bool {
    return config.abi_version == abi_version and
        config.struct_size == @sizeOf(Config) and
        config.generation != 0 and
        config.maximum_desired_count != 0 and
        config.maximum_applied_count != 0 and
        config.maximum_action_count != 0 and
        config.maximum_state_count != 0 and
        config.maximum_action_count == config.maximum_state_count and
        config.maximum_desired_count <= config.maximum_state_count and
        config.maximum_applied_count <= config.maximum_state_count and
        config.retry_delay_milliseconds != 0 and
        config.action_timeout_milliseconds != 0 and
        config.flags == ConfigFlags.known and
        allZero(&config.reserved);
}

pub fn validCycleHeader(cycle: *const CycleInput, config: *const Config) bool {
    return cycle.abi_version == abi_version and
        cycle.struct_size == @sizeOf(CycleInput) and
        cycle.configuration_generation == config.generation and
        cycle.cycle_epoch != 0 and
        cycle.observed_at_milliseconds != 0 and
        cycle.valid_mask == CycleValid.required and
        cycle.desired_count <= config.maximum_desired_count and
        cycle.applied_count <= config.maximum_applied_count and
        cycle.action_capacity <= config.maximum_action_count and
        cycle.action_count == 0 and
        cycle.next_wake_milliseconds == 0 and
        cycle.state_revision == 0 and
        allZero(&cycle.reserved);
}

pub fn validDesired(row: *const DesiredInput) bool {
    return row.struct_size == @sizeOf(DesiredInput) and
        row.target_key != 0 and row.record_key != 0 and row.desired_digest != 0 and
        row.flags & ~DesiredFlags.known == 0 and
        row.valid_mask & DesiredValid.required == DesiredValid.required and
        row.valid_mask & ~DesiredValid.known == 0 and
        validKnownResourceKind(row.resource_kind) and validKnownPlacementKind(row.placement_kind) and
        processIdentityConsistent(row.valid_mask, row.process_id, row.process_start_key, DesiredValid.process_identity) and
        row.reserved == 0;
}

pub fn validApplied(row: *const AppliedInput) bool {
    const payload_valid = row.flags & AppliedFlags.payload_valid != 0;
    const current_required = row.observation_status == @intFromEnum(ObservationStatus.found);
    return row.struct_size == @sizeOf(AppliedInput) and
        row.target_key != 0 and row.record_key != 0 and
        row.flags & ~AppliedFlags.known == 0 and
        (if (payload_valid)
            row.receipt_digest != 0 and row.previous_digest != 0 and
                row.valid_mask & AppliedValid.required == AppliedValid.required
        else
            row.receipt_digest == 0 and row.previous_digest == 0 and
                row.current_digest == 0 and row.valid_mask == AppliedValid.identity) and
        row.valid_mask & ~AppliedValid.known == 0 and
        (if (payload_valid)
            validKnownResourceKind(row.resource_kind) and validKnownPlacementKind(row.placement_kind)
        else
            row.resource_kind == @intFromEnum(ResourceKind.unknown) and
                row.placement_kind == @intFromEnum(PlacementKind.unknown)) and
        validObservationStatus(row.observation_status) and
        (!payload_valid or !current_required or
            (row.valid_mask & AppliedValid.current_digest != 0 and row.current_digest != 0)) and
        processIdentityConsistent(row.valid_mask, row.process_id, row.process_start_key, AppliedValid.process_identity) and
        row.reserved == 0;
}

pub fn validFeedback(row: *const FeedbackInput) bool {
    return row.struct_size == @sizeOf(FeedbackInput) and
        row.action_id != 0 and row.target_key != 0 and row.record_key != 0 and
        row.completed_at_milliseconds != 0 and row.flags == 0 and
        row.valid_mask & ~FeedbackValid.known == 0 and
        validFeedbackStatus(row.status);
}

pub fn sameIdentity(target_a: u64, record_a: u64, target_b: u64, record_b: u64) bool {
    return target_a == target_b and record_a == record_b;
}

fn validKnownResourceKind(value: u32) bool {
    return value == @intFromEnum(ResourceKind.cpu) or value == @intFromEnum(ResourceKind.gpu);
}

fn validKnownPlacementKind(value: u32) bool {
    return value >= @intFromEnum(PlacementKind.cpu_sets) and
        value <= @intFromEnum(PlacementKind.gpu_shim_policy);
}

fn validObservationStatus(value: u32) bool {
    return value >= @intFromEnum(ObservationStatus.found) and
        value <= @intFromEnum(ObservationStatus.unchecked);
}

fn validFeedbackStatus(value: u32) bool {
    return value >= @intFromEnum(FeedbackStatus.applied) and
        value <= @intFromEnum(FeedbackStatus.invalid_receipt);
}

fn processIdentityConsistent(valid_mask: u64, process_id: u32, start_key: u64, bit: u64) bool {
    const valid = valid_mask & bit != 0;
    return if (valid) process_id != 0 and start_key != 0 else process_id == 0 and start_key == 0;
}

fn allZero(values: []const u64) bool {
    for (values) |value| if (value != 0) return false;
    return true;
}

comptime {
    if (@sizeOf(Config) != 80) @compileError("placement Config ABI size changed");
    if (@sizeOf(Capacity) != 48) @compileError("placement Capacity ABI size changed");
    if (@sizeOf(CycleInput) != 88) @compileError("placement CycleInput ABI size changed");
    if (@sizeOf(DesiredInput) != 72) @compileError("placement DesiredInput ABI size changed");
    if (@sizeOf(AppliedInput) != 88) @compileError("placement AppliedInput ABI size changed");
    if (@sizeOf(ActionOutput) != 96) @compileError("placement ActionOutput ABI size changed");
    if (@sizeOf(FeedbackInput) != 64) @compileError("placement FeedbackInput ABI size changed");
    if (@sizeOf(SnapshotHeader) != 96) @compileError("placement SnapshotHeader ABI size changed");
    if (@sizeOf(StateOutput) != 112) @compileError("placement StateOutput ABI size changed");
}
