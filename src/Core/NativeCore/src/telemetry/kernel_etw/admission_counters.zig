const std = @import("std");
const Allocator = std.mem.Allocator;

// These values are also the managed diagnostic key order. No strings cross the
// event path. Physical delivery and consumer rejection are separate totals.
pub const PhysicalKind = enum(u32) {
    process_start, process_end, thread_start, thread_end, context_switch,
    disk_read, disk_write, file_create, file_name_create, file_name_delete,
    file_write, file_rename, file_delete, tcp_send, tcp_receive, tcp6_send,
    tcp6_receive, udp_send, udp_receive, udp6_send, udp6_receive, image_rundown,
    image_load, image_unload, isr, dpc, threaded_dpc, timer_dpc,
};
pub const Kind = enum(u32) {
    process_start, process_end, thread_start, thread_end, context_switch,
    isr, dpc, threaded_dpc, timer_dpc, image_load_or_rundown, image_unload,
    disk_transfer, process_identity, network_transfer, renderer_invalidation,
    process_notification,
};
pub const Reason = enum(u32) {
    // Ordinal2 is reserved after retiring mode rejection; frame layout stays fixed.
    none, generation_mismatch, reserved_2, outside_window, before_window,
    unmapped_processor, invalid_duration, inactive_binding, invalid_payload,
    stale_binding, incomplete_window, capacity, before_process_birth,
    invalid_process, stopped, system_process, closed_queue, user_mode_image,
};
pub const kind_count = @typeInfo(Kind).@"enum".field_names.len;
pub const physical_count = @typeInfo(PhysicalKind).@"enum".field_names.len;
pub const reason_count = @typeInfo(Reason).@"enum".field_names.len;
pub const all_kinds: u64 = (@as(u64, 1) << kind_count) - 1;
pub const active: u32 = 1;
pub const window_active: u32 = 4;
pub const current_binding: u32 = 8;
pub const stopped: u32 = 16;
pub const all_flags = active | window_active | current_binding | stopped;
pub const all_outputs: u32 = 127;

pub const Pair = extern struct { received: u64 = 0, dropped: u64 = 0 };
/// A retired binding's final immutable totals. Taking it acknowledges native
/// release; the host folds these bounded rows into stable holder diagnostics.
pub const Frame = extern struct {
    totals: [kind_count]Pair = @as([kind_count]Pair, @splat(.{})),
    rejected: [kind_count][reason_count]u64 = @as([kind_count][reason_count]u64, @splat(@as([reason_count]u64, @splat(0)))),
};
pub const Config = extern struct {
    token: u64,
    generation: i64,
    kind_mask: u64,
    output_mask: u32,
    flags: u32,
    // CPU uses event QPC; IRQ uses its observed monotonic millisecond tick.
    window_start: i64,
    // Network uses the observed millisecond tick, including equality rejection.
    incomplete_through: i64,
};
pub const Facts = struct {
    generation: i64,
    qpc: i64 = 0,
    observed_tick: i64 = 0,
    process_id: i32 = 1,
    mapped_processor: bool = true,
    duration_ms: f64 = 1,
    valid_payload: bool = true,
    current: bool = true,
    before_process_birth: bool = false,
    at_capacity: bool = false,
    queue_closed: bool = false,
    current_window_start: ?i64 = null,
    current_window_active: ?bool = null,
    current_incomplete_through: ?i64 = null,
    kernel_image: bool = true,
};
pub const Observation = struct { had_handler: bool = false, accepted: usize = 0, epoch: u64 = 0 };

