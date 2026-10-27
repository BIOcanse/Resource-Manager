// One translation unit keeps Windows header parsing within the build's bound.
pub const c = @import("backend/c.zig").win;
pub const L = @import("std").unicode.utf8ToUtf16LeStringLiteral;

pub fn stopped(stop: c.HANDLE) bool {
    return c.WaitForSingleObject(stop, 0) == c.WAIT_OBJECT_0;
}

pub fn waitIo(stop: c.HANDLE, handle: c.HANDLE, operation: *c.OVERLAPPED) !u32 {
    const handles = [_]c.HANDLE{ stop, operation.hEvent };
    const result = c.WaitForMultipleObjects(handles.len, &handles, c.FALSE, c.INFINITE);
    if (result != c.WAIT_OBJECT_0 + 1) {
        _ = c.CancelIoEx(handle, operation);
        var ignored: c.DWORD = 0;
        _ = c.GetOverlappedResult(handle, operation, &ignored, c.TRUE);
        return error.Stopped;
    }
    var transferred: c.DWORD = 0;
    if (c.GetOverlappedResult(handle, operation, &transferred, c.FALSE) == 0) return error.PipeIo;
    return transferred;
}
