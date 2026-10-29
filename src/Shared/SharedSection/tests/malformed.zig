const std = @import("std");
const support = @import("support.zig");
const ss = support.ss;
const testing = std.testing;

/// Both sources written, not yet read: a read after a corruption checks it.
fn written(f: *support.Fixture) !void {
    try f.init();
    const rows = try f.writeRows(&.{ 1, 2, 3 }, &.{ "a", "b", "c" });
    try f.writeCpu(&.{ 4, 5, 6 }, rows);
}

/// Every source read fails with `failure`, and so does every position.
fn expectFailed(f: *support.Fixture, failure: ss.Failure) !void {
    for ([_]u32{ support.rows_source, support.metrics_source }) |source| {
        try testing.expectEqual(ss.SourceRead{ .failed = failure }, f.read(source));
        try testing.expectEqual(@intFromEnum(failure), f.sources[source].reason);
    }
    for (0..5) |position| try testing.expectEqual(ss.PositionValue{ .failed = failure }, f.value(@intCast(position)));
}

test "a malformed header fails every read with its reason" {
    const cases = [_]struct { field: usize, number: u32, failure: ss.Failure }{
        .{ .field = @offsetOf(ss.Header, "magic"), .number = 0x1234, .failure = .bad_magic },
        .{ .field = @offsetOf(ss.Header, "version"), .number = 2, .failure = .bad_version },
        .{ .field = @offsetOf(ss.Header, "source_count"), .number = 3, .failure = .count_over_capacity },
        .{ .field = @offsetOf(ss.Header, "position_count"), .number = 6, .failure = .count_over_capacity },
        .{ .field = @offsetOf(ss.Header, "data_capacity"), .number = 300, .failure = .data_capacity },
        .{ .field = @offsetOf(ss.Header, "data_capacity"), .number = 4096, .failure = .region_too_small },
        .{ .field = @offsetOf(ss.Header, "source_capacity"), .number = 0xFFFF_FFFF, .failure = .region_too_small },
        .{ .field = @offsetOf(ss.Header, "position_capacity"), .number = 0xFFFF_FFFF, .failure = .region_too_small },
    };
    for (cases) |case| {
        var f = support.Fixture{};
        try written(&f);
        f.poke32(case.field, case.number);
        try expectFailed(&f, case.failure);
        if (ss.describe(f.bytes())) |_| return error.TestUnexpectedResult else |err| try testing.expectEqual(case.failure, ss.failureOf(err));
        if (ss.Writer.begin(f.bytes(), 0)) |_| return error.TestUnexpectedResult else |err| try testing.expectEqual(case.failure, ss.failureOf(err));
        if (ss.end(f.bytes(), 0)) |_| return error.TestUnexpectedResult else |err| try testing.expectEqual(case.failure, ss.failureOf(err));
    }
}

test "a region shorter than its header or its capacities fails" {
    var f = support.Fixture{};
    try written(&f);
    try testing.expectEqual(ss.SourceRead{ .failed = .region_too_small }, ss.read(f.region[0..16], &f.copy, 0, 1));
    try testing.expectEqual(ss.SourceRead{ .failed = .region_too_small }, ss.read(f.region[0 .. support.region_size - 8], &f.copy, 0, 1));
    try testing.expectEqual(ss.SourceRead{ .failed = .region_too_small }, ss.read(f.region[0..0], &f.copy, 1, 1));
    try testing.expectError(error.RegionTooSmall, ss.Writer.begin(f.region[0..100], 0));
    try testing.expectError(error.RegionTooSmall, ss.end(f.region[0..100], 0));
}

