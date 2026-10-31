//! Test/replay adapter for the real native IO owners. No ETW session lifetime.
const std = @import("std");
const disk = @import("disk_io_owner.zig");
const network = @import("network_io_owner.zig");
const identity = @import("io_identity.zig");
const shared = @import("shared_owner.zig");
const shared_abi = @import("shared_abi.zig");
const fileio = @import("fileio.zig");
const decoder = @import("decoder.zig");
const PathMap = @import("path_mapping.zig").PathMap;
const History = @import("thread_process_history.zig").History;
const Budget = @import("budget.zig").Budget;

pub const Identity = extern struct {
    context: ?*anyopaque,
    open: *const fn (?*anyopaque, u32) callconv(.c) ?*anyopaque,
    pid: *const fn (?*anyopaque, *anyopaque) callconv(.c) u32,
    birth: *const fn (?*anyopaque, *anyopaque) callconv(.c) u64,
    close: *const fn (?*anyopaque, *anyopaque) callconv(.c) void,
};
pub const DiskRow = extern struct {
    pid: u32, number: u32, birth: u64, available: u32, read_count: u32, write_count: u32, reserved: u32 = 0,
    read: u64, write: u64, read_rate: f64, write_rate: f64,
};
pub const NetworkRow = extern struct {
    pid: u32, available: u32, birth: u64, receive: u64, send: u64,
    receive_rate: f64, send_rate: f64, count: u32, external_count: u32,
    external_receive: u64, external_send: u64, external_receive_rate: f64, external_send_rate: f64,
};
pub const View = extern struct {
    captured_at_filetime: i64, start_tick: i64, through_tick: i64, window_ms: i64,
    disk_complete: u32, network_complete: u32, process_count: u32, disk_row_count: u32, disk_count: u32, network_count: u32,
    processes: [*]const DiskRow, disk_rows: [*]const DiskRow, disks: [*]const u32, networks: [*]const NetworkRow,
};
const max_pairs = 8192;
const max_network = 65536;
pub const Session = struct {
    backing: std.mem.Allocator,
    callbacks: Identity,
    disk_owner: disk.Owner,
    network_owner: network.Owner,
    started: i64,
    disk_processes: []disk.ProcessRow,
    disk_rows: []disk.DiskRow,
    disks: []u32,
    network_rows: []network.Row,
    published_processes: []DiskRow,
    published_disks: []DiskRow,
    published_networks: []NetworkRow,

    fn source(self: *Session) identity.Source {
        return .{ .context = self, .open_process = open, .read_process_id = pid, .read_creation_filetime = birth, .close_process = close };
    }
    fn cast(context: ?*anyopaque) *Session { return @ptrCast(@alignCast(context.?)); }
    fn open(context: ?*anyopaque, process: u32) ?*anyopaque {
        const self = cast(context); return self.callbacks.open(self.callbacks.context, process);
    }
    fn pid(context: ?*anyopaque, handle: *anyopaque) ?u32 {
        const self = cast(context); const value = self.callbacks.pid(self.callbacks.context, handle); return if (value == 0) null else value;
    }
    fn birth(context: ?*anyopaque, handle: *anyopaque) ?u64 {
        const self = cast(context); const value = self.callbacks.birth(self.callbacks.context, handle); return if (value == 0) null else value;
    }
    fn close(context: ?*anyopaque, handle: *anyopaque) void {
        const self = cast(context); self.callbacks.close(self.callbacks.context, handle);
    }
    fn create(a: std.mem.Allocator, callbacks: Identity, started: i64) !*Session {
        const self = try a.create(Session);
        errdefer a.destroy(self);
        self.callbacks = callbacks;
        self.backing = a;
        self.started = started;
        self.disk_owner = try disk.Owner.init(a, .{ .byte_cap = 64 * 1024 * 1024 }, self.source());
        errdefer self.disk_owner.deinit();
        self.network_owner = try network.Owner.init(a, .{ .byte_cap = 64 * 1024 * 1024 }, self.source());
        errdefer self.network_owner.deinit();
        self.disk_processes = try a.alloc(disk.ProcessRow, max_pairs);
        errdefer a.free(self.disk_processes);
        self.disk_rows = try a.alloc(disk.DiskRow, max_pairs);
        errdefer a.free(self.disk_rows);
        self.disks = try a.alloc(u32, max_pairs);
        errdefer a.free(self.disks);
        self.network_rows = try a.alloc(network.Row, max_network);
        errdefer a.free(self.network_rows);
        self.published_processes = try a.alloc(DiskRow, max_pairs);
        errdefer a.free(self.published_processes);
        self.published_disks = try a.alloc(DiskRow, max_pairs);
        errdefer a.free(self.published_disks);
        self.published_networks = try a.alloc(NetworkRow, max_network);
        // Managed binding rejects arrivals at/before its incomplete start tick.
        self.network_owner.invalidate(started);
        return self;
    }
    fn destroy(self: *Session) void {
        const a = self.backing;
        self.disk_owner.deinit(); self.network_owner.deinit();
        a.free(self.disk_processes); a.free(self.disk_rows); a.free(self.disks); a.free(self.network_rows);
        a.free(self.published_processes); a.free(self.published_disks); a.free(self.published_networks); a.destroy(self);
    }
};
fn session(handle: ?*anyopaque) ?*Session { return @ptrCast(@alignCast(handle)); }
fn result(err: anyerror) i32 { return switch (err) { error.OutOfMemory => 7, error.CapacityExceeded, error.OutputTooSmall => 5, else => 1 }; }
pub export fn rm_kernel_etw_io_replay_create(callbacks: ?*const Identity, started: i64, output: ?*?*anyopaque) callconv(.c) i32 {
    const out = output orelse return 1; out.* = null;
    const cb = callbacks orelse return 1; if (started < 0) return 1;
    out.* = Session.create(std.heap.smp_allocator, cb.*, started) catch |err| return result(err); return 0;
}

