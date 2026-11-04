//! The whole block against the mock backend (backend/mock_backend.zig): the test's own
//! process is the target, a fixture window the test creates is its window,
//! and the injected foreground reading names that window, so the user's
//! foreground window is never touched. The fixture lies outside the virtual
//! screen. Captures are window-only (PrintWindow) and written when
//! RM_OVERLAY_CAPTURE_DIR is set.
const std = @import("std");
const memory = @import("../memory.zig");
const c = @import("../backend/c.zig").win;
const mock = @import("../backend/mock_backend.zig");
const overlay = @import("root.zig");
const model = @import("model.zig");
const window = @import("window.zig");

const fixture_class = std.unicode.utf8ToUtf16LeStringLiteral("ResourceManager.Desktop.OverlayFollowFixture");

const Fixture = struct {
    ready: c.HANDLE,
    handle: c.HWND = null,
    thread_id: c.DWORD = 0,
    x: i32,
    y: i32,
    fn procedure(handle: c.HWND, message: c.UINT, wparam: c.WPARAM, lparam: c.LPARAM) callconv(.winapi) c.LRESULT {
        if (message == c.WM_PAINT) {
            var paint: c.PAINTSTRUCT = undefined;
            const dc = c.BeginPaint(handle, &paint);
            const brush = c.CreateSolidBrush(0x00503020);
            _ = c.FillRect(dc, &paint.rcPaint, brush);
            _ = c.DeleteObject(brush);
            _ = c.EndPaint(handle, &paint);
            return 0;
        }
        return c.DefWindowProcW(handle, message, wparam, lparam);
    }
    fn main(raw: ?*anyopaque) callconv(.winapi) c.DWORD {
        const self: *Fixture = @ptrCast(@alignCast(raw.?));
        _ = c.SetThreadDpiAwarenessContext(@ptrFromInt(@as(usize, @bitCast(@as(isize, -4)))));
        const instance: c.HINSTANCE = @ptrCast(c.GetModuleHandleW(null));
        var class: c.WNDCLASSEXW = std.mem.zeroes(c.WNDCLASSEXW);
        class.cbSize = @sizeOf(c.WNDCLASSEXW);
        class.lpfnWndProc = procedure;
        class.hInstance = instance;
        class.lpszClassName = fixture_class;
        _ = c.RegisterClassExW(&class);
        defer _ = c.UnregisterClassW(fixture_class, instance);
        self.thread_id = c.GetCurrentThreadId();
        // WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE: no taskbar entry and never activated.
        self.handle = c.CreateWindowExW(0x80 | 0x08000000, fixture_class, null, c.WS_POPUP, self.x, self.y, 640, 400, null, null, instance, null);
        if (self.handle != null) _ = c.ShowWindow(self.handle, c.SW_SHOWNOACTIVATE);
        _ = c.SetEvent(self.ready);
        if (self.handle == null) return 1;
        var message: c.MSG = undefined;
        while (c.GetMessageW(&message, null, 0, 0) > 0) {
            _ = c.TranslateMessage(&message);
            _ = c.DispatchMessageW(&message);
        }
        return 0;
    }
};

const Context = struct {
    owner: *memory.Owner,
    stop: c.HANDLE,
    settings_event: c.HANDLE,
    config: overlay.Config,
    fixture: *Fixture,
    foreground: bool = true,
    overlay_enabled: bool = true,
    failure: ?anyerror = null,
    log_count: usize = 0,
    fn log(raw: *anyopaque, message: []const u8) void {
        const self: *Context = @ptrCast(@alignCast(raw));
        self.log_count += 1;
        std.debug.print("{s}\n", .{message});
    }
    fn language(_: *anyopaque, output: *[32]u8) usize {
        @memcpy(output[0..5], "en-US");
        return 5;
    }
    fn settings(raw: *anyopaque, output: []u8) usize {
        const self: *Context = @ptrCast(@alignCast(raw));
        const enabled = @atomicLoad(bool, &self.overlay_enabled, .seq_cst);
        @memcpy(output[0..overlay_settings.len], overlay_settings);
        if (enabled) return overlay_settings.len;
        // Same complete backend section, only its enabled bit changes.
        const at = comptime std.mem.indexOf(u8, overlay_settings, "\"enabled\":true").? + "\"enabled\":".len;
        @memmove(output[at + 5 .. overlay_settings.len + 1], output[at + 4 .. overlay_settings.len]);
        @memcpy(output[at..][0..5], "false");
        return overlay_settings.len + 1;
    }
    fn foregroundWindow(raw: *anyopaque) c.HWND {
        const self: *Context = @ptrCast(@alignCast(raw));
        return if (@atomicLoad(bool, &self.foreground, .seq_cst)) self.fixture.handle else null;
    }
    fn main(raw: ?*anyopaque) callconv(.winapi) c.DWORD {
        const self: *Context = @ptrCast(@alignCast(raw.?));
        overlay.run(self.owner, self.stop, self.settings_event, .{ .context = self, .log = log, .language = language, .settings = settings, .foreground = foregroundWindow }, self.config) catch |err| {
            self.failure = err;
        };
        return 0;
    }
};

