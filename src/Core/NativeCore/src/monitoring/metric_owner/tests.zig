const std = @import("std");
const api = @import("root.zig");
const p = @import("protocol.zig");
const st = @import("state.zig");
const ss = @import("rm_shared_section");
const expect = std.testing.expect;
const eq = std.testing.expectEqual;

const Fixture = struct {
    arena: std.heap.ArenaAllocator,
    region: []align(8) u8,
    active: []align(8) u8,
    work: []align(8) u8,
    owner: *st.Owner,
    bindings: [2]p.TableBinding,
    result: p.TableBinding,
    catalog: p.Catalog,
    fn init(n: u32) !Fixture {
        return initBudget(n, 8192);
    }
    fn initBudget(n: u32, payload_budget: u64) !Fixture {
        return initAdmitted(n, n, payload_budget);
    }
    fn initAdmitted(n: u32, capacity: u32, payload_budget: u64) !Fixture {
        var arena = std.heap.ArenaAllocator.init(std.testing.allocator);
        errdefer arena.deinit();
        const a = arena.allocator();
        const positions = try a.alloc(ss.PositionSpec, 47);
        var bindings: [3]p.TableBinding = undefined;
        var offset: u32 = 0;
        var pos: u32 = 0;
        for (&bindings, 0..) |*binding, ti| {
            binding.* = .{ .region = null, .byte_length = 0, .rows_source = @intCast(ti * 4), .value_source = @intCast(ti * 4 + 1), .health_source = @intCast(ti * 4 + 2), .provenance_source = @intCast(ti * 4 + 3) };
            const used: usize = if (ti == 2) 12 else 16;
            var slot: usize = 0;
            while (slot < used) : (slot += 1) {
                offset = std.mem.alignForward(u32, offset, 8);
                const width: u32 = if (ti == 2) (if (slot == 4 or slot == 5 or slot == 9 or slot == 10 or slot == 11) 4 else 8) else (if (slot == 9) 1 else if (slot == 3 or slot == 4 or slot == 10 or slot == 11 or slot == 13) 4 else 8);
                const source = if (slot == 0 or (ti != 2 and slot == 15)) binding.rows_source else if (ti != 2 and slot >= 10 and slot <= 12) binding.health_source else binding.value_source;
                binding.positions[slot] = pos;
                positions[pos] = if (ti != 2 and slot == 9) ss.PositionSpec.stringTable(source, offset, 4096) else ss.PositionSpec.column(source, offset, n, width, if (slot == 0) ss.none else binding.positions[0]);
                offset += positions[pos].size;
                pos += 1;
            }
            offset = std.mem.alignForward(u32, offset, 8);
            binding.provenance_position = pos;
            positions[pos] = ss.PositionSpec.column(binding.provenance_source, offset, 8 + n * (if (ti == 2) @as(u32, 4) else 12), 8, ss.none);
            offset += positions[pos].size;
            pos += 1;
        }
        const data = std.mem.alignForward(u32, offset, 8);
        const size = ss.requiredSize(12, 47, data);
        const region = try a.alignedAlloc(u8, .@"8", @intCast(size));
        var identities: [12]ss.SourceIdentity = undefined;
        for (&identities, 0..) |*id, i| id.* = try ss.SourceIdentity.init("fixture", @intCast(i + 1), 0, 0);
        try ss.initialize(region, 42, 12, 47, data, &identities, positions);
        for (&bindings) |*binding| {
            binding.region = region.ptr;
            binding.byte_length = region.len;
        }
        const config = p.Config{ .source_capacity = 2, .rule_capacity = capacity, .metric_capacity = capacity, .max_value_bytes = 512, .active_limit = 64 * 1024 * 1024, .work_limit = 64 * 1024 * 1024, .payload_work_bytes = payload_budget };
        var sizes: p.Sizes = undefined;
        var failure: p.Failure = undefined;
        try eq(@as(u32, 0), api.rm_metric_owner_required_sizes(&config, &sizes, &failure));
        const active = try a.alignedAlloc(u8, .@"8", @intCast(sizes.active));
        const work = try a.alignedAlloc(u8, .@"8", @intCast(sizes.work));
        var owner: ?*st.Owner = null;
        try eq(@as(u32, 0), api.rm_metric_owner_bind(&config, active.ptr, active.len, &bindings, 2, &bindings[2], &owner, &failure));
        const sources = try a.alloc(u64, 2);
        sources[0] = 10;
        sources[1] = 20;
        const incarnations = try a.alloc(u64, 2);
        @memset(incarnations, 1);
        const caps = try a.alloc(u64, 2);
        @memset(caps, 1);
        const availability = try a.alloc(u32, 2);
        @memset(availability, 1);
        const ids = try a.alloc(u64, n);
        const sourceids = try a.alloc(u64, n);
        const metricids = try a.alloc(u64, n);
        const ruleidentities = try a.alloc(u64, n);
        const public_metric_ids = try a.alloc(u64, n);
        const kinds = try a.alloc(u32, n);
        const flags = try a.alloc(u32, n);
        const priorities = try a.alloc(u32, n);
        const metricidentities = try a.alloc(u64, n);
        const valuekinds = try a.alloc(u32, n);
        const minima = try a.alloc(u64, n);
        const maxima = try a.alloc(u64, n);
        const masks = try a.alloc(u64, n);
        for (ids, 0..) |*id, i| {
            id.* = 100 + i;
            sourceids[i] = 10;
            metricids[i] = 1000 + i;
            public_metric_ids[i] = 1000 + i;
            ruleidentities[i] = 2000 + i;
            kinds[i] = 13;
            flags[i] = 0;
            priorities[i] = 1;
            metricidentities[i] = 3000 + i;
            valuekinds[i] = 3;
            minima[i] = 0;
            maxima[i] = std.math.maxInt(u64);
            masks[i] = 1;
        }
        const c = p.Catalog{ .sources = .{ .count = 2, .ids = sources.ptr, .incarnations = incarnations.ptr, .capabilities = caps.ptr, .availability = availability.ptr }, .rules = .{ .count = n, .ids = ids.ptr, .source_ids = sourceids.ptr, .metric_ids = metricids.ptr, .identities = ruleidentities.ptr, .kinds = kinds.ptr, .flags = flags.ptr, .priorities = priorities.ptr, .value_kinds = valuekinds.ptr, .minimum_bits = minima.ptr, .maximum_bits = maxima.ptr, .capability_masks = masks.ptr }, .metrics = .{ .count = n, .ids = public_metric_ids.ptr, .identities = metricidentities.ptr } };
        try eq(@as(u32, 0), api.rm_metric_owner_replace_catalog(owner, &c, work.ptr, work.len, 100, &failure));
        return .{ .arena = arena, .region = region, .active = active, .work = work, .owner = owner.?, .bindings = .{ bindings[0], bindings[1] }, .result = bindings[2], .catalog = c };
    }
    fn deinit(f: *Fixture) void {
        _ = api.rm_metric_owner_unbind(f.owner, null);
        f.arena.deinit();
    }
    fn kind(f: *Fixture, row: u32, value_kind: u32) void {
        @constCast(f.catalog.rules.value_kinds.?)[row] = value_kind;
        @constCast(f.catalog.rules.minimum_bits.?)[row] = if (value_kind == 1) @bitCast(-std.math.floatMax(f64)) else if (value_kind == 2) @bitCast(@as(i64, std.math.minInt(i64))) else 0;
        @constCast(f.catalog.rules.maximum_bits.?)[row] = switch (value_kind) {
            1 => @bitCast(std.math.floatMax(f64)),
            2 => @bitCast(@as(i64, std.math.maxInt(i64))),
            5 => 512,
            6 => 1,
            else => std.math.maxInt(u64),
        };
    }
    fn reload(f: *Fixture) !void {
        try eq(@as(u32, 0), api.rm_metric_owner_replace_catalog(f.owner, &f.catalog, f.work.ptr, f.work.len, 100, null));
    }
    fn plan(f: *Fixture, indices: []const u32, utc: i64) !p.Round {
        var out: p.Round = undefined;
        var failure: p.Failure = undefined;
        try eq(@as(u32, 0), api.rm_metric_owner_plan(f.owner, indices.ptr, @intCast(indices.len), f.work.ptr, f.work.len, utc, &out, &failure));
        return out;
    }
    fn commit(f: *Fixture, r: p.Round) !void {
        var failure: p.Failure = undefined;
        try eq(@as(u32, 0), api.rm_metric_owner_prepare(f.owner, r.token, &failure));
        try eq(@as(u32, 0), api.rm_metric_owner_commit(f.owner, r.token, 200, &failure));
        try expect(f.owner.work.region == null);
    }
    fn value(f: *Fixture, slot: usize, row: u32) !u64 {
        const t = try st.table(f.owner, 0);
        return t.load(u64, slot, row);
    }
    fn resultValue(f: *Fixture, slot: usize, row: u32) !u64 {
        const t = try st.resultTable(f.owner);
        return t.load(u64, slot, row);
    }
    fn publicStatus(f: *Fixture, row: u32) !u32 {
        const t = try st.resultTable(f.owner);
        return t.load(u32, 11, row);
    }
    fn rebind(f: *Fixture, utc: i64) !void {
        const config = f.owner.config;
        try eq(@as(u32, 0), api.rm_metric_owner_unbind_at(f.owner, utc, null));
        @memset(f.active, 0xcc);
        var owner: ?*st.Owner = null;
        try eq(@as(u32, 0), api.rm_metric_owner_bind(&config, f.active.ptr, f.active.len, &f.bindings, 2, &f.result, &owner, null));
        f.owner = owner.?;
        try f.reload();
    }
};
fn current(r: p.Round, i: u32, kind: u32, low: u64) void {
    const raw = r.observations;
    raw.status.?[i] = 1;
    raw.kinds.?[i] = kind;
    raw.presence.?[i] = 1;
    raw.low.?[i] = low;
}

