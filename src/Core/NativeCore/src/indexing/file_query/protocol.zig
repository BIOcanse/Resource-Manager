const std = @import("std");
const unicode61 = @import("unicode61.zig");

pub const abi_version: u32 = 0x0002_0000;
// ASCII space/tab/CR/LF boundary trim plus ASCII-only case-insensitive substring matching.
pub const text_matching_version: u32 = 0x0001_0000;
pub const unicode_remove_diacritics_mode: u32 = 2;
// SQLite FTS5 trigram defaults: case_sensitive=0 and remove_diacritics=0.
pub const trigram_tokenizer_contract_version: u32 = 0x0001_0000;

pub const QueryPhase = enum(u32) {
    empty = 1,
    planned = 2,
    collecting = 3,
    finalized = 4,
};

pub const PlanMode = enum(u32) {
    short_scan = 1,
    fts = 2,
};

pub const SourceId = enum(u32) {
    file_name = 1,
    relative_path = 2,
    software_name = 3,
    short_scan = 4,
};

pub const PrimitiveKind = enum(u32) {
    short_substring_ordered_scan = 1,
    file_name_trigram_fts = 2,
    relative_path_unicode_fts = 3,
    software_name_trigram_fts = 4,
};

pub const SourceMask = struct {
    pub const file_name: u32 = 1 << 0;
    pub const relative_path: u32 = 1 << 1;
    pub const software_name: u32 = 1 << 2;
    pub const short_scan: u32 = 1 << 3;
    pub const known: u32 = file_name | relative_path | software_name | short_scan;
};

pub const BeginValid = struct {
    pub const operation_epoch: u64 = 1 << 0;
    pub const query_epoch: u64 = 1 << 1;
    pub const query_bytes: u64 = 1 << 2;
    pub const result_limit: u64 = 1 << 3;
    pub const required: u64 = operation_epoch | query_epoch | query_bytes | result_limit;
    pub const known: u64 = required;
};

pub const SubmitValid = struct {
    pub const operation_epoch: u64 = 1 << 0;
    pub const query_epoch: u64 = 1 << 1;
    pub const batch_epoch: u64 = 1 << 2;
    pub const candidates: u64 = 1 << 3;
    pub const candidate_bytes: u64 = 1 << 4;
    pub const required: u64 = operation_epoch | query_epoch | batch_epoch |
        candidates | candidate_bytes;
    pub const known: u64 = required;
};

pub const FinalizeValid = struct {
    pub const operation_epoch: u64 = 1 << 0;
    pub const query_epoch: u64 = 1 << 1;
    pub const result_capacity: u64 = 1 << 2;
    pub const required: u64 = operation_epoch | query_epoch | result_capacity;
    pub const known: u64 = required;
};

pub const ResetValid = struct {
    pub const operation_epoch: u64 = 1 << 0;
    pub const required: u64 = operation_epoch;
    pub const known: u64 = required;
};

pub const Config = extern struct {
    abi_version: u32,
    struct_size: u32,
    generation: u64,
    maximum_query_utf8_byte_count: u32,
    maximum_query_rune_count: u32,
    maximum_plan_utf8_byte_count: u32,
    maximum_source_plan_count: u32,
    maximum_candidate_count_per_source: u32,
    maximum_submitted_candidate_count: u32,
    maximum_unique_candidate_count: u32,
    maximum_candidate_submit_batch_count: u32,
    maximum_candidate_submit_utf8_byte_count: u32,
    candidate_text_arena_byte_count: u32,
    maximum_file_name_utf8_byte_count: u32,
    maximum_result_count: u32,
    entry_index_capacity: u32,
    ordinal_index_capacity: u32,
    short_query_rune_threshold: u32,
    unicode_tokenizer_version: u32,
    unicode_remove_diacritics_mode: u32,
    trigram_tokenizer_contract_version: u32,
    candidate_limit_multiplier: u32,
    candidate_limit_floor: u32,
    candidate_limit_ceiling: u32,
    file_name_priority: u32,
    relative_path_priority: u32,
    software_name_priority: u32,
    flags: u32,
    text_matching_version: u32,
    resident_byte_budget: u64,
    reserved: [5]u64,
};

pub const Capacity = extern struct {
    struct_size: u32,
    query_utf8_byte_capacity: u32,
    query_rune_capacity: u32,
    plan_utf8_byte_capacity: u32,
    source_plan_capacity: u32,
    candidate_capacity_per_source: u32,
    submitted_candidate_capacity: u32,
    unique_candidate_capacity: u32,
    candidate_submit_batch_capacity: u32,
    candidate_submit_utf8_byte_capacity: u32,
    candidate_text_arena_byte_capacity: u32,
    file_name_utf8_byte_capacity: u32,
    result_capacity: u32,
    entry_index_capacity: u32,
    ordinal_index_capacity: u32,
    reserved_u32: u32,
    resident_byte_count: u64,
    reserved: [4]u64,
};

