//! MCP 2026-07-28 JSON-RPC boundary. All borrowed message fields live until
//! Parsed.deinit(); the transport removes the line terminator before parse().
//! https://modelcontextprotocol.io/specification/2026-07-28/basic/versioning
const std = @import("std");
pub const Value = std.json.Value;
pub const protocol_version = "2026-07-28";
pub const jsonrpc_version = "2.0";
pub const complete_result_type = "complete";
pub const supported_versions = [_][]const u8{protocol_version};
pub const meta_protocol_version = "io.modelcontextprotocol/protocolVersion";
pub const meta_client_info = "io.modelcontextprotocol/clientInfo";
pub const meta_client_capabilities = "io.modelcontextprotocol/clientCapabilities";
pub const meta_server_info = "io.modelcontextprotocol/serverInfo";

/// No defaults: the owning adapter supplies its configured resource budget.
pub const Limits = struct {
    max_line_bytes: usize,
    max_value_bytes: usize,
    max_depth: usize,
    max_tokens: usize,
};
pub const Metadata = struct {
    protocol_version: []const u8,
    /// Self-reported software identity; never an authenticated Windows identity.
    client_info: ?Value,
    client_capabilities: Value,
};
pub const Request = struct { id: Value, method: []const u8, params: Value, metadata: Metadata };
pub const Notification = struct { method: []const u8, params: Value };
pub const FaultKind = enum {
    parse_error,
    invalid_request,
    invalid_params,
    method_not_found,
    internal_error,
    unsupported_protocol_version,
    missing_required_client_capability,

    pub fn code(self: FaultKind) i32 {
        return switch (self) {
            .parse_error => -32700,
            .invalid_request => -32600,
            .invalid_params => -32602,
            .method_not_found => -32601,
            .internal_error => -32603,
            .unsupported_protocol_version => -32022,
            .missing_required_client_capability => -32021,
        };
    }
    pub fn message(self: FaultKind) []const u8 {
        return switch (self) {
            .parse_error => "Parse error",
            .invalid_request => "Invalid request",
            .invalid_params => "Invalid params",
            .method_not_found => "Method not found",
            .internal_error => "Internal error",
            .unsupported_protocol_version => "Unsupported protocol version",
            .missing_required_client_capability => "Missing required client capability",
        };
    }
};
pub const Fault = struct {
    kind: FaultKind,
    id: ?Value = null,
    /// Malformed notifications are discarded, never answered.
    respond: bool = true,
    requested_version: ?[]const u8 = null,
    required_capabilities: ?Value = null,
    /// Application data for standard JSON-RPC errors. Official typed MCP error
    /// data takes precedence for unsupported versions and required capabilities.
    data: ?Value = null,
};
pub const Message = union(enum) { request: Request, notification: Notification, fault: Fault };
pub const Parsed = struct {
    json: ?std.json.Parsed(Value),
    message: Message,

    pub fn deinit(self: *Parsed) void {
        if (self.json) |json| json.deinit();
        self.* = undefined;
    }
};

pub fn objectField(value: Value, key: []const u8) ?Value {
    return if (value == .object) value.object.get(key) else null;
}
fn stringField(value: Value, key: []const u8) ?[]const u8 {
    const child = objectField(value, key) orelse return null;
    return if (child == .string) child.string else null;
}

const Exponent = struct { negative: bool, digits: []const u8 };
/// Value = significant digits * 10^(exponent + offset). The significant slice
/// may contain its original decimal point, which digit comparison skips.
const NumberId = struct { negative: bool, significant: []const u8, exponent: Exponent, offset: i128 };

fn digit(byte: u8) bool {
    return byte >= '0' and byte <= '9';
}

