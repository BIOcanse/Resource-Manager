const std = @import("std");

// Read-only, per-call Windows facts. No authorization, launch policy or cache.
// The receivers borrow native SID text / the final caller-owned ID destination
// synchronously. All OS allocations and owned handles end before returning.
pub const Status = enum(u32) { ok, not_found, windows_error, receiver_failed, invalid_argument };
pub const Operation = enum(u32) { none, current_token, token_user, process_session, sid_text, enumerate_sessions };
pub const Failure = extern struct { operation: Operation = .none, code: u32 = 0 };
pub const SidReceiver = *const fn (?*anyopaque, [*]const u16, u32) callconv(.c) u32;
pub const IdDestination = *const fn (?*anyopaque, u32) callconv(.c) ?[*]u32;
pub const Handle = *anyopaque;
pub const TokenResult = union(enum) { ok: Handle, failed: u32 };
pub const TextResult = union(enum) { ok: [:0]u16, failed: u32 };
pub const SessionBuffer = struct { items: []Session, allocation: ?*anyopaque = null };
pub const SessionResult = union(enum) { ok: SessionBuffer, failed: u32 };
pub const SessionIdResult = union(enum) { ok: u32, failed: u32 };

// WTS_SESSION_INFOW / TOKEN_USER, from the Windows SDK. These values describe
// the OS wire layout, not product choices or capacity defaults.
pub const Session = extern struct { id: u32, station: ?[*:0]u16 = null, state: i32 = 0 };
const TokenUser = extern struct { sid: ?*anyopaque, attributes: u32 };
pub const security_max_sid_size = 68; // SECURITY_MAX_SID_SIZE, winnt.h
pub const UserBuffer = struct {
    bytes: [@sizeOf(TokenUser) + security_max_sid_size]u8 align(@alignOf(TokenUser)) = undefined,
    pub fn sid(self: *UserBuffer) ?*anyopaque {
        const user: *const TokenUser = @ptrCast(&self.bytes);
        return user.sid;
    }
};

fn fail(failure: *Failure, operation: Operation, code: u32) Status {
    failure.* = .{ .operation = operation, .code = code };
    return .windows_error;
}

pub fn userSid(api: anytype, session_id: u32, context: ?*anyopaque, receive: SidReceiver, failure: *Failure) Status {
    failure.* = .{};
    const current = switch (api.openCurrentToken()) {
        .ok => |handle| handle,
        .failed => |code| return fail(failure, .current_token, code),
    };
    defer api.closeToken(current);
    var current_user: UserBuffer = .{};
    if (api.readUser(current, &current_user)) |code| return fail(failure, .token_user, code);
    const current_sid = current_user.sid() orelse return fail(failure, .token_user, error_invalid_sid);
    if (!api.isSystem(current_sid)) {
        const current_session = switch (api.processSession()) {
            .ok => |id| id,
            .failed => |code| return fail(failure, .process_session, code),
        };
        // Process.SessionId is signed in the original managed comparison with
        // the uint request. Preserve that comparison for the full DWORD domain.
        if (current_session <= std.math.maxInt(i32) and current_session == session_id)
            return deliverSid(api, current_sid, context, receive, failure);
    }

    // A failed WTS query is the old null result, never the current-user SID.
    const token = switch (api.queryUserToken(session_id)) {
        .ok => |handle| handle,
        .failed => return .not_found,
    };
    defer api.closeToken(token);
    var user: UserBuffer = .{};
    if (api.readUser(token, &user)) |code| return fail(failure, .token_user, code);
    const sid = user.sid() orelse return fail(failure, .token_user, error_invalid_sid);
    return deliverSid(api, sid, context, receive, failure);
}

fn deliverSid(api: anytype, sid: *anyopaque, context: ?*anyopaque, receive: SidReceiver, failure: *Failure) Status {
    const text = switch (api.sidText(sid)) {
        .ok => |value| value,
        .failed => |code| return fail(failure, .sid_text, code),
    };
    defer api.freeText(text);
    return if (receive(context, text.ptr, @intCast(text.len)) == 0) .ok else .receiver_failed;
}

