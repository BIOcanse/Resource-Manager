const std = @import("std");
const protocol = @import("protocol.zig");
const unicode61 = @import("unicode61.zig");

test "file query ABI has fixed extern layout and known masks" {
    try std.testing.expectEqual(@as(u32, 0x0002_0000), protocol.abi_version);
    try std.testing.expectEqual(@as(u32, 2), protocol.unicode_remove_diacritics_mode);
    try std.testing.expectEqual(
        @as(u32, 0x0001_0000),
        protocol.trigram_tokenizer_contract_version,
    );
    try std.testing.expectEqual(@as(u32, 0x0001_0000), protocol.text_matching_version);
    try std.testing.expectEqual(protocol.BeginValid.required, protocol.BeginValid.known);
    try std.testing.expectEqual(protocol.SubmitValid.required, protocol.SubmitValid.known);
    try std.testing.expectEqual(protocol.FinalizeValid.required, protocol.FinalizeValid.known);
    try std.testing.expectEqual(protocol.ResetValid.required, protocol.ResetValid.known);
    try std.testing.expectEqual(@as(usize, 0), @offsetOf(protocol.Config, "abi_version"));
    try std.testing.expectEqual(@as(usize, 8), @offsetOf(protocol.Config, "generation"));
    try std.testing.expectEqual(@as(usize, 76), @offsetOf(protocol.Config, "unicode_tokenizer_version"));
    try std.testing.expectEqual(@as(usize, 80), @offsetOf(protocol.Config, "unicode_remove_diacritics_mode"));
    try std.testing.expectEqual(@as(usize, 84), @offsetOf(protocol.Config, "trigram_tokenizer_contract_version"));
    try std.testing.expectEqual(@as(usize, 116), @offsetOf(protocol.Config, "text_matching_version"));
    try std.testing.expectEqual(@as(usize, 120), @offsetOf(protocol.Config, "resident_byte_budget"));
    try std.testing.expectEqual(@as(usize, 0), @offsetOf(protocol.BeginInput, "abi_version"));
    try std.testing.expectEqual(@as(usize, 8), @offsetOf(protocol.BeginInput, "configuration_generation"));
    try std.testing.expectEqual(@as(usize, 0), @offsetOf(protocol.CandidateInput, "struct_size"));
    try std.testing.expectEqual(@as(usize, 8), @offsetOf(protocol.CandidateInput, "entry_handle"));
    try std.testing.expectEqual(@as(usize, 0), @offsetOf(protocol.ResultOutput, "struct_size"));
    try std.testing.expectEqual(@as(usize, 8), @offsetOf(protocol.ResultOutput, "entry_handle"));
    try std.testing.expectEqual(@as(usize, 64), @offsetOf(protocol.PlanOutput, "normalized_query_offset"));
    try std.testing.expectEqual(@as(usize, 68), @offsetOf(protocol.PlanOutput, "normalized_query_length"));
    try std.testing.expectEqual(@as(usize, 72), @offsetOf(protocol.PlanOutput, "unicode_tokenizer_version"));
    try std.testing.expectEqual(@as(usize, 76), @offsetOf(protocol.PlanOutput, "unicode_remove_diacritics_mode"));
    try std.testing.expectEqual(@as(usize, 80), @offsetOf(protocol.PlanOutput, "trigram_tokenizer_contract_version"));
    try std.testing.expectEqual(@as(usize, 84), @offsetOf(protocol.PlanOutput, "text_matching_version"));
    try std.testing.expectEqual(@as(usize, 88), @offsetOf(protocol.PlanOutput, "flags"));
    try std.testing.expectEqual(@as(usize, 168), @sizeOf(protocol.Config));
    try std.testing.expectEqual(@as(usize, 104), @sizeOf(protocol.Capacity));
    try std.testing.expectEqual(@as(usize, 72), @sizeOf(protocol.BeginInput));
    try std.testing.expectEqual(@as(usize, 48), @sizeOf(protocol.SourcePlanOutput));
    try std.testing.expectEqual(@as(usize, 120), @sizeOf(protocol.PlanOutput));
    try std.testing.expectEqual(@as(usize, 72), @sizeOf(protocol.CandidateInput));
    try std.testing.expectEqual(@as(usize, 80), @sizeOf(protocol.SubmitInput));
    try std.testing.expectEqual(@as(usize, 88), @sizeOf(protocol.SubmitOutput));
    try std.testing.expectEqual(@as(usize, 72), @sizeOf(protocol.FinalizeInput));
    try std.testing.expectEqual(@as(usize, 48), @sizeOf(protocol.ResultOutput));
    try std.testing.expectEqual(@as(usize, 80), @sizeOf(protocol.FinalizeOutput));
    try std.testing.expectEqual(@as(usize, 56), @sizeOf(protocol.ResetInput));
    try std.testing.expectEqual(@as(usize, 128), @sizeOf(protocol.SnapshotOutput));
}

test "file query Unicode 6.1 token table is fixed and ordered" {
    try std.testing.expectEqual(@as(u32, 0x0006_0100), unicode61.tokenizer_version);
    try std.testing.expectEqual(@as(u64, 0xea4c_ba4a_6b0f_ffcf), unicode61.table_fingerprint);
    try std.testing.expectEqual(@as(usize, 547), unicode61.token_ranges.len);
    try std.testing.expectEqual(@as(u21, 0x30), unicode61.token_ranges[0].first);
    try std.testing.expectEqual(@as(u21, 0x39), unicode61.token_ranges[0].last);
    try std.testing.expectEqual(
        @as(u21, 0x100000),
        unicode61.token_ranges[unicode61.token_ranges.len - 1].first,
    );
    try std.testing.expectEqual(
        @as(u21, 0x10fffd),
        unicode61.token_ranges[unicode61.token_ranges.len - 1].last,
    );

    var previous_last: ?u21 = null;
    for (unicode61.token_ranges) |range| {
        try std.testing.expect(range.first <= range.last);
        if (previous_last) |last| try std.testing.expect(last + 1 < range.first);
        previous_last = range.last;
    }
    var fingerprint: u64 = 14_695_981_039_346_656_037;
    for (unicode61.token_ranges) |range| {
        for ([_]u32{ range.first, range.last }) |scalar| {
            var remaining = scalar;
            var byte_index: u32 = 0;
            while (byte_index < 4) : (byte_index += 1) {
                fingerprint = (fingerprint ^ @as(u8, @truncate(remaining))) *%
                    1_099_511_628_211;
                remaining >>= 8;
            }
        }
    }
    try std.testing.expectEqual(unicode61.table_fingerprint, fingerprint);

    try std.testing.expect(unicode61.isTokenScalar('a'));
    try std.testing.expect(unicode61.isTokenScalar('9'));
    try std.testing.expect(unicode61.isTokenScalar(0x4f60));
    try std.testing.expect(unicode61.isTokenScalar(0x2163));
    try std.testing.expect(unicode61.isTokenScalar(0x00b2));
    try std.testing.expect(unicode61.isTokenScalar(0xe000));
    try std.testing.expect(!unicode61.isTokenScalar(0x0301));
    try std.testing.expect(!unicode61.isTokenScalar(0x2014));
    try std.testing.expect(!unicode61.isTokenScalar(0xff0c));
    try std.testing.expect(!unicode61.isTokenScalar(0xd800));
}
