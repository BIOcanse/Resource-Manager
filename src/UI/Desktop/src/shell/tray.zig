const std = @import("std");
const points = @import("desktop_points_values").Bound;
const w = @import("../win32.zig");
const c = w.c;
const copy = @import("../text/generated/tray.zig");
const lookup = @import("../text/lookup.zig");
pub const Event = union(enum) {
    open,
    exit,
    session_ending,
    running: c.HWND,
    /// The context menu is about to open: the host fills in the overlay item
    /// (synchronously, on the tray thread).
    overlay_menu: *OverlayMenu,
    /// The overlay item was clicked while it named a software.
    overlay_toggle: *const OverlayMenu,
};
/// The menu item 「性能浮窗：<前台软件名>」 as the host read it when the menu opened.
pub const OverlayMenu = struct {
    pub const State = enum { backend_unavailable, no_foreground, failed, software };
    state: State = .backend_unavailable,
    on: bool = false,
    // Holding buffers: an id or name that does not fit leaves the item failed.
    id: [points.overlay_menu_identity_bytes]u8 = undefined,
    id_len: usize = 0,
    name: [points.overlay_menu_identity_bytes]u8 = undefined,
    name_len: usize = 0,
    pub fn set(self: *OverlayMenu, id: []const u8, name: []const u8, on: bool) void {
        if (id.len == 0 or id.len > self.id.len or name.len > self.name.len) {
            self.state = .failed;
            return;
        }
        @memcpy(self.id[0..id.len], id);
        self.id_len = id.len;
        @memcpy(self.name[0..name.len], name);
        self.name_len = name.len;
        self.on = on;
        self.state = .software;
    }
    pub fn softwareId(self: *const OverlayMenu) []const u8 {
        return self.id[0..self.id_len];
    }
    pub fn enabled(self: *const OverlayMenu) bool {
        return self.on;
    }
    /// The item's label in `text`: the software's name, or why there is none.
    pub fn label(self: *const OverlayMenu, text: *const lookup.Text) lookup.Balloon {
        const detail = switch (self.state) {
            .software => self.name[0..self.name_len],
            .no_foreground => text.overlay_menu_no_software,
            .backend_unavailable => text.overlay_menu_backend_unavailable,
            .failed => text.overlay_menu_read_failed,
        };
        return lookup.balloon(.info, "", text.overlay_menu_format, &.{detail});
    }
};
const overlay_item = 3;
pub const Callback = *const fn (?*anyopaque, Event) void;
const icon_message = c.WM_APP + 1;
const language_message = c.WM_APP + 2;
// NIN_BALLOONUSERCLICK (WM_USER + 5) with the default notify icon version.
const balloon_click = c.WM_USER + 5;

