//! Common-command differential corpus; never linked by the product build.
//! Old collector diagnostics are NOT an oracle for public last-good retention.
const std = @import("std");
const old = @import("../metric_snapshot/abi.zig");
const op = @import("../metric_snapshot/protocol.zig");
const old_session = @import("../metric_snapshot/session.zig");
const old_scratch = @import("../metric_snapshot/scratch.zig");
const Rc = @import("../../common/result_codes.zig").ResultCode;
const api = @import("root.zig");
const p = @import("protocol.zig");
const st = @import("state.zig");
const support = @import("support_exports.zig");
const checkpoint = @import("persistence.zig");
const ss = @import("rm_shared_section");
const t = std.testing;
const n = 8;
const max_rows = n + 1; // exact-cap +1 commands have real allocated input extents
const value_kinds = [n]u32{
    @intFromEnum(op.ValueKind.unsigned64),  @intFromEnum(op.ValueKind.signed64),
    @intFromEnum(op.ValueKind.unsigned128), @intFromEnum(op.ValueKind.boolean),
    @intFromEnum(op.ValueKind.bytes),       @intFromEnum(op.ValueKind.bytes),
    @intFromEnum(op.ValueKind.float64),     @intFromEnum(op.ValueKind.float64),
};

// These are inputs, not expected outputs. BOTH actual implementations receive
// each Sample. The last row is a counter for the collector lane.
const Sample = struct {
    low: u64 = 0,
    high: u64 = 0,
    flags: u32 = 0,
    frequency: u64 = 0,
    presence: u32 = 1,
    status: u32 = 1,
    bytes: []const u8 = "",
};
const Kind = enum { publish, missing, duplicate_plan, bad_catalog, capacity_catalog, invalid_observation, stale_commit, replace_identity };
const Command = struct {
    kind: Kind,
    at: u64,
    ordinal: u32 = 0,
};
const commands = [_]Command{
    .{ .kind = .publish, .at = 1000, .ordinal = 0 },
    .{ .kind = .publish, .at = 2000, .ordinal = 1 },
    .{ .kind = .duplicate_plan, .at = 2100 },
    .{ .kind = .bad_catalog, .at = 2200 },
    .{ .kind = .capacity_catalog, .at = 2300 },
    .{ .kind = .invalid_observation, .at = 2400, .ordinal = 2 },
    .{ .kind = .stale_commit, .at = 2500, .ordinal = 2 },
    .{ .kind = .publish, .at = 3000, .ordinal = 2 },
    .{ .kind = .publish, .at = 4000, .ordinal = 3 }, // regressed raw counters
    .{ .kind = .publish, .at = 5000, .ordinal = 4 }, // new baseline succeeds
    .{ .kind = .missing, .at = 6000 },
    .{ .kind = .publish, .at = 7000, .ordinal = 5 },
    .{ .kind = .publish, .at = 8000, .ordinal = 6 },
    .{ .kind = .replace_identity, .at = 8500 },
    .{ .kind = .publish, .at = 9000, .ordinal = 7 },
    .{ .kind = .publish, .at = 10000, .ordinal = 8 },
};
const counter_kinds = [_]u32{
    @intFromEnum(op.MetricKind.counter_ratio),                        @intFromEnum(op.MetricKind.timed_counter_rate),
    @intFromEnum(op.MetricKind.floating_counter_ratio),               @intFromEnum(op.MetricKind.floating_counter_percentage),
    @intFromEnum(op.MetricKind.timed_microsecond_counter_percentage),
};
// Includes eight initial catalog commands; excludes allocator-injection retries.
pub const shared_command_count = commands.len * counter_kinds.len + 3 + 7 + counter_kinds.len + 1 + 2;

fn samples(counter_kind: u32, ordinal: u32, missing: bool) [n]Sample {
    var out = [n]Sample{
        .{ .low = std.math.maxInt(u64) - @as(u64, ordinal) },
        .{ .low = @bitCast(@as(i64, -9_007_199_254_740_993) + @as(i64, ordinal)) },
        .{ .low = 0xfedcba9876543210, .high = 0xabcdef0123456789 },
        .{ .low = ordinal % 2 },
        .{ .bytes = &.{ 0, 255, 1, 128, 0 } },
        .{ .bytes = &.{0} }, // legacy collector bytes require a positive declared minimum
        .{ .low = @bitCast(@as(f64, -0.0)) },
        .{},
    };
    const step: u64 = if (ordinal == 3) 0 else 10 + ordinal;
    out[7] = if (counter_kind == 16 or counter_kind == 17)
        .{ .flags = 4, .low = @bitCast(@as(f64, @floatFromInt(step)) + 0.125), .high = @bitCast(@as(f64, @floatFromInt(step * 4)) + 0.125) }
    else
        .{ .flags = if (counter_kind == 14) 1 else 2, .low = (if (ordinal == 3) @as(u64, 1) else (@as(u64, 1) << 60) + step * 500_000), .high = step * 1_000_000 + 100, .frequency = if (counter_kind == 14) 0 else 1_000_000 };
    if (missing) for (&out) |*sample| {
        const flags = sample.flags;
        sample.* = .{ .status = 2, .presence = 0, .flags = flags };
    };
    return out;
}

fn oldOk(code: i32) !void {
    try t.expectEqual(@intFromEnum(Rc.ok), code);
}
fn newOk(code: u32) !void {
    try t.expectEqual(@intFromEnum(p.Code.ok), code);
}

