const std = @import("std");
const control = @import("rm_control");
const state = control.state;
const d = control.declarations;
const blocks = [_]d.Block{ .{ .uid = 1, .name = null }, .{ .uid = 2, .name = null }, .{ .uid = 3, .name = null } };
const items = [_]d.Item{
    .{ .uid = 10, .storage = .own, .value_type = .bool, .per_block = false, .trigger = .event, .owner_block = null, .name = null },
    .{ .uid = 11, .storage = .reference, .value_type = .bool, .per_block = false, .trigger = .event, .owner_block = 2, .name = null },
    .{ .uid = 12, .storage = .reference, .value_type = .bool, .per_block = true, .trigger = .event, .owner_block = null, .name = null },
};
const registry: d.Registry = .{ .version = "fixture", .process = "fixture", .blocks = &blocks, .items = &items, .conditions = &.{} };

test "state claims require declared storage and exactly one owner" {
    var authority = try state.State.init(std.testing.allocator, registry);
    defer authority.deinit();
    try std.testing.expectError(error.StorageMismatch, authority.claimCell(.{ .item = 10 }));
    const writer = try authority.claimWrite(.{ .item = 10 });
    _ = try authority.publish(writer, .{ .value = false });
    try std.testing.expectError(error.AlreadyClaimed, authority.claimWrite(.{ .item = 10 }));
    try std.testing.expect(!(try authority.read(.{ .item = 10 })).value);
    try std.testing.expectError(error.StorageMismatch, authority.claimWrite(.{ .item = 11 }));
    const cell = try authority.claimCell(.{ .item = 11 });
    _ = try authority.publish(cell, .{ .value = true });
    try std.testing.expectError(error.AlreadyClaimed, authority.claimCell(.{ .item = 11 }));
    try std.testing.expect((try authority.read(.{ .item = 11 })).value);
    try std.testing.expectError(error.InvalidClaim, authority.publish(@ptrFromInt(1), .{ .value = true }));
}

test "state rejects unknown items and invalid per-block keys" {
    var authority = try state.State.init(std.testing.allocator, registry);
    defer authority.deinit();
    try std.testing.expectError(error.UnregisteredItem, authority.read(.{ .item = 999 }));
    try std.testing.expectError(error.UnregisteredItem, authority.read(.{ .item = 12 }));
    try std.testing.expectError(error.UnregisteredItem, authority.read(.{ .item = 12, .block = 999 }));
    try std.testing.expectError(error.UnregisteredItem, authority.read(.{ .item = 11, .block = 2 }));
    try std.testing.expectEqual(state.Read.not_published, try authority.read(.{ .item = 12, .block = 2 }));
}

test "reference unbind publishes owner stopped until rebinding the current value" {
    var authority = try state.State.init(std.testing.allocator, registry);
    defer authority.deinit();
    try std.testing.expectEqual(state.Read.not_published, try authority.read(.{ .item = 11 }));
    const cell = try authority.claimCell(.{ .item = 11 });
    try std.testing.expectEqual(state.Read.not_published, try authority.read(.{ .item = 11 }));
    _ = try authority.publish(cell, .{ .value = true });
    const previous = try authority.read(.{ .item = 11 });
    _ = try authority.unbind(cell);
    try std.testing.expectEqual(state.Read.owner_stopped, try authority.read(.{ .item = 11 }));
    try std.testing.expect(previous.value);
    try std.testing.expect(!try authority.publish(cell, .{ .value = false }));
    try std.testing.expectEqual(state.Read.owner_stopped, try authority.read(.{ .item = 11 }));
    _ = try authority.bind(cell);
    try std.testing.expect(!(try authority.read(.{ .item = 11 })).value);
    _ = try authority.publish(cell, .read_failed);
    try std.testing.expectEqual(state.Read.read_failed, try authority.read(.{ .item = 11 }));
}

test "current Boolean and absent reads allocate nothing after creation" {
    var failing = std.testing.FailingAllocator.init(std.testing.allocator, .{});
    var authority = try state.State.init(failing.allocator(), registry);
    defer authority.deinit();
    const cell = try authority.claimCell(.{ .item = 11 });
    _ = try authority.publish(cell, .{ .value = true });
    failing.fail_index = failing.alloc_index;
    const before = failing.allocations;
    var total: usize = 0;
    for (0..104096) |_| {
        if ((try authority.read(.{ .item = 11 })).valueOr(false)) total += 1;
        if (state.eql(try authority.read(.{ .item = 10 }), .not_published)) total += 1;
    }
    try std.testing.expectEqual(@as(usize, 208192), total);
    try std.testing.expectEqual(before, failing.allocations);
    try std.testing.expect(!failing.has_induced_failure);
}

test "per-block publications and claims are independent" {
    var authority = try state.State.init(std.testing.allocator, registry);
    defer authority.deinit();
    const first = try authority.claimCell(.{ .item = 12, .block = 2 });
    const second = try authority.claimCell(.{ .item = 12, .block = 3 });
    _ = try authority.publish(first, .{ .value = true });
    _ = try authority.publish(second, .{ .value = false });
    try std.testing.expect((try authority.read(.{ .item = 12, .block = 2 })).value);
    try std.testing.expect(!(try authority.read(.{ .item = 12, .block = 3 })).value);
    _ = try authority.unbind(first);
    try std.testing.expectEqual(state.Read.owner_stopped, try authority.read(.{ .item = 12, .block = 2 }));
    try std.testing.expect(!(try authority.read(.{ .item = 12, .block = 3 })).value);
}

