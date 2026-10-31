const std = @import("std");
const shared = @import("shared_owner.zig");
const trace = @import("trace_abi.zig");
const native = @import("consumer.zig");
const p = @import("protocol.zig");
const irq = @import("interrupt_abi.zig");
const host = @import("host_allocator.zig");
const counters = @import("admission_counters.zig");
const Result = @import("../../common/result_codes.zig").ResultCode;
fn code(v: Result) i32 {
    return @intFromEnum(v);
}
fn owner(handle: ?*anyopaque) ?*shared.Owner {
    return @ptrCast(@alignCast(handle));
}
fn fail(err: anyerror) i32 {
    return code(switch (err) {
        error.OutOfMemory => .out_of_memory,
        error.CapacityExceeded => .buffer_too_small,
        error.InvalidInput, error.InvalidConfig, error.InvalidLease, error.InvalidAllocator => .invalid_argument,
        else => .unavailable,
    });
}
pub export fn rm_kernel_etw_shared_create_in_region(config: ?*const shared.Config, memory: ?[*]u8, length: usize, output: ?*?*anyopaque) callconv(.c) i32 {
    const out = output orelse return code(.invalid_argument);
    out.* = null;
    const cfg = config orelse return code(.invalid_argument);
    if (cfg.size != @sizeOf(shared.Config) or cfg.version != shared.abi_version) return code(.abi_mismatch);
    out.* = shared.Owner.createExternal((memory orelse return code(.invalid_argument))[0..length], cfg.*, @import("io_identity.zig").windows) catch |err| return fail(err);
    return code(.ok);
}
pub export fn rm_kernel_etw_shared_create_with_allocator(config: ?*const shared.Config, allocator: ?*const host.Callbacks, output: ?*?*anyopaque) callconv(.c) i32 {
    const out = output orelse return code(.invalid_argument);
    out.* = null;
    const cfg = config orelse return code(.invalid_argument);
    const callbacks = allocator orelse return code(.invalid_argument);
    if (cfg.size != @sizeOf(shared.Config) or cfg.version != shared.abi_version or callbacks.size != @sizeOf(host.Callbacks) or callbacks.version != 1) return code(.abi_mismatch);
    out.* = shared.Owner.createWithAllocator(cfg.*, callbacks.*, @import("io_identity.zig").windows) catch |err| return fail(err);
    return code(.ok);
}
pub export fn rm_kernel_etw_shared_set_outputs(handle: ?*anyopaque, mask: u32) callconv(.c) i32 {
    const self = owner(handle) orelse return code(.invalid_argument);
    self.setOutputs(mask) catch |err| return fail(err);
    return code(.ok);
}
pub export fn rm_kernel_etw_shared_configure_consumer_counter(handle: ?*anyopaque, config: ?*const counters.Config, size: u32) callconv(.c) i32 {
    const self = owner(handle) orelse return code(.invalid_argument);
    const cfg = config orelse return code(.invalid_argument);
    if (size != @sizeOf(counters.Config)) return code(.abi_mismatch);
    self.configureConsumerCounter(cfg.*) catch |err| return fail(err);
    return code(.ok);
}
pub export fn rm_kernel_etw_shared_retire_consumer_counter(handle: ?*anyopaque, token: u64) callconv(.c) i32 {
    const self = owner(handle) orelse return code(.invalid_argument);
    if (token == 0) return code(.invalid_argument);
    self.retireConsumerCounter(token) catch |err| return fail(err);
    return code(.ok);
}
pub export fn rm_kernel_etw_shared_take_retired_consumer_counter(handle: ?*anyopaque, token: u64, output: ?*counters.Frame, size: u32) callconv(.c) i32 {
    const self = owner(handle) orelse return code(.invalid_argument);
    const out = output orelse return code(.invalid_argument);
    if (size != @sizeOf(counters.Frame)) return code(.abi_mismatch);
    out.* = .{};
    if (token == 0) return code(.invalid_argument);
    out.* = self.takeRetiredConsumerCounter(token) orelse return code(.unavailable);
    return code(.ok);
}
pub export fn rm_kernel_etw_shared_query_consumer_counter(handle: ?*anyopaque, token: u64, kind: u32, reason: u32, output: ?*counters.Pair, size: u32) callconv(.c) i32 {
    const self = owner(handle) orelse return code(.invalid_argument);
    const out = output orelse return code(.invalid_argument);
    if (size != @sizeOf(counters.Pair)) return code(.abi_mismatch);
    out.* = .{};
    if (token == 0 or kind >= counters.kind_count or reason >= counters.reason_count) return code(.invalid_argument);
    out.* = self.consumer_counters.query(token, @enumFromInt(kind), @enumFromInt(reason)) orelse return code(.unavailable);
    return code(.ok);
}
pub export fn rm_kernel_etw_shared_query_event_counter(handle: ?*anyopaque, kind: u32, output: ?*counters.Pair, size: u32) callconv(.c) i32 {
    const self = owner(handle) orelse return code(.invalid_argument);
    const out = output orelse return code(.invalid_argument);
    if (size != @sizeOf(counters.Pair)) return code(.abi_mismatch);
    out.* = .{};
    if (kind >= counters.physical_count) return code(.invalid_argument);
    out.* = self.consumer_counters.queryPhysical(@enumFromInt(kind));
    return code(.ok);
}
pub export fn rm_kernel_etw_shared_query_residency_diagnostics(handle: ?*anyopaque, output: ?*shared.AggregationDiagnostics, size: u32) callconv(.c) i32 {
    const self = owner(handle) orelse return code(.invalid_argument);
    const out = output orelse return code(.invalid_argument);
    if (size != @sizeOf(shared.AggregationDiagnostics)) return code(.abi_mismatch);
    out.* = std.mem.zeroes(shared.AggregationDiagnostics);
    out.* = self.queryResidencyDiagnostics() catch |err| return fail(err);
    return code(.ok);
}
pub export fn rm_kernel_etw_shared_query_interrupt_metadata(handle: ?*anyopaque, output: ?*shared.InterruptMetadata, size: u32) callconv(.c) i32 {
    const self = owner(handle) orelse return code(.invalid_argument);
    const out = output orelse return code(.invalid_argument);
    if (size != @sizeOf(shared.InterruptMetadata)) return code(.abi_mismatch);
    out.* = std.mem.zeroes(shared.InterruptMetadata);
    out.* = self.queryInterruptMetadata() catch |err| return fail(err);
    return code(.ok);
}
pub export fn rm_kernel_etw_shared_query_renderer_stamp(handle: ?*anyopaque, process_id: u32, output: ?*shared.RendererStamp, size: u32) callconv(.c) i32 {
    const self = owner(handle) orelse return code(.invalid_argument);
    const out = output orelse return code(.invalid_argument);
    if (size != @sizeOf(shared.RendererStamp)) return code(.abi_mismatch);
    out.* = std.mem.zeroes(shared.RendererStamp);
    out.* = self.queryRendererStamp(process_id) catch |err| return fail(err);
    return code(.ok);
}
pub export fn rm_kernel_etw_shared_open(handle: ?*anyopaque, name: ?[*]const u16, length: u32, realtime: u32) callconv(.c) i32 {
    const self = owner(handle) orelse return code(.invalid_argument);
    const text = (name orelse return code(.invalid_argument))[0..length];
    if (self.trace != null or length == 0 or length > 32767 or realtime > 1 or std.mem.indexOfScalar(u16, text, 0) != null) return code(.invalid_argument);
    const a = self.controlAllocator();
    const t = a.create(trace.Trace) catch |err| return fail(err);
    const copy = a.dupeZ(u16, text) catch |err| {
        a.destroy(t);
        return fail(err);
    };
    t.* = .{ .owner = if (self.has(1)) self.capture else null, .interrupts = if (self.has(2)) self.interrupts else null, .paths = if (self.paths_active) &self.paths else null, .name = copy, .consumer = native.Consumer.init(.{ .context = t, .on_notice = trace.Trace.onNotice, .on_unhandled = trace.Trace.onUnhandled, .on_buffer = trace.Trace.onBuffer }), .shared_hooks = self.hooks() };
    t.interrupt_tick_origin = self.config.started_tick_ms;
    self.trace = t;
    if (self.has(16)) self.files.mode = if (realtime == 1) .realtime else .recorded;
    if (self.history_active) self.history.realtime = realtime == 1;
    if (self.has(1)) self.readers.thread_diagnostics.recorded = realtime == 0;
    const opened = if (realtime == 1) t.consumer.openRealtime(copy) else t.consumer.openEtl(copy);
    opened catch {
        if (t.consumer.handle.load(.acquire) == native.invalid_handle) {
            a.free(copy);
            a.destroy(t);
            self.trace = null;
        }
        return code(.unavailable);
    };
    if (t.consumer.metadata.qpc_frequency != self.config.residency.qpc_frequency) {
        t.failure = code(.invalid_argument);
        return code(.invalid_argument);
    }
    if (self.has(1)) self.capture.active_traces += 1;
    if (self.has(2)) self.interrupts.active_traces += 1;
    return code(.ok);
}
pub export fn rm_kernel_etw_shared_process(handle: ?*anyopaque) callconv(.c) i32 {
    const self = owner(handle) orelse return code(.invalid_argument);
    const t = self.trace orelse return code(.unavailable);
    if (t.failure != 0) return t.failure;
    const status = t.consumer.process() catch |err| return fail(err);
    if (t.failure != 0) return t.failure;
    self.finish() catch |err| {
        t.invalidate(err);
        return t.failure;
    };
    return if (status == 0 or status == 1223) code(.ok) else code(.unavailable);
}
pub export fn rm_kernel_etw_shared_request_stop(handle: ?*anyopaque) callconv(.c) void {
    if (owner(handle)) |self| if (self.trace) |t| t.consumer.requestStop();
}
/// Host stops the provider first. CloseTrace drains already delivered callbacks
/// and unblocks ProcessTrace; storage remains until the host joins that flow.
pub export fn rm_kernel_etw_shared_stop(handle: ?*anyopaque) callconv(.c) i32 {
    const self = owner(handle) orelse return code(.invalid_argument);
    const t = self.trace orelse return code(.ok);
    const status = t.consumer.close() catch |err| return fail(err);
    return if (status == 0 or status == 6 or status == 7007) code(.ok) else code(.unavailable);
}
pub export fn rm_kernel_etw_shared_close_trace(handle: ?*anyopaque) callconv(.c) i32 {
    const self = owner(handle) orelse return code(.invalid_argument);
    const t = self.trace orelse return code(.ok);
    if (t.consumer.processing.load(.acquire)) return code(.unavailable);
    const status = t.consumer.close() catch |err| return fail(err);
    if (status != 0 and status != 6 and status != 7007) return code(.unavailable);
    const a = self.controlAllocator();
    t.deinitImageRound();
    t.thread_process.deinit(a);
    a.free(t.name);
    // Failed opens retain callback storage but have no active owner binding.
    if (self.has(1) and self.capture.active_traces > 0) self.capture.active_traces -= 1;
    if (self.has(2) and self.interrupts.active_traces > 0) self.interrupts.active_traces -= 1;
    a.destroy(t);
    self.trace = null;
    return code(.ok);
}
pub export fn rm_kernel_etw_shared_destroy(handle: ?*anyopaque) callconv(.c) i32 {
    const self = owner(handle) orelse return code(.invalid_argument);
    self.destroy() catch |err| return fail(err);
    return code(.ok);
}
pub export fn rm_kernel_etw_shared_submit(handle: ?*anyopaque, inputs: ?[*]const p.Input, count: u32) callconv(.c) i32 {
    const self = owner(handle) orelse return code(.invalid_argument);
    self.submit(if (count == 0) &.{} else (inputs orelse return code(.invalid_argument))[0..count]) catch |err| return fail(err);
    return code(.ok);
}
pub export fn rm_kernel_etw_shared_configure(handle: ?*anyopaque, ids: ?[*]const i32, count: u32, flags: u32) callconv(.c) i32 {
    const self = owner(handle) orelse return code(.invalid_argument);
    self.configure((ids orelse return code(.invalid_argument))[0..count], flags) catch |err| return fail(err);
    return code(.ok);
}
pub export fn rm_kernel_etw_shared_request_refresh(handle: ?*anyopaque, tick: i64) callconv(.c) i32 {
    const self = owner(handle) orelse return code(.invalid_argument);
    if (tick < 0) return code(.invalid_argument);
    self.pending_tick.store(tick, .release);
    return code(.ok);
}
pub export fn rm_kernel_etw_shared_configure_processors(handle: ?*anyopaque, ids: ?[*]const i32, count: u32) callconv(.c) i32 {
    const self = owner(handle) orelse return code(.invalid_argument);
    self.configureProcessors((ids orelse return code(.invalid_argument))[0..count]) catch |err| return fail(err);
    return code(.ok);
}
pub export fn rm_kernel_etw_shared_set_enabled(handle: ?*anyopaque, mask: u32, value: u32) callconv(.c) i32 {
    const self = owner(handle) orelse return code(.invalid_argument);
    self.setEnabled(mask, value) catch |err| return fail(err);
    return code(.ok);
}
pub export fn rm_kernel_etw_shared_acquire(handle: ?*anyopaque, output: ?*shared.Lease, size: u32) callconv(.c) i32 {
    const self = owner(handle) orelse return code(.invalid_argument);
    const out = output orelse return code(.invalid_argument);
    if (size != @sizeOf(shared.Lease)) return code(.abi_mismatch);
    out.* = self.acquire() catch |err| return fail(err);
    return code(.ok);
}
/// The token encodes the retained output subset. Only those component fields
/// may be read from the view; typed exports project unavailable fields to zero.
pub export fn rm_kernel_etw_shared_acquire_outputs(handle: ?*anyopaque, mask: u32, output: ?*shared.Lease, size: u32) callconv(.c) i32 {
    const self = owner(handle) orelse return code(.invalid_argument);
    const out = output orelse return code(.invalid_argument);
    if (size != @sizeOf(shared.Lease)) return code(.abi_mismatch);
    out.* = .{ .view = null, .token = 0 };
    out.* = self.acquireOutputs(mask) catch |err| return fail(err);
    return code(.ok);
}
pub export fn rm_kernel_etw_shared_acquire_residency(handle: ?*anyopaque, output: ?*p.View, sequence: ?*u64, events_lost: ?*u64, applied_ticket: ?*u64, lease: ?*u64) callconv(.c) i32 {
    const self = owner(handle) orelse return code(.invalid_argument);
    const out = output orelse return code(.invalid_argument);
    const seq = sequence orelse return code(.invalid_argument);
    const token = lease orelse return code(.invalid_argument);
    const lost = events_lost orelse return code(.invalid_argument);
    const applied = applied_ticket orelse return code(.invalid_argument);
    out.* = .{};
    seq.* = 0;
    lost.* = 0;
    applied.* = 0;
    token.* = 0;
    const acquired = self.acquireResidency() catch |err| return fail(err);
    lost.* = acquired.events_lost;
    applied.* = acquired.applied_ticket;
    out.* = acquired.view;
    seq.* = acquired.sequence;
    token.* = acquired.token;
    return code(.ok);
}
pub export fn rm_kernel_etw_shared_acquire_interrupts(handle: ?*anyopaque, output: ?*irq.View, sequence: ?*u64, lease: ?*u64) callconv(.c) i32 {
    const self = owner(handle) orelse return code(.invalid_argument);
    const out = output orelse return code(.invalid_argument);
    const seq = sequence orelse return code(.invalid_argument);
    const token = lease orelse return code(.invalid_argument);
    const acquired = self.acquireOutputs(2) catch |err| return fail(err);
    out.* = acquired.view.?.interrupts;
    seq.* = acquired.view.?.version;
    token.* = acquired.token;
    return code(.ok);
}
pub export fn rm_kernel_etw_shared_release(handle: ?*anyopaque, token: u64) callconv(.c) i32 {
    const self = owner(handle) orelse return code(.invalid_argument);
    self.release(token) catch |err| return fail(err);
    return code(.ok);
}
pub export fn rm_kernel_etw_shared_status(handle: ?*anyopaque, output: ?*trace.TraceStatus, size: u32) callconv(.c) i32 {
    const self = owner(handle) orelse return code(.invalid_argument);
    if (self.trace == null) return code(.unavailable);
    return trace.rm_kernel_etw_trace_status(self.trace, output, size);
}
pub export fn rm_kernel_etw_shared_events_lost(handle: ?*anyopaque) callconv(.c) u64 {
    const self = owner(handle) orelse return 0;
    return self.events_lost.load(.acquire);
}
pub export fn rm_kernel_etw_shared_query_region_usage(handle: ?*anyopaque, output: ?*@import("abi.zig").RegionUsage, size: u32) callconv(.c) i32 {
    const self = owner(handle) orelse return code(.invalid_argument);
    const out = output orelse return code(.invalid_argument);
    if (size != @sizeOf(@import("abi.zig").RegionUsage)) return code(.abi_mismatch);
    if (self.failure.load(.acquire) != 0) {
        out.* = self.failure_region_usage;
        return code(.ok);
    }
    const acquired = self.acquireOutputs(0) catch |err| return fail(err);
    defer self.release(acquired.token) catch {};
    out.* = acquired.view.?.region_usage;
    return code(.ok);
}
pub export fn rm_kernel_etw_shared_set_replay_clock(handle: ?*anyopaque, qpc: i64, tick: i64) callconv(.c) i32 {
    const self = owner(handle) orelse return code(.invalid_argument);
    const t = self.trace orelse return code(.unavailable);
    if (t.consumer.realtime or t.consumer.processing.load(.acquire) or qpc < 0 or tick < 0) return code(.invalid_argument);
    t.interrupt_qpc_origin = qpc;
    t.interrupt_tick_origin = tick;
    return code(.ok);
}
pub const IoView = extern struct {
    through_tick_ms: i64,
    window_ms: i64,
    flags: u32,
    process_disk_count: u32,
    process_disk: ?[*]const shared.ProcessDisk,
    disk_count: u32,
    known_disk_count: u32,
    disks: ?[*]const shared.Disk,
    known_disks: ?[*]const u32,
    network_count: u32,
    reserved: u32 = 0,
    network: ?[*]const shared.Network,
    network_transition_version: u64,
    reserved2: u64 = 0,
    events_lost: u64,
    disk_transition_version: u64,
};
pub export fn rm_kernel_etw_shared_acquire_io(handle: ?*anyopaque, output: ?*IoView, sequence: ?*u64, lease: ?*u64) callconv(.c) i32 {
    const self = owner(handle) orelse return code(.invalid_argument);
    const out = output orelse return code(.invalid_argument);
    const seq = sequence orelse return code(.invalid_argument);
    const token = lease orelse return code(.invalid_argument);
    const acquired = self.acquireOutputs(12) catch |err| return fail(err);
    const v = acquired.view.?;
    out.* = .{ .through_tick_ms = v.through_tick_ms, .window_ms = v.window_ms, .flags = v.flags, .process_disk_count = v.process_disk_count, .process_disk = v.process_disk, .disk_count = v.disk_count, .known_disk_count = v.known_disk_count, .disks = v.disks, .known_disks = v.known_disks, .network_count = v.network_count, .network = v.network, .network_transition_version = v.network_transition_version, .events_lost = v.events_lost, .disk_transition_version = v.disk_transition_version };
    const outputs = shared.Owner.leasedOutputs(acquired.token);
    if (outputs & 4 == 0) {
        out.flags &= ~@as(u32, 1);
        out.process_disk_count = 0; out.process_disk = null;
        out.disk_count = 0; out.disks = null;
        out.known_disk_count = 0; out.known_disks = null;
        out.disk_transition_version = 0;
    }
    if (outputs & 8 == 0) {
        out.flags &= ~@as(u32, 2);
        out.network_count = 0; out.network = null;
        out.network_transition_version = 0;
    }
    seq.* = v.version;
    token.* = acquired.token;
    return code(.ok);
}
pub const NotificationView = extern struct { events_lost: u64, count: u32, reserved: u32 = 0, records: ?[*]const shared.Notification, first: u64 };
pub export fn rm_kernel_etw_shared_acquire_notifications(handle: ?*anyopaque, output: ?*NotificationView, sequence: ?*u64, lease: ?*u64) callconv(.c) i32 {
    const self = owner(handle) orelse return code(.invalid_argument);
    const out = output orelse return code(.invalid_argument);
    const seq = sequence orelse return code(.invalid_argument);
    const token = lease orelse return code(.invalid_argument);
    const acquired = self.acquireOutputs(16 | 64) catch |err| return fail(err);
    const v = acquired.view.?;
    out.* = .{ .events_lost = v.events_lost, .count = v.notification_count, .records = v.notifications, .first = v.notification_first };
    seq.* = v.version;
    token.* = acquired.token;
    return code(.ok);
}
pub const RendererView = extern struct { epoch: u64, events_lost: u64, count: u32, reserved: u32 = 0, revisions: ?[*]const u64 };
pub export fn rm_kernel_etw_shared_acquire_renderer(handle: ?*anyopaque, output: ?*RendererView, sequence: ?*u64, lease: ?*u64) callconv(.c) i32 {
    const self = owner(handle) orelse return code(.invalid_argument);
    const out = output orelse return code(.invalid_argument);
    const seq = sequence orelse return code(.invalid_argument);
    const token = lease orelse return code(.invalid_argument);
    const acquired = self.acquireOutputs(32) catch |err| return fail(err);
    const v = acquired.view.?;
    out.* = .{ .epoch = v.renderer_epoch, .events_lost = v.events_lost, .count = if (v.renderer_revisions != null) self.config.maximum_renderer_processes else 0, .revisions = v.renderer_revisions };
    seq.* = v.version;
    token.* = acquired.token;
    return code(.ok);
}
pub export fn rm_kernel_etw_shared_set_publication_callback(handle: ?*anyopaque, callback: ?*const fn (?*anyopaque) callconv(.c) void, context: ?*anyopaque) callconv(.c) i32 {
    const self = owner(handle) orelse return code(.invalid_argument);
    if (self.trace) |t| if (t.consumer.processing.load(.acquire)) return code(.unavailable);
    self.publication_callback = callback;
    self.publication_context = context;
    return code(.ok);
}
/// The old 104-byte thread-row interface must never expose a new 112-byte stride.
pub export fn rm_kernel_etw_shared_acquire_thread_diagnostics(_: ?*anyopaque, _: u64, _: ?*@import("thread_diagnostics.zig").RequestView, _: ?*u64, _: ?*u64) callconv(.c) i32 {
    return code(.abi_mismatch);
}
pub export fn rm_kernel_etw_shared_acquire_thread_diagnostics_at(handle: ?*anyopaque, request_id: u64, required_ticket: u64, from: i64, through: i64, output: ?*@import("thread_diagnostics.zig").RequestView, sequence: ?*u64, applied_ticket: ?*u64, lease: ?*u64) callconv(.c) i32 {
    const self = owner(handle) orelse return code(.invalid_argument);
    const out = output orelse return code(.invalid_argument);
    const seq = sequence orelse return code(.invalid_argument);
    const applied = applied_ticket orelse return code(.invalid_argument);
    const token = lease orelse return code(.invalid_argument);
    applied.* = 0;
    token.* = 0;
    seq.* = 0;
    out.* = .{};
    if (required_ticket == 0 or from <= 0 or through < from) return code(.invalid_argument);
    const acquired = self.acquireOutputs(1) catch |err| return fail(err);
    const v = acquired.view.?;
    applied.* = v.command_version;
    if (v.command_version >= required_ticket) if (v.diagnostics.requests) |requests| for (requests[0..v.diagnostics.request_count]) |request| if (request.request_id == request_id and request.sampled_from == from and request.sampled_through == through) {
        out.* = request;
        seq.* = v.version;
        token.* = acquired.token;
        return code(.ok);
    };
    self.release(acquired.token) catch {};
    return code(.unavailable);
}
pub export fn rm_kernel_etw_shared_clock(handle: ?*anyopaque, output: ?*trace.TraceClock, size: u32) callconv(.c) i32 {
    const self = owner(handle) orelse return code(.invalid_argument);
    const t = self.trace orelse return code(.unavailable);
    return trace.rm_kernel_etw_trace_clock(t, output, size);
}
test "diagnostic sampling requires the exact acknowledged ticket window and new row interface" {
    const d = @import("thread_diagnostics.zig");
    const cfg: shared.Config = .{
        .residency = .{ .qpc_frequency = 1000, .window_ms = 1000, .max_pairs = 16, .max_processes = 16, .max_threads = 32, .max_processors = 2, .max_name_bytes = 4096, .byte_cap = 1024 * 1024 },
        .interrupts = .{ .max_modules = 8, .max_path_units = 128, .max_identities = 16, .max_slices = 64, .max_slice_drivers = 128, .byte_cap = 1024 * 1024, .generation = 1, .started_tick_ms = 0, .events_lost = 0 },
        .byte_cap = 4 * 1024 * 1024,
        .disk_byte_cap = 512 * 1024,
        .network_byte_cap = 512 * 1024,
        .file_byte_cap = 256 * 1024,
        .history_byte_cap = 256 * 1024,
        .maximum_disk_pairs = 32,
        .maximum_disks = 16,
        .maximum_cached_processes = 16,
        .maximum_network_events = 64,
        .maximum_network_processes = 16,
        .maximum_file_entries = 16,
        .maximum_file_versions = 64,
        .maximum_history_entries = 64,
        .maximum_path_units = 128,
        .maximum_namespace_entries = 16,
        .maximum_commands = 8,
        .maximum_command_name_bytes = 128,
        .publication_banks = 3,
        .window_ms = 2000,
        .retention_ms = 8000,
        .disk_bucket_ms = 100,
        .started_tick_ms = 0,
        .maximum_pending_events = 128,
        .maximum_pending_path_units = 4096,
        .maximum_renderer_processes = 4096,
    };
    const memory = try std.testing.allocator.alloc(u8, cfg.byte_cap);
    defer std.testing.allocator.free(memory);
    const self = try shared.Owner.createExternal(memory, cfg, @import("io_identity.zig").windows);
    defer self.destroy() catch unreachable;
    self.readers.thread_diagnostics.recorded = true;
    _ = try self.submitTracked(&.{.{ .kind = 13, .unique = 9, .pid = 10, .start = 1, .time_ticks = 100 }});
    try self.drain(100);
    try self.publish(100);
    const ticket = try self.submitTracked(&.{.{ .kind = 19, .unique = 9, .time_ticks = 100, .qpc = 200 }});
    var view: d.RequestView = .{};
    var sequence: u64 = 0;
    var applied: u64 = 0;
    var lease: u64 = 0;
    try std.testing.expectEqual(code(.abi_mismatch), rm_kernel_etw_shared_acquire_thread_diagnostics(self, 9, &view, &sequence, &lease));
    try std.testing.expectEqual(code(.unavailable), rm_kernel_etw_shared_acquire_thread_diagnostics_at(self, 9, ticket, 100, 200, &view, &sequence, &applied, &lease));
    try std.testing.expect(applied < ticket and lease == 0);
    try self.drain(200);
    try self.publish(200);
    try std.testing.expectEqual(code(.ok), rm_kernel_etw_shared_acquire_thread_diagnostics_at(self, 9, ticket, 100, 200, &view, &sequence, &applied, &lease));
    try std.testing.expect(applied >= ticket and lease != 0);
    try std.testing.expectEqual(@as(i32, 50), view.native_error);
    try std.testing.expectEqual(@as(u32, 0), view.thread_count);
    try std.testing.expectEqual(@as(i64, 100), view.sampled_from);
    try std.testing.expectEqual(@as(i64, 200), view.sampled_through);
    try std.testing.expectEqual(code(.ok), rm_kernel_etw_shared_release(self, lease));
    lease = 0;
    try std.testing.expectEqual(code(.unavailable), rm_kernel_etw_shared_acquire_thread_diagnostics_at(self, 9, ticket, 100, 201, &view, &sequence, &applied, &lease));
    try std.testing.expect(applied >= ticket and lease == 0);
    // The request can disappear between submission and the native command drain.
    try self.submit(&.{.{ .kind = 14, .unique = 9 }});
    const removed_ticket = try self.submitTracked(&.{.{ .kind = 19, .unique = 9, .time_ticks = 200, .qpc = 300 }});
    try self.drain(300);
    try self.publish(300);
    try std.testing.expectEqual(@as(i32, 0), self.failure.load(.acquire));
    try std.testing.expectEqual(code(.unavailable), rm_kernel_etw_shared_acquire_thread_diagnostics_at(self, 9, removed_ticket, 200, 300, &view, &sequence, &applied, &lease));
    try std.testing.expect(applied >= removed_ticket and lease == 0);
}

