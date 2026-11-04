const std = @import("std");
const root = @import("http_common");
const connection = root.connection;
const host = root.host;
const socket = root.socket;
const io = connection.io;
const w = socket.win;
const expect = std.testing.expect;
const equal = std.testing.expectEqual;
const eq = std.testing.expectEqualSlices;

// These are explicit TEST capacities/timings, never product host defaults.
const test_routes = [_]host.Route{.{ .method = "POST", .template = "/__test/http/anonymous-echo", .requirement = .anonymous, .handler = .echo }};
fn config() connection.Config {
    return .{
        .dispatch = .{ .startup = .{ .profile_id = "native-test", .allowed = 0x3f }, .normal_actions_allowed = true, .test_routes = true, .family_routes = &test_routes },
        .parse = .{ .request_line_bytes = 1024, .header_bytes = 2048, .header_count = 16, .trailer_bytes = 1024, .trailer_count = 8, .chunk_line_bytes = 128, .body_bytes = 4096, .keep_alive_ns = null, .headers_ns = null, .body_ns = null, .body_rate = null },
        .send = .{ .header_bytes = 1024, .header_count = 16, .pending_body_bytes = 8192, .final_output_bytes = 8192 },
    };
}
fn tableEndpoint(value: io.Endpoint) root.tcp_table.Endpoint {
    return .{ .family = if (value.family == .ipv4) 2 else 23, .port = value.port, .address = value.address, .scope = value.scope };
}
fn owner(local: io.Endpoint, remote: io.Endpoint) !?u32 {
    var length: u32 = 0;
    const family: u32 = if (local.family == .ipv4) 2 else 23;
    try equal(@as(u32, 122), w.GetExtendedTcpTable(null, &length, 0, family, 4, 0));
    if (length > 1024 * 1024) return error.TestTableCapacity;
    const bytes = try std.testing.allocator.alloc(u8, length);
    defer std.testing.allocator.free(bytes);
    try equal(@as(u32, 0), w.GetExtendedTcpTable(bytes.ptr, &length, 0, family, 4, 0));
    return try root.tcp_table.find(bytes[0..length], .{ .client = tableEndpoint(local), .server = tableEndpoint(remote) });
}
const Wire = struct {
    bytes: [8192]u8 = undefined,
    used: usize = 0,
    body_start: usize = 0,
    status: u16 = 0,
    fn body(self: *const Wire) []const u8 {
        return self.bytes[self.body_start..self.used];
    }
    fn headers(self: *const Wire) []const u8 {
        return self.bytes[0..self.body_start];
    }
};
const Fixture = struct {
    slots: [1]socket.Slot = undefined,
    platform: socket.Host = undefined,
    c: w.Socket = w.invalid_socket,
    state: connection.Connection = undefined,
    receive: [1024]u8 = undefined,
    metadata: [4096]u8 = undefined,
    headers: [16]host.Header = undefined,
    body: [4096]u8 = undefined,
    output: [8192]u8 = undefined,
    response_headers: [1024]u8 = undefined,
    family_state: [128]u8 align(8) = undefined,
    queued_wakes: usize = 0,
    wake_tickets: [64]u64 = undefined,
    wake_connections: [64]io.ConnectionId = undefined,
    proof_calls: usize = 0,
    proof_borrowed: bool = false,
    reject_proof: bool = false,
    sends: usize = 0,
    responses_started: usize = 0,
    responses_read: usize = 0,
    drained: bool = false,

    fn init(self: *Fixture, settings: connection.Config) !void {
        try self.platform.init(&self.slots, .{ .family = .ipv4, .port = 0, .backlog = 1, .max_io_bytes = 7, .socket_receive_bytes = 1024, .socket_send_bytes = 1024, .thread_stack_bytes = 256 * 1024 });
        errdefer self.platform.deinit();
        try expect(self.platform.port != 0 and self.platform.port != 9321 and self.platform.port != 9333);
        self.c = w.WSASocketW(w.af_inet, 1, 6, null, 0, 0x81);
        if (self.c == w.invalid_socket) return error.ClientSocketFailed;
        errdefer _ = w.closesocket(self.c);
        const ms: u32 = 3000;
        if (w.setsockopt(self.c, w.sol_socket, 0x1005, &ms, @sizeOf(u32)) != 0 or
            w.setsockopt(self.c, w.sol_socket, 0x1006, &ms, @sizeOf(u32)) != 0) return error.ClientTimeoutFailed;
        const address: w.Address4 = .{ .port = std.mem.nativeToBig(u16, self.platform.port) };
        if (w.connect(self.c, &address, @sizeOf(w.Address4)) != 0) return error.ClientConnectFailed;
        const accepted = (try self.platform.poll(3000)) orelse return error.AcceptTimedOut;
        try equal(io.Kind.accepted, accepted.kind);
        var prepared = settings;
        prepared.wake = .{ .context = self, .post = postWake };
        self.state = try connection.Connection.init(.{ .context = self, .receive_fn = receiveFn, .send_fn = sendFn, .close_fn = closeFn }, accepted.connection, try self.platform.facts(accepted.connection), prepared, .{ .receive = &self.receive, .metadata = &self.metadata, .headers = &self.headers, .body = &self.body, .output = &self.output, .response_headers = &self.response_headers, .family_state = &self.family_state }, .{ .context = self, .with_caller = prove }, 0);
        try self.state.start();
    }
    fn postWake(context: ?*anyopaque, ticket: u64) void {
        const self: *Fixture = @ptrCast(@alignCast(context.?));
        // An owner may post only while its Call is live. Capture the actual
        // socket serial now, never reconstruct it when delivering the event.
        self.enqueueWake(self.state.id, ticket);
    }
    fn enqueueWake(self: *Fixture, connection_id: io.ConnectionId, ticket: u64) void {
        std.debug.assert(self.queued_wakes < self.wake_tickets.len);
        self.wake_tickets[self.queued_wakes] = ticket;
        self.wake_connections[self.queued_wakes] = connection_id;
        self.queued_wakes += 1; // enqueue only: never reenter Connection here
    }
    fn dispatchApplication(self: *Fixture) !void {
        const count = self.queued_wakes;
        const tickets = self.wake_tickets;
        const connections = self.wake_connections;
        self.queued_wakes = 0;
        var index: usize = 0;
        while (index < count) : (index += 1) try self.state.onApplicationReady(connections[index], tickets[index], 0);
    }
    fn receiveFn(context: *anyopaque, id: io.ConnectionId, buffer: []u8) !void {
        return (@as(*Fixture, @ptrCast(@alignCast(context)))).platform.receive(id, buffer);
    }
    fn sendFn(context: *anyopaque, id: io.ConnectionId, bytes: []const u8) !void {
        const self: *Fixture = @ptrCast(@alignCast(context));
        // In particular final codec bytes may not extend the caller's borrowed
        // platform/token scope across an asynchronous Winsock send.
        try expect(!self.proof_borrowed);
        self.sends += 1;
        if (std.mem.startsWith(u8, bytes, "HTTP/1.1 ")) self.responses_started += 1;
        return self.platform.send(id, bytes);
    }
    fn closeFn(context: *anyopaque, id: io.ConnectionId) !void {
        return (@as(*Fixture, @ptrCast(@alignCast(context)))).platform.close(id);
    }
    fn prove(context: ?*anyopaque, facts: io.Facts, consume_context: *anyopaque, consume: connection.Proof.Consume) !void {
        const self: *Fixture = @ptrCast(@alignCast(context.?));
        self.proof_calls += 1;
        try expect(std.meta.eql(facts.remote, try socket.endpoint(self.c, false)));
        try expect(std.meta.eql(facts.local, try socket.endpoint(self.c, true)));
        // Fresh queries on BOTH ENDS of the actual connected socket; no PID or
        // Caller is fabricated. This test seam deliberately supplies UNKNOWN.
        try equal(@as(?u32, w.GetCurrentProcessId()), try owner(facts.local, facts.remote));
        try equal(@as(?u32, w.GetCurrentProcessId()), try owner(facts.remote, facts.local));
        try equal(@as(?u32, w.GetCurrentProcessId()), try owner(facts.remote, facts.local));
        if (self.reject_proof) return error.UntrustedPeer;
        self.proof_borrowed = true;
        defer self.proof_borrowed = false;
        try consume(consume_context, null, null);
    }
    fn write(self: *Fixture, bytes: []const u8) !void {
        var offset: usize = 0;
        while (offset < bytes.len) {
            const sent = w.send(self.c, bytes[offset..].ptr, @intCast(bytes.len - offset), 0);
            if (sent <= 0) return error.ClientSendFailed;
            offset += @intCast(sent);
        }
    }
    fn request(self: *Fixture, method: []const u8, target: []const u8, extra: []const u8, body: []const u8) !void {
        var buffer: [8192]u8 = undefined;
        try self.write(try std.fmt.bufPrint(&buffer, "{s} {s} HTTP/1.1\r\nHost: 127.0.0.1:{d}\r\n{s}\r\n{s}", .{ method, target, self.platform.port, extra, body }));
    }
    fn step(self: *Fixture) !io.Completion {
        try self.dispatchApplication();
        const completion = (try self.platform.poll(3000)) orelse return error.CompletionTimedOut;
        try self.state.onCompletion(completion, 0);
        return completion;
    }
    fn responseReady(self: *Fixture) !void {
        var step_count: usize = 0;
        const required = self.responses_read + 1;
        if (self.state.phase == .closed or (self.responses_started >= required and self.state.phase == .reading and self.state.receive_pending)) return;
        while (step_count < 8192) : (step_count += 1) {
            _ = try self.step();
            if (self.state.phase == .closed or (self.responses_started >= required and self.state.phase == .reading and self.state.receive_pending)) return;
        }
        return error.TestStepLimit;
    }
    fn read(self: *Fixture, bytes: []u8) !void {
        var offset: usize = 0;
        while (offset < bytes.len) {
            const received = w.recv(self.c, bytes[offset..].ptr, @intCast(bytes.len - offset), 0);
            if (received <= 0) return error.ClientReceiveFailed;
            offset += @intCast(received);
        }
    }
    fn readResponse(self: *Fixture, wire: *Wire, is_head: bool) !void {
        wire.* = .{};
        while (wire.used < wire.bytes.len) {
            try self.read(wire.bytes[wire.used..][0..1]);
            wire.used += 1;
            if (std.mem.endsWith(u8, wire.bytes[0..wire.used], "\r\n\r\n")) break;
        } else return error.TestHeaderLimit;
        wire.body_start = wire.used;
        wire.status = try std.fmt.parseInt(u16, wire.bytes[9..12], 10);
        var length: usize = 0;
        if (!is_head) if (std.mem.indexOf(u8, wire.headers(), "Content-Length: ")) |start| {
            const value = wire.headers()[start + "Content-Length: ".len ..];
            const end = std.mem.indexOf(u8, value, "\r\n") orelse return error.BadTestResponse;
            length = try std.fmt.parseInt(usize, value[0..end], 10);
        };
        if (length > wire.bytes.len - wire.used) return error.TestBodyLimit;
        try self.read(wire.bytes[wire.used..][0..length]);
        wire.used += length;
        self.responses_read += 1;
    }
    fn readRejectedFraming(self: *Fixture) !void {
        // Strict framing rejection terminates the connection, not an admitted
        // request. Closing with unread attack bytes can reset TCP, so a complete
        // HTTP error response is not promised (H0/H-parse strict corpus).
        // A timeout or any unrelated socket error still fails this assertion.
        var bytes: [8192]u8 = undefined;
        var used: usize = 0;
        while (used < bytes.len) {
            const received = w.recv(self.c, bytes[used..].ptr, @intCast(bytes.len - used), 0);
            if (received == 0) break;
            if (received < 0) {
                try equal(@as(c_int, 10054), w.WSAGetLastError()); // WSAECONNRESET
                break;
            }
            used += @intCast(received);
        } else return error.TestBodyLimit;
        const prefix = "HTTP/1.1 400 Bad Request\r\n";
        try eq(u8, prefix[0..@min(prefix.len, used)], bytes[0..@min(prefix.len, used)]);
        try expect(std.mem.indexOf(u8, bytes[0..used], "HTTP/1.1 200") == null);
        try expect(std.mem.indexOf(u8, bytes[0..used], "HTTP/1.1 100") == null);
        try equal(@as(usize, 0), self.proof_calls);
        try expect(self.state.released());
    }
    fn drain(self: *Fixture) !void {
        if (self.drained) return;
        try self.state.cancel();
        self.platform.beginStop();
        var steps: usize = 0;
        while (steps < 128) : (steps += 1) {
            const completion = (try self.platform.poll(3000)) orelse return error.StopTimedOut;
            if (completion.kind == .stopped) {
                try equal(@as(usize, 0), self.platform.outstanding);
                try expect(self.state.released());
                self.drained = true;
                return;
            }
            try self.state.onCompletion(completion, 0);
        }
        return error.TestStepLimit;
    }
    fn deinit(self: *Fixture) void {
        self.drain() catch {};
        if (self.c != w.invalid_socket) _ = w.closesocket(self.c);
        self.platform.deinit();
    }
};

