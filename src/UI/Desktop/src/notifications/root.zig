//! TrayNotifications (uid 50, under Tray). One named holder of the backend
//! notification list through the unified subscription stream; each new
//! notification (by sequence, against the tray's own record file) becomes a
//! Windows balloon when its kind's Notify and System switches are on, read at
//! the moment of the decision. Ported from the C# TrayBackendNotifications.
//!
//! The document places interface visibility with the resident and says the
//! resident uses it to decide whether a balloon pops: while the main window is
//! shown and not covered, the page shows the notice and no balloon is shown.
const std = @import("std");
const w = @import("../win32.zig");
const c = w.c;
const subscriptions = @import("../backend/subscriptions.zig");
const channel_module = @import("../backend/channel.zig");
const lookup = @import("../text/lookup.zig");
const mock = @import("../backend/mock_backend.zig");

pub const subscription_id = "notifications";
pub const subscription_path = "/api/notifications/subscribe";
pub const record_suffix = ".native-notifications.json";
/// Bound for the record path and its temporary sibling (UTF-16 units).
pub const max_path_units = 4096;
const memory_protection_kind = "memory-protection.action";
const update_kind = "update-available";
const access_request_kind = "access-request";
const crash_report_kind = "crash-report";

pub const Kind = enum { memory_protection, update, access_request, crash_report };
pub const Switches = struct { memory_protection: bool = false, update: bool = false, access_request: bool = false, crash_report: bool = false };

pub const Host = struct {
    context: *anyopaque,
    running: *const fn (*anyopaque) void,
    /// Per-kind switches from the current preferences snapshot (unknown is off).
    switches: *const fn (*anyopaque) Switches,
    /// Interface visible as last computed by DesktopObservation; unknown is false.
    interface_visible: *const fn (*anyopaque) bool,
    /// Catalog of the current shell language (also gives the size separator).
    text: *const fn (*anyopaque) *const lookup.Text,
    balloon: *const fn (*anyopaque, *const lookup.Balloon) void,
    log: *const fn (*anyopaque, []const u8) void,
};
pub const Config = struct {
    host: Host,
    opener: channel_module.Opener,
    retry_ms: u32,
    /// `<frontend cache path>.native-notifications.json`, NUL-terminated.
    record_path: [:0]const u16,
};

// ---- record file ----

