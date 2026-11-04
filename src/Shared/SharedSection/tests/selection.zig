const std = @import("std");
const ss = @import("rm_shared_section");
const testing = std.testing;

// Four sources: rows (ids, names and their table), metrics (cpu over ids and
// totals), gpu (a four-element scalar) and temperatures (a column with its own
// rows). Readers select some of them.
const rows_source: u32 = 0;
const metrics_source: u32 = 1;
const gpu_source: u32 = 2;
const temperature_source: u32 = 3;
const ids: u32 = 0;
const names: u32 = 1;
const name_table: u32 = 2;
const cpu: u32 = 3;
const totals: u32 = 4;
const gpu: u32 = 5;
const temperatures: u32 = 6;
const data_capacity: u32 = 360;
const positions = [_]ss.PositionSpec{
    ss.PositionSpec.column(rows_source, 0, 8, 8, ss.none),
    ss.PositionSpec.stringColumn(rows_source, 64, 8, ids, name_table),
    ss.PositionSpec.stringTable(rows_source, 128, 128),
    ss.PositionSpec.column(metrics_source, 256, 8, 4, ids),
    ss.PositionSpec.scalar(metrics_source, 288, 16, 8),
    ss.PositionSpec.scalar(gpu_source, 304, 32, 8),
    ss.PositionSpec.column(temperature_source, 336, 5, 4, ss.none),
};
const gpu_and_temperatures = [_]u32{ gpu_source, temperature_source };

const Region = struct {
    bytes: [@intCast(ss.requiredSize(4, 7, data_capacity))]u8 align(8) = undefined,

    fn init(self: *Region) !void {
        const identities = [_]ss.SourceIdentity{
            try ss.SourceIdentity.init("process-table", 1, 0, 0),
            try ss.SourceIdentity.init("cpu-sampler", 2, 0, 0),
            try ss.SourceIdentity.init("gpu-sampler", 3, 0, 0),
            try ss.SourceIdentity.init("temperature-sampler", 4, 0, 0),
        };
        try ss.initialize(&self.bytes, 9, 4, 7, data_capacity, &identities, &positions);
    }
    fn poke32(self: *Region, offset: usize, number: u32) void {
        std.mem.writeInt(u32, self.bytes[offset..][0..4], number, .little);
    }
    fn positionField(position: u32, comptime field: []const u8) usize {
        return @intCast(ss.layout.positionTableOffset(4) + position * ss.layout.position_entry_size + @offsetOf(ss.PositionEntry, field));
    }
    fn writeGpu(self: *Region, values: []const u64) !void {
        var writer = try ss.Writer.begin(&self.bytes, gpu_source);
        try writer.store(u64, gpu, 0, values);
        try writer.end(.has_value, 1);
    }
    fn writeTemperatures(self: *Region, values: []const u32) !void {
        var writer = try ss.Writer.begin(&self.bytes, temperature_source);
        try writer.store(u32, temperatures, 0, values);
        try writer.setLength(temperatures, @intCast(values.len), 0);
        try writer.end(.has_value, 2);
    }
    fn writeRows(self: *Region, row_ids: []const u64) !u64 {
        var writer = try ss.Writer.begin(&self.bytes, rows_source);
        try writer.store(u64, ids, 0, row_ids);
        try writer.setLength(ids, @intCast(row_ids.len), 0);
        const published = writer.roundSequence();
        try writer.end(.has_value, 3);
        return published;
    }
    fn writeCpu(self: *Region, values: []const u32, against: u64) !void {
        var writer = try ss.Writer.begin(&self.bytes, metrics_source);
        try writer.store(u32, cpu, 0, values);
        try writer.setLength(cpu, @intCast(values.len), against);
        try writer.end(.has_value, 4);
    }
};

fn sequenceOf(result: ss.SourceRead) !u64 {
    return switch (result) {
        .value => |sequence| sequence,
        else => error.NoValue,
    };
}
fn valueOf(selection: *const ss.Selection, position: u32) !ss.Value {
    return switch (ss.positionValue(selection, position)) {
        .value => |found| found,
        else => error.NoValue,
    };
}
fn expectU64s(expected: []const u64, bytes: []const u8) !void {
    try testing.expectEqual(expected.len * 8, bytes.len);
    for (expected, 0..) |number, index| try testing.expectEqual(number, std.mem.readInt(u64, bytes[index * 8 ..][0..8], .little));
}
fn expectU32s(expected: []const u32, bytes: []const u8) !void {
    try testing.expectEqual(expected.len * 4, bytes.len);
    for (expected, 0..) |number, index| try testing.expectEqual(number, std.mem.readInt(u32, bytes[index * 4 ..][0..4], .little));
}

