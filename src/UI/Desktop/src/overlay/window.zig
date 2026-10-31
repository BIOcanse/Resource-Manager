//! The external overlay: a layered, click-through, non-activating topmost
//! popup placed over the target's client area (ExternalOverlayWindow).
const std = @import("std");
const c = @import("../backend/c.zig").win;

pub const class_name = std.unicode.utf8ToUtf16LeStringLiteral("ResourceManager.Desktop.PerformanceOverlay");
// WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOPMOST | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW
const extended_style = 0x00080000 | 0x20 | 0x8 | 0x08000000 | 0x80;
const show_flags = c.SWP_NOACTIVATE | c.SWP_NOZORDER | c.SWP_SHOWWINDOW;
const hide_flags = c.SWP_HIDEWINDOW | c.SWP_NOACTIVATE | c.SWP_NOZORDER;

pub const Bounds = struct { x: i32 = 0, y: i32 = 0, width: i32 = 0, height: i32 = 0 };

/// Registers the window class for this thread's windows; `unregister` when the
/// block returns.
pub fn register(instance: c.HINSTANCE) !void {
    var class: c.WNDCLASSEXW = std.mem.zeroes(c.WNDCLASSEXW);
    class.cbSize = @sizeOf(c.WNDCLASSEXW);
    class.lpfnWndProc = c.DefWindowProcW;
    class.hInstance = instance;
    class.lpszClassName = class_name;
    if (c.RegisterClassExW(&class) == 0) return error.OverlayWindowClass;
}
pub fn unregister(instance: c.HINSTANCE) void {
    _ = c.UnregisterClassW(class_name, instance);
}

pub const Window = struct {
    handle: c.HWND,
    shown: bool = false,

    pub fn create(instance: c.HINSTANCE) !Window {
        const handle = c.CreateWindowExW(extended_style, class_name, null, c.WS_POPUP, 0, 0, 1, 1, null, null, instance, null) orelse return error.OverlayWindowCreate;
        return .{ .handle = handle };
    }

    /// Shows `source` (a memory DC holding the premultiplied image) at the bounds.
    pub fn update(self: *Window, source: c.HDC, bounds: Bounds) !void {
        const screen = c.GetDC(null) orelse return error.OverlayWindowUpdate;
        defer _ = c.ReleaseDC(null, screen);
        var position: c.POINT = .{ .x = bounds.x, .y = bounds.y };
        var size: c.SIZE = .{ .cx = bounds.width, .cy = bounds.height };
        var origin: c.POINT = .{ .x = 0, .y = 0 };
        var blend: c.BLENDFUNCTION = .{ .BlendOp = c.AC_SRC_OVER, .BlendFlags = 0, .SourceConstantAlpha = 255, .AlphaFormat = c.AC_SRC_ALPHA };
        if (c.UpdateLayeredWindow(self.handle, screen, &position, &size, source, &origin, 0, &blend, c.ULW_ALPHA) == 0) return error.OverlayWindowUpdate;
        try self.move(bounds);
    }

    pub fn move(self: *Window, bounds: Bounds) !void {
        // The window was created topmost; SWP_NOZORDER keeps its place, as the window process does.
        if (c.SetWindowPos(self.handle, null, bounds.x, bounds.y, bounds.width, bounds.height, show_flags) == 0) return error.OverlayWindowMove;
        self.shown = true;
    }

    pub fn hide(self: *Window) void {
        if (!self.shown) return;
        _ = c.SetWindowPos(self.handle, null, 0, 0, 0, 0, hide_flags);
        self.shown = false;
    }

    pub fn destroy(self: *Window) void {
        _ = c.DestroyWindow(self.handle);
        self.shown = false;
    }
};
