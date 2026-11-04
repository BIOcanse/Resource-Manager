const runtime = @import("runtime.zig");
pub export fn rm_http_runtime_minimum_window() callconv(.c) usize {
    return runtime.minimum_window;
}
pub export fn rm_http_runtime_path(kind: u32, length: ?*usize) callconv(.c) ?[*]const u8 {
    const size = length orelse return null;
    size.* = 0;
    if (kind >= runtime.paths.len) return null;
    size.* = runtime.paths[kind].len;
    return runtime.paths[kind].ptr;
}
pub export fn rm_http_runtime_borrow(kind: u32, source: ?*const runtime.Source, window: usize, context: ?*anyopaque, consume: ?*const fn (?*anyopaque, *const runtime.host.Route) callconv(.c) i32) callconv(.c) i32 {
    if (kind > @backingInt(runtime.Kind.public_browsers)) return 1;
    const route = runtime.route(@fromBackingInt(kind), source, window);
    return (consume orelse return 1)(context, &route);
}
