//! Single connection owner. Platform callbacks perform OS I/O/attestation only;
//! all accept/dispatch/cancel/close state lives here. No frontend imports this.
const std = @import("std");
pub const contract = @import("rm_invocation_contract");
pub const wire = @import("rm_invocation_wire");

pub const Caller = struct {
    // Live, message-scoped platform proof. Never obtained from JSON. The
    // original attester controls token/process lifetime and impersonation.
    context: *anyopaque,
    release: *const fn (*anyopaque) void,
};
pub const PayloadView = struct {
    context: ?*anyopaque,
    write_json: *const fn (?*anyopaque, *std.Io.Writer) anyerror!void,
    release: *const fn (?*anyopaque) void,
};
pub const Reply = struct { status: contract.Status, payload: PayloadView };
pub const Termination = struct {
    context: *anyopaque,
    finish: *const fn (*anyopaque) void,
};

pub const Scope = struct {
    connection: contract.Connection,
    allocator: std.mem.Allocator,
    maximum_frame_bytes: u32,
    control: contract.Control,
    clock: contract.Clock,
    parent_cancel: contract.Cancel,
    cancelled_flag: std.atomic.Value(bool) = .init(false),
    termination: ?Termination = null,
    termination_registered: bool = false,
    dispatched: bool = false,
    response_attempted: bool = false,

    // P-stream registers its original holder's cleanup here. One callback may
    // own its several source borrows; this owner runs it once on every exit.
    pub fn onEnd(self: *Scope, termination: Termination) !void {
        if (self.termination_registered) return error.AlreadyRegistered;
        self.termination_registered = true;
        self.termination = termination;
    }
    pub fn finish(self: *Scope) void {
        if (self.termination) |termination| {
            self.termination = null;
            termination.finish(termination.context);
        }
    }
    pub fn cancel(self: *Scope) void {
        self.cancelled_flag.store(true, .release);
        self.connection.interrupt(self.connection.context);
    }
    pub fn check(self: *Scope) !void {
        try self.control.cancel.check();
    }
    fn cancelled(context: ?*anyopaque) bool {
        const self: *Scope = @ptrCast(@alignCast(context.?));
        return self.cancelled_flag.load(.acquire) or
            self.parent_cancel.is_cancelled(self.parent_cancel.context) or
            self.connection.is_ended(self.connection.context) or
            (if (self.control.deadline_ms) |deadline| self.clock.now_ms(self.clock.context) >= deadline else false);
    }

    // Encode while the original result view is borrowed. It is released BEFORE
    // any blocking send. No pointer/GCHandle or intermediate report graph goes
    // on the wire. The local allocator must be a leased Ledger workspace.
    pub fn send(self: *Scope, reply: Reply) !void {
        const encoded = try self.encode(reply);
        defer self.allocator.free(encoded.buffer);
        try self.check();
        self.response_attempted = true;
        try wire.writeFrame(self.connection, self.control, self.maximum_frame_bytes, encoded.bytes);
    }
    const Encoded = struct { buffer: []u8, bytes: []const u8 };
    fn encode(self: *Scope, reply: Reply) !Encoded {
        defer reply.payload.release(reply.payload.context);
        const buffer = try self.allocator.alloc(u8, self.maximum_frame_bytes);
        errdefer self.allocator.free(buffer);
        var writer = std.Io.Writer.fixed(buffer);
        try writer.writeAll("{\"status\":");
        try std.json.Stringify.value(reply.status.text(), .{}, &writer);
        try writer.writeAll(",\"payload\":");
        try reply.payload.write_json(reply.payload.context, &writer);
        try writer.writeAll("}");
        return .{ .buffer = buffer, .bytes = writer.buffered() };
    }
};

pub const Binding = struct {
    descriptor: contract.Descriptor,
    context: *anyopaque,
    // Exactly one call into P-access. This callback must use its formal
    // evaluate/evaluateRead/per-process interfaces, not implement a policy here.
    admit: *const fn (*anyopaque, Caller, contract.Descriptor) anyerror!contract.Status,
    // Unary: invoke original owner once, then scope.send borrowed view. A
    // subscription runs its original source until end and uses the SAME scope.
    invoke: *const fn (*anyopaque, Caller, contract.Request, *Scope) anyerror!void,
};

pub const Registry = struct {
    pub const DirectoryPolicy = struct {
        context: *anyopaque,
        evaluate: *const fn (*anyopaque, Caller, contract.Descriptor) anyerror!contract.Status,
    };
    bindings: []const Binding,
    // The built-in list still enters the same P-access leaf. No binding means
    // unavailable, never an implicit normal_read permission bypass.
    directory_policy: ?DirectoryPolicy = null,
    pub fn validate(self: Registry) !void {
        for (self.bindings, 0..) |binding, index| {
            const id = binding.descriptor.operation;
            if (!contract.operationValid(id) or
                std.mem.eql(u8, id, contract.directory_operation)) return error.InvalidRegistration;
            for (self.bindings[0..index]) |earlier| {
                if (std.mem.eql(u8, id, earlier.descriptor.operation)) return error.DuplicateOperation;
            }
        }
    }
    pub fn find(self: Registry, operation: []const u8) ?*const Binding {
        for (self.bindings) |*binding| {
            if (std.mem.eql(u8, operation, binding.descriptor.operation)) return binding;
        }
        return null;
    }
};

