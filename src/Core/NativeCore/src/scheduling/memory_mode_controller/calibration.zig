const std = @import("std");
const pressure_policy = @import("pressure_policy.zig");

pub const ratio_units_max: u32 = 10_000;
const point_count: usize = ratio_units_max + 1;
pub const required_scratch_words: usize = point_count * 4;

pub const Partition = enum(u8) {
    training,
    validation,
};

pub const Domain = enum(u8) {
    memory,
};

pub const Sample = struct {
    sample_id: u64,
    domain: Domain,
    partition: Partition,
    free_ratio_units: u32,
    weight: u32,
    unrestricted_safe: bool,
    normal_sufficient: bool,
    strongest_required: bool,
};

pub const ThresholdUnits = struct {
    unrestricted_minimum_free_ratio: u32,
    normal_minimum_free_ratio: u32,
    strong_begin_free_ratio: u32,

    pub fn toPressurePolicy(self: ThresholdUnits) pressure_policy.Thresholds {
        return .{
            .ratio_units_maximum = ratio_units_max,
            .unrestricted_minimum_free_ratio_units = self.unrestricted_minimum_free_ratio,
            .normal_minimum_free_ratio_units = self.normal_minimum_free_ratio,
            .strong_begin_free_ratio_units = self.strong_begin_free_ratio,
        };
    }
};

pub const Evaluation = struct {
    sample_count: u32,
    weighted_mismatch: u64,
    weighted_label_count: u64,
};

pub const Result = struct {
    domain: Domain,
    thresholds: ThresholdUnits,
    training: Evaluation,
    validation: Evaluation,
};

pub const AcceptancePolicy = struct {
    minimum_training_samples: u32,
    minimum_validation_samples: u32,
    maximum_training_mismatch_per_10k: u32,
    maximum_validation_mismatch_per_10k: u32,
};

pub const CalibrationError = error{
    EmptyCorpus,
    ScratchTooSmall,
    NonCanonicalSamples,
    MixedDomain,
    InvalidSample,
    InconsistentLabels,
    IncompleteTrainingCoverage,
    IncompleteValidationCoverage,
    WeightOverflow,
    NoThresholds,
};

const Coverage = struct {
    count: u32 = 0,
    unrestricted_true: bool = false,
    unrestricted_false: bool = false,
    normal_true: bool = false,
    normal_false: bool = false,
    strongest_true: bool = false,
    strongest_false: bool = false,

    fn observe(self: *Coverage, sample: Sample) void {
        self.count += 1;
        if (sample.unrestricted_safe) self.unrestricted_true = true else self.unrestricted_false = true;
        if (sample.normal_sufficient) self.normal_true = true else self.normal_false = true;
        if (sample.strongest_required) self.strongest_true = true else self.strongest_false = true;
    }

    fn complete(self: Coverage) bool {
        return self.count != 0 and
            self.unrestricted_true and self.unrestricted_false and
            self.normal_true and self.normal_false and
            self.strongest_true and self.strongest_false;
    }
};

