//! Ordinary CSV. One schema traversal serves synchronous and resumable callers.
//! Cursor contains indices and tiny escape fragments, never source pointers.
//! The source owner retains ONE accepted report independently of a plan lease;
//! callbacks borrow its fields only inside step, and must not read current anew.
const std = @import("std");

pub const Code = struct {
    pub const ok: i32 = 0;
    pub const invalid: i32 = 1;
    pub const callback: i32 = 2;
    pub const overflow: i32 = 3;
    pub const pending: i32 = 4;
    pub const cancelled: i32 = 5;
};
pub const Field = enum(u32) {
    id,
    software_id,
    software_name,
    started_at,
    ended_at,
    maximum_duration,
    status,
    stop_reason,
    incomplete,
    average_fps,
    one_percent_low,
    point_one_percent_low,
    p50,
    p95,
    p99,
    maximum,
    frame_date,
    process_id,
    process_start_key,
    source,
    swap_chain,
    duration,
    resource_date,
};
pub const Count = enum(u32) { summary, frames, resources, software_metrics, system_metrics };
pub const Callbacks = extern struct {
    count: *const fn (?*anyopaque, usize, u32, *usize) callconv(.c) i32,
    field: *const fn (?*anyopaque, usize, u32, *Writer) callconv(.c) i32,
    compare: *const fn (?*anyopaque, usize, u32, usize, usize, *i32) callconv(.c) i32,
    metric: *const fn (?*anyopaque, usize, u32, usize, *Writer) callconv(.c) i32,
};
pub const Output = extern struct {
    reserve: *const fn (?*anyopaque, usize, *?[*]u8, *usize) callconv(.c) i32,
    commit: *const fn (?*anyopaque, usize) callconv(.c) i32,
};
pub const DateParts = extern struct { year: i32, month: i32, day: i32, hour: i32, minute: i32, second: i32, fraction: i32, offset_minutes: i32 };

/// Progress WITHIN one field callback. Completed primitive calls are skipped
/// in constant time; text resumes at its UTF-16 index, including mid-UTF8 or
/// between the two quotes of an escape. No prefix is encoded and discarded.
const Scalar = struct {
    operation: usize = 0,
    offset: usize = 0,
    position: usize = 0,
    text_phase: enum { inspect, open, body, close, done } = .inspect,
    quoted: bool = false,
    fragment: [4]u8 = undefined,
    fragment_length: usize = 0,
    fragment_offset: usize = 0,

    fn enter(self: *Scalar, writer: *Writer) bool {
        const operation = writer.operation;
        writer.operation += 1;
        return operation == self.operation and writer.failure == Code.ok;
    }
    fn finish(self: *Scalar) void {
        self.* = .{ .operation = self.operation + 1 };
    }
    fn put(_: *Scalar, writer: *Writer, bytes: []const u8) usize {
        const n = @min(bytes.len, writer.capacity - writer.used);
        const total = @addWithOverflow(writer.length, n);
        if (total[1] != 0) {
            writer.failure = Code.overflow;
            return 0;
        }
        @memcpy(writer.buffer.?[writer.used..][0..n], bytes[0..n]);
        writer.used += n;
        writer.length = total[0];
        if (n < bytes.len) writer.failure = Code.pending;
        return n;
    }
    fn text(self: *Scalar, writer: *Writer, value: []const u16) i32 {
        if (!self.enter(writer)) return writer.failure;
        while (writer.failure == Code.ok) {
            if (self.fragment_offset < self.fragment_length) {
                self.fragment_offset += self.put(writer, self.fragment[self.fragment_offset..self.fragment_length]);
                if (writer.failure != Code.ok) break;
                self.fragment_length = 0;
                self.fragment_offset = 0;
            }
            switch (self.text_phase) {
                .inspect => {
                    if (writer.escape_text) {
                        for (value) |unit| {
                            if (unit == ',' or unit == '"' or unit == '\r' or unit == '\n') {
                                self.quoted = true;
                                break;
                            }
                        }
                    }
                    self.text_phase = .open;
                },
                .open => {
                    self.text_phase = .body;
                    if (self.quoted) self.quote();
                },
                .body => {
                    if (self.position == value.len) {
                        self.text_phase = .close;
                        continue;
                    }
                    // The accepted view guarantees the same string on reborrow.
                    if (self.position > value.len) return Code.invalid;
                    var cp: u21 = value[self.position];
                    self.position += 1;
                    if (cp >= 0xD800 and cp <= 0xDBFF) {
                        if (self.position < value.len and value[self.position] >= 0xDC00 and value[self.position] <= 0xDFFF) {
                            cp = 0x10000 + ((cp - 0xD800) << 10) + (@as(u21, value[self.position]) - 0xDC00);
                            self.position += 1;
                        } else cp = 0xFFFD;
                    } else if (cp >= 0xDC00 and cp <= 0xDFFF) cp = 0xFFFD;
                    self.fragment_length = std.unicode.utf8Encode(cp, &self.fragment) catch unreachable;
                    if (self.quoted and cp == '"') {
                        self.fragment[1] = '"';
                        self.fragment_length = 2;
                    }
                },
                .close => {
                    self.text_phase = .done;
                    if (self.quoted) self.quote();
                },
                .done => {
                    self.finish();
                    return writer.failure;
                },
            }
        }
        return writer.failure;
    }
    fn quote(self: *Scalar) void {
        self.fragment[0] = '"';
        self.fragment_length = 1;
    }
};

