//! Region owner tests against the first consumer's declarations: the hardware
//! metric owner's Raw/Result tables and supplemental locations, with slot
//! kinds, widths, writers, identity and string tables as declared in
//! ReadWriteCore/Windows/Metrics/Zig/MetricOwnerPositionDeclarations.cs and
//! MetricOwnerSupplementalDeclarations.cs (hardware-metric-zig worktree).
const std = @import("std");
const ss = @import("rm_shared_section");
const plan = @import("plan.zig");
const owner_module = @import("owner.zig");
const Owner = owner_module.Owner;
const none = plan.none;

extern "kernel32" fn Sleep(milliseconds: u32) callconv(.winapi) void;

// Writer roles, the declaration-local source order.
const rows_source: u32 = 0;
const values_source: u32 = 1;
const health_source: u32 = 2;
const provenance_source: u32 = 3;

fn column(source: u32, width: u32, capacity: u32, identity: u32) plan.PositionDeclaration {
    return .{ .source = source, .kind = .column, .element_width = width, .capacity = capacity, .identity = identity };
}
fn stringColumn(source: u32, capacity: u32, identity: u32, strings: u32) plan.PositionDeclaration {
    return .{ .source = source, .kind = .string_column, .element_width = 8, .capacity = capacity, .identity = identity, .strings = strings };
}
fn stringTable(source: u32, bytes: u32) plan.PositionDeclaration {
    return .{ .source = source, .kind = .string_table, .element_width = 1, .capacity = bytes };
}
fn isAny(slot: u32, comptime set: []const u32) bool {
    inline for (set) |member| if (slot == member) return true;
    return false;
}

fn raw(rules: u32, payload_bytes: u32) [17]plan.PositionDeclaration {
    var out: [17]plan.PositionDeclaration = undefined;
    var slot: u32 = 0;
    while (slot < 16) : (slot += 1) {
        const payload = slot == 9;
        const width: u32 = if (payload) 1 else if (isAny(slot, &.{ 3, 4, 10, 11, 13 })) 4 else 8;
        const writer = if (slot == 0 or slot == 15) rows_source else if (slot >= 10 and slot <= 12) health_source else values_source;
        out[slot] = if (payload) stringTable(writer, payload_bytes) else column(writer, width, rules, if (slot == 0) none else 0);
    }
    out[16] = column(provenance_source, 8, 8 + rules * 12, none);
    return out;
}

fn result(metrics: u32) [13]plan.PositionDeclaration {
    var out: [13]plan.PositionDeclaration = undefined;
    var slot: u32 = 0;
    while (slot < 12) : (slot += 1) {
        const width: u32 = if (isAny(slot, &.{ 4, 5, 9, 10, 11 })) 4 else 8;
        out[slot] = column(if (slot == 0) rows_source else values_source, width, metrics, if (slot == 0) none else 0);
    }
    out[12] = column(provenance_source, 8, 8 + metrics * 4, none);
    return out;
}

fn catalog(rows: u32, text_bytes: u32) [7]plan.PositionDeclaration {
    var out: [7]plan.PositionDeclaration = undefined;
    var slot: u32 = 0;
    while (slot < 7) : (slot += 1) {
        const identity = if (slot == 0) none else 0;
        out[slot] = if (slot == 6) stringTable(rows_source, text_bytes) else if (slot == 4 or slot == 5)
            stringColumn(rows_source, rows, identity, 6)
        else
            column(rows_source, if (isAny(slot, &.{ 1, 2, 3 })) 4 else 8, rows, identity);
    }
    return out;
}

fn inventory(rows: u32, text_bytes: u32) [14]plan.PositionDeclaration {
    var out: [14]plan.PositionDeclaration = undefined;
    var slot: u32 = 0;
    while (slot < 14) : (slot += 1) {
        const identity = if (slot == 0) none else 0;
        out[slot] = if (slot == 13) stringTable(rows_source, text_bytes) else if (slot == 12)
            stringColumn(rows_source, rows, identity, 13)
        else
            column(rows_source, if (slot == 10) 4 else 8, rows, identity);
    }
    return out;
}

