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
    boundaries_ready: bool = false,
    records: []Record = &.{},
    // Borrowed-name snapshots exist only during a serialized publication call.
    // Record storage is always owned by the supplied snapshot allocator.
    owns_process_names: bool = true,

    pub fn deinit(self: *Snapshot, allocator: Allocator) void {
        if (self.owns_process_names) for (self.records) |row| allocator.free(row.process_name);
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
    bucket_resize = 9,
    bucket_collision = 10,
};
pub const CapacityResetReason = enum(u32) {
    none = 0,
    active_pair_limit = 1,
    bucket_storage_limit = 2,
    bucket_retention_span = 3,
    bucket_resize_limit = 4,
    bucket_collision = 5,
};
/// One pre-reset snapshot. reason=0 means absent; minInt start means no retained
/// boundary. These are the managed LastCapacityReset fields, without strings.
pub const CapacityReset = extern struct {
    reason: u32 = 0,
    reserved: u32 = 0,
    event_qpc: i64 = 0,
    retained_window_start_qpc: i64 = std.math.minInt(i64),
    active_pair_count: u64 = 0,
    maximum_active_pairs: u64 = 0,
    retained_bucket_slots: u64 = 0,
    maximum_bucket_slots: u64 = 0,
    required_bucket_count: u64 = 0,
    maximum_bucket_count: u64 = 0,
    current_process_count: u64 = 0,
    current_thread_count: u64 = 0,
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
    retained_bucket_bytes: usize,
    peak_retained_bucket_bytes: usize,
    retained_bucket_slots: i64,
    maximum_bucket_slots: i64,
    last_capacity_reset: CapacityReset,
};
const Process = struct { instance_id: i64, process_id: i32, unique_key: u64, start_key: ?i64, name: []u8, current: bool = true, references: usize = 1, index: usize };
const Thread = struct { instance_id: i64, thread_id: i32, process: *Process, current: bool = true, references: usize = 1, index: usize, observed_filetime: i64 = 0 };
const Running = struct { raw_process_id: i32, raw_thread_id: i32, thread: ?*Thread, start_qpc: i64, idle: bool };
const Processor = struct { running: Running, continuous_since: i64, boundary_qpc: i64, boundary_time: i64 };
const PairKey = struct { thread_instance_id: i64, processor_id: i32 };
const Cell = struct { duration: i64 = 0, switches: i32 = 0 };
const Pair = struct { thread: *Thread, cells: std.AutoHashMapUnmanaged(i64, Cell) = .empty, cell_count: i64, last_end_qpc: i64 = 0 };

