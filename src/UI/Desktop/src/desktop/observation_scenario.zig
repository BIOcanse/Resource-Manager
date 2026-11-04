//! Scenario test for DesktopObservation driven by a test-owned window: a 1x1
//! tool window far off screen, shown without activation and minimized. It
//! never takes focus and no input is sent. Other desktop activity may add
//! lines, so the checks follow this window's entry through the received lines:
//! each state change of the window appears exactly once, and no two
//! consecutive lines of one stream differ only in their capture time.
const std = @import("std");
const w = @import("../win32.zig");
const c = w.c;
const block = @import("observation.zig");
const mock = @import("../backend/mock_backend.zig");

const Probe = struct {
    running: c.HANDLE,
    changed: c.HANDLE,
    window: c.HWND = null,
    visible: std.atomic.Value(u8) = .init(2),
    fn onRunning(raw: *anyopaque) void {
        const self: *Probe = @ptrCast(@alignCast(raw));
        _ = c.SetEvent(self.running);
    }
    fn onWindow(raw: *anyopaque) c.HWND {
        const self: *Probe = @ptrCast(@alignCast(raw));
        return self.window;
    }
    fn onForeground(_: *anyopaque, _: block.Entry) void {}
    fn onVisibility(raw: *anyopaque, _: c.HWND, visible: bool) void {
        const self: *Probe = @ptrCast(@alignCast(raw));
        self.visible.store(@intFromBool(visible), .release);
        _ = c.SetEvent(self.changed);
    }
    fn onLog(_: *anyopaque, _: []const u8) void {}
    fn waitVisible(self: *Probe, expected: bool) bool {
        const deadline = c.GetTickCount64() + 5000;
        while (self.visible.load(.acquire) != @intFromBool(expected)) {
            const now = c.GetTickCount64();
            if (now >= deadline) return false;
            _ = c.WaitForSingleObject(self.changed, @intCast(deadline - now));
        }
        return true;
    }
};
const Run = struct {
    stop: c.HANDLE,
    config: block.Config,
    fn entry(raw: ?*anyopaque) callconv(.winapi) c.DWORD {
        const self: *Run = @ptrCast(@alignCast(raw.?));
        block.run(self.stop, self.config) catch return 1;
        return 0;
    }
};

/// This window's entry in a line.
const State = enum { absent, visible, background };
const Lines = struct {
    server: *mock.Server,
    shown: []const u8,
    minimized: []const u8,
    a: []u8,
    b: []u8,

    fn state(self: *const Lines, line: []const u8) State {
        if (std.mem.indexOf(u8, line, self.shown) != null) return .visible;
        if (std.mem.indexOf(u8, line, self.minimized) != null) return .background;
        return .absent;
    }
    /// Waits until a line of `stream` shows `expected`, bounded.
    fn wait(self: *const Lines, stream: usize, expected: State) bool {
        const deadline = c.GetTickCount64() + 5000;
        var index: usize = 0;
        while (true) {
            while (self.server.copyUploadLine(index, self.a)) |line| : (index += 1) {
                if (line.stream == stream and self.state(line.bytes) == expected) return true;
            }
            const now = c.GetTickCount64();
            if (now >= deadline) return false;
            if (!self.server.waitCounts(.{ .lines = index + 1 }, @intCast(deadline - now))) return false;
        }
    }
    /// Number of lines of `stream` that change this window's entry to `into`.
    fn transitions(self: *const Lines, stream: usize, into: State) usize {
        var previous: ?State = null;
        var total: usize = 0;
        var index: usize = 0;
        while (self.server.copyUploadLine(index, self.a)) |line| : (index += 1) {
            if (line.stream != stream) continue;
            const now = self.state(line.bytes);
            if (previous != null and previous.? != now and now == into) total += 1;
            previous = now;
        }
        return total;
    }
    /// Consecutive lines of one stream that differ only in observedAtUtcTicks.
    fn repeats(self: *const Lines) usize {
        var total: usize = 0;
        var index: usize = 1;
        while (self.server.copyUploadLine(index, self.b)) |later| : (index += 1) {
            const earlier = self.server.copyUploadLine(index - 1, self.a).?;
            if (earlier.stream == later.stream and std.mem.eql(u8, withoutTime(earlier.bytes, 0), withoutTime(later.bytes, 0)) and
                std.mem.eql(u8, withoutTime(earlier.bytes, 1), withoutTime(later.bytes, 1))) total += 1;
        }
        return total;
    }
    /// First line of `stream`.
    fn first(self: *const Lines, stream: usize) ?[]const u8 {
        var index: usize = 0;
        while (self.server.copyUploadLine(index, self.a)) |line| : (index += 1) {
            if (line.stream == stream) return line.bytes;
        }
        return null;
    }
};
/// Part 0 (before the capture time digits) or part 1 (after them) of a line.
fn withoutTime(line: []const u8, part: u1) []const u8 {
    const key = "\"observedAtUtcTicks\":";
    const at = (std.mem.indexOf(u8, line, key) orelse return line) + key.len;
    var end = at;
    while (end < line.len and std.ascii.isDigit(line[end])) end += 1;
    return if (part == 0) line[0..at] else line[end..];
}