fn oldConfig() op.Config {
    var c = std.mem.zeroes(op.Config);
    c.abi_version = op.abi_version;
    c.struct_size = @sizeOf(op.Config);
    c.generation = 1;
    c.maximum_source_count = 1;
    c.maximum_metric_count = n;
    c.maximum_rule_count = n;
    c.maximum_requested_count = n;
    c.maximum_observation_count = n;
    c.maximum_gpu_adapter_count = 1;
    c.maximum_persistence_source_count = 1;
    c.maximum_persistence_rule_count = n;
    c.maximum_persistence_gpu_count = 1;
    c.source_index_capacity = 2;
    c.metric_index_capacity = 16;
    c.rule_index_capacity = 16;
    c.gpu_index_capacity = 2;
    c.gpu_luid_index_capacity = 2;
    c.gpu_key_index_capacity = 2;
    c.maximum_future_skew_milliseconds = 30_000;
    c.resident_byte_budget = 8 << 20;
    c.catalog_contract_version = op.catalog_contract_version;
    c.value_contract_version = op.value_contract_version;
    c.observation_contract_version = op.observation_contract_version;
    c.inventory_contract_version = op.inventory_contract_version;
    c.persistence_contract_version = op.persistence_contract_version;
    c.maximum_plan_metric_count = n;
    c.maximum_source_mode_count = 1;
    c.maximum_source_plan_count = 1;
    c.maximum_metric_plan_count = n;
    return c;
}

const Fixture = struct {
    arena: std.heap.ArenaAllocator,
    old_handle: ?*anyopaque,
    owner: *st.Owner,
    region: []align(8) u8,
    work: []align(8) u8,
    scratch: []align(8) u8,
    current_resident: []align(8) u8,
    config: op.Config,
    operation: u64 = 0,
    plan_epoch: u64 = 0,
    catalog_generation: u64 = 0,
    sequence: u64 = 0,
    counter_kind: u32,
    identity_revision: u64 = 0,

    fn init(counter_kind: u32) !Fixture {
        return initUsing(t.allocator, counter_kind);
    }
    fn initUsing(allocator: std.mem.Allocator, counter_kind: u32) !Fixture {
        var arena = std.heap.ArenaAllocator.init(allocator);
        errdefer arena.deinit();
        const a = arena.allocator();
        const config = oldConfig();
        var handle: ?*anyopaque = null;
        try oldOk(old.rm_metric_snapshot_create(&config, &handle));
        errdefer old.rm_metric_snapshot_destroy(handle);
        const scratch_layout = try old_scratch.Layout.init(&config, n, n);
        const scratch = try a.alignedAlloc(u8, .@"8", scratch_layout.byte_count);
        const resident = try a.alignedAlloc(u8, .@"8", 2 * (n * @sizeOf(op.CurrentValue) + 64));
        // One real NORMAL SharedSection mapping, raw + result, exclusive writers.
        const positions = try a.alloc(ss.PositionSpec, 30);
        var tables: [2]p.TableBinding = undefined;
        var offset: u32 = 0;
        var position: u32 = 0;
        for (&tables, 0..) |*b, table_index| {
            b.* = .{ .region = null, .byte_length = 0, .rows_source = @intCast(table_index * 4), .value_source = @intCast(table_index * 4 + 1), .health_source = @intCast(table_index * 4 + 2), .provenance_source = @intCast(table_index * 4 + 3) };
            const result = table_index == 1;
            for (0..if (result) @as(usize, 12) else 16) |slot| {
                offset = std.mem.alignForward(u32, offset, 8);
                const width: u32 = if (result) (if (slot == 4 or slot == 5 or slot == 9 or slot == 10 or slot == 11) 4 else 8) else (if (slot == 9) 1 else if (slot == 3 or slot == 4 or slot == 10 or slot == 11 or slot == 13) 4 else 8);
                const source = if (slot == 0 or (!result and slot == 15)) b.rows_source else if (!result and slot >= 10 and slot <= 12) b.health_source else b.value_source;
                b.positions[slot] = position;
                positions[position] = if (!result and slot == 9) ss.PositionSpec.stringTable(source, offset, 4096) else ss.PositionSpec.column(source, offset, n, width, if (slot == 0) ss.none else b.positions[0]);
                offset += positions[position].size;
                position += 1;
            }
            offset = std.mem.alignForward(u32, offset, 8);
            b.provenance_position = position;
            positions[position] = ss.PositionSpec.column(b.provenance_source, offset, 8 + n * (if (result) @as(u32, 4) else 12), 8, ss.none);
            offset += positions[position].size;
            position += 1;
        }
        try t.expectEqual(@as(u32, 30), position);
        const bytes = ss.requiredSize(8, 30, offset);
        const region = try a.alignedAlloc(u8, .@"8", @intCast(bytes));
        var identities: [8]ss.SourceIdentity = undefined;
        for (&identities, 0..) |*id, i| id.* = try ss.SourceIdentity.init("equivalence", @intCast(i + 1), 0, 0);
        try ss.initialize(region, 42, 8, 30, offset, &identities, positions);
        for (&tables) |*b| {
            b.region = region.ptr;
            b.byte_length = region.len;
        }
        const nc = p.Config{ .source_capacity = 1, .rule_capacity = n, .metric_capacity = n, .max_value_bytes = 512, .active_limit = 64 << 20, .work_limit = 64 << 20, .payload_work_bytes = 8192 };
        var sizes: p.Sizes = undefined;
        try newOk(api.rm_metric_owner_required_sizes(&nc, &sizes, null));
        const active = try a.alignedAlloc(u8, .@"8", @intCast(sizes.active));
        const work = try a.alignedAlloc(u8, .@"8", @intCast(sizes.work));
        var owner: ?*st.Owner = null;
        try newOk(api.rm_metric_owner_bind(&nc, active.ptr, active.len, &tables, 1, &tables[1], &owner, null));
        return .{ .arena = arena, .old_handle = handle, .owner = owner.?, .region = region, .work = work, .scratch = scratch, .current_resident = resident, .config = config, .counter_kind = counter_kind };
    }
    fn deinit(f: *Fixture) void {
        _ = api.rm_metric_owner_abort_detach(f.owner, null);
        _ = api.rm_metric_owner_unbind(f.owner, null);
        old.rm_metric_snapshot_destroy(f.old_handle); // ends the legacy scratch/current borrow before arena free
        f.arena.deinit();
    }
    fn epoch(f: *Fixture) u64 {
        f.operation += 1;
        return f.operation;
    }
    fn header(f: *Fixture) !op.SnapshotHeader {
        var h: op.SnapshotHeader = undefined;
        try oldOk(old.rm_metric_snapshot_query_header(f.old_handle, &h, @sizeOf(op.SnapshotHeader)));
        return h;
    }
    fn readInput(f: *Fixture) !op.ReadInput {
        const h = try f.header();
        var input = std.mem.zeroes(op.ReadInput);
        input.abi_version = op.abi_version;
        input.struct_size = @sizeOf(op.ReadInput);
        input.configuration_generation = 1;
        input.catalog_generation = h.catalog_generation;
        input.catalog_fingerprint = h.catalog_fingerprint;
        input.state_revision = h.state_revision;
        input.committed_generation = h.committed_generation;
        return input;
    }
    fn oldValues(f: *Fixture) ![n]op.MetricOutput {
        var values: [n]op.MetricOutput = undefined;
        const input = try f.readInput();
        try oldOk(old.rm_metric_snapshot_read(f.old_handle, &input, null, 0, &values, n, null, 0, null, 0));
        return values;
    }
    fn oldSource(f: *Fixture) !op.SourceOutput {
        var sources: [1]op.SourceOutput = undefined;
        const input = try f.readInput();
        try oldOk(old.rm_metric_snapshot_read(f.old_handle, &input, &sources, 1, null, 0, null, 0, null, 0));
        return sources[0];
    }
};

