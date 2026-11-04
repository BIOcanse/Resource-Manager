//! Invoke.v1 framing and strict envelopes, independent of the OS transport.
//! All returned JSON strings/containers belong to Owned*. No input is borrowed.
const std = @import("std");
pub const contract = @import("rm_invocation_contract");
const Allocator = std.mem.Allocator;

pub const OwnedRequest = struct {
    value: contract.Request,
    parsed: std.json.Parsed(std.json.Value),
    pub fn deinit(self: *OwnedRequest) void {
        self.parsed.deinit();
        self.* = undefined;
    }
};
pub const OwnedResponse = struct {
    value: contract.Response,
    parsed: std.json.Parsed(std.json.Value),
    pub fn deinit(self: *OwnedResponse) void {
        self.parsed.deinit();
        self.* = undefined;
    }
};

fn parse(allocator: Allocator, bytes: []const u8) !std.json.Parsed(std.json.Value) {
    if (!std.unicode.utf8ValidateSlice(bytes)) return error.InvalidUtf8;
    return std.json.parseFromSlice(std.json.Value, allocator, bytes, .{
        .allocate = .alloc_always,
        .parse_numbers = false,
        .duplicate_field_behavior = .@"error",
        .ignore_unknown_fields = false,
    });
}

pub fn decodeRequest(allocator: Allocator, bytes: []const u8) !OwnedRequest {
    var parsed = try parse(allocator, bytes);
    errdefer parsed.deinit();
    if (parsed.value != .object) return error.InvalidEnvelope;
    const object = parsed.value.object;
    // Count + required names reject key, unknown and missing members alike.
    if (object.count() != 2) return error.InvalidEnvelope;
    const operation = object.get("operation") orelse return error.InvalidEnvelope;
    const payload = object.get("payload") orelse return error.InvalidEnvelope;
    if (operation != .string or !contract.operationValid(operation.string)) return error.InvalidOperation;
    return .{ .value = .{ .operation = operation.string, .payload = payload }, .parsed = parsed };
}

pub fn decodeResponse(allocator: Allocator, bytes: []const u8) !OwnedResponse {
    var parsed = try parse(allocator, bytes);
    errdefer parsed.deinit();
    if (parsed.value != .object) return error.InvalidEnvelope;
    const object = parsed.value.object;
    if (object.count() != 2) return error.InvalidEnvelope;
    const status = object.get("status") orelse return error.InvalidEnvelope;
    const payload = object.get("payload") orelse return error.InvalidEnvelope;
    if (status != .string) return error.InvalidStatus;
    return .{
        .value = .{ .status = contract.Status.parse(status.string) orelse return error.InvalidStatus, .payload = payload },
        .parsed = parsed,
    };
}

fn checkLength(max_bytes: u32, length: usize) !void {
    if (length == 0) return error.EmptyFrame;
    if (length > max_bytes or length > std.math.maxInt(u32)) return error.FrameTooLarge;
}

fn readAll(connection: contract.Connection, control: contract.Control, bytes: []u8) !void {
    var offset: usize = 0;
    while (offset < bytes.len) {
        try control.cancel.check();
        const count = connection.read(connection.context, bytes[offset..], control) catch |err| return transportError(err);
        if (count == 0) return error.EndOfStream;
        if (count > bytes.len - offset) return error.InvalidTransfer;
        offset += count;
    }
    try control.cancel.check();
}

fn writeAll(connection: contract.Connection, control: contract.Control, bytes: []const u8) !void {
    var offset: usize = 0;
    while (offset < bytes.len) {
        try control.cancel.check();
        const count = connection.write(connection.context, bytes[offset..], control) catch |err| return transportError(err);
        if (count == 0) return error.EndOfStream;
        if (count > bytes.len - offset) return error.InvalidTransfer;
        offset += count;
    }
    try control.cancel.check();
}

fn transportError(err: anyerror) anyerror {
    return switch (err) {
        error.Cancelled, error.DeadlineExceeded => err,
        else => error.TransportFailure,
    };
}

/// Transport callbacks enforce the supplied absolute deadline while blocked.
/// The loops preserve the same Control for every short transfer.
pub fn readFrame(allocator: Allocator, connection: contract.Connection, control: contract.Control, max_bytes: u32) ![]u8 {
    var header: [contract.header_bytes]u8 = undefined;
    try readAll(connection, control, &header);
    const length = std.mem.readInt(u32, &header, .little);
    try checkLength(max_bytes, length);
    const bytes = try allocator.alloc(u8, length);
    errdefer allocator.free(bytes);
    try readAll(connection, control, bytes);
    return bytes;
}

/// Writes already encoded JSON directly, without a second stringify pass.
pub fn writeFrame(connection: contract.Connection, control: contract.Control, max_bytes: u32, bytes: []const u8) !void {
    try checkLength(max_bytes, bytes.len);
    var header: [contract.header_bytes]u8 = undefined;
    std.mem.writeInt(u32, &header, @intCast(bytes.len), .little);
    try writeAll(connection, control, &header);
    try writeAll(connection, control, bytes);
}

pub fn readRequest(allocator: Allocator, connection: contract.Connection, control: contract.Control, max_bytes: u32) !OwnedRequest {
    const bytes = try readFrame(allocator, connection, control, max_bytes);
    defer allocator.free(bytes);
    return decodeRequest(allocator, bytes);
}

