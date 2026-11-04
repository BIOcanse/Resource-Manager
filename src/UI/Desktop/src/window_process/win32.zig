const std = @import("std");
pub const HANDLE = std.os.windows.HANDLE;
pub const Startup = std.os.windows.STARTUPINFOW;
pub const Security = extern struct { length: u32 = @sizeOf(Security), descriptor: ?*anyopaque = null, inherit: c_int = 1 };
pub const StartupEx = extern struct { startup: Startup, attributes: ?*anyopaque };
pub const Process = extern struct { process: HANDLE, thread: HANDLE, pid: u32, tid: u32 };
pub const JobLimits = extern struct {
    basic: extern struct {
        process_time: i64 = 0,
        job_time: i64 = 0,
        flags: u32 = 0x00002000,
        minimum_working_set: usize = 0,
        maximum_working_set: usize = 0,
        active_processes: u32 = 0,
        affinity: usize = 0,
        priority: u32 = 0,
        scheduling: u32 = 0,
    } = .{},
    io: [6]u64 = @splat(0),
    process_memory: usize = 0,
    job_memory: usize = 0,
    peak_process_memory: usize = 0,
    peak_job_memory: usize = 0,
};
pub extern "kernel32" fn CreateJobObjectW(?*anyopaque, ?[*:0]const u16) callconv(.winapi) ?HANDLE;
pub extern "kernel32" fn SetInformationJobObject(HANDLE, u32, *const JobLimits, u32) callconv(.winapi) c_int;
pub extern "kernel32" fn ResumeThread(HANDLE) callconv(.winapi) u32;
pub extern "kernel32" fn TerminateProcess(HANDLE, u32) callconv(.winapi) c_int;
pub extern "kernel32" fn OpenProcess(u32, c_int, u32) callconv(.winapi) ?HANDLE;
pub extern "kernel32" fn GetTickCount64() callconv(.winapi) u64;
pub extern "kernel32" fn CreatePipe(*HANDLE, *HANDLE, *const Security, u32) callconv(.winapi) c_int;
pub extern "kernel32" fn SetHandleInformation(HANDLE, u32, u32) callconv(.winapi) c_int;
pub extern "kernel32" fn GetHandleInformation(HANDLE, *u32) callconv(.winapi) c_int;
pub extern "kernel32" fn InitializeProcThreadAttributeList(?*anyopaque, u32, u32, *usize) callconv(.winapi) c_int;
pub extern "kernel32" fn UpdateProcThreadAttribute(*anyopaque, u32, usize, *anyopaque, usize, ?*anyopaque, ?*usize) callconv(.winapi) c_int;
pub extern "kernel32" fn DeleteProcThreadAttributeList(*anyopaque) callconv(.winapi) void;
pub extern "kernel32" fn CreateProcessW(?[*:0]const u16, [*:0]u16, ?*anyopaque, ?*anyopaque, c_int, u32, ?*anyopaque, ?[*:0]const u16, *StartupEx, *Process) callconv(.winapi) c_int;
pub extern "kernel32" fn CloseHandle(HANDLE) callconv(.winapi) c_int;
pub extern "kernel32" fn ReadFile(HANDLE, [*]u8, u32, *u32, ?*anyopaque) callconv(.winapi) c_int;
pub extern "kernel32" fn WriteFile(HANDLE, [*]const u8, u32, *u32, ?*anyopaque) callconv(.winapi) c_int;
pub extern "kernel32" fn CreateEventW(?*anyopaque, c_int, c_int, ?[*:0]const u16) callconv(.winapi) ?HANDLE;
pub extern "kernel32" fn SetEvent(HANDLE) callconv(.winapi) c_int;
pub extern "kernel32" fn ResetEvent(HANDLE) callconv(.winapi) c_int;
pub extern "kernel32" fn WaitForSingleObject(HANDLE, u32) callconv(.winapi) u32;
pub extern "kernel32" fn WaitForMultipleObjects(u32, [*]const HANDLE, c_int, u32) callconv(.winapi) u32;
pub extern "kernel32" fn GetExitCodeProcess(HANDLE, *u32) callconv(.winapi) c_int;
pub extern "kernel32" fn CancelSynchronousIo(HANDLE) callconv(.winapi) c_int;
pub extern "kernel32" fn AcquireSRWLockExclusive(*usize) callconv(.winapi) void;
pub extern "kernel32" fn ReleaseSRWLockExclusive(*usize) callconv(.winapi) void;
pub extern "kernel32" fn GetCurrentProcessId() callconv(.winapi) u32;
pub extern "kernel32" fn ExitProcess(u32) callconv(.winapi) noreturn;
pub const infinite: u32 = 0xffffffff;
pub fn close(handle: HANDLE) void {
    _ = CloseHandle(handle);
}
pub const Mutex = struct {
    value: usize = 0,
    pub fn lock(self: *Mutex) void {
        AcquireSRWLockExclusive(&self.value);
    }
    pub fn unlock(self: *Mutex) void {
        ReleaseSRWLockExclusive(&self.value);
    }
};