pub const BeginInput = extern struct {
    abi_version: u32,
    struct_size: u32,
    configuration_generation: u64,
    operation_epoch: u64,
    query_epoch: u64,
    query_byte_count: u32,
    requested_result_count: u32,
    valid_mask: u64,
    flags: u64,
    reserved: [2]u64,
};

pub const SourcePlanOutput = extern struct {
    struct_size: u32,
    source_id: u32,
    primitive_kind: u32,
    priority: u32,
    expression_offset: u32,
    expression_length: u32,
    candidate_limit: u32,
    flags: u32,
    reserved: [2]u64,
};

pub const PlanOutput = extern struct {
    abi_version: u32,
    struct_size: u32,
    configuration_generation: u64,
    state_revision: u64,
    query_epoch: u64,
    mode: u32,
    source_plan_count: u32,
    query_rune_count: u32,
    requested_result_count: u32,
    plan_utf8_byte_count: u32,
    source_mask: u32,
    candidate_limit_per_source: u32,
    maximum_total_candidate_count: u32,
    normalized_query_offset: u32,
    normalized_query_length: u32,
    unicode_tokenizer_version: u32,
    unicode_remove_diacritics_mode: u32,
    trigram_tokenizer_contract_version: u32,
    text_matching_version: u32,
    flags: u64,
    reserved: [3]u64,
};

pub const CandidateInput = extern struct {
    struct_size: u32,
    source_id: u32,
    entry_handle: u64,
    candidate_ordinal: u32,
    file_name_offset: u32,
    file_name_length: u32,
    relative_path_offset: u32,
    relative_path_length: u32,
    software_name_offset: u32,
    software_name_length: u32,
    flags: u64,
    reserved: [2]u64,
};

pub const SubmitInput = extern struct {
    abi_version: u32,
    struct_size: u32,
    configuration_generation: u64,
    operation_epoch: u64,
    query_epoch: u64,
    batch_epoch: u64,
    candidate_count: u32,
    candidate_byte_count: u32,
    valid_mask: u64,
    flags: u64,
    reserved: [2]u64,
};

pub const SubmitOutput = extern struct {
    abi_version: u32,
    struct_size: u32,
    configuration_generation: u64,
    state_revision: u64,
    query_epoch: u64,
    batch_epoch: u64,
    accepted_candidate_count: u32,
    duplicate_candidate_count: u32,
    total_submitted_candidate_count: u32,
    total_unique_candidate_count: u32,
    candidate_text_byte_count: u32,
    flags: u32,
    reserved: [3]u64,
};

pub const FinalizeInput = extern struct {
    abi_version: u32,
    struct_size: u32,
    configuration_generation: u64,
    operation_epoch: u64,
    query_epoch: u64,
    result_capacity: u32,
    reserved_u32: u32,
    valid_mask: u64,
    flags: u64,
    reserved: [2]u64,
};

pub const ResultOutput = extern struct {
    struct_size: u32,
    source_priority: u32,
    entry_handle: u64,
    candidate_ordinal: u32,
    file_name_rune_count: u32,
    order_index: u32,
    flags: u32,
    reserved: [2]u64,
};

pub const FinalizeOutput = extern struct {
    abi_version: u32,
    struct_size: u32,
    configuration_generation: u64,
    state_revision: u64,
    query_epoch: u64,
    result_count: u32,
    matched_candidate_count: u32,
    submitted_candidate_count: u32,
    unique_candidate_count: u32,
    flags: u64,
    reserved: [3]u64,
};

pub const ResetInput = extern struct {
    abi_version: u32,
    struct_size: u32,
    configuration_generation: u64,
    operation_epoch: u64,
    valid_mask: u64,
    flags: u64,
    reserved: [2]u64,
};

pub const SnapshotOutput = extern struct {
    abi_version: u32,
    struct_size: u32,
    configuration_generation: u64,
    state_revision: u64,
    last_operation_epoch: u64,
    last_query_epoch: u64,
    last_batch_epoch: u64,
    phase: u32,
    source_plan_count: u32,
    submitted_candidate_count: u32,
    unique_candidate_count: u32,
    matched_candidate_count: u32,
    result_count: u32,
    query_byte_count: u32,
    plan_byte_count: u32,
    candidate_text_byte_count: u32,
    resident_byte_count: u64,
    flags: u64,
    reserved: [3]u64,
};

