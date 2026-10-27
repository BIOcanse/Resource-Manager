//! Explicit real Debug child acceptance, separate from deterministic fixture gates.
const std = @import("std");
const wp = @import("window_process");
const options = @import("fixture_options");
const w = wp.win32;
const Rect = extern struct { left: i32, top: i32, right: i32, bottom: i32 };
const MonitorInfo = extern struct { size: u32 = @sizeOf(MonitorInfo), monitor: Rect = undefined, work: Rect = undefined, flags: u32 = 0 };
extern "user32" fn GetSystemMetrics(c_int) callconv(.winapi) c_int;
extern "user32" fn MonitorFromWindow(w.HANDLE, u32) callconv(.winapi) ?w.HANDLE;
extern "user32" fn GetMonitorInfoW(w.HANDLE, *MonitorInfo) callconv(.winapi) c_int;
extern "user32" fn IsWindowVisible(w.HANDLE) callconv(.winapi) c_int;
extern "user32" fn GetForegroundWindow() callconv(.winapi) ?w.HANDLE;
extern "user32" fn PostMessageW(w.HANDLE, u32, usize, isize) callconv(.winapi) c_int;
extern "user32" fn EnumWindows(*const fn (w.HANDLE, isize) callconv(.winapi) c_int, isize) callconv(.winapi) c_int;
extern "user32" fn GetWindowThreadProcessId(w.HANDLE, *u32) callconv(.winapi) u32;
extern "kernel32" fn GetLastError() callconv(.winapi) u32;

const WindowObservation = struct {
    pid: u32,
    count: usize = 0,
    visible: usize = 0,
    fn visit(handle: w.HANDLE, context: isize) callconv(.winapi) c_int {
        const self: *WindowObservation = @ptrFromInt(@as(usize, @bitCast(context)));
        var owner: u32 = 0;
        _ = GetWindowThreadProcessId(handle, &owner);
        if (owner == self.pid) {
            self.count += 1;
            if (IsWindowVisible(handle) != 0) self.visible += 1;
        }
        return 1;
    }
    fn sample(self: *WindowObservation) bool {
        return EnumWindows(visit, @bitCast(@intFromPtr(self))) != 0;
    }
};

