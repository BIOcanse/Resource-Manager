const std = @import("std");
const windows = std.os.windows;

extern "kernel32" fn AddVectoredExceptionHandler(first: u32, handler: windows.VECTORED_EXCEPTION_HANDLER) callconv(.winapi) ?*anyopaque;
extern "kernel32" fn RemoveVectoredExceptionHandler(handle: *anyopaque) callconv(.winapi) u32;
extern "kernel32" fn GetCurrentThreadId() callconv(.winapi) u32;
extern "kernel32" fn GetLastError() callconv(.winapi) u32;

/// Full WER dumps must retain the original AV, even when CoreCLR's fatal-error
/// policy reports its own ProcessCLRException/GetCurrentIP location instead.
/// Owned by the native session; no file, allocation or managed call in the VEH.
pub const Snapshot = struct {
    signature: [8]u8 = "RMETWAV1".*,
    // 0: no AV, 1: copying (also rejects reentry), 2: immutable complete copy.
    state: std.atomic.Value(u32) = .init(0),
    thread_id: u32 = 0,
    registration_error: u32 = 0,
    removal_error: u32 = 0,
    exception_record: windows.EXCEPTION_RECORD = undefined,
    context: windows.CONTEXT = undefined,
};

// A process-wide VEH may run on any thread. Only the shared_process call's
// thread may touch its owner, and only until that call's defer returns it.
threadlocal var active: ?*Snapshot = null;

pub const Scope = struct {
    snapshot: *Snapshot,
    previous: ?*Snapshot,
    registration: ?*anyopaque,

    pub fn begin(snapshot: *Snapshot) Scope {
        const previous = active;
        snapshot.thread_id = GetCurrentThreadId();
        const registration = AddVectoredExceptionHandler(1, onException);
        if (registration == null) snapshot.registration_error = GetLastError();
        active = snapshot;
        return .{ .snapshot = snapshot, .previous = previous, .registration = registration };
    }

    pub fn end(self: Scope) void {
        active = self.previous;
        if (self.registration) |registration| {
            if (RemoveVectoredExceptionHandler(registration) == 0)
                self.snapshot.removal_error = GetLastError();
        }
    }
};

fn onException(info: *windows.EXCEPTION_POINTERS) callconv(.winapi) c_long {
    const snapshot = active orelse return windows.EXCEPTION_CONTINUE_SEARCH;
    if (info.ExceptionRecord.ExceptionCode != windows.EXCEPTION_ACCESS_VIOLATION)
        return windows.EXCEPTION_CONTINUE_SEARCH;
    if (snapshot.state.cmpxchgStrong(0, 1, .acquire, .monotonic) == null) {
        snapshot.exception_record = info.ExceptionRecord.*;
        snapshot.context = info.ContextRecord.*;
        snapshot.state.store(2, .release);
    }
    // Observe only. Windows/CoreCLR retain their original exception behavior.
    return windows.EXCEPTION_CONTINUE_SEARCH;
}

extern "kernel32" fn RaiseException(code: u32, flags: u32, count: u32, arguments: [*]const usize) callconv(.winapi) void;
const test_exception: u32 = 0xe042_1234;
fn resumeSynthetic(info: *windows.EXCEPTION_POINTERS) callconv(.winapi) c_long {
    const record = info.ExceptionRecord;
    // Test-only software exceptions with unmistakable parameters. Never resume
    // an actual hardware AV, nor any exception emitted by product code.
    if ((record.ExceptionCode == windows.EXCEPTION_ACCESS_VIOLATION or record.ExceptionCode == test_exception) and
        record.NumberParameters == 3 and record.ExceptionInformation[2] == test_exception and record.ExceptionInformation[0] == 1 and
        (record.ExceptionInformation[1] == 0x1234 or record.ExceptionInformation[1] == 0x5678)) return -1;
    return windows.EXCEPTION_CONTINUE_SEARCH;
}
fn raiseSynthetic(code: u32, address: usize) void {
    // Hardware AVs have two parameters; the third marks our continuable test.
    const arguments = [_]usize{ 1, address, test_exception };
    RaiseException(code, 0, arguments.len, &arguments);
}

test "ETW first fault preserves original Windows exception parameters and context" {
    // Install ahead of Zig's panic handler; Scope.begin then goes ahead of us.
    const catcher = AddVectoredExceptionHandler(1, resumeSynthetic) orelse return error.HandlerRegistrationFailed;
    defer std.debug.assert(RemoveVectoredExceptionHandler(catcher) != 0);
    var snapshot: Snapshot = .{};
    const scope = Scope.begin(&snapshot);
    defer scope.end();
    try std.testing.expect(scope.registration != null);
    raiseSynthetic(test_exception, 0x1234);
    try std.testing.expectEqual(@as(u32, 0), snapshot.state.load(.acquire));
    raiseSynthetic(windows.EXCEPTION_ACCESS_VIOLATION, 0x1234);
    try std.testing.expectEqual(@as(u32, 2), snapshot.state.load(.acquire));
    try std.testing.expectEqual(GetCurrentThreadId(), snapshot.thread_id);
    try std.testing.expectEqual(@as(u32, windows.EXCEPTION_ACCESS_VIOLATION), snapshot.exception_record.ExceptionCode);
    try std.testing.expectEqual(@as(u32, 3), snapshot.exception_record.NumberParameters);
    try std.testing.expectEqual(@as(usize, 1), snapshot.exception_record.ExceptionInformation[0]);
    try std.testing.expectEqual(@as(usize, 0x1234), snapshot.exception_record.ExceptionInformation[1]);
    try std.testing.expect(@intFromPtr(snapshot.exception_record.ExceptionAddress) != 0);
    try std.testing.expect(windows.CONTEXT.getRegs(&snapshot.context).ip != 0);
    try std.testing.expect(windows.CONTEXT.getRegs(&snapshot.context).sp != 0);
    // A subsequent CLR fail-fast/secondary AV must not replace the evidence.
    raiseSynthetic(windows.EXCEPTION_ACCESS_VIOLATION, 0x5678);
    try std.testing.expectEqual(@as(usize, 0x1234), snapshot.exception_record.ExceptionInformation[1]);
}

test "ETW first fault ignores other threads and ends with processing scope" {
    const catcher = AddVectoredExceptionHandler(1, resumeSynthetic) orelse return error.HandlerRegistrationFailed;
    defer std.debug.assert(RemoveVectoredExceptionHandler(catcher) != 0);
    var snapshot: Snapshot = .{};
    {
        const scope = Scope.begin(&snapshot);
        defer scope.end();
        try std.testing.expect(scope.registration != null);
        const other = try std.Thread.spawn(.{}, raiseSynthetic, .{ windows.EXCEPTION_ACCESS_VIOLATION, 0x1234 });
        other.join();
        try std.testing.expectEqual(@as(u32, 0), snapshot.state.load(.acquire));
    }
    try std.testing.expect(active == null);
    try std.testing.expectEqual(@as(u32, 0), snapshot.removal_error);
    raiseSynthetic(windows.EXCEPTION_ACCESS_VIOLATION, 0x5678);
    try std.testing.expectEqual(@as(u32, 0), snapshot.state.load(.acquire));
}
