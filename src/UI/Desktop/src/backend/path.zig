const std = @import("std");
const c = @import("c.zig").win;
pub const capacity = 32768;
pub const Buffer = [capacity]u16;

pub fn full(input: []const u16, output: *Buffer) ![:0]const u16 {
    if (input.len == 0 or input.len >= capacity or std.mem.indexOfScalar(u16, input, 0) != null) return error.InvalidPath;
    var source: Buffer = undefined;
    @memcpy(source[0..input.len], input);
    source[input.len] = 0;
    const n = c.GetFullPathNameW(&source, capacity, output, null);
    if (n == 0 or n >= capacity) return error.InvalidPath;
    return output[0..n :0];
}

pub fn equal(a: []const u16, b: []const u16) bool {
    if (a.len > std.math.maxInt(c_int) or b.len > std.math.maxInt(c_int)) return false;
    return c.CompareStringOrdinal(a.ptr, @intCast(a.len), b.ptr, @intCast(b.len), c.TRUE) == c.CSTR_EQUAL;
}

pub fn rootLength(path: []const u16) !usize {
    if (path.len >= 3 and path[1] == ':' and path[2] == '\\') return 3;
    if (path.len >= 7 and std.mem.eql(u16, path[0..4], &std.unicode.utf8ToUtf16LeStringLiteral("\\\\?\\").*) and path[5] == ':' and path[6] == '\\') return 7;
    var offset: usize = 2;
    if (path.len >= 8 and equal(path[0..8], std.unicode.utf8ToUtf16LeStringLiteral("\\\\?\\UNC\\"))) offset = 8;
    if (path.len < offset or path[0] != '\\' or path[1] != '\\') return error.InvalidPath;
    var separators: usize = 0;
    while (offset < path.len) : (offset += 1) {
        if (path[offset] == '\\') {
            separators += 1;
            if (separators == 2) return offset + 1;
        }
    }
    if (separators == 1) return path.len;
    return error.InvalidPath;
}
