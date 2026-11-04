//! Owned icon handles. Metrics are read on start and on Windows DPI/settings events.
const c = @import("desktop_win32_c");
pub const resource_id = 1;

pub const Pair = struct {
    small: c.HICON = null,
    large: c.HICON = null,

    pub fn load(dpi: c.UINT) !Pair {
        const instance = c.GetModuleHandleW(null);
        const small = try image(instance, dpi, c.SM_CXSMICON, c.SM_CYSMICON);
        errdefer _ = c.DestroyIcon(small);
        return .{ .small = small, .large = try image(instance, dpi, c.SM_CXICON, c.SM_CYICON) };
    }
    pub fn deinit(self: *Pair) void {
        if (self.small != null) _ = c.DestroyIcon(self.small);
        if (self.large != null) _ = c.DestroyIcon(self.large);
        self.* = .{};
    }
    pub fn apply(self: Pair, window: c.HWND) void {
        _ = c.SendMessageW(window, c.WM_SETICON, c.ICON_SMALL, @bitCast(@intFromPtr(self.small)));
        _ = c.SendMessageW(window, c.WM_SETICON, c.ICON_BIG, @bitCast(@intFromPtr(self.large)));
        _ = c.SetClassLongPtrW(window, c.GCLP_HICON, @bitCast(@intFromPtr(self.large)));
    }
};

fn image(instance: c.HINSTANCE, dpi: c.UINT, x: c_int, y: c_int) !c.HICON {
    const handle = c.ResourceManagerLoadIconResource(instance, resource_id,
        c.GetSystemMetricsForDpi(x, dpi), c.GetSystemMetricsForDpi(y, dpi));
    if (handle == null) return error.ProductIcon;
    return handle;
}
