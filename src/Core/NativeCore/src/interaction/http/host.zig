//! Public HTTP dispatch. No socket, DI, credentials, domain current or allocator.
//! A caller/scope is borrowed from the platform only during admission/invocation.
//! The transitional Kestrel entry and native connection use these same functions.
const std = @import("std");
pub const access = @import("../capabilities/access.zig");
pub const io = @import("io.zig");
const json = @import("crash_reports.zig");

pub const Header = @import("response.zig").Header;
pub const Request = struct {
    method: []const u8,
    target: []const u8,
    scheme: []const u8 = "http",
    headers: []const Header,
    body: []const u8,
    facts: io.Facts,
    caller: ?access.Caller,
    caller_scope: ?*const anyopaque,
    cancelled: bool = false,
    /// Set by admission; parameter access never performs a second lookup.
    route_template: []const u8 = "",
};
pub const Startup = struct { profile_id: []const u8, allowed: u32 };
/// A window, not a maximum legal response. A resumable codec may emit any
/// number of windows, advancing only after the preceding bytes are consumed.
pub const Capacity = struct {
    state_bytes: usize,
    output_bytes: usize,
    state_alignment: usize = @alignOf(usize),
};
/// post must enqueue on the connection's serialized dispatcher. It must never
/// invoke resume inline, and must remain valid until deinit has drained owner
/// work. A synchronous family needs no wake; pending without one is an error.
pub const Wake = struct {
    context: ?*anyopaque = null,
    /// Echoed by the dispatcher; prevents an old request's queued wake from
    /// resuming a later keep-alive request in the same connection slot.
    ticket: u64 = 0,
    post: ?*const fn (?*anyopaque, u64) void = null,
    pub fn notify(self: Wake) void {
        if (self.post) |post| post(self.context, self.ticket);
    }
};
pub const Call = struct {
    /// Exactly capacity.state_bytes are zeroed once before begin on both paths.
    state: []u8,
    wake: Wake,
    /// Live, request-local cancellation. Async owner work may read this atomic
    /// until it acknowledges cancellation; it must not retain Call afterwards.
    cancelled: std.atomic.Value(bool) = .init(false),
    pub fn isCancelled(self: *const Call) bool {
        return self.cancelled.load(.acquire);
    }
};
pub const Event = enum { ready, writable, cancel };
pub const Progress = union(enum) { pending, output: Result, done };
/// One original owner/source instance, supplied by composition. The context is
/// not caller_scope, a new domain current, or a DI container. begin binds typed
/// parameters/accepts work once. resume advances that same work or codec cursor.
/// Every entry is synchronous/nonblocking; all caller and source field borrows
/// end on return. Pending state contains owned control/cursor data only. release
/// runs once after terminal owner work AND final send/close have drained.
pub const Family = struct {
    context: ?*anyopaque = null,
    capacity: Capacity,
    /// Explicit false is for a strictly synchronous, single-window family.
    /// Default true requires a dispatcher before any owner work can start.
    can_suspend: bool = true,
    begin: *const fn (?*anyopaque, Request, *Call, []u8) anyerror!Progress,
    step: *const fn (?*anyopaque, *Call, Event, []u8) anyerror!Progress,
    release: *const fn (?*anyopaque, *Call) void,
};
pub const Handler = union(enum) {
    capabilities,
    echo,
    /// A family owns its typed binding/codec and may borrow the live caller.
    /// No HttpContext or managed endpoint is ever synthesized here.
    family: Family,
};
pub const Route = struct {
    method: []const u8,
    template: []const u8,
    required_capabilities: u32 = 0,
    requirement: access.Requirement,
    handler: Handler,
};
pub const Config = struct {
    startup: Startup,
    normal_actions_allowed: bool,
    test_routes: bool = false,
    family_routes: []const Route = &.{},
};
pub const Result = struct {
    status: u16,
    body: []const u8,
    // null explicitly omits Content-Type (for example a health204 response).
    content_type: ?[]const u8 = "application/json; charset=utf-8",
    /// Header views live until the transport synchronously copies the first
    /// response head. Body bytes live until writable, including partial sends.
    headers: []const Header = &.{},
    more: bool = false,
    /// For a multi-window response: null means chunked. Finite results use
    /// body.len. A family that knows its full size may declare a fixed length.
    total_length: ?u64 = null,
};
pub const Admission = struct {
    route: ?*const Route,
    rejection: ?Result,
    decision: access.Decision,
};
/// Stable, request-run-local control record. Neither this object nor state may
/// move while an owner can post a wake. The embedding run owns its workspace
/// lease locally through deinit, including cancellation acknowledgement.
pub const Operation = @import("operation.zig").Engine(@This());
pub fn startOperation(self: *Operation, config: Config, input: Request, admission: Admission, output: []u8) !Progress {
    if (self.phase != .fresh) return error.AlreadyStarted;
    if (input.cancelled) {
        self.call.cancelled.store(true, .release);
        self.phase = .done;
        return error.Cancelled;
    }
    if (admission.rejection) |rejection| return self.accept(.{ .output = rejection }, output);
    const route = admission.route orelse return self.accept(.{ .output = try failure(output, 404, "api-endpoint-not-found") }, output);
    if (admission.decision.disposition != .allowed and admission.decision.disposition != .check_selectors)
        return self.accept(.{ .output = try failure(output, 403, "access-denied") }, output);
    if (route.requirement != .anonymous and route.requirement != .per_selector and
        (input.caller == null or input.caller_scope == null))
        return self.accept(.{ .output = try failure(output, 403, "local-windows-caller-required") }, output);
    var request = input;
    request.route_template = route.template;
    const progress: Progress = switch (route.handler) {
        .capabilities => .{ .output = try capabilities(config, output) },
        .echo => .{ .output = echo(request, output) catch |err| try mapError(output, err) },
        .family => |binding| return self.startFamily(binding, request, output),
    };
    try validateWindow(progress, output);
    return self.accept(progress, output);
}

