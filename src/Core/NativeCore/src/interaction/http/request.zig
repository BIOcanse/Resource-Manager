//! HTTP/1.x framing only. No socket, allocator, clock, route, or permission owner.
//! Event byte spans are valid only during Sink.emit. The caller retains unconsumed
//! input, and must not resume the next request until the previous response ends.
const std = @import("std");
const c = @import("llhttp");

pub const Limits = struct {
    request_line_bytes: u64,
    header_bytes: u64,
    header_count: u64,
    trailer_bytes: u64,
    trailer_count: u64,
    chunk_line_bytes: u64,
    body_bytes: ?u64,
    keep_alive_ns: ?u64,
    headers_ns: ?u64,
    body_ns: ?u64,
    body_rate: ?Rate,
};
pub const Rate = struct { bytes_per_second: u64, grace_ns: u64 };
/// Prepared by the host after route/permission checks. Explicit null permits a
/// streaming upload; it is never inferred from a URL by the parser.
pub const BodyPolicy = struct { max_bytes: ?u64, deadline_ns: ?u64, rate: ?Rate };
pub const Head = struct {
    method: u8,
    major: u8,
    minor: u8,
    content_length: ?u64,
    chunked: bool,
    expect_continue: bool,
    keep_alive: bool,
};
pub const Event = union(enum) {
    begin,
    method: []const u8,
    url: []const u8,
    version: []const u8,
    header_name: []const u8,
    header_value: []const u8,
    header_end: struct { trailer: bool },
    headers: Head,
    body: []const u8,
    complete: Head,
};
pub const Flow = enum { proceed, pause, abort };
pub const Sink = struct { context: *anyopaque, emit: *const fn (*anyopaque, Event) Flow };
pub const Failure = enum {
    syntax,
    unsupported_version,
    unsupported_upgrade,
    conflicting_framing,
    duplicate_security_header,
    unsupported_expectation,
    forbidden_trailer,
    request_line_limit,
    header_bytes_limit,
    header_count_limit,
    trailer_bytes_limit,
    trailer_count_limit,
    chunk_line_limit,
    body_limit,
    keep_alive_timeout,
    headers_timeout,
    body_timeout,
    body_rate_timeout,
    premature_eof,
    sink_aborted,
    clock_regression,
};
pub const State = enum { need_input, headers, body_paused, complete, closed, failed };
pub const Result = struct { consumed: usize, state: State, failure: ?Failure };
const Phase = enum { head, chunk_line, body, trailers };

