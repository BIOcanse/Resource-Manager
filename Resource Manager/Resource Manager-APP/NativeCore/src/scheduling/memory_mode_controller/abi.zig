const protocol = @import("protocol.zig");
const desired_state = @import("desired_state.zig");

pub export fn rm_memory_mode_controller_abi_version() callconv(.c) u32 {
    return protocol.abi_version;
}

pub export fn rm_memory_mode_controller_create(
    config: ?*const protocol.Config,
    handle: ?*?*anyopaque,
) callconv(.c) i32 {
    const output = handle orelse return code(.invalid_argument);
    output.* = null;
    const session = desired_state.Session.create(
        config orelse return code(.invalid_argument),
    ) catch |err| switch (err) {
        error.InvalidConfiguration => return code(.abi_mismatch),
        else => return code(.out_of_memory),
    };
    output.* = @ptrCast(session);
    return code(.ok);
}

pub export fn rm_memory_mode_controller_destroy(handle: ?*anyopaque) callconv(.c) void {
    const session: *desired_state.Session = @ptrCast(@alignCast(handle orelse return));
    session.destroy();
}

pub export fn rm_memory_mode_controller_reconfigure(
    handle: ?*anyopaque,
    config: ?*const protocol.Config,
) callconv(.c) i32 {
    const session: *desired_state.Session = @ptrCast(@alignCast(
        handle orelse return code(.invalid_argument),
    ));
    return code(session.reconfigure(config orelse return code(.invalid_argument)));
}

pub export fn rm_memory_mode_controller_query_capacity(
    handle: ?*anyopaque,
    output: ?*protocol.Capacity,
    output_size: u32,
) callconv(.c) i32 {
    if (output_size != @sizeOf(protocol.Capacity)) return code(.abi_mismatch);
    const session: *desired_state.Session = @ptrCast(@alignCast(
        handle orelse return code(.invalid_argument),
    ));
    const actual_output = output orelse return code(.invalid_argument);
    actual_output.* = session.capacity();
    return code(.ok);
}

pub export fn rm_memory_mode_controller_plan(
    handle: ?*anyopaque,
    envelope: ?*const protocol.GenerationEnvelope,
    software: ?[*]const protocol.SoftwareInput,
    software_capacity: u32,
    software_outputs: ?[*]protocol.DesiredSoftwareOutput,
    software_output_capacity: u32,
    snapshot: ?*protocol.Snapshot,
) callconv(.c) i32 {
    const session: *desired_state.Session = @ptrCast(@alignCast(
        handle orelse return code(.invalid_argument),
    ));
    return code(desired_state.plan(
        session,
        envelope orelse return code(.invalid_argument),
        software,
        software_capacity,
        software_outputs,
        software_output_capacity,
        snapshot orelse return code(.invalid_argument),
    ));
}

fn code(status: protocol.Status) i32 {
    return @intFromEnum(status);
}
