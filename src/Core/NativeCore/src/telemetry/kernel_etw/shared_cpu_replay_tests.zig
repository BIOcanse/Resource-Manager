const std = @import("std");
const shared = @import("shared_owner.zig");
const boundary = @import("shared_abi.zig");
const host = @import("host_allocator.zig");
const identity = @import("io_identity.zig");
const p = @import("protocol.zig");

/// Host callbacks backed by whole OS pages, so a use after release faults at once.
/// The lock stands in for the managed host's monitor: the consumer flow and a
/// host thread call it concurrently, as in the service.
const PageHost = struct {
    const Entry = struct { wanted: u64, physical: usize, tag: u32 };
    lock: std.atomic.Mutex = .unlocked,
    blocks: std.AutoHashMapUnmanaged(usize, Entry) = .empty,

    fn enter(self: *PageHost) void {
        while (!self.lock.tryLock()) std.atomic.spinLoopHint();
    }
    fn callbacks(self: *PageHost) host.Callbacks {
        return .{ .page_size = 4096, .context = self, .allocate = allocate, .remap = remap, .release = release };
    }
    fn pages(wanted: u64) usize {
        return std.mem.alignForward(usize, @intCast(@max(wanted, 1)), 4096);
    }
    fn allocate(context: ?*anyopaque, tag: u32, wanted: u64, alignment_log2: u32) callconv(.c) ?*anyopaque {
        const self: *PageHost = @ptrCast(@alignCast(context.?));
        self.enter();
        defer self.lock.unlock();
        if (alignment_log2 > 12) return null;
        const memory = std.heap.page_allocator.alloc(u8, pages(wanted)) catch return null;
        self.blocks.put(std.heap.page_allocator, @intFromPtr(memory.ptr), .{ .wanted = wanted, .physical = memory.len, .tag = tag }) catch {
            std.heap.page_allocator.free(memory);
            return null;
        };
        return memory.ptr;
    }
    fn remap(context: ?*anyopaque, tag: u32, old: ?*anyopaque, old_bytes: u64, new_bytes: u64, _: u32) callconv(.c) ?*anyopaque {
        const self: *PageHost = @ptrCast(@alignCast(context.?));
        self.enter();
        defer self.lock.unlock();
        const address = @intFromPtr(old orelse return null);
        const entry = self.blocks.getPtr(address) orelse return null;
        if (entry.tag != tag or entry.wanted != old_bytes) return null;
        const physical = pages(new_bytes);
        if (physical == entry.physical) {
            entry.wanted = new_bytes;
            return old;
        }
        const memory = std.heap.page_allocator.alloc(u8, physical) catch return null;
        const previous: [*]u8 = @ptrCast(old.?);
        const kept: usize = @intCast(@min(old_bytes, new_bytes));
        @memcpy(memory[0..kept], previous[0..kept]);
        const old_physical = entry.physical;
        _ = self.blocks.remove(address);
        self.blocks.put(std.heap.page_allocator, @intFromPtr(memory.ptr), .{ .wanted = new_bytes, .physical = physical, .tag = tag }) catch {
            std.heap.page_allocator.free(memory);
            return null;
        };
        std.heap.page_allocator.free(previous[0..old_physical]);
        return memory.ptr;
    }
    fn release(context: ?*anyopaque, tag: u32, pointer: ?*anyopaque, bytes: u64, _: u32) callconv(.c) u32 {
        const self: *PageHost = @ptrCast(@alignCast(context.?));
        self.enter();
        defer self.lock.unlock();
        const address = @intFromPtr(pointer orelse return 1);
        const entry = self.blocks.get(address) orelse return 1;
        if (entry.tag != tag or entry.wanted != bytes) return 2;
        _ = self.blocks.remove(address);
        const memory: [*]u8 = @ptrCast(pointer.?);
        std.heap.page_allocator.free(memory[0..entry.physical]);
        return 0;
    }
};

fn absentOpen(_: ?*anyopaque, _: u32) ?*anyopaque {
    return null;
}
fn absentPid(_: ?*anyopaque, _: *anyopaque) ?u32 {
    return null;
}
fn absentBirth(_: ?*anyopaque, _: *anyopaque) ?u64 {
    return null;
}
fn absentClose(_: ?*anyopaque, _: *anyopaque) void {}
const absent_identity: identity.Source = .{ .open_process = absentOpen, .read_process_id = absentPid, .read_creation_filetime = absentBirth, .close_process = absentClose };

