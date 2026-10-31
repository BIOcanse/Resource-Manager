const std = @import("std");
const capture = @import("abi.zig");
const p = @import("protocol.zig");
const native = @import("consumer.zig");
const interrupt_abi = @import("interrupt_abi.zig");
const PathMap = @import("path_mapping.zig").PathMap;
const namespace = @import("namespace.zig");
const Result = @import("../../common/result_codes.zig").ResultCode;

// The session/control owner remains in C#. Optional children are rebound only
// on the consumer flow as the held output set changes.
pub const Hooks = struct {
    context: *anyopaque,
    failed: *const fn (*anyopaque, i32) void,
    creation: *const fn (*anyopaque, u32, i64) ?i64,
    event: *const fn (*anyopaque, native.decoder.Event) anyerror!void,
    raw: *const fn (*anyopaque, native.decoder.RawEvent) anyerror!void,
    loss: *const fn (*anyopaque, i64) void,
    loss_count: ?*const fn (*anyopaque, i64, u64) void = null,
    boundary: *const fn (*anyopaque) anyerror!void,
    thread: *const fn (*anyopaque, u32, i64) ?u32,
    processor: *const fn (*anyopaque, u16) ?i32,
    enabled: *const fn (*anyopaque, bool) bool,
    held: ?*const fn (*anyopaque, bool) bool = null,
    admit: ?*const fn (*anyopaque, native.decoder.Event) anyerror!void = null,
    failure_reason: ?*const fn (*anyopaque, anyerror) anyerror = null,
};
pub const Trace = struct {
    shared_hooks: ?Hooks = null,
    owner: ?*capture.Session = null,
    consumer: native.Consumer,
    name: [:0]u16,
    thread_process: std.AutoHashMapUnmanaged(u32, u32) = .empty,
    switch_limit: u64 = 0,
    switches: u64 = 0,
    failure: i32 = 0,
    failure_reason: ?anyerror = null,
    failure_records: u64 = 0,
    failure_qpc: i64 = 0,
    stopped_at_limit: bool = false,
    interrupts: ?*interrupt_abi.Session = null,
    interrupt_qpc_origin: ?i64 = null,
    interrupt_tick_origin: i64 = 0,
    last_event_qpc: ?i64 = null,
    paths: ?*PathMap = null,
    image_round: ?std.heap.ArenaAllocator = null,
    image_round_target: ?*interrupt_abi.Session = null,

    // SysConfig is delivered as a raw task-GUID record, before any subsequent
    // image conversion. Its strings join the same owner-budgeted map; do not
    // eagerly seed local defaults ahead of recorded namespace evidence.
    pub fn onUnhandled(context: ?*anyopaque, raw: native.decoder.RawEvent) bool {
        const self: *Trace = @ptrCast(@alignCast(context.?));
        if (self.failure != 0 or self.stopped_at_limit) return false;
        if (self.shared_hooks) |hooks| {
            // I/O and parser-state records also advance the shared buffer's
            // publication clock when no decoded core event was delivered.
            self.last_event_qpc = @max(self.last_event_qpc orelse raw.qpc, raw.qpc);
            hooks.raw(hooks.context, raw) catch |err| {
                self.invalidate(err);
                return false;
            };
            return true;
        }
        const paths = self.paths orelse return true;
        const owner = self.owner orelse return false;
        owner.lock();
        defer owner.mutex.unlock();
        self.acceptNamespace(raw, paths) catch |err| {
            self.invalidate(err);
            return false;
        };
        return true;
    }
    fn acceptNamespace(_: *Trace, raw: native.decoder.RawEvent, paths: *PathMap) !void {
        if (try namespace.decode(raw)) |event| try namespace.apply(event, paths);
    }
    pub fn invalidate(self: *Trace, err: anyerror) void {
        if (self.failure != 0) return;
        // std.mem.Allocator reports null as OutOfMemory. A composed owner can
        // retain the backing callback's actual typed cause before cleanup.
        const reason = if (self.shared_hooks) |hooks| (if (hooks.failure_reason) |normalize| normalize(hooks.context, err) else err) else err;
        self.failure_reason = reason;
        self.failure_records = self.consumer.counters.records;
        self.failure_qpc = self.last_event_qpc orelse 0;
        if (self.owner) |owner| owner.residency.resetContinuity() catch {};
        if (self.interrupts) |target| {
            if (self.shared_hooks == null) target.lock();
            defer if (self.shared_hooks == null) target.mutex.unlock();
            target.owner.invalidated = true;
            _ = target.fail(reason);
        }
        self.failure = if (self.owner) |owner| owner.fail(reason) else code(switch (reason) {
            error.InvalidInput, error.InvalidConfig, error.Overflow => .invalid_argument,
            error.CapacityExceeded => .buffer_too_small,
            error.OutOfMemory => .out_of_memory,
            else => .unavailable,
        });
        if (self.shared_hooks) |hooks| hooks.failed(hooks.context, self.failure);
    }

    pub fn onNotice(context: ?*anyopaque, notice: native.Notice) bool {
        const self: *Trace = @ptrCast(@alignCast(context.?));
        if (self.failure != 0 or self.stopped_at_limit) return false;
        if (self.shared_hooks == null) if (self.owner) |owner| owner.lock();
        defer if (self.shared_hooks == null) if (self.owner) |owner| owner.mutex.unlock();
        self.accept(notice) catch |err| {
            self.invalidate(err);
            return false;
        };
        return !self.stopped_at_limit;
    }
    fn accept(self: *Trace, notice: native.Notice) !void {
        switch (notice) {
            .rejected, .invalid_record => return error.InvalidTraceRecord,
            .loss => |loss| {
                if (self.shared_hooks) |hooks| {
                    const tick = try self.eventTick(self.last_event_qpc);
                    if (hooks.loss_count) |lossCount|
                        lossCount(hooks.context, tick, loss.events_lost)
                    else
                        hooks.loss(hooks.context, tick);
                } else if (self.owner) |owner| {
                    owner.events_lost = loss.events_lost;
                    try owner.residency.resetAfterEventLoss();
                    self.thread_process.clearAndFree(owner.budget.allocator());
                    if (self.interrupts) |target| {
                        target.lock();
                        defer target.mutex.unlock();
                        _ = target.owner.refreshLoss(try self.eventTick(self.last_event_qpc), owner.events_lost);
                    }
                }
            },
            .event => |event| {
                self.last_event_qpc = @max(self.last_event_qpc orelse event.qpc, event.qpc);
                if (self.shared_hooks) |hooks| if (hooks.admit) |admit| try admit(hooks.context, event);
                // Only closed CSwitch boundaries need civil time. Lifecycle
                // timestamps do not affect residency totals or metadata.
                var input: p.Input = .{ .kind = 0, .qpc = event.qpc };
                switch (event.data) {
                    .process => |process| {
                        input.kind = if (process.lifecycle == .end or process.lifecycle == .rundown_end) 4 else 3;
                        input.pid = @bitCast(process.process_id);
                        input.unique = process.unique_key orelse 0;
                        if (self.shared_hooks) |hooks| if (input.kind == 3) {
                            if (hooks.creation(hooks.context, process.process_id, event.qpc)) |birth| {
                                input.start = birth;
                                input.flags |= 1;
                            }
                        };
                        var name = process.image_name;
                        if (std.mem.lastIndexOfScalar(u8, name, '.')) |dot| name = name[0..dot];
                        input.name = name.ptr;
                        input.name_len = @intCast(name.len);
                    },
                    .thread => |thread| {
                        input.pid = @bitCast(thread.process_id);
                        input.tid = @bitCast(thread.thread_id);
                        input.kind = if (thread.lifecycle == .end or thread.lifecycle == .rundown_end) 6 else 5;
                        if (self.shared_hooks == null) if (self.owner) |owner| {
                            if (input.kind == 5) {
                                if (!self.thread_process.contains(thread.thread_id) and self.thread_process.count() >= owner.residency.config.max_threads) return error.CapacityExceeded;
                                try self.thread_process.put(owner.budget.allocator(), thread.thread_id, thread.process_id);
                            } else _ = self.thread_process.remove(thread.thread_id);
                        };
                    },
                    .cswitch => |cswitch| {
                        if (self.shared_hooks) |hooks| if (!hooks.enabled(hooks.context, false)) return;
                        input.kind = 7;
                        input.pid = if (cswitch.new_thread_id == 0) 0 else @bitCast(self.lookupThread(cswitch.new_thread_id, event.qpc) orelse @as(u32, 0xffffffff));
                        input.old_pid = if (cswitch.old_thread_id == 0) 0 else @bitCast(self.lookupThread(cswitch.old_thread_id, event.qpc) orelse @as(u32, 0xffffffff));
                        input.tid = @bitCast(cswitch.new_thread_id);
                        input.old_tid = @bitCast(cswitch.old_thread_id);
                        input.processor = if (self.shared_hooks) |hooks| hooks.processor(hooks.context, event.processor) orelse return else event.processor;
                        const filetime = try self.consumer.metadata.qpcToFiletime(event.qpc);
                        input.time_ticks = try std.math.add(i64, filetime, 504911232000000000);
                        self.switches += 1;
                        if (self.switch_limit != 0 and self.switches >= self.switch_limit) self.stopped_at_limit = true;
                    },
                    .lost => {
                        if (self.shared_hooks) |hooks| {
                            const tick = try self.eventTick(self.last_event_qpc);
                            if (hooks.loss_count) |lossCount|
                                lossCount(hooks.context, tick, self.consumer.counters.events_lost)
                            else
                                hooks.loss(hooks.context, tick);
                        } else if (self.owner) |owner| {
                            try owner.residency.resetAfterEventLoss();
                            self.thread_process.clearAndFree(owner.budget.allocator());
                            if (self.interrupts) |target| {
                                target.lock();
                                defer target.mutex.unlock();
                                target.owner.reset(try self.eventTick(event.qpc), owner.events_lost);
                            }
                        }
                        return;
                    },
                    .image, .interrupt => {
                        if (self.shared_hooks) |hooks| try hooks.event(hooks.context, event);
                        try self.acceptInterrupt(event);
                        return;
                    },
                }
                if (self.owner) |owner| {
                    const held = if (self.shared_hooks) |hooks| if (hooks.held) |isHeld| isHeld(hooks.context, false) else true else true;
                    if (held) try owner.apply(&input);
                }
                if (self.shared_hooks) |hooks| try hooks.event(hooks.context, event);
            },
        }
    }
    pub fn onBuffer(context: ?*anyopaque) bool {
        const self: *Trace = @ptrCast(@alignCast(context.?));
        if (self.failure != 0 or self.stopped_at_limit) return false;
        if (self.shared_hooks) |hooks| hooks.boundary(hooks.context) catch |err| {
            self.invalidate(err);
            return false;
        };
        return self.failure == 0;
    }
    fn lookupThread(self: *Trace, tid: u32, qpc: i64) ?u32 {
        if (self.shared_hooks) |hooks| return hooks.thread(hooks.context, tid, qpc);
        return self.thread_process.get(tid);
    }
    // RuntimeRead: Sampled
    pub fn eventTick(self: *Trace, qpc: ?i64) !i64 {
        // Finite input belongs to its recorded Header clock, even when the
        // caller did not supply an explicit replay-clock override. A live
        // machine tick would make replay windows depend on processing speed.
        const origin = self.interrupt_qpc_origin orelse if (!self.consumer.realtime)
            (self.consumer.metadata.header_qpc orelse return error.NoClockAnchor)
        else
            return @intCast(GetTickCount64());
        const elapsed = @divTrunc((@as(i128, qpc orelse origin) - origin) * 1000, self.consumer.metadata.qpc_frequency);
        return std.math.add(i64, self.interrupt_tick_origin, std.math.cast(i64, elapsed) orelse return error.Overflow);
    }
    fn acquireImageRound(self: *Trace, target: *interrupt_abi.Session) !*std.heap.ArenaAllocator {
        if (self.image_round) |*arena| {
            const bound_target = self.image_round_target orelse return error.InvalidTraceRecord;
            if (bound_target != target) return error.InvalidTraceRecord;
            return arena;
        }
        self.image_round = std.heap.ArenaAllocator.init(target.budget.transientAllocator());
        self.image_round_target = target;
        if (self.image_round) |*arena| return arena;
        unreachable;
    }
    fn resetImageRound(self: *Trace, target: *interrupt_abi.Session) void {
        if (self.image_round) |*arena| {
            const path_bytes = std.math.mul(usize, @as(usize, @intCast(target.owner.config.max_path_units)), @sizeOf(u16)) catch std.math.maxInt(usize);
            const working_set = std.math.mul(usize, path_bytes, 8) catch std.math.maxInt(usize);
            const retained_limit = @min(
                std.math.add(usize, working_set, 128 * 1024) catch std.math.maxInt(usize),
                target.budget.cap,
            );
            _ = arena.reset(.{ .retain_with_limit = retained_limit });
        }
    }
    pub fn deinitImageRound(self: *Trace) void {
        if (self.image_round) |*arena| {
            arena.deinit();
            self.image_round = null;
        }
        self.image_round_target = null;
    }
    fn acceptInterrupt(self: *Trace, event: native.decoder.Event) !void {
        if (self.shared_hooks) |hooks| if (hooks.held) |isHeld| if (!isHeld(hooks.context, true)) return;
        const target = self.interrupts orelse return;
        if (self.shared_hooks == null) target.lock();
        defer if (self.shared_hooks == null) target.mutex.unlock();
        const tick = try self.eventTick(event.qpc);
        switch (event.data) {
            .image => |image| {
                // The existing interrupt source subscribes to DCStart, Load
                // and Unload only. DCStop is a rundown fact, not an unload.
                if (image.lifecycle == .rundown_end) return;
                // User ImageLoad records still reach other held consumers, but
                // can neither allocate IRQ ownership nor unload a kernel image.
                if (!@import("interrupts.zig").isKernelAddress(image.base)) return;
                if (image.lifecycle == .end) {
                    target.owner.removeImage(image.base, tick);
                    return;
                }
                const path_units = image.path_utf16le.len / 2;
                const max_path_units: usize = @intCast(target.owner.config.max_path_units);
                if (path_units > max_path_units) return error.CapacityExceeded;
                if (image.path_utf16le.len % 2 != 0) return error.InvalidTraceRecord;
                // Reuse one target-budgeted arena across image rounds. The
                // ModuleMap takes its own canonical copy before this reset.
                const arena = try self.acquireImageRound(target);
                defer self.resetImageRound(target);
                const round = arena.allocator();
                const path = try round.alloc(u16, path_units);
                for (path, 0..) |*unit, i| unit.* = std.mem.readInt(u16, image.path_utf16le[i * 2 ..][0..2], .little);
                const canonical = try self.paths.?.map(round, path, max_path_units);
                target.owner.upsertImage(image.base, image.size, canonical, tick) catch |err| {
                    _ = target.fail(err);
                    return err;
                };
            },
            .interrupt => |interrupt| {
                if (self.shared_hooks) |hooks| if (!hooks.enabled(hooks.context, true)) return;
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

test "finite event ticks require recorded Header and preserve configured origin and explicit override" {
    var input: Trace = .{ .consumer = native.Consumer.init(.{ .context = null, .on_notice = Trace.onNotice }),
        .name = @constCast(std.unicode.utf8ToUtf16LeStringLiteral("")), .interrupt_tick_origin = 1000 };
    input.consumer.metadata.qpc_frequency = 1000;
    try std.testing.expectError(error.NoClockAnchor, input.eventTick(100));
    input.consumer.metadata.header_qpc = 100;
    try std.testing.expectEqual(@as(i64, 1000), try input.eventTick(100));
    try std.testing.expectEqual(@as(i64, 2000), try input.eventTick(1100));
    try std.testing.expectEqual(@as(i64, 1000), try input.eventTick(null));
    // The explicit replay ABI remains authoritative when the caller sets it.
    input.interrupt_qpc_origin = 200;
    input.interrupt_tick_origin = 4500;
    try std.testing.expectEqual(@as(i64, 5500), try input.eventTick(1200));
}
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
    const copied = a.dupeSentinel(u16, text[0..length], 0) catch |err| {
        a.destroy(self);
        return owner.fail(err);
    };
    self.* = .{ .owner = owner, .name = copied, .consumer = native.Consumer.init(.{ .context = self, .on_notice = Trace.onNotice, .on_unhandled = Trace.onUnhandled }) };
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
    const owner = self.owner orelse return code(.invalid_argument);
    if (self.failure != 0) return self.failure;
    self.switch_limit = switch_limit;
    const status = self.consumer.process() catch return code(.unavailable);
    owner.lock();
    owner.trace_error = status;
    owner.mutex.unlock();
    if (self.failure != 0) return self.failure;
    return if (status == 0 or (status == 1223 and self.stopped_at_limit)) code(.ok) else code(.unavailable);
}
// The caller retains both owners until trace_close. For ETL replay use the exact
// input's QPC/tick origins; realtime uses the same delivery clock as the old reader.
pub export fn rm_kernel_etw_trace_bind_interrupts(handle: ?*anyopaque, interrupt_handle: ?*anyopaque, qpc_origin: i64, tick_origin: i64, replay_clock: u32) callconv(.c) i32 {
    const self = trace(handle) orelse return code(.invalid_argument);
    const owner = self.owner orelse return code(.invalid_argument);
    if (self.consumer.processing.load(.acquire) or replay_clock > 1) return code(.invalid_argument);
    owner.lock();
    defer owner.mutex.unlock();
    const next = interrupt_abi.session(interrupt_handle);
    if (next) |target| {
        if (self.paths == null) {
            const a = owner.budget.allocator();
            const paths = a.create(PathMap) catch |err| return owner.fail(err);
            // Only the interrupt equivalence tests bind a standalone trace; the product's shared owner
            // sizes its map from kernel_etw_limits. 512 is the namespace map those replays expect.
            paths.* = PathMap.initDeferredWithLimits(a, target.owner.config.max_path_units, 512) catch |err| {
                a.destroy(paths);
                return owner.fail(err);
            };
            self.paths = paths;
        }
        target.lock();
        target.active_traces += 1;
        target.mutex.unlock();
    }
    if (self.interrupts) |previous| {
        previous.lock();
        const same_target = if (next) |candidate| candidate == previous else false;
        if (!same_target) self.deinitImageRound();
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
    const owner = self.owner orelse return code(.invalid_argument);
    owner.lock();
    defer owner.mutex.unlock();
    const a = owner.budget.allocator();
    if (self.interrupts) |target| {
        target.lock();
        self.deinitImageRound();
        target.active_traces -= 1;
        target.mutex.unlock();
    } else self.deinitImageRound();
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
// flags: bit 0 local defaults loaded, bit 1 local topology query failed,
// bit 2 recorded SysConfig mappings applied. Both sources may be present.
pub const PathMappingStatus = extern struct { entries: u32, failed_drive_mask: u32, flags: u32, reserved: u32 = 0 };
pub export fn rm_kernel_etw_trace_query_path_mapping(handle: ?*anyopaque, output: ?*PathMappingStatus, size: u32) callconv(.c) i32 {
    const self = trace(handle) orelse return code(.invalid_argument);
    const out = output orelse return code(.invalid_argument);
    if (size != @sizeOf(PathMappingStatus)) return code(.abi_mismatch);
    if (self.consumer.processing.load(.acquire)) return code(.unavailable);
    out.* = if (self.paths) |paths|
        .{ .entries = @intCast(paths.count()), .failed_drive_mask = paths.failed_drive_mask, .flags = @as(u32, if (paths.local) 1 else 0) | @as(u32, if (paths.drive_query_failed) 2 else 0) | @as(u32, if (paths.recorded) 4 else 0) }
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
    const processors = [_]i32{0};
    const config: p.Config = .{ .qpc_frequency = 1000, .window_ms = 1000, .max_pairs = 8, .max_processes = 8, .max_threads = 16, .max_processors = 2, .max_name_bytes = 1024, .byte_cap = 1024 * 1024, .processor_count = 1, .processors = &processors };
    const owner = try capture.Session.create(std.testing.allocator, &config);
    defer owner.destroy();
    try owner.apply(&.{ .kind = 3, .pid = 1, .unique = 1 });
    try owner.apply(&.{ .kind = 5, .pid = 1, .tid = 2 });
    try owner.apply(&.{ .kind = 7, .pid = 1, .tid = 2 });
    try owner.apply(&.{ .kind = 7, .old_pid = 1, .old_tid = 2, .qpc = 1000 });
    var before = try owner.residency.snapshot(std.testing.allocator);
    defer before.deinit(std.testing.allocator);
    try std.testing.expect(before.is_complete);
    const icfg: interrupt_abi.Config = .{ .max_modules = 4, .max_path_units = 128, .max_identities = 8, .max_slices = 32, .max_slice_drivers = 32, .bucket_ms = 10000, .bucket_count = 6, .reported_drivers = 16, .byte_cap = 1024 * 1024, .generation = 1, .started_tick_ms = 0, .events_lost = 0 };
    const target = try interrupt_abi.Session.create(std.testing.allocator, &icfg);
    defer target.destroy();
    try target.owner.observe(.dpc, 1, 0, 1000);
    var input: Trace = .{ .owner = owner, .consumer = native.Consumer.init(.{ .context = null, .on_notice = Trace.onNotice }), .name = @constCast(std.unicode.utf8ToUtf16LeStringLiteral("")), .interrupts = target };
    try std.testing.expect(!Trace.onNotice(&input, .{ .invalid_record = .{ .original_header_size = 1, .flags = 0, .payload_length = 8, .missing_payload = true } }));
    var after = try owner.residency.snapshot(std.testing.allocator);
    defer after.deinit(std.testing.allocator);
    try std.testing.expect(!after.is_complete);
    try std.testing.expectError(error.Invalidated, target.owner.publish(std.testing.allocator, 20000, 1));
}

test "lost event marker resets interrupt coverage at its actual replay time" {
    const config: p.Config = .{ .qpc_frequency = 1000, .window_ms = 1000, .max_pairs = 8, .max_processes = 8, .max_threads = 16, .max_processors = 2, .max_name_bytes = 1024, .byte_cap = 1024 * 1024 };
    const owner = try capture.Session.create(std.testing.allocator, &config);
    defer owner.destroy();
    const icfg: interrupt_abi.Config = .{ .max_modules = 4, .max_path_units = 128, .max_identities = 8, .max_slices = 32, .max_slice_drivers = 32, .bucket_ms = 10000, .bucket_count = 6, .reported_drivers = 16, .byte_cap = 1024 * 1024, .generation = 1, .started_tick_ms = 0, .events_lost = 0 };
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

test "recorded namespace is applied by raw consumer before first image path" {
    const cfg: p.Config = .{ .qpc_frequency = 1000, .window_ms = 1000, .max_pairs = 8, .max_processes = 8, .max_threads = 16, .max_processors = 2, .max_name_bytes = 1024, .byte_cap = 1024 * 1024 };
    const owner = try capture.Session.create(std.testing.allocator, &cfg);
    defer owner.destroy();
    const icfg: interrupt_abi.Config = .{ .max_modules = 4, .max_path_units = 128, .max_identities = 8, .max_slices = 32, .max_slice_drivers = 32, .bucket_ms = 10000, .bucket_count = 6, .reported_drivers = 16, .byte_cap = 1024 * 1024, .generation = 1, .started_tick_ms = 0, .events_lost = 0 };
    const target = try interrupt_abi.Session.create(std.testing.allocator, &icfg);
    defer target.destroy();
    var paths = try PathMap.initDeferredWithLimits(owner.budget.allocator(), 128, 8);
    defer paths.deinit();
    var input: Trace = .{ .owner = owner, .consumer = native.Consumer.init(.{ .context = null, .on_notice = Trace.onNotice, .on_unhandled = Trace.onUnhandled }), .name = @constCast(std.unicode.utf8ToUtf16LeStringLiteral("")), .interrupts = target, .paths = &paths, .interrupt_qpc_origin = 0 };
    defer input.deinitImageRound();
    input.consumer.metadata.qpc_frequency = 1000;
    const mapping: []const u16 = std.unicode.utf8ToUtf16LeStringLiteral("\\Device\\Recorded\\\x00Z:\\\x00");
    var raw: native.decoder.RawEvent = .{ .provider = namespace.sysconfig_task_guid, .opcode = 35, .version = 0, .pointer_size = 8, .qpc = 0, .processor = 0, .header_process_id = 0, .header_thread_id = 0, .payload = std.mem.sliceAsBytes(mapping) };
    try std.testing.expect(Trace.onUnhandled(&input, raw));
    try std.testing.expect(!paths.local);
    const image: []const u16 = std.unicode.utf8ToUtf16LeStringLiteral("\\Device\\Recorded\\driver.sys");
    // Drivers load in kernel space; user-mode images are rejected before the module map.
    try input.accept(.{ .event = .{ .qpc = 1, .processor = 0, .data = .{ .image = .{ .lifecycle = .start, .process_id = 0, .base = 0xFFFF_F800_0000_0100, .size = 16, .path_utf16le = std.mem.sliceAsBytes(image) } } } });
    try std.testing.expectEqualSlices(u16, std.unicode.utf8ToUtf16LeStringLiteral("Z:\\driver.sys"), target.owner.modules.resolve(0xFFFF_F800_0000_0101).?.path.?);
    try std.testing.expect(!paths.local);
    // A malformed required namespace record fails the same owner explicitly.
    raw.payload = raw.payload[0 .. raw.payload.len - 2];
    try std.testing.expect(!Trace.onUnhandled(&input, raw));
    try std.testing.expect(input.failure != 0);
    try std.testing.expect(target.owner.invalidated);
}

test "IRQ-only shared trace needs no CPU owner and respects held-output admission" {
    const Fixture = struct {
        irq_held: bool = true,
        admitted: usize = 0,
        events: usize = 0,
        losses: usize = 0,
        legacy_losses: usize = 0,
        last_lost_count: u64 = 0,
        fn cast(ctx: *anyopaque) *@This() { return @ptrCast(@alignCast(ctx)); }
        fn failed(_: *anyopaque, _: i32) void { }
        fn creation(_: *anyopaque, _: u32, _: i64) ?i64 { return null; }
        fn event(ctx: *anyopaque, _: native.decoder.Event) anyerror!void { cast(ctx).events += 1; }
        fn raw(_: *anyopaque, _: native.decoder.RawEvent) anyerror!void { }
        fn loss(ctx: *anyopaque, _: i64) void { cast(ctx).legacy_losses += 1; }
        fn lossCount(ctx: *anyopaque, _: i64, count: u64) void {
            cast(ctx).losses += 1;
            cast(ctx).last_lost_count = count;
        }
        fn boundary(_: *anyopaque) anyerror!void { }
        fn thread(_: *anyopaque, _: u32, _: i64) ?u32 { return null; }
        fn processor(_: *anyopaque, _: u16) ?i32 { return null; }
        fn enabled(ctx: *anyopaque, irq: bool) bool { return irq and cast(ctx).irq_held; }
        fn held(ctx: *anyopaque, irq: bool) bool { return irq and cast(ctx).irq_held; }
        fn admit(ctx: *anyopaque, _: native.decoder.Event) anyerror!void { cast(ctx).admitted += 1; }
    };
    var fixture: Fixture = .{};
    const cfg: interrupt_abi.Config = .{ .max_modules = 4, .max_path_units = 128, .max_identities = 8, .max_slices = 32, .max_slice_drivers = 32, .bucket_ms = 10000, .bucket_count = 6, .reported_drivers = 16, .byte_cap = 1024 * 1024, .generation = 1, .started_tick_ms = 0, .events_lost = 0 };
    const target = try interrupt_abi.Session.create(std.testing.allocator, &cfg);
    defer target.destroy();
    var paths = PathMap.init(std.testing.allocator, 32768, 32);
    defer paths.deinit();
    var input: Trace = .{
        .consumer = native.Consumer.init(.{ .context = null, .on_notice = Trace.onNotice }),
        .name = @constCast(std.unicode.utf8ToUtf16LeStringLiteral("")), .interrupts = target,
        .paths = &paths, .interrupt_qpc_origin = 0,
        .shared_hooks = .{ .context = &fixture, .failed = Fixture.failed, .creation = Fixture.creation,
            .event = Fixture.event, .raw = Fixture.raw, .loss = Fixture.loss, .loss_count = Fixture.lossCount, .boundary = Fixture.boundary,
            .thread = Fixture.thread, .processor = Fixture.processor, .enabled = Fixture.enabled,
            .held = Fixture.held, .admit = Fixture.admit },
    };
    defer input.deinitImageRound();
    input.consumer.metadata.qpc_frequency = 1000;
    input.consumer.metadata.header_qpc = 0;
    const path: []const u16 = std.unicode.utf8ToUtf16LeStringLiteral("driver.sys");
    try input.accept(.{ .event = .{ .qpc = 1, .processor = 0, .data = .{ .image = .{ .lifecycle = .start, .process_id = 0, .base = 0xFFFF_F800_0000_0100, .size = 16, .path_utf16le = std.mem.sliceAsBytes(path) } } } });
    const dpc: native.decoder.Event = .{ .qpc = 2, .processor = 0, .data = .{ .interrupt = .{ .kind = .dpc, .initial_qpc = 1, .routine = 0xFFFF_F800_0000_0101 } } };
    try input.accept(.{ .event = dpc });
    try std.testing.expect(input.owner == null);
    var first = try target.owner.publish(std.testing.allocator, 10000, 1);
    defer first.deinit();
    try std.testing.expectEqual(@as(u64, 1), first.event_count);
    try std.testing.expectEqualSlices(u16, path, first.drivers[0].module.name.?);
    fixture.irq_held = false;
    try input.accept(.{ .event = dpc });
    // The notice carries the count even when a controlled caller has not
    // mutated Consumer.counters. The explicit hook must receive that authority.
    try input.accept(.{ .loss = .{ .source = .buffer_callback, .events_lost = 7, .buffers_lost = 0 } });
    try std.testing.expectEqual(@as(u64, 7), fixture.last_lost_count);
    input.consumer.counters.events_lost = 8;
    try input.accept(.{ .event = .{ .qpc = 15000, .processor = 0, .data = .{ .lost = {} } } });
    try std.testing.expectEqual(@as(usize, 4), fixture.admitted);
    try std.testing.expectEqual(@as(usize, 3), fixture.events);
    try std.testing.expectEqual(@as(usize, 2), fixture.losses);
    try std.testing.expectEqual(@as(u64, 8), fixture.last_lost_count);
    try std.testing.expectEqual(@as(usize, 0), fixture.legacy_losses);
    var after = try target.owner.publish(std.testing.allocator, 10000, 1);
    defer after.deinit();
    // Shared loss belongs to the shared owner hook. The trace must not perform
    // a second child reset, nor admit an interrupt after its output is returned.
    try std.testing.expectEqual(@as(u64, 1), after.event_count);
}