test "native dispatch real TCP: fragmented capabilities, bounded pipeline, API404 and HEAD" {
    var fixture: Fixture = .{};
    try fixture.init(config());
    defer fixture.deinit();
    try fixture.request("GET", "/API/runtime/capabilities/?x=1", "", "");
    try fixture.request("GET", "/api/not-migrated", "", "");
    try fixture.responseReady();
    var wire: Wire = .{};
    try fixture.readResponse(&wire, false);
    try equal(@as(u16, 200), wire.status);
    try expect(std.mem.indexOf(u8, wire.body(), "\"profileId\":\"native-test\"") != null);
    try expect(std.mem.indexOf(u8, wire.headers(), "application/json; charset=utf-8") != null);
    try fixture.responseReady();
    try fixture.readResponse(&wire, false);
    try equal(@as(u16, 404), wire.status);
    try eq(u8, "{\"error\":\"api-endpoint-not-found\"}", wire.body());
    try fixture.drain();
    var head_fixture: Fixture = .{};
    try head_fixture.init(config());
    defer head_fixture.deinit();
    try head_fixture.request("HEAD", "/api/runtime/capabilities", "", "");
    try head_fixture.responseReady();
    try head_fixture.readResponse(&wire, true);
    try equal(@as(u16, 404), wire.status);
    try equal(@as(usize, 0), wire.body().len);
    try head_fixture.drain();
}

