pub const abi_version: u32 = 3;
pub const snapshot_schema_version: u8 = 11;

pub const ResultCode = enum(i32) {
    ok = 0,
    invalid_argument = 1,
    invalid_state = 2,
    buffer_too_small = 3,
    duplicate_resource_key = 4,
    duplicate_resource_id = 5,
    sequence_regression = 6,
    timestamp_regression = 7,
    snapshot_too_old = 8,
    snapshot_from_future = 9,
    same_sequence_conflict = 10,
    stale_resource_reference = 11,
    resource_not_found = 12,
    configuration_regression = 13,
    invalid_enum = 14,
    numeric_overflow = 15,
};

pub const ResourceTier = enum(u8) {
    vram = 0,
    physical_memory = 1,
    virtual_memory = 2,
};

pub const ResourceKind = enum(u8) {
    primary_data = 0,
    cache = 1,
    index = 2,
    model_weights = 3,
    media_resource = 4,
    editing_document_state = 5,
    staging_buffer = 6,
    temporary_compute_memory = 7,
    runtime_overhead = 8,
    render_surface = 9,
    texture = 10,
    render_buffer = 11,
    compute_buffer = 12,
};

pub const RecoveryKind = enum(u8) {
    disk_copy = 0,
    built_data = 1,
    live_state = 2,
};

pub const Granularity = enum(u8) {
    fully_loaded = 0,
    partial_usable = 1,
    not_applicable = 2,
};

pub const ActionRoute = enum(u8) {
    manager_direct = 0,
    adapter_handler = 1,
};

pub const SurfaceState = enum(u8) {
    foreground_focused = 0,
    foreground_unfocused = 1,
    background_window = 2,
    tray_background = 3,
    pure_background = 4,
};

pub const ActionStatus = enum(u8) {
    completed = 0,
    resource_not_found = 1,
    action_not_supported = 2,
    invalid_request = 3,
    resource_busy = 4,
    failed = 5,
};

pub const Action = enum(u8) {
    discard = 1,
    trim = 2,
    move_down = 4,
    move_up = 8,
};

pub const import_activity_flag: u8 = 1 << 0;
pub const import_demand_flag: u8 = 1 << 1;
pub const known_import_flags: u8 = import_activity_flag | import_demand_flag;
pub const known_action_mask: u8 = 0x0f;
pub const known_demand_mask: u8 = 0x0f;

pub const Config = extern struct {
    abi_version: u32,
    struct_size: u32,
    configuration_generation: u64,
    capacity: u32,
    maximum_snapshot_age_milliseconds: u32,
    maximum_future_skew_milliseconds: u32,
    reserved0: u32,
    activity_settlement_interval: u64,
    activity_increment: u8,
    activity_decay_numerator: u8,
    activity_decay_denominator: u8,
    reserved1: [5]u8,
};

pub const SnapshotInput = extern struct {
    abi_version: u32,
    struct_size: u32,
    owner_application_key: u64,
    owner_process_instance_key: u64,
    source_sequence: u64,
    captured_at_unix_milliseconds: i64,
    observed_at_monotonic: u64,
    now_unix_milliseconds: i64,
    owner_process_id: u32,
    schema_version: u8,
    surface_state: SurfaceState,
    import_flags: u8,
    reserved0: [9]u8,
};

pub const ResourceInput = extern struct {
    resource_key: u64,
    size_bytes: u64,
    resource_id: u32,
    tier: ResourceTier,
    resource_kind: ResourceKind,
    recovery_kind: RecoveryKind,
    granularity: Granularity,
    inapplicable_actions: u8,
    action_route: ActionRoute,
    reserved0: u8,
    activity_score: u8,
    demand_mask: u8,
    reserved1: [3]u8,
};

pub const ResourceRef = extern struct {
    slot_index: u32,
    slot_generation: u32,
    resource_key: u64,
};

pub const ActionFeedback = extern struct {
    abi_version: u32,
    struct_size: u32,
    resource: ResourceRef,
    action: Action,
    status: ActionStatus,
    current_tier: ResourceTier,
    preferred_gpu_kind: ResourceKind,
    reserved0: u32,
    resident_bytes: u64,
    released_bytes: u64,
};

pub const ResourceView = extern struct {
    resource: ResourceRef,
    ledger_generation: u64,
    value: ResourceInput,
};

