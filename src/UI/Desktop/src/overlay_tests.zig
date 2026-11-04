//! Root of `zig build test-overlay`; `zig build test` runs the same artifact.
test {
    _ = @import("overlay/layout.zig");
    _ = @import("overlay/text.zig");
    _ = @import("overlay/model.zig");
    _ = @import("overlay/d2d.zig");
    _ = @import("overlay/section.zig");
    _ = @import("overlay/render.zig");
    _ = @import("overlay/window.zig");
    _ = @import("overlay/root.zig");
    _ = @import("overlay/follow_test.zig");
    _ = @import("overlay/defaults_test.zig");
}
