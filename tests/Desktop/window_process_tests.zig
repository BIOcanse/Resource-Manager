const std = @import("std");
const wp = @import("window_process");
const w = wp.win32;
const fixture = @import("fixture_options");

const Harness = struct {
    client: wp.Client = .{},
    stop: wp.HANDLE,
    notice: wp.HANDLE,
    done: wp.HANDLE,
    worker: ?std.Thread = null,
    mode: []const u8,
    mutex: w.Mutex = .{},
    pid: u32 = 0,
    handle: usize = 0,
    user_close: bool = false,
    apply_exit: bool = false,
    crashed: bool = false,
    window_wanted: bool = true,
    sessions: u32 = 0,
    failed: bool = false,
    forbidden: ?wp.HANDLE = null,
    executable: []const u8 = fixture.path,
    language: []const u8 = "zh-CN",

    fn init(mode: []const u8) !Harness {
        const stop = w.CreateEventW(null, 1, 0, null) orelse return error.Event;
        errdefer w.close(stop);
        const notice = w.CreateEventW(null, 1, 0, null) orelse return error.Event;
        errdefer w.close(notice);
        const done = w.CreateEventW(null, 1, 0, null) orelse return error.Event;
        return .{ .stop = stop, .notice = notice, .done = done, .mode = mode };
    }
    fn start(self: *Harness) !void {
        self.worker = try std.Thread.spawn(.{}, run, .{self});
    }
    fn deinit(self: *Harness) void {
        _ = w.SetEvent(self.stop);
        if (self.worker) |thread| thread.join();
        w.close(self.stop);
        w.close(self.notice);
        w.close(self.done);
    }
    fn run(self: *Harness) void {
        var args: [128]u8 = undefined;
        const arguments = if (self.forbidden) |handle|
            std.fmt.bufPrint(&args, "--mode {s} --forbidden-handle {d}", .{ self.mode, @intFromPtr(handle) }) catch unreachable
        else
            std.fmt.bufPrint(&args, "--mode {s}", .{self.mode}) catch unreachable;
        self.client.run(self.stop, self, onEvent, .{ .executable = self.executable, .cancel_poll_ms = 10, .arguments = arguments, .session = .{ .ready = true, .generation = 21 }, .language = self.language }) catch {
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
                if (std.mem.eql(u8, self.mode, "stop-on-running")) _ = w.SetEvent(self.stop);
            },
            .window_handle => |handle| {
                self.handle = handle;
                if (std.mem.eql(u8, self.mode, "hold") or std.mem.eql(u8, self.mode, "session")) _ = w.SetEvent(self.notice);
            },
            .user_close => {
                self.user_close = true;
                _ = w.SetEvent(self.notice);
            },
            .apply_exit => {
                self.apply_exit = true;
                _ = w.SetEvent(self.notice);
            },
            .preferences_changed => {
                self.sessions += 1;
                _ = w.SetEvent(self.notice);
            },
            .crashed => {
                self.window_wanted = false;
                self.crashed = true;
                _ = w.SetEvent(self.notice);
            },
            else => {},
        }
    }
    fn waitNotice(self: *Harness) !void {
        try std.testing.expectEqual(@as(u32, 0), w.WaitForSingleObject(self.notice, 10_000));
    }
    fn finish(self: *Harness) !void {
        _ = w.SetEvent(self.stop);
        try std.testing.expectEqual(@as(u32, 0), w.WaitForSingleObject(self.done, 10_000));
        self.worker.?.join();
        self.worker = null;
    }
};

