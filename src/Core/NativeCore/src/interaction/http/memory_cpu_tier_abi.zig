const view = @import("../settings/memory_cpu_tier_view.zig");
const http = @import("memory_cpu_tier.zig");
pub export fn rm_memory_cpu_tier_use_published(has_value: u32) callconv(.c) u32 {
    return @intFromBool(view.usePublished(has_value != 0));
}
pub export fn rm_memory_cpu_tier_encode_stream(context: ?*anyopaque, callbacks: ?*const view.Callbacks, output: ?*const http.OutputCallbacks) callconv(.c) i32 {
    return http.encodeStream(context, callbacks, output);
}
