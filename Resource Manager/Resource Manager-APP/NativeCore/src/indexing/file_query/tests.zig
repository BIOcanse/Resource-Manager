const std = @import("std");
const protocol = @import("protocol.zig");
const unicode61 = @import("unicode61.zig");
const session_module = @import("session.zig");
const abi = @import("abi.zig");
const ResultCode = @import("../../common/result_codes.zig").ResultCode;

fn config() protocol.Config {
    return .{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.Config),
        .generation = 7,
        .maximum_query_utf8_byte_count = 128,
        .maximum_query_rune_count = 64,
        .maximum_plan_utf8_byte_count = 1024,
        .maximum_source_plan_count = 3,
        .maximum_candidate_count_per_source = 64,
        .maximum_submitted_candidate_count = 192,
        .maximum_unique_candidate_count = 192,
        .maximum_candidate_submit_batch_count = 32,
        .maximum_candidate_submit_utf8_byte_count = 4096,
        .candidate_text_arena_byte_count = 16384,
        .maximum_file_name_utf8_byte_count = 512,
        .maximum_result_count = 32,
        .entry_index_capacity = 256,
        .ordinal_index_capacity = 256,
        .short_query_rune_threshold = 3,
        .unicode_tokenizer_version = unicode61.tokenizer_version,
        .unicode_remove_diacritics_mode = protocol.unicode_remove_diacritics_mode,
        .trigram_tokenizer_contract_version = protocol.trigram_tokenizer_contract_version,
        .candidate_limit_multiplier = 8,
        .candidate_limit_floor = 16,
        .candidate_limit_ceiling = 64,
        .file_name_priority = 1,
        .relative_path_priority = 2,
        .software_name_priority = 3,
        .flags = 0,
        .text_matching_version = protocol.text_matching_version,
        .resident_byte_budget = 1_000_000,
        .reserved = .{ 0, 0, 0, 0, 0 },
    };
}

fn beginInput(query_epoch: u64, operation_epoch: u64, query_length: usize) protocol.BeginInput {
    return .{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.BeginInput),
        .configuration_generation = 7,
        .operation_epoch = operation_epoch,
        .query_epoch = query_epoch,
        .query_byte_count = @intCast(query_length),
        .requested_result_count = 4,
        .valid_mask = protocol.BeginValid.required,
        .flags = 0,
        .reserved = .{ 0, 0 },
    };
}

fn submitInput(
    query_epoch: u64,
    operation_epoch: u64,
    batch_epoch: u64,
    candidate_count: usize,
    byte_count: usize,
) protocol.SubmitInput {
    return .{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.SubmitInput),
        .configuration_generation = 7,
        .operation_epoch = operation_epoch,
        .query_epoch = query_epoch,
        .batch_epoch = batch_epoch,
        .candidate_count = @intCast(candidate_count),
        .candidate_byte_count = @intCast(byte_count),
        .valid_mask = protocol.SubmitValid.required,
        .flags = 0,
        .reserved = .{ 0, 0 },
    };
}

fn finalizeInput(query_epoch: u64, operation_epoch: u64) protocol.FinalizeInput {
    return .{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.FinalizeInput),
        .configuration_generation = 7,
        .operation_epoch = operation_epoch,
        .query_epoch = query_epoch,
        .result_capacity = 4,
        .reserved_u32 = 0,
        .valid_mask = protocol.FinalizeValid.required,
        .flags = 0,
        .reserved = .{ 0, 0 },
    };
}

