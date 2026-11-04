//! Shared wire primitives extracted from the existing synchronous writer.
//! No schema, HTTP state or source ownership.
const std = @import("std");
const allowed_bmp = @import("../http/http_json_allowed_bmp.zig");
pub const OutputCallbacks = extern struct {
    reserve: *const fn (?*anyopaque, usize, *?[*]u8, *usize) callconv(.c) i32,
    commit: *const fn (?*anyopaque, usize) callconv(.c) i32,
};
pub const DateParts = extern struct {
    year: i32,
    month: i32,
    day: i32,
    hour: i32,
    minute: i32,
    second: i32,
    fraction: i32,
    offset_minutes: i32,
};
pub const Code = struct {
    pub const success: i32 = 0;
    pub const invalid: i32 = 1;
    pub const callback: i32 = 2;
    pub const short_buffer: i32 = 3;
    pub const overflow: i32 = 4;
};

/// Opaque to managed callers. No pointer in this stack-local writer survives
/// encode(). Short buffers continue counting, including inside text callbacks.
pub const Writer = struct {
    buffer: ?[*]u8,
    capacity: usize,
    length: u64 = 0,
    failure: i32 = Code.success,
    output_callbacks: ?*const OutputCallbacks = null,
    context: ?*anyopaque = null,
    used: usize = 0,

    pub fn raw(self: *Writer, bytes: []const u8) void {
        if (self.failure != Code.success) return;
        const sum = @addWithOverflow(self.length, @as(u64, @intCast(bytes.len)));
        if (sum[1] != 0) {
            self.failure = Code.overflow;
            return;
        }
        if (self.output_callbacks) |output| {
            var consumed: usize = 0;
            while (consumed < bytes.len) {
                if (self.used == self.capacity) {
                    var next: ?[*]u8 = null;
                    var next_capacity: usize = 0;
                    if (output.reserve(self.context, self.used, &next, &next_capacity) != 0 or next == null or next_capacity == 0) {
                        self.failure = Code.callback;
                        return;
                    }
                    self.buffer = next;
                    self.capacity = next_capacity;
                    self.used = 0;
                }
                const count = @min(bytes.len - consumed, self.capacity - self.used);
                @memcpy(self.buffer.?[self.used..][0..count], bytes[consumed..][0..count]);
                self.used += count;
                consumed += count;
                self.length += @as(u64, @intCast(count));
            }
            return;
        }
        if (self.buffer) |buffer| {
            if (self.length < self.capacity) {
                const start: usize = @intCast(self.length);
                const count = @min(bytes.len, self.capacity - start);
                @memcpy(buffer[start..][0..count], bytes[0..count]);
            }
        }
        self.length = sum[0];
    }

    fn byte(self: *Writer, value: u8) void {
        self.raw(&.{value});
    }

    fn escaped(self: *Writer, value: u16) void {
        const hex = "0123456789ABCDEF";
        self.raw(&.{ '\\', 'u', hex[value >> 12], hex[(value >> 8) & 15], hex[(value >> 4) & 15], hex[value & 15] });
    }

    fn bmp(self: *Writer, value: u16) void {
        switch (value) {
            '"' => self.raw("\\\""),
            '\\' => self.raw("\\\\"),
            8 => self.raw("\\b"),
            9 => self.raw("\\t"),
            10 => self.raw("\\n"),
            12 => self.raw("\\f"),
            13 => self.raw("\\r"),
            else => {
                if (!allowed_bmp.allowed(value)) {
                    self.escaped(value);
                } else if (value < 0x80) {
                    self.byte(@intCast(value));
                } else if (value < 0x800) {
                    self.raw(&.{ @as(u8, @intCast(value >> 6)) | 0xC0, @as(u8, @intCast(value & 63)) | 0x80 });
                } else {
                    self.raw(&.{ @as(u8, @intCast(value >> 12)) | 0xE0, @as(u8, @intCast((value >> 6) & 63)) | 0x80, @as(u8, @intCast(value & 63)) | 0x80 });
                }
            },
        }
    }

    pub fn integer(self: *Writer, value: i64) void {
        var digits: [20]u8 = undefined;
        var remaining: u64 = if (value < 0) @as(u64, @intCast(-(value + 1))) + 1 else @intCast(value);
        var start: usize = digits.len;
        while (true) {
            start -= 1;
            digits[start] = @as(u8, @intCast(remaining % 10)) + '0';
            remaining /= 10;
            if (remaining == 0) break;
        }
        if (value < 0) self.byte('-');
        self.raw(digits[start..]);
    }

    fn fixed(self: *Writer, value: u32, comptime width: usize) void {
        var digits: [width]u8 = undefined;
        var remaining = value;
        var index: usize = width;
        while (index > 0) {
            index -= 1;
            digits[index] = @as(u8, @intCast(remaining % 10)) + '0';
            remaining /= 10;
        }
        self.raw(&digits);
    }
};

