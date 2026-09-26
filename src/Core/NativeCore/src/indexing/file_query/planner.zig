const std = @import("std");
const protocol = @import("protocol.zig");
const state = @import("state.zig");
const unicode61 = @import("unicode61.zig");
const ResultCode = @import("../../common/result_codes.zig").ResultCode;

const Writer = struct {
    bytes: []u8,
    length: u32 = 0,

    fn append(self: *Writer, value: []const u8) bool {
        const end = std.math.add(u32, self.length, @intCast(value.len)) catch return false;
        if (end > self.bytes.len) return false;
        @memcpy(self.bytes[self.length..end], value);
        self.length = end;
        return true;
    }

    fn quote(self: *Writer, value: []const u8) bool {
        if (!self.append("\"")) return false;
        for (value) |byte| {
            if (byte == '"' and !self.append("\"")) return false;
            if (!self.append(&.{byte})) return false;
        }
        return self.append("\"");
    }
};

pub fn begin(
    session: *state.Session,
    input: *const protocol.BeginInput,
    query: []const u8,
    output: *protocol.PlanOutput,
    source_outputs: []protocol.SourcePlanOutput,
    plan_output_bytes: []u8,
) ResultCode {
    state.lockMutex(&session.mutex);
    defer session.mutex.unlock();

    if (!protocol.validBegin(input, &session.config, query)) return .abi_mismatch;
    if (!protocol.emptyPlan(output) or
        !protocol.allBytesZeroSlice(protocol.SourcePlanOutput, source_outputs) or
        !allZeroBytes(plan_output_bytes))
    {
        return .abi_mismatch;
    }
    if (session.phase != .empty) return .invalid_argument;
    if (input.operation_epoch <= session.last_operation_epoch or
        input.query_epoch <= session.last_query_epoch)
    {
        return .stale_frame;
    }
    const normalized_query = protocol.trimQuery(query);
    if (normalized_query.len == 0) return .invalid_argument;
    const rune_count = state.runeCount(normalized_query) orelse return .invalid_argument;
    if (rune_count > session.config.maximum_query_rune_count) return .invalid_argument;

    const mode: protocol.PlanMode = if (rune_count < session.config.short_query_rune_threshold)
        .short_scan
    else
        .fts;
    const source_count: u32 = if (mode == .short_scan) 1 else 3;
    if (source_outputs.len < source_count) return .buffer_too_small;

    const candidate_limit = if (mode == .short_scan)
        input.requested_result_count
    else
        boundedCandidateLimit(&session.config, input.requested_result_count) orelse
            return .invalid_argument;
    const maximum_total_candidate_count = if (mode == .short_scan)
        candidate_limit
    else
        std.math.mul(u32, candidate_limit, 3) catch return .invalid_argument;

    @memset(session.plan_bytes, 0);
    @memset(session.source_plans, std.mem.zeroes(protocol.SourcePlanOutput));
    var writer = Writer{ .bytes = session.plan_bytes };
    const normalized_query_offset = writer.length;
    if (!writer.append(normalized_query)) return .buffer_too_small;
    if (mode == .short_scan) {
        session.source_plans[0] = sourcePlan(
            .short_scan,
            .short_substring_ordered_scan,
            0,
            normalized_query_offset,
            @intCast(normalized_query.len),
            candidate_limit,
        );
    } else {
        var start = writer.length;
        if (!writeTrigramExpression(&writer, normalized_query)) return .buffer_too_small;
        session.source_plans[0] = sourcePlan(
            .file_name,
            .file_name_trigram_fts,
            session.config.file_name_priority,
            start,
            writer.length - start,
            candidate_limit,
        );

        start = writer.length;
        if (!writePathExpression(&writer, normalized_query)) return .buffer_too_small;
        session.source_plans[1] = sourcePlan(
            .relative_path,
            .relative_path_unicode_fts,
            session.config.relative_path_priority,
            start,
            writer.length - start,
            candidate_limit,
        );

        start = writer.length;
        if (!writeTrigramExpression(&writer, normalized_query)) return .buffer_too_small;
        session.source_plans[2] = sourcePlan(
            .software_name,
            .software_name_trigram_fts,
            session.config.software_name_priority,
            start,
            writer.length - start,
            candidate_limit,
        );
    }

    if (plan_output_bytes.len < writer.length) return .buffer_too_small;
    @memcpy(session.query_bytes[0..normalized_query.len], normalized_query);
    session.query_byte_count = @intCast(normalized_query.len);
    session.query_rune_count = rune_count;
    session.plan_byte_count = writer.length;
    session.source_plan_count = source_count;
    session.requested_result_count = input.requested_result_count;
    session.candidate_limit_per_source = candidate_limit;
    session.maximum_total_candidate_count = maximum_total_candidate_count;
    session.phase = .planned;
    session.last_operation_epoch = input.operation_epoch;
    session.last_query_epoch = input.query_epoch;
    session.advanceRevision();

    @memcpy(source_outputs[0..source_count], session.source_plans[0..source_count]);
    @memcpy(plan_output_bytes[0..writer.length], session.plan_bytes[0..writer.length]);
    output.* = .{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.PlanOutput),
        .configuration_generation = session.config.generation,
        .state_revision = session.state_revision,
        .query_epoch = input.query_epoch,
        .mode = @intFromEnum(mode),
        .source_plan_count = source_count,
        .query_rune_count = rune_count,
        .requested_result_count = input.requested_result_count,
        .plan_utf8_byte_count = writer.length,
        .source_mask = if (mode == .short_scan)
            protocol.SourceMask.short_scan
        else
            protocol.SourceMask.file_name |
                protocol.SourceMask.relative_path |
                protocol.SourceMask.software_name,
        .candidate_limit_per_source = candidate_limit,
        .maximum_total_candidate_count = maximum_total_candidate_count,
        .normalized_query_offset = normalized_query_offset,
        .normalized_query_length = @intCast(normalized_query.len),
        .unicode_tokenizer_version = session.config.unicode_tokenizer_version,
        .unicode_remove_diacritics_mode = session.config.unicode_remove_diacritics_mode,
        .trigram_tokenizer_contract_version = session.config.trigram_tokenizer_contract_version,
        .text_matching_version = session.config.text_matching_version,
        .flags = 0,
        .reserved = .{ 0, 0, 0 },
    };
    return .ok;
}