test "file query short and FTS plans are explicit and deterministic" {
    var actual_config = config();
    const session = try session_module.Session.create(&actual_config);
    defer session.destroy();

    var output = std.mem.zeroes(protocol.PlanOutput);
    var sources = [_]protocol.SourcePlanOutput{std.mem.zeroes(protocol.SourcePlanOutput)} ** 3;
    var plan_bytes = [_]u8{0} ** 1024;
    var short_input = beginInput(1, 1, 2);
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.begin(
            session,
            &short_input,
            "ab".ptr,
            2,
            &output,
            &sources,
            sources.len,
            &plan_bytes,
            plan_bytes.len,
        ),
    );
    try std.testing.expectEqual(@intFromEnum(protocol.PlanMode.short_scan), output.mode);
    try std.testing.expectEqual(@as(u32, 1), output.source_plan_count);
    try std.testing.expectEqual(
        @intFromEnum(protocol.PrimitiveKind.short_substring_ordered_scan),
        sources[0].primitive_kind,
    );
    try std.testing.expectEqualSlices(u8, "ab", plan_bytes[0..output.plan_utf8_byte_count]);

    var reset_input = protocol.ResetInput{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.ResetInput),
        .configuration_generation = 7,
        .operation_epoch = 2,
        .valid_mask = protocol.ResetValid.required,
        .flags = 0,
        .reserved = .{ 0, 0 },
    };
    try std.testing.expectEqual(ResultCode.ok, session.reset(&reset_input));

    output = std.mem.zeroes(protocol.PlanOutput);
    sources = [_]protocol.SourcePlanOutput{std.mem.zeroes(protocol.SourcePlanOutput)} ** 3;
    @memset(&plan_bytes, 0);
    var fts_input = beginInput(2, 3, 4);
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.begin(
            session,
            &fts_input,
            "abcd".ptr,
            4,
            &output,
            &sources,
            sources.len,
            &plan_bytes,
            plan_bytes.len,
        ),
    );
    try std.testing.expectEqual(@intFromEnum(protocol.PlanMode.fts), output.mode);
    try std.testing.expectEqual(@as(u32, 3), output.source_plan_count);
    try std.testing.expectEqual(@as(u32, 32), output.candidate_limit_per_source);
    try std.testing.expectEqualSlices(
        u8,
        "\"abc\" AND \"bcd\"",
        plan_bytes[sources[0].expression_offset .. sources[0].expression_offset + sources[0].expression_length],
    );
}

test "file query module-local C ABI completes an exact lifecycle" {
    var actual_config = config();
    var handle: ?*anyopaque = null;
    try std.testing.expectEqual(
        @intFromEnum(ResultCode.ok),
        abi.rm_file_query_create(&actual_config, &handle),
    );
    defer abi.rm_file_query_destroy(handle);
    var capacity = std.mem.zeroes(protocol.Capacity);
    try std.testing.expectEqual(
        @intFromEnum(ResultCode.ok),
        abi.rm_file_query_query_capacity(handle, &capacity, @sizeOf(protocol.Capacity)),
    );
    try std.testing.expectEqual(actual_config.maximum_query_utf8_byte_count, capacity.query_utf8_byte_capacity);
    try std.testing.expectEqual(actual_config.maximum_unique_candidate_count, capacity.unique_candidate_capacity);
    try std.testing.expect(capacity.resident_byte_count <= actual_config.resident_byte_budget);

    const query = "GaMe";
    var begin_input = beginInput(1, 1, query.len);
    var plan_output = std.mem.zeroes(protocol.PlanOutput);
    var source_outputs = [_]protocol.SourcePlanOutput{std.mem.zeroes(protocol.SourcePlanOutput)} ** 3;
    var plan_bytes = [_]u8{0} ** 1024;
    try std.testing.expectEqual(
        @intFromEnum(ResultCode.ok),
        abi.rm_file_query_begin(
            handle,
            &begin_input,
            query.ptr,
            @intCast(query.len),
            &plan_output,
            source_outputs[0..].ptr,
            @intCast(source_outputs.len),
            plan_bytes[0..].ptr,
            @intCast(plan_bytes.len),
        ),
    );

    const candidate_bytes = "Game.exe";
    const candidate_inputs = [_]protocol.CandidateInput{
        candidate(1, 1, 0, @intCast(candidate_bytes.len)),
    };
    var submit_input = submitInput(1, 2, 1, candidate_inputs.len, candidate_bytes.len);
    var submit_output = std.mem.zeroes(protocol.SubmitOutput);
    try std.testing.expectEqual(
        @intFromEnum(ResultCode.ok),
        abi.rm_file_query_submit_candidates(
            handle,
            &submit_input,
            candidate_inputs[0..].ptr,
            @intCast(candidate_inputs.len),
            candidate_bytes.ptr,
            @intCast(candidate_bytes.len),
            &submit_output,
        ),
    );

    var finalize_input = finalizeInput(1, 3);
    var results = [_]protocol.ResultOutput{std.mem.zeroes(protocol.ResultOutput)} ** 4;
    var finalize_output = std.mem.zeroes(protocol.FinalizeOutput);
    try std.testing.expectEqual(
        @intFromEnum(ResultCode.ok),
        abi.rm_file_query_finalize(
            handle,
            &finalize_input,
            results[0..].ptr,
            @intCast(results.len),
            &finalize_output,
        ),
    );
    try std.testing.expectEqual(@as(u32, 1), finalize_output.result_count);

    var snapshot = std.mem.zeroes(protocol.SnapshotOutput);
    try std.testing.expectEqual(
        @intFromEnum(ResultCode.ok),
        abi.rm_file_query_snapshot(handle, &snapshot),
    );
    try std.testing.expectEqual(@intFromEnum(protocol.QueryPhase.finalized), snapshot.phase);

    var reset_input = resetInput(4);
    try std.testing.expectEqual(
        @intFromEnum(ResultCode.ok),
        abi.rm_file_query_reset(handle, &reset_input),
    );
}

