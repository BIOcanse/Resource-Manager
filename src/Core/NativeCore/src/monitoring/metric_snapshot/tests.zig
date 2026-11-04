const std = @import("std");
const protocol = @import("protocol.zig");
const state = @import("state.zig");
const scratch_module = @import("scratch.zig");
const session_module = @import("session.zig");
const persistence = @import("persistence.zig");
const ResultCode = @import("../../common/result_codes.zig").ResultCode;

const source_count = 3;
const rule_count = 3;
const metric_count = 2;

test "selected current reads return 100 exact cells from a large table without whole-table buffers" {
    const current = @import("current.zig");
    var config = testConfig(1);
    const session = try state.Session.create(&config);
    defer session.destroy();
    const count = 10_000;
    const all = try std.testing.allocator.alloc(protocol.CurrentValue, count);
    defer std.testing.allocator.free(all);
    const resident = try std.testing.allocator.alignedAlloc(u8, .@"8", 2 * count * @sizeOf(protocol.CurrentValue));
    defer std.testing.allocator.free(resident);
    for (all, 0..) |*value, index| value.* = .{ .dataset_handle = 1 + index / 100,
        .cell_handle = 1 + index % 100, .value_kind = 3, .flags = 1,
        .value_bits = std.math.maxInt(u64) - index, .value_high_bits = 0 };
    var datasets: [100]u64 = undefined;
    var keys: [100]protocol.CurrentKey = undefined;
    for (&datasets, &keys, 0..) |*dataset, *key, index| {
        dataset.* = index + 1;
        key.* = .{ .dataset_handle = 100 - index, .cell_handle = 51 };
    }
    const input = protocol.CurrentCommitInput{ .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.CurrentCommitInput), .cell_capacity = count,
        .byte_capacity = 0, .flags = 0, .reserved = 0 };
    try std.testing.expectEqual(ResultCode.ok, current.commit(session, &input, &datasets, &.{}, all, "", resident));
    var output: protocol.CurrentReadOutput = undefined;
    try std.testing.expectEqual(ResultCode.buffer_too_small, current.readSelected(session, &keys, &output, &.{}, &.{}));
    try std.testing.expectEqual(@as(u32, 100), output.cell_count);
    try std.testing.expectEqual(@as(u32, 0), output.byte_count);
    var values: [100]protocol.CurrentValue = undefined;
    try std.testing.expectEqual(ResultCode.ok, current.readSelected(session, &keys, &output, &values, &.{}));
    for (keys, values) |key, value| try std.testing.expectEqualDeep(all[(key.dataset_handle - 1) * 100 + 50], value);
    const missing = [_]protocol.CurrentKey{ .{ .dataset_handle = 101, .cell_handle = 51 }, .{ .dataset_handle = 1, .cell_handle = 101 } };
    try std.testing.expectEqual(ResultCode.ok, current.readSelected(session, &missing, &output, &.{}, &.{}));
    try std.testing.expectEqual(@as(u32, 0), output.cell_count);
    try std.testing.expectEqual(ResultCode.ok, current.readSelected(session, &.{}, &output, &.{}, &.{}));
}

test "selected current reads compact bytes preserve exact time and reject aliases without publication changes" {
    const current = @import("current.zig");
    const native_abi = @import("abi.zig");
    var config = testConfig(1);
    const session = try state.Session.create(&config);
    defer session.destroy();
    var resident: [2 * (4 * @sizeOf(protocol.CurrentValue) + 8)]u8 align(8) = undefined;
    const input = protocol.CurrentCommitInput{ .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.CurrentCommitInput), .cell_capacity = 4,
        .byte_capacity = 8, .flags = 0, .reserved = 0 };
    const all = [_]protocol.CurrentValue{
        .{ .dataset_handle = 1, .cell_handle = 1, .value_kind = 5, .flags = 1, .value_bits = 0, .value_high_bits = 3 },
        .{ .dataset_handle = 1, .cell_handle = 2, .value_kind = 2, .flags = 1, .value_bits = @bitCast(@as(i64, -638_000_000_000_000_001)), .value_high_bits = 0 },
        .{ .dataset_handle = 2, .cell_handle = 1, .value_kind = 5, .flags = 1, .value_bits = 3, .value_high_bits = 2 },
        .{ .dataset_handle = 2, .cell_handle = 2, .value_kind = 5, .flags = 0, .value_bits = 0, .value_high_bits = 0 },
    };
    try std.testing.expectEqual(ResultCode.ok, current.commit(session, &input, &.{1, 2}, &.{}, &all, "abcxy", &resident));
    const keys = [_]protocol.CurrentKey{ .{ .dataset_handle = 2, .cell_handle = 1 },
        .{ .dataset_handle = 1, .cell_handle = 2 }, .{ .dataset_handle = 2, .cell_handle = 1 },
        .{ .dataset_handle = 2, .cell_handle = 2 } };
    var output: protocol.CurrentReadOutput = undefined;
    var values: [4]protocol.CurrentValue = undefined;
    var bytes: [4]u8 = undefined;
    try std.testing.expectEqual(@intFromEnum(ResultCode.abi_mismatch), native_abi.rm_metric_snapshot_read_current_selected(@ptrCast(session),
        protocol.abi_version - 1, &keys, keys.len, &output, &values, values.len, &bytes, bytes.len));
    try std.testing.expectEqual(@intFromEnum(ResultCode.ok), native_abi.rm_metric_snapshot_read_current_selected(@ptrCast(session),
        protocol.abi_version, &keys, keys.len, &output, &values, values.len, &bytes, bytes.len));
    try std.testing.expectEqualStrings("xyxy", &bytes);
    try std.testing.expectEqual(@as(u64, 0), values[0].value_bits);
    try std.testing.expectEqualDeep(all[1], values[1]);
    try std.testing.expectEqual(@as(u64, 2), values[2].value_bits);
    try std.testing.expectEqualDeep(all[3], values[3]);
    @memset(&bytes, 0xA5);
    const before = values;
    try std.testing.expectEqual(ResultCode.buffer_too_small, current.readSelected(session, &keys, &output, &values, bytes[0..3]));
    try std.testing.expectEqualDeep(before, values);
    for (bytes) |byte| try std.testing.expectEqual(@as(u8, 0xA5), byte);
    const alias: *protocol.CurrentReadOutput = @ptrCast(&values);
    try std.testing.expectEqual(ResultCode.invalid_argument, current.readSelected(session, &keys, alias, &values, &bytes));
    try std.testing.expectEqualDeep(before, values);
    const key_alias: [*]const protocol.CurrentKey = @ptrCast(&values);
    try std.testing.expectEqual(ResultCode.invalid_argument, current.readSelected(session, key_alias[0..1], &output, &values, &bytes));
    try std.testing.expectEqual(ResultCode.invalid_argument, current.readSelected(session, &keys, &output, session.current_values, &bytes));
    try std.testing.expectEqual(ResultCode.invalid_argument, current.readSelected(session, &.{.{ .dataset_handle = 0, .cell_handle = 1 }}, &output, &values, &bytes));
    try std.testing.expectEqualSlices(u8, std.mem.sliceAsBytes(&all), std.mem.sliceAsBytes(session.current_values));
    try std.testing.expectEqualStrings("abcxy", session.current_bytes);
}

test "metric current publication preserves typed cells and atomically replaces requested datasets" {
    const current = @import("current.zig");
    var config = testConfig(1);
    const session = try state.Session.create(&config);
    defer session.destroy();
    var resident: [2 * (8 * @sizeOf(protocol.CurrentValue) + 16)]u8 align(8) = undefined;
    const input = protocol.CurrentCommitInput{ .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.CurrentCommitInput), .cell_capacity = 8,
        .byte_capacity = 16, .flags = 0, .reserved = 0 };
    const initial = [_]protocol.CurrentValue{
        .{ .dataset_handle = 1, .cell_handle = 1, .value_kind = 1, .flags = 1,
            .value_bits = @bitCast(@as(f64, 12.5)), .value_high_bits = 0 },
        .{ .dataset_handle = 1, .cell_handle = 2, .value_kind = 2, .flags = 1,
            .value_bits = @bitCast(@as(i64, -638_000_000_000_000_000)), .value_high_bits = 0 },
        .{ .dataset_handle = 2, .cell_handle = 1, .value_kind = 4, .flags = 1,
            .value_bits = 0xfedcba9876543210, .value_high_bits = 0xabcdef0123456789 },
        .{ .dataset_handle = 3, .cell_handle = 1, .value_kind = 5, .flags = 1,
            .value_bits = 0, .value_high_bits = 3 },
        .{ .dataset_handle = 4, .cell_handle = 1, .value_kind = 6, .flags = 1,
            .value_bits = 1, .value_high_bits = 0 },
    };
    try std.testing.expectEqual(ResultCode.ok, current.commit(session, &input, &.{ 1, 2, 3, 4 }, &.{}, &initial, "abc", &resident));
    // Inputs are not borrowed after publication, and a failed multi-dataset
    // transaction must not publish even the valid first dataset.
    var replacement = [_]protocol.CurrentValue{ initial[0], initial[4] };
    replacement[0].value_bits = @bitCast(@as(f64, 99));
    replacement[1].value_bits = 2;
    try std.testing.expectEqual(ResultCode.invalid_argument, current.commit(session, &input, &.{ 1, 4 }, &.{}, &replacement, "", &resident));
    var output: protocol.CurrentReadOutput = undefined;
    var values: [8]protocol.CurrentValue = undefined;
    var bytes: [16]u8 = undefined;
    try std.testing.expectEqual(ResultCode.ok, current.read(session, &output, &values, &bytes));
    try std.testing.expectEqual(@as(u32, 5), output.cell_count);
    try std.testing.expectEqualSlices(u8, std.mem.sliceAsBytes(&initial), std.mem.sliceAsBytes(values[0..5]));
    try std.testing.expectEqualStrings("abc", bytes[0..3]);
    replacement[1].flags = 0;
    replacement[1].value_bits = 0;
    try std.testing.expectEqual(ResultCode.ok, current.commit(session, &input, &.{ 1, 4 }, &.{}, &replacement, "", &resident));
    try std.testing.expectEqual(ResultCode.ok, current.read(session, &output, &values, &bytes));
    try std.testing.expectEqual(@as(u32, 4), output.cell_count);
    try std.testing.expectEqual(@as(f64, 99), @as(f64, @bitCast(values[0].value_bits)));
    try std.testing.expectEqual(initial[2].value_high_bits, values[1].value_high_bits);
    try std.testing.expectEqualStrings("abc", bytes[0..3]);
    try std.testing.expectEqual(@as(u32, 0), values[3].flags);
    const byte_replacement = protocol.CurrentValue{ .dataset_handle = 1, .cell_handle = 1,
        .value_kind = 5, .flags = 1, .value_bits = 0, .value_high_bits = 2 };
    try std.testing.expectEqual(ResultCode.ok, current.commit(session, &input, &.{1}, &.{}, &.{byte_replacement}, "xy", &resident));
    try std.testing.expectEqual(ResultCode.ok, current.read(session, &output, &values, &bytes));
    try std.testing.expectEqualStrings("xyabc", bytes[0..5]);
    try std.testing.expectEqual(@as(u64, 2), values[2].value_bits);
    // Collector workspace reset is not a public current-value transaction.
    const reset = protocol.ControlInput{ .abi_version = protocol.abi_version, .struct_size = @sizeOf(protocol.ControlInput),
        .configuration_generation = config.generation, .operation_epoch = 1, .command_at_milliseconds = 100,
        .flags = 0, .reserved = .{ 0, 0 } };
    try std.testing.expectEqual(ResultCode.ok, session_module.reset(session, &reset));
    try std.testing.expectEqual(ResultCode.ok, current.read(session, &output, &values, &bytes));
    try std.testing.expectEqual(@as(u32, 4), output.cell_count);
    try std.testing.expectEqual(ResultCode.ok, current.commit(session, &input, &.{}, &.{ 2, 3 }, &.{}, "", &resident));
    try std.testing.expectEqual(ResultCode.ok, current.read(session, &output, &values, &bytes));
    try std.testing.expectEqual(@as(u32, 2), output.cell_count);
    try std.testing.expectEqual(@as(u32, 2), output.byte_count);
    try std.testing.expectEqualStrings("xy", bytes[0..2]);
    try std.testing.expectEqual(@as(u64, 4), values[1].dataset_handle);
}

test "metric current capacity and alias failures leave the whole publication untouched" {
    const current = @import("current.zig");
    var config = testConfig(1);
    const session = try state.Session.create(&config);
    defer session.destroy();
    var resident: [2 * (@sizeOf(protocol.CurrentValue) + 8)]u8 align(8) = undefined;
    const input = protocol.CurrentCommitInput{ .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.CurrentCommitInput), .cell_capacity = 1,
        .byte_capacity = 8, .flags = 0, .reserved = 0 };
    var value = protocol.CurrentValue{ .dataset_handle = 1, .cell_handle = 1,
        .value_kind = 3, .flags = 1, .value_bits = std.math.maxInt(u64), .value_high_bits = 0 };
    try std.testing.expectEqual(ResultCode.ok, current.commit(session, &input, &.{1}, &.{}, &.{value}, "", &resident));
    value.dataset_handle = 2;
    try std.testing.expectEqual(ResultCode.buffer_too_small, current.commit(session, &input, &.{2}, &.{}, &.{value}, "", &resident));
    var output: protocol.CurrentReadOutput = undefined;
    try std.testing.expectEqual(ResultCode.buffer_too_small, current.read(session, &output, &.{}, &.{}));
    try std.testing.expectEqual(@as(u32, 1), output.cell_count);
    try std.testing.expectEqual(ResultCode.invalid_argument, current.read(session, &output, session.current_values, &.{}));
    var values: [1]protocol.CurrentValue = undefined;
    try std.testing.expectEqual(ResultCode.ok, current.read(session, &output, &values, &.{}));
    try std.testing.expectEqual(@as(u64, 1), values[0].dataset_handle);
    try std.testing.expectEqual(std.math.maxInt(u64), values[0].value_bits);
    var empty_input = input;
    empty_input.cell_capacity = 0;
    empty_input.byte_capacity = 0;
    try std.testing.expectEqual(ResultCode.buffer_too_small, current.commit(session, &empty_input, &.{}, &.{}, &.{}, "", &.{}));
    try std.testing.expectEqual(ResultCode.ok, current.commit(session, &empty_input, &.{}, &.{1}, &.{}, "", &.{}));
    try std.testing.expectEqual(ResultCode.ok, current.read(session, &output, &.{}, &.{}));
    try std.testing.expectEqual(@as(u32, 0), output.cell_count);
}

test "metric current storage and collector workspace cannot alias in either direction" {
    const current = @import("current.zig");
    var config = testConfig(1);
    const session_scratch = try testScratch(&config);
    defer std.testing.allocator.free(session_scratch);
    const session = try state.Session.create(&config);
    defer session.destroy();
    var sources = testSources();
    var rules = testRules();
    try std.testing.expectEqual(ResultCode.ok, replaceCatalog(session, 1, 1, 100, &sources, &rules));
    var resident: [2 * (8 * @sizeOf(protocol.CurrentValue) + 1024)]u8 align(8) = undefined;
    const input = protocol.CurrentCommitInput{ .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.CurrentCommitInput), .cell_capacity = 8,
        .byte_capacity = 1024, .flags = 0, .reserved = 0 };
    const value = protocol.CurrentValue{ .dataset_handle = 1, .cell_handle = 1,
        .value_kind = 3, .flags = 1, .value_bits = 42, .value_high_bits = 0 };
    try std.testing.expectEqual(ResultCode.ok, current.commit(session, &input, &.{1}, &.{}, &.{value}, "", &resident));
    var source_plans = std.mem.zeroes([4]protocol.SourcePlanOutput);
    var metric_plans = std.mem.zeroes([4]protocol.MetricPlanOutput);
    var output = std.mem.zeroes(protocol.PlanOutput);
    const requested = [_]protocol.PlanMetricInput{planMetric(200)};
    const modes = testModes();
    try std.testing.expectEqual(ResultCode.invalid_argument, runPlan(session, &resident, 2, 1, 200, 0, &requested, &modes, &output, &source_plans, &metric_plans));
    try std.testing.expectEqual(ResultCode.ok, runPlan(session, session_scratch, 2, 1, 200, 0, &requested, &modes, &output, &source_plans, &metric_plans));
    var header = metricCompletion(3, 1, 10, 1, 1, 300, 1, 1, 0);
    header.capability_generation = source_plans[0].capability_generation;
    header.plan_token_fingerprint = source_plans[0].plan_token_fingerprint;
    try std.testing.expectEqual(ResultCode.invalid_argument, session_module.beginCompletion(session, &header, &resident));
    try std.testing.expectEqual(ResultCode.ok, session_module.beginCompletion(session, &header, session_scratch));
    try std.testing.expectEqual(ResultCode.invalid_argument, current.commit(session, &input, &.{}, &.{}, &.{}, "", session_scratch[0..resident.len]));
    const finish = finalizeInput(3, 1, 10, 300);
    try std.testing.expectEqual(ResultCode.ok, session_module.abortCompletion(session, &finish));
    try std.testing.expectEqualDeep(value, session.current_values[0]);
}

test "metric current double bank obeys exact resident budget independently of collector limits" {
    const current = @import("current.zig");
    var config = testConfig(1);
    const session = try state.Session.create(&config);
    defer session.destroy();
    config.generation += 1;
    config.resident_byte_budget = session.requiredByteCount() + 2 * (@sizeOf(protocol.CurrentValue) + 8);
    try std.testing.expectEqual(ResultCode.ok, session_module.reconfigure(session, &config));
    var resident: [2 * (@sizeOf(protocol.CurrentValue) + 8)]u8 align(8) = undefined;
    var input = protocol.CurrentCommitInput{ .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.CurrentCommitInput), .cell_capacity = 1,
        .byte_capacity = 8, .flags = 0, .reserved = 0 };
    const value = protocol.CurrentValue{ .dataset_handle = 1, .cell_handle = 1,
        .value_kind = 5, .flags = 1, .value_bits = 0, .value_high_bits = 3 };
    try std.testing.expectEqual(ResultCode.ok, current.commit(session, &input, &.{1}, &.{}, &.{value}, "abc", &resident));
    try std.testing.expectEqual(config.resident_byte_budget, session.requiredByteCount());
    var larger: [2 * (2 * @sizeOf(protocol.CurrentValue) + 8)]u8 align(8) = undefined;
    input.cell_capacity = 2;
    try std.testing.expectEqual(ResultCode.invalid_argument, current.commit(session, &input, &.{1}, &.{}, &.{value}, "abc", &larger));
    try std.testing.expectEqualDeep(value, session.current_values[0]);
    try std.testing.expectEqualStrings("abc", session.current_bytes);
}

test "floating counters commit fractional SDK deltas atomically for ratio and percentage" {
    inline for (.{ protocol.MetricKind.floating_counter_ratio, protocol.MetricKind.floating_counter_percentage }) |kind| {
        var config = testConfig(1);
        const session_scratch = try testScratch(&config);
        defer std.testing.allocator.free(session_scratch);
        const session = try state.Session.create(&config);
        defer session.destroy();
        var sources = testSources();
        var rules = testRules();
        for (rules[0..2]) |*rule| {
            rule.metric_kind = @intFromEnum(kind);
            rule.flags = protocol.MetricFlags.nonnegative;
            rule.minimum_value_bits = @bitCast(@as(f64, 0));
            rule.maximum_value_bits = @bitCast(@as(f64, if (kind == .floating_counter_percentage) 100 else std.math.floatMax(f64)));
        }
        try std.testing.expectEqual(ResultCode.ok, replaceCatalog(session, 1, 1, 100, &sources, &rules));
        const requested = [_]protocol.PlanMetricInput{planMetric(100)};
        const requested_rules = [_]protocol.RequestedMetricInput{requestedRule(1_001)};
        const modes = testModes();
        for (0..2) |index| {
            const at: u64 = 200 + index * 1000;
            const epoch: u64 = 2 + index * 3;
            var output = std.mem.zeroes(protocol.PlanOutput);
            var source_plans = std.mem.zeroes([4]protocol.SourcePlanOutput);
            var metric_plans = std.mem.zeroes([4]protocol.MetricPlanOutput);
            try std.testing.expectEqual(ResultCode.ok, runPlan(session, session_scratch, epoch, index + 1, at, 0, &requested, &modes, &output, &source_plans, &metric_plans));
            var header = metricCompletion(epoch + 1, index + 1, 10, 1, index + 1, at, 1, 1, 0);
            header.catalog_generation = session.catalog_generation;
            header.capability_generation = source_plans[0].capability_generation;
            header.plan_token_fingerprint = source_plans[0].plan_token_fingerprint;
            try std.testing.expectEqual(ResultCode.ok, session_module.beginCompletion(session, &header, session_scratch));
            try std.testing.expectEqual(ResultCode.ok, session_module.submitRequested(session, &requested_rules));
            var observation = unsignedObservation(1_001, 100, 10, rules[0].scope_handle, at, 0);
            observation.flags = protocol.ObservationFlags.floating_counter_pair;
            observation.value_kind = @intFromEnum(protocol.ValueKind.float64);
            observation.source_observation_sequence = index + 1;
            observation.value_bits = @bitCast(@as(f64, if (index == 0) 1000.125 else 1000.625));
            observation.value_high_bits = @bitCast(@as(f64, if (index == 0) 10.125 else 12.125));
            try std.testing.expectEqual(ResultCode.ok, session_module.submitObservations(session, &.{observation}));
            var finalize = finalizeInput(epoch + 1, index + 1, 10, at);
            finalize.catalog_generation = session.catalog_generation;
            const rule_index = session.active.findRule(1_001).?;
            if (index == 1) {
                finalize.catalog_generation += 1;
                try std.testing.expectEqual(ResultCode.abi_mismatch, session_module.finalizeCompletion(session, &finalize));
                try std.testing.expectEqual(@as(u64, @bitCast(@as(f64, 1000.125))), session.active.rules[rule_index].counter_baseline.primary);
                finalize.catalog_generation -= 1;
            }
            try std.testing.expectEqual(ResultCode.ok, session_module.finalizeCompletion(session, &finalize));
            const record = session.active.rules[rule_index];
            if (index == 0) {
                try std.testing.expectEqual(protocol.MetricStatus.unavailable, record.status);
                var expanded: [4]protocol.MetricDefinitionInput = undefined;
                @memcpy(expanded[0..3], &rules);
                expanded[3] = rules[2];
                expanded[3].rule_handle = 3001;
                expanded[3].metric_handle = 300;
                expanded[3].semantic_fingerprint += 1;
                try std.testing.expectEqual(ResultCode.ok, replaceCatalog(session, epoch + 2, 2, at, &sources, &expanded));
                try std.testing.expectEqual(record.counter_baseline.primary, session.active.rules[0].counter_baseline.primary);
                try std.testing.expectEqual(record.counter_baseline.reference, session.active.rules[0].counter_baseline.reference);
                try std.testing.expect(session.active.rules[0].counter_baseline.valid);
            } else {
                try std.testing.expectEqual(protocol.MetricStatus.current, record.status);
                try std.testing.expectEqual(@as(f64, if (kind == .floating_counter_percentage) 25 else 0.25), @as(f64, @bitCast(record.value_bits)));
                try std.testing.expectEqual(@as(u64, 2000), record.sample_duration_milliseconds);
            }
        }
    }
}

