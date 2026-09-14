const std = @import("std");
const protocol = @import("protocol.zig");
const state = @import("state.zig");
const ResultCode = @import("../../common/result_codes.zig").ResultCode;

pub fn submit(
    session: *state.Session,
    input: *const protocol.SubmitInput,
    candidates: []const protocol.CandidateInput,
    bytes: []const u8,
    output: *protocol.SubmitOutput,
) ResultCode {
    state.lockMutex(&session.mutex);
    defer session.mutex.unlock();

    if (!protocol.validSubmit(input, &session.config, candidates, bytes)) return .abi_mismatch;
    if (!protocol.emptySubmit(output)) return .abi_mismatch;
    if (session.phase != .planned and session.phase != .collecting) return .invalid_argument;
    if (input.operation_epoch <= session.last_operation_epoch or
        input.query_epoch != session.last_query_epoch or
        input.batch_epoch <= session.last_batch_epoch)
    {
        return .stale_frame;
    }

    var new_unique_count: u32 = 0;
    var new_text_byte_count: u32 = 0;
    var duplicate_count: u32 = 0;
    var added_per_source = [_]u32{ 0, 0, 0, 0 };
    for (candidates, 0..) |candidate, batch_index| {
        const source = protocol.sourceFromInt(candidate.source_id) orelse
            return .invalid_argument;
        if (!sourceAllowed(session, source)) return .invalid_argument;
        const source_index = sourceArrayIndex(source);
        added_per_source[source_index] = std.math.add(
            u32,
            added_per_source[source_index],
            1,
        ) catch return .invalid_argument;
        const projected_source_count = std.math.add(
            u32,
            session.submitted_per_source[source_index],
            added_per_source[source_index],
        ) catch return .invalid_argument;
        if (projected_source_count > session.candidate_limit_per_source) {
            return .invalid_argument;
        }

        if (session.findOrdinal(candidate.candidate_ordinal) != null) return .invalid_argument;
        for (candidates[0..batch_index]) |prior| {
            if (prior.candidate_ordinal == candidate.candidate_ordinal) return .invalid_argument;
        }

        if (session.findEntry(candidate.entry_handle)) |existing_index| {
            if (!payloadEqualsStored(session, &session.candidates[existing_index], &candidate, bytes)) {
                return .invalid_argument;
            }
            duplicate_count += 1;
            continue;
        }
        var prior_duplicate = false;
        for (candidates[0..batch_index]) |prior| {
            if (prior.entry_handle != candidate.entry_handle) continue;
            if (!payloadEqualsInput(&prior, &candidate, bytes)) return .invalid_argument;
            prior_duplicate = true;
            break;
        }
        if (prior_duplicate) {
            duplicate_count += 1;
            continue;
        }

        new_unique_count = std.math.add(u32, new_unique_count, 1) catch
            return .invalid_argument;
        const payload_bytes = std.math.add(
            u32,
            candidate.file_name_length,
            candidate.relative_path_length,
        ) catch return .invalid_argument;
        new_text_byte_count = std.math.add(
            u32,
            new_text_byte_count,
            std.math.add(u32, payload_bytes, candidate.software_name_length) catch
                return .invalid_argument,
        ) catch return .invalid_argument;
    }

    const projected_submitted_count = std.math.add(
        u32,
        session.submitted_candidate_count,
        input.candidate_count,
    ) catch return .invalid_argument;
    const projected_unique_count = std.math.add(
        u32,
        session.unique_candidate_count,
        new_unique_count,
    ) catch return .invalid_argument;
    const projected_text_byte_count = std.math.add(
        u32,
        session.candidate_text_byte_count,
        new_text_byte_count,
    ) catch return .invalid_argument;
    if (projected_submitted_count > session.config.maximum_submitted_candidate_count or
        projected_submitted_count > session.maximum_total_candidate_count or
        projected_unique_count > session.config.maximum_unique_candidate_count or
        projected_text_byte_count > session.config.candidate_text_arena_byte_count)
    {
        return .invalid_argument;
    }

    for (candidates) |candidate| {
        const source = protocol.sourceFromInt(candidate.source_id) orelse unreachable;
        var candidate_index = session.findEntry(candidate.entry_handle);
        if (candidate_index == null) {
            candidate_index = session.unique_candidate_count;
            const file_name = protocol.byteSlice(
                bytes,
                candidate.file_name_offset,
                candidate.file_name_length,
            ).?;
            const relative_path = protocol.byteSlice(
                bytes,
                candidate.relative_path_offset,
                candidate.relative_path_length,
            ).?;
            const software_name = protocol.byteSlice(
                bytes,
                candidate.software_name_offset,
                candidate.software_name_length,
            ).?;
            const file_name_offset = copyText(session, file_name);
            const relative_path_offset = copyText(session, relative_path);
            const software_name_offset = copyText(session, software_name);
            session.candidates[candidate_index.?] = .{
                .occupied = true,
                .entry_handle = candidate.entry_handle,
                .candidate_ordinal = candidate.candidate_ordinal,
                .source_mask = protocol.sourceBit(source),
                .file_name_offset = file_name_offset,
                .file_name_length = candidate.file_name_length,
                .relative_path_offset = relative_path_offset,
                .relative_path_length = candidate.relative_path_length,
                .software_name_offset = software_name_offset,
                .software_name_length = candidate.software_name_length,
                .file_name_rune_count = state.runeCount(file_name).?,
            };
            session.unique_candidate_count += 1;
            if (!session.insertEntryIndex(candidate_index.?)) unreachable;
        } else {
            const slot = &session.candidates[candidate_index.?];
            slot.source_mask |= protocol.sourceBit(source);
            slot.candidate_ordinal = @min(slot.candidate_ordinal, candidate.candidate_ordinal);
        }
        if (!session.insertOrdinalIndex(candidate.candidate_ordinal, candidate_index.?, source)) {
            unreachable;
        }
        session.submitted_candidate_count += 1;
        session.submitted_per_source[sourceArrayIndex(source)] += 1;
    }

    session.phase = .collecting;
    session.last_operation_epoch = input.operation_epoch;
    session.last_batch_epoch = input.batch_epoch;
    session.advanceRevision();
    output.* = .{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.SubmitOutput),
        .configuration_generation = session.config.generation,
        .state_revision = session.state_revision,
        .query_epoch = input.query_epoch,
        .batch_epoch = input.batch_epoch,
        .accepted_candidate_count = input.candidate_count,
        .duplicate_candidate_count = duplicate_count,
        .total_submitted_candidate_count = session.submitted_candidate_count,
        .total_unique_candidate_count = session.unique_candidate_count,
        .candidate_text_byte_count = session.candidate_text_byte_count,
        .flags = 0,
        .reserved = .{ 0, 0, 0 },
    };
    return .ok;
}

