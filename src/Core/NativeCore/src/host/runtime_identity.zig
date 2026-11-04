//! C4 host identity, distinct from durable operation session and clock records.
const std = @import("std");
const clock = @import("rm_windows_clock");
pub const Error = error{ ProcessUnavailable, RandomUnavailable, InvalidIdentity, Overflow };
pub const Identity = extern struct {
    instance_id: u64 = 0,
    process_created_utc_ticks: u64 = 0,
    process_id: u32 = 0,
    reserved: u32 = 0,
};
pub const Process = struct { id: u32, created_filetime: u64 };
pub const Source = struct {
    context: ?*anyopaque,
    process: *const fn (?*anyopaque) Error!Process,
    random: *const fn (?*anyopaque, []u8) Error!void,
};

// RuntimeRead: fixed per host construction. PID and birth are captured from
// the same borrowed current-process object; no retained handles/provider.
pub fn capture() Error!Identity {
    return captureFrom(.{ .context = null, .process = currentProcess, .random = randomBytes });
}
pub fn captureFrom(source: Source) Error!Identity {
    const process = try source.process(source.context);
    if (process.id == 0 or process.id > std.math.maxInt(i32) or process.created_filetime == 0)
        return error.InvalidIdentity;
    const utc = try clock.fromFileTime(process.created_filetime);
    var bytes: [@sizeOf(u64)]u8 = undefined;
    while (true) {
        try source.random(source.context, &bytes);
        const instance = std.mem.readInt(u64, &bytes, .little);
        if (instance != 0) return .{
            .instance_id = instance,
            .process_created_utc_ticks = utc.ticks,
            .process_id = process.id,
        };
    }
}
fn currentProcess(_: ?*anyopaque) Error!Process {
    var created: clock.FileTime = .{};
    var exited: clock.FileTime = .{};
    var kernel: clock.FileTime = .{};
    var user: clock.FileTime = .{};
    // GetCurrentProcess is a borrowed pseudo-handle: never close it.
    if (GetProcessTimes(GetCurrentProcess(), &created, &exited, &kernel, &user) == 0)
        return error.ProcessUnavailable;
    return .{ .id = GetCurrentProcessId(), .created_filetime = created.value() };
}
fn randomBytes(_: ?*anyopaque, bytes: []u8) Error!void {
    // BCRYPT_USE_SYSTEM_PREFERRED_RNG needs no provider handle.
    if (BCryptGenRandom(null, bytes.ptr, @intCast(bytes.len), 0x2) < 0)
        return error.RandomUnavailable;
}
extern "kernel32" fn GetCurrentProcess() callconv(.winapi) usize;
extern "kernel32" fn GetCurrentProcessId() callconv(.winapi) u32;
extern "kernel32" fn GetProcessTimes(usize, *clock.FileTime, *clock.FileTime, *clock.FileTime, *clock.FileTime) callconv(.winapi) i32;
extern "bcrypt" fn BCryptGenRandom(?*anyopaque, [*]u8, u32, u32) callconv(.winapi) i32;
