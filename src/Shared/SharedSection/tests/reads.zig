const std = @import("std");
const support = @import("support.zig");
const ss = support.ss;
const testing = std.testing;

fn expectU64s(expected: []const u64, bytes: []const u8) !void {
    try testing.expectEqual(expected.len * 8, bytes.len);
    for (expected, 0..) |number, index| try testing.expectEqual(number, std.mem.readInt(u64, bytes[index * 8 ..][0..8], .little));
}
fn expectU32s(expected: []const u32, bytes: []const u8) !void {
    try testing.expectEqual(expected.len * 4, bytes.len);
    for (expected, 0..) |number, index| try testing.expectEqual(number, std.mem.readInt(u32, bytes[index * 4 ..][0..4], .little));
}
fn sequenceOf(result: ss.SourceRead) !u64 {
    return switch (result) {
        .value => |sequence| sequence,
        else => error.NoValue,
    };
}

test "a source not written yet is unavailable with the not-written reason" {
    var f = support.Fixture{};
    try f.init();
    try testing.expectEqual(@as(ss.PositionValue, .not_arrived), f.value(support.ids));
    try testing.expectEqual(ss.SourceRead{ .unavailable = ss.not_written_reason }, f.read(support.metrics_source));
    try testing.expectEqual(ss.PositionValue{ .unavailable = ss.not_written_reason }, f.value(support.totals));
}

test "an unchanged sequence reports unchanged and copies nothing" {
    var f = support.Fixture{};
    try f.init();
    const first = try f.writeRows(&.{ 1, 2, 3 }, &.{ "a", "bb", "ccc" });
    try testing.expectEqual(ss.SourceRead{ .value = first }, f.read(support.rows_source));
    try expectU64s(&.{ 1, 2, 3 }, try f.bytesOf(support.ids));
    try testing.expectEqual(@as(i64, 1000), f.sources[support.rows_source].write_time_unix_ms);

    @memset(&f.data, 0xAA);
    try testing.expectEqual(@as(ss.SourceRead, .unchanged), f.read(support.rows_source));
    try testing.expectEqual(@as(ss.SourceRead, .unchanged), f.read(support.rows_source));
    for (f.data) |byte| try testing.expectEqual(@as(u8, 0xAA), byte);

    const second = try f.writeRows(&.{4}, &.{"d"});
    try testing.expect(second > first and second % 2 == 0);
    try testing.expectEqual(ss.SourceRead{ .value = second }, f.read(support.rows_source));
    try expectU64s(&.{4}, try f.bytesOf(support.ids));
    try testing.expectEqualStrings("d", ss.stringAt(&f.selection, support.names, 0).?);
}

test "a column is used only when written against the identity rows the copy holds" {
    var f = support.Fixture{};
    try f.init();
    const r1 = try f.writeRows(&.{ 10, 20, 30 }, &.{ "x", "y", "z" });
    try f.writeCpu(&.{ 1, 2, 3 }, r1);
    _ = try sequenceOf(f.read(support.rows_source));
    _ = try sequenceOf(f.read(support.metrics_source));
    try expectU32s(&.{ 1, 2, 3 }, try f.bytesOf(support.cpu));
    try testing.expectEqualStrings("y", ss.stringAt(&f.selection, support.names, 1).?);

    // New rows: the cpu column still belongs to the old ones.
    const r2 = try f.writeRows(&.{ 10, 20, 30, 40 }, &.{ "x", "y", "z", "w" });
    try testing.expectEqual(ss.SourceRead{ .value = r2 }, f.read(support.rows_source));
    try testing.expectEqual(@as(ss.SourceRead, .unchanged), f.read(support.metrics_source));
    try testing.expectEqual(@as(ss.PositionValue, .not_arrived), f.value(support.cpu));
    // A column written against an older identity sequence has not arrived.
    try f.writeCpu(&.{ 5, 6, 7 }, r1);
    _ = try sequenceOf(f.read(support.metrics_source));
    try testing.expectEqual(@as(ss.PositionValue, .not_arrived), f.value(support.cpu));
    // A scalar of the same source does not depend on rows.
    try expectU64s(&.{ 11, 22 }, try f.bytesOf(support.totals));
    // Written against the current rows, it is used.
    try f.writeCpu(&.{ 5, 6, 7, 8 }, r2);
    _ = try sequenceOf(f.read(support.metrics_source));
    try expectU32s(&.{ 5, 6, 7, 8 }, try f.bytesOf(support.cpu));

    // The reader pairs with the rows it holds, not the rows in the region.
    const r3 = try f.writeRows(&.{50}, &.{"v"});
    try f.writeCpu(&.{9}, r3);
    _ = try sequenceOf(f.read(support.metrics_source));
    try testing.expectEqual(@as(ss.PositionValue, .not_arrived), f.value(support.cpu));
    _ = try sequenceOf(f.read(support.rows_source));
    try expectU32s(&.{9}, try f.bytesOf(support.cpu));

    // A same-source column not rewritten in a round with new rows has not arrived.
    var writer = try ss.Writer.begin(f.bytes(), support.rows_source);
    try writer.store(u64, support.ids, 0, &.{ 60, 70 });
    try writer.setLength(support.ids, 2, 0);
    try writer.end(.has_value, 3000);
    const r4 = try sequenceOf(f.read(support.rows_source));
    try expectU64s(&.{ 60, 70 }, try f.bytesOf(support.ids));
    try testing.expectEqual(@as(ss.PositionValue, .not_arrived), f.value(support.names));
    try testing.expectEqual(@as(ss.PositionValue, .not_arrived), f.value(support.cpu));

    // Against the current rows but with another row count: malformed.
    try f.writeCpu(&.{1}, r4);
    _ = try sequenceOf(f.read(support.metrics_source));
    try testing.expectEqual(ss.PositionValue{ .failed = .rows_mismatch }, f.value(support.cpu));
}

