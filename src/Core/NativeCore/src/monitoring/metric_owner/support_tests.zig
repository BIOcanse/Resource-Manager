const std = @import("std");
const p = @import("protocol.zig");
const st = @import("state.zig");
const api = @import("root.zig");
const ss = @import("rm_shared_section");
const h = @import("history.zig");
const cp = @import("persistence.zig");
const exports = @import("support_exports.zig");
const v = @import("vector_inputs.zig");
const eq = std.testing.expectEqual;
const expect = std.testing.expect;

// Real SharedSection creator, not a mock Writer. Test allocations are leak
// checked and released; production never calls a heap allocator.
const Fixture = struct {
    arena: std.heap.ArenaAllocator,
    owner: *st.Owner,
    region: []align(8) u8,
    active: []align(8) u8,
    work: []align(8) u8,
    history: h.Binding,
    history_region: []align(8) u8,
    history_work: []align(8) u8,
    catalog: p.Catalog,
    mixed: ?h.MixedBinding = null,
    vector_binding: ?v.BoundVector = null,
    fn init() !Fixture {
        return initMode(false);
    }
    fn initMode(vectors: bool) !Fixture {
        var arena = std.heap.ArenaAllocator.init(std.testing.allocator);
        errdefer arena.deinit();
        const a = arena.allocator();
        const positions = try a.alloc(ss.PositionSpec, if (vectors) 64 else 30);
        var bindings: [2]p.TableBinding = undefined;
        var offset: u32 = 0;
        var pos: u32 = 0;
        for (&bindings, 0..) |*b, table| {
            b.* = .{ .region = null, .byte_length = 0, .rows_source = @intCast(table * 4), .value_source = @intCast(table * 4 + 1), .health_source = @intCast(table * 4 + 2), .provenance_source = @intCast(table * 4 + 3) };
            const used: usize = if (table == 0) 16 else 12;
            var slot: usize = 0;
            while (slot < used) : (slot += 1) {
                offset = std.mem.alignForward(u32, offset, 8);
                const width: u32 = if (table == 1) (if (slot == 4 or slot == 5 or slot == 9 or slot == 10 or slot == 11) 4 else 8) else if (slot == 9) 1 else if (slot == 3 or slot == 4 or slot == 10 or slot == 11 or slot == 13) 4 else 8;
                const source = if (slot == 0 or (table == 0 and slot == 15)) b.rows_source else if (table == 0 and slot >= 10 and slot <= 12) b.health_source else b.value_source;
                b.positions[slot] = pos;
                positions[pos] = if (table == 0 and slot == 9) ss.PositionSpec.stringTable(source, offset, 64) else ss.PositionSpec.column(source, offset, 3, width, if (slot == 0) ss.none else b.positions[0]);
                offset += positions[pos].size;
                pos += 1;
            }
            offset = std.mem.alignForward(u32, offset, 8);
            b.provenance_position = pos;
            positions[pos] = ss.PositionSpec.column(b.provenance_source, offset, 8 + 3 * @as(u32, if (table == 0) 12 else 4), 8, ss.none);
            offset += positions[pos].size;
            pos += 1;
        }
        var vector_binding = v.BoundVector{ .source_id = 10, .rows_source = 8, .value_source = 9, .health_source = 10 };
        var mixed = h.MixedBinding{ .base = .{ .region = null, .byte_length = 0, .rows_source = 11, .data_source = 12, .positions = undefined }, .kind_position = 60, .address_position = 61, .text_position = 62, .live_position = 63 };
        if (vectors) {
            for (0..21) |slot| {
                offset = std.mem.alignForward(u32, offset, 8);
                const width: u32 = switch (slot) {
                    2, 3, 4, 6, 7, 10, 11, 17, 20 => 4,
                    14 => 1,
                    else => 8,
                };
                const rows = slot <= 4 or (slot >= 13 and slot <= 18);
                const health = (slot >= 10 and slot <= 12) or slot == 20;
                const source: u32 = if (rows) 8 else if (health) 10 else 9;
                vector_binding.positions[slot] = pos;
                positions[pos] = if (slot == 14) ss.PositionSpec.stringTable(source, offset, 432) else ss.PositionSpec.column(source, offset, 3, width, if (slot == 0) ss.none else vector_binding.positions[0]);
                offset += positions[pos].size;
                pos += 1;
            }
            for (0..13) |slot| {
                offset = std.mem.alignForward(u32, offset, 8);
                const width: u32 = switch (slot) {
                    0, 1, 6, 7, 10 => 8,
                    11 => 1,
                    else => 4,
                };
                if (slot < 9) mixed.base.positions[slot] = pos;
                positions[pos] = if (slot == 11) ss.PositionSpec.stringTable(12, offset, 432) else ss.PositionSpec.column(if (slot == 0) 11 else 12, offset, if (slot >= 6 and slot <= 8) 16 else 3, width, if ((slot >= 1 and slot <= 5) or slot == 9 or slot == 10 or slot == 12) mixed.base.positions[0] else ss.none);
                offset += positions[pos].size;
                pos += 1;
            }
        }
        offset = std.mem.alignForward(u32, offset, 8);
        const source_count: u32 = if (vectors) 13 else 8;
        const region = try a.alignedAlloc(u8, .@"8", @intCast(ss.requiredSize(source_count, @intCast(positions.len), offset)));
        const sources = try a.alloc(ss.SourceIdentity, source_count);
        for (sources, 0..) |*src, i| src.* = try ss.SourceIdentity.init("support-test", @intCast(i + 1), 0, 0);
        try ss.initialize(region, 42, source_count, @intCast(positions.len), offset, sources, positions);
        vector_binding.region = region.ptr;
        vector_binding.byte_length = region.len;
        mixed.base.region = region.ptr;
        mixed.base.byte_length = region.len;
        for (&bindings) |*b| {
            b.region = region.ptr;
            b.byte_length = region.len;
        }
        const config = p.Config{ .source_capacity = 1, .rule_capacity = 3, .metric_capacity = 3, .max_value_bytes = 64, .payload_work_bytes = 64, .active_limit = 1024 * 1024, .work_limit = 1024 * 1024 };
        var sizes: p.Sizes = undefined;
        try eq(@as(u32, 0), api.rm_metric_owner_required_sizes(&config, &sizes, null));
        const active = try a.alignedAlloc(u8, .@"8", @intCast(sizes.active));
        const whole_work = try a.alignedAlloc(u8, .@"8", @intCast(sizes.work + (if (vectors) @as(u64, 32768) else 0)));
        const work = whole_work[0..@intCast(sizes.work)];
        var owner: ?*st.Owner = null;
        try eq(@as(u32, 0), api.rm_metric_owner_bind(&config, active.ptr, active.len, &bindings, 1, &bindings[1], &owner, null));
        // All descriptor arrays live in the fixture arena through teardown.
        const ids = try a.dupe(u64, &.{ 100, 101, 102 });
        const metric_ids = try a.dupe(u64, &.{ 1000, 1001, 1002 });
        if (vectors) metric_ids[0] = 10; // collide with the vector SOURCE ID
        const identities = try a.dupe(u64, &.{ 2000, 2001, 2002 });
        const metric_identity = try a.dupe(u64, &.{ 3000, 3001, 3002 });
        const source_ids = try a.dupe(u64, &.{ 10, 10, 10 });
        const kinds = try a.dupe(u32, &.{ 13, 13, 13 });
        const flags = try a.dupe(u32, &.{ 0, 0, 0 });
        const priorities = try a.dupe(u32, &.{ 1, 1, 1 });
        const value_kinds = try a.dupe(u32, &.{ 3, 2, 5 });
        const minima = try a.dupe(u64, &.{ 0, @bitCast(@as(i64, -100)), 0 });
        const maxima = try a.dupe(u64, &.{ std.math.maxInt(u64), 100, 64 });
        const masks = try a.dupe(u64, &.{ 1, 1, 1 });
        const sid = try a.dupe(u64, &.{10});
        const incarnations = try a.dupe(u64, &.{1});
        const capabilities = try a.dupe(u64, &.{1});
        const availability = try a.dupe(u32, &.{1});
        const catalog = p.Catalog{ .sources = .{ .count = 1, .ids = sid.ptr, .incarnations = incarnations.ptr, .capabilities = capabilities.ptr, .availability = availability.ptr }, .rules = .{ .count = 3, .ids = ids.ptr, .identities = identities.ptr, .source_ids = source_ids.ptr, .metric_ids = metric_ids.ptr, .kinds = kinds.ptr, .flags = flags.ptr, .priorities = priorities.ptr, .value_kinds = value_kinds.ptr, .minimum_bits = minima.ptr, .maximum_bits = maxima.ptr, .capability_masks = masks.ptr }, .metrics = .{ .count = 3, .ids = metric_ids.ptr, .identities = metric_identity.ptr } };
        try eq(@as(u32, 0), api.rm_metric_owner_replace_catalog(owner, &catalog, work.ptr, work.len, 100, null));
        if (vectors) try eq(@as(u32, 0), v.rm_metric_vector_gpu_bind(owner, &vector_binding, null));
        var hb = h.Binding{ .region = null, .byte_length = 0, .rows_source = 0, .data_source = 1, .positions = undefined };
        var hp: [9]ss.PositionSpec = undefined;
        var ho: u32 = 0;
        for (&hp, 0..) |*spec, slot| {
            ho = std.mem.alignForward(u32, ho, 8);
            hb.positions[slot] = @intCast(slot);
            const width: u32 = if (slot == 0 or slot == 1 or slot == 6 or slot == 7) 8 else 4;
            spec.* = ss.PositionSpec.column(if (slot == 0) 0 else 1, ho, if (slot < 6) 3 else 16, width, if (slot >= 1 and slot <= 5) 0 else ss.none);
            ho += spec.size;
        }
        const hr = try a.alignedAlloc(u8, .@"8", @intCast(ss.requiredSize(2, 9, ho)));
        const hs = [_]ss.SourceIdentity{ try ss.SourceIdentity.init("history-rows", 1, 0, 0), try ss.SourceIdentity.init("history-values", 2, 0, 0) };
        try ss.initialize(hr, 42, 2, 9, ho, &hs, &hp);
        hb.region = hr.ptr;
        hb.byte_length = hr.len;
        return .{ .arena = arena, .owner = owner.?, .region = region, .active = active, .work = work, .history = hb, .history_region = hr, .history_work = if (vectors) @alignCast(whole_work[std.mem.alignForward(usize, @intCast(sizes.work), 8)..]) else try a.alignedAlloc(u8, .@"8", @intCast(h.configureWorkSize(3, 16))), .catalog = catalog, .mixed = if (vectors) mixed else null, .vector_binding = if (vectors) vector_binding else null };
    }
    fn deinit(f: *Fixture) void {
        _ = api.rm_metric_owner_unbind(f.owner, null);
        f.arena.deinit();
    }
    fn plan(f: *Fixture, indices: []const u32, utc: i64) !p.Round {
        var out: p.Round = undefined;
        try eq(@as(u32, 0), api.rm_metric_owner_plan(f.owner, indices.ptr, @intCast(indices.len), f.work.ptr, f.work.len, utc, &out, null));
        return out;
    }
    fn commit(f: *Fixture, r: p.Round, indices: []const u32, utc: i64, history: bool) !void {
        try eq(@as(u32, 0), api.rm_metric_owner_prepare(f.owner, r.token, null));
        if (history) try eq(p.Code.ok, h.commit(f.owner, f.history, indices, r.token, utc)) else try eq(@as(u32, 0), api.rm_metric_owner_commit(f.owner, r.token, utc, null));
        try expect(f.owner.work.region == null);
    }
    fn configure(f: *Fixture, requirements: []const h.Requirement) !void {
        try h.configure(f.owner, f.history, requirements, f.history_work, 100);
    }
    fn image(f: *Fixture) ![]u8 {
        const len = try cp.encodedSize(f.owner);
        const bytes = try f.arena.allocator().alloc(u8, @intCast(len));
        try eq(len, try cp.encode(f.owner, bytes));
        return bytes;
    }
    fn restoreWork(f: *Fixture, len: usize) ![]align(8) u8 {
        return f.arena.allocator().alignedAlloc(u8, .@"8", @intCast(cp.restoreWorkSize(f.owner, len)));
    }
};
fn current(r: p.Round, i: usize, kind: u32, low: u64) void {
    r.observations.status.?[i] = 1;
    r.observations.kinds.?[i] = kind;
    r.observations.presence.?[i] = 1;
    r.observations.low.?[i] = low;
}
fn fill(f: *Fixture) !void {
    const r = try f.plan(&.{ 0, 1, 2 }, 200);
    current(r, 0, 3, 9_007_199_254_740_993);
    current(r, 1, 2, 0);
    r.observations.presence.?[1] = 2;
    current(r, 2, 5, 0); // present empty byte string
    try f.commit(r, &.{ 0, 1, 2 }, 200, false);
}
fn rehash(image: []u8) void {
    const hash = std.hash.Wyhash.hash(0x726d6f636b707431, image[0 .. image.len - 8]);
    std.mem.writeInt(u64, image[image.len - 8 ..][0..8], if (hash == 0) 1 else hash, .little);
}
test "requested history appends numeric or null, never retained good; unrequested rows do no work" {
    var f = try Fixture.init();
    defer f.deinit();
    try f.configure(&.{.{ .metric_id = 1000, .rounds = 2 }});
    var r = try f.plan(&.{0}, 200);
    current(r, 0, 3, 42);
    try f.commit(r, &.{0}, 200, true);
    r = try f.plan(&.{1}, 300);
    current(r, 0, 2, 8);
    try f.commit(r, &.{1}, 300, true);
    var t = try h.Table.init(f.history);
    try eq(@as(u32, 1), t.load(u32, 4, 0));
    r = try f.plan(&.{0}, 400); // ordinary failure retains public good
    try f.commit(r, &.{0}, 400, true);
    t = try h.Table.init(f.history);
    try eq(@as(u32, 2), t.load(u32, 4, 0));
    try eq(@as(u64, @bitCast(@as(f64, 42))), t.sample(0, 0).bits);
    try eq(@as(i64, 200), t.sample(0, 0).time);
    try eq(@as(u32, 0), t.sample(0, 1).present);
    try eq(@as(u64, 0), t.sample(0, 1).bits);
    try eq(@as(i64, 400), t.sample(0, 1).time);
    const raw = try st.table(f.owner, 0);
    try eq(@as(u64, 42), raw.load(u64, 1, 0));
    const result = try st.resultTable(f.owner);
    try eq(@as(u32, 2), result.load(u32, 11, 0));
    try eq(@as(u32, 3), result.load(u32, 4, 0)); // diagnostic unavailable != retained public status
}
test "history configure seeds existing attempt, merges max, resizes newest, drops and clears" {
    var f = try Fixture.init();
    defer f.deinit();
    try fill(&f);
    try f.configure(&.{ .{ .metric_id = 1000, .rounds = 1 }, .{ .metric_id = 1000, .rounds = 3 }, .{ .metric_id = 1001, .rounds = 2 } });
    var t = try h.Table.init(f.history);
    try eq(@as(u32, 2), t.rows);
    try eq(@as(u32, 3), t.load(u32, 2, 0));
    try eq(@as(u32, 1), t.load(u32, 4, 0));
    try eq(@as(u32, 0), t.sample(1, 0).present);
    var time: i64 = 300;
    while (time <= 600) : (time += 100) {
        const r = try f.plan(&.{0}, time);
        current(r, 0, 3, @intCast(time));
        try f.commit(r, &.{0}, time, true);
    }
    try f.configure(&.{.{ .metric_id = 1000, .rounds = 2 }});
    t = try h.Table.init(f.history);
    try eq(@as(u32, 1), t.rows);
    try eq(@as(u32, 2), t.load(u32, 4, 0));
    try eq(@as(i64, 500), t.sample(0, 0).time);
    try eq(@as(i64, 600), t.sample(0, 1).time);
    try f.configure(&.{.{ .metric_id = 1000, .rounds = 16 }});
    t = try h.Table.init(f.history);
    try eq(@as(u32, 16), t.load(u32, 2, 0));
    try eq(@as(u32, 2), t.load(u32, 4, 0));
    try f.configure(&.{});
    t = try h.Table.init(f.history);
    try eq(@as(u32, 0), t.rows);
}
test "history rejected depth/work/cap/unknown plans leave complete region unchanged" {
    var f = try Fixture.init();
    defer f.deinit();
    try f.configure(&.{.{ .metric_id = 1000, .rounds = 2 }});
    const before = try f.arena.allocator().dupe(u8, f.history_region);
    try std.testing.expectError(error.Capacity, f.configure(&.{.{ .metric_id = 1000, .rounds = std.math.maxInt(u32) }}));
    try std.testing.expectEqualSlices(u8, before, f.history_region);
    try std.testing.expectError(error.Invalid, f.configure(&.{.{ .metric_id = 1000, .rounds = 0 }}));
    try std.testing.expectError(error.Invalid, f.configure(&.{.{ .metric_id = 9999, .rounds = 2 }}));
    try std.testing.expectError(error.Workspace, h.configure(f.owner, f.history, &.{.{ .metric_id = 1000, .rounds = 3 }}, f.history_work[0..8], 200));
    try std.testing.expectEqualSlices(u8, before, f.history_region);
}
test "history preflight rejects bad completion before native publication and detaches" {
    var f = try Fixture.init();
    defer f.deinit();
    try f.configure(&.{.{ .metric_id = 1000, .rounds = 2 }});
    const before = try f.arena.allocator().dupe(u8, f.region);
    const r = try f.plan(&.{0}, 200);
    current(r, 0, 3, 42);
    try eq(@as(u32, 0), api.rm_metric_owner_prepare(f.owner, r.token, null));
    try eq(p.Code.invalid_contract, h.commit(f.owner, f.history, &.{3}, r.token, 200));
    try std.testing.expectEqualSlices(u8, before, f.region);
    try expect(f.owner.work.region == null);
}
test "empty history has no writer and unavailable history writer rejects before kernel stores" {
    var f = try Fixture.init();
    defer f.deinit();
    try h.configure(f.owner, f.history, &.{}, f.history_work[0..0], 100);
    const initial = try f.arena.allocator().dupe(u8, f.history_region);
    var r = try f.plan(&.{0}, 200);
    current(r, 0, 3, 42);
    try f.commit(r, &.{0}, 200, true);
    try std.testing.expectEqualSlices(u8, initial, f.history_region);
    try f.configure(&.{.{ .metric_id = 1000, .rounds = 2 }});
    const before = try f.arena.allocator().dupe(u8, f.region);
    r = try f.plan(&.{0}, 300);
    current(r, 0, 3, 64);
    try eq(@as(u32, 0), api.rm_metric_owner_prepare(f.owner, r.token, null));
    var busy = try ss.Writer.begin(f.history_region, f.history.data_source);
    defer busy.end(.has_value, 300) catch {};
    try eq(p.Code.shared_section, h.commit(f.owner, f.history, &.{0}, r.token, 300));
    try std.testing.expectEqualSlices(u8, before, f.region);
    try expect(f.owner.work.region == null);
}
test "history writer rollback preserves samples and source facts when kernel writer admission fails" {
    var f = try Fixture.init();
    defer f.deinit();
    try f.configure(&.{.{ .metric_id = 1000, .rounds = 2 }});
    const r = try f.plan(&.{0}, 200);
    current(r, 0, 3, 42);
    try expect(f.owner.work.region == f.work.ptr);
    try eq(@as(u32, 0), api.rm_metric_owner_prepare(f.owner, r.token, null));
    const layout = try ss.layout.Layout.load(f.history_region);
    const data = try f.arena.allocator().dupe(u8, layout.dataArea(f.history_region));
    const entry = layout.source(f.history_region, f.history.data_source);
    const time = @atomicLoad(i64, &entry.write_time_unix_ms, .acquire);
    const status = @atomicLoad(ss.SourceState, &entry.state, .acquire);
    const reason = @atomicLoad(u32, &entry.reason, .acquire);
    // This conflict occurs after HistoryCommit has acquired its own writer.
    var busy = try ss.Writer.begin(f.region, f.owner.sources[0].binding.value_source);
    defer busy.end(.has_value, 100) catch {};
    try eq(p.Code.shared_section, h.commit(f.owner, f.history, &.{0}, r.token, 200));
    try std.testing.expectEqualSlices(u8, data, layout.dataArea(f.history_region));
    try eq(time, @atomicLoad(i64, &entry.write_time_unix_ms, .acquire));
    try eq(status, @atomicLoad(ss.SourceState, &entry.state, .acquire));
    try eq(reason, @atomicLoad(u32, &entry.reason, .acquire));
    try expect(f.owner.work.region == null);
}
test "history seed accepts a successful or failed attempt at Unix epoch zero" {
    var f = try Fixture.init();
    defer f.deinit();
    const r = try f.plan(&.{ 0, 1 }, 0);
    current(r, 0, 3, 42);
    try f.commit(r, &.{ 0, 1 }, 0, false);
    try f.configure(&.{ .{ .metric_id = 1000, .rounds = 2 }, .{ .metric_id = 1001, .rounds = 2 } });
    const t = try h.Table.init(f.history);
    try eq(@as(u32, 1), t.load(u32, 4, 0));
    try eq(@as(i64, 0), t.sample(0, 0).time);
    try eq(@as(u32, 1), t.sample(0, 0).present);
    try eq(@as(u32, 1), t.load(u32, 4, 1));
    try eq(@as(i64, 0), t.sample(1, 0).time);
    try eq(@as(u32, 0), t.sample(1, 0).present);
}
test "catalog-only changes do not invent a completed history sample and support errors detach held work" {
    var f = try Fixture.init();
    defer f.deinit();
    try eq(@as(u32, 0), api.rm_metric_owner_replace_catalog(f.owner, &f.catalog, f.work.ptr, f.work.len, 200, null));
    try f.configure(&.{.{ .metric_id = 1000, .rounds = 2 }});
    const t = try h.Table.init(f.history);
    try eq(@as(u32, 0), t.load(u32, 4, 0));
    _ = try f.plan(&.{0}, 300);
    try expect(f.owner.work.region != null);
    var failure: p.Failure = .{};
    try eq(@intFromEnum(p.Code.invalid_contract), exports.rm_metric_owner_history_configure(f.owner, null, null, 0, f.work.ptr, f.work.len, 300, &failure));
    try expect(f.owner.work.region == null);
    _ = try f.plan(&.{0}, 300);
    try expect(f.owner.work.region != null);
    var image_size: u64 = 0;
    var work_size: u64 = 0;
    try eq(@intFromEnum(p.Code.stale_identity), exports.rm_metric_owner_checkpoint_size(f.owner, &image_size, &work_size, &failure));
    try expect(f.owner.work.region == null);
}
test "history reserves post-commit sequence headroom before any public store" {
    var f = try Fixture.init();
    defer f.deinit();
    try f.configure(&.{.{ .metric_id = 1000, .rounds = 2 }});
    const r = try f.plan(&.{0}, 200);
    current(r, 0, 3, 42);
    try eq(@as(u32, 0), api.rm_metric_owner_prepare(f.owner, r.token, null));
    const layout = try ss.layout.Layout.load(f.region);
    const entry = layout.sourceMut(f.region, f.owner.sources[0].binding.value_source);
    const prior = @atomicLoad(u64, &entry.sequence, .acquire);
    defer @atomicStore(u64, &entry.sequence, prior, .release);
    @atomicStore(u64, &entry.sequence, ss.ended_bit - 4, .release);
    const before = try f.arena.allocator().dupe(u8, f.region);
    const history_before = try f.arena.allocator().dupe(u8, f.history_region);
    try eq(p.Code.capacity_exceeded, h.commit(f.owner, f.history, &.{0}, r.token, 200));
    try eq(p.Family.generation, f.owner.failure.family);
    try std.testing.expectEqualSlices(u8, before, f.region);
    try std.testing.expectEqualSlices(u8, history_before, f.history_region);
    try expect(f.owner.work.region == null);
}
test "checkpoint exact >2^53 null and empty bytes roundtrip via caller work" {
    var source = try Fixture.init();
    defer source.deinit();
    var target = try Fixture.init();
    defer target.deinit();
    try fill(&source);
    const image = try source.image();
    const work = try target.restoreWork(image.len);
    var prepared: cp.Prepared = .{};
    try cp.prepare(target.owner, image, work, &prepared);
    try expect(target.owner.work.region == null);
    try cp.publish(target.owner, &prepared, 250);
    try expect(prepared.work == null and target.owner.work.region == null);
    const raw = try st.table(target.owner, 0);
    try eq(@as(u64, 9_007_199_254_740_993), raw.load(u64, 1, 0));
    try eq(@as(u32, 2), raw.load(u32, 4, 1));
    try eq(@as(u32, 5), raw.load(u32, 3, 2));
    try eq(@as(u32, 1), raw.load(u32, 4, 2));
    try eq(@as(u64, 0), raw.load(u64, 8, 2) >> 32);
    const result = try st.resultTable(target.owner);
    try eq(@as(u64, 100), result.load(u64, 2, 0));
    try eq(@as(u64, 2000), result.load(u64, 8, 0));
    try eq(raw.load(u64, 5, 0), result.load(u64, 3, 0));
    try eq(@as(u32, 1), result.load(u32, 4, 0));
    try eq(@as(u32, 1), result.load(u32, 11, 0));
    try eq(@as(u32, 0), result.load(u32, 9, 0));
    try eq(@as(u32, 0), result.load(u32, 10, 0));
    try eq(@as(u64, 100), raw.load(u64, 15, 0));
}
test "checkpoint empty catalog image and absent values restore without fabricated types" {
    var source = try Fixture.init();
    defer source.deinit();
    var target = try Fixture.init();
    defer target.deinit();
    // First exercise a populated catalog with no successful values.
    var image = try source.image();
    var work = try target.restoreWork(image.len);
    var q: cp.Prepared = .{};
    try cp.prepare(target.owner, image, work, &q);
    try cp.publish(target.owner, &q, 200);
    const t = try st.table(target.owner, 0);
    try eq(@as(u32, 5), t.load(u32, 3, 2));
    try eq(@as(u32, 0), t.load(u32, 4, 2));
    // Then zero metric/rule counts; the source identity still closes the image.
    for ([_]*Fixture{ &source, &target }) |f| {
        f.catalog.rules.count = 0;
        f.catalog.metrics.count = 0;
        try eq(@as(u32, 0), api.rm_metric_owner_replace_catalog(f.owner, &f.catalog, f.work.ptr, f.work.len, 300, null));
    }
    // Target already consumed one restore revision; advance source once.
    const r = try source.plan(&.{}, 400);
    try source.commit(r, &.{}, 400, false);
    image = try source.image();
    work = try target.restoreWork(image.len);
    try eq(@as(usize, 112), image.len);
    try cp.prepare(target.owner, image, work, &q);
    try cp.publish(target.owner, &q, 500);
    try eq(@as(u32, 0), target.owner.rule_count);
    try eq(@as(u32, 0), target.owner.metric_count);
}
test "checkpoint actual cold bytes are compact and restored exactly" {
    var source = try Fixture.init();
    defer source.deinit();
    var target = try Fixture.init();
    defer target.deinit();
    const r = try source.plan(&.{2}, 200);
    current(r, 0, 5, 0);
    @memcpy(r.payload.?[0..5], &[_]u8{ 0, 255, 4, 0, 9 });
    r.observations.payload_lengths.?[0] = 5;
    try source.commit(r, &.{2}, 200, false);
    const image = try source.image();
    try eq(@as(usize, 72 + 40 + 3 * 224 + 3 * 88 + 5), image.len);
    const work = try target.restoreWork(image.len);
    var q: cp.Prepared = .{};
    try cp.prepare(target.owner, image, work, &q);
    try cp.publish(target.owner, &q, 300);
    const t = try st.table(target.owner, 0);
    const ref = t.load(u64, 8, 2);
    const off: u32 = @truncate(ref);
    try std.testing.expectEqualSlices(u8, &.{ 0, 255, 4, 0, 9 }, t.layout.dataArea(t.bytes())[@as(usize, t.specs[9].offset) + off ..][0..5]);
    try eq(@as(u32, 5), @atomicLoad(u32, &t.layout.position(t.bytes(), t.binding.positions[9]).length, .acquire));
}
test "checkpoint persists raw diagnostic separately from retained public status" {
    var source = try Fixture.init();
    defer source.deinit();
    var target = try Fixture.init();
    defer target.deinit();
    var r = try source.plan(&.{0}, 0);
    current(r, 0, 3, 42);
    try source.commit(r, &.{0}, 0, false);
    r = try source.plan(&.{0}, 1);
    try source.commit(r, &.{0}, 1, false);
    const image = try source.image();
    const work = try target.restoreWork(image.len);
    var q: cp.Prepared = .{};
    try cp.prepare(target.owner, image, work, &q);
    try cp.publish(target.owner, &q, 2);
    const result = try st.resultTable(target.owner);
    try eq(@as(u32, 3), result.load(u32, 4, 0));
    try eq(@as(u32, 2), result.load(u32, 11, 0));
    const raw = try st.table(target.owner, 0);
    try eq(@as(u64, 42), raw.load(u64, 1, 0));
    const bad = try source.arena.allocator().dupe(u8, image);
    const public_at = 64 + 40 + 3 * 224 + 80;
    std.mem.writeInt(u32, bad[public_at..][0..4], 1, .little);
    rehash(bad);
    var fresh = try Fixture.init();
    defer fresh.deinit();
    const before = try fresh.arena.allocator().dupe(u8, fresh.region);
    try std.testing.expectError(error.Invalid, cp.prepare(fresh.owner, bad, work, &q));
    try std.testing.expectEqualSlices(u8, before, fresh.region);
    const old_length = image.len - 3 * 8;
    const old = try source.arena.allocator().alloc(u8, old_length);
    const metrics_at = 64 + 40 + 3 * 224;
    @memcpy(old[0..metrics_at], image[0..metrics_at]);
    var mi: usize = 0;
    while (mi < 3) : (mi += 1) @memcpy(old[metrics_at + mi * 80 ..][0..80], image[metrics_at + mi * 88 ..][0..80]);
    std.mem.writeInt(u64, old[16..][0..8], old_length, .little);
    rehash(old);
    try std.testing.expectError(error.Invalid, cp.prepare(fresh.owner, old, work, &q));
    try std.testing.expectEqualSlices(u8, before, fresh.region);
}
test "checkpoint publish invalidates copied preparation and allows the next committed checkpoint" {
    var source = try Fixture.init();
    defer source.deinit();
    var target = try Fixture.init();
    defer target.deinit();
    var r = try source.plan(&.{0}, 200);
    current(r, 0, 3, 42);
    try source.commit(r, &.{0}, 200, false);
    var image = try source.image();
    const work = try target.restoreWork(image.len);
    var q: cp.Prepared = .{};
    try cp.prepare(target.owner, image, work, &q);
    var copy = q;
    try cp.publish(target.owner, &q, 300);
    const before = try target.arena.allocator().dupe(u8, target.region);
    try std.testing.expectError(error.Stale, cp.publish(target.owner, &copy, 300));
    try std.testing.expectEqualSlices(u8, before, target.region);
    try expect(copy.work == null);
    r = try source.plan(&.{0}, 400);
    current(r, 0, 3, 43);
    try source.commit(r, &.{0}, 400, false);
    image = try source.image();
    try cp.prepare(target.owner, image, work, &q);
    try cp.publish(target.owner, &q, 500);
    const raw = try st.table(target.owner, 0);
    try eq(@as(u64, 43), raw.load(u64, 1, 0));
}
test "checkpoint validates column identity versions and post-write sequence headroom before publication" {
    var source = try Fixture.init();
    defer source.deinit();
    var target = try Fixture.init();
    defer target.deinit();
    try fill(&source);
    const image = try source.image();
    const work = try target.restoreWork(image.len);
    var q: cp.Prepared = .{};
    const layout = try ss.layout.Layout.load(target.region);
    const b = target.owner.sources[0].binding;
    const column = layout.positionMut(target.region, b.positions[1]);
    const version = @atomicLoad(u64, &column.identity_sequence, .acquire);
    @atomicStore(u64, &column.identity_sequence, version + 2, .release);
    var before = try target.arena.allocator().dupe(u8, target.region);
    try std.testing.expectError(error.Stale, cp.prepare(target.owner, image, work, &q));
    try std.testing.expectEqualSlices(u8, before, target.region);
    try expect(q.work == null);
    @atomicStore(u64, &column.identity_sequence, version, .release);
    try cp.prepare(target.owner, image, work, &q);
    const entry = layout.sourceMut(target.region, b.value_source);
    const sequence = @atomicLoad(u64, &entry.sequence, .acquire);
    defer @atomicStore(u64, &entry.sequence, sequence, .release);
    @atomicStore(u64, &entry.sequence, ss.ended_bit - 4, .release);
    before = try target.arena.allocator().dupe(u8, target.region);
    try std.testing.expectError(error.GenerationCapacity, cp.publish(target.owner, &q, 300));
    try std.testing.expectEqualSlices(u8, before, target.region);
    try expect(q.work == null and target.owner.work.region == null);
}
test "checkpoint preserves signed, u128, boolean and float bit patterns with exact declared types" {
    var source = try Fixture.init();
    defer source.deinit();
    var target = try Fixture.init();
    defer target.deinit();
    for ([_]*Fixture{ &source, &target }) |f| {
        @constCast(f.catalog.rules.value_kinds.?)[0] = 4;
        @constCast(f.catalog.rules.value_kinds.?)[2] = 6;
        @constCast(f.catalog.rules.maximum_bits.?)[2] = 1;
        try eq(@as(u32, 0), api.rm_metric_owner_replace_catalog(f.owner, &f.catalog, f.work.ptr, f.work.len, 100, null));
    }
    var r = try source.plan(&.{ 0, 1, 2 }, 200);
    current(r, 0, 4, 0xfedcba9876543210);
    r.observations.high.?[0] = 0x123456789abcdef0;
    current(r, 1, 2, @bitCast(@as(i64, -73)));
    current(r, 2, 6, 0);
    try source.commit(r, &.{ 0, 1, 2 }, 200, false);
    var image = try source.image();
    var work = try target.restoreWork(image.len);
    var q: cp.Prepared = .{};
    try cp.prepare(target.owner, image, work, &q);
    try cp.publish(target.owner, &q, 300);
    var t = try st.table(target.owner, 0);
    try eq(@as(u64, 0xfedcba9876543210), t.load(u64, 1, 0));
    try eq(@as(u64, 0x123456789abcdef0), t.load(u64, 2, 0));
    try eq(@as(u64, @bitCast(@as(i64, -73))), t.load(u64, 1, 1));
    try eq(@as(u32, 6), t.load(u32, 3, 2));
    try eq(@as(u64, 0), t.load(u64, 1, 2));
    try eq(@as(u32, 1), t.load(u32, 4, 2));
    // Switch both catalogs explicitly; next image is newer than target state.
    for ([_]*Fixture{ &source, &target }) |f| {
        @constCast(f.catalog.rules.value_kinds.?)[0] = 1;
        @constCast(f.catalog.rules.minimum_bits.?)[0] = @bitCast(-std.math.floatMax(f64));
        @constCast(f.catalog.rules.maximum_bits.?)[0] = @bitCast(std.math.floatMax(f64));
        try eq(@as(u32, 0), api.rm_metric_owner_replace_catalog(f.owner, &f.catalog, f.work.ptr, f.work.len, 300, null));
    }
    r = try source.plan(&.{0}, 400);
    current(r, 0, 1, @bitCast(@as(f64, -0.0)));
    try source.commit(r, &.{0}, 400, false);
    r = try source.plan(&.{0}, 450);
    current(r, 0, 1, @bitCast(@as(f64, -0.0)));
    try source.commit(r, &.{0}, 450, false);
    image = try source.image();
    work = try target.restoreWork(image.len);
    try cp.prepare(target.owner, image, work, &q);
    try cp.publish(target.owner, &q, 500);
    t = try st.table(target.owner, 0);
    try eq(@as(u32, 1), t.load(u32, 3, 0));
    try eq(@as(u64, 0x8000000000000000), t.load(u64, 1, 0));
}
test "checkpoint resets generic counter baseline and restores capability suppression without managed serialization" {
    var source = try Fixture.init();
    defer source.deinit();
    var target = try Fixture.init();
    defer target.deinit();
    // Configure a timed-counter rule in both admitted catalogs.
    for ([_]*Fixture{ &source, &target }) |f| {
        @constCast(f.catalog.rules.kinds.?)[0] = 18;
        @constCast(f.catalog.rules.value_kinds.?)[0] = 1;
        @constCast(f.catalog.rules.minimum_bits.?)[0] = @bitCast(@as(f64, 0));
        @constCast(f.catalog.rules.maximum_bits.?)[0] = @bitCast(std.math.floatMax(f64));
        try eq(@as(u32, 0), api.rm_metric_owner_replace_catalog(f.owner, &f.catalog, f.work.ptr, f.work.len, 100, null));
    }
    const r = try source.plan(&.{0}, 200);
    current(r, 0, 1, 9007199254740993);
    r.observations.high.?[0] = 50;
    r.observations.tick_frequency.?[0] = 1000;
    r.observations.flags.?[0] = 2;
    try source.commit(r, &.{0}, 200, false);
    const image = try source.image();
    const work = try target.restoreWork(image.len);
    var q: cp.Prepared = .{};
    try cp.prepare(target.owner, image, work, &q);
    try cp.publish(target.owner, &q, 300);
    try expect(source.owner.rules[0].baseline.valid);
    try eq(@as(u64, 9007199254740993), source.owner.rules[0].baseline.low);
    try eq(st.Baseline{}, target.owner.rules[0].baseline);
    try expect(!target.owner.rules[0].baseline.valid);
    try eq(source.owner.sources[0].suppressed, target.owner.sources[0].suppressed);
}
test "checkpoint unsupported source and rule suppression survive until capability change" {
    var source = try Fixture.init();
    defer source.deinit();
    var target = try Fixture.init();
    defer target.deinit();
    const r = try source.plan(&.{1}, 200);
    r.observations.status.?[0] = 3;
    r.observations.flags.?[0] = 32;
    try source.commit(r, &.{1}, 200, false);
    const image = try source.image();
    const work = try target.restoreWork(image.len);
    var q: cp.Prepared = .{};
    try cp.prepare(target.owner, image, work, &q);
    try cp.publish(target.owner, &q, 300);
    try expect(target.owner.rules[1].suppressed);
    try expect(target.owner.sources[0].suppressed);
    const skipped = try target.plan(&.{1}, 400);
    try eq(@as(u32, 0), skipped.count);
    _ = api.rm_metric_owner_abort_detach(target.owner, null);
}
test "checkpoint source-wide unsupported records unrequested alias attempts at UTC0 and restores strict guards" {
    for ([_]i64{ 0, 200 }) |utc| {
        var source = try Fixture.init();
        defer source.deinit();
        var target = try Fixture.init();
        defer target.deinit();
        for (source.owner.metrics[0..3]) |metric| try expect(!metric.attempted);
        const r = try source.plan(&.{1}, utc);
        try eq(@as(u32, 1), r.count);
        r.observations.status.?[0] = 3;
        r.observations.flags.?[0] = 32;
        r.observations.reasons.?[0] = 73;
        try eq(@as(u32, 0), api.rm_metric_owner_prepare(source.owner, r.token, null));
        for (source.owner.metrics[0..3]) |metric| try expect(!metric.attempted);
        try eq(@as(u32, 3), source.owner.work.affected_count);
        try eq(@as(u32, 1), source.owner.work.request_count);
        try eq(@as(u32, 0), api.rm_metric_owner_commit(source.owner, r.token, utc, null));
        try expect(source.owner.work.region == null);
        const result = source.owner.results[0];
        const before = try abiCopy(source.arena.allocator(), source.region);
        for (source.owner.metrics[0..3], 0..) |metric, mi| {
            const rule = source.owner.rules[mi];
            const row = metric.result_row;
            try expect(metric.attempted and rule.suppressed);
            try eq(@as(u64, 1), try abiValue(u64, &before, result.provenance_position, 11 + row * 4));
            try eq(utc, try abiValue(i64, &before, result.positions[6], row));
            try eq(@as(u32, 5), try abiValue(u32, &before, result.positions[11], row));
            try eq(@as(u32, 4), try abiValue(u32, &before, result.positions[4], row));
            try eq(@as(u32, 73), try abiValue(u32, &before, result.positions[5], row));
            try eq(@as(u64, 0), try abiValue(u64, &before, result.positions[2], row));
            try eq(rule.id, try abiValue(u64, &before, result.positions[1], row));
            try eq(rule.identity, try abiValue(u64, &before, result.positions[8], row));
            const raw = source.owner.sources[0].binding;
            try eq(@as(u32, 0), try abiValue(u32, &before, raw.positions[4], rule.row));
            try eq(try abiValue(u64, &before, raw.positions[5], rule.row), try abiValue(u64, &before, result.positions[3], row));
        }
        // A newly configured alias recognizes its completed UTC0 attempt,
        // seeding null rather than pretending it has never attempted.
        try source.configure(&.{.{ .metric_id = 1000, .rounds = 2 }});
        const history = try h.Table.init(source.history);
        try eq(@as(u32, 1), history.load(u32, 4, 0));
        try eq(utc, history.sample(0, 0).time);
        try eq(@as(u32, 0), history.sample(0, 0).present);
        const image = try source.image(); // strict encode self-validation stays on
        const work = try target.restoreWork(image.len);
        var q: cp.Prepared = .{};
        try cp.prepare(target.owner, image, work, &q);
        try cp.publish(target.owner, &q, 300);
        try expect(q.work == null and target.owner.sources[0].suppressed);
        const restored = try abiCopy(target.arena.allocator(), target.region);
        for (target.owner.metrics[0..3], 0..) |metric, mi| {
            try expect(metric.attempted and target.owner.rules[mi].suppressed);
            const rt = target.owner.results[0];
            try eq(@as(u64, 1), try abiValue(u64, &restored, rt.provenance_position, 11 + metric.result_row * 4));
            try eq(utc, try abiValue(i64, &restored, rt.positions[6], metric.result_row));
            try eq(@as(u32, 5), try abiValue(u32, &restored, rt.positions[11], metric.result_row));
            try eq(@as(u64, 0), try abiValue(u64, &restored, rt.positions[2], metric.result_row));
        }
        const bad = try target.arena.allocator().dupe(u8, image);
        const metrics_at = 64 + 40 + 3 * 224;
        std.mem.writeInt(u32, bad[metrics_at + 84 ..][0..4], 0, .little);
        rehash(bad);
        const unchanged = try target.arena.allocator().dupe(u8, target.region);
        try std.testing.expectError(if (utc == 0) error.Stale else error.Invalid, cp.prepare(target.owner, bad, work, &q));
        try expect(q.work == null);
        try std.testing.expectEqualSlices(u8, unchanged, target.region);
        const skipped = try target.plan(&.{ 0, 1, 2 }, 400);
        try eq(@as(u32, 0), skipped.count);
        try eq(@as(u32, 0), api.rm_metric_owner_abort_detach(target.owner, null));
        @constCast(target.catalog.sources.capabilities.?)[0] = 2;
        try eq(@as(u32, 0), api.rm_metric_owner_replace_catalog(target.owner, &target.catalog, target.work.ptr, target.work.len, 500, null));
        try expect(!target.owner.sources[0].suppressed);
        for (target.owner.rules[0..3]) |rule| try expect(!rule.suppressed);
        const resumed = try target.plan(&.{1}, 600);
        try eq(@as(u32, 1), resumed.count);
        try eq(@as(u64, 2), resumed.queries.capabilities.?[0]);
        try eq(@as(u32, 0), api.rm_metric_owner_abort_detach(target.owner, null));
    }
}

