//! C22: complete-input Web JSON tokens. Borrow input for this call; use the
//! request's scoped arena for nesting and decoded values. The caller releases
//! that arena after its last synchronous use; returned strings/raw values may
//! borrow input. No pointers or allocator leases may cross an asynchronous wait.
//! No domain state, default limits, HTTP framing, or command invocation.
//! Unknown values are lexically skipped, not decoded: the original Web reader
//! permits lone surrogate escapes there, but rejects them in known strings.
const std = @import("std");

pub const Limits = struct {
    max_depth: usize,
    max_document_bytes: usize,
    max_value_bytes: usize,
};
pub const Error = error{ SyntaxError, DepthLimit, DocumentLimit, ValueTooLong, InvalidLimits, OutOfMemory };
const Frame = enum { object_first, object_key, object_colon, object_value, object_after, array_first, array_value, array_after };

pub const Scanner = struct {
    pub const NextError = Error || std.json.Scanner.NextError;
    pub const PeekError = Error;
    pub const AllocError = NextError;

    input: []const u8,
    limits: Limits,
    allocator: std.mem.Allocator,
    frames: std.ArrayList(Frame) = .empty,
    cursor: usize = 0,
    root_done: bool = false,
    string: ?std.json.Scanner = null,

    pub fn init(allocator: std.mem.Allocator, input: []const u8, limits: Limits) Error!Scanner {
        if (limits.max_depth == 0) return error.InvalidLimits;
        if (input.len > limits.max_document_bytes) return error.DocumentLimit;
        return .{ .input = input, .limits = limits, .allocator = allocator };
    }

    pub fn deinit(self: *Scanner) void {
        if (self.string) |*value| value.deinit();
        self.frames.deinit(self.allocator);
        self.* = undefined;
    }

    pub fn stackHeight(self: *const Scanner) usize {
        return self.frames.items.len;
    }

    fn whitespace(self: *Scanner) void {
        while (self.cursor < self.input.len) : (self.cursor += 1) {
            switch (self.input[self.cursor]) {
                ' ', '\t', '\r', '\n' => {},
                else => break,
            }
        }
    }

    fn take(self: *Scanner, byte: u8) bool {
        if (self.cursor == self.input.len or self.input[self.cursor] != byte) return false;
        self.cursor += 1;
        return true;
    }

    // Consume punctuation once. peek does not consume the following token.
    fn prepare(self: *Scanner) Error!void {
        self.whitespace();
        if (self.frames.items.len == 0) return;
        const frame = &self.frames.items[self.frames.items.len - 1];
        switch (frame.*) {
            .object_colon => {
                if (!self.take(':')) return error.SyntaxError;
                frame.* = .object_value;
            },
            .object_after => {
                if (self.take(',')) frame.* = .object_key else if (self.cursor == self.input.len or self.input[self.cursor] != '}') return error.SyntaxError;
            },
            .array_after => {
                if (self.take(',')) frame.* = .array_value else if (self.cursor == self.input.len or self.input[self.cursor] != ']') return error.SyntaxError;
            },
            else => {},
        }
        self.whitespace();
    }

    pub fn peekNextTokenType(self: *Scanner) PeekError!std.json.TokenType {
        if (self.string != null) return .string;
        try self.prepare();
        if (self.frames.items.len == 0 and self.root_done) {
            if (self.cursor != self.input.len) return error.SyntaxError;
            return .end_of_document;
        }
        if (self.cursor == self.input.len) return error.SyntaxError;
        const byte = self.input[self.cursor];
        if (self.frames.items.len != 0) {
            switch (self.frames.items[self.frames.items.len - 1]) {
                .object_first => if (byte == '}') return .object_end else if (byte != '"') return error.SyntaxError,
                .object_key => if (byte != '"') return error.SyntaxError,
                .object_after => return if (byte == '}') .object_end else error.SyntaxError,
                .array_first => if (byte == ']') return .array_end,
                .array_after => return if (byte == ']') .array_end else error.SyntaxError,
                else => {},
            }
        }
        return switch (byte) {
            '{' => .object_begin,
            '[' => .array_begin,
            '"' => .string,
            't' => .true,
            'f' => .false,
            'n' => .null,
            '-', '0'...'9' => .number,
            else => error.SyntaxError,
        };
    }

    fn acceptedValue(self: *Scanner) void {
        if (self.frames.items.len == 0) {
            self.root_done = true;
        } else {
            const frame = &self.frames.items[self.frames.items.len - 1];
            frame.* = switch (frame.*) {
                .object_first, .object_key => .object_colon,
                .object_value => .object_after,
                .array_first, .array_value => .array_after,
                else => unreachable,
            };
        }
    }

    fn digit(byte: u8) bool {
        return byte >= '0' and byte <= '9';
    }

    fn number(self: *Scanner) Error![]const u8 {
        const begin = self.cursor;
        _ = self.take('-');
        if (!self.take('0')) {
            if (self.cursor == self.input.len or self.input[self.cursor] < '1' or self.input[self.cursor] > '9') return error.SyntaxError;
            while (self.cursor < self.input.len and digit(self.input[self.cursor])) self.cursor += 1;
        }
        if (self.take('.')) {
            const start = self.cursor;
            while (self.cursor < self.input.len and digit(self.input[self.cursor])) self.cursor += 1;
            if (start == self.cursor) return error.SyntaxError;
        }
        if (self.take('e') or self.take('E')) {
            _ = self.take('+') or self.take('-');
            const start = self.cursor;
            while (self.cursor < self.input.len and digit(self.input[self.cursor])) self.cursor += 1;
            if (start == self.cursor) return error.SyntaxError;
        }
        return self.input[begin..self.cursor];
    }

    // Lexical validation only: never materialize an ignored string or number.
    fn rawString(self: *Scanner) Error![]const u8 {
        const begin = self.cursor;
        self.cursor += 1;
        while (self.cursor < self.input.len) {
            const byte = self.input[self.cursor];
            self.cursor += 1;
            if (byte == '"') return self.input[begin..self.cursor];
            if (byte < 0x20) return error.SyntaxError;
            if (byte != '\\') continue;
            if (self.cursor == self.input.len) return error.SyntaxError;
            const escape = self.input[self.cursor];
            self.cursor += 1;
            switch (escape) {
                '"', '\\', '/', 'b', 'f', 'n', 'r', 't' => {},
                'u' => {
                    var index: usize = 0;
                    while (index < 4) : (index += 1) {
                        if (self.cursor == self.input.len or !std.ascii.isHex(self.input[self.cursor])) return error.SyntaxError;
                        self.cursor += 1;
                    }
                },
                else => return error.SyntaxError,
            }
        }
        return error.SyntaxError;
    }

    fn rawNext(self: *Scanner) Error!std.json.Token {
        const token = try self.peekNextTokenType();
        switch (token) {
            .object_begin, .array_begin => {
                if (self.frames.items.len == self.limits.max_depth) return error.DepthLimit;
                self.acceptedValue();
                try self.frames.append(self.allocator, if (token == .object_begin) .object_first else .array_first);
                self.cursor += 1;
                return if (token == .object_begin) .object_begin else .array_begin;
            },
            .object_end, .array_end => {
                _ = self.frames.pop();
                self.cursor += 1;
                return if (token == .object_end) .object_end else .array_end;
            },
            .string => {
                const raw = try self.rawString();
                self.acceptedValue();
                return .{ .string = raw };
            },
            .number => {
                const raw = try self.number();
                self.acceptedValue();
                return .{ .number = raw };
            },
            .true, .false, .null => {
                const expected: []const u8 = switch (token) {
                    .true => "true",
                    .false => "false",
                    .null => "null",
                    else => unreachable,
                };
                if (!std.mem.startsWith(u8, self.input[self.cursor..], expected)) return error.SyntaxError;
                self.cursor += expected.len;
                self.acceptedValue();
                return switch (token) {
                    .true => .true,
                    .false => .false,
                    .null => .null,
                    else => unreachable,
                };
            },
            .end_of_document => return .end_of_document,
        }
    }

    /// Strict decoding for a schema-known string (including property names).
    /// Partial tokens borrow the original input or contain inline escape bytes.
    pub fn next(self: *Scanner) NextError!std.json.Token {
        if (self.string == null) {
            const token = try self.rawNext();
            if (token != .string) return token;
            self.string = std.json.Scanner.initCompleteInput(self.allocator, token.string);
        }
        const token = try self.string.?.next();
        if (token == .string) {
            self.string.?.deinit();
            self.string = null;
        }
        return token;
    }

    pub fn nextAllocMax(self: *Scanner, allocator: std.mem.Allocator, when: std.json.AllocWhen, maximum: usize) AllocError!std.json.Token {
        const kind = try self.peekNextTokenType();
        if (kind != .string and kind != .number) return self.next();
        const limit = @min(maximum, self.limits.max_value_bytes);
        var bytes: std.ArrayList(u8) = .empty;
        errdefer bytes.deinit(allocator);
        while (true) {
            const token = try self.next();
            const text: []const u8 = switch (token) {
                .string, .number, .partial_string, .partial_number => |value| value,
                .partial_string_escaped_1 => |*value| value,
                .partial_string_escaped_2 => |*value| value,
                .partial_string_escaped_3 => |*value| value,
                .partial_string_escaped_4 => |*value| value,
                else => return error.SyntaxError,
            };
            if (text.len > limit - bytes.items.len) return error.ValueTooLong;
            const final = token == .string or token == .number;
            if (final and bytes.items.len == 0 and when == .alloc_if_needed) return token;
            try bytes.appendSlice(allocator, text);
            if (final) {
                const owned = try bytes.toOwnedSlice(allocator);
                return if (kind == .string) .{ .allocated_string = owned } else .{ .allocated_number = owned };
            }
        }
    }

    /// Iterative bounded lexical skip, including unknown property names. Do not
    /// replace with next(): ignored surrogate escapes must not be decoded.
    pub fn skipValue(self: *Scanner) Error!void {
        if (self.string != null) return error.SyntaxError;
        const depth = self.stackHeight();
        const first = try self.rawNext();
        if (first == .object_end or first == .array_end or first == .end_of_document) return error.SyntaxError;
        while (self.stackHeight() > depth) _ = try self.rawNext();
    }

    /// Borrow one exact JSON value (for JsonElement-like domain inputs). The
    /// same lexical/depth rules apply as skipValue, including ignored escapes.
    /// The request owns the bytes; this does not extend their lifetime.
    pub fn rawValue(self: *Scanner) Error![]const u8 {
        try self.prepare();
        const begin = self.cursor;
        try self.skipValue();
        return self.input[begin..self.cursor];
    }
};