pub const Tray = struct {
    context: ?*anyopaque = null,
    callback: Callback = undefined,
    lock: std.atomic.Mutex = .unlocked,
    window: c.HWND = null,
    menu: c.HMENU = null,
    language: [32]u8 = @splat(0),
    language_length: usize = 0,
    diagnostic_menu: bool = false,
    // Tray thread only: the overlay item of the menu being shown.
    overlay: OverlayMenu = .{},

    pub fn setLanguage(self: *Tray, language: []const u8) !void {
        if (language.len > self.language.len) return error.InvalidLanguage;
        while (!self.lock.tryLock()) std.atomic.spinLoopHint();
        defer self.lock.unlock();
        @memcpy(self.language[0..language.len], language);
        self.language_length = language.len;
        if (self.window != null) _ = c.PostMessageW(self.window, language_message, 0, 0);
    }
    /// Shows one balloon on the running tray icon. Returns false when the icon
    /// is not running; the caller drops the message (no queue, no retry).
    pub fn showBalloon(self: *Tray, balloon: *const lookup.Balloon) bool {
        while (!self.lock.tryLock()) std.atomic.spinLoopHint();
        const window = self.window;
        self.lock.unlock();
        if (window == null) return false;
        var icon: c.NOTIFYICONDATAW = std.mem.zeroes(c.NOTIFYICONDATAW);
        icon.cbSize = @sizeOf(c.NOTIFYICONDATAW);
        icon.hWnd = window;
        icon.uID = 1;
        icon.uFlags = c.NIF_INFO;
        @memcpy(icon.szInfoTitle[0 .. balloon.title_len + 1], balloon.title[0 .. balloon.title_len + 1]);
        @memcpy(icon.szInfo[0 .. balloon.body_len + 1], balloon.body[0 .. balloon.body_len + 1]);
        icon.dwInfoFlags = @backingInt(balloon.icon);
        return c.Shell_NotifyIconW(c.NIM_MODIFY, &icon) != 0;
    }
    fn refresh(self: *Tray) !void {
        while (!self.lock.tryLock()) std.atomic.spinLoopHint();
        defer self.lock.unlock();
        // Language normalization belongs to the preference reader. The English
        // catalog is the existing explicit language fallback, never a second value.
        var selected = copy.all[0];
        for (copy.all) |item| if (std.mem.eql(u8, item.language, copy.fallback_language)) {
            selected = item;
            break;
        };
        for (copy.all) |item| if (std.ascii.eqlIgnoreCase(item.language, self.language[0..self.language_length])) {
            selected = item;
            break;
        };
        const menu = c.CreatePopupMenu();
        if (menu == null) return error.Menu;
        var buffer: [128:0]u16 = undefined;
        var length = try std.unicode.utf8ToUtf16Le(&buffer, selected.open);
        buffer[length] = 0;
        _ = c.AppendMenuW(menu, c.MF_STRING, 1, &buffer);
        length = try std.unicode.utf8ToUtf16Le(&buffer, selected.exit);
        buffer[length] = 0;
        _ = c.AppendMenuW(menu, c.MF_STRING, 2, &buffer);
        if (self.menu != null) _ = c.DestroyMenu(self.menu);
        self.menu = menu;
    }
    pub fn run(self: *Tray, stop: c.HANDLE, context: ?*anyopaque, callback: Callback) !void {
        self.context = context;
        self.callback = callback;
        var class: c.WNDCLASSW = std.mem.zeroes(c.WNDCLASSW);
        class.lpfnWndProc = procedure;
        class.hInstance = c.GetModuleHandleW(null);
        class.lpszClassName = w.L("ResourceManager.Desktop.Tray");
        if (c.RegisterClassW(&class) == 0 and c.GetLastError() != c.ERROR_CLASS_ALREADY_EXISTS) return error.WindowClass;
        defer _ = c.UnregisterClassW(class.lpszClassName, class.hInstance);
        const window = c.CreateWindowExW(c.WS_EX_TOOLWINDOW, class.lpszClassName, w.L("Resource Manager Desktop"), c.WS_OVERLAPPED, 0, 0, 0, 0, null, null, class.hInstance, self);
        if (window == null) return error.Window;
        while (!self.lock.tryLock()) std.atomic.spinLoopHint();
        self.window = window;
        self.lock.unlock();
        defer {
            while (!self.lock.tryLock()) std.atomic.spinLoopHint();
            self.window = null;
            self.lock.unlock();
            _ = c.DestroyWindow(window);
        }
        try self.refresh();
        defer {
            _ = c.DestroyMenu(self.menu);
            self.menu = null;
        }
        var icon: c.NOTIFYICONDATAW = std.mem.zeroes(c.NOTIFYICONDATAW);
        icon.cbSize = @sizeOf(c.NOTIFYICONDATAW);
        icon.hWnd = window;
        icon.uID = 1;
        icon.uFlags = c.NIF_MESSAGE | c.NIF_ICON | c.NIF_TIP;
        icon.uCallbackMessage = icon_message;
        icon.hIcon = c.LoadIconW(null, @ptrFromInt(32512));
        const tip = w.L("Resource Manager");
        @memcpy(icon.szTip[0..tip.len], tip);
        if (c.Shell_NotifyIconW(c.NIM_ADD, &icon) == 0) return error.TrayIcon;
        defer _ = c.Shell_NotifyIconW(c.NIM_DELETE, &icon);
        callback(context, .{ .running = window });
        const handles = [_]c.HANDLE{stop};
        while (true) {
            const result = c.MsgWaitForMultipleObjects(1, &handles, c.FALSE, c.INFINITE, c.QS_ALLINPUT);
            if (result == c.WAIT_OBJECT_0) break;
            if (result != c.WAIT_OBJECT_0 + 1) return error.MessageWait;
            var message: c.MSG = undefined;
            while (c.PeekMessageW(&message, null, 0, 0, c.PM_REMOVE) != 0) {
                if (message.message == c.WM_QUIT) return error.MessageLoopEnded;
                _ = c.TranslateMessage(&message);
                _ = c.DispatchMessageW(&message);
            }
        }
    }
    fn procedure(window: c.HWND, message: c.UINT, wp: c.WPARAM, lp: c.LPARAM) callconv(.winapi) c.LRESULT {
        if (message == c.WM_NCCREATE) {
            const create: *c.CREATESTRUCTW = @ptrFromInt(@as(usize, @bitCast(lp)));
            _ = c.SetWindowLongPtrW(window, c.GWLP_USERDATA, @bitCast(@intFromPtr(create.lpCreateParams)));
        }
        const pointer = c.GetWindowLongPtrW(window, c.GWLP_USERDATA);
        if (pointer == 0) return c.DefWindowProcW(window, message, wp, lp);
        const self: *Tray = @ptrFromInt(@as(usize, @bitCast(pointer)));
        switch (message) {
            c.WM_QUERYENDSESSION => return c.TRUE,
            c.WM_ENDSESSION => {
                if (wp != 0) self.callback(self.context, .session_ending);
                return 0;
            },
            c.WM_COMMAND => {
                if (wp & 0xffff == 1) self.callback(self.context, .open) else if (wp & 0xffff == 2) self.callback(self.context, .exit);
                return 0;
            },
            language_message => {
                self.refresh() catch {};
                return 0;
            },
            c.WM_APP + 10 => {
                // Explicit diagnostic mode only: a window-scoped menu capture on
                // the spare display, without moving the mouse or taking focus.
                const coordinates: usize = @bitCast(lp);
                const x: i16 = @bitCast(@as(u16, @truncate(coordinates)));
                const y: i16 = @bitCast(@as(u16, @truncate(coordinates >> 16)));
                if (self.diagnostic_menu) self.showMenu(window, x, y, false);
                return 0;
            },
            icon_message => {
                if (lp == c.WM_LBUTTONUP or lp == balloon_click) self.callback(self.context, .open);
                if (lp == c.WM_RBUTTONUP or lp == c.WM_CONTEXTMENU) {
                    var point: c.POINT = undefined;
                    if (c.GetCursorPos(&point) == 0) return 0;
                    self.showMenu(window, point.x, point.y, true);
                }
                return 0;
            },
            else => return c.DefWindowProcW(window, message, wp, lp),
        }
    }
    fn showMenu(self: *Tray, window: c.HWND, x: c_int, y: c_int, activate: bool) void {
        if (activate) _ = c.SetForegroundWindow(window);
        self.overlay = .{};
        self.callback(self.context, .{ .overlay_menu = &self.overlay });
        const inserted = self.insertOverlayItem();
        const id = c.TrackPopupMenu(self.menu, c.TPM_RETURNCMD | c.TPM_RIGHTBUTTON, x, y, 0, window, null);
        if (inserted) {
            _ = c.DeleteMenu(self.menu, overlay_item, c.MF_BYCOMMAND);
            _ = c.DeleteMenu(self.menu, 0, c.MF_BYPOSITION);
        }
        if (id == 1) self.callback(self.context, .open) else if (id == 2) self.callback(self.context, .exit) else if (id == overlay_item and self.overlay.state == .software) self.callback(self.context, .{ .overlay_toggle = &self.overlay });
        _ = c.PostMessageW(window, c.WM_NULL, 0, 0);
    }
    /// Puts the overlay item and a separator at the top of the menu: checked
    /// when the switch is on, greyed (with the reason as its label) when there
    /// is no software to switch.
    fn insertOverlayItem(self: *Tray) bool {
        while (!self.lock.tryLock()) std.atomic.spinLoopHint();
        const text = lookup.select(self.language[0..self.language_length]);
        self.lock.unlock();
        var value = self.overlay.label(text);
        var flags: c.UINT = c.MF_BYPOSITION | c.MF_STRING;
        if (self.overlay.state != .software) flags |= c.MF_GRAYED;
        if (self.overlay.state == .software and self.overlay.on) flags |= c.MF_CHECKED;
        if (c.InsertMenuW(self.menu, 0, c.MF_BYPOSITION | c.MF_SEPARATOR, 0, null) == 0) return false;
        if (c.InsertMenuW(self.menu, 0, flags, overlay_item, &value.body) == 0) {
            _ = c.DeleteMenu(self.menu, 0, c.MF_BYPOSITION);
            return false;
        }
        return true;
    }
};

