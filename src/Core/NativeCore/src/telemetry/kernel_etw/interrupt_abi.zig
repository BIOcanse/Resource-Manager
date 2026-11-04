const std = @import("std");
const interrupts = @import("interrupts.zig");
const Budget = @import("budget.zig").Budget;
const RegionAllocator = @import("region_allocator.zig").RegionAllocator;
const Usage = @import("protocol.zig").Usage;
const Result = @import("../../common/result_codes.zig").ResultCode;

pub const abi_version: u32 = 0x0001_0000;
pub const Config = extern struct {
    size: u32 = @sizeOf(Config),
    version: u32 = abi_version,
    max_modules: u32,
    max_path_units: u32,
    max_identities: u32,
    max_slices: u32,
    max_slice_drivers: u32,
    bucket_ms: u32,
    bucket_count: u32,
    reported_drivers: u32,
    byte_cap: u64,
    generation: u64,
    started_tick_ms: i64,
    events_lost: u64,
};
pub const Identity = extern struct {
    path: ?[*]const u16 = null,
    name: ?[*]const u16 = null,
    path_len: u32 = 0,
    name_len: u32 = 0,
};
pub const Driver = extern struct {
    module: Identity,
    total_duration_ms: f64,
    maximum_single_duration_ms: f64,
    cpu_capacity_percent: f64,
    event_count: u64,
    long_event_count: u64,
};
pub const Bucket = extern struct {
    started_tick_ms: i64,
    ended_tick_ms: i64,
    total_duration_ms: f64,
    cpu_capacity_percent: f64,
    event_count: u64,
};
pub const View = extern struct {
    generation: u64 = 0,
    events_lost: u64 = 0,
    started_tick_ms: i64 = 0,
    ended_tick_ms: i64 = 0,
    window_ms: i64 = 0,
    processor_count: u32 = 0,
    flags: u32 = 0, // bit 0: at least one complete 10-second bucket
    total_duration_ms: f64 = 0,
    maximum_single_duration_ms: f64 = 0,
    cpu_capacity_percent: f64 = 0,
    event_count: u64 = 0,
    long_event_count: u64 = 0,
    maximum_kind: u32 = 0,
    reserved: u32 = 0,
    maximum_module: Identity = .{},
    driver_count: u32 = 0,
    bucket_count: u32 = 0,
    drivers: ?[*]const Driver = null,
    buckets: ?[*]const Bucket = null,
    current_bytes: u64 = 0,
    peak_bytes: u64 = 0,
};