test "native dispatch real TCP: chunked JSON across UTF8/chunk/receive boundaries, typed binding" {
    var fixture: Fixture = .{};
    try fixture.init(config());
    defer fixture.deinit();
    const body = "{\"value\":\"a\xc3\xa9\",\"count\":7,\"enabled\":true}";
    var chunks: [256]u8 = undefined;
    const data = try std.fmt.bufPrint(&chunks, "{x}\r\n{s}\r\n{x}\r\n{s}\r\n0\r\n\r\n", .{ @as(usize, 12), body[0..12], body.len - 12, body[12..] });
    try fixture.request("POST", "/__test/http/anonymous-echo", "Transfer-Encoding: chunked\r\nContent-Type: application/json\r\n", data);
    try fixture.responseReady();
    var wire: Wire = .{};
    try fixture.readResponse(&wire, false);
    try equal(@as(u16, 200), wire.status);
    try expect(std.mem.indexOf(u8, wire.body(), "\"count\":7") != null);
    try expect(std.mem.indexOf(u8, wire.body(), "\"enabled\":true") != null);
    try equal(@as(usize, 2), fixture.proof_calls);
    try fixture.request("POST", "/__test/http/anonymous-echo", "Content-Type: application/json\r\nContent-Length: 16\r\n", "{\"count\":\"nope\"}");
    try fixture.responseReady();
    try fixture.readResponse(&wire, false);
    try equal(@as(u16, 400), wire.status);
    try fixture.drain();
}

