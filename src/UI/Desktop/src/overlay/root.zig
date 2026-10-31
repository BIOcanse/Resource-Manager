//! The performance overlay block (Overlay, under BackendConnection). Its
//! thread owns the presentations of the backend settings section
//! `performanceOverlay` (the host hands over what its settings subscription
//! last received; the block never reads a file or polls), the
//! targets the subscription stream names, and for each target either a
//! layered window over its client area or the injected section. The stream is
//! the shared runner (backend/subscriptions.zig) on one inner thread, which
//! hands each frame or loss to the block thread and waits until it is taken.
//! Window changes arrive as WinEvent hooks; there is no timer except the
//! declared stream retry delay. Everything the block opens, the inner thread
//! included, is released before `run` returns.
const std = @import("std");
const memory = @import("../memory.zig");
const points = @import("desktop_points_values").Bound;
const c = @import("../backend/c.zig").win;
const channel = @import("../backend/channel.zig");
const subscriptions = @import("../backend/subscriptions.zig");
const model = @import("model.zig");
const text = @import("text.zig");
const layout = @import("layout.zig");
pub const render = @import("render.zig");
const window = @import("window.zig");
const section = @import("section.zig");

/// Displays at once; further targets in a frame are left out and logged.
pub const maximum_targets = points.overlay_maximum_targets;
const maximum_round_targets = points.overlay_maximum_round_targets;
const store_bytes = points.overlay_store_bytes;
const parse_bytes = points.overlay_parse_bytes;
const presentation_bytes = points.overlay_presentation_bytes;
const module_bytes = points.overlay_module_bytes;
const supported_version = 3;
const request_bytes = points.overlay_request_bytes;
/// Parse arena for one settings section copy, as a multiple of its JSON bytes.
const settings_parse_factor = points.overlay_settings_parse_factor;

pub const Config = struct {
    /// Opens an attested channel: backend.Link in the product, a mock backend in tests.
    opener: channel.Opener,
    sample_interval_ms: u32,
    /// The runner's delay after a loss, and the block's after it ended the stream itself.
    retry_ms: u32,
    /// Reserved stack of the inner subscription thread.
    stack_bytes: usize,
    /// Largest `performanceOverlay` section JSON the block takes (the
    /// overlay_settings_bytes point in the product); `Host.settings` copies at most this.
    settings_bytes: usize,
    render: render.Options = .{},
    /// section.product_prefix in the product; tests pass their own name.
    section_prefix: []const u8 = section.product_prefix,
};

/// The host side of the block. Callbacks are called on the block thread.
pub const Host = struct {
    context: *anyopaque,
    log: *const fn (context: *anyopaque, message: []const u8) void,
    /// Copies the current UI language into `output`; returns its length.
    language: *const fn (context: *anyopaque, output: *[32]u8) usize,
    /// Copies the `performanceOverlay` section the settings subscription last
    /// delivered (JSON) into `output`; returns its length, 0 when unknown.
    /// A section longer than `output` is not copied: the host returns 0.
    settings: *const fn (context: *anyopaque, output: []u8) usize,
    /// The root window that counts as the foreground window.
    foreground: *const fn (context: *anyopaque) c.HWND = systemForeground,
};

pub fn systemForeground(_: *anyopaque) c.HWND {
    // RuntimeRead: EventDriven("EVENT_SYSTEM_FOREGROUND")
    return c.GetAncestor(c.GetForegroundWindow(), c.GA_ROOT);
}

const RenderKey = struct { content: u64, language: u64, width: i32, height: i32, revision: u64 };

const Display = struct {
    used: bool = false,
    process_id: u32 = 0,
    start_key: u64 = 0,
    content: u64 = 0,
    rendered: ?RenderKey = null,
    mode: model.Mode = .external,
    target_window: c.HWND = null,
    /// The external window's bounds and the image's offset in the client area.
    last_bounds: window.Bounds = .{},
    offset_x: i32 = 0,
    offset_y: i32 = 0,
    external: ?window.Window = null,
    injected: ?section.Section = null,
    process: c.HANDLE = null,
    hooks: [3]c.HWINEVENTHOOK = .{ null, null, null },

    fn resetOutput(self: *Display) void {
        if (self.external) |*value| value.destroy();
        self.external = null;
        if (self.injected) |*value| value.deinit();
        self.injected = null;
        self.rendered = null;
    }
    fn hide(self: *Display) void {
        if (self.external) |*value| value.hide();
        if (self.injected) |*value| value.disable();
    }
};

/// A stored frame: the whole NDJSON line of one subscription, in the order
/// the subscriptions first delivered a value.
const Frame = struct { subscription: usize, offset: usize, len: usize };

