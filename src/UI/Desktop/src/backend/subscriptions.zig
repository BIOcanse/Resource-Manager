//! The unified subscription stream (POST /api/subscriptions/stream) as one
//! named holder per call: open an attested channel, subscribe to exactly the
//! listed sources, hand every frame to the owner, and on loss report it and
//! retry after the declared backend probe interval. Nothing runs between frames.
const std = @import("std");
const points = @import("desktop_points_values").Bound;
const c = @import("c.zig").win;
const channel_module = @import("channel.zig");
const Channel = channel_module.Channel;
const Definition = @import("../hotkeys/definition.zig").Definition;

pub const stream_path = "/api/subscriptions/stream";

pub const Source = struct { id: []const u8, path: []const u8 };
/// `line` is the whole NDJSON frame `id` and `value` were parsed from, for an
/// owner that keeps frames as received (the overlay's frame store).
pub const Frame = struct { id: []const u8, value: std.json.Value, line: []const u8 };

/// Writes {"version":1,"subscriptions":[{"id":...,"path":...},...]}.
pub fn requestBody(out: []u8, sources: []const Source) ![]const u8 {
    var writer = std.Io.Writer.fixed(out);
    try writer.writeAll("{\"version\":1,\"subscriptions\":[");
    for (sources, 0..) |source, index| {
        if (index != 0) try writer.writeByte(',');
        try writer.writeAll("{\"id\":");
        try channel_module.jsonString(&writer, source.id);
        try writer.writeAll(",\"path\":");
        try channel_module.jsonString(&writer, source.path);
        try writer.writeByte('}');
    }
    try writer.writeAll("]}");
    return writer.buffered();
}

pub fn parseFrame(allocator: std.mem.Allocator, line: []const u8) !Frame {
    const root = std.json.parseFromSliceLeaky(std.json.Value, allocator, line, .{ .duplicate_field_behavior = .use_last }) catch return error.InvalidFrame;
    if (root != .object) return error.InvalidFrame;
    const id = root.object.get("subscriptionId") orelse return error.InvalidFrame;
    const value = root.object.get("value") orelse return error.InvalidFrame;
    if (id != .string) return error.InvalidFrame;
    return .{ .id = id.string, .value = value, .line = line };
}

/// A rejected subscription is a value carrying `statusCode` (401/403 denied,
/// 400 unsupported selector, 503 policy missing).
pub fn rejection(value: std.json.Value) ?i64 {
    if (value != .object) return null;
    const status = value.object.get("statusCode") orelse return null;
    return if (status == .integer) status.integer else null;
}

pub const Handler = struct {
    context: *anyopaque,
    /// Borrowed for the call; the arena is reset after it returns.
    frame: *const fn (context: *anyopaque, frame: Frame) void,
    lost: *const fn (context: *anyopaque, reason: []const u8) void,
};
pub const Config = struct {
    opener: channel_module.Opener,
    body: []const u8,
    retry_ms: u32,
    /// Optional manual-reset fact handle (backend session ready). Attempts
    /// wait for it, so no connection is tried while the backend is known down.
    ready: c.HANDLE = null,
};
pub const Workspace = struct {
    channel: Channel = .{},
    line: [points.subscription_line_bytes]u8 = undefined,
    arena: [points.subscription_parse_bytes]u8 align(16) = undefined,
};

/// Runs until `stop`. Workspace is caller-owned fixed memory.
pub fn run(stop: c.HANDLE, workspace: *Workspace, config: Config, handler: Handler) void {
    defer workspace.channel.close();
    var round: u64 = 0;
    while (c.WaitForSingleObject(stop, 0) == c.WAIT_TIMEOUT) : (round += 1) {
        if (config.ready != null) {
            const handles = [_]c.HANDLE{ stop, config.ready };
            if (c.WaitForMultipleObjects(handles.len, &handles, c.FALSE, c.INFINITE) != c.WAIT_OBJECT_0 + 1) return;
        }
        const reason = attempt(stop, workspace, config, handler);
        workspace.channel.close();
        if (c.WaitForSingleObject(stop, 0) != c.WAIT_TIMEOUT) return;
        handler.lost(handler.context, reason);
        if (c.WaitForSingleObject(stop, config.retry_ms) != c.WAIT_TIMEOUT) return;
    }
}