// Cold declaration adapter: exact same IDs/types/ranges/priority/identity in both
// ABIs. Capacity+1 has actual arrays of nine records, not a false pointer extent.
const Declarations = struct {
    source: [1]op.SourcePolicyInput,
    rules: [max_rows]op.MetricDefinitionInput,
    ids: [max_rows]u64,
    sources: [max_rows]u64,
    metrics: [max_rows]u64,
    identities: [max_rows]u64,
    kinds: [max_rows]u32,
    flags: [max_rows]u32,
    priorities: [max_rows]u32,
    value_types: [max_rows]u32,
    minima: [max_rows]u64,
    maxima: [max_rows]u64,
    masks: [max_rows]u64,
    metric_identities: [max_rows]u64,
    fn init(counter_kind: u32, identity_revision: u64) Declarations {
        var d: Declarations = undefined;
        var source = std.mem.zeroes(op.SourcePolicyInput);
        source.struct_size = @sizeOf(op.SourcePolicyInput);
        source.source_handle = 10;
        source.source_role = 1;
        source.priority = 1;
        source.capability_mask = 1;
        source.semantic_fingerprint = 1001;
        d.source = .{source};
        for (0..max_rows) |i| {
            const kind = value_kinds[i % n];
            const minimum: u64 = if (kind == 1) @bitCast(if (i == 7) @as(f64, 0) else -std.math.floatMax(f64)) else if (kind == 2) @bitCast(@as(i64, std.math.minInt(i64))) else if (kind == 5) 1 else 0;
            const maximum: u64 = if (kind == 1) @bitCast(std.math.floatMax(f64)) else if (kind == 2) @bitCast(@as(i64, std.math.maxInt(i64))) else if (kind == 5) 512 else if (kind == 6) 1 else std.math.maxInt(u64);
            d.ids[i] = 100 + i;
            d.sources[i] = 10;
            d.metrics[i] = 1000 + i;
            d.identities[i] = 2000 + i + identity_revision * 100;
            d.metric_identities[i] = 3000 + i + identity_revision * 100;
            d.kinds[i] = if (i == 7) counter_kind else 13;
            d.flags[i] = if (i == 7) op.MetricFlags.nonnegative else 0;
            d.priorities[i] = 1;
            d.value_types[i] = kind;
            d.minima[i] = minimum;
            d.maxima[i] = maximum;
            d.masks[i] = 1;
            var rule = std.mem.zeroes(op.MetricDefinitionInput);
            rule.struct_size = @sizeOf(op.MetricDefinitionInput);
            rule.rule_handle = d.ids[i];
            rule.metric_handle = d.metrics[i];
            rule.source_handle = 10;
            rule.scope_handle = 1;
            rule.capability_mask = 1;
            rule.metric_kind = d.kinds[i];
            rule.flags = d.flags[i];
            rule.scope_kind = 1;
            rule.value_kind = kind;
            rule.source_priority = 1;
            rule.minimum_value_bits = minimum;
            rule.maximum_value_bits = maximum;
            rule.semantic_fingerprint = d.identities[i];
            d.rules[i] = rule;
        }
        return d;
    }
    fn catalog(d: *const Declarations, count: u32) p.Catalog {
        return .{ .sources = .{ .count = 1, .ids = &.{10}, .incarnations = &.{1}, .capabilities = &.{1}, .availability = &.{1} }, .rules = .{ .count = count, .ids = &d.ids, .source_ids = &d.sources, .metric_ids = &d.metrics, .identities = &d.identities, .kinds = &d.kinds, .flags = &d.flags, .priorities = &d.priorities, .value_kinds = &d.value_types, .minimum_bits = &d.minima, .maximum_bits = &d.maxima, .capability_masks = &d.masks }, .metrics = .{ .count = count, .ids = &d.metrics, .identities = &d.metric_identities } };
    }
};