fn overlayWindow() c.HWND {
    var handle: c.HWND = null;
    var rounds: usize = 0;
    while (rounds < 64) : (rounds += 1) {
        handle = c.FindWindowExW(null, handle, window.class_name, null);
        if (handle == null) return null;
        var owner: c.DWORD = 0;
        _ = c.GetWindowThreadProcessId(handle, &owner);
        if (owner == c.GetCurrentProcessId() and c.IsWindowVisible(handle) != 0) return handle;
    }
    return null;
}

fn windowRect(handle: c.HWND) c.RECT {
    var rect: c.RECT = std.mem.zeroes(c.RECT);
    _ = c.GetWindowRect(handle, &rect);
    return rect;
}

/// Waits (bounded) until the overlay is visible and `accept` holds for its rectangle.
fn waitFor(accept: anytype, argument: anytype) !c.RECT {
    var rounds: usize = 0;
    while (rounds < 500) : (rounds += 1) {
        if (overlayWindow()) |handle| {
            const rect = windowRect(handle);
            if (accept(rect, argument)) return rect;
        }
        c.Sleep(10);
    }
    if (overlayWindow()) |handle| {
        const rect = windowRect(handle);
        std.debug.print("overlay at ({d},{d})-({d},{d}), expected {any}\n", .{ rect.left, rect.top, rect.right, rect.bottom, argument });
    } else std.debug.print("overlay not visible\n", .{});
    return error.OverlayDidNotFollow;
}

fn waitHidden() !void {
    var rounds: usize = 0;
    while (rounds < 500) : (rounds += 1) {
        if (overlayWindow() == null) return;
        c.Sleep(10);
    }
    return error.OverlayStayedVisible;
}

const Capture = struct { width: i32, height: i32, opaque_pixels: usize };

/// PrintWindow of one window into a 32-bit DIB; written as BMP when a directory is given.
fn capture(handle: c.HWND, directory: ?[]const u16, name: []const u8) !Capture {
    const rect = windowRect(handle);
    const width = rect.right - rect.left;
    const height = rect.bottom - rect.top;
    if (width <= 0 or height <= 0) return error.EmptyWindow;
    const dc = c.CreateCompatibleDC(null) orelse return error.CaptureUnavailable;
    defer _ = c.DeleteDC(dc);
    var info: c.BITMAPINFO = std.mem.zeroes(c.BITMAPINFO);
    info.bmiHeader.biSize = @sizeOf(c.BITMAPINFOHEADER);
    info.bmiHeader.biWidth = width;
    info.bmiHeader.biHeight = -height;
    info.bmiHeader.biPlanes = 1;
    info.bmiHeader.biBitCount = 32;
    info.bmiHeader.biCompression = c.BI_RGB;
    var bits: ?*anyopaque = null;
    const bitmap = c.CreateDIBSection(dc, &info, c.DIB_RGB_COLORS, &bits, null, 0) orelse return error.CaptureUnavailable;
    defer _ = c.DeleteObject(bitmap);
    const previous = c.SelectObject(dc, bitmap);
    defer _ = c.SelectObject(dc, previous);
    // PW_RENDERFULLCONTENT: the window's own composed content, nothing around it.
    if (c.PrintWindow(handle, dc, 2) == 0) return error.CaptureFailed;
    _ = c.GdiFlush();
    const length: usize = @intCast(width * height * 4);
    const pixels: [*]const u8 = @ptrCast(bits.?);
    var opaque_pixels: usize = 0;
    var index: usize = 0;
    while (index < length) : (index += 4) {
        if (pixels[index] != 0 or pixels[index + 1] != 0 or pixels[index + 2] != 0) opaque_pixels += 1;
    }
    if (directory) |root| try writeBitmap(root, name, pixels[0..length], width, height);
    return .{ .width = width, .height = height, .opaque_pixels = opaque_pixels };
}