test "exact integer boolean null and empty bytes live only in bound positions" {
    var f = try Fixture.init(4);
    defer f.deinit();
    f.kind(1, 6);
    f.kind(2, 5);
    try f.reload();
    const r = try f.plan(&.{ 0, 1, 2, 3 }, 200);
    current(r, 0, 3, 9_007_199_254_740_993);
    current(r, 1, 6, 1);
    current(r, 2, 5, 0);
    current(r, 3, 3, 0);
    r.observations.presence.?[3] = 2;
    try f.commit(r);
    try eq(@as(u64, 9_007_199_254_740_993), try f.value(1, 0));
    try eq(@as(u64, 1), try f.value(1, 1));
    const t = try st.table(f.owner, 0);
    try eq(@as(u32, 1), t.load(u32, 4, 2));
    try eq(@as(u64, 0), try f.value(8, 2));
    try eq(@as(u32, 2), t.load(u32, 4, 3));
    try eq(f.owner.rules[0].id, try f.resultValue(2, 0));
}
test "failure retains good and invalidation clears every source alias outside request" {
    var f = try Fixture.init(2);
    defer f.deinit();
    var r = try f.plan(&.{ 0, 1 }, 200);
    current(r, 0, 3, 44);
    current(r, 1, 3, 55);
    try f.commit(r);
    const gen = try f.value(5, 0);
    r = try f.plan(&.{0}, 300);
    try f.commit(r);
    try eq(@as(u64, 44), try f.value(1, 0));
    try eq(gen, try f.value(5, 0));
    try eq(@as(u64, 100), try f.resultValue(2, 0));
    r = try f.plan(&.{0}, 400);
    r.observations.flags.?[0] = 8;
    try f.commit(r);
    try eq(@as(u64, 0), try f.resultValue(2, 0));
    try eq(@as(u64, 0), try f.resultValue(2, 1));
    const t = try st.table(f.owner, 0);
    try eq(@as(u32, 0), t.load(u32, 4, 1));
}
test "payload writes actual bytes once no metric copy and malformed round rejects atomically" {
    var f = try Fixture.init(2);
    defer f.deinit();
    f.kind(0, 5);
    f.kind(1, 6);
    try f.reload();
    var r = try f.plan(&.{0}, 200);
    current(r, 0, 5, 0);
    @memcpy(r.payload.?[0..3], "abc");
    r.observations.payload_lengths.?[0] = 3;
    const before = f.owner.counters.payload;
    try f.commit(r);
    try eq(@as(u64, 3), f.owner.counters.payload - before);
    const ref = try f.value(8, 0);
    try eq(@as(u64, 3), ref >> 32);
    const gen = try f.value(5, 0);
    const writes = f.owner.counters.written;
    r = try f.plan(&.{ 0, 1 }, 300);
    current(r, 0, 5, 0);
    current(r, 1, 6, 2);
    try eq(@as(u32, @backingInt(p.Code.invalid_contract)), api.rm_metric_owner_prepare(f.owner, r.token, null));
    try eq(gen, try f.value(5, 0));
    try eq(writes, f.owner.counters.written);
    try expect(f.owner.work.region == null);
}
test "unsupported capability suppresses queries and changed capability restores them" {
    var f = try Fixture.init(1);
    defer f.deinit();
    var r = try f.plan(&.{0}, 200);
    r.observations.status.?[0] = 3;
    try f.commit(r);
    r = try f.plan(&.{0}, 300);
    try eq(@as(u32, 0), r.count);
    _ = api.rm_metric_owner_abort_detach(f.owner, null);
    @constCast(f.catalog.sources.capabilities.?)[0] = 2;
    try eq(@as(u32, 0), api.rm_metric_owner_replace_catalog(f.owner, &f.catalog, f.work.ptr, f.work.len, 300, null));
    r = try f.plan(&.{0}, 400);
    try eq(@as(u32, 1), r.count);
    _ = api.rm_metric_owner_abort_detach(f.owner, null);
}
test "counter reset advances baseline without destroying public last good" {
    var f = try Fixture.init(1);
    defer f.deinit();
    @constCast(f.catalog.rules.kinds.?)[0] = 14;
    f.kind(0, 1);
    try eq(@as(u32, 0), api.rm_metric_owner_replace_catalog(f.owner, &f.catalog, f.work.ptr, f.work.len, 100, null));
    var r = try f.plan(&.{0}, 200);
    current(r, 0, 1, 10);
    r.observations.high.?[0] = 20;
    r.observations.flags.?[0] = 1;
    try f.commit(r);
    try eq(@as(u64, 0), try f.resultValue(2, 0));
    r = try f.plan(&.{0}, 300);
    current(r, 0, 1, 20);
    r.observations.high.?[0] = 40;
    r.observations.flags.?[0] = 1;
    try f.commit(r);
    try eq(@as(u64, @bitCast(@as(f64, 0.5))), try f.value(1, 0));
    const generation = try f.value(5, 0);
    r = try f.plan(&.{0}, 400);
    current(r, 0, 1, 1);
    r.observations.high.?[0] = 2;
    r.observations.flags.?[0] = 1;
    try f.commit(r);
    try eq(generation, try f.value(5, 0));
    try eq(@as(u64, 1), f.owner.rules[0].baseline.low);
}
test "one scalar write is independent of unrelated row capacity and row source does not move" {
    var expected: ?u64 = null;
    var expected_reads: ?u64 = null;
    for ([_]u32{ 1, 400, 16384 }) |n| {
        var f = try Fixture.init(n);
        defer f.deinit();
        const t = try st.table(f.owner, 0);
        const seq = t.identitySequence();
        const before = f.owner.counters.written;
        const reads_before = f.owner.counters.read;
        const r = try f.plan(&.{0}, 200);
        current(r, 0, 3, 123);
        try f.commit(r);
        const written = f.owner.counters.written - before;
        // 52 scalar columns +16 health +68 result reference/health columns,
        // plus3*(8 begin +24 end) bytes of SharedSection source metadata,
        // and the first-attempt provenance word8 + its source metadata32.
        try eq(@as(u64, 272), written);
        if (expected) |prior| try eq(prior, written) else expected = written;
        const reads = f.owner.counters.read - reads_before;
        if (expected_reads) |prior| try eq(prior, reads) else expected_reads = reads;
        try eq(seq, (try st.table(f.owner, 0)).identitySequence());
        if (n > 1) {
            try eq(@as(u64, 1), try f.value(5, 1));
            try eq(@as(u64, 0), try f.resultValue(7, 1) - 1);
        }
        try eq(@as(u64, 0), f.owner.counters.payload);
        try eq(@as(u32, 0), @atomicLoad(u32, &t.layout.position(f.region, t.binding.positions[9]).length, .acquire));
        const next_before = f.owner.counters.written;
        const next = try f.plan(&.{0}, 300);
        current(next, 0, 3, 124);
        try f.commit(next);
        try eq(@as(u64, 232), f.owner.counters.written - next_before);
    }
}
// Test-only byte mask: native may modify these ACTUAL used elements, never the
// rest of the admitted workspace. This is not a production write counter.
fn allowRoundBytes(mask: []bool, backing: []const u8, used: []const u8) !void {
    if (used.len == 0) return;
    const base = @intFromPtr(backing.ptr);
    const start = @intFromPtr(used.ptr);
    try expect(start >= base and used.len <= backing.len and start - base <= backing.len - used.len);
    @memset(mask[start - base ..][0..used.len], true);
}
fn assertUntouchedRoundBytes(mask: []const bool, before: []const u8, after: []const u8) !void {
    try eq(before.len, after.len);
    for (mask, before, after, 0..) |allowed, prior, actual, i| if (!allowed and prior != actual) {
        std.debug.print("unused round work byte {d}: expected {x}, found {x}\n", .{ i, prior, actual });
        return error.UnusedRoundWorkChanged;
    };
}
test "one rule round leaves unused admitted work and large payload bytes untouched" {
    var admitted_writes: ?usize = null;
    for ([_]u32{ 400, 4000 }) |capacity| {
        // One actual rule/metric; larger admission is workspace capacity only.
        var f = try Fixture.initAdmitted(1, capacity, 1024 * 1024);
        defer f.deinit();
        try eq(@as(u32, 1), f.owner.rule_count);
        try eq(@as(u32, 1), f.owner.metric_count);
        const a = f.arena.allocator();
        const mask = try a.alloc(bool, f.work.len);
        for (0..3) |iteration| {
            for (f.work, 0..) |*byte, i| byte.* = if (iteration == 0) 0xa7 else @truncate((i *% 131) ^ (i >> 7) ^ (iteration * 0x53));
            const before = try a.dupe(u8, f.work);
            @memset(mask, false);
            if (iteration == 0) {
                var rejected: p.Round = undefined;
                var failure: p.Failure = undefined;
                try eq(@as(u32, @backingInt(p.Code.workspace_unavailable)), api.rm_metric_owner_plan(f.owner, &.{0}, 1, f.work.ptr, f.work.len - 1, 200, &rejected, &failure));
                try eq(f.owner.sizes.work, failure.required);
                try eq(@as(u64, f.work.len - 1), failure.limit);
                try expect(f.owner.work.region == null);
                try std.testing.expectEqualSlices(u8, before, f.work);
            }
            const raw = try f.plan(&.{0}, 200);
            const work = f.owner.work; // Local test borrow survives native detach.
            try eq(capacity, raw.observations.capacity);
            try eq(@as(u32, 1), raw.count);
            try eq(@as(u64, 1024 * 1024), raw.payload_capacity);
            inline for (.{ "observations", "queries" }) |group| {
                const Columns = @TypeOf(@field(raw, group));
                inline for (@typeInfo(Columns).@"struct".field_names, @typeInfo(Columns).@"struct".field_types) |column_name, column_type| if (@typeInfo(column_type) == .optional) {
                    try allowRoundBytes(mask, f.work, std.mem.sliceAsBytes(@field(@field(raw, group), column_name).?[0..1]));
                };
            }
            inline for (.{ "requested", "deltas", "expected", "affected", "synthetic", "affected_ids" }) |field| try allowRoundBytes(mask, f.work, std.mem.sliceAsBytes(@field(work, field)[0..1]));
            inline for (.{ "invalid_sources", "unsupported_sources", "unsupported_reasons" }) |field| try allowRoundBytes(mask, f.work, std.mem.sliceAsBytes(@field(work, field)[0..f.owner.source_count]));
            inline for (.{ "opens", "tables" }) |field| try allowRoundBytes(mask, f.work, std.mem.sliceAsBytes(@field(work, field)[0 .. f.owner.source_count + f.owner.result_count]));
            try allowRoundBytes(mask, f.work, std.mem.asBytes(work.receipt.?));
            const allowed = std.mem.countScalar(bool, mask, true);
            if (admitted_writes) |previous| try eq(previous, allowed) else admitted_writes = allowed;
            try expect(allowed < f.work.len / 10);
            // Payload and every unused raw/query/delta/expected/synthetic/result
            // metadata slot remain outside the allowed byte ranges.
            try assertUntouchedRoundBytes(mask, before, f.work);
            if (iteration == 0) {
                // Internal reservation failure at the last large extent must
                // neither poison prior ranges nor expose a partial descriptor.
                const planned_bytes = try a.dupe(u8, f.work);
                const payload_end = @intFromPtr(work.payload.ptr) - @intFromPtr(f.work.ptr) + work.payload.len;
                try std.testing.expectError(error.OutOfMemory, st.allocateRound(f.owner, f.work[0 .. payload_end - 1]));
                try std.testing.expectEqualSlices(u8, planned_bytes, f.work);
                try eq(raw.token, f.owner.work.raw.token);
            }
            if (iteration != 2) current(raw, 0, 3, 42 + iteration);
            try eq(@as(u32, 0), api.rm_metric_owner_prepare(f.owner, raw.token, null));
            try assertUntouchedRoundBytes(mask, before, f.work);
            var receipt: p.Receipt = undefined;
            try eq(@as(u32, 0), api.rm_metric_owner_commit_receipt(f.owner, raw.token, 200, &receipt, null));
            try eq(@as(u32, 1), receipt.committed);
            try eq(@as(u32, 1), receipt.query_count);
            try eq(@as(u32, 1), receipt.affected_count);
            try eq(@as(u32, 0), receipt.invalidated_alias_count);
            try expect(f.owner.work.region == null);
            try assertUntouchedRoundBytes(mask, before, f.work);
            try eq(@as(u64, if (iteration == 0) 42 else 43), try f.value(1, 0));
            try eq(@as(u32, if (iteration == 2) 2 else 1), try f.publicStatus(0));
        }
    }
}

