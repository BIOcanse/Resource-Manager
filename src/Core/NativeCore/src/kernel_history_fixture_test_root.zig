//! TEST-only module root keeps all native imports inside the src/ module path.
comptime {
    _ = @import("tests/kernel_history_listener.zig").rm_kernel_history_test_run;
}
