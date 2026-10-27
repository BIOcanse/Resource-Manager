//! A read-only snapshot of the frontend cache. Call at initialization and on
//! preferences-changed notices; this module owns no watcher or retained buffer.
const std = @import("std");
const catalog = @import("text/generated/tray.zig");
const c = @import("backend/c.zig").win;

pub const Config = struct {
    package_root: []const u8,
    preferences_path: ?[]const u8 = null,
};
pub const Snapshot = struct {
    language: [32]u8 = .{0} ** 32,
    language_len: usize = 0,
    overlay_enabled: ?bool = null,
    notifications_enabled: ?bool = null,

    pub fn languageSlice(self: *const Snapshot) []const u8 {
        return self.language[0..self.language_len];
    }
    pub fn system() Snapshot {
        var result: Snapshot = .{};
        result.setLanguage(resolveLanguage(null, systemLanguage()));
        return result;
    }
    fn setLanguage(self: *Snapshot, language: []const u8) void {
        @memset(&self.language, 0);
        @memcpy(self.language[0..language.len], language);
        self.language_len = language.len;
    }
};

const file_limit = 1024 * 1024;
const parse_limit = 4 * 1024 * 1024;

pub fn read(config: Config) !Snapshot {
    // A single bounded allocation is released on every return, including parse
    // failures. No cache data or parser arena survives the snapshot operation.
    const memory = c.VirtualAlloc(null, file_limit + parse_limit, c.MEM_RESERVE | c.MEM_COMMIT, c.PAGE_READWRITE) orelse return error.OutOfMemory;
    defer _ = c.VirtualFree(memory, 0, c.MEM_RELEASE);
    const buffer: [*]u8 = @ptrCast(memory);
    var allocator = std.heap.FixedBufferAllocator.init(buffer[file_limit .. file_limit + parse_limit]);
    var path_buffer: [32768]u8 = undefined;
    const path = config.preferences_path orelse try defaultPath(config.package_root, &path_buffer);
    var wide: [32768:0]u16 = undefined;
    const length = try std.unicode.utf8ToUtf16Le(wide[0 .. wide.len - 1], path);
    wide[length] = 0;
    const file = c.CreateFileW(&wide, c.GENERIC_READ, c.FILE_SHARE_READ | c.FILE_SHARE_DELETE, null, c.OPEN_EXISTING, c.FILE_ATTRIBUTE_NORMAL, null);
    if (file == c.INVALID_HANDLE_VALUE) {
        const code = c.GetLastError();
        if (code == c.ERROR_FILE_NOT_FOUND or code == c.ERROR_PATH_NOT_FOUND) return Snapshot.system();
        return error.PreferencesOpen;
    }
    defer _ = c.CloseHandle(file);
    var size: c.LARGE_INTEGER = undefined;
    if (c.GetFileSizeEx(file, &size) == 0) return error.PreferencesRead;
    if (size.QuadPart < 0 or size.QuadPart > file_limit) return error.PreferencesTooLarge;
    const expected: u32 = @intCast(size.QuadPart);
    var count: c.DWORD = 0;
    if (c.ReadFile(file, buffer, expected, &count, null) == 0 or count != expected) return error.PreferencesRead;
    var extra: u8 = undefined;
    if (c.ReadFile(file, &extra, 1, &count, null) == 0) return error.PreferencesRead;
    if (count != 0) return error.PreferencesChangedDuringRead;
    return parse(allocator.allocator(), buffer[0..expected], systemLanguage());
}

fn defaultPath(root: []const u8, output: []u8) ![]const u8 {
    var token: c.HANDLE = null;
    if (c.OpenProcessToken(c.GetCurrentProcess(), c.TOKEN_QUERY, &token) == 0) return error.UserIdentity;
    defer _ = c.CloseHandle(token);
    var token_buffer: [512]u8 align(@alignOf(c.TOKEN_USER)) = undefined;
    var needed: c.DWORD = 0;
    if (c.GetTokenInformation(token, c.TokenUser, &token_buffer, token_buffer.len, &needed) == 0) return error.UserIdentity;
    const user: *const c.TOKEN_USER = @ptrCast(@alignCast(&token_buffer));
    var sid: [*c]u16 = null;
    if (c.ConvertSidToStringSidW(user.User.Sid, &sid) == 0) return error.UserIdentity;
    defer _ = c.LocalFree(@ptrCast(sid));
    var sid_buffer: [256]u8 = undefined;
    const sid_length = try std.unicode.utf16LeToUtf8(&sid_buffer, std.mem.span(@as([*:0]const u16, @ptrCast(sid))));
    return std.fmt.bufPrint(output, "{s}\\Config\\WebView2\\{s}\\frontend-preferences.cache.json", .{ root, sid_buffer[0..sid_length] });
}

fn systemLanguage() ?[]const u8 {
    // Return a canonical catalog string, so no stack slice escapes this call.
    var wide: [c.LOCALE_NAME_MAX_LENGTH]u16 = undefined;
    const count = c.GetUserDefaultLocaleName(&wide, wide.len);
    if (count <= 1) return null;
    var bytes: [128]u8 = undefined;
    const length = std.unicode.utf16LeToUtf8(&bytes, wide[0..@intCast(count - 1)]) catch return null;
    return matchSystem(bytes[0..length]);
}