test "inventory-only plan selects its owner without any numeric metric source" {
    var config = testConfig(1);
    const bytes = try testScratch(&config);
    defer std.testing.allocator.free(bytes);
    const session = try state.Session.create(&config);
    defer session.destroy();
    const sources = testSources();
    const rules = testRules();
    try std.testing.expectEqual(ResultCode.ok, replaceCatalog(session, 1, 1, 100, &sources, &rules));
    var source_plans = std.mem.zeroes([4]protocol.SourcePlanOutput);
    var metric_plans = std.mem.zeroes([4]protocol.MetricPlanOutput);
    var output = std.mem.zeroes(protocol.PlanOutput);
    const modes = testModes();
    try std.testing.expectEqual(ResultCode.abi_mismatch, runPlan(session, bytes, 2, 1, 200, 0, &.{}, &modes, &output, &source_plans, &metric_plans));
    try std.testing.expectEqual(ResultCode.ok, runPlan(session, bytes, 2, 1, 200, protocol.PlanFlags.include_gpu_inventory, &.{}, &modes, &output, &source_plans, &metric_plans));
    try std.testing.expectEqual(@as(u32, 1), output.source_plan_count);
    try std.testing.expectEqual(@as(u32, 0), output.metric_plan_count);
    try std.testing.expectEqual(protocol.PlanOutputFlags.gpu_inventory_selected, output.flags);
    try std.testing.expectEqual(@as(u64, 30), source_plans[0].source_handle);
    try std.testing.expectEqual(@as(u32, 0), source_plans[0].requested_rule_count);
}

test "catalog semantic fingerprint is field-ordered and stable" {
    const sources = testSources();
    const rules = testRules();
    try std.testing.expectEqual(
        @as(u64, 8_681_741_352_029_876_231),
        session_module.catalogFingerprint(&sources, &rules),
    );
}

test "catalog plan CPU baseline GPU inventory and exact snapshot are one state machine" {
    var config = testConfig(1);
    const session_scratch = try testScratch(&config);
    defer std.testing.allocator.free(session_scratch);
    const session = try state.Session.create(&config);
    defer session.destroy();
    var sources = testSources();
    var rules = testRules();
    try std.testing.expectEqual(
        ResultCode.ok,
        replaceCatalog(session, 1, 1, 100, &sources, &rules),
    );

    var source_plans = std.mem.zeroes([4]protocol.SourcePlanOutput);
    var metric_plans = std.mem.zeroes([4]protocol.MetricPlanOutput);
    var output = std.mem.zeroes(protocol.PlanOutput);
    const requested = [_]protocol.PlanMetricInput{
        planMetric(100),
        planMetric(200),
    };
    const modes = testModes();
    try std.testing.expectEqual(
        ResultCode.ok,
        runPlan(
            session,
            session_scratch,
            2,
            1,
            200,
            protocol.PlanFlags.include_gpu_inventory,
            &requested,
            &modes,
            &output,
            &source_plans,
            &metric_plans,
        ),
    );
    try std.testing.expectEqual(@as(u32, 2), output.source_plan_count);
    try std.testing.expectEqual(
        protocol.PlanOutputFlags.gpu_inventory_selected,
        output.flags,
    );
    try std.testing.expectEqual(@as(u64, 10), source_plans[0].source_handle);
    try std.testing.expectEqual(@as(u64, 30), source_plans[1].source_handle);

    const requested_rules = [_]protocol.RequestedMetricInput{
        requestedRule(1_001),
        requestedRule(2_001),
    };
    const memory = [_]protocol.ObservationInput{
        unsignedObservation(2_001, 200, 10, 2, 300, 4096),
    };
    var first_header = metricCompletion(3, 1, 10, 1, 1, 300, 2, 1, 1);
    first_header.capability_generation = source_plans[0].capability_generation;
    first_header.plan_token_fingerprint = source_plans[0].plan_token_fingerprint;
    try std.testing.expectEqual(ResultCode.ok, session_module.beginCompletion(session, &first_header, session_scratch));
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.submitRequested(session, &requested_rules),
    );
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.submitObservations(session, &memory),
    );
    var first_counter = cpuCounter(1, 300, 100, 500, 500, 1_000);
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.submitCpuCounter(session, &first_counter),
    );
    var first_finalize = finalizeInput(3, 1, 10, 300);
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.finalizeCompletion(session, &first_finalize),
    );

    var header = std.mem.zeroes(protocol.SnapshotHeader);
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.querySnapshotHeader(session, &header),
    );
    try std.testing.expectEqual(@as(u32, 1), header.current_metric_count);
    try std.testing.expectEqual(@as(u32, 1), header.skipped_metric_count);

    output = std.mem.zeroes(protocol.PlanOutput);
    source_plans = std.mem.zeroes([4]protocol.SourcePlanOutput);
    metric_plans = std.mem.zeroes([4]protocol.MetricPlanOutput);
    try std.testing.expectEqual(
        ResultCode.ok,
        runPlan(
            session,
            session_scratch,
            4,
            2,
            400,
            protocol.PlanFlags.include_gpu_inventory,
            &requested,
            &modes,
            &output,
            &source_plans,
            &metric_plans,
        ),
    );
    var second_header = metricCompletion(5, 2, 10, 1, 2, 500, 2, 1, 1);
    second_header.capability_generation = source_plans[0].capability_generation;
    second_header.plan_token_fingerprint = source_plans[0].plan_token_fingerprint;
    try std.testing.expectEqual(ResultCode.ok, session_module.beginCompletion(session, &second_header, session_scratch));
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.submitRequested(session, &requested_rules),
    );
    const memory_second = [_]protocol.ObservationInput{
        unsignedObservation(2_001, 200, 10, 2, 500, 8192),
    };
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.submitObservations(session, &memory_second),
    );
    var second_counter = cpuCounter(2, 500, 150, 650, 650, 1_200);
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.submitCpuCounter(session, &second_counter),
    );
    var second_finalize = finalizeInput(5, 2, 10, 500);
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.finalizeCompletion(session, &second_finalize),
    );

    var gpu_header = gpuCompletion(6, 2, 30, 1, 1, 600, 2);
    gpu_header.capability_generation = source_plans[1].capability_generation;
    gpu_header.plan_token_fingerprint = source_plans[1].plan_token_fingerprint;
    try std.testing.expectEqual(ResultCode.ok, session_module.beginCompletion(session, &gpu_header, session_scratch));
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.submitRequested(session, &.{}),
    );
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.submitObservations(session, &.{}),
    );
    const gpus = [_]protocol.GpuInventoryInput{
        gpuInput(101, 1, 11, 1_001, 1, 600),
        gpuInput(102, 2, 22, 1_002, 1, 600),
    };
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.submitGpuInventory(session, &gpus),
    );
    var gpu_finalize = finalizeInput(6, 2, 30, 600);
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.finalizeCompletion(session, &gpu_finalize),
    );

    header = std.mem.zeroes(protocol.SnapshotHeader);
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.querySnapshotHeader(session, &header),
    );
    try std.testing.expectEqual(@as(u32, 2), header.current_metric_count);
    try std.testing.expectEqual(@as(u32, 2), header.gpu_adapter_count);

    var source_outputs = std.mem.zeroes([source_count]protocol.SourceOutput);
    var metric_outputs = std.mem.zeroes([metric_count]protocol.MetricOutput);
    var gpu_outputs = std.mem.zeroes([2]protocol.GpuInventoryOutput);
    var rule_outputs = std.mem.zeroes([rule_count]protocol.RuleStateOutput);
    var read = readInput(header);
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.readSnapshot(
            session,
            &read,
            &source_outputs,
            &metric_outputs,
            &gpu_outputs,
            &rule_outputs,
        ),
    );
    try std.testing.expectEqual(@as(u64, 2), source_outputs[0].source_generation);
    try std.testing.expectEqual(@as(u64, 2), source_outputs[0].source_observation_sequence);
    try std.testing.expectEqual(@as(u32, 0), source_outputs[0].reset_count);
    try std.testing.expectEqual(@as(u64, 200), metric_outputs[0].sample_duration_milliseconds);
    try std.testing.expectEqual(@as(u64, 101), gpu_outputs[0].adapter_handle);

    // Callers that need only source facts may omit the other read columns.
    var source_only_outputs = std.mem.zeroes([source_count]protocol.SourceOutput);
    const source_only_read = readInput(header);
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.readSnapshot(
            session,
            &source_only_read,
            &source_only_outputs,
            &.{},
            &.{},
            &.{},
        ),
    );
    try std.testing.expectEqualDeep(source_outputs, source_only_outputs);
    const native_abi = @import("abi.zig");
    source_only_outputs = std.mem.zeroes([source_count]protocol.SourceOutput);
    try std.testing.expectEqual(
        @intFromEnum(ResultCode.ok),
        native_abi.rm_metric_snapshot_read(
            @ptrCast(session),
            &source_only_read,
            source_only_outputs[0..].ptr,
            source_count,
            null,
            0,
            null,
            0,
            null,
            0,
        ),
    );
    try std.testing.expectEqualDeep(source_outputs, source_only_outputs);

    // An omitted column is valid, but every supplied non-empty column still
    // needs enough space for the active output count.
    var short_sources = std.mem.zeroes([source_count - 1]protocol.SourceOutput);
    try std.testing.expectEqual(
        ResultCode.buffer_too_small,
        session_module.readSnapshot(
            session,
            &source_only_read,
            &short_sources,
            &metric_outputs,
            &gpu_outputs,
            &rule_outputs,
        ),
    );
    var short_metrics = std.mem.zeroes([metric_count - 1]protocol.MetricOutput);
    try std.testing.expectEqual(
        ResultCode.buffer_too_small,
        session_module.readSnapshot(
            session,
            &source_only_read,
            &source_outputs,
            &short_metrics,
            &gpu_outputs,
            &rule_outputs,
        ),
    );
    var short_gpu = std.mem.zeroes([1]protocol.GpuInventoryOutput);
    try std.testing.expectEqual(
        ResultCode.buffer_too_small,
        session_module.readSnapshot(
            session,
            &source_only_read,
            &source_outputs,
            &metric_outputs,
            &short_gpu,
            &rule_outputs,
        ),
    );
    var short_rules = std.mem.zeroes([rule_count - 1]protocol.RuleStateOutput);
    try std.testing.expectEqual(
        ResultCode.buffer_too_small,
        session_module.readSnapshot(
            session,
            &source_only_read,
            &source_outputs,
            &metric_outputs,
            &gpu_outputs,
            &short_rules,
        ),
    );

    var stale_read = source_only_read;
    stale_read.catalog_generation += 1;
    try std.testing.expectEqual(
        ResultCode.stale_frame,
        session_module.readSnapshot(
            session,
            &stale_read,
            &source_only_outputs,
            &.{},
            &.{},
            &.{},
        ),
    );
    var counts_after = std.mem.zeroes(protocol.SnapshotHeader);
    try std.testing.expectEqual(ResultCode.ok, session_module.querySnapshotHeader(session, &counts_after));
    try std.testing.expectEqual(header.source_count, counts_after.source_count);
    try std.testing.expectEqual(header.metric_count, counts_after.metric_count);
    try std.testing.expectEqual(header.gpu_adapter_count, counts_after.gpu_adapter_count);
    try std.testing.expectEqual(header.rule_count, counts_after.rule_count);
    try std.testing.expectEqual(header.committed_generation, counts_after.committed_generation);
    try std.testing.expectEqualDeep(header, counts_after);
}

test "catalog replacement is atomic and permits priority independent handle ordering" {
    var config = testConfig(1);
    const session_scratch = try testScratch(&config);
    defer std.testing.allocator.free(session_scratch);
    const session = try state.Session.create(&config);
    defer session.destroy();
    var sources = testSources();
    sources[0].priority = 3;
    sources[1].priority = 1;
    sources[2].priority = 2;
    var rules = testRules();
    rules[0].source_priority = 3;
    rules[1].source_priority = 1;
    rules[2].source_priority = 3;
    std.mem.swap(protocol.MetricDefinitionInput, &rules[0], &rules[1]);
    rules[0].rule_handle = 1_002;
    rules[1].rule_handle = 1_001;
    try std.testing.expectEqual(
        ResultCode.ok,
        replaceCatalog(session, 1, 1, 100, &sources, &rules),
    );
    const original_generation = session.catalog_generation;
    const original_fingerprint = session.catalog_fingerprint;

    var invalid_rules = rules;
    invalid_rules[1].source_handle = 999;
    try std.testing.expectEqual(
        ResultCode.invalid_argument,
        replaceCatalog(session, 2, 2, 200, &sources, &invalid_rules),
    );
    try std.testing.expectEqual(original_generation, session.catalog_generation);
    try std.testing.expectEqual(original_fingerprint, session.catalog_fingerprint);
}

test "hot reconfigure invalidates old plan authority and preserves the epoch lower bound" {
    var config = testConfig(1);
    const session_scratch = try testScratch(&config);
    defer std.testing.allocator.free(session_scratch);
    const session = try state.Session.create(&config);
    defer session.destroy();
    var sources = testSources();
    var rules = testRules();
    try std.testing.expectEqual(
        ResultCode.ok,
        replaceCatalog(session, 1, 1, 100, &sources, &rules),
    );

    var source_plans = std.mem.zeroes([4]protocol.SourcePlanOutput);
    var metric_plans = std.mem.zeroes([4]protocol.MetricPlanOutput);
    var plan_output = std.mem.zeroes(protocol.PlanOutput);
    const requested = [_]protocol.PlanMetricInput{planMetric(100)};
    const modes = testModes();
    try std.testing.expectEqual(
        ResultCode.ok,
        runPlan(
            session,
            session_scratch,
            2,
            1,
            200,
            0,
            &requested,
            &modes,
            &plan_output,
            &source_plans,
            &metric_plans,
        ),
    );
    const old_source_plan = source_plans[0];

    config.generation = 2;
    try std.testing.expectEqual(ResultCode.ok, session_module.reconfigure(session, &config));
    try std.testing.expectEqual(@as(u64, 1), session.last_plan_epoch);

    var old_header = metricCompletion(3, 1, 10, 1, 1, 300, 1, 0, 1);
    old_header.configuration_generation = 2;
    old_header.capability_generation = old_source_plan.capability_generation;
    old_header.plan_token_fingerprint = old_source_plan.plan_token_fingerprint;
    try std.testing.expectEqual(
        ResultCode.stale_frame,
        session_module.beginCompletion(session, &old_header, session_scratch),
    );

    source_plans = std.mem.zeroes([4]protocol.SourcePlanOutput);
    metric_plans = std.mem.zeroes([4]protocol.MetricPlanOutput);
    try std.testing.expectEqual(
        ResultCode.ok,
        runPlan(
            session,
            session_scratch,
            3,
            2,
            300,
            0,
            &requested,
            &modes,
            &plan_output,
            &source_plans,
            &metric_plans,
        ),
    );
    var new_header = metricCompletion(4, 2, 10, 1, 1, 400, 1, 0, 1);
    new_header.configuration_generation = 2;
    new_header.capability_generation = source_plans[0].capability_generation;
    new_header.plan_token_fingerprint = source_plans[0].plan_token_fingerprint;
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.beginCompletion(session, &new_header, session_scratch),
    );
    var abort = finalizeInput(4, 2, 10, 400);
    abort.configuration_generation = 2;
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.abortCompletion(session, &abort),
    );
}

test "completion and observation timestamps are nonzero facts independent of acceptance order" {
    var config = testConfig(1);
    const session_scratch = try testScratch(&config);
    defer std.testing.allocator.free(session_scratch);
    const session = try state.Session.create(&config);
    defer session.destroy();
    var sources = testSources();
    var rules = testRules();
    try std.testing.expectEqual(
        ResultCode.ok,
        replaceCatalog(session, 1, 1, 100, &sources, &rules),
    );

    var source_plans = std.mem.zeroes([4]protocol.SourcePlanOutput);
    var metric_plans = std.mem.zeroes([4]protocol.MetricPlanOutput);
    var plan_output = std.mem.zeroes(protocol.PlanOutput);
    const requested = [_]protocol.PlanMetricInput{planMetric(200)};
    const modes = testModes();
    try std.testing.expectEqual(
        ResultCode.ok,
        runPlan(
            session,
            session_scratch,
            2,
            1,
            200,
            0,
            &requested,
            &modes,
            &plan_output,
            &source_plans,
            &metric_plans,
        ),
    );
    const revision_before_zero = session.state_revision;
    var zero_header = metricCompletion(3, 1, 10, 1, 1, 0, 1, 1, 0);
    zero_header.capability_generation = source_plans[0].capability_generation;
    zero_header.plan_token_fingerprint = source_plans[0].plan_token_fingerprint;
    try std.testing.expectEqual(
        ResultCode.abi_mismatch,
        session_module.beginCompletion(session, &zero_header, session_scratch),
    );
    try std.testing.expectEqual(revision_before_zero, session.state_revision);

    var first_header = metricCompletion(3, 1, 10, 1, 1, 300, 1, 1, 0);
    first_header.capability_generation = source_plans[0].capability_generation;
    first_header.plan_token_fingerprint = source_plans[0].plan_token_fingerprint;
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.beginCompletion(session, &first_header, session_scratch),
    );
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.submitRequested(session, &.{requestedRule(2_001)}),
    );
    const zero = [_]protocol.ObservationInput{
        unsignedObservation(2_001, 200, 10, 2, 0, 4096),
    };
    try std.testing.expectEqual(
        ResultCode.invalid_argument,
        session_module.submitObservations(session, &zero),
    );
    var current = [_]protocol.ObservationInput{
        unsignedObservation(2_001, 200, 10, 2, 301, 4096),
    };
    current[0].source_observation_sequence = 1;
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.submitObservations(session, &current),
    );
    var first_finalize = finalizeInput(3, 1, 10, 250);
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.finalizeCompletion(session, &first_finalize),
    );

    try std.testing.expectEqual(
        ResultCode.ok,
        runPlan(
            session,
            session_scratch,
            4,
            2,
            100,
            0,
            &requested,
            &modes,
            &plan_output,
            &source_plans,
            &metric_plans,
        ),
    );
    var second_header = metricCompletion(5, 2, 10, 1, 2, 200, 1, 1, 0);
    second_header.capability_generation = source_plans[0].capability_generation;
    second_header.plan_token_fingerprint = source_plans[0].plan_token_fingerprint;
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.beginCompletion(session, &second_header, session_scratch),
    );
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.submitRequested(session, &.{requestedRule(2_001)}),
    );
    const regressed = [_]protocol.ObservationInput{
        unsignedObservation(2_001, 200, 10, 2, 299, 8192),
    };
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.submitObservations(session, &regressed),
    );
    var finalize = finalizeInput(5, 2, 10, 150);
    try std.testing.expectEqual(ResultCode.ok, session_module.finalizeCompletion(session, &finalize));
    try std.testing.expectEqual(@as(u64, 299), session.active.rules[2].observed_at_milliseconds);
    try std.testing.expectEqual(@as(u64, 8192), session.active.rules[2].value_bits);
    try std.testing.expectEqual(ResultCode.stale_frame, session_module.beginCompletion(session, &second_header, session_scratch));
}