fn replace(f: *Fixture, count: u32, duplicate: bool, at: u64) !void {
    var d = Declarations.init(f.counter_kind, f.identity_revision);
    try t.expect(op.validSourcePolicy(&d.source[0]));
    for (d.rules[0..count]) |rule| try t.expect(op.validMetricDefinition(&rule));
    if (duplicate) {
        d.rules[1].rule_handle = d.rules[0].rule_handle;
        d.ids[1] = d.ids[0];
    }
    const cat = d.catalog(count);
    std.debug.print("COLD count={d} duplicate={any} identity_revision={d} at={d}\n", .{ count, duplicate, f.identity_revision, at });
    for (d.rules[0..count]) |rule| std.debug.print("COLD rule={d} metric={d} source={d} scope={d} metric_kind={d} type={d} flags={d} min={x} max={x} identity={d}\n", .{ rule.rule_handle, rule.metric_handle, rule.source_handle, rule.scope_handle, rule.metric_kind, rule.value_kind, rule.flags, rule.minimum_value_bits, rule.maximum_value_bits, rule.semantic_fingerprint });
    var input = std.mem.zeroes(op.CatalogReplaceInput);
    input.abi_version = op.abi_version;
    input.struct_size = @sizeOf(op.CatalogReplaceInput);
    input.configuration_generation = 1;
    input.catalog_generation = f.catalog_generation + 1;
    input.operation_epoch = f.epoch();
    input.command_at_milliseconds = at;
    input.source_count = 1;
    input.rule_count = count;
    input.metric_count = count;
    input.source_index_capacity = f.config.source_index_capacity;
    input.metric_index_capacity = f.config.metric_index_capacity;
    input.rule_index_capacity = f.config.rule_index_capacity;
    input.semantic_fingerprint = old_session.catalogFingerprint(&d.source, d.rules[0..count]);
    const oc = old.rm_metric_snapshot_replace_catalog(f.old_handle, &input, &d.source, 1, &d.rules, count);
    const nc = api.rm_metric_owner_replace_catalog(f.owner, &cat, f.work.ptr, f.work.len, @intCast(at), null);
    if (duplicate) {
        try t.expectEqual(@intFromEnum(Rc.invalid_argument), oc);
        try t.expectEqual(@intFromEnum(p.Code.invalid_contract), nc);
    } else if (count > n) {
        try t.expectEqual(@intFromEnum(Rc.abi_mismatch), oc);
        try t.expectEqual(@intFromEnum(p.Code.capacity_exceeded), nc);
    } else {
        try oldOk(oc);
        try newOk(nc);
        f.catalog_generation += 1;
        f.plan_epoch = 0;
    }
}

const Planned = struct { raw: p.Round, source: op.SourcePlanOutput, at: u64 };
fn plan(f: *Fixture, at: u64, duplicate: bool) !Planned {
    var requested: [n]op.PlanMetricInput = undefined;
    var indices: [n]u32 = undefined;
    for (&requested, &indices, 0..) |*request, *index, i| {
        request.* = std.mem.zeroes(op.PlanMetricInput);
        request.struct_size = @sizeOf(op.PlanMetricInput);
        request.metric_handle = 1000 + i;
        index.* = @intCast(i);
    }
    if (duplicate) {
        requested[1] = requested[0];
        indices[1] = indices[0];
    }
    var mode = std.mem.zeroes(op.SourceStateInput);
    mode.struct_size = @sizeOf(op.SourceStateInput);
    mode.source_handle = 10;
    mode.capability_generation = 1;
    mode.availability = 1;
    const source_states = [1]op.SourceStateInput{mode};
    var input = std.mem.zeroes(op.PlanInput);
    input.abi_version = op.abi_version;
    input.struct_size = @sizeOf(op.PlanInput);
    input.configuration_generation = 1;
    input.catalog_generation = f.catalog_generation;
    input.operation_epoch = f.epoch();
    input.plan_epoch = f.plan_epoch + 1;
    input.command_at_milliseconds = at;
    input.requested_metric_count = n;
    input.source_mode_count = 1;
    input.source_plan_capacity = 1;
    input.metric_plan_capacity = n;
    var output: op.PlanOutput = undefined;
    var sources: [1]op.SourcePlanOutput = undefined;
    var metrics: [n]op.MetricPlanOutput = undefined;
    const oc = old.rm_metric_snapshot_plan(f.old_handle, &input, &requested, n, &source_states, 1, &output, &sources, 1, &metrics, n, f.scratch.ptr, f.scratch.len);
    var raw: p.Round = .{};
    const nc = api.rm_metric_owner_plan(f.owner, &indices, n, f.work.ptr, f.work.len, @intCast(at), &raw, null);
    if (duplicate) {
        try t.expectEqual(@intFromEnum(Rc.invalid_argument), oc);
        try t.expectEqual(@intFromEnum(p.Code.invalid_contract), nc);
        return .{ .raw = raw, .source = std.mem.zeroes(op.SourcePlanOutput), .at = at };
    }
    try oldOk(oc);
    try newOk(nc);
    f.plan_epoch += 1;
    try t.expectEqual(@as(u32, 1), output.source_plan_count);
    try t.expectEqual(@as(u32, n), output.metric_plan_count);
    try t.expectEqual(@as(u32, n), raw.count);
    for (metrics, 0..) |metric, i| {
        try t.expectEqual(metric.rule_handle, raw.queries.rule_ids.?[i]);
        try t.expectEqual(metric.metric_handle, raw.queries.metric_ids.?[i]);
        try t.expectEqual(metric.source_handle, raw.queries.source_ids.?[i]);
        try t.expectEqual(metric.value_kind, raw.queries.value_kinds.?[i]);
    }
    return .{ .raw = raw, .source = sources[0], .at = at };
}

