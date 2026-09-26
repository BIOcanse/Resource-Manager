const std = @import("std");

pub const abi_version: u32 = 0x0001_0000;
pub const maximum_source_count: u32 = 64;

pub const ObservationStatus = enum(u32) {
    no_match = 1,
    unavailable = 2,
    matched = 3,
};

pub const ResolutionStatus = enum(u32) {
    unattributed = 1,
    matched = 2,
    conflict = 3,
    unavailable = 4,
};

pub const PolicyReplaceValid = struct {
    pub const policy_generation: u64 = 1 << 0;
    pub const operation_epoch: u64 = 1 << 1;
    pub const policies: u64 = 1 << 2;
    pub const required: u64 = policy_generation | operation_epoch | policies;
    pub const known: u64 = required;
};

pub const ResolveValid = struct {
    pub const policy_generation: u64 = 1 << 0;
    pub const frame_epoch: u64 = 1 << 1;
    pub const command_utc_ms: u64 = 1 << 2;
    pub const observed_at_utc_ms: u64 = 1 << 3;
    pub const observations: u64 = 1 << 4;
    pub const required: u64 = policy_generation |
        frame_epoch |
        command_utc_ms |
        observed_at_utc_ms |
        observations;
    pub const known: u64 = required;
};

pub const Config = extern struct {
    abi_version: u32,
    struct_size: u32,
    generation: u64,
    maximum_policy_count: u32,
    maximum_observation_count: u32,
    policy_index_capacity: u32,
    reserved_u32: u32,
    required_source_mask: u64,
    stop_on_unavailable_source_mask: u64,
    maximum_future_skew_ms: u64,
    resident_byte_budget: u64,
    flags: u64,
    reserved: [4]u64,
};

pub const Capacity = extern struct {
    struct_size: u32,
    policy_capacity: u32,
    observation_capacity: u32,
    policy_index_capacity: u32,
    resident_byte_count: u64,
    reserved: [5]u64,
};

pub const SourcePolicyInput = extern struct {
    struct_size: u32,
    source_id: u32,
    priority: u32,
    flags: u32,
    reserved: [4]u64,
};

pub const PolicyReplaceInput = extern struct {
    abi_version: u32,
    struct_size: u32,
    configuration_generation: u64,
    policy_generation: u64,
    operation_epoch: u64,
    policy_count: u32,
    reserved_u32: u32,
    valid_mask: u64,
    flags: u64,
    reserved: [2]u64,
};

pub const ResolveInput = extern struct {
    abi_version: u32,
    struct_size: u32,
    configuration_generation: u64,
    policy_generation: u64,
    frame_epoch: u64,
    command_utc_ms: i64,
    observed_at_utc_ms: i64,
    observation_count: u32,
    reserved_u32: u32,
    valid_mask: u64,
    flags: u64,
    reserved: [3]u64,
};

pub const ObservationInput = extern struct {
    struct_size: u32,
    source_id: u32,
    status: u32,
    flags: u32,
    identity_handle: u64,
    display_name_handle: u64,
    software_kind: u32,
    reserved_u32: u32,
    root_handle: u64,
    evidence_mask: u64,
    observation_generation: u64,
    reserved: [2]u64,
};

pub const ResolutionOutput = extern struct {
    abi_version: u32,
    struct_size: u32,
    configuration_generation: u64,
    policy_generation: u64,
    policy_fingerprint_low: u64,
    policy_fingerprint_high: u64,
    frame_epoch: u64,
    state_revision: u64,
    selected_source_id: u32,
    decision_source_id: u32,
    status: u32,
    flags: u32,
    identity_handle: u64,
    display_name_handle: u64,
    software_kind: u32,
    conflict_count: u32,
    root_handle: u64,
    evidence_mask: u64,
    observed_source_mask: u64,
    unavailable_source_mask: u64,
    no_match_source_mask: u64,
    matched_source_mask: u64,
    next_required_source_id: u32,
    reserved_u32: u32,
    reserved: [3]u64,
};

pub const ResolutionSummary = extern struct {
    abi_version: u32,
    struct_size: u32,
    configuration_generation: u64,
    policy_generation: u64,
    state_revision: u64,
    last_operation_epoch: u64,
    last_frame_epoch: u64,
    last_command_utc_ms: i64,
    required_source_mask: u64,
    stop_on_unavailable_source_mask: u64,
    policy_fingerprint_low: u64,
    policy_fingerprint_high: u64,
    resident_byte_count: u64,
    policy_count: u32,
    flags: u32,
    reserved: [3]u64,
};

pub fn validConfig(config: *const Config) bool {
    return config.abi_version == abi_version and
        config.struct_size == @sizeOf(Config) and
        config.generation != 0 and
        config.maximum_policy_count != 0 and
        config.maximum_policy_count <= maximum_source_count and
        config.maximum_observation_count != 0 and
        validIndexCapacity(config.policy_index_capacity, config.maximum_policy_count) and
        config.reserved_u32 == 0 and
        (config.stop_on_unavailable_source_mask & ~config.required_source_mask) == 0 and
        config.resident_byte_budget != 0 and
        config.flags == 0 and
        allZero(&config.reserved);
}

pub fn validPolicyReplace(input: *const PolicyReplaceInput, config: *const Config) bool {
    return input.abi_version == abi_version and
        input.struct_size == @sizeOf(PolicyReplaceInput) and
        input.configuration_generation == config.generation and
        input.policy_generation != 0 and
        input.operation_epoch != 0 and
        input.policy_count <= config.maximum_policy_count and
        input.policy_count == @popCount(config.required_source_mask) and
        input.reserved_u32 == 0 and
        input.valid_mask == PolicyReplaceValid.required and
        input.flags == 0 and
        allZero(&input.reserved);
}

