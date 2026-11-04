//! A single grant's local pin of the process already held by the attester.
//! No PID lookup, caller admission or data-source permission policy lives here.
const std = @import("std");
const access = @import("rm_access");
const windows = std.os.windows;

const process_query_limited_information: u32 = 0x1000;
const process_dup_handle: u32 = 0x0040;
const token_query: u32 = 0x0008;
const token_user: u32 = 1;
const token_groups: u32 = 2;
const token_session_id: u32 = 12;
const group_enabled: u32 = 0x00000004;
const group_use_for_deny_only: u32 = 0x00000010;
const win_local_system_sid: u32 = 22;
const win_builtin_administrators_sid: u32 = 26;
const error_insufficient_buffer: u32 = 122;
const still_active: u32 = 259;
// FILETIME and DateTime.UtcTicks both use 100 ns units; their epochs differ.
const filetime_to_dotnet_ticks: u64 = 504911232000000000;

extern "kernel32" fn GetCurrentProcess() callconv(.winapi) windows.HANDLE;
extern "kernel32" fn DuplicateHandle(source_process: windows.HANDLE, source: windows.HANDLE, target_process: windows.HANDLE, target: *windows.HANDLE, desired_access: u32, inherit: i32, options: u32) callconv(.winapi) i32;
extern "kernel32" fn CloseHandle(handle: windows.HANDLE) callconv(.winapi) i32;
extern "kernel32" fn GetLastError() callconv(.winapi) u32;
extern "kernel32" fn GetProcessId(process: windows.HANDLE) callconv(.winapi) u32;
extern "kernel32" fn GetProcessTimes(process: windows.HANDLE, created: *windows.FILETIME, exited: *windows.FILETIME, kernel: *windows.FILETIME, user: *windows.FILETIME) callconv(.winapi) i32;
extern "kernel32" fn GetExitCodeProcess(process: windows.HANDLE, exit_code: *u32) callconv(.winapi) i32;
extern "kernel32" fn LocalAlloc(flags: u32, bytes: usize) callconv(.winapi) ?*anyopaque;
extern "kernel32" fn LocalFree(memory: *anyopaque) callconv(.winapi) ?*anyopaque;
extern "advapi32" fn OpenProcessToken(process: windows.HANDLE, desired_access: u32, token: *windows.HANDLE) callconv(.winapi) i32;
extern "advapi32" fn GetTokenInformation(token: windows.HANDLE, information_class: u32, information: ?*anyopaque, length: u32, returned_length: *u32) callconv(.winapi) i32;
extern "advapi32" fn ConvertSidToStringSidW(sid: *const anyopaque, text: *?[*:0]u16) callconv(.winapi) i32;
extern "advapi32" fn IsWellKnownSid(sid: *const anyopaque, sid_type: u32) callconv(.winapi) i32;

pub const Error = error{ InvalidCaller, CallerChanged, ProcessFailed, TokenFailed };