fn fillNew(raw: p.Round, input: [n]Sample, at: u64) void {
    var used: u32 = 0;
    for (input, 0..) |sample, i| {
        const r = raw.observations;
        r.status.?[i] = sample.status;
        r.kinds.?[i] = value_kinds[i];
        r.presence.?[i] = sample.presence;
        r.flags.?[i] = sample.flags;
        r.low.?[i] = sample.low;
        r.high.?[i] = sample.high;
        r.tick_frequency.?[i] = sample.frequency;
        r.observed_ms.?[i] = @intCast(at);
        r.capability_masks.?[i] = 1;
        r.quality.?[i] = 100;
        r.payload_offsets.?[i] = used;
        r.payload_lengths.?[i] = @intCast(sample.bytes.len);
        @memcpy(raw.payload.?[used..][0..sample.bytes.len], sample.bytes);
        used += @intCast(sample.bytes.len);
    }
}

fn beginOld(f: *Fixture, planned: Planned, input: [n]Sample, invalid: bool) !op.FinalizeInput {
    f.sequence += 1;
    var h = std.mem.zeroes(op.CompletionHeader);
    h.abi_version = op.abi_version;
    h.struct_size = @sizeOf(op.CompletionHeader);
    h.configuration_generation = 1;
    h.catalog_generation = f.catalog_generation;
    h.operation_epoch = f.epoch();
    h.plan_epoch = f.plan_epoch;
    h.source_handle = 10;
    h.source_incarnation = 1;
    h.source_observation_sequence = f.sequence;
    h.capability_generation = planned.source.capability_generation;
    h.plan_token_fingerprint = planned.source.plan_token_fingerprint;
    h.command_at_milliseconds = planned.at;
    h.captured_at_milliseconds = planned.at;
    h.requested_rule_count = n;
    h.observation_count = n;
    h.status = 1;
    try oldOk(old.rm_metric_snapshot_begin_completion(f.old_handle, &h, f.scratch.ptr, f.scratch.len));
    var requested: [n]op.RequestedMetricInput = undefined;
    var observations: [n]op.ObservationInput = undefined;
    for (input, 0..) |sample, i| {
        requested[i] = std.mem.zeroes(op.RequestedMetricInput);
        requested[i].struct_size = @sizeOf(op.RequestedMetricInput);
        requested[i].rule_handle = 100 + i;
        var o = std.mem.zeroes(op.ObservationInput);
        o.struct_size = @sizeOf(op.ObservationInput);
        o.rule_handle = 100 + i;
        o.metric_handle = 1000 + i;
        o.source_handle = 10;
        o.scope_handle = 1;
        o.source_observation_sequence = f.sequence;
        o.observed_at_milliseconds = planned.at;
        o.capability_mask = 1;
        o.valid_mask = op.ObservationValid.required | (if (sample.status == 1) op.ObservationValid.value else @as(u64, 0));
        o.status = sample.status;
        o.value_kind = value_kinds[i];
        o.flags = sample.flags;
        o.value_bits = sample.low;
        o.value_high_bits = sample.high;
        o.quality = 100;
        o.byte_count = @intCast(sample.bytes.len);
        o.counter_ticks_per_second = sample.frequency;
        @memcpy(o.value_bytes[0..sample.bytes.len], sample.bytes);
        observations[i] = o;
    }
    try oldOk(old.rm_metric_snapshot_submit_requested(f.old_handle, &requested, n));
    const before_submit: ?op.SnapshotHeader = if (invalid) try f.header() else null;
    const submitted = old.rm_metric_snapshot_submit_observations(f.old_handle, &observations, n);
    if (invalid) try t.expectEqual(@intFromEnum(Rc.invalid_argument), submitted) else try oldOk(submitted);
    if (before_submit) |before| try t.expectEqualDeep(before, try f.header());
    var finish = std.mem.zeroes(op.FinalizeInput);
    finish.abi_version = op.abi_version;
    finish.struct_size = @sizeOf(op.FinalizeInput);
    finish.configuration_generation = 1;
    finish.catalog_generation = f.catalog_generation;
    finish.operation_epoch = h.operation_epoch;
    finish.plan_epoch = h.plan_epoch;
    finish.source_handle = 10;
    finish.command_at_milliseconds = planned.at;
    return finish;
}

