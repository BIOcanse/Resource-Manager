const std = @import("std");
const series = @import("series.zig");

const fast = series.Options{
    .series_capacity = 4,
    .maximum_holders = 8,
    .minimum_samples = 3,
    .minimum_seconds = 2,
    .handle_floor = 1000,
    .minimum_growth = 100,
    .edge_percentage = 25,
};
fn sample(pid: i32, key: []const u8, value: u64) series.Sample {
    var result = series.Sample{
        .pid = pid,
        .kind = 0,
        .birth = std.fmt.parseInt(u64, key, 10) catch 0,
        .value = value,
        .key = @splat(0),
        .key_length = @intCast(key.len),
    };
    @memcpy(result.key[0..key.len], key);
    return result;
}

test "recorded sequences retain the original floor and first to last evidence" {
    const cases = [_]struct { values: [3]u64, suspected: bool }{
        .{ .values = .{ 1000, 1100, 1200 }, .suspected = true },
        .{ .values = .{ 1200, 1400, 1200 }, .suspected = false },
        .{ .values = .{ 800, 900, 1000 }, .suspected = false },
        .{ .values = .{ 1200, 1200, 1200 }, .suspected = false },
    };
    for (cases) |case| {
        const memory = try std.testing.allocator.alignedAlloc(u8, .@"8", series.requiredBytes(fast));
        defer std.testing.allocator.free(memory);
        const owner = try series.init(memory, fast);
        var suspects: [8]series.Suspect = undefined;
        var judged: [8]series.Key = undefined;
        for (case.values, 0..) |value, second| {
            const result = try owner.observe(@as(i64, @intCast(second)) * 10_000_000,
                &.{sample(7, "42", value)}, &suspects, &judged);
            try std.testing.expectEqual(@as(usize, if (second == 2 and case.suspected) 1 else 0), result.suspect_count);
            try std.testing.expectEqual(@as(usize, if (second == 2) 1 else 0), result.judged_count);
        }
        if (case.suspected) {
            try std.testing.expectEqual(@as(u64, 200), suspects[0].growth);
            try std.testing.expectEqual(@as(f64, 2), suspects[0].seconds);
            try std.testing.expectEqual(@as(f64, 100), suspects[0].rate);
            try std.testing.expectEqual(@as(u32, 3), suspects[0].sample_count);
        }
    }
}

test "bounded ring drops the old high fallback and preserves diagnostic byte semantics" {
    const memory = try std.testing.allocator.alignedAlloc(u8, .@"8", series.requiredBytes(fast));
    defer std.testing.allocator.free(memory);
    const owner = try series.init(memory, fast);
    var suspects: [8]series.Suspect = undefined;
    var judged: [8]series.Key = undefined;
    const values = [_]u64{ 1400, 1000, 1100, 1200, 1300 };
    for (values, 0..) |value, second| {
        const result = try owner.observe(@as(i64, @intCast(second)) * 10_000_000,
            &.{sample(7, "42", value)}, &suspects, &judged);
        try std.testing.expectEqual(@as(usize, if (second == 4) 1 else 0), result.suspect_count);
    }
    try std.testing.expectEqual(@as(u64, 64), owner.bytes());
    try std.testing.expectEqual(@as(u64, 64), owner.peakBytes());
    owner.clear();
    try std.testing.expectEqual(@as(usize, 0), owner.count());
    try std.testing.expectEqual(@as(u64, 0), owner.bytes());
    try std.testing.expectEqual(@as(u64, 64), owner.peakBytes());
}

test "sustained disjoint edges tolerate noise but veto a return to the early band" {
    const cases = [_]struct { values: [8]u64, suspected: bool }{
        .{ .values = .{ 1000, 1030, 1100, 1099, 1200, 1195, 1300, 1297 }, .suspected = true },
        .{ .values = .{ 1000, 1030, 1200, 1250, 1245, 1200, 1005, 1000 }, .suspected = false },
        .{ .values = .{ 1000, 1030, 1100, 1200, 1300, 1400, 1000, 1700 }, .suspected = false },
    };
    var options = fast;
    options.series_capacity = 8;
    options.minimum_samples = 8;
    options.minimum_seconds = 7;
    for (cases) |case| {
        const memory = try std.testing.allocator.alignedAlloc(u8, .@"8", series.requiredBytes(options));
        defer std.testing.allocator.free(memory);
        const owner = try series.init(memory, options);
        var suspects: [8]series.Suspect = undefined;
        var judged: [8]series.Key = undefined;
        for (case.values, 0..) |value, second| {
            const result = try owner.observe(@as(i64, @intCast(second)) * 10_000_000,
                &.{sample(7, "42", value)}, &suspects, &judged);
            try std.testing.expectEqual(@as(usize, if (second == 7 and case.suspected) 1 else 0), result.suspect_count);
        }
        if (case.suspected) {
            try std.testing.expectEqual(@as(u64, 297), suspects[0].growth);
            try std.testing.expectEqual(@as(f64, 297.0 / 7.0), suspects[0].rate);
        }
    }
}

