const std = @import("std");
const api = @import("root.zig");
const p = @import("protocol.zig");
const st = @import("state.zig");
const v = @import("vector_inputs.zig");
const ss = @import("rm_shared_section");
const eq = std.testing.expectEqual;
const expect = std.testing.expect;
const Fixture = struct {
    arena: std.heap.ArenaAllocator,
    region: []align(8) u8,
    active: []align(8) u8,
    all_work: []align(8) u8,
    kernel: []align(8) u8,
    side: []align(8) u8,
    owner: *st.Owner,
    binding: v.BoundVector,
    catalog: p.Catalog,
    b: v.Bindings,
    t: v.ScopeTopology,
    fields: []u32,
    scope_indices: []u32,
    keys: []v.Ref,
    parents: []v.Ref,
    pnp: []v.Ref,
    drivers: []u64,
    driver_present: []u32,
    dir: []u64,
    expansions: []u32,
    flags: []u32,
    engine_indices: []u32,
    engine_counts: []u32,
    luids: []u64,
    fn init(mode: u32) !Fixture {
        var arena = std.heap.ArenaAllocator.init(std.testing.allocator);
        errdefer arena.deinit();
        const a = arena.allocator();
        const n: u32 = 3;
        const specs = try a.alloc(ss.PositionSpec, 51);
        var pos: u32 = 0;
        var off: u32 = 0;
        var raw = p.TableBinding{ .region = null, .byte_length = 0, .rows_source = 0, .value_source = 1, .health_source = 2, .provenance_source = 3 };
        var result_binding = p.TableBinding{ .region = null, .byte_length = 0, .rows_source = 4, .value_source = 5, .health_source = 6, .provenance_source = 7 };
        for ([_]*p.TableBinding{ &raw, &result_binding }, 0..) |binding, ti| {
            const used: usize = if (ti == 0) 16 else 12;
            for (0..used) |slot| {
                off = std.mem.alignForward(u32, off, 8);
                const width: u32 = if (ti == 0) (if (slot == 9) 1 else if (slot == 3 or slot == 4 or slot == 10 or slot == 11 or slot == 13) 4 else 8) else (if (slot == 4 or slot == 5 or slot == 9 or slot == 10 or slot == 11) 4 else 8);
                const source = if (slot == 0 or (ti == 0 and slot == 15)) binding.rows_source else if (ti == 0 and slot >= 10 and slot <= 12) binding.health_source else binding.value_source;
                binding.positions[slot] = pos;
                specs[pos] = if (ti == 0 and slot == 9) ss.PositionSpec.stringTable(source, off, 512) else ss.PositionSpec.column(source, off, n, width, if (slot == 0) ss.none else binding.positions[0]);
                off += specs[pos].size;
                pos += 1;
            }
            off = std.mem.alignForward(u32, off, 8);
            binding.provenance_position = pos;
            specs[pos] = ss.PositionSpec.column(binding.provenance_source, off, 8 + n * (if (ti == 0) @as(u32, 12) else 4), 8, ss.none);
            off += specs[pos].size;
            pos += 1;
        }
        var vector_binding = v.BoundVector{ .source_id = 10, .rows_source = 8, .value_source = 9, .health_source = 10 };
        for (0..21) |slot| {
            off = std.mem.alignForward(u32, off, 8);
            const width: u32 = switch (slot) {
                2, 3, 4, 6, 7, 10, 11, 17, 20 => 4,
                14 => 1,
                else => 8,
            };
            const rows = slot <= 4 or (slot >= 13 and slot <= 18);
            const health = (slot >= 10 and slot <= 12) or slot == 20;
            const source: u32 = if (rows) 8 else if (health) 10 else 9;
            vector_binding.positions[slot] = pos;
            specs[pos] = if (slot == 14) ss.PositionSpec.stringTable(source, off, 144 * n) else ss.PositionSpec.column(source, off, n, width, if (slot == 0) ss.none else vector_binding.positions[0]);
            off += specs[pos].size;
            pos += 1;
        }
        const data = std.mem.alignForward(u32, off, 8);
        const region = try a.alignedAlloc(u8, .@"8", @intCast(ss.requiredSize(11, 51, data)));
        var identities: [11]ss.SourceIdentity = undefined;
        for (&identities, 0..) |*id, i| id.* = try ss.SourceIdentity.init("vector_binding-test", @intCast(i + 1), 0, 0);
        try ss.initialize(region, 99, 11, 51, data, &identities, specs);
        raw.region = region.ptr;
        raw.byte_length = region.len;
        result_binding.region = region.ptr;
        result_binding.byte_length = region.len;
        vector_binding.region = region.ptr;
        vector_binding.byte_length = region.len;
        const config = p.Config{ .source_capacity = 1, .rule_capacity = n, .metric_capacity = n, .max_value_bytes = 512, .active_limit = 128 * 1024, .work_limit = 128 * 1024, .payload_work_bytes = 0 };
        var sizes: p.Sizes = undefined;
        try eq(@as(u32, 0), api.rm_metric_owner_required_sizes(&config, &sizes, null));
        const active = try a.alignedAlloc(u8, .@"8", @intCast(sizes.active));
        const prefix = std.mem.alignForward(usize, @intCast(sizes.work), 8);
        const all_work = try a.alignedAlloc(u8, .@"8", prefix + 16384);
        const kernel = all_work[0..@intCast(sizes.work)];
        const side: []align(8) u8 = @alignCast(all_work[prefix..]);
        var owner: ?*st.Owner = null;
        try eq(@as(u32, 0), api.rm_metric_owner_bind(&config, active.ptr, active.len, (&raw)[0..1].ptr, 1, &result_binding, &owner, null));
        const ids = try a.alloc(u64, n);
        const mids = try a.alloc(u64, n);
        const identities_rules = try a.alloc(u64, n);
        const mids_identity = try a.alloc(u64, n);
        const sourceids = try a.alloc(u64, n);
        const kinds = try a.alloc(u32, n);
        const flags = try a.alloc(u32, n);
        const priorities = try a.alloc(u32, n);
        const value_kinds = try a.alloc(u32, n);
        const minimum = try a.alloc(u64, n);
        const maximum = try a.alloc(u64, n);
        const masks = try a.alloc(u64, n);
        for (ids, 0..) |*id, i| {
            id.* = 100 + i;
            mids[i] = 1000 + i;
            identities_rules[i] = 2000 + i;
            mids_identity[i] = 3000 + i;
            sourceids[i] = 10;
            kinds[i] = if (mode == 1 and i > 0) 18 else 13;
            flags[i] = 0;
            priorities[i] = 1;
            value_kinds[i] = 1;
            minimum[i] = @bitCast(@as(f64, 0));
            maximum[i] = @bitCast(@as(f64, 100000));
            masks[i] = 1;
        }
        const c = p.Catalog{ .sources = .{ .count = 1, .ids = &.{10}, .incarnations = &.{1}, .capabilities = &.{1}, .availability = &.{1} }, .rules = .{ .count = n, .ids = ids.ptr, .source_ids = sourceids.ptr, .metric_ids = mids.ptr, .identities = identities_rules.ptr, .kinds = kinds.ptr, .flags = flags.ptr, .priorities = priorities.ptr, .value_kinds = value_kinds.ptr, .minimum_bits = minimum.ptr, .maximum_bits = maximum.ptr, .capability_masks = masks.ptr }, .metrics = .{ .count = n, .ids = mids.ptr, .identities = mids_identity.ptr } };
        try eq(@as(u32, 0), api.rm_metric_owner_replace_catalog(owner, &c, kernel.ptr, kernel.len, 100, null));
        const strings = "device|Aengine|0engine|1PNP|Adrivertrigger-0trigger-1trigger-2";
        const keys = try a.alloc(v.Ref, n);
        keys[0] = .{ .offset = 0, .length = 8 };
        keys[1] = .{ .offset = 8, .length = 8 };
        keys[2] = .{ .offset = 16, .length = 8 };
        const parents = try a.alloc(v.Ref, n);
        @memset(parents, keys[0]);
        const pnps = try a.alloc(v.Ref, n);
        @memset(pnps, .{ .offset = 24, .length = 5 });
        const pnp_drivers = try a.alloc(v.Ref, n);
        @memset(pnp_drivers, .{ .offset = 29, .length = 6 });
        const addresses = try a.alloc(v.Ref, n);
        for (addresses, 0..) |*ref, i| ref.* = .{ .offset = @intCast(35 + i * 9), .length = 9 };
        const fields = try a.alloc(u32, n);
        const scopes = try a.alloc(u32, n);
        const metric_indices = try a.alloc(u32, n);
        const scope_ids = try a.alloc(u64, n);
        const expansions = try a.alloc(u32, n);
        const dedicated = try a.alloc(u32, n);
        const engine_indices = try a.alloc(u32, n);
        const counts = try a.alloc(u32, n);
        const vendors = try a.alloc(u32, n);
        const devices = try a.alloc(u32, n);
        const present = try a.alloc(u32, n);
        const directory = try a.alloc(u64, n);
        const directory_hi = try a.alloc(u64, n);
        const luids = try a.alloc(u64, n);
        const drivers = try a.alloc(u64, n);
        const generations = try a.alloc(u64, n);
        for (fields, 0..) |*field, i| {
            field.* = if (mode == 1) (if (i == 0) @import("field_codes.zig").npu_device_engine_busy_percent else @import("field_codes.zig").npu_engine_reported_running_time_ratio_percent) else if (mode == 2) (if (i == 0) @import("field_codes.zig").gpu_engine_busy else 0) else ([_]u32{ 64, 19, 48 })[i];
            scopes[i] = if (mode == 2) 0 else @intCast(i);
            metric_indices[i] = @intCast(i);
            scope_ids[i] = if (mode == 2) 100 else 100 + i;
            expansions[i] = if (mode == 1) (if (i == 0) 16 else 17) else if (i == 0) 4 else 0;
            dedicated[i] = if (i == 0) 1 else 0;
            engine_indices[i] = if (i == 0) 0 else @intCast(i - 1);
            counts[i] = if (i == 0) 0 else 2;
            vendors[i] = 0x8086;
            devices[i] = 42;
            present[i] = 1;
            directory[i] = if (i == 0) 0 else 9;
            directory_hi[i] = 0;
            luids[i] = 5;
            drivers[i] = 88;
            generations[i] = 1;
        }
        const b = v.Bindings{ .count = n, .catalog_generation = owner.?.catalog_generation, .field_codes = fields.ptr, .scope_indices = scopes.ptr, .metric_indices = metric_indices.ptr, .scope_ids = scope_ids.ptr, .addresses = addresses.ptr, .device_keys = keys.ptr, .text = .{ .bytes = strings.ptr, .length = strings.len } };
        const t = v.ScopeTopology{ .count = n, .text = .{ .bytes = strings.ptr, .length = strings.len }, .keys = keys.ptr, .parents = parents.ptr, .pnp = pnps.ptr, .pnp_drivers = pnp_drivers.ptr, .directory_low = directory.ptr, .directory_high = directory_hi.ptr, .luids = luids.ptr, .drivers = drivers.ptr, .generations = generations.ptr, .vendors = vendors.ptr, .devices = devices.ptr, .engine_indices = engine_indices.ptr, .engine_counts = counts.ptr, .driver_present = present.ptr, .flags = dedicated.ptr, .expansions = expansions.ptr };
        return .{ .arena = arena, .region = region, .active = active, .all_work = all_work, .kernel = kernel, .side = side, .owner = owner.?, .binding = vector_binding, .catalog = c, .b = b, .t = t, .fields = fields, .scope_indices = scopes, .keys = keys, .parents = parents, .pnp = pnps, .drivers = drivers, .driver_present = present, .dir = directory, .expansions = expansions, .flags = dedicated, .engine_indices = engine_indices, .engine_counts = counts, .luids = luids };
    }
    fn deinit(f: *Fixture) void {
        _ = api.rm_metric_owner_unbind(f.owner, null);
        f.arena.deinit();
    }
    fn plan(f: *Fixture, requested: []const u32, utc: i64) !p.Round {
        var raw: p.Round = undefined;
        try eq(@as(u32, 0), api.rm_metric_owner_plan(f.owner, requested.ptr, @intCast(requested.len), f.kernel.ptr, f.kernel.len, utc, &raw, null));
        return raw;
    }
    fn commit(f: *Fixture, raw: p.Round, utc: i64) !p.Receipt {
        try eq(@as(u32, 0), api.rm_metric_owner_prepare(f.owner, raw.token, null));
        var receipt: p.Receipt = undefined;
        try eq(@as(u32, 0), api.rm_metric_owner_commit_receipt(f.owner, raw.token, utc, &receipt, null));
        return receipt;
    }
    fn npu(f: *Fixture, utc: i64, generation: u64) !struct { plan: v.NpuPlan, raw: p.Round } {
        var npu_plan: v.NpuPlan = undefined;
        try eq(@as(u32, 0), v.rm_metric_vector_npu_expand(f.owner, &f.b, &f.t, &.{0}, 1, 10, f.side.ptr, f.side.len, &npu_plan, null));
        const raw = try f.plan(npu_plan.metrics.?[0..npu_plan.metric_count], utc);
        try eq(@as(u32, 0), v.rm_metric_vector_npu_attach(f.owner, raw.token, generation, &npu_plan, null));
        return .{ .plan = npu_plan, .raw = raw };
    }
    fn result(f: *Fixture, slot: usize, row: u32) !u64 {
        const location = try st.resultLocation(f.owner, row);
        return if (slot == 4 or slot == 11) location.table.load(u32, slot, location.row) else location.table.load(u64, slot, location.row);
    }
    fn vector(f: *Fixture, comptime T: type, slot: usize, row: u32) !T {
        const layout = try ss.layout.Layout.load(f.region);
        const spec = try layout.checkedSpec(f.region, f.binding.positions[slot]);
        const ptr: *const T = @ptrCast(@alignCast(layout.dataArea(f.region).ptr + spec.offset + @as(usize, row) * @sizeOf(T)));
        return @atomicLoad(T, ptr, .acquire);
    }
};
fn engine(raw: p.Round, index: u32, run: u64, clock: u64) void {
    raw.observations.status.?[index] = 1;
    raw.observations.presence.?[index] = 1;
    raw.observations.kinds.?[index] = 1;
    raw.observations.low.?[index] = run;
    raw.observations.high.?[index] = clock;
    raw.observations.flags.?[index] = 2;
    raw.observations.tick_frequency.?[index] = 1000;
}
fn warm(f: *Fixture) !void {
    const first = try f.npu(1000, 1);
    engine(first.raw, 1, 10000, 1000);
    engine(first.raw, 2, 50000, 1000);
    first.plan.sample_generations.?[1] = 1;
    first.plan.sample_generations.?[2] = 1;
    _ = try f.commit(first.raw, 1000);
}