/// Exists on encode's stack only. Each address is invalidated by reserve/commit.
pub const Writer = struct {
    context: ?*anyopaque,
    output: ?*const Output,
    buffer: ?[*]u8 = null,
    capacity: usize = 0,
    used: usize = 0,
    length: u64 = 0,
    failure: i32 = Code.ok,
    escape_text: bool = false,
    continuation: ?*Scalar = null,
    operation: usize = 0,

    pub fn raw(self: *Writer, bytes: []const u8) void {
        if (self.failure != Code.ok) return;
        if (self.continuation) |scalar| {
            if (!scalar.enter(self)) return;
            scalar.offset += scalar.put(self, bytes[scalar.offset..]);
            if (scalar.offset == bytes.len) scalar.finish();
            return;
        }
        const sum = @addWithOverflow(self.length, bytes.len);
        if (sum[1] != 0) {
            self.failure = Code.overflow;
            return;
        }
        const sink = self.output orelse {
            self.length = sum[0];
            return;
        };
        var offset: usize = 0;
        while (offset < bytes.len) {
            if (self.used == self.capacity) {
                var pointer: ?[*]u8 = null;
                var capacity: usize = 0;
                if (sink.reserve(self.context, self.used, &pointer, &capacity) != 0 or pointer == null or capacity == 0) {
                    self.failure = Code.callback;
                    return;
                }
                self.buffer = pointer;
                self.capacity = capacity;
                self.used = 0;
            }
            const chunk = @min(bytes.len - offset, self.capacity - self.used);
            @memcpy(self.buffer.?[self.used..][0..chunk], bytes[offset..][0..chunk]);
            self.used += chunk;
            offset += chunk;
            self.length += chunk;
        }
    }
    fn byte(self: *Writer, value: u8) void {
        self.raw(&.{value});
    }
    fn unsigned(self: *Writer, value: u64) void {
        var digits: [20]u8 = undefined;
        var n = value;
        var start: usize = digits.len;
        while (true) {
            start -= 1;
            digits[start] = '0' + @as(u8, @intCast(n % 10));
            n /= 10;
            if (n == 0) break;
        }
        self.raw(digits[start..]);
    }
    fn fixed(self: *Writer, value: u32, comptime width: usize) void {
        var bytes: [width]u8 = undefined;
        var n = value;
        var i = width;
        while (i != 0) {
            i -= 1;
            bytes[i] = '0' + @as(u8, @intCast(n % 10));
            n /= 10;
        }
        self.raw(&bytes);
    }
    fn field(self: *Writer, callbacks: *const Callbacks, index: usize, selected: Field) void {
        if (self.failure == Code.ok and callbacks.field(self.context, index, @backingInt(selected), self) != 0 and self.failure == Code.ok)
            self.failure = Code.callback;
    }
    fn count(self: *Writer, callbacks: *const Callbacks, index: usize, selected: Count) usize {
        var value: usize = 0;
        if (self.failure == Code.ok and callbacks.count(self.context, index, @backingInt(selected), &value) != 0)
            self.failure = Code.callback;
        return value;
    }
    fn compare(self: *Writer, callbacks: *const Callbacks, sample: usize, scope: u32, left: usize, right: usize) i32 {
        var result: i32 = 0;
        if (self.failure == Code.ok and callbacks.compare(self.context, sample, scope, left, right, &result) != 0)
            self.failure = Code.callback;
        return result;
    }
};

