//! OverlayToggleHotkey: the toggle-performance-overlay-foreground hotkey. The
//! same registration and failure report as ForceTerminateHotkey (low-level
//! keyboard/mouse hooks on the block's own thread and message loop, rebind on
//! a key change, one balloon when a hook cannot be installed). A press asks
//! the backend to toggle the overlay of its current foreground software
//! (POST /api/performance-overlay/toggle without a software id) on one action
//! worker and reports the outcome in one tray balloon. The overlay setting
//! itself is the backend's; this block keeps no copy of it.
//!
//! The tray's 「性能浮窗：<前台软件名>」 item uses `readForeground` and
//! `toggleSoftware` from here on its own thread: the same requests, one owner
//! of their wire format.
const std = @import("std");
const memory = @import("../memory.zig");
const points = @import("desktop_points_values").Bound;
const w = @import("../win32.zig");
const c = w.c;
const definition = @import("definition.zig");
const force_terminate = @import("force_terminate.zig");
const channel_module = @import("../backend/channel.zig");
const lookup = @import("../text/lookup.zig");

pub const rebind_message = c.WM_APP + 1;
pub const toggle_path = "/api/performance-overlay/toggle";
pub const foreground_path = "/api/performance-overlay/foreground";

pub const Host = struct {
    context: *anyopaque,
    running: *const fn (*anyopaque) void,
    /// Current effective binding (already enabled and not empty), read under the host lock.
    binding: *const fn (*anyopaque) definition.Definition,
    balloon: *const fn (*anyopaque, *const lookup.Balloon) void,
    text: *const fn (*anyopaque) *const lookup.Text,
    backend_ready: *const fn (*anyopaque) bool,
};
pub const Config = struct {
    ledger: *memory.Owner,
    host: Host,
    opener: channel_module.Opener,
    thread_stack_bytes: usize,
};

/// Request and response memory of one call; the caller owns it.
pub const Workspace = struct {
    channel: channel_module.Channel = .{},
    request: [points.overlay_toggle_request_bytes]u8 = undefined,
    response: [points.overlay_toggle_response_bytes]u8 = undefined,
    arena: [points.overlay_toggle_parse_bytes]u8 align(16) = undefined,
};

/// What the backend answered: the software it acted on (or read) with its
/// switch, or the reason it did not act (409).
pub const Outcome = union(enum) {
    software: struct { id: []const u8, name: []const u8, enabled: bool },
    rejected: []const u8,
};

/// Reason codes (PerformanceOverlayRejections) that mean "the foreground has no
/// attributable software", shown with their own text; any other code is shown
/// as it is inside the failure text.
pub fn noForegroundSoftware(reason: []const u8) bool {
    const codes = [_][]const u8{ "no-desktop-observation", "no-foreground-window", "foreground-process-not-found", "foreground-not-attributed" };
    for (codes) |code| if (std.mem.eql(u8, reason, code)) return true;
    return false;
}

/// POST /api/performance-overlay/toggle. A null `software_id` lets the backend
/// take its current foreground software; a null `enabled` inverts the switch.
pub fn toggleSoftware(workspace: *Workspace, opener: channel_module.Opener, stop: c.HANDLE, software_id: ?[]const u8, enabled: ?bool) !Outcome {
    var writer = std.Io.Writer.fixed(&workspace.request);
    try writer.writeAll("{");
    if (software_id) |id| {
        try writer.writeAll("\"softwareId\":");
        try channel_module.jsonString(&writer, id);
    }
    if (enabled) |value| try writer.print("{s}\"enabled\":{}", .{ if (software_id != null) "," else "", value });
    try writer.writeAll("}");
    return call(workspace, opener, stop, "POST", toggle_path, writer.buffered());
}

/// GET /api/performance-overlay/foreground for one process (the window that
/// was foreground before the tray took the focus).
pub fn readForeground(workspace: *Workspace, opener: channel_module.Opener, stop: c.HANDLE, pid: u32, created: i64) !Outcome {
    var path: [128]u8 = undefined;
    const text = try std.fmt.bufPrint(&path, "{s}?processId={d}&processStartKey={d}", .{ foreground_path, pid, created });
    return call(workspace, opener, stop, "GET", text, "");
}

fn call(workspace: *Workspace, opener: channel_module.Opener, stop: c.HANDLE, method: []const u8, path: []const u8, body: []const u8) !Outcome {
    defer workspace.channel.close();
    try opener.open(&workspace.channel, stop);
    const response = try workspace.channel.request(method, path, body, &workspace.response);
    var arena = std.heap.FixedBufferAllocator.init(&workspace.arena);
    return parseOutcome(arena.allocator(), response.status, response.body);
}