test "NPU native expansion complete maximum uses same-round counters and exact dependency receipts" {
    var f = try Fixture.init(1);
    defer f.deinit();
    try warm(&f);
    try eq(@as(u64, 3), try f.result(11, 0));
    const second = try f.npu(2000, 2);
    try eq(@as(u32, 3), second.raw.count);
    try eq(@as(u32, 1), f.owner.work.request_count);
    try eq(@as(u32, 0), second.plan.target_observations.?[0]);
    engine(second.raw, 1, 20000, 2000);
    engine(second.raw, 2, 90000, 2000);
    second.plan.sample_generations.?[1] = 2;
    second.plan.sample_generations.?[2] = 2;
    const receipt = try f.commit(second.raw, 2000);
    try eq(@as(u64, 1), try f.result(11, 0));
    try eq(@as(u64, 100), try f.result(2, 0));
    const raw = try st.table(f.owner, 0);
    try eq(@as(u64, @bitCast(@as(f64, 4))), raw.load(u64, 1, 0));
    try eq(@as(u64, 1000), raw.load(u64, 7, 0));
    try eq(@as(i64, 2000), raw.load(i64, 6, 0));
    try eq(@as(u32, 2), receipt.query_count);
    try eq(@as(u64, 101), receipt.queries.rule_ids.?[0]);
    try eq(@as(u64, 102), receipt.queries.rule_ids.?[1]);
    try eq(@as(u32, 3), receipt.affected_count);
    try eq(@as(u32, 0), receipt.invalidated_alias_count);
    try expect(f.owner.work.region == null and f.owner.work.npu_plan == null);
}

