const std = @import("std");
pub const phase_count = 11;
pub const Phase = enum(u32) { bank_select, bank_reset, io, notifications, residency, interrupts, diagnostics, renderer, scratch_reset, install, signal };
pub const PhaseStats = extern struct { calls: u64 = 0, wall_qpc_ticks: u64 = 0 };
pub const RegionStats = extern struct {
    low_alloc_calls: u64 = 0,
    high_alloc_calls: u64 = 0,
    low_search_steps: u64 = 0,
    high_search_steps: u64 = 0,
    free_calls: u64 = 0,
    free_insert_steps: u64 = 0,
    resize_calls: u64 = 0,
    resize_successes: u64 = 0,
    low_requested_bytes: u64 = 0,
    high_requested_bytes: u64 = 0,
};
pub const View = extern struct {
    enabled: u32 = 0,
    phases_count: u32 = phase_count,
    qpc_frequency: i64 = 0,
    publications: u64 = 0,
    clock_failures: u64 = 0,
    cpu_rows: u64 = 0,
    cpu_pairs_bound: u64 = 0,
    irq_identities: u64 = 0,
    irq_cells: u64 = 0,
    notifications: u64 = 0,
    path_units: u64 = 0,
    region: RegionStats = .{},
    phases: [phase_count]PhaseStats = @splat(PhaseStats{}),
};
pub const CpuDetail = extern struct {
    enabled: u32 = 0,
    reserved: u32 = 0,
    qpc_frequency: i64 = 0,
    detail_clock_reads: u64 = 0,
    detail_clock_failures: u64 = 0,
    native_snapshot: PhaseStats = .{},
    abi_conversion: PhaseStats = .{},
};
extern "kernel32" fn QueryPerformanceCounter(value: *i64) callconv(.winapi) i32;
extern "kernel32" fn QueryPerformanceFrequency(value: *i64) callconv(.winapi) i32;
/// Single-flow diagnostic state. Never read concurrently with ProcessTrace.
/// Elapsed QPC includes preemption; it is not thread CPU time.
pub const Profile = struct {
    enabled: bool = false,
    view: View = .{},
    cpu_detail: CpuDetail = .{},
    phase: ?Phase = null,
    mark: ?i64 = null,
    // RuntimeRead: Fixed
    pub fn configure(self: *Profile, enabled: bool) !void {
        var frequency: i64 = 0;
        if (enabled and (QueryPerformanceFrequency(&frequency) == 0 or frequency <= 0)) return error.ClockSamplingFailed;
        self.* = .{ .enabled = enabled, .view = .{ .enabled = if (enabled) 1 else 0, .qpc_frequency = frequency }, .cpu_detail = .{ .enabled = if (enabled) 1 else 0, .qpc_frequency = frequency } };
    }
    // RuntimeRead: Sampled
    fn now(self: *Profile) ?i64 {
        var value: i64 = 0;
        if (QueryPerformanceCounter(&value) == 0 or value < 0) {
            self.view.clock_failures +|= 1;
            return null;
        }
        return value;
    }
    pub fn detailStart(self: *Profile) ?i64 {
        if (!self.enabled) return null;
        self.cpu_detail.detail_clock_reads +|= 1;
        const tick = self.now();
        if (tick == null) self.cpu_detail.detail_clock_failures +|= 1;
        return tick;
    }
    fn detailElapsed(self: *Profile, started: ?i64) u64 {
        const ended = self.detailStart();
        if (started) |start| if (ended) |end_tick| {
            if (end_tick >= start) return @intCast(end_tick - start);
            self.cpu_detail.detail_clock_failures +|= 1;
            self.view.clock_failures +|= 1;
        };
        return 0;
    }
    pub fn finishCpuPart(self: *Profile, started: ?i64, stats: *PhaseStats) void {
        if (!self.enabled) return;
        stats.calls +|= 1;
        stats.wall_qpc_ticks +|= self.detailElapsed(started);
    }
    fn finishAt(self: *Profile, now_tick: ?i64) void {
        if (self.phase) |phase| {
            const stats = &self.view.phases[@intFromEnum(phase)];
            stats.calls +|= 1;
            if (self.mark) |start| if (now_tick) |ended| {
                if (ended >= start) stats.wall_qpc_ticks +|= @intCast(ended - start) else self.view.clock_failures +|= 1;
            };
        }
    }
    pub fn begin(self: *Profile, phase: Phase) void {
        if (!self.enabled) return;
        const tick = self.now();
        self.finishAt(tick);
        self.phase = phase;
        self.mark = tick;
    }
    pub fn end(self: *Profile) void {
        if (!self.enabled) return;
        self.finishAt(self.now());
        self.phase = null;
        self.mark = null;
    }
};
test "profile is disabled by default and has a stable bounded wire layout" {
    var profile: Profile = .{};
    profile.begin(.residency);
    profile.end();
    try std.testing.expectEqual(@as(usize, 336), @sizeOf(View));
    try std.testing.expectEqual(@as(usize, 64), @sizeOf(CpuDetail));
    try std.testing.expectEqual(@as(?i64, null), profile.detailStart());
    try std.testing.expectEqual(@as(u64, 0), profile.cpu_detail.detail_clock_reads);
    try std.testing.expectEqual(@as(usize, 80), @sizeOf(RegionStats));
    try std.testing.expectEqual(@as(u64, 0), profile.view.phases[4].calls);
    profile.enabled = true;
    profile.phase = .residency;
    profile.mark = 10;
    profile.finishAt(25);
    try std.testing.expectEqual(@as(u64, 1), profile.view.phases[4].calls);
    try std.testing.expectEqual(@as(u64, 15), profile.view.phases[4].wall_qpc_ticks);
}
