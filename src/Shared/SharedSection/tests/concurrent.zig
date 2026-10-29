const std = @import("std");
const ss = @import("rm_shared_section");
const testing = std.testing;

fn word(bytes: []const u8, index: usize) u64 {
    return std.mem.readInt(u64, bytes[index * 8 ..][0..8], .little);
}
fn valueOf(copy: *const ss.Copy, position: u32) !ss.Value {
    return switch (ss.positionValue(copy, position)) {
        .value => |found| found,
        else => error.NoValue,
    };
}

// One source writes a 512-row column whose every element is the round number
// and a pair {round, ~round}. A torn copy would mix rounds.
const rows = 512;
const column: u32 = 0;
const pair: u32 = 1;
const torn_data_capacity: u32 = rows * 8 + 16;

const RoundWriter = struct {
    region: []align(8) u8,
    rounds: u64,
    done: std.atomic.Value(bool) = .init(false),
    failed: std.atomic.Value(bool) = .init(false),

    fn run(self: *RoundWriter) void {
        self.writeAll() catch self.failed.store(true, .release);
        self.done.store(true, .release);
    }
    fn writeAll(self: *RoundWriter) !void {
        var values: [rows]u64 = undefined;
        var round: u64 = 1;
        while (round <= self.rounds) : (round += 1) {
            @memset(&values, round);
            var writer = try ss.Writer.begin(self.region, 0);
            try writer.store(u64, column, 0, &values);
            try writer.setLength(column, rows, writer.roundSequence());
            try writer.store(u64, pair, 0, &.{ round, ~round });
            try writer.end(.has_value, @intCast(round));
        }
    }
};

test "a reader concurrent with a writer never returns a torn round" {
    var region: [@intCast(ss.requiredSize(1, 2, torn_data_capacity))]u8 align(8) = undefined;
    const identity = [_]ss.SourceIdentity{try ss.SourceIdentity.init("round-writer", 1, 0, 0)};
    const positions = [_]ss.PositionSpec{ ss.PositionSpec.column(0, 0, rows, 8, ss.none), ss.PositionSpec.scalar(0, rows * 8, 16, 8) };
    try ss.initialize(&region, 1, 1, 2, torn_data_capacity, &identity, &positions);
    var data: [torn_data_capacity]u8 = undefined;
    var sources: [1]ss.SourceCopy = undefined;
    var entries: [2]ss.PositionEntry = undefined;
    var copy = ss.Copy.init(&data, &sources, &entries);

    var writer = RoundWriter{ .region = &region, .rounds = 20_000 };
    const thread = try std.Thread.spawn(.{}, RoundWriter.run, .{&writer});
    defer thread.join();
    var last: u64 = 0;
    var consistent: u64 = 0;
    var finished = false;
    while (!finished) {
        // One more read after the writer is done.
        finished = writer.done.load(.acquire);
        switch (ss.read(&region, &copy, 0, ss.default_retry_limit)) {
            .value => {},
            // Before the first round the source is still not written.
            .unchanged, .unavailable => continue,
            .failed => |failure| {
                try testing.expectEqual(ss.Failure.retry_limit, failure);
                continue;
            },
            .ended => return error.UnexpectedRead,
        }
        const values = try valueOf(&copy, column);
        const halves = try valueOf(&copy, pair);
        const round = word(halves.bytes, 0);
        try testing.expectEqual(~round, word(halves.bytes, 1));
        try testing.expectEqual(@as(u32, rows), values.length);
        for (0..rows) |index| try testing.expectEqual(round, word(values.bytes, index));
        try testing.expectEqual(@as(i64, @intCast(round)), sources[0].write_time_unix_ms);
        try testing.expect(round >= last);
        last = round;
        consistent += 1;
    }
    try testing.expect(!writer.failed.load(.acquire));
    try testing.expectEqual(writer.rounds, last);
    try testing.expect(consistent > 0);
}

