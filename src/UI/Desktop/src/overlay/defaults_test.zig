//! The resident draws exactly the entry the backend settings hold: the
//! backend normalizer expands omitted fields from its declared defaults
//! (freedom point monitoring/performance_overlay), so a default entry arrives
//! complete and the resident adds nothing. This entry spells out today's
//! declared defaults as the backend sends them.
const std = @import("std");
const testing = std.testing;
const layout = @import("layout.zig");
const model = @import("model.zig");
const render = @import("render.zig");
const text = @import("text.zig");

fn load(presentations: *model.Presentations, scratch: []u8, json: []const u8) !void {
    var arena = std.heap.FixedBufferAllocator.init(scratch);
    const document = try std.json.parseFromSliceLeaky(std.json.Value, arena.allocator(), json, .{});
    try presentations.load(document.object.get("performanceOverlay"));
}

const Expected = struct { kind: model.Kind, id: []const u8 };
const order = [_]Expected{
    .{ .kind = .frames, .id = "frames.fps" },
    .{ .kind = .frames, .id = "frames.low1" },
    .{ .kind = .frames, .id = "frames.present" },
    .{ .kind = .software, .id = "cpu.usage" },
    .{ .kind = .software, .id = "gpu.usage" },
};

test "a backend default entry renders as it arrives" {
    const storage = try testing.allocator.alloc(u8, 1 << 20);
    defer testing.allocator.free(storage);
    var presentations = model.Presentations.init(storage[0 .. 1 << 19]);
    try load(&presentations, storage[1 << 19 ..],
        \\{"performanceOverlay":{"software":{
        \\ "fixture.app":{"enabled":true,"mode":"external","items":[
        \\   {"kind":"frames","id":"frames.fps","source":"auto","intervalMs":null,"syncPrecision":"mid"},
        \\   {"kind":"frames","id":"frames.low1","source":"auto","intervalMs":null,"syncPrecision":"mid"},
        \\   {"kind":"frames","id":"frames.present","source":"auto","intervalMs":null,"syncPrecision":"mid"},
        \\   {"kind":"software","id":"cpu.usage","source":"auto","intervalMs":null,"syncPrecision":"mid"},
        \\   {"kind":"software","id":"gpu.usage","source":"auto","intervalMs":null,"syncPrecision":"mid"}],
        \\   "sizeMode":"absolutePixels","fontSizePx":14,"regionWidthRatio":0.25,"regionHeightRatio":0.2,"anchor":"topLeft","marginPx":12}
        \\}}}
    );
    const presentation = presentations.find("FIXTURE.APP").?;
    try testing.expect(presentation.enabled);
    try testing.expectEqual(model.Mode.external, presentation.mode);
    try testing.expectEqual(order.len, presentation.items.len);
    for (order, presentation.items) |expected, actual| {
        try testing.expectEqual(expected.kind, actual.kind);
        try testing.expectEqualStrings(expected.id, actual.id);
    }
    try testing.expectEqual(layout.Settings{ .size_mode = .absolute_pixels, .font_size_px = 14, .region_width_ratio = 0.25, .region_height_ratio = 0.2, .anchor = .top_left, .margin_px = 12 }, presentation.settings);

    // Lines follow the stored item order, whatever order the values arrive in.
    const values = [_]model.ItemValue{
        .{ .kind = "metric", .id = "cpu.usage", .value = 999, .unit = "%" },
        .{ .kind = "software", .id = "gpu.usage", .value = 105, .unit = "%" },
        .{ .kind = "software", .id = "cpu.usage", .value = 104, .unit = "%" },
        .{ .kind = "frames", .id = "frames.present", .value = 103, .unit = "ms" },
        .{ .kind = "frames", .id = "frames.low1", .value = 102, .unit = "fps" },
        .{ .kind = "frames", .id = "frames.fps", .value = 101, .unit = "fps" },
    };
    const lines = try testing.allocator.create(model.Lines);
    defer testing.allocator.destroy(lines);
    const invariant: text.NumberFormat = .{};
    lines.* = .{};
    try lines.build(&values, presentation, "en-US", &invariant);
    try testing.expectEqual(order.len, lines.count);
    for (lines.slice(), 0..) |line, index| {
        var value: [8]u8 = undefined;
        try testing.expect(std.mem.indexOf(u8, line, try std.fmt.bufPrint(&value, "10{d}", .{index + 1})) != null);
    }

    // Layout: top-left at the 12 px margin.
    const renderer = try testing.allocator.create(render.Renderer);
    defer testing.allocator.destroy(renderer);
    renderer.init(.{});
    defer renderer.deinit();
    const drawn = (try renderer.render(lines.slice(), 1280, 720, presentation.settings, "en-US", true)).?;
    try testing.expectEqual(@as(i32, 12), drawn.x);
    try testing.expectEqual(@as(i32, 12), drawn.y);
    try testing.expect(drawn.width > 0 and drawn.width < 1280 - 24 and drawn.height > 0 and drawn.height < 720 - 24);
}
