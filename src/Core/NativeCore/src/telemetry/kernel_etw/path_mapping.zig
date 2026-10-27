const std = @import("std");

extern "kernel32" fn GetEnvironmentVariableW(name: [*:0]const u16, output: [*]u16, capacity: u32) callconv(.winapi) u32;
extern "kernel32" fn GetLogicalDrives() callconv(.winapi) u32;
extern "kernel32" fn QueryDosDeviceW(name: [*:0]const u16, output: [*]u16, capacity: u32) callconv(.winapi) u32;
extern "kernel32" fn CompareStringOrdinal(a: [*]const u16, alen: i32, b: [*]const u16, blen: i32, ignore_case: i32) callconv(.winapi) i32;

fn wide(comptime value: []const u8) []const u16 {
    return std.unicode.utf8ToUtf16LeStringLiteral(value);
}
const Entry = struct { kernel: []u16, user: []u16 };

/// TraceEvent 3.2.4 KernelToUserDriveMapping's local-machine defaults, in
/// insertion order (first prefix wins). All retained strings are owner-budgeted.
/// The six default prefixes plus the 26 Windows drive letters need at most 32
/// entries. Recorded SysConfig mapping overrides remain a production-cutover gate.
pub const PathMap = struct {
    allocator: std.mem.Allocator,
    entries: [32]Entry = undefined,
    len: usize = 0,
    local: bool = false,
    failed_drive_mask: u32 = 0,
    drive_query_failed: bool = false,

    pub fn init(allocator: std.mem.Allocator) PathMap {
        return .{ .allocator = allocator };
    }
    pub fn deinit(self: *PathMap) void {
        for (self.entries[0..self.len]) |entry| {
            self.allocator.free(entry.kernel);
            self.allocator.free(entry.user);
        }
        self.len = 0;
    }
    pub fn add(self: *PathMap, kernel: []const u16, user: []const u16) !void {
        if (self.len == self.entries.len) return error.CapacityExceeded;
        const key = try self.allocator.dupe(u16, kernel);
        errdefer self.allocator.free(key);
        const value = try self.allocator.dupe(u16, user);
        self.entries[self.len] = .{ .kernel = key, .user = value };
        self.len += 1;
    }
    fn environment(a: std.mem.Allocator, comptime name: []const u8, cap: u32) ![]const u16 {
        const buffer = try a.alloc(u16, cap);
        const length = GetEnvironmentVariableW(std.unicode.utf8ToUtf16LeStringLiteral(name), buffer.ptr, cap);
        if (length == 0) return error.WindowsPathUnavailable;
        if (length >= cap) return error.CapacityExceeded;
        return buffer[0..length];
    }
    pub fn initLocal(allocator: std.mem.Allocator, max_path_units: u32) !PathMap {
        var self = PathMap.init(allocator);
        errdefer self.deinit();
        var round = std.heap.ArenaAllocator.init(allocator);
        defer round.deinit();
        const a = round.allocator();
        try self.add(wide("\\??\\"), &.{});
        const root = try environment(a, "SystemRoot", max_path_units);
        try self.add(wide("\\SystemRoot\\"), try std.mem.concat(a, u16, &.{ root, wide("\\") }));
        // Match the legacy query's explicit 2048-unit bound and A-to-Z order.
        const target = try a.alloc(u16, 2048);
        const drives = GetLogicalDrives();
        self.drive_query_failed = drives == 0;
        for (0..26) |i| {
            const bit: u32 = @as(u32, 1) << @as(u5, @intCast(i));
            if (drives & bit == 0) continue;
            var name: [3:0]u16 = .{ @as(u16, 'A') + @as(u16, @intCast(i)), ':', 0 };
            const count = QueryDosDeviceW(&name, target.ptr, 2048);
            if (count == 0) {
                // Legacy leaves this path unmapped. Make that missing fact
                // readable without disabling unrelated, valid path mappings.
                self.failed_drive_mask |= bit;
                continue;
            }
            const end = std.mem.indexOfScalar(u16, target[0..@min(count, target.len)], 0) orelse return error.InvalidPath;
            try self.add(try std.mem.concat(a, u16, &.{ target[0..end], wide("\\") }), try std.mem.concat(a, u16, &.{ name[0..2], wide("\\") }));
        }
        try self.add(wide("\\Device\\LanmanRedirector\\"), wide("\\\\"));
        try self.add(wide("\\Device\\Mup\\"), wide("\\\\"));
        const windows = try environment(a, "windir", max_path_units);
        try self.add(wide("\\Windows\\"), try std.mem.concat(a, u16, &.{ windows, wide("\\") }));
        try self.add(wide("\\\\"), wide("\\\\"));
        self.local = true;
        return self;
    }
    pub fn map(self: *const PathMap, round: std.mem.Allocator, path: []const u16, cap: usize) ![]const u16 {
        for (self.entries[0..self.len]) |entry| {
            if (path.len < entry.kernel.len or CompareStringOrdinal(path.ptr, @intCast(entry.kernel.len), entry.kernel.ptr, @intCast(entry.kernel.len), 1) != 2) continue;
            const length = std.math.add(usize, entry.user.len, path.len - entry.kernel.len) catch return error.CapacityExceeded;
            if (length > cap) return error.CapacityExceeded;
            return std.mem.concat(round, u16, &.{ entry.user, path[entry.kernel.len..] });
        }
        if (path.len > cap) return error.CapacityExceeded;
        return path;
    }
};

