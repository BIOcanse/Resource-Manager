//! P-proc-read first group: QueryFullProcessImageNameW, Win32 format only.
//! Inputs are borrowed FROM the existing attester, never request-body identities.
//! No source/current publication and no implicit route or stronger-rights retry.
// API contracts: Microsoft Learn /handleapi/{compareobjecthandles,duplicatehandle},
// /securitybaseapi/impersonateloggedonuser, /winbase/queryfullprocessimagenamew.
const std = @import("std");
pub const windows = @import("process_read_access_windows.zig");
const w = windows;
pub const abi_version: u32 = 0x0001_0000;
pub const image_path_api: u32 = 1;
// Stable fatal-safety exit, not a tunable policy or recoverable ABI result.
pub const revert_failure_exit: u32 = 0xe0520001;
pub const Route = enum(u32) { open_under_caller = 1, caller_held_handle = 2 };
pub const Status = enum(u32) {
    ok = 0,
    invalid_input = 1,
    caller_changed = 2,
    target_mismatch = 3,
    target_exited = 4,
    access_denied = 5,
    insufficient_impersonation_level = 6,
    impersonation_failed = 7,
    ambient_impersonation = 8,
    cancelled = 9,
    buffer_too_small = 10,
    query_failed = 11,
    duplicate_failed = 12,
    invalid_handle = 13,
    unsupported = 14,
    native_failure = 15,
};
pub const Stage = enum(u32) {
    none = 0,
    input = 1,
    caller_identity = 2,
    target_identity = 3,
    token = 4,
    impersonate = 5,
    open = 6,
    duplicate = 7,
    compare_object = 8,
    granted_access = 9,
    query = 10,
    cancellation = 11,
};
pub const Result = extern struct {
    status: Status = .ok,
    stage: Stage = .none,
    win32: u32 = 0,
    ntstatus: i32 = 0,
    path_length: u32 = 0,
    granted_access: u32 = 0,
};
pub const Request = extern struct {
    caller_process: usize,
    caller_token: usize,
    target_reference: usize,
    caller_held_handle: usize,
    caller_created_filetime: u64,
    target_created_filetime: u64,
    caller_pid: u32,
    target_pid: u32,
    route: u32,
    api: u32,
    // Optional SERVICE-only exit observers, separate from caller query rights.
    // Zero explicitly delegates end-of-scope to the owner's cancellation signal.
    caller_exit_observer: usize = 0,
    target_exit_observer: usize = 0,
};
/// Owned by ONE local consumer scope. Do not bit-copy, replay or cache as proof.
/// Holds a real object reference; never export this service handle to a client.
pub const Capability = extern struct {
    handle: usize = 0,
    target_created_filetime: u64 = 0,
    target_pid: u32 = 0,
    api: u32 = 0,
    granted_access: u32 = 0,
    route: u32 = 0,
};

