//! One HTTP/1.x connection run, connecting H-io, H-parse, H-send and H-common.
//! All regions are borrowed from the caller's LOCAL run scope (Ledger for large
//! regions). No allocator or lease is stored here. Keep this object, its regions
//! and its transport alive until the real `closed` completion, even after stop.
//! The outer dispatcher owns timers and supplies monotonic `now` on every entry.
const std = @import("std");
pub const io = @import("io.zig");
pub const request = @import("request.zig");
pub const response = @import("response.zig");
pub const host = @import("host.zig");
const access = @import("../capabilities/access.zig");

/// The platform proof owns the actual connection/process/token rechecks and the
/// caller borrow. It MUST invoke consume exactly once, synchronously, and end all
/// caller/scope borrows before returning. The connection never retains either.
pub const Proof = struct {
    pub const Consume = *const fn (*anyopaque, ?access.Caller, ?*const anyopaque) anyerror!void;
    context: ?*anyopaque = null,
    with_caller: ?*const fn (?*anyopaque, io.Facts, *anyopaque, Consume) anyerror!void = null,
};
pub const Config = struct {
    dispatch: host.Config,
    parse: request.Limits,
    send: response.Limits,
    wake: host.Wake = .{},
};
pub const Regions = struct {
    receive: []u8,
    metadata: []u8,
    headers: []host.Header,
    body: []u8,
    output: []u8,
    response_headers: []u8,
    family_state: []u8 = &.{},
};
pub const Phase = enum { reading, continuing, waiting, responding, closing, closed };
const Identity = struct { pid: i32, created: i64, session: i32, administrator: bool };

