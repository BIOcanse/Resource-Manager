pub const implementation = @import("cpu_features.zig");
const cpu = implementation;

pub export fn rm_cpu_os_features_capture(request: ?*const cpu.Request, output: ?*cpu.Result, output_size: u32) callconv(.c) i32 {
    return cpu.capture(request, output, output_size);
}

// Original injected-reader overloads use this synchronous boundary too. Managed exceptions
// are recorded by their local context and rethrown only after returning from the C ABI.
pub export fn rm_cpu_os_features_capture_with_callbacks(request: ?*const cpu.Request, context: ?*anyopaque, callbacks: ?*const cpu.Callbacks, output: ?*cpu.Result, output_size: u32) callconv(.c) i32 {
    return cpu.captureWithCallbacks(request, context, callbacks, output, output_size);
}