const RoundTarget = struct {
    target: *const model.Target,
    /// The item values of the subscription the target arrived on.
    items: []const model.ItemValue,
    /// The target's own presentation, or the frontend defaults when it has none.
    presentation: *const model.Presentation,
    enabled: bool,
    mode: model.Mode,
};
const Round = struct {
    items: [maximum_round_targets]RoundTarget = undefined,
    count: usize = 0,
    fn find(self: *const Round, process_id: u32, start_key: u64) ?*const RoundTarget {
        for (self.items[0..self.count]) |*item| {
            if (selected(item) and item.target.processId == process_id and item.target.processStartKey == start_key) return item;
        }
        return null;
    }
};
fn selected(item: *const RoundTarget) bool {
    return item.target.processId > 0 and item.target.processId <= std.math.maxInt(u32) and item.target.processStartKey > 0 and item.enabled;
}

const Subscribed = struct { signature: u64, with_modules: bool, overlay_count: usize };

/// One frame or loss from the inner subscription thread. Its slices point into
/// the runner's workspace and stay valid until the block sets `taken`.
const Handoff = union(enum) { frame: subscriptions.Frame, lost: []const u8 };

/// The inner subscription thread and its one-item handoff slot.
const Feed = struct {
    /// Manual reset: the runner's stop.
    stop: c.HANDLE = null,
    /// Auto reset: the runner set `slot`; the block waits on it.
    posted: c.HANDLE = null,
    /// Auto reset: the block is done with `slot`; the runner waits on it.
    taken: c.HANDLE = null,
    thread: c.HANDLE = null,
    opener: channel.Opener = undefined,
    retry_ms: u32 = 0,
    body: [request_bytes]u8 = undefined,
    body_len: usize = 0,
    slot: Handoff = .{ .lost = "" },
    runner: subscriptions.Workspace = undefined,

    /// Field by field: the Ledger region is too large for a stack temporary.
    fn open(self: *Feed) !void {
        self.stop = null;
        self.posted = null;
        self.taken = null;
        self.thread = null;
        self.slot = .{ .lost = "" };
        self.stop = c.CreateEventW(null, c.TRUE, c.FALSE, null) orelse return error.Event;
        self.posted = c.CreateEventW(null, c.FALSE, c.FALSE, null) orelse return error.Event;
        self.taken = c.CreateEventW(null, c.FALSE, c.FALSE, null) orelse return error.Event;
    }
    fn close(self: *Feed) void {
        self.join();
        for ([_]*c.HANDLE{ &self.stop, &self.posted, &self.taken }) |handle| if (handle.* != null) {
            _ = c.CloseHandle(handle.*);
            handle.* = null;
        };
    }
    /// Starts the runner on `body` (copied; the block rebuilds its request buffer).
    fn start(self: *Feed, body: []const u8, opener: channel.Opener, retry_ms: u32, stack_bytes: usize) !void {
        std.debug.assert(self.thread == null);
        if (body.len > self.body.len) return error.OverlayRequestTooLarge;
        @memcpy(self.body[0..body.len], body);
        self.body_len = body.len;
        self.opener = opener;
        self.retry_ms = retry_ms;
        self.runner.channel = .{};
        _ = c.ResetEvent(self.stop);
        _ = c.ResetEvent(self.posted);
        _ = c.ResetEvent(self.taken);
        self.thread = c.CreateThread(null, stack_bytes, entry, self, c.STACK_SIZE_PARAM_IS_A_RESERVATION, null) orelse return error.Thread;
    }
    /// Stops the runner and waits for it; the runner closes its channel before it returns.
    fn join(self: *Feed) void {
        const thread = self.thread orelse return;
        _ = c.SetEvent(self.stop);
        _ = c.WaitForSingleObject(thread, c.INFINITE);
        _ = c.CloseHandle(thread);
        self.thread = null;
    }
    fn entry(raw: ?*anyopaque) callconv(.winapi) c.DWORD {
        const self: *Feed = @ptrCast(@alignCast(raw.?));
        subscriptions.run(self.stop, &self.runner, .{ .opener = self.opener, .body = self.body[0..self.body_len], .retry_ms = self.retry_ms }, .{ .context = self, .frame = frame, .lost = lost });
        return 0;
    }
    fn frame(raw: *anyopaque, value: subscriptions.Frame) void {
        hand(@ptrCast(@alignCast(raw)), .{ .frame = value });
    }
    fn lost(raw: *anyopaque, reason: []const u8) void {
        hand(@ptrCast(@alignCast(raw)), .{ .lost = reason });
    }
    /// Runner thread: posts one item, then waits until the block took it or stop.
    fn hand(self: *Feed, item: Handoff) void {
        self.slot = item;
        _ = c.SetEvent(self.posted);
        const handles = [_]c.HANDLE{ self.stop, self.taken };
        _ = c.WaitForMultipleObjects(handles.len, &handles, c.FALSE, c.INFINITE);
    }
};

pub const Workspace = struct {
    feed: Feed,
    renderer: render.Renderer,
    lines: model.Lines,
    presentations: model.Presentations,
    modules: model.Modules,
    round: Round,
    displays: [maximum_targets]Display,
    frames: [model.maximum_overlays]Frame,
    order: [model.maximum_overlays]usize,
    request: [request_bytes]u8,
    presentation_storage: [presentation_bytes]u8,
    module_storage: [module_bytes]u8,
    store: [store_bytes]u8,
    parse: [parse_bytes]u8,
};