fn numberId(text: []const u8) ?NumberId {
    if (text.len == 0) return null;
    var i: usize = if (text[0] == '-') 1 else 0;
    const negative = i != 0;
    const start = i;
    if (i == text.len or !digit(text[i])) return null;
    if (text[i] == '0') {
        i += 1;
        if (i < text.len and digit(text[i])) return null;
    } else {
        while (i < text.len and digit(text[i])) : (i += 1) {}
    }
    var fractional: usize = 0;
    if (i < text.len and text[i] == '.') {
        i += 1;
        const fraction_start = i;
        while (i < text.len and digit(text[i])) : (i += 1) {}
        fractional = i - fraction_start;
        if (fractional == 0) return null;
    }
    const mantissa_end = i;
    var exponent = Exponent{ .negative = false, .digits = "0" };
    if (i < text.len and (text[i] == 'e' or text[i] == 'E')) {
        i += 1;
        var exponent_negative = false;
        if (i < text.len and (text[i] == '+' or text[i] == '-')) {
            exponent_negative = text[i] == '-';
            i += 1;
        }
        const exponent_start = i;
        while (i < text.len and digit(text[i])) : (i += 1) {}
        if (exponent_start == i) return null;
        var significant_start = exponent_start;
        while (significant_start + 1 < i and text[significant_start] == '0') : (significant_start += 1) {}
        const digits = text[significant_start..i];
        exponent = .{ .negative = exponent_negative and !std.mem.eql(u8, digits, "0"), .digits = digits };
    }
    if (i != text.len) return null;
    var first = start;
    while (first < mantissa_end and (text[first] == '0' or text[first] == '.')) : (first += 1) {}
    if (first == mantissa_end) return .{ .negative = false, .significant = "", .exponent = exponent, .offset = 0 };
    var last = mantissa_end;
    var trailing: usize = 0;
    while (last > first and (text[last - 1] == '0' or text[last - 1] == '.')) {
        last -= 1;
        if (text[last] == '0') trailing += 1;
    }
    return .{
        .negative = negative,
        .significant = text[first..last],
        .exponent = exponent,
        .offset = @as(i128, @intCast(trailing)) - @as(i128, @intCast(fractional)),
    };
}

fn smallExponent(exponent: Exponent) ?i128 {
    const magnitude = std.fmt.parseInt(i128, exponent.digits, 10) catch return null;
    return if (exponent.negative) -magnitude else magnitude;
}

fn integral(number: NumberId) bool {
    if (number.significant.len == 0) return true;
    const exponent = smallExponent(number.exponent) orelse return !number.exponent.negative;
    const adjusted = std.math.add(i128, exponent, number.offset) catch return number.offset >= 0;
    return adjusted >= 0;
}

/// JSON Schema integer is a numeric value with zero fractional part, including
/// integral decimal/exponent notation. Never convert to float or expand powers.
pub fn validId(id: Value) bool {
    return switch (id) {
        .string, .integer => true,
        .number_string => |text| integral(numberId(text) orelse return false),
        else => false,
    };
}

fn magnitudeOrder(a: []const u8, b: []const u8) std.math.Order {
    if (a.len < b.len) return .lt;
    if (a.len > b.len) return .gt;
    return std.mem.order(u8, a, b);
}

/// Check a-b == delta by decimal subtraction. Work is proportional to the
/// exponent's spelling, never its numerical magnitude; storage is constant.
fn exponentDifference(a: Exponent, b: Exponent, delta: i128) bool {
    if (a.negative != b.negative) {
        const left = smallExponent(a) orelse return false;
        const right = smallExponent(b) orelse return false;
        return (std.math.sub(i128, left, right) catch return false) == delta;
    }
    const order = magnitudeOrder(a.digits, b.digits);
    if (order == .eq) return delta == 0;
    const negative = (order == .lt) != a.negative;
    if (negative != (delta < 0)) return false;
    const larger = if (order == .gt) a.digits else b.digits;
    const smaller = if (order == .gt) b.digits else a.digits;
    var expected: u128 = @intCast(if (delta < 0) -delta else delta);
    var borrow: i16 = 0;
    var i: usize = 0;
    while (i < larger.len) : (i += 1) {
        const left: i16 = larger[larger.len - i - 1] - '0';
        const right: i16 = if (i < smaller.len) smaller[smaller.len - i - 1] - '0' else 0;
        var difference = left - right - borrow;
        borrow = if (difference < 0) 1 else 0;
        if (difference < 0) difference += 10;
        if (@as(u128, @intCast(difference)) != expected % 10) return false;
        expected /= 10;
    }
    return expected == 0 and borrow == 0;
}

fn significantEqual(a: []const u8, b: []const u8) bool {
    var i: usize = 0;
    var j: usize = 0;
    while (i < a.len and j < b.len) {
        if (a[i] == '.') {
            i += 1;
            continue;
        }
        if (b[j] == '.') {
            j += 1;
            continue;
        }
        if (a[i] != b[j]) return false;
        i += 1;
        j += 1;
    }
    return i == a.len and j == b.len;
}

