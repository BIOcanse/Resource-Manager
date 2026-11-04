/// Narrow adapter to OS process identity. Owners retain no process handles
/// between calls; snapshot verification reads twice from one opened handle.
pub const Source = struct {
    context: ?*anyopaque = null,
    open_process: *const fn (?*anyopaque, u32) ?*anyopaque,
    read_process_id: *const fn (?*anyopaque, *anyopaque) ?u32,
    read_creation_filetime: *const fn (?*anyopaque, *anyopaque) ?u64,
    close_process: *const fn (?*anyopaque, *anyopaque) void,
};

pub const Lease = struct {
    source: Source,
    handle: *anyopaque,
    process_id: u32,

    pub fn open(source: Source, process_id: u32) ?Lease {
        const handle = source.open_process(source.context, process_id) orelse return null;
        const actual_id = source.read_process_id(source.context, handle) orelse {
            source.close_process(source.context, handle);
            return null;
        };
        if (actual_id != process_id) {
            source.close_process(source.context, handle);
            return null;
        }
        return .{ .source = source, .handle = handle, .process_id = process_id };
    }

    pub fn creationFiletime(self: Lease) ?u64 {
        const value = self.source.read_creation_filetime(self.source.context, self.handle) orelse return null;
        return if (value > 0) value else null;
    }

    pub fn close(self: Lease) void {
        self.source.close_process(self.source.context, self.handle);
    }
};

const FileTime = extern struct { low: u32, high: u32 };
extern "kernel32" fn OpenProcess(access: u32, inherit: i32, pid: u32) callconv(.winapi) ?*anyopaque;
extern "kernel32" fn GetProcessId(handle: *anyopaque) callconv(.winapi) u32;
extern "kernel32" fn GetProcessTimes(handle: *anyopaque, creation: *FileTime, exit: *FileTime, kernel: *FileTime, user: *FileTime) callconv(.winapi) i32;
extern "kernel32" fn CloseHandle(handle: *anyopaque) callconv(.winapi) i32;
/// One PROCESS_QUERY_LIMITED_INFORMATION handle per verification, never cached.
pub const windows: Source = .{ .open_process = windowsOpen, .read_process_id = windowsPid, .read_creation_filetime = windowsBirth, .close_process = windowsClose };
fn windowsOpen(_: ?*anyopaque, pid: u32) ?*anyopaque {
    return OpenProcess(0x1000, 0, pid);
}
fn windowsPid(_: ?*anyopaque, handle: *anyopaque) ?u32 {
    const value = GetProcessId(handle);
    return if (value == 0) null else value;
}
fn windowsBirth(_: ?*anyopaque, handle: *anyopaque) ?u64 {
    var creation: FileTime = undefined;
    var exit: FileTime = undefined;
    var kernel: FileTime = undefined;
    var user: FileTime = undefined;
    if (GetProcessTimes(handle, &creation, &exit, &kernel, &user) == 0) return null;
    return @as(u64, creation.high) << 32 | creation.low;
}
fn windowsClose(_: ?*anyopaque, handle: *anyopaque) void {
    _ = CloseHandle(handle);
}
