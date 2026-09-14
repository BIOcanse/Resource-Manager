const std = @import("std");

pub const abi_version: u32 = 0x0003_0000;

pub const ConfigFlags = struct {
    pub const known: u64 = 0;
};

pub const SourceFlags = struct {
    pub const persistent: u32 = 1 << 0;
    pub const explicit_interval: u32 = 1 << 1;
    pub const known: u32 = persistent | explicit_interval;
};

pub const PlanFlags = struct {
    pub const active: u64 = 1 << 0;
    pub const due_items: u64 = 1 << 1;
    pub const next_wake_valid: u64 = 1 << 2;
    pub const expired_sources: u64 = 1 << 3;
    pub const known: u64 = active | due_items | next_wake_valid | expired_sources;
};

pub const PlanRequestFlags = struct {
    pub const known: u64 = 0;
};

pub const DueItemFlags = struct {
    pub const known: u32 = 0;
};

pub const ItemStateFlags = DueItemFlags;

pub const CompletionFlags = struct {
    pub const active: u64 = 1 << 0;
    pub const next_wake_valid: u64 = 1 << 1;
    pub const known: u64 = active | next_wake_valid;
};

pub const SnapshotFlags = CompletionFlags;

pub const TrackValid = struct {
    pub const command_at: u64 = 1 << 0;
    pub const observed_at: u64 = 1 << 1;
    pub const source_identity: u64 = 1 << 2;
    pub const items: u64 = 1 << 3;
    pub const explicit_interval: u64 = 1 << 4;
    pub const required: u64 = command_at | observed_at | source_identity | items;
    pub const known: u64 = required | explicit_interval;
};

pub const RemoveValid = struct {
    pub const command_at: u64 = 1 << 0;
    pub const source_identity: u64 = 1 << 1;
    pub const required: u64 = command_at | source_identity;
    pub const known: u64 = required;
};

pub const PlanValid = struct {
    pub const command_at: u64 = 1 << 0;
    pub const required: u64 = command_at;
    pub const known: u64 = required;
};

pub const CompletionValid = struct {
    pub const command_at: u64 = 1 << 0;
    pub const completed_at: u64 = 1 << 1;
    pub const items: u64 = 1 << 2;
    pub const required: u64 = command_at | completed_at | items;
    pub const known: u64 = required;
};

pub const ControlValid = struct {
    pub const command_at: u64 = 1 << 0;
    pub const required: u64 = command_at;
    pub const known: u64 = required;
};

pub const CompletionStatus = enum(u32) {
    sampled = 1,
    failed = 2,
    skipped = 3,
};

pub const ExpiryReason = enum(u32) {
    ttl_elapsed = 1,
};

pub const Config = extern struct {
    abi_version: u32,
    struct_size: u32,
    generation: u64,
    maximum_source_count: u32,
    maximum_item_count: u32,
    maximum_membership_count: u32,
    maximum_due_item_count: u32,
    maximum_source_view_count: u32,
    maximum_expired_source_count: u32,
    default_interval_milliseconds: u64,
    minimum_interval_milliseconds: u64,
    active_ttl_milliseconds: u64,
    maximum_future_skew_milliseconds: u64,
    flags: u64,
    reserved_u64_0: u64,
    reserved_u64_1: u64,
    reserved_u64_2: u64,
    reserved_u32_0: u32,
    reserved_u32_1: u32,
};

pub const Capacity = extern struct {
    struct_size: u32,
    source_capacity: u32,
    item_capacity: u32,
    membership_capacity: u32,
    due_item_capacity: u32,
    source_view_capacity: u32,
    expired_source_capacity: u32,
    snapshot_source_capacity: u32,
    snapshot_item_capacity: u32,
    reserved_u32: u32,
    reserved: [3]u64,
};

pub const ItemReference = extern struct {
    struct_size: u32,
    flags: u32,
    item_handle: u64,
    reserved: [2]u64,
};

