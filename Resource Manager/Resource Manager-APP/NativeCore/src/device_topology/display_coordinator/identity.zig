const std = @import("std");
const protocol = @import("protocol.zig");

pub fn normalizedLength(kind: protocol.TextKind, input: []const u8) ?u32 {
    const trimmed = trimAscii(input);
    if (trimmed.len == 0) return null;
    const value = if (kind == .monitor_device_path and
        trimmed.len >= 4 and std.mem.eql(u8, trimmed[0..4], "\\\\?\\"))
        trimmed[4..]
    else
        trimmed;
    if (value.len == 0 or value.len > std.math.maxInt(u32)) return null;
    return @intCast(value.len);
}

pub fn normalize(
    kind: protocol.TextKind,
    input: []const u8,
    output: []u8,
) ?[]const u8 {
    const trimmed = trimAscii(input);
    const value = if (kind == .monitor_device_path and
        trimmed.len >= 4 and std.mem.eql(u8, trimmed[0..4], "\\\\?\\"))
        trimmed[4..]
    else
        trimmed;
    if (value.len == 0 or output.len < value.len) return null;
    for (value, 0..) |byte, index| {
        output[index] = switch (kind) {
            .monitor_device_path => if (byte == '#') '\\' else asciiUpper(byte),
            .source_device_name,
            .identity_token,
            .adapter_device_path,
            .adapter_match_token,
            => asciiUpper(byte),
            .friendly_name, .evidence => byte,
        };
    }
    const normalized = output[0..value.len];
    if (!std.unicode.utf8ValidateSlice(normalized) or
        std.mem.indexOfScalar(u8, normalized, 0) != null)
    {
        return null;
    }
    return normalized;
}

pub fn textHandle(kind: protocol.TextKind, bytes: []const u8) protocol.Handle128 {
    var hasher = std.crypto.hash.sha2.Sha256.init(.{});
    var kind_bytes: [4]u8 = undefined;
    std.mem.writeInt(u32, &kind_bytes, @intFromEnum(kind), .little);
    hasher.update("rm-display-text-v1");
    hasher.update(&kind_bytes);
    hasher.update(bytes);
    var digest: [32]u8 = undefined;
    hasher.final(&digest);
    return digestHandle(&digest);
}

pub fn targetHandle(adapter_luid: u64, target_id: u32) protocol.Handle128 {
    var bytes: [12]u8 = undefined;
    std.mem.writeInt(u64, bytes[0..8], adapter_luid, .little);
    std.mem.writeInt(u32, bytes[8..12], target_id, .little);
    return namespacedHandle("rm-display-target-v1", &bytes);
}

pub fn sourceObjectHandle(
    source: protocol.SourceId,
    source_object_key: u64,
) protocol.Handle128 {
    var bytes: [12]u8 = undefined;
    std.mem.writeInt(u32, bytes[0..4], @intFromEnum(source), .little);
    std.mem.writeInt(u64, bytes[4..12], source_object_key, .little);
    return namespacedHandle("rm-display-source-object-v1", &bytes);
}

pub fn nodeHandle(
    kind: protocol.NodeKind,
    identity_handle: protocol.Handle128,
) protocol.Handle128 {
    var bytes: [20]u8 = undefined;
    std.mem.writeInt(u32, bytes[0..4], @intFromEnum(kind), .little);
    std.mem.writeInt(u64, bytes[4..12], identity_handle.high, .little);
    std.mem.writeInt(u64, bytes[12..20], identity_handle.low, .little);
    return namespacedHandle("rm-display-node-v1", &bytes);
}

pub fn edgeHandle(
    parent: protocol.Handle128,
    child: protocol.Handle128,
) protocol.Handle128 {
    var bytes: [32]u8 = undefined;
    std.mem.writeInt(u64, bytes[0..8], parent.high, .little);
    std.mem.writeInt(u64, bytes[8..16], parent.low, .little);
    std.mem.writeInt(u64, bytes[16..24], child.high, .little);
    std.mem.writeInt(u64, bytes[24..32], child.low, .little);
    return namespacedHandle("rm-display-edge-v1", &bytes);
}

pub fn unresolvedHandle(value: *const protocol.UnresolvedOutput) protocol.Handle128 {
    return namespacedHandle("rm-display-unresolved-v1", std.mem.asBytes(value));
}

pub fn zeroHandle() protocol.Handle128 {
    return .{ .high = 0, .low = 0 };
}

fn namespacedHandle(namespace: []const u8, bytes: []const u8) protocol.Handle128 {
    var hasher = std.crypto.hash.sha2.Sha256.init(.{});
    hasher.update(namespace);
    hasher.update(bytes);
    var digest: [32]u8 = undefined;
    hasher.final(&digest);
    return digestHandle(&digest);
}

fn digestHandle(digest: *const [32]u8) protocol.Handle128 {
    var result = protocol.Handle128{
        .high = std.mem.readInt(u64, digest[0..8], .little),
        .low = std.mem.readInt(u64, digest[8..16], .little),
    };
    if (result.isZero()) result.low = 1;
    return result;
}

fn trimAscii(input: []const u8) []const u8 {
    var start: usize = 0;
    while (start < input.len and isAsciiWhitespace(input[start])) start += 1;
    var end = input.len;
    while (end > start and isAsciiWhitespace(input[end - 1])) end -= 1;
    return input[start..end];
}

fn asciiUpper(byte: u8) u8 {
    return if (byte >= 'a' and byte <= 'z') byte - ('a' - 'A') else byte;
}

fn isAsciiWhitespace(byte: u8) bool {
    return byte == ' ' or byte == '\t' or byte == '\r' or byte == '\n';
}
