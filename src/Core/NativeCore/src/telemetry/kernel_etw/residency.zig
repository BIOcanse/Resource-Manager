const std = @import("std");
const Allocator = std.mem.Allocator;

pub const Config = struct {
    qpc_frequency: i64,
    window_ms: i64 = 5000,
    bucket_ms: i64 = 50,
    max_pairs: usize = 65536,
    max_cells: usize = 1048576,
    max_processes: usize = 32768,
    max_threads: usize = 131072,
    max_processors: usize = 4096,
    max_name_bytes: usize = 8 * 1024 * 1024,
};
pub const Record = struct {
    process_instance_id: i64,
    process_id: i32,
    process_start_key: ?i64,
    process_name: []const u8,
    thread_instance_id: i64,
    thread_id: i32,
    logical_processor_id: i32,
    execution_time_qpc: i64,
    switch_count: i32,
};
pub const Snapshot = struct {
    qpc_frequency: i64,
    window_start_qpc: i64 = 0,
    measured_through_qpc: i64 = 0,
    measured_through: ?i64 = null,
    is_complete: bool = false,
    records: []Record = &.{},

    pub fn deinit(self: *Snapshot, allocator: Allocator) void {
        for (self.records) |row| allocator.free(row.process_name);
        allocator.free(self.records);
        self.* = undefined;
    }
};
pub const InventorySeed = struct { version: i64 };
pub const SeededProcess = struct { version: i64, process_id: i32, instance_id: i64 };
pub const CapacityReason = enum(u32) {
    none = 0,
    active_pairs = 1,
    bucket_slots = 2,
    bucket_span = 3,
    sparse_cells = 4,
    process_identities = 5,
    thread_identities = 6,
    processors = 7,
    name_bytes = 8,
};
/// Captured before continuity reset; never reconstructed from already-cleared
/// state. The oldest boundary substitutes this switch's prospective boundary.
pub const CapacityFailure = extern struct {
    reason: u32 = 0,
    logical_processor: i32 = -1,
    event_qpc: i64 = -1,
    observed: u64 = 0,
    limit: u64 = 0,
    pairs: u64 = 0,
    cells: u64 = 0,
    retained_slots: u64 = 0,
    retained_start_qpc: i64 = -1,
    oldest_processor: i32 = -1,
    reserved: u32 = 0,
    oldest_boundary_qpc: i64 = -1,
    processors: u64 = 0,
    processes: u64 = 0,
    threads: u64 = 0,
    name_bytes: u64 = 0,
};
pub const Diagnostics = struct {
    qpc_frequency: i64,
    window_qpc: i64,
    maximum_active_pairs: usize,
    active_pair_count: usize,
    bucket_count: i64,
    maximum_bucket_count: i64,
    bucket_width_qpc: i64,
    retained_cell_count: usize,
    maximum_cell_count: usize,
    current_process_count: usize,
    current_thread_count: usize,
    retained_process_identity_count: usize,
    retained_thread_identity_count: usize,
    continuity_epoch: i64,
    capacity_reset_count: i64,
    name_bytes: usize,
};
const Process = struct { instance_id: i64, process_id: i32, unique_key: u64, start_key: ?i64, name: []u8, current: bool = true, references: usize = 1, index: usize };
const Thread = struct { instance_id: i64, thread_id: i32, process: *Process, current: bool = true, references: usize = 1, index: usize };
const Running = struct { raw_process_id: i32, raw_thread_id: i32, thread: ?*Thread, start_qpc: i64, idle: bool };
const Processor = struct { running: Running, continuous_since: i64, boundary_qpc: i64, boundary_time: i64 };
const PairKey = struct { thread_instance_id: i64, processor_id: i32 };
const Cell = struct { duration: i64 = 0, switches: i32 = 0 };
const Pair = struct { thread: *Thread, cells: std.AutoHashMapUnmanaged(i64, Cell) = .empty, cell_count: i64, last_end_qpc: i64 = 0 };