test "shared x64 ABI layouts match the managed boundary declarations" {
    if (@sizeOf(usize) != 8) return error.UnsupportedPlatform;
    try std.testing.expectEqual(@as(usize, 280), @sizeOf(shared.Config));
    try std.testing.expectEqual(@as(usize, 48), @sizeOf(host.Callbacks));
    try std.testing.expectEqual(@as(usize, 48), @sizeOf(counters.Config));
    try std.testing.expectEqual(@as(usize, 16), @sizeOf(counters.Pair));
    try std.testing.expectEqual(@as(usize, 2560), @sizeOf(counters.Frame));
    try std.testing.expectEqual(@as(usize, 192), @sizeOf(shared.AggregationDiagnostics));
    try std.testing.expectEqual(@as(usize, 32), @sizeOf(shared.InterruptMetadata));
    try std.testing.expectEqual(@as(usize, 24), @sizeOf(shared.RendererStamp));
    try std.testing.expectEqual(@as(usize, 96), @sizeOf(shared.Notification));
    try std.testing.expectEqual(@as(usize, 56), @sizeOf(shared.ProcessDisk));
    try std.testing.expectEqual(@as(usize, 40), @sizeOf(shared.Disk));
    try std.testing.expectEqual(@as(usize, 88), @sizeOf(shared.Network));
    try std.testing.expectEqual(@as(usize, 16), @sizeOf(shared.Lease));
    try std.testing.expectEqual(@as(usize, 104), @sizeOf(IoView));
    try std.testing.expectEqual(@as(usize, 32), @sizeOf(NotificationView));
    try std.testing.expectEqual(@as(usize, 32), @sizeOf(RendererView));
    try std.testing.expectEqual(@as(usize, 48), @sizeOf(shared.OwnerUsage));
    try std.testing.expectEqual(@as(usize, 40), @sizeOf(shared.FailureDetail));
    try std.testing.expectEqual(@as(usize, 64), @sizeOf(shared.FailureAllocation));
    try std.testing.expectEqual(@as(usize, 64), @sizeOf(@import("thread_diagnostics.zig").RequestView));
    try std.testing.expectEqual(@as(usize, 112), @sizeOf(@import("thread_diagnostics.zig").ThreadView));
    try std.testing.expectEqual(@as(usize, 80), @offsetOf(shared.Notification, "time_filetime_100ns"));
    try std.testing.expectEqual(@as(usize, 88), @offsetOf(shared.Notification, "parent_pid"));
    try std.testing.expectEqual(@as(usize, 88), @offsetOf(IoView, "events_lost"));
}
/// Kinds: 1 total shared requested bytes (includes all children), 2 residency,
/// 3 IRQ, 4 disk, 5 network, 6 FileIO, 7 parser history. Paths and diagnostic
/// allocations are included in total; diagnostic OS handles are reported by
/// the same publication's diagnostics view, separately from requested bytes.
pub export fn rm_kernel_etw_shared_query_owner_usage(handle: ?*anyopaque, output: ?[*]shared.OwnerUsage, capacity: u32, count: ?*u32) callconv(.c) i32 {
    const self = owner(handle) orelse return code(.invalid_argument);
    const written = count orelse return code(.invalid_argument);
    written.* = 7;
    if (capacity < 7) return code(.buffer_too_small);
    const out = output orelse return code(.invalid_argument);
    if (self.failure.load(.acquire) != 0) {
        @memcpy(out[0..7], &self.failure_usage);
        return code(.ok);
    }
    const acquired = self.acquireOutputs(0) catch |err| return fail(err);
    defer self.release(acquired.token) catch {};
    @memcpy(out[0..7], &acquired.view.?.owner_usage);
    return code(.ok);
}
pub export fn rm_kernel_etw_shared_submit_tracked(handle: ?*anyopaque, inputs: ?[*]const p.Input, count: u32, ticket: ?*u64) callconv(.c) i32 {
    const self = owner(handle) orelse return code(.invalid_argument);
    const out = ticket orelse return code(.invalid_argument);
    if (count == 0) return code(.invalid_argument);
    out.* = self.submitTracked((inputs orelse return code(.invalid_argument))[0..count]) catch |err| return fail(err);
    return code(.ok);
}
pub export fn rm_kernel_etw_shared_reset_io_at(handle: ?*anyopaque, mask: u32, tick: i64, ticket: ?*u64) callconv(.c) i32 {
    const self = owner(handle) orelse return code(.invalid_argument);
    const out = ticket orelse return code(.invalid_argument);
    out.* = self.submitTracked(&.{.{ .kind = 18, .flags = mask, .time_ticks = tick }}) catch |err| return fail(err);
    return code(.ok);
}
/// A cold atomic read distinguishes a healthy owner with no bank from fatal
/// invalidation. The caller retains a read lease for the handle's lifetime.
pub export fn rm_kernel_etw_shared_query_failure(handle: ?*anyopaque) callconv(.c) i32 {
    const self = owner(handle) orelse return code(.invalid_argument);
    const failure = self.failure.load(.acquire);
    if (failure != 0) return failure;
    return if (self.allocation_fault.load(.acquire) != 0) code(.unavailable) else code(.ok);
}