test "desktop observation holds one upload stream, writes only changes and reopens after an end or a conflict" {
    var server = try mock.Server.start(.{ .script = &.{
        .{ .status = "200 OK", .upload = true },
        .{ .status = "200 OK", .upload = true },
        .{ .status = "409 Conflict", .body = "", .close_after = true, .drain = true },
        .{ .status = "200 OK", .upload = true, .repeat = true },
    } });
    defer server.deinit();

    const module = c.GetModuleHandleW(null);
    const class_name = w.L("RmDesktopObservationScenario");
    var class = std.mem.zeroes(c.WNDCLASSEXW);
    class.cbSize = @sizeOf(c.WNDCLASSEXW);
    class.lpfnWndProc = c.DefWindowProcW;
    class.hInstance = module;
    class.lpszClassName = class_name;
    _ = c.RegisterClassExW(&class);
    defer _ = c.UnregisterClassW(class_name, module);
    const window = c.CreateWindowExW(c.WS_EX_TOOLWINDOW | c.WS_EX_NOACTIVATE, class_name, w.L("scenario"), c.WS_POPUP, -20000, -20000, 1, 1, null, null, module, null) orelse return error.Window;
    defer _ = c.DestroyWindow(window);

    var probe: Probe = .{
        .running = c.CreateEventW(null, c.TRUE, c.FALSE, null).?,
        .changed = c.CreateEventW(null, c.FALSE, c.FALSE, null).?,
        .window = window,
    };
    defer _ = c.CloseHandle(probe.running);
    defer _ = c.CloseHandle(probe.changed);
    const stop = c.CreateEventW(null, c.TRUE, c.FALSE, null).?;
    defer _ = c.CloseHandle(stop);
    var context: Run = .{ .stop = stop, .config = .{
        .host = .{ .context = &probe, .running = Probe.onRunning, .window = Probe.onWindow, .visibility = Probe.onVisibility, .foreground = Probe.onForeground, .log = Probe.onLog },
        .opener = server.opener(),
        .retry_ms = 300,
    } };

    var created: c.FILETIME = undefined;
    var times: [3]c.FILETIME = undefined;
    _ = c.GetProcessTimes(c.GetCurrentProcess(), &created, &times[0], &times[1], &times[2]);
    const created_value: i64 = @bitCast((@as(u64, created.dwHighDateTime) << 32) | created.dwLowDateTime);
    const memory = c.VirtualAlloc(null, 2 * block.max_line_bytes, c.MEM_RESERVE | c.MEM_COMMIT, c.PAGE_READWRITE) orelse return error.OutOfMemory;
    defer _ = c.VirtualFree(memory, 0, c.MEM_RELEASE);
    const buffers = @as([*]u8, @ptrCast(memory))[0 .. 2 * block.max_line_bytes];
    const format = "{{\"processId\":{d},\"creationFileTimeUtc\":{d},\"hasVisibleWindow\":{},\"hasBackgroundWindow\":{}}}";
    var shown: [192]u8 = undefined;
    var minimized: [192]u8 = undefined;
    const lines: Lines = .{
        .server = server,
        .shown = try std.fmt.bufPrint(&shown, format, .{ c.GetCurrentProcessId(), created_value, true, false }),
        .minimized = try std.fmt.bufPrint(&minimized, format, .{ c.GetCurrentProcessId(), created_value, false, true }),
        .a = buffers[0..block.max_line_bytes],
        .b = buffers[block.max_line_bytes..],
    };

    const thread = c.CreateThread(null, 512 * 1024, Run.entry, &context, c.STACK_SIZE_PARAM_IS_A_RESERVATION, null).?;
    defer _ = c.CloseHandle(thread);
    try std.testing.expectEqual(c.WAIT_OBJECT_0, c.WaitForSingleObject(probe.running, 5000));
    try std.testing.expect(probe.waitVisible(false));

    // Exactly one stream at start; its first line is the current sample.
    try std.testing.expect(server.waitCounts(.{ .streams = 1, .lines = 1 }, 5000));
    try std.testing.expectEqual(State.absent, lines.state(lines.first(1).?));

    _ = c.ShowWindow(window, c.SW_SHOWNOACTIVATE);
    try std.testing.expect(probe.waitVisible(true));
    try std.testing.expect(lines.wait(1, .visible));
    _ = c.ShowWindow(window, c.SW_SHOWMINNOACTIVE);
    try std.testing.expect(probe.waitVisible(false));
    try std.testing.expect(lines.wait(1, .background));

    // One line per change of this window, no line without a content change,
    // and still the one stream request.
    try std.testing.expectEqual(@as(usize, 1), lines.transitions(1, .visible));
    try std.testing.expectEqual(@as(usize, 1), lines.transitions(1, .background));
    try std.testing.expectEqual(@as(usize, 0), lines.repeats());
    try std.testing.expectEqual(@as(usize, 1), server.requestCount());
    try std.testing.expectEqual(@as(usize, 1), server.uploadCounts().streams);

    // The backend ends the stream: the block reopens and first writes the
    // current sample (the window is still minimized).
    server.endStream();
    try std.testing.expect(server.waitCounts(.{ .streams = 2, .server_ends = 1 }, 5000));
    try std.testing.expect(lines.wait(2, .background));
    try std.testing.expectEqual(State.background, lines.state(lines.first(2).?));

    // The backend ends it again; the next open is answered 409 and writes no
    // line; the open after `retry_ms` is accepted and writes the current sample.
    server.endStream();
    try std.testing.expect(server.waitCounts(.{ .streams = 3, .server_ends = 2 }, 5000));
    try std.testing.expect(lines.wait(3, .background));
    try std.testing.expectEqual(@as(usize, 4), server.requestCount());
    try std.testing.expectEqual(@as(usize, 0), server.uploadCounts().rejected_body_bytes);
    try std.testing.expectEqual(State.background, lines.state(lines.first(3).?));
    try std.testing.expectEqual(@as(usize, 0), lines.repeats());

    // Stop closes the body (the server sees the terminating chunk) and the
    // block thread exits 0.
    _ = c.SetEvent(stop);
    _ = c.PostThreadMessageW(c.GetThreadId(thread), c.WM_QUIT, 0, 0);
    try std.testing.expectEqual(c.WAIT_OBJECT_0, c.WaitForSingleObject(thread, 5000));
    var exit_code: c.DWORD = 99;
    _ = c.GetExitCodeThread(thread, &exit_code);
    try std.testing.expectEqual(@as(c.DWORD, 0), exit_code);
    try std.testing.expect(server.waitCounts(.{ .client_ends = 1 }, 5000));
    try std.testing.expectEqual(@as(usize, 0), server.uploadCounts().drops);
    try std.testing.expectEqual(@as(usize, 3), server.uploadCounts().streams);
    server.finish();
}
