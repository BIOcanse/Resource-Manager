const std = @import("std");
const ss = @import("rm_shared_section");
const testing = std.testing;

// Six sources. rows (0) owns process ids, names over them and the names'
// string table; metrics (1) a cpu column over the ids and totals; gpu (2) a
// scalar; temperatures (3) a column with its own rows; adapters (4) adapter
// rows plus a per-process share column over the process ids; load (5) a
// column over the adapter rows. Selecting load needs adapters, which needs rows.
const rows_source: u32 = 0;
const metrics_source: u32 = 1;
const gpu_source: u32 = 2;
const temperature_source: u32 = 3;
const adapter_source: u32 = 4;
const load_source: u32 = 5;
const ids: u32 = 0;
const names: u32 = 1;
const name_table: u32 = 2;
const cpu: u32 = 3;
const totals: u32 = 4;
const gpu: u32 = 5;
const temperatures: u32 = 6;
const adapters: u32 = 7;
const gpu_share: u32 = 8;
const adapter_load: u32 = 9;
const source_count = 6;
const position_count = 10;
const data_capacity: u32 = 440;
const region_size: usize = @intCast(ss.requiredSize(source_count, position_count, data_capacity));

const positions = [_]ss.PositionSpec{
    ss.PositionSpec.column(rows_source, 0, 8, 8, ss.none),
    ss.PositionSpec.stringColumn(rows_source, 64, 8, ids, name_table),
    ss.PositionSpec.stringTable(rows_source, 128, 128),
    ss.PositionSpec.column(metrics_source, 256, 8, 4, ids),
    ss.PositionSpec.scalar(metrics_source, 288, 16, 8),
    ss.PositionSpec.scalar(gpu_source, 304, 32, 8),
    ss.PositionSpec.column(temperature_source, 336, 5, 4, ss.none),
    ss.PositionSpec.column(adapter_source, 360, 4, 8, ss.none),
    ss.PositionSpec.column(adapter_source, 392, 8, 4, ids),
    ss.PositionSpec.column(load_source, 424, 4, 4, adapters),
};

fn identities() ![source_count]ss.SourceIdentity {
    return .{
        try ss.SourceIdentity.init("process-table", 1, 0, 0),
        try ss.SourceIdentity.init("cpu-sampler", 2, 0, 0),
        try ss.SourceIdentity.init("gpu-sampler", 3, 0, 0),
        try ss.SourceIdentity.init("temperature-sampler", 4, 0, 0),
        try ss.SourceIdentity.init("gpu-adapters", 5, 0, 0),
        try ss.SourceIdentity.init("adapter-load", 6, 0, 0),
    };
}

fn positionField(position: u32, comptime field: []const u8) usize {
    return @intCast(ss.layout.positionTableOffset(source_count) + position * ss.layout.position_entry_size + @offsetOf(ss.PositionEntry, field));
}
fn specField(position: u32, comptime field: []const u8) usize {
    return positionField(position, "spec") + @offsetOf(ss.PositionSpec, field);
}

const Region = struct {
    bytes: [region_size]u8 align(8) = undefined,

    fn init(self: *Region) !void {
        const sources = try identities();
        try ss.initialize(&self.bytes, 9, source_count, position_count, data_capacity, &sources, &positions);
    }
    fn poke32(self: *Region, offset: usize, number: u32) void {
        std.mem.writeInt(u32, self.bytes[offset..][0..4], number, .little);
    }
    fn writeRows(self: *Region, row_ids: []const u64, row_names: []const []const u8) !u64 {
        var writer = try ss.Writer.begin(&self.bytes, rows_source);
        try writer.store(u64, ids, 0, row_ids);
        try writer.setLength(ids, @intCast(row_ids.len), 0);
        try writer.setLength(name_table, 0, 0);
        for (row_names, 0..) |name, row| try writer.appendString(names, @intCast(row), name);
        try writer.setLength(names, @intCast(row_names.len), writer.roundSequence());
        const published = writer.roundSequence();
        try writer.end(.has_value, 10);
        return published;
    }
    fn writeCpu(self: *Region, values: []const u32, against: u64) !void {
        var writer = try ss.Writer.begin(&self.bytes, metrics_source);
        try writer.store(u32, cpu, 0, values);
        try writer.setLength(cpu, @intCast(values.len), against);
        try writer.store(u64, totals, 0, &.{ 11, 22 });
        try writer.end(.has_value, 11);
    }
    fn writeGpu(self: *Region, values: []const u64) !void {
        var writer = try ss.Writer.begin(&self.bytes, gpu_source);
        try writer.store(u64, gpu, 0, values);
        try writer.end(.has_value, 12);
    }
    fn writeTemperatures(self: *Region, values: []const u32) !void {
        var writer = try ss.Writer.begin(&self.bytes, temperature_source);
        try writer.store(u32, temperatures, 0, values);
        try writer.setLength(temperatures, @intCast(values.len), 0);
        try writer.end(.has_value, 13);
    }
    /// Adapter rows and the per-process share against process rows `against`.
    fn writeAdapters(self: *Region, adapter_ids: []const u64, shares: []const u32, against: u64) !u64 {
        var writer = try ss.Writer.begin(&self.bytes, adapter_source);
        try writer.store(u64, adapters, 0, adapter_ids);
        try writer.setLength(adapters, @intCast(adapter_ids.len), 0);
        try writer.store(u32, gpu_share, 0, shares);
        try writer.setLength(gpu_share, @intCast(shares.len), against);
        const published = writer.roundSequence();
        try writer.end(.has_value, 14);
        return published;
    }
    fn writeLoad(self: *Region, values: []const u32, against: u64) !void {
        var writer = try ss.Writer.begin(&self.bytes, load_source);
        try writer.store(u32, adapter_load, 0, values);
        try writer.setLength(adapter_load, @intCast(values.len), against);
        try writer.end(.has_value, 15);
    }
};

