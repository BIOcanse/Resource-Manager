const std = @import("std");
const t = @import("types.zig");

const DiskRun = struct {
    used: bool = false,
    id: u32 = 0,
    count: u32 = 0,
    last_sequence: u64 = 0,
};

// A streaming classifier. Raw events, dump paths and curve persistence belong to
// the report owner, not this bounded accumulator.
pub const Review = struct {
    config: t.Config,
    boot_ms: i64,
    result: t.ReviewResult,
    disk_runs: [t.disk_capacity]DiskRun = @splat(.{}),
    last_sequence: u64 = 0,
    events_complete: bool = false,

    // boot_ms is the service startup boundary, allowing boot-time Kernel-Power
    // records. shutdown_timestamp_ms takes precedence over logged event time.
    pub fn init(config: t.Config, last_record_ms: i64, boot_ms: i64) !Review {
        try config.validate();
        if (last_record_ms > boot_ms) return error.InvalidWindow;
        return .{ .config = config, .boot_ms = boot_ms, .result = .{
            .last_record_timestamp_ms = last_record_ms,
            .review_from_ms = last_record_ms -| @as(i64, config.review_window_ms),
            .review_to_ms = last_record_ms,
        } };
    }

    pub fn setEventsComplete(self: *Review) void {
        self.events_complete = true;
    }

    pub fn markIncomplete(self: *Review) void {
        self.result.inputs_complete = false;
    }

    pub fn addSample(self: *Review, record: *const t.Record) void {
        const s = &record.sample;
        if (s.timestamp_ms < self.result.review_from_ms or s.timestamp_ms > self.result.review_to_ms) return;
        if (record.sequence == 0 or record.sequence <= self.last_sequence) {
            self.markIncomplete();
            return;
        }
        if (self.last_sequence != 0 and record.sequence - self.last_sequence != 1) self.markIncomplete();
        self.last_sequence = record.sequence;
        self.result.samples_seen +|= 1;

        if (s.commit_bytes == null or s.commit_limit_bytes == null or s.commit_limit_bytes.? == 0) {
            self.markIncomplete();
        } else if (self.config.commit_immediate_percent) |threshold| {
            if (percent(s.commit_bytes.?, s.commit_limit_bytes.?) >= threshold)
                self.result.tags.commit_exhausted = true;
        }
        if (s.physical_total_bytes == null or s.physical_total_bytes.? == 0 or
            s.physical_available_bytes == null or s.physical_available_bytes.? > s.physical_total_bytes.?)
        {
            self.markIncomplete();
        } else {
            if (percent(s.physical_available_bytes.?, s.physical_total_bytes.?) <= self.config.physical_available_percent)
                self.result.tags.physical_exhausted = true;
        }
        if (s.nonpaged_pool_bytes == null or s.physical_total_bytes == null or s.physical_total_bytes.? == 0) {
            self.markIncomplete();
        } else if (percent(s.nonpaged_pool_bytes.?, s.physical_total_bytes.?) >= self.config.nonpaged_pool_percent) {
            self.result.tags.pool_high = true;
        }
        if (s.kernel_leak_active) |active| {
            if (active) self.result.tags.pool_high = true;
        } else self.markIncomplete();
        if (s.paged_pool_bytes == null or s.interrupt_cpu_percent == null or s.longest_interrupt_us == null)
            self.markIncomplete();
        if (s.interrupt_cpu_percent) |interrupt| {
            if (!std.math.isFinite(interrupt) or interrupt < 0 or interrupt > 100) self.markIncomplete();
        }
        if (!s.processes_complete or s.process_count > t.process_capacity) self.markIncomplete();
        if (!s.disks_complete or s.disk_count > t.disk_capacity) self.markIncomplete();

        // Drop disks absent from this projection before reserving new identities;
        // replacing a full inventory must not discard the new disks' first sample.
        var run_index: usize = 0;
        while (run_index < self.disk_runs.len) : (run_index += 1) {
            const run = &self.disk_runs[run_index];
            if (!run.used) continue;
            var present = false;
            var index: usize = 0;
            while (index < @min(s.disk_count, t.disk_capacity)) : (index += 1)
                present = present or s.disks[index].physical_disk_id == run.id;
            if (!present) run.* = .{};
        }

        var disk_index: usize = 0;
        while (disk_index < @min(s.disk_count, t.disk_capacity)) : (disk_index += 1) {
            const d = s.disks[disk_index];
            var prior: usize = 0;
            while (prior < disk_index) : (prior += 1) {
                if (s.disks[prior].physical_disk_id == d.physical_disk_id) {
                    self.markIncomplete();
                    break;
                }
            }
            if (prior != disk_index) continue;
            const run = self.diskRun(d.physical_disk_id) orelse {
                self.markIncomplete();
                continue;
            };
            if (run.last_sequence == 0 or record.sequence - run.last_sequence != 1) run.count = 0;
            run.last_sequence = record.sequence;
            if (d.queue_depth == null or d.completed_operations == null or
                !std.math.isFinite(d.queue_depth.?) or d.queue_depth.? < 0)
            {
                run.count = 0;
                self.markIncomplete();
                continue;
            }
            // Latency is legitimately null when no operations complete.
            if (d.completed_operations.? != 0 and d.latency_ms == null) self.markIncomplete();
            if (d.latency_ms) |latency| {
                if (!std.math.isFinite(latency) or latency < 0) self.markIncomplete();
            }
            if (d.queue_depth.? > 0 and d.completed_operations.? == 0) {
                run.count +|= 1;
                if (run.count >= self.config.storage_unresponsive_consecutive)
                    self.result.tags.storage_unresponsive = true;
            } else run.count = 0;
        }
        var i: usize = 0;
        while (i < self.disk_runs.len) : (i += 1) {
            if (self.disk_runs[i].used and self.disk_runs[i].last_sequence != record.sequence)
                self.disk_runs[i] = .{};
        }
    }

    pub fn addEvent(self: *Review, event: *const t.EventEvidence) void {
        const marker = event.kind == .unexpected_shutdown or event.kind == .bugcheck;
        const effective = if (marker) event.shutdown_timestamp_ms orelse event.timestamp_ms else event.timestamp_ms;
        if (effective < self.result.review_from_ms or effective > self.boot_ms) return;
        if (marker and effective > self.result.last_record_timestamp_ms) {
            self.result.unexpected_shutdown = true;
            if (self.result.shutdown_timestamp_ms == null or effective < self.result.shutdown_timestamp_ms.?)
                self.result.shutdown_timestamp_ms = effective;
        }
        switch (event.kind) {
            .resource_exhaustion => self.result.tags.commit_exhausted = true,
            .storage_retry_reset => self.result.tags.storage_unresponsive = true,
            .gpu_reset => self.result.tags.gpu_reset = true,
            .hardware_error => self.result.tags.hardware_error = true,
            .bugcheck => {
                self.result.tags.bugcheck = true;
                if (event.bugcheck_code) |code| {
                    // Stable under duplicate/reordered catchup deliveries.
                    if (self.result.bugcheck_code == null or code < self.result.bugcheck_code.?)
                        self.result.bugcheck_code = code;
                }
            },
            .unexpected_shutdown => {},
        }
    }

    pub fn finish(self: *const Review) t.ReviewResult {
        var result = self.result;
        result.inputs_complete = result.inputs_complete and self.events_complete and result.samples_seen != 0;
        result.tags.recording_stopped_normal = result.unexpected_shutdown and result.inputs_complete and !result.tags.hasCause();
        if (result.shutdown_timestamp_ms) |shutdown| {
            result.gap_ms = @intCast(@as(i128, shutdown) - result.last_record_timestamp_ms);
            // Canonical incident identity is independent of source record IDs:
            // KP41 / 6008 / 1001 duplicates merge into one history report.
            var key: [16]u8 = undefined;
            std.mem.writeInt(i64, key[0..8], result.last_record_timestamp_ms, .little);
            std.mem.writeInt(i64, key[8..16], shutdown, .little);
            result.evidence_identity = std.hash.Wyhash.hash(0, &key);
        }
        return result;
    }

    fn diskRun(self: *Review, id: u32) ?*DiskRun {
        var empty: ?usize = null;
        var i: usize = 0;
        while (i < self.disk_runs.len) : (i += 1) {
            const run = &self.disk_runs[i];
            if (run.used and run.id == id) return run;
            if (!run.used and empty == null) empty = i;
        }
        const index = empty orelse return null;
        self.disk_runs[index] = .{ .used = true, .id = id };
        return &self.disk_runs[index];
    }
};

fn percent(part: u64, whole: u64) f64 {
    return @as(f64, @floatFromInt(part)) / @as(f64, @floatFromInt(whole)) * 100;
}
