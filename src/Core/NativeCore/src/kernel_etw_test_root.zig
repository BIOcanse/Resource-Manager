test {
    _ = @import("telemetry/kernel_etw/abi.zig");
    _ = @import("telemetry/kernel_etw/trace_abi.zig");
    _ = @import("telemetry/kernel_etw/interrupt_abi.zig");
    _ = @import("telemetry/kernel_etw/region_allocator.zig");
    _ = @import("telemetry/kernel_etw/residency_tests.zig");
    _ = @import("telemetry/kernel_etw/interrupt_tests.zig");
    _ = @import("telemetry/kernel_etw/decoder_tests.zig");
    _ = @import("telemetry/kernel_etw/path_mapping.zig");
}
