const std = @import("std");
const io = @import("io_decode.zig");
const identity = @import("io_identity.zig");
const Budget = @import("budget.zig").Budget;

/// Every limit comes from the host's kernel_etw_limits declaration; no field defaults.
pub const Config = struct {
    window_ms: i64,
    retention_ms: i64,
    bucket_width_ms: i64,
    maximum_active_pairs: usize,
    maximum_disks: usize,
    maximum_cached_processes: usize,
    byte_cap: usize,
};

/// One verified process result. Rates preserve the old fixed two-second
/// divisor; bytes and operation counts stay exact integers.
pub const ProcessRow = struct {
    process_id: u32,
    process_birth_filetime_100ns: ?u64,
    available: bool,
    read_bytes: u64,
    write_bytes: u64,
    read_bytes_per_second: f64,
    write_bytes_per_second: f64,
    read_operation_count: u32,
    write_operation_count: u32,
};

/// Per-process, per-known-disk result. The owner emits an unavailable row for
/// a disk while process identity is unknown, matching the old observation API.
pub const DiskRow = struct {
    process_id: u32,
    disk_number: u32,
    process_birth_filetime_100ns: ?u64,
    available: bool,
    read_bytes: u64,
    write_bytes: u64,
};

pub const Window = struct {
    complete: bool,
    processes: []const ProcessRow,
    disk_rows: []const DiskRow,
    disks: []const u32,
    start_tick_ms: i64,
    through_tick_ms: i64,
    window_ms: i64,
    process_revision: u64,
};

pub const Usage = struct {
    current_bytes: usize,
    peak_bytes: usize,
    byte_cap: usize,
    failed_allocations: u64,
    capacity_failures: u64,
};

const Key = struct {
    process_id: u32,
    disk_number: ?u32,
    is_write: bool,
    birth: ?u64,
};
const Bucket = struct {
    id: i64 = 0,
    first_tick: i64 = 0,
    last_tick: i64 = 0,
    bytes: u64 = 0,
    count: u32 = 0,
    invalid: bool = false,
};
const Pair = struct { buckets: []Bucket, last_tick: i64 };
const BirthCacheEntry = struct { birth: ?u64, sequence: u64 };
const ProcessSelection = struct { has_unknown_birth: bool = false };
const BirthKey = struct { process_id: u32, birth: u64 };
const DiskBirthKey = struct { process_id: u32, disk_number: u32, birth: u64 };
const DiskSelectionKey = struct { process_id: u32, disk_number: u32 };
const DiskSelection = struct {
    birth: ?u64 = null,
    available: bool = false,
    read_bytes: u64 = 0,
    write_bytes: u64 = 0,
};
const Totals = struct {
    read_bytes: u64 = 0,
    write_bytes: u64 = 0,
    read_count: u32 = 0,
    write_count: u32 = 0,
    invalid: bool = false,
};