/// Null until the tray has received its first list (file missing or no value).
pub fn readRecord(path: [:0]const u16) !?i64 {
    const file = c.CreateFileW(path.ptr, c.GENERIC_READ, c.FILE_SHARE_READ | c.FILE_SHARE_DELETE, null, c.OPEN_EXISTING, c.FILE_ATTRIBUTE_NORMAL, null);
    if (file == c.INVALID_HANDLE_VALUE) {
        const code = c.GetLastError();
        if (code == c.ERROR_FILE_NOT_FOUND or code == c.ERROR_PATH_NOT_FOUND) return null;
        return error.RecordOpen;
    }
    defer _ = c.CloseHandle(file);
    var bytes: [4096]u8 = undefined;
    var count: c.DWORD = 0;
    if (c.ReadFile(file, &bytes, bytes.len, &count, null) == 0) return error.RecordRead;
    if (count == bytes.len) return error.RecordTooLarge;
    return parseRecord(bytes[0..count]);
}
pub fn parseRecord(bytes: []const u8) !?i64 {
    var storage: [16 * 1024]u8 = undefined;
    var arena = std.heap.FixedBufferAllocator.init(&storage);
    const root = std.json.parseFromSliceLeaky(std.json.Value, arena.allocator(), bytes, .{}) catch return error.InvalidRecord;
    if (root != .object) return error.InvalidRecord;
    // Fields from before 2026-10-29 (updateSystemNotifiedVersion,
    // accessRequestNotifiedIds) are ignored, as the C# migration drops them.
    const value = root.object.get("lastHandledSequence") orelse return null;
    return switch (value) {
        .null => null,
        .integer => |number| if (number < 0) error.InvalidRecord else number,
        else => error.InvalidRecord,
    };
}
/// Whole-file replace through a temporary file, flushed before the rename.
pub fn writeRecord(path: [:0]const u16, value: i64) !void {
    std.debug.assert(value >= 0);
    var json: [64]u8 = undefined;
    const body = try std.fmt.bufPrint(&json, "{{\"lastHandledSequence\":{d}}}", .{value});
    var temporary: [max_path_units:0]u16 = undefined;
    var suffix: [48]u8 = undefined;
    const tail = try std.fmt.bufPrint(&suffix, ".{d}-{d}.tmp", .{ c.GetCurrentProcessId(), c.GetTickCount64() });
    if (path.len + tail.len >= temporary.len) return error.NameTooLong;
    @memcpy(temporary[0..path.len], path);
    for (tail, 0..) |byte, index| temporary[path.len + index] = byte;
    temporary[path.len + tail.len] = 0;
    createParents(path);
    const file = c.CreateFileW(&temporary, c.GENERIC_WRITE, 0, null, c.CREATE_NEW, c.FILE_ATTRIBUTE_NORMAL, null);
    if (file == c.INVALID_HANDLE_VALUE) return error.RecordWrite;
    var written: c.DWORD = 0;
    const ok = c.WriteFile(file, body.ptr, @intCast(body.len), &written, null) != 0 and written == body.len and c.FlushFileBuffers(file) != 0;
    _ = c.CloseHandle(file);
    if (!ok or c.MoveFileExW(&temporary, path.ptr, c.MOVEFILE_REPLACE_EXISTING | c.MOVEFILE_WRITE_THROUGH) == 0) {
        _ = c.DeleteFileW(&temporary);
        return error.RecordWrite;
    }
}
/// Creates each missing parent directory, outermost first.
fn createParents(path: [:0]const u16) void {
    var buffer: [max_path_units:0]u16 = undefined;
    if (path.len >= buffer.len) return;
    var index: usize = 3;
    while (index < path.len) : (index += 1) {
        if (path[index] != '\\' and path[index] != '/') continue;
        @memcpy(buffer[0..index], path[0..index]);
        buffer[index] = 0;
        _ = c.CreateDirectoryW(&buffer, null);
    }
}

// ---- messages ----

/// Binary units with one decimal from KiB up ("0 B", "1.5 KiB"), using the
/// language's decimal separator like the C# culture formatting.
pub fn formatSize(out: []u8, bytes: i64, separator: u8) ![]const u8 {
    std.debug.assert(bytes >= 0);
    if (bytes < 1024) return std.fmt.bufPrint(out, "{d} B", .{bytes});
    const units = [_][]const u8{ "KiB", "MiB", "GiB" };
    var size = @as(f64, @floatFromInt(bytes)) / 1024.0;
    var unit: usize = 0;
    while (unit < units.len - 1 and size >= 1024) : (unit += 1) size /= 1024;
    const tenths: u64 = @intFromFloat(@round(size * 10));
    return std.fmt.bufPrint(out, "{d}{c}{d} {s}", .{ tenths / 10, separator, tenths % 10, units[unit] });
}
pub fn decimalSeparator(language: []const u8) u8 {
    for ([_][]const u8{ "fr-FR", "de-DE", "es-ES", "ru-RU" }) |comma| if (std.ascii.eqlIgnoreCase(comma, language)) return ',';
    return '.';
}
/// Path.GetFileName: the text after the last directory or volume separator.
fn fileName(path: []const u8) []const u8 {
    var start: usize = 0;
    for (path, 0..) |byte, index| {
        if (byte == '\\' or byte == '/' or byte == ':') start = index + 1;
    }
    return path[start..];
}
fn string(value: std.json.Value, name: []const u8) ?[]const u8 {
    const field = value.object.get(name) orelse return null;
    return if (field == .string) field.string else null;
}
fn countField(value: std.json.Value, name: []const u8) ?i64 {
    const field = value.object.get(name) orelse return null;
    return if (field == .integer and field.integer >= 0) field.integer else null;
}

pub fn kindOf(name: []const u8) ?Kind {
    if (std.mem.eql(u8, name, memory_protection_kind)) return .memory_protection;
    if (std.mem.eql(u8, name, update_kind)) return .update;
    if (std.mem.eql(u8, name, access_request_kind)) return .access_request;
    if (std.mem.eql(u8, name, crash_report_kind)) return .crash_report;
    return null;
}

