const std = @import("std");

// One request, not a desktop-fact owner. The host owns this small value across
// awaits; no callback, input address or managed object is retained here.
pub const State = extern struct {
    scanned: u64 = 0,
    phase: u32 = 0, // fresh, reading, stopping, ended
    selected: u32 = 0,
};

pub const Options = extern struct {
    maximum_line_bytes: u64,
    // Supplied from the original Utf8JsonReader's effective reader options.
    maximum_depth: u32,
};

pub const Window = extern struct {
    creation_file_time: i64 = 0,
    process_id: i32 = 0,
    visible: u32 = 0,
    background: u32 = 0,
    present: u32 = 0,
};

pub const Observation = extern struct {
    publisher_creation_file_time: i64 = 0,
    observed_at_ticks: i64 = 0,
    publisher_process_id: i32 = 0,
    session_id: i32 = 0,
    complete: u32 = 0,
    foreground_known: u32 = 0,
    foreground: Window = .{},
    windows_count: u64 = 0,
    windows_present: u32 = 0,
};

// All callbacks are synchronous. borrow replaces the previous segment borrow;
// the host releases its final pin after the ABI call, before AdvanceTo/await.
// begin/window/publish construct only the original store's typed input, never
// another native fact collection. No exception may cross these callbacks.
pub const Callbacks = extern struct {
    borrow: *const fn (?*anyopaque, u64, *?[*]const u8, *usize) callconv(.c) i32,
    select: *const fn (?*anyopaque) callconv(.c) i32,
    release: *const fn (?*anyopaque) callconv(.c) i32,
    begin: *const fn (?*anyopaque, *const Observation) callconv(.c) i32,
    window: *const fn (?*anyopaque, u64, *const Window) callconv(.c) i32,
    publish: *const fn (?*anyopaque) callconv(.c) i32,
};

const Failure = error{ Invalid, Bridge };

const Source = struct {
    context: ?*anyopaque,
    callbacks: *const Callbacks,

    fn borrow(self: Source, offset: u64, limit: u64) Failure![]const u8 {
        var pointer: ?[*]const u8 = null;
        var length: usize = 0;
        if (self.callbacks.borrow(self.context, offset, &pointer, &length) != 0) return error.Bridge;
        if (length == 0 or pointer == null or offset >= limit) return error.Bridge;
        return pointer.?[0..@min(length, limit - offset)];
    }
};

pub fn begin(state: *State, verified: bool, session_id: i32, admitted: bool, rejection_status: i32, context: ?*anyopaque, callbacks: *const Callbacks) i32 {
    if (state.phase != 0) return -1;
    // The one permission owner supplies the decision. This HTTP leaf does not
    // interpret a token, key, scope or operation switch, or grant permission.
    if (!admitted) {
        state.phase = 2;
        return if (rejection_status >= 400 and rejection_status <= 599) rejection_status else -1;
    }
    if (!verified or session_id <= 0) {
        state.phase = 2;
        return 403;
    }
    const selected = callbacks.select(context);
    if (selected < 0) return -1;
    if (selected == 0) {
        state.phase = 2;
        return 409;
    }
    state.selected = 1;
    state.phase = 1;
    return 200;
}

pub fn end(state: *State, context: ?*anyopaque, callbacks: *const Callbacks) i32 {
    state.phase = 3;
    state.scanned = 0;
    if (state.selected == 0) return 0;
    // End is idempotent even if the managed release throws.
    state.selected = 0;
    return callbacks.release(context);
}

