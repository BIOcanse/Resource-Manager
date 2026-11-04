const input = @import("input_adapter.zig");

pub export fn rm_kernel_leak_input_capture(state: *input.State, capture: *const input.Capture,
    context: ?*anyopaque, query: input.Query, sink: input.Sink, result: *input.Result) callconv(.c) void
{
    result.* = input.capture(state, capture.*, context, query, sink);
}
