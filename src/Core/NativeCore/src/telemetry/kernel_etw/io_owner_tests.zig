const std = @import("std");
const decode = @import("io_decode.zig");
const identity = @import("io_identity.zig");
const disk = @import("disk_io_owner.zig");
const network = @import("network_io_owner.zig");

fn put(comptime T: type, buffer: []u8, offset: usize, value: T) void {
    std.mem.writeInt(T, buffer[offset..][0..@sizeOf(T)], value, .little);
}

fn raw(provider: @TypeOf(decode.disk_io_task_guid), opcode: u8, version: u8, pointer_size: u8, payload: []const u8) decode.RawEvent {
    return .{
        .provider = provider,
        .opcode = opcode,
        .version = version,
        .pointer_size = pointer_size,
        .qpc = 42,
        .processor = 0,
        .header_process_id = std.math.maxInt(u32),
        .header_thread_id = std.math.maxInt(u32),
        .payload = payload,
    };
}

test "classic disk TaskGuid decodes unsigned bytes and repaired issue thread id" {
    var payload: [52]u8 = @splat(0);
    put(u32, &payload, 0, 2);
    put(u32, &payload, 8, std.math.maxInt(u32));
    put(u32, &payload, 48, 77);
    const event = try decode.decode(raw(decode.disk_io_task_guid, 11, 1, 8, &payload), 1234);
    try std.testing.expect(event != null and event.? == .disk);
    const transfer = event.?.disk;
    try std.testing.expectEqual(@as(?u32, null), transfer.process_id);
    try std.testing.expectEqual(@as(?u32, 77), transfer.thread_id);
    try std.testing.expectEqual(std.math.maxInt(u32), transfer.bytes);
    try std.testing.expectEqual(@as(?u32, 2), transfer.disk_number);
    try std.testing.expect(transfer.is_write);
    try std.testing.expectEqual(@as(?u64, 1234), transfer.event_filetime_100ns);
}

test "classic disk read retains unknown disk and header thread identity" {
    var payload: [48]u8 = @splat(0);
    put(u32, &payload, 0, @bitCast(@as(i32, -1)));
    put(u32, &payload, 8, 4096);
    var event_raw = raw(decode.disk_io_task_guid, 10, 0, 8, &payload);
    event_raw.header_process_id = 41;
    event_raw.header_thread_id = 12;
    const event = try decode.decode(event_raw, null);
    try std.testing.expect(event != null and event.? == .disk);
    try std.testing.expectEqual(@as(?u32, 41), event.?.disk.process_id);
    try std.testing.expectEqual(@as(?u32, 12), event.?.disk.thread_id);
    try std.testing.expectEqual(@as(?u32, null), event.?.disk.disk_number);
    try std.testing.expect(!event.?.disk.is_write);
}

test "UDPv0 preserves a missing PID and the header TID for native history resolution" {
    var payload: [24]u8 = @splat(0);
    put(i16, &payload, 14, 16);
    payload[16..20].* = .{ 127, 0, 0, 1 };
    var event_raw = raw(decode.udp_ip_task_guid, 10, 0, 8, &payload);
    event_raw.header_thread_id = 77;
    const event = (try decode.decode(event_raw, 1234)).?.network;
    try std.testing.expectEqual(@as(?u32, null), event.process_id);
    try std.testing.expectEqual(@as(?u32, 77), event.thread_id);
    try std.testing.expectEqual(@as(u32, 16), event.bytes);
}