// consumed is measured in the original ReadOnlySequence. An incomplete line
// stays in that sequence, while scanned avoids rescanning an idle partial line.
pub fn read(state: *State, options: *const Options, length: u64, completed: bool, canceled: bool, context: ?*anyopaque, callbacks: *const Callbacks, consumed: *u64) i32 {
    consumed.* = 0;
    if (state.phase != 1 or state.scanned > length) return -1;
    const source: Source = .{ .context = context, .callbacks = callbacks };
    var offset = state.scanned;
    while (offset < length) {
        const bytes = source.borrow(offset, length) catch {
            state.phase = 2;
            return -1;
        };
        if (std.mem.indexOfScalar(u8, bytes, '\n')) |newline| {
            const line_end = offset + newline;
            const line_start = consumed.*;
            consumed.* = line_end + 1;
            if (line_end - line_start > options.maximum_line_bytes) {
                state.phase = 2;
                return 0;
            }
            decode(source, options, line_start, line_end) catch |err| {
                state.phase = 2;
                return if (err == error.Bridge) -1 else 0;
            };
            offset = consumed.*;
        } else {
            offset += bytes.len;
        }
    }
    state.scanned = length - consumed.*;
    if (state.scanned > options.maximum_line_bytes or completed or canceled) {
        state.phase = 2;
        return 0;
    }
    return 1;
}