test "empty failure permits the next observation after wall clock rollback" {
    var config = testConfig(1);
    const session_scratch = try testScratch(&config);
    defer std.testing.allocator.free(session_scratch);
    const session = try state.Session.create(&config);
    defer session.destroy();
    var sources = testSources();
    var rules = testRules();
    try std.testing.expectEqual(
        ResultCode.ok,
        replaceCatalog(session, 1, 1, 100, &sources, &rules),
    );

    var source_plans = std.mem.zeroes([4]protocol.SourcePlanOutput);
    var metric_plans = std.mem.zeroes([4]protocol.MetricPlanOutput);
    var plan_output = std.mem.zeroes(protocol.PlanOutput);
    const requested = [_]protocol.PlanMetricInput{planMetric(200)};
    const modes = testModes();
    try std.testing.expectEqual(
        ResultCode.ok,
        runPlan(
            session,
            session_scratch,
            2,
            1,
            200,
            0,
            &requested,
            &modes,
            &plan_output,
            &source_plans,
            &metric_plans,
        ),
    );
    var current_header = metricCompletion(3, 1, 10, 1, 1, 300, 1, 1, 0);
    current_header.capability_generation = source_plans[0].capability_generation;
    current_header.plan_token_fingerprint = source_plans[0].plan_token_fingerprint;
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.beginCompletion(session, &current_header, session_scratch),
    );
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.submitRequested(session, &.{requestedRule(2_001)}),
    );
    const current = [_]protocol.ObservationInput{
        unsignedObservation(2_001, 200, 10, 2, 300, 4096),
    };
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.submitObservations(session, &current),
    );
    var current_finalize = finalizeInput(3, 1, 10, 300);
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.finalizeCompletion(session, &current_finalize),
    );

    try std.testing.expectEqual(
        ResultCode.ok,
        runPlan(
            session,
            session_scratch,
            4,
            2,
            400,
            0,
            &requested,
            &modes,
            &plan_output,
            &source_plans,
            &metric_plans,
        ),
    );
    var unavailable_header = metricCompletion(5, 2, 10, 1, 2, 500, 1, 0, 0);
    unavailable_header.status = @intFromEnum(protocol.SourceStatus.unavailable);
    unavailable_header.capability_generation = source_plans[0].capability_generation;
    unavailable_header.plan_token_fingerprint = source_plans[0].plan_token_fingerprint;
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.beginCompletion(session, &unavailable_header, session_scratch),
    );
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.submitRequested(session, &.{requestedRule(2_001)}),
    );
    var unavailable_finalize = finalizeInput(5, 2, 10, 500);
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.finalizeCompletion(session, &unavailable_finalize),
    );
    try std.testing.expect(session.active.rules[2].status != .current);
    try std.testing.expectEqual(
        @as(u64, 300),
        session.active.rules[2].observed_at_milliseconds,
    );

    try std.testing.expectEqual(
        ResultCode.ok,
        runPlan(
            session,
            session_scratch,
            6,
            3,
            600,
            0,
            &requested,
            &modes,
            &plan_output,
            &source_plans,
            &metric_plans,
        ),
    );
    var stale_header = metricCompletion(7, 3, 10, 1, 3, 700, 1, 1, 0);
    stale_header.capability_generation = source_plans[0].capability_generation;
    stale_header.plan_token_fingerprint = source_plans[0].plan_token_fingerprint;
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.beginCompletion(session, &stale_header, session_scratch),
    );
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.submitRequested(session, &.{requestedRule(2_001)}),
    );
    var stale = unsignedObservation(2_001, 200, 10, 2, 299, 8192);
    stale.source_observation_sequence = 3;
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.submitObservations(session, &.{stale}),
    );
    var finalize = finalizeInput(7, 3, 10, 700);
    try std.testing.expectEqual(ResultCode.ok, session_module.finalizeCompletion(session, &finalize));
    try std.testing.expectEqual(@as(u64, 299), session.active.rules[2].observed_at_milliseconds);
}

test "wall clock rollback preserves metric GPU and restored timestamps while capability identities change" {
    var config = testConfig(1);
    const bytes = try testScratch(&config);
    defer std.testing.allocator.free(bytes);
    const session = try state.Session.create(&config);
    defer session.destroy();
    var sources = testSources();
    var rules = testRules();
    try std.testing.expectEqual(ResultCode.ok, replaceCatalog(session, 1, 1, 100, &sources, &rules));
    var source_plans = std.mem.zeroes([4]protocol.SourcePlanOutput);
    var metric_plans = std.mem.zeroes([4]protocol.MetricPlanOutput);
    var output = std.mem.zeroes(protocol.PlanOutput);
    const requested = [_]protocol.PlanMetricInput{planMetric(200)};
    var modes = testModes();
    for ([_]u64{ 10_000, 200 }, 0..) |captured, index| {
        const sequence: u64 = @intCast(index + 1);
        const operation = sequence * 3 - 1;
        for (&modes) |*mode| mode.capability_generation = if (index == 0) 10 else 1;
        try std.testing.expectEqual(ResultCode.ok, runPlan(session, bytes, operation, sequence, captured - 50, protocol.PlanFlags.include_gpu_inventory, &requested, &modes, &output, &source_plans, &metric_plans));
        try completeGpuCycle(session, bytes, operation + 1, sequence, sequence, captured, source_plans[1], &.{gpuInput(101, 1, 11, 1_001, sequence, captured + 2_000)}, .complete, 1);
        var header = metricCompletion(operation + 2, sequence, 10, 1, sequence, captured, 1, 1, 0);
        header.command_at_milliseconds = captured - 50;
        header.capability_generation = source_plans[0].capability_generation;
        header.plan_token_fingerprint = source_plans[0].plan_token_fingerprint;
        try std.testing.expectEqual(ResultCode.ok, session_module.beginCompletion(session, &header, bytes));
        try std.testing.expectEqual(ResultCode.ok, session_module.submitRequested(session, &.{requestedRule(2_001)}));
        var value = unsignedObservation(2_001, 200, 10, 2, captured + 2_000, sequence * 4096);
        value.source_observation_sequence = sequence;
        try std.testing.expectEqual(ResultCode.ok, session_module.submitObservations(session, &.{value}));
        var finalize = finalizeInput(operation + 2, sequence, 10, captured - 100);
        try std.testing.expectEqual(ResultCode.ok, session_module.finalizeCompletion(session, &finalize));
    }
    try std.testing.expectEqual(@as(u64, 2_200), session.active.rules[2].observed_at_milliseconds);
    try std.testing.expectEqual(@as(u64, 2_200), session.gpu_active[0].output.observed_at_milliseconds);
    try std.testing.expectEqual(@as(u64, 1), session.active.rules[2].capability_generation);
    var snapshot = std.mem.zeroes(protocol.SnapshotHeader);
    try std.testing.expectEqual(ResultCode.ok, session_module.querySnapshotHeader(session, &snapshot));
    var read = readInput(snapshot);
    var persisted = std.mem.zeroes(protocol.PersistenceHeader);
    var source_outputs = std.mem.zeroes([source_count]protocol.SourcePersistenceOutput);
    var rule_outputs = std.mem.zeroes([rule_count]protocol.RuleStateOutput);
    var gpu_outputs = std.mem.zeroes([1]protocol.GpuInventoryOutput);
    try std.testing.expectEqual(ResultCode.ok, persistence.exportState(session, &read, &persisted, &source_outputs, &rule_outputs, &gpu_outputs));
    const restored = try state.Session.create(&config);
    defer restored.destroy();
    try std.testing.expectEqual(ResultCode.ok, replaceCatalog(restored, 1, 1, 20_000, &sources, &rules));
    var restore = persistenceInput(8, 50);
    try std.testing.expectEqual(ResultCode.ok, persistence.importState(restored, &restore, &persisted, &source_outputs, &rule_outputs, &gpu_outputs));
    try std.testing.expectEqual(@as(u64, 2_200), restored.active.rules[2].observed_at_milliseconds);
    try std.testing.expectEqual(@as(u64, 8192), restored.active.rules[2].value_bits);
    try std.testing.expectEqual(@as(u64, 2_200), restored.gpu_active[0].output.observed_at_milliseconds);
}

test "failed requested rule is empty while unrequested slots and other sources stay current" {
    var config = testConfig(1);
    const bytes = try testScratch(&config);
    defer std.testing.allocator.free(bytes);
    const session = try state.Session.create(&config);
    defer session.destroy();
    var sources = testSources();
    var rules = testRules();
    try std.testing.expectEqual(ResultCode.ok, replaceCatalog(session, 1, 1, 100, &sources, &rules));
    var source_plans = std.mem.zeroes([4]protocol.SourcePlanOutput);
    var metric_plans = std.mem.zeroes([4]protocol.MetricPlanOutput);
    var output = std.mem.zeroes(protocol.PlanOutput);
    var modes = testModes();
    var operation: u64 = 2;
    var plan_epoch: u64 = 1;
    for ([_]u64{ 10, 20 }) |source_handle| {
        for ([_]u64{ 1, 2 }) |sequence| {
            const captured = operation * 100;
            modes[0].availability = @intFromEnum(if (source_handle == 10) protocol.SourceAvailability.available else protocol.SourceAvailability.unavailable);
            const requested = [_]protocol.PlanMetricInput{ planMetric(100), planMetric(200) };
            const demand = requested[0..@as(usize, if (source_handle == 10) 2 else 1)];
            try std.testing.expectEqual(ResultCode.ok, runPlan(session, bytes, operation, plan_epoch, captured, 0, demand, &modes, &output, &source_plans, &metric_plans));
            var header = metricCompletion(operation + 1, plan_epoch, source_handle, 1, sequence, captured, @intCast(demand.len), if (source_handle == 10) 1 else 0, 1);
            header.capability_generation = source_plans[0].capability_generation;
            header.plan_token_fingerprint = source_plans[0].plan_token_fingerprint;
            try std.testing.expectEqual(ResultCode.ok, session_module.beginCompletion(session, &header, bytes));
            const requested_rules = [_]protocol.RequestedMetricInput{ requestedRule(if (source_handle == 10) 1_001 else 1_002), requestedRule(2_001) };
            try std.testing.expectEqual(ResultCode.ok, session_module.submitRequested(session, requested_rules[0..demand.len]));
            var value = unsignedObservation(2_001, 200, 10, 2, captured, sequence * 4096);
            value.source_observation_sequence = sequence;
            try std.testing.expectEqual(ResultCode.ok, session_module.submitObservations(session, if (source_handle == 10) &.{value} else &.{}));
            var counter = cpuCounter(sequence, captured, sequence * 100, sequence * 500, sequence * 500, sequence * 1_000);
            counter.source_handle = source_handle;
            counter.rule_handle = if (source_handle == 10) 1_001 else 1_002;
            counter.capability_mask = if (source_handle == 10) 0x01 else 0x02;
            try std.testing.expectEqual(ResultCode.ok, session_module.submitCpuCounter(session, &counter));
            var finalize = finalizeInput(operation + 1, plan_epoch, source_handle, captured);
            try std.testing.expectEqual(ResultCode.ok, session_module.finalizeCompletion(session, &finalize));
            operation += 2;
            plan_epoch += 1;
        }
    }
    const other_rule = session.active.rules[1];
    const other_source = session.active.sources[1];
    try std.testing.expectEqual(protocol.MetricStatus.current, other_rule.status);
    try std.testing.expect(session.active.sources[0].cpu_baseline_valid);
    modes[0].availability = @intFromEnum(protocol.SourceAvailability.available);
    const requested = [_]protocol.PlanMetricInput{planMetric(200)};
    try std.testing.expectEqual(ResultCode.ok, runPlan(session, bytes, operation, plan_epoch, 1_000, 0, &requested, &modes, &output, &source_plans, &metric_plans));
    var failed = metricCompletion(operation + 1, plan_epoch, 10, 1, 3, 1_100, 1, 0, 0);
    failed.status = @intFromEnum(protocol.SourceStatus.unavailable);
    failed.capability_generation = source_plans[0].capability_generation;
    failed.plan_token_fingerprint = source_plans[0].plan_token_fingerprint;
    try std.testing.expectEqual(ResultCode.ok, session_module.beginCompletion(session, &failed, bytes));
    try std.testing.expectEqual(ResultCode.ok, session_module.submitRequested(session, &.{requestedRule(2_001)}));
    var finalize = finalizeInput(operation + 1, plan_epoch, 10, 1_100);
    try std.testing.expectEqual(ResultCode.ok, session_module.finalizeCompletion(session, &finalize));
    const failed_rule = session.active.rules[2];
    try std.testing.expectEqual(protocol.MetricStatus.unavailable, failed_rule.status);
    try std.testing.expectEqual(@as(u64, 0), failed_rule.value_bits);
    // CPU was not requested: neither its slot nor its source-owned counter pair changes.
    try std.testing.expectEqual(protocol.MetricStatus.current, session.active.rules[0].status);
    try std.testing.expect(session.active.sources[0].cpu_baseline_valid);
    try std.testing.expectEqualDeep(other_rule, session.active.rules[1]);
    try std.testing.expectEqualDeep(other_source, session.active.sources[1]);
    const cpu_metric = session.active.metrics[session.active.findMetric(100).?].output;
    try std.testing.expectEqual(@as(u64, 10), cpu_metric.source_handle);
    try std.testing.expectEqual(@intFromEnum(protocol.MetricStatus.current), cpu_metric.status);
    const memory_metric = session.active.metrics[session.active.findMetric(200).?].output;
    try std.testing.expectEqual(@intFromEnum(protocol.MetricStatus.unavailable), memory_metric.status);

    // An unavailable completion does not discard a valid cumulative GetSystemTimes
    // baseline. The next CPU reading uses the full monotonic interval across the gap.
    const cpu_request = [_]protocol.PlanMetricInput{planMetric(100)};
    try std.testing.expectEqual(ResultCode.ok, runPlan(session, bytes, operation + 2, plan_epoch + 1, 1_200, 0, &cpu_request, &modes, &output, &source_plans, &metric_plans));
    var recovered = metricCompletion(operation + 3, plan_epoch + 1, 10, 1, 4, 1_300, 1, 0, 1);
    recovered.capability_generation = source_plans[0].capability_generation;
    recovered.plan_token_fingerprint = source_plans[0].plan_token_fingerprint;
    try std.testing.expectEqual(ResultCode.ok, session_module.beginCompletion(session, &recovered, bytes));
    try std.testing.expectEqual(ResultCode.ok, session_module.submitRequested(session, &.{requestedRule(1_001)}));
    var counter = cpuCounter(4, 1_300, 400, 2_000, 2_000, 4_000);
    try std.testing.expectEqual(ResultCode.ok, session_module.submitCpuCounter(session, &counter));
    var recovered_finalize = finalizeInput(operation + 3, plan_epoch + 1, 10, 1_300);
    try std.testing.expectEqual(ResultCode.ok, session_module.finalizeCompletion(session, &recovered_finalize));
    try std.testing.expectEqual(protocol.MetricStatus.current, session.active.rules[0].status);
    try std.testing.expectApproxEqAbs(@as(f64, 90), @as(f64, @bitCast(session.active.rules[0].value_bits)), 1e-10);
    try std.testing.expectEqual(@as(u64, 2_000), session.active.rules[0].sample_duration_milliseconds);
}

test "failed requested rule and failed GPU inventory both publish empty values" {
    inline for (.{ protocol.SourceStatus.unavailable, protocol.SourceStatus.unsupported, protocol.SourceStatus.skipped }) |failure| {
        var config = testConfig(1);
        const bytes = try testScratch(&config);
        defer std.testing.allocator.free(bytes);
        const session = try state.Session.create(&config);
        defer session.destroy();
        var sources = testSources();
        var rules = testRules();
        try std.testing.expectEqual(ResultCode.ok, replaceCatalog(session, 1, 1, 100, &sources, &rules));
        var source_plans = std.mem.zeroes([4]protocol.SourcePlanOutput);
        var metric_plans = std.mem.zeroes([4]protocol.MetricPlanOutput);
        var output = std.mem.zeroes(protocol.PlanOutput);
        const modes = testModes();
        const requested = [_]protocol.PlanMetricInput{planMetric(200)};
        for ([_]u64{ 1, 2 }) |sequence| {
            const operation = sequence * 3 - 1;
            const captured = sequence * 300;
            try std.testing.expectEqual(ResultCode.ok, runPlan(session, bytes, operation, sequence, captured, protocol.PlanFlags.include_gpu_inventory, &requested, &modes, &output, &source_plans, &metric_plans));
            try completeGpuCycle(session, bytes, operation + 1, sequence, sequence, captured, source_plans[1], if (sequence == 1) &.{gpuInput(101, 1, 11, 1_001, sequence, captured)} else &.{}, if (sequence == 1) .complete else failure, if (sequence == 1) 1 else 0);
            var header = metricCompletion(operation + 2, sequence, 10, 1, sequence, captured, 1, 1, 0);
            header.capability_generation = source_plans[0].capability_generation;
            header.plan_token_fingerprint = source_plans[0].plan_token_fingerprint;
            try std.testing.expectEqual(ResultCode.ok, session_module.beginCompletion(session, &header, bytes));
            try std.testing.expectEqual(ResultCode.ok, session_module.submitRequested(session, &.{requestedRule(2_001)}));
            var value = unsignedObservation(2_001, 200, 10, 2, captured, if (sequence == 1) 4096 else 0);
            value.source_observation_sequence = sequence;
            if (sequence == 2) {
                value.status = @intFromEnum(protocol.ObservationStatus.unavailable);
                value.valid_mask = protocol.ObservationValid.required;
            }
            try std.testing.expectEqual(ResultCode.ok, session_module.submitObservations(session, &.{value}));
            var finalize = finalizeInput(operation + 2, sequence, 10, captured);
            try std.testing.expectEqual(ResultCode.ok, session_module.finalizeCompletion(session, &finalize));
        }
        const rule = session.active.rules[2];
        try std.testing.expectEqual(protocol.MetricStatus.unavailable, rule.status);
        try std.testing.expectEqual(@as(u64, 0), rule.value_bits);
        try std.testing.expectEqual(@as(u32, 0), @import("inventory.zig").activeCount(session));
        try std.testing.expectEqual(failure, session.active.sources[2].status);
    }
}

test "late catalog rejection restores staging rows indexes and GPU scratch" {
    var config = testConfig(1);
    const session_scratch = try testScratch(&config);
    defer std.testing.allocator.free(session_scratch);
    const session = try state.Session.create(&config);
    defer session.destroy();
    var sources = testSources();
    var rules = testRules();
    try std.testing.expectEqual(
        ResultCode.ok,
        replaceCatalog(session, 1, 1, 100, &sources, &rules),
    );

    var invalid_sources = sources;
    invalid_sources[0].source_handle = 110;
    invalid_sources[1].source_handle = 120;
    invalid_sources[2].source_handle = 130;
    var invalid_rules = rules;
    invalid_rules[0].source_handle = 110;
    invalid_rules[1].source_handle = 120;
    invalid_rules[2].source_handle = 110;
    invalid_rules[2].source_priority = 2;
    try std.testing.expectEqual(
        ResultCode.invalid_argument,
        replaceCatalog(session, 2, 2, 200, &invalid_sources, &invalid_rules),
    );
    try std.testing.expect(session.staging.findSource(10) != null);
    try std.testing.expect(session.staging.findSource(110) == null);
    try std.testing.expect(session.staging.findRule(1_001) != null);
    try std.testing.expect(session.staging.findRule(1_001 + 100) == null);

    try completeCpuCycle(session, session_scratch, 3, 4, 1, 300, 400, 1, 100, 500, 500, 1_000);
    try std.testing.expectEqual(@as(u64, 1), session.active.sources[0].source_generation);
}

test "persistence restores one canonical image and rejects corruption" {
    var config = testConfig(1);
    const source_session_scratch = try testScratch(&config);
    defer std.testing.allocator.free(source_session_scratch);
    const source_session = try state.Session.create(&config);
    defer source_session.destroy();
    var sources = testSources();
    var rules = testRules();
    try std.testing.expectEqual(
        ResultCode.ok,
        replaceCatalog(source_session, 1, 1, 10_000, &sources, &rules),
    );
    // Keep a persisted historical capture later than the importer's rolled-back clock.
    source_session.captured_at_milliseconds = 10_000;

    var source_outputs = std.mem.zeroes([source_count]protocol.SourcePersistenceOutput);
    var rule_outputs = std.mem.zeroes([rule_count]protocol.RuleStateOutput);
    var gpu_outputs = std.mem.zeroes([0]protocol.GpuInventoryOutput);
    var persisted = std.mem.zeroes(protocol.PersistenceHeader);
    var snapshot = std.mem.zeroes(protocol.SnapshotHeader);
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.querySnapshotHeader(source_session, &snapshot),
    );
    var read = readInput(snapshot);
    try std.testing.expectEqual(
        ResultCode.ok,
        persistence.exportState(
            source_session,
            &read,
            &persisted,
            &source_outputs,
            &rule_outputs,
            &gpu_outputs,
        ),
    );

    const restored_session_scratch = try testScratch(&config);
    defer std.testing.allocator.free(restored_session_scratch);
    const restored_session = try state.Session.create(&config);
    defer restored_session.destroy();
    try std.testing.expectEqual(
        ResultCode.ok,
        replaceCatalog(restored_session, 1, 1, 100, &sources, &rules),
    );
    var import_input = persistenceInput(2, 1_000);
    var corrupt = persisted;
    corrupt.checksum ^= 1;
    const before_revision = restored_session.state_revision;
    try std.testing.expectEqual(
        ResultCode.invalid_argument,
        persistence.importState(
            restored_session,
            &import_input,
            &corrupt,
            &source_outputs,
            &rule_outputs,
            &gpu_outputs,
        ),
    );
    try std.testing.expectEqual(before_revision, restored_session.state_revision);
    try std.testing.expectEqual(
        ResultCode.ok,
        persistence.importState(
            restored_session,
            &import_input,
            &persisted,
            &source_outputs,
            &rule_outputs,
            &gpu_outputs,
        ),
    );
    try std.testing.expectEqual(
        source_session.semantic_fingerprint,
        restored_session.semantic_fingerprint,
    );
    try std.testing.expectEqual(@as(u64, 1_000), restored_session.last_command_at_milliseconds);
    try std.testing.expectEqual(@as(u64, 10_000), restored_session.captured_at_milliseconds);
}

test "diagnostic-only CPU progression preserves committed generation and reset is monotonic" {
    var config = testConfig(1);
    const session_scratch = try testScratch(&config);
    defer std.testing.allocator.free(session_scratch);
    const session = try state.Session.create(&config);
    defer session.destroy();
    var sources = testSources();
    var rules = testRules();
    try std.testing.expectEqual(
        ResultCode.ok,
        replaceCatalog(session, 1, 1, 100, &sources, &rules),
    );

    try completeCpuCycle(session, session_scratch, 2, 3, 1, 200, 300, 1, 100, 500, 500, 1_000);
    try completeCpuCycle(session, session_scratch, 4, 5, 2, 400, 500, 2, 150, 650, 650, 1_200);
    const semantic_generation = session.committed_generation;
    const revision_before = session.state_revision;
    try completeCpuCycle(session, session_scratch, 6, 7, 3, 600, 700, 3, 200, 800, 800, 1_400);
    try std.testing.expectEqual(semantic_generation, session.committed_generation);
    try std.testing.expect(session.state_revision > revision_before);

    var reset_input = controlInput(8, 800);
    try std.testing.expectEqual(ResultCode.ok, session_module.reset(session, &reset_input));
    try std.testing.expectEqual(semantic_generation + 1, session.committed_generation);
    const generation_after_reset = session.committed_generation;
    reset_input.operation_epoch = 9;
    reset_input.command_at_milliseconds = 900;
    try std.testing.expectEqual(ResultCode.ok, session_module.reset(session, &reset_input));
    try std.testing.expectEqual(generation_after_reset, session.committed_generation);
}

