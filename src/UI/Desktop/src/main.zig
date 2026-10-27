//! The controller owns states and decisions. Its callbacks only enqueue;
//! native host threads own run entries, stop events and diagnostics.
const std = @import("std");
const control = @import("rm_control");
const topology = @import("tree.zig");
const B = topology.BlockUids;
const I = topology.ItemUids;
const w = @import("win32.zig");
const c = w.c;
const shell = @import("shell/instance.zig");
const tray_module = @import("shell/tray.zig");
const window = @import("window_process/root.zig");
const backend = @import("backend/root.zig");
const Options = @import("options.zig").Options;
const points = @import("desktop_points_values").Bound;
const preferences = @import("preferences.zig");
const Claim = control.state.Claim;
const Log = struct { bytes: [1024:0]u8 = undefined, length: usize = 0 };
/// FILE_RENAME_INFO in its FileRenameInfoEx form (flags instead of ReplaceIfExists), declared here because the MinGW headers lack the Ex class and flags.
const RenameInformation = extern struct {
    flags: c.DWORD,
    root_directory: c.HANDLE,
    file_name_length: c.DWORD,
    file_name: [32769]u16,
};
const file_rename_info_ex = 22;
const file_rename_flag_replace_if_exists = 0x1;
const file_rename_flag_posix_semantics = 0x2;

