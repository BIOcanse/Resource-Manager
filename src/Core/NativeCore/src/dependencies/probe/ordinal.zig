//! Immutable case tables generated with .NET 10.0.14; no runtime shadow allocation.
pub threadlocal var profile: u32 = undefined;
extern "kernel32" fn CompareStringOrdinal([*]const u16, i32, [*]const u16, i32, i32) callconv(.winapi) i32;

fn word(bytes: []const u8, offset: usize) u32 {
    return @as(u32, bytes[offset]) | (@as(u32, bytes[offset + 1]) << 8) | (@as(u32, bytes[offset + 2]) << 16) | (@as(u32, bytes[offset + 3]) << 24);
}
fn fold(value: u32, active_profile: u32) u32 {
    const chars = switch (active_profile) {
        0 => @embedFile("dotnet-char-upper.bin"),
        1 => @embedFile("dotnet-char-upper.icu.bin"),
        2 => @embedFile("dotnet-char-upper.invariant.bin"),
        else => unreachable,
    };
    const runes: []const u8 = switch (active_profile) {
        0 => @embedFile("dotnet-rune-upper.bin"),
        1 => @embedFile("dotnet-rune-upper.icu.bin"),
        2 => @embedFile("dotnet-rune-upper.invariant.bin"),
        else => unreachable,
    };
    if (value <= 0xffff) {
        const index = @as(usize, value) * 2;
        return @as(u32, chars[index]) | (@as(u32, chars[index + 1]) << 8);
    }
    var first: usize = 0;
    var last: usize = runes.len / 8;
    while (first < last) {
        const middle = first + (last - first) / 2;
        const key = word(runes, middle * 8);
        if (key < value) first = middle + 1 else if (key > value) last = middle else return word(runes, middle * 8 + 4);
    }
    return value;
}
fn next(text: []const u16, index: *usize) u32 {
    const first = text[index.*];
    index.* += 1;
    if (first >= 0xd800 and first <= 0xdbff and index.* < text.len) {
        const second = text[index.*];
        if (second >= 0xdc00 and second <= 0xdfff) {
            index.* += 1;
            return 0x10000 + (@as(u32, first - 0xd800) << 10) + second - 0xdc00;
        }
    }
    return first;
}
pub fn equal(left: []const u16, right: []const u16, active_profile: u32) bool {
    if (left.len != right.len) return false;
    if (active_profile == 0 and left.len > 0) return CompareStringOrdinal(left.ptr, @intCast(left.len), right.ptr, @intCast(right.len), 1) == 2;
    var lhs: usize = 0;
    var rhs: usize = 0;
    while (lhs < left.len and rhs < right.len) if (fold(next(left, &lhs), active_profile) != fold(next(right, &rhs), active_profile)) return false;
    return lhs == left.len and rhs == right.len;
}
