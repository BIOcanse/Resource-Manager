const std = @import("std");
const c = @import("c.zig").win;
const identity = @import("identity.zig");

pub const maximum_body_bytes = 16 * 1024;
const maximum_header_bytes = 8 * 1024;
pub const Scratch = struct {
    wire: [4096]u8 = undefined,
    line: [maximum_header_bytes]u8 = undefined,
    body: [maximum_body_bytes]u8 = undefined,
    json: [64 * 1024]u8 = undefined,
};

const Gate = struct {
    scratch: *identity.Scratch,
    expected: identity.Identity,
    path: []const u16,
    service_name: [:0]const u16,
    fn verify(self: Gate, client: identity.Endpoint, server: identity.Endpoint) !void {
        const current = try identity.capture(self.scratch, self.path, self.service_name);
        if (!std.meta.eql(current, self.expected)) return error.ServiceChanged;
        if (!identity.verifyPeer(self.scratch, self.expected, self.path, client, server)) return error.UntrustedConnectedPeer;
    }
};

/// The connected socket is attested before the first HTTP byte is written.
/// No proxy, redirects, pooling, cookies, decompression or transport fallback.
pub fn authenticated(stop: c.HANDLE, port: u16, timeout_ms: u32, scratch: *Scratch, process_scratch: *identity.Scratch, expected: identity.Identity, expected_path: []const u16, service_name: [:0]const u16, key: []const u8) !void {
    try exchange(stop, port, timeout_ms, scratch, Gate{ .scratch = process_scratch, .expected = expected, .path = expected_path, .service_name = service_name }, key);
}

const Connection = struct {
    socket: c.SOCKET,
    event: c.WSAEVENT,
    stop: c.HANDLE,
    deadline: u64,
    fn check(self: *Connection) !void {
        if (c.WaitForSingleObject(self.stop, 0) != c.WAIT_TIMEOUT) return error.Stopped;
        if (c.GetTickCount64() >= self.deadline) return error.ProbeTimedOut;
    }
    fn wait(self: *Connection) !void {
        try self.check();
        const now = c.GetTickCount64();
        if (now >= self.deadline) return error.ProbeTimedOut;
        const handles = [_]c.HANDLE{ self.stop, self.event };
        const result = c.WaitForMultipleObjects(handles.len, &handles, 0, @intCast(@min(self.deadline - now, std.math.maxInt(u32) - 1)));
        if (result == c.WAIT_OBJECT_0) return error.Stopped;
        if (result == c.WAIT_TIMEOUT) return error.ProbeTimedOut;
        if (result != c.WAIT_OBJECT_0 + 1) return error.SocketWaitFailed;
        var events: c.WSANETWORKEVENTS = std.mem.zeroes(c.WSANETWORKEVENTS);
        if (c.WSAEnumNetworkEvents(self.socket, self.event, &events) != 0) return error.SocketWaitFailed;
        // Winsock defines an error entry only when its event bit is set.
        // Unreported entries must not be interpreted as connection failures.
        for (events.iErrorCode, 0..) |failure, index| {
            if (events.lNetworkEvents & (@as(c_long, 1) << @intCast(index)) != 0 and failure != 0) return error.SocketIoFailed;
        }
    }
    fn write(self: *Connection, bytes: []const u8) !void {
        var sent: usize = 0;
        while (sent < bytes.len) {
            try self.check();
            const n = c.send(self.socket, @ptrCast(bytes[sent..].ptr), @intCast(bytes.len - sent), 0);
            if (n > 0) {
                sent += @intCast(n);
            } else if (n == c.SOCKET_ERROR and c.WSAGetLastError() == c.WSAEWOULDBLOCK) {
                try self.wait();
            } else return error.SocketSendFailed;
        }
    }
    fn read(self: *Connection, bytes: []u8) !usize {
        while (true) {
            try self.check();
            const n = c.recv(self.socket, @ptrCast(bytes.ptr), @intCast(bytes.len), 0);
            if (n >= 0) return @intCast(n);
            if (c.WSAGetLastError() != c.WSAEWOULDBLOCK) return error.SocketReceiveFailed;
            try self.wait();
        }
    }
};