pub fn parseOutcome(allocator: std.mem.Allocator, status: u16, body: []const u8) !Outcome {
    if (status == 409) {
        const Rejection = struct { reason: []const u8 };
        const value = std.json.parseFromSliceLeaky(Rejection, allocator, body, .{ .ignore_unknown_fields = true }) catch return error.InvalidOverlayResponse;
        return .{ .rejected = value.reason };
    }
    if (status < 200 or status > 299) return error.BackendHttpStatus;
    const Software = struct {
        softwareId: []const u8,
        softwareName: ?[]const u8 = null,
        enabled: bool,
        runtimeApplicationDisposition: ?[]const u8 = null,
        runtimeFailureCode: ?[]const u8 = null,
    };
    const value = std.json.parseFromSliceLeaky(Software, allocator, body, .{ .ignore_unknown_fields = true }) catch return error.InvalidOverlayResponse;
    if (value.runtimeApplicationDisposition) |disposition| {
        if (std.mem.eql(u8, disposition, "savedNotApplied") or std.mem.eql(u8, disposition, "committedWithDeliveryFailures"))
            return .{ .rejected = value.runtimeFailureCode orelse disposition };
    }
    return .{ .software = .{ .id = value.softwareId, .name = value.softwareName orelse value.softwareId, .enabled = value.enabled } };
}

var current: ?*Block = null;

const Block = struct {
    config: Config,
    matcher: definition.Matcher = definition.Matcher.init(.{}),
    keyboard: c.HHOOK = null,
    mouse: c.HHOOK = null,
    quit: c.HANDLE = null,
    action: c.HANDLE = null,
    busy: std.atomic.Value(bool) = .init(false),

    fn unhook(self: *Block) void {
        if (self.keyboard != null) _ = c.UnhookWindowsHookEx(self.keyboard);
        if (self.mouse != null) _ = c.UnhookWindowsHookEx(self.mouse);
        self.keyboard = null;
        self.mouse = null;
    }
    fn apply(self: *Block, binding: definition.Definition) void {
        self.unhook();
        self.matcher = definition.Matcher.init(binding);
        if (binding.isEmpty()) return;
        const module = c.GetModuleHandleW(null);
        if (binding.usesKeyboard()) self.keyboard = c.SetWindowsHookExW(c.WH_KEYBOARD_LL, keyboardProc, module, 0);
        if (binding.usesMouse()) self.mouse = c.SetWindowsHookExW(c.WH_MOUSE_LL, mouseProc, module, 0);
        if ((binding.usesKeyboard() and self.keyboard == null) or (binding.usesMouse() and self.mouse == null)) {
            self.unhook();
            const text = self.config.host.text(self.config.host.context);
            const value = lookup.balloon(.warning, text.overlay_title, text.overlay_hotkey_enable_failed, &.{});
            self.config.host.balloon(self.config.host.context, &value);
        }
    }
    /// Hook context: hand off; one action at a time, a press while one runs is dropped.
    fn trigger(self: *Block) void {
        if (self.busy.cmpxchgStrong(false, true, .acq_rel, .acquire) != null) return;
        _ = c.SetEvent(self.action);
    }
    fn worker(context: ?*anyopaque) callconv(.winapi) c.DWORD {
        const self: *Block = @ptrCast(@alignCast(context.?));
        const handles = [_]c.HANDLE{ self.quit, self.action };
        while (c.WaitForMultipleObjects(handles.len, &handles, c.FALSE, c.INFINITE) == c.WAIT_OBJECT_0 + 1) {
            self.act();
            self.busy.store(false, .release);
        }
        return 0;
    }
    fn act(self: *Block) void {
        const host = self.config.host;
        if (!host.backend_ready(host.context)) {
            const value = unavailable(host.text(host.context));
            host.balloon(host.context, &value);
            return;
        }
        var lease = self.config.ledger.acquire(.overlay_hotkey_call, @sizeOf(Workspace)) catch |err| {
            const value = report(host.text(host.context), err);
            host.balloon(host.context, &value);
            return;
        };
        defer _ = lease.endUse();
        const workspace = lease.as(Workspace);
        workspace.channel = .{};
        const value = report(host.text(host.context), toggleSoftware(workspace, self.config.opener, self.quit, null, null));
        host.balloon(host.context, &value);
        lease.publish();
    }
};

pub fn unavailable(text: *const lookup.Text) lookup.Balloon {
    return lookup.balloon(.warning, text.overlay_title, text.overlay_backend_unavailable, &.{});
}

