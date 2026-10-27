const std = @import("std");
const capture = @import("abi.zig");
const p = @import("protocol.zig");
const native = @import("consumer.zig");
const interrupt_abi = @import("interrupt_abi.zig");
const PathMap = @import("path_mapping.zig").PathMap;
const Result = @import("../../common/result_codes.zig").ResultCode;

// A native-only consumer binding. The session/control owner remains in C#.
// This candidate intentionally does not claim disk/file/network support.
pub const Trace = struct {
    owner: *capture.Session,
    consumer: native.Consumer,
    name: [:0]u16,
    thread_process: std.AutoHashMapUnmanaged(u32, u32) = .empty,
    switch_limit: u64 = 0,
    switches: u64 = 0,
    failure: i32 = 0,
    stopped_at_limit: bool = false,
    interrupts: ?*interrupt_abi.Session = null,
    interrupt_qpc_origin: ?i64 = null,
    interrupt_tick_origin: i64 = 0,
    last_event_qpc: ?i64 = null,
    paths: ?*PathMap = null,

    fn onNotice(context: ?*anyopaque, notice: native.Notice) bool {
        const self: *Trace = @ptrCast(@alignCast(context.?));
        if (self.failure != 0 or self.stopped_at_limit) return false;
        self.owner.lock();
        defer self.owner.mutex.unlock();
        self.accept(notice) catch |err| {
            self.owner.residency.resetContinuity() catch {};
            if (self.interrupts) |target| {
                target.lock();
                defer target.mutex.unlock();
                target.owner.invalidated = true;
                _ = target.fail(err);
            }
            self.failure = self.owner.fail(err);
            return false;
        };
        return !self.stopped_at_limit;
    }
    fn accept(self: *Trace, notice: native.Notice) !void {
        const owner = self.owner;
        switch (notice) {
            .rejected, .invalid_record => return error.InvalidTraceRecord,
            .loss => |loss| {
                owner.events_lost = loss.events_lost;
                try owner.residency.resetAfterEventLoss();
                self.thread_process.clearAndFree(owner.budget.allocator());
                if (self.interrupts) |target| {
                    target.lock();
                    defer target.mutex.unlock();
                    _ = target.owner.refreshLoss(try self.eventTick(self.last_event_qpc), owner.events_lost);
                }
            },
            .event => |event| {
                self.last_event_qpc = @max(self.last_event_qpc orelse event.qpc, event.qpc);
                // Only closed CSwitch boundaries need civil time. Lifecycle
                // timestamps do not affect residency totals or metadata.
                var input: p.Input = .{ .kind = 0, .qpc = event.qpc };
                switch (event.data) {
                    .process => |process| {
                        input.kind = if (process.lifecycle == .end or process.lifecycle == .rundown_end) 4 else 3;
                        input.pid = @bitCast(process.process_id);
                        input.unique = process.unique_key orelse 0;
                        var name = process.image_name;
                        if (std.mem.lastIndexOfScalar(u8, name, '.')) |dot| name = name[0..dot];
                        input.name = name.ptr;
                        input.name_len = @intCast(name.len);
                    },
                    .thread => |thread| {
                        input.pid = @bitCast(thread.process_id);
                        input.tid = @bitCast(thread.thread_id);
                        input.kind = if (thread.lifecycle == .end or thread.lifecycle == .rundown_end) 6 else 5;
                        if (input.kind == 5) {
                            if (!self.thread_process.contains(thread.thread_id) and self.thread_process.count() >= owner.residency.config.max_threads) return error.CapacityExceeded;
                            try self.thread_process.put(owner.budget.allocator(), thread.thread_id, thread.process_id);
                        } else _ = self.thread_process.remove(thread.thread_id);
                    },
                    .cswitch => |cswitch| {
                        input.kind = 7;
                        input.pid = if (cswitch.new_thread_id == 0) 0 else @bitCast(self.thread_process.get(cswitch.new_thread_id) orelse @as(u32, 0xffffffff));
                        input.old_pid = if (cswitch.old_thread_id == 0) 0 else @bitCast(self.thread_process.get(cswitch.old_thread_id) orelse @as(u32, 0xffffffff));
                        input.tid = @bitCast(cswitch.new_thread_id);
                        input.old_tid = @bitCast(cswitch.old_thread_id);
                        input.processor = event.processor;
                        const filetime = try self.consumer.metadata.qpcToFiletime(event.qpc);
                        input.time_ticks = try std.math.add(i64, filetime, 504911232000000000);
                        self.switches += 1;
                        if (self.switch_limit != 0 and self.switches >= self.switch_limit) self.stopped_at_limit = true;
                    },
                    .lost => {
                        try owner.residency.resetAfterEventLoss();
                        self.thread_process.clearAndFree(owner.budget.allocator());
                        if (self.interrupts) |target| {
                            target.lock();
                            defer target.mutex.unlock();
                            target.owner.reset(try self.eventTick(event.qpc), owner.events_lost);
                        }
                        return;
                    },
                    .image, .interrupt => {
                        try self.acceptInterrupt(event);
                        return;
                    },
                }
                try owner.apply(&input);
            },
        }
    }
    // RuntimeRead: Sampled
    fn eventTick(self: *Trace, qpc: ?i64) !i64 {
        if (self.interrupt_qpc_origin) |origin| {
            const elapsed = @divTrunc((@as(i128, qpc orelse origin) - origin) * 1000, self.consumer.metadata.qpc_frequency);
            return std.math.add(i64, self.interrupt_tick_origin, std.math.cast(i64, elapsed) orelse return error.Overflow);
        }
        return @intCast(GetTickCount64());
    }
    fn acceptInterrupt(self: *Trace, event: native.decoder.Event) !void {
        const target = self.interrupts orelse return;
        target.lock();
        defer target.mutex.unlock();
        const tick = try self.eventTick(event.qpc);
        switch (event.data) {
            .image => |image| {
                // The existing interrupt source subscribes to DCStart, Load
                // and Unload only. DCStop is a rundown fact, not an unload.
                if (image.lifecycle == .rundown_end) return;
                if (image.lifecycle == .end) {
                    target.owner.removeImage(image.base, tick);
                    return;
                }
                // Decode little-endian, unaligned ETW text into one bounded
                // owner round arena; ModuleMap retains its own canonical copy.
                var arena = std.heap.ArenaAllocator.init(target.budget.allocator());
                defer arena.deinit();
                const path = try arena.allocator().alloc(u16, image.path_utf16le.len / 2);
                for (path, 0..) |*unit, i| unit.* = std.mem.readInt(u16, image.path_utf16le[i * 2 ..][0..2], .little);
                const canonical = try self.paths.?.map(arena.allocator(), path, target.owner.config.max_path_units);
                target.owner.upsertImage(image.base, image.size, canonical, tick) catch |err| {
                    _ = target.fail(err);
                    return err;
                };
            },
            .interrupt => |interrupt| {
                // Preserve the two relative conversions and their floating
                // subtraction, including the old pre-session clamp.
                const metadata = self.consumer.metadata;
                const anchor = if (metadata.realtime_clock) |sample|
                    sample.midpoint() - @divTrunc(metadata.qpc_frequency, 10)
                else
                    metadata.header_qpc orelse return error.NoClockAnchor;
                const duration = relativeMilliseconds(event.qpc, anchor, metadata.qpc_frequency) -
                    relativeMilliseconds(interrupt.initial_qpc, anchor, metadata.qpc_frequency);
                target.owner.observe(switch (interrupt.kind) {
                    .isr => .isr,
                    .dpc => .dpc,
                    .threaded_dpc => .threaded_dpc,
                    .timer_dpc => .timer_dpc,
                }, duration, interrupt.routine, tick) catch |err| {
                    _ = target.fail(err);
                    return err;
                };
            },
            else => unreachable,
        }
    }
};
fn relativeMilliseconds(qpc: i64, anchor: i64, frequency: i64) f64 {
    return @as(f64, @floatFromInt(@max(qpc, anchor) - anchor)) * 1000.0 / @as(f64, @floatFromInt(frequency));
}
extern "kernel32" fn GetTickCount64() callconv(.winapi) u64;
pub const TraceStatus = extern struct {
    windows_status: u32,
    owner_status: i32,
    records: u64,
    decoded: u64,
    rejected: u64,
    unhandled: u64,
    switches: u64,
    events_lost: u32,
    buffers_lost: u32,
    qpc_frequency: i64,
    processor_count: u32,
    os_buffer_size: u32,
};
pub const TraceClock = extern struct {
    frequency: i64,
    header_qpc: i64,
    qpc_before: i64,
    qpc_after: i64,
    filetime_100ns: i64,
    flags: u32, // bit 0: real-time sample; bit 1: recorded header anchor
    reserved: u32 = 0,
};
fn code(value: Result) i32 {
    return @intFromEnum(value);
}
fn trace(handle: ?*anyopaque) ?*Trace {
    return @ptrCast(@alignCast(handle));
}

