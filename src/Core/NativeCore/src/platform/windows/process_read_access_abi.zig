pub const access = @import("process_read_access.zig");
pub export fn rm_process_read_access_version() callconv(.c) u32 {
    return access.abi_version;
}
pub export fn rm_process_read_acquire(request: ?*const access.Request, cancelled: ?*const u32, path: ?[*]u16, capacity: u32, capability: ?*access.Capability, result: ?*access.Result) callconv(.c) u32 {
    const output = result orelse return @backingInt(access.Status.invalid_input);
    output.* = .{ .status = .invalid_input, .stage = .input };
    if (request == null or path == null or capability == null) return @backingInt(output.status);
    output.* = access.acquire(request.?, cancelled, path.?[0..capacity], capability.?);
    return @backingInt(output.status);
}
pub export fn rm_process_read_query(capability: ?*const access.Capability, target_exit_observer: usize, cancelled: ?*const u32, path: ?[*]u16, capacity: u32, result: ?*access.Result) callconv(.c) u32 {
    const output = result orelse return @backingInt(access.Status.invalid_input);
    output.* = .{ .status = .invalid_input, .stage = .input };
    if (capability == null or path == null) return @backingInt(output.status);
    output.* = access.query(capability.?, target_exit_observer, cancelled, path.?[0..capacity]);
    return @backingInt(output.status);
}
pub export fn rm_process_read_close(capability: ?*access.Capability, result: ?*access.Result) callconv(.c) u32 {
    const output = result orelse return @backingInt(access.Status.invalid_input);
    output.* = if (capability) |value| access.close(value) else .{ .status = .invalid_input, .stage = .input };
    return @backingInt(output.status);
}