// Set by the WinEvent callback, read by the same thread after its message drain.
threadlocal var window_event_seen: bool = false;

fn windowEvent(_: c.HWINEVENTHOOK, _: c.DWORD, _: c.HWND, object: c.LONG, child: c.LONG, _: c.DWORD, _: c.DWORD) callconv(.winapi) void {
    // OBJID_WINDOW and CHILDID_SELF: the window itself, not its caret, cursor or parts.
    if (object == 0 and child == 0) window_event_seen = true;
}

const Block = struct {
    workspace: *Workspace,
    // The performanceOverlay section copy and its parse arena (Config.settings_bytes and its multiple).
    settings_json: []u8,
    settings_parse: []u8,
    host: Host,
    config: Config,
    stop: c.HANDLE,
    instance: c.HINSTANCE,
    format: text.NumberFormat = .{},
    language: [32]u8 = undefined,
    language_len: usize = 0,
    revision: u64 = 0,
    frame_count: usize = 0,
    store_used: usize = 0,
    latest_version: i64 = 0,
    connected: bool = false,
    subscribed: ?Subscribed = null,
    resubscribe: bool = false,
    foreground_hook: c.HWINEVENTHOOK = null,
    dropped_logged: bool = false,

    fn log(self: *Block, comptime format: []const u8, arguments: anytype) void {
        var buffer: [512]u8 = undefined;
        const message = std.fmt.bufPrint(&buffer, "overlay: " ++ format, arguments) catch buffer[0..];
        self.host.log(self.host.context, message);
    }

    fn languageSlice(self: *const Block) []const u8 {
        return self.language[0..self.language_len];
    }

    /// Takes the latest settings section and the UI language again.
    fn readSettings(self: *Block) void {
        const workspace = self.workspace;
        workspace.presentations.count = 0;
        const settings_length = @min(self.host.settings(self.host.context, self.settings_json), self.settings_json.len);
        if (settings_length != 0) {
            var arena = std.heap.FixedBufferAllocator.init(self.settings_parse);
            if (std.json.parseFromSliceLeaky(std.json.Value, arena.allocator(), self.settings_json[0..settings_length], .{})) |parsed| {
                workspace.presentations.load(parsed) catch |err| self.log("overlay settings rejected: {s}", .{@errorName(err)});
            } else |err| self.log("overlay settings unreadable: {s}", .{@errorName(err)});
        }
        var language: [32]u8 = undefined;
        const length = @min(self.host.language(self.host.context, &language), language.len);
        @memcpy(self.language[0..length], language[0..length]);
        self.language_len = length;
        self.revision += 1;
    }

    fn desired(self: *Block) !model.Request {
        return model.request(&self.workspace.presentations, &self.workspace.modules, self.config.sample_interval_ms, &self.workspace.order, &self.workspace.request);
    }

    fn sameSubscription(self: *const Block, request: model.Request) bool {
        const current = self.subscribed orelse return false;
        return current.signature == request.signature and current.with_modules == request.with_modules and current.overlay_count == request.overlay_count;
    }

    /// Connects when there is anything to subscribe to.
    fn connect(self: *Block) !void {
        const request = try self.desired();
        if (request.overlay_count > model.maximum_overlays) return error.TooManyOverlaySubscriptions;
        self.subscribed = .{ .signature = request.signature, .with_modules = request.with_modules, .overlay_count = request.overlay_count };
        self.frame_count = 0;
        self.store_used = 0;
        self.latest_version = 0;
        if (request.overlay_count == 0 and !request.with_modules) return;
        try self.workspace.feed.start(request.body, self.config.opener, self.config.retry_ms, self.config.stack_bytes);
        self.connected = true;
    }

    fn disconnect(self: *Block) void {
        if (self.connected) self.workspace.feed.join();
        self.connected = false;
        self.subscribed = null;
        self.frame_count = 0;
        self.store_used = 0;
    }

    /// One frame from the runner, already split into its id and value.
    fn takeFrame(self: *Block, received: subscriptions.Frame) !void {
        var arena = std.heap.FixedBufferAllocator.init(&self.workspace.parse);
        const allocator = arena.allocator();
        const id = received.id;
        const value = received.value;
        const current = self.subscribed orelse return;
        if (current.with_modules and std.mem.eql(u8, id, model.modules_id)) {
            const was_known = self.workspace.modules.known;
            const changed = if (std.json.parseFromValueLeaky(model.Modules.Document, allocator, value, .{ .ignore_unknown_fields = true })) |document|
                try self.workspace.modules.load(document)
            else |err| blk: {
                // A rejected or unreadable module settings value: the selection is unknown.
                self.log("startup module settings unavailable: {s}", .{@errorName(err)});
                self.workspace.modules.clear();
                break :blk was_known;
            };
            if (changed) {
                const request = try self.desired();
                if (!self.sameSubscription(request)) self.resubscribe = true;
            }
            return;
        }
        const index = overlayIndex(id) orelse return;
        if (index >= current.overlay_count) return;
        if (value == .null) return;
        // An undeserializable value ends the stream, as the window process's reader does.
        const snapshot = try std.json.parseFromValueLeaky(model.Snapshot, allocator, value, .{ .ignore_unknown_fields = true });
        try self.storeFrame(index, received.line);
        self.latest_version = snapshot.version;
        try self.apply();
    }

    fn storeFrame(self: *Block, subscription: usize, bytes: []const u8) !void {
        const frames = &self.workspace.frames;
        var existing: ?usize = null;
        for (frames[0..self.frame_count], 0..) |frame, index| {
            if (frame.subscription == subscription) existing = index;
        }
        if (existing) |index| {
            const old = frames[index];
            const tail = self.workspace.store[old.offset + old.len .. self.store_used];
            std.mem.copyForwards(u8, self.workspace.store[old.offset..][0..tail.len], tail);
            self.store_used -= old.len;
            for (frames[0..self.frame_count]) |*frame| {
                if (frame.offset > old.offset) frame.offset -= old.len;
            }
        }
        if (self.store_used + bytes.len > self.workspace.store.len) return error.OverlayFrameStoreFull;
        @memcpy(self.workspace.store[self.store_used..][0..bytes.len], bytes);
        const frame: Frame = .{ .subscription = subscription, .offset = self.store_used, .len = bytes.len };
        self.store_used += bytes.len;
        if (existing) |index| frames[index] = frame else {
            frames[self.frame_count] = frame;
            self.frame_count += 1;
        }
    }

    /// The union of the stored frames' targets, each with its subscription's
    /// item values and its presentation's settings. Valid until the next round.
    fn buildRound(self: *Block, arena: *std.heap.FixedBufferAllocator) !void {
        const round = &self.workspace.round;
        round.count = 0;
        const Envelope = struct { subscriptionId: []const u8, value: model.Snapshot };
        var dropped = false;
        for (self.workspace.frames[0..self.frame_count]) |frame| {
            const bytes = self.workspace.store[frame.offset..][0..frame.len];
            const envelope = try std.json.parseFromSliceLeaky(Envelope, arena.allocator(), bytes, .{ .ignore_unknown_fields = true });
            for (envelope.value.targets) |*target| {
                if (round.count == round.items.len) {
                    dropped = true;
                    break;
                }
                // Subscriptions exist only for stored entries; a target whose entry
                // went away before the stream was rebuilt is not drawn.
                const presentation = self.workspace.presentations.find(target.softwareId) orelse continue;
                round.items[round.count] = .{
                    .target = target,
                    .items = envelope.value.items,
                    .presentation = presentation,
                    .enabled = presentation.enabled,
                    .mode = presentation.mode,
                };
                round.count += 1;
            }
        }
        if (dropped) self.log("more than {d} overlay targets in one round; the rest are left out", .{maximum_round_targets});
    }

    /// ApplySnapshot: removes absent targets, adds new ones, takes content changes.
    fn apply(self: *Block) !void {
        if (self.latest_version != supported_version) {
            self.clear();
            return;
        }
        var arena = std.heap.FixedBufferAllocator.init(&self.workspace.parse);
        try self.buildRound(&arena);
        const round = &self.workspace.round;
        for (&self.workspace.displays) |*display| {
            if (display.used and round.find(display.process_id, display.start_key) == null) self.remove(display);
        }
        for (round.items[0..round.count], 0..) |*item, index| {
            if (!selected(item)) continue;
            const process_id: u32 = @intCast(item.target.processId);
            // The first target per process wins.
            var first = true;
            for (round.items[0..index]) |*earlier| {
                if (selected(earlier) and earlier.target.processId == item.target.processId and earlier.target.processStartKey == item.target.processStartKey) first = false;
            }
            if (!first) continue;
            const content = contentSignature(item);
            if (self.findDisplay(process_id, item.target.processStartKey)) |display| {
                if (content != display.content) {
                    const previous = display.mode;
                    display.content = content;
                    display.rendered = null;
                    // A process first seen in external mode cannot acquire the module until its next launch.
                    if (item.mode == .external) display.mode = .external else if (previous == .injected or display.injected != null) display.mode = .injected;
                }
            } else self.add(process_id, item.target.processStartKey, content, item.mode);
        }
        self.refreshRound();
    }

    fn findDisplay(self: *Block, process_id: u32, start_key: u64) ?*Display {
        for (&self.workspace.displays) |*display| {
            if (display.used and display.process_id == process_id and display.start_key == start_key) return display;
        }
        return null;
    }

    fn displayCount(self: *const Block) usize {
        var count: usize = 0;
        for (&self.workspace.displays) |*display| count += @intFromBool(display.used);
        return count;
    }

    fn add(self: *Block, process_id: u32, start_key: u64, content: u64, mode: model.Mode) void {
        const slot = for (&self.workspace.displays) |*display| {
            if (!display.used) break display;
        } else {
            if (!self.dropped_logged) self.log("more than {d} overlay targets; the rest are not shown", .{maximum_targets});
            self.dropped_logged = true;
            return;
        };
        // A process that cannot be opened or started at another time has exited.
        const process = c.OpenProcess(c.SYNCHRONIZE | c.PROCESS_QUERY_LIMITED_INFORMATION, c.FALSE, process_id) orelse return;
        if (!processMatches(process, start_key)) {
            _ = c.CloseHandle(process);
            return;
        }
        var hooks: [3]c.HWINEVENTHOOK = .{ null, null, null };
        const ranges = [_][2]c.DWORD{ .{ c.EVENT_OBJECT_CREATE, c.EVENT_OBJECT_HIDE }, .{ c.EVENT_OBJECT_LOCATIONCHANGE, c.EVENT_OBJECT_LOCATIONCHANGE }, .{ c.EVENT_SYSTEM_MINIMIZESTART, c.EVENT_SYSTEM_MINIMIZEEND } };
        for (ranges, 0..) |range, index| {
            hooks[index] = c.SetWinEventHook(range[0], range[1], null, windowEvent, process_id, 0, c.WINEVENT_OUTOFCONTEXT);
            if (hooks[index] == null) {
                for (hooks[0..index]) |hook| _ = c.UnhookWinEvent(hook);
                _ = c.CloseHandle(process);
                self.log("window events unavailable for process {d}", .{process_id});
                return;
            }
        }
        if (self.foreground_hook == null) {
            self.foreground_hook = c.SetWinEventHook(c.EVENT_SYSTEM_FOREGROUND, c.EVENT_SYSTEM_FOREGROUND, null, windowEvent, 0, 0, c.WINEVENT_OUTOFCONTEXT);
            if (self.foreground_hook == null) self.log("foreground events unavailable", .{});
        }
        slot.* = .{ .used = true, .process_id = process_id, .start_key = start_key, .content = content, .mode = mode, .process = process, .hooks = hooks };
        self.dropped_logged = false;
    }

    fn remove(self: *Block, display: *Display) void {
        display.resetOutput();
        for (display.hooks) |hook| {
            if (hook != null) _ = c.UnhookWinEvent(hook);
        }
        if (display.process != null) _ = c.CloseHandle(display.process);
        display.* = .{};
        if (self.displayCount() == 0) self.releaseShared();
    }

    /// Without displays, the foreground hook and the drawing device go too.
    fn releaseShared(self: *Block) void {
        if (self.foreground_hook != null) _ = c.UnhookWinEvent(self.foreground_hook);
        self.foreground_hook = null;
        self.workspace.renderer.deinit();
    }

    fn clear(self: *Block) void {
        for (&self.workspace.displays) |*display| {
            if (display.used) self.remove(display);
        }
        self.releaseShared();
    }

    /// Refresh with a fresh round of the stored frames.
    fn refresh(self: *Block) void {
        if (self.displayCount() == 0) return;
        if (self.latest_version != supported_version) {
            self.clear();
            return;
        }
        var arena = std.heap.FixedBufferAllocator.init(&self.workspace.parse);
        self.buildRound(&arena) catch |err| {
            self.log("stored frames unreadable: {s}", .{@errorName(err)});
            self.clear();
            return;
        };
        self.refreshRound();
    }

    fn refreshRound(self: *Block) void {
        for (&self.workspace.displays) |*display| {
            if (!display.used) continue;
            self.refreshDisplay(display) catch |err| {
                self.log("target {d}: {s}", .{ display.process_id, @errorName(err) });
                display.resetOutput();
            };
        }
    }

    fn refreshDisplay(self: *Block, display: *Display) !void {
        const item = self.workspace.round.find(display.process_id, display.start_key) orelse {
            self.remove(display);
            return;
        };
        const state = readWindow(display, self.host);
        if (state.exited) {
            self.remove(display);
            return;
        }
        if (state.handle != display.target_window) {
            display.target_window = state.handle;
            display.rendered = null;
            display.hide();
            if (display.injected) |*value| value.setTargetWindow(@intFromPtr(state.handle));
        }
        if (state.handle == null or !state.visible) {
            display.hide();
            display.rendered = null;
            return;
        }
        if (display.mode == .injected) {
            if (display.external) |*value| value.destroy();
            display.external = null;
            if (display.injected == null) display.injected = try section.Section.create(self.config.section_prefix, display.process_id, display.start_key, @intFromPtr(state.handle));
            display.injected.?.setTargetWindow(@intFromPtr(state.handle));
            const size = display.injected.?.swapChainSize();
            if (size.width <= 0 or size.height <= 0) return;
            try self.renderIfNeeded(display, item, size.width, size.height, state.client, true);
        } else {
            // A loaded module's section stays disabled so a later switch back can reuse it.
            if (display.injected) |*value| value.disable();
            if (!state.foreground) {
                if (display.external) |*value| value.hide();
                return;
            }
            if (display.external == null) display.external = try window.Window.create(self.instance);
            try self.renderIfNeeded(display, item, state.client.width, state.client.height, state.client, false);
        }
    }

    fn renderIfNeeded(self: *Block, display: *Display, item: *const RoundTarget, width: i32, height: i32, client: window.Bounds, injected: bool) !void {
        const presentation = item.presentation;
        const key: RenderKey = .{ .content = display.content, .language = std.hash.Wyhash.hash(0, self.languageSlice()), .width = width, .height = height, .revision = self.revision };
        if (display.rendered == null or !std.meta.eql(display.rendered.?, key)) {
            const lines = &self.workspace.lines;
            try lines.build(item.items, presentation, self.languageSlice(), &self.format);
            const image = try self.workspace.renderer.render(lines.slice(), width, height, presentation.settings, self.languageSlice(), !injected) orelse {
                display.hide();
                return;
            };
            if (injected) {
                try display.injected.?.publish(image.pixels, image.width, image.height, image.stride);
            } else {
                const bounds: window.Bounds = .{ .x = client.x + image.x, .y = client.y + image.y, .width = image.width, .height = image.height };
                try display.external.?.update(self.workspace.renderer.deviceContext(), bounds);
                display.last_bounds = bounds;
                display.offset_x = image.x;
                display.offset_y = image.y;
            }
            display.rendered = key;
        } else if (!injected) {
            const bounds: window.Bounds = .{ .x = client.x + display.offset_x, .y = client.y + display.offset_y, .width = display.last_bounds.width, .height = display.last_bounds.height };
            if (!std.meta.eql(bounds, display.last_bounds) or !display.external.?.shown) {
                try display.external.?.move(bounds);
                display.last_bounds = bounds;
            }
        }
    }

    fn drainMessages(self: *Block) void {
        _ = self;
        var message: c.MSG = undefined;
        while (c.PeekMessageW(&message, null, 0, 0, c.PM_REMOVE) != 0) {
            _ = c.TranslateMessage(&message);
            _ = c.DispatchMessageW(&message);
        }
    }

    /// The block thread's side of one handoff. A frame the block cannot take
    /// ends the stream, as the window process's reader does: the runner is
    /// joined and the block subscribes again after the retry delay. A loss is
    /// retried by the runner itself; the block only drops what it showed, and
    /// subscribes again after the delay when the module selection it no longer
    /// knows changes the request.
    fn take(self: *Block, retry_at: *?u64) void {
        switch (self.workspace.feed.slot) {
            .frame => |received| self.takeFrame(received) catch |err| {
                self.log("subscription stream ended: {s}", .{@errorName(err)});
                self.disconnect();
                self.clear();
                self.workspace.modules.clear();
                // RuntimeRead: Fixed
                retry_at.* = c.GetTickCount64() + self.config.retry_ms;
                return;
            },
            .lost => |reason| {
                self.log("subscription stream ended: {s}", .{reason});
                self.clear();
                self.frame_count = 0;
                self.store_used = 0;
                self.latest_version = 0;
                self.workspace.modules.clear();
                const request = self.desired() catch null;
                if (request == null or !self.sameSubscription(request.?)) {
                    self.disconnect();
                    // RuntimeRead: Fixed
                    retry_at.* = c.GetTickCount64() + self.config.retry_ms;
                    return;
                }
            },
        }
        if (self.resubscribe) {
            self.resubscribe = false;
            self.disconnect();
            self.clear();
            return;
        }
        _ = c.SetEvent(self.workspace.feed.taken);
    }

    fn loop(self: *Block, settings_event: c.HANDLE) !void {
        self.readSettings();
        var retry_at: ?u64 = null;
        var handles: [3 + maximum_targets]c.HANDLE = undefined;
        while (true) {
            if (!self.connected and retry_at == null and self.subscribed == null) {
                self.connect() catch |err| {
                    self.log("subscription stream unavailable: {s}", .{@errorName(err)});
                    self.disconnect();
                    self.clear();
                    self.workspace.modules.clear();
                    // RuntimeRead: Fixed
                    retry_at = c.GetTickCount64() + self.config.retry_ms;
                };
            }
            handles[0] = self.stop;
            handles[1] = settings_event;
            var count: usize = 2;
            const stream_index: ?usize = if (self.connected) blk: {
                handles[count] = self.workspace.feed.posted;
                count += 1;
                break :blk count - 1;
            } else null;
            const process_start = count;
            for (&self.workspace.displays) |*display| {
                if (!display.used) continue;
                handles[count] = display.process;
                count += 1;
            }
            var timeout: c.DWORD = c.INFINITE;
            if (retry_at) |at| {
                // RuntimeRead: Fixed
                const now = c.GetTickCount64();
                timeout = if (now >= at) 0 else @intCast(@min(at - now, c.INFINITE - 1));
            }
            const result = c.MsgWaitForMultipleObjectsEx(@intCast(count), &handles, timeout, c.QS_ALLINPUT, c.MWMO_INPUTAVAILABLE);
            if (result == c.WAIT_FAILED) return error.OverlayWaitFailed;
            if (result == c.WAIT_OBJECT_0) return;
            window_event_seen = false;
            var refresh_needed = false;
            if (result == c.WAIT_TIMEOUT) {
                retry_at = null;
                self.subscribed = null;
            } else if (result == c.WAIT_OBJECT_0 + 1) {
                self.readSettings();
                const request = self.desired() catch |err| blk: {
                    self.log("subscription request not built: {s}", .{@errorName(err)});
                    break :blk null;
                };
                if ((request == null or !self.sameSubscription(request.?)) and retry_at == null) {
                    self.disconnect();
                    self.clear();
                } else if (self.connected) {
                    self.apply() catch |err| self.log("targets not applied: {s}", .{@errorName(err)});
                }
                // While a retry is pending, the next attempt subscribes to the new set.
            } else if (stream_index != null and result == c.WAIT_OBJECT_0 + stream_index.?) {
                self.take(&retry_at);
            } else if (result >= c.WAIT_OBJECT_0 + process_start and result < c.WAIT_OBJECT_0 + count) {
                refresh_needed = true;
            }
            self.drainMessages();
            if (refresh_needed or window_event_seen) self.refresh();
        }
    }
};