test "sizes caps stale tokens short workspace abort and shutdown detach before release" {
    var f = try Fixture.init(1);
    defer f.arena.deinit();
    var failure: p.Failure = undefined;
    var sizes: p.Sizes = undefined;
    var config = f.owner.config;
    config.work_limit = 1;
    try eq(@as(u32, @backingInt(p.Code.capacity_exceeded)), api.rm_metric_owner_required_sizes(&config, &sizes, &failure));
    try eq(p.Family.work, failure.family);
    try eq(sizes.work, failure.required);
    var r: p.Round = undefined;
    try eq(@as(u32, @backingInt(p.Code.workspace_unavailable)), api.rm_metric_owner_plan(f.owner, &.{0}, 1, f.work.ptr, 1, 200, &r, &failure));
    r = try f.plan(&.{0}, 200);
    try eq(@as(u32, @backingInt(p.Code.stale_plan)), api.rm_metric_owner_prepare(f.owner, r.token + 1, &failure));
    try expect(f.owner.work.region == null);
    r = try f.plan(&.{0}, 200);
    try eq(@as(u32, 0), api.rm_metric_owner_abort_detach(f.owner, &failure));
    try eq(@as(u32, 0), api.rm_metric_owner_abort_detach(f.owner, &failure));
    r = try f.plan(&.{0}, 200);
    try eq(@as(u32, 0), api.rm_metric_owner_unbind(f.owner, &failure));
    try expect(f.owner.work.region == null);
    try eq(@as(u32, @backingInt(p.Code.closed)), api.rm_metric_owner_commit(f.owner, r.token, 200, &failure));
    try expect((try ss.describe(f.region)).magic == ss.magic);
}

test "native priority arbitration includes competing origins and falls back to current" {
    var f = try Fixture.init(2);
    defer f.deinit();
    @constCast(f.catalog.rules.source_ids.?)[1] = 20;
    @constCast(f.catalog.rules.metric_ids.?)[1] = 1000;
    f.catalog.metrics.count = 1;
    @constCast(f.catalog.rules.priorities.?)[0] = 2;
    @constCast(f.catalog.rules.priorities.?)[1] = 1;
    try f.reload();
    var r = try f.plan(&.{0}, 200);
    try eq(@as(u32, 1), r.count);
    try eq(@as(u64, 101), r.queries.rule_ids.?[0]);
    current(r, 0, 3, 20);
    try f.commit(r);
    try eq(@as(u64, 101), try f.resultValue(2, 0));
    r = try f.plan(&.{0}, 300);
    r.observations.status.?[0] = 3;
    try f.commit(r);
    try eq(@as(u64, 101), try f.resultValue(2, 0));
    r = try f.plan(&.{0}, 400);
    try eq(@as(u64, 100), r.queries.rule_ids.?[0]);
    current(r, 0, 3, 30);
    try f.commit(r);
    try eq(@as(u64, 100), try f.resultValue(2, 0));
}

test "catalog reorder keeps exact failed last-good identities and incarnation change clears them" {
    var f = try Fixture.init(2);
    defer f.deinit();
    var r = try f.plan(&.{ 0, 1 }, 200);
    current(r, 0, 3, 70);
    current(r, 1, 3, 80);
    try f.commit(r);
    r = try f.plan(&.{ 0, 1 }, 300);
    try f.commit(r);
    const generation = try f.value(5, 0);
    const rowseq = (try st.table(f.owner, 0)).identitySequence();
    std.mem.swap(u64, &@constCast(f.catalog.metrics.ids.?)[0], &@constCast(f.catalog.metrics.ids.?)[1]);
    std.mem.swap(u64, &@constCast(f.catalog.metrics.identities.?)[0], &@constCast(f.catalog.metrics.identities.?)[1]);
    try f.reload();
    try eq(@as(u64, 100), try f.resultValue(2, 1));
    try eq(generation, try f.resultValue(3, 1));
    try eq(rowseq, (try st.table(f.owner, 0)).identitySequence());
    @constCast(f.catalog.sources.incarnations.?)[0] = 2;
    try f.reload();
    try eq(@as(u64, 0), try f.resultValue(2, 0));
    try eq(@as(u64, 0), try f.resultValue(2, 1));
    try expect(try f.value(5, 0) > generation);
}

test "payload exhaustion rejects all stores and freed extent is reused without compaction" {
    var f = try Fixture.init(9);
    defer f.deinit();
    var i: u32 = 0;
    while (i < 9) : (i += 1) f.kind(i, 5);
    try f.reload();
    var r = try f.plan(&.{ 0, 1, 2, 3, 4, 5, 6, 7 }, 200);
    i = 0;
    while (i < 8) : (i += 1) {
        current(r, i, 5, 0);
        r.observations.payload_offsets.?[i] = i * 512;
        r.observations.payload_lengths.?[i] = 512;
        @memset(r.payload.?[i * 512 ..][0..512], @intCast(i));
    }
    try f.commit(r);
    const generation = try f.value(5, 0);
    const bytes = f.owner.counters.written;
    r = try f.plan(&.{8}, 300);
    current(r, 0, 5, 0);
    r.observations.payload_lengths.?[0] = 1;
    var failure: p.Failure = undefined;
    try eq(@as(u32, @backingInt(p.Code.payload_capacity)), api.rm_metric_owner_prepare(f.owner, r.token, &failure));
    try eq(p.Family.payload, failure.family);
    try eq(@as(u64, 4096), failure.limit);
    try eq(bytes, f.owner.counters.written);
    try eq(generation, try f.value(5, 0));
    r = try f.plan(&.{0}, 400);
    current(r, 0, 5, 0);
    r.observations.presence.?[0] = 2;
    try f.commit(r);
    r = try f.plan(&.{8}, 500);
    current(r, 0, 5, 0);
    r.observations.payload_lengths.?[0] = 512;
    @memset(r.payload.?[0..512], 99);
    try f.commit(r);
    try eq(@as(u64, 0), @as(u32, @truncate(try f.value(8, 8))));
}

test "range type capability overflow and malformed bindings reject without publication" {
    var f = try Fixture.init(1);
    defer f.deinit();
    @constCast(f.catalog.rules.maximum_bits.?)[0] = 100;
    try f.reload();
    const before = f.owner.counters.written;
    var r = try f.plan(&.{0}, 200);
    current(r, 0, 3, 101);
    try eq(@as(u32, @backingInt(p.Code.invalid_contract)), api.rm_metric_owner_prepare(f.owner, r.token, null));
    try eq(before, f.owner.counters.written);
    r = try f.plan(&.{0}, 200);
    current(r, 0, 2, 99);
    try eq(@as(u32, @backingInt(p.Code.invalid_contract)), api.rm_metric_owner_prepare(f.owner, r.token, null));
    r = try f.plan(&.{0}, 200);
    current(r, 0, 3, 99);
    r.observations.capability_masks.?[0] = 2;
    try eq(@as(u32, @backingInt(p.Code.invalid_contract)), api.rm_metric_owner_prepare(f.owner, r.token, null));
    var bad = f.bindings;
    bad[0].positions[3] = bad[0].positions[1];
    var owner: ?*st.Owner = null;
    try eq(@as(u32, @backingInt(p.Code.invalid_contract)), api.rm_metric_owner_bind(&f.owner.config, f.active.ptr, f.active.len, &bad, 2, &f.result, &owner, null));
    try expect(owner == null);
    try eq(before, f.owner.counters.written);
    var config = f.owner.config;
    config.rule_capacity = std.math.maxInt(u32);
    config.max_value_bytes = std.math.maxInt(u32);
    config.active_limit = std.math.maxInt(u64);
    config.work_limit = std.math.maxInt(u64);
    config.payload_work_bytes = std.math.maxInt(u64);
    var sizes: p.Sizes = undefined;
    try eq(@as(u32, @backingInt(p.Code.capacity_exceeded)), api.rm_metric_owner_required_sizes(&config, &sizes, null));
}

test "catalog inputs can be filled inside the one work lease without scratch aliasing" {
    var f = try Fixture.init(2);
    defer f.deinit();
    var c: p.Catalog = undefined;
    try eq(@as(u32, 0), api.rm_metric_owner_catalog_input(f.owner, f.work.ptr, f.work.len, &c, null));
    inline for (.{ "ids", "incarnations", "capabilities" }) |field| @memcpy(@constCast(@field(c.sources, field).?)[0..2], @field(f.catalog.sources, field).?[0..2]);
    @memcpy(@constCast(c.sources.availability.?)[0..2], f.catalog.sources.availability.?[0..2]);
    inline for (.{ "ids", "source_ids", "metric_ids", "identities", "minimum_bits", "maximum_bits", "capability_masks" }) |field| @memcpy(@constCast(@field(c.rules, field).?)[0..2], @field(f.catalog.rules, field).?[0..2]);
    inline for (.{ "kinds", "flags", "priorities", "value_kinds" }) |field| @memcpy(@constCast(@field(c.rules, field).?)[0..2], @field(f.catalog.rules, field).?[0..2]);
    inline for (.{ "ids", "identities" }) |field| @memcpy(@constCast(@field(c.metrics, field).?)[0..2], @field(f.catalog.metrics, field).?[0..2]);
    const prefix: usize = @intCast(st.catalogInputBytes(f.owner.config));
    try eq(@intFromPtr(f.work.ptr) + prefix, @intFromPtr(c.metrics.identities.?) + 2 * @sizeOf(u64));
    const filled_input = try f.arena.allocator().dupe(u8, f.work[0..prefix]);
    try eq(@as(u32, 0), api.rm_metric_owner_replace_catalog(f.owner, &c, f.work.ptr, f.work.len, 200, null));
    try std.testing.expectEqualSlices(u8, filled_input, f.work[0..prefix]);
    try eq(@as(u64, 100), f.owner.rules[0].id);
    try eq(@as(u64, 101), f.owner.rules[1].id);
    try eq(@as(u64, 1001), f.owner.metrics[1].id);
    const before = f.owner.catalog_generation;
    const written = f.owner.counters.written;
    // Reject both a column crossing the reserved-prefix boundary and columns
    // inside output scratch. Valid prefix inputs must remain unchanged too.
    for ([_]usize{ prefix - 8, prefix, std.mem.alignBackward(usize, f.work.len - 16, 8) }) |offset| {
        c.rules.ids = @ptrCast(@alignCast(f.work.ptr + offset));
        try eq(@as(u32, @backingInt(p.Code.invalid_contract)), api.rm_metric_owner_replace_catalog(f.owner, &c, f.work.ptr, f.work.len, 300, null));
        try eq(before, f.owner.catalog_generation);
        try eq(written, f.owner.counters.written);
        try std.testing.expectEqualSlices(u8, filled_input, f.work[0..prefix]);
        try expect(f.owner.work.region == null);
    }
}

test "catalog input prefix size matches allocation for zero and odd column capacities" {
    for ([_]u32{ 1, 2, 3 }) |sources| for ([_]u32{ 0, 1, 3 }) |rules| for ([_]u32{ 0, 1, 3 }) |metrics| {
        const config = p.Config{ .source_capacity = sources, .rule_capacity = rules, .metric_capacity = metrics, .max_value_bytes = 512, .active_limit = 65536, .work_limit = 65536 };
        const extent = st.catalogInputBytes(config);
        const bytes = try std.testing.allocator.alignedAlloc(u8, .@"8", @intCast(extent));
        defer std.testing.allocator.free(bytes);
        var fba = std.heap.FixedBufferAllocator.init(bytes);
        const input = try st.allocateCatalogInput(fba.allocator(), config);
        try eq(extent, @as(u64, fba.end_index));
        try eq(sources, input.sources.count);
        try eq(rules, input.rules.count);
        try eq(metrics, input.metrics.count);
        try expect(st.sizes(config).work > extent);
    };
}

