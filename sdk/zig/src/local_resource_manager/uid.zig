const std = @import("std");

/// The adopter supplies the bytes. Width is a build fact selected by LedgerPoints.
pub fn Identifier(comptime bits: u16) type {
    if (bits != 128 and bits != 256) @compileError("ledger UID width must be 128 or 256 bits");
    return extern struct {
        bytes: [bits / 8]u8,

        const Self = @This();
        pub const width_bits = bits;

        pub fn order(left: Self, right: Self) std.math.Order {
            return std.mem.order(u8, &left.bytes, &right.bytes);
        }

        pub fn eql(left: Self, right: Self) bool {
            return std.mem.eql(u8, &left.bytes, &right.bytes);
        }

        /// FNV-1a over every byte, shared by both D1 comparison candidates.
        pub fn hash(value: Self) u64 {
            var result: u64 = 14695981039346656037;
            for (value.bytes) |byte| {
                result ^= byte;
                result *%= 1099511628211;
            }
            return result;
        }
    };
}

pub const Uid128 = Identifier(128);
pub const Uid256 = Identifier(256);

test "opaque UID layout has no padding or business validation" {
    try std.testing.expectEqual(@as(usize, 16), @sizeOf(Uid128));
    try std.testing.expectEqual(@as(usize, 32), @sizeOf(Uid256));
    try std.testing.expectEqual(@as(usize, 1), @alignOf(Uid128));
    try std.testing.expectEqual(@as(usize, 1), @alignOf(Uid256));
    try std.testing.expectEqual(@as(usize, 0), @offsetOf(Uid256, "bytes"));
    const zero: Uid256 = .{ .bytes = @splat(0) };
    try std.testing.expect(zero.eql(zero));
}

test "every UID byte participates in equality ordering and hash" {
    inline for (.{ Uid128, Uid256 }) |T| {
        var original: T = undefined;
        for (&original.bytes, 0..) |*byte, i| byte.* = @intCast(i);
        for (0..original.bytes.len) |i| {
            var changed = original;
            changed.bytes[i] ^= 0x80;
            try std.testing.expect(!original.eql(changed));
            try std.testing.expect(original.order(changed) == .lt);
            try std.testing.expect(original.hash() != changed.hash());
        }
    }
}
