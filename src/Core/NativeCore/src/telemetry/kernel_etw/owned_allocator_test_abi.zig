// Test-only controlled intake borrowing the production create_with_allocator
// owner. It creates no duplicate owner, allocator, trace, timer or publication.
const std = @import("std");
const shared = @import("shared_owner.zig");
const p = @import("protocol.zig");

fn owner(handle: ?*anyopaque) ?*shared.Owner {
    return @ptrCast(@alignCast(handle));
}
fn result(err: anyerror) i32 {
    return switch (err) {
        error.OutOfMemory => 7,
        error.CapacityExceeded => 5,
        error.InvalidInput, error.InvalidConfig, error.InvalidLease => 1,
        else => 3,
    };
}
pub export fn rm_kernel_etw_test_owned_drain(handle: ?*anyopaque, tick: i64) callconv(.c) i32 {
    const self = owner(handle) orelse return 1;
    if (tick < 0) return 1;
    self.drain(tick) catch |err| return result(err);
    return self.failure.load(.acquire);
}
pub export fn rm_kernel_etw_test_owned_publish(handle: ?*anyopaque, tick: i64) callconv(.c) i32 {
    const self = owner(handle) orelse return 1;
    if (tick < 0) return 1;
    self.drain(tick) catch |err| return result(err);
    self.publish(tick) catch |err| return result(err);
    return self.failure.load(.acquire);
}
pub export fn rm_kernel_etw_test_owned_cswitch(handle: ?*anyopaque, tick: i64, input: ?*const p.Input) callconv(.c) i32 {
    const self = owner(handle) orelse return 1;
    var value = (input orelse return 1).*;
    if (value.kind != 7 or value.processor < 0 or value.processor > std.math.maxInt(u16)) return 1;
    self.drain(tick) catch |err| return result(err);
    const hooks = self.hooks();
    if (!hooks.enabled(hooks.context, false)) return 0;
    const raw_index: u16 = @intCast(value.processor);
    value.processor = hooks.processor(hooks.context, raw_index) orelse return 0;
    self.capture.apply(&value) catch |err| return result(err);
    hooks.event(hooks.context, .{ .qpc = value.qpc, .processor = raw_index,
        .data = .{ .cswitch = .{ .new_thread_id = @bitCast(value.tid), .old_thread_id = @bitCast(value.old_tid) } } }) catch |err| return result(err);
    return self.failure.load(.acquire);
}
