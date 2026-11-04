const std = @import("std");
const decoder = @import("decoder.zig");
const path_mapping = @import("path_mapping.zig");

/// The task GUIDs written to EVENT_RECORD.EventHeader.ProviderId for classic
/// kernel events. TraceEvent's logical provider GUID is not present there.
pub const sysconfig_task_guid: decoder.Guid = .{
    .data1 = 0x9b79ee91,
    .data2 = 0xb5fd,
    .data3 = 0x41c0,
    .data4 = .{ 0xa2, 0x43, 0x42, 0x48, 0xe2, 0x66, 0xe9, 0xd0 },
};

pub const DecodeError = error{ TruncatedPayload, InvalidPayload };

/// A borrowed, validated UTF-16LE span. It remains valid only while the raw
/// EVENT_RECORD payload remains valid.
pub const WideSpan = struct {
    bytes: []const u8,

    pub fn len(self: WideSpan) usize {
        return self.bytes.len / 2;
    }

    pub fn copy(self: WideSpan, allocator: std.mem.Allocator) std.mem.Allocator.Error![]u16 {
        const result = try allocator.alloc(u16, self.len());
        for (result, 0..) |*unit, index| {
            unit.* = std.mem.readInt(u16, self.bytes[index * 2 ..][0..2], .little);
        }
        return result;
    }
};

pub const Event = union(enum) {
    system_paths: struct {
        system_directory: WideSpan,
        windows_directory: WideSpan,
    },
    volume_mapping: struct {
        nt_path: WideSpan,
        dos_path: WideSpan,
    },
};

const ParsedString = struct {
    value: WideSpan,
    next: usize,
};

fn wideString(payload: []const u8, offset: usize) DecodeError!ParsedString {
    if (offset > payload.len or offset % 2 != 0) return error.InvalidPayload;

    var end = offset;
    while (end <= payload.len and payload.len - end >= 2) : (end += 2) {
        if (payload[end] == 0 and payload[end + 1] == 0) {
            return .{
                .value = .{ .bytes = payload[offset..end] },
                .next = end + 2,
            };
        }
    }
    return error.TruncatedPayload;
}

/// Decodes the SysConfig records TraceEvent uses to maintain kernel path
/// namespaces. Spans borrow the input payload; call apply before that payload
/// expires so the PathMap can make owner-budgeted copies.
pub fn decode(raw: decoder.RawEvent) DecodeError!?Event {
    if (!raw.provider.eql(sysconfig_task_guid)) return null;

    return switch (raw.opcode) {
        33 => blk: {
            const system = try wideString(raw.payload, 0);
            const windows = try wideString(raw.payload, system.next);
            if (windows.next != raw.payload.len) return error.InvalidPayload;
            break :blk .{ .system_paths = .{
                .system_directory = system.value,
                .windows_directory = windows.value,
            } };
        },
        35 => blk: {
            const nt = try wideString(raw.payload, 0);
            const dos = try wideString(raw.payload, nt.next);
            if (dos.next != raw.payload.len) return error.InvalidPayload;
            break :blk .{ .volume_mapping = .{
                .nt_path = nt.value,
                .dos_path = dos.value,
            } };
        },
        else => null,
    };
}

/// Applies a decoded namespace record to the one persistent PathMap owner.
/// The PathMap copies both strings using its bounded owner allocator.
pub fn apply(event: Event, paths: *path_mapping.PathMap) !void {
    switch (event) {
        .system_paths => |value| try paths.addSystemPathsUtf16Le(value.system_directory.bytes, value.windows_directory.bytes),
        .volume_mapping => |value| try paths.addVolumeMappingUtf16Le(value.nt_path.bytes, value.dos_path.bytes),
    }
}

fn fixtureRaw(provider: decoder.Guid, opcode: u8, payload: []const u8) decoder.RawEvent {
    return .{
        .provider = provider,
        .opcode = opcode,
        .version = 0,
        .pointer_size = 8,
        .qpc = 1,
        .processor = 0,
        .header_process_id = 0,
        .header_thread_id = 0,
        .payload = payload,
    };
}

