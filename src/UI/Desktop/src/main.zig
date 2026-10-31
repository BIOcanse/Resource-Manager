//! The controller owns states and decisions. Its callbacks only enqueue;
//! native host threads own run entries, stop events and diagnostics.
const std = @import("std");
const memory = @import("memory.zig");
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
const overlay = @import("overlay/root.zig");
const Options = @import("options.zig").Options;
const points = @import("desktop_points_values").Bound;
const preferences = @import("preferences.zig");
const lookup = @import("text/lookup.zig");
const subscriptions = @import("backend/subscriptions.zig");
const Definition = @import("hotkeys/definition.zig").Definition;
const force_terminate = @import("hotkeys/force_terminate.zig");
const overlay_toggle = @import("hotkeys/overlay_toggle.zig");
const task_manager = @import("hotkeys/task_manager.zig");
const observation = @import("desktop/observation.zig");
const notifications = @import("notifications/root.zig");
const Claim = control.state.Claim;
const settings_source: subscriptions.Source = .{ .id = "settings", .path = "/api/settings/app/subscribe" };
const OverlayJson = struct { len: usize, data: [points.overlay_settings_bytes]u8 };
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
    /// Backend setting `systemIntegration` from the settings subscription.
    settings: subscriptions.SystemIntegration,
    /// The settings subscription was rejected: the three facts are unknown.
    settings_rejected,
    /// The settings' `performanceOverlay` section was stored in the host
    /// buffer; the value is the OverlayEnabled fact, null when unknown.
    overlay_settings: ?bool,
    visibility: Visibility,
};
const Run = struct { block: u32, number: u64 };
const Visibility = struct { handle: usize, visible: bool };
const Language = struct { bytes: [32]u8 = @splat(0), len: usize = 0 };
const Session = struct {
    ready: bool = false,
    generation: u64 = 0,
    bytes: [256]u8 = @splat(0),
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
    ledger: memory.Owner = .{},
    engine: *topology.Engine = undefined,
    options: Options = .{},
    instance: shell.Instance = undefined,
    tray: tray_module.Tray = .{},
    client: window.Client = .{},
    wake: c.HANDLE = null,
    // Program stop cancels a window branch's cooperative shutdown wait.
    // This token outlives all runs; per-run stop tokens still own normal close.
    ending_stop: c.HANDLE = null,
    // Auto-reset: set after every overlay settings or language change; the overlay block re-reads its inputs on it.
    overlay_changed: c.HANDLE = null,
    log_file: c.HANDLE = null,
    lock: usize = 0,
    // One queued notice per transition plus bounded shell/runtime events.
    queue: [512]Event = undefined,
    head: usize = 0,
    count: usize = 0,
    slots: [topology.host_blocks.len]Slot = @as([topology.host_blocks.len]Slot, @splat(.{})),
    started: Claim = undefined,
    wanted: Claim = undefined,
    ready: Claim = undefined,
    backend_session: Claim = undefined,
    overlay_enabled: Claim = undefined,
    notifications_enabled: Claim = undefined,
    hotkey_enabled: Claim = undefined,
    hotkey_valid: Claim = undefined,
    task_manager_enabled: Claim = undefined,
    overlay_toggle_enabled: Claim = undefined,
    overlay_toggle_valid: Claim = undefined,
    // Read by block threads under `lock`; written by dispatch.
    binding: Definition = .{},
    overlay_binding: Definition = .{},
    // Settings thread owns the stream token; readers signal it under `lock`
    // to replay the same subscription after the Ledger cache was reclaimed.
    settings_stream_stop: c.HANDLE = null,
    // The last foreground window's process that was neither the shell's taskbar
    // nor this process, from DesktopObservation; the tray's overlay item names
    // its software. Written by the observation thread, read by the tray thread.
    last_foreground: ?observation.Entry = null,
    switches: notifications.Switches = .{},
    interface_visible: bool = false,
    // Owned by the BackendConnection run: set while its session is ready, so the
    // settings subscription only connects while the backend is known up.
    settings_ready: c.HANDLE = null,
    record_wide: [notifications.max_path_units:0]u16 = undefined,
    record_len: usize = 0,
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
    fn overlayLog(context: *anyopaque, text: []const u8) void {
        var value: Log = .{};
        value.length = @min(text.len, value.bytes.len - 1);
        @memcpy(value.bytes[0..value.length], text[0..value.length]);
        value.bytes[value.length] = 0;
        from(context).push(.{ .log = value });
    }
    fn overlaySettings(context: *anyopaque, output: []u8) usize {
        const self = from(context);
        c.AcquireSRWLockShared(@ptrCast(&self.lock));
        defer c.ReleaseSRWLockShared(@ptrCast(&self.lock));
        var lease = (self.ledger.use(.overlay_settings_json) catch return 0) orelse {
            if (self.settings_stream_stop != null) _ = c.SetEvent(self.settings_stream_stop);
            return 0;
        };
        defer _ = lease.endUse();
        const buffer = lease.as(OverlayJson);
        if (buffer.len > output.len) return 0;
        @memcpy(output[0..buffer.len], buffer.data[0..buffer.len]);
        return buffer.len;
    }
    fn overlayLanguage(context: *anyopaque, output: *[32]u8) usize {
        const self = from(context);
        c.AcquireSRWLockExclusive(@ptrCast(&self.lock));
        defer c.ReleaseSRWLockExclusive(@ptrCast(&self.lock));
        const length = @min(self.language.len, output.len);
        @memcpy(output[0..length], self.language.bytes[0..length]);
        return length;
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
            .overlay_menu => |menu| return self.readOverlayMenu(menu),
            .overlay_toggle => |menu| return self.toggleOverlayFromTray(menu),
        });
    }
    /// Tray thread, before the menu opens: the software of the window that was
    /// foreground before the tray took the focus, and its current switch.
    fn readOverlayMenu(self: *Host, menu: *tray_module.OverlayMenu) void {
        c.AcquireSRWLockShared(@ptrCast(&self.lock));
        const ready = self.session.ready;
        const foreground = self.last_foreground;
        c.ReleaseSRWLockShared(@ptrCast(&self.lock));
        menu.* = .{ .state = .backend_unavailable };
        if (!ready) return;
        const process = foreground orelse {
            menu.state = .no_foreground;
            return;
        };
        var lease = self.ledger.acquire(.tray_call, @sizeOf(overlay_toggle.Workspace)) catch {
            menu.state = .failed;
            return;
        };
        defer _ = lease.endUse();
        const workspace = lease.as(overlay_toggle.Workspace);
        workspace.channel = .{};
        const link = backend.Link.create(self.backendConfig()) catch {
            menu.state = .failed;
            return;
        };
        defer link.destroy();
        const outcome = overlay_toggle.readForeground(workspace, link.opener(), self.ending_stop, process.pid, process.created) catch |err| {
            self.pushText(@errorName(err));
            menu.state = .failed;
            return;
        };
        switch (outcome) {
            .rejected => |cause| menu.state = if (overlay_toggle.noForegroundSoftware(cause)) .no_foreground else .failed,
            .software => |software| menu.set(software.id, software.name, software.enabled),
        }
        lease.publish();
    }
    /// Tray thread: toggle the named software's current backend value. The
    /// check mark may have changed while the context menu was open.
    fn toggleOverlayFromTray(self: *Host, menu: *const tray_module.OverlayMenu) void {
        const text = self.trayText();
        var lease = self.ledger.acquire(.tray_call, @sizeOf(overlay_toggle.Workspace)) catch |err| {
            const failed = overlay_toggle.report(text, err);
            _ = self.tray.showBalloon(&failed);
            return;
        };
        defer _ = lease.endUse();
        const workspace = lease.as(overlay_toggle.Workspace);
        workspace.channel = .{};
        const value = blk: {
            const link = backend.Link.create(self.backendConfig()) catch |err| break :blk overlay_toggle.report(text, err);
            defer link.destroy();
            break :blk overlay_toggle.report(text, overlay_toggle.toggleSoftware(workspace, link.opener(), self.ending_stop, menu.softwareId(), null));
        };
        // A successful click shows its result as the item's check mark; only a failure is reported.
        if (value.icon != .info) _ = self.tray.showBalloon(&value);
        lease.publish();
    }
    fn trayText(self: *Host) *const lookup.Text {
        c.AcquireSRWLockShared(@ptrCast(&self.lock));
        defer c.ReleaseSRWLockShared(@ptrCast(&self.lock));
        return lookup.select(self.language.bytes[0..self.language.len]);
    }
    fn backendEvent(context: *anyopaque, ready: bool, generation: u64, cause: []const u8) void {
        var session: Session = .{ .ready = ready, .generation = generation };
        session.len = @min(cause.len, session.bytes.len);
        @memcpy(session.bytes[0..session.len], cause[0..session.len]);
        const self = from(context);
        // Same thread as the BackendConnection run that owns the handle.
        if (self.settings_ready != null) _ = if (ready) c.SetEvent(self.settings_ready) else c.ResetEvent(self.settings_ready);
        self.push(.{ .backend = session });
    }
    // ---- callbacks of the resident blocks; the context is the block's slot ----
    fn slotHost(raw: *anyopaque) *Host {
        const slot: *Slot = @ptrCast(@alignCast(raw));
        return slot.host;
    }
    fn blockRunning(raw: *anyopaque) void {
        running(raw);
    }
    fn blockLog(raw: *anyopaque, text: []const u8) void {
        const slot: *Slot = @ptrCast(@alignCast(raw));
        var message: Log = .{};
        const value = std.fmt.bufPrint(&message.bytes, "desktop block {d}: {s}", .{ slot.run.block, text[0..@min(text.len, 900)] }) catch unreachable;
        message.length = value.len;
        message.bytes[value.len] = 0;
        slot.host.push(.{ .log = message });
    }
    fn currentText(raw: *anyopaque) *const lookup.Text {
        const self = slotHost(raw);
        c.AcquireSRWLockShared(@ptrCast(&self.lock));
        defer c.ReleaseSRWLockShared(@ptrCast(&self.lock));
        return lookup.select(self.language.bytes[0..self.language.len]);
    }
    fn showBalloon(raw: *anyopaque, balloon: *const lookup.Balloon) void {
        const self = slotHost(raw);
        if (!self.tray.showBalloon(balloon)) blockLog(raw, "balloon not shown: no tray icon");
    }
    fn currentBinding(raw: *anyopaque) Definition {
        const self = slotHost(raw);
        c.AcquireSRWLockShared(@ptrCast(&self.lock));
        defer c.ReleaseSRWLockShared(@ptrCast(&self.lock));
        return self.binding;
    }
    fn currentOverlayBinding(raw: *anyopaque) Definition {
        const self = slotHost(raw);
        c.AcquireSRWLockShared(@ptrCast(&self.lock));
        defer c.ReleaseSRWLockShared(@ptrCast(&self.lock));
        return self.overlay_binding;
    }
    fn backendReady(raw: *anyopaque) bool {
        const self = slotHost(raw);
        c.AcquireSRWLockShared(@ptrCast(&self.lock));
        defer c.ReleaseSRWLockShared(@ptrCast(&self.lock));
        return self.session.ready;
    }
    fn openWindow(raw: *anyopaque) void {
        slotHost(raw).push(.{ .command = .show });
    }
    fn currentSwitches(raw: *anyopaque) notifications.Switches {
        const self = slotHost(raw);
        c.AcquireSRWLockShared(@ptrCast(&self.lock));
        defer c.ReleaseSRWLockShared(@ptrCast(&self.lock));
        return self.switches;
    }
    fn interfaceVisible(raw: *anyopaque) bool {
        const self = slotHost(raw);
        c.AcquireSRWLockShared(@ptrCast(&self.lock));
        defer c.ReleaseSRWLockShared(@ptrCast(&self.lock));
        return self.interface_visible;
    }
    fn observedWindow(raw: *anyopaque) c.HWND {
        const self = slotHost(raw);
        c.AcquireSRWLockShared(@ptrCast(&self.lock));
        const value = self.window_handle;
        c.ReleaseSRWLockShared(@ptrCast(&self.lock));
        return handleFromInt(value);
    }
    fn observedVisibility(raw: *anyopaque, handle: c.HWND, visible: bool) void {
        slotHost(raw).push(.{ .visibility = .{ .handle = @intFromPtr(handle), .visible = visible } });
    }
    fn observedForeground(raw: *anyopaque, process: observation.Entry) void {
        const self = slotHost(raw);
        c.AcquireSRWLockExclusive(@ptrCast(&self.lock));
        self.last_foreground = process;
        c.ReleaseSRWLockExclusive(@ptrCast(&self.lock));
    }
    /// Window handles are not 4-byte aligned; the cast skips the alignment
    /// check of the translated HWND type (the value is never dereferenced).
    fn handleFromInt(value: usize) c.HWND {
        @setRuntimeSafety(false);
        return @ptrFromInt(value);
    }
    fn backendConfig(self: *Host) backend.Config {
        return .{
            .package_root = self.package_wide[0..self.package_len],
            .backend_path = self.backend_wide[0..self.backend_len],
            .service_name = self.service_wide[0..self.service_len :0],
            .port = self.options.backend_port,
            .probe_interval_ms = points.backend_probe_interval_ms,
            .timeout_ms = points.backend_timeout_ms,
        };
    }
    /// Posts a thread message to a running block's own message loop.
    fn postToBlock(self: *Host, block: u32, message: c.UINT) void {
        const slot = self.find(block);
        if (slot.thread != null) _ = c.PostThreadMessageW(c.GetThreadId(slot.thread), message, 0, 0);
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
            const text = std.mem.printSentinel(&buffer, "desktop block {d}: {s}\n", .{ slot.run.block, @errorName(failure) }, 0) catch unreachable;
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
                try self.runBackendConnection(slot);
            },
            B.DesktopObservation => {
                const link = try backend.Link.create(self.backendConfig());
                defer link.destroy();
                try observation.run(slot.stop, .{
                    .host = .{ .context = slot, .running = blockRunning, .window = observedWindow, .visibility = observedVisibility, .foreground = observedForeground, .log = blockLog },
                    .opener = link.opener(),
                    .retry_ms = points.backend_probe_interval_ms,
                });
            },
            B.TrayNotifications => {
                if (self.record_len == 0) return error.NotificationRecordPathUnavailable;
                const link = try backend.Link.create(self.backendConfig());
                defer link.destroy();
                try notifications.run(slot.stop, .{
                    .host = .{ .context = slot, .running = blockRunning, .switches = currentSwitches, .interface_visible = interfaceVisible, .text = currentText, .balloon = showBalloon, .log = blockLog },
                    .opener = link.opener(),
                    .retry_ms = points.backend_probe_interval_ms,
                    .record_path = self.record_wide[0..self.record_len :0],
                });
            },
            B.ForceTerminateHotkey => {
                const link = try backend.Link.create(self.backendConfig());
                defer link.destroy();
                try force_terminate.run(slot.stop, .{
                    .ledger = &self.ledger,
                    .host = .{ .context = slot, .running = blockRunning, .binding = currentBinding, .balloon = showBalloon, .text = currentText, .backend_ready = backendReady },
                    .opener = link.opener(),
                    .thread_stack_bytes = points.thread_stack_bytes,
                });
            },
            B.OverlayToggleHotkey => {
                const link = try backend.Link.create(self.backendConfig());
                defer link.destroy();
                try overlay_toggle.run(slot.stop, .{
                    .ledger = &self.ledger,
                    .host = .{ .context = slot, .running = blockRunning, .binding = currentOverlayBinding, .balloon = showBalloon, .text = currentText, .backend_ready = backendReady },
                    .opener = link.opener(),
                    .thread_stack_bytes = points.thread_stack_bytes,
                });
            },
            B.TaskManagerReplacement => try task_manager.run(slot.stop, .{ .context = slot, .running = blockRunning, .open = openWindow, .balloon = showBalloon, .text = currentText }),
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
            B.Overlay => {
                running(slot);
                const link = try backend.Link.create(self.backendConfig());
                defer link.destroy();
                try overlay.run(&self.ledger, @ptrCast(slot.stop.?), @ptrCast(self.overlay_changed.?), .{ .context = self, .log = overlayLog, .language = overlayLanguage, .settings = overlaySettings }, .{
                    .opener = link.opener(),
                    .sample_interval_ms = @intCast(points.overlay_sample_interval_ms),
                    .settings_bytes = points.overlay_settings_bytes,
                    .retry_ms = @intCast(points.overlay_stream_retry_ms),
                    .stack_bytes = points.thread_stack_bytes,
                    .render = .{
                        .target = comptime std.meta.stringToEnum(overlay.render.Target, @tagName(points.overlay_render_target)).?,
                        .text_mode = comptime std.meta.stringToEnum(overlay.render.TextMode, @tagName(points.overlay_text_rendering_mode)).?,
                    },
                });
            },
            else => {
                // Frontend, MainFrontend and SystemIntegration are wrapper blocks:
                // they report running and wait for stop.
                running(slot);
                _ = c.WaitForSingleObject(slot.stop, c.INFINITE);
            },
        }
    }
    /// Health monitoring on this thread plus the settings subscription on one
    /// inner thread that is joined before return. The settings stream waits
    /// for the ready session, so it connects only while the backend is up.
    fn runBackendConnection(self: *Host, slot: *Slot) !void {
        const config = self.backendConfig();
        const settings_stop = c.CreateEventW(null, c.TRUE, c.FALSE, null) orelse return error.Event;
        defer _ = c.CloseHandle(settings_stop);
        const ready = c.CreateEventW(null, c.TRUE, c.FALSE, null) orelse return error.Event;
        defer _ = c.CloseHandle(ready);
        const link = try backend.Link.create(config);
        defer link.destroy();
        var settings: Settings = .{ .host = self, .stop = settings_stop, .ready = ready, .opener = link.opener() };
        const thread = c.CreateThread(null, points.thread_stack_bytes, Settings.entry, &settings, c.STACK_SIZE_PARAM_IS_A_RESERVATION, null) orelse return error.Thread;
        defer _ = c.CloseHandle(thread);
        self.settings_ready = ready;
        backend.run(@ptrCast(slot.stop.?), self, backendEvent, config);
        self.settings_ready = null;
        self.stopSettings(settings_stop);
        _ = c.WaitForSingleObject(thread, c.INFINITE);
    }
    fn stopSettings(self: *Host, owner_stop: c.HANDLE) void {
        _ = c.SetEvent(owner_stop);
        c.AcquireSRWLockShared(@ptrCast(&self.lock));
        defer c.ReleaseSRWLockShared(@ptrCast(&self.lock));
        if (self.settings_stream_stop != null) _ = c.SetEvent(self.settings_stream_stop);
    }
    /// Settings thread: keeps the performanceOverlay section for the overlay
    /// block and queues the OverlayEnabled fact it implies.
    fn storeOverlaySettings(self: *Host, settings: std.json.Value) void {
        const enabled = self.writeOverlaySettings(settings) catch |err| {
            self.pushText(@errorName(err));
            self.push(.{ .overlay_settings = null });
            return;
        };
        self.push(.{ .overlay_settings = enabled });
    }
    fn writeOverlaySettings(self: *Host, settings: std.json.Value) !?bool {
        c.AcquireSRWLockExclusive(@ptrCast(&self.lock));
        defer c.ReleaseSRWLockExclusive(@ptrCast(&self.lock));
        var lease = try self.ledger.acquire(.overlay_settings_json, @sizeOf(OverlayJson));
        defer _ = lease.endUse();
        const buffer = lease.as(OverlayJson);
        buffer.len = 0;
        const section = try subscriptions.overlaySection(settings, &buffer.data);
        buffer.len = section.json.len;
        lease.publish();
        return section.enabled_any;
    }
    fn clearOverlaySettings(self: *Host) void {
        c.AcquireSRWLockExclusive(@ptrCast(&self.lock));
        defer c.ReleaseSRWLockExclusive(@ptrCast(&self.lock));
        var lease = (self.ledger.use(.overlay_settings_json) catch return) orelse return;
        defer _ = lease.endUse();
        lease.as(OverlayJson).len = 0;
    }
    fn pushText(self: *Host, text: []const u8) void {
        var message: Log = .{};
        message.length = @min(text.len, message.bytes.len - 1);
        @memcpy(message.bytes[0..message.length], text[0..message.length]);
        message.bytes[message.length] = 0;
        self.push(.{ .log = message });
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
                    // Hook blocks wait in GetMessage on their own thread; the queue
                    // exists before they check stop, so this post is never lost.
                    if (run.block == B.ForceTerminateHotkey or run.block == B.OverlayToggleHotkey or run.block == B.TaskManagerReplacement or run.block == B.DesktopObservation)
                        _ = c.PostThreadMessageW(c.GetThreadId(slot.thread), c.WM_QUIT, 0, 0);
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
                    self.setWindowHandle(0);
                    try self.removeBackendReader();
                }
                if (run.block == B.DesktopObservation) {
                    self.setInterfaceVisible(false);
                    // The remembered foreground is only as current as the observation that wrote it.
                    c.AcquireSRWLockExclusive(@ptrCast(&self.lock));
                    self.last_foreground = null;
                    c.ReleaseSRWLockExclusive(@ptrCast(&self.lock));
                }
                if (run.block == B.Tray) self.tray_window = null;
                if (run.block == B.BackendConnection) try self.engine.unbind(self.ready, reason("backend-owner-stopped"), null);
                if (run.block == B.BackendConnection) try self.engine.unbind(self.backend_session, reason("backend-owner-stopped"), null);
                // The settings subscription ran inside BackendConnection: its facts end with it.
                if (run.block == B.BackendConnection) {
                    self.clearOverlaySettings();
                    try self.publishSettings(null, "settings-owner-stopped");
                }
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
            .window_handle => |hwnd| self.setWindowHandle(hwnd),
            .settings => |value| try self.publishSettings(value, "backend-settings"),
            .settings_rejected => try self.publishSettings(null, "settings-subscription-rejected"),
            .overlay_settings => |enabled| {
                try self.engine.publish(self.overlay_enabled, if (enabled) |value| .{ .value = value } else .not_published, reason("backend-settings"), null);
                _ = c.SetEvent(self.overlay_changed);
            },
            .visibility => |value| {
                // A result computed for an earlier handle is stale.
                if (value.handle != self.window_handle) return;
                self.setInterfaceVisible(value.visible);
                self.client.sendVisibility(value.visible) catch |err| if (err != error.NotRunning) return err;
            },
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
                _ = c.SetEvent(self.overlay_changed);
            },
            .preferences_changed => {
                try self.readPreferences();
                _ = c.SetEvent(self.overlay_changed);
            },
            .log => |message| self.writeLog(message.bytes[0..message.length :0]),
            .backend_item_changed => |registration| {
                if (self.backend_reader == registration) try self.sendCurrentSession();
            },
        }
    }
    fn setWindowHandle(self: *Host, handle: usize) void {
        if (self.window_handle == handle) return;
        c.AcquireSRWLockExclusive(@ptrCast(&self.lock));
        self.window_handle = handle;
        if (handle == 0) self.interface_visible = false;
        c.ReleaseSRWLockExclusive(@ptrCast(&self.lock));
        // DesktopObservation re-reads the handle and retargets its hooks.
        self.postToBlock(B.DesktopObservation, observation.window_message);
    }
    fn setInterfaceVisible(self: *Host, visible: bool) void {
        c.AcquireSRWLockExclusive(@ptrCast(&self.lock));
        self.interface_visible = visible;
        c.ReleaseSRWLockExclusive(@ptrCast(&self.lock));
    }
    /// Null means unknown (rejected subscription or its owner stopped).
    fn publishSettings(self: *Host, value: ?subscriptions.SystemIntegration, cause: []const u8) !void {
        const settings = value orelse {
            c.AcquireSRWLockExclusive(@ptrCast(&self.lock));
            self.binding = .{};
            self.overlay_binding = .{};
            c.ReleaseSRWLockExclusive(@ptrCast(&self.lock));
            try self.engine.publish(self.hotkey_enabled, .not_published, reason(cause), null);
            try self.engine.publish(self.hotkey_valid, .not_published, reason(cause), null);
            try self.engine.publish(self.task_manager_enabled, .not_published, reason(cause), null);
            try self.engine.publish(self.overlay_toggle_enabled, .not_published, reason(cause), null);
            try self.engine.publish(self.overlay_toggle_valid, .not_published, reason(cause), null);
            // The overlay section came from the same subscription: unknown with it.
            // The settings producer invalidates its cache before queuing this
            // event. Do not clear a newer frame already written by that thread.
            try self.engine.publish(self.overlay_enabled, .not_published, reason(cause), null);
            _ = c.SetEvent(self.overlay_changed);
            return;
        };
        const valid = settings.hotkey.isSafe();
        // The block only ever sees an enabled, safe binding; disabling stops it.
        const binding: Definition = if (settings.hotkey_enabled and valid) settings.hotkey else .{};
        c.AcquireSRWLockExclusive(@ptrCast(&self.lock));
        const changed = !self.binding.eql(&binding);
        self.binding = binding;
        c.ReleaseSRWLockExclusive(@ptrCast(&self.lock));
        try self.engine.publish(self.hotkey_enabled, .{ .value = settings.hotkey_enabled }, reason(cause), null);
        try self.engine.publish(self.hotkey_valid, .{ .value = valid }, reason(cause), null);
        try self.engine.publish(self.task_manager_enabled, .{ .value = settings.task_manager_enabled }, reason(cause), null);
        // A key change while enabled is re-registration inside the running block (scenario 8).
        if (changed) self.postToBlock(B.ForceTerminateHotkey, force_terminate.rebind_message);
        // The overlay toggle is not destructive: any non-empty key set is valid.
        const overlay_valid = !settings.overlay_toggle.isEmpty();
        const overlay_binding: Definition = if (settings.overlay_toggle_enabled and overlay_valid) settings.overlay_toggle else .{};
        c.AcquireSRWLockExclusive(@ptrCast(&self.lock));
        const overlay_changed = !self.overlay_binding.eql(&overlay_binding);
        self.overlay_binding = overlay_binding;
        c.ReleaseSRWLockExclusive(@ptrCast(&self.lock));
        try self.engine.publish(self.overlay_toggle_enabled, .{ .value = settings.overlay_toggle_enabled }, reason(cause), null);
        try self.engine.publish(self.overlay_toggle_valid, .{ .value = overlay_valid }, reason(cause), null);
        if (overlay_changed) self.postToBlock(B.OverlayToggleHotkey, overlay_toggle.rebind_message);
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
        // TrayNotifications reads its kind's switches when it decides a balloon.
        self.switches = .{
            .memory_protection = value.memory_protection orelse false,
            .update = value.update orelse false,
            .access_request = value.access_request orelse false,
            .crash_report = value.crash_report orelse false,
        };
        c.ReleaseSRWLockExclusive(@ptrCast(&self.lock));
        try self.tray.setLanguage(value.languageSlice());
        try self.engine.publish(self.notifications_enabled, if (value.notifications_enabled) |enabled| .{ .value = enabled } else .not_published, reason("preferences-changed"), null);
    }
    /// The tray's notification record beside the frontend cache. Left empty
    /// when the path cannot be formed; TrayNotifications then fails visibly.
    fn recordPath(self: *Host) void {
        var text: [notifications.max_path_units]u8 = undefined;
        const cache = preferences.cachePath(.{ .package_root = self.options.package_root, .preferences_path = self.options.preferences_path }, &text) catch return;
        if (cache.len + notifications.record_suffix.len > text.len) return;
        @memcpy(text[cache.len..][0..notifications.record_suffix.len], notifications.record_suffix);
        const length = std.unicode.utf8ToUtf16Le(self.record_wide[0 .. self.record_wide.len - 1], text[0 .. cache.len + notifications.record_suffix.len]) catch return;
        self.record_wide[length] = 0;
        self.record_len = length;
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
        self.recordPath();
        self.wake = c.CreateEventW(null, c.TRUE, c.FALSE, null);
        if (self.wake == null) return error.Event;
        defer _ = c.CloseHandle(self.wake);
        self.ending_stop = c.CreateEventW(null, c.TRUE, c.FALSE, null);
        if (self.ending_stop == null) return error.Event;
        defer _ = c.CloseHandle(self.ending_stop);
        self.overlay_changed = c.CreateEventW(null, c.FALSE, c.FALSE, null);
        if (self.overlay_changed == null) return error.Event;
        defer _ = c.CloseHandle(self.overlay_changed);
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
        self.runResident() catch |failure| {
            var failure_log: [256:0]u8 = undefined;
            const message = std.mem.printSentinel(&failure_log, "Resident failed: {s}", .{@errorName(failure)}, 0) catch unreachable;
            self.writeLog(message);
            return failure;
        };
    }
    /// The resident part of `runProcess`, after the wake events and the log are open;
    /// `runProcess` logs its failure before closing them.
    fn runResident(self: *Host) !void {
        try self.ledger.init();
        // Engine cleanup/drain is deferred below and runs before this owner.
        defer self.ledger.deinit() catch @panic("Desktop Ledger shutdown before all users ended");
        var version_log: [128:0]u8 = undefined;
        self.writeLog(try std.mem.printSentinel(&version_log, "State registry version {s}", .{topology.registry.version}, 0));
        var allocator = std.heap.FixedBufferAllocator.init(&self.arena_buffer);
        self.engine = try topology.Engine.create(allocator.allocator(), .{
            .worker_count = points.controller_workers,
            .history_capacity = points.history_capacity,
            .completion_capacity = 32,
            .reader_capacity = 16,
            .reader_bytes = 2048,
            .worker_arena_bytes = points.worker_arena_bytes,
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
        self.hotkey_enabled = try self.engine.claimWrite(.{ .item = I.ForceTerminateHotkeyEnabled });
        self.hotkey_valid = try self.engine.claimWrite(.{ .item = I.ForceTerminateHotkeyValid });
        self.task_manager_enabled = try self.engine.claimWrite(.{ .item = I.TaskManagerReplacementEnabled });
        self.overlay_toggle_enabled = try self.engine.claimWrite(.{ .item = I.OverlayToggleHotkeyEnabled });
        self.overlay_toggle_valid = try self.engine.claimWrite(.{ .item = I.OverlayToggleHotkeyValid });
        try self.readPreferences();
        try self.engine.publish(self.wanted, .{ .value = self.options.command != .ensure_running }, reason("startup-window"), null);
        try self.engine.publish(self.started, .{ .value = true }, reason("program-started"), null);
        const handles = [_]c.HANDLE{self.wake};
        const tick_interval: u64 = @intCast(points.ledger_tick_interval_ms);
        var tick_deadline = c.GetTickCount64() + tick_interval;
        while (true) {
            const now = c.GetTickCount64();
            const wait_ms: c.DWORD = @intCast(tick_deadline -| now);
            const result = c.MsgWaitForMultipleObjects(1, &handles, c.FALSE, wait_ms, c.QS_ALLINPUT);
            if (result == c.WAIT_FAILED) return error.MessageWait;
            var message: c.MSG = undefined;
            while (c.PeekMessageW(&message, null, 0, 0, c.PM_REMOVE) != 0) {
                _ = c.TranslateMessage(&message);
                _ = c.DispatchMessageW(&message);
            }
            while (self.pop()) |event| try self.dispatch(event);
            self.engine.waitIdle();
            const after_events = c.GetTickCount64();
            if (after_events >= tick_deadline) {
                _ = try self.ledger.tick();
                tick_deadline = c.GetTickCount64() + tick_interval;
            }
            try self.snapshot();
            if (try topology.mayExitMessageLoop(self.engine, self.ending)) break;
        }
        try self.engine.destroy();
        owns_engine = false;
    }
};

/// One owner of the existing settings subscription, including explicit replay.
const Settings = struct {
    host: *Host,
    stop: c.HANDLE,
    ready: c.HANDLE,
    opener: @import("backend/channel.zig").Opener,

    fn entry(raw: ?*anyopaque) callconv(.winapi) c.DWORD {
        const self: *Settings = @ptrCast(@alignCast(raw.?));
        self.run() catch |err| {
            self.host.pushText(@errorName(err));
            self.host.clearOverlaySettings();
            self.host.push(.settings_rejected);
            return 1;
        };
        return 0;
    }
    fn run(self: *Settings) !void {
        var lease = try self.host.ledger.acquire(.backend_settings_subscription, @sizeOf(subscriptions.Workspace));
        defer _ = lease.endUse();
        const workspace = lease.as(subscriptions.Workspace);
        workspace.channel = .{};
        const stream_stop = c.CreateEventW(null, c.TRUE, c.FALSE, null) orelse return error.Event;
        defer _ = c.CloseHandle(stream_stop);
        c.AcquireSRWLockExclusive(@ptrCast(&self.host.lock));
        self.host.settings_stream_stop = stream_stop;
        c.ReleaseSRWLockExclusive(@ptrCast(&self.host.lock));
        defer {
            c.AcquireSRWLockExclusive(@ptrCast(&self.host.lock));
            self.host.settings_stream_stop = null;
            c.ReleaseSRWLockExclusive(@ptrCast(&self.host.lock));
        }
        var body: [256]u8 = undefined;
        const request = try subscriptions.requestBody(&body, &.{settings_source});
        while (true) {
            // Reset/publication, replay signals and shutdown cannot race handle
            // closure or let a reset erase the owner's terminal stop.
            c.AcquireSRWLockExclusive(@ptrCast(&self.host.lock));
            const stopped = w.stopped(self.stop);
            if (!stopped) _ = c.ResetEvent(stream_stop);
            c.ReleaseSRWLockExclusive(@ptrCast(&self.host.lock));
            if (stopped) break;
            subscriptions.run(stream_stop, workspace, .{
                .opener = self.opener,
                .body = request,
                .retry_ms = points.backend_probe_interval_ms,
                .ready = self.ready,
            }, .{ .context = self, .frame = frame, .lost = lost });
            // run closes the channel before this workspace is used again.
        }
        lease.publish();
    }
    fn frame(raw: *anyopaque, value: subscriptions.Frame) void {
        const self: *Settings = @ptrCast(@alignCast(raw));
        if (!std.mem.eql(u8, value.id, settings_source.id)) return;
        if (subscriptions.rejection(value.value) != null) {
            self.host.clearOverlaySettings();
            self.host.push(.settings_rejected);
            return;
        }
        self.host.push(.{ .settings = subscriptions.systemIntegration(value.value) });
        self.host.storeOverlaySettings(value.value);
    }
    fn lost(raw: *anyopaque, cause: []const u8) void {
        const self: *Settings = @ptrCast(@alignCast(raw));
        self.host.clearOverlaySettings();
        self.host.pushText(cause);
        self.host.push(.settings_rejected);
    }
};

var host: Host = .{};
pub fn main() void {
    host.runProcess() catch |failure| {
        var buffer: [256:0]u8 = undefined;
        const text = std.mem.printSentinel(&buffer, "ResourceManager.Desktop: {s}", .{@errorName(failure)}, 0) catch unreachable;
        c.OutputDebugStringA(text);
        c.ExitProcess(1);
    };
}

test "Ledger settings cache eviction replays the same subscription and shutdown cancels its current stream" {
    const ledger_values = @import("ledger_values").Bound;
    if (ledger_values.table_0_idle_epochs == 0) return error.SkipZigTest;
    const mock = @import("backend/mock_backend.zig");
    const frame = "{\"subscriptionId\":\"settings\",\"value\":{\"systemIntegration\":{},\"performanceOverlay\":{\"software\":{\"already-running\":{\"enabled\":true}}}}}\n";
    const server = try mock.Server.start(.{ .script = &.{.{ .status = "200 OK", .stream = &.{frame}, .repeat = true }} });
    defer server.deinit();
    // The resident's static Host storage avoids a multi-MiB test stack copy.
    const subject = &host;
    subject.head = 0;
    subject.count = 0;
    try subject.ledger.init();
    defer subject.ledger.deinit() catch @panic("settings thread retained memory after join");
    subject.wake = c.CreateEventW(null, c.TRUE, c.FALSE, null) orelse return error.Event;
    defer {
        _ = c.CloseHandle(subject.wake);
        subject.wake = null;
    }
    const owner_stop = c.CreateEventW(null, c.TRUE, c.FALSE, null) orelse return error.Event;
    defer _ = c.CloseHandle(owner_stop);
    const ready = c.CreateEventW(null, c.TRUE, c.TRUE, null) orelse return error.Event;
    defer _ = c.CloseHandle(ready);
    var settings: Settings = .{ .host = subject, .stop = owner_stop, .ready = ready, .opener = server.opener() };
    const thread = c.CreateThread(null, points.thread_stack_bytes, Settings.entry, &settings, c.STACK_SIZE_PARAM_IS_A_RESERVATION, null) orelse return error.Thread;
    defer _ = c.CloseHandle(thread);
    defer {
        subject.stopSettings(owner_stop);
        _ = c.WaitForSingleObject(thread, c.INFINITE);
    }
    const Await = struct {
        fn frameReceived(target: *Host, out: []u8) !usize {
            const deadline = c.GetTickCount64() + 30000;
            while (true) {
                while (target.pop()) |event| switch (event) {
                    .overlay_settings => |enabled| if (enabled == true) {
                        const n = Host.overlaySettings(target, out);
                        if (n > 0) return n;
                    },
                    else => {},
                };
                const now = c.GetTickCount64();
                if (now >= deadline) return error.SettingsReplayTimeout;
                _ = c.WaitForSingleObject(target.wake, @intCast(deadline - now));
            }
        }
    };
    var output: [4096]u8 = undefined;
    const first_len = try Await.frameReceived(subject, &output);
    try std.testing.expect(std.mem.indexOf(u8, output[0..first_len], "already-running") != null);
    var tick: usize = 0;
    while (tick < ledger_values.table_0_idle_epochs + 2) : (tick += 1) _ = try subject.ledger.tick();
    try std.testing.expect((try subject.ledger.use(.overlay_settings_json)) == null);
    try std.testing.expectEqual(@as(usize, 0), Host.overlaySettings(subject, &output));
    const replay_len = try Await.frameReceived(subject, &output);
    try std.testing.expectEqualStrings("{\"software\":{\"already-running\":{\"enabled\":true}}}", output[0..replay_len]);
    try std.testing.expect(server.requestCount() >= 2);
    try std.testing.expectEqual(server.requestCount(), server.countLines("POST /api/subscriptions/stream HTTP/1.1"));
    var body: [512]u8 = undefined;
    try std.testing.expectEqualStrings("{\"version\":1,\"subscriptions\":[{\"id\":\"settings\",\"path\":\"/api/settings/app/subscribe\"}]}", server.copyLastBody(&body));
    subject.stopSettings(owner_stop);
    try std.testing.expectEqual(@as(c.DWORD, c.WAIT_OBJECT_0), c.WaitForSingleObject(thread, 5000));
    try std.testing.expect(subject.settings_stream_stop == null);
    var exit_code: c.DWORD = 1;
    try std.testing.expect(c.GetExitCodeThread(thread, &exit_code) != 0);
    try std.testing.expectEqual(@as(c.DWORD, 0), exit_code);
    // The subscription region is published and unleased once its thread exits.
    var workspace = (try subject.ledger.use(.backend_settings_subscription)).?;
    defer _ = workspace.endUse();
    try std.testing.expectEqual(memory.Status.completed, workspace.endUse());
}