test "file query deduplicates sources and ranks stable matches" {
    var actual_config = config();
    const session = try session_module.Session.create(&actual_config);
    defer session.destroy();

    var plan_output = std.mem.zeroes(protocol.PlanOutput);
    var sources = [_]protocol.SourcePlanOutput{std.mem.zeroes(protocol.SourcePlanOutput)} ** 3;
    var plan_bytes = [_]u8{0} ** 1024;
    var begin_input = beginInput(11, 1, 4);
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.begin(
            session,
            &begin_input,
            "GaMe".ptr,
            4,
            &plan_output,
            &sources,
            sources.len,
            &plan_bytes,
            plan_bytes.len,
        ),
    );

    const bytes = "Game.exepluginsgame";
    const candidates = [_]protocol.CandidateInput{
        .{
            .struct_size = @sizeOf(protocol.CandidateInput),
            .source_id = @intFromEnum(protocol.SourceId.software_name),
            .entry_handle = 9,
            .candidate_ordinal = 2,
            .file_name_offset = 0,
            .file_name_length = 8,
            .relative_path_offset = 8,
            .relative_path_length = 7,
            .software_name_offset = 15,
            .software_name_length = 4,
            .flags = 0,
            .reserved = .{ 0, 0 },
        },
        .{
            .struct_size = @sizeOf(protocol.CandidateInput),
            .source_id = @intFromEnum(protocol.SourceId.file_name),
            .entry_handle = 9,
            .candidate_ordinal = 1,
            .file_name_offset = 0,
            .file_name_length = 8,
            .relative_path_offset = 8,
            .relative_path_length = 7,
            .software_name_offset = 15,
            .software_name_length = 4,
            .flags = 0,
            .reserved = .{ 0, 0 },
        },
    };
    var submit_input = submitInput(11, 2, 1, candidates.len, bytes.len);
    var submit_output = std.mem.zeroes(protocol.SubmitOutput);
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.submit(
            session,
            &submit_input,
            &candidates,
            candidates.len,
            bytes.ptr,
            bytes.len,
            &submit_output,
        ),
    );
    try std.testing.expectEqual(@as(u32, 2), submit_output.accepted_candidate_count);
    try std.testing.expectEqual(@as(u32, 1), submit_output.duplicate_candidate_count);
    try std.testing.expectEqual(@as(u32, 1), submit_output.total_unique_candidate_count);

    var results = [_]protocol.ResultOutput{std.mem.zeroes(protocol.ResultOutput)} ** 4;
    var finalize_output = std.mem.zeroes(protocol.FinalizeOutput);
    var finalize_input = finalizeInput(11, 3);
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.finalize(
            session,
            &finalize_input,
            &results,
            results.len,
            &finalize_output,
        ),
    );
    try std.testing.expectEqual(@as(u32, 1), finalize_output.result_count);
    try std.testing.expectEqual(@as(u32, 1), results[0].source_priority);
    try std.testing.expectEqual(@as(u32, 1), results[0].candidate_ordinal);
}

test "file query rejects a drifting batch atomically" {
    var actual_config = config();
    const session = try session_module.Session.create(&actual_config);
    defer session.destroy();

    var plan_output = std.mem.zeroes(protocol.PlanOutput);
    var sources = [_]protocol.SourcePlanOutput{std.mem.zeroes(protocol.SourcePlanOutput)} ** 3;
    var plan_bytes = [_]u8{0} ** 1024;
    var begin_input = beginInput(20, 1, 3);
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.begin(
            session,
            &begin_input,
            "abc".ptr,
            3,
            &plan_output,
            &sources,
            sources.len,
            &plan_bytes,
            plan_bytes.len,
        ),
    );

    const bytes = "abc.exebad.exe";
    const candidates = [_]protocol.CandidateInput{
        candidate(1, 1, 0, 7),
        candidate(1, 2, 7, 7),
    };
    var submit_input = submitInput(20, 2, 1, candidates.len, bytes.len);
    var submit_output = std.mem.zeroes(protocol.SubmitOutput);
    try std.testing.expectEqual(
        ResultCode.invalid_argument,
        session_module.submit(
            session,
            &submit_input,
            &candidates,
            candidates.len,
            bytes.ptr,
            bytes.len,
            &submit_output,
        ),
    );
    var snapshot = std.mem.zeroes(protocol.SnapshotOutput);
    try std.testing.expectEqual(ResultCode.ok, session.snapshot(&snapshot));
    try std.testing.expectEqual(@as(u32, 0), snapshot.submitted_candidate_count);
    try std.testing.expectEqual(@as(u32, 0), snapshot.unique_candidate_count);
}