const Diagnostic = enum { current, unavailable, unsupported };
const Observable = struct {
    metric: u64,
    rule: u64 = 0,
    diagnostic: Diagnostic,
    kind: u32 = 0,
    presence: u32 = 0,
    low: u64 = 0,
    high: u64 = 0,
    observed: u64 = 0,
    duration: u64 = 0,
    quality: u32 = 0,
    mask: u64 = 0,
    length: u32 = 0,
    bytes: [512]u8 = @splat(0),
};
fn oldObservable(value: op.MetricOutput) Observable {
    var out = Observable{ .metric = value.metric_handle, .diagnostic = if (value.status == 1) .current else if (value.status == 4) .unsupported else .unavailable };
    if (out.diagnostic != .current) return out; // diagnostic-only, never a manufactured last-good reference
    out.rule = value.winning_rule_handle;
    out.kind = value.value_kind;
    out.presence = 1;
    out.low = value.value_bits;
    out.high = value.value_high_bits;
    out.observed = value.observed_at_milliseconds;
    out.duration = value.sample_duration_milliseconds;
    out.quality = value.quality;
    out.mask = value.capability_mask;
    out.length = value.byte_count;
    @memcpy(out.bytes[0..out.length], value.value_bytes[0..out.length]);
    return out;
}
fn newObservable(f: *Fixture, i: usize) !Observable {
    // Read actual SharedSection positions, not private rule values or predicted outputs.
    const result = try st.resultTable(f.owner);
    var route: p.ResultRoute = .{};
    try newOk(api.rm_metric_owner_result_route(f.owner, @intCast(i), &route, null));
    // Collector diagnostics are result4; result11 is the separate public
    // last-good status and is not the legacy collector's comparison domain.
    const status = result.load(u32, 4, route.row);
    const raw = try st.table(f.owner, result.load(u32, 9, route.row));
    const row = result.load(u32, 10, route.row);
    var out = Observable{ .metric = route.metric_id, .diagnostic = if (status == 1) .current else if (status == 4) .unsupported else .unavailable };
    if (out.diagnostic != .current) return out;
    try t.expectEqual(result.load(u64, 1, route.row), raw.load(u64, 15, row));
    try t.expectEqual(result.load(u64, 8, route.row), raw.load(u64, 0, row));
    try t.expectEqual(result.load(u64, 3, route.row), raw.load(u64, 5, row));
    out.rule = raw.load(u64, 15, row);
    out.kind = raw.load(u32, 3, row);
    out.presence = raw.load(u32, 4, row);
    out.low = raw.load(u64, 1, row);
    out.high = raw.load(u64, 2, row);
    out.observed = @intCast(raw.load(i64, 6, row));
    out.duration = raw.load(u64, 7, row);
    out.quality = raw.load(u32, 13, row);
    out.mask = raw.load(u64, 14, row);
    if (out.kind == 5 and out.presence == 1) {
        const ref = raw.load(u64, 8, row);
        out.length = @intCast(ref >> 32);
        const start: u32 = @truncate(ref);
        @memcpy(out.bytes[0..out.length], raw.layout.dataArea(raw.bytes())[raw.specs[9].offset + start ..][0..out.length]);
    }
    return out;
}
fn compare(f: *Fixture) !void {
    const values = try f.oldValues();
    for (values, 0..) |value, i| try t.expectEqualDeep(oldObservable(value), try newObservable(f, i));
}

const Saved = struct {
    header: op.SnapshotHeader,
    values: [n]op.MetricOutput,
    source: op.SourceOutput,
    region: []u8,
    fn take(f: *Fixture) !Saved {
        return .{ .header = try f.header(), .values = try f.oldValues(), .source = try f.oldSource(), .region = try t.allocator.dupe(u8, f.region) };
    }
    fn deinit(s: Saved) void {
        t.allocator.free(s.region);
    }
    fn unchanged(s: Saved, f: *Fixture, check_header: bool) !void {
        if (check_header) try t.expectEqualDeep(s.header, try f.header());
        try t.expectEqualDeep(s.values, try f.oldValues());
        try t.expectEqualDeep(s.source, try f.oldSource());
        try t.expectEqualSlices(u8, s.region, f.region);
    }
};

fn execute(f: *Fixture, command: Command) !void {
    std.debug.print("COMMON kind={s} at={d} sample={d} counter={d}\n", .{ @tagName(command.kind), command.at, command.ordinal, f.counter_kind });
    switch (command.kind) {
        .bad_catalog, .capacity_catalog, .duplicate_plan => {
            const before = try Saved.take(f);
            defer before.deinit();
            if (command.kind == .duplicate_plan) {
                _ = try plan(f, command.at, true);
            } else try replace(f, if (command.kind == .capacity_catalog) max_rows else n, command.kind == .bad_catalog, command.at);
            try before.unchanged(f, true);
        },
        .replace_identity => {
            f.identity_revision += 1;
            try replace(f, n, false, command.at);
        },
        else => {
            var input = samples(f.counter_kind, command.ordinal, command.kind == .missing);
            if (command.kind == .invalid_observation) input[3].low = 2; // same malformed Boolean in both ABIs
            const before_invalid: ?Saved = if (command.kind == .invalid_observation) try Saved.take(f) else null;
            defer if (before_invalid) |saved| saved.deinit();
            for (input, 0..) |value, i| std.debug.print("INPUT row={d} type={d} status={d} presence={d} flags={d} low={x} high={x} ticks={d} bytes={any}\n", .{ i, value_kinds[i], value.status, value.presence, value.flags, value.low, value.high, value.frequency, value.bytes });
            const planned = try plan(f, command.at, false);
            var finish = try beginOld(f, planned, input, command.kind == .invalid_observation);
            fillNew(planned.raw, input, command.at);
            if (command.kind == .invalid_observation) {
                try t.expectEqual(@intFromEnum(p.Code.invalid_contract), api.rm_metric_owner_prepare(f.owner, planned.raw.token, null));
                try before_invalid.?.unchanged(f, false); // valid Plan/Begin advanced private header first
                try oldOk(old.rm_metric_snapshot_abort_completion(f.old_handle, &finish));
                try newOk(api.rm_metric_owner_abort_detach(f.owner, null));
            } else if (command.kind == .stale_commit) {
                const before = try Saved.take(f);
                defer before.deinit();
                finish.plan_epoch += 1;
                try t.expectEqual(@intFromEnum(Rc.abi_mismatch), old.rm_metric_snapshot_finalize_completion(f.old_handle, &finish));
                try newOk(api.rm_metric_owner_prepare(f.owner, planned.raw.token, null));
                try t.expectEqual(@intFromEnum(p.Code.stale_plan), api.rm_metric_owner_commit(f.owner, planned.raw.token + 1, @intCast(command.at), null));
                try before.unchanged(f, true);
                finish.plan_epoch -= 1;
                try oldOk(old.rm_metric_snapshot_abort_completion(f.old_handle, &finish));
                try newOk(api.rm_metric_owner_abort_detach(f.owner, null));
            } else {
                try oldOk(old.rm_metric_snapshot_finalize_completion(f.old_handle, &finish));
                try newOk(api.rm_metric_owner_prepare(f.owner, planned.raw.token, null));
                try newOk(api.rm_metric_owner_commit(f.owner, planned.raw.token, @intCast(command.at), null));
            }
        },
    }
    try compare(f);
}