// Name storage is copied once at open, not per event; it is owner-budgeted.
// The caller closes the trace after ProcessTrace returns and before destroy(owner).
pub export fn rm_kernel_etw_trace_open(owner_handle: ?*anyopaque, name: ?[*]const u16, length: u32, realtime: u32, output: ?*?*anyopaque) callconv(.c) i32 {
    const out = output orelse return code(.invalid_argument);
    out.* = null;
    const owner: *capture.Session = @ptrCast(@alignCast(owner_handle orelse return code(.invalid_argument)));
    const text = name orelse return code(.invalid_argument);
    if (length == 0 or length > 32767 or realtime > 1 or std.mem.indexOfScalar(u16, text[0..length], 0) != null) return code(.invalid_argument);
    owner.lock();
    defer owner.mutex.unlock();
    const a = owner.budget.allocator();
    const self = a.create(Trace) catch |err| return owner.fail(err);
    const copied = a.dupeZ(u16, text[0..length]) catch |err| {
        a.destroy(self);
        return owner.fail(err);
    };
    self.* = .{ .owner = owner, .name = copied, .consumer = native.Consumer.init(.{ .context = self, .on_notice = Trace.onNotice }) };
    const opened = if (realtime == 1) self.consumer.openRealtime(copied) else self.consumer.openEtl(copied);
    opened catch {
        owner.trace_error = self.consumer.last_windows_error;
        owner.last_error = code(.unavailable);
        if (self.consumer.handle.load(.acquire) != native.invalid_handle) {
            // Failed cleanup still owns an OS handle and callback storage.
            // Return it for explicit close/retry; never discard that ownership.
            self.failure = code(.unavailable);
            owner.active_traces += 1;
            out.* = self;
        } else {
            a.free(copied);
            a.destroy(self);
        }
        return code(.unavailable);
    };
    if (self.consumer.metadata.qpc_frequency != owner.residency.config.qpc_frequency) {
        const closed = self.consumer.close() catch 1;
        if (closed != 0 and closed != 7007 and closed != 6) {
            self.failure = code(.invalid_argument);
            owner.trace_error = closed;
            owner.active_traces += 1;
            out.* = self;
            return code(.unavailable);
        }
        a.free(copied);
        a.destroy(self);
        return code(.invalid_argument);
    }
    owner.active_traces += 1;
    out.* = self;
    return code(.ok);
}
pub export fn rm_kernel_etw_trace_process(handle: ?*anyopaque, switch_limit: u64) callconv(.c) i32 {
    const self = trace(handle) orelse return code(.invalid_argument);
    if (self.failure != 0) return self.failure;
    self.switch_limit = switch_limit;
    const status = self.consumer.process() catch return code(.unavailable);
    self.owner.lock();
    self.owner.trace_error = status;
    self.owner.mutex.unlock();
    if (self.failure != 0) return self.failure;
    return if (status == 0 or (status == 1223 and self.stopped_at_limit)) code(.ok) else code(.unavailable);
}
// The caller retains both owners until trace_close. For ETL replay use the exact
// input's QPC/tick origins; realtime uses the same delivery clock as the old reader.
pub export fn rm_kernel_etw_trace_bind_interrupts(handle: ?*anyopaque, interrupt_handle: ?*anyopaque, qpc_origin: i64, tick_origin: i64, replay_clock: u32) callconv(.c) i32 {
    const self = trace(handle) orelse return code(.invalid_argument);
    if (self.consumer.processing.load(.acquire) or replay_clock > 1) return code(.invalid_argument);
    self.owner.lock();
    defer self.owner.mutex.unlock();
    const next = interrupt_abi.session(interrupt_handle);
    if (next) |target| {
        if (self.paths == null) {
            const a = self.owner.budget.allocator();
            const paths = a.create(PathMap) catch |err| return self.owner.fail(err);
            paths.* = PathMap.initLocal(a, target.owner.config.max_path_units) catch |err| {
                a.destroy(paths);
                return self.owner.fail(err);
            };
            self.paths = paths;
        }
        target.lock();
        target.active_traces += 1;
        target.mutex.unlock();
    }
    if (self.interrupts) |previous| {
        previous.lock();
        previous.active_traces -= 1;
        previous.mutex.unlock();
    }
    self.interrupts = next;
    self.interrupt_qpc_origin = if (replay_clock == 1) qpc_origin else null;
    self.interrupt_tick_origin = tick_origin;
    return code(.ok);
}
pub export fn rm_kernel_etw_trace_request_stop(handle: ?*anyopaque) callconv(.c) void {
    if (trace(handle)) |self| self.consumer.requestStop();
}
// Close the real-time handle to unblock an idle ProcessTrace without freeing
// callback storage. The host then joins its dedicated thread and calls close.
pub export fn rm_kernel_etw_trace_stop(handle: ?*anyopaque) callconv(.c) i32 {
    const self = trace(handle) orelse return code(.invalid_argument);
    self.consumer.requestStop();
    if (!self.consumer.realtime) return code(.ok);
    const status = self.consumer.close() catch return code(.unavailable);
    return if (status == 0 or status == 7007 or status == 6) code(.ok) else code(.unavailable);
}
pub export fn rm_kernel_etw_trace_close(handle: ?*anyopaque) callconv(.c) i32 {
    const self = trace(handle) orelse return code(.invalid_argument);
    if (self.consumer.processing.load(.acquire)) return code(.unavailable);
    const status = self.consumer.close() catch return code(.unavailable);
    if (status != 0 and status != 7007 and status != 6) return code(.unavailable);
    const owner = self.owner;
    owner.lock();
    defer owner.mutex.unlock();
    const a = owner.budget.allocator();
    if (self.interrupts) |target| {
        target.lock();
        target.active_traces -= 1;
        target.mutex.unlock();
    }
    self.thread_process.deinit(a);
    if (self.paths) |paths| {
        paths.deinit();
        a.destroy(paths);
    }
    a.free(self.name);
    owner.active_traces -= 1;
    a.destroy(self);
    return code(.ok);
}
pub export fn rm_kernel_etw_trace_status(handle: ?*anyopaque, output: ?*TraceStatus, size: u32) callconv(.c) i32 {
    const self = trace(handle) orelse return code(.invalid_argument);
    const out = output orelse return code(.invalid_argument);
    if (size != @sizeOf(TraceStatus)) return code(.abi_mismatch);
    if (self.consumer.processing.load(.acquire)) return code(.unavailable);
    const c = self.consumer.counters;
    out.* = .{ .windows_status = self.consumer.last_windows_error, .owner_status = self.failure, .records = c.records, .decoded = c.decoded, .rejected = c.rejected, .unhandled = c.unhandled, .switches = self.switches, .events_lost = c.events_lost, .buffers_lost = c.buffers_lost, .qpc_frequency = self.consumer.metadata.qpc_frequency, .processor_count = self.consumer.metadata.processor_count, .os_buffer_size = self.consumer.metadata.os_buffer_size };
    return code(.ok);
}
pub const PathMappingStatus = extern struct { entries: u32, failed_drive_mask: u32, flags: u32, reserved: u32 = 0 };
pub export fn rm_kernel_etw_trace_query_path_mapping(handle: ?*anyopaque, output: ?*PathMappingStatus, size: u32) callconv(.c) i32 {
    const self = trace(handle) orelse return code(.invalid_argument);
    const out = output orelse return code(.invalid_argument);
    if (size != @sizeOf(PathMappingStatus)) return code(.abi_mismatch);
    if (self.consumer.processing.load(.acquire)) return code(.unavailable);
    out.* = if (self.paths) |paths|
        .{ .entries = @intCast(paths.len), .failed_drive_mask = paths.failed_drive_mask, .flags = @as(u32, if (paths.local) 1 else 0) | @as(u32, if (paths.drive_query_failed) 2 else 0) }
    else
        .{ .entries = 0, .failed_drive_mask = 0, .flags = 0 };
    return code(.ok);
}
pub export fn rm_kernel_etw_trace_clock(handle: ?*anyopaque, output: ?*TraceClock, size: u32) callconv(.c) i32 {
    const self = trace(handle) orelse return code(.invalid_argument);
    const out = output orelse return code(.invalid_argument);
    if (size != @sizeOf(TraceClock)) return code(.abi_mismatch);
    if (self.consumer.processing.load(.acquire)) return code(.unavailable);
    const metadata = self.consumer.metadata;
    const sample = metadata.realtime_clock;
    out.* = .{ .frequency = metadata.qpc_frequency, .header_qpc = metadata.header_qpc orelse 0, .qpc_before = if (sample) |value| value.qpc_before else 0, .qpc_after = if (sample) |value| value.qpc_after else 0, .filetime_100ns = if (sample) |value| value.filetime_100ns else metadata.start_filetime_100ns, .flags = @as(u32, if (sample != null) 1 else 0) | @as(u32, if (metadata.header_qpc != null) 2 else 0) };
    return code(.ok);
}

