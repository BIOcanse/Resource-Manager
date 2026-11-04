//! Draws overlay lines with Direct2D and DirectWrite into a premultiplied BGRA
//! DIB: Segoe UI in pixel units, white grayscale-antialiased text, a black
//! background of alpha 168 over the layout's background bounds, text clipped
//! to those bounds (OverlayBitmapRenderer.Render).
const std = @import("std");
const points = @import("desktop_points_values").Bound;
const c = @import("../backend/c.zig").win;
const d2d = @import("d2d.zig");
const layout = @import("layout.zig");
const section = @import("section.zig");

fn color(value: i32) f32 {
    return @as(f32, @floatFromInt(value)) / 255;
}

pub const maximum_dimension = points.overlay_maximum_dimension;
pub const maximum_line_units = points.overlay_maximum_line_units;

/// Freedom points overlay_render_target and overlay_text_rendering_mode.
pub const Target = enum { software, hardware };
/// DWRITE_RENDERING_MODE values; `default` uses the system rendering parameters.
pub const TextMode = enum(u32) { default = 0, aliased = 1, gdi_classic = 2, gdi_natural = 3, natural = 4, natural_symmetric = 5 };
pub const Options = struct {
    target: Target = @fromBackingInt(@intCast(@backingInt(points.overlay_render_target))),
    text_mode: TextMode = @fromBackingInt(@intCast(@backingInt(points.overlay_text_rendering_mode))),
};

/// A rendered region of the target: the whole target, or only the background
/// bounds when cropped. Pixels stay valid until the next render or deinit.
pub const Image = struct {
    x: i32,
    y: i32,
    width: i32,
    height: i32,
    stride: i32,
    pixels: []const u8,
};