test "file query emits distinct Unicode trigrams and explicit path tokens" {
    var actual_config = config();
    const session = try session_module.Session.create(&actual_config);
    defer session.destroy();

    var output = std.mem.zeroes(protocol.PlanOutput);
    var sources = [_]protocol.SourcePlanOutput{std.mem.zeroes(protocol.SourcePlanOutput)} ** 3;
    var plan_bytes = [_]u8{0} ** 1024;
    const query = "foo/bar baz";
    var input = beginInput(1, 1, query.len);
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.begin(
            session,
            &input,
            query.ptr,
            query.len,
            &output,
            &sources,
            sources.len,
            &plan_bytes,
            plan_bytes.len,
        ),
    );
    try std.testing.expectEqualSlices(
        u8,
        "\"foo\" AND \"bar\" AND \"baz\"",
        plan_bytes[sources[1].expression_offset .. sources[1].expression_offset + sources[1].expression_length],
    );

    var reset_input = resetInput(2);
    try std.testing.expectEqual(ResultCode.ok, session.reset(&reset_input));
    output = std.mem.zeroes(protocol.PlanOutput);
    sources = [_]protocol.SourcePlanOutput{std.mem.zeroes(protocol.SourcePlanOutput)} ** 3;
    @memset(&plan_bytes, 0);
    const unicode_query = "你好你";
    input = beginInput(2, 3, unicode_query.len);
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.begin(
            session,
            &input,
            unicode_query.ptr,
            unicode_query.len,
            &output,
            &sources,
            sources.len,
            &plan_bytes,
            plan_bytes.len,
        ),
    );
    try std.testing.expectEqual(@as(u32, 3), output.query_rune_count);
    try std.testing.expectEqualSlices(
        u8,
        "\"你好你\"",
        plan_bytes[sources[0].expression_offset .. sources[0].expression_offset + sources[0].expression_length],
    );
}

test "file query path planning matches the fixed Unicode 6.1 token categories" {
    const cases = [_]struct {
        query: []const u8,
        expression: []const u8,
    }{
        .{ .query = "foo，bar", .expression = "\"foo\" AND \"bar\"" },
        .{ .query = "foo—bar", .expression = "\"foo\" AND \"bar\"" },
        .{ .query = "aⅣb", .expression = "\"aⅣb\"" },
        .{ .query = "a²b", .expression = "\"a²b\"" },
        .{ .query = "a\u{e000}b", .expression = "\"a\u{e000}b\"" },
        .{ .query = "a\u{0301}b", .expression = "\"a\" AND \"b\"" },
    };

    for (cases) |case| {
        var actual_config = config();
        const session = try session_module.Session.create(&actual_config);
        defer session.destroy();

        var output = std.mem.zeroes(protocol.PlanOutput);
        var sources = [_]protocol.SourcePlanOutput{std.mem.zeroes(protocol.SourcePlanOutput)} ** 3;
        var plan_bytes = [_]u8{0} ** 1024;
        var input = beginInput(1, 1, case.query.len);
        try std.testing.expectEqual(
            ResultCode.ok,
            session_module.begin(
                session,
                &input,
                case.query.ptr,
                @intCast(case.query.len),
                &output,
                &sources,
                sources.len,
                &plan_bytes,
                plan_bytes.len,
            ),
        );
        try std.testing.expectEqual(unicode61.tokenizer_version, output.unicode_tokenizer_version);
        try std.testing.expectEqual(
            protocol.unicode_remove_diacritics_mode,
            output.unicode_remove_diacritics_mode,
        );
        try std.testing.expectEqual(
            protocol.trigram_tokenizer_contract_version,
            output.trigram_tokenizer_contract_version,
        );
        try std.testing.expectEqual(protocol.text_matching_version, output.text_matching_version);
        try std.testing.expectEqualSlices(
            u8,
            case.expression,
            plan_bytes[sources[1].expression_offset .. sources[1].expression_offset + sources[1].expression_length],
        );
    }
}