/// Caller memory for a selected copy, zeroed, with room for every source.
const Storage = struct {
    data: [data_capacity]u8 = [_]u8{0} ** data_capacity,
    sources: [source_count]ss.SourceCopy = [_]ss.SourceCopy{.{}} ** source_count,
    positions: [position_count]ss.PositionEntry = std.mem.zeroes([position_count]ss.PositionEntry),
    source_indices: [source_count]u32 = [_]u32{0} ** source_count,
    position_indices: [position_count]u32 = [_]u32{0} ** position_count,
    copy: ss.SelectedCopy = .{},

    /// Points the copy at exactly the prefixes `needs` asks for, as a binding does.
    fn bind(self: *Storage, needs: ss.SelectedRequirements) void {
        self.copy = .{
            .data = &self.data,
            .data_length = needs.data_bytes,
            .sources = &self.sources,
            .positions = &self.positions,
            .source_indices = &self.source_indices,
            .position_indices = &self.position_indices,
            .source_length = needs.source_count,
            .position_length = needs.position_count,
        };
    }
    fn read(self: *Storage, region: []align(8) const u8, named: []const u32, retry_limit: u32) !void {
        try ss.readSelected(region, named, &self.copy, retry_limit);
    }
    fn value(self: *const Storage, position: u32) ss.PositionValue {
        return ss.selectedPositionValue(&self.copy, position);
    }
    fn found(self: *const Storage, position: u32) !ss.Value {
        return switch (self.value(position)) {
            .value => |result| result,
            else => error.NoValue,
        };
    }
    /// Row `row` of string column `column`, decoded from the copy's data.
    fn string(self: *const Storage, column: u32, row: u32) ![]const u8 {
        const rows = try self.found(column);
        const table = try self.found(name_table);
        const packed_row = std.mem.readInt(u64, rows.bytes[@as(usize, row) * 8 ..][0..8], .little);
        const offset: usize = @intCast(packed_row & 0xFFFF_FFFF);
        return table.bytes[offset..][0..@intCast(packed_row >> 32)];
    }
};

fn expectU64s(expected: []const u64, bytes: []const u8) !void {
    try testing.expectEqual(expected.len * 8, bytes.len);
    for (expected, 0..) |number, index| try testing.expectEqual(number, std.mem.readInt(u64, bytes[index * 8 ..][0..8], .little));
}
fn expectU32s(expected: []const u32, bytes: []const u8) !void {
    try testing.expectEqual(expected.len * 4, bytes.len);
    for (expected, 0..) |number, index| try testing.expectEqual(number, std.mem.readInt(u32, bytes[index * 4 ..][0..4], .little));
}

test "a selected read needs the named sources and, recursively, their identity owners only" {
    var region = Region{};
    try region.init();
    // load needs adapters (its rows), which needs rows (the share column's rows):
    // ids 64, names 64, name table 128, adapters 32, share 32, load 16.
    try testing.expectEqual(ss.SelectedRequirements{ .data_bytes = 336, .source_count = 3, .position_count = 6 }, try ss.selectedRequirements(&region.bytes, &.{load_source}));
    // gpu 32 and temperatures 20 rounded up to 24.
    try testing.expectEqual(ss.SelectedRequirements{ .data_bytes = 56, .source_count = 2, .position_count = 2 }, try ss.selectedRequirements(&region.bytes, &.{ gpu_source, temperature_source }));
    try testing.expectEqual(ss.SelectedRequirements{ .data_bytes = 304, .source_count = 2, .position_count = 5 }, try ss.selectedRequirements(&region.bytes, &.{metrics_source}));
    try testing.expectEqual(ss.SelectedRequirements{ .data_bytes = 256, .source_count = 1, .position_count = 3 }, try ss.selectedRequirements(&region.bytes, &.{rows_source}));
    // Naming an identity owner as well changes nothing.
    try testing.expectEqual(try ss.selectedRequirements(&region.bytes, &.{load_source}), try ss.selectedRequirements(&region.bytes, &.{ rows_source, adapter_source, load_source }));

    try testing.expectError(error.SelectionEmpty, ss.selectedRequirements(&region.bytes, &.{}));
    try testing.expectError(error.SelectionOrder, ss.selectedRequirements(&region.bytes, &.{ temperature_source, gpu_source }));
    try testing.expectError(error.SelectionOrder, ss.selectedRequirements(&region.bytes, &.{ gpu_source, gpu_source }));
    try testing.expectError(error.SourceIndex, ss.selectedRequirements(&region.bytes, &.{source_count}));
}

test "selected requirements do not depend on unrelated sources' reservations" {
    var region = Region{};
    try region.init();
    // The same layout with gpu reserving 4 KiB more, every later position moved.
    var wide = positions;
    wide[gpu].size += 4096;
    for (wide[temperatures..]) |*spec| spec.offset += 4096;
    const wide_capacity = data_capacity + 4096;
    var wide_region: [@intCast(ss.requiredSize(source_count, position_count, wide_capacity))]u8 align(8) = undefined;
    const sources = try identities();
    try ss.initialize(&wide_region, 9, source_count, position_count, wide_capacity, &sources, &wide);
    try testing.expectEqual(try ss.selectedRequirements(&region.bytes, &.{load_source}), try ss.selectedRequirements(&wide_region, &.{load_source}));
    try testing.expectEqual(try ss.selectedRequirements(&region.bytes, &.{temperature_source}), try ss.selectedRequirements(&wide_region, &.{temperature_source}));
}

