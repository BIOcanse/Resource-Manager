//! Product tree + shared controller + real anonymous-pipe child. Native shell
//! entries are inert workers; all lifecycle decisions still belong to Engine.
const std = @import("std");
const control = @import("rm_control");
const topology = @import("desktop_tree");
const wp = @import("window_process");
const fixture = @import("fixture_options");
const w = wp.win32;
const B = topology.BlockUids;
const I = topology.ItemUids;
const Run = struct { block: u32, number: u64 };
const Event = union(enum) { start: Run, stop: Run, running: Run, returned: Run, window_ready, close, ending, session };
const Claim = control.state.Claim;
const Target = enum { roots_running, window_running, window_stopped, desktop_running, desktop_stopped, all_stopped, first_session, second_session };

const Host = struct {
    const Slot = struct { host: *Host = undefined, run: Run = undefined, stop: w.HANDLE = undefined, worker: ?std.Thread = null, starts: usize = 0 };
    engine: *topology.Engine = undefined,
    slots: [topology.host_blocks.len]Slot = [_]Slot{.{}} ** topology.host_blocks.len,
    client: wp.Client = .{},
    mutex: w.Mutex = .{},
    wake: w.HANDLE = undefined,
    stop_wait_cancel: w.HANDLE = undefined,
    queue: [128]Event = undefined,
    head: usize = 0,
    count: usize = 0,
    wanted: Claim = undefined,
    ready: Claim = undefined,
    mode: []const u8 = "hold",
    pid: u32 = 0,
    window_ready: bool = false,
    ending: bool = false,
    sessions: usize = 0,
    failures: std.atomic.Value(usize) = .init(0),
    crash_retracted_before_return: bool = false,

    fn why() control.Reason {
        return .{ .kind = 1, .owner = "tree-scenario", .cause = "test input", .plan_version = 0 };
    }
    fn from(context: ?*anyopaque) *Host {
        return @ptrCast(@alignCast(context.?));
    }
    fn slot(self: *Host, block: u32) *Slot {
        for (topology.host_blocks, 0..) |uid, index| if (uid == block) return &self.slots[index];
        unreachable;
    }
    fn push(self: *Host, event: Event) void {
        self.mutex.lock();
        defer self.mutex.unlock();
        std.debug.assert(self.count < self.queue.len);
        self.queue[(self.head + self.count) % self.queue.len] = event;
        self.count += 1;
        _ = w.SetEvent(self.wake);
    }
    fn pop(self: *Host) ?Event {
        self.mutex.lock();
        defer self.mutex.unlock();
        if (self.count == 0) {
            _ = w.ResetEvent(self.wake);
            return null;
        }
        const event = self.queue[self.head];
        self.head = (self.head + 1) % self.queue.len;
        self.count -= 1;
        return event;
    }
    fn startCallback(context: ?*anyopaque, block: u32, run: u64) callconv(.c) void {
        from(context).push(.{ .start = .{ .block = block, .number = run } });
    }
    fn stopCallback(context: ?*anyopaque, block: u32, run: u64) callconv(.c) void {
        from(context).push(.{ .stop = .{ .block = block, .number = run } });
    }
    fn complete(_: ?*anyopaque, _: u64) callconv(.c) void {}
    fn log(_: ?*anyopaque, _: u32, _: [*]const u8, _: usize) callconv(.c) void {}
    fn changed(_: ?*anyopaque, _: u64, _: control.types.ItemKeyView, _: *const control.types.ReasonView) callconv(.c) void {}
    fn init(self: *Host, wanted: bool, mode: []const u8) !void {
        self.mode = mode;
        self.wake = w.CreateEventW(null, 1, 0, null) orelse return error.Event;
        errdefer w.close(self.wake);
        self.stop_wait_cancel = w.CreateEventW(null, 1, 0, null) orelse return error.Event;
        errdefer w.close(self.stop_wait_cancel);
        self.engine = try topology.Engine.create(std.testing.allocator, .{
            .worker_count = 2,
            .history_capacity = 128,
            .demand_capacity = 8,
            .demand_bytes = 1024,
            .completion_capacity = 8,
            .reader_capacity = 8,
            .reader_bytes = 1024,
            .worker_arena_bytes = 128 * 1024,
            .demand_item = null,
            .shutdown_item = I.ProgramShuttingDown,
            .host_blocks = &topology.host_blocks,
        }, .{ .context = self, .start = startCallback, .stop = stopCallback, .complete = complete, .log = log, .item_changed = changed });
        self.wanted = try self.engine.claimWrite(.{ .item = I.WindowWanted });
        self.ready = try self.engine.claimCell(.{ .item = I.BackendSessionReady });
        const started = try self.engine.claimWrite(.{ .item = I.ProgramStarted });
        try self.engine.publish(self.wanted, .{ .value = wanted }, why(), null);
        try self.engine.publish(started, .{ .value = true }, why(), null);
        self.engine.activate();
    }
    fn end(self: *Host) !void {
        self.ending = true;
        _ = w.SetEvent(self.stop_wait_cancel);
        try self.engine.shutdown(why(), null);
    }
    fn deinit(self: *Host) void {
        self.end() catch @panic("scenario shutdown");
        self.wait(.all_stopped) catch @panic("scenario cleanup did not stop");
        self.engine.destroy() catch @panic("scenario destroy");
        w.close(self.wake);
        w.close(self.stop_wait_cancel);
    }
    fn entry(item: *Slot) void {
        const self = item.host;
        if (item.run.block == B.WindowProcess) {
            var args: [80]u8 = undefined;
            const arguments = std.fmt.bufPrint(&args, "--mode {s}", .{self.mode}) catch unreachable;
            self.client.run(item.stop, item, windowEvent, .{ .executable = fixture.path, .cancel_poll_ms = 10, .stop_deadline_ms = 1000, .timeout_policy = .terminate, .stop_wait_cancel = self.stop_wait_cancel, .arguments = arguments, .session = .{ .ready = true, .generation = 21 } }) catch {
                _ = self.failures.fetchAdd(1, .monotonic);
            };
        } else {
            self.push(.{ .running = item.run });
            _ = w.WaitForSingleObject(item.stop, w.infinite);
        }
        self.push(.{ .returned = item.run });
    }
    fn windowEvent(context: ?*anyopaque, event: wp.Event) void {
        const item: *Slot = @ptrCast(@alignCast(context.?));
        const self = item.host;
        switch (event) {
            .running => self.push(.{ .running = item.run }),
            .window_handle => self.push(.window_ready),
            .user_close => self.push(.close),
            .apply_exit => self.push(.ending),
            .preferences_changed => self.push(.session),
            .crashed => self.engine.publish(self.wanted, .{ .value = false }, why(), null) catch @panic("crash fact"),
            else => {},
        }
    }
    fn dispatch(self: *Host, event: Event) !void {
        switch (event) {
            .start => |run| {
                const item = self.slot(run.block);
                try std.testing.expect(item.worker == null);
                item.host = self;
                item.run = run;
                item.starts += 1;
                if (run.block == B.BackendConnection) try self.engine.bind(self.ready, why(), null);
                item.stop = w.CreateEventW(null, 1, 0, null) orelse return error.Event;
                item.worker = try std.Thread.spawn(.{}, entry, .{item});
            },
            .stop => |run| {
                const item = self.slot(run.block);
                if (item.worker != null and item.run.number == run.number) _ = w.SetEvent(item.stop);
            },
            .running => |run| try self.engine.reportRunning(run.block, run.number),
            .returned => |run| {
                const item = self.slot(run.block);
                item.worker.?.join();
                item.worker = null;
                w.close(item.stop);
                if (run.block == B.WindowProcess) {
                    self.window_ready = false;
                    if (self.failures.load(.monotonic) != 0)
                        self.crash_retracted_before_return = !(try self.engine.read(.{ .item = I.WindowWanted })).valueOr(true);
                }
                if (run.block == B.BackendConnection) try self.engine.unbind(self.ready, why(), null);
                try self.engine.reportReturned(run.block, run.number);
            },
            .window_ready => {
                self.window_ready = true;
                self.pid += 1;
            },
            .close => try self.engine.publish(self.wanted, .{ .value = false }, why(), null),
            .ending => try self.end(),
            .session => self.sessions += 1,
        }
    }
    fn state(self: *Host, block: u32) !control.types.BlockState {
        return self.engine.readState(block);
    }
    fn allStopped(self: *Host) !bool {
        for (topology.host_blocks) |block| if (try self.state(block) != .stopped) return false;
        return true;
    }
    fn reached(self: *Host, target: Target) !bool {
        return switch (target) {
            .roots_running => try self.state(B.MainRoot) == .running and try self.state(B.TrayRoot) == .running and try self.state(B.BackendConnection) == .running and try self.state(B.SingleInstance) == .running,
            .window_running => self.window_ready and try self.state(B.WindowProcess) == .running,
            .window_stopped => self.slot(B.WindowProcess).starts != 0 and self.slot(B.WindowProcess).worker == null and try self.state(B.WindowProcess) == .stopped,
            .desktop_running => try self.state(B.DesktopObservation) == .running,
            .desktop_stopped => try self.state(B.DesktopObservation) == .stopped and self.slot(B.DesktopObservation).worker == null,
            .all_stopped => try self.allStopped(),
            .first_session => self.sessions >= 1,
            .second_session => self.sessions >= 2,
        };
    }
    fn wait(self: *Host, target: Target) !void {
        // Bounded test harness only; this is not a product stop deadline.
        for (0..2000) |_| {
            self.engine.waitIdle();
            while (self.pop()) |event| try self.dispatch(event);
            self.engine.waitIdle();
            if (try self.reached(target)) return;
            _ = w.WaitForSingleObject(self.wake, 5);
        }
        return error.ScenarioTimedOut;
    }
    fn rootsSurvive(self: *Host) !void {
        for ([_]u32{ B.MainRoot, B.TrayRoot, B.BackendConnection, B.SingleInstance }) |uid| {
            try std.testing.expectEqual(control.types.BlockState.running, try self.state(uid));
            try std.testing.expectEqual(@as(usize, 1), self.slot(uid).starts);
        }
    }
};