/// The balloon for one notification, or null when its facts do not decode.
pub fn message(kind: Kind, facts: ?std.json.Value, text: *const lookup.Text) ?lookup.Balloon {
    const value = facts orelse return null;
    if (value != .object) return null;
    var numbers: [2][24]u8 = undefined;
    var size: [32]u8 = undefined;
    switch (kind) {
        .memory_protection => {
            const target = string(value, "targetKind") orelse return null;
            const name = string(value, "name") orelse return null;
            const pid = countField(value, "processId") orelse return null;
            const written = countField(value, "bytesWritten") orelse return null;
            const size_text = formatSize(&size, written, decimalSeparator(text.language)) catch return null;
            const pid_text = std.fmt.bufPrint(&numbers[0], "{d}", .{pid}) catch unreachable;
            if (std.mem.eql(u8, target, "process")) return lookup.balloon(.info, text.memory_protection_title, text.memory_protection_process, &.{ name, pid_text, size_text });
            if (!std.mem.eql(u8, target, "processTree")) return null;
            const records = countField(value, "recordCount") orelse return null;
            const records_text = std.fmt.bufPrint(&numbers[1], "{d}", .{records}) catch unreachable;
            return lookup.balloon(.info, text.memory_protection_title, text.memory_protection_tree, &.{ name, pid_text, records_text, size_text });
        },
        .update => {
            const version = string(value, "version") orelse return null;
            if (version.len == 0) return null;
            return lookup.balloon(.info, text.update_available_title, text.update_available, &.{version});
        },
        .access_request => {
            // The self-reported name can be forged, so the observed file name is shown with it.
            const name = string(value, "name") orelse return null;
            const path = string(value, "observedExecutablePath") orelse return null;
            if (name.len == 0 or path.len == 0) return null;
            const file = fileName(path);
            if (file.len == 0) return null;
            return lookup.balloon(.info, text.access_request_title, text.access_request, &.{ name, file });
        },
        .crash_report => {
            // Startup discovery posts one notice for newly registered records.
            // Facts validate the envelope; recovery copy is fixed catalog text.
            const count = countField(value, "count") orelse return null;
            const latest = string(value, "latestReportId") orelse return null;
            if (count < 1 or latest.len == 0) return null;
            return lookup.balloon(.info, text.crash_report_title, text.crash_report, &.{});
        },
    }
}

/// Sequences must be >= 1 and strictly increasing and every kind a string;
/// otherwise the whole frame is ignored. Returns the latest sequence (0 when empty).
pub fn validate(list: []const std.json.Value) ?i64 {
    var previous: i64 = 0;
    for (list) |item| {
        if (item != .object) return null;
        const sequence = item.object.get("sequence") orelse return null;
        if (sequence != .integer or sequence.integer < 1 or sequence.integer <= previous) return null;
        _ = string(item, "kind") orelse return null;
        previous = sequence.integer;
    }
    return previous;
}

const State = struct {
    config: Config,

    fn frame(raw: *anyopaque, value: subscriptions.Frame) void {
        const self: *State = @ptrCast(@alignCast(raw));
        if (!std.mem.eql(u8, value.id, subscription_id)) return;
        self.observe(value.value);
    }
    fn lost(raw: *anyopaque, reason: []const u8) void {
        const self: *State = @ptrCast(@alignCast(raw));
        self.config.host.log(self.config.host.context, reason);
    }
    fn observe(self: *State, value: std.json.Value) void {
        const host = self.config.host;
        // A rejected selector or any value without a list is not an empty list:
        // it must not move the record.
        if (value != .object) return;
        const list = value.object.get("notifications") orelse {
            if (subscriptions.rejection(value) != null) host.log(host.context, "notification subscription rejected");
            return;
        };
        if (list != .array) return;
        const items = list.array.items;
        const latest = validate(items) orelse {
            host.log(host.context, "notification list has an invalid sequence or kind; frame ignored");
            return;
        };
        var record = (readRecord(self.config.record_path) catch |err| {
            host.log(host.context, @errorName(err));
            return;
        }) orelse {
            // The first list ever only records where the tray starts.
            _ = self.store(latest);
            return;
        };
        if (latest < record) {
            // The backend notification file was rebuilt and its sequence started again.
            record = 0;
            if (!self.store(0)) return;
        }
        for (items) |item| {
            const sequence = item.object.get("sequence").?.integer;
            if (sequence <= record) continue;
            self.handle(item);
            // Stored after each notification, so a restart never shows the same one twice.
            if (!self.store(sequence)) return;
            record = sequence;
        }
    }
    fn store(self: *State, value: i64) bool {
        writeRecord(self.config.record_path, value) catch |err| {
            self.config.host.log(self.config.host.context, @errorName(err));
            return false;
        };
        return true;
    }
    fn handle(self: *State, item: std.json.Value) void {
        const host = self.config.host;
        const kind = kindOf(string(item, "kind").?) orelse return;
        const value = message(kind, item.object.get("facts"), host.text(host.context)) orelse {
            host.log(host.context, "notification facts do not decode; not shown");
            return;
        };
        const switches = host.switches(host.context);
        const enabled = switch (kind) {
            .memory_protection => switches.memory_protection,
            .update => switches.update,
            .access_request => switches.access_request,
            .crash_report => switches.crash_report,
        };
        if (!enabled or host.interface_visible(host.context)) return;
        host.balloon(host.context, &value);
    }
};