pub const Pin = struct {
    handle: windows.HANDLE,

    /// `supplied` is borrowed from the platform attester, never a request PID.
    /// The returned handle belongs only to this call and must be closed.
    pub fn open(supplied: windows.HANDLE, expected: access.Caller, native_error: *u32) Error!Pin {
        native_error.* = 0;
        try validateExpected(expected);
        const current = GetCurrentProcess();
        var handle: windows.HANDLE = undefined;
        if (DuplicateHandle(current, supplied, current, &handle, process_query_limited_information | process_dup_handle, 0, 0) == 0) {
            native_error.* = GetLastError();
            return error.ProcessFailed;
        }
        const pin: Pin = .{ .handle = handle };
        errdefer pin.close();
        try pin.validate(expected, native_error);
        return pin;
    }

    pub fn close(self: Pin) void {
        _ = CloseHandle(self.handle);
    }

    /// Every OS query uses the same pinned object. Token changes or termination
    /// cause refusal; a recycled PID can never select a different process.
    pub fn validate(self: Pin, expected: access.Caller, native_error: *u32) Error!void {
        native_error.* = 0;
        try validateExpected(expected);
        try self.validateProcess(expected, native_error);

        var token: windows.HANDLE = undefined;
        if (OpenProcessToken(self.handle, token_query, &token) == 0) {
            native_error.* = GetLastError();
            return error.TokenFailed;
        }
        defer _ = CloseHandle(token);

        const user = try TokenInformation.read(token, token_user, native_error);
        defer user.close();
        if (user.length < @sizeOf(SidAndAttributes)) return error.TokenFailed;
        const user_information: *const SidAndAttributes = @ptrCast(@alignCast(user.memory));
        const sid = user_information.sid orelse return error.TokenFailed;
        var sid_text: ?[*:0]u16 = null;
        if (ConvertSidToStringSidW(sid, &sid_text) == 0) {
            native_error.* = GetLastError();
            return error.TokenFailed;
        }
        const text = sid_text orelse return error.TokenFailed;
        defer _ = LocalFree(@ptrCast(text));
        if (!std.mem.eql(u16, std.mem.span(text), expected.windows_sid)) return error.CallerChanged;

        var session: u32 = undefined;
        var returned_length: u32 = 0;
        if (GetTokenInformation(token, token_session_id, &session, @sizeOf(u32), &returned_length) == 0) {
            native_error.* = GetLastError();
            return error.TokenFailed;
        }
        if (returned_length != @sizeOf(u32)) return error.TokenFailed;
        if (session != @as(u32, @intCast(expected.windows_session_id))) return error.CallerChanged;

        // Mirrors WindowsPrincipal.IsInRole(Administrator): the live token's
        // enabled Administrators SID counts; a filtered deny-only SID does not.
        // SYSTEM is explicitly included in the existing caller contract. Never
        // inspect a linked token or treat TokenElevation alone as membership.
        const administrator = IsWellKnownSid(sid, win_local_system_sid) != 0 or
            try hasEnabledAdministrators(token, native_error);
        if (administrator != expected.is_administrator) return error.CallerChanged;
        try self.validateProcess(expected, native_error);
    }

    /// The caller owns the permission decision. Duplicate only its explicit
    /// section rights, without inheritance or DUPLICATE_SAME_ACCESS.
    /// The result is a handle value in the pinned process, not in this service.
    pub fn duplicateSection(self: Pin, source: windows.HANDLE, desired_access: u32, native_error: *u32) Error!windows.HANDLE {
        native_error.* = 0;
        var target: windows.HANDLE = undefined;
        if (DuplicateHandle(GetCurrentProcess(), source, self.handle, &target, desired_access, 0, 0) == 0) {
            native_error.* = GetLastError();
            return error.ProcessFailed;
        }
        return target;
    }

    fn validateProcess(self: Pin, expected: access.Caller, native_error: *u32) Error!void {
        const process_id = GetProcessId(self.handle);
        if (process_id == 0) {
            native_error.* = GetLastError();
            return error.ProcessFailed;
        }
        if (process_id != @as(u32, @intCast(expected.process_id))) return error.CallerChanged;
        var created: windows.FILETIME = undefined;
        var exited: windows.FILETIME = undefined;
        var kernel: windows.FILETIME = undefined;
        var user: windows.FILETIME = undefined;
        if (GetProcessTimes(self.handle, &created, &exited, &kernel, &user) == 0) {
            native_error.* = GetLastError();
            return error.ProcessFailed;
        }
        const filetime = @as(u64, created.dwHighDateTime) << 32 | created.dwLowDateTime;
        if (filetime > std.math.maxInt(i64) - filetime_to_dotnet_ticks or
            filetime + filetime_to_dotnet_ticks != @as(u64, @intCast(expected.process_created_utc_ticks)))
            return error.CallerChanged;

        // GetProcessTimes leaves exit time undefined for a live process. Use
        // its creation time only, then query the documented termination status.
        // Exit code 259 is ambiguous; this is not a lifetime lock. A concurrent
        // exit cannot redirect the pin, and DuplicateHandle must still succeed.
        var exit_code: u32 = undefined;
        if (GetExitCodeProcess(self.handle, &exit_code) == 0) {
            native_error.* = GetLastError();
            return error.ProcessFailed;
        }
        if (exit_code != still_active) return error.CallerChanged;
    }
};

fn validateExpected(expected: access.Caller) Error!void {
    if (expected.process_id <= 0 or expected.process_created_utc_ticks <= 0 or
        expected.windows_session_id < 0 or expected.windows_sid.len == 0)
        return error.InvalidCaller;
}

const SidAndAttributes = extern struct {
    sid: ?*anyopaque,
    attributes: u32,
};
const TokenGroups = extern struct {
    count: u32,
    groups: [1]SidAndAttributes,
};

// GetTokenInformation supplies the required size. These short-lived Windows
// allocations require no service allocator, retained workspace or retry loop.
const TokenInformation = struct {
    memory: *anyopaque,
    length: u32,

    fn read(token: windows.HANDLE, information_class: u32, native_error: *u32) Error!TokenInformation {
        var required: u32 = 0;
        if (GetTokenInformation(token, information_class, null, 0, &required) != 0)
            return error.TokenFailed;
        const size_error = GetLastError();
        if (size_error != error_insufficient_buffer or required == 0) {
            native_error.* = size_error;
            return error.TokenFailed;
        }
        const memory = LocalAlloc(0, required) orelse {
            native_error.* = GetLastError();
            return error.TokenFailed;
        };
        errdefer _ = LocalFree(memory);
        var returned_length: u32 = 0;
        if (GetTokenInformation(token, information_class, memory, required, &returned_length) == 0) {
            native_error.* = GetLastError();
            return error.TokenFailed;
        }
        if (returned_length > required) return error.TokenFailed;
        return .{ .memory = memory, .length = returned_length };
    }

    fn close(self: TokenInformation) void {
        _ = LocalFree(self.memory);
    }
};

fn hasEnabledAdministrators(token: windows.HANDLE, native_error: *u32) Error!bool {
    const information = try TokenInformation.read(token, token_groups, native_error);
    defer information.close();
    const offset = @offsetOf(TokenGroups, "groups");
    if (information.length < offset) return error.TokenFailed;
    const groups: *const TokenGroups = @ptrCast(@alignCast(information.memory));
    if (groups.count > (information.length - offset) / @sizeOf(SidAndAttributes)) return error.TokenFailed;
    const entries: [*]const SidAndAttributes = @ptrCast(&groups.groups);
    var index: usize = 0;
    while (index < groups.count) : (index += 1) {
        const entry = entries[index];
        const sid = entry.sid orelse return error.TokenFailed;
        if (entry.attributes & group_enabled != 0 and entry.attributes & group_use_for_deny_only == 0 and
            IsWellKnownSid(sid, win_builtin_administrators_sid) != 0) return true;
    }
    return false;
}