test "source-wide first alias attempt admits other result-group provenance without marking unrelated source" {
    var source = try GroupedFixture.init(false);
    defer source.deinit();
    var target = try GroupedFixture.init(false);
    defer target.deinit();
    var r: p.Round = undefined;
    // The requested normal metric already attempted. Only its unrequested
    // mixed-dependency alias needs a first attempt in the administrator group.
    try eq(@as(u32, 0), api.rm_metric_owner_plan(source.owner, &[_]u32{1}, 1, source.work.ptr, source.work.len, 7, &r, null));
    try eq(@as(u32, 0), api.rm_metric_owner_prepare(source.owner, r.token, null));
    try eq(@as(u32, 0), api.rm_metric_owner_commit(source.owner, r.token, 7, null));
    try expect(source.owner.metrics[1].attempted);
    try expect(!source.owner.metrics[0].attempted and !source.owner.metrics[2].attempted);
    try eq(@as(u32, 0), api.rm_metric_owner_plan(source.owner, &[_]u32{1}, 1, source.work.ptr, source.work.len, 0, &r, null));
    r.observations.status.?[0] = 3;
    r.observations.flags.?[0] = 32;
    try eq(@as(u32, 0), api.rm_metric_owner_prepare(source.owner, r.token, null));
    try expect(!source.owner.metrics[2].attempted);
    try eq(@as(u32, 0), api.rm_metric_owner_abort_detach(source.owner, null));
    try expect(!source.owner.metrics[2].attempted);
    try eq(@as(u32, 0), api.rm_metric_owner_plan(source.owner, &[_]u32{1}, 1, source.work.ptr, source.work.len, 0, &r, null));
    r.observations.status.?[0] = 3;
    r.observations.flags.?[0] = 32;
    try eq(@as(u32, 0), api.rm_metric_owner_prepare(source.owner, r.token, null));
    try eq(@as(u32, 2), source.owner.work.affected_count);
    try eq(@as(u32, 0), api.rm_metric_owner_commit(source.owner, r.token, 0, null));
    try expect(source.owner.metrics[1].attempted and source.owner.metrics[2].attempted);
    try expect(!source.owner.metrics[0].attempted);
    try expect(source.owner.sources[1].suppressed and !source.owner.sources[0].suppressed);
    const admin = try abiCopy(source.arena.allocator(), source.areas[1].region);
    const alias = source.owner.metrics[2];
    const untouched = source.owner.metrics[0];
    try eq(@as(u64, 1), try abiValue(u64, &admin, source.areas[1].result.provenance_position, 11 + alias.result_row * 4));
    try eq(@as(u64, 0), try abiValue(u64, &admin, source.areas[1].result.provenance_position, 11 + untouched.result_row * 4));
    try eq(@as(i64, 0), try abiValue(i64, &admin, source.areas[1].result.positions[6], alias.result_row));
    try eq(@as(u32, 5), try abiValue(u32, &admin, source.areas[1].result.positions[11], alias.result_row));
    const image = try source.image();
    const work = try target.arena.allocator().alignedAlloc(u8, .@"8", @intCast(cp.restoreWorkSize(target.owner, image.len)));
    var q: cp.Prepared = .{};
    try cp.prepare(target.owner, image, work, &q);
    try cp.publish(target.owner, &q, 100);
    try expect(target.owner.metrics[1].attempted and target.owner.metrics[2].attempted);
    try expect(!target.owner.metrics[0].attempted);
    try expect(target.owner.sources[1].suppressed and !target.owner.sources[0].suppressed);
    const restored = try abiCopy(target.arena.allocator(), target.areas[1].region);
    try eq(@as(u64, 1), try abiValue(u64, &restored, target.areas[1].result.provenance_position, 11 + target.owner.metrics[2].result_row * 4));
    try eq(@as(u64, 0), try abiValue(u64, &restored, target.areas[1].result.provenance_position, 11 + target.owner.metrics[0].result_row * 4));
    try expect(q.work == null and target.owner.work.region == null);
}

