//! CLI-only subscription consumption; no connection, reader, sampling or policy
//! implementation. Bind Client to the common P-stream client at CLI1 assembly.
const std = @import("std");
const invocation = @import("rm_invocation_contract");
const pipe = @import("rm_invocation_client");
const output = @import("cli_watch_output");

/// P0 owns the concrete Client/Subscription/Delivery/Completion contract.
/// P-stream installs Client.subscription_binding; an absent binding remains
/// SubscriptionUnavailable. CLI1 maps the scalar completion or original error
/// through its common result/exit policy, including any effect-unknown error.
/// The supplied formatter consumes pipe.Delivery, rendering its original view
/// with Delivery.writeJson (or the common text formatter) while the loan lives.
pub fn run(
    client: *pipe.Client,
    request: invocation.Request,
    writer: *std.Io.Writer,
    formatter: anytype,
    cancel: invocation.Cancel,
) !pipe.SubscriptionCompletion {
    try cancel.check();
    var subscription = try client.subscribe(request, cancel);
    // consume owns the acquired subscription even if cancellation won the race
    // immediately after subscribe returned.
    return consume(&subscription, writer, formatter, cancel);
}

/// Takes ownership of this subscription's scope. Exactly one delivery can be
/// borrowed at a time; flush/release precedes the next wait. The output writer
/// must use CLI1's cancellable I/O with this same Cancel while blocked. A closed
/// output propagates immediately, so no end frame is retried on a broken pipe.
pub fn consume(
    subscription: *pipe.Client.Subscription,
    writer: *std.Io.Writer,
    formatter: anytype,
    cancel: invocation.Cancel,
) !pipe.SubscriptionCompletion {
    defer subscription.deinit();
    while (true) {
        try cancel.check();
        var delivery = try subscription.next(cancel);
        defer delivery.deinit();
        // A delivery returned concurrently with cancellation must still be
        // released, but it must not begin another output operation.
        try cancel.check();
        try output.emit(writer, formatter, delivery.value);
        if (delivery.completion) |completion| return completion;
    }
}