test "NPU timed ratios at UTC epoch zero remain completed observations" {
    var f = try Fixture.init(1);
    defer f.deinit();
    const first = try f.npu(0, 1);
    engine(first.raw, 1, 10000, 1000);
    engine(first.raw, 2, 50000, 1000);
    first.plan.sample_generations.?[1] = 1;
    first.plan.sample_generations.?[2] = 1;
    _ = try f.commit(first.raw, 0);
    const second = try f.npu(0, 2);
    engine(second.raw, 1, 20000, 2000);
    engine(second.raw, 2, 90000, 2000);
    second.plan.sample_generations.?[1] = 2;
    second.plan.sample_generations.?[2] = 2;
    _ = try f.commit(second.raw, 0);
    try eq(@as(u64, 1), try f.result(11, 0));
    const raw = try st.table(f.owner, 0);
    try eq(@as(u64, @bitCast(@as(f64, 4))), raw.load(u64, 1, 0));
    try eq(@as(i64, 0), raw.load(i64, 6, 0));
    try eq(@as(u64, 1000), raw.load(u64, 7, 0));
}
test "NPU incomplete identity missing warming and stale sample generations retain a legitimate good" {
    var f = try Fixture.init(1);
    defer f.deinit();
    try warm(&f);
    var round = try f.npu(2000, 2);
    engine(round.raw, 1, 20000, 2000);
    engine(round.raw, 2, 90000, 2000);
    round.plan.sample_generations.?[1] = 2;
    round.plan.sample_generations.?[2] = 2;
    _ = try f.commit(round.raw, 2000);
    const good = (try st.table(f.owner, 0)).load(u64, 5, 0);
    f.drivers[2] += 1;
    round = try f.npu(3000, 3);
    try eq(@as(u32, 0), round.plan.complete.?[0]);
    engine(round.raw, 1, 30000, 3000);
    engine(round.raw, 2, 130000, 3000);
    round.plan.sample_generations.?[1] = 3;
    round.plan.sample_generations.?[2] = 3;
    _ = try f.commit(round.raw, 3000);
    try eq(@as(u64, 2), try f.result(11, 0));
    f.drivers[2] -= 1;
    round = try f.npu(4000, 4);
    engine(round.raw, 1, 40000, 4000);
    round.plan.sample_generations.?[1] = 4;
    round.plan.sample_generations.?[2] = 4;
    _ = try f.commit(round.raw, 4000);
    try eq(@as(u64, 2), try f.result(11, 0));
    round = try f.npu(5000, 5);
    engine(round.raw, 1, 50000, 5000);
    engine(round.raw, 2, 170000, 5000);
    round.plan.sample_generations.?[1] = 4;
    round.plan.sample_generations.?[2] = 4;
    _ = try f.commit(round.raw, 5000);
    try eq(@as(u64, 2), try f.result(11, 0));
    try eq(good, (try st.table(f.owner, 0)).load(u64, 5, 0));
}
test "NPU completeness checks exact directory parent PnP LUID vendor device count and index closure" {
    var f = try Fixture.init(1);
    defer f.deinit();
    for (0..10) |case| {
        switch (case) {
            0 => f.dir[2] += 1,
            1 => f.parents[2] = f.keys[2],
            2 => f.pnp[2] = f.keys[2],
            3 => f.luids[2] += 1,
            4 => @constCast(f.t.vendors.?)[2] += 1,
            5 => @constCast(f.t.devices.?)[2] += 1,
            6 => f.engine_counts[2] = 3,
            7 => f.engine_indices[2] = 0,
            8 => f.driver_present[2] = 0,
            9 => @constCast(f.t.pnp_drivers.?)[2] = .{ .offset = p.none },
            else => unreachable,
        }
        var npu_plan: v.NpuPlan = undefined;
        try eq(@as(u32, 0), v.rm_metric_vector_npu_expand(f.owner, &f.b, &f.t, &.{0}, 1, 10, f.side.ptr, f.side.len, &npu_plan, null));
        try eq(@as(u32, 0), npu_plan.complete.?[0]);
        switch (case) {
            0 => f.dir[2] -= 1,
            1 => f.parents[2] = f.keys[0],
            2 => f.pnp[2] = f.pnp[1],
            3 => f.luids[2] -= 1,
            4 => @constCast(f.t.vendors.?)[2] -= 1,
            5 => @constCast(f.t.devices.?)[2] -= 1,
            6 => f.engine_counts[2] = 2,
            7 => f.engine_indices[2] = 1,
            8 => f.driver_present[2] = 1,
            9 => @constCast(f.t.pnp_drivers.?)[2] = f.t.pnp_drivers.?[1],
            else => unreachable,
        }
    }
    f.fields[1] = 0;
    f.fields[2] = 0;
    const no_engines = try f.npu(1000, 1);
    try eq(@as(u32, 0), no_engines.plan.engine_count);
    try eq(@as(u32, 0), no_engines.plan.complete.?[0]);
    _ = try f.commit(no_engines.raw, 1000);
    try eq(@as(u64, 3), try f.result(11, 0));
}
test "NPU checked sizing short work wrong token stale catalog and abort exclude publication" {
    var f = try Fixture.init(1);
    defer f.deinit();
    var size: u64 = 0;
    try eq(@as(u32, 0), v.rm_metric_vector_npu_size(f.owner, &f.b, &f.t, &.{0}, 1, 10, &size, null));
    var npu_plan: v.NpuPlan = undefined;
    var failure: p.Failure = undefined;
    try eq(@as(u32, @intFromEnum(p.Code.workspace_unavailable)), v.rm_metric_vector_npu_expand(f.owner, &f.b, &f.t, &.{0}, 1, 10, f.side.ptr, size - 1, &npu_plan, &failure));
    try eq(size, failure.required);
    try eq(@as(u32, 0), v.rm_metric_vector_npu_expand(f.owner, &f.b, &f.t, &.{0}, 1, 10, f.side.ptr, f.side.len, &npu_plan, null));
    const raw = try f.plan(npu_plan.metrics.?[0..npu_plan.metric_count], 1000);
    try eq(@as(u32, @intFromEnum(p.Code.stale_identity)), v.rm_metric_vector_npu_attach(f.owner, raw.token + 1, 1, &npu_plan, null));
    try expect(f.owner.work.region == null);
    try eq(@as(u32, 0), v.rm_metric_vector_npu_expand(f.owner, &f.b, &f.t, &.{0}, 1, 10, f.side.ptr, f.side.len, &npu_plan, null));
    const next = try f.plan(npu_plan.metrics.?[0..npu_plan.metric_count], 1000);
    npu_plan.catalog_generation += 1;
    try eq(@as(u32, @intFromEnum(p.Code.stale_identity)), v.rm_metric_vector_npu_attach(f.owner, next.token, 1, &npu_plan, null));
    try expect(f.owner.work.region == null);
    const before = f.owner.counters.written;
    const state = try f.npu(2000, 2);
    _ = state;
    try eq(@as(u32, 0), api.rm_metric_owner_abort_detach(f.owner, null));
    try eq(before, f.owner.counters.written);
    try expect(f.owner.work.npu_plan == null);
}