/// Failure detail and usage survive a failed bank and a later trace close.
/// The host retains its normal owner read lease while reading this cold view.
pub export fn rm_kernel_etw_shared_query_failure_detail(handle: ?*anyopaque, output: ?*shared.FailureDetail, size: u32) callconv(.c) i32 {
    const self = owner(handle) orelse return code(.invalid_argument);
    const out = output orelse return code(.invalid_argument);
    if (size != @sizeOf(shared.FailureDetail)) return code(.abi_mismatch);
    const failure = self.failure.load(.acquire);
    out.* = if (failure == 0) .{} else self.failure_detail;
    out.result = failure;
    return code(.ok);
}
pub const DiskConfiguration = extern struct { bucket_ms: i64, maximum_pairs: u32, reserved: u32 = 0, byte_cap: u64 };
/// Immutable construction facts; safe before the first publication and while
/// the single event flow is running. The caller holds its owner read lease.
pub export fn rm_kernel_etw_shared_query_disk_config(handle: ?*anyopaque, output: ?*DiskConfiguration, size: u32) callconv(.c) i32 {
    const self = owner(handle) orelse return code(.invalid_argument);
    const out = output orelse return code(.invalid_argument);
    if (size != @sizeOf(DiskConfiguration)) return code(.abi_mismatch);
    out.* = .{ .bucket_ms = self.config.disk_bucket_ms, .maximum_pairs = self.config.maximum_disk_pairs, .byte_cap = self.config.disk_byte_cap };
    return code(.ok);
}

