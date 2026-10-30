//! Source-candidate tests; primary queues this root with the SDK ledger_values module.
test {
    _ = @import("local_resource_manager/owned/owner_cache_tests.zig");
    // Preserve the original activity arithmetic independently of the cache.
    _ = @import("local_resource_manager/lease_window_tests.zig");
}