test "native dispatch real TCP: Expect accepted before body, size and unknown caller reject before 100" {
    for (0..3) |scenario| {
        var fixture: Fixture = .{};
        try fixture.init(config());
        defer fixture.deinit();
        const target = if (scenario == 2) "/__test/http/echo" else "/__test/http/anonymous-echo";
        try fixture.request("POST", target, if (scenario == 1)
            "Content-Type: application/json\r\nContent-Length: 4097\r\nExpect: 100-continue\r\n"
        else
            "Content-Type: application/json\r\nContent-Length: 2\r\nExpect: 100-continue\r\n", "");
        try fixture.responseReady();
        var wire: Wire = .{};
        try fixture.readResponse(&wire, false);
        if (scenario == 0) {
            try equal(@as(u16, 100), wire.status);
            try equal(@as(usize, 1), fixture.proof_calls);
            try fixture.write("{}");
            try fixture.responseReady();
            try fixture.readResponse(&wire, false);
            try equal(@as(u16, 200), wire.status);
            try equal(@as(usize, 2), fixture.proof_calls);
        } else {
            try equal(@as(u16, if (scenario == 1) 413 else 403), wire.status);
            try equal(@as(usize, 1), fixture.proof_calls);
            try expect(std.mem.indexOf(u8, wire.headers(), "Connection: close") != null);
        }
        try fixture.drain();
    }
}

test "native dispatch real TCP: startup excludes action route, Host Origin and security headers fail closed" {
    for (0..5) |scenario| {
        var fixture: Fixture = .{};
        var settings = config();
        if (scenario == 0) settings.dispatch.startup = .{ .profile_id = "read-only", .allowed = 0 };
        try fixture.init(settings);
        defer fixture.deinit();
        switch (scenario) {
            0 => try fixture.request("POST", "/__test/http/echo", "Content-Length: 0\r\n", ""),
            1 => try fixture.write("GET /api/runtime/capabilities HTTP/1.1\r\nHost: evil.example\r\n\r\n"),
            2 => try fixture.request("GET", "/api/runtime/capabilities", "Origin: http://evil.example\r\n", ""),
            3 => try fixture.request("GET", "/api/runtime/capabilities", "Host: localhost\r\n", ""),
            else => try fixture.request("POST", "/__test/http/anonymous-echo", "Content-Length: 0\r\nTransfer-Encoding: chunked\r\n", "0\r\n\r\n"),
        }
        try fixture.responseReady();
        if (scenario >= 3) {
            try equal(connection.request.State.failed, fixture.state.parser.state);
            // Pinned llhttp reports CL-before-TE as INVALID_TRANSFER_ENCODING
            // (llhttp.c's error_62); the adapter classifies that as syntax.
            // Both remain pre-admission framing rejection, not HTTP dispatch.
            try equal(@as(?connection.request.Failure, if (scenario == 3) .duplicate_security_header else .syntax), fixture.state.parser.failure);
            try fixture.readRejectedFraming();
            try fixture.drain();
            continue;
        }
        var wire: Wire = .{};
        try fixture.readResponse(&wire, false);
        try equal(@as(u16, if (scenario == 0) 404 else 403), wire.status);
        try expect(std.mem.indexOf(u8, wire.headers(), "Connection: close") != null);
        try fixture.drain();
    }
}