test "application equivalence common collector commands typed values ratios reset rejection and identity" {
    for (counter_kinds) |kind| {
        var f = try Fixture.init(kind);
        defer f.deinit();
        try replace(&f, n, false, 100);
        for (commands) |command| try execute(&f, command);
    }
    std.debug.print("COMMON collector commands={d}\n", .{commands.len * counter_kinds.len});
}

test "application equivalence actual current-cell API typed null empty and exact values" {
    var f = try Fixture.init(13);
    defer f.deinit();
    // Legacy current cells have no collector catalog or range declaration.
    // Admit the same current-cell domain at the owner's declaration boundary;
    // a legacy collector catalog would reject the empty-byte domain itself.
    var declarations = Declarations.init(13, 0);
    declarations.minima[4] = 0;
    declarations.minima[5] = 0;
    const catalog = declarations.catalog(n);
    try newOk(api.rm_metric_owner_replace_catalog(f.owner, &catalog, f.work.ptr, f.work.len, 100, null));
    for (0..3) |step| {
        var input = samples(14, @intCast(step), false);
        input[5] = .{}; // typed empty bytes remain in the actual current-cell lane
        input[7] = .{ .low = @bitCast(@as(f64, 12.5)) };
        if (step != 1) {
            input[0] = .{ .presence = 2 };
            input[4] = .{ .presence = 2 };
        }
        for (input, 0..) |value, i| std.debug.print("CURRENT INPUT command={d} row={d} type={d} presence={d} low={x} high={x} bytes={any}\n", .{ step, i, value_kinds[i], value.presence, value.low, value.high, value.bytes });
        var cells: [n]op.CurrentValue = undefined;
        var payload: [64]u8 = @splat(0);
        var used: u32 = 0;
        for (input, 0..) |value, i| {
            cells[i] = .{ .dataset_handle = 1, .cell_handle = 1000 + i, .value_kind = value_kinds[i], .flags = if (value.presence == 2) 0 else 1, .value_bits = if (value_kinds[i] == 5) used else value.low, .value_high_bits = if (value_kinds[i] == 5) value.bytes.len else value.high };
            @memcpy(payload[used..][0..value.bytes.len], value.bytes);
            used += @intCast(value.bytes.len);
        }
        const commit = op.CurrentCommitInput{ .abi_version = op.abi_version, .struct_size = @sizeOf(op.CurrentCommitInput), .cell_capacity = n, .byte_capacity = 64, .flags = 0, .reserved = 0 };
        try oldOk(old.rm_metric_snapshot_commit_current(f.old_handle, &commit, &.{1}, 1, null, 0, &cells, n, &payload, used, f.current_resident.ptr, f.current_resident.len));
        // This lane compares actual native current cells; it cannot use old
        // collector ObservationInput, whose validPayload contract has no null.
        var raw: p.Round = .{};
        const indices = [_]u32{ 0, 1, 2, 3, 4, 5, 6, 7 };
        // Row7 is a scalar in BOTH initial cold declarations in this lane.
        try newOk(api.rm_metric_owner_plan(f.owner, &indices, n, f.work.ptr, f.work.len, @intCast(1000 + step * 1000), &raw, null));
        fillNew(raw, input, 1000 + step * 1000);
        try newOk(api.rm_metric_owner_prepare(f.owner, raw.token, null));
        try newOk(api.rm_metric_owner_commit(f.owner, raw.token, @intCast(1000 + step * 1000), null));
        var output: op.CurrentReadOutput = undefined;
        var actual: [n]op.CurrentValue = undefined;
        var bytes: [64]u8 = undefined;
        try oldOk(old.rm_metric_snapshot_read_current(f.old_handle, op.abi_version, &output, &actual, n, &bytes, bytes.len));
        try t.expectEqual(@as(u32, n), output.cell_count);
        for (actual, 0..) |value, i| {
            const newer = try newObservable(&f, i);
            try t.expectEqual(value.cell_handle, newer.metric);
            try t.expectEqual(value.value_kind, newer.kind);
            try t.expectEqual(if (value.flags == 0) @as(u32, 2) else 1, newer.presence);
            if (value.value_kind == 5) {
                try t.expectEqual(value.value_high_bits, newer.length);
                try t.expectEqualSlices(u8, bytes[@intCast(value.value_bits)..][0..@intCast(value.value_high_bits)], newer.bytes[0..newer.length]);
            } else {
                try t.expectEqual(value.value_bits, newer.low);
                try t.expectEqual(value.value_high_bits, newer.high);
            }
        }
        std.debug.print("COMMON current-cell command={d}\n", .{step});
    }
}