// Leases are local to serve(), never stored on Owner/Slot. The original Ledger
// owner provides a bounded allocator; release covers parse/output failures too.
pub const Workspace = struct {
    memory: []u8,
    context: *anyopaque,
    release: *const fn (*anyopaque) void,
};
pub const Platform = struct {
    context: *anyopaque,
    accept: *const fn (*anyopaque, contract.Control) anyerror!contract.Connection,
    // Called after readRequest has consumed the message checked by Windows.
    attest_message: *const fn (*anyopaque, contract.Connection, contract.Control) anyerror!Caller,
    workspace: *const fn (*anyopaque) anyerror!Workspace,
    // OS event/condition only. wait must consume an already signalled event,
    // avoiding lost wakes. No polling or second connection registry.
    wait_capacity: *const fn (*anyopaque, contract.Cancel) anyerror!void,
    signal_capacity: *const fn (*anyopaque) void,
};

pub const Slot = struct {
    thread: ?std.Thread = null,
    connection: ?contract.Connection = null,
    completed: std.atomic.Value(bool) = .init(false),
    cancelled: std.atomic.Value(bool) = .init(false),
    // Protects interrupt versus close, never held during OS I/O or work.
    mutex: std.atomic.Mutex = .unlocked,
    owner: ?*Owner = null,
    fn lock(self: *Slot) void {
        while (!self.mutex.tryLock()) std.atomic.spinLoopHint();
    }
    fn cancel(self: *Slot) void {
        self.cancelled.store(true, .release);
        self.lock();
        defer self.mutex.unlock();
        if (self.connection) |connection| connection.interrupt(connection.context);
    }
    fn isCancelled(context: ?*anyopaque) bool {
        const self: *Slot = @ptrCast(@alignCast(context.?));
        return self.cancelled.load(.acquire) or self.owner.?.stopping.is_cancelled(self.owner.?.stopping.context);
    }
};