// Source 0 defines rows; source 1 writes, against the rows it read, each id
// doubled. The reader must never pair a doubled column with other rows.
const id_rows = 64;
const ids: u32 = 0;
const doubled: u32 = 1;
const pairing_data_capacity: u32 = id_rows * 8 * 2;

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
            try writer.store(u64, ids, 0, values[0..count]);
            try writer.setLength(ids, @intCast(count), 0);
            try writer.end(.has_value, 0);
        }
    }

    fn runColumns(self: *PairingWriters) void {
        self.writeColumns() catch self.failed.store(true, .release);
        self.columns_done.store(true, .release);
    }
    fn writeColumns(self: *PairingWriters) !void {
        var data: [pairing_data_capacity]u8 = undefined;
        var sources: [2]ss.SourceCopy = undefined;
        var entries: [2]ss.PositionEntry = undefined;
        var copy = ss.Copy.init(&data, &sources, &entries);
        var values: [id_rows]u64 = undefined;
        while (true) {
            // A pass that starts after the rows are final writes against them and stops.
            const final = self.rows_done.load(.acquire);
            const sequence = switch (ss.read(self.region, &copy, 0, ss.default_retry_limit)) {
                .value => |published| published,
                .unchanged => sources[0].sequence,
                .failed, .unavailable => if (final) return error.FinalRowsUnreadable else continue,
                else => return error.UnexpectedRead,
            };
            const rows_value = try valueOf(&copy, ids);
            for (values[0..rows_value.length], 0..) |*number, index| number.* = word(rows_value.bytes, index) * 2;
            var writer = try ss.Writer.begin(self.region, 1);
            try writer.store(u64, doubled, 0, values[0..rows_value.length]);
            try writer.setLength(doubled, rows_value.length, sequence);
            try writer.end(.has_value, 0);
            if (final) return;
        }
    }
};

test "a reader concurrent with two sources never pairs a column with other rows" {
    var region: [@intCast(ss.requiredSize(2, 2, pairing_data_capacity))]u8 align(8) = undefined;
    const identity = [_]ss.SourceIdentity{ try ss.SourceIdentity.init("rows", 1, 0, 0), try ss.SourceIdentity.init("doubler", 2, 0, 0) };
    const positions = [_]ss.PositionSpec{ ss.PositionSpec.column(0, 0, id_rows, 8, ss.none), ss.PositionSpec.column(1, id_rows * 8, id_rows, 8, ids) };
    try ss.initialize(&region, 2, 2, 2, pairing_data_capacity, &identity, &positions);
    var data: [pairing_data_capacity]u8 = undefined;
    var sources: [2]ss.SourceCopy = undefined;
    var entries: [2]ss.PositionEntry = undefined;
    var copy = ss.Copy.init(&data, &sources, &entries);

    var writers = PairingWriters{ .region = &region, .rounds = 20_000 };
    const rows_thread = try std.Thread.spawn(.{}, PairingWriters.runRows, .{&writers});
    defer rows_thread.join();
    const columns_thread = try std.Thread.spawn(.{}, PairingWriters.runColumns, .{&writers});
    defer columns_thread.join();
    var paired: u64 = 0;
    var finished = false;
    while (!finished) {
        finished = writers.columns_done.load(.acquire);
        for (0..2) |source| {
            switch (ss.read(&region, &copy, @intCast(source), ss.default_retry_limit)) {
                .value, .unchanged, .unavailable => {},
                .failed => |failure| try testing.expectEqual(ss.Failure.retry_limit, failure),
                .ended => return error.UnexpectedRead,
            }
        }
        const column_value = switch (ss.positionValue(&copy, doubled)) {
            .value => |found| found,
            .not_arrived, .unavailable => continue,
            .failed => |failure| {
                try testing.expectEqual(ss.Failure.retry_limit, failure);
                continue;
            },
            .ended => return error.UnexpectedValue,
        };
        // Arrived means written against exactly the rows the copy holds.
        const rows_value = try valueOf(&copy, ids);
        try testing.expectEqual(rows_value.length, column_value.length);
        for (0..rows_value.length) |index| try testing.expectEqual(word(rows_value.bytes, index) * 2, word(column_value.bytes, index));
        paired += 1;
    }
    try testing.expect(!writers.failed.load(.acquire));
    // The final pass wrote against the final rows, so the last read paired them.
    try testing.expect(paired > 0);
    try testing.expectEqual(@as(u32, @intCast(1 + writers.rounds % id_rows)), (try valueOf(&copy, doubled)).length);
}