test "native dispatch real TCP: EOF inside body cannot dispatch or become another request" {
    var fixture: Fixture = .{};
    try fixture.init(config());
    defer fixture.deinit();
    try fixture.request("POST", "/__test/http/anonymous-echo", "Content-Length: 20\r\nContent-Type: application/json\r\n", "{");
    // Prove the still-ESTABLISHED connection and admit the headers first. FIN
    // changes TCP state; the identity owner correctly refuses to call that an
    // ESTABLISHED proof. This case tests premature body EOF after admission.
    var steps: usize = 0;
    while (steps < 512 and (fixture.proof_calls != 1 or !fixture.state.receive_pending)) : (steps += 1) _ = try fixture.step();
    try equal(@as(usize, 1), fixture.proof_calls);
    try expect(fixture.state.receive_pending);
    try equal(@as(c_int, 0), w.shutdown(fixture.c, 1));
    try fixture.responseReady();
    var wire: Wire = .{};
    try fixture.readResponse(&wire, false);
    try equal(@as(u16, 400), wire.status);
    try equal(@as(usize, 1), fixture.proof_calls);
    try fixture.drain();
}

test "native dispatch real TCP: rejected real tuple proof sends no bytes, and cancellation drains pending IO" {
    for (0..3) |scenario| {
        var fixture: Fixture = .{};
        try fixture.init(config());
        defer fixture.deinit();
        if (scenario == 0) {
            fixture.reject_proof = true;
            try fixture.request("GET", "/api/runtime/capabilities", "", "");
            var steps: usize = 0;
            while (steps < 512) : (steps += 1) {
                _ = fixture.step() catch |err| {
                    try expect(err == error.UntrustedPeer);
                    break;
                };
            } else return error.TestStepLimit;
            try equal(@as(usize, 0), fixture.sends);
        } else if (scenario == 2) {
            try fixture.request("GET", "/api/runtime/capabilities", "", "");
            var steps: usize = 0;
            while (steps < 512 and fixture.state.sending == null) : (steps += 1) _ = try fixture.step();
            try expect(fixture.state.sending != null and fixture.state.sending.?.pending());
        }
        try fixture.state.cancel();
        try expect(!fixture.state.released());
        try fixture.drain();
        try expect(fixture.state.released());
    }
}

// A test source owner. Call holds only this operation's stable control record;
// Request/caller/SID/header/body borrows end at begin, before any async wait.
const PendingOwner = struct {
    call: ?*host.Call = null,
    begins: usize = 0,
    cancels: usize = 0,
    releases: usize = 0,
    ready_steps: usize = 0,
    ready: bool = false,
    stopped: bool = false,
    fail_ack: bool = false,
    latest: []const u8 = "first",
    fn family(self: *PendingOwner) host.Family {
        return .{ .context = self, .capacity = .{ .state_bytes = 8, .output_bytes = 64 }, .begin = begin, .step = step, .release = release };
    }
    fn begin(context: ?*anyopaque, input: host.Request, call: *host.Call, _: []u8) !host.Progress {
        const self: *PendingOwner = @ptrCast(@alignCast(context.?));
        try eq(u8, "/__test/http/pending", input.route_template);
        self.begins += 1;
        try equal(@as(usize, 1), self.begins);
        self.call = call;
        return .pending;
    }
    fn step(context: ?*anyopaque, call: *host.Call, event: host.Event, output: []u8) !host.Progress {
        const self: *PendingOwner = @ptrCast(@alignCast(context.?));
        try expect(self.call == call);
        if (event == .cancel) {
            self.cancels += 1;
            try expect(call.isCancelled());
            return if (self.stopped) .done else .pending;
        }
        if (call.isCancelled()) {
            if (self.stopped and self.fail_ack) return error.OwnerCompletionFailed;
            return if (self.stopped) .done else .pending;
        }
        try equal(host.Event.ready, event);
        self.ready_steps += 1;
        if (!self.ready) return .pending;
        @memcpy(output[0..self.latest.len], self.latest);
        return .{ .output = .{ .status = 202, .body = output[0..self.latest.len], .content_type = "text/plain; charset=utf-8", .headers = &.{
            .{ .name = "Location", .value = "/api/operations/test-only" },
            .{ .name = "Cache-Control", .value = "no-store" },
        } } };
    }
    fn release(context: ?*anyopaque, call: *host.Call) void {
        const self: *PendingOwner = @ptrCast(@alignCast(context.?));
        std.debug.assert(self.call == call);
        self.releases += 1;
        self.call = null;
    }
    fn publish(self: *PendingOwner, value: []const u8) void {
        self.latest = value;
        self.ready = true;
        self.call.?.wake.notify();
    }
    fn finishCancel(self: *PendingOwner) void {
        self.stopped = true;
        self.call.?.wake.notify();
    }
};
fn pendingConfig(owner_state: *PendingOwner, routes: *[1]host.Route) connection.Config {
    routes.* = .{.{ .method = "GET", .template = "/__test/http/pending", .requirement = .anonymous, .handler = .{ .family = owner_state.family() } }};
    var settings = config();
    settings.dispatch.family_routes = routes;
    return settings;
}
fn waitPending(fixture: *Fixture) !void {
    var steps: usize = 0;
    while (steps < 1024 and fixture.state.phase != .waiting) : (steps += 1) _ = try fixture.step();
    try equal(connection.Phase.waiting, fixture.state.phase);
}