fn failure(status: Status, stage: Stage, win32: u32) Result {
    return .{ .status = status, .stage = stage, .win32 = win32 };
}
fn osFailure(otherwise: Status, stage: Stage) Result {
    const code = w.GetLastError();
    return failure(switch (code) {
        5 => .access_denied,
        6 => .invalid_handle,
        50, 120 => .unsupported,
        122 => .buffer_too_small,
        else => otherwise,
    }, stage, code);
}
pub fn cancelled(flag: ?*const u32) bool {
    return if (flag) |value| @atomicLoad(u32, value, .acquire) != 0 else false;
}
fn cancellation() Result {
    return failure(.cancelled, .cancellation, 1223);
}
// Windows pseudo handles include negative/sign-extended 32-bit values. None is
// valid as an untrusted caller handle-table entry (DuplicateHandle accepts some).
fn realHandle(value: usize) bool {
    return value != 0 and @as(isize, @bitCast(value)) >= 0 and
        !(value >= 0xfffffff0 and value <= 0xffffffff);
}
fn identity(handle: usize, pid: u32, created: u64, caller: bool) Result {
    const stage: Stage = if (caller) .caller_identity else .target_identity;
    const actual_pid = w.GetProcessId(handle);
    if (actual_pid == 0) return osFailure(.native_failure, stage);
    var birth: w.FileTime = .{};
    var exit: w.FileTime = .{};
    var kernel: w.FileTime = .{};
    var user: w.FileTime = .{};
    if (w.GetProcessTimes(handle, &birth, &exit, &kernel, &user) == 0)
        return osFailure(.native_failure, stage);
    if (actual_pid != pid or birth.value() != created)
        return failure(if (caller) .caller_changed else .target_mismatch, stage, 0);
    // GetProcessTimes.ExitTime is undefined for a live process. Never infer
    // liveness from that output or GetExitCodeProcess's ambiguous exit code 259.
    return .{};
}
fn observedExit(comparison: *const w.ObjectComparison, observer: usize, reference: usize, caller: bool) Result {
    if (observer == 0) return .{};
    const stage: Stage = if (caller) .caller_identity else .target_identity;
    if (!realHandle(observer)) return failure(.invalid_input, stage, 0);
    if (!comparison.same(observer, reference))
        return failure(if (caller) .caller_changed else .target_mismatch, stage, w.GetLastError());
    return switch (w.WaitForSingleObject(observer, 0)) {
        0 => failure(if (caller) .caller_changed else .target_exited, stage, 0),
        258 => .{}, // WAIT_TIMEOUT: zero-time observation, never a polling loop.
        0xffffffff => osFailure(.native_failure, stage),
        else => failure(.native_failure, stage, 13),
    };
}
fn lifetime(comparison: *const w.ObjectComparison, request: *const Request) Result {
    const caller = observedExit(comparison, request.caller_exit_observer, request.caller_process, true);
    if (caller.status != .ok) return caller;
    return observedExit(comparison, request.target_exit_observer, request.target_reference, false);
}
fn noAmbientImpersonation() Result {
    var token: usize = 0;
    if (w.OpenThreadToken(w.GetCurrentThread(), w.token_query, 1, &token) != 0) {
        _ = w.CloseHandle(token);
        return failure(.ambient_impersonation, .token, 0);
    }
    if (w.GetLastError() != w.error_no_token) return osFailure(.native_failure, .token);
    return .{};
}
fn tokenLevel(token: usize) Result {
    var token_type: u32 = 0;
    var returned: u32 = 0;
    if (w.GetTokenInformation(token, 8, &token_type, @sizeOf(u32), &returned) == 0)
        return osFailure(.impersonation_failed, .token);
    if (returned != @sizeOf(u32)) return failure(.invalid_input, .token, 0);
    if (token_type == 1) return .{}; // Primary: Windows derives its same-context impersonation token.
    if (token_type != 2) return failure(.invalid_input, .token, 0);
    var level: u32 = 0;
    if (w.GetTokenInformation(token, 9, &level, @sizeOf(u32), &returned) == 0)
        return osFailure(.impersonation_failed, .token);
    if (returned != @sizeOf(u32)) return failure(.invalid_input, .token, 0);
    if (level < 2) return failure(.insufficient_impersonation_level, .token, 1346);
    return .{};
}
fn fatalRevert() noreturn {
    _ = w.TerminateProcess(w.GetCurrentProcess(), revert_failure_exit);
    w.ExitProcess(revert_failure_exit);
}
fn queryPath(handle: usize, path: []u16, flag: ?*const u32) Result {
    if (cancelled(flag)) return cancellation();
    var length: u32 = @intCast(path.len);
    if (w.QueryFullProcessImageNameW(handle, 0, path.ptr, &length) == 0)
        return osFailure(.query_failed, .query);
    if (length == 0 or length >= path.len) return failure(.query_failed, .query, 13);
    if (cancelled(flag)) return cancellation();
    return .{ .path_length = length };
}
fn finish(comparison: *const w.ObjectComparison, request: *const Request, handle: usize, path: []u16, flag: ?*const u32, capability: *Capability) Result {
    // The reference was checked as a real PROCESS object above. Equality proves
    // type and instance without asking any extra access of the caller's handle.
    if (!comparison.same(handle, request.target_reference))
        return failure(.target_mismatch, .compare_object, w.GetLastError());
    var basic: w.ObjectBasic = undefined;
    var returned: u32 = 0;
    const nt = w.NtQueryObject(handle, 0, &basic, @sizeOf(w.ObjectBasic), &returned);
    if (nt < 0) return .{ .status = .native_failure, .stage = .granted_access, .ntstatus = nt };
    if (returned != @sizeOf(w.ObjectBasic)) return failure(.native_failure, .granted_access, 13);
    var result = queryPath(handle, path, flag);
    if (result.status != .ok) return result;
    const target = identity(request.target_reference, request.target_pid, request.target_created_filetime, false);
    if (target.status != .ok) return target;
    const caller = identity(request.caller_process, request.caller_pid, request.caller_created_filetime, true);
    if (caller.status != .ok) return caller;
    const live = lifetime(comparison, request);
    if (live.status != .ok) return live;
    if (cancelled(flag)) return cancellation();
    result.granted_access = basic.granted_access;
    capability.* = .{ .handle = handle, .target_pid = request.target_pid, .target_created_filetime = request.target_created_filetime, .api = request.api, .granted_access = basic.granted_access, .route = request.route };
    return result;
}

