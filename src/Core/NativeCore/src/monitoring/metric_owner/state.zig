const std = @import("std");
const p = @import("protocol.zig");
const s = @import("storage.zig");
pub const Baseline = @import("counter_core.zig").Baseline;
pub const Source = struct { id: u64 = 0, incarnation: u64 = 0, capability: u64 = 0, rows: u32 = 0, availability: u32 = 1, planned_availability: u32 = 1, suppressed: bool = false, binding_ordinal: u32 = 0, access_region: u32 = 0, first_rule: u32 = p.none, last_rule: u32 = p.none, heap_start: u32 = 0, heap_count: u32 = 0, binding: p.TableBinding };
pub const Rule = struct {
    id: u64 = 0,
    identity: u64 = 0,
    source: u32 = 0,
    metric: u32 = 0,
    row: u32 = 0,
    next_metric: u32 = p.none,
    next_source: u32 = p.none,
    heap_position: u32 = p.none,
    kind: u32 = 0,
    flags: u32 = 0,
    priority: u32 = 0,
    value_kind: u32 = 0,
    minimum: u64 = 0,
    maximum: u64 = 0,
    capability_mask: u64 = 0,
    suppressed: bool = false,
    baseline: Baseline = .{},
};
pub const Metric = struct { id: u64 = 0, identity: u64 = 0, first_rule: u32 = p.none, last_rule: u32 = p.none, value_kind: u32 = 0, result_group: u32 = 0, result_row: u32 = 0, affected_token: u64 = 0, attempted: bool = false };
pub const ResultCandidate = [9]u64;
pub fn resultSlot(index: usize) usize {
    return if (index == 8) 11 else index + 1;
}
/// Only per-operation candidate values. Never retained in active state.
pub const Delta = struct {
    low: u64 = 0,
    high: u64 = 0,
    generation: u64 = 0,
    old_generation: u64 = 0,
    observed: i64 = 0,
    attempt: i64 = 0,
    duration: u64 = 0,
    payload_ref: u64 = 0,
    kind: u32 = 1,
    presence: u32 = 0,
    status: u32 = 2,
    reason: u32 = 0,
    capability_mask: u64 = 0,
    quality: u32 = 0,
    baseline: Baseline = .{},
    write_value: bool = false,
    invalidate: bool = false,
    suppressed: bool = false,
};
pub const Work = struct {
    region: ?[*]align(8) u8 = null,
    length: usize = 0,
    raw: p.Round = .{},
    requested: []u32 = &.{},
    request_count: u32 = 0,
    deltas: []Delta = &.{},
    expected: []u32 = &.{},
    affected: []u32 = &.{},
    affected_count: u32 = 0,
    affected_ids: []u64 = &.{},
    invalidated_aliases: []u32 = &.{},
    invalidated_alias_count: u32 = 0,
    receipt: ?*p.Receipt = null,
    invalid_sources: []bool = &.{},
    unsupported_sources: []bool = &.{},
    unsupported_reasons: []u32 = &.{},
    opens: []s.Open = &.{},
    tables: []s.Table = &.{},
    payload: []u8 = &.{},
    utc: i64 = 0,
    prepared: bool = false,
    synthetic: []bool = &.{},
    npu_plan: ?@import("vector_inputs.zig").NpuPlan = null,
    gpu_pending: ?*@import("vector_inputs.zig").GpuPrepared = null,
};
pub const Owner = struct {
    magic: u64 = live_magic,
    config: p.Config,
    sizes: p.Sizes,
    active_length: u64,
    sources: []Source,
    bindings: []p.SourceBinding,
    rules: []Rule,
    metrics: []Metric,
    payload_heap: []u32,
    source_count: u32 = 0,
    rule_count: u32 = 0,
    metric_count: u32 = 0,
    result: p.TableBinding,
    results: [2]p.TableBinding,
    result_rows: [2]u32 = .{ 0, 0 },
    result_count: u32 = 1,
    legacy_bindings: bool = false,
    gpu_vector: ?@import("vector_inputs.zig").BoundVector = null,
    catalog_generation: u64 = 0,
    token: u64 = 0,
    owner_epoch: u64 = 0,
    round_generation: u64 = 0,
    last_utc_ms: i64 = 0,
    work: Work = .{},
    counters: s.Counters = .{},
    failure: p.Failure = .{},
    pub const live_magic: u64 = 0x4d45545249434f31;
    pub fn detach(o: *Owner) void {
        o.work = .{};
    }
    pub fn fail(o: *Owner, code: p.Code, family: p.Family, required: u64, limit: u64) p.Code {
        o.failure = .{ .code = code, .family = family, .required = required, .limit = limit };
        return code;
    }
};