test "fatal decoder failure invalidates both capture owners" {
    const config: p.Config = .{ .qpc_frequency = 1000, .window_ms = 1000, .bucket_ms = 50, .max_pairs = 8, .max_cells = 512, .max_processes = 8, .max_threads = 16, .max_processors = 2, .max_name_bytes = 1024, .byte_cap = 1024 * 1024 };
    const owner = try capture.Session.create(std.testing.allocator, &config);
    defer owner.destroy();
    try owner.apply(&.{ .kind = 3, .pid = 1, .unique = 1 });
    try owner.apply(&.{ .kind = 5, .pid = 1, .tid = 2 });
    try owner.apply(&.{ .kind = 7, .pid = 1, .tid = 2 });
    try owner.apply(&.{ .kind = 7, .old_pid = 1, .old_tid = 2, .qpc = 1000 });
    var before = try owner.residency.snapshot(std.testing.allocator, &.{0});
    defer before.deinit(std.testing.allocator);
    try std.testing.expect(before.is_complete);
    const icfg: interrupt_abi.Config = .{ .max_modules = 4, .max_path_units = 128, .max_identities = 8, .max_slices = 32, .max_slice_drivers = 32, .byte_cap = 1024 * 1024, .generation = 1, .started_tick_ms = 0, .events_lost = 0 };
    const target = try interrupt_abi.Session.create(std.testing.allocator, &icfg);
    defer target.destroy();
    try target.owner.observe(.dpc, 1, 0, 1000);
    var input: Trace = .{ .owner = owner, .consumer = native.Consumer.init(.{ .context = null, .on_notice = Trace.onNotice }), .name = @constCast(std.unicode.utf8ToUtf16LeStringLiteral("")), .interrupts = target };
    try std.testing.expect(!Trace.onNotice(&input, .{ .invalid_record = .{ .original_header_size = 1, .flags = 0, .payload_length = 8, .missing_payload = true } }));
    var after = try owner.residency.snapshot(std.testing.allocator, &.{0});
    defer after.deinit(std.testing.allocator);
    try std.testing.expect(!after.is_complete);
    try std.testing.expectError(error.Invalidated, target.owner.publish(std.testing.allocator, 20000, 1));
}