pub const WakeArg = Wake;
pub fn hasWake(wake: Wake) bool {
    return wake.post != null;
}
pub fn cancelOperation(self: *Operation, output: []u8) !Progress {
    return self.cancelAfterDrain(output);
}
pub fn familyFailure(self: *Operation, err: anyerror, output: []u8) anyerror!Progress {
    self.phase = .done;
    if (self.response_status != null or self.call.isCancelled() or err == error.Cancelled) return err;
    return self.accept(.{ .output = try mapError(output, err) }, output);
}

fn validateWindow(progress: Progress, output: []u8) !void {
    if (progress != .output or progress.output.body.len == 0) return;
    const body = progress.output.body;
    const start = @intFromPtr(output.ptr);
    const pointer = @intFromPtr(body.ptr);
    if (body.len > output.len or pointer < start or pointer - start > output.len - body.len)
        return error.ResponseBodyNotFinalOutput;
}
pub fn validateCapacity(capacity: Capacity, state: []u8, output: []u8) !void {
    if (capacity.output_bytes == 0 or capacity.state_alignment < @alignOf(usize) or
        !std.math.isPowerOfTwo(capacity.state_alignment)) return error.InvalidCapacity;
    if (state.len < capacity.state_bytes or output.len < capacity.output_bytes) return error.OutputLimit;
    if (state.len != 0 and @intFromPtr(state.ptr) % capacity.state_alignment != 0) return error.InvalidStateAlignment;
    if (state.len != 0 and output.len != 0) {
        const x = @intFromPtr(state.ptr);
        const y = @intFromPtr(output.ptr);
        if (if (x <= y) y - x < state.len else x - y < output.len) return error.OverlappingRegions;
    }
}
/// Identical validation on both transports, before committing response headers.
/// Framing belongs to H-send/Kestrel, never family metadata.
pub fn validateProgressResult(result: Result, output: []u8) !void {
    try validateWindow(.{ .output = result }, output);
    try validateResult(result);
}
// Existing H family DTOs repeat Content-Type on windows; the transport copies
// only the first head. Never retain a borrowed header view across a send wait.
pub fn continuationValid(_: Result) bool {
    return true;
}
pub fn validateResult(result: Result) !void {
    const send = @import("response.zig");
    if (result.status < 200 or result.status > 599) return error.InvalidResponse;
    if (result.content_type) |value| if (!send.validValue(value)) return error.InvalidResponse;
    for (result.headers) |item| {
        if (!send.validName(item.name) or !send.validValue(item.value) or
            std.ascii.eqlIgnoreCase(item.name, "content-length") or
            std.ascii.eqlIgnoreCase(item.name, "transfer-encoding") or
            std.ascii.eqlIgnoreCase(item.name, "connection") or
            std.ascii.eqlIgnoreCase(item.name, "trailer") or
            std.ascii.eqlIgnoreCase(item.name, "content-type")) return error.InvalidResponse;
    }
    if (result.more and (result.status == 204 or result.status == 304)) return error.InvalidResponse;
    if (!result.more and result.total_length != null and result.total_length.? != result.body.len) return error.InvalidResponse;
    if (result.total_length) |length| if (length < result.body.len) return error.InvalidResponse;
}
pub const product_routes = [_]Route{.{
    .method = "GET",
    .template = "/api/runtime/capabilities",
    .requirement = .anonymous,
    .handler = .capabilities,
}};
const test_routes = [_]Route{.{
    .method = "POST",
    .template = "/__test/http/echo",
    .required_capabilities = 1 << 3,
    .requirement = .normal_action,
    .handler = .echo,
}};

