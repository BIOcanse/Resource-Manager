//! The few Direct2D and DirectWrite COM methods the overlay calls, by vtable
//! index. Indices follow the MinGW headers shipped with Zig
//! (lib/libc/include/any-windows-any/d2d1.h, dwrite.h); C translation of those
//! headers needs D2D_USE_C_DEFINITIONS and is not used.
const std = @import("std");
const points = @import("desktop_points_values").Bound;

pub const HRESULT = i32;
pub const Guid = extern struct { data1: u32, data2: u16, data3: u16, data4: [8]u8 };
pub const ColorF = extern struct { r: f32, g: f32, b: f32, a: f32 };
pub const RectF = extern struct { left: f32, top: f32, right: f32, bottom: f32 };
pub const Point2F = extern struct { x: f32, y: f32 };
pub const PixelFormat = extern struct { format: u32, alpha_mode: u32 };
pub const RenderTargetProperties = extern struct {
    type: u32,
    pixel_format: PixelFormat,
    dpi_x: f32,
    dpi_y: f32,
    usage: u32,
    min_level: u32,
};
pub const TextMetrics = extern struct {
    left: f32,
    top: f32,
    width: f32,
    width_including_trailing_whitespace: f32,
    height: f32,
    layout_width: f32,
    layout_height: f32,
    max_bidi_reordering_depth: u32,
    line_count: u32,
};
pub const Rect = extern struct { left: i32, top: i32, right: i32, bottom: i32 };

pub const factory_type_single_threaded = 0;
pub const render_target_type_default = 0;
pub const render_target_type_software = 1;
pub const render_target_type_hardware = 2;
pub const dxgi_format_b8g8r8a8_unorm = 87;
pub const alpha_mode_premultiplied = 1;
pub const antialias_mode_per_primitive = 0;
pub const antialias_mode_aliased = 1;
pub const text_antialias_mode_grayscale = 2;
pub const draw_text_options_none = 0;
pub const recreate_target: HRESULT = @bitCast(@as(u32, 0x8899000C));
pub const dwrite_factory_type_shared = 0;
pub const font_weight_normal = 400;
pub const font_style_normal = 0;
pub const font_stretch_normal = 5;
pub const word_wrapping_no_wrap = 1;
pub const pixel_geometry_flat = 0;

pub const iid_factory: Guid = .{ .data1 = 0x06152247, .data2 = 0x6f50, .data3 = 0x465a, .data4 = .{ 0x92, 0x45, 0x11, 0x8b, 0xfd, 0x3b, 0x60, 0x07 } };
pub const iid_dwrite_factory: Guid = .{ .data1 = 0xb859ee5a, .data2 = 0xd838, .data3 = 0x4b5b, .data4 = .{ 0xa2, 0xe8, 0x1a, 0xdc, 0x7d, 0x93, 0xdb, 0x48 } };

pub extern "d2d1" fn D2D1CreateFactory(factory_type: u32, riid: *const Guid, options: ?*const anyopaque, factory: *?*Object) callconv(.winapi) HRESULT;
pub extern "dwrite" fn DWriteCreateFactory(factory_type: u32, riid: *const Guid, factory: *?*Object) callconv(.winapi) HRESULT;