test "file query escapes quotes and removes repeated trigrams" {
    var actual_config = config();
    const session = try session_module.Session.create(&actual_config);
    defer session.destroy();

    var output = std.mem.zeroes(protocol.PlanOutput);
    var sources = [_]protocol.SourcePlanOutput{std.mem.zeroes(protocol.SourcePlanOutput)} ** 3;
    var plan_bytes = [_]u8{0} ** 1024;
    const quoted = "a\"b";
    var input = beginInput(1, 1, quoted.len);
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.begin(
            session,
            &input,
            quoted.ptr,
            quoted.len,
            &output,
            &sources,
            sources.len,
            &plan_bytes,
            plan_bytes.len,
        ),
    );
    try std.testing.expectEqualSlices(
        u8,
        "\"a\"\"b\"",
        plan_bytes[sources[0].expression_offset .. sources[0].expression_offset + sources[0].expression_length],
    );

    var reset_input = resetInput(2);
    try std.testing.expectEqual(ResultCode.ok, session.reset(&reset_input));
    output = std.mem.zeroes(protocol.PlanOutput);
    sources = [_]protocol.SourcePlanOutput{std.mem.zeroes(protocol.SourcePlanOutput)} ** 3;
    @memset(&plan_bytes, 0);
    input = beginInput(2, 3, 4);
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.begin(
            session,
            &input,
            "aaaa".ptr,
            4,
            &output,
            &sources,
            sources.len,
            &plan_bytes,
            plan_bytes.len,
        ),
    );
    try std.testing.expectEqualSlices(
        u8,
        "\"aaa\"",
        plan_bytes[sources[0].expression_offset .. sources[0].expression_offset + sources[0].expression_length],
    );
}

test "file query text and matching normalization contracts are explicit" {
    try std.testing.expect(!protocol.validText(""));
    try std.testing.expect(!protocol.validText(&.{ 0xff, 0xfe }));
    try std.testing.expect(!protocol.validText(&.{ 0xed, 0xa0, 0x80 }));
    try std.testing.expect(!protocol.validText(&.{ 'a', 0, 'b' }));
    try std.testing.expect(protocol.validText(" AbC "));
    try std.testing.expect(protocol.validText("你好"));
    try std.testing.expectEqualSlices(u8, "AbC", protocol.trimQuery(" \tAbC\r\n"));
    try std.testing.expectEqualSlices(
        u8,
        "\u{3000}AbC\u{3000}",
        protocol.trimQuery("\u{3000}AbC\u{3000}"),
    );
    try std.testing.expectEqual(@as(u8, 'a'), protocol.foldAscii('A'));
    try std.testing.expectEqual(@as(u8, 0xc3), protocol.foldAscii(0xc3));
}

test "file query trims only the published query boundary and rejects an empty result" {
    var actual_config = config();
    const session = try session_module.Session.create(&actual_config);
    defer session.destroy();

    var output = std.mem.zeroes(protocol.PlanOutput);
    var sources = [_]protocol.SourcePlanOutput{std.mem.zeroes(protocol.SourcePlanOutput)} ** 3;
    var plan_bytes = [_]u8{0} ** 1024;
    const query = " \tGaMe\r\n";
    var input = beginInput(1, 1, query.len);
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.begin(
            session,
            &input,
            query.ptr,
            query.len,
            &output,
            &sources,
            sources.len,
            &plan_bytes,
            plan_bytes.len,
        ),
    );
    try std.testing.expectEqual(@as(u32, 4), output.query_rune_count);
    try std.testing.expectEqualSlices(
        u8,
        "GaMe",
        plan_bytes[output.normalized_query_offset .. output.normalized_query_offset + output.normalized_query_length],
    );
    try std.testing.expectEqualSlices(
        u8,
        "\"GaM\" AND \"aMe\"",
        plan_bytes[sources[0].expression_offset .. sources[0].expression_offset + sources[0].expression_length],
    );

    var reset_input = resetInput(2);
    try std.testing.expectEqual(ResultCode.ok, session.reset(&reset_input));
    output = std.mem.zeroes(protocol.PlanOutput);
    sources = [_]protocol.SourcePlanOutput{std.mem.zeroes(protocol.SourcePlanOutput)} ** 3;
    @memset(&plan_bytes, 0);
    input = beginInput(2, 3, 4);
    try std.testing.expectEqual(
        ResultCode.invalid_argument,
        session_module.begin(
            session,
            &input,
            " \t\r\n".ptr,
            4,
            &output,
            &sources,
            sources.len,
            &plan_bytes,
            plan_bytes.len,
        ),
    );
    var snapshot = std.mem.zeroes(protocol.SnapshotOutput);
    try std.testing.expectEqual(ResultCode.ok, session.snapshot(&snapshot));
    try std.testing.expectEqual(@intFromEnum(protocol.QueryPhase.empty), snapshot.phase);
}

test "file query rejects a trigram threshold that cannot produce a trigram" {
    var invalid = config();
    invalid.short_query_rune_threshold = 2;
    try std.testing.expectError(
        error.InvalidConfiguration,
        session_module.Session.create(&invalid),
    );
}

