const std = @import("std");
const fixtures = @import("owner_tests.zig");
const types = @import("types.zig");

fn Storage(comptime Ledger: type) type {
    const Index = @TypeOf(@as(Ledger, undefined).index);
    const Destruction = Ledger.Destruction;
    const count = Ledger.resource_capacity + Ledger.partition_capacity;
    return struct {
        ids: [count]u32 = undefined,
        notices: [count]Destruction.Notice = undefined,
        views: [Ledger.table_count]types.TableView = undefined,
        homes: [count]u32 = undefined,
        nodes: [count]u32 = undefined,
        publications: [count]Index.Publication = undefined,
        removals: [count]Index.RemovePlan = undefined,
        pub fn workspace(self: *@This()) Destruction.Workspace {
            return .{ .remove_ids = &self.ids, .notices = &self.notices, .table_views = &self.views, .index_workspace = .{ .homes = &self.homes, .nodes = &self.nodes, .publications = &self.publications, .removals = &self.removals } };
        }
    };
}

fn Capture(comptime Uid: type) type {
    return struct {
        const Self = @This();
        count: usize = 0,
        last_uid: ?Uid = null,
        last_number: u64 = 0,
        pub fn callback(context: ?*anyopaque, uid: Uid, number: u64) callconv(.c) void {
            const self: *Self = @ptrCast(@alignCast(context.?));
            self.count += 1;
            self.last_uid = uid;
            self.last_number = number;
        }
    };
}

test "normal destruction preflights all tables before any release or notification" {
    inline for (.{ 128, 256 }) |bits| {
        const F = fixtures.Fixture(bits);
        const key = fixtures.key;
        var fixture = F{};
        try fixture.initialize();
        defer fixture.cleanup();
        const ledger = &fixture.ledger;
        var storage = Storage(F.Ledger){};
        var work = storage.workspace();
        var first_notices = Capture(F.Uid){};
        var second_notices = Capture(F.Uid){};
        _ = try ledger.subscribe(0, Capture(F.Uid).callback, &first_notices);
        _ = try ledger.subscribe(1, Capture(F.Uid).callback, &second_notices);
        const first = (try ledger.push(.{ .table = 0 }, key(F.Uid, 1), null, &fixture.work[0])).newly_allocated;
        first.region[0] = 0x7c;
        const second = (try ledger.push(.{ .table = 1 }, key(F.Uid, 22), 9, &fixture.work[1])).newly_allocated;
        _ = try ledger.endUse(second.lease, .complete);
        const before_first = try ledger.view(0);
        const before_second = try ledger.view(1);
        try std.testing.expectError(error.InUse, ledger.destroy(false, &work));
        try std.testing.expectEqual(@as(u8, 0x7c), first.region[0]);
        try std.testing.expectEqualDeep(before_first, try ledger.view(0));
        try std.testing.expectEqualDeep(before_second, try ledger.view(1));
        try std.testing.expectEqual(@as(usize, 0), first_notices.count + second_notices.count);
        _ = try ledger.endUse(first.lease, .complete);
        _ = try ledger.updateUid(key(F.Uid, 22), .{ .attributes = .{ .access_loss_impact = .fatal } });
        try std.testing.expectError(error.Blocked, ledger.destroy(false, &work));
        try std.testing.expectEqualDeep(before_first, try ledger.view(0));
        try std.testing.expectEqualDeep(before_second, try ledger.view(1));
        try std.testing.expect(fixture.rows[2].resource.registration.?.region != null);
        try std.testing.expectEqual(@as(usize, 0), first_notices.count + second_notices.count);
        _ = try ledger.updateUid(key(F.Uid, 22), .{ .attributes = .{ .access_loss_impact = .unobservable_now } });
        try ledger.destroy(false, &work);
        // The instance API is no longer called after successful destruction.
        for (&fixture.rows) |*row| try std.testing.expect(row.uid == null and row.resource.registration == null);
        for (&fixture.tables) |*table| {
            try std.testing.expectEqual(@as(u64, 0), table.registered_bytes);
            try std.testing.expectEqual(@as(u64, 0), table.system_page_bytes);
        }
        try std.testing.expectEqual(@as(usize, 1), first_notices.count);
        try std.testing.expectEqual(@as(usize, 1), second_notices.count);
        try std.testing.expect(first_notices.last_uid.?.eql(key(F.Uid, 1)));
        try std.testing.expect(second_notices.last_uid.?.eql(key(F.Uid, 22)));
        try std.testing.expectEqual(first.allocation_number, first_notices.last_number);
        try std.testing.expectEqual(second.allocation_number, second_notices.last_number);
    }
}

test "force destruction invalidates held allocations and failure before commit retains all state" {
    const F = fixtures.Fixture(256);
    const key = fixtures.key;
    var fixture = F{};
    try fixture.initialize();
    defer fixture.cleanup();
    var storage = Storage(F.Ledger){};
    var work = storage.workspace();
    const ledger = &fixture.ledger;
    const loaded = (try ledger.push(.{ .table = 0 }, key(F.Uid, 1), null, &fixture.work[0])).newly_allocated;
    _ = try ledger.endUse(loaded.lease, .complete);
    const held = (try ledger.useFixed(0)).in_memory;
    const other = (try ledger.push(.{ .table = 1 }, key(F.Uid, 2), 19, &fixture.work[1])).newly_allocated;
    const before_first = try ledger.view(0);
    const before_second = try ledger.view(1);
    const full_index_workspace = work.index_workspace;
    work.index_workspace = .{};
    try std.testing.expectError(error.WorkspaceTooSmall, ledger.destroy(true, &work));
    try std.testing.expectEqualDeep(before_first, try ledger.view(0));
    try std.testing.expectEqualDeep(before_second, try ledger.view(1));
    try std.testing.expectEqual(held.allocation_number, fixture.rows[2].resource.generation);
    try std.testing.expectEqual(@as(usize, 19), other.region.len);
    work.index_workspace = full_index_workspace;
    // No thread accesses either held region during or after forced destruction.
    try ledger.destroy(true, &work);
    for (&fixture.rows) |*row| try std.testing.expect(row.uid == null and row.resource.use_count == 0);
    for (&fixture.bindings) |*binding| try std.testing.expectEqual(types.no_slot, binding.slot.load(.acquire));
}
