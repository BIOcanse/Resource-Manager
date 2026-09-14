const std = @import("std");
const base_score_tier = @import("../base_score_tier.zig");

pub const abi_version: u32 = 0x0006_0001;

pub const Status = enum(i32) {
    ok = 0,
    invalid_argument = 1,
    abi_mismatch = 2,
    no_data = 3,
    buffer_too_small = 4,
    stale_generation = 5,
    capacity_exceeded = 6,
    conflicting_facts = 7,
    invalid_facts = 8,
    out_of_memory = 9,
    recreate_required = 10,
};

pub const MemoryMode = enum(u8) {
    invalid = 0,
    unrestricted = 1,
    normal = 2,
    optimize = 3,
    paged_frozen = 4,
};

pub const MemoryGrade = enum(u3) {
    normal = 0,
    level1 = 1,
    level2 = 2,
    level3 = 3,
    level4 = 4,
};

pub const MemoryGradeSet = struct {
    pub fn bit(grade: MemoryGrade) u8 {
        return @as(u8, 1) << @intFromEnum(grade);
    }

    pub const normal = bit(.normal);
    pub const optimization = bit(.level1) | bit(.level2) | bit(.level3);
    pub const all = normal | optimization | bit(.level4);

    pub fn contains(grades: u8, grade: MemoryGrade) bool {
        return (grades & bit(grade)) != 0;
    }
};

pub const EnvelopeValidity = struct {
    pub const memory_source: u64 = 1 << 0;
    pub const memory_pressure: u64 = 1 << 1;
    pub const cpu_scores: u64 = 1 << 2;
    pub const required: u64 = memory_source | memory_pressure | cpu_scores;
    pub const known: u64 = required;
};

pub const EnvelopeFlags = struct {
    pub const full_replacement: u64 = 1 << 0;
    pub const allow_unrestricted: u64 = 1 << 1;
    pub const required: u64 = full_replacement;
    pub const known: u64 = required | allow_unrestricted;
};

pub const SoftwareValidity = struct {
    pub const identity: u64 = 1 << 0;
    pub const scheduling_generation: u64 = 1 << 1;
    pub const cpu_score: u64 = 1 << 2;
    pub const base_score: u64 = 1 << 3;
    pub const required: u64 = identity | scheduling_generation | cpu_score | base_score;
    pub const known: u64 = required;
};

pub const DesiredSoftwareFlags = struct {
    pub const base_score_clamped: u8 = 1 << 0;
    pub const known: u8 = base_score_clamped;
};

pub const DesiredSoftwareValidity = struct {
    pub const software_identity: u64 = 1 << 0;
    pub const snapshot_generation: u64 = 1 << 1;
    pub const scheduling_generation: u64 = 1 << 2;
    pub const score: u64 = 1 << 3;
    pub const rank: u64 = 1 << 4;
    pub const mode: u64 = 1 << 5;
    pub const base_score: u64 = 1 << 6;
    pub const base_score_allowed_grades: u64 = 1 << 7;
    pub const required: u64 = software_identity | snapshot_generation |
        scheduling_generation | score | rank | mode | base_score | base_score_allowed_grades;
    pub const known: u64 = required;
};

pub const SnapshotFlags = struct {
    pub const full_replacement: u64 = 1 << 0;
    pub const unrestricted_allowed: u64 = 1 << 1;
    pub const known: u64 = full_replacement | unrestricted_allowed;
};

pub const Config = extern struct {
    abi_version: u32,
    struct_size: u32,
    generation: u64,
    maximum_software_count: u32,
    maximum_output_count: u32,
    ratio_units_maximum: u32,
    unrestricted_minimum_free_ratio_units: u32,
    normal_minimum_free_ratio_units: u32,
    strong_begin_free_ratio_units: u32,
    middle_tier_minimum_base_score: f64,
    high_tier_minimum_base_score: f64,
    reserved: [3]u64,
};

pub const Capacity = extern struct {
    abi_version: u32,
    struct_size: u32,
    configuration_generation: u64,
    software_input_struct_size: u32,
    software_output_struct_size: u32,
    software_capacity: u32,
    output_capacity: u32,
    reserved: [4]u64,
};

