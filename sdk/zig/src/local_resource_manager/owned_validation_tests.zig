//! Actual source composition entry; selected values are supplied per build.
test {
    _ = @import("uid.zig");
    _ = @import("memory_region.zig");
    _ = @import("allocation_registration.zig");
    _ = @import("memory_resource.zig");
    _ = @import("activity_window.zig");
    _ = @import("fixed_binding.zig");
    _ = @import("release_check.zig");
    _ = @import("owned/sync.zig");
    _ = @import("owned/index_tests.zig");
    _ = @import("owned/index_b2.zig");
    _ = @import("owned/notifications.zig");
    _ = @import("owned/owner_tests.zig");
    _ = @import("owned/allocation_statistics_tests.zig");
    _ = @import("owned/partition_planning.zig");
    _ = @import("owned/partition_owner_tests.zig");
    _ = @import("owned/audit_tests.zig");
    _ = @import("owned/destruction_tests.zig");
    _ = @import("owned/owner_candidate_tests.zig");
    _ = @import("owned/owner_maxima_tests.zig");
    _ = @import("owned/owner_maximum_operations_tests.zig");
    _ = @import("owned/owner_concurrency_tests.zig");
}