test "a reader of 2 of 4 sources needs memory for those sources only" {
    var region = Region{};
    try region.init();
    // gpu: 32 bytes at 0; temperatures: 5 rows of 4 bytes at 32, rounded up to 8.
    const size = try ss.selectionSize(&region.bytes, &gpu_and_temperatures);
    try testing.expectEqual(ss.SelectionSize{ .data_bytes = 56, .position_count = 2 }, size);
    try testing.expectEqual(ss.SelectionSize{ .data_bytes = data_capacity, .position_count = 7 }, try ss.selectionSize(&region.bytes, &.{ 0, 1, 2, 3 }));
    try testing.expectEqual(ss.SelectionSize{}, try ss.selectionSize(&region.bytes, &.{}));

    var held: [2]ss.SourceCopy = undefined;
    var records: [2]ss.SelectedPosition = undefined;
    var short: [55]u8 = undefined;
    try testing.expectError(error.CopyTooSmall, ss.Selection.init(&region.bytes, &gpu_and_temperatures, &held, &records, &short));
    try testing.expectError(error.CopyTooSmall, ss.Selection.init(&region.bytes, &gpu_and_temperatures, &held, records[0..1], &short));
    var data: [56]u8 = undefined;
    var selection = try ss.Selection.init(&region.bytes, &gpu_and_temperatures, &held, &records, &data);
    try testing.expectEqual(gpu, records[0].index);
    try testing.expectEqual(@as(u64, 0), records[0].offset);
    try testing.expectEqual(temperatures, records[1].index);
    try testing.expectEqual(@as(u64, 32), records[1].offset);

    try region.writeGpu(&.{ 1, 2, 3, 4 });
    try region.writeTemperatures(&.{ 40, 41, 42 });
    _ = try sequenceOf(ss.read(&region.bytes, &selection, gpu_source, ss.default_retry_limit));
    _ = try sequenceOf(ss.read(&region.bytes, &selection, temperature_source, ss.default_retry_limit));
    try expectU64s(&.{ 1, 2, 3, 4 }, (try valueOf(&selection, gpu)).bytes);
    const read_temperatures = try valueOf(&selection, temperatures);
    try testing.expectEqual(@as(u64, 32), read_temperatures.offset);
    try expectU32s(&.{ 40, 41, 42 }, read_temperatures.bytes);

    try testing.expectEqual(ss.SourceRead{ .failed = .not_selected }, ss.read(&region.bytes, &selection, rows_source, 1));
    try testing.expectEqual(ss.PositionValue{ .failed = .not_selected }, ss.positionValue(&selection, ids));
}

test "an unchanged source is not copied while a changed one is" {
    var region = Region{};
    try region.init();
    var held: [2]ss.SourceCopy = undefined;
    var records: [2]ss.SelectedPosition = undefined;
    var data: [56]u8 = undefined;
    var selection = try ss.Selection.init(&region.bytes, &gpu_and_temperatures, &held, &records, &data);
    try region.writeGpu(&.{ 1, 2, 3, 4 });
    try region.writeTemperatures(&.{ 40, 41, 42 });
    _ = try sequenceOf(ss.read(&region.bytes, &selection, gpu_source, 1));
    _ = try sequenceOf(ss.read(&region.bytes, &selection, temperature_source, 1));

    @memset(&data, 0xAA);
    try region.writeGpu(&.{ 5, 6, 7, 8 });
    _ = try sequenceOf(ss.read(&region.bytes, &selection, gpu_source, 1));
    try testing.expectEqual(@as(ss.SourceRead, .unchanged), ss.read(&region.bytes, &selection, temperature_source, 1));
    try expectU64s(&.{ 5, 6, 7, 8 }, data[0..32]);
    for (data[32..]) |byte| try testing.expectEqual(@as(u8, 0xAA), byte);
}