test "replay storage and actual owner state release without leaked identity leases" {
    const Fake = struct {
        fn open(_: ?*anyopaque, process: u32) callconv(.c) ?*anyopaque { return @ptrFromInt(process); }
        fn pid(_: ?*anyopaque, handle: *anyopaque) callconv(.c) u32 { return @intCast(@intFromPtr(handle)); }
        fn birth(_: ?*anyopaque, _: *anyopaque) callconv(.c) u64 { return 100; }
        fn close(_: ?*anyopaque, _: *anyopaque) callconv(.c) void {}
    };
    const self = try Session.create(std.testing.allocator, .{ .context = null, .open = Fake.open, .pid = Fake.pid, .birth = Fake.birth, .close = Fake.close }, 1000);
    defer self.destroy();
    try std.testing.expectEqual(@as(i32, 0), rm_kernel_etw_io_replay_disk(self, 1100, 41, 0, 4096, 0, 100));
    var view: View = undefined;
    try std.testing.expectEqual(@as(i32, 0), rm_kernel_etw_io_replay_snapshot(self, 3100, 12345, &view));
    try std.testing.expectEqual(@as(u32, 1), view.process_count);
    try std.testing.expectEqual(@as(u64, 4096), view.processes[0].read);
    try std.testing.expectEqual(@as(i64, 12345), view.captured_at_filetime);
}
pub export fn rm_kernel_etw_io_replay_destroy(handle: ?*anyopaque) callconv(.c) i32 {
    const self = session(handle) orelse return 1; self.destroy(); return 0;
}
pub export fn rm_kernel_etw_io_replay_disk(handle: ?*anyopaque, tick: i64, process: u32, number: i32, bytes: u32, write: u32, filetime: u64) callconv(.c) i32 {
    const self = session(handle) orelse return 1;
    self.disk_owner.observe(.{ .qpc = 0, .thread_id = null, .process_id = process, .disk_number = if (number < 0) null else @intCast(number), .bytes = bytes, .is_write = write != 0, .event_filetime_100ns = if (filetime == 0) null else filetime }, tick) catch |err| return result(err); return 0;
}
pub export fn rm_kernel_etw_io_replay_network(handle: ?*anyopaque, tick: i64, process: u32, bytes: u32, send: u32, udp: u32, address: ?[*]const u8, address_length: u32, filetime: u64) callconv(.c) i32 {
    const self = session(handle) orelse return 1; const addr = address orelse return 1;
    if (address_length != 4 and address_length != 16) return 1;
    self.network_owner.observe(.{ .qpc = 0, .process_id = process, .bytes = bytes, .direction = if (send != 0) .send else .receive, .protocol = if (udp != 0) .udp else .tcp, .remote_address = if (address_length == 4) .{ .ipv4 = addr[0..4].* } else .{ .ipv6 = addr[0..16].* }, .event_filetime_100ns = if (filetime == 0) null else filetime }, tick) catch |err| return result(err); return 0;
}
pub export fn rm_kernel_etw_io_replay_process_changed(handle: ?*anyopaque, process: u32) callconv(.c) i32 {
    const self = session(handle) orelse return 1; self.disk_owner.processIdentityChanged(process); return 0;
}
pub export fn rm_kernel_etw_io_replay_invalidate(handle: ?*anyopaque, tick: i64) callconv(.c) i32 {
    const self = session(handle) orelse return 1; self.disk_owner.invalidate(tick); self.network_owner.invalidate(tick); return 0;
}
pub export fn rm_kernel_etw_io_replay_snapshot(handle: ?*anyopaque, tick: i64, captured: i64, output: ?*View) callconv(.c) i32 {
    const self = session(handle) orelse return 1; const out = output orelse return 1;
    const d = self.disk_owner.snapshot(tick, self.started, self.disk_processes, self.disk_rows, self.disks) catch |err| return result(err);
    const n = self.network_owner.snapshot(tick, self.started, self.network_rows) catch |err| return result(err);
    for (d.processes, 0..) |row, i| self.published_processes[i] = .{ .pid = row.process_id, .number = 0, .birth = row.process_birth_filetime_100ns orelse 0, .available = @intFromBool(row.available), .read_count = row.read_operation_count, .write_count = row.write_operation_count, .read = row.read_bytes, .write = row.write_bytes, .read_rate = row.read_bytes_per_second, .write_rate = row.write_bytes_per_second };
    for (d.disk_rows, 0..) |row, i| self.published_disks[i] = .{ .pid = row.process_id, .number = row.disk_number, .birth = row.process_birth_filetime_100ns orelse 0, .available = @intFromBool(row.available), .read_count = 0, .write_count = 0, .read = row.read_bytes, .write = row.write_bytes, .read_rate = 0, .write_rate = 0 };
    for (n.rows, 0..) |row, i| self.published_networks[i] = .{ .pid = row.process_id, .birth = row.process_birth_filetime_100ns orelse 0, .available = @intFromBool(row.available), .receive = row.receive_bytes, .send = row.send_bytes, .receive_rate = row.receive_bytes_per_second, .send_rate = row.send_bytes_per_second, .count = row.event_count, .external_count = row.external_event_count, .external_receive = row.external_receive_bytes, .external_send = row.external_send_bytes, .external_receive_rate = row.external_receive_bytes_per_second, .external_send_rate = row.external_send_bytes_per_second };
    out.* = .{ .captured_at_filetime = captured, .start_tick = d.start_tick_ms, .through_tick = d.through_tick_ms, .window_ms = d.window_ms, .disk_complete = @intFromBool(d.complete), .network_complete = @intFromBool(n.complete), .process_count = @intCast(d.processes.len), .disk_row_count = @intCast(d.disk_rows.len), .disk_count = @intCast(d.disks.len), .network_count = if (n.complete) @intCast(n.rows.len) else 0, .processes = self.published_processes.ptr, .disk_rows = self.published_disks.ptr, .disks = self.disks.ptr, .networks = self.published_networks.ptr }; return 0;
}