test "tree scenario 6: background WindowWanted false never requests a child" {
    var host: Host = .{};
    try host.init(false, "hold");
    defer host.deinit();
    try host.wait(.roots_running);
    try std.testing.expectEqual(@as(usize, 0), host.slot(B.WindowProcess).starts);
    try std.testing.expectEqual(control.types.BlockState.stopped, try host.state(B.WindowProcess));
}
test "tree scenario 1: user close stops only the window branch" {
    var host: Host = .{};
    try host.init(true, "close");
    defer host.deinit();
    try host.wait(.window_stopped);
    try host.wait(.roots_running);
    try host.rootsSurvive();
    try std.testing.expectEqual(@as(usize, 0), host.failures.load(.monotonic));
}
test "tree scenario 2: explicit reopen starts after the previous real child joined" {
    var host: Host = .{};
    try host.init(true, "close");
    defer host.deinit();
    try host.wait(.window_stopped);
    host.mode = "hold";
    try host.engine.publish(host.wanted, .{ .value = true }, Host.why(), null);
    try host.wait(.window_running);
    try std.testing.expectEqual(@as(usize, 2), host.slot(B.WindowProcess).starts);
    try host.wait(.roots_running);
    try host.rootsSurvive();
}
test "tree scenario 4: session generations cross pipes while the window branch remains running" {
    var host: Host = .{};
    try host.init(true, "session");
    defer host.deinit();
    try host.wait(.window_running);
    try host.engine.publish(host.ready, .{ .value = true }, Host.why(), null);
    try host.wait(.desktop_running);
    try host.engine.publish(host.ready, .{ .value = false }, Host.why(), null);
    try host.wait(.desktop_stopped);
    try std.testing.expectEqual(control.types.BlockState.running, try host.state(B.WindowProcess));
    try host.client.sendSession(.{ .ready = false, .generation = 22, .reason = "disconnect" });
    try host.wait(.first_session);
    try host.engine.publish(host.ready, .{ .value = true }, Host.why(), null);
    try host.wait(.desktop_running);
    try host.client.sendSession(.{ .ready = true, .generation = 23 });
    try host.wait(.second_session);
    try std.testing.expectEqual(control.types.BlockState.running, try host.state(B.WindowProcess));
    try std.testing.expectEqual(@as(usize, 1), host.slot(B.WindowProcess).starts);
    try std.testing.expectEqual(@as(usize, 2), host.slot(B.DesktopObservation).starts);
    try host.wait(.roots_running);
    try host.rootsSurvive();
}
test "tree scenario 5: program ending joins every branch before completion" {
    var host: Host = .{};
    try host.init(true, "hold");
    defer host.deinit();
    try host.wait(.window_running);
    try host.end();
    try host.wait(.all_stopped);
    for (host.slots) |item| try std.testing.expect(item.worker == null);
}
test "tree scenario 10: crash clears wanted before reportReturned and waits for explicit reopen" {
    var host: Host = .{};
    try host.init(true, "crash");
    defer host.deinit();
    try host.wait(.window_stopped);
    try std.testing.expect(host.crash_retracted_before_return);
    try std.testing.expectEqual(@as(usize, 1), host.failures.load(.monotonic));
    try host.wait(.roots_running);
    try host.rootsSurvive();
    // The controller has finished the returned transition; no true demand exists.
    host.engine.waitIdle();
    try std.testing.expectEqual(@as(usize, 1), host.slot(B.WindowProcess).starts);
    host.mode = "hold";
    try host.engine.publish(host.wanted, .{ .value = true }, Host.why(), null);
    try host.wait(.window_running);
    try std.testing.expectEqual(@as(usize, 2), host.slot(B.WindowProcess).starts);
}
test "tree scenario 11: update intent ends the complete resident tree" {
    var host: Host = .{};
    try host.init(true, "update");
    defer host.deinit();
    // Avoid accepting the initial all-stopped state before any starts dispatch.
    for (0..2000) |_| {
        if (host.ending) break;
        host.engine.waitIdle();
        while (host.pop()) |event| try host.dispatch(event);
        _ = w.WaitForSingleObject(host.wake, 5);
    }
    try std.testing.expect(host.ending);
    try host.wait(.all_stopped);
    try std.testing.expectEqual(@as(usize, 1), host.slot(B.WindowProcess).starts);
}