/// Domain schemas supply jsonParse (field types, nullability, required fields).
/// This deliberately forbids Zig's default struct/string coercions, which are
/// not the Web contract. Use fieldIndex for Web name matching and skipValue
/// for unknown values. Parsing must finish before invoking a command owner.
pub fn bind(comptime T: type, allocator: std.mem.Allocator, body: []const u8, limits: Limits) !T {
    var scanner = try Scanner.init(allocator, body, limits);
    defer scanner.deinit();
    const value = try T.jsonParse(allocator, &scanner, std.json.ParseOptions{
        .ignore_unknown_fields = true,
        .duplicate_field_behavior = .use_last,
        .allocate = .alloc_if_needed,
        .max_value_len = limits.max_value_bytes,
    });
    if (try scanner.next() != .end_of_document) return error.SyntaxError;
    return value;
}

/// Product schema names are ASCII. Escaped names are decoded before matching.
/// Call in wire order and assign on every occurrence: Web uses the last value,
/// but still validates earlier occurrences before allowing command acceptance.
pub fn fieldIndex(name: []const u8, fields: []const []const u8) ?usize {
    for (fields, 0..) |field, index| if (std.ascii.eqlIgnoreCase(name, field)) return index;
    return null;
}

pub const JsonString = struct {
    value: []const u8,
    pub fn jsonParse(allocator: std.mem.Allocator, source: anytype, options: std.json.ParseOptions) std.json.ParseError(@TypeOf(source.*))!JsonString {
        return switch (try source.nextAllocMax(allocator, .alloc_if_needed, options.max_value_len.?)) {
            .string, .allocated_string => |value| .{ .value = value },
            else => error.UnexpectedToken,
        };
    }
};

/// Web AllowReadingFromString for integer fields. Do not pass through a float:
/// precise IDs and Int64/UInt64 bounds must retain all bits. Schema chooses T.
pub fn readNumber(comptime T: type, allocator: std.mem.Allocator, source: anytype, options: std.json.ParseOptions) std.json.ParseError(@TypeOf(source.*))!T {
    const token = try source.nextAllocMax(allocator, .alloc_if_needed, options.max_value_len.?);
    defer switch (token) {
        .allocated_string, .allocated_number => |value| allocator.free(value),
        else => {},
    };
    const value = switch (token) {
        .string, .allocated_string, .number, .allocated_number => |value| value,
        else => return error.UnexpectedToken,
    };
    var index: usize = 0;
    if (value.len != 0 and (value[0] == '-' or value[0] == '+')) index += 1;
    if (index == value.len) return error.InvalidNumber;
    while (index < value.len) : (index += 1) if (value[index] < '0' or value[index] > '9') return error.InvalidNumber;
    return std.fmt.parseInt(T, value, 10);
}
