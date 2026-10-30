//! Bounded diagnostic facts only; never affect activity, ownership or release.
//! Tracker is pure with caller-supplied monotonic time. Owner provides locking.
const std = @import("std");
const builtin = @import("builtin");
const Lock = @import("sync.zig").Lock;
pub const write_watch = @import("write_watch.zig");

pub const active: u32 = 1;
pub const ended: u32 = 2;
pub const forced: u32 = 3;

pub const Record = extern struct {
    uid: [32]u8 = @splat(0),
    slot: u32 = 0,
    state: u32 = 0,
    allocation_number: u64 = 0,
    use_generation: u64 = 0,
    started_epoch: u64 = 0,
    ended_epoch: u64 = 0,
    started_ms: u64 = 0,
    ended_ms: u64 = 0,
    write_window_count: u64 = 0,
    dirty_page_samples: u64 = 0,
    last_observed_write_ms: u64 = 0,
    maximum_no_write_gap_ms: u64 = 0,
    support_flags: u32 = 0,
    observation_flags: u32 = 0,
};

pub const Summary = extern struct {
    record_capacity: u32 = 0,
    written_count: u32 = 0,
    active_count: u64 = 0,
    completed_count: u64 = 0,
    dropped_records: u64 = 0,
    unavailable_write_samples: u64 = 0,
    snapshot_ms: u64 = 0,
};

comptime {
    if (@sizeOf(Record) != 128 or @alignOf(Record) != 8 or
        @offsetOf(Record, "allocation_number") != 40 or
        @offsetOf(Record, "support_flags") != 120 or
        @sizeOf(Summary) != 48 or @alignOf(Summary) != 8)
        @compileError("lease analysis ABI layout changed");
}

pub fn Storage(comptime capacity: u32) type {
    return StorageWithWriteWatch(capacity, 16384);
}

