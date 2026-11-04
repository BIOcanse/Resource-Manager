const std = @import("std");
const types = @import("types.zig");
const config_module = @import("config.zig");
const capacity_module = @import("capacity.zig");

pub const FilterReason = enum(u8) {
    allowed = 0,
    invalid_resource = 1,
    required_now = 2,
    no_legal_action = 3,
    demand_blocked = 4,
    intrinsic_blocked = 5,
    capacity_blocked = 6,
};

pub const FilterDecision = struct {
    allowed_actions: u8,
    reason: FilterReason,
};

pub fn validateRequest(request: *const types.PlanRequest, config: *const config_module.Config) bool {
    return request.abi_version == types.protocol_version and
        request.struct_size == @sizeOf(types.PlanRequest) and
        request.request_id != 0 and
        request.scoring_config_generation == config.generation and
        request.requested_action_mask & ~types.destructive_action_bits == 0 and
        request.enabled_tier_mask & ~types.all_tier_bits == 0 and
        request.flags & ~types.all_request_flags == 0 and
        request.reserved0 == 0 and
        request.reserved1 == 0;
}

pub fn validateTarget(target: *const types.TargetInput, config: *const config_module.Config) bool {
    return target.abi_version == types.protocol_version and
        target.struct_size == @sizeOf(types.TargetInput) and
        target.target_key != 0 and
        target.owner_application_key != 0 and
        (target.owner_instance_id_low != 0 or target.owner_instance_id_high != 0) and
        target.owner_context_generation != 0 and
        target.lease_generation != 0 and
        target.capability_generation != 0 and
        config_module.policyIndex(target.policy_grade) != null and
        config_module.schedulingMultiplier(config, target.cpu_grade) != null and
        config_module.schedulingMultiplier(config, target.gpu_grade) != null and
        target.surface_state <= @intFromEnum(types.SurfaceState.pure_background) and
        target.target_flags & ~types.all_target_flags == 0 and
        target.base_score >= config.base_score_min and
        target.base_score <= config.base_score_max and
        target.reserved0[0] == 0 and
        target.reserved0[1] == 0 and
        target.reserved0[2] == 0 and
        target.reserved1 == 0;
}

pub fn validResource(
    resource: *const types.PrivateResourceInput,
    target: *const types.TargetInput,
    projection_epoch: u64,
) bool {
    if (resource.abi_version != types.protocol_version or
        resource.struct_size != @sizeOf(types.PrivateResourceInput) or
        resource.target_key != target.target_key or
        resource.size_bytes == 0 or
        resource.tier > @intFromEnum(types.Tier.virtual_memory) or
        resource.resource_kind > @intFromEnum(types.ResourceKind.compute_buffer) or
        resource.recovery_kind > @intFromEnum(types.RecoveryKind.live_state) or
        resource.granularity > @intFromEnum(types.Granularity.not_applicable) or
        resource.inapplicable_actions & ~types.all_action_bits != 0 or
        resource.action_route > @intFromEnum(types.ActionRoute.adapter_handler) or
        resource.demand_mask & ~types.all_demand_bits != 0 or
        resource.reserved0 != 0 or
        !allZero(resource.reserved1[0..]))
    {
        return false;
    }
    if (resource.inapplicable_actions & types.destructive_action_bits ==
        types.destructive_action_bits)
    {
        return false;
    }

    var primary: ?types.ResourceExecutionAuthority = null;
    for ([_]u8{ types.action_discard, types.action_trim, types.action_move_down }) |action| {
        const authority = types.authorityForAction(resource, action).?;
        if (resource.inapplicable_actions & action != 0) {
            if (!allZero(std.mem.asBytes(&authority))) return false;
            continue;
        }
        if (!types.validExecutionAuthority(authority) or
            authority.source != @intFromEnum(types.Source.adapted_private) or
            authority.action != action or
            authority.action_route != resource.action_route or
            authority.owner_application_key != target.owner_application_key or
            authority.owner_instance_id_low != target.owner_instance_id_low or
            authority.owner_instance_id_high != target.owner_instance_id_high or
            authority.owner_context_generation != target.owner_context_generation or
            authority.lease_generation != target.lease_generation or
            authority.capability_generation != target.capability_generation or
            authority.projection_epoch != projection_epoch)
        {
            return false;
        }
        if (primary) |expected| {
            if (!types.sameResourceAuthority(expected, authority) or
                expected.source_snapshot_generation != authority.source_snapshot_generation or
                expected.owner_application_key != authority.owner_application_key or
                expected.owner_instance_id_low != authority.owner_instance_id_low or
                expected.owner_instance_id_high != authority.owner_instance_id_high or
                expected.owner_context_generation != authority.owner_context_generation or
                expected.lease_generation != authority.lease_generation or
                expected.binding_generation != authority.binding_generation or
                expected.capability_generation != authority.capability_generation or
                expected.scheduling_revision != authority.scheduling_revision or
                expected.executor_id_low != authority.executor_id_low or
                expected.executor_id_high != authority.executor_id_high or
                expected.projection_epoch != authority.projection_epoch)
            {
                return false;
            }
        } else {
            primary = authority;
        }
    }
    return primary != null;
}

