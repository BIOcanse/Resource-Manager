const std = @import("std");
const corpus = @import("golden_corpus.zig");
const protocol = @import("protocol.zig");
const session = @import("session.zig");
const state = @import("state.zig");

fn fill(owner: *state.Session, first: u32, end: u32) !void {
    var id = first;
    while (id < end) : (id += 1) {
        var input = corpus.processPromote(id + 1, owner.ledger_revision, 100 + id);
        try std.testing.expectEqual(protocol.Status.ok, session.promote(owner, &input));
    }
}

fn imageOf(owner: *state.Session, output: []u8) ![]const u8 {
    var written: u64 = 0;
    try std.testing.expectEqual(protocol.Status.ok, session.encode(owner, output, &written));
    return output[0..@intCast(written)];
}

test "ownership growth preserves exact canonical image and all indexes" {
    var config = corpus.createConfig(8192, 16384);
    const growing = try state.Session.createWithAllocator(&config, std.testing.allocator);
    defer growing.destroy();
    const preallocated = try state.Session.createWithContentCapacity(&config, std.testing.allocator, 8192);
    defer preallocated.destroy();
    try std.testing.expectEqual(@as(u32, 16), growing.workspaceCapacity().record_capacity);
    try fill(growing, 0, 300);
    try fill(preallocated, 0, 300);
    const diagnostic = growing.workspaceCapacity();
    try std.testing.expectEqual(@as(u32, 512), diagnostic.record_capacity);
    try std.testing.expectEqual(@as(u64, 5), diagnostic.growth.count);
    for (diagnostic.growth.events[0..5], 0..) |event, index| {
        try std.testing.expectEqual(@as(u64, index + 1), event.sequence);
        try std.testing.expectEqual(@as(u32, 3), event.reason);
        try std.testing.expectEqual(@as(u32, 0), event.unit);
        try std.testing.expectEqual(@as(u64, 16) << @intCast(index), event.old_capacity);
        try std.testing.expectEqual(@as(u64, 32) << @intCast(index), event.new_capacity);
    }
    try std.testing.expectEqual(@as(u32, 1024), growing.workspaceCapacity().primary_index_capacity);
    try std.testing.expectEqual(@as(u64, @sizeOf(state.Session) + 512 * 644 + 1024 * 16), growing.workspaceCapacity().resident_bytes);
    var id: u32 = 0;
    while (id < 300) : (id += 1) {
        const primary = corpus.processPrimary(id + 1);
        const index = growing.active.findPrimary(&primary) orelse return error.MissingRecord;
        const record = &growing.active.records[index];
        try std.testing.expectEqual(index, growing.active.findPayload(&record.original_binding, &record.payload).?);
    }
    var left: [128 + 300 * 320]u8 = undefined;
    var right: [128 + 300 * 320]u8 = undefined;
    const canonical = try imageOf(growing, &left);
    try std.testing.expectEqualSlices(u8, canonical, try imageOf(preallocated, &right));

    var open_config = corpus.openConfig(8192, 16384);
    var opened: ?*state.Session = null;
    try std.testing.expectEqual(protocol.Status.ok, session.openExisting(&open_config, canonical, &opened));
    defer opened.?.destroy();
    try std.testing.expectEqual(@as(u32, 300), opened.?.workspaceCapacity().record_capacity);
    try std.testing.expectEqualSlices(u8, canonical, try imageOf(opened.?, &right));
    try fill(opened.?, 300, 301);
    try std.testing.expectEqual(@as(u32, 600), opened.?.workspaceCapacity().record_capacity);
    const primary = corpus.processPrimary(23);
    var record = std.mem.zeroes(protocol.Record);
    try std.testing.expectEqual(protocol.Status.ok, session.get(opened.?, &primary, &record));
    var transition = corpus.processTransition(&record, opened.?.ledger_revision, .level_2, 1000);
    try std.testing.expectEqual(protocol.Status.ok, session.transition(opened.?, &transition));
    record = std.mem.zeroes(protocol.Record);
    try std.testing.expectEqual(protocol.Status.ok, session.get(opened.?, &primary, &record));
    var remove = corpus.remove(&record, opened.?.ledger_revision, 1100);
    try std.testing.expectEqual(protocol.Status.ok, session.remove(opened.?, &remove));
    try std.testing.expectEqual(@as(u32, 300), opened.?.active.count);
}

