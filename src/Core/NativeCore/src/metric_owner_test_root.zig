comptime {
    @import("monitoring/metric_owner/root.zig").exportAnchor();
}
test {
    _ = @import("monitoring/metric_owner/root.zig");
}
