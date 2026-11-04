//! C41 bounded file IO only. The caller owns JSON, admission, serialization,
//! directory preparation, cancellation and counters. Never reports its own error
//! through a logger: failure is returned once to the existing diagnostics owner.
const std = @import("std");
pub const invalid_parameter: u32 = 87;
const invalid_handle = std.math.maxInt(usize);
const file_not_found = 2;
const path_not_found = 3;

pub const Sink = struct {
    path: [:0]const u16,
    archive_path: [:0]const u16,
    rotate_bytes: u64,

    pub fn init(path: [:0]const u16, archive_path: [:0]const u16, rotate_bytes: u64) error{InvalidInput}!Sink {
        // Win32's extended-length path bound, not a product retention choice.
        if (path.len == 0 or path.len > 32767 or archive_path.len == 0 or archive_path.len > 32767 or
            rotate_bytes == 0 or std.mem.eql(u16, path, archive_path) or
            std.mem.indexOfScalar(u16, path, 0) != null or std.mem.indexOfScalar(u16, archive_path, 0) != null)
            return error.InvalidInput;
        return .{ .path = path, .archive_path = archive_path, .rotate_bytes = rotate_bytes };
    }

    // Synchronous single use: no handle, payload or borrowed path survives return.
    // Caller serializes append/rotation, as the old channel's single reader did.
    pub fn rotate(self: Sink) u32 {
        // FILE_READ_ATTRIBUTES: metadata inspection does not need read access.
        const existing = CreateFileW(self.path.ptr, 0x80, 0x7, null, 3, 0x80, 0);
        if (existing == invalid_handle) {
            const code = GetLastError();
            if (code != file_not_found and code != path_not_found) return code;
        } else {
            var length: i64 = 0;
            const sized = GetFileSizeEx(existing, &length);
            const code = if (sized == 0) GetLastError() else 0;
            _ = CloseHandle(existing);
            if (code != 0) return code;
            if (length < 0) return invalid_parameter;
            if (@as(u64, @intCast(length)) >= self.rotate_bytes) {
                // Preserve delete-old-archive then rename semantics; never silently
                // append to the full log after rotation failure.
                if (DeleteFileW(self.archive_path.ptr) == 0) {
                    const deleted = GetLastError();
                    if (deleted != file_not_found and deleted != path_not_found) return deleted;
                }
                if (MoveFileExW(self.path.ptr, self.archive_path.ptr, 0) == 0) return GetLastError();
            }
        }
        return 0;
    }

    // Separate from rotate: the existing caller rotates BEFORE serializing.
    // A serialization failure after rotation preserves the original effects.
    pub fn append(self: Sink, bytes: []const u8) u32 {
        const file = CreateFileW(self.path.ptr, 0x4, 0x1, null, 4, 0x80, 0); // FILE_APPEND_DATA, OPEN_ALWAYS
        if (file == invalid_handle) return GetLastError();
        defer _ = CloseHandle(file);
        var offset: usize = 0;
        while (offset < bytes.len) {
            var written: u32 = 0;
            const count: u32 = @intCast(@min(bytes.len - offset, std.math.maxInt(u32)));
            if (WriteFile(file, bytes.ptr + offset, count, &written, null) == 0)
                return GetLastError();
            if (written == 0 or written > count) return 29; // ERROR_WRITE_FAULT; do not spin on zero progress.
            offset += written;
        }
        return 0;
    }
};

extern "kernel32" fn CreateFileW([*:0]const u16, u32, u32, ?*anyopaque, u32, u32, usize) callconv(.winapi) usize;
extern "kernel32" fn GetFileSizeEx(usize, *i64) callconv(.winapi) i32;
extern "kernel32" fn WriteFile(usize, [*]const u8, u32, *u32, ?*anyopaque) callconv(.winapi) i32;
extern "kernel32" fn DeleteFileW([*:0]const u16) callconv(.winapi) i32;
extern "kernel32" fn MoveFileExW([*:0]const u16, [*:0]const u16, u32) callconv(.winapi) i32;
extern "kernel32" fn CloseHandle(usize) callconv(.winapi) i32;
extern "kernel32" fn GetLastError() callconv(.winapi) u32;
