const std = @import("std");
const control = @import("rm_control");
const readers = control.readers;

fn byId(table: *const readers.Readers, id: u64) ?readers.Entry {
    for (table.entries) |entry| if (entry.used and entry.id == id) return entry;
    return null;
}

test "runtime reader holder names are copied and unique within an item key" {
    var table = try readers.Readers.init(std.testing.allocator, 4, 64);
    defer table.deinit();
    var holder = [_]u8{ 'v', 'i', 'e', 'w' };
    const first = try table.register(.{ .item = 10, .block = 2 }, &holder);
    @memset(&holder, 'x');
    try std.testing.expectEqualStrings("view", byId(&table, first).?.holder);
    try std.testing.expectError(error.DuplicateHolder, table.register(.{ .item = 10, .block = 2 }, "view"));
    try std.testing.expectEqual(@as(usize, 1), table.count);
    try std.testing.expectEqual(first, table.last_id);
    _ = try table.register(.{ .item = 10, .block = 3 }, "view");
    _ = try table.register(.{ .item = 11 }, "view");
    try std.testing.expectEqual(@as(usize, 3), table.count);
    try std.testing.expectError(error.InvalidHolder, table.register(.{ .item = 11 }, ""));
}

test "runtime reader unregister is idempotent and stale IDs cannot remove reused slots" {
    var table = try readers.Readers.init(std.testing.allocator, 1, 8);
    defer table.deinit();
    const first = try table.register(.{ .item = 10 }, "one");
    table.unregister(first);
    table.unregister(first);
    table.unregister(0);
    try std.testing.expectEqual(@as(usize, 0), table.current());
    const second = try table.register(.{ .item = 10 }, "two");
    try std.testing.expect(second > first);
    table.unregister(first);
    try std.testing.expectEqual(@as(usize, 1), table.count);
    try std.testing.expectEqualStrings("two", byId(&table, second).?.holder);
    table.unregister(second);
    try std.testing.expectEqual(@as(usize, 0), table.current());
}

test "runtime reader entry and text limits fail before changing the table" {
    var entries = try readers.Readers.init(std.testing.allocator, 1, 32);
    defer entries.deinit();
    const first = try entries.register(.{ .item = 10 }, "first");
    const occupied = entries.current();
    try std.testing.expectError(error.ReaderFull, entries.register(.{ .item = 10 }, "second"));
    try std.testing.expectEqual(occupied, entries.current());
    try std.testing.expectEqual(first, entries.last_id);
    try std.testing.expectEqualStrings("first", byId(&entries, first).?.holder);

    var bytes = try readers.Readers.init(std.testing.allocator, 2, 3);
    defer bytes.deinit();
    const full = try bytes.register(.{ .item = 10 }, "abc");
    const byte_usage = bytes.current();
    try std.testing.expectError(error.ReaderFull, bytes.register(.{ .item = 11 }, "d"));
    try std.testing.expectEqual(byte_usage, bytes.current());
    try std.testing.expectEqual(full, bytes.last_id);
    bytes.unregister(full);
    const reused = try bytes.register(.{ .item = 11 }, "xyz");
    try std.testing.expect(reused > full);
    try std.testing.expectEqual(byte_usage, bytes.current());
    try std.testing.expectEqual(byte_usage, bytes.peak);
}

test "runtime reader text compaction preserves holders and reuses all released bytes" {
    var table = try readers.Readers.init(std.testing.allocator, 3, 17);
    defer table.deinit();
    const alpha = try table.register(.{ .item = 10 }, "alpha");
    const bravo = try table.register(.{ .item = 10 }, "bravo");
    const charlie = try table.register(.{ .item = 10 }, "charlie");
    table.unregister(bravo);
    try std.testing.expectEqualStrings("charlie", byId(&table, charlie).?.holder);
    const delta = try table.register(.{ .item = 10 }, "delta");
    try std.testing.expectEqual(@as(usize, 17), table.used_bytes);
    try std.testing.expectEqualStrings("alpha", byId(&table, alpha).?.holder);
    try std.testing.expectEqualStrings("charlie", byId(&table, charlie).?.holder);
    try std.testing.expectEqualStrings("delta", byId(&table, delta).?.holder);
    table.unregister(alpha);
    table.unregister(charlie);
    try std.testing.expectEqualStrings("delta", byId(&table, delta).?.holder);
    table.unregister(delta);
    try std.testing.expectEqual(@as(usize, 0), table.current());
    try std.testing.expectEqual(table.capacity(), table.peak);
}

test "runtime reader registration allocates nothing after bounded storage creation" {
    var failing = std.testing.FailingAllocator.init(std.testing.allocator, .{});
    var table = try readers.Readers.init(failing.allocator(), 2, 32);
    defer table.deinit();
    failing.fail_index = failing.alloc_index;
    for (0..1000) |_| {
        const first = try table.register(.{ .item = 10 }, "first");
        const second = try table.register(.{ .item = 11 }, "second");
        table.unregister(first);
        table.unregister(first);
        table.unregister(second);
    }
    try std.testing.expect(!failing.has_induced_failure);
    try std.testing.expectEqual(@as(u64, 2000), table.last_id);
    try std.testing.expectEqual(@as(usize, 0), table.current());
    try std.testing.expect(table.peak <= table.capacity());
}

test "runtime reader ID exhaustion returns a bounded capacity error without wrapping" {
    var table = try readers.Readers.init(std.testing.allocator, 1, 16);
    defer table.deinit();
    table.last_id = std.math.maxInt(u64);
    try std.testing.expectError(error.ReaderFull, table.register(.{ .item = 10 }, "holder"));
    try std.testing.expectEqual(std.math.maxInt(u64), table.last_id);
    try std.testing.expectEqual(@as(usize, 0), table.current());
    try std.testing.expectEqual(@as(usize, 0), table.count);
}