pub fn StorageWithWriteWatch(comptime capacity: u32, comptime pages: u32) type {
    if (pages == 0 or pages > 1048576) @compileError("write watch pages must be 1..1048576");
    return struct {
        lock: Lock = .{},
        tracker: Tracker(capacity) = .{},
        scratch: [pages]usize = undefined,

        /// In place: records and scratch reach many MiB at their maxima. The
        /// scratch needs no value; GetWriteWatch writes it before any read.
        pub fn init(self: *@This()) void {
            self.lock = .{};
            self.tracker.init();
        }

        /// The caller holds the resource metadata gate. Never called by a
        /// production/analysis-off owner. Query/reset is serialized per instance.
        pub fn collectResource(self: *@This(), resource: anytype, epoch: u64, holders: u64) void {
            const registration = resource.registration orelse return;
            const region = registration.region orelse return;
            const base = region.base orelse return;
            self.lock.lock();
            defer self.lock.unlock();
            const sample = write_watch.collect(base, region.page_bytes, &self.scratch);
            self.tracker.observe(resource.generation, epoch, sample.pages, sample.flags | (if (holders > 1) write_watch.shared_attribution else @as(u32, 0)), monotonicMilliseconds());
        }

        /// Only after a new SDK allocation/copy, while mutation is exclusive.
        /// Resetting an ordinary hit would discard the prior holder's writes.
        pub fn resetResource(self: *@This(), resource: anytype, epoch: u64) void {
            const registration = resource.registration orelse return;
            const region = registration.region orelse return;
            const base = region.base orelse return;
            self.lock.lock();
            defer self.lock.unlock();
            const flags = write_watch.establishBaseline(base, region.page_bytes, pages);
            if (flags != 0) self.tracker.observe(resource.generation, epoch, 0, flags, monotonicMilliseconds());
        }
    };
}
pub fn Tracker(comptime capacity: u32) type {
    if (capacity == 0 or capacity > 65536) @compileError("lease analysis capacity must be 1..65536");
    return struct {
        const Self = @This();
        const Cell = struct { record: Record = .{}, next_ended: ?usize = null, last_credited_epoch: ?u64 = null };
        cells: [capacity]Cell = @splat(.{}),
        // FIFO completion order also handles equal clock timestamps and has no
        // sequence counter that could wrap. Active cells never enter this list.
        oldest_ended: ?usize = null,
        newest_ended: ?usize = null,
        dropped_records: u64 = 0,
        unavailable_write_samples: u64 = 0,

        /// Same state as the field defaults, written cell by cell into place.
        pub fn init(self: *Self) void {
            for (&self.cells) |*cell| cell.* = .{};
            self.oldest_ended = null;
            self.newest_ended = null;
            self.dropped_records = 0;
            self.unavailable_write_samples = 0;
        }

        pub fn begin(self: *Self, uid: []const u8, lease: anytype, epoch: u64, now: u64) void {
            var available: ?usize = null;
            var i: usize = 0;
            while (i < self.cells.len) : (i += 1) {
                if (self.cells[i].record.state == 0) {
                    available = i;
                    break;
                }
            }
            if (available == null) {
                const oldest = self.oldest_ended orelse {
                    self.dropped_records +|= 1;
                    return;
                };
                available = oldest;
                self.oldest_ended = self.cells[oldest].next_ended;
                if (self.oldest_ended == null) self.newest_ended = null;
            }
            const cell = &self.cells[available.?];
            cell.* = .{ .record = .{
                .slot = lease.slot,
                .state = active,
                .allocation_number = lease.allocation_number,
                .use_generation = lease.use_generation,
                .started_epoch = epoch,
                .started_ms = now,
                .support_flags = write_watch.supportFlags(),
            } };
            std.debug.assert(uid.len == 16 or uid.len == 32);
            @memcpy(cell.record.uid[0..uid.len], uid);
        }

        /// Internal observer only: ended/untracked records cannot be updated.
        /// With overlapping holders these facts belong to the shared allocation,
        /// not to an identifiable thread. Callers never supply access markers.
        pub fn observe(self: *Self, allocation_number: u64, epoch: u64, pages: u64, flags: u32, now: u64) void {
            var holders: usize = 0;
            for (&self.cells) |*cell| {
                if (cell.record.state == active and cell.record.allocation_number == allocation_number) holders += 1;
            }
            if (flags & (write_watch.exceeds_scratch | write_watch.query_failed | write_watch.platform_unavailable) != 0) self.unavailable_write_samples +|= 1;
            for (&self.cells) |*cell| {
                const r = &cell.record;
                if (r.state != active or r.allocation_number != allocation_number) continue;
                r.observation_flags |= flags | (if (holders > 1) write_watch.shared_attribution else @as(u32, 0));
                if (pages == 0) continue;
                const previous = if (r.dirty_page_samples != 0) r.last_observed_write_ms else r.started_ms;
                r.maximum_no_write_gap_ms = @max(r.maximum_no_write_gap_ms, now -| previous);
                r.last_observed_write_ms = now;
                r.dirty_page_samples +|= pages;
                if (cell.last_credited_epoch == null or cell.last_credited_epoch.? != epoch) {
                    r.write_window_count +|= 1;
                    cell.last_credited_epoch = epoch;
                }
            }
        }
        pub fn finish(self: *Self, lease: anytype, epoch: u64, now: u64) void {
            var i: usize = 0;
            while (i < self.cells.len) : (i += 1) {
                const r = self.cells[i].record;
                if (r.state == active and r.slot == lease.slot and
                    r.allocation_number == lease.allocation_number and
                    r.use_generation == lease.use_generation)
                {
                    self.complete(i, ended, epoch, now);
                    return;
                }
            }
        }

        pub fn invalidate(self: *Self, allocation_number: u64, epoch: u64, now: u64) void {
            if (allocation_number == 0) return;
            var i: usize = 0;
            while (i < self.cells.len) : (i += 1) {
                if (self.cells[i].record.state == active and self.cells[i].record.allocation_number == allocation_number)
                    self.complete(i, forced, epoch, now);
            }
        }

        fn complete(self: *Self, i: usize, state: u32, epoch: u64, now: u64) void {
            const r = &self.cells[i].record;
            r.state = state;
            r.ended_epoch = epoch;
            r.ended_ms = now;
            includeIdleGap(r, now);
            self.cells[i].next_ended = null;
            if (self.newest_ended) |last| self.cells[last].next_ended = i else self.oldest_ended = i;
            self.newest_ended = i;
        }

        pub fn snapshot(self: *const Self, output: []Record, now: u64) Summary {
            var result = Summary{ .record_capacity = capacity, .dropped_records = self.dropped_records, .unavailable_write_samples = self.unavailable_write_samples, .snapshot_ms = now };
            var i: usize = 0;
            while (i < self.cells.len) : (i += 1) {
                const r = self.cells[i].record;
                if (r.state == 0) continue;
                if (r.state == active) result.active_count +|= 1 else result.completed_count +|= 1;
                if (result.written_count < output.len) {
                    var copy = r;
                    if (r.state == active) includeIdleGap(&copy, now);
                    output[result.written_count] = copy;
                    result.written_count += 1;
                }
            }
            return result;
        }
    };
}

fn includeIdleGap(r: *Record, now: u64) void {
    const previous = if (r.dirty_page_samples != 0) r.last_observed_write_ms else r.started_ms;
    r.maximum_no_write_gap_ms = @max(r.maximum_no_write_gap_ms, now -| previous);
}

const Windows = struct {
    extern "kernel32" fn GetTickCount64() callconv(.winapi) u64;
};

/// Lazy referenced only by enabled owner methods. No wall-clock fallback.
pub fn monotonicMilliseconds() u64 {
    switch (builtin.os.tag) {
        .windows => return Windows.GetTickCount64(),
        .linux => {
            var value: std.os.linux.timespec = undefined;
            const rc = std.os.linux.clock_gettime(.MONOTONIC, &value);
            if (std.os.linux.errno(rc) != .SUCCESS) @panic("monotonic clock unavailable");
            return @as(u64, @intCast(value.sec)) *| 1000 +| @as(u64, @intCast(@divTrunc(value.nsec, 1_000_000)));
        },
        else => @compileError("lease analysis requires a Windows or Linux monotonic clock"),
    }
}
