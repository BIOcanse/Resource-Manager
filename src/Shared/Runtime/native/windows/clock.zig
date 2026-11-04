//! Passive C3 mechanism. UTC is for timestamps; QPC alone measures elapsed time.
//! No timers, persisted ordering, settings, allocator, or retained OS handles.
const std = @import("std");

// Windows FILETIME and .NET DateTime both count 100 ns, with different epochs.
pub const filetime_epoch_ticks: u64 = 504911232000000000;
pub const unix_epoch_ticks: u64 = 621355968000000000;
pub const ticks_per_second: u64 = 10000000;
pub const Utc = struct { ticks: u64 };
pub const Monotonic = struct { ticks: u64 };
pub const Frequency = struct { ticks_per_second: u64 };
pub const Deadline = struct {
    at: Monotonic,

    pub fn after(now: Monotonic, clock_frequency: Frequency, duration_100ns: u64) error{ InvalidFrequency, Overflow }!Deadline {
        if (clock_frequency.ticks_per_second == 0) return error.InvalidFrequency;
        // Round up: a sub-QPC-tick duration must not expire early.
        const scaled = @as(u128, duration_100ns) * clock_frequency.ticks_per_second;
        const delta = std.math.cast(u64, (scaled + ticks_per_second - 1) / ticks_per_second) orelse return error.Overflow;
        return .{ .at = .{ .ticks = std.math.add(u64, now.ticks, delta) catch return error.Overflow } };
    }

    pub fn expired(self: Deadline, now: Monotonic) bool {
        return now.ticks >= self.at.ticks;
    }
};

pub const FileTime = extern struct {
    low: u32 = 0,
    high: u32 = 0,
    pub fn value(self: FileTime) u64 {
        return (@as(u64, self.high) << 32) | self.low;
    }
};

pub fn fromFileTime(value: u64) error{Overflow}!Utc {
    const ticks = std.math.add(u64, value, filetime_epoch_ticks) catch return error.Overflow;
    // DateTime.MaxValue.Ticks is the externally retained SDK timestamp domain.
    if (ticks > 3155378975999999999) return error.Overflow;
    return .{ .ticks = ticks };
}

pub fn utcNow() error{Overflow}!Utc {
    var value: FileTime = .{};
    GetSystemTimePreciseAsFileTime(&value);
    return fromFileTime(value.value());
}

pub fn monotonicNow() error{ClockUnavailable}!Monotonic {
    var value: i64 = 0;
    if (QueryPerformanceCounter(&value) == 0 or value < 0) return error.ClockUnavailable;
    return .{ .ticks = @intCast(value) };
}

pub fn frequency() error{ClockUnavailable}!Frequency {
    var value: i64 = 0;
    if (QueryPerformanceFrequency(&value) == 0 or value <= 0) return error.ClockUnavailable;
    return .{ .ticks_per_second = @intCast(value) };
}

extern "kernel32" fn GetSystemTimePreciseAsFileTime(*FileTime) callconv(.winapi) void;
extern "kernel32" fn QueryPerformanceCounter(*i64) callconv(.winapi) i32;
extern "kernel32" fn QueryPerformanceFrequency(*i64) callconv(.winapi) i32;
