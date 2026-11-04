//! DesktopObservation (uid 46, under BackendConnection). WinEvent hooks on
//! the block's own thread mark the sample or the main-window visibility dirty;
//! after the queue drains, the block captures once and writes a line only when
//! the content (foreground, window set with flags, Complete) changed. The
//! capture time alone is not a change.
//!
//! Backend contract (maintenance session, 2026-10-30; the observation section
//! of docs/trusted_access_2026-10-24.md): the block holds one upload stream,
//! `POST stream_path` with a chunked NDJSON request body, one observation per
//! line of at most `max_line_bytes`. Opening it selects this connection as the
//! session's publisher: 200 keeps the response open, 403/409 reject at once.
//! Every line replaces the current observation; when the stream ends (body
//! closed, connection dropped, process exit) the selection and the observation
//! are released together. After a 200 the block writes the current sample,
//! then a line only on a content change: no heartbeat, no periodic line. A
//! failed open, a rejection or a broken stream closes the connection and the
//! block opens it again after `retry_ms`; stop closes the body and returns.
//!
//! Interface visibility (scenario 3) is computed here for the one handle the
//! window process gave the host, with the C# NativeWindowOcclusion rules.
const std = @import("std");
const w = @import("../win32.zig");
const c = w.c;
const channel_module = @import("../backend/channel.zig");
const occlusion = @import("occlusion.zig");

/// The publisher upload stream.
pub const stream_path = "/api/runtime/interactive-desktop/publisher";
/// Longest line including its newline: the backend's
/// InteractiveDesktopObservation.MaximumRequestBytes.
pub const max_line_bytes = 512 * 1024;
pub const max_windows = 2048;
/// Longest JSON of one entry (PID and creation time at their widest).
const max_entry_bytes = 128;
comptime {
    // The foreground plus every window always fits one line.
    std.debug.assert((max_windows + 1) * (max_entry_bytes + 1) + 512 < max_line_bytes);
}
/// Host posts this to the block thread when the visibility handle changed.
pub const window_message = c.WM_APP + 2;
const wake_message = c.WM_APP + 3;
const max_identities = 4096;
/// .NET ticks at the FILETIME epoch (1601-01-01).
const filetime_epoch_ticks: i64 = 504911232000000000;

pub const Entry = struct {
    pid: u32,
    created: i64,
    visible: bool = false,
    background: bool = false,
};
pub const Sample = struct {
    observed_at: i64 = 0,
    complete: bool = false,
    foreground: ?Entry = null,
    count: usize = 0,
    windows: [max_windows]Entry = undefined,

    pub fn slice(self: *const Sample) []const Entry {
        return self.windows[0..self.count];
    }
    /// Content equality: everything except the capture time.
    pub fn sameContent(self: *const Sample, other: *const Sample) bool {
        if (self.complete != other.complete or self.count != other.count) return false;
        if ((self.foreground == null) != (other.foreground == null)) return false;
        if (self.foreground) |mine| if (!std.meta.eql(mine, other.foreground.?)) return false;
        for (self.slice(), other.slice()) |a, b| if (!std.meta.eql(a, b)) return false;
        return true;
    }
};
pub const Publisher = struct { pid: u32, created: i64, session: u32 };

pub fn writeJson(out: []u8, publisher: Publisher, sample: *const Sample) ![]const u8 {
    var writer = std.Io.Writer.fixed(out);
    try writer.print("{{\"publisherProcessId\":{d},\"publisherCreationFileTimeUtc\":{d},\"sessionId\":{d},\"observedAtUtcTicks\":{d},\"complete\":{},\"foregroundKnown\":{},\"foreground\":", .{
        publisher.pid, publisher.created, publisher.session, sample.observed_at, sample.complete, sample.foreground != null,
    });
    if (sample.foreground) |entry| try writeEntry(&writer, entry) else try writer.writeAll("null");
    try writer.writeAll(",\"windows\":[");
    for (sample.slice(), 0..) |entry, index| {
        if (index != 0) try writer.writeByte(',');
        try writeEntry(&writer, entry);
    }
    try writer.writeAll("]}");
    return writer.buffered();
}
fn writeEntry(writer: *std.Io.Writer, entry: Entry) !void {
    try writer.print("{{\"processId\":{d},\"creationFileTimeUtc\":{d},\"hasVisibleWindow\":{},\"hasBackgroundWindow\":{}}}", .{ entry.pid, entry.created, entry.visible, entry.background });
}