const Atomic = std.atomic.Value(u64);
const Counter = struct {
    received: Atomic = Atomic.init(0),
    dropped: Atomic = Atomic.init(0),
    fn receive(self: *Counter) void { increment(&self.received); }
    fn drop(self: *Counter) void { increment(&self.dropped); }
    fn read(self: *const Counter) Pair {
        const dropped = self.dropped.load(.acquire);
        return .{ .received = self.received.load(.acquire), .dropped = dropped };
    }
};
fn increment(value: *Atomic) void {
    // One flow writes; cold readers only load. Saturation preserves monotonic
    // diagnostics rather than wrapping an exceptionally long-lived session.
    value.store(value.load(.monotonic) +| 1, .release);
}
const Consumer = struct {
    config: Config,
    totals: [kind_count]Counter = @as([kind_count]Counter, @splat(.{})),
    rejected: [kind_count][reason_count]Atomic = @as([kind_count][reason_count]Atomic, @splat(@as([reason_count]Atomic, @splat(Atomic.init(0))))),
    accepted_epoch: [kind_count]u64 = @as([kind_count]u64, @splat(0)),
    retire_requested: bool = false,
    next_retired: ?*Consumer = null,
};
const Mutex = struct {
    value: std.atomic.Mutex = .unlocked,
    fn lock(self: *Mutex) void { while (!self.value.tryLock()) std.atomic.spinLoopHint(); }
    fn unlock(self: *Mutex) void { self.value.unlock(); }
};