/// The caller pins the original UTF-16 string only for this invocation.
/// Malformed surrogate code units follow Encoding.UTF8's replacement behavior.
pub fn text(writer: *Writer, pointer: ?[*]const u16, length: usize) i32 {
    if (pointer == null and length != 0) return Code.invalid;
    const value = if (pointer) |p| p[0..length] else &.{};
    if (writer.continuation) |scalar| return scalar.text(writer, value);
    var quoted = false;
    var i: usize = 0;
    if (writer.escape_text) {
        while (i < value.len) : (i += 1) {
            if (value[i] == ',' or value[i] == '"' or value[i] == '\r' or value[i] == '\n') {
                quoted = true;
                break;
            }
        }
    }
    if (quoted) writer.byte('"');
    i = 0;
    while (i < value.len and writer.failure == Code.ok) : (i += 1) {
        var cp: u21 = value[i];
        if (cp >= 0xD800 and cp <= 0xDBFF) {
            if (i + 1 < value.len and value[i + 1] >= 0xDC00 and value[i + 1] <= 0xDFFF) {
                cp = 0x10000 + ((cp - 0xD800) << 10) + (@as(u21, value[i + 1]) - 0xDC00);
                i += 1;
            } else cp = 0xFFFD;
        } else if (cp >= 0xDC00 and cp <= 0xDFFF) cp = 0xFFFD;
        var bytes: [4]u8 = undefined;
        const count = std.unicode.utf8Encode(cp, &bytes) catch unreachable;
        writer.raw(bytes[0..count]);
        if (quoted and cp == '"') writer.byte('"');
    }
    if (quoted) writer.byte('"');
    return writer.failure;
}

pub fn integer(writer: *Writer, value: i64, sign: ?[*]const u16, sign_length: usize) i32 {
    if (value < 0) {
        const result = text(writer, sign, sign_length);
        if (result != Code.ok) return result;
    }
    writer.unsigned(if (value < 0) @as(u64, @intCast(-(value + 1))) + 1 else @intCast(value));
    return writer.failure;
}
pub fn unsigned(writer: *Writer, value: u64) i32 {
    writer.unsigned(value);
    return writer.failure;
}