/// Runs until stop. The stream workspace is VirtualAlloc'ed for the run only.
pub fn run(stop: c.HANDLE, config: Config) !void {
    const memory = c.VirtualAlloc(null, @sizeOf(subscriptions.Workspace), c.MEM_RESERVE | c.MEM_COMMIT, c.PAGE_READWRITE) orelse return error.OutOfMemory;
    defer _ = c.VirtualFree(memory, 0, c.MEM_RELEASE);
    const workspace: *subscriptions.Workspace = @ptrCast(@alignCast(memory));
    workspace.channel = .{};
    var body: [256]u8 = undefined;
    const request = try subscriptions.requestBody(&body, &.{.{ .id = subscription_id, .path = subscription_path }});
    var state: State = .{ .config = config };
    config.host.running(config.host.context);
    subscriptions.run(stop, workspace, .{ .opener = config.opener, .body = request, .retry_ms = config.retry_ms }, .{ .context = &state, .frame = State.frame, .lost = State.lost });
}

test "sizes, separators and file names follow the C# formatting" {
    var out: [32]u8 = undefined;
    try std.testing.expectEqualStrings("0 B", try formatSize(&out, 0, '.'));
    try std.testing.expectEqualStrings("1023 B", try formatSize(&out, 1023, '.'));
    try std.testing.expectEqualStrings("1.5 KiB", try formatSize(&out, 1536, '.'));
    try std.testing.expectEqualStrings("1,0 MiB", try formatSize(&out, 1024 * 1024, ','));
    try std.testing.expectEqualStrings("2048.0 GiB", try formatSize(&out, 2048 * 1024 * 1024 * 1024, '.'));
    try std.testing.expectEqual(@as(u8, ','), decimalSeparator("de-DE"));
    try std.testing.expectEqual(@as(u8, '.'), decimalSeparator("zh-CN"));
    try std.testing.expectEqualStrings("app.exe", fileName("C:\\Tools\\app.exe"));
    try std.testing.expectEqualStrings("", fileName("C:\\Tools\\"));
}

test "messages decode facts like the C# reader and reject incomplete ones" {
    var storage: [16 * 1024]u8 = undefined;
    var arena = std.heap.FixedBufferAllocator.init(&storage);
    const text = lookup.select("en-US");
    const facts = try std.json.parseFromSliceLeaky(std.json.Value, arena.allocator(), "{\"targetKind\":\"processTree\",\"name\":\"game.exe\",\"processId\":42,\"bytesWritten\":1536,\"recordCount\":3}", .{});
    const tree = message(.memory_protection, facts, text).?;
    const expected = lookup.balloon(.info, text.memory_protection_title, text.memory_protection_tree, &.{ "game.exe", "42", "3", "1.5 KiB" });
    try std.testing.expectEqualSlices(u16, expected.bodySlice(), tree.bodySlice());
    const no_count = try std.json.parseFromSliceLeaky(std.json.Value, arena.allocator(), "{\"targetKind\":\"processTree\",\"name\":\"a\",\"processId\":1,\"bytesWritten\":0}", .{});
    try std.testing.expectEqual(@as(?lookup.Balloon, null), message(.memory_protection, no_count, text));
    const access = try std.json.parseFromSliceLeaky(std.json.Value, arena.allocator(), "{\"name\":\"Helper\",\"observedExecutablePath\":\"D:\\\\x\\\\helper.exe\"}", .{});
    const request = message(.access_request, access, text).?;
    const expected_access = lookup.balloon(.info, text.access_request_title, text.access_request, &.{ "Helper", "helper.exe" });
    try std.testing.expectEqualSlices(u16, expected_access.bodySlice(), request.bodySlice());
    try std.testing.expectEqual(@as(?lookup.Balloon, null), message(.update, null, text));
    try std.testing.expectEqual(@as(?Kind, null), kindOf("other"));
}