fn gpuInput(count: u32, luids: [*]const u64, physical: [*]const u32, engines: [*]const u32, classes: [*]const u32, usage: [*]const f64) v.GpuInput {
    return .{ .count = count, .source_id = 10, .incarnation = 1, .capability = 1, .observation = 1, .observed_ms = 2000, .status = 1, .valid_mask = 1, .luids = luids, .physical = physical, .engines = engines, .classes = classes, .usage = usage };
}
fn prepareGpu(f: *Fixture, request: v.Request, input: v.GpuInput) !*const v.GpuPublication {
    const raw = try f.plan(&.{0}, input.observed_ms);
    try eq(@as(u32, 0), api.rm_metric_owner_prepare(f.owner, raw.token, null));
    var publication: ?*const v.GpuPublication = null;
    try eq(@as(u32, 0), v.rm_metric_vector_gpu_prepare(f.owner, raw.token, &f.b, &f.t, &request, &input, f.side.ptr, f.side.len, &publication, null));
    return publication.?;
}
fn commitGpu(f: *Fixture, publication: *const v.GpuPublication) !void {
    try eq(@as(u32, 0), publication.published);
    var receipt: p.Receipt = undefined;
    try eq(@as(u32, 0), api.rm_metric_owner_commit_receipt(f.owner, publication.token, 2000, &receipt, null));
    try eq(@as(u32, 1), publication.published);
    try eq(@as(u32, 1), receipt.query_count);
    try expect(f.owner.work.gpu_pending == null);
}
fn stringAt(f: *Fixture, slot: usize, row: u32) ![]const u8 {
    const ref_bits = try f.vector(u64, slot, row);
    const layout = try ss.layout.Layout.load(f.region);
    const spec = try layout.checkedSpec(f.region, f.binding.positions[14]);
    return layout.dataArea(f.region)[@as(usize, spec.offset) + @as(u32, @truncate(ref_bits)) ..][0..@intCast(ref_bits >> 32)];
}