pub const Session = struct {
    budget: Budget,
    backing: std.mem.Allocator,
    mutex: std.atomic.Mutex = .unlocked,
    owner: interrupts.Owner,
    publication: ?std.heap.ArenaAllocator = null,
    last_error: i32 = 0,
    active_traces: usize = 0,
    region: ?RegionAllocator = null,
    external_region_bytes: usize = 0,

    pub fn create(backing: std.mem.Allocator, cfg: *const Config) !*Session {
        if (cfg.size != @sizeOf(Config) or cfg.version != abi_version or cfg.byte_cap < @sizeOf(Session)) return error.InvalidConfig;
        const self = try backing.create(Session);
        errdefer backing.destroy(self);
        self.* = .{ .backing = backing, .budget = .{ .backing = backing, .cap = @intCast(cfg.byte_cap), .current = @sizeOf(Session), .peak = @sizeOf(Session) }, .owner = undefined };
        self.owner = try interrupts.Owner.init(self.budget.allocator(), .{
            .max_modules = cfg.max_modules,
            .max_path_units = cfg.max_path_units,
            .max_identities = cfg.max_identities,
            .max_slices = cfg.max_slices,
            .max_slice_drivers = cfg.max_slice_drivers,
            .bucket_ms = cfg.bucket_ms,
            .bucket_count = cfg.bucket_count,
            .reported_drivers = cfg.reported_drivers,
        }, cfg.generation, cfg.started_tick_ms, cfg.events_lost);
        return self;
    }

    /// The caller retains the whole ledger lease until destroy succeeds.
    /// The Session, allocator metadata, working state and views all stay inside.
    pub fn createExternal(span: []u8, cfg: *const Config) !*Session {
        if (cfg.size != @sizeOf(Config) or cfg.version != abi_version or
            cfg.byte_cap < @sizeOf(Session) or span.len > cfg.byte_cap) return error.InvalidConfig;
        const address = @intFromPtr(span.ptr);
        const aligned = (try std.math.add(usize, address, @alignOf(Session) - 1)) & ~(@as(usize, @alignOf(Session)) - 1);
        const prefix = aligned - address;
        if (prefix > span.len or span.len - prefix < @sizeOf(Session)) return error.InvalidConfig;
        const remaining = span[prefix + @sizeOf(Session) ..];
        const region = RegionAllocator.init(remaining) catch return error.InvalidConfig;
        const self: *Session = @ptrFromInt(aligned);
        self.* = .{ .backing = undefined, .budget = undefined, .owner = undefined, .region = region, .external_region_bytes = span.len };
        errdefer self.region.?.deinit();
        self.budget = .{ .backing = self.region.?.allocator(), .cap = @intCast(cfg.byte_cap), .current = @sizeOf(Session), .peak = @sizeOf(Session) };
        self.owner = try interrupts.Owner.init(self.budget.allocator(), .{
            .max_modules = cfg.max_modules,
            .max_path_units = cfg.max_path_units,
            .max_identities = cfg.max_identities,
            .max_slices = cfg.max_slices,
            .max_slice_drivers = cfg.max_slice_drivers,
            .bucket_ms = cfg.bucket_ms,
            .bucket_count = cfg.bucket_count,
            .reported_drivers = cfg.reported_drivers,
        }, cfg.generation, cfg.started_tick_ms, cfg.events_lost);
        return self;
    }

    pub fn destroy(self: *Session) void {
        std.debug.assert(self.active_traces == 0);
        if (self.publication) |*arena| arena.deinit();
        self.owner.deinit();
        std.debug.assert(self.budget.current == @sizeOf(Session));
        if (self.region) |*region| {
            region.deinit();
            self.* = undefined;
            return;
        }
        const backing = self.backing;
        backing.destroy(self);
    }

    pub fn lock(self: *Session) void {
        while (!self.mutex.tryLock()) std.atomic.spinLoopHint();
    }

    pub fn fail(self: *Session, err: anyerror) i32 {
        const result: Result = switch (err) {
            error.InvalidConfig, error.InvalidInput => .invalid_argument,
            error.CapacityExceeded => .buffer_too_small,
            error.OutOfMemory => .out_of_memory,
            else => .unavailable,
        };
        if (err == error.CapacityExceeded or err == error.OutOfMemory) self.owner.invalidated = true;
        self.last_error = code(result);
        return self.last_error;
    }
};

pub fn session(handle: ?*anyopaque) ?*Session {
    return @ptrCast(@alignCast(handle));
}
fn code(result: Result) i32 {
    return @intFromEnum(result);
}
fn moduleIdentity(value: interrupts.Identity) Identity {
    return .{ .path = if (value.path) |p| p.ptr else null, .path_len = if (value.path) |p| @intCast(p.len) else 0, .name = if (value.name) |n| n.ptr else null, .name_len = if (value.name) |n| @intCast(n.len) else 0 };
}

pub export fn rm_kernel_etw_interrupt_abi_version() callconv(.c) u32 {
    return abi_version;
}
pub export fn rm_kernel_etw_interrupt_create(config: ?*const Config, output: ?*?*anyopaque) callconv(.c) i32 {
    const out = output orelse return code(.invalid_argument);
    out.* = null;
    const cfg = config orelse return code(.invalid_argument);
    if (cfg.size != @sizeOf(Config) or cfg.version != abi_version) return code(.abi_mismatch);
    // Replay/candidate construction. Production uses create_in_region with the
    // caller's ledger lease; small objects never allocate individual OS pages.
    out.* = Session.create(std.heap.smp_allocator, cfg) catch |err| return if (err == error.OutOfMemory) code(.out_of_memory) else code(.invalid_argument);
    return code(.ok);
}
pub export fn rm_kernel_etw_interrupt_create_in_region(config: ?*const Config, memory: ?[*]u8, length: usize, output: ?*?*anyopaque) callconv(.c) i32 {
    const out = output orelse return code(.invalid_argument);
    out.* = null;
    const cfg = config orelse return code(.invalid_argument);
    if (cfg.size != @sizeOf(Config) or cfg.version != abi_version) return code(.abi_mismatch);
    const span = (memory orelse return code(.invalid_argument))[0..length];
    out.* = Session.createExternal(span, cfg) catch |err| return if (err == error.OutOfMemory) code(.out_of_memory) else code(.invalid_argument);
    return code(.ok);
}
pub export fn rm_kernel_etw_interrupt_destroy(handle: ?*anyopaque) callconv(.c) i32 {
    const self = session(handle) orelse return code(.invalid_argument);
    self.lock();
    if (self.active_traces != 0 or self.publication != null) {
        self.mutex.unlock();
        return code(.unavailable);
    }
    self.mutex.unlock();
    self.destroy();
    return code(.ok);
}