// ---- capture ----

const Identity = struct { pid: u32, entry: ?Entry };
const Capture = struct {
    publisher: Publisher,
    sample: *Sample,
    identities: *[max_identities]Identity,
    identity_count: usize = 0,

    fn read(self: *Capture, pid: u32) ?Entry {
        for (self.identities[0..self.identity_count]) |known| if (known.pid == pid) return known.entry;
        const entry = readIdentity(pid, self.publisher.session);
        if (self.identity_count < self.identities.len) {
            self.identities[self.identity_count] = .{ .pid = pid, .entry = entry };
            self.identity_count += 1;
        }
        return entry;
    }
};

/// Same reading as the C# ReadIdentity: another session or an exited process
/// is left out; a process this user cannot open is reported by PID with an
/// unverified (0) creation time.
fn readIdentity(pid: u32, session: u32) ?Entry {
    var owner: c.DWORD = 0;
    if (c.ProcessIdToSessionId(pid, &owner) != 0 and owner != session) return null;
    const process = c.OpenProcess(c.PROCESS_QUERY_LIMITED_INFORMATION, c.FALSE, pid) orelse {
        return if (gone(c.GetLastError())) null else .{ .pid = pid, .created = 0 };
    };
    defer _ = c.CloseHandle(process);
    var created: c.FILETIME = undefined;
    var exited: c.FILETIME = undefined;
    var kernel: c.FILETIME = undefined;
    var user: c.FILETIME = undefined;
    if (c.GetProcessTimes(process, &created, &exited, &kernel, &user) == 0) {
        return if (gone(c.GetLastError())) null else .{ .pid = pid, .created = 0 };
    }
    return .{ .pid = pid, .created = fileTime(created) };
}
fn gone(code: c.DWORD) bool {
    return code == c.ERROR_INVALID_PARAMETER or code == c.ERROR_NOT_FOUND;
}
fn fileTime(value: c.FILETIME) i64 {
    return @bitCast((@as(u64, value.dwHighDateTime) << 32) | value.dwLowDateTime);
}

fn entryLess(_: void, a: Entry, b: Entry) bool {
    return a.pid < b.pid;
}

fn collectWindow(window: c.HWND, parameter: c.LPARAM) callconv(.winapi) c.BOOL {
    const capture: *Capture = @ptrFromInt(@as(usize, @bitCast(parameter)));
    const sample = capture.sample;
    if (c.IsWindowVisible(window) == 0) return c.TRUE;
    if (sample.count >= max_windows) {
        sample.complete = false;
        return c.FALSE;
    }
    var pid: c.DWORD = 0;
    _ = c.GetWindowThreadProcessId(window, &pid);
    if (pid == 0 or pid > std.math.maxInt(i32)) {
        sample.complete = false;
        return c.TRUE;
    }
    const minimized = c.IsIconic(window) != 0;
    const index = for (sample.slice(), 0..) |entry, at| {
        if (entry.pid == pid) break at;
    } else blk: {
        const entry = capture.read(pid) orelse return c.TRUE;
        sample.windows[sample.count] = entry;
        sample.count += 1;
        break :blk sample.count - 1;
    };
    sample.windows[index].visible = sample.windows[index].visible or !minimized;
    sample.windows[index].background = sample.windows[index].background or minimized;
    return c.TRUE;
}

/// Captures the sample into `sample`, windows sorted by PID.
pub fn captureSample(publisher: Publisher, sample: *Sample, identities: *[max_identities]Identity) void {
    var now: c.FILETIME = undefined;
    c.GetSystemTimeAsFileTime(&now);
    sample.* = .{ .observed_at = fileTime(now) + filetime_epoch_ticks, .complete = true };
    var capture: Capture = .{ .publisher = publisher, .sample = sample, .identities = identities };
    if (c.GetForegroundWindow()) |window| {
        var pid: c.DWORD = 0;
        _ = c.GetWindowThreadProcessId(window, &pid);
        sample.foreground = if (pid > 0 and pid <= std.math.maxInt(i32)) capture.read(pid) else null;
        if (sample.foreground == null) sample.complete = false;
    }
    if (c.EnumWindows(collectWindow, @bitCast(@intFromPtr(&capture))) == 0) sample.complete = false;
    std.mem.sort(Entry, sample.windows[0..sample.count], {}, entryLess);
}

