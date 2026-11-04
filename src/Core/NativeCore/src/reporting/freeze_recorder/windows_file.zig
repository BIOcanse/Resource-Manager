const std = @import("std");
const ring = @import("ring.zig");
const windows = std.os.windows;
const HANDLE = windows.HANDLE;

// Win32 ABI constants, not product defaults.
pub const write_through: u32 = 0x80000000;
pub const open_flags: u32 = write_through | 0x00000080; // FILE_ATTRIBUTE_NORMAL
const generic_read: u32 = 0x80000000;
const generic_write: u32 = 0x40000000;
const share_read: u32 = 1;
const open_existing: u32 = 3;
const open_always: u32 = 4;

pub const CreateFunction = *const fn ([*:0]const u16, u32, u32, ?*anyopaque, u32, u32, ?HANDLE) callconv(.winapi) HANDLE;
extern "kernel32" fn CreateFileW([*:0]const u16, u32, u32, ?*anyopaque, u32, u32, ?HANDLE) callconv(.winapi) HANDLE;
extern "kernel32" fn CloseHandle(HANDLE) callconv(.winapi) i32;
extern "kernel32" fn GetFileSizeEx(HANDLE, *i64) callconv(.winapi) i32;
extern "kernel32" fn SetFilePointerEx(HANDLE, i64, ?*i64, u32) callconv(.winapi) i32;
extern "kernel32" fn SetEndOfFile(HANDLE) callconv(.winapi) i32;
extern "kernel32" fn ReadFile(HANDLE, [*]u8, u32, *u32, ?*anyopaque) callconv(.winapi) i32;
extern "kernel32" fn WriteFile(HANDLE, [*]const u8, u32, *u32, ?*anyopaque) callconv(.winapi) i32;

pub const WindowsFile = struct {
    handle: HANDLE,
    writable: bool,

    pub fn open(path: [*:0]const u16, writable: bool) !WindowsFile {
        return openWith(path, writable, CreateFileW);
    }

    // Injectable constructor verifies the actual CreateFileW flags in tests.
    pub fn openWith(path: [*:0]const u16, writable: bool, create: CreateFunction) !WindowsFile {
        const handle = create(path, generic_read | (if (writable) generic_write else @as(u32, 0)), share_read, null, if (writable) open_always else open_existing, open_flags, null);
        if (@intFromPtr(handle) == std.math.maxInt(usize)) return error.OpenFailed;
        return .{ .handle = handle, .writable = writable };
    }

    // Keep this owner at a stable address until the Ring is closed.
    pub fn ops(self: *WindowsFile) ring.FileOps {
        return .{ .context = self, .length = length, .set_length = setLength, .read_at = readAt, .write_at = writeAt, .close = close };
    }

    fn owner(context: ?*anyopaque) *WindowsFile {
        return @ptrCast(@alignCast(context.?));
    }
    fn seek(self: *WindowsFile, offset: u64) !void {
        if (offset > std.math.maxInt(i64)) return error.OffsetOverflow;
        if (SetFilePointerEx(self.handle, @intCast(offset), null, 0) == 0) return error.SeekFailed;
    }
    fn length(context: ?*anyopaque) !u64 {
        var size: i64 = 0;
        if (GetFileSizeEx(owner(context).handle, &size) == 0 or size < 0) return error.LengthFailed;
        return @intCast(size);
    }
    fn setLength(context: ?*anyopaque, size: u64) !void {
        const self = owner(context);
        if (!self.writable) return error.ReadOnly;
        try self.seek(size);
        if (SetEndOfFile(self.handle) == 0) return error.ResizeFailed;
    }
    fn readAt(context: ?*anyopaque, offset: u64, buffer: []u8) !usize {
        const self = owner(context);
        try self.seek(offset);
        var completed: u32 = 0;
        if (ReadFile(self.handle, buffer.ptr, @intCast(buffer.len), &completed, null) == 0)
            return error.ReadFailed;
        return completed;
    }
    fn writeAt(context: ?*anyopaque, offset: u64, buffer: []const u8) !usize {
        const self = owner(context);
        if (!self.writable) return error.ReadOnly;
        try self.seek(offset);
        var completed: u32 = 0;
        if (WriteFile(self.handle, buffer.ptr, @intCast(buffer.len), &completed, null) == 0)
            return error.WriteFailed;
        return completed;
    }
    pub fn close(context: ?*anyopaque) void {
        _ = CloseHandle(owner(context).handle);
    }
};
