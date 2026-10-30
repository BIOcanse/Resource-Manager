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
/// insertion order (first case-insensitive prefix wins). Retained strings and
/// the entry array use the injected owner allocator, normally its Budget.
pub const PathMap = struct {
    pub const default_entry_cap: usize = 32;
    pub const namespace_entry_cap: usize = 512;

    allocator: std.mem.Allocator,
    entries: std.ArrayListUnmanaged(Entry) = .empty,
    max_entries: usize = default_entry_cap,
    max_path_units: usize = 32768,
    local: bool = false,
    recorded: bool = false,
    deferred_local: bool = false,
    failed_drive_mask: u32 = 0,
    drive_query_failed: bool = false,
    system_drive: ?[2]u16 = null,

    /// Compatibility initializer for explicit maps and existing retrospective
    /// query tests. Local defaults are not loaded unless initLocal is requested.
    pub fn init(allocator: std.mem.Allocator) PathMap {
        return .{ .allocator = allocator };
    }

    /// Creates the production map with local-machine defaults deferred until
    /// the first path conversion. SysConfig records added before that point
    /// suppress local defaults, matching TraceEvent's empty-map check.
    pub fn initDeferred(allocator: std.mem.Allocator, max_path_units: u32) !PathMap {
        return initDeferredWithLimits(allocator, max_path_units, namespace_entry_cap);
    }

    pub fn initDeferredWithLimits(allocator: std.mem.Allocator, max_path_units: u32, max_entries: usize) !PathMap {
        if (max_path_units == 0 or max_entries == 0) return error.InvalidConfiguration;
        return .{
            .allocator = allocator,
            .max_path_units = max_path_units,
            .max_entries = max_entries,
            .deferred_local = true,
        };
    }

    pub fn deinit(self: *PathMap) void {
        const allocator = self.allocator;
        for (self.entries.items) |entry| {
            allocator.free(entry.kernel);
            allocator.free(entry.user);
        }
        self.entries.deinit(allocator);
        self.entries = .empty;
    }

    pub fn count(self: *const PathMap) usize {
        return self.entries.items.len;
    }

    pub fn add(self: *PathMap, kernel: []const u16, user: []const u16) !void {
        if (kernel.len == 0) return error.InvalidPath;
        if (kernel.len > self.max_path_units or user.len > self.max_path_units) return error.CapacityExceeded;
        if (self.entries.items.len >= self.max_entries) return error.CapacityExceeded;

        const key = try self.allocator.dupe(u16, kernel);
        errdefer self.allocator.free(key);
        const value = try self.allocator.dupe(u16, user);
        errdefer self.allocator.free(value);
        try self.entries.append(self.allocator, .{ .kernel = key, .user = value });
    }

    fn addOwned(self: *PathMap, kernel: []u16, user: []u16) !void {
        errdefer self.allocator.free(kernel);
        errdefer self.allocator.free(user);
        if (kernel.len == 0) return error.InvalidPath;
        if (kernel.len > self.max_path_units or user.len > self.max_path_units) return error.CapacityExceeded;
        if (self.entries.items.len >= self.max_entries) return error.CapacityExceeded;
        try self.entries.append(self.allocator, .{ .kernel = kernel, .user = user });
    }

    fn copyUtf16Le(self: *PathMap, bytes: []const u8) ![]u16 {
        if (bytes.len % 2 != 0) return error.InvalidPath;
        const units = bytes.len / 2;
        if (units > self.max_path_units) return error.CapacityExceeded;
        const result = try self.allocator.alloc(u16, units);
        for (result, 0..) |*unit, index| {
            unit.* = std.mem.readInt(u16, bytes[index * 2 ..][0..2], .little);
        }
        return result;
    }

    /// Mirrors KernelToUserDriveMapping.AddSystemDrive: append the
    /// `\Windows\` mapping and remember the first two UTF-16 units of the
    /// event's SystemWindowsDirectory. TraceEvent stores SystemDirectory in
    /// the record but does not use it in its mapping callback.
    pub fn addSystemPathsUtf16Le(self: *PathMap, system_directory: []const u8, system_windows_directory: []const u8) !void {
        if (system_directory.len % 2 != 0) return error.InvalidPath;
        if (system_directory.len / 2 > self.max_path_units) return error.CapacityExceeded;
        const windows = try self.copyUtf16Le(system_windows_directory);
        if (windows.len < 2) {
            self.allocator.free(windows);
            return error.InvalidPath;
        }
        const key = self.allocator.dupe(u16, wide("\\Windows\\")) catch |err| {
            self.allocator.free(windows);
            return err;
        };
        try self.addOwned(key, windows);
        self.system_drive = .{ windows[0], windows[1] };
        self.deferred_local = false;
        self.recorded = true;
    }

    /// Adds one exact SysConfig VolumeMapping pair in event order.
    pub fn addVolumeMappingUtf16Le(self: *PathMap, nt_path: []const u8, dos_path: []const u8) !void {
        const key = try self.copyUtf16Le(nt_path);
        const value = self.copyUtf16Le(dos_path) catch |err| {
            self.allocator.free(key);
            return err;
        };
        try self.addOwned(key, value);
        self.deferred_local = false;
        self.recorded = true;
    }

    // RuntimeRead: EventDriven("Process environment changed")
    fn environment(a: std.mem.Allocator, comptime name: []const u8, cap: u32) ![]const u16 {
        if (cap == 0) return error.CapacityExceeded;
        const buffer = try a.alloc(u16, cap);
        const length = GetEnvironmentVariableW(std.unicode.utf8ToUtf16LeStringLiteral(name), buffer.ptr, cap);
        if (length == 0) return error.WindowsPathUnavailable;
        if (length >= cap) return error.CapacityExceeded;
        return buffer[0..length];
    }

    // RuntimeRead: EventDriven("Windows drive topology or DOS device mapping changed")
    fn populateLocal(self: *PathMap, scratch: std.mem.Allocator) !void {
        try self.add(wide("\\??\\"), &.{});
        const root = try environment(scratch, "SystemRoot", @intCast(self.max_path_units));
        try self.add(wide("\\SystemRoot\\"), try std.mem.concat(scratch, u16, &.{ root, wide("\\") }));

        // Match TraceEvent's explicit 2048-unit query bound and A-to-Z order.
        const target = try scratch.alloc(u16, 2048);
        const drives = GetLogicalDrives();
        self.drive_query_failed = drives == 0;
        for (0..26) |i| {
            const bit: u32 = @as(u32, 1) << @as(u5, @intCast(i));
            if (drives & bit == 0) continue;
            var name: [3:0]u16 = .{ @as(u16, 'A') + @as(u16, @intCast(i)), ':', 0 };
            const query_count = QueryDosDeviceW(&name, target.ptr, 2048);
            if (query_count == 0) {
                // Keep a readable record of missing local topology while
                // preserving unrelated valid mappings.
                self.failed_drive_mask |= bit;
                continue;
            }
            const bounded_count = @min(@as(usize, query_count), target.len);
            const end = std.mem.indexOfScalar(u16, target[0..bounded_count], 0) orelse return error.InvalidPath;
            try self.add(
                try std.mem.concat(scratch, u16, &.{ target[0..end], wide("\\") }),
                try std.mem.concat(scratch, u16, &.{ name[0..2], wide("\\") }),
            );
        }
        try self.add(wide("\\Device\\LanmanRedirector\\"), wide("\\\\"));
        try self.add(wide("\\Device\\Mup\\"), wide("\\\\"));
        const windows = try environment(scratch, "windir", @intCast(self.max_path_units));
        const with_separator = try std.mem.concat(scratch, u16, &.{ windows, wide("\\") });
        try self.add(wide("\\Windows\\"), with_separator);
        if (with_separator.len < 2) return error.InvalidPath;
        self.system_drive = .{ with_separator[0], with_separator[1] };
        try self.add(wide("\\\\"), wide("\\\\"));
        self.local = true;
        self.deferred_local = false;
    }

    /// RuntimeRead: EventDriven("Windows drive topology or DOS device mapping changed")
    pub fn initLocal(allocator: std.mem.Allocator, max_path_units: u32) !PathMap {
        if (max_path_units == 0) return error.InvalidConfiguration;
        var self = PathMap.init(allocator);
        self.max_path_units = max_path_units;
        var round = std.heap.ArenaAllocator.init(allocator);
        defer round.deinit();
        errdefer self.deinit();
        try self.populateLocal(round.allocator());
        return self;
    }

    fn ensureLocal(self: *PathMap, scratch: std.mem.Allocator) !void {
        if (!self.deferred_local) return;
        if (self.entries.items.len == 0) {
            try self.populateLocal(scratch);
        } else {
            // One or more SysConfig records arrived first. TraceEvent does not
            // add local defaults after the map has any entries.
            self.deferred_local = false;
        }
    }

    pub fn map(self: *PathMap, round: std.mem.Allocator, path: []const u16, cap: usize) ![]const u16 {
        try self.ensureLocal(round);
        if (path.len > self.max_path_units) return error.CapacityExceeded;
        // TraceEvent 3.2.4's shipped assembly compiles out its historical
        // CONTAINER_WORKAROUND block. Only the ordinary first-prefix mapping
        // applies; an embedded "\\os\\windows\\" may be a real source path.
        for (self.entries.items) |entry| {
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

test "deferred local defaults follow SysConfig arrival precedence" {
    var deferred = try PathMap.initDeferredWithLimits(std.testing.allocator, 256, 8);
    defer deferred.deinit();
    const nt = [_]u8{ '\\', 0, 'D', 0, 'e', 0, 'v', 0, 'i', 0, 'c', 0, 'e', 0, '\\', 0, 'V', 0, 'o', 0, 'l', 0, 'u', 0, 'm', 0, 'e', 0, '1', 0, '\\', 0 };
    const dos = [_]u8{ 'D', 0, ':', 0, '\\', 0 };
    try deferred.addVolumeMappingUtf16Le(&nt, &dos);
    var round = std.heap.ArenaAllocator.init(std.testing.allocator);
    defer round.deinit();
    const mapped = try deferred.map(round.allocator(), wide("\\Windows\\System32\\kernel32.dll"), 256);
    try std.testing.expectEqualSlices(u16, wide("\\Windows\\System32\\kernel32.dll"), mapped);
    try std.testing.expect(!deferred.local);

    var eager = try PathMap.initLocal(std.testing.allocator, 4096);
    defer eager.deinit();
    const custom_windows = [_]u8{ 'Z', 0, ':', 0, '\\', 0, 'W', 0, 'i', 0, 'n', 0, 'd', 0, 'o', 0, 'w', 0, 's', 0, '\\', 0 };
    try eager.addSystemPathsUtf16Le(&.{ 'C', 0, ':', 0 }, &custom_windows);
    const eager_result = try eager.map(round.allocator(), wide("\\Windows\\System32\\kernel32.dll"), 4096);
    try std.testing.expect(!std.mem.startsWith(u16, eager_result, wide("Z:\\")));
    try std.testing.expect(eager.local);
}

test "container-like path segments follow shipped TraceEvent ordinary prefix mapping" {
    var paths = try PathMap.initDeferredWithLimits(std.testing.allocator, 256, 8);
    defer paths.deinit();
    try paths.add(wide("\\host\\"), wide("X:\\"));
    var round = std.heap.ArenaAllocator.init(std.testing.allocator);
    defer round.deinit();
    const original = wide("\\host\\Files\\Windows\\driver.sys");
    try std.testing.expectEqualSlices(u16, wide("X:\\Files\\Windows\\driver.sys"), try paths.map(round.allocator(), original, 256));
    try paths.addSystemPathsUtf16Le(&.{}, std.mem.sliceAsBytes(wide("Z:\\Windows\\")));
    try std.testing.expectEqualSlices(u16, wide("X:\\Files\\Windows\\driver.sys"), try paths.map(round.allocator(), original, 256));
    try std.testing.expectEqualSlices(u16, wide("X:\\oS\\wINDows\\driver.sys"), try paths.map(round.allocator(), wide("\\host\\oS\\wINDows\\driver.sys"), 256));
    const source_path = wide("D:\\Tools\\zig\\lib\\std\\os\\windows\\tls.zig");
    try std.testing.expectEqualSlices(u16, source_path, try paths.map(round.allocator(), source_path, 256));
    try std.testing.expectError(error.CapacityExceeded, paths.map(round.allocator(), original, 2));
}

fn allocationRound(a: std.mem.Allocator) !void {
    var map = try PathMap.initDeferredWithLimits(a, 64, 2);
    defer map.deinit();
    try map.add(wide("kernel"), wide("user"));
}

test "kernel path mapping fails boundedly and releases partial allocations" {
    try std.testing.checkAllAllocationFailures(std.testing.allocator, allocationRound, .{});
    try std.testing.expectError(error.CapacityExceeded, PathMap.initLocal(std.testing.allocator, 1));
    var local = try PathMap.initLocal(std.testing.allocator, 4096);
    defer local.deinit();
    try std.testing.expect(local.local);
    try std.testing.expect(local.count() >= 6 and local.count() <= 32);

    var full = PathMap.init(std.testing.allocator);
    defer full.deinit();
    for (0..32) |_| try full.add(wide("k"), wide("u"));
    try std.testing.expectError(error.CapacityExceeded, full.add(wide("overflow"), wide("overflow")));

    try std.testing.expectError(error.InvalidConfiguration, PathMap.initDeferred(std.testing.allocator, 0));
    var map = try PathMap.initDeferredWithLimits(std.testing.allocator, 2, 1);
    defer map.deinit();
    try std.testing.expectError(error.CapacityExceeded, map.add(wide("long"), wide("x")));
    try map.add(wide("k"), wide("u"));
    try std.testing.expectError(error.CapacityExceeded, map.add(wide("x"), wide("y")));
}
