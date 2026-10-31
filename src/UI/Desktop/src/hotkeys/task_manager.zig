//! TaskManagerReplacement (uid 88): a low-level keyboard hook on the block's
//! own thread swallows Ctrl+Shift+Esc and reports the "window wants open"
//! fact. No IFEO or registry change happens here. Stop posts WM_QUIT.
const std = @import("std");
const w = @import("../win32.zig");
const c = w.c;
const lookup = @import("../text/lookup.zig");

pub const Host = struct {
    context: *anyopaque,
    running: *const fn (*anyopaque) void,
    /// Called from the hook; must only record the fact and wake the host.
    open: *const fn (*anyopaque) void,
    balloon: *const fn (*anyopaque, *const lookup.Balloon) void,
    text: *const fn (*anyopaque) *const lookup.Text,
};

var current: ?*const Host = null;

const escape = 0x1b;
/// Same decision as the C# TaskManagerShortcutHook.
pub fn isCtrlShiftEscape(virtual_key: u32, control: bool, shift: bool, alt: bool) bool {
    return virtual_key == escape and control and shift and !alt;
}

pub fn run(stop: c.HANDLE, host: Host) !void {
    var message: c.MSG = undefined;
    _ = c.PeekMessageW(&message, null, c.WM_USER, c.WM_USER, c.PM_NOREMOVE);
    if (w.stopped(stop)) return;
    std.debug.assert(current == null);
    current = &host;
    defer current = null;
    const hook = c.SetWindowsHookExW(c.WH_KEYBOARD_LL, keyboardProc, c.GetModuleHandleW(null), 0);
    defer if (hook != null) {
        _ = c.UnhookWindowsHookEx(hook);
    };
    if (hook == null) {
        // C# reports this once and leaves the shortcut to Windows; the block
        // keeps running until its parent stops it.
        const text = host.text(host.context);
        const value = lookup.balloon(.warning, text.task_manager_title, text.task_manager_enable_failed, &.{});
        host.balloon(host.context, &value);
    }
    host.running(host.context);
    while (true) {
        const result = c.GetMessageW(&message, null, 0, 0);
        if (result == 0) return;
        if (result == -1) return error.MessageLoop;
        _ = c.TranslateMessage(&message);
        _ = c.DispatchMessageW(&message);
    }
}

fn down(key: c_int) bool {
    return (@as(u16, @bitCast(c.GetAsyncKeyState(key))) & 0x8000) != 0;
}

fn keyboardProc(code: c_int, wp: c.WPARAM, lp: c.LPARAM) callconv(.winapi) c.LRESULT {
    if (code >= 0 and (wp == c.WM_KEYDOWN or wp == c.WM_SYSKEYDOWN)) if (current) |host| {
        const info: *const c.KBDLLHOOKSTRUCT = @ptrFromInt(@as(usize, @bitCast(lp)));
        if (isCtrlShiftEscape(info.vkCode, down(c.VK_CONTROL), down(c.VK_SHIFT), down(c.VK_MENU))) {
            host.open(host.context);
            return 1;
        }
    };
    return c.CallNextHookEx(null, code, wp, lp);
}

test "Ctrl+Shift+Esc decision matches the C# hook" {
    try std.testing.expect(isCtrlShiftEscape(0x1b, true, true, false));
    try std.testing.expect(!isCtrlShiftEscape(0x1b, true, true, true));
    try std.testing.expect(!isCtrlShiftEscape(0x1b, true, false, false));
    try std.testing.expect(!isCtrlShiftEscape(0x41, true, true, false));
}

test "hook thread installs, reports running and leaves on WM_QUIT" {
    const Probe = struct {
        running: c.HANDLE,
        balloons: std.atomic.Value(u32) = .init(0),
        fn onRunning(raw: *anyopaque) void {
            const self: *@This() = @ptrCast(@alignCast(raw));
            _ = c.SetEvent(self.running);
        }
        fn onOpen(_: *anyopaque) void {}
        fn onBalloon(raw: *anyopaque, _: *const lookup.Balloon) void {
            const self: *@This() = @ptrCast(@alignCast(raw));
            _ = self.balloons.fetchAdd(1, .acq_rel);
        }
        fn onText(_: *anyopaque) *const lookup.Text {
            return lookup.select("en-US");
        }
    };
    const Context = struct {
        probe: *Probe,
        stop: c.HANDLE,
        fn entry(raw: ?*anyopaque) callconv(.winapi) c.DWORD {
            const context: *@This() = @ptrCast(@alignCast(raw.?));
            run(context.stop, .{ .context = context.probe, .running = Probe.onRunning, .open = Probe.onOpen, .balloon = Probe.onBalloon, .text = Probe.onText }) catch return 1;
            return 0;
        }
    };
    var probe: Probe = .{ .running = c.CreateEventW(null, c.TRUE, c.FALSE, null).? };
    defer _ = c.CloseHandle(probe.running);
    const stop = c.CreateEventW(null, c.TRUE, c.FALSE, null).?;
    defer _ = c.CloseHandle(stop);
    var context: Context = .{ .probe = &probe, .stop = stop };
    const thread = c.CreateThread(null, 256 * 1024, Context.entry, &context, c.STACK_SIZE_PARAM_IS_A_RESERVATION, null).?;
    defer _ = c.CloseHandle(thread);
    try std.testing.expectEqual(c.WAIT_OBJECT_0, c.WaitForSingleObject(probe.running, 5000));
    try std.testing.expectEqual(@as(u32, 0), probe.balloons.load(.acquire));
    _ = c.SetEvent(stop);
    _ = c.PostThreadMessageW(c.GetThreadId(thread), c.WM_QUIT, 0, 0);
    try std.testing.expectEqual(c.WAIT_OBJECT_0, c.WaitForSingleObject(thread, 5000));
    var exit_code: c.DWORD = 99;
    _ = c.GetExitCodeThread(thread, &exit_code);
    try std.testing.expectEqual(@as(c.DWORD, 0), exit_code);
}
