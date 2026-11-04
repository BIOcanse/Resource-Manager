const std = @import("std");
const root = @import("http_socket");
const socket = root.socket;
const io = socket.io;
const w = socket.win;
const expect = std.testing.expect;
const equal = std.testing.expectEqual;

// Test-only capacities/timings, deliberately small to force split operations.
fn config(family: io.Family) socket.Config {
    return .{ .family = family, .port = 0, .backlog = 4, .max_io_bytes = 7, .socket_receive_bytes = 1024, .socket_send_bytes = 1024, .thread_stack_bytes = 256 * 1024 };
}
fn next(host: *socket.Host, kind: io.Kind) !io.Completion {
    const result = (try host.poll(3000)) orelse return error.CompletionTimedOut;
    try equal(kind, result.kind);
    return result;
}
fn client(host: *socket.Host) !w.Socket {
    // Windows requires WSA_FLAG_OVERLAPPED even for synchronous send/recv when
    // SO_RCVTIMEO/SO_SNDTIMEO are used; without it the timeout is ignored.
    // https://learn.microsoft.com/windows/win32/api/winsock2/nf-winsock2-wsasocketw
    const s = w.WSASocketW(if (host.config.family == .ipv4) w.af_inet else w.af_inet6, 1, 6, null, 0, 0x81);
    if (s == w.invalid_socket) return error.ClientSocketFailed;
    errdefer _ = w.closesocket(s);
    const ms: u32 = 3000;
    if (w.setsockopt(s, w.sol_socket, 0x1005, &ms, @sizeOf(u32)) != 0 or
        w.setsockopt(s, w.sol_socket, 0x1006, &ms, @sizeOf(u32)) != 0) return error.ClientTimeoutFailed;
    if (host.config.family == .ipv4) {
        const address: w.Address4 = .{ .port = std.mem.nativeToBig(u16, host.port) };
        if (w.connect(s, &address, @sizeOf(w.Address4)) != 0) return error.ClientConnectFailed;
    } else {
        const address: w.Address6 = .{ .port = std.mem.nativeToBig(u16, host.port) };
        if (w.connect(s, &address, @sizeOf(w.Address6)) != 0) return error.ClientConnectFailed;
    }
    return s;
}
fn write(s: w.Socket, data: []const u8) !void {
    var sent: usize = 0;
    while (sent < data.len) {
        const n = w.send(s, data[sent..].ptr, @intCast(data.len - sent), 0);
        if (n <= 0) return error.ClientSendFailed;
        sent += @intCast(n);
    }
}
fn read(s: w.Socket, data: []u8) !void {
    var got: usize = 0;
    while (got < data.len) {
        const n = w.recv(s, data[got..].ptr, @intCast(data.len - got), 0);
        if (n <= 0) return error.ClientReceiveFailed;
        got += @intCast(n);
    }
}
// RuntimeRead: Sampled
fn drain(host: *socket.Host) !void {
    host.beginStop();
    const deadline = w.GetTickCount64() + 3000;
    while (w.GetTickCount64() < deadline) {
        const completion = (try host.poll(3000)) orelse return error.StopTimedOut;
        if (completion.kind == .stopped) {
            try equal(@as(usize, 0), host.outstanding);
            return;
        }
    }
    return error.StopTimedOut;
}
fn tableEndpoint(value: io.Endpoint) root.tcp_table.Endpoint {
    return .{ .family = if (value.family == .ipv4) 2 else 23, .port = value.port, .address = value.address, .scope = value.scope };
}
// Test-only observation buffer, allocated to the actual table length, never a
// product allocator. Production proof continues to use the Ledger TCP owner.
fn owner(local: io.Endpoint, remote: io.Endpoint) !?u32 {
    var length: u32 = 0;
    const family: u32 = if (local.family == .ipv4) 2 else 23;
    try equal(@as(u32, 122), w.GetExtendedTcpTable(null, &length, 0, family, 4, 0));
    if (length > 1024 * 1024) return error.TestTableCapacity;
    const buffer = try std.testing.allocator.alloc(u8, length);
    defer std.testing.allocator.free(buffer);
    try equal(@as(u32, 0), w.GetExtendedTcpTable(buffer.ptr, &length, 0, family, 4, 0));
    return try root.tcp_table.find(buffer[0..length], .{ .client = tableEndpoint(local), .server = tableEndpoint(remote) });
}