fn overlayIndex(id: []const u8) ?usize {
    const prefix = "overlay-";
    if (!std.mem.startsWith(u8, id, prefix) or id.len == prefix.len) return null;
    return std.fmt.parseInt(usize, id[prefix.len..], 10) catch null;
}

fn processMatches(process: c.HANDLE, start_key: u64) bool {
    // RuntimeRead: EventDriven("process-exit")
    if (c.WaitForSingleObject(process, 0) != c.WAIT_TIMEOUT) return false;
    var created: c.FILETIME = undefined;
    var exited: c.FILETIME = undefined;
    var kernel: c.FILETIME = undefined;
    var user: c.FILETIME = undefined;
    if (c.GetProcessTimes(process, &created, &exited, &kernel, &user) == 0) return false;
    return ((@as(u64, created.dwHighDateTime) << 32) | created.dwLowDateTime) == start_key;
}

const WindowState = struct { handle: c.HWND = null, client: window.Bounds = .{}, visible: bool = false, foreground: bool = false, exited: bool = false };

/// OverlayTargetWindow.Read: the previous window while it is still a
/// candidate, else the process's largest visible unowned top-level window.
fn readWindow(display: *const Display, host: Host) WindowState {
    // RuntimeRead: EventDriven("WinEvent")
    if (!processMatches(display.process, display.start_key)) return .{ .exited = true };
    const handle = if (candidate(display.target_window, display.process_id)) display.target_window else mainWindow(display.process_id);
    if (handle == null) return .{};
    const client = clientBounds(handle);
    return .{
        .handle = handle,
        .client = client,
        .visible = c.IsWindowVisible(handle) != 0 and c.IsIconic(handle) == 0 and client.width > 0 and client.height > 0,
        .foreground = host.foreground(host.context) == handle,
    };
}