test "ownership failed growth allocation leaves every old allocation and byte intact" {
    var fail_offset: usize = 0;
    while (fail_offset < 7) : (fail_offset += 1) {
        var failing = std.testing.FailingAllocator.init(std.testing.allocator, .{});
        var config = corpus.createConfig(64, 128);
        const owner = try state.Session.createWithAllocator(&config, failing.allocator());
        defer owner.destroy();
        try fill(owner, 0, 16);
        // Preserve populated staging and sort data as well as active authority.
        try std.testing.expect(owner.staging.append(owner.active.records[0]));
        _ = owner.prepareSortIndices();
        const active_records = owner.active.records.ptr;
        const staging_records = owner.staging.records.ptr;
        const sort_indices = owner.sort_indices.ptr;
        const old_bytes = failing.allocated_bytes - failing.freed_bytes;
        var before: [128 + 16 * 320]u8 = undefined;
        var after: [128 + 16 * 320]u8 = undefined;
        const canonical = try imageOf(owner, &before);
        failing.fail_index = failing.alloc_index + fail_offset;
        var input = corpus.processPromote(17, owner.ledger_revision, 200);
        try std.testing.expectEqual(protocol.Status.out_of_memory, session.promote(owner, &input));
        try std.testing.expectEqual(@as(u64, 0), owner.workspaceCapacity().growth.count);
        try std.testing.expectEqual(active_records, owner.active.records.ptr);
        try std.testing.expectEqual(staging_records, owner.staging.records.ptr);
        try std.testing.expectEqual(sort_indices, owner.sort_indices.ptr);
        try std.testing.expectEqual(@as(u32, 1), owner.staging.count);
        try std.testing.expectEqual(old_bytes, failing.allocated_bytes - failing.freed_bytes);
        try std.testing.expectEqualSlices(u8, canonical, try imageOf(owner, &after));
        failing.fail_index = std.math.maxInt(usize);
        try std.testing.expectEqual(protocol.Status.ok, session.promote(owner, &input));
        try std.testing.expectEqual(@as(u32, 1), owner.staging.count);
        try std.testing.expectEqual(@as(u32, 0), owner.staging.findPrimary(&owner.active.records[0].primary).?);
    }
}

test "ownership growth honors configured record and resident maxima" {
    var config = corpus.createConfig(33, 128);
    const owner = try state.Session.createWithAllocator(&config, std.testing.allocator);
    defer owner.destroy();
    try fill(owner, 0, 33);
    try std.testing.expectEqual(@as(u32, 33), owner.workspaceCapacity().record_capacity);
    var rejected = corpus.processPromote(34, owner.ledger_revision, 300);
    const revision = owner.ledger_revision;
    try std.testing.expectEqual(protocol.Status.capacity_full, session.promote(owner, &rejected));
    try std.testing.expectEqual(revision, owner.ledger_revision);
    var bounded_config = corpus.createConfig(8192, 16384);
    bounded_config.maximum_resident_bytes = @sizeOf(state.Session) + 16 * 644 + 32 * 16;
    const bounded = try state.Session.createWithAllocator(&bounded_config, std.testing.allocator);
    defer bounded.destroy();
    try fill(bounded, 0, 16);
    rejected = corpus.processPromote(17, bounded.ledger_revision, 300);
    try std.testing.expectEqual(protocol.Status.capacity_full, session.promote(bounded, &rejected));
    try std.testing.expectEqual(@as(u32, 16), bounded.active.count);
    try std.testing.expectEqual(@as(u64, 17), bounded.ledger_revision);
}

test "ownership decode growth allocation failure preserves old live image" {
    var config = corpus.createConfig(64, 128);
    const source = try state.Session.createWithAllocator(&config, std.testing.allocator);
    defer source.destroy();
    try fill(source, 0, 33);
    var incoming: [128 + 33 * 320]u8 = undefined;
    const incoming_image = try imageOf(source, &incoming);
    var failing = std.testing.FailingAllocator.init(std.testing.allocator, .{});
    const target = try state.Session.createWithAllocator(&config, failing.allocator());
    defer target.destroy();
    try fill(target, 0, 1);
    var before: [448]u8 = undefined;
    var after: [448]u8 = undefined;
    const canonical = try imageOf(target, &before);
    failing.fail_index = failing.alloc_index + 4;
    try std.testing.expectEqual(protocol.Status.out_of_memory, session.decodeReplace(target, incoming_image));
    try std.testing.expectEqual(@as(u64, 0), target.workspaceCapacity().growth.count);
    try std.testing.expectEqualSlices(u8, canonical, try imageOf(target, &after));
    failing.fail_index = std.math.maxInt(usize);
    try std.testing.expectEqual(protocol.Status.ok, session.decodeReplace(target, incoming_image));
    try std.testing.expectEqual(@as(u32, 33), target.active.count);
    const event = target.workspaceCapacity().growth.events[0];
    try std.testing.expectEqual(@as(u64, 1), event.sequence);
    try std.testing.expectEqual(@as(u32, 4), event.reason);
    try std.testing.expectEqual(@as(u64, 16), event.old_capacity);
    try std.testing.expectEqual(@as(u64, 64), event.new_capacity);
}