/// Mutable configuration is changed only on the consumer flow. Its cold query
/// mutex protects descriptor lookup/growth, never event increments. Descriptors
/// allocate only for registered counter kinds, bounded by the explicit command
/// descriptor capacity; no max-capacity array is committed on init.
/// Retirement allocates nothing: a descriptor is frozen by unlinking it from
/// intake into a list only host acknowledgement walks, so stop and restart can
/// prove release even after an owner allocation fault.
pub const Store = struct {
    allocator: Allocator,
    maximum_consumers: usize,
    consumers: std.AutoHashMap(u64, *Consumer),
    retired: ?*Consumer = null,
    retired_count: usize = 0,
    retire_requests: usize = 0,
    physical: [physical_count]Counter = @as([physical_count]Counter, @splat(.{})),
    query_mutex: Mutex = .{},
    epoch: u64 = 0,

    pub fn init(allocator: Allocator, maximum_consumers: usize) !Store {
        if (maximum_consumers == 0) return error.InvalidConfig;
        return .{ .allocator = allocator, .maximum_consumers = maximum_consumers,
            .consumers = std.AutoHashMap(u64, *Consumer).init(allocator) };
    }
    pub fn deinit(self: *Store) void {
        var values = self.consumers.valueIterator();
        while (values.next()) |entry| self.allocator.destroy(entry.*);
        self.consumers.deinit();
        while (self.retired) |consumer| {
            self.retired = consumer.next_retired;
            self.allocator.destroy(consumer);
        }
        self.* = undefined;
    }
    pub fn configure(self: *Store, config: Config) !void {
        if (config.token == 0 or config.kind_mask & ~all_kinds != 0 or
            config.output_mask & ~all_outputs != 0 or config.flags & ~all_flags != 0)
            return error.InvalidConfig;
        self.query_mutex.lock();
        defer self.query_mutex.unlock();
        if (self.findRetired(config.token) != null) return error.InvalidConfig;
        if (self.consumers.get(config.token)) |consumer| {
            if (consumer.retire_requested) return error.InvalidConfig;
            consumer.config = config;
            return;
        }
        // A duration hold with no counter reader needs no descriptor/storage.
        if (config.kind_mask == 0) return;
        if (self.consumers.count() + self.retired_count >= self.maximum_consumers) return error.CapacityExceeded;
        const consumer = try self.allocator.create(Consumer);
        errdefer self.allocator.destroy(consumer);
        consumer.* = .{ .config = config };
        try self.consumers.putNoClobber(config.token, consumer);
    }
    // Caller has cached the final immutable totals and stopped querying this
    // token before retiring it. A new token never inherits this descriptor.
    pub fn retire(self: *Store, token: u64) void {
        self.query_mutex.lock();
        defer self.query_mutex.unlock();
        if (self.consumers.fetchRemove(token)) |entry| self.allocator.destroy(entry.value);
        if (self.consumers.count() == 0) self.consumers.clearAndFree();
    }
    /// Runs on the event consumer after all earlier counter commands, or with
    /// no consumer running. No event can mutate this binding once it is frozen.
    pub fn retireToSnapshot(self: *Store, token: u64) !void {
        if (token == 0) return error.InvalidConfig;
        self.query_mutex.lock();
        defer self.query_mutex.unlock();
        if (self.consumers.get(token)) |consumer| self.freeze(consumer);
    }
    /// Host side while the consumer flow runs: mark the descriptor in place and
    /// let the flow freeze it at its next drain. A token without a descriptor
    /// counted nothing and needs no mark.
    pub fn requestRetire(self: *Store, token: u64) !void {
        if (token == 0) return error.InvalidConfig;
        self.query_mutex.lock();
        defer self.query_mutex.unlock();
        const consumer = self.consumers.get(token) orelse return;
        if (consumer.retire_requested) return;
        consumer.retire_requested = true;
        self.retire_requests += 1;
    }
    /// Consumer flow, before any further event: freeze every marked descriptor.
    pub fn applyRetireRequests(self: *Store) void {
        self.query_mutex.lock();
        defer self.query_mutex.unlock();
        while (self.retire_requests != 0) {
            var values = self.consumers.valueIterator();
            const marked = while (values.next()) |entry| {
                if (entry.*.retire_requested) break entry.*;
            } else unreachable;
            self.freeze(marked);
        }
    }
    fn freeze(self: *Store, consumer: *Consumer) void {
        if (consumer.retire_requested) {
            consumer.retire_requested = false;
            self.retire_requests -= 1;
        }
        _ = self.consumers.remove(consumer.config.token);
        if (self.consumers.count() == 0) self.consumers.clearAndFree();
        consumer.next_retired = self.retired;
        self.retired = consumer;
        self.retired_count += 1;
    }
    fn findRetired(self: *const Store, token: u64) ?*Consumer {
        var cursor = self.retired;
        while (cursor) |consumer| : (cursor = consumer.next_retired) if (consumer.config.token == token) return consumer;
        return null;
    }
    /// Cold host acknowledgement: the retired list is never walked by event
    /// intake, so it can be copied/freed here without a lock on observation.
    /// A token without a descriptor counted nothing; its frame is empty. A
    /// descriptor still live or marked has not been released yet.
    pub fn takeRetired(self: *Store, token: u64) ?Frame {
        self.query_mutex.lock();
        defer self.query_mutex.unlock();
        var link = &self.retired;
        while (link.*) |consumer| : (link = &consumer.next_retired) {
            if (consumer.config.token != token) continue;
            link.* = consumer.next_retired;
            self.retired_count -= 1;
            var frame: Frame = .{};
            for (0..kind_count) |kind| {
                frame.totals[kind] = consumer.totals[kind].read();
                for (0..reason_count) |reason|
                    frame.rejected[kind][reason] = consumer.rejected[kind][reason].load(.acquire);
            }
            self.allocator.destroy(consumer);
            return frame;
        }
        if (self.consumers.contains(token)) return null;
        return .{};
    }
    pub fn recordPhysical(self: *Store, kind: PhysicalKind, had_handler: bool) void {
        const counter = &self.physical[@intFromEnum(kind)];
        counter.receive();
        if (!had_handler) counter.drop();
    }
    pub fn queryPhysical(self: *const Store, kind: PhysicalKind) Pair {
        return self.physical[@intFromEnum(kind)].read();
    }
    pub fn query(self: *Store, token: u64, kind: Kind, reason: Reason) ?Pair {
        self.query_mutex.lock();
        defer self.query_mutex.unlock();
        const consumer = self.consumers.get(token) orelse return null;
        if (reason == .none) return consumer.totals[@intFromEnum(kind)].read();
        const value = consumer.rejected[@intFromEnum(kind)][@intFromEnum(reason)].load(.acquire);
        return .{ .received = value, .dropped = value };
    }
    pub fn observe(self: *Store, output: u32, kind: Kind, facts: Facts) !Observation {
        self.epoch = try std.math.add(u64, self.epoch, 1);
        var result = Observation{ .epoch = self.epoch };
        var values = self.consumers.valueIterator();
        while (values.next()) |entry| {
            const consumer = entry.*;
            if (consumer.config.output_mask & output == 0 or consumer.config.flags & active == 0 or
                consumer.config.kind_mask & bit(kind) == 0) continue;
            result.had_handler = true;
            if (admitConsumer(consumer, kind, facts, self.epoch)) result.accepted += 1;
        }
        return result;
    }
    /// For a callback already physically admitted before cancellation/state
    /// change. Normal intake uses observe(), which skips inactive descriptors.
    pub fn observeAdmitted(self: *Store, token: u64, kind: Kind, facts: Facts) !bool {
        const consumer = self.consumers.get(token) orelse return false;
        if (consumer.config.kind_mask & bit(kind) == 0) return false;
        self.epoch = try std.math.add(u64, self.epoch, 1);
        return admitConsumer(consumer, kind, facts, self.epoch);
    }
    /// Business/identity checks performed after basic admission reject exactly
    /// the consumers accepted by that observation, without receiving twice.
    pub fn rejectObservation(self: *Store, observation: Observation, kind: Kind, reason: Reason) void {
        std.debug.assert(reason != .none);
        var values = self.consumers.valueIterator();
        while (values.next()) |entry| {
            const consumer = entry.*;
            const index = @intFromEnum(kind);
            if (consumer.accepted_epoch[index] != observation.epoch) continue;
            consumer.accepted_epoch[index] = 0;
            reject(consumer, kind, reason);
        }
    }
};

