const std = @import("std");
const uid = @import("../uid.zig");
const index = @import("index.zig");

const Uid128 = uid.Identifier(128);
const Uid256 = uid.Identifier(256);
const Candidate128 = index.Candidate(Uid128, .c2_chain);
const Candidate256 = index.Candidate(Uid256, .c2_chain);

fn uid128(first: u8) Uid128 {
    var value: Uid128 = .{ .bytes = @splat(0) };
    value.bytes[0] = first;
    return value;
}

fn resource(table: u32, slot: u32) index.Location {
    return .{ .kind = .resource, .table = table, .slot = slot };
}

fn partition(table: u32, slot: u32) index.Location {
    return .{ .kind = .partition, .table = table, .slot = slot };
}

fn uid128AtHome(map: *const Candidate128, home_id: u32, start: usize, avoid: []const Uid128) !Uid128 {
    for (0..256) |offset| {
        const candidate = uid128(@intCast((start + offset) % 256));
        if (try map.homeFor(candidate) != home_id) continue;
        var already_used = false;
        for (avoid) |prior| {
            if (prior.eql(candidate)) {
                already_used = true;
                break;
            }
        }
        if (!already_used) return candidate;
    }
    return error.NoTestUid;
}

test "C2 carries the global maximum shape without a two-times bucket rule" {
    try std.testing.expectEqual(@as(usize, 1_048_576), index.max_indexed_nodes);
    try std.testing.expectEqual(@as(usize, 2_097_152), index.max_homes);

    var nodes: [3]Candidate128.Node = undefined;
    var homes: [1]Candidate128.Home = undefined;
    var map = Candidate128.init(&nodes, &homes);

    const first = uid128(1);
    const second = uid128(2);
    const third = uid128(3);
    try map.insert(0, first, resource(0, 10));
    try map.insert(1, second, partition(0, 4));
    try map.insert(2, third, resource(1, 20));
    try std.testing.expectEqual(@as(usize, 3), map.nodes.len);
    try std.testing.expectEqual(@as(usize, 1), map.homes.len);
    try std.testing.expectEqual(resource(1, 20), (try map.find(third)).?);
}

test "zero UID, full ordering, duplicate lookup, deletion and node reuse" {
    var nodes: [4]Candidate128.Node = undefined;
    var homes: [1]Candidate128.Home = undefined;
    var map = Candidate128.init(&nodes, &homes);

    const zero: Uid128 = .{ .bytes = @splat(0) };
    const low = uid128(5);
    const high = uid128(200);
    try map.insert(2, high, partition(3, 8));
    try map.insert(0, zero, resource(1, 0));
    try map.insert(1, low, resource(2, 11));

    try std.testing.expectEqual(resource(1, 0), (try map.find(zero)).?);
    try std.testing.expectEqual(resource(2, 11), (try map.find(low)).?);
    try std.testing.expectEqual(partition(3, 8), (try map.find(high)).?);
    try std.testing.expectEqual(Candidate128.Entry{ .uid = zero, .location = resource(1, 0) }, (try map.entryAt(0)).?);
    try std.testing.expectError(error.DuplicateUid, map.insert(3, low, resource(9, 9)));
    try std.testing.expectEqual(@as(?u32, null), try map.homeForNode(3));
    try std.testing.expectEqual(@as(?Candidate128.Entry, null), try map.entryAt(3));

    try map.remove(1);
    try std.testing.expectEqual(@as(?index.Location, null), try map.find(low));
    try std.testing.expectEqual(resource(1, 0), (try map.find(zero)).?);
    try std.testing.expectEqual(partition(3, 8), (try map.find(high)).?);

    const replacement = uid128(77);
    try map.insert(1, replacement, partition(5, 12));
    try std.testing.expectEqual(partition(5, 12), (try map.find(replacement)).?);
    try std.testing.expectEqual(@as(?index.Location, null), try map.find(low));

    const audit = try map.audit();
    try std.testing.expectEqual(@as(usize, 1), audit.home_body);
    try std.testing.expectEqual(@as(usize, 4), audit.node_body);
    try std.testing.expectEqual(@as(usize, 2), audit.neighbor_payload_loads);
    try std.testing.expectEqual(map.homes.len + map.nodes.len, audit.home_body + audit.node_body);
}

