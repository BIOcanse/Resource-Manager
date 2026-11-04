//! One explicit source query on a borrowed, already identified target handle.
//! This is not caller authorization and retains no value, handle, retry policy or current.
const std = @import("std");

pub const Status = enum(u32) { current = 0, query_failed = 1, invalid_result = 2, invalid_argument = 3 };
pub const Result = extern struct { status: Status, win32_error: u32, characters: usize };
pub const Query = *const fn (?*anyopaque, ?*anyopaque, [*]u16, *u32, *u32) callconv(.c) u32;
pub const Api = struct { context: ?*anyopaque = null, query: Query };

pub fn read(handle: ?*anyopaque, output: []u16, api: Api) Result {
    if (handle == null or @intFromPtr(handle.?) == std.math.maxInt(usize) or output.len == 0 or output.len > std.math.maxInt(u32))
        return .{ .status = .invalid_argument, .win32_error = 0, .characters = 0 };
    var length: u32 = @intCast(output.len);
    var err: u32 = 0;
    if (api.query(api.context, handle, output.ptr, &length, &err) == 0)
        return .{ .status = .query_failed, .win32_error = err, .characters = 0 };
    if (length == 0 or length >= output.len) return .{ .status = .invalid_result, .win32_error = 0, .characters = 0 };
    return .{ .status = .current, .win32_error = 0, .characters = length };
}

fn queryWindows(_: ?*anyopaque, handle: ?*anyopaque, buffer: [*]u16, length: *u32, err: *u32) callconv(.c) u32 {
    if (QueryFullProcessImageNameW(handle, 0, buffer, length) != 0) return 1;
    err.* = GetLastError();
    return 0;
}

pub const windows_api: Api = .{ .query = queryWindows };
extern "kernel32" fn QueryFullProcessImageNameW(?*anyopaque, u32, [*]u16, *u32) callconv(.winapi) i32;
extern "kernel32" fn GetLastError() callconv(.winapi) u32;