test "native family actual socket: pending stays open, source handoff wakes once, 202 Location cache and content type" {
    var source: PendingOwner = .{};
    var routes: [1]host.Route = undefined;
    var fixture: Fixture = .{};
    try fixture.init(pendingConfig(&source, &routes));
    defer fixture.deinit();
    try fixture.request("GET", "/__test/http/pending", "", "");
    try waitPending(&fixture);
    try equal(@as(usize, 0), fixture.sends);
    try equal(@as(usize, 0), source.releases);
    try expect(fixture.state.receive_pending); // live disconnect observation
    // A real client sees neither response bytes nor premature EOF while pending.
    const short_ms: u32 = 20;
    try equal(@as(c_int, 0), w.setsockopt(fixture.c, w.sol_socket, 0x1006, &short_ms, @sizeOf(u32)));
    var byte: [1]u8 = undefined;
    try equal(@as(c_int, -1), w.recv(fixture.c, &byte, 1, 0));
    try equal(@as(c_int, 10060), w.WSAGetLastError());
    const normal_ms: u32 = 3000;
    try equal(@as(c_int, 0), w.setsockopt(fixture.c, w.sol_socket, 0x1006, &normal_ms, @sizeOf(u32)));
    // Retain a complete next request while the source is pending. The watcher
    // remains in flight into the tail when that request is later parsed.
    var pipeline: [256]u8 = undefined;
    const next_request = try std.fmt.bufPrint(&pipeline, "GET /api/runtime/capabilities HTTP/1.1\r\nHost: 127.0.0.1:{d}\r\n\r\n", .{fixture.platform.port});
    try fixture.write(next_request);
    var receive_steps: usize = 0;
    while (receive_steps < 1024 and fixture.state.input_end < next_request.len) : (receive_steps += 1) _ = try fixture.step();
    try equal(next_request.len, fixture.state.input_end);
    try equal(@as(usize, 2), fixture.proof_calls);
    source.publish("source-version-two");
    try expect(fixture.queued_wakes != 0 and fixture.sends == 0); // no inline reentry
    try fixture.responseReady();
    var wire: Wire = .{};
    try fixture.readResponse(&wire, false);
    try equal(@as(u16, 202), wire.status);
    try eq(u8, "source-version-two", wire.body());
    try expect(std.mem.indexOf(u8, wire.headers(), "Location: /api/operations/test-only\r\n") != null);
    try expect(std.mem.indexOf(u8, wire.headers(), "Cache-Control: no-store\r\n") != null);
    try expect(std.mem.indexOf(u8, wire.headers(), "Content-Type: text/plain; charset=utf-8\r\n") != null);
    try equal(@as(usize, 1), source.begins);
    try equal(@as(usize, 1), source.releases);
    try fixture.responseReady();
    try fixture.readResponse(&wire, false);
    try equal(@as(u16, 200), wire.status);
    try equal(@as(usize, 4), fixture.proof_calls); // two requests; resumes never prove again
    try fixture.drain();
}

test "native family actual socket: disconnect cancels once, closed IO waits for owner acknowledgement and late wake is harmless" {
    var source: PendingOwner = .{};
    var routes: [1]host.Route = undefined;
    var fixture: Fixture = .{};
    try fixture.init(pendingConfig(&source, &routes));
    defer fixture.deinit();
    try fixture.request("GET", "/__test/http/pending", "", "");
    try waitPending(&fixture);
    try equal(@as(c_int, 0), w.shutdown(fixture.c, 2));
    _ = w.closesocket(fixture.c);
    fixture.c = w.invalid_socket;
    var steps: usize = 0;
    while (steps < 64 and !fixture.state.network_closed) : (steps += 1) _ = try fixture.step();
    try expect(fixture.state.network_closed);
    try equal(@as(usize, 1), source.cancels);
    try equal(@as(usize, 0), source.releases);
    try expect(!fixture.state.released());
    source.finishCancel();
    try fixture.dispatchApplication();
    try expect(fixture.state.released());
    try equal(@as(usize, 1), source.releases);
    fixture.enqueueWake(fixture.state.id, fixture.state.request_ticket); // delayed dispatcher item, never a retired Call pointer
    try fixture.dispatchApplication();
    try equal(@as(usize, 1), source.releases);
    try fixture.drain();
}
test "native family terminal owner error after closed IO still releases without another completion" {
    var source: PendingOwner = .{ .fail_ack = true };
    var routes: [1]host.Route = undefined;
    var fixture: Fixture = .{};
    try fixture.init(pendingConfig(&source, &routes));
    defer fixture.deinit();
    try fixture.request("GET", "/__test/http/pending", "", "");
    try waitPending(&fixture);
    try equal(@as(c_int, 0), w.shutdown(fixture.c, 2));
    _ = w.closesocket(fixture.c);
    fixture.c = w.invalid_socket;
    var steps: usize = 0;
    while (steps < 64 and !fixture.state.network_closed) : (steps += 1) _ = try fixture.step();
    try expect(fixture.state.network_closed);
    try equal(@as(usize, 0), source.releases);
    source.finishCancel(); // owner ends but reports its terminal failure
    try std.testing.expectError(error.OwnerCompletionFailed, fixture.dispatchApplication());
    try expect(fixture.state.released());
    try equal(@as(usize, 1), source.releases);
}