// Wire-format constants below are the existing .NET invariant R/O contracts,
// not product defaults. R uses shortest round-trip digits and G17's exponent
// threshold, upper-case E and at least two exponent digits.
pub fn number(writer: *Writer, value: f64) i32 {
    if (std.math.isNan(value)) {
        writer.raw("NaN");
        return writer.failure;
    }
    if (std.math.isInf(value)) {
        writer.raw(if (value < 0) "-Infinity" else "Infinity");
        return writer.failure;
    }
    if (value == 0) {
        writer.raw(if (std.math.signbit(value)) "-0" else "0");
        return writer.failure;
    }
    var scientific: [std.fmt.float.min_buffer_size]u8 = undefined;
    const rendered = std.fmt.float.render(&scientific, value, .{}) catch unreachable;
    const e = std.mem.indexOfScalar(u8, rendered, 'e').?;
    const exponent = std.fmt.parseInt(i32, rendered[e + 1 ..], 10) catch unreachable;
    if (exponent >= -4 and exponent < 17) {
        var decimal: [std.fmt.float.bufferSize(.decimal, f64)]u8 = undefined;
        writer.raw(std.fmt.float.render(&decimal, value, .{ .mode = .decimal }) catch unreachable);
    } else {
        writer.raw(rendered[0..e]);
        writer.raw(if (exponent < 0) "E-" else "E+");
        const magnitude: u32 = @intCast(if (exponent < 0) -exponent else exponent);
        if (magnitude < 10) writer.byte('0');
        writer.unsigned(magnitude);
    }
    return writer.failure;
}

pub fn dateTime(writer: *Writer, value: *const DateParts) i32 {
    if (value.year < 1 or value.year > 9999 or value.month < 1 or value.month > 12 or
        value.day < 1 or value.day > 31 or value.hour < 0 or value.hour > 23 or
        value.minute < 0 or value.minute > 59 or value.second < 0 or value.second > 59 or
        value.fraction < 0 or value.fraction >= 10000000 or value.offset_minutes < -840 or value.offset_minutes > 840) return Code.invalid;
    writer.fixed(@intCast(value.year), 4);
    writer.byte('-');
    writer.fixed(@intCast(value.month), 2);
    writer.byte('-');
    writer.fixed(@intCast(value.day), 2);
    writer.byte('T');
    writer.fixed(@intCast(value.hour), 2);
    writer.byte(':');
    writer.fixed(@intCast(value.minute), 2);
    writer.byte(':');
    writer.fixed(@intCast(value.second), 2);
    writer.byte('.');
    writer.fixed(@intCast(value.fraction), 7);
    writer.byte(if (value.offset_minutes < 0) '-' else '+');
    const offset: u32 = @intCast(if (value.offset_minutes < 0) -value.offset_minutes else value.offset_minutes);
    writer.fixed(offset / 60, 2);
    writer.byte(':');
    writer.fixed(offset % 60, 2);
    return writer.failure;
}

pub fn source(writer: *Writer, value: i32, sign: ?[*]const u16, sign_length: usize) i32 {
    switch (value) {
        0 => writer.raw("Dxgi"),
        1 => writer.raw("Direct3D9"),
        2 => writer.raw("GraphicsKernel"),
        else => return integer(writer, value, sign, sign_length),
    }
    return writer.failure;
}

/// Exact UTF-16 ordinal order, including non-BMP and unpaired surrogates.
pub fn compareUtf16(left: ?[*]const u16, left_length: usize, right: ?[*]const u16, right_length: usize) i32 {
    var i: usize = 0;
    while (i < @min(left_length, right_length)) : (i += 1) {
        if (left.?[i] != right.?[i]) return if (left.?[i] < right.?[i]) -1 else 1;
    }
    return if (left_length == right_length) 0 else if (left_length < right_length) -1 else 1;
}

pub fn metric(writer: *Writer, key: ?[*]const u16, length: usize, present: u32, value: f64) i32 {
    if (present > 1) return Code.invalid;
    const result = text(writer, key, length);
    if (result != Code.ok) return result;
    writer.byte(',');
    if (present == 1) _ = number(writer, value);
    return writer.failure;
}

pub const Step = struct { code: i32, written: usize, length: u64 };

