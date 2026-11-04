const std = @import("std");
const root = @import("http_send");
const response = root.response;
const socket = root.socket;
const io = response.io;
const w = socket.win;
const expect = std.testing.expect;
const equal = std.testing.expectEqual;
const eq = std.testing.expectEqualSlices;
const id: io.ConnectionId = .{ .slot = 0, .serial = 1 };
const payload = "ab\xc3\xa9\n!";
fn limits() response.Limits {
    return .{ .header_bytes = 512, .header_count = 8, .pending_body_bytes = 256 * 1024, .final_output_bytes = 512 };
}
fn head(body: response.Body) response.Head {
    return .{ .status = 200, .reason = "OK", .headers = &.{.{ .name = "Content-Type", .value = "application/octet-stream" }},
        .body = body, .is_head = false, .keep_alive = true };
}
const Fake = struct {
    wire: [4096]u8 = undefined,
    used: usize = 0,
    pending: []const u8 = &.{},
    closes: usize = 0,
    sends: usize = 0,
    fail_at: usize = 0,
    source_borrow: bool = false,
    fn send(ctx: *anyopaque, _: io.ConnectionId, bytes: []const u8) !void {
        const self: *Fake = @ptrCast(@alignCast(ctx));
        try expect(!self.source_borrow);
        try expect(self.pending.len == 0);
        self.sends += 1;
        if (self.sends == self.fail_at) return error.InjectedSendFailure;
        self.pending = bytes;
    }
    fn receive(_: *anyopaque, _: io.ConnectionId, _: []u8) !void { return error.NotUsed; }
    fn close(ctx: *anyopaque, _: io.ConnectionId) !void { (@as(*Fake, @ptrCast(@alignCast(ctx)))).closes += 1; }
    fn transport(self: *Fake) io.Transport { return .{ .context = self, .send_fn = send, .receive_fn = receive, .close_fn = close }; }
    fn complete(self: *Fake, output: *response.Response, outcome: io.Outcome) ?response.Event {
        const bytes = self.pending;
        self.pending = &.{};
        if (outcome == .ok) {
            @memcpy(self.wire[self.used..][0..bytes.len], bytes);
            self.used += bytes.len;
        }
        return output.onCompletion(.{ .kind = .sent, .connection = id, .outcome = outcome, .transferred = if (outcome == .ok) bytes.len else 0 });
    }
};
test "final status/framing bytes: fixed, chunked, HEAD, 204 and 304" {
    for (0..5) |case| {
        var fake: Fake = .{};
        var headers: [512]u8 = undefined;
        var output = try response.Response.init(fake.transport(), id, &headers, limits());
        var metadata = head(if (case == 1) .chunked else .{ .fixed = payload.len });
        metadata.is_head = case == 2;
        if (case == 3) metadata.status = 204;
        if (case == 4) metadata.status = 304;
        try output.start(metadata);
        try equal(response.Event.headers_sent, fake.complete(&output, .ok).?);
        if (case < 2) {
            try output.write(payload);
            try std.testing.expectError(error.WouldBlock, output.write("late"));
            try std.testing.expectError(error.WouldBlock, output.finish());
            if (case == 1) { try expect(fake.complete(&output, .ok) == null); try expect(fake.complete(&output, .ok) == null); }
            try equal(response.Event.writable, fake.complete(&output, .ok).?);
        } else try std.testing.expectError(error.BodyForbidden, output.write(payload));
        const end = try output.finish();
        if (case == 1) {
            try expect(end == null);
            try equal(response.Event.ended, fake.complete(&output, .ok).?);
        } else try equal(response.Event.ended, end.?);
        const wire = fake.wire[0..fake.used];
        const split = std.mem.indexOf(u8, wire, "\r\n\r\n").? + 4;
        const cl = std.mem.indexOf(u8, wire[0..split], "Content-Length:") != null;
        const te = std.mem.indexOf(u8, wire[0..split], "Transfer-Encoding:") != null;
        try expect(!(cl and te));
        try equal(case != 1 and case != 3, cl);
        try equal(case == 1, te);
        try eq(u8, if (case == 0) payload else if (case == 1) "6\r\nab\xc3\xa9\n!\r\n0\r\n\r\n" else "", wire[split..]);
    }
}
test "reject header injection, reserved framing, bounds before bytes; no short fixed reuse" {
    var fake: Fake = .{};
    var headers: [512]u8 = undefined;
    var output = try response.Response.init(fake.transport(), id, &headers, limits());
    var metadata = head(.{ .fixed = 1 });
    for ([_]response.Header{
        .{ .name = "X\r\nCL", .value = "v" }, .{ .name = "X", .value = "v\r\nCL: 1" },
        .{ .name = "", .value = "" }, .{ .name = "bad name", .value = "v" },
    }) |bad| {
        metadata.headers = &.{bad};
        try std.testing.expectError(error.InvalidHeader, output.start(metadata));
    }
    for ([_][]const u8{ "content-length", "TRANSFER-ENCODING", "Connection", "Trailer" }) |reserved| {
        metadata.headers = &.{.{ .name = reserved, .value = "1" }};
        try std.testing.expectError(error.ReservedHeader, output.start(metadata));
    }
    metadata.headers = &.{};
    metadata.reason = "OK\nwrong";
    try std.testing.expectError(error.InvalidStatus, output.start(metadata));
    try equal(@as(usize, 0), fake.sends);
    metadata.reason = "OK";
    var small_limits = limits();
    small_limits.header_count = 1;
    output = try response.Response.init(fake.transport(), id, &headers, small_limits);
    try std.testing.expectError(error.TooManyHeaders, output.start(metadata));
    small_limits = limits(); small_limits.header_bytes = 5;
    output = try response.Response.init(fake.transport(), id, &headers, small_limits);
    try std.testing.expectError(error.HeaderLimit, output.start(metadata));
    try equal(@as(usize, 0), fake.sends);
    output = try response.Response.init(fake.transport(), id, &headers, limits());
    try output.start(metadata);
    _ = fake.complete(&output, .ok);
    try std.testing.expectError(error.ContentLengthExceeded, output.write("xx"));
    try std.testing.expectError(error.ContentLengthMismatch, output.finish());
    try equal(@as(usize, 1), fake.closes);
    try std.testing.expectError(error.NotWritable, output.write("x"));
}
test "cancel retains final bytes to terminal; stale receive/sent ignored; immediate and chained failures" {
    for (0..4) |case| {
        var fake: Fake = .{};
        var headers: [512]u8 = undefined;
        var output = try response.Response.init(fake.transport(), id, &headers, limits());
        try output.start(head(.chunked));
        _ = fake.complete(&output, .ok);
        if (case == 3) fake.fail_at = 2;
        if (case == 3) {
            try std.testing.expectError(error.InjectedSendFailure, output.write(payload));
            try expect(!output.pending());
        } else {
            try output.write(payload);
            try expect(output.onCompletion(.{ .kind = .received, .connection = id }) == null);
            try expect(output.onCompletion(.{ .kind = .sent, .connection = .{ .slot = 0, .serial = 2 } }) == null);
            if (case == 0) {
                try expect(try output.cancel() == null);
                try expect(output.pending());
                try eq(u8, payload, output.body_bytes);
                try equal(response.Event.cancelled, fake.complete(&output, .cancelled).?);
            } else if (case == 1) try equal(response.Event.failed, fake.complete(&output, .failed).?)
            else {
                fake.fail_at = 3; // Prefix completes, body submission fails immediately.
                try equal(response.Event.failed, fake.complete(&output, .ok).?);
            }
            try expect(!output.pending());
        }
        try equal(@as(usize, 0), output.body_bytes.len);
        try expect(fake.closes >= 1);
    }
}