pub fn activeSessionIds(api: anytype, context: ?*anyopaque, destination: IdDestination, failure: *Failure) Status {
    failure.* = .{};
    const buffer = switch (api.enumerateSessions()) {
        .ok => |value| value,
        .failed => |code| return fail(failure, .enumerate_sessions, code),
    };
    defer api.freeSessions(buffer);
    const sessions = buffer.items;

    // Reuse the original WTS allocation. Compacting to u32 writes only into
    // already-consumed entries (the entire current entry is read first).
    const ids: [*]u32 = @ptrCast(sessions.ptr);
    var count: usize = 0;
    var index: usize = 0;
    while (index < sessions.len) : (index += 1) {
        const entry = sessions[index];
        if (entry.state == wts_active and entry.id != 0) {
            ids[count] = entry.id;
            count += 1;
        }
    }
    std.sort.heap(u32, ids[0..count], {}, std.sort.asc(u32));
    var unique: usize = 0;
    index = 0;
    while (index < count) : (index += 1) {
        if (unique == 0 or ids[index] != ids[unique - 1]) {
            ids[unique] = ids[index];
            unique += 1;
        }
    }
    const output = destination(context, @intCast(unique));
    if (unique == 0) return .ok;
    const target = output orelse return .receiver_failed;
    @memcpy(target[0..unique], ids[0..unique]);
    return .ok;
}

const maximum_allowed: u32 = 0x02000000; // WindowsIdentity.GetCurrent's access
const token_user: u32 = 1; // TOKEN_INFORMATION_CLASS.TokenUser
const win_local_system_sid: u32 = 22; // WELL_KNOWN_SID_TYPE.WinLocalSystemSid
const error_no_token: u32 = 1008;
const error_invalid_sid: u32 = 1337;
const wts_active: i32 = 0; // WTS_CONNECTSTATE_CLASS.WTSActive
const wts_enumeration_version: u32 = 1; // WTSEnumerateSessionsW contract

extern "kernel32" fn GetCurrentProcess() callconv(.winapi) Handle;
extern "kernel32" fn GetCurrentThread() callconv(.winapi) Handle;
extern "kernel32" fn GetCurrentProcessId() callconv(.winapi) u32;
extern "kernel32" fn ProcessIdToSessionId(u32, *u32) callconv(.winapi) i32;
extern "kernel32" fn GetLastError() callconv(.winapi) u32;
extern "kernel32" fn CloseHandle(Handle) callconv(.winapi) i32;
extern "kernel32" fn LocalFree(*anyopaque) callconv(.winapi) ?*anyopaque;
extern "advapi32" fn OpenThreadToken(Handle, u32, i32, *?Handle) callconv(.winapi) i32;
extern "advapi32" fn OpenProcessToken(Handle, u32, *?Handle) callconv(.winapi) i32;
extern "advapi32" fn GetTokenInformation(Handle, u32, *anyopaque, u32, *u32) callconv(.winapi) i32;
extern "advapi32" fn IsWellKnownSid(*anyopaque, u32) callconv(.winapi) i32;
extern "advapi32" fn ConvertSidToStringSidW(*anyopaque, *?[*:0]u16) callconv(.winapi) i32;
extern "wtsapi32" fn WTSQueryUserToken(u32, *?Handle) callconv(.winapi) i32;
extern "wtsapi32" fn WTSEnumerateSessionsW(?Handle, u32, u32, *?[*]Session, *u32) callconv(.winapi) i32;
extern "wtsapi32" fn WTSFreeMemory(?*anyopaque) callconv(.winapi) void;