test "file query rejects an unknown Unicode tokenizer contract" {
    var invalid = config();
    invalid.unicode_tokenizer_version += 1;
    try std.testing.expectError(
        error.InvalidConfiguration,
        session_module.Session.create(&invalid),
    );

    invalid = config();
    invalid.unicode_remove_diacritics_mode = 1;
    try std.testing.expectError(
        error.InvalidConfiguration,
        session_module.Session.create(&invalid),
    );

    invalid = config();
    invalid.trigram_tokenizer_contract_version += 1;
    try std.testing.expectError(
        error.InvalidConfiguration,
        session_module.Session.create(&invalid),
    );
}

test "file query rejects an unknown text matching contract" {
    var invalid = config();
    invalid.text_matching_version += 1;
    try std.testing.expectError(
        error.InvalidConfiguration,
        session_module.Session.create(&invalid),
    );
}

test "file query output buffers are transactional and require zero initialization" {
    var actual_config = config();
    const session = try session_module.Session.create(&actual_config);
    defer session.destroy();

    var output = std.mem.zeroes(protocol.PlanOutput);
    output.flags = 1;
    var sources = [_]protocol.SourcePlanOutput{std.mem.zeroes(protocol.SourcePlanOutput)} ** 3;
    var plan_bytes = [_]u8{0} ** 1024;
    var input = beginInput(1, 1, 4);
    try std.testing.expectEqual(
        ResultCode.abi_mismatch,
        session_module.begin(
            session,
            &input,
            "game".ptr,
            4,
            &output,
            &sources,
            sources.len,
            &plan_bytes,
            plan_bytes.len,
        ),
    );

    output = std.mem.zeroes(protocol.PlanOutput);
    var tiny_plan = [_]u8{0};
    try std.testing.expectEqual(
        ResultCode.buffer_too_small,
        session_module.begin(
            session,
            &input,
            "game".ptr,
            4,
            &output,
            &sources,
            sources.len,
            &tiny_plan,
            tiny_plan.len,
        ),
    );
    var snapshot = std.mem.zeroes(protocol.SnapshotOutput);
    try std.testing.expectEqual(ResultCode.ok, session.snapshot(&snapshot));
    try std.testing.expectEqual(@intFromEnum(protocol.QueryPhase.empty), snapshot.phase);
    try std.testing.expectEqual(@as(u32, 0), snapshot.query_byte_count);
}

test "file query ranks by configured source then rune count name and handle" {
    var actual_config = config();
    const session = try session_module.Session.create(&actual_config);
    defer session.destroy();
    try beginQuery(session, 1, "game");

    const bytes = "gameb.exegamea.exegame.exe";
    const candidates = [_]protocol.CandidateInput{
        candidate(2, 2, 0, 9),
        candidate(1, 1, 9, 9),
        candidate(3, 3, 18, 8),
    };
    var submit_input = submitInput(1, 2, 1, candidates.len, bytes.len);
    var submit_output = std.mem.zeroes(protocol.SubmitOutput);
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.submit(
            session,
            &submit_input,
            &candidates,
            candidates.len,
            bytes.ptr,
            bytes.len,
            &submit_output,
        ),
    );
    var results = [_]protocol.ResultOutput{std.mem.zeroes(protocol.ResultOutput)} ** 4;
    var finalize_output = std.mem.zeroes(protocol.FinalizeOutput);
    var finalize_input = finalizeInput(1, 3);
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.finalize(
            session,
            &finalize_input,
            &results,
            results.len,
            &finalize_output,
        ),
    );
    try std.testing.expectEqual(@as(u32, 3), finalize_output.result_count);
    try std.testing.expectEqual(@as(u64, 3), results[0].entry_handle);
    try std.testing.expectEqual(@as(u64, 1), results[1].entry_handle);
    try std.testing.expectEqual(@as(u64, 2), results[2].entry_handle);
}

test "file query duplicate ordinals and stale epochs do not mutate state" {
    var actual_config = config();
    const session = try session_module.Session.create(&actual_config);
    defer session.destroy();
    try beginQuery(session, 4, "game");

    const bytes = "game.exegame.bin";
    const candidates = [_]protocol.CandidateInput{
        candidate(1, 1, 0, 8),
        candidate(2, 1, 8, 8),
    };
    var submit_input = submitInput(4, 2, 1, candidates.len, bytes.len);
    var submit_output = std.mem.zeroes(protocol.SubmitOutput);
    try std.testing.expectEqual(
        ResultCode.invalid_argument,
        session_module.submit(
            session,
            &submit_input,
            &candidates,
            candidates.len,
            bytes.ptr,
            bytes.len,
            &submit_output,
        ),
    );
    var snapshot = std.mem.zeroes(protocol.SnapshotOutput);
    try std.testing.expectEqual(ResultCode.ok, session.snapshot(&snapshot));
    try std.testing.expectEqual(@as(u32, 0), snapshot.submitted_candidate_count);

    var stale_reset = resetInput(1);
    try std.testing.expectEqual(ResultCode.stale_frame, session.reset(&stale_reset));
    var reset_input = resetInput(3);
    try std.testing.expectEqual(ResultCode.ok, session.reset(&reset_input));
    snapshot = std.mem.zeroes(protocol.SnapshotOutput);
    try std.testing.expectEqual(ResultCode.ok, session.snapshot(&snapshot));
    try std.testing.expectEqual(@intFromEnum(protocol.QueryPhase.empty), snapshot.phase);
}

