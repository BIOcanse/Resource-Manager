//! A scalar continuation. Reborrow text only during fill; retain only offsets
//! and at most one encoded UTF-16 unit/pair across output waits.
const std = @import("std");
const output = @import("output.zig");
pub const Value = union(enum) { text: []const u16, number: f64, unsigned: u64, null_value };

pub const Cursor = struct {
    offset: usize = 0,
    pending: [std.fmt.float.min_buffer_size]u8 = undefined,
    used: usize = 0,
    sent: usize = 0,
    started: bool = false,
    finished: bool = false,

    pub fn fill(self: *Cursor, value: Value, bytes: []u8) !usize {
        var written: usize = 0;
        while (written < bytes.len) {
            if (self.sent < self.used) {
                const count = @min(bytes.len - written, self.used - self.sent);
                @memcpy(bytes[written..][0..count], self.pending[self.sent..][0..count]);
                self.sent += count;
                written += count;
                continue;
            }
            if (self.finished) break;
            self.sent = 0;
            self.used = 0;
            var writer: output.Writer = .{ .buffer = &self.pending, .capacity = self.pending.len };
            if (!self.started) {
                self.started = true;
                switch (value) {
                    .text => writer.raw("\""),
                    .number => |number| {
                        if (output.writeF64(&writer, number) != 0) return error.Invalid;
                        self.finished = true;
                    },
                    .unsigned => |number| {
                        if (output.writeU64(&writer, number) != 0) return error.Invalid;
                        self.finished = true;
                    },
                    .null_value => {
                        writer.raw("null");
                        self.finished = true;
                    },
                }
            } else switch (value) {
                .text => |text| {
                    if (self.offset > text.len) return error.Invalid;
                    if (self.offset == text.len) {
                        writer.raw("\"");
                        self.finished = true;
                    } else {
                        const high = text[self.offset];
                        const pair = high >= 0xD800 and high <= 0xDBFF and text.len - self.offset > 1 and
                            text[self.offset + 1] >= 0xDC00 and text[self.offset + 1] <= 0xDFFF;
                        const count: usize = if (pair) 2 else 1;
                        _ = output.writeString(&writer, text[self.offset..].ptr, count);
                        // Reuse the ONE original string encoder, removing only
                        // its per-unit quotes. Escapes may span any window.
                        const length: usize = @intCast(writer.length);
                        std.mem.copyForwards(u8, &self.pending, self.pending[1 .. length - 1]);
                        writer.length -= 2;
                        self.offset += count;
                    }
                },
                else => return error.Invalid,
            }
            if (writer.failure != 0 or writer.length > self.pending.len) return error.Invalid;
            self.used = @intCast(writer.length);
        }
        return written;
    }

    pub fn done(self: *const Cursor) bool {
        return self.finished and self.sent == self.used;
    }
};
