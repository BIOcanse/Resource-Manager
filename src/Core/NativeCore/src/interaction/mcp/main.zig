//! The MCP process owns framing, correlation and output only. The assembly
//! module `mcp_host` supplies the real P0-client binding, configured limits and
//! one locally leased Ledger workspace. No service owner is linked here.
const std = @import("std");
pub const client_module = @import("rm_invocation_client");
pub const protocol = @import("protocol.zig");
pub const discovery = @import("discovery.zig");
pub const tools = @import("tools.zig");
pub const stdio = @import("stdio.zig");

const win = struct {
    extern "kernel32" fn GetStdHandle(kind: u32) callconv(.winapi) ?*anyopaque;
    extern "kernel32" fn ReadFile(handle: *anyopaque, buffer: [*]u8, length: u32, read: *u32, overlapped: ?*anyopaque) callconv(.winapi) i32;
    extern "kernel32" fn WriteFile(handle: *anyopaque, buffer: [*]const u8, length: u32, written: *u32, overlapped: ?*anyopaque) callconv(.winapi) i32;
    extern "kernel32" fn GetLastError() callconv(.winapi) u32;
    extern "kernel32" fn CreateThread(attributes: ?*anyopaque, stack_size: usize, entry: *const fn (?*anyopaque) callconv(.winapi) u32, argument: ?*anyopaque, flags: u32, id: ?*u32) callconv(.winapi) ?*anyopaque;
    extern "kernel32" fn WaitForSingleObject(handle: *anyopaque, milliseconds: u32) callconv(.winapi) u32;
    extern "kernel32" fn CloseHandle(handle: *anyopaque) callconv(.winapi) i32;
    extern "kernel32" fn GetTickCount64() callconv(.winapi) u64;
    extern "kernel32" fn ExitProcess(code: u32) callconv(.winapi) noreturn;
    // Windows ABI constants, not runtime budget choices.
    const stdin: u32 = @bitCast(@as(i32, -10));
    const stdout: u32 = @bitCast(@as(i32, -11));
    const broken_pipe: u32 = 109;
    const handle_eof: u32 = 38;
    const wait_signalled: u32 = 0;
    const wait_timeout: u32 = 258;
    const infinite: u32 = 0xffffffff;
    const stack_reservation: u32 = 0x00010000;
};

/// The process has one declaration of every effective limit and no fallback
/// values. Assembly binds these to the existing configuration/FP mechanism.
pub const Options = struct {
    protocol_limits: protocol.Limits,
    read_chunk_bytes: usize,
    control_workspace_bytes: usize,
    request_workspace_bytes: usize,
    max_in_flight: usize,
    output_queue_frames: usize,
    max_output_frame_bytes: usize,
    thread_stack_bytes: usize,
    shutdown_timeout_ms: u32,
    discovery: discovery.Options,

    pub fn validate(self: Options) !void {
        if (self.protocol_limits.max_line_bytes == 0 or self.protocol_limits.max_value_bytes == 0 or
            self.protocol_limits.max_depth == 0 or self.protocol_limits.max_tokens == 0 or
            self.read_chunk_bytes == 0 or self.read_chunk_bytes > std.math.maxInt(u32) or
            self.control_workspace_bytes == 0 or self.request_workspace_bytes == 0 or
            self.max_in_flight == 0 or self.output_queue_frames == 0 or
            self.max_output_frame_bytes == 0 or self.thread_stack_bytes == 0 or
            self.shutdown_timeout_ms == win.infinite)
            return error.InvalidLimits;
    }
};