test "a selected copy holds dense tables over compact data" {
    var region = Region{};
    try region.init();
    const process_rows = try region.writeRows(&.{ 100, 200, 300 }, &.{ "init", "shell", "game" });
    const adapter_rows = try region.writeAdapters(&.{ 7, 8 }, &.{ 5, 0, 95 }, process_rows);
    try region.writeLoad(&.{ 40, 60 }, adapter_rows);
    try region.writeGpu(&.{ 1, 2, 3, 4 });

    var storage = Storage{};
    const needs = try ss.selectedRequirements(&region.bytes, &.{load_source});
    storage.bind(needs);
    try storage.read(&region.bytes, &.{load_source}, ss.default_retry_limit);
    try testing.expectEqual(@as(u32, 3), storage.copy.source_length);
    try testing.expectEqual(@as(u32, 6), storage.copy.position_length);
    try testing.expectEqualSlices(u32, &.{ rows_source, adapter_source, load_source }, storage.source_indices[0..3]);
    try testing.expectEqualSlices(u32, &.{ ids, names, name_table, adapters, gpu_share, adapter_load }, storage.position_indices[0..6]);
    for (storage.sources[0..3], storage.source_indices[0..3]) |held, source| {
        try testing.expectEqual(ss.CopyStatus.has_value, held.status);
        try testing.expectEqual(source, held.source);
    }
    try testing.expectEqual(adapter_rows, storage.sources[1].sequence);
    try testing.expectEqual(@as(i64, 15), storage.sources[2].write_time_unix_ms);

    // Sources, identities and string tables are dense indexes; offsets are compact.
    const dense = storage.positions[0..6];
    try testing.expectEqual(ss.PositionSpec{ .source = 0, .kind = .string_column, .offset = 64, .size = 64, .element_width = 8, .identity = 0, .strings = 2 }, dense[1].spec);
    try testing.expectEqual(ss.PositionSpec{ .source = 1, .kind = .column, .offset = 256, .size = 32, .element_width = 8 }, dense[3].spec);
    try testing.expectEqual(ss.PositionSpec{ .source = 1, .kind = .column, .offset = 288, .size = 32, .element_width = 4, .identity = 0 }, dense[4].spec);
    try testing.expectEqual(ss.PositionSpec{ .source = 2, .kind = .column, .offset = 320, .size = 16, .element_width = 4, .identity = 3 }, dense[5].spec);
    try testing.expectEqual(adapter_rows, dense[5].identity_sequence);

    // Values come by region position index; their offsets index the copy's data.
    const load = try storage.found(adapter_load);
    try testing.expectEqual(@as(u64, 320), load.offset);
    try expectU32s(&.{ 40, 60 }, load.bytes);
    try testing.expectEqual(@as(u64, 320), switch (ss.selectedPositionValue(&storage.copy, adapter_load)) {
        .value => |result| result.offset,
        else => return error.NoValue,
    });
    try expectU32s(&.{ 5, 0, 95 }, (try storage.found(gpu_share)).bytes);
    try expectU64s(&.{ 7, 8 }, (try storage.found(adapters)).bytes);
    try expectU64s(&.{ 100, 200, 300 }, (try storage.found(ids)).bytes);
    try testing.expectEqualStrings("shell", try storage.string(names, 1));
    try testing.expectEqual(ss.PositionValue{ .failed = .not_selected }, storage.value(gpu));
    try testing.expectEqual(ss.PositionValue{ .failed = .not_selected }, storage.value(cpu));
    try testing.expectEqual(ss.PositionValue{ .failed = .not_selected }, storage.value(position_count));
}

test "an unchanged source copies nothing and a changed one rewrites only its own extents" {
    var region = Region{};
    try region.init();
    const process_rows = try region.writeRows(&.{ 1, 2 }, &.{ "a", "b" });
    const adapter_rows = try region.writeAdapters(&.{9}, &.{ 50, 50 }, process_rows);
    try region.writeLoad(&.{70}, adapter_rows);
    var storage = Storage{};
    storage.bind(try ss.selectedRequirements(&region.bytes, &.{load_source}));
    try storage.read(&region.bytes, &.{load_source}, 1);

    @memset(&storage.data, 0xAA);
    try storage.read(&region.bytes, &.{load_source}, 1);
    for (storage.data) |byte| try testing.expectEqual(@as(u8, 0xAA), byte);

    // New adapter rows: only the adapters source's extents (256..320) are copied.
    _ = try region.writeAdapters(&.{ 9, 10 }, &.{ 30, 70 }, process_rows);
    try storage.read(&region.bytes, &.{load_source}, 1);
    for (storage.data[0..256]) |byte| try testing.expectEqual(@as(u8, 0xAA), byte);
    for (storage.data[320..]) |byte| try testing.expectEqual(@as(u8, 0xAA), byte);
    try expectU64s(&.{ 9, 10 }, (try storage.found(adapters)).bytes);
    try expectU32s(&.{ 30, 70 }, (try storage.found(gpu_share)).bytes);
    // The load column was written against the old adapter rows.
    try testing.expectEqual(@as(ss.PositionValue, .not_arrived), storage.value(adapter_load));
}