/// Reserved counts the full caller span; occupied includes Session/alignment
/// overhead and the region's physical block headers, padding and slack.
pub const RegionUsage = extern struct {
    reserved_bytes: u64 = 0,
    usable_bytes: u64 = 0,
    occupied_bytes: u64 = 0,
    peak_occupied_bytes: u64 = 0,
    requested_bytes: u64 = 0,
    peak_requested_bytes: u64 = 0,
    live_allocations: u64 = 0,
    failed_allocations: u64 = 0,
};
pub export fn rm_kernel_etw_interrupt_query_region_usage(handle: ?*anyopaque, output: ?*RegionUsage, size: u32) callconv(.c) i32 {
    const self = session(handle) orelse return code(.invalid_argument);
    const out = output orelse return code(.invalid_argument);
    if (size != @sizeOf(RegionUsage)) return code(.abi_mismatch);
    self.lock();
    defer self.mutex.unlock();
    out.* = .{};
    const region = if (self.region) |*value| value else return code(.unavailable);
    const overhead = self.external_region_bytes - region.capacity;
    out.* = .{ .reserved_bytes = self.external_region_bytes, .usable_bytes = region.capacity, .occupied_bytes = overhead + region.current, .peak_occupied_bytes = overhead + region.peak, .requested_bytes = self.budget.current, .peak_requested_bytes = self.budget.peak, .live_allocations = region.live_allocations, .failed_allocations = self.budget.failures };
    return code(.ok);
}
pub export fn rm_kernel_etw_interrupt_image(handle: ?*anyopaque, base: u64, size: u64, path: ?[*]const u16, path_len: u32, observed_tick_ms: i64, unload: u32) callconv(.c) i32 {
    const self = session(handle) orelse return code(.invalid_argument);
    if (unload > 1 or (path_len > 0 and path == null)) return code(.invalid_argument);
    self.lock();
    defer self.mutex.unlock();
    if (self.owner.invalidated) return self.fail(error.Invalidated);
    if (!interrupts.isKernelAddress(base)) return code(.ok);
    if (unload == 1) self.owner.removeImage(base, observed_tick_ms) else {
        const value = if (path) |p| p[0..path_len] else null;
        self.owner.upsertImage(base, size, value, observed_tick_ms) catch |err| return self.fail(err);
    }
    return code(.ok);
}
pub export fn rm_kernel_etw_interrupt_observe(handle: ?*anyopaque, kind: u32, duration_ms: f64, routine: u64, observed_tick_ms: i64) callconv(.c) i32 {
    const self = session(handle) orelse return code(.invalid_argument);
    const value = std.enums.fromInt(interrupts.Kind, kind) orelse return code(.invalid_argument);
    self.lock();
    defer self.mutex.unlock();
    self.owner.observe(value, duration_ms, routine, observed_tick_ms) catch |err| return self.fail(err);
    return code(.ok);
}
pub export fn rm_kernel_etw_interrupt_reset(handle: ?*anyopaque, started_tick_ms: i64, events_lost: u64) callconv(.c) i32 {
    const self = session(handle) orelse return code(.invalid_argument);
    self.lock();
    defer self.mutex.unlock();
    self.owner.reset(started_tick_ms, events_lost);
    self.last_error = 0;
    return code(.ok);
}
pub export fn rm_kernel_etw_interrupt_refresh_loss(handle: ?*anyopaque, now_tick_ms: i64, events_lost: u64) callconv(.c) i32 {
    const self = session(handle) orelse return code(.invalid_argument);
    self.lock();
    defer self.mutex.unlock();
    if (self.owner.refreshLoss(now_tick_ms, events_lost)) self.last_error = 0;
    return code(.ok);
}

