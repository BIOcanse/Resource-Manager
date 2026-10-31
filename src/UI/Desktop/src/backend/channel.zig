//! A persistent attested HTTP/1.1 connection to the backend. The socket is
//! attested exactly like the probe (transport.zig) before the first byte is
//! written; then it carries keep-alive requests, one NDJSON response stream,
//! or one chunked NDJSON request-body upload.
//! No proxy, redirects, pooling, cookies, decompression or transport fallback.
const std = @import("std");
const c = @import("c.zig").win;
const identity = @import("identity.zig");
const keys = @import("key_reader.zig");

pub const header_limit = 8 * 1024;
const infinite = std.math.maxInt(u64);

/// Opens a channel already attested for the current backend. The product
/// opener lives in root.zig (Link); tests attest their own loopback fixture.
pub const Opener = struct {
    context: *anyopaque,
    open_fn: *const fn (context: *anyopaque, channel: *Channel, stop: c.HANDLE) anyerror!void,
    pub fn open(self: Opener, channel: *Channel, stop: c.HANDLE) !void {
        return self.open_fn(self.context, channel, stop);
    }
};

pub const Response = struct { status: u16, body: []const u8 };

pub const Channel = struct {
    socket: c.SOCKET = c.INVALID_SOCKET,
    event: c.WSAEVENT = null,
    stop: c.HANDLE = null,
    port: u16 = 0,
    timeout_ms: u32 = 0,
    deadline: u64 = 0,
    winsock: bool = false,
    key: [keys.maximum_file_bytes]u8 = @splat(0),
    key_len: usize = 0,
    wire: [4096]u8 = undefined,
    start: usize = 0,
    end: usize = 0,
    line: [header_limit]u8 = undefined,
    streaming: bool = false,
    chunk_remaining: usize = 0,
    stream_ended: bool = false,
    uploading: bool = false,

    pub fn isOpen(self: *const Channel) bool {
        return self.socket != c.INVALID_SOCKET;
    }
    /// An accepted upload is open: `writeLine` may be called.
    pub fn isUploading(self: *const Channel) bool {
        return self.uploading;
    }

    /// `gate.verify(client, server)` attests the connected peer before any byte is sent.
    pub fn open(self: *Channel, stop: c.HANDLE, port: u16, timeout_ms: u32, gate: anytype, key: []const u8) !void {
        std.debug.assert(!self.isOpen());
        if (port == 0 or timeout_ms == 0 or timeout_ms > std.math.maxInt(i32)) return error.InvalidTransportConfig;
        if (c.WaitForSingleObject(stop, 0) != c.WAIT_TIMEOUT) return error.Stopped;
        var decoded = try keys.decode(key);
        defer decoded.clear();
        var data: c.WSADATA = undefined;
        if (c.WSAStartup(0x0202, &data) != 0) return error.WinsockUnavailable;
        self.winsock = true;
        errdefer self.close();
        self.socket = c.WSASocketW(c.AF_INET, c.SOCK_STREAM, c.IPPROTO_TCP, null, 0, c.WSA_FLAG_NO_HANDLE_INHERIT);
        if (self.socket == c.INVALID_SOCKET) return error.SocketUnavailable;
        self.event = c.WSACreateEvent();
        if (self.event == null) return error.SocketEventUnavailable;
        if (c.WSAEventSelect(self.socket, self.event, c.FD_CONNECT | c.FD_READ | c.FD_WRITE | c.FD_CLOSE) != 0) return error.SocketEventUnavailable;
        self.stop = stop;
        self.port = port;
        self.timeout_ms = timeout_ms;
        self.start = 0;
        self.end = 0;
        self.streaming = false;
        self.uploading = false;
        self.stream_ended = false;
        self.chunk_remaining = 0;
        self.arm();
        var address: c.SOCKADDR_IN = std.mem.zeroes(c.SOCKADDR_IN);
        address.sin_family = c.AF_INET;
        address.sin_addr.S_un.S_addr = 0x0100007f;
        address.sin_port = std.mem.nativeToBig(u16, port);
        if (c.connect(self.socket, @ptrCast(&address), @sizeOf(@TypeOf(address))) != 0) {
            if (c.WSAGetLastError() != c.WSAEWOULDBLOCK) return error.SocketConnectFailed;
            try self.wait();
        }
        const client = try endpoint(self.socket, false);
        const server = try endpoint(self.socket, true);
        if (server.address != address.sin_addr.S_un.S_addr or server.port != port) return error.UntrustedConnectedPeer;
        try gate.verify(client, server);
        try self.check();
        @memcpy(self.key[0..decoded.len], decoded.value());
        self.key_len = decoded.len;
    }

    pub fn close(self: *Channel) void {
        if (self.socket != c.INVALID_SOCKET) {
            _ = c.WSAEventSelect(self.socket, null, 0);
            _ = c.closesocket(self.socket);
            self.socket = c.INVALID_SOCKET;
        }
        if (self.event != null) {
            _ = c.WSACloseEvent(self.event);
            self.event = null;
        }
        if (self.winsock) {
            _ = c.WSACleanup();
            self.winsock = false;
        }
        std.crypto.secureZero(u8, &self.key);
        self.key_len = 0;
        self.streaming = false;
        self.uploading = false;
    }

    /// Waitable handle while no response is being read (an idle keep-alive
    /// connection or an open upload); see `peerEnded`.
    pub fn waitHandle(self: *const Channel) c.HANDLE {
        return self.event;
    }

    /// Called when `waitHandle` signals while no response is being read. A
    /// close, a socket error or any response byte means the server ended the
    /// connection (keep-alive timeout) or the upload's response; each closes
    /// the channel. It decides by peeking the socket, so it stays exact after
    /// a write's wait consumed the socket's event records.
    pub fn peerEnded(self: *Channel) bool {
        if (!self.isOpen()) return true;
        var events: c.WSANETWORKEVENTS = std.mem.zeroes(c.WSANETWORKEVENTS);
        if (c.WSAEnumNetworkEvents(self.socket, self.event, &events) != 0) {
            self.close();
            return true;
        }
        var peek: [1]u8 = undefined;
        const n = c.recv(self.socket, &peek, 1, c.MSG_PEEK);
        if (n == c.SOCKET_ERROR and c.WSAGetLastError() == c.WSAEWOULDBLOCK) return false;
        self.close();
        return true;
    }

    /// One keep-alive request. `out` bounds the response body. A response that
    /// closes the connection closes the channel after its body is read.
    pub fn request(self: *Channel, method: []const u8, path: []const u8, body: []const u8, out: []u8) !Response {
        if (!self.isOpen() or self.streaming or self.uploading) return error.ChannelUnavailable;
        errdefer self.close();
        self.arm();
        try self.sendHead(method, path, if (body.len != 0 or std.mem.eql(u8, method, "POST")) .{ .length = body.len } else .none, "application/json");
        try self.writeAll(body);
        const head = try self.readHead();
        var length: usize = 0;
        if (head.chunked) {
            while (true) {
                const count = try self.chunkSize();
                if (count == 0) break;
                if (count > out.len - length) return error.HttpBodyTooLarge;
                try self.exact(out[length..][0..count]);
                length += count;
                try self.crlf();
            }
        } else if (head.content_length) |count| {
            if (count > out.len) return error.HttpBodyTooLarge;
            try self.exact(out[0..count]);
            length = count;
        } else if (head.status != 204 and head.status != 304) {
            while (try self.byte()) |value| {
                if (length == out.len) return error.HttpBodyTooLarge;
                out[length] = value;
                length += 1;
            }
            self.close();
            return .{ .status = head.status, .body = out[0..length] };
        }
        if (self.start != self.end) return error.InvalidHttpFraming;
        if (head.close) self.close();
        return .{ .status = head.status, .body = out[0..length] };
    }

    /// Opens the unified subscription stream. A non-200 status is returned as
    /// an error after the channel is closed. The stream has no deadline.
    pub fn openStream(self: *Channel, path: []const u8, body: []const u8) !void {
        if (!self.isOpen() or self.streaming or self.uploading) return error.ChannelUnavailable;
        errdefer self.close();
        self.arm();
        try self.sendHead("POST", path, .{ .length = body.len }, "application/x-ndjson");
        try self.writeAll(body);
        const head = try self.readHead();
        if (head.status == 401 or head.status == 403) return error.BackendAccessDenied;
        if (head.status != 200) return error.BackendHttpStatus;
        if (!head.chunked or !head.ndjson) return error.InvalidStreamFraming;
        self.streaming = true;
        self.stream_ended = false;
        self.chunk_remaining = 0;
        self.deadline = infinite;
    }

    /// Opens a chunked NDJSON request-body upload and reads the response head
    /// once, within the transport timeout. On 200 the response stays open with
    /// no deadline and `writeLine` may be called; 401/403, 409 and any other
    /// status close the channel. A response byte right after the head means
    /// the server already ended it.
    pub fn openUpload(self: *Channel, path: []const u8) !void {
        if (!self.isOpen() or self.streaming or self.uploading) return error.ChannelUnavailable;
        errdefer self.close();
        self.arm();
        // Each line is three small sends; none waits for the previous one's ACK.
        const no_delay: c.BOOL = c.TRUE;
        if (c.setsockopt(self.socket, c.IPPROTO_TCP, c.TCP_NODELAY, @ptrCast(&no_delay), @sizeOf(c.BOOL)) != 0) return error.SocketOptionFailed;
        try self.sendHead("POST", path, .chunked, "application/x-ndjson");
        const head = try self.readHead();
        if (head.status == 401 or head.status == 403) return error.BackendAccessDenied;
        if (head.status == 409) return error.PublisherConflict;
        if (head.status != 200) return error.BackendHttpStatus;
        if (!head.chunked) return error.InvalidStreamFraming;
        if (self.start != self.end) return error.UploadEnded;
        self.uploading = true;
        self.deadline = infinite;
    }

    /// Writes one NDJSON line (`line` without its newline) as one chunk,
    /// bounded by the transport timeout and cancelled by stop. A response
    /// byte or a close seen afterwards means the server ended the upload.
    pub fn writeLine(self: *Channel, line: []const u8) !void {
        if (!self.uploading) return error.ChannelUnavailable;
        std.debug.assert(std.mem.indexOfScalar(u8, line, '\n') == null);
        errdefer self.close();
        self.arm();
        var size: [24]u8 = undefined;
        try self.writeAll(try std.fmt.bufPrint(&size, "{x}\r\n", .{line.len + 1}));
        try self.writeAll(line);
        try self.writeAll("\n\r\n");
        self.deadline = infinite;
        if (self.peerEnded()) return error.UploadEnded;
    }

    /// Ends an open upload: the terminating chunk is sent once without waiting
    /// (stop may already be signalled), the send side is shut down, and the
    /// channel closes. In any other state it only closes.
    pub fn endUpload(self: *Channel) void {
        if (self.uploading) {
            const last = "0\r\n\r\n";
            _ = c.send(self.socket, last, last.len, 0);
            _ = c.shutdown(self.socket, c.SD_SEND);
        }
        self.close();
    }

    /// Next NDJSON line (without the newline), or null when the server ended
    /// the stream. `out` bounds one frame; blank lines are skipped.
    pub fn nextLine(self: *Channel, out: []u8) !?[]const u8 {
        if (!self.streaming) return error.ChannelUnavailable;
        errdefer self.close();
        var length: usize = 0;
        while (true) {
            if (self.stream_ended) return null;
            if (self.chunk_remaining == 0) {
                const count = try self.chunkSize();
                if (count == 0) {
                    self.stream_ended = true;
                    self.close();
                    return null;
                }
                self.chunk_remaining = count;
            }
            const value = (try self.byte()) orelse return error.TruncatedHttpResponse;
            self.chunk_remaining -= 1;
            if (self.chunk_remaining == 0) try self.crlf();
            if (value == '\n') {
                const line = std.mem.trimEnd(u8, out[0..length], "\r");
                if (line.len == 0) {
                    length = 0;
                    continue;
                }
                return line;
            }
            if (length == out.len) return error.FrameTooLarge;
            out[length] = value;
            length += 1;
        }
    }

    fn arm(self: *Channel) void {
        self.deadline = c.GetTickCount64() + self.timeout_ms;
    }
    fn check(self: *Channel) !void {
        if (c.WaitForSingleObject(self.stop, 0) != c.WAIT_TIMEOUT) return error.Stopped;
        if (c.GetTickCount64() >= self.deadline) return error.ProbeTimedOut;
    }
    fn wait(self: *Channel) !void {
        try self.check();
        const handles = [_]c.HANDLE{ self.stop, self.event };
        const timeout: u32 = if (self.deadline == infinite) c.INFINITE else @intCast(@min(self.deadline - c.GetTickCount64(), std.math.maxInt(u32) - 1));
        const result = c.WaitForMultipleObjects(handles.len, &handles, 0, timeout);
        if (result == c.WAIT_OBJECT_0) return error.Stopped;
        if (result == c.WAIT_TIMEOUT) return error.ProbeTimedOut;
        if (result != c.WAIT_OBJECT_0 + 1) return error.SocketWaitFailed;
        var events: c.WSANETWORKEVENTS = std.mem.zeroes(c.WSANETWORKEVENTS);
        if (c.WSAEnumNetworkEvents(self.socket, self.event, &events) != 0) return error.SocketWaitFailed;
        for (events.iErrorCode, 0..) |failure, index| {
            if (events.lNetworkEvents & (@as(c_long, 1) << @intCast(index)) != 0 and failure != 0) return error.SocketIoFailed;
        }
    }
    fn writeAll(self: *Channel, bytes: []const u8) !void {
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
    const Framing = union(enum) { none, length: usize, chunked };
    fn sendHead(self: *Channel, method: []const u8, path: []const u8, framing: Framing, accept: []const u8) !void {
        var head: [1024]u8 = undefined;
        defer std.crypto.secureZero(u8, &head);
        var used = (try std.fmt.bufPrint(&head, "{s} {s} HTTP/1.1\r\nHost: 127.0.0.1:{d}\r\nAccept: {s}\r\nConnection: keep-alive\r\n", .{ method, path, self.port, accept })).len;
        switch (framing) {
            .none => {},
            .length => |length| used += (try std.fmt.bufPrint(head[used..], "Content-Type: application/json\r\nContent-Length: {d}\r\n", .{length})).len,
            .chunked => used += (try std.fmt.bufPrint(head[used..], "Content-Type: application/x-ndjson\r\nTransfer-Encoding: chunked\r\n", .{})).len,
        }
        if (self.key_len != 0)
            used += (try std.fmt.bufPrint(head[used..], "X-Resource-Manager-Key: {s}\r\n", .{self.key[0..self.key_len]})).len;
        used += (try std.fmt.bufPrint(head[used..], "\r\n", .{})).len;
        try self.writeAll(head[0..used]);
    }
    fn byte(self: *Channel) !?u8 {
        if (self.start == self.end) {
            self.start = 0;
            self.end = 0;
            while (true) {
                try self.check();
                const n = c.recv(self.socket, @ptrCast(&self.wire), self.wire.len, 0);
                if (n == 0) return null;
                if (n > 0) {
                    self.end = @intCast(n);
                    break;
                }
                if (c.WSAGetLastError() != c.WSAEWOULDBLOCK) return error.SocketReceiveFailed;
                try self.wait();
            }
        }
        const value = self.wire[self.start];
        self.start += 1;
        return value;
    }
    fn exact(self: *Channel, bytes: []u8) !void {
        for (bytes) |*value| value.* = (try self.byte()) orelse return error.TruncatedHttpResponse;
    }
    fn crlf(self: *Channel) !void {
        var pair: [2]u8 = undefined;
        try self.exact(&pair);
        if (!std.mem.eql(u8, &pair, "\r\n")) return error.InvalidHttpFraming;
    }
    fn readLine(self: *Channel) ![]const u8 {
        var length: usize = 0;
        while (length < self.line.len) {
            const value = (try self.byte()) orelse return error.TruncatedHttpResponse;
            if (value == '\n') {
                if (length == 0 or self.line[length - 1] != '\r') return error.InvalidHttpResponse;
                return self.line[0 .. length - 1];
            }
            if (value < 0x20 and value != '\r' and value != '\t') return error.InvalidHttpResponse;
            self.line[length] = value;
            length += 1;
        }
        return error.HttpHeadersTooLarge;
    }
    fn chunkSize(self: *Channel) !usize {
        const chunk = try self.readLine();
        const end = std.mem.indexOfScalar(u8, chunk, ';') orelse chunk.len;
        const count = try httpNumber(chunk[0..end], 16);
        if (count == 0) while ((try self.readLine()).len != 0) {};
        return count;
    }
    const Head = struct { status: u16, content_length: ?usize = null, chunked: bool = false, close: bool = false, ndjson: bool = false };
    fn readHead(self: *Channel) !Head {
        const status_line = try self.readLine();
        const http11 = std.mem.startsWith(u8, status_line, "HTTP/1.1 ");
        if (!(http11 or std.mem.startsWith(u8, status_line, "HTTP/1.0 ")) or status_line.len < 12 or (status_line.len > 12 and status_line[12] != ' ')) return error.InvalidHttpResponse;
        var head: Head = .{ .status = std.fmt.parseInt(u16, status_line[9..12], 10) catch return error.InvalidHttpResponse, .close = !http11 };
        var header_bytes: usize = status_line.len;
        while (true) {
            const header = try self.readLine();
            header_bytes += header.len + 2;
            if (header_bytes > header_limit) return error.HttpHeadersTooLarge;
            if (header.len == 0) break;
            const colon = std.mem.indexOfScalar(u8, header, ':') orelse return error.InvalidHttpResponse;
            const name = header[0..colon];
            const value = std.mem.trim(u8, header[colon + 1 ..], " \t");
            if (std.ascii.eqlIgnoreCase(name, "Content-Length")) {
                if (head.content_length != null) return error.InvalidHttpFraming;
                head.content_length = try httpNumber(value, 10);
            } else if (std.ascii.eqlIgnoreCase(name, "Transfer-Encoding")) {
                if (head.chunked or !std.ascii.eqlIgnoreCase(value, "chunked")) return error.InvalidHttpFraming;
                head.chunked = true;
            } else if (std.ascii.eqlIgnoreCase(name, "Connection")) {
                if (std.ascii.eqlIgnoreCase(value, "close")) head.close = true;
            } else if (std.ascii.eqlIgnoreCase(name, "Content-Type")) {
                const media_end = std.mem.indexOfScalar(u8, value, ';') orelse value.len;
                head.ndjson = std.ascii.eqlIgnoreCase(std.mem.trim(u8, value[0..media_end], " \t"), "application/x-ndjson");
            } else if (std.ascii.eqlIgnoreCase(name, "Content-Encoding") and !std.ascii.eqlIgnoreCase(value, "identity")) return error.UnsupportedContentEncoding;
        }
        if (head.chunked and head.content_length != null) return error.InvalidHttpFraming;
        return head;
    }
};

fn endpoint(socket: c.SOCKET, remote: bool) !identity.Endpoint {
    var address: c.SOCKADDR_IN = std.mem.zeroes(c.SOCKADDR_IN);
    var length: c_int = @sizeOf(@TypeOf(address));
    const result = if (remote) c.getpeername(socket, @ptrCast(&address), &length) else c.getsockname(socket, @ptrCast(&address), &length);
    if (result != 0 or length != @sizeOf(@TypeOf(address)) or address.sin_family != c.AF_INET) return error.PeerUnavailable;
    return .{ .address = address.sin_addr.S_un.S_addr, .port = std.mem.bigToNative(u16, address.sin_port) };
}

fn httpNumber(bytes: []const u8, comptime base: u8) !usize {
    if (bytes.len == 0) return error.InvalidHttpFraming;
    for (bytes) |value| {
        if (if (base == 16) !std.ascii.isHex(value) else !std.ascii.isDigit(value)) return error.InvalidHttpFraming;
    }
    return std.fmt.parseInt(usize, bytes, base) catch error.InvalidHttpFraming;
}

/// Writes `text` as a JSON string literal (quotes included).
pub fn jsonString(writer: anytype, text: []const u8) !void {
    try writer.writeByte('"');
    for (text) |value| switch (value) {
        '"' => try writer.writeAll("\\\""),
        '\\' => try writer.writeAll("\\\\"),
        0...0x1f => try writer.print("\\u{x:0>4}", .{value}),
        else => try writer.writeByte(value),
    };
    try writer.writeByte('"');
}

// ---------------------------------------------------------------- tests
const mock = @import("mock_backend.zig");

test "keep-alive requests share one attested connection" {
    var server = try mock.Server.start(.{ .script = &.{
        .{ .status = "204 No Content" },
        .{ .status = "200 OK", .body = "{\"ok\":true}" },
        .{ .status = "409 Conflict", .body = "{}" },
    } });
    defer server.deinit();
    var channel: Channel = .{};
    defer channel.close();
    try server.opener().open(&channel, server.stop);
    var body: [256]u8 = undefined;
    const select = try channel.request("POST", "/select", "", &body);
    try std.testing.expectEqual(@as(u16, 204), select.status);
    const publish = try channel.request("POST", "/publish", "{\"a\":1}", &body);
    try std.testing.expectEqual(@as(u16, 200), publish.status);
    try std.testing.expectEqualStrings("{\"ok\":true}", publish.body);
    const conflict = try channel.request("POST", "/publish", "{}", &body);
    try std.testing.expectEqual(@as(u16, 409), conflict.status);
    try std.testing.expect(channel.isOpen());
    channel.close();
    server.finish();
    try std.testing.expectEqual(@as(usize, 1), server.connections);
    try std.testing.expectEqual(@as(usize, 3), server.requests);
    try std.testing.expect(server.logged("POST /publish HTTP/1.1"));
    try std.testing.expect(server.logged("{\"a\":1}"));
}

test "NDJSON lines are reassembled across chunk boundaries and end cleanly" {
    var server = try mock.Server.start(.{ .script = &.{
        .{ .status = "200 OK", .stream = &.{ "{\"subscriptionId\":\"a\",", "\"value\":1}\n\n{\"subscription", "Id\":\"a\",\"value\":2}\n" }, .end_stream = true },
    } });
    defer server.deinit();
    var channel: Channel = .{};
    defer channel.close();
    try server.opener().open(&channel, server.stop);
    try channel.openStream("/api/subscriptions/stream", "{}");
    var frame: [256]u8 = undefined;
    try std.testing.expectEqualStrings("{\"subscriptionId\":\"a\",\"value\":1}", (try channel.nextLine(&frame)).?);
    try std.testing.expectEqualStrings("{\"subscriptionId\":\"a\",\"value\":2}", (try channel.nextLine(&frame)).?);
    try std.testing.expectEqual(@as(?[]const u8, null), try channel.nextLine(&frame));
    try std.testing.expect(!channel.isOpen());
}

test "an open stream is cancelled by stop and a denied stream reports access" {
    var server = try mock.Server.start(.{ .script = &.{
        .{ .status = "200 OK", .stream = &.{"{\"subscriptionId\":\"a\",\"value\":1}\n"}, .stop_after = true },
    } });
    defer server.deinit();
    var channel: Channel = .{};
    defer channel.close();
    try server.opener().open(&channel, server.stop);
    try channel.openStream("/s", "{}");
    var frame: [256]u8 = undefined;
    _ = (try channel.nextLine(&frame)).?;
    const started = c.GetTickCount64();
    try std.testing.expectError(error.Stopped, channel.nextLine(&frame));
    try std.testing.expect(c.GetTickCount64() - started < 2000);
    server.finish();

    var denied = try mock.Server.start(.{ .script = &.{.{ .status = "403 Forbidden", .body = "" }} });
    defer denied.deinit();
    var second: Channel = .{};
    defer second.close();
    try denied.opener().open(&second, denied.stop);
    try std.testing.expectError(error.BackendAccessDenied, second.openStream("/s", "{}"));
    try std.testing.expect(!second.isOpen());
}

test "an idle keep-alive connection closed by the server is detected" {
    var server = try mock.Server.start(.{ .script = &.{.{ .status = "204 No Content", .fin_after = true }} });
    defer server.deinit();
    var channel: Channel = .{};
    defer channel.close();
    try server.opener().open(&channel, server.stop);
    var body: [16]u8 = undefined;
    _ = try channel.request("POST", "/select", "", &body);
    try std.testing.expect(channel.isOpen());
    var closed = false;
    var rounds: usize = 0;
    while (!closed and rounds < 8) : (rounds += 1) {
        try std.testing.expectEqual(c.WAIT_OBJECT_0, c.WaitForSingleObject(channel.waitHandle(), 3000));
        closed = channel.peerEnded();
    }
    try std.testing.expect(closed);
    try std.testing.expect(!channel.isOpen());
}

test "an upload carries NDJSON lines, ends by either side, and a conflict is a rejection" {
    var server = try mock.Server.start(.{ .script = &.{
        .{ .status = "200 OK", .upload = true },
        .{ .status = "200 OK", .upload = true, .end_after_lines = 1 },
        .{ .status = "409 Conflict", .body = "", .close_after = true, .drain = true },
    } });
    defer server.deinit();
    var channel: Channel = .{};
    defer channel.close();

    // The client ends the body: the server sees the terminating chunk.
    try server.opener().open(&channel, server.stop);
    try channel.openUpload("/u");
    try std.testing.expect(channel.isUploading());
    try channel.writeLine("{\"a\":1}");
    try channel.writeLine("{\"a\":2}");
    channel.endUpload();
    try std.testing.expect(!channel.isOpen());
    try std.testing.expect(server.waitCounts(.{ .streams = 1, .lines = 2, .client_ends = 1 }, 5000));

    // The server ends the stream after one line: the client sees the end.
    try server.opener().open(&channel, server.stop);
    try channel.openUpload("/u");
    channel.writeLine("{\"a\":3}") catch |err| try std.testing.expectEqual(error.UploadEnded, err);
    try std.testing.expect(server.waitCounts(.{ .streams = 2, .lines = 3, .server_ends = 1 }, 5000));
    if (channel.isOpen()) {
        try std.testing.expectEqual(c.WAIT_OBJECT_0, c.WaitForSingleObject(channel.waitHandle(), 3000));
        try std.testing.expect(channel.peerEnded());
    }
    try std.testing.expect(!channel.isOpen());

    // A conflict answers at once; no line is written.
    try server.opener().open(&channel, server.stop);
    try std.testing.expectError(error.PublisherConflict, channel.openUpload("/u"));
    try std.testing.expect(!channel.isOpen());
    server.finish();
    const counts = server.uploadCounts();
    try std.testing.expectEqual(@as(usize, 0), counts.rejected_body_bytes);
    try std.testing.expectEqual(@as(usize, 0), counts.drops);
    try std.testing.expect(server.logged("Transfer-Encoding: chunked"));
    var line: [64]u8 = undefined;
    const first = server.copyUploadLine(0, &line).?;
    try std.testing.expectEqual(@as(usize, 1), first.stream);
    try std.testing.expectEqualStrings("{\"a\":1}", first.bytes);
    const third = server.copyUploadLine(2, &line).?;
    try std.testing.expectEqual(@as(usize, 2), third.stream);
    try std.testing.expectEqualStrings("{\"a\":3}", third.bytes);
}

test "json string escaping" {
    var buffer: [64]u8 = undefined;
    var writer = std.Io.Writer.fixed(&buffer);
    try jsonString(&writer, "a\"b\\c\n");
    try std.testing.expectEqualStrings("\"a\\\"b\\\\c\\u000a\"", writer.buffered());
}