fn writeBitmap(root: []const u16, name: []const u8, pixels: []const u8, width: i32, height: i32) !void {
    var path: [1024:0]u16 = undefined;
    var length: usize = root.len;
    @memcpy(path[0..length], root);
    path[length] = '\\';
    length += 1;
    length += try std.unicode.utf8ToUtf16Le(path[length..1000], name);
    path[length] = 0;
    const file = c.CreateFileW(&path, c.GENERIC_WRITE, 0, null, c.CREATE_ALWAYS, c.FILE_ATTRIBUTE_NORMAL, null);
    if (file == c.INVALID_HANDLE_VALUE) return error.CaptureWrite;
    defer _ = c.CloseHandle(file);
    var header: [54]u8 = std.mem.zeroes([54]u8);
    header[0] = 'B';
    header[1] = 'M';
    std.mem.writeInt(u32, header[2..6], @intCast(54 + pixels.len), .little);
    std.mem.writeInt(u32, header[10..14], 54, .little);
    std.mem.writeInt(u32, header[14..18], 40, .little);
    std.mem.writeInt(i32, header[18..22], width, .little);
    std.mem.writeInt(i32, header[22..26], -height, .little);
    std.mem.writeInt(u16, header[26..28], 1, .little);
    std.mem.writeInt(u16, header[28..30], 32, .little);
    var written: c.DWORD = 0;
    if (c.WriteFile(file, &header, header.len, &written, null) == 0) return error.CaptureWrite;
    if (c.WriteFile(file, pixels.ptr, @intCast(pixels.len), &written, null) == 0) return error.CaptureWrite;
}

/// The `performanceOverlay` section as the host hands it over from the settings subscription.
const overlay_settings =
    \\{"software":{"fixture.app":{"enabled":true,"mode":"external","items":[{"kind":"metric","id":"gpu.temp","source":"auto","intervalMs":null,"syncPrecision":"mid"},{"kind":"frames","id":"frames.fps","source":"auto","intervalMs":null,"syncPrecision":"mid"}],"sizeMode":"absolutePixels","fontSizePx":16,"regionWidthRatio":0.25,"regionHeightRatio":0.2,"anchor":"bottomRight","marginPx":12}}}
;

fn movedBy(rect: c.RECT, expected: c.RECT) bool {
    return std.meta.eql(rect, expected);
}