fn candidate(handle: c.HWND, process_id: u32) bool {
    if (handle == null or c.IsWindow(handle) == 0 or c.IsWindowVisible(handle) == 0 or c.GetWindow(handle, c.GW_OWNER) != null) return false;
    var owner: c.DWORD = 0;
    _ = c.GetWindowThreadProcessId(handle, &owner);
    return owner == process_id;
}

const Search = struct { process_id: u32, handle: c.HWND = null, area: i64 = 0 };

fn visit(handle: c.HWND, parameter: c.LPARAM) callconv(.winapi) c.BOOL {
    const search: *Search = @ptrFromInt(@as(usize, @bitCast(parameter)));
    if (!candidate(handle, search.process_id)) return c.TRUE;
    const bounds = clientBounds(handle);
    const area = @as(i64, bounds.width) * bounds.height;
    if (area > search.area) {
        search.handle = handle;
        search.area = area;
    }
    return c.TRUE;
}

fn mainWindow(process_id: u32) c.HWND {
    var search: Search = .{ .process_id = process_id };
    _ = c.EnumWindows(visit, @bitCast(@intFromPtr(&search)));
    return search.handle;
}

fn clientBounds(handle: c.HWND) window.Bounds {
    var rect: c.RECT = undefined;
    if (c.GetClientRect(handle, &rect) == 0) return .{};
    var origin: c.POINT = .{ .x = 0, .y = 0 };
    if (c.ClientToScreen(handle, &origin) == 0) return .{};
    return .{ .x = origin.x, .y = origin.y, .width = @max(0, rect.right - rect.left), .height = @max(0, rect.bottom - rect.top) };
}