test "native keepalive request ticket rejects an old queued application wake after the next request starts" {
    var first: PendingOwner = .{};
    var second: PendingOwner = .{};
    var routes: [1]host.Route = undefined;
    var fixture: Fixture = .{};
    try fixture.init(pendingConfig(&first, &routes));
    defer fixture.deinit();
    try fixture.request("GET", "/__test/http/pending", "", "");
    try waitPending(&fixture);
    const old_wake = first.call.?.wake; // copied queued event identity, no Call borrow
    const old_connection = fixture.state.id;
    first.publish("first-response");
    try fixture.responseReady();
    var wire: Wire = .{};
    try fixture.readResponse(&wire, false);
    try eq(u8, "first-response", wire.body());
    try equal(@as(usize, 1), first.releases);
    routes[0].handler = .{ .family = second.family() };
    try fixture.request("GET", "/__test/http/pending", "", "");
    try waitPending(&fixture);
    try expect(old_wake.ticket != fixture.state.request_ticket);
    const sends_before = fixture.sends;
    second.latest = "second-response";
    second.ready = true; // source result exists but its completion has not posted
    // Deliver a previously captured dispatcher item after the old owner has
    // released. A producer itself is forbidden to post after release.
    fixture.enqueueWake(old_connection, old_wake.ticket);
    const replacement: io.ConnectionId = .{ .slot = fixture.state.id.slot, .serial = fixture.state.id.serial + 1 };
    fixture.enqueueWake(replacement, fixture.state.request_ticket);
    try fixture.dispatchApplication();
    try equal(@as(usize, 0), second.ready_steps);
    try equal(sends_before, fixture.sends);
    try equal(connection.Phase.waiting, fixture.state.phase);
    second.publish("second-response");
    try fixture.responseReady();
    try fixture.readResponse(&wire, false);
    try equal(@as(u16, 202), wire.status);
    try eq(u8, "second-response", wire.body());
    try equal(@as(usize, 1), second.ready_steps);
    try equal(@as(usize, 1), second.releases);
    try fixture.drain();
}

test "native family capacity fails before begin and releases only after closed IO" {
    var source: PendingOwner = .{};
    var routes: [1]host.Route = undefined;
    var settings = pendingConfig(&source, &routes);
    routes[0].handler.family.capacity.state_bytes = 129;
    settings.dispatch.family_routes = &routes;
    var fixture: Fixture = .{};
    try fixture.init(settings);
    defer fixture.deinit();
    try fixture.request("GET", "/__test/http/pending", "", "");
    var steps: usize = 0;
    while (steps < 1024) : (steps += 1) {
        _ = fixture.step() catch |err| {
            try expect(err == error.OutputLimit);
            break;
        };
    } else return error.TestStepLimit;
    try equal(@as(usize, 0), source.begins);
    try equal(@as(usize, 0), source.releases);
    try fixture.drain();
}