test "zero catalog priority rejects atomically under the existing enum authority" {
    var f = try Fixture.init(1);
    defer f.deinit();
    const generation = f.owner.catalog_generation;
    const written = f.owner.counters.written;
    @constCast(f.catalog.rules.priorities.?)[0] = 0;
    var failure: p.Failure = undefined;
    try eq(@as(u32, @backingInt(p.Code.invalid_contract)), api.rm_metric_owner_replace_catalog(f.owner, &f.catalog, f.work.ptr, f.work.len, 300, &failure));
    try eq(p.Family.rules, failure.family);
    try eq(generation, f.owner.catalog_generation);
    try eq(written, f.owner.counters.written);
    try expect(f.owner.work.region == null);
}

test "SharedSection consistent copied reads see exact rule and result references" {
    var f = try Fixture.init(1);
    defer f.deinit();
    const r = try f.plan(&.{0}, 200);
    current(r, 0, 3, 9_007_199_254_740_995);
    try f.commit(r);
    var copy = try copyFor(&f);
    _ = ss.read(f.region, &copy, f.bindings[0].rows_source, 10);
    _ = ss.read(f.region, &copy, f.bindings[0].value_source, 10);
    _ = ss.read(f.region, &copy, f.result.rows_source, 10);
    _ = ss.read(f.region, &copy, f.result.value_source, 10);
    const value = switch (ss.positionValue(&copy, f.bindings[0].positions[1])) {
        .value => |v| v,
        else => return error.NoValue,
    };
    try eq(@as(u64, 9_007_199_254_740_995), std.mem.readInt(u64, value.bytes[0..8], .little));
    const ref = switch (ss.positionValue(&copy, f.result.positions[3])) {
        .value => |v| v,
        else => return error.NoValue,
    };
    try eq(try f.value(5, 0), std.mem.readInt(u64, ref.bytes[0..8], .little));
    switch (ss.read(f.region, &copy, f.bindings[0].value_source, 10)) {
        .unchanged => {},
        else => return error.ExpectedUnchanged,
    }
}

test "CPU raw counters stay native preserve baseline on failure and use monotonic duration" {
    var f = try Fixture.init(1);
    defer f.deinit();
    f.kind(0, 1);
    @constCast(f.catalog.rules.kinds.?)[0] = 1;
    try f.reload();
    var r = try f.plan(&.{0}, 200);
    current(r, 0, 1, 100);
    r.observations.high.?[0] = 200;
    r.observations.reference.?[0] = 100;
    r.observations.monotonic_ns.?[0] = 1000;
    r.observations.tick_frequency.?[0] = 1000;
    r.observations.flags.?[0] = 16;
    try f.commit(r);
    r = try f.plan(&.{0}, 100);
    current(r, 0, 1, 150);
    r.observations.high.?[0] = 300;
    r.observations.reference.?[0] = 200;
    r.observations.monotonic_ns.?[0] = 2000;
    r.observations.tick_frequency.?[0] = 1000;
    r.observations.flags.?[0] = 16;
    try f.commit(r);
    try eq(@as(u64, @bitCast(@as(f64, 75))), try f.value(1, 0));
    try eq(@as(u64, 1000), try f.value(7, 0));
    r = try f.plan(&.{0}, 300);
    try f.commit(r);
    try expect(f.owner.rules[0].baseline.valid);
    try eq(@as(u64, 2000), f.owner.rules[0].baseline.monotonic);
}

test "signed64 and unsigned128 preserve every bit and publish quality capability" {
    var f = try Fixture.init(2);
    defer f.deinit();
    f.kind(0, 2);
    f.kind(1, 4);
    try f.reload();
    const r = try f.plan(&.{ 0, 1 }, 200);
    current(r, 0, 2, @bitCast(@as(i64, -9_007_199_254_740_993)));
    current(r, 1, 4, std.math.maxInt(u64));
    r.observations.high.?[1] = std.math.maxInt(u64);
    r.observations.quality.?[0] = 17;
    try f.commit(r);
    try eq(@as(u64, @bitCast(@as(i64, -9_007_199_254_740_993))), try f.value(1, 0));
    try eq(std.math.maxInt(u64), try f.value(2, 1));
    const t = try st.table(f.owner, 0);
    try eq(@as(u32, 17), t.load(u32, 13, 0));
    try eq(@as(u64, 1), try f.value(14, 0));
}

test "prepared wake view includes unrequested invalidated aliases and query identity tampering rejects" {
    var f = try Fixture.init(2);
    defer f.deinit();
    var r = try f.plan(&.{0}, 200);
    const writes = f.owner.counters.written;
    @constCast(r.queries.incarnations.?)[0] = 99;
    try eq(@as(u32, @backingInt(p.Code.stale_identity)), api.rm_metric_owner_prepare(f.owner, r.token, null));
    try eq(writes, f.owner.counters.written);
    try expect(f.owner.work.region == null);
    r = try f.plan(&.{0}, 300);
    r.observations.flags.?[0] = 8;
    try eq(@as(u32, 0), api.rm_metric_owner_prepare(f.owner, r.token, null));
    var prepared: p.Prepared = undefined;
    try eq(@as(u32, 0), api.rm_metric_owner_prepared_view(f.owner, r.token, &prepared, null));
    try eq(@as(u32, 2), prepared.count);
    try eq(@as(u32, 0), prepared.metric_indices.?[0]);
    try eq(@as(u32, 1), prepared.metric_indices.?[1]);
    try eq(@as(u32, 0), api.rm_metric_owner_commit(f.owner, r.token, 300, null));
    try expect(f.owner.work.region == null);
    // This is caller-owned work still under the same lease; native retains no
    // pointer. The parent consumes these wake indices before its lease ends.
    try eq(@as(u32, 1), prepared.metric_indices.?[1]);
}

test "ended mapping before publication detaches and no source availability makes queries" {
    var f = try Fixture.init(1);
    defer f.deinit();
    @constCast(f.catalog.sources.availability.?)[0] = 2;
    try f.reload();
    var r = try f.plan(&.{0}, 200);
    try eq(@as(u32, 0), r.count);
    _ = api.rm_metric_owner_abort_detach(f.owner, null);
    @constCast(f.catalog.sources.availability.?)[0] = 1;
    try f.reload();
    r = try f.plan(&.{0}, 300);
    current(r, 0, 3, 90);
    try eq(@as(u32, 0), api.rm_metric_owner_prepare(f.owner, r.token, null));
    try ss.end(f.region, 300);
    try eq(@as(u32, @backingInt(p.Code.shared_section)), api.rm_metric_owner_commit(f.owner, r.token, 300, null));
    try expect(f.owner.work.region == null);
}

test "whole-source unsupported raw32 preserves every alias and flags outside63 reject" {
    var f = try Fixture.init(2);
    defer f.deinit();
    var r = try f.plan(&.{ 0, 1 }, 200);
    current(r, 0, 3, 11);
    current(r, 1, 3, 22);
    try f.commit(r);
    const t = try st.table(f.owner, 0);
    const row_sequence = t.identitySequence();
    const value_sequence = t.layout.source(t.bytes(), t.binding.value_source).sequence;
    const good_generation = t.load(u64, 5, 1);
    r = try f.plan(&.{0}, 300);
    r.observations.status.?[0] = 3;
    r.observations.flags.?[0] = 32;
    r.observations.reasons.?[0] = 77;
    try eq(@as(u32, 0), api.rm_metric_owner_prepare(f.owner, r.token, null));
    var receipt: p.Receipt = undefined;
    try eq(@as(u32, 0), api.rm_metric_owner_commit_receipt(f.owner, r.token, 300, &receipt, null));
    try eq(@as(u32, 2), receipt.affected_count);
    try eq(@as(u32, 1), receipt.query_count);
    try eq(@as(u32, 0), receipt.invalidated_alias_count);
    try expect(f.owner.sources[0].suppressed);
    try eq(@as(u64, 101), try f.resultValue(2, 1));
    try eq(@as(u32, 2), try f.publicStatus(0));
    try eq(@as(u32, 2), try f.publicStatus(1));
    try eq(@as(u64, 11), try f.value(1, 0));
    try eq(@as(u64, 22), try f.value(1, 1));
    try eq(@as(u32, 3), t.load(u32, 10, 1));
    try eq(@as(u32, 77), t.load(u32, 11, 1));
    try eq(good_generation, t.load(u64, 5, 1));
    try eq(value_sequence, t.layout.source(t.bytes(), t.binding.value_source).sequence);
    try eq(row_sequence, (try st.table(f.owner, 0)).identitySequence());
    r = try f.plan(&.{1}, 400);
    try eq(@as(u32, 0), r.count);
    _ = api.rm_metric_owner_abort_detach(f.owner, null);
    try f.rebind(450);
    try expect(f.owner.sources[0].suppressed);
    try eq(@as(u32, 2), try f.publicStatus(1));
    @constCast(f.catalog.sources.capabilities.?)[0] = 2;
    @constCast(f.catalog.sources.availability.?)[0] = 1;
    try f.reload();
    r = try f.plan(&.{1}, 500);
    try eq(@as(u32, 1), r.count);
    r.observations.flags.?[0] = 64;
    try eq(@as(u32, @backingInt(p.Code.invalid_contract)), api.rm_metric_owner_prepare(f.owner, r.token, null));
    r = try f.plan(&.{1}, 600);
    r.observations.flags.?[0] = 32;
    try eq(@as(u32, @backingInt(p.Code.invalid_contract)), api.rm_metric_owner_prepare(f.owner, r.token, null));
    r = try f.plan(&.{1}, 700);
    r.observations.status.?[0] = 3;
    r.observations.flags.?[0] = 32 | 8;
    try eq(@as(u32, @backingInt(p.Code.invalid_contract)), api.rm_metric_owner_prepare(f.owner, r.token, null));
}

