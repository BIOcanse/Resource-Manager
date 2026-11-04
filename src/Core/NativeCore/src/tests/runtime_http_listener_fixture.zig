//! TEST-ONLY in-process native listener. Imported only by the verification DLL
//! overlay, never by the product ABI. Real Windows caller proof stays managed
//! until its platform owner migrates; no HttpContext is constructed here.
const std = @import("std");
const connection = @import("../interaction/http/connection.zig");
const socket = @import("../platform/windows/http_socket.zig");
const io = connection.io;
const access = connection.host.access;
const host = connection.host;
const runtime = @import("../interaction/http/runtime.zig");
pub const BorrowFamily = *const fn (?*anyopaque, ?*anyopaque, *const fn (?*anyopaque, *const host.Route) callconv(.c) i32) callconv(.c) i32;

pub const Facts = extern struct {
    local_family: u32,
    remote_family: u32,
    local_address: [16]u8,
    remote_address: [16]u8,
    local_port: u32,
    remote_port: u32,
};
pub const Caller = extern struct {
    pid: i32,
    session: i32,
    created: i64,
    admin: u32,
    reserved: u32,
    sid: ?[*]const u16,
    sid_length: usize,
    scope: ?*const anyopaque,
};
pub const Consume = *const fn (*anyopaque, ?*const Caller) callconv(.c) i32;
pub const Borrow = *const fn (?*anyopaque, *const Facts, *anyopaque, Consume) callconv(.c) i32;
pub const Ready = *const fn (?*anyopaque, u16) callconv(.c) void;

// Explicit test parameters, not product defaults or hidden service policy.
const request_deadline_ms = 30_000;
const poll_ms = 10;
const drain_polls = 3000;
const max_completions = 1_000_000;

const Run = struct {
    context: ?*anyopaque,
    borrow: Borrow,
    borrow_family: BorrowFamily,
    route: [1]host.Route = undefined,
    pending_wake: std.atomic.Value(u64) = .init(0),
    family_state: [@sizeOf(runtime.State)]u8 align(@alignOf(runtime.State)) = undefined,
    slots: [1]socket.Slot = undefined,
    platform: socket.Host = undefined,
    state: connection.Connection = undefined,
    state_started: bool = false,
    receive: [1024]u8 = undefined,
    metadata: [4096]u8 = undefined,
    headers: [16]connection.host.Header = undefined,
    body: [4096]u8 = undefined,
    output: [8192]u8 = undefined,
    response_headers: [1024]u8 = undefined,

    // RuntimeRead: Sampled
    fn serve(self: *Run, actions: u32, allowed: u32) !void {
        const deadline = socket.win.GetTickCount64() + request_deadline_ms;
        var completions: usize = 0;
        while (completions < max_completions) : (completions += 1) {
            const now = socket.win.GetTickCount64();
            if (now >= deadline) return error.TestListenerTimedOut;
            const ticket = self.pending_wake.swap(0, .acq_rel);
            if (ticket != 0 and self.state_started) try self.state.onApplicationReady(self.state.id, ticket, now * std.time.ns_per_ms);
            if (self.state_started and self.state.released()) return;
            const completion = (try self.platform.poll(@intCast(@min(poll_ms, deadline - now)))) orelse continue;
            if (completion.kind == .accepted) {
                if (self.state_started) return error.MultipleTestConnections;
                self.state = try connection.Connection.init(self.platform.transport(), completion.connection, try self.platform.facts(completion.connection), .{
                    .dispatch = .{ .startup = .{ .profile_id = if (allowed == 0) "normal-read-only" else "full", .allowed = allowed }, .normal_actions_allowed = actions != 0, .test_routes = false, .family_routes = &self.route },
                    .wake = .{ .context = self, .post = notify },
                    .parse = .{ .request_line_bytes = 1024, .header_bytes = 2048, .header_count = 16, .trailer_bytes = 1024, .trailer_count = 8, .chunk_line_bytes = 128, .body_bytes = self.body.len, .keep_alive_ns = null, .headers_ns = null, .body_ns = null, .body_rate = null },
                    .send = .{ .header_bytes = self.response_headers.len, .header_count = 16, .pending_body_bytes = self.output.len, .final_output_bytes = self.output.len },
                }, .{ .receive = &self.receive, .metadata = &self.metadata, .headers = &self.headers, .body = &self.body, .output = &self.output, .response_headers = &self.response_headers, .family_state = &self.family_state }, .{ .context = self, .with_caller = withCaller }, (now * std.time.ns_per_ms));
                self.state_started = true;
                try self.state.start();
            } else if (self.state_started) {
                try self.state.onCompletion(completion, now * std.time.ns_per_ms);
                if (self.state.released()) return;
            } else if (completion.kind == .closed or completion.kind == .stopped) return error.AcceptFailed;
        }
        return error.TestCompletionLimit;
    }
    // RuntimeRead: Sampled
    fn drain(self: *Run) !void {
        if (self.state_started) try self.state.cancel();
        self.platform.beginStop();
        var polls: usize = 0;
        while (polls < drain_polls) : (polls += 1) {
            const ticket = self.pending_wake.swap(0, .acq_rel);
            if (ticket != 0 and self.state_started) try self.state.onApplicationReady(self.state.id, ticket, socket.win.GetTickCount64() * std.time.ns_per_ms);
            const completion = (try self.platform.poll(poll_ms)) orelse continue;
            if (completion.kind == .stopped) {
                if (self.platform.outstanding != 0 or (self.state_started and !self.state.released())) return error.TestDrainIncomplete;
                return;
            }
            if (self.state_started) try self.state.onCompletion(completion, socket.win.GetTickCount64() * std.time.ns_per_ms);
        }
        return error.TestDrainTimedOut;
    }
    fn withCaller(context: ?*anyopaque, facts: io.Facts, consume_context: *anyopaque, consume: connection.Proof.Consume) !void {
        const self: *Run = @ptrCast(@alignCast(context.?));
        const actual: Facts = .{ .local_family = if (facts.local.family == .ipv4) 4 else 6, .remote_family = if (facts.remote.family == .ipv4) 4 else 6, .local_address = facts.local.address, .remote_address = facts.remote.address, .local_port = facts.local.port, .remote_port = facts.remote.port };
        var scope: Scope = .{ .context = consume_context, .consume = consume };
        if (self.borrow(self.context, &actual, &scope, consumeCaller) != 0) return error.CallerProofFailed;
        if (scope.failure) |failure| return failure;
        if (scope.calls != 1) return error.InvalidCallerBorrow;
    }
};
const Scope = struct {
    context: *anyopaque,
    consume: connection.Proof.Consume,
    calls: usize = 0,
    failure: ?anyerror = null,
};
fn consumeCaller(context: *anyopaque, caller: ?*const Caller) callconv(.c) i32 {
    const scope: *Scope = @ptrCast(@alignCast(context));
    scope.calls += 1;
    if (scope.calls != 1) return 1;
    var value: ?access.Caller = null;
    var live_scope: ?*const anyopaque = null;
    if (caller) |actual| {
        if (actual.reserved != 0 or actual.admin > 1 or (actual.sid == null and actual.sid_length != 0)) return 1;
        value = .{ .process_id = actual.pid, .process_created_utc_ticks = actual.created, .windows_session_id = actual.session, .is_administrator = actual.admin != 0, .windows_sid = if (actual.sid) |sid| sid[0..actual.sid_length] else &.{} };
        live_scope = actual.scope;
    }
    scope.consume(scope.context, value, live_scope) catch |failure| {
        scope.failure = failure;
        return 1;
    };
    return 0;
}