/// The service's kernel_etw_limits (backend.json) for a 32-processor machine.
fn serviceConfig() shared.Config {
    return .{
        .residency = .{ .qpc_frequency = 10_000_000, .window_ms = 5000, .max_pairs = 65536, .max_processes = 32768, .max_threads = 65536, .max_processors = 32, .max_name_bytes = 2 * 1024 * 1024, .byte_cap = 40 * 1024 * 1024 },
        // kernel_etw_limits in Configuration/FreedomPoints/backend.json, as
        // passed by KernelEtwNativeSettings.CreateConfiguration (including bucket/report limits).
        .interrupts = .{ .max_modules = 16384, .max_path_units = 4096, .max_identities = 32768, .max_slices = 128, .max_slice_drivers = 16384, .bucket_ms = 10000, .bucket_count = 6, .reported_drivers = 16, .byte_cap = 16 * 1024 * 1024, .generation = 1, .started_tick_ms = 0, .events_lost = 0 },
        .byte_cap = 64 * 1024 * 1024,
        .disk_byte_cap = 8 * 1024 * 1024,
        .network_byte_cap = 8 * 1024 * 1024,
        .file_byte_cap = 4 * 1024 * 1024,
        .history_byte_cap = 4 * 1024 * 1024,
        .maximum_disk_pairs = 8192,
        .maximum_disks = 256,
        .maximum_cached_processes = 65536,
        .maximum_network_events = 65536,
        .maximum_network_processes = 32768,
        .maximum_file_entries = 16384,
        .maximum_file_versions = 65536,
        .maximum_history_entries = 65536,
        .maximum_path_units = 4096,
        .maximum_namespace_entries = 512,
        .maximum_commands = 32768,
        .maximum_command_name_bytes = 2 * 1024 * 1024,
        .publication_banks = 3,
        .window_ms = 2000,
        .retention_ms = 8000,
        .disk_bucket_ms = 100,
        .started_tick_ms = 0,
        .maximum_pending_events = 16384,
        .maximum_pending_path_units = 1048576,
        .maximum_renderer_processes = 4096,
        .network_initial_events = 256,
    };
}

const HostThread = struct {
    owner: *shared.Owner,
    done: std.atomic.Value(bool) = .init(false),
    rounds: usize = 0,
    seeded: usize = 0,
    publications: usize = 0,
    windows: usize = 0,

    /// What the managed host does while ProcessTrace runs: inventory seeds of one
    /// process and its threads in 64-command submits, residency requests and
    /// leases, notification leases, and finite windows paused and begun again.
    fn run(self: *HostThread) void {
        var state: u64 = 0x5eed;
        var inputs: [64]p.Input = undefined;
        const name = "seeded-process";
        while (!self.done.load(.acquire)) : (self.rounds += 1) {
            state = state *% 6364136223846793005 +% 1442695040888963407;
            const pid: i32 = @intCast(4 + (state >> 33) % 449);
            const threads: usize = @intCast(1 + (state >> 20) % 25);
            inputs[0] = .{ .kind = 11, .flags = 1, .pid = pid, .start = 1000 + pid, .qpc = 0, .name = name, .name_len = name.len };
            var count: usize = 1;
            for (0..threads) |index| {
                if (count == inputs.len) {
                    self.owner.submit(inputs[0..count]) catch {};
                    count = 0;
                }
                inputs[count] = .{ .kind = 12, .pid = pid, .start = 1000 + pid, .tid = @intCast(8 + pid * 256 + @as(i32, @intCast(index)) * 4), .qpc = 0 };
                count += 1;
            }
            self.owner.submit(inputs[0..count]) catch {};
            self.seeded += threads;
            _ = self.owner.requestResidency() catch {};
            if (self.owner.acquireResidency()) |lease| {
                self.publications += 1;
                var total: i64 = 0;
                if (lease.view.rows) |rows| for (rows[0..lease.view.count]) |row| {
                    total +%= row.duration;
                    if (row.name) |text| if (row.name_len != 0) {
                        total +%= text[row.name_len - 1];
                    };
                };
                std.mem.doNotOptimizeAway(total);
                self.owner.release(lease.token) catch {};
            } else |_| {}
            if (self.owner.acquireOutputs(64)) |lease| {
                const view = lease.view.?.*;
                if (view.notifications) |rows| for (rows[0..view.notification_count]) |row| std.mem.doNotOptimizeAway(row.pid);
                self.owner.release(lease.token) catch {};
            } else |_| {}
            if (self.rounds % 65536 == 65535) {
                self.windows += 1;
                self.owner.setEnabled(1, 0) catch {};
                self.owner.setEnabled(1, 1) catch {};
                self.owner.submit(&.{.{ .kind = 9, .qpc = 0, .unique = 0 }}) catch {};
            }
            for (0..2000) |_| std.atomic.spinLoopHint();
        }
    }
};