test "network decoder accepts the eight classic TCP and UDP TaskGuid variants" {
    var v4_send: [36]u8 = @splat(0);
    put(u32, &v4_send, 0, 7);
    put(u32, &v4_send, 4, std.math.maxInt(u32));
    v4_send[8..12].* = .{ 127, 0, 0, 1 };
    const tcp_send = try decode.decode(raw(decode.tcp_ip_task_guid, 10, 2, 8, &v4_send), 90);
    try std.testing.expect(tcp_send != null and tcp_send.? == .network);
    try std.testing.expectEqual(@as(?u32, 7), tcp_send.?.network.process_id);
    try std.testing.expectEqual(std.math.maxInt(u32), tcp_send.?.network.bytes);
    try std.testing.expectEqual(decode.Direction.send, tcp_send.?.network.direction);
    try std.testing.expectEqual(decode.Protocol.tcp, tcp_send.?.network.protocol);
    try std.testing.expectEqual([4]u8{ 127, 0, 0, 1 }, tcp_send.?.network.remote_address.ipv4);
    put(u32, &v4_send, 8, 1);
    const little_endian_ipv4 = try decode.decode(raw(decode.tcp_ip_task_guid, 10, 2, 8, &v4_send), 90);
    try std.testing.expectEqual([4]u8{ 1, 0, 0, 0 }, little_endian_ipv4.?.network.remote_address.ipv4);

    var v4_receive: [28]u8 = @splat(0);
    put(u32, &v4_receive, 0, 8);
    put(u32, &v4_receive, 4, 123);
    v4_receive[8..12].* = .{ 10, 0, 0, 1 };
    v4_receive[12..16].* = .{ 203, 0, 113, 9 };
    const tcp_receive = try decode.decode(raw(decode.tcp_ip_task_guid, 11, 1, 8, &v4_receive), 91);
    try std.testing.expectEqual(decode.Direction.receive, tcp_receive.?.network.direction);
    try std.testing.expectEqual([4]u8{ 203, 0, 113, 9 }, tcp_receive.?.network.remote_address.ipv4);

    var v6_send: [64]u8 = @splat(0);
    put(u32, &v6_send, 0, 9);
    put(u32, &v6_send, 4, 321);
    v6_send[8..24].* = .{ 0x20, 1, 0x0d, 0xb8, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1 };
    const tcp_v6_send = try decode.decode(raw(decode.tcp_ip_task_guid, 26, 2, 8, &v6_send), 92);
    try std.testing.expectEqual(decode.Protocol.tcp, tcp_v6_send.?.network.protocol);
    try std.testing.expectEqual(v6_send[8..24].*, tcp_v6_send.?.network.remote_address.ipv6);

    var v6_receive: [56]u8 = @splat(0);
    put(u32, &v6_receive, 0, 10);
    put(u32, &v6_receive, 4, 654);
    v6_receive[24..40].* = .{ 0x20, 1, 0x0d, 0xb8, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 2 };
    const tcp_v6_receive = try decode.decode(raw(decode.tcp_ip_task_guid, 27, 2, 8, &v6_receive), 93);
    try std.testing.expectEqual(decode.Direction.receive, tcp_v6_receive.?.network.direction);
    try std.testing.expectEqual(v6_receive[24..40].*, tcp_v6_receive.?.network.remote_address.ipv6);

    var udp_v4_send: [24]u8 = @splat(0);
    put(u32, &udp_v4_send, 0, 11);
    put(u32, &udp_v4_send, 4, 1000);
    udp_v4_send[8..12].* = .{ 198, 51, 100, 2 };
    udp_v4_send[12..16].* = .{ 10, 0, 0, 2 };
    const udp_send = try decode.decode(raw(decode.udp_ip_task_guid, 10, 1, 8, &udp_v4_send), 94);
    try std.testing.expectEqual(decode.Protocol.udp, udp_send.?.network.protocol);
    try std.testing.expectEqual(decode.Direction.send, udp_send.?.network.direction);
    try std.testing.expectEqual([4]u8{ 198, 51, 100, 2 }, udp_send.?.network.remote_address.ipv4);

    var udp_v4_receive: [24]u8 = @splat(0);
    put(u32, &udp_v4_receive, 0, 12);
    put(u32, &udp_v4_receive, 4, 1001);
    udp_v4_receive[8..12].* = .{ 10, 0, 0, 3 };
    udp_v4_receive[12..16].* = .{ 192, 0, 2, 9 };
    const udp_receive = try decode.decode(raw(decode.udp_ip_task_guid, 11, 1, 8, &udp_v4_receive), 95);
    try std.testing.expectEqual(decode.Direction.receive, udp_receive.?.network.direction);
    try std.testing.expectEqual([4]u8{ 192, 0, 2, 9 }, udp_receive.?.network.remote_address.ipv4);

    var udp_v6_send: [56]u8 = @splat(0);
    put(u32, &udp_v6_send, 0, 13);
    put(u32, &udp_v6_send, 4, 2000);
    udp_v6_send[8..24].* = v6_send[8..24].*;
    const udp_send6 = try decode.decode(raw(decode.udp_ip_task_guid, 26, 2, 8, &udp_v6_send), 96);
    try std.testing.expectEqual(decode.Protocol.udp, udp_send6.?.network.protocol);
    try std.testing.expectEqual(v6_send[8..24].*, udp_send6.?.network.remote_address.ipv6);

    var udp_v6_receive: [56]u8 = @splat(0);
    put(u32, &udp_v6_receive, 0, 14);
    put(u32, &udp_v6_receive, 4, 2001);
    udp_v6_receive[24..40].* = v6_receive[24..40].*;
    const udp_receive6 = try decode.decode(raw(decode.udp_ip_task_guid, 27, 2, 8, &udp_v6_receive), 97);
    try std.testing.expectEqual(decode.Direction.receive, udp_receive6.?.network.direction);
    try std.testing.expectEqual(v6_receive[24..40].*, udp_receive6.?.network.remote_address.ipv6);
}