const Event = union(enum) {
    start: Run,
    stop: Run,
    returned: Run,
    command: shell.Command,
    tray_running: c.HWND,
    backend: Session,
    language: Language,
    preferences_changed,
    window_handle: usize,
    window_pid: u32,
    ending,
    user_close,
    log: Log,
    backend_item_changed: u64,
};
const Run = struct { block: u32, number: u64 };
const Language = struct { bytes: [32]u8 = .{0} ** 32, len: usize = 0 };
const Session = struct {
    ready: bool = false,
    generation: u64 = 0,
    bytes: [256]u8 = .{0} ** 256,
    len: usize = 0,
    fn reason(self: *const Session) []const u8 {
        return self.bytes[0..self.len];
    }
};
const Slot = struct {
    host: *Host = undefined,
    run: Run = undefined,
    stop: c.HANDLE = null,
    thread: c.HANDLE = null,
};
const Host = struct {
    engine: *topology.Engine = undefined,
    options: Options = .{},
    instance: shell.Instance = undefined,
    tray: tray_module.Tray = .{},
    client: window.Client = .{},
    wake: c.HANDLE = null,
    // Program stop cancels a window branch's cooperative shutdown wait.
    // This token outlives all runs; per-run stop tokens still own normal close.
    ending_stop: c.HANDLE = null,
    log_file: c.HANDLE = null,
    lock: usize = 0,
    // One queued notice per transition plus bounded shell/runtime events.
    queue: [512]Event = undefined,
    head: usize = 0,
    count: usize = 0,
    slots: [topology.host_blocks.len]Slot = [_]Slot{.{}} ** topology.host_blocks.len,
    started: Claim = undefined,
    wanted: Claim = undefined,
    ready: Claim = undefined,
    backend_session: Claim = undefined,
    overlay_enabled: Claim = undefined,
    notifications_enabled: Claim = undefined,
    backend_reader: ?u64 = null,
    session: Session = .{},
    language: Language = .{},
    ending: bool = false,
    // Kept as the handle itself: window handles are not 4-byte aligned, so turning an integer
    // back into the translated HWND pointer type panics in safe builds.
    tray_window: c.HWND = null,
    window_handle: usize = 0,
    window_pid: u32 = 0,
    show_command: window.ShowCommand = .show,
    package_wide: [32768:0]u16 = undefined,
    package_len: usize = 0,
    backend_wide: [32768:0]u16 = undefined,
    backend_len: usize = 0,
    service_wide: [256:0]u16 = undefined,
    service_len: usize = 0,
    arena_buffer: [4 * 1024 * 1024]u8 align(16) = undefined,
    diagnostics_buffer: [1024 * 1024]u8 = undefined,

    fn reason(cause: []const u8) control.Reason {
        return .{ .kind = 1, .owner = "desktop", .cause = cause, .plan_version = 0 };
    }
    fn from(context: ?*anyopaque) *Host {
        return @ptrCast(@alignCast(context.?));
    }
    fn push(self: *Host, event: Event) void {
        c.AcquireSRWLockExclusive(@ptrCast(&self.lock));
        if (self.count == self.queue.len) {
            c.ReleaseSRWLockExclusive(@ptrCast(&self.lock));
            // Exhaustion is explicit; silently losing a stop would violate the contract.
            c.ExitProcess(70);
        }
        self.queue[(self.head + self.count) % self.queue.len] = event;
        self.count += 1;
        _ = c.SetEvent(self.wake);
        c.ReleaseSRWLockExclusive(@ptrCast(&self.lock));
    }
    fn pop(self: *Host) ?Event {
        c.AcquireSRWLockExclusive(@ptrCast(&self.lock));
        defer c.ReleaseSRWLockExclusive(@ptrCast(&self.lock));
        if (self.count == 0) {
            _ = c.ResetEvent(self.wake);
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
    fn completeCallback(_: ?*anyopaque, _: u64) callconv(.c) void {}
    fn logCallback(context: ?*anyopaque, _: u32, bytes: [*]const u8, length: usize) callconv(.c) void {
        var value: Log = .{};
        value.length = @min(length, value.bytes.len - 1);
        @memcpy(value.bytes[0..value.length], bytes[0..value.length]);
        value.bytes[value.length] = 0;
        from(context).push(.{ .log = value });
    }
    fn itemCallback(context: ?*anyopaque, registration: u64, _: control.types.ItemKeyView, _: *const control.types.ReasonView) callconv(.c) void {
        from(context).push(.{ .backend_item_changed = registration });
    }
    fn find(self: *Host, block: u32) *Slot {
        for (topology.host_blocks, 0..) |uid, index| if (uid == block) return &self.slots[index];
        unreachable;
    }
    fn running(context: ?*anyopaque) void {
        const slot: *Slot = @ptrCast(@alignCast(context.?));
        slot.host.engine.reportRunning(slot.run.block, slot.run.number) catch @panic("reportRunning");
    }
    fn instanceCommand(context: ?*anyopaque, command: shell.Command) void {
        from(context).push(.{ .command = command });
    }
    fn instanceRunning(context: ?*anyopaque) void {
        const self = from(context);
        running(self.find(B.SingleInstance));
    }
    fn trayEvent(context: ?*anyopaque, event: tray_module.Event) void {
        const self = from(context);
        self.push(switch (event) {
            .open => .{ .command = .show },
            .exit, .session_ending => .ending,
            .running => |handle| .{ .tray_running = handle },
        });
    }
    fn backendEvent(context: *anyopaque, ready: bool, generation: u64, cause: []const u8) void {
        var session: Session = .{ .ready = ready, .generation = generation };
        session.len = @min(cause.len, session.bytes.len);
        @memcpy(session.bytes[0..session.len], cause[0..session.len]);
        from(context).push(.{ .backend = session });
    }
    fn windowEvent(context: ?*anyopaque, event: window.Event) void {
        const self = from(context);
        switch (event) {
            .running => |pid| {
                const slot = self.find(B.WindowProcess);
                running(slot);
                self.push(.{ .window_pid = pid });
            },
            .window_handle => |handle| self.push(.{ .window_handle = handle }),
            .user_close => self.push(.user_close),
            .apply_exit => self.push(.ending),
            .preferences_changed => self.push(.preferences_changed),
            .language => |text| {
                var value: Language = .{};
                value.len = @min(value.bytes.len, text.len);
                @memcpy(value.bytes[0..value.len], text[0..value.len]);
                self.push(.{ .language = value });
            },
            .crashed => {
                // This write is synchronous BEFORE the run reports failure/returns;
                // there is no controller callback on this path, and no cached state.
                self.engine.publish(self.wanted, .{ .value = false }, reason("window-crashed"), null) catch @panic("window-crashed publication");
            },
            .stopped => |result| {
                var message: Log = .{};
                const text = std.fmt.bufPrint(&message.bytes, "window stopped: {s}; elapsed_ms={d}; exit_code={d}", .{ @tagName(result.outcome), result.elapsed_ms, result.exit_code }) catch unreachable;
                message.length = text.len;
                message.bytes[text.len] = 0;
                self.push(.{ .log = message });
            },
        }
    }
    fn entry(context: ?*anyopaque) callconv(.winapi) c.DWORD {
        const slot: *Slot = @ptrCast(@alignCast(context.?));
        const self = slot.host;
        self.runEntry(slot) catch |failure| {
            if (slot.run.block == B.WindowProcess) self.engine.publish(self.wanted, .{ .value = false }, reason("window-start-or-run-failed"), null) catch @panic("failure publication");
            var buffer: [256:0]u8 = undefined;
            const text = std.fmt.bufPrintZ(&buffer, "desktop block {d}: {s}\n", .{ slot.run.block, @errorName(failure) }) catch unreachable;
            var message: Log = .{};
            message.length = text.len;
            @memcpy(message.bytes[0..text.len], text);
            message.bytes[text.len] = 0;
            self.push(.{ .log = message });
        };
        self.push(.{ .returned = slot.run });
        return 0;
    }
    fn runEntry(self: *Host, slot: *Slot) !void {
        switch (slot.run.block) {
            B.SingleInstance => try self.instance.run(slot.stop, self, instanceCommand, instanceRunning),
            B.Tray => try self.tray.run(slot.stop, self, trayEvent),
            B.BackendConnection => {
                running(slot);
                backend.run(@ptrCast(slot.stop.?), self, backendEvent, .{
                    .package_root = self.package_wide[0..self.package_len],
                    .backend_path = self.backend_wide[0..self.backend_len],
                    .service_name = self.service_wide[0..self.service_len :0],
                    .port = self.options.backend_port,
                    .probe_interval_ms = points.backend_probe_interval_ms,
                    .timeout_ms = points.backend_timeout_ms,
                });
            },
            B.WindowProcess => {
                // All initial values are copied while dispatching the block. The
                // running owner subsequently receives updates through its queue.
                var initial_session: Session = undefined;
                var language: Language = undefined;
                var show: window.ShowCommand = undefined;
                c.AcquireSRWLockExclusive(@ptrCast(&self.lock));
                initial_session = self.session;
                language = self.language;
                show = self.show_command;
                c.ReleaseSRWLockExclusive(@ptrCast(&self.lock));
                try self.client.run(@ptrCast(slot.stop.?), self, windowEvent, .{
                    .executable = self.options.window_executable,
                    .arguments = self.options.child_arguments,
                    .session = .{ .ready = initial_session.ready, .generation = @intCast(initial_session.generation), .reason = initial_session.reason() },
                    .language = language.bytes[0..language.len],
                    .show = show,
                    .cancel_poll_ms = points.window_cancel_poll_ms,
                    .stop_deadline_ms = points.window_stop_deadline_ms,
                    .timeout_policy = switch (points.window_stop_timeout_policy) {
                        .terminate => .terminate,
                        .wait => .wait,
                    },
                    .stop_wait_cancel = @ptrCast(self.ending_stop.?),
                });
            },
            else => {
                // Frontend, MainFrontend and SystemIntegration are packaging blocks.
                // The five future entries are
                // explicitly inert phase A stubs, with their declared conditions.
                running(slot);
                _ = c.WaitForSingleObject(slot.stop, c.INFINITE);
            },
        }
    }
    fn dispatch(self: *Host, event: Event) !void {
        switch (event) {
            .start => |run| {
                const slot = self.find(run.block);
                std.debug.assert(slot.thread == null);
                // A stop callback can reach the host queue before its start.
                // Consult the controller instead of caching another run state.
                const state = try self.engine.readState(run.block);
                if (state == .stopping or state == .stopped) {
                    try self.engine.reportReturned(run.block, run.number);
                    return;
                }
                errdefer {
                    if (run.block == B.WindowProcess) {
                        self.engine.publish(self.wanted, .{ .value = false }, reason("window-dispatch-failed"), null) catch {};
                        self.removeBackendReader() catch {};
                    }
                    if (slot.stop != null) {
                        _ = c.CloseHandle(slot.stop);
                        slot.stop = null;
                    }
                    self.engine.reportReturned(run.block, run.number) catch {};
                }
                slot.host = self;
                slot.run = run;
                if (run.block == B.WindowProcess) self.backend_reader = try self.engine.registerReader(.{ .item = I.BackendSession }, "window-process");
                if (run.block == B.BackendConnection) try self.engine.bind(self.ready, reason("backend-owner-started"), null);
                if (run.block == B.BackendConnection) try self.engine.bind(self.backend_session, reason("backend-owner-started"), null);
                slot.stop = c.CreateEventW(null, c.TRUE, c.FALSE, null);
                if (slot.stop == null) {
                    if (run.block == B.WindowProcess) {
                        try self.engine.publish(self.wanted, .{ .value = false }, reason("window-stop-token-failed"), null);
                        try self.removeBackendReader();
                    }
                    try self.engine.reportReturned(run.block, run.number);
                    return;
                }
                slot.thread = c.CreateThread(null, points.thread_stack_bytes, entry, slot, c.STACK_SIZE_PARAM_IS_A_RESERVATION, null);
                if (slot.thread == null) {
                    _ = c.CloseHandle(slot.stop);
                    slot.stop = null;
                    if (run.block == B.WindowProcess) {
                        try self.engine.publish(self.wanted, .{ .value = false }, reason("window-executor-failed"), null);
                        try self.removeBackendReader();
                    }
                    try self.engine.reportReturned(run.block, run.number);
                }
            },
            .stop => |run| {
                const slot = self.find(run.block);
                if (slot.thread != null and slot.run.number == run.number) {
                    _ = c.SetEvent(slot.stop);
                    // TrackPopupMenu owns a nested message loop. Cancel its modal
                    // wait as part of the host's queued stop delivery.
                    if (run.block == B.Tray and self.tray_window != null)
                        _ = c.PostMessageW(self.tray_window, c.WM_CANCELMODE, 0, 0);
                }
            },
            .returned => |run| {
                const slot = self.find(run.block);
                _ = c.WaitForSingleObject(slot.thread, c.INFINITE);
                _ = c.CloseHandle(slot.thread);
                _ = c.CloseHandle(slot.stop);
                slot.thread = null;
                slot.stop = null;
                if (run.block == B.WindowProcess) {
                    self.window_pid = 0;
                    self.window_handle = 0;
                    try self.removeBackendReader();
                }
                if (run.block == B.Tray) self.tray_window = null;
                if (run.block == B.BackendConnection) try self.engine.unbind(self.ready, reason("backend-owner-stopped"), null);
                if (run.block == B.BackendConnection) try self.engine.unbind(self.backend_session, reason("backend-owner-stopped"), null);
                try self.engine.reportReturned(run.block, run.number);
            },
            .ending => try self.beginEnding(),
            .user_close => try self.engine.publish(self.wanted, .{ .value = false }, reason("user-close"), null),
            .command => |command| {
                if (command == .exit) {
                    try self.beginEnding();
                    return;
                }
                if (self.ending or command == .ensure_running) return;
                c.AcquireSRWLockExclusive(@ptrCast(&self.lock));
                self.show_command = switch (command) {
                    .show_passive => .@"show-passive",
                    .show_passive_secondary => .@"show-passive-secondary",
                    else => .show,
                };
                c.ReleaseSRWLockExclusive(@ptrCast(&self.lock));
                try self.engine.publish(self.wanted, .{ .value = true }, reason("show-window"), null);
                self.client.showWindow(switch (command) {
                    .show_passive => .@"show-passive",
                    .show_passive_secondary => .@"show-passive-secondary",
                    else => .show,
                }) catch |err| if (err != error.NotRunning) return err;
            },
            .backend => |session| {
                c.AcquireSRWLockExclusive(@ptrCast(&self.lock));
                self.session = session;
                c.ReleaseSRWLockExclusive(@ptrCast(&self.lock));
                try self.engine.publish(self.ready, .{ .value = session.ready }, reason("backend-session"), null);
                try self.engine.publishPresence(self.backend_session, .present, reason("backend-session"), null);
            },
            .tray_running => |hwnd| {
                self.tray_window = hwnd;
                const slot = self.find(B.Tray);
                running(slot);
            },
            .window_handle => |hwnd| self.window_handle = hwnd,
            .window_pid => |pid| {
                self.window_pid = pid;
                // Refresh after the child handshake closes the creation/update race.
                try self.sendCurrentSession();
                self.client.sendLanguage(self.language.bytes[0..self.language.len]) catch |err| if (err != error.NotRunning) return err;
                self.client.showWindow(self.show_command) catch |err| if (err != error.NotRunning) return err;
            },
            .language => |language| {
                c.AcquireSRWLockExclusive(@ptrCast(&self.lock));
                self.language = language;
                c.ReleaseSRWLockExclusive(@ptrCast(&self.lock));
                try self.tray.setLanguage(language.bytes[0..language.len]);
            },
            .preferences_changed => try self.readPreferences(),
            .log => |message| self.writeLog(message.bytes[0..message.length :0]),
            .backend_item_changed => |registration| {
                if (self.backend_reader == registration) try self.sendCurrentSession();
            },
        }
    }
    fn removeBackendReader(self: *Host) !void {
        if (self.backend_reader) |registration| {
            self.backend_reader = null;
            try self.engine.unregisterReader(registration);
        }
    }
    fn sendCurrentSession(self: *Host) !void {
        const presence = try self.engine.readPresence(.{ .item = I.BackendSession });
        const absent: ?[]const u8 = switch (presence) {
            .present => null,
            .not_published => "not-published",
            .owner_stopped => "owner-stopped",
            .read_failed => "read-failed",
        };
        self.client.sendSession(.{ .ready = absent == null and self.session.ready, .generation = @intCast(self.session.generation), .reason = absent orelse self.session.reason() }) catch |err| if (err != error.NotRunning) return err;
    }
    fn beginEnding(self: *Host) !void {
        if (!self.ending) {
            self.ending = true;
            _ = c.SetEvent(self.ending_stop);
            try self.engine.shutdown(reason("program-ending"), null);
        }
    }
    fn drainAfterFailure(self: *Host) void {
        self.beginEnding() catch return;
        while (true) {
            while (self.pop()) |event| self.dispatch(event) catch {};
            self.engine.waitIdle();
            var all_stopped = true;
            for (topology.host_blocks) |block| {
                if ((self.engine.readState(block) catch return) != .stopped) {
                    all_stopped = false;
                    break;
                }
            }
            if (all_stopped) break;
            _ = c.WaitForSingleObject(self.wake, c.INFINITE);
        }
        self.engine.destroy() catch {};
    }
    fn writeLog(self: *Host, message: [:0]const u8) void {
        c.OutputDebugStringA(message.ptr);
        if (self.log_file != null) {
            var count: c.DWORD = 0;
            _ = c.WriteFile(self.log_file, message.ptr, @intCast(message.len), &count, null);
            _ = c.WriteFile(self.log_file, "\r\n", 2, &count, null);
        }
    }
    fn readPreferences(self: *Host) !void {
        const value = preferences.read(.{ .package_root = self.options.package_root, .preferences_path = self.options.preferences_path }) catch preferences.Snapshot.system();
        c.AcquireSRWLockExclusive(@ptrCast(&self.lock));
        self.language.len = value.language_len;
        @memcpy(self.language.bytes[0..self.language.len], value.languageSlice());
        c.ReleaseSRWLockExclusive(@ptrCast(&self.lock));
        try self.tray.setLanguage(value.languageSlice());
        try self.engine.publish(self.overlay_enabled, if (value.overlay_enabled) |enabled| .{ .value = enabled } else .not_published, reason("preferences-changed"), null);
        try self.engine.publish(self.notifications_enabled, if (value.notifications_enabled) |enabled| .{ .value = enabled } else .not_published, reason("preferences-changed"), null);
    }
    fn snapshot(self: *Host) !void {
        if (self.options.diagnostics.len == 0) return;
        var quoted_reason: [1024]u8 = undefined;
        const session_reason = try window.protocol.quote(&quoted_reason, self.session.reason());
        var prefix: [1536]u8 = undefined;
        const header = try std.fmt.bufPrint(&prefix, "{{\"pid\":{d},\"trayWindow\":{d},\"windowPid\":{d},\"windowHandle\":{d},\"ending\":{s},\"backendReady\":{s},\"backendGeneration\":{d},\"backendReason\":{s},\"controller\":", .{ c.GetCurrentProcessId(), @intFromPtr(self.tray_window), self.window_pid, self.window_handle, if (self.ending) "true" else "false", if (self.session.ready) "true" else "false", self.session.generation, session_reason });
        @memcpy(self.diagnostics_buffer[0..header.len], header);
        const length = try self.engine.snapshot(self.diagnostics_buffer[header.len .. self.diagnostics_buffer.len - 1]);
        if (length + header.len >= self.diagnostics_buffer.len) return error.SnapshotTooLarge;
        self.diagnostics_buffer[header.len + length] = '}';
        var rename: RenameInformation = undefined;
        const size = try std.unicode.utf8ToUtf16Le(rename.file_name[0 .. rename.file_name.len - 1], self.options.diagnostics);
        rename.file_name[size] = 0;
        rename.flags = file_rename_flag_replace_if_exists | file_rename_flag_posix_semantics;
        rename.root_directory = null;
        rename.file_name_length = @intCast(size * 2);
        var temporary_text: [32768]u8 = undefined;
        const temporary_name = try std.fmt.bufPrint(&temporary_text, "{s}.tmp", .{self.options.diagnostics});
        var temporary_path: [32768:0]u16 = undefined;
        const temporary_length = try std.unicode.utf8ToUtf16Le(&temporary_path, temporary_name);
        temporary_path[temporary_length] = 0;
        const file = c.CreateFileW(&temporary_path, c.GENERIC_WRITE | c.DELETE, c.FILE_SHARE_READ, null, c.CREATE_ALWAYS, c.FILE_ATTRIBUTE_NORMAL, null);
        if (file == c.INVALID_HANDLE_VALUE) return error.DiagnosticsWrite;
        defer _ = c.CloseHandle(file);
        var written: c.DWORD = 0;
        const bytes = header.len + length + 1;
        if (c.WriteFile(file, &self.diagnostics_buffer, @intCast(bytes), &written, null) == 0 or written != bytes) return error.DiagnosticsWrite;
        // Readers open the snapshot with FILE_SHARE_DELETE. A POSIX-semantics rename replaces it while a reader
        // still holds the old image; MoveFileExW fails with a sharing error in that case.
        const rename_bytes = @offsetOf(RenameInformation, "file_name") + (size + 1) * 2;
        if (c.SetFileInformationByHandle(file, file_rename_info_ex, &rename, @intCast(rename_bytes)) == 0) return error.DiagnosticsWrite;
    }
    fn runProcess(self: *Host) !void {
        try self.options.parse();
        self.tray.diagnostic_menu = self.options.diagnostics.len != 0;
        self.show_command = switch (self.options.command) {
            .show_passive => .@"show-passive",
            .show_passive_secondary => .@"show-passive-secondary",
            else => .show,
        };
        self.instance = try shell.Instance.init(self.options.pipe_name);
        defer self.instance.deinit();
        if (!self.instance.primary) {
            try self.instance.signal(self.options.command, points.instance_connect_timeout_ms, points.instance_connect_poll_ms);
            return;
        }
        if (self.options.command == .exit) return;
        self.package_len = try std.unicode.utf8ToUtf16Le(&self.package_wide, self.options.package_root);
        self.package_wide[self.package_len] = 0;
        self.backend_len = try std.unicode.utf8ToUtf16Le(&self.backend_wide, self.options.backend_path);
        self.backend_wide[self.backend_len] = 0;
        self.service_len = try std.unicode.utf8ToUtf16Le(&self.service_wide, self.options.backend_service);
        self.service_wide[self.service_len] = 0;
        self.wake = c.CreateEventW(null, c.TRUE, c.FALSE, null);
        if (self.wake == null) return error.Event;
        defer _ = c.CloseHandle(self.wake);
        self.ending_stop = c.CreateEventW(null, c.TRUE, c.FALSE, null);
        if (self.ending_stop == null) return error.Event;
        defer _ = c.CloseHandle(self.ending_stop);
        if (self.options.diagnostics.len != 0) {
            var name: [32768]u8 = undefined;
            const name_text = try std.fmt.bufPrint(&name, "{s}.log", .{self.options.diagnostics});
            var path: [32768:0]u16 = undefined;
            const length = try std.unicode.utf8ToUtf16Le(&path, name_text);
            path[length] = 0;
            self.log_file = c.CreateFileW(&path, c.GENERIC_WRITE, c.FILE_SHARE_READ, null, c.CREATE_ALWAYS, c.FILE_ATTRIBUTE_NORMAL, null);
            if (self.log_file == c.INVALID_HANDLE_VALUE) {
                self.log_file = null;
                return error.LogOpen;
            }
        }
        defer if (self.log_file != null) {
            _ = c.CloseHandle(self.log_file);
            self.log_file = null;
        };
        var version_log: [128:0]u8 = undefined;
        errdefer |failure| {
            var failure_log: [256:0]u8 = undefined;
            const message = std.fmt.bufPrintZ(&failure_log, "Resident failed: {s}", .{@errorName(failure)}) catch unreachable;
            self.writeLog(message);
        }
        self.writeLog(try std.fmt.bufPrintZ(&version_log, "State registry version {s}", .{topology.registry.version}));
        var allocator = std.heap.FixedBufferAllocator.init(&self.arena_buffer);
        self.engine = try topology.Engine.create(allocator.allocator(), .{
            .worker_count = points.controller_workers,
            .history_capacity = points.history_capacity,
            .demand_capacity = 16,
            .demand_bytes = 2048,
            .completion_capacity = 32,
            .reader_capacity = 16,
            .reader_bytes = 2048,
            .worker_arena_bytes = points.worker_arena_bytes,
            .demand_item = null,
            .shutdown_item = I.ProgramShuttingDown,
            .host_blocks = &topology.host_blocks,
        }, .{ .context = self, .start = startCallback, .stop = stopCallback, .complete = completeCallback, .log = logCallback, .item_changed = itemCallback });
        var owns_engine = true;
        defer if (owns_engine) self.drainAfterFailure();
        self.engine.activate();
        self.started = try self.engine.claimWrite(.{ .item = I.ProgramStarted });
        self.wanted = try self.engine.claimWrite(.{ .item = I.WindowWanted });
        self.ready = try self.engine.claimCell(.{ .item = I.BackendSessionReady });
        self.backend_session = try self.engine.claimCell(.{ .item = I.BackendSession });
        self.overlay_enabled = try self.engine.claimWrite(.{ .item = I.OverlayEnabled });
        self.notifications_enabled = try self.engine.claimWrite(.{ .item = I.NotificationsEnabled });
        try self.readPreferences();
        try self.engine.publish(self.wanted, .{ .value = self.options.command != .ensure_running }, reason("startup-window"), null);
        try self.engine.publish(self.started, .{ .value = true }, reason("program-started"), null);
        const handles = [_]c.HANDLE{self.wake};
        while (true) {
            const result = c.MsgWaitForMultipleObjects(1, &handles, c.FALSE, c.INFINITE, c.QS_ALLINPUT);
            if (result == c.WAIT_FAILED) return error.MessageWait;
            var message: c.MSG = undefined;
            while (c.PeekMessageW(&message, null, 0, 0, c.PM_REMOVE) != 0) {
                _ = c.TranslateMessage(&message);
                _ = c.DispatchMessageW(&message);
            }
            while (self.pop()) |event| try self.dispatch(event);
            self.engine.waitIdle();
            try self.snapshot();
            if (try topology.mayExitMessageLoop(self.engine, self.ending)) break;
        }
        try self.engine.destroy();
        owns_engine = false;
    }
};

var host: Host = .{};
pub fn main() void {
    host.runProcess() catch |failure| {
        var buffer: [256:0]u8 = undefined;
        const text = std.fmt.bufPrintZ(&buffer, "ResourceManager.Desktop: {s}", .{@errorName(failure)}) catch unreachable;
        c.OutputDebugStringA(text);
        c.ExitProcess(1);
    };
}
