//! Startup/OS bridge for P0. Export registration is maintenance-owned.
//! No callback may throw across ABI. All addresses are in-process borrows.
const std = @import("std");
const pipe = @import("rm_invocation");
const c = pipe.contract;
const capability = @import("rm_invocation_directory");
pub const unary_adapter = @import("unary.zig");

pub const Code = enum(i32) { ok = 0, invalid = 1, cancelled = 2, ended = 3, timeout = 4, failed = 5 };
pub const Bytes = extern struct { data: ?[*]const u8, length: usize };
pub const Control = extern struct {
    cancel_context: ?*anyopaque,
    cancelled: *const fn (?*anyopaque) callconv(.c) u32,
    has_deadline: u32,
    deadline_ms: u64,
};
pub const Connection = extern struct {
    context: *anyopaque,
    read: *const fn (*anyopaque, [*]u8, usize, Control, *usize) callconv(.c) i32,
    write: *const fn (*anyopaque, [*]const u8, usize, Control, *usize) callconv(.c) i32,
    verify_server: *const fn (*anyopaque) callconv(.c) i32,
    is_ended: *const fn (*anyopaque) callconv(.c) u32,
    interrupt: *const fn (*anyopaque) callconv(.c) void,
    // Closes OS handle and frees this callback table, after last native use.
    close: *const fn (*anyopaque) callconv(.c) void,
};
pub const Caller = extern struct {
    context: *anyopaque,
    release: *const fn (*anyopaque) callconv(.c) void,
};
pub const Workspace = extern struct {
    data: [*]u8,
    length: usize,
    context: *anyopaque,
    release: *const fn (*anyopaque) callconv(.c) void,
};
pub const Reply = extern struct {
    status: u32,
    context: ?*anyopaque,
    // Encode original C0/GPU view directly into the final frame's remaining
    // bytes. Exactly one synchronous call; no JSON graph or count/re-encode.
    write_json: *const fn (?*anyopaque, [*]u8, usize, *usize) callconv(.c) i32,
    release: *const fn (?*anyopaque) callconv(.c) void,
};
pub const Binding = extern struct {
    operation: Bytes,
    input_schema: Bytes,
    output_schema: Bytes,
    kind: u32,
    admission: u32,
    context: *anyopaque,
    admit: *const fn (*anyopaque, *anyopaque, u32, *u32) callconv(.c) i32,
    invoke: *const fn (*anyopaque, *anyopaque, *const c.Request, Control, *Reply) callconv(.c) i32,
};
pub const Platform = extern struct {
    context: *anyopaque,
    accept: *const fn (*anyopaque, Control, *?*Connection) callconv(.c) i32,
    attest_message: *const fn (*anyopaque, *Connection, Control, *?*Caller) callconv(.c) i32,
    workspace: *const fn (*anyopaque, *?*Workspace) callconv(.c) i32,
    wait_capacity: *const fn (*anyopaque, Control) callconv(.c) i32,
    signal_capacity: *const fn (*anyopaque) callconv(.c) void,
    now_ms: *const fn (*anyopaque) callconv(.c) u64,
    stopping: *const fn (*anyopaque) callconv(.c) u32,
};
pub const Config = extern struct {
    maximum_frame_bytes: u32,
    maximum_connections: usize,
    worker_stack_bytes: usize,
    request_timeout_ms: u32,
    response_timeout_ms: u32,
    // Registered owner's local run lease, held until run returns/join drains.
    state_memory: [*]u8,
    state_memory_length: usize,
};

fn check(code: i32) !void {
    switch (code) {
        0 => {},
        2 => return error.Cancelled,
        3 => return error.EndOfStream,
        4 => return error.Timeout,
        else => return error.PlatformFailed,
    }
}
fn cancelled(context: ?*anyopaque) callconv(.c) u32 {
    const cancellation: *const c.Cancel = @ptrCast(@alignCast(context.?));
    return @intFromBool(cancellation.is_cancelled(cancellation.context));
}
// Only use within a synchronous callback: cancel_context borrows the supplied
// local Cancel for that callback's duration, never retained by the platform.
fn control(value: *const c.Control) Control {
    return .{ .cancel_context = @constCast(&value.cancel), .cancelled = cancelled, .has_deadline = @intFromBool(value.deadline_ms != null), .deadline_ms = value.deadline_ms orelse 0 };
}
fn connection(pointer: *Connection) c.Connection {
    return .{ .context = pointer, .read = read, .write = write, .verify_server = verify, .is_ended = ended, .interrupt = interrupt, .close = close };
}
fn raw(context: *anyopaque) *Connection {
    return @ptrCast(@alignCast(context));
}
fn read(context: *anyopaque, output: []u8, value: c.Control) !usize {
    const conn = raw(context);
    var count: usize = 0;
    try check(conn.read(conn.context, output.ptr, output.len, control(&value), &count));
    if (count > output.len) return error.InvalidTransfer;
    return count;
}
fn write(context: *anyopaque, input: []const u8, value: c.Control) !usize {
    const conn = raw(context);
    var count: usize = 0;
    try check(conn.write(conn.context, input.ptr, input.len, control(&value), &count));
    if (count > input.len) return error.InvalidTransfer;
    return count;
}
fn verify(context: *anyopaque) !void {
    const conn = raw(context);
    try check(conn.verify_server(conn.context));
}
fn ended(context: *anyopaque) bool {
    const conn = raw(context);
    return conn.is_ended(conn.context) != 0;
}
fn interrupt(context: *anyopaque) void {
    const conn = raw(context);
    conn.interrupt(conn.context);
}
fn close(context: *anyopaque) void {
    const conn = raw(context);
    conn.close(conn.context);
}

