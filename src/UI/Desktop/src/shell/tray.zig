const std = @import("std");
const w = @import("../win32.zig");
const c = w.c;
const copy = @import("../text/generated/tray.zig");
pub const Event = union(enum) { open, exit, session_ending, running: usize };
pub const Callback = *const fn (?*anyopaque, Event) void;
const icon_message = c.WM_APP + 1;
const language_message = c.WM_APP + 2;

pub const Tray = struct {
    context: ?*anyopaque = null,
    callback: Callback = undefined,
    lock: std.atomic.Mutex = .unlocked,
    window: c.HWND = null,
    menu: c.HMENU = null,
    language: [32]u8 = .{0} ** 32,
    language_length: usize = 0,
    diagnostic_menu: bool = false,

    pub fn setLanguage(self: *Tray, language: []const u8) !void {
        if (language.len > self.language.len) return error.InvalidLanguage;
        while (!self.lock.tryLock()) std.atomic.spinLoopHint();
        defer self.lock.unlock();
        @memcpy(self.language[0..language.len], language);
        self.language_length = language.len;
        if (self.window != null) _ = c.PostMessageW(self.window, language_message, 0, 0);
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
        callback(context, .{ .running = @intFromPtr(window) });
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
                if (lp == c.WM_LBUTTONUP) self.callback(self.context, .open);
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
        const id = c.TrackPopupMenu(self.menu, c.TPM_RETURNCMD | c.TPM_RIGHTBUTTON, x, y, 0, window, null);
        if (id == 1) self.callback(self.context, .open) else if (id == 2) self.callback(self.context, .exit);
        _ = c.PostMessageW(window, c.WM_NULL, 0, 0);
    }
};

test "every native language supplies valid UTF-8 menu copy" {
    try std.testing.expect(copy.all.len >= 9);
    for (copy.all) |item| {
        try std.testing.expect(item.open.len > 0 and item.exit.len > 0);
        try std.testing.expect(std.unicode.utf8ValidateSlice(item.open));
        try std.testing.expect(std.unicode.utf8ValidateSlice(item.exit));
    }
}
