const std = @import("std");
const builtin = @import("builtin");
const core = @import("core.zig");

const HKEY = *anyopaque;
const Paths = struct { path: [:0]const u16, name: [:0]const u16 };
fn wide(comptime value: []const u8) [:0]const u16 {
    // Compile-time UTF-16 expansion of the fixed registry path table; no runtime policy or allocation.
    @setEvalBranchQuota(10000);
    return std.unicode.utf8ToUtf16LeStringLiteral(value);
}
const slots = [_]Paths{
    .{ .path = wide("SYSTEM\\CurrentControlSet\\Services\\kbdhid\\Parameters"), .name = wide("CrashOnCtrlScroll") },
    .{ .path = wide("SYSTEM\\CurrentControlSet\\Services\\kbdhid\\crashdump"), .name = wide("Dump1Keys") },
    .{ .path = wide("SYSTEM\\CurrentControlSet\\Services\\kbdhid\\crashdump"), .name = wide("Dump2Key") },
    .{ .path = wide("SYSTEM\\CurrentControlSet\\Services\\i8042prt\\Parameters"), .name = wide("CrashOnCtrlScroll") },
    .{ .path = wide("SYSTEM\\CurrentControlSet\\Services\\i8042prt\\crashdump"), .name = wide("Dump1Keys") },
    .{ .path = wide("SYSTEM\\CurrentControlSet\\Services\\i8042prt\\crashdump"), .name = wide("Dump2Key") },
    .{ .path = wide("SYSTEM\\CurrentControlSet\\Control\\CrashControl"), .name = wide("CrashDumpEnabled") },
};

pub const Registry = struct {
    root: HKEY,

    pub fn machine() Registry {
        return .{ .root = @ptrFromInt(@as(usize, @bitCast(@as(isize, @as(i32, @bitCast(@as(u32, 0x80000002))))))) };
    }

    pub fn adapter(self: *Registry) core.Adapter {
        return .{ .context = self, .read = read, .write = write };
    }

    // RuntimeRead: EventDriven("Crash-dump key setting or status requested")
    fn read(context: ?*anyopaque, slot: u32, output: *core.Value) u32 {
        if (builtin.os.tag != .windows) return 50;
        if (slot >= slots.len) return 87;
        const self: *Registry = @ptrCast(@alignCast(context.?));
        output.* = .{};
        var key: ?HKEY = null;
        const opened = RegOpenKeyExW(self.root, slots[slot].path.ptr, 0, key_query_value | key_wow64_64key, &key);
        if (opened == error_file_not_found) return 0;
        if (opened != 0) return opened;
        defer _ = RegCloseKey(key.?);
        var size: u32 = core.maximum_value_bytes;
        const status = RegQueryValueExW(key.?, slots[slot].name.ptr, null, &output.value_type, &output.data, &size);
        if (status == error_file_not_found) {
            output.* = .{};
            return 0;
        }
        if (status != 0) return status;
        output.present = 1;
        output.length = size;
        return 0;
    }

    // RuntimeRead: EventDriven("Crash-dump key setting committed")
    fn write(context: ?*anyopaque, slot: u32, value: *const core.Value) u32 {
        if (builtin.os.tag != .windows) return 50;
        if (slot >= slots.len or value.length > core.maximum_value_bytes) return 87;
        const self: *Registry = @ptrCast(@alignCast(context.?));
        var key: ?HKEY = null;
        const opened = if (value.present != 0)
            RegCreateKeyExW(self.root, slots[slot].path.ptr, 0, null, 0, key_query_value | key_set_value | key_wow64_64key, null, &key, null)
        else
            RegOpenKeyExW(self.root, slots[slot].path.ptr, 0, key_query_value | key_set_value | key_wow64_64key, &key);
        if (opened == error_file_not_found and value.present == 0) return 0;
        if (opened != 0) return opened;
        defer _ = RegCloseKey(key.?);
        const status = if (value.present == 0) RegDeleteValueW(key.?, slots[slot].name.ptr) else RegSetValueExW(key.?, slots[slot].name.ptr, 0, value.value_type, &value.data, value.length);
        if (status == error_file_not_found and value.present == 0) return 0;
        if (status != 0) return status;
        // The saved pre-image is already durable. Flush each changed key before reporting success.
        return RegFlushKey(key.?);
    }
};

const key_query_value = 0x0001;
const key_set_value = 0x0002;
const key_wow64_64key = 0x0100;
const error_file_not_found = 2;
extern "advapi32" fn RegOpenKeyExW(HKEY, [*:0]const u16, u32, u32, *?HKEY) callconv(.winapi) u32;
extern "advapi32" fn RegCreateKeyExW(HKEY, [*:0]const u16, u32, ?*anyopaque, u32, u32, ?*anyopaque, *?HKEY, ?*u32) callconv(.winapi) u32;
extern "advapi32" fn RegQueryValueExW(HKEY, [*:0]const u16, ?*u32, *u32, [*]u8, *u32) callconv(.winapi) u32;
extern "advapi32" fn RegSetValueExW(HKEY, [*:0]const u16, u32, u32, [*]const u8, u32) callconv(.winapi) u32;
extern "advapi32" fn RegDeleteValueW(HKEY, [*:0]const u16) callconv(.winapi) u32;
extern "advapi32" fn RegFlushKey(HKEY) callconv(.winapi) u32;
extern "advapi32" fn RegCloseKey(HKEY) callconv(.winapi) u32;
