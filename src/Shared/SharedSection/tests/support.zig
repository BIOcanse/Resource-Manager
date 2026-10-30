const std = @import("std");
pub const ss = @import("rm_shared_section");

/// A resource-table-like fixture. Source 0 owns the rows (identity column ids,
/// a names string column over them and its string table); source 1 owns a cpu
/// column over the same rows and a two-element totals scalar.
pub const data_kind: u32 = 7;
pub const rows_source: u32 = 0;
pub const metrics_source: u32 = 1;
pub const ids: u32 = 0;
pub const names: u32 = 1;
pub const name_table: u32 = 2;
pub const cpu: u32 = 3;
pub const totals: u32 = 4;
pub const row_capacity = 8;
pub const data_capacity: u32 = 64 + 64 + 128 + 32 + 16;
pub const region_size: usize = @intCast(ss.requiredSize(2, 5, data_capacity));

pub const positions = [_]ss.PositionSpec{
    ss.PositionSpec.column(rows_source, 0, row_capacity, 8, ss.none),
    ss.PositionSpec.stringColumn(rows_source, 64, row_capacity, ids, name_table),
    ss.PositionSpec.stringTable(rows_source, 128, 128),
    ss.PositionSpec.column(metrics_source, 256, row_capacity, 4, ids),
    ss.PositionSpec.scalar(metrics_source, 288, 16, 8),
};

pub fn identities() ![2]ss.SourceIdentity {
    return .{
        try ss.SourceIdentity.init("process-table", 1, 0, 0),
        try ss.SourceIdentity.init("cpu-sampler", 2, 4242, 133_000_000_000_000_000),
    };
}

/// Byte offsets of region fields, for tests that corrupt a region directly.
pub fn sourceField(source: u32, comptime field: []const u8) usize {
    return @intCast(ss.layout.header_size + source * ss.layout.source_entry_size + @offsetOf(ss.SourceEntry, field));
}
pub fn positionField(position: u32, comptime field: []const u8) usize {
    return @intCast(ss.layout.positionTableOffset(2) + position * ss.layout.position_entry_size + @offsetOf(ss.PositionEntry, field));
}
pub fn specField(position: u32, comptime field: []const u8) usize {
    return positionField(position, "spec") + @offsetOf(ss.PositionSpec, field);
}
pub fn dataField(offset: u32) usize {
    return @as(usize, @intCast(ss.layout.dataOffset(2, 5))) + offset;
}

/// Both sources selected: the compact layout then equals the data area's, since
/// the fixture's positions are contiguous in table order.
pub const both_sources = [_]u32{ rows_source, metrics_source };

pub const Fixture = struct {
    region: [region_size]u8 align(8) = undefined,
    data: [data_capacity]u8 = undefined,
    sources: [2]ss.SourceCopy = undefined,
    positions: [5]ss.SelectedPosition = undefined,
    selection: ss.Selection = .{},

    /// The fixture must not move afterwards: the selection points into it.
    pub fn init(self: *Fixture) !void {
        const sources = try identities();
        try ss.initialize(&self.region, data_kind, 2, 5, data_capacity, &sources, &positions);
        self.selection = try ss.Selection.init(&self.region, &both_sources, &self.sources, &self.positions, &self.data);
    }
    pub fn bytes(self: *Fixture) []align(8) u8 {
        return &self.region;
    }
    pub fn read(self: *Fixture, source: u32) ss.SourceRead {
        return ss.read(&self.region, &self.selection, source, ss.default_retry_limit);
    }
    pub fn value(self: *Fixture, position: u32) ss.PositionValue {
        return ss.positionValue(&self.selection, position);
    }
    pub fn bytesOf(self: *Fixture, position: u32) ![]const u8 {
        return switch (self.value(position)) {
            .value => |found| found.bytes,
            else => error.NoValue,
        };
    }
    pub fn poke32(self: *Fixture, offset: usize, number: u32) void {
        std.mem.writeInt(u32, self.region[offset..][0..4], number, .little);
    }
    pub fn poke64(self: *Fixture, offset: usize, number: u64) void {
        std.mem.writeInt(u64, self.region[offset..][0..8], number, .little);
    }

    /// One rows round; returns the sequence it published.
    pub fn writeRows(self: *Fixture, row_ids: []const u64, row_names: []const []const u8) !u64 {
        var writer = try ss.Writer.begin(self.bytes(), rows_source);
        try writer.store(u64, ids, 0, row_ids);
        try writer.setLength(ids, @intCast(row_ids.len), 0);
        try writer.setLength(name_table, 0, 0);
        for (row_names, 0..) |name, row| try writer.appendString(names, @intCast(row), name);
        try writer.setLength(names, @intCast(row_names.len), writer.roundSequence());
        const published = writer.roundSequence();
        try writer.end(.has_value, 1000);
        return published;
    }

    /// One metrics round, written against the rows of identity sequence `against`.
    pub fn writeCpu(self: *Fixture, values: []const u32, against: u64) !void {
        var writer = try ss.Writer.begin(self.bytes(), metrics_source);
        try writer.store(u32, cpu, 0, values);
        try writer.setLength(cpu, @intCast(values.len), against);
        try writer.store(u64, totals, 0, &.{ 11, 22 });
        try writer.end(.has_value, 2000);
    }
};