pub fn validatePending(item: *const types.PendingActionInput) bool {
    return item.abi_version == types.protocol_version and
        item.struct_size == @sizeOf(types.PendingActionInput) and
        item.target_key != 0 and
        types.validExecutionAuthority(item.authority) and
        item.size_bytes != 0 and
        item.deadline_timestamp != 0 and
        item.pending_generation != 0 and
        item.tier <= @intFromEnum(types.Tier.virtual_memory) and
        item.state >= @intFromEnum(types.PendingState.queued) and
        item.state <= @intFromEnum(types.PendingState.effect_uncertain) and
        item.flags == 0 and
        ((item.state == @intFromEnum(types.PendingState.journal_pending) or
            item.state == @intFromEnum(types.PendingState.effect_uncertain)) ==
            ((item.journal_transaction_id_low | item.journal_transaction_id_high) != 0)) and
        allZero(item.reserved0[0..]);
}

pub fn allowedActions(
    config: *const config_module.Config,
    target: *const types.TargetInput,
    resource: *const types.PrivateResourceInput,
    resolved: *const capacity_module.Resolved,
    pressure_level: u8,
    phase_actions: u8,
) FilterDecision {
    const static_decision = staticallyAllowedActions(
        config,
        resource,
        target.policy_grade,
        phase_actions,
    );
    if (static_decision.reason != .allowed) return static_decision;

    const actions = static_decision.allowed_actions &
        runtimeFeasibilityMask(config, resource, resolved, pressure_level);
    if (actions == 0) return .{ .allowed_actions = 0, .reason = .capacity_blocked };

    return .{ .allowed_actions = actions, .reason = .allowed };
}

pub fn staticallyAllowedActions(
    config: *const config_module.Config,
    resource: *const types.PrivateResourceInput,
    policy_grade: i8,
    requested_actions: u8,
) FilterDecision {
    if (resource.demand_mask & types.demand_required_now != 0)
    {
        return .{ .allowed_actions = 0, .reason = .required_now };
    }

    var source_legal_actions = sourceTierLegalMask(resource.tier);
    if (resource.tier == @intFromEnum(types.Tier.physical_memory) and
        resource.action_route == @intFromEnum(types.ActionRoute.manager_direct) and
        resource.trim_authority.flags ==
            types.authority_flags_host_self_executor)
    {
        source_legal_actions |= types.action_trim;
    }
    var actions = source_legal_actions & types.all_action_bits &
        ~resource.inapplicable_actions & requested_actions;
    if (actions == 0) return .{ .allowed_actions = 0, .reason = .no_legal_action };

    actions &= demandMaskRule(resource.demand_mask, policy_grade);
    if (actions == 0) return .{ .allowed_actions = 0, .reason = .demand_blocked };

    actions &= intrinsicResourceMask(config, resource, policy_grade);
    if (actions == 0) return .{ .allowed_actions = 0, .reason = .intrinsic_blocked };

    return .{ .allowed_actions = actions, .reason = .allowed };
}