pub fn validConfig(config: *const Config) bool {
    const priorities_distinct =
        config.file_name_priority != config.relative_path_priority and
        config.file_name_priority != config.software_name_priority and
        config.relative_path_priority != config.software_name_priority;
    const total_source_budget = std.math.mul(
        u32,
        3,
        config.candidate_limit_ceiling,
    ) catch return false;
    _ = std.math.mul(
        u32,
        config.maximum_result_count,
        config.candidate_limit_multiplier,
    ) catch return false;
    return config.abi_version == abi_version and
        config.struct_size == @sizeOf(Config) and config.generation != 0 and
        config.maximum_query_utf8_byte_count != 0 and
        config.maximum_query_rune_count != 0 and
        config.maximum_plan_utf8_byte_count != 0 and
        config.maximum_source_plan_count >= 3 and
        config.maximum_candidate_count_per_source != 0 and
        config.maximum_result_count <= config.maximum_candidate_count_per_source and
        config.maximum_submitted_candidate_count >= total_source_budget and
        config.maximum_submitted_candidate_count >= config.maximum_result_count and
        config.maximum_unique_candidate_count >= total_source_budget and
        config.maximum_unique_candidate_count >= config.maximum_result_count and
        config.maximum_unique_candidate_count <= config.maximum_submitted_candidate_count and
        config.maximum_candidate_submit_batch_count != 0 and
        config.maximum_candidate_submit_batch_count <= config.maximum_submitted_candidate_count and
        config.maximum_candidate_submit_utf8_byte_count != 0 and
        config.candidate_text_arena_byte_count != 0 and
        config.maximum_file_name_utf8_byte_count != 0 and
        config.maximum_result_count != 0 and
        validIndexCapacity(config.entry_index_capacity, config.maximum_unique_candidate_count) and
        validIndexCapacity(config.ordinal_index_capacity, config.maximum_submitted_candidate_count) and
        config.short_query_rune_threshold >= 3 and
        config.unicode_tokenizer_version == unicode61.tokenizer_version and
        config.unicode_remove_diacritics_mode == unicode_remove_diacritics_mode and
        config.trigram_tokenizer_contract_version == trigram_tokenizer_contract_version and
        config.candidate_limit_multiplier != 0 and
        config.candidate_limit_floor != 0 and
        config.candidate_limit_ceiling >= config.candidate_limit_floor and
        config.candidate_limit_ceiling <= config.maximum_candidate_count_per_source and
        config.file_name_priority != 0 and config.relative_path_priority != 0 and
        config.software_name_priority != 0 and priorities_distinct and
        config.flags == 0 and config.text_matching_version == text_matching_version and
        config.resident_byte_budget != 0 and
        allZero(&config.reserved);
}

pub fn validBegin(
    input: *const BeginInput,
    config: *const Config,
    query: []const u8,
) bool {
    return input.abi_version == abi_version and
        input.struct_size == @sizeOf(BeginInput) and
        input.configuration_generation == config.generation and
        input.operation_epoch != 0 and input.query_epoch != 0 and
        input.query_byte_count == query.len and
        input.query_byte_count <= config.maximum_query_utf8_byte_count and
        input.requested_result_count != 0 and
        input.requested_result_count <= config.maximum_result_count and
        input.valid_mask == BeginValid.required and input.flags == 0 and
        allZero(&input.reserved) and validText(query);
}

pub fn validSubmit(
    input: *const SubmitInput,
    config: *const Config,
    candidates: []const CandidateInput,
    bytes: []const u8,
) bool {
    if (input.abi_version != abi_version or
        input.struct_size != @sizeOf(SubmitInput) or
        input.configuration_generation != config.generation or
        input.operation_epoch == 0 or input.query_epoch == 0 or input.batch_epoch == 0 or
        input.candidate_count != candidates.len or input.candidate_count == 0 or
        input.candidate_count > config.maximum_candidate_submit_batch_count or
        input.candidate_byte_count != bytes.len or
        input.candidate_byte_count > config.maximum_candidate_submit_utf8_byte_count or
        input.valid_mask != SubmitValid.required or input.flags != 0 or
        !allZero(&input.reserved))
    {
        return false;
    }
    for (candidates) |candidate| {
        if (!validCandidate(&candidate, bytes, config)) return false;
    }
    return true;
}