test "timed floating percentage and frequency counters retain baseline reset semantics" {
    const Case = struct { kind: u32, flags: u32, frequency: u64 = 0, reference: u64 = 0, value: f64 };
    const cases = [_]Case{
        .{ .kind = 15, .flags = 2, .frequency = 1000, .value = 100 },
        .{ .kind = 16, .flags = 4, .value = 0.5 },
        .{ .kind = 17, .flags = 4, .value = 50 },
        .{ .kind = 18, .flags = 2, .frequency = 1000, .value = 0.01 },
        .{ .kind = 19, .flags = 1, .reference = 3000, .value = 300 },
    };
    for (cases) |case| {
        var f = try Fixture.init(1);
        defer f.deinit();
        f.kind(0, 1);
        @constCast(f.catalog.rules.kinds.?)[0] = case.kind;
        try f.reload();
        var r = try f.plan(&.{0}, 200);
        try eq(@as(u32, 1), r.queries.value_kinds.?[0]);
        try eq(case.kind, r.queries.metric_kinds.?[0]);
        try eq(@as(u64, 100), r.queries.rule_ids.?[0]);
        try eq(@as(u64, 10), r.queries.source_ids.?[0]);
        try eq(@as(u64, 1000), r.queries.metric_ids.?[0]);
        current(r, 0, 1, if (case.flags == 4) @bitCast(@as(f64, 10)) else 10);
        r.observations.high.?[0] = if (case.flags == 4) @bitCast(@as(f64, 20)) else 100;
        r.observations.flags.?[0] = case.flags;
        r.observations.tick_frequency.?[0] = case.frequency;
        r.observations.reference.?[0] = case.reference;
        try f.commit(r);
        try eq(@as(u64, 0), try f.resultValue(2, 0));
        r = try f.plan(&.{0}, 300);
        current(r, 0, 1, if (case.flags == 4) @bitCast(@as(f64, 20)) else 20);
        r.observations.high.?[0] = if (case.flags == 4) @bitCast(@as(f64, 40)) else 200;
        r.observations.flags.?[0] = case.flags;
        r.observations.tick_frequency.?[0] = case.frequency;
        r.observations.reference.?[0] = case.reference;
        try f.commit(r);
        try eq(@as(u64, @bitCast(case.value)), try f.value(1, 0));
        const generation = try f.value(5, 0);
        try eq(@as(u64, if (case.flags == 4) 20000 else 100), try f.value(7, 0));
        r = try f.plan(&.{0}, 400);
        current(r, 0, 1, if (case.flags == 4) @bitCast(@as(f64, 1)) else 30);
        r.observations.high.?[0] = if (case.flags == 4) @bitCast(@as(f64, 2)) else 300;
        r.observations.flags.?[0] = case.flags;
        r.observations.tick_frequency.?[0] = case.frequency * 2;
        r.observations.reference.?[0] = case.reference + 1;
        try f.commit(r);
        try eq(generation, try f.value(5, 0));
        try eq(@as(u64, @bitCast(case.value)), try f.value(1, 0));
    }
}

test "catalog capacity and duplicate identity failures leave publication and active catalog untouched" {
    var f = try Fixture.init(2);
    defer f.deinit();
    const generation = f.owner.catalog_generation;
    const writes = f.owner.counters.written;
    @constCast(f.catalog.rules.ids.?)[1] = @constCast(f.catalog.rules.ids.?)[0];
    try eq(@as(u32, @backingInt(p.Code.invalid_contract)), api.rm_metric_owner_replace_catalog(f.owner, &f.catalog, f.work.ptr, f.work.len, 200, null));
    try eq(generation, f.owner.catalog_generation);
    try eq(writes, f.owner.counters.written);
    try expect(f.owner.work.region == null);
    f.catalog.rules.count = 3;
    var failure: p.Failure = undefined;
    try eq(@as(u32, @backingInt(p.Code.capacity_exceeded)), api.rm_metric_owner_replace_catalog(f.owner, &f.catalog, f.work.ptr, f.work.len, 200, &failure));
    try eq(p.Family.rules, failure.family);
    try eq(@as(u64, 3), failure.required);
    try eq(@as(u64, 2), failure.limit);
    try eq(generation, f.owner.catalog_generation);
}

test "public position11 is authoritative and absence has the declared exact kind" {
    var f = try Fixture.init(3);
    defer f.deinit();
    f.kind(0, 2);
    f.kind(1, 5);
    f.kind(2, 6);
    try f.reload();
    const kinds = [_]u32{ 2, 5, 6 };
    const raw = try st.table(f.owner, 0);
    for (kinds, 0..) |kind, row| {
        try eq(kind, raw.load(u32, 3, @intCast(row)));
        try eq(@as(u32, 3), try f.publicStatus(@intCast(row)));
    }
    var r = try f.plan(&.{0}, 200);
    current(r, 0, 2, @bitCast(@as(i64, -73)));
    try f.commit(r);
    try eq(@as(u32, 1), try f.publicStatus(0));
    r = try f.plan(&.{0}, 300);
    try f.commit(r);
    try eq(@as(u32, 2), try f.publicStatus(0));
    const result = try st.resultTable(f.owner);
    try eq(@as(u32, 3), result.load(u32, 4, 0));
    r = try f.plan(&.{1}, 400);
    r.observations.status.?[0] = 3;
    try f.commit(r);
    try eq(@as(u32, 5), try f.publicStatus(1));
    try eq(@as(u32, 4), (try st.resultTable(f.owner)).load(u32, 4, 1));
    r = try f.plan(&.{2}, 500);
    r.observations.status.?[0] = 4;
    try f.commit(r);
    try eq(@as(u32, 4), try f.publicStatus(2));
    try eq(@as(u32, 5), (try st.resultTable(f.owner)).load(u32, 4, 2));
    // An absent reference still locates checked declared-type metadata.
    const view = try st.resultTable(f.owner);
    const source = view.load(u32, 9, 1);
    const row = view.load(u32, 10, 1);
    const selected = view.load(u64, 1, 1);
    const table = try st.table(f.owner, source);
    try eq(selected, table.load(u64, 15, row));
    try eq(view.load(u64, 8, 1), table.load(u64, 0, row));
    try eq(@as(u32, 5), table.load(u32, 3, row));
}

test "all candidate rules of a logical metric must agree in declared value kind" {
    var f = try Fixture.init(2);
    defer f.deinit();
    const before = f.owner.counters.written;
    const generation = f.owner.catalog_generation;
    f.kind(1, 2);
    @constCast(f.catalog.rules.metric_ids.?)[1] = 1000;
    f.catalog.metrics.count = 1;
    var failure: p.Failure = undefined;
    try eq(@as(u32, @backingInt(p.Code.invalid_contract)), api.rm_metric_owner_replace_catalog(f.owner, &f.catalog, f.work.ptr, f.work.len, 200, &failure));
    try eq(p.Family.metrics, failure.family);
    try eq(before, f.owner.counters.written);
    try eq(generation, f.owner.catalog_generation);
}

fn copyFor(f: *Fixture) !ss.Selection {
    const a = f.arena.allocator();
    const h = try ss.describe(f.region);
    const selected_sources = try a.alloc(u32, h.source_count);
    for (selected_sources, 0..) |*source, index| source.* = @intCast(index);
    const size = try ss.selectionSize(f.region, selected_sources);
    const data = try a.alloc(u8, @intCast(size.data_bytes));
    @memset(data, 0xa5);
    return ss.Selection.init(f.region, selected_sources, try a.alloc(ss.SourceCopy, h.source_count), try a.alloc(ss.SelectedPosition, size.position_count), data);
}
fn payloadLength(copy: *const ss.Selection, position: u32) !usize {
    return switch (ss.positionValue(copy, position)) {
        .value => |value| value.bytes.len,
        else => error.ExpectedValue,
    };
}

test "actual SharedSection copies zero scalar payload bytes and shrink to highest live extent" {
    var f = try Fixture.init(2);
    defer f.deinit();
    var copy = try copyFor(&f);
    const t = try st.table(f.owner, 0);
    const payload = copy.positionSlice()[t.binding.positions[9]];
    try eq(t.binding.positions[9], payload.index);
    var r = try f.plan(&.{0}, 200);
    current(r, 0, 3, 99);
    try f.commit(r);
    _ = ss.read(f.region, &copy, t.binding.rows_source, 10);
    _ = ss.read(f.region, &copy, t.binding.value_source, 10);
    try eq(@as(usize, 0), try payloadLength(&copy, t.binding.positions[9]));
    for (copy.data.?[payload.offset..][0..payload.entry.spec.size]) |byte| try eq(@as(u8, 0xa5), byte);
    f.kind(0, 5);
    f.kind(1, 5);
    try f.reload();
    r = try f.plan(&.{ 0, 1 }, 300);
    current(r, 0, 5, 0);
    current(r, 1, 5, 0);
    @memcpy(r.payload.?[0..12], "abcde1234567");
    r.observations.payload_lengths.?[0] = 5;
    r.observations.payload_offsets.?[1] = 5;
    r.observations.payload_lengths.?[1] = 7;
    try f.commit(r);
    _ = ss.read(f.region, &copy, t.binding.rows_source, 10);
    _ = ss.read(f.region, &copy, t.binding.value_source, 10);
    try eq(@as(usize, 12), try payloadLength(&copy, t.binding.positions[9]));
    try eq(@as(u8, 0xa5), copy.data.?[payload.offset + 12]);
    r = try f.plan(&.{1}, 400);
    current(r, 0, 5, 0);
    @memcpy(r.payload.?[0..2], "XY");
    r.observations.payload_lengths.?[0] = 2;
    try f.commit(r);
    _ = ss.read(f.region, &copy, t.binding.value_source, 10);
    try eq(@as(usize, 7), try payloadLength(&copy, t.binding.positions[9]));
    r = try f.plan(&.{1}, 500);
    current(r, 0, 5, 0);
    r.observations.presence.?[0] = 2;
    try f.commit(r);
    _ = ss.read(f.region, &copy, t.binding.value_source, 10);
    try eq(@as(usize, 5), try payloadLength(&copy, t.binding.positions[9]));
    r = try f.plan(&.{0}, 600);
    current(r, 0, 5, 0);
    r.observations.presence.?[0] = 2;
    try f.commit(r);
    _ = ss.read(f.region, &copy, t.binding.value_source, 10);
    try eq(@as(usize, 0), try payloadLength(&copy, t.binding.positions[9]));
}

test "payload work is an explicit pool independent of scalar row capacity" {
    var f = try Fixture.initBudget(400, 0);
    defer f.deinit();
    const r = try f.plan(&.{0}, 200);
    try eq(@as(u64, 0), r.payload_capacity);
    current(r, 0, 3, 42);
    try f.commit(r);
    var config = f.owner.config;
    var zero: p.Sizes = undefined;
    var pool: p.Sizes = undefined;
    try eq(@as(u32, 0), api.rm_metric_owner_required_sizes(&config, &zero, null));
    config.payload_work_bytes = 4096;
    try eq(@as(u32, 0), api.rm_metric_owner_required_sizes(&config, &pool, null));
    try eq(@as(u64, 4096), pool.work - zero.work);
}

test "retired then reintroduced row cannot resurrect an ancient identity generation guard" {
    var f = try Fixture.init(1);
    defer f.deinit();
    const r = try f.plan(&.{0}, 200);
    current(r, 0, 3, 42);
    try f.commit(r);
    const old_generation = try f.value(5, 0);
    const old_identity = (try st.table(f.owner, 0)).load(u64, 0, 0);
    f.catalog.rules.count = 0;
    f.catalog.metrics.count = 0;
    try f.reload();
    f.catalog.rules.count = 1;
    f.catalog.metrics.count = 1;
    try f.reload();
    const table = try st.table(f.owner, 0);
    try eq(old_identity, table.load(u64, 0, 0));
    try expect(table.load(u64, 5, 0) > old_generation);
    try eq(@as(u32, 0), table.load(u32, 4, 0));
    try eq(@as(u32, 3), try f.publicStatus(0));
}

