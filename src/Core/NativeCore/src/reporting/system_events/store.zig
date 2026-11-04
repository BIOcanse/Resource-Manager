//! File-backed report rows. Reads and writes are scoped; history is never evicted.
const std = @import("std");
const w = std.os.windows;
extern "kernel32" fn CreateFileW([*:0]const u16, u32, u32, ?*anyopaque, u32, u32, ?*anyopaque) callconv(.winapi) ?*anyopaque;
extern "kernel32" fn ReadFile(?*anyopaque, [*]u8, u32, *u32, ?*anyopaque) callconv(.winapi) i32;
extern "kernel32" fn WriteFile(?*anyopaque, [*]const u8, u32, *u32, ?*anyopaque) callconv(.winapi) i32;
extern "kernel32" fn GetFileSizeEx(?*anyopaque, *i64) callconv(.winapi) i32;
extern "kernel32" fn FlushFileBuffers(?*anyopaque) callconv(.winapi) i32;
extern "kernel32" fn CloseHandle(?*anyopaque) callconv(.winapi) i32;
extern "kernel32" fn MoveFileExW([*:0]const u16, [*:0]const u16, u32) callconv(.winapi) i32;
extern "kernel32" fn CreateDirectoryW([*:0]const u16, ?*anyopaque) callconv(.winapi) i32;
extern "kernel32" fn DeleteFileW([*:0]const u16) callconv(.winapi) i32;
extern "kernel32" fn FindFirstFileW([*:0]const u16, *FindData) callconv(.winapi) ?*anyopaque;
extern "kernel32" fn FindNextFileW(?*anyopaque, *FindData) callconv(.winapi) i32;
extern "kernel32" fn FindClose(?*anyopaque) callconv(.winapi) i32;
extern "kernel32" fn GetLastError() callconv(.winapi) u32;
extern "kernel32" fn GetFileAttributesW([*:0]const u16) callconv(.winapi) u32;
const FindData = extern struct {
    attributes: u32,
    created: w.FILETIME,
    accessed: w.FILETIME,
    written: w.FILETIME,
    size_high: u32,
    size_low: u32,
    reserved0: u32,
    reserved1: u32,
    name: [260]u16,
    alternate: [14]u16,
};
fn invalid(handle: ?*anyopaque) bool {
    return handle == null or @intFromPtr(handle.?) == std.math.maxInt(usize);
}
pub fn wide(a: std.mem.Allocator, path: []const u8) ![:0]u16 {
    return std.unicode.utf8ToUtf16LeAllocZ(a, path);
}
pub fn join(a: std.mem.Allocator, root: []const u8, leaf: []const u8) ![]const u8 {
    return std.fmt.allocPrint(a, "{s}\\{s}", .{ root, leaf });
}
pub fn mkdir(a: std.mem.Allocator, path: []const u8) !void {
    const p = try wide(a, path);
    defer a.free(p);
    if (CreateDirectoryW(p, null) == 0 and GetLastError() != 183) return error.CreateDirectoryFailed;
}
pub fn exists(a: std.mem.Allocator, path: []const u8) !bool {
    const p = try wide(a, path);
    defer a.free(p);
    if (GetFileAttributesW(p) != 0xffffffff) return true;
    if (GetLastError() == 2 or GetLastError() == 3) return false;
    return error.StatFailed;
}
// RuntimeRead: EventDriven("Report request, startup restore or event transaction")
pub fn read(a: std.mem.Allocator, path: []const u8, cap: usize) !?[]const u8 {
    const p = try wide(a, path);
    defer a.free(p);
    const file = CreateFileW(p, 0x80000000, 7, null, 3, 0x80, null);
    if (invalid(file)) {
        if (GetLastError() == 2 or GetLastError() == 3) return null;
        return error.OpenFailed;
    }
    defer _ = CloseHandle(file);
    var size: i64 = 0;
    if (GetFileSizeEx(file, &size) == 0 or size < 0 or size > cap) return error.DocumentCapacityExceeded;
    const result = try a.alloc(u8, @intCast(size));
    errdefer a.free(result);
    var n: usize = 0;
    while (n < result.len) {
        var got: u32 = 0;
        if (ReadFile(file, result[n..].ptr, @intCast(result.len - n), &got, null) == 0 or got == 0) return error.ReadFailed;
        n += got;
    }
    return result;
}
/// Write-through temporary + durable rename; the old report survives failed writes.
pub fn atomic(a: std.mem.Allocator, path: []const u8, bytes: []const u8) !void {
    const temp = try std.fmt.allocPrint(a, "{s}.tmp", .{path});
    defer a.free(temp);
    const p = try wide(a, path);
    defer a.free(p);
    const t = try wide(a, temp);
    defer a.free(t);
    const file = CreateFileW(t, 0x40000000, 0, null, 2, 0x80000080, null);
    if (invalid(file)) return error.OpenFailed;
    var closed = false;
    defer {
        if (!closed) _ = CloseHandle(file);
    }
    var n: usize = 0;
    while (n < bytes.len) {
        var put: u32 = 0;
        if (WriteFile(file, bytes[n..].ptr, @intCast(@min(bytes.len - n, std.math.maxInt(u32))), &put, null) == 0 or put == 0) return error.WriteFailed;
        n += put;
    }
    if (FlushFileBuffers(file) == 0) return error.FlushFailed;
    _ = CloseHandle(file);
    closed = true;
    if (MoveFileExW(t, p, 1 | 8) == 0) return error.ReplaceFailed;
}
pub fn remove(a: std.mem.Allocator, path: []const u8) !void {
    const p = try wide(a, path);
    defer a.free(p);
    if (DeleteFileW(p) == 0) return error.DeleteFailed;
}
pub const Visitor = *const fn (?*anyopaque, []const u8) anyerror!void;
/// Only direct children matching *.json; callers never recursively delete directories.
// RuntimeRead: EventDriven("Report request, history deadline or startup review")
pub fn each(a: std.mem.Allocator, dir: []const u8, context: ?*anyopaque, visit: Visitor) !void {
    const glob = try join(a, dir, "*.json");
    defer a.free(glob);
    const p = try wide(a, glob);
    defer a.free(p);
    var data: FindData = undefined;
    const search = FindFirstFileW(p, &data);
    if (invalid(search)) {
        if (GetLastError() == 2 or GetLastError() == 3) return;
        return error.EnumerateFailed;
    }
    defer _ = FindClose(search);
    while (true) {
        if (data.attributes & 0x10 == 0) {
            const name = try std.unicode.utf16LeToUtf8Alloc(a, std.mem.sliceTo(&data.name, 0));
            defer a.free(name);
            // Never follow a directory/reparse point while consuming report files.
            if (data.attributes & 0x400 != 0) return error.ReparsePoint;
            const path = try join(a, dir, name);
            defer a.free(path);
            try visit(context, path);
        }
        if (FindNextFileW(search, &data) == 0) {
            if (GetLastError() != 18) return error.EnumerateFailed;
            break;
        }
    }
}