test "GPU partial completion is empty and the next complete inventory replaces it" {
    var config = testConfig(1);
    const session_scratch = try testScratch(&config);
    defer std.testing.allocator.free(session_scratch);
    const session = try state.Session.create(&config);
    defer session.destroy();
    var sources = testSources();
    var rules = testRules();
    try std.testing.expectEqual(
        ResultCode.ok,
        replaceCatalog(session, 1, 1, 100, &sources, &rules),
    );

    var source_plans = std.mem.zeroes([4]protocol.SourcePlanOutput);
    var metric_plans = std.mem.zeroes([4]protocol.MetricPlanOutput);
    var plan_output = std.mem.zeroes(protocol.PlanOutput);
    const requested = [_]protocol.PlanMetricInput{planMetric(200)};
    const modes = testModes();
    try std.testing.expectEqual(
        ResultCode.ok,
        runPlan(
            session,
            session_scratch,
            2,
            1,
            200,
            protocol.PlanFlags.include_gpu_inventory,
            &requested,
            &modes,
            &plan_output,
            &source_plans,
            &metric_plans,
        ),
    );
    try completeGpuCycle(
        session,
        session_scratch,
        3,
        1,
        1,
        300,
        source_plans[1],
        &.{
            gpuInput(101, 1, 11, 1_001, 1, 250),
            gpuInput(102, 2, 22, 1_002, 1, 250),
        },
        .complete,
        2,
    );

    source_plans = std.mem.zeroes([4]protocol.SourcePlanOutput);
    metric_plans = std.mem.zeroes([4]protocol.MetricPlanOutput);
    try std.testing.expectEqual(
        ResultCode.ok,
        runPlan(
            session,
            session_scratch,
            4,
            2,
            400,
            protocol.PlanFlags.include_gpu_inventory,
            &requested,
            &modes,
            &plan_output,
            &source_plans,
            &metric_plans,
        ),
    );
    try completeGpuCycle(
        session,
        session_scratch,
        5,
        2,
        2,
        500,
        source_plans[1],
        &.{gpuInput(101, 1, 11, 1_001, 2, 500)},
        .partial,
        2,
    );
    try std.testing.expectEqual(@as(u32, 0), @import("inventory.zig").activeCount(session));
    try std.testing.expectEqual(protocol.SourceStatus.partial, session.active.sources[2].status);

    source_plans = std.mem.zeroes([4]protocol.SourcePlanOutput);
    metric_plans = std.mem.zeroes([4]protocol.MetricPlanOutput);
    try std.testing.expectEqual(
        ResultCode.ok,
        runPlan(
            session,
            session_scratch,
            6,
            3,
            600,
            protocol.PlanFlags.include_gpu_inventory,
            &requested,
            &modes,
            &plan_output,
            &source_plans,
            &metric_plans,
        ),
    );
    var final_header = gpuCompletion(7, 3, 30, 1, 3, 700, 2);
    final_header.capability_generation = source_plans[1].capability_generation;
    final_header.plan_token_fingerprint = source_plans[1].plan_token_fingerprint;
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.beginCompletion(session, &final_header, session_scratch),
    );
    try std.testing.expectEqual(ResultCode.ok, session_module.submitRequested(session, &.{}));
    try std.testing.expectEqual(ResultCode.ok, session_module.submitObservations(session, &.{}));
    const exact = [_]protocol.GpuInventoryInput{
        gpuInput(101, 1, 11, 1_001, 3, 650),
        gpuInput(102, 2, 22, 1_002, 3, 650),
    };
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.submitGpuInventory(session, &exact),
    );
    try std.testing.expectEqual(
        @as(u64, 650),
        session.gpu_staging[0].output.observed_at_milliseconds,
    );
    const accepted_first = session.gpu_staging[0].output.semantic_fingerprint;
    const accepted_second = session.gpu_staging[1].output.semantic_fingerprint;
    var duplicate_identity = exact;
    duplicate_identity[1].stable_key_handle = duplicate_identity[0].stable_key_handle;
    try std.testing.expectEqual(
        ResultCode.invalid_argument,
        session_module.submitGpuInventory(session, &duplicate_identity),
    );
    try std.testing.expectEqual(@as(u32, 2), session.gpu_staging_count);
    try std.testing.expectEqual(
        accepted_first,
        session.gpu_staging[0].output.semantic_fingerprint,
    );
    try std.testing.expectEqual(
        accepted_second,
        session.gpu_staging[1].output.semantic_fingerprint,
    );
    var unsupported_capability = exact;
    unsupported_capability[1].capability_mask = 0x08;
    try std.testing.expectEqual(
        ResultCode.invalid_argument,
        session_module.submitGpuInventory(session, &unsupported_capability),
    );
    try std.testing.expectEqual(@as(u32, 2), session.gpu_staging_count);
    var final_input = finalizeInput(7, 3, 30, 700);
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.finalizeCompletion(session, &final_input),
    );
}

test "source incarnation change invalidates CPU baseline and old current facts" {
    var config = testConfig(1);
    const session_scratch = try testScratch(&config);
    defer std.testing.allocator.free(session_scratch);
    const session = try state.Session.create(&config);
    defer session.destroy();
    var sources = testSources();
    var rules = testRules();
    try std.testing.expectEqual(
        ResultCode.ok,
        replaceCatalog(session, 1, 1, 100, &sources, &rules),
    );
    try completeCpuCycle(session, session_scratch, 2, 3, 1, 200, 300, 1, 100, 500, 500, 1_000);
    try completeCpuCycle(session, session_scratch, 4, 5, 2, 400, 500, 2, 150, 650, 650, 1_200);
    const cpu_rule_index = session.active.findRule(1_001).?;
    try std.testing.expectEqual(
        protocol.MetricStatus.current,
        session.active.rules[cpu_rule_index].status,
    );

    var source_plans = std.mem.zeroes([4]protocol.SourcePlanOutput);
    var metric_plans = std.mem.zeroes([4]protocol.MetricPlanOutput);
    var plan_output = std.mem.zeroes(protocol.PlanOutput);
    const memory_request = [_]protocol.PlanMetricInput{planMetric(200)};
    const modes = testModes();
    try std.testing.expectEqual(
        ResultCode.ok,
        runPlan(
            session,
            session_scratch,
            6,
            3,
            600,
            0,
            &memory_request,
            &modes,
            &plan_output,
            &source_plans,
            &metric_plans,
        ),
    );
    var restart_header = metricCompletion(7, 3, 10, 2, 1, 700, 1, 1, 0);
    restart_header.capability_generation = source_plans[0].capability_generation;
    restart_header.plan_token_fingerprint = source_plans[0].plan_token_fingerprint;
    var memory = unsignedObservation(2_001, 200, 10, 2, 700, 8_192);
    memory.source_observation_sequence = 1;
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.beginCompletion(session, &restart_header, session_scratch),
    );
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.submitRequested(session, &.{requestedRule(2_001)}),
    );
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.submitObservations(session, &.{memory}),
    );
    var restart_finalize = finalizeInput(7, 3, 10, 700);
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.finalizeCompletion(session, &restart_finalize),
    );
    const source_index = session.active.findSource(10).?;
    try std.testing.expect(!session.active.sources[source_index].cpu_baseline_valid);
    try std.testing.expectEqual(
        protocol.MetricStatus.unavailable,
        session.active.rules[cpu_rule_index].status,
    );

    source_plans = std.mem.zeroes([4]protocol.SourcePlanOutput);
    metric_plans = std.mem.zeroes([4]protocol.MetricPlanOutput);
    const cpu_request = [_]protocol.PlanMetricInput{planMetric(100)};
    try std.testing.expectEqual(
        ResultCode.ok,
        runPlan(
            session,
            session_scratch,
            8,
            4,
            800,
            0,
            &cpu_request,
            &modes,
            &plan_output,
            &source_plans,
            &metric_plans,
        ),
    );
    var warm_header = metricCompletion(9, 4, 10, 2, 2, 900, 1, 0, 1);
    warm_header.capability_generation = source_plans[0].capability_generation;
    warm_header.plan_token_fingerprint = source_plans[0].plan_token_fingerprint;
    var warm_counter = cpuCounter(2, 900, 200, 800, 800, 1_400);
    warm_counter.source_incarnation = 2;
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.beginCompletion(session, &warm_header, session_scratch),
    );
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.submitRequested(session, &.{requestedRule(1_001)}),
    );
    try std.testing.expectEqual(ResultCode.ok, session_module.submitObservations(session, &.{}));
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.submitCpuCounter(session, &warm_counter),
    );
    var warm_finalize = finalizeInput(9, 4, 10, 900);
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.finalizeCompletion(session, &warm_finalize),
    );
    try std.testing.expect(session.active.sources[source_index].cpu_baseline_valid);
    try std.testing.expectEqual(
        protocol.MetricStatus.skipped,
        session.active.rules[cpu_rule_index].status,
    );
}

test "unsupported sources affect only their metrics and any changed capability can revive them" {
    var config = testConfig(1);
    const session_scratch = try testScratch(&config);
    defer std.testing.allocator.free(session_scratch);
    const session = try state.Session.create(&config);
    defer session.destroy();
    var sources = testSources();
    var rules = testRules();
    try std.testing.expectEqual(
        ResultCode.ok,
        replaceCatalog(session, 1, 1, 100, &sources, &rules),
    );

    var source_plans = std.mem.zeroes([4]protocol.SourcePlanOutput);
    var metric_plans = std.mem.zeroes([4]protocol.MetricPlanOutput);
    var plan_output = std.mem.zeroes(protocol.PlanOutput);
    const requested = [_]protocol.PlanMetricInput{planMetric(100)};
    var modes = testModes();
    try std.testing.expectEqual(
        ResultCode.ok,
        runPlan(
            session,
            session_scratch,
            2,
            1,
            200,
            0,
            &requested,
            &modes,
            &plan_output,
            &source_plans,
            &metric_plans,
        ),
    );
    var unsupported = metricCompletion(3, 1, 10, 1, 1, 300, 1, 0, 0);
    unsupported.capability_generation = source_plans[0].capability_generation;
    unsupported.plan_token_fingerprint = source_plans[0].plan_token_fingerprint;
    unsupported.status = @intFromEnum(protocol.SourceStatus.unsupported);
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.beginCompletion(session, &unsupported, session_scratch),
    );
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.submitRequested(session, &.{requestedRule(1_001)}),
    );
    try std.testing.expectEqual(ResultCode.ok, session_module.submitObservations(session, &.{}));
    var unsupported_finalize = finalizeInput(3, 1, 10, 300);
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.finalizeCompletion(session, &unsupported_finalize),
    );

    try std.testing.expectEqual(
        ResultCode.ok,
        runPlan(
            session,
            session_scratch,
            4,
            2,
            400,
            0,
            &requested,
            &modes,
            &plan_output,
            &source_plans,
            &metric_plans,
        ),
    );
    try std.testing.expectEqual(@as(u64, 1_002), metric_plans[0].rule_handle);
    modes[0].capability_generation = 2;
    try std.testing.expectEqual(
        ResultCode.ok,
        runPlan(
            session,
            session_scratch,
            5,
            3,
            400,
            0,
            &requested,
            &modes,
            &plan_output,
            &source_plans,
            &metric_plans,
        ),
    );
    var unsupported_generation_two = metricCompletion(6, 3, 10, 1, 2, 500, 1, 0, 0);
    unsupported_generation_two.capability_generation =
        source_plans[0].capability_generation;
    unsupported_generation_two.plan_token_fingerprint =
        source_plans[0].plan_token_fingerprint;
    unsupported_generation_two.status = @intFromEnum(protocol.SourceStatus.unsupported);
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.beginCompletion(session, &unsupported_generation_two, session_scratch),
    );
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.submitRequested(session, &.{requestedRule(1_001)}),
    );
    try std.testing.expectEqual(ResultCode.ok, session_module.submitObservations(session, &.{}));
    var unsupported_generation_two_finalize = finalizeInput(6, 3, 10, 500);
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.finalizeCompletion(session, &unsupported_generation_two_finalize),
    );

    modes[0].capability_generation = 1;
    try std.testing.expectEqual(
        ResultCode.ok,
        runPlan(
            session,
            session_scratch,
            7,
            4,
            600,
            0,
            &requested,
            &modes,
            &plan_output,
            &source_plans,
            &metric_plans,
        ),
    );
    try std.testing.expectEqual(@as(u64, 1_001), metric_plans[0].rule_handle);
}

test "field unsupported remains closed only for the same capability generation" {
    var config = testConfig(1);
    const session_scratch = try testScratch(&config);
    defer std.testing.allocator.free(session_scratch);
    const session = try state.Session.create(&config);
    defer session.destroy();
    var sources = testSources();
    var rules = testRules();
    try std.testing.expectEqual(
        ResultCode.ok,
        replaceCatalog(session, 1, 1, 100, &sources, &rules),
    );

    var source_plans = std.mem.zeroes([4]protocol.SourcePlanOutput);
    var metric_plans = std.mem.zeroes([4]protocol.MetricPlanOutput);
    var plan_output = std.mem.zeroes(protocol.PlanOutput);
    const requested = [_]protocol.PlanMetricInput{planMetric(200)};
    var modes = testModes();
    try std.testing.expectEqual(
        ResultCode.ok,
        runPlan(
            session,
            session_scratch,
            2,
            1,
            200,
            0,
            &requested,
            &modes,
            &plan_output,
            &source_plans,
            &metric_plans,
        ),
    );

    var header = metricCompletion(3, 1, 10, 1, 1, 300, 1, 1, 0);
    header.capability_generation = source_plans[0].capability_generation;
    header.plan_token_fingerprint = source_plans[0].plan_token_fingerprint;
    try std.testing.expectEqual(ResultCode.ok, session_module.beginCompletion(session, &header, session_scratch));
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.submitRequested(session, &.{requestedRule(2_001)}),
    );
    var unsupported = unsignedObservation(2_001, 200, 10, 2, 300, 0);
    unsupported.valid_mask = protocol.ObservationValid.required;
    unsupported.status = @intFromEnum(protocol.ObservationStatus.unsupported);
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.submitObservations(session, &.{unsupported}),
    );
    var finalize = finalizeInput(3, 1, 10, 300);
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.finalizeCompletion(session, &finalize),
    );

    try std.testing.expectEqual(
        ResultCode.ok,
        runPlan(
            session,
            session_scratch,
            4,
            2,
            400,
            0,
            &requested,
            &modes,
            &plan_output,
            &source_plans,
            &metric_plans,
        ),
    );
    try std.testing.expectEqual(@as(u64, 0), metric_plans[0].rule_handle);
    try std.testing.expectEqual(@as(u32, 1), plan_output.unavailable_metric_count);

    modes[0].capability_generation = 2;
    try std.testing.expectEqual(
        ResultCode.ok,
        runPlan(
            session,
            session_scratch,
            5,
            3,
            500,
            0,
            &requested,
            &modes,
            &plan_output,
            &source_plans,
            &metric_plans,
        ),
    );
    try std.testing.expectEqual(@as(u64, 2_001), metric_plans[0].rule_handle);
    modes[0].capability_generation = 1;
    try std.testing.expectEqual(
        ResultCode.ok,
        runPlan(
            session,
            session_scratch,
            6,
            4,
            600,
            0,
            &requested,
            &modes,
            &plan_output,
            &source_plans,
            &metric_plans,
        ),
    );
    try std.testing.expectEqual(@as(u64, 0), metric_plans[0].rule_handle);
    modes[0].capability_generation = 3;
    try std.testing.expectEqual(ResultCode.ok, runPlan(session, session_scratch, 7, 5, 700, 0, &requested, &modes, &plan_output, &source_plans, &metric_plans));
    try std.testing.expectEqual(@as(u64, 2_001), metric_plans[0].rule_handle);
}

test "unsupported empty rule suppression survives checkpoint until capability changes" {
    var config = testConfig(1);
    const source_session_scratch = try testScratch(&config);
    defer std.testing.allocator.free(source_session_scratch);
    const source_session = try state.Session.create(&config);
    defer source_session.destroy();
    var sources = testSources();
    var rules = testRules();
    try std.testing.expectEqual(
        ResultCode.ok,
        replaceCatalog(source_session, 1, 1, 100, &sources, &rules),
    );

    var source_plans = std.mem.zeroes([4]protocol.SourcePlanOutput);
    var metric_plans = std.mem.zeroes([4]protocol.MetricPlanOutput);
    var plan_output = std.mem.zeroes(protocol.PlanOutput);
    const requested = [_]protocol.PlanMetricInput{planMetric(200)};
    var modes = testModes();
    try std.testing.expectEqual(
        ResultCode.ok,
        runPlan(
            source_session,
            source_session_scratch,
            2,
            1,
            200,
            0,
            &requested,
            &modes,
            &plan_output,
            &source_plans,
            &metric_plans,
        ),
    );
    var current_header = metricCompletion(3, 1, 10, 1, 1, 300, 1, 1, 0);
    current_header.capability_generation = source_plans[0].capability_generation;
    current_header.plan_token_fingerprint = source_plans[0].plan_token_fingerprint;
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.beginCompletion(source_session, &current_header, source_session_scratch),
    );
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.submitRequested(source_session, &.{requestedRule(2_001)}),
    );
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.submitObservations(
            source_session,
            &.{unsignedObservation(2_001, 200, 10, 2, 300, 4096)},
        ),
    );
    var current_finalize = finalizeInput(3, 1, 10, 300);
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.finalizeCompletion(source_session, &current_finalize),
    );

    try std.testing.expectEqual(
        ResultCode.ok,
        runPlan(
            source_session,
            source_session_scratch,
            4,
            2,
            400,
            0,
            &requested,
            &modes,
            &plan_output,
            &source_plans,
            &metric_plans,
        ),
    );
    var unsupported_header = metricCompletion(5, 2, 10, 1, 2, 500, 1, 1, 0);
    unsupported_header.capability_generation = source_plans[0].capability_generation;
    unsupported_header.plan_token_fingerprint = source_plans[0].plan_token_fingerprint;
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.beginCompletion(source_session, &unsupported_header, source_session_scratch),
    );
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.submitRequested(source_session, &.{requestedRule(2_001)}),
    );
    var unsupported = unsignedObservation(2_001, 200, 10, 2, 500, 0);
    unsupported.valid_mask = protocol.ObservationValid.required;
    unsupported.status = @intFromEnum(protocol.ObservationStatus.unsupported);
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.submitObservations(source_session, &.{unsupported}),
    );
    var unsupported_finalize = finalizeInput(5, 2, 10, 500);
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.finalizeCompletion(source_session, &unsupported_finalize),
    );
    const unsupported_rule = source_session.active.rules[2];
    try std.testing.expectEqual(protocol.MetricStatus.unsupported, unsupported_rule.status);
    try std.testing.expectEqual(@as(u64, 1), unsupported_rule.unsupported_until_capability_generation);

    try std.testing.expectEqual(
        ResultCode.ok,
        runPlan(
            source_session,
            source_session_scratch,
            6,
            3,
            600,
            0,
            &requested,
            &modes,
            &plan_output,
            &source_plans,
            &metric_plans,
        ),
    );
    try std.testing.expectEqual(@as(u64, 0), metric_plans[0].rule_handle);

    var source_outputs = std.mem.zeroes([source_count]protocol.SourcePersistenceOutput);
    var rule_outputs = std.mem.zeroes([rule_count]protocol.RuleStateOutput);
    var gpu_outputs = std.mem.zeroes([0]protocol.GpuInventoryOutput);
    var persisted = std.mem.zeroes(protocol.PersistenceHeader);
    var snapshot = std.mem.zeroes(protocol.SnapshotHeader);
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.querySnapshotHeader(source_session, &snapshot),
    );
    var read = readInput(snapshot);
    try std.testing.expectEqual(
        ResultCode.ok,
        persistence.exportState(
            source_session,
            &read,
            &persisted,
            &source_outputs,
            &rule_outputs,
            &gpu_outputs,
        ),
    );
    try std.testing.expectEqual(
        @as(u64, 1),
        rule_outputs[2].unsupported_until_capability_generation,
    );

    const target_scratch = try testScratch(&config);
    defer std.testing.allocator.free(target_scratch);
    const target = try state.Session.create(&config);
    defer target.destroy();
    try std.testing.expectEqual(
        ResultCode.ok,
        replaceCatalog(target, 1, 1, 100, &sources, &rules),
    );
    var import_input = persistenceInput(7, 700);
    try std.testing.expectEqual(
        ResultCode.ok,
        persistence.importState(
            target,
            &import_input,
            &persisted,
            &source_outputs,
            &rule_outputs,
            &gpu_outputs,
        ),
    );
    try std.testing.expectEqual(
        ResultCode.ok,
        runPlan(
            target,
            target_scratch,
            8,
            4,
            800,
            0,
            &requested,
            &modes,
            &plan_output,
            &source_plans,
            &metric_plans,
        ),
    );
    try std.testing.expectEqual(@as(u64, 0), metric_plans[0].rule_handle);
    modes[0].capability_generation = 2;
    try std.testing.expectEqual(
        ResultCode.ok,
        runPlan(
            target,
            target_scratch,
            9,
            5,
            900,
            0,
            &requested,
            &modes,
            &plan_output,
            &source_plans,
            &metric_plans,
        ),
    );
    try std.testing.expectEqual(@as(u64, 2_001), metric_plans[0].rule_handle);
}