/// Single-consumer state. The caller serializes seed/event/snapshot access and supplies
/// the persistent owner allocator; snapshots use a separate temporary allocator.
pub const Owner = struct {
    allocator: Allocator,
    config: Config,
    window_qpc: i64,
    bucket_width_qpc: i64,
    bucket_count: i64,
    maximum_bucket_count: i64,
    maximum_bucket_slots: i64,
    retained_bucket_slots: i64 = 0,
    cell_count: usize = 0,
    name_bytes: usize = 0,
    processes: std.ArrayList(*Process) = .empty,
    threads: std.ArrayList(*Thread) = .empty,
    current_processes: std.AutoHashMapUnmanaged(i32, *Process) = .empty,
    current_threads: std.AutoHashMapUnmanaged(i32, *Thread) = .empty,
    processors: std.AutoHashMapUnmanaged(i32, Processor) = .empty,
    pairs: std.AutoHashMapUnmanaged(PairKey, Pair) = .empty,
    retained_window_start_qpc: i64 = std.math.minInt(i64),
    last_pruned_bucket: i64 = -1,
    last_retention_attempt_bucket: i64 = -1,
    latest_event_qpc: i64 = -1,
    next_process_instance_id: i64 = 0,
    next_thread_instance_id: i64 = 0,
    continuity_epoch: i64 = 0,
    inventory_version: i64 = 0,
    capacity_reset_count: i64 = 0,
    last_capacity_failure: CapacityFailure = .{},

    pub fn init(allocator: Allocator, config: Config) !Owner {
        if (config.qpc_frequency <= 0 or config.window_ms <= 0 or config.bucket_ms <= 0 or config.max_pairs == 0 or config.max_cells == 0 or config.max_processes == 0 or config.max_threads == 0 or config.max_processors == 0 or config.max_name_bytes == 0) return error.InvalidConfig;
        const window = try ceilDiv(try std.math.mul(i64, config.qpc_frequency, config.window_ms), 1000);
        const width = try ceilDiv(try std.math.mul(i64, config.qpc_frequency, config.bucket_ms), 1000);
        const buckets = try std.math.add(i64, try ceilDiv(window, width), 2);
        return .{ .allocator = allocator, .config = config, .window_qpc = window, .bucket_width_qpc = width, .bucket_count = buckets, .maximum_bucket_count = try std.math.mul(i64, buckets, 2), .maximum_bucket_slots = try std.math.mul(i64, std.math.cast(i64, config.max_pairs) orelse return error.InvalidConfig, buckets) };
    }

    pub fn deinit(self: *Owner) void {
        self.clearPairs();
        self.pairs.deinit(self.allocator);
        self.processors.deinit(self.allocator);
        self.current_threads.deinit(self.allocator);
        self.current_processes.deinit(self.allocator);
        for (self.threads.items) |thread| self.allocator.destroy(thread);
        for (self.processes.items) |process| {
            self.allocator.free(process.name);
            self.allocator.destroy(process);
        }
        self.threads.deinit(self.allocator);
        self.processes.deinit(self.allocator);
        self.* = undefined;
    }

    pub fn observeProcessStart(self: *Owner, qpc: i64, time: i64, pid: i32, unique: u64, start: ?i64, name: ?[]const u8) !void {
        errdefer self.resetContinuity() catch {};
        _ = time;
        if (pid <= 0 or !try self.advance(qpc)) return;
        if (self.current_processes.get(pid)) |process| {
            if (sameProcess(process, unique, start)) {
                try self.updateIdentity(process, unique, start, name);
                return;
            }
            self.retireProcess(process);
        }
        _ = try self.createProcess(pid, unique, start, name);
    }

    pub fn seedProcess(self: *Owner, pid: i32, start: ?i64, name: ?[]const u8) !void {
        errdefer self.resetContinuity() catch {};
        if (pid > 0) _ = try self.seedProcessCore(pid, start, name);
    }

    fn seedProcessCore(self: *Owner, pid: i32, start: ?i64, name: ?[]const u8) !*Process {
        if (self.current_processes.get(pid)) |process| {
            if (process.unique_key != 0) {
                if (positive(process.start_key) and process.start_key == start) try self.updateIdentity(process, 0, start, name);
                return process;
            }
            if (positive(start) and (!positive(process.start_key) or start != process.start_key)) {
                self.retireProcess(process);
            } else {
                try self.updateIdentity(process, 0, start, name);
                return process;
            }
        }
        return self.createProcess(pid, 0, start, name);
    }

    pub fn seedThread(self: *Owner, pid: i32, tid: i32) !void {
        errdefer self.resetContinuity() catch {};
        if (pid <= 0 or tid <= 0) return;
        if (self.current_processes.get(pid)) |process| try self.seedThreadCore(process, tid);
    }

    fn seedThreadCore(self: *Owner, process: *Process, tid: i32) !void {
        if (self.current_threads.get(tid)) |thread| {
            if (thread.process == process) return;
            self.retireThread(thread);
        }
        try self.addThread(process, tid);
    }

    pub fn captureInventorySeed(self: *const Owner) InventorySeed {
        return .{ .version = self.inventory_version };
    }

    pub fn seedInventoryProcess(self: *Owner, seed: InventorySeed, pid: i32, start: ?i64, name: ?[]const u8) !?SeededProcess {
        errdefer self.resetContinuity() catch {};
        if (pid <= 0 or seed.version != self.inventory_version) return null;
        if (self.current_processes.get(pid)) |process| {
            if (positive(start) and positive(process.start_key) and process.start_key != start) return null;
            if (process.unique_key != 0 and !positive(process.start_key)) return null;
        }
        const process = try self.seedProcessCore(pid, start, name);
        return .{ .version = seed.version, .process_id = pid, .instance_id = process.instance_id };
    }

    pub fn seedInventoryThread(self: *Owner, seed: SeededProcess, tid: i32) !void {
        errdefer self.resetContinuity() catch {};
        if (tid <= 0 or seed.version != self.inventory_version) return;
        const process = self.current_processes.get(seed.process_id) orelse return;
        if (process.instance_id == seed.instance_id) try self.seedThreadCore(process, tid);
    }

    pub fn observeProcessEnd(self: *Owner, qpc: i64, time: i64, pid: i32, unique: u64) !void {
        _ = time;
        if (pid <= 0 or !try self.advance(qpc)) return;
        const process = self.current_processes.get(pid) orelse return;
        if (unique != 0 and process.unique_key != 0 and unique != process.unique_key) return;
        self.retireProcess(process);
    }

    pub fn observeThreadStart(self: *Owner, qpc: i64, time: i64, pid: i32, tid: i32) !void {
        errdefer self.resetContinuity() catch {};
        _ = time;
        if (pid <= 0 or tid <= 0 or !try self.advance(qpc)) return;
        if (self.current_processes.get(pid)) |process| try self.seedThreadCore(process, tid);
    }

    pub fn observeThreadEnd(self: *Owner, qpc: i64, time: i64, pid: i32, tid: i32) !void {
        _ = time;
        if (pid <= 0 or tid <= 0 or !try self.advance(qpc)) return;
        const thread = self.current_threads.get(tid) orelse return;
        if (thread.process.process_id == pid) self.retireThread(thread);
    }

    pub fn observeContextSwitch(self: *Owner, qpc: i64, time: i64, lp: i32, old_pid: i32, old_tid: i32, new_pid: i32, name: ?[]const u8, new_tid: i32) !void {
        if (lp < 0 or !try self.advance(qpc)) return;
        var preserve_capacity_boundary = false;
        errdefer if (!preserve_capacity_boundary) self.resetContinuity() catch {};
        var since = qpc;
        var capacity_failure = false;
        if (self.processors.get(lp)) |previous| {
            since = previous.continuous_since;
            if (previous.running.raw_thread_id != old_tid or (previous.running.raw_process_id > 0 and old_pid > 0 and previous.running.raw_process_id != old_pid)) {
                since = qpc;
            } else if (!previous.running.idle and previous.running.thread != null) {
                self.appendClosed(previous.running, lp, qpc) catch |err| {
                    if (err != error.CapacityExceeded) return err;
                    self.capacity_reset_count = try std.math.add(i64, self.capacity_reset_count, 1);
                    try self.resetContinuity();
                    since = qpc;
                    capacity_failure = true;
                };
            }
        } else if (self.processors.count() >= self.config.max_processors) return self.capacityExceeded(.processors, self.processors.count() + 1, self.config.max_processors, lp);
        const running = try self.resolve(qpc, new_pid, name, new_tid);
        const old_thread = if (self.processors.get(lp)) |processor| processor.running.thread else null;
        try self.processors.put(self.allocator, lp, .{ .running = running, .continuous_since = since, .boundary_qpc = qpc, .boundary_time = time });
        if (running.thread) |thread| thread.references += 1;
        if (old_thread) |thread| self.releaseThread(thread);
        if (capacity_failure) {
            // The C# owner begins the next interval at the failing switch after
            // clearing totals. Retain that incomplete boundary and new running
            // lifetime while still reporting the native capacity error.
            preserve_capacity_boundary = true;
            return error.CapacityExceeded;
        }
    }

    pub fn resetContinuity(self: *Owner) !void {
        var processors = self.processors.valueIterator();
        while (processors.next()) |processor| if (processor.running.thread) |thread| self.releaseThread(thread);
        self.processors.clearAndFree(self.allocator);
        self.clearPairs();
        self.continuity_epoch = try std.math.add(i64, self.continuity_epoch, 1);
    }

    pub fn resetAfterEventLoss(self: *Owner) !void {
        self.inventory_version = try std.math.add(i64, self.inventory_version, 1);
        // Current-map iterators are not retained across removals.
        while (self.current_threads.count() != 0) {
            var threads = self.current_threads.valueIterator();
            self.retireThread(threads.next().?.*);
        }
        while (self.current_processes.count() != 0) {
            var processes = self.current_processes.valueIterator();
            const process = processes.next().?.*;
            _ = self.current_processes.remove(process.process_id);
            process.current = false;
            self.releaseProcess(process);
        }
        self.current_processes.clearAndFree(self.allocator);
        self.current_threads.clearAndFree(self.allocator);
        try self.resetContinuity();
    }

    pub fn clear(self: *Owner) !void {
        try self.resetAfterEventLoss();
        self.latest_event_qpc = -1;
        self.next_process_instance_id = 0;
        self.next_thread_instance_id = 0;
        self.last_capacity_failure = .{};
    }

    pub fn getDiagnostics(self: *const Owner) Diagnostics {
        return .{ .qpc_frequency = self.config.qpc_frequency, .window_qpc = self.window_qpc, .maximum_active_pairs = self.config.max_pairs, .active_pair_count = self.pairs.count(), .bucket_count = self.bucket_count, .maximum_bucket_count = self.maximum_bucket_count, .bucket_width_qpc = self.bucket_width_qpc, .retained_cell_count = self.cell_count, .maximum_cell_count = self.config.max_cells, .current_process_count = self.current_processes.count(), .current_thread_count = self.current_threads.count(), .retained_process_identity_count = self.processes.items.len, .retained_thread_identity_count = self.threads.items.len, .continuity_epoch = self.continuity_epoch, .capacity_reset_count = self.capacity_reset_count, .name_bytes = self.name_bytes };
    }

    pub fn snapshot(self: *Owner, allocator: Allocator, expected: []const i32) !Snapshot {
        var result: Snapshot = .{ .qpc_frequency = self.config.qpc_frequency };
        if (expected.len == 0) return result;
        var through: i64 = std.math.maxInt(i64);
        var through_time: ?i64 = null;
        for (expected, 0..) |lp, i| {
            for (expected[0..i]) |previous| if (lp == previous) return result;
            const processor = self.processors.get(lp) orelse return result;
            if (through_time == null or processor.boundary_qpc < through) {
                through = processor.boundary_qpc;
                through_time = processor.boundary_time;
            }
        }
        const start = @max(0, through - self.window_qpc);
        try self.advanceRetention(start);
        result.window_start_qpc = start;
        result.measured_through_qpc = through;
        result.measured_through = through_time;
        if (through < self.window_qpc) return result;
        for (expected) |lp| if (self.processors.get(lp).?.continuous_since > start) return result;
        var rows: std.ArrayList(Record) = .empty;
        errdefer {
            for (rows.items) |row| allocator.free(row.process_name);
            rows.deinit(allocator);
        }
        const first = @divTrunc(start, self.bucket_width_qpc);
        const last = @divTrunc(through - 1, self.bucket_width_qpc);
        var iterator = self.pairs.iterator();
        while (iterator.next()) |entry| {
            const lp = entry.key_ptr.processor_id;
            if (std.mem.indexOfScalar(i32, expected, lp) == null) continue;
            var duration: i64 = 0;
            var switches: i32 = 0;
            const boundary = self.processors.get(lp).?.boundary_qpc;
            var cells = entry.value_ptr.cells.iterator();
            while (cells.next()) |cell| {
                const id = cell.key_ptr.*;
                if (id < first or id > last) continue;
                if (id > first and id < last) {
                    duration = try std.math.add(i64, duration, cell.value_ptr.duration);
                    switches = try std.math.add(i32, switches, cell.value_ptr.switches);
                    continue;
                }
                const bucket_start = try std.math.mul(i64, id, self.bucket_width_qpc);
                const end = if (bucket_start > std.math.maxInt(i64) - self.bucket_width_qpc) boundary else @min(boundary, bucket_start + self.bucket_width_qpc);
                const from = @max(start, bucket_start);
                const to = @min(through, end);
                const covered = end - bucket_start;
                if (covered <= 0 or to <= from) continue;
                const overlap = to - from;
                const value: i64 = if (overlap == covered) cell.value_ptr.duration else @intCast(@divTrunc(@as(i128, cell.value_ptr.duration) * overlap, covered));
                duration = try std.math.add(i64, duration, value);
                if (bucket_start >= start) switches = try std.math.add(i32, switches, cell.value_ptr.switches);
            }
            if (duration == 0 and switches == 0) continue;
            const thread = entry.value_ptr.thread;
            const process = thread.process;
            const name = try allocator.dupe(u8, process.name);
            errdefer allocator.free(name);
            try rows.append(allocator, .{ .process_instance_id = process.instance_id, .process_id = process.process_id, .process_start_key = process.start_key, .process_name = name, .thread_instance_id = thread.instance_id, .thread_id = thread.thread_id, .logical_processor_id = lp, .execution_time_qpc = duration, .switch_count = switches });
        }
        result.records = try rows.toOwnedSlice(allocator);
        result.is_complete = true;
        return result;
    }

    fn createProcess(self: *Owner, pid: i32, unique: u64, start: ?i64, name: ?[]const u8) !*Process {
        if (self.processes.items.len >= self.config.max_processes) return self.capacityExceeded(.process_identities, self.processes.items.len + 1, self.config.max_processes, -1);
        const owned_name = try self.copyName(pid, name, null);
        errdefer {
            self.name_bytes -= owned_name.len;
            self.allocator.free(owned_name);
        }
        const process = try self.allocator.create(Process);
        errdefer self.allocator.destroy(process);
        const next = try std.math.add(i64, self.next_process_instance_id, 1);
        process.* = .{ .instance_id = next, .process_id = pid, .unique_key = unique, .start_key = start, .name = owned_name, .index = self.processes.items.len };
        try self.processes.ensureUnusedCapacity(self.allocator, 1);
        try self.current_processes.put(self.allocator, pid, process);
        self.processes.appendAssumeCapacity(process);
        self.next_process_instance_id = next;
        return process;
    }

    fn addThread(self: *Owner, process: *Process, tid: i32) !void {
        if (self.threads.items.len >= self.config.max_threads) return self.capacityExceeded(.thread_identities, self.threads.items.len + 1, self.config.max_threads, -1);
        const thread = try self.allocator.create(Thread);
        errdefer self.allocator.destroy(thread);
        const next = try std.math.add(i64, self.next_thread_instance_id, 1);
        thread.* = .{ .instance_id = next, .thread_id = tid, .process = process, .index = self.threads.items.len };
        try self.threads.ensureUnusedCapacity(self.allocator, 1);
        try self.current_threads.put(self.allocator, tid, thread);
        self.threads.appendAssumeCapacity(thread);
        process.references += 1;
        self.next_thread_instance_id = next;
    }

    fn updateIdentity(self: *Owner, process: *Process, unique: u64, start: ?i64, name: ?[]const u8) !void {
        const normalized = if (name) |candidate| trimName(candidate) else "";
        if (normalized.len != 0 and !std.mem.eql(u8, normalized, process.name)) {
            const replacement = try self.copyName(process.process_id, normalized, process.name);
            self.name_bytes -= process.name.len;
            self.allocator.free(process.name);
            process.name = replacement;
        }
        if (process.unique_key == 0) process.unique_key = unique;
        if (!positive(process.start_key)) process.start_key = start;
    }

    fn copyName(self: *Owner, pid: i32, candidate: ?[]const u8, existing: ?[]const u8) ![]u8 {
        var fallback: [32]u8 = undefined;
        const normalized = if (candidate) |value| trimName(value) else "";
        const selected = if (normalized.len != 0) normalized else if (existing) |value| value else try std.fmt.bufPrint(&fallback, "PID {d}", .{pid});
        if (selected.len > self.config.max_name_bytes - self.name_bytes) return self.capacityExceeded(.name_bytes, self.name_bytes +| selected.len, self.config.max_name_bytes, -1);
        const result = try self.allocator.dupe(u8, selected);
        self.name_bytes += result.len;
        return result;
    }

    fn retireThread(self: *Owner, thread: *Thread) void {
        if (!thread.current) return;
        if (self.current_threads.get(thread.thread_id) == thread) _ = self.current_threads.remove(thread.thread_id);
        thread.current = false;
        self.releaseThread(thread);
    }

    fn retireProcess(self: *Owner, process: *Process) void {
        if (self.current_processes.get(process.process_id) == process) _ = self.current_processes.remove(process.process_id);
        process.current = false;
        var i: usize = 0;
        while (i < self.threads.items.len) {
            const thread = self.threads.items[i];
            if (thread.process == process and thread.current) {
                const destroys = thread.references == 1;
                self.retireThread(thread);
                if (destroys) continue;
            }
            i += 1;
        }
        self.releaseProcess(process);
    }

    fn advance(self: *Owner, qpc: i64) !bool {
        if (qpc < 0) return false;
        if (self.latest_event_qpc >= 0 and qpc < self.latest_event_qpc) {
            try self.resetContinuity();
            return false;
        }
        self.latest_event_qpc = qpc;
        return true;
    }

    fn resolve(self: *Owner, qpc: i64, pid: i32, name: ?[]const u8, tid: i32) !Running {
        var result: Running = .{ .raw_process_id = pid, .raw_thread_id = tid, .thread = null, .start_qpc = qpc, .idle = tid == 0 };
        if (tid <= 0) return result;
        const thread = self.current_threads.get(tid) orelse return result;
        if (pid > 0 and thread.process.process_id != pid) return result;
        if (name != null) try self.updateIdentity(thread.process, 0, null, name);
        result.raw_process_id = thread.process.process_id;
        result.thread = thread;
        return result;
    }

    fn appendClosed(self: *Owner, previous: Running, lp: i32, qpc: i64) !void {
        const event_bucket = @divTrunc(qpc, self.bucket_width_qpc);
        if (event_bucket != self.last_retention_attempt_bucket or self.pairs.count() >= self.config.max_pairs) {
            var minimum = qpc;
            var processors = self.processors.iterator();
            while (processors.next()) |entry| if (entry.key_ptr.* != lp) {
                minimum = @min(minimum, entry.value_ptr.boundary_qpc);
            };
            if (minimum >= self.window_qpc) try self.advanceRetention(minimum - self.window_qpc);
            self.last_retention_attempt_bucket = event_bucket;
        }
        const start = @max(previous.start_qpc, @max(0, self.retained_window_start_qpc));
        if (qpc <= start) return;
        const thread = previous.thread.?;
        const key: PairKey = .{ .thread_instance_id = thread.instance_id, .processor_id = lp };
        if (!self.pairs.contains(key)) {
            if (self.pairs.count() >= self.config.max_pairs) return self.capacityExceeded(.active_pairs, self.pairs.count() + 1, self.config.max_pairs, lp);
            if (self.retained_bucket_slots + self.bucket_count > self.maximum_bucket_slots) return self.capacityExceeded(.bucket_slots, @intCast(self.retained_bucket_slots + self.bucket_count), @intCast(self.maximum_bucket_slots), lp);
            try self.pairs.put(self.allocator, key, .{ .thread = thread, .cell_count = self.bucket_count });
            thread.references += 1;
            self.retained_bucket_slots += self.bucket_count;
        }
        const pair = self.pairs.getPtr(key).?;
        const first = @divTrunc(start, self.bucket_width_qpc);
        const last = @divTrunc(qpc - 1, self.bucket_width_qpc);
        const retained_id = @divTrunc(@max(0, self.retained_window_start_qpc), self.bucket_width_qpc);
        const required = last - retained_id + 1;
        if (required > self.maximum_bucket_count) return self.capacityExceeded(.bucket_span, @intCast(required), @intCast(self.maximum_bucket_count), lp);
        if (required > pair.cell_count) try self.resizePair(pair, required);
        var id = first;
        while (id <= last) : (id += 1) {
            if (!pair.cells.contains(id) and self.cell_count >= self.config.max_cells) return self.capacityExceeded(.sparse_cells, self.cell_count + 1, self.config.max_cells, lp);
            const entry = try pair.cells.getOrPut(self.allocator, id);
            if (!entry.found_existing) {
                entry.value_ptr.* = .{};
                self.cell_count += 1;
            }
            const bucket_start = try std.math.mul(i64, id, self.bucket_width_qpc);
            const end = if (bucket_start > std.math.maxInt(i64) - self.bucket_width_qpc) qpc else @min(qpc, bucket_start + self.bucket_width_qpc);
            entry.value_ptr.duration = try std.math.add(i64, entry.value_ptr.duration, end - @max(start, bucket_start));
            if (previous.start_qpc >= bucket_start and previous.start_qpc - bucket_start < self.bucket_width_qpc) entry.value_ptr.switches = try std.math.add(i32, entry.value_ptr.switches, 1);
        }
        pair.last_end_qpc = qpc;
    }

    fn resizePair(self: *Owner, pair: *Pair, count: i64) !void {
        const slots = self.retained_bucket_slots - pair.cell_count + count;
        if (slots > self.maximum_bucket_slots) return self.capacityExceeded(.bucket_slots, @intCast(slots), @intCast(self.maximum_bucket_slots), -1);
        self.retained_bucket_slots = slots;
        pair.cell_count = count;
    }

    fn advanceRetention(self: *Owner, start: i64) !void {
        if (start <= self.retained_window_start_qpc) return;
        self.retained_window_start_qpc = start;
        const bucket = @divTrunc(start, self.bucket_width_qpc);
        if (bucket <= self.last_pruned_bucket) return;
        self.last_pruned_bucket = bucket;
        // Remove one entry then restart: no iterator survives a hash-map mutation.
        while (true) {
            var removed = false;
            var pairs = self.pairs.iterator();
            while (pairs.next()) |entry| {
                if (entry.value_ptr.last_end_qpc <= bucket * self.bucket_width_qpc) {
                    self.cell_count -= entry.value_ptr.cells.count();
                    self.retained_bucket_slots -= entry.value_ptr.cell_count;
                    entry.value_ptr.cells.deinit(self.allocator);
                    self.releaseThread(entry.value_ptr.thread);
                    const key = entry.key_ptr.*;
                    _ = self.pairs.remove(key);
                    removed = true;
                    break;
                }
            }
            if (!removed) break;
        }
        var pairs = self.pairs.valueIterator();
        while (pairs.next()) |pair| {
            while (true) {
                var removed = false;
                var cells = pair.cells.keyIterator();
                while (cells.next()) |id| if (id.* < bucket) {
                    const key = id.*;
                    _ = pair.cells.remove(key);
                    self.cell_count -= 1;
                    removed = true;
                    break;
                };
                if (!removed) break;
            }
            if (pair.cell_count > self.bucket_count and @divTrunc(pair.last_end_qpc - 1, self.bucket_width_qpc) - bucket + 1 <= self.bucket_count) try self.resizePair(pair, self.bucket_count);
        }
    }

    fn clearPairs(self: *Owner) void {
        var iterator = self.pairs.valueIterator();
        while (iterator.next()) |pair| {
            pair.cells.deinit(self.allocator);
            self.releaseThread(pair.thread);
        }
        self.pairs.clearAndFree(self.allocator);
        self.cell_count = 0;
        self.retained_bucket_slots = 0;
        self.retained_window_start_qpc = std.math.minInt(i64);
        self.last_pruned_bucket = -1;
        self.last_retention_attempt_bucket = -1;
    }

    fn releaseThread(self: *Owner, thread: *Thread) void {
        std.debug.assert(thread.references > 0);
        thread.references -= 1;
        if (thread.references != 0) return;
        const index = thread.index;
        _ = self.threads.swapRemove(index);
        if (index < self.threads.items.len) self.threads.items[index].index = index;
        const process = thread.process;
        self.allocator.destroy(thread);
        self.releaseProcess(process);
    }

    fn releaseProcess(self: *Owner, process: *Process) void {
        std.debug.assert(process.references > 0);
        process.references -= 1;
        if (process.references != 0) return;
        const index = process.index;
        _ = self.processes.swapRemove(index);
        if (index < self.processes.items.len) self.processes.items[index].index = index;
        self.name_bytes -= process.name.len;
        self.allocator.free(process.name);
        self.allocator.destroy(process);
    }

    fn capacityExceeded(self: *Owner, reason: CapacityReason, observed: u64, limit: u64, lp: i32) error{CapacityExceeded} {
        var oldest_qpc: i64 = std.math.maxInt(i64);
        var oldest_lp: i32 = -1;
        var processors = self.processors.iterator();
        while (processors.next()) |entry| {
            const boundary = if (entry.key_ptr.* == lp) self.latest_event_qpc else entry.value_ptr.boundary_qpc;
            if (boundary < oldest_qpc) {
                oldest_qpc = boundary;
                oldest_lp = entry.key_ptr.*;
            }
        }
        self.last_capacity_failure = .{ .reason = @intFromEnum(reason), .logical_processor = lp, .event_qpc = self.latest_event_qpc, .observed = observed, .limit = limit, .pairs = self.pairs.count(), .cells = self.cell_count, .retained_slots = @intCast(self.retained_bucket_slots), .retained_start_qpc = self.retained_window_start_qpc, .oldest_processor = oldest_lp, .oldest_boundary_qpc = if (oldest_lp == -1) -1 else oldest_qpc, .processors = self.processors.count(), .processes = self.processes.items.len, .threads = self.threads.items.len, .name_bytes = self.name_bytes };
        return error.CapacityExceeded;
    }
};

