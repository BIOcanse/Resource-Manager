const std = @import("std");
const series = @import("series.zig");

pub const Frame = extern struct {
    ticks: i64,
    available: u32,
    unavailable_count: u32,
    samples: ?[*]const series.Sample,
    sample_count: usize,
    unavailable: ?[*]const series.Key,
    unavailable_length: usize,
};
pub const Snapshot = extern struct {
    status: u32 = 1,
    failure: u32 = 0,
    captured: i64 = 0,
    started: i64 = 0,
    failed_at: i64 = 0,
    previous_start: i64 = 0,
    previous_at: i64 = 0,
    elapsed: f64 = 0,
    frames: u64 = 0,
    series_bytes: u64 = 0,
    peak_bytes: u64 = 0,
    samples: u32 = 0,
    has_start: u32 = 0,
    has_previous: u32 = 0,
    previous_failure: u32 = 0,
    unavailable: u32 = 0,
    unrecorded: u32 = 0,
    recorded_suspects: u32 = 0,
    holders: u32 = 0,
    judgment_milliseconds: f64 = 0,
    attribution_milliseconds: f64 = 0,
};
pub const Callbacks = extern struct {
    context: ?*anyopaque,
    next: *const fn (?*anyopaque, *Frame) callconv(.c) u32,
    prepare: *const fn (?*anyopaque, *Snapshot, ?[*]const series.Suspect, usize, ?[*]const series.Key, usize) callconv(.c) u32,
    publish: *const fn (?*anyopaque, *const Snapshot) callconv(.c) u32,
    started: *const fn (?*anyopaque) callconv(.c) u32,
    wait: *const fn (?*anyopaque, u32) callconv(.c) u32,
    now: *const fn (?*anyopaque) callconv(.c) i64,
    clock: *const fn (?*anyopaque) callconv(.c) f64,
};
pub const Configuration = extern struct { options: series.Options, interval: u32, item_capacity: u32 };

pub export fn rm_kernel_leak_series_size(options: *const series.Options) callconv(.c) usize {
    return series.requiredBytes(options.*);
}
pub export fn rm_kernel_leak_series_init(region: [*]align(8) u8, length: usize, options: *const series.Options) callconv(.c) u32 {
    _ = series.init(region[0..length], options.*) catch return 3;
    return 0;
}
pub export fn rm_kernel_leak_series_observe(region: *series.Series, ticks: i64, samples: ?[*]const series.Sample, count: usize, suspects: [*]series.Suspect, judged: [*]series.Key, capacity: usize, result: *extern struct { suspects: usize, judged: usize }) callconv(.c) u32 {
    const value = region.observe(ticks, if (samples) |p| p[0..count] else &.{}, suspects[0..capacity], judged[0..capacity]) catch |err| return classify(err);
    result.* = .{ .suspects = value.suspect_count, .judged = value.judged_count };
    return 0;
}
pub export fn rm_kernel_leak_series_clear(region: *series.Series) callconv(.c) void {
    region.clear();
}
pub export fn rm_kernel_leak_series_metrics(region: *const series.Series, output: *[3]u64) callconv(.c) void {
    output.* = .{ region.count(), region.bytes(), region.peakBytes() };
}
fn classify(err: anyerror) u32 {
    return if (err == error.CapacityExceeded or err == error.BufferTooSmall) 2 else 3;
}