test "persistence rejects source rule and time closure drift atomically" {
    var config = testConfig(1);
    const source_session_scratch = try testScratch(&config);
    defer std.testing.allocator.free(source_session_scratch);
    const source_session = try state.Session.create(&config);
    defer source_session.destroy();
    var sources = testSources();
    var rules = testRules();
    try std.testing.expectEqual(
        ResultCode.ok,
        replaceCatalog(source_session, 1, 1, 100, &sources, &rules),
    );
    try completeCpuCycle(source_session, source_session_scratch, 2, 3, 1, 200, 300, 1, 100, 500, 500, 1_000);
    try completeCpuCycle(source_session, source_session_scratch, 4, 5, 2, 400, 500, 2, 150, 650, 650, 1_200);

    var source_outputs = std.mem.zeroes([source_count]protocol.SourcePersistenceOutput);
    var rule_outputs = std.mem.zeroes([rule_count]protocol.RuleStateOutput);
    var gpu_outputs = std.mem.zeroes([0]protocol.GpuInventoryOutput);
    var persisted = std.mem.zeroes(protocol.PersistenceHeader);
    var snapshot = std.mem.zeroes(protocol.SnapshotHeader);
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.querySnapshotHeader(source_session, &snapshot),
    );
    var read = readInput(snapshot);
    try std.testing.expectEqual(
        ResultCode.ok,
        persistence.exportState(
            source_session,
            &read,
            &persisted,
            &source_outputs,
            &rule_outputs,
            &gpu_outputs,
        ),
    );

    rule_outputs[0].source_generation = source_outputs[0].source_generation + 1;
    rule_outputs[0].state_fingerprint = ruleStateFingerprint(&rule_outputs[0]);
    persisted.checksum = persistenceChecksum(
        &persisted,
        &source_outputs,
        &rule_outputs,
        &gpu_outputs,
    );
    const target_scratch = try testScratch(&config);
    defer std.testing.allocator.free(target_scratch);
    const target = try state.Session.create(&config);
    defer target.destroy();
    try std.testing.expectEqual(
        ResultCode.ok,
        replaceCatalog(target, 1, 1, 100, &sources, &rules),
    );
    const before_revision = target.state_revision;
    var import_input = persistenceInput(6, 600);
    try std.testing.expectEqual(
        ResultCode.invalid_argument,
        persistence.importState(
            target,
            &import_input,
            &persisted,
            &source_outputs,
            &rule_outputs,
            &gpu_outputs,
        ),
    );
    try std.testing.expectEqual(before_revision, target.state_revision);
}

test "persistence import invalidates local plan authority even at the same plan epoch" {
    var config = testConfig(1);
    const source_session_scratch = try testScratch(&config);
    defer std.testing.allocator.free(source_session_scratch);
    const source_session = try state.Session.create(&config);
    defer source_session.destroy();
    const target_scratch = try testScratch(&config);
    defer std.testing.allocator.free(target_scratch);
    const target = try state.Session.create(&config);
    defer target.destroy();
    var sources = testSources();
    var rules = testRules();
    try std.testing.expectEqual(
        ResultCode.ok,
        replaceCatalog(source_session, 1, 1, 100, &sources, &rules),
    );
    try std.testing.expectEqual(
        ResultCode.ok,
        replaceCatalog(target, 1, 1, 100, &sources, &rules),
    );

    var source_plans = std.mem.zeroes([4]protocol.SourcePlanOutput);
    var target_source_plans = std.mem.zeroes([4]protocol.SourcePlanOutput);
    var metric_plans = std.mem.zeroes([4]protocol.MetricPlanOutput);
    var target_metric_plans = std.mem.zeroes([4]protocol.MetricPlanOutput);
    var plan_output = std.mem.zeroes(protocol.PlanOutput);
    const requested = [_]protocol.PlanMetricInput{planMetric(100)};
    const modes = testModes();
    try std.testing.expectEqual(
        ResultCode.ok,
        runPlan(
            source_session,
            source_session_scratch,
            2,
            1,
            200,
            0,
            &requested,
            &modes,
            &plan_output,
            &source_plans,
            &metric_plans,
        ),
    );
    try std.testing.expectEqual(
        ResultCode.ok,
        runPlan(
            target,
            target_scratch,
            2,
            1,
            200,
            0,
            &requested,
            &modes,
            &plan_output,
            &target_source_plans,
            &target_metric_plans,
        ),
    );

    var source_outputs = std.mem.zeroes([source_count]protocol.SourcePersistenceOutput);
    var rule_outputs = std.mem.zeroes([rule_count]protocol.RuleStateOutput);
    var gpu_outputs = std.mem.zeroes([0]protocol.GpuInventoryOutput);
    var persisted = std.mem.zeroes(protocol.PersistenceHeader);
    var snapshot = std.mem.zeroes(protocol.SnapshotHeader);
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.querySnapshotHeader(source_session, &snapshot),
    );
    var read = readInput(snapshot);
    try std.testing.expectEqual(
        ResultCode.ok,
        persistence.exportState(
            source_session,
            &read,
            &persisted,
            &source_outputs,
            &rule_outputs,
            &gpu_outputs,
        ),
    );
    var import_input = persistenceInput(3, 300);
    try std.testing.expectEqual(
        ResultCode.ok,
        persistence.importState(
            target,
            &import_input,
            &persisted,
            &source_outputs,
            &rule_outputs,
            &gpu_outputs,
        ),
    );

    var stale_header = metricCompletion(4, 1, 10, 1, 1, 400, 1, 0, 1);
    stale_header.capability_generation = target_source_plans[0].capability_generation;
    stale_header.plan_token_fingerprint = target_source_plans[0].plan_token_fingerprint;
    try std.testing.expectEqual(
        ResultCode.stale_frame,
        session_module.beginCompletion(target, &stale_header, target_scratch),
    );
    try std.testing.expectEqual(
        ResultCode.ok,
        runPlan(
            target,
            target_scratch,
            5,
            2,
            500,
            0,
            &requested,
            &modes,
            &plan_output,
            &target_source_plans,
            &target_metric_plans,
        ),
    );
}

test "persistence rejects impossible source accounting and phase images atomically" {
    var config = testConfig(1);
    const source_session_scratch = try testScratch(&config);
    defer std.testing.allocator.free(source_session_scratch);
    const source_session = try state.Session.create(&config);
    defer source_session.destroy();
    var sources = testSources();
    var rules = testRules();
    try std.testing.expectEqual(
        ResultCode.ok,
        replaceCatalog(source_session, 1, 1, 100, &sources, &rules),
    );
    try completeCpuCycle(source_session, source_session_scratch, 2, 3, 1, 200, 300, 1, 100, 500, 500, 1_000);

    var source_outputs = std.mem.zeroes([source_count]protocol.SourcePersistenceOutput);
    var rule_outputs = std.mem.zeroes([rule_count]protocol.RuleStateOutput);
    var gpu_outputs = std.mem.zeroes([0]protocol.GpuInventoryOutput);
    var persisted = std.mem.zeroes(protocol.PersistenceHeader);
    var snapshot = std.mem.zeroes(protocol.SnapshotHeader);
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.querySnapshotHeader(source_session, &snapshot),
    );
    var read = readInput(snapshot);
    try std.testing.expectEqual(
        ResultCode.ok,
        persistence.exportState(
            source_session,
            &read,
            &persisted,
            &source_outputs,
            &rule_outputs,
            &gpu_outputs,
        ),
    );

    const target_scratch = try testScratch(&config);
    defer std.testing.allocator.free(target_scratch);
    const target = try state.Session.create(&config);
    defer target.destroy();
    try std.testing.expectEqual(
        ResultCode.ok,
        replaceCatalog(target, 1, 1, 100, &sources, &rules),
    );
    const before_revision = target.state_revision;
    const before_semantic = target.semantic_fingerprint;
    var import_input = persistenceInput(4, 400);

    var invalid_sources = source_outputs;
    invalid_sources[0].status = @intFromEnum(protocol.SourceStatus.partial);
    invalid_sources[0].overflow_count = 0;
    invalid_sources[0].state_fingerprint = sourceStateFingerprint(&invalid_sources[0]);
    var invalid_header = persisted;
    invalid_header.checksum = persistenceChecksum(
        &invalid_header,
        &invalid_sources,
        &rule_outputs,
        &gpu_outputs,
    );
    try std.testing.expectEqual(
        ResultCode.invalid_argument,
        persistence.importState(
            target,
            &import_input,
            &invalid_header,
            &invalid_sources,
            &rule_outputs,
            &gpu_outputs,
        ),
    );

    invalid_sources = source_outputs;
    invalid_sources[1].status = @intFromEnum(protocol.SourceStatus.complete);
    invalid_sources[1].state_fingerprint = sourceStateFingerprint(&invalid_sources[1]);
    invalid_header = persisted;
    invalid_header.checksum = persistenceChecksum(
        &invalid_header,
        &invalid_sources,
        &rule_outputs,
        &gpu_outputs,
    );
    try std.testing.expectEqual(
        ResultCode.invalid_argument,
        persistence.importState(
            target,
            &import_input,
            &invalid_header,
            &invalid_sources,
            &rule_outputs,
            &gpu_outputs,
        ),
    );

    invalid_sources = source_outputs;
    invalid_sources[0].reset_count = 1;
    invalid_sources[0].last_reset_reason_mask = 0;
    invalid_sources[0].state_fingerprint = sourceStateFingerprint(&invalid_sources[0]);
    invalid_header = persisted;
    invalid_header.checksum = persistenceChecksum(
        &invalid_header,
        &invalid_sources,
        &rule_outputs,
        &gpu_outputs,
    );
    try std.testing.expectEqual(
        ResultCode.invalid_argument,
        persistence.importState(
            target,
            &import_input,
            &invalid_header,
            &invalid_sources,
            &rule_outputs,
            &gpu_outputs,
        ),
    );

    invalid_sources = source_outputs;
    invalid_sources[0].reset_count = 1;
    invalid_sources[0].last_reset_reason_mask =
        protocol.SourceResetReason.incarnation_changed;
    invalid_sources[0].state_fingerprint = sourceStateFingerprint(&invalid_sources[0]);
    invalid_header = persisted;
    invalid_header.checksum = persistenceChecksum(
        &invalid_header,
        &invalid_sources,
        &rule_outputs,
        &gpu_outputs,
    );
    try std.testing.expectEqual(
        ResultCode.invalid_argument,
        persistence.importState(
            target,
            &import_input,
            &invalid_header,
            &invalid_sources,
            &rule_outputs,
            &gpu_outputs,
        ),
    );

    invalid_header = persisted;
    invalid_header.phase = @intFromEnum(protocol.Phase.completion_open);
    invalid_header.checksum = persistenceChecksum(
        &invalid_header,
        &source_outputs,
        &rule_outputs,
        &gpu_outputs,
    );
    try std.testing.expectEqual(
        ResultCode.abi_mismatch,
        persistence.importState(
            target,
            &import_input,
            &invalid_header,
            &source_outputs,
            &rule_outputs,
            &gpu_outputs,
        ),
    );
    try std.testing.expectEqual(before_revision, target.state_revision);
    try std.testing.expectEqual(before_semantic, target.semantic_fingerprint);
    try std.testing.expect(target.staging.findSource(10) != null);
}

test "checkpoint requires incarnation reset proof and an empty failed GPU image" {
    var config = testConfig(1);
    const source_session_scratch = try testScratch(&config);
    defer std.testing.allocator.free(source_session_scratch);
    const source_session = try state.Session.create(&config);
    defer source_session.destroy();
    var sources = testSources();
    var rules = testRules();
    try std.testing.expectEqual(
        ResultCode.ok,
        replaceCatalog(source_session, 1, 1, 100, &sources, &rules),
    );

    var source_plans = std.mem.zeroes([4]protocol.SourcePlanOutput);
    var metric_plans = std.mem.zeroes([4]protocol.MetricPlanOutput);
    var plan_output = std.mem.zeroes(protocol.PlanOutput);
    const requested = [_]protocol.PlanMetricInput{planMetric(200)};
    const modes = testModes();
    try std.testing.expectEqual(
        ResultCode.ok,
        runPlan(
            source_session,
            source_session_scratch,
            2,
            1,
            200,
            protocol.PlanFlags.include_gpu_inventory,
            &requested,
            &modes,
            &plan_output,
            &source_plans,
            &metric_plans,
        ),
    );
    try completeGpuCycle(
        source_session,
        source_session_scratch,
        3,
        1,
        1,
        300,
        source_plans[1],
        &.{
            gpuInput(101, 1, 11, 1_001, 1, 250),
            gpuInput(102, 2, 22, 1_002, 1, 250),
        },
        .complete,
        2,
    );
    try std.testing.expectEqual(
        ResultCode.ok,
        runPlan(
            source_session,
            source_session_scratch,
            4,
            2,
            400,
            protocol.PlanFlags.include_gpu_inventory,
            &requested,
            &modes,
            &plan_output,
            &source_plans,
            &metric_plans,
        ),
    );
    try completeGpuCycle(
        source_session,
        source_session_scratch,
        5,
        2,
        2,
        500,
        source_plans[1],
        &.{gpuInput(101, 1, 11, 1_001, 2, 500)},
        .partial,
        2,
    );

    var source_outputs = std.mem.zeroes([source_count]protocol.SourcePersistenceOutput);
    var rule_outputs = std.mem.zeroes([rule_count]protocol.RuleStateOutput);
    var gpu_outputs = std.mem.zeroes([2]protocol.GpuInventoryOutput);
    var persisted = std.mem.zeroes(protocol.PersistenceHeader);
    var snapshot = std.mem.zeroes(protocol.SnapshotHeader);
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.querySnapshotHeader(source_session, &snapshot),
    );
    var read = readInput(snapshot);
    try std.testing.expectEqual(
        ResultCode.ok,
        persistence.exportState(
            source_session,
            &read,
            &persisted,
            &source_outputs,
            &rule_outputs,
            gpu_outputs[0..persisted.gpu_adapter_count],
        ),
    );
    try std.testing.expectEqual(@as(u32, 0), source_outputs[2].gpu_inventory_count);
    try std.testing.expectEqual(@as(u32, 0), persisted.gpu_adapter_count);

    const target_scratch = try testScratch(&config);
    defer std.testing.allocator.free(target_scratch);
    const target = try state.Session.create(&config);
    defer target.destroy();
    try std.testing.expectEqual(
        ResultCode.ok,
        replaceCatalog(target, 1, 1, 100, &sources, &rules),
    );
    var import_input = persistenceInput(6, 600);
    const before_revision = target.state_revision;

    var invalid_sources = source_outputs;
    invalid_sources[2].gpu_inventory_count = 1;
    invalid_sources[2].state_fingerprint = sourceStateFingerprint(&invalid_sources[2]);
    var invalid_header = persisted;
    invalid_header.checksum = persistenceChecksum(
        &invalid_header,
        &invalid_sources,
        &rule_outputs,
        gpu_outputs[0..persisted.gpu_adapter_count],
    );
    try std.testing.expectEqual(
        ResultCode.invalid_argument,
        persistence.importState(
            target,
            &import_input,
            &invalid_header,
            &invalid_sources,
            &rule_outputs,
            gpu_outputs[0..persisted.gpu_adapter_count],
        ),
    );

    invalid_sources = source_outputs;
    invalid_sources[2].observed_count = std.math.maxInt(u32);
    invalid_sources[2].overflow_count = 1;
    invalid_sources[2].state_fingerprint = sourceStateFingerprint(&invalid_sources[2]);
    invalid_header = persisted;
    invalid_header.checksum = persistenceChecksum(
        &invalid_header,
        &invalid_sources,
        &rule_outputs,
        gpu_outputs[0..persisted.gpu_adapter_count],
    );
    try std.testing.expectEqual(
        ResultCode.invalid_argument,
        persistence.importState(
            target,
            &import_input,
            &invalid_header,
            &invalid_sources,
            &rule_outputs,
            gpu_outputs[0..persisted.gpu_adapter_count],
        ),
    );

    invalid_sources = source_outputs;
    invalid_sources[2].observed_count = config.maximum_gpu_adapter_count;
    invalid_sources[2].overflow_count = 1;
    invalid_sources[2].state_fingerprint = sourceStateFingerprint(&invalid_sources[2]);
    invalid_header = persisted;
    invalid_header.checksum = persistenceChecksum(
        &invalid_header,
        &invalid_sources,
        &rule_outputs,
        gpu_outputs[0..persisted.gpu_adapter_count],
    );
    try std.testing.expectEqual(
        ResultCode.invalid_argument,
        persistence.importState(
            target,
            &import_input,
            &invalid_header,
            &invalid_sources,
            &rule_outputs,
            gpu_outputs[0..persisted.gpu_adapter_count],
        ),
    );

    var invalid_gpu = gpu_outputs;
    invalid_gpu[0].status = @intFromEnum(protocol.InventoryStatus.current);
    invalid_gpu[0].semantic_fingerprint = gpuOutputFingerprint(&invalid_gpu[0]);
    invalid_header = persisted;
    invalid_header.checksum = persistenceChecksum(
        &invalid_header,
        &source_outputs,
        &rule_outputs,
        &invalid_gpu,
    );
    try std.testing.expectEqual(
        ResultCode.abi_mismatch,
        persistence.importState(
            target,
            &import_input,
            &invalid_header,
            &source_outputs,
            &rule_outputs,
            &invalid_gpu,
        ),
    );
    try std.testing.expectEqual(before_revision, target.state_revision);
    try std.testing.expectEqual(
        ResultCode.ok,
        persistence.importState(
            target,
            &import_input,
            &persisted,
            &source_outputs,
            &rule_outputs,
            gpu_outputs[0..persisted.gpu_adapter_count],
        ),
    );

    var resetless_sources = source_outputs;
    resetless_sources[2].source_incarnation += 1;
    resetless_sources[2].source_generation += 1;
    resetless_sources[2].source_observation_sequence += 1;
    resetless_sources[2].state_fingerprint = sourceStateFingerprint(&resetless_sources[2]);
    invalid_header = persisted;
    invalid_header.state_revision = target.state_revision;
    invalid_header.last_operation_epoch = import_input.operation_epoch;
    invalid_header.last_command_at_milliseconds = import_input.command_at_milliseconds;
    invalid_header.checksum = persistenceChecksum(
        &invalid_header,
        &resetless_sources,
        &rule_outputs,
        gpu_outputs[0..persisted.gpu_adapter_count],
    );
    import_input.operation_epoch = 7;
    import_input.command_at_milliseconds = 700;
    try std.testing.expectEqual(
        ResultCode.invalid_argument,
        persistence.importState(
            target,
            &import_input,
            &invalid_header,
            &resetless_sources,
            &rule_outputs,
            gpu_outputs[0..persisted.gpu_adapter_count],
        ),
    );
}

test "failed revision and generation exhaustion commands leave authoritative state unchanged" {
    var config = testConfig(1);
    const begin_session_scratch = try testScratch(&config);
    defer std.testing.allocator.free(begin_session_scratch);
    const begin_session = try state.Session.create(&config);
    defer begin_session.destroy();
    var sources = testSources();
    var rules = testRules();
    try std.testing.expectEqual(
        ResultCode.ok,
        replaceCatalog(begin_session, 1, 1, 100, &sources, &rules),
    );

    var source_plans = std.mem.zeroes([4]protocol.SourcePlanOutput);
    var metric_plans = std.mem.zeroes([4]protocol.MetricPlanOutput);
    var plan_output = std.mem.zeroes(protocol.PlanOutput);
    const requested = [_]protocol.PlanMetricInput{planMetric(100)};
    const modes = testModes();
    try std.testing.expectEqual(
        ResultCode.ok,
        runPlan(
            begin_session,
            begin_session_scratch,
            2,
            1,
            200,
            0,
            &requested,
            &modes,
            &plan_output,
            &source_plans,
            &metric_plans,
        ),
    );

    var header = metricCompletion(3, 1, 10, 1, 1, 300, 1, 0, 1);
    header.capability_generation = source_plans[0].capability_generation;
    header.plan_token_fingerprint = source_plans[0].plan_token_fingerprint;
    const phase_before_begin = begin_session.phase;
    const completion_before_begin = begin_session.completion;
    header.plan_token_fingerprint ^= 1;
    try std.testing.expectEqual(
        ResultCode.stale_frame,
        session_module.beginCompletion(begin_session, &header, begin_session_scratch),
    );
    try std.testing.expectEqual(phase_before_begin, begin_session.phase);
    try std.testing.expectEqualDeep(completion_before_begin, begin_session.completion);
    header.plan_token_fingerprint ^= 1;
    begin_session.state_revision = std.math.maxInt(u64);
    try std.testing.expectEqual(
        ResultCode.unavailable,
        session_module.beginCompletion(begin_session, &header, begin_session_scratch),
    );
    try std.testing.expectEqual(phase_before_begin, begin_session.phase);
    try std.testing.expectEqualDeep(completion_before_begin, begin_session.completion);
    try std.testing.expectEqual(std.math.maxInt(u64), begin_session.state_revision);

    const abort_session_scratch = try testScratch(&config);
    defer std.testing.allocator.free(abort_session_scratch);
    const abort_session = try state.Session.create(&config);
    defer abort_session.destroy();
    try std.testing.expectEqual(
        ResultCode.ok,
        replaceCatalog(abort_session, 1, 1, 100, &sources, &rules),
    );
    source_plans = std.mem.zeroes([4]protocol.SourcePlanOutput);
    metric_plans = std.mem.zeroes([4]protocol.MetricPlanOutput);
    try std.testing.expectEqual(
        ResultCode.ok,
        runPlan(
            abort_session,
            abort_session_scratch,
            2,
            1,
            200,
            0,
            &requested,
            &modes,
            &plan_output,
            &source_plans,
            &metric_plans,
        ),
    );
    header.capability_generation = source_plans[0].capability_generation;
    header.plan_token_fingerprint = source_plans[0].plan_token_fingerprint;
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.beginCompletion(abort_session, &header, abort_session_scratch),
    );
    const completion_before_abort = abort_session.completion;
    abort_session.state_revision = std.math.maxInt(u64);
    var abort_input = finalizeInput(3, 1, 10, 300);
    try std.testing.expectEqual(
        ResultCode.unavailable,
        session_module.abortCompletion(abort_session, &abort_input),
    );
    try std.testing.expectEqual(protocol.Phase.completion_open, abort_session.phase);
    try std.testing.expectEqualDeep(completion_before_abort, abort_session.completion);

    const reset_session_scratch = try testScratch(&config);
    defer std.testing.allocator.free(reset_session_scratch);
    const reset_session = try state.Session.create(&config);
    defer reset_session.destroy();
    try std.testing.expectEqual(
        ResultCode.ok,
        replaceCatalog(reset_session, 1, 1, 100, &sources, &rules),
    );
    const catalog_before_reset = reset_session.catalog_fingerprint;
    const semantic_before_reset = reset_session.semantic_fingerprint;
    reset_session.committed_generation = std.math.maxInt(u64);
    var reset_input = controlInput(2, 200);
    try std.testing.expectEqual(
        ResultCode.unavailable,
        session_module.reset(reset_session, &reset_input),
    );
    try std.testing.expectEqual(catalog_before_reset, reset_session.catalog_fingerprint);
    try std.testing.expectEqual(semantic_before_reset, reset_session.semantic_fingerprint);
    try std.testing.expectEqual(std.math.maxInt(u64), reset_session.committed_generation);
}