test "kernel path mapping preserves first prefix, casing, and one-pass semantics" {
    var map = PathMap.init(std.testing.allocator);
    defer map.deinit();
    try map.add(wide("\\??\\"), &.{});
    try map.add(wide("\\SystemRoot\\"), wide("C:\\WINDOWS\\"));
    try map.add(wide("\\Device\\Volume1\\"), wide("D:\\"));
    try map.add(wide("\\Device\\Volume1\\nested\\"), wide("E:\\"));
    try map.add(wide("\\Device\\Mup\\"), wide("\\\\"));
    var round = std.heap.ArenaAllocator.init(std.testing.allocator);
    defer round.deinit();
    const cases = .{
        .{ "\\systemroot\\system32\\ntoskrnl.exe", "C:\\WINDOWS\\system32\\ntoskrnl.exe" },
        .{ "\\??\\C:\\driver.sys", "C:\\driver.sys" },
        .{ "\\Device\\Volume1\\nested\\driver.sys", "D:\\nested\\driver.sys" },
        .{ "\\Device\\Mup\\server\\driver.sys", "\\\\server\\driver.sys" },
        .{ "\\??\\\\SystemRoot\\driver.sys", "\\SystemRoot\\driver.sys" },
        .{ "unmapped.sys", "unmapped.sys" },
    };
    inline for (cases) |pair| try std.testing.expectEqualSlices(u16, wide(pair[1]), try map.map(round.allocator(), wide(pair[0]), 4096));
    try std.testing.expectError(error.CapacityExceeded, map.map(round.allocator(), wide("\\SystemRoot\\x"), 1));
}

fn allocationRound(a: std.mem.Allocator) !void {
    var map = PathMap.init(a);
    defer map.deinit();
    try map.add(wide("kernel"), wide("user"));
}
test "kernel path mapping fails boundedly and releases partial allocations" {
    try std.testing.checkAllAllocationFailures(std.testing.allocator, allocationRound, .{});
    try std.testing.expectError(error.CapacityExceeded, PathMap.initLocal(std.testing.allocator, 1));
    var local = try PathMap.initLocal(std.testing.allocator, 4096);
    defer local.deinit();
    try std.testing.expect(local.local);
    try std.testing.expect(local.len >= 6 and local.len <= 32);
    var map = PathMap.init(std.testing.allocator);
    defer map.deinit();
    for (0..32) |_| try map.add(wide("k"), wide("u"));
    try std.testing.expectError(error.CapacityExceeded, map.add(wide("overflow"), wide("overflow")));
}