const SharedReplay = struct { adapter: *Session, owner: *shared.Owner, controlled_diagnostics: ?*ControlledDiagnostics = null };
pub export fn rm_kernel_etw_io_replay_shared_create(config: ?*const shared.Config, memory: ?[*]u8, length: usize, callbacks: ?*const Identity, output: ?*?*anyopaque, raw_owner: ?*?*anyopaque) callconv(.c) i32 {
    const cfg = config orelse return 1;
    const cb = callbacks orelse return 1;
    const out = output orelse return 1;
    const raw = raw_owner orelse return 1;
    out.* = null; raw.* = null;
    const bytes = (memory orelse return 1)[0..length];
    const a = std.heap.smp_allocator;
    const adapter = Session.create(a, cb.*, cfg.started_tick_ms) catch |err| return result(err);
    const self = a.create(SharedReplay) catch { adapter.destroy(); return 7; };
    const owner = shared.Owner.createExternal(bytes, cfg.*, adapter.source()) catch |err| { a.destroy(self); adapter.destroy(); return result(err); };
    self.* = .{ .adapter = adapter, .owner = owner }; out.* = self; raw.* = owner; return 0;
}
pub export fn rm_kernel_etw_io_replay_shared_destroy(handle: ?*anyopaque) callconv(.c) i32 {
    const self: *SharedReplay = @ptrCast(@alignCast(handle orelse return 1));
    const status = shared_abi.rm_kernel_etw_shared_destroy(self.owner);
    if (status != 0) return status;
    if (self.controlled_diagnostics) |source| source.destroy();
    self.adapter.destroy(); std.heap.smp_allocator.destroy(self); return 0;
}
/// ProcessTrace has returned: publishing one common EOF boundary mutates no
/// incoming event stream. Identity callbacks remain identical to the C# oracle.
pub export fn rm_kernel_etw_io_replay_shared_snapshot(handle: ?*anyopaque, tick: i64, captured: i64, output: ?*View) callconv(.c) i32 {
    const self: *SharedReplay = @ptrCast(@alignCast(handle orelse return 1));
    const out = output orelse return 1;
    if (self.owner.trace) |t| if (t.consumer.processing.load(.acquire)) return 3;
    self.owner.drain(tick) catch |err| return result(err);
    self.owner.publish(tick) catch |err| return result(err);
    const acquired = self.owner.acquireOutputs(12) catch |err| switch (err) {
        error.NoPublication => {
            // A CPU/IRQ/renderer-only publication has a header but no IO
            // component. Do not request all outputs or touch retired fields.
            const header = self.owner.acquireOutputs(0) catch |header_err| return result(header_err);
            defer self.owner.release(header.token) catch unreachable;
            const a = self.adapter;
            out.* = .{ .captured_at_filetime = captured, .start_tick = tick - header.view.?.window_ms,
                .through_tick = tick, .window_ms = header.view.?.window_ms,
                .disk_complete = 0, .network_complete = 0, .process_count = 0, .disk_row_count = 0,
                .disk_count = 0, .network_count = 0, .processes = a.published_processes.ptr,
                .disk_rows = a.published_disks.ptr, .disks = a.disks.ptr, .networks = a.published_networks.ptr };
            return 0;
        },
        else => return result(err),
    };
    defer self.owner.release(acquired.token) catch unreachable;
    var v = acquired.view.?.*; const a = self.adapter;
    const held = shared.Owner.leasedOutputs(acquired.token);
    if (held & 4 == 0) {
        v.flags &= ~@as(u32, 1); v.process_disk_count = 0; v.process_disk = null;
        v.disk_count = 0; v.disks = null; v.known_disk_count = 0; v.known_disks = null;
    }
    if (held & 8 == 0) { v.flags &= ~@as(u32, 2); v.network_count = 0; v.network = null; }
    if (v.process_disk_count > a.published_processes.len or v.disk_count > a.published_disks.len or v.network_count > a.published_networks.len or v.known_disk_count > a.disks.len) return 5;
    if (v.process_disk) |rows| for (rows[0..v.process_disk_count], 0..) |row, i| { a.published_processes[i] = .{ .pid = row.pid, .number = 0, .birth = row.birth, .available = row.flags & 1, .read_count = row.read_count, .write_count = row.write_count, .read = row.read_bytes, .write = row.write_bytes, .read_rate = row.read_rate, .write_rate = row.write_rate }; };
    if (v.disks) |rows| for (rows[0..v.disk_count], 0..) |row, i| { a.published_disks[i] = .{ .pid = row.pid, .number = row.disk, .birth = row.birth, .available = row.flags & 1, .read_count = 0, .write_count = 0, .read = row.read_bytes, .write = row.write_bytes, .read_rate = 0, .write_rate = 0 }; };
    if (v.network) |rows| for (rows[0..v.network_count], 0..) |row, i| { a.published_networks[i] = .{ .pid = row.pid, .birth = row.birth, .available = row.flags & 1, .receive = row.receive_bytes, .send = row.send_bytes, .receive_rate = row.receive_rate, .send_rate = row.send_rate, .count = row.event_count, .external_count = row.external_event_count, .external_receive = row.external_receive_bytes, .external_send = row.external_send_bytes, .external_receive_rate = row.external_receive_rate, .external_send_rate = row.external_send_rate }; };
    if (v.known_disks) |numbers| @memcpy(a.disks[0..v.known_disk_count], numbers[0..v.known_disk_count]);
    out.* = .{ .captured_at_filetime = captured, .start_tick = tick - v.window_ms, .through_tick = tick, .window_ms = v.window_ms, .disk_complete = v.flags & 1, .network_complete = (v.flags >> 1) & 1, .process_count = v.process_disk_count, .disk_row_count = v.disk_count, .disk_count = v.known_disk_count, .network_count = if (v.flags & 2 != 0) v.network_count else 0, .processes = a.published_processes.ptr, .disk_rows = a.published_disks.ptr, .disks = a.disks.ptr, .networks = a.published_networks.ptr }; return 0;
}
pub export fn rm_kernel_etw_io_replay_shared_disk(handle: ?*anyopaque, tick: i64, process: u32, bytes: u32) callconv(.c) i32 {
    const self: *SharedReplay = @ptrCast(@alignCast(handle orelse return 1));
    self.owner.drain(tick) catch |err| return result(err);
    self.owner.disk.observe(.{ .process_id = process, .thread_id = null, .bytes = bytes, .disk_number = 0, .is_write = false, .qpc = 0, .event_filetime_100ns = 100 }, tick) catch |err| return result(err);
    return 0;
}
pub export fn rm_kernel_etw_io_replay_shared_fail(handle: ?*anyopaque, failure: i32) callconv(.c) i32 {
    const self: *SharedReplay = @ptrCast(@alignCast(handle orelse return 1));
    if (failure <= 0) return 1;
    self.owner.recordFailure(failure); return 0;
}
/// Test-only controlled loss input over the real shared owner; no OS provider.
pub export fn rm_kernel_etw_io_replay_shared_loss(handle: ?*anyopaque, tick: i64, cumulative: u64) callconv(.c) i32 {
    const self: *SharedReplay = @ptrCast(@alignCast(handle orelse return 1));
    if (self.owner.trace) |t| if (t.consumer.processing.load(.acquire)) return 3;
    if (self.owner.failure.load(.acquire) != 0) return result(error.Invalidated);
    self.owner.drain(tick) catch |err| return result(err);
    if (cumulative <= self.owner.events_lost.load(.acquire)) return 0;
    const hooks = self.owner.hooks();
    hooks.loss_count.?(hooks.context, tick, cumulative);
    return 0;
}
pub const IrqCounts = extern struct {
    modules: u32, module_cap: u32, identities: u32, identity_cap: u32,
    slices: u32, slice_cap: u32, cells: u32, cell_cap: u32,
    file_entries: u32, file_entry_cap: u32, file_versions: u32, file_version_cap: u32, file_names: u32,
    pending_events: u32, pending_event_cap: u32, pending_paths: u32, pending_path_cap: u32,
    history_starts: u32, history_end_keys: u32, history_ends: u32, history_cap: u32,
    namespace_entries: u32, namespace_cap: u32, path_units_cap: u32,
};
pub export fn rm_kernel_etw_io_replay_shared_irq_counts(handle: ?*anyopaque, output: ?*IrqCounts) callconv(.c) i32 {
    const self: *SharedReplay = @ptrCast(@alignCast(handle orelse return 1));
    const out = output orelse return 1;
    const owner = self.owner;
    // Each part exists only while its output (or user) is on: setOutputs destroys the
    // interrupt session and deinitializes files, history and paths when they are off.
    out.* = std.mem.zeroes(IrqCounts);
    if (owner.has(2)) {
        const irq_owner = &owner.interrupts.owner;
        var identities: u32 = 0;
        for (irq_owner.identities) |value| if (value.path != null) { identities += 1; };
        out.modules = @intCast(irq_owner.modules.len); out.module_cap = @intCast(irq_owner.modules.ranges.len); out.identities = identities; out.identity_cap = @intCast(irq_owner.identities.len);
        out.slices = @intCast(irq_owner.slice_len); out.slice_cap = @intCast(irq_owner.slices.len); out.cells = @intCast(irq_owner.cell_len); out.cell_cap = @intCast(irq_owner.cells.len);
    }
    if (owner.has(16)) {
        out.file_entries = @intCast(owner.files.entryCount()); out.file_entry_cap = @intCast(owner.files.limits.max_entries); out.file_versions = @intCast(owner.files.version_count);
        out.file_version_cap = @intCast(owner.files.limits.max_versions); out.file_names = @intCast(owner.files.names.count());
    }
    out.pending_events = @intCast(owner.pending_event_count); out.pending_event_cap = @intCast(owner.pending_events.len);
    out.pending_paths = @intCast(owner.pending_path_count); out.pending_path_cap = @intCast(owner.pending_paths.len);
    if (owner.history_active) {
        out.history_starts = @intCast(owner.history.starts.count()); out.history_end_keys = @intCast(owner.history.ends.count()); out.history_ends = @intCast(owner.history.end_count); out.history_cap = @intCast(owner.history.max_entries);
    }
    if (owner.paths_active) {
        out.namespace_entries = @intCast(owner.paths.count()); out.namespace_cap = @intCast(owner.paths.max_entries); out.path_units_cap = @intCast(owner.paths.max_path_units);
    }
    return 0;
}

