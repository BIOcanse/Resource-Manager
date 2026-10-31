//! ForceTerminateHotkey (uid 87): low-level keyboard/mouse hooks on the
//! block's own thread and message loop. Stop posts WM_QUIT to this thread; a
//! binding change while enabled is re-registered here (scenario 8). The
//! terminate action is the C# backend call, run by one action worker.
const std = @import("std");
const memory = @import("../memory.zig");
const points = @import("desktop_points_values").Bound;
const w = @import("../win32.zig");
const c = w.c;
const definition = @import("definition.zig");
const channel_module = @import("../backend/channel.zig");
const lookup = @import("../text/lookup.zig");

pub const rebind_message = c.WM_APP + 1;
pub const max_targets = points.force_terminate_maximum_targets;
/// Bound of distinct hung-window PIDs collected before sorting (C# HashSet).
const max_candidates = points.force_terminate_maximum_candidates;
pub const terminate_path = "/api/system/processes/terminate";

pub const Target = struct { pid: u32, start_key: u64 };
pub const Resolve = *const fn (foreground: ?u32, out: *[max_targets]Target) usize;

pub const Host = struct {
    context: *anyopaque,
    running: *const fn (*anyopaque) void,
    /// Current effective binding (already enabled and safe), read under the host lock.
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
    resolve: Resolve = resolveTargets,
};

var current: ?*Block = null;

const Workspace = struct {
    channel: channel_module.Channel = .{},
    request: [points.force_terminate_request_bytes]u8 = undefined,
    response: [points.force_terminate_response_bytes]u8 = undefined,
    arena: [points.force_terminate_parse_bytes]u8 align(16) = undefined,
    targets: [max_targets]Target = undefined,
};