pub fn readResponse(allocator: Allocator, connection: contract.Connection, control: contract.Control, max_bytes: u32) !OwnedResponse {
    const bytes = try readFrame(allocator, connection, control, max_bytes);
    defer allocator.free(bytes);
    return decodeResponse(allocator, bytes);
}

// Encode borrowed Values with an explicit depth stack. This preserves exact
// number lexemes and string-valued GPU payloads, and bounds cyclic caller data
// by the caller's byte limit instead of overflowing the native stack.
fn writeValue(allocator: Allocator, max_bytes: u32, writer: *std.Io.Writer, value: std.json.Value) !void {
    const Frame = struct { value: std.json.Value, index: usize = 0, started: bool = false };
    var stack: std.ArrayList(Frame) = .empty;
    defer stack.deinit(allocator);
    try stack.append(allocator, .{ .value = value });
    var nodes: usize = 0;
    while (stack.items.len != 0) {
        const frame = &stack.items[stack.items.len - 1];
        if (!frame.started) {
            if (nodes >= max_bytes) return error.FrameTooLarge;
            nodes += 1;
            frame.started = true;
            switch (frame.value) {
                .array => try writer.writeByte('['),
                .object => try writer.writeByte('{'),
                .string => |text| {
                    if (!std.unicode.utf8ValidateSlice(text)) return error.InvalidUtf8;
                    try std.json.Stringify.value(text, .{}, writer);
                },
                .number_string => |number| {
                    var scanner = std.json.Scanner.initCompleteInput(allocator, number);
                    defer scanner.deinit();
                    const token = try scanner.next();
                    if (token != .number or !std.mem.eql(u8, token.number, number)) return error.InvalidNumber;
                    if (try scanner.next() != .end_of_document) return error.InvalidNumber;
                    try writer.writeAll(number);
                },
                .float => |number| {
                    if (!std.math.isFinite(number)) return error.InvalidNumber;
                    try std.json.Stringify.value(number, .{}, writer);
                },
                .integer => |number| try std.json.Stringify.value(number, .{}, writer),
                .bool => |boolean| try std.json.Stringify.value(boolean, .{}, writer),
                .null => try writer.writeAll("null"),
            }
        }
        switch (frame.value) {
            .array => |array| {
                if (frame.index < array.items.len) {
                    if (frame.index != 0) try writer.writeByte(',');
                    const child = array.items[frame.index];
                    frame.index += 1;
                    try stack.append(allocator, .{ .value = child });
                    continue;
                }
                try writer.writeByte(']');
            },
            .object => |object| {
                if (frame.index < object.count()) {
                    const key = object.keys()[frame.index];
                    if (!std.unicode.utf8ValidateSlice(key)) return error.InvalidUtf8;
                    if (frame.index != 0) try writer.writeByte(',');
                    try std.json.Stringify.value(key, .{}, writer);
                    try writer.writeByte(':');
                    const child = object.values()[frame.index];
                    frame.index += 1;
                    try stack.append(allocator, .{ .value = child });
                    continue;
                }
                try writer.writeByte('}');
            },
            else => {},
        }
        _ = stack.pop();
    }
}

fn encodeInto(allocator: Allocator, max_bytes: u32, writer: *std.Io.Writer, field: []const u8, text: []const u8, payload: std.json.Value) !void {
    try writer.writeByte('{');
    try std.json.Stringify.value(field, .{}, writer);
    try writer.writeByte(':');
    try std.json.Stringify.value(text, .{}, writer);
    try writer.writeAll(",\"payload\":");
    try writeValue(allocator, max_bytes, writer, payload);
    try writer.writeByte('}');
}

fn encode(allocator: Allocator, max_bytes: u32, field: []const u8, text: []const u8, payload: std.json.Value) ![]u8 {
    if (max_bytes == 0) return error.FrameTooLarge;
    const buffer = try allocator.alloc(u8, max_bytes);
    errdefer allocator.free(buffer);
    var writer = std.Io.Writer.fixed(buffer);
    encodeInto(allocator, max_bytes, &writer, field, text, payload) catch |err| return if (err == error.WriteFailed) error.FrameTooLarge else err;
    return allocator.realloc(buffer, writer.buffered().len);
}

/// Returned slices contain only JSON; writeFrame supplies the four-byte prefix.
pub fn encodeRequest(allocator: Allocator, max_bytes: u32, value: contract.Request) ![]u8 {
    if (!contract.operationValid(value.operation)) return error.InvalidOperation;
    return encode(allocator, max_bytes, "operation", value.operation, value.payload);
}

pub fn encodeResponse(allocator: Allocator, max_bytes: u32, value: contract.Response) ![]u8 {
    return encode(allocator, max_bytes, "status", value.status.text(), value.payload);
}

pub fn writeRequest(allocator: Allocator, connection: contract.Connection, control: contract.Control, max_bytes: u32, value: contract.Request) !void {
    const bytes = try encodeRequest(allocator, max_bytes, value);
    defer allocator.free(bytes);
    try writeFrame(connection, control, max_bytes, bytes);
}

pub fn writeResponse(allocator: Allocator, connection: contract.Connection, control: contract.Control, max_bytes: u32, value: contract.Response) !void {
    const bytes = try encodeResponse(allocator, max_bytes, value);
    defer allocator.free(bytes);
    try writeFrame(connection, control, max_bytes, bytes);
}
