comptime {
    @import("monitoring/metric_owner/discovery_exports.zig").exportAnchor();
}
test {
    _ = @import("monitoring/metric_owner/discovery_tests.zig");
}