test "256-bit lookup compares the complete UID, including the last byte" {
    var nodes: [2]Candidate256.Node = undefined;
    var homes: [1]Candidate256.Home = undefined;
    var map = Candidate256.init(&nodes, &homes);

    var first: Uid256 = .{ .bytes = @splat(0) };
    var second = first;
    first.bytes[31] = 1;
    second.bytes[31] = 2;
    try map.insert(0, first, resource(4, 15));
    try map.insert(1, second, partition(4, 6));

    try std.testing.expectEqual(resource(4, 15), (try map.find(first)).?);
    try std.testing.expectEqual(partition(4, 6), (try map.find(second)).?);

    const audit = try map.audit();
    try std.testing.expectEqual(@as(usize, 1), audit.home_body);
    try std.testing.expectEqual(@as(usize, 2), audit.node_body);
    try std.testing.expectEqual(@as(usize, 1), audit.neighbor_payload_loads);
    try std.testing.expectEqual(Candidate256.Entry{ .uid = first, .location = resource(4, 15) }, (try map.entryAt(0)).?);
}

test "128-bit audit orders the complete UID through its last byte" {
    var nodes: [2]Candidate128.Node = undefined;
    var homes: [1]Candidate128.Home = undefined;
    var map = Candidate128.init(&nodes, &homes);

    var first: Uid128 = .{ .bytes = @splat(0) };
    var second = first;
    first.bytes[15] = 1;
    second.bytes[15] = 2;
    try map.insert(0, second, resource(0, 1));
    try map.insert(1, first, resource(0, 0));

    try std.testing.expectEqual(resource(0, 0), (try map.find(first)).?);
    try std.testing.expectEqual(resource(0, 1), (try map.find(second)).?);
    const audit = try map.audit();
    try std.testing.expectEqual(@as(usize, 1), audit.home_body);
    try std.testing.expectEqual(@as(usize, 2), audit.node_body);
    try std.testing.expectEqual(@as(usize, 1), audit.neighbor_payload_loads);
}

test "C2 audit rejects a bad home and odd publication version" {
    var nodes: [1]Candidate128.Node = undefined;
    var homes: [2]Candidate128.Home = undefined;
    var map = Candidate128.init(&nodes, &homes);
    const key = uid128(7);
    try map.insert(0, key, resource(0, 0));
    const home_id = try map.homeFor(key);

    homes[home_id].head.store(index.no_node, .seq_cst);
    nodes[0].home_index.store(@intCast(homes.len), .seq_cst);
    try std.testing.expectError(error.InvalidIndexState, map.audit());

    homes[home_id].head.store(0, .seq_cst);
    nodes[0].home_index.store(home_id, .seq_cst);
    homes[home_id].version.store(1, .seq_cst);
    try std.testing.expectError(error.InvalidIndexState, map.audit());
}

test "C2 audit rejects invalid location tag and primitive bounds" {
    var nodes: [1]Candidate128.Node = undefined;
    var homes: [1]Candidate128.Home = undefined;
    var map = Candidate128.init(&nodes, &homes);
    try map.insert(0, uid128(1), resource(0, 0));

    nodes[0].location_kind.store(2, .seq_cst);
    try std.testing.expectError(error.InvalidIndexState, map.audit());
    nodes[0].location_kind.store(@intFromEnum(index.LocationKind.resource), .seq_cst);

    nodes[0].location_table.store(64, .seq_cst);
    try std.testing.expectError(error.InvalidIndexState, map.audit());
    nodes[0].location_table.store(0, .seq_cst);

    nodes[0].location_slot.store(@intCast(index.max_indexed_nodes), .seq_cst);
    try std.testing.expectError(error.InvalidIndexState, map.audit());
}

test "C2 audit rejects a closed ring without walking a home chain" {
    var nodes: [2]Candidate128.Node = undefined;
    var homes: [1]Candidate128.Home = undefined;
    var map = Candidate128.init(&nodes, &homes);
    try map.insert(0, uid128(1), resource(0, 0));
    try map.insert(1, uid128(2), resource(0, 1));

    homes[0].head.store(index.no_node, .seq_cst);
    nodes[0].previous.store(1, .seq_cst);
    nodes[0].next.store(1, .seq_cst);
    nodes[1].previous.store(0, .seq_cst);
    nodes[1].next.store(0, .seq_cst);

    try std.testing.expectError(error.InvalidIndexState, map.audit());
}