fn vector(rows: u32, text_bytes: u32) [21]plan.PositionDeclaration {
    var out: [21]plan.PositionDeclaration = undefined;
    var slot: u32 = 0;
    while (slot < 21) : (slot += 1) {
        const writer = if (slot <= 4 or (slot >= 13 and slot <= 18)) rows_source else if ((slot >= 10 and slot <= 12) or slot == 20) health_source else values_source;
        const width: u32 = if (isAny(slot, &.{ 2, 3, 4, 6, 7, 10, 11, 17, 20 })) 4 else 8;
        out[slot] = if (slot == 14) stringTable(writer, text_bytes) else column(writer, width, rows, if (slot == 0) none else 0);
    }
    return out;
}

fn history(comptime count: u32, rows: u32, samples: u32, address_bytes: u32) [count]plan.PositionDeclaration {
    var out: [count]plan.PositionDeclaration = undefined;
    var slot: u32 = 0;
    while (slot < count) : (slot += 1) {
        const writer = if (slot == 0) rows_source else values_source;
        const width: u32 = if (isAny(slot, &.{ 0, 1, 6, 7, 10 })) 8 else 4;
        out[slot] = if (slot == 11) stringTable(writer, address_bytes) else if (slot >= 6 and slot <= 8)
            column(writer, width, samples, none)
        else
            column(writer, width, rows, if (slot == 0) none else 0);
    }
    return out;
}

fn identities(comptime count: usize, comptime prefix: []const u8, kind: u32) [count]ss.SourceIdentity {
    const roles = [_][]const u8{ ".rows", ".values", ".health", ".provenance" };
    var out: [count]ss.SourceIdentity = undefined;
    for (&out, 0..) |*identity, i| {
        var name: [ss.owner_name_capacity]u8 = undefined;
        const text = std.fmt.bufPrint(&name, "{s}{s}", .{ prefix, roles[i] }) catch unreachable;
        identity.* = ss.SourceIdentity.init(text, kind, 0, 0) catch unreachable;
    }
    return out;
}

/// The consumer's declaration set: every location at least once, both regions.
const Fixture = struct {
    raw_normal_sources: [4]ss.SourceIdentity,
    raw_normal: [17]plan.PositionDeclaration,
    result_normal_sources: [4]ss.SourceIdentity,
    result_normal: [13]plan.PositionDeclaration,
    catalog_sources: [1]ss.SourceIdentity,
    catalog: [7]plan.PositionDeclaration,
    inventory_sources: [1]ss.SourceIdentity,
    inventory: [14]plan.PositionDeclaration,
    vector_sources: [3]ss.SourceIdentity,
    vector: [21]plan.PositionDeclaration,
    history_sources: [2]ss.SourceIdentity,
    history: [9]plan.PositionDeclaration,
    mixed_sources: [2]ss.SourceIdentity,
    mixed: [13]plan.PositionDeclaration,
    raw_admin_sources: [4]ss.SourceIdentity,
    raw_admin: [17]plan.PositionDeclaration,
    /// Derived from admin raw inputs: the highest privilege of its inputs.
    result_admin_sources: [4]ss.SourceIdentity,
    result_admin: [13]plan.PositionDeclaration,
    history_admin_sources: [2]ss.SourceIdentity,
    history_admin: [9]plan.PositionDeclaration,

    fn init(catalog_rows: u32) Fixture {
        return .{
            .raw_normal_sources = identities(4, "metric.raw.normal", 1),
            .raw_normal = raw(24, 512),
            .result_normal_sources = identities(4, "metric.result.normal", 2),
            .result_normal = result(32),
            .catalog_sources = identities(1, "metric.catalog", 3),
            .catalog = catalog(catalog_rows, 2048),
            .inventory_sources = identities(1, "metric.gpu_inventory", 4),
            .inventory = inventory(4, 1024),
            .vector_sources = identities(3, "metric.gpu_vector", 5),
            .vector = vector(64, 4096),
            .history_sources = identities(2, "metric.history", 6),
            .history = history(9, 16, 120, 0),
            .mixed_sources = identities(2, "metric.mixed_history", 7),
            .mixed = history(13, 8, 60, 512),
            .raw_admin_sources = identities(4, "metric.raw.admin", 1),
            .raw_admin = raw(8, 256),
            .result_admin_sources = identities(4, "metric.result.admin", 2),
            .result_admin = result(8),
            .history_admin_sources = identities(2, "metric.history.admin", 6),
            .history_admin = history(9, 4, 60, 0),
        };
    }

    fn declarations(self: *const Fixture) [10]plan.Declaration {
        return .{
            plan.Declaration.init(.normal, &self.raw_normal_sources, &self.raw_normal),
            plan.Declaration.init(.normal, &self.result_normal_sources, &self.result_normal),
            plan.Declaration.init(.normal, &self.catalog_sources, &self.catalog),
            plan.Declaration.init(.normal, &self.inventory_sources, &self.inventory),
            plan.Declaration.init(.normal, &self.vector_sources, &self.vector),
            plan.Declaration.init(.normal, &self.history_sources, &self.history),
            plan.Declaration.init(.normal, &self.mixed_sources, &self.mixed),
            plan.Declaration.init(.administrator, &self.raw_admin_sources, &self.raw_admin),
            plan.Declaration.init(.administrator, &self.result_admin_sources, &self.result_admin),
            plan.Declaration.init(.administrator, &self.history_admin_sources, &self.history_admin),
        };
    }
};