const Codec = struct {
    output: *response.FinalOutput,
    calls: usize = 0,
    borrowed: bool = true,
    fn reserve(ctx: ?*anyopaque, used: usize, ptr: *?[*]u8, capacity: *usize) callconv(.c) i32 {
        const self: *Codec = @ptrCast(@alignCast(ctx.?));
        const bytes = self.output.reserve(used) catch return -1;
        ptr.* = bytes.ptr; capacity.* = bytes.len;
        return 0;
    }
    fn commit(ctx: ?*anyopaque, used: usize) callconv(.c) i32 {
        const self: *Codec = @ptrCast(@alignCast(ctx.?));
        self.output.commit(used) catch return -1;
        return 0;
    }
    fn encode(self: *Codec, final: *response.FinalOutput) !usize {
        try expect(self.borrowed);
        self.calls += 1;
        var result: root.crash_codec.Response = undefined;
        const callbacks: root.crash_codec.OutputCallbacks = .{ .reserve = reserve, .commit = commit };
        const code = root.crash_codec.encodeStream(1, 0, self, null, &callbacks, &result);
        if (code != 0) return error.CodecOutputTooLarge;
        return final.used;
    }
};
test "existing synchronous crash codec encodes once into bounded final region; source ends before send" {
    var region: [512]u8 = undefined;
    var options = limits(); options.final_output_bytes = root.crash_codec.unknown_report.len;
    var final = try response.FinalOutput.init(&region, options);
    var codec: Codec = .{ .output = &final };
    try final.encodeOnce(&codec, Codec.encode);
    codec.borrowed = false; // End source borrow; send retains only final bytes.
    try std.testing.expectError(error.AlreadyEncoded, final.encodeOnce(&codec, Codec.encode));
    try eq(u8, root.crash_codec.unknown_report, try final.bytes());
    try equal(@as(usize, 1), codec.calls);
    var fake: Fake = .{ .source_borrow = codec.borrowed };
    var headers: [512]u8 = undefined;
    var output = try response.Response.init(fake.transport(), id, &headers, options);
    try output.start(head(.{ .fixed = (try final.bytes()).len }));
    _ = fake.complete(&output, .ok);
    try output.write(try final.bytes());
    try expect(fake.pending.ptr == region[0..].ptr);
    _ = fake.complete(&output, .ok);
    _ = try output.finish();
    options.final_output_bytes -= 1;
    final = try response.FinalOutput.init(&region, options);
    codec = .{ .output = &final };
    try std.testing.expectError(error.CodecOutputTooLarge, final.encodeOnce(&codec, Codec.encode));
    try std.testing.expectError(error.NotEncoded, final.bytes());
    try std.testing.expectError(error.AlreadyEncoded, final.encodeOnce(&codec, Codec.encode));
    try equal(@as(usize, 1), codec.calls);
}

