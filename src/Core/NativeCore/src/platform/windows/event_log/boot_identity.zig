//! Read-only startup fact, not a metric reader. Layout follows System Informer's
//! phnt/ntexapi.h SYSTEM_BOOT_ENVIRONMENT_INFORMATION (class 90).
//! https://github.com/winsiderss/phnt/blob/master/ntexapi.h
const BootEnvironment = extern struct { id: [16]u8, firmware: u32, flags: u64 };
extern "ntdll" fn NtQuerySystemInformation(u32, *anyopaque, u32, ?*u32) callconv(.winapi) i32;
// RuntimeRead: Fixed
pub fn read() ![16]u8 {
    var value: BootEnvironment = undefined;
    if (NtQuerySystemInformation(90, &value, @sizeOf(BootEnvironment), null) < 0) return error.BootIdentityUnavailable;
    return value.id;
}
