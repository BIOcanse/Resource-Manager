const std = @import("std");
const m = @import("model.zig");
const Handle = *anyopaque;
const FileTime = extern struct { low: u32, high: u32 };
const FindData = extern struct {
    attributes: u32,
    creation: FileTime,
    access: FileTime,
    write: FileTime,
    size_high: u32,
    size_low: u32,
    reserved0: u32,
    reserved1: u32,
    name: [260]u16,
    alternate_name: [14]u16,
};
extern "kernel32" fn FindFirstFileW([*:0]const u16, *FindData) callconv(.winapi) ?Handle;
extern "kernel32" fn FindNextFileW(Handle, *FindData) callconv(.winapi) i32;
extern "kernel32" fn FindClose(Handle) callconv(.winapi) i32;
extern "kernel32" fn GetLastError() callconv(.winapi) u32;
pub const Config = struct {
    root: []const u8,
    /// Explicit display-driver report directories only (e.g. WATCHDOG). No generic
    /// LiveKernelReports tree classification and no dump-content reads.
    display_directories: []const []const u8,
    after_ms: i64,
    max_entries: usize,
};
/// Fixed startup/display request read, never a background poller. Existing dumps
/// remain owned by Windows. Path/timestamp is display evidence, not a log event.
// RuntimeRead: EventDriven("Service start or display-driver event")
pub fn scan(allocator: std.mem.Allocator, config: Config, sink: m.LiveKernelSink) !void {
    if (config.max_entries == 0) return error.InvalidLimits;
    var seen: usize = 0;
    for (config.display_directories) |directory| {
        if (directory.len == 0 or std.mem.findAny(u8, directory, "\\/:*?.") != null) return error.InvalidDirectory;
        const pattern = try std.fmt.allocPrint(allocator, "{s}\\{s}\\*.dmp", .{ config.root, directory });
        defer allocator.free(pattern);
        const wide = try std.unicode.utf8ToUtf16LeAllocZ(allocator, pattern);
        defer allocator.free(wide);
        var data: FindData = undefined;
        const handle = FindFirstFileW(wide.ptr, &data);
        if (handle == null or @intFromPtr(handle.?) == std.math.maxInt(usize)) {
            const code = GetLastError();
            if (code == 2 or code == 3) continue;
            if (code == 5) return error.AccessDenied;
            return error.ScanFailed;
        }
        defer _ = FindClose(handle.?);
        while (true) {
            seen += 1;
            if (seen > config.max_entries) return error.EntryLimitExceeded;
            if (data.attributes & 0x10 == 0) {
                const ticks = (@as(u64, data.write.high) << 32) | data.write.low;
                const ms: i64 = @as(i64, @intCast(ticks / 10000)) - 11644473600000;
                if (ms > config.after_ms) {
                    const name_len = std.mem.findScalar(u16, &data.name, 0) orelse return error.InvalidFileName;
                    const name = try std.unicode.utf16LeToUtf8Alloc(allocator, data.name[0..name_len]);
                    defer allocator.free(name);
                    const path = try std.fmt.allocPrint(allocator, "{s}\\{s}\\{s}", .{ config.root, directory, name });
                    defer allocator.free(path);
                    try sink.display(sink.context, .{ .path = path, .timestamp_ms = ms });
                }
            }
            if (FindNextFileW(handle.?, &data) == 0) {
                if (GetLastError() != 18) return error.ScanFailed;
                break;
            }
        }
    }
}
