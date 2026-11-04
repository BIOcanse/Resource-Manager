const std = @import("std");
const c = @import("c.zig").win;
const identity = @import("identity.zig");
const wide = std.unicode.utf8ToUtf16LeStringLiteral;

pub const Result = struct { status: u32, peer_verified: bool };
const Completion = struct {
    done: c.HANDLE,
    closed: c.HANDLE,
    failure: std.atomic.Value(u32) = .init(0),
};

fn callback(_: c.HINTERNET, context: c.DWORD_PTR, status: c.DWORD, information: c.LPVOID, length: c.DWORD) callconv(.winapi) void {
    if (context == 0) return;
    const state: *Completion = @ptrFromInt(context);
    switch (status) {
        c.WINHTTP_CALLBACK_STATUS_REQUEST_ERROR => {
            const failure: *const c.WINHTTP_ASYNC_RESULT = @ptrCast(@alignCast(information));
            state.failure.store(if (length >= @sizeOf(c.WINHTTP_ASYNC_RESULT)) failure.dwError else c.ERROR_INVALID_DATA, .release);
            _ = c.SetEvent(state.done);
        },
        c.WINHTTP_CALLBACK_STATUS_SENDREQUEST_COMPLETE, c.WINHTTP_CALLBACK_STATUS_HEADERS_AVAILABLE => {
            _ = c.SetEvent(state.done);
        },
        c.WINHTTP_CALLBACK_STATUS_HANDLE_CLOSING => {
            _ = c.SetEvent(state.closed);
        },
        else => {},
    }
}

fn wait(state: *Completion, stop: c.HANDLE, deadline: u64) !void {
    const now = c.GetTickCount64();
    if (now >= deadline) return error.ProbeTimedOut;
    const handles = [_]c.HANDLE{ stop, state.done };
    const result = c.WaitForMultipleObjects(handles.len, &handles, 0, @intCast(@min(deadline - now, std.math.maxInt(u32) - 1)));
    if (result == c.WAIT_OBJECT_0) return error.Stopped;
    if (result == c.WAIT_TIMEOUT) return error.ProbeTimedOut;
    if (result != c.WAIT_OBJECT_0 + 1 or state.failure.load(.acquire) != 0) return error.ProbeFailed;
}

/// Deliberately has no key argument and never sets credentials or custom headers.
/// WinHTTP documents endpoint querying only after the response; this attests the
/// diagnostic response, not a future authenticated request or pooled connection.
pub fn unauthenticated(stop: c.HANDLE, port: u16, timeout_ms: u32, scratch: *identity.Scratch, expected: identity.Identity, expected_path: []const u16) !Result {
    if (c.WaitForSingleObject(stop, 0) != c.WAIT_TIMEOUT) return error.Stopped;
    const deadline = c.GetTickCount64() + timeout_ms;
    const session = c.WinHttpOpen(wide("ResourceManager.Desktop").ptr, c.WINHTTP_ACCESS_TYPE_NO_PROXY, null, null, c.WINHTTP_FLAG_ASYNC) orelse return error.ProbeFailed;
    defer _ = c.WinHttpCloseHandle(session);
    if (c.WinHttpSetTimeouts(session, @intCast(timeout_ms), @intCast(timeout_ms), @intCast(timeout_ms), @intCast(timeout_ms)) == 0) return error.ProbeFailed;
    const connection = c.WinHttpConnect(session, wide("127.0.0.1").ptr, port, 0) orelse return error.ProbeFailed;
    defer _ = c.WinHttpCloseHandle(connection);
    const request = c.WinHttpOpenRequest(connection, wide("GET").ptr, wide("/api/runtime/capabilities").ptr, null, null, null, 0) orelse return error.ProbeFailed;
    const done = c.CreateEventW(null, 1, 0, null) orelse {
        _ = c.WinHttpCloseHandle(request);
        return error.ProbeFailed;
    };
    defer _ = c.CloseHandle(done);
    const closed = c.CreateEventW(null, 1, 0, null) orelse {
        _ = c.WinHttpCloseHandle(request);
        return error.ProbeFailed;
    };
    defer _ = c.CloseHandle(closed);
    var state: Completion = .{ .done = done, .closed = closed };
    var context: c.DWORD_PTR = @intFromPtr(&state);
    if (c.WinHttpSetOption(request, c.WINHTTP_OPTION_CONTEXT_VALUE, &context, @sizeOf(@TypeOf(context))) == 0) {
        _ = c.WinHttpCloseHandle(request);
        return error.ProbeFailed;
    }
    const previous = c.WinHttpSetStatusCallback(request, callback, c.WINHTTP_CALLBACK_FLAG_ALL_COMPLETIONS | c.WINHTTP_CALLBACK_FLAG_HANDLES, 0);
    if (@intFromPtr(previous) == std.math.maxInt(usize)) {
        _ = c.WinHttpCloseHandle(request);
        return error.ProbeFailed;
    }
    defer {
        _ = c.WinHttpCloseHandle(request);
        // HANDLE_CLOSING is the final callback and follows all other callbacks.
        // Drain after cancellation before stack context/events may be released.
        _ = c.WaitForSingleObject(closed, c.INFINITE);
    }
    var disabled: u32 = c.WINHTTP_DISABLE_REDIRECTS | c.WINHTTP_DISABLE_COOKIES | c.WINHTTP_DISABLE_AUTHENTICATION;
    if (c.WinHttpSetOption(request, c.WINHTTP_OPTION_DISABLE_FEATURE, &disabled, @sizeOf(u32)) == 0) return error.ProbeFailed;
    if (c.WinHttpSendRequest(request, null, 0, null, 0, 0, context) == 0) return error.ProbeFailed;
    try wait(&state, stop, deadline);
    _ = c.ResetEvent(done);
    if (c.WinHttpReceiveResponse(request, null) == 0) return error.ProbeFailed;
    try wait(&state, stop, deadline);
    var status: u32 = 0;
    var bytes: u32 = @sizeOf(u32);
    if (c.WinHttpQueryHeaders(request, c.WINHTTP_QUERY_STATUS_CODE | c.WINHTTP_QUERY_FLAG_NUMBER, null, &status, &bytes, null) == 0) return error.ProbeFailed;
    var info: c.WINHTTP_CONNECTION_INFO = std.mem.zeroes(c.WINHTTP_CONNECTION_INFO);
    info.cbSize = @sizeOf(@TypeOf(info));
    bytes = @sizeOf(@TypeOf(info));
    if (c.WinHttpQueryOption(request, c.WINHTTP_OPTION_CONNECTION_INFO, &info, &bytes) == 0) return error.PeerUnavailable;
    if (info.LocalAddress.ss_family != c.AF_INET or info.RemoteAddress.ss_family != c.AF_INET) return error.PeerUnavailable;
    const client: *const c.SOCKADDR_IN = @ptrCast(@alignCast(&info.LocalAddress));
    const server: *const c.SOCKADDR_IN = @ptrCast(@alignCast(&info.RemoteAddress));
    const verified = identity.verifyPeer(scratch, expected, expected_path, .{ .address = client.sin_addr.S_un.S_addr, .port = std.mem.bigToNative(u16, client.sin_port) }, .{ .address = server.sin_addr.S_un.S_addr, .port = std.mem.bigToNative(u16, server.sin_port) });
    return .{ .status = status, .peer_verified = verified };
}

