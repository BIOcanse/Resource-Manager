const std = @import("std");
const event_decoder = @import("decoder.zig");

/// Classic Windows kernel providers used by TraceEvent's DiskIO/TcpIp/UdpIp
/// parser. Classic EVENT_RECORD.ProviderId is the task GUID. TraceEvent calls
// 9e814aad-... the logical provider, but that is not what ProcessTrace emits.
pub const disk_io_task_guid = event_decoder.Guid{
    .data1 = 0x3d6fa8d4,
    .data2 = 0xfe05,
    .data3 = 0x11d0,
    .data4 = .{ 0x9d, 0xda, 0x00, 0xc0, 0x4f, 0xd7, 0xba, 0x7c },
};
pub const tcp_ip_task_guid = event_decoder.Guid{
    .data1 = 0x9a280ac0,
    .data2 = 0xc8e0,
    .data3 = 0x11d1,
    .data4 = .{ 0x84, 0xe2, 0x00, 0xc0, 0x4f, 0xb9, 0x98, 0xa2 },
};
pub const udp_ip_task_guid = event_decoder.Guid{
    .data1 = 0xbf3a50c5,
    .data2 = 0xa9c9,
    .data3 = 0x4988,
    .data4 = .{ 0xa0, 0x05, 0x2d, 0xf0, 0xb7, 0xc8, 0x0f, 0x80 },
};

pub const RawEvent = event_decoder.RawEvent;
pub const Direction = enum { send, receive };
pub const Protocol = enum { tcp, udp };
pub const Address = union(enum) { ipv4: [4]u8, ipv6: [16]u8 };

pub const DiskTransfer = struct {
    process_id: ?u32,
    thread_id: ?u32,
    bytes: u32,
    disk_number: ?u32,
    is_write: bool,
    qpc: i64,
    event_filetime_100ns: ?u64,
};

pub const NetworkTransfer = struct {
    process_id: ?u32,
    thread_id: ?u32 = null,
    bytes: u32,
    direction: Direction,
    protocol: Protocol,
    remote_address: Address,
    qpc: i64,
    event_filetime_100ns: ?u64,
};

pub const Event = union(enum) { disk: DiskTransfer, network: NetworkTransfer };
pub const DecodeError = error{ UnsupportedVersion, InvalidPointerSize, TruncatedPayload, InvalidPayload, InvalidTimestamp };

fn uint(comptime T: type, bytes: []const u8, offset: usize) DecodeError!T {
    if (offset > bytes.len or bytes.len - offset < @sizeOf(T)) return error.TruncatedPayload;
    return std.mem.readInt(T, bytes[offset..][0..@sizeOf(T)], .little);
}

fn nullableHeaderId(value: u32) ?u32 {
    return if (value == std.math.maxInt(u32)) null else value;
}

fn hostOffset(offset: usize, pointer_size: u8, pointer_fields: usize) usize {
    return offset + (@as(usize, pointer_size) - 4) * pointer_fields;
}

fn ipv4(bytes: []const u8, offset: usize) DecodeError!Address {
    // TraceEvent reads the field as a little-endian ULONG and exposes that
    // ULONG's little-endian address bytes through IPAddress(uint).
    if (offset > bytes.len or bytes.len - offset < 4) return error.TruncatedPayload;
    return .{ .ipv4 = bytes[offset..][0..4].* };
}

fn ipv6(bytes: []const u8, offset: usize) DecodeError!Address {
    if (offset > bytes.len or bytes.len - offset < 16) return error.TruncatedPayload;
    return .{ .ipv6 = bytes[offset..][0..16].* };
}

fn payloadPid(bytes: []const u8, offset: usize) DecodeError!?u32 {
    const value = try uint(u32, bytes, offset);
    return nullableHeaderId(value);
}

fn disk(raw: RawEvent, filetime: ?u64) DecodeError!?Event {
    if (raw.opcode != 10 and raw.opcode != 11) return null;
    if (raw.pointer_size != 4 and raw.pointer_size != 8) return error.InvalidPointerSize;
    const required = hostOffset(40, raw.pointer_size, 2);
    if (raw.payload.len < required) return error.TruncatedPayload;

    const disk_number = @as(i32, @bitCast(try uint(u32, raw.payload, 0)));
    const issue_tid_offset = hostOffset(40, raw.pointer_size, 2);
    const issue_tid_end = hostOffset(44, raw.pointer_size, 2);
    const thread_id = if (raw.header_thread_id != std.math.maxInt(u32))
        nullableHeaderId(raw.header_thread_id)
    else if (issue_tid_end <= raw.payload.len)
        try payloadPid(raw.payload, issue_tid_offset)
    else
        null;

    return .{
        .disk = .{
            .process_id = nullableHeaderId(raw.header_process_id),
            .thread_id = thread_id,
            // TransferSize is a signed C# Int32 view of an ULONG. Preserve its bits.
            .bytes = try uint(u32, raw.payload, 8),
            .disk_number = if (disk_number < 0) null else @intCast(disk_number),
            .is_write = raw.opcode == 11,
            .qpc = raw.qpc,
            .event_filetime_100ns = filetime,
        },
    };
}

