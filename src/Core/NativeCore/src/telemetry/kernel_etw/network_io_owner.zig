const std = @import("std");
const io = @import("io_decode.zig");
const identity = @import("io_identity.zig");
const Budget = @import("budget.zig").Budget;

/// Every limit comes from the host's kernel_etw_limits declaration; no field defaults.
pub const Config = struct {
    window_ms: i64,
    retention_ms: i64,
    initial_event_capacity: usize,
    maximum_retained_events: usize,
    maximum_processes: usize,
    byte_cap: usize,
};

pub const Row = struct {
    process_id: u32,
    process_birth_filetime_100ns: ?u64,
    available: bool,
    receive_bytes: u64,
    send_bytes: u64,
    receive_bytes_per_second: f64,
    send_bytes_per_second: f64,
    event_count: u32,
    external_receive_bytes: u64,
    external_send_bytes: u64,
    external_receive_bytes_per_second: f64,
    external_send_bytes_per_second: f64,
    external_event_count: u32,
};

pub const Window = struct {
    complete: bool,
    rows: []const Row,
    start_tick_ms: i64,
    through_tick_ms: i64,
    window_ms: i64,
};

pub const Usage = struct {
    current_bytes: usize,
    peak_bytes: usize,
    byte_cap: usize,
    failed_allocations: u64,
    capacity_failures: u64,
};

const Event = struct {
    tick_ms: i64,
    process_id: u32,
    bytes: u32,
    direction: io.Direction,
    leaves_host: bool,
    event_filetime_100ns: ?u64,
};

const Accumulator = struct {
    birth: ?u64,
    handle: ?*anyopaque = null,
    missing_filetime: bool = false,
    receive_bytes: u64 = 0,
    send_bytes: u64 = 0,
    event_count: u32 = 0,
    external_receive_bytes: u64 = 0,
    external_send_bytes: u64 = 0,
    external_event_count: u32 = 0,
    overflowed: bool = false,
};