pub const FileView = extern struct {
    kind: u32 = 0, opcode: u32 = 0, pid: u32 = 0, tid: u32 = 0, qpc: i64 = 0,
    file_key: u64 = 0, file_object: u64 = 0, irp: u64 = 0, offset: i64 = 0, extra_info: u64 = 0,
    io_size: u32 = 0, io_flags: u32 = 0, create_options: u32 = 0, create_disposition: u32 = 0,
    file_attributes: u32 = 0, share_access: u32 = 0, info_class: u32 = 0, name_length: u32 = 0,
    name: ?[*]const u16 = null,
};
const FileReplay = struct {
    backing: std.mem.Allocator,
    budget: Budget,
    files: fileio.FileIoState,
    paths: PathMap,
    history: History,
    round: std.heap.ArenaAllocator,

    fn create(a: std.mem.Allocator) !*FileReplay {
        const self = try a.create(FileReplay); errdefer a.destroy(self);
        self.backing = a;
        self.budget = .{ .backing = a, .cap = 256 * 1024 * 1024, .current = @sizeOf(FileReplay), .peak = @sizeOf(FileReplay) };
        // The finite input has ~one million Create records whose recorded
        // lifetimes must remain queryable; the byte cap remains unchanged.
        self.files = try fileio.FileIoState.init(self.budget.allocator(), .{ .byte_cap = 128 * 1024 * 1024, .max_entries = 131072, .max_versions = 2097152 }, .recorded);
        errdefer self.files.deinit();
        self.paths = try PathMap.initDeferredWithLimits(self.budget.allocator(), 32768, 512);
        errdefer self.paths.deinit();
        self.history = try History.init(self.budget.allocator(), 524288, 64 * 1024 * 1024, false);
        self.round = std.heap.ArenaAllocator.init(self.budget.allocator());
        return self;
    }
    fn destroy(self: *FileReplay) void {
        const a = self.backing;
        self.round.deinit(); self.history.deinit(); self.paths.deinit(); self.files.deinit();
        std.debug.assert(self.budget.current == @sizeOf(FileReplay)); a.destroy(self);
    }
};
pub export fn rm_kernel_etw_io_replay_file_create(output: ?*?*anyopaque) callconv(.c) i32 {
    const out = output orelse return 1; out.* = null;
    out.* = FileReplay.create(std.heap.smp_allocator) catch |err| return result(err); return 0;
}
pub export fn rm_kernel_etw_io_replay_file_destroy(handle: ?*anyopaque) callconv(.c) i32 {
    const self: *FileReplay = @ptrCast(@alignCast(handle orelse return 1)); self.destroy(); return 0;
}
pub const FileUsage = extern struct {
    current: u64, peak: u64, cap: u64, entries: u64, versions: u64, names: u64,
};
/// Cold replay diagnostics after processing, before releasing the real owner.
pub export fn rm_kernel_etw_io_replay_file_usage(handle: ?*anyopaque, output: ?*FileUsage) callconv(.c) i32 {
    const self: *FileReplay = @ptrCast(@alignCast(handle orelse return 1));
    const out = output orelse return 1;
    out.* = .{ .current = @intCast(self.files.budget.current), .peak = @intCast(self.files.budget.peak), .cap = @intCast(self.files.budget.cap), .entries = @intCast(self.files.entryCount()), .versions = @intCast(self.files.version_count), .names = @intCast(self.files.names.count()) };
    return 0;
}
/// One raw record. PID/TID are the original EVENT_HEADER values, never the
/// managed parser's already-resolved properties. Result text lasts one call.
pub export fn rm_kernel_etw_io_replay_file_observe(handle: ?*anyopaque, provider: decoder.Guid, opcode: u8, version: u8, pointer_size: u8, qpc: i64, process: u32, thread: u32, payload: ?[*]const u8, payload_length: u32, output: ?*FileView) callconv(.c) i32 {
    const self: *FileReplay = @ptrCast(@alignCast(handle orelse return 1));
    const out = output orelse return 1; out.* = .{ .opcode = opcode, .qpc = qpc };
    const raw: decoder.RawEvent = .{ .provider = provider, .opcode = opcode, .version = version, .pointer_size = pointer_size, .qpc = qpc, .processor = 0, .header_process_id = process, .header_thread_id = thread, .payload = if (payload_length == 0) &.{} else (payload orelse return 1)[0..payload_length] };
    _ = self.round.reset(.free_all);
    if (provider.eql(decoder.thread_provider)) {
        if (decoder.decode(raw) catch |err| return result(err)) |event| {
            switch (event.data) {
                .thread => |v| {
                    if (v.lifecycle == .start or v.lifecycle == .rundown_start) {
                        self.history.start(v.thread_id, v.process_id) catch |err| return result(err);
                    } else {
                        self.history.end(v.thread_id, v.process_id, qpc) catch |err| return result(err);
                    }
                },
                else => {},
            }
        }
    }
    tryNamespace(raw, &self.paths) catch |err| return result(err);
    if (self.files.observe(raw, &self.paths, &self.history, self.round.allocator()) catch |err| return result(err)) |event| {
        var name: ?[]const u16 = null;
        switch (event) {
            .name => |v| { out.kind = 1; out.file_key = v.file_key; name = v.file_name; },
            .create => |v| { out.kind = 2; out.pid = v.process_id; out.tid = v.thread_id; out.file_object = v.file_object; out.irp = v.irp; out.create_options = v.create_options; out.create_disposition = v.create_disposition; out.file_attributes = v.file_attributes; out.share_access = v.share_access; name = v.file_name; },
            .read_write => |v| { out.kind = if (v.kind == .read) 3 else 4; out.pid = v.process_id; out.tid = v.thread_id; out.file_key = v.file_key; out.file_object = v.file_object; out.irp = v.irp; out.offset = v.offset; out.io_size = v.io_size; out.io_flags = v.io_flags; name = v.file_name; },
            .info => |v| { out.kind = switch (v.kind) { .set_info => 5, .delete => 6, .rename => 7 }; out.pid = v.process_id; out.tid = v.thread_id; out.file_key = v.file_key; out.file_object = v.file_object; out.irp = v.irp; out.extra_info = v.extra_info; out.info_class = v.info_class; name = v.file_name; },
        }
        if (name) |text| { out.name = text.ptr; out.name_length = @intCast(text.len); }
    }
    if (provider.eql(fileio.file_io_task_guid) and (opcode == 0 or opcode == 36)) {
        out.kind = 8;
        out.file_key = fileio.pointer(raw, 0) catch |err| return result(err);
        if (self.files.lookupName(out.file_key, qpc)) |text| { out.name = text.ptr; out.name_length = @intCast(text.len); }
    }
    if (provider.eql(fileio.file_io_task_guid) and (opcode == 65 or opcode == 66)) {
        const fields = fileio.decodeSimple(raw) catch |err| return result(err);
        out.kind = 9; out.file_key = fields.file_key; out.file_object = fields.file_object;
        out.irp = fileio.pointer(raw, 0) catch |err| return result(err);
        out.tid = fileio.resolveThreadId(raw, fields.thread_id_offset) catch |err| return result(err);
        out.pid = fileio.resolveProcessId(raw, out.tid, &self.history);
        if (self.files.resolveName(fields.file_key, fields.file_object, qpc)) |text| { out.name = text.ptr; out.name_length = @intCast(text.len); }
    }
    return 0;
}
fn tryNamespace(raw: decoder.RawEvent, paths: *PathMap) !void {
    if (try @import("namespace.zig").decode(raw)) |event| try @import("namespace.zig").apply(event, paths);
}
test "FileIO replay state releases bounded names paths and history" {
    const replay = try FileReplay.create(std.testing.allocator); defer replay.destroy();
}
// DRAFT: append to io_replay_abi.zig after gates 1-3 pass. These exports are
// test/replay adapters, never production dispatch or a managed event path.
// Calls are serialized by NativeKernelEtwTestResource on its explicit cold flow.
fn controlledShared(handle: ?*anyopaque, tick: i64) !*SharedReplay {
    const self: *SharedReplay = @ptrCast(@alignCast(handle orelse return error.InvalidInput));
    if (self.owner.trace) |t| if (t.consumer.processing.load(.acquire)) return error.Processing;
    if (self.owner.failure.load(.acquire) != 0) return error.Invalidated;
    try self.owner.drain(tick);
    return self;
}
pub export fn rm_kernel_etw_test_shared_disk(handle: ?*anyopaque, tick: i64, process: u32, number: i32, bytes: u32, write: u32, filetime: u64) callconv(.c) i32 {
    if (write > 1) return 1;
    const self = controlledShared(handle, tick) catch |err| return result(err);
    self.owner.disk.observe(.{ .qpc = 0, .thread_id = null, .process_id = process,
        .disk_number = if (number < 0) null else @intCast(number), .bytes = bytes,
        .is_write = write != 0, .event_filetime_100ns = if (filetime == 0) null else filetime }, tick) catch |err| return result(err);
    return 0;
}
pub export fn rm_kernel_etw_test_shared_network(handle: ?*anyopaque, tick: i64, process: u32, bytes: u32, send: u32, udp: u32, address: ?[*]const u8, address_length: u32, filetime: u64) callconv(.c) i32 {
    if (send > 1 or udp > 1 or (address_length != 4 and address_length != 16)) return 1;
    const addr = address orelse return 1;
    const self = controlledShared(handle, tick) catch |err| return result(err);
    if (self.owner.active_outputs & 8 == 0) return 0;
    self.owner.network.observe(.{ .qpc = 0, .process_id = process, .bytes = bytes,
        .direction = if (send != 0) .send else .receive, .protocol = if (udp != 0) .udp else .tcp,
        .remote_address = if (address_length == 4) .{ .ipv4 = addr[0..4].* } else .{ .ipv6 = addr[0..16].* },
        .event_filetime_100ns = if (filetime == 0) null else filetime }, tick) catch |err| return result(err);
    return 0;
}
pub export fn rm_kernel_etw_test_shared_image(handle: ?*anyopaque, tick: i64, base: u64, length: u64, path: ?[*]const u16, path_length: u32, unload: u32) callconv(.c) i32 {
    if (unload > 1 or (path_length > 0 and path == null)) return 1;
    const self = controlledShared(handle, tick) catch |err| return result(err);
    if (self.owner.active_outputs & 2 == 0 or !@import("interrupts.zig").isKernelAddress(base)) return 0;
    if (unload != 0) self.owner.interrupts.owner.removeImage(base, tick) else
        self.owner.interrupts.owner.upsertImage(base, length, if (path) |text| text[0..path_length] else null, tick) catch |err| return result(err);
    return 0;
}
pub export fn rm_kernel_etw_test_shared_interrupt(handle: ?*anyopaque, tick: i64, kind: u32, duration_ms: f64, routine: u64) callconv(.c) i32 {
    if (kind < 1 or kind > 4) return 1;
    const self = controlledShared(handle, tick) catch |err| return result(err);
    if (self.owner.active_outputs & 2 != 0)
        self.owner.interrupts.owner.observe(@enumFromInt(kind), duration_ms, routine, tick) catch |err| return result(err);
    return 0;
}
pub export fn rm_kernel_etw_test_shared_loss(handle: ?*anyopaque, tick: i64, cumulative: u64) callconv(.c) i32 {
    const self = controlledShared(handle, tick) catch |err| return result(err);
    if (cumulative <= self.owner.events_lost.load(.acquire)) return 0;
    const hooks = self.owner.hooks();
    hooks.loss_count.?(hooks.context, tick, cumulative);
    return 0;
}
pub export fn rm_kernel_etw_test_shared_thread(handle: ?*anyopaque, tick: i64, input: ?*const @import("protocol.zig").Input) callconv(.c) i32 {
    const value = input orelse return 1;
    if ((value.kind != 5 and value.kind != 6) or value.pid <= 0 or value.tid <= 0 or value.time_ticks < 504911232000000000) return 1;
    const self = controlledShared(handle, tick) catch |err| return result(err);
    self.owner.readers.apply(value) catch |err| return result(err);
    const ended = value.kind == 6;
    const tid: u32 = @intCast(value.tid);
    const pid: u32 = @intCast(value.pid);
    if (ended) {
        self.owner.history.end(tid, pid, value.qpc) catch |err| return result(err);
    } else {
        self.owner.history.start(tid, pid) catch |err| return result(err);
    }
    // Same shared lifecycle hook, with civil time supplied explicitly by the
    // controlled input instead of a live/recorded Consumer metadata header.
    self.owner.readers.observeThread(pid, tid, ended, value.time_ticks - 504911232000000000) catch |err| return result(err);
    return 0;
}
pub export fn rm_kernel_etw_test_shared_process(handle: ?*anyopaque, tick: i64, input: ?*const @import("protocol.zig").Input) callconv(.c) i32 {
    const value = input orelse return 1;
    if ((value.kind != 3 and value.kind != 4) or value.pid <= 0 or (value.name_len > 0 and value.name == null)) return 1;
    const self = controlledShared(handle, tick) catch |err| return result(err);
    self.owner.readers.apply(value) catch |err| return result(err);
    const hooks = self.owner.hooks();
    hooks.event(hooks.context, .{ .qpc = value.qpc, .processor = 0, .data = .{ .process = .{
        .lifecycle = if (value.kind == 3) .start else .end, .process_id = @intCast(value.pid),
        .parent_id = 0, .unique_key = if (value.unique == 0) null else value.unique,
        .session_id = null, .exit_status = null,
        .image_name = if (value.name) |name| name[0..value.name_len] else &.{}, .command_line_utf16le = &.{}
    } } }) catch |err| return result(err);
    return 0;
}
pub export fn rm_kernel_etw_test_shared_cswitch(handle: ?*anyopaque, tick: i64, input: ?*const @import("protocol.zig").Input, resolve_threads: u32) callconv(.c) i32 {
    var value = (input orelse return 1).*;
    if (value.kind != 7 or value.processor < 0 or value.processor > std.math.maxInt(u16) or resolve_threads > 1) return 1;
    const self = controlledShared(handle, tick) catch |err| return result(err);
    const hooks = self.owner.hooks();
    if (!hooks.enabled(hooks.context, false)) return 0;
    const raw_index: u16 = @intCast(value.processor);
    value.processor = hooks.processor(hooks.context, raw_index) orelse return 0;
    if (resolve_threads != 0) {
        const old: u32 = @bitCast(value.old_tid);
        const next: u32 = @bitCast(value.tid);
        value.old_pid = if (old == 0) 0 else @bitCast(hooks.thread(hooks.context, old, value.qpc) orelse @as(u32, 0xffffffff));
        value.pid = if (next == 0) 0 else @bitCast(hooks.thread(hooks.context, next, value.qpc) orelse @as(u32, 0xffffffff));
    }
    self.owner.capture.apply(&value) catch |err| return result(err);
    hooks.event(hooks.context, .{ .qpc = value.qpc, .processor = raw_index,
        .data = .{ .cswitch = .{ .new_thread_id = @bitCast(value.tid), .old_thread_id = @bitCast(value.old_tid) } } }) catch |err| return result(err);
    return 0;
}

