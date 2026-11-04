//! P-stream's binding of P0's ONE subscription client. No connection/framer,
//! attester, source current, grant implementation, sampler or extra SDK.
const std = @import("std");
pub const invocation = @import("rm_invocation_contract");
pub const contract = @import("rm_subscription_contract");
pub const pipe = @import("rm_invocation_client");
const wire = @import("rm_invocation_wire");

/// An actual original H/R result, kept only for the lifetime of one returned
/// P0 Loan. Encoding is synchronous; it must not retain its writer or context.
pub const View = struct {
    context: ?*anyopaque,
    write_json: *const fn (?*anyopaque, *std.Io.Writer) anyerror!void,
    release: *const fn (?*anyopaque) void,
};
pub const CurrentLoan = struct { status: invocation.Status, view: View };
pub const CurrentReader = struct {
    context: *anyopaque,
    // Read ACTUAL current from the same authorized physical source using H/R,
    // including typed empty/unavailable. Match service/region/source/version
    // and dependency closure; retain these actual identities in the result.
    // A wake is not its historical snapshot. No sample,
    // sequence polling, JSON current graph or old-epoch fallback.
    // Failed read must release every partially acquired native loan.
    read_current: *const fn (*anyopaque, std.json.Value, invocation.Cancel) anyerror!CurrentLoan,
    close: *const fn (*anyopaque) void,
};
pub const ReaderFactory = struct {
    context: *anyopaque,
    // Apply the original H grant and trusted layout. Missing/malformed or
    // unavailable authorization fails closed. Do not retain borrowed JSON or
    // Cancel/context addresses; this synchronous registration call returns
    // before their storage is released. Failure unwinds partial maps/handles.
    open: *const fn (*anyopaque, std.json.Value, invocation.Cancel) anyerror!CurrentReader,
};

/// Assembly installs binding() on its existing pipe.Client. CLI/MCP keep
/// pipe.Client.Subscription/next/Loan/Completion/finish/deinit unchanged.
/// This Binding/factory context must outlive Client.subscribe's open callback;
/// each successful subscription owns its separate State and original reader.
/// No Reader is needed for original message sources. No substitute native
/// AppSettings implementation or production mock Reader is provided here.
pub const Binding = struct {
    reader_factory: ?ReaderFactory,

    pub fn binding(self: *Binding) pipe.SubscriptionBinding {
        return .{ .context = self, .open = open };
    }

    fn open(context: *anyopaque, scope: *pipe.Scope, cancel: invocation.Cancel) !pipe.SubscriptionSource {
        const self: *Binding = @ptrCast(@alignCast(context));
        var response: ?wire.OwnedResponse = try scope.readResponse();
        defer if (response) |*value| value.deinit();
        try cancel.check();
        if (response.?.value.status != .ok) {
            // Keep the ORIGINAL typed rejection as a terminal loan. It need
            // not be a subscription envelope or have any invented presence.
            const state = try scope.allocator.create(State);
            state.* = .{ .allocator = scope.allocator, .pending_rejection = response.? };
            response = null;
            return state.source();
        }
        // Use P0's public invalid-envelope category at the admission boundary,
        // so its effect-unknown wrapper preserves invalid-response semantics.
        const registration = contract.Frame.parse(response.?.value.payload) catch return error.InvalidEnvelope;
        if (registration.kind != .registered) return error.InvalidEnvelope;
        var reader: ?CurrentReader = null;
        if (registration.mode.? == .current) {
            const factory = self.reader_factory orelse return error.SubscriptionReaderUnavailable;
            reader = try factory.open(factory.context, registration.data, cancel);
        }
        errdefer if (reader) |value| value.close(value.context);
        try cancel.check();
        // Do not clear admission's finite response deadline before the actual
        // grant has been applied. H/R's open is synchronous and non-waiting.
        if (scope.control.deadline_ms) |deadline| {
            if (scope.clock.now_ms(scope.clock.context) >= deadline) return error.DeadlineExceeded;
        }
        const state = try scope.allocator.create(State);
        state.* = .{ .allocator = scope.allocator, .mode = registration.mode, .reader = reader };
        scope.control.deadline_ms = null;
        return state.source();
    }
};