/// Window classes of the shell surfaces that take the foreground while the
/// user operates the notification area: the taskbars and the overflow flyout.
/// These name Windows' own windows; they are platform identifiers, not choices.
const shell_tray_classes = [_][]const u8{ "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "NotifyIconOverflowWindow", "TopLevelWindowForOverflowXamlIsland" };
/// True when the current foreground window belongs to `pid`, is not this
/// process and is not one of the shell's notification-area surfaces.
fn userForeground(pid: u32) bool {
    if (pid == c.GetCurrentProcessId()) return false;
    const window = c.GetForegroundWindow() orelse return false;
    var owner: c.DWORD = 0;
    _ = c.GetWindowThreadProcessId(window, &owner);
    if (owner != pid) return false;
    var wide: [64]u16 = undefined;
    const length = c.GetClassNameW(window, &wide, wide.len);
    if (length <= 0) return true;
    var name: [64 * 3]u8 = undefined;
    const name_length = std.unicode.utf16LeToUtf8(&name, wide[0..@intCast(length)]) catch return true;
    for (shell_tray_classes) |class| if (std.mem.eql(u8, name[0..name_length], class)) return false;
    return true;
}

// ---- visibility ----

extern "dwmapi" fn DwmGetWindowAttribute(window: c.HWND, attribute: c.DWORD, value: *anyopaque, size: c.DWORD) callconv(.winapi) c.HRESULT;
const dwmwa_cloaked = 14;
fn cloaked(window: c.HWND) bool {
    var value: c.DWORD = 0;
    return DwmGetWindowAttribute(window, dwmwa_cloaked, &value, @sizeOf(c.DWORD)) == 0 and value != 0;
}
fn bounds(window: c.HWND) ?occlusion.Rect {
    var rect: c.RECT = undefined;
    if (c.GetWindowRect(window, &rect) == 0) return null;
    const value: occlusion.Rect = .{ .left = rect.left, .top = rect.top, .right = rect.right, .bottom = rect.bottom };
    return if (value.empty()) null else value;
}
fn canOcclude(window: c.HWND) bool {
    const style: u32 = @bitCast(c.GetWindowLongW(window, c.GWL_EXSTYLE));
    if (style & (c.WS_EX_TRANSPARENT | c.WS_EX_LAYERED | c.WS_EX_NOREDIRECTIONBITMAP) != 0) return false;
    return !cloaked(window);
}

pub const Coverage = struct {
    target: c.HWND,
    occluders: [occlusion.max_occluders]occlusion.Rect = undefined,
    count: usize = 0,
    found: bool = false,
    overflow: bool = false,
    scratch: occlusion.Scratch = .{},
};
fn collectOccluder(window: c.HWND, parameter: c.LPARAM) callconv(.winapi) c.BOOL {
    const coverage: *Coverage = @ptrFromInt(@as(usize, @bitCast(parameter)));
    if (window == coverage.target) {
        coverage.found = true;
        return c.FALSE;
    }
    if (coverage.count >= occlusion.max_occluders) {
        coverage.overflow = true;
        return c.FALSE;
    }
    // EnumWindows is top-down Z order: everything before the target is above it.
    if (c.IsWindowVisible(window) != 0 and c.IsIconic(window) == 0 and canOcclude(window)) {
        if (bounds(window)) |rect| {
            coverage.occluders[coverage.count] = rect;
            coverage.count += 1;
        }
    }
    return c.TRUE;
}
/// Shown, not minimized, not cloaked and not fully covered. An incomplete
/// enumeration is conservatively visible.
pub fn interfaceVisible(target: c.HWND, coverage: *Coverage) bool {
    if (c.IsWindowVisible(target) == 0 or c.IsIconic(target) != 0 or cloaked(target)) return false;
    const rect = bounds(target) orelse return true;
    coverage.target = target;
    coverage.count = 0;
    coverage.found = false;
    coverage.overflow = false;
    _ = c.EnumWindows(collectOccluder, @bitCast(@intFromPtr(coverage)));
    if (!coverage.found or coverage.overflow) return true;
    return !occlusion.isFullyCovered(rect, coverage.occluders[0..coverage.count], &coverage.scratch);
}

// ---- block ----

pub const Host = struct {
    context: *anyopaque,
    running: *const fn (*anyopaque) void,
    /// The main window handle the window process reported, read under the host lock.
    window: *const fn (*anyopaque) c.HWND,
    /// Computed visibility for `window`; called when it changes.
    visibility: *const fn (*anyopaque, c.HWND, bool) void,
    /// The foreground process of the last sample whose foreground window was
    /// the user's: neither this process nor the shell's taskbar and tray
    /// overflow, which take the foreground when the tray icon is clicked.
    foreground: *const fn (*anyopaque, Entry) void,
    log: *const fn (*anyopaque, []const u8) void,
};
pub const Config = struct {
    host: Host,
    opener: channel_module.Opener,
    /// Wait before opening the stream again after a failed open, a rejection
    /// or a break (the declared backend probe interval).
    retry_ms: u32,
};

const Workspace = struct {
    channel: channel_module.Channel = .{},
    current: Sample = .{},
    /// Content of the last line written on the open stream.
    written: Sample = .{},
    identities: [max_identities]Identity = undefined,
    coverage: Coverage = .{ .target = null },
    /// One line without its newline.
    json: [max_line_bytes - 1]u8 = undefined,
};

var current: ?*Block = null;

const Block = struct {
    config: Config,
    stop: c.HANDLE,
    thread_id: c.DWORD,
    workspace: *Workspace,
    publisher: Publisher,
    sample_dirty: bool = true,
    visibility_dirty: bool = true,
    wake_posted: bool = false,
    /// The open stream carries a line with `workspace.written`'s content.
    written: bool = false,
    /// Tick from which the stream may be opened again; null opens at once.
    reopen_at: ?u64 = null,
    target: c.HWND = null,
    last_visible: ?bool = null,
    base_hooks: [3]c.HWINEVENTHOOK = @splat(null),
    target_hooks: [2]c.HWINEVENTHOOK = @splat(null),

    fn hook(min: c.DWORD, max: c.DWORD) c.HWINEVENTHOOK {
        return c.SetWinEventHook(min, max, null, winEvent, 0, 0, c.WINEVENT_OUTOFCONTEXT);
    }
    fn unhook(hooks: []c.HWINEVENTHOOK) void {
        for (hooks) |*value| {
            if (value.* != null) _ = c.UnhookWinEvent(value.*);
            value.* = null;
        }
    }
    /// Location and cloak changes matter only for the visibility of a known handle.
    fn retarget(self: *Block) void {
        const window = self.config.host.window(self.config.host.context);
        if (window == self.target) return;
        self.target = window;
        self.last_visible = null;
        self.visibility_dirty = true;
        unhook(&self.target_hooks);
        if (window != null) {
            self.target_hooks[0] = hook(c.EVENT_OBJECT_LOCATIONCHANGE, c.EVENT_OBJECT_LOCATIONCHANGE);
            self.target_hooks[1] = hook(c.EVENT_OBJECT_CLOAKED, c.EVENT_OBJECT_UNCLOAKED);
        }
    }
    fn mark(self: *Block, sample: bool) void {
        if (sample) self.sample_dirty = true;
        self.visibility_dirty = true;
        if (!self.wake_posted) self.wake_posted = c.PostThreadMessageW(self.thread_id, wake_message, 0, 0) != 0;
    }
    fn refresh(self: *Block) void {
        const workspace = self.workspace;
        if (self.visibility_dirty) {
            self.visibility_dirty = false;
            if (self.target) |target| {
                const visible = interfaceVisible(target, &workspace.coverage);
                if (self.last_visible == null or self.last_visible.? != visible) {
                    self.last_visible = visible;
                    self.config.host.visibility(self.config.host.context, target, visible);
                }
            }
        }
        if (!workspace.channel.isOpen() and self.reopenDue()) self.open();
        if (self.sample_dirty) {
            self.sample_dirty = false;
            captureSample(self.publisher, &workspace.current, &workspace.identities);
            if (workspace.current.foreground) |entry| if (userForeground(entry.pid)) self.config.host.foreground(self.config.host.context, entry);
        }
        self.publish();
    }
    fn reopenDue(self: *Block) bool {
        const at = self.reopen_at orelse return true;
        if (c.GetTickCount64() < at) return false;
        self.reopen_at = null;
        return true;
    }
    /// Milliseconds until the stream may be opened again.
    fn reopenWait(self: *const Block) c.DWORD {
        const at = self.reopen_at orelse return 0;
        const now = c.GetTickCount64();
        return if (now >= at) 0 else @intCast(@min(at - now, c.INFINITE - 1));
    }
    /// Opens the upload stream; a 200 selects this connection as the publisher.
    fn open(self: *Block) void {
        const channel = &self.workspace.channel;
        self.written = false;
        self.config.opener.open(channel, self.stop) catch |err| return self.lost(err);
        channel.openUpload(stream_path) catch |err| return self.lost(err);
        // The first line is the sample as it is now.
        self.sample_dirty = true;
    }
    /// Writes the current sample when the stream is open and its content
    /// differs from the last line written on it.
    fn publish(self: *Block) void {
        const workspace = self.workspace;
        if (!workspace.channel.isUploading()) return;
        if (self.written and workspace.current.sameContent(&workspace.written)) return;
        const line = writeJson(&workspace.json, self.publisher, &workspace.current) catch |err| return self.lost(err);
        workspace.channel.writeLine(line) catch |err| return self.lost(err);
        workspace.written = workspace.current;
        self.written = true;
    }
    /// A failed open, a 403/409 rejection or a broken stream. The backend
    /// releases the selection and the observation with the connection; the
    /// stream opens again after `retry_ms`.
    fn lost(self: *Block, err: anyerror) void {
        self.workspace.channel.close();
        self.written = false;
        self.reopen_at = c.GetTickCount64() + self.config.retry_ms;
        if (err != error.Stopped) self.config.host.log(self.config.host.context, @errorName(err));
    }
};

fn topLevel(window: c.HWND) bool {
    return c.GetAncestor(window, c.GA_ROOT) == window;
}
fn winEvent(_: c.HWINEVENTHOOK, event: c.DWORD, window: c.HWND, object: c.LONG, child: c.LONG, _: c.DWORD, _: c.DWORD) callconv(.winapi) void {
    const block = current orelse return;
    switch (event) {
        c.EVENT_SYSTEM_FOREGROUND, c.EVENT_SYSTEM_MINIMIZESTART, c.EVENT_SYSTEM_MINIMIZEEND => block.mark(true),
        c.EVENT_OBJECT_SHOW, c.EVENT_OBJECT_HIDE, c.EVENT_OBJECT_DESTROY => {
            if (object != c.OBJID_WINDOW or child != c.CHILDID_SELF or window == null) return;
            // A destroyed handle can no longer be classified; take it.
            if (event != c.EVENT_OBJECT_DESTROY or c.IsWindow(window) != 0) {
                if (!topLevel(window)) return;
            }
            block.mark(true);
        },
        c.EVENT_OBJECT_LOCATIONCHANGE, c.EVENT_OBJECT_CLOAKED, c.EVENT_OBJECT_UNCLOAKED => {
            if (object != c.OBJID_WINDOW or child != c.CHILDID_SELF or window == null or !topLevel(window)) return;
            block.mark(false);
        },
        else => {},
    }
}

fn publisherIdentity() !Publisher {
    const pid = c.GetCurrentProcessId();
    var session: c.DWORD = 0;
    if (c.ProcessIdToSessionId(pid, &session) == 0) return error.SessionUnavailable;
    var created: c.FILETIME = undefined;
    var exited: c.FILETIME = undefined;
    var kernel: c.FILETIME = undefined;
    var user: c.FILETIME = undefined;
    if (c.GetProcessTimes(c.GetCurrentProcess(), &created, &exited, &kernel, &user) == 0) return error.ProcessTimes;
    return .{ .pid = pid, .created = fileTime(created), .session = session };
}

/// Runs on the block thread until WM_QUIT or stop.
pub fn run(stop: c.HANDLE, config: Config) !void {
    var message: c.MSG = undefined;
    _ = c.PeekMessageW(&message, null, c.WM_USER, c.WM_USER, c.PM_NOREMOVE);
    if (w.stopped(stop)) return;
    const memory = c.VirtualAlloc(null, @sizeOf(Workspace), c.MEM_RESERVE | c.MEM_COMMIT, c.PAGE_READWRITE) orelse return error.OutOfMemory;
    defer _ = c.VirtualFree(memory, 0, c.MEM_RELEASE);
    const workspace: *Workspace = @ptrCast(@alignCast(memory));
    workspace.channel = .{};
    // Stop, WM_QUIT and every error return close the body, then the connection.
    defer workspace.channel.endUpload();
    var block: Block = .{ .config = config, .stop = stop, .thread_id = c.GetCurrentThreadId(), .workspace = workspace, .publisher = try publisherIdentity() };
    std.debug.assert(current == null);
    current = &block;
    defer current = null;
    block.base_hooks[0] = Block.hook(c.EVENT_SYSTEM_FOREGROUND, c.EVENT_SYSTEM_FOREGROUND);
    block.base_hooks[1] = Block.hook(c.EVENT_SYSTEM_MINIMIZESTART, c.EVENT_SYSTEM_MINIMIZEEND);
    block.base_hooks[2] = Block.hook(c.EVENT_OBJECT_DESTROY, c.EVENT_OBJECT_HIDE);
    defer Block.unhook(&block.base_hooks);
    defer Block.unhook(&block.target_hooks);
    for (block.base_hooks) |value| if (value == null) return error.WinEventHook;
    block.retarget();
    config.host.running(config.host.context);
    block.refresh();
    while (true) {
        // Open: wait for the stream to end. Closed: refresh already set the reopen tick.
        const open = workspace.channel.isOpen();
        var handles: [2]c.HANDLE = .{ stop, workspace.channel.waitHandle() };
        const count: c.DWORD = if (open) 2 else 1;
        const timeout: c.DWORD = if (open) c.INFINITE else block.reopenWait();
        const result = c.MsgWaitForMultipleObjectsEx(count, &handles, timeout, c.QS_ALLINPUT, c.MWMO_INPUTAVAILABLE);
        if (result == c.WAIT_OBJECT_0) return;
        if (result == c.WAIT_FAILED) return error.Wait;
        if (open and result == c.WAIT_OBJECT_0 + 1 and workspace.channel.peerEnded()) block.lost(error.PublisherStreamEnded);
        while (c.PeekMessageW(&message, null, 0, 0, c.PM_REMOVE) != 0) {
            if (message.message == c.WM_QUIT) return;
            if (message.hwnd == null and message.message == wake_message) {
                block.wake_posted = false;
                continue;
            }
            if (message.hwnd == null and message.message == window_message) {
                block.retarget();
                continue;
            }
            _ = c.TranslateMessage(&message);
            _ = c.DispatchMessageW(&message);
        }
        block.refresh();
    }
}

test "content equality ignores the capture time and JSON matches the C# record" {
    var a: Sample = .{ .observed_at = 1, .complete = true, .foreground = .{ .pid = 9, .created = 5 } };
    a.windows[0] = .{ .pid = 9, .created = 5, .visible = true };
    a.count = 1;
    var b = a;
    b.observed_at = 2;
    try std.testing.expect(a.sameContent(&b));
    b.windows[0].background = true;
    try std.testing.expect(!a.sameContent(&b));
    b = a;
    b.complete = false;
    try std.testing.expect(!a.sameContent(&b));
    b = a;
    b.foreground = null;
    try std.testing.expect(!a.sameContent(&b));
    var out: [512]u8 = undefined;
    try std.testing.expectEqualStrings(
        "{\"publisherProcessId\":7,\"publisherCreationFileTimeUtc\":8,\"sessionId\":1,\"observedAtUtcTicks\":1,\"complete\":true,\"foregroundKnown\":true,\"foreground\":{\"processId\":9,\"creationFileTimeUtc\":5,\"hasVisibleWindow\":false,\"hasBackgroundWindow\":false},\"windows\":[{\"processId\":9,\"creationFileTimeUtc\":5,\"hasVisibleWindow\":true,\"hasBackgroundWindow\":false}]}",
        try writeJson(&out, .{ .pid = 7, .created = 8, .session = 1 }, &a),
    );
}

test "a live capture lists this process's shown window with sorted, distinct PIDs" {
    const publisher = try publisherIdentity();
    const memory = c.VirtualAlloc(null, @sizeOf(Workspace), c.MEM_RESERVE | c.MEM_COMMIT, c.PAGE_READWRITE) orelse return error.OutOfMemory;
    defer _ = c.VirtualFree(memory, 0, c.MEM_RELEASE);
    const workspace: *Workspace = @ptrCast(@alignCast(memory));
    captureSample(publisher, &workspace.current, &workspace.identities);
    const windows = workspace.current.slice();
    var index: usize = 1;
    while (index < windows.len) : (index += 1) try std.testing.expect(windows[index - 1].pid < windows[index].pid);
    try std.testing.expect(workspace.current.observed_at > filetime_epoch_ticks);
}

const mock = @import("../backend/mock_backend.zig");
const Quiet = struct {
    fn running(_: *anyopaque) void {}
    fn window(_: *anyopaque) c.HWND {
        return null;
    }
    fn visibility(_: *anyopaque, _: c.HWND, _: bool) void {}
    fn foreground(_: *anyopaque, _: Entry) void {}
    fn log(_: *anyopaque, _: []const u8) void {}
};

test "a content-equal sample and a capture-time-only change write no line" {
    var server = try mock.Server.start(.{ .script = &.{.{ .status = "200 OK", .upload = true }} });
    defer server.deinit();
    const memory = c.VirtualAlloc(null, @sizeOf(Workspace), c.MEM_RESERVE | c.MEM_COMMIT, c.PAGE_READWRITE) orelse return error.OutOfMemory;
    defer _ = c.VirtualFree(memory, 0, c.MEM_RELEASE);
    const workspace: *Workspace = @ptrCast(@alignCast(memory));
    workspace.channel = .{};
    defer workspace.channel.endUpload();
    var context: u8 = 0;
    var block: Block = .{
        .config = .{ .host = .{ .context = &context, .running = Quiet.running, .window = Quiet.window, .visibility = Quiet.visibility, .foreground = Quiet.foreground, .log = Quiet.log }, .opener = server.opener(), .retry_ms = 1000 },
        .stop = server.stop,
        .thread_id = 0,
        .workspace = workspace,
        .publisher = .{ .pid = 7, .created = 8, .session = 1 },
    };
    block.open();
    try std.testing.expect(workspace.channel.isUploading());
    try std.testing.expect(block.sample_dirty);
    block.sample_dirty = false;
    workspace.current = .{ .observed_at = 1, .complete = true, .foreground = .{ .pid = 9, .created = 5 } };
    workspace.current.windows[0] = .{ .pid = 9, .created = 5, .visible = true };
    workspace.current.count = 1;

    block.publish(); // the first line after the open
    block.publish(); // same content
    workspace.current.observed_at = 2;
    block.publish(); // only the capture time changed
    workspace.current.windows[0].background = true;
    block.publish(); // a flag changed

    workspace.channel.endUpload();
    try std.testing.expect(server.waitCounts(.{ .streams = 1, .lines = 2, .client_ends = 1 }, 5000));
    server.finish();
    try std.testing.expectEqual(@as(usize, 2), server.uploadCounts().lines);
    var line: [1024]u8 = undefined;
    var expected: [1024]u8 = undefined;
    var first = workspace.current;
    first.observed_at = 1;
    first.windows[0].background = false;
    try std.testing.expectEqualStrings(try writeJson(&expected, block.publisher, &first), server.copyUploadLine(0, &line).?.bytes);
    try std.testing.expectEqualStrings(try writeJson(&expected, block.publisher, &workspace.current), server.copyUploadLine(1, &line).?.bytes);
}