test "list validation and record file round trip" {
    var storage: [16 * 1024]u8 = undefined;
    var arena = std.heap.FixedBufferAllocator.init(&storage);
    const good = try std.json.parseFromSliceLeaky(std.json.Value, arena.allocator(), "[{\"sequence\":1,\"kind\":\"x\"},{\"sequence\":5,\"kind\":\"update-available\"}]", .{});
    try std.testing.expectEqual(@as(?i64, 5), validate(good.array.items));
    const bad = try std.json.parseFromSliceLeaky(std.json.Value, arena.allocator(), "[{\"sequence\":2,\"kind\":\"x\"},{\"sequence\":2,\"kind\":\"x\"}]", .{});
    try std.testing.expectEqual(@as(?i64, null), validate(bad.array.items));
    try std.testing.expectEqual(@as(?i64, 0), validate(&.{}));
    try std.testing.expectEqual(@as(?i64, null), try parseRecord("{\"updateSystemNotifiedVersion\":\"1\"}"));
    try std.testing.expectError(error.InvalidRecord, parseRecord("{\"lastHandledSequence\":-1}"));

    var record: TempRecord = undefined;
    const record_path = try record.init("roundtrip");
    defer record.deinit();
    try std.testing.expectEqual(@as(?i64, null), try readRecord(record_path));
    try writeRecord(record_path, 7);
    try std.testing.expectEqual(@as(?i64, 7), try readRecord(record_path));
    try writeRecord(record_path, 0);
    try std.testing.expectEqual(@as(?i64, 0), try readRecord(record_path));
}

/// A record path under a per-process temp directory, removed by deinit.
const TempRecord = struct {
    path: [max_path_units:0]u16,
    len: usize,
    fn init(self: *TempRecord, tag: []const u8) ![:0]const u16 {
        const length = c.GetTempPathW(max_path_units, &self.path);
        if (length == 0 or length >= max_path_units - 128) return error.TempPath;
        var name: [128]u8 = undefined;
        const leaf = try std.fmt.bufPrint(&name, "rm-notify-{d}-{s}\\cache.json" ++ record_suffix, .{ c.GetCurrentProcessId(), tag });
        for (leaf, 0..) |byte, index| self.path[length + index] = byte;
        self.len = length + leaf.len;
        self.path[self.len] = 0;
        _ = c.DeleteFileW(&self.path);
        return self.path[0..self.len :0];
    }
    fn deinit(self: *TempRecord) void {
        _ = c.DeleteFileW(&self.path);
        const slash = std.mem.lastIndexOfScalar(u16, self.path[0..self.len], '\\').?;
        self.path[slash] = 0;
        _ = c.RemoveDirectoryW(&self.path);
    }
};

const Probe = struct {
    balloons: [8]lookup.Balloon = undefined,
    count: usize = 0,
    visible: bool = false,
    running_seen: bool = false,
    current_switches: Switches = .{ .memory_protection = false, .update = true, .access_request = true },
    language: []const u8 = "en-US",
    fn running(raw: *anyopaque) void {
        const self: *Probe = @ptrCast(@alignCast(raw));
        self.running_seen = true;
    }
    fn switches(raw: *anyopaque) Switches {
        const self: *Probe = @ptrCast(@alignCast(raw));
        return self.current_switches;
    }
    fn interfaceVisible(raw: *anyopaque) bool {
        const self: *Probe = @ptrCast(@alignCast(raw));
        return self.visible;
    }
    fn text(raw: *anyopaque) *const lookup.Text {
        const self: *Probe = @ptrCast(@alignCast(raw));
        return lookup.select(self.language);
    }
    fn balloon(raw: *anyopaque, value: *const lookup.Balloon) void {
        const self: *Probe = @ptrCast(@alignCast(raw));
        if (self.count < self.balloons.len) self.balloons[self.count] = value.*;
        self.count += 1;
    }
    fn log(_: *anyopaque, _: []const u8) void {}
    fn host(self: *Probe) Host {
        return .{ .context = self, .running = running, .switches = switches, .interface_visible = interfaceVisible, .text = text, .balloon = balloon, .log = log };
    }
};