test "IPv4 and IPv6 leaves-host policy matches the managed owner" {
    try std.testing.expect(!decode.leavesHost(.{ .ipv4 = .{ 127, 0, 0, 1 } }));
    try std.testing.expect(!decode.leavesHost(.{ .ipv4 = .{ 127, 2, 3, 4 } }));
    try std.testing.expect(!decode.leavesHost(.{ .ipv4 = .{ 0, 0, 0, 0 } }));
    try std.testing.expect(!decode.leavesHost(.{ .ipv4 = .{ 255, 255, 255, 255 } }));
    try std.testing.expect(decode.leavesHost(.{ .ipv4 = .{ 192, 0, 2, 1 } }));
    try std.testing.expect(!decode.leavesHost(.{ .ipv6 = @splat(0) }));
    try std.testing.expect(!decode.leavesHost(.{ .ipv6 = .{ 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1 } }));
    try std.testing.expect(decode.leavesHost(.{ .ipv6 = .{ 0x20, 1, 0x0d, 0xb8, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1 } }));
    try std.testing.expect(!decode.leavesHost(.{ .ipv6 = .{ 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0xff, 0xff, 127, 0, 0, 1 } }));
    try std.testing.expect(decode.leavesHost(.{ .ipv6 = .{ 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0xff, 0xff, 127, 0, 0, 0 } }));
    try std.testing.expect(decode.leavesHost(.{ .ipv6 = .{ 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0xff, 0xff, 127, 2, 3, 4 } }));
    try std.testing.expect(decode.leavesHost(.{ .ipv6 = .{ 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0xff, 0xff, 127, 255, 255, 255 } }));
    try std.testing.expect(decode.leavesHost(.{ .ipv6 = .{ 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0xff, 0xff, 126, 255, 255, 255 } }));
    try std.testing.expect(decode.leavesHost(.{ .ipv6 = .{ 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0xff, 0xff, 128, 0, 0, 0 } }));
    try std.testing.expect(decode.leavesHost(.{ .ipv6 = .{ 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0xff, 0xff, 0, 0, 0, 0 } }));
    try std.testing.expect(decode.leavesHost(.{ .ipv6 = .{ 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 127, 0, 0, 1 } }));
}

test "recognized malformed records fail and non-TaskGuid providers stay unhandled" {
    try std.testing.expectError(error.TruncatedPayload, decode.decode(raw(decode.tcp_ip_task_guid, 27, 2, 8, &.{ 0, 1 }), null));
    var bad_disk: [47]u8 = @splat(0);
    try std.testing.expectError(error.TruncatedPayload, decode.decode(raw(decode.disk_io_task_guid, 10, 0, 8, &bad_disk), null));
    const logical_provider = std.mem.zeroes(@TypeOf(decode.tcp_ip_task_guid));
    try std.testing.expect((try decode.decode(raw(logical_provider, 10, 1, 8, &.{}), null)) == null);
}

const IdentityFixture = struct {
    current_pid: u32 = 0,
    birth: ?u64 = null,
    change_birth_on_read: usize = 0,
    changed_birth: ?u64 = null,
    fail_open_count: usize = 0,
    open_count: usize = 0,
    creation_reads: usize = 0,
    close_count: usize = 0,
};

fn identityOpen(context: ?*anyopaque, process_id: u32) ?*anyopaque {
    const fixture: *IdentityFixture = @ptrCast(@alignCast(context.?));
    fixture.open_count += 1;
    if (fixture.open_count <= fixture.fail_open_count) return null;
    fixture.current_pid = process_id;
    return fixture;
}

fn identityProcessId(_: ?*anyopaque, handle: *anyopaque) ?u32 {
    const fixture: *IdentityFixture = @ptrCast(@alignCast(handle));
    return fixture.current_pid;
}

fn identityCreation(context: ?*anyopaque, handle: *anyopaque) ?u64 {
    const fixture: *IdentityFixture = @ptrCast(@alignCast(handle));
    std.debug.assert(context.? == handle);
    fixture.creation_reads += 1;
    if (fixture.creation_reads == fixture.change_birth_on_read) return fixture.changed_birth;
    return fixture.birth;
}

fn identityClose(context: ?*anyopaque, handle: *anyopaque) void {
    const fixture: *IdentityFixture = @ptrCast(@alignCast(handle));
    std.debug.assert(context.? == handle);
    fixture.close_count += 1;
}