pub const Renderer = struct {
    options: Options = .{},
    factory: ?*d2d.Object = null,
    writer: ?*d2d.Object = null,
    params: ?*d2d.Object = null,
    target: ?*d2d.Object = null,
    white: ?*d2d.Object = null,
    background: ?*d2d.Object = null,
    format: ?*d2d.Object = null,
    format_size: i32 = 0,
    language: [32]u8 = undefined,
    language_len: usize = 0,
    dc: c.HDC = null,
    bitmap: c.HBITMAP = null,
    previous: c.HGDIOBJ = null,
    bits: ?[*]u8 = null,
    bitmap_width: i32 = 0,
    bitmap_height: i32 = 0,
    lines: [layout.maximum_lines][maximum_line_units]u16 = undefined,
    line_lengths: [layout.maximum_lines]usize = undefined,
    line_count: usize = 0,

    /// Sets the small fields in place; the line buffers stay undefined, so a
    /// renderer in a block workspace is never copied through the thread stack.
    pub fn init(self: *Renderer, options: Options) void {
        const info = @typeInfo(Renderer).@"struct";
        inline for (info.field_names, info.field_types, info.field_attrs) |field_name, field_type, field_attrs| {
            if (comptime !std.mem.eql(u8, field_name, "lines") and !std.mem.eql(u8, field_name, "line_lengths") and !std.mem.eql(u8, field_name, "language"))
                @field(self, field_name) = comptime field_attrs.defaultValue(field_type).?;
        }
        self.options = options;
    }

    /// Releases every device, factory and bitmap; the renderer can render again afterwards.
    pub fn deinit(self: *Renderer) void {
        self.releaseTarget();
        d2d.releaseOptional(&self.format);
        d2d.releaseOptional(&self.params);
        d2d.releaseOptional(&self.writer);
        d2d.releaseOptional(&self.factory);
        self.releaseBitmap();
        self.format_size = 0;
    }

    pub fn active(self: *const Renderer) bool {
        return self.factory != null or self.dc != null;
    }

    /// The memory DC holding the last image, for UpdateLayeredWindow.
    pub fn deviceContext(self: *const Renderer) c.HDC {
        return self.dc;
    }

    pub fn render(self: *Renderer, lines: []const []const u8, width: i32, height: i32, settings: layout.Settings, language: []const u8, crop: bool) !?Image {
        if (width <= 0 or height <= 0 or width > maximum_dimension or height > maximum_dimension) return null;
        if (@as(i64, width) * height * 4 > section.maximum_pixel_bytes) return null;
        if (lines.len == 0) return null;
        if (lines.len > layout.maximum_lines) return error.TooManyOverlayLines;
        for (lines, 0..) |line, index| {
            self.line_lengths[index] = std.unicode.utf8ToUtf16Le(&self.lines[index], line) catch return error.OverlayLineTooLong;
        }
        self.line_count = lines.len;
        if (language.len > self.language.len) return error.InvalidOverlayLanguage;
        if (!std.mem.eql(u8, language, self.language[0..self.language_len])) {
            d2d.releaseOptional(&self.format);
            self.format_size = 0;
            @memcpy(self.language[0..language.len], language);
            self.language_len = language.len;
        }
        try self.ensureFactories();
        const result = try layout.calculate(width, height, settings, lines.len, self);
        if (result.font_size_px <= 0) return null;
        const region: layout.Rect = if (crop) result.background else .{ .x = 0, .y = 0, .width = width, .height = height };
        if (region.width <= 0 or region.height <= 0) return null;
        try self.ensureBitmap(region.width, region.height);
        var attempt: usize = 0;
        while (true) : (attempt += 1) {
            try self.ensureTarget();
            const drawn = try self.draw(&result, region);
            if (drawn) break;
            // D2DERR_RECREATE_TARGET: the device was lost; one new device is tried.
            self.releaseTarget();
            if (attempt >= points.overlay_device_retries) return error.OverlayDeviceLost;
        }
        _ = c.GdiFlush();
        const stride = region.width * 4;
        const length: usize = @intCast(@as(i64, stride) * region.height);
        return .{ .x = region.x, .y = region.y, .width = region.width, .height = region.height, .stride = stride, .pixels = self.bits.?[0..length] };
    }

    /// layout.calculate's measurer: one DirectWrite layout per line at the size.
    pub fn measure(self: *Renderer, index: usize, font_size: i32) !layout.Size {
        const format = try self.textFormat(font_size);
        var text_layout: ?*d2d.Object = null;
        try d2d.check(self.writer.?.createTextLayout(self.lines[index][0..self.line_lengths[index]], format, points.overlay_text_layout_extent_px, points.overlay_text_layout_extent_px, &text_layout));
        defer d2d.releaseOptional(&text_layout);
        var metrics: d2d.TextMetrics = undefined;
        try d2d.check(text_layout.?.metrics(&metrics));
        return .{ .width = metrics.width_including_trailing_whitespace, .height = metrics.height };
    }

    fn draw(self: *Renderer, result: *const layout.Result, region: layout.Rect) !bool {
        const target = self.target.?;
        const bounds: d2d.Rect = .{ .left = 0, .top = 0, .right = region.width, .bottom = region.height };
        try d2d.check(target.bindDc(self.dc.?, &bounds));
        const format = try self.textFormat(result.font_size_px);
        target.beginDraw();
        target.setTextAntialiasMode(points.overlay_text_antialias_mode);
        target.setTextRenderingParams(self.params);
        target.clear(&.{ .r = 0, .g = 0, .b = 0, .a = 0 });
        const left: f32 = @floatFromInt(result.background.x - region.x);
        const top: f32 = @floatFromInt(result.background.y - region.y);
        const box: d2d.RectF = .{ .left = left, .top = top, .right = left + @as(f32, @floatFromInt(result.background.width)), .bottom = top + @as(f32, @floatFromInt(result.background.height)) };
        target.fillRectangle(&box, self.background.?);
        target.pushAxisAlignedClip(&box, d2d.antialias_mode_aliased);
        var failure: ?anyerror = null;
        for (0..self.line_count) |index| {
            var text_layout: ?*d2d.Object = null;
            d2d.check(self.writer.?.createTextLayout(self.lines[index][0..self.line_lengths[index]], format, points.overlay_text_layout_extent_px, points.overlay_text_layout_extent_px, &text_layout)) catch |err| {
                failure = err;
                break;
            };
            defer d2d.releaseOptional(&text_layout);
            const origin: d2d.Point2F = .{ .x = @as(f32, @floatFromInt(result.text.x - region.x)), .y = result.line_tops[index] - @as(f32, @floatFromInt(region.y)) };
            target.drawTextLayout(origin, text_layout.?, self.white.?);
        }
        target.popAxisAlignedClip();
        const ended = target.endDraw();
        if (failure) |err| return err;
        if (ended == d2d.recreate_target) return false;
        try d2d.check(ended);
        return true;
    }

    fn ensureFactories(self: *Renderer) !void {
        if (self.factory == null) try d2d.check(d2d.D2D1CreateFactory(d2d.factory_type_single_threaded, &d2d.iid_factory, null, &self.factory));
        if (self.writer == null) try d2d.check(d2d.DWriteCreateFactory(d2d.dwrite_factory_type_shared, &d2d.iid_dwrite_factory, &self.writer));
        if (self.params == null) {
            var system: ?*d2d.Object = null;
            try d2d.check(self.writer.?.createRenderingParams(&system));
            if (self.options.text_mode == .default) {
                self.params = system;
            } else {
                defer d2d.releaseOptional(&system);
                try d2d.check(self.writer.?.createCustomRenderingParams(system.?.gamma(), system.?.enhancedContrast(), @as(f32, @floatFromInt(points.overlay_custom_cleartype_percent)) / 100, d2d.pixel_geometry_flat, @backingInt(self.options.text_mode), &self.params));
            }
        }
    }

    fn ensureTarget(self: *Renderer) !void {
        if (self.target != null) return;
        errdefer self.releaseTarget();
        const properties: d2d.RenderTargetProperties = .{
            .type = switch (self.options.target) {
                .software => d2d.render_target_type_software,
                .hardware => d2d.render_target_type_hardware,
            },
            .pixel_format = .{ .format = d2d.dxgi_format_b8g8r8a8_unorm, .alpha_mode = d2d.alpha_mode_premultiplied },
            .dpi_x = 96,
            .dpi_y = 96,
            .usage = 0,
            .min_level = 0,
        };
        try d2d.check(self.factory.?.createDcRenderTarget(&properties, &self.target));
        try d2d.check(self.target.?.createSolidColorBrush(&.{ .r = color(points.overlay_foreground_red), .g = color(points.overlay_foreground_green), .b = color(points.overlay_foreground_blue), .a = color(points.overlay_foreground_alpha) }, &self.white));
        try d2d.check(self.target.?.createSolidColorBrush(&.{ .r = color(points.overlay_background_red), .g = color(points.overlay_background_green), .b = color(points.overlay_background_blue), .a = color(points.overlay_background_alpha) }, &self.background));
        self.target.?.setAntialiasMode(points.overlay_antialias_mode);
    }

    fn releaseTarget(self: *Renderer) void {
        d2d.releaseOptional(&self.white);
        d2d.releaseOptional(&self.background);
        d2d.releaseOptional(&self.target);
    }

    /// The text format at a size in the language of the current render.
    fn textFormat(self: *Renderer, size: i32) !*d2d.Object {
        if (self.format != null and self.format_size == size) return self.format.?;
        var locale_wide: [33:0]u16 = undefined;
        const locale_length = try std.unicode.utf8ToUtf16Le(locale_wide[0..32], self.language[0..self.language_len]);
        locale_wide[locale_length] = 0;
        d2d.releaseOptional(&self.format);
        self.format_size = 0;
        try d2d.check(self.writer.?.createTextFormat(std.unicode.utf8ToUtf16LeStringLiteral(points.overlay_font_family), @floatFromInt(size), &locale_wide, &self.format));
        try d2d.check(self.format.?.setWordWrapping(d2d.word_wrapping_no_wrap));
        self.format_size = size;
        return self.format.?;
    }

    fn ensureBitmap(self: *Renderer, width: i32, height: i32) !void {
        if (self.dc != null and self.bitmap_width == width and self.bitmap_height == height) return;
        self.releaseBitmap();
        errdefer self.releaseBitmap();
        self.dc = c.CreateCompatibleDC(null) orelse return error.OverlayBitmap;
        var info: c.BITMAPINFO = std.mem.zeroes(c.BITMAPINFO);
        info.bmiHeader.biSize = @sizeOf(c.BITMAPINFOHEADER);
        info.bmiHeader.biWidth = width;
        info.bmiHeader.biHeight = -height;
        info.bmiHeader.biPlanes = 1;
        info.bmiHeader.biBitCount = 32;
        info.bmiHeader.biCompression = c.BI_RGB;
        var bits: ?*anyopaque = null;
        self.bitmap = c.CreateDIBSection(self.dc, &info, c.DIB_RGB_COLORS, &bits, null, 0) orelse return error.OverlayBitmap;
        self.bits = @ptrCast(bits orelse return error.OverlayBitmap);
        self.previous = c.SelectObject(self.dc, self.bitmap);
        self.bitmap_width = width;
        self.bitmap_height = height;
    }

    fn releaseBitmap(self: *Renderer) void {
        if (self.dc) |dc| {
            if (self.previous != null) _ = c.SelectObject(dc, self.previous);
            _ = c.DeleteDC(dc);
        }
        if (self.bitmap) |bitmap| _ = c.DeleteObject(bitmap);
        self.dc = null;
        self.bitmap = null;
        self.previous = null;
        self.bits = null;
        self.bitmap_width = 0;
        self.bitmap_height = 0;
    }
};

