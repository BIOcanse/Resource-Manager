const std = @import("std");
const e = @import("export_tree.zig");
const d = @import("declarations.zig");
const t = @import("tree.zig");
const Writer = @import("json.zig").Writer;

const registry: d.Registry = .{
    .version = "fixture-v1",
    .process = "desktop",
    .blocks = &.{ .{ .uid = 1, .name = "Program" }, .{ .uid = 2, .name = "Frontend" }, .{ .uid = 3, .name = "WindowProcess" }, .{ .uid = 4, .name = "Tray" } },
    .items = &.{.{ .uid = 10, .name = "Wanted", .storage = .own, .value_type = .bool, .per_block = true, .trigger = .event, .owner_block = null }},
    .conditions = &.{
        .{ .uid = 20, .name = "AlwaysWanted", .node = 1, .per_block = false, .items = &.{}, .conditions = &.{21}, .children = &.{2}, .decides = 2 },
        .{ .uid = 21, .name = "Lower", .node = 1, .per_block = false, .items = &.{.{ .item = 10, .block = 2 }}, .conditions = &.{}, .children = &.{}, .decides = null },
        .{ .uid = 22, .name = "ItemIsTrue", .node = null, .per_block = true, .items = &.{.{ .item = 10, .block = null }}, .conditions = &.{}, .children = &.{}, .decides = null },
    },
};
const full: t.Tree = .{ .program = 1, .blocks = &.{
    .{ .uid = 1, .parent = null },                                             .{ .uid = 2, .parent = 1 },
    .{ .uid = 3, .parent = 2, .inputs = &.{2}, .launches_process = "window" }, .{ .uid = 4, .parent = 2 },
}, .uses = &.{ .{ .node = 2, .condition = 22, .child = 3 }, .{ .node = 2, .condition = 22, .child = 4 } }, .computes = &.{} };
const small: t.Tree = .{ .program = 1, .blocks = full.blocks[0..3], .uses = full.uses[0..1], .computes = &.{} };
const profiles: []const e.Profile = &.{ .{ .name = "normal", .tree = full }, .{ .name = "minimal", .tree = small } };

fn parse() !std.json.Parsed(std.json.Value) {
    var diagnostic: ?[]const u8 = null;
    var measure = Writer.init(&.{});
    try e.write(registry, "desktop", profiles, &measure, &diagnostic);
    const bytes = try std.testing.allocator.alloc(u8, measure.len);
    defer std.testing.allocator.free(bytes);
    var out = Writer.init(bytes);
    try e.write(registry, "desktop", profiles, &out, &diagnostic);
    try std.testing.expectEqual(measure.len, out.len);
    return std.json.parseFromSlice(std.json.Value, std.testing.allocator, bytes, .{ .allocate = .alloc_always });
}
test "export named node/per-block conditions and effective item keys without computes" {
    const result = try parse();
    defer result.deinit();
    const blocks = result.value.object.get("blocks").?.array.items;
    const front = blocks[1].object.get("declarations").?.array.items[0].object.get("decidingCondition").?;
    try std.testing.expectEqualStrings("AlwaysWanted", front.object.get("name").?.string);
    try std.testing.expect(!front.object.get("perBlock").?.bool);
    try std.testing.expectEqualStrings("Lower", front.object.get("conditions").?.array.items[0].object.get("name").?.string);
    const window = blocks[2].object.get("declarations").?.array.items[0];
    const deciding = window.object.get("decidingCondition").?;
    try std.testing.expect(deciding.object.get("perBlock").?.bool);
    const item = deciding.object.get("items").?.array.items[0];
    try std.testing.expectEqualStrings("Wanted", item.object.get("name").?.string);
    try std.testing.expectEqualStrings("WindowProcess", item.object.get("block").?.object.get("name").?.string);
    const lower = result.value.object.get("conditions").?.array.items[1];
    try std.testing.expectEqualStrings("Wanted", lower.object.get("items").?.array.items[0].object.get("name").?.string);
    try std.testing.expectEqualStrings("Frontend", window.object.get("inputs").?.array.items[0].object.get("name").?.string);
}
test "export merges profiles once and records profile-only blocks and process launch" {
    const result = try parse();
    defer result.deinit();
    const blocks = result.value.object.get("blocks").?.array.items;
    try std.testing.expectEqual(@as(usize, 4), blocks.len);
    try std.testing.expectEqual(@as(usize, 2), blocks[2].object.get("profiles").?.array.items.len);
    try std.testing.expectEqual(@as(usize, 1), blocks[3].object.get("profiles").?.array.items.len);
    try std.testing.expectEqualStrings("window", blocks[2].object.get("declarations").?.array.items[0].object.get("launchesProcess").?.string);
    try std.testing.expectEqual(@as(usize, 2), blocks[1].object.get("children").?.array.items.len);
}
test "export rejects changed parent with the block name before writing" {
    var moved = full.blocks[0..4].*;
    moved[3].parent = 1;
    var changed = full;
    changed.blocks = &moved;
    var diagnostic: ?[]const u8 = null;
    var out = Writer.init(&.{});
    try std.testing.expectError(error.ParentConflict, e.write(registry, "desktop", &.{ profiles[0], .{ .name = "moved", .tree = changed } }, &out, &diagnostic));
    try std.testing.expectEqualStrings("Tray", diagnostic.?);
    try std.testing.expectEqual(@as(usize, 0), out.len);
}
test "export rejects release declarations and asks for development table" {
    const release = comptime blk: {
        var r = registry;
        var blocks = registry.blocks[0..4].*;
        blocks[0].name = null;
        const frozen = blocks;
        r.blocks = &frozen;
        break :blk r;
    };
    var diagnostic: ?[]const u8 = null;
    var out = Writer.init(&.{});
    try std.testing.expectError(error.DevelopmentTableRequired, e.write(release, "desktop", profiles, &out, &diagnostic));
    try std.testing.expect(std.mem.indexOf(u8, diagnostic.?, "development") != null);
    try std.testing.expectEqual(@as(usize, 0), out.len);
}
test "export preserves different declarations for the same block in two profiles" {
    var changedBlocks = small.blocks[0..3].*;
    changedBlocks[2].inputs = &.{};
    var changed = small;
    changed.blocks = &changedBlocks;
    var buffer: [16000]u8 = undefined;
    var out = Writer.init(&buffer);
    var diagnostic: ?[]const u8 = null;
    try e.write(registry, "desktop", &.{ profiles[0], .{ .name = "other", .tree = changed } }, &out, &diagnostic);
    const result = try std.json.parseFromSlice(std.json.Value, std.testing.allocator, buffer[0..out.len], .{});
    defer result.deinit();
    const declarations = result.value.object.get("blocks").?.array.items[2].object.get("declarations").?.array.items;
    try std.testing.expectEqual(@as(usize, 1), declarations[0].object.get("inputs").?.array.items.len);
    try std.testing.expectEqual(@as(usize, 0), declarations[1].object.get("inputs").?.array.items.len);
}

test "export rejects a registered block absent from every profile" {
    var diagnostic: ?[]const u8 = null;
    var out = Writer.init(&.{});
    try std.testing.expectError(error.InvalidDeclaration, e.write(registry, "desktop", &.{.{ .name = "minimal", .tree = small }}, &out, &diagnostic));
    try std.testing.expectEqualStrings("Tray", diagnostic.?);
    try std.testing.expectEqual(@as(usize, 0), out.len);
}