pub fn bit(kind: Kind) u64 { return @as(u64, 1) << @intCast(@intFromEnum(kind)); }
fn reject(consumer: *Consumer, kind: Kind, reason: Reason) void {
    consumer.totals[@intFromEnum(kind)].drop();
    increment(&consumer.rejected[@intFromEnum(kind)][@intFromEnum(reason)]);
}
fn admitConsumer(consumer: *Consumer, kind: Kind, facts: Facts, epoch: u64) bool {
    const index = @intFromEnum(kind);
    consumer.totals[index].receive();
    if (rejection(consumer.config, kind, facts)) |reason| { reject(consumer, kind, reason); return false; }
    consumer.accepted_epoch[index] = epoch;
    return true;
}
pub fn rejection(config: Config, kind: Kind, facts: Facts) ?Reason {
    const is_active = config.flags & active != 0;
    const is_current = config.flags & current_binding != 0 and facts.current and config.generation == facts.generation;
    const is_window_active = facts.current_window_active orelse (config.flags & window_active != 0);
    const window_start = facts.current_window_start orelse config.window_start;
    const incomplete_through = facts.current_incomplete_through orelse config.incomplete_through;
    return switch (kind) {
        .process_start, .process_end, .thread_start, .thread_end => if (!is_current) .generation_mismatch else null,
        .context_switch => if (!is_current) .generation_mismatch else if (!facts.mapped_processor) .unmapped_processor
            else if (!is_window_active) .outside_window
            else if (facts.qpc < window_start) .before_window else null,
        .isr, .dpc, .threaded_dpc, .timer_dpc => if (!std.math.isFinite(facts.duration_ms) or facts.duration_ms <= 0) .invalid_duration
            else if (!is_active) .inactive_binding else if (!is_current) .generation_mismatch
            else if (facts.observed_tick < window_start) .before_window else null,
        .image_load_or_rundown, .image_unload => if (!facts.kernel_image) .user_mode_image
            else if (!is_active) .inactive_binding else if (!is_current) .generation_mismatch
            else if (facts.observed_tick < window_start) .before_window else null,
        .disk_transfer => if (!facts.valid_payload) .invalid_payload else if (!is_active or !is_current) .stale_binding
            else if (facts.before_process_birth) .before_process_birth else null,
        .process_identity => if (!is_active or !is_current) .stale_binding else null,
        .network_transfer => if (!facts.valid_payload or facts.process_id <= 0) .invalid_payload
            else if (!is_active or !is_current) .stale_binding else if (facts.observed_tick <= incomplete_through) .incomplete_window
            else if (facts.at_capacity) .capacity else null,
        .renderer_invalidation => if (facts.process_id <= 0) .invalid_process else if (!is_active or !is_current) .stale_binding else null,
        .process_notification => if (!is_active or config.flags & stopped != 0) .stopped else if (facts.process_id <= 4) .system_process
            else if (facts.queue_closed) .closed_queue else null,
    };
}