test "a malformed position table fails every read with its reason" {
    const cases = [_]struct { position: u32, field: []const u8, number: u32, failure: ss.Failure }{
        .{ .position = support.cpu, .field = "offset", .number = 1000, .failure = .position_range },
        .{ .position = support.cpu, .field = "offset", .number = 260, .failure = .position_range },
        .{ .position = support.cpu, .field = "size", .number = 30, .failure = .position_range },
        .{ .position = support.totals, .field = "size", .number = 0xFFFF_FFF8, .failure = .position_range },
        .{ .position = support.cpu, .field = "source", .number = 5, .failure = .position_source },
        .{ .position = support.cpu, .field = "source", .number = ss.none, .failure = .position_source },
        .{ .position = support.cpu, .field = "kind", .number = 9, .failure = .position_kind },
        .{ .position = support.cpu, .field = "kind", .number = 0, .failure = .position_kind },
        .{ .position = support.cpu, .field = "element_width", .number = 3, .failure = .position_width },
        .{ .position = support.cpu, .field = "element_width", .number = 0, .failure = .position_width },
        .{ .position = support.names, .field = "element_width", .number = 4, .failure = .position_width },
        .{ .position = support.name_table, .field = "element_width", .number = 8, .failure = .position_width },
        .{ .position = support.cpu, .field = "identity", .number = support.cpu, .failure = .position_identity },
        .{ .position = support.cpu, .field = "identity", .number = support.totals, .failure = .position_identity },
        .{ .position = support.cpu, .field = "identity", .number = support.names, .failure = .position_identity },
        .{ .position = support.cpu, .field = "identity", .number = 77, .failure = .position_identity },
        .{ .position = support.totals, .field = "identity", .number = support.ids, .failure = .position_identity },
        .{ .position = support.names, .field = "strings", .number = support.cpu, .failure = .position_strings },
        .{ .position = support.names, .field = "strings", .number = ss.none, .failure = .position_strings },
        .{ .position = support.name_table, .field = "source", .number = support.metrics_source, .failure = .position_strings },
        .{ .position = support.cpu, .field = "strings", .number = support.name_table, .failure = .position_strings },
    };
    inline for (cases) |case| {
        var f = support.Fixture{};
        try written(&f);
        f.poke32(support.specField(case.position, case.field), case.number);
        try expectFailed(&f, case.failure);
        if (ss.describePosition(f.bytes(), case.position)) |_| {} else |err| try testing.expectEqual(case.failure, ss.failureOf(err));
        // A writer refuses a malformed position too.
        var writer = try ss.Writer.begin(f.bytes(), support.metrics_source);
        const stored = writer.store(u32, support.cpu, 0, &.{1});
        if (case.position == support.cpu) {
            if (stored) |_| return error.TestUnexpectedResult else |err| try testing.expectEqual(case.failure, ss.failureOf(err));
        }
        try writer.end(.has_value, 0);
    }
}

test "malformed dynamic content fails the source that wrote it" {
    {
        var f = support.Fixture{};
        try written(&f);
        f.poke32(support.positionField(support.ids, "length"), support.row_capacity + 1);
        try testing.expectEqual(ss.SourceRead{ .failed = .length_over_capacity }, f.read(support.rows_source));
        try testing.expectEqual(ss.PositionValue{ .failed = .length_over_capacity }, f.value(support.ids));
        // The other source still reads; its column has no rows to pair with.
        try testing.expect(f.read(support.metrics_source) == .value);
        try testing.expectEqual(@as(ss.PositionValue, .not_arrived), f.value(support.cpu));
        try testing.expect(f.value(support.totals) == .value);
    }
    {
        var f = support.Fixture{};
        try written(&f);
        f.poke32(support.positionField(support.name_table, "length"), 129);
        try testing.expectEqual(ss.SourceRead{ .failed = .length_over_capacity }, f.read(support.rows_source));
    }
    {
        var f = support.Fixture{};
        try written(&f);
        // Row 1 of names points past the three bytes its table holds.
        f.poke64(support.dataField(64 + 8), 2 | (5 << 32));
        try testing.expectEqual(ss.SourceRead{ .failed = .string_range }, f.read(support.rows_source));
        f.poke64(support.dataField(64 + 8), 0xFFFF_FFFF_FFFF_FFFF);
        try testing.expectEqual(ss.SourceRead{ .failed = .string_range }, f.read(support.rows_source));
    }
    {
        var f = support.Fixture{};
        try written(&f);
        f.poke32(support.sourceField(support.metrics_source, "state"), 7);
        try testing.expectEqual(ss.SourceRead{ .failed = .source_state }, f.read(support.metrics_source));
        try testing.expectEqual(ss.PositionValue{ .failed = .source_state }, f.value(support.totals));
        f.poke32(support.sourceField(support.metrics_source, "state"), 0);
        try testing.expectEqual(ss.SourceRead{ .failed = .source_state }, f.read(support.metrics_source));
    }
    {
        var f = support.Fixture{};
        try written(&f);
        f.poke32(support.sourceField(support.metrics_source, "identity") + @offsetOf(ss.SourceIdentity, "owner_name_length"), 65);
        try testing.expectError(error.OwnerName, ss.describeSource(f.bytes(), support.metrics_source));
        try testing.expectEqualStrings("process-table", (try ss.describeSource(f.bytes(), support.rows_source)).owner());
        try testing.expectError(error.SourceIndex, ss.describeSource(f.bytes(), 2));
        try testing.expectError(error.PositionIndex, ss.describePosition(f.bytes(), 5));
    }
}

test "a bad source index or a short copy fails without touching other sources" {
    var f = support.Fixture{};
    try written(&f);
    try testing.expect(f.read(support.rows_source) == .value);
    try testing.expectEqual(ss.SourceRead{ .failed = .source_index }, f.read(2));
    try testing.expectEqual(ss.SourceRead{ .failed = .source_index }, f.read(ss.none));
    try testing.expect(f.value(support.ids) == .value);
    var short = ss.Copy.init(f.data[0..100], &f.sources, &f.positions);
    try testing.expectEqual(ss.SourceRead{ .failed = .copy_too_small }, ss.read(f.bytes(), &short, 0, 1));
    var few = ss.Copy.init(&f.data, f.sources[0..1], &f.positions);
    try testing.expectEqual(ss.SourceRead{ .failed = .copy_too_small }, ss.read(f.bytes(), &few, 0, 1));
    var empty = ss.Copy{};
    try testing.expectEqual(ss.SourceRead{ .failed = .copy_too_small }, ss.read(f.bytes(), &empty, 0, 1));
    try testing.expectEqual(ss.PositionValue{ .failed = .position_index }, ss.positionValue(&empty, 0));
}

