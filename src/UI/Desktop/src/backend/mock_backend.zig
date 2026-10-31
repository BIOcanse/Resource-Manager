//! Test-only loopback backend, modelled on the phase A transport fixture: a
//! real listener on 127.0.0.1 port 0 (never 9321/9333), attested through the
//! same peer check as the product gate against the current test process.
const std = @import("std");
const c = @import("c.zig").win;
const identity = @import("identity.zig");
const channel = @import("channel.zig");

pub const Step = struct {
    status: []const u8,
    /// Content-Length body. Null with no stream sends no body (204 style).
    body: ?[]const u8 = null,
    /// Chunked application/x-ndjson response; each element is one chunk.
    stream: ?[]const []const u8 = null,
    end_stream: bool = false,
    /// After the stream chunks, signal the client's stop event.
    stop_after: bool = false,
    /// Answer with `Connection: close` and close.
    close_after: bool = false,
    /// Answer keep-alive, then close the idle connection (Kestrel keep-alive timeout).
    fin_after: bool = false,
    /// Keep answering every later request with this step.
    repeat: bool = false,
    /// Accept a chunked NDJSON request-body upload: answer `status` with
    /// chunked headers at once, keep the response open and record every
    /// received line until the client ends the body or the connection drops.
    upload: bool = false,
    /// With `upload`: end the stream (terminating chunk, then close) after
    /// this many lines of this stream.
    end_after_lines: ?usize = null,
    /// After answering, read the rest of the request until the client closes
    /// and count those bytes in `rejected_body_bytes` (a rejected upload).
    drain: bool = false,
};
pub const Options = struct { script: []const Step };

/// Upload bookkeeping; `waitCounts` waits until every field reaches a minimum.
pub const Counts = struct {
    /// Uploads answered with the step status (stream ids are 1-based).
    streams: usize = 0,
    /// Lines received over all streams.
    lines: usize = 0,
    /// Terminating chunk received: the client closed the body.
    client_ends: usize = 0,
    /// The connection went away without the terminating chunk.
    drops: usize = 0,
    /// Streams the server ended (`end_after_lines` or `endStream`).
    server_ends: usize = 0,
    /// Request-body bytes received after a `drain` answer.
    rejected_body_bytes: usize = 0,
};
pub const UploadLine = struct { stream: usize, bytes: []const u8 };
const max_upload_lines = 512;