const State = struct {
    allocator: std.mem.Allocator,
    mode: ?contract.Mode = null,
    reader: ?CurrentReader = null,
    pending_rejection: ?wire.OwnedResponse = null,
    first_current: bool = true,
    failed: bool = false,

    fn source(self: *State) pipe.SubscriptionSource {
        return .{ .context = self, .next = next, .finish = finish };
    }
    fn from(context: *anyopaque) *State {
        return @ptrCast(@alignCast(context));
    }
    fn next(context: *anyopaque, scope: *pipe.Scope, cancel: invocation.Cancel) !pipe.SourceLoan {
        const self = from(context);
        if (self.failed) return error.SubscriptionSourceEnded;
        // P0 still owns finish/deinit after a failed next. Invalidate its
        // source binding now, so an explicit later call cannot reuse maps from
        // a cancelled/lost service. Partial output loans unwind before this.
        errdefer self.invalidate();
        // P0 supplies the CURRENT scope address; open must never retain its
        // stack-local Scope pointer because subscribe moves it on return.
        var combined: CombinedCancel = .{ .original = scope.control.cancel, .consumer = cancel };
        const effective = combined.token();
        const previous = scope.control.cancel;
        scope.control.cancel = effective;
        defer scope.control.cancel = previous;
        try effective.check();
        var response = if (self.pending_rejection) |value| blk: {
            self.pending_rejection = null;
            break :blk value;
        } else try scope.readResponse();
        errdefer response.deinit();
        try effective.check();
        const result = try self.allocator.create(Loan);
        errdefer self.allocator.destroy(result);
        result.* = .{ .allocator = self.allocator, .response = response, .payload = response.value.payload };
        errdefer if (result.native) |native| native.view.release(native.view.context);
        var kind: pipe.Delivery.Kind = .terminal;
        var status = response.value.status;
        var completion: ?pipe.SubscriptionCompletion = null;
        if (self.mode == null) {
            // Only a buffered admission refusal has no source mode.
            completion = .{ .status = status };
        } else {
            const frame = try contract.Frame.parse(response.value.payload);
            result.payload = frame.data;
            switch (frame.kind) {
                .registered => return error.InvalidSubscriptionFrame,
                .current => {
                    if (self.mode.? != .current) return error.InvalidSubscriptionFrame;
                    try checkCurrentConnection(scope, effective);
                    const reader = self.reader.?;
                    result.native = try reader.read_current(reader.context, frame.data, effective);
                    try checkCurrentConnection(scope, effective);
                    // Actual current may be newer than a coalesced wake. Its
                    // original H/R result/status is authoritative, including
                    // unavailable; do not freeze a historical wake failure.
                    status = result.native.?.status;
                    kind = if (self.first_current) .initial else .current;
                    self.first_current = false;
                },
                .record => {
                    if (self.mode.? != .messages) return error.InvalidSubscriptionFrame;
                    kind = .event;
                },
                .end => completion = .{ .status = status },
            }
        }
        try effective.check();
        if (completion != null) self.invalidate();
        return .{
            .value = .{ .kind = kind, .status = status, .context = result, .write_json = Loan.write },
            .completion = completion,
            .context = result,
            .release = Loan.release,
        };
    }
    fn finish(context: *anyopaque, _: *pipe.Scope) void {
        // P0 calls this once, AFTER its sole output loan is returned. The
        // original H/R owner closes only this subscription's maps/binding.
        const self = from(context);
        if (self.reader) |reader| reader.close(reader.context);
        if (self.pending_rejection) |*response| response.deinit();
        const allocator = self.allocator;
        allocator.destroy(self);
    }
    fn invalidate(self: *State) void {
        self.failed = true;
        if (self.reader) |reader| reader.close(reader.context);
        self.reader = null;
        if (self.pending_rejection) |*response| response.deinit();
        self.pending_rejection = null;
    }
};

// This is one output loan, not owner-held workspace/lease state. P0's returned
// Loan releases it after actual consumption; it never enters a second queue.
const Loan = struct {
    allocator: std.mem.Allocator,
    response: wire.OwnedResponse,
    payload: std.json.Value,
    native: ?CurrentLoan = null,
    fn write(context: ?*anyopaque, writer: *std.Io.Writer) !void {
        const self: *const Loan = @ptrCast(@alignCast(context.?));
        if (self.native) |native| return native.view.write_json(native.view.context, writer);
        // Message data is already owned by the original P0 parsed response.
        // Encode directly into the caller's final output; no intermediate DTO.
        try std.json.Stringify.value(self.payload, .{}, writer);
    }
    fn release(context: *anyopaque) void {
        const self: *Loan = @ptrCast(@alignCast(context));
        if (self.native) |native| native.view.release(native.view.context);
        self.response.deinit();
        const allocator = self.allocator;
        allocator.destroy(self);
    }
};

fn checkCurrentConnection(scope: *pipe.Scope, cancel: invocation.Cancel) !void {
    try cancel.check();
    if (scope.connection.is_ended(scope.connection.context)) return error.EndOfStream;
    // Call P0's original verification; no identity or permission policy here.
    scope.connection.verify_server(scope.connection.context) catch return error.ServerIdentity;
    try cancel.check();
}

const CombinedCancel = struct {
    original: invocation.Cancel,
    consumer: invocation.Cancel,
    fn token(self: *CombinedCancel) invocation.Cancel {
        return .{ .context = self, .is_cancelled = cancelled };
    }
    fn cancelled(context: ?*anyopaque) bool {
        const self: *const CombinedCancel = @ptrCast(@alignCast(context.?));
        return self.original.is_cancelled(self.original.context) or self.consumer.is_cancelled(self.consumer.context);
    }
};