pub const Parser = struct {
    parser: c.llhttp_t,
    limits: Limits,
    sink: Sink,
    state: State = .need_input,
    failure: ?Failure = null,
    phase: Phase = .head,
    head: Head = undefined,
    policy: BodyPolicy,
    last_now: u64,
    idle_since: u64,
    headers_since: ?u64 = null,
    body_since: ?u64 = null,
    paused_since: ?u64 = null,
    body_received: u64 = 0,
    line_bytes: u64 = 0,
    head_bytes: u64 = 0,
    trailer_bytes: u64 = 0,
    headers_count: u64 = 0,
    trailers_count: u64 = 0,
    request_line: bool = true,
    internal_pause: bool = false,
    name: ["transfer-encoding".len]u8 = undefined,
    name_len: usize = 0,
    expect: ["100-continue".len]u8 = undefined,
    expect_len: usize = 0,
    expect_invalid: bool = false,
    expect_seen: bool = false,
    host_seen: bool = false,
    origin_seen: bool = false,

    pub fn init(limits: Limits, sink: Sink, now: u64) Parser {
        var self: Parser = .{
            .parser = undefined,
            .limits = limits,
            .sink = sink,
            .last_now = now,
            .idle_since = now,
            .policy = .{ .max_bytes = limits.body_bytes, .deadline_ns = limits.body_ns, .rate = limits.body_rate },
        };
        c.llhttp_init(&self.parser, c.HTTP_REQUEST, &settings);
        return self;
    }

    /// May consume less than input.len even without a failure. An empty call
    /// drives llhttp's pending completion after a headers/body pause.
    pub fn feed(self: *Parser, input: []const u8, now: u64) Result {
        self.tick(now);
        if (self.state != .need_input) return self.result(0);
        self.parser.data = self;
        var offset: usize = 0;
        var drive_empty = input.len == 0;
        while (offset < input.len or drive_empty) {
            drive_empty = false;
            if (self.headers_since == null and self.phase == .head and offset < input.len) self.headers_since = now;
            const available = input[offset..];
            const count = if (self.phase == .body) available.len else self.controlPrefix(available);
            if (self.failure != null) return self.result(offset);
            const span = available[0..count];
            // A non-null address is needed for an empty execute after resume.
            const address: [*]const u8 = if (span.len == 0) "".ptr else span.ptr;
            const rc = c.llhttp_execute(&self.parser, address, span.len);
            var consumed = span.len;
            if (rc != c.HPE_OK) {
                const end = c.llhttp_get_error_pos(&self.parser);
                if (end != null) consumed = @min(span.len, @intFromPtr(end) - @intFromPtr(address));
            }
            offset += consumed;
            if (rc == c.HPE_PAUSED and self.internal_pause) {
                self.internal_pause = false;
                c.llhttp_resume(&self.parser);
                drive_empty = offset == input.len;
                continue;
            }
            if (rc == c.HPE_PAUSED) return self.result(offset);
            if (rc != c.HPE_OK) {
                if (self.failure == null) self.fail(if (rc == c.HPE_UNEXPECTED_CONTENT_LENGTH) .conflicting_framing else .syntax);
                return self.result(offset);
            }
            if (span.len == 0) break;
        }
        return self.result(offset);
    }

    /// Call only after the host has accepted the headers, including known length.
    /// The host sends 100 Continue only after this returns true and head says so.
    pub fn acceptBody(self: *Parser, policy: BodyPolicy, now: u64) bool {
        self.tick(now);
        if (self.state != .headers) return false;
        if (policy.max_bytes) |maximum| {
            if (self.head.content_length) |length| if (length > maximum) {
                self.fail(.body_limit);
                return false;
            };
        }
        self.policy = policy;
        self.body_since = now;
        self.phase = if (self.head.chunked) .chunk_line else .body;
        self.state = .need_input;
        c.llhttp_resume(&self.parser);
        return true;
    }

    pub fn resumeBody(self: *Parser, now: u64) bool {
        self.tick(now);
        if (self.state != .body_paused) return false;
        if (self.body_since) |start| self.body_since = start + (now - self.paused_since.?);
        self.paused_since = null;
        self.state = .need_input;
        c.llhttp_resume(&self.parser);
        return true;
    }

    /// Response completion, not just parsing completion, permits reuse.
    pub fn nextRequest(self: *Parser, now: u64) bool {
        self.tick(now);
        if (self.state != .complete) return false;
        if (!self.head.keep_alive) {
            self.state = .closed;
            return false;
        }
        const limits = self.limits;
        const sink = self.sink;
        self.* = init(limits, sink, now);
        return true;
    }

    /// The connection owner supplies EOF only after it has drained pending input
    /// and explicitly resumed any headers/body pause. FIN is not request success.
    pub fn finish(self: *Parser, now: u64) Result {
        self.tick(now);
        if (self.state == .failed or self.state == .closed) return self.result(0);
        if (self.state == .complete or (self.headers_since == null and self.phase == .head)) {
            self.state = .closed;
            return self.result(0);
        }
        if (self.state != .need_input) return self.result(0);
        self.parser.data = self;
        if (c.llhttp_finish(&self.parser) != c.HPE_OK) self.fail(.premature_eof) else self.state = .closed;
        return self.result(0);
    }

    /// No timer thread or clock read: H-io calls this on its monotonic timer entry.
    /// Host backpressure pauses body clocks; caller inactivity does not.
    pub fn tick(self: *Parser, now: u64) void {
        if (self.state == .failed or self.state == .closed) return;
        if (now < self.last_now) {
            self.fail(.clock_regression);
            return;
        }
        self.last_now = now;
        if (self.state == .complete or self.state == .body_paused) return;
        if (self.headers_since) |start| {
            if (self.phase == .head) {
                if (elapsed(start, now, self.limits.headers_ns)) self.fail(.headers_timeout);
                return;
            }
        } else {
            if (elapsed(self.idle_since, now, self.limits.keep_alive_ns)) self.fail(.keep_alive_timeout);
            return;
        }
        if (self.body_since) |start| {
            if (elapsed(start, now, self.policy.deadline_ns)) {
                self.fail(.body_timeout);
                return;
            }
            if (self.policy.rate) |rate| {
                const duration = now - start;
                if (duration > rate.grace_ns) {
                    const required = @as(u128, duration - rate.grace_ns) * rate.bytes_per_second;
                    if (@as(u128, self.body_received) * std.time.ns_per_s < required) self.fail(.body_rate_timeout);
                }
            }
        }
    }

    /// Exact raw wire accounting around llhttp framing boundaries. Only finds
    /// line ends for byte budgets; llhttp alone validates their HTTP grammar.
    fn controlPrefix(self: *Parser, bytes: []const u8) usize {
        var used: usize = 0;
        while (used < bytes.len) {
            const byte = bytes[used];
            used += 1;
            self.line_bytes +|= 1;
            switch (self.phase) {
                .head => {
                    if (self.request_line) {
                        if (self.line_bytes > self.limits.request_line_bytes) self.fail(.request_line_limit);
                    } else {
                        self.head_bytes +|= 1;
                        if (self.head_bytes > self.limits.header_bytes) self.fail(.header_bytes_limit);
                    }
                },
                .trailers => {
                    self.trailer_bytes +|= 1;
                    if (self.trailer_bytes > self.limits.trailer_bytes) self.fail(.trailer_bytes_limit);
                },
                .chunk_line => if (self.line_bytes > self.limits.chunk_line_bytes) {
                    self.fail(.chunk_line_limit);
                },
                .body => unreachable,
            }
            if (self.failure != null) return used;
            if (byte == '\n') {
                self.line_bytes = 0;
                self.request_line = false;
                return used;
            }
        }
        return used;
    }

    fn result(self: *const Parser, consumed: usize) Result {
        return .{ .consumed = consumed, .state = self.state, .failure = self.failure };
    }
    fn fail(self: *Parser, failure: Failure) void {
        if (self.failure == null) self.failure = failure;
        self.state = .failed;
    }
    fn send(self: *Parser, event: Event) c_int {
        switch (self.sink.emit(self.sink.context, event)) {
            .proceed => return 0,
            .pause => if (event == .body) {
                self.state = .body_paused;
                self.paused_since = self.last_now;
                return c.HPE_PAUSED;
            },
            .abort => {},
        }
        self.fail(.sink_aborted);
        return c.HPE_USER;
    }
    fn isName(self: *const Parser, expected: []const u8) bool {
        return self.name_len == expected.len and std.ascii.eqlIgnoreCase(self.name[0..@min(self.name_len, self.name.len)], expected);
    }
    fn from(p: [*c]c.llhttp_t) *Parser {
        return @ptrCast(@alignCast(p[0].data.?));
    }
    fn begin(p: [*c]c.llhttp_t) callconv(.c) c_int {
        return from(p).send(.begin);
    }
    fn method(p: [*c]c.llhttp_t, at: [*c]const u8, len: usize) callconv(.c) c_int {
        return from(p).send(.{ .method = at[0..len] });
    }
    fn url(p: [*c]c.llhttp_t, at: [*c]const u8, len: usize) callconv(.c) c_int {
        return from(p).send(.{ .url = at[0..len] });
    }
    fn version(p: [*c]c.llhttp_t, at: [*c]const u8, len: usize) callconv(.c) c_int {
        return from(p).send(.{ .version = at[0..len] });
    }
    fn field(p: [*c]c.llhttp_t, at: [*c]const u8, len: usize) callconv(.c) c_int {
        const self = from(p);
        const room = self.name.len - @min(self.name_len, self.name.len);
        const count = @min(room, len);
        @memcpy(self.name[@min(self.name_len, self.name.len)..][0..count], at[0..count]);
        self.name_len +|= len;
        return self.send(.{ .header_name = at[0..len] });
    }
    fn value(p: [*c]c.llhttp_t, at: [*c]const u8, len: usize) callconv(.c) c_int {
        const self = from(p);
        if (self.isName("expect")) {
            var index: usize = 0;
            while (index < len) : (index += 1) {
                const byte = at[index];
                if (self.expect_len == self.expect.len) {
                    if (byte != ' ' and byte != '\t') self.expect_invalid = true;
                } else {
                    self.expect[self.expect_len] = byte;
                    self.expect_len += 1;
                }
            }
        }
        return self.send(.{ .header_value = at[0..len] });
    }
    fn headerEnd(p: [*c]c.llhttp_t) callconv(.c) c_int {
        const self = from(p);
        const trailer = self.phase == .trailers;
        if (trailer) {
            self.trailers_count +|= 1;
            if (self.trailers_count > self.limits.trailer_count) self.fail(.trailer_count_limit);
            if (self.isName("content-length") or self.isName("transfer-encoding") or self.isName("host") or self.isName("expect") or self.isName("origin")) self.fail(.forbidden_trailer);
        } else {
            self.headers_count +|= 1;
            if (self.headers_count > self.limits.header_count) self.fail(.header_count_limit);
            if (self.isName("host")) {
                if (self.host_seen) self.fail(.duplicate_security_header);
                self.host_seen = true;
            }
            if (self.isName("origin")) {
                if (self.origin_seen) self.fail(.duplicate_security_header);
                self.origin_seen = true;
            }
            if (self.isName("expect")) {
                if (self.expect_seen or self.expect_invalid or self.expect_len != self.expect.len or !std.ascii.eqlIgnoreCase(&self.expect, "100-continue")) self.fail(.unsupported_expectation);
                self.expect_seen = true;
            }
        }
        self.name_len = 0;
        self.expect_len = 0;
        self.expect_invalid = false;
        if (self.failure != null) return c.HPE_USER;
        return self.send(.{ .header_end = .{ .trailer = trailer } });
    }
    fn headers(p: [*c]c.llhttp_t) callconv(.c) c_int {
        const self = from(p);
        const parser = &self.parser;
        if (parser.http_major != 1 or parser.http_minor > 1) self.fail(.unsupported_version);
        if (parser.upgrade != 0 or parser.method == c.HTTP_CONNECT) self.fail(.unsupported_upgrade);
        const chunked = (parser.flags & c.F_CHUNKED) != 0;
        const has_length = (parser.flags & c.F_CONTENT_LENGTH) != 0;
        if (chunked and has_length) self.fail(.conflicting_framing);
        if (self.failure != null) return c.HPE_USER;
        self.head = .{
            .method = parser.method,
            .major = parser.http_major,
            .minor = parser.http_minor,
            .content_length = if (has_length) parser.content_length else null,
            .chunked = chunked,
            .expect_continue = self.expect_seen,
            .keep_alive = c.llhttp_should_keep_alive(parser) != 0,
        };
        self.state = .headers;
        if (self.send(.{ .headers = self.head }) != 0) return c.HPE_USER;
        return c.HPE_PAUSED;
    }
    fn body(p: [*c]c.llhttp_t, at: [*c]const u8, len: usize) callconv(.c) c_int {
        const self = from(p);
        self.body_received +|= len;
        if (self.policy.max_bytes) |maximum| if (self.body_received > maximum) {
            self.fail(.body_limit);
            return c.HPE_USER;
        };
        return self.send(.{ .body = at[0..len] });
    }
    fn complete(p: [*c]c.llhttp_t) callconv(.c) c_int {
        const self = from(p);
        self.state = .complete;
        if (self.send(.{ .complete = self.head }) != 0) return c.HPE_USER;
        return c.HPE_PAUSED;
    }
    fn chunkHeader(p: [*c]c.llhttp_t) callconv(.c) c_int {
        const self = from(p);
        if (self.policy.max_bytes) |maximum| {
            if (self.parser.content_length > maximum - self.body_received) {
                self.fail(.body_limit);
                return c.HPE_USER;
            }
        }
        self.phase = if (self.parser.content_length == 0) .trailers else .body;
        self.internal_pause = true;
        return c.HPE_PAUSED;
    }
    fn chunkEnd(p: [*c]c.llhttp_t) callconv(.c) c_int {
        const self = from(p);
        if (self.phase == .trailers) return 0;
        self.phase = .chunk_line;
        self.internal_pause = true;
        return c.HPE_PAUSED;
    }
};

fn elapsed(start: u64, now: u64, limit: ?u64) bool {
    return if (limit) |duration| now - start >= duration else false;
}
const settings: c.llhttp_settings_t = blk: {
    var callbacks = std.mem.zeroes(c.llhttp_settings_t);
    callbacks.on_message_begin = Parser.begin;
    callbacks.on_method = Parser.method;
    callbacks.on_url = Parser.url;
    callbacks.on_version = Parser.version;
    callbacks.on_header_field = Parser.field;
    callbacks.on_header_value = Parser.value;
    callbacks.on_header_value_complete = Parser.headerEnd;
    callbacks.on_headers_complete = Parser.headers;
    callbacks.on_body = Parser.body;
    callbacks.on_message_complete = Parser.complete;
    callbacks.on_chunk_header = Parser.chunkHeader;
    callbacks.on_chunk_complete = Parser.chunkEnd;
    break :blk callbacks;
};