/// The output handler exists even when its observation window or binding rejects;
/// physical drops mean no output handler, not a consumer rejection.
pub fn handlerOutputs(kind: PhysicalKind) u32 {
    return switch (kind) {
        .process_start => 1 | 4 | 8 | 32 | 64,
        .process_end => 1 | 4 | 8 | 32,
        .thread_start, .thread_end => 1 | 4 | 8,
        .context_switch => 1,
        .disk_read, .disk_write => 4,
        .file_create, .file_name_create, .file_name_delete, .file_write, .file_rename, .file_delete => 16,
        .tcp_send, .tcp_receive, .tcp6_send, .tcp6_receive, .udp_send, .udp_receive, .udp6_send, .udp6_receive => 8,
        .image_rundown, .image_load, .image_unload => 2 | 32,
        .isr, .dpc, .threaded_dpc, .timer_dpc => 2,
    };
}

fn fixture(token: u64) Config {
    return .{ .token = token, .generation = 17, .kind_mask = all_kinds, .output_mask = all_outputs,
        .flags = active | current_binding | window_active, .window_start = 100, .incomplete_through = -1 };
}
test "counter POD layouts and every physical kind preserve handler admission totals" {
    try std.testing.expectEqual(@as(usize, 48), @sizeOf(Config));
    try std.testing.expectEqual(@as(usize, 16), @sizeOf(Pair));
    try std.testing.expectEqual(@as(usize, 2560), @sizeOf(Frame));
    var store = try Store.init(std.testing.allocator, 4);
    defer store.deinit();
    inline for (std.meta.tags(PhysicalKind)) |kind| {
        store.recordPhysical(kind, false);
        store.recordPhysical(kind, handlerOutputs(kind) != 0);
        store.recordPhysical(kind, false);
        try std.testing.expectEqual(Pair{ .received = 3, .dropped = 2 }, store.queryPhysical(kind));
    }
}