fn numericText(value: Value, buffer: []u8) ?[]const u8 {
    return switch (value) {
        .integer => |number| std.fmt.bufPrint(buffer, "{d}", .{number}) catch unreachable,
        .number_string => |text| text,
        else => null,
    };
}

/// Exact numerical equality, including alternate integral spellings and signed
/// zero. String IDs remain distinct; original numeric lexemes stay untouched.
pub fn idEqual(a: Value, b: Value) bool {
    if (!validId(a) or !validId(b)) return false;
    if (a == .string) return b == .string and std.mem.eql(u8, a.string, b.string);
    // An i64 decimal representation always fits within its binary width + sign.
    var a_buffer: [@bitSizeOf(i64) + 1]u8 = undefined;
    var b_buffer: [@bitSizeOf(i64) + 1]u8 = undefined;
    const left = numberId(numericText(a, &a_buffer) orelse return false).?;
    const right = numberId(numericText(b, &b_buffer) orelse return false).?;
    if (left.significant.len == 0 or right.significant.len == 0) return left.significant.len == right.significant.len;
    return left.negative == right.negative and significantEqual(left.significant, right.significant) and
        exponentDifference(left.exponent, right.exponent, right.offset - left.offset);
}

/// Scanner preflight bounds nesting and token work before building the tree.
fn preflight(allocator: std.mem.Allocator, line: []const u8, limits: Limits) !void {
    if (line.len > limits.max_line_bytes) return error.InputLimitExceeded;
    if (!std.unicode.utf8ValidateSlice(line)) return error.InvalidUtf8;
    if (std.mem.indexOfAny(u8, line, "\r\n") != null) return error.EmbeddedNewline;
    var scanner = std.json.Scanner.initCompleteInput(allocator, line);
    defer scanner.deinit();
    var depth: usize = 0;
    var tokens: usize = 0;
    while (true) {
        const token = try scanner.next();
        if (token == .end_of_document) break;
        if (tokens == limits.max_tokens) return error.InputLimitExceeded;
        tokens += 1;
        switch (token) {
            .object_begin, .array_begin => {
                if (depth == limits.max_depth) return error.InputLimitExceeded;
                depth += 1;
            },
            .object_end, .array_end => depth -= 1,
            else => {},
        }
    }
}

/// Syntax/shape/protocol errors are Fault values; only allocation failure throws.
pub fn parse(allocator: std.mem.Allocator, line: []const u8, limits: Limits) error{OutOfMemory}!Parsed {
    preflight(allocator, line, limits) catch |err| {
        if (err == error.OutOfMemory) return error.OutOfMemory;
        return .{ .json = null, .message = .{ .fault = .{ .kind = .parse_error } } };
    };
    const json = std.json.parseFromSlice(Value, allocator, line, .{
        .allocate = .alloc_always,
        .parse_numbers = false,
        .max_value_len = limits.max_value_bytes,
        .duplicate_field_behavior = .@"error",
    }) catch |err| {
        if (err == error.OutOfMemory) return error.OutOfMemory;
        return .{ .json = null, .message = .{ .fault = .{ .kind = .parse_error } } };
    };
    return .{ .json = json, .message = validate(json.value) };
}