test "scenario 1: user close publishes a fact and waits for its parent's stop" {
    var host = try Harness.init("close");
    defer host.deinit();
    try host.start();
    try host.waitNotice();
    try std.testing.expectEqual(@as(u32, 258), w.WaitForSingleObject(host.done, 30));
    try host.finish();
    try std.testing.expect(host.user_close and !host.failed and !host.crashed);
}
test "scenario 2: reopen creates a new process only after the old run joined" {
    var host = try Harness.init("close");
    defer host.deinit();
    try host.start();
    try host.waitNotice();
    try host.finish();
    const previous = host.pid;
    _ = w.ResetEvent(host.stop);
    _ = w.ResetEvent(host.done);
    _ = w.ResetEvent(host.notice);
    host.user_close = false;
    host.handle = 0;
    try host.start();
    try host.waitNotice();
    try host.finish();
    try std.testing.expect(host.pid != previous and host.pid != 0 and !host.failed);
}
test "scenario 4: backend disconnect and next generation cross the real pipes without stopping the child" {
    var host = try Harness.init("session");
    defer host.deinit();
    try host.start();
    try host.waitNotice();
    _ = w.ResetEvent(host.notice);
    try host.client.sendSession(.{ .ready = false, .generation = 22, .reason = "disconnect\n中文" });
    try host.waitNotice();
    _ = w.ResetEvent(host.notice);
    try host.client.sendSession(.{ .ready = true, .generation = 23 });
    try host.waitNotice();
    try std.testing.expectEqual(@as(u32, 258), w.WaitForSingleObject(host.done, 0));
    try host.finish();
    try std.testing.expectEqual(@as(u32, 2), host.sessions);
    try std.testing.expect(!host.failed);
}
test "scenario 5: program end sends stop and joins the real child before returning" {
    var host = try Harness.init("hold");
    defer host.deinit();
    try host.start();
    try host.waitNotice();
    try host.finish();
    try std.testing.expect(!host.failed and !host.crashed);
    try std.testing.expectError(error.NotRunning, host.client.showWindow(.show));
}
test "scenario 6: a background run with a stopped token never launches a window process" {
    var host = try Harness.init("hold");
    defer host.deinit();
    _ = w.SetEvent(host.stop);
    try host.start();
    try host.finish();
    try std.testing.expectEqual(@as(u32, 0), host.pid);
}
test "stop while startup facts are being delivered still joins a cooperative child" {
    var host = try Harness.init("stop-on-running");
    defer host.deinit();
    try host.start();
    try std.testing.expectEqual(@as(u32, 0), w.WaitForSingleObject(host.done, 10_000));
    host.worker.?.join();
    host.worker = null;
    try std.testing.expect(host.pid != 0 and !host.failed and !host.crashed);
}
test "launch and early setup failures retract WindowWanted before returning" {
    for ([_]bool{ false, true }) |launched| {
        var host = try Harness.init("hold");
        defer host.deinit();
        if (launched) host.language = "\xff" else host.executable = "\"invalid";
        try host.start();
        try std.testing.expectEqual(@as(u32, 0), w.WaitForSingleObject(host.done, 10_000));
        host.worker.?.join();
        host.worker = null;
        try std.testing.expect(host.failed and host.crashed and !host.window_wanted);
        try std.testing.expectError(error.NotRunning, host.client.showWindow(.show));
    }
}
test "scenario 10: crash retracts WindowWanted before failed run returns and explicit reopen works" {
    var host = try Harness.init("crash");
    defer host.deinit();
    try host.start();
    try std.testing.expectEqual(@as(u32, 0), w.WaitForSingleObject(host.done, 10_000));
    host.worker.?.join();
    host.worker = null;
    try std.testing.expect(host.failed and host.crashed and !host.window_wanted);
    _ = w.ResetEvent(host.stop);
    _ = w.ResetEvent(host.done);
    _ = w.ResetEvent(host.notice);
    host.mode = "close";
    host.failed = false;
    host.crashed = false;
    host.window_wanted = true;
    host.handle = 0;
    try host.start();
    try host.waitNotice();
    try host.finish();
    try std.testing.expect(!host.failed and !host.crashed);
}
test "scenario 11: applyExit only publishes program-end intent until its parent sends stop" {
    var host = try Harness.init("update");
    defer host.deinit();
    try host.start();
    try host.waitNotice();
    try std.testing.expectEqual(@as(u32, 258), w.WaitForSingleObject(host.done, 30));
    try host.finish();
    try std.testing.expect(host.apply_exit and !host.failed);
}
test "invalid decimal handle and oversized input fail the run and join the child" {
    for ([_][]const u8{ "malformed", "oversize" }) |mode| {
        var host = try Harness.init(mode);
        defer host.deinit();
        try host.start();
        try std.testing.expectEqual(@as(u32, 0), w.WaitForSingleObject(host.done, 10_000));
        host.worker.?.join();
        host.worker = null;
        try std.testing.expect(host.failed and host.crashed and !host.window_wanted);
    }
}
test "wire encoder escapes UTF8 JSON text and enforces byte limits" {
    var storage: [64]u8 = undefined;
    try std.testing.expectEqualStrings("\"中文\\n\\\"\\\\\"", try wp.protocol.quote(&storage, "中文\n\"\\"));
    try std.testing.expectError(error.InvalidUtf8, wp.protocol.quote(&storage, "\xff"));
    var arena: [96 * 1024]u8 = undefined;
    var fixed = std.heap.FixedBufferAllocator.init(&arena);
    try std.testing.expectError(error.InvalidMessage, wp.protocol.parse("{\"type\":\"window-handle\",\"handle\":12}", fixed.allocator()));
}
test "CreateProcess inherits only the explicitly designated two pipe handles" {
    var security: w.Security = .{};
    const unrelated = w.CreateEventW(&security, 1, 0, null) orelse return error.Event;
    defer w.close(unrelated);
    var host = try Harness.init("hold");
    defer host.deinit();
    host.forbidden = unrelated;
    try host.start();
    try host.waitNotice();
    try host.finish();
    try std.testing.expect(!host.failed and !host.crashed);
}
