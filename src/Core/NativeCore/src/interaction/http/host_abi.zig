//! Temporary in-process Kestrel boundary; removed by H-cutover. All spans and
//! the actual caller token/scope are borrowed only during this synchronous call.
const std = @import("std");
pub const host = @import("host.zig");
pub const Slice = extern struct {
    ptr: ?[*]const u8,
    len: usize,
    fn bytes(self: Slice) ![]const u8 {
        return if (self.ptr) |pointer| pointer[0..self.len] else if (self.len == 0) &.{} else error.Invalid;
    }
};
pub const Header = extern struct { name: Slice, value: Slice };
pub const Request = extern struct {
    version: u32,
    struct_size: u32,
    method: Slice,
    target: Slice,
    scheme: Slice,
    headers: ?[*]const Header,
    header_count: usize,
    body: Slice,
    profile_id: Slice,
    allowed_capabilities: u32,
    normal_actions_allowed: u32,
    test_routes: u32,
    cancelled: u32,
    local_family: u32,
    remote_family: u32,
    local_address: [16]u8,
    remote_address: [16]u8,
    local_port: u32,
    remote_port: u32,
    process_id: i32,
    session_id: i32,
    created_ticks: i64,
    is_admin: u32,
    reserved: u32,
    sid: ?[*]const u16,
    sid_length: usize,
    caller_scope: ?*const anyopaque,
    output: ?[*]u8,
    output_capacity: usize,
};
pub const Result = extern struct { status: u32, content_type: u32, length: usize };

pub export fn rm_http_common_owns(method: ?[*]const u8, method_length: usize, target: ?[*]const u8, target_length: usize) callconv(.c) i32 {
    const verb = (Slice{ .ptr = method, .len = method_length }).bytes() catch return 0;
    const url = (Slice{ .ptr = target, .len = target_length }).bytes() catch return 0;
    return @intFromBool(host.owns(verb, url));
}
pub export fn rm_http_common_output_bound(profile_length: usize, header_count: usize, body_length: usize) callconv(.c) usize {
    const product = host.outputBound(profile_length);
    if (product == 0) return 0;
    const echo_bytes = std.math.mul(usize, body_length, 12) catch return 0;
    const final_bytes = std.math.add(usize, std.math.mul(usize, product, 2) catch return 0, echo_bytes) catch return 0;
    const header_bytes = std.math.mul(usize, header_count, @sizeOf(host.Header)) catch return 0;
    return std.math.add(usize, final_bytes, std.math.add(usize, header_bytes, @alignOf(host.Header) - 1) catch return 0) catch 0;
}
fn family(value: u32) !host.io.Family {
    return switch (value) {
        4 => .ipv4,
        6 => .ipv6,
        else => error.Invalid,
    };
}
fn invoke(input: *const Request, result: *Result) !void {
    if (input.version != 1 or input.struct_size != @sizeOf(Request) or input.reserved != 0 or
        input.normal_actions_allowed > 1 or input.test_routes > 1 or input.cancelled > 1 or input.is_admin > 1 or
        input.local_port > std.math.maxInt(u16) or input.remote_port > std.math.maxInt(u16) or
        (input.headers == null and input.header_count != 0) or (input.sid == null and input.sid_length != 0)) return error.Invalid;
    // Header views use the supplied final workspace temporarily, then encoding
    // reuses it after admission/handler reads finish. No arbitrary header-count
    // ABI default and no heap/fallback allocation. It is the caller's bound.
    const output = (input.output orelse return error.Invalid)[0..input.output_capacity];
    const header_bytes = std.math.mul(usize, input.header_count, @sizeOf(host.Header)) catch return error.Invalid;
    const alignment_slack = @alignOf(host.Header) - 1;
    if (header_bytes +| alignment_slack > output.len) return error.OutputLimit;
    var allocator = std.heap.FixedBufferAllocator.init(output);
    const headers = try allocator.allocator().alloc(host.Header, input.header_count);
    for (headers, 0..) |*item, index| item.* = .{
        .name = try input.headers.?[index].name.bytes(),
        .value = try input.headers.?[index].value.bytes(),
    };
    // The native typed header views remain live while the handler runs; final
    // bytes use the remaining region, then move once to the caller's segment.
    const final = output[allocator.end_index..];
    const caller: ?host.access.Caller = if (input.caller_scope == null) null else .{
        .process_id = input.process_id,
        .process_created_utc_ticks = input.created_ticks,
        .windows_session_id = input.session_id,
        .is_administrator = input.is_admin != 0,
        .windows_sid = if (input.sid) |pointer| pointer[0..input.sid_length] else &.{},
    };
    const response = try host.dispatch(.{
        .startup = .{ .profile_id = try input.profile_id.bytes(), .allowed = input.allowed_capabilities },
        .normal_actions_allowed = input.normal_actions_allowed != 0,
        .test_routes = input.test_routes != 0,
    }, .{
        .method = try input.method.bytes(),
        .target = try input.target.bytes(),
        .scheme = try input.scheme.bytes(),
        .headers = headers,
        .body = try input.body.bytes(),
        .cancelled = input.cancelled != 0,
        .caller = caller,
        .caller_scope = input.caller_scope,
        .facts = .{
            .local = .{ .family = try family(input.local_family), .address = input.local_address, .scope = 0, .port = @intCast(input.local_port) },
            .remote = .{ .family = try family(input.remote_family), .address = input.remote_address, .scope = 0, .port = @intCast(input.remote_port) },
        },
    }, final);
    std.mem.copyForwards(u8, output[0..response.body.len], response.body);
    result.* = .{ .status = response.status, .content_type = 1, .length = response.body.len };
}
pub export fn rm_http_common_dispatch(input: ?*const Request, output: ?*Result) callconv(.c) i32 {
    const result = output orelse return 1;
    result.* = .{ .status = 0, .content_type = 0, .length = 0 };
    invoke(input orelse return 1, result) catch |err| return switch (err) {
        error.OutputLimit, error.OutOfMemory => 2,
        error.Cancelled => 3,
        else => 1,
    };
    return 0;
}

