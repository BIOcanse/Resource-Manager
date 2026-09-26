const std = @import("std");
const protocol = @import("protocol.zig");

pub fn assert() void {
    @setEvalBranchQuota(200_000);
    assertType(protocol.Handle128, 16, &.{ 0, 8 }, 0xb426_f46b_bb4c_b015, "Handle128");
    assertType(protocol.Config, 168, &.{
        0,  4,   8,   16,  32,  48, 52, 56, 60, 64, 68, 72, 76, 80, 84, 88, 92,
        96, 104, 112, 120, 128,
    }, 0xc1b0_f8b7_2416_6137, "Config");
    assertType(protocol.Capacity, 112, &.{
        0, 4, 8, 12, 16, 20, 24, 28, 32, 48, 64, 72, 80,
    }, 0x991e_dc3c_323f_85cf, "Capacity");
    assertType(protocol.SubmitInput, 224, &.{
        0,   4,   8,   16,  24,  40,  56,  72, 88, 104, 112, 116, 120, 128, 136,
        144, 152, 160, 168, 176, 184, 192,
    }, 0xcc0e_f89b_e013_1dae, "SubmitInput");
    assertType(protocol.SubmitOutput, 72, &.{ 0, 4, 8, 24, 32, 40, 44, 48 }, 0x00f0_7618_7358_d27d, "SubmitOutput");
    assertType(protocol.CancelInput, 96, &.{ 0, 4, 8, 16, 24, 40, 48, 56, 64 }, 0xe4da_ffb8_66f5_53bd, "CancelInput");
    assertType(protocol.CancelOutput, 64, &.{ 0, 4, 8, 24, 32, 40 }, 0x1f07_23a8_b536_bfe8, "CancelOutput");
    assertType(protocol.PlanInput, 80, &.{ 0, 4, 8, 16, 24, 32, 40, 44, 48 }, 0x8aa6_7ab4_270b_25c1, "PlanInput");
    assertType(protocol.PlanOutput, 96, &.{
        0, 4, 8, 16, 24, 32, 36, 40, 44, 48, 56, 64,
    }, 0x4aec_76e4_40e1_7138, "PlanOutput");
    assertType(protocol.ActionOutput, 112, &.{
        0, 4, 8, 16, 24, 40, 56, 64, 68, 72, 80, 88,
    }, 0xa485_5e66_68c8_99f5, "ActionOutput");
    assertType(protocol.ActionFeedbackInput, 168, &.{
        0,   4, 8, 16, 24, 32, 40, 56, 72, 76, 80, 88, 96, 112, 128, 136,
        144,
    }, 0xb764_a67f_2e4f_7192, "ActionFeedbackInput");
    assertType(protocol.CompletionInput, 152, &.{
        0, 4, 8, 16, 24, 40, 56, 60, 64, 72, 80, 96, 112, 120, 128,
    }, 0xda70_f93a_2b8a_5e22, "CompletionInput");
    assertType(protocol.ProgressInput, 192, &.{
        0,   4,   8,   16, 32, 48, 56, 64, 72, 76, 80, 88, 96, 104, 120, 136,
        152, 160, 168,
    }, 0x685c_824b_171a_8a2b, "ProgressInput");
    assertType(protocol.SnapshotOutput, 120, &.{
        0, 4, 8, 16, 24, 32, 40, 48, 56, 60, 64, 68, 72, 76, 80, 88,
    }, 0x7955_8aec_d431_e323, "SnapshotOutput");
    assertType(protocol.ReadInput, 64, &.{ 0, 4, 8, 16, 24, 32, 40 }, 0xd190_215b_fae2_af31, "ReadInput");
    assertType(protocol.OperationOutput, 400, &.{
        0,   4,   8,   24,  40,  56,  72,  88,  104, 120, 136, 144, 152, 160, 168,
        176, 184, 192, 200, 208, 216, 224, 228, 232, 236, 240, 248, 256, 264, 272,
        280, 284, 288, 296, 304, 312, 328, 344, 360, 368, 376, 384,
    }, 0xa390_8d1e_d0e7_f80c, "OperationOutput");
    assertType(protocol.PersistenceInput, 64, &.{ 0, 4, 8, 16, 24, 32 }, 0xf392_24d0_0a28_e516, "PersistenceInput");
    assertType(protocol.PersistenceImportInput, 96, &.{
        0, 4, 8, 16, 24, 32, 40, 56, 64,
    }, 0xef3c_d882_556d_2d4c, "PersistenceImportInput");
    assertType(protocol.PersistenceHeader, 200, &.{
        0,   4,   8,   16,  32,  48,  56, 64, 72, 80, 88, 96, 104, 112, 120, 128,
        136, 140, 144, 152, 160, 168,
    }, 0x7eb8_097a_cbd7_d2ab, "PersistenceHeader");
    assertFieldTypes(protocol.Handle128, 0x73aa_13ed_16b8_446d, "Handle128");
    assertFieldTypes(protocol.Config, 0x2e44_8d69_f189_47d6, "Config");
    assertFieldTypes(protocol.Capacity, 0xa923_561d_e633_bba8, "Capacity");
    assertFieldTypes(protocol.SubmitInput, 0x78c6_eedf_b9e6_9166, "SubmitInput");
    assertFieldTypes(protocol.SubmitOutput, 0xac31_9443_2a48_6338, "SubmitOutput");
    assertFieldTypes(protocol.CancelInput, 0x2456_62bc_153b_1075, "CancelInput");
    assertFieldTypes(protocol.CancelOutput, 0x8d4d_33ff_7f06_8640, "CancelOutput");
    assertFieldTypes(protocol.PlanInput, 0xc0dc_881e_07a0_2851, "PlanInput");
    assertFieldTypes(protocol.PlanOutput, 0x54bf_d49f_cd5a_a80f, "PlanOutput");
    assertFieldTypes(protocol.ActionOutput, 0xff4b_f5ad_2405_a4c8, "ActionOutput");
    assertFieldTypes(protocol.ActionFeedbackInput, 0x9111_c985_1d0b_4e26, "ActionFeedbackInput");
    assertFieldTypes(protocol.CompletionInput, 0x8ee2_c55c_c82f_24ae, "CompletionInput");
    assertFieldTypes(protocol.ProgressInput, 0x9292_7cac_49ea_5b36, "ProgressInput");
    assertFieldTypes(protocol.SnapshotOutput, 0xe511_2915_daca_5dce, "SnapshotOutput");
    assertFieldTypes(protocol.ReadInput, 0xfee2_60ab_6a35_f557, "ReadInput");
    assertFieldTypes(protocol.OperationOutput, 0x27e6_f0aa_9f43_d7bc, "OperationOutput");
    assertFieldTypes(protocol.PersistenceInput, 0xf983_18ae_4518_20b7, "PersistenceInput");
    assertFieldTypes(protocol.PersistenceImportInput, 0x485b_2788_e2f1_59d9, "PersistenceImportInput");
    assertFieldTypes(protocol.PersistenceHeader, 0x75ee_b7c6_eac9_32a6, "PersistenceHeader");
    assertValues();
}

