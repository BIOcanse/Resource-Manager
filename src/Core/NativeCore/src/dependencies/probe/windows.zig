const std = @import("std");
const A = std.mem.Allocator;
pub const Error = error{ OutOfMemory, InvalidPath, Io, AccessDenied, ReparsePoint, HardLink };
const INVALID = std.math.maxInt(usize);
extern "kernel32" fn GetFullPathNameW([*:0]const u16, u32, ?[*]u16, ?*?[*]u16) callconv(.winapi) u32;
extern "kernel32" fn ExpandEnvironmentStringsW([*:0]const u16, ?[*]u16, u32) callconv(.winapi) u32;
extern "kernel32" fn GetFileAttributesW([*:0]const u16) callconv(.winapi) u32;
extern "kernel32" fn GetLastError() callconv(.winapi) u32;
extern "kernel32" fn CloseHandle(usize) callconv(.winapi) i32;
extern "kernel32" fn ReadFile(usize, [*]u8, u32, *u32, ?*anyopaque) callconv(.winapi) i32;
extern "kernel32" fn GetFileSizeEx(usize, *i64) callconv(.winapi) i32;
extern "kernel32" fn FindFirstFileW([*:0]const u16, *FindData) callconv(.winapi) usize;
extern "kernel32" fn FindNextFileW(usize, *FindData) callconv(.winapi) i32;
extern "kernel32" fn FindClose(usize) callconv(.winapi) i32;
extern "kernel32" fn GetLogicalDrives() callconv(.winapi) u32;
extern "kernel32" fn GetDriveTypeW([*:0]const u16) callconv(.winapi) u32;
extern "shell32" fn SHGetFolderPathW(?*anyopaque, i32, ?*anyopaque, u32, [*]u16) callconv(.winapi) i32;
const FileTime = extern struct { low: u32, high: u32 };

