comptime {
    @import("monitoring/metric_owner/vector_inputs.zig").exportAnchor();
}
test {
    _ = @import("monitoring/metric_owner/vector_tests.zig");
}
