//! Dedicated TEST DLL. No product ABI, service, composition or domain owner.
comptime {
    _ = @import("tests/http_common_listener_fixture.zig").rm_http_common_test_run;
    _ = @import("tests/http_common_kestrel_fixture.zig").rm_http_common_kestrel_fixture_borrow;
}