fn identitySource(fixture: *IdentityFixture) identity.Source {
    fixture.current_pid = 0;
    return .{
        .context = fixture,
        .open_process = identityOpen,
        .read_process_id = identityProcessId,
        .read_creation_filetime = identityCreation,
        .close_process = identityClose,
    };
}

test "disk owner aggregates verified process and per-disk rows with fixed rates" {
    var fixture: IdentityFixture = .{ .birth = 400 };
    var owner = try disk.Owner.init(std.testing.allocator, .{ .maximum_active_pairs = 8, .maximum_disks = 4, .byte_cap = 1 << 20 }, identitySource(&fixture));
    defer owner.deinit();
    const transfer: decode.DiskTransfer = .{ .process_id = 7, .thread_id = 70, .bytes = 100, .disk_number = 2, .is_write = false, .qpc = 1, .event_filetime_100ns = 500 };
    try owner.observe(transfer, 710);
    var second = transfer;
    second.bytes = 50;
    try owner.observe(second, 750);
    var unknown_disk = transfer;
    unknown_disk.disk_number = null;
    unknown_disk.is_write = true;
    unknown_disk.bytes = 200;
    unknown_disk.event_filetime_100ns = 520;
    try owner.observe(unknown_disk, 820);
    try std.testing.expectEqual(@as(usize, 1), fixture.open_count);
    try std.testing.expectEqual(@as(usize, 1), fixture.creation_reads);
    try std.testing.expectEqual(@as(usize, 1), fixture.close_count);

    var processes: [8]disk.ProcessRow = undefined;
    var disk_rows: [8]disk.DiskRow = undefined;
    var disks: [4]u32 = undefined;
    const window = try owner.snapshot(2710, 0, &processes, &disk_rows, &disks);
    try std.testing.expect(window.complete);
    try std.testing.expectEqual(@as(usize, 1), window.processes.len);
    try std.testing.expectEqual(@as(usize, 1), window.disk_rows.len);
    try std.testing.expectEqualSlices(u32, &.{2}, window.disks);
    const process_row = window.processes[0];
    try std.testing.expect(process_row.available);
    try std.testing.expectEqual(@as(?u64, 400), process_row.process_birth_filetime_100ns);
    try std.testing.expectEqual(@as(u64, 150), process_row.read_bytes);
    try std.testing.expectEqual(@as(u64, 200), process_row.write_bytes);
    try std.testing.expectEqual(@as(u32, 2), process_row.read_operation_count);
    try std.testing.expectEqual(@as(u32, 1), process_row.write_operation_count);
    try std.testing.expectEqual(@as(f64, 75), process_row.read_bytes_per_second);
    try std.testing.expectEqual(@as(f64, 100), process_row.write_bytes_per_second);
    try std.testing.expect(window.disk_rows[0].available);
    try std.testing.expectEqual(@as(u32, 2), window.disk_rows[0].disk_number);
    try std.testing.expectEqual(@as(u64, 150), window.disk_rows[0].read_bytes);
    try std.testing.expectEqual(@as(u64, 0), window.disk_rows[0].write_bytes);
    try std.testing.expectEqual(@as(usize, 2), fixture.open_count);
    try std.testing.expectEqual(@as(usize, 3), fixture.creation_reads);
    try std.testing.expectEqual(@as(usize, 2), fixture.close_count);
    try std.testing.expect(owner.usage().peak_bytes >= owner.usage().current_bytes);
}

test "disk owner caches intake identity until process lifecycle invalidates it" {
    var fixture: IdentityFixture = .{ .birth = 100 };
    var owner = try disk.Owner.init(std.testing.allocator, .{ .maximum_active_pairs = 8, .maximum_disks = 4, .byte_cap = 1 << 20 }, identitySource(&fixture));
    defer owner.deinit();
    const event: decode.DiskTransfer = .{ .process_id = 7, .thread_id = 70, .bytes = 10, .disk_number = 1, .is_write = false, .qpc = 1, .event_filetime_100ns = 100 };
    try owner.observe(event, 100);
    try owner.observe(event, 110);
    try std.testing.expectEqual(@as(usize, 1), fixture.open_count);
    owner.processIdentityChanged(7);
    try owner.observe(event, 120);
    try std.testing.expectEqual(@as(usize, 2), fixture.open_count);
    try std.testing.expectEqual(@as(u64, 1), owner.process_revision);
}

