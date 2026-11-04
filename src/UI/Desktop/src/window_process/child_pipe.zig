//! W0: one child owns both directions. The caller's scoped Ledger workspace
//! outlives run(), all I/O threads and the last synchronous receive callback.
//! No pointer into the receive region survives that callback.
const std = @import("std");
const w = @import("win32.zig");
pub const protocol = @import("protocol.zig");

pub const Config = extern struct {
    queue_capacity: u32,
    cancel_poll_ms: u32,
    reader_stack_bytes: usize,
    writer_stack_bytes: usize,
    json_max_depth: u32,
    // Original SendAsync preflight policy, supplied from its original owner.
    input_margin_bytes: u32,
    input_escape_multiplier: u32,
};
pub const Text = extern struct { data: ?[*]const u16, length: usize };
pub const Kind = enum(u32) { running, backend_session, language, visibility, show_window };
pub const Message = extern struct {
    kind: u32,
    flag: u32 = 0,
    generation: i64 = 0,
    value: ?[*]const u8 = null,
    value_length: usize = 0,
};
pub const Io = extern struct {
    read: *const fn (?*anyopaque, [*]u8, usize) callconv(.c) isize,
    write: *const fn (?*anyopaque, [*]const u8, usize) callconv(.c) c_int,
    close: *const fn (?*anyopaque) callconv(.c) void,
    ready: *const fn (?*anyopaque, c_int) callconv(.c) void,
    sent: *const fn (?*anyopaque) callconv(.c) void,
};
pub const Receiver = extern struct {
    context: ?*anyopaque,
    receive: *const fn (?*anyopaque, *const Message, u32) callconv(.c) c_int,
    finished: *const fn (?*anyopaque, c_int) callconv(.c) void,
};
const Slot = struct { length: usize, completion: ?*anyopaque };
pub const SendResult = enum(c_int) { queued = 0, stopped = 1, full = 2, oversized = 3, invalid = 4 };

pub fn validate(config: Config) !void {
    if (config.queue_capacity == 0 or config.cancel_poll_ms == 0 or config.cancel_poll_ms > std.math.maxInt(i32) or
        config.reader_stack_bytes == 0 or config.writer_stack_bytes == 0 or config.json_max_depth == 0 or
        config.input_margin_bytes >= protocol.max_line or config.input_escape_multiplier == 0) return error.InvalidConfig;
}

// The iterative decoder retains original wire slices, not a JSON object graph.
// JsonDocument permits unpaired \u surrogate escapes in ignored fields. Decode
// selected strings only, preserving that distinction from Zig's JSON Scanner.
const RawKind = enum { string, number, true_value, false_value, null_value, object, array };
const Raw = struct { kind: RawKind, bytes: []const u8 };
const Step = enum { object_first, object_key, object_colon, object_value, object_next, array_first, array_value, array_next };
const Frame = struct { step: Step, field: ?usize = null };
const names = [_][]const u8{ "type", "running", "generation", "ready", "reason", "language", "visible", "command" };

pub fn parseBytes(line_length: usize, max_depth: u32) !usize {
    if (max_depth == 0) return error.InvalidConfig;
    const frames = @min(@as(usize, max_depth), try std.math.add(usize, line_length, 1));
    const stack_bytes = try std.math.mul(usize, frames, @sizeOf(Frame));
    return std.math.add(usize, stack_bytes, try std.math.add(usize, line_length, @alignOf(Frame)));
}
pub fn workspaceBytes(config: Config) !usize {
    try validate(config);
    const slots = try std.math.add(usize, config.queue_capacity, 1);
    const queue = try std.math.mul(usize, slots, @sizeOf(Slot) + protocol.max_line);
    return std.math.add(usize, queue, protocol.max_line + try parseBytes(protocol.max_line, config.json_max_depth) + @alignOf(Slot));
}