pub fn calibrate(
    domain: Domain,
    samples: []const Sample,
    scratch: []u64,
) CalibrationError!Result {
    if (samples.len == 0) return error.EmptyCorpus;
    if (scratch.len < required_scratch_words) return error.ScratchTooSmall;

    var training_coverage = Coverage{};
    var validation_coverage = Coverage{};
    try validateSamples(domain, samples, &training_coverage, &validation_coverage);
    if (!training_coverage.complete()) return error.IncompleteTrainingCoverage;
    if (!validation_coverage.complete()) return error.IncompleteValidationCoverage;

    const strong_loss = scratch[0..point_count];
    const normal_loss = scratch[point_count .. point_count * 2];
    const unrestricted_loss = scratch[point_count * 2 .. point_count * 3];
    const temporary = scratch[point_count * 3 .. point_count * 4];
    @memset(scratch[0..required_scratch_words], 0);

    try buildLoss(samples, .strongest, .true_below_threshold, strong_loss, temporary);
    try buildLoss(samples, .normal, .true_at_or_above_threshold, normal_loss, temporary);
    try buildLoss(samples, .unrestricted, .true_at_or_above_threshold, unrestricted_loss, temporary);

    var best_unrestricted = ratio_units_max;
    temporary[ratio_units_max] = best_unrestricted;
    var reverse = ratio_units_max - 1;
    while (reverse >= 1) : (reverse -= 1) {
        const candidate: usize = @intCast(reverse);
        const current_best: usize = @intCast(best_unrestricted);
        if (unrestricted_loss[candidate] < unrestricted_loss[current_best]) {
            best_unrestricted = reverse;
        }
        temporary[candidate] = best_unrestricted;
        if (reverse == 1) break;
    }

    var chosen: ?ThresholdUnits = null;
    var chosen_loss: u64 = 0;
    var best_strong: u32 = 1;
    var normal: u32 = 2;
    while (normal < ratio_units_max) : (normal += 1) {
        const strong_candidate = normal - 1;
        if (strong_loss[strong_candidate] <= strong_loss[best_strong]) {
            best_strong = strong_candidate;
        }
        const unrestricted: u32 = @intCast(temporary[normal + 1]);
        const total = try addLosses(
            strong_loss[best_strong],
            normal_loss[normal],
            unrestricted_loss[unrestricted],
        );
        const candidate = ThresholdUnits{
            .unrestricted_minimum_free_ratio = unrestricted,
            .normal_minimum_free_ratio = normal,
            .strong_begin_free_ratio = best_strong,
        };
        if (chosen == null or betterCandidate(total, candidate, chosen_loss, chosen.?)) {
            chosen = candidate;
            chosen_loss = total;
        }
    }

    const thresholds = chosen orelse return error.NoThresholds;
    return .{
        .domain = domain,
        .thresholds = thresholds,
        .training = try evaluate(domain, samples, .training, thresholds),
        .validation = try evaluate(domain, samples, .validation, thresholds),
    };
}

pub fn evaluate(
    domain: Domain,
    samples: []const Sample,
    partition: Partition,
    thresholds: ThresholdUnits,
) CalibrationError!Evaluation {
    var result = Evaluation{
        .sample_count = 0,
        .weighted_mismatch = 0,
        .weighted_label_count = 0,
    };
    for (samples) |sample| {
        if (sample.domain != domain) return error.MixedDomain;
        if (sample.partition != partition) continue;
        result.sample_count += 1;
        const weight: u64 = sample.weight;
        result.weighted_label_count = std.math.add(
            u64,
            result.weighted_label_count,
            std.math.mul(u64, weight, 3) catch return error.WeightOverflow,
        ) catch return error.WeightOverflow;
        if ((sample.free_ratio_units >= thresholds.unrestricted_minimum_free_ratio) !=
            sample.unrestricted_safe)
        {
            result.weighted_mismatch = try addWeight(result.weighted_mismatch, sample.weight);
        }
        if ((sample.free_ratio_units >= thresholds.normal_minimum_free_ratio) !=
            sample.normal_sufficient)
        {
            result.weighted_mismatch = try addWeight(result.weighted_mismatch, sample.weight);
        }
        if ((sample.free_ratio_units < thresholds.strong_begin_free_ratio) !=
            sample.strongest_required)
        {
            result.weighted_mismatch = try addWeight(result.weighted_mismatch, sample.weight);
        }
    }
    return result;
}

pub fn accepts(result: Result, policy: AcceptancePolicy) bool {
    if (policy.minimum_training_samples == 0 or
        policy.minimum_validation_samples == 0 or
        policy.maximum_training_mismatch_per_10k > 10_000 or
        policy.maximum_validation_mismatch_per_10k > 10_000 or
        result.training.sample_count < policy.minimum_training_samples or
        result.validation.sample_count < policy.minimum_validation_samples)
    {
        return false;
    }
    return withinMismatchRate(
        result.training,
        policy.maximum_training_mismatch_per_10k,
    ) and withinMismatchRate(
        result.validation,
        policy.maximum_validation_mismatch_per_10k,
    );
}

