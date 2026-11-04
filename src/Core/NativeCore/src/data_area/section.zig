//! One unnamed, pagefile-backed shared memory section and the owner's own
//! read-write view of it (backend_data_area §3: regions are not named; only a
//! process given a handle can use one). The handle stays with the region: step
//! 2 duplicates read-only handles from it for callers.
const std = @import("std");
const windows = std.os.windows;

const page_readwrite: u32 = 0x04;
const file_map_write: u32 = 0x0002;
const file_map_read: u32 = 0x0004;

extern "kernel32" fn CreateFileMappingW(
    file: windows.HANDLE,
    attributes: ?*anyopaque,
    protect: u32,
    maximum_size_high: u32,
    maximum_size_low: u32,
    name: ?[*:0]const u16,
) callconv(.winapi) ?windows.HANDLE;
extern "kernel32" fn MapViewOfFile(
    mapping: windows.HANDLE,
    desired_access: u32,
    offset_high: u32,
    offset_low: u32,
    bytes: usize,
) callconv(.winapi) ?*anyopaque;
extern "kernel32" fn UnmapViewOfFile(base: *const anyopaque) callconv(.winapi) i32;
extern "kernel32" fn CloseHandle(handle: windows.HANDLE) callconv(.winapi) i32;
extern "kernel32" fn GetLastError() callconv(.winapi) u32;

pub const Section = struct {
    handle: windows.HANDLE,
    view: []align(8) u8,

    /// Creates the section and maps it read-write. `byte_length` is nonzero.
    /// On failure `win32_error` holds GetLastError and nothing is held.
    pub fn create(byte_length: u64, win32_error: *u32) error{SectionFailed}!Section {
        win32_error.* = 0;
        if (byte_length == 0 or byte_length > std.math.maxInt(usize)) return error.SectionFailed;
        const handle = CreateFileMappingW(
            windows.INVALID_HANDLE_VALUE,
            null,
            page_readwrite,
            @truncate(byte_length >> 32),
            @truncate(byte_length),
            null,
        ) orelse {
            win32_error.* = GetLastError();
            return error.SectionFailed;
        };
        const base = MapViewOfFile(handle, file_map_read | file_map_write, 0, 0, @intCast(byte_length)) orelse {
            win32_error.* = GetLastError();
            _ = CloseHandle(handle);
            return error.SectionFailed;
        };
        const bytes: [*]align(8) u8 = @ptrCast(@alignCast(base));
        return .{ .handle = handle, .view = bytes[0..@intCast(byte_length)] };
    }

    /// Unmaps the owner's view and closes its handle. Readers given their own
    /// handles keep the section alive until they close them.
    pub fn destroy(self: Section) void {
        _ = UnmapViewOfFile(self.view.ptr);
        _ = CloseHandle(self.handle);
    }
};

test "an unnamed section maps zeroed and read-write" {
    var win32_error: u32 = 0;
    const section = try Section.create(64 * 1024, &win32_error);
    defer section.destroy();
    try std.testing.expectEqual(@as(usize, 64 * 1024), section.view.len);
    try std.testing.expectEqual(@as(u8, 0), section.view[4096]);
    section.view[4096] = 7;
    try std.testing.expectEqual(@as(u8, 7), section.view[4096]);
    try std.testing.expectError(error.SectionFailed, Section.create(0, &win32_error));
}
