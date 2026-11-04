const std = @import("std");
const items = @import("items.zig");

fn row(pid: i32, birth: u64, first: i64) items.Row {
    var key: [20]u8 = @splat(0);
    key[0] = @intCast('0' + birth);
    return .{ .pid = pid, .state = 0, .birth = birth, .key = key, .key_length = 1, .current = 1200, .growth = 200, .seconds = 2, .rate = 100, .sample_count = 3, .first = first, .last = first, .payload = 0 };
}

fn unavailableMetadata() items.Metadata {
    const none = items.Text{ .pointer = null, .length = 0 };
    return .{ .image_status = 1, .services_status = 1, .name = none, .path = none, .product = none, .names = null, .names_count = 0 };
}
fn bytes(maximum: u32) usize {
    return items.requiredBytes(maximum) + 2 * @as(usize, maximum) * @sizeOf(items.Metadata);
}

test "item layout stays identical to the reference boundary" {
    try std.testing.expectEqual(@as(usize, 120), @sizeOf(items.Row));
    try std.testing.expectEqual(@as(usize, 40), @sizeOf(items.Key));
    try std.testing.expectEqual(@as(usize, 24), @sizeOf(items.PlanResult));
    try std.testing.expectEqual(@as(usize, 72), @sizeOf(items.Metadata));
}

test "failed file save leaves committed evidence and metadata untouched" {
    const memory = try std.testing.allocator.alignedAlloc(u8, .@"8", bytes(4));
    defer std.testing.allocator.free(memory);
    const store = try items.init(memory, 4);
    const original = row(7, 1, 100);
    try store.restore(&.{original}, &.{unavailableMetadata()});
    const live = original.identity();
    var next = original;
    next.current = 1400;
    const planned = try store.plan(300, &.{live}, &.{}, &.{live}, &.{next}, &.{unavailableMetadata()}, 4);
    try std.testing.expectEqual(@as(u32, 1), planned.changed);
    try std.testing.expectEqual(@as(u64, 1400), (try store.snapshot(true))[0].current);
    // Simulate SaveItems throwing: do not call commit.
    try std.testing.expectEqualDeep(original, (try store.snapshot(false))[0]);
    _ = try store.plan(400, &.{live}, &.{}, &.{}, &.{}, &.{}, 4);
    try store.commit();
    try std.testing.expectEqualDeep(original, (try store.snapshot(false))[0]);
}

test "history capacity does not evict and clear only commits on success" {
    const memory = try std.testing.allocator.alignedAlloc(u8, .@"8", bytes(4));
    defer std.testing.allocator.free(memory);
    const store = try items.init(memory, 4);
    const original = row(7, 1, 100);
    try store.restore(&.{original}, &.{unavailableMetadata()});
    const replacement = row(7, 2, 200);
    const result = try store.plan(200, &.{replacement.identity()}, &.{}, &.{}, &.{replacement}, &.{unavailableMetadata()}, 1);
    try std.testing.expectEqual(@as(u32, 1), result.unrecorded);
    try std.testing.expectEqual(@as(u32, 0), result.recorded_suspects);
    try store.commit();
    try std.testing.expectEqual(@as(u32, 1), (try store.snapshot(false))[0].state);
    const cleared = try store.clearHistory();
    try std.testing.expectEqual(@as(u32, 0), cleared.count);
    try std.testing.expectEqual(@as(usize, 1), (try store.snapshot(false)).len);
    try store.commit();
    try std.testing.expectEqual(@as(usize, 0), (try store.snapshot(false)).len);
}

test "unavailable identity preserves original holder and later negative judgment ends it" {
    const memory = try std.testing.allocator.alignedAlloc(u8, .@"8", bytes(4));
    defer std.testing.allocator.free(memory);
    const store = try items.init(memory, 4);
    const original = row(7, 1, 100);
    try store.restore(&.{original}, &.{unavailableMetadata()});
    var unknown = original.identity();
    unknown.key_length = 0;
    _ = try store.plan(200, &.{}, &.{unknown}, &.{}, &.{}, &.{}, 4);
    try store.commit();
    try std.testing.expectEqualDeep(original, (try store.snapshot(false))[0]);
    _ = try store.plan(300, &.{original.identity()}, &.{}, &.{original.identity()}, &.{}, &.{}, 4);
    try store.commit();
    const historical = (try store.snapshot(false))[0];
    try std.testing.expectEqual(@as(u32, 1), historical.state);
    try std.testing.expectEqual(original.current, historical.current);
    try std.testing.expectEqual(original.last, historical.last);
}