test "CPU counter reset reasons cover overflow regression and frequency changes" {
    var config = testConfig(1);
    const session_scratch = try testScratch(&config);
    defer std.testing.allocator.free(session_scratch);
    const session = try state.Session.create(&config);
    defer session.destroy();
    var sources = testSources();
    var rules = testRules();
    try std.testing.expectEqual(
        ResultCode.ok,
        replaceCatalog(session, 1, 1, 100, &sources, &rules),
    );

    try completeCpuCycleWithFrequency(session, session_scratch, 2, 3, 1, 200, 300, 1, 0, 0, 0, 100, 1_000);
    try completeCpuCycleWithFrequency(
        session,
        session_scratch,
        4,
        5,
        2,
        400,
        500,
        2,
        0,
        std.math.maxInt(u64),
        std.math.maxInt(u64),
        200,
        1_000,
    );
    try std.testing.expectEqual(
        protocol.SourceResetReason.arithmetic_overflow,
        session.active.sources[0].last_reset_reason_mask,
    );
    try std.testing.expectEqual(@as(u32, 1), session.active.sources[0].reset_count);

    try completeCpuCycleWithFrequency(
        session,
        session_scratch,
        6,
        7,
        3,
        600,
        700,
        3,
        0,
        1,
        1,
        150,
        2_000,
    );
    const expected = protocol.SourceResetReason.counter_regressed |
        protocol.SourceResetReason.monotonic_regressed |
        protocol.SourceResetReason.tick_frequency_changed;
    try std.testing.expectEqual(expected, session.active.sources[0].last_reset_reason_mask);
    try std.testing.expectEqual(@as(u32, 2), session.active.sources[0].reset_count);
}

test "catalog replacement preserves only the exact GPU inventory source definition" {
    var config = testConfig(1);
    const session_scratch = try testScratch(&config);
    defer std.testing.allocator.free(session_scratch);
    const session = try state.Session.create(&config);
    defer session.destroy();
    var sources = testSources();
    var rules = testRules();
    try std.testing.expectEqual(
        ResultCode.ok,
        replaceCatalog(session, 1, 1, 100, &sources, &rules),
    );

    var source_plans = std.mem.zeroes([4]protocol.SourcePlanOutput);
    var metric_plans = std.mem.zeroes([4]protocol.MetricPlanOutput);
    var plan_output = std.mem.zeroes(protocol.PlanOutput);
    const requested = [_]protocol.PlanMetricInput{planMetric(200)};
    const modes = testModes();
    try std.testing.expectEqual(
        ResultCode.ok,
        runPlan(
            session,
            session_scratch,
            2,
            1,
            200,
            protocol.PlanFlags.include_gpu_inventory,
            &requested,
            &modes,
            &plan_output,
            &source_plans,
            &metric_plans,
        ),
    );
    try completeGpuCycle(
        session,
        session_scratch,
        3,
        1,
        1,
        300,
        source_plans[1],
        &.{
            gpuInput(101, 1, 11, 1_001, 1, 300),
            gpuInput(102, 2, 22, 1_002, 1, 300),
        },
        .complete,
        2,
    );
    try std.testing.expectEqual(@as(u32, 2), @import("inventory.zig").activeCount(session));

    try std.testing.expectEqual(
        ResultCode.ok,
        replaceCatalog(session, 4, 2, 400, &sources, &rules),
    );
    try std.testing.expectEqual(@as(u32, 2), @import("inventory.zig").activeCount(session));

    sources[2].capability_mask = 0x40;
    sources[2].semantic_fingerprint += 1;
    try std.testing.expectEqual(
        ResultCode.ok,
        replaceCatalog(session, 5, 3, 500, &sources, &rules),
    );
    try std.testing.expectEqual(@as(u32, 0), @import("inventory.zig").activeCount(session));
}

test "catalog generation exhaustion restores prepared GPU staging exactly" {
    var config = testConfig(1);
    const session_scratch = try testScratch(&config);
    defer std.testing.allocator.free(session_scratch);
    const session = try state.Session.create(&config);
    defer session.destroy();
    var sources = testSources();
    var rules = testRules();
    try std.testing.expectEqual(
        ResultCode.ok,
        replaceCatalog(session, 1, 1, 100, &sources, &rules),
    );

    var source_plans = std.mem.zeroes([4]protocol.SourcePlanOutput);
    var metric_plans = std.mem.zeroes([4]protocol.MetricPlanOutput);
    var plan_output = std.mem.zeroes(protocol.PlanOutput);
    const requested = [_]protocol.PlanMetricInput{planMetric(200)};
    const modes = testModes();
    try std.testing.expectEqual(
        ResultCode.ok,
        runPlan(
            session,
            session_scratch,
            2,
            1,
            200,
            protocol.PlanFlags.include_gpu_inventory,
            &requested,
            &modes,
            &plan_output,
            &source_plans,
            &metric_plans,
        ),
    );
    try completeGpuCycle(
        session,
        session_scratch,
        3,
        1,
        1,
        300,
        source_plans[1],
        &.{
            gpuInput(101, 1, 11, 1_001, 1, 300),
            gpuInput(102, 2, 22, 1_002, 1, 300),
        },
        .complete,
        2,
    );
    const first_fingerprint = session.gpu_active[0].output.semantic_fingerprint;
    const second_fingerprint = session.gpu_active[1].output.semantic_fingerprint;
    const active_semantic = session.semantic_fingerprint;

    sources[2].capability_mask = 0x40;
    sources[2].semantic_fingerprint += 1;
    session.committed_generation = std.math.maxInt(u64);
    try std.testing.expectEqual(
        ResultCode.unavailable,
        replaceCatalog(session, 4, 2, 400, &sources, &rules),
    );
    try std.testing.expectEqual(active_semantic, session.semantic_fingerprint);
    try std.testing.expectEqual(@as(u32, 2), @import("inventory.zig").activeCount(session));
    try std.testing.expectEqual(@as(u32, 2), session.gpu_staging_count);
    try std.testing.expectEqual(
        first_fingerprint,
        session.gpu_staging[0].output.semantic_fingerprint,
    );
    try std.testing.expectEqual(
        second_fingerprint,
        session.gpu_staging[1].output.semantic_fingerprint,
    );
}

test "persistence rejects rollback drift and revision exhaustion without publishing staging" {
    var config = testConfig(1);
    const source_session_scratch = try testScratch(&config);
    defer std.testing.allocator.free(source_session_scratch);
    const source_session = try state.Session.create(&config);
    defer source_session.destroy();
    var sources = testSources();
    var rules = testRules();
    try std.testing.expectEqual(
        ResultCode.ok,
        replaceCatalog(source_session, 1, 1, 100, &sources, &rules),
    );
    source_session.state_revision = std.math.maxInt(u64);

    var source_outputs = std.mem.zeroes([source_count]protocol.SourcePersistenceOutput);
    var rule_outputs = std.mem.zeroes([rule_count]protocol.RuleStateOutput);
    var gpu_outputs = std.mem.zeroes([0]protocol.GpuInventoryOutput);
    var persisted = std.mem.zeroes(protocol.PersistenceHeader);
    var snapshot = std.mem.zeroes(protocol.SnapshotHeader);
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.querySnapshotHeader(source_session, &snapshot),
    );
    var read = readInput(snapshot);
    try std.testing.expectEqual(
        ResultCode.ok,
        persistence.exportState(
            source_session,
            &read,
            &persisted,
            &source_outputs,
            &rule_outputs,
            &gpu_outputs,
        ),
    );

    const target_scratch = try testScratch(&config);
    defer std.testing.allocator.free(target_scratch);
    const target = try state.Session.create(&config);
    defer target.destroy();
    try std.testing.expectEqual(
        ResultCode.ok,
        replaceCatalog(target, 1, 1, 100, &sources, &rules),
    );
    var import_input = persistenceInput(2, 200);
    const target_revision = target.state_revision;
    const target_phase = target.phase;
    const target_semantic = target.semantic_fingerprint;

    var rollback = persisted;
    rollback.committed_generation = 0;
    try std.testing.expectEqual(
        ResultCode.abi_mismatch,
        persistence.importState(
            target,
            &import_input,
            &rollback,
            &source_outputs,
            &rule_outputs,
            &gpu_outputs,
        ),
    );
    var drift = persisted;
    drift.committed_generation = target.committed_generation;
    drift.semantic_fingerprint ^= 1;
    try std.testing.expectEqual(
        ResultCode.abi_mismatch,
        persistence.importState(
            target,
            &import_input,
            &drift,
            &source_outputs,
            &rule_outputs,
            &gpu_outputs,
        ),
    );
    try std.testing.expectEqual(
        ResultCode.unavailable,
        persistence.importState(
            target,
            &import_input,
            &persisted,
            &source_outputs,
            &rule_outputs,
            &gpu_outputs,
        ),
    );
    try std.testing.expectEqual(target_revision, target.state_revision);
    try std.testing.expectEqual(target_phase, target.phase);
    try std.testing.expectEqual(target_semantic, target.semantic_fingerprint);
}

test "declared capacities and resident budget are exact production gates" {
    var config = testConfig(1);
    config.maximum_source_count = source_count;
    config.maximum_metric_count = metric_count;
    config.maximum_rule_count = rule_count;
    config.maximum_requested_count = metric_count;
    config.maximum_observation_count = rule_count;
    config.maximum_gpu_adapter_count = 2;
    config.maximum_persistence_source_count = source_count;
    config.maximum_persistence_rule_count = rule_count;
    config.maximum_persistence_gpu_count = 2;
    config.source_index_capacity = 4;
    config.metric_index_capacity = 2;
    config.rule_index_capacity = 4;
    config.gpu_index_capacity = 2;
    config.maximum_plan_metric_count = metric_count;
    config.maximum_source_mode_count = source_count;
    config.maximum_source_plan_count = source_count;
    config.maximum_metric_plan_count = rule_count;
    config.gpu_luid_index_capacity = 2;
    config.gpu_key_index_capacity = 2;

    const probe_scratch = try testScratch(&config);
    defer std.testing.allocator.free(probe_scratch);
    const probe = try state.Session.create(&config);
    const empty_required = probe.requiredByteCount();
    var sources = testSources();
    var rules = testRules();
    try std.testing.expectEqual(ResultCode.ok, replaceCatalog(probe, 1, 1, 100, &sources, &rules));
    const exact_resident = probe.requiredByteCount();
    probe.destroy();
    config.resident_byte_budget = exact_resident;
    const session_scratch = try testScratch(&config);
    defer std.testing.allocator.free(session_scratch);
    const session = try state.Session.create(&config);
    defer session.destroy();
    try std.testing.expectEqual(empty_required, session.requiredByteCount());

    try std.testing.expectEqual(
        ResultCode.ok,
        replaceCatalog(session, 1, 1, 100, &sources, &rules),
    );
    try std.testing.expectEqual(exact_resident, session.capacity().resident_byte_count + session.capacity().scratch_byte_count);
    var source_plans = std.mem.zeroes([source_count]protocol.SourcePlanOutput);
    var metric_plans = std.mem.zeroes([rule_count]protocol.MetricPlanOutput);
    var plan_output = std.mem.zeroes(protocol.PlanOutput);
    const requested = [_]protocol.PlanMetricInput{
        planMetric(100),
        planMetric(200),
    };
    const modes = testModes();
    try std.testing.expectEqual(
        ResultCode.ok,
        runPlan(
            session,
            session_scratch,
            2,
            1,
            200,
            protocol.PlanFlags.include_gpu_inventory,
            &requested,
            &modes,
            &plan_output,
            &source_plans,
            &metric_plans,
        ),
    );
    try std.testing.expectEqual(@as(u32, 2), plan_output.metric_plan_count);
    try completeGpuCycle(
        session,
        session_scratch,
        3,
        1,
        1,
        300,
        source_plans[1],
        &.{
            gpuInput(101, 1, 11, 1_001, 1, 300),
            gpuInput(102, 2, 22, 1_002, 1, 300),
        },
        .complete,
        2,
    );
    try std.testing.expectEqual(@as(u32, 2), @import("inventory.zig").activeCount(session));
    try std.testing.expect(!session.gpu_staging_prepared);
    const first_gpu_fingerprint = session.gpu_active[0].output.semantic_fingerprint;
    const second_gpu_fingerprint = session.gpu_active[1].output.semantic_fingerprint;

    var cpu_header = metricCompletion(4, 1, 10, 1, 2, 400, 2, 1, 1);
    cpu_header.capability_generation = source_plans[0].capability_generation;
    cpu_header.plan_token_fingerprint = source_plans[0].plan_token_fingerprint;
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.beginCompletion(session, &cpu_header, session_scratch),
    );
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.submitRequested(
            session,
            &.{ requestedRule(1_001), requestedRule(2_001) },
        ),
    );
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.submitObservations(
            session,
            &.{unsignedObservation(2_001, 200, 10, 2, 400, 4096)},
        ),
    );
    var counter = cpuCounter(2, 400, 100, 500, 500, 1_000);
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.submitCpuCounter(session, &counter),
    );
    var cpu_finalize = finalizeInput(4, 1, 10, 400);
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.finalizeCompletion(session, &cpu_finalize),
    );
    try std.testing.expect(!session.gpu_staging_prepared);
    try std.testing.expectEqual(@as(u32, 2), @import("inventory.zig").activeCount(session));
    try std.testing.expectEqual(
        first_gpu_fingerprint,
        session.gpu_active[0].output.semantic_fingerprint,
    );
    try std.testing.expectEqual(
        second_gpu_fingerprint,
        session.gpu_active[1].output.semantic_fingerprint,
    );

    config.resident_byte_budget = exact_resident - 1;
    const limited = try state.Session.create(&config);
    defer limited.destroy();
    try std.testing.expectEqual(ResultCode.invalid_argument, replaceCatalog(limited, 1, 1, 100, &sources, &rules));
    try std.testing.expectEqual(@as(u64, 0), limited.catalog_generation);
    config.resident_byte_budget = empty_required - 1;
    try std.testing.expectError(error.InvalidConfiguration, state.Session.create(&config));
}

test "catalog banks and scratch follow real rows across growth shrink and rejected replacements" {
    var config = testConfig(1);
    config.maximum_source_count = 1024;
    config.maximum_metric_count = 4096;
    config.maximum_rule_count = 8192;
    config.maximum_persistence_source_count = 1024;
    config.maximum_persistence_rule_count = 8192;
    config.maximum_source_mode_count = 1024;
    config.maximum_source_plan_count = 1024;
    config.source_index_capacity = 1024;
    config.metric_index_capacity = 4096;
    config.rule_index_capacity = 8192;
    const session = try state.Session.create(&config);
    defer session.destroy();
    const empty_resident = session.capacity().resident_byte_count;
    try std.testing.expectEqual(@as(u64, 8), session.capacity().scratch_byte_count);
    try std.testing.expectEqual(@as(usize, 0), session.active.rules.len);
    try std.testing.expectEqual(@as(usize, 0), session.staging.rules.len);
    try std.testing.expectEqual(@as(u32, 8192), session.capacity().rule_capacity);

    const sources = testSources();
    const rules = testRules();
    try std.testing.expectEqual(ResultCode.ok, replaceCatalog(session, 1, 1, 100, &sources, &rules));
    const small_capacity = session.capacity();
    try std.testing.expect(small_capacity.resident_byte_count < 64 * 1024);
    try std.testing.expectEqual(@as(usize, 3), session.active.sources.len);
    try std.testing.expectEqual(@as(usize, 3), session.active.rules.len);
    try std.testing.expectEqual(@as(usize, 2), session.active.metrics.len);
    try std.testing.expectEqual(@as(usize, 3), session.staging.rules.len);
    try std.testing.expectEqual(@as(usize, 3), session.source_modes.len);
    try std.testing.expectEqual(@as(usize, 3), session.planned_rule_flags.len);
    try std.testing.expectEqual(@as(u32, 3), session.scratch_layout.observation_count);
    try std.testing.expectEqual(@as(u32, 2), session.scratch_layout.plan_metric_count);
    const bytes = try std.testing.allocator.alignedAlloc(u8, .@"8", small_capacity.scratch_byte_count);
    defer std.testing.allocator.free(bytes);
    try completeCpuCycle(session, bytes, 2, 3, 1, 200, 300, 1, 100, 200, 100, 1000);
    try completeCpuCycle(session, bytes, 4, 5, 2, 400, 500, 2, 120, 260, 140, 1200);
    const cpu_before = session.active.sources[0];
    const value_before = session.active.metrics[0].output.value_bits;
    try std.testing.expect(cpu_before.cpu_baseline_valid);
    try std.testing.expectEqual(protocol.MetricStatus.current, session.active.rules[0].status);

    var output = std.mem.zeroes(protocol.PlanOutput);
    var source_plans = std.mem.zeroes([4]protocol.SourcePlanOutput);
    var metric_plans = std.mem.zeroes([4]protocol.MetricPlanOutput);
    const modes = testModes();
    try std.testing.expectEqual(ResultCode.ok, runPlan(session, bytes, 6, 3, 600, 0, &.{planMetric(200)}, &modes, &output, &source_plans, &metric_plans));
    var header = metricCompletion(7, 3, 10, 1, 3, 700, 1, 1, 0);
    header.capability_generation = source_plans[0].capability_generation;
    header.plan_token_fingerprint = source_plans[0].plan_token_fingerprint;
    try std.testing.expectEqual(ResultCode.ok, session_module.beginCompletion(session, &header, bytes));
    try std.testing.expectEqual(ResultCode.ok, session_module.submitRequested(session, &.{requestedRule(2001)}));
    var unsupported = unsignedObservation(2001, 200, 10, 2, 700, 0);
    unsupported.source_observation_sequence = 3;
    unsupported.valid_mask = protocol.ObservationValid.required;
    unsupported.status = @intFromEnum(protocol.ObservationStatus.unsupported);
    try std.testing.expectEqual(ResultCode.ok, session_module.submitObservations(session, &.{unsupported}));
    const finish = finalizeInput(7, 3, 10, 700);
    try std.testing.expectEqual(ResultCode.ok, session_module.finalizeCompletion(session, &finish));
    try std.testing.expectEqual(protocol.MetricStatus.unsupported, session.active.rules[2].status);
    const suppressed_until = session.active.rules[2].unsupported_until_capability_generation;

    var expanded_sources: [4]protocol.SourcePolicyInput = undefined;
    @memcpy(expanded_sources[0..3], &sources);
    expanded_sources[3] = sourcePolicy(40, .metrics, 4, 0x01);
    var expanded_rules: [4]protocol.MetricDefinitionInput = undefined;
    @memcpy(expanded_rules[0..3], &rules);
    expanded_rules[3] = rules[2];
    expanded_rules[3].rule_handle = 3001;
    expanded_rules[3].metric_handle = 300;
    expanded_rules[3].source_handle = 40;
    expanded_rules[3].source_priority = 4;
    expanded_rules[3].semantic_fingerprint += 1;

    var invalid_rules = expanded_rules;
    invalid_rules[3].source_handle = 50;
    const active_address = @intFromPtr(session.active.rules.ptr);
    const revision = session.state_revision;
    try std.testing.expectEqual(ResultCode.invalid_argument, replaceCatalog(session, 8, 2, 800, &expanded_sources, &invalid_rules));
    try std.testing.expectEqual(active_address, @intFromPtr(session.active.rules.ptr));
    try std.testing.expectEqual(revision, session.state_revision);
    try std.testing.expectEqual(small_capacity.resident_byte_count, session.capacity().resident_byte_count);
    try std.testing.expectEqual(value_before, session.active.metrics[0].output.value_bits);
    const original_budget = session.config.resident_byte_budget;
    const original_allocator = session.allocator;
    var failing = std.testing.FailingAllocator.init(std.testing.allocator, .{ .fail_index = 8 });
    session.allocator = failing.allocator();
    const failed_allocation_result = replaceCatalog(session, 8, 2, 800, &expanded_sources, &expanded_rules);
    session.allocator = original_allocator;
    try std.testing.expectEqual(ResultCode.out_of_memory, failed_allocation_result);
    try std.testing.expectEqual(failing.allocated_bytes, failing.freed_bytes);
    try std.testing.expectEqual(active_address, @intFromPtr(session.active.rules.ptr));
    try std.testing.expectEqual(revision, session.state_revision);
    session.config.resident_byte_budget = session.requiredByteCount();
    try std.testing.expectEqual(ResultCode.invalid_argument, replaceCatalog(session, 8, 2, 800, &expanded_sources, &expanded_rules));
    session.config.resident_byte_budget = original_budget;
    try std.testing.expectEqual(active_address, @intFromPtr(session.active.rules.ptr));

    try std.testing.expectEqual(ResultCode.ok, replaceCatalog(session, 8, 2, 800, &expanded_sources, &expanded_rules));
    try std.testing.expectEqual(@as(usize, 4), session.active.rules.len);
    try std.testing.expectEqual(@as(usize, 4), session.staging.rules.len);
    try std.testing.expectEqual(@as(usize, 3), session.active.metrics.len);
    try std.testing.expectEqual(cpu_before.cpu_idle_ticks, session.active.sources[0].cpu_idle_ticks);
    try std.testing.expectEqual(cpu_before.cpu_monotonic_ticks, session.active.sources[0].cpu_monotonic_ticks);
    try std.testing.expectEqual(value_before, session.active.metrics[0].output.value_bits);
    try std.testing.expectEqual(protocol.MetricStatus.unsupported, session.active.rules[2].status);
    try std.testing.expectEqual(suppressed_until, session.active.rules[2].unsupported_until_capability_generation);
    try std.testing.expect(session.capacity().resident_byte_count > small_capacity.resident_byte_count);
    try std.testing.expect(session.capacity().scratch_byte_count > small_capacity.scratch_byte_count);

    try std.testing.expectEqual(ResultCode.ok, replaceCatalog(session, 9, 3, 900, &sources, &rules));
    try std.testing.expectEqual(small_capacity.resident_byte_count, session.capacity().resident_byte_count);
    try std.testing.expectEqual(small_capacity.scratch_byte_count, session.capacity().scratch_byte_count);
    try std.testing.expectEqual(value_before, session.active.metrics[0].output.value_bits);
    // The exact small region remains sufficient after a rejected growth and a shrink.
    try std.testing.expectEqual(ResultCode.ok, runPlan(session, bytes, 10, 4, 1000, 0, &.{planMetric(100)}, &modes, &output, &source_plans, &metric_plans));
    const reset = controlInput(11, 1100);
    try std.testing.expectEqual(ResultCode.ok, session_module.reset(session, &reset));
    try std.testing.expectEqual(empty_resident, session.capacity().resident_byte_count);
    try std.testing.expectEqual(@as(u64, 8), session.capacity().scratch_byte_count);
}