pub fn phaseActionMask(tier: u8) u8 {
    return switch (tier) {
        @intFromEnum(types.Tier.virtual_memory) => types.action_discard,
        @intFromEnum(types.Tier.physical_memory) => types.action_discard | types.action_trim | types.action_move_down,
        @intFromEnum(types.Tier.vram) => types.action_trim | types.action_discard | types.action_move_down,
        else => 0,
    };
}

pub fn sourceTierLegalMask(tier: u8) u8 {
    return switch (tier) {
        @intFromEnum(types.Tier.physical_memory) => types.action_discard |
            types.action_move_down | types.action_move_up,
        @intFromEnum(types.Tier.virtual_memory) => types.action_discard | types.action_move_up,
        @intFromEnum(types.Tier.vram) => types.action_discard |
            types.action_trim | types.action_move_down,
        else => 0,
    };
}

fn demandMaskRule(demand_mask: u8, policy_grade: i8) u8 {
    var allowed = types.all_action_bits;
    if (demand_mask & types.demand_ready_soon != 0) {
        allowed &= ~(types.action_discard | types.action_move_down);
    }
    if (demand_mask & types.demand_preload_eager != 0 and policy_grade != -4) {
        allowed &= ~types.action_discard;
    }
    return allowed;
}

fn intrinsicResourceMask(
    config: *const config_module.Config,
    resource: *const types.PrivateResourceInput,
    policy_grade: i8,
) u8 {
    if (resource.tier == @intFromEnum(types.Tier.vram) and
        (resource.resource_kind == @intFromEnum(types.ResourceKind.primary_data) or
            resource.resource_kind == @intFromEnum(types.ResourceKind.editing_document_state) or
            resource.resource_kind == @intFromEnum(types.ResourceKind.runtime_overhead)))
    {
        return 0;
    }

    var allowed = types.all_action_bits;
    if (policy_grade != -4 and
        resource.size_bytes >= config.large_resource_bytes and
        resource.activity_score >= config.high_activity_score)
    {
        allowed &= types.action_move_up;
    }

    if (resource.resource_kind == @intFromEnum(types.ResourceKind.primary_data) or
        resource.resource_kind == @intFromEnum(types.ResourceKind.editing_document_state) or
        resource.resource_kind == @intFromEnum(types.ResourceKind.runtime_overhead))
    {
        allowed &= ~types.action_move_up;
    }
    if (resource.resource_kind == @intFromEnum(types.ResourceKind.staging_buffer) and
        resource.tier == @intFromEnum(types.Tier.physical_memory))
    {
        allowed &= ~types.action_move_down;
    }
    if (resource.resource_kind == @intFromEnum(types.ResourceKind.runtime_overhead)) {
        allowed &= ~(types.action_discard | types.action_move_down);
    }
    if (resource.recovery_kind == @intFromEnum(types.RecoveryKind.live_state) and
        !(policy_grade == -4 and resource.action_route == @intFromEnum(types.ActionRoute.adapter_handler)))
    {
        allowed &= ~types.action_discard;
    }
    if (resource.granularity != @intFromEnum(types.Granularity.partial_usable)) {
        allowed &= ~types.action_trim;
    }
    return allowed;
}

fn runtimeFeasibilityMask(
    config: *const config_module.Config,
    resource: *const types.PrivateResourceInput,
    resolved: *const capacity_module.Resolved,
    pressure_level: u8,
) u8 {
    var allowed = types.all_action_bits;
    if (resource.tier == @intFromEnum(types.Tier.physical_memory)) {
        const minimum = if (pressure_level >= config.physical_to_virtual_desperate_pressure_level)
            config.physical_to_virtual_desperate_min_free_ratio
        else
            config.physical_to_virtual_min_free_ratio;
        if (resolved.free_ratio[@intFromEnum(types.Tier.virtual_memory)] < minimum) {
            allowed &= ~types.action_move_down;
        }
    }
    if (resource.tier == @intFromEnum(types.Tier.vram) and
        resolved.free_ratio[@intFromEnum(types.Tier.physical_memory)] <
            config.vram_to_physical_min_free_ratio)
    {
        allowed &= ~types.action_move_down;
    }
    return allowed;
}

fn allZero(values: []const u8) bool {
    for (values) |value| {
        if (value != 0) return false;
    }
    return true;
}
