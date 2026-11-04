const core = @import("../core/table.zig");
const runtime = @import("../runtime/lookup.zig");

extern "iphlpapi" fn GetExtendedTcpTable(?[*]u8, *u32, c_int, u32, u32, u32) callconv(.winapi) u32;
fn query(_: ?*anyopaque, buffer: ?[*]u8, length: *u32, family: u16) u32 {
    // TCP_TABLE_OWNER_PID_CONNECTIONS is a Win32 ABI identifier, not policy.
    return GetExtendedTcpTable(buffer, length, 0, family, 4, 0);
}

pub export fn rm_loopback_tcp_find(host: ?*const runtime.Host, connection: ?*const core.Connection, maximum_bytes: u32, result: ?*runtime.Result) callconv(.c) void {
    const output = result orelse return;
    output.* = .{ .status = .invalid_input };
    const callbacks = host orelse return;
    const tuple = connection orelse return;
    output.* = runtime.find(callbacks.*, .{ .context = null, .call = query }, tuple.*, maximum_bytes);
}

test "ABI sizes" {
    const std = @import("std");
    try std.testing.expectEqual(@as(usize, 24), @sizeOf(core.Endpoint));
    try std.testing.expectEqual(@as(usize, 48), @sizeOf(core.Connection));
    try std.testing.expectEqual(@as(usize, 40), @sizeOf(runtime.Host));
    try std.testing.expectEqual(@as(usize, 24), @sizeOf(runtime.Result));
}