fn config() socket.Config {
    return .{ .family = .ipv4, .port = 0, .backlog = 4, .max_io_bytes = 3,
        .socket_receive_bytes = 1024, .socket_send_bytes = 1024, .thread_stack_bytes = 256 * 1024 };
}
fn poll(host: *socket.Host) !io.Completion { return (try host.poll(10000)) orelse error.CompletionTimedOut; }
fn next(host: *socket.Host, kind: io.Kind) !io.Completion {
    const value = try poll(host); try equal(kind, value.kind); return value;
}
// RuntimeRead: Sampled
fn advance(host: *socket.Host, output: *response.Response) !response.Event {
    const deadline = w.GetTickCount64() + 10000;
    while (w.GetTickCount64() < deadline) {
        const completion = try poll(host);
        if (output.onCompletion(completion)) |event| return event;
    }
    return error.ResponseTimedOut;
}
// RuntimeRead: Sampled
fn drain(host: *socket.Host) !void {
    host.beginStop();
    const deadline = w.GetTickCount64() + 10000;
    while (w.GetTickCount64() < deadline) if ((try poll(host)).kind == .stopped) {
        try equal(@as(usize, 0), host.outstanding); return;
    };
    return error.DrainTimedOut;
}
fn client(host: *socket.Host) !w.Socket {
    const s = w.WSASocketW(w.af_inet, 1, 6, null, 0, 0x81);
    if (s == w.invalid_socket) return error.ClientSocket;
    errdefer _ = w.closesocket(s);
    const ms: u32 = 10000;
    const receive_bytes: u32 = 1024;
    if (w.setsockopt(s, w.sol_socket, w.so_rcvbuf, &receive_bytes, @sizeOf(u32)) != 0) return error.ClientBuffer;
    if (w.setsockopt(s, w.sol_socket, 0x1005, &ms, @sizeOf(u32)) != 0 or w.setsockopt(s, w.sol_socket, 0x1006, &ms, @sizeOf(u32)) != 0) return error.ClientTimeout;
    const address: w.Address4 = .{ .port = std.mem.nativeToBig(u16, host.port) };
    if (w.connect(s, &address, @sizeOf(w.Address4)) != 0) return error.ClientConnect;
    return s;
}
fn read(s: w.Socket, bytes: []u8) !void {
    var used: usize = 0;
    while (used < bytes.len) {
        const n = w.recv(s, bytes[used..].ptr, @intCast(bytes.len - used), 0);
        if (n <= 0) return error.ClientRead;
        used += @intCast(n);
    }
}
fn write(s: w.Socket, bytes: []const u8) !void {
    var used: usize = 0;
    while (used < bytes.len) {
        const n = w.send(s, bytes[used..].ptr, @intCast(bytes.len - used), 0);
        if (n <= 0) return error.ClientWrite;
        used += @intCast(n);
    }
}
fn readHead(s: w.Socket, bytes: []u8) ![]const u8 {
    var used: usize = 0;
    while (used < bytes.len) {
        try read(s, bytes[used..][0..1]); used += 1;
        if (std.mem.endsWith(u8, bytes[0..used], "\r\n\r\n")) return bytes[0..used];
    }
    return error.HeaderTooLarge;
}
test "real Zig reader: early headers, split UTF8/chunks, receive and send pending, ordered keepalive" {
    var slots: [1]socket.Slot = undefined;
    var host: socket.Host = undefined;
    try host.init(&slots, config()); defer host.deinit();
    try expect(host.port != 9321 and host.port != 9333);
    const c = try client(&host); defer _ = w.closesocket(c);
    const connection = (try next(&host, .accepted)).connection;
    var headers: [512]u8 = undefined;
    var received: [16]u8 = undefined;
    var output = try response.Response.init(host.transport(), connection, &headers, limits());
    try host.receive(connection, &received); // Body absent: receive stays pending.
    try output.start(head(.chunked));
    try expect(host.outstanding == 2); // Real receive + send, same socket.
    try equal(response.Event.headers_sent, try advance(&host, &output));
    var head_buffer: [512]u8 = undefined;
    const actual_head = try readHead(c, &head_buffer);
    try expect(std.mem.indexOf(u8, actual_head, "Transfer-Encoding: chunked") != null);
    // Client only produces upload AFTER receiving response headers.
    try write(c, "up");
    const upload = try next(&host, .received); try eq(u8, "up", upload.data);
    try host.receive(connection, &received);
    try output.write(payload[0..3]);
    try equal(response.Event.writable, try advance(&host, &output));
    try output.write(payload[3..]);
    try equal(response.Event.writable, try advance(&host, &output));
    try expect(try output.finish() == null);
    try equal(response.Event.ended, try advance(&host, &output));
    const expected = "3\r\nab\xc3\r\n3\r\n\xa9\n!\r\n0\r\n\r\n";
    var wire: [expected.len]u8 = undefined; try read(c, &wire); try eq(u8, expected, &wire);
    // New response after previous terminal chunk; no byte interleaving.
    output = try response.Response.init(host.transport(), connection, &headers, limits());
    try output.start(head(.{ .fixed = 0 }));
    try equal(response.Event.headers_sent, try advance(&host, &output));
    try equal(response.Event.ended, (try output.finish()).?);
    _ = try readHead(c, &head_buffer);
    try drain(&host);
}