fn attempt(stop: c.HANDLE, workspace: *Workspace, config: Config, handler: Handler) []const u8 {
    config.opener.open(&workspace.channel, stop) catch |err| return @errorName(err);
    workspace.channel.openStream(stream_path, config.body) catch |err| return @errorName(err);
    while (true) {
        const line = (workspace.channel.nextLine(&workspace.line) catch |err| return @errorName(err)) orelse return "stream-ended";
        var arena = std.heap.FixedBufferAllocator.init(&workspace.arena);
        const frame = parseFrame(arena.allocator(), line) catch |err| return @errorName(err);
        handler.frame(handler.context, frame);
    }
}

/// System integration settings read from AppSettings (backend need 1).
pub const SystemIntegration = struct {
    task_manager_enabled: bool = false,
    /// The force-terminate entry's enabled flag, already false for an empty key set.
    hotkey_enabled: bool = false,
    hotkey: Definition = .{},
    /// The toggle-performance-overlay-foreground entry, read the same way.
    overlay_toggle_enabled: bool = false,
    overlay_toggle: Definition = .{},
};
pub const force_terminate_action = "force-terminate-unresponsive-and-foreground";
pub const overlay_toggle_action = "toggle-performance-overlay-foreground";

/// Same reading as the C# controllers: a missing section or entry means off.
/// The first entry of each action id counts, as the backend normalizer keeps it.
pub fn systemIntegration(settings: std.json.Value) SystemIntegration {
    var value: SystemIntegration = .{};
    if (settings != .object) return value;
    const section = settings.object.get("systemIntegration") orelse return value;
    if (section != .object) return value;
    if (section.object.get("taskManagerShortcutReplacementEnabled")) |enabled| value.task_manager_enabled = enabled == .bool and enabled.bool;
    const hotkeys = section.object.get("hotkeys") orelse return value;
    if (hotkeys != .array) return value;
    var seen_terminate = false;
    var seen_toggle = false;
    for (hotkeys.array.items) |hotkey| {
        if (hotkey != .object) continue;
        const action = hotkey.object.get("actionId") orelse continue;
        if (action != .string) continue;
        const terminate = std.mem.eql(u8, action.string, force_terminate_action);
        const toggle = std.mem.eql(u8, action.string, overlay_toggle_action);
        if (!(terminate and !seen_terminate) and !(toggle and !seen_toggle)) continue;
        const enabled_value = hotkey.object.get("enabled");
        const enabled = enabled_value != null and enabled_value.? == .bool and enabled_value.?.bool;
        var encoding: [@import("../hotkeys/definition.zig").encoding_length]i32 = undefined;
        var count: usize = 0;
        if (hotkey.object.get("encoding")) |items| if (items == .array) for (items.array.items) |item| {
            if (count == encoding.len) break;
            if (item == .integer and item.integer >= std.math.minInt(i32) and item.integer <= std.math.maxInt(i32)) {
                encoding[count] = @intCast(item.integer);
                count += 1;
            }
        };
        const binding = Definition.create(encoding[0..count]);
        if (terminate) {
            seen_terminate = true;
            value.hotkey = binding;
            value.hotkey_enabled = enabled and !binding.isEmpty();
        } else {
            seen_toggle = true;
            value.overlay_toggle = binding;
            value.overlay_toggle_enabled = enabled and !binding.isEmpty();
        }
    }
    return value;
}

/// The backend settings section the overlay draws from (`performanceOverlay`).
/// `enabled_any` is the OverlayEnabled fact: at least one software entry has
/// `enabled` true; null when the section is missing or not an object.
pub const OverlaySection = struct { enabled_any: ?bool, json: []const u8 };

/// Writes the settings' `performanceOverlay` member as JSON into `out`. The
/// overlay block parses it again on its own thread; the backend normalizer has
/// already expanded every entry, so nothing here fills a value.
pub fn overlaySection(settings: std.json.Value, out: []u8) !OverlaySection {
    if (settings != .object) return .{ .enabled_any = null, .json = "" };
    const section = settings.object.get("performanceOverlay") orelse return .{ .enabled_any = null, .json = "" };
    if (section != .object) return .{ .enabled_any = null, .json = "" };
    var any = false;
    if (section.object.get("software")) |software| if (software == .object) for (software.object.values()) |entry| {
        if (entry != .object) continue;
        const enabled = entry.object.get("enabled") orelse continue;
        any = any or (enabled == .bool and enabled.bool);
    };
    var writer = std.Io.Writer.fixed(out);
    std.json.Stringify.value(section, .{}, &writer) catch return error.OverlaySettingsTooLarge;
    return .{ .enabled_any = any, .json = writer.buffered() };
}