test "checkpoint strict corruption truncation duplicate closure mismatch and caps reject before public writes" {
    var source = try Fixture.init();
    defer source.deinit();
    var target = try Fixture.init();
    defer target.deinit();
    try fill(&source);
    const image = try source.image();
    const work = try target.restoreWork(image.len);
    var q: cp.Prepared = .{};
    const before = try target.arena.allocator().dupe(u8, target.region);
    const baseline = target.owner.rules[0].baseline;
    const round_generation = target.owner.round_generation;
    var length: usize = 0;
    while (length < image.len) : (length += 1) {
        cp.prepare(target.owner, image[0..length], work, &q) catch {};
        try expect(q.work == null);
    }
    const broken = try source.arena.allocator().dupe(u8, image);
    broken[120] ^= 1;
    try std.testing.expectError(error.Invalid, cp.prepare(target.owner, broken, work, &q));
    @memcpy(broken, image);
    // Duplicate the second rule's identity with the first's; recompute checksum
    // to exercise semantic validation rather than only the checksum guard.
    @memcpy(broken[64 + 40 + 224 + 8 ..][0..8], image[64 + 40 + 8 ..][0..8]);
    rehash(broken);
    try std.testing.expectError(error.Stale, cp.prepare(target.owner, broken, work, &q));
    @memcpy(broken, image);
    // Retained reference identity closure.
    std.mem.writeInt(u64, broken[64 + 40 + 3 * 224 + 16 + 7 * 8 ..][0..8], 99, .little);
    rehash(broken);
    try std.testing.expectError(error.Invalid, cp.prepare(target.owner, broken, work, &q));
    @memcpy(broken, image);
    std.mem.writeInt(u64, broken[64 + 16 ..][0..8], 99, .little);
    rehash(broken);
    try std.testing.expectError(error.Stale, cp.prepare(target.owner, broken, work, &q));
    @memcpy(broken, image);
    std.mem.writeInt(u32, broken[52..][0..4], 4, .little);
    rehash(broken);
    try std.testing.expectError(error.Capacity, cp.prepare(target.owner, broken, work, &q));
    @memcpy(broken, image);
    std.mem.writeInt(u64, broken[32..][0..8], std.math.maxInt(u64), .little);
    rehash(broken);
    try std.testing.expectError(error.Capacity, cp.prepare(target.owner, broken, work, &q));
    @memcpy(broken, image);
    std.mem.writeInt(u32, broken[64 + 40 + 216 ..][0..4], 65, .little);
    rehash(broken);
    try std.testing.expectError(error.Capacity, cp.prepare(target.owner, broken, work, &q));
    @memcpy(broken, image);
    std.mem.writeInt(u32, broken[8..][0..4], 0, .little);
    rehash(broken);
    try std.testing.expectError(error.Invalid, cp.prepare(target.owner, broken, work, &q));
    try std.testing.expectEqualSlices(u8, before, target.region);
    try eq(baseline, target.owner.rules[0].baseline);
    try eq(round_generation, target.owner.round_generation);
}
test "checkpoint revalidates prepared image, typed workspace/capacity and stale publication detach" {
    var source = try Fixture.init();
    defer source.deinit();
    var target = try Fixture.init();
    defer target.deinit();
    try fill(&source);
    const image = try source.image();
    const work = try target.restoreWork(image.len);
    var q: cp.Prepared = .{};
    const before = try target.arena.allocator().dupe(u8, target.region);
    try std.testing.expectError(error.Workspace, cp.prepare(target.owner, image, work[0..8], &q));
    try std.testing.expectError(error.Capacity, cp.encode(source.owner, image[0..8]));
    try cp.prepare(target.owner, image, work, &q);
    work[100] ^= 1;
    try std.testing.expectError(error.Invalid, cp.publish(target.owner, &q, 300));
    try expect(q.work == null);
    try std.testing.expectEqualSlices(u8, before, target.region);
    try cp.prepare(target.owner, image, work, &q);
    target.owner.token += 1;
    try std.testing.expectError(error.Stale, cp.publish(target.owner, &q, 300));
    try expect(q.work == null and target.owner.work.region == null);
}
test "support ABI sizes, null inputs, typed required capacity and owner cleanup" {
    try eq(@as(usize, 64), @sizeOf(h.Binding));
    try eq(@as(usize, 16), @sizeOf(h.Requirement));
    try eq(@as(usize, 24), @sizeOf(h.Sample));
    try eq(@as(usize, 56), @sizeOf(cp.Prepared));
    var failure: p.Failure = .{};
    try eq(@intFromEnum(p.Code.closed), exports.rm_metric_owner_checkpoint_size(null, null, null, &failure));
    var f = try Fixture.init();
    defer f.deinit();
    try fill(&f);
    var written: u64 = 999;
    const destination = try f.arena.allocator().alloc(u8, 8);
    try eq(@intFromEnum(p.Code.capacity_exceeded), exports.rm_metric_owner_checkpoint_encode(f.owner, destination.ptr, destination.len, &written, &failure));
    try expect(failure.required > failure.limit);
    try eq(@as(u64, 0), written);
    var q: cp.Prepared = .{};
    try eq(@intFromEnum(p.Code.invalid_contract), exports.rm_metric_owner_checkpoint_prepare(f.owner, null, 0, null, 0, &q, &failure));
    try expect(f.owner.work.region == null and q.work == null);
    q = .{ .work = f.work.ptr, .work_length = f.work.len };
    try eq(@intFromEnum(p.Code.closed), exports.rm_metric_owner_checkpoint_publish(null, &q, 0, &failure));
    try expect(q.work == null and q.work_length == 0);
}

