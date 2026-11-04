//! Shared framing and send ownership; adapters supply nominal IO types.
const std = @import("std");
pub fn Implementation(comptime IoContract: type, comptime with_final_output: bool) type {
    return struct {
        pub const io = IoContract;
        pub const Limits = if (with_final_output) struct {
            header_bytes: usize,
            header_count: usize,
            pending_body_bytes: usize,
            final_output_bytes: usize,

            /// Visible bootstrap inputs, to be prepared/overridden ONCE by H-common's
            /// host settings/FreedomPoints. Not implicit per-response fallback values.
            /// Header defaults match the existing Kestrel baseline. Output budgets are
            /// bounded host choices, NOT a claim that every family's legal result fits.
            pub const defaults: Limits = .{
                .header_bytes = 32 * 1024,
                .header_count = 100,
                .pending_body_bytes = 4 * 1024 * 1024,
                .final_output_bytes = 4 * 1024 * 1024,
            };
            pub fn validate(self: Limits) !void {
                if (self.header_bytes == 0 or self.header_count == 0 or
                    self.pending_body_bytes == 0 or self.final_output_bytes == 0)
                    return error.InvalidLimits;
            }
        } else struct {
            header_bytes: usize,
            header_count: usize,
            pending_body_bytes: usize,

            pub fn validate(self: Limits) !void {
                if (self.header_bytes == 0 or self.header_count == 0 or
                    self.pending_body_bytes == 0)
                    return error.InvalidLimits;
            }
        };
        pub const Header = struct { name: []const u8, value: []const u8 };
        pub const Body = union(enum) { fixed: u64, chunked };
        pub const Head = struct {
            status: u16,
            reason: []const u8,
            headers: []const Header,
            content_type: ?[]const u8 = null,
            body: Body,
            is_head: bool,
            keep_alive: bool,
        };
        pub const Event = enum { headers_sent, writable, ended, cancelled, failed };
        const Phase = enum { fresh, headers, open, prefix, body, tail, end, done };

        pub const Response = struct {
            transport: io.Transport,
            connection: io.ConnectionId,
            limits: Limits,
            header_storage: []u8,
            phase: Phase = .fresh,
            in_flight: bool = false,
            cancel_requested: bool = false,
            pending_length: usize = 0,
            body_kind: Body = .{ .fixed = 0 },
            suppress_body: bool = false,
            keep_alive: bool = true,
            written: u64 = 0,
            body_bytes: []const u8 = &.{},
            // At most sizeof(usize)*2 hex digits, plus CRLF; small run-local controls.
            chunk_prefix: [@sizeOf(usize) * 2 + 2]u8 = undefined,

            pub fn init(transport: io.Transport, connection: io.ConnectionId, header_storage: []u8, limits: Limits) !Response {
                try limits.validate();
                if (header_storage.len == 0) return error.InvalidHeaderStorage;
                return .{ .transport = transport, .connection = connection, .limits = limits, .header_storage = header_storage[0..@min(header_storage.len, limits.header_bytes)] };
            }

            pub fn pending(self: *const Response) bool {
                return self.in_flight;
            }
            pub fn ready(self: *const Response) bool {
                return self.phase == .open and !self.in_flight;
            }

            /// Flushes headers independently of body/receive: desktop full duplex may
            /// leave response body silent while request-body receive remains pending.
            /// All validation is before the first byte. CL/TE/Connection belong here.
            pub fn start(self: *Response, head: Head) !void {
                if (self.phase != .fresh) return error.AlreadyStarted;
                if (head.reason.len > self.limits.header_bytes) return error.HeaderLimit;
                if (head.status < 200 or head.status > 599 or !validValue(head.reason)) return error.InvalidStatus;
                const framing_header = head.status != 204 and (head.body == .fixed or (!head.is_head and head.status != 304));
                const generated_count: usize = (if (framing_header) @as(usize, 2) else 1) + @intFromBool(head.content_type != null);
                if (generated_count > self.limits.header_count or head.headers.len > self.limits.header_count - generated_count) return error.TooManyHeaders;
                if (head.content_type) |content_type| if (!validValue(content_type)) return error.InvalidHeader;
                for (head.headers) |header| {
                    if (header.name.len > self.limits.header_bytes or header.value.len > self.limits.header_bytes) return error.HeaderLimit;
                    if (!validName(header.name) or !validValue(header.value)) return error.InvalidHeader;
                    if (std.ascii.eqlIgnoreCase(header.name, "content-length") or
                        std.ascii.eqlIgnoreCase(header.name, "transfer-encoding") or
                        std.ascii.eqlIgnoreCase(header.name, "connection") or
                        std.ascii.eqlIgnoreCase(header.name, "trailer")) return error.ReservedHeader;
                }
                var writer: HeaderWriter = .{ .bytes = self.header_storage };
                try writer.append("HTTP/1.1 ");
                try writer.number(head.status, 10);
                try writer.append(" ");
                try writer.append(head.reason);
                try writer.append("\r\n");
                if (head.content_type) |content_type| {
                    try writer.append("Content-Type: ");
                    try writer.append(content_type);
                    try writer.append("\r\n");
                }
                for (head.headers) |header| {
                    try writer.append(header.name);
                    try writer.append(": ");
                    try writer.append(header.value);
                    try writer.append("\r\n");
                }
                // 204 never has CL/TE. 304/HEAD can describe the selected representation
                // length but send neither entity bytes nor a terminal chunk.
                if (head.status != 204) switch (head.body) {
                    .fixed => |length| {
                        try writer.append("Content-Length: ");
                        try writer.number(length, 10);
                        try writer.append("\r\n");
                    },
                    .chunked => if (!head.is_head and head.status != 304) try writer.append("Transfer-Encoding: chunked\r\n"),
                };
                try writer.append(if (head.keep_alive) "Connection: keep-alive\r\n\r\n" else "Connection: close\r\n\r\n");
                self.body_kind = head.body;
                self.keep_alive = head.keep_alive;
                self.suppress_body = head.is_head or head.status == 204 or head.status == 304;
                self.phase = .headers;
                try self.submit(self.header_storage[0..writer.used]);
            }

            /// Only FINAL bytes, source/GCHandle/pin borrows already ended. The caller
            /// holds this final region until writable/failed/cancelled. No copy or queue.
            /// While pending, the producer must stop; receive is independently allowed.
            pub fn write(self: *Response, bytes: []const u8) !void {
                if (self.in_flight) return error.WouldBlock;
                if (!self.ready()) return error.NotWritable;
                if (self.suppress_body) return error.BodyForbidden;
                if (bytes.len == 0) return error.EmptyWrite;
                if (bytes.len > self.limits.pending_body_bytes) return error.PendingLimit;
                const total = std.math.add(u64, self.written, bytes.len) catch return error.LengthOverflow;
                switch (self.body_kind) {
                    .fixed => |length| if (total > length) return error.ContentLengthExceeded,
                    .chunked => {},
                }
                // Reject overlap: a later chunk prefix/header operation must never
                // overwrite the caller's supposedly final payload.
                if (overlap(bytes, self.header_storage) or overlap(bytes, &self.chunk_prefix)) return error.OverlappingOutput;
                self.body_bytes = bytes;
                self.written = total;
                switch (self.body_kind) {
                    .fixed => {
                        self.phase = .body;
                        try self.submit(bytes);
                    },
                    .chunked => {
                        var writer: HeaderWriter = .{ .bytes = &self.chunk_prefix };
                        try writer.number(bytes.len, 16);
                        try writer.append("\r\n");
                        self.phase = .prefix;
                        try self.submit(self.chunk_prefix[0..writer.used]);
                    },
                }
            }

            /// Call only after the previous logical write completes. Fixed short output
            /// is fatal: close, never let a following response fill the missing bytes.
            pub fn finish(self: *Response) !?Event {
                if (self.in_flight) return error.WouldBlock;
                if (!self.ready()) return error.NotWritable;
                if (!self.suppress_body) switch (self.body_kind) {
                    .fixed => |length| if (self.written != length) {
                        self.fail();
                        return error.ContentLengthMismatch;
                    },
                    .chunked => {
                        self.phase = .end;
                        try self.submit("0\r\n\r\n");
                        return null;
                    },
                };
                return self.complete();
            }

            /// Close requests cancellation; a pending send still owns its whole slice.
            /// No timer, retry, synchronous slow-client wait or new cancellation owner.
            pub fn cancel(self: *Response) !?Event {
                if (self.phase == .done or self.cancel_requested) return null;
                self.cancel_requested = true;
                try self.transport.close(self.connection);
                if (self.in_flight) return null;
                self.phase = .done;
                self.body_bytes = &.{};
                return .cancelled;
            }

            /// Dispatch the SAME H-io terminal event. null means unrelated event or an
            /// internal framing segment advanced; don't resume the producer yet.
            pub fn onCompletion(self: *Response, completion: io.Completion) ?Event {
                if (completion.kind != .sent or !std.meta.eql(completion.connection, self.connection) or !self.in_flight) return null;
                self.in_flight = false;
                if (self.cancel_requested or completion.outcome == .cancelled) {
                    self.phase = .done;
                    self.body_bytes = &.{};
                    return .cancelled;
                }
                if (completion.outcome != .ok or completion.transferred != self.pending_length) {
                    self.fail();
                    return .failed;
                }
                switch (self.phase) {
                    .headers => {
                        self.phase = .open;
                        return .headers_sent;
                    },
                    .prefix => {
                        self.phase = .body;
                        self.submit(self.body_bytes) catch return .failed;
                        return null;
                    },
                    .body => if (self.body_kind == .chunked) {
                        self.phase = .tail;
                        self.submit("\r\n") catch return .failed;
                        return null;
                    } else {
                        self.body_bytes = &.{};
                        self.phase = .open;
                        return .writable;
                    },
                    .tail => {
                        self.body_bytes = &.{};
                        self.phase = .open;
                        return .writable;
                    },
                    .end => return self.complete(),
                    else => unreachable,
                }
            }

            fn submit(self: *Response, bytes: []const u8) !void {
                self.transport.send(self.connection, bytes) catch |err| {
                    // Immediate submission failure owes no terminal completion.
                    self.fail();
                    return err;
                };
                self.in_flight = true;
                self.pending_length = bytes.len;
            }
            fn fail(self: *Response) void {
                std.debug.assert(!self.in_flight);
                self.phase = .done;
                self.body_bytes = &.{};
                self.transport.close(self.connection) catch {};
            }
            fn complete(self: *Response) Event {
                self.phase = .done;
                if (!self.keep_alive) self.transport.close(self.connection) catch {};
                return .ended;
            }
        };

        /// Synchronous codec seam: local final region, NOT another domain current.
        /// Use encodeOnce inside the SOURCE BORROW scope, end that scope, then start/
        /// write with bytes(). Hold the surrounding FINAL REGION lease until sending
        /// finishes. No codec pointer or source reference is stored. Never encode again
        /// after WouldBlock or overflow, especially for results of side-effecting calls.
        ///
        /// A family NEEDS a resumable encoder iff its maximum legal encoded response
        /// (or one indivisible stream frame) cannot fit final_output_bytes. Its codec
        /// owner must supply a byte-equivalent resumable traversal with explicit source
        /// read lifetime; that family's H1/H2 waits. Splitting transport writes alone
        /// cannot make the synchronous source traversal resumable.
        pub const FinalOutput = struct {
            region: []u8,
            used: usize = 0,
            attempted: bool = false,
            committed: bool = false,
            sealed: bool = false,

            pub fn init(region: []u8, limits: Limits) !FinalOutput {
                try limits.validate();
                if (region.len < limits.final_output_bytes) return error.FinalRegionTooSmall;
                return .{ .region = region[0..limits.final_output_bytes] };
            }
            pub fn encodeOnce(self: *FinalOutput, context: anytype, comptime encode: anytype) !void {
                if (self.attempted) return error.AlreadyEncoded;
                self.attempted = true;
                // The adapter calls an existing encode/encodeStream exactly once.
                // reserve/commit below adapt synchronous streaming callbacks if needed.
                const used = try encode(context, self);
                if (used > self.region.len) return error.FinalOutputTooLarge;
                if (self.committed and used != self.used) return error.InvalidCommit;
                self.used = used;
                self.committed = true;
                self.sealed = true;
            }
            /// Previous segment is complete at reserve; next segment stays in the SAME
            /// final region. No pointers escape encodeOnce; zero capacity is overflow.
            pub fn reserve(self: *FinalOutput, previous_used: usize) ![]u8 {
                if (!self.attempted or self.committed) return error.InvalidCommit;
                const next = std.math.add(usize, self.used, previous_used) catch return error.FinalOutputTooLarge;
                if (next >= self.region.len) return error.FinalOutputTooLarge;
                self.used = next;
                return self.region[next..];
            }
            pub fn commit(self: *FinalOutput, final_used: usize) !void {
                if (!self.attempted or self.committed) return error.InvalidCommit;
                const next = std.math.add(usize, self.used, final_used) catch return error.FinalOutputTooLarge;
                if (next > self.region.len) return error.FinalOutputTooLarge;
                self.used = next;
                self.committed = true;
            }
            pub fn bytes(self: *const FinalOutput) ![]const u8 {
                if (!self.sealed) return error.NotEncoded;
                return self.region[0..self.used];
            }
        };

        const HeaderWriter = struct {
            bytes: []u8,
            used: usize = 0,
            fn append(self: *HeaderWriter, value: []const u8) !void {
                if (value.len > self.bytes.len - self.used) return error.HeaderLimit;
                @memcpy(self.bytes[self.used..][0..value.len], value);
                self.used += value.len;
            }
            fn number(self: *HeaderWriter, value: u64, base: u8) !void {
                var buffer: [20]u8 = undefined; // decimal u64's protocol maximum digits
                var start = buffer.len;
                var n = value;
                while (true) {
                    start -= 1;
                    buffer[start] = "0123456789abcdef"[@intCast(n % base)];
                    n /= base;
                    if (n == 0) break;
                }
                try self.append(buffer[start..]);
            }
        };
        pub fn validName(name: []const u8) bool {
            if (name.len == 0) return false;
            for (name) |c| if (!std.ascii.isAlphanumeric(c) and std.mem.indexOfScalar(u8, "!#$%&'*+-.^_`|~", c) == null) return false;
            return true;
        }
        pub fn validValue(value: []const u8) bool {
            for (value) |c| if ((c < 32 and c != '\t') or c == 127) return false;
            return true;
        }
        fn overlap(a: []const u8, b: []const u8) bool {
            if (a.len == 0 or b.len == 0) return false;
            return @intFromPtr(a.ptr) < @intFromPtr(b.ptr) + b.len and @intFromPtr(b.ptr) < @intFromPtr(a.ptr) + a.len;
        }
    };
}