pub fn validPolicy(input: *const SourcePolicyInput) bool {
    return input.struct_size == @sizeOf(SourcePolicyInput) and
        sourceBit(input.source_id) != null and
        input.priority != 0 and
        input.flags == 0 and
        allZero(&input.reserved);
}

pub fn validResolve(
    input: *const ResolveInput,
    config: *const Config,
    policy_generation: u64,
) bool {
    if (input.abi_version != abi_version or
        input.struct_size != @sizeOf(ResolveInput) or
        input.configuration_generation != config.generation or
        input.policy_generation != policy_generation or
        input.policy_generation == 0 or
        input.frame_epoch == 0 or
        input.command_utc_ms < 0 or
        input.observed_at_utc_ms < 0 or
        input.observation_count > config.maximum_observation_count or
        input.reserved_u32 != 0 or
        input.valid_mask != ResolveValid.required or
        input.flags != 0 or
        !allZero(&input.reserved))
    {
        return false;
    }
    const maximum_observed = std.math.add(
        i64,
        input.command_utc_ms,
        @as(i64, @intCast(config.maximum_future_skew_ms)),
    ) catch return false;
    return input.observed_at_utc_ms <= maximum_observed;
}

pub fn validObservation(input: *const ObservationInput) bool {
    const status = observationStatus(input.status) orelse return false;
    if (input.struct_size != @sizeOf(ObservationInput) or
        sourceBit(input.source_id) == null or
        input.flags != 0 or
        input.reserved_u32 != 0 or
        input.observation_generation == 0 or
        !allZero(&input.reserved))
    {
        return false;
    }
    return switch (status) {
        .no_match, .unavailable => input.identity_handle == 0 and
            input.display_name_handle == 0 and
            input.software_kind == 0 and
            input.root_handle == 0 and
            input.evidence_mask == 0,
        .matched => input.identity_handle != 0 and
            input.display_name_handle != 0 and
            input.software_kind != 0 and
            input.evidence_mask != 0,
    };
}

pub fn emptyResolutionOutput(output: *const ResolutionOutput) bool {
    return output.abi_version == abi_version and
        output.struct_size == @sizeOf(ResolutionOutput) and
        output.configuration_generation == 0 and
        output.policy_generation == 0 and
        output.policy_fingerprint_low == 0 and output.policy_fingerprint_high == 0 and
        output.frame_epoch == 0 and output.state_revision == 0 and
        output.selected_source_id == 0 and output.decision_source_id == 0 and
        output.status == 0 and output.flags == 0 and
        output.identity_handle == 0 and output.display_name_handle == 0 and
        output.software_kind == 0 and output.conflict_count == 0 and
        output.root_handle == 0 and output.evidence_mask == 0 and
        output.observed_source_mask == 0 and output.unavailable_source_mask == 0 and
        output.no_match_source_mask == 0 and output.matched_source_mask == 0 and
        output.next_required_source_id == 0 and output.reserved_u32 == 0 and
        allZero(&output.reserved);
}

pub fn emptySummary(output: *const ResolutionSummary) bool {
    return output.abi_version == abi_version and
        output.struct_size == @sizeOf(ResolutionSummary) and
        output.configuration_generation == 0 and output.policy_generation == 0 and
        output.state_revision == 0 and output.last_operation_epoch == 0 and
        output.last_frame_epoch == 0 and output.last_command_utc_ms == 0 and
        output.required_source_mask == 0 and output.stop_on_unavailable_source_mask == 0 and
        output.policy_fingerprint_low == 0 and output.policy_fingerprint_high == 0 and
        output.resident_byte_count == 0 and output.policy_count == 0 and
        output.flags == 0 and allZero(&output.reserved);
}

pub fn observationStatus(value: u32) ?ObservationStatus {
    return switch (value) {
        1 => .no_match,
        2 => .unavailable,
        3 => .matched,
        else => null,
    };
}

pub fn sourceBit(source_id: u32) ?u64 {
    if (source_id == 0 or source_id > maximum_source_count) return null;
    return @as(u64, 1) << @intCast(source_id - 1);
}

fn validIndexCapacity(index_capacity: u32, item_capacity: u32) bool {
    return index_capacity >= item_capacity and std.math.isPowerOfTwo(index_capacity);
}

pub fn allZero(values: anytype) bool {
    for (values.*) |value| if (value != 0) return false;
    return true;
}

comptime {
    if (@sizeOf(Config) != 104) @compileError("software resolution Config size changed");
    if (@sizeOf(Capacity) != 64) @compileError("software resolution Capacity size changed");
    if (@sizeOf(SourcePolicyInput) != 48) @compileError("software resolution SourcePolicyInput size changed");
    if (@sizeOf(PolicyReplaceInput) != 72) @compileError("software resolution PolicyReplaceInput size changed");
    if (@sizeOf(ResolveInput) != 96) @compileError("software resolution ResolveInput size changed");
    if (@sizeOf(ObservationInput) != 80) @compileError("software resolution ObservationInput size changed");
    if (@sizeOf(ResolutionOutput) != 176) @compileError("software resolution ResolutionOutput size changed");
    if (@sizeOf(ResolutionSummary) != 128) @compileError("software resolution ResolutionSummary size changed");
}