test "real slow reader bounds production; cancel mid-send waits for terminal before region release" {
    var slots: [1]socket.Slot = undefined;
    var host: socket.Host = undefined;
    var options = config(); options.max_io_bytes = 1024;
    try host.init(&slots, options); defer host.deinit();
    const c = try client(&host); defer _ = w.closesocket(c);
    const connection = (try next(&host, .accepted)).connection;
    var headers: [512]u8 = undefined;
    var output = try response.Response.init(host.transport(), connection, &headers, limits());
    const bytes = try std.testing.allocator.alloc(u8, limits().pending_body_bytes);
    defer std.testing.allocator.free(bytes); // TEST allocator; product uses scoped Ledger region.
    @memset(bytes, 's');
    try output.start(head(.{ .fixed = bytes.len }));
    try equal(response.Event.headers_sent, try advance(&host, &output));
    var head_buffer: [512]u8 = undefined; _ = try readHead(c, &head_buffer);
    try output.write(bytes);
    // No client reads. Pump partial IO to a timeout: body cannot all fit the
    // explicitly tiny socket buffers; terminal completion must remain absent.
    try expect(try host.poll(50) == null);
    try expect(output.pending());
    try std.testing.expectError(error.WouldBlock, output.write("more"));
    try std.testing.expectError(error.WouldBlock, output.finish());
    try expect(try output.cancel() == null);
    try expect(output.pending());
    try expect(output.body_bytes.ptr == bytes.ptr);
    try equal(response.Event.cancelled, try advance(&host, &output));
    try expect(!output.pending());
    try equal(@as(usize, 0), output.body_bytes.len);
    _ = try next(&host, .closed);
    try drain(&host);
}
test "real RST during send reports failure without holding final region" {
    var slots: [1]socket.Slot = undefined;
    var host: socket.Host = undefined;
    var options = config(); options.max_io_bytes = 1024;
    try host.init(&slots, options); defer host.deinit();
    var c = try client(&host); defer if (c != w.invalid_socket) { _ = w.closesocket(c); };
    const connection = (try next(&host, .accepted)).connection;
    var headers: [512]u8 = undefined;
    var output = try response.Response.init(host.transport(), connection, &headers, limits());
    const bytes = try std.testing.allocator.alloc(u8, limits().pending_body_bytes); defer std.testing.allocator.free(bytes);
    @memset(bytes, 'f');
    try output.start(head(.{ .fixed = bytes.len }));
    _ = try advance(&host, &output);
    try output.write(bytes);
    const linger = extern struct { on: u16, seconds: u16 }{ .on = 1, .seconds = 0 };
    try equal(@as(c_int, 0), w.setsockopt(c, w.sol_socket, 0x80, &linger, @sizeOf(@TypeOf(linger))));
    _ = w.closesocket(c); c = w.invalid_socket;
    try equal(response.Event.failed, try advance(&host, &output));
    try expect(!output.pending());
    _ = try next(&host, .closed);
    try drain(&host);
}