/// Single-consumer native disk window. It owns the process-birth intake cache,
/// exact bucket totals and verified snapshot aggregation. ETW session lifecycle,
/// subscriptions and thread-to-process history remain with the shared consumer.
pub const Owner = struct {
    budget: Budget,
    config: Config,
    identity_source: identity.Source,
    bucket_count: usize,
    pairs: std.AutoHashMapUnmanaged(Key, Pair) = .empty,
    disks: std.AutoHashMapUnmanaged(u32, i64) = .empty,
    process_births: std.AutoHashMapUnmanaged(u32, BirthCacheEntry) = .empty,
    selections: std.AutoHashMapUnmanaged(u32, ProcessSelection) = .empty,
    birth_totals: std.AutoHashMapUnmanaged(BirthKey, Totals) = .empty,
    disk_birth_totals: std.AutoHashMapUnmanaged(DiskBirthKey, Totals) = .empty,
    disk_selections: std.AutoHashMapUnmanaged(DiskSelectionKey, DiskSelection) = .empty,
    prune_keys: []Key = &.{},
    prune_disks: []u32 = &.{},
    prune_births: []u32 = &.{},
    active_process_ids: []u32 = &.{},
    incomplete_through_tick: i64 = std.math.minInt(i64),
    last_pruned_bucket: i64 = std.math.minInt(i64),
    next_birth_sequence: u64 = 1,
    process_revision: u64 = 0,
    capacity_failures: u64 = 0,
    last_rejection: enum { none, before_process_birth } = .none,

    pub fn init(backing: std.mem.Allocator, config: Config, identity_source: identity.Source) !Owner {
        if (config.window_ms <= 0 or config.retention_ms < config.window_ms or config.bucket_width_ms <= 0 or
            config.maximum_active_pairs == 0 or config.maximum_disks == 0 or config.maximum_cached_processes == 0 or
            std.math.cast(u32, config.maximum_active_pairs) == null or std.math.cast(u32, config.maximum_disks) == null or
            std.math.cast(u32, config.maximum_cached_processes) == null or
            config.byte_cap < @sizeOf(Owner)) return error.InvalidConfig;
        const rounded = try std.math.add(i64, config.window_ms, config.bucket_width_ms - 1);
        const buckets_i64 = try std.math.add(i64, @divTrunc(rounded, config.bucket_width_ms), 2);
        const bucket_count = std.math.cast(usize, buckets_i64) orelse return error.InvalidConfig;
        return .{
            .budget = .{ .backing = backing, .cap = config.byte_cap, .current = @sizeOf(Owner), .peak = @sizeOf(Owner) },
            .config = config,
            .identity_source = identity_source,
            .bucket_count = bucket_count,
        };
    }

    pub fn deinit(self: *Owner) void {
        const allocator = self.budget.allocator();
        var iter = self.pairs.valueIterator();
        while (iter.next()) |pair| allocator.free(pair.buckets);
        self.pairs.deinit(allocator);
        self.disks.deinit(allocator);
        self.process_births.deinit(allocator);
        self.selections.deinit(allocator);
        self.birth_totals.deinit(allocator);
        self.disk_birth_totals.deinit(allocator);
        self.disk_selections.deinit(allocator);
        if (self.prune_keys.len != 0) allocator.free(self.prune_keys);
        if (self.prune_disks.len != 0) allocator.free(self.prune_disks);
        if (self.prune_births.len != 0) allocator.free(self.prune_births);
        if (self.active_process_ids.len != 0) allocator.free(self.active_process_ids);
        self.* = undefined;
    }

    pub fn usage(self: *const Owner) Usage {
        return .{
            .current_bytes = self.budget.current,
            .peak_bytes = self.budget.peak,
            .byte_cap = self.budget.cap,
            .failed_allocations = self.budget.failures,
            .capacity_failures = self.capacity_failures,
        };
    }

    /// Native disk intake also owns one process creation-time lookup per PID
    /// lifecycle. The caller passes the PID resolved at event QPC by the shared
    /// thread map; missing attribution stays PID 0.
    pub fn observe(self: *Owner, event: io.DiskTransfer, tick_ms: i64) !void {
        self.last_rejection = .none;
        if (event.bytes == 0) return;
        if (tick_ms < 0) return error.InvalidTimestamp;
        try self.prune(tick_ms);

        // The managed owner updates observed disk presence before process
        // identity and pre-birth filtering, so retain that order.
        if (event.disk_number) |number| {
            if (self.disks.getPtr(number)) |last_tick| {
                last_tick.* = @max(last_tick.*, tick_ms);
            } else {
                if (self.disks.count() >= self.config.maximum_disks) return self.capacityFailure(tick_ms);
                try self.disks.put(self.budget.allocator(), number, tick_ms);
            }
        }

        const supplied_pid = event.process_id orelse 0;
        // TraceEvent exposes ProcessID as Int32; the managed owner clamps
        // every negative value to its unknown (zero) attribution row.
        const pid = if (supplied_pid <= std.math.maxInt(i32)) supplied_pid else 0;
        const birth = try self.intakeBirth(pid);
        if (birth) |known_birth| {
            if (event.event_filetime_100ns) |filetime| if (filetime < known_birth) {
                self.last_rejection = .before_process_birth;
                return;
            };
        }
        const key: Key = .{
            .process_id = pid,
            .disk_number = event.disk_number,
            .is_write = event.is_write,
            // An unknown timestamp makes the whole PID's selected attribution
            // unavailable, as in the managed owner.
            .birth = if (event.event_filetime_100ns != null) birth else null,
        };
        var pair = self.pairs.getPtr(key) orelse blk: {
            if (self.pairs.count() >= self.config.maximum_active_pairs) return self.capacityFailure(tick_ms);
            const buckets = self.budget.allocator().alloc(Bucket, self.bucket_count) catch |err| {
                self.incomplete_through_tick = @max(self.incomplete_through_tick, tick_ms);
                return err;
            };
            @memset(buckets, .{});
            errdefer self.budget.allocator().free(buckets);
            self.pairs.put(self.budget.allocator(), key, .{ .buckets = buckets, .last_tick = tick_ms }) catch |err| {
                self.incomplete_through_tick = @max(self.incomplete_through_tick, tick_ms);
                return err;
            };
            break :blk self.pairs.getPtr(key).?;
        };
        const id = @divTrunc(tick_ms, self.config.bucket_width_ms);
        const index: usize = @intCast(@mod(id, @as(i64, @intCast(self.bucket_count))));
        var bucket = &pair.buckets[index];
        if (bucket.id != id or bucket.count == 0) bucket.* = .{ .id = id, .first_tick = tick_ms, .last_tick = tick_ms };
        bucket.first_tick = @min(bucket.first_tick, tick_ms);
        bucket.last_tick = @max(bucket.last_tick, tick_ms);
        if (std.math.add(u64, bucket.bytes, event.bytes)) |value| bucket.bytes = value else |_| bucket.invalid = true;
        if (std.math.add(u32, bucket.count, 1)) |value| bucket.count = value else |_| bucket.invalid = true;
        pair.last_tick = @max(pair.last_tick, tick_ms);
    }

    /// Process start/end records invalidate only the identity cache entry. This
    /// does not start, stop or otherwise control the ETW session.
    pub fn processIdentityChanged(self: *Owner, process_id: u32) void {
        _ = self.process_births.remove(process_id);
        self.process_revision +|= 1;
    }

    /// The shared consumer calls this on event loss or generation replacement.
    /// The disk owner never owns or changes that lifecycle itself.
    pub fn invalidate(self: *Owner, tick_ms: i64) void {
        self.incomplete_through_tick = @max(self.incomplete_through_tick, tick_ms);
        self.process_revision +|= 1;
        self.clearTotals();
    }

    /// A new data holder owns a fresh capture interval on the existing trace.
    /// Clear previous holder totals and identity caches without changing control.
    pub fn resetCapture(self: *Owner) void {
        self.clearTotals();
        self.incomplete_through_tick = std.math.minInt(i64);
        self.process_revision +|= 1;
    }

    pub fn snapshot(
        self: *Owner,
        now_tick_ms: i64,
        capture_started_tick_ms: ?i64,
        processes_out: []ProcessRow,
        disk_rows_out: []DiskRow,
        disks_out: []u32,
    ) !Window {
        if (now_tick_ms < 0) return error.InvalidTimestamp;
        try self.prune(now_tick_ms);
        const cutoff = try std.math.sub(i64, now_tick_ms, self.config.window_ms);
        var disk_count: usize = 0;
        var disk_iter = self.disks.iterator();
        while (disk_iter.next()) |entry| {
            if (entry.value_ptr.* <= now_tick_ms) {
                if (disk_count == disks_out.len) return error.OutputTooSmall;
                disks_out[disk_count] = entry.key_ptr.*;
                disk_count += 1;
            }
        }
        std.mem.sort(u32, disks_out[0..disk_count], {}, std.sort.asc(u32));
        const started = capture_started_tick_ms orelse std.math.minInt(i64);
        const warmed = started == std.math.minInt(i64) or now_tick_ms - started >= self.config.window_ms;
        const complete = self.incomplete_through_tick < cutoff and warmed;
        if (!complete) return .{
            .complete = false,
            .processes = processes_out[0..0],
            .disk_rows = disk_rows_out[0..0],
            .disks = disks_out[0..disk_count],
            .start_tick_ms = cutoff,
            .through_tick_ms = now_tick_ms,
            .window_ms = self.config.window_ms,
            .process_revision = self.process_revision,
        };

        self.selections.clearRetainingCapacity();
        self.birth_totals.clearRetainingCapacity();
        self.disk_birth_totals.clearRetainingCapacity();
        self.disk_selections.clearRetainingCapacity();
        var pair_iter = self.pairs.iterator();
        while (pair_iter.next()) |entry| {
            const key = entry.key_ptr.*;
            var pair_bytes: u64 = 0;
            var pair_count: u32 = 0;
            var pair_invalid = false;
            for (entry.value_ptr.buckets) |bucket| {
                if (bucket.count == 0 or bucket.first_tick < cutoff or bucket.last_tick > now_tick_ms) continue;
                if (std.math.add(u64, pair_bytes, bucket.bytes)) |sum| pair_bytes = sum else |_| pair_invalid = true;
                if (std.math.add(u32, pair_count, bucket.count)) |sum| pair_count = sum else |_| pair_invalid = true;
                pair_invalid = pair_invalid or bucket.invalid;
            }
            if (pair_count == 0 and !pair_invalid) continue;

            const selection = self.selections.getOrPut(self.budget.allocator(), key.process_id) catch |err| {
                self.incomplete_through_tick = @max(self.incomplete_through_tick, now_tick_ms);
                return err;
            };
            if (selection.found_existing == false) selection.value_ptr.* = .{};
            if (key.birth) |birth| {
                const birth_key: BirthKey = .{ .process_id = key.process_id, .birth = birth };
                const total = self.birth_totals.getOrPut(self.budget.allocator(), birth_key) catch |err| {
                    self.incomplete_through_tick = @max(self.incomplete_through_tick, now_tick_ms);
                    return err;
                };
                if (!total.found_existing) total.value_ptr.* = .{};
                addPairTotals(total.value_ptr, key.is_write, pair_bytes, pair_count, pair_invalid);
                if (key.disk_number) |disk_number| {
                    const disk_key: DiskBirthKey = .{ .process_id = key.process_id, .disk_number = disk_number, .birth = birth };
                    const disk_total = self.disk_birth_totals.getOrPut(self.budget.allocator(), disk_key) catch |err| {
                        self.incomplete_through_tick = @max(self.incomplete_through_tick, now_tick_ms);
                        return err;
                    };
                    if (!disk_total.found_existing) disk_total.value_ptr.* = .{};
                    addPairTotals(disk_total.value_ptr, key.is_write, pair_bytes, pair_count, pair_invalid);
                }
            } else {
                selection.value_ptr.has_unknown_birth = true;
            }
            if (key.disk_number) |disk_number| {
                const disk_key: DiskSelectionKey = .{ .process_id = key.process_id, .disk_number = disk_number };
                const selected_disk = self.disk_selections.getOrPut(self.budget.allocator(), disk_key) catch |err| {
                    self.incomplete_through_tick = @max(self.incomplete_through_tick, now_tick_ms);
                    return err;
                };
                if (!selected_disk.found_existing) selected_disk.value_ptr.* = .{};
            }
        }
        if (processes_out.len < self.selections.count() or disk_rows_out.len < self.disk_selections.count()) return error.OutputTooSmall;

        const revision_at_start = self.process_revision;
        const seconds = @as(f64, @floatFromInt(self.config.window_ms)) / 1000.0;
        var process_count: usize = 0;
        var selection_iter = self.selections.iterator();
        while (selection_iter.next()) |entry| {
            const process_id = entry.key_ptr.*;
            const selection = entry.value_ptr.*;
            var row: ProcessRow = unavailableProcess(process_id);
            if (process_id == 0) continue;
            {
                if (identity.Lease.open(self.identity_source, process_id)) |lease| {
                    defer lease.close();
                    const birth = lease.creationFiletime() orelse {
                        processes_out[process_count] = row;
                        process_count += 1;
                        continue;
                    };
                    if (selection.has_unknown_birth) {
                        const final_birth = lease.creationFiletime();
                        if (final_birth != null and final_birth.? == birth and self.process_revision == revision_at_start) {
                            if (self.process_births.getPtr(process_id)) |cached| {
                                if (cached.birth == null) cached.birth = birth;
                            }
                        }
                        processes_out[process_count] = row;
                        process_count += 1;
                        continue;
                    }
                    const total = self.birth_totals.get(.{ .process_id = process_id, .birth = birth }) orelse Totals{};
                    if (!total.invalid) {
                        const final_birth = lease.creationFiletime();
                        if (final_birth != null and final_birth.? == birth and self.process_revision == revision_at_start) {
                            if (self.fillDiskRows(process_id, birth)) row = .{
                                .process_id = process_id,
                                .process_birth_filetime_100ns = birth,
                                .available = true,
                                .read_bytes = total.read_bytes,
                                .write_bytes = total.write_bytes,
                                .read_bytes_per_second = @as(f64, @floatFromInt(total.read_bytes)) / seconds,
                                .write_bytes_per_second = @as(f64, @floatFromInt(total.write_bytes)) / seconds,
                                .read_operation_count = total.read_count,
                                .write_operation_count = total.write_count,
                            };
                        }
                    }
                }
            }
            processes_out[process_count] = row;
            process_count += 1;
        }
        std.mem.sort(ProcessRow, processes_out[0..process_count], {}, processRowLessThan);
        const disk_row_count = self.writeDiskRows(disk_rows_out);
        if (self.process_revision != revision_at_start) return .{
            .complete = false,
            .processes = processes_out[0..0],
            .disk_rows = disk_rows_out[0..0],
            .disks = disks_out[0..disk_count],
            .start_tick_ms = cutoff,
            .through_tick_ms = now_tick_ms,
            .window_ms = self.config.window_ms,
            .process_revision = self.process_revision,
        };
        return .{
            .complete = true,
            .processes = processes_out[0..process_count],
            .disk_rows = disk_rows_out[0..disk_row_count],
            .disks = disks_out[0..disk_count],
            .start_tick_ms = cutoff,
            .through_tick_ms = now_tick_ms,
            .window_ms = self.config.window_ms,
            .process_revision = self.process_revision,
        };
    }

    fn intakeBirth(self: *Owner, process_id: u32) !?u64 {
        if (self.process_births.get(process_id)) |entry| return entry.birth;
        var birth: ?u64 = null;
        if (process_id != 0) {
            if (identity.Lease.open(self.identity_source, process_id)) |lease| {
                defer lease.close();
                birth = lease.creationFiletime();
            }
        }
        if (self.process_births.count() >= self.config.maximum_cached_processes) self.evictOldestBirth();
        const sequence = self.next_birth_sequence;
        self.next_birth_sequence +|= 1;
        try self.process_births.put(self.budget.allocator(), process_id, .{ .birth = birth, .sequence = sequence });
        return birth;
    }

    fn evictOldestBirth(self: *Owner) void {
        var oldest_pid: ?u32 = null;
        var oldest_sequence: u64 = std.math.maxInt(u64);
        var iter = self.process_births.iterator();
        while (iter.next()) |entry| {
            if (oldest_pid == null or entry.value_ptr.sequence < oldest_sequence) {
                oldest_pid = entry.key_ptr.*;
                oldest_sequence = entry.value_ptr.sequence;
            }
        }
        if (oldest_pid) |pid| _ = self.process_births.remove(pid);
    }

    fn fillDiskRows(self: *Owner, process_id: u32, birth: u64) bool {
        var any_invalid = false;
        var iter = self.disk_selections.iterator();
        while (iter.next()) |entry| {
            const key = entry.key_ptr.*;
            if (key.process_id != process_id) continue;
            var selection = DiskSelection{ .birth = birth, .available = true };
            if (self.disk_birth_totals.get(.{ .process_id = process_id, .disk_number = key.disk_number, .birth = birth })) |total| {
                if (total.invalid) {
                    selection.available = false;
                    any_invalid = true;
                } else {
                    selection.read_bytes = total.read_bytes;
                    selection.write_bytes = total.write_bytes;
                }
            }
            entry.value_ptr.* = selection;
        }
        if (any_invalid) {
            iter = self.disk_selections.iterator();
            while (iter.next()) |entry| {
                if (entry.key_ptr.process_id == process_id) entry.value_ptr.* = .{};
            }
            return false;
        }
        return true;
    }

    fn writeDiskRows(self: *Owner, rows_out: []DiskRow) usize {
        var count: usize = 0;
        var iter = self.disk_selections.iterator();
        while (iter.next()) |entry| {
            const key = entry.key_ptr.*;
            const selection = entry.value_ptr.*;
            rows_out[count] = .{
                .process_id = key.process_id,
                .disk_number = key.disk_number,
                .process_birth_filetime_100ns = if (selection.available) selection.birth else null,
                .available = selection.available,
                .read_bytes = if (selection.available) selection.read_bytes else 0,
                .write_bytes = if (selection.available) selection.write_bytes else 0,
            };
            count += 1;
        }
        std.mem.sort(DiskRow, rows_out[0..count], {}, diskRowLessThan);
        return count;
    }

    fn capacityFailure(self: *Owner, tick_ms: i64) error{CapacityExceeded} {
        self.capacity_failures +|= 1;
        self.incomplete_through_tick = @max(self.incomplete_through_tick, tick_ms);
        return error.CapacityExceeded;
    }

    fn prune(self: *Owner, now_tick_ms: i64) !void {
        const bucket_id = @divTrunc(now_tick_ms, self.config.bucket_width_ms);
        if (bucket_id <= self.last_pruned_bucket) return;
        // These are scratch for the current occupied maps, not allocations at
        // the configured maxima. Grow before mutating a pruning pass so an
        // allocation failure leaves every current key reachable for cleanup.
        const allocator = self.budget.allocator();
        if (self.prune_keys.len < self.pairs.count())
            self.prune_keys = try allocator.realloc(self.prune_keys, self.pairs.count());
        if (self.active_process_ids.len < self.pairs.count())
            self.active_process_ids = try allocator.realloc(self.active_process_ids, self.pairs.count());
        if (self.prune_disks.len < self.disks.count())
            self.prune_disks = try allocator.realloc(self.prune_disks, self.disks.count());
        if (self.prune_births.len < self.process_births.count())
            self.prune_births = try allocator.realloc(self.prune_births, self.process_births.count());
        self.last_pruned_bucket = bucket_id;
        const pair_expiry = subtractSaturating(subtractSaturating(now_tick_ms, self.config.window_ms), self.config.bucket_width_ms);
        var key_count: usize = 0;
        var pair_iter = self.pairs.iterator();
        while (pair_iter.next()) |entry| {
            if (entry.value_ptr.last_tick < pair_expiry) {
                self.prune_keys[key_count] = entry.key_ptr.*;
                key_count += 1;
            }
        }
        for (self.prune_keys[0..key_count]) |key| {
            if (self.pairs.fetchRemove(key)) |removed| self.budget.allocator().free(removed.value.buckets);
        }

        const disk_expiry = subtractSaturating(now_tick_ms, self.config.retention_ms);
        var disk_count: usize = 0;
        var disk_iter = self.disks.iterator();
        while (disk_iter.next()) |entry| {
            if (entry.value_ptr.* < disk_expiry) {
                self.prune_disks[disk_count] = entry.key_ptr.*;
                disk_count += 1;
            }
        }
        for (self.prune_disks[0..disk_count]) |number| _ = self.disks.remove(number);

        // Collect active PIDs, sort and deduplicate for bounded cache pruning.
        var active_count: usize = 0;
        pair_iter = self.pairs.iterator();
        while (pair_iter.next()) |entry| {
            self.active_process_ids[active_count] = entry.key_ptr.process_id;
            active_count += 1;
        }
        std.mem.sort(u32, self.active_process_ids[0..active_count], {}, std.sort.asc(u32));
        var unique_count: usize = 0;
        for (self.active_process_ids[0..active_count]) |process_id| {
            if (unique_count == 0 or self.active_process_ids[unique_count - 1] != process_id) {
                self.active_process_ids[unique_count] = process_id;
                unique_count += 1;
            }
        }
        var birth_count: usize = 0;
        var birth_iter = self.process_births.iterator();
        while (birth_iter.next()) |entry| {
            if (!containsProcessId(self.active_process_ids[0..unique_count], entry.key_ptr.*)) {
                self.prune_births[birth_count] = entry.key_ptr.*;
                birth_count += 1;
            }
        }
        for (self.prune_births[0..birth_count]) |process_id| _ = self.process_births.remove(process_id);
        if (self.pairs.count() == 0) {
            self.pairs.deinit(allocator);
            self.pairs = .empty;
            self.selections.deinit(allocator);
            self.selections = .empty;
            self.birth_totals.deinit(allocator);
            self.birth_totals = .empty;
            self.disk_birth_totals.deinit(allocator);
            self.disk_birth_totals = .empty;
            self.disk_selections.deinit(allocator);
            self.disk_selections = .empty;
            allocator.free(self.prune_keys);
            self.prune_keys = &.{};
            allocator.free(self.active_process_ids);
            self.active_process_ids = &.{};
        }
        if (self.disks.count() == 0) {
            self.disks.deinit(allocator);
            self.disks = .empty;
            allocator.free(self.prune_disks);
            self.prune_disks = &.{};
        }
        if (self.process_births.count() == 0) {
            self.process_births.deinit(allocator);
            self.process_births = .empty;
            allocator.free(self.prune_births);
            self.prune_births = &.{};
        }
    }

    fn clearTotals(self: *Owner) void {
        var iter = self.pairs.valueIterator();
        while (iter.next()) |pair| self.budget.allocator().free(pair.buckets);
        const allocator = self.budget.allocator();
        self.pairs.deinit(allocator);
        self.pairs = .empty;
        self.disks.deinit(allocator);
        self.disks = .empty;
        self.process_births.deinit(allocator);
        self.process_births = .empty;
        self.selections.deinit(allocator);
        self.selections = .empty;
        self.birth_totals.deinit(allocator);
        self.birth_totals = .empty;
        self.disk_birth_totals.deinit(allocator);
        self.disk_birth_totals = .empty;
        self.disk_selections.deinit(allocator);
        self.disk_selections = .empty;
        allocator.free(self.prune_keys);
        self.prune_keys = &.{};
        allocator.free(self.prune_disks);
        self.prune_disks = &.{};
        allocator.free(self.prune_births);
        self.prune_births = &.{};
        allocator.free(self.active_process_ids);
        self.active_process_ids = &.{};
        self.last_pruned_bucket = std.math.minInt(i64);
    }
};