fn roundTrip(family: io.Family) !void {
    var slots: [2]socket.Slot = undefined;
    var host: socket.Host = undefined;
    try host.init(&slots, config(family));
    defer host.deinit();
    try expect(host.port != 0 and host.port != 9321 and host.port != 9333);
    const c = try client(&host);
    defer _ = w.closesocket(c);
    // AcceptEx completes before ANY request bytes; no first-byte deadlock.
    const accepted = try next(&host, .accepted);
    const facts = try host.facts(accepted.connection);
    const client_local = try socket.endpoint(c, false);
    const client_remote = try socket.endpoint(c, true);
    try expect(std.meta.eql(facts.remote, client_local));
    try expect(std.meta.eql(facts.local, client_remote));
    try equal(@as(?u32, w.GetCurrentProcessId()), try owner(facts.local, facts.remote));
    try equal(@as(?u32, w.GetCurrentProcessId()), try owner(facts.remote, facts.local));
    // Same tuple is queried twice: no cached identity or separate preflight.
    try equal(@as(?u32, w.GetCurrentProcessId()), try owner(facts.local, facts.remote));
    const transport = host.transport();
    const input = "ab\xc3\xa9/cdefghijklmnop";
    const output = "HTTP opaque bytes split across many Winsock sends";
    var receive_buffer: [64]u8 = undefined;
    try transport.receive(accepted.connection, &receive_buffer);
    try std.testing.expectError(error.ReceiveBusy, transport.receive(accepted.connection, &receive_buffer));
    try transport.send(accepted.connection, output);
    try std.testing.expectError(error.SendBusy, transport.send(accepted.connection, output));
    try write(c, input[0..3]); // Split UTF-8; platform must not reinterpret it.
    try write(c, input[3..]);
    var received: usize = 0;
    var sends: usize = 0;
    while (received != input.len or sends == 0) {
        const completion = (try host.poll(3000)) orelse return error.CompletionTimedOut;
        try expect(std.meta.eql(accepted.connection, completion.connection));
        try equal(io.Outcome.ok, completion.outcome);
        switch (completion.kind) {
            .received => {
                try expect(completion.data.ptr == receive_buffer[0..].ptr);
                try expect(completion.transferred <= host.config.max_io_bytes);
                try std.testing.expectEqualSlices(u8, input[received..][0..completion.data.len], completion.data);
                received += completion.data.len;
                if (received != input.len) try transport.receive(accepted.connection, &receive_buffer);
            },
            .sent => {
                sends += 1;
                try equal(output.len, completion.transferred);
            },
            else => return error.UnexpectedCompletion,
        }
    }
    try equal(@as(usize, 1), sends);
    var actual: [output.len]u8 = undefined;
    try read(c, &actual);
    try std.testing.expectEqualSlices(u8, output, &actual);
    try drain(&host);
}

test "real IPv4 full duplex segmented IO, actual two-ended TCP identity" {
    try roundTrip(.ipv4);
}
test "real IPv6 loopback facts and segmented IO" {
    // TUN denial is reported as a conditional environmental skip, not success.
    roundTrip(.ipv6) catch |err| {
        if ((err == error.BindFailed or err == error.ClientConnectFailed) and w.WSAGetLastError() == 10013)
            return error.SkipZigTest;
        return err;
    };
}