fn parse(allocator: std.mem.Allocator, bytes: []const u8, system_language: ?[]const u8) !Snapshot {
    const document = try std.json.parseFromSlice(std.json.Value, allocator, bytes, .{ .allocate = .alloc_if_needed });
    defer document.deinit();
    if (document.value != .object) return error.InvalidPreferences;
    var result: Snapshot = .{};
    var language: ?[]const u8 = null;
    if (document.value.object.get("appearance")) |appearance| {
        if (appearance != .object) return error.InvalidPreferences;
        if (appearance.object.get("language")) |value| {
            if (value != .string) return error.InvalidPreferences;
            language = value.string;
        }
    }
    result.setLanguage(resolveLanguage(language, system_language));
    if (document.value.object.get("overlays")) |overlays| {
        if (overlays != .object) return error.InvalidPreferences;
        var enabled: ?bool = if (overlays.object.count() == 0) false else null;
        for (overlays.object.values()) |overlay| {
            if (overlay != .object) return error.InvalidPreferences;
            if (overlay.object.get("enabled")) |value| {
                if (value != .bool) return error.InvalidPreferences;
                enabled = (enabled orelse false) or value.bool;
            }
        }
        result.overlay_enabled = enabled;
    }
    if (document.value.object.get("notifications")) |notifications| {
        if (notifications != .object) return error.InvalidPreferences;
        var enabled: ?bool = null;
        for ([_][]const u8{ "updateSystem", "accessRequestSystem" }) |key| {
            if (notifications.object.get(key)) |value| {
                if (value != .string) return error.InvalidPreferences;
                const on = std.mem.eql(u8, value.string, "on");
                if (!on and !std.mem.eql(u8, value.string, "off")) return error.InvalidPreferences;
                enabled = (enabled orelse false) or on;
            }
        }
        result.notifications_enabled = enabled;
    }
    return result;
}

fn normalize(input: []const u8, output: *[128]u8) ?[]const u8 {
    const trimmed = std.mem.trim(u8, input, " \t\r\n");
    if (trimmed.len == 0 or trimmed.len > output.len) return null;
    for (trimmed, 0..) |byte, index| output[index] = if (byte == '_') '-' else std.ascii.toLower(byte);
    return output[0..trimmed.len];
}
fn exact(input: []const u8) ?[]const u8 {
    for (catalog.all) |text| if (std.ascii.eqlIgnoreCase(input, text.language)) return text.language;
    return null;
}
fn resolveLanguage(preference: ?[]const u8, system_language: ?[]const u8) []const u8 {
    var buffer: [128]u8 = undefined;
    if (preference) |input| if (normalize(input, &buffer)) |normalized| if (exact(normalized)) |language| return language;
    if (system_language) |language| if (matchSystem(language)) |matched| return matched;
    return catalog.fallback_language;
}
fn matchSystem(input: []const u8) ?[]const u8 {
    var buffer: [128]u8 = undefined;
    const normalized = normalize(input, &buffer) orelse return null;
    if (exact(normalized)) |language| return language;
    const separator = std.mem.indexOfScalar(u8, normalized, '-') orelse normalized.len;
    const prefix = normalized[0..separator];
    if (std.mem.eql(u8, prefix, "zh")) return exact(if (std.mem.indexOf(u8, normalized, "tw") != null or std.mem.indexOf(u8, normalized, "hk") != null or std.mem.indexOf(u8, normalized, "hant") != null) "zh-TW" else "zh-CN");
    for ([_][]const u8{ "en", "ja", "ko", "fr", "de", "es", "ru" }) |delivered| {
        if (std.mem.eql(u8, prefix, delivered)) {
            for (catalog.all) |text| if (std.ascii.startsWithIgnoreCase(text.language, delivered)) return text.language;
        }
    }
    for ([_][]const u8{ "pt", "uk", "pl", "tr", "it", "nl", "sv", "fi", "da", "nb", "no", "nn", "cs", "hu", "ro", "el", "he", "iw", "ar", "hi", "id", "in", "vi", "th" }) |undelivered| {
        if (std.mem.eql(u8, prefix, undelivered)) return exact("en-US");
    }
    return null;
}

test "cache fields remain optional and native toggles follow frontend fields" {
    const absent = try parse(std.testing.allocator, "{}", "en-GB");
    try std.testing.expectEqualStrings("en-US", absent.languageSlice());
    try std.testing.expectEqual(@as(?bool, null), absent.overlay_enabled);
    try std.testing.expectEqual(@as(?bool, null), absent.notifications_enabled);
    const present = try parse(std.testing.allocator,
        \\{"appearance":{"language":"zh_TW"},"overlays":{"one":{"enabled":false},"two":{"enabled":true}},"notifications":{"updateSystem":"off","accessRequestSystem":"on"}}
    , "en-US");
    try std.testing.expectEqualStrings("zh-TW", present.languageSlice());
    try std.testing.expectEqual(@as(?bool, true), present.overlay_enabled);
    try std.testing.expectEqual(@as(?bool, true), present.notifications_enabled);
}

