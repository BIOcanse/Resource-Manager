//! Synchronous C18 binding to the original service owner. Transports retain
//! proof/connection lifetime; this module owns no policy, state or defaults.
const std = @import("std");
pub const contract = @import("rm_invocation_contract").capability;
pub const Caller = struct {
    /// Actual in-process proof supplied by C16, never a request-supplied value.
    /// Borrowed for admission/call only. The transport releases its own proof.
    proof: *anyopaque,
};
pub const Result = struct {
    status: contract.Status,
    payload: struct {
        context: ?*anyopaque,
        write_json: *const fn (?*anyopaque, *std.Io.Writer) anyerror!void,
        release: *const fn (?*anyopaque) void,
    },
};
pub const Sink = struct {
    context: *anyopaque,
    /// Consumes the result, including release on encoding/send failure. This
    /// synchronous call is the entire field borrow; never retain a source view.
    send: *const fn (*anyopaque, Result) anyerror!void,
};
pub const Unary = struct {
    descriptor: contract.Descriptor,
    /// Legacy bridges explicitly defer effect/cancel decisions to their original
    /// owner. Native domain bindings declare read_only/may_commit when known.
    effects: enum { read_only, may_commit, existing_owner },
    cancellation: enum { owner_cooperative, before_entry_only },
    context: *anyopaque,
    evaluate: *const fn (*anyopaque, Caller, contract.Descriptor) anyerror!contract.Status,
    /// Call the original owner once. Deliver its accepted result inside its
    /// real borrow scope. A returned opaque token is not serialized field data.
    call: *const fn (*anyopaque, Caller, contract.Request, contract.Control, Sink) anyerror!void,

    pub const result_lifetime = .synchronous_delivery;

    pub fn admit(self: *const Unary, caller: Caller) !contract.Status {
        return self.evaluate(self.context, caller, self.descriptor);
    }
    /// For transports whose existing dispatcher already called admit. No
    /// re-evaluation or settings IO occurs here; policy is never cached here.
    pub fn invokeAdmitted(self: *const Unary, caller: Caller, request: contract.Request, control: contract.Control, sink: Sink) !void {
        if (!std.mem.eql(u8, request.operation, self.descriptor.operation)) return error.OperationMismatch;
        if (self.descriptor.kind == .subscription) return error.UnarySubscription;
        try control.cancel.check();
        var delivery: Delivery = .{ .target = sink };
        try self.call(self.context, caller, request, control, .{ .context = &delivery, .send = Delivery.send });
        if (delivery.failure) |failure| return failure;
        if (!delivery.sent) return error.ResultMissing;
        // No post-call cancel-to-not-started conversion: effects may be committed.
    }
    /// Returns admission to the transport's existing typed denial mapper. A
    /// denial never invokes the owner or creates a fabricated successful result.
    pub fn invoke(self: *const Unary, caller: Caller, request: contract.Request, control: contract.Control, sink: Sink) !contract.Status {
        if (!std.mem.eql(u8, request.operation, self.descriptor.operation)) return error.OperationMismatch;
        if (self.descriptor.kind == .subscription) return error.UnarySubscription;
        try control.cancel.check();
        const status = try self.admit(caller);
        if (status != .ok) return status;
        try self.invokeAdmitted(caller, request, control, sink);
        return .ok;
    }
};
const Delivery = struct {
    target: Sink,
    sent: bool = false,
    failure: ?anyerror = null,
    fn send(context: *anyopaque, result: Result) !void {
        const self: *Delivery = @ptrCast(@alignCast(context));
        if (self.sent) {
            result.payload.release(result.payload.context);
            self.failure = error.ResultAlreadyDelivered;
            return error.ResultAlreadyDelivered;
        }
        self.sent = true;
        self.target.send(self.target.context, result) catch |err| {
            self.failure = err;
            return err;
        };
    }
};