fn validateSamples(
    domain: Domain,
    samples: []const Sample,
    training: *Coverage,
    validation: *Coverage,
) CalibrationError!void {
    var previous_id: u64 = 0;
    for (samples) |sample| {
        if (sample.domain != domain) return error.MixedDomain;
        if (sample.sample_id == 0 or sample.sample_id <= previous_id) {
            return error.NonCanonicalSamples;
        }
        previous_id = sample.sample_id;
        if (sample.free_ratio_units > ratio_units_max or sample.weight == 0) {
            return error.InvalidSample;
        }
        if ((sample.unrestricted_safe and !sample.normal_sufficient) or
            (sample.normal_sufficient and sample.strongest_required))
        {
            return error.InconsistentLabels;
        }
        switch (sample.partition) {
            .training => training.observe(sample),
            .validation => validation.observe(sample),
        }
    }
}

fn withinMismatchRate(evaluation: Evaluation, maximum_per_10k: u32) bool {
    if (evaluation.weighted_label_count == 0) return false;
    return @as(u128, evaluation.weighted_mismatch) * 10_000 <=
        @as(u128, evaluation.weighted_label_count) * maximum_per_10k;
}

const Label = enum {
    strongest,
    normal,
    unrestricted,
};

const Prediction = enum {
    true_below_threshold,
    true_at_or_above_threshold,
};

fn buildLoss(
    samples: []const Sample,
    label: Label,
    prediction: Prediction,
    output: []u64,
    total_histogram: []u64,
) CalibrationError!void {
    @memset(total_histogram, 0);
    var total_weight: u64 = 0;
    var true_weight: u64 = 0;
    for (samples) |sample| {
        if (sample.partition != .training) continue;
        const index: usize = @intCast(sample.free_ratio_units);
        total_histogram[index] = try addWeight(total_histogram[index], sample.weight);
        total_weight = try addWeight(total_weight, sample.weight);
        if (labelValue(sample, label)) {
            output[index] = try addWeight(output[index], sample.weight);
            true_weight = try addWeight(true_weight, sample.weight);
        }
    }

    const false_weight = total_weight - true_weight;
    // Descending traversal lets `output` serve first as the true-label
    // histogram and then as the loss table without overwriting unread slots.
    var all_at_or_above: u64 = 0;
    var true_at_or_above: u64 = 0;
    var threshold: u32 = ratio_units_max;
    while (threshold >= 1) : (threshold -= 1) {
        const crossed_index: usize = @intCast(threshold);
        all_at_or_above = try addLossPair(
            all_at_or_above,
            total_histogram[crossed_index],
        );
        true_at_or_above = try addLossPair(
            true_at_or_above,
            output[crossed_index],
        );
        const false_at_or_above = all_at_or_above - true_at_or_above;
        output[threshold] = switch (prediction) {
            .true_below_threshold => try addLossPair(
                true_at_or_above,
                false_weight - false_at_or_above,
            ),
            .true_at_or_above_threshold => try addLossPair(
                true_weight - true_at_or_above,
                false_at_or_above,
            ),
        };
        if (threshold == 1) break;
    }
}

fn labelValue(sample: Sample, label: Label) bool {
    return switch (label) {
        .strongest => sample.strongest_required,
        .normal => sample.normal_sufficient,
        .unrestricted => sample.unrestricted_safe,
    };
}

fn addWeight(current: u64, weight: u32) CalibrationError!u64 {
    return std.math.add(u64, current, weight) catch error.WeightOverflow;
}

fn addLossPair(first: u64, second: u64) CalibrationError!u64 {
    return std.math.add(u64, first, second) catch error.WeightOverflow;
}

fn addLosses(first: u64, second: u64, third: u64) CalibrationError!u64 {
    const partial = std.math.add(u64, first, second) catch return error.WeightOverflow;
    return std.math.add(u64, partial, third) catch error.WeightOverflow;
}

fn betterCandidate(
    loss: u64,
    candidate: ThresholdUnits,
    best_loss: u64,
    best: ThresholdUnits,
) bool {
    if (loss != best_loss) return loss < best_loss;
    if (candidate.unrestricted_minimum_free_ratio != best.unrestricted_minimum_free_ratio) {
        return candidate.unrestricted_minimum_free_ratio > best.unrestricted_minimum_free_ratio;
    }
    if (candidate.normal_minimum_free_ratio != best.normal_minimum_free_ratio) {
        return candidate.normal_minimum_free_ratio > best.normal_minimum_free_ratio;
    }
    return candidate.strong_begin_free_ratio > best.strong_begin_free_ratio;
}