/// One traversal of ONE accepted result. The owner keeps that report/query
/// alive until done/cancel/failure, not a field pin or execution-plan lease.
/// The host supplies a scoped output window (normally its B-memory region).
/// It must retain final bytes through B-http-send completion BEFORE step again.
/// No source/allocator/output/transport address is retained in this cursor.
pub const Cursor = struct {
    stage: enum { header, recording, summary_count, summary, frame_count, frame, resource_count, metric_count, select, resource, done, failed } = .header,
    row: usize = 0,
    part: usize = 0,
    count: usize = 0,
    resources: usize = 0,
    scope: u32 = 0,
    rank: usize = 0,
    previous: ?usize = null,
    selected: usize = 0,
    scalar: Scalar = .{},
    length: u64 = 0,
    failure: i32 = Code.ok,

    pub fn cancel(self: *Cursor) void {
        if (self.stage == .done or self.stage == .failed) return;
        self.stage = .failed;
        self.failure = Code.cancelled;
    }

    pub fn step(self: *Cursor, context: ?*anyopaque, callbacks: *const Callbacks, window: []u8) Step {
        if (self.stage == .failed) return .{ .code = self.failure, .written = 0, .length = self.length };
        if (self.stage == .done) return .{ .code = Code.ok, .written = 0, .length = self.length };
        if (window.len == 0) return .{ .code = Code.invalid, .written = 0, .length = self.length };
        var w: Writer = .{ .context = context, .output = null, .buffer = window.ptr, .capacity = window.len, .length = self.length, .continuation = &self.scalar };
        while (w.failure == Code.ok and self.stage != .done) {
            w.operation = 0;
            switch (self.stage) {
                .header => {
                    w.raw("kind,utc,processId,processStartKey,source,swapChain,frameTimeMs,scope,metric,value\r\n");
                    if (w.failure == Code.ok) self.stage = .recording;
                },
                .recording, .summary => {
                    const recording = self.stage == .recording;
                    const names: []const []const u8 = if (recording) &recording_names else &summary_names;
                    switch (self.part) {
                        0 => w.raw(if (recording) "recording,,,,,,,," else "summary,,,,,,,,"),
                        1 => w.raw(names[self.row]),
                        2 => w.byte(','),
                        3 => {
                            w.escape_text = recording;
                            w.field(callbacks, 0, @fromBackingInt(@as(u32, @intCast(self.row)) + (if (recording) @as(u32, 0) else @backingInt(Field.average_fps))));
                            w.escape_text = false;
                        },
                        4 => w.raw("\r\n"),
                        else => unreachable,
                    }
                    if (w.failure == Code.ok) {
                        self.part += 1;
                        if (self.part == 5) {
                            self.part = 0;
                            self.row += 1;
                            if (self.row == names.len) {
                                self.row = 0;
                                self.stage = if (recording) .summary_count else .frame_count;
                            }
                        }
                    }
                },
                .summary_count => {
                    const n = w.count(callbacks, 0, .summary);
                    if (n > 1) w.failure = Code.invalid;
                    self.stage = if (n == 1) .summary else .frame_count;
                },
                .frame_count => {
                    self.count = w.count(callbacks, 0, .frames);
                    self.stage = if (self.count != 0) .frame else .resource_count;
                },
                .frame => {
                    if (self.part == 0) w.raw("frame,") else if (self.part == 12) w.raw(",,,\r\n") else if (self.part % 2 == 0) w.byte(',') else w.field(callbacks, self.row, @fromBackingInt(@backingInt(Field.frame_date) + @as(u32, @intCast(self.part / 2))));
                    if (w.failure == Code.ok) {
                        self.part += 1;
                        if (self.part == 13) {
                            self.part = 0;
                            self.row += 1;
                            if (self.row == self.count) {
                                self.row = 0;
                                self.stage = .resource_count;
                            }
                        }
                    }
                },
                .resource_count => {
                    self.resources = w.count(callbacks, 0, .resources);
                    self.stage = if (self.resources != 0) .metric_count else .done;
                },
                .metric_count => {
                    self.count = w.count(callbacks, self.row, if (self.scope == 0) .software_metrics else .system_metrics);
                    self.rank = 0;
                    self.previous = null;
                    self.stage = .select;
                },
                .select => {
                    if (self.rank == self.count) {
                        self.scope += 1;
                        if (self.scope == 2) {
                            self.scope = 0;
                            self.row += 1;
                        }
                        self.stage = if (self.row == self.resources) .done else .metric_count;
                    } else {
                        // Preserve the original ordinal selection, once per row.
                        // Window resumption does NOT redo this selection/traversal.
                        var best: ?usize = null;
                        var candidate: usize = 0;
                        while (candidate < self.count and w.failure == Code.ok) : (candidate += 1) {
                            if (self.previous) |p| {
                                if (w.compare(callbacks, self.row, self.scope, candidate, p) <= 0) continue;
                            }
                            if (best) |b| {
                                if (w.compare(callbacks, self.row, self.scope, candidate, b) >= 0) continue;
                            }
                            best = candidate;
                        }
                        if (w.failure == Code.ok) {
                            if (best) |b| self.selected = b else w.failure = Code.invalid;
                            self.stage = .resource;
                        }
                    }
                },
                .resource => {
                    switch (self.part) {
                        0 => w.raw("resource,"),
                        1 => w.field(callbacks, self.row, .resource_date),
                        2 => w.raw(if (self.scope == 0) ",,,,,,software," else ",,,,,,system,"),
                        3 => {
                            if (callbacks.metric(context, self.row, self.scope, self.selected, &w) != 0 and w.failure == Code.ok) w.failure = Code.callback;
                        },
                        4 => w.raw("\r\n"),
                        else => unreachable,
                    }
                    if (w.failure == Code.ok) {
                        self.part += 1;
                        if (self.part == 5) {
                            self.part = 0;
                            self.previous = self.selected;
                            self.rank += 1;
                            self.stage = .select;
                        }
                    }
                },
                .done, .failed => unreachable,
            }
            if (w.failure == Code.ok) self.scalar = .{};
        }
        self.length = w.length;
        if (w.failure != Code.ok and w.failure != Code.pending) {
            self.stage = .failed;
            self.failure = w.failure;
        }
        return .{ .code = w.failure, .written = w.used, .length = self.length };
    }
};