fn endpoint(socket: c.SOCKET, remote: bool) !identity.Endpoint {
    var address: c.SOCKADDR_IN = std.mem.zeroes(c.SOCKADDR_IN);
    var length: c_int = @sizeOf(@TypeOf(address));
    const result = if (remote) c.getpeername(socket, @ptrCast(&address), &length) else c.getsockname(socket, @ptrCast(&address), &length);
    if (result != 0 or length != @sizeOf(@TypeOf(address)) or address.sin_family != c.AF_INET) return error.PeerUnavailable;
    return .{ .address = address.sin_addr.S_un.S_addr, .port = std.mem.bigToNative(u16, address.sin_port) };
}

fn exchange(stop: c.HANDLE, port: u16, timeout_ms: u32, scratch: *Scratch, gate: anytype, key: []const u8) !void {
    if (port == 0 or timeout_ms == 0 or timeout_ms > std.math.maxInt(i32)) return error.InvalidTransportConfig;
    if (c.WaitForSingleObject(stop, 0) != c.WAIT_TIMEOUT) return error.Stopped;
    // Only validated key syntax can enter a request header, including the
    // explicitly empty credential supported by the backend's default-key file.
    var decoded = try @import("key_reader.zig").decode(key);
    defer decoded.clear();
    var data: c.WSADATA = undefined;
    if (c.WSAStartup(0x0202, &data) != 0) return error.WinsockUnavailable;
    defer _ = c.WSACleanup();
    const socket = c.WSASocketW(c.AF_INET, c.SOCK_STREAM, c.IPPROTO_TCP, null, 0, c.WSA_FLAG_NO_HANDLE_INHERIT);
    if (socket == c.INVALID_SOCKET) return error.SocketUnavailable;
    defer _ = c.closesocket(socket);
    const event = c.WSACreateEvent();
    // MinGW's cast macro is not translated correctly by Zig 0.16.
    if (event == null) return error.SocketEventUnavailable;
    defer _ = c.WSACloseEvent(event);
    if (c.WSAEventSelect(socket, event, c.FD_CONNECT | c.FD_READ | c.FD_WRITE | c.FD_CLOSE) != 0) return error.SocketEventUnavailable;
    defer _ = c.WSAEventSelect(socket, null, 0);
    var connection: Connection = .{ .socket = socket, .event = event, .stop = stop, .deadline = c.GetTickCount64() + timeout_ms };
    var address: c.SOCKADDR_IN = std.mem.zeroes(c.SOCKADDR_IN);
    address.sin_family = c.AF_INET;
    address.sin_addr.S_un.S_addr = 0x0100007f;
    address.sin_port = std.mem.nativeToBig(u16, port);
    if (c.connect(socket, @ptrCast(&address), @sizeOf(@TypeOf(address))) != 0) {
        if (c.WSAGetLastError() != c.WSAEWOULDBLOCK) return error.SocketConnectFailed;
        try connection.wait();
    }
    const client = try endpoint(socket, false);
    const server = try endpoint(socket, true);
    if (server.address != address.sin_addr.S_un.S_addr or server.port != port) return error.UntrustedConnectedPeer;
    try gate.verify(client, server);
    try connection.check();
    // No request or credential write exists above the actual-socket gate.
    var request: [512]u8 = undefined;
    defer std.crypto.secureZero(u8, &request);
    const bytes = if (decoded.len == 0)
        try std.fmt.bufPrint(&request, "GET /api/runtime/capabilities HTTP/1.1\r\nHost: 127.0.0.1:{d}\r\nAccept: application/json\r\nConnection: close\r\n\r\n", .{port})
    else
        try std.fmt.bufPrint(&request, "GET /api/runtime/capabilities HTTP/1.1\r\nHost: 127.0.0.1:{d}\r\nAccept: application/json\r\nConnection: close\r\nX-Resource-Manager-Key: {s}\r\n\r\n", .{ port, decoded.value() });
    try connection.write(bytes);
    var reader: Reader = .{ .connection = &connection, .scratch = scratch };
    const body = try reader.response();
    try validateCapabilities(body, &scratch.json);
    try connection.check();
}