test "PDH exact vector sort sum clamp canonical address and native catalog suppression are golden" {
    var f = try Fixture.init(2);
    defer f.deinit();
    try eq(@as(u32, 0), v.rm_metric_vector_gpu_bind(f.owner, &f.binding, null));
    var input = gpuInput(3, &.{ 5, 5, 5 }, &.{ 1, 0, 1 }, &.{ 2, 1, 2 }, &.{ 2, 0, 2 }, &.{ 70, 30, 40 });
    input.valid_mask = 0; // Original PdhGpuEngineUsageRead has no mask field.
    const publication = try prepareGpu(&f, .{ .flags = 1 }, input);
    try eq(@as(u32, 2), publication.rows);
    try eq(@as(u32, 2), publication.selected_count);
    try eq(@as(u32, 1), publication.suppressed_count);
    try eq(@as(u32, 0), publication.suppressed_metrics.?[0]);
    try commitGpu(&f, publication);
    try eq(@as(u32, 0), try f.vector(u32, 2, 0));
    try eq(@as(u32, 1), try f.vector(u32, 3, 0));
    try eq(@as(u64, @bitCast(@as(f64, 30))), try f.vector(u64, 5, 0));
    try eq(@as(u64, @bitCast(@as(f64, 100))), try f.vector(u64, 5, 1));
    try eq(@as(u32, 1), try f.vector(u32, 20, 0));
    try std.testing.expectEqualStrings("gpu-engine/0000000000000005/0/1/0/busyPercent", try stringAt(&f, 13, 0));
    try std.testing.expectEqualStrings("gpu-engine|0000000000000005|1|2|2", try stringAt(&f, 15, 1));
    try eq(@as(u64, 1000), try f.vector(u64, 16, 0));
    const layout = try ss.layout.Layout.load(f.region);
    const seq = layout.source(f.region, f.binding.rows_source).sequence;
    const next = try prepareGpu(&f, .{ .flags = 1 }, gpuInput(2, &.{ 5, 5 }, &.{ 0, 1 }, &.{ 1, 2 }, &.{ 0, 2 }, &.{ 31, 99 }));
    try commitGpu(&f, next);
    try eq(seq, layout.source(f.region, f.binding.rows_source).sequence);
    try eq(@as(u64, @bitCast(@as(f64, 31))), try f.vector(u64, 5, 0));
}
test "PDH native exact selector updates one engine and ordinary failure retains it" {
    var f = try Fixture.init(2);
    defer f.deinit();
    try eq(@as(u32, 0), v.rm_metric_vector_gpu_bind(f.owner, &f.binding, null));
    const first = try prepareGpu(&f, .{ .flags = 1 }, gpuInput(2, &.{ 5, 5 }, &.{ 0, 0 }, &.{ 0, 1 }, &.{ 0, 2 }, &.{ 10, 20 }));
    try commitGpu(&f, first);
    const generation = try f.vector(u64, 8, 1);
    const address = "gpu-engine/0000000000000005/0/0/0/busyPercent";
    const refs = [_]v.Ref{.{ .length = address.len }};
    const request = v.Request{ .count = 1, .text = .{ .bytes = address.ptr, .length = address.len }, .addresses = &refs };
    const next = try prepareGpu(&f, request, gpuInput(2, &.{ 5, 5 }, &.{ 0, 0 }, &.{ 0, 1 }, &.{ 0, 2 }, &.{ 11, 99 }));
    try eq(@as(u32, 1), next.selected_count);
    try eq(@as(u32, 0), next.selected_rows.?[0]);
    try commitGpu(&f, next);
    try eq(generation, try f.vector(u64, 8, 1));
    try eq(@as(u64, @bitCast(@as(f64, 20))), try f.vector(u64, 5, 1));
    var failure = v.GpuInput{ .source_id = 10, .incarnation = 1, .capability = 1, .observation = 3, .observed_ms = 3000, .status = 3, .reason = 9 };
    const failed = try prepareGpu(&f, request, failure);
    try commitGpu(&f, failed);
    try eq(@as(u32, 2), try f.vector(u32, 20, 0));
    try eq(@as(u64, @bitCast(@as(f64, 11))), try f.vector(u64, 5, 0));
    try eq(@as(u32, 1), try f.vector(u32, 20, 1));
    // A successful numeric query missing one known engine is an unavailable
    // reading, not evidence that its catalog identity disappeared.
    const layout = try ss.layout.Layout.load(f.region);
    const row_sequence = layout.source(f.region, f.binding.rows_source).sequence;
    const missing = try prepareGpu(&f, .{ .flags = 1 }, gpuInput(1, &.{5}, &.{0}, &.{0}, &.{0}, &.{12}));
    try eq(@as(u32, 2), missing.rows);
    try commitGpu(&f, missing);
    try eq(row_sequence, layout.source(f.region, f.binding.rows_source).sequence);
    try eq(@as(u32, 2), try f.vector(u32, 20, 1));
    try eq(generation, try f.vector(u64, 8, 1));
    failure.status = 4;
    const unsupported = try prepareGpu(&f, request, failure);
    try commitGpu(&f, unsupported);
    try eq(@as(u32, 3), try f.vector(u32, 10, 0));
    try eq(@as(u32, 2), try f.vector(u32, 20, 0));
}
test "PDH catalog probe publishes fields with typed absence and native trigger expansion" {
    var f = try Fixture.init(2);
    defer f.deinit();
    try eq(@as(u32, 0), v.rm_metric_vector_gpu_bind(f.owner, &f.binding, null));
    const request = v.Request{ .flags = 2 };
    var bytes: u64 = 0;
    try eq(@as(u32, 0), v.rm_metric_vector_gpu_expand_size(f.owner, &bytes, null));
    var selection: v.Selection = undefined;
    try eq(@as(u32, 0), v.rm_metric_vector_gpu_expand(f.owner, &f.b, &f.t, &request, null, 0, f.side.ptr, f.side.len, &selection, null));
    try eq(@as(u32, 1), selection.count);
    try eq(@as(u32, 0), selection.metric_indices.?[0]);
    const publication = try prepareGpu(&f, request, gpuInput(1, &.{5}, &.{0}, &.{7}, &.{1}, &.{88}));
    try commitGpu(&f, publication);
    try eq(@as(u32, 1), try f.vector(u32, 6, 0));
    try eq(@as(u32, 0), try f.vector(u32, 7, 0));
    try eq(@as(u32, 4), try f.vector(u32, 20, 0));
}
test "PDH capacity unaligned work wrong identity abort and busy admission reject before stores" {
    var f = try Fixture.init(2);
    defer f.deinit();
    try eq(@as(u32, 0), v.rm_metric_vector_gpu_bind(f.owner, &f.binding, null));
    var size: u64 = 0;
    try eq(@as(u32, 0), v.rm_metric_vector_gpu_work_size(f.owner, 1, &size, null));
    const raw = try f.plan(&.{0}, 2000);
    try eq(@as(u32, 0), api.rm_metric_owner_prepare(f.owner, raw.token, null));
    const input = gpuInput(1, &.{5}, &.{0}, &.{0}, &.{0}, &.{22});
    var out: ?*const v.GpuPublication = null;
    const request = v.Request{ .flags = 1 };
    const before = f.owner.counters.written;
    try eq(@as(u32, @intFromEnum(p.Code.invalid_contract)), v.rm_metric_vector_gpu_prepare(f.owner, raw.token, &f.b, &f.t, &request, &input, f.side.ptr + 1, f.side.len - 1, &out, null));
    try eq(before, f.owner.counters.written);
    try expect(out == null and f.owner.work.region == null);
    const identity_round = try f.plan(&.{0}, 2000);
    try eq(@as(u32, 0), api.rm_metric_owner_prepare(f.owner, identity_round.token, null));
    var wrong_identity = input;
    wrong_identity.incarnation += 1;
    try eq(@as(u32, @intFromEnum(p.Code.stale_identity)), v.rm_metric_vector_gpu_prepare(f.owner, identity_round.token, &f.b, &f.t, &request, &wrong_identity, f.side.ptr, f.side.len, &out, null));
    try eq(before, f.owner.counters.written);
    try expect(out == null and f.owner.work.region == null);
    const publication = try prepareGpu(&f, request, input);
    var busy = try ss.Writer.begin(f.region, 5);
    try eq(@as(u32, @intFromEnum(p.Code.shared_section)), api.rm_metric_owner_commit(f.owner, publication.token, 2000, null));
    try eq(before, f.owner.counters.written);
    try eq(@as(u32, 0), publication.published);
    try expect(f.owner.work.gpu_pending == null);
    try busy.end(.has_value, 2000);
    const canceled = try prepareGpu(&f, request, input);
    try eq(@as(u32, 0), api.rm_metric_owner_abort_detach(f.owner, null));
    try eq(@as(u32, 0), canceled.published);
    const too_many = gpuInput(4, &.{ 5, 5, 5, 5 }, &.{ 0, 0, 0, 0 }, &.{ 0, 1, 2, 3 }, &.{ 0, 0, 0, 0 }, &.{ 1, 2, 3, 4 });
    const r = try f.plan(&.{0}, 2000);
    try eq(@as(u32, 0), api.rm_metric_owner_prepare(f.owner, r.token, null));
    var failure: p.Failure = undefined;
    try eq(@as(u32, @intFromEnum(p.Code.capacity_exceeded)), v.rm_metric_vector_gpu_prepare(f.owner, r.token, &f.b, &f.t, &request, &too_many, f.side.ptr, f.side.len, &out, &failure));
    try eq(@as(u64, 4), failure.required);
    try eq(@as(u64, 3), failure.limit);
}
test "notebook native zero one two GPU or sensor routing preserves CPU and exact physical fan paths" {
    for (0..3) |gpu_count| for (0..3) |sensor_count| {
        var f = try Fixture.init(3);
        defer f.deinit();
        f.flags[0] = if (gpu_count == 0) 0 else 1;
        if (gpu_count == 2) {
            f.expansions[1] = 4;
            f.flags[1] = 1;
            f.luids[1] = 6;
        }
        const raw = try f.plan(&.{ 0, 1, 2 }, 2000);
        raw.observations.status.?[2] = 1;
        raw.observations.presence.?[2] = 1;
        raw.observations.low.?[2] = @bitCast(@as(f64, 777));
        const input = v.SensorInput{ .source_id = 10, .observed_ms = 2000, .status = 1, .cpu_rpm = 1234, .cpu_valid = 1, .count = @intCast(sensor_count), .indices = &.{ 7, 9 }, .rpm = &.{ 2000, 3000 }, .percent = &.{ 40, 60 }, .valid = &.{ 3, 3 } };
        try eq(@as(u32, 0), v.rm_metric_vector_notebook_apply(f.owner, raw.token, &f.b, &f.t, &input, null));
        try eq(@as(u32, if (gpu_count == 1 and sensor_count == 1) 1 else 3), raw.observations.status.?[0]);
        try eq(@as(u64, @bitCast(@as(f64, 1234))), raw.observations.low.?[1]);
        try eq(@as(u64, @bitCast(@as(f64, 777))), raw.observations.low.?[2]);
        _ = try f.commit(raw, 2000);
    };
}
test "notebook provider failures become raw health and preserve good legacy GPU fan" {
    var f = try Fixture.init(3);
    defer f.deinit();
    var raw = try f.plan(&.{0}, 2000);
    var input = v.SensorInput{ .source_id = 10, .observed_ms = 2000, .status = 1, .count = 1, .indices = &.{4}, .rpm = &.{2222}, .percent = &.{22}, .valid = &.{3} };
    try eq(@as(u32, 0), v.rm_metric_vector_notebook_apply(f.owner, raw.token, &f.b, &f.t, &input, null));
    _ = try f.commit(raw, 2000);
    raw = try f.plan(&.{0}, 3000);
    input.status = 3;
    try eq(@as(u32, 0), v.rm_metric_vector_notebook_apply(f.owner, raw.token, &f.b, &f.t, &input, null));
    _ = try f.commit(raw, 3000);
    try eq(@as(u64, 2), try f.result(11, 0));
    try eq(@as(u64, @bitCast(@as(f64, 2222))), (try st.table(f.owner, 0)).load(u64, 1, 0));
}

