//! Win32 ABI only. No product policy, cached identity or privilege adjustment.
pub const Handle = usize;
pub const FileTime = extern struct {
    low: u32 = 0,
    high: u32 = 0,
    pub fn value(self: FileTime) u64 {
        return (@as(u64, self.high) << 32) | self.low;
    }
};
// PUBLIC_OBJECT_BASIC_INFORMATION, winternl.h. GrantedAccess is not inferred
// from the desired access mask. NtQueryObject requires no extra process rights.
pub const ObjectBasic = extern struct {
    attributes: u32,
    granted_access: u32,
    handle_count: u32,
    pointer_count: u32,
    reserved: [10]u32,
};
pub const query_limited: u32 = 0x1000;
pub const duplicate_same_access: u32 = 0x2;
pub const token_query: u32 = 0x8;
pub const error_no_token: u32 = 1008;
pub extern "kernel32" fn OpenProcess(u32, i32, u32) callconv(.winapi) Handle;
pub extern "kernel32" fn GetCurrentProcess() callconv(.winapi) Handle;
pub extern "kernel32" fn GetCurrentThread() callconv(.winapi) Handle;
pub extern "kernel32" fn GetCurrentProcessId() callconv(.winapi) u32;
pub extern "kernel32" fn GetProcessId(Handle) callconv(.winapi) u32;
pub extern "kernel32" fn GetProcessTimes(Handle, *FileTime, *FileTime, *FileTime, *FileTime) callconv(.winapi) i32;
pub extern "kernel32" fn WaitForSingleObject(Handle, u32) callconv(.winapi) u32;
pub extern "kernel32" fn DuplicateHandle(Handle, Handle, Handle, *Handle, u32, i32, u32) callconv(.winapi) i32;
// Microsoft handleapi.h: Kernelbase.lib / Kernelbase.dll (NOT kernel32).
// The bundled Zig/MinGW import set has no kernelbase import library. Resolve
// this ONE API from System32 explicitly; absence is an unsupported result,
// never a different identity algorithm or a permission fallback.
pub const ObjectComparison = struct {
    module: Handle,
    compare: *const fn (Handle, Handle) callconv(.winapi) i32,
    pub fn open() ?ObjectComparison {
        const name = @import("std").unicode.utf8ToUtf16LeStringLiteral("kernelbase.dll");
        const module = LoadLibraryExW(name, 0, 0x800); // LOAD_LIBRARY_SEARCH_SYSTEM32
        if (module == 0) return null;
        const procedure = GetProcAddress(module, "CompareObjectHandles") orelse {
            const code = GetLastError();
            _ = FreeLibrary(module);
            SetLastError(code);
            return null;
        };
        return .{ .module = module, .compare = @ptrCast(procedure) };
    }
    pub fn same(self: ObjectComparison, first: Handle, second: Handle) bool {
        SetLastError(0);
        return self.compare(first, second) != 0;
    }
    pub fn close(self: ObjectComparison) void {
        _ = FreeLibrary(self.module);
    }
};
extern "kernel32" fn LoadLibraryExW([*:0]const u16, Handle, u32) callconv(.winapi) Handle;
extern "kernel32" fn GetProcAddress(Handle, [*:0]const u8) callconv(.winapi) ?*anyopaque;
extern "kernel32" fn FreeLibrary(Handle) callconv(.winapi) i32;
pub extern "kernel32" fn QueryFullProcessImageNameW(Handle, u32, [*]u16, *u32) callconv(.winapi) i32;
pub extern "kernel32" fn CloseHandle(Handle) callconv(.winapi) i32;
pub extern "kernel32" fn GetLastError() callconv(.winapi) u32;
pub extern "kernel32" fn SetLastError(u32) callconv(.winapi) void;
pub extern "kernel32" fn TerminateProcess(Handle, u32) callconv(.winapi) i32;
pub extern "kernel32" fn ExitProcess(u32) callconv(.winapi) noreturn;
pub extern "advapi32" fn OpenThreadToken(Handle, u32, i32, *Handle) callconv(.winapi) i32;
pub extern "advapi32" fn OpenProcessToken(Handle, u32, *Handle) callconv(.winapi) i32;
pub extern "advapi32" fn GetTokenInformation(Handle, u32, *anyopaque, u32, *u32) callconv(.winapi) i32;
pub extern "advapi32" fn ImpersonateLoggedOnUser(Handle) callconv(.winapi) i32;
pub extern "advapi32" fn RevertToSelf() callconv(.winapi) i32;
pub extern "ntdll" fn NtQueryObject(Handle, u32, *anyopaque, u32, *u32) callconv(.winapi) i32;