fn validate(root: Value) Message {
    const raw_id = objectField(root, "id");
    const id: ?Value = if (raw_id) |value| (if (validId(value)) value else null) else null;
    var fault = Fault{
        .kind = .invalid_request,
        .id = id,
        .respond = !(raw_id == null and stringField(root, "method") != null),
    };
    if (root != .object) return .{ .fault = fault };
    const version = stringField(root, "jsonrpc") orelse return .{ .fault = fault };
    if (!std.mem.eql(u8, version, jsonrpc_version)) return .{ .fault = fault };
    const method = stringField(root, "method") orelse return .{ .fault = fault };
    if (objectField(root, "result") != null or objectField(root, "error") != null) return .{ .fault = fault };
    if (raw_id != null and id == null) return .{ .fault = fault };
    fault.respond = raw_id != null;
    const params = objectField(root, "params") orelse if (raw_id == null) Value{ .object = .empty } else {
        fault.kind = .invalid_params;
        return .{ .fault = fault };
    };
    if (params != .object) {
        fault.kind = .invalid_params;
        return .{ .fault = fault };
    }
    if (raw_id == null) {
        if (std.mem.eql(u8, method, "notifications/cancelled")) {
            const request_id = objectField(params, "requestId") orelse return .{ .fault = fault };
            if (!validId(request_id)) return .{ .fault = fault };
            if (objectField(params, "reason")) |reason| {
                if (reason != .string) return .{ .fault = fault };
            }
        }
        return .{ .notification = .{ .method = method, .params = params } };
    }
    fault.kind = .invalid_params;
    const meta = objectField(params, "_meta") orelse return .{ .fault = fault };
    const requested_version = stringField(meta, meta_protocol_version) orelse return .{ .fault = fault };
    const capabilities = objectField(meta, meta_client_capabilities) orelse return .{ .fault = fault };
    if (capabilities != .object) return .{ .fault = fault };
    const client_info = objectField(meta, meta_client_info);
    if (client_info) |info| {
        if (stringField(info, "name") == null or stringField(info, "version") == null) return .{ .fault = fault };
    }
    if (!std.mem.eql(u8, requested_version, protocol_version)) {
        fault.kind = .unsupported_protocol_version;
        fault.requested_version = requested_version;
        return .{ .fault = fault };
    }
    return .{ .request = .{ .id = id.?, .method = method, .params = params, .metadata = .{
        .protocol_version = requested_version,
        .client_info = client_info,
        .client_capabilities = capabilities,
    } } };
}

/// Serializers return compact JSON without a transport newline. The caller owns
/// the returned allocation. A fixed writer prevents allocations beyond the cap.
pub fn stringifyBounded(allocator: std.mem.Allocator, value: anytype, max_output_bytes: usize) ![]u8 {
    const buffer = try allocator.alloc(u8, max_output_bytes);
    errdefer allocator.free(buffer);
    var writer = std.Io.Writer.fixed(buffer);
    std.json.Stringify.value(value, .{}, &writer) catch return error.OutputLimitExceeded;
    return try allocator.realloc(buffer, writer.end);
}

pub fn serializeResult(allocator: std.mem.Allocator, id: Value, result: Value, max_output_bytes: usize) ![]u8 {
    if (!validId(id)) return error.InvalidId;
    const result_type = stringField(result, "resultType") orelse return error.InvalidResult;
    if (!std.mem.eql(u8, result_type, complete_result_type)) return error.InvalidResult;
    return stringifyBounded(allocator, .{ .jsonrpc = jsonrpc_version, .id = id, .result = result }, max_output_bytes);
}

pub fn serializeFault(allocator: std.mem.Allocator, fault: Fault, max_output_bytes: usize) ![]u8 {
    if (!fault.respond) return error.NotificationHasNoResponse;
    if (fault.id) |id| if (!validId(id)) return error.InvalidId;
    if (fault.kind == .unsupported_protocol_version) {
        return faultResponse(allocator, fault.id, .{
            .code = fault.kind.code(),
            .message = fault.kind.message(),
            .data = .{
                .requested = fault.requested_version orelse return error.InvalidFault,
                .supported = &supported_versions,
            },
        }, max_output_bytes);
    }
    if (fault.kind == .missing_required_client_capability) {
        const required = fault.required_capabilities orelse return error.InvalidFault;
        if (required != .object) return error.InvalidFault;
        return faultResponse(allocator, fault.id, .{
            .code = fault.kind.code(),
            .message = fault.kind.message(),
            .data = .{ .requiredCapabilities = required },
        }, max_output_bytes);
    }
    if (fault.data) |data| return faultResponse(allocator, fault.id, .{
        .code = fault.kind.code(),
        .message = fault.kind.message(),
        .data = data,
    }, max_output_bytes);
    return faultResponse(allocator, fault.id, .{
        .code = fault.kind.code(),
        .message = fault.kind.message(),
    }, max_output_bytes);
}

fn faultResponse(allocator: std.mem.Allocator, id: ?Value, fault_error: anytype, max_output_bytes: usize) ![]u8 {
    if (id) |known_id| return stringifyBounded(allocator, .{ .jsonrpc = jsonrpc_version, .id = known_id, .@"error" = fault_error }, max_output_bytes);
    // Modern JSONRPCErrorResponse.id is optional RequestId; null is not an ID.
    return stringifyBounded(allocator, .{ .jsonrpc = jsonrpc_version, .@"error" = fault_error }, max_output_bytes);
}