// .NET 10 WindowsIdentity.GetCurrent uses MAXIMUM_ALLOWED, opens the current
// thread as self then as thread after any failed open, and falls back to the process
// only on ERROR_NO_TOKEN. See dotnet/runtime v10.0.0 WindowsIdentity.cs and
// Interop.OpenThreadToken.cs (MIT); behavior reimplemented here, no copied code.
pub fn openCurrentTokenWith(api: anytype) TokenResult {
    var result = api.openThread(true);
    if (result == .failed) result = api.openThread(false);
    if (result == .failed and result.failed == error_no_token) return api.openProcess();
    return result;
}

pub const Windows = struct {
    pub fn openCurrentToken(self: *Windows) TokenResult {
        return openCurrentTokenWith(self);
    }
    pub fn openThread(_: *Windows, as_self: bool) TokenResult {
        var token: ?Handle = null;
        if (OpenThreadToken(GetCurrentThread(), maximum_allowed, @intFromBool(as_self), &token) == 0) {
            const code = GetLastError();
            if (token) |owned| _ = CloseHandle(owned);
            return .{ .failed = code };
        }
        return .{ .ok = token.? };
    }
    pub fn openProcess(_: *Windows) TokenResult {
        var token: ?Handle = null;
        if (OpenProcessToken(GetCurrentProcess(), maximum_allowed, &token) == 0) {
            const code = GetLastError();
            if (token) |owned| _ = CloseHandle(owned);
            return .{ .failed = code };
        }
        return .{ .ok = token.? };
    }
    pub fn closeToken(_: *Windows, token: Handle) void {
        _ = CloseHandle(token);
    }
    pub fn readUser(_: *Windows, token: Handle, buffer: *UserBuffer) ?u32 {
        var needed: u32 = 0;
        if (GetTokenInformation(token, token_user, &buffer.bytes, buffer.bytes.len, &needed) == 0) return GetLastError();
        return null;
    }
    pub fn isSystem(_: *Windows, sid: *anyopaque) bool {
        return IsWellKnownSid(sid, win_local_system_sid) != 0;
    }
    pub fn processSession(_: *Windows) SessionIdResult {
        var id: u32 = 0;
        if (ProcessIdToSessionId(GetCurrentProcessId(), &id) == 0) return .{ .failed = GetLastError() };
        return .{ .ok = id };
    }
    pub fn queryUserToken(_: *Windows, session_id: u32) TokenResult {
        var token: ?Handle = null;
        if (WTSQueryUserToken(session_id, &token) == 0) {
            const code = GetLastError();
            if (token) |owned| _ = CloseHandle(owned);
            return .{ .failed = code };
        }
        return .{ .ok = token.? };
    }
    pub fn sidText(_: *Windows, sid: *anyopaque) TextResult {
        var text: ?[*:0]u16 = null;
        if (ConvertSidToStringSidW(sid, &text) == 0) {
            const code = GetLastError();
            if (text) |owned| _ = LocalFree(owned);
            return .{ .failed = code };
        }
        return .{ .ok = std.mem.span(text.?) };
    }
    pub fn freeText(_: *Windows, text: [:0]u16) void {
        _ = LocalFree(text.ptr);
    }
    pub fn enumerateSessions(_: *Windows) SessionResult {
        var entries: ?[*]Session = null;
        var count: u32 = 0;
        if (WTSEnumerateSessionsW(null, 0, wts_enumeration_version, &entries, &count) == 0) {
            const code = GetLastError();
            if (entries) |owned| WTSFreeMemory(owned);
            return .{ .failed = code };
        }
        // Windows permits a null buffer for a successful empty enumeration.
        return .{ .ok = .{
            .items = if (entries) |ptr| ptr[0..count] else &.{},
            .allocation = if (entries) |ptr| @ptrCast(ptr) else null,
        } };
    }
    pub fn freeSessions(_: *Windows, buffer: SessionBuffer) void {
        if (buffer.allocation) |owned| WTSFreeMemory(owned);
    }
};