/// Exactly one outstanding publication. Its pointers remain valid across input,
/// reset and loss until release_view. Destroy refuses a live view lease.
pub export fn rm_kernel_etw_interrupt_acquire_view(handle: ?*anyopaque, now_tick_ms: i64, processor_count: u32, output: ?*View) callconv(.c) i32 {
    const self = session(handle) orelse return code(.invalid_argument);
    const out = output orelse return code(.invalid_argument);
    out.* = .{};
    self.lock();
    defer self.mutex.unlock();
    if (self.publication != null) return code(.unavailable);
    var arena = std.heap.ArenaAllocator.init(self.budget.allocator());
    const snapshot = self.owner.publish(arena.allocator(), now_tick_ms, processor_count) catch |err| {
        arena.deinit();
        return self.fail(err);
    };
    const drivers = arena.allocator().alloc(Driver, snapshot.drivers.len) catch |err| {
        arena.deinit();
        return self.fail(err);
    };
    const buckets = arena.allocator().alloc(Bucket, snapshot.bucket_count) catch |err| {
        arena.deinit();
        return self.fail(err);
    };
    for (snapshot.drivers, drivers) |d, *row| row.* = .{ .module = moduleIdentity(d.module), .total_duration_ms = d.total_duration_ms, .maximum_single_duration_ms = d.maximum_single_duration_ms, .cpu_capacity_percent = d.cpu_capacity_percent, .event_count = d.event_count, .long_event_count = d.long_event_count };
    for (snapshot.buckets[0..snapshot.bucket_count], buckets) |b, *row| row.* = .{ .started_tick_ms = b.started_tick_ms, .ended_tick_ms = b.ended_tick_ms, .total_duration_ms = b.total_duration_ms, .cpu_capacity_percent = b.cpu_capacity_percent, .event_count = b.event_count };
    self.publication = arena;
    out.* = .{ .generation = snapshot.generation, .events_lost = snapshot.events_lost, .started_tick_ms = snapshot.started_tick_ms, .ended_tick_ms = snapshot.ended_tick_ms, .window_ms = snapshot.window_ms, .processor_count = snapshot.processor_count, .flags = if (snapshot.available) 1 else 0, .total_duration_ms = snapshot.total_duration_ms, .maximum_single_duration_ms = snapshot.maximum_single_duration_ms, .cpu_capacity_percent = snapshot.cpu_capacity_percent, .event_count = snapshot.event_count, .long_event_count = snapshot.long_event_count, .maximum_kind = if (snapshot.maximum_kind) |kind| @intFromEnum(kind) else 0, .maximum_module = moduleIdentity(snapshot.maximum_module), .driver_count = @intCast(drivers.len), .bucket_count = @intCast(buckets.len), .drivers = drivers.ptr, .buckets = buckets.ptr, .current_bytes = self.budget.current, .peak_bytes = self.budget.peak };
    return code(.ok);
}
pub export fn rm_kernel_etw_interrupt_release_view(handle: ?*anyopaque) callconv(.c) void {
    const self = session(handle) orelse return;
    self.lock();
    defer self.mutex.unlock();
    if (self.publication) |*arena| arena.deinit();
    self.publication = null;
}
pub export fn rm_kernel_etw_interrupt_query_usage(handle: ?*anyopaque, output: ?*Usage, size: u32) callconv(.c) i32 {
    const self = session(handle) orelse return code(.invalid_argument);
    const out = output orelse return code(.invalid_argument);
    if (size != @sizeOf(Usage)) return code(.abi_mismatch);
    self.lock();
    defer self.mutex.unlock();
    out.* = .{ .current_bytes = self.budget.current, .peak_bytes = self.budget.peak, .byte_cap = self.budget.cap, .failed_allocations = self.budget.failures, .last_error = self.last_error, .trace_error = 0, .events_lost = self.owner.events_lost };
    return code(.ok);
}