const Harness = struct {
    client: wp.Client = .{},
    mutex: w.Mutex = .{},
    stop: w.HANDLE,
    cancel: w.HANDLE,
    changed: w.HANDLE,
    done: w.HANDLE,
    worker: ?std.Thread = null,
    pid: u32 = 0,
    handle: usize = 0,
    closed: bool = false,
    crashed: bool = false,
    failed: bool = false,
    stopped: ?wp.StopResult = null,
    window_wanted: bool = true,

    fn init() !Harness {
        const stop = w.CreateEventW(null, 1, 0, null) orelse return error.Event;
        errdefer w.close(stop);
        const cancel = w.CreateEventW(null, 1, 0, null) orelse return error.Event;
        errdefer w.close(cancel);
        const changed = w.CreateEventW(null, 1, 0, null) orelse return error.Event;
        errdefer w.close(changed);
        const done = w.CreateEventW(null, 1, 0, null) orelse return error.Event;
        return .{ .stop = stop, .cancel = cancel, .changed = changed, .done = done };
    }
    fn deinit(self: *Harness) void {
        _ = w.SetEvent(self.stop);
        if (self.worker) |thread| thread.join();
        w.close(self.stop);
        w.close(self.cancel);
        w.close(self.changed);
        w.close(self.done);
    }
    fn run(self: *Harness) void {
        self.client.run(self.stop, self, onEvent, .{
            .executable = options.realNativeUi,
            .arguments = "--resident-api-base http://127.0.0.1:19321/",
            .session = .{ .ready = false, .generation = 0 },
            .language = "zh-CN",
            .show = .@"show-passive-secondary",
            .cancel_poll_ms = 10,
            .stop_deadline_ms = 1000,
            .timeout_policy = .terminate,
            .stop_wait_cancel = self.cancel,
        }) catch |err| {
            std.debug.print("real NativeUi client failed: {s}\n", .{@errorName(err)});
            self.failed = true;
        };
        _ = w.SetEvent(self.done);
    }
    fn onEvent(context: ?*anyopaque, event: wp.Event) void {
        const self: *Harness = @ptrCast(@alignCast(context.?));
        self.mutex.lock();
        defer self.mutex.unlock();
        switch (event) {
            .running => |pid| {
                self.pid = pid;
                std.debug.print("real NativeUi launched pid={d}\n", .{pid});
            },
            .window_handle => |handle| self.handle = handle,
            .user_close => self.closed = true,
            .crashed => |code| {
                self.window_wanted = false;
                self.crashed = true;
                std.debug.print("real NativeUi crashed code={d}\n", .{code});
            },
            .stopped => |result| {
                self.stopped = result;
                std.debug.print("real NativeUi stopped: {s}, elapsed_ms={d}, exit_code={d}\n", .{ @tagName(result.outcome), result.elapsed_ms, result.exit_code });
            },
            else => {},
        }
        _ = w.SetEvent(self.changed);
    }
    fn secondaryWindow(self: *Harness) !w.HANDLE {
        // A bounded test observation, without moving or activating any window.
        for (0..300) |_| {
            self.mutex.lock();
            const raw = self.handle;
            self.mutex.unlock();
            if (raw != 0) {
                const handle: w.HANDLE = @ptrFromInt(raw);
                if (IsWindowVisible(handle) != 0) {
                    const monitor = MonitorFromWindow(handle, 0) orelse return error.WindowOutsideDisplays;
                    var info: MonitorInfo = .{};
                    if (GetMonitorInfoW(monitor, &info) == 0) return error.MonitorInfo;
                    if (info.flags & 1 == 0) return handle;
                }
            }
            if (w.WaitForSingleObject(self.done, 0) == 0) return error.ChildExitedBeforeWindow;
            _ = w.WaitForSingleObject(self.changed, 50);
            _ = w.ResetEvent(self.changed);
        }
        return error.NoPassiveSecondaryWindow;
    }
    fn waitClose(self: *Harness) !void {
        for (0..300) |_| {
            self.mutex.lock();
            const closed = self.closed;
            self.mutex.unlock();
            if (closed) return;
            if (w.WaitForSingleObject(self.done, 0) == 0) return error.ChildExitedBeforeUserClose;
            _ = w.WaitForSingleObject(self.changed, 50);
            _ = w.ResetEvent(self.changed);
        }
        return error.UserCloseNotReported;
    }
};

test "real Debug NativeUi reports close intent and waits for parent stop on secondary display" {
    if (options.realNativeUi.len == 0) return error.SkipZigTest;
    const displays = GetSystemMetrics(80); // SM_CMONITORS
    std.debug.print("real NativeUi acceptance displays={d}\n", .{displays});
    if (displays < 2) {
        std.debug.print("SKIP: no secondary display; no window launched\n", .{});
        return error.SkipZigTest;
    }
    var host = try Harness.init();
    defer host.deinit();
    host.worker = try std.Thread.spawn(.{}, Harness.run, .{&host});
    const handle = try host.secondaryWindow();
    try std.testing.expect(GetForegroundWindow() != handle);
    std.debug.print("real NativeUi visible on secondary display, hwnd={d}, foreground preserved\n", .{@intFromPtr(handle)});
    try std.testing.expect(PostMessageW(handle, 0x0010, 0, 0) != 0); // WM_CLOSE is only an intent.
    try host.waitClose();
    try std.testing.expectEqual(@as(u32, 258), w.WaitForSingleObject(host.done, 100));
    _ = w.SetEvent(host.stop);
    try std.testing.expectEqual(@as(u32, 0), w.WaitForSingleObject(host.done, 15_000));
    host.worker.?.join();
    host.worker = null;
    try std.testing.expect(host.pid != 0 and host.closed and !host.failed and !host.crashed);
    try std.testing.expect(host.stopped != null);
    try std.testing.expectEqual(wp.StopOutcome.normal, host.stopped.?.outcome);
    try std.testing.expectEqual(@as(u32, 0), host.stopped.?.exit_code);
    std.debug.print("real NativeUi close intent observed; parent stop joined child with exit 0\n", .{});
}