/// The one balloon a toggle reports.
pub fn report(text: *const lookup.Text, outcome: anyerror!Outcome) lookup.Balloon {
    const result = outcome catch |err| return lookup.balloon(.@"error", text.overlay_title, text.overlay_toggle_failed, &.{@errorName(err)});
    return switch (result) {
        .software => |software| lookup.balloon(.info, text.overlay_title, if (software.enabled) text.overlay_enabled_format else text.overlay_disabled_format, &.{software.name}),
        .rejected => |reason| if (noForegroundSoftware(reason))
            lookup.balloon(.info, text.overlay_title, text.overlay_no_foreground, &.{})
        else
            lookup.balloon(.warning, text.overlay_title, text.overlay_toggle_failed, &.{reason}),
    };
}

/// Runs on the block thread until WM_QUIT. Returns only after its hooks are
/// removed and its action worker has joined.
pub fn run(stop: c.HANDLE, config: Config) !void {
    var message: c.MSG = undefined;
    // Create this thread's queue before checking stop: the host sets stop and
    // then posts WM_QUIT, so one of the two is always observed.
    _ = c.PeekMessageW(&message, null, c.WM_USER, c.WM_USER, c.PM_NOREMOVE);
    if (w.stopped(stop)) return;
    var block: Block = .{ .config = config };
    block.quit = c.CreateEventW(null, c.TRUE, c.FALSE, null) orelse return error.Event;
    defer _ = c.CloseHandle(block.quit);
    block.action = c.CreateEventW(null, c.FALSE, c.FALSE, null) orelse return error.Event;
    defer _ = c.CloseHandle(block.action);
    const worker = c.CreateThread(null, config.thread_stack_bytes, Block.worker, &block, c.STACK_SIZE_PARAM_IS_A_RESERVATION, null) orelse return error.Thread;
    defer {
        _ = c.SetEvent(block.quit);
        _ = c.WaitForSingleObject(worker, c.INFINITE);
        _ = c.CloseHandle(worker);
    }
    std.debug.assert(current == null);
    current = &block;
    defer current = null;
    defer block.unhook();
    block.apply(config.host.binding(config.host.context));
    config.host.running(config.host.context);
    while (true) {
        const result = c.GetMessageW(&message, null, 0, 0);
        if (result == 0) return;
        if (result == -1) return error.MessageLoop;
        if (message.hwnd == null and message.message == rebind_message) {
            block.apply(config.host.binding(config.host.context));
            continue;
        }
        _ = c.TranslateMessage(&message);
        _ = c.DispatchMessageW(&message);
    }
}

fn keyboardProc(code: c_int, wp: c.WPARAM, lp: c.LPARAM) callconv(.winapi) c.LRESULT {
    if (code == c.HC_ACTION) if (current) |block| {
        const info: *const c.KBDLLHOOKSTRUCT = @ptrFromInt(@as(usize, @bitCast(lp)));
        switch (wp) {
            c.WM_KEYUP, c.WM_SYSKEYUP => block.matcher.keyUp(info.vkCode),
            c.WM_KEYDOWN, c.WM_SYSKEYDOWN => if (block.matcher.keyDown(info.vkCode)) {
                block.trigger();
                return 1;
            },
            else => {},
        }
    };
    return c.CallNextHookEx(null, code, wp, lp);
}
fn mouseProc(code: c_int, wp: c.WPARAM, lp: c.LPARAM) callconv(.winapi) c.LRESULT {
    if (code == c.HC_ACTION) if (current) |block| {
        const info: *const c.MSLLHOOKSTRUCT = @ptrFromInt(@as(usize, @bitCast(lp)));
        if (force_terminate.mouseButton(@intCast(wp), info.mouseData)) |button| {
            if (!button.down) {
                block.matcher.keyUp(button.key);
            } else if (block.matcher.keyDown(button.key)) {
                block.trigger();
                return 1;
            }
        }
    };
    return c.CallNextHookEx(null, code, wp, lp);
}

