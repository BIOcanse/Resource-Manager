//! Validation-only Family. Never registered by production abi.zig.
const std = @import("std");
const host = @import("../interaction/http/host.zig");
pub const Source = extern struct {
    context: ?*anyopaque,
    read: *const fn (?*anyopaque, u32, ?*anyopaque, *const fn (?*anyopaque) callconv(.c) void, *u64, *u32) callconv(.c) i32,
};
const State = struct { offset: u64 = 0, total: u64 = 0, fixed: bool = false, first: bool = true, no_content: bool = false };
fn state(call: *host.Call) *State {
    return @ptrCast(@alignCast(call.state.ptr));
}
fn wake(context: ?*anyopaque) callconv(.c) void {
    const call: *host.Call = @ptrCast(@alignCast(context.?));
    call.wake.notify();
}
fn read(context: ?*anyopaque, call: *host.Call, event: u32) !i32 {
    const source: *const Source = @ptrCast(@alignCast(context.?));
    var flags: u32 = 0;
    const result = source.read(source.context, event, call, wake, &state(call).total, &flags);
    state(call).fixed = flags & 1 != 0;
    state(call).no_content = flags & 2 != 0;
    if (result < 0) return error.InvalidFixtureSource;
    return result;
}
const headers = [_]host.Header{
    .{ .name = "Location", .value = "/__test/receipt/1" },
    .{ .name = "Cache-Control", .value = "no-store, no-cache, must-revalidate" },
    .{ .name = "Pragma", .value = "no-cache" },
    .{ .name = "Expires", .value = "0" },
    .{ .name = "X-Family-Source", .value = "original-owner" },
    .{ .name = "Content-Disposition", .value = "attachment; filename=family.bin" },
};
fn emit(call: *host.Call, output: []u8) !host.Progress {
    const value = state(call);
    const count: usize = @intCast(@min(output.len, value.total - value.offset));
    @memset(output[0..count], 'x');
    value.offset += count;
    const first = value.first;
    value.first = false;
    return .{ .output = .{ .status = if (value.no_content) 204 else 202, .content_type = if (value.no_content) null else "application/octet-stream", .headers = if (first and !value.no_content) &headers else &.{}, .body = output[0..count], .more = value.offset < value.total, .total_length = if (first and value.fixed) value.total else null } };
}
fn begin(context: ?*anyopaque, request: host.Request, call: *host.Call, output: []u8) !host.Progress {
    if (!std.mem.eql(u8, host.parameterRaw(request, "id") orelse return error.InvalidFixtureParameter, "sample")) return error.InvalidFixtureParameter;
    state(call).* = .{};
    if (try read(context, call, 0) == 0) return .pending;
    return emit(call, output);
}
fn step(context: ?*anyopaque, call: *host.Call, event: host.Event, output: []u8) !host.Progress {
    const result = try read(context, call, if (event == .cancel or call.isCancelled()) 2 else 1);
    if (result == 0) return .pending;
    if (call.isCancelled()) return .done;
    return emit(call, output);
}
fn release(context: ?*anyopaque, call: *host.Call) void {
    _ = read(context, call, 3) catch {};
}
pub export fn rm_http_common_kestrel_fixture_borrow(source: ?*const Source, window: usize, context: ?*anyopaque, consume: ?*const fn (?*anyopaque, *const host.Route) callconv(.c) i32) callconv(.c) i32 {
    const binding = host.Route{ .method = "GET", .template = "/__test/family/{id}", .requirement = .anonymous, .handler = .{ .family = .{ .context = @constCast(source), .capacity = .{ .state_bytes = @sizeOf(State), .state_alignment = @alignOf(State), .output_bytes = window }, .begin = begin, .step = step, .release = release } } };
    return (consume orelse return 1)(context, &binding);
}