pub fn expect() !void {
    try expectType(protocol.Handle128, 16, &.{ 0, 8 }, 0xb426_f46b_bb4c_b015);
    try expectType(protocol.Config, 168, &.{
        0,  4,   8,   16,  32,  48, 52, 56, 60, 64, 68, 72, 76, 80, 84, 88, 92,
        96, 104, 112, 120, 128,
    }, 0xc1b0_f8b7_2416_6137);
    try expectType(protocol.Capacity, 112, &.{
        0, 4, 8, 12, 16, 20, 24, 28, 32, 48, 64, 72, 80,
    }, 0x991e_dc3c_323f_85cf);
    try expectType(protocol.SubmitInput, 224, &.{
        0,   4,   8,   16,  24,  40,  56,  72, 88, 104, 112, 116, 120, 128, 136,
        144, 152, 160, 168, 176, 184, 192,
    }, 0xcc0e_f89b_e013_1dae);
    try expectType(protocol.SubmitOutput, 72, &.{ 0, 4, 8, 24, 32, 40, 44, 48 }, 0x00f0_7618_7358_d27d);
    try expectType(protocol.CancelInput, 96, &.{ 0, 4, 8, 16, 24, 40, 48, 56, 64 }, 0xe4da_ffb8_66f5_53bd);
    try expectType(protocol.CancelOutput, 64, &.{ 0, 4, 8, 24, 32, 40 }, 0x1f07_23a8_b536_bfe8);
    try expectType(protocol.PlanInput, 80, &.{ 0, 4, 8, 16, 24, 32, 40, 44, 48 }, 0x8aa6_7ab4_270b_25c1);
    try expectType(protocol.PlanOutput, 96, &.{
        0, 4, 8, 16, 24, 32, 36, 40, 44, 48, 56, 64,
    }, 0x4aec_76e4_40e1_7138);
    try expectType(protocol.ActionOutput, 112, &.{
        0, 4, 8, 16, 24, 40, 56, 64, 68, 72, 80, 88,
    }, 0xa485_5e66_68c8_99f5);
    try expectType(protocol.ActionFeedbackInput, 168, &.{
        0,   4, 8, 16, 24, 32, 40, 56, 72, 76, 80, 88, 96, 112, 128, 136,
        144,
    }, 0xb764_a67f_2e4f_7192);
    try expectType(protocol.CompletionInput, 152, &.{
        0, 4, 8, 16, 24, 40, 56, 60, 64, 72, 80, 96, 112, 120, 128,
    }, 0xda70_f93a_2b8a_5e22);
    try expectType(protocol.ProgressInput, 192, &.{
        0,   4,   8,   16, 32, 48, 56, 64, 72, 76, 80, 88, 96, 104, 120, 136,
        152, 160, 168,
    }, 0x685c_824b_171a_8a2b);
    try expectType(protocol.SnapshotOutput, 120, &.{
        0, 4, 8, 16, 24, 32, 40, 48, 56, 60, 64, 68, 72, 76, 80, 88,
    }, 0x7955_8aec_d431_e323);
    try expectType(protocol.ReadInput, 64, &.{ 0, 4, 8, 16, 24, 32, 40 }, 0xd190_215b_fae2_af31);
    try expectType(protocol.OperationOutput, 400, &.{
        0,   4,   8,   24,  40,  56,  72,  88,  104, 120, 136, 144, 152, 160, 168,
        176, 184, 192, 200, 208, 216, 224, 228, 232, 236, 240, 248, 256, 264, 272,
        280, 284, 288, 296, 304, 312, 328, 344, 360, 368, 376, 384,
    }, 0xa390_8d1e_d0e7_f80c);
    try expectType(protocol.PersistenceInput, 64, &.{ 0, 4, 8, 16, 24, 32 }, 0xf392_24d0_0a28_e516);
    try expectType(protocol.PersistenceImportInput, 96, &.{
        0, 4, 8, 16, 24, 32, 40, 56, 64,
    }, 0xef3c_d882_556d_2d4c);
    try expectType(protocol.PersistenceHeader, 200, &.{
        0,   4,   8,   16,  32,  48,  56, 64, 72, 80, 88, 96, 104, 112, 120, 128,
        136, 140, 144, 152, 160, 168,
    }, 0x7eb8_097a_cbd7_d2ab);
    try expectFieldTypes(protocol.Handle128, 0x73aa_13ed_16b8_446d);
    try expectFieldTypes(protocol.Config, 0x2e44_8d69_f189_47d6);
    try expectFieldTypes(protocol.Capacity, 0xa923_561d_e633_bba8);
    try expectFieldTypes(protocol.SubmitInput, 0x78c6_eedf_b9e6_9166);
    try expectFieldTypes(protocol.SubmitOutput, 0xac31_9443_2a48_6338);
    try expectFieldTypes(protocol.CancelInput, 0x2456_62bc_153b_1075);
    try expectFieldTypes(protocol.CancelOutput, 0x8d4d_33ff_7f06_8640);
    try expectFieldTypes(protocol.PlanInput, 0xc0dc_881e_07a0_2851);
    try expectFieldTypes(protocol.PlanOutput, 0x54bf_d49f_cd5a_a80f);
    try expectFieldTypes(protocol.ActionOutput, 0xff4b_f5ad_2405_a4c8);
    try expectFieldTypes(protocol.ActionFeedbackInput, 0x9111_c985_1d0b_4e26);
    try expectFieldTypes(protocol.CompletionInput, 0x8ee2_c55c_c82f_24ae);
    try expectFieldTypes(protocol.ProgressInput, 0x9292_7cac_49ea_5b36);
    try expectFieldTypes(protocol.SnapshotOutput, 0xe511_2915_daca_5dce);
    try expectFieldTypes(protocol.ReadInput, 0xfee2_60ab_6a35_f557);
    try expectFieldTypes(protocol.OperationOutput, 0x27e6_f0aa_9f43_d7bc);
    try expectFieldTypes(protocol.PersistenceInput, 0xf983_18ae_4518_20b7);
    try expectFieldTypes(protocol.PersistenceImportInput, 0x485b_2788_e2f1_59d9);
    try expectFieldTypes(protocol.PersistenceHeader, 0x75ee_b7c6_eac9_32a6);
}