// DRAFT: append in io_replay_abi.zig. Fixed-cap scripted metadata is test input;
// all diagnostic ownership, matching, sampling and publication remain actual.
const controlled_native = @import("consumer.zig");
const controlled_trace = @import("trace_abi.zig");
const controlled_diagnostics = @import("thread_diagnostics.zig");

pub const ControlledProcessInput = extern struct { pid: u32, flags: u32, birth: i64 };
pub const ControlledThreadInput = extern struct {
    pid: u32, tid: u32, flags: u32, native_error: i32,
    birth: i64, exit: i64, kernel: i64, user: i64, start: u64,
    description: ?[*]const u16, module: ?[*]const u16,
    description_len: u32, module_len: u32, description_hresult: i32, reserved: u32,
    process_birth: i64,
};
const ControlledDiagnostics = struct {
    const Process = struct { value: ControlledProcessInput, latest: bool = true };
    const Thread = struct { value: ControlledThreadInput, latest: bool = true };
    budget: Budget,
    processes: []Process, threads: []Thread, names: []u16, modules: []u16,
    process_count: usize = 0, thread_count: usize = 0, units: usize,
    now_filetime: i64, last_error: i32 = 5,

    fn create(backing: std.mem.Allocator, config: shared.Config, now_filetime: i64) !*ControlledDiagnostics {
        // This is a distinct test-input owner. Its explicit allowance comes
        // from the fixture's CPU byte cap; it is not another production point.
        const cap = std.math.cast(usize, config.residency.byte_cap) orelse return error.InvalidInput;
        if (cap < @sizeOf(ControlledDiagnostics) or config.residency.max_processes == 0 or
            config.residency.max_threads == 0 or config.maximum_path_units == 0) return error.InvalidInput;
        const self = try backing.create(ControlledDiagnostics);
        self.budget = .{ .backing = backing, .cap = cap, .current = @sizeOf(ControlledDiagnostics), .peak = @sizeOf(ControlledDiagnostics) };
        errdefer {
            std.debug.assert(self.budget.current == @sizeOf(ControlledDiagnostics));
            backing.destroy(self);
        }
        const a = self.budget.allocator();
        const processes = try a.alloc(Process, config.residency.max_processes); errdefer a.free(processes);
        const threads = try a.alloc(Thread, config.residency.max_threads); errdefer a.free(threads);
        const count = try std.math.mul(usize, config.residency.max_threads, config.maximum_path_units);
        const names = try a.alloc(u16, count); errdefer a.free(names);
        const modules = try a.alloc(u16, count); errdefer a.free(modules);
        self.* = .{ .budget = self.budget, .processes = processes, .threads = threads, .names = names, .modules = modules,
            .units = config.maximum_path_units, .now_filetime = now_filetime };
        return self;
    }
    fn destroy(self: *ControlledDiagnostics) void {
        const backing = self.budget.backing;
        const a = self.budget.allocator();
        a.free(self.processes); a.free(self.threads); a.free(self.names); a.free(self.modules);
        std.debug.assert(self.budget.current == @sizeOf(ControlledDiagnostics));
        self.budget.current -= @sizeOf(ControlledDiagnostics);
        std.debug.assert(self.budget.current == 0);
        backing.destroy(self);
    }
    fn cast(context: ?*anyopaque) *ControlledDiagnostics { return @ptrCast(@alignCast(context.?)); }
    fn source(self: *ControlledDiagnostics) controlled_diagnostics.Source {
        return .{ .context = self, .open_process = openProcess, .process_birth = processBirth,
            .process_running = processRunning, .open_thread = openThread, .times = times,
            .name = name, .start = start, .module = module, .inventory = inventory,
            .close = close, .now = now, .last_error = lastError };
    }
    fn openProcess(context: ?*anyopaque, pid: u32, modules: bool) ?*anyopaque {
        const self = cast(context);
        for (self.processes[0..self.process_count]) |*entry|
            if (entry.latest and entry.value.pid == pid and entry.value.flags & 1 != 0 and (!modules or entry.value.flags & 4 != 0)) return entry;
        self.last_error = 5; return null;
    }
    fn processBirth(_: ?*anyopaque, handle: *anyopaque) ?i64 {
        const entry: *Process = @ptrCast(@alignCast(handle));
        return if (entry.value.flags & 8 != 0) entry.value.birth else null;
    }
    fn processRunning(_: ?*anyopaque, handle: *anyopaque) bool {
        const entry: *Process = @ptrCast(@alignCast(handle)); return entry.value.flags & 2 != 0;
    }
    fn openThread(context: ?*anyopaque, pid: u32, tid: u32) ?*anyopaque {
        const self = cast(context);
        var current_birth: i64 = 0;
        for (self.processes[0..self.process_count]) |entry| if (entry.latest and entry.value.pid == pid) { current_birth = entry.value.birth; break; };
        for (self.threads[0..self.thread_count]) |*entry| if (entry.latest and entry.value.pid == pid and entry.value.process_birth == current_birth and entry.value.tid == tid) {
            if (entry.value.flags & 1 != 0) return entry;
            self.last_error = entry.value.native_error; return null;
        };
        self.last_error = 5; return null;
    }
    fn times(context: ?*anyopaque, handle: *anyopaque) ?controlled_diagnostics.Times {
        const self = cast(context); const entry: *Thread = @ptrCast(@alignCast(handle)); const value = entry.value;
        if (value.flags & 2 == 0) { self.last_error = value.native_error; return null; }
        return .{ .birth = value.birth, .exit = value.exit, .kernel = value.kernel, .user = value.user };
    }
    fn name(_: ?*anyopaque, handle: *anyopaque, a: std.mem.Allocator, max_units: u32) !controlled_diagnostics.Name {
        const entry: *Thread = @ptrCast(@alignCast(handle)); const value = entry.value;
        if (value.description_len > max_units) return error.CapacityExceeded;
        return .{ .text = if (value.flags & 4 != 0) try a.dupe(u16, value.description.?[0..value.description_len]) else null,
            .hresult = if (value.flags & 8 != 0) value.description_hresult else null };
    }
    fn start(_: ?*anyopaque, handle: *anyopaque) ?u64 {
        const entry: *Thread = @ptrCast(@alignCast(handle)); return if (entry.value.flags & 16 != 0) entry.value.start else null;
    }
    fn module(context: ?*anyopaque, process: *anyopaque, address: u64, a: std.mem.Allocator, _: std.mem.Allocator, max_units: u32, _: u32) !?[]u16 {
        const self = cast(context); const owner: *Process = @ptrCast(@alignCast(process));
        for (self.threads[0..self.thread_count]) |entry| if (entry.value.pid == owner.value.pid and entry.value.process_birth == owner.value.birth and entry.value.start == address and entry.value.flags & 32 != 0) {
            if (entry.value.module_len > max_units) return error.CapacityExceeded;
            return try a.dupe(u16, entry.value.module.?[0..entry.value.module_len]);
        };
        return null;
    }
    fn inventory(context: ?*anyopaque, pid: u32, birth: i64, _: std.mem.Allocator, _: usize, visit: controlled_diagnostics.Visit, output: *anyopaque) !void {
        const self = cast(context);
        var valid = false;
        for (self.processes[0..self.process_count]) |entry| if (entry.latest and entry.value.pid == pid and entry.value.birth == birth) {
            if (entry.value.flags & 16 == 0) return error.SystemInformationUnavailable;
            valid = true;
        };
        if (!valid) return error.SystemInformationUnavailable;
        for (self.threads[0..self.thread_count]) |entry| if (entry.latest and entry.value.pid == pid and entry.value.process_birth == birth and entry.value.flags & 64 != 0)
            try visit(output, entry.value.tid, entry.value.birth, if (entry.value.flags & 16 != 0) entry.value.start else null);
    }
    fn close(_: ?*anyopaque, _: *anyopaque) void {}
    fn now(context: ?*anyopaque) i64 { return cast(context).now_filetime; }
    fn lastError(context: ?*anyopaque) i32 { return cast(context).last_error; }
    fn putProcess(self: *ControlledDiagnostics, input: ControlledProcessInput) !void {
        if (input.pid == 0 or input.birth <= 0 or input.flags & ~@as(u32, 31) != 0) return error.InvalidInput;
        for (self.processes[0..self.process_count]) |*entry| if (entry.value.pid == input.pid and entry.value.birth == input.birth) { entry.value = input; return; };
        if (self.process_count == self.processes.len) return error.CapacityExceeded;
        for (self.processes[0..self.process_count]) |*entry| if (entry.value.pid == input.pid) { entry.latest = false; entry.value.flags &= ~@as(u32, 2); };
        self.processes[self.process_count] = .{ .value = input }; self.process_count += 1;
    }
    fn putThread(self: *ControlledDiagnostics, incoming: ControlledThreadInput) !void {
        var input = incoming;
        if (input.process_birth == 0) for (self.processes[0..self.process_count]) |process|
            if (process.latest and process.value.pid == input.pid) { input.process_birth = process.value.birth; break; };
        if (input.pid == 0 or input.tid == 0 or input.birth <= 0 or input.flags & ~@as(u32, 127) != 0 or input.reserved != 0 or
            input.process_birth <= 0 or
            input.description_len > self.units or input.module_len > self.units or
            (input.description_len > 0 and input.description == null) or (input.module_len > 0 and input.module == null)) return error.InvalidInput;
        var index = self.thread_count;
        for (self.threads[0..self.thread_count], 0..) |entry, i| if (entry.value.pid == input.pid and entry.value.process_birth == input.process_birth and entry.value.tid == input.tid and entry.value.birth == input.birth) { index = i; break; };
        const fresh = index == self.thread_count;
        if (fresh) {
            if (index == self.threads.len) return error.CapacityExceeded;
            for (self.threads[0..self.thread_count]) |*entry| if (entry.value.pid == input.pid and entry.value.tid == input.tid) { entry.latest = false; };
            self.thread_count += 1;
        }
        var copy = input;
        const name_slice = self.names[index * self.units ..][0..input.description_len];
        const module_slice = self.modules[index * self.units ..][0..input.module_len];
        if (input.description_len > 0) @memcpy(name_slice, input.description.?[0..input.description_len]);
        if (input.module_len > 0) @memcpy(module_slice, input.module.?[0..input.module_len]);
        copy.description = name_slice.ptr; copy.module = module_slice.ptr;
        self.threads[index] = .{ .value = copy, .latest = if (fresh) true else self.threads[index].latest };
    }
};

