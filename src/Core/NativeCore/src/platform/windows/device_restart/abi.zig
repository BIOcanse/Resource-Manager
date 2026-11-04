pub const core = @import("core.zig");
pub const windows = @import("windows.zig");

// UTF-16 input length is bounded before a terminating NUL is inspected.
fn input(value: ?[*:0]const u16, capacity: usize) ?[]const u16 {
    const pointer = value orelse return null;
    var length: usize = 0;
    while (length < capacity) : (length += 1) if (pointer[length] == 0) return pointer[0..length];
    return null;
}

pub export fn rm_device_restart(instance_id: ?[*:0]const u16, confirmed: u32, output: ?*core.Result) callconv(.c) u32 {
    const result = output orelse return @intFromEnum(core.Code.invalid_instance_id);
    const id = input(instance_id, core.instance_id_capacity) orelse {
        result.* = .{ .code = .invalid_instance_id };
        return @intFromEnum(result.code);
    };
    result.* = windows.restart(id, confirmed == 1);
    return @intFromEnum(result.code);
}

pub export fn rm_device_resolve_driver(module_name: ?[*:0]const u16, instance_id: ?[*]u16, capacity: u32, output: ?*core.Result) callconv(.c) u32 {
    const result = output orelse return @intFromEnum(core.Code.invalid_module);
    const module = input(module_name, 260) orelse {
        result.* = .{ .code = .invalid_module };
        return @intFromEnum(result.code);
    };
    const destination = instance_id orelse {
        result.* = .{ .code = .buffer_too_small, .detail = 122 };
        return @intFromEnum(result.code);
    };
    result.* = windows.resolveDriver(module, destination[0..capacity]);
    return @intFromEnum(result.code);
}