pub fn assertValues() void {
    assertValue(protocol.abi_version, 0x0001_0000, "abi_version");
    assertValue(protocol.no_slot, 0xffff_ffff, "no_slot");

    assertValue(@intFromEnum(protocol.OperationState.queued), 1, "OperationState.queued");
    assertValue(@intFromEnum(protocol.OperationState.start_pending), 2, "OperationState.start_pending");
    assertValue(@intFromEnum(protocol.OperationState.running), 3, "OperationState.running");
    assertValue(@intFromEnum(protocol.OperationState.cancel_pending), 4, "OperationState.cancel_pending");
    assertValue(@intFromEnum(protocol.OperationState.retry_wait), 5, "OperationState.retry_wait");
    assertValue(@intFromEnum(protocol.OperationState.recovery_pending), 6, "OperationState.recovery_pending");
    assertValue(@intFromEnum(protocol.OperationState.succeeded), 7, "OperationState.succeeded");
    assertValue(@intFromEnum(protocol.OperationState.failed), 8, "OperationState.failed");
    assertValue(@intFromEnum(protocol.OperationState.canceled), 9, "OperationState.canceled");
    assertValue(@intFromEnum(protocol.OperationState.state_uncertain), 10, "OperationState.state_uncertain");

    assertValue(@intFromEnum(protocol.ActionKind.start), 1, "ActionKind.start");
    assertValue(@intFromEnum(protocol.ActionKind.cancel), 2, "ActionKind.cancel");
    assertValue(@intFromEnum(protocol.ActionKind.recover), 3, "ActionKind.recover");

    assertValue(@intFromEnum(protocol.ActionFeedbackOutcome.started), 1, "ActionFeedbackOutcome.started");
    assertValue(@intFromEnum(protocol.ActionFeedbackOutcome.start_retryable_failure), 2, "ActionFeedbackOutcome.start_retryable_failure");
    assertValue(@intFromEnum(protocol.ActionFeedbackOutcome.start_terminal_failure), 3, "ActionFeedbackOutcome.start_terminal_failure");
    assertValue(@intFromEnum(protocol.ActionFeedbackOutcome.cancel_completed), 4, "ActionFeedbackOutcome.cancel_completed");
    assertValue(@intFromEnum(protocol.ActionFeedbackOutcome.cancel_retryable_failure), 5, "ActionFeedbackOutcome.cancel_retryable_failure");
    assertValue(@intFromEnum(protocol.ActionFeedbackOutcome.recovered_queued), 6, "ActionFeedbackOutcome.recovered_queued");
    assertValue(@intFromEnum(protocol.ActionFeedbackOutcome.recovered_succeeded), 7, "ActionFeedbackOutcome.recovered_succeeded");
    assertValue(@intFromEnum(protocol.ActionFeedbackOutcome.recovered_failed), 8, "ActionFeedbackOutcome.recovered_failed");
    assertValue(@intFromEnum(protocol.ActionFeedbackOutcome.recovered_canceled), 9, "ActionFeedbackOutcome.recovered_canceled");
    assertValue(@intFromEnum(protocol.ActionFeedbackOutcome.recovered_uncertain), 10, "ActionFeedbackOutcome.recovered_uncertain");

    assertValue(@intFromEnum(protocol.CompletionOutcome.succeeded), 1, "CompletionOutcome.succeeded");
    assertValue(@intFromEnum(protocol.CompletionOutcome.retryable_failure), 2, "CompletionOutcome.retryable_failure");
    assertValue(@intFromEnum(protocol.CompletionOutcome.terminal_failure), 3, "CompletionOutcome.terminal_failure");
    assertValue(@intFromEnum(protocol.CompletionOutcome.canceled), 4, "CompletionOutcome.canceled");
    assertValue(@intFromEnum(protocol.CompletionOutcome.state_uncertain), 5, "CompletionOutcome.state_uncertain");

    assertValue(protocol.ConfigFlags.known, 0, "ConfigFlags.known");
    assertValue(protocol.SubmitValid.domain, 1, "SubmitValid.domain");
    assertValue(protocol.SubmitValid.title, 2, "SubmitValid.title");
    assertValue(protocol.SubmitValid.request, 4, "SubmitValid.request");
    assertValue(protocol.SubmitValid.known, 7, "SubmitValid.known");
    assertValue(protocol.OperationFlags.domain_valid, 1, "OperationFlags.domain_valid");
    assertValue(protocol.OperationFlags.title_valid, 2, "OperationFlags.title_valid");
    assertValue(protocol.OperationFlags.request_valid, 4, "OperationFlags.request_valid");
    assertValue(protocol.OperationFlags.result_valid, 8, "OperationFlags.result_valid");
    assertValue(protocol.OperationFlags.error_valid, 16, "OperationFlags.error_valid");
    assertValue(protocol.OperationFlags.progress_valid, 32, "OperationFlags.progress_valid");
    assertValue(protocol.OperationFlags.cancel_requested, 64, "OperationFlags.cancel_requested");
    assertValue(protocol.OperationFlags.terminal, 128, "OperationFlags.terminal");
    assertValue(protocol.OperationFlags.imported_recovery, 256, "OperationFlags.imported_recovery");
    assertValue(protocol.OperationFlags.known, 0x1ff, "OperationFlags.known");
    assertValue(protocol.SubmitOutputFlags.inserted, 1, "SubmitOutputFlags.inserted");
    assertValue(protocol.SubmitOutputFlags.active_domain_duplicate, 2, "SubmitOutputFlags.active_domain_duplicate");
    assertValue(protocol.SubmitOutputFlags.operation_id_duplicate, 4, "SubmitOutputFlags.operation_id_duplicate");
    assertValue(protocol.SubmitOutputFlags.known, 7, "SubmitOutputFlags.known");
    assertValue(protocol.ActionFlags.cancel_requested, 1, "ActionFlags.cancel_requested");
    assertValue(protocol.ActionFlags.execution_timeout, 2, "ActionFlags.execution_timeout");
    assertValue(protocol.ActionFlags.imported_recovery, 4, "ActionFlags.imported_recovery");
    assertValue(protocol.ActionFlags.known, 7, "ActionFlags.known");
    assertValue(protocol.FeedbackValid.result, 1, "FeedbackValid.result");
    assertValue(protocol.FeedbackValid.error_handle, 2, "FeedbackValid.error_handle");
    assertValue(protocol.FeedbackValid.known, 3, "FeedbackValid.known");
    assertValue(protocol.CompletionValid.result, 1, "CompletionValid.result");
    assertValue(protocol.CompletionValid.error_handle, 2, "CompletionValid.error_handle");
    assertValue(protocol.CompletionValid.known, 3, "CompletionValid.known");
    assertValue(protocol.ProgressValid.percent_milli, 1, "ProgressValid.percent_milli");
    assertValue(protocol.ProgressValid.bytes_done, 2, "ProgressValid.bytes_done");
    assertValue(protocol.ProgressValid.bytes_total, 4, "ProgressValid.bytes_total");
    assertValue(protocol.ProgressValid.speed_bytes_per_second, 8, "ProgressValid.speed_bytes_per_second");
    assertValue(protocol.ProgressValid.stage, 16, "ProgressValid.stage");
    assertValue(protocol.ProgressValid.message, 32, "ProgressValid.message");
    assertValue(protocol.ProgressValid.checkpoint, 64, "ProgressValid.checkpoint");
    assertValue(protocol.ProgressValid.known, 0x7f, "ProgressValid.known");
    assertValue(protocol.SnapshotFlags.next_wake_valid, 1, "SnapshotFlags.next_wake_valid");
    assertValue(protocol.SnapshotFlags.known, 1, "SnapshotFlags.known");
    assertValue(protocol.PersistenceFlags.known, 0, "PersistenceFlags.known");
}