test "notebook native percent mapping does no RPM substitution and leaves physical PWM untouched" {
    var f = try Fixture.init(3);
    defer f.deinit();
    f.fields[0] = 63;
    f.fields[1] = 20;
    f.fields[2] = 177;
    const raw = try f.plan(&.{ 0, 1, 2 }, 2000);
    raw.observations.status.?[2] = 1;
    raw.observations.presence.?[2] = 1;
    raw.observations.low.?[2] = @bitCast(@as(f64, 55));
    const input = v.SensorInput{ .source_id = 10, .observed_ms = 2000, .status = 1, .cpu_percent = 15, .cpu_valid = 2, .count = 1, .indices = &.{8}, .rpm = &.{9999}, .percent = &.{22}, .valid = &.{2} };
    try eq(@as(u32, 0), v.rm_metric_vector_notebook_apply(f.owner, raw.token, &f.b, &f.t, &input, null));
    try eq(@as(u64, @bitCast(@as(f64, 22))), raw.observations.low.?[0]);
    try eq(@as(u64, @bitCast(@as(f64, 15))), raw.observations.low.?[1]);
    try eq(@as(u64, @bitCast(@as(f64, 55))), raw.observations.low.?[2]);
    _ = try f.commit(raw, 2000);
}

test "GPU vector stop preserves row identity and rebind failure restores retained public value access" {
    var f = try Fixture.init(2);
    defer f.deinit();
    try eq(@as(u32, 0), v.rm_metric_vector_gpu_bind(f.owner, &f.binding, null));
    const first = try prepareGpu(&f, .{ .flags = 1 }, gpuInput(1, &.{5}, &.{0}, &.{0}, &.{0}, &.{33}));
    try commitGpu(&f, first);
    const layout = try ss.layout.Layout.load(f.region);
    const row_sequence = layout.source(f.region, f.binding.rows_source).sequence;
    const config = f.owner.config;
    try eq(@as(u32, 0), api.rm_metric_owner_unbind_at(f.owner, 3000, null));
    try eq(p.no_subscription_reason, layout.source(f.region, f.binding.value_source).reason);
    try eq(row_sequence, layout.source(f.region, f.binding.rows_source).sequence);
    // Existing creator bindings, same raw region, a fresh borrowed active header.
    const raw_binding = p.TableBinding{ .region = f.region.ptr, .byte_length = f.region.len, .rows_source = 0, .value_source = 1, .health_source = 2, .provenance_source = 3, .provenance_position = 16, .positions = .{ 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15 } };
    const result_binding = p.TableBinding{ .region = f.region.ptr, .byte_length = f.region.len, .rows_source = 4, .value_source = 5, .health_source = 6, .provenance_source = 7, .provenance_position = 29, .positions = .{ 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, p.none, p.none, p.none, p.none } };
    var owner: ?*st.Owner = null;
    try eq(@as(u32, 0), api.rm_metric_owner_bind(&config, f.active.ptr, f.active.len, (&raw_binding)[0..1].ptr, 1, &result_binding, &owner, null));
    f.owner = owner.?;
    try eq(@as(u32, 0), api.rm_metric_owner_replace_catalog(f.owner, &f.catalog, f.kernel.ptr, f.kernel.len, 3000, null));
    f.b.catalog_generation = f.owner.catalog_generation;
    try eq(@as(u32, 0), v.rm_metric_vector_gpu_bind(f.owner, &f.binding, null));
    const failure = v.GpuInput{ .source_id = 10, .incarnation = 1, .capability = 1, .observation = 2, .observed_ms = 4000, .status = 3 };
    const publication = try prepareGpu(&f, .{ .flags = 1 }, failure);
    try commitGpu(&f, publication);
    try eq(@as(u32, 2), try f.vector(u32, 20, 0));
    try eq(@as(u64, @bitCast(@as(f64, 33))), try f.vector(u64, 5, 0));
    try eq(ss.SourceState.has_value, layout.source(f.region, f.binding.value_source).state);
    try eq(row_sequence, layout.source(f.region, f.binding.rows_source).sequence);
}

