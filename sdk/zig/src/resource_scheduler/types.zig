const std = @import("std");

pub const protocol_version: u32 = 11;
pub const no_scheduling_grade: u8 = 0xff;
pub const none_index: u32 = std.math.maxInt(u32);

pub const Tier = enum(u8) {
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

pub const SchedulingGrade = enum(u8) {
    freeze = 0,
    optimize = 1,
    normal = 2,
    extreme = 3,
};

pub const PendingState = enum(u8) {
    queued = 1,
    reserved = 2,
    active = 3,
    journal_pending = 4,
    effect_uncertain = 5,
};

pub const Source = enum(u8) {
    adapted_private = 0,
};

pub const authority_flag_typed_executor_proof: u8 = 1 << 0;
pub const authority_flag_host_self_executor: u8 = 1 << 1;
pub const authority_flags_host_self_executor: u8 =
    authority_flag_typed_executor_proof | authority_flag_host_self_executor;
pub const all_authority_flags: u8 = authority_flags_host_self_executor;

pub const ResourceExecutionAuthority = extern struct {
    source: u8,
    action: u8,
    action_route: u8,
    flags: u8,
    resource_slot: u32,
    resource_id: u32,
    reserved0: u32,
    ledger_instance_id: u64,
    source_snapshot_generation: u64,
    resource_generation: u64,
    resource_key: u64,
    owner_application_key: u64,
    owner_instance_id_low: u64,
    owner_instance_id_high: u64,
    owner_context_generation: u64,
    lease_generation: u64,
    binding_generation: u64,
    capability_generation: u64,
    scheduling_revision: u64,
    executor_id_low: u64,
    executor_id_high: u64,
    action_attempt_id_low: u64,
    action_attempt_id_high: u64,
    projection_epoch: u64,
};

pub const SelectionKind = enum(u8) {
    none = 0,
    action = 1,
    danger = 2,
};

pub const ResultCode = enum(u8) {
    unknown = 0,
    applied = 1,
    no_changes = 2,
    rejected = 3,
    timeout = 4,
    danger_line = 5,
    target_pool_danger = 6,
    ledger_drift_danger = 7,
};

pub const action_discard: u8 = 1 << 0;
pub const action_trim: u8 = 1 << 1;
pub const action_move_down: u8 = 1 << 2;
pub const action_move_up: u8 = 1 << 3;
pub const all_action_bits: u8 = action_discard | action_trim | action_move_down | action_move_up;
pub const destructive_action_bits: u8 = action_discard | action_trim | action_move_down;

pub const demand_required_now: u8 = 1 << 0;
pub const demand_ready_soon: u8 = 1 << 1;
pub const demand_preload_eager: u8 = 1 << 2;
pub const demand_preload_opportunistic: u8 = 1 << 3;
pub const all_demand_bits: u8 = demand_required_now | demand_ready_soon |
    demand_preload_eager | demand_preload_opportunistic;

pub const tier_vram_bit: u8 = 1 << @intFromEnum(Tier.vram);
pub const tier_physical_bit: u8 = 1 << @intFromEnum(Tier.physical_memory);
pub const tier_virtual_bit: u8 = 1 << @intFromEnum(Tier.virtual_memory);
pub const all_tier_bits: u8 = tier_vram_bit | tier_physical_bit | tier_virtual_bit;

pub const target_flag_include_vram: u8 = 1 << 0;
pub const target_flag_software_mode: u8 = 1 << 1;
pub const all_target_flags: u8 = target_flag_include_vram | target_flag_software_mode;
pub const revalidation_flag_snapshot_available: u8 = 1 << 0;
pub const all_revalidation_flags: u8 = revalidation_flag_snapshot_available;
pub const request_flag_emit_candidates: u8 = 1 << 0;
pub const all_request_flags: u8 = request_flag_emit_candidates;

pub const RevalidationReason = enum(u8) {
    allowed = 0,
    snapshot_unavailable = 1,
    snapshot_older = 2,
    identity_changed = 3,
    resource_changed = 4,
    required_now = 5,
    action_blocked = 6,
};

pub const danger_memory: u8 = 1 << 0;
pub const danger_virtual_memory: u8 = 1 << 1;
pub const danger_vram: u8 = 1 << 2;
pub const danger_paging_stall: u8 = 1 << 3;
pub const danger_system_interrupt: u8 = 1 << 4;
pub const all_danger_bits: u8 = danger_memory | danger_virtual_memory | danger_vram |
    danger_paging_stall | danger_system_interrupt;

pub const candidate_flag_queue_selected: u8 = 1 << 0;
pub const candidate_flag_quota_selected: u8 = 1 << 1;

pub const resource_scratch_assigned: u8 = 1 << 0;
pub const resource_scratch_valid: u8 = 1 << 1;
pub const resource_scratch_duplicate: u8 = 1 << 2;
pub const resource_scratch_quota_selected: u8 = 1 << 3;
pub const resource_scratch_action_selected: u8 = 1 << 4;

pub const CapacityInput = extern struct {
    total_vram_bytes: u64,
    free_vram_bytes: u64,
    total_physical_bytes: u64,
    free_physical_bytes: u64,
    total_virtual_bytes: u64,
    free_virtual_bytes: u64,
    fallback_vram_free_ratio: f64,
    fallback_physical_free_ratio: f64,
    fallback_virtual_free_ratio: f64,
    free_bytes_valid_mask: u8,
    reserved0: [7]u8,
};

pub const PlanRequest = extern struct {
    abi_version: u32,
    struct_size: u32,
    request_id: u64,
    scoring_config_generation: u64,
    now_monotonic_timestamp: u64,
    requested_action_mask: u8,
    enabled_tier_mask: u8,
    flags: u8,
    reserved0: u8,
    reserved1: u32,
};

pub const TargetInput = extern struct {
    abi_version: u32,
    struct_size: u32,
    target_key: u64,
    owner_application_key: u64,
    owner_instance_id_low: u64,
    owner_instance_id_high: u64,
    owner_context_generation: u64,
    lease_generation: u64,
    capability_generation: u64,
    base_score: f64,
    capacity: CapacityInput,
    private_resource_start: u32,
    private_resource_count: u32,
    policy_grade: i8,
    cpu_grade: u8,
    gpu_grade: u8,
    surface_state: u8,
    target_flags: u8,
    reserved0: [3]u8,
    reserved1: u32,
};

pub const PrivateResourceInput = extern struct {
    abi_version: u32,
    struct_size: u32,
    target_key: u64,
    size_bytes: u64,
    discard_authority: ResourceExecutionAuthority,
    trim_authority: ResourceExecutionAuthority,
    move_down_authority: ResourceExecutionAuthority,
    tier: u8,
    resource_kind: u8,
    recovery_kind: u8,
    granularity: u8,
    inapplicable_actions: u8,
    action_route: u8,
    demand_mask: u8,
    activity_score: u8,
    reserved0: u8,
    reserved1: [4]u8,
};

pub const PendingActionInput = extern struct {
    abi_version: u32,
    struct_size: u32,
    authority: ResourceExecutionAuthority,
    journal_transaction_id_low: u64,
    journal_transaction_id_high: u64,
    target_key: u64,
    size_bytes: u64,
    deadline_timestamp: u64,
    pending_generation: u64,
    state: u8,
    tier: u8,
    flags: u8,
    reserved0: [5]u8,
};

pub const CandidateOutput = extern struct {
    target_key: u64,
    authority: ResourceExecutionAuthority,
    size_bytes: u64,
    estimated_release_bytes: u64,
    owner_importance: f64,
    final_importance: f64,
    resource_input_index: u32,
    target_input_index: u32,
    rank: u32,
    resource_id: u32,
    tier: u8,
    resource_kind: u8,
    activity_score: u8,
    surface_state: u8,
    phase_order: u8,
    candidate_flags: u8,
    reserved0: u16,
};

pub const SelectionOutput = extern struct {
    target_key: u64,
    request_id: u64,
    authority: ResourceExecutionAuthority,
    size_bytes: u64,
    estimated_release_bytes: u64,
    configuration_generation: u64,
    final_importance: f64,
    candidate_index: u32,
    target_input_index: u32,
    tier: u8,
    activity_score: u8,
    reserved0: [6]u8,
};

pub const TargetPlanOutput = extern struct {
    target_key: u64,
    owner_application_key: u64,
    release_goal_bytes: [3]u64,
    candidate_count: u32,
    selected_action_count: u32,
    first_selection_index: u32,
    action_limit: u32,
    pressure_level: [3]u8,
    active_phase_order: u8,
    result_code: u8,
    danger_flags: u8,
    target_flags: u8,
    reserved0: [7]u8,
};

pub const GlobalSelectionOutput = extern struct {
    target_key: u64,
    owner_application_key: u64,
    candidate_index: u32,
    selection_index: u32,
    target_input_index: u32,
    danger_severity: u32,
    kind: u8,
    result_code: u8,
    danger_flags: u8,
    reserved0: u8,
    reserved1: u32,
};

pub const PlanSummary = extern struct {
    request_id: u64,
    scoring_config_generation: u64,
    target_count: u32,
    resource_count: u32,
    active_pending_count: u32,
    pending_duplicate_count: u32,
    candidate_capacity_required: u32,
    raw_candidate_count: u32,
    candidate_count: u32,
    selection_count: u32,
    invalid_resource_count: u32,
    required_now_count: u32,
    no_legal_action_count: u32,
    demand_blocked_count: u32,
    intrinsic_blocked_count: u32,
    capacity_blocked_count: u32,
    invalid_numeric_count: u32,
    pending_blocked_count: u32,
    danger_target_count: u32,
    reserved0: [2]u32,
};

pub const ReservationRequest = extern struct {
    abi_version: u32,
    struct_size: u32,
    now_monotonic_timestamp: u64,
    deadline_timestamp: u64,
    pending_generation: u64,
    configuration_generation: u64,
    danger_min_physical_after_vram_move_ratio: f64,
    danger_min_virtual_after_physical_move_ratio: f64,
    maximum_in_flight: u32,
    maximum_in_flight_per_target: u32,
};

pub const ReservationToken = extern struct {
    state_generation: u64,
    slot_generation: u64,
    pending_generation: u64,
    slot_index: u32,
    reserved0: u32,
};

pub const RevalidationInput = extern struct {
    abi_version: u32,
    struct_size: u32,
    authority: ResourceExecutionAuthority,
    target_key: u64,
    size_bytes: u64,
    policy_grade: i8,
    tier: u8,
    flags: u8,
    resource_kind: u8,
    recovery_kind: u8,
    granularity: u8,
    inapplicable_actions: u8,
    demand_mask: u8,
    activity_score: u8,
    reserved0: u8,
    reserved1: [6]u8,
};

pub const RevalidationOutput = extern struct {
    allowed: u8,
    reason: u8,
    reserved0: [6]u8,
};

pub const FeedbackRequest = extern struct {
    abi_version: u32,
    struct_size: u32,
    expected_request_id: u64,
    policy_result_code: u8,
    policy_danger_flags: u8,
    policy_accepted: u8,
    action_result_count: u8,
    reserved0: u32,
    reserved1: u64,
};

pub const ActionFeedbackInput = extern struct {
    authority: ResourceExecutionAuthority,
    request_id: u64,
    released_bytes: u64,
    resident_bytes: u64,
    action_gate_epoch: u64,
    detail_code: u16,
    status: u8,
    previous_tier: u8,
    current_tier: u8,
    reserved0: [3]u8,
};

pub const FeedbackOutput = extern struct {
    projected_capacity: CapacityInput,
    applied: u8,
    result_code: u8,
    danger_flags: u8,
    current_tier: u8,
    error_code: u16,
    reservation_disposition: u8,
    reserved0: u8,
    reserved1: u64,
};

pub const ResourceScratch = extern struct {
    target_input_index: u32,
    group_index: u32,
    flags: u8,
    reserved0: [7]u8,
};

pub const TargetScratch = extern struct {
    ledger_bytes: [3]u64,
    reserved_physical_bytes: u64,
    reserved_virtual_bytes: u64,
    raw_candidate_count: u32,
    candidate_count: u32,
    selected_action_count: u32,
    action_limit: u32,
    active_phase_order: u8,
    result_code: u8,
    danger_flags: u8,
    reserved0: u8,
};

pub const PlannerBuffers = struct {
    candidates: []CandidateOutput,
    selections: []SelectionOutput,
    target_plans: []TargetPlanOutput,
    resource_scratch: []ResourceScratch,
    target_scratch: []TargetScratch,
    resource_order: []u32,
    pending_scratch: []PendingActionInput,
};

pub const PlanError = error{
    InvalidConfig,
    InvalidRequest,
    InvalidTarget,
    InvalidCapacity,
    InvalidPending,
    BufferTooSmall,
    NumericOverflow,
    InvalidState,
    StateFull,
    StaleReservation,
    InvalidFeedback,
};

pub fn tierIndex(tier: u8) ?usize {
    return if (tier <= @intFromEnum(Tier.virtual_memory)) @as(usize, tier) else null;
}

pub fn isSingleAction(action: u8) bool {
    return action != 0 and action & (action - 1) == 0 and action & ~all_action_bits == 0;
}

pub fn validExecutionAuthority(authority: ResourceExecutionAuthority) bool {
    return authority.source == @intFromEnum(Source.adapted_private) and
        isSingleAction(authority.action) and
        authority.action & destructive_action_bits != 0 and
        validExecutionRouteAndProof(authority) and
        authority.resource_slot != std.math.maxInt(u32) and
        authority.resource_id != 0 and
        authority.reserved0 == 0 and
        authority.ledger_instance_id != 0 and
        authority.source_snapshot_generation != 0 and
        authority.resource_generation != 0 and
        authority.resource_key != 0 and
        authority.owner_application_key != 0 and
        (authority.owner_instance_id_low != 0 or authority.owner_instance_id_high != 0) and
        authority.owner_context_generation != 0 and
        authority.lease_generation != 0 and
        authority.binding_generation != 0 and
        authority.capability_generation != 0 and
        authority.scheduling_revision != 0 and
        (authority.executor_id_low != 0 or authority.executor_id_high != 0) and
        (authority.action_attempt_id_low != 0 or authority.action_attempt_id_high != 0) and
        authority.projection_epoch != 0;
}

fn validExecutionRouteAndProof(authority: ResourceExecutionAuthority) bool {
    return (authority.action_route == @intFromEnum(ActionRoute.adapter_handler) and
        authority.flags == authority_flag_typed_executor_proof) or
        (authority.action_route == @intFromEnum(ActionRoute.manager_direct) and
            authority.flags == authority_flags_host_self_executor);
}

pub fn sameResourceAuthority(
    left: ResourceExecutionAuthority,
    right: ResourceExecutionAuthority,
) bool {
    return sameResourceLocation(left, right) and
        left.action_route == right.action_route and
        left.resource_id == right.resource_id;
}

pub fn sameResourceLocation(
    left: ResourceExecutionAuthority,
    right: ResourceExecutionAuthority,
) bool {
    return left.source == right.source and
        left.resource_slot == right.resource_slot and
        left.ledger_instance_id == right.ledger_instance_id and
        left.resource_generation == right.resource_generation and
        left.resource_key == right.resource_key;
}

pub fn sameExecutionAuthority(
    left: ResourceExecutionAuthority,
    right: ResourceExecutionAuthority,
) bool {
    return std.meta.eql(left, right);
}

pub fn authorityForAction(
    resource: *const PrivateResourceInput,
    action: u8,
) ?ResourceExecutionAuthority {
    return switch (action) {
        action_discard => resource.discard_authority,
        action_trim => resource.trim_authority,
        action_move_down => resource.move_down_authority,
        else => null,
    };
}

pub fn primaryAuthority(resource: *const PrivateResourceInput) ?ResourceExecutionAuthority {
    inline for ([_]u8{ action_discard, action_trim, action_move_down }) |action| {
        const authority = authorityForAction(resource, action).?;
        if (validExecutionAuthority(authority)) return authority;
    }
    return null;
}

test "scheduler POD records remain stable" {
    try std.testing.expect(@sizeOf(ResourceExecutionAuthority) == 152);
    try std.testing.expect(@alignOf(ResourceExecutionAuthority) == 8);
    try std.testing.expect(@offsetOf(ResourceExecutionAuthority, "source") == 0);
    try std.testing.expect(@offsetOf(ResourceExecutionAuthority, "resource_slot") == 4);
    try std.testing.expect(@offsetOf(ResourceExecutionAuthority, "ledger_instance_id") == 16);
    try std.testing.expect(@offsetOf(ResourceExecutionAuthority, "owner_application_key") == 48);
    try std.testing.expect(@offsetOf(ResourceExecutionAuthority, "scheduling_revision") == 104);
    try std.testing.expect(@offsetOf(ResourceExecutionAuthority, "action_attempt_id_low") == 128);
    try std.testing.expect(@offsetOf(ResourceExecutionAuthority, "projection_epoch") == 144);
    try std.testing.expect(@sizeOf(CapacityInput) == 80);
    try std.testing.expect(@sizeOf(PlanRequest) == 40);
    try std.testing.expect(@sizeOf(TargetInput) % 8 == 0);
    try std.testing.expect(@sizeOf(PrivateResourceInput) % 8 == 0);
    try std.testing.expect(@sizeOf(PendingActionInput) % 8 == 0);
    try std.testing.expect(@sizeOf(CandidateOutput) % 8 == 0);
    try std.testing.expect(@sizeOf(ReservationToken) == 32);
    try std.testing.expect(@sizeOf(PendingActionInput) == 216);
    try std.testing.expect(@sizeOf(CandidateOutput) == 216);
    try std.testing.expect(@sizeOf(SelectionOutput) == 216);
    try std.testing.expect(@sizeOf(RevalidationInput) == 192);
    try std.testing.expect(@sizeOf(ActionFeedbackInput) == 192);
    try std.testing.expect(@sizeOf(ResourceScratch) == 16);
    try std.testing.expect(@sizeOf(RevalidationOutput) == 8);
    try std.testing.expect(@sizeOf(FeedbackOutput) % 8 == 0);
}