pub fn expectValues() !void {
    try std.testing.expectEqual(@as(u32, 0x0001_0000), protocol.abi_version);
    try std.testing.expectEqual(@as(u32, 0xffff_ffff), protocol.no_slot);
    try expectEnum(protocol.OperationState, &.{ 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 });
    try expectEnum(protocol.ActionKind, &.{ 1, 2, 3 });
    try expectEnum(protocol.ActionFeedbackOutcome, &.{ 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 });
    try expectEnum(protocol.CompletionOutcome, &.{ 1, 2, 3, 4, 5 });

    try std.testing.expectEqual(@as(u64, 0), protocol.ConfigFlags.known);
    try expectU64(&.{ 1, 2, 4, 7 }, &.{ protocol.SubmitValid.domain, protocol.SubmitValid.title, protocol.SubmitValid.request, protocol.SubmitValid.known });
    try expectU64(&.{ 1, 2, 4, 8, 16, 32, 64, 128, 256, 0x1ff }, &.{
        protocol.OperationFlags.domain_valid,
        protocol.OperationFlags.title_valid,
        protocol.OperationFlags.request_valid,
        protocol.OperationFlags.result_valid,
        protocol.OperationFlags.error_valid,
        protocol.OperationFlags.progress_valid,
        protocol.OperationFlags.cancel_requested,
        protocol.OperationFlags.terminal,
        protocol.OperationFlags.imported_recovery,
        protocol.OperationFlags.known,
    });
    try expectU32(&.{ 1, 2, 4, 7 }, &.{
        protocol.SubmitOutputFlags.inserted,
        protocol.SubmitOutputFlags.active_domain_duplicate,
        protocol.SubmitOutputFlags.operation_id_duplicate,
        protocol.SubmitOutputFlags.known,
    });
    try expectU64(&.{ 1, 2, 4, 7 }, &.{ protocol.ActionFlags.cancel_requested, protocol.ActionFlags.execution_timeout, protocol.ActionFlags.imported_recovery, protocol.ActionFlags.known });
    try expectU64(&.{ 1, 2, 3 }, &.{ protocol.FeedbackValid.result, protocol.FeedbackValid.error_handle, protocol.FeedbackValid.known });
    try expectU64(&.{ 1, 2, 3 }, &.{ protocol.CompletionValid.result, protocol.CompletionValid.error_handle, protocol.CompletionValid.known });
    try expectU64(&.{ 1, 2, 4, 8, 16, 32, 64, 0x7f }, &.{
        protocol.ProgressValid.percent_milli,
        protocol.ProgressValid.bytes_done,
        protocol.ProgressValid.bytes_total,
        protocol.ProgressValid.speed_bytes_per_second,
        protocol.ProgressValid.stage,
        protocol.ProgressValid.message,
        protocol.ProgressValid.checkpoint,
        protocol.ProgressValid.known,
    });
    try expectU64(&.{ 1, 1 }, &.{ protocol.SnapshotFlags.next_wake_valid, protocol.SnapshotFlags.known });
    try std.testing.expectEqual(@as(u64, 0), protocol.PersistenceFlags.known);
}

