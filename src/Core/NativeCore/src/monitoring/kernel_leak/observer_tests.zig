const std = @import("std");
const observer = @import("observer_abi.zig");
const series = @import("series.zig");

const configuration = observer.Configuration{
    .options = .{
        .series_capacity = 4,
        .maximum_holders = 8,
        .minimum_samples = 3,
        .minimum_seconds = 2,
        .handle_floor = 1000,
        .minimum_growth = 100,
        .edge_percentage = 25,
    },
    .interval = 1000,
    .item_capacity = 8,
};
const second: i64 = 10_000_000;

fn sample(value: u64) series.Sample {
    var key: [20]u8 = @splat(0);
    @memcpy(key[0..2], "42");
    return .{ .pid = 7, .kind = 0, .birth = 42, .value = value, .key = key, .key_length = 2 };
}
fn frame(ticks: i64, rows: []const series.Sample) observer.Frame {
    return .{ .ticks = ticks, .available = 1, .unavailable_count = 0, .samples = rows.ptr, .sample_count = rows.len, .unavailable = null, .unavailable_length = 0 };
}
const Event = union(enum) { frame: observer.Frame, code: u32 };
const Harness = struct {
    events: []const Event,
    position: usize = 0,
    reports: [16]observer.Snapshot = undefined,
    report_count: usize = 0,
    starts: usize = 0,
    waits: usize = 0,
    last_interval: u32 = 0,
    clock: i64 = 70 * second,
    monotonic_ms: f64 = 0,
    record_suspects: bool = true,
    cancel_at_start: bool = false,
    last_suspect: ?series.Suspect = null,

    fn get(context: ?*anyopaque) *Harness {
        return @ptrCast(@alignCast(context.?));
    }
    fn next(context: ?*anyopaque, output: *observer.Frame) callconv(.c) u32 {
        const self = get(context);
        if (self.position == self.events.len) return 2;
        const event = self.events[self.position];
        self.position += 1;
        switch (event) {
            .frame => |value| {
                output.* = value;
                return 0;
            },
            .code => |code| return code,
        }
    }
    fn prepare(context: ?*anyopaque, report: *observer.Snapshot, suspects: ?[*]const series.Suspect, count: usize, _: ?[*]const series.Key, _: usize) callconv(.c) u32 {
        const self = get(context);
        report.recorded_suspects = if (self.record_suspects) @intCast(count) else 0;
        report.unrecorded = if (self.record_suspects) 0 else @intCast(count);
        report.attribution_milliseconds = 1;
        if (count != 0) self.last_suspect = suspects.?[count - 1];
        return 0;
    }
    fn publish(context: ?*anyopaque, report: *const observer.Snapshot) callconv(.c) u32 {
        const self = get(context);
        if (self.report_count == self.reports.len) return 6;
        self.reports[self.report_count] = report.*;
        self.report_count += 1;
        return 0;
    }
    fn started(context: ?*anyopaque) callconv(.c) u32 {
        const self = get(context);
        self.starts += 1;
        return if (self.cancel_at_start) 2 else 0;
    }
    fn wait(context: ?*anyopaque, interval: u32) callconv(.c) u32 {
        const self = get(context);
        self.waits += 1;
        self.last_interval = interval;
        return 0;
    }
    fn now(context: ?*anyopaque) callconv(.c) i64 {
        return get(context).clock;
    }
    fn monotonic(context: ?*anyopaque) callconv(.c) f64 {
        const self = get(context);
        self.monotonic_ms += 1;
        return self.monotonic_ms;
    }
    fn callbacks(self: *Harness) observer.Callbacks {
        return .{ .context = self, .next = next, .prepare = prepare, .publish = publish, .started = started, .wait = wait, .now = now, .clock = monotonic };
    }
};

fn run(harness: *Harness) !void {
    const options = configuration.options;
    const bytes = std.mem.alignForward(usize, series.requiredBytes(options), 8) +
        options.maximum_holders * (@sizeOf(series.Suspect) + @sizeOf(series.Key));
    const memory = try std.testing.allocator.alignedAlloc(u8, .@"8", bytes);
    defer std.testing.allocator.free(memory);
    const callbacks = harness.callbacks();
    try std.testing.expectEqual(@as(u32, 0), observer.rm_kernel_leak_run(memory.ptr, memory.len, &configuration, &callbacks));
    const state: *const series.Series = @ptrCast(memory.ptr);
    try std.testing.expectEqual(@as(usize, 0), state.count());
    try std.testing.expectEqual(@as(u64, 0), state.bytes());
}

test "observer ABI layouts match x64 reference boundary" {
    try std.testing.expectEqual(@as(usize, 48), @sizeOf(observer.Frame));
    try std.testing.expectEqual(@as(usize, 128), @sizeOf(observer.Snapshot));
    try std.testing.expectEqual(@as(usize, 48), @sizeOf(observer.Configuration));
    try std.testing.expectEqual(@as(usize, 64), @sizeOf(observer.Callbacks));
    try std.testing.expectEqual(@as(usize, 8), @alignOf(observer.Frame));
    try std.testing.expectEqual(@as(usize, 8), @alignOf(observer.Snapshot));
}