test "a row column needs its identity source in the selection" {
    var region = Region{};
    try region.init();
    try testing.expectError(error.IdentityNotSelected, ss.selectionSize(&region.bytes, &.{metrics_source}));
    try testing.expectError(error.IdentityNotSelected, ss.selectionSize(&region.bytes, &.{ metrics_source, gpu_source }));
    // A string column over an identity of its own source needs nothing more.
    try testing.expectEqual(ss.SelectionSize{ .data_bytes = 256, .position_count = 3 }, try ss.selectionSize(&region.bytes, &.{rows_source}));

    var held: [2]ss.SourceCopy = undefined;
    var records: [5]ss.SelectedPosition = undefined;
    var data: [304]u8 = undefined;
    try testing.expectError(error.IdentityNotSelected, ss.Selection.init(&region.bytes, &.{metrics_source}, &held, &records, &data));
    var selection = try ss.Selection.init(&region.bytes, &.{ rows_source, metrics_source }, &held, &records, &data);
    const rows = try region.writeRows(&.{ 10, 20, 30 });
    try region.writeCpu(&.{ 1, 2, 3 }, rows);
    _ = try sequenceOf(ss.read(&region.bytes, &selection, metrics_source, 1));
    // Not arrived until the reader holds the rows it was written against.
    try testing.expectEqual(@as(ss.PositionValue, .not_arrived), ss.positionValue(&selection, cpu));
    _ = try sequenceOf(ss.read(&region.bytes, &selection, rows_source, 1));
    try expectU32s(&.{ 1, 2, 3 }, (try valueOf(&selection, cpu)).bytes);
    try expectU64s(&.{ 10, 20, 30 }, (try valueOf(&selection, ids)).bytes);

    // A region rewritten so the column's identity is elsewhere no longer matches.
    region.poke32(Region.positionField(cpu, "spec") + @offsetOf(ss.PositionSpec, "identity"), temperatures);
    try region.writeCpu(&.{ 4, 5, 6 }, rows);
    try testing.expectEqual(ss.SourceRead{ .failed = .layout_changed }, ss.read(&region.bytes, &selection, metrics_source, 1));
    try testing.expectEqual(ss.PositionValue{ .failed = .layout_changed }, ss.positionValue(&selection, cpu));
}

test "malformed content fails only the selected source it is in" {
    var region = Region{};
    try region.init();
    var held: [2]ss.SourceCopy = undefined;
    var records: [2]ss.SelectedPosition = undefined;
    var data: [56]u8 = undefined;
    var selection = try ss.Selection.init(&region.bytes, &gpu_and_temperatures, &held, &records, &data);
    try region.writeGpu(&.{ 1, 2, 3, 4 });
    try region.writeTemperatures(&.{ 40, 41, 42 });

    // A length over capacity in temperatures.
    region.poke32(Region.positionField(temperatures, "length"), 6);
    try testing.expectEqual(ss.SourceRead{ .failed = .length_over_capacity }, ss.read(&region.bytes, &selection, temperature_source, 1));
    try testing.expectEqual(ss.PositionValue{ .failed = .length_over_capacity }, ss.positionValue(&selection, temperatures));
    _ = try sequenceOf(ss.read(&region.bytes, &selection, gpu_source, 1));

    // A header change after setup fails every read of the selection.
    region.poke32(@offsetOf(ss.Header, "data_kind"), 10);
    try testing.expectEqual(ss.SourceRead{ .failed = .layout_changed }, ss.read(&region.bytes, &selection, gpu_source, 1));
    region.poke32(@offsetOf(ss.Header, "data_kind"), 9);
    region.poke32(@offsetOf(ss.Header, "magic"), 0);
    try testing.expectEqual(ss.SourceRead{ .failed = .bad_magic }, ss.read(&region.bytes, &selection, gpu_source, 1));
    region.poke32(@offsetOf(ss.Header, "magic"), ss.magic);

    // A broken position of an unselected source does not affect this selection,
    // but a new selection checks the whole table.
    region.poke32(Region.positionField(ids, "spec") + @offsetOf(ss.PositionSpec, "offset"), 1000);
    _ = try sequenceOf(ss.read(&region.bytes, &selection, gpu_source, 1));
    try expectU64s(&.{ 1, 2, 3, 4 }, (try valueOf(&selection, gpu)).bytes);
    try testing.expectError(error.PositionRange, ss.selectionSize(&region.bytes, &gpu_and_temperatures));

    // A selected position rewritten after setup fails its source.
    region.poke32(Region.positionField(gpu, "spec") + @offsetOf(ss.PositionSpec, "size"), 24);
    try region.writeGpu(&.{ 9, 9, 9 });
    try testing.expectEqual(ss.SourceRead{ .failed = .layout_changed }, ss.read(&region.bytes, &selection, gpu_source, 1));
}
