//! Unprivileged Invoke.v1 client. Only contracts and framing are dependencies.
const std = @import("std");
pub const wire = @import("rm_invocation_wire");
pub const contract = @import("rm_invocation_contract");

pub const Result = union(enum) {
    response: wire.OwnedResponse,
    failure: contract.Failure,
    pub fn deinit(self: *Result) void {
        switch (self.*) {
            .response => |*response| response.deinit(),
            .failure => {},
        }
        self.* = undefined;
    }
};

pub const Client = struct {
    pub const Subscription = SubscriptionScope;
    connector: contract.Connector,
    clock: contract.Clock,
    limits: contract.Limits,
    allocator: std.mem.Allocator,
    // Installed by P-stream, which owns subscription semantics. Missing binding
    // is explicit unavailable; there is no invented subscription wire fallback.
    subscription_binding: ?SubscriptionBinding = null,

    pub fn invoke(self: Client, request: contract.Request, cancel: contract.Cancel) Result {
        var scope = self.openScope(cancel) catch |err| return failed(err, .not_started, .transport);
        defer scope.deinit();
        scope.sendRequest(request) catch |err| return failed(err, scope.effect(), if (scope.write_attempted) .transport else .invalid_request);
        const response = scope.readResponse() catch |err| return failed(err, scope.effect(), .invalid_response);
        return .{ .response = response };
    }

    pub fn list(self: Client, cancel: contract.Cancel) Result {
        return self.invoke(.{ .operation = contract.directory_operation, .payload = .null }, cancel);
    }

    pub fn subscribe(self: Client, request: contract.Request, cancel: contract.Cancel) !Subscription {
        const binding = self.subscription_binding orelse return error.SubscriptionUnavailable;
        var scope = try self.openScope(cancel);
        errdefer scope.deinit();
        scope.sendRequest(request) catch |err| return subscriptionError(err, scope.effect());
        // P-stream consumes admission through Scope.readResponse and only then
        // explicitly removes the response deadline for a long subscription.
        const source = binding.open(binding.context, &scope, cancel) catch |err|
            return subscriptionError(err, scope.effect());
        return .{ .scope = scope, .source = source };
    }

    /// Every scope opens and verifies a distinct connection; no multiplexing,
    /// retries or shared cancellation state. Caller must eventually deinit it.
    pub fn openScope(self: Client, cancel: contract.Cancel) !Scope {
        if (!self.limits.valid()) return error.InvalidLimits;
        try cancel.check();
        const control: contract.Control = .{ .cancel = cancel, .deadline_ms = self.clock.deadline(self.limits.request_timeout_ms) };
        const connection = try self.connector.open(self.connector.context, control);
        errdefer connection.close(connection.context);
        connection.verify_server(connection.context) catch return error.ServerIdentity;
        try checkControl(self.clock, control);
        return .{ .connection = connection, .control = control, .allocator = self.allocator, .limits = self.limits, .clock = self.clock };
    }
};

pub const SubscriptionCompletion = struct {
    status: contract.Status,
    failure: ?contract.Failure = null,
};

// This is a client-side view, not a new transport frame. P-stream supplies the
// original complete current/record view while its loan is held. Encoding reads
// that view synchronously; no second current or copied JSON object is required.
pub const Delivery = struct {
    pub const Kind = enum { initial, current, event, terminal };
    kind: Kind,
    status: contract.Status,
    context: ?*anyopaque,
    write_json: *const fn (?*anyopaque, *std.Io.Writer) anyerror!void,
    pub fn writeJson(self: Delivery, writer: *std.Io.Writer) !void {
        try self.write_json(self.context, writer);
    }
};
pub const SourceLoan = struct {
    value: Delivery,
    completion: ?SubscriptionCompletion,
    context: *anyopaque,
    release: *const fn (*anyopaque) void,
};
pub const SubscriptionSource = struct {
    context: *anyopaque,
    // Blocks on the original notification/event path using this same scope.
    // Failed next must return every partial source loan before propagating.
    next: *const fn (*anyopaque, *Scope, contract.Cancel) anyerror!SourceLoan,
    // Releases only this holder/mappings. Called once after all loans return.
    finish: *const fn (*anyopaque, *Scope) void,
};
pub const SubscriptionBinding = struct {
    context: *anyopaque,
    // Failure must return any source state acquired during admission.
    // Scope is borrowed only during open: subscribe moves it into the returned
    // subscription. Never retain this pointer. next/finish receive its current
    // address explicitly; the underlying Connection context remains stable.
    open: *const fn (*anyopaque, *Scope, contract.Cancel) anyerror!SubscriptionSource,
};

