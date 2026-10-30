const std = @import("std");
const d = @import("thread_diagnostics.zig");
const FileTime = extern struct { low: u32, high: u32 };
const ModuleInfo = extern struct { base: ?*anyopaque, size: u32, entry: ?*anyopaque };
extern "kernel32" fn OpenProcess(u32, i32, u32) callconv(.winapi) ?*anyopaque;
extern "kernel32" fn GetProcessTimes(*anyopaque, *FileTime, *FileTime, *FileTime, *FileTime) callconv(.winapi) i32;
extern "kernel32" fn GetExitCodeProcess(*anyopaque, *u32) callconv(.winapi) i32;
extern "kernel32" fn OpenThread(u32, i32, u32) callconv(.winapi) ?*anyopaque;
extern "kernel32" fn GetProcessIdOfThread(*anyopaque) callconv(.winapi) u32;
extern "kernel32" fn GetThreadTimes(*anyopaque, *FileTime, *FileTime, *FileTime, *FileTime) callconv(.winapi) i32;
extern "kernel32" fn GetThreadDescription(*anyopaque, *?[*:0]u16) callconv(.winapi) i32;
extern "kernel32" fn GetSystemTimePreciseAsFileTime(*FileTime) callconv(.winapi) void;
extern "kernel32" fn LocalFree(*anyopaque) callconv(.winapi) ?*anyopaque;
extern "kernel32" fn CloseHandle(*anyopaque) callconv(.winapi) i32;
extern "kernel32" fn GetLastError() callconv(.winapi) u32;
extern "kernel32" fn SetLastError(u32) callconv(.winapi) void;
extern "kernel32" fn K32EnumProcessModulesEx(*anyopaque, [*]?*anyopaque, u32, *u32, u32) callconv(.winapi) i32;
extern "kernel32" fn K32GetModuleInformation(*anyopaque, *anyopaque, *ModuleInfo, u32) callconv(.winapi) i32;
extern "kernel32" fn K32GetModuleFileNameExW(*anyopaque, *anyopaque, [*]u16, u32) callconv(.winapi) u32;
extern "ntdll" fn NtQueryInformationThread(*anyopaque, u32, *u64, u32, ?*u32) callconv(.winapi) i32;
extern "ntdll" fn NtQuerySystemInformation(u32, [*]u8, u32, *u32) callconv(.winapi) i32;
extern "ntdll" fn RtlNtStatusToDosError(i32) callconv(.winapi) u32;

pub const source: d.Source = .{ .open_process = processOpen, .process_birth = processBirth, .process_running = processRunning,
    .open_thread = threadOpen, .times = threadTimes, .name = name, .start = start, .module = modulePath,
    .inventory = inventory, .close = close, .now = now, .last_error = lastError };