fn completeCpuCycle(
    session: *state.Session,
    scratch_bytes: []u8,
    plan_operation_epoch: u64,
    completion_operation_epoch: u64,
    plan_epoch: u64,
    plan_at: u64,
    captured_at: u64,
    sequence: u64,
    idle_ticks: u64,
    kernel_ticks: u64,
    user_ticks: u64,
    monotonic_ticks: u64,
) !void {
    return completeCpuCycleWithFrequency(
        session,
        scratch_bytes,
        plan_operation_epoch,
        completion_operation_epoch,
        plan_epoch,
        plan_at,
        captured_at,
        sequence,
        idle_ticks,
        kernel_ticks,
        user_ticks,
        monotonic_ticks,
        1_000,
    );
}

fn completeCpuCycleWithFrequency(
    session: *state.Session,
    scratch_bytes: []u8,
    plan_operation_epoch: u64,
    completion_operation_epoch: u64,
    plan_epoch: u64,
    plan_at: u64,
    captured_at: u64,
    sequence: u64,
    idle_ticks: u64,
    kernel_ticks: u64,
    user_ticks: u64,
    monotonic_ticks: u64,
    monotonic_ticks_per_second: u64,
) !void {
    var source_plans = std.mem.zeroes([4]protocol.SourcePlanOutput);
    var metric_plans = std.mem.zeroes([4]protocol.MetricPlanOutput);
    var plan_output = std.mem.zeroes(protocol.PlanOutput);
    const requested = [_]protocol.PlanMetricInput{planMetric(100)};
    const modes = testModes();
    try std.testing.expectEqual(
        ResultCode.ok,
        runPlan(
            session,
            scratch_bytes,
            plan_operation_epoch,
            plan_epoch,
            plan_at,
            0,
            &requested,
            &modes,
            &plan_output,
            &source_plans,
            &metric_plans,
        ),
    );
    var header = metricCompletion(
        completion_operation_epoch,
        plan_epoch,
        10,
        1,
        sequence,
        captured_at,
        1,
        0,
        1,
    );
    header.capability_generation = source_plans[0].capability_generation;
    header.plan_token_fingerprint = source_plans[0].plan_token_fingerprint;
    try std.testing.expectEqual(ResultCode.ok, session_module.beginCompletion(session, &header, scratch_bytes));
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.submitRequested(session, &.{requestedRule(1_001)}),
    );
    try std.testing.expectEqual(ResultCode.ok, session_module.submitObservations(session, &.{}));
    var counter = cpuCounter(
        sequence,
        captured_at,
        idle_ticks,
        kernel_ticks,
        user_ticks,
        monotonic_ticks,
    );
    counter.monotonic_ticks_per_second = monotonic_ticks_per_second;
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.submitCpuCounter(session, &counter),
    );
    var finalize = finalizeInput(
        completion_operation_epoch,
        plan_epoch,
        10,
        captured_at,
    );
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.finalizeCompletion(session, &finalize),
    );
}

fn completeGpuCycle(
    session: *state.Session,
    scratch_bytes: []u8,
    completion_operation_epoch: u64,
    plan_epoch: u64,
    sequence: u64,
    captured_at: u64,
    source_plan: protocol.SourcePlanOutput,
    inputs: []const protocol.GpuInventoryInput,
    status: protocol.SourceStatus,
    expected_count: u32,
) !void {
    var header = gpuCompletion(
        completion_operation_epoch,
        plan_epoch,
        30,
        1,
        sequence,
        captured_at,
        @intCast(inputs.len),
    );
    header.capability_generation = source_plan.capability_generation;
    header.plan_token_fingerprint = source_plan.plan_token_fingerprint;
    header.expected_gpu_inventory_count = expected_count;
    header.status = @intFromEnum(status);
    header.overflow_count = expected_count - @as(u32, @intCast(inputs.len));
    try std.testing.expectEqual(ResultCode.ok, session_module.beginCompletion(session, &header, scratch_bytes));
    try std.testing.expectEqual(ResultCode.ok, session_module.submitRequested(session, &.{}));
    try std.testing.expectEqual(ResultCode.ok, session_module.submitObservations(session, &.{}));
    try std.testing.expectEqual(ResultCode.ok, session_module.submitGpuInventory(session, inputs));
    var finalize = finalizeInput(
        completion_operation_epoch,
        plan_epoch,
        30,
        captured_at,
    );
    try std.testing.expectEqual(
        ResultCode.ok,
        session_module.finalizeCompletion(session, &finalize),
    );
}

fn controlInput(operation_epoch: u64, command_at: u64) protocol.ControlInput {
    return .{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.ControlInput),
        .configuration_generation = 1,
        .operation_epoch = operation_epoch,
        .command_at_milliseconds = command_at,
        .flags = 0,
        .reserved = .{ 0, 0 },
    };
}

fn testConfig(generation: u64) protocol.Config {
    return .{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.Config),
        .generation = generation,
        .maximum_source_count = 4,
        .maximum_metric_count = 4,
        .maximum_rule_count = 8,
        .maximum_requested_count = 4,
        .maximum_observation_count = 8,
        .maximum_gpu_adapter_count = 4,
        .maximum_persistence_source_count = 4,
        .maximum_persistence_rule_count = 8,
        .maximum_persistence_gpu_count = 4,
        .source_index_capacity = 8,
        .metric_index_capacity = 8,
        .rule_index_capacity = 16,
        .gpu_index_capacity = 8,
        .maximum_future_skew_milliseconds = 1_000,
        .resident_byte_budget = 1 << 20,
        .catalog_contract_version = protocol.catalog_contract_version,
        .value_contract_version = protocol.value_contract_version,
        .observation_contract_version = protocol.observation_contract_version,
        .inventory_contract_version = protocol.inventory_contract_version,
        .persistence_contract_version = protocol.persistence_contract_version,
        .flags = 0,
        .maximum_plan_metric_count = 4,
        .maximum_source_mode_count = 4,
        .maximum_source_plan_count = 4,
        .maximum_metric_plan_count = 8,
        .gpu_luid_index_capacity = 8,
        .gpu_key_index_capacity = 8,
        .reserved_u32 = 0,
        .reserved = .{ 0, 0 },
    };
}

fn testSources() [source_count]protocol.SourcePolicyInput {
    return .{
        sourcePolicy(10, .metrics, 1, 0x01),
        sourcePolicy(20, .metrics, 2, 0x02),
        sourcePolicy(30, .gpu_inventory, 3, 0x04),
    };
}

fn sourcePolicy(
    handle: u64,
    role: protocol.SourceRole,
    priority: u32,
    capability: u64,
) protocol.SourcePolicyInput {
    return .{
        .struct_size = @sizeOf(protocol.SourcePolicyInput),
        .flags = 0,
        .source_handle = handle,
        .source_role = @intFromEnum(role),
        .priority = priority,
        .reserved_u32 = 0,
        .capability_mask = capability,
        .semantic_fingerprint = handle * 100 + priority,
        .reserved = .{ 0, 0 },
    };
}

fn testRules() [rule_count]protocol.MetricDefinitionInput {
    return .{
        metricDefinition(
            1_001,
            100,
            10,
            1,
            1,
            .cpu_usage,
            .cpu,
            .float64,
            protocol.MetricFlags.percentage | protocol.MetricFlags.nonnegative,
            @bitCast(@as(f64, 0)),
            @bitCast(@as(f64, 100)),
        ),
        metricDefinition(
            1_002,
            100,
            20,
            1,
            2,
            .cpu_usage,
            .cpu,
            .float64,
            protocol.MetricFlags.percentage | protocol.MetricFlags.nonnegative,
            @bitCast(@as(f64, 0)),
            @bitCast(@as(f64, 100)),
        ),
        metricDefinition(
            2_001,
            200,
            10,
            2,
            1,
            .memory_used,
            .memory,
            .unsigned64,
            protocol.MetricFlags.nonnegative | protocol.MetricFlags.used_value,
            0,
            1 << 40,
        ),
    };
}

fn metricDefinition(
    rule: u64,
    metric: u64,
    source: u64,
    scope: u64,
    priority: u32,
    metric_kind: protocol.MetricKind,
    scope_kind: protocol.ScopeKind,
    value_kind: protocol.ValueKind,
    flags: u32,
    minimum: u64,
    maximum: u64,
) protocol.MetricDefinitionInput {
    return .{
        .struct_size = @sizeOf(protocol.MetricDefinitionInput),
        .flags = flags,
        .rule_handle = rule,
        .metric_handle = metric,
        .source_handle = source,
        .scope_handle = scope,
        .capability_mask = if (source == 20) 0x02 else 0x01,
        .metric_kind = @intFromEnum(metric_kind),
        .scope_kind = @intFromEnum(scope_kind),
        .value_kind = @intFromEnum(value_kind),
        .source_priority = priority,
        .reserved_u32 = 0,
        .minimum_value_bits = minimum,
        .maximum_value_bits = maximum,
        .semantic_fingerprint = rule * 100 + metric,
    };
}

fn replaceCatalog(
    session: *state.Session,
    operation_epoch: u64,
    catalog_generation: u64,
    command_at: u64,
    sources: []const protocol.SourcePolicyInput,
    rules: []const protocol.MetricDefinitionInput,
) ResultCode {
    var actual_metric_count: u32 = 0;
    var previous_metric: u64 = 0;
    for (rules) |rule| {
        if (rule.metric_handle != previous_metric) actual_metric_count += 1;
        previous_metric = rule.metric_handle;
    }
    var inventory_source_count: u32 = 0;
    for (sources) |source| {
        if (source.source_role == @intFromEnum(protocol.SourceRole.gpu_inventory)) inventory_source_count += 1;
    }
    var input = protocol.CatalogReplaceInput{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.CatalogReplaceInput),
        .configuration_generation = session.config.generation,
        .catalog_generation = catalog_generation,
        .operation_epoch = operation_epoch,
        .command_at_milliseconds = command_at,
        .source_count = @intCast(sources.len),
        .rule_count = @intCast(rules.len),
        .metric_count = actual_metric_count,
        .gpu_inventory_source_count = inventory_source_count,
        .source_index_capacity = session.config.source_index_capacity,
        .metric_index_capacity = session.config.metric_index_capacity,
        .rule_index_capacity = session.config.rule_index_capacity,
        .flags = 0,
        .semantic_fingerprint = session_module.catalogFingerprint(sources, rules),
        .reserved = .{ 0, 0 },
    };
    return session_module.replaceCatalog(session, &input, sources, rules);
}

fn planMetric(handle: u64) protocol.PlanMetricInput {
    return .{
        .struct_size = @sizeOf(protocol.PlanMetricInput),
        .flags = 0,
        .metric_handle = handle,
        .reserved = .{ 0, 0 },
    };
}

fn testModes() [source_count]protocol.SourceStateInput {
    return .{
        sourceMode(10),
        sourceMode(20),
        sourceMode(30),
    };
}

fn sourceMode(handle: u64) protocol.SourceStateInput {
    return .{
        .struct_size = @sizeOf(protocol.SourceStateInput),
        .flags = 0,
        .source_handle = handle,
        .capability_generation = 1,
        .availability = @intFromEnum(protocol.SourceAvailability.available),
        .reserved = .{ 0, 0 },
    };
}

fn runPlan(
    session: *state.Session,
    scratch_bytes: []u8,
    operation_epoch: u64,
    plan_epoch: u64,
    command_at: u64,
    flags: u64,
    requested: []const protocol.PlanMetricInput,
    modes: []const protocol.SourceStateInput,
    output: *protocol.PlanOutput,
    source_plans: []protocol.SourcePlanOutput,
    metric_plans: []protocol.MetricPlanOutput,
) ResultCode {
    var input = protocol.PlanInput{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.PlanInput),
        .configuration_generation = session.config.generation,
        .catalog_generation = session.catalog_generation,
        .operation_epoch = operation_epoch,
        .plan_epoch = plan_epoch,
        .command_at_milliseconds = command_at,
        .requested_metric_count = @intCast(requested.len),
        .source_mode_count = @intCast(modes.len),
        .source_plan_capacity = @intCast(source_plans.len),
        .metric_plan_capacity = @intCast(metric_plans.len),
        .flags = flags,
        .reserved = .{ 0, 0 },
    };
    return session_module.plan(
        session,
        &input,
        requested,
        modes,
        output,
        source_plans,
        metric_plans,
        scratch_bytes,
    );
}

fn requestedRule(handle: u64) protocol.RequestedMetricInput {
    return .{
        .struct_size = @sizeOf(protocol.RequestedMetricInput),
        .flags = 0,
        .rule_handle = handle,
        .reserved = .{ 0, 0 },
    };
}

fn unsignedObservation(
    rule: u64,
    metric: u64,
    source: u64,
    scope: u64,
    observed_at: u64,
    value: u64,
) protocol.ObservationInput {
    return .{
        .struct_size = @sizeOf(protocol.ObservationInput),
        .flags = 0,
        .rule_handle = rule,
        .metric_handle = metric,
        .source_handle = source,
        .scope_handle = scope,
        .source_observation_sequence = if (observed_at == 300) 1 else 2,
        .observed_at_milliseconds = observed_at,
        .value_bits = value,
        .capability_mask = 0x01,
        .valid_mask = protocol.ObservationValid.required | protocol.ObservationValid.value,
        .status = @intFromEnum(protocol.ObservationStatus.current),
        .value_kind = @intFromEnum(protocol.ValueKind.unsigned64),
        .quality = 100,
        .reserved_u32 = 0,
        .sample_duration_milliseconds = 0,
    };
}

fn cpuCounter(
    sequence: u64,
    observed_at: u64,
    idle: u64,
    kernel: u64,
    user: u64,
    monotonic: u64,
) protocol.CpuCounterInput {
    return .{
        .struct_size = @sizeOf(protocol.CpuCounterInput),
        .flags = 0,
        .rule_handle = 1_001,
        .metric_handle = 100,
        .source_handle = 10,
        .source_incarnation = 1,
        .source_observation_sequence = sequence,
        .observed_at_milliseconds = observed_at,
        .monotonic_ticks = monotonic,
        .monotonic_ticks_per_second = 1_000,
        .idle_ticks = idle,
        .kernel_ticks = kernel,
        .user_ticks = user,
        .capability_mask = 0x01,
        .valid_mask = protocol.CpuCounterValid.required,
        .status = @intFromEnum(protocol.ObservationStatus.current),
        .counter_contract_version = protocol.cpu_counter_contract_version,
        .reserved = .{ 0, 0 },
    };
}

fn metricCompletion(
    operation_epoch: u64,
    plan_epoch: u64,
    source_handle: u64,
    incarnation: u64,
    sequence: u64,
    captured_at: u64,
    requested_count: u32,
    observation_count: u32,
    cpu_count: u32,
) protocol.CompletionHeader {
    return .{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.CompletionHeader),
        .configuration_generation = 1,
        .catalog_generation = 1,
        .operation_epoch = operation_epoch,
        .plan_epoch = plan_epoch,
        .source_handle = source_handle,
        .source_incarnation = incarnation,
        .source_observation_sequence = sequence,
        .capability_generation = 0,
        .plan_token_fingerprint = 0,
        .command_at_milliseconds = captured_at,
        .captured_at_milliseconds = captured_at,
        .requested_rule_count = requested_count,
        .observation_count = observation_count,
        .gpu_inventory_count = 0,
        .expected_gpu_inventory_count = 0,
        .cpu_counter_count = cpu_count,
        .overflow_count = 0,
        .status = @intFromEnum(protocol.SourceStatus.complete),
        .flags = 0,
        .reserved = .{ 0, 0 },
    };
}

fn gpuCompletion(
    operation_epoch: u64,
    plan_epoch: u64,
    source_handle: u64,
    incarnation: u64,
    sequence: u64,
    captured_at: u64,
    count: u32,
) protocol.CompletionHeader {
    var output = metricCompletion(
        operation_epoch,
        plan_epoch,
        source_handle,
        incarnation,
        sequence,
        captured_at,
        0,
        0,
        0,
    );
    output.gpu_inventory_count = count;
    output.expected_gpu_inventory_count = count;
    return output;
}

fn gpuInput(
    adapter: u64,
    luid_low: u64,
    luid_high: u64,
    stable_key: u64,
    sequence: u64,
    observed_at: u64,
) protocol.GpuInventoryInput {
    return .{
        .struct_size = @sizeOf(protocol.GpuInventoryInput),
        .flags = 0,
        .adapter_handle = adapter,
        .source_handle = 30,
        .source_observation_sequence = sequence,
        .observed_at_milliseconds = observed_at,
        .adapter_luid_low = luid_low,
        .adapter_luid_high = luid_high,
        .stable_key_handle = stable_key,
        .capability_mask = 0x04,
        .topology_fingerprint = adapter * 10,
        .valid_mask = protocol.InventoryValid.required,
        .status = @intFromEnum(protocol.InventoryStatus.current),
        .reserved_u32 = 0,
        .reserved = .{ 0, 0 },
    };
}

fn finalizeInput(
    operation_epoch: u64,
    plan_epoch: u64,
    source_handle: u64,
    command_at: u64,
) protocol.FinalizeInput {
    return .{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.FinalizeInput),
        .configuration_generation = 1,
        .catalog_generation = 1,
        .operation_epoch = operation_epoch,
        .plan_epoch = plan_epoch,
        .source_handle = source_handle,
        .command_at_milliseconds = command_at,
        .flags = 0,
        .reserved = .{ 0, 0 },
    };
}

fn readInput(header: protocol.SnapshotHeader) protocol.ReadInput {
    return .{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.ReadInput),
        .configuration_generation = header.configuration_generation,
        .catalog_generation = header.catalog_generation,
        .catalog_fingerprint = header.catalog_fingerprint,
        .state_revision = header.state_revision,
        .committed_generation = header.committed_generation,
        .flags = 0,
        .reserved = .{ 0, 0 },
    };
}

fn ruleStateFingerprint(input: *const protocol.RuleStateOutput) u64 {
    var copy = input.*;
    copy.state_fingerprint = 0;
    const value = std.hash.Wyhash.hash(
        0x726d_6d65_7472_7275,
        std.mem.asBytes(&copy),
    );
    return if (value == 0) 1 else value;
}

fn sourceStateFingerprint(input: *const protocol.SourcePersistenceOutput) u64 {
    var copy = input.*;
    copy.state_fingerprint = 0;
    const value = std.hash.Wyhash.hash(
        0x726d_6d65_7472_7073,
        std.mem.asBytes(&copy),
    );
    return if (value == 0) 1 else value;
}

fn gpuOutputFingerprint(input: *const protocol.GpuInventoryOutput) u64 {
    const semantic_fields = [_]u64{
        input.flags,
        input.adapter_handle,
        input.source_handle,
        input.adapter_luid_low,
        input.adapter_luid_high,
        input.stable_key_handle,
        input.capability_mask,
        input.topology_fingerprint,
        input.valid_mask,
        input.status,
    };
    const value = std.hash.Wyhash.hash(
        0x726d_6770_756f_7574,
        std.mem.asBytes(&semantic_fields),
    );
    return if (value == 0) 1 else value;
}

fn persistenceChecksum(
    header: *const protocol.PersistenceHeader,
    sources: []const protocol.SourcePersistenceOutput,
    rules: []const protocol.RuleStateOutput,
    gpu_inventory: []const protocol.GpuInventoryOutput,
) u64 {
    var copy = header.*;
    copy.checksum = 0;
    var hash = std.hash.Wyhash.init(0x726d_6d65_7472_7063);
    hash.update(std.mem.asBytes(&copy));
    hash.update(std.mem.sliceAsBytes(sources));
    hash.update(std.mem.sliceAsBytes(rules));
    hash.update(std.mem.sliceAsBytes(gpu_inventory));
    const value = hash.final();
    return if (value == 0) 1 else value;
}