const Bridge = struct {
    platform: *const Platform,
    fn get(context: *anyopaque) *Bridge {
        return @ptrCast(@alignCast(context));
    }
    fn accept(context: *anyopaque, value: c.Control) !c.Connection {
        const self = get(context);
        var result: ?*Connection = null;
        try check(self.platform.accept(self.platform.context, control(&value), &result));
        return connection(result orelse return error.InvalidConnection);
    }
    fn attest(context: *anyopaque, conn: c.Connection, value: c.Control) !pipe.Caller {
        const self = get(context);
        var result: ?*Caller = null;
        try check(self.platform.attest_message(self.platform.context, raw(conn.context), control(&value), &result));
        // The original platform owns this table through release, not a local
        // copy of its callback pointers. It pins the actual caller token.
        return .{ .context = result orelse return error.InvalidCaller, .release = callerRelease };
    }
    fn callerRelease(context: *anyopaque) void {
        // Platform context is a Caller table whose first two fields are stable.
        const caller: *Caller = @ptrCast(@alignCast(context));
        caller.release(caller.context);
    }
    fn workspace(context: *anyopaque) !pipe.Workspace {
        const self = get(context);
        var result: ?*Workspace = null;
        try check(self.platform.workspace(self.platform.context, &result));
        const space = result orelse return error.InvalidWorkspace;
        return .{ .memory = space.data[0..space.length], .context = space, .release = workspaceRelease };
    }
    fn workspaceRelease(context: *anyopaque) void {
        const space: *Workspace = @ptrCast(@alignCast(context));
        space.release(space.context);
    }
    fn wait(context: *anyopaque, cancel: c.Cancel) !void {
        const self = get(context);
        const value: c.Control = .{ .cancel = cancel, .deadline_ms = null };
        try check(self.platform.wait_capacity(self.platform.context, control(&value)));
    }
    fn signal(context: *anyopaque) void {
        const self = get(context);
        self.platform.signal_capacity(self.platform.context);
    }
    fn now(context: ?*anyopaque) u64 {
        const self = get(context.?);
        return self.platform.now_ms(self.platform.context);
    }
    fn stopping(context: ?*anyopaque) bool {
        const self = get(context.?);
        return self.platform.stopping(self.platform.context) != 0;
    }
};

fn span(value: Bytes) ![]const u8 {
    if (value.data == null) return error.InvalidText;
    return value.data.?[0..value.length];
}
fn admit(context: *anyopaque, caller: pipe.Caller, descriptor: c.Descriptor) !c.Status {
    return evaluate(context, .{ .proof = caller.context }, descriptor);
}
fn evaluate(context: *anyopaque, caller: capability.Caller, descriptor: c.Descriptor) !c.Status {
    const binding: *const Binding = @ptrCast(@alignCast(context));
    var status: u32 = 0;
    const actual: *Caller = @ptrCast(@alignCast(caller.proof));
    try check(binding.admit(binding.context, actual.context, @backingInt(descriptor.admission), &status));
    if (status > @backingInt(c.Status.effect_unknown)) return error.InvalidStatus;
    return @fromBackingInt(status);
}
fn invoke(context: *anyopaque, caller: capability.Caller, request: c.Request, value: c.Control, sink: capability.Sink) !void {
    const binding: *const Binding = @ptrCast(@alignCast(context));
    var reply: Reply = undefined;
    const actual: *Caller = @ptrCast(@alignCast(caller.proof));
    try check(binding.invoke(binding.context, actual.context, &request, control(&value), &reply));
    if (reply.status > @backingInt(c.Status.effect_unknown)) {
        reply.release(reply.context);
        return error.InvalidStatus;
    }
    try sink.send(sink.context, .{ .status = @fromBackingInt(reply.status), .payload = .{
        .context = &reply,
        .write_json = writePayload,
        .release = releasePayload,
    } });
}
fn writePayload(context: ?*anyopaque, writer: *std.Io.Writer) !void {
    const reply: *Reply = @ptrCast(@alignCast(context.?));
    var written: usize = 0;
    const space = writer.unusedCapacitySlice();
    try check(reply.write_json(reply.context, space.ptr, space.len, &written));
    if (written == 0 or written > space.len) return error.InvalidResult;
    writer.advance(written);
}
fn releasePayload(context: ?*anyopaque) void {
    const reply: *Reply = @ptrCast(@alignCast(context.?));
    reply.release(reply.context);
}