pub fn decode(line: []const u8, storage: []u8, max_depth: u32) !Message {
    if (max_depth == 0) return error.InvalidMessage;
    var fba = std.heap.FixedBufferAllocator.init(storage);
    const allocator = fba.allocator();
    const stack = try allocator.alloc(Frame, @min(@as(usize, max_depth), line.len + 1));
    var cursor: usize = 0;
    const root = try rawValue(line, &cursor);
    var depth: usize = 0;
    try push(stack, &depth, root);
    var fields: [names.len]?Raw = @splat(null);
    while (depth != 0) {
        const frame = &stack[depth - 1];
        whitespace(line, &cursor);
        if (cursor == line.len) return error.InvalidMessage;
        switch (frame.step) {
            .object_first, .object_key => {
                if (frame.step == .object_first and line[cursor] == '}') {
                    cursor += 1;
                    depth -= 1;
                    continue;
                }
                const key = try rawString(line, &cursor);
                frame.field = null;
                if (depth == 1) for (names, 0..) |name, index| {
                    if (keyEquals(key, name)) {
                        frame.field = index;
                        break;
                    }
                };
                frame.step = .object_colon;
            },
            .object_colon => {
                if (line[cursor] != ':') return error.InvalidMessage;
                cursor += 1;
                frame.step = .object_value;
            },
            .object_value => {
                const value = try rawValue(line, &cursor);
                if (depth == 1) if (frame.field) |index| {
                    fields[index] = value;
                };
                frame.step = .object_next;
                try push(stack, &depth, value);
            },
            .object_next => {
                switch (line[cursor]) {
                    ',' => {
                        cursor += 1;
                        frame.step = .object_key;
                    },
                    '}' => {
                        cursor += 1;
                        depth -= 1;
                    },
                    else => return error.InvalidMessage,
                }
            },
            .array_first, .array_value => {
                if (frame.step == .array_first and line[cursor] == ']') {
                    cursor += 1;
                    depth -= 1;
                    continue;
                }
                const value = try rawValue(line, &cursor);
                frame.step = .array_next;
                try push(stack, &depth, value);
            },
            .array_next => {
                switch (line[cursor]) {
                    ',' => {
                        cursor += 1;
                        frame.step = .array_value;
                    },
                    ']' => {
                        cursor += 1;
                        depth -= 1;
                    },
                    else => return error.InvalidMessage,
                }
            },
        }
    }
    whitespace(line, &cursor);
    if (cursor != line.len or root.kind != .object) return error.InvalidMessage;
    const kind = try rawText(fields[0] orelse return error.InvalidMessage, allocator);
    if (std.mem.eql(u8, kind, "parent-wants-me-running")) return .{ .kind = @backingInt(Kind.running), .flag = try rawBool(fields[1]) };
    if (std.mem.eql(u8, kind, "stop")) return .{ .kind = @backingInt(Kind.running) };
    if (std.mem.eql(u8, kind, "visibility")) return .{ .kind = @backingInt(Kind.visibility), .flag = try rawBool(fields[6]) };
    if (std.mem.eql(u8, kind, "language")) return messageText(.language, try nullableText(fields[5] orelse return error.InvalidMessage, allocator));
    if (std.mem.eql(u8, kind, "show-window")) {
        const command = try rawText(fields[7] orelse return error.InvalidMessage, allocator);
        if (std.meta.stringToEnum(protocol.ShowCommand, command) == null) return error.InvalidMessage;
        return messageText(.show_window, command);
    }
    if (std.mem.eql(u8, kind, "backend-session")) {
        const number = fields[2] orelse return error.InvalidMessage;
        if (number.kind != .number) return error.InvalidMessage;
        for (number.bytes) |c| if (c == '.' or c == 'e' or c == 'E') return error.InvalidMessage;
        const generation = std.fmt.parseInt(i64, number.bytes, 10) catch return error.InvalidMessage;
        if (generation < 0) return error.InvalidMessage;
        var result = messageText(.backend_session, if (fields[4]) |reason| try nullableText(reason, allocator) else null);
        result.flag = try rawBool(fields[3]);
        result.generation = generation;
        return result;
    }
    return error.InvalidMessage;
}
fn push(stack: []Frame, depth: *usize, value: Raw) !void {
    const step: Step = switch (value.kind) {
        .object => .object_first,
        .array => .array_first,
        else => return,
    };
    if (depth.* == stack.len) return error.InvalidMessage;
    stack[depth.*] = .{ .step = step };
    depth.* += 1;
}
fn whitespace(line: []const u8, cursor: *usize) void {
    while (cursor.* < line.len) : (cursor.* += 1) switch (line[cursor.*]) {
        ' ', '\t', '\r', '\n' => {},
        else => return,
    };
}
fn hexDigit(c: u8) !u16 {
    return switch (c) {
        '0'...'9' => c - '0',
        'a'...'f' => c - 'a' + 10,
        'A'...'F' => c - 'A' + 10,
        else => error.InvalidMessage,
    };
}
fn unit(bytes: []const u8) !u16 {
    if (bytes.len < 4) return error.InvalidMessage;
    var value: u16 = 0;
    for (bytes[0..4]) |c| value = (value << 4) | try hexDigit(c);
    return value;
}
fn rawString(line: []const u8, cursor: *usize) ![]const u8 {
    if (cursor.* == line.len or line[cursor.*] != '"') return error.InvalidMessage;
    cursor.* += 1;
    const start = cursor.*;
    while (cursor.* < line.len) {
        const c = line[cursor.*];
        cursor.* += 1;
        if (c == '"') return line[start .. cursor.* - 1];
        if (c < 0x20) return error.InvalidMessage;
        if (c != '\\') continue;
        if (cursor.* == line.len) return error.InvalidMessage;
        const escaped = line[cursor.*];
        cursor.* += 1;
        switch (escaped) {
            '"', '\\', '/', 'b', 'f', 'n', 'r', 't' => {},
            'u' => {
                _ = try unit(line[cursor.*..]);
                cursor.* += 4;
            },
            else => return error.InvalidMessage,
        }
    }
    return error.InvalidMessage;
}
fn rawValue(line: []const u8, cursor: *usize) !Raw {
    whitespace(line, cursor);
    if (cursor.* == line.len) return error.InvalidMessage;
    const start = cursor.*;
    switch (line[start]) {
        '"' => return .{ .kind = .string, .bytes = try rawString(line, cursor) },
        '{', '[' => |c| {
            cursor.* += 1;
            return .{ .kind = if (c == '{') .object else .array, .bytes = &.{} };
        },
        't', 'f', 'n' => {
            const text: []const u8 = switch (line[start]) {
                't' => "true",
                'f' => "false",
                else => "null",
            };
            if (!std.mem.startsWith(u8, line[start..], text)) return error.InvalidMessage;
            cursor.* += text.len;
            return .{ .kind = switch (line[start]) {
                't' => .true_value,
                'f' => .false_value,
                else => .null_value,
            }, .bytes = line[start..cursor.*] };
        },
        '-', '0'...'9' => {
            if (line[cursor.*] == '-') cursor.* += 1;
            if (cursor.* == line.len) return error.InvalidMessage;
            if (line[cursor.*] == '0') cursor.* += 1 else {
                if (line[cursor.*] < '1' or line[cursor.*] > '9') return error.InvalidMessage;
                digits(line, cursor);
            }
            if (cursor.* < line.len and line[cursor.*] == '.') {
                cursor.* += 1;
                const first = cursor.*;
                digits(line, cursor);
                if (first == cursor.*) return error.InvalidMessage;
            }
            if (cursor.* < line.len and (line[cursor.*] == 'e' or line[cursor.*] == 'E')) {
                cursor.* += 1;
                if (cursor.* < line.len and (line[cursor.*] == '+' or line[cursor.*] == '-')) cursor.* += 1;
                const first = cursor.*;
                digits(line, cursor);
                if (first == cursor.*) return error.InvalidMessage;
            }
            return .{ .kind = .number, .bytes = line[start..cursor.*] };
        },
        else => return error.InvalidMessage,
    }
}
fn digits(line: []const u8, cursor: *usize) void {
    while (cursor.* < line.len and line[cursor.*] >= '0' and line[cursor.*] <= '9') cursor.* += 1;
}
fn keyEquals(key: []const u8, name: []const u8) bool {
    var i: usize = 0;
    var n: usize = 0;
    while (i < key.len) {
        var c: u16 = key[i];
        i += 1;
        if (c == '\\') {
            const escaped = key[i];
            i += 1;
            if (escaped != 'u') return false; // None of the fixed names contains a JSON short escape.
            c = unit(key[i..]) catch return false;
            i += 4;
        }
        if (n == name.len or c != name[n]) return false;
        n += 1;
    }
    return n == name.len;
}
fn rawText(value: Raw, allocator: std.mem.Allocator) ![]const u8 {
    if (value.kind != .string) return error.InvalidMessage;
    if (std.mem.indexOfScalar(u8, value.bytes, '\\') == null) {
        if (!std.unicode.utf8ValidateSlice(value.bytes)) return error.InvalidMessage;
        return value.bytes;
    }
    const output = try allocator.alloc(u8, value.bytes.len);
    var i: usize = 0;
    var n: usize = 0;
    while (i < value.bytes.len) {
        const c = value.bytes[i];
        i += 1;
        if (c != '\\') {
            output[n] = c;
            n += 1;
            continue;
        }
        const escaped = value.bytes[i];
        i += 1;
        if (escaped == 'u') {
            const first = try unit(value.bytes[i..]);
            i += 4;
            var scalar: u21 = first;
            if (first >= 0xd800 and first <= 0xdbff) {
                if (value.bytes.len - i < 6 or value.bytes[i] != '\\' or value.bytes[i + 1] != 'u') return error.InvalidMessage;
                const second = try unit(value.bytes[i + 2 ..]);
                if (second < 0xdc00 or second > 0xdfff) return error.InvalidMessage;
                i += 6;
                scalar = 0x10000 + (@as(u21, first - 0xd800) << 10) + second - 0xdc00;
            } else if (first >= 0xdc00 and first <= 0xdfff) return error.InvalidMessage;
            var encoded: [4]u8 = undefined;
            const length = try std.unicode.utf8Encode(scalar, &encoded);
            @memcpy(output[n..][0..length], encoded[0..length]);
            n += length;
        } else {
            output[n] = switch (escaped) {
                '"' => '"',
                '\\' => '\\',
                '/' => '/',
                'b' => 8,
                'f' => 12,
                'n' => '\n',
                'r' => '\r',
                't' => '\t',
                else => return error.InvalidMessage,
            };
            n += 1;
        }
    }
    if (!std.unicode.utf8ValidateSlice(output[0..n])) return error.InvalidMessage;
    return output[0..n];
}
fn nullableText(value: Raw, allocator: std.mem.Allocator) !?[]const u8 {
    return if (value.kind == .null_value) null else try rawText(value, allocator);
}
fn rawBool(value: ?Raw) !u32 {
    return switch ((value orelse return error.InvalidMessage).kind) {
        .true_value => 1,
        .false_value => 0,
        else => error.InvalidMessage,
    };
}
fn messageText(kind: Kind, value: ?[]const u8) Message {
    return .{ .kind = @backingInt(kind), .value = if (value) |s| s.ptr else null, .value_length = if (value) |s| s.len else 0 };
}
fn textSlice(text: Text) ![]const u16 {
    if (text.data) |p| return p[0..text.length];
    if (text.length != 0) return error.InvalidText;
    return &.{};
}
fn utf8Length(text: []const u16) usize {
    var result: usize = 0;
    var i: usize = 0;
    while (i < text.len) : (i += 1) {
        const c = text[i];
        if (c >= 0xd800 and c <= 0xdbff and i + 1 < text.len and text[i + 1] >= 0xdc00 and text[i + 1] <= 0xdfff) {
            result += 4;
            i += 1;
        } else result += if (c < 0x80) @as(usize, 1) else if (c < 0x800) @as(usize, 2) else 3;
    }
    return result;
}
fn append(output: []u8, length: *usize, bytes: []const u8) !void {
    if (bytes.len > output.len - length.*) return error.MessageTooLong;
    @memcpy(output[length.*..][0..bytes.len], bytes);
    length.* += bytes.len;
}
fn escapedUnit(output: []u8, length: *usize, value: u16) !void {
    const hex = "0123456789ABCDEF";
    try append(output, length, &.{ '\\', 'u', hex[value >> 12], hex[(value >> 8) & 15], hex[(value >> 4) & 15], hex[value & 15] });
}
// Utf8JsonWriter's original JavaScriptEncoder.Default: Basic Latin with its
// HTML-sensitive block list. Work from original UTF-16, into the final slot.
fn quote(output: []u8, length: *usize, text: []const u16) !void {
    try append(output, length, "\"");
    var i: usize = 0;
    while (i < text.len) : (i += 1) {
        const c = text[i];
        switch (c) {
            '\\' => try append(output, length, "\\\\"),
            '\n' => try append(output, length, "\\n"),
            '\r' => try append(output, length, "\\r"),
            '\t' => try append(output, length, "\\t"),
            8 => try append(output, length, "\\b"),
            12 => try append(output, length, "\\f"),
            else => {
                if (c >= 0xd800 and c <= 0xdbff and i + 1 < text.len and text[i + 1] >= 0xdc00 and text[i + 1] <= 0xdfff) {
                    try escapedUnit(output, length, c);
                    i += 1;
                    try escapedUnit(output, length, text[i]);
                } else if (c >= 0xd800 and c <= 0xdfff) try escapedUnit(output, length, 0xfffd) else if (c < 0x20 or c > 0x7e or c == '"' or c == '\'' or c == '&' or c == '+' or c == '<' or c == '>' or c == '`') {
                    try escapedUnit(output, length, c);
                } else try append(output, length, &.{@intCast(c)});
            },
        }
    }
    try append(output, length, "\"");
}
pub fn encode(output: []u8, config: Config, kind: Text, field: Text, value: Text) !usize {
    const t = try textSlice(kind);
    const f = try textSlice(field);
    const v = try textSlice(value);
    if (t.len > protocol.max_line or f.len > protocol.max_line or v.len > protocol.max_line or
        utf8Length(t) + utf8Length(f) + utf8Length(v) > (protocol.max_line - config.input_margin_bytes) / config.input_escape_multiplier) return error.MessageTooLong;
    var length: usize = 0;
    try append(output, &length, "{\"type\":");
    try quote(output, &length, t);
    if (field.data != null) {
        try append(output, &length, ",");
        try quote(output, &length, f);
        try append(output, &length, ":");
        if (value.data == null) try append(output, &length, "null") else try quote(output, &length, v);
    }
    try append(output, &length, "}\n");
    return length;
}