pub const Owner = struct {
    registry: Registry,
    platform: Platform,
    limits: contract.Limits,
    clock: contract.Clock,
    stopping: contract.Cancel,

    // slots are bounded storage supplied by assembly, not a second registry.
    // run owns every thread until joined; caller cannot move Owner/slots while
    // it runs. Stop interrupts pending accept via its supplied Cancel contract.
    pub fn run(self: *Owner, slots: []Slot) !void {
        if (!self.limits.valid() or slots.len != self.limits.maximum_connections) return error.InvalidLimits;
        try self.registry.validate();
        for (slots) |*slot| {
            if (slot.thread != null or slot.connection != null) return error.SlotInUse;
        }
        defer self.drain(slots);
        while (true) {
            try self.stopping.check();
            self.reap(slots);
            const slot = findFree(slots) orelse {
                try self.platform.wait_capacity(self.platform.context, self.stopping);
                continue;
            };
            const connection = try self.platform.accept(self.platform.context, .{ .cancel = self.stopping, .deadline_ms = null });
            slot.owner = self;
            slot.connection = connection;
            slot.cancelled.store(false, .release);
            slot.completed.store(false, .release);
            slot.thread = std.Thread.spawn(.{ .stack_size = self.limits.worker_stack_bytes }, worker, .{slot}) catch |err| {
                connection.close(connection.context);
                slot.connection = null;
                return err;
            };
        }
    }
    fn findFree(slots: []Slot) ?*Slot {
        for (slots) |*slot| if (slot.thread == null) return slot;
        return null;
    }
    fn reap(_: *Owner, slots: []Slot) void {
        for (slots) |*slot| {
            if (slot.thread != null and slot.completed.load(.acquire)) {
                slot.thread.?.join();
                slot.thread = null;
                slot.owner = null;
            }
        }
    }
    fn drain(_: *Owner, slots: []Slot) void {
        for (slots) |*slot| if (slot.thread != null) slot.cancel();
        for (slots) |*slot| {
            if (slot.thread) |thread| thread.join();
            slot.thread = null;
            slot.owner = null;
        }
    }
    fn worker(slot: *Slot) void {
        const self = slot.owner.?;
        defer self.platform.signal_capacity(self.platform.context);
        defer slot.completed.store(true, .release);
        defer {
            slot.lock();
            if (slot.connection) |connection| connection.close(connection.context);
            slot.connection = null;
            slot.mutex.unlock();
        }
        self.serve(slot.connection.?, .{ .context = slot, .is_cancelled = Slot.isCancelled }) catch {};
    }

    // Public for the ABI and product harness. Caller retains the connection;
    // the run worker is its only close owner in production.
    pub fn serve(self: *Owner, connection: contract.Connection, cancel: contract.Cancel) !void {
        const workspace = try self.platform.workspace(self.platform.context);
        defer workspace.release(workspace.context);
        var workspace_allocator = std.heap.FixedBufferAllocator.init(workspace.memory);
        const allocator = workspace_allocator.allocator();
        const initial: contract.Control = .{ .cancel = cancel, .deadline_ms = self.clock.deadline(self.limits.request_timeout_ms) };
        var request = wire.readRequest(allocator, connection, initial, self.limits.maximum_frame_bytes) catch |err| {
            if (err != error.Cancelled and err != error.EndOfStream) {
                try wire.writeResponse(allocator, connection, initial, self.limits.maximum_frame_bytes, .{ .status = .invalid_request, .payload = .null });
                try waitPeerClose(connection, initial);
            }
            return err;
        };
        defer request.deinit();
        try cancel.check();
        const caller = try self.platform.attest_message(self.platform.context, connection, initial);
        defer caller.release(caller.context);
        var scope: Scope = .{
            .connection = connection,
            .allocator = allocator,
            .maximum_frame_bytes = self.limits.maximum_frame_bytes,
            .control = .{ .cancel = cancel, .deadline_ms = self.clock.deadline(self.limits.response_timeout_ms) },
            .clock = self.clock,
            .parent_cancel = cancel,
        };
        scope.control.cancel = .{ .context = &scope, .is_cancelled = Scope.cancelled };
        defer scope.finish();
        if (std.mem.eql(u8, request.value.operation, contract.directory_operation)) {
            if (request.value.payload != .null) return self.status(&scope, .invalid_request);
            const policy = self.registry.directory_policy orelse return self.status(&scope, .unavailable);
            const admission = try policy.evaluate(policy.context, caller, contract.directory_descriptor);
            if (admission != .ok) return self.status(&scope, admission);
            var directory = self.registry;
            try scope.send(.{ .status = .ok, .payload = .{
                .context = &directory,
                .write_json = writeDirectory,
                .release = releaseNothing,
            } });
            return waitPeerClose(connection, scope.control);
        }
        const binding = self.registry.find(request.value.operation) orelse return self.status(&scope, .invalid_request);
        const admission = try binding.admit(binding.context, caller, binding.descriptor);
        if (admission != .ok) return self.status(&scope, admission);
        try scope.check();
        if (binding.descriptor.kind == .subscription) scope.control.deadline_ms = null;
        scope.dispatched = true;
        // No retry: a callback failure after dispatch may follow a committed
        // side effect. The original domain's explicit Cancel is separate.
        binding.invoke(binding.context, caller, request.value, &scope) catch |err| {
            try scope.check();
            if (scope.response_attempted) return err;
            try self.status(&scope, if (binding.descriptor.kind == .current_read) .unavailable else .effect_unknown);
            return err;
        };
        // Preserve the existing one-request close handshake: closing the server
        // immediately after WriteFile can discard a response not yet read.
        // Long subscriptions regain a bounded close deadline only AFTER end.
        if (binding.descriptor.kind == .subscription)
            scope.control.deadline_ms = self.clock.deadline(self.limits.response_timeout_ms);
        try waitPeerClose(connection, scope.control);
    }
    fn status(_: *Owner, scope: *Scope, value: contract.Status) !void {
        try wire.writeResponse(scope.allocator, scope.connection, scope.control, scope.maximum_frame_bytes, .{ .status = value, .payload = .null });
        try waitPeerClose(scope.connection, scope.control);
    }
};

fn waitPeerClose(connection: contract.Connection, control: contract.Control) !void {
    if (connection.is_ended(connection.context)) return;
    var end: [1]u8 = undefined;
    const count = connection.read(connection.context, &end, control) catch |err| {
        if (connection.is_ended(connection.context)) return;
        return err;
    };
    if (count != 0) return error.UnexpectedTrailingData;
}

fn releaseNothing(_: ?*anyopaque) void {}
fn writeDirectory(context: ?*anyopaque, writer: *std.Io.Writer) !void {
    const registry: *const Registry = @ptrCast(@alignCast(context.?));
    try writer.writeAll("{\"contract\":");
    try std.json.Stringify.value(contract.version, .{}, writer);
    // This is an incrementally bound directory, never a claim of all-product
    // coverage. Only the supplied live service bindings appear.
    try writer.writeAll(",\"complete\":false,\"operations\":[");
    for (registry.bindings, 0..) |binding, index| {
        if (index != 0) try writer.writeByte(',');
        const descriptor = binding.descriptor;
        try writer.writeAll("{\"operation\":");
        try std.json.Stringify.value(descriptor.operation, .{}, writer);
        try writer.writeAll(",\"kind\":");
        try std.json.Stringify.value(@tagName(descriptor.kind), .{}, writer);
        try writer.writeAll(",\"admission\":");
        try std.json.Stringify.value(@tagName(descriptor.admission), .{}, writer);
        try writer.writeAll(",\"inputSchema\":");
        try writer.writeAll(descriptor.input_schema_json);
        try writer.writeAll(",\"outputSchema\":");
        try writer.writeAll(descriptor.output_schema_json);
        try writer.writeByte('}');
    }
    try writer.writeAll("]}");
}
