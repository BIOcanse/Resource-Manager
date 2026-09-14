const std = @import("std");
const types = @import("types.zig");
const config_module = @import("config.zig");

pub const Resolved = extern struct {
    total_bytes: [3]u64,
    free_bytes: [3]u64,
    free_ratio: [3]f64,
};

pub fn resolve(input: *const types.CapacityInput) types.PlanError!Resolved {
    if (input.free_bytes_valid_mask & ~types.all_tier_bits != 0 or
        !validRatio(input.fallback_vram_free_ratio) or
        !validRatio(input.fallback_physical_free_ratio) or
        !validRatio(input.fallback_virtual_free_ratio) or
        !allZero(input.reserved0[0..]))
    {
        return error.InvalidCapacity;
    }

    const totals = [3]u64{
        input.total_vram_bytes,
        input.total_physical_bytes,
        input.total_virtual_bytes,
    };
    const supplied_free = [3]u64{
        input.free_vram_bytes,
        input.free_physical_bytes,
        input.free_virtual_bytes,
    };
    const fallbacks = [3]f64{
        input.fallback_vram_free_ratio,
        input.fallback_physical_free_ratio,
        input.fallback_virtual_free_ratio,
    };

    var result = Resolved{
        .total_bytes = totals,
        .free_bytes = undefined,
        .free_ratio = undefined,
    };
    var index: usize = 0;
    while (index < 3) : (index += 1) {
        const bit: u8 = @as(u8, 1) << @intCast(index);
        const has_free = input.free_bytes_valid_mask & bit != 0;
        const total = totals[index];
        if (has_free and ((total == 0 and supplied_free[index] != 0) or
            (total != 0 and supplied_free[index] > total)))
        {
            return error.InvalidCapacity;
        }

        const free = if (has_free)
            supplied_free[index]
        else
            deriveFreeBytes(total, fallbacks[index]);
        result.free_bytes[index] = free;
        result.free_ratio[index] = if (total == 0)
            fallbacks[index]
        else
            @as(f64, @floatFromInt(free)) / @as(f64, @floatFromInt(total));
    }
    return result;
}

pub fn pressureLevel(
    value: *const config_module.Config,
    free_ratio: f64,
    target_free_ratio: f64,
) u8 {
    if (free_ratio < value.pressure_free_ratio_threshold[0]) return 4;
    if (free_ratio < value.pressure_free_ratio_threshold[1]) return 3;
    if (free_ratio < value.pressure_free_ratio_threshold[2]) return 2;
    return if (free_ratio < target_free_ratio) 1 else 0;
}

pub fn releaseGoalBytes(
    value: *const config_module.Config,
    tier: u8,
    resolved: *const Resolved,
    base_score: f64,
    ledger_tier_bytes: u64,
) types.PlanError!u64 {
    const index = types.tierIndex(tier) orelse return error.InvalidTarget;
    const pressure = pressureLevel(value, resolved.free_ratio[index], value.target_free_ratio[index]);
    if (pressure == 0 or ledger_tier_bytes == 0) return 0;

    const total_bytes = if (resolved.total_bytes[index] == 0)
        ledger_tier_bytes
    else
        resolved.total_bytes[index];
    const desired = if (pressure == 1)
        value.target_free_ratio[index]
    else
        value.desired_free_ratio_level_2_to_4[pressure - 2];
    const free_gap = @max(@as(f64, 0), desired - resolved.free_ratio[index]);
    const release_multiplier = baseReleaseMultiplier(value, base_score);
    const raw_goal = try ceilProductToU64(total_bytes, free_gap * release_multiplier);
    const minimum_goal = value.minimum_release_bytes[pressure - 1];
    var max_goal = try ceilProductToU64(
        ledger_tier_bytes,
        value.max_release_share[pressure - 1] * release_multiplier,
    );
    if (max_goal == 0) max_goal = ledger_tier_bytes;

    const goal = @max(raw_goal, @min(minimum_goal, ledger_tier_bytes));
    return @min(goal, @min(max_goal, ledger_tier_bytes));
}

pub fn baseReleaseMultiplier(value: *const config_module.Config, base_score: f64) f64 {
    if (base_score >= value.base_score_threshold[0]) return value.base_release_multiplier[0];
    if (base_score >= value.base_score_threshold[1]) return value.base_release_multiplier[1];
    if (base_score >= value.base_score_threshold[2]) return value.base_release_multiplier[2];
    return value.base_release_multiplier[3];
}

pub fn phaseOrder(tier: u8) u8 {
    return switch (tier) {
        @intFromEnum(types.Tier.virtual_memory) => 0,
        @intFromEnum(types.Tier.physical_memory) => 1,
        @intFromEnum(types.Tier.vram) => 2,
        else => 9,
    };
}

pub fn isConservativeTargetPressure(value: *const config_module.Config, resolved: *const Resolved) bool {
    var has_target_pressure = false;
    var has_strong_pressure = false;
    var index: usize = 0;
    while (index < 3) : (index += 1) {
        has_target_pressure = has_target_pressure or
            resolved.free_ratio[index] < value.target_free_ratio[index];
        has_strong_pressure = has_strong_pressure or
            resolved.free_ratio[index] < value.strong_pressure_free_ratio;
    }
    return has_target_pressure and !has_strong_pressure;
}

pub fn saturatingAdd(left: u64, right: u64) u64 {
    return if (std.math.maxInt(u64) - left < right) std.math.maxInt(u64) else left + right;
}

fn deriveFreeBytes(total: u64, ratio: f64) u64 {
    if (total == 0 or ratio <= 0) return 0;
    if (ratio >= 1) return total;
    const product = @as(f64, @floatFromInt(total)) * ratio;
    return @intFromFloat(@floor(product + 0.5));
}

fn ceilProductToU64(bytes: u64, multiplier: f64) types.PlanError!u64 {
    if (!std.math.isFinite(multiplier) or multiplier < 0) return error.NumericOverflow;
    if (bytes == 0 or multiplier == 0) return 0;
    const product = @as(f64, @floatFromInt(bytes)) * multiplier;
    if (!std.math.isFinite(product) or
        product >= @as(f64, @floatFromInt(std.math.maxInt(u64))))
    {
        return error.NumericOverflow;
    }
    return @intFromFloat(@ceil(product));
}

fn validRatio(value: f64) bool {
    return std.math.isFinite(value) and value >= 0 and value <= 1;
}

fn allZero(values: []const u8) bool {
    for (values) |value| {
        if (value != 0) return false;
    }
    return true;
}