const Expected = struct { region: plan.RegionClass, sources: u32, positions: u32 };
const expected = [_]Expected{
    .{ .region = .normal, .sources = 4, .positions = 17 },
    .{ .region = .normal, .sources = 4, .positions = 13 },
    .{ .region = .normal, .sources = 1, .positions = 7 },
    .{ .region = .normal, .sources = 1, .positions = 14 },
    .{ .region = .normal, .sources = 3, .positions = 21 },
    .{ .region = .normal, .sources = 2, .positions = 9 },
    .{ .region = .normal, .sources = 2, .positions = 13 },
    .{ .region = .administrator, .sources = 4, .positions = 17 },
    .{ .region = .administrator, .sources = 4, .positions = 13 },
    .{ .region = .administrator, .sources = 2, .positions = 9 },
};

/// The data capacity computed independently: each position padded to 8.
fn expectedDataCapacity(declarations: []const plan.Declaration, region: plan.RegionClass) u32 {
    var total: u64 = 0;
    for (declarations) |*declaration| {
        if (declaration.region != region) continue;
        for (declaration.positionSlice().?) |position| total += std.mem.alignForward(u64, position.byteSize().?, 8);
    }
    return @intCast(total);
}

fn regionBytes(sources: u32, positions: u32, data: u32) u64 {
    return std.mem.alignForward(u64, ss.requiredSize(sources, positions, data), plan.region_alignment);
}

