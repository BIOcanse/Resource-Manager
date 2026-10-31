test {
    _ = @import("tree.zig");
    _ = @import("shell/instance.zig");
    _ = @import("shell/tray.zig");
    _ = @import("backend/root.zig");
    _ = @import("backend/channel.zig");
    _ = @import("preferences.zig");
    _ = @import("text/lookup.zig");
    _ = @import("backend/subscriptions.zig");
    _ = @import("hotkeys/definition.zig");
    _ = @import("hotkeys/force_terminate.zig");
    _ = @import("hotkeys/overlay_toggle.zig");
    _ = @import("hotkeys/task_manager.zig");
    _ = @import("hotkeys/force_terminate_scenario.zig");
    _ = @import("desktop/occlusion.zig");
    _ = @import("desktop/observation.zig");
    _ = @import("desktop/observation_scenario.zig");
    _ = @import("notifications/root.zig");
}
