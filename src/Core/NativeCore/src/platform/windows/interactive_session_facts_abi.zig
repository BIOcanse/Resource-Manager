const facts = @import("interactive_session_facts.zig");

pub export fn rm_interactive_session_facts_abi_version() callconv(.c) u32 {
    return 1;
}

pub export fn rm_interactive_session_facts_user_sid(
    session_id: u32,
    context: ?*anyopaque,
    receive: ?facts.SidReceiver,
    failure: ?*facts.Failure,
) callconv(.c) facts.Status {
    const output = failure orelse return .invalid_argument;
    output.* = .{};
    const receiver = receive orelse return .invalid_argument;
    var windows: facts.Windows = .{};
    return facts.userSid(&windows, session_id, context, receiver, output);
}

pub export fn rm_interactive_session_facts_active_ids(
    context: ?*anyopaque,
    destination: ?facts.IdDestination,
    failure: ?*facts.Failure,
) callconv(.c) facts.Status {
    const output = failure orelse return .invalid_argument;
    output.* = .{};
    const acquire = destination orelse return .invalid_argument;
    var windows: facts.Windows = .{};
    return facts.activeSessionIds(&windows, context, acquire, output);
}