test "the hardware metric declarations lay out, bind and borrow in both regions" {
    const fixture = Fixture.init(40);
    const declarations = fixture.declarations();
    var owner = Owner.init(std.testing.allocator, .{ .borrow_drain_timeout_ms = 5_000 });
    var failure = plan.ApplyFailure{};
    try owner.apply(&declarations, 1_000, &failure);

    // Bindings: consecutive global indexes per region, in declaration order.
    var source_next = [_]u32{ 0, 0 };
    var position_next = [_]u32{ 0, 0 };
    for (expected, 0..) |want, d| {
        const binding = try owner.binding(@intCast(d));
        const r = want.region.index().?;
        try std.testing.expectEqual(want.region, binding.region);
        try std.testing.expectEqual(source_next[r], binding.source_base);
        try std.testing.expectEqual(want.sources, binding.source_count);
        try std.testing.expectEqual(position_next[r], binding.position_base);
        try std.testing.expectEqual(want.positions, binding.position_count);
        try std.testing.expectEqual(@as(u64, 1), binding.layout_generation);
        source_next[r] += want.sources;
        position_next[r] += want.positions;
    }
    try std.testing.expectEqual([_]u32{ 17, 10 }, source_next);
    try std.testing.expectEqual([_]u32{ 94, 39 }, position_next);
    try std.testing.expectError(error.InvalidArgument, owner.binding(10));

    // Layout: header, sizes, offsets on 8 bytes and disjoint, references global.
    for ([_]plan.RegionClass{ .normal, .administrator }, 0..) |class, r| {
        const borrow = try owner.acquire(class, 1);
        defer owner.release(&borrow) catch unreachable;
        try std.testing.expectEqual(@as(u64, 1), borrow.epoch);
        const data = expectedDataCapacity(&declarations, class);
        try std.testing.expectEqual(regionBytes(source_next[r], position_next[r], data), borrow.byte_length);
        try std.testing.expectEqual(@as(u64, 0), borrow.byte_length % plan.region_alignment);
        try std.testing.expectEqual(@as(usize, 0), @intFromPtr(borrow.region_address.?) % plan.region_alignment);
        const header = try ss.describe(borrow.bytes());
        try std.testing.expectEqual(plan.dataKind(class), header.data_kind);
        try std.testing.expectEqual(source_next[r], header.source_count);
        try std.testing.expectEqual(position_next[r], header.position_count);
        try std.testing.expectEqual(data, header.data_capacity);
        var end: u64 = 0;
        var p: u32 = 0;
        while (p < header.position_count) : (p += 1) {
            const spec = try ss.describePosition(borrow.bytes(), p);
            try std.testing.expectEqual(@as(u32, 0), spec.offset % 8);
            try std.testing.expect(spec.offset >= end);
            end = @as(u64, spec.offset) + spec.size;
        }
        var s: u32 = 0;
        while (s < header.source_count) : (s += 1) {
            const identity = try ss.describeSource(borrow.bytes(), s);
            try std.testing.expect(std.mem.startsWith(u8, identity.owner(), "metric."));
        }
    }

    // Global references: the normal result's value column reads its own rows,
    // the catalog's string columns their own table, the vector's string table
    // belongs to its rows source.
    const normal = try owner.acquire(.normal, 1);
    {
        const result_binding = try owner.binding(1);
        const value = try ss.describePosition(normal.bytes(), result_binding.position(3).?);
        try std.testing.expectEqual(result_binding.position(0).?, value.identity);
        try std.testing.expectEqual(result_binding.source(values_source).?, value.source);
        const provenance = try ss.describePosition(normal.bytes(), result_binding.position(12).?);
        try std.testing.expectEqual(result_binding.source(provenance_source).?, provenance.source);
        try std.testing.expectEqual(@as(u32, 8 * (8 + 32 * 4)), provenance.size);
        const catalog_binding = try owner.binding(2);
        const name = try ss.describePosition(normal.bytes(), catalog_binding.position(4).?);
        try std.testing.expectEqual(ss.PositionKind.string_column, name.kind);
        try std.testing.expectEqual(catalog_binding.position(6).?, name.strings);
        try std.testing.expectEqual(@as(u32, 30), catalog_binding.position_base);
        const vector_binding = try owner.binding(4);
        const text = try ss.describePosition(normal.bytes(), vector_binding.position(14).?);
        try std.testing.expectEqual(ss.PositionKind.string_table, text.kind);
        try std.testing.expectEqual(vector_binding.source(rows_source).?, text.source);
        try std.testing.expectEqual(@as(u32, 4096), text.size);
    }

    // A writer writes by its binding; a reader selects by its view.
    {
        const binding = try owner.binding(5); // scalar history
        var writer = try ss.Writer.begin(normal.bytes(), binding.source(rows_source).?);
        try writer.store(u64, binding.position(0).?, 0, &.{ 11, 12, 13 });
        try writer.setLength(binding.position(0).?, 3, 0);
        try writer.end(.has_value, 1_001);

        var sources: [4]u32 = undefined;
        var view = owner_module.ReadView{};
        try std.testing.expectError(error.BufferTooSmall, owner.readView(&.{ 5, 2 }, sources[0..2], &view));
        try std.testing.expectEqual(@as(u32, 3), view.source_count);
        // A refused view leaves the buffer unspecified, so the good one is last.
        try std.testing.expectError(error.MixedRegions, owner.readView(&.{ 5, 7 }, &sources, &view));
        try std.testing.expectError(error.InvalidArgument, owner.readView(&.{ 5, 5 }, &sources, &view));
        try owner.readView(&.{ 5, 2 }, &sources, &view);
        try std.testing.expectEqualSlices(u32, &.{ 8, 13, 14 }, sources[0..view.source_count]);
        try std.testing.expectEqual(plan.RegionClass.normal, view.region);

        const selected = sources[0..3];
        const size = try ss.selectionSize(normal.bytes(), selected);
        const held = try std.testing.allocator.alloc(ss.SourceCopy, selected.len);
        defer std.testing.allocator.free(held);
        const records = try std.testing.allocator.alloc(ss.SelectedPosition, size.position_count);
        defer std.testing.allocator.free(records);
        const bytes = try std.testing.allocator.alloc(u8, @intCast(size.data_bytes));
        defer std.testing.allocator.free(bytes);
        var selection = try ss.Selection.init(normal.bytes(), selected, held, records, bytes);
        const read = ss.read(normal.bytes(), &selection, binding.source(rows_source).?, ss.default_retry_limit);
        try std.testing.expect(read == .value);
        const value = ss.positionValue(&selection, binding.position(0).?);
        try std.testing.expectEqual(@as(u32, 3), value.value.length);
        try std.testing.expectEqualSlices(u8, std.mem.sliceAsBytes(&[_]u64{ 11, 12, 13 }), value.value.bytes);
    }
    try owner.release(&normal);

    try owner.stop(2_000);
    try owner.deinit();
}