test "PDH suppression cannot bypass native plan and source invalidation retires every vector alias" {
    var f = try Fixture.init(2);
    defer f.deinit();
    try eq(@as(u32, 0), v.rm_metric_vector_gpu_bind(f.owner, &f.binding, null));
    try commitGpu(&f, try prepareGpu(&f, .{ .flags = 1 }, gpuInput(2, &.{ 5, 5 }, &.{ 0, 0 }, &.{ 0, 1 }, &.{ 0, 2 }, &.{ 10, 20 })));
    const old_generation = try f.vector(u64, 8, 0);
    f.owner.sources[0].suppressed = true;
    const suppressed = try f.plan(&.{0}, 3000);
    try eq(@as(u32, 0), suppressed.count);
    try eq(@as(u32, 0), api.rm_metric_owner_prepare(f.owner, suppressed.token, null));
    const input = gpuInput(1, &.{5}, &.{0}, &.{0}, &.{0}, &.{22});
    const request = v.Request{ .flags = 1 };
    var out: ?*const v.GpuPublication = null;
    const before = f.owner.counters.written;
    try eq(@as(u32, @intFromEnum(p.Code.stale_identity)), v.rm_metric_vector_gpu_prepare(f.owner, suppressed.token, &f.b, &f.t, &request, &input, f.side.ptr, f.side.len, &out, null));
    try expect(out == null and f.owner.work.region == null);
    try eq(before, f.owner.counters.written);
    f.owner.sources[0].suppressed = false;
    const invalid = try f.plan(&.{0}, 3000);
    invalid.observations.flags.?[0] = 8;
    invalid.observations.status.?[0] = 2;
    try eq(@as(u32, 0), api.rm_metric_owner_prepare(f.owner, invalid.token, null));
    try eq(@as(u32, 0), v.rm_metric_vector_gpu_prepare(f.owner, invalid.token, &f.b, &f.t, &request, &input, f.side.ptr, f.side.len, &out, null));
    try eq(@as(u32, 0), out.?.rows);
    try commitGpu(&f, out.?);
    const layout = try ss.layout.Layout.load(f.region);
    try eq(@as(u32, 0), layout.position(f.region, f.binding.positions[0]).length);
    try eq(@as(u32, 0), layout.position(f.region, f.binding.positions[5]).length);
    try commitGpu(&f, try prepareGpu(&f, .{ .flags = 1 }, input));
    try expect(try f.vector(u64, 8, 0) > old_generation);
    try eq(@as(u64, @bitCast(@as(f64, 22))), try f.vector(u64, 5, 0));
}