/// No OS trace handle is opened. The same actual Trace/Consumer receives the
/// caller's raw records and uses this explicit clock for metadata/notifications.
pub export fn rm_kernel_etw_test_shared_initialize(handle: ?*anyopaque, qpc: i64, filetime: i64, mode: u32) callconv(.c) i32 {
    const self: *SharedReplay = @ptrCast(@alignCast(handle orelse return 1));
    if (self.owner.trace != null or qpc < 0 or filetime <= 0 or mode > 2) return 1;
    const a = self.owner.controlAllocator();
    const t = a.create(controlled_trace.Trace) catch |err| return result(err);
    const copied = a.dupeSentinel(u16, std.unicode.utf8ToUtf16LeStringLiteral("controlled-native-test"), 0) catch |err| { a.destroy(t); return result(err); };
    const scripted = if (mode == 2) ControlledDiagnostics.create(std.heap.smp_allocator, self.owner.config, filetime) catch |err| {
        a.free(copied); a.destroy(t); return result(err);
    } else null;
    t.* = .{ .owner = if (self.owner.has(1)) self.owner.capture else null,
        .interrupts = if (self.owner.has(2)) self.owner.interrupts else null,
        .paths = if (self.owner.paths_active) &self.owner.paths else null,
        .name = copied, .shared_hooks = self.owner.hooks(), .interrupt_qpc_origin = qpc,
        .interrupt_tick_origin = self.owner.config.started_tick_ms,
        .consumer = controlled_native.Consumer.init(.{ .context = t, .on_notice = controlled_trace.Trace.onNotice,
            .on_unhandled = controlled_trace.Trace.onUnhandled, .on_buffer = controlled_trace.Trace.onBuffer }) };
    t.consumer.metadata = .{ .qpc_frequency = self.owner.config.residency.qpc_frequency,
        .start_filetime_100ns = filetime, .header_qpc = qpc, .clock_type = 1, .pointer_size = 8,
        .processor_count = self.owner.config.residency.max_processors };
    if (self.owner.has(1)) self.owner.capture.active_traces += 1;
    if (self.owner.has(2)) self.owner.interrupts.active_traces += 1;
    self.owner.trace = t;
    self.owner.configureDiagnosticSource(if (scripted) |source| source.source() else null, mode == 0);
    self.controlled_diagnostics = scripted;
    return 0;
}
pub export fn rm_kernel_etw_test_shared_raw(handle: ?*anyopaque, provider: decoder.Guid, opcode: u8, version: u8, pointer_size: u8,
    qpc: i64, processor: u16, pid: u32, tid: u32, payload: ?[*]const u8, length: u32) callconv(.c) i32 {
    const self: *SharedReplay = @ptrCast(@alignCast(handle orelse return 1));
    const t = self.owner.trace orelse return 3;
    if (self.owner.failure.load(.acquire) != 0) return self.owner.failure.load(.acquire);
    if (t.consumer.processing.load(.acquire) or (pointer_size != 4 and pointer_size != 8) or length > std.math.maxInt(u16) or (length > 0 and payload == null)) return 1;
    const tick = t.eventTick(qpc) catch |err| return result(err);
    self.owner.drain(tick) catch |err| return result(err);
    var record: controlled_native.EventRecord = std.mem.zeroes(controlled_native.EventRecord);
    record.header.size = @sizeOf(controlled_native.EventHeader);
    record.header.flags = (if (pointer_size == 4) @as(u16, 0x20) else @as(u16, 0x40)) | 0x200;
    record.header.provider = provider; record.header.descriptor.opcode = opcode; record.header.descriptor.version = version;
    record.header.timestamp = qpc; record.header.process_id = pid; record.header.thread_id = tid;
    record.processor_index = processor; record.payload_length = @intCast(length);
    record.payload = if (payload) |value| @constCast(@as(*const anyopaque, @ptrCast(value))) else null;
    t.consumer.acceptRecord(&record);
    return t.failure;
}
pub const ControlledDiagnosticUsage = extern struct { native_reads: u64, open_handles: u64, peak_handles: u64 };
pub export fn rm_kernel_etw_test_shared_diagnostic_usage(handle: ?*anyopaque, output: ?*ControlledDiagnosticUsage) callconv(.c) i32 {
    const owner: *shared.Owner = @ptrCast(@alignCast(handle orelse return 1)); const out = output orelse return 1;
    const acquired = owner.acquireOutputs(1) catch |err| return result(err);
    defer owner.release(acquired.token) catch unreachable;
    const value = acquired.view.?.diagnostics;
    out.* = .{ .native_reads = value.native_reads, .open_handles = value.open_handles, .peak_handles = value.peak_handles };
    return 0;
}
pub export fn rm_kernel_etw_test_shared_diagnostic_clock(handle: ?*anyopaque, filetime: i64) callconv(.c) i32 {
    const self: *SharedReplay = @ptrCast(@alignCast(handle orelse return 1));
    const source = self.controlled_diagnostics orelse return 1;
    if (filetime <= 0) return 1; source.now_filetime = filetime; return 0;
}
pub export fn rm_kernel_etw_test_shared_diagnostic_process(handle: ?*anyopaque, input: ?*const ControlledProcessInput) callconv(.c) i32 {
    const self: *SharedReplay = @ptrCast(@alignCast(handle orelse return 1));
    const source = self.controlled_diagnostics orelse return 1;
    source.putProcess((input orelse return 1).*) catch |err| return result(err); return 0;
}
pub export fn rm_kernel_etw_test_shared_diagnostic_thread(handle: ?*anyopaque, input: ?*const ControlledThreadInput) callconv(.c) i32 {
    const self: *SharedReplay = @ptrCast(@alignCast(handle orelse return 1));
    const source = self.controlled_diagnostics orelse return 1;
    source.putThread((input orelse return 1).*) catch |err| return result(err); return 0;
}
/// Cold test staging barrier only; it never samples or publishes owner data.
pub export fn rm_kernel_etw_test_shared_pending_diagnostic_samples(handle: ?*anyopaque, output: ?*u32) callconv(.c) i32 {
    const owner: *shared.Owner = @ptrCast(@alignCast(handle orelse return 1)); const out = output orelse return 1;
    // Use the actual queue mutex value; no product owner helper/API is added.
    while (!owner.queue_mutex.value.tryLock()) std.atomic.spinLoopHint();
    defer owner.queue_mutex.value.unlock();
    var count: u32 = 0;
    for (owner.commands[0..owner.command_count]) |command| if (command.kind == 19) { count += 1; };
    out.* = count; return 0;
}
pub const ControlledDiagnosticInputUsage = extern struct { current: u64, peak: u64, cap: u64, failed: u64 };
pub export fn rm_kernel_etw_test_shared_diagnostic_input_usage(handle: ?*anyopaque, output: ?*ControlledDiagnosticInputUsage) callconv(.c) i32 {
    const self: *SharedReplay = @ptrCast(@alignCast(handle orelse return 1)); const out = output orelse return 1;
    const source = self.controlled_diagnostics orelse return 3;
    out.* = .{ .current = source.budget.current, .peak = source.budget.peak, .cap = source.budget.cap, .failed = source.budget.failures };
    return 0;
}
fn controlledInputAllocationScenario(backing: std.mem.Allocator) !void {
    var config: shared.Config = std.mem.zeroes(shared.Config);
    config.residency.max_processes = 2; config.residency.max_threads = 3;
    config.residency.byte_cap = 4096; config.maximum_path_units = 16;
    const source = try ControlledDiagnostics.create(backing, config, 1000);
    defer source.destroy();
    try source.putProcess(.{ .pid = 41, .flags = 31, .birth = 100 });
    var thread: ControlledThreadInput = std.mem.zeroes(ControlledThreadInput);
    thread.pid = 41; thread.tid = 51; thread.birth = 200; thread.process_birth = 100; thread.flags = 71;
    const name = std.unicode.utf8ToUtf16LeStringLiteral("worker");
    thread.description = name.ptr; thread.description_len = name.len;
    try source.putThread(thread);
    const before = source.budget.current;
    thread.kernel = 10; thread.user = 20; try source.putThread(thread);
    try std.testing.expectEqual(before, source.budget.current);
    try std.testing.expect(source.budget.current > @sizeOf(ControlledDiagnostics));
    try std.testing.expectEqual(source.budget.current, source.budget.peak);
    try std.testing.expect(source.budget.peak <= source.budget.cap);
    thread.description_len = 17;
    try std.testing.expectError(error.InvalidInput, source.putThread(thread));
    try std.testing.expectEqual(before, source.budget.current);
    try source.putProcess(.{ .pid = 41, .flags = 31, .birth = 101 });
    try std.testing.expectError(error.CapacityExceeded, source.putProcess(.{ .pid = 42, .flags = 31, .birth = 102 }));
    try std.testing.expectEqual(before, source.budget.current);
}
test "scripted diagnostic input budget covers complete lifecycle and every backing failure" {
    try std.testing.checkAllAllocationFailures(std.testing.allocator, controlledInputAllocationScenario, .{});
}
test "scripted diagnostic input cap failure and invalid bounds release every allocation" {
    var config: shared.Config = std.mem.zeroes(shared.Config);
    config.residency.max_processes = 2; config.residency.max_threads = 3; config.maximum_path_units = 16;
    config.residency.byte_cap = @sizeOf(ControlledDiagnostics);
    try std.testing.expectError(error.OutOfMemory, ControlledDiagnostics.create(std.testing.allocator, config, 1000));
    config.residency.byte_cap = 4096; config.maximum_path_units = 0;
    try std.testing.expectError(error.InvalidInput, ControlledDiagnostics.create(std.testing.allocator, config, 1000));
    config.maximum_path_units = 16;
    const source = try ControlledDiagnostics.create(std.testing.allocator, config, 1000);
    defer source.destroy();
    const before = source.budget.current;
    try std.testing.expectError(error.OutOfMemory, source.budget.allocator().alloc(u8, source.budget.cap));
    try std.testing.expectEqual(@as(u64, 1), source.budget.failures);
    try std.testing.expectEqual(before, source.budget.current);
    try std.testing.expectEqual(before, source.budget.peak);
}