test "stop resume preserves proven good data resets baseline and excludes old owner tokens" {
    var f = try Fixture.init(1);
    defer f.deinit();
    var r = try f.plan(&.{0}, 200);
    current(r, 0, 3, 9_007_199_254_740_993);
    try f.commit(r);
    const old_token = r.token;
    const generation = try f.value(5, 0);
    const rows = (try st.table(f.owner, 0)).identitySequence();
    try f.rebind(300);
    try eq(rows, (try st.table(f.owner, 0)).identitySequence());
    try eq(generation, try f.value(5, 0));
    try eq(@as(u32, 2), try f.publicStatus(0));
    try expect(!f.owner.rules[0].baseline.valid);
    r = try f.plan(&.{0}, 400);
    try expect(r.token > old_token);
    try eq(@as(u32, @backingInt(p.Code.stale_plan)), api.rm_metric_owner_prepare(f.owner, old_token, null));
    r = try f.plan(&.{0}, 500);
    try f.commit(r);
    try eq(@as(u32, 2), try f.publicStatus(0));
    try eq(@as(u64, 9_007_199_254_740_993), try f.value(1, 0));
    try eq(generation, try f.value(5, 0));
    const row_sequence = (try st.table(f.owner, 0)).identitySequence();
    const table = try st.table(f.owner, 0);
    try eq(@as(u32, 0), api.rm_metric_owner_unbind_at(f.owner, 600, null));
    const value = table.layout.source(f.region, table.binding.value_source);
    try eq(ss.SourceState.unavailable, value.state);
    try eq(p.no_subscription_reason, value.reason);
    try eq(row_sequence, table.identitySequence());
    try expect(value.sequence & ss.ended_bit == 0);
    try eq(@as(u32, @backingInt(p.Code.closed)), api.rm_metric_owner_abort_detach(f.owner, null));
}

test "commit receipt reports exact queries affected IDs and outside-request aliases without retaining work" {
    var f = try Fixture.init(3);
    defer f.deinit();
    @constCast(f.catalog.rules.source_ids.?)[2] = 20;
    try f.reload();
    var r = try f.plan(&.{ 0, 1, 2 }, 200);
    current(r, 0, 3, 1);
    current(r, 1, 3, 2);
    current(r, 2, 3, 3);
    try f.commit(r);
    const untouched = try f.resultValue(7, 2);
    r = try f.plan(&.{0}, 300);
    r.observations.flags.?[0] = 8;
    try eq(@as(u32, 0), api.rm_metric_owner_prepare(f.owner, r.token, null));
    var borrowed: ?*const p.Receipt = null;
    try eq(@as(u32, 0), api.rm_metric_owner_receipt_view(f.owner, r.token, &borrowed, null));
    try eq(@as(u32, 0), borrowed.?.committed);
    var receipt: p.Receipt = undefined;
    try eq(@as(u32, 0), api.rm_metric_owner_commit_receipt(f.owner, r.token, 300, &receipt, null));
    try eq(@as(u32, 1), receipt.committed);
    try eq(@as(u32, 1), borrowed.?.committed);
    try eq(@as(u32, 2), receipt.affected_count);
    try eq(@as(u32, 1), receipt.query_count);
    try eq(@as(u64, 100), receipt.queries.rule_ids.?[0]);
    try eq(@as(u64, 10), receipt.queries.source_ids.?[0]);
    try eq(@as(u64, 1), receipt.queries.incarnations.?[0]);
    try eq(@as(u64, 1000), receipt.metric_ids.?[0]);
    try eq(@as(u64, 1001), receipt.metric_ids.?[1]);
    try eq(@as(u32, 1), receipt.invalidated_alias_count);
    try eq(@as(u32, 1), receipt.invalidated_aliases.?[0]);
    try eq(untouched, try f.resultValue(7, 2));
    try expect(f.owner.work.region == null);
    try eq(@as(u32, 3), try f.publicStatus(0));
    try eq(@as(u32, 3), try f.publicStatus(1));
    try eq(@as(u32, 1), try f.publicStatus(2));
    try eq(@as(u32, @backingInt(p.Code.stale_plan)), api.rm_metric_owner_commit_receipt(f.owner, r.token, 400, &receipt, null));
    try eq(@as(u32, 0), receipt.committed);
    r = try f.plan(&.{0}, 500);
    r.observations.status.?[0] = 3;
    try f.commit(r);
    r = try f.plan(&.{0}, 600);
    try eq(@as(u32, 0), r.count);
    try eq(@as(u32, 0), api.rm_metric_owner_prepare(f.owner, r.token, null));
    try eq(@as(u32, 0), api.rm_metric_owner_commit_receipt(f.owner, r.token, 600, &receipt, null));
    try eq(@as(u32, 0), receipt.query_count);
}

test "CPU raw golden stream equals old native submitCpuCounter without an old owner allocation" {
    const op = @import("../metric_snapshot/protocol.zig");
    const os = @import("../metric_snapshot/state.zig");
    const oc = @import("../metric_snapshot/catalog.zig");
    const observation = @import("../metric_snapshot/observation.zig");
    const Frame = struct { idle: u64 = 0, kernel: u64 = 0, user: u64 = 0, clock: u64 = 0, frequency: u64 = 0, status: u32 = 1, incarnation: u64 = 1 };
    const frames = [_]Frame{
        .{ .idle = 100, .kernel = 200, .user = 100, .clock = 1000, .frequency = 1000 },
        .{ .idle = 150, .kernel = 300, .user = 200, .clock = 2000, .frequency = 1000 },
        .{ .status = 2 },
        .{ .idle = 170, .kernel = 350, .user = 250, .clock = 2500, .frequency = 1000 },
        .{ .idle = 10, .kernel = 20, .user = 10, .clock = 3000, .frequency = 1000 },
        .{ .idle = 20, .kernel = 40, .user = 30, .clock = 4000, .frequency = 1000 },
        .{ .idle = 30, .kernel = 60, .user = 50, .clock = 3999, .frequency = 1000 },
        .{ .idle = 40, .kernel = 80, .user = 70, .clock = 5000, .frequency = 2000 },
        .{ .idle = 50, .kernel = 100, .user = 90, .clock = 7000, .frequency = 2000 },
        .{ .idle = 60, .kernel = 120, .user = 110, .clock = 7001, .frequency = 2_000_000 },
        .{ .idle = 70, .kernel = 140, .user = 130, .clock = 7002, .frequency = 2_000_000 },
        .{ .clock = 8000, .frequency = 2_000_000 },
        .{ .kernel = std.math.maxInt(u64), .user = std.math.maxInt(u64), .clock = 10000, .frequency = 2_000_000 },
        .{ .idle = 1, .kernel = 2, .user = 1, .clock = 11000, .frequency = 1000, .incarnation = 2 },
        .{ .idle = 11, .kernel = 22, .user = 21, .clock = 12000, .frequency = 1000, .incarnation = 2 },
    };
    var f = try Fixture.init(1);
    defer f.deinit();
    f.kind(0, 1);
    @constCast(f.catalog.rules.kinds.?)[0] = 1;
    try f.reload();
    // Oracle is only the existing old CPU function on bounded test records.
    // Neither Session.create nor any old bank allocator/current owner is used.
    var sources = [_]oc.SourceRecord{.{ .active = true, .source_incarnation = 1 }};
    sources[0].definition.source_handle = 10;
    var rules = [_]oc.RuleRecord{.{ .active = true }};
    rules[0].definition.rule_handle = 100;
    rules[0].definition.metric_handle = 1000;
    rules[0].definition.source_handle = 10;
    rules[0].definition.scope_handle = 1;
    rules[0].definition.metric_kind = 1;
    rules[0].definition.value_kind = 1;
    rules[0].definition.capability_mask = 1;
    var index = [_]u32{0};
    var requested = [_]bool{true};
    var slots = [_]u32{oc.no_index};
    var values: [1]op.ObservationInput = undefined;
    var legacy: os.Session = undefined;
    legacy.mutex = std.os.windows.SRWLOCK_INIT;
    legacy.phase = .completion_open;
    legacy.active = .{ .sources = &sources, .rules = &rules, .source_index = &index, .rule_index = &index, .source_count = 1, .rule_count = 1 };
    legacy.requested_rule_flags = &requested;
    legacy.observation_slot_by_rule = &slots;
    legacy.observations = &values;
    var good: ?u64 = null;
    for (frames, 0..) |frame, fi| {
        const utc: i64 = @intCast(1000 + fi * 1000);
        if (sources[0].source_incarnation != frame.incarnation) {
            @constCast(f.catalog.sources.incarnations.?)[0] = frame.incarnation;
            try f.reload();
            good = null;
        }
        legacy.completion = std.mem.zeroes(op.CompletionHeader);
        legacy.completion.cpu_counter_count = 1;
        legacy.completion.source_incarnation = frame.incarnation;
        legacy.completion.source_observation_sequence = fi + 1;
        legacy.completion.captured_at_milliseconds = @intCast(utc);
        legacy.observation_count = 0;
        legacy.pending_cpu_counter_present = false;
        slots[0] = oc.no_index;
        var input = std.mem.zeroes(op.CpuCounterInput);
        input.struct_size = @sizeOf(op.CpuCounterInput);
        input.rule_handle = 100;
        input.metric_handle = 1000;
        input.source_handle = 10;
        input.source_incarnation = frame.incarnation;
        input.source_observation_sequence = fi + 1;
        input.observed_at_milliseconds = @intCast(utc);
        input.counter_contract_version = op.cpu_counter_contract_version;
        input.status = frame.status;
        if (frame.status == 1) {
            input.valid_mask = op.CpuCounterValid.required;
            input.capability_mask = 1;
            input.idle_ticks = frame.idle;
            input.kernel_ticks = frame.kernel;
            input.user_ticks = frame.user;
            input.monotonic_ticks = frame.clock;
            input.monotonic_ticks_per_second = frame.frequency;
        }
        try eq(@import("../../common/result_codes.zig").ResultCode.ok, observation.submitCpuCounter(&legacy, &input));
        const r = try f.plan(&.{0}, utc);
        r.observations.status.?[0] = frame.status;
        if (frame.status == 1) {
            current(r, 0, 1, frame.idle);
            r.observations.flags.?[0] = 16;
            r.observations.high.?[0] = frame.kernel;
            r.observations.reference.?[0] = frame.user;
            r.observations.monotonic_ns.?[0] = frame.clock;
            r.observations.tick_frequency.?[0] = frame.frequency;
        }
        try f.commit(r);
        const table = try st.table(f.owner, 0);
        try eq(values[0].status, table.load(u32, 10, 0));
        if (values[0].status == 1) {
            good = values[0].value_bits;
            try eq(values[0].value_bits, try f.value(1, 0));
            try eq(values[0].sample_duration_milliseconds, try f.value(7, 0));
            try eq(@as(u32, 1), try f.publicStatus(0));
        } else if (good) |bits| {
            try eq(bits, try f.value(1, 0));
            try eq(@as(u32, 2), try f.publicStatus(0));
        } else try eq(@as(u32, if (values[0].status == 4) 4 else 3), try f.publicStatus(0));
        // Exact old stageFinalizedSource baseline transition for the next call.
        if (frame.status == 1) {
            sources[0].cpu_baseline_valid = true;
            sources[0].cpu_idle_ticks = frame.idle;
            sources[0].cpu_kernel_ticks = frame.kernel;
            sources[0].cpu_user_ticks = frame.user;
            sources[0].cpu_monotonic_ticks = frame.clock;
            sources[0].cpu_monotonic_ticks_per_second = frame.frequency;
            sources[0].cpu_counter_contract_version = op.cpu_counter_contract_version;
        }
        sources[0].source_incarnation = frame.incarnation;
    }
}