test "restoration validates first and duplicates before changing authority" {
    const memory = try std.testing.allocator.alignedAlloc(u8, .@"8", bytes(4));
    defer std.testing.allocator.free(memory);
    const store = try items.init(memory, 4);
    const original = row(7, 1, 100);
    try store.restore(&.{original}, &.{unavailableMetadata()});
    var bad = original;
    bad.first = 0;
    try std.testing.expectError(error.InvalidInput, store.restore(&.{bad}, &.{unavailableMetadata()}));
    try std.testing.expectError(error.InvalidInput, store.restore(&.{ original, original }, &.{ unavailableMetadata(), unavailableMetadata() }));
    try std.testing.expectEqualDeep(original, (try store.snapshot(false))[0]);
    const early = row(8, 2, 50);
    try store.restore(&.{ original, early }, &.{ unavailableMetadata(), unavailableMetadata() });
    try std.testing.expectEqual(@as(i32, 8), (try store.snapshot(false))[0].pid);
}

test "metadata rejects stale fields and Unicode blank names" {
    const title = [_]u16{ 'a', 'b' };
    const blank = [_]u16{0x3000};
    const none = items.Text{ .pointer = null, .length = 0 };
    const name = items.Text{ .pointer = &title, .length = title.len };
    var metadata = items.Metadata{ .image_status = 0, .services_status = 0, .name = name, .path = name, .product = none, .names = null, .names_count = 0 };
    try std.testing.expect(items.validMetadata(metadata));
    metadata.image_status = 1;
    try std.testing.expect(!items.validMetadata(metadata));
    metadata.name = none;
    metadata.path = none;
    try std.testing.expect(items.validMetadata(metadata));
    const names = [_]items.Text{.{ .pointer = &blank, .length = blank.len }};
    metadata.names = &names;
    metadata.names_count = names.len;
    try std.testing.expect(!items.validMetadata(metadata));
}

test "owned metadata survives input mutation and is unchanged by an uncommitted replacement" {
    const memory = try std.testing.allocator.alignedAlloc(u8, .@"8", bytes(4));
    defer std.testing.allocator.free(memory);
    const store = try items.init(memory, 4);
    var title = [_]u16{ 'a', 'b' };
    const name = items.Text{ .pointer = &title, .length = title.len };
    const none = items.Text{ .pointer = null, .length = 0 };
    const description = items.Metadata{ .image_status = 0, .services_status = 0, .name = name, .path = name, .product = none, .names = null, .names_count = 0 };
    const original = row(7, 1, 100);
    try store.restore(&.{original}, &.{description});
    title[0] = 'x';
    const copied = try store.metadata(false, 0);
    try std.testing.expectEqual(@as(u16, 'a'), copied.name.pointer.?[0]);
    var next = original;
    next.current = 1400;
    _ = try store.plan(300, &.{original.identity()}, &.{}, &.{original.identity()}, &.{next}, &.{description}, 4);
    try std.testing.expectEqual(@as(u16, 'x'), (try store.metadata(true, 0)).name.pointer.?[0]);
    try std.testing.expectEqual(@as(u16, 'a'), (try store.metadata(false, 0)).name.pointer.?[0]);
    try store.commit();
    try std.testing.expectEqual(@as(u16, 'x'), (try store.metadata(false, 0)).name.pointer.?[0]);
}

test "measured metadata reserve can relocate an empty owner without retained pointers" {
    const initial = try std.testing.allocator.alignedAlloc(u8, .@"8", items.requiredBytes(4));
    defer std.testing.allocator.free(initial);
    const store = try items.init(initial, 4);
    const original = row(7, 1, 100);
    try std.testing.expectError(error.BufferTooSmall, store.restore(&.{original}, &.{unavailableMetadata()}));
    const grown = try std.testing.allocator.alignedAlloc(u8, .@"8", initial.len + 2 * store.required_arena);
    defer std.testing.allocator.free(grown);
    @memcpy(grown[0..initial.len], initial);
    const moved: *items.Store = @ptrCast(grown.ptr);
    try moved.reserve(grown.len);
    try moved.restore(&.{original}, &.{unavailableMetadata()});
    try std.testing.expectEqualDeep(original, (try moved.snapshot(false))[0]);
    try std.testing.expectEqual(@as(u32, 1), (try moved.metadata(false, 0)).image_status);
}