pub const Owner = struct {
    config: Config,
    io: Io,
    context: ?*anyopaque,
    mutex: w.Mutex = .{},
    stopped: bool = false,
    active: bool = false,
    ready_for_calls: bool = false,
    finished: bool = false,
    reader_started: bool = false,
    receiver: ?Receiver = null,
    slots: []Slot = &.{},
    payload: []u8 = &.{},
    input: []u8 = &.{},
    scratch: []u8 = &.{},
    head: usize = 0,
    count: usize = 0,
    read_thread: ?w.HANDLE = null,
    write_thread: ?w.HANDLE = null,
    reader_done: bool = false,
    writer_done: bool = false,
    wake: ?w.HANDLE = null,
    start: ?w.HANDLE = null,
    stopping: ?w.HANDLE = null,
    done: ?w.HANDLE = null,

    pub fn init(config: Config, io: Io, context: ?*anyopaque) !Owner {
        _ = try workspaceBytes(config);
        return .{ .config = config, .io = io, .context = context };
    }
    /// Called exactly once. Storage is a scoped lease, never an owned mirror.
    pub fn run(self: *Owner, storage: []u8) !void {
        self.mutex.lock();
        if (self.active or self.finished) {
            self.mutex.unlock();
            return error.AlreadyRunning;
        }
        self.active = true;
        self.mutex.unlock();
        defer {
            self.mutex.lock();
            self.active = false;
            self.ready_for_calls = false;
            self.finished = true;
            self.slots = &.{};
            self.payload = &.{};
            self.input = &.{};
            self.scratch = &.{};
            self.mutex.unlock();
            self.io.close(self.context);
        }
        defer self.closeEvents();
        var fba = std.heap.FixedBufferAllocator.init(storage);
        const alloc = fba.allocator();
        self.slots = try alloc.alloc(Slot, @as(usize, self.config.queue_capacity) + 1);
        self.payload = try alloc.alloc(u8, self.slots.len * protocol.max_line);
        self.input = try alloc.alloc(u8, protocol.max_line);
        self.scratch = try alloc.alloc(u8, try parseBytes(protocol.max_line, self.config.json_max_depth));
        try self.createEvent("wake");
        try self.createEvent("start");
        try self.createEvent("stopping");
        try self.createEvent("done");
        const writer = try std.Thread.spawn(.{ .stack_size = self.config.writer_stack_bytes }, writerMain, .{self});
        const reader = std.Thread.spawn(.{ .stack_size = self.config.reader_stack_bytes }, readerMain, .{self}) catch |err| {
            self.stop();
            writer.join();
            return err;
        };
        self.mutex.lock();
        self.ready_for_calls = true;
        if (self.stopped) _ = w.SetEvent(self.stopping.?);
        self.mutex.unlock();
        self.io.ready(self.context, 0);
        _ = w.WaitForSingleObject(self.stopping.?, w.infinite);
        while (true) {
            self.mutex.lock();
            const all_done = self.reader_done and self.writer_done;
            if (self.read_thread) |thread| _ = w.CancelSynchronousIo(thread);
            if (self.write_thread) |thread| _ = w.CancelSynchronousIo(thread);
            self.mutex.unlock();
            if (all_done) break;
            _ = w.WaitForSingleObject(self.done.?, self.config.cancel_poll_ms);
        }
        reader.join();
        writer.join();
    }
    pub fn startReader(self: *Owner, receiver: Receiver) !bool {
        self.mutex.lock();
        defer self.mutex.unlock();
        if (self.reader_started) return error.AlreadyReading;
        // Old RunAsync after Dispose invokes withdrawal without installing a reader.
        if (self.stopped) return false;
        if (!self.ready_for_calls) return error.NotReady;
        self.reader_started = true;
        self.receiver = receiver;
        _ = w.SetEvent(self.start.?);
        return true;
    }
    pub fn send(self: *Owner, kind: Text, field: Text, value: Text, completion: ?*anyopaque) SendResult {
        self.mutex.lock();
        defer self.mutex.unlock();
        if (self.stopped) return .stopped;
        if (!self.ready_for_calls) return .invalid;
        // Preserve preflight-before-queue-full precedence without an extra slot.
        const t = textSlice(kind) catch return .invalid;
        const f = textSlice(field) catch return .invalid;
        const v = textSlice(value) catch return .invalid;
        if (t.len > protocol.max_line or f.len > protocol.max_line or v.len > protocol.max_line or utf8Length(t) + utf8Length(f) + utf8Length(v) >
            (protocol.max_line - self.config.input_margin_bytes) / self.config.input_escape_multiplier) return .oversized;
        if (self.count == self.config.queue_capacity) return .full;
        const index = (self.head + self.count) % self.slots.len;
        const length = encode(self.slotBytes(index), self.config, kind, field, value) catch return .oversized;
        self.slots[index] = .{ .length = length, .completion = completion };
        self.count += 1;
        _ = w.SetEvent(self.wake.?);
        return .queued;
    }
    pub fn stop(self: *Owner) void {
        self.mutex.lock();
        defer self.mutex.unlock();
        self.stopped = true;
        if (self.wake) |handle| _ = w.SetEvent(handle);
        if (self.start) |handle| _ = w.SetEvent(handle);
        if (self.stopping) |handle| _ = w.SetEvent(handle);
    }
    fn slotBytes(self: *Owner, index: usize) []u8 {
        return self.payload[index * protocol.max_line ..][0..protocol.max_line];
    }
    fn closeEvents(self: *Owner) void {
        self.mutex.lock();
        defer self.mutex.unlock();
        inline for (.{ "wake", "start", "stopping", "done" }) |name| {
            if (@field(self, name)) |handle| w.close(handle);
            @field(self, name) = null;
        }
    }
    fn createEvent(self: *Owner, comptime name: []const u8) !void {
        const event = w.CreateEventW(null, 1, 0, null) orelse return error.CreateEventFailed;
        self.mutex.lock();
        @field(self, name) = event;
        if (self.stopped and !std.mem.eql(u8, name, "done")) _ = w.SetEvent(event);
        self.mutex.unlock();
    }
    fn threadDone(self: *Owner, reading: bool) void {
        self.mutex.lock();
        defer self.mutex.unlock();
        const handle = if (reading) self.read_thread else self.write_thread;
        if (handle) |h| w.close(h);
        if (reading) {
            self.read_thread = null;
            self.reader_done = true;
        } else {
            self.write_thread = null;
            self.writer_done = true;
        }
        if (self.reader_done and self.writer_done) _ = w.SetEvent(self.done.?);
    }
    fn readerMain(self: *Owner) void {
        defer self.threadDone(true);
        const thread = OpenThread(0x0001, 0, GetCurrentThreadId());
        self.mutex.lock();
        self.read_thread = thread;
        self.mutex.unlock();
        _ = w.WaitForSingleObject(self.start.?, w.infinite);
        self.mutex.lock();
        const receiver = self.receiver;
        self.mutex.unlock();
        const target = receiver orelse return;
        var status: c_int = if (thread == null) 1 else 0;
        var count: usize = 0;
        while (status == 0) {
            self.mutex.lock();
            const stopped = self.stopped;
            self.mutex.unlock();
            if (stopped) break;
            const got = self.io.read(self.context, self.input[count..].ptr, self.input.len - count);
            if (got <= 0) break;
            if (@as(usize, @intCast(got)) > self.input.len - count) {
                status = 1;
                break;
            }
            const end = count + @as(usize, @intCast(got));
            var start_index: usize = 0;
            var index = count;
            while (index < end) : (index += 1) {
                if (self.input[index] != '\n') continue;
                const message = decode(self.input[start_index..index], self.scratch, self.config.json_max_depth) catch {
                    status = 1;
                    break;
                };
                // Native message/scratch remain live until UI's Task really ends.
                if (target.receive(target.context, &message, 0) != 0) {
                    status = 1;
                    break;
                }
                start_index = index + 1;
            }
            if (status != 0) break;
            count = end - start_index;
            if (count == self.input.len) break;
            if (start_index != 0) std.mem.copyForwards(u8, self.input[0..count], self.input[start_index..end]);
        }
        const withdrawal = Message{ .kind = @backingInt(Kind.running) };
        if (target.receive(target.context, &withdrawal, 1) != 0) status = 2;
        target.finished(target.context, status);
    }
    fn writerMain(self: *Owner) void {
        defer self.threadDone(false);
        const thread = OpenThread(0x0001, 0, GetCurrentThreadId());
        self.mutex.lock();
        self.write_thread = thread;
        self.mutex.unlock();
        if (thread == null) {
            self.stop();
            return;
        }
        while (true) {
            _ = w.WaitForSingleObject(self.wake.?, w.infinite);
            self.mutex.lock();
            if (self.count == 0) {
                const stopped = self.stopped;
                _ = w.ResetEvent(self.wake.?);
                self.mutex.unlock();
                if (stopped) return;
                continue;
            }
            const index = self.head;
            const slot = self.slots[index];
            self.head = (self.head + 1) % self.slots.len;
            self.count -= 1;
            const stopped = self.stopped;
            self.mutex.unlock();
            if (!stopped and self.io.write(self.context, self.slotBytes(index).ptr, slot.length) != 0) self.stop();
            self.io.sent(slot.completion);
        }
    }
};
extern "kernel32" fn OpenThread(u32, c_int, u32) callconv(.winapi) ?w.HANDLE;
extern "kernel32" fn GetCurrentThreadId() callconv(.winapi) u32;