/// A short final buffer is not a callback failure: all input must still be
/// visited so encode() can report the complete required length.
pub fn writeString(writer: ?*Writer, text: ?[*]const u16, length: usize) i32 {
    const output = writer orelse return Code.invalid;
    const input = text orelse {
        output.raw("null");
        return output.failure;
    };
    output.byte('"');
    var index: usize = 0;
    while (index < length) : (index += 1) {
        const value = input[index];
        if (value >= 0xD800 and value <= 0xDBFF) {
            if (length - index > 1 and input[index + 1] >= 0xDC00 and input[index + 1] <= 0xDFFF) {
                output.escaped(value);
                index += 1;
                output.escaped(input[index]);
            } else {
                // .NET escapes replacement for malformed UTF-16 even though
                // a literal U+FFFD in a well-formed string is allowed as UTF-8.
                output.escaped(0xFFFD);
            }
        } else if (value >= 0xDC00 and value <= 0xDFFF) {
            output.escaped(0xFFFD);
        } else {
            output.bmp(value);
        }
    }
    output.byte('"');
    return output.failure;
}

pub fn writeDateTime(writer: ?*Writer, date: ?*const DateParts) i32 {
    const output = writer orelse return Code.invalid;
    const value = date orelse return Code.invalid;
    if (!validDate(value.*)) {
        output.failure = Code.invalid;
        return Code.invalid;
    }
    output.byte('"');
    output.fixed(@intCast(value.year), 4);
    output.byte('-');
    output.fixed(@intCast(value.month), 2);
    output.byte('-');
    output.fixed(@intCast(value.day), 2);
    output.byte('T');
    output.fixed(@intCast(value.hour), 2);
    output.byte(':');
    output.fixed(@intCast(value.minute), 2);
    output.byte(':');
    output.fixed(@intCast(value.second), 2);
    if (value.fraction != 0) {
        output.byte('.');
        var fraction: u32 = @intCast(value.fraction);
        var digits: [7]u8 = undefined;
        var index: usize = digits.len;
        while (index > 0) {
            index -= 1;
            digits[index] = @as(u8, @intCast(fraction % 10)) + '0';
            fraction /= 10;
        }
        var end: usize = digits.len;
        while (digits[end - 1] == '0') end -= 1;
        output.raw(digits[0..end]);
    }
    output.byte(if (value.offset_minutes < 0) '-' else '+');
    const offset: u32 = @intCast(if (value.offset_minutes < 0) -value.offset_minutes else value.offset_minutes);
    output.fixed(offset / 60, 2);
    output.byte(':');
    output.fixed(offset % 60, 2);
    output.byte('"');
    return output.failure;
}

fn validDate(date: DateParts) bool {
    if (date.year < 1 or date.year > 9999 or date.month < 1 or date.month > 12 or
        date.hour < 0 or date.hour > 23 or date.minute < 0 or date.minute > 59 or
        date.second < 0 or date.second > 59 or date.fraction < 0 or date.fraction > 9999999 or
        date.offset_minutes < -14 * 60 or date.offset_minutes > 14 * 60) return false;
    const month_days = [_]i32{ 31, 28, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31 };
    const leap = @mod(date.year, 4) == 0 and (@mod(date.year, 100) != 0 or @mod(date.year, 400) == 0);
    const days = month_days[@intCast(date.month - 1)] + @as(i32, if (date.month == 2 and leap) 1 else 0);
    return date.day >= 1 and date.day <= days;
}

pub fn writeU64(writer: ?*Writer, value: u64) i32 {
    const w = writer orelse return Code.invalid;
    var buffer: [20]u8 = undefined;
    w.raw(std.fmt.bufPrint(&buffer, "{d}", .{value}) catch return Code.invalid);
    return w.failure;
}

pub fn writeF64(writer: ?*Writer, value: f64) i32 {
    const w = writer orelse return Code.invalid;
    if (!std.math.isFinite(value)) return Code.invalid;
    var buffer: [std.fmt.float.min_buffer_size]u8 = undefined;
    const scientific = std.fmt.float.render(&buffer, value, .{ .mode = .scientific }) catch return Code.invalid;
    const separator = std.mem.indexOfScalar(u8, scientific, 'e') orelse return Code.invalid;
    const exponent = std.fmt.parseInt(i32, scientific[separator + 1 ..], 10) catch return Code.invalid;
    if (exponent >= -4 and exponent < 17) {
        w.raw(std.fmt.float.render(&buffer, value, .{ .mode = .decimal }) catch return Code.invalid);
    } else {
        w.raw(scientific[0..separator]);
        w.raw(if (exponent < 0) "E-" else "E+");
        const magnitude: u32 = @intCast(if (exponent < 0) -exponent else exponent);
        if (magnitude < 10) w.raw("0");
        _ = writeU64(w, magnitude);
    }
    return w.failure;
}