test "request body lists exactly the named sources" {
    var buffer: [256]u8 = undefined;
    const body = try requestBody(&buffer, &.{ .{ .id = "settings", .path = "/api/settings/app/subscribe" }, .{ .id = "n", .path = "/x" } });
    try std.testing.expectEqualStrings("{\"version\":1,\"subscriptions\":[{\"id\":\"settings\",\"path\":\"/api/settings/app/subscribe\"},{\"id\":\"n\",\"path\":\"/x\"}]}", body);
}

test "frames, rejections and system integration settings" {
    var storage: [64 * 1024]u8 = undefined;
    var arena = std.heap.FixedBufferAllocator.init(&storage);
    const frame = try parseFrame(arena.allocator(),
        \\{"subscriptionId":"settings","value":{"systemIntegration":{"taskManagerShortcutReplacementEnabled":true,"hotkeys":[{"actionId":"other","enabled":true,"encoding":[17,0,65]},{"actionId":"force-terminate-unresponsive-and-foreground","enabled":true,"encoding":[17,1,18,0,"x",0,115]}]}}}
    );
    try std.testing.expectEqualStrings("settings", frame.id);
    try std.testing.expectEqual(@as(?i64, null), rejection(frame.value));
    const value = systemIntegration(frame.value);
    try std.testing.expect(value.task_manager_enabled and value.hotkey_enabled);
    // Non-integer entries are dropped before slotting, like the C# reader.
    try std.testing.expectEqualSlices(i32, &.{ 17, 1, 18, 0, 0, 0, 0 }, &value.hotkey.encoding());
    const denied = try parseFrame(arena.allocator(), "{\"subscriptionId\":\"settings\",\"value\":{\"statusCode\":403}}");
    try std.testing.expectEqual(@as(?i64, 403), rejection(denied.value));
    const empty = systemIntegration((try parseFrame(arena.allocator(), "{\"subscriptionId\":\"s\",\"value\":{\"systemIntegration\":{\"hotkeys\":[{\"actionId\":\"force-terminate-unresponsive-and-foreground\",\"enabled\":true,\"encoding\":[]}]}}}")).value);
    try std.testing.expect(!empty.hotkey_enabled and !empty.task_manager_enabled);
    try std.testing.expectError(error.InvalidFrame, parseFrame(arena.allocator(), "[1]"));
}

test "the overlay toggle hotkey and the overlay section come from the same settings frame" {
    var storage: [64 * 1024]u8 = undefined;
    var arena = std.heap.FixedBufferAllocator.init(&storage);
    const frame = try parseFrame(arena.allocator(),
        \\{"subscriptionId":"settings","value":{"systemIntegration":{"hotkeys":[{"actionId":"toggle-performance-overlay-foreground","enabled":true,"encoding":[17,1,79,0,0,0,0]},{"actionId":"toggle-performance-overlay-foreground","enabled":false,"encoding":[]}]},"performanceOverlay":{"software":{"game":{"enabled":false},"other":{"enabled":true,"mode":"external"}}}}}
    );
    const value = systemIntegration(frame.value);
    try std.testing.expect(value.overlay_toggle_enabled and !value.hotkey_enabled);
    try std.testing.expectEqualSlices(i32, &.{ 17, 1, 79, 0, 0, 0, 0 }, &value.overlay_toggle.encoding());
    var out: [1024]u8 = undefined;
    const section = try overlaySection(frame.value, &out);
    try std.testing.expectEqual(@as(?bool, true), section.enabled_any);
    try std.testing.expect(std.mem.indexOf(u8, section.json, "\"other\"") != null);
    const off = try overlaySection((try parseFrame(arena.allocator(), "{\"subscriptionId\":\"s\",\"value\":{\"performanceOverlay\":{\"software\":{}}}}")).value, &out);
    try std.testing.expectEqual(@as(?bool, false), off.enabled_any);
    const absent = try overlaySection((try parseFrame(arena.allocator(), "{\"subscriptionId\":\"s\",\"value\":{}}")).value, &out);
    try std.testing.expectEqual(@as(?bool, null), absent.enabled_any);
    var tiny: [4]u8 = undefined;
    try std.testing.expectError(error.OverlaySettingsTooLarge, overlaySection(frame.value, &tiny));
}