fn expectRejected(expected: ss.Error, region: []align(8) u8, sources: []const ss.SourceIdentity, positions: []const ss.PositionSpec, source_capacity: u32, data_capacity: u32) !void {
    try testing.expectError(expected, ss.initialize(region, support.data_kind, source_capacity, 5, data_capacity, sources, positions));
    for (region) |byte| try testing.expectEqual(@as(u8, 0x5A), byte);
}

test "laying out rejects an invalid layout before writing anything" {
    var region: [support.region_size]u8 align(8) = undefined;
    @memset(&region, 0x5A);
    const sources = try support.identities();
    const capacity = support.data_capacity;

    var overlap = support.positions;
    overlap[support.totals].offset = 280;
    try expectRejected(error.PositionOverlap, &region, &sources, &overlap, 2, capacity);
    var missing = support.positions;
    missing[support.cpu].source = 2;
    try expectRejected(error.PositionSource, &region, &sources, &missing, 2, capacity);
    var chained = support.positions;
    chained[support.cpu].identity = support.names;
    try expectRejected(error.PositionIdentity, &region, &sources, &chained, 2, capacity);
    var foreign = support.positions;
    foreign[support.name_table].source = support.metrics_source;
    try expectRejected(error.PositionStrings, &region, &sources, &foreign, 2, capacity);
    var outside = support.positions;
    outside[support.totals].offset = capacity;
    try expectRejected(error.PositionRange, &region, &sources, &outside, 2, capacity);

    try expectRejected(error.CountOverCapacity, &region, &sources, &support.positions, 1, capacity);
    try expectRejected(error.DataCapacity, &region, &sources, &support.positions, 2, capacity - 4);
    try expectRejected(error.RegionTooSmall, &region, &sources, &support.positions, 2, capacity + 8);
    try expectRejected(error.RegionTooSmall, region[0..500], &sources, &support.positions, 2, capacity);
    var long_name = sources;
    long_name[1].owner_name_length = ss.owner_name_capacity + 1;
    try expectRejected(error.OwnerName, &region, &long_name, &support.positions, 2, capacity);
    try testing.expectError(error.OwnerName, ss.SourceIdentity.init(&([_]u8{'n'} ** 65), 1, 0, 0));
}

test "random corruption of a written region never crashes a reader, writer or end" {
    var prng = std.Random.DefaultPrng.init(0x5EED_2026_1029);
    const random = prng.random();
    var iteration: usize = 0;
    while (iteration < 20_000) : (iteration += 1) {
        var f = support.Fixture{};
        try written(&f);
        if (random.boolean()) {
            _ = f.read(support.rows_source);
            _ = f.read(support.metrics_source);
        }
        const flips = random.intRangeLessThan(usize, 1, 9);
        for (0..flips) |_| f.region[random.uintLessThan(usize, support.region_size)] = random.int(u8);
        const length = if (random.uintLessThan(u8, 4) == 0) random.uintLessThan(usize, support.region_size + 1) else support.region_size;
        const region = f.region[0..length];
        for (0..3) |source| _ = ss.read(region, &f.copy, @intCast(source), 2);
        for (0..6) |position| {
            _ = ss.positionValue(&f.copy, @intCast(position));
            for (0..support.row_capacity + 1) |row| _ = ss.stringAt(&f.copy, @intCast(position), @intCast(row));
        }
        _ = ss.describe(region) catch {};
        for (0..3) |source| _ = ss.describeSource(region, @intCast(source)) catch {};
        for (0..6) |position| _ = ss.describePosition(region, @intCast(position)) catch {};
        if (ss.Writer.begin(region, random.uintLessThan(u32, 3))) |opened| {
            var writer = opened;
            for (0..6) |position| {
                writer.store(u32, @intCast(position), random.uintLessThan(u32, 10), &.{ 1, 2 }) catch {};
                writer.store(u64, @intCast(position), random.uintLessThan(u32, 10), &.{3}) catch {};
                writer.setLength(@intCast(position), random.uintLessThan(u32, 10), 0) catch {};
                writer.appendString(@intCast(position), random.uintLessThan(u32, 10), "corrupt") catch {};
            }
            writer.end(.has_value, 0) catch {};
        } else |_| {}
        if (random.boolean()) ss.end(region, 0) catch {};
        for (0..3) |source| _ = ss.read(region, &f.copy, @intCast(source), 2);
    }
}