test "string columns read through their own string table" {
    var f = support.Fixture{};
    try f.init();
    _ = try f.writeRows(&.{ 1, 2, 3 }, &.{ "explorer", "", "ゲーム" });
    _ = try sequenceOf(f.read(support.rows_source));
    try testing.expectEqualStrings("explorer", ss.stringAt(&f.selection, support.names, 0).?);
    try testing.expectEqualStrings("", ss.stringAt(&f.selection, support.names, 1).?);
    try testing.expectEqualStrings("ゲーム", ss.stringAt(&f.selection, support.names, 2).?);
    try testing.expectEqual(@as(?[]const u8, null), ss.stringAt(&f.selection, support.names, 3));
    try testing.expectEqual(@as(?[]const u8, null), ss.stringAt(&f.selection, support.ids, 0));
    try testing.expectEqualStrings("explorerゲーム", try f.bytesOf(support.name_table));
}

test "a source that writes unavailable carries its own reason" {
    var f = support.Fixture{};
    try f.init();
    var writer = try ss.Writer.begin(f.bytes(), support.metrics_source);
    try writer.end(.{ .unavailable = 42 }, 5);
    try testing.expectEqual(ss.SourceRead{ .unavailable = 42 }, f.read(support.metrics_source));
    try testing.expectEqual(ss.PositionValue{ .unavailable = 42 }, f.value(support.totals));
    try testing.expectEqual(@as(i64, 5), f.sources[support.metrics_source].write_time_unix_ms);
    try testing.expectEqual(@as(ss.SourceRead, .unchanged), f.read(support.metrics_source));
}

test "a writer that keeps the sequence odd makes the reader give up at the limit" {
    var f = support.Fixture{};
    try f.init();
    var writer = try ss.Writer.begin(f.bytes(), support.metrics_source);
    try writer.store(u64, support.totals, 0, &.{ 1, 2 });
    try testing.expectEqual(ss.SourceRead{ .failed = .retry_limit }, ss.read(f.bytes(), &f.selection, support.metrics_source, 0));
    try testing.expectEqual(ss.SourceRead{ .failed = .retry_limit }, ss.read(f.bytes(), &f.selection, support.metrics_source, 5));
    try testing.expectEqual(ss.SourceRead{ .failed = .retry_limit }, f.read(support.metrics_source));
    try testing.expectEqual(ss.CopyStatus.failed, f.sources[support.metrics_source].status);
    try testing.expectEqual(ss.PositionValue{ .failed = .retry_limit }, f.value(support.totals));
    try writer.end(.has_value, 1);
    _ = try sequenceOf(f.read(support.metrics_source));
    try expectU64s(&.{ 1, 2 }, try f.bytesOf(support.totals));
}

const HeldRound = struct {
    region: []align(8) u8,
    opened: std.atomic.Value(bool) = .init(false),
    release: std.atomic.Value(bool) = .init(false),
    failed: std.atomic.Value(bool) = .init(false),

    fn run(self: *HeldRound) void {
        self.hold() catch self.failed.store(true, .release);
        self.opened.store(true, .release);
    }
    fn hold(self: *HeldRound) !void {
        var writer = try ss.Writer.begin(self.region, support.metrics_source);
        self.opened.store(true, .release);
        while (!self.release.load(.acquire)) std.Thread.yield() catch {};
        try writer.store(u64, support.totals, 0, &.{ 3, 4 });
        try writer.end(.has_value, 2);
    }
};