pub const GenerationEnvelope = extern struct {
    abi_version: u32,
    struct_size: u32,
    software_input_struct_size: u32,
    software_output_struct_size: u32,
    configuration_generation: u64,
    scheduling_generation: u64,
    memory_source_workspace_identity: u64,
    memory_source_committed_generation: u64,
    snapshot_generation: u64,
    valid_mask: u64,
    flags: u64,
    memory_free_ratio_units: u32,
    reserved0: u32,
    software_count: u32,
    output_capacity: u32,
    reserved: [3]u64,
};

pub const SoftwareInput = extern struct {
    struct_size: u32,
    flags: u32,
    valid_mask: u64,
    software_key: u64,
    scheduling_generation: u64,
    cpu_score: f64,
    base_score: f64,
    source_index: u32,
    reserved0: u32,
    reserved: [1]u64,
};

pub const DesiredSoftwareOutput = extern struct {
    struct_size: u32,
    mode: u8,
    flags: u8,
    base_score_allowed_grades: u8,
    reserved0: u8,
    valid_mask: u64,
    software_key: u64,
    snapshot_generation: u64,
    scheduling_generation: u64,
    score: f64,
    base_score: f64,
    rank: u32,
    source_index: u32,
};

pub const Snapshot = extern struct {
    abi_version: u32,
    struct_size: u32,
    configuration_generation: u64,
    scheduling_generation: u64,
    memory_source_workspace_identity: u64,
    memory_source_committed_generation: u64,
    snapshot_generation: u64,
    flags: u64,
    output_count: u32,
    optimize_count: u32,
    strongest_count: u32,
    reserved0: u32,
    reserved: [3]u64,
};

pub fn validateConfig(config: *const Config) bool {
    return config.abi_version == abi_version and
        config.struct_size == @sizeOf(Config) and
        config.generation != 0 and
        config.maximum_software_count != 0 and
        config.maximum_output_count >= config.maximum_software_count and
        validThresholds(
            config.ratio_units_maximum,
            config.strong_begin_free_ratio_units,
            config.normal_minimum_free_ratio_units,
            config.unrestricted_minimum_free_ratio_units,
        ) and
        base_score_tier.isValidThresholds(
            config.middle_tier_minimum_base_score,
            config.high_tier_minimum_base_score,
        ) and
        allZero(std.mem.asBytes(&config.reserved));
}

pub fn validRatioUnits(value: u32, maximum: u32) bool {
    return maximum != 0 and value <= maximum;
}

pub fn validScore(value: f64) bool {
    return std.math.isFinite(value) and value >= 0;
}

fn validThresholds(maximum: u32, strong: u32, normal: u32, unrestricted: u32) bool {
    return maximum != 0 and strong > 0 and strong < normal and
        normal < unrestricted and unrestricted <= maximum;
}

fn allZero(bytes: []const u8) bool {
    for (bytes) |byte| if (byte != 0) return false;
    return true;
}

comptime {
    if (@offsetOf(DesiredSoftwareOutput, "base_score_allowed_grades") != 6)
        @compileError("memory mode allowed grades offset drift");
    if (@sizeOf(Config) != 80) @compileError("memory mode Config ABI drift");
    if (@sizeOf(Capacity) != 64) @compileError("memory mode Capacity ABI drift");
    if (@sizeOf(GenerationEnvelope) != 112) @compileError("memory mode envelope ABI drift");
    if (@sizeOf(SoftwareInput) != 64) @compileError("memory mode software input ABI drift");
    if (@sizeOf(DesiredSoftwareOutput) != 64) @compileError("memory mode software output ABI drift");
    if (@sizeOf(Snapshot) != 96) @compileError("memory mode snapshot ABI drift");
    if (@offsetOf(GenerationEnvelope, "memory_source_workspace_identity") != 32)
        @compileError("memory mode envelope workspace identity offset drift");
    if (@offsetOf(GenerationEnvelope, "memory_source_committed_generation") != 40)
        @compileError("memory mode envelope committed generation offset drift");
    if (@offsetOf(Snapshot, "memory_source_workspace_identity") != 24)
        @compileError("memory mode snapshot workspace identity offset drift");
    if (@offsetOf(Snapshot, "memory_source_committed_generation") != 32)
        @compileError("memory mode snapshot committed generation offset drift");
}