pub fn finalize(
    session: *state.Session,
    input: *const protocol.FinalizeInput,
    results: []protocol.ResultOutput,
    output: *protocol.FinalizeOutput,
) ResultCode {
    state.lockMutex(&session.mutex);
    defer session.mutex.unlock();

    if (!protocol.validFinalize(input, &session.config)) return .abi_mismatch;
    if (!protocol.emptyFinalize(output) or
        !protocol.allBytesZeroSlice(protocol.ResultOutput, results))
    {
        return .abi_mismatch;
    }
    if (session.phase != .planned and session.phase != .collecting) return .invalid_argument;
    if (input.operation_epoch <= session.last_operation_epoch or
        input.query_epoch != session.last_query_epoch)
    {
        return .stale_frame;
    }
    if (input.result_capacity < session.requested_result_count or
        results.len < session.requested_result_count)
    {
        return .buffer_too_small;
    }
    const result_limit = session.requested_result_count;

    var matched_count: u32 = 0;
    for (session.candidates[0..session.unique_candidate_count], 0..) |*candidate, index| {
        if (!matchesQuery(session, candidate)) continue;
        candidate.matched_priority = minimumSubmittedPriority(session, candidate.source_mask);
        session.rank_order[matched_count] = @intCast(index);
        matched_count += 1;
    }
    std.sort.pdq(
        u32,
        session.rank_order[0..matched_count],
        session,
        candidateBefore,
    );

    const result_count = @min(matched_count, result_limit);
    for (session.rank_order[0..result_count], 0..) |candidate_index, order_index| {
        const candidate = &session.candidates[candidate_index];
        results[order_index] = .{
            .struct_size = @sizeOf(protocol.ResultOutput),
            .source_priority = candidate.matched_priority,
            .entry_handle = candidate.entry_handle,
            .candidate_ordinal = candidate.candidate_ordinal,
            .file_name_rune_count = candidate.file_name_rune_count,
            .order_index = @intCast(order_index),
            .flags = 0,
            .reserved = .{ 0, 0 },
        };
    }

    session.matched_candidate_count = matched_count;
    session.result_count = result_count;
    session.phase = .finalized;
    session.last_operation_epoch = input.operation_epoch;
    session.advanceRevision();
    output.* = .{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.FinalizeOutput),
        .configuration_generation = session.config.generation,
        .state_revision = session.state_revision,
        .query_epoch = input.query_epoch,
        .result_count = result_count,
        .matched_candidate_count = matched_count,
        .submitted_candidate_count = session.submitted_candidate_count,
        .unique_candidate_count = session.unique_candidate_count,
        .flags = 0,
        .reserved = .{ 0, 0, 0 },
    };
    return .ok;
}