test "real Debug NativeUi headless false demand exits with the parent write endpoint still open" {
    if (options.realNativeUi.len == 0) return error.SkipZigTest;
    const cancel = w.CreateEventW(null, 1, 0, null) orelse return error.Event;
    defer w.close(cancel);
    const captured = try wp.test_support.launchCaptured(.{
        .executable = options.realNativeUi,
        .arguments = "--resident-api-base http://127.0.0.1:19321/",
        .cancel_poll_ms = 10,
        .stop_deadline_ms = 1000,
        .timeout_policy = .terminate,
        .stop_wait_cancel = cancel,
    });
    const child = captured.child;
    defer w.close(captured.stderr);
    defer w.close(child.process);
    defer w.close(child.read);
    defer {
        w.close(child.write);
        // Raw launch has no Client stop wrapper; its Job still owns failure cleanup.
        w.close(child.job);
        _ = w.WaitForSingleObject(child.process, w.infinite);
    }
    std.debug.print("real headless NativeUi launched pid={d}; parent writer retained; running=true is never sent\n", .{child.pid});
    try wp.writeAll(child.write, "{\"type\":\"backend-session\",\"ready\":false,\"generation\":0,\"reason\":null}\n{\"type\":\"language\",\"language\":\"zh-CN\"}\n{\"type\":\"parent-wants-me-running\",\"running\":false}\n");
    var windows: WindowObservation = .{ .pid = child.pid };
    var observation_available = true;
    var exited = false;
    for (0..300) |_| {
        if (observation_available and !windows.sample()) {
            observation_available = false;
            std.debug.print("LIMITATION: EnumWindows unavailable, error={d}; visible-window absence is not dynamically certified\n", .{GetLastError()});
        }
        if (w.WaitForSingleObject(child.process, 50) == 0) {
            exited = true;
            break;
        }
    }
    try std.testing.expect(exited);
    var exit_code: u32 = 1;
    try std.testing.expect(w.GetExitCodeProcess(child.process, &exit_code) != 0);
    var flags: u32 = 0;
    try std.testing.expect(w.GetHandleInformation(child.write, &flags) != 0);
    std.debug.print("real headless NativeUi pid={d}, exit={d} (0x{x}), parent writer still open=true, running true sent=false\n", .{ child.pid, exit_code, exit_code });
    var stderr_bytes: [4096]u8 = undefined;
    var stderr_total: usize = 0;
    while (stderr_total < 64 * 1024) {
        var stderr_got: u32 = 0;
        if (w.ReadFile(captured.stderr, &stderr_bytes, stderr_bytes.len, &stderr_got, null) == 0 or stderr_got == 0) break;
        stderr_total += stderr_got;
        std.debug.print("NativeUi stderr: {s}", .{stderr_bytes[0..stderr_got]});
    }
    try std.testing.expectEqual(@as(u32, 0), exit_code);
    var bytes: [256]u8 = undefined;
    var got: u32 = 0;
    _ = w.ReadFile(child.read, &bytes, bytes.len, &got, null);
    try std.testing.expectEqual(@as(u32, 0), got);
    if (observation_available) try std.testing.expectEqual(@as(usize, 0), windows.visible);
    std.debug.print("real headless NativeUi pid={d}, exit={d}, parent writer still open=true, running true sent=false, child fact bytes={d}, window observation available={}, sampled visible windows={d}, sampled top-level windows={d}\n", .{ child.pid, exit_code, got, observation_available, windows.visible, windows.count });
}