// One native run owns progress, restart decisions, judgment and retry loops.
// next/prepare/publish borrow existing subscription and transport adapters only.
pub export fn rm_kernel_leak_run(region: [*]align(8) u8, length: usize, config: *const Configuration, cb: *const Callbacks) callconv(.c) u32 {
    const series_length = series.requiredBytes(config.options);
    const output_offset = std.mem.alignForward(usize, series_length, 8);
    const maximum = config.options.maximum_holders;
    const required = output_offset + maximum * (@sizeOf(series.Suspect) + @sizeOf(series.Key));
    if (series_length == 0 or length < required) return 3;
    const state = series.init(region[0..series_length], config.options) catch return 3;
    const output: [*]series.Suspect = @ptrCast(@alignCast(region + output_offset));
    const judged: [*]series.Key = @ptrCast(@alignCast(region + output_offset + maximum * @sizeOf(series.Suspect)));
    var report: Snapshot = .{};
    var failed = false;
    // The visible report failure may change during source retries. A failed
    // run's first cause and timestamp remain the evidence for the next run.
    var failed_cause: u32 = 0;
    defer {
        state.clear();
        report.status = 0;
        report.failure = 0;
        report.captured = 0;
        report.started = 0;
        report.has_start = 0;
        report.elapsed = 0;
        report.samples = 0;
        report.has_previous = 0;
        report.holders = 0;
        report.series_bytes = 0;
        report.unavailable = 0;
        report.unrecorded = 0;
        _ = cb.publish(cb.context, &report);
    }
    const initial = cb.publish(cb.context, &report);
    if (initial != 0) return if (initial == 2) 0 else 6;
    const started = cb.started(cb.context);
    if (started != 0) return if (started == 2) 0 else 6;
    while (true) {
        var frame: Frame = undefined;
        const code = cb.next(cb.context, &frame);
        if (code == 2) return 0;
        if (code == 6) return 6;
        if (code != 0) {
            if (code != 1 or !failed) {
                fail(&report, state, &failed, &failed_cause, if (code == 4) 2 else if (code == 5) 3 else 1, cb.now(cb.context), 0);
                if (cb.publish(cb.context, &report) != 0) return 6;
            }
            if (cb.wait(cb.context, config.interval) != 0) return 0;
            continue;
        }
        if (report.has_start == 0 or failed) {
            if (failed) {
                report.has_previous = 1;
                report.previous_start = report.started;
                report.previous_at = report.failed_at;
                report.previous_failure = failed_cause;
            }
            report.started = frame.ticks;
            report.has_start = 1;
            report.samples = 0;
            report.elapsed = 0;
            failed = false;
        }
        if (frame.available == 0) {
            fail(&report, state, &failed, &failed_cause, 1, frame.ticks, frame.unavailable_count);
            if (cb.publish(cb.context, &report) != 0) return 6;
            continue;
        }
        var failure: u32 = 0;
        var result: series.Result = .{ .suspect_count = 0, .judged_count = 0 };
        const judgment_started = cb.clock(cb.context);
        if (frame.ticks < report.started) failure = 3 else {
            result = state.observe(frame.ticks, if (frame.samples) |p| p[0..frame.sample_count] else &.{}, output[0..maximum], judged[0..maximum]) catch |err| blk: {
                failure = classify(err);
                break :blk .{ .suspect_count = 0, .judged_count = 0 };
            };
        }
        report.judgment_milliseconds = @max(0, cb.clock(cb.context) - judgment_started);
        if (failure == 0) {
            if (report.samples == std.math.maxInt(i32)) failure = 2 else {
                report.samples += 1;
                report.elapsed = @as(f64, @floatFromInt(frame.ticks - report.started)) / 10000000;
                report.captured = frame.ticks;
                report.failure = 0;
                report.unavailable = frame.unavailable_count;
                report.attribution_milliseconds = 0;
                const prepared = cb.prepare(cb.context, &report, output, result.suspect_count, judged, result.judged_count);
                // prepare reports attribution cost before reported-item persistence.
                if (prepared == 2) return 0;
                if (prepared == 6) return 6;
                if (prepared != 0) failure = if (prepared == 4) 2 else if (prepared == 5) 3 else 1;
            }
        }
        if (failure != 0) fail(&report, state, &failed, &failed_cause, failure, frame.ticks, frame.unavailable_count) else {
            report.status = if (result.suspect_count > 0) (if (report.recorded_suspects > 0) @as(u32, 3) else 2) else if (report.samples < config.options.minimum_samples or report.elapsed < @as(f64, @floatFromInt(config.options.minimum_seconds))) 1 else 2;
            metrics(&report, state);
        }
        if (cb.publish(cb.context, &report) != 0) return 6;
    }
}
fn metrics(report: *Snapshot, state: *const series.Series) void {
    report.holders = @intCast(state.count());
    report.series_bytes = state.bytes();
    report.peak_bytes = @max(report.peak_bytes, state.peakBytes());
    report.frames += 1;
}
fn fail(report: *Snapshot, state: *series.Series, failed: *bool, failed_cause: *u32, failure: u32, ticks: i64, unavailable: u32) void {
    state.clear();
    if (report.has_start != 0 and !failed.*) {
        report.elapsed = @max(0, @as(f64, @floatFromInt(ticks - report.started)) / 10000000);
        report.failed_at = ticks;
        failed.* = true;
        failed_cause.* = failure;
    }
    report.status = 4;
    report.failure = failure;
    report.captured = ticks;
    report.unavailable = unavailable;
    report.unrecorded = 0;
    report.judgment_milliseconds = 0;
    report.attribution_milliseconds = 0;
    metrics(report, state);
}
