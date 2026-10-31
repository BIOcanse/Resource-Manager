const std = @import("std");
const Allocator = std.mem.Allocator;

pub const Config = struct {
    qpc_frequency: i64,
    window_ms: i64 = 5000,
    max_pairs: usize = 65536,
    max_processes: usize = 32768,
    max_threads: usize = 131072,
    max_processors: usize = 4096,
    max_name_bytes: usize = 8 * 1024 * 1024,
    /// Expected logical processors. Empty: the caller sets them with setProcessors.
    processors: []const i32 = &.{},
};
/// A continuous window keeps 2n + 2 ring cells per pair; 2047 keeps one ring within 64 KiB.
pub const maximum_slots_per_window: u32 = 2047;
pub const pair_record_bytes: usize = @sizeOf(PairRecord);
pub const records_per_chunk: usize = 2730;
const ring_chunk_bytes: usize = 65536;
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
    ring_span = 3,
    process_identities = 5,
    thread_identities = 6,
    processors = 7,
    name_bytes = 8,
};
pub const CapacityResetReason = enum(u32) {
    none = 0,
    active_pair_limit = 1,
    ring_span = 3,
};
/// One pre-reset snapshot. reason=0 means absent; minInt start means no window
/// start (S) was established. These are the managed LastCapacityReset fields.
pub const CapacityReset = extern struct {
    reason: u32 = 0,
    reserved: u32 = 0,
    event_qpc: i64 = 0,
    window_start_qpc: i64 = std.math.minInt(i64),
    active_pair_count: u64 = 0,
    maximum_active_pairs: u64 = 0,
    required_slot_span: u64 = 0,
    ring_slots: u64 = 0,
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
    window_start_qpc: i64 = -1,
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
    slots_per_window: u32,
    ring_slots: u32,
    pair_record_bytes: usize,
    window_storage_bytes: usize,
    peak_window_storage_bytes: usize,
    maximum_window_storage_bytes: usize,
    current_process_count: usize,
    current_thread_count: usize,
    retained_process_identity_count: usize,
    retained_thread_identity_count: usize,
    continuity_epoch: i64,
    capacity_reset_count: i64,
    name_bytes: usize,
    last_capacity_reset: CapacityReset,
};
const Process = struct { instance_id: i64, process_id: i32, unique_key: u64, start_key: ?i64, name: []u8, current: bool = true, references: usize = 1, index: usize };
/// window_pairs is the head (record index + 1) of this thread's pair chain; a
/// non-empty chain holds one thread reference.
const Thread = struct { instance_id: i64, thread_id: i32, process: *Process, current: bool = true, references: usize = 1, index: usize, observed_filetime: i64 = 0, window_pairs: u32 = 0 };
const Running = struct { raw_process_id: i32, raw_thread_id: i32, thread: ?*Thread, start_qpc: i64, idle: bool };
/// One expected processor: its current run and its last switch (boundary).
const Processor = struct { running: Running = undefined, boundary_qpc: i64 = 0, boundary_time: i64 = 0, known: bool = false };
/// One (thread instance, expected processor) pair. total is the finite
/// duration inside [S, E); in a continuous window it is the last written slot.
const PairRecord = extern struct { total: i64, switches: i32, processor: u32, next: u32, reserved: u32 = 0 };
/// One continuous slot of one pair; stamp = slot + 1 (truncated), 0 = empty.
const RingCell = extern struct { duration: i64 = 0, switches: i32 = 0, stamp: u32 = 0 };

