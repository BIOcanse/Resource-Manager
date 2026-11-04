const std = @import("std");
const gpu = @import("gpu_owner.zig");
const pdh = @import("../../../telemetry/pdh_collector.zig");
fn engine(pid: u32, index: u32, value: f64) pdh.GpuEngineRow {
    return .{ .adapter_luid = 23, .process_id = pid, .physical_index = 0, .engine_index = index, .engine_class = 0, .usage_percent = value };
}
fn window(pid: u32, index: u32) pdh.GpuEngineWindow {
    return .{ .adapter_luid = 23, .process_id = pid, .physical_index = 0, .engine_index = index,
        .begin_filetime = 1100, .end_filetime = 1200, .begin_counter = 10, .end_counter = 20, .counter_frequency = 100 };
}
fn input(engines: []const pdh.GpuEngineRow, windows: []const pdh.GpuEngineWindow, memory: []const pdh.GpuMemoryRow, ids: []const gpu.Identity) gpu.Input {
    return .{ .engines = engines.ptr, .engine_count = engines.len, .windows = windows.ptr, .window_count = windows.len,
        .memory = memory.ptr, .memory_count = memory.len, .identities = ids.ptr, .identity_count = ids.len, .flags = 31 };
}
fn find(rows: []const gpu.Row, kind: u32, pid: u32) ?f64 {
    for (rows) |row| if (row.kind == kind and row.pid == pid) return row.value;
    return null;
}
test "same frame scheduling uses each process busiest engine; breakdown uses device busiest engine" {
    const engines = [_]pdh.GpuEngineRow{ engine(41, 0, 30), engine(42, 0, 10), engine(41, 1, 5), engine(42, 1, 50) };
    const windows = [_]pdh.GpuEngineWindow{ window(41, 0), window(42, 0), window(41, 1), window(42, 1) };
    const ids = [_]gpu.Identity{ .{ .pid = 41, .expected = 1000, .current = 1000 }, .{ .pid = 42, .expected = 1000, .current = 1000 } };
    const request = input(&engines, &windows, &.{}, &ids);
    const work = try std.testing.allocator.alloc(u8, gpu.required(&request).?);
    defer std.testing.allocator.free(work);
    var output: gpu.Output = .{};
    try std.testing.expectEqual(@as(u32, 0), gpu.project(&request, work, &output));
    const rows = output.rows.?[0..output.count];
    try std.testing.expectEqual(@as(?f64, 30), find(rows, 1, 41));
    try std.testing.expectEqual(@as(?f64, 50), find(rows, 1, 42));
    try std.testing.expectEqual(@as(?f64, 5), find(rows, 3, 41));
    try std.testing.expectEqual(@as(?f64, 55), find(rows, 5, 0));
    try std.testing.expect(output.workspace_used <= work.len);
}
test "duplicate or reused identities and ambiguous windows reject attribution without removing device totals" {
    const engines = [_]pdh.GpuEngineRow{ engine(41, 0, 20), engine(42, 0, 30), engine(43, 0, 10) };
    const windows = [_]pdh.GpuEngineWindow{ window(41, 0), window(42, 0), window(43, 0), window(43, 0) };
    const ids = [_]gpu.Identity{ .{ .pid = 41, .expected = 1000, .current = 1000 }, .{ .pid = 41, .expected = 1000, .current = 1000 },
        .{ .pid = 42, .expected = 1000, .current = 2000 }, .{ .pid = 43, .expected = 1000, .current = 1000 } };
    const request = input(&engines, &windows, &.{}, &ids);
    const work = try std.testing.allocator.alloc(u8, gpu.required(&request).?);
    defer std.testing.allocator.free(work);
    var output: gpu.Output = .{};
    try std.testing.expectEqual(@as(u32, 0), gpu.project(&request, work, &output));
    for (output.rows.?[0..output.count]) |row| try std.testing.expect(row.kind != 1 and row.kind != 3);
    try std.testing.expectEqual(@as(?f64, 60), find(output.rows.?[0..output.count], 5, 0));
}
test "memory only output does not overwrite pending rows and unavailable identities do not hide raw breakdown memory" {
    const memory = [_]pdh.GpuMemoryRow{ .{ .adapter_luid = 23, .process_id = 41, .reserved = 0, .dedicated_bytes = 100 },
        .{ .adapter_luid = 23, .process_id = 42, .reserved = 0, .dedicated_bytes = 200 } };
    const request = input(&.{}, &.{}, &memory, &.{});
    const work = try std.testing.allocator.alloc(u8, gpu.required(&request).?);
    defer std.testing.allocator.free(work);
    var output: gpu.Output = .{};
    try std.testing.expectEqual(@as(u32, 0), gpu.project(&request, work, &output));
    try std.testing.expectEqual(@as(usize, 2), output.count);
    try std.testing.expectEqual(@as(?f64, 100), find(output.rows.?[0..output.count], 4, 41));
    try std.testing.expectEqual(@as(?f64, 200), find(output.rows.?[0..output.count], 4, 42));
    try std.testing.expectEqual(@as(u32, 5), gpu.project(&request, work[0 .. work.len - 1], &output));
    try std.testing.expect(output.rows == null and output.count == 0);
}
test "names are exact bounded Windows identities, zero luid and pid overflow are rejected" {
    const good = std.unicode.utf8ToUtf16LeStringLiteral("pid_41_luid_0x00000000_0x00000017_phys_0");
    const bad = std.unicode.utf8ToUtf16LeStringLiteral("pid_2147483648_luid_0x00000000_0x00000017_phys_0");
    var pid: u32 = 0; var luid: u64 = 0;
    try std.testing.expect(gpu.parse(.{ .text = good, .length = good.len }, true, &pid, &luid));
    try std.testing.expectEqual(@as(u32, 41), pid); try std.testing.expectEqual(@as(u64, 23), luid);
    try std.testing.expect(!gpu.parse(.{ .text = bad, .length = bad.len }, true, &pid, &luid));
    try std.testing.expect(pid == 0 and luid == 0);
}
test "one invalid physical counter drops its whole process sum, valid zero remains data" {
    const name0 = std.unicode.utf8ToUtf16LeStringLiteral("pid_41_luid_0x00000000_0x00000017_phys_0");
    const name1 = std.unicode.utf8ToUtf16LeStringLiteral("pid_41_luid_0x00000000_0x00000017_phys_1");
    const name2 = std.unicode.utf8ToUtf16LeStringLiteral("pid_42_luid_0x00000000_0x00000017_phys_0");
    const values = [_]gpu.Formatted{ .{ .name = .{ .text = name0, .length = name0.len }, .status = 0, .value = 100 },
        .{ .name = .{ .text = name1, .length = name1.len }, .status = 7, .value = 10 },
        .{ .name = .{ .text = name2, .length = name2.len }, .status = 1, .value = 0 } };
    var rows: [3]gpu.SharedRow = undefined;
    var output: gpu.SharedResult = .{};
    try std.testing.expectEqual(@as(u32, 0), gpu.shared(&values, &.{23}, true, &rows, &output));
    try std.testing.expectEqual(@as(usize, 1), output.count);
    try std.testing.expectEqual(@as(u32, 42), output.rows.?[0].pid);
    try std.testing.expectEqual(@as(u64, 0), output.rows.?[0].bytes);
    try std.testing.expectEqual(@as(u32, 1), output.failures);
    try std.testing.expectEqual(@as(u32, 7), output.native_status);
}
test "ABI sizes and multiplication overflow fail explicitly" {
    try std.testing.expectEqual(@as(usize, 24), @sizeOf(gpu.Identity));
    try std.testing.expectEqual(@as(usize, 48), @sizeOf(gpu.Row));
    try std.testing.expectEqual(@as(usize, 32), @sizeOf(gpu.Formatted));
    var request = input(&.{}, &.{}, &.{}, &.{});
    request.engine_count = std.math.maxInt(usize);
    try std.testing.expect(gpu.required(&request) == null);
}