fn addPairTotals(total: *Totals, is_write: bool, bytes: u64, count: u32, invalid: bool) void {
    if (is_write) {
        if (std.math.add(u64, total.write_bytes, bytes)) |sum| total.write_bytes = sum else |_| total.invalid = true;
        if (std.math.add(u32, total.write_count, count)) |sum| total.write_count = sum else |_| total.invalid = true;
    } else {
        if (std.math.add(u64, total.read_bytes, bytes)) |sum| total.read_bytes = sum else |_| total.invalid = true;
        if (std.math.add(u32, total.read_count, count)) |sum| total.read_count = sum else |_| total.invalid = true;
    }
    total.invalid = total.invalid or invalid;
}

fn unavailableProcess(process_id: u32) ProcessRow {
    return .{
        .process_id = process_id,
        .process_birth_filetime_100ns = null,
        .available = false,
        .read_bytes = 0,
        .write_bytes = 0,
        .read_bytes_per_second = 0,
        .write_bytes_per_second = 0,
        .read_operation_count = 0,
        .write_operation_count = 0,
    };
}

fn processRowLessThan(_: void, left: ProcessRow, right: ProcessRow) bool {
    return left.process_id < right.process_id;
}

fn diskRowLessThan(_: void, left: DiskRow, right: DiskRow) bool {
    if (left.process_id != right.process_id) return left.process_id < right.process_id;
    return left.disk_number < right.disk_number;
}

fn containsProcessId(sorted_ids: []const u32, process_id: u32) bool {
    var low: usize = 0;
    var high = sorted_ids.len;
    while (low < high) {
        const middle = low + (high - low) / 2;
        if (sorted_ids[middle] == process_id) return true;
        if (sorted_ids[middle] < process_id) low = middle + 1 else high = middle;
    }
    return false;
}

fn subtractSaturating(value: i64, delta: i64) i64 {
    return std.math.sub(i64, value, delta) catch std.math.minInt(i64);
}
