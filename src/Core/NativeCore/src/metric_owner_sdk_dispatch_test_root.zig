comptime {
    @import("monitoring/metric_owner/sdk_dispatch.zig").exportAnchor();
}
test {
    _ = @import("monitoring/metric_owner/sdk_dispatch_tests.zig");
}