// The parser borrows the wire directly. First validate the complete typed
// value, remembering only the last windows array's range. Decode that array
// into the managed store input only after validation. This preserves duplicate
// properties (including an earlier oversized array overwritten by a later one)
// without a JSON DOM, native row array, line copy or large scratch allocation.
const Cursor = struct {
    source: Source,
    offset: u64,
    limit: u64,
    bytes: []const u8 = &.{},
    position: usize = 0,

    fn peek(self: *Cursor) Failure!?u8 {
        if (self.offset == self.limit) return null;
        if (self.position == self.bytes.len) {
            self.bytes = try self.source.borrow(self.offset, self.limit);
            self.position = 0;
        }
        return self.bytes[self.position];
    }

    fn take(self: *Cursor) Failure!u8 {
        const byte = (try self.peek()) orelse return error.Invalid;
        self.position += 1;
        self.offset += 1;
        return byte;
    }

    fn whitespace(self: *Cursor) Failure!void {
        while (try self.peek()) |byte| {
            switch (byte) {
                ' ', '\r', '\t', '\n' => _ = try self.take(),
                else => return,
            }
        }
    }

    fn expect(self: *Cursor, byte: u8) Failure!void {
        try self.whitespace();
        if (try self.take() != byte) return error.Invalid;
    }

    fn literal(self: *Cursor, rest: []const u8) Failure!void {
        for (rest) |byte| if (try self.take() != byte) return error.Invalid;
    }

    fn hex(self: *Cursor) Failure!u16 {
        var value: u16 = 0;
        for (0..4) |_| {
            const byte = try self.take();
            const digit = std.fmt.charToDigit(byte, 16) catch return error.Invalid;
            value = value * 16 + digit;
        }
        return value;
    }

    // Unknown JSON strings are syntactically skipped like Utf8JsonReader.TrySkip:
    // escaped surrogate halves need not be materialized as Unicode strings.
    // Known names and numeric strings collect decoded ASCII, with no heap buffer.
    fn string(self: *Cursor, output: ?[]u8) Failure!usize {
        return self.stringValue(output, null);
    }

    fn stringValue(self: *Cursor, output: ?[]u8, integer_value: ?*IntegerValue) Failure!usize {
        try self.expect('"');
        var length: usize = 0;
        while (true) {
            var value: u16 = try self.take();
            if (value == '"') return length;
            if (value < 0x20) return error.Invalid;
            if (value == '\\') {
                value = switch (try self.take()) {
                    '"' => '"',
                    '\\' => '\\',
                    '/' => '/',
                    'b' => 8,
                    'f' => 12,
                    'n' => 10,
                    'r' => 13,
                    't' => 9,
                    'u' => blk: {
                        const unit = try self.hex();
                        if (output != null or integer_value != null) {
                            if (unit >= 0xd800 and unit <= 0xdbff) {
                                if ((try self.take()) != '\\' or (try self.take()) != 'u') return error.Invalid;
                                const low = try self.hex();
                                if (low < 0xdc00 or low > 0xdfff) return error.Invalid;
                            } else if (unit >= 0xdc00 and unit <= 0xdfff) return error.Invalid;
                        }
                        break :blk unit;
                    },
                    else => return error.Invalid,
                };
            }
            if (output) |destination| {
                if (length < destination.len) destination[length] = if (value <= 0x7f) @intCast(value) else 0xff;
            }
            if (integer_value) |number_value| number_value.add(value);
            length += 1;
        }
    }

    fn name(self: *Cursor, names: []const []const u8) Failure!?usize {
        // Capacity is representation storage, not a wire-size restriction: longer
        // unknown names are fully scanned and ignored without truncation matching.
        var bytes: [@sizeOf(Observation)]u8 = undefined;
        const length = try self.string(&bytes);
        if (length <= bytes.len) {
            for (names, 0..) |candidate, index| {
                if (std.ascii.eqlIgnoreCase(candidate, bytes[0..length])) return index;
            }
        }
        return null;
    }

    fn boolean(self: *Cursor) Failure!u32 {
        try self.whitespace();
        return switch (try self.take()) {
            't' => blk: {
                try self.literal("rue");
                break :blk 1;
            },
            'f' => blk: {
                try self.literal("alse");
                break :blk 0;
            },
            else => error.Invalid,
        };
    }

    fn number(self: *Cursor, output: ?[]u8) Failure!usize {
        var length: usize = 0;
        var byte = (try self.peek()) orelse return error.Invalid;
        if (byte == '-') {
            if (output) |dst| if (length < dst.len) {
                dst[length] = byte;
            };
            length += 1;
            _ = try self.take();
            byte = (try self.peek()) orelse return error.Invalid;
        }
        if (byte < '0' or byte > '9') return error.Invalid;
        const leading_zero = byte == '0';
        if (output) |dst| if (length < dst.len) {
            dst[length] = byte;
        };
        length += 1;
        _ = try self.take();
        if (!leading_zero) while (try self.peek()) |digit| {
            if (digit < '0' or digit > '9') break;
            if (output) |dst| if (length < dst.len) {
                dst[length] = digit;
            };
            length += 1;
            _ = try self.take();
        };
        if ((try self.peek()) == '.') {
            if (output) |dst| if (length < dst.len) {
                dst[length] = '.';
            };
            length += 1;
            _ = try self.take();
            var digits: usize = 0;
            while (try self.peek()) |digit| {
                if (digit < '0' or digit > '9') break;
                if (output) |dst| if (length < dst.len) {
                    dst[length] = digit;
                };
                length += 1;
                digits += 1;
                _ = try self.take();
            }
            if (digits == 0) return error.Invalid;
        }
        byte = (try self.peek()) orelse return length;
        if (byte == 'e' or byte == 'E') {
            if (output) |dst| if (length < dst.len) {
                dst[length] = byte;
            };
            length += 1;
            _ = try self.take();
            byte = (try self.peek()) orelse return error.Invalid;
            if (byte == '+' or byte == '-') {
                if (output) |dst| if (length < dst.len) {
                    dst[length] = byte;
                };
                length += 1;
                _ = try self.take();
            }
            var digits: usize = 0;
            while (try self.peek()) |digit| {
                if (digit < '0' or digit > '9') break;
                if (output) |dst| if (length < dst.len) {
                    dst[length] = digit;
                };
                length += 1;
                digits += 1;
                _ = try self.take();
            }
            if (digits == 0) return error.Invalid;
        }
        return length;
    }

    fn integer(self: *Cursor, comptime T: type) Failure!T {
        try self.whitespace();
        // Decimal storage covers every value of T. Oversized tokens are still
        // consumed to their end but cannot silently become truncated integers.
        var bytes: [@bitSizeOf(T)]u8 = undefined;
        if ((try self.peek()) == '"') {
            var value: IntegerValue = .{};
            _ = try self.stringValue(null, &value);
            return value.finish(T);
        }
        const length = try self.number(&bytes);
        if (length == 0 or length > bytes.len) return error.Invalid;
        const digits = bytes[0..length];
        var index: usize = if (digits[0] == '-') 1 else 0;
        if (index == digits.len) return error.Invalid;
        while (index < digits.len) : (index += 1) if (digits[index] < '0' or digits[index] > '9') return error.Invalid;
        return std.fmt.parseInt(T, digits, 10) catch error.Invalid;
    }

    // Iterative JSON grammar for ignored values. No recursion or token/DOM
    // allocations; the original reader's depth is an explicit input.
    fn skip(self: *Cursor, enclosing_depth: u32, maximum_depth: u32) Failure!void {
        const Frame = enum(u8) { object_first, key, colon, object_after, array_first, array_after };
        var stack: [std.math.maxInt(u8)]Frame = undefined;
        var depth: usize = 0;
        var value = true;
        while (true) {
            try self.whitespace();
            if (value) {
                const byte = (try self.peek()) orelse return error.Invalid;
                switch (byte) {
                    '{', '[' => {
                        if (enclosing_depth + depth >= maximum_depth or depth == stack.len) return error.Invalid;
                        _ = try self.take();
                        stack[depth] = if (byte == '{') .object_first else .array_first;
                        depth += 1;
                    },
                    '"' => _ = try self.string(null),
                    't' => try self.literal("true"),
                    'f' => try self.literal("false"),
                    'n' => try self.literal("null"),
                    '-', '0'...'9' => _ = try self.number(null),
                    else => return error.Invalid,
                }
                value = false;
                if (byte == '{' or byte == '[') continue;
            }
            if (depth == 0) return;
            switch (stack[depth - 1]) {
                .object_first => {
                    if ((try self.peek()) == '}') {
                        _ = try self.take();
                        depth -= 1;
                    } else stack[depth - 1] = .key;
                },
                .key => {
                    _ = try self.string(null);
                    stack[depth - 1] = .colon;
                },
                .colon => {
                    try self.expect(':');
                    stack[depth - 1] = .object_after;
                    value = true;
                },
                .object_after => switch (try self.take()) {
                    '}' => depth -= 1,
                    ',' => stack[depth - 1] = .key,
                    else => return error.Invalid,
                },
                .array_first => {
                    if ((try self.peek()) == ']') {
                        _ = try self.take();
                        depth -= 1;
                    } else {
                        stack[depth - 1] = .array_after;
                        value = true;
                    }
                },
                .array_after => switch (try self.take()) {
                    ']' => depth -= 1,
                    ',' => value = true,
                    else => return error.Invalid,
                },
            }
        }
    }
};

