//! A leaf of P0's connection owner: no accept/connect/framer/global registry,
//! no grant policy, source current, event store, timer or sampler.
const std = @import("std");
pub const contract = @import("rm_subscription_contract");
pub const invocation = @import("rm_invocation_server");
const call = @import("rm_invocation_contract");

/// data must remain valid and internally stable until release (including encode
/// failure). Source loss/end or a terminal source/capacity failure returns .end
/// with its ORIGINAL typed status/reason. Current unavailable is not terminal
/// by itself: H/R returns it in the actual current; records may also carry a
/// nonterminal failure. Throws are host/transport faults and may end the
/// connection without another frame; they are not success or empty values.
pub const Update = struct {
    kind: contract.Kind,
    status: call.Status,
    data: invocation.PayloadView,
};
pub const Registration = struct { status: call.Status, data: invocation.PayloadView };

/// All storage and callbacks belong to the ORIGINAL source/H owner. attach
/// registers publication AND loss before registration/initial is read. Its
/// failure unwinds partial acquisition. On success this local run owns close.
/// The service binding/source validates its declared selector, typed parameters,
/// named holder and explicit frequency through the existing preparation chain;
/// it must use formal P-access/H for this live caller, including partial grants.
/// Generic P0 admission alone is NOT source/object/field authorization.
/// No callback may outlive close: stop entry, drain callbacks, retire only this
/// holder/restricted binding through H, then release. Never stop the whole area
/// owner, another holder, accepted shared capture, or a domain edit session.
pub const Source = struct {
    context: *anyopaque,
    attach: *const fn (*anyopaque, invocation.Caller, call.Request, call.Control) anyerror!Session,
};
pub const Session = struct {
    context: *anyopaque,
    mode: contract.Mode,
    // Original H transaction / source registration, not a made-up grant.
    // Source/P-access refusal is a typed Registration with the original status
    // and reason, not a thrown host error or a successful empty/presence value.
    registration: *const fn (*anyopaque, call.Control) anyerror!Registration,
    // Current: original identity-only wake after registration, even if empty.
    // Consume pending BEFORE the initial read, just as in next; a publication
    // during that read must leave a pending wake. No reset-after-read.
    // Messages: original first message or end (no invented presence payload).
    initial: *const fn (*anyopaque, call.Control) anyerror!Update,
    // Must block on original notification/event/loss and P0 cancel/EOF/stop.
    // Current wakes may coalesce in the ORIGINAL signal, consuming pending
    // BEFORE reading. Events use the source's bounded ordered queue/backpressure
    // or explicit capacity failure. No sequence polling/reset-after-read.
    // Original loss/permission/selection checks take priority over stale pending
    // current: its payload writer must reject a retired binding before encode.
    // `messages` is an original message/record source, never a JSON fallback
    // for a native current whose typed view has not been delivered.
    next: *const fn (*anyopaque, call.Control) anyerror!Update,
    close: *const fn (*anyopaque) void,
};

/// Called by P0 Binding.invoke after its formal admission. Source attach gets
/// the same message-scoped live Caller. Scope and caller remain P0-owned. Each
/// frame is encoded from a local borrowed view, released by P0 before OS send.
pub fn run(scope: *invocation.Scope, caller: invocation.Caller, request: call.Request, source: Source, limits: contract.Limits, clock: call.Clock) !void {
    if (!limits.valid()) return error.InvalidSubscriptionLimits;
    try scope.check();
    const registering: call.Control = .{
        .cancel = scope.control.cancel,
        .deadline_ms = clock.deadline(limits.registration_timeout_ms),
    };
    const session = try source.attach(source.context, caller, request, registering);
    defer session.close(session.context);
    try check(registering, clock);
    const registration = try session.registration(session.context, registering);
    check(registering, clock) catch |err| {
        registration.data.release(registration.data.context);
        return err;
    };
    if (registration.status != .ok) {
        return sendReply(scope, .{ .status = registration.status, .payload = registration.data }, limits, clock);
    }
    try send(scope, .registered, session.mode, .ok, registration.data, limits, clock);
    // Only the waiting lifetime is unbounded. No 3-second forced termination,
    // implicit reconnect or replay. Each send has a fresh explicit deadline.
    const waiting: call.Control = .{ .cancel = scope.control.cancel, .deadline_ms = null };
    var update = try session.initial(session.context, waiting);
    while (true) {
        const ended = update.kind == .end;
        if (!ended and ((session.mode == .current and update.kind != .current) or
            (session.mode == .messages and update.kind != .record)))
        {
            update.data.release(update.data.context);
            return error.InvalidSubscriptionUpdate;
        }
        try send(scope, update.kind, null, update.status, update.data, limits, clock);
        if (ended) return;
        try scope.check();
        update = try session.next(session.context, waiting);
    }
}

const Envelope = struct {
    kind: contract.Kind,
    mode: ?contract.Mode,
    data: invocation.PayloadView,
    fn write(context: ?*anyopaque, writer: *std.Io.Writer) !void {
        const self: *const Envelope = @ptrCast(@alignCast(context.?));
        try writer.writeAll("{\"contract\":");
        try std.json.Stringify.value(contract.version, .{}, writer);
        try writer.writeAll(",\"kind\":");
        try std.json.Stringify.value(@tagName(self.kind), .{}, writer);
        if (self.mode) |mode| {
            try writer.writeAll(",\"mode\":");
            try std.json.Stringify.value(@tagName(mode), .{}, writer);
        }
        try writer.writeAll(",\"data\":");
        try self.data.write_json(self.data.context, writer);
        try writer.writeByte('}');
    }
    fn release(context: ?*anyopaque) void {
        const self: *Envelope = @ptrCast(@alignCast(context.?));
        self.data.release(self.data.context);
    }
};

fn send(scope: *invocation.Scope, kind: contract.Kind, mode: ?contract.Mode, status: call.Status, data: invocation.PayloadView, limits: contract.Limits, clock: call.Clock) !void {
    // Even late/cancelled results must be released, and must not begin encoding
    // or revive a closed binding. Original H/R also checks at its commit gate.
    scope.check() catch |err| {
        data.release(data.context);
        return err;
    };
    var envelope: Envelope = .{ .kind = kind, .mode = mode, .data = data };
    try sendReply(scope, .{ .status = status, .payload = .{
        .context = &envelope,
        .write_json = Envelope.write,
        .release = Envelope.release,
    } }, limits, clock);
}

fn sendReply(scope: *invocation.Scope, reply: invocation.Reply, limits: contract.Limits, clock: call.Clock) !void {
    scope.check() catch |err| {
        reply.payload.release(reply.payload.context);
        return err;
    };
    const previous = scope.control.deadline_ms;
    scope.control.deadline_ms = clock.deadline(limits.send_timeout_ms);
    defer scope.control.deadline_ms = previous;
    try scope.send(reply);
}

fn check(control: call.Control, clock: call.Clock) !void {
    try control.cancel.check();
    if (control.deadline_ms) |deadline| {
        if (clock.now_ms(clock.context) >= deadline) return error.DeadlineExceeded;
    }
}
