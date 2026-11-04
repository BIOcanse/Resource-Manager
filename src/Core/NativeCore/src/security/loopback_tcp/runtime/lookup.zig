const core = @import("../core/table.zig");

pub const Status = enum(u32) { ok, invalid_input, unavailable, capacity, invalid_table, ambiguous, retired };
pub const Result = extern struct {
    pid: u32 = 0,
    status: Status = .unavailable,
    workspace_bytes: u64 = 0,
    table_bytes: u32 = 0,
    reserved: u32 = 0,
};
pub const Query = struct {
    context: ?*anyopaque,
    call: *const fn (?*anyopaque, ?[*]u8, *u32, u16) u32,
};
pub const Work = *const fn (?*anyopaque, ?[*]u8, usize) callconv(.c) u32;
// enter/leave grant one execution slot, not a business-state lock held over IO.
// with_region calls work synchronously with a local scoped lease. No pointer or
// input table survives it. Exceptions must be contained by the host adapters.
pub const Host = extern struct {
    size: u32 = @sizeOf(Host),
    version: u32 = 1,
    context: ?*anyopaque,
    enter: ?*const fn (?*anyopaque) callconv(.c) u32,
    leave: ?*const fn (?*anyopaque) callconv(.c) void,
    with_region: ?*const fn (?*anyopaque, u32, Work, ?*anyopaque) callconv(.c) u32,
};

const Operation = struct {
    connection: core.Connection,
    query: Query,
    requested: u32,
    result: *Result,

    fn run(context: ?*anyopaque, memory: ?[*]u8, length: usize) callconv(.c) u32 {
        const self: *Operation = @ptrCast(@alignCast(context orelse return 0));
        self.result.workspace_bytes = length;
        const bytes = memory orelse return 0;
        if (length < self.requested) {
            self.result.status = .capacity;
            return 0;
        }
        // Limit the second system call to the probed size even if the Ledger
        // workspace is larger. Growth between the two queries fails closed.
        var actual = self.requested;
        if (self.query.call(self.query.context, bytes, &actual, self.connection.client.family) != 0) return 0;
        if (actual > self.requested or actual < @sizeOf(u32)) {
            self.result.status = .invalid_table;
            return 0;
        }
        self.result.table_bytes = actual;
        self.result.pid = (core.find(bytes[0..actual], self.connection) catch |err| {
            self.result.status = switch (err) {
                error.InvalidEndpoint => .invalid_input,
                error.InvalidTable => .invalid_table,
                error.AmbiguousOwner => .ambiguous,
            };
            return 0;
        }) orelse 0;
        self.result.status = .ok;
        return 1;
    }
};

pub fn find(host: Host, query: Query, connection: core.Connection, maximum_bytes: u32) Result {
    var result: Result = .{};
    core.validate(connection) catch {
        result.status = .invalid_input;
        return result;
    };
    if (host.size != @sizeOf(Host) or host.version != 1 or host.enter == null or
        host.leave == null or host.with_region == null or maximum_bytes < @sizeOf(u32))
    {
        result.status = .invalid_input;
        return result;
    }
    if (host.enter.?(host.context) == 0) {
        result.status = .retired;
        return result;
    }
    defer host.leave.?(host.context);
    var required: u32 = 0;
    // ERROR_INSUFFICIENT_BUFFER is the only accepted probe response, as before.
    if (query.call(query.context, null, &required, connection.client.family) != 122) return result;
    if (required < @sizeOf(u32)) {
        result.status = .invalid_table;
        return result;
    }
    if (required > maximum_bytes) {
        result.status = .capacity;
        return result;
    }
    var operation = Operation{ .connection = connection, .query = query, .requested = required, .result = &result };
    if (host.with_region.?(host.context, required, Operation.run, &operation) == 0) {
        result.pid = 0;
        if (result.status == .ok) result.status = .unavailable;
    }
    return result;
}
