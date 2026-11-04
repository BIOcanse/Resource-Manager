//! Focused validation composes the current product exports with F3/F4 while
//! maintenance owns registration in abi.zig. This is not a product build root.
comptime {
    _ = @import("abi.zig");
    _ = @import("reporting/system_events/abi.zig");
}
