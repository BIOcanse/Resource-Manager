//! Scalar file safety: the temporary Unicode path is owned and freed by ntdll.
//! No scratch owner, workspace, retained address or managed filesystem callback.
const std = @import("std");
const Unicode = extern struct { length: u16, maximum: u16, buffer: ?[*]u16 };
const Attributes = extern struct { length: u32 = @sizeOf(Attributes), root: usize = 0, name: *Unicode, attributes: u32 = 0x40, security: ?*anyopaque = null, qos: ?*anyopaque = null };
const Io = extern struct { status: usize = 0, information: usize = 0 };
const Basic = extern struct { creation: i64, access: i64, write: i64, change: i64, attributes: u32 };
const Standard = extern struct { allocation: i64, end: i64, links: u32, delete_pending: u8, directory: u8 };
extern "ntdll" fn RtlDosPathNameToNtPathName_U_WithStatus([*:0]const u16, *Unicode, ?*anyopaque, ?*anyopaque) callconv(.winapi) i32;
extern "ntdll" fn RtlFreeUnicodeString(*Unicode) callconv(.winapi) void;
extern "ntdll" fn NtQueryAttributesFile(*Attributes, *Basic) callconv(.winapi) i32;
extern "ntdll" fn NtOpenFile(*usize, u32, *Attributes, *Io, u32, u32) callconv(.winapi) i32;
extern "ntdll" fn NtQueryInformationFile(usize, *Io, *Standard, u32, u32) callconv(.winapi) i32;
extern "ntdll" fn NtClose(usize) callconv(.winapi) i32;
extern "ntdll" fn RtlNtStatusToDosError(i32) callconv(.winapi) u32;
pub fn check(path: [*:0]const u16, count: u32, root_count: u32, regular: bool, os_error: *u32) u32 {
    os_error.* = 0;
    if (count == 0 or count > 32767 or root_count == 0 or root_count > count) return 1;
    var name: Unicode = .{ .length = 0, .maximum = 0, .buffer = null };
    const converted = RtlDosPathNameToNtPathName_U_WithStatus(path, &name, null, null);
    if (converted < 0) {
        os_error.* = RtlNtStatusToDosError(converted);
        return 2;
    }
    defer RtlFreeUnicodeString(&name);
    const native_count: usize = name.length / 2;
    if (native_count < count) return 1;
    const prefix = native_count - count;
    var attributes: Attributes = .{ .name = &name };
    var current: usize = count;
    var first = true;
    while (true) {
        name.length = @intCast((prefix + current) * 2);
        var basic: Basic = undefined;
        const queried = NtQueryAttributesFile(&attributes, &basic);
        if (queried < 0) {
            if (regular and first) return 0;
            const bits: u32 = @bitCast(queried);
            if (bits != 0xc0000034 and bits != 0xc000003a and bits != 0xc000000f) {
                os_error.* = RtlNtStatusToDosError(queried);
                return 2;
            }
        } else {
            if (regular and first and basic.attributes & 0x10 != 0) return 0;
            if (basic.attributes & 0x400 != 0) {
                os_error.* = @intCast(current);
                return if (regular) 0 else 3;
            }
            if (first and basic.attributes & 0x10 == 0) {
                var handle: usize = 0;
                var io: Io = .{};
                const opened = NtOpenFile(&handle, 0x80100000, &attributes, &io, 7, 0x400060);
                if (opened < 0) {
                    if (regular) return 0;
                    os_error.* = RtlNtStatusToDosError(opened);
                    return 2;
                }
                defer _ = NtClose(handle);
                var info: Standard = undefined;
                const identified = NtQueryInformationFile(handle, &io, &info, @sizeOf(Standard), 5);
                if (identified < 0) {
                    if (regular) return 0;
                    os_error.* = RtlNtStatusToDosError(identified);
                    return 2;
                }
                if (info.links > 1) {
                    os_error.* = @intCast(current);
                    return if (regular) 0 else 4;
                }
            }
        }
        first = false;
        if (current <= root_count) break;
        while (current > root_count and (path[current - 1] == '\\' or path[current - 1] == '/')) : (current -= 1) {}
        while (current > root_count and path[current - 1] != '\\' and path[current - 1] != '/') : (current -= 1) {}
        while (current > root_count and (path[current - 1] == '\\' or path[current - 1] == '/')) : (current -= 1) {}
    }
    return if (regular) 1 else 0;
}