test "retirement acknowledgement releases descriptors across more than the production lifetime limit" {
    var store = try Store.init(std.testing.allocator, 1);
    defer store.deinit();
    var token: u64 = 1;
    var total: u64 = 0;
    while (token <= 32770) : (token += 1) {
        try store.configure(fixture(token));
        _ = try store.observe(1, .context_switch, .{ .generation = 17, .qpc = 100 });
        try store.retireToSnapshot(token);
        try std.testing.expect(store.query(token, .context_switch, .none) == null);
        try std.testing.expectEqual(@as(usize, 0), store.consumers.count());
        // The bounded handoff occupies this slot until the host acknowledges.
        try std.testing.expectError(error.CapacityExceeded, store.configure(fixture(token + 1)));
        const frame = store.takeRetired(token).?;
        try std.testing.expectEqual(Pair{ .received = 1 }, frame.totals[@intFromEnum(Kind.context_switch)]);
        total += frame.totals[@intFromEnum(Kind.context_switch)].received;
        // Nothing remains to report once acknowledged.
        try std.testing.expectEqual(Frame{}, store.takeRetired(token).?);
        try std.testing.expectEqual(@as(usize, 0), store.retired_count);
        try std.testing.expectEqual(@as(usize, 0), store.consumers.capacity());
        try std.testing.expect(store.retired == null);
    }
    try std.testing.expectEqual(@as(u64, 32770), total);
}
test "retirement freezes descriptors without allocating after an allocation failure" {
    var failing = std.testing.FailingAllocator.init(std.testing.allocator, .{});
    var store = try Store.init(failing.allocator(), 3);
    defer store.deinit();
    try store.configure(fixture(1));
    try store.configure(fixture(2));
    _ = try store.observe(1, .context_switch, .{ .generation = 17, .qpc = 100 });
    failing.fail_index = failing.alloc_index;
    try std.testing.expectError(error.OutOfMemory, store.configure(fixture(3)));
    // No consumer flow: frozen at once.
    try store.retireToSnapshot(1);
    // Running consumer flow: marked, still counting until the flow freezes it.
    try store.requestRetire(2);
    try std.testing.expect(store.takeRetired(2) == null);
    try std.testing.expectError(error.InvalidConfig, store.configure(fixture(2)));
    try std.testing.expectEqual(@as(usize, 1), (try store.observe(1, .context_switch, .{ .generation = 17, .qpc = 101 })).accepted);
    store.applyRetireRequests();
    try std.testing.expect(!(try store.observe(1, .context_switch, .{ .generation = 17, .qpc = 102 })).had_handler);
    try std.testing.expectEqual(Pair{ .received = 1 }, store.takeRetired(1).?.totals[@intFromEnum(Kind.context_switch)]);
    try std.testing.expectEqual(Pair{ .received = 2 }, store.takeRetired(2).?.totals[@intFromEnum(Kind.context_switch)]);
    // The descriptor whose allocation failed counted nothing.
    try std.testing.expectEqual(Frame{}, store.takeRetired(3).?);
    try std.testing.expectEqual(failing.fail_index, failing.alloc_index);
}
test "one physical delivery is counted once for multiple native consumers and rejects remain separate" {
    var store = try Store.init(std.testing.allocator, 2);
    defer store.deinit();
    try store.configure(fixture(1));
    var outside_window = fixture(2);
    outside_window.flags &= ~window_active;
    try store.configure(outside_window);
    const observation = try store.observe(1, .context_switch, .{ .generation = 17, .qpc = 100 });
    store.recordPhysical(.context_switch, observation.had_handler);
    try std.testing.expectEqual(@as(usize, 1), observation.accepted);
    try std.testing.expectEqual(Pair{ .received = 1 }, store.queryPhysical(.context_switch));
    try std.testing.expectEqual(Pair{ .received = 1 }, store.query(1, .context_switch, .none).?);
    try std.testing.expectEqual(Pair{ .received = 1, .dropped = 1 }, store.query(2, .context_switch, .none).?);
    try std.testing.expectEqual(Pair{ .received = 1, .dropped = 1 }, store.query(2, .context_switch, .outside_window).?);
}
test "late rejection never receives twice and retired bindings stop admitting new events" {
    var store = try Store.init(std.testing.allocator, 2);
    defer store.deinit();
    try store.configure(fixture(1));
    const accepted = try store.observe(4, .disk_transfer, .{ .generation = 17 });
    store.rejectObservation(accepted, .disk_transfer, .before_process_birth);
    store.rejectObservation(accepted, .disk_transfer, .before_process_birth);
    try std.testing.expectEqual(Pair{ .received = 1, .dropped = 1 }, store.query(1, .disk_transfer, .none).?);
    var canceled = fixture(1);
    canceled.flags &= ~active;
    try store.configure(canceled);
    const next = try store.observe(4, .disk_transfer, .{ .generation = 17 });
    try std.testing.expect(!next.had_handler);
    try std.testing.expectEqual(Pair{ .received = 1, .dropped = 1 }, store.query(1, .disk_transfer, .none).?);
    store.retire(1);
    try std.testing.expect(store.query(1, .disk_transfer, .none) == null);
}
test "reference rejection precedence and positive stale QPC remain observable" {
    var config = fixture(1);
    config.flags &= ~window_active;
    try std.testing.expectEqual(Reason.invalid_duration, rejection(config, .dpc, .{ .generation = 18, .duration_ms = 0 }).?);
    try std.testing.expectEqual(Reason.unmapped_processor, rejection(config, .context_switch, .{ .generation = 17, .mapped_processor = false }).?);
    config.flags |= window_active;
    try std.testing.expectEqual(Reason.before_window, rejection(config, .context_switch, .{ .generation = 17, .qpc = 99 }).?);
    try std.testing.expect(rejection(config, .context_switch, .{ .generation = 17, .qpc = 100 }) == null);
    try std.testing.expectEqual(Reason.before_window, rejection(config, .image_unload, .{ .generation = 17, .observed_tick = 99 }).?);
    try std.testing.expectEqual(Reason.system_process, rejection(config, .process_notification, .{ .generation = 17, .process_id = 4 }).?);
}