// One original-service binding used by the startup ABI. It owns no second
// registry/state/defaults and calls the existing callbacks without conversion.
// Product composition must supply real C15/C16/domain callbacks before cutover.
pub fn serviceBinding(entry: *const Binding) !capability.Unary {
    if (entry.kind >= @backingInt(c.Kind.subscription) or entry.admission > @backingInt(c.Admission.per_selector)) return error.InvalidBinding;
    const operation = try span(entry.operation);
    if (!c.operationValid(operation) or std.mem.eql(u8, operation, c.directory_operation)) return error.InvalidBinding;
    return .{ .descriptor = .{
        .operation = operation,
        .input_schema_json = try span(entry.input_schema),
        .output_schema_json = try span(entry.output_schema),
        .kind = @fromBackingInt(entry.kind),
        .admission = @fromBackingInt(entry.admission),
    }, .effects = .existing_owner, .cancellation = .owner_cooperative, .context = @constCast(entry), .evaluate = evaluate, .call = invoke };
}

pub export fn rm_invocation_owner_run(config: *const Config, platform: *const Platform, source: [*]const Binding, count: usize) callconv(.c) i32 {
    var state = std.heap.FixedBufferAllocator.init(config.state_memory[0..config.state_memory_length]);
    const allocator = state.allocator();
    const bindings = allocator.alloc(pipe.Binding, count) catch return @backingInt(Code.invalid);
    const slots = allocator.alloc(pipe.Slot, config.maximum_connections) catch return @backingInt(Code.invalid);
    const unaries = allocator.alloc(capability.Unary, count) catch return @backingInt(Code.invalid);
    const adapters = allocator.alloc(unary_adapter.Adapter, count) catch return @backingInt(Code.invalid);
    var bound_count: usize = 0;
    var directory_policy: ?pipe.Registry.DirectoryPolicy = null;
    for (slots) |*slot| slot.* = .{};
    for (source[0..count]) |*entry| {
        if (entry.kind >= @backingInt(c.Kind.subscription) or entry.admission > @backingInt(c.Admission.per_selector))
            return @backingInt(Code.invalid); // Native P-stream uses the same owner directly.
        const operation = span(entry.operation) catch return @backingInt(Code.invalid);
        if (std.mem.eql(u8, operation, c.directory_operation)) {
            if (directory_policy != null or entry.admission != @backingInt(c.Admission.normal_read) or
                entry.kind != @backingInt(c.Kind.current_read)) return @backingInt(Code.invalid);
            directory_policy = .{ .context = @constCast(entry), .evaluate = admit };
            continue;
        }
        unaries[bound_count] = serviceBinding(entry) catch return @backingInt(Code.invalid);
        adapters[bound_count] = .{ .unary = &unaries[bound_count] };
        bindings[bound_count] = adapters[bound_count].bind();
        bound_count += 1;
    }
    var bridge: Bridge = .{ .platform = platform };
    var owner: pipe.Owner = .{
        .registry = .{ .bindings = bindings[0..bound_count], .directory_policy = directory_policy },
        .limits = .{ .maximum_frame_bytes = config.maximum_frame_bytes, .maximum_connections = config.maximum_connections, .worker_stack_bytes = config.worker_stack_bytes, .request_timeout_ms = config.request_timeout_ms, .response_timeout_ms = config.response_timeout_ms },
        .clock = .{ .context = &bridge, .now_ms = Bridge.now },
        .stopping = .{ .context = &bridge, .is_cancelled = Bridge.stopping },
        .platform = .{ .context = &bridge, .accept = Bridge.accept, .attest_message = Bridge.attest, .workspace = Bridge.workspace, .wait_capacity = Bridge.wait, .signal_capacity = Bridge.signal },
    };
    owner.run(slots) catch |err| return if (err == error.Cancelled) @backingInt(Code.cancelled) else @backingInt(Code.failed);
    return @backingInt(Code.ok);
}

// Borrowed until the synchronous invoke callback returns. These helpers expose
// only the existing GPU text / crash target inputs, never caller facts.
pub export fn rm_invocation_payload_string(request: *const c.Request, result: *Bytes) callconv(.c) i32 {
    if (request.payload != .string) return @backingInt(Code.invalid);
    result.* = .{ .data = request.payload.string.ptr, .length = request.payload.string.len };
    return @backingInt(Code.ok);
}
pub export fn rm_invocation_crash_target(request: *const c.Request, result: *Bytes) callconv(.c) i32 {
    if (request.payload != .object or request.payload.object.count() != 1) return @backingInt(Code.invalid);
    const id = request.payload.object.get("id") orelse return @backingInt(Code.invalid);
    if (id != .string) return @backingInt(Code.invalid);
    result.* = .{ .data = id.string.ptr, .length = id.string.len };
    return @backingInt(Code.ok);
}