const recording_names = [_][]const u8{ "id", "softwareId", "softwareName", "startedAt", "endedAt", "maximumDurationSeconds", "status", "stopReason", "incomplete" };
const summary_names = [_][]const u8{ "averageFps", "onePercentLowFps", "pointOnePercentLowFps", "p50FrameTimeMs", "p95FrameTimeMs", "p99FrameTimeMs", "maxFrameTimeMs" };

/// Existing foreign/synchronous consumers now use the SAME resumable traversal.
/// The callback sink remains synchronous until its family adopts the step API.
pub fn encode(context: ?*anyopaque, callbacks: ?*const Callbacks, output: ?*const Output, length: ?*u64) i32 {
    const result = length orelse return Code.invalid;
    result.* = 0;
    const input = callbacks orelse return Code.invalid;
    var cursor: Cursor = .{};
    // Measurement scratch is a small tool buffer, never whole-result storage.
    var counting: [256]u8 = undefined;
    var completed: usize = 0;
    while (true) {
        var window: []u8 = &counting;
        if (output) |sink| {
            var pointer: ?[*]u8 = null;
            var capacity: usize = 0;
            if (sink.reserve(context, completed, &pointer, &capacity) != 0 or pointer == null or capacity == 0) return Code.callback;
            window = pointer.?[0..capacity];
        }
        const step = cursor.step(context, input, window);
        result.* = step.length;
        completed = step.written;
        if (step.code == Code.pending) continue;
        if (step.code != Code.ok) return step.code;
        if (output) |sink| if (sink.commit(context, completed) != 0) return Code.callback;
        return Code.ok;
    }
}