const AccessArea = struct { region: []align(8) u8, raw: p.TableBinding, result: p.TableBinding, history: h.Binding };
fn accessArea(a: std.mem.Allocator, access: u32) !AccessArea {
    var positions: [39]ss.PositionSpec = undefined;
    var tables: [2]p.TableBinding = undefined;
    var offset: u32 = 0;
    var position: u32 = 0;
    for (&tables, 0..) |*table, ti| {
        table.* = .{ .region = null, .byte_length = 0, .rows_source = @intCast(ti * 4), .value_source = @intCast(ti * 4 + 1), .health_source = @intCast(ti * 4 + 2), .provenance_source = @intCast(ti * 4 + 3) };
        var slot: usize = 0;
        while (slot < (if (ti == 0) @as(usize, 16) else 12)) : (slot += 1) {
            offset = std.mem.alignForward(u32, offset, 8);
            table.positions[slot] = position;
            const width: u32 = if (ti == 1) (if (slot == 4 or slot == 5 or slot >= 9) 4 else 8) else if (slot == 9) 1 else if (slot == 3 or slot == 4 or slot == 10 or slot == 11 or slot == 13) 4 else 8;
            const source = if (slot == 0 or (ti == 0 and slot == 15)) table.rows_source else if (ti == 0 and slot >= 10 and slot <= 12) table.health_source else table.value_source;
            positions[position] = if (ti == 0 and slot == 9) ss.PositionSpec.stringTable(source, offset, 64) else ss.PositionSpec.column(source, offset, 3, width, if (slot == 0) ss.none else table.positions[0]);
            offset += positions[position].size;
            position += 1;
        }
        offset = std.mem.alignForward(u32, offset, 8);
        table.provenance_position = position;
        positions[position] = ss.PositionSpec.column(table.provenance_source, offset, 8 + 3 * @as(u32, if (ti == 0) 12 else 4), 8, ss.none);
        offset += positions[position].size;
        position += 1;
    }
    var history = h.Binding{ .region = null, .byte_length = 0, .rows_source = 8, .data_source = 9, .positions = undefined };
    for (&history.positions, 0..) |*pos, slot| {
        offset = std.mem.alignForward(u32, offset, 8);
        pos.* = position;
        const width: u32 = if (slot == 0 or slot == 1 or slot == 6 or slot == 7) 8 else 4;
        positions[position] = ss.PositionSpec.column(if (slot == 0) 8 else 9, offset, if (slot < 6) 3 else 16, width, if (slot >= 1 and slot <= 5) history.positions[0] else ss.none);
        offset += positions[position].size;
        position += 1;
    }
    try eq(@as(u32, 39), position);
    var identities: [10]ss.SourceIdentity = undefined;
    for (&identities, 0..) |*id, i| id.* = try ss.SourceIdentity.init("support-access", @intCast(i + 1), 0, 0);
    const region = try a.alignedAlloc(u8, .@"8", @intCast(ss.requiredSize(10, 39, offset)));
    try ss.initialize(region, access + 1, 10, 39, offset, &identities, &positions);
    for (&tables) |*table| {
        table.region = region.ptr;
        table.byte_length = region.len;
    }
    history.region = region.ptr;
    history.byte_length = region.len;
    return .{ .region = region, .raw = tables[0], .result = tables[1], .history = history };
}
const GroupedFixture = struct {
    arena: std.heap.ArenaAllocator,
    areas: [2]AccessArea,
    owner: *st.Owner,
    work: []align(8) u8,
    history_work: []align(8) u8,
    fn init(reverse_bindings: bool) !GroupedFixture {
        var arena = std.heap.ArenaAllocator.init(std.testing.allocator);
        errdefer arena.deinit();
        const a = arena.allocator();
        const areas = [2]AccessArea{ try accessArea(a, 0), try accessArea(a, 1) };
        const normal = p.SourceBinding{ .source_id = 100, .access_region = 0, .table = areas[0].raw };
        const admin = p.SourceBinding{ .source_id = 200, .access_region = 1, .table = areas[1].raw };
        const bindings = if (reverse_bindings) [2]p.SourceBinding{ admin, normal } else [2]p.SourceBinding{ normal, admin };
        const results = [2]p.TableBinding{ areas[0].result, areas[1].result };
        const config = p.Config{ .source_capacity = 2, .rule_capacity = 4, .metric_capacity = 3, .max_value_bytes = 64, .payload_work_bytes = 64, .active_limit = 1024 * 1024, .work_limit = 1024 * 1024 };
        var sizes: p.Sizes = undefined;
        try eq(@as(u32, 0), api.rm_metric_owner_required_sizes(&config, &sizes, null));
        const active = try a.alignedAlloc(u8, .@"8", @intCast(sizes.active));
        const work = try a.alignedAlloc(u8, .@"8", @intCast(@max(sizes.work, 65536)));
        var owner: ?*st.Owner = null;
        try eq(@as(u32, 0), api.rm_metric_owner_bind_grouped(&config, active.ptr, active.len, &bindings, 2, &results, 2, &owner, null));
        const source_ids = try a.dupe(u64, &.{ 200, 100 }); // catalog order deliberately differs from physical binding order
        const incarnations = try a.dupe(u64, &.{ 1, 1 });
        const caps = try a.dupe(u64, &.{ 1, 1 });
        const availability = try a.dupe(u32, &.{ 1, 1 });
        const rule_ids = try a.dupe(u64, &.{ 100, 101, 102, 103 });
        const rule_sources = try a.dupe(u64, &.{ 100, 200, 100, 200 });
        const rule_metrics = try a.dupe(u64, &.{ 1000, 1001, 1002, 1002 });
        const identities = try a.dupe(u64, &.{ 2000, 2001, 2002, 2003 });
        const kinds = try a.dupe(u32, &.{ 13, 13, 13, 13 });
        const flags = try a.dupe(u32, &.{ 0, 0, 0, 0 });
        const priorities = try a.dupe(u32, &.{ 1, 1, 1, 2 });
        const value_kinds = try a.dupe(u32, &.{ 3, 3, 3, 3 });
        const minima = try a.dupe(u64, &.{ 0, 0, 0, 0 });
        const maxima = try a.dupe(u64, &.{ std.math.maxInt(u64), std.math.maxInt(u64), std.math.maxInt(u64), std.math.maxInt(u64) });
        const masks = try a.dupe(u64, &.{ 1, 1, 1, 1 });
        const metric_ids = try a.dupe(u64, &.{ 1001, 1000, 1002 });
        const metric_identities = try a.dupe(u64, &.{ 3001, 3000, 3002 });
        const catalog = p.Catalog{ .sources = .{ .count = 2, .ids = source_ids.ptr, .incarnations = incarnations.ptr, .capabilities = caps.ptr, .availability = availability.ptr }, .rules = .{ .count = 4, .ids = rule_ids.ptr, .source_ids = rule_sources.ptr, .metric_ids = rule_metrics.ptr, .identities = identities.ptr, .kinds = kinds.ptr, .flags = flags.ptr, .priorities = priorities.ptr, .value_kinds = value_kinds.ptr, .minimum_bits = minima.ptr, .maximum_bits = maxima.ptr, .capability_masks = masks.ptr }, .metrics = .{ .count = 3, .ids = metric_ids.ptr, .identities = metric_identities.ptr } };
        try eq(@as(u32, 0), api.rm_metric_owner_replace_catalog(owner, &catalog, work.ptr, work.len, 100, null));
        return .{ .arena = arena, .areas = areas, .owner = owner.?, .work = work, .history_work = try a.alignedAlloc(u8, .@"8", @intCast(h.configureWorkSize(3, 16))) };
    }
    fn deinit(f: *GroupedFixture) void {
        _ = api.rm_metric_owner_unbind(f.owner, null);
        f.arena.deinit();
    }
    fn plan(f: *GroupedFixture, utc: i64, success: bool) !p.Round {
        var out: p.Round = undefined;
        try eq(@as(u32, 0), api.rm_metric_owner_plan(f.owner, &[_]u32{ 0, 1, 2 }, 3, f.work.ptr, f.work.len, utc, &out, null));
        if (success) {
            var i: u32 = 0;
            while (i < out.count) : (i += 1) current(out, i, 3, @as(u64, 900) + out.observations.rule_indices.?[i]);
        }
        try eq(@as(u32, 0), api.rm_metric_owner_prepare(f.owner, out.token, null));
        return out;
    }
    fn image(f: *GroupedFixture) ![]u8 {
        const bytes = try f.arena.allocator().alloc(u8, @intCast(try cp.encodedSize(f.owner)));
        _ = try cp.encode(f.owner, bytes);
        return bytes;
    }
};
fn abiCopy(a: std.mem.Allocator, region: []align(8) u8) !ss.Selection {
    var header: ss.Header = undefined;
    try eq(@as(u32, 0), ss.rm_shared_section_describe(region.ptr, region.len, &header));
    const sources = try a.alloc(u32, header.source_count);
    for (sources, 0..) |*source, index| source.* = @intCast(index);
    var size: ss.SelectionSize = undefined;
    try eq(@as(u32, 0), ss.rm_shared_section_selection_size(region.ptr, region.len, sources.ptr, header.source_count, &size));
    const data = try a.alloc(u8, @intCast(size.data_bytes));
    @memset(data, 0xa5);
    var copy: ss.Selection = .{ .data = data.ptr, .data_length = data.len, .held = (try a.alloc(ss.SourceCopy, header.source_count)).ptr, .held_length = header.source_count, .positions = (try a.alloc(ss.SelectedPosition, size.position_count)).ptr, .position_length = size.position_count };
    try eq(@as(u32, 0), ss.rm_shared_section_select(region.ptr, region.len, sources.ptr, header.source_count, &copy));
    var source: u32 = 0;
    while (source < header.source_count) : (source += 1) _ = ss.rm_shared_section_read(region.ptr, region.len, &copy, source, 1000);
    return copy;
}
fn abiValue(comptime T: type, copy: *const ss.Selection, position: u32, row: u32) !T {
    var value: ss.ValueView = .{};
    try eq(@intFromEnum(ss.PositionStatus.has_value), ss.rm_shared_section_position_value(copy, position, &value));
    try expect(row < value.length and @as(u64, row + 1) * @sizeOf(T) <= value.byte_length);
    const at: usize = @intCast(value.offset + @as(u64, row) * @sizeOf(T));
    return std.mem.readInt(T, copy.dataSlice()[at..][0..@sizeOf(T)], .little);
}

