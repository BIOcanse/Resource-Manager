const std = @import("std");
const table = @import("table.zig");

fn word(bytes: []u8, offset: usize, value: u32) void {
    std.mem.writeInt(u32, bytes[offset..][0..4], value, .little);
}
fn endpoint(family: u16, port: u16) table.Endpoint {
    var result = table.Endpoint{ .address = @splat(0), .scope = 0, .port = port, .family = family };
    if (family == table.ipv4) result.address[0..4].* = .{ 127, 0, 0, 1 } else result.address[15] = 1;
    return result;
}
fn connection(family: u16) table.Connection {
    return .{ .client = endpoint(family, 41001), .server = endpoint(family, 9321) };
}
fn row(bytes: []u8, tuple: table.Connection, pid: u32) void {
    const six = tuple.client.family == table.ipv6;
    @memset(bytes, 0);
    word(bytes, if (six) 48 else 0, 5);
    word(bytes, if (six) 52 else 20, pid);
    const local_port: usize = if (six) 20 else 8;
    const remote_port: usize = if (six) 44 else 16;
    std.mem.writeInt(u16, bytes[local_port..][0..2], tuple.client.port, .big);
    std.mem.writeInt(u16, bytes[remote_port..][0..2], tuple.server.port, .big);
    if (six) {
        @memcpy(bytes[0..16], &tuple.client.address);
        @memcpy(bytes[24..40], &tuple.server.address);
        word(bytes, 16, tuple.client.scope);
        word(bytes, 40, tuple.server.scope);
    } else {
        @memcpy(bytes[4..8], tuple.client.address[0..4]);
        @memcpy(bytes[12..16], tuple.server.address[0..4]);
    }
}

test "connected client tuple selects the client rather than the reverse server row" {
    const families = [_]u16{ table.ipv4, table.ipv6 };
    var family_index: usize = 0;
    while (family_index < families.len) : (family_index += 1) {
        const tuple = connection(families[family_index]);
        const width: usize = if (tuple.client.family == table.ipv6) 56 else 24;
        var bytes: [4 + 2 * 56]u8 = undefined;
        word(&bytes, 0, 2);
        row(bytes[4..][0..width], .{ .client = tuple.server, .server = tuple.client }, 90);
        row(bytes[4 + width ..][0..width], tuple, 91);
        try std.testing.expectEqual(@as(?u32, 91), try table.find(bytes[0 .. 4 + 2 * width], tuple));
    }
}

test "malformed count truncated row and invalid endpoints fail closed" {
    var bytes: [28]u8 = @splat(0);
    const tuple = connection(table.ipv4);
    try std.testing.expectEqual(@as(?u32, null), try table.find(&bytes, tuple));
    word(&bytes, 0, 1);
    row(bytes[4..], tuple, 70);
    try std.testing.expectError(error.InvalidTable, table.find(bytes[0..27], tuple));
    word(&bytes, 0, std.math.maxInt(u32));
    try std.testing.expectError(error.InvalidTable, table.find(&bytes, tuple));
    try std.testing.expectError(error.InvalidTable, table.find(bytes[0..3], tuple));
    var bad = tuple;
    bad.server.family = table.ipv6;
    try std.testing.expectError(error.InvalidEndpoint, table.find(&bytes, bad));
    bad = tuple;
    bad.client.port = 0;
    try std.testing.expectError(error.InvalidEndpoint, table.find(&bytes, bad));
}

test "duplicate matching rows and invalid PIDs do not establish identity" {
    var bytes: [52]u8 = @splat(0);
    const tuple = connection(table.ipv4);
    word(&bytes, 0, 2);
    row(bytes[4..28], tuple, 70);
    row(bytes[28..52], tuple, 70);
    try std.testing.expectError(error.AmbiguousOwner, table.find(&bytes, tuple));
    word(&bytes, 48, 71);
    try std.testing.expectError(error.AmbiguousOwner, table.find(&bytes, tuple));
    word(&bytes, 0, 1);
    word(&bytes, 24, 0);
    try std.testing.expectError(error.AmbiguousOwner, table.find(&bytes, tuple));
    word(&bytes, 24, 0x80000000);
    try std.testing.expectError(error.AmbiguousOwner, table.find(&bytes, tuple));
}

test "state port address and IPv6 scope must all match" {
    var bytes: [60]u8 = @splat(0);
    var tuple = connection(table.ipv6);
    tuple.client.scope = 8;
    tuple.server.scope = 9;
    word(&bytes, 0, 1);
    row(bytes[4..], tuple, 70);
    try std.testing.expectEqual(@as(?u32, 70), try table.find(&bytes, tuple));
    var different = tuple;
    different.client.scope += 1;
    try std.testing.expectEqual(@as(?u32, null), try table.find(&bytes, different));
    different = tuple;
    different.server.port += 1;
    try std.testing.expectEqual(@as(?u32, null), try table.find(&bytes, different));
    different = tuple;
    different.client.address[0] = 4;
    try std.testing.expectEqual(@as(?u32, null), try table.find(&bytes, different));
    word(&bytes, 52, 2); // LISTEN cannot prove a connected peer.
    try std.testing.expectEqual(@as(?u32, null), try table.find(&bytes, tuple));
}