const ConcurrentRead = struct {
    authority: *state.State,
    writer: state.Claim,
    done: std.atomic.Value(bool) = .init(false),
    fn write(context: *ConcurrentRead) void {
        for (0..100002) |index| {
            _ = context.authority.publish(context.writer, switch (index % 3) {
                0 => .{ .value = false },
                1 => .{ .value = true },
                else => .read_failed,
            }) catch unreachable;
        }
        context.done.store(true, .release);
    }
};

test "state current publication is atomic during concurrent writes and reads" {
    var authority = try state.State.init(std.testing.allocator, registry);
    defer authority.deinit();
    var context = ConcurrentRead{ .authority = &authority, .writer = try authority.claimWrite(.{ .item = 10 }) };
    const thread = try std.Thread.spawn(.{}, ConcurrentRead.write, .{&context});
    defer thread.join();
    while (!context.done.load(.acquire)) {
        const read = try authority.read(.{ .item = 10 });
        switch (read) {
            .not_published, .read_failed, .value => {},
            .owner_stopped => return error.UnexpectedOwnerStopped,
        }
    }
    try std.testing.expectEqual(state.Read.read_failed, try authority.read(.{ .item = 10 }));
}

test "state rejects a capability claimed by a different authority" {
    var first = try state.State.init(std.testing.allocator, registry);
    defer first.deinit();
    var second = try state.State.init(std.testing.allocator, registry);
    defer second.deinit();
    const first_writer = try first.claimWrite(.{ .item = 10 });
    const second_writer = try second.claimWrite(.{ .item = 10 });
    _ = try first.publish(first_writer, .{ .value = true });
    _ = try second.publish(second_writer, .{ .value = false });
    try std.testing.expectError(error.InvalidClaim, second.publish(first_writer, .{ .value = true }));
    try std.testing.expect(!(try second.read(.{ .item = 10 })).value);
    try std.testing.expect((try first.read(.{ .item = 10 })).value);
    try std.testing.expectError(error.InvalidClaim, first.claimIndex(@ptrFromInt(@intFromPtr(first_writer) + 1)));
}

const host_items = [_]d.Item{
    items[0],
    .{ .uid = 13, .storage = .reference, .value_type = .host, .per_block = false, .trigger = .event, .owner_block = 2, .name = null },
    .{ .uid = 14, .storage = .own, .value_type = .host, .per_block = false, .trigger = .event, .owner_block = null, .name = null },
};
const host_registry: d.Registry = .{ .version = "host-fixture", .process = "fixture", .blocks = &blocks, .items = &host_items, .conditions = &.{} };

test "host reference presence binds and unbinds without copying a host object" {
    var authority = try state.State.init(std.testing.allocator, host_registry);
    defer authority.deinit();
    try std.testing.expectEqual(state.Presence.not_published, try authority.readPresence(.{ .item = 13 }));
    const cell = try authority.claimCell(.{ .item = 13 });
    _ = try authority.publishPresence(cell, .present);
    try std.testing.expectEqual(state.Presence.present, try authority.readPresence(.{ .item = 13 }));
    try std.testing.expectError(error.TypeMismatch, authority.read(.{ .item = 13 }));
    try std.testing.expectError(error.TypeMismatch, authority.publish(cell, .{ .value = false }));
    try std.testing.expectEqual(state.Presence.present, try authority.readPresence(.{ .item = 13 }));
    _ = try authority.unbind(cell);
    try std.testing.expectEqual(state.Presence.owner_stopped, try authority.readPresence(.{ .item = 13 }));
    try std.testing.expect(!try authority.publishPresence(cell, .read_failed));
    try std.testing.expectEqual(state.Presence.owner_stopped, try authority.readPresence(.{ .item = 13 }));
    _ = try authority.bind(cell);
    try std.testing.expectEqual(state.Presence.read_failed, try authority.readPresence(.{ .item = 13 }));
    _ = try authority.publishPresence(cell, .present);
    _ = try authority.unbind(cell);
    try std.testing.expectError(error.TypeMismatch, authority.publish(cell, .read_failed));
    _ = try authority.bind(cell);
    try std.testing.expectEqual(state.Presence.present, try authority.readPresence(.{ .item = 13 }));
}

test "host own writer publishes only presence and absence metadata" {
    var authority = try state.State.init(std.testing.allocator, host_registry);
    defer authority.deinit();
    const writer = try authority.claimWrite(.{ .item = 14 });
    _ = try authority.publishPresence(writer, .present);
    try std.testing.expectEqual(state.Presence.present, try authority.readPresence(.{ .item = 14 }));
    try std.testing.expectError(error.TypeMismatch, authority.publish(writer, .not_published));
    try std.testing.expectEqual(state.Presence.present, try authority.readPresence(.{ .item = 14 }));
    try std.testing.expectError(error.StorageMismatch, authority.unbind(writer));
    _ = try authority.publishPresence(writer, .read_failed);
    try std.testing.expectEqual(state.Presence.read_failed, try authority.readPresence(.{ .item = 14 }));
    _ = try authority.publishPresence(writer, .not_published);
    try std.testing.expectEqual(state.Presence.not_published, try authority.readPresence(.{ .item = 14 }));
}

test "Boolean entries reject presence APIs before changing their value" {
    var authority = try state.State.init(std.testing.allocator, host_registry);
    defer authority.deinit();
    const writer = try authority.claimWrite(.{ .item = 10 });
    _ = try authority.publish(writer, .{ .value = false });
    try std.testing.expectError(error.TypeMismatch, authority.readPresence(.{ .item = 10 }));
    try std.testing.expectError(error.TypeMismatch, authority.publishPresence(writer, .present));
    try std.testing.expect(!(try authority.read(.{ .item = 10 })).value);
    try std.testing.expectError(error.TypeMismatch, authority.publishPresence(writer, .owner_stopped));
    try std.testing.expect(!(try authority.read(.{ .item = 10 })).value);
}