pub export fn rm_http_runtime_test_run(context: ?*anyopaque, ready: Ready, borrow: Borrow, borrow_family: BorrowFamily, actions: u32, allowed: u32) callconv(.c) i32 {
    var run: Run = .{ .context = context, .borrow = borrow, .borrow_family = borrow_family };
    if (borrow_family(context, &run, describe) != 0) {
        ready(context, 0);
        return 1;
    }
    var binding = run.route[0].handler.family;
    binding.context = &run;
    binding.begin = beginFamily;
    binding.step = stepFamily;
    binding.release = releaseFamily;
    run.route[0].handler = .{ .family = binding };
    run.platform.init(&run.slots, .{ .family = .ipv4, .port = 0, .backlog = 1, .max_io_bytes = 4096, .socket_receive_bytes = 1024, .socket_send_bytes = 1024, .thread_stack_bytes = 256 * 1024 }) catch {
        ready(context, 0);
        return 1;
    };
    // deinit is the final ownership-preserving safety drain even if the bounded
    // diagnostic drain fails; live OVERLAPPEDs cannot escape this stack scope.
    defer run.platform.deinit();
    ready(context, run.platform.port);
    var failed = false;
    run.serve(actions, allowed) catch {
        failed = true;
    };
    run.drain() catch {
        failed = true;
    };
    return if (failed) 1 else 0;
}

fn notify(context: ?*anyopaque, ticket: u64) void {
    const self: *Run = @ptrCast(@alignCast(context.?));
    self.pending_wake.store(ticket, .release);
}
fn describe(context: ?*anyopaque, route: *const host.Route) callconv(.c) i32 {
    const self: *Run = @ptrCast(@alignCast(context.?));
    self.route[0] = route.*;
    return 0;
}
const FamilyBorrow = struct {
    request: ?host.Request = null,
    call: *host.Call,
    output: []u8,
    event: host.Event = .ready,
    releasing: bool = false,
    progress: host.Progress = .done,
    failure: ?anyerror = null,
    fn consume(context: ?*anyopaque, route: *const host.Route) callconv(.c) i32 {
        const self: *FamilyBorrow = @ptrCast(@alignCast(context.?));
        const family = route.handler.family;
        if (self.releasing) {
            family.release(family.context, self.call);
            return 0;
        }
        self.progress = if (self.request) |request| family.begin(family.context, request, self.call, self.output) catch |err| {
            self.failure = err;
            return 1;
        } else family.step(family.context, self.call, self.event, self.output) catch |err| {
            self.failure = err;
            return 1;
        };
        return 0;
    }
};
fn execute(context: ?*anyopaque, borrow: *FamilyBorrow) !host.Progress {
    const self: *Run = @ptrCast(@alignCast(context.?));
    const result = self.borrow_family(self.context, borrow, FamilyBorrow.consume);
    if (borrow.failure) |err| return err;
    if (result != 0) return error.RuntimeFixtureBorrow;
    return borrow.progress;
}
fn beginFamily(context: ?*anyopaque, request: host.Request, call: *host.Call, output: []u8) !host.Progress {
    var borrow = FamilyBorrow{ .request = request, .call = call, .output = output };
    return execute(context, &borrow);
}
fn stepFamily(context: ?*anyopaque, call: *host.Call, event: host.Event, output: []u8) !host.Progress {
    var borrow = FamilyBorrow{ .event = event, .call = call, .output = output };
    return execute(context, &borrow);
}
fn releaseFamily(context: ?*anyopaque, call: *host.Call) void {
    var borrow = FamilyBorrow{ .releasing = true, .call = call, .output = &.{} };
    _ = execute(context, &borrow) catch {};
}