test "disk owner repairs unknown birth only for future events after double verification" {
    var fixture: IdentityFixture = .{ .birth = 100, .fail_open_count = 1 };
    var owner = try disk.Owner.init(std.testing.allocator, .{ .maximum_active_pairs = 8, .maximum_disks = 4, .byte_cap = 1 << 20 }, identitySource(&fixture));
    defer owner.deinit();
    const event: decode.DiskTransfer = .{ .process_id = 7, .thread_id = 70, .bytes = 10, .disk_number = 1, .is_write = false, .qpc = 1, .event_filetime_100ns = 150 };
    try owner.observe(event, 100);
    var processes: [4]disk.ProcessRow = undefined;
    var disk_rows: [4]disk.DiskRow = undefined;
    var disks: [4]u32 = undefined;
    const first = try owner.snapshot(2100, 0, &processes, &disk_rows, &disks);
    try std.testing.expect(first.complete);
    try std.testing.expectEqual(@as(usize, 1), first.processes.len);
    try std.testing.expect(!first.processes[0].available);
    try std.testing.expectEqual(@as(usize, 2), fixture.creation_reads);
    try std.testing.expectEqual(@as(usize, 2), fixture.open_count);
    try std.testing.expectEqual(@as(usize, 1), fixture.close_count);

    var later = event;
    later.bytes = 20;
    later.event_filetime_100ns = 200;
    try owner.observe(later, 2200);
    try std.testing.expectEqual(@as(usize, 2), fixture.open_count);
    const next = try owner.snapshot(4200, 0, &processes, &disk_rows, &disks);
    try std.testing.expect(next.complete);
    try std.testing.expectEqual(@as(usize, 1), next.processes.len);
    try std.testing.expect(next.processes[0].available);
    try std.testing.expectEqual(@as(u64, 20), next.processes[0].read_bytes);
    try std.testing.expectEqual(@as(?u64, 100), next.processes[0].process_birth_filetime_100ns);
}

test "disk owner rejects a process whose identity changes on its final same-handle read" {
    var fixture: IdentityFixture = .{ .birth = 50, .change_birth_on_read = 3, .changed_birth = 51 };
    var owner = try disk.Owner.init(std.testing.allocator, .{ .maximum_active_pairs = 8, .maximum_disks = 4, .maximum_cached_processes = 8, .byte_cap = 1 << 20 }, identitySource(&fixture));
    defer owner.deinit();
    const event: decode.DiskTransfer = .{ .process_id = 7, .thread_id = 70, .bytes = 10, .disk_number = 1, .is_write = false, .qpc = 1, .event_filetime_100ns = 60 };
    try owner.observe(event, 100);
    var processes: [4]disk.ProcessRow = undefined;
    var disk_rows: [4]disk.DiskRow = undefined;
    var disks: [4]u32 = undefined;
    const window = try owner.snapshot(2100, 0, &processes, &disk_rows, &disks);
    try std.testing.expect(window.complete);
    try std.testing.expectEqual(@as(usize, 1), window.processes.len);
    try std.testing.expect(!window.processes[0].available);
    try std.testing.expectEqual(@as(usize, 1), window.disk_rows.len);
    try std.testing.expect(!window.disk_rows[0].available);
    try std.testing.expectEqual(@as(usize, 3), fixture.creation_reads);
    try std.testing.expectEqual(@as(usize, 2), fixture.close_count);
}

test "disk owner omits partial buckets and excludes completion before process birth" {
    var fixture: IdentityFixture = .{ .birth = 100 };
    var owner = try disk.Owner.init(std.testing.allocator, .{ .maximum_active_pairs = 8, .maximum_disks = 4, .byte_cap = 1 << 20 }, identitySource(&fixture));
    defer owner.deinit();
    const event: decode.DiskTransfer = .{ .process_id = 7, .thread_id = 70, .bytes = 10, .disk_number = 1, .is_write = false, .qpc = 1, .event_filetime_100ns = 90 };
    try owner.observe(event, 90);
    var later = event;
    later.qpc = 2;
    later.bytes = 20;
    later.event_filetime_100ns = 120;
    try owner.observe(later, 120);
    var old = event;
    old.process_id = 8;
    old.event_filetime_100ns = 99;
    try owner.observe(old, 180);
    var processes: [8]disk.ProcessRow = undefined;
    var disk_rows: [8]disk.DiskRow = undefined;
    var disks: [4]u32 = undefined;
    const window = try owner.snapshot(2095, 0, &processes, &disk_rows, &disks);
    try std.testing.expectEqual(@as(i64, 95), window.start_tick_ms);
    try std.testing.expectEqual(@as(usize, 1), window.processes.len);
    try std.testing.expectEqual(@as(u64, 20), window.processes[0].read_bytes);
    try std.testing.expectEqual(@as(u32, 7), window.processes[0].process_id);
}