test "CPU flags validate raw kind shape and retain normalized native scalar input" {
    var f = try Fixture.init(1);
    defer f.deinit();
    f.kind(0, 1);
    @constCast(f.catalog.rules.kinds.?)[0] = 1;
    try f.reload();
    var r = try f.plan(&.{0}, 200);
    current(r, 0, 1, @bitCast(@as(f64, 23.5)));
    try f.commit(r);
    try eq(@as(u32, 1), try f.publicStatus(0));
    const generation = try f.value(5, 0);
    r = try f.plan(&.{0}, 300);
    current(r, 0, 1, 11);
    r.observations.flags.?[0] = 16;
    r.observations.high.?[0] = 10;
    r.observations.reference.?[0] = 1;
    r.observations.monotonic_ns.?[0] = 1000;
    r.observations.tick_frequency.?[0] = 1000;
    try eq(@as(u32, @backingInt(p.Code.invalid_contract)), api.rm_metric_owner_prepare(f.owner, r.token, null));
    try eq(generation, try f.value(5, 0));
}

test "row moves and absent type changes reject old copied reference guards" {
    var f = try Fixture.init(2);
    defer f.deinit();
    const r = try f.plan(&.{ 0, 1 }, 200);
    current(r, 0, 3, 111);
    current(r, 1, 3, 222);
    try f.commit(r);
    const old_generation = try f.value(5, 0);
    const old_identity = (try st.table(f.owner, 0)).load(u64, 0, 0);
    inline for (.{ "ids", "source_ids", "metric_ids", "identities", "minimum_bits", "maximum_bits", "capability_masks" }) |field| std.mem.swap(u64, &@constCast(@field(f.catalog.rules, field).?)[0], &@constCast(@field(f.catalog.rules, field).?)[1]);
    inline for (.{ "kinds", "flags", "priorities", "value_kinds" }) |field| std.mem.swap(u32, &@constCast(@field(f.catalog.rules, field).?)[0], &@constCast(@field(f.catalog.rules, field).?)[1]);
    try f.reload();
    try eq(@as(u64, 111), try f.value(1, 1));
    try expect(try f.value(5, 1) > old_generation);
    inline for (.{ "ids", "source_ids", "metric_ids", "identities", "minimum_bits", "maximum_bits", "capability_masks" }) |field| std.mem.swap(u64, &@constCast(@field(f.catalog.rules, field).?)[0], &@constCast(@field(f.catalog.rules, field).?)[1]);
    inline for (.{ "kinds", "flags", "priorities", "value_kinds" }) |field| std.mem.swap(u32, &@constCast(@field(f.catalog.rules, field).?)[0], &@constCast(@field(f.catalog.rules, field).?)[1]);
    try f.reload();
    const table = try st.table(f.owner, 0);
    try eq(old_identity, table.load(u64, 0, 0));
    try expect(table.load(u64, 5, 0) > old_generation);
    try eq(@as(u64, 111), try f.value(1, 0));
    const prior = try f.value(5, 0);
    const rows = table.identitySequence();
    f.kind(0, 2);
    try f.reload();
    const result = try st.resultTable(f.owner);
    try eq(rows, (try st.table(f.owner, 0)).identitySequence());
    try eq(@as(u32, 3), try f.publicStatus(0));
    try expect(result.load(u64, 3, 0) > prior);
    try eq(result.load(u64, 3, 0), try f.value(5, 0));
    try eq(@as(u32, 2), (try st.table(f.owner, 0)).load(u32, 3, 0));
}

test "attempted provenance distinguishes UTC zero failure from catalog and survives exact rebind" {
    const prov = @import("provenance.zig");
    var f = try Fixture.init(1);
    defer f.deinit();
    try expect(!f.owner.metrics[0].attempted);
    var result = try st.resultTable(f.owner);
    try expect(!prov.attempted(&result, 0));
    try f.reload();
    try expect(!f.owner.metrics[0].attempted);
    @constCast(f.catalog.sources.availability.?)[0] = 3;
    try f.reload();
    const raw = try f.plan(&.{0}, 0);
    try eq(@as(u32, 0), raw.count);
    try eq(@as(u32, 0), api.rm_metric_owner_prepare(f.owner, raw.token, null));
    var receipt: p.Receipt = undefined;
    try eq(@as(u32, 0), api.rm_metric_owner_commit_receipt(f.owner, raw.token, 0, &receipt, null));
    try eq(@as(u32, 0), receipt.query_count);
    try eq(@as(u32, 1), receipt.affected_count);
    try eq(@as(u32, 5), try f.publicStatus(0));
    try expect(f.owner.metrics[0].attempted);
    result = try st.resultTable(f.owner);
    try eq(@as(i64, 0), result.load(i64, 6, 0));
    try expect(prov.attempted(&result, 0));
    try f.rebind(10);
    try expect(f.owner.metrics[0].attempted);
    result = try st.resultTable(f.owner);
    try eq(@as(i64, 0), result.load(i64, 6, 0));
    try expect(prov.attempted(&result, 0));
    @constCast(f.catalog.metrics.identities.?)[0] += 10;
    try f.reload();
    try expect(!f.owner.metrics[0].attempted);
    result = try st.resultTable(f.owner);
    try expect(!prov.attempted(&result, 0));
}

test "query receipts exclude adapter skipped rows after identity validation" {
    var f = try Fixture.init(2);
    defer f.deinit();
    const raw = try f.plan(&.{ 0, 1 }, 200);
    raw.observations.status.?[0] = 4;
    current(raw, 1, 3, 42);
    try eq(@as(u32, 0), api.rm_metric_owner_prepare(f.owner, raw.token, null));
    var receipt: p.Receipt = undefined;
    try eq(@as(u32, 0), api.rm_metric_owner_commit_receipt(f.owner, raw.token, 200, &receipt, null));
    try eq(@as(u32, 1), receipt.query_count);
    try eq(@as(u64, 101), receipt.queries.rule_ids.?[0]);
    try eq(@as(u64, 1001), receipt.queries.metric_ids.?[0]);
    try eq(@as(u32, 2), receipt.affected_count);
    try eq(@as(u32, 4), try f.publicStatus(0));
    try eq(@as(u32, 1), try f.publicStatus(1));
    try eq(@as(u64, 42), try f.value(1, 1));
}

test "unaligned C active and work pointers reject before aligned access and owned span caps are exact" {
    var f = try Fixture.init(1);
    defer f.deinit();
    var config = f.owner.config;
    var failure: p.Failure = undefined;
    var owner: ?*st.Owner = null;
    try eq(@as(u32, @backingInt(p.Code.invalid_contract)), api.rm_metric_owner_bind(&config, f.active.ptr + 1, f.active.len - 1, &f.bindings, 2, &f.result, &owner, &failure));
    try expect(owner == null);
    var raw: p.Round = undefined;
    try eq(@as(u32, @backingInt(p.Code.invalid_contract)), api.rm_metric_owner_plan(f.owner, &.{0}, 1, f.work.ptr + 1, f.work.len - 1, 200, &raw, &failure));
    try expect(f.owner.work.region == null);
    const a = f.arena.allocator();
    const oversized_active = try a.alignedAlloc(u8, .@"8", f.active.len + 1);
    config.active_limit = f.active.len;
    try eq(@as(u32, @backingInt(p.Code.capacity_exceeded)), api.rm_metric_owner_bind(&config, oversized_active.ptr, oversized_active.len, &f.bindings, 2, &f.result, &owner, &failure));
    try eq(p.Family.active, failure.family);
    try eq(@as(u64, oversized_active.len), failure.required);
    try eq(config.active_limit, failure.limit);
    const oversized_work = try a.alignedAlloc(u8, .@"8", f.work.len + 1);
    f.owner.config.work_limit = f.work.len;
    try eq(@as(u32, @backingInt(p.Code.capacity_exceeded)), api.rm_metric_owner_plan(f.owner, &.{0}, 1, oversized_work.ptr, oversized_work.len, 200, &raw, &failure));
    try eq(p.Family.work, failure.family);
    try eq(@as(u64, oversized_work.len), failure.required);
    try eq(f.owner.config.work_limit, failure.limit);
    try eq(@as(u32, @backingInt(p.Code.capacity_exceeded)), api.rm_metric_owner_replace_catalog(f.owner, &f.catalog, oversized_work.ptr, oversized_work.len, 200, &failure));
    try expect(f.owner.work.region == null);
    // Global mapping length is deliberately much larger than admitted active;
    // only the owned active/work extents are compared with metric budgets.
    try expect(f.region.len > f.active.len);
}

fn route(o: *st.Owner, mi: u32) !p.ResultRoute {
    var out: p.ResultRoute = undefined;
    try eq(@as(u32, 0), api.rm_metric_owner_result_route(o, mi, &out, null));
    return out;
}

