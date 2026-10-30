//! Source-candidate regression gate for scoped leases and bounded diagnostics.
test {
    _ = @import("local_resource_manager/lease_window_tests.zig");
    _ = @import("local_resource_manager/owned/owner_tests.zig");
    _ = @import("local_resource_manager/owned/partition_owner_tests.zig");
    _ = @import("local_resource_manager/owned/destruction_tests.zig");
    _ = @import("local_resource_manager/owned/allocation_statistics_tests.zig");
}