test "disk owner exposes hard capacity failure and invalidates its current window" {
    var fixture: IdentityFixture = .{ .birth = 5 };
    var owner = try disk.Owner.init(std.testing.allocator, .{ .maximum_active_pairs = 1, .maximum_disks = 2, .byte_cap = 1 << 20 }, identitySource(&fixture));
    defer owner.deinit();
    const event: decode.DiskTransfer = .{ .process_id = 7, .thread_id = 1, .bytes = 1, .disk_number = 1, .is_write = false, .qpc = 1, .event_filetime_100ns = 5 };
    try owner.observe(event, 100);
    var second = event;
    second.process_id = 8;
    second.disk_number = null;
    try std.testing.expectError(error.CapacityExceeded, owner.observe(second, 110));
    try std.testing.expectEqual(@as(u64, 1), owner.usage().capacity_failures);
    var processes: [2]disk.ProcessRow = undefined;
    var disk_rows: [2]disk.DiskRow = undefined;
    var disks: [2]u32 = undefined;
    const window = try owner.snapshot(2000, 0, &processes, &disk_rows, &disks);
    try std.testing.expect(!window.complete);
}

fn networkTransfer(process_id: u32, bytes: u32, direction: decode.Direction, address: decode.Address, filetime: ?u64) decode.NetworkTransfer {
    return .{ .process_id = process_id, .bytes = bytes, .direction = direction, .protocol = .tcp, .remote_address = address, .qpc = 1, .event_filetime_100ns = filetime };
}

test "signed TraceEvent PID sentinels retain the managed attribution policy" {
    var fixture: IdentityFixture = .{ .birth = 5 };
    var disks = try disk.Owner.init(std.testing.allocator, .{ .maximum_active_pairs = 2, .maximum_disks = 2, .byte_cap = 1 << 20 }, identitySource(&fixture));
    defer disks.deinit();
    try disks.observe(.{ .process_id = 0x8000_0001, .thread_id = null, .bytes = 10, .disk_number = 1, .is_write = true, .qpc = 1, .event_filetime_100ns = 5 }, 100);
    var processes: [2]disk.ProcessRow = undefined;
    var disk_rows: [2]disk.DiskRow = undefined;
    var disk_numbers: [2]u32 = undefined;
    const window = try disks.snapshot(2000, 0, &processes, &disk_rows, &disk_numbers);
    try std.testing.expectEqual(@as(usize, 0), window.processes.len);
    try std.testing.expectEqual(@as(usize, 1), window.disk_rows.len);
    try std.testing.expectEqual(@as(u32, 0), window.disk_rows[0].process_id);
    try std.testing.expect(!window.disk_rows[0].available);
    try std.testing.expectEqual(@as(usize, 0), fixture.open_count);
    var networks = try network.Owner.init(std.testing.allocator, .{ .initial_event_capacity = 2, .maximum_retained_events = 4, .maximum_processes = 2, .byte_cap = 1 << 20 }, identitySource(&fixture));
    defer networks.deinit();
    try networks.observe(networkTransfer(0x8000_0001, 10, .send, .{ .ipv4 = .{ 192, 0, 2, 1 } }, 5), 100);
    try std.testing.expectEqual(@as(usize, 0), networks.event_count);
}

test "network owner aggregates raw and external totals with same-handle identity checks" {
    var fixture: IdentityFixture = .{ .birth = 1100 };
    var owner = try network.Owner.init(std.testing.allocator, .{ .initial_event_capacity = 2, .maximum_retained_events = 16, .maximum_processes = 4, .byte_cap = 1 << 20 }, identitySource(&fixture));
    defer owner.deinit();
    owner.invalidate(100);
    try owner.observe(networkTransfer(7, 100, .receive, .{ .ipv4 = .{ 198, 51, 100, 9 } }, 1200), 600);
    try owner.observe(networkTransfer(7, 200, .send, .{ .ipv4 = .{ 127, 0, 0, 1 } }, 1201), 650);
    try owner.observe(networkTransfer(7, 50, .send, .{ .ipv6 = .{ 0x20, 1, 0x0d, 0xb8, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1 } }, 1202), 700);
    var rows: [4]network.Row = undefined;
    const window = try owner.snapshot(2600, 100, &rows);
    try std.testing.expect(window.complete);
    try std.testing.expectEqual(@as(usize, 1), window.rows.len);
    const row = window.rows[0];
    try std.testing.expect(row.available);
    try std.testing.expectEqual(@as(?u64, 1100), row.process_birth_filetime_100ns);
    try std.testing.expectEqual(@as(u64, 100), row.receive_bytes);
    try std.testing.expectEqual(@as(u64, 250), row.send_bytes);
    try std.testing.expectEqual(@as(u32, 3), row.event_count);
    try std.testing.expectEqual(@as(u64, 100), row.external_receive_bytes);
    try std.testing.expectEqual(@as(u64, 50), row.external_send_bytes);
    try std.testing.expectEqual(@as(u32, 2), row.external_event_count);
    try std.testing.expectEqual(@as(usize, 1), fixture.open_count);
    try std.testing.expectEqual(@as(usize, 2), fixture.creation_reads);
    try std.testing.expectEqual(@as(usize, 1), fixture.close_count);
    try std.testing.expectEqual(@as(f64, 50), row.receive_bytes_per_second);
    try std.testing.expectEqual(@as(f64, 125), row.send_bytes_per_second);
}