pub const Response = union(enum) {
    result: protocol.Value,
    owned_result: std.json.Parsed(protocol.Value),
    fault: protocol.Fault,
    owned_fault: struct { kind: protocol.FaultKind, data: std.json.Parsed(protocol.Value) },

    pub fn deinit(self: *Response) void {
        switch (self.*) {
            .owned_result => |*value| value.deinit(),
            .owned_fault => |*value| value.data.deinit(),
            else => {},
        }
    }
};
/// MCP2 receives this borrowed endpoint after the writer reaches its stable
/// address. It encodes its declared notifications/results and enqueues here;
/// it must keep each Delivery alive until done and cancel pending deliveries
/// on its own subscription/request cancellation. No second framer or thread.
/// All producers must be stopped/joined by their route/close lifetime before
/// Runtime.shutdown finishes. Attaching output does not advertise capability.
pub const Output = struct {
    writer: *stdio.Writer,

    pub fn enqueue(self: Output, frame: []const u8, delivery: *stdio.Delivery) !void {
        try self.writer.enqueue(frame, delivery);
    }
};
pub const Routing = struct {
    context: ?*anyopaque,
    // Only MCP2 extension routes live here. Synchronous tools below call the
    // common P0 Client directly, never an MCP-specific replacement client.
    execute: ?*const fn (?*anyopaque, std.mem.Allocator, protocol.Request, *stdio.Cancellation) anyerror!Response = null,
    /// EOF: stop this client's subscriptions/edits and release only its owners.
    /// Must be nonblocking; request cancellation scopes remain alive until join.
    close: *const fn (?*anyopaque) void,
    // MCP2 supplies only implemented routes/capabilities. These empty defaults
    // express the current implementation, not a permanent feature prohibition.
    extension_methods: []const []const u8 = &.{},
    extra_capabilities: ?protocol.Value = null,
    notification: ?*const fn (?*anyopaque, protocol.Notification) void = null,
    attach_output: ?*const fn (?*anyopaque, Output) void = null,
};

/// Normal stdio startup does not connect to or launch a service. The host opens
/// the unique client lazily for each actual operation. In-place init/deinit
/// keep Ledger owners at stable addresses; a lease stays local to this run.
/// `mcp_host` is deliberately an assembly dependency, not a fallback backend.
pub fn main(init: std.process.Init.Minimal) void {
    process(init) catch |err| fatal(err);
}

fn process(init: std.process.Init.Minimal) !void {
    const binding = @import("mcp_host");
    var host: binding.Host = undefined;
    try host.init(init);
    defer host.deinit();
    // The lease is local to this execution scope, never a Host/client field.
    var workspace = try host.acquireWorkspace();
    defer _ = workspace.endUse();
    try serve(workspace.allocator(), host.options(), host.client(workspace.allocator()), host.routing());
}

fn fatal(_: anyerror) noreturn {
    // A broken/backpressured stdout cannot carry a reliable response. Exit
    // closes OS-owned scopes; never replay a possibly committed operation.
    // In particular, do not block process teardown on an undrained stderr.
    // Assembly may diagnose before startup, using its own bounded mechanism.
    win.ExitProcess(1);
}

fn validHandle(handle: ?*anyopaque) ?*anyopaque {
    const value = handle orelse return null;
    return if (@intFromPtr(value) == std.math.maxInt(usize)) null else value;
}

fn writeStdout(context: ?*anyopaque, bytes: []const u8) !void {
    const handle = context orelse return error.MissingStdout;
    var offset: usize = 0;
    while (offset < bytes.len) {
        const count: u32 = @intCast(@min(bytes.len - offset, std.math.maxInt(u32)));
        var written: u32 = 0;
        if (win.WriteFile(handle, bytes.ptr + offset, count, &written, null) == 0 or written == 0)
            return error.StdoutClosed;
        offset += written;
    }
}

fn stdoutFailure(_: ?*anyopaque, err: anyerror) void {
    fatal(err);
}

/// Caller owns a bounded, Ledger-derived allocator for the entire serve scope.
/// Each request then has its own fixed region; no shared allocator is touched
/// by workers, and one slow operation never blocks reading another/cancellation.
pub fn serve(allocator: std.mem.Allocator, options: Options, client: client_module.Client, routing: Routing) !void {
    try options.validate();
    const input = validHandle(win.GetStdHandle(win.stdin)) orelse return error.MissingStdin;
    const output = validHandle(win.GetStdHandle(win.stdout)) orelse return error.MissingStdout;
    var runtime = try Runtime.init(allocator, options, client, routing, .{ .context = output, .write_all = writeStdout, .on_failure = stdoutFailure });
    defer runtime.deinit();
    try runtime.writer.start(options.thread_stack_bytes);
    if (routing.attach_output) |attach| attach(routing.context, .{ .writer = &runtime.writer });
    // Once threads exist, all returns go through shutdown. A timeout must exit
    // before unwinding the leased backing storage used by a still-live thread.
    const read_result = runtime.read(input);
    runtime.shutdown() catch |err| fatal(err);
    try read_result;
}

