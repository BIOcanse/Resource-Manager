pub const core = @import("core.zig");
pub const windows = @import("windows.zig");

pub export fn rm_crash_dump_keys_plan(previous: ?*const core.Request, desired: ?*const core.Request, has_snapshot: u32, output: ?*core.Plan) callconv(.c) i32 {
    const result = output orelse return -1;
    if (has_snapshot > 1) return -1;
    result.* = core.plan((previous orelse return -1).*, (desired orelse return -1).*, has_snapshot != 0);
    return 0;
}

pub export fn rm_crash_dump_keys_capture(snapshot: ?*core.Snapshot, output: ?*core.Result) callconv(.c) i32 {
    const result = output orelse return -1;
    var registry = windows.Registry.machine();
    result.* = core.capture(registry.adapter(), snapshot orelse return -1);
    return 0;
}

pub export fn rm_crash_dump_keys_apply(desired: ?*const core.Request, baseline: ?*const core.Snapshot, output: ?*core.Result) callconv(.c) i32 {
    const result = output orelse return -1;
    var registry = windows.Registry.machine();
    result.* = core.apply(registry.adapter(), (desired orelse return -1).*, baseline orelse return -1);
    return 0;
}

pub export fn rm_crash_dump_keys_inspect(desired: ?*const core.Request, baseline: ?*const core.Snapshot, output: ?*core.Result) callconv(.c) i32 {
    const result = output orelse return -1;
    var registry = windows.Registry.machine();
    result.* = core.inspect(registry.adapter(), (desired orelse return -1).*, baseline);
    return 0;
}