test "network owner invalidates a PID when any event lacks FILETIME" {
    var fixture: IdentityFixture = .{ .birth = 90 };
    var owner = try network.Owner.init(std.testing.allocator, .{ .initial_event_capacity = 2, .maximum_retained_events = 8, .maximum_processes = 4, .byte_cap = 1 << 20 }, identitySource(&fixture));
    defer owner.deinit();
    owner.invalidate(50);
    try owner.observe(networkTransfer(7, 10, .send, .{ .ipv4 = .{ 192, 0, 2, 1 } }, 100), 100);
    try owner.observe(networkTransfer(7, 20, .receive, .{ .ipv4 = .{ 192, 0, 2, 2 } }, null), 110);
    var rows: [2]network.Row = undefined;
    const window = try owner.snapshot(2100, 50, &rows);
    try std.testing.expect(window.complete);
    try std.testing.expectEqual(@as(usize, 1), window.rows.len);
    try std.testing.expect(!window.rows[0].available);
    try std.testing.expectEqual(@as(u64, 0), window.rows[0].send_bytes);
    try std.testing.expectEqual(@as(usize, 2), fixture.creation_reads);
    try std.testing.expectEqual(@as(usize, 1), fixture.close_count);
}

test "network owner rejects a process whose identity changes on its final same-handle read" {
    var fixture: IdentityFixture = .{ .birth = 50, .change_birth_on_read = 2, .changed_birth = 51 };
    var owner = try network.Owner.init(std.testing.allocator, .{ .initial_event_capacity = 2, .maximum_retained_events = 8, .maximum_processes = 4, .byte_cap = 1 << 20 }, identitySource(&fixture));
    defer owner.deinit();
    owner.invalidate(10);
    try owner.observe(networkTransfer(7, 10, .send, .{ .ipv4 = .{ 192, 0, 2, 1 } }, 100), 100);
    var rows: [2]network.Row = undefined;
    const window = try owner.snapshot(2100, 10, &rows);
    try std.testing.expect(window.complete);
    try std.testing.expectEqual(@as(usize, 1), window.rows.len);
    try std.testing.expect(!window.rows[0].available);
    try std.testing.expectEqual(@as(usize, 2), fixture.creation_reads);
    try std.testing.expectEqual(@as(usize, 1), fixture.close_count);
}

test "network owner reports retained-event cap instead of hiding truncation" {
    var fixture: IdentityFixture = .{ .birth = 90 };
    var owner = try network.Owner.init(std.testing.allocator, .{ .initial_event_capacity = 2, .maximum_retained_events = 2, .maximum_processes = 2, .byte_cap = 1 << 20 }, identitySource(&fixture));
    defer owner.deinit();
    owner.invalidate(1);
    const event = networkTransfer(7, 1, .send, .{ .ipv4 = .{ 192, 0, 2, 1 } }, 100);
    try owner.observe(event, 2);
    try owner.observe(event, 3);
    try std.testing.expectError(error.CapacityExceeded, owner.observe(event, 4));
    try std.testing.expectEqual(@as(u64, 1), owner.usage().capacity_failures);
    try std.testing.expectEqual(@as(usize, 0), owner.event_count);
    var rows: [2]network.Row = undefined;
    const window = try owner.snapshot(2002, 1, &rows);
    try std.testing.expect(!window.complete);
}

