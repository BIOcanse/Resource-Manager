const protocol = @import("protocol.zig");
const session_module = @import("session.zig");
const ResultCode = @import("../../common/result_codes.zig").ResultCode;

pub export fn rm_software_identity_resolution_abi_version() callconv(.c) u32 {
    return protocol.abi_version;
}

pub export fn rm_software_identity_resolution_create(
    config: ?*const protocol.Config,
    handle: ?*?*anyopaque,
) callconv(.c) i32 {
    const output = handle orelse return code(.invalid_argument);
    output.* = null;
    const session = session_module.Session.create(config orelse return code(.invalid_argument)) catch |err| switch (err) {
        error.InvalidConfiguration => return code(.abi_mismatch),
        else => return code(.out_of_memory),
    };
    output.* = @ptrCast(session);
    return code(.ok);
}

pub export fn rm_software_identity_resolution_destroy(handle: ?*anyopaque) callconv(.c) void {
    const session: *session_module.Session = @ptrCast(@alignCast(handle orelse return));
    session.destroy();
}

pub export fn rm_software_identity_resolution_reconfigure(
    handle: ?*anyopaque,
    config: ?*const protocol.Config,
) callconv(.c) i32 {
    const session: *session_module.Session = @ptrCast(@alignCast(
        handle orelse return code(.invalid_argument),
    ));
    return code(session.reconfigure(config orelse return code(.invalid_argument)));
}

pub export fn rm_software_identity_resolution_query_capacity(
    handle: ?*anyopaque,
    output: ?*protocol.Capacity,
    output_size: u32,
) callconv(.c) i32 {
    if (output_size != @sizeOf(protocol.Capacity)) return code(.abi_mismatch);
    const session: *session_module.Session = @ptrCast(@alignCast(
        handle orelse return code(.invalid_argument),
    ));
    const actual_output = output orelse return code(.invalid_argument);
    actual_output.* = session.capacity();
    return code(.ok);
}

pub export fn rm_software_identity_resolution_replace_policy(
    handle: ?*anyopaque,
    input: ?*const protocol.PolicyReplaceInput,
    policies: ?[*]const protocol.SourcePolicyInput,
    policy_count: u32,
) callconv(.c) i32 {
    const session: *session_module.Session = @ptrCast(@alignCast(
        handle orelse return code(.invalid_argument),
    ));
    return code(session_module.replacePolicy(
        session,
        input orelse return code(.invalid_argument),
        policies,
        policy_count,
    ));
}

pub export fn rm_software_identity_resolution_resolve(
    handle: ?*anyopaque,
    input: ?*const protocol.ResolveInput,
    observations: ?[*]const protocol.ObservationInput,
    observation_count: u32,
    output: ?*protocol.ResolutionOutput,
) callconv(.c) i32 {
    const session: *session_module.Session = @ptrCast(@alignCast(
        handle orelse return code(.invalid_argument),
    ));
    return code(session_module.resolve(
        session,
        input orelse return code(.invalid_argument),
        observations,
        observation_count,
        output orelse return code(.invalid_argument),
    ));
}

pub export fn rm_software_identity_resolution_snapshot(
    handle: ?*anyopaque,
    output: ?*protocol.ResolutionSummary,
) callconv(.c) i32 {
    const session: *session_module.Session = @ptrCast(@alignCast(
        handle orelse return code(.invalid_argument),
    ));
    return code(session.snapshot(output orelse return code(.invalid_argument)));
}

fn code(result: ResultCode) i32 {
    return @intFromEnum(result);
}

comptime {
    _ = protocol.Config;
    _ = protocol.Capacity;
    _ = protocol.SourcePolicyInput;
    _ = protocol.PolicyReplaceInput;
    _ = protocol.ResolveInput;
    _ = protocol.ObservationInput;
    _ = protocol.ResolutionOutput;
    _ = protocol.ResolutionSummary;
}