fn sourceAllowed(session: *const state.Session, source: protocol.SourceId) bool {
    if (session.source_plan_count == 1) return source == .short_scan;
    return source == .file_name or source == .relative_path or source == .software_name;
}

fn sourceArrayIndex(source: protocol.SourceId) usize {
    return @intFromEnum(source) - 1;
}

fn payloadEqualsStored(
    session: *const state.Session,
    stored: *const state.CandidateSlot,
    input: *const protocol.CandidateInput,
    bytes: []const u8,
) bool {
    return std.mem.eql(
        u8,
        session.candidateFileName(stored),
        protocol.byteSlice(bytes, input.file_name_offset, input.file_name_length).?,
    ) and std.mem.eql(
        u8,
        session.candidateRelativePath(stored),
        protocol.byteSlice(bytes, input.relative_path_offset, input.relative_path_length).?,
    ) and std.mem.eql(
        u8,
        session.candidateSoftwareName(stored),
        protocol.byteSlice(bytes, input.software_name_offset, input.software_name_length).?,
    );
}

fn payloadEqualsInput(
    left: *const protocol.CandidateInput,
    right: *const protocol.CandidateInput,
    bytes: []const u8,
) bool {
    return std.mem.eql(
        u8,
        protocol.byteSlice(bytes, left.file_name_offset, left.file_name_length).?,
        protocol.byteSlice(bytes, right.file_name_offset, right.file_name_length).?,
    ) and std.mem.eql(
        u8,
        protocol.byteSlice(bytes, left.relative_path_offset, left.relative_path_length).?,
        protocol.byteSlice(bytes, right.relative_path_offset, right.relative_path_length).?,
    ) and std.mem.eql(
        u8,
        protocol.byteSlice(bytes, left.software_name_offset, left.software_name_length).?,
        protocol.byteSlice(bytes, right.software_name_offset, right.software_name_length).?,
    );
}

fn copyText(session: *state.Session, value: []const u8) u32 {
    const offset = session.candidate_text_byte_count;
    const end = offset + @as(u32, @intCast(value.len));
    @memcpy(session.candidate_text_bytes[offset..end], value);
    session.candidate_text_byte_count = end;
    return offset;
}

fn matchesQuery(session: *const state.Session, candidate: *const state.CandidateSlot) bool {
    const query = session.query();
    return containsAsciiCaseInsensitive(session.candidateFileName(candidate), query) or
        containsAsciiCaseInsensitive(session.candidateRelativePath(candidate), query) or
        containsAsciiCaseInsensitive(session.candidateSoftwareName(candidate), query);
}

fn containsAsciiCaseInsensitive(haystack: []const u8, needle: []const u8) bool {
    if (needle.len > haystack.len) return false;
    var start: usize = 0;
    while (start <= haystack.len - needle.len) : (start += 1) {
        var index: usize = 0;
        while (index < needle.len) : (index += 1) {
            if (protocol.foldAscii(haystack[start + index]) != protocol.foldAscii(needle[index])) {
                break;
            }
        }
        if (index == needle.len) return true;
    }
    return false;
}

fn minimumSubmittedPriority(session: *const state.Session, source_mask: u32) u32 {
    if ((source_mask & protocol.SourceMask.short_scan) != 0) return 0;
    var priority: u32 = std.math.maxInt(u32);
    if ((source_mask & protocol.SourceMask.file_name) != 0) {
        priority = @min(priority, session.config.file_name_priority);
    }
    if ((source_mask & protocol.SourceMask.relative_path) != 0) {
        priority = @min(priority, session.config.relative_path_priority);
    }
    if ((source_mask & protocol.SourceMask.software_name) != 0) {
        priority = @min(priority, session.config.software_name_priority);
    }
    return priority;
}

fn candidateBefore(session: *state.Session, left_index: u32, right_index: u32) bool {
    const left = &session.candidates[left_index];
    const right = &session.candidates[right_index];
    if (left.matched_priority != right.matched_priority) {
        return left.matched_priority < right.matched_priority;
    }
    if (left.file_name_rune_count != right.file_name_rune_count) {
        return left.file_name_rune_count < right.file_name_rune_count;
    }
    const name_order = std.mem.order(
        u8,
        session.candidateFileName(left),
        session.candidateFileName(right),
    );
    if (name_order != .eq) return name_order == .lt;
    return left.entry_handle < right.entry_handle;
}