const Reader = struct {
    connection: *Connection,
    scratch: *Scratch,
    start: usize = 0,
    end: usize = 0,
    header_bytes: usize = 0,
    fn byte(self: *Reader) !?u8 {
        if (self.start == self.end) {
            self.start = 0;
            self.end = try self.connection.read(&self.scratch.wire);
            if (self.end == 0) return null;
        }
        const value = self.scratch.wire[self.start];
        self.start += 1;
        return value;
    }
    fn line(self: *Reader) ![]const u8 {
        var length: usize = 0;
        while (length < self.scratch.line.len) {
            const value = (try self.byte()) orelse return error.TruncatedHttpResponse;
            self.header_bytes += 1;
            if (self.header_bytes > maximum_header_bytes) return error.HttpHeadersTooLarge;
            if (value == '\n') {
                if (length == 0 or self.scratch.line[length - 1] != '\r') return error.InvalidHttpResponse;
                return self.scratch.line[0 .. length - 1];
            }
            if (value < 0x20 and value != '\r' and value != '\t') return error.InvalidHttpResponse;
            self.scratch.line[length] = value;
            length += 1;
        }
        return error.HttpHeadersTooLarge;
    }
    fn exact(self: *Reader, bytes: []u8) !void {
        for (bytes) |*value| value.* = (try self.byte()) orelse return error.TruncatedHttpResponse;
    }
    fn response(self: *Reader) ![]const u8 {
        const status = try self.line();
        if (!(std.mem.startsWith(u8, status, "HTTP/1.1 ") or std.mem.startsWith(u8, status, "HTTP/1.0 ")) or status.len < 12 or !std.mem.eql(u8, status[9..12], "200") or (status.len > 12 and status[12] != ' ')) return error.BackendHttpStatus;
        var content_length: ?usize = null;
        var json = false;
        var chunked = false;
        while (true) {
            const header = try self.line();
            if (header.len == 0) break;
            const colon = std.mem.indexOfScalar(u8, header, ':') orelse return error.InvalidHttpResponse;
            const name = header[0..colon];
            const value = std.mem.trim(u8, header[colon + 1 ..], " \t");
            if (std.ascii.eqlIgnoreCase(name, "Content-Length")) {
                if (content_length != null or value.len == 0) return error.InvalidHttpFraming;
                content_length = try httpNumber(value, 10);
                if (content_length.? > maximum_body_bytes) return error.HttpBodyTooLarge;
            } else if (std.ascii.eqlIgnoreCase(name, "Transfer-Encoding")) {
                if (chunked or !std.ascii.eqlIgnoreCase(value, "chunked")) return error.InvalidHttpFraming;
                chunked = true;
            } else if (std.ascii.eqlIgnoreCase(name, "Content-Type")) {
                if (json) return error.InvalidHttpResponse;
                const media_end = std.mem.indexOfScalar(u8, value, ';') orelse value.len;
                if (!std.ascii.eqlIgnoreCase(std.mem.trim(u8, value[0..media_end], " \t"), "application/json")) return error.InvalidContentType;
                json = true;
            } else if (std.ascii.eqlIgnoreCase(name, "Content-Encoding") and !std.ascii.eqlIgnoreCase(value, "identity")) return error.UnsupportedContentEncoding;
        }
        if (!json) return error.InvalidContentType;
        if (chunked and content_length != null) return error.InvalidHttpFraming;
        var length: usize = 0;
        if (chunked) {
            while (true) {
                const chunk = try self.line();
                const end = std.mem.indexOfScalar(u8, chunk, ';') orelse chunk.len;
                if (end == 0) return error.InvalidHttpFraming;
                const count = try httpNumber(chunk[0..end], 16);
                if (count > maximum_body_bytes - length) return error.HttpBodyTooLarge;
                if (count == 0) {
                    while ((try self.line()).len != 0) {}
                    break;
                }
                try self.exact(self.scratch.body[length..][0..count]);
                length += count;
                var crlf: [2]u8 = undefined;
                try self.exact(&crlf);
                if (!std.mem.eql(u8, &crlf, "\r\n")) return error.InvalidHttpFraming;
            }
        } else if (content_length) |count| {
            try self.exact(self.scratch.body[0..count]);
            length = count;
        } else {
            while (try self.byte()) |value| {
                if (length == maximum_body_bytes) return error.HttpBodyTooLarge;
                self.scratch.body[length] = value;
                length += 1;
            }
        }
        return self.scratch.body[0..length];
    }
};

fn httpNumber(bytes: []const u8, comptime base: u8) !usize {
    if (bytes.len == 0) return error.InvalidHttpFraming;
    for (bytes) |value| {
        if (if (base == 16) !std.ascii.isHex(value) else !std.ascii.isDigit(value)) return error.InvalidHttpFraming;
    }
    return std.fmt.parseInt(usize, bytes, base) catch error.InvalidHttpFraming;
}

