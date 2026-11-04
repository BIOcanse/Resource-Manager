const std = @import("std");
const p = @import("protocol.zig");
const st = @import("state.zig");
const sdk = @import("sdk_dispatch.zig");
const t = std.testing;
const binding = p.TableBinding{ .region = null, .byte_length = 0, .rows_source = 0, .value_source = 1, .health_source = 2 };
fn owner(rows: u32, sources: u32, raw: p.Round) st.Owner {
    return .{ .config = .{ .source_capacity = sources, .rule_capacity = rows, .metric_capacity = rows, .max_value_bytes = 512, .active_limit = 1 << 30, .work_limit = 1 << 30 }, .sizes = .{ .active = @sizeOf(st.Owner), .work = 1 }, .active_length = @sizeOf(st.Owner), .sources = &.{}, .bindings = &.{}, .rules = &.{}, .metrics = &.{}, .payload_heap = &.{}, .source_count = sources, .rule_count = rows, .metric_count = rows, .result = binding, .results = .{ binding, binding }, .result_count = 0, .catalog_generation = raw.catalog_generation, .work = .{ .raw = raw } };
}
test "SDK grouping actual source order, static exact keys, fields, aliases and excluded synthetic row" {
    var source_ids = [_]u64{ 11, 11, 11, 11, 22, 22 };
    var rules = [_]u32{ 0, 1, 2, 3, 4, 5 };
    var fields = [_]u32{ 3, 1, 3, 2, 1, 1 };
    var scopes = [_]u32{ p.none, p.none, p.none, p.none, 8, 9 };
    var display = [_]u32{ p.none, p.none, p.none, p.none, 8, 9 };
    var refs = [_]sdk.Ref{ .{ .offset = 1, .length = 1 }, .{ .length = 1 }, .{ .offset = 1, .length = 1 }, .{ .offset = 1, .length = 1 }, .{ .length = 1 }, .{ .length = 1 } };
    const raw = p.Round{ .token = 9, .catalog_generation = 7, .count = 6, .observations = .{ .capacity = 6, .rule_indices = &rules }, .queries = .{ .capacity = 6, .source_ids = &source_ids } };
    var cols = sdk.Columns{ .count = 6, .field_codes = &fields, .scope_indices = &scopes, .display_indices = &display, .device_keys = &refs, .text = .{ .bytes = "ab", .length = 2 } };
    var o = owner(6, 3, raw);
    const ids = [_]u64{ 22, 11, 33 };
    const excluded = [_]u32{4};
    const size = try sdk.requiredSize(6, 3);
    const bytes = try t.allocator.alignedAlloc(u8, .@"8", @intCast(size));
    defer t.allocator.free(bytes);
    var out: sdk.Dispatch = .{};
    var failure: p.Failure = .{};
    try t.expectEqual(@as(u32, 0), sdk.rm_metric_sdk_dispatch_build(&o, &raw, &cols, &ids, 3, &excluded, 1, bytes.ptr, size, &out, &failure));
    try t.expectEqual(@as(u32, 6), out.row_count);
    try t.expectEqual(@as(u32, 5), out.sdk_row_count);
    try t.expectEqual(@as(u32, 3), out.group_count);
    try t.expectEqualSlices(u64, &ids, out.source_ids.?[0..3]);
    try t.expectEqualSlices(u32, &.{ 0, 1, 3, 3 }, out.source_group_offsets.?[0..4]);
    try t.expectEqual(p.none, out.raw_row_groups.?[4]);
    try t.expectEqual(@as(u32, 4), out.group_field_offsets.?[3]);
    try t.expectEqualSlices(u32, &.{ 1, 1, 2, 3 }, out.field_codes.?[0..4]);
    try t.expectEqual(out.raw_row_groups.?[0], out.raw_row_groups.?[2]);
    try t.expectEqual(@as(i32, -1), out.result_ordinals.?[0]);
    try t.expectEqual(@as(u64, 9), out.token);
    try t.expectEqual(@as(u64, 7), out.catalog_generation);
    // Invalid calls leave the output descriptor and every input unchanged.
    out.token = 99;
    cols.reserved = 1;
    try t.expectEqual(@intFromEnum(p.Code.invalid_contract), sdk.rm_metric_sdk_dispatch_build(&o, &raw, &cols, &ids, 3, &excluded, 1, bytes.ptr, size, &out, &failure));
    try t.expectEqual(@as(u64, 99), out.token);
    cols.reserved = 0;
    const duplicate = [_]u32{ 4, 4 };
    try t.expectEqual(@intFromEnum(p.Code.invalid_contract), sdk.rm_metric_sdk_dispatch_build(&o, &raw, &cols, &ids, 3, &duplicate, 2, bytes.ptr, size, &out, &failure));
    const duplicate_sources = [_]u64{ 22, 22, 33 };
    try t.expectEqual(@intFromEnum(p.Code.invalid_contract), sdk.rm_metric_sdk_dispatch_build(&o, &raw, &cols, &duplicate_sources, 3, null, 0, bytes.ptr, size, &out, &failure));
    try t.expectEqual(@intFromEnum(p.Code.workspace_unavailable), sdk.rm_metric_sdk_dispatch_build(&o, &raw, &cols, &ids, 3, null, 0, bytes.ptr, size - 1, &out, &failure));
    try t.expectEqual(p.Family.work, failure.family);
    try t.expectEqual(size, failure.required);
    try t.expectEqual(@intFromEnum(p.Code.invalid_contract), sdk.rm_metric_sdk_dispatch_build(&o, &raw, &cols, &ids, 3, null, 0, bytes.ptr + 1, size - 1, &out, &failure));
    o.config.work_limit = size - 1;
    try t.expectEqual(@intFromEnum(p.Code.capacity_exceeded), sdk.rm_metric_sdk_dispatch_build(&o, &raw, &cols, &ids, 3, null, 0, bytes.ptr, size, &out, &failure));
    o.config.work_limit = 1 << 30;
    o.config.rule_capacity = 5;
    try t.expectEqual(@intFromEnum(p.Code.capacity_exceeded), sdk.rm_metric_sdk_dispatch_build(&o, &raw, &cols, &ids, 3, null, 0, bytes.ptr, size, &out, &failure));
    try t.expectEqual(p.Family.rules, failure.family);
    o.config.rule_capacity = 6;
    o.config.source_capacity = 2;
    try t.expectEqual(@intFromEnum(p.Code.capacity_exceeded), sdk.rm_metric_sdk_dispatch_build(&o, &raw, &cols, &ids, 3, null, 0, bytes.ptr, size, &out, &failure));
    try t.expectEqual(p.Family.sources, failure.family);
    o.config.source_capacity = 3;
    const saved_fields = fields;
    cols.field_codes = @ptrCast(bytes.ptr);
    try t.expectEqual(@intFromEnum(p.Code.invalid_contract), sdk.rm_metric_sdk_dispatch_build(&o, &raw, &cols, &ids, 3, null, 0, bytes.ptr, size, &out, &failure));
    cols.field_codes = &fields;
    o.work.region = bytes.ptr;
    o.work.length = bytes.len;
    try t.expectEqual(@intFromEnum(p.Code.invalid_contract), sdk.rm_metric_sdk_dispatch_build(&o, &raw, &cols, &ids, 3, null, 0, bytes.ptr, size, &out, &failure));
    o.work.region = null;
    o.work.length = 0;
    o.result_count = 1;
    o.results[0].region = bytes.ptr;
    o.results[0].byte_length = size;
    try t.expectEqual(@intFromEnum(p.Code.invalid_contract), sdk.rm_metric_sdk_dispatch_build(&o, &raw, &cols, &ids, 3, null, 0, bytes.ptr, size, &out, &failure));
    o.result_count = 0;
    o.results[0] = binding;
    try t.expectEqual(@intFromEnum(p.Code.invalid_contract), sdk.rm_metric_sdk_dispatch_build(&o, &raw, &cols, &ids, 3, null, 0, @ptrCast(&o), size, &out, &failure));
    try t.expectEqual(st.Owner.live_magic, o.magic);
    try t.expectEqual(@intFromEnum(p.Code.invalid_contract), sdk.rm_metric_sdk_dispatch_build(&o, &raw, &cols, &ids, 3, null, 0, bytes.ptr, size, @ptrCast(&source_ids), &failure));
    try t.expectEqualSlices(u32, &saved_fields, &fields);
    try t.expectEqualSlices(u64, &.{ 11, 11, 11, 11, 22, 22 }, &source_ids);
}
test "synthetic-only rows need no SDK source event or fake acquisition" {
    var rules = [_]u32{0};
    const source_ids = [_]u64{11};
    const fields = [_]u32{@import("field_codes.zig").npu_device_engine_busy_percent};
    const scopes = [_]u32{0};
    const refs = [_]sdk.Ref{.{ .length = 1 }};
    const raw = p.Round{ .token = 9, .catalog_generation = 7, .count = 1, .observations = .{ .capacity = 1, .rule_indices = &rules }, .queries = .{ .capacity = 1, .source_ids = &source_ids } };
    const cols = sdk.Columns{ .count = 1, .field_codes = &fields, .scope_indices = &scopes, .display_indices = &scopes, .device_keys = &refs, .text = .{ .bytes = "x", .length = 1 } };
    const o = owner(1, 1, raw);
    const size = try sdk.requiredSize(1, 0);
    const bytes = try t.allocator.alignedAlloc(u8, .@"8", @intCast(size));
    defer t.allocator.free(bytes);
    var out: sdk.Dispatch = .{};
    var failure: p.Failure = .{};
    const excluded = [_]u32{0};
    try t.expectEqual(@as(u32, 0), sdk.rm_metric_sdk_dispatch_build(&o, &raw, &cols, null, 0, &excluded, 1, bytes.ptr, size, &out, &failure));
    try t.expectEqual(@as(u32, 0), out.source_count);
    try t.expectEqual(@as(u32, 0), out.sdk_row_count);
    try t.expectEqual(@as(u32, 0), out.group_count);
    try t.expectEqual(p.none, out.raw_row_groups.?[0]);
    // A real non-excluded row still requires its declared acquisition event.
    try t.expectEqual(@intFromEnum(p.Code.invalid_contract), sdk.rm_metric_sdk_dispatch_build(&o, &raw, &cols, null, 0, null, 0, bytes.ptr, size, &out, &failure));
}

