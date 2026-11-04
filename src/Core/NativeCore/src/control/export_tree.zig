//! Development-only view of the existing backend declarations; no controller is created.
const control = @import("rm_control");
const source = @import("backend_tree");

pub const process_name = "backend";
// The service registry covers the union, including the service-only update worker.
pub const registry = source.service_registry;
pub const profiles: []const control.export_tree.Profile = &.{
    .{ .name = "full", .tree = source.topology },
    .{ .name = "service", .tree = source.service_topology },
    .{ .name = "normal-readonly", .tree = source.normal_readonly_topology },
};