test "SysConfig namespace records decode only from their task GUID" {
    const system_paths = [_]u8{
        'C', 0, ':', 0, '\\', 0, 'W', 0, 'i', 0, 'n', 0, 'd', 0, 'o', 0, 'w', 0, 's', 0, '\\', 0, 'S', 0, 'y', 0, 's', 0, 't', 0, 'e', 0, 'm', 0, '3', 0, '2', 0, 0, 0,
        'C', 0, ':', 0, '\\', 0, 'W', 0, 'i', 0, 'n', 0, 'd', 0, 'o', 0, 'w', 0, 's', 0, 0,    0,
    };
    const decoded = (try decode(fixtureRaw(sysconfig_task_guid, 33, &system_paths))).?;
    switch (decoded) {
        .system_paths => |value| {
            try std.testing.expectEqualSlices(u8, system_paths[0..38], value.system_directory.bytes);
            try std.testing.expectEqualSlices(u8, system_paths[40..60], value.windows_directory.bytes);
            const copied = try value.windows_directory.copy(std.testing.allocator);
            defer std.testing.allocator.free(copied);
            try std.testing.expectEqualSlices(u16, &.{ 'C', ':', '\\', 'W', 'i', 'n', 'd', 'o', 'w', 's' }, copied);
        },
        else => return error.UnexpectedEventKind,
    }

    const volume = [_]u8{
        '\\', 0, 'D', 0, 'e',  0, 'v', 0, 'i', 0, 'c', 0, 'e', 0, '\\', 0, 'V', 0, 'o', 0, 'l', 0, 'u', 0, 'm', 0, 'e', 0, '1', 0, '\\', 0, 0, 0,
        'D',  0, ':', 0, '\\', 0, 0,   0,
    };
    const mapping = (try decode(fixtureRaw(sysconfig_task_guid, 35, &volume))).?;
    switch (mapping) {
        .volume_mapping => |value| {
            try std.testing.expectEqualSlices(u8, volume[0..32], value.nt_path.bytes);
            try std.testing.expectEqualSlices(u8, volume[34..40], value.dos_path.bytes);
        },
        else => return error.UnexpectedEventKind,
    }

    const trace_event_logical_guid: decoder.Guid = .{
        .data1 = 0x9e814aad,
        .data2 = 0x3204,
        .data3 = 0x11d2,
        .data4 = .{ 0x9a, 0x82, 0, 0x60, 8, 0xa8, 0x69, 0x39 },
    };
    try std.testing.expect((try decode(fixtureRaw(trace_event_logical_guid, 33, &system_paths))) == null);

    const file_io_task_guid: decoder.Guid = .{
        .data1 = 0x90cbdc39,
        .data2 = 0x4a3e,
        .data3 = 0x11d1,
        .data4 = .{ 0x84, 0xf4, 0x00, 0x00, 0xf8, 0x04, 0x64, 0xe3 },
    };
    try std.testing.expect((try decode(fixtureRaw(file_io_task_guid, 35, &volume))) == null);

    const system_config_task_guid: decoder.Guid = .{
        .data1 = 0x01853a65,
        .data2 = 0x418f,
        .data3 = 0x4f36,
        .data4 = .{ 0xae, 0xfc, 0xdc, 0x0f, 0x1d, 0x2f, 0xd2, 0x35 },
    };
    try std.testing.expect((try decode(fixtureRaw(system_config_task_guid, 33, &system_paths))) == null);
}

test "SysConfig namespace strings reject missing terminators and trailing data" {
    const missing_terminator = [_]u8{ 'C', 0, ':', 0 };
    try std.testing.expectError(error.TruncatedPayload, decode(fixtureRaw(sysconfig_task_guid, 33, &missing_terminator)));

    const extra = [_]u8{ 'C', 0, ':', 0, 0, 0, 'X', 0 };
    try std.testing.expectError(error.TruncatedPayload, decode(fixtureRaw(sysconfig_task_guid, 33, &extra)));

    const empty = [_]u8{};
    try std.testing.expect((try decode(fixtureRaw(sysconfig_task_guid, 34, &empty))) == null);
}