test "lost event marker resets interrupt coverage at its actual replay time" {
    const config: p.Config = .{ .qpc_frequency = 1000, .window_ms = 1000, .bucket_ms = 50, .max_pairs = 8, .max_cells = 512, .max_processes = 8, .max_threads = 16, .max_processors = 2, .max_name_bytes = 1024, .byte_cap = 1024 * 1024 };
    const owner = try capture.Session.create(std.testing.allocator, &config);
    defer owner.destroy();
    const icfg: interrupt_abi.Config = .{ .max_modules = 4, .max_path_units = 128, .max_identities = 8, .max_slices = 32, .max_slice_drivers = 32, .byte_cap = 1024 * 1024, .generation = 1, .started_tick_ms = 0, .events_lost = 0 };
    const target = try interrupt_abi.Session.create(std.testing.allocator, &icfg);
    defer target.destroy();
    try target.owner.observe(.isr, 1, 0, 1000);
    var input: Trace = .{ .owner = owner, .consumer = native.Consumer.init(.{ .context = null, .on_notice = Trace.onNotice }), .name = @constCast(std.unicode.utf8ToUtf16LeStringLiteral("")), .interrupts = target, .interrupt_qpc_origin = 0, .interrupt_tick_origin = 0 };
    input.consumer.metadata.qpc_frequency = 1000;
    try input.accept(.{ .event = .{ .qpc = 15000, .processor = 0, .data = .{ .lost = {} } } });
    var snapshot = try target.owner.publish(std.testing.allocator, 20000, 1);
    defer snapshot.deinit();
    try std.testing.expectEqual(@as(i64, 15000), snapshot.started_tick_ms);
    try std.testing.expectEqual(@as(i64, 5000), snapshot.window_ms);
    try std.testing.expectEqual(@as(u64, 0), snapshot.event_count);
    try std.testing.expect(!snapshot.available);
}