test "C2 audit rejects a broken reciprocal link and an orphaned component" {
    var broken_nodes: [2]Candidate128.Node = undefined;
    var broken_homes: [1]Candidate128.Home = undefined;
    var broken = Candidate128.init(&broken_nodes, &broken_homes);
    try broken.insert(0, uid128(1), resource(0, 0));
    try broken.insert(1, uid128(2), resource(0, 1));
    broken_nodes[0].next.store(index.no_node, .seq_cst);
    try std.testing.expectError(error.InvalidIndexState, broken.audit());

    var orphan_nodes: [2]Candidate128.Node = undefined;
    var orphan_homes: [1]Candidate128.Home = undefined;
    var orphan = Candidate128.init(&orphan_nodes, &orphan_homes);
    try orphan.insert(0, uid128(1), resource(0, 0));
    try orphan.insert(1, uid128(2), resource(0, 1));
    orphan_nodes[0].next.store(index.no_node, .seq_cst);
    orphan_nodes[1].previous.store(index.no_node, .seq_cst);
    try std.testing.expectError(error.InvalidIndexState, orphan.audit());
}

test "closed-tree removal hooks prepare before one no-fail publication" {
    var nodes: [4]Candidate128.Node = undefined;
    var homes: [1]Candidate128.Home = undefined;
    var map = Candidate128.init(&nodes, &homes);
    const keys = [_]Uid128{ uid128(10), uid128(20), uid128(30), uid128(40) };
    for (keys, 0..) |key, node_id| {
        try map.insert(@intCast(node_id), key, resource(0, @intCast(node_id)));
    }

    const home_id = try map.homeFor(keys[0]);
    try map.lockHome(home_id);
    const publication = try map.preflightPublishLocked(home_id);
    const first = try map.prepareRemoveLocked(1, home_id);
    const second = try map.prepareRemoveLocked(2, home_id);

    map.beginPublishLocked(publication);
    map.commitRemoveLocked(first);
    map.commitRemoveLocked(second);
    map.finishPublishLocked(publication);
    map.unlockHome(home_id);

    try std.testing.expectEqual(resource(0, 0), (try map.find(keys[0])).?);
    try std.testing.expectEqual(@as(?index.Location, null), try map.find(keys[1]));
    try std.testing.expectEqual(@as(?index.Location, null), try map.find(keys[2]));
    try std.testing.expectEqual(resource(0, 3), (try map.find(keys[3])).?);
}

test "transaction removes across homes and reuses nodes before sorted insert commit" {
    var nodes: [5]Candidate128.Node = undefined;
    var homes: [2]Candidate128.Home = undefined;
    var map = Candidate128.init(&nodes, &homes);

    var used: [6]Uid128 = undefined;
    var used_count: usize = 0;
    const removed_a = try uid128AtHome(&map, 0, 1, used[0..used_count]);
    used[used_count] = removed_a;
    used_count += 1;
    const removed_b = try uid128AtHome(&map, 1, 2, used[0..used_count]);
    used[used_count] = removed_b;
    used_count += 1;
    const kept = try uid128AtHome(&map, 0, 20, used[0..used_count]);
    used[used_count] = kept;
    used_count += 1;
    const inserted_a = try uid128AtHome(&map, 1, 30, used[0..used_count]);
    used[used_count] = inserted_a;
    used_count += 1;
    const inserted_b = try uid128AtHome(&map, 0, 40, used[0..used_count]);
    used[used_count] = inserted_b;
    used_count += 1;
    const inserted_c = try uid128AtHome(&map, 1, 50, used[0..used_count]);

    try map.insert(0, removed_a, resource(0, 10));
    try map.insert(1, removed_b, partition(1, 20));
    try map.insert(2, kept, resource(0, 30));

    const locked_homes = [_]u32{ 0, 1 };
    for (locked_homes) |home_id| try map.lockHome(home_id);
    var home_locks = locked_homes.len;
    defer while (home_locks > 0) {
        home_locks -= 1;
        map.unlockHome(locked_homes[home_locks]);
    };

    var publications: [2]Candidate128.Publication = undefined;
    for (locked_homes, 0..) |home_id, i| {
        publications[i] = try map.preflightPublishLocked(home_id);
        try map.preflightHomeLocked(home_id);
    }

    const locked_nodes = [_]u32{ 0, 1, 3 };
    for (locked_nodes) |node_id| try map.lockNode(node_id);
    var node_locks = locked_nodes.len;
    defer while (node_locks > 0) {
        node_locks -= 1;
        map.unlockNode(locked_nodes[node_locks]);
    };

    const remove_a = try map.preflightRemoveNodeLocked(0, 0);
    const remove_b = try map.preflightRemoveNodeLocked(1, 1);
    const insert_a = try map.preflightInsertNodeLocked(0, inserted_a, partition(3, 40), 1, 0);
    const insert_b = try map.preflightInsertNodeLocked(1, inserted_b, resource(4, 50), 0, 1);
    const insert_c = try map.preflightInsertNodeLocked(3, inserted_c, resource(5, 60), 1, null);

    for (publications) |publication| map.beginPublishLocked(publication);
    map.commitRemoveRetainingNodeLocked(remove_a);
    map.commitRemoveRetainingNodeLocked(remove_b);
    map.commitInsertRetainingNodeLocked(insert_a);
    map.commitInsertRetainingNodeLocked(insert_b);
    map.commitInsertRetainingNodeLocked(insert_c);
    for (publications) |publication| map.finishPublishLocked(publication);

    node_locks = 0;
    for ([_]u32{ 3, 1, 0 }) |node_id| map.unlockNode(node_id);
    home_locks = 0;
    for ([_]u32{ 1, 0 }) |home_id| map.unlockHome(home_id);

    try std.testing.expectEqual(@as(?index.Location, null), try map.find(removed_a));
    try std.testing.expectEqual(@as(?index.Location, null), try map.find(removed_b));
    try std.testing.expectEqual(resource(0, 30), (try map.find(kept)).?);
    try std.testing.expectEqual(partition(3, 40), (try map.find(inserted_a)).?);
    try std.testing.expectEqual(resource(4, 50), (try map.find(inserted_b)).?);
    try std.testing.expectEqual(resource(5, 60), (try map.find(inserted_c)).?);
}

