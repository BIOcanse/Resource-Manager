//! ForceTerminateHotkey scenario on a thread created by this test. The test-only
//! message input uses the production matcher/action path without global hooks,
//! SendInput, foreground-window actions or messages to another process.
const std = @import("std");
const memory = @import("../memory.zig");
const w = @import("../win32.zig");
const c = w.c;
const block = @import("force_terminate.zig");
const definition = @import("definition.zig");
const lookup = @import("../text/lookup.zig");
const mock = @import("../backend/mock_backend.zig");

const Key = struct { vk: u16, down: bool };
fn inject(thread: c.HANDLE, done: c.HANDLE, keys: []const Key) !void {
    var index: usize = 0;
    while (index < keys.len) : (index += 1) {
        try block.TestInput.post(thread, keys[index].down, keys[index].vk);
    }
    try block.TestInput.barrier(thread, done);
    // Queue order also acknowledges a preceding rebind before the next chord.
    try std.testing.expectEqual(c.WAIT_OBJECT_0, c.WaitForSingleObject(done, 5000));
}
fn chord(trigger: u16) [4]Key {
    return .{ .{ .vk = 0x11, .down = true }, .{ .vk = trigger, .down = true }, .{ .vk = trigger, .down = false }, .{ .vk = 0x11, .down = false } };
}

const Scenario = struct {
    running: c.HANDLE,
    shown: c.HANDLE,
    dispatched: c.HANDLE,
    binding_value: definition.Definition,
    last: lookup.Balloon = .{},
    balloons: std.atomic.Value(u32) = .init(0),
    fn onRunning(raw: *anyopaque) void {
        const self: *Scenario = @ptrCast(@alignCast(raw));
        _ = c.SetEvent(self.running);
    }
    fn onBinding(raw: *anyopaque) definition.Definition {
        const self: *Scenario = @ptrCast(@alignCast(raw));
        return self.binding_value;
    }
    fn onBalloon(raw: *anyopaque, value: *const lookup.Balloon) void {
        const self: *Scenario = @ptrCast(@alignCast(raw));
        self.last = value.*;
        _ = self.balloons.fetchAdd(1, .acq_rel);
        _ = c.SetEvent(self.shown);
    }
    fn onText(_: *anyopaque) *const lookup.Text {
        return lookup.select("en-US");
    }
    fn onReady(_: *anyopaque) bool {
        return true;
    }
    /// Never names a real process to the mock.
    fn fake(_: ?u32, out: *[block.max_targets]block.Target) usize {
        out[0] = .{ .pid = 4000001, .start_key = 133000000000000001 };
        return 1;
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

test "chord press terminates through the backend, rebind takes the new chord, WM_QUIT stops" {
    var owner: memory.Owner = .{};
    try owner.init();
    defer owner.deinit() catch @panic("hotkey scenario left an active workspace");
    var server = try mock.Server.start(.{ .script = &.{
        .{ .status = "200 OK", .body = "{\"action\":\"Terminate\",\"items\":[{\"outcome\":\"Terminated\",\"state\":\"Succeeded\"}]}", .close_after = true },
        .{ .status = "200 OK", .body = "{\"action\":\"Terminate\",\"items\":[{\"outcome\":\"AccessDenied\",\"state\":\"Failed\"}]}", .close_after = true },
    } });
    defer server.deinit();
    var scenario: Scenario = .{
        .running = c.CreateEventW(null, c.TRUE, c.FALSE, null).?,
        .shown = c.CreateEventW(null, c.FALSE, c.FALSE, null).?,
        .dispatched = c.CreateEventW(null, c.FALSE, c.FALSE, null).?,
        .binding_value = definition.Definition.create(&.{ 17, 0, 0x4b }),
    };
    defer _ = c.CloseHandle(scenario.running);
    defer _ = c.CloseHandle(scenario.shown);
    defer _ = c.CloseHandle(scenario.dispatched);
    const stop = c.CreateEventW(null, c.TRUE, c.FALSE, null).?;
    defer _ = c.CloseHandle(stop);
    var context: Run = .{ .stop = stop, .config = .{
        .host = .{ .context = &scenario, .running = Scenario.onRunning, .binding = Scenario.onBinding, .balloon = Scenario.onBalloon, .text = Scenario.onText, .backend_ready = Scenario.onReady },
        .opener = server.opener(),
        .ledger = &owner,
        .thread_stack_bytes = 256 * 1024,
        .resolve = Scenario.fake,
        .test_input = true,
    } };
    const thread = c.CreateThread(null, 256 * 1024, Run.entry, &context, c.STACK_SIZE_PARAM_IS_A_RESERVATION, null).?;
    defer {
        _ = c.SetEvent(stop);
        _ = c.PostThreadMessageW(c.GetThreadId(thread), c.WM_QUIT, 0, 0);
        _ = c.WaitForSingleObject(thread, c.INFINITE);
        _ = c.CloseHandle(thread);
    }
    try std.testing.expectEqual(c.WAIT_OBJECT_0, c.WaitForSingleObject(scenario.running, 5000));

    // Ctrl+K: the production input handler fires once and reaches the mock backend.
    try inject(thread, scenario.dispatched, &chord(0x4b));
    try std.testing.expectEqual(c.WAIT_OBJECT_0, c.WaitForSingleObject(scenario.shown, 5000));
    try std.testing.expect(server.logged("POST /api/system/processes/terminate HTTP/1.1"));
    var body: [512]u8 = undefined;
    try std.testing.expectEqualStrings("{\"targets\":[{\"processId\":4000001,\"processStartKey\":\"133000000000000001\"}]}", server.copyLastBody(&body));
    const text = lookup.select("en-US");
    const expected = lookup.balloon(.info, text.force_terminate_title, text.terminate_result, &.{ "1", "1" });
    try std.testing.expectEqual(lookup.Icon.info, scenario.last.icon);
    try std.testing.expectEqualSlices(u16, expected.bodySlice(), scenario.last.bodySlice());
    try std.testing.expectEqual(@as(u32, 1), scenario.balloons.load(.acquire));

    // Rebind to Ctrl+J: the old chord no longer fires, the new one does.
    scenario.binding_value = definition.Definition.create(&.{ 17, 0, 0x4a });
    _ = c.PostThreadMessageW(c.GetThreadId(thread), block.rebind_message, 0, 0);
    try inject(thread, scenario.dispatched, &chord(0x4b));
    try std.testing.expectEqual(@as(c.DWORD, c.WAIT_TIMEOUT), c.WaitForSingleObject(scenario.shown, 300));
    try inject(thread, scenario.dispatched, &chord(0x4a));
    try std.testing.expectEqual(c.WAIT_OBJECT_0, c.WaitForSingleObject(scenario.shown, 5000));
    const second = lookup.balloon(.info, text.force_terminate_title, text.terminate_result, &.{ "0", "1" });
    try std.testing.expectEqualSlices(u16, second.bodySlice(), scenario.last.bodySlice());
    try std.testing.expectEqual(@as(u32, 2), scenario.balloons.load(.acquire));

    _ = c.SetEvent(stop);
    _ = c.PostThreadMessageW(c.GetThreadId(thread), c.WM_QUIT, 0, 0);
    try std.testing.expectEqual(c.WAIT_OBJECT_0, c.WaitForSingleObject(thread, 5000));
    var exit_code: c.DWORD = 99;
    _ = c.GetExitCodeThread(thread, &exit_code);
    try std.testing.expectEqual(@as(c.DWORD, 0), exit_code);
    server.finish();
}
