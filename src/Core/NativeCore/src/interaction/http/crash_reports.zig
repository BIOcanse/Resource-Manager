//! Stateless synchronous encoding of the original managed crash-report graph.
//! The context, strings and dates are borrowed only during their callbacks. The
//! only output storage is the caller's final buffer; null/zero counts its size.
const allowed_bmp = @import("http_json_allowed_bmp.zig");

pub const Operation = enum(u32) { list = 0, package = 1, issue_link = 2, acknowledge = 3, delete = 4 };
pub const ScalarField = enum(u32) {
    report_count = 0,
    attachment_limit = 1,
    dump_present = 2,
    dump_size = 3,
    fault_present = 4,
    package_present = 5,
    package_size = 6,
    package_exceeds = 7,
    acknowledged = 8,
    reported = 9,
    result_package_size = 10,
    result_package_exceeds = 11,
};
pub const TextField = enum(u32) {
    dump_folder = 0,
    id = 1,
    occurred_at = 2,
    program = 3,
    role = 4,
    version = 5,
    dump_status = 6,
    dump_path = 7,
    fault_module = 8,
    fault_offset = 9,
    fault_exception_code = 10,
    fault_managed_stack = 11,
    fault_description = 12,
    package_path = 13,
    package_created_at = 14,
    result_package_path = 15,
    result_package_created_at = 16,
    reason = 17,
    url = 18,
};
pub const Callbacks = extern struct {
    scalar: *const fn (?*anyopaque, usize, u32, *i64) callconv(.c) i32,
    text: *const fn (?*anyopaque, usize, u32, *Writer) callconv(.c) i32,
};
pub const OutputCallbacks = extern struct {
    reserve: *const fn (?*anyopaque, usize, *?[*]u8, *usize) callconv(.c) i32,
    commit: *const fn (?*anyopaque, usize) callconv(.c) i32,
};
pub const Response = extern struct { length: u64, status: u32, content_type: u32 };
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
    while (index < length) index += writeStringUnit(output, input[0..length], index);
    output.byte('"');
    return output.failure;
}

