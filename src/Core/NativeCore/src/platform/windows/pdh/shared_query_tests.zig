const std = @import("std");
const query = @import("shared_query.zig");
const gpu = @import("gpu_owner.zig");
const Fake = struct {
    fail: u8 = 0, closes: u32 = 0, opens: u32 = 0, emitted: u32 = 0,
    result: gpu.SharedResult = .{}, bytes: u64 = 0, consume_after_close: bool = false,
    region: [2048]u8 align(8) = undefined,
    fn self(context: ?*anyopaque) *Fake { return @ptrCast(@alignCast(context.?)); }
    fn open(context: ?*anyopaque, handle: *usize) callconv(.c) i32 {
        const value = self(context); value.opens += 1; handle.* = 1;
        return if (value.fail == 1) 71 else 0;
    }
    fn add(context: ?*anyopaque, _: usize, _: [*:0]const u16, handle: *usize) callconv(.c) i32 {
        handle.* = 2; return if (self(context).fail == 2) 72 else 0;
    }
    fn collect(context: ?*anyopaque, _: usize) callconv(.c) i32 { return if (self(context).fail == 3) 73 else 0; }
    fn array(context: ?*anyopaque, _: usize, _: u32, length: *u32, count: *u32, buffer: ?[*]u8) callconv(.c) i32 {
        const value = self(context);
        if (buffer == null) {
            if (value.fail == 4) return 74;
            length.* = 200; count.* = 1;
            return @bitCast(@as(u32, 0x800007d2));
        }
        if (value.fail == 5) return 75;
        const raw = buffer.?;
        @memset(raw[0..length.*], 0);
        const name = std.unicode.utf8ToUtf16LeStringLiteral("pid_41_luid_0x00000000_0x00000017_phys_0");
        std.mem.writeInt(u64, raw[0..8], @intFromPtr(raw + 24), .little);
        std.mem.writeInt(i64, raw[16..24], 120, .little);
        for (name, 0..) |character, index| std.mem.writeInt(u16, raw[24 + index * 2 ..][0..2], character, .little);
        count.* = if (value.fail == 7) 2 else 1;
        return 0;
    }
    fn close(context: ?*anyopaque, _: usize) callconv(.c) i32 {
        const value = self(context); value.closes += 1;
        return if (value.fail == 6 or value.fail == 8) 76 else 0;
    }
    fn withRegion(context: ?*anyopaque, length: usize, work: query.Work, operation: ?*anyopaque) callconv(.c) u32 {
        const value = self(context);
        if (length > value.region.len or value.fail == 9) return 0;
        return work(operation, &value.region, length);
    }
    fn consume(context: ?*anyopaque, output: *const gpu.SharedResult, _: usize) callconv(.c) void {
        const value = self(context); value.emitted += 1;
        value.result = output.*;
        value.consume_after_close = value.closes == 1;
        if (output.count != 0) value.bytes = output.rows.?[0].bytes;
    }
    fn host(value: *Fake) query.Host {
        return .{ .context = value, .open = open, .add = add, .collect = collect, .array = array, .close = close,
            .workspace_context = value, .with_region = withRegion, .consume_context = value, .consume = consume };
    }
};
fn request(ids: []const u64) query.Request {
    return .{ .limits = .{ .bytes = 1000, .items = 8, .name_characters = 128 },
        .maximum_workspace = 2048, .requested = ids.ptr, .requested_count = ids.len, .process = 1 };
}
test "one demand scoped query closes before borrowed rows are consumed" {
    var fake: Fake = .{};
    const host = fake.host();
    const args = request(&.{23});
    try std.testing.expectEqual(@as(u32, 0), query.read(&host, &args));
    try std.testing.expect(fake.opens == 1 and fake.closes == 1 and fake.emitted == 1 and fake.consume_after_close);
    try std.testing.expectEqual(@as(u64, 120), fake.bytes);
    try std.testing.expectEqual(@as(u32, 0), fake.result.result);
}
test "all failure exits close once and close errors erase otherwise valid rows" {
    var fail: u8 = 1;
    while (fail <= 7) : (fail += 1) {
        var fake = Fake{ .fail = fail };
        const host = fake.host(); const args = request(&.{23});
        try std.testing.expectEqual(@as(u32, 0), query.read(&host, &args));
        try std.testing.expect(fake.closes == 1 and fake.emitted == 1 and fake.consume_after_close);
        try std.testing.expect(fake.result.count == 0 and fake.result.result == 8);
    }
}
test "empty selection opens nothing and workspace rejection closes without retaining rows" {
    var fake: Fake = .{}; const host = fake.host(); var args = request(&.{});
    try std.testing.expectEqual(@as(u32, 0), query.read(&host, &args));
    try std.testing.expect(fake.opens == 0 and fake.closes == 0 and fake.emitted == 1 and fake.result.flags == 1);
    fake = .{ .fail = 9 }; args = request(&.{23});
    try std.testing.expectEqual(@as(u32, 5), query.read(&host, &args));
    try std.testing.expect(fake.closes == 1 and fake.emitted == 1 and fake.result.count == 0);
}
test "adapter ambiguity counts display indices and suppresses collisions with duplicate LUID groups" {
    const select = query.selection;
    const topology = [_]select.Adapter{
        .{ .luid = 23, .index = 0, .software = 0 }, .{ .luid = 23, .index = 1, .software = 0 },
        .{ .luid = 24, .index = 0, .software = 0 }, .{ .luid = 25, .index = 7, .software = 0 },
    };
    try std.testing.expectEqual(@as(u32, 2), select.selectedCount(&topology));
    try std.testing.expectEqual(@as(u32, 2), select.ambiguousCount(&topology));
    try std.testing.expect(select.indexOf(&topology, 24) == null);
    try std.testing.expectEqual(@as(?u32, 7), select.indexOf(&topology, 25));
}