/// Test-only pre-process adapter. The caller owns this fixed storage until the
/// actual processing flow joins and unregisters its native publication callback.
pub const PublicationChain = extern struct {
    original_callback: ?*const fn (?*anyopaque) callconv(.c) void,
    original_context: ?*anyopaque,
    barrier_callback: *const fn (?*anyopaque) callconv(.c) void,
    barrier_context: ?*anyopaque,

    fn signal(context: ?*anyopaque) callconv(.c) void {
        const self: *PublicationChain = @ptrCast(@alignCast(context.?));
        if (self.original_callback) |callback| callback(self.original_context);
        self.barrier_callback(self.barrier_context);
    }
};
pub export fn rm_kernel_etw_test_shared_chain_publication(handle: ?*anyopaque, storage: ?*PublicationChain,
    barrier: ?*const fn (?*anyopaque) callconv(.c) void, context: ?*anyopaque) callconv(.c) i32
{
    const owner: *shared.Owner = @ptrCast(@alignCast(handle orelse return 1));
    const chain = storage orelse return 1;
    const callback = barrier orelse return 1;
    if (owner.trace) |t| if (t.consumer.processing.load(.acquire)) return 3;
    if (owner.publication_callback == PublicationChain.signal) return 1;
    chain.* = .{ .original_callback = owner.publication_callback, .original_context = owner.publication_context,
        .barrier_callback = callback, .barrier_context = context };
    owner.publication_callback = PublicationChain.signal;
    owner.publication_context = chain;
    return 0;
}