/// Single-consumer state. The caller serializes seed/event/snapshot access and supplies
/// the persistent owner allocator; snapshots use a separate temporary allocator.
pub const Owner = struct {
    allocator: Allocator,
    window_allocator: Allocator,
    profile: ?*@import("publication_profile.zig").Profile = null,
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
    retention_pair_keys: std.ArrayList(PairKey) = .empty,
    retention_cell_keys: std.ArrayList(i64) = .empty,
    retained_window_start_qpc: i64 = std.math.minInt(i64),
    last_pruned_bucket: i64 = -1,
    last_retention_attempt_bucket: i64 = -1,
    latest_event_qpc: i64 = -1,
    context_switch_floor_qpc: i64 = 0,
    next_process_instance_id: i64 = 0,
    next_thread_instance_id: i64 = 0,
    continuity_epoch: i64 = 0,
    inventory_version: i64 = 0,
    capacity_reset_count: i64 = 0,
    last_capacity_failure: CapacityFailure = .{},
    last_capacity_reset: CapacityReset = .{},
    bucket_storage_bytes: usize = 0,
    bucket_storage_peak: usize = 0,

    pub fn init(allocator: Allocator, config: Config) !Owner {
        return initWithWindowAllocator(allocator, allocator, config);
    }

    /// Run identities and per-window mutable state can use separate backing.
    /// The window allocator context must remain valid until storage is released.
    pub fn initWithWindowAllocator(allocator: Allocator, window_allocator: Allocator, config: Config) !Owner {
        if (config.qpc_frequency <= 0 or config.window_ms <= 0 or config.bucket_ms <= 0 or config.max_pairs == 0 or config.max_cells == 0 or config.max_processes == 0 or config.max_threads == 0 or config.max_processors == 0 or config.max_name_bytes == 0) return error.InvalidConfig;
        const window = try ceilDiv(try std.math.mul(i64, config.qpc_frequency, config.window_ms), 1000);
        const width = try ceilDiv(try std.math.mul(i64, config.qpc_frequency, config.bucket_ms), 1000);
        const buckets = try std.math.add(i64, try ceilDiv(window, width), 2);
        return .{ .allocator = allocator, .window_allocator = window_allocator, .config = config, .window_qpc = window, .bucket_width_qpc = width, .bucket_count = buckets, .maximum_bucket_count = try std.math.mul(i64, buckets, 2), .maximum_bucket_slots = try std.math.mul(i64, std.math.cast(i64, config.max_pairs) orelse return error.InvalidConfig, buckets) };
    }

    pub fn deinit(self: *Owner) void {
        self.clearPairs();
        self.retention_pair_keys.deinit(self.window_allocator);
        self.retention_cell_keys.deinit(self.window_allocator);
        self.pairs.deinit(self.window_allocator);
        self.processors.deinit(self.window_allocator);
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
        // A delayed record from the prior closed window is not a discontinuity
        // in the new one and must not establish a new boundary or mutate time.
        if (lp < 0 or !self.contextSwitchBelongsToWindow(qpc) or !try self.advance(qpc)) return;
        errdefer self.resetContinuity() catch {};
        var since = qpc;
        if (self.processors.get(lp)) |previous| {
            since = previous.continuous_since;
            if (previous.running.raw_thread_id != old_tid or (previous.running.raw_process_id > 0 and old_pid > 0 and previous.running.raw_process_id != old_pid)) {
                since = qpc;
            } else if (!previous.running.idle and previous.running.thread != null) {
                self.appendClosed(previous.running, lp, qpc) catch |err| {
                    if (err != error.CapacityExceeded) return err;
                    self.capacity_reset_count = try std.math.add(i64, self.capacity_reset_count, 1);
                    if (!isContinuityCapacityReason(self.last_capacity_failure.reason)) return err;
                    try self.resetContinuity();
                    since = qpc;
                };
            }
        } else if (self.processors.count() >= self.config.max_processors) return self.capacityExceeded(.processors, self.processors.count() + 1, self.config.max_processors, lp);
        const running = try self.resolve(qpc, new_pid, name, new_tid);
        const old_thread = if (self.processors.get(lp)) |processor| processor.running.thread else null;
        try self.processors.put(self.window_allocator, lp, .{ .running = running, .continuous_since = since, .boundary_qpc = qpc, .boundary_time = time });
        if (running.thread) |thread| thread.references += 1;
        if (old_thread) |thread| self.releaseThread(thread);
        // A managed-style closed-interval rejection was handled above. The
        // failing switch is the first boundary of a fresh CPU window, not a
        // fatal error in the shared trace. Other capacity errors still escape.
    }

    pub fn resetContinuity(self: *Owner) !void {
        var processors = self.processors.valueIterator();
        while (processors.next()) |processor| if (processor.running.thread) |thread| self.releaseThread(thread);
        self.processors.clearAndFree(self.window_allocator);
        self.clearPairs();
        self.continuity_epoch = try std.math.add(i64, self.continuity_epoch, 1);
    }

    /// Explicit window cleanup, after closed output has been copied/published.
    /// Process/thread identity ownership remains with the CPU run.
    pub fn releaseWindowStorage(self: *Owner) !void {
        try self.resetContinuity();
        self.retention_pair_keys.deinit(self.window_allocator);
        self.retention_pair_keys = .empty;
        self.retention_cell_keys.deinit(self.window_allocator);
        self.retention_cell_keys = .empty;
        std.debug.assert(self.bucket_storage_bytes == 0);
    }

    pub fn beginWindow(self: *Owner) !void {
        try self.beginWindowAt(@max(0, self.latest_event_qpc));
    }

    pub fn beginWindowAt(self: *Owner, qpc_floor: i64) !void {
        if (qpc_floor < 0) return error.InvalidWindowBoundary;
        const floor = @max(qpc_floor, @max(0, self.latest_event_qpc));
        try self.releaseWindowStorage();
        self.context_switch_floor_qpc = floor;
    }

    /// The trace owner uses the same predicate for per-consumer rejection
    /// accounting before dispatching a well-formed context-switch record.
    pub fn contextSwitchBelongsToWindow(self: *const Owner, qpc: i64) bool {
        return qpc >= self.context_switch_floor_qpc;
    }

    pub fn setWindowAllocator(self: *Owner, allocator: Allocator) error{WindowStorageInUse}!void {
        if (self.pairs.capacity() != 0 or self.processors.capacity() != 0 or self.retention_pair_keys.capacity != 0 or self.retention_cell_keys.capacity != 0 or self.bucket_storage_bytes != 0)
            return error.WindowStorageInUse;
        self.window_allocator = allocator;
    }

    /// Cold consumer command: change window shape without discarding verified
    /// process/thread lifetimes or retaining cells using the previous shape.
    pub fn configureWindow(self: *Owner, window_ms: i64, bucket_ms: i64, max_pairs: usize) !void {
        if (self.config.window_ms == window_ms and self.config.bucket_ms == bucket_ms and self.config.max_pairs == max_pairs) return;
        var config = self.config;
        config.window_ms = window_ms;
        config.bucket_ms = bucket_ms;
        config.max_pairs = max_pairs;
        var shape = try Owner.initWithWindowAllocator(self.allocator, self.window_allocator, config);
        defer shape.deinit();
        try self.resetContinuity();
        self.config = config;
        self.window_qpc = shape.window_qpc;
        self.bucket_width_qpc = shape.bucket_width_qpc;
        self.bucket_count = shape.bucket_count;
        self.maximum_bucket_count = shape.maximum_bucket_count;
        self.maximum_bucket_slots = shape.maximum_bucket_slots;
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
        self.context_switch_floor_qpc = 0;
        self.next_process_instance_id = 0;
        self.next_thread_instance_id = 0;
        self.last_capacity_failure = .{};
    }

    pub fn getDiagnostics(self: *const Owner) Diagnostics {
        return .{ .qpc_frequency = self.config.qpc_frequency, .window_qpc = self.window_qpc, .maximum_active_pairs = self.config.max_pairs, .active_pair_count = self.pairs.count(), .bucket_count = self.bucket_count, .maximum_bucket_count = self.maximum_bucket_count, .bucket_width_qpc = self.bucket_width_qpc, .retained_cell_count = self.cell_count, .maximum_cell_count = self.config.max_cells, .current_process_count = self.current_processes.count(), .current_thread_count = self.current_threads.count(), .retained_process_identity_count = self.processes.items.len, .retained_thread_identity_count = self.threads.items.len, .continuity_epoch = self.continuity_epoch, .capacity_reset_count = self.capacity_reset_count, .name_bytes = self.name_bytes, .retained_bucket_bytes = self.bucket_storage_bytes, .peak_retained_bucket_bytes = self.bucket_storage_peak, .retained_bucket_slots = self.retained_bucket_slots, .maximum_bucket_slots = self.maximum_bucket_slots, .last_capacity_reset = self.last_capacity_reset };
    }

    pub fn snapshot(self: *Owner, allocator: Allocator, expected: []const i32) !Snapshot {
        return self.snapshotCore(allocator, expected, true);
    }

    /// Only the names borrow owner storage. The single consumer must finish
    /// copying final publication rows before any further owner mutation.
    /// Snapshot.deinit still releases its allocator-owned Record storage.
    pub fn snapshotBorrowedNames(self: *Owner, allocator: Allocator, expected: []const i32) !Snapshot {
        return self.snapshotCore(allocator, expected, false);
    }

    /// Pure closed-boundary coverage; no pruning, row scan or allocation.
    pub fn snapshotMetadata(self: *const Owner, expected: []const i32) Snapshot {
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
        result.boundaries_ready = true;
        const start = @max(0, through - self.window_qpc);
        result.window_start_qpc = start;
        result.measured_through_qpc = through;
        result.measured_through = through_time;
        if (through < self.window_qpc) return result;
        for (expected) |lp| if (self.processors.get(lp).?.continuous_since > start) return result;
        result.is_complete = true;
        return result;
    }

    fn snapshotCore(self: *Owner, allocator: Allocator, expected: []const i32, comptime own_names: bool) !Snapshot {
        var result = self.snapshotMetadata(expected);
        result.owns_process_names = own_names;
        if (result.measured_through == null) return result;
        const start = result.window_start_qpc;
        const through = result.measured_through_qpc;
        try self.advanceRetention(start, .snapshot);
        if (!result.is_complete) return result;
        var rows: std.ArrayList(Record) = .empty;
        errdefer {
            if (own_names) for (rows.items) |row| allocator.free(row.process_name);
            rows.deinit(allocator);
        }
        // Retention has pruned expired pairs. Reserve the actual occupied bound
        // once so arena-backed publication scratch does not retain old growth
        // buffers interleaved with per-row name allocations.
        if (!own_names) try rows.ensureTotalCapacityPrecise(allocator, self.pairs.count());
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
            const name = if (own_names) try allocator.dupe(u8, process.name) else process.name;
            errdefer if (own_names) allocator.free(name);
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
            if (minimum >= self.window_qpc) try self.advanceRetention(minimum - self.window_qpc, .event);
            self.last_retention_attempt_bucket = event_bucket;
        }
        const start = @max(previous.start_qpc, @max(0, self.retained_window_start_qpc));
        if (qpc <= start) return;
        const thread = previous.thread.?;
        const key: PairKey = .{ .thread_instance_id = thread.instance_id, .processor_id = lp };
        if (!self.pairs.contains(key)) {
            if (self.pairs.count() >= self.config.max_pairs) return self.closedCapacityExceeded(.active_pairs, self.pairs.count() + 1, self.config.max_pairs, lp, self.bucket_count);
            if (self.retained_bucket_slots + self.bucket_count > self.maximum_bucket_slots) return self.closedCapacityExceeded(.bucket_slots, @intCast(self.retained_bucket_slots + self.bucket_count), @intCast(self.maximum_bucket_slots), lp, self.bucket_count);
            try self.pairs.put(self.window_allocator, key, .{ .thread = thread, .cell_count = self.bucket_count });
            thread.references += 1;
            self.retained_bucket_slots += self.bucket_count;
        }
        const pair = self.pairs.getPtr(key).?;
        const first = @divTrunc(start, self.bucket_width_qpc);
        const last = @divTrunc(qpc - 1, self.bucket_width_qpc);
        const retained_id = @divTrunc(@max(0, self.retained_window_start_qpc), self.bucket_width_qpc);
        const required = last - retained_id + 1;
        if (required > self.maximum_bucket_count) return self.closedCapacityExceeded(.bucket_span, @intCast(required), @intCast(self.maximum_bucket_count), lp, required);
        if (required > pair.cell_count) try self.resizePair(pair, required);
        var id = first;
        while (id <= last) : (id += 1) {
            if (!pair.cells.contains(id) and self.cell_count >= self.config.max_cells) return self.capacityExceeded(.sparse_cells, self.cell_count + 1, self.config.max_cells, lp);
            const entry = try pair.cells.getOrPut(self.bucketAllocator(), id);
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
        if (slots > self.maximum_bucket_slots) return self.closedCapacityExceeded(.bucket_resize, @intCast(slots), @intCast(self.maximum_bucket_slots), -1, count);
        self.retained_bucket_slots = slots;
        pair.cell_count = count;
    }

    fn advanceRetention(self: *Owner, start: i64, caller: @import("publication_profile.zig").RetentionCaller) !void {
        if (start <= self.retained_window_start_qpc) return;
        const bucket = @divTrunc(start, self.bucket_width_qpc);
        if (bucket <= self.last_pruned_bucket) {
            self.retained_window_start_qpc = start;
            return;
        }
        // Optional diagnostics only after a new bucket passes both guards.
        const profiling: ?*@import("publication_profile.zig").Profile = if (self.profile) |value| (if (value.enabled) value else null) else null;
        const stats = if (profiling) |profile| profile.retentionStats(caller) else null;
        const started = if (profiling) |profile| profile.detailStart() else null;
        defer if (profiling) |profile| profile.finishRetention(started, caller);
        // Reserve from occupied state, not configured maxima. A failed reserve
        // must not advance the cutoff or remove any owner state.
        const pair_count = self.pairs.count();
        try self.retention_pair_keys.ensureTotalCapacityPrecise(self.window_allocator, pair_count);
        self.retention_pair_keys.items.len = pair_count;
        var expired_count: usize = 0;
        var survivor_start: usize = pair_count;
        var maximum_cells: usize = 0;
        const cutoff = bucket * self.bucket_width_qpc;
        var pairs = self.pairs.iterator();
        while (pairs.next()) |entry| {
            if (stats) |value| value.pair_entry_visits +|= 1;
            if (entry.value_ptr.last_end_qpc <= cutoff) {
                self.retention_pair_keys.items[expired_count] = entry.key_ptr.*;
                expired_count += 1;
            } else {
                survivor_start -= 1;
                self.retention_pair_keys.items[survivor_start] = entry.key_ptr.*;
                maximum_cells = @max(maximum_cells, entry.value_ptr.cells.count());
            }
        }
        std.debug.assert(expired_count == survivor_start);
        try self.retention_cell_keys.ensureTotalCapacityPrecise(self.window_allocator, maximum_cells);
        self.retained_window_start_qpc = start;
        self.last_pruned_bucket = bucket;
        // No hash-map iterator survives a mutation. Expired pairs retain their
        // original scan order; survivor order cannot change independent sums.
        for (self.retention_pair_keys.items[0..expired_count]) |key| {
            const pair = self.pairs.getPtr(key).?;
            if (stats) |value| {
                value.pairs_removed +|= 1;
                value.cells_removed +|= pair.cells.count();
            }
            self.cell_count -= pair.cells.count();
            self.retained_bucket_slots -= pair.cell_count;
            pair.cells.deinit(self.bucketAllocator());
            self.releaseThread(pair.thread);
            const removed = self.pairs.remove(key);
            std.debug.assert(removed);
        }
        for (self.retention_pair_keys.items[survivor_start..]) |key| {
            if (stats) |value| value.pair_entry_visits +|= 1;
            const pair = self.pairs.getPtr(key).?;
            self.retention_cell_keys.clearRetainingCapacity();
            var cells = pair.cells.keyIterator();
            while (cells.next()) |id| {
                if (stats) |value| value.cell_entry_visits +|= 1;
                if (id.* < bucket) self.retention_cell_keys.appendAssumeCapacity(id.*);
            }
            for (self.retention_cell_keys.items) |id| {
                const removed = pair.cells.remove(id);
                std.debug.assert(removed);
                self.cell_count -= 1;
                if (stats) |value| value.cells_removed +|= 1;
            }
            if (pair.cell_count > self.bucket_count and @divTrunc(pair.last_end_qpc - 1, self.bucket_width_qpc) - bucket + 1 <= self.bucket_count) try self.resizePair(pair, self.bucket_count);
        }
    }

    fn clearPairs(self: *Owner) void {
        var iterator = self.pairs.valueIterator();
        while (iterator.next()) |pair| {
            pair.cells.deinit(self.bucketAllocator());
            self.releaseThread(pair.thread);
        }
        self.pairs.clearAndFree(self.window_allocator);
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

    fn closedCapacityExceeded(self: *Owner, reason: CapacityReason, observed: u64, limit: u64, lp: i32, required_bucket_count: i64) error{CapacityExceeded} {
        const reset_reason: CapacityResetReason = switch (reason) {
            .active_pairs => .active_pair_limit,
            .bucket_slots => .bucket_storage_limit,
            .bucket_span => .bucket_retention_span,
            .bucket_resize => .bucket_resize_limit,
            .bucket_collision => .bucket_collision,
            else => unreachable,
        };
        self.last_capacity_reset = .{
            .reason = @intFromEnum(reset_reason),
            .event_qpc = self.latest_event_qpc,
            .retained_window_start_qpc = self.retained_window_start_qpc,
            .active_pair_count = self.pairs.count(),
            .maximum_active_pairs = self.config.max_pairs,
            .retained_bucket_slots = @intCast(self.retained_bucket_slots),
            .maximum_bucket_slots = @intCast(self.maximum_bucket_slots),
            .required_bucket_count = @intCast(required_bucket_count),
            .maximum_bucket_count = @intCast(self.maximum_bucket_count),
            .current_process_count = self.current_processes.count(),
            .current_thread_count = self.current_threads.count(),
        };
        return self.capacityExceeded(reason, observed, limit, lp);
    }

    /// Exact requested bytes used by sparse bucket maps, not dense logical slot
    /// accounting and not OS committed bytes. The supplied backing owns commit.
    fn bucketAllocator(self: *Owner) Allocator {
        return .{ .ptr = self, .vtable = &.{ .alloc = allocBucket, .resize = resizeBucket, .remap = remapBucket, .free = freeBucket } };
    }
    fn accountBucket(self: *Owner, old: usize, new: usize) void {
        self.bucket_storage_bytes = self.bucket_storage_bytes - old + new;
        self.bucket_storage_peak = @max(self.bucket_storage_peak, self.bucket_storage_bytes);
    }
    fn allocBucket(ctx: *anyopaque, len: usize, alignment: std.mem.Alignment, ret: usize) ?[*]u8 {
        const self: *Owner = @ptrCast(@alignCast(ctx));
        const ptr = self.window_allocator.rawAlloc(len, alignment, ret) orelse return null;
        self.accountBucket(0, len);
        return ptr;
    }
    fn resizeBucket(ctx: *anyopaque, memory: []u8, alignment: std.mem.Alignment, len: usize, ret: usize) bool {
        const self: *Owner = @ptrCast(@alignCast(ctx));
        if (!self.window_allocator.rawResize(memory, alignment, len, ret)) return false;
        self.accountBucket(memory.len, len);
        return true;
    }
    fn remapBucket(ctx: *anyopaque, memory: []u8, alignment: std.mem.Alignment, len: usize, ret: usize) ?[*]u8 {
        const self: *Owner = @ptrCast(@alignCast(ctx));
        const ptr = self.window_allocator.rawRemap(memory, alignment, len, ret) orelse return null;
        self.accountBucket(memory.len, len);
        return ptr;
    }
    fn freeBucket(ctx: *anyopaque, memory: []u8, alignment: std.mem.Alignment, ret: usize) void {
        const self: *Owner = @ptrCast(@alignCast(ctx));
        self.window_allocator.rawFree(memory, alignment, ret);
        self.accountBucket(memory.len, 0);
    }
};

fn isContinuityCapacityReason(reason: u32) bool {
    return switch (reason) {
        @intFromEnum(CapacityReason.active_pairs), @intFromEnum(CapacityReason.bucket_slots), @intFromEnum(CapacityReason.bucket_span), @intFromEnum(CapacityReason.bucket_resize), @intFromEnum(CapacityReason.bucket_collision) => true,
        else => false,
    };
}

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