pub const TrackInput = extern struct {
    abi_version: u32,
    struct_size: u32,
    configuration_generation: u64,
    operation_epoch: u64,
    command_at_milliseconds: u64,
    observed_at_milliseconds: u64,
    source_handle: u64,
    explicit_interval_milliseconds: u64,
    valid_mask: u64,
    item_count: u32,
    flags: u32,
    reserved: [3]u64,
};

pub const RemoveInput = extern struct {
    abi_version: u32,
    struct_size: u32,
    configuration_generation: u64,
    operation_epoch: u64,
    command_at_milliseconds: u64,
    source_handle: u64,
    valid_mask: u64,
    flags: u32,
    reserved_u32: u32,
    reserved: [2]u64,
};

pub const ControlInput = extern struct {
    abi_version: u32,
    struct_size: u32,
    configuration_generation: u64,
    operation_epoch: u64,
    command_at_milliseconds: u64,
    valid_mask: u64,
    flags: u32,
    reserved_u32: u32,
    reserved: [2]u64,
};

pub const PlanHeader = extern struct {
    abi_version: u32,
    struct_size: u32,
    configuration_generation: u64,
    operation_epoch: u64,
    plan_epoch: u64,
    command_at_milliseconds: u64,
    valid_mask: u64,
    due_item_capacity: u32,
    source_view_capacity: u32,
    expired_source_capacity: u32,
    due_item_count: u32,
    source_view_count: u32,
    expired_source_count: u32,
    active_source_count: u32,
    active_item_count: u32,
    next_wake_milliseconds: u64,
    state_revision: u64,
    flags: u64,
    request_flags: u64,
    reserved: u64,
};

pub const DueItemOutput = extern struct {
    struct_size: u32,
    flags: u32,
    item_handle: u64,
    effective_interval_milliseconds: u64,
    last_sampled_milliseconds: u64,
    retry_at_milliseconds: u64,
    plan_epoch: u64,
    source_count: u32,
    reserved_u32: u32,
    scheduled_due_milliseconds: u64,
};

pub const SourceViewOutput = extern struct {
    struct_size: u32,
    flags: u32,
    source_handle: u64,
    last_seen_milliseconds: u64,
    observed_interval_milliseconds: u64,
    effective_interval_milliseconds: u64,
    explicit_interval_milliseconds: u64,
    item_count: u32,
    reserved_u32: u32,
    reserved: u64,
};

pub const ExpiredSourceOutput = extern struct {
    struct_size: u32,
    reason: u32,
    source_handle: u64,
    last_seen_milliseconds: u64,
    source_flags: u32,
    item_count: u32,
    expired_at_milliseconds: u64,
    reserved: u64,
};

pub const CompletionInput = extern struct {
    abi_version: u32,
    struct_size: u32,
    configuration_generation: u64,
    operation_epoch: u64,
    plan_epoch: u64,
    command_at_milliseconds: u64,
    completed_at_milliseconds: u64,
    valid_mask: u64,
    item_count: u32,
    status: u32,
    flags: u32,
    reserved_u32: u32,
    reserved: [2]u64,
};

pub const CompletionOutput = extern struct {
    abi_version: u32,
    struct_size: u32,
    configuration_generation: u64,
    state_revision: u64,
    operation_epoch: u64,
    next_wake_milliseconds: u64,
    active_source_count: u32,
    active_item_count: u32,
    flags: u64,
    reserved: [2]u64,
};

pub const SnapshotHeader = extern struct {
    abi_version: u32,
    struct_size: u32,
    configuration_generation: u64,
    state_revision: u64,
    last_operation_epoch: u64,
    last_command_at_milliseconds: u64,
    last_plan_epoch: u64,
    next_wake_milliseconds: u64,
    active_source_count: u32,
    known_item_count: u32,
    membership_count: u32,
    persistent_source_count: u32,
    explicit_interval_source_count: u32,
    source_output_count: u32,
    item_output_count: u32,
    reserved_u32: u32,
    flags: u64,
    reserved: [2]u64,
};