fn add(comptime T: type, cursor: *u64, count: u64) void {
    const padding = std.math.add(u64, cursor.*, @alignOf(T) - 1) catch {
        cursor.* = std.math.maxInt(u64);
        return;
    };
    const aligned = padding & ~@as(u64, @alignOf(T) - 1);
    const bytes = std.math.mul(u64, @sizeOf(T), count) catch {
        cursor.* = std.math.maxInt(u64);
        return;
    };
    cursor.* = std.math.add(u64, aligned, bytes) catch std.math.maxInt(u64);
}
pub fn sizes(c: p.Config) p.Sizes {
    var active: u64 = 0;
    add(Owner, &active, 1);
    add(Source, &active, c.source_capacity);
    add(p.SourceBinding, &active, c.source_capacity);
    add(Rule, &active, c.rule_capacity);
    add(Metric, &active, c.metric_capacity);
    add(u32, &active, c.rule_capacity);
    var round: u64 = 0;
    // Field order is mirrored by allocateRound.
    add(u32, &round, c.metric_capacity);
    inline for (0..6) |_| add(u32, &round, c.rule_capacity);
    inline for (0..6) |_| add(u64, &round, c.rule_capacity);
    add(i64, &round, c.rule_capacity);
    inline for (0..2) |_| add(u32, &round, c.rule_capacity);
    add(u64, &round, c.rule_capacity);
    add(u32, &round, c.rule_capacity);
    inline for (0..7) |_| add(u64, &round, c.rule_capacity);
    inline for (0..2) |_| add(u32, &round, c.rule_capacity);
    add(Delta, &round, c.rule_capacity);
    add(u32, &round, c.rule_capacity);
    add(u32, &round, c.metric_capacity);
    add(bool, &round, c.rule_capacity);
    add(bool, &round, c.source_capacity);
    add(bool, &round, c.source_capacity);
    add(u32, &round, c.source_capacity);
    add(s.Open, &round, @as(u64, c.source_capacity) + 2);
    add(s.Table, &round, @as(u64, c.source_capacity) + 2);
    add(u64, &round, c.metric_capacity);
    add(u32, &round, c.metric_capacity);
    add(p.Receipt, &round, 1);
    add(u8, &round, c.payload_work_bytes);
    var catalog = catalogInputBytes(c);
    add(Source, &catalog, c.source_capacity);
    add(p.SourceBinding, &catalog, c.source_capacity);
    add(Rule, &catalog, c.rule_capacity);
    add(Metric, &catalog, c.metric_capacity);
    add(ResultCandidate, &catalog, c.metric_capacity);
    add(Delta, &catalog, c.rule_capacity);
    add(s.Open, &catalog, @as(u64, c.source_capacity) * 2 + 2);
    add(s.Table, &catalog, @as(u64, c.source_capacity) * 2 + 2);
    add(bool, &catalog, @as(u64, c.source_capacity) * 2 + 2);
    add(u8, &catalog, c.payload_work_bytes);
    return .{ .active = active, .work = @max(round, catalog) };
}
/// Same field order/alignment as allocateCatalogInput, without allocating or
/// setting existing caller-filled input bytes to undefined.
pub fn catalogInputBytes(c: p.Config) u64 {
    var catalog: u64 = 0;
    inline for (0..3) |_| add(u64, &catalog, c.source_capacity);
    add(u32, &catalog, c.source_capacity);
    // Allocator.alloc(0) returns a sentinel without advancing/alignment.
    if (c.rule_capacity != 0) {
        inline for (0..4) |_| add(u64, &catalog, c.rule_capacity);
        inline for (0..4) |_| add(u32, &catalog, c.rule_capacity);
        inline for (0..3) |_| add(u64, &catalog, c.rule_capacity);
    }
    if (c.metric_capacity != 0) inline for (0..2) |_| add(u64, &catalog, c.metric_capacity);
    return catalog;
}
/// Reserve a deterministic input prefix in the same work lease as replacement
/// scratch. No native pointer is retained after returning this descriptor.
pub fn allocateCatalogInput(a: std.mem.Allocator, c: p.Config) !p.Catalog {
    var out: p.Catalog = undefined;
    out.sources = .{ .count = c.source_capacity, .ids = (try a.alloc(u64, c.source_capacity)).ptr, .incarnations = (try a.alloc(u64, c.source_capacity)).ptr, .capabilities = (try a.alloc(u64, c.source_capacity)).ptr, .availability = (try a.alloc(u32, c.source_capacity)).ptr };
    out.rules = .{ .count = c.rule_capacity, .ids = (try a.alloc(u64, c.rule_capacity)).ptr, .source_ids = (try a.alloc(u64, c.rule_capacity)).ptr, .metric_ids = (try a.alloc(u64, c.rule_capacity)).ptr, .identities = (try a.alloc(u64, c.rule_capacity)).ptr, .kinds = (try a.alloc(u32, c.rule_capacity)).ptr, .flags = (try a.alloc(u32, c.rule_capacity)).ptr, .priorities = (try a.alloc(u32, c.rule_capacity)).ptr, .value_kinds = (try a.alloc(u32, c.rule_capacity)).ptr, .minimum_bits = (try a.alloc(u64, c.rule_capacity)).ptr, .maximum_bits = (try a.alloc(u64, c.rule_capacity)).ptr, .capability_masks = (try a.alloc(u64, c.rule_capacity)).ptr };
    out.metrics = .{ .count = c.metric_capacity, .ids = (try a.alloc(u64, c.metric_capacity)).ptr, .identities = (try a.alloc(u64, c.metric_capacity)).ptr };
    return out;
}
pub fn allocateRound(o: *Owner, bytes: []align(8) u8) !Work {
    var fba = std.heap.FixedBufferAllocator.init(bytes);
    const a = fba.allocator();
    const n = o.config.rule_capacity;
    var w = Work{ .region = bytes.ptr, .length = bytes.len };
    w.raw.observations.capacity = n;
    w.raw.queries.capacity = n;
    // This is the owner's round layout, in exactly sizes() order. Allocator.alloc
    // poisons the whole admitted extent even when only one row will be used.
    // FBA rawAlloc only reserves/alignment-checks the borrowed bytes. No general
    // region utility or allocator owner is introduced; used rows initialize in
    // plan/prepare before any read, and unused capacity stays untouched.
    inline for (.{ "requested", "raw", "deltas", "expected", "affected", "synthetic", "invalid_sources", "unsupported_sources", "unsupported_reasons", "opens", "tables", "affected_ids", "invalidated_aliases", "receipt", "payload" }) |field| {
        if (comptime std.mem.eql(u8, field, "raw")) {
            inline for (.{ "observations", "queries" }) |group| {
                const Columns = @TypeOf(@field(w.raw, group));
                inline for (@typeInfo(Columns).@"struct".field_names, @typeInfo(Columns).@"struct".field_types) |column_name, column_type| {
                    if (@typeInfo(column_type) == .optional) {
                        const T = @typeInfo(@typeInfo(column_type).optional.child).pointer.child;
                        const length = try std.math.mul(usize, n, @sizeOf(T));
                        const ptr = if (length == 0)
                            @as([*]u8, @ptrFromInt(std.mem.alignBackward(usize, std.math.maxInt(usize), @alignOf(T))))
                        else
                            a.rawAlloc(length, .of(T), @returnAddress()) orelse return error.OutOfMemory;
                        const typed: [*]T = @ptrCast(@alignCast(ptr));
                        @field(@field(w.raw, group), column_name) = typed;
                    }
                }
            }
        } else {
            const receipt = comptime std.mem.eql(u8, field, "receipt");
            const T = if (receipt) p.Receipt else @typeInfo(@TypeOf(@field(w, field))).pointer.child;
            const count: usize = if (comptime std.mem.eql(u8, field, "payload"))
                std.math.cast(usize, o.config.payload_work_bytes) orelse return error.OutOfMemory
            else if (receipt)
                1
            else if (comptime std.mem.eql(u8, field, "opens") or std.mem.eql(u8, field, "tables"))
                try std.math.add(usize, o.config.source_capacity, 2)
            else if (comptime std.mem.eql(u8, field, "invalid_sources") or std.mem.eql(u8, field, "unsupported_sources") or std.mem.eql(u8, field, "unsupported_reasons"))
                o.config.source_capacity
            else if (comptime std.mem.eql(u8, field, "deltas") or std.mem.eql(u8, field, "expected") or std.mem.eql(u8, field, "synthetic"))
                n
            else
                o.config.metric_capacity;
            const length = try std.math.mul(usize, count, @sizeOf(T));
            const ptr = if (length == 0)
                @as([*]u8, @ptrFromInt(std.mem.alignBackward(usize, std.math.maxInt(usize), @alignOf(T))))
            else
                a.rawAlloc(length, .of(T), @returnAddress()) orelse return error.OutOfMemory;
            const typed: [*]T = @ptrCast(@alignCast(ptr));
            if (receipt) w.receipt = &typed[0] else @field(w, field) = typed[0..count];
        }
    }
    w.receipt.?.* = .{};
    w.raw.payload = w.payload.ptr;
    w.raw.payload_capacity = w.payload.len;
    return w;
}
pub fn table(o: *const Owner, source: u32) s.Error!s.Table {
    const src = o.sources[source];
    return s.Table.init(src.binding, src.rows, false);
}
pub fn resultTable(o: *const Owner) s.Error!s.Table {
    if (o.result_count != 1) return error.Invalid;
    return resultGroupTable(o, 0);
}
pub fn resultGroupTable(o: *const Owner, group: u32) s.Error!s.Table {
    if (group >= o.result_count) return error.Invalid;
    return s.Table.init(o.results[group], o.result_rows[group], true);
}
pub const ResultLocation = struct { table: s.Table, row: u32, group: u32 };
pub fn resultLocation(o: *const Owner, metric: u32) s.Error!ResultLocation {
    if (metric >= o.metric_count) return error.Invalid;
    const m = o.metrics[metric];
    return .{ .table = try resultGroupTable(o, m.result_group), .row = m.result_row, .group = m.result_group };
}
pub fn sourceIndexForBinding(o: *const Owner, ordinal: u32) ?u32 {
    for (o.sources[0..o.source_count], 0..) |src, i| if (src.binding_ordinal == ordinal) return @intCast(i);
    return null;
}
pub fn sourceTableByBinding(o: *const Owner, ordinal: u32) s.Error!s.Table {
    return table(o, sourceIndexForBinding(o, ordinal) orelse return error.Invalid);
}
pub fn readGood(t: *const s.Table, row: u32, c: *s.Counters) Delta {
    c.read +|= 84;
    return .{ .low = t.load(u64, 1, row), .high = t.load(u64, 2, row), .kind = t.load(u32, 3, row), .presence = t.load(u32, 4, row), .generation = t.load(u64, 5, row), .observed = t.load(i64, 6, row), .attempt = t.load(i64, 12, row), .duration = t.load(u64, 7, row), .payload_ref = t.load(u64, 8, row), .status = t.load(u32, 10, row), .reason = t.load(u32, 11, row), .quality = t.load(u32, 13, row), .capability_mask = t.load(u64, 14, row) };
}
pub fn writeGood(t: *const s.Table, w: *const s.ss.Writer, row: u32, d: Delta, c: *s.Counters) void {
    writeGoodImpl(t, w, row, d, c, true);
}
pub fn writeChangedGood(t: *const s.Table, w: *const s.ss.Writer, row: u32, d: Delta, c: *s.Counters) void {
    writeGoodImpl(t, w, row, d, c, false);
}
fn writeGoodImpl(t: *const s.Table, w: *const s.ss.Writer, row: u32, d: Delta, c: *s.Counters, full: bool) void {
    s.store(t, w, c, u64, 1, row, d.low);
    if (full or d.kind == 4) s.store(t, w, c, u64, 2, row, d.high);
    s.store(t, w, c, u32, 3, row, d.kind);
    s.store(t, w, c, u32, 4, row, d.presence);
    s.store(t, w, c, u64, 5, row, d.generation);
    s.store(t, w, c, i64, 6, row, d.observed);
    s.store(t, w, c, u64, 7, row, d.duration);
    if (full or d.kind == 5) s.store(t, w, c, u64, 8, row, d.payload_ref);
    s.store(t, w, c, u32, 13, row, d.quality);
    s.store(t, w, c, u64, 14, row, d.capability_mask);
}
pub fn metricStatus(status: u32) u32 {
    return switch (status) {
        1 => 1,
        3 => 4,
        4 => 5,
        else => 3,
    };
}
pub fn overlaps(a: usize, alen: usize, b: usize, blen: usize) bool {
    if (alen > std.math.maxInt(usize) - a or blen > std.math.maxInt(usize) - b) return true;
    return a < b + blen and b < a + alen;
}
pub fn bindingAlias(a: p.TableBinding, b: p.TableBinding) bool {
    if (a.region != b.region) return overlaps(@intFromPtr(a.region.?), @intCast(a.byte_length), @intFromPtr(b.region.?), @intCast(b.byte_length));
    for ([_]u32{ a.rows_source, a.value_source, a.health_source, a.provenance_source }) |as| for ([_]u32{ b.rows_source, b.value_source, b.health_source, b.provenance_source }) |bs| {
        if (as == bs) return true;
    };
    return false;
}
pub fn validWork(o: *const Owner, bytes: []align(8) u8) bool {
    if (bytes.len < o.sizes.work or bytes.len > o.config.work_limit or overlaps(@intFromPtr(bytes.ptr), bytes.len, @intFromPtr(o), @intCast(o.active_length))) return false;
    for (o.bindings) |binding| if (overlaps(@intFromPtr(bytes.ptr), bytes.len, @intFromPtr(binding.table.region.?), @intCast(binding.table.byte_length))) return false;
    for (o.results[0..o.result_count]) |binding| if (overlaps(@intFromPtr(bytes.ptr), bytes.len, @intFromPtr(binding.region.?), @intCast(binding.byte_length))) return false;
    return true;
}
