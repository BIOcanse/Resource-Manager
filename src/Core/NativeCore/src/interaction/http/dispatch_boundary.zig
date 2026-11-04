//! C19 request/operation lifetime. Adapted from H-common b4437b66, without
//! its policy/default table, test endpoints or capability business implementation.
//! This is NOT an admission authority. Composition must use the original C15/
//! C16 owner before starting an operation, inside its synchronous caller borrow.
//! There is no permissive/default gate here. Missing admission/source composition
//! must remain unavailable; this module alone does not replace a product host.
const std = @import("std");
pub const contract = @import("invocation_contract");
pub const io = @import("socket_io");
const response = @import("http_response");
pub const Header = response.Header;

pub const Request = struct {
    method: []const u8,
    target: []const u8,
    headers: []const Header,
    body: []const u8,
    facts: io.Facts,
    /// Actual C15/C16 proof borrow, never request data. Ends when begin returns.
    caller_scope: ?*const anyopaque,
};
pub const Capacity = struct { state_bytes: usize, output_bytes: usize, state_alignment: usize };
pub const Wake = struct {
    context: *anyopaque,
    /// Queues only; errors MUST reach the source's run failure path.
    post: *const fn (*anyopaque) anyerror!void,
    pub fn notify(self: Wake) !void {
        try self.post(self.context);
    }
};
pub const Call = struct {
    state: []u8,
    wake: ?Wake,
    cancelled: std.atomic.Value(bool) = .init(false),
    pub fn isCancelled(self: *const Call) bool {
        return self.cancelled.load(.acquire);
    }
};
pub const Event = enum { ready, writable, cancel };
pub const Result = struct {
    status: u16,
    body: []const u8,
    content_type: ?[]const u8,
    headers: []const Header,
    more: bool,
    total_length: ?u64,
};
pub const Progress = union(enum) { pending, output: Result, done };
/// Source/codec binding, not a second business owner. Calls are synchronous and
/// nonblocking; source field/caller borrows end on return. begin accepts once,
/// step advances THAT result; release joins/unregisters its original callbacks.
/// Throwing means owner work and source borrows are terminal. Before headers the
/// caller maps errors through the existing family contract; after headers it
/// closes the incomplete framing. Never append a second error response.
pub const Family = struct {
    context: ?*anyopaque,
    capacity: Capacity,
    can_suspend: bool,
    begin: *const fn (?*anyopaque, Request, *Call, []u8) anyerror!Progress,
    step: *const fn (?*anyopaque, *Call, Event, []u8) anyerror!Progress,
    release: *const fn (?*anyopaque, *Call) void,
};

/// HTTP aliases refer to the ONE neutral descriptor, never copy its policy,
/// schema or operation business logic. Composition owns these immutable views
/// and their actual source contexts until all operations release.
pub const Route = struct {
    method: []const u8,
    template: []const u8,
    capability: *const contract.Descriptor,
    family: Family,
};
pub const Registry = struct {
    routes: []const Route,
    pub fn init(routes: []const Route) !Registry {
        for (routes, 0..) |route, index| {
            if (route.method.len == 0 or route.template.len == 0 or route.template[0] != '/' or
                std.mem.indexOfAny(u8, route.template, "?#{}%\\") != null or
                !contract.operationValid(route.capability.operation)) return error.InvalidRoute;
            for (route.method) |byte| if (!std.ascii.isUpper(byte)) return error.InvalidRoute;
            try validateDeclaration(route.family.capacity);
            for (routes[0..index]) |earlier| {
                if (std.mem.eql(u8, route.method, earlier.method) and
                    std.ascii.eqlIgnoreCase(trimSlash(route.template), trimSlash(earlier.template)))
                    return error.DuplicateRoute;
            }
        }
        return .{ .routes = routes };
    }
    /// Literal route subset only, matching ASP.NET case/trailing-slash rules.
    /// Parameter/constraint/catch-all routes need their actual owner's binding;
    /// init rejects them instead of silently advertising partial support.
    pub fn resolve(self: Registry, method: []const u8, target: []const u8) ?*const Route {
        const end = std.mem.indexOfScalar(u8, target, '?') orelse target.len;
        const path = trimSlash(target[0..end]);
        for (self.routes) |*route| {
            if (std.mem.eql(u8, method, route.method) and
                std.ascii.eqlIgnoreCase(path, trimSlash(route.template))) return route;
        }
        return null;
    }
};
fn trimSlash(path: []const u8) []const u8 {
    return if (path.len > 1 and path[path.len - 1] == '/') path[0 .. path.len - 1] else path;
}