test "a column pairs only with the identity rows the copy holds" {
    var region = Region{};
    try region.init();
    var storage = Storage{};
    storage.bind(try ss.selectedRequirements(&region.bytes, &.{metrics_source}));
    try storage.read(&region.bytes, &.{metrics_source}, 1);
    try testing.expectEqual(ss.PositionValue{ .unavailable = ss.not_written_reason }, storage.value(cpu));

    const r1 = try region.writeRows(&.{ 10, 20, 30 }, &.{ "x", "y", "z" });
    try region.writeCpu(&.{ 1, 2, 3 }, r1);
    try storage.read(&region.bytes, &.{metrics_source}, 1);
    try expectU32s(&.{ 1, 2, 3 }, (try storage.found(cpu)).bytes);
    try expectU64s(&.{ 11, 22 }, (try storage.found(totals)).bytes);

    // New rows: the cpu column still belongs to the old ones.
    const r2 = try region.writeRows(&.{ 10, 20, 30, 40 }, &.{ "x", "y", "z", "w" });
    try storage.read(&region.bytes, &.{metrics_source}, 1);
    try testing.expectEqual(@as(ss.PositionValue, .not_arrived), storage.value(cpu));
    try expectU64s(&.{ 11, 22 }, (try storage.found(totals)).bytes);
    try region.writeCpu(&.{ 4, 5, 6, 7 }, r2);
    try storage.read(&region.bytes, &.{metrics_source}, 1);
    try expectU32s(&.{ 4, 5, 6, 7 }, (try storage.found(cpu)).bytes);

    // Written against the right rows with another count: a mismatch, not a value.
    try region.writeCpu(&.{ 4, 5 }, r2);
    try storage.read(&region.bytes, &.{metrics_source}, 1);
    try testing.expectEqual(ss.PositionValue{ .failed = .rows_mismatch }, storage.value(cpu));
}

test "the same selection and layout keep the copy; another selection or layout resets it" {
    var region = Region{};
    try region.init();
    try region.writeGpu(&.{ 1, 2, 3, 4 });
    try region.writeTemperatures(&.{ 40, 41, 42 });
    var storage = Storage{};
    const both = [_]u32{ gpu_source, temperature_source };
    const needs = try ss.selectedRequirements(&region.bytes, &both);
    storage.bind(needs);
    try storage.read(&region.bytes, &both, 1);
    const gpu_sequence = storage.sources[0].sequence;

    // A selection that is the prefix of the held one: its entries are what a
    // fresh copy would hold, so they are kept and nothing is copied.
    @memset(&storage.data, 0xAA);
    try storage.read(&region.bytes, &.{gpu_source}, 1);
    try testing.expectEqual(@as(u32, 1), storage.copy.source_length);
    try testing.expectEqual(@as(u32, 1), storage.copy.position_length);
    for (storage.data) |byte| try testing.expectEqual(@as(u8, 0xAA), byte);
    try testing.expectEqual(gpu_sequence, storage.sources[0].sequence);

    // Another selection in the same memory: rewritten and read afresh.
    storage.bind(needs);
    try storage.read(&region.bytes, &.{temperature_source}, 1);
    try testing.expectEqual(temperature_source, storage.source_indices[0]);
    const read_temperatures = try storage.found(temperatures);
    try testing.expectEqual(@as(u64, 0), read_temperatures.offset);
    try expectU32s(&.{ 40, 41, 42 }, read_temperatures.bytes);

    // Back to both: reset again, both read.
    storage.bind(needs);
    try storage.read(&region.bytes, &both, 1);
    try expectU64s(&.{ 1, 2, 3, 4 }, (try storage.found(gpu)).bytes);
    try testing.expectEqual(@as(u64, 32), (try storage.found(temperatures)).offset);

    // A layout change of a selected position (gpu now reserves 24 bytes) resets.
    region.poke32(specField(gpu, "size"), 24);
    const narrowed = try ss.selectedRequirements(&region.bytes, &both);
    try testing.expectEqual(@as(u64, 48), narrowed.data_bytes);
    @memset(&storage.data, 0xAA);
    try storage.read(&region.bytes, &both, 1);
    try expectU64s(&.{ 1, 2, 3 }, (try storage.found(gpu)).bytes);
    const moved = try storage.found(temperatures);
    try testing.expectEqual(@as(u64, 24), moved.offset);
    try expectU32s(&.{ 40, 41, 42 }, moved.bytes);
    try testing.expectEqual(@as(u32, 24), storage.positions[1].spec.offset);

    // Unchanged again from here on.
    @memset(&storage.data, 0xAA);
    try storage.read(&region.bytes, &both, 1);
    for (storage.data) |byte| try testing.expectEqual(@as(u8, 0xAA), byte);
}

test "a source held odd fails at the retry limit while the others are read; limit 0 tries once" {
    var region = Region{};
    try region.init();
    try region.writeGpu(&.{ 1, 2, 3, 4 });
    try region.writeTemperatures(&.{40});
    var storage = Storage{};
    const both = [_]u32{ gpu_source, temperature_source };
    storage.bind(try ss.selectedRequirements(&region.bytes, &both));
    var open = try ss.Writer.begin(&region.bytes, temperature_source);
    try storage.read(&region.bytes, &both, 0);
    try testing.expectEqual(ss.CopyStatus.failed, storage.sources[1].status);
    try testing.expectEqual(@intFromEnum(ss.Failure.retry_limit), storage.sources[1].reason);
    try testing.expectEqual(ss.PositionValue{ .failed = .retry_limit }, storage.value(temperatures));
    try expectU64s(&.{ 1, 2, 3, 4 }, (try storage.found(gpu)).bytes);
    try storage.read(&region.bytes, &both, 3);
    try testing.expectEqual(ss.PositionValue{ .failed = .retry_limit }, storage.value(temperatures));

    try open.store(u32, temperatures, 0, &.{ 50, 51 });
    try open.setLength(temperatures, 2, 0);
    try open.end(.has_value, 99);
    try storage.read(&region.bytes, &both, 0);
    try expectU32s(&.{ 50, 51 }, (try storage.found(temperatures)).bytes);
    try testing.expectEqual(@as(i64, 99), storage.sources[1].write_time_unix_ms);
}