const Fixture = struct {
    listener: c.SOCKET,
    release: c.HANDLE,
    stop: c.HANDLE,
    cancel: bool,
    bytes: [2048]u8 = undefined,
    length: usize = 0,
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
        while (self.length < self.bytes.len) {
            const n = c.recv(socket, @ptrCast(self.bytes[self.length..].ptr), @intCast(self.bytes.len - self.length), 0);
            if (n <= 0) return 3;
            self.length += @intCast(n);
            if (std.mem.indexOf(u8, self.bytes[0..self.length], "\r\n\r\n") != null) break;
        }
        if (std.mem.indexOf(u8, self.bytes[0..self.length], "\r\n\r\n") == null) return 3;
        if (self.cancel) {
            _ = c.SetEvent(self.stop);
        } else {
            const response = "HTTP/1.1 401 Unauthorized\r\nContent-Length: 0\r\nConnection: keep-alive\r\n\r\n";
            if (c.send(socket, response.ptr, response.len, 0) != response.len) return 4;
        }
        _ = c.WaitForSingleObject(self.release, 3000);
        return 0;
    }
};

fn fixtureTest(cancel: bool) !void {
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
    const fixture_port = std.mem.bigToNative(u16, address.sin_port);
    try std.testing.expect(fixture_port != 9321 and fixture_port != 9333);
    const stop = c.CreateEventW(null, 1, 0, null) orelse return error.EventUnavailable;
    defer _ = c.CloseHandle(stop);
    const release = c.CreateEventW(null, 1, 0, null) orelse return error.EventUnavailable;
    defer _ = c.CloseHandle(release);
    var fixture: Fixture = .{ .listener = listener, .stop = stop, .release = release, .cancel = cancel };
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
    const expected: identity.Identity = .{ .pid = c.GetCurrentProcessId(), .created = (@as(u64, created.dwHighDateTime) << 32) | created.dwLowDateTime };
    var path: [32768]u16 = undefined;
    var path_size: u32 = path.len;
    try std.testing.expect(c.QueryFullProcessImageNameW(c.GetCurrentProcess(), 0, &path, &path_size) != 0);
    // Fixed scratch is outside the test thread's stack.
    const memory = c.VirtualAlloc(null, @sizeOf(identity.Scratch), c.MEM_RESERVE | c.MEM_COMMIT, c.PAGE_READWRITE) orelse return error.OutOfMemory;
    defer _ = c.VirtualFree(memory, 0, c.MEM_RELEASE);
    const scratch: *identity.Scratch = @ptrCast(@alignCast(memory));
    const started = c.GetTickCount64();
    if (cancel) {
        try std.testing.expectError(error.Stopped, unauthenticated(stop, fixture_port, 2000, scratch, expected, path[0..path_size]));
        try std.testing.expect(c.GetTickCount64() - started < 2000);
    } else {
        const result = try unauthenticated(stop, fixture_port, 2000, scratch, expected, path[0..path_size]);
        try std.testing.expectEqual(@as(u32, 401), result.status);
        try std.testing.expect(result.peer_verified);
    }
    _ = c.SetEvent(release);
    try std.testing.expectEqual(c.WAIT_OBJECT_0, c.WaitForSingleObject(worker, 5000));
    try std.testing.expect(fixture.length > 0);
    try std.testing.expect(std.mem.indexOf(u8, fixture.bytes[0..fixture.length], "\r\n\r\n") != null);
    try std.testing.expect(std.mem.indexOf(u8, fixture.bytes[0..fixture.length], "X-Resource-Manager-Key") == null);
    try std.testing.expect(std.mem.indexOf(u8, fixture.bytes[0..fixture.length], "rm_sk_") == null);
}

test "real ephemeral loopback diagnostic never sends key and attests response peer" {
    try fixtureTest(false);
}
test "stop cancels WinHTTP receive and drains callbacks" {
    try fixtureTest(true);
}