pub const Connection = struct {
    transport: io.Transport,
    id: io.ConnectionId,
    facts: io.Facts,
    config: Config,
    regions: Regions,
    proof: Proof,
    parser: request.Parser,
    phase: Phase = .reading,
    receive_pending: bool = false,
    receive_offset: usize = 0,
    network_closed: bool = false,
    peer_eof: bool = false,
    interim_pending: bool = false,
    input_start: usize = 0,
    input_end: usize = 0,
    metadata_used: usize = 0,
    method: []const u8 = &.{},
    target: []const u8 = &.{},
    header_name: []const u8 = &.{},
    header_value: []const u8 = &.{},
    header_count: usize = 0,
    body_used: usize = 0,
    headers_done: bool = false,
    overflow: ?enum { metadata, body } = null,
    admission: ?host.Admission = null,
    identity: ?Identity = null,
    // Exists only during this response run; cleared before idle/next request.
    sending: ?response.Response = null,
    result: ?host.Result = null,
    operation: ?host.Operation = null,
    operation_cancelled: bool = false,
    application_ready: bool = false,
    deferred_writable: bool = false,
    progress: ?host.Progress = null,
    response_body_written: bool = false,
    close_after_response: bool = false,
    proof_calls: usize = 0,
    proof_stage: enum { admission, dispatch } = .admission,
    now: u64 = 0,
    request_ticket: u64 = 1,

    /// init does not submit IO and does not retain the temporary parser address.
    /// start binds the parser sink to the final, immovable Connection address.
    pub fn init(transport: io.Transport, id: io.ConnectionId, facts: io.Facts, config: Config, regions: Regions, proof: Proof, now: u64) !Connection {
        try config.send.validate();
        if (regions.receive.len == 0 or regions.metadata.len == 0 or regions.headers.len == 0 or
            regions.output.len == 0 or regions.response_headers.len == 0) return error.InvalidRegions;
        var bounded = regions;
        bounded.output = regions.output[0..@min(regions.output.len, config.send.final_output_bytes)];
        if (bounded.output.len < host.errorOutputBound()) return error.InvalidRegions;
        return .{ .transport = transport, .id = id, .facts = facts, .config = config, .regions = bounded, .proof = proof, .parser = undefined, .now = now };
    }
    pub fn start(self: *Connection) !void {
        self.parser = request.Parser.init(self.config.parse, .{ .context = self, .emit = emit }, self.now);
        try self.armReceive();
    }
    pub fn released(self: *const Connection) bool {
        return self.phase == .closed and !self.receive_pending and !self.interim_pending and
            (self.sending == null or !self.sending.?.pending()) and self.operation == null;
    }
    /// Cancellation requests close. It never releases an in-flight region.
    pub fn cancel(self: *Connection) !void {
        if (self.phase == .closed) return;
        if (self.phase == .closing) {
            // A terminal owner callback may throw after marking Operation done.
            // The error must not skip final release when closed IO owes no more
            // completion/wake. Never release before both guards are satisfied.
            defer self.finishClosed() catch {};
            try self.cancelOwner();
            return;
        }
        self.phase = .closing;
        if (self.sending) |*sending| {
            _ = sending.cancel() catch |err| {
                if (err != error.StaleConnection) return err;
                try self.cancelOwner();
                return;
            };
        } else self.transport.close(self.id) catch |err| {
            // H-io already closes on RST/stop. Its queued terminal completions
            // still own the buffers even though submissions are now stale.
            if (err != error.StaleConnection) return err;
        };
        try self.cancelOwner();
    }
    /// Called only for a queued, serialized dispatcher notification. A producer
    /// must never call this directly from Wake.post or a source callback.
    pub fn onApplicationReady(self: *Connection, connection_id: io.ConnectionId, ticket: u64, now: u64) !void {
        if (!std.meta.eql(connection_id, self.id) or ticket != self.request_ticket) return;
        self.now = now;
        errdefer self.cancel() catch {};
        if (self.phase == .closing) {
            try self.cancelOwner();
            if (self.operation != null and self.operation_cancelled and !self.operation.?.done() and !self.wirePending()) {
                _ = try self.operation.?.step(.ready, self.regions.output);
            }
            try self.finishClosed();
            return;
        }
        if (self.operation == null) return;
        if (self.wirePending() or self.result != null) {
            self.application_ready = true;
            return;
        }
        const event: host.Event = if (self.deferred_writable) .writable else .ready;
        self.deferred_writable = false;
        if (event == .ready) self.application_ready = false;
        try self.applyProgress(try self.operation.?.step(event, self.regions.output));
    }
    pub fn tick(self: *Connection, now: u64) !void {
        self.now = now;
        if (self.phase != .reading) return;
        self.parser.tick(now);
        if (self.parser.state == .failed) {
            // A pending receive still borrows its region. Close and drain rather
            // than starting an error response with an outstanding read owner.
            if (self.receive_pending) try self.cancel() else try self.parseFailure();
        }
    }
    pub fn onCompletion(self: *Connection, completion: io.Completion, now: u64) !void {
        if (!std.meta.eql(completion.connection, self.id)) return;
        errdefer self.cancel() catch {};
        self.now = now;
        switch (completion.kind) {
            .received => {
                if (!self.receive_pending) return error.UnexpectedReceive;
                self.receive_pending = false;
                if (self.phase == .closing) {
                    try self.cancelOwner();
                    try self.finishClosed();
                    return;
                }
                if (completion.outcome == .eof) {
                    // EOF ends this receive direction even when parsing now
                    // produces a finite400 reply. Do not re-arm its watcher.
                    self.peer_eof = true;
                    if (self.phase == .waiting or self.phase == .responding) {
                        // FIN remains a half-close for an already final reply.
                        // A deferred owner/unfinished stream loses its holder.
                        if (self.result != null and !self.result.?.more) {
                            self.peer_eof = true;
                            self.close_after_response = true;
                            if (self.sending) |*sending| sending.keep_alive = false;
                        } else try self.cancel();
                        return;
                    }
                    _ = self.parser.finish(now);
                    if (self.parser.state == .failed) try self.parseFailure() else try self.cancel();
                    return;
                }
                if (completion.outcome != .ok) {
                    try self.cancel();
                    return;
                }
                if (completion.data.len > self.regions.receive.len - self.receive_offset or completion.data.ptr != self.regions.receive[self.receive_offset..].ptr)
                    return error.InvalidReceiveRegion;
                self.input_end = self.receive_offset + completion.data.len;
                if (self.phase == .reading) try self.pump() else try self.watchReceive();
            },
            .sent => {
                if (self.interim_pending) {
                    self.interim_pending = false;
                    if (self.phase == .closing) {
                        try self.cancelOwner();
                        try self.finishClosed();
                        return;
                    }
                    if (completion.outcome != .ok or completion.transferred != interim.len) {
                        try self.cancel();
                        return;
                    }
                    self.phase = .reading;
                    try self.pump();
                } else if (self.sending) |*sending| {
                    const event = sending.onCompletion(completion) orelse return;
                    if (self.phase == .closing) {
                        try self.cancelOwner();
                        try self.finishClosed();
                        return;
                    }
                    switch (event) {
                        .headers_sent, .writable => try self.advanceResponse(),
                        .ended => try self.responseEnded(),
                        .failed, .cancelled => try self.cancel(),
                    }
                }
            },
            .closed => {
                if (self.receive_pending or self.interim_pending or (self.sending != null and self.sending.?.pending()))
                    return error.CloseBeforeTerminalIO;
                self.network_closed = true;
                self.phase = .closing;
                self.result = null;
                try self.cancelOwner();
                try self.finishClosed();
            },
            else => {},
        }
    }
    fn armReceive(self: *Connection) !void {
        if (self.phase != .reading or self.receive_pending or self.input_start != self.input_end) return;
        self.input_start = 0;
        self.input_end = 0;
        self.receive_offset = 0;
        try self.transport.receive(self.id, self.regions.receive);
        self.receive_pending = true;
    }
    fn watchReceive(self: *Connection) !void {
        if ((self.phase != .waiting and self.phase != .responding) or self.receive_pending or self.network_closed or self.peer_eof) return;
        // Retain every pipelined byte. Once this bounded region fills, ordinary
        // receive backpressure applies; FIN behind it is seen after consumption.
        if (self.input_end == self.regions.receive.len) return;
        self.receive_offset = self.input_end;
        try self.transport.receive(self.id, self.regions.receive[self.receive_offset..]);
        self.receive_pending = true;
    }
    fn wirePending(self: *const Connection) bool {
        return self.interim_pending or (self.sending != null and self.sending.?.pending());
    }
    fn cancelOwner(self: *Connection) anyerror!void {
        if (self.operation == null or self.operation.?.done() or self.wirePending()) return;
        if (!self.operation_cancelled) {
            self.operation_cancelled = true;
            _ = try self.operation.?.cancel(self.regions.output);
        }
    }
    fn finishClosed(self: *Connection) !void {
        if (!self.network_closed or self.receive_pending or self.wirePending()) return;
        if (self.operation) |*operation| {
            if (!operation.done()) return;
            try operation.deinit();
            self.operation = null;
        }
        self.sending = null;
        self.result = null;
        self.phase = .closed;
    }
    fn pump(self: *Connection) anyerror!void {
        // Each iteration consumes input or advances one llhttp pause boundary.
        // There are only headers, completion and the externally completed send.
        while (self.phase == .reading) {
            const parsed = self.parser.feed(self.regions.receive[self.input_start..self.input_end], self.now);
            self.input_start += parsed.consumed;
            switch (parsed.state) {
                .headers => {
                    self.proof_stage = .admission;
                    try self.withCaller();
                    if (self.admission.?.rejection) |rejection| {
                        self.close_after_response = true;
                        try self.beginResponse(rejection);
                        return;
                    }
                    const maximum = if (self.config.parse.body_bytes) |limit| @min(limit, self.regions.body.len) else self.regions.body.len;
                    if (!self.parser.acceptBody(.{ .max_bytes = maximum, .deadline_ns = self.config.parse.body_ns, .rate = self.config.parse.body_rate }, self.now)) {
                        try self.parseFailure();
                        return;
                    }
                    if (self.parser.head.expect_continue) {
                        try self.transport.send(self.id, interim);
                        self.interim_pending = true;
                        self.phase = .continuing;
                        return;
                    }
                },
                .complete => {
                    if (!self.receive_pending) {
                        const remaining = self.input_end - self.input_start;
                        std.mem.copyForwards(u8, self.regions.receive[0..remaining], self.regions.receive[self.input_start..self.input_end]);
                        self.input_start = 0;
                        self.input_end = remaining;
                    }
                    var wake = self.config.wake;
                    wake.ticket = self.request_ticket;
                    self.operation = host.Operation.init(self.regions.family_state, wake);
                    self.proof_stage = .dispatch;
                    try self.withCaller();
                    try self.applyProgress(self.progress.?);
                    return;
                },
                .failed => {
                    try self.parseFailure();
                    return;
                },
                .closed => {
                    try self.cancel();
                    return;
                },
                .need_input => {
                    try self.armReceive();
                    return;
                },
                .body_paused => return error.UnexpectedBodyPause,
            }
        }
    }
    fn withCaller(self: *Connection) !void {
        self.proof_calls = 0;
        if (self.proof.with_caller) |borrow| try borrow(self.proof.context, self.facts, self, consume) else try consume(self, null, null);
        if (self.proof_calls != 1) return error.InvalidCallerBorrow;
    }
    fn consume(context: *anyopaque, caller: ?access.Caller, caller_scope: ?*const anyopaque) !void {
        const self: *Connection = @ptrCast(@alignCast(context));
        self.proof_calls += 1;
        if (self.proof_calls != 1) return error.InvalidCallerBorrow;
        const value: host.Request = .{ .method = self.method, .target = self.target, .headers = self.regions.headers[0..self.header_count], .body = self.regions.body[0..self.body_used], .facts = self.facts, .caller = caller, .caller_scope = caller_scope };
        switch (self.proof_stage) {
            .admission => {
                self.admission = try host.admit(self.config.dispatch, value, self.regions.output);
                if (self.admission.?.decision.caller_level != .unidentified) {
                    const actual = caller orelse return error.InvalidCallerBorrow;
                    self.identity = callerIdentity(actual);
                }
            },
            .dispatch => {
                if (self.identity) |previous| {
                    if (caller == null or caller_scope == null or !std.meta.eql(previous, callerIdentity(caller.?))) {
                        self.close_after_response = true;
                        self.progress = .{ .output = try host.failure(self.regions.output, 403, "local-windows-caller-required") };
                        self.operation = null;
                        return;
                    }
                }
                self.progress = try self.operation.?.start(self.config.dispatch, value, self.admission.?, self.regions.output);
            },
        }
    }
    fn callerIdentity(caller: access.Caller) Identity {
        return .{ .pid = caller.process_id, .created = caller.process_created_utc_ticks, .session = caller.windows_session_id, .administrator = caller.is_administrator };
    }
    fn parseFailure(self: *Connection) !void {
        self.close_after_response = true;
        const failure = self.parser.failure;
        const large_body = self.overflow == .body or failure == .body_limit;
        const large_head = self.overflow == .metadata or failure == .header_bytes_limit or failure == .header_count_limit;
        try self.beginResponse(try host.failure(self.regions.output, if (large_body) 413 else if (large_head) 431 else 400, if (large_body) "request-body-too-large" else if (large_head) "request-headers-too-large" else "invalid-http-request"));
    }
    fn beginResponse(self: *Connection, result: host.Result) !void {
        self.result = result;
        self.phase = .responding;
        self.response_body_written = false;
        self.sending = try response.Response.init(self.transport, self.id, self.regions.response_headers, self.config.send);
        const keep_alive = !self.close_after_response and self.parser.state == .complete and self.parser.head.keep_alive;
        try self.sending.?.start(.{ .status = result.status, .reason = reason(result.status), .headers = result.headers, .content_type = result.content_type, .body = if (result.total_length) |length| .{ .fixed = length } else if (result.more) .chunked else .{ .fixed = result.body.len }, .is_head = std.mem.eql(u8, self.method, "HEAD"), .keep_alive = keep_alive });
        try self.watchReceive();
    }
    fn applyProgress(self: *Connection, progress: host.Progress) anyerror!void {
        switch (progress) {
            .pending => {
                self.phase = .waiting;
                try self.watchReceive();
                if (self.application_ready) try self.postWritable(false);
            },
            .output => |result| {
                self.result = result;
                self.response_body_written = false;
                if (self.sending == null) try self.beginResponse(result) else {
                    self.phase = .responding;
                    if (!self.sending.?.ready()) return error.OutputWhileSendPending;
                    try self.advanceResponse();
                }
            },
            .done => {
                if (self.sending == null) return error.FamilyEndedWithoutResponse;
                if (try self.sending.?.finish()) |event| if (event == .ended) try self.responseEnded();
            },
        }
    }
    fn postWritable(self: *Connection, writable: bool) !void {
        self.deferred_writable = self.deferred_writable or writable;
        const post = self.config.wake.post orelse return error.MissingDispatcherWake;
        post(self.config.wake.context, self.request_ticket);
    }
    fn advanceResponse(self: *Connection) !void {
        const sending = &self.sending.?;
        const result = self.result.?;
        if (!self.response_body_written and !std.mem.eql(u8, self.method, "HEAD") and result.status != 204 and result.status != 304 and result.body.len != 0) {
            self.response_body_written = true;
            try sending.write(result.body);
            return;
        }
        self.result = null;
        if (self.operation) |*operation| {
            if (result.more) {
                self.phase = .waiting;
                try self.watchReceive();
                try self.postWritable(true);
            } else {
                const progress = try operation.step(.writable, self.regions.output);
                if (progress != .done or !operation.done()) return error.FinalOutputNotTerminal;
                try self.applyProgress(progress);
            }
        } else {
            if (try sending.finish()) |event| if (event == .ended) try self.responseEnded();
        }
    }
    fn responseEnded(self: *Connection) anyerror!void {
        const keep_alive = self.sending.?.keep_alive;
        self.sending = null;
        self.result = null;
        if (self.operation) |*operation| {
            if (!operation.done()) return error.ResponseBeforeOwnerTerminal;
            try operation.deinit();
            self.operation = null;
        }
        if (!keep_alive) {
            self.phase = .closing;
            return;
        }
        self.request_ticket = std.math.add(u64, self.request_ticket, 1) catch {
            try self.cancel();
            return;
        };
        if (!self.parser.nextRequest(self.now)) {
            try self.cancel();
            return;
        }
        self.metadata_used = 0;
        self.method = &.{};
        self.target = &.{};
        self.header_name = &.{};
        self.header_value = &.{};
        self.header_count = 0;
        self.body_used = 0;
        self.headers_done = false;
        self.overflow = null;
        self.admission = null;
        self.identity = null;
        self.close_after_response = false;
        self.operation_cancelled = false;
        self.application_ready = false;
        self.deferred_writable = false;
        self.progress = null;
        self.phase = .reading;
        try self.pump();
    }
    fn append(self: *Connection, value: *[]const u8, bytes: []const u8) bool {
        if (bytes.len > self.regions.metadata.len - self.metadata_used) {
            self.overflow = .metadata;
            return false;
        }
        const region_start = if (value.len == 0) self.metadata_used else @intFromPtr(value.ptr) - @intFromPtr(self.regions.metadata.ptr);
        @memcpy(self.regions.metadata[self.metadata_used..][0..bytes.len], bytes);
        self.metadata_used += bytes.len;
        value.* = self.regions.metadata[region_start..self.metadata_used];
        return true;
    }
    fn emit(context: *anyopaque, event: request.Event) request.Flow {
        const self: *Connection = @ptrCast(@alignCast(context));
        const ok = switch (event) {
            .method => |bytes| self.append(&self.method, bytes),
            .url => |bytes| self.append(&self.target, bytes),
            .header_name => |bytes| if (self.headers_done) true else self.append(&self.header_name, bytes),
            .header_value => |bytes| if (self.headers_done) true else self.append(&self.header_value, bytes),
            .header_end => |end| blk: {
                if (end.trailer) break :blk true;
                if (self.header_count == self.regions.headers.len) {
                    self.overflow = .metadata;
                    break :blk false;
                }
                self.regions.headers[self.header_count] = .{ .name = self.header_name, .value = self.header_value };
                self.header_count += 1;
                self.header_name = &.{};
                self.header_value = &.{};
                break :blk true;
            },
            .headers => blk: {
                self.headers_done = true;
                break :blk true;
            },
            .body => |bytes| blk: {
                if (bytes.len > self.regions.body.len - self.body_used) {
                    self.overflow = .body;
                    break :blk false;
                }
                @memcpy(self.regions.body[self.body_used..][0..bytes.len], bytes);
                self.body_used += bytes.len;
                break :blk true;
            },
            else => true,
        };
        return if (ok) .proceed else .abort;
    }
};
const interim = "HTTP/1.1 100 Continue\r\n\r\n";
fn reason(status: u16) []const u8 {
    return switch (status) {
        200 => "OK",
        204 => "No Content",
        400 => "Bad Request",
        403 => "Forbidden",
        404 => "Not Found",
        413 => "Content Too Large",
        415 => "Unsupported Media Type",
        431 => "Request Header Fields Too Large",
        500 => "Internal Server Error",
        503 => "Service Unavailable",
        else => "Response",
    };
}