test "FIN is half close; final response still sends before explicit close" {
    var slots: [1]socket.Slot = undefined;
    var host: socket.Host = undefined;
    try host.init(&slots, config(.ipv4));
    defer host.deinit();
    const c = try client(&host);
    defer _ = w.closesocket(c);
    const id = (try next(&host, .accepted)).connection;
    var buffer: [16]u8 = undefined;
    try host.receive(id, &buffer);
    try equal(@as(c_int, 0), w.shutdown(c, 1));
    const eof = try next(&host, .received);
    try equal(io.Outcome.eof, eof.outcome);
    try std.testing.expectError(error.ReceiveEnded, host.receive(id, &buffer));
    try host.send(id, "final");
    try equal(io.Outcome.ok, (try next(&host, .sent)).outcome);
    var actual: [5]u8 = undefined;
    try read(c, &actual);
    try std.testing.expectEqualSlices(u8, "final", &actual);
    try host.close(id);
    _ = try next(&host, .closed);
    try drain(&host);
}

test "RST reports receive failure and drains before reuse" {
    var slots: [1]socket.Slot = undefined;
    var host: socket.Host = undefined;
    try host.init(&slots, config(.ipv4));
    defer host.deinit();
    var c = try client(&host);
    defer if (c != w.invalid_socket) {
        _ = w.closesocket(c);
    };
    const id = (try next(&host, .accepted)).connection;
    var buffer: [16]u8 = undefined;
    try host.receive(id, &buffer);
    const linger = extern struct { on: u16, seconds: u16 }{ .on = 1, .seconds = 0 };
    try equal(@as(c_int, 0), w.setsockopt(c, w.sol_socket, 0x80, &linger, @sizeOf(@TypeOf(linger))));
    _ = w.closesocket(c);
    c = w.invalid_socket;
    try equal(io.Outcome.failed, (try next(&host, .received)).outcome);
    _ = try next(&host, .closed);
    try std.testing.expectError(error.StaleConnection, host.send(id, "late"));
    try drain(&host);
}

test "explicit connection capacity, closed notification and stale slot rejection" {
    var slots: [1]socket.Slot = undefined;
    var host: socket.Host = undefined;
    try host.init(&slots, config(.ipv4));
    defer host.deinit();
    const c1 = try client(&host);
    defer _ = w.closesocket(c1);
    const first = (try next(&host, .accepted)).connection;
    const c2 = try client(&host);
    defer _ = w.closesocket(c2);
    try expect(try host.poll(30) == null); // Backlog is not another owner slot.
    try host.close(first);
    try expect(std.meta.eql(first, (try next(&host, .closed)).connection));
    try std.testing.expectError(error.StaleConnection, host.send(first, "old"));
    const second = (try next(&host, .accepted)).connection;
    try equal(first.slot, second.slot);
    try expect(first.serial != second.serial);
    try std.testing.expectError(error.StaleConnection, host.close(first));
    try host.send(second, "new");
    _ = try next(&host, .sent);
    var actual: [3]u8 = undefined;
    try read(c2, &actual);
    try std.testing.expectEqualSlices(u8, "new", &actual);
    try drain(&host);
}

test "stop with pending accept receive and slow-reader send returns each borrow once" {
    var slots: [2]socket.Slot = undefined;
    var host: socket.Host = undefined;
    var options = config(.ipv4);
    options.max_io_bytes = 16 * 1024;
    try host.init(&slots, options);
    defer host.deinit();
    const c = try client(&host);
    defer _ = w.closesocket(c);
    const id = (try next(&host, .accepted)).connection;
    var receive_buffer: [16]u8 = undefined;
    const output = try std.testing.allocator.alloc(u8, 128 * 1024);
    defer std.testing.allocator.free(output);
    @memset(output, 's');
    try host.receive(id, &receive_buffer);
    try host.send(id, output);
    try host.requestStop();
    var receives: usize = 0;
    var sends: usize = 0;
    var closed: usize = 0;
    var steps: usize = 0;
    while (steps < 64) : (steps += 1) {
        const completion = (try host.poll(3000)) orelse return error.StopTimedOut;
        switch (completion.kind) {
            .received => {
                receives += 1;
                try equal(io.Outcome.cancelled, completion.outcome);
            },
            .sent => {
                sends += 1;
                try equal(io.Outcome.cancelled, completion.outcome);
            },
            .closed => {
                if (std.meta.eql(id, completion.connection)) {
                    closed += 1;
                    try equal(@as(usize, 1), receives);
                    try equal(@as(usize, 1), sends);
                }
            },
            .stopped => break,
            else => return error.UnexpectedCompletion,
        }
    } else return error.StopTimedOut;
    try equal(@as(usize, 1), receives);
    try equal(@as(usize, 1), sends);
    try equal(@as(usize, 1), closed);
    try equal(@as(usize, 0), host.outstanding);
    // The two caller buffers can now be released; no lease is stored in Host.
}

