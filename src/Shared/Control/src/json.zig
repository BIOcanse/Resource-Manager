//! Allocation-free JSON output. len always counts the complete JSON size even
//! when the caller supplied less space, allowing the caller to retry exactly.
const std = @import("std");

pub const Writer = struct {
    buffer: []u8,
    len: usize = 0,

    pub fn init(buffer: []u8) Writer {
        return .{ .buffer = buffer };
    }

    pub fn raw(self: *Writer, text: []const u8) void {
        if (self.len < self.buffer.len) {
            const copy_length = @min(text.len, self.buffer.len - self.len);
            @memcpy(self.buffer[self.len .. self.len + copy_length], text[0..copy_length]);
        }
        self.len += text.len;
    }

    pub fn string(self: *Writer, text: []const u8) void {
        const hex = "0123456789abcdef";
        self.raw("\"");
        // JSON leaves valid UTF-8 bytes unchanged. Its ASCII control bytes,
        // quotation marks and backslashes are the bytes requiring escaping.
        for (text) |byte| {
            switch (byte) {
                '"' => self.raw("\\\""),
                '\\' => self.raw("\\\\"),
                '\n' => self.raw("\\n"),
                '\r' => self.raw("\\r"),
                '\t' => self.raw("\\t"),
                0x08 => self.raw("\\b"),
                0x0c => self.raw("\\f"),
                else => if (byte < 0x20) {
                    const escaped = [_]u8{ '\\', 'u', '0', '0', hex[byte >> 4], hex[byte & 0x0f] };
                    self.raw(&escaped);
                } else {
                    self.raw(&.{byte});
                },
            }
        }
        self.raw("\"");
    }

    pub fn number(self: *Writer, value: anytype) void {
        const Value = @TypeOf(value);
        const size = switch (@typeInfo(Value)) {
            .int => |info| info.bits + 2,
            .comptime_int => @max(128, @bitSizeOf(std.math.IntFittingRange(value, value)) + 2),
            else => @compileError("JSON number expects an integer"),
        };
        var bytes: [size]u8 = undefined;
        const text = std.fmt.bufPrint(&bytes, "{d}", .{value}) catch unreachable;
        self.raw(text);
    }

    pub fn boolean(self: *Writer, value: bool) void {
        self.raw(if (value) "true" else "false");
    }
};

test "JSON writer escapes controls and preserves non-ASCII UTF-8" {
    var buffer: [256]u8 = undefined;
    var writer = Writer.init(&buffer);
    writer.string("quote\" slash\\\n\r\t\x08\x0c\x00\x01\x1f 中文🙂");
    try std.testing.expectEqualStrings("\"quote\\\" slash\\\\\\n\\r\\t\\b\\f\\u0000\\u0001\\u001f 中文🙂\"", buffer[0..writer.len]);
}

test "JSON writer reports full size with an undersized or empty buffer" {
    var buffer: [5]u8 = undefined;
    var writer = Writer.init(&buffer);
    writer.raw("{\"x\":");
    writer.string("long");
    writer.raw("}");
    try std.testing.expectEqual(@as(usize, 12), writer.len);
    try std.testing.expectEqualStrings("{\"x\":", &buffer);
    var empty = Writer.init(&.{});
    empty.string("\x00中文");
    try std.testing.expectEqual(@as(usize, 14), empty.len);
}

test "JSON writer formats Booleans and integer limits without allocation" {
    var buffer: [512]u8 = undefined;
    var writer = Writer.init(&buffer);
    writer.number(std.math.minInt(i128));
    writer.raw(",");
    writer.number(std.math.maxInt(u128));
    writer.raw(",");
    writer.number(@as(u0, 0));
    writer.raw(",");
    writer.number(-42);
    writer.raw(",");
    writer.boolean(true);
    writer.raw(",");
    writer.boolean(false);
    try std.testing.expectEqualStrings("-170141183460469231731687303715884105728,340282366920938463463374607431768211455,0,-42,true,false", buffer[0..writer.len]);
}