test "recorded publication barrier preserves every original signal and caller-owned storage" {
    const State = struct {
        order: [3]u8 = @splat(0),
        fn original(context: ?*anyopaque) callconv(.c) void {
            const self: *@This() = @ptrCast(@alignCast(context.?));
            self.order[0] = 1; self.order[1] = 2;
        }
        fn barrier(context: ?*anyopaque) callconv(.c) void {
            const self: *@This() = @ptrCast(@alignCast(context.?));
            self.order[2] = if (self.order[0] == 1 and self.order[1] == 2) 3 else 255;
        }
    };
    try std.testing.expectEqual(4 * @sizeOf(usize), @sizeOf(PublicationChain));
    try std.testing.expectEqual(2 * @sizeOf(usize), @offsetOf(PublicationChain, "barrier_callback"));
    // Only the fields inspected by the cold installer are initialized. No
    // product resource, OS trace, allocation fallback or fake bank is created.
    const owner = try std.testing.allocator.create(shared.Owner);
    defer std.testing.allocator.destroy(owner);
    var state: State = .{};
    owner.trace = null;
    owner.publication_callback = State.original; owner.publication_context = &state;
    var chain: PublicationChain = undefined;
    try std.testing.expectEqual(@as(i32, 0), rm_kernel_etw_test_shared_chain_publication(owner, &chain, State.barrier, &state));
    try std.testing.expectEqual(@as(i32, 1), rm_kernel_etw_test_shared_chain_publication(owner, &chain, State.barrier, &state));
    owner.publication_callback.?(owner.publication_context);
    try std.testing.expectEqualSlices(u8, &.{ 1, 2, 3 }, &state.order);
    // Detach before caller storage leaves scope, exactly as native Dispose does.
    owner.publication_callback = null; owner.publication_context = null;
}

test "recorded publication barrier cannot replace signals during actual processing" {
    const State = struct {
        fn original(_: ?*anyopaque) callconv(.c) void {}
        fn barrier(_: ?*anyopaque) callconv(.c) void {}
        fn notice(_: ?*anyopaque, _: controlled_native.Notice) bool { return true; }
    };
    const owner = try std.testing.allocator.create(shared.Owner);
    defer std.testing.allocator.destroy(owner);
    const trace = try std.testing.allocator.create(controlled_trace.Trace);
    defer std.testing.allocator.destroy(trace);
    trace.consumer = controlled_native.Consumer.init(.{ .context = null, .on_notice = State.notice });
    trace.consumer.processing.store(true, .release);
    owner.trace = trace;
    owner.publication_callback = State.original; owner.publication_context = owner;
    var chain: PublicationChain = undefined;
    try std.testing.expectEqual(@as(i32, 3), rm_kernel_etw_test_shared_chain_publication(owner, &chain, State.barrier, null));
    try std.testing.expect(owner.publication_callback == State.original);
    try std.testing.expect(owner.publication_context == @as(?*anyopaque, @ptrCast(owner)));
    trace.consumer.processing.store(false, .release);
    owner.trace = null; owner.publication_callback = null; owner.publication_context = null;
}