test "the overlay item names the software or the reason there is none" {
    const text = lookup.select("en-US");
    var menu: OverlayMenu = .{};
    menu.set("steam:570", "Dota 2", true);
    try std.testing.expectEqual(OverlayMenu.State.software, menu.state);
    try std.testing.expectEqualStrings("steam:570", menu.softwareId());
    var label = menu.label(text);
    var expected = lookup.balloon(.info, "", text.overlay_menu_format, &.{"Dota 2"});
    try std.testing.expectEqualSlices(u16, expected.bodySlice(), label.bodySlice());
    menu = .{ .state = .no_foreground };
    label = menu.label(text);
    expected = lookup.balloon(.info, "", text.overlay_menu_format, &.{text.overlay_menu_no_software});
    try std.testing.expectEqualSlices(u16, expected.bodySlice(), label.bodySlice());
    menu = .{};
    menu.set("", "x", false);
    try std.testing.expectEqual(OverlayMenu.State.failed, menu.state);
}

test "every native language supplies valid UTF-8 menu copy" {
    try std.testing.expect(copy.all.len >= 9);
    for (copy.all) |item| {
        try std.testing.expect(item.open.len > 0 and item.exit.len > 0);
        try std.testing.expect(std.unicode.utf8ValidateSlice(item.open));
        try std.testing.expect(std.unicode.utf8ValidateSlice(item.exit));
        try std.testing.expect(std.mem.indexOf(u8, item.overlay_menu_format, "{0}") != null);
        try std.testing.expect(std.mem.indexOf(u8, item.overlay_enabled_format, "{0}") != null);
        try std.testing.expect(std.mem.indexOf(u8, item.overlay_disabled_format, "{0}") != null);
        try std.testing.expect(std.mem.indexOf(u8, item.overlay_toggle_failed, "{0}") != null);
    }
}