test "PDH whole source unsupported retains unselected aliases and suppresses future queries" {
    var f = try Fixture.init(2);
    defer f.deinit();
    try eq(@as(u32, 0), v.rm_metric_vector_gpu_bind(f.owner, &f.binding, null));
    try commitGpu(&f, try prepareGpu(&f, .{ .flags = 1 }, gpuInput(2, &.{ 5, 5 }, &.{ 0, 0 }, &.{ 0, 1 }, &.{ 0, 2 }, &.{ 10, 20 })));
    const old_generation = try f.vector(u64, 8, 1);
    const raw = try f.plan(&.{0}, 3000);
    raw.observations.status.?[0] = 3;
    raw.observations.flags.?[0] = 32;
    raw.observations.reasons.?[0] = 73;
    try eq(@as(u32, 0), api.rm_metric_owner_prepare(f.owner, raw.token, null));
    const address = "gpu-engine/0000000000000005/0/0/0/busyPercent";
    const request = v.Request{ .count = 1, .text = .{ .bytes = address.ptr, .length = address.len }, .addresses = &.{.{ .length = address.len }} };
    const input = v.GpuInput{ .source_id = 10, .incarnation = 1, .capability = 1, .observation = 2, .observed_ms = 3000, .status = 4, .reason = 73 };
    var publication: ?*const v.GpuPublication = null;
    try eq(@as(u32, 0), v.rm_metric_vector_gpu_prepare(f.owner, raw.token, &f.b, &f.t, &request, &input, f.side.ptr, f.side.len, &publication, null));
    try eq(@as(u32, 1), publication.?.selected_count);
    try commitGpu(&f, publication.?);
    for (0..2) |i| {
        try eq(@as(u32, 3), try f.vector(u32, 10, @intCast(i)));
        try eq(@as(u32, 2), try f.vector(u32, 20, @intCast(i)));
    }
    try eq(old_generation, try f.vector(u64, 8, 1));
    const next = try f.plan(&.{0}, 4000);
    try eq(@as(u32, 0), next.count);
    const receipt = try f.commit(next, 4000);
    try eq(@as(u32, 0), receipt.query_count);
}
