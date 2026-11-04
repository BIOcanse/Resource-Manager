//! Kernel gate fixture, never registered by product abi.zig. Explicit bounded
//! test loop pumps real IOCP and queued request-ticket wakes on one thread.
const std = @import("std");
const connection = @import("../interaction/http/connection.zig");
const socket = @import("../platform/windows/http_socket.zig");
const common = @import("http_common_listener_fixture.zig");
const host = connection.host;
const Run = struct {
    context: ?*anyopaque,
    borrow: common.Borrow,
    route: host.Route,
    ticket: std.atomic.Value(u64) = .init(0),
    slots: [1]socket.Slot = undefined,
    platform: socket.Host = undefined,
    state: connection.Connection = undefined,
    started: bool = false,
    receive: [1024]u8 = undefined,
    metadata: [4096]u8 = undefined,
    headers: [16]host.Header = undefined,
    body: [4096]u8 = undefined,
    output: [8192]u8 = undefined,
    response_headers: [1024]u8 = undefined,

    fn wake(context: ?*anyopaque, ticket: u64) void {
        const self: *Run = @ptrCast(@alignCast(context.?));
        self.ticket.store(ticket, .release); // enqueue only, never reenter
    }
    // RuntimeRead: Sampled
    fn pump(self: *Run, actions: u32, allowed: u32) !void {
        const deadline = socket.win.GetTickCount64() + 15_000;
        var events: usize = 0;
        while (events < 100_000) : (events += 1) {
            const now = socket.win.GetTickCount64();
            if (now >= deadline) return error.TestDeadline;
            const ticket = self.ticket.swap(0, .acq_rel);
            if (ticket != 0 and self.started) {
                try self.state.onApplicationReady(self.state.id, ticket, now * std.time.ns_per_ms);
                if (self.state.released()) return;
            }
            const completion = (try self.platform.poll(10)) orelse continue;
            if (completion.kind == .accepted) {
                if (self.started) return error.SecondTestConnection;
                self.state = try connection.Connection.init(self.platform.transport(), completion.connection, try self.platform.facts(completion.connection), .{
                    .dispatch = .{ .startup = .{ .profile_id = if (allowed == 0) "normal-read-only" else "full", .allowed = allowed }, .normal_actions_allowed = actions != 0, .family_routes = @as([*]const host.Route, @ptrCast(&self.route))[0..1] },
                    .parse = .{ .request_line_bytes = 1024, .header_bytes = 2048, .header_count = 16, .trailer_bytes = 1024, .trailer_count = 8, .chunk_line_bytes = 128, .body_bytes = self.body.len, .keep_alive_ns = null, .headers_ns = null, .body_ns = null, .body_rate = null },
                    .send = .{ .header_bytes = self.response_headers.len, .header_count = 16, .pending_body_bytes = self.output.len, .final_output_bytes = self.output.len },
                    .wake = .{ .context = self, .post = wake },
                }, .{ .receive = &self.receive, .metadata = &self.metadata, .headers = &self.headers, .body = &self.body, .output = &self.output, .response_headers = &self.response_headers }, .{ .context = self, .with_caller = prove }, now * std.time.ns_per_ms);
                self.started = true;
                try self.state.start();
            } else if (self.started) {
                try self.state.onCompletion(completion, now * std.time.ns_per_ms);
                if (self.state.released()) return;
            } else return error.AcceptFailed;
        }
        return error.TestEventLimit;
    }
    fn prove(context: ?*anyopaque, facts: connection.io.Facts, target: *anyopaque, consume: connection.Proof.Consume) !void {
        const self: *Run = @ptrCast(@alignCast(context.?));
        const actual: common.Facts = .{ .local_family = if (facts.local.family == .ipv4) 4 else 6, .remote_family = if (facts.remote.family == .ipv4) 4 else 6, .local_address = facts.local.address, .remote_address = facts.remote.address, .local_port = facts.local.port, .remote_port = facts.remote.port };
        var scope: Scope = .{ .target = target, .consume = consume };
        if (self.borrow(self.context, &actual, &scope, consumeCaller) != 0) return error.ProofFailed;
        if (scope.failure) |err| return err;
        if (scope.calls != 1) return error.InvalidBorrow;
    }
    // RuntimeRead: Sampled
    fn drain(self: *Run) !void {
        if (self.started) try self.state.cancel();
        self.platform.beginStop();
        var polls: usize = 0;
        while (polls < 1500) : (polls += 1) {
            if (self.started) {
                const ticket = self.ticket.swap(0, .acq_rel);
                if (ticket != 0) try self.state.onApplicationReady(self.state.id, ticket, socket.win.GetTickCount64() * std.time.ns_per_ms);
            }
            const completion = (try self.platform.poll(10)) orelse continue;
            if (completion.kind == .stopped) {
                if (self.started and !self.state.released()) continue;
                return;
            }
            if (self.started) try self.state.onCompletion(completion, socket.win.GetTickCount64() * std.time.ns_per_ms);
        }
        return error.TestDrainDeadline;
    }
};
const Scope = struct { target: *anyopaque, consume: connection.Proof.Consume, calls: u32 = 0, failure: ?anyerror = null };
fn consumeCaller(context: *anyopaque, actual: ?*const common.Caller) callconv(.c) i32 {
    const scope: *Scope = @ptrCast(@alignCast(context));
    scope.calls += 1;
    var caller: ?host.access.Caller = null;
    if (actual) |value| caller = .{ .process_id = value.pid, .process_created_utc_ticks = value.created, .windows_session_id = value.session, .is_administrator = value.admin != 0, .windows_sid = if (value.sid) |sid| sid[0..value.sid_length] else &.{} };
    scope.consume(scope.target, caller, if (actual) |value| value.scope else null) catch |err| {
        scope.failure = err;
        return 1;
    };
    return 0;
}
pub export fn rm_kernel_history_test_run(route: *const host.Route, context: ?*anyopaque, ready: common.Ready, borrow: common.Borrow, actions: u32, allowed: u32) callconv(.c) i32 {
    var run: Run = .{ .route = route.*, .context = context, .borrow = borrow };
    run.platform.init(&run.slots, .{ .family = .ipv4, .port = 0, .backlog = 1, .max_io_bytes = 7, .socket_receive_bytes = 1024, .socket_send_bytes = 1024, .thread_stack_bytes = 256 * 1024 }) catch {
        ready(context, 0);
        return 1;
    };
    defer run.platform.deinit();
    ready(context, run.platform.port);
    var failed = false;
    run.pump(actions, allowed) catch {
        failed = true;
    };
    run.drain() catch {
        failed = true;
    };
    return if (failed) 1 else 0;
}