pub const SubscriptionScope = struct {
    pub const Completion = SubscriptionCompletion;
    pub const Loan = struct {
        value: Delivery,
        completion: ?Completion,
        source: ?SourceLoan,
        owner: *SubscriptionScope,
        pub fn deinit(self: *Loan) void {
            if (self.source) |source| {
                self.source = null;
                source.release(source.context);
                self.owner.loan_outstanding = false;
            }
        }
    };
    scope: Scope,
    source: SubscriptionSource,
    loan_outstanding: bool = false,
    terminal: bool = false,
    finished: bool = false,
    cancelled: std.atomic.Value(bool) = .init(false),

    // Single consumer. Do not move/destroy the subscription while next or its
    // returned Loan is active. One outstanding loan provides backpressure: next
    // cannot advance until the original value has been rendered and released.
    pub fn next(self: *SubscriptionScope, next_cancel: contract.Cancel) !Loan {
        if (self.finished or self.terminal) return error.EndOfStream;
        if (self.loan_outstanding) return error.LoanOutstanding;
        try self.checkCancellation(next_cancel);
        const source = self.source.next(self.source.context, &self.scope, next_cancel) catch |err| {
            try self.checkCancellation(next_cancel);
            return err;
        };
        self.checkCancellation(next_cancel) catch |err| {
            source.release(source.context);
            return err;
        };
        self.loan_outstanding = true;
        self.terminal = source.completion != null;
        return .{ .value = source.value, .completion = source.completion, .source = source, .owner = self };
    }
    fn checkCancellation(self: *SubscriptionScope, next_cancel: contract.Cancel) !void {
        if (self.cancelled.load(.acquire)) return error.Cancelled;
        try self.scope.control.cancel.check();
        try next_cancel.check();
    }
    // May run on another thread while next is blocked. The cancellation flag
    // persists for this subscription; interrupt wakes only its connection.
    // Wait for next and cancel to return and release its Loan before finish or
    // deinit. No domain edit or submitted-operation cancellation.
    pub fn cancel(self: *SubscriptionScope) void {
        self.cancelled.store(true, .release);
        self.scope.interrupt();
    }
    pub fn finish(self: *SubscriptionScope) void {
        if (self.finished) return;
        std.debug.assert(!self.loan_outstanding);
        self.finished = true;
        self.source.finish(self.source.context, &self.scope);
    }
    pub fn deinit(self: *SubscriptionScope) void {
        self.finish();
        self.scope.deinit();
    }
};

fn subscriptionError(err: anyerror, effect: contract.Effect) anyerror {
    if (effect == .not_started) return err;
    return switch (err) {
        error.Cancelled => error.CancelledEffectUnknown,
        error.ServerIdentity => error.IdentityEffectUnknown,
        error.InvalidEnvelope, error.InvalidStatus, error.InvalidUtf8 => error.InvalidResponseEffectUnknown,
        else => error.TransportEffectUnknown,
    };
}

pub const Scope = struct {
    connection: contract.Connection,
    control: contract.Control,
    allocator: std.mem.Allocator,
    limits: contract.Limits,
    clock: contract.Clock,
    write_attempted: bool = false,
    closed: bool = false,

    pub fn effect(self: Scope) contract.Effect {
        return if (self.write_attempted) .unknown else .not_started;
    }

    pub fn sendRequest(self: *Scope, request: contract.Request) !void {
        if (self.closed) return error.Closed;
        if (self.write_attempted) return error.AlreadySent;
        // Encoding/cancellation failures here precede every write attempt.
        try checkControl(self.clock, self.control);
        const bytes = try wire.encodeRequest(self.allocator, self.limits.maximum_frame_bytes, request);
        defer self.allocator.free(bytes);
        try checkControl(self.clock, self.control);
        self.write_attempted = true;
        try wire.writeFrame(self.connection, self.control, self.limits.maximum_frame_bytes, bytes);
        self.control.deadline_ms = self.clock.deadline(self.limits.response_timeout_ms);
    }

    /// The initial response keeps the response deadline. An admitted streaming
    /// consumer may explicitly set control.deadline_ms=null; cancellation stays
    /// active. Every returned frame has passed a fresh server identity check.
    /// OwnedResponse outlives the scope because its arena owns every JSON byte.
    pub fn readResponse(self: *Scope) !wire.OwnedResponse {
        if (self.closed) return error.Closed;
        try checkControl(self.clock, self.control);
        const bytes = try wire.readFrame(self.allocator, self.connection, self.control, self.limits.maximum_frame_bytes);
        defer self.allocator.free(bytes);
        self.connection.verify_server(self.connection.context) catch return error.ServerIdentity;
        try checkControl(self.clock, self.control);
        var response = try wire.decodeResponse(self.allocator, bytes);
        errdefer response.deinit();
        try checkControl(self.clock, self.control);
        return response;
    }

    /// This is an I/O interruption, not a persistent cancellation state. Only
    /// this scope's connection is interrupted. The transport must wake
    /// blocked read/write calls; this does not close or cancel sibling scopes.
    /// Keep Scope and its callback context alive until the blocked call returns;
    /// join that call before deinit. Do not race destruction with interrupt.
    pub fn interrupt(self: *Scope) void {
        if (!self.closed) self.connection.interrupt(self.connection.context);
    }

    pub fn deinit(self: *Scope) void {
        if (self.closed) return;
        self.closed = true;
        self.connection.close(self.connection.context);
    }
};

fn checkControl(clock: contract.Clock, control: contract.Control) !void {
    try control.cancel.check();
    if (control.deadline_ms) |deadline| {
        if (clock.now_ms(clock.context) >= deadline) return error.DeadlineExceeded;
    }
}

fn failed(err: anyerror, effect: contract.Effect, fallback: contract.FailureKind) Result {
    const kind: contract.FailureKind = switch (err) {
        error.Cancelled => .cancelled,
        error.ServerIdentity => .identity,
        error.TransportFailure, error.EndOfStream, error.InvalidTransfer, error.DeadlineExceeded, error.Closed => .transport,
        else => fallback,
    };
    return .{ .failure = .{ .kind = kind, .effect = effect } };
}