/// ContentSignature: the target's settings and its subscription's item values,
/// as far as a line shows them (kind, id, unit, value, state, reason).
fn contentSignature(item: *const RoundTarget) u64 {
    var hasher = std.hash.Wyhash.init(0);
    const target = item.target;
    hashText(&hasher, target.softwareId);
    hasher.update(&.{ @intFromBool(item.enabled), @backingInt(item.mode) });
    hashInteger(&hasher, item.items.len);
    for (item.items) |value| {
        hashText(&hasher, value.kind);
        hashText(&hasher, value.id);
        hasher.update(&.{@intFromBool(value.unit != null)});
        if (value.unit) |unit| hashText(&hasher, unit);
        hashNumber(&hasher, value.value);
        hashText(&hasher, value.state);
        hasher.update(&.{@intFromBool(value.reason != null)});
        if (value.reason) |reason| hashText(&hasher, reason);
    }
    return hasher.final();
}
fn hashText(hasher: *std.hash.Wyhash, value: []const u8) void {
    hashInteger(hasher, value.len);
    hasher.update(value);
}
fn hashInteger(hasher: *std.hash.Wyhash, value: u64) void {
    hasher.update(std.mem.asBytes(&value));
}
fn hashNumber(hasher: *std.hash.Wyhash, value: ?f64) void {
    hasher.update(&.{@intFromBool(value != null)});
    if (value) |number| hashInteger(hasher, @bitCast(number));
}