fn assertType(
    comptime T: type,
    comptime size: usize,
    comptime offsets: []const usize,
    comptime expected_name_hash: u64,
    comptime name: []const u8,
) void {
    if (@sizeOf(T) != size) @compileError("operation coordinator ABI size drift: " ++ name);
    const fields = @typeInfo(T).@"struct".fields;
    if (fields.len != offsets.len) {
        @compileError("operation coordinator ABI field-count drift: " ++ name);
    }
    if (fieldNameHash(T) != expected_name_hash) {
        @compileError("operation coordinator ABI field-name/order drift: " ++ name);
    }
    inline for (fields, 0..) |field, index| {
        if (@offsetOf(T, field.name) != offsets[index]) {
            @compileError("operation coordinator ABI field-offset drift: " ++ name);
        }
    }
}

fn expectType(
    comptime T: type,
    expected_size: usize,
    comptime offsets: []const usize,
    expected_name_hash: u64,
) !void {
    try std.testing.expectEqual(expected_size, @sizeOf(T));
    const fields = @typeInfo(T).@"struct".fields;
    try std.testing.expectEqual(offsets.len, fields.len);
    try std.testing.expectEqual(expected_name_hash, fieldNameHash(T));
    inline for (fields, 0..) |field, index| {
        try std.testing.expectEqual(offsets[index], @offsetOf(T, field.name));
    }
}

