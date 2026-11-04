//! H1-kernel: no request DTO/body, one command, exactly {"removedCount":N}.
const std = @import("std");
const host = @import("host.zig");
pub const Source = extern struct {
    context: ?*anyopaque,
    // 0 start, 1 poll, 2 cancel/drain, 3 release; 0 pending, 1 ready,
    // -1 terminal failure. post only enqueues a wake; never resumes inline.
    read: *const fn (?*anyopaque, u32, ?*anyopaque, *const fn (?*anyopaque) callconv(.c) void, *i32) callconv(.c) i32,
};
pub const output_bytes = "{\"removedCount\":2147483647}".len;
fn wake(context: ?*anyopaque) callconv(.c) void {
    const call: *host.Call = @ptrCast(@alignCast(context.?));
    call.wake.notify();
}
fn advance(context: ?*anyopaque, call: *host.Call, event: u32, output: []u8) !host.Progress {
    const source: *const Source = @ptrCast(@alignCast(context orelse return error.SourceUnavailable));
    var count: i32 = 0;
    const result = source.read(source.context, event, call, wake, &count);
    if (result == 0) return .pending;
    if (call.isCancelled()) return .done;
    // Old unhandled command exceptions are an empty HTTP500, without JSON or
    // content type. Preserve it instead of the common internal-error envelope.
    if (result < 0) return .{ .output = .{ .status = 500, .body = &.{}, .content_type = null } };
    if (count < 0) return error.InvalidResult;
    const bytes = std.fmt.bufPrint(output, "{{\"removedCount\":{d}}}", .{count}) catch return error.OutputLimit;
    return .{ .output = .{ .status = 200, .body = bytes } };
}
fn begin(context: ?*anyopaque, _: host.Request, call: *host.Call, output: []u8) !host.Progress {
    return advance(context, call, 0, output);
}
fn step(context: ?*anyopaque, call: *host.Call, event: host.Event, output: []u8) !host.Progress {
    return advance(context, call, if (event == .cancel or call.isCancelled()) 2 else 1, output);
}
fn release(context: ?*anyopaque, call: *host.Call) void {
    const source: *const Source = @ptrCast(@alignCast(context.?));
    var count: i32 = 0;
    _ = source.read(source.context, 3, call, wake, &count);
}
pub fn route(source: ?*const Source) host.Route {
    return .{ .method = "POST", .template = "/api/kernel-leaks/history/clear", .requirement = .normal_action, .handler = .{ .family = .{ .context = @constCast(source), .capacity = .{ .state_bytes = 0, .output_bytes = output_bytes }, .begin = begin, .step = step, .release = release } } };
}
pub export fn rm_http_kernel_history_borrow(source: ?*const Source, context: ?*anyopaque, consume: ?*const fn (?*anyopaque, *const host.Route) callconv(.c) i32) callconv(.c) i32 {
    const descriptor = route(source);
    return (consume orelse return 1)(context, &descriptor);
}
