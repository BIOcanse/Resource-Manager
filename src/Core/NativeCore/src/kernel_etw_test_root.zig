test {
    _ = @import("telemetry/kernel_etw/consumer_drop_regression_tests.zig");
    _ = @import("telemetry/kernel_etw/shared_reset_tests.zig");
    _ = @import("telemetry/kernel_etw/io_replay_abi.zig");
    _ = @import("telemetry/kernel_etw/owned_allocator_test_abi.zig");
    _ = @import("telemetry/kernel_etw/shared_abi.zig");
    _ = @import("telemetry/kernel_etw/shared_owner.zig");
    _ = @import("telemetry/kernel_etw/abi.zig");
    _ = @import("telemetry/kernel_etw/trace_abi.zig");
    _ = @import("telemetry/kernel_etw/interrupt_abi.zig");
    _ = @import("telemetry/kernel_etw/region_allocator.zig");
    _ = @import("telemetry/kernel_etw/residency_tests.zig");
    _ = @import("telemetry/kernel_etw/interrupt_tests.zig");
    _ = @import("telemetry/kernel_etw/decoder_tests.zig");
    _ = @import("telemetry/kernel_etw/path_mapping.zig");
    _ = @import("telemetry/kernel_etw/namespace.zig");
    _ = @import("telemetry/kernel_etw/fileio.zig");
    _ = @import("telemetry/kernel_etw/thread_process_history.zig");
    _ = @import("telemetry/kernel_etw/io_owner_tests.zig");
    _ = @import("telemetry/kernel_etw/shared_replay_tests.zig");
}
