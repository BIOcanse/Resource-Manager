const std = @import("std");
const input = @import("input_adapter.zig");

const ready = [_]u16{ 'r', 'e', 'a', 'd', 'y' };
const warming = [_]u16{ 'w', 'a', 'r', 'm', 'i', 'n', 'g' };
fn text(value: []const u16) input.Text { return .{ .pointer = value.ptr, .length = value.len }; }
fn row(pid: i32, key: []const u16, value: u64) input.RawRow {
    return .{ .pid = pid, .has_count = 1, .start_key = text(key), .negative = 0, .reserved = 0, .high_bits = 0, .low_bits = value };
}
const Context = struct {
    rows: []const input.RawRow = &.{},
    emitted: [8]input.Emission = undefined,
    count: usize = 0,
    fn query(pointer: ?*anyopaque, index: usize, output: *input.RawRow) callconv(.c) u32 {
        const self: *@This() = @ptrCast(@alignCast(pointer.?));
        output.* = self.rows[index];
        return 0;
    }
    fn sink(pointer: ?*anyopaque, emitted: *const input.Emission) callconv(.c) u32 {
        const self: *@This() = @ptrCast(@alignCast(pointer.?));
        self.emitted[self.count] = emitted.*;
        self.count += 1;
        return 0;
    }
};
fn frame(ticks: i64, count: usize) input.Capture {
    return .{ .snapshot_ticks = ticks, .field_ticks = ticks, .has_field_time = 1, .row_count = @intCast(count), .status = text(&ready) };
}

test "original subscription warmup and current replay never become observations" {
    var state = input.State{};
    var context = Context{};
    var publication = frame(100, 0);
    var result = input.capture(&state, publication, &context, &Context.query, &Context.sink);
    try std.testing.expectEqual(@as(u32, 2), result.disposition);
    try std.testing.expectEqual(@as(u32, 1), result.available);
    result = input.capture(&state, publication, &context, &Context.query, &Context.sink);
    try std.testing.expectEqual(@as(u32, 0), result.disposition);
    publication.status = text(&warming);
    publication.field_ticks = 200;
    result = input.capture(&state, publication, &context, &Context.query, &Context.sink);
    try std.testing.expectEqual(@as(u32, 0), result.disposition);
    try std.testing.expectEqual(@as(i64, 100), state.last_ticks);
    publication.has_field_time = 0;
    publication.status = text(&ready);
    result = input.capture(&state, publication, &context, &Context.query, &Context.sink);
    try std.testing.expectEqual(@as(u32, 1), result.disposition);
    try std.testing.expectEqual(@as(i64, 0), state.has_last);
    result = input.capture(&state, frame(100, 0), &context, &Context.query, &Context.sink);
    try std.testing.expectEqual(@as(u32, 2), result.disposition);
}

test "count failures retain known identity while valid rows canonicalize birth" {
    const zero_padded = [_]u16{ '0', '0', '4', '2' };
    const invalid = [_]u16{ ' ', '4', '2' };
    var missing = row(8, &zero_padded, 0);
    missing.has_count = 0;
    var wide = row(9, &zero_padded, 3);
    wide.high_bits = 1;
    var negative = row(10, &zero_padded, 4);
    negative.negative = 1;
    const rows = [_]input.RawRow{
        row(7, &zero_padded, 1234), missing, wide, negative,
        row(11, &invalid, 5), row(0, &invalid, 6),
    };
    var context = Context{ .rows = &rows };
    var state = input.State{};
    const result = input.capture(&state, frame(100, rows.len), &context, &Context.query, &Context.sink);
    try std.testing.expectEqual(@as(u32, 2), result.disposition);
    try std.testing.expectEqual(@as(u32, 1), result.available);
    try std.testing.expectEqual(@as(u32, 4), result.unavailable_count);
    try std.testing.expectEqual(@as(usize, 5), context.count);
    try std.testing.expectEqualStrings("42", context.emitted[0].key[0..context.emitted[0].key_length]);
    try std.testing.expectEqual(@as(u64, 1234), context.emitted[0].value);
    try std.testing.expectEqual(@as(u32, 0), context.emitted[0].unavailable);
    try std.testing.expectEqual(@as(u32, 1), context.emitted[1].unavailable);
    try std.testing.expectEqual(@as(u32, 1), context.emitted[1].has_birth);
    try std.testing.expectEqual(@as(u32, 0), context.emitted[4].has_birth);
}

test "all failed holders unavailable empty frame available and oversized input rejected" {
    const zero = [_]u16{'0'};
    var context = Context{ .rows = &.{row(7, &zero, 1)} };
    var state = input.State{};
    var result = input.capture(&state, frame(100, 1), &context, &Context.query, &Context.sink);
    try std.testing.expectEqual(@as(u32, 0), result.available);
    try std.testing.expectEqual(@as(u32, 1), result.unavailable_count);
    result = input.capture(&state, frame(200, 0), &context, &Context.query, &Context.sink);
    try std.testing.expectEqual(@as(u32, 1), result.available);
    result = input.capture(&state, frame(300, 16385), &context, &Context.query, &Context.sink);
    try std.testing.expectEqual(@as(u32, 3), result.disposition);
    try std.testing.expectEqual(@as(i64, 300), state.last_ticks);
}