/// Immutable first raw-allocation failure plus terminal publication stage.
/// Zero fields mean that the owner is healthy or its leaf budget failed before
/// reaching the region allocator. Safe after fatal failure and trace close.
pub export fn rm_kernel_etw_shared_query_failure_allocation(handle: ?*anyopaque, output: ?*shared.FailureAllocation, size: u32) callconv(.c) i32 {
    const self = owner(handle) orelse return code(.invalid_argument);
    const out = output orelse return code(.invalid_argument);
    if (size != @sizeOf(shared.FailureAllocation)) return code(.abi_mismatch);
    out.* = if (self.failure.load(.acquire) == 0) .{} else self.failure_allocation;
    return code(.ok);
}

/// Requests the current closed CPU state. Bare acquire remains passive.
pub export fn rm_kernel_etw_shared_request_residency(handle: ?*anyopaque, ticket: ?*u64, pending: ?*u32) callconv(.c) i32 {
    const self = owner(handle) orelse return code(.invalid_argument);
    const requested = ticket orelse return code(.invalid_argument);
    const waits = pending orelse return code(.invalid_argument);
    requested.* = 0;
    waits.* = 0;
    const result = self.requestResidency() catch |err| return fail(err);
    requested.* = result.ticket;
    waits.* = if (result.pending) 1 else 0;
    return code(.ok);
}