test "SDK grouping 257 and 5017 rows and widened sizing use caller sidecar only" {
    inline for (.{ 257, 5017 }) |n| {
        const a = t.allocator;
        const ids = try a.alloc(u64, n);
        defer a.free(ids);
        const indices = try a.alloc(u32, n);
        defer a.free(indices);
        const fields = try a.alloc(u32, n);
        defer a.free(fields);
        const scopes = try a.alloc(u32, n);
        defer a.free(scopes);
        const displays = try a.alloc(u32, n);
        defer a.free(displays);
        const refs = try a.alloc(sdk.Ref, n);
        defer a.free(refs);
        for (0..n) |i| {
            ids[i] = 1;
            indices[i] = @intCast(i);
            fields[i] = 1;
            scopes[i] = @intCast(n - 1 - i);
            displays[i] = scopes[i];
            refs[i] = .{ .length = 1 };
        }
        const raw = p.Round{ .token = 5, .catalog_generation = 2, .count = n, .observations = .{ .capacity = n, .rule_indices = indices.ptr }, .queries = .{ .capacity = n, .source_ids = ids.ptr } };
        const cols = sdk.Columns{ .count = n, .field_codes = fields.ptr, .scope_indices = scopes.ptr, .display_indices = displays.ptr, .device_keys = refs.ptr, .text = .{ .bytes = "x", .length = 1 } };
        const o = owner(n, 1, raw);
        const sources = [_]u64{1};
        const size = try sdk.requiredSize(n, 1);
        const work = try a.alignedAlloc(u8, .@"8", @intCast(size));
        defer a.free(work);
        var out: sdk.Dispatch = .{};
        var failure: p.Failure = .{};
        try t.expectEqual(@as(u32, 0), sdk.rm_metric_sdk_dispatch_build(&o, &raw, &cols, &sources, 1, null, 0, work.ptr, size, &out, &failure));
        try t.expectEqual(@as(u32, n), out.group_count);
        try t.expectEqual(@as(u32, n), out.sdk_row_count);
        for (0..n) |i| {
            try t.expectEqual(@as(u32, @intCast(i)), out.group_scope_indices.?[i]);
            try t.expectEqual(@as(u32, @intCast(n - 1 - i)), out.row_indices.?[i]);
        }
    }
    var huge: u64 = 0;
    var failure: p.Failure = .{};
    try t.expectEqual(@as(u32, 0), sdk.rm_metric_sdk_dispatch_required_size(std.math.maxInt(u32), std.math.maxInt(u32), &huge, &failure));
    try t.expect(huge > std.math.maxInt(u32));
}