test "notification stream: first list only records, new ones pop by switch, a rebuilt list restarts" {
    var record: TempRecord = undefined;
    const record_path = try record.init("stream");
    defer record.deinit();
    const first = "{\"subscriptionId\":\"notifications\",\"value\":{\"notifications\":[{\"sequence\":1,\"kind\":\"update-available\",\"facts\":{\"version\":\"0.9\"}},{\"sequence\":2,\"kind\":\"access-request\"}]}}\n";
    const rejected = "{\"subscriptionId\":\"notifications\",\"value\":{\"statusCode\":403}}\n";
    const invalid = "{\"subscriptionId\":\"notifications\",\"value\":{\"notifications\":[{\"sequence\":9,\"kind\":\"update-available\"},{\"sequence\":9,\"kind\":\"x\"}]}}\n";
    const second = "{\"subscriptionId\":\"notifications\",\"value\":{\"notifications\":[{\"sequence\":2,\"kind\":\"access-request\"},{\"sequence\":3,\"kind\":\"update-available\",\"facts\":{\"version\":\"1.2.3\"}},{\"sequence\":4,\"kind\":\"future-kind\"},{\"sequence\":5,\"kind\":\"access-request\",\"facts\":{\"name\":\"Helper\",\"observedExecutablePath\":\"C:\\\\x\\\\helper.exe\"}},{\"sequence\":6,\"kind\":\"update-available\",\"facts\":{}}]}}\n";
    const rebuilt = "{\"subscriptionId\":\"notifications\",\"value\":{\"notifications\":[{\"sequence\":1,\"kind\":\"memory-protection.action\",\"facts\":{\"targetKind\":\"process\",\"name\":\"a.exe\",\"processId\":7,\"bytesWritten\":0}}]}}\n";
    var server = try mock.Server.start(.{ .script = &.{
        .{ .status = "200 OK", .stream = &.{ first, rejected, invalid, second, rebuilt }, .stop_after = true },
    } });
    defer server.deinit();
    var probe: Probe = .{};
    try run(server.stop, .{ .host = probe.host(), .opener = server.opener(), .retry_ms = 2000, .record_path = record_path });
    server.finish();
    try std.testing.expect(probe.running_seen);
    try std.testing.expect(server.logged("/api/notifications/subscribe"));
    // Sequence 3 (update) and 5 (access request); 4 is unknown, 6 lacks a version,
    // and the rebuilt list's memory-protection notice is switched off.
    try std.testing.expectEqual(@as(usize, 2), probe.count);
    const text = lookup.select("en-US");
    const update = lookup.balloon(.info, text.update_available_title, text.update_available, &.{"1.2.3"});
    try std.testing.expectEqualSlices(u16, update.bodySlice(), probe.balloons[0].bodySlice());
    const access = lookup.balloon(.info, text.access_request_title, text.access_request, &.{ "Helper", "helper.exe" });
    try std.testing.expectEqualSlices(u16, access.bodySlice(), probe.balloons[1].bodySlice());
    try std.testing.expectEqual(@as(?i64, 1), try readRecord(record_path));

    // While the interface is visible the page shows the notice: the record moves, no balloon.
    var storage: [16 * 1024]u8 = undefined;
    var arena = std.heap.FixedBufferAllocator.init(&storage);
    const later = try std.json.parseFromSliceLeaky(std.json.Value, arena.allocator(), "{\"notifications\":[{\"sequence\":2,\"kind\":\"update-available\",\"facts\":{\"version\":\"2\"}}]}", .{});
    probe.visible = true;
    var state: State = .{ .config = .{ .host = probe.host(), .opener = server.opener(), .retry_ms = 2000, .record_path = record_path } };
    state.observe(later);
    try std.testing.expectEqual(@as(usize, 2), probe.count);
    try std.testing.expectEqual(@as(?i64, 2), try readRecord(record_path));
}