pub fn acquire(request: *const Request, flag: ?*const u32, path: []u16, capability: *Capability) Result {
    return acquireWithReverter(w.RevertToSelf, request, flag, path, capability);
}
/// Compile-time seam for the external fatal-child fixture; no runtime fault ABI.
/// The production entry always instantiates this with the actual RevertToSelf.
pub fn acquireWithReverter(comptime revert: fn () callconv(.winapi) i32, request: *const Request, flag: ?*const u32, path: []u16, capability: *Capability) Result {
    @memset(path, 0);
    var result = acquireInner(revert, request, flag, path, capability);
    if (result.status != .ok) {
        @memset(path, 0);
        result.path_length = 0;
        result.granted_access = 0;
    }
    return result;
}
fn acquireInner(comptime revert: fn () callconv(.winapi) i32, request: *const Request, flag: ?*const u32, path: []u16, capability: *Capability) Result {
    if (capability.handle != 0 or path.len == 0 or path.len > std.math.maxInt(u32) or
        request.api != image_path_api or request.caller_pid == 0 or request.target_pid == 0 or
        request.caller_created_filetime == 0 or request.target_created_filetime == 0 or
        !realHandle(request.caller_process) or !realHandle(request.target_reference))
        return failure(.invalid_input, .input, 0);
    if (request.route != @backingInt(Route.open_under_caller) and request.route != @backingInt(Route.caller_held_handle))
        return failure(.invalid_input, .input, 0);
    if (cancelled(flag)) return cancellation();
    const comparison = w.ObjectComparison.open() orelse return osFailure(.unsupported, .compare_object);
    defer comparison.close();
    var check = noAmbientImpersonation();
    if (check.status != .ok) return check;
    check = identity(request.caller_process, request.caller_pid, request.caller_created_filetime, true);
    if (check.status != .ok) return check;
    check = identity(request.target_reference, request.target_pid, request.target_created_filetime, false);
    if (check.status != .ok) return check;
    check = lifetime(&comparison, request);
    if (check.status != .ok) return check;
    var handle: usize = 0;
    defer if (handle != 0) {
        _ = w.CloseHandle(handle);
    };
    if (request.route == @backingInt(Route.open_under_caller)) {
        if (!realHandle(request.caller_token) or request.caller_held_handle != 0) return failure(.invalid_input, .input, 0);
        check = tokenLevel(request.caller_token);
        if (check.status != .ok) return check;
        if (cancelled(flag)) return cancellation();
        if (w.ImpersonateLoggedOnUser(request.caller_token) == 0) return osFailure(.impersonation_failed, .impersonate);
        defer if (revert() == 0) {
            fatalRevert();
        };
        // No AdjustTokenPrivileges, linked token, SYNCHRONIZE or stronger retry.
        handle = w.OpenProcess(w.query_limited, 0, request.target_pid);
        if (handle == 0) return osFailure(.query_failed, .open);
        check = finish(&comparison, request, handle, path, flag, capability);
    } else {
        if (!realHandle(request.caller_held_handle)) return failure(.invalid_handle, .input, 6);
        // Handle-only API: no token impersonation is needed to exercise the
        // already-granted rights. The broker acquires no additional target right.
        if (w.DuplicateHandle(request.caller_process, request.caller_held_handle, w.GetCurrentProcess(), &handle, 0, 0, w.duplicate_same_access) == 0) return osFailure(.duplicate_failed, .duplicate);
        check = finish(&comparison, request, handle, path, flag, capability);
    }
    if (check.status == .ok) handle = 0; // one ownership transfer, only after actual Query
    return check;
}

/// Re-query only inside the STILL ACTIVE consuming scope, using the same object
/// and handle-only API. Its connection/exit cancellation belongs to the caller.
pub fn query(capability: *const Capability, target_exit_observer: usize, flag: ?*const u32, path: []u16) Result {
    @memset(path, 0);
    if (!realHandle(capability.handle) or capability.api != image_path_api or path.len == 0 or path.len > std.math.maxInt(u32))
        return failure(.invalid_input, .input, 0);
    if (cancelled(flag)) return cancellation();
    const comparison = w.ObjectComparison.open() orelse return osFailure(.unsupported, .compare_object);
    defer comparison.close();
    const ambient = noAmbientImpersonation();
    if (ambient.status != .ok) return ambient;
    const live = observedExit(&comparison, target_exit_observer, capability.handle, false);
    if (live.status != .ok) return live;
    var result = queryPath(capability.handle, path, flag);
    if (result.status == .ok) {
        const after = observedExit(&comparison, target_exit_observer, capability.handle, false);
        if (after.status != .ok) result = after;
    }
    if (result.status != .ok) {
        @memset(path, 0);
        result.path_length = 0;
    }
    if (result.status == .ok) result.granted_access = capability.granted_access;
    return result;
}
pub fn close(capability: *Capability) Result {
    const handle = capability.handle;
    if (handle == 0) return .{};
    capability.* = .{};
    if (w.CloseHandle(handle) == 0) return osFailure(.invalid_handle, .input);
    return .{};
}