fn join(value: FileTime) i64 { return @bitCast(@as(u64, value.high) << 32 | value.low); }
fn processOpen(_: ?*anyopaque, pid: u32, modules: bool) ?*anyopaque { return OpenProcess(if (modules) 0x0410 else 0x101000, 0, pid); }
fn processBirth(_: ?*anyopaque, handle: *anyopaque) ?i64 {
    var c: FileTime = undefined; var e: FileTime = undefined; var k: FileTime = undefined; var u: FileTime = undefined;
    if (GetProcessTimes(handle, &c, &e, &k, &u) == 0) return null;
    return join(c);
}
fn processRunning(_: ?*anyopaque, handle: *anyopaque) bool { var exit: u32 = 0; return GetExitCodeProcess(handle, &exit) != 0 and exit == 259; }
fn threadOpen(_: ?*anyopaque, pid: u32, tid: u32) ?*anyopaque {
    const handle = OpenThread(0x0040, 0, tid) orelse return null;
    if (GetProcessIdOfThread(handle) != pid) { _ = CloseHandle(handle); SetLastError(1168); return null; }
    return handle;
}
fn threadTimes(_: ?*anyopaque, handle: *anyopaque) ?d.Times {
    var c: FileTime = undefined; var e: FileTime = undefined; var k: FileTime = undefined; var u: FileTime = undefined;
    if (GetThreadTimes(handle, &c, &e, &k, &u) == 0) return null;
    return .{ .birth = join(c), .exit = join(e), .kernel = join(k), .user = join(u) };
}
fn name(_: ?*anyopaque, handle: *anyopaque, allocator: std.mem.Allocator, max_units: u32) !d.Name {
    var text: ?[*:0]u16 = null;
    const result = GetThreadDescription(handle, &text);
    defer if (text) |value| { _ = LocalFree(@ptrCast(value)); };
    if (result < 0) return .{ .hresult = result };
    const value = text orelse return .{};
    var length: usize = 0;
    while (length <= max_units and value[length] != 0) : (length += 1) {}
    if (length > max_units) return error.CapacityExceeded;
    return .{ .text = if (length == 0) null else try allocator.dupe(u16, value[0..length]) };
}
fn start(_: ?*anyopaque, handle: *anyopaque) ?u64 {
    var address: u64 = 0;
    return if (NtQueryInformationThread(handle, 9, &address, @sizeOf(u64), null) == 0) address else null;
}
fn modulePath(_: ?*anyopaque, process: *anyopaque, address: u64, allocator: std.mem.Allocator, scratch: std.mem.Allocator, max_units: u32, max_modules: u32) !?[]u16 {
    const modules = try scratch.alloc(?*anyopaque, max_modules);
    defer scratch.free(modules);
    var needed: u32 = 0;
    if (K32EnumProcessModulesEx(process, modules.ptr, @intCast(modules.len * @sizeOf(?*anyopaque)), &needed, 3) == 0) return null;
    if (needed > modules.len * @sizeOf(?*anyopaque)) return error.CapacityExceeded;
    for (modules[0..needed / @sizeOf(?*anyopaque)]) |optional| {
        const handle = optional orelse continue;
        var info: ModuleInfo = undefined;
        if (K32GetModuleInformation(process, handle, &info, @sizeOf(ModuleInfo)) == 0) continue;
        const base = @intFromPtr(info.base);
        if (address < base or address - base >= info.size) continue;
        const buffer = try scratch.alloc(u16, max_units + 1);
        defer scratch.free(buffer);
        const length = K32GetModuleFileNameExW(process, handle, buffer.ptr, @intCast(buffer.len));
        if (length == 0) return null;
        if (length >= buffer.len) return error.CapacityExceeded;
        return try allocator.dupe(u16, buffer[0..length]);
    }
    return null;
}
fn inventory(_: ?*anyopaque, pid: u32, birth: i64, allocator: std.mem.Allocator, cap: usize, visit: d.Visit, context: *anyopaque) !void {
    var length: usize = 65536;
    while (length <= cap) {
        const buffer = try allocator.alloc(u8, length);
        defer allocator.free(buffer);
        var needed: u32 = 0;
        const status = NtQuerySystemInformation(5, buffer.ptr, @intCast(buffer.len), &needed);
        if (@as(u32, @bitCast(status)) == 0xc0000004) {
            const doubled = try std.math.mul(usize, length, 2);
            length = @max(doubled, needed);
            continue;
        }
        if (status < 0) { SetLastError(RtlNtStatusToDosError(status)); return error.SystemInformationUnavailable; }
        const data = buffer[0..@min(buffer.len, needed)];
        var offset: usize = 0;
        while (offset < data.len) {
            if (data.len - offset < 256) return error.InvalidSystemInformation;
            const row = data[offset..];
            const next = std.mem.readInt(u32, row[0..4], .little);
            const count = std.mem.readInt(u32, row[4..8], .little);
            const record_length: usize = if (next == 0) row.len else next;
            if (record_length < 256 or record_length > row.len or count > (record_length - 256) / 80) return error.InvalidSystemInformation;
            if (std.mem.readInt(u64, row[80..88], .little) == pid) {
                if (std.mem.readInt(i64, row[32..40], .little) != birth) return;
                for (0..count) |index| {
                    const thread = row[256 + index * 80..][0..80];
                    if (std.mem.readInt(u64, thread[40..48], .little) != pid) return error.InvalidSystemInformation;
                    const tid = std.math.cast(u32, std.mem.readInt(u64, thread[48..56], .little)) orelse return error.InvalidSystemInformation;
                    try visit(context, tid, std.mem.readInt(i64, thread[16..24], .little), std.mem.readInt(u64, thread[32..40], .little));
                }
                return;
            }
            if (next == 0) return;
            if (next % 8 != 0) return error.InvalidSystemInformation;
            offset += next;
        }
        return;
    }
    return error.CapacityExceeded;
}
fn close(_: ?*anyopaque, handle: *anyopaque) void { _ = CloseHandle(handle); }
// RuntimeRead: Sampled
fn now(_: ?*anyopaque) i64 { var value: FileTime = undefined; GetSystemTimePreciseAsFileTime(&value); return join(value); }
fn lastError(_: ?*anyopaque) i32 { return @bitCast(GetLastError()); }