/// Single-consumer state. The caller serializes seed/event/snapshot access and supplies
/// the persistent owner allocator; snapshots use a separate temporary allocator.
/// Window storage (pair records and rings) uses the window allocator in fixed
/// chunks that never move; it exists only between S and the window's release.
pub const Owner = struct {
    allocator: Allocator,
    window_allocator: Allocator,
    profile: ?*@import("publication_profile.zig").Profile = null,
    config: Config,
    window_qpc: i64,
    name_bytes: usize = 0,
    processes: std.ArrayList(*Process) = .empty,
    threads: std.ArrayList(*Thread) = .empty,
    current_processes: std.AutoHashMapUnmanaged(i32, *Process) = .empty,
    current_threads: std.AutoHashMapUnmanaged(i32, *Thread) = .empty,
    // Expected processor set, stored for the capture's lifetime.
    expected: []i32 = &.{},
    processors: []Processor = &.{},
    processor_index: std.AutoHashMapUnmanaged(i32, u32) = .empty,
    known_processors: usize = 0,
    // Window mode, chosen at the window begin: 0 = finite, n = continuous slots per window.
    slots: u32 = 0,
    ring_slots: u32 = 0,
    // S, time(S), E (finite), the cached live floor slot F and the slot of the last F recomputation.
    measuring: bool = false,
    start_qpc: i64 = 0,
    start_time: i64 = 0,
    end_qpc: i64 = 0,
    live_floor: i64 = 0,
    last_crossing: i64 = 0,
    record_chunks: std.ArrayList([]PairRecord) = .empty,
    ring_chunks: std.ArrayList([]RingCell) = .empty,
    record_count: u32 = 0,
    live_pairs: usize = 0,
    free_records: u32 = 0,
    window_storage_bytes: usize = 0,
    window_storage_peak: usize = 0,
    latest_event_qpc: i64 = -1,
    context_switch_floor_qpc: i64 = 0,
    next_process_instance_id: i64 = 0,
    next_thread_instance_id: i64 = 0,
    continuity_epoch: i64 = 0,
    inventory_version: i64 = 0,
    capacity_reset_count: i64 = 0,
    last_capacity_failure: CapacityFailure = .{},
    last_capacity_reset: CapacityReset = .{},

    pub fn init(allocator: Allocator, config: Config) !Owner {
        return initWithWindowAllocator(allocator, allocator, config);
    }

    /// Run identities and per-window mutable state can use separate backing.
    /// The window allocator context must remain valid until storage is released.
    pub fn initWithWindowAllocator(allocator: Allocator, window_allocator: Allocator, config: Config) !Owner {
        if (config.qpc_frequency <= 0 or config.window_ms <= 0 or config.max_pairs == 0 or config.max_pairs >= std.math.maxInt(u32) or config.max_processes == 0 or config.max_threads == 0 or config.max_processors == 0 or config.max_processors > std.math.maxInt(u32) or config.max_name_bytes == 0) return error.InvalidConfig;
        const window = try ceilDiv(try std.math.mul(i64, config.qpc_frequency, config.window_ms), 1000);
        var owner: Owner = .{ .allocator = allocator, .window_allocator = window_allocator, .config = config, .window_qpc = window };
        owner.config.processors = &.{};
        if (config.processors.len != 0) {
            errdefer owner.deinit();
            try owner.setProcessors(config.processors);
        }
        return owner;
    }

    pub fn deinit(self: *Owner) void {
        self.clearPairs();
        self.record_chunks.deinit(self.window_allocator);
        self.ring_chunks.deinit(self.window_allocator);
        self.allocator.free(self.processors);
        self.allocator.free(self.expected);
        self.processor_index.deinit(self.allocator);
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

    /// Replaces the expected processor set and resets continuity. Empty, negative
    /// or duplicate ids are invalid; more than max_processors is a capacity failure.
    pub fn setProcessors(self: *Owner, ids: []const i32) !void {
        if (ids.len == 0) return error.InvalidConfig;
        if (ids.len > self.config.max_processors) return self.capacityExceeded(.processors, ids.len, self.config.max_processors, -1);
        var index: std.AutoHashMapUnmanaged(i32, u32) = .empty;
        errdefer index.deinit(self.allocator);
        try index.ensureTotalCapacity(self.allocator, @intCast(ids.len));
        for (ids, 0..) |id, position| {
            if (id < 0) return error.InvalidConfig;
            const entry = index.getOrPutAssumeCapacity(id);
            if (entry.found_existing) return error.InvalidConfig;
            entry.value_ptr.* = @intCast(position);
        }
        const expected = try self.allocator.dupe(i32, ids);
        errdefer self.allocator.free(expected);
        const processors = try self.allocator.alloc(Processor, ids.len);
        errdefer self.allocator.free(processors);
        @memset(processors, .{});
        try self.resetContinuity();
        self.allocator.free(self.processors);
        self.allocator.free(self.expected);
        self.processor_index.deinit(self.allocator);
        self.processors = processors;
        self.expected = expected;
        self.processor_index = index;
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
        // Processors outside the expected set never contribute.
        if (lp < 0 or !self.contextSwitchBelongsToWindow(qpc) or !try self.advance(qpc)) return;
        const index = self.processor_index.get(lp) orelse return;
        errdefer self.resetContinuity() catch {};
        const processor = &self.processors[index];
        var restart = false;
        if (processor.known) {
            const previous = processor.running;
            if (previous.raw_thread_id != old_tid or (previous.raw_process_id > 0 and old_pid > 0 and previous.raw_process_id != old_pid)) {
                // This processor's run is dropped. A measuring window restarts at
                // this switch unless it is finite and this processor already covered E.
                restart = self.measuring and (self.slots != 0 or processor.boundary_qpc < self.end_qpc);
                if (restart) self.clearPairs();
            } else if (self.measuring) {
                if (self.slots != 0 and qpc >= self.boundaryOf(self.last_crossing + 1)) {
                    self.advanceLiveFloor(index, qpc);
                    self.last_crossing = self.slotOf(qpc);
                }
                // Unattributed execution is omitted; it is not a gap in context-switch continuity.
                if (!previous.idle and previous.thread != null) {
                    if (!try self.closeRun(previous, index, qpc)) {
                        // The failing switch is the first boundary of a fresh window,
                        // not a fatal error in the shared trace.
                        self.capacity_reset_count = try std.math.add(i64, self.capacity_reset_count, 1);
                        try self.resetContinuity();
                    }
                }
            }
        }
        const running = try self.resolve(qpc, new_pid, name, new_tid);
        const was_known = processor.known;
        const old_thread = if (was_known) processor.running.thread else null;
        processor.* = .{ .running = running, .boundary_qpc = qpc, .boundary_time = time, .known = true };
        if (running.thread) |thread| thread.references += 1;
        if (old_thread) |thread| self.releaseThread(thread);
        if (!was_known) {
            // S: the switch that gives the last expected processor a known run.
            self.known_processors += 1;
            if (self.known_processors == self.processors.len) self.startMeasuring(qpc, time);
        } else if (restart) self.startMeasuring(qpc, time);
    }

    pub fn resetContinuity(self: *Owner) !void {
        for (self.processors) |*processor| {
            if (!processor.known) continue;
            const thread = processor.running.thread;
            processor.* = .{};
            if (thread) |value| self.releaseThread(value);
        }
        self.known_processors = 0;
        self.clearPairs();
        self.measuring = false;
        self.start_qpc = 0;
        self.start_time = 0;
        self.end_qpc = 0;
        self.live_floor = 0;
        self.last_crossing = 0;
        self.continuity_epoch = try std.math.add(i64, self.continuity_epoch, 1);
    }

    /// Explicit window cleanup, after closed output has been copied/published.
    /// Process/thread identity ownership remains with the CPU run.
    pub fn releaseWindowStorage(self: *Owner) !void {
        try self.resetContinuity();
        std.debug.assert(self.window_storage_bytes == 0);
    }

    /// Begins a window at the floor: slots 0 = finite, 1..2047 = continuous.
    /// Releases all window storage. A new owner is a finite window at floor 0.
    pub fn beginWindowAt(self: *Owner, qpc_floor: i64, slots: u64) !void {
        if (qpc_floor < 0) return error.InvalidWindowBoundary;
        if (slots > maximum_slots_per_window) return error.InvalidInput;
        const floor = @max(qpc_floor, @max(0, self.latest_event_qpc));
        try self.releaseWindowStorage();
        self.context_switch_floor_qpc = floor;
        self.slots = @intCast(slots);
        self.ring_slots = if (slots == 0) 0 else @intCast(slots * 2 + 2);
    }

    /// The trace owner uses the same predicate for per-consumer rejection
    /// accounting before dispatching a well-formed context-switch record.
    pub fn contextSwitchBelongsToWindow(self: *const Owner, qpc: i64) bool {
        return qpc >= self.context_switch_floor_qpc;
    }

    pub fn setWindowAllocator(self: *Owner, allocator: Allocator) error{WindowStorageInUse}!void {
        if (self.record_chunks.capacity != 0 or self.ring_chunks.capacity != 0 or self.window_storage_bytes != 0)
            return error.WindowStorageInUse;
        self.window_allocator = allocator;
    }

    /// Cold consumer command: change window shape without discarding verified
    /// process/thread lifetimes or keeping totals of the previous shape.
    pub fn configureWindow(self: *Owner, window_ms: i64, max_pairs: usize) !void {
        if (self.config.window_ms == window_ms and self.config.max_pairs == max_pairs) return;
        if (window_ms <= 0 or max_pairs == 0 or max_pairs >= std.math.maxInt(u32)) return error.InvalidConfig;
        const window = try ceilDiv(try std.math.mul(i64, self.config.qpc_frequency, window_ms), 1000);
        try self.resetContinuity();
        self.config.window_ms = window_ms;
        self.config.max_pairs = max_pairs;
        self.window_qpc = window;
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

    /// A cleared owner is a fresh one: finite window, floor 0.
    pub fn clear(self: *Owner) !void {
        try self.resetAfterEventLoss();
        self.latest_event_qpc = -1;
        self.context_switch_floor_qpc = 0;
        self.slots = 0;
        self.ring_slots = 0;
        self.next_process_instance_id = 0;
        self.next_thread_instance_id = 0;
        self.last_capacity_failure = .{};
    }

    pub fn getDiagnostics(self: *const Owner) Diagnostics {
        return .{ .qpc_frequency = self.config.qpc_frequency, .window_qpc = self.window_qpc, .maximum_active_pairs = self.config.max_pairs, .active_pair_count = self.live_pairs, .slots_per_window = self.slots, .ring_slots = self.ring_slots, .pair_record_bytes = pair_record_bytes, .window_storage_bytes = self.window_storage_bytes, .peak_window_storage_bytes = self.window_storage_peak, .maximum_window_storage_bytes = windowStorageBytes(self.config.max_pairs, self.ring_slots), .current_process_count = self.current_processes.count(), .current_thread_count = self.current_threads.count(), .retained_process_identity_count = self.processes.items.len, .retained_thread_identity_count = self.threads.items.len, .continuity_epoch = self.continuity_epoch, .capacity_reset_count = self.capacity_reset_count, .name_bytes = self.name_bytes, .last_capacity_reset = self.last_capacity_reset };
    }

    /// Chunk bytes held for `records` live pair records (records never move and
    /// freed records are reused, so chunks follow the peak since the last clear).
    pub fn windowStorageBytes(records: usize, ring_slots: u32) usize {
        if (records == 0) return 0;
        const record_chunk = records_per_chunk * @sizeOf(PairRecord);
        var bytes = (std.math.divCeil(usize, records, records_per_chunk) catch unreachable) * record_chunk;
        if (ring_slots == 0) return bytes;
        const ring_bytes = @as(usize, ring_slots) * @sizeOf(RingCell);
        const rings_per_chunk = @max(1, ring_chunk_bytes / ring_bytes);
        bytes += (std.math.divCeil(usize, records, rings_per_chunk) catch unreachable) * rings_per_chunk * ring_bytes;
        return bytes;
    }

    pub fn snapshot(self: *Owner, allocator: Allocator) !Snapshot {
        return self.snapshotCore(allocator, true);
    }

    /// Only the names borrow owner storage. The single consumer must finish
    /// copying final publication rows before any further owner mutation.
    /// Snapshot.deinit still releases its allocator-owned Record storage.
    pub fn snapshotBorrowedNames(self: *Owner, allocator: Allocator) !Snapshot {
        return self.snapshotCore(allocator, false);
    }

    /// Pure coverage metadata; no row scan, mutation or allocation.
    /// Before S nothing is known. After S an incomplete window reports S and the
    /// lowest boundary; a complete one reports exactly its measured interval.
    pub fn snapshotMetadata(self: *const Owner) Snapshot {
        var result: Snapshot = .{ .qpc_frequency = self.config.qpc_frequency };
        if (!self.measuring) return result;
        var lowest = &self.processors[0];
        for (self.processors[1..]) |*processor| {
            if (processor.boundary_qpc < lowest.boundary_qpc) lowest = processor;
        }
        result.boundaries_ready = true;
        result.window_start_qpc = self.start_qpc;
        result.measured_through_qpc = lowest.boundary_qpc;
        result.measured_through = lowest.boundary_time;
        var start = self.start_qpc;
        var through = self.end_qpc;
        if (self.slots == 0) {
            if (lowest.boundary_qpc < self.end_qpc) return result;
        } else {
            const slot = self.slotOf(lowest.boundary_qpc);
            if (slot < self.slots) return result;
            start = @intCast(self.boundaryOf(slot - self.slots));
            through = @intCast(self.boundaryOf(slot));
        }
        // Civil through = time(S) + floor((through - S) * 10^7 / frequency) ticks.
        const ticks = @divTrunc(@as(i128, through - self.start_qpc) * 10_000_000, self.config.qpc_frequency);
        result.measured_through = std.math.add(i64, self.start_time, std.math.cast(i64, ticks) orelse return result) catch return result;
        result.window_start_qpc = start;
        result.measured_through_qpc = through;
        result.is_complete = true;
        return result;
    }

    fn snapshotCore(self: *Owner, allocator: Allocator, comptime own_names: bool) !Snapshot {
        var result = self.snapshotMetadata();
        result.owns_process_names = own_names;
        if (!result.is_complete) return result;
        const first: i64 = if (self.slots == 0) 0 else self.slotOf(self.lowestProcessor().boundary_qpc) - self.slots;
        var rows: std.ArrayList(Record) = .empty;
        errdefer {
            if (own_names) for (rows.items) |row| allocator.free(row.process_name);
            rows.deinit(allocator);
        }
        // Reserve the live bound once so arena-backed publication scratch does
        // not retain old growth buffers interleaved with per-row name allocations.
        try rows.ensureTotalCapacityPrecise(allocator, self.live_pairs);
        for (self.threads.items) |thread| {
            var link = thread.window_pairs;
            while (link != 0) {
                const pair = link - 1;
                const value = self.record(pair);
                link = value.next;
                var duration: i64 = value.total;
                var switches: i32 = value.switches;
                if (self.slots != 0) {
                    duration = 0;
                    switches = 0;
                    const cells = self.ring(pair);
                    var slot = first;
                    while (slot < first + self.slots) : (slot += 1) {
                        const slot_cell = &cells[@intCast(@mod(slot, self.ring_slots))];
                        if (slot_cell.stamp != stampOf(slot)) continue;
                        duration = try std.math.add(i64, duration, slot_cell.duration);
                        switches = try std.math.add(i32, switches, slot_cell.switches);
                    }
                }
                if (duration == 0 and switches == 0) continue;
                const process = thread.process;
                const name = if (own_names) try allocator.dupe(u8, process.name) else process.name;
                errdefer if (own_names) allocator.free(name);
                try rows.append(allocator, .{ .process_instance_id = process.instance_id, .process_id = process.process_id, .process_start_key = process.start_key, .process_name = name, .thread_instance_id = thread.instance_id, .thread_id = thread.thread_id, .logical_processor_id = self.expected[value.processor], .execution_time_qpc = duration, .switch_count = switches });
            }
        }
        std.mem.sort(Record, rows.items, {}, rowLessThan);
        result.records = try rows.toOwnedSlice(allocator);
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

    fn startMeasuring(self: *Owner, qpc: i64, time: i64) void {
        self.measuring = true;
        self.start_qpc = qpc;
        self.start_time = time;
        self.end_qpc = if (qpc > std.math.maxInt(i64) - self.window_qpc) std.math.maxInt(i64) else qpc + self.window_qpc;
        // Every boundary is at most S here, so F = max(0, slot(m) - n) = 0.
        self.live_floor = 0;
        self.last_crossing = if (self.slots == 0) 0 else self.slotOf(qpc);
    }

    fn lowestProcessor(self: *const Owner) *const Processor {
        var lowest = &self.processors[0];
        for (self.processors[1..]) |*processor| {
            if (processor.boundary_qpc < lowest.boundary_qpc) lowest = processor;
        }
        return lowest;
    }

    /// b(k) = S + floor(k * W / n).
    fn boundaryOf(self: *const Owner, slot: i64) i128 {
        return @as(i128, self.start_qpc) + @divFloor(@as(i128, slot) * self.window_qpc, self.slots);
    }

    /// slot(x) = floor(((x - S + 1) * n - 1) / W), the largest k with b(k) <= x; -1 before S.
    fn slotOf(self: *const Owner, qpc: i64) i64 {
        if (qpc < self.start_qpc) return -1;
        return @intCast(@divFloor((@as(i128, qpc) - self.start_qpc + 1) * self.slots - 1, self.window_qpc));
    }

    /// Finite: the run's overlap with [S, E) and one switch if S <= start < E.
    /// Continuous: its overlap with each slot at or above the cached live floor F,
    /// and one switch in slot(start) if start >= S. False = capacity reset, with
    /// last_capacity_reset and last_capacity_failure already captured.
    fn closeRun(self: *Owner, previous: Running, index: u32, qpc: i64) !bool {
        const thread = previous.thread.?;
        const run_start = previous.start_qpc;
        const from = @max(run_start, self.start_qpc);
        if (self.slots == 0) {
            const duration = @max(0, @min(qpc, self.end_qpc) - from);
            const switches: i32 = if (run_start >= self.start_qpc and run_start < self.end_qpc) 1 else 0;
            if (duration == 0 and switches == 0) return true;
            const pair = (try self.findOrAddPair(thread, index)) orelse return false;
            const value = self.record(pair);
            value.total = try std.math.add(i64, value.total, duration);
            value.switches = try std.math.add(i32, value.switches, switches);
            return true;
        }
        const first_slot: i64 = if (qpc > from) self.slotOf(from) else std.math.maxInt(i64);
        const last_slot: i64 = if (qpc > from) self.slotOf(qpc - 1) else -1;
        const switch_slot: i64 = if (run_start >= self.start_qpc) self.slotOf(run_start) else -1;
        var highest = self.highestWrite(first_slot, last_slot, switch_slot);
        if (highest < 0) return true;
        if (highest >= self.live_floor + self.ring_slots) {
            self.advanceLiveFloor(index, qpc);
            highest = self.highestWrite(first_slot, last_slot, switch_slot);
            if (highest < 0) return true;
            // One processor lags the others by more than about W.
            if (highest >= self.live_floor + self.ring_slots)
                return self.rejectCapacity(.ring_span, @intCast(highest - self.live_floor + 1), self.ring_slots, index, @intCast(highest - self.live_floor + 1));
        }
        const pair = (try self.findOrAddPair(thread, index)) orelse return false;
        const cells = self.ring(pair);
        var slot = @max(first_slot, self.live_floor);
        while (slot <= last_slot) : (slot += 1) {
            const slot_start: i64 = @intCast(self.boundaryOf(slot));
            const next = self.boundaryOf(slot + 1);
            const slot_end: i64 = if (next < qpc) @intCast(next) else qpc;
            const overlap = slot_end - @max(from, slot_start);
            if (overlap <= 0) continue;
            const cell = self.ringCell(cells, slot);
            cell.duration = try std.math.add(i64, cell.duration, overlap);
        }
        if (switch_slot >= self.live_floor) {
            const cell = self.ringCell(cells, switch_slot);
            cell.switches = try std.math.add(i32, cell.switches, 1);
        }
        const value = self.record(pair);
        value.total = @max(value.total, highest);
        return true;
    }

    /// The highest slot a close writes after clipping below the cached F; -1 = nothing.
    fn highestWrite(self: *const Owner, first_slot: i64, last_slot: i64, switch_slot: i64) i64 {
        var highest: i64 = -1;
        if (last_slot >= @max(first_slot, self.live_floor)) highest = last_slot;
        if (switch_slot >= self.live_floor) highest = @max(highest, switch_slot);
        return highest;
    }

    fn ringCell(self: *const Owner, cells: []RingCell, slot: i64) *RingCell {
        const value = &cells[@intCast(@mod(slot, self.ring_slots))];
        // Any other stamp in this cell is a slot below F (R = 2n + 2 > live span).
        if (value.stamp != stampOf(slot)) value.* = .{ .stamp = stampOf(slot) };
        return value;
    }

    /// F = max(0, slot(m) - n), m = the lowest boundary with this processor's
    /// prospective boundary. When F advances, pairs last written below it leave.
    fn advanceLiveFloor(self: *Owner, index: u32, prospective_qpc: i64) void {
        var lowest = prospective_qpc;
        for (self.processors, 0..) |processor, position| {
            if (position != index and processor.boundary_qpc < lowest) lowest = processor.boundary_qpc;
        }
        const floor = @max(0, self.slotOf(lowest) - self.slots);
        if (floor <= self.live_floor) return;
        self.live_floor = floor;
        var position: usize = 0;
        while (position < self.threads.items.len) {
            const thread = self.threads.items[position];
            const had_pairs = thread.window_pairs != 0;
            var link = &thread.window_pairs;
            while (link.* != 0) {
                const index_of_record = link.* - 1;
                const value = self.record(index_of_record);
                if (value.total >= floor) {
                    link = &value.next;
                    continue;
                }
                link.* = value.next;
                self.freeRecord(index_of_record);
            }
            if (had_pairs and thread.window_pairs == 0) {
                // The emptied chain drops the reference it held.
                const destroys = thread.references == 1;
                self.releaseThread(thread);
                if (destroys) continue; // swapRemove moved an unvisited thread here.
            }
            position += 1;
        }
    }

    fn findOrAddPair(self: *Owner, thread: *Thread, index: u32) !?u32 {
        var link = thread.window_pairs;
        while (link != 0) {
            const value = self.record(link - 1);
            if (value.processor == index) return link - 1;
            link = value.next;
        }
        if (self.live_pairs >= self.config.max_pairs) {
            _ = self.rejectCapacity(.active_pairs, self.live_pairs + 1, self.config.max_pairs, index, 0);
            return null;
        }
        const pair = try self.allocateRecord();
        self.record(pair).* = .{ .total = if (self.slots == 0) 0 else -1, .switches = 0, .processor = index, .next = thread.window_pairs };
        if (self.ring_slots != 0) @memset(self.ring(pair), .{});
        if (thread.window_pairs == 0) thread.references += 1;
        thread.window_pairs = pair + 1;
        self.live_pairs += 1;
        return pair;
    }

    fn allocateRecord(self: *Owner) !u32 {
        if (self.free_records != 0) {
            const pair = self.free_records - 1;
            self.free_records = self.record(pair).next;
            return pair;
        }
        const pair = self.record_count;
        if (pair / records_per_chunk >= self.record_chunks.items.len) {
            try self.record_chunks.ensureUnusedCapacity(self.window_allocator, 1);
            const chunk = try self.window_allocator.alloc(PairRecord, records_per_chunk);
            self.record_chunks.appendAssumeCapacity(chunk);
            self.accountWindow(chunk.len * @sizeOf(PairRecord));
        }
        if (self.ring_slots != 0) {
            const per_chunk = self.ringsPerChunk();
            if (pair / per_chunk >= self.ring_chunks.items.len) {
                try self.ring_chunks.ensureUnusedCapacity(self.window_allocator, 1);
                const chunk = try self.window_allocator.alloc(RingCell, per_chunk * self.ring_slots);
                self.ring_chunks.appendAssumeCapacity(chunk);
                self.accountWindow(chunk.len * @sizeOf(RingCell));
            }
        }
        self.record_count += 1;
        return pair;
    }

    fn freeRecord(self: *Owner, pair: u32) void {
        self.record(pair).next = self.free_records;
        self.free_records = pair + 1;
        self.live_pairs -= 1;
    }

    fn record(self: *const Owner, pair: u32) *PairRecord {
        return &self.record_chunks.items[pair / records_per_chunk][pair % records_per_chunk];
    }

    fn ringsPerChunk(self: *const Owner) u32 {
        return @intCast(@max(1, ring_chunk_bytes / (@as(usize, self.ring_slots) * @sizeOf(RingCell))));
    }

    fn ring(self: *const Owner, pair: u32) []RingCell {
        const per_chunk = self.ringsPerChunk();
        const offset = (pair % per_chunk) * self.ring_slots;
        return self.ring_chunks.items[pair / per_chunk][offset .. offset + self.ring_slots];
    }

    fn rejectCapacity(self: *Owner, reason: CapacityReason, observed: u64, limit: u64, index: u32, required_slot_span: u64) bool {
        const reset_reason: CapacityResetReason = switch (reason) {
            .active_pairs => .active_pair_limit,
            .ring_span => .ring_span,
            else => unreachable,
        };
        self.last_capacity_reset = .{
            .reason = @intFromEnum(reset_reason),
            .event_qpc = self.latest_event_qpc,
            .window_start_qpc = if (self.measuring) self.start_qpc else std.math.minInt(i64),
            .active_pair_count = self.live_pairs,
            .maximum_active_pairs = self.config.max_pairs,
            .required_slot_span = required_slot_span,
            .ring_slots = self.ring_slots,
            .current_process_count = self.current_processes.count(),
            .current_thread_count = self.current_threads.count(),
        };
        self.recordCapacityFailure(reason, observed, limit, self.expected[index]);
        return false;
    }

    /// Clearing every pair frees every window chunk.
    fn clearPairs(self: *Owner) void {
        var position: usize = 0;
        while (position < self.threads.items.len) {
            const thread = self.threads.items[position];
            if (thread.window_pairs != 0) {
                thread.window_pairs = 0;
                const destroys = thread.references == 1;
                self.releaseThread(thread);
                if (destroys) continue; // swapRemove moved an unvisited thread here.
            }
            position += 1;
        }
        for (self.record_chunks.items) |chunk| self.window_allocator.free(chunk);
        for (self.ring_chunks.items) |chunk| self.window_allocator.free(chunk);
        self.record_chunks.clearAndFree(self.window_allocator);
        self.ring_chunks.clearAndFree(self.window_allocator);
        self.record_count = 0;
        self.live_pairs = 0;
        self.free_records = 0;
        self.window_storage_bytes = 0;
    }

    fn accountWindow(self: *Owner, added: usize) void {
        self.window_storage_bytes += added;
        self.window_storage_peak = @max(self.window_storage_peak, self.window_storage_bytes);
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
        self.recordCapacityFailure(reason, observed, limit, lp);
        return error.CapacityExceeded;
    }

    fn recordCapacityFailure(self: *Owner, reason: CapacityReason, observed: u64, limit: u64, lp: i32) void {
        var oldest_qpc: i64 = std.math.maxInt(i64);
        var oldest_lp: i32 = -1;
        for (self.processors, self.expected) |processor, id| {
            if (!processor.known) continue;
            const boundary = if (id == lp) self.latest_event_qpc else processor.boundary_qpc;
            if (boundary < oldest_qpc) {
                oldest_qpc = boundary;
                oldest_lp = id;
            }
        }
        self.last_capacity_failure = .{ .reason = @intFromEnum(reason), .logical_processor = lp, .event_qpc = self.latest_event_qpc, .observed = observed, .limit = limit, .pairs = self.live_pairs, .window_start_qpc = if (self.measuring) self.start_qpc else -1, .oldest_processor = oldest_lp, .oldest_boundary_qpc = if (oldest_lp == -1) -1 else oldest_qpc, .processors = self.known_processors, .processes = self.processes.items.len, .threads = self.threads.items.len, .name_bytes = self.name_bytes };
    }
};

fn stampOf(slot: i64) u32 {
    return @truncate(@as(u64, @bitCast(slot + 1)));
}
fn rowLessThan(_: void, left: Record, right: Record) bool {
    if (left.process_instance_id != right.process_instance_id) return left.process_instance_id < right.process_instance_id;
    if (left.thread_instance_id != right.thread_instance_id) return left.thread_instance_id < right.thread_instance_id;
    return left.logical_processor_id < right.logical_processor_id;
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