test "network native continuity floor overrides stale cold configuration including equality" {
    var config = fixture(1);
    config.incomplete_through = 10;
    try std.testing.expect(rejection(config, .network_transfer, .{ .generation = 17, .observed_tick = 100 }) == null);
    try std.testing.expectEqual(Reason.incomplete_window, rejection(config, .network_transfer,
        .{ .generation = 17, .observed_tick = 100, .current_incomplete_through = 100 }).?);
    try std.testing.expect(rejection(config, .network_transfer,
        .{ .generation = 17, .observed_tick = 101, .current_incomplete_through = 100 }) == null);
    config.incomplete_through = 100;
    try std.testing.expect(rejection(config, .network_transfer,
        .{ .generation = 17, .observed_tick = 100, .current_incomplete_through = 99 }) == null);
}
test "descriptor limits and failed allocation do not replace existing counters" {
    var store = try Store.init(std.testing.allocator, 1);
    defer store.deinit();
    try store.configure(fixture(1));
    try std.testing.expectError(error.CapacityExceeded, store.configure(fixture(2)));
    _ = try store.observe(1, .process_start, .{ .generation = 17 });
    try std.testing.expectEqual(Pair{ .received = 1 }, store.query(1, .process_start, .none).?);
}
test "warmed native intake with and without a handler allocates nothing" {
    var failing = std.testing.FailingAllocator.init(std.testing.allocator, .{});
    var store = try Store.init(failing.allocator(), 1);
    defer store.deinit();
    try store.configure(fixture(1));
    const warm = try store.observe(1, .process_start, .{ .generation = 17 });
    store.recordPhysical(.process_start, warm.had_handler);
    failing.fail_index = failing.alloc_index;
    var index: usize = 0;
    while (index < 1000) : (index += 1) {
        const accepted = try store.observe(1, .process_start, .{ .generation = 17 });
        store.recordPhysical(.process_start, accepted.had_handler);
    }
    var canceled = fixture(1);
    canceled.flags &= ~active;
    try store.configure(canceled);
    index = 0;
    while (index < 1000) : (index += 1) {
        const absent = try store.observe(1, .process_start, .{ .generation = 17 });
        store.recordPhysical(.process_start, absent.had_handler);
    }
    try std.testing.expectEqual(Pair{ .received = 2001, .dropped = 1000 }, store.queryPhysical(.process_start));
    try std.testing.expectEqual(failing.fail_index, failing.alloc_index);
}
test "network reference callbacks preserve four received three rejected and every reason" {
    var store = try Store.init(std.testing.allocator, 1);
    defer store.deinit();
    var config = fixture(1);
    config.incomplete_through = 1000;
    try store.configure(config);
    try std.testing.expect(try store.observeAdmitted(1, .network_transfer, .{ .generation = 17, .observed_tick = 1001, .process_id = 41 }));
    try std.testing.expect(!try store.observeAdmitted(1, .network_transfer, .{ .generation = 17, .observed_tick = 1001, .process_id = 0 }));
    try std.testing.expect(!try store.observeAdmitted(1, .network_transfer, .{ .generation = 17, .observed_tick = 1000, .process_id = 41 }));
    try std.testing.expect(!try store.observeAdmitted(1, .network_transfer, .{ .generation = 18, .observed_tick = 1003, .process_id = 41 }));
    try std.testing.expectEqual(Pair{ .received = 4, .dropped = 3 }, store.query(1, .network_transfer, .none).?);
    inline for (.{ Reason.invalid_payload, Reason.incomplete_window, Reason.stale_binding }) |reason|
        try std.testing.expectEqual(Pair{ .received = 1, .dropped = 1 }, store.query(1, .network_transfer, reason).?);
    try std.testing.expectEqual(Pair{}, store.query(1, .network_transfer, .reserved_2).?);
}