test "a changed declaration rebuilds only its region after every borrow ends" {
    const fixture = Fixture.init(40);
    const declarations = fixture.declarations();
    var owner = Owner.init(std.testing.allocator, .{ .borrow_drain_timeout_ms = 0 });
    var failure = plan.ApplyFailure{};
    try owner.apply(&declarations, 1_000, &failure);
    const old_catalog = try owner.binding(2);
    const old_admin = try owner.binding(7);

    // The same declarations again: nothing is rebuilt.
    try owner.apply(&declarations, 1_100, &failure);
    try std.testing.expectEqual(old_catalog, try owner.binding(2));
    const kept = try owner.acquire(.normal, old_catalog.layout_generation);
    try std.testing.expectEqual(@as(u64, 1), kept.epoch);

    // The catalog's capacity changes: it registers again. A held borrow keeps
    // the old mapping and the apply is refused without any change.
    const grown = Fixture.init(48);
    const regrown = grown.declarations();
    try std.testing.expectError(error.Borrowed, owner.apply(&regrown, 1_200, &failure));
    try std.testing.expectEqual(@as(u32, 0), failure.region);
    try std.testing.expectEqual(old_catalog, try owner.binding(2));
    const still = try owner.acquire(.normal, old_catalog.layout_generation);
    try owner.release(&still);
    try owner.release(&kept);

    const admin_borrow = try owner.acquire(.administrator, old_admin.layout_generation);
    try owner.apply(&regrown, 1_300, &failure);
    // The replaced mapping's epoch is gone: a borrow of it is not accepted.
    try std.testing.expectError(error.NotBorrowed, owner.release(&kept));

    const new_catalog = try owner.binding(2);
    try std.testing.expectEqual(@as(u64, 2), new_catalog.layout_generation);
    try std.testing.expectEqual(old_catalog.position_base, new_catalog.position_base);
    try std.testing.expectError(error.StaleBinding, owner.acquire(.normal, old_catalog.layout_generation));
    const rebuilt = try owner.acquire(.normal, new_catalog.layout_generation);
    try std.testing.expectEqual(@as(u64, 2), rebuilt.epoch);
    const position = try ss.describePosition(rebuilt.bytes(), new_catalog.position(1).?);
    try std.testing.expectEqual(@as(u32, 48 * 4), position.size);
    try owner.release(&rebuilt);

    // The administrator region was not touched, and its borrow stayed valid.
    try std.testing.expectEqual(old_admin, try owner.binding(7));
    try std.testing.expectEqual(@as(u64, 1), admin_borrow.epoch);
    try owner.release(&admin_borrow);

    // A bad declaration leaves the current plan in place.
    var broken = grown;
    broken.vector[3].element_width = 3;
    const refused = broken.declarations();
    try std.testing.expectError(error.DeclarationInvalid, owner.apply(&refused, 1_400, &failure));
    try std.testing.expectEqual(@as(u32, 4), failure.declaration);
    try std.testing.expectEqual(@as(u32, 3), failure.position);
    try std.testing.expectEqual(new_catalog, try owner.binding(2));

    try owner.stop(2_000);
    try owner.deinit();
}