test "rejected replacement proof sends zero bytes on actual connected socket" {
    var slots: [1]socket.Slot = undefined;
    var host: socket.Host = undefined;
    try host.init(&slots, config(.ipv4));
    defer host.deinit();
    const c = try client(&host);
    defer _ = w.closesocket(c);
    const id = (try next(&host, .accepted)).connection;
    const Proof = struct {
        calls: usize = 0,
        fn reject(context: ?*anyopaque, facts: io.Facts) bool {
            const self: *@This() = @ptrCast(@alignCast(context.?));
            self.calls += 1;
            // Test a mismatching identity on the actual tuple, not a fake socket.
            return (owner(facts.local, facts.remote) catch return false) == 0;
        }
    };
    var proof: Proof = .{};
    try std.testing.expectError(error.UntrustedPeer, host.sendVerified(id, "credential", &proof, Proof.reject));
    try equal(@as(usize, 1), proof.calls);
    try equal(@as(usize, 0), host.outstanding);
    const ms: u32 = 50;
    try equal(@as(c_int, 0), w.setsockopt(c, w.sol_socket, 0x1006, &ms, @sizeOf(u32)));
    var byte: [1]u8 = undefined;
    try equal(@as(c_int, -1), w.recv(c, &byte, 1, 0));
    try equal(@as(c_int, 10060), w.WSAGetLastError());
    try drain(&host);
}

fn handles() !u32 {
    var count: u32 = 0;
    if (w.GetProcessHandleCount(w.invalid_handle, &count) == 0) return error.HandleCountUnavailable;
    return count;
}
test "repeated start stop with explicit dispatcher stack leaves no handles or IO" {
    const Dispatch = struct {
        stopped: bool = false,
        fn call(context: ?*anyopaque, _: *socket.Host, completion: io.Completion) void {
            const self: *@This() = @ptrCast(@alignCast(context.?));
            if (completion.kind == .stopped) self.stopped = true;
        }
    };
    // Warm Winsock's process-wide provider state before the handle baseline.
    var startup: w.WsaData = undefined;
    try equal(@as(c_int, 0), w.WSAStartup(0x0202, &startup));
    defer _ = w.WSACleanup();
    var slots: [2]socket.Slot = undefined;
    var host: socket.Host = undefined;
    try host.init(&slots, config(.ipv4));
    try drain(&host);
    host.deinit();
    const before = try handles();
    for (0..12) |_| {
        try host.init(&slots, config(.ipv4));
        var state: Dispatch = .{};
        try host.requestStop();
        try host.run(&state, Dispatch.call);
        try expect(state.stopped);
        try equal(@as(usize, 0), host.outstanding);
        host.deinit();
        try equal(before, try handles());
    }
}

test "invalid capacities fail before allocating any OS resource" {
    var slots: [1]socket.Slot = undefined;
    var host: socket.Host = undefined;
    try std.testing.expectError(error.InvalidConfig, host.init(slots[0..0], config(.ipv4)));
    var options = config(.ipv4);
    options.thread_stack_bytes = 0;
    try std.testing.expectError(error.InvalidConfig, host.init(&slots, options));
}
