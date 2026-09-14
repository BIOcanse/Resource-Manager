const std = @import("std");
const protocol = @import("protocol.zig");

pub const ScoreError = error{InvalidScoreInput};

pub fn softwareBaseMean(values: []const f64, maximum: f64) ScoreError!f64 {
    if (values.len == 0) return 0;
    var mean: f64 = 0;
    for (values, 0..) |value, index| {
        if (!protocol.finiteNonNegative(value) or value > maximum)
            return error.InvalidScoreInput;
        mean += (value - mean) / @as(f64, @floatFromInt(index + 1));
    }
    return normalizeZero(mean);
}

pub const Welfare = struct {
    cpu_free_ratio: f64,
    gpu_free_ratio: f64,
    vram_free_ratio: f64,
    memory_free_ratio: f64,
    multiplier: f64,
    system_pressure: f64,
    budget: f64,
    share: f64,
    eligible_process_count: u32,
    cpu_multiplier: f64,
    memory_multiplier: f64,
    cpu_bonus: f64,
    memory_bonus: f64,
};

pub fn calculateWelfare(
    config: *const protocol.Config,
    envelope: *const protocol.GenerationEnvelope,
) ScoreError!Welfare {
    if (!protocol.validRatio(envelope.cpu_free_ratio) or
        !protocol.validRatio(envelope.gpu_free_ratio) or
        !protocol.validRatio(envelope.vram_free_ratio) or
        !protocol.validRatio(envelope.memory_free_ratio) or
        !protocol.finiteNonNegative(envelope.software_base_mean) or
        envelope.software_base_mean > config.maximum_base_importance) return error.InvalidScoreInput;
    const multiplier = envelope.cpu_free_ratio * envelope.gpu_free_ratio *
        envelope.vram_free_ratio * envelope.memory_free_ratio;
    const system_pressure = 1 - multiplier;
    const cpu_multiplier = try welfareRatio(envelope.cpu_free_ratio, config.welfare_utilization_baseline_percent);
    const memory_multiplier = try welfareRatio(envelope.memory_free_ratio, config.welfare_utilization_baseline_percent);
    const budget = envelope.software_base_mean * multiplier;
    const share = if (envelope.welfare_eligible_process_count > 0)
        budget / @as(f64, @floatFromInt(envelope.welfare_eligible_process_count))
    else
        0;
    if (!protocol.validRatio(multiplier) or !protocol.validRatio(system_pressure) or
        !protocol.finiteNonNegative(budget) or !protocol.finiteNonNegative(share))
        return error.InvalidScoreInput;
    return .{
        .cpu_free_ratio = normalizeZero(envelope.cpu_free_ratio),
        .gpu_free_ratio = normalizeZero(envelope.gpu_free_ratio),
        .vram_free_ratio = normalizeZero(envelope.vram_free_ratio),
        .memory_free_ratio = normalizeZero(envelope.memory_free_ratio),
        .multiplier = normalizeZero(multiplier),
        .system_pressure = normalizeZero(system_pressure),
        .budget = normalizeZero(budget),
        .share = normalizeZero(share),
        .eligible_process_count = envelope.welfare_eligible_process_count,
        .cpu_multiplier = cpu_multiplier,
        .memory_multiplier = memory_multiplier,
        .cpu_bonus = normalizeZero(envelope.software_base_mean * cpu_multiplier),
        .memory_bonus = normalizeZero(envelope.software_base_mean * memory_multiplier),
    };
}

pub fn welfareRatio(free_ratio: f64, baseline_percent: f64) ScoreError!f64 {
    if (!protocol.validRatio(free_ratio) or !protocol.finitePositive(baseline_percent) or baseline_percent > 100)
        return error.InvalidScoreInput;
    const usage_percent = (1 - free_ratio) * 100;
    if (usage_percent >= baseline_percent) return 0;
    return normalizeZero(1 - usage_percent / baseline_percent);
}

pub fn cpu(
    config: *const protocol.Config,
    row: *const protocol.ProcessInput,
    welfare_share: f64,
) ScoreError!f64 {
    if (row.runtime_state >= protocol.runtime_state_count) return error.InvalidScoreInput;
    const whole_cpu_score = try compute(
        config,
        row.base_importance,
        config.cpu_state_multipliers[row.runtime_state],
        row.cpu_policy_multiplier,
        row.weighted_cpu_use_percent,
    );
    const raw_score = whole_cpu_score / config.cpu_baseline_ratio;
    const score = try addWelfare(raw_score, row.runtime_state, welfare_share);
    if (!protocol.finiteNonNegative(score)) return error.InvalidScoreInput;
    return if (score == 0) 0 else score;
}

pub fn gpu(
    config: *const protocol.Config,
    process: *const protocol.ProcessInput,
    row: *const protocol.GpuInput,
    welfare_share: f64,
) ScoreError!f64 {
    if (process.runtime_state >= protocol.runtime_state_count) return error.InvalidScoreInput;
    const raw_score = try compute(
        config,
        process.base_importance,
        config.gpu_state_multipliers[process.runtime_state],
        row.gpu_policy_multiplier,
        row.gpu_occupancy_percent,
    );
    return addWelfare(raw_score, process.runtime_state, welfare_share);
}

pub fn addWelfare(raw_score: f64, runtime_state: u8, welfare_share: f64) ScoreError!f64 {
    if (!protocol.finiteNonNegative(raw_score) or
        !protocol.finiteNonNegative(welfare_share)) return error.InvalidScoreInput;
    if (runtime_state == @intFromEnum(protocol.RuntimeState.not_running)) return 0;
    const score = raw_score + welfare_share;
    if (!protocol.finiteNonNegative(score)) return error.InvalidScoreInput;
    return normalizeZero(score);
}

fn compute(
    config: *const protocol.Config,
    base_importance: f64,
    state_multiplier: f64,
    policy_multiplier: f64,
    occupancy_percent: f64,
) ScoreError!f64 {
    if (!protocol.finiteNonNegative(base_importance) or
        base_importance > config.maximum_base_importance or
        !protocol.finiteNonNegative(state_multiplier) or
        state_multiplier > config.maximum_policy_multiplier or
        !protocol.finiteNonNegative(policy_multiplier) or
        policy_multiplier > config.maximum_policy_multiplier or
        !protocol.validPercent(occupancy_percent)) return error.InvalidScoreInput;
    if (base_importance == 0 or state_multiplier == 0 or
        policy_multiplier == 0 or occupancy_percent == 0) return 0;

    const weighted_base = base_importance * state_multiplier;
    if (!std.math.isFinite(weighted_base)) return error.InvalidScoreInput;
    const weighted_policy = weighted_base * policy_multiplier;
    if (!std.math.isFinite(weighted_policy)) return error.InvalidScoreInput;
    const score = weighted_policy * (occupancy_percent / 100.0);
    if (!protocol.finiteNonNegative(score)) return error.InvalidScoreInput;
    return if (score == 0) 0 else score;
}

fn normalizeZero(value: f64) f64 {
    return if (value == 0) 0 else value;
}