/// Single-consumer network window. It accepts normalized classic kernel events;
/// session start/stop, subscriptions, and loss notifications remain external.
pub const Owner = struct {
    budget: Budget,
    config: Config,
    identity_source: identity.Source,
    events: []Event = &.{},
    event_head: usize = 0,
    event_count: usize = 0,
    processes: std.AutoHashMapUnmanaged(u32, Accumulator) = .empty,
    incomplete_through_tick: i64 = std.math.minInt(i64),
    capacity_failures: u64 = 0,

    pub fn init(backing: std.mem.Allocator, config: Config, identity_source: identity.Source) !Owner {
        if (config.window_ms <= 0 or config.retention_ms < config.window_ms or config.initial_event_capacity == 0 or
            config.maximum_retained_events == 0 or config.maximum_processes == 0 or
            config.initial_event_capacity > config.maximum_retained_events or config.byte_cap < @sizeOf(Owner)) return error.InvalidConfig;
        return .{
            .budget = .{ .backing = backing, .cap = config.byte_cap, .current = @sizeOf(Owner), .peak = @sizeOf(Owner) },
            .config = config,
            .identity_source = identity_source,
        };
    }

    pub fn deinit(self: *Owner) void {
        self.closeSnapshotHandles();
        const allocator = self.budget.allocator();
        if (self.events.len != 0) allocator.free(self.events);
        self.processes.deinit(allocator);
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

    /// The caller supplies the same capture clock used by the previous owner.
    /// Unknown PIDs and zero-byte events retain the previous filtering policy.
    pub fn observe(self: *Owner, transfer: io.NetworkTransfer, tick_ms: i64) !void {
        const process_id = transfer.process_id orelse return;
        if (process_id == 0 or process_id > std.math.maxInt(i32) or transfer.bytes == 0) return;
        if (tick_ms < 0) return error.InvalidTimestamp;
        if (tick_ms <= self.incomplete_through_tick) return;
        if (self.event_count == self.config.maximum_retained_events) return self.capacityFailure(tick_ms);
        self.ensureEventCapacity(tick_ms) catch |err| {
            self.invalidate(tick_ms);
            return err;
        };
        const index = (self.event_head + self.event_count) % self.events.len;
        self.events[index] = .{
            .tick_ms = tick_ms,
            .process_id = process_id,
            .bytes = transfer.bytes,
            .direction = transfer.direction,
            .leaves_host = io.leavesHost(transfer.remote_address),
            .event_filetime_100ns = transfer.event_filetime_100ns,
        };
        self.event_count += 1;
    }

    /// Clear continuity after the session owner reports a loss or rebind.
    pub fn invalidate(self: *Owner, tick_ms: i64) void {
        self.incomplete_through_tick = @max(self.incomplete_through_tick, tick_ms);
        self.event_head = 0;
        self.event_count = 0;
        self.closeSnapshotHandles();
        self.processes.clearRetainingCapacity();
    }

    /// Aggregates the selected 2-second window into caller-owned publication
    /// storage. One process handle is held per PID during aggregation; creation
    /// identity is checked twice on that same handle before it is published.
    pub fn snapshot(self: *Owner, now_tick_ms: i64, capture_started_tick_ms: ?i64, rows_out: []Row) !Window {
        return self.snapshotInto(now_tick_ms, capture_started_tick_ms, .{ .fixed = rows_out });
    }

    /// Allocate exactly one temporary row per selected process after the normal
    /// single-pass aggregation. Identity handles are still verified only once.
    pub fn snapshotAllocated(self: *Owner, output: std.mem.Allocator, now_tick_ms: i64, capture_started_tick_ms: ?i64) !Window {
        return self.snapshotInto(now_tick_ms, capture_started_tick_ms, .{ .allocated = output });
    }
    const Output = union(enum) { fixed: []Row, allocated: std.mem.Allocator };
    fn snapshotInto(self: *Owner, now_tick_ms: i64, capture_started_tick_ms: ?i64, output: Output) !Window {
        if (now_tick_ms < 0) return error.InvalidTimestamp;
        self.prune(now_tick_ms);
        self.processes.clearRetainingCapacity();
        defer self.closeSnapshotHandles();
        const cutoff = try std.math.sub(i64, now_tick_ms, self.config.window_ms);
        var i: usize = 0;
        while (i < self.event_count) : (i += 1) {
            const event = self.events[(self.event_head + i) % self.events.len];
            if (event.tick_ms < cutoff or event.tick_ms > now_tick_ms) continue;
            var accumulator = self.processes.getPtr(event.process_id) orelse blk: {
                if (self.processes.count() >= self.config.maximum_processes) return self.snapshotCapacityFailure(now_tick_ms);
                var handle: ?*anyopaque = null;
                var created: ?u64 = null;
                if (identity.Lease.open(self.identity_source, event.process_id)) |lease| {
                    handle = lease.handle;
                    created = lease.creationFiletime();
                }
                const entry = self.processes.getOrPut(self.budget.allocator(), event.process_id) catch |err| {
                    self.incomplete_through_tick = @max(self.incomplete_through_tick, now_tick_ms);
                    if (handle) |owned_handle| self.identity_source.close_process(self.identity_source.context, owned_handle);
                    return err;
                };
                entry.value_ptr.* = .{ .birth = created, .handle = handle };
                break :blk entry.value_ptr;
            };
            if (event.event_filetime_100ns == null) {
                accumulator.missing_filetime = true;
                continue;
            }
            if (accumulator.birth == null or event.event_filetime_100ns.? < accumulator.birth.?) continue;
            if (event.direction == .receive) {
                if (std.math.add(u64, accumulator.receive_bytes, event.bytes)) |sum| accumulator.receive_bytes = sum else |_| accumulator.overflowed = true;
            } else {
                if (std.math.add(u64, accumulator.send_bytes, event.bytes)) |sum| accumulator.send_bytes = sum else |_| accumulator.overflowed = true;
            }
            if (std.math.add(u32, accumulator.event_count, 1)) |sum| accumulator.event_count = sum else |_| accumulator.overflowed = true;
            if (event.leaves_host) {
                if (event.direction == .receive) {
                    if (std.math.add(u64, accumulator.external_receive_bytes, event.bytes)) |sum| accumulator.external_receive_bytes = sum else |_| accumulator.overflowed = true;
                } else {
                    if (std.math.add(u64, accumulator.external_send_bytes, event.bytes)) |sum| accumulator.external_send_bytes = sum else |_| accumulator.overflowed = true;
                }
                if (std.math.add(u32, accumulator.external_event_count, 1)) |sum| accumulator.external_event_count = sum else |_| accumulator.overflowed = true;
            }
        }
        const rows_out = switch (output) {
            .fixed => |rows| rows,
            .allocated => |allocator| try allocator.alloc(Row, self.processes.count()),
        };
        if (rows_out.len < self.processes.count()) return error.OutputTooSmall;
        const seconds = @as(f64, @floatFromInt(self.config.window_ms)) / 1000.0;
        var row_count: usize = 0;
        var process_iter = self.processes.iterator();
        while (process_iter.next()) |entry| {
            const process_id = entry.key_ptr.*;
            const accumulator = entry.value_ptr.*;
            var final_matches = false;
            if (accumulator.birth) |birth| {
                if (accumulator.handle) |handle| {
                    const final_birth = self.identity_source.read_creation_filetime(self.identity_source.context, handle);
                    final_matches = final_birth != null and final_birth.? > 0 and final_birth.? == birth;
                }
            }
            const available = accumulator.birth != null and !accumulator.missing_filetime and !accumulator.overflowed and final_matches;
            rows_out[row_count] = .{
                .process_id = process_id,
                .process_birth_filetime_100ns = if (available) accumulator.birth else null,
                .available = available,
                .receive_bytes = if (available) accumulator.receive_bytes else 0,
                .send_bytes = if (available) accumulator.send_bytes else 0,
                .receive_bytes_per_second = if (available) @as(f64, @floatFromInt(accumulator.receive_bytes)) / seconds else 0,
                .send_bytes_per_second = if (available) @as(f64, @floatFromInt(accumulator.send_bytes)) / seconds else 0,
                .event_count = if (available) accumulator.event_count else 0,
                .external_receive_bytes = if (available) accumulator.external_receive_bytes else 0,
                .external_send_bytes = if (available) accumulator.external_send_bytes else 0,
                .external_receive_bytes_per_second = if (available) @as(f64, @floatFromInt(accumulator.external_receive_bytes)) / seconds else 0,
                .external_send_bytes_per_second = if (available) @as(f64, @floatFromInt(accumulator.external_send_bytes)) / seconds else 0,
                .external_event_count = if (available) accumulator.external_event_count else 0,
            };
            row_count += 1;
        }
        std.mem.sort(Row, rows_out[0..row_count], {}, rowLessThan);
        const started = capture_started_tick_ms orelse std.math.minInt(i64);
        const warmed = started == std.math.minInt(i64) or now_tick_ms - started >= self.config.window_ms;
        return .{
            .complete = self.incomplete_through_tick < cutoff and warmed,
            .rows = rows_out[0..row_count],
            .start_tick_ms = cutoff,
            .through_tick_ms = now_tick_ms,
            .window_ms = self.config.window_ms,
        };
    }

    fn rowLessThan(_: void, left: Row, right: Row) bool {
        return left.process_id < right.process_id;
    }

    fn closeSnapshotHandles(self: *Owner) void {
        var process_iter = self.processes.valueIterator();
        while (process_iter.next()) |accumulator| {
            if (accumulator.handle) |handle| {
                self.identity_source.close_process(self.identity_source.context, handle);
                accumulator.handle = null;
            }
        }
    }

    fn ensureEventCapacity(self: *Owner, tick_ms: i64) !void {
        if (self.event_count < self.events.len) return;
        const current = self.events.len;
        const proposed = if (current == 0) self.config.initial_event_capacity else current *| 2;
        const next = @min(self.config.maximum_retained_events, proposed);
        if (next <= current) return self.capacityFailure(tick_ms);
        const allocator = self.budget.allocator();
        const expanded = allocator.alloc(Event, next) catch return error.OutOfMemory;
        errdefer allocator.free(expanded);
        for (0..self.event_count) |index| expanded[index] = self.events[(self.event_head + index) % current];
        if (current != 0) allocator.free(self.events);
        self.events = expanded;
        self.event_head = 0;
    }

    fn prune(self: *Owner, now_tick_ms: i64) void {
        const cutoff = now_tick_ms - self.config.retention_ms;
        while (self.event_count != 0 and self.events[self.event_head].tick_ms < cutoff) {
            self.event_head = (self.event_head + 1) % self.events.len;
            self.event_count -= 1;
        }
        if (self.event_count == 0) {
            self.event_head = 0;
            // No retained input uses the ring or aggregation table now. The
            // next real transfer grows from the declared initial capacity.
            const allocator = self.budget.allocator();
            if (self.events.len != 0) allocator.free(self.events);
            self.events = &.{};
            self.closeSnapshotHandles();
            self.processes.deinit(allocator);
            self.processes = .empty;
        }
    }

    fn capacityFailure(self: *Owner, tick_ms: i64) error{CapacityExceeded} {
        self.capacity_failures +|= 1;
        self.invalidate(tick_ms);
        return error.CapacityExceeded;
    }

    fn snapshotCapacityFailure(self: *Owner, tick_ms: i64) error{CapacityExceeded} {
        self.capacity_failures +|= 1;
        self.incomplete_through_tick = @max(self.incomplete_through_tick, tick_ms);
        self.closeSnapshotHandles();
        self.processes.clearRetainingCapacity();
        return error.CapacityExceeded;
    }
};