const Capabilities = struct {
    profileId: []const u8,
    readOnly: bool = false,
    mutablePersistence: bool = false,
    legacyPersistenceImport: bool = false,
    gpuLaunchInterceptionReconciliation: bool = false,
    runtimeEffectOwners: bool = false,
    publicServiceCoordination: bool = false,
    optimizationRuntime: bool = false,
    sharedResourceOwnership: bool = false,
};
fn validateCapabilities(bytes: []const u8, storage: []u8) !void {
    var allocator = std.heap.FixedBufferAllocator.init(storage);
    const parsed = std.json.parseFromSlice(Capabilities, allocator.allocator(), bytes, .{ .allocate = .alloc_if_needed }) catch return error.InvalidCapabilities;
    defer parsed.deinit();
    // Match C# string.IsNullOrWhiteSpace, including non-ASCII JSON text.
    const view = std.unicode.Utf8View.init(parsed.value.profileId) catch return error.InvalidCapabilities;
    var iterator = view.iterator();
    while (iterator.nextCodepoint()) |point| {
        const whitespace = switch (point) {
            0x09...0x0d, 0x20, 0x85, 0xa0, 0x1680, 0x2000...0x200a, 0x2028, 0x2029, 0x202f, 0x205f, 0x3000 => true,
            else => false,
        };
        if (!whitespace) return;
    }
    return error.InvalidCapabilities;
}

test "capabilities enforce the bounded typed C# probe contract" {
    var storage: [64 * 1024]u8 = undefined;
    try validateCapabilities("{\"profileId\":\"normal\",\"readOnly\":true}", &storage);
    try std.testing.expectError(error.InvalidCapabilities, validateCapabilities("{\"profileId\":\" \\u3000\"}", &storage));
    try std.testing.expectError(error.InvalidCapabilities, validateCapabilities("{\"profileId\":\"normal\",\"unknown\":true}", &storage));
    try std.testing.expectError(error.InvalidCapabilities, validateCapabilities("{\"profileId\":\"normal\",\"readOnly\":1}", &storage));
    try std.testing.expectError(error.InvalidCapabilities, validateCapabilities("{\"profileId\":\"normal\",\"profileId\":\"other\"}", &storage));
}