/// The user's decimal separator and negative sign (CultureInfo.CurrentCulture).
fn numberFormat() text.NumberFormat {
    var result: text.NumberFormat = .{};
    var wide: [16]u16 = undefined;
    // RuntimeRead: Fixed
    const decimal = c.GetLocaleInfoEx(null, c.LOCALE_SDECIMAL, &wide, wide.len);
    if (decimal > 1) {
        if (std.unicode.utf16LeToUtf8(&result.decimal, wide[0..@intCast(decimal - 1)])) |length| result.decimal_len = length else |_| {}
    }
    // RuntimeRead: Fixed
    const negative = c.GetLocaleInfoEx(null, c.LOCALE_SNEGATIVESIGN, &wide, wide.len);
    if (negative > 1) {
        if (std.unicode.utf16LeToUtf8(&result.negative, wide[0..@intCast(negative - 1)])) |length| result.negative_len = length else |_| {}
    }
    return result;
}

/// The block entry: returns when `stop` is signaled, with every window, hook,
/// section, device and socket released. `settings_event` is an auto-reset
/// event the host sets when the settings section or the language changed.
pub fn run(owner: *memory.Owner, stop: c.HANDLE, settings_event: c.HANDLE, host: Host, config: Config) !void {
    if (config.sample_interval_ms == 0 or config.retry_ms == 0 or config.retry_ms >= c.INFINITE or config.stack_bytes == 0 or config.settings_bytes == 0) return error.InvalidOverlayConfig;
    // One stable workspace, including the selected settings-copy/parse tail.
    const settings_total = std.math.mul(usize, config.settings_bytes, 1 + settings_parse_factor) catch return error.InvalidOverlayConfig;
    const total = std.math.add(usize, @sizeOf(Workspace), settings_total) catch return error.InvalidOverlayConfig;
    var lease = try owner.acquire(.overlay_block, total);
    defer _ = lease.endUse();
    const settings_bytes = lease.bytes()[@sizeOf(Workspace)..];
    const workspace = lease.as(Workspace);
    defer workspace.feed.close();
    try workspace.feed.open();
    workspace.renderer.init(config.render);
    defer workspace.renderer.deinit();
    workspace.lines.count = 0;
    workspace.lines.used = 0;
    workspace.presentations = model.Presentations.init(&workspace.presentation_storage);
    workspace.modules = model.Modules.init(&workspace.module_storage);
    workspace.round.count = 0;
    for (&workspace.displays) |*display| display.* = .{};
    // Per-monitor client coordinates, as the window process used.
    const previous_awareness = c.SetThreadDpiAwarenessContext(@ptrFromInt(@as(usize, @bitCast(@as(isize, -4)))));
    defer _ = c.SetThreadDpiAwarenessContext(previous_awareness);
    const instance: c.HINSTANCE = @ptrCast(c.GetModuleHandleW(null));
    try window.register(instance);
    defer window.unregister(instance);
    var block: Block = .{ .workspace = workspace, .settings_json = settings_bytes[0..config.settings_bytes], .settings_parse = settings_bytes[config.settings_bytes..settings_total], .host = host, .config = config, .stop = stop, .instance = instance, .format = numberFormat() };
    defer {
        block.disconnect();
        block.clear();
    }
    try block.loop(settings_event);
    lease.publish();
}

test "overlay subscription ids map to their index" {
    try std.testing.expectEqual(@as(?usize, 3), overlayIndex("overlay-3"));
    try std.testing.expectEqual(@as(?usize, null), overlayIndex("overlay-"));
    try std.testing.expectEqual(@as(?usize, null), overlayIndex("modules"));
}
