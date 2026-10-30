const std = @import("std");
const support = @import("support.zig");
const ss = support.ss;
const testing = std.testing;

fn writtenAndRead(f: *support.Fixture) !void {
    try f.init();
    const rows = try f.writeRows(&.{ 1, 2, 3 }, &.{ "a", "b", "c" });
    try f.writeCpu(&.{ 4, 5, 6 }, rows);
    try testing.expect(f.read(support.rows_source) == .value);
    try testing.expect(f.read(support.metrics_source) == .value);
    try testing.expect(f.value(support.cpu) == .value);
}

fn expectZero(bytes: []const u8) !void {
    for (bytes) |byte| try testing.expectEqual(@as(u8, 0), byte);
}

test "end marks every source ended and zeroes the data area" {
    var f = support.Fixture{};
    try writtenAndRead(&f);
    try ss.end(f.bytes(), 777);

    const view = try ss.layout.Layout.load(f.bytes());
    for (0..2) |source| {
        const entry = view.source(f.bytes(), @intCast(source));
        try testing.expect(entry.sequence & ss.ended_bit != 0);
        try testing.expectEqual(ss.SourceState.ended, entry.state);
        try testing.expectEqual(@as(i64, 777), entry.write_time_unix_ms);
    }
    for (0..5) |position| {
        const entry = view.position(f.bytes(), @intCast(position));
        try testing.expectEqual(@as(u32, 0), entry.length);
        try testing.expectEqual(@as(u64, 0), entry.identity_sequence);
    }
    try expectZero(view.dataArea(f.bytes()));
    // The identities and the layout stay readable.
    try testing.expectEqualStrings("cpu-sampler", (try ss.describeSource(f.bytes(), support.metrics_source)).owner());

    try testing.expectEqual(@as(ss.SourceRead, .ended), f.read(support.rows_source));
    try testing.expectEqual(@as(ss.SourceRead, .ended), f.read(support.metrics_source));
    for (0..5) |position| try testing.expectEqual(@as(ss.PositionValue, .ended), f.value(@intCast(position)));
    // The reader's copy keeps nothing either.
    try expectZero(&f.data);
    try testing.expectEqual(@as(?[]const u8, null), ss.stringAt(&f.selection, support.names, 0));
    try testing.expectEqual(@as(ss.SourceRead, .unchanged), f.read(support.rows_source));

    try testing.expectError(error.Ended, ss.Writer.begin(f.bytes(), support.rows_source));
    try testing.expectError(error.Ended, ss.Writer.begin(f.bytes(), support.metrics_source));
    // Ending twice is harmless.
    try ss.end(f.bytes(), 778);
    try testing.expectEqual(@as(ss.SourceRead, .unchanged), f.read(support.metrics_source));
}

test "a round open during end fails and leaves no values" {
    var f = support.Fixture{};
    try writtenAndRead(&f);
    var writer = try ss.Writer.begin(f.bytes(), support.metrics_source);
    try writer.store(u64, support.totals, 0, &.{ 1, 2 });
    // A reader during the open round, then end.
    try testing.expectEqual(ss.SourceRead{ .failed = .retry_limit }, ss.read(f.bytes(), &f.selection, support.metrics_source, 3));
    try ss.end(f.bytes(), 1);
    try testing.expectError(error.Ended, writer.store(u64, support.totals, 0, &.{ 3, 4 }));
    try testing.expectError(error.Ended, writer.setLength(support.cpu, 1, 0));
    // A store that passed its check just before end landed afterwards.
    f.poke64(support.dataField(288), 0xDEAD);
    f.poke32(support.positionField(support.cpu, "length"), 3);
    try testing.expectError(error.Ended, writer.end(.has_value, 2));
    const view = try ss.layout.Layout.load(f.bytes());
    try expectZero(view.dataArea(f.bytes()));
    try testing.expectEqual(@as(u32, 0), view.position(f.bytes(), support.cpu).length);
    try testing.expectEqual(@as(ss.SourceRead, .ended), f.read(support.metrics_source));
}

test "a source that ends itself zeroes its positions and cannot begin again" {
    var f = support.Fixture{};
    try writtenAndRead(&f);
    var writer = try ss.Writer.begin(f.bytes(), support.metrics_source);
    try writer.end(.ended, 9);
    try testing.expect(writer.sequence & ss.ended_bit != 0);

    try testing.expectEqual(@as(ss.SourceRead, .ended), f.read(support.metrics_source));
    try testing.expectEqual(@as(ss.PositionValue, .ended), f.value(support.cpu));
    try testing.expectEqual(@as(ss.PositionValue, .ended), f.value(support.totals));
    const view = try ss.layout.Layout.load(f.bytes());
    const data = view.dataArea(f.bytes());
    try expectZero(data[256..304]);
    try expectZero(f.data[256..304]);
    try testing.expectEqual(@as(u32, 0), view.position(f.bytes(), support.cpu).length);
    try testing.expectError(error.Ended, ss.Writer.begin(f.bytes(), support.metrics_source));

    // The other source keeps its value.
    try testing.expectEqual(@as(ss.SourceRead, .unchanged), f.read(support.rows_source));
    try testing.expect(f.value(support.ids) == .value);
    try testing.expectEqualStrings("b", ss.stringAt(&f.selection, support.names, 1).?);
    try testing.expect(f.read(support.metrics_source) == .unchanged);
}