pub const Server = struct {
    listener: c.SOCKET = c.INVALID_SOCKET,
    port: u16 = 0,
    stop: c.HANDLE = null,
    release: c.HANDLE = null,
    arrived: c.HANDLE = null,
    thread: c.HANDLE = null,
    options: Options = .{ .script = &.{} },
    step: usize = 0,
    requests: usize = 0,
    connections: usize = 0,
    lock: usize = 0,
    expected: identity.Identity = .{ .pid = 0, .created = 0 },
    path: [32768]u16 = undefined,
    path_len: usize = 0,
    scratch: identity.Scratch = undefined,
    request: [64 * 1024]u8 = undefined,
    log: [64 * 1024]u8 = undefined,
    log_len: usize = 0,
    /// Request lines of every request, kept even after `log` is full.
    lines: [256][96]u8 = undefined,
    line_lens: [256]usize = undefined,
    last_body: [256 * 1024]u8 = undefined,
    last_body_len: usize = 0,
    winsock: bool = false,
    /// `endStream` signals it; the upload in progress ends at once.
    cut: c.HANDLE = null,
    counts: Counts = .{},
    /// Received lines, newline removed; a line past the arena keeps its count only.
    uploaded: [2 * 1024 * 1024]u8 = undefined,
    uploaded_len: usize = 0,
    line_start: [max_upload_lines]usize = undefined,
    line_end: [max_upload_lines]usize = undefined,
    line_stream: [max_upload_lines]usize = undefined,

    pub fn start(options: Options) !*Server {
        const memory = c.VirtualAlloc(null, @sizeOf(Server), c.MEM_RESERVE | c.MEM_COMMIT, c.PAGE_READWRITE) orelse return error.OutOfMemory;
        const self: *Server = @ptrCast(@alignCast(memory));
        self.* = .{ .options = options };
        errdefer self.deinit();
        var data: c.WSADATA = undefined;
        if (c.WSAStartup(0x0202, &data) != 0) return error.WinsockUnavailable;
        self.winsock = true;
        self.listener = c.socket(c.AF_INET, c.SOCK_STREAM, c.IPPROTO_TCP);
        if (self.listener == c.INVALID_SOCKET) return error.SocketUnavailable;
        var address: c.SOCKADDR_IN = std.mem.zeroes(c.SOCKADDR_IN);
        address.sin_family = c.AF_INET;
        address.sin_addr.S_un.S_addr = 0x0100007f;
        if (c.bind(self.listener, @ptrCast(&address), @sizeOf(@TypeOf(address))) != 0) return error.Bind;
        if (c.listen(self.listener, 4) != 0) return error.Listen;
        var size: c_int = @sizeOf(@TypeOf(address));
        if (c.getsockname(self.listener, @ptrCast(&address), &size) != 0) return error.Bind;
        self.port = std.mem.bigToNative(u16, address.sin_port);
        if (self.port == 9321 or self.port == 9333) return error.ReservedPort;
        self.stop = c.CreateEventW(null, 1, 0, null) orelse return error.EventUnavailable;
        self.release = c.CreateEventW(null, 1, 0, null) orelse return error.EventUnavailable;
        self.arrived = c.CreateEventW(null, 0, 0, null) orelse return error.EventUnavailable;
        self.cut = c.CreateEventW(null, 0, 0, null) orelse return error.EventUnavailable;
        var created: c.FILETIME = undefined;
        var exited: c.FILETIME = undefined;
        var kernel: c.FILETIME = undefined;
        var user: c.FILETIME = undefined;
        if (c.GetProcessTimes(c.GetCurrentProcess(), &created, &exited, &kernel, &user) == 0) return error.ProcessTimes;
        self.expected = .{ .pid = c.GetCurrentProcessId(), .created = (@as(u64, created.dwHighDateTime) << 32) | created.dwLowDateTime };
        var length: u32 = self.path.len;
        if (c.QueryFullProcessImageNameW(c.GetCurrentProcess(), 0, &self.path, &length) == 0) return error.ProcessPath;
        self.path_len = length;
        self.thread = c.CreateThread(null, 256 * 1024, serve, self, c.STACK_SIZE_PARAM_IS_A_RESERVATION, null) orelse return error.ThreadUnavailable;
        return self;
    }

    /// Stops accepting and joins the server thread. Idempotent.
    pub fn finish(self: *Server) void {
        if (self.release != null) _ = c.SetEvent(self.release);
        if (self.listener != c.INVALID_SOCKET) {
            _ = c.closesocket(self.listener);
            self.listener = c.INVALID_SOCKET;
        }
        if (self.thread != null) {
            _ = c.WaitForSingleObject(self.thread, c.INFINITE);
            _ = c.CloseHandle(self.thread);
            self.thread = null;
        }
    }
    pub fn deinit(self: *Server) void {
        self.finish();
        for ([_]*c.HANDLE{ &self.stop, &self.release, &self.arrived, &self.cut }) |handle| if (handle.* != null) {
            _ = c.CloseHandle(handle.*);
            handle.* = null;
        };
        if (self.winsock) _ = c.WSACleanup();
        _ = c.VirtualFree(self, 0, c.MEM_RELEASE);
    }

    pub fn opener(self: *Server) channel.Opener {
        return .{ .context = self, .open_fn = openFixture };
    }
    fn openFixture(context: *anyopaque, target: *channel.Channel, stop: c.HANDLE) anyerror!void {
        const self: *Server = @ptrCast(@alignCast(context));
        try target.open(stop, self.port, 2000, Gate{ .server = self }, "");
    }
    const Gate = struct {
        server: *Server,
        pub fn verify(self: Gate, client: identity.Endpoint, server: identity.Endpoint) !void {
            if (!identity.verifyPeer(&self.server.scratch, self.server.expected, self.server.path[0..self.server.path_len], client, server)) return error.UntrustedConnectedPeer;
        }
    };

    pub fn logged(self: *Server, needle: []const u8) bool {
        c.AcquireSRWLockExclusive(@ptrCast(&self.lock));
        defer c.ReleaseSRWLockExclusive(@ptrCast(&self.lock));
        return std.mem.indexOf(u8, self.log[0..self.log_len], needle) != null;
    }
    /// Requests whose request line contains `needle` (first 256 requests).
    pub fn countLines(self: *Server, needle: []const u8) usize {
        c.AcquireSRWLockExclusive(@ptrCast(&self.lock));
        defer c.ReleaseSRWLockExclusive(@ptrCast(&self.lock));
        var count: usize = 0;
        for (0..@min(self.requests, self.lines.len)) |index| {
            if (std.mem.indexOf(u8, self.lines[index][0..self.line_lens[index]], needle) != null) count += 1;
        }
        return count;
    }
    pub fn requestCount(self: *Server) usize {
        c.AcquireSRWLockExclusive(@ptrCast(&self.lock));
        defer c.ReleaseSRWLockExclusive(@ptrCast(&self.lock));
        return self.requests;
    }
    pub fn copyLastBody(self: *Server, out: []u8) []const u8 {
        c.AcquireSRWLockExclusive(@ptrCast(&self.lock));
        defer c.ReleaseSRWLockExclusive(@ptrCast(&self.lock));
        const n = @min(out.len, self.last_body_len);
        @memcpy(out[0..n], self.last_body[0..n]);
        return out[0..n];
    }
    /// Waits until at least `count` requests arrived, bounded by `timeout_ms`.
    pub fn waitRequests(self: *Server, count: usize, timeout_ms: u32) bool {
        const deadline = c.GetTickCount64() + timeout_ms;
        while (self.requestCount() < count) {
            const now = c.GetTickCount64();
            if (now >= deadline) return false;
            _ = c.WaitForSingleObject(self.arrived, @intCast(deadline - now));
        }
        return true;
    }

    pub fn uploadCounts(self: *Server) Counts {
        c.AcquireSRWLockExclusive(@ptrCast(&self.lock));
        defer c.ReleaseSRWLockExclusive(@ptrCast(&self.lock));
        return self.counts;
    }
    /// Waits until every field of the counts reaches `minimum`, bounded by `timeout_ms`.
    pub fn waitCounts(self: *Server, minimum: Counts, timeout_ms: u32) bool {
        const deadline = c.GetTickCount64() + timeout_ms;
        while (true) {
            const now_counts = self.uploadCounts();
            var reached = true;
            inline for (@typeInfo(Counts).@"struct".field_names) |field_name| {
                if (@field(now_counts, field_name) < @field(minimum, field_name)) reached = false;
            }
            if (reached) return true;
            const now = c.GetTickCount64();
            if (now >= deadline) return false;
            _ = c.WaitForSingleObject(self.arrived, @intCast(deadline - now));
        }
    }
    /// Copies received line `index` (the first `max_upload_lines` are kept).
    pub fn copyUploadLine(self: *Server, index: usize, out: []u8) ?UploadLine {
        c.AcquireSRWLockExclusive(@ptrCast(&self.lock));
        defer c.ReleaseSRWLockExclusive(@ptrCast(&self.lock));
        if (index >= @min(self.counts.lines, max_upload_lines)) return null;
        const line = self.uploaded[self.line_start[index]..self.line_end[index]];
        const n = @min(out.len, line.len);
        @memcpy(out[0..n], line[0..n]);
        return .{ .stream = self.line_stream[index], .bytes = out[0..n] };
    }
    /// Ends the upload in progress: terminating chunk, then close.
    pub fn endStream(self: *Server) void {
        _ = c.SetEvent(self.cut);
    }

    fn serve(context: ?*anyopaque) callconv(.winapi) c.DWORD {
        const self: *Server = @ptrCast(@alignCast(context.?));
        var accepted: usize = 0;
        while (accepted < 64) : (accepted += 1) {
            const socket = c.accept(self.listener, null, null);
            if (socket == c.INVALID_SOCKET) return 0;
            self.connections += 1;
            self.connection(socket);
            _ = c.closesocket(socket);
        }
        return 0;
    }
    fn connection(self: *Server, socket: c.SOCKET) void {
        var served: usize = 0;
        while (served < 4096) : (served += 1) {
            var received: usize = 0;
            var head_end: usize = 0;
            while (head_end == 0) {
                if (received == self.request.len) return;
                const count = c.recv(socket, @ptrCast(self.request[received..].ptr), @intCast(self.request.len - received), 0);
                if (count <= 0) return;
                received += @intCast(count);
                if (std.mem.indexOf(u8, self.request[0..received], "\r\n\r\n")) |end| head_end = end + 4;
            }
            const length = contentLength(self.request[0..head_end]);
            while (received < head_end + length) {
                if (received == self.request.len) return;
                const count = c.recv(socket, @ptrCast(self.request[received..].ptr), @intCast(self.request.len - received), 0);
                if (count <= 0) return;
                received += @intCast(count);
            }
            const step = self.record(self.request[0 .. head_end + length], self.request[head_end .. head_end + length]);
            if (step.upload) return self.upload(socket, step, self.request[0..head_end], received - head_end);
            const keep = self.respond(socket, step);
            if (step.drain) return self.drain(socket, received - (head_end + length));
            if (!keep) return;
        }
    }
    fn bump(self: *Server, comptime field: []const u8, amount: usize) void {
        c.AcquireSRWLockExclusive(@ptrCast(&self.lock));
        @field(self.counts, field) += amount;
        c.ReleaseSRWLockExclusive(@ptrCast(&self.lock));
        _ = c.SetEvent(self.arrived);
    }
    fn drain(self: *Server, socket: c.SOCKET, already: usize) void {
        if (already != 0) self.bump("rejected_body_bytes", already);
        var reads: usize = 0;
        while (reads < 4096) : (reads += 1) {
            const n = c.recv(socket, @ptrCast(&self.request), self.request.len, 0);
            if (n <= 0) return;
            self.bump("rejected_body_bytes", @intCast(n));
        }
    }

    /// Serves one upload on `socket`; `pending` bytes of the body already sit
    /// in `request` right after the head. The connection closes afterwards.
    fn upload(self: *Server, socket: c.SOCKET, step: Step, head: []const u8, pending: usize) void {
        var text: [256]u8 = undefined;
        if (std.mem.indexOf(u8, head, "\r\nTransfer-Encoding: chunked\r\n") == null) {
            _ = sendAll(socket, "HTTP/1.1 400 Bad Request\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
            return;
        }
        const answer = std.fmt.bufPrint(&text, "HTTP/1.1 {s}\r\nTransfer-Encoding: chunked\r\n\r\n", .{step.status}) catch return;
        if (!sendAll(socket, answer)) return;
        var reader: UploadReader = .{ .server = self, .socket = socket, .end = pending };
        @memmove(self.request[0..pending], self.request[head.len..][0..pending]);
        reader.event = c.WSACreateEvent();
        if (reader.event == null) return;
        defer _ = c.WSACloseEvent(reader.event);
        if (c.WSAEventSelect(socket, reader.event, c.FD_READ | c.FD_CLOSE) != 0) return;
        c.AcquireSRWLockExclusive(@ptrCast(&self.lock));
        self.counts.streams += 1;
        const stream = self.counts.streams;
        c.ReleaseSRWLockExclusive(@ptrCast(&self.lock));
        _ = c.SetEvent(self.arrived);
        var stream_lines: usize = 0;
        var line_start = self.uploadedEnd();
        var line_len: usize = 0;
        var chunks: usize = 0;
        while (chunks < 1 << 20) : (chunks += 1) {
            const size = reader.chunkSize() catch |err| return self.uploadOver(socket, err);
            if (size == 0) {
                self.bump("client_ends", 1);
                _ = sendAll(socket, "0\r\n\r\n");
                return;
            }
            var left = size;
            while (left > 0) : (left -= 1) {
                const value = reader.byte() catch |err| return self.uploadOver(socket, err);
                if (value != '\n') {
                    if (line_start + line_len < self.uploaded.len) self.uploaded[line_start + line_len] = value;
                    line_len += 1;
                    continue;
                }
                self.commitLine(stream, line_start, @min(line_len, self.uploaded.len - line_start));
                stream_lines += 1;
                line_start = self.uploadedEnd();
                line_len = 0;
                if (step.end_after_lines) |limit| if (stream_lines >= limit) return self.uploadOver(socket, error.Cut);
            }
            reader.crlf() catch |err| return self.uploadOver(socket, err);
        }
    }
    fn uploadedEnd(self: *Server) usize {
        c.AcquireSRWLockExclusive(@ptrCast(&self.lock));
        defer c.ReleaseSRWLockExclusive(@ptrCast(&self.lock));
        return self.uploaded_len;
    }
    fn commitLine(self: *Server, stream: usize, from: usize, kept: usize) void {
        c.AcquireSRWLockExclusive(@ptrCast(&self.lock));
        if (self.counts.lines < max_upload_lines) {
            self.line_start[self.counts.lines] = from;
            self.line_end[self.counts.lines] = from + kept;
            self.line_stream[self.counts.lines] = stream;
        }
        self.uploaded_len = from + kept;
        self.counts.lines += 1;
        c.ReleaseSRWLockExclusive(@ptrCast(&self.lock));
        _ = c.SetEvent(self.arrived);
    }
    fn uploadOver(self: *Server, socket: c.SOCKET, err: anyerror) void {
        switch (err) {
            error.Cut => {
                _ = sendAll(socket, "0\r\n\r\n");
                _ = c.shutdown(socket, c.SD_SEND);
                self.bump("server_ends", 1);
            },
            error.Released => {},
            else => self.bump("drops", 1),
        }
    }
    const UploadReader = struct {
        server: *Server,
        socket: c.SOCKET,
        event: c.WSAEVENT = null,
        start: usize = 0,
        end: usize,

        /// Next body byte; error.Cut on `endStream`, error.Released on `finish`.
        fn byte(self: *UploadReader) !u8 {
            const buffer = &self.server.request;
            while (self.start == self.end) {
                const n = c.recv(self.socket, @ptrCast(buffer), buffer.len, 0);
                if (n > 0) {
                    self.start = 0;
                    self.end = @intCast(n);
                    break;
                }
                if (n == 0 or c.WSAGetLastError() != c.WSAEWOULDBLOCK) return error.Closed;
                const handles = [_]c.HANDLE{ self.event, self.server.cut, self.server.release };
                const result = c.WaitForMultipleObjects(handles.len, &handles, 0, c.INFINITE);
                if (result == c.WAIT_OBJECT_0 + 1) return error.Cut;
                if (result != c.WAIT_OBJECT_0) return error.Released;
                var events: c.WSANETWORKEVENTS = std.mem.zeroes(c.WSANETWORKEVENTS);
                _ = c.WSAEnumNetworkEvents(self.socket, self.event, &events);
            }
            const value = buffer[self.start];
            self.start += 1;
            return value;
        }
        fn line(self: *UploadReader, out: []u8) ![]const u8 {
            var length: usize = 0;
            while (length < out.len) {
                const value = try self.byte();
                if (value == '\n') return std.mem.trimEnd(u8, out[0..length], "\r");
                out[length] = value;
                length += 1;
            }
            return error.InvalidChunk;
        }
        fn chunkSize(self: *UploadReader) !usize {
            var text: [32]u8 = undefined;
            const size = std.fmt.parseInt(usize, try self.line(&text), 16) catch return error.InvalidChunk;
            if (size == 0) _ = try self.line(&text);
            return size;
        }
        fn crlf(self: *UploadReader) !void {
            var text: [4]u8 = undefined;
            if ((try self.line(&text)).len != 0) return error.InvalidChunk;
        }
    };
    fn record(self: *Server, whole: []const u8, body: []const u8) Step {
        c.AcquireSRWLockExclusive(@ptrCast(&self.lock));
        defer c.ReleaseSRWLockExclusive(@ptrCast(&self.lock));
        const n = @min(whole.len, self.log.len - self.log_len);
        @memcpy(self.log[self.log_len..][0..n], whole[0..n]);
        self.log_len += n;
        if (self.requests < self.lines.len) {
            const end = std.mem.indexOfScalar(u8, whole, 13) orelse whole.len;
            const keep = @min(end, self.lines[0].len);
            @memcpy(self.lines[self.requests][0..keep], whole[0..keep]);
            self.line_lens[self.requests] = keep;
        }
        const b = @min(body.len, self.last_body.len);
        @memcpy(self.last_body[0..b], body[0..b]);
        self.last_body_len = b;
        self.requests += 1;
        _ = c.SetEvent(self.arrived);
        if (self.step >= self.options.script.len) return .{ .status = "500 Internal Server Error", .body = "", .close_after = true };
        const step = self.options.script[self.step];
        if (!step.repeat) self.step += 1;
        return step;
    }
    fn respond(self: *Server, socket: c.SOCKET, step: Step) bool {
        var head: [512]u8 = undefined;
        if (step.stream) |chunks| {
            const text = std.fmt.bufPrint(&head, "HTTP/1.1 {s}\r\nContent-Type: application/x-ndjson\r\nTransfer-Encoding: chunked\r\n\r\n", .{step.status}) catch return false;
            if (!sendAll(socket, text)) return false;
            for (chunks) |chunk| {
                const size = std.fmt.bufPrint(&head, "{x}\r\n", .{chunk.len}) catch return false;
                if (!sendAll(socket, size) or !sendAll(socket, chunk) or !sendAll(socket, "\r\n")) return false;
                // Separate writes so the client sees real chunk boundaries.
                _ = c.WaitForSingleObject(self.release, 10);
            }
            if (step.end_stream) return sendAll(socket, "0\r\n\r\n") and !step.close_after;
            if (step.stop_after) _ = c.SetEvent(self.stop);
            _ = c.WaitForSingleObject(self.release, 10000);
            return false;
        }
        const body = step.body orelse "";
        const text = if (step.body == null)
            std.fmt.bufPrint(&head, "HTTP/1.1 {s}\r\n{s}\r\n", .{ step.status, if (step.close_after) "Connection: close\r\n" else "" }) catch return false
        else
            std.fmt.bufPrint(&head, "HTTP/1.1 {s}\r\nContent-Type: application/json\r\nContent-Length: {d}\r\n{s}\r\n", .{ step.status, body.len, if (step.close_after) "Connection: close\r\n" else "" }) catch return false;
        if (!sendAll(socket, text) or !sendAll(socket, body)) return false;
        if (step.close_after) return false;
        if (step.fin_after) {
            _ = c.WaitForSingleObject(self.release, 50);
            return false;
        }
        return true;
    }
};

fn contentLength(head: []const u8) usize {
    var lines = std.mem.splitSequence(u8, head, "\r\n");
    while (lines.next()) |line| {
        const colon = std.mem.indexOfScalar(u8, line, ':') orelse continue;
        if (std.ascii.eqlIgnoreCase(line[0..colon], "Content-Length"))
            return std.fmt.parseInt(usize, std.mem.trim(u8, line[colon + 1 ..], " \t"), 10) catch 0;
    }
    return 0;
}
fn sendAll(socket: c.SOCKET, bytes: []const u8) bool {
    var sent: usize = 0;
    while (sent < bytes.len) {
        const n = c.send(socket, @ptrCast(bytes[sent..].ptr), @intCast(bytes.len - sent), 0);
        if (n <= 0) return false;
        sent += @intCast(n);
    }
    return true;
}
