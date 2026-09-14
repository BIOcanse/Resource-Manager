const std = @import("std");
const types = @import("types.zig");

pub const Config = extern struct {
    abi_version: u32,
    struct_size: u32,
    generation: u64,
    bytes_per_megabyte: u64,
    size_importance_min: f64,
    size_importance_max: f64,
    size_log_divisor: f64,
    resource_kind_multiplier: [13]f64,
    surface_multiplier: [5]f64,
    policy_grade_multiplier: [5]f64,
    policy_grade_pressure: [5]u8,
    reserved0: [3]u8,
    scheduling_grade_multiplier: [4]f64,
    absent_scheduling_grade_multiplier: f64,
    action_kind_multiplier: [4][13]f64,
    activity_base_multiplier: f64,
    activity_quadratic_scale: f64,
    activity_normalizer: f64,
    demand_multiplier: [3]f64,
    large_resource_bytes: u64,
    high_activity_score: u8,
    physical_to_virtual_desperate_pressure_level: u8,
    reserved1: [6]u8,
    physical_to_virtual_min_free_ratio: f64,
    physical_to_virtual_desperate_min_free_ratio: f64,
    vram_to_physical_min_free_ratio: f64,
    pressure_free_ratio_threshold: [3]f64,
    target_free_ratio: [3]f64,
    desired_free_ratio_level_2_to_4: [3]f64,
    minimum_release_bytes: [4]u64,
    max_release_share: [4]f64,
    base_score_threshold: [3]f64,
    base_release_multiplier: [4]f64,
    base_score_min: f64,
    base_score_max: f64,
    trim_release_numerator: u32,
    trim_release_denominator: u32,
    max_actions_per_target: u32,
    reserved2: u32,
    strong_pressure_free_ratio: f64,
    danger_min_physical_after_vram_move_ratio: f64,
    danger_min_virtual_after_physical_move_ratio: f64,
    danger_severity_weight: [5]u8,
    reserved3: [3]u8,
};

pub fn validate(value: *const Config) bool {
    if (value.abi_version != types.protocol_version or
        value.struct_size != @sizeOf(Config) or
        value.generation == 0 or
        value.bytes_per_megabyte == 0 or
        !positiveFinite(value.size_importance_min) or
        !positiveFinite(value.size_importance_max) or
        value.size_importance_max < value.size_importance_min or
        !positiveFinite(value.size_log_divisor) or
        !positiveFinite(value.absent_scheduling_grade_multiplier) or
        !positiveFinite(value.activity_base_multiplier) or
        !nonnegativeFinite(value.activity_quadratic_scale) or
        !positiveFinite(value.activity_normalizer) or
        value.large_resource_bytes == 0 or
        value.physical_to_virtual_desperate_pressure_level < 1 or
        value.physical_to_virtual_desperate_pressure_level > 4 or
        value.trim_release_numerator == 0 or
        value.trim_release_denominator == 0 or
        value.trim_release_numerator > value.trim_release_denominator or
        value.max_actions_per_target == 0 or
        !ratio(value.strong_pressure_free_ratio) or
        !ratio(value.danger_min_physical_after_vram_move_ratio) or
        !ratio(value.danger_min_virtual_after_physical_move_ratio) or
        !ratio(value.physical_to_virtual_min_free_ratio) or
        !ratio(value.physical_to_virtual_desperate_min_free_ratio) or
        value.physical_to_virtual_desperate_min_free_ratio > value.physical_to_virtual_min_free_ratio or
        !ratio(value.vram_to_physical_min_free_ratio) or
        !finite(value.base_score_min) or
        !finite(value.base_score_max) or
        value.base_score_min < 0 or
        value.base_score_max <= value.base_score_min or
        !allZero(value.reserved0[0..]) or
        !allZero(value.reserved1[0..]) or
        value.reserved2 != 0 or
        !allZero(value.reserved3[0..]))
    {
        return false;
    }

    if (!positiveArray(value.resource_kind_multiplier[0..]) or
        !positiveArray(value.surface_multiplier[0..]) or
        !positiveArray(value.policy_grade_multiplier[0..]) or
        !positiveArray(value.scheduling_grade_multiplier[0..]) or
        !positiveArray(value.demand_multiplier[0..]) or
        !positiveArray(value.max_release_share[0..]) or
        !positiveArray(value.base_release_multiplier[0..]))
    {
        return false;
    }

    for (value.action_kind_multiplier) |row| {
        if (!positiveArray(row[0..])) return false;
    }
    for (value.policy_grade_pressure) |level| {
        if (level > 4) return false;
    }
    for (value.target_free_ratio) |item| {
        if (!ratio(item)) return false;
    }
    for (value.desired_free_ratio_level_2_to_4) |item| {
        if (!ratio(item) or item == 0) return false;
    }
    for (value.minimum_release_bytes) |item| {
        if (item == 0) return false;
    }
    for (value.max_release_share) |item| {
        if (item > 1) return false;
    }
    for (value.danger_severity_weight) |item| {
        if (item == 0) return false;
    }

    const pressure = value.pressure_free_ratio_threshold;
    if (!ratio(pressure[0]) or !ratio(pressure[1]) or !ratio(pressure[2]) or
        !(pressure[0] < pressure[1] and pressure[1] < pressure[2]))
    {
        return false;
    }

    const thresholds = value.base_score_threshold;
    if (!finite(thresholds[0]) or !finite(thresholds[1]) or !finite(thresholds[2]) or
        !(thresholds[0] > thresholds[1] and thresholds[1] > thresholds[2]) or
        thresholds[0] > value.base_score_max or thresholds[2] < value.base_score_min)
    {
        return false;
    }

    return true;
}

pub fn policyIndex(grade: i8) ?usize {
    return switch (grade) {
        -4 => 0,
        -3 => 1,
        -2 => 2,
        -1 => 3,
        0, 1 => 4,
        else => null,
    };
}

pub fn schedulingMultiplier(value: *const Config, grade: u8) ?f64 {
    if (grade == types.no_scheduling_grade) return value.absent_scheduling_grade_multiplier;
    return if (grade <= @intFromEnum(types.SchedulingGrade.extreme))
        value.scheduling_grade_multiplier[grade]
    else
        null;
}

fn finite(value: f64) bool {
    return std.math.isFinite(value);
}

fn positiveFinite(value: f64) bool {
    return finite(value) and value > 0;
}

fn nonnegativeFinite(value: f64) bool {
    return finite(value) and value >= 0;
}

fn ratio(value: f64) bool {
    return finite(value) and value >= 0 and value <= 1;
}

fn positiveArray(values: []const f64) bool {
    for (values) |value| {
        if (!positiveFinite(value)) return false;
    }
    return true;
}

fn allZero(values: []const u8) bool {
    for (values) |value| {
        if (value != 0) return false;
    }
    return true;
}