test "edge percentage chooses the original conservative band" {
    for ([_]u32{ 25, 50 }) |percentage| {
        var options = fast;
        options.series_capacity = 8;
        options.minimum_samples = 8;
        options.minimum_seconds = 7;
        options.edge_percentage = percentage;
        const memory = try std.testing.allocator.alignedAlloc(u8, .@"8", series.requiredBytes(options));
        defer std.testing.allocator.free(memory);
        const owner = try series.init(memory, options);
        var suspects: [8]series.Suspect = undefined;
        var judged: [8]series.Key = undefined;
        const values = [_]u64{ 1000, 1030, 1250, 1300, 1250, 1290, 1400, 1450 };
        for (values, 0..) |value, second| {
            const result = try owner.observe(@as(i64, @intCast(second)) * 10_000_000,
                &.{sample(7, "42", value)}, &suspects, &judged);
            if (second == 7) try std.testing.expectEqual(@as(usize, if (percentage == 25) 1 else 0), result.suspect_count);
        }
    }
}

test "PID reuse and disappearance remove only matching exact identities" {
    const memory = try std.testing.allocator.alignedAlloc(u8, .@"8", series.requiredBytes(fast));
    defer std.testing.allocator.free(memory);
    const owner = try series.init(memory, fast);
    var suspects: [8]series.Suspect = undefined;
    var judged: [8]series.Key = undefined;
    _ = try owner.observe(0, &.{ sample(7, "42", 1000), sample(7, "042", 1000) }, &suspects, &judged);
    try std.testing.expectEqual(@as(usize, 2), owner.count());
    _ = try owner.observe(10_000_000, &.{sample(7, "43", 2000)}, &suspects, &judged);
    try std.testing.expectEqual(@as(usize, 1), owner.count());
    const result = try owner.observe(20_000_000, &.{sample(7, "43", 2200)}, &suspects, &judged);
    try std.testing.expectEqual(@as(usize, 0), result.suspect_count);
    _ = try owner.observe(30_000_000, &.{}, &suspects, &judged);
    try std.testing.expectEqual(@as(usize, 0), owner.count());
}

test "cache inputs are excluded and malformed frames are explicit" {
    const memory = try std.testing.allocator.alignedAlloc(u8, .@"8", series.requiredBytes(fast));
    defer std.testing.allocator.free(memory);
    const owner = try series.init(memory, fast);
    var suspects: [8]series.Suspect = undefined;
    var judged: [8]series.Key = undefined;
    var cache = sample(0, "0", 5000);
    cache.kind = 1;
    _ = try owner.observe(0, &.{cache}, &suspects, &judged);
    try std.testing.expectEqual(@as(usize, 0), owner.count());
    try std.testing.expectError(error.InvalidObservation, owner.observe(0,
        &.{ sample(7, "42", 1000), sample(7, "42", 1200) }, &suspects, &judged));
    try std.testing.expectEqual(@as(usize, 0), owner.count());
    try std.testing.expectError(error.InvalidObservation, owner.observe(0, &.{sample(7, "0", 1000)}, &suspects, &judged));
    try std.testing.expectError(error.InvalidObservation, owner.observe(0, &.{sample(0, "42", 1000)}, &suspects, &judged));
    _ = try owner.observe(0, &.{sample(7, "42", 1000)}, &suspects, &judged);
    try std.testing.expectError(error.InvalidObservation, owner.observe(0, &.{sample(7, "42", 1100)}, &suspects, &judged));
    const oversized: [9]series.Sample = @splat(cache);
    try std.testing.expectError(error.CapacityExceeded, owner.observe(10_000_000, &oversized, &suspects, &judged));
    try std.testing.expectError(error.BufferTooSmall, owner.observe(10_000_000, &.{sample(7, "42", 1100)}, &.{}, &judged));
}

test "the series region can relocate between scoped Ledger uses" {
    const memory = try std.testing.allocator.alignedAlloc(u8, .@"8", series.requiredBytes(fast));
    defer std.testing.allocator.free(memory);
    const moved = try std.testing.allocator.alignedAlloc(u8, .@"8", memory.len);
    defer std.testing.allocator.free(moved);
    const owner = try series.init(memory, fast);
    var suspects: [8]series.Suspect = undefined;
    var judged: [8]series.Key = undefined;
    _ = try owner.observe(0, &.{sample(7, "42", 1000)}, &suspects, &judged);
    _ = try owner.observe(10_000_000, &.{sample(7, "42", 1100)}, &suspects, &judged);
    @memcpy(moved, memory);
    const relocated: *series.Series = @ptrCast(moved.ptr);
    const result = try relocated.observe(20_000_000, &.{sample(7, "42", 1200)}, &suspects, &judged);
    try std.testing.expectEqual(@as(usize, 1), result.suspect_count);
    try std.testing.expectEqual(@as(u64, 200), suspects[0].growth);
}

test "invalid options and undersized workspace fail before initialization" {
    var memory: [64]u8 align(8) = undefined;
    try std.testing.expectError(error.BufferTooSmall, series.init(&memory, fast));
    var options = fast;
    options.edge_percentage = 0;
    try std.testing.expectEqual(@as(usize, 0), series.requiredBytes(options));
    try std.testing.expectError(error.InvalidOptions, series.init(&memory, options));
}