test "optional real kernel ETL replays CPU residency through a host-backed owner while a host thread seeds and reads" {
    const key = std.unicode.utf8ToUtf16LeStringLiteral("RESOURCE_MANAGER_ETW_CPU_ETL_PATH");
    const path = std.testing.environ.getWindows(key) orelse return error.SkipZigTest;
    var pages: PageHost = .{};
    defer pages.blocks.deinit(std.heap.page_allocator);
    const self = try shared.Owner.createWithAllocator(serviceConfig(), pages.callbacks(), absent_identity);
    var alive = true;
    defer if (alive) {
        self.setOutputs(0) catch {};
        self.destroy() catch {};
    };
    // CPU residency, IRQ, renderer and process notifications: the tray-state holders.
    try self.setOutputs(1 | 2 | 32 | 64);
    var ids: [32]i32 = undefined;
    for (&ids, 0..) |*id, index| id.* = @intCast(index);
    try self.configureProcessors(&ids);
    try self.setEnabled(1, 1);
    try self.submit(&.{ .{ .kind = 16, .qpc = 1000, .unique = 65536 }, .{ .kind = 9, .qpc = 0, .unique = 0 } });
    try std.testing.expectEqual(@as(i32, 0), boundary.rm_kernel_etw_shared_open(self, path.ptr, @intCast(path.len), 0));
    var worker: HostThread = .{ .owner = self };
    const thread = try std.Thread.spawn(.{}, HostThread.run, .{&worker});
    const result = boundary.rm_kernel_etw_shared_process(self);
    worker.done.store(true, .release);
    thread.join();
    std.debug.print("CPU ETL replay result={d} rounds={d} seeded={d} publications={d} windows={d} failure={d} host_blocks={d}\n", .{ result, worker.rounds, worker.seeded, worker.publications, worker.windows, self.failure.load(.acquire), pages.blocks.count() });
    if (self.failure.load(.acquire) != 0) std.debug.print("failure detail: {s} records={d} qpc={d} stage={d} cpu_failure={d}\n", .{ if (self.failure_detail.error_name) |text| text[0..self.failure_detail.error_name_length] else "(none)", self.failure_detail.records, self.failure_detail.qpc, self.failure_allocation.publication_stage, self.capture.residency.last_capacity_failure.reason });
    {
        const r = &self.capture.residency;
        const m = r.snapshotMetadata();
        std.debug.print("residency: measuring={} complete={} known={d}/{d} live_pairs={d} peak_storage={d} epoch={d} resets={d} threads={d} processes={d} latest_qpc={d} floor={d} enabled={}" ++ "\n", .{ r.measuring, m.is_complete, r.known_processors, r.processors.len, r.live_pairs, r.window_storage_peak, r.continuity_epoch, r.capacity_reset_count, r.threads.items.len, r.processes.items.len, r.latest_event_qpc, r.context_switch_floor_qpc, self.readers.residency_enabled });
    }
    try std.testing.expectEqual(@as(i32, 0), result);
    try std.testing.expectEqual(@as(i32, 0), boundary.rm_kernel_etw_shared_close_trace(self));
    try self.setOutputs(0);
    try self.destroy();
    alive = false;
    try std.testing.expectEqual(@as(u32, 0), pages.blocks.count());
}