fn path(target: []const u8) []const u8 {
    var relative = target;
    if (absoluteSeparator(target)) |separator| {
        const tail = target[separator + 3 ..];
        const boundary = std.mem.indexOfAny(u8, tail, "/?#") orelse return "/";
        if (tail[boundary] != '/') return "/";
        relative = tail[boundary..];
    }
    const end = std.mem.indexOfScalar(u8, relative, '?') orelse relative.len;
    const value = relative[0..end];
    return if (value.len > 1 and value[value.len - 1] == '/') value[0 .. value.len - 1] else value;
}
fn absoluteSeparator(target: []const u8) ?usize {
    if (std.ascii.startsWithIgnoreCase(target, "http://")) return "http".len;
    if (std.ascii.startsWithIgnoreCase(target, "https://")) return "https".len;
    return null;
}
fn literalMatches(value: []const u8, template: []const u8) bool {
    // ASP.NET literal routes are case insensitive; HTTP method tokens are not.
    // HEAD is NOT implicitly a GET in the frozen RM Minimal API oracle.
    var at: usize = 0;
    var index: usize = 0;
    while (at < value.len and index < template.len) : (index += 1) {
        var byte = value[at];
        at += 1;
        if (byte == '%' and value.len - at >= 2) {
            const decoded = std.fmt.parseInt(u8, value[at..][0..2], 16) catch return false;
            // Kestrel keeps encoded separators escaped in Request.Path.
            if (decoded == '/' or decoded == '\\') return false;
            byte = decoded;
            at += 2;
        }
        if (std.ascii.toLower(byte) != std.ascii.toLower(template[index])) return false;
    }
    return at == value.len and index == template.len;
}
fn parameterName(segment: []const u8) ?[]const u8 {
    if (segment.len < 3 or segment[0] != '{' or segment[segment.len - 1] != '}') return null;
    const name = segment[1 .. segment.len - 1];
    // This seam supports one required parameter per whole path segment. A
    // family needing constraints/optional/catch-all routes must declare that
    // separate contract, never silently get a weaker match.
    for (name) |byte| if (!(std.ascii.isAlphanumeric(byte) or byte == '_')) return null;
    return name;
}
fn validEscapes(value: []const u8) bool {
    var index: usize = 0;
    while (index < value.len) : (index += 1) {
        if (value[index] != '%') continue;
        if (value.len - index < 3) return false;
        _ = std.fmt.parseInt(u8, value[index + 1 ..][0..2], 16) catch return false;
        index += 2;
    }
    return true;
}
fn matches(route: Route, method: []const u8, target: []const u8) bool {
    if (!std.mem.eql(u8, route.method, method)) return false;
    const value = path(target);
    if (std.mem.indexOfScalar(u8, route.template, '{') == null) return literalMatches(value, route.template);
    var values = std.mem.splitScalar(u8, value, '/');
    var templates = std.mem.splitScalar(u8, route.template, '/');
    while (templates.next()) |segment| {
        const actual = values.next() orelse return false;
        if (parameterName(segment) != null) {
            if (actual.len == 0 or !validEscapes(actual)) return false;
        } else {
            if (std.mem.indexOfAny(u8, segment, "{}") != null or !literalMatches(actual, segment)) return false;
        }
    }
    return values.next() == null;
}
/// Raw route value exactly as it appears in the target. In particular encoded
/// separators remain escaped, as in Kestrel Request.Path/route values.
pub fn parameterRaw(request: Request, name: []const u8) ?[]const u8 {
    var values = std.mem.splitScalar(u8, path(request.target), '/');
    var templates = std.mem.splitScalar(u8, request.route_template, '/');
    while (templates.next()) |segment| {
        const actual = values.next() orelse return null;
        if (parameterName(segment)) |key| if (std.ascii.eqlIgnoreCase(key, name)) return actual;
    }
    return null;
}
/// Decode a parameter into caller-owned scratch. %2F/%5C remain escaped; '+'
/// is a literal path byte. The family owns UTF8/type/business interpretation.
pub fn parameter(request: Request, name: []const u8, scratch: []u8) !?[]const u8 {
    const raw = parameterRaw(request, name) orelse return null;
    var source: usize = 0;
    var length: usize = 0;
    while (source < raw.len) {
        var byte = raw[source];
        var consumed: usize = 1;
        if (byte == '%') {
            if (raw.len - source < 3) return error.InvalidRouteParameter;
            const decoded = std.fmt.parseInt(u8, raw[source + 1 ..][0..2], 16) catch return error.InvalidRouteParameter;
            if (decoded != '/' and decoded != '\\') {
                byte = decoded;
                consumed = 3;
            }
        }
        if (length == scratch.len) return error.OutputLimit;
        scratch[length] = byte;
        length += 1;
        source += consumed;
    }
    return scratch[0..length];
}
pub fn ownsRoutes(routes: []const Route, method: []const u8, target: []const u8) bool {
    for (routes) |route| if (matches(route, method, target)) return true;
    return false;
}
/// Common precedence for independently supplied temporary family descriptors.
/// Zero is no match, literal2 outranks parameter1; equal ranks keep order.
pub fn matchRank(route: Route, method: []const u8, target: []const u8) u2 {
    if (!matches(route, method, target)) return 0;
    return if (std.mem.indexOfScalar(u8, route.template, '{') == null) 2 else 1;
}
pub fn owns(method: []const u8, target: []const u8) bool {
    return ownsRoutes(&product_routes, method, target);
}
pub fn lookup(config: Config, request: Request) ?*const Route {
    for (&product_routes) |*route| if (matches(route.*, request.method, request.target)) return route;
    // Literal routes win over parameter routes, independent of registration
    // order. Parameter route ties retain their explicit registration order.
    var best: ?*const Route = null;
    for (config.family_routes) |*route| {
        if ((config.startup.allowed & route.required_capabilities) == route.required_capabilities and
            matches(route.*, request.method, request.target))
        {
            if (std.mem.indexOfScalar(u8, route.template, '{') == null) return route;
            if (best == null) best = route;
        }
    }
    if (best) |route| return route;
    if (config.test_routes) for (&test_routes) |*route| {
        if ((config.startup.allowed & route.required_capabilities) == route.required_capabilities and
            matches(route.*, request.method, request.target)) return route;
    };
    return null;
}
pub fn loopback(endpoint: io.Endpoint) bool {
    if (endpoint.family == .ipv4) return endpoint.address[0] == 127;
    const mapped = [_]u8{ 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 255, 255 };
    if (std.mem.eql(u8, endpoint.address[0..12], &mapped)) return endpoint.address[12] == 127;
    const localhost = [_]u8{ 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1 };
    return std.mem.eql(u8, &endpoint.address, &localhost);
}
pub fn header(request: Request, name: []const u8) !?[]const u8 {
    var found: ?[]const u8 = null;
    for (request.headers) |item| if (std.ascii.eqlIgnoreCase(item.name, name)) {
        if (found != null) return error.DuplicateHeader;
        found = item.value;
    };
    return found;
}
fn allowedAuthority(authority: []const u8, scheme: []const u8, local_port: u16) bool {
    if (authority.len == 0 or std.mem.indexOfAny(u8, authority, "/\\?#@ \t\r\n,") != null) return false;
    var host = authority;
    var port: u16 = if (std.ascii.eqlIgnoreCase(scheme, "https")) 443 else 80;
    if (authority[0] == '[') {
        const close = std.mem.indexOfScalar(u8, authority, ']') orelse return false;
        host = authority[1..close];
        if (close + 1 < authority.len) {
            if (authority[close + 1] != ':') return false;
            port = std.fmt.parseInt(u16, authority[close + 2 ..], 10) catch return false;
        }
    } else if (std.mem.lastIndexOfScalar(u8, authority, ':')) |colon| {
        host = authority[0..colon];
        port = std.fmt.parseInt(u16, authority[colon + 1 ..], 10) catch return false;
    }
    if (port != local_port) return false;
    if (std.ascii.eqlIgnoreCase(host, "localhost")) return true;
    const address = std.Io.net.IpAddress.parse(host, 0) catch return false;
    return switch (address) {
        .ip4 => |v| v.bytes[0] == 127,
        .ip6 => |v| loopback(.{ .family = .ipv6, .address = v.bytes, .port = port, .scope = 0 }),
    };
}
pub fn originAllowed(request: Request) bool {
    const host = (header(request, "Host") catch return false) orelse return false;
    if (!allowedAuthority(host, request.scheme, request.facts.local.port)) return false;
    if (absoluteSeparator(request.target)) |separator| {
        if (!std.ascii.eqlIgnoreCase(request.target[0..separator], request.scheme)) return false;
        const tail = request.target[separator + 3 ..];
        const end = std.mem.indexOfAny(u8, tail, "/?#") orelse tail.len;
        if (!std.ascii.eqlIgnoreCase(tail[0..end], host)) return false;
    }
    const origin = (header(request, "Origin") catch return false) orelse return true;
    const separator = std.mem.indexOf(u8, origin, "://") orelse return false;
    if (!std.ascii.eqlIgnoreCase(origin[0..separator], request.scheme)) return false;
    var authority = origin[separator + 3 ..];
    if (authority.len > 0 and authority[authority.len - 1] == '/') authority = authority[0 .. authority.len - 1];
    return std.ascii.eqlIgnoreCase(authority, host);
}
pub fn failure(output: []u8, status: u16, code: []const u8) !Result {
    var writer = json.Writer{ .buffer = output.ptr, .capacity = output.len };
    writer.raw("{\"error\":\"");
    writer.raw(code); // Closed internal error identifiers, never caller text.
    writer.raw("\"}");
    return finish(writer, output, status);
}
/// Format-derived reserve for the longest closed common error. A family's
/// useful payload window may be smaller; admission must still return its real
/// 403/404/503 reply before the source opens, on either listener path.
pub fn errorOutputBound() usize {
    return "{\"error\":\"local-windows-caller-required\"}".len;
}
fn finish(writer: json.Writer, output: []u8, status: u16) !Result {
    if (writer.failure != 0 or writer.length > output.len) return error.OutputLimit;
    return .{ .status = status, .body = output[0..@intCast(writer.length)] };
}
pub fn admit(config: Config, request: Request, output: []u8) !Admission {
    const route = lookup(config, request);
    // One and only one P-access entry per request, including missing routes.
    const decision = access.evaluate(.{
        .requirement = if (route) |value| value.requirement else .anonymous,
        .local_connection = loopback(request.facts.local) and loopback(request.facts.remote),
        .origin_allowed = originAllowed(request),
        .caller = if (request.caller_scope != null) request.caller else null,
        .normal_actions_allowed = config.normal_actions_allowed,
    });
    var rejection: ?Result = null;
    if (request.cancelled) return error.Cancelled;
    if (decision.disposition == .rejected) {
        const code: []const u8 = switch (decision.reason) {
            .loopback_origin_required => "loopback-origin-required",
            .local_windows_caller_required => "local-windows-caller-required",
            .administrator_required => "administrator-required",
            .access_policy_missing => "access-policy-missing",
            .source_unavailable => "source-unavailable",
            .none => "access-denied",
        };
        rejection = try failure(output, if (decision.reason == .access_policy_missing) 503 else 403, code);
    } else if (route == null) {
        rejection = try failure(output, 404, "api-endpoint-not-found");
    }
    return .{ .route = route, .decision = decision, .rejection = rejection };
}

