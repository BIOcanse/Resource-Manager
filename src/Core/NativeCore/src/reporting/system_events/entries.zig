const std = @import("std");
pub const State = enum { active, history };
pub const RawEvent = struct {
    channel: []const u8,
    provider: []const u8,
    eventId: u32,
    recordId: u64,
    at: i64,
    xml: []const u8,
    bugcheckCode: ?u32 = null,
    dumpPath: ?[]const u8 = null,
};
pub const Entry = struct {
    schemaVersion: u32 = 1,
    id: []const u8,
    category: []const u8,
    target: []const u8,
    state: State = .active,
    count: u64 = 0,
    firstObservedAt: i64,
    lastObservedAt: i64,
    detailsRevision: u64 = 0,
    events: []const RawEvent = &.{},
};
pub fn identity(category: []const u8, target: []const u8, out: *[64]u8) void {
    var hash = std.crypto.hash.sha2.Sha256.init(.{});
    hash.update(category);
    hash.update(&.{0});
    hash.update(target);
    out.* = std.fmt.bytesToHex(hash.finalResult(), .lower);
}
pub fn merge(a: std.mem.Allocator, item: *Entry, event: RawEvent, tail_capacity: usize) !bool {
    for (item.events) |old| if (old.recordId == event.recordId and old.at == event.at and std.mem.eql(u8, old.channel, event.channel)) return false;
    const n = @min(item.events.len + 1, tail_capacity);
    if (n == 0) return error.InvalidConfig;
    const tail = try a.alloc(RawEvent, n);
    const retained = n - 1;
    @memcpy(tail[0..retained], item.events[item.events.len - retained ..]);
    tail[retained] = event;
    item.events = tail;
    item.count += 1;
    item.firstObservedAt = @min(item.firstObservedAt, event.at);
    item.lastObservedAt = @max(item.lastObservedAt, event.at);
    item.state = .active;
    item.detailsRevision += 1;
    return true;
}
pub fn expire(item: *Entry, now_ms: i64, history_after_ms: i64) bool {
    if (item.state != .active or now_ms < item.lastObservedAt or now_ms - item.lastObservedAt < history_after_ms) return false;
    item.state = .history;
    item.detailsRevision += 1;
    return true;
}
test "same category target merges bounded raw evidence and reactivates history" {
    var arena = std.heap.ArenaAllocator.init(std.testing.allocator);
    defer arena.deinit();
    const a = arena.allocator();
    var entry: Entry = .{ .id = "id", .category = "storage-retry-reset", .target = "disk:2", .firstObservedAt = 10, .lastObservedAt = 10 };
    var event: RawEvent = .{ .channel = "System", .provider = "disk", .eventId = 153, .recordId = 1, .at = 10, .xml = "xml" };
    try std.testing.expect(try merge(a, &entry, event, 2));
    try std.testing.expect(!try merge(a, &entry, event, 2));
    event.recordId = 2;
    event.at = 20;
    _ = try merge(a, &entry, event, 2);
    event.recordId = 3;
    event.at = 30;
    _ = try merge(a, &entry, event, 2);
    try std.testing.expectEqual(@as(u64, 3), entry.count);
    try std.testing.expectEqual(@as(usize, 2), entry.events.len);
    try std.testing.expect(!expire(&entry, 129, 100));
    try std.testing.expect(expire(&entry, 130, 100));
    event.recordId = 4;
    event.at = 140;
    _ = try merge(a, &entry, event, 2);
    try std.testing.expectEqual(State.active, entry.state);
    try std.testing.expectEqual(@as(i64, 10), entry.firstObservedAt);
}