pub const FindData = extern struct {
    attributes: u32,
    creation: FileTime,
    access: FileTime,
    write: FileTime,
    size_high: u32,
    size_low: u32,
    reserved0: u32,
    reserved1: u32,
    name: [260]u16,
    alternate: [14]u16,
};
pub fn zero(a: A, value: []const u16) Error![:0]u16 {
    if (std.mem.indexOfScalar(u16, value, 0) != null) return error.InvalidPath;
    return a.dupeSentinel(u16, value, 0);
}
pub fn text(a: A, value: []const u8) Error![]const u16 {
    return std.unicode.utf8ToUtf16LeAlloc(a, value) catch |err| return if (err == error.OutOfMemory) error.OutOfMemory else error.InvalidPath;
}
pub fn eq(left: []const u16, right: []const u16) bool {
    return @import("ordinal.zig").equal(left, right, @import("ordinal.zig").profile);
}
pub fn contains(haystack: []const u16, needle: []const u16) bool {
    if (needle.len > haystack.len) return false;
    var i: usize = 0;
    while (i <= haystack.len - needle.len) : (i += 1) if (eq(haystack[i .. i + needle.len], needle)) return true;
    return false;
}
pub fn whitespace(ch: u16) bool {
    return ch == 0x20 or (ch >= 9 and ch <= 13) or ch == 0x85 or ch == 0xa0 or ch == 0x1680 or (ch >= 0x2000 and ch <= 0x200a) or ch == 0x2028 or ch == 0x2029 or ch == 0x202f or ch == 0x205f or ch == 0x3000;
}
pub fn trim(value: []const u16) []const u16 {
    var first: usize = 0;
    var last = value.len;
    while (first < last and whitespace(value[first])) : (first += 1) {}
    while (last > first and whitespace(value[last - 1])) : (last -= 1) {}
    return value[first..last];
}
pub fn rooted(value: []const u16) bool {
    return value.len > 0 and (value[0] == '\\' or value[0] == '/' or (value.len > 1 and value[1] == ':'));
}
pub fn combine(a: A, left: []const u16, right: []const u16) Error![]const u16 {
    if (left.len == 0 or rooted(right)) return a.dupe(u16, right);
    if (right.len == 0) return a.dupe(u16, left);
    const separator: usize = if (left[left.len - 1] == '\\' or left[left.len - 1] == '/' or left[left.len - 1] == ':') 0 else 1;
    const value = try a.alloc(u16, left.len + separator + right.len);
    @memcpy(value[0..left.len], left);
    if (separator == 1) value[left.len] = '\\';
    @memcpy(value[left.len + separator ..], right);
    return value;
}
pub fn full(a: A, value: []const u16) Error![]const u16 {
    if (trim(value).len == 0) return error.InvalidPath;
    const input = try zero(a, value);
    const required = GetFullPathNameW(input, 0, null, null);
    if (required == 0) return error.InvalidPath;
    const output = try a.alloc(u16, required);
    const written = GetFullPathNameW(input, required, output.ptr, null);
    if (written == 0 or written >= required) return error.InvalidPath;
    return output[0..written];
}
pub fn normalize(a: A, raw: []const u16) Error!?[]const u16 {
    var value = trim(raw);
    if (value.len == 0) return null;
    while (value.len > 0 and value[0] == '"') value = value[1..];
    while (value.len > 0 and value[value.len - 1] == '"') value = value[0 .. value.len - 1];
    // The managed oracle expands environment variables BEFORE Path.GetFullPath.
    // Environment.Windows passes this string to ExpandEnvironmentStringsW, so an
    // embedded NUL terminates the candidate here rather than invalidating it.
    // Keep zero/full strict for paths that do not pass through this expansion.
    const input = try a.dupeSentinel(u16, value, 0);
    const required = ExpandEnvironmentStringsW(input, null, 0);
    if (required == 0) return null;
    const expanded = try a.alloc(u16, required);
    const written = ExpandEnvironmentStringsW(input, expanded.ptr, required);
    if (written == 0 or written > required) return null;
    const result = full(a, expanded[0 .. written - 1]) catch |err| switch (err) {
        error.InvalidPath => return null,
        else => return err,
    };
    var end = result.len;
    while (end > 0 and (result[end - 1] == '\\' or result[end - 1] == '/')) : (end -= 1) {}
    return result[0..end];
}
pub fn same(a: A, left: []const u16, right: []const u16) Error!bool {
    const lhs = try normalize(a, left) orelse return false;
    const rhs = try normalize(a, right) orelse return false;
    return lhs.len > 0 and eq(lhs, rhs);
}
fn osPath(a: A, value: []const u16) Error![:0]u16 {
    if (value.len < 248 or std.mem.startsWith(u16, value, &.{ '\\', '\\', '?', '\\' })) return zero(a, value);
    const prefix: []const u16 = if (std.mem.startsWith(u16, value, &.{ '\\', '\\' })) &.{ '\\', '\\', '?', '\\', 'U', 'N', 'C', '\\' } else &.{ '\\', '\\', '?', '\\' };
    const suffix = if (prefix.len == 8) value[2..] else value;
    const output = try a.allocSentinel(u16, prefix.len + suffix.len, 0);
    @memcpy(output[0..prefix.len], prefix);
    @memcpy(output[prefix.len..], suffix);
    return output;
}
fn attributes(a: A, path: []const u16) Error!?u32 {
    const result = GetFileAttributesW(try osPath(a, path));
    if (result != std.math.maxInt(u32)) return result;
    return switch (GetLastError()) {
        2, 3 => null,
        5 => error.AccessDenied,
        else => error.Io,
    };
}
pub fn directory(a: A, path: []const u16) Error!bool {
    const attr = attributes(a, path) catch |err| return if (err == error.OutOfMemory) err else false;
    return if (attr) |v| v & 0x10 != 0 else false;
}
pub fn file(a: A, path: []const u16) Error!bool {
    const attr = attributes(a, path) catch |err| return if (err == error.OutOfMemory) err else false;
    return if (attr) |v| v & 0x10 == 0 else false;
}
pub fn parent(path: []const u16) ?[]const u16 {
    var end = path.len;
    while (end > 0 and (path[end - 1] == '\\' or path[end - 1] == '/')) : (end -= 1) {}
    if (end <= 3 and path.len >= 2 and path[1] == ':') return null;
    while (end > 0 and path[end - 1] != '\\' and path[end - 1] != '/') : (end -= 1) {}
    if (end == 0) return null;
    if (end == 3 and path[1] == ':') return path[0..3];
    if (path.len > 1 and path[0] == '\\' and path[1] == '\\') {
        var separators: usize = 0;
        var i: usize = 2;
        while (i < end) : (i += 1) {
            if (path[i] == '\\') separators += 1;
        }
        if (separators < 2) return null;
    }
    return path[0 .. end - 1];
}
fn rootCount(path: []const u16) usize {
    if (path.len >= 3 and path[1] == ':') return 3;
    if (path.len >= 4 and path[0] == '\\' and path[1] == '\\') {
        var start: usize = 2;
        if ((path[2] == '?' or path[2] == '.') and path[3] == '\\') {
            if (path.len >= 7 and path[5] == ':') return 7;
            if (path.len >= 8 and (path[4] == 'U' or path[4] == 'u') and (path[5] == 'N' or path[5] == 'n') and (path[6] == 'C' or path[6] == 'c') and path[7] == '\\') start = 8 else return 4;
        }
        var segments: usize = 0;
        var index = start;
        while (index < path.len and segments < 2) : (index += 1) if (path[index] == '\\' or path[index] == '/') {
            segments += 1;
        };
        return index;
    }
    return @min(path.len, 1);
}
pub fn rejectLinks(a: A, path: []const u16) Error!void {
    const complete = try full(a, path);
    var detail: u32 = 0;
    const status = @import("scalar_paths.zig").check(try zero(a, complete), @intCast(complete.len), @intCast(rootCount(complete)), false, &detail);
    return switch (status) {
        0 => {},
        3 => error.ReparsePoint,
        4 => error.HardLink,
        else => if (detail == 5) error.AccessDenied else error.Io,
    };
}
pub fn regular(a: A, path: []const u16) Error!bool {
    const complete = full(a, path) catch |err| return if (err == error.OutOfMemory) err else false;
    var detail: u32 = 0;
    return @import("scalar_paths.zig").check(try zero(a, complete), @intCast(complete.len), @intCast(rootCount(complete)), true, &detail) == 1;
}
pub const Enumeration = struct {
    handle: usize,
    data: FindData,
    first: bool = true,
    pub fn open(a: A, pattern: []const u16) Error!?Enumeration {
        var data: FindData = undefined;
        const handle = FindFirstFileW(try osPath(a, pattern), &data);
        if (handle == INVALID) return switch (GetLastError()) {
            2, 3 => null,
            5 => error.AccessDenied,
            else => error.Io,
        };
        return .{ .handle = handle, .data = data };
    }
    pub fn next(self: *Enumeration) Error!?*const FindData {
        while (true) {
            if (self.first) self.first = false else if (FindNextFileW(self.handle, &self.data) == 0) {
                if (GetLastError() == 18) return null;
                return error.Io;
            }
            const name = nameOf(&self.data);
            if (std.mem.eql(u16, name, &.{'.'}) or std.mem.eql(u16, name, &.{ '.', '.' })) continue;
            return &self.data;
        }
    }
    pub fn close(self: *Enumeration) void {
        _ = FindClose(self.handle);
    }
};
pub fn nameOf(data: *const FindData) []const u16 {
    return std.mem.sliceTo(&data.name, 0);
}
pub fn timestamp(data: *const FindData) u64 {
    return (@as(u64, data.write.high) << 32) | data.write.low;
}
pub fn read(a: A, path: []const u16, attempt_limit: u32, delay_ms: u32, attempts: *u32, last_status: *i32) Error!?[]const u8 {
    if (!try file(a, path)) return null;
    try rejectLinks(a, path);
    var native_name: Unicode = .{ .length = 0, .maximum = 0, .buffer = null };
    const converted = RtlDosPathNameToNtPathName_U_WithStatus(try osPath(a, path), &native_name, null, null);
    if (converted < 0) return error.Io;
    defer RtlFreeUnicodeString(&native_name);
    var object: Object = .{ .name = &native_name };
    var handle: usize = 0;
    var io: Io = .{};
    while (true) {
        attempts.* += 1;
        last_status.* = NtCreateFile(&handle, 0x80100000, &object, &io, null, 0, 7, 1, 0x400060, null, 0);
        if (last_status.* >= 0) break;
        if (@as(u32, @bitCast(last_status.*)) != 0xc0000034 or attempts.* >= attempt_limit) return error.Io;
        Sleep(delay_ms);
    }
    defer _ = CloseHandle(handle);
    var size: i64 = 0;
    if (GetFileSizeEx(handle, &size) == 0 or size < 0 or size > std.math.maxInt(u32)) return error.Io;
    const buffer = try a.alloc(u8, @intCast(size));
    var offset: usize = 0;
    while (offset < buffer.len) {
        var count: u32 = 0;
        if (ReadFile(handle, buffer[offset..].ptr, @intCast(buffer.len - offset), &count, null) == 0 or count == 0) return error.Io;
        offset += count;
    }
    return buffer;
}
// RuntimeRead: EventDriven("Optional dependency state, installed software inventory or explicit refresh changed")
pub fn commonRoots(a: A) Error![]const []const u16 {
    var roots: std.ArrayList([]const u16) = .empty;
    for ([_]i32{ 0x26, 0x2a }) |kind| {
        var path: [260]u16 = @splat(0);
        if (SHGetFolderPathW(null, kind, null, 0, &path) >= 0 and path[0] != 0) try roots.append(a, try a.dupe(u16, std.mem.sliceTo(&path, 0)));
    }
    const mask = GetLogicalDrives();
    var drive: u5 = 0;
    while (@as(u32, drive) < 26) : (drive += 1) {
        if (mask & (@as(u32, 1) << drive) == 0) continue;
        const root: [3:0]u16 = .{ @as(u16, 'A') + drive, ':', '\\' };
        if (GetDriveTypeW(&root) != 3 or !try directory(a, &root)) continue;
        for ([_][]const u8{ "Software", "Tools", "Apps", "Program Files", "Program Files (x86)" }) |name| try roots.append(a, try combine(a, &root, try text(a, name)));
    }
    return roots.items;
}

const Unicode = extern struct { length: u16, maximum: u16, buffer: ?[*]u16 };
const Object = extern struct { length: u32 = @sizeOf(Object), root: usize = 0, name: *Unicode, attributes: u32 = 0x40, security: ?*anyopaque = null, qos: ?*anyopaque = null };
const Io = extern struct { status: usize = 0, information: usize = 0 };
extern "ntdll" fn RtlDosPathNameToNtPathName_U_WithStatus([*:0]const u16, *Unicode, ?*anyopaque, ?*anyopaque) callconv(.winapi) i32;
extern "ntdll" fn RtlFreeUnicodeString(*Unicode) callconv(.winapi) void;
extern "ntdll" fn NtCreateFile(*usize, u32, *Object, *Io, ?*i64, u32, u32, u32, u32, ?*anyopaque, u32) callconv(.winapi) i32;
extern "kernel32" fn Sleep(u32) callconv(.winapi) void;