const Slot = struct {
    runtime: *Runtime = undefined,
    memory: []u8 = undefined,
    arena: std.heap.FixedBufferAllocator = undefined,
    parsed: ?protocol.Parsed = null,
    request: ?protocol.Request = null,
    thread: ?*anyopaque = null,
    finished: std.atomic.Value(bool) = .init(false),
    queued: bool = false,
    delivery: stdio.Delivery = .{},
    cancel: stdio.Cancellation = .{},

    fn entry(context: ?*anyopaque) callconv(.winapi) u32 {
        const self: *Slot = @ptrCast(@alignCast(context.?));
        self.execute() catch |err| {
            // A cooperative common-client cancellation may surface as an
            // error. It terminates only this request, never its siblings.
            if (!self.cancel.isRequested()) fatal(err);
        };
        self.cancel.unbind();
        self.finished.store(true, .release);
        return 0;
    }

    fn execute(self: *Slot) !void {
        if (self.cancel.isRequested()) return;
        const runtime = self.runtime;
        const request = self.request.?;
        const allocator = self.arena.allocator();
        var response = runtime.dispatch(allocator, request, &self.cancel) catch |err| {
            if (self.cancel.isRequested()) return;
            // Domain/transport outcomes must arrive explicitly in Response;
            // unexpected adapter errors are protocol failures, not guessed
            // isError values inferred from their names or diagnostic wording.
            if (err == error.OutOfMemory) return err;
            var detail: std.json.ObjectMap = .empty;
            defer detail.deinit(allocator);
            try detail.put(allocator, "adapterError", .{ .string = @errorName(err) });
            const bytes = try protocol.serializeFault(allocator, .{ .kind = .internal_error, .id = request.id, .data = .{ .object = detail } }, runtime.options.max_output_frame_bytes);
            defer allocator.free(bytes);
            try runtime.writer.enqueue(bytes, &self.delivery);
            self.queued = true;
            return;
        };
        defer response.deinit();
        if (self.cancel.isRequested()) return;
        const bytes = switch (response) {
            .result => |result| try protocol.serializeResult(allocator, request.id, result, runtime.options.max_output_frame_bytes),
            .owned_result => |result| try protocol.serializeResult(allocator, request.id, result.value, runtime.options.max_output_frame_bytes),
            .owned_fault => |failure| try protocol.serializeFault(allocator, .{ .kind = failure.kind, .id = request.id, .data = failure.data.value }, runtime.options.max_output_frame_bytes),
            .fault => |failure| blk: {
                var fault = failure;
                fault.id = request.id;
                break :blk try protocol.serializeFault(allocator, fault, runtime.options.max_output_frame_bytes);
            },
        };
        defer allocator.free(bytes);
        try runtime.writer.enqueue(bytes, &self.delivery);
        self.queued = true;
    }
};

