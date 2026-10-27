const control = @import("rm_control");
pub const process_name = "fixture";
pub const registry: control.declarations.Registry = .{
    .process = process_name,
    .version = "fixture-v1",
    .blocks = &.{ .{ .uid = 1, .name = "Program" }, .{ .uid = 2, .name = "Worker" } },
    .items = &.{},
    .conditions = &.{.{ .uid = 3, .name = "Wanted", .node = 1, .per_block = false, .items = &.{}, .conditions = &.{}, .children = &.{}, .decides = 2 }},
};
pub const profiles: []const control.export_tree.Profile = &.{.{ .name = "normal", .tree = .{
    .program = 1,
    .blocks = &.{ .{ .uid = 1, .parent = null }, .{ .uid = 2, .parent = 1 } },
    .uses = &.{},
    .computes = &.{},
} }};
