const reader = @import("kernel_leak_attribution.zig");
pub const implementation = reader;
pub export fn rm_kernel_leak_attribution_workspace_bytes(limits: *const reader.Limits) callconv(.c) usize {
    return reader.workspaceBytes(limits.*);
}
pub export fn rm_kernel_leak_attribution_required(context: ?*anyopaque, query: reader.QueryCallback, count: usize, limits: *const reader.Limits, memory: [*]align(8) u8, length: usize) callconv(.c) usize {
    return reader.required(context, query, count, limits.*, memory[0..length]);
}
pub export fn rm_kernel_leak_attribution_capture(context: ?*anyopaque, query: reader.QueryCallback, sink: reader.SinkCallback, count: usize, limits: *const reader.Limits, memory: [*]align(8) u8, length: usize) callconv(.c) i32 {
    return reader.capture(context, query, sink, count, limits.*, memory[0..length]);
}
pub export fn rm_kernel_leak_attribution_compose(facts: *const reader.Facts, maximum_text: u32, output: *reader.Metadata) callconv(.c) void {
    reader.compose(facts, maximum_text, output);
}
