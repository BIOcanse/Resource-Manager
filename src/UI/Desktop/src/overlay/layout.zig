//! OverlayLayout.Calculate: fixed pixel or window ratio sizing, four-corner
//! anchoring and a 6 px padding around the measured text.
const std = @import("std");
const points = @import("desktop_points_values").Bound;

pub const padding = points.overlay_padding_px;
pub const maximum_lines = points.overlay_maximum_items;

pub const SizeMode = enum { absolute_pixels, window_ratio };
pub const Anchor = enum { top_left, top_right, bottom_left, bottom_right };
pub const Settings = struct {
    size_mode: SizeMode,
    font_size_px: i32,
    region_width_ratio: f64,
    region_height_ratio: f64,
    anchor: Anchor,
    margin_px: i32,
};
pub const Size = struct { width: f32, height: f32 };
pub const Rect = struct {
    x: i32 = 0,
    y: i32 = 0,
    width: i32 = 0,
    height: i32 = 0,
};
pub const Result = struct {
    font_size_px: i32 = 0,
    text: Rect = .{},
    background: Rect = .{},
    /// Top of each line; every line starts at text.x.
    line_tops: [maximum_lines]f32 = undefined,
};

/// `measurer.measure(line_index, font_size_px) !Size` measures one line.
pub fn calculate(width: i32, height: i32, settings: Settings, line_count: usize, measurer: anytype) !Result {
    if (points.overlay_ratio_font_minimum_px > points.overlay_ratio_font_maximum_px) return error.InvalidOverlayFontRange;
    if (width <= 0 or height <= 0 or line_count == 0) return .{};
    if (line_count > maximum_lines) return error.TooManyOverlayLines;
    var available_width = @max(1, width - 2 * settings.margin_px);
    var available_height = @max(1, height - 2 * settings.margin_px);
    var font_size: i32 = undefined;
    if (settings.size_mode == .window_ratio) {
        available_width = @min(available_width, @max(1, floorInt(@as(f64, @floatFromInt(width)) * settings.region_width_ratio)));
        available_height = @min(available_height, @max(1, floorInt(@as(f64, @floatFromInt(height)) * settings.region_height_ratio)));
        // The largest size in the declared range whose padded text fits.
        var low: i32 = points.overlay_ratio_font_minimum_px;
        var high: i32 = points.overlay_ratio_font_maximum_px;
        font_size = points.overlay_ratio_font_minimum_px;
        while (low <= high) {
            const middle = low + @divTrunc(high - low, 2);
            const measured = try total(line_count, middle, measurer);
            if (measured.width + 2 * padding <= @as(f32, @floatFromInt(available_width)) and measured.height + 2 * padding <= @as(f32, @floatFromInt(available_height))) {
                font_size = middle;
                low = middle + 1;
            } else high = middle - 1;
        }
    } else font_size = @max(1, settings.font_size_px);
    var result: Result = .{ .font_size_px = font_size };
    var heights: [maximum_lines]f32 = undefined;
    var widest: f32 = 0;
    var sum: f32 = 0;
    var index: usize = 0;
    while (index < line_count) : (index += 1) {
        const size = try measurer.measure(index, font_size);
        heights[index] = size.height;
        widest = @max(widest, size.width);
        sum += size.height;
    }
    const text_width: i32 = @intFromFloat(@ceil(widest));
    const text_height: i32 = @intFromFloat(@ceil(sum));
    const block_width = @min(available_width, text_width + 2 * padding);
    const block_height = @min(available_height, text_height + 2 * padding);
    const right = settings.anchor == .top_right or settings.anchor == .bottom_right;
    const bottom = settings.anchor == .bottom_left or settings.anchor == .bottom_right;
    var x = if (right) width - block_width - settings.margin_px else settings.margin_px;
    var y = if (bottom) height - block_height - settings.margin_px else settings.margin_px;
    x = std.math.clamp(x, 0, @max(0, width - block_width));
    y = std.math.clamp(y, 0, @max(0, height - block_height));
    var top: f32 = @floatFromInt(y + padding);
    index = 0;
    while (index < line_count) : (index += 1) {
        result.line_tops[index] = top;
        top += heights[index];
    }
    result.text = .{ .x = x + padding, .y = y + padding, .width = text_width, .height = text_height };
    result.background = .{ .x = x, .y = y, .width = block_width, .height = block_height };
    return result;
}

