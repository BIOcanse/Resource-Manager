const std = @import("std");
const c = @import("c.zig").win;
const paths = @import("path.zig");
const wide = std.unicode.utf8ToUtf16LeStringLiteral;
pub const service_name = wide("ResourceManager.Service");
pub const Identity = struct { pid: u32, created: u64 };
pub const Endpoint = struct { address: u32, port: u16 };
pub const Scratch = struct {
    expected: paths.Buffer = undefined,
    image: paths.Buffer = undefined,
    normalized: paths.Buffer = undefined,
    config: [8192]u8 align(8) = undefined,
    tcp: [256 * 1024]u8 align(8) = undefined,
};

pub fn capture(scratch: *Scratch, expected_path: []const u16, name: [:0]const u16) !Identity {
    const expected = try paths.full(expected_path, &scratch.expected);
    const manager = c.OpenSCManagerW(null, null, c.SC_MANAGER_CONNECT) orelse return error.ServiceManagerUnavailable;
    defer _ = c.CloseServiceHandle(manager);
    const service = c.OpenServiceW(manager, name.ptr, c.SERVICE_QUERY_CONFIG | c.SERVICE_QUERY_STATUS) orelse return error.ServiceUnavailable;
    defer _ = c.CloseServiceHandle(service);
    const pid = try servicePid(scratch, service, expected);
    const identity = try process(scratch, pid, expected);
    if (try servicePid(scratch, service, expected) != pid) return error.ServiceChanged;
    if (!verify(scratch, identity, expected)) return error.ProcessChanged;
    return identity;
}

fn servicePid(scratch: *Scratch, service: c.SC_HANDLE, expected: []const u16) !u32 {
    var needed: u32 = 0;
    if (c.QueryServiceConfigW(service, @ptrCast(&scratch.config), scratch.config.len, &needed) == 0) return error.ServiceConfigUnavailable;
    const config: *const c.QUERY_SERVICE_CONFIGW = @ptrCast(@alignCast(&scratch.config));
    const command = try boundedString(config.lpBinaryPathName, &scratch.config);
    const account = try boundedString(config.lpServiceStartName, &scratch.config);
    if (!paths.equal(account, wide("LocalSystem"))) return error.UntrustedServiceAccount;
    var binary = std.mem.trim(u16, command, wide(" \t\r\n"));
    if (binary.len >= 2 and binary[0] == '"' and binary[binary.len - 1] == '"') binary = binary[1 .. binary.len - 1];
    const normalized = try paths.full(binary, &scratch.normalized);
    if (!paths.equal(expected, normalized)) return error.UntrustedServicePath;
    var status: c.SERVICE_STATUS_PROCESS = undefined;
    if (c.QueryServiceStatusEx(service, c.SC_STATUS_PROCESS_INFO, @ptrCast(&status), @sizeOf(@TypeOf(status)), &needed) == 0) return error.ServiceStatusUnavailable;
    if (status.dwCurrentState != c.SERVICE_RUNNING or status.dwProcessId == 0 or status.dwProcessId > std.math.maxInt(i32)) return error.ServiceNotRunning;
    return status.dwProcessId;
}

fn boundedString(value: [*c]const u16, buffer: []const u8) ![]const u16 {
    if (value == null) return error.InvalidServiceConfig;
    const start = @intFromPtr(value);
    const base = @intFromPtr(buffer.ptr);
    if (start < base or start >= base + buffer.len or start % 2 != 0) return error.InvalidServiceConfig;
    const max = (base + buffer.len - start) / 2;
    var n: usize = 0;
    while (n < max) : (n += 1) if (value[n] == 0) return value[0..n];
    return error.InvalidServiceConfig;
}

fn process(scratch: *Scratch, pid: u32, expected: []const u16) !Identity {
    const handle = c.OpenProcess(c.PROCESS_QUERY_LIMITED_INFORMATION, 0, pid) orelse return error.ProcessUnavailable;
    defer _ = c.CloseHandle(handle);
    var created: c.FILETIME = undefined;
    var exited: c.FILETIME = undefined;
    var kernel: c.FILETIME = undefined;
    var user: c.FILETIME = undefined;
    if (c.GetProcessTimes(handle, &created, &exited, &kernel, &user) == 0) return error.ProcessUnavailable;
    var size: u32 = scratch.image.len;
    if (c.QueryFullProcessImageNameW(handle, 0, &scratch.image, &size) == 0 or size >= scratch.image.len) return error.ProcessPathUnavailable;
    const image = try paths.full(scratch.image[0..size], &scratch.normalized);
    if (!paths.equal(image, expected)) return error.UntrustedProcessPath;
    return .{ .pid = pid, .created = (@as(u64, created.dwHighDateTime) << 32) | created.dwLowDateTime };
}

pub fn verify(scratch: *Scratch, expected: Identity, expected_path: []const u16) bool {
    const path = paths.full(expected_path, &scratch.expected) catch return false;
    const actual = process(scratch, expected.pid, path) catch return false;
    return std.meta.eql(actual, expected);
}

/// Call only with the endpoints of the actual connection being authenticated.
/// A separate preflight socket never attests a later WinHTTP connection.
pub fn verifyPeer(scratch: *Scratch, expected: Identity, expected_path: []const u16, client: Endpoint, server: Endpoint) bool {
    if (!loopback(client.address) or !loopback(server.address)) return false;
    return findServer(scratch, client, server) == expected.pid and verify(scratch, expected, expected_path) and findServer(scratch, client, server) == expected.pid;
}
fn loopback(address: u32) bool {
    return address & 0xff == 127;
}
fn port(value: u32) u16 {
    return @intCast(((value & 0xff) << 8) | ((value >> 8) & 0xff));
}
fn findServer(scratch: *Scratch, client: Endpoint, server: Endpoint) u32 {
    var size: u32 = scratch.tcp.len;
    if (c.GetExtendedTcpTable(&scratch.tcp, &size, 0, c.AF_INET, c.TCP_TABLE_OWNER_PID_CONNECTIONS, 0) != 0 or size > scratch.tcp.len or size < 4) return 0;
    const count = std.mem.readInt(u32, scratch.tcp[0..4], .little);
    const Row = c.MIB_TCPROW_OWNER_PID;
    if (count > (size - 4) / @sizeOf(Row)) return 0;
    var found: u32 = 0;
    var index: usize = 0;
    while (index < count) : (index += 1) {
        const row: *align(1) const Row = @ptrCast(scratch.tcp[4 + index * @sizeOf(Row) ..].ptr);
        if (row.dwState != c.MIB_TCP_STATE_ESTAB or row.dwLocalAddr != server.address or row.dwRemoteAddr != client.address or port(row.dwLocalPort) != server.port or port(row.dwRemotePort) != client.port) continue;
        if (found != 0 or row.dwOwningPid == 0 or row.dwOwningPid > std.math.maxInt(i32)) return 0;
        found = row.dwOwningPid;
    }
    return found;
}

test "TCP address and network byte order match loopback contract" {
    try std.testing.expect(loopback(0x0100007f));
    try std.testing.expect(!loopback(0x0100000a));
    try std.testing.expectEqual(@as(u16, 9321), port(0x6924));
}