fn positive(value: ?i64) bool {
    return if (value) |v| v > 0 else false;
}
fn sameProcess(process: *const Process, unique: u64, start: ?i64) bool {
    if (process.unique_key != 0 and unique != 0) return process.unique_key == unique;
    if (positive(process.start_key) and positive(start)) return process.start_key == start;
    return false;
}
fn ceilDiv(numerator: i64, denominator: i64) !i64 {
    return @divTrunc(try std.math.add(i64, numerator, denominator - 1), denominator);
}

// .NET Char.IsWhiteSpace / String.Trim whitespace, encoded as UTF-8.
fn trimName(value: []const u8) []const u8 {
    var from: usize = 0;
    var to = value.len;
    while (from < to) {
        const n = whitespacePrefix(value[from..to]);
        if (n == 0) break;
        from += n;
    }
    while (to > from) {
        var begin = to - 1;
        while (begin > from and value[begin] & 0xc0 == 0x80) : (begin -= 1) {}
        if (whitespacePrefix(value[begin..to]) != to - begin) break;
        to = begin;
    }
    return value[from..to];
}
fn whitespacePrefix(value: []const u8) usize {
    if (value.len == 0) return 0;
    if (value[0] == 0x20 or (value[0] >= 9 and value[0] <= 13)) return 1;
    if (value.len >= 2 and value[0] == 0xc2 and (value[1] == 0x85 or value[1] == 0xa0)) return 2;
    if (value.len >= 3) {
        if (value[0] == 0xe1 and value[1] == 0x9a and value[2] == 0x80) return 3;
        if (value[0] == 0xe2 and value[1] == 0x80 and ((value[2] >= 0x80 and value[2] <= 0x8a) or value[2] == 0xa8 or value[2] == 0xa9 or value[2] == 0xaf)) return 3;
        if (value[0] == 0xe2 and value[1] == 0x81 and value[2] == 0x9f) return 3;
        if (value[0] == 0xe3 and value[1] == 0x80 and value[2] == 0x80) return 3;
    }
    return 0;
}
