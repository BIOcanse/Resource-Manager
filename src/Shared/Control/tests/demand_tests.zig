const std = @import("std");
const control = @import("rm_control");
const demand = control.demand;
fn reason(owner: []const u8, cause: []const u8) demand.Reason {
    return .{ .kind = 2, .owner = owner, .cause = cause, .plan_version = 7 };
}

test "demand copies caller texts and updates a key instead of duplicating it" {
    var table = try demand.Demands.init(std.testing.allocator, 3, 128);
    defer table.deinit();
    var key = [_]u8{ 'k', 'e', 'y' };
    var owner = [_]u8{ 'u', 'i' };
    var cause = [_]u8{ 'o', 'p', 'e', 'n' };
    const first = try table.set(&key, 2, reason(&owner, &cause));
    try std.testing.expectEqual(@as(?u32, null), first.previous);
    @memset(&key, 'x');
    @memset(&owner, 'x');
    @memset(&cause, 'x');
    const entry = table.entries[table.find("key").?];
    try std.testing.expectEqualStrings("key", entry.key);
    try std.testing.expectEqualStrings("ui", entry.reason.owner);
    try std.testing.expectEqualStrings("open", entry.reason.cause);
    try std.testing.expectEqual(@as(u64, 7), entry.reason.plan_version);
    const updated = try table.set("key", 3, reason("service", "changed"));
    try std.testing.expectEqual(@as(?u32, 2), updated.previous);
    try std.testing.expectEqual(@as(?u32, 3), updated.current);
    try std.testing.expectEqual(@as(usize, 1), table.count);
    try std.testing.expect(!table.has(2));
    try std.testing.expect(table.has(3));
}

test "demand remains active until the final key for its block is removed" {
    var table = try demand.Demands.init(std.testing.allocator, 3, 128);
    defer table.deinit();
    _ = try table.set("one", 2, reason("owner", "first"));
    _ = try table.set("two", 2, reason("owner", "second"));
    const cleared = try table.clear("one");
    try std.testing.expectEqual(@as(?u32, 2), cleared.previous);
    try std.testing.expectEqual(@as(?u32, null), cleared.current);
    try std.testing.expect(table.has(2));
    _ = try table.clear("two");
    try std.testing.expect(!table.has(2));
    try std.testing.expectEqual(@as(usize, 0), table.current());
}

test "full demand entries and text allocator return errors without losing old demand" {
    var table = try demand.Demands.init(std.testing.allocator, 1, 9);
    defer table.deinit();
    _ = try table.set("k", 2, reason("own", "cause"));
    const occupied = table.current();
    try std.testing.expectError(error.DemandFull, table.set("other", 3, reason("own", "cause")));
    try std.testing.expectError(error.DemandFull, table.set("k", 3, reason("owner", "too long")));
    try std.testing.expect(table.has(2));
    try std.testing.expect(!table.has(3));
    try std.testing.expectEqual(occupied, table.current());
    try std.testing.expectEqual(occupied, table.peak);
    _ = try table.clear("k");
    try std.testing.expectEqual(@as(usize, 0), table.current());
    _ = try table.set("n", 3, reason("own", "cause"));
    try std.testing.expect(table.has(3));
}

test "demand compaction reuses released bytes and preserves remaining texts" {
    var table = try demand.Demands.init(std.testing.allocator, 3, 27);
    defer table.deinit();
    _ = try table.set("a", 2, reason("own", "first"));
    _ = try table.set("b", 3, reason("own", "other"));
    _ = try table.set("c", 4, reason("own", "third"));
    _ = try table.clear("b");
    const last = table.entries[table.find("c").?];
    try std.testing.expectEqualStrings("own", last.reason.owner);
    try std.testing.expectEqualStrings("third", last.reason.cause);
    _ = try table.set("d", 5, reason("own", "after"));
    try std.testing.expectEqual(@as(usize, 27), table.used_bytes);
    _ = try table.set("a", 6, reason("x", "y"));
    try std.testing.expectEqual(@as(usize, 21), table.used_bytes);
    try std.testing.expectEqualStrings("after", table.entries[table.find("d").?].reason.cause);
    try std.testing.expectEqualStrings("third", table.entries[table.find("c").?].reason.cause);
}

test "demand mutations allocate nothing beyond the bounded buffers" {
    var failing = std.testing.FailingAllocator.init(std.testing.allocator, .{});
    var table = try demand.Demands.init(failing.allocator(), 2, 64);
    defer table.deinit();
    failing.fail_index = failing.alloc_index;
    for (0..1000) |_| {
        _ = try table.set("one", 2, reason("owner", "one"));
        _ = try table.set("two", 3, reason("owner", "two"));
        _ = try table.clear("one");
        _ = try table.set("two", 4, reason("owner", "replace"));
        _ = try table.clear("two");
    }
    try std.testing.expect(!failing.has_induced_failure);
    try std.testing.expectEqual(@as(usize, 0), table.current());
    try std.testing.expect(table.peak <= table.capacity());
}

test "demand requires an explicit key block owner and cause" {
    var table = try demand.Demands.init(std.testing.allocator, 2, 64);
    defer table.deinit();
    try std.testing.expectError(error.InvalidDemand, table.set("", 2, reason("owner", "cause")));
    try std.testing.expectError(error.InvalidDemand, table.set("key", 0, reason("owner", "cause")));
    try std.testing.expectError(error.InvalidDemand, table.set("key", 2, reason("", "cause")));
    try std.testing.expectError(error.InvalidDemand, table.set("key", 2, reason("owner", "")));
    try std.testing.expectError(error.InvalidDemand, table.clear(""));
    try std.testing.expectEqual(@as(?u32, null), (try table.clear("missing")).previous);
}