const Runtime = struct {
    allocator: std.mem.Allocator,
    options: Options,
    client: client_module.Client,
    routing: Routing,
    writer: stdio.Writer,
    slots: []Slot,
    request_memory: []u8,
    control_memory: []u8,
    frame_memory: []u8,
    read_memory: []u8,

    fn init(allocator: std.mem.Allocator, options: Options, client: client_module.Client, routing: Routing, sink: stdio.Sink) !Runtime {
        var writer = try stdio.Writer.init(allocator, options.output_queue_frames, options.max_output_frame_bytes, sink);
        errdefer writer.deinit();
        const slots = try allocator.alloc(Slot, options.max_in_flight);
        errdefer allocator.free(slots);
        const request_size = std.math.mul(usize, options.max_in_flight, options.request_workspace_bytes) catch return error.InvalidLimits;
        const request_memory = try allocator.alloc(u8, request_size);
        errdefer allocator.free(request_memory);
        const control_memory = try allocator.alloc(u8, options.control_workspace_bytes);
        errdefer allocator.free(control_memory);
        const frame_memory = try allocator.alloc(u8, options.protocol_limits.max_line_bytes);
        errdefer allocator.free(frame_memory);
        const read_memory = try allocator.alloc(u8, options.read_chunk_bytes);
        for (slots, 0..) |*slot, i| {
            slot.* = .{ .memory = request_memory[i * options.request_workspace_bytes ..][0..options.request_workspace_bytes] };
        }
        return .{ .allocator = allocator, .options = options, .client = client, .routing = routing, .writer = writer, .slots = slots, .request_memory = request_memory, .control_memory = control_memory, .frame_memory = frame_memory, .read_memory = read_memory };
    }

    fn deinit(self: *Runtime) void {
        self.writer.deinit();
        self.allocator.free(self.read_memory);
        self.allocator.free(self.frame_memory);
        self.allocator.free(self.control_memory);
        self.allocator.free(self.request_memory);
        self.allocator.free(self.slots);
    }

    fn read(self: *Runtime, input: *anyopaque) !void {
        var framer = stdio.Framer.init(self.frame_memory);
        while (true) {
            var count: u32 = 0;
            if (win.ReadFile(input, self.read_memory.ptr, @intCast(self.read_memory.len), &count, null) == 0) {
                const code = win.GetLastError();
                if (code == win.broken_pipe or code == win.handle_eof) break;
                return error.StdinReadFailed;
            }
            if (count == 0) break;
            try framer.push(self.read_memory[0..count], self, receive);
        }
        try framer.eof(self, receive);
    }

    fn receive(self: *Runtime, frame: stdio.Frame) !void {
        var arena = std.heap.FixedBufferAllocator.init(self.control_memory);
        const allocator = arena.allocator();
        const line = switch (frame) {
            .line => |line| line,
            .oversized, .truncated => return self.sendFault(allocator, .{ .kind = .parse_error }),
        };
        var parsed = try protocol.parse(allocator, line, self.options.protocol_limits);
        defer parsed.deinit();
        switch (parsed.message) {
            .fault => |fault| if (fault.respond) try self.sendFault(allocator, fault),
            .notification => |notification| {
                if (std.mem.eql(u8, notification.method, "notifications/cancelled")) {
                    const id = protocol.objectField(notification.params, "requestId").?;
                    for (self.slots) |*slot| {
                        if (slot.request) |request| if (protocol.idEqual(id, request.id)) slot.cancel.cancel(&slot.delivery);
                    }
                } else if (self.routing.notification) |notify| notify(self.routing.context, notification);
            },
            .request => |request| {
                self.reap();
                var available: ?*Slot = null;
                for (self.slots) |*slot| {
                    if (slot.request) |active| {
                        if (protocol.idEqual(request.id, active.id))
                            return self.sendFault(allocator, .{ .kind = .invalid_request, .id = request.id });
                    } else if (available == null) available = slot;
                }
                const slot = available orelse return self.sendFault(allocator, .{ .kind = .internal_error, .id = request.id });
                slot.runtime = self;
                slot.arena = std.heap.FixedBufferAllocator.init(slot.memory);
                slot.parsed = try protocol.parse(slot.arena.allocator(), line, self.options.protocol_limits);
                errdefer {
                    slot.parsed.?.deinit();
                    slot.parsed = null;
                    slot.request = null;
                }
                slot.request = slot.parsed.?.message.request;
                slot.cancel.reset();
                slot.delivery.reset();
                slot.queued = false;
                slot.finished.store(false, .release);
                slot.thread = win.CreateThread(null, self.options.thread_stack_bytes, Slot.entry, slot, win.stack_reservation, null) orelse return error.ThreadCreationFailed;
            },
        }
    }

    fn sendFault(self: *Runtime, allocator: std.mem.Allocator, fault: protocol.Fault) !void {
        const bytes = try protocol.serializeFault(allocator, fault, self.options.max_output_frame_bytes);
        defer allocator.free(bytes);
        try self.writer.enqueue(bytes, null);
    }

    fn dispatch(self: *Runtime, allocator: std.mem.Allocator, request: protocol.Request, cancel: *stdio.Cancellation) !Response {
        if (std.mem.eql(u8, request.method, "server/discover")) {
            const bytes = try discovery.result(allocator, self.options.discovery, self.routing.extra_capabilities, self.options.max_output_frame_bytes);
            defer allocator.free(bytes);
            const result = try std.json.parseFromSlice(protocol.Value, allocator, bytes, .{ .allocate = .alloc_always, .parse_numbers = false });
            return .{ .owned_result = result };
        }
        const token: client_module.contract.Cancel = .{ .context = cancel, .is_cancelled = cancelled };
        if (std.mem.eql(u8, request.method, "tools/list")) {
            tools.parseListParams(request.params) catch return .{ .fault = .{ .kind = .invalid_params } };
            return self.projectExecution(allocator, try tools.executeList(allocator, self.client, token, self.options.max_output_frame_bytes));
        }
        if (std.mem.eql(u8, request.method, "tools/call")) {
            const params = tools.parseCallParams(request.params) catch return .{ .fault = .{ .kind = .invalid_params } };
            return self.projectExecution(allocator, try tools.executeCall(allocator, self.client, token, params, self.options.max_output_frame_bytes));
        }
        for (self.routing.extension_methods) |method| {
            if (std.mem.eql(u8, method, request.method)) {
                const execute = self.routing.execute orelse return .{ .fault = .{ .kind = .method_not_found } };
                return execute(self.routing.context, allocator, request, cancel);
            }
        }
        return .{ .fault = .{ .kind = .method_not_found } };
    }

    fn projectExecution(self: *Runtime, allocator: std.mem.Allocator, execution: tools.Execution) !Response {
        return switch (execution) {
            .fault => |fault| blk: {
                if (fault.data_json) |bytes| {
                    defer allocator.free(bytes);
                    break :blk .{ .owned_fault = .{ .kind = fault.kind, .data = try std.json.parseFromSlice(protocol.Value, allocator, bytes, .{
                        .allocate = .alloc_always,
                        .parse_numbers = false,
                        .max_value_len = self.options.max_output_frame_bytes,
                    }) } };
                }
                break :blk .{ .fault = .{ .kind = fault.kind } };
            },
            .result => |bytes| blk: {
                defer allocator.free(bytes);
                break :blk .{ .owned_result = try std.json.parseFromSlice(protocol.Value, allocator, bytes, .{
                    .allocate = .alloc_always,
                    .parse_numbers = false,
                    .max_value_len = self.options.max_output_frame_bytes,
                }) };
            },
        };
    }

    fn cancelled(context: ?*anyopaque) bool {
        const cancel: *stdio.Cancellation = @ptrCast(@alignCast(context.?));
        return cancel.isRequested();
    }

    fn reap(self: *Runtime) void {
        for (self.slots) |*slot| {
            if (slot.thread == null or !slot.finished.load(.acquire)) continue;
            if (slot.queued and !slot.delivery.isDone()) continue;
            if (win.WaitForSingleObject(slot.thread.?, 0) != win.wait_signalled) continue;
            _ = win.CloseHandle(slot.thread.?);
            slot.thread = null;
            slot.parsed.?.deinit();
            slot.parsed = null;
            slot.request = null;
        }
    }

    // RuntimeRead: Sampled
    fn remaining(self: *Runtime, start: u64) u32 {
        const elapsed = win.GetTickCount64() -| start;
        if (elapsed >= self.options.shutdown_timeout_ms) return 0;
        return self.options.shutdown_timeout_ms - @as(u32, @intCast(elapsed));
    }

    // RuntimeRead: Sampled
    fn shutdown(self: *Runtime) !void {
        const start = win.GetTickCount64();
        for (self.slots) |*slot| if (slot.thread != null) slot.cancel.cancel(&slot.delivery);
        self.routing.close(self.routing.context);
        for (self.slots) |*slot| {
            const thread = slot.thread orelse continue;
            switch (win.WaitForSingleObject(thread, self.remaining(start))) {
                win.wait_signalled => {},
                win.wait_timeout => return error.ShutdownTimedOut,
                else => return error.ThreadWaitFailed,
            }
            _ = win.CloseHandle(thread);
            slot.thread = null;
        }
        // Delivery pointers remain alive until every queued entry is consumed.
        try self.writer.finish(self.remaining(start));
        for (self.slots) |*slot| {
            if (slot.parsed) |*parsed| parsed.deinit();
            slot.parsed = null;
            slot.request = null;
        }
    }
};
