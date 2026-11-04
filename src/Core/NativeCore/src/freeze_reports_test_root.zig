test {
    _ = @import("platform/windows/event_log/source.zig");
    _ = @import("reporting/freeze_recorder/root.zig");
    _ = @import("reporting/system_events/entries.zig");
    _ = @import("reporting/system_events/abi.zig");
    _ = @import("reporting/system_events/october_tests.zig");
}