pub const SourceStateOutput = SourceViewOutput;

pub const ItemStateOutput = extern struct {
    struct_size: u32,
    flags: u32,
    item_handle: u64,
    last_sampled_milliseconds: u64,
    retry_at_milliseconds: u64,
    effective_interval_milliseconds: u64,
    last_planned_epoch: u64,
    source_count: u32,
    reserved_u32: u32,
    scheduled_due_milliseconds: u64,
};

pub fn validConfig(config: *const Config) bool {
    return config.abi_version == abi_version and
        config.struct_size == @sizeOf(Config) and
        config.generation != 0 and
        config.maximum_source_count != 0 and
        config.maximum_item_count != 0 and
        config.maximum_membership_count >= config.maximum_source_count and
        config.maximum_membership_count >= config.maximum_item_count and
        config.maximum_due_item_count >= config.maximum_item_count and
        config.maximum_source_view_count >= config.maximum_source_count and
        config.maximum_expired_source_count >= config.maximum_source_count and
        config.minimum_interval_milliseconds != 0 and
        config.default_interval_milliseconds >= config.minimum_interval_milliseconds and
        config.active_ttl_milliseconds >= config.minimum_interval_milliseconds and
        config.flags == ConfigFlags.known and
        config.reserved_u64_0 == 0 and
        config.reserved_u64_1 == 0 and
        config.reserved_u64_2 == 0 and
        config.reserved_u32_0 == 0 and
        config.reserved_u32_1 == 0;
}

pub fn validTrackHeader(input: *const TrackInput, config: *const Config) bool {
    const explicit = input.flags & SourceFlags.explicit_interval != 0;
    const expected_valid = TrackValid.required |
        (if (explicit) TrackValid.explicit_interval else 0);
    return input.abi_version == abi_version and
        input.struct_size == @sizeOf(TrackInput) and
        input.configuration_generation == config.generation and
        input.operation_epoch != 0 and
        input.command_at_milliseconds != 0 and
        input.observed_at_milliseconds != 0 and
        input.source_handle != 0 and
        input.item_count != 0 and
        input.item_count <= config.maximum_item_count and
        input.item_count <= config.maximum_membership_count and
        input.flags & ~SourceFlags.known == 0 and
        input.valid_mask == expected_valid and
        (if (explicit)
            input.explicit_interval_milliseconds >= config.minimum_interval_milliseconds
        else
            input.explicit_interval_milliseconds == 0) and
        allZero(&input.reserved);
}

pub fn validItemReference(reference: *const ItemReference) bool {
    return reference.struct_size == @sizeOf(ItemReference) and
        reference.flags == 0 and
        reference.item_handle != 0 and
        allZero(&reference.reserved);
}

pub fn validRemove(input: *const RemoveInput, config: *const Config) bool {
    return input.abi_version == abi_version and
        input.struct_size == @sizeOf(RemoveInput) and
        input.configuration_generation == config.generation and
        input.operation_epoch != 0 and
        input.command_at_milliseconds != 0 and
        input.source_handle != 0 and
        input.valid_mask == RemoveValid.required and
        input.flags == 0 and input.reserved_u32 == 0 and
        allZero(&input.reserved);
}

pub fn validControl(input: *const ControlInput, config: *const Config) bool {
    return input.abi_version == abi_version and
        input.struct_size == @sizeOf(ControlInput) and
        input.configuration_generation == config.generation and
        input.operation_epoch != 0 and
        input.command_at_milliseconds != 0 and
        input.valid_mask == ControlValid.required and
        input.flags == 0 and input.reserved_u32 == 0 and
        allZero(&input.reserved);
}