test "a structural failure leaves no usable value in the copy" {
    var region = Region{};
    try region.init();
    try region.writeGpu(&.{ 1, 2, 3, 4 });
    try region.writeTemperatures(&.{40});
    var storage = Storage{};
    const both = [_]u32{ gpu_source, temperature_source };
    const needs = try ss.selectedRequirements(&region.bytes, &both);
    storage.bind(needs);
    try storage.read(&region.bytes, &both, 1);
    _ = try storage.found(gpu);

    // A broken header.
    region.poke32(@offsetOf(ss.Header, "magic"), 0);
    try testing.expectError(error.BadMagic, storage.read(&region.bytes, &both, 1));
    try testing.expectEqual(ss.PositionValue{ .failed = .bad_magic }, storage.value(gpu));
    try testing.expectEqual(ss.PositionValue{ .failed = .bad_magic }, storage.value(temperatures));
    region.poke32(@offsetOf(ss.Header, "magic"), ss.magic);
    try storage.read(&region.bytes, &both, 1);
    try expectU64s(&.{ 1, 2, 3, 4 }, (try storage.found(gpu)).bytes);

    // Memory too small for the selection: refused before any value is copied.
    storage.copy.data_length = needs.data_bytes - 1;
    @memset(&storage.data, 0xAA);
    try testing.expectError(error.CopyTooSmall, storage.read(&region.bytes, &both, 1));
    try testing.expectEqual(ss.PositionValue{ .failed = .copy_too_small }, storage.value(gpu));
    for (storage.data) |byte| try testing.expectEqual(@as(u8, 0xAA), byte);
    storage.bind(needs);
    storage.copy.source_length = 1;
    try testing.expectError(error.CopyTooSmall, storage.read(&region.bytes, &both, 1));
    storage.bind(needs);
    storage.copy.position_indices = null;
    try testing.expectError(error.InvalidArgument, storage.read(&region.bytes, &both, 1));
    try testing.expectEqual(ss.PositionValue{ .failed = .invalid_argument }, storage.value(gpu));

    // A broken position of a selected source fails the call; one of an
    // unselected source is never looked at.
    storage.bind(needs);
    region.poke32(specField(ids, "offset"), 1000);
    try storage.read(&region.bytes, &both, 1);
    try expectU64s(&.{ 1, 2, 3, 4 }, (try storage.found(gpu)).bytes);
    try testing.expectError(error.PositionRange, ss.selectedRequirements(&region.bytes, &.{metrics_source}));
    region.poke32(specField(temperatures, "offset"), 1000);
    try testing.expectError(error.PositionRange, storage.read(&region.bytes, &both, 1));
    try testing.expectEqual(ss.PositionValue{ .failed = .position_range }, storage.value(gpu));
    try testing.expectError(error.PositionRange, ss.selectedRequirements(&region.bytes, &both));
    // An identity whose owner is outside the source table.
    region.poke32(specField(temperatures, "offset"), 336);
    region.poke32(specField(adapters, "source"), 77);
    try testing.expectError(error.PositionSource, ss.selectedRequirements(&region.bytes, &.{load_source}));
}

test "malformed dynamic content fails only the source it is in" {
    var region = Region{};
    try region.init();
    const process_rows = try region.writeRows(&.{ 1, 2 }, &.{ "a", "b" });
    try region.writeCpu(&.{ 3, 4 }, process_rows);
    var storage = Storage{};
    storage.bind(try ss.selectedRequirements(&region.bytes, &.{metrics_source}));

    region.poke32(positionField(cpu, "length"), 9);
    try storage.read(&region.bytes, &.{metrics_source}, 1);
    try testing.expectEqual(ss.PositionValue{ .failed = .length_over_capacity }, storage.value(totals));
    try expectU64s(&.{ 1, 2 }, (try storage.found(ids)).bytes);
    try testing.expectEqualStrings("b", try storage.string(names, 1));

    // A string row past its table's written bytes fails the rows source.
    region.poke32(positionField(cpu, "length"), 2);
    region.poke32(positionField(name_table, "length"), 1);
    try region.writeCpu(&.{ 3, 4 }, process_rows);
    var writer = try ss.Writer.begin(&region.bytes, rows_source);
    try writer.setLength(name_table, 1, 0);
    const rewritten = writer.roundSequence();
    try writer.end(.has_value, 0);
    try storage.read(&region.bytes, &.{metrics_source}, 1);
    try testing.expectEqual(ss.PositionValue{ .failed = .string_range }, storage.value(ids));
    // The cpu column was written against the earlier rows.
    try testing.expect(rewritten != process_rows);
    try testing.expectEqual(@as(ss.PositionValue, .not_arrived), storage.value(cpu));
}

test "a selected copy reports unavailable and keeps nothing of an ended source" {
    var region = Region{};
    try region.init();
    try region.writeGpu(&.{ 1, 2, 3, 4 });
    var writer = try ss.Writer.begin(&region.bytes, temperature_source);
    try writer.end(.{ .unavailable = 17 }, 5);
    var storage = Storage{};
    const both = [_]u32{ gpu_source, temperature_source };
    storage.bind(try ss.selectedRequirements(&region.bytes, &both));
    try storage.read(&region.bytes, &both, 1);
    try testing.expectEqual(ss.PositionValue{ .unavailable = 17 }, storage.value(temperatures));
    try expectU64s(&.{ 1, 2, 3, 4 }, (try storage.found(gpu)).bytes);

    try ss.end(&region.bytes, 6);
    try storage.read(&region.bytes, &both, 1);
    try testing.expectEqual(@as(ss.PositionValue, .ended), storage.value(gpu));
    try testing.expectEqual(@as(ss.PositionValue, .ended), storage.value(temperatures));
    for (storage.data[0..56]) |byte| try testing.expectEqual(@as(u8, 0), byte);
    try testing.expectEqual(@as(u32, 0), storage.positions[0].length);
}