test "language resolution matches delivered AppLanguage policy" {
    for (catalog.all) |text| try std.testing.expectEqualStrings(text.language, resolveLanguage(text.language, null));
    try std.testing.expectEqualStrings("zh-TW", resolveLanguage("system", "zh-Hant-HK"));
    try std.testing.expectEqualStrings("en-US", resolveLanguage("unknown", "pt-PT"));
    try std.testing.expectEqualStrings(catalog.fallback_language, resolveLanguage(null, "unknown"));
    try std.testing.expectError(error.InvalidPreferences, parse(std.testing.allocator, "{\"notifications\":{\"updateSystem\":true}}", null));
}

test "missing cache reads do not synthesize frontend defaults" {
    // A nonexistent child of this process's existing temp path exercises the
    // complete Win32 path, token/SID lookup and transient-allocation boundary.
    var wide: [32768]u16 = undefined;
    const count = c.GetTempPathW(wide.len, &wide);
    if (count == 0 or count >= wide.len) return error.TempPath;
    var temporary: [32768]u8 = undefined;
    const length = try std.unicode.utf16LeToUtf8(&temporary, wide[0..count]);
    var root_buffer: [32768]u8 = undefined;
    const root = try std.fmt.bufPrint(&root_buffer, "{s}rm-preferences-missing-{d}", .{ temporary[0..length], c.GetCurrentProcessId() });
    const result = try read(.{ .package_root = root });
    try std.testing.expectEqual(@as(?bool, null), result.overlay_enabled);
    try std.testing.expectEqual(@as(?bool, null), result.notifications_enabled);
    try std.testing.expect(result.language_len > 0);
}

test "explicit cache path reads saved values and rejects oversized files" {
    var temp: [32768]u16 = undefined;
    const count = c.GetTempPathW(temp.len, &temp);
    if (count == 0 or count >= temp.len) return error.TempPath;
    var utf8: [32768]u8 = undefined;
    const temp_length = try std.unicode.utf16LeToUtf8(&utf8, temp[0..count]);
    var path_buffer: [32768]u8 = undefined;
    const path = try std.fmt.bufPrint(&path_buffer, "{s}rm-preferences-偏好-{d}.json", .{ utf8[0..temp_length], c.GetCurrentProcessId() });
    var wide: [32768:0]u16 = undefined;
    const length = try std.unicode.utf8ToUtf16Le(wide[0 .. wide.len - 1], path);
    wide[length] = 0;
    const file = c.CreateFileW(&wide, c.DELETE, c.FILE_SHARE_READ | c.FILE_SHARE_WRITE | c.FILE_SHARE_DELETE, null, c.CREATE_NEW, c.FILE_ATTRIBUTE_TEMPORARY | c.FILE_FLAG_DELETE_ON_CLOSE, null);
    if (file == c.INVALID_HANDLE_VALUE) return error.FixtureCreate;
    defer _ = c.CloseHandle(file);
    const json = "{\"appearance\":{\"language\":\"ja-JP\"},\"overlays\":{},\"notifications\":{\"updateSystem\":\"off\",\"accessRequestSystem\":\"off\"}}";
    {
        const writer = c.CreateFileW(&wide, c.GENERIC_WRITE, c.FILE_SHARE_READ | c.FILE_SHARE_WRITE | c.FILE_SHARE_DELETE, null, c.OPEN_EXISTING, 0, null);
        if (writer == c.INVALID_HANDLE_VALUE) return error.FixtureCreate;
        defer _ = c.CloseHandle(writer);
        var written: c.DWORD = 0;
        if (c.WriteFile(writer, json.ptr, json.len, &written, null) == 0 or written != json.len) return error.FixtureWrite;
    }
    const snapshot = try read(.{ .package_root = "", .preferences_path = path });
    try std.testing.expectEqualStrings("ja-JP", snapshot.languageSlice());
    try std.testing.expectEqual(@as(?bool, false), snapshot.overlay_enabled);
    try std.testing.expectEqual(@as(?bool, false), snapshot.notifications_enabled);
    {
        const writer = c.CreateFileW(&wide, c.GENERIC_WRITE, c.FILE_SHARE_READ | c.FILE_SHARE_WRITE | c.FILE_SHARE_DELETE, null, c.OPEN_EXISTING, 0, null);
        if (writer == c.INVALID_HANDLE_VALUE) return error.FixtureCreate;
        defer _ = c.CloseHandle(writer);
        const position: c.LARGE_INTEGER = .{ .QuadPart = file_limit + 1 };
        if (c.SetFilePointerEx(writer, position, null, c.FILE_BEGIN) == 0 or c.SetEndOfFile(writer) == 0) return error.FixtureWrite;
    }
    try std.testing.expectError(error.PreferencesTooLarge, read(.{ .package_root = "", .preferences_path = path }));
}