test "multi-home version exhaustion leaves the whole replacement untouched" {
    var nodes: [2]Candidate128.Node = undefined;
    var homes: [2]Candidate128.Home = undefined;
    var map = Candidate128.init(&nodes, &homes);
    const old_uid = try uid128AtHome(&map, 0, 3, &.{});
    const new_uid = try uid128AtHome(&map, 1, 9, &.{old_uid});
    try map.insert(0, old_uid, resource(2, 15));
    homes[1].version.store(std.math.maxInt(u64) - 1, .seq_cst);

    try map.lockHome(0);
    try map.lockHome(1);
    defer map.unlockHome(1);
    defer map.unlockHome(0);

    const first_publication = try map.preflightPublishLocked(0);
    try map.preflightHomeLocked(0);
    const first_version = homes[0].version.load(.seq_cst);
    const exhausted_version = homes[1].version.load(.seq_cst);
    try std.testing.expectError(error.VersionExhausted, map.preflightPublishLocked(1));

    try std.testing.expectEqual(first_version, homes[0].version.load(.seq_cst));
    try std.testing.expectEqual(exhausted_version, homes[1].version.load(.seq_cst));
    try std.testing.expectEqual(resource(2, 15), (try map.find(old_uid)).?);
    try std.testing.expectEqual(@as(?index.Location, null), try map.find(new_uid));
    try std.testing.expect(nodes[0].linked.load(.seq_cst));
    try std.testing.expect(!nodes[1].linked.load(.seq_cst));
    _ = first_publication;
}