test "a selection holds at most the source limit" {
    const count = ss.selected_source_limit + 1;
    const size: usize = @intCast(ss.requiredSize(count, 0, 0));
    const region = try testing.allocator.alignedAlloc(u8, .@"8", size);
    defer testing.allocator.free(region);
    const sources = try testing.allocator.alloc(ss.SourceIdentity, count);
    defer testing.allocator.free(sources);
    @memset(sources, .{});
    try ss.initialize(region, 1, count, 0, 0, sources, &.{});
    const named = try testing.allocator.alloc(u32, count);
    defer testing.allocator.free(named);
    for (named, 0..) |*source, index| source.* = @intCast(index);

    try testing.expectError(error.SelectionTooLarge, ss.selectedRequirements(region, named));
    const needs = try ss.selectedRequirements(region, named[0..ss.selected_source_limit]);
    try testing.expectEqual(ss.SelectedRequirements{ .source_count = ss.selected_source_limit }, needs);
    const held = try testing.allocator.alloc(ss.SourceCopy, count);
    defer testing.allocator.free(held);
    @memset(held, .{});
    const indices = try testing.allocator.alloc(u32, count);
    defer testing.allocator.free(indices);
    var copy = ss.SelectedCopy{ .sources = held.ptr, .source_indices = indices.ptr, .source_length = ss.selected_source_limit };
    try ss.readSelected(region, named[0..ss.selected_source_limit], &copy, 1);
    for (held[0..ss.selected_source_limit]) |entry| try testing.expectEqual(ss.CopyStatus.unavailable, entry.status);
}

// Rows (0) are rewritten with a new count every round; a column writer (1)
// reads them through its own selected copy and writes each id doubled against
// them; a reader of the column's source (whose selection adds the rows) must
// never pair a column with other rows, nor see a torn round.
const id_rows = 64;
const pairing_ids: u32 = 0;
const doubled: u32 = 1;
const pairing_data_capacity: u32 = id_rows * 8 * 2;

fn word(bytes: []const u8, index: usize) u64 {
    return std.mem.readInt(u64, bytes[index * 8 ..][0..8], .little);
}

const PairingWriters = struct {
    region: []align(8) u8,
    rounds: u64,
    rows_done: std.atomic.Value(bool) = .init(false),
    columns_done: std.atomic.Value(bool) = .init(false),
    failed: std.atomic.Value(bool) = .init(false),

    fn runRows(self: *PairingWriters) void {
        self.writeRows() catch self.failed.store(true, .release);
        self.rows_done.store(true, .release);
    }
    fn writeRows(self: *PairingWriters) !void {
        var values: [id_rows]u64 = undefined;
        var round: u64 = 1;
        while (round <= self.rounds) : (round += 1) {
            const count: usize = @intCast(1 + round % id_rows);
            for (values[0..count], 0..) |*id, index| id.* = round * 1000 + index;
            var writer = try ss.Writer.begin(self.region, 0);
            try writer.store(u64, pairing_ids, 0, values[0..count]);
            try writer.setLength(pairing_ids, @intCast(count), 0);
            try writer.end(.has_value, 0);
        }
    }

    fn runColumns(self: *PairingWriters) void {
        self.writeColumns() catch self.failed.store(true, .release);
        self.columns_done.store(true, .release);
    }
    fn writeColumns(self: *PairingWriters) !void {
        var data: [id_rows * 8]u8 = [_]u8{0} ** (id_rows * 8);
        var held = [_]ss.SourceCopy{.{}};
        var entries = std.mem.zeroes([1]ss.PositionEntry);
        var source_index = [_]u32{0};
        var position_index = [_]u32{0};
        var copy = ss.SelectedCopy{ .data = &data, .data_length = data.len, .sources = &held, .positions = &entries, .source_indices = &source_index, .position_indices = &position_index, .source_length = 1, .position_length = 1 };
        var values: [id_rows]u64 = undefined;
        while (true) {
            // A pass that starts after the rows are final writes against them and stops.
            const final = self.rows_done.load(.acquire);
            try ss.readSelected(self.region, &.{0}, &copy, ss.default_retry_limit);
            if (held[0].status != .has_value) {
                if (final) return error.FinalRowsUnreadable;
                continue;
            }
            const rows_value = switch (ss.selectedPositionValue(&copy, pairing_ids)) {
                .value => |found| found,
                else => return error.UnexpectedValue,
            };
            for (values[0..rows_value.length], 0..) |*number, index| number.* = word(rows_value.bytes, index) * 2;
            var writer = try ss.Writer.begin(self.region, 1);
            try writer.store(u64, doubled, 0, values[0..rows_value.length]);
            try writer.setLength(doubled, rows_value.length, held[0].sequence);
            try writer.end(.has_value, 0);
            if (final) return;
        }
    }
};