const TestMode = enum { length, chunked, eof, empty_key, reject_peer, reject_pid, reject_path, reject_service, cancelled, timeout, unauthorized, oversized, invalid_json, ambiguous_framing, invalid_content_type, truncated };
fn rejected(mode: TestMode) bool {
    return mode == .reject_peer or mode == .reject_pid or mode == .reject_path or mode == .reject_service;
}
const Fixture = struct {
    listener: c.SOCKET,
    stop: c.HANDLE,
    release: c.HANDLE,
    mode: TestMode,
    request: [1024]u8 = undefined,
    received: usize = 0,
    fn serve(raw: c.LPVOID) callconv(.winapi) c.DWORD {
        const self: *Fixture = @ptrCast(@alignCast(raw));
        var readable: c.fd_set = std.mem.zeroes(c.fd_set);
        readable.fd_count = 1;
        readable.fd_array[0] = self.listener;
        var timeout: c.timeval = .{ .tv_sec = 3, .tv_usec = 0 };
        if (c.select(0, &readable, null, null, &timeout) != 1) return 1;
        const socket = c.accept(self.listener, null, null);
        if (socket == c.INVALID_SOCKET) return 2;
        defer _ = c.closesocket(socket);
        var milliseconds: u32 = 2000;
        _ = c.setsockopt(socket, c.SOL_SOCKET, c.SO_RCVTIMEO, @ptrCast(&milliseconds), @sizeOf(u32));
        while (self.received < self.request.len) {
            const count = c.recv(socket, @ptrCast(self.request[self.received..].ptr), @intCast(self.request.len - self.received), 0);
            if (count == 0 and rejected(self.mode)) return 0;
            if (count <= 0) return 3;
            self.received += @intCast(count);
            if (std.mem.indexOf(u8, self.request[0..self.received], "\r\n\r\n") != null) break;
        }
        if (self.mode == .cancelled) _ = c.SetEvent(self.stop);
        if (self.mode == .cancelled or self.mode == .timeout) {
            _ = c.WaitForSingleObject(self.release, 3000);
            return 0;
        }
        const body = "{\"profileId\":\"normal\",\"readOnly\":true}";
        var response: [512]u8 = undefined;
        const bytes = switch (self.mode) {
            .chunked => std.fmt.bufPrint(&response, "HTTP/1.1 200 OK\r\nContent-Type: application/json; charset=utf-8\r\nTransfer-Encoding: chunked\r\n\r\n{x}\r\n{s}\r\n0\r\n\r\n", .{ body.len, body }) catch return 4,
            .unauthorized => "HTTP/1.1 401 Unauthorized\r\nContent-Length: 0\r\n\r\n",
            .oversized => "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: 16385\r\n\r\n",
            .invalid_json => "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: 2\r\n\r\n{}",
            .ambiguous_framing => "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: 2\r\nTransfer-Encoding: chunked\r\n\r\n",
            .invalid_content_type => "HTTP/1.1 200 OK\r\nContent-Type: text/html\r\nContent-Length: 2\r\n\r\n{}",
            .truncated => "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: 100\r\n\r\n{}",
            .eof => std.fmt.bufPrint(&response, "HTTP/1.0 200 OK\r\nContent-Type: application/json\r\n\r\n{s}", .{body}) catch return 4,
            else => std.fmt.bufPrint(&response, "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {d}\r\n\r\n{s}", .{ body.len, body }) catch return 4,
        };
        // Deliberately fragment the response down to single-byte writes.
        for (bytes) |value| {
            const byte_value = [1]u8{value};
            if (c.send(socket, &byte_value, 1, 0) != 1) return 0;
        }
        return 0;
    }
};
const TestGate = struct {
    scratch: *identity.Scratch,
    expected: identity.Identity,
    path: []const u16,
    called: *bool,
    fn verify(self: TestGate, client: identity.Endpoint, server: identity.Endpoint) !void {
        self.called.* = true;
        if (!identity.verifyPeer(self.scratch, self.expected, self.path, client, server)) return error.UntrustedConnectedPeer;
    }
};
fn fixtureTest(mode: TestMode) !void {
    var data: c.WSADATA = undefined;
    try std.testing.expectEqual(@as(c_int, 0), c.WSAStartup(0x0202, &data));
    defer _ = c.WSACleanup();
    const listener = c.socket(c.AF_INET, c.SOCK_STREAM, c.IPPROTO_TCP);
    try std.testing.expect(listener != c.INVALID_SOCKET);
    defer _ = c.closesocket(listener);
    var address: c.SOCKADDR_IN = std.mem.zeroes(c.SOCKADDR_IN);
    address.sin_family = c.AF_INET;
    address.sin_addr.S_un.S_addr = 0x0100007f;
    try std.testing.expectEqual(@as(c_int, 0), c.bind(listener, @ptrCast(&address), @sizeOf(@TypeOf(address))));
    try std.testing.expectEqual(@as(c_int, 0), c.listen(listener, 1));
    var size: c_int = @sizeOf(@TypeOf(address));
    try std.testing.expectEqual(@as(c_int, 0), c.getsockname(listener, @ptrCast(&address), &size));
    const port = std.mem.bigToNative(u16, address.sin_port);
    try std.testing.expect(port != 9321 and port != 9333);
    const stop = c.CreateEventW(null, 1, 0, null) orelse return error.EventUnavailable;
    defer _ = c.CloseHandle(stop);
    const release = c.CreateEventW(null, 1, 0, null) orelse return error.EventUnavailable;
    defer _ = c.CloseHandle(release);
    var fixture: Fixture = .{ .listener = listener, .stop = stop, .release = release, .mode = mode };
    const worker = c.CreateThread(null, 0, Fixture.serve, &fixture, 0, null) orelse return error.ThreadUnavailable;
    defer {
        _ = c.SetEvent(release);
        _ = c.WaitForSingleObject(worker, c.INFINITE);
        _ = c.CloseHandle(worker);
    }
    var created: c.FILETIME = undefined;
    var exited: c.FILETIME = undefined;
    var kernel: c.FILETIME = undefined;
    var user: c.FILETIME = undefined;
    try std.testing.expect(c.GetProcessTimes(c.GetCurrentProcess(), &created, &exited, &kernel, &user) != 0);
    var expected: identity.Identity = .{ .pid = c.GetCurrentProcessId(), .created = (@as(u64, created.dwHighDateTime) << 32) | created.dwLowDateTime };
    if (mode == .reject_peer) expected.created += 1;
    if (mode == .reject_pid) expected.pid += 1;
    var path: [32768]u16 = undefined;
    var path_length: u32 = path.len;
    try std.testing.expect(c.QueryFullProcessImageNameW(c.GetCurrentProcess(), 0, &path, &path_length) != 0);
    const Workspace = struct { process: identity.Scratch, transport: Scratch };
    const memory = c.VirtualAlloc(null, @sizeOf(Workspace), c.MEM_RESERVE | c.MEM_COMMIT, c.PAGE_READWRITE) orelse return error.OutOfMemory;
    defer _ = c.VirtualFree(memory, 0, c.MEM_RELEASE);
    const scratch: *Workspace = @ptrCast(@alignCast(memory));
    var called = false;
    const expected_path = if (mode == .reject_path) std.unicode.utf8ToUtf16LeStringLiteral("Z:\\not-the-fixture.exe") else path[0..path_length];
    const gate: TestGate = .{ .scratch = &scratch.process, .expected = expected, .path = expected_path, .called = &called };
    const key = if (mode == .empty_key) "" else "rm_sk_" ++ "0" ** 64;
    const started = c.GetTickCount64();
    const result = if (mode == .reject_service)
        authenticated(stop, port, 2000, &scratch.transport, &scratch.process, expected, expected_path, std.unicode.utf8ToUtf16LeStringLiteral("ResourceManager.Desktop.NonexistentTestService"), key)
    else
        exchange(stop, port, if (mode == .timeout) 150 else 2000, &scratch.transport, gate, key);
    switch (mode) {
        .reject_peer, .reject_pid, .reject_path => try std.testing.expectError(error.UntrustedConnectedPeer, result),
        .reject_service => try std.testing.expectError(error.ServiceUnavailable, result),
        .cancelled => try std.testing.expectError(error.Stopped, result),
        .timeout => try std.testing.expectError(error.ProbeTimedOut, result),
        .unauthorized => try std.testing.expectError(error.BackendHttpStatus, result),
        .oversized => try std.testing.expectError(error.HttpBodyTooLarge, result),
        .invalid_json => try std.testing.expectError(error.InvalidCapabilities, result),
        .ambiguous_framing => try std.testing.expectError(error.InvalidHttpFraming, result),
        .invalid_content_type => try std.testing.expectError(error.InvalidContentType, result),
        .truncated => try std.testing.expectError(error.TruncatedHttpResponse, result),
        else => try result,
    }
    try std.testing.expectEqual(mode != .reject_service, called);
    if (mode == .cancelled or mode == .timeout) try std.testing.expect(c.GetTickCount64() - started < 2000);
    _ = c.SetEvent(release);
    try std.testing.expectEqual(c.WAIT_OBJECT_0, c.WaitForSingleObject(worker, 5000));
    var exit_code: u32 = 99;
    try std.testing.expect(c.GetExitCodeThread(worker, &exit_code) != 0);
    try std.testing.expectEqual(@as(u32, 0), exit_code);
    if (rejected(mode)) {
        try std.testing.expectEqual(@as(usize, 0), fixture.received);
    } else {
        const request = fixture.request[0..fixture.received];
        try std.testing.expect(std.mem.startsWith(u8, request, "GET /api/runtime/capabilities HTTP/1.1\r\n"));
        const sent_key = std.mem.indexOf(u8, request, "X-Resource-Manager-Key:") != null;
        try std.testing.expectEqual(mode != .empty_key, sent_key);
        if (mode != .empty_key) try std.testing.expect(std.mem.indexOf(u8, request, key) != null);
    }
}
test "authenticated real loopback content length and chunked responses" {
    try fixtureTest(.length);
    try fixtureTest(.chunked);
    try fixtureTest(.eof);
    try fixtureTest(.empty_key);
}
test "real peer rejection writes zero HTTP bytes" {
    try fixtureTest(.reject_peer);
    try fixtureTest(.reject_pid);
    try fixtureTest(.reject_path);
    try fixtureTest(.reject_service);
}
test "authenticated receive is stop cancellable and deadline bounded" {
    try fixtureTest(.cancelled);
    try fixtureTest(.timeout);
}
test "HTTP failures and invalid bounded payloads never establish health" {
    try fixtureTest(.unauthorized);
    try fixtureTest(.oversized);
    try fixtureTest(.invalid_json);
    try fixtureTest(.ambiguous_framing);
    try fixtureTest(.invalid_content_type);
    try fixtureTest(.truncated);
}