fn floorInt(value: f64) i32 {
    const floored = @floor(value);
    if (!(floored >= -2147483648.0 and floored <= 2147483647.0)) return if (floored > 0) std.math.maxInt(i32) else std.math.minInt(i32);
    return @intFromFloat(floored);
}

fn total(line_count: usize, font_size: i32, measurer: anytype) !Size {
    var result: Size = .{ .width = 0, .height = 0 };
    var index: usize = 0;
    while (index < line_count) : (index += 1) {
        const size = try measurer.measure(index, font_size);
        result.width = @max(result.width, size.width);
        result.height += size.height;
    }
    return result;
}

const testing = std.testing;
/// Each line is 0.5 px per character per font pixel wide and 1.25 font pixels tall.
const Fixed = struct {
    characters: []const usize,
    calls: usize = 0,
    fn measure(self: *Fixed, index: usize, font_size: i32) !Size {
        self.calls += 1;
        const size: f32 = @floatFromInt(font_size);
        return .{ .width = @as(f32, @floatFromInt(self.characters[index])) * size * 0.5, .height = size * 1.25 };
    }
};

test "absolute pixel layout anchors each corner with margin and padding" {
    var measurer: Fixed = .{ .characters = &.{ 10, 7 } };
    const settings: Settings = .{ .size_mode = .absolute_pixels, .font_size_px = 14, .region_width_ratio = 0.25, .region_height_ratio = 0.2, .anchor = .top_left, .margin_px = 12 };
    // Width 70, height 2 * 17.5 = 35: block 82 x 47.
    const top_left = try calculate(800, 600, settings, 2, &measurer);
    try testing.expectEqual(@as(i32, 14), top_left.font_size_px);
    try testing.expectEqual(Rect{ .x = 12, .y = 12, .width = 82, .height = 47 }, top_left.background);
    try testing.expectEqual(Rect{ .x = 18, .y = 18, .width = 70, .height = 35 }, top_left.text);
    try testing.expectEqual(@as(f32, 18), top_left.line_tops[0]);
    try testing.expectEqual(@as(f32, 35.5), top_left.line_tops[1]);
    var other = settings;
    other.anchor = .top_right;
    try testing.expectEqual(Rect{ .x = 800 - 82 - 12, .y = 12, .width = 82, .height = 47 }, (try calculate(800, 600, other, 2, &measurer)).background);
    other.anchor = .bottom_left;
    try testing.expectEqual(Rect{ .x = 12, .y = 600 - 47 - 12, .width = 82, .height = 47 }, (try calculate(800, 600, other, 2, &measurer)).background);
    other.anchor = .bottom_right;
    try testing.expectEqual(Rect{ .x = 706, .y = 541, .width = 82, .height = 47 }, (try calculate(800, 600, other, 2, &measurer)).background);
}

test "a small target clamps the block and window ratio picks the largest fitting size" {
    var measurer: Fixed = .{ .characters = &.{20} };
    const small = try calculate(50, 20, .{ .size_mode = .absolute_pixels, .font_size_px = 14, .region_width_ratio = 0.25, .region_height_ratio = 0.2, .anchor = .top_left, .margin_px = 12 }, 1, &measurer);
    // Available 26 x 1 clamps the block; the anchor clamps into the target.
    try testing.expectEqual(Rect{ .x = 12, .y = 12, .width = 26, .height = 1 }, small.background);
    const ratio: Settings = .{ .size_mode = .window_ratio, .font_size_px = 14, .region_width_ratio = 0.25, .region_height_ratio = 0.2, .anchor = .top_left, .margin_px = 0 };
    // Region 200 x 120. Width 10 s + 12 <= 200 and 1.25 s + 12 <= 120: s = 18.
    const fitted = try calculate(800, 600, ratio, 1, &measurer);
    try testing.expectEqual(@as(i32, 18), fitted.font_size_px);
    try testing.expectEqual(@as(i32, 192), fitted.background.width);
    // Nothing fits: size 1 is used.
    const tiny = try calculate(10, 10, ratio, 1, &measurer);
    try testing.expectEqual(@as(i32, 1), tiny.font_size_px);
    try testing.expectEqual(@as(i32, 0), (try calculate(0, 10, ratio, 1, &measurer)).font_size_px);
}