test "a selected reader concurrent with two sources never pairs a column with other rows" {
    var region: [@intCast(ss.requiredSize(2, 2, pairing_data_capacity))]u8 align(8) = undefined;
    const identity = [_]ss.SourceIdentity{ try ss.SourceIdentity.init("rows", 1, 0, 0), try ss.SourceIdentity.init("doubler", 2, 0, 0) };
    const pairing_positions = [_]ss.PositionSpec{ ss.PositionSpec.column(0, 0, id_rows, 8, ss.none), ss.PositionSpec.column(1, id_rows * 8, id_rows, 8, pairing_ids) };
    try ss.initialize(&region, 2, 2, 2, pairing_data_capacity, &identity, &pairing_positions);
    // The reader names only the column's source; the selection adds the rows.
    const needs = try ss.selectedRequirements(&region, &.{1});
    try testing.expectEqual(ss.SelectedRequirements{ .data_bytes = pairing_data_capacity, .source_count = 2, .position_count = 2 }, needs);
    var data = [_]u8{0} ** pairing_data_capacity;
    var held = [_]ss.SourceCopy{ .{}, .{} };
    var entries = std.mem.zeroes([2]ss.PositionEntry);
    var source_indices = [_]u32{ 0, 0 };
    var position_indices = [_]u32{ 0, 0 };
    var copy = ss.SelectedCopy{ .data = &data, .data_length = data.len, .sources = &held, .positions = &entries, .source_indices = &source_indices, .position_indices = &position_indices, .source_length = 2, .position_length = 2 };

    var writers = PairingWriters{ .region = &region, .rounds = 20_000 };
    const rows_thread = try std.Thread.spawn(.{}, PairingWriters.runRows, .{&writers});
    defer rows_thread.join();
    const columns_thread = try std.Thread.spawn(.{}, PairingWriters.runColumns, .{&writers});
    defer columns_thread.join();
    var paired: u64 = 0;
    var finished = false;
    while (!finished) {
        finished = writers.columns_done.load(.acquire);
        try ss.readSelected(&region, &.{1}, &copy, ss.default_retry_limit);
        for (held) |entry| {
            if (entry.status == .failed) try testing.expectEqual(@intFromEnum(ss.Failure.retry_limit), entry.reason);
        }
        const column_value = switch (ss.selectedPositionValue(&copy, doubled)) {
            .value => |found| found,
            .not_arrived, .unavailable => continue,
            .failed => |failure| {
                try testing.expectEqual(ss.Failure.retry_limit, failure);
                continue;
            },
            .ended => return error.UnexpectedValue,
        };
        // Arrived means written against exactly the rows the copy holds.
        const rows_value = switch (ss.selectedPositionValue(&copy, pairing_ids)) {
            .value => |found| found,
            else => return error.UnexpectedValue,
        };
        try testing.expectEqual(rows_value.length, column_value.length);
        for (0..rows_value.length) |index| {
            const id = word(rows_value.bytes, index);
            // One round's rows: every id carries the same round number.
            try testing.expectEqual(word(rows_value.bytes, 0) / 1000, id / 1000);
            try testing.expectEqual(id * 2, word(column_value.bytes, index));
        }
        paired += 1;
    }
    try testing.expect(!writers.failed.load(.acquire));
    // The final pass wrote against the final rows, so the last read paired them.
    try testing.expect(paired > 0);
    switch (ss.selectedPositionValue(&copy, doubled)) {
        .value => |found| try testing.expectEqual(@as(u32, @intCast(1 + writers.rounds % id_rows)), found.length),
        else => return error.NoValue,
    }
}

test "random corruption of a written region never crashes a selected read" {
    var prng = std.Random.DefaultPrng.init(0x5E1E_C7ED_2026);
    const random = prng.random();
    const subsets = [_][]const u32{ &.{load_source}, &.{metrics_source}, &.{ gpu_source, temperature_source }, &.{ 0, 1, 2, 3, 4, 5 }, &.{rows_source} };
    var iteration: usize = 0;
    while (iteration < 20_000) : (iteration += 1) {
        var region = Region{};
        try region.init();
        const process_rows = try region.writeRows(&.{ 1, 2, 3 }, &.{ "a", "bb", "ccc" });
        try region.writeCpu(&.{ 4, 5, 6 }, process_rows);
        const adapter_rows = try region.writeAdapters(&.{ 7, 8 }, &.{ 1, 2, 3 }, process_rows);
        try region.writeLoad(&.{ 9, 10 }, adapter_rows);
        try region.writeGpu(&.{ 1, 2, 3, 4 });
        try region.writeTemperatures(&.{ 1, 2 });
        var storage = Storage{};
        const subset = subsets[random.uintLessThan(usize, subsets.len)];
        storage.bind(try ss.selectedRequirements(&region.bytes, subset));
        if (random.boolean()) try storage.read(&region.bytes, subset, 2);

        const flips = random.intRangeLessThan(usize, 1, 9);
        for (0..flips) |_| region.bytes[random.uintLessThan(usize, region_size)] = random.int(u8);
        const length = if (random.uintLessThan(u8, 4) == 0) random.uintLessThan(usize, region_size + 1) else region_size;
        const bytes = region.bytes[0..length];
        _ = ss.selectedRequirements(bytes, subset) catch {};
        storage.read(bytes, subset, 2) catch {};
        for (0..position_count + 1) |position| _ = storage.value(@intCast(position));
        // A copy sized on the corrupted region, with all the room it might need.
        storage.copy.data_length = data_capacity;
        storage.copy.source_length = source_count;
        storage.copy.position_length = position_count;
        storage.read(bytes, subset, 2) catch {};
        for (0..position_count + 1) |position| _ = storage.value(@intCast(position));
        if (random.boolean()) ss.end(bytes, 0) catch {};
        storage.read(bytes, subset, 2) catch {};
        for (0..position_count + 1) |position| _ = storage.value(@intCast(position));
    }
}

fn code(failure: ss.Failure) u32 {
    return @intFromEnum(failure);
}