const StreamOwner = struct {
    const total = 4 * 1024 * 1024 + 17;
    begins: usize = 0,
    steps: usize = 0,
    releases: usize = 0,
    fn family(self: *StreamOwner) host.Family {
        return .{ .context = self, .capacity = .{ .state_bytes = @sizeOf(usize), .output_bytes = 8192 }, .begin = begin, .step = step, .release = release };
    }
    fn begin(context: ?*anyopaque, _: host.Request, call: *host.Call, output: []u8) !host.Progress {
        const self: *StreamOwner = @ptrCast(@alignCast(context.?));
        self.begins += 1;
        try equal(@as(usize, 1), self.begins);
        std.mem.writeInt(usize, call.state[0..@sizeOf(usize)], 0, .little);
        return emit(call, output);
    }
    fn step(context: ?*anyopaque, call: *host.Call, event: host.Event, output: []u8) !host.Progress {
        const self: *StreamOwner = @ptrCast(@alignCast(context.?));
        if (event == .cancel) return .done;
        try equal(host.Event.writable, event);
        self.steps += 1;
        return emit(call, output);
    }
    fn emit(call: *host.Call, output: []u8) host.Progress {
        const cursor = std.mem.readInt(usize, call.state[0..@sizeOf(usize)], .little);
        const count = @min(output.len, total - cursor);
        for (output[0..count], 0..) |*byte, index| byte.* = @intCast('a' + (cursor + index) % 26);
        std.mem.writeInt(usize, call.state[0..@sizeOf(usize)], cursor + count, .little);
        return .{ .output = .{ .status = 200, .content_type = "application/octet-stream", .body = output[0..count], .more = cursor + count != total } };
    }
    fn release(context: ?*anyopaque, _: *host.Call) void {
        (@as(*StreamOwner, @ptrCast(@alignCast(context.?)))).releases += 1;
    }
};
const ChunkReader = struct {
    phase: enum { headers, size, body, body_cr, body_lf, final_cr, final_lf, done } = .headers,
    control: [1024]u8 = undefined,
    used: usize = 0,
    remaining: usize = 0,
    count: usize = 0,
    fn accept(self: *ChunkReader, bytes: []const u8) !void {
        for (bytes) |byte| switch (self.phase) {
            .headers => {
                if (self.used == self.control.len) return error.TestHeaderLimit;
                self.control[self.used] = byte;
                self.used += 1;
                if (std.mem.endsWith(u8, self.control[0..self.used], "\r\n\r\n")) {
                    try expect(std.mem.indexOf(u8, self.control[0..self.used], "Transfer-Encoding: chunked\r\n") != null);
                    self.used = 0;
                    self.phase = .size;
                }
            },
            .size => {
                if (self.used == self.control.len) return error.TestChunkLineLimit;
                self.control[self.used] = byte;
                self.used += 1;
                if (std.mem.endsWith(u8, self.control[0..self.used], "\r\n")) {
                    self.remaining = try std.fmt.parseInt(usize, self.control[0 .. self.used - 2], 16);
                    self.used = 0;
                    self.phase = if (self.remaining == 0) .final_cr else .body;
                }
            },
            .body => {
                try equal(@as(u8, @intCast('a' + self.count % 26)), byte);
                self.count += 1;
                self.remaining -= 1;
                if (self.remaining == 0) self.phase = .body_cr;
            },
            .body_cr => {
                try equal(@as(u8, '\r'), byte);
                self.phase = .body_lf;
            },
            .body_lf => {
                try equal(@as(u8, '\n'), byte);
                self.phase = .size;
            },
            .final_cr => {
                try equal(@as(u8, '\r'), byte);
                self.phase = .final_lf;
            },
            .final_lf => {
                try equal(@as(u8, '\n'), byte);
                self.phase = .done;
            },
            .done => return error.BytesAfterTerminalChunk,
        };
    }
};
extern "ws2_32" fn ioctlsocket(w.Socket, c_int, *u32) callconv(.winapi) c_int;
// RuntimeRead: Sampled
fn testMilliseconds() u64 {
    return w.GetTickCount64();
}

test "native family real slow reader: more than 4MiB resumes exactly once per drained window without full result allocation" {
    var source: StreamOwner = .{};
    var routes = [_]host.Route{.{ .method = "GET", .template = "/__test/http/large", .requirement = .anonymous, .handler = .{ .family = source.family() } }};
    var settings = config();
    settings.dispatch.family_routes = &routes;
    var fixture: Fixture = .{};
    try fixture.init(settings);
    defer fixture.deinit();
    fixture.platform.config.max_io_bytes = 16 * 1024;
    try fixture.request("GET", "/__test/http/large", "", "");
    var nonblocking: u32 = 1;
    try equal(@as(c_int, 0), ioctlsocket(fixture.c, @bitCast(@as(u32, 0x8004667e)), &nonblocking));
    var reader: ChunkReader = .{};
    var buffer: [257]u8 = undefined; // slow client reads much less than a window
    const deadline = testMilliseconds() + 30_000;
    var iterations: usize = 0;
    while (iterations < 1_000_000 and testMilliseconds() < deadline) : (iterations += 1) {
        try fixture.dispatchApplication();
        if (try fixture.platform.poll(0)) |completion| try fixture.state.onCompletion(completion, 0);
        const received = w.recv(fixture.c, &buffer, buffer.len, 0);
        if (received > 0) try reader.accept(buffer[0..@intCast(received)]) else if (received == 0) return error.PrematureStreamEof else if (w.WSAGetLastError() != 10035) return error.ClientReceiveFailed;
        if (reader.phase == .done and fixture.state.phase == .reading) break;
    } else return error.TestStepLimit;
    try equal(@as(usize, StreamOwner.total), reader.count);
    try equal(@as(usize, 1), source.begins);
    try equal(@as(usize, 1), source.releases);
    try equal(@as(usize, (StreamOwner.total + fixture.output.len - 1) / fixture.output.len - 1), source.steps);
    try equal(@as(usize, 2), fixture.proof_calls);
    try fixture.drain();
}