pub fn validCandidate(
    candidate: *const CandidateInput,
    bytes: []const u8,
    config: *const Config,
) bool {
    _ = sourceFromInt(candidate.source_id) orelse return false;
    const file_name = byteSlice(bytes, candidate.file_name_offset, candidate.file_name_length) orelse
        return false;
    const relative_path = byteSlice(
        bytes,
        candidate.relative_path_offset,
        candidate.relative_path_length,
    ) orelse return false;
    const software_name = byteSlice(
        bytes,
        candidate.software_name_offset,
        candidate.software_name_length,
    ) orelse return false;
    return candidate.struct_size == @sizeOf(CandidateInput) and
        candidate.entry_handle != 0 and candidate.candidate_ordinal != 0 and
        file_name.len <= config.maximum_file_name_utf8_byte_count and
        validText(file_name) and validText(relative_path) and
        validText(software_name) and
        candidate.flags == 0 and allZero(&candidate.reserved);
}

pub fn validFinalize(input: *const FinalizeInput, config: *const Config) bool {
    return input.abi_version == abi_version and
        input.struct_size == @sizeOf(FinalizeInput) and
        input.configuration_generation == config.generation and
        input.operation_epoch != 0 and input.query_epoch != 0 and
        input.result_capacity != 0 and input.result_capacity <= config.maximum_result_count and
        input.reserved_u32 == 0 and input.valid_mask == FinalizeValid.required and
        input.flags == 0 and allZero(&input.reserved);
}

pub fn validReset(input: *const ResetInput, config: *const Config) bool {
    return input.abi_version == abi_version and
        input.struct_size == @sizeOf(ResetInput) and
        input.configuration_generation == config.generation and input.operation_epoch != 0 and
        input.valid_mask == ResetValid.required and input.flags == 0 and
        allZero(&input.reserved);
}

pub fn validText(value: []const u8) bool {
    if (value.len == 0 or !std.unicode.utf8ValidateSlice(value)) return false;
    for (value) |byte| {
        if (byte == 0) return false;
    }
    return true;
}

pub fn trimQuery(value: []const u8) []const u8 {
    var start: usize = 0;
    while (start < value.len and isAsciiWhitespace(value[start])) start += 1;
    var end = value.len;
    while (end > start and isAsciiWhitespace(value[end - 1])) end -= 1;
    return value[start..end];
}

pub fn foldAscii(value: u8) u8 {
    return if (value >= 'A' and value <= 'Z') value + ('a' - 'A') else value;
}

pub fn byteSlice(bytes: []const u8, offset: u32, length: u32) ?[]const u8 {
    const end = std.math.add(u32, offset, length) catch return null;
    if (end > bytes.len) return null;
    return bytes[offset..end];
}

pub fn sourceBit(source_id: SourceId) u32 {
    return switch (source_id) {
        .file_name => SourceMask.file_name,
        .relative_path => SourceMask.relative_path,
        .software_name => SourceMask.software_name,
        .short_scan => SourceMask.short_scan,
    };
}

pub fn sourceFromInt(value: u32) ?SourceId {
    return switch (value) {
        @intFromEnum(SourceId.file_name) => .file_name,
        @intFromEnum(SourceId.relative_path) => .relative_path,
        @intFromEnum(SourceId.software_name) => .software_name,
        @intFromEnum(SourceId.short_scan) => .short_scan,
        else => null,
    };
}

pub fn emptyPlan(output: *const PlanOutput) bool {
    return allBytesZero(PlanOutput, output);
}

pub fn emptySubmit(output: *const SubmitOutput) bool {
    return allBytesZero(SubmitOutput, output);
}

pub fn emptyFinalize(output: *const FinalizeOutput) bool {
    return allBytesZero(FinalizeOutput, output);
}

pub fn emptySnapshot(output: *const SnapshotOutput) bool {
    return allBytesZero(SnapshotOutput, output);
}

pub fn allZero(values: []const u64) bool {
    for (values) |value| if (value != 0) return false;
    return true;
}

pub fn allBytesZeroSlice(comptime T: type, values: []const T) bool {
    for (std.mem.sliceAsBytes(values)) |byte| if (byte != 0) return false;
    return true;
}

fn validIndexCapacity(capacity: u32, item_count: u32) bool {
    return capacity >= item_count and std.math.isPowerOfTwo(capacity);
}

fn isAsciiWhitespace(value: u8) bool {
    return value == ' ' or value == '\t' or value == '\r' or value == '\n';
}

fn allBytesZero(comptime T: type, value: *const T) bool {
    for (std.mem.asBytes(value)) |byte| if (byte != 0) return false;
    return true;
}
