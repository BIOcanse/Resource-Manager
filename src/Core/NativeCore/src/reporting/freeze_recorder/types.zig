const std = @import("std");
const policy = @import("../../configuration/NativePolicy.g.zig");

// These are format capacities, not sampling policy. Incomplete projections remain
// explicitly incomplete; the producer must never silently truncate a disk list.
pub const disk_capacity = policy.freeze_recorder_disk_capacity;
pub const process_capacity = policy.freeze_recorder_process_capacity;
pub const image_name_capacity = policy.freeze_recorder_image_name_capacity;

comptime {
    if (disk_capacity < 1 or disk_capacity > 255 or
        process_capacity < 1 or process_capacity > 255 or
        image_name_capacity < 1 or image_name_capacity > 255)
        @compileError("freeze recorder format capacities must fit nonzero u8 counts and lengths");
}

// All runtime defaults have one owner. The host persists/overrides this object,
// and supplies the memory protection immediate line (null means disabled).
pub const Config = struct {
    enabled: bool = policy.freeze_recorder_enabled,
    slots: u32 = policy.freeze_recorder_slots,
    sample_interval_ms: u32 = policy.freeze_recorder_interval_ms,
    review_window_ms: u32 = policy.freeze_recorder_review_window_ms,
    commit_immediate_percent: ?f64 = null,
    physical_available_percent: f64 = policy.freeze_recorder_physical_available_percent,
    nonpaged_pool_percent: f64 = policy.freeze_recorder_pool_physical_percent,
    storage_unresponsive_consecutive: u32 = policy.freeze_recorder_storage_consecutive,

    pub fn validate(self: Config) !void {
        if (self.slots == 0 or self.sample_interval_ms == 0 or
            self.review_window_ms == 0 or self.storage_unresponsive_consecutive == 0)
            return error.InvalidConfiguration;
        if (!validPercent(self.physical_available_percent) or
            !validPercent(self.nonpaged_pool_percent)) return error.InvalidConfiguration;
        if (self.commit_immediate_percent) |v| {
            if (!validPercent(v)) return error.InvalidConfiguration;
        }
    }
};

fn validPercent(v: f64) bool {
    return std.math.isFinite(v) and v >= 0 and v <= 100;
}

// Stable persisted tags. A null reason means unknown, including legacy records.
pub const GapReason = enum(u8) {
    @"not-published" = 1,
    @"source-unavailable" = 2,
    @"stale-publication" = 3,
    @"window-mismatch" = 4,
    @"system-item-not-supported" = 5,
    @"counter-warmup" = 6,
    @"counter-reset" = 7,
    @"invalid-value" = 8,
};

pub const DiskSample = struct {
    physical_disk_id: u32 = 0,
    queue_depth: ?f32 = null,
    latency_ms: ?f32 = null,
    // Completed operations in this sampling window, never a lifetime counter.
    completed_operations: ?u64 = null,
    completed_operations_unavailable_reason: ?GapReason = null,
};

pub const ProcessSample = struct {
    pid: u32 = 0,
    image_name: [image_name_capacity]u8 = @splat(0),
    image_name_len: u8 = 0,
    private_commit_bytes: u64 = 0,

    pub fn imageName(self: *const ProcessSample) []const u8 {
        return self.image_name[0..@min(self.image_name_len, image_name_capacity)];
    }
};

pub const Sample = struct {
    timestamp_ms: i64 = 0,
    commit_bytes: ?u64 = null,
    commit_limit_bytes: ?u64 = null,
    physical_total_bytes: ?u64 = null,
    physical_available_bytes: ?u64 = null,
    paged_pool_bytes: ?u64 = null,
    nonpaged_pool_bytes: ?u64 = null,
    interrupt_cpu_percent: ?f32 = null,
    longest_interrupt_us: ?u64 = null,
    interrupt_timestamp_ms: ?i64 = null,
    interrupt_window_ms: ?u32 = null,
    interrupt_unavailable_reason: ?GapReason = null,
    kernel_leak_active: ?bool = null,
    kernel_leak_unavailable_reason: ?GapReason = null,
    disks: [disk_capacity]DiskSample = @splat(.{}),
    disk_count: u8 = 0,
    disks_complete: bool = false,
    top_processes: [process_capacity]ProcessSample = @splat(.{}),
    process_count: u8 = 0,
    processes_complete: bool = false,

    // Producers streaming process facts can select the largest three without
    // allocating or retaining the full process list.
    pub fn considerProcess(self: *Sample, process: ProcessSample) void {
        var at: usize = 0;
        while (at < self.process_count and at < process_capacity) : (at += 1) {
            const previous = self.top_processes[at];
            if (process.private_commit_bytes > previous.private_commit_bytes or
                (process.private_commit_bytes == previous.private_commit_bytes and process.pid < previous.pid)) break;
        }
        if (at >= process_capacity) return;
        var end: usize = @min(self.process_count, process_capacity - 1);
        while (end > at) : (end -= 1) self.top_processes[end] = self.top_processes[end - 1];
        self.top_processes[at] = process;
        self.process_count = @intCast(@min(@as(usize, self.process_count) + 1, process_capacity));
    }
};

pub const Record = struct { sequence: u64, sample: Sample };

pub const EventKind = enum {
    resource_exhaustion,
    storage_retry_reset,
    gpu_reset,
    hardware_error,
    unexpected_shutdown,
    bugcheck,
};

pub const EventEvidence = struct {
    kind: EventKind,
    // Event time (UTC Unix ms). For 6008 use the actual shutdown payload below.
    timestamp_ms: i64,
    identity: u64,
    shutdown_timestamp_ms: ?i64 = null,
    bugcheck_code: ?u32 = null,
};

pub const Tags = struct {
    bugcheck: bool = false,
    commit_exhausted: bool = false,
    physical_exhausted: bool = false,
    pool_high: bool = false,
    storage_unresponsive: bool = false,
    gpu_reset: bool = false,
    hardware_error: bool = false,
    recording_stopped_normal: bool = false,

    pub fn hasCause(self: Tags) bool {
        return self.bugcheck or self.commit_exhausted or self.physical_exhausted or
            self.pool_high or self.storage_unresponsive or self.gpu_reset or self.hardware_error;
    }
};

pub const ReviewResult = struct {
    unexpected_shutdown: bool = false,
    last_record_timestamp_ms: i64,
    shutdown_timestamp_ms: ?i64 = null,
    gap_ms: ?u64 = null,
    review_from_ms: i64,
    review_to_ms: i64,
    tags: Tags = .{},
    bugcheck_code: ?u32 = null,
    // Host stores this stable identity with its one-shot history entry.
    evidence_identity: u64 = 0,
    samples_seen: u32 = 0,
    inputs_complete: bool = true,
};