/// Any COM object: a pointer to its vtable of function pointers.
pub const Object = extern struct {
    vtable: [*]const *const anyopaque,

    fn method(self: *Object, comptime T: type, index: usize) T {
        return @ptrCast(self.vtable[index]);
    }
    pub fn release(self: *Object) void {
        _ = self.method(*const fn (*Object) callconv(.winapi) u32, 2)(self);
    }

    // ID2D1Factory
    pub fn createDcRenderTarget(self: *Object, properties: *const RenderTargetProperties, target: *?*Object) HRESULT {
        return self.method(*const fn (*Object, *const RenderTargetProperties, *?*Object) callconv(.winapi) HRESULT, 16)(self, properties, target);
    }

    // ID2D1RenderTarget and ID2D1DCRenderTarget
    pub fn createSolidColorBrush(self: *Object, color: *const ColorF, brush: *?*Object) HRESULT {
        return self.method(*const fn (*Object, *const ColorF, ?*const anyopaque, *?*Object) callconv(.winapi) HRESULT, 8)(self, color, null, brush);
    }
    pub fn fillRectangle(self: *Object, rect: *const RectF, brush: *Object) void {
        self.method(*const fn (*Object, *const RectF, *Object) callconv(.winapi) void, 17)(self, rect, brush);
    }
    pub fn drawTextLayout(self: *Object, origin: Point2F, layout: *Object, brush: *Object) void {
        self.method(*const fn (*Object, Point2F, *Object, *Object, u32) callconv(.winapi) void, 28)(self, origin, layout, brush, draw_text_options_none);
    }
    pub fn setAntialiasMode(self: *Object, mode: u32) void {
        self.method(*const fn (*Object, u32) callconv(.winapi) void, 32)(self, mode);
    }
    pub fn setTextAntialiasMode(self: *Object, mode: u32) void {
        self.method(*const fn (*Object, u32) callconv(.winapi) void, 34)(self, mode);
    }
    pub fn setTextRenderingParams(self: *Object, params: ?*Object) void {
        self.method(*const fn (*Object, ?*Object) callconv(.winapi) void, 36)(self, params);
    }
    pub fn pushAxisAlignedClip(self: *Object, rect: *const RectF, mode: u32) void {
        self.method(*const fn (*Object, *const RectF, u32) callconv(.winapi) void, 45)(self, rect, mode);
    }
    pub fn popAxisAlignedClip(self: *Object) void {
        self.method(*const fn (*Object) callconv(.winapi) void, 46)(self);
    }
    pub fn clear(self: *Object, color: *const ColorF) void {
        self.method(*const fn (*Object, *const ColorF) callconv(.winapi) void, 47)(self, color);
    }
    pub fn beginDraw(self: *Object) void {
        self.method(*const fn (*Object) callconv(.winapi) void, 48)(self);
    }
    pub fn endDraw(self: *Object) HRESULT {
        return self.method(*const fn (*Object, ?*u64, ?*u64) callconv(.winapi) HRESULT, 49)(self, null, null);
    }
    pub fn bindDc(self: *Object, dc: *anyopaque, rect: *const Rect) HRESULT {
        return self.method(*const fn (*Object, *anyopaque, *const Rect) callconv(.winapi) HRESULT, 57)(self, dc, rect);
    }

    // IDWriteFactory
    pub fn createRenderingParams(self: *Object, params: *?*Object) HRESULT {
        return self.method(*const fn (*Object, *?*Object) callconv(.winapi) HRESULT, 10)(self, params);
    }
    pub fn createCustomRenderingParams(self: *Object, gamma_value: f32, contrast: f32, cleartype: f32, geometry: u32, mode: u32, params: *?*Object) HRESULT {
        return self.method(*const fn (*Object, f32, f32, f32, u32, u32, *?*Object) callconv(.winapi) HRESULT, 12)(self, gamma_value, contrast, cleartype, geometry, mode, params);
    }
    pub fn createTextFormat(self: *Object, family: [*:0]const u16, size: f32, locale: [*:0]const u16, format: *?*Object) HRESULT {
        return self.method(*const fn (*Object, [*:0]const u16, ?*anyopaque, u32, u32, u32, f32, [*:0]const u16, *?*Object) callconv(.winapi) HRESULT, 15)(self, family, null, points.overlay_font_weight, points.overlay_font_style, points.overlay_font_stretch, size, locale, format);
    }
    pub fn createTextLayout(self: *Object, text: []const u16, format: *Object, max_width: f32, max_height: f32, layout: *?*Object) HRESULT {
        return self.method(*const fn (*Object, [*]const u16, u32, *Object, f32, f32, *?*Object) callconv(.winapi) HRESULT, 18)(self, text.ptr, @intCast(text.len), format, max_width, max_height, layout);
    }

    // IDWriteRenderingParams
    pub fn gamma(self: *Object) f32 {
        return self.method(*const fn (*Object) callconv(.winapi) f32, 3)(self);
    }
    pub fn enhancedContrast(self: *Object) f32 {
        return self.method(*const fn (*Object) callconv(.winapi) f32, 4)(self);
    }

    // IDWriteTextFormat and IDWriteTextLayout
    pub fn setWordWrapping(self: *Object, wrapping: u32) HRESULT {
        return self.method(*const fn (*Object, u32) callconv(.winapi) HRESULT, 5)(self, wrapping);
    }
    pub fn metrics(self: *Object, value: *TextMetrics) HRESULT {
        return self.method(*const fn (*Object, *TextMetrics) callconv(.winapi) HRESULT, 60)(self, value);
    }
};

pub fn check(result: HRESULT) !void {
    if (result < 0) return error.ComCallFailed;
}

pub fn releaseOptional(object: *?*Object) void {
    if (object.*) |value| value.release();
    object.* = null;
}

test "COM records match the Windows ABI sizes" {
    try std.testing.expectEqual(@as(usize, 28), @sizeOf(RenderTargetProperties));
    try std.testing.expectEqual(@as(usize, 36), @sizeOf(TextMetrics));
    try std.testing.expectEqual(@as(usize, 16), @sizeOf(Guid));
}