const IntegerValue = struct {
    value: u64 = 0,
    negative: bool = false,
    first: bool = true,
    digits: bool = false,
    valid: bool = true,

    fn add(self: *IntegerValue, byte: u16) void {
        if (self.first and (byte == '-' or byte == '+')) {
            self.negative = byte == '-';
            self.first = false;
            return;
        }
        self.first = false;
        if (byte < '0' or byte > '9') {
            self.valid = false;
            return;
        }
        self.digits = true;
        const multiplied = @mulWithOverflow(self.value, 10);
        const added = @addWithOverflow(multiplied[0], byte - '0');
        if (multiplied[1] != 0 or added[1] != 0) self.valid = false;
        self.value = added[0];
    }

    fn finish(self: IntegerValue, comptime T: type) Failure!T {
        if (!self.valid or !self.digits) return error.Invalid;
        const bound: u64 = @as(u64, std.math.maxInt(T)) + @intFromBool(self.negative);
        if (self.value > bound) return error.Invalid;
        if (self.negative and self.value == bound) return std.math.minInt(T);
        const result: T = @intCast(self.value);
        return if (self.negative) -result else result;
    }
};

const observation_names = [_][]const u8{ "publisherProcessId", "publisherCreationFileTimeUtc", "sessionId", "observedAtUtcTicks", "complete", "foregroundKnown", "foreground", "windows" };
const window_names = [_][]const u8{ "processId", "creationFileTimeUtc", "hasVisibleWindow", "hasBackgroundWindow" };

