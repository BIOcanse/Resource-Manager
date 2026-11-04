pub const diagnostics_windows = @import("telemetry/kernel_etw/thread_diagnostics_windows.zig");

test {
    _ = @import("telemetry/kernel_etw/thread_diagnostics.zig");
}