/// Scoped IRQ reads acknowledge reset/configuration before returning any rows.
/// The original unscoped export remains available to raw replay consumers.
pub export fn rm_kernel_etw_shared_acquire_interrupts_at(handle: ?*anyopaque, required_ticket: u64, output: ?*irq.View, sequence: ?*u64, lease: ?*u64) callconv(.c) i32 {
    const self = owner(handle) orelse return code(.invalid_argument);
    const out = output orelse return code(.invalid_argument);
    const seq = sequence orelse return code(.invalid_argument);
    const token = lease orelse return code(.invalid_argument);
    out.* = .{};
    seq.* = 0;
    token.* = 0;
    const acquired = self.acquireInterruptsAt(required_ticket) catch |err| return fail(err);
    out.* = acquired.view.?.interrupts;
    seq.* = acquired.view.?.version;
    token.* = acquired.token;
    return code(.ok);
}

/// Consumer-captured immutable publication progress, safe during processing.
/// The live trace-status guard is intentionally unchanged.
pub export fn rm_kernel_etw_shared_query_published_progress(handle: ?*anyopaque, output: ?*shared.PublishedProgress, size: u32) callconv(.c) i32 {
    const self = owner(handle) orelse return code(.invalid_argument);
    const out = output orelse return code(.invalid_argument);
    if (size != @sizeOf(shared.PublishedProgress)) return code(.abi_mismatch);
    out.* = .{};
    out.* = self.readPublishedProgress() catch |err| return fail(err);
    return code(.ok);
}