test "render draws the background, crops to it and releases everything" {
    const renderer = try std.testing.allocator.create(Renderer);
    defer std.testing.allocator.destroy(renderer);
    renderer.init(.{});
    defer renderer.deinit();
    const lines = [_][]const u8{ "Target FPS  144 fps", "CPU  \u{2014}" };
    const settings: layout.Settings = .{ .size_mode = .absolute_pixels, .font_size_px = 14, .region_width_ratio = 0.25, .region_height_ratio = 0.2, .anchor = .bottom_right, .margin_px = 12 };
    const full = (try renderer.render(&lines, 640, 360, settings, "en-US", false)).?;
    try std.testing.expectEqual(@as(i32, 640), full.width);
    // Copy the full image, then compare it with the cropped one.
    const copy = try std.testing.allocator.dupe(u8, full.pixels);
    defer std.testing.allocator.free(copy);
    const cropped = (try renderer.render(&lines, 640, 360, settings, "en-US", true)).?;
    try std.testing.expect(cropped.x > 0 and cropped.y > 0 and cropped.x + cropped.width == 640 - 12 and cropped.y + cropped.height == 360 - 12);
    var inside: usize = 0;
    var white: usize = 0;
    for (0..360) |row| for (0..640) |column| {
        const pixel = copy[(row * 640 + column) * 4 ..][0..4];
        const x: i32 = @intCast(column);
        const y: i32 = @intCast(row);
        if (x >= cropped.x and x < cropped.x + cropped.width and y >= cropped.y and y < cropped.y + cropped.height) {
            const offset: usize = @intCast((y - cropped.y) * cropped.stride + (x - cropped.x) * 4);
            try std.testing.expectEqualSlices(u8, pixel, cropped.pixels[offset..][0..4]);
            try std.testing.expect(pixel[3] >= 168);
            if (pixel[0] > 200) white += 1;
            inside += 1;
        } else try std.testing.expectEqualSlices(u8, &.{ 0, 0, 0, 0 }, pixel);
    };
    try std.testing.expect(inside > 0 and white > 0);
    // The background corner is exactly the premultiplied translucent black.
    try std.testing.expectEqualSlices(u8, &.{ 0, 0, 0, 168 }, cropped.pixels[0..4]);
    try std.testing.expectEqual(@as(?Image, null), try renderer.render(&.{}, 640, 360, settings, "en-US", false));
    try std.testing.expectEqual(@as(?Image, null), try renderer.render(&lines, 8193, 360, settings, "en-US", false));
    renderer.deinit();
    try std.testing.expect(!renderer.active());
}