const engine0 = "gpu-engine/0000000000000005/0/0/0/busyPercent";
const engine1 = "gpu-engine/0000000000000005/0/1/0/busyPercent";

fn discoveryVectorArea(a: std.mem.Allocator, capacity: u32) !v.BoundVector {
    var specs: [21]ss.PositionSpec = undefined;
    var offset: u32 = 0;
    var binding = v.BoundVector{ .source_id = 10, .rows_source = 0, .value_source = 1, .health_source = 2 };
    for (&specs, 0..) |*spec, slot| {
        offset = std.mem.alignForward(u32, offset, 8);
        const width: u32 = switch (slot) {
            2, 3, 4, 6, 7, 10, 11, 17, 20 => 4,
            14 => 1,
            else => 8,
        };
        const rows = slot <= 4 or (slot >= 13 and slot <= 18);
        const health = (slot >= 10 and slot <= 12) or slot == 20;
        const source: u32 = if (rows) 0 else if (health) 2 else 1;
        binding.positions[slot] = @intCast(slot);
        spec.* = if (slot == 14) ss.PositionSpec.stringTable(source, offset, capacity * 144) else ss.PositionSpec.column(source, offset, capacity, width, if (slot == 0) ss.none else 0);
        offset += spec.size;
    }
    offset = std.mem.alignForward(u32, offset, 8);
    const region = try a.alignedAlloc(u8, .@"8", @intCast(ss.requiredSize(3, 21, offset)));
    const sources = [_]ss.SourceIdentity{ try ss.SourceIdentity.init("discovery-vector-rows", 1, 0, 0), try ss.SourceIdentity.init("discovery-vector-values", 2, 0, 0), try ss.SourceIdentity.init("discovery-vector-health", 3, 0, 0) };
    try ss.initialize(region, 1, 3, 21, offset, &sources, &specs);
    binding.region = region.ptr;
    binding.byte_length = region.len;
    return binding;
}
test "discovery GPU sizing is owner-free widened finite-cap admission with exact failure diagnostics" {
    var arena = std.heap.ArenaAllocator.init(std.testing.allocator);
    defer arena.deinit();
    const binding = try discoveryVectorArea(arena.allocator(), 3); // NO Owner acquired
    var failure: p.Failure = .{};
    const size = try v.discoveryGpuWorkSizeChecked(binding, 2, 3, 4, std.math.maxInt(u64), &failure);
    try eq(p.Code.ok, failure.code);
    try expect(size > try v.discoveryGpuWorkSize(binding, 2, 0, 4, std.math.maxInt(u64)));
    try std.testing.expectError(error.Capacity, v.discoveryGpuWorkSizeChecked(binding, 2, 4, 4, std.math.maxInt(u64), &failure));
    try eq(p.Family.rules, failure.family);
    try eq(@as(u64, 4), failure.required);
    try eq(@as(u64, 3), failure.limit);
    try std.testing.expectError(error.Capacity, v.discoveryGpuWorkSizeChecked(binding, 2, 3, 4, size - 1, &failure));
    try eq(p.Family.work, failure.family);
    try eq(size, failure.required);
    try eq(size - 1, failure.limit);
    const huge = try v.discoveryGpuWorkSize(binding, std.math.maxInt(u32), 3, 4, std.math.maxInt(u64));
    try expect(huge > std.math.maxInt(u32)); // addition/product never wrap u32
    var bad = binding;
    bad.positions[1] = bad.positions[0];
    try std.testing.expectError(error.Invalid, v.discoveryGpuWorkSize(bad, 1, 3, 4, size));
    const region: []align(8) u8 = @alignCast(binding.region.?[0..@intCast(binding.byte_length)]);
    var held = try ss.Writer.begin(region, binding.value_source);
    try std.testing.expectError(error.Shared, v.discoveryGpuWorkSizeChecked(binding, 1, 3, 4, size, &failure));
    try eq(p.Code.shared_section, failure.code);
    try held.end(.has_value, 0);
    var f = try Fixture.initMode(true);
    defer f.deinit();
    const admitted = try v.discoveryGpuWorkSize(f.vector_binding.?, 2, 3, 3, f.owner.config.work_limit);
    var current_size: u64 = 0;
    try eq(@as(u32, 0), v.rm_metric_vector_gpu_work_size(f.owner, 2, &current_size, null));
    try expect(current_size <= admitted);
    const round = try vectorHistoryPrepare(&f, .{ .flags = 1 }, 1, 2, 1);
    try vectorHistoryCommit(&f, round, 1);
    try eq(admitted, try v.discoveryGpuWorkSize(f.vector_binding.?, 2, 3, 3, f.owner.config.work_limit));
    try eq(@as(u32, 0), v.rm_metric_vector_gpu_work_size(f.owner, 2, &current_size, null));
    try expect(current_size <= admitted);
    try std.testing.expectError(error.Capacity, v.discoveryGpuWorkSizeChecked(f.vector_binding.?, 2, 1, 3, f.owner.config.work_limit, &failure));
    try eq(@as(u64, 2), failure.required);
    try eq(@as(u64, 1), failure.limit);
}
test "discovery NPU worst-count bound shares exact native layout and rejects count overflow" {
    try eq(@as(u64, 4), try v.discoveryNpuWorkSize(0, 0, 0));
    try std.testing.expectError(error.Capacity, v.discoveryNpuWorkSize(std.math.maxInt(u32), std.math.maxInt(u32), 1));
    var f = try Fixture.initMode(true);
    defer f.deinit();
    const facts = HistoryVectorFacts.init(&f);
    var exact: u64 = 0;
    const requests = [_]u32{ 0, 1, 2 };
    try eq(@as(u32, 0), v.rm_metric_vector_npu_size(f.owner, &facts.bindings, &facts.topology, &requests, 3, 10, &exact, null));
    try expect(exact <= try v.discoveryNpuWorkSize(3, 3, 1));
    var plan: v.NpuPlan = .{};
    try eq(@as(u32, 0), v.rm_metric_vector_npu_expand(f.owner, &facts.bindings, &facts.topology, &requests, 3, 10, f.history_work.ptr, exact, &plan, null));
    try eq(@as(u32, 3), plan.metric_count);
    try expect(f.owner.work.region == null);
}
test "discovery cold GPU preflight precedes first catalog and shares actual bind checks without mutation" {
    var f = try Fixture.initMode(true);
    defer f.deinit();
    const config = f.owner.config;
    const raw = f.owner.bindings[0].table;
    const result = f.owner.results[0];
    const binding = f.vector_binding.?;
    try eq(@as(u32, 0), api.rm_metric_owner_unbind(f.owner, null));
    var fresh: ?*st.Owner = null;
    try eq(@as(u32, 0), api.rm_metric_owner_bind(&config, f.active.ptr, f.active.len, (&raw)[0..1].ptr, 1, &result, &fresh, null));
    f.owner = fresh.?; // real cold owner, not a synthetic sizing stand-in
    try eq(@as(u32, 0), f.owner.source_count);
    const before = try abiCopy(f.arena.allocator(), f.region);
    const failure = f.owner.failure;
    try v.discoveryValidateGpuBinding(f.owner, binding, 10);
    try expect(f.owner.gpu_vector == null and f.owner.work.region == null);
    try eq(failure, f.owner.failure);
    try std.testing.expectError(error.Invalid, v.discoveryValidateGpuBinding(f.owner, binding, 11));
    var wrong = binding;
    wrong.byte_length -= 8;
    try std.testing.expectError(error.Invalid, v.discoveryValidateGpuBinding(f.owner, wrong, 10));
    wrong = binding;
    wrong.value_source = raw.value_source;
    try std.testing.expectError(error.Invalid, v.discoveryValidateGpuBinding(f.owner, wrong, 10));
    const after = try abiCopy(f.arena.allocator(), f.region);
    try expect(std.mem.eql(u8, before.dataSlice(), after.dataSlice()));
    try eq(@as(u32, 0), api.rm_metric_owner_replace_catalog(f.owner, &f.catalog, f.work.ptr, f.work.len, 0, null));
    try eq(@as(u32, 0), v.rm_metric_vector_gpu_bind(f.owner, &binding, null));
    try std.testing.expectError(error.Invalid, v.discoveryValidateGpuBinding(f.owner, binding, 10)); // never promise a second install
}
test "discovery request projection reads native selectors and suppresses base IDs even with no vector rows" {
    var f = try Fixture.initMode(true);
    defer f.deinit();
    var facts = HistoryVectorFacts.init(&f);
    const refs = [_]v.Ref{.{ .length = engine1.len }};
    const request = v.Request{ .count = 1, .addresses = &refs, .text = .{ .bytes = engine1.ptr, .length = engine1.len } };
    var projection = try v.projectRequest(f.owner, facts.bindings, facts.topology, request, f.history_work);
    try eq(@as(u32, 0), projection.selected_count);
    try eq(@as(u32, 1), projection.base_count);
    try eq(@as(u32, 1), projection.suppressed_count);
    try eq(@as(u32, 0), projection.base_metric_indices.?[0]);
    try eq(@as(u64, 10), projection.base_metric_ids.?[0]);
    try expect(projection.base_metric_ids == projection.suppressed_metric_ids);
    const round = try vectorHistoryPrepare(&f, .{ .flags = 1 }, 1, 2, 1);
    try vectorHistoryCommit(&f, round, 1);
    const before = try abiCopy(f.arena.allocator(), f.region);
    const token = f.owner.token;
    const counters = f.owner.counters;
    const failure = f.owner.failure;
    projection = try v.projectRequest(f.owner, facts.bindings, facts.topology, request, f.history_work);
    try eq(@as(u32, 1), projection.selected_count);
    try eq(@as(u32, 1), projection.selected_rows.?[0]);
    try eq(try abiValue(u64, &before, f.vector_binding.?.positions[0], 1), projection.expected_identities.?[0]);
    try eq(before.heldSlice()[f.vector_binding.?.rows_source].sequence, projection.membership_revision);
    try eq(f.owner.catalog_generation, projection.catalog_generation);
    try eq(f.vector_binding.?.region, projection.binding.region);
    const revision = projection.selection_revision;
    const membership = projection.membership_revision;
    const uppercase = "GPU-ENGINE/0000000000000005/0/1/0/BUSYPERCENT";
    const case_request = v.Request{ .count = 1, .addresses = &refs, .text = .{ .bytes = uppercase, .length = uppercase.len } };
    try eq(@as(u32, 1), (try v.projectRequest(f.owner, facts.bindings, facts.topology, case_request, f.history_work)).selected_count);
    const unknown = "gpu-engine/0000000000000006/0/1/0/busyPercent";
    const unknown_request = v.Request{ .count = 1, .addresses = &refs, .text = .{ .bytes = unknown, .length = unknown.len } };
    try eq(@as(u32, 0), (try v.projectRequest(f.owner, facts.bindings, facts.topology, unknown_request, f.history_work)).selected_count);
    const trigger = v.Request{ .count = 1, .addresses = &.{.{ .length = 7 }}, .text = .{ .bytes = "trigger", .length = 7 } };
    try eq(@as(u32, 2), (try v.projectRequest(f.owner, facts.bindings, facts.topology, trigger, f.history_work)).selected_count);
    try eq(@as(u32, 2), (try v.projectRequest(f.owner, facts.bindings, facts.topology, .{ .flags = 1 }, f.history_work)).selected_count);
    try eq(@as(u32, 2), (try v.projectRequest(f.owner, facts.bindings, facts.topology, .{ .flags = 2 }, f.history_work)).selected_count);
    facts.topology.generations = &.{2}; // stale catalog identity is filtered
    try eq(@as(u32, 0), (try v.projectRequest(f.owner, facts.bindings, facts.topology, request, f.history_work)).selected_count);
    facts.topology.generations = &.{1};
    try eq(token, f.owner.token);
    try eq(counters, f.owner.counters);
    try eq(failure, f.owner.failure);
    const after = try abiCopy(f.arena.allocator(), f.region);
    try expect(std.mem.eql(u8, before.dataSlice(), after.dataSlice()));
    const numeric = try vectorHistoryPrepare(&f, .{ .flags = 1 }, 1, 2, 2);
    try vectorHistoryCommit(&f, numeric, 2);
    projection = try v.projectRequest(f.owner, facts.bindings, facts.topology, request, f.history_work);
    try eq(revision, projection.selection_revision);
    try eq(membership, projection.membership_revision);
    const catalog = try vectorHistoryPrepare(&f, .{ .flags = 2 }, 1, 1, 3);
    try vectorHistoryCommit(&f, catalog, 3);
    projection = try v.projectRequest(f.owner, facts.bindings, facts.topology, request, f.history_work);
    try eq(@as(u32, 0), projection.selected_count);
    try eq(@as(u32, 1), projection.suppressed_count);
    try expect(membership != projection.membership_revision);
}
test "discovery request projection rejects malformed or overlapping work before output and preserves held round" {
    var f = try Fixture.initMode(true);
    defer f.deinit();
    const round = try vectorHistoryPrepare(&f, .{ .flags = 1 }, 1, 2, 1);
    try vectorHistoryCommit(&f, round, 1);
    const facts = HistoryVectorFacts.init(&f);
    @memset(f.history_work, 0xcc);
    const size = try v.requestProjectionSize(f.owner);
    try std.testing.expectError(error.Work, v.projectRequest(f.owner, facts.bindings, facts.topology, .{ .flags = 1 }, f.history_work[0..@intCast(size - 1)]));
    for (f.history_work) |byte| try eq(@as(u8, 0xcc), byte);
    const bad = v.Request{ .count = 1, .addresses = &.{.{ .length = 1 }} };
    try std.testing.expectError(error.Invalid, v.projectRequest(f.owner, facts.bindings, facts.topology, bad, f.history_work));
    for (f.history_work) |byte| try eq(@as(u8, 0xcc), byte);
    const before = try abiCopy(f.arena.allocator(), f.region);
    try std.testing.expectError(error.Invalid, v.projectRequest(f.owner, facts.bindings, facts.topology, .{ .flags = 1 }, f.region));
    const pending = try f.plan(&.{0}, 2);
    try std.testing.expectError(error.Invalid, v.projectRequest(f.owner, facts.bindings, facts.topology, .{ .flags = 1 }, f.work));
    try eq(pending.token, f.owner.token);
    try expect(f.owner.work.region != null);
    const after = try abiCopy(f.arena.allocator(), f.region);
    try expect(std.mem.eql(u8, before.dataSlice(), after.dataSlice()));
    try eq(@as(u32, 0), api.rm_metric_owner_abort_detach(f.owner, null));
}
const HistoryVectorFacts = struct {
    bindings: v.Bindings,
    topology: v.ScopeTopology,
    fn init(f: *Fixture) HistoryVectorFacts {
        const strings = "devtrigger";
        return .{
            .bindings = .{ .count = 3, .catalog_generation = f.owner.catalog_generation, .field_codes = &.{ @import("field_codes.zig").gpu_engine_busy, 0, 0 }, .scope_indices = &.{ 0, p.none, p.none }, .metric_indices = &.{ 0, 1, 2 }, .scope_ids = &.{ 100, 0, 0 }, .addresses = &.{ .{ .offset = 3, .length = 7 }, .{}, .{} }, .device_keys = &.{ .{ .length = 3 }, .{}, .{} }, .text = .{ .bytes = strings.ptr, .length = strings.len } },
            .topology = .{ .count = 1, .text = .{ .bytes = strings.ptr, .length = strings.len }, .keys = &.{.{ .length = 3 }}, .parents = &.{.{}}, .pnp = &.{.{}}, .pnp_drivers = &.{.{}}, .directory_low = &.{0}, .directory_high = &.{0}, .luids = &.{5}, .drivers = &.{0}, .generations = &.{1}, .vendors = &.{0}, .devices = &.{0}, .engine_indices = &.{0}, .engine_counts = &.{2}, .driver_present = &.{0}, .flags = &.{1}, .expansions = &.{4} },
        };
    }
};
fn vectorHistoryPrepare(f: *Fixture, request: v.Request, status: u32, count: u32, utc: i64) !p.Round {
    return vectorHistoryPrepareFlags(f, request, status, count, utc, 0);
}
fn vectorHistoryPrepareFlags(f: *Fixture, request: v.Request, status: u32, count: u32, utc: i64, flags: u32) !p.Round {
    const r = try f.plan(&.{0}, utc);
    if (status == 1) current(r, 0, 3, 77);
    if (flags & 32 != 0) r.observations.status.?[0] = 3;
    r.observations.flags.?[0] = flags;
    try eq(@as(u32, 0), api.rm_metric_owner_prepare(f.owner, r.token, null));
    const facts = HistoryVectorFacts.init(f);
    const input = v.GpuInput{ .count = count, .source_id = 10, .incarnation = 1, .capability = 1, .observation = r.token, .observed_ms = @max(utc, 1), .status = status, .luids = &.{ 5, 5 }, .physical = &.{ 0, 0 }, .engines = &.{ 0, 1 }, .classes = &.{ 0, 0 }, .usage = &.{ 22.125, 44.25 } };
    var publication: ?*const v.GpuPublication = null;
    try eq(@as(u32, 0), v.rm_metric_vector_gpu_prepare(f.owner, r.token, &facts.bindings, &facts.topology, &request, &input, f.history_work.ptr, 16384, &publication, null));
    try expect(publication != null and f.owner.work.region != null);
    return r;
}
fn vectorHistoryCommit(f: *Fixture, r: p.Round, utc: i64) !void {
    const binding = f.mixed.?;
    try eq(@as(u32, 0), exports.rm_metric_owner_history_commit_mixed(f.owner, null, 0, (&binding)[0..1].ptr, 1, &[_]u32{0}, 1, r.token, utc, null));
    try expect(f.owner.work.region == null and f.owner.work.gpu_pending == null);
}
fn vectorHistoryLocation(f: *Fixture, name: []const u8) !exports.GpuHistoryLocation {
    const text = v.Text{ .bytes = name.ptr, .length = @intCast(name.len) };
    var location: exports.GpuHistoryLocation = .{};
    try eq(@as(u32, 0), exports.rm_metric_owner_history_gpu_location(f.owner, &text, .{ .length = @intCast(name.len) }, &location, null));
    const copy = try abiCopy(f.arena.allocator(), f.region);
    // Actual SS C ABI copy/column metadata; no direct raw-pointer success read.
    try eq(copy.positionSlice()[f.vector_binding.?.positions[13]].entry.identity_sequence, location.column_identity_sequence);
    try eq(location.identity, try abiValue(u64, &copy, f.vector_binding.?.positions[0], location.row));
    return location;
}
fn mixedRequirements(f: *Fixture) ![3]h.MixedRequirement {
    return .{ .{ .kind = 1, .id = 10, .rounds = 2 }, .{ .kind = 2, .id = 10, .rounds = 2, .identity = (try vectorHistoryLocation(f, engine0)).identity, .address = .{ .length = engine0.len } }, .{ .kind = 2, .id = 10, .rounds = 2, .identity = (try vectorHistoryLocation(f, engine1)).identity, .address = .{ .offset = engine0.len, .length = engine1.len } } };
}
fn configureMixedFixture(f: *Fixture, reqs: []const h.MixedRequirement) !void {
    const names = engine0 ++ engine1;
    const text = v.Text{ .bytes = names.ptr, .length = names.len };
    try eq(@as(u32, 0), exports.rm_metric_owner_history_configure_mixed(f.owner, &f.mixed.?, reqs.ptr, @intCast(reqs.len), &text, f.history_work.ptr, f.history_work.len, 0, null));
}
test "mixed requested history uses typed exact addresses, native one-of-two selection, UTC0 and null retained failures" {
    var f = try Fixture.initMode(true);
    defer f.deinit();
    // Catalog-only rows have no completed attempt, even at UTC0.
    var r = try vectorHistoryPrepare(&f, .{ .flags = 2 }, 1, 2, 0);
    try vectorHistoryCommit(&f, r, 0);
    var reqs = try mixedRequirements(&f);
    try configureMixedFixture(&f, &reqs);
    var t = try h.MixedTable.init(f.mixed.?);
    try eq(@as(u32, 0), t.base.load(u32, 4, 1));
    try eq(@as(u32, 0), t.base.load(u32, 4, 2));
    const layout = try ss.layout.Layout.load(f.region);
    const seq = layout.source(f.region, f.mixed.?.base.rows_source).sequence;
    const refs = [_]v.Ref{.{ .length = engine0.len }};
    const selected = v.Request{ .count = 1, .addresses = &refs, .text = .{ .bytes = engine0.ptr, .length = engine0.len } };
    r = try vectorHistoryPrepare(&f, selected, 1, 2, 0);
    try eq(@as(u32, 1), f.owner.work.gpu_pending.?.view.selected_count);
    try vectorHistoryCommit(&f, r, 0);
    t = try h.MixedTable.init(f.mixed.?);
    try eq(seq, layout.source(f.region, f.mixed.?.base.rows_source).sequence);
    try eq(@as(u32, 1), t.base.load(u32, 4, 1));
    try eq(@as(u32, 0), t.base.load(u32, 4, 2));
    try eq(@as(i64, 0), t.base.sample(1, 0).time);
    try eq(@as(u64, @bitCast(@as(f64, 22.125))), t.base.sample(1, 0).bits);
    try eq(@as(u32, 1), t.base.sample(1, 0).present);
    const good = try vectorHistoryLocation(&f, engine0);
    // Reconfigure merges duplicate depth without losing the UTC0 seed.
    reqs[1].rounds = 3;
    const duplicates = [_]h.MixedRequirement{ reqs[0], reqs[1], reqs[1], reqs[2] };
    try configureMixedFixture(&f, &duplicates);
    r = try vectorHistoryPrepare(&f, selected, 3, 0, 1);
    try vectorHistoryCommit(&f, r, 1);
    t = try h.MixedTable.init(f.mixed.?);
    try eq(@as(u32, 2), t.base.load(u32, 4, 1));
    try eq(@as(u32, 0), t.base.sample(1, 1).present);
    try eq(@as(u64, 0), t.base.sample(1, 1).bits);
    const retained = try vectorHistoryLocation(&f, engine0);
    try eq(@as(u32, 2), retained.public_status);
    try eq(good.identity, retained.identity);
    // Ring wrap and shrink retain newest nulls, not a stale good number.
    r = try vectorHistoryPrepare(&f, selected, 4, 0, 2);
    try vectorHistoryCommit(&f, r, 2);
    r = try vectorHistoryPrepare(&f, selected, 3, 0, 3);
    try vectorHistoryCommit(&f, r, 3);
    reqs[1].rounds = 1;
    try configureMixedFixture(&f, reqs[1..2]);
    t = try h.MixedTable.init(f.mixed.?);
    try eq(@as(u32, 1), t.base.rows);
    try eq(@as(i64, 3), t.base.sample(0, 0).time);
    try eq(@as(u32, 0), t.base.sample(0, 0).present);
    try configureMixedFixture(&f, &.{});
    try eq(@as(u32, 0), (try h.MixedTable.init(f.mixed.?)).base.rows);
}
test "mixed history preadmits every writer and rejected or aborted rounds publish no kernel vector or ring values" {
    var f = try Fixture.initMode(true);
    defer f.deinit();
    var r = try vectorHistoryPrepare(&f, .{ .flags = 1 }, 1, 2, 1);
    try vectorHistoryCommit(&f, r, 1);
    const reqs = try mixedRequirements(&f);
    try configureMixedFixture(&f, &reqs);
    const before = try abiCopy(f.arena.allocator(), f.region);
    // A denied permission calls abort, never a history append.
    r = try vectorHistoryPrepare(&f, .{ .flags = 1 }, 1, 2, 2);
    try eq(@as(u32, 0), api.rm_metric_owner_abort_detach(f.owner, null));
    try expect(f.owner.work.region == null and f.owner.work.gpu_pending == null);
    var after = try abiCopy(f.arena.allocator(), f.region);
    try expect(std.mem.eql(u8, before.dataSlice(), after.dataSlice()));
    // Busy history admission rejects before kernel OR vector stores.
    r = try vectorHistoryPrepare(&f, .{ .flags = 1 }, 1, 2, 3);
    var held = try ss.Writer.begin(f.region, f.mixed.?.base.data_source);
    const binding = f.mixed.?;
    try eq(@intFromEnum(p.Code.shared_section), exports.rm_metric_owner_history_commit_mixed(f.owner, null, 0, (&binding)[0..1].ptr, 1, &[_]u32{0}, 1, r.token, 3, null));
    try held.end(.has_value, 1);
    after = try abiCopy(f.arena.allocator(), f.region);
    try expect(std.mem.eql(u8, before.dataSlice(), after.dataSlice()));
    try expect(f.owner.work.region == null and f.owner.work.gpu_pending == null);
    // History opens first; a later kernel/vector writer failure still stores no bytes.
    r = try vectorHistoryPrepare(&f, .{ .flags = 1 }, 1, 2, 4);
    held = try ss.Writer.begin(f.region, f.vector_binding.?.health_source);
    try eq(@intFromEnum(p.Code.shared_section), exports.rm_metric_owner_history_commit_mixed(f.owner, null, 0, (&binding)[0..1].ptr, 1, &[_]u32{0}, 1, r.token, 4, null));
    try held.end(.has_value, 1);
    after = try abiCopy(f.arena.allocator(), f.region);
    try expect(std.mem.eql(u8, before.dataSlice(), after.dataSlice()));
}
test "mixed history retires missing vector keys and explicit reconfigure is required before an identical address can append" {
    var f = try Fixture.initMode(true);
    defer f.deinit();
    var r = try vectorHistoryPrepare(&f, .{ .flags = 1 }, 1, 2, 1);
    try vectorHistoryCommit(&f, r, 1);
    const reqs = try mixedRequirements(&f);
    try configureMixedFixture(&f, &reqs);
    r = try vectorHistoryPrepare(&f, .{ .flags = 2 }, 1, 1, 2);
    try vectorHistoryCommit(&f, r, 2);
    const copy = try abiCopy(f.arena.allocator(), f.region);
    try eq(@as(u32, 0), try abiValue(u32, &copy, f.mixed.?.live_position, 2));
    var t = try h.MixedTable.init(f.mixed.?);
    try eq(@as(u32, 1), t.base.load(u32, 4, 2)); // seed only, probe never appended
    r = try vectorHistoryPrepare(&f, .{ .flags = 1 }, 1, 2, 3);
    try vectorHistoryCommit(&f, r, 3);
    t = try h.MixedTable.init(f.mixed.?);
    try eq(@as(u32, 1), t.base.load(u32, 4, 2)); // old key remains retired
    try configureMixedFixture(&f, &reqs);
    r = try vectorHistoryPrepare(&f, .{ .flags = 1 }, 1, 2, 4);
    try vectorHistoryCommit(&f, r, 4);
    try eq(@as(u32, 2), (try h.MixedTable.init(f.mixed.?)).base.load(u32, 4, 2));
    r = try vectorHistoryPrepareFlags(&f, .{ .flags = 1 }, 2, 0, 5, 8);
    try vectorHistoryCommit(&f, r, 5);
    const invalidated = try abiCopy(f.arena.allocator(), f.region);
    try eq(@as(u32, 0), invalidated.positionSlice()[f.vector_binding.?.positions[0]].entry.length);
    try eq(@as(u32, 0), try abiValue(u32, &invalidated, f.mixed.?.live_position, 1));
    try eq(@as(u32, 0), try abiValue(u32, &invalidated, f.mixed.?.live_position, 2));
}
test "GPU row hash equal to a real kernel metric ID remains an explicit distinct history kind" {
    var f = try Fixture.initMode(true);
    defer f.deinit();
    var r = try vectorHistoryPrepare(&f, .{ .flags = 1 }, 1, 2, 1);
    try vectorHistoryCommit(&f, r, 1);
    const gpu = try vectorHistoryLocation(&f, engine0);
    @constCast(f.catalog.metrics.ids.?)[0] = gpu.identity;
    @constCast(f.catalog.rules.metric_ids.?)[0] = gpu.identity;
    try eq(@as(u32, 0), api.rm_metric_owner_replace_catalog(f.owner, &f.catalog, f.work.ptr, f.work.len, 2, null));
    const reqs = [_]h.MixedRequirement{ .{ .kind = 1, .id = gpu.identity, .rounds = 2 }, .{ .kind = 2, .id = 10, .identity = gpu.identity, .rounds = 2, .address = .{ .length = engine0.len } } };
    try configureMixedFixture(&f, &reqs);
    r = try vectorHistoryPrepare(&f, .{ .flags = 1 }, 1, 2, 3);
    try vectorHistoryCommit(&f, r, 3);
    const t = try h.MixedTable.init(f.mixed.?);
    try eq(@as(u32, 2), t.base.rows);
    try eq(@as(u64, @bitCast(@as(f64, 77))), t.base.sample(0, t.base.load(u32, 4, 0) - 1).bits);
    try eq(@as(u64, @bitCast(@as(f64, 22.125))), t.base.sample(1, t.base.load(u32, 4, 1) - 1).bits);
}
test "newly prepared engine history receives its FIRST numeric publication in the same held workspace" {
    var f = try Fixture.initMode(true);
    defer f.deinit();
    var r = try vectorHistoryPrepare(&f, .{ .flags = 1 }, 1, 2, 0);
    const text = v.Text{ .bytes = engine0.ptr, .length = engine0.len };
    var definition: exports.GpuHistoryLocation = .{};
    try eq(@as(u32, 0), exports.rm_metric_owner_history_gpu_prepared_location(f.owner, r.token, &text, .{ .length = engine0.len }, &definition, null));
    try eq(@as(u64, 0), definition.column_identity_sequence);
    try eq(@as(u32, 0), definition.public_status);
    try eq(@as(u64, 0), definition.numeric_bits);
    try expect(definition.identity != 0 and f.owner.work.region != null);
    const reqs = [_]h.MixedRequirement{ .{ .kind = 1, .rounds = 2, .id = 10 }, .{ .kind = 2, .rounds = 2, .id = definition.source_id, .identity = definition.identity, .address = .{ .length = engine0.len } } };
    const scratch: []align(8) u8 = @alignCast(f.history_work[16384..]);
    try eq(@as(u32, 0), exports.rm_metric_owner_history_configure_mixed(f.owner, &f.mixed.?, &reqs, 2, &text, scratch.ptr, scratch.len, 0, null));
    try expect(f.owner.work.region != null and f.owner.work.gpu_pending != null);
    var table = try h.MixedTable.init(f.mixed.?);
    try eq(@as(u32, 0), table.base.load(u32, 4, 1));
    try eq(@as(u32, 0), api.rm_metric_owner_abort_detach(f.owner, null));
    try eq(@as(u32, 0), (try h.MixedTable.init(f.mixed.?)).base.load(u32, 4, 1));
    r = try vectorHistoryPrepare(&f, .{ .flags = 1 }, 1, 2, 0);
    try vectorHistoryCommit(&f, r, 0);
    table = try h.MixedTable.init(f.mixed.?);
    try eq(@as(u32, 1), table.base.load(u32, 4, 1));
    try eq(@as(i64, 0), table.base.sample(1, 0).time);
    try eq(@as(u64, @bitCast(@as(f64, 22.125))), table.base.sample(1, 0).bits);
    const published = try vectorHistoryLocation(&f, engine0);
    try eq(definition.identity, published.identity);
    try expect(published.column_identity_sequence != 0);
}
test "whole-source UnsupportedSource32 retains vector good but appends null only for native selected engine histories" {
    var f = try Fixture.initMode(true);
    defer f.deinit();
    var r = try vectorHistoryPrepare(&f, .{ .flags = 1 }, 1, 2, 1);
    try vectorHistoryCommit(&f, r, 1);
    const reqs = try mixedRequirements(&f);
    try configureMixedFixture(&f, &reqs);
    const refs = [_]v.Ref{.{ .length = engine0.len }};
    const request = v.Request{ .count = 1, .addresses = &refs, .text = .{ .bytes = engine0.ptr, .length = engine0.len } };
    r = try vectorHistoryPrepareFlags(&f, request, 4, 0, 2, 32);
    try vectorHistoryCommit(&f, r, 2);
    const t = try h.MixedTable.init(f.mixed.?);
    try eq(@as(u32, 2), t.base.load(u32, 4, 1));
    try eq(@as(u32, 0), t.base.sample(1, 1).present);
    try eq(@as(u32, 1), t.base.load(u32, 4, 2));
    const copy = try abiCopy(f.arena.allocator(), f.region);
    for (0..2) |row| {
        try eq(@as(u32, 2), try abiValue(u32, &copy, f.vector_binding.?.positions[20], @intCast(row)));
        try eq(@as(u32, 1), try abiValue(u32, &copy, f.vector_binding.?.positions[7], @intCast(row)));
    }
    try expect(f.owner.sources[0].suppressed and f.owner.work.region == null);
}
test "mixed history size identity class and legacy Source32 images reject before mutation with typed caps" {
    var f = try Fixture.initMode(true);
    defer f.deinit();
    const r = try vectorHistoryPrepare(&f, .{ .flags = 1 }, 1, 2, 1);
    try vectorHistoryCommit(&f, r, 1);
    var reqs = try mixedRequirements(&f);
    try configureMixedFixture(&f, &reqs);
    const before = try abiCopy(f.arena.allocator(), f.region);
    reqs[1].rounds = 100;
    const names = engine0 ++ engine1;
    const text = v.Text{ .bytes = names.ptr, .length = names.len };
    var failure: p.Failure = .{};
    try eq(@intFromEnum(p.Code.capacity_exceeded), exports.rm_metric_owner_history_configure_mixed(f.owner, &f.mixed.?, &reqs, 3, &text, f.history_work.ptr, f.history_work.len, 2, &failure));
    try eq(@as(u64, 104), failure.required);
    try eq(@as(u64, 16), failure.limit);
    reqs[1].rounds = 2;
    try eq(@intFromEnum(p.Code.workspace_unavailable), exports.rm_metric_owner_history_configure_mixed(f.owner, &f.mixed.?, &reqs, 3, &text, f.history_work.ptr, 8, 2, &failure));
    try expect(failure.required > 8);
    try eq(@as(u64, 8), failure.limit);
    reqs[1].identity +%= 1;
    try eq(@intFromEnum(p.Code.stale_identity), exports.rm_metric_owner_history_configure_mixed(f.owner, &f.mixed.?, &reqs, 3, &text, f.history_work.ptr, f.history_work.len, 2, null));
    var wrong = f.mixed.?;
    wrong.version = 0;
    try std.testing.expectError(error.Invalid, h.configureMixed(f.owner, wrong, &reqs, text, f.history_work, 2));
    wrong = f.mixed.?;
    wrong.base.region = f.history_region.ptr; // wrong creator mapping/class
    wrong.base.byte_length = f.history_region.len;
    try std.testing.expectError(error.Invalid, h.configureMixed(f.owner, wrong, &reqs, text, f.history_work, 2));
    const image = try f.image();
    const old = try f.arena.allocator().alloc(u8, image.len - 8);
    @memcpy(old[0..96], image[0..96]);
    @memcpy(old[96..], image[104..]);
    std.mem.writeInt(u64, old[16..24], old.len, .little);
    std.mem.writeInt(u32, old[60..64], 0, .little);
    rehash(old);
    const scratch = try f.restoreWork(old.len);
    var prepared: cp.Prepared = .{};
    try std.testing.expectError(error.Invalid, cp.prepare(f.owner, old, scratch, &prepared));
    try expect(prepared.work == null);
    const after = try abiCopy(f.arena.allocator(), f.region);
    try expect(std.mem.eql(u8, before.dataSlice(), after.dataSlice()));
    try expect(f.owner.work.region == null and f.owner.work.gpu_pending == null);
}
test "grouped history keeps global metric IDs and stable origin ordinals across normal and administrator regions" {
    var f = try GroupedFixture.init(false);
    defer f.deinit();
    try h.configure(f.owner, f.areas[0].history, &.{.{ .metric_id = 1000, .rounds = 2 }}, f.history_work, 100);
    try h.configure(f.owner, f.areas[1].history, &.{ .{ .metric_id = 1001, .rounds = 2 }, .{ .metric_id = 1002, .rounds = 2 } }, f.history_work, 100);
    const bindings = [2]h.Binding{ f.areas[0].history, f.areas[1].history };
    var round = try f.plan(200, true);
    try eq(@as(u32, 0), exports.rm_metric_owner_history_commit_grouped(f.owner, &bindings, 2, &[_]u32{ 0, 1, 2 }, 3, round.token, 200, null));
    const a = f.arena.allocator();
    const normal = try abiCopy(a, f.areas[0].region);
    const admin = try abiCopy(a, f.areas[1].region);
    try eq(@as(u32, 1), normal.positionSlice()[f.areas[0].result.positions[0]].entry.length);
    try eq(@as(u32, 2), admin.positionSlice()[f.areas[1].result.positions[0]].entry.length);
    try eq(@as(u32, 1), try abiValue(u32, &admin, f.areas[1].result.positions[9], 0)); // logical source index0 is creator ordinal1
    try eq(@as(u32, 0), try abiValue(u32, &admin, f.areas[1].result.positions[9], 1)); // mixed metric stays admin although winner is normal
    try eq(@as(u64, 1000), try abiValue(u64, &normal, f.areas[0].history.positions[1], 0));
    try eq(@as(u64, @bitCast(@as(f64, 901))), try abiValue(u64, &admin, f.areas[1].history.positions[7], 0));
    round = try f.plan(300, false);
    try eq(p.Code.ok, h.commitMany(f.owner, &bindings, &.{ 0, 1, 2 }, round.token, 300));
    const failed_normal = try abiCopy(a, f.areas[0].region);
    const failed_admin = try abiCopy(a, f.areas[1].region);
    try eq(@as(u32, 2), try abiValue(u32, &failed_admin, f.areas[1].result.positions[11], 0));
    try eq(@as(u32, 0), try abiValue(u32, &failed_normal, f.areas[0].history.positions[8], 1));
    try eq(@as(u32, 0), try abiValue(u32, &failed_admin, f.areas[1].history.positions[8], 1));
    try eq(normal.heldSlice()[f.areas[0].history.rows_source].sequence, failed_normal.heldSlice()[f.areas[0].history.rows_source].sequence);
    try eq(admin.heldSlice()[f.areas[1].result.rows_source].sequence, failed_admin.heldSlice()[f.areas[1].result.rows_source].sequence);
    try eq(failed_admin.heldSlice()[f.areas[1].history.rows_source].sequence, failed_admin.positionSlice()[f.areas[1].history.positions[4]].entry.identity_sequence);
    const before = try abiCopy(a, f.areas[0].region);
    try std.testing.expectError(error.Stale, h.configure(f.owner, f.areas[0].history, &.{.{ .metric_id = 1001, .rounds = 2 }}, f.history_work, 400));
    const after = try abiCopy(a, f.areas[0].region);
    try std.testing.expectEqualSlices(u8, before.dataSlice(), after.dataSlice());
}
test "grouped checkpoint restores all result groups using current binding ordinals and rejects classification tampering" {
    var source = try GroupedFixture.init(false);
    defer source.deinit();
    var target = try GroupedFixture.init(true);
    defer target.deinit();
    const round = try source.plan(200, true);
    try eq(@as(u32, 0), api.rm_metric_owner_commit(source.owner, round.token, 200, null));
    const image = try source.image();
    var prepared: cp.Prepared = .{};
    try cp.prepare(target.owner, image, target.work, &prepared);
    try cp.publish(target.owner, &prepared, 300);
    const normal = try abiCopy(target.arena.allocator(), target.areas[0].region);
    const admin = try abiCopy(target.arena.allocator(), target.areas[1].region);
    try eq(@as(u32, 0), try abiValue(u32, &admin, target.areas[1].result.positions[9], 0)); // target binding order is reversed
    try eq(@as(u32, 1), try abiValue(u32, &normal, target.areas[0].result.positions[9], 0));
    try eq(@as(u64, 901), try abiValue(u64, &admin, target.areas[1].raw.positions[1], 0));
    try eq(@as(u64, 900), try abiValue(u64, &normal, target.areas[0].raw.positions[1], 0));
    try eq(admin.heldSlice()[target.areas[1].result.rows_source].sequence, admin.positionSlice()[target.areas[1].result.positions[11]].entry.identity_sequence);
    var fresh = try GroupedFixture.init(false);
    defer fresh.deinit();
    const bad = try source.arena.allocator().dupe(u8, image);
    std.mem.writeInt(u32, bad[64 + 32 ..][0..4], 0, .little);
    rehash(bad);
    const before0 = try abiCopy(fresh.arena.allocator(), fresh.areas[0].region);
    const before1 = try abiCopy(fresh.arena.allocator(), fresh.areas[1].region);
    try std.testing.expectError(error.Stale, cp.prepare(fresh.owner, bad, fresh.work, &prepared));
    const after0 = try abiCopy(fresh.arena.allocator(), fresh.areas[0].region);
    const after1 = try abiCopy(fresh.arena.allocator(), fresh.areas[1].region);
    try std.testing.expectEqualSlices(u8, before0.dataSlice(), after0.dataSlice());
    try std.testing.expectEqualSlices(u8, before1.dataSlice(), after1.dataSlice());
    try cp.prepare(fresh.owner, image, fresh.work, &prepared);
    var writer = try ss.Writer.begin(fresh.areas[1].region, fresh.areas[1].result.value_source);
    defer writer.end(.has_value, 100) catch {};
    try std.testing.expectError(error.Shared, cp.publish(fresh.owner, &prepared, 400));
    try writer.end(.has_value, 100);
    const rejected0 = try abiCopy(fresh.arena.allocator(), fresh.areas[0].region);
    const rejected1 = try abiCopy(fresh.arena.allocator(), fresh.areas[1].region);
    try std.testing.expectEqualSlices(u8, before0.dataSlice(), rejected0.dataSlice());
    try std.testing.expectEqualSlices(u8, before1.dataSlice(), rejected1.dataSlice());
    try expect(prepared.work == null and fresh.owner.work.region == null);
}
test "checkpoint absence keeps selected identity and generation guards and full u64 duration" {
    var source = try Fixture.init();
    defer source.deinit();
    var target = try Fixture.init();
    defer target.deinit();
    const r = try source.plan(&.{0}, 200);
    current(r, 0, 3, 42);
    r.observations.duration_ms.?[0] = std.math.maxInt(u64);
    try source.commit(r, &.{0}, 200, false);
    const image = try source.image();
    const work = try target.restoreWork(image.len);
    var q: cp.Prepared = .{};
    try cp.prepare(target.owner, image, work, &q);
    try cp.publish(target.owner, &q, 300);
    const raw = try st.table(target.owner, 0);
    try eq(@as(u64, std.math.maxInt(u64)), raw.load(u64, 7, 0));
    const copy = try abiCopy(target.arena.allocator(), target.region);
    const result = target.owner.results[0];
    const binding = target.owner.sources[0].binding;
    try eq(@as(u64, 0), try abiValue(u64, &copy, result.positions[2], 1));
    try eq(@as(u64, 101), try abiValue(u64, &copy, result.positions[1], 1));
    try eq(@as(u64, 2001), try abiValue(u64, &copy, result.positions[8], 1));
    try eq(try abiValue(u64, &copy, binding.positions[5], 1), try abiValue(u64, &copy, result.positions[3], 1));
    try eq(@as(u32, 0), try abiValue(u32, &copy, result.positions[9], 1));
    try eq(@as(u32, 1), try abiValue(u32, &copy, result.positions[10], 1));
    try eq(@as(u32, 0), try abiValue(u32, &copy, binding.positions[4], 1));
    const corrupted = try source.arena.allocator().dupe(u8, image);
    const metric_at = 64 + 40 + 3 * 224 + 88;
    std.mem.writeInt(u64, corrupted[metric_at + 32 ..][0..8], 99, .little); // absence generation guard
    std.mem.writeInt(u64, corrupted[metric_at + 64 ..][0..8], 2, .little); // not an equal-result-revision shortcut
    rehash(corrupted);
    var fresh = try Fixture.init();
    defer fresh.deinit();
    const fresh_work = try fresh.restoreWork(image.len);
    const before = try abiCopy(fresh.arena.allocator(), fresh.region);
    try std.testing.expectError(error.Invalid, cp.prepare(fresh.owner, corrupted, fresh_work, &q));
    const after = try abiCopy(fresh.arena.allocator(), fresh.region);
    try std.testing.expectEqualSlices(u8, before.dataSlice(), after.dataSlice());
}
test "checkpoint preserves CPU schema16 baseline through ordinary declared source unavailability" {
    var source = try Fixture.init();
    defer source.deinit();
    var target = try Fixture.init();
    defer target.deinit();
    for ([_]*Fixture{ &source, &target }) |f| {
        @constCast(f.catalog.rules.kinds.?)[0] = 1;
        @constCast(f.catalog.rules.value_kinds.?)[0] = 1;
        @constCast(f.catalog.rules.minimum_bits.?)[0] = @bitCast(@as(f64, 0));
        @constCast(f.catalog.rules.maximum_bits.?)[0] = @bitCast(@as(f64, 100));
        try eq(@as(u32, 0), api.rm_metric_owner_replace_catalog(f.owner, &f.catalog, f.work.ptr, f.work.len, 100, null));
    }
    const r = try source.plan(&.{0}, 200);
    current(r, 0, 1, 10);
    r.observations.high.?[0] = 100;
    r.observations.reference.?[0] = 100;
    r.observations.flags.?[0] = 16;
    r.observations.monotonic_ns.?[0] = 1000;
    r.observations.tick_frequency.?[0] = 1000;
    try source.commit(r, &.{0}, 200, false);
    for ([_]*Fixture{ &source, &target }) |f| {
        @constCast(f.catalog.sources.availability.?)[0] = 2;
        try eq(@as(u32, 0), api.rm_metric_owner_replace_catalog(f.owner, &f.catalog, f.work.ptr, f.work.len, 300, null));
    }
    try expect(source.owner.rules[0].baseline.valid and source.owner.rules[0].baseline.schema == 16);
    const image = try source.image();
    const work = try target.restoreWork(image.len);
    var q: cp.Prepared = .{};
    try cp.prepare(target.owner, image, work, &q);
    try cp.publish(target.owner, &q, 400);
    try eq(source.owner.rules[0].baseline, target.owner.rules[0].baseline);
    try eq(@as(u32, 2), target.owner.sources[0].availability);
}
