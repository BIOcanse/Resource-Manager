//! Development-only adapter over the resident's actual root declaration.
const control = @import("rm_control");
const source = @import("desktop_tree");
pub const process_name = "desktop";
pub const registry = source.registry;
pub const profiles: []const control.export_tree.Profile = &.{
    .{ .name = "normal", .tree = source.tree },
};