test "toggle responses become one balloon each" {
    var storage: [4096]u8 = undefined;
    var arena = std.heap.FixedBufferAllocator.init(&storage);
    const text = lookup.select("en-US");
    const on = try parseOutcome(arena.allocator(), 200, "{\"softwareId\":\"game\",\"softwareName\":\"Game\",\"enabled\":true,\"revision\":\"r\"}");
    try std.testing.expectEqualStrings("Game", on.software.name);
    try std.testing.expect(on.software.enabled);
    const shown = report(text, on);
    try std.testing.expectEqual(lookup.Icon.info, shown.icon);
    try std.testing.expect(std.mem.indexOf(u16, shown.bodySlice(), std.unicode.utf8ToUtf16LeStringLiteral("Game")) != null);
    // A software named only by id is reported by its id.
    const by_id = try parseOutcome(arena.allocator(), 200, "{\"softwareId\":\"game\",\"enabled\":false}");
    try std.testing.expectEqualStrings("game", by_id.software.name);
    const none = try parseOutcome(arena.allocator(), 409, "{\"reason\":\"foreground-not-attributed\"}");
    try std.testing.expectEqualStrings("foreground-not-attributed", none.rejected);
    const no_foreground = report(text, none);
    const expected_none = lookup.balloon(.info, text.overlay_title, text.overlay_no_foreground, &.{});
    try std.testing.expectEqualSlices(u16, expected_none.bodySlice(), no_foreground.bodySlice());
    const other = report(text, try parseOutcome(arena.allocator(), 409, "{\"reason\":\"performance-overlay-too-many-software\"}"));
    try std.testing.expectEqual(lookup.Icon.warning, other.icon);
    try std.testing.expect(std.mem.indexOf(u16, other.bodySlice(), std.unicode.utf8ToUtf16LeStringLiteral("performance-overlay-too-many-software")) != null);
    try std.testing.expectError(error.BackendHttpStatus, parseOutcome(arena.allocator(), 500, ""));
    try std.testing.expectEqual(lookup.Icon.@"error", report(text, error.BackendHttpStatus).icon);
    const partial = try parseOutcome(arena.allocator(), 202, "{\"softwareId\":\"game\",\"enabled\":true,\"runtimeApplicationDisposition\":\"committedWithDeliveryFailures\",\"runtimeFailureCode\":\"delivery-failed\"}");
    try std.testing.expectEqualStrings("delivery-failed", partial.rejected);
    try std.testing.expectEqual(lookup.Icon.warning, report(text, partial).icon);
}

test "Ledger overlay action closes the channel and returns its lease before the next action" {
    const Fixture = @import("../memory_action_test_host.zig").Fixture;
    const server = try mock.Server.start(.{ .script = &.{.{ .status = "200 OK", .body = "{\"softwareId\":\"already-running\",\"softwareName\":\"Running app\",\"enabled\":true}", .close_after = true, .repeat = true }} });
    defer server.deinit();
    var owner: memory.Owner = .{};
    try owner.init();
    defer if (owner.instance != null) owner.deinit() catch @panic("overlay action retained a lease");
    var fixture: Fixture = .{};
    var block: Block = .{ .config = .{ .ledger = &owner, .host = fixture.host(Host), .opener = server.opener(), .thread_stack_bytes = points.thread_stack_bytes }, .quit = server.stop };
    block.act();
    block.act();
    try std.testing.expectEqual(@as(usize, 2), fixture.balloons);
    try std.testing.expectEqual(lookup.Icon.info, fixture.last.icon);
    try std.testing.expectEqual(@as(usize, 2), server.countLines("POST /api/performance-overlay/toggle HTTP/1.1"));
    var body: [64]u8 = undefined;
    try std.testing.expectEqualStrings("{}", server.copyLastBody(&body));
    // Successful normal destruction proves no action retained a protection.
    try owner.deinit();
}

const mock = @import("../backend/mock_backend.zig");
test "toggle requests name the software and the value only when given" {
    var server = try mock.Server.start(.{ .script = &.{
        .{ .status = "200 OK", .body = "{\"softwareId\":\"game\",\"softwareName\":\"Game\",\"enabled\":true}" },
        .{ .status = "409 Conflict", .body = "{\"reason\":\"no-foreground-window\"}" },
        .{ .status = "200 OK", .body = "{\"softwareId\":\"x\",\"softwareName\":\"X\",\"enabled\":false}" },
    } });
    defer server.deinit();
    var owner: memory.Owner = .{};
    try owner.init();
    defer owner.deinit() catch @panic("toggle request test retained a lease");
    var lease = try owner.acquire(.overlay_hotkey_call, @sizeOf(Workspace));
    defer _ = lease.endUse();
    const workspace = lease.as(Workspace);
    workspace.channel = .{};
    const explicit = try toggleSoftware(workspace, server.opener(), server.stop, "game", true);
    try std.testing.expect(explicit.software.enabled);
    var body: [256]u8 = undefined;
    try std.testing.expectEqualStrings("{\"softwareId\":\"game\",\"enabled\":true}", server.copyLastBody(&body));
    const foreground = try toggleSoftware(workspace, server.opener(), server.stop, null, null);
    try std.testing.expectEqualStrings("no-foreground-window", foreground.rejected);
    try std.testing.expectEqualStrings("{}", server.copyLastBody(&body));
    _ = try readForeground(workspace, server.opener(), server.stop, 42, 133);
    try std.testing.expect(server.logged("GET /api/performance-overlay/foreground?processId=42&processStartKey=133 HTTP/1.1"));
    lease.publish();
    server.finish();
}