test "interrupt ABI publication lease survives loss and releases all bytes" {
    const cfg: Config = .{ .max_modules = 8, .max_path_units = 128, .max_identities = 16, .max_slices = 64, .max_slice_drivers = 128, .bucket_ms = 10000, .bucket_count = 6, .reported_drivers = 16, .byte_cap = 1024 * 1024, .generation = 1, .started_tick_ms = 0, .events_lost = 0 };
    const self = try Session.create(std.testing.allocator, &cfg);
    defer self.destroy();
    const name = std.unicode.utf8ToUtf16LeStringLiteral("driver.sys");
    const before_images = self.budget.current;
    // Intake ignores user loads and unloads before creating any module strings.
    for ([_]u32{ 0, 1 }) |unload| {
        try std.testing.expectEqual(code(.ok), rm_kernel_etw_interrupt_image(self, 0xFFFF7FFFFFFFFFFF, 10, name, name.len, 0, unload));
        try std.testing.expectEqual(@as(usize, 0), self.owner.modules.len);
        try std.testing.expectEqual(before_images, self.budget.current);
    }
    try std.testing.expectEqual(@as(i32, 0), rm_kernel_etw_interrupt_image(self, 0xFFFF800000000100, 10, name, name.len, 0, 0));
    try std.testing.expectEqual(@as(i32, 0), rm_kernel_etw_interrupt_observe(self, 1, 1, 0xFFFF800000000100, 0));
    var view: View = .{};
    try std.testing.expectEqual(@as(i32, 0), rm_kernel_etw_interrupt_acquire_view(self, 10000, 1, &view));
    try std.testing.expectEqual(@as(u32, 1), view.driver_count);
    try std.testing.expectEqual(@as(i32, 0), rm_kernel_etw_interrupt_refresh_loss(self, 10000, 1));
    try std.testing.expectEqualSlices(u16, name, view.maximum_module.name.?[0..view.maximum_module.name_len]);
    var second: View = .{};
    try std.testing.expectEqual(@as(i32, 3), rm_kernel_etw_interrupt_acquire_view(self, 20000, 1, &second));
    rm_kernel_etw_interrupt_release_view(self);
    try std.testing.expectEqual(@as(i32, 0), rm_kernel_etw_interrupt_acquire_view(self, 20000, 1, &second));
    try std.testing.expectEqual(@as(u64, 0), second.event_count);
    rm_kernel_etw_interrupt_release_view(self);
}