test "C2 removal-only transaction rejects a corrupted adjacent node before publication" {
    var nodes: [3]Candidate128.Node = undefined;
    var homes: [1]Candidate128.Home = undefined;
    var map = Candidate128.init(&nodes, &homes);
    const keys = [_]Uid128{ uid128(10), uid128(20), uid128(30) };
    for (keys, 0..) |key, node_id| try map.insert(@intCast(node_id), key, resource(0, @intCast(node_id)));

    const home_id = try map.homeFor(keys[1]);
    nodes[2].previous.store(index.no_node, .seq_cst);
    const head_before = homes[home_id].head.load(.seq_cst);
    const version_before = homes[home_id].version.load(.seq_cst);
    const previous_before = nodes[1].previous.load(.seq_cst);
    const next_before = nodes[1].next.load(.seq_cst);

    var home_work: [1]u32 = undefined;
    var node_work: [1]u32 = undefined;
    var publication_work: [1]Candidate128.Publication = undefined;
    var removal_work: [1]Candidate128.RemovePlan = undefined;
    var workspace: Candidate128.Transactions.Workspace = .{
        .homes = &home_work,
        .nodes = &node_work,
        .publications = &publication_work,
        .removals = &removal_work,
    };

    try std.testing.expectError(
        error.InvalidIndexState,
        Candidate128.Transactions.prepare(&map, &.{1}, &.{}, &workspace),
    );

    try std.testing.expectEqual(head_before, homes[home_id].head.load(.seq_cst));
    try std.testing.expectEqual(version_before, homes[home_id].version.load(.seq_cst));
    try std.testing.expectEqual(previous_before, nodes[1].previous.load(.seq_cst));
    try std.testing.expectEqual(next_before, nodes[1].next.load(.seq_cst));
    for (&nodes) |*node| try std.testing.expect(node.linked.load(.seq_cst));
    try std.testing.expect(nodes[2].previous.load(.seq_cst) == index.no_node);
    try std.testing.expect(homes[home_id].writer.tryLock());
    homes[home_id].writer.unlock();
    try std.testing.expect(nodes[1].writer.tryLock());
    nodes[1].writer.unlock();
}

test "owner transaction removes then reuses a node across homes" {
    var nodes: [4]Candidate128.Node = undefined;
    var homes: [3]Candidate128.Home = undefined;
    var map = Candidate128.init(&nodes, &homes);
    const removed = try uid128AtHome(&map, 0, 4, &.{});
    const kept = try uid128AtHome(&map, 1, 8, &.{removed});
    const inserted_partition = try uid128AtHome(&map, 2, 12, &.{ removed, kept });
    const inserted_resource = try uid128AtHome(&map, 0, 22, &.{ removed, kept, inserted_partition });
    try map.insert(0, removed, resource(0, 10));
    try map.insert(2, kept, resource(1, 20));

    var homes_work: [3]u32 = undefined;
    var nodes_work: [3]u32 = undefined;
    var publications: [3]Candidate128.Publication = undefined;
    var removals: [1]Candidate128.RemovePlan = undefined;
    var inserts: [2]Candidate128.InsertPlan = undefined;
    var workspace = Candidate128.Transactions.Workspace{
        .homes = &homes_work,
        .nodes = &nodes_work,
        .publications = &publications,
        .removals = &removals,
        .inserts = &inserts,
    };
    const removal_ids = [_]u32{0};
    const puts = [_]Candidate128.Transactions.Put{
        .{ .node_id = 0, .uid = inserted_partition, .location = partition(2, 4) },
        .{ .node_id = 1, .uid = inserted_resource, .location = resource(1, 22) },
    };

    var prepared = try Candidate128.Transactions.prepare(&map, &removal_ids, &puts, &workspace);
    defer prepared.abort();
    prepared.commit();
    prepared.abort();

    try std.testing.expectEqual(@as(?index.Location, null), try map.find(removed));
    try std.testing.expectEqual(resource(1, 20), (try map.find(kept)).?);
    try std.testing.expectEqual(partition(2, 4), (try map.find(inserted_partition)).?);
    try std.testing.expectEqual(resource(1, 22), (try map.find(inserted_resource)).?);
    try std.testing.expectEqual(@as(?u32, null), try map.homeForNode(3));
}