test "the selected read C ABI round-trips" {
    var region = Region{};
    try region.init();
    const length: u64 = region_size;
    const process_rows = try region.writeRows(&.{ 5, 6 }, &.{ "p", "q" });
    try region.writeCpu(&.{ 70, 80 }, process_rows);
    const named = [_]u32{metrics_source};

    var needs: ss.SelectedRequirements = undefined;
    try testing.expectEqual(@as(u32, 0), ss.rm_shared_section_selected_required_size(&region.bytes, length, &named, 1, &needs));
    try testing.expectEqual(ss.SelectedRequirements{ .data_bytes = 304, .source_count = 2, .position_count = 5 }, needs);
    try testing.expectEqual(code(.invalid_argument), ss.rm_shared_section_selected_required_size(&region.bytes, length, &named, 1, null));
    try testing.expectEqual(code(.invalid_argument), ss.rm_shared_section_selected_required_size(&region.bytes, length, null, 1, &needs));
    try testing.expectEqual(code(.selection_empty), ss.rm_shared_section_selected_required_size(&region.bytes, length, null, 0, &needs));
    try testing.expectEqual(code(.invalid_argument), ss.rm_shared_section_selected_required_size(null, length, &named, 1, &needs));
    try testing.expectEqual(code(.region_misaligned), ss.rm_shared_section_selected_required_size(region.bytes[1..].ptr, length - 1, &named, 1, &needs));
    try testing.expectEqual(code(.selection_order), ss.rm_shared_section_selected_required_size(&region.bytes, length, &[_]u32{ 1, 0 }, 2, &needs));

    var storage = Storage{};
    storage.bind(needs);
    try testing.expectEqual(@as(u32, 0), ss.rm_shared_section_read_selected(&region.bytes, length, &named, 1, &storage.copy, 0));
    var view: ss.ValueView = undefined;
    try testing.expectEqual(@intFromEnum(ss.PositionStatus.has_value), ss.rm_shared_section_selected_position_value(&storage.copy, cpu, &view));
    try testing.expectEqual(ss.ValueView{ .offset = 256, .byte_length = 8, .length = 2, .status = .has_value }, view);
    try expectU32s(&.{ 70, 80 }, storage.data[256..264]);
    try testing.expectEqual(@intFromEnum(ss.PositionStatus.has_value), ss.rm_shared_section_selected_position_value(&storage.copy, totals, &view));
    try testing.expectEqual(@as(u32, 2), view.length);
    try testing.expectEqual(@intFromEnum(ss.PositionStatus.failed), ss.rm_shared_section_selected_position_value(&storage.copy, gpu, &view));
    try testing.expectEqual(code(.not_selected), view.reason);
    try testing.expectEqual(@intFromEnum(ss.PositionStatus.failed), ss.rm_shared_section_selected_position_value(null, cpu, &view));
    try testing.expectEqual(code(.invalid_argument), view.reason);
    try testing.expectEqual(@intFromEnum(ss.PositionStatus.failed), ss.rm_shared_section_selected_position_value(&storage.copy, cpu, null));

    // A structural code marks every held source failed.
    try testing.expectEqual(code(.invalid_argument), ss.rm_shared_section_read_selected(&region.bytes, length, &named, 1, null, 0));
    try testing.expectEqual(code(.invalid_argument), ss.rm_shared_section_read_selected(&region.bytes, length, null, 1, &storage.copy, 0));
    try testing.expectEqual(@intFromEnum(ss.PositionStatus.failed), ss.rm_shared_section_selected_position_value(&storage.copy, cpu, &view));
    try testing.expectEqual(code(.invalid_argument), view.reason);
    try testing.expectEqual(code(.region_too_small), ss.rm_shared_section_read_selected(null, 0, &named, 1, &storage.copy, 0));
    try testing.expectEqual(code(.region_too_small), storage.sources[0].reason);
    try testing.expectEqual(code(.region_too_small), ss.rm_shared_section_read_selected(&region.bytes, 16, &named, 1, &storage.copy, 0));
    try testing.expectEqual(@as(u32, 0), ss.rm_shared_section_read_selected(&region.bytes, length, &named, 1, &storage.copy, 0));
    try testing.expectEqual(@intFromEnum(ss.PositionStatus.has_value), ss.rm_shared_section_selected_position_value(&storage.copy, cpu, &view));
}

test "selected read C ABI structs have the documented layout" {
    try testing.expectEqual(@as(usize, 16), @sizeOf(ss.SelectedRequirements));
    try testing.expectEqual(@as(usize, 8), @offsetOf(ss.SelectedRequirements, "source_count"));
    try testing.expectEqual(@as(usize, 12), @offsetOf(ss.SelectedRequirements, "position_count"));
    try testing.expectEqual(@as(usize, 56), @sizeOf(ss.SelectedCopy));
    const offsets = [_]usize{ 0, 8, 16, 24, 32, 40, 48, 52 };
    const fields = .{ "data", "data_length", "sources", "positions", "source_indices", "position_indices", "source_length", "position_length" };
    inline for (fields, 0..) |field, index| try testing.expectEqual(offsets[index], @offsetOf(ss.SelectedCopy, field));
    try testing.expectEqual(@as(usize, 32), @sizeOf(ss.SourceCopy));
    try testing.expectEqual(@as(usize, 24), @offsetOf(ss.SourceCopy, "source"));
    try testing.expectEqual(@as(usize, 40), @sizeOf(ss.PositionEntry));
    try testing.expectEqual(@as(u32, 35), code(.selection_empty));
    try testing.expectEqual(@as(u32, 36), code(.selection_too_large));
    try testing.expectEqual(@as(u32, 33), code(.layout_changed));
}
