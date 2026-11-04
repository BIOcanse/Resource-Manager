const std = @import("std");
pub const Handle = *anyopaque;
const wide = std.unicode.utf8ToUtf16LeStringLiteral;
extern "wevtapi" fn EvtSubscribe(?Handle, ?Handle, [*:0]const u16, [*:0]const u16, ?Handle, ?*anyopaque, ?*const anyopaque, u32) callconv(.winapi) ?Handle;
extern "wevtapi" fn EvtCreateBookmark(?[*:0]const u16) callconv(.winapi) ?Handle;
extern "wevtapi" fn EvtUpdateBookmark(Handle, Handle) callconv(.winapi) i32;
extern "wevtapi" fn EvtRender(?Handle, Handle, u32, u32, ?*anyopaque, *u32, *u32) callconv(.winapi) i32;
extern "wevtapi" fn EvtNext(Handle, u32, [*]Handle, u32, u32, *u32) callconv(.winapi) i32;
extern "wevtapi" fn EvtClose(Handle) callconv(.winapi) i32;
extern "kernel32" fn CreateEventW(?*anyopaque, i32, i32, ?[*:0]const u16) callconv(.winapi) ?Handle;
extern "kernel32" fn SetEvent(Handle) callconv(.winapi) i32;
extern "kernel32" fn CloseHandle(Handle) callconv(.winapi) i32;
extern "kernel32" fn WaitForMultipleObjects(u32, [*]const Handle, i32, u32) callconv(.winapi) u32;
extern "kernel32" fn GetLastError() callconv(.winapi) u32;
pub const Render = enum { event, bookmark };
/// Injected seam includes all event-log effects. Tests use fake handles and XML;
/// production never writes event logs or registry.
pub const Api = struct {
    context: ?*anyopaque = null,
    create_signal: *const fn (?*anyopaque) anyerror!Handle = createSignal,
    set_signal: *const fn (?*anyopaque, Handle) void = setSignal,
    close_signal: *const fn (?*anyopaque, Handle) void = closeSignal,
    bookmark: *const fn (?*anyopaque, ?[*:0]const u16) anyerror!Handle = nativeBookmark,
    subscribe: *const fn (?*anyopaque, Handle, [*:0]const u16, [*:0]const u16, Handle, bool) anyerror!Handle = nativeSubscribe,
    next: *const fn (?*anyopaque, Handle) anyerror!?Handle = nativeNext,
    update: *const fn (?*anyopaque, Handle, Handle) anyerror!void = nativeUpdate,
    render: *const fn (?*anyopaque, std.mem.Allocator, Handle, Render, usize) anyerror![]u8 = nativeRender,
    close: *const fn (?*anyopaque, Handle) void = nativeClose,
    wait: *const fn (?*anyopaque, []const Handle) anyerror!usize = nativeWait,
};
fn createSignal(_: ?*anyopaque) !Handle {
    return CreateEventW(null, 0, 0, null) orelse error.CreateSignalFailed;
}
fn setSignal(_: ?*anyopaque, h: Handle) void {
    _ = SetEvent(h);
}
fn closeSignal(_: ?*anyopaque, h: Handle) void {
    _ = CloseHandle(h);
}
fn nativeBookmark(_: ?*anyopaque, xml: ?[*:0]const u16) !Handle {
    return EvtCreateBookmark(xml) orelse error.InvalidBookmark;
}
fn nativeSubscribe(_: ?*anyopaque, signal: Handle, channel: [*:0]const u16, query: [*:0]const u16, mark: Handle, saved: bool) !Handle {
    // One subscription performs persisted catch-up and live delivery atomically.
    // Strict reports cleared/overwritten cursors instead of silently losing data.
    // EvtSubscribe requires NULL unless StartAfterBookmark is selected. An
    // empty bookmark is still a non-NULL handle and fails first start with
    // ERROR_INVALID_PARAMETER (87), before any event can be delivered.
    return EvtSubscribe(null, signal, channel, query, if (saved) mark else null, null, null, (if (saved) @as(u32, 3) else 2) | 0x10000) orelse switch (GetLastError()) {
        5 => error.SubscribeAccessDenied,
        87 => error.SubscribeInvalidParameter,
        1168 => error.SubscribeBookmarkNotFound,
        15001 => error.SubscribeInvalidQuery,
        15007 => error.SubscribeChannelNotFound,
        15011 => error.StaleSubscription,
        else => error.SubscribeFailed,
    };
}
fn nativeNext(_: ?*anyopaque, subscription: Handle) !?Handle {
    var handles: [1]Handle = undefined;
    var count: u32 = 0;
    if (EvtNext(subscription, 1, &handles, 0, 0, &count) == 0) {
        const code = GetLastError();
        if (code == 259) return null; // ERROR_NO_MORE_ITEMS
        if (code == 15011) return error.StaleSubscription;
        return error.ReadEventFailed;
    }
    if (count != 1) return error.ReadEventFailed;
    return handles[0];
}
fn nativeUpdate(_: ?*anyopaque, mark: Handle, event: Handle) !void {
    if (EvtUpdateBookmark(mark, event) == 0) return error.UpdateBookmarkFailed;
}
fn nativeRender(_: ?*anyopaque, allocator: std.mem.Allocator, h: Handle, kind: Render, limit: usize) ![]u8 {
    var used: u32 = 0;
    var count: u32 = 0;
    const flags: u32 = if (kind == .event) 1 else 2;
    if (EvtRender(null, h, flags, 0, null, &used, &count) == 0 and GetLastError() != 122) return error.RenderFailed;
    if (used == 0 or used > limit or used % 2 != 0) return error.XmlLimitExceeded;
    const buffer = try allocator.alloc(u16, used / 2);
    defer allocator.free(buffer);
    if (EvtRender(null, h, flags, used, buffer.ptr, &used, &count) == 0) return error.RenderFailed;
    if (used == 0 or used > buffer.len * 2 or used % 2 != 0) return error.RenderFailed;
    const units = buffer[0 .. used / 2];
    if (units[units.len - 1] != 0) return error.RenderFailed;
    return std.unicode.utf16LeToUtf8Alloc(allocator, units[0 .. units.len - 1]);
}
fn nativeClose(_: ?*anyopaque, h: Handle) void {
    _ = EvtClose(h);
}
fn nativeWait(_: ?*anyopaque, handles: []const Handle) !usize {
    const result = WaitForMultipleObjects(@intCast(handles.len), handles.ptr, 0, 0xffffffff);
    if (result >= handles.len) return error.WaitFailed;
    return @intCast(result);
}