fn dotnetReader(case: []const u8) !void {
    var slots: [2]socket.Slot = undefined;
    var host: socket.Host = undefined;
    try host.init(&slots, config()); defer host.deinit();
    try expect(host.port != 9321 and host.port != 9333);
    var port_buffer: [5]u8 = undefined;
    const port = try std.fmt.bufPrint(&port_buffer, "{d}", .{host.port});
    var child = try std.process.spawn(std.testing.io, .{
        .argv = &.{ "pwsh", "-NoProfile", "-NonInteractive", "-File", "tools/validation/Read-HttpResponse.ps1", "-Port", port, "-Case", case },
        .create_no_window = true,
    });
    defer child.kill(std.testing.io);
    const connection = (try next(&host, .accepted)).connection;
    var request: [2048]u8 = undefined;
    var used: usize = 0;
    while (used < request.len) {
        try host.receive(connection, request[used..]);
        const part = try next(&host, .received);
        if (part.outcome != .ok) return error.RequestEnded;
        used += part.data.len;
        if (std.mem.indexOf(u8, request[0..used], "\r\n\r\n") != null) break;
    }
    if (used == request.len) return error.RequestHeaderTooLarge;
    var metadata = head(if (std.mem.eql(u8, case, "chunked")) .chunked else .{ .fixed = payload.len });
    metadata.is_head = std.mem.eql(u8, case, "head");
    if (std.mem.eql(u8, case, "no-content")) metadata.status = 204;
    if (std.mem.eql(u8, case, "not-modified")) metadata.status = 304;
    var headers: [512]u8 = undefined;
    var output = try response.Response.init(host.transport(), connection, &headers, limits());
    try output.start(metadata);
    try equal(response.Event.headers_sent, try advance(&host, &output));
    // New peer only connects after HttpClient's ResponseHeadersRead has returned.
    const signal = (try next(&host, .accepted)).connection;
    try expect(!std.meta.eql(connection, signal));
    if (metadata.status == 200 and !metadata.is_head) {
        try output.write(payload[0..3]);
        try equal(response.Event.writable, try advance(&host, &output));
        try output.write(payload[3..]);
        try equal(response.Event.writable, try advance(&host, &output));
    }
    if (try output.finish() == null) try equal(response.Event.ended, try advance(&host, &output));
    // Child has its own 15s HTTP timeout; bound process exit too (no idle host).
    try equal(@as(u32, 0), w.WaitForSingleObject(child.id.?, 20000));
    try expect((try child.wait(std.testing.io)).success());
    try drain(&host);
}
test "real .NET HttpClient: fixed / chunked / HEAD / 204 / 304, early headers and split writes" {
    for ([_][]const u8{ "fixed", "chunked", "head", "no-content", "not-modified" }) |case| try dotnetReader(case);
}
