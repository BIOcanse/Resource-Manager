const std = @import("std");
const shared = @import("shared_owner.zig");
const host = @import("host_allocator.zig");
const identity = @import("io_identity.zig");
const native = @import("consumer.zig");
const trace = @import("trace_abi.zig");
const io = @import("io_decode.zig");
const fileio = @import("fileio.zig");

fn config() shared.Config {
    return .{
        .residency = .{ .qpc_frequency = 1000, .window_ms = 1000, .bucket_ms = 50, .max_pairs = 1, .max_cells = 1024, .max_processes = 16, .max_threads = 32, .max_processors = 2, .max_name_bytes = 4096, .byte_cap = 1024 * 1024 },
        .interrupts = .{ .max_modules = 8, .max_path_units = 128, .max_identities = 16, .max_slices = 64, .max_slice_drivers = 128, .byte_cap = 1024 * 1024, .generation = 1, .started_tick_ms = 0, .events_lost = 0 },
        .byte_cap = 8 * 1024 * 1024, .disk_byte_cap = 512 * 1024, .network_byte_cap = 512 * 1024,
        .file_byte_cap = 256 * 1024, .history_byte_cap = 256 * 1024,
        .maximum_disk_pairs = 32, .maximum_disks = 16, .maximum_cached_processes = 16,
        .maximum_network_events = 64, .maximum_network_processes = 16,
        .maximum_file_entries = 16, .maximum_file_versions = 64, .maximum_history_entries = 64,
        .maximum_path_units = 128, .maximum_namespace_entries = 16,
        .maximum_commands = 8, .maximum_command_name_bytes = 128, .publication_banks = 3,
        .window_ms = 2000, .retention_ms = 8000, .disk_bucket_ms = 100, .started_tick_ms = 0,
        .maximum_pending_events = 128, .maximum_pending_path_units = 4096, .maximum_renderer_processes = 64,
    };
}

const FakeIdentity = struct {
    fn open(_: ?*anyopaque, process_id: u32) ?*anyopaque { return if (process_id == 7) @ptrFromInt(7) else null; }
    fn pid(_: ?*anyopaque, handle: *anyopaque) ?u32 { return @intCast(@intFromPtr(handle)); }
    fn birth(_: ?*anyopaque, _: *anyopaque) ?u64 { return 100; }
    fn close(_: ?*anyopaque, _: *anyopaque) void {}
    const source: identity.Source = .{ .open_process = open, .read_process_id = pid, .read_creation_filetime = birth, .close_process = close };
};

/// Uses the production EVENT_RECORD conversion, decoder and shared trace hooks.
/// Payload construction is test input; it performs no alternate event decoding.
fn deliver(t: *trace.Trace, provider: native.decoder.Guid, opcode: u8, version: u8, qpc: i64, payload: []const u8) !void {
    var record: native.EventRecord = std.mem.zeroes(native.EventRecord);
    record.header.size = @sizeOf(native.EventHeader);
    record.header.flags = 0x40 | 0x200;
    record.header.provider = provider;
    record.header.descriptor.opcode = opcode;
    record.header.descriptor.version = version;
    record.header.timestamp = qpc;
    record.header.process_id = 7;
    record.header.thread_id = 8;
    record.processor_index = 0;
    record.payload_length = @intCast(payload.len);
    record.payload = if (payload.len == 0) null else @ptrCast(@constCast(payload.ptr));
    t.consumer.acceptRecord(&record);
    try std.testing.expectEqual(@as(i32, 0), t.failure);
    try std.testing.expectEqual(@as(u64, 0), t.consumer.counters.rejected);
}

fn switchTo(t: *trace.Trace, qpc: i64, old: u32, next: u32) !void {
    var payload: [24]u8 = @splat(0);
    std.mem.writeInt(u32, payload[0..4], next, .little);
    std.mem.writeInt(u32, payload[4..8], old, .little);
    try deliver(t, native.decoder.thread_provider, 36, 2, qpc, &payload);
}