const Block = struct {
    config: Config,
    matcher: definition.Matcher = definition.Matcher.init(.{}),
    keyboard: c.HHOOK = null,
    mouse: c.HHOOK = null,
    quit: c.HANDLE = null,
    action: c.HANDLE = null,
    busy: std.atomic.Value(bool) = .init(false),
    foreground: std.atomic.Value(u32) = .init(0),

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
            const value = lookup.balloon(.warning, text.force_terminate_title, text.force_terminate_enable_failed, &.{});
            self.config.host.balloon(self.config.host.context, &value);
        }
    }
    /// Hook context: read the foreground process and hand off; one action at a time.
    fn trigger(self: *Block) void {
        if (self.busy.cmpxchgStrong(false, true, .acq_rel, .acquire) != null) return;
        self.foreground.store(foregroundProcess() orelse 0, .release);
        _ = c.SetEvent(self.action);
    }
    fn worker(context: ?*anyopaque) callconv(.winapi) c.DWORD {
        const self: *Block = @ptrCast(@alignCast(context.?));
        const handles = [_]c.HANDLE{ self.quit, self.action };
        while (c.WaitForMultipleObjects(handles.len, &handles, c.FALSE, c.INFINITE) == c.WAIT_OBJECT_0 + 1) {
            const pid = self.foreground.load(.acquire);
            self.act(if (pid == 0) null else pid);
            self.busy.store(false, .release);
        }
        return 0;
    }
    fn act(self: *Block, foreground: ?u32) void {
        const host = self.config.host;
        const text = host.text(host.context);
        var lease = self.config.ledger.acquire(.force_terminate_call, @sizeOf(Workspace)) catch {
            const value = lookup.balloon(.@"error", text.force_terminate_title, text.force_terminate_failed, &.{});
            host.balloon(host.context, &value);
            return;
        };
        defer _ = lease.endUse();
        const workspace = lease.as(Workspace);
        workspace.channel = .{};
        const count = self.config.resolve(foreground, &workspace.targets);
        var value: lookup.Balloon = undefined;
        if (count == 0) {
            value = lookup.balloon(.info, text.force_terminate_title, text.no_foreground_process, &.{});
        } else if (!host.backend_ready(host.context)) {
            value = lookup.balloon(.warning, text.force_terminate_title, text.backend_unavailable_no_terminate, &.{});
        } else if (self.terminate(workspace, workspace.targets[0..count])) |result| {
            var succeeded: [20]u8 = undefined;
            var total: [20]u8 = undefined;
            value = lookup.balloon(.info, text.force_terminate_title, text.terminate_result, &.{
                std.fmt.bufPrint(&succeeded, "{d}", .{result.succeeded}) catch unreachable,
                std.fmt.bufPrint(&total, "{d}", .{result.total}) catch unreachable,
            });
        } else |_| {
            value = lookup.balloon(.@"error", text.force_terminate_title, text.force_terminate_failed, &.{});
        }
        host.balloon(host.context, &value);
        lease.publish();
    }
    fn terminate(self: *Block, workspace: *Workspace, targets: []const Target) !Summary {
        defer workspace.channel.close();
        try self.config.opener.open(&workspace.channel, self.quit);
        const body = try requestBody(&workspace.request, targets);
        const response = try workspace.channel.request("POST", terminate_path, body, &workspace.response);
        if (response.status < 200 or response.status > 299) return error.BackendHttpStatus;
        var arena = std.heap.FixedBufferAllocator.init(&workspace.arena);
        const result = try summarize(arena.allocator(), response.body);
        return result;
    }
};

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
        if (mouseButton(@intCast(wp), info.mouseData)) |button| {
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

pub const Button = struct { key: u8, down: bool };
/// WM_*BUTTON* to the virtual key the definition stores (1, 2, 4, 5, 6).
pub fn mouseButton(message: u32, data: u32) ?Button {
    return switch (message) {
        0x201, 0x202 => .{ .key = 1, .down = message == 0x201 },
        0x204, 0x205 => .{ .key = 2, .down = message == 0x204 },
        0x207, 0x208 => .{ .key = 4, .down = message == 0x207 },
        0x20b, 0x20c => switch ((data >> 16) & 0xffff) {
            1 => .{ .key = 5, .down = message == 0x20b },
            2 => .{ .key = 6, .down = message == 0x20b },
            else => null,
        },
        else => null,
    };
}

fn foregroundProcess() ?u32 {
    const window = c.GetForegroundWindow() orelse return null;
    var pid: c.DWORD = 0;
    _ = c.GetWindowThreadProcessId(window, &pid);
    return if (pid > 0) pid else null;
}

/// Hung visible windows plus the foreground process, sorted, first 64, each
/// with its creation time; critical system processes and this process excluded.
pub fn resolveTargets(foreground: ?u32, out: *[max_targets]Target) usize {
    var candidates: Candidates = .{};
    _ = c.EnumWindows(collectHung, @bitCast(@intFromPtr(&candidates)));
    if (foreground) |pid| candidates.add(pid);
    std.mem.sort(u32, candidates.items[0..candidates.len], {}, std.sort.asc(u32));
    var count: usize = 0;
    for (candidates.items[0..@min(candidates.len, max_targets)]) |pid| {
        if (allowedTarget(pid)) |target| {
            out[count] = target;
            count += 1;
        }
    }
    return count;
}
const Candidates = struct {
    items: [max_candidates]u32 = undefined,
    len: usize = 0,
    fn add(self: *Candidates, pid: u32) void {
        if (pid == 0 or std.mem.indexOfScalar(u32, self.items[0..self.len], pid) != null or self.len == self.items.len) return;
        self.items[self.len] = pid;
        self.len += 1;
    }
};
fn collectHung(window: c.HWND, parameter: c.LPARAM) callconv(.winapi) c.BOOL {
    const candidates: *Candidates = @ptrFromInt(@as(usize, @bitCast(parameter)));
    if (c.IsWindowVisible(window) != 0 and c.IsHungAppWindow(window) != 0) {
        var pid: c.DWORD = 0;
        _ = c.GetWindowThreadProcessId(window, &pid);
        candidates.add(pid);
    }
    return c.TRUE;
}
const critical_names = [_][]const u8{ "csrss", "wininit", "winlogon", "services", "lsass", "smss", "fontdrvhost", "dwm" };
pub fn isCriticalName(file_name: []const u16) bool {
    var ascii: [32]u8 = undefined;
    var stem = file_name;
    if (stem.len > 4) {
        const tail = stem[stem.len - 4 ..];
        if (tail[0] == '.' and lower(tail[1]) == 'e' and lower(tail[2]) == 'x' and lower(tail[3]) == 'e') stem = stem[0 .. stem.len - 4];
    }
    if (stem.len > ascii.len) return false;
    for (stem, 0..) |unit, index| {
        if (unit > 0x7f) return false;
        ascii[index] = lower(unit);
    }
    for (critical_names) |name| if (std.mem.eql(u8, name, ascii[0..stem.len])) return true;
    return false;
}
fn lower(unit: u16) u8 {
    return std.ascii.toLower(@intCast(unit & 0x7f));
}
fn allowedTarget(pid: u32) ?Target {
    if (pid <= 4 or pid == c.GetCurrentProcessId()) return null;
    const process = c.OpenProcess(c.PROCESS_QUERY_LIMITED_INFORMATION, c.FALSE, pid) orelse return null;
    defer _ = c.CloseHandle(process);
    var path: [32768]u16 = undefined;
    var length: c.DWORD = path.len;
    if (c.QueryFullProcessImageNameW(process, 0, &path, &length) == 0) return null;
    const full = path[0..length];
    const slash = std.mem.lastIndexOfScalar(u16, full, '\\');
    if (isCriticalName(if (slash) |index| full[index + 1 ..] else full)) return null;
    var created: c.FILETIME = undefined;
    var exited: c.FILETIME = undefined;
    var kernel: c.FILETIME = undefined;
    var user: c.FILETIME = undefined;
    if (c.GetProcessTimes(process, &created, &exited, &kernel, &user) == 0) return null;
    return .{ .pid = pid, .start_key = (@as(u64, created.dwHighDateTime) << 32) | created.dwLowDateTime };
}

pub fn requestBody(out: []u8, targets: []const Target) ![]const u8 {
    var writer = std.Io.Writer.fixed(out);
    try writer.writeAll("{\"targets\":[");
    for (targets, 0..) |target, index| {
        if (index != 0) try writer.writeByte(',');
        try writer.print("{{\"processId\":{d},\"processStartKey\":\"{d}\"}}", .{ target.pid, target.start_key });
    }
    try writer.writeAll("]}");
    return writer.buffered();
}

pub const Summary = struct { succeeded: usize, total: usize };
/// C# SummarizeTermination: the action must be Terminate and every item's
/// state must agree with its outcome; anything else is an error.
pub fn summarize(allocator: std.mem.Allocator, body: []const u8) !Summary {
    const root = std.json.parseFromSliceLeaky(std.json.Value, allocator, body, .{}) catch return error.InvalidTerminateResult;
    if (root != .object) return error.InvalidTerminateResult;
    const action = root.object.get("action") orelse return error.InvalidTerminateResult;
    if (action != .string or !std.mem.eql(u8, action.string, "Terminate")) return error.InvalidTerminateResult;
    const items = root.object.get("items") orelse return error.InvalidTerminateResult;
    if (items != .array) return error.InvalidTerminateResult;
    var succeeded: usize = 0;
    for (items.array.items) |item| {
        if (item != .object) return error.InvalidTerminateResult;
        const outcome = item.object.get("outcome") orelse return error.InvalidTerminateResult;
        const state = item.object.get("state") orelse return error.InvalidTerminateResult;
        if (outcome != .string or state != .string) return error.InvalidTerminateResult;
        const expected = expectedState(outcome.string) orelse return error.UnknownTerminateOutcome;
        if (!std.mem.eql(u8, state.string, expected)) return error.TerminateStateMismatch;
        if (std.mem.eql(u8, outcome.string, "Terminated")) succeeded += 1;
    }
    return .{ .succeeded = succeeded, .total = items.array.items.len };
}
fn expectedState(outcome: []const u8) ?[]const u8 {
    const table = [_]struct { []const u8, []const u8 }{
        .{ "Terminated", "Succeeded" },
        .{ "BackendProtected", "Skipped" },
        .{ "CriticalProcessProtected", "Skipped" },
        .{ "IdentityChanged", "Skipped" },
        .{ "ProcessExited", "Skipped" },
        .{ "AccessDenied", "Failed" },
        .{ "NativeFailure", "Failed" },
        .{ "FileFailure", "Failed" },
        .{ "RequestRejected", "Failed" },
    };
    for (table) |entry| if (std.mem.eql(u8, entry[0], outcome)) return entry[1];
    return null;
}

test "terminate request and result summary follow the C# contract" {
    var buffer: [256]u8 = undefined;
    try std.testing.expectEqualStrings("{\"targets\":[{\"processId\":12,\"processStartKey\":\"133\"},{\"processId\":13,\"processStartKey\":\"7\"}]}", try requestBody(&buffer, &.{ .{ .pid = 12, .start_key = 133 }, .{ .pid = 13, .start_key = 7 } }));
    var storage: [16 * 1024]u8 = undefined;
    var arena = std.heap.FixedBufferAllocator.init(&storage);
    const summary = try summarize(arena.allocator(), "{\"action\":\"Terminate\",\"items\":[{\"outcome\":\"Terminated\",\"state\":\"Succeeded\"},{\"outcome\":\"ProcessExited\",\"state\":\"Skipped\"}]}");
    try std.testing.expectEqual(@as(usize, 1), summary.succeeded);
    try std.testing.expectEqual(@as(usize, 2), summary.total);
    try std.testing.expectError(error.TerminateStateMismatch, summarize(arena.allocator(), "{\"action\":\"Terminate\",\"items\":[{\"outcome\":\"Terminated\",\"state\":\"Failed\"}]}"));
    try std.testing.expectError(error.UnknownTerminateOutcome, summarize(arena.allocator(), "{\"action\":\"Terminate\",\"items\":[{\"outcome\":\"New\",\"state\":\"Failed\"}]}"));
    try std.testing.expectError(error.InvalidTerminateResult, summarize(arena.allocator(), "{\"action\":\"Suspend\",\"items\":[]}"));
}

test "critical names, mouse buttons and own process are excluded or mapped" {
    try std.testing.expect(isCriticalName(w.L("DWM.EXE")));
    try std.testing.expect(isCriticalName(w.L("csrss")));
    try std.testing.expect(!isCriticalName(w.L("notepad.exe")));
    try std.testing.expectEqual(Button{ .key = 6, .down = true }, mouseButton(0x20b, 2 << 16).?);
    try std.testing.expectEqual(Button{ .key = 2, .down = false }, mouseButton(0x205, 0).?);
    try std.testing.expectEqual(@as(?Button, null), mouseButton(0x200, 0));
    try std.testing.expectEqual(@as(?Target, null), allowedTarget(c.GetCurrentProcessId()));
    try std.testing.expectEqual(@as(?Target, null), allowedTarget(4));
}

test "Ledger force terminate action uses scoped workspace without hooks or real process actions" {
    const Fixture = @import("../memory_action_test_host.zig").Fixture;
    const mock = @import("../backend/mock_backend.zig");
    const server = try mock.Server.start(.{ .script = &.{.{ .status = "200 OK", .body = "{\"action\":\"Terminate\",\"items\":[{\"outcome\":\"Terminated\",\"state\":\"Succeeded\"}]}", .close_after = true, .repeat = true }} });
    defer server.deinit();
    var owner: memory.Owner = .{};
    try owner.init();
    defer if (owner.instance != null) owner.deinit() catch @panic("terminate action retained a lease");
    var fixture: Fixture = .{};
    const Fake = struct {
        fn resolve(_: ?u32, targets: *[max_targets]Target) usize {
            targets[0] = .{ .pid = 4000001, .start_key = 133000000000000001 };
            return 1;
        }
    };
    var block: Block = .{ .config = .{ .ledger = &owner, .host = fixture.host(Host), .opener = server.opener(), .thread_stack_bytes = points.thread_stack_bytes, .resolve = Fake.resolve }, .quit = server.stop };
    block.act(null);
    block.act(null);
    try std.testing.expectEqual(@as(usize, 2), fixture.balloons);
    const expected = lookup.balloon(.info, lookup.select("en-US").force_terminate_title, lookup.select("en-US").terminate_result, &.{ "1", "1" });
    try std.testing.expectEqualSlices(u16, expected.bodySlice(), fixture.last.bodySlice());
    try std.testing.expectEqual(@as(usize, 2), server.countLines("POST /api/system/processes/terminate HTTP/1.1"));
    var body: [256]u8 = undefined;
    try std.testing.expectEqualStrings("{\"targets\":[{\"processId\":4000001,\"processStartKey\":\"133000000000000001\"}]}", server.copyLastBody(&body));
    try owner.deinit();
}
