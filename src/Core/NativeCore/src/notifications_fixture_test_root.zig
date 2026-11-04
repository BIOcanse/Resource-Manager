//! Isolated fixture DLL, never a product registration root.
comptime {
    _ = @import("http_common_fixture_test_root.zig");
    _ = @import("tests/notifications_listener_fixture.zig").rm_http_notifications_test_run;
}
