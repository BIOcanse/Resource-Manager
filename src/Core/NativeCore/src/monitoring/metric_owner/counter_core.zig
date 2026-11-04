//! Pure interval calculations. The owner wrapper supplies both observations,
//! including UTC/monotonic time; this core never reads or publishes shared data.
const std = @import("std");
const old = @import("../metric_snapshot/protocol.zig");
pub const Baseline = struct { valid: bool = false, schema: u32 = 0, low: u64 = 0, high: u64 = 0, frequency: u64 = 0, reference: u64 = 0, time: i64 = 0, monotonic: u64 = 0 };
pub const Value = struct { low: u64, duration: u64 };

pub fn cpuCounter(prior: Baseline, next: Baseline) ?Value {
    if (!prior.valid or prior.schema != 16 or prior.frequency != next.frequency or next.monotonic <= prior.monotonic or next.low < prior.low or next.high < prior.high or next.reference < prior.reference) return null;
    const total = std.math.add(u64, next.high - prior.high, next.reference - prior.reference) catch return null;
    const idle = next.low - prior.low;
    if (total == 0 or idle > total) return null;
    const duration = @as(u128, next.monotonic - prior.monotonic) * 1000 / next.frequency;
    if (duration == 0 or duration > std.math.maxInt(u64)) return null;
    const usage = @as(f64, @floatFromInt(total - idle)) * 100 / @as(f64, @floatFromInt(total));
    return .{ .low = @bitCast(usage), .duration = @intCast(duration) };
}
pub fn counter(kind: u32, prior: Baseline, next: Baseline) ?Value {
    if (!prior.valid or prior.schema != next.schema) return null;
    const timed = old.isTimedCounterMetric(kind);
    const floating = old.isFloatingCounterMetric(kind);
    var duration: u64 = 0;
    var ratio: f64 = 0;
    if (floating) {
        const a: f64 = @bitCast(next.low);
        const b: f64 = @bitCast(next.high);
        const pa: f64 = @bitCast(prior.low);
        const pb: f64 = @bitCast(prior.high);
        if (!std.math.isFinite(a) or !std.math.isFinite(b) or a < 0 or b < 0 or a < pa or b <= pb) return null;
        const ms = (b - pb) * 1000;
        if (!std.math.isFinite(ms) or ms < 1 or ms >= 0x1p64) return null;
        duration = @intFromFloat(ms);
        ratio = (a - pa) / (b - pb);
        if (kind == 17) ratio *= 100;
    } else {
        if (next.low < prior.low or next.high <= prior.high or (!timed and next.time <= prior.time)) return null;
        if (timed and (next.frequency == 0 or next.frequency != prior.frequency)) return null;
        if (kind == 19 and next.reference != prior.reference) return null;
        const ms: u128 = if (timed) @as(u128, next.high - prior.high) * 1000 / next.frequency else @intCast(next.time - prior.time);
        if (ms == 0 or ms > std.math.maxInt(u64)) return null;
        duration = @intCast(ms);
        ratio = @as(f64, @floatFromInt(next.low - prior.low)) * (if (timed) @as(f64, @floatFromInt(next.frequency)) else 1) / @as(f64, @floatFromInt(next.high - prior.high));
    }
    if (kind == 18) ratio /= 10_000;
    if (kind == 19) ratio *= @as(f64, @floatFromInt(next.reference));
    if (!std.math.isFinite(ratio)) return null;
    return .{ .low = @bitCast(ratio), .duration = duration };
}