// V2 is a borrowed, resumable family boundary. All allocation belongs to the
// request dispatcher. Only bridge control and family-owned state survive calls.
pub const FamilyCapacity = extern struct { storage_bytes: usize, alignment: usize, output_bytes: usize };
pub const FamilyProgress = extern struct {
    kind: u32, // pending=0, output=1, done=2, admitted/body required=3
    status: u32,
    content_type: Slice,
    headers: ?[*]const Header,
    header_count: usize,
    body: Slice,
    more: u32,
    total_known: u32,
    total_length: u64,
};
const Post = *const fn (?*anyopaque) callconv(.c) void;
const Identity = struct { pid: i32, created: i64, session: i32, administrator: u32 };
const FamilyRun = struct {
    operation: host.Operation,
    route: host.Route,
    source: host.Family,
    context: ?*anyopaque = null,
    wake_context: ?*anyopaque,
    post: Post,
    headers: []host.Header,
    admission: ?host.Admission = null,
    identity: ?Identity = null,

    fn wake(context: ?*anyopaque, _: u64) void {
        const self: *FamilyRun = @ptrCast(@alignCast(context.?));
        self.post(self.wake_context);
    }
    fn begin(context: ?*anyopaque, request: host.Request, call: *host.Call, output: []u8) !host.Progress {
        const self: *FamilyRun = @ptrCast(@alignCast(context.?));
        return self.source.begin(self.context, request, call, output);
    }
    fn step(context: ?*anyopaque, call: *host.Call, event: host.Event, output: []u8) !host.Progress {
        const self: *FamilyRun = @ptrCast(@alignCast(context.?));
        return self.source.step(self.context, call, event, output);
    }
    fn release(context: ?*anyopaque, call: *host.Call) void {
        const self: *FamilyRun = @ptrCast(@alignCast(context.?));
        self.source.release(self.context, call);
    }
    fn borrow(self: *FamilyRun, route: *const host.Route) !void {
        const binding = try familyBinding(route);
        if (!std.mem.eql(u8, route.method, self.route.method) or !std.mem.eql(u8, route.template, self.route.template) or
            route.requirement != self.route.requirement or route.required_capabilities != self.route.required_capabilities or
            binding.capacity.state_bytes != self.source.capacity.state_bytes or binding.capacity.output_bytes != self.source.capacity.output_bytes or
            binding.capacity.state_alignment != self.source.capacity.state_alignment or binding.begin != self.source.begin or
            binding.step != self.source.step or binding.release != self.source.release or binding.can_suspend != self.source.can_suspend) return error.Invalid;
        // Context belongs to THIS synchronous source borrow, never to Operation.
        self.context = binding.context;
    }
    fn endBorrow(self: *FamilyRun) void {
        self.context = null;
        for (self.headers) |*item| item.* = .{ .name = &.{}, .value = &.{} };
    }
};
fn familyBinding(route: *const host.Route) !host.Family {
    return switch (route.handler) {
        .family => |value| value,
        else => error.Invalid,
    };
}
fn familyLayout(route: *const host.Route, header_count: usize) !FamilyCapacity {
    const binding = try familyBinding(route);
    const capacity = binding.capacity;
    if (capacity.output_bytes == 0 or capacity.state_alignment < @alignOf(usize) or !std.math.isPowerOfTwo(capacity.state_alignment)) return error.Invalid;
    const alignment = @max(@alignOf(FamilyRun), @max(@alignOf(host.Header), capacity.state_alignment));
    var bytes = try std.math.add(usize, @sizeOf(FamilyRun), alignment - 1);
    bytes = try std.math.add(usize, bytes, capacity.state_bytes);
    bytes = try std.math.add(usize, bytes, @alignOf(host.Header) - 1);
    bytes = try std.math.add(usize, bytes, try std.math.mul(usize, header_count, @sizeOf(host.Header)));
    bytes = try std.math.add(usize, bytes, try std.math.add(usize, route.method.len, route.template.len));
    bytes = try std.math.add(usize, bytes, alignment - 1);
    bytes &= ~(alignment - 1);
    return .{ .storage_bytes = bytes, .alignment = alignment, .output_bytes = @max(capacity.output_bytes, host.errorOutputBound()) };
}
fn familyRun(pointer: ?*anyopaque) !*FamilyRun {
    const storage = pointer orelse return error.Invalid;
    if (@intFromPtr(storage) % @alignOf(FamilyRun) != 0) return error.Invalid;
    return @ptrCast(@alignCast(storage));
}
fn view(input: *const Request, headers: []host.Header) !host.Request {
    if (input.version != 1 or input.struct_size != @sizeOf(Request) or input.reserved != 0 or
        input.normal_actions_allowed > 1 or input.test_routes > 1 or input.cancelled > 1 or input.is_admin > 1 or
        input.local_port > std.math.maxInt(u16) or input.remote_port > std.math.maxInt(u16) or
        input.header_count > headers.len or (input.headers == null and input.header_count != 0) or
        (input.sid == null and input.sid_length != 0)) return error.Invalid;
    for (headers[0..input.header_count], 0..) |*item, index| item.* = .{
        .name = try input.headers.?[index].name.bytes(),
        .value = try input.headers.?[index].value.bytes(),
    };
    return .{
        .method = try input.method.bytes(),
        .target = try input.target.bytes(),
        .scheme = try input.scheme.bytes(),
        .headers = headers[0..input.header_count],
        .body = try input.body.bytes(),
        .cancelled = input.cancelled != 0,
        .caller_scope = input.caller_scope,
        .caller = if (input.caller_scope == null) null else .{
            .process_id = input.process_id,
            .process_created_utc_ticks = input.created_ticks,
            .windows_session_id = input.session_id,
            .is_administrator = input.is_admin != 0,
            .windows_sid = if (input.sid) |sid| sid[0..input.sid_length] else &.{},
        },
        .facts = .{
            .local = .{ .family = try family(input.local_family), .address = input.local_address, .scope = 0, .port = @intCast(input.local_port) },
            .remote = .{ .family = try family(input.remote_family), .address = input.remote_address, .scope = 0, .port = @intCast(input.remote_port) },
        },
    };
}
fn config(input: *const Request, route: *const host.Route) !host.Config {
    return .{ .startup = .{ .profile_id = try input.profile_id.bytes(), .allowed = input.allowed_capabilities }, .normal_actions_allowed = input.normal_actions_allowed != 0, .test_routes = input.test_routes != 0, .family_routes = @as([*]const host.Route, @ptrCast(route))[0..1] };
}
fn resultProgress(progress: host.Progress, output: []u8, result: *FamilyProgress) !void {
    result.* = std.mem.zeroes(FamilyProgress);
    switch (progress) {
        .pending => result.kind = 0,
        .done => result.kind = 2,
        .output => |value| {
            if (value.body.len > output.len) return error.OutputLimit;
            result.* = .{ .kind = 1, .status = value.status, .content_type = if (value.content_type) |content_type| .{ .ptr = content_type.ptr, .len = content_type.len } else .{ .ptr = null, .len = 0 }, .headers = if (value.headers.len == 0) null else @ptrCast(value.headers.ptr), .header_count = value.headers.len, .body = .{ .ptr = value.body.ptr, .len = value.body.len }, .more = @intFromBool(value.more), .total_known = @intFromBool(value.total_length != null), .total_length = value.total_length orelse value.body.len };
        },
    }
}
fn errorCode(err: anyerror) i32 {
    return switch (err) {
        error.OutputLimit, error.OutOfMemory => 2,
        error.Cancelled => 3,
        else => 1,
    };
}
pub export fn rm_http_common_family_owns(route: ?*const host.Route, method: ?[*]const u8, method_length: usize, target: ?[*]const u8, target_length: usize) callconv(.c) i32 {
    const value = route orelse return 0;
    _ = familyBinding(value) catch return 0;
    const verb = (Slice{ .ptr = method, .len = method_length }).bytes() catch return 0;
    const url = (Slice{ .ptr = target, .len = target_length }).bytes() catch return 0;
    return host.matchRank(value.*, verb, url);
}
/// Passive descriptor metadata, supplied by the sole native route owner.
pub export fn rm_http_common_family_requirement(route: ?*const host.Route) callconv(.c) i32 {
    const descriptor = route orelse return -1;
    _ = familyBinding(descriptor) catch return -1;
    return @intCast(@backingInt(descriptor.requirement));
}
pub export fn rm_http_common_family_capacity(route: ?*const host.Route, header_count: usize, output: ?*FamilyCapacity) callconv(.c) i32 {
    const result = output orelse return 1;
    result.* = familyLayout(route orelse return 1, header_count) catch |err| return errorCode(err);
    return 0;
}
pub export fn rm_http_common_family_init(storage: ?*anyopaque, length: usize, route: ?*const host.Route, header_count: usize, wake_context: ?*anyopaque, post: ?Post) callconv(.c) i32 {
    const descriptor = route orelse return 1;
    const layout = familyLayout(descriptor, header_count) catch |err| return errorCode(err);
    const pointer = storage orelse return 1;
    if (length < layout.storage_bytes or @intFromPtr(pointer) % layout.alignment != 0) return 1;
    const self: *FamilyRun = @ptrCast(@alignCast(pointer));
    const binding = familyBinding(descriptor) catch return 1;
    var offset = std.mem.alignForward(usize, @sizeOf(FamilyRun), binding.capacity.state_alignment);
    const bytes = @as([*]u8, @ptrCast(pointer))[0..length];
    const state = bytes[offset..][0..binding.capacity.state_bytes];
    @memset(state, 0);
    offset = std.mem.alignForward(usize, offset + state.len, @alignOf(host.Header));
    const headers = @as([*]host.Header, @ptrCast(@alignCast(bytes.ptr + offset)))[0..header_count];
    offset += header_count * @sizeOf(host.Header);
    const method = bytes[offset..][0..descriptor.method.len];
    @memcpy(method, descriptor.method);
    offset += method.len;
    const template = bytes[offset..][0..descriptor.template.len];
    @memcpy(template, descriptor.template);
    self.* = .{ .operation = host.Operation.init(state, .{ .context = self, .post = FamilyRun.wake }), .route = descriptor.*, .source = binding, .headers = headers, .wake_context = wake_context, .post = post orelse return 1 };
    self.source.context = null;
    self.route.method = method;
    self.route.template = template;
    self.route.handler = .{ .family = .{ .context = self, .capacity = binding.capacity, .begin = FamilyRun.begin, .step = FamilyRun.step, .release = FamilyRun.release, .can_suspend = binding.can_suspend } };
    return 0;
}
fn admitFamily(storage: ?*anyopaque, input: *const Request, route: *const host.Route, result: *FamilyProgress) !void {
    const self = try familyRun(storage);
    if (self.admission != null or self.operation.phase != .fresh) return error.AlreadyStarted;
    try self.borrow(route);
    defer self.endBorrow();
    const request = try view(input, self.headers);
    const output = (input.output orelse return error.Invalid)[0..input.output_capacity];
    const settings = try config(input, &self.route);
    const admitted = try host.admit(settings, request, output);
    if (admitted.rejection != null) {
        try resultProgress(try self.operation.start(settings, request, admitted, output), output, result);
        return;
    }
    self.admission = admitted;
    self.identity = if (request.caller == null) null else .{ .pid = input.process_id, .created = input.created_ticks, .session = input.session_id, .administrator = input.is_admin };
    result.* = std.mem.zeroes(FamilyProgress);
    result.kind = 3;
}
pub export fn rm_http_common_family_admit(storage: ?*anyopaque, input: ?*const Request, route: ?*const host.Route, result: ?*FamilyProgress) callconv(.c) i32 {
    admitFamily(storage, input orelse return 1, route orelse return 1, result orelse return 1) catch |err| return errorCode(err);
    return 0;
}
fn startFamily(storage: ?*anyopaque, input: *const Request, route: *const host.Route, result: *FamilyProgress) !void {
    const self = try familyRun(storage);
    const admitted = self.admission orelse return error.Invalid;
    try self.borrow(route);
    defer self.endBorrow();
    const request = try view(input, self.headers);
    const output = (input.output orelse return error.Invalid)[0..input.output_capacity];
    if (self.identity) |identity| {
        if (request.caller == null or input.process_id != identity.pid or input.created_ticks != identity.created or
            input.session_id != identity.session or input.is_admin != identity.administrator)
        {
            self.admission = null;
            const rejected = host.Admission{ .route = &self.route, .decision = admitted.decision, .rejection = try host.failure(output, 403, "local-windows-caller-required") };
            try resultProgress(try self.operation.start(try config(input, &self.route), request, rejected, output), output, result);
            return;
        }
    }
    self.admission = null;
    try resultProgress(try self.operation.start(try config(input, &self.route), request, admitted, output), output, result);
}
pub export fn rm_http_common_family_start(storage: ?*anyopaque, input: ?*const Request, route: ?*const host.Route, result: ?*FamilyProgress) callconv(.c) i32 {
    startFamily(storage, input orelse return 1, route orelse return 1, result orelse return 1) catch |err| return errorCode(err);
    return 0;
}
fn stepFamily(storage: ?*anyopaque, route: *const host.Route, event: u32, pointer: ?[*]u8, length: usize, result: *FamilyProgress) !void {
    const self = try familyRun(storage);
    try self.borrow(route);
    defer self.endBorrow();
    const output = (pointer orelse return error.Invalid)[0..length];
    const progress = switch (event) {
        0 => try self.operation.step(.ready, output),
        1 => try self.operation.step(.writable, output),
        2 => try self.operation.cancel(output),
        else => return error.Invalid,
    };
    try resultProgress(progress, output, result);
}
pub export fn rm_http_common_family_step(storage: ?*anyopaque, route: ?*const host.Route, event: u32, pointer: ?[*]u8, length: usize, result: ?*FamilyProgress) callconv(.c) i32 {
    stepFamily(storage, route orelse return 1, event, pointer, length, result orelse return 1) catch |err| return errorCode(err);
    return 0;
}
pub export fn rm_http_common_family_release(storage: ?*anyopaque, route: ?*const host.Route) callconv(.c) i32 {
    const self = familyRun(storage) catch return 1;
    self.borrow(route orelse return 1) catch |err| return errorCode(err);
    defer self.endBorrow();
    self.operation.deinit() catch |err| return errorCode(err);
    return 0;
}