test "the overlay follows its target window's client area and releases everything on stop" {
    // The block and the fixture are per-monitor aware; the test reads physical pixels too.
    const awareness = c.SetThreadDpiAwarenessContext(@ptrFromInt(@as(usize, @bitCast(@as(isize, -4)))));
    defer _ = c.SetThreadDpiAwarenessContext(awareness);
    // The target is this process.
    var created: c.FILETIME = undefined;
    var exited: c.FILETIME = undefined;
    var kernel: c.FILETIME = undefined;
    var user: c.FILETIME = undefined;
    try std.testing.expect(c.GetProcessTimes(c.GetCurrentProcess(), &created, &exited, &kernel, &user) != 0);
    const start_key = (@as(u64, created.dwHighDateTime) << 32) | created.dwLowDateTime;
    var frame_buffer: [1024]u8 = undefined;
    const frame = try std.fmt.bufPrint(&frame_buffer, "{{\"subscriptionId\":\"overlay-0\",\"value\":{{\"version\":3,\"targets\":[{{\"softwareId\":\"fixture.app\",\"processId\":{d},\"processStartKey\":{d}}}],\"items\":[{{\"kind\":\"metric\",\"id\":\"gpu.temp\",\"value\":61.3,\"unit\":\"\u{00B0}C\",\"state\":\"current\"}},{{\"kind\":\"frames\",\"id\":\"frames.fps\",\"value\":144,\"unit\":\"fps\",\"state\":\"current\"}}]}}}}\n", .{ c.GetCurrentProcessId(), start_key });
    // The mock backend answers the subscription with the one frame, on every retry too.
    const server = try mock.Server.start(.{ .script = &.{.{ .status = "200 OK", .stream = &.{frame}, .repeat = true }} });
    defer server.deinit();

    // Capture directory.
    var directory_buffer: [512]u16 = undefined;
    const directory_length = c.GetEnvironmentVariableW(std.unicode.utf8ToUtf16LeStringLiteral("RM_OVERLAY_CAPTURE_DIR"), &directory_buffer, directory_buffer.len);
    const directory: ?[]const u16 = if (directory_length > 0 and directory_length < directory_buffer.len) directory_buffer[0..directory_length] else null;

    // The fixture window, outside every monitor.
    const virtual_right = c.GetSystemMetrics(c.SM_XVIRTUALSCREEN) + c.GetSystemMetrics(c.SM_CXVIRTUALSCREEN);
    const virtual_bottom = c.GetSystemMetrics(c.SM_YVIRTUALSCREEN) + c.GetSystemMetrics(c.SM_CYVIRTUALSCREEN);
    const ready = c.CreateEventW(null, 1, 0, null) orelse return error.EventUnavailable;
    defer _ = c.CloseHandle(ready);
    var fixture: Fixture = .{ .ready = ready, .x = virtual_right + 4000, .y = virtual_bottom + 4000 };
    const fixture_thread = c.CreateThread(null, 128 * 1024, Fixture.main, &fixture, c.STACK_SIZE_PARAM_IS_A_RESERVATION, null) orelse return error.ThreadUnavailable;
    defer {
        _ = c.PostThreadMessageW(fixture.thread_id, c.WM_QUIT, 0, 0);
        _ = c.WaitForSingleObject(fixture_thread, c.INFINITE);
        _ = c.CloseHandle(fixture_thread);
    }
    try std.testing.expectEqual(@as(c.DWORD, c.WAIT_OBJECT_0), c.WaitForSingleObject(ready, 5000));
    try std.testing.expect(fixture.handle != null);

    // The block.
    var owner: memory.Owner = .{};
    try owner.init();
    defer owner.deinit() catch @panic("overlay test owner still in use");
    const stop = c.CreateEventW(null, 1, 0, null) orelse return error.EventUnavailable;
    defer _ = c.CloseHandle(stop);
    const settings_event = c.CreateEventW(null, 0, 0, null) orelse return error.EventUnavailable;
    defer _ = c.CloseHandle(settings_event);
    var section_prefix: [96]u8 = undefined;
    var context: Context = .{
        .owner = &owner,
        .stop = stop,
        .settings_event = settings_event,
        .config = .{
            .opener = server.opener(),
            .sample_interval_ms = 500,
            .settings_bytes = 64 * 1024,
            .retry_ms = 1000,
            .stack_bytes = 256 * 1024,
            .section_prefix = try std.fmt.bufPrint(&section_prefix, "Local\\ResourceManager.Desktop.OverlayFollowTest.{d}", .{c.GetCurrentProcessId()}),
        },
        .fixture = &fixture,
    };
    // An explicit small stack, as every resident thread gets.
    const block = c.CreateThread(null, 256 * 1024, Context.main, &context, c.STACK_SIZE_PARAM_IS_A_RESERVATION, null) orelse return error.ThreadUnavailable;
    var block_joined = false;
    defer if (!block_joined) {
        _ = c.SetEvent(stop);
        _ = c.WaitForSingleObject(block, c.INFINITE);
        _ = c.CloseHandle(block);
    };

    // 1. Shown inside the fixture's client area, anchored bottom-right.
    const Inside = struct {
        fn accept(rect: c.RECT, target: c.HWND) bool {
            const client = clientRect(target);
            return rect.left >= client.left and rect.top >= client.top and rect.right <= client.right and rect.bottom <= client.bottom and rect.right > rect.left;
        }
    };
    const first = try waitFor(Inside.accept, fixture.handle);
    const client_first = clientRect(fixture.handle);
    const right_gap = client_first.right - first.right;
    const bottom_gap = client_first.bottom - first.bottom;
    try std.testing.expect(right_gap >= 0 and bottom_gap >= 0);
    const overlay_handle = overlayWindow().?;
    const shot = capture(overlay_handle, directory, "follow-1-overlay.bmp") catch |err| blk: {
        std.debug.print("overlay capture: {s}\n", .{@errorName(err)});
        break :blk Capture{ .width = 0, .height = 0, .opaque_pixels = 0 };
    };
    _ = capture(fixture.handle, directory, "follow-1-fixture.bmp") catch {};
    std.debug.print("follow: overlay {d}x{d} at gap ({d},{d}); captured {d}x{d}, {d} non-black pixels\n", .{ first.right - first.left, first.bottom - first.top, right_gap, bottom_gap, shot.width, shot.height, shot.opaque_pixels });

    // 2. Moving the target moves the overlay by the same amount.
    try std.testing.expect(c.SetWindowPos(fixture.handle, null, fixture.x + 37, fixture.y + 23, 0, 0, c.SWP_NOSIZE | c.SWP_NOZORDER | c.SWP_NOACTIVATE) != 0);
    const moved_expected: c.RECT = .{ .left = first.left + 37, .top = first.top + 23, .right = first.right + 37, .bottom = first.bottom + 23 };
    _ = try waitFor(movedBy, moved_expected);

    // 3. Resizing keeps the bottom-right gap and the size.
    try std.testing.expect(c.SetWindowPos(fixture.handle, null, 0, 0, 740, 450, c.SWP_NOMOVE | c.SWP_NOZORDER | c.SWP_NOACTIVATE) != 0);
    const client_resized = clientRect(fixture.handle);
    const resized_expected: c.RECT = .{ .left = client_resized.right - right_gap - (first.right - first.left), .top = client_resized.bottom - bottom_gap - (first.bottom - first.top), .right = client_resized.right - right_gap, .bottom = client_resized.bottom - bottom_gap };
    _ = try waitFor(movedBy, resized_expected);
    _ = capture(overlayWindow().?, directory, "follow-3-overlay-resized.bmp") catch {};

    // 4. A backend settings notification hides the existing overlay while its
    // target process/window and stream data already exist. No process restart.
    @atomicStore(bool, &context.overlay_enabled, false, .seq_cst);
    _ = c.SetEvent(settings_event);
    try waitHidden();
    try std.testing.expect(c.IsWindow(fixture.handle) != 0);

    // 5. Not foreground also stays hidden on the next window event.
    @atomicStore(bool, &context.foreground, false, .seq_cst);
    try std.testing.expect(c.SetWindowPos(fixture.handle, null, fixture.x, fixture.y, 0, 0, c.SWP_NOSIZE | c.SWP_NOZORDER | c.SWP_NOACTIVATE) != 0);
    try waitHidden();

    // 6. Stop releases the windows and the class before its scoped owner.
    _ = c.SetEvent(stop);
    try std.testing.expectEqual(@as(c.DWORD, c.WAIT_OBJECT_0), c.WaitForSingleObject(block, 10000));
    _ = c.CloseHandle(block);
    block_joined = true;
    if (context.failure) |err| return err;
    try std.testing.expect(c.FindWindowExW(null, null, window.class_name, null) == null);
    server.finish();
    try std.testing.expect(server.logged("POST /api/subscriptions/stream HTTP/1.1\r\n"));
    var body: [8192]u8 = undefined;
    try std.testing.expect(std.mem.indexOf(u8, server.copyLastBody(&body), "/api/performance-overlay/subscribe?intervalMs=") != null);
    try std.testing.expect(std.mem.indexOf(u8, server.copyLastBody(&body), "\"items\":[{\"kind\":\"metric\",\"id\":\"gpu.temp\",\"source\":\"auto\",\"intervalMs\":null,\"syncPrecision\":\"mid\"},") != null);
}

fn clientRect(handle: c.HWND) c.RECT {
    var rect: c.RECT = std.mem.zeroes(c.RECT);
    _ = c.GetClientRect(handle, &rect);
    var origin: c.POINT = .{ .x = 0, .y = 0 };
    _ = c.ClientToScreen(handle, &origin);
    return .{ .left = origin.x, .top = origin.y, .right = origin.x + rect.right, .bottom = origin.y + rect.bottom };
}