test "native run cancellation clears series and publishes final stopped progress" {
    const rows = [_]series.Sample{sample(1000)};
    const events = [_]Event{.{ .frame = frame(0, &rows) }};
    var harness = Harness{ .events = &events };
    try run(&harness);
    try std.testing.expectEqual(@as(usize, 1), harness.starts);
    try std.testing.expectEqual(@as(usize, 3), harness.report_count);
    try std.testing.expectEqual(@as(u32, 1), harness.reports[1].holders);
    const stopped = harness.reports[2];
    try std.testing.expectEqual(@as(u32, 0), stopped.status);
    try std.testing.expectEqual(@as(u32, 0), stopped.failure);
    try std.testing.expectEqual(@as(u32, 0), stopped.has_start);
    try std.testing.expectEqual(@as(u32, 0), stopped.samples);
    try std.testing.expectEqual(@as(u32, 0), stopped.holders);
    try std.testing.expectEqual(@as(u64, 0), stopped.series_bytes);
    try std.testing.expectEqual(@as(u64, 64), stopped.peak_bytes);
}

test "startup cancellation still publishes final stopped report" {
    var harness = Harness{ .events = &.{}, .cancel_at_start = true };
    try run(&harness);
    try std.testing.expectEqual(@as(usize, 2), harness.report_count);
    try std.testing.expectEqual(@as(u32, 1), harness.reports[0].status);
    try std.testing.expectEqual(@as(u32, 0), harness.reports[1].status);
}

test "native progress moves from empty to suspected only when prepare records the evidence" {
    for ([_]bool{ true, false }) |record| {
        const rows = [_][1]series.Sample{
            .{sample(1000)}, .{sample(1000)}, .{sample(1000)}, .{sample(1100)}, .{sample(1200)},
        };
        const events = [_]Event{
            .{ .frame = frame(0, &rows[0]) },          .{ .frame = frame(second, &rows[1]) },
            .{ .frame = frame(2 * second, &rows[2]) }, .{ .frame = frame(3 * second, &rows[3]) },
            .{ .frame = frame(4 * second, &rows[4]) },
        };
        var harness = Harness{ .events = &events, .record_suspects = record };
        try run(&harness);
        try std.testing.expectEqual(@as(u32, 2), harness.reports[3].status);
        try std.testing.expectEqual(@as(u32, if (record) 3 else 2), harness.reports[5].status);
        try std.testing.expectEqual(@as(u32, if (record) 0 else 1), harness.reports[5].unrecorded);
        try std.testing.expectEqual(@as(u32, 5), harness.reports[5].samples);
        try std.testing.expectEqual(@as(f64, 4), harness.reports[5].elapsed);
        try std.testing.expectEqual(@as(f64, 1), harness.reports[5].judgment_milliseconds);
        try std.testing.expectEqual(@as(f64, 1), harness.reports[5].attribution_milliseconds);
        try std.testing.expectEqual(@as(u64, 200), harness.last_suspect.?.growth);
        try std.testing.expectEqual(@as(u32, 4), harness.last_suspect.?.sample_count);
    }
}

test "source retries keep the failed run first cause and timestamp" {
    const rows = [_]series.Sample{sample(1000)};
    const duplicates = [_]series.Sample{ sample(1100), sample(1100) };
    const events = [_]Event{
        .{ .frame = frame(0, &rows) }, .{ .frame = frame(second, &duplicates) },
        .{ .code = 4 },                .{ .frame = frame(2 * second, &.{}) },
    };
    var harness = Harness{ .events = &events };
    try run(&harness);
    try std.testing.expectEqual(@as(u32, 3), harness.reports[2].failure);
    try std.testing.expectEqual(@as(f64, 0), harness.reports[2].judgment_milliseconds);
    try std.testing.expectEqual(@as(f64, 0), harness.reports[2].attribution_milliseconds);
    try std.testing.expectEqual(@as(u32, 2), harness.reports[3].failure);
    const recovered = harness.reports[4];
    try std.testing.expectEqual(@as(u32, 1), recovered.has_previous);
    try std.testing.expectEqual(@as(i64, 0), recovered.previous_start);
    try std.testing.expectEqual(second, recovered.previous_at);
    try std.testing.expectEqual(@as(u32, 3), recovered.previous_failure);
    try std.testing.expectEqual(2 * second, recovered.started);
    try std.testing.expectEqual(@as(usize, 1), harness.waits);
    try std.testing.expectEqual(configuration.interval, harness.last_interval);
}

test "invalid frame retains the real run start until the next frame starts recovery" {
    const events = [_]Event{
        .{ .frame = frame(5 * second, &.{}) }, .{ .frame = frame(4 * second, &.{}) },
        .{ .frame = frame(6 * second, &.{}) },
    };
    var harness = Harness{ .events = &events };
    try run(&harness);
    const failed = harness.reports[2];
    try std.testing.expectEqual(@as(u32, 3), failed.failure);
    try std.testing.expectEqual(5 * second, failed.started);
    try std.testing.expectEqual(4 * second, failed.captured);
    try std.testing.expectEqual(@as(f64, 0), failed.elapsed);
    const recovered = harness.reports[3];
    try std.testing.expectEqual(5 * second, recovered.previous_start);
    try std.testing.expectEqual(4 * second, recovered.previous_at);
    try std.testing.expectEqual(@as(u32, 3), recovered.previous_failure);
    try std.testing.expectEqual(6 * second, recovered.started);
}