/// Stable local run record. Contains no lease or domain current. Caller supplies
/// scoped Ledger state/output in product; keeps it immovable through source join
/// and terminal send. Admission-wait must not construct/acquire that workspace.
pub const Operation = @import("operation.zig").Engine(@This());

fn validateDeclaration(capacity: Capacity) !void {
    if (capacity.output_bytes == 0 or capacity.state_alignment < @alignOf(usize) or
        !std.math.isPowerOfTwo(capacity.state_alignment)) return error.InvalidCapacity;
}
pub fn validateCapacity(capacity: Capacity, state: []u8, output: []u8) !void {
    try validateDeclaration(capacity);
    if (state.len < capacity.state_bytes or output.len < capacity.output_bytes) return error.OutputLimit;
    if (state.len != 0 and @intFromPtr(state.ptr) % capacity.state_alignment != 0) return error.InvalidStateAlignment;
    if (overlap(state, output)) return error.OverlappingRegions;
}
fn overlap(a: []const u8, b: []const u8) bool {
    if (a.len == 0 or b.len == 0) return false;
    const x = @intFromPtr(a.ptr);
    const y = @intFromPtr(b.ptr);
    return if (x <= y) y - x < a.len else x - y < b.len;
}
pub fn validateResult(result: Result, output: []u8) !void {
    if (result.status < 200 or result.status > 599) return error.InvalidResponse;
    if (result.content_type) |value| if (!response.validValue(value)) return error.InvalidResponse;
    for (result.headers) |header| {
        if (!response.validName(header.name) or !response.validValue(header.value) or
            std.ascii.eqlIgnoreCase(header.name, "content-length") or
            std.ascii.eqlIgnoreCase(header.name, "transfer-encoding") or
            std.ascii.eqlIgnoreCase(header.name, "connection") or
            std.ascii.eqlIgnoreCase(header.name, "trailer") or
            std.ascii.eqlIgnoreCase(header.name, "content-type")) return error.InvalidResponse;
    }
    if (result.more and (result.status == 204 or result.status == 304)) return error.InvalidResponse;
    if (!result.more and result.total_length != null and result.total_length.? != result.body.len) return error.InvalidResponse;
    if (result.total_length) |length| if (length < result.body.len) return error.InvalidResponse;
    if (result.body.len == 0) return;
    const start = @intFromPtr(output.ptr);
    const pointer = @intFromPtr(result.body.ptr);
    if (result.body.len > output.len or pointer < start or pointer - start > output.len - result.body.len)
        return error.ResponseBodyNotFinalOutput;
}

pub const WakeArg = ?Wake;
pub const validateProgressResult = validateResult;
pub fn continuationValid(result: Result) bool {
    return result.content_type == null;
}
pub fn hasWake(wake: WakeArg) bool {
    return wake != null;
}
pub fn startOperation(self: *Operation, family: Family, request: Request, output: []u8) !Progress {
    return self.startFamily(family, request, output);
}
pub fn cancelOperation(self: *Operation, output: []u8) !Progress {
    return self.cancelDeferred(output);
}
pub fn familyFailure(self: *Operation, err: anyerror, _: []u8) anyerror!Progress {
    self.phase = .done;
    return err;
}