fn fieldNameHash(comptime T: type) u64 {
    var hash: u64 = 0xcbf2_9ce4_8422_2325;
    inline for (@typeInfo(T).@"struct".fields) |field| {
        inline for (field.name) |byte| {
            hash = (hash ^ byte) *% 0x0000_0100_0000_01b3;
        }
        hash *%= 0x0000_0100_0000_01b3;
    }
    return hash;
}

fn fieldTypeHash(comptime T: type) u64 {
    var hash: u64 = 0xcbf2_9ce4_8422_2325;
    inline for (@typeInfo(T).@"struct".fields) |field| {
        mixType(&hash, field.type);
        mixInteger(&hash, @sizeOf(field.type));
        mixInteger(&hash, @alignOf(field.type));
    }
    return hash;
}

fn assertFieldTypes(
    comptime T: type,
    comptime expected: u64,
    comptime name: []const u8,
) void {
    if (fieldTypeHash(T) != expected) {
        @compileError("operation coordinator ABI field-type drift: " ++ name);
    }
}

fn expectFieldTypes(comptime T: type, expected: u64) !void {
    try std.testing.expectEqual(expected, fieldTypeHash(T));
}

fn mixType(hash: *u64, comptime T: type) void {
    switch (@typeInfo(T)) {
        .int => |int| {
            mixByte(hash, 1);
            mixByte(hash, if (int.signedness == .signed) 1 else 0);
            mixInteger(hash, int.bits);
        },
        .array => |array| {
            mixByte(hash, 2);
            mixInteger(hash, array.len);
            mixType(hash, array.child);
        },
        .@"struct" => |structure| {
            mixByte(hash, 3);
            mixInteger(hash, @sizeOf(T));
            mixInteger(hash, @alignOf(T));
            inline for (structure.fields) |field| {
                inline for (field.name) |byte| mixByte(hash, byte);
                mixByte(hash, 0);
                mixType(hash, field.type);
            }
        },
        else => @compileError("unsupported operation coordinator ABI field type"),
    }
}