test "a reader on another thread gives up at the limit while a round stays open" {
    var f = support.Fixture{};
    try f.init();
    var held = HeldRound{ .region = f.bytes() };
    const thread = try std.Thread.spawn(.{}, HeldRound.run, .{&held});
    var joined = false;
    defer {
        if (!joined) {
            held.release.store(true, .release);
            thread.join();
        }
    }
    while (!held.opened.load(.acquire)) std.Thread.yield() catch {};
    try testing.expect(!held.failed.load(.acquire));
    try testing.expectEqual(ss.SourceRead{ .failed = .retry_limit }, f.read(support.metrics_source));
    held.release.store(true, .release);
    thread.join();
    joined = true;
    try testing.expect(!held.failed.load(.acquire));
    _ = try sequenceOf(f.read(support.metrics_source));
    try expectU64s(&.{ 3, 4 }, try f.bytesOf(support.totals));
}

test "a writer writes only its own positions, within their capacity, inside its round" {
    var f = support.Fixture{};
    try f.init();
    var writer = try ss.Writer.begin(f.bytes(), support.metrics_source);
    try testing.expectError(error.WriteInProgress, ss.Writer.begin(f.bytes(), support.metrics_source));
    try testing.expectError(error.NotOwner, writer.store(u64, support.ids, 0, &.{1}));
    try testing.expectError(error.ElementWidth, writer.store(u64, support.cpu, 0, &.{1}));
    try testing.expectError(error.ElementWidth, writer.storeRaw(support.cpu, 0, &.{ 1, 2, 3 }));
    try testing.expectError(error.ElementRange, writer.store(u32, support.cpu, 7, &.{ 1, 2 }));
    try testing.expectError(error.LengthOverCapacity, writer.setLength(support.cpu, support.row_capacity + 1, 0));
    try testing.expectError(error.WrongKind, writer.setLength(support.totals, 1, 0));
    try testing.expectError(error.PositionIndex, writer.store(u64, 99, 0, &.{1}));
    try writer.end(.has_value, 0);
    try testing.expectError(error.NotWriting, writer.store(u64, support.totals, 0, &.{1}));
    try testing.expectError(error.NotWriting, writer.end(.has_value, 0));
    try testing.expectError(error.SourceIndex, ss.Writer.begin(f.bytes(), 2));

    var rows = try ss.Writer.begin(f.bytes(), support.rows_source);
    try testing.expectError(error.WrongKind, rows.appendString(support.ids, 0, "x"));
    try testing.expectError(error.ElementRange, rows.appendString(support.names, support.row_capacity, "x"));
    try testing.expectError(error.ElementRange, rows.appendString(support.names, 0, &(@as([129]u8, @splat('q')))));
    try rows.appendString(support.names, 0, &(@as([128]u8, @splat('q'))));
    try testing.expectError(error.ElementRange, rows.appendString(support.names, 1, "r"));
    try rows.end(.has_value, 0);
}

test "every source sequence is a 64-bit field on an 8-byte offset" {
    // The page reads each sequence with Atomics.load on a BigInt64Array.
    var capacity: u32 = 0;
    while (capacity < 40) : (capacity += 1) {
        var source: u32 = 0;
        while (source < capacity) : (source += 1) {
            const offset = ss.layout.header_size + source * ss.layout.source_entry_size + @offsetOf(ss.SourceEntry, "sequence");
            try testing.expectEqual(@as(u64, 0), offset % 8);
        }
        try testing.expectEqual(@as(u64, 0), ss.layout.positionTableOffset(capacity) % 8);
        try testing.expectEqual(@as(u64, 0), ss.layout.dataOffset(capacity, capacity + 3) % 8);
    }
    // In a laid-out region, measured from its start.
    var f = support.Fixture{};
    try f.init();
    const view = try ss.layout.Layout.load(f.bytes());
    for (0..2) |source| {
        const offset = @intFromPtr(&view.source(f.bytes(), @intCast(source)).sequence) - @intFromPtr(&f.region);
        try testing.expectEqual(@as(usize, 0), offset % 8);
        try testing.expectEqual(support.sourceField(@intCast(source), "sequence"), offset);
    }
    for (0..5) |position| {
        const offset = @intFromPtr(&view.position(f.bytes(), @intCast(position)).identity_sequence) - @intFromPtr(&f.region);
        try testing.expectEqual(@as(usize, 0), offset % 8);
        try testing.expectEqual(@as(usize, 0), (support.dataField(support.positions[position].offset)) % 8);
    }
}