test "disk reference callbacks preserve four received three rejected and every reason" {
    var store = try Store.init(std.testing.allocator, 1);
    defer store.deinit();
    try store.configure(fixture(1));
    try std.testing.expect(try store.observeAdmitted(1, .disk_transfer, .{ .generation = 17, .process_id = 41 }));
    try std.testing.expect(!try store.observeAdmitted(1, .disk_transfer, .{ .generation = 17, .process_id = 41, .valid_payload = false }));
    try std.testing.expect(!try store.observeAdmitted(1, .disk_transfer, .{ .generation = 17, .process_id = 41, .before_process_birth = true }));
    try std.testing.expect(!try store.observeAdmitted(1, .disk_transfer, .{ .generation = 18, .process_id = 41 }));
    try std.testing.expectEqual(Pair{ .received = 4, .dropped = 3 }, store.query(1, .disk_transfer, .none).?);
    inline for (.{ Reason.invalid_payload, Reason.before_process_birth, Reason.stale_binding }) |reason|
        try std.testing.expectEqual(Pair{ .received = 1, .dropped = 1 }, store.query(1, .disk_transfer, reason).?);
}

test "renderer reference callbacks preserve three received two rejected and cancellation" {
    var store = try Store.init(std.testing.allocator, 1);
    defer store.deinit();
    var config = fixture(1);
    try store.configure(config);
    try std.testing.expect(try store.observeAdmitted(1, .renderer_invalidation, .{ .generation = 17, .process_id = 41 }));
    try std.testing.expect(!try store.observeAdmitted(1, .renderer_invalidation, .{ .generation = 17, .process_id = 0 }));
    config.flags &= ~active;
    try store.configure(config);
    try std.testing.expect(!try store.observeAdmitted(1, .renderer_invalidation, .{ .generation = 17, .process_id = 41 }));
    try std.testing.expectEqual(Pair{ .received = 3, .dropped = 2 }, store.query(1, .renderer_invalidation, .none).?);
    inline for (.{ Reason.invalid_process, Reason.stale_binding }) |reason|
        try std.testing.expectEqual(Pair{ .received = 1, .dropped = 1 }, store.query(1, .renderer_invalidation, reason).?);
    try store.retireToSnapshot(1);
    const after = try store.observe(32, .renderer_invalidation, .{ .generation = 17, .process_id = 41 });
    try std.testing.expect(!after.had_handler);
    try std.testing.expectEqual(Pair{ .received = 3, .dropped = 2 }, store.takeRetired(1).?.totals[@intFromEnum(Kind.renderer_invalidation)]);
}

test "retired mode descriptor bit is rejected and its fixed frame column stays zero" {
    var store = try Store.init(std.testing.allocator, 1);
    defer store.deinit();
    var descriptor = fixture(1);
    descriptor.flags |= 2;
    try std.testing.expectError(error.InvalidConfig, store.configure(descriptor));
    try store.configure(fixture(1));
    try std.testing.expect(try store.observeAdmitted(1, .context_switch, .{ .generation = 17, .qpc = 100 }));
    try std.testing.expectEqual(Pair{}, store.query(1, .context_switch, .reserved_2).?);
    try std.testing.expectEqual(@as(u32, 2), @intFromEnum(Reason.reserved_2));
    try std.testing.expectEqual(@as(u32, 17), @intFromEnum(Reason.user_mode_image));
    try std.testing.expectEqual(@as(usize, 18), reason_count);
    try std.testing.expectEqual(@as(usize, 2560), @sizeOf(Frame));
}