pub const SnapshotSummary = extern struct {
    ledger_generation: u64,
    configuration_generation: u64,
    ledger_instance_id: u64,
    owner_application_key: u64,
    owner_process_instance_key: u64,
    source_sequence: u64,
    captured_at_unix_milliseconds: i64,
    observed_at_monotonic: u64,
    fingerprint: u64,
    owner_process_id: u32,
    resource_count: u32,
    surface_state: SurfaceState,
    has_snapshot: u8,
    reserved0: [6]u8,
};

pub fn validConfig(config: Config) bool {
    return config.abi_version == abi_version and
        config.struct_size == @sizeOf(Config) and
        config.configuration_generation != 0 and
        config.capacity != 0 and
        config.maximum_snapshot_age_milliseconds != 0 and
        config.activity_settlement_interval != 0 and
        config.activity_increment != 0 and
        config.activity_decay_denominator != 0 and
        config.activity_decay_numerator <= config.activity_decay_denominator and
        config.reserved0 == 0 and allZero(config.reserved1[0..]);
}

pub fn validSnapshotInput(input: SnapshotInput) bool {
    return input.abi_version == abi_version and
        input.struct_size == @sizeOf(SnapshotInput) and
        input.owner_application_key != 0 and
        input.owner_process_instance_key != 0 and
        input.owner_process_id != 0 and
        input.source_sequence != 0 and
        input.captured_at_unix_milliseconds > 0 and
        input.observed_at_monotonic != 0 and
        input.now_unix_milliseconds > 0 and
        input.schema_version == snapshot_schema_version and
        @intFromEnum(input.surface_state) <= @intFromEnum(SurfaceState.pure_background) and
        input.import_flags & ~known_import_flags == 0 and
        allZero(input.reserved0[0..]);
}

pub fn validResourceInput(input: ResourceInput) bool {
    return input.resource_key != 0 and input.resource_id != 0 and
        @intFromEnum(input.tier) <= @intFromEnum(ResourceTier.virtual_memory) and
        @intFromEnum(input.resource_kind) <= @intFromEnum(ResourceKind.compute_buffer) and
        @intFromEnum(input.recovery_kind) <= @intFromEnum(RecoveryKind.live_state) and
        @intFromEnum(input.granularity) <= @intFromEnum(Granularity.not_applicable) and
        @intFromEnum(input.action_route) <= @intFromEnum(ActionRoute.adapter_handler) and
        input.inapplicable_actions & ~known_action_mask == 0 and
        input.reserved0 == 0 and input.demand_mask & ~known_demand_mask == 0 and
        allZero(input.reserved1[0..]);
}

pub fn validActionFeedback(input: ActionFeedback) bool {
    return input.abi_version == abi_version and input.struct_size == @sizeOf(ActionFeedback) and
        input.resource.resource_key != 0 and input.resource.slot_generation != 0 and
        isKnownAction(input.action) and
        @intFromEnum(input.status) <= @intFromEnum(ActionStatus.failed) and
        @intFromEnum(input.current_tier) <= @intFromEnum(ResourceTier.virtual_memory) and
        @intFromEnum(input.preferred_gpu_kind) <= @intFromEnum(ResourceKind.compute_buffer) and
        input.reserved0 == 0;
}

fn isKnownAction(action: Action) bool {
    return switch (@intFromEnum(action)) {
        @intFromEnum(Action.discard),
        @intFromEnum(Action.trim),
        @intFromEnum(Action.move_down),
        @intFromEnum(Action.move_up),
        => true,
        else => false,
    };
}

fn allZero(values: []const u8) bool {
    for (values) |value| if (value != 0) return false;
    return true;
}

comptime {
    if (@sizeOf(Config) != 48) @compileError("private resource Config ABI drift");
    if (@sizeOf(SnapshotInput) != 72) @compileError("private resource SnapshotInput ABI drift");
    if (@sizeOf(ResourceInput) != 32) @compileError("private resource ResourceInput ABI drift");
    if (@sizeOf(ResourceRef) != 16) @compileError("private resource ResourceRef ABI drift");
    if (@sizeOf(ActionFeedback) != 48) @compileError("private resource ActionFeedback ABI drift");
    if (@sizeOf(ResourceView) != 56) @compileError("private resource ResourceView ABI drift");
    if (@sizeOf(SnapshotSummary) != 88) @compileError("private resource SnapshotSummary ABI drift");
}
