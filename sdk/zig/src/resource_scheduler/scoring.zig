const std = @import("std");
const types = @import("types.zig");
const config_module = @import("config.zig");

pub fn candidateImportance(
    value: *const config_module.Config,
    resource: *const types.PrivateResourceInput,
    target: *const types.TargetInput,
    action: u8,
) types.PlanError!f64 {
    const kind_index: usize = resource.resource_kind;
    const surface_index: usize = target.surface_state;
    const policy_index = config_module.policyIndex(target.policy_grade) orelse return error.InvalidTarget;
    const action_index = actionIndex(action) orelse return error.InvalidRequest;
    const schedule_multiplier = try schedulingDimensionMultiplier(value, resource, target);
    const result = sizeImportance(value, resource.size_bytes) *
        value.resource_kind_multiplier[kind_index] *
        value.surface_multiplier[surface_index] *
        value.policy_grade_multiplier[policy_index] *
        schedule_multiplier *
        value.action_kind_multiplier[action_index][kind_index] *
        activityMultiplier(value, resource.activity_score) *
        demandMultiplier(value, resource.demand_mask);
    if (!std.math.isFinite(result) or result <= 0) return error.NumericOverflow;
    return result;
}

pub fn sizeImportance(value: *const config_module.Config, size_bytes: u64) f64 {
    if (size_bytes == 0) return value.size_importance_min;
    const megabytes = @as(f64, @floatFromInt(size_bytes)) /
        @as(f64, @floatFromInt(value.bytes_per_megabyte));
    return std.math.clamp(
        value.size_importance_min + (@log2(megabytes + 1.0) / value.size_log_divisor),
        value.size_importance_min,
        value.size_importance_max,
    );
}

pub fn estimateReleaseBytes(
    value: *const config_module.Config,
    size_bytes: u64,
    action: u8,
) types.PlanError!u64 {
    if (!types.isSingleAction(action) or action & types.destructive_action_bits == 0) {
        return error.InvalidRequest;
    }
    if (action != types.action_trim) return size_bytes;
    const product = @as(u128, size_bytes) * value.trim_release_numerator;
    const quotient = product / value.trim_release_denominator;
    return @max(@as(u64, 1), @as(u64, @intCast(quotient)));
}

pub fn actionIndex(action: u8) ?usize {
    return switch (action) {
        types.action_discard => 0,
        types.action_trim => 1,
        types.action_move_down => 2,
        types.action_move_up => 3,
        else => null,
    };
}

pub fn activityMultiplier(value: *const config_module.Config, activity_score: u8) f64 {
    const normalized = @as(f64, @floatFromInt(activity_score)) / value.activity_normalizer;
    return value.activity_base_multiplier +
        (value.activity_quadratic_scale * normalized * normalized);
}

pub fn demandMultiplier(value: *const config_module.Config, demand_mask: u8) f64 {
    var multiplier: f64 = 1;
    if (demand_mask & types.demand_ready_soon != 0) multiplier *= value.demand_multiplier[0];
    if (demand_mask & types.demand_preload_eager != 0) multiplier *= value.demand_multiplier[1];
    if (demand_mask & types.demand_preload_opportunistic != 0) multiplier *= value.demand_multiplier[2];
    return multiplier;
}

fn schedulingDimensionMultiplier(
    value: *const config_module.Config,
    resource: *const types.PrivateResourceInput,
    target: *const types.TargetInput,
) types.PlanError!f64 {
    const gpu_dimension = resource.tier == @intFromEnum(types.Tier.vram) or
        (resource.tier == @intFromEnum(types.Tier.physical_memory) and
            (resource.resource_kind == @intFromEnum(types.ResourceKind.texture) or
                resource.resource_kind == @intFromEnum(types.ResourceKind.render_buffer) or
                resource.resource_kind == @intFromEnum(types.ResourceKind.render_surface)));
    return config_module.schedulingMultiplier(
        value,
        if (gpu_dimension) target.gpu_grade else target.cpu_grade,
    ) orelse error.InvalidTarget;
}