/// One UTF-16 scalar, using the original Web escaping rules. At most twelve
/// bytes are written (an escaped surrogate pair). Shared by both traversals.
pub fn writeStringUnit(output: *Writer, input: []const u16, index: usize) usize {
    const value = input[index];
    if (value >= 0xD800 and value <= 0xDBFF) {
        if (input.len - index > 1 and input[index + 1] >= 0xDC00 and input[index + 1] <= 0xDFFF) {
            output.escaped(value);
            output.escaped(input[index + 1]);
            return 2;
        }
        output.escaped(0xFFFD);
    } else if (value >= 0xDC00 and value <= 0xDFFF) {
        output.escaped(0xFFFD);
    } else output.bmp(value);
    return 1;
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

pub const ReadError = error{ Invalid, Callback };

// The single schema program serves the legacy synchronous ABI and the bounded
// view cursor. Frames point only at static schema, never at borrowed fields.
const Instruction = union(enum) {
    raw: []const u8,
    text: TextField,
    number: ScalarField,
    flag: ScalarField,
    optional: struct { field: ScalarField, body: []const Instruction },
    report,
    result_package,
    reports,
};
const package_schema = [_]Instruction{
    .{ .raw = "{\"path\":" },              .{ .text = .package_path },
    .{ .raw = ",\"sizeBytes\":" },         .{ .number = .package_size },
    .{ .raw = ",\"createdAt\":" },         .{ .text = .package_created_at },
    .{ .raw = ",\"exceedsIssueLimit\":" }, .{ .flag = .package_exceeds },
    .{ .raw = "}" },
};
const result_package_schema = [_]Instruction{
    .{ .raw = "{\"path\":" },              .{ .text = .result_package_path },
    .{ .raw = ",\"sizeBytes\":" },         .{ .number = .result_package_size },
    .{ .raw = ",\"createdAt\":" },         .{ .text = .result_package_created_at },
    .{ .raw = ",\"exceedsIssueLimit\":" }, .{ .flag = .result_package_exceeds },
    .{ .raw = "}" },
};
const dump_schema = [_]Instruction{
    .{ .raw = "{\"path\":" },      .{ .text = .dump_path },
    .{ .raw = ",\"sizeBytes\":" }, .{ .number = .dump_size },
    .{ .raw = "}" },
};
const fault_schema = [_]Instruction{
    .{ .raw = "{\"module\":" },        .{ .text = .fault_module },
    .{ .raw = ",\"offset\":" },        .{ .text = .fault_offset },
    .{ .raw = ",\"exceptionCode\":" }, .{ .text = .fault_exception_code },
    .{ .raw = ",\"managedStack\":" },  .{ .text = .fault_managed_stack },
    .{ .raw = ",\"description\":" },   .{ .text = .fault_description },
    .{ .raw = "}" },
};
const report_schema = [_]Instruction{
    .{ .raw = "{\"id\":" },           .{ .text = .id },
    .{ .raw = ",\"occurredAt\":" },   .{ .text = .occurred_at },
    .{ .raw = ",\"program\":" },      .{ .text = .program },
    .{ .raw = ",\"role\":" },         .{ .text = .role },
    .{ .raw = ",\"version\":" },      .{ .text = .version },
    .{ .raw = ",\"dumpStatus\":" },   .{ .text = .dump_status },
    .{ .raw = ",\"dump\":" },         .{ .optional = .{ .field = .dump_present, .body = &dump_schema } },
    .{ .raw = ",\"fault\":" },        .{ .optional = .{ .field = .fault_present, .body = &fault_schema } },
    .{ .raw = ",\"package\":" },      .{ .optional = .{ .field = .package_present, .body = &package_schema } },
    .{ .raw = ",\"acknowledged\":" }, .{ .flag = .acknowledged },
    .{ .raw = ",\"reported\":" },     .{ .flag = .reported },
    .{ .raw = "}" },
};
const list_schema = [_]Instruction{
    .{ .raw = "{\"reports\":[" }, .reports,                                      .{ .raw = "],\"dumpFolder\":" },
    .{ .text = .dump_folder },    .{ .raw = ",\"issueAttachmentLimitBytes\":" }, .{ .number = .attachment_limit },
    .{ .raw = "}" },
};
const package_result_schema = [_]Instruction{
    .{ .raw = "{\"report\":" }, .report,              .{ .raw = ",\"package\":" }, .result_package,
    .{ .raw = ",\"reason\":" }, .{ .text = .reason }, .{ .raw = "}" },
};
const issue_schema = [_]Instruction{
    .{ .raw = "{\"url\":" }, .{ .text = .url }, .{ .raw = ",\"report\":" }, .report, .{ .raw = "}" },
};
const ack_schema = [_]Instruction{ .{ .raw = "{\"report\":" }, .report, .{ .raw = "}" } };
const missing_schema = [_]Instruction{.{ .raw = unknown_report }};

pub const Piece = struct {
    index: usize,
    value: union(enum) { raw: []const u8, text: TextField, number: i64 },
};
pub const Traversal = struct {
    const Frame = struct { schema: []const Instruction, pc: usize = 0, index: usize = 0 };
    // Root -> report -> optional object is the complete, fixed schema depth.
    frames: [3]Frame = undefined,
    depth: usize = 1,
    report_count: ?usize = null,
    report_next: usize = 0,
    comma: bool = false,

    pub fn init(operation: Operation, status: u32) Traversal {
        var result: Traversal = .{};
        result.frames[0] = .{ .schema = if (status == 404) &missing_schema else if (status == 204) &.{} else switch (operation) {
            .list => &list_schema,
            .package => &package_result_schema,
            .issue_link => &issue_schema,
            .acknowledge => &ack_schema,
            .delete => &.{},
        } };
        return result;
    }
    fn scalar(context: ?*anyopaque, callbacks: ?*const Callbacks, index: usize, field: ScalarField) ReadError!i64 {
        const reader = callbacks orelse return error.Invalid;
        var value: i64 = 0;
        if (reader.scalar(context, index, @backingInt(field), &value) != 0) return error.Callback;
        return value;
    }
    fn boolean(context: ?*anyopaque, callbacks: ?*const Callbacks, index: usize, field: ScalarField) ReadError!bool {
        return switch (try scalar(context, callbacks, index, field)) {
            0 => false,
            1 => true,
            else => error.Invalid,
        };
    }
    fn push(self: *Traversal, schema: []const Instruction, index: usize) ReadError!void {
        if (self.depth == self.frames.len) return error.Invalid;
        self.frames[self.depth] = .{ .schema = schema, .index = index };
        self.depth += 1;
    }
    pub fn next(self: *Traversal, context: ?*anyopaque, callbacks: ?*const Callbacks) ReadError!?Piece {
        while (self.depth != 0) {
            const frame = &self.frames[self.depth - 1];
            if (frame.pc == frame.schema.len) {
                self.depth -= 1;
                continue;
            }
            const index = frame.index;
            const instruction = frame.schema[frame.pc];
            frame.pc += 1;
            switch (instruction) {
                .raw => |bytes| return .{ .index = index, .value = .{ .raw = bytes } },
                .text => |field| return .{ .index = index, .value = .{ .text = field } },
                .number => |field| return .{ .index = index, .value = .{ .number = try scalar(context, callbacks, index, field) } },
                .flag => |field| return .{ .index = index, .value = .{ .raw = if (try boolean(context, callbacks, index, field)) "true" else "false" } },
                .optional => |part| {
                    if (try boolean(context, callbacks, index, part.field)) try self.push(part.body, index) else return .{ .index = index, .value = .{ .raw = "null" } };
                },
                .report => try self.push(&report_schema, index),
                .result_package => try self.push(&result_package_schema, index),
                .reports => {
                    if (self.report_count == null) {
                        const count = try scalar(context, callbacks, 0, .report_count);
                        if (count < 0 or @as(u64, @intCast(count)) > @import("std").math.maxInt(usize)) return error.Invalid;
                        self.report_count = @intCast(count);
                    }
                    if (self.report_next < self.report_count.?) {
                        frame.pc -= 1;
                        if (self.report_next != 0 and !self.comma) {
                            self.comma = true;
                            return .{ .index = 0, .value = .{ .raw = "," } };
                        }
                        try self.push(&report_schema, self.report_next);
                        self.report_next += 1;
                        self.comma = false;
                    }
                },
            }
        }
        return null;
    }
};
pub const unknown_report = "{\"type\":\"https://tools.ietf.org/html/rfc9110#section-15.5.5\",\"title\":\"Unknown crash report\",\"status\":404}";

pub fn describe(operation: u32, found: u32, response: ?*Response) i32 {
    const result = response orelse return Code.invalid;
    result.* = .{ .length = 0, .status = 0, .content_type = 0 };
    if (operation > @backingInt(Operation.delete) or found > 1) return Code.invalid;
    const selected: Operation = @fromBackingInt(@intCast(operation));
    if (selected != .list and found == 0) {
        result.status = 404;
        result.content_type = 2;
    } else if (selected == .delete) {
        result.status = 204;
    } else {
        result.status = 200;
        result.content_type = 1;
    }
    return Code.success;
}

fn writeResponse(operation: u32, context: ?*anyopaque, callbacks: ?*const Callbacks, writer: *Writer, result: *Response) i32 {
    if (result.status == 404) {
        writer.raw(unknown_report);
    } else if (result.status == 200) {
        const reader = callbacks orelse return Code.invalid;
        const selected: Operation = @fromBackingInt(@intCast(operation));
        var traversal = Traversal.init(selected, result.status);
        writeTraversal(&traversal, context, reader, writer) catch |err| {
            result.length = writer.length;
            if (writer.failure != Code.success) return writer.failure;
            return switch (err) {
                error.Invalid => Code.invalid,
                error.Callback => Code.callback,
            };
        };
    }
    result.length = writer.length;
    if (writer.failure != Code.success) return writer.failure;
    return Code.success;
}

fn writeTraversal(traversal: *Traversal, context: ?*anyopaque, reader: *const Callbacks, writer: *Writer) ReadError!void {
    while (try traversal.next(context, reader)) |piece| {
        if (writer.failure != Code.success) return error.Callback;
        switch (piece.value) {
            .raw => |bytes| writer.raw(bytes),
            .number => |value| writer.integer(value),
            .text => |field| if (reader.text(context, piece.index, @backingInt(field), writer) != 0) {
                return error.Callback;
            },
        }
        if (writer.failure != Code.success) return error.Callback;
    }
}
pub fn encode(operation: u32, found: u32, context: ?*anyopaque, callbacks: ?*const Callbacks, buffer: ?[*]u8, capacity: usize, response: ?*Response) i32 {
    const metadata = describe(operation, found, response);
    if (metadata != Code.success) return metadata;
    const result = response.?;
    if (buffer == null and capacity != 0) return Code.invalid;
    var writer: Writer = .{ .buffer = buffer, .capacity = capacity };
    const code = writeResponse(operation, context, callbacks, &writer, result);
    if (code != Code.success) return code;
    if (buffer != null and writer.length > capacity) return Code.short_buffer;
    return Code.success;
}

/// Streams once into successively borrowed final PipeWriter segments. reserve
/// completes the preceding full segment; commit completes only the final one.
/// Neither callback nor a segment pointer is retained after this call returns.
pub fn encodeStream(operation: u32, found: u32, context: ?*anyopaque, callbacks: ?*const Callbacks, output_callbacks: ?*const OutputCallbacks, response: ?*Response) i32 {
    const metadata = describe(operation, found, response);
    if (metadata != Code.success) return metadata;
    const output = output_callbacks orelse return Code.invalid;
    var writer: Writer = .{ .buffer = null, .capacity = 0, .output_callbacks = output, .context = context };
    const code = writeResponse(operation, context, callbacks, &writer, response.?);
    if (code != Code.success) return code;
    if (output.commit(context, writer.used) != 0) return Code.callback;
    return Code.success;
}