test "file query enforces the explicit per-source candidate budget atomically" {
    var actual_config = config();
    actual_config.maximum_candidate_count_per_source = 4;
    actual_config.maximum_submitted_candidate_count = 12;
    actual_config.maximum_unique_candidate_count = 12;
    actual_config.maximum_candidate_submit_batch_count = 2;
    actual_config.maximum_result_count = 1;
    actual_config.entry_index_capacity = 16;
    actual_config.ordinal_index_capacity = 16;
    actual_config.candidate_limit_multiplier = 1;
    actual_config.candidate_limit_floor = 1;
    actual_config.candidate_limit_ceiling = 4;
    const session = try session_module.Session.create(&actual_config);
    defer session.destroy();

    var plan_output = std.mem.zeroes(protocol.PlanOutput);
    var sources = [_]protocol.SourcePlanOutput{std.mem.zeroes(protocol.SourcePlanOutput)} ** 3;
    var plan_bytes = [_]u8{0} ** 1024;
    var begin_input = beginInput(1, 1, 4);
    begin_input.requested_result_count = 1;
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.begin(
            session,
            &begin_input,
            "game".ptr,
            4,
            &plan_output,
            &sources,
            sources.len,
            &plan_bytes,
            plan_bytes.len,
        ),
    );
    const bytes = "game.exegame.bin";
    const candidates = [_]protocol.CandidateInput{
        candidate(1, 1, 0, 8),
        candidate(2, 2, 8, 8),
    };
    var submit_input = submitInput(1, 2, 1, candidates.len, bytes.len);
    var submit_output = std.mem.zeroes(protocol.SubmitOutput);
    try std.testing.expectEqual(
        ResultCode.invalid_argument,
        session_module.submit(
            session,
            &submit_input,
            &candidates,
            candidates.len,
            bytes.ptr,
            bytes.len,
            &submit_output,
        ),
    );
    var snapshot = std.mem.zeroes(protocol.SnapshotOutput);
    try std.testing.expectEqual(ResultCode.ok, session.snapshot(&snapshot));
    try std.testing.expectEqual(@as(u32, 0), snapshot.submitted_candidate_count);
}

test "file query finalize capacity failure is retryable without phase mutation" {
    var actual_config = config();
    const session = try session_module.Session.create(&actual_config);
    defer session.destroy();
    try beginQuery(session, 1, "game");

    var tiny_results = [_]protocol.ResultOutput{std.mem.zeroes(protocol.ResultOutput)};
    var output = std.mem.zeroes(protocol.FinalizeOutput);
    var input = finalizeInput(1, 2);
    try std.testing.expectEqual(
        ResultCode.buffer_too_small,
        session_module.finalize(
            session,
            &input,
            &tiny_results,
            tiny_results.len,
            &output,
        ),
    );
    var snapshot = std.mem.zeroes(protocol.SnapshotOutput);
    try std.testing.expectEqual(ResultCode.ok, session.snapshot(&snapshot));
    try std.testing.expectEqual(@intFromEnum(protocol.QueryPhase.planned), snapshot.phase);

    input.result_capacity = 1;
    output = std.mem.zeroes(protocol.FinalizeOutput);
    try std.testing.expectEqual(
        ResultCode.buffer_too_small,
        session_module.finalize(
            session,
            &input,
            &tiny_results,
            tiny_results.len,
            &output,
        ),
    );
    snapshot = std.mem.zeroes(protocol.SnapshotOutput);
    try std.testing.expectEqual(ResultCode.ok, session.snapshot(&snapshot));
    try std.testing.expectEqual(@intFromEnum(protocol.QueryPhase.planned), snapshot.phase);

    var results = [_]protocol.ResultOutput{std.mem.zeroes(protocol.ResultOutput)} ** 4;
    input.result_capacity = results.len;
    output = std.mem.zeroes(protocol.FinalizeOutput);
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.finalize(
            session,
            &input,
            &results,
            results.len,
            &output,
        ),
    );
    try std.testing.expectEqual(@as(u32, 0), output.result_count);
}

