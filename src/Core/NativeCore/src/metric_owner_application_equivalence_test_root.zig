// Isolated test-only linkage. The runner filters to this corpus's test names.
comptime {
    _ = @import("monitoring/metric_owner/application_equivalence_tests.zig");
}