const mock = @import("mock_backend.zig");
test "stream runner delivers frames and reports loss to its owner" {
    var server = try mock.Server.start(.{ .script = &.{
        .{ .status = "200 OK", .stream = &.{"{\"subscriptionId\":\"settings\",\"value\":{}}\n"}, .stop_after = true },
    } });
    defer server.deinit();
    const memory = c.VirtualAlloc(null, @sizeOf(Workspace), c.MEM_RESERVE | c.MEM_COMMIT, c.PAGE_READWRITE) orelse return error.OutOfMemory;
    defer _ = c.VirtualFree(memory, 0, c.MEM_RELEASE);
    const workspace: *Workspace = @ptrCast(@alignCast(memory));
    workspace.channel = .{};
    const Owner = struct {
        frames: usize = 0,
        losses: usize = 0,
        fn frame(raw: *anyopaque, value: Frame) void {
            const self: *@This() = @ptrCast(@alignCast(raw));
            std.debug.assert(std.mem.eql(u8, value.id, "settings"));
            self.frames += 1;
        }
        fn lost(raw: *anyopaque, _: []const u8) void {
            const self: *@This() = @ptrCast(@alignCast(raw));
            self.losses += 1;
        }
    };
    var owner: Owner = .{};
    var body: [256]u8 = undefined;
    run(server.stop, workspace, .{ .opener = server.opener(), .body = try requestBody(&body, &.{.{ .id = "settings", .path = "/api/settings/app/subscribe" }}), .retry_ms = 2000 }, .{ .context = &owner, .frame = Owner.frame, .lost = Owner.lost });
    server.finish();
    try std.testing.expectEqual(@as(usize, 1), owner.frames);
    // Stop is not a loss.
    try std.testing.expectEqual(@as(usize, 0), owner.losses);
    try std.testing.expect(server.logged("POST /api/subscriptions/stream HTTP/1.1"));
    try std.testing.expect(server.logged("/api/settings/app/subscribe"));
}

test "an invalid settings frame reports loss and reopens the same subscription" {
    var server = try mock.Server.start(.{ .script = &.{
        .{ .status = "200 OK", .stream = &.{"{}\n"}, .end_stream = true, .close_after = true },
        .{ .status = "200 OK", .stream = &.{"{\"subscriptionId\":\"settings\",\"value\":{}}\n"}, .end_stream = true, .close_after = true },
    } });
    defer server.deinit();
    const memory = @import("../memory.zig");
    var ledger: memory.Owner = .{};
    try ledger.init();
    defer ledger.deinit() catch unreachable;
    var lease = try ledger.acquire(.backend_settings_subscription, @sizeOf(Workspace));
    defer _ = lease.endUse();
    const workspace = lease.as(Workspace);
    workspace.channel = .{};
    const Owner = struct {
        stop: c.HANDLE,
        frames: usize = 0,
        losses: usize = 0,
        fn frame(raw: *anyopaque, value: Frame) void {
            const self: *@This() = @ptrCast(@alignCast(raw));
            std.debug.assert(std.mem.eql(u8, value.id, "settings"));
            self.frames += 1;
            _ = c.SetEvent(self.stop);
        }
        fn lost(raw: *anyopaque, cause: []const u8) void {
            const self: *@This() = @ptrCast(@alignCast(raw));
            std.debug.assert(std.mem.eql(u8, cause, "InvalidFrame"));
            self.losses += 1;
        }
    };
    var owner: Owner = .{ .stop = server.stop };
    var body: [256]u8 = undefined;
    run(server.stop, workspace, .{ .opener = server.opener(), .body = try requestBody(&body, &.{.{ .id = "settings", .path = "/api/settings/app/subscribe" }}), .retry_ms = 1 }, .{ .context = &owner, .frame = Owner.frame, .lost = Owner.lost });
    lease.publish();
    server.finish();
    try std.testing.expectEqual(@as(usize, 1), owner.frames);
    try std.testing.expectEqual(@as(usize, 1), owner.losses);
}