test "application equivalence semantic recovery roundtrip corruption and baseline continuation" {
    var source = try Fixture.init(14);
    defer source.deinit();
    var target = try Fixture.init(14);
    defer target.deinit();
    try replace(&source, n, false, 100);
    try replace(&target, n, false, 100);
    try execute(&source, commands[0]);
    try execute(&source, commands[1]);
    const read = try source.readInput();
    var header: op.PersistenceHeader = undefined;
    var sources: [1]op.SourcePersistenceOutput = undefined;
    var rules: [n]op.RuleStateOutput = undefined;
    try oldOk(old.rm_metric_snapshot_export_state(source.old_handle, &read, &header, &sources, 1, &rules, n, null, 0));
    var image_size: u64 = 0;
    var restore_size: u64 = 0;
    try newOk(support.rm_metric_owner_checkpoint_size(source.owner, &image_size, &restore_size, null));
    const image = try t.allocator.alloc(u8, @intCast(image_size));
    defer t.allocator.free(image);
    const work = try t.allocator.alignedAlloc(u8, .@"8", @intCast(restore_size));
    defer t.allocator.free(work);
    var written: u64 = 0;
    try newOk(support.rm_metric_owner_checkpoint_encode(source.owner, image.ptr, image.len, &written, null));
    try t.expectEqual(image_size, written);
    std.debug.print("RECOVERY old_checksum={x} new_image_bytes={d} new_image_hash={x}\n", .{ header.checksum, image_size, std.hash.Wyhash.hash(0, image) });
    var restore = std.mem.zeroes(op.PersistenceInput);
    restore.abi_version = op.abi_version;
    restore.struct_size = @sizeOf(op.PersistenceInput);
    restore.configuration_generation = 1;
    restore.catalog_generation = 1;
    restore.operation_epoch = 100;
    restore.command_at_milliseconds = 3000;
    var corrupt_header = header;
    corrupt_header.checksum ^= 1;
    const corrupt_image = try t.allocator.dupe(u8, image);
    defer t.allocator.free(corrupt_image);
    corrupt_image[corrupt_image.len - 1] ^= 1;
    const before = try Saved.take(&target);
    defer before.deinit();
    var prepared: checkpoint.Prepared = .{};
    try t.expectEqual(@intFromEnum(Rc.invalid_argument), old.rm_metric_snapshot_import_state(target.old_handle, &restore, &corrupt_header, &sources, 1, &rules, n, null, 0));
    try t.expectEqual(@intFromEnum(p.Code.invalid_contract), support.rm_metric_owner_checkpoint_prepare(target.owner, corrupt_image.ptr, corrupt_image.len, work.ptr, work.len, &prepared, null));
    try before.unchanged(&target, true);
    try oldOk(old.rm_metric_snapshot_import_state(target.old_handle, &restore, &header, &sources, 1, &rules, n, null, 0));
    try newOk(support.rm_metric_owner_checkpoint_prepare(target.owner, image.ptr, image.len, work.ptr, work.len, &prepared, null));
    try newOk(support.rm_metric_owner_checkpoint_publish(target.owner, &prepared, 3000, null));
    try t.expect(prepared.work == null);
    try compare(&target);
    const old_source = try source.oldValues();
    const old_target = try target.oldValues();
    for (old_source, old_target, 0..) |left, right, i| {
        try t.expectEqualDeep(oldObservable(left), oldObservable(right));
        try t.expectEqualDeep(try newObservable(&source, i), try newObservable(&target, i));
    }
    // Both implementations restore the source too: source and target must have
    // the same command history before their continuations are compared. Legacy
    // restore clears generic baselines, so comparing a restored target with an
    // unrestored source would test two different intervals.
    try oldOk(old.rm_metric_snapshot_import_state(source.old_handle, &restore, &header, &sources, 1, &rules, n, null, 0));
    try newOk(support.rm_metric_owner_checkpoint_prepare(source.owner, image.ptr, image.len, work.ptr, work.len, &prepared, null));
    try newOk(support.rm_metric_owner_checkpoint_publish(source.owner, &prepared, 3000, null));
    try t.expect(prepared.work == null);
    try compare(&source);
    source.operation = 100;
    target.operation = 100;
    target.plan_epoch = source.plan_epoch;
    target.sequence = source.sequence;
    // Keep both old/new continuations and source/target comparison exact.
    try execute(&source, commands[7]);
    try execute(&target, commands[7]);
    const continued_source = try source.oldValues();
    const continued_target = try target.oldValues();
    for (continued_source, continued_target) |left, right| try t.expectEqualDeep(oldObservable(left), oldObservable(right));
    std.debug.print("COMMON recovery commands=7 (seed2, corrupt, restore-target, restore-source, continue-source, continue-target)\n", .{});
}

fn failingFixture(allocator: std.mem.Allocator) !void {
    var f = try Fixture.initUsing(allocator, 14);
    defer f.deinit();
    try replace(&f, n, false, 100);
    try compare(&f);
}

test "application equivalence caller allocations are leak checked with every allocation failure" {
    // The unchanged legacy create ABI hardcodes page_allocator; its allocations
    // are explicitly destroyed but cannot be instrumented through this ABI.
    // All allocations owned by this test fixture are injected and leak checked.
    try t.checkAllAllocationFailures(t.allocator, failingFixture, .{});
}

test "application equivalence explicitly distinct ABI layouts and bounded command manifest" {
    try t.expectEqual(@as(u32, 0x000e0000), old.rm_metric_snapshot_abi_version());
    try t.expectEqual(@as(u32, 0x00010001), p.version);
    try t.expectEqual(@as(usize, 640), @sizeOf(op.ObservationInput));
    try t.expectEqual(@as(usize, 264), @sizeOf(p.Round));
    try t.expectEqual(@as(usize, 144), @sizeOf(p.Observations));
    try t.expectEqual(@as(usize, 104), @sizeOf(p.TableBinding));
    try t.expectEqual(@as(usize, 128), @offsetOf(op.ObservationInput, "value_bytes"));
    try t.expectEqual(@as(usize, 168), @offsetOf(p.Round, "payload"));
    try t.expectEqual(@as(usize, 184), @offsetOf(p.Round, "queries"));
    try t.expectEqual(@as(usize, 56), @sizeOf(checkpoint.Prepared));
    try t.expect(@sizeOf(op.PersistenceHeader) != @sizeOf(checkpoint.Prepared));
    std.debug.print("COMMON manifest command_count={d} rows={d} counter_kinds={any}; no wire-image comparison\n", .{ shared_command_count, n, counter_kinds });
}