test "handled CPU capacity reset rewarms while shared IRQ disk network and FileIO keep consuming" {
    var backing: host.TestHost = .{};
    const self = try shared.Owner.createWithAllocator(config(), backing.callbacks(), FakeIdentity.source);
    defer {
        self.destroy() catch unreachable;
        std.debug.assert(backing.empty());
    }
    try self.setOutputs(127);
    try self.submit(&.{.{ .kind = 1, .pid = 7, .flags = 1, .start = 100, .name = "process".ptr, .name_len = 7 }});
    try self.drain(0);

    var t: trace.Trace = .{
        .owner = self.capture, .interrupts = self.interrupts, .paths = &self.paths,
        .name = @constCast(std.unicode.utf8ToUtf16LeStringLiteral("controlled-capacity-reset")),
        .consumer = native.Consumer.init(.{ .context = null, .on_notice = trace.Trace.onNotice, .on_unhandled = trace.Trace.onUnhandled, .on_buffer = trace.Trace.onBuffer }),
        .shared_hooks = self.hooks(), .interrupt_qpc_origin = 0,
    };
    t.consumer.sink.context = &t;
    t.consumer.metadata = .{ .qpc_frequency = 1000, .header_qpc = 0, .start_filetime_100ns = 100, .clock_type = 1, .pointer_size = 8, .processor_count = 2 };
    self.trace = &t;
    defer { t.deinitImageRound(); self.trace = null; }

    for ([_]u32{ 8, 9 }) |tid| {
        var payload: [8]u8 = @splat(0);
        std.mem.writeInt(u32, payload[0..4], tid, .little);
        std.mem.writeInt(u32, payload[4..8], 7, .little);
        try deliver(&t, native.decoder.thread_provider, 1, 0, 0, &payload);
    }
    try switchTo(&t, 0, 0, 8);
    try switchTo(&t, 100, 8, 9);
    try switchTo(&t, 200, 9, 8); // A second live pair exceeds the CPU cap of one.
    const reset = self.capture.residency.getDiagnostics();
    try std.testing.expectEqual(@as(i64, 1), reset.capacity_reset_count);
    try std.testing.expectEqual(@as(u32, 1), reset.last_capacity_reset.reason);
    try std.testing.expectEqual(@as(usize, 0), reset.active_pair_count);
    try std.testing.expectEqual(@as(i64, 200), reset.last_capacity_reset.event_qpc);
    try std.testing.expectEqual(@as(i32, 0), self.failure.load(.acquire));
    try switchTo(&t, 300, 8, 8); // The failing switch became the fresh first boundary.
    try std.testing.expectEqual(@as(usize, 1), self.capture.residency.getDiagnostics().active_pair_count);

    var image: [28]u8 = @splat(0);
    std.mem.writeInt(u64, image[0..8], 0x1000, .little);
    std.mem.writeInt(u32, image[8..12], 0x100, .little);
    for ("drv.sys", 0..) |unit, i| image[12 + 2 * i] = unit;
    try deliver(&t, native.decoder.image_provider, 10, 0, 1400, &image);
    var dpc: [16]u8 = @splat(0);
    std.mem.writeInt(i64, dpc[0..8], 1490, .little);
    std.mem.writeInt(u64, dpc[8..16], 0x1010, .little);
    try deliver(&t, native.decoder.perfinfo_provider, 68, 2, 1500, &dpc);

    var disk: [64]u8 = @splat(0);
    std.mem.writeInt(u32, disk[0..4], 2, .little);
    std.mem.writeInt(u32, disk[8..12], 4096, .little);
    try deliver(&t, io.disk_io_task_guid, 10, 3, 1500, &disk);
    var network: [20]u8 = @splat(0);
    std.mem.writeInt(u32, network[0..4], 7, .little);
    std.mem.writeInt(u32, network[4..8], 2048, .little);
    @memcpy(network[8..12], &[_]u8{ 8, 8, 8, 8 });
    try deliver(&t, io.tcp_ip_task_guid, 10, 1, 1500, &network);

    var create: [52]u8 = @splat(0);
    std.mem.writeInt(u64, create[0..8], 0x111, .little);
    std.mem.writeInt(u32, create[8..12], 8, .little);
    std.mem.writeInt(u64, create[16..24], 0x44, .little);
    for ("C:\\file", 0..) |unit, i| create[36 + 2 * i] = unit;
    try deliver(&t, fileio.file_io_task_guid, 64, 2, 1500, &create);
    try std.testing.expectEqual(@as(u64, 1), self.file_events);
    try std.testing.expectEqualSlices(u16, &.{ 'C', ':', '\\', 'f', 'i', 'l', 'e' }, self.files.lookupName(0x44, 1500).?);

    try self.publish(3000);
    const publication = try self.acquire();
    defer self.release(publication.token) catch unreachable;
    const view = publication.view.?;
    try std.testing.expectEqual(@as(u64, 1), view.interrupts.event_count);
    try std.testing.expectEqual(@as(u32, 1), view.process_disk_count);
    try std.testing.expectEqual(@as(u64, 4096), view.process_disk.?[0].read_bytes);
    try std.testing.expectEqual(@as(u32, 1), view.network_count);
    try std.testing.expectEqual(@as(u64, 2048), view.network.?[0].send_bytes);
    try std.testing.expectEqual(@as(u64, 1), view.file_events);
    try std.testing.expectEqual(@as(u32, 2), view.notification_count);
    try std.testing.expectEqual(@as(u32, 9), view.notifications.?[0].kind);
    try std.testing.expectEqual(@as(u32, 1), view.notifications.?[1].kind);
    try std.testing.expectEqual(@as(i32, 0), self.failure.load(.acquire));
    try std.testing.expectEqual(@as(u64, 0), t.consumer.counters.rejected);
    try std.testing.expectEqual(@as(u64, 11), t.consumer.counters.records);
    try std.testing.expectEqual(@as(u64, 8), t.consumer.counters.decoded);
    try std.testing.expectEqual(@as(i64, 1), self.capture.residency.getDiagnostics().capacity_reset_count);
}