test "batch13 crash report uses fixed shell-language copy in every delivered language" {
    var storage: [4096]u8 = undefined;
    var arena = std.heap.FixedBufferAllocator.init(&storage);
    const facts = try std.json.parseFromSliceLeaky(std.json.Value, arena.allocator(), "{\"count\":1,\"latestReportId\":\"report-1\",\"program\":\"untrusted injected copy\",\"role\":\"service\"}", .{});
    try std.testing.expectEqual(@as(?Kind, .crash_report), kindOf("crash-report"));
    for (&lookup.catalog.all) |*text| {
        const shown = message(.crash_report, facts, text).?;
        const expected = lookup.balloon(.info, text.crash_report_title, text.crash_report, &.{});
        try std.testing.expectEqualSlices(u16, expected.titleSlice(), shown.titleSlice());
        try std.testing.expectEqualSlices(u16, expected.bodySlice(), shown.bodySlice());
    }
}

test "batch13 crash report decisions use current switch and visibility and always advance valid sequences" {
    var record: TempRecord = undefined;
    const path = try record.init("crash-decisions");
    defer record.deinit();
    try writeRecord(path, 0);
    var probe: Probe = .{ .language = "zh-CN" };
    var state: State = .{ .config = .{ .host = probe.host(), .opener = undefined, .retry_ms = 1, .record_path = path } };
    var storage: [64 * 1024]u8 = undefined;
    var arena = std.heap.FixedBufferAllocator.init(&storage);
    const frame = try std.json.parseFromSliceLeaky(std.json.Value, arena.allocator(), "{\"notifications\":[{\"sequence\":1,\"kind\":\"crash-report\",\"facts\":{\"count\":1,\"latestReportId\":\"r\"}}]}", .{});
    state.observe(frame);
    try std.testing.expectEqual(@as(usize, 0), probe.count);
    try std.testing.expectEqual(@as(?i64, 1), try readRecord(path));
    probe.current_switches.crash_report = true;
    // A second observation of the handled sequence never shows it retroactively.
    state.observe(frame);
    try std.testing.expectEqual(@as(usize, 0), probe.count);
    const later = try std.json.parseFromSliceLeaky(std.json.Value, arena.allocator(), "{\"notifications\":[{\"sequence\":2,\"kind\":\"crash-report\",\"facts\":{\"count\":1,\"latestReportId\":\"r2\"}}]}", .{});
    state.observe(later);
    try std.testing.expectEqual(@as(usize, 1), probe.count);
    const text = lookup.select("zh-CN");
    const expected = lookup.balloon(.info, text.crash_report_title, text.crash_report, &.{});
    try std.testing.expectEqualSlices(u16, expected.bodySlice(), probe.balloons[0].bodySlice());
    probe.visible = true;
    const visible = try std.json.parseFromSliceLeaky(std.json.Value, arena.allocator(), "{\"notifications\":[{\"sequence\":3,\"kind\":\"crash-report\",\"facts\":{\"count\":1,\"latestReportId\":\"r3\"}}]}", .{});
    state.observe(visible);
    try std.testing.expectEqual(@as(usize, 1), probe.count);
    try std.testing.expectEqual(@as(?i64, 3), try readRecord(path));
    probe.visible = false;
    const bad = try std.json.parseFromSliceLeaky(std.json.Value, arena.allocator(),
        \\{"notifications":[
        \\ {"sequence":4,"kind":"crash-report","facts":{"latestReportId":"x"}},
        \\ {"sequence":5,"kind":"crash-report","facts":{"count":0,"latestReportId":"x"}},
        \\ {"sequence":6,"kind":"crash-report","facts":{"count":1,"latestReportId":""}},
        \\ {"sequence":7,"kind":"crash-report","facts":{"count":"1","latestReportId":"x"}},
        \\ {"sequence":8,"kind":"crash-report","facts":null}]}
    , .{});
    state.observe(bad);
    try std.testing.expectEqual(@as(usize, 1), probe.count);
    try std.testing.expectEqual(@as(?i64, 8), try readRecord(path));
}