test "one owner routes possible dependencies and bound source identity through group moves and reorder" {
    var f = try Fixture.init(4);
    var admin = Fixture.init(4) catch |err| {
        f.deinit();
        return err;
    };
    defer admin.deinit();
    // The grouped owner borrows admin.region below. Drain it before the admin
    // arena is released, including assertion-error exits before explicit stop.
    defer f.deinit();
    const config = f.owner.config;
    try eq(@as(u32, 0), api.rm_metric_owner_unbind(f.owner, null));
    try eq(@as(u32, 0), api.rm_metric_owner_unbind(admin.owner, null));
    @constCast(f.catalog.rules.source_ids.?)[1] = 20;
    @constCast(f.catalog.rules.source_ids.?)[3] = 20;
    @constCast(f.catalog.rules.metric_ids.?)[3] = 1002;
    @constCast(f.catalog.rules.priorities.?)[2] = 2;
    f.catalog.metrics.count = 3;
    var declarations = [_]p.SourceBinding{
        .{ .source_id = 10, .access_region = 0, .table = f.bindings[0] },
        .{ .source_id = 20, .access_region = 0, .table = f.bindings[1] },
    };
    const groups = [_]p.TableBinding{ f.result, admin.result };
    var owner: ?*st.Owner = null;
    try eq(@as(u32, 0), api.rm_metric_owner_bind_grouped(&config, f.active.ptr, f.active.len, &declarations, 2, &groups, 2, &owner, null));
    f.owner = owner.?;
    try f.reload();
    try eq(@as(u32, 0), (try route(f.owner, 2)).group);
    var raw = try f.plan(&.{ 0, 1, 2 }, 200);
    try eq(@as(u64, 103), raw.queries.rule_ids.?[2]);
    current(raw, 0, 3, 100);
    current(raw, 1, 3, 200);
    current(raw, 2, 3, 300);
    try f.commit(raw);
    const normal_raw = try st.sourceTableByBinding(f.owner, 0);
    const raw_generation = normal_raw.load(u64, 5, 0);
    const raw_identity_seq = normal_raw.identitySequence();
    var old_copy = try copyFor(&f);
    _ = ss.read(f.region, &old_copy, f.result.rows_source, 10);
    _ = ss.read(f.region, &old_copy, f.result.value_source, 10);
    const old_route = try route(f.owner, 1);
    const old_rows = (try st.resultGroupTable(f.owner, 0)).identitySequence();
    declarations[1].access_region = 1;
    const rejected_before = f.owner.counters.written;
    try eq(@as(u32, @backingInt(p.Code.invalid_contract)), api.rm_metric_owner_replace_catalog_bound(f.owner, &f.catalog, &declarations, 2, f.work.ptr, f.work.len, 250, null));
    try eq(rejected_before, f.owner.counters.written);
    try eq(@as(u32, 0), (try route(f.owner, 1)).group);
    try eq(old_rows, (try st.resultGroupTable(f.owner, 0)).identitySequence());
    declarations[1].table = admin.bindings[1];
    try eq(@as(u32, 0), api.rm_metric_owner_replace_catalog_bound(f.owner, &f.catalog, &declarations, 2, f.work.ptr, f.work.len, 300, null));
    try eq(@as(u32, 0), (try route(f.owner, 0)).group);
    try eq(@as(u32, 1), (try route(f.owner, 1)).group);
    try eq(@as(u32, 1), (try route(f.owner, 2)).group);
    try eq(@as(u32, 0), (try route(f.owner, 1)).row);
    try eq(@as(u32, 1), (try route(f.owner, 2)).row);
    try expect((try st.resultGroupTable(f.owner, 0)).identitySequence() != old_rows);
    try eq(raw_generation, (try st.sourceTableByBinding(f.owner, 0)).load(u64, 5, 0));
    try eq(raw_identity_seq, (try st.sourceTableByBinding(f.owner, 0)).identitySequence());
    try eq(@as(u64, 100), (try st.sourceTableByBinding(f.owner, 0)).load(u64, 1, 0));
    const old_table = try @import("storage.zig").Table.init(f.bindings[1], 0, false);
    try eq(ss.SourceState.unavailable, old_table.layout.source(f.region, f.bindings[1].value_source).state);
    try eq(p.no_subscription_reason, old_table.layout.source(f.region, f.bindings[1].value_source).reason);
    // Refresh membership without values: old copied result references are now
    // rejected by SharedSection's actual identity sequence, not a C# decision.
    _ = ss.read(f.region, &old_copy, f.result.rows_source, 10);
    switch (ss.positionValue(&old_copy, f.result.positions[2])) {
        .not_arrived => {},
        else => return error.ExpectedNotArrived,
    }
    _ = ss.read(f.region, &old_copy, f.result.value_source, 10);
    switch (ss.positionValue(&old_copy, f.result.positions[2])) {
        .value => |v| try expect(v.length <= old_route.row),
        else => return error.ExpectedValue,
    }
    const admin_before = try st.resultGroupTable(f.owner, 1);
    const admin_sequence = admin_before.layout.source(admin_before.bytes(), admin_before.binding.value_source).sequence;
    const normal_rows = (try st.resultGroupTable(f.owner, 0)).identitySequence();
    const admin_rows = admin_before.identitySequence();
    raw = try f.plan(&.{0}, 400);
    current(raw, 0, 3, 101);
    try f.commit(raw);
    try eq(admin_sequence, admin_before.layout.source(admin_before.bytes(), admin_before.binding.value_source).sequence);
    try eq(normal_rows, (try st.resultGroupTable(f.owner, 0)).identitySequence());
    try eq(admin_rows, (try st.resultGroupTable(f.owner, 1)).identitySequence());
    raw = try f.plan(&.{2}, 500);
    try eq(@as(u64, 103), raw.queries.rule_ids.?[0]);
    _ = api.rm_metric_owner_abort_detach(f.owner, null);
    // Prefer the NORMAL candidate, but the mixed metric stays ADMIN because
    // the possible dependency is structural, independent of current winner.
    @constCast(f.catalog.rules.priorities.?)[2] = 1;
    @constCast(f.catalog.rules.priorities.?)[3] = 2;
    try f.reload();
    raw = try f.plan(&.{2}, 600);
    try eq(@as(u64, 102), raw.queries.rule_ids.?[0]);
    current(raw, 0, 3, 999);
    try f.commit(raw);
    var location = try st.resultLocation(f.owner, 2);
    try eq(@as(u32, 1), location.group);
    try eq(@as(u32, 0), location.table.load(u32, 9, location.row));
    try eq(@as(u64, 102), location.table.load(u64, 2, location.row));
    const physical_before = (try st.sourceTableByBinding(f.owner, 0)).load(u64, 5, 1);
    // Reorder BOTH source and metric input columns. Stable creator ordinal9 and
    // physical source values survive; metric indices follow the new catalog.
    inline for (.{ "ids", "incarnations", "capabilities" }) |field| std.mem.swap(u64, &@constCast(@field(f.catalog.sources, field).?)[0], &@constCast(@field(f.catalog.sources, field).?)[1]);
    std.mem.swap(u32, &@constCast(f.catalog.sources.availability.?)[0], &@constCast(f.catalog.sources.availability.?)[1]);
    inline for (.{ "ids", "identities" }) |field| std.mem.swap(u64, &@constCast(@field(f.catalog.metrics, field).?)[0], &@constCast(@field(f.catalog.metrics, field).?)[2]);
    try f.reload();
    try eq(physical_before, (try st.sourceTableByBinding(f.owner, 0)).load(u64, 5, 1));
    location = try st.resultLocation(f.owner, 0);
    try eq(@as(u32, 1), location.group);
    try eq(@as(u32, 0), location.table.load(u32, 9, location.row));
    try eq(@as(u64, 999), (try st.sourceTableByBinding(f.owner, 0)).load(u64, 1, 1));
    try eq(@as(u32, 1), f.owner.sources[0].binding_ordinal);
    try eq(@as(u32, 0), f.owner.sources[1].binding_ordinal);
    var proofs: [4]u64 = undefined;
    for ([_]p.TableBinding{ f.owner.bindings[0].table, f.owner.bindings[1].table, groups[0], groups[1] }, 0..) |b, i| {
        const t = try @import("storage.zig").Table.init(b, 0, i >= 2);
        proofs[i] = t.layout.source(t.bytes(), b.provenance_source).sequence;
    }
    const stopped = [_]p.TableBinding{ f.owner.bindings[0].table, f.owner.bindings[1].table, groups[0], groups[1] };
    try eq(@as(u32, 0), api.rm_metric_owner_unbind_at(f.owner, 700, null));
    try expect(f.owner.work.region == null);
    for (stopped, 0..) |b, i| {
        const bytes: []align(8) u8 = @alignCast(b.region.?[0..@intCast(b.byte_length)]);
        const layout = try ss.layout.Layout.load(bytes);
        try eq(proofs[i], layout.source(bytes, b.provenance_source).sequence);
        for ([_]u32{ b.value_source, b.health_source }) |source| {
            const entry = layout.source(bytes, source);
            try eq(ss.SourceState.unavailable, entry.state);
            try eq(p.no_subscription_reason, entry.reason);
            try expect(entry.sequence & ss.ended_bit == 0);
        }
    }
}

test "legacy first-catalog source declarations remain stable on source reorder and unknown ID rejects" {
    var f = try Fixture.init(2);
    defer f.deinit();
    @constCast(f.catalog.rules.source_ids.?)[1] = 20;
    try f.reload();
    var raw = try f.plan(&.{ 0, 1 }, 200);
    current(raw, 0, 3, 111);
    current(raw, 1, 3, 222);
    try f.commit(raw);
    const physical0 = try st.sourceTableByBinding(f.owner, 0);
    const physical1 = try st.sourceTableByBinding(f.owner, 1);
    const generation0 = physical0.load(u64, 5, 0);
    const generation1 = physical1.load(u64, 5, 0);
    inline for (.{ "ids", "incarnations", "capabilities" }) |field| std.mem.swap(u64, &@constCast(@field(f.catalog.sources, field).?)[0], &@constCast(@field(f.catalog.sources, field).?)[1]);
    std.mem.swap(u32, &@constCast(f.catalog.sources.availability.?)[0], &@constCast(f.catalog.sources.availability.?)[1]);
    try f.reload();
    try eq(@as(u64, 111), (try st.sourceTableByBinding(f.owner, 0)).load(u64, 1, 0));
    try eq(@as(u64, 222), (try st.sourceTableByBinding(f.owner, 1)).load(u64, 1, 0));
    try eq(generation0, (try st.sourceTableByBinding(f.owner, 0)).load(u64, 5, 0));
    try eq(generation1, (try st.sourceTableByBinding(f.owner, 1)).load(u64, 5, 0));
    const first = try st.resultLocation(f.owner, 0);
    const second = try st.resultLocation(f.owner, 1);
    try eq(@as(u32, 0), first.table.load(u32, 9, first.row));
    try eq(@as(u32, 1), second.table.load(u32, 9, second.row));
    raw = try f.plan(&.{1}, 300);
    try eq(@as(u64, 20), raw.queries.source_ids.?[0]);
    _ = api.rm_metric_owner_abort_detach(f.owner, null);
    const before = f.owner.counters.written;
    const catalog_generation = f.owner.catalog_generation;
    @constCast(f.catalog.sources.ids.?)[0] = 30;
    try eq(@as(u32, @backingInt(p.Code.invalid_contract)), api.rm_metric_owner_replace_catalog(f.owner, &f.catalog, f.work.ptr, f.work.len, 400, null));
    try eq(before, f.owner.counters.written);
    try eq(catalog_generation, f.owner.catalog_generation);
    try expect(f.owner.work.region == null);
    var explicit = [_]p.SourceBinding{ f.owner.bindings[0], f.owner.bindings[1] };
    explicit[1].source_id = 30;
    @constCast(f.catalog.rules.source_ids.?)[1] = 30;
    try eq(@as(u32, 0), api.rm_metric_owner_replace_catalog_bound(f.owner, &f.catalog, &explicit, 2, f.work.ptr, f.work.len, 500, null));
    const replaced = try st.sourceTableByBinding(f.owner, 1);
    try eq(@as(u32, 0), replaced.load(u32, 4, 0));
    try expect(replaced.load(u64, 5, 0) > generation1);
    try eq(@as(u64, 111), (try st.sourceTableByBinding(f.owner, 0)).load(u64, 1, 0));
}

test "native explicit unavailable transition resets generic baseline but due omissions do not" {
    var f = try Fixture.init(1);
    defer f.deinit();
    f.kind(0, 1);
    @constCast(f.catalog.rules.kinds.?)[0] = 14;
    try f.reload();
    var raw = try f.plan(&.{0}, 200);
    current(raw, 0, 1, 100);
    raw.observations.high.?[0] = 1000;
    raw.observations.flags.?[0] = 1;
    try f.commit(raw);
    try expect(f.owner.rules[0].baseline.valid);
    raw = try f.plan(&.{}, 300);
    try eq(@as(u32, 0), raw.count);
    try expect(f.owner.rules[0].baseline.valid);
    _ = api.rm_metric_owner_abort_detach(f.owner, null);
    f.owner.sources[0].availability = 2; // Native provider deadline fact.
    raw = try f.plan(&.{0}, 400);
    try eq(@as(u32, 0), raw.count);
    try expect(!f.owner.rules[0].baseline.valid);
    _ = api.rm_metric_owner_abort_detach(f.owner, null);
    f.owner.sources[0].availability = 1;
    raw = try f.plan(&.{0}, 500);
    current(raw, 0, 1, 200);
    raw.observations.high.?[0] = 2000;
    raw.observations.flags.?[0] = 1;
    try f.commit(raw);
    try eq(@as(u32, 3), try f.publicStatus(0));
    try expect(f.owner.rules[0].baseline.valid);
    raw = try f.plan(&.{0}, 600);
    raw.observations.status.?[0] = 3;
    raw.observations.flags.?[0] = 32 | 1; // Counter adapter's valid shape may remain.
    try f.commit(raw);
    try expect(f.owner.sources[0].suppressed);
    try expect(!f.owner.rules[0].baseline.valid);
    try eq(@as(u32, 5), try f.publicStatus(0));
}