fn releaseLater(owner: *Owner, borrow: *const owner_module.Borrow) void {
    Sleep(50);
    owner.release(borrow) catch unreachable;
}

test "a replacement waits for a borrow that ends within the timeout" {
    const fixture = Fixture.init(40);
    const declarations = fixture.declarations();
    var owner = Owner.init(std.testing.allocator, .{ .borrow_drain_timeout_ms = 5_000 });
    var failure = plan.ApplyFailure{};
    try owner.apply(&declarations, 1_000, &failure);
    const borrow = try owner.acquire(.normal, 1);
    const thread = try std.Thread.spawn(.{}, releaseLater, .{ &owner, &borrow });
    const grown = Fixture.init(64);
    const regrown = grown.declarations();
    try owner.apply(&regrown, 1_100, &failure);
    thread.join();
    const binding = try owner.binding(0);
    try std.testing.expectEqual(@as(u64, 2), binding.layout_generation);
    try owner.stop(2_000);
    try owner.deinit();
}

test "stop writes every source ended and unmaps once borrows end" {
    const fixture = Fixture.init(40);
    const declarations = fixture.declarations();
    var owner = Owner.init(std.testing.allocator, .{ .borrow_drain_timeout_ms = 0 });
    var failure = plan.ApplyFailure{};
    try owner.apply(&declarations, 1_000, &failure);
    const binding = try owner.binding(7);
    const admin = try owner.acquire(.administrator, binding.layout_generation);
    // A writer's round in flight when the service stops.
    var writer = try ss.Writer.begin(admin.bytes(), binding.source(values_source).?);

    try std.testing.expectError(error.NotStopped, owner.deinit());
    try std.testing.expectError(error.Borrowed, owner.stop(2_000));
    try std.testing.expectError(error.Stopped, owner.acquire(.administrator, binding.layout_generation));
    try std.testing.expectError(error.Stopped, owner.binding(7));
    try std.testing.expectError(error.Stopped, owner.apply(&declarations, 2_100, &failure));
    try std.testing.expectError(error.Borrowed, owner.deinit());

    // Every source of the still-borrowed region reads ended, writers cannot begin.
    const header = try ss.describe(admin.bytes());
    const entries: [*]const ss.SourceEntry = @ptrCast(@alignCast(admin.bytes()[@sizeOf(ss.Header)..].ptr));
    var s: u32 = 0;
    while (s < header.source_count) : (s += 1) {
        try std.testing.expectEqual(ss.SourceState.ended, entries[s].state);
        try std.testing.expect(entries[s].sequence & ss.ended_bit != 0);
        try std.testing.expectEqual(@as(i64, 2_000), entries[s].write_time_unix_ms);
    }
    try std.testing.expectError(error.Ended, writer.end(.has_value, 2_001));
    try std.testing.expectError(error.Ended, ss.Writer.begin(admin.bytes(), binding.source(rows_source).?));

    try owner.release(&admin);
    try owner.stop(2_200);
    try owner.deinit();
}

test "a region no declaration targets is not created" {
    const sources = [_]ss.SourceIdentity{try ss.SourceIdentity.init("only.normal", 1, 0, 0)};
    const positions = [_]plan.PositionDeclaration{column(0, 8, 4, none)};
    const declarations = [_]plan.Declaration{plan.Declaration.init(.normal, &sources, &positions)};
    var owner = Owner.init(std.testing.allocator, .{ .borrow_drain_timeout_ms = 5_000 });
    var failure = plan.ApplyFailure{};
    try owner.apply(&declarations, 1, &failure);
    try std.testing.expectError(error.Absent, owner.acquire(.administrator, 0));
    const borrow = try owner.acquire(.normal, 1);
    try std.testing.expectEqual(plan.region_alignment, borrow.byte_length);
    try owner.release(&borrow);
    // Every declaration removed: the normal region goes too.
    try owner.apply(&.{}, 2, &failure);
    try std.testing.expectError(error.Absent, owner.acquire(.normal, 2));
    try std.testing.expectError(error.InvalidArgument, owner.binding(0));
    try owner.stop(3);
    try owner.deinit();
}