test "owner transaction partition retry and exhausted version release all locks without mutation" {
    var nodes: [3]Candidate128.Node = undefined;
    var homes: [2]Candidate128.Home = undefined;
    var map = Candidate128.init(&nodes, &homes);
    const existing = try uid128AtHome(&map, 0, 7, &.{});
    const replacement = try uid128AtHome(&map, 1, 17, &.{existing});
    try map.insert(0, existing, partition(0, 5));

    var home_work: [2]u32 = undefined;
    var node_work: [2]u32 = undefined;
    var publication_work: [2]Candidate128.Publication = undefined;
    var removal_work: [1]Candidate128.RemovePlan = undefined;
    var insert_work: [1]Candidate128.InsertPlan = undefined;
    var workspace = Candidate128.Transactions.Workspace{
        .homes = &home_work,
        .nodes = &node_work,
        .publications = &publication_work,
        .removals = &removal_work,
        .inserts = &insert_work,
    };
    const no_removals: [0]u32 = .{};
    const duplicate_put = [_]Candidate128.Transactions.Put{
        .{ .node_id = 1, .uid = existing, .location = partition(0, 6) },
    };
    try std.testing.expectError(
        error.RetryExisting,
        Candidate128.Transactions.prepare(&map, &no_removals, &duplicate_put, &workspace),
    );
    try std.testing.expectEqual(partition(0, 5), (try map.find(existing)).?);
    try std.testing.expectEqual(@as(?u32, null), try map.homeForNode(1));

    homes[1].version.store(std.math.maxInt(u64) - 1, .seq_cst);
    const new_put = [_]Candidate128.Transactions.Put{
        .{ .node_id = 1, .uid = replacement, .location = resource(1, 3) },
    };
    try std.testing.expectError(
        error.VersionExhausted,
        Candidate128.Transactions.prepare(&map, &no_removals, &new_put, &workspace),
    );
    try std.testing.expectEqual(partition(0, 5), (try map.find(existing)).?);
    try std.testing.expectEqual(@as(?index.Location, null), try map.find(replacement));
    try std.testing.expectEqual(@as(?u32, null), try map.homeForNode(1));
    try std.testing.expect(nodes[1].writer.tryLock());
    nodes[1].writer.unlock();
    try std.testing.expect(homes[1].writer.tryLock());
    homes[1].writer.unlock();
}

test "version exhaustion refuses before touching the node or head" {
    var nodes: [1]Candidate128.Node = undefined;
    var homes: [1]Candidate128.Home = undefined;
    var map = Candidate128.init(&nodes, &homes);
    homes[0].version.store(std.math.maxInt(u64) - 1, .seq_cst);

    try std.testing.expectError(error.VersionExhausted, map.insert(0, uid128(1), resource(0, 0)));
    try std.testing.expectEqual(index.no_node, homes[0].head.load(.seq_cst));
    try std.testing.expect(!nodes[0].linked.load(.seq_cst));
}

const ConcurrentState = struct {
    map: *Candidate128,
    stable_uid: Uid128,
    changing_uid: Uid128,
    start: std.atomic.Value(bool) = std.atomic.Value(bool).init(false),
    failed: std.atomic.Value(bool) = std.atomic.Value(bool).init(false),
};

fn concurrentReader(state: *ConcurrentState) void {
    while (!state.start.load(.acquire)) std.atomic.spinLoopHint();
    for (0..8_000) |_| {
        const stable = state.map.find(state.stable_uid) catch {
            state.failed.store(true, .release);
            return;
        };
        if (stable == null or !std.meta.eql(stable.?, resource(7, 77))) {
            state.failed.store(true, .release);
            return;
        }

        const changing = state.map.find(state.changing_uid) catch {
            state.failed.store(true, .release);
            return;
        };
        if (changing) |location| {
            if (!std.meta.eql(location, resource(1, 10)) and
                !std.meta.eql(location, partition(2, 20)))
            {
                state.failed.store(true, .release);
                return;
            }
        }
    }
}

fn concurrentWriter(state: *ConcurrentState) void {
    state.start.store(true, .release);
    for (0..1_000) |iteration| {
        state.map.remove(1) catch {
            state.failed.store(true, .release);
            return;
        };
        const location = if ((iteration & 1) == 0) resource(1, 10) else partition(2, 20);
        state.map.insert(1, state.changing_uid, location) catch {
            state.failed.store(true, .release);
            return;
        };
    }
}

test "concurrent find sees stable entries and complete reused locations" {
    var nodes: [2]Candidate128.Node = undefined;
    var homes: [1]Candidate128.Home = undefined;
    var map = Candidate128.init(&nodes, &homes);
    const changing_uid = uid128(10);
    const stable_uid = uid128(250);
    try map.insert(0, stable_uid, resource(7, 77));
    try map.insert(1, changing_uid, resource(1, 10));

    var state = ConcurrentState{
        .map = &map,
        .stable_uid = stable_uid,
        .changing_uid = changing_uid,
    };
    var reader = try std.Thread.spawn(.{}, concurrentReader, .{&state});
    const writer = std.Thread.spawn(.{}, concurrentWriter, .{&state}) catch |err| {
        state.start.store(true, .release);
        reader.join();
        return err;
    };
    writer.join();
    reader.join();
    try std.testing.expect(!state.failed.load(.acquire));
}