fn network(raw: RawEvent, protocol: Protocol, filetime: ?u64) DecodeError!?Event {
    const task_guid = if (protocol == .tcp) tcp_ip_task_guid else udp_ip_task_guid;
    if (!raw.provider.eql(task_guid)) return null;
    if (raw.opcode != 10 and raw.opcode != 11 and raw.opcode != 26 and raw.opcode != 27) return null;
    if (raw.pointer_size != 4 and raw.pointer_size != 8) return error.InvalidPointerSize;

    const send = raw.opcode == 10 or raw.opcode == 26;
    const receive = raw.opcode == 11 or raw.opcode == 27;
    if (!send and !receive) return null;
    const direction: Direction = if (send) .send else .receive;
    var pid = nullableHeaderId(raw.header_process_id);
    var bytes: u32 = 0;
    var remote: Address = undefined;
    const is_tcp = protocol == .tcp;

    if (is_tcp and raw.opcode == 10) {
        pid = try payloadPid(raw.payload, 0);
        bytes = try uint(u32, raw.payload, 4);
        remote = try ipv4(raw.payload, 8);
    } else if (is_tcp and raw.opcode == 11) {
        const size_offset: usize = if (raw.version >= 1) 4 else 12;
        const remote_offset: usize = if (raw.version >= 1) 12 else 4;
        const pid_offset: usize = if (raw.version >= 1) 0 else 16;
        pid = try payloadPid(raw.payload, pid_offset);
        bytes = try uint(u32, raw.payload, size_offset);
        remote = try ipv4(raw.payload, remote_offset);
    } else if (is_tcp and raw.opcode == 26) {
        pid = try payloadPid(raw.payload, 0);
        bytes = try uint(u32, raw.payload, 4);
        remote = try ipv6(raw.payload, 8);
    } else if (is_tcp and raw.opcode == 27) {
        pid = try payloadPid(raw.payload, 0);
        bytes = try uint(u32, raw.payload, 4);
        remote = try ipv6(raw.payload, 24);
    } else if (!is_tcp and (raw.opcode == 10 or raw.opcode == 11)) {
        if (raw.version == 0) {
            const source_offset = hostOffset(4, raw.pointer_size, 1);
            const size_offset = hostOffset(10, raw.pointer_size, 1);
            const destination_offset = hostOffset(12, raw.pointer_size, 1);
            const minimum = hostOffset(20, raw.pointer_size, 1);
            if (raw.payload.len < minimum) return error.TruncatedPayload;
            const signed_size = try uint(i16, raw.payload, size_offset);
            bytes = @bitCast(@as(i32, signed_size));
            remote = try ipv4(raw.payload, if (send) destination_offset else source_offset);
        } else {
            pid = try payloadPid(raw.payload, 0);
            bytes = try uint(u32, raw.payload, 4);
            remote = try ipv4(raw.payload, if (send) 8 else 12);
        }
    } else if (!is_tcp and raw.opcode == 26) {
        pid = try payloadPid(raw.payload, 0);
        bytes = try uint(u32, raw.payload, 4);
        remote = try ipv6(raw.payload, 8);
    } else if (!is_tcp and raw.opcode == 27) {
        pid = try payloadPid(raw.payload, 0);
        bytes = try uint(u32, raw.payload, 4);
        remote = try ipv6(raw.payload, 24);
    } else return null;

    return .{ .network = .{
        .process_id = pid,
        .thread_id = nullableHeaderId(raw.header_thread_id),
        .bytes = bytes,
        .direction = direction,
        .protocol = protocol,
        .remote_address = remote,
        .qpc = raw.qpc,
        .event_filetime_100ns = filetime,
    } };
}

/// null means a different kernel record. A recognized malformed record fails
/// instead of supplying zero values to either aggregate.
pub fn decode(raw: RawEvent, event_filetime_100ns: ?u64) DecodeError!?Event {
    if (raw.provider.eql(disk_io_task_guid)) {
        if (raw.qpc < 0) return error.InvalidTimestamp;
        return disk(raw, event_filetime_100ns);
    }
    if (raw.provider.eql(tcp_ip_task_guid)) {
        if (raw.qpc < 0) return error.InvalidTimestamp;
        return network(raw, .tcp, event_filetime_100ns);
    }
    if (raw.provider.eql(udp_ip_task_guid)) {
        if (raw.qpc < 0) return error.InvalidTimestamp;
        return network(raw, .udp, event_filetime_100ns);
    }
    return null;
}

pub fn leavesHost(address: Address) bool {
    return switch (address) {
        .ipv4 => |v| !(v[0] == 127 or (v[0] == 0 and v[1] == 0 and v[2] == 0 and v[3] == 0) or
            (v[0] == 255 and v[1] == 255 and v[2] == 255 and v[3] == 255)),
        .ipv6 => |v| blk: {
            var all_zero = true;
            for (v) |byte| all_zero = all_zero and byte == 0;
            var prefix15_zero = true;
            for (v[0..15]) |byte| prefix15_zero = prefix15_zero and byte == 0;
            // .NET IPAddress.IsLoopback recognizes only mapped 127.0.0.1;
            // its IPv4 127/8 rule does not apply to other mapped addresses.
            const mapped_loopback = std.mem.eql(u8, &v, &.{ 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0xff, 0xff, 127, 0, 0, 1 });
            const loopback = all_zero or (prefix15_zero and v[15] == 1) or mapped_loopback;
            break :blk !loopback;
        },
    };
}