fn boundedCandidateLimit(config: *const protocol.Config, requested: u32) ?u32 {
    const multiplied = std.math.mul(u32, requested, config.candidate_limit_multiplier) catch
        return null;
    return @min(@max(multiplied, config.candidate_limit_floor), config.candidate_limit_ceiling);
}

fn sourcePlan(
    source: protocol.SourceId,
    primitive: protocol.PrimitiveKind,
    priority: u32,
    offset: u32,
    length: u32,
    candidate_limit: u32,
) protocol.SourcePlanOutput {
    return .{
        .struct_size = @sizeOf(protocol.SourcePlanOutput),
        .source_id = @intFromEnum(source),
        .primitive_kind = @intFromEnum(primitive),
        .priority = priority,
        .expression_offset = offset,
        .expression_length = length,
        .candidate_limit = candidate_limit,
        .flags = 0,
        .reserved = .{ 0, 0 },
    };
}

fn writeTrigramExpression(writer: *Writer, value: []const u8) bool {
    var first_boundary: usize = 0;
    var second_boundary = first_boundary + (state.utf8SequenceLength(value[first_boundary]) orelse
        return false);
    var third_boundary = second_boundary + (state.utf8SequenceLength(value[second_boundary]) orelse
        return false);
    var end_boundary = third_boundary + (state.utf8SequenceLength(value[third_boundary]) orelse
        return false);
    var trigram_index: u32 = 0;
    while (end_boundary <= value.len) {
        const trigram = value[first_boundary..end_boundary];
        if (!priorTrigramExists(value, first_boundary, trigram)) {
            if (trigram_index != 0 and !writer.append(" AND ")) return false;
            if (!writer.quote(trigram)) return false;
            trigram_index += 1;
        }
        if (end_boundary == value.len) break;
        first_boundary = second_boundary;
        second_boundary = third_boundary;
        third_boundary = end_boundary;
        end_boundary += state.utf8SequenceLength(value[end_boundary]) orelse return false;
    }
    return trigram_index != 0;
}

fn priorTrigramExists(value: []const u8, current_start: usize, current: []const u8) bool {
    var first: usize = 0;
    while (first < current_start) {
        var end = first;
        var rune_index: u32 = 0;
        while (rune_index < 3) : (rune_index += 1) {
            if (end >= value.len) return false;
            end += state.utf8SequenceLength(value[end]) orelse return false;
        }
        if (std.mem.eql(u8, value[first..end], current)) return true;
        first += state.utf8SequenceLength(value[first]) orelse return false;
    }
    return false;
}

fn writePathExpression(writer: *Writer, value: []const u8) bool {
    var token_start: ?usize = null;
    var index: usize = 0;
    var token_count: u32 = 0;
    while (index < value.len) {
        const rune_length = state.utf8SequenceLength(value[index]) orelse return false;
        const token_char = isPathTokenRune(value[index .. index + rune_length]);
        if (token_char and token_start == null) token_start = index;
        if (!token_char and token_start != null) {
            if (token_count != 0 and !writer.append(" AND ")) return false;
            if (!writer.quote(value[token_start.?..index])) return false;
            token_count += 1;
            token_start = null;
        }
        index += rune_length;
    }
    if (token_start) |start| {
        if (token_count != 0 and !writer.append(" AND ")) return false;
        if (!writer.quote(value[start..])) return false;
        token_count += 1;
    }
    if (token_count == 0) return writer.quote(value);
    return true;
}

fn isPathTokenRune(bytes: []const u8) bool {
    const scalar = std.unicode.utf8Decode(bytes) catch return false;
    return unicode61.isTokenScalar(scalar);
}

fn allZeroBytes(bytes: []const u8) bool {
    for (bytes) |byte| if (byte != 0) return false;
    return true;
}