fn mixInteger(hash: *u64, value: anytype) void {
    var remaining: u64 = @intCast(value);
    inline for (0..8) |_| {
        mixByte(hash, @truncate(remaining));
        remaining >>= 8;
    }
}

fn mixByte(hash: *u64, byte: u8) void {
    hash.* = (hash.* ^ byte) *% 0x0000_0100_0000_01b3;
}

fn assertValue(
    comptime actual: anytype,
    comptime expected: @TypeOf(actual),
    comptime name: []const u8,
) void {
    if (actual != expected) {
        @compileError("operation coordinator ABI value drift: " ++ name);
    }
}

fn expectEnum(comptime E: type, comptime expected: []const u32) !void {
    const fields = @typeInfo(E).@"enum".fields;
    try std.testing.expectEqual(expected.len, fields.len);
    inline for (fields, 0..) |field, index| {
        try std.testing.expectEqual(expected[index], field.value);
    }
}

fn expectU64(comptime expected: []const u64, comptime actual: []const u64) !void {
    try std.testing.expectEqual(expected.len, actual.len);
    inline for (expected, actual) |expected_value, actual_value| {
        try std.testing.expectEqual(expected_value, actual_value);
    }
}

fn expectU32(comptime expected: []const u32, comptime actual: []const u32) !void {
    try std.testing.expectEqual(expected.len, actual.len);
    inline for (expected, actual) |expected_value, actual_value| {
        try std.testing.expectEqual(expected_value, actual_value);
    }
}