test "interrupt external region contains state and immutable view until explicit release" {
    const capacity = 128 * 1024;
    const memory = try std.testing.allocator.alloc(u8, capacity + 1);
    defer std.testing.allocator.free(memory);
    const cfg: Config = .{ .max_modules = 8, .max_path_units = 128, .max_identities = 16, .max_slices = 64, .max_slice_drivers = 128, .bucket_ms = 10000, .bucket_count = 6, .reported_drivers = 16, .byte_cap = capacity, .generation = 1, .started_tick_ms = 0, .events_lost = 0 };
    var handle: ?*anyopaque = null;
    try std.testing.expectEqual(code(.ok), rm_kernel_etw_interrupt_create_in_region(&cfg, memory.ptr + 1, capacity, &handle));
    const self = session(handle).?;
    var alive = true;
    defer if (alive) self.destroy();
    const name = std.unicode.utf8ToUtf16LeStringLiteral("driver.sys");
    try std.testing.expectEqual(code(.ok), rm_kernel_etw_interrupt_image(handle, 0xFFFF800000000100, 10, name, name.len, 0, 0));
    try std.testing.expectEqual(code(.ok), rm_kernel_etw_interrupt_observe(handle, 1, 1, 0xFFFF800000000100, 0));
    const baseline = self.budget.current;
    var baseline_region: RegionUsage = .{};
    try std.testing.expectEqual(code(.ok), rm_kernel_etw_interrupt_query_region_usage(handle, &baseline_region, @sizeOf(RegionUsage)));
    var view: View = .{};
    try std.testing.expectEqual(code(.ok), rm_kernel_etw_interrupt_acquire_view(handle, 10000, 1, &view));
    try std.testing.expectEqual(@as(u32, 1), view.driver_count);
    const begin = @intFromPtr(memory.ptr + 1);
    const end = begin + capacity;
    try std.testing.expect(@intFromPtr(self) >= begin and @intFromPtr(self) + @sizeOf(Session) <= end);
    try std.testing.expect(@intFromPtr(view.drivers.?) >= begin and @intFromPtr(view.drivers.?) + @sizeOf(Driver) <= end);
    try std.testing.expect(@intFromPtr(view.maximum_module.name.?) >= begin and @intFromPtr(view.maximum_module.name.?) + name.len * @sizeOf(u16) <= end);
    try std.testing.expectEqual(code(.unavailable), rm_kernel_etw_interrupt_destroy(handle));
    var usage: RegionUsage = .{};
    try std.testing.expectEqual(code(.ok), rm_kernel_etw_interrupt_query_region_usage(handle, &usage, @sizeOf(RegionUsage)));
    try std.testing.expectEqual(@as(u64, capacity), usage.reserved_bytes);
    try std.testing.expect(usage.occupied_bytes > usage.requested_bytes and usage.occupied_bytes <= usage.reserved_bytes);
    try std.testing.expect(usage.peak_occupied_bytes >= usage.occupied_bytes and usage.peak_occupied_bytes <= usage.reserved_bytes);
    try std.testing.expect(usage.live_allocations != 0);
    rm_kernel_etw_interrupt_release_view(handle);
    const held_bytes = self.budget.current;
    const scratch_capacity = self.owner.publication_round.queryCapacity();
    // Publication now returns its scratch at the end of the owner round.
    // Releasing the immutable view must restore the exact native baseline;
    // retaining a bounded scratch difference is no longer sufficient.
    try std.testing.expectEqual(@as(usize, 0), scratch_capacity);
    try std.testing.expectEqual(baseline, held_bytes);
    var released_region: RegionUsage = .{};
    try std.testing.expectEqual(code(.ok), rm_kernel_etw_interrupt_query_region_usage(handle, &released_region, @sizeOf(RegionUsage)));
    try std.testing.expectEqual(baseline_region.requested_bytes, released_region.requested_bytes);
    try std.testing.expectEqual(baseline_region.occupied_bytes, released_region.occupied_bytes);
    try std.testing.expectEqual(baseline_region.live_allocations, released_region.live_allocations);
    for (0..4) |index| {
        var next: View = .{};
        try std.testing.expectEqual(code(.ok), rm_kernel_etw_interrupt_acquire_view(handle, 10001 + @as(i64, @intCast(index)), 1, &next));
        try std.testing.expectEqual(@as(u64, 1), next.event_count);
        try std.testing.expectEqualSlices(u16, name, next.maximum_module.name.?[0..next.maximum_module.name_len]);
        rm_kernel_etw_interrupt_release_view(handle);
        try std.testing.expectEqual(held_bytes, self.budget.current);
        try std.testing.expectEqual(scratch_capacity, self.owner.publication_round.queryCapacity());
        try std.testing.expectEqual(code(.ok), rm_kernel_etw_interrupt_query_region_usage(handle, &released_region, @sizeOf(RegionUsage)));
        try std.testing.expectEqual(baseline_region.requested_bytes, released_region.requested_bytes);
        try std.testing.expectEqual(baseline_region.occupied_bytes, released_region.occupied_bytes);
        try std.testing.expectEqual(baseline_region.live_allocations, released_region.live_allocations);
    }
    try std.testing.expectEqual(baseline, self.budget.current);
    self.active_traces = 1;
    const refused = rm_kernel_etw_interrupt_destroy(handle);
    self.active_traces = 0;
    try std.testing.expectEqual(code(.unavailable), refused);
    try std.testing.expectEqual(code(.ok), rm_kernel_etw_interrupt_destroy(handle));
    alive = false;
    @memset(memory, 0xaa); // Native destruction leaves caller-owned backing intact.
}

test "interrupt external region rejects undersized and over-cap backing" {
    var memory: [32]u8 = undefined;
    const cfg: Config = .{ .max_modules = 1, .max_path_units = 16, .max_identities = 2, .max_slices = 1, .max_slice_drivers = 1, .bucket_ms = 10000, .bucket_count = 6, .reported_drivers = 16, .byte_cap = 4096, .generation = 1, .started_tick_ms = 0, .events_lost = 0 };
    var output: ?*anyopaque = null;
    try std.testing.expectEqual(code(.invalid_argument), rm_kernel_etw_interrupt_create_in_region(&cfg, &memory, memory.len, &output));
    try std.testing.expectEqual(@as(?*anyopaque, null), output);
    var tiny_cap = cfg;
    tiny_cap.byte_cap = 1;
    try std.testing.expectEqual(code(.invalid_argument), rm_kernel_etw_interrupt_create_in_region(&tiny_cap, &memory, memory.len, &output));
}
