const std = @import("std");
const p = @import("protocol.zig");
const r = @import("residency.zig");
const Budget = @import("budget.zig").Budget;
const RegionAllocator = @import("region_allocator.zig").RegionAllocator;
const Result = @import("../../common/result_codes.zig").ResultCode;

pub const Session = struct {
    budget: Budget,
    backing: std.mem.Allocator,
    mutex: std.atomic.Mutex = .unlocked,
    residency: r.Owner,
    publication: ?std.heap.ArenaAllocator = null,
    last_error: i32 = 0,
    trace_error: u32 = 0,
    events_lost: u64 = 0,
    active_traces: usize = 0,
    region: ?RegionAllocator = null,
    external_region_bytes: usize = 0,

    pub fn create(backing: std.mem.Allocator, config: *const p.Config) !*Session {
        if (config.size != @sizeOf(p.Config) or config.version != p.abi_version or config.byte_cap < @sizeOf(Session)) return error.InvalidConfig;
        const self = try backing.create(Session);
        errdefer backing.destroy(self);
        self.* = .{
            .backing = backing,
            .budget = .{ .backing = backing, .cap = config.byte_cap, .current = @sizeOf(Session), .peak = @sizeOf(Session) },
            .residency = undefined,
        };
        self.residency = try r.Owner.init(self.budget.allocator(), .{
            .qpc_frequency = config.qpc_frequency,
            .window_ms = config.window_ms,
            .bucket_ms = config.bucket_ms,
            .max_pairs = config.max_pairs,
            .max_cells = config.max_cells,
            .max_processes = config.max_processes,
            .max_threads = config.max_threads,
            .max_processors = config.max_processors,
            .max_name_bytes = config.max_name_bytes,
        });
        return self;
    }

    /// The host owns this entire span and keeps its ledger lease until destroy
    /// succeeds. Session state and every owner/publication allocation stay in it.
    pub fn createExternal(span: []u8, config: *const p.Config) !*Session {
        if (config.size != @sizeOf(p.Config) or config.version != p.abi_version or span.len > config.byte_cap) return error.InvalidConfig;
        const address = @intFromPtr(span.ptr);
        const aligned = (try std.math.add(usize, address, @alignOf(Session) - 1)) & ~(@as(usize, @alignOf(Session)) - 1);
        const prefix = aligned - address;
        if (prefix > span.len or span.len - prefix < @sizeOf(Session)) return error.InvalidConfig;
        const remaining = span[prefix + @sizeOf(Session) ..];
        const region = RegionAllocator.init(remaining) catch return error.InvalidConfig;
        const self: *Session = @ptrFromInt(aligned);
        self.* = .{
            .backing = undefined,
            .budget = undefined,
            .residency = undefined,
            .region = region,
            .external_region_bytes = span.len,
        };
        errdefer self.region.?.deinit();
        self.budget = .{ .backing = self.region.?.allocator(), .cap = @intCast(config.byte_cap), .current = @sizeOf(Session), .peak = @sizeOf(Session) };
        self.residency = try r.Owner.init(self.budget.allocator(), .{
            .qpc_frequency = config.qpc_frequency,
            .window_ms = config.window_ms,
            .bucket_ms = config.bucket_ms,
            .max_pairs = config.max_pairs,
            .max_cells = config.max_cells,
            .max_processes = config.max_processes,
            .max_threads = config.max_threads,
            .max_processors = config.max_processors,
            .max_name_bytes = config.max_name_bytes,
        });
        return self;
    }

    pub fn destroy(self: *Session) void {
        std.debug.assert(self.active_traces == 0);
        if (self.publication) |*arena| arena.deinit();
        self.residency.deinit();
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
    pub fn apply(self: *Session, input: *const p.Input) !void {
        const name: ?[]const u8 = if (input.name_len == 0) null else (input.name orelse return error.InvalidInput)[0..input.name_len];
        const start: ?i64 = if (input.flags & 1 != 0) input.start else null;
        switch (input.kind) {
            1 => try self.residency.seedProcess(input.pid, start, name),
            2 => try self.residency.seedThread(input.pid, input.tid),
            3 => try self.residency.observeProcessStart(input.qpc, input.time_ticks, input.pid, input.unique, start, name),
            4 => try self.residency.observeProcessEnd(input.qpc, input.time_ticks, input.pid, input.unique),
            5 => try self.residency.observeThreadStart(input.qpc, input.time_ticks, input.pid, input.tid),
            6 => try self.residency.observeThreadEnd(input.qpc, input.time_ticks, input.pid, input.tid),
            7 => try self.residency.observeContextSwitch(input.qpc, input.time_ticks, input.processor, input.old_pid, input.old_tid, input.pid, name, input.tid),
            8 => try self.residency.resetAfterEventLoss(),
            9 => try self.residency.resetContinuity(),
            10 => try self.residency.clear(),
            else => return error.InvalidInput,
        }
    }
    pub fn fail(self: *Session, err: anyerror) i32 {
        if (err == error.OutOfMemory)
            self.residency.resetContinuity() catch {};
        const result: Result = switch (err) {
            error.InvalidInput, error.InvalidConfig, error.Overflow => .invalid_argument,
            error.CapacityExceeded => .buffer_too_small,
            error.OutOfMemory => .out_of_memory,
            else => .unavailable,
        };
        self.last_error = @intFromEnum(result);
        return self.last_error;
    }
};
fn session(handle: ?*anyopaque) ?*Session {
    return @ptrCast(@alignCast(handle));
}
fn code(result: Result) i32 {
    return @intFromEnum(result);
}

pub export fn rm_kernel_etw_abi_version() callconv(.c) u32 {
    return p.abi_version;
}
pub export fn rm_kernel_etw_create(config: ?*const p.Config, output: ?*?*anyopaque) callconv(.c) i32 {
    const out = output orelse return code(.invalid_argument);
    out.* = null;
    const cfg = config orelse return code(.invalid_argument);
    if (cfg.size != @sizeOf(p.Config) or cfg.version != p.abi_version) return code(.abi_mismatch);
    // Replay/candidate construction. Production supplies ledger-owned backing
    // through create_in_region; tiny objects must never use page_allocator.
    out.* = Session.create(std.heap.smp_allocator, cfg) catch |err| return if (err == error.OutOfMemory) code(.out_of_memory) else code(.invalid_argument);
    return code(.ok);
}
pub export fn rm_kernel_etw_create_in_region(config: ?*const p.Config, memory: ?[*]u8, length: usize, output: ?*?*anyopaque) callconv(.c) i32 {
    const out = output orelse return code(.invalid_argument);
    out.* = null;
    const cfg = config orelse return code(.invalid_argument);
    if (cfg.size != @sizeOf(p.Config) or cfg.version != p.abi_version) return code(.abi_mismatch);
    const span = (memory orelse return code(.invalid_argument))[0..length];
    out.* = Session.createExternal(span, cfg) catch |err| return if (err == error.OutOfMemory) code(.out_of_memory) else code(.invalid_argument);
    return code(.ok);
}
pub export fn rm_kernel_etw_destroy(handle: ?*anyopaque) callconv(.c) i32 {
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

/// All bytes are native facts. Reserved backing includes the Session and all
/// alignment loss; occupied spans include allocator headers/padding/slack.
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
pub export fn rm_kernel_etw_query_region_usage(handle: ?*anyopaque, output: ?*RegionUsage, size: u32) callconv(.c) i32 {
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
pub export fn rm_kernel_etw_apply(handle: ?*anyopaque, input: ?*const p.Input) callconv(.c) i32 {
    const self = session(handle) orelse return code(.invalid_argument);
    const value = input orelse return code(.invalid_argument);
    if (value.reserved != 0 or value.reserved2 != 0 or value.flags & ~@as(u32, 1) != 0) return code(.invalid_argument);
    self.lock();
    defer self.mutex.unlock();
    self.apply(value) catch |err| return self.fail(err);
    return code(.ok);
}

/// Produces one immutable native publication. Its rows and names are references,
/// valid through release, even while subsequent events update capture state.
/// A second outstanding publication is rejected; destroy requires no active caller.
pub export fn rm_kernel_etw_acquire_residency(handle: ?*anyopaque, processors: ?[*]const i32, count: u32, output: ?*p.View) callconv(.c) i32 {
    const self = session(handle) orelse return code(.invalid_argument);
    const out = output orelse return code(.invalid_argument);
    out.* = .{};
    const ids: []const i32 = if (count == 0) &.{} else (processors orelse return code(.invalid_argument))[0..count];
    self.lock();
    defer self.mutex.unlock();
    if (self.publication != null) return code(.unavailable);
    var arena = std.heap.ArenaAllocator.init(self.budget.allocator());
    const snapshot = self.residency.snapshot(arena.allocator(), ids) catch |err| {
        arena.deinit();
        return self.fail(err);
    };
    const rows = arena.allocator().alloc(p.Row, snapshot.records.len) catch |err| {
        arena.deinit();
        return self.fail(err);
    };
    for (snapshot.records, rows) |row, *outrow| outrow.* = .{
        .process_instance = row.process_instance_id,
        .pid = row.process_id,
        .flags = if (row.process_start_key != null) 1 else 0,
        .start = row.process_start_key orelse 0,
        .thread_instance = row.thread_instance_id,
        .tid = row.thread_id,
        .processor = row.logical_processor_id,
        .duration = row.execution_time_qpc,
        .switches = row.switch_count,
        .name_len = @intCast(row.process_name.len),
        .name = row.process_name.ptr,
    };
    self.publication = arena;
    out.* = .{
        .frequency = snapshot.qpc_frequency,
        .start_qpc = snapshot.window_start_qpc,
        .through_qpc = snapshot.measured_through_qpc,
        .time_ticks = snapshot.measured_through orelse 0,
        .flags = @as(u32, if (snapshot.is_complete) 1 else 0) | @as(u32, if (snapshot.measured_through != null) 2 else 0),
        .count = @intCast(rows.len),
        .rows = rows.ptr,
        .current_bytes = self.budget.current,
        .peak_bytes = self.budget.peak,
    };
    return code(.ok);
}
pub export fn rm_kernel_etw_release_residency(handle: ?*anyopaque) callconv(.c) void {
    const self = session(handle) orelse return;
    self.lock();
    defer self.mutex.unlock();
    if (self.publication) |*arena| arena.deinit();
    self.publication = null;
}
pub export fn rm_kernel_etw_query_usage(handle: ?*anyopaque, output: ?*p.Usage, size: u32) callconv(.c) i32 {
    const self = session(handle) orelse return code(.invalid_argument);
    const out = output orelse return code(.invalid_argument);
    if (size != @sizeOf(p.Usage)) return code(.abi_mismatch);
    self.lock();
    defer self.mutex.unlock();
    out.* = .{ .current_bytes = self.budget.current, .peak_bytes = self.budget.peak, .byte_cap = self.budget.cap, .failed_allocations = self.budget.failures, .last_error = self.last_error, .trace_error = self.trace_error, .events_lost = self.events_lost };
    return code(.ok);
}

pub export fn rm_kernel_etw_query_capacity_failure(handle: ?*anyopaque, output: ?*r.CapacityFailure, size: u32) callconv(.c) i32 {
    const self = session(handle) orelse return code(.invalid_argument);
    const out = output orelse return code(.invalid_argument);
    if (size != @sizeOf(r.CapacityFailure)) return code(.abi_mismatch);
    self.lock();
    defer self.mutex.unlock();
    out.* = self.residency.last_capacity_failure;
    return code(.ok);
}

test "ABI owner and immutable publication release all allocations" {
    const config: p.Config = .{ .qpc_frequency = 1000, .window_ms = 1000, .bucket_ms = 50, .max_pairs = 64, .max_cells = 4096, .max_processes = 64, .max_threads = 128, .max_processors = 8, .max_name_bytes = 4096, .byte_cap = 1024 * 1024 };
    const self = try Session.create(std.testing.allocator, &config);
    defer self.destroy();
    var input: p.Input = .{ .kind = 3, .pid = 1, .unique = 1 };
    try std.testing.expectEqual(@as(i32, 0), rm_kernel_etw_apply(self, &input));
    input = .{ .kind = 2, .pid = 1, .tid = 2 };
    try std.testing.expectEqual(@as(i32, 0), rm_kernel_etw_apply(self, &input));
    input = .{ .kind = 7, .pid = 1, .tid = 2 };
    try std.testing.expectEqual(@as(i32, 0), rm_kernel_etw_apply(self, &input));
    input = .{ .kind = 7, .old_pid = 1, .old_tid = 2, .qpc = 1000, .time_ticks = 1000 };
    try std.testing.expectEqual(@as(i32, 0), rm_kernel_etw_apply(self, &input));
    var view: p.View = .{};
    try std.testing.expectEqual(@as(i32, 0), rm_kernel_etw_acquire_residency(self, &.{0}, 1, &view));
    try std.testing.expectEqual(@as(u32, 1), view.count);
    try std.testing.expectEqual(@as(i64, 1000), view.rows.?[0].duration);
    rm_kernel_etw_release_residency(self);
}

test "external region contains owner allocations and refuses destruction while leased" {
    const capacity = 256 * 1024;
    const memory = try std.testing.allocator.alloc(u8, capacity + 1);
    defer std.testing.allocator.free(memory);
    const config: p.Config = .{ .qpc_frequency = 1000, .window_ms = 100, .bucket_ms = 50, .max_pairs = 64, .max_cells = 4096, .max_processes = 64, .max_threads = 128, .max_processors = 8, .max_name_bytes = 4096, .byte_cap = capacity };
    var handle: ?*anyopaque = null;
    try std.testing.expectEqual(code(.ok), rm_kernel_etw_create_in_region(&config, memory.ptr + 1, capacity, &handle));
    const self = session(handle).?;
    var alive = true;
    defer if (alive) self.destroy();
    try self.residency.seedProcess(1, 10, "p");
    try self.residency.seedThread(1, 2);
    try self.residency.observeContextSwitch(0, 0, 0, 0, 0, 1, null, 2);
    try self.residency.observeContextSwitch(100, 100, 0, 1, 2, 0, null, 0);
    var view: p.View = .{};
    try std.testing.expectEqual(code(.ok), rm_kernel_etw_acquire_residency(handle, &.{0}, 1, &view));
    try std.testing.expectEqual(@as(u32, 1), view.count);
    try std.testing.expectEqual(code(.unavailable), rm_kernel_etw_destroy(handle));
    var usage: RegionUsage = .{};
    try std.testing.expectEqual(code(.ok), rm_kernel_etw_query_region_usage(handle, &usage, @sizeOf(RegionUsage)));
    try std.testing.expectEqual(@as(u64, capacity), usage.reserved_bytes);
    try std.testing.expect(usage.occupied_bytes > usage.requested_bytes and usage.occupied_bytes <= usage.reserved_bytes);
    try std.testing.expect(usage.live_allocations != 0);
    rm_kernel_etw_release_residency(handle);
    self.active_traces = 1;
    const refused = rm_kernel_etw_destroy(handle);
    self.active_traces = 0;
    try std.testing.expectEqual(code(.unavailable), refused);
    try std.testing.expectEqual(code(.ok), rm_kernel_etw_destroy(handle));
    alive = false;
    @memset(memory, 0xaa); // The caller still owns the region after native destroy.
}

test "external region creation validates actual region footprint and minimum size" {
    var memory: [32]u8 = undefined;
    const config: p.Config = .{ .qpc_frequency = 1000, .window_ms = 100, .bucket_ms = 50, .max_pairs = 1, .max_cells = 4, .max_processes = 1, .max_threads = 1, .max_processors = 1, .max_name_bytes = 32, .byte_cap = 4096 };
    var output: ?*anyopaque = null;
    try std.testing.expectEqual(code(.invalid_argument), rm_kernel_etw_create_in_region(&config, &memory, memory.len, &output));
    try std.testing.expectEqual(@as(?*anyopaque, null), output);
    var tiny_cap = config;
    tiny_cap.byte_cap = 1;
    try std.testing.expectEqual(code(.invalid_argument), rm_kernel_etw_create_in_region(&tiny_cap, &memory, memory.len, &output));
}

test "capacity diagnostics retain exact bucket span and lagging processor before reset" {
    const config: p.Config = .{ .qpc_frequency = 1000, .window_ms = 100, .bucket_ms = 50, .max_pairs = 32, .max_cells = 4096, .max_processes = 64, .max_threads = 128, .max_processors = 8, .max_name_bytes = 4096, .byte_cap = 1024 * 1024 };
    const self = try Session.create(std.testing.allocator, &config);
    defer self.destroy();
    try self.residency.seedProcess(10, 100, "p");
    try self.residency.seedThread(10, 20);
    try self.residency.observeContextSwitch(0, 0, 0, 0, 0, 10, null, 20);
    try self.residency.observeContextSwitch(0, 0, 1, 0, 0, 0, null, 0);
    try std.testing.expectError(error.CapacityExceeded, self.residency.observeContextSwitch(500, 500, 0, 10, 20, 0, null, 0));
    var failure: r.CapacityFailure = .{};
    try std.testing.expectEqual(code(.ok), rm_kernel_etw_query_capacity_failure(self, &failure, @sizeOf(r.CapacityFailure)));
    try std.testing.expectEqual(@as(usize, 112), @sizeOf(r.CapacityFailure));
    try std.testing.expectEqual(@intFromEnum(r.CapacityReason.bucket_span), failure.reason);
    try std.testing.expectEqual(@as(i64, 500), failure.event_qpc);
    try std.testing.expectEqual(@as(u64, 10), failure.observed);
    try std.testing.expectEqual(@as(u64, 8), failure.limit);
    try std.testing.expectEqual(@as(i32, 0), failure.logical_processor);
    try std.testing.expectEqual(@as(i32, 1), failure.oldest_processor);
    try std.testing.expectEqual(@as(i64, 0), failure.oldest_boundary_qpc);
    try std.testing.expectEqual(@as(u64, 1), failure.pairs);
    try std.testing.expectEqual(@as(u64, 2), failure.processors);
    try std.testing.expectEqual(@as(usize, 0), self.residency.getDiagnostics().active_pair_count);
}