fn persistenceInput(operation_epoch: u64, command_at: u64) protocol.PersistenceInput {
    return .{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.PersistenceInput),
        .configuration_generation = 1,
        .catalog_generation = 1,
        .operation_epoch = operation_epoch,
        .command_at_milliseconds = command_at,
        .flags = 0,
        .reserved = .{ 0, 0 },
    };
}

test "integer timed counters commit original rate and microsecond percentage atomically" {
    inline for (.{ protocol.MetricKind.timed_counter_rate, protocol.MetricKind.timed_microsecond_counter_percentage }) |kind| {
        var config = testConfig(1);
        const session_scratch = try testScratch(&config);
        defer std.testing.allocator.free(session_scratch);
        const session = try state.Session.create(&config);
        defer session.destroy();
        var sources = testSources();
        var rules = testRules();
        for (rules[0..2]) |*rule| {
            rule.metric_kind = @intFromEnum(kind);
            rule.flags = protocol.MetricFlags.nonnegative;
            rule.minimum_value_bits = @bitCast(@as(f64, 0));
            rule.maximum_value_bits = @bitCast(std.math.floatMax(f64));
        }
        try std.testing.expectEqual(ResultCode.ok, replaceCatalog(session, 1, 1, 100, &sources, &rules));
        const requested = [_]protocol.PlanMetricInput{planMetric(100)};
        const requested_rules = [_]protocol.RequestedMetricInput{requestedRule(1_001)};
        const modes = testModes();
        for (0..2) |index| {
            const at: u64 = 200 + index * 1000;
            const epoch: u64 = 2 + index * 2;
            var output = std.mem.zeroes(protocol.PlanOutput);
            var source_plans = std.mem.zeroes([4]protocol.SourcePlanOutput);
            var metric_plans = std.mem.zeroes([4]protocol.MetricPlanOutput);
            try std.testing.expectEqual(ResultCode.ok, runPlan(session, session_scratch, epoch, index + 1, at, 0, &requested, &modes, &output, &source_plans, &metric_plans));
            var header = metricCompletion(epoch + 1, index + 1, 10, 1, index + 1, at, 1, 1, 0);
            header.capability_generation = source_plans[0].capability_generation;
            header.plan_token_fingerprint = source_plans[0].plan_token_fingerprint;
            try std.testing.expectEqual(ResultCode.ok, session_module.beginCompletion(session, &header, session_scratch));
            try std.testing.expectEqual(ResultCode.ok, session_module.submitRequested(session, &requested_rules));
            var sample = unsignedObservation(1_001, 100, 10, rules[0].scope_handle, at, 0);
            sample.flags = protocol.ObservationFlags.timed_counter;
            sample.value_kind = @intFromEnum(protocol.ValueKind.float64);
            sample.source_observation_sequence = index + 1;
            sample.value_bits = (@as(u64, 1) << 60) + @as(u64, @intCast(index)) * 500_000;
            sample.value_high_bits = 100 + @as(u64, @intCast(index)) * 1_000_000;
            sample.counter_ticks_per_second = 1_000_000;
            try std.testing.expectEqual(ResultCode.ok, session_module.submitObservations(session, &.{sample}));
            var finalize = finalizeInput(epoch + 1, index + 1, 10, at);
            const rule_index = session.active.findRule(1_001).?;
            if (index == 1) {
                finalize.catalog_generation += 1;
                try std.testing.expectEqual(ResultCode.abi_mismatch, session_module.finalizeCompletion(session, &finalize));
                try std.testing.expectEqual(@as(u64, 1) << 60, session.active.rules[rule_index].counter_baseline.primary);
                finalize.catalog_generation -= 1;
            }
            try std.testing.expectEqual(ResultCode.ok, session_module.finalizeCompletion(session, &finalize));
            const record = session.active.rules[rule_index];
            if (index == 0) {
                try std.testing.expectEqual(protocol.MetricStatus.unavailable, record.status);
            } else {
                try std.testing.expectEqual(protocol.MetricStatus.current, record.status);
                try std.testing.expectEqual(@as(f64, if (kind == .timed_counter_rate) 500_000 else 50), @as(f64, @bitCast(record.value_bits)));
                try std.testing.expectEqual(@as(u64, 1000), record.sample_duration_milliseconds);
            }
        }
    }
}

fn testScratch(config: *const protocol.Config) ![]align(8) u8 {
    const layout = try scratch_module.Layout.init(config, config.maximum_rule_count, config.maximum_metric_count);
    return std.testing.allocator.alignedAlloc(u8, .@"8", layout.byte_count);
}

test "external metric region is the real completion storage and failures retain its address" {
    const native_abi = @import("abi.zig");
    var config = testConfig(1);
    const layout = try scratch_module.Layout.init(&config, rule_count, metric_count);
    const first = try std.testing.allocator.alignedAlloc(u8, .@"8", layout.byte_count + 8);
    defer std.testing.allocator.free(first);
    const second = try testScratch(&config);
    defer std.testing.allocator.free(second);
    @memset(first, 0xA5);
    @memset(second, 0xCC);
    const session = try state.Session.create(&config);
    var alive = true;
    defer if (alive) session.destroy();
    try std.testing.expectEqual(session.requiredByteCount(), session.residentByteCount() + session.scratch_layout.byte_count);
    try std.testing.expectEqual(@as(u32, 8), session.capacity().scratch_alignment);
    try std.testing.expectEqual(@as(usize, 0), session.observations.len);
    var sources = testSources();
    var rules = testRules();
    try std.testing.expectEqual(ResultCode.ok, replaceCatalog(session, 1, 1, 100, &sources, &rules));
    var source_plans = std.mem.zeroes([4]protocol.SourcePlanOutput);
    var metric_plans = std.mem.zeroes([4]protocol.MetricPlanOutput);
    var output = std.mem.zeroes(protocol.PlanOutput);
    const requested = [_]protocol.PlanMetricInput{planMetric(200)};
    const modes = testModes();
    try std.testing.expectEqual(ResultCode.ok, runPlan(session, first, 2, 1, 200, 0, &requested, &modes, &output, &source_plans, &metric_plans));
    try std.testing.expectEqual(@as(usize, 0), session.observations.len);
    const handles = layout.column(u64, first, layout.plan_handles, config.maximum_plan_metric_count);
    try std.testing.expectEqual(@as(u64, 2_001), handles[0]);
    var header = metricCompletion(3, 1, 10, 1, 1, 300, 1, 1, 0);
    header.capability_generation = source_plans[0].capability_generation;
    header.plan_token_fingerprint = source_plans[0].plan_token_fingerprint;
    try std.testing.expectEqual(ResultCode.buffer_too_small, session_module.beginCompletion(session, &header, second[0 .. layout.byte_count - 1]));
    try std.testing.expectEqual(ResultCode.invalid_argument, session_module.beginCompletion(session, &header, first[1 .. layout.byte_count + 1]));
    try std.testing.expectEqual(@as(usize, 0), session.observations.len);
    try std.testing.expectEqual(@intFromEnum(ResultCode.invalid_argument), native_abi.rm_metric_snapshot_begin_completion(@ptrCast(session), &header, null, second.len));
    try std.testing.expectEqual(@intFromEnum(ResultCode.invalid_argument), native_abi.rm_metric_snapshot_begin_completion(@ptrCast(session), &header, second.ptr, std.math.maxInt(u64)));
    try std.testing.expectEqual(@intFromEnum(ResultCode.ok), native_abi.rm_metric_snapshot_begin_completion(@ptrCast(session), &header, second.ptr, second.len));
    try std.testing.expectEqual(@intFromPtr(second.ptr) + layout.observations, @intFromPtr(session.observations.ptr));
    try std.testing.expectEqual(@intFromPtr(second.ptr) + layout.requested, @intFromPtr(session.requested_rule_indices.ptr));
    try std.testing.expectEqual(ResultCode.unavailable, session_module.beginCompletion(session, &header, first));
    try std.testing.expectEqual(@intFromPtr(second.ptr), @intFromPtr(session.observations.ptr));
    try std.testing.expectEqual(ResultCode.ok, session_module.submitRequested(session, &.{requestedRule(2_001)}));
    try std.testing.expectEqual(ResultCode.ok, session_module.submitObservations(session, &.{unsignedObservation(2_001, 200, 10, 2, 300, 4096)}));
    const observations = layout.column(protocol.ObservationInput, second, layout.observations, config.maximum_observation_count);
    try std.testing.expectEqual(@as(u64, 4096), observations[0].value_bits);
    var finish = finalizeInput(3, 1, 10, 300);
    finish.catalog_generation += 1;
    try std.testing.expectEqual(ResultCode.abi_mismatch, session_module.finalizeCompletion(session, &finish));
    try std.testing.expectEqual(ResultCode.abi_mismatch, session_module.abortCompletion(session, &finish));
    try std.testing.expectEqual(@intFromPtr(second.ptr), @intFromPtr(session.observations.ptr));
    try std.testing.expectEqual(ResultCode.unavailable, runPlan(session, first, 4, 2, 400, 0, &requested, &modes, &output, &source_plans, &metric_plans));
    finish.catalog_generation -= 1;
    try std.testing.expectEqual(ResultCode.ok, session_module.finalizeCompletion(session, &finish));
    try std.testing.expectEqual(@as(usize, 0), session.observations.len);
    try std.testing.expectEqual(@as(usize, 0), session.requested_rule_indices.len);
    try std.testing.expectEqual(@as(u64, 4096), session.active.rules[session.active.findRule(2_001).?].value_bits);
    @memset(second, 0xA5);
    const reset = controlInput(4, 400);
    try std.testing.expectEqual(ResultCode.ok, session_module.reset(session, &reset));
    for (second) |value| try std.testing.expectEqual(@as(u8, 0xA5), value);
    try std.testing.expectEqual(@as(u64, 0), session.catalog_generation);
    try std.testing.expectEqual(@as(u32, 0), session.active.source_count);
    try std.testing.expectEqual(ResultCode.ok, replaceCatalog(session, 5, 1, 500, &sources, &rules));
    try std.testing.expectEqual(ResultCode.ok, runPlan(session, first, 6, 2, 600, 0, &requested, &modes, &output, &source_plans, &metric_plans));
    header = metricCompletion(7, 2, 10, 1, 1, 700, 1, 1, 0);
    header.capability_generation = source_plans[0].capability_generation;
    header.plan_token_fingerprint = source_plans[0].plan_token_fingerprint;
    try std.testing.expectEqual(ResultCode.ok, session_module.beginCompletion(session, &header, first));
    const before_destroy = try std.testing.allocator.dupe(u8, first);
    defer std.testing.allocator.free(before_destroy);
    session.destroy();
    alive = false;
    try std.testing.expectEqualSlices(u8, before_destroy, first);
}

test "successful abort detaches every external completion slice" {
    var config = testConfig(1);
    const bytes = try testScratch(&config);
    defer std.testing.allocator.free(bytes);
    const session = try state.Session.create(&config);
    defer session.destroy();
    var sources = testSources();
    var rules = testRules();
    try std.testing.expectEqual(ResultCode.ok, replaceCatalog(session, 1, 1, 100, &sources, &rules));
    var source_plans = std.mem.zeroes([4]protocol.SourcePlanOutput);
    var metric_plans = std.mem.zeroes([4]protocol.MetricPlanOutput);
    var output = std.mem.zeroes(protocol.PlanOutput);
    const requested = [_]protocol.PlanMetricInput{planMetric(200)};
    const modes = testModes();
    try std.testing.expectEqual(ResultCode.ok, runPlan(session, bytes, 2, 1, 200, 0, &requested, &modes, &output, &source_plans, &metric_plans));
    var header = metricCompletion(3, 1, 10, 1, 1, 300, 1, 1, 0);
    header.capability_generation = source_plans[0].capability_generation;
    header.plan_token_fingerprint = source_plans[0].plan_token_fingerprint;
    try std.testing.expectEqual(ResultCode.ok, session_module.beginCompletion(session, &header, bytes));
    const finish = finalizeInput(3, 1, 10, 300);
    try std.testing.expectEqual(ResultCode.ok, session_module.abortCompletion(session, &finish));
    try std.testing.expectEqual(@as(usize, 0), session.observations.len);
    try std.testing.expectEqual(@as(usize, 0), session.requested_rule_indices.len);
    try std.testing.expectEqual(@as(usize, 0), session.requested_rule_flags.len);
    try std.testing.expectEqual(@as(usize, 0), session.observation_slot_by_rule.len);
}

test "Plan rejects real aliases before clearing any actual capacity or committing state" {
    const native_abi = @import("abi.zig");
    var config = testConfig(1);
    const layout = try scratch_module.Layout.init(&config, rule_count, metric_count);
    const allocation = try std.testing.allocator.alignedAlloc(u8, .@"8", layout.byte_count + 4096);
    defer std.testing.allocator.free(allocation);
    // Leave real storage before and after the required prefix to exercise exact extents.
    const region = allocation[2048 .. 2048 + layout.byte_count];
    const session = try state.Session.create(&config);
    defer session.destroy();
    var sources = testSources();
    var rules = testRules();
    try std.testing.expectEqual(ResultCode.ok, replaceCatalog(session, 1, 1, 100, &sources, &rules));
    const before_modes = try std.testing.allocator.dupe(state.SourceModeState, session.source_modes);
    defer std.testing.allocator.free(before_modes);
    const revision = session.state_revision;
    const phase = session.phase;
    const operation = session.last_operation_epoch;
    const Alias = enum { input, requested, modes, output, sources, metrics, unused_output_tail, input_output };
    for (std.enums.values(Alias)) |alias| {
        @memset(allocation, 0xA5);
        var input = protocol.PlanInput{
            .abi_version = protocol.abi_version,
            .struct_size = @sizeOf(protocol.PlanInput),
            .configuration_generation = config.generation,
            .catalog_generation = session.catalog_generation,
            .operation_epoch = 2,
            .plan_epoch = 1,
            .command_at_milliseconds = 200,
            .requested_metric_count = 1,
            .source_mode_count = source_count,
            .source_plan_capacity = 4,
            .metric_plan_capacity = 4,
            .flags = 0,
            .reserved = .{ 0, 0 },
        };
        var requested = [_]protocol.PlanMetricInput{planMetric(200)};
        var modes = testModes();
        var output = std.mem.zeroes(protocol.PlanOutput);
        var source_plans = std.mem.zeroes([4]protocol.SourcePlanOutput);
        var metric_plans = std.mem.zeroes([4]protocol.MetricPlanOutput);
        var input_pointer: *const protocol.PlanInput = &input;
        var requested_slice: []const protocol.PlanMetricInput = &requested;
        var modes_slice: []const protocol.SourceStateInput = &modes;
        var output_pointer: *protocol.PlanOutput = &output;
        var source_slice: []protocol.SourcePlanOutput = &source_plans;
        var metric_slice: []protocol.MetricPlanOutput = &metric_plans;
        const inside = region.ptr + layout.plan_handles;
        switch (alias) {
            .input => {
                const value: *protocol.PlanInput = @ptrCast(@alignCast(inside));
                value.* = input;
                input_pointer = value;
            },
            .requested => {
                const value: *protocol.PlanMetricInput = @ptrCast(@alignCast(inside));
                value.* = requested[0];
                requested_slice = @as([*]const protocol.PlanMetricInput, @ptrCast(value))[0..1];
            },
            .modes => {
                const value: [*]protocol.SourceStateInput = @ptrCast(@alignCast(inside));
                @memcpy(value[0..source_count], &modes);
                modes_slice = value[0..source_count];
            },
            .output => output_pointer = @ptrCast(@alignCast(inside)),
            .sources => source_slice = @as([*]protocol.SourcePlanOutput, @ptrCast(@alignCast(inside)))[0..4],
            .metrics => metric_slice = @as([*]protocol.MetricPlanOutput, @ptrCast(@alignCast(inside)))[0..4],
            .unused_output_tail => {
                // Only the fourth source output (unused by the three-source catalogue)
                // overlaps scratch. Plan still clears that full capacity extent.
                const start = allocation.ptr + 2048 - source_count * @sizeOf(protocol.SourcePlanOutput);
                source_slice = @as([*]protocol.SourcePlanOutput, @ptrCast(@alignCast(start)))[0..4];
            },
            .input_output => {
                // Pairwise alias outside scratch must also be rejected before input is overwritten.
                const value: *protocol.PlanInput = @ptrCast(@alignCast(region.ptr + layout.byte_count + 512));
                value.* = input;
                input_pointer = value;
                output_pointer = @ptrCast(value);
            },
        }
        const before = try std.testing.allocator.dupe(u8, allocation);
        defer std.testing.allocator.free(before);
        try std.testing.expectEqual(@intFromEnum(ResultCode.invalid_argument), native_abi.rm_metric_snapshot_plan(
            @ptrCast(session),
            input_pointer,
            requested_slice.ptr,
            @intCast(requested_slice.len),
            modes_slice.ptr,
            @intCast(modes_slice.len),
            output_pointer,
            source_slice.ptr,
            @intCast(source_slice.len),
            metric_slice.ptr,
            @intCast(metric_slice.len),
            region.ptr,
            region.len,
        ));
        try std.testing.expectEqualSlices(u8, before, allocation);
        for (before_modes, session.source_modes) |before_mode, after_mode| try std.testing.expectEqualDeep(before_mode, after_mode);
        try std.testing.expectEqual(revision, session.state_revision);
        try std.testing.expectEqual(phase, session.phase);
        try std.testing.expectEqual(operation, session.last_operation_epoch);
        try std.testing.expectEqual(@as(u64, 0), session.last_plan_epoch);
        try std.testing.expectEqualDeep(std.mem.zeroes(protocol.PlanOutput), output);
        try std.testing.expectEqualDeep(std.mem.zeroes([4]protocol.SourcePlanOutput), source_plans);
        try std.testing.expectEqualDeep(std.mem.zeroes([4]protocol.MetricPlanOutput), metric_plans);
    }
    var source_plans = std.mem.zeroes([4]protocol.SourcePlanOutput);
    var metric_plans = std.mem.zeroes([4]protocol.MetricPlanOutput);
    const requested = [_]protocol.PlanMetricInput{planMetric(200)};
    const modes = testModes();
    // Extra caller tail is not owned by Native. Output exactly adjacent to the
    // required prefix is accepted even though the supplied allocation is larger.
    const tail_output: *protocol.PlanOutput = @ptrCast(@alignCast(region.ptr + layout.byte_count));
    try std.testing.expectEqual(ResultCode.ok, runPlan(session, allocation[2048..], 2, 1, 200, 0, &requested, &modes, tail_output, &source_plans, &metric_plans));
    try std.testing.expectEqual(@as(u64, 1), tail_output.plan_epoch);
}

test "Begin rejects valid header in real scratch without binding or writing then accepts adjacent tail" {
    const native_abi = @import("abi.zig");
    var config = testConfig(1);
    const layout = try scratch_module.Layout.init(&config, rule_count, metric_count);
    const region = try std.testing.allocator.alignedAlloc(u8, .@"8", layout.byte_count + @sizeOf(protocol.CompletionHeader));
    defer std.testing.allocator.free(region);
    const session = try state.Session.create(&config);
    defer session.destroy();
    var sources = testSources();
    var rules = testRules();
    try std.testing.expectEqual(ResultCode.ok, replaceCatalog(session, 1, 1, 100, &sources, &rules));
    var source_plans = std.mem.zeroes([4]protocol.SourcePlanOutput);
    var metric_plans = std.mem.zeroes([4]protocol.MetricPlanOutput);
    var output = std.mem.zeroes(protocol.PlanOutput);
    const requested = [_]protocol.PlanMetricInput{planMetric(200)};
    const modes = testModes();
    try std.testing.expectEqual(ResultCode.ok, runPlan(session, region, 2, 1, 200, 0, &requested, &modes, &output, &source_plans, &metric_plans));
    var header = metricCompletion(3, 1, 10, 1, 1, 300, 1, 1, 0);
    header.capability_generation = source_plans[0].capability_generation;
    header.plan_token_fingerprint = source_plans[0].plan_token_fingerprint;
    const revision = session.state_revision;
    const phase = session.phase;
    const alias: *protocol.CompletionHeader = @ptrCast(@alignCast(region.ptr));
    alias.* = header;
    const before = try std.testing.allocator.dupe(u8, region);
    defer std.testing.allocator.free(before);
    try std.testing.expectEqual(@intFromEnum(ResultCode.invalid_argument), native_abi.rm_metric_snapshot_begin_completion(@ptrCast(session), alias, region.ptr, region.len));
    try std.testing.expectEqualSlices(u8, before, region);
    try std.testing.expectEqual(revision, session.state_revision);
    try std.testing.expectEqual(phase, session.phase);
    try std.testing.expectEqual(@as(usize, 0), session.observations.len);
    try std.testing.expectEqual(@as(usize, 0), session.requested_rule_indices.len);
    try std.testing.expectEqual(@as(usize, 0), session.requested_rule_flags.len);
    try std.testing.expectEqual(@as(usize, 0), session.observation_slot_by_rule.len);
    const tail: *protocol.CompletionHeader = @ptrCast(@alignCast(region.ptr + layout.byte_count));
    tail.* = header;
    try std.testing.expectEqual(@intFromEnum(ResultCode.ok), native_abi.rm_metric_snapshot_begin_completion(@ptrCast(session), tail, region.ptr, region.len));
    try std.testing.expectEqualDeep(header, session.completion);
    try std.testing.expectEqualDeep(header, tail.*);
    const finish = finalizeInput(3, 1, 10, 300);
    try std.testing.expectEqual(ResultCode.ok, session_module.abortCompletion(session, &finish));
}
