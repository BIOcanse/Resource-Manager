const std = @import("std");

pub const maximum_request: u32 = 64 * 1024 * 1024;

pub const Region = extern struct {
    address: ?[*]u8 = null,
    length: u32 = 0,
};

// The previous address expires at the next supply or when sample returns.
// Collector never retains this provider or its region.
pub const Provider = extern struct {
    context: ?*anyopaque,
    supply: ?*const fn (?*anyopaque, u32, *Region) callconv(.c) i32,
};

pub const Scratch = struct {
    provider: Provider,
    region: Region = .{},

    pub fn ensure(self: *Scratch, required: u32) !void {
        if (required > maximum_request) return error.ArrayBufferTooLarge;
        if (required <= self.region.length) return;
        const supply = self.provider.supply orelse return error.InvalidScratch;
        // Even a failed supply can retire the previous address.
        self.region = .{};
        var next: Region = .{};
        if (supply(self.provider.context, required, &next) != 0) return error.OutOfMemory;
        const pointer = next.address orelse return error.InvalidScratch;
        const address = @intFromPtr(pointer);
        if (next.length < required or address % @alignOf(u64) != 0 or
            next.length > std.math.maxInt(usize) - address) return error.InvalidScratch;
        self.region = next;
    }
};

test "external scratch reuses one real region and invalidates it before failed growth" {
    const Fixture = struct {
        bytes: [256]u8 align(8) = undefined,
        calls: usize = 0,
        fail: bool = false,
        fn supply(context: ?*anyopaque, required: u32, output: *Region) callconv(.c) i32 {
            const self: *@This() = @ptrCast(@alignCast(context.?));
            self.calls += 1;
            if (self.fail or required > self.bytes.len) return -1;
            output.* = .{ .address = &self.bytes, .length = @intCast(self.bytes.len) };
            return 0;
        }
    };
    var fixture: Fixture = .{};
    var scratch: Scratch = .{ .provider = .{ .context = &fixture, .supply = Fixture.supply } };
    try scratch.ensure(16);
    scratch.region.address.?[15] = 73;
    try scratch.ensure(256);
    try std.testing.expectEqual(@as(usize, 1), fixture.calls);
    try std.testing.expectEqual(@as(u8, 73), fixture.bytes[15]);
    fixture.fail = true;
    try std.testing.expectError(error.OutOfMemory, scratch.ensure(257));
    try std.testing.expect(scratch.region.address == null);
}

test "external scratch rejects missing, short, unaligned and overflowing regions" {
    const Fixture = struct {
        region: Region,
        fn supply(context: ?*anyopaque, _: u32, output: *Region) callconv(.c) i32 {
            const self: *@This() = @ptrCast(@alignCast(context.?));
            output.* = self.region;
            return 0;
        }
    };
    var bytes: [16]u8 align(8) = undefined;
    for ([_]Region{
        .{},
        .{ .address = &bytes, .length = 7 },
        .{ .address = bytes[1..].ptr, .length = 8 },
        .{ .address = @ptrFromInt(std.math.maxInt(usize) - 7), .length = 16 },
    }) |region| {
        var fixture: Fixture = .{ .region = region };
        var scratch: Scratch = .{ .provider = .{ .context = &fixture, .supply = Fixture.supply } };
        try std.testing.expectError(error.InvalidScratch, scratch.ensure(8));
        try std.testing.expect(scratch.region.address == null);
    }
    var missing: Scratch = .{ .provider = .{ .context = null, .supply = null } };
    try std.testing.expectError(error.InvalidScratch, missing.ensure(8));
    try std.testing.expectError(error.ArrayBufferTooLarge, missing.ensure(maximum_request + 1));
}