/// Binding seam for each H1 family. Storage belongs to the current request's
/// local workspace allocator; no decoded strings or domain pointers escape it.
pub fn bindJson(comptime T: type, allocator: std.mem.Allocator, request: Request) !T {
    const content_type = (try header(request, "Content-Type")) orelse return error.UnsupportedMediaType;
    const end = std.mem.indexOfScalar(u8, content_type, ';') orelse content_type.len;
    const media = std.mem.trim(u8, content_type[0..end], " \t");
    if (!std.ascii.eqlIgnoreCase(media, "application/json") and
        !(std.ascii.startsWithIgnoreCase(media, "application/") and std.ascii.endsWithIgnoreCase(media, "+json")))
        return error.UnsupportedMediaType;
    if (request.body.len == 0) return error.InvalidJson;
    return std.json.parseFromSliceLeaky(T, allocator, request.body, .{
        .ignore_unknown_fields = true,
        .duplicate_field_behavior = .use_last,
    }) catch return error.InvalidJson;
}
/// Web JSON string: Zig byte slices also accept number arrays, so DTOs use
/// this wrapper when the wire field is a string. Null belongs to ?JsonString.
pub const JsonString = struct {
    value: []const u8,
    pub fn jsonParse(allocator: std.mem.Allocator, source: anytype, options: std.json.ParseOptions) std.json.ParseError(@TypeOf(source.*))!JsonString {
        return switch (try source.nextAllocMax(allocator, .alloc_if_needed, options.max_value_len.?)) {
            .string, .allocated_string => |value| .{ .value = value },
            else => error.UnexpectedToken,
        };
    }
};
/// Web allows integer strings, but an integer field never rounds floating or
/// exponent syntax, even when its mathematical value is integral.
pub const JsonInteger = struct {
    value: i64,
    pub fn jsonParse(allocator: std.mem.Allocator, source: anytype, options: std.json.ParseOptions) std.json.ParseError(@TypeOf(source.*))!JsonInteger {
        const token = try source.nextAllocMax(allocator, .alloc_if_needed, options.max_value_len.?);
        defer switch (token) {
            .allocated_number, .allocated_string => |value| allocator.free(value),
            else => {},
        };
        const value = switch (token) {
            .number, .allocated_number, .string, .allocated_string => |value| value,
            else => return error.UnexpectedToken,
        };
        if (value.len == 0) return error.InvalidNumber;
        const first_digit: usize = if (value[0] == '-' or value[0] == '+') 1 else 0;
        if (first_digit == value.len) return error.InvalidNumber;
        for (value[first_digit..]) |byte| if (byte < '0' or byte > '9') return error.InvalidNumber;
        return .{ .value = try std.fmt.parseInt(i64, value, 10) };
    }
};
const EchoInput = struct {
    value: ?JsonString = null,
    count: ?JsonInteger = null,
    enabled: ?bool = null,
    pub fn jsonParse(allocator: std.mem.Allocator, source: anytype, options: std.json.ParseOptions) std.json.ParseError(@TypeOf(source.*))!EchoInput {
        if (try source.next() != .object_begin) return error.UnexpectedToken;
        var value: EchoInput = .{};
        while (true) {
            if (try source.peekNextTokenType() == .object_end) {
                _ = try source.next();
                return value;
            }
            const key = try JsonString.jsonParse(allocator, source, options);
            if (std.ascii.eqlIgnoreCase(key.value, "value")) {
                value.value = try std.json.innerParse(?JsonString, allocator, source, options);
            } else if (std.ascii.eqlIgnoreCase(key.value, "count")) {
                value.count = try std.json.innerParse(?JsonInteger, allocator, source, options);
            } else if (std.ascii.eqlIgnoreCase(key.value, "enabled")) {
                value.enabled = try std.json.innerParse(?bool, allocator, source, options);
            } else try source.skipValue();
        }
    }
};
fn utf8String(writer: *json.Writer, input: []const u8) !void {
    const view = try std.unicode.Utf8View.init(input);
    var iterator = view.iterator();
    writer.raw("\"");
    while (iterator.nextCodepoint()) |point| {
        var units: [2]u16 = undefined;
        const length: usize = if (point <= 0xffff) blk: {
            units[0] = @intCast(point);
            break :blk 1;
        } else blk: {
            const rest = point - 0x10000;
            units = .{ @intCast(0xd800 + (rest >> 10)), @intCast(0xdc00 + (rest & 0x3ff)) };
            break :blk 2;
        };
        // Reuse the production HTTP escaping bitmap/UTF16 rules without a
        // second string encoder. Strip only this small writer's quote pair.
        var small: [14]u8 = undefined;
        var encoded = json.Writer{ .buffer = &small, .capacity = small.len };
        if (json.writeString(&encoded, &units, length) != 0) return error.InvalidJson;
        writer.raw(small[1 .. @as(usize, @intCast(encoded.length)) - 1]);
    }
    writer.raw("\"");
}
fn capabilities(config: Config, output: []u8) !Result {
    var writer = json.Writer{ .buffer = output.ptr, .capacity = output.len };
    writer.raw("{\"profileId\":");
    try utf8String(&writer, config.startup.profile_id);
    const names = [_][]const u8{ "readOnly", "mutablePersistence", "gpuLaunchInterceptionReconciliation", "runtimeEffectOwners", "publicServiceCoordination", "optimizationRuntime" };
    const flags = [_]u32{ 0, 1 << 0, 1 << 2, 1 << 3, 1 << 4, 1 << 5 };
    for (names, flags) |name, flag| {
        writer.raw(",\"");
        writer.raw(name);
        writer.raw("\":");
        const enabled = if (flag == 0) config.startup.allowed == 0 else config.startup.allowed & flag == flag;
        writer.raw(if (enabled) "true" else "false");
    }
    writer.raw("}");
    return finish(writer, output, 200);
}
pub fn mapError(output: []u8, err: anyerror) !Result {
    return switch (err) {
        error.Cancelled => error.Cancelled,
        error.UnsupportedMediaType => failure(output, 415, "unsupported-media-type"),
        error.InvalidJson, error.DuplicateHeader => failure(output, 400, "invalid-json"),
        error.BodyLimit => failure(output, 413, "request-body-too-large"),
        error.OutputLimit => failure(output, 503, "response-output-limit"),
        else => failure(output, 500, "internal-error"),
    };
}
pub fn dispatchAdmitted(config: Config, request: Request, admission: Admission, output: []u8) !Result {
    if (request.cancelled) return error.Cancelled;
    if (admission.rejection) |result| return result;
    const route = admission.route orelse return failure(output, 404, "api-endpoint-not-found");
    if (admission.decision.disposition != .allowed and admission.decision.disposition != .check_selectors)
        return failure(output, 403, "access-denied");
    if (route.requirement != .anonymous and route.requirement != .per_selector and
        (request.caller == null or request.caller_scope == null)) return failure(output, 403, "local-windows-caller-required");
    return switch (route.handler) {
        .capabilities => capabilities(config, output),
        // Stateful/deferred families always use Operation. This synchronous
        // compatibility entry cannot start work it has no lifetime to drain.
        .family => error.OperationRequired,
        .echo => echo(request, output) catch |err| mapError(output, err),
    };
}
fn echo(request: Request, output: []u8) !Result {
    // Test-only binding proof. The supplied bounded final region is shared
    // between temporary decoding and final encoding in this synchronous scope.
    const split = output.len / 2;
    var temporary = std.heap.FixedBufferAllocator.init(output[0..split]);
    const value = try bindJson(EchoInput, temporary.allocator(), request);
    const final = output[split..];
    var writer = json.Writer{ .buffer = final.ptr, .capacity = final.len };
    writer.raw("{\"value\":");
    if (value.value) |text| try utf8String(&writer, text.value) else writer.raw("null");
    writer.raw(",\"count\":");
    if (value.count) |count| writer.integer(count.value) else writer.raw("null");
    writer.raw(",\"enabled\":");
    if (value.enabled) |enabled| writer.raw(if (enabled) "true" else "false") else writer.raw("null");
    writer.raw("}");
    const result = try finish(writer, final, 200);
    std.mem.copyForwards(u8, output[0..result.body.len], result.body);
    return .{ .status = 200, .body = output[0..result.body.len] };
}
pub fn dispatch(config: Config, request: Request, output: []u8) !Result {
    return dispatchAdmitted(config, request, try admit(config, request, output), output);
}
/// Format-derived capacity, not a behavioral/default limit. The profile ID is
/// the sole variable capability field; a UTF8 byte needs at most six JSON bytes.
pub fn outputBound(profile_bytes: usize) usize {
    return std.math.add(usize, std.math.mul(usize, profile_bytes, 6) catch return 0, "{\"profileId\":\"\",\"readOnly\":false,\"mutablePersistence\":false,\"gpuLaunchInterceptionReconciliation\":false,\"runtimeEffectOwners\":false,\"publicServiceCoordination\":false,\"optimizationRuntime\":false}".len) catch 0;
}