fn window(cursor: *Cursor, depth: u32, maximum_depth: u32) Failure!Window {
    try cursor.whitespace();
    if ((try cursor.peek()) == 'n') {
        try cursor.literal("null");
        return .{};
    }
    if (depth > maximum_depth) return error.Invalid;
    try cursor.expect('{');
    var result: Window = .{ .present = 1 };
    try cursor.whitespace();
    if ((try cursor.peek()) == '}') {
        _ = try cursor.take();
        return result;
    }
    while (true) {
        const field = try cursor.name(&window_names);
        try cursor.expect(':');
        if (field) |index| switch (index) {
            0 => result.process_id = try cursor.integer(i32),
            1 => result.creation_file_time = try cursor.integer(i64),
            2 => result.visible = try cursor.boolean(),
            3 => result.background = try cursor.boolean(),
            else => unreachable,
        } else try cursor.skip(depth, maximum_depth);
        try cursor.whitespace();
        switch (try cursor.take()) {
            '}' => return result,
            ',' => {},
            else => return error.Invalid,
        }
    }
}

fn windows(cursor: *Cursor, maximum_depth: u32, emit: bool) Failure!u64 {
    if (maximum_depth < 2) return error.Invalid;
    try cursor.expect('[');
    try cursor.whitespace();
    if ((try cursor.peek()) == ']') {
        _ = try cursor.take();
        return 0;
    }
    var count: u64 = 0;
    while (true) {
        const row = try window(cursor, 3, maximum_depth);
        if (emit and cursor.source.callbacks.window(cursor.source.context, count, &row) != 0) return error.Bridge;
        count += 1;
        try cursor.whitespace();
        switch (try cursor.take()) {
            ']' => return count,
            ',' => {},
            else => return error.Invalid,
        }
    }
}

fn decode(source: Source, options: *const Options, start: u64, limit: u64) Failure!void {
    if (options.maximum_depth == 0) return error.Invalid;
    var cursor: Cursor = .{ .source = source, .offset = start, .limit = limit };
    var result: Observation = .{};
    var windows_start: u64 = 0;
    var windows_end: u64 = 0;
    try cursor.expect('{');
    try cursor.whitespace();
    if ((try cursor.peek()) == '}') {
        _ = try cursor.take();
    } else while (true) {
        const field = try cursor.name(&observation_names);
        try cursor.expect(':');
        if (field) |index| switch (index) {
            0 => result.publisher_process_id = try cursor.integer(i32),
            1 => result.publisher_creation_file_time = try cursor.integer(i64),
            2 => result.session_id = try cursor.integer(i32),
            3 => result.observed_at_ticks = try cursor.integer(i64),
            4 => result.complete = try cursor.boolean(),
            5 => result.foreground_known = try cursor.boolean(),
            6 => result.foreground = try window(&cursor, 2, options.maximum_depth),
            7 => {
                try cursor.whitespace();
                if ((try cursor.peek()) == 'n') {
                    try cursor.literal("null");
                    result.windows_present = 0;
                    result.windows_count = 0;
                } else {
                    windows_start = cursor.offset;
                    result.windows_count = try windows(&cursor, options.maximum_depth, false);
                    windows_end = cursor.offset;
                    result.windows_present = 1;
                }
            },
            else => unreachable,
        } else try cursor.skip(1, options.maximum_depth);
        try cursor.whitespace();
        switch (try cursor.take()) {
            '}' => break,
            ',' => {},
            else => return error.Invalid,
        }
    }
    try cursor.whitespace();
    if (cursor.offset != limit) return error.Invalid;
    // Fact validation (including the window-count bound) stays in the original
    // store. Decode only its final input, never an additional native collection.
    if (source.callbacks.begin(source.context, &result) != 0) return error.Bridge;
    if (result.windows_present != 0) {
        var rows: Cursor = .{ .source = source, .offset = windows_start, .limit = windows_end };
        _ = try windows(&rows, options.maximum_depth, true);
    }
    const published = source.callbacks.publish(source.context);
    if (published < 0) return error.Bridge;
    if (published == 0) return error.Invalid;
}