pub fn validPlanHeader(header: *const PlanHeader, config: *const Config) bool {
    return header.abi_version == abi_version and
        header.struct_size == @sizeOf(PlanHeader) and
        header.configuration_generation == config.generation and
        header.operation_epoch != 0 and header.plan_epoch != 0 and
        header.command_at_milliseconds != 0 and
        header.valid_mask == PlanValid.required and
        header.due_item_capacity != 0 and header.source_view_capacity != 0 and
        header.expired_source_capacity != 0 and
        header.due_item_count == 0 and header.source_view_count == 0 and
        header.expired_source_count == 0 and header.active_source_count == 0 and
        header.active_item_count == 0 and header.next_wake_milliseconds == 0 and
        header.state_revision == 0 and header.flags == 0 and
        header.request_flags == PlanRequestFlags.known and
        header.reserved == 0;
}

pub fn validCompletion(input: *const CompletionInput, config: *const Config) bool {
    return input.abi_version == abi_version and
        input.struct_size == @sizeOf(CompletionInput) and
        input.configuration_generation == config.generation and
        input.operation_epoch != 0 and
        input.command_at_milliseconds != 0 and
        input.completed_at_milliseconds != 0 and
        input.valid_mask == CompletionValid.required and
        input.item_count != 0 and input.item_count <= config.maximum_item_count and
        validCompletionStatus(input.status) and
        input.flags == 0 and input.reserved_u32 == 0 and
        allZero(&input.reserved);
}

pub fn validEmptySnapshotHeader(header: *const SnapshotHeader) bool {
    return header.abi_version == abi_version and
        header.struct_size == @sizeOf(SnapshotHeader) and
        header.configuration_generation == 0 and header.state_revision == 0 and
        header.last_operation_epoch == 0 and header.last_command_at_milliseconds == 0 and
        header.last_plan_epoch == 0 and header.next_wake_milliseconds == 0 and
        header.active_source_count == 0 and header.known_item_count == 0 and
        header.membership_count == 0 and header.persistent_source_count == 0 and
        header.explicit_interval_source_count == 0 and header.source_output_count == 0 and
        header.item_output_count == 0 and header.reserved_u32 == 0 and
        header.flags == 0 and allZero(&header.reserved);
}

pub fn validCompletionStatus(value: u32) bool {
    return value >= @intFromEnum(CompletionStatus.sampled) and
        value <= @intFromEnum(CompletionStatus.skipped);
}

pub fn allZero(values: []const u64) bool {
    for (values) |value| if (value != 0) return false;
    return true;
}

comptime {
    if (@sizeOf(Config) != 112) @compileError("sampling subscription Config ABI size changed");
    if (@sizeOf(Capacity) != 64) @compileError("sampling subscription Capacity ABI size changed");
    if (@sizeOf(ItemReference) != 32) @compileError("sampling subscription ItemReference ABI size changed");
    if (@sizeOf(TrackInput) != 96) @compileError("sampling subscription TrackInput ABI size changed");
    if (@sizeOf(RemoveInput) != 72) @compileError("sampling subscription RemoveInput ABI size changed");
    if (@sizeOf(ControlInput) != 64) @compileError("sampling subscription ControlInput ABI size changed");
    if (@sizeOf(PlanHeader) != 120) @compileError("sampling subscription PlanHeader ABI size changed");
    if (@sizeOf(DueItemOutput) != 64) @compileError("sampling subscription DueItemOutput ABI size changed");
    if (@sizeOf(SourceViewOutput) != 64) @compileError("sampling subscription SourceViewOutput ABI size changed");
    if (@sizeOf(ExpiredSourceOutput) != 48) @compileError("sampling subscription ExpiredSourceOutput ABI size changed");
    if (@sizeOf(CompletionInput) != 88) @compileError("sampling subscription CompletionInput ABI size changed");
    if (@sizeOf(CompletionOutput) != 72) @compileError("sampling subscription CompletionOutput ABI size changed");
    if (@sizeOf(SnapshotHeader) != 112) @compileError("sampling subscription SnapshotHeader ABI size changed");
    if (@sizeOf(SourceStateOutput) != 64) @compileError("sampling subscription SourceStateOutput ABI size changed");
    if (@sizeOf(ItemStateOutput) != 64) @compileError("sampling subscription ItemStateOutput ABI size changed");
    _ = std;
}