test "file query reconfigure is generation strict and shape stable" {
    var actual_config = config();
    const session = try session_module.Session.create(&actual_config);
    defer session.destroy();

    var stale = actual_config;
    try std.testing.expectEqual(ResultCode.stale_frame, session.reconfigure(&stale));
    var changed_shape = actual_config;
    changed_shape.generation = 8;
    changed_shape.maximum_result_count = 31;
    try std.testing.expectEqual(ResultCode.invalid_argument, session.reconfigure(&changed_shape));
    var hot = actual_config;
    hot.generation = 8;
    hot.candidate_limit_multiplier = 4;
    hot.file_name_priority = 3;
    hot.relative_path_priority = 1;
    hot.software_name_priority = 2;
    try std.testing.expectEqual(ResultCode.ok, session.reconfigure(&hot));
    var snapshot = std.mem.zeroes(protocol.SnapshotOutput);
    try std.testing.expectEqual(ResultCode.ok, session.snapshot(&snapshot));
    try std.testing.expectEqual(@as(u64, 8), snapshot.configuration_generation);
}

test "file query runs one thousand lifecycles from the create-time resident set" {
    var actual_config = config();
    const session = try session_module.Session.create(&actual_config);
    defer session.destroy();

    var operation_epoch: u64 = 1;
    var query_epoch: u64 = 1;
    var iteration: u32 = 0;
    while (iteration < 1000) : (iteration += 1) {
        var plan_output = std.mem.zeroes(protocol.PlanOutput);
        var sources = [_]protocol.SourcePlanOutput{
            std.mem.zeroes(protocol.SourcePlanOutput),
        } ** 3;
        var plan_bytes = [_]u8{0} ** 1024;
        var begin_input = beginInput(query_epoch, operation_epoch, 3);
        try std.testing.expectEqual(
            ResultCode.ok,
            session_module.begin(
                session,
                &begin_input,
                "abc".ptr,
                3,
                &plan_output,
                &sources,
                sources.len,
                &plan_bytes,
                plan_bytes.len,
            ),
        );

        var results = [_]protocol.ResultOutput{std.mem.zeroes(protocol.ResultOutput)} ** 4;
        var finalize_output = std.mem.zeroes(protocol.FinalizeOutput);
        var finalize_input = finalizeInput(query_epoch, operation_epoch + 1);
        try std.testing.expectEqual(
            ResultCode.ok,
            session_module.finalize(
                session,
                &finalize_input,
                &results,
                results.len,
                &finalize_output,
            ),
        );
        var reset_input = resetInput(operation_epoch + 2);
        try std.testing.expectEqual(ResultCode.ok, session.reset(&reset_input));
        operation_epoch += 3;
        query_epoch += 1;
    }
    var snapshot = std.mem.zeroes(protocol.SnapshotOutput);
    try std.testing.expectEqual(ResultCode.ok, session.snapshot(&snapshot));
    try std.testing.expectEqual(@intFromEnum(protocol.QueryPhase.empty), snapshot.phase);
    try std.testing.expectEqual(@as(u64, 1000), snapshot.last_query_epoch);
}

fn beginQuery(session: *session_module.Session, query_epoch: u64, query: []const u8) !void {
    var output = std.mem.zeroes(protocol.PlanOutput);
    var sources = [_]protocol.SourcePlanOutput{std.mem.zeroes(protocol.SourcePlanOutput)} ** 3;
    var plan_bytes = [_]u8{0} ** 1024;
    var input = beginInput(query_epoch, 1, query.len);
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.begin(
            session,
            &input,
            query.ptr,
            @intCast(query.len),
            &output,
            &sources,
            sources.len,
            &plan_bytes,
            plan_bytes.len,
        ),
    );
}

fn resetInput(operation_epoch: u64) protocol.ResetInput {
    return .{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.ResetInput),
        .configuration_generation = 7,
        .operation_epoch = operation_epoch,
        .valid_mask = protocol.ResetValid.required,
        .flags = 0,
        .reserved = .{ 0, 0 },
    };
}

fn candidate(entry: u64, ordinal: u32, offset: u32, length: u32) protocol.CandidateInput {
    return .{
        .struct_size = @sizeOf(protocol.CandidateInput),
        .source_id = @intFromEnum(protocol.SourceId.file_name),
        .entry_handle = entry,
        .candidate_ordinal = ordinal,
        .file_name_offset = offset,
        .file_name_length = length,
        .relative_path_offset = offset,
        .relative_path_length = length,
        .software_name_offset = offset,
        .software_name_length = length,
        .flags = 0,
        .reserved = .{ 0, 0 },
    };
}