test "disk allocations follow occupied data rather than configured maxima and return when unused" {
    var fixture: IdentityFixture = .{ .birth = 5 };
    var owner = try disk.Owner.init(std.testing.allocator, .{ .byte_cap = 8 * 1024 * 1024 }, identitySource(&fixture));
    defer owner.deinit();
    try std.testing.expectEqual(@as(usize, @sizeOf(disk.Owner)), owner.usage().current_bytes);
    const event: decode.DiskTransfer = .{ .process_id = 7, .thread_id = 1, .bytes = 4096, .disk_number = 2, .is_write = false, .qpc = 1, .event_filetime_100ns = 5 };
    try owner.observe(event, 1100);
    var processes: [4]disk.ProcessRow = undefined;
    var rows: [4]disk.DiskRow = undefined;
    var numbers: [4]u32 = undefined;
    const active = try owner.snapshot(3100, 1000, &processes, &rows, &numbers);
    try std.testing.expect(active.complete);
    try std.testing.expectEqual(@as(u64, 4096), active.processes[0].read_bytes);
    try std.testing.expect(owner.usage().current_bytes > @sizeOf(disk.Owner));
    try std.testing.expect(owner.usage().peak_bytes < 1024 * 1024);
    _ = try owner.snapshot(10000, 1000, &processes, &rows, &numbers);
    try std.testing.expectEqual(@as(usize, @sizeOf(disk.Owner)), owner.usage().current_bytes);
    try std.testing.expectEqual(@as(usize, 0), owner.prune_keys.len);
    try std.testing.expectEqual(@as(usize, 0), owner.active_process_ids.len);
    try owner.observe(event, 11000);
    owner.resetCapture();
    try std.testing.expectEqual(@as(usize, @sizeOf(disk.Owner)), owner.usage().current_bytes);
}

test "network allocations return after all retained input expires and can grow again" {
    var fixture: IdentityFixture = .{ .birth = 5 };
    var owner = try network.Owner.init(std.testing.allocator, .{ .initial_event_capacity = 2, .maximum_retained_events = 16, .maximum_processes = 4, .byte_cap = 1 << 20 }, identitySource(&fixture));
    defer owner.deinit();
    try std.testing.expectEqual(@as(usize, @sizeOf(network.Owner)), owner.usage().current_bytes);
    const event = networkTransfer(7, 4096, .receive, .{ .ipv4 = .{ 192, 0, 2, 1 } }, 5);
    try owner.observe(event, 1100);
    var rows: [4]network.Row = undefined;
    const active = try owner.snapshot(3100, 1000, &rows);
    try std.testing.expectEqual(@as(u64, 4096), active.rows[0].receive_bytes);
    _ = try owner.snapshot(10000, 1000, &rows);
    try std.testing.expectEqual(@as(usize, 0), owner.events.len);
    try std.testing.expectEqual(@as(usize, @sizeOf(network.Owner)), owner.usage().current_bytes);
    try owner.observe(event, 11001);
    const resumed = try owner.snapshot(13001, 1000, &rows);
    try std.testing.expectEqual(@as(u64, 4096), resumed.rows[0].receive_bytes);
}

fn occupiedDiskAllocationLifetime(allocator: std.mem.Allocator) !void {
    var fixture: IdentityFixture = .{ .birth = 5 };
    var owner = try disk.Owner.init(allocator, .{ .maximum_active_pairs = 8, .maximum_disks = 8, .maximum_cached_processes = 8, .byte_cap = 1 << 20 }, identitySource(&fixture));
    defer owner.deinit();
    for (0..3) |index| {
        try owner.observe(.{ .process_id = @as(u32, @intCast(7 + index)), .thread_id = null, .bytes = 4096, .disk_number = @as(u32, @intCast(index)), .is_write = false, .qpc = 1, .event_filetime_100ns = 5 }, @intCast(1100 + index * 100));
    }
    var processes: [8]disk.ProcessRow = undefined;
    var rows: [8]disk.DiskRow = undefined;
    var numbers: [8]u32 = undefined;
    _ = try owner.snapshot(3100, 1000, &processes, &rows, &numbers);
    _ = try owner.snapshot(10000, 1000, &processes, &rows, &numbers);
    owner.resetCapture();
}

fn occupiedNetworkAllocationLifetime(allocator: std.mem.Allocator) !void {
    var fixture: IdentityFixture = .{ .birth = 5 };
    var owner = try network.Owner.init(allocator, .{ .initial_event_capacity = 2, .maximum_retained_events = 16, .maximum_processes = 8, .byte_cap = 1 << 20 }, identitySource(&fixture));
    defer owner.deinit();
    for (0..3) |index| try owner.observe(networkTransfer(@intCast(7 + index), 4096, .receive, .{ .ipv4 = .{ 192, 0, 2, 1 } }, 5), @intCast(1100 + index));
    var rows: [8]network.Row = undefined;
    _ = try owner.snapshot(3100, 1000, &rows);
    _ = try owner.snapshot(10000, 1000, &rows);
}

test "occupied IO growth pruning snapshot and release survive every allocator failure" {
    try std.testing.checkAllAllocationFailures(std.testing.allocator, occupiedDiskAllocationLifetime, .{});
    try std.testing.checkAllAllocationFailures(std.testing.allocator, occupiedNetworkAllocationLifetime, .{});
}
