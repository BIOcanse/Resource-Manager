//! Bounded, field-ordered recovery image. No disk I/O or owned storage.
//! Schema1 covers kernel rows (including NPU derived metrics), not the separate
//! PDH engine vector or memory-only history rings; the old session image also
//! excluded managed Capture.ProjectPdhGpuEngineFields enrichment.
const std = @import("std");
const p = @import("protocol.zig");
const st = @import("state.zig");
const s = @import("storage.zig");
const old = @import("../metric_snapshot/protocol.zig");
const provenance = @import("provenance.zig");
const payload_index = @import("payload_index.zig");
pub const Error = error{ Invalid, Capacity, GenerationCapacity, Workspace, Shared, Stale, Truncated };
pub const magic: u64 = 0x3154504b434f4d52; // RMOCKPT1
pub const version: u32 = 1;
pub const Prepared = extern struct {
    work: ?[*]u8 = null,
    work_length: u64 = 0,
    image_length: u64 = 0,
    catalog_generation: u64 = 0,
    round_generation: u64 = 0,
    token: u64 = 0,
    checksum: u64 = 0,
};
const Header = struct { length: u64, catalog: u64, round: u64, token: u64, sources: u32, rules: u32, metrics: u32 };
const Reader = struct {
    bytes: []const u8,
    at: usize = 0,
    fn int(r: *Reader, comptime T: type) Error!T {
        if (r.at > r.bytes.len or @sizeOf(T) > r.bytes.len - r.at) return error.Truncated;
        const value = std.mem.readInt(T, r.bytes[r.at..][0..@sizeOf(T)], .little);
        r.at += @sizeOf(T);
        return value;
    }
    fn take(r: *Reader, length: usize) Error![]const u8 {
        if (r.at > r.bytes.len or length > r.bytes.len - r.at) return error.Truncated;
        const bytes = r.bytes[r.at..][0..length];
        r.at += length;
        return bytes;
    }
};
const Encoder = struct {
    bytes: []u8,
    at: usize = 0,
    fn int(w: *Encoder, comptime T: type, value: T) void {
        std.mem.writeInt(T, w.bytes[w.at..][0..@sizeOf(T)], value, .little);
        w.at += @sizeOf(T);
    }
    fn put(w: *Encoder, bytes: []const u8) void {
        @memcpy(w.bytes[w.at..][0..bytes.len], bytes);
        w.at += bytes.len;
    }
};
fn checksum(bytes: []const u8) u64 {
    const hash = std.hash.Wyhash.hash(0x726d6f636b707431, bytes);
    return if (hash == 0) 1 else hash;
}
fn header(r: *Reader) Error!Header {
    if (try r.int(u64) != magic or try r.int(u32) != version or try r.int(u32) != 64) return error.Invalid;
    const h = Header{ .length = try r.int(u64), .catalog = try r.int(u64), .round = try r.int(u64), .token = try r.int(u64), .sources = try r.int(u32), .rules = try r.int(u32), .metrics = try r.int(u32) };
    if (try r.int(u32) != 1 or h.length != r.bytes.len or h.catalog == 0) return error.Invalid;
    if (h.catalog == std.math.maxInt(u64) or h.round == std.math.maxInt(u64) or h.token == std.math.maxInt(u64)) return error.Capacity;
    if (h.token >> 32 == 0) return error.Invalid;
    return h;
}
fn baselineWrite(w: *Encoder, b: st.Baseline) void {
    w.int(u32, @intFromBool(b.valid));
    w.int(u32, b.schema);
    w.int(u64, b.low);
    w.int(u64, b.high);
    w.int(u64, b.frequency);
    w.int(u64, b.reference);
    w.int(i64, b.time);
    w.int(u64, b.monotonic);
}
fn baselineRead(r: *Reader, rule: st.Rule) Error!st.Baseline {
    const valid = try r.int(u32);
    if (valid > 1) return error.Invalid;
    const b = st.Baseline{ .valid = valid == 1, .schema = try r.int(u32), .low = try r.int(u64), .high = try r.int(u64), .frequency = try r.int(u64), .reference = try r.int(u64), .time = try r.int(i64), .monotonic = try r.int(u64) };
    if (b.time < 0) return error.Invalid;
    if (!b.valid) {
        if (b.schema != 0 or b.low != 0 or b.high != 0 or b.frequency != 0 or b.reference != 0 or b.time != 0 or b.monotonic != 0) return error.Invalid;
        return b;
    }
    const schema: u32 = if (rule.kind == 1) 16 else if (old.isFloatingCounterMetric(rule.kind)) 4 else if (old.isTimedCounterMetric(rule.kind)) 2 else 1;
    if ((!old.isCounterMetric(rule.kind) and rule.kind != 1) or b.schema != schema) return error.Invalid;
    if (schema == 16 and (b.frequency == 0 or b.high < b.low)) return error.Invalid;
    if (schema == 2 and b.frequency == 0) return error.Invalid;
    if (schema == 4) {
        const low: f64 = @bitCast(b.low);
        const high: f64 = @bitCast(b.high);
        if (!std.math.isFinite(low) or !std.math.isFinite(high) or low < 0 or high < 0) return error.Invalid;
    }
    return b;
}
fn contractWrite(w: *Encoder, o: *const st.Owner, r: st.Rule) void {
    w.int(u64, r.id);
    w.int(u64, r.identity);
    w.int(u64, o.sources[r.source].id);
    w.int(u64, o.metrics[r.metric].id);
    w.int(u32, r.kind);
    w.int(u32, r.flags);
    w.int(u32, r.priority);
    w.int(u32, r.value_kind);
    w.int(u64, r.minimum);
    w.int(u64, r.maximum);
    w.int(u64, r.capability_mask);
}
fn contractRead(r: *Reader, o: *const st.Owner, rule: st.Rule) Error!void {
    if (try r.int(u64) != rule.id or try r.int(u64) != rule.identity or try r.int(u64) != o.sources[rule.source].id or try r.int(u64) != o.metrics[rule.metric].id or
        try r.int(u32) != rule.kind or try r.int(u32) != rule.flags or try r.int(u32) != rule.priority or try r.int(u32) != rule.value_kind or
        try r.int(u64) != rule.minimum or try r.int(u64) != rule.maximum or try r.int(u64) != rule.capability_mask) return error.Stale;
}
const RuleImage = struct { value: st.Delta, attempt: i64, payload: []const u8 };
fn ruleRead(r: *Reader, o: *const st.Owner, rule: st.Rule) Error!RuleImage {
    try contractRead(r, o, rule);
    const base = try baselineRead(r, rule);
    const suppressed = try r.int(u32);
    if (suppressed > 1 or try r.int(u32) != 0) return error.Invalid;
    var d = st.Delta{ .low = try r.int(u64), .high = try r.int(u64), .generation = try r.int(u64), .observed = try r.int(i64), .duration = try r.int(u64), .kind = try r.int(u32), .presence = try r.int(u32), .status = try r.int(u32), .reason = try r.int(u32), .capability_mask = try r.int(u64), .quality = try r.int(u32), .baseline = base, .suppressed = suppressed == 1 };
    if (try r.int(u32) != 0) return error.Invalid;
    const attempt = try r.int(i64);
    const len = try r.int(u32);
    if (try r.int(u32) != 0) return error.Invalid;
    if (len > o.config.max_value_bytes) return error.Capacity;
    const payload = try r.take(len);
    if (d.kind != rule.value_kind or d.presence > 2 or d.status < 1 or d.status > 4 or d.generation == std.math.maxInt(u64) or d.observed < 0 or attempt < 0 or d.capability_mask & ~rule.capability_mask != 0) return error.Invalid;
    if ((d.suppressed and d.status != 3) or (d.suppressed and d.baseline.valid and d.baseline.schema != 16)) return error.Invalid;
    if (o.sources[rule.source].availability != 1 and d.baseline.valid and d.baseline.schema != 16) return error.Invalid;
    if (d.presence != 0 and d.generation == 0) return error.Invalid;
    if (d.status == 1 and d.presence == 0) return error.Invalid;
    if (d.kind != 4 and d.high != 0) return error.Invalid;
    if (d.kind == 5) {
        if (d.low != 0 or d.high != 0 or (d.presence == 1 and (len < rule.minimum or len > rule.maximum))) return error.Invalid;
    } else {
        if (len != 0) return error.Invalid;
        if (d.presence == 1 and d.kind != 4 and !old.validValue(d.kind, rule.flags, rule.minimum, rule.maximum, d.low)) return error.Invalid;
    }
    if (d.presence != 1 and len != 0) return error.Invalid;
    d.payload_ref = @as(u64, len) << 32;
    return .{ .value = d, .attempt = attempt, .payload = payload };
}
fn rawPayload(t: *const s.Table, d: st.Delta, max: u32) Error![]const u8 {
    const off: u32 = @truncate(d.payload_ref);
    const len: u32 = @intCast(d.payload_ref >> 32);
    if (len > max) return error.Capacity;
    if (off > t.specs[9].size or len > t.specs[9].size - off) return error.Invalid;
    const used = @atomicLoad(u32, &t.layout.position(t.bytes(), t.binding.positions[9]).length, .acquire);
    if (used > t.specs[9].size or (len != 0 and (off > used or len > used - off))) return error.Invalid;
    return t.layout.dataArea(t.bytes())[@as(usize, t.specs[9].offset) + off ..][0..len];
}
fn tableReady(t: *const s.Table, rows: u32) Error!void {
    if ((provenance.count(t) orelse return error.Stale) != rows) return error.Stale;
    for ([_]u32{ t.binding.value_source, t.binding.health_source, t.binding.provenance_source }) |source| {
        if (@atomicLoad(u64, &t.layout.source(t.bytes(), source).sequence, .acquire) > s.ss.ended_bit - 6) return error.GenerationCapacity;
    }
    const used: usize = if (t.result) 12 else 16;
    for (t.specs[0..used], 0..) |spec, slot| {
        const entry = t.layout.position(t.bytes(), t.binding.positions[slot]);
        const length = @atomicLoad(u32, &entry.length, .acquire);
        if (!t.result and slot == 9) {
            if (length > spec.size) return error.Invalid;
        } else {
            if (length != rows) return error.Stale;
            if (slot != 0 and @atomicLoad(u64, &entry.identity_sequence, .acquire) != t.identitySequence()) return error.Stale;
        }
    }
}
// Separate borrowed writers retain original source facts on admission failure.
// Kernel Open's provenance cleanup is not used for checkpoint admission.
const ProvenanceOpen = struct {
    writer: ?s.ss.Writer = null,
    state: s.ss.SourceState,
    reason: u32,
    time: i64,
    fn begin(t: *const s.Table) Error!ProvenanceOpen {
        const entry = t.layout.source(t.bytes(), t.binding.provenance_source);
        const prior = ProvenanceOpen{ .state = @atomicLoad(s.ss.SourceState, &entry.state, .acquire), .reason = @atomicLoad(u32, &entry.reason, .acquire), .time = @atomicLoad(i64, &entry.write_time_unix_ms, .acquire) };
        var out = prior;
        out.writer = t.writer(t.binding.provenance_source) catch return error.Shared;
        return out;
    }
    fn unchanged(open: *ProvenanceOpen) void {
        if (open.writer) |*w| {
            w.end(if (open.state == .has_value) .has_value else .{ .unavailable = open.reason }, open.time) catch unreachable;
            open.writer = null;
        }
    }
    fn close(open: *ProvenanceOpen, utc: i64, counters: *s.Counters) void {
        if (open.writer) |*w| {
            s.finish(w, counters, utc) catch unreachable;
            open.writer = null;
        }
    }
};
fn sameGood(a: st.Delta, b: st.Delta) bool {
    return a.low == b.low and a.high == b.high and a.kind == b.kind and a.presence == b.presence and a.generation == b.generation and a.observed == b.observed and a.duration == b.duration and a.quality == b.quality and a.capability_mask == b.capability_mask;
}
pub fn encodedSize(o: *st.Owner) Error!u64 {
    if (o.work.region != null) {
        o.detach();
        return error.Stale;
    }
    if (o.catalog_generation == 0) return error.Stale;
    var bytes: u64 = 72 + @as(u64, o.source_count) * 40 + @as(u64, o.rule_count) * 224 + @as(u64, o.metric_count) * 88;
    for (o.rules[0..o.rule_count]) |r| {
        const t = st.table(o, r.source) catch return error.Shared;
        const d = st.readGood(&t, r.row, &o.counters);
        bytes = std.math.add(u64, bytes, (try rawPayload(&t, d, o.config.max_value_bytes)).len) catch return error.Capacity;
    }
    return bytes;
}
fn disjoint(o: *const st.Owner, bytes: []const u8) bool {
    if (st.overlaps(@intFromPtr(bytes.ptr), bytes.len, @intFromPtr(o), @intCast(o.active_length))) return false;
    for (o.bindings) |origin| if (st.overlaps(@intFromPtr(bytes.ptr), bytes.len, @intFromPtr(origin.table.region.?), @intCast(origin.table.byte_length))) return false;
    for (o.results[0..o.result_count]) |result| if (st.overlaps(@intFromPtr(bytes.ptr), bytes.len, @intFromPtr(result.region.?), @intCast(result.byte_length))) return false;
    return true;
}
pub fn encode(o: *st.Owner, destination: []u8) Error!u64 {
    defer o.detach();
    const length = try encodedSize(o);
    if (length > destination.len) return error.Capacity;
    if (!disjoint(o, destination)) return error.Invalid;
    var w = Encoder{ .bytes = destination[0..@intCast(length)] };
    w.int(u64, magic);
    w.int(u32, version);
    w.int(u32, 64);
    w.int(u64, length);
    w.int(u64, o.catalog_generation);
    w.int(u64, o.round_generation);
    w.int(u64, o.token);
    w.int(u32, o.source_count);
    w.int(u32, o.rule_count);
    w.int(u32, o.metric_count);
    w.int(u32, 1);
    for (o.sources[0..o.source_count]) |src| {
        w.int(u64, src.id);
        w.int(u64, src.incarnation);
        w.int(u64, src.capability);
        w.int(u32, src.availability);
        w.int(u32, @intFromBool(src.suppressed));
        w.int(u32, src.access_region);
        w.int(u32, 0);
    }
    for (o.rules[0..o.rule_count]) |rule| {
        const t = st.table(o, rule.source) catch return error.Shared;
        const d = st.readGood(&t, rule.row, &o.counters);
        const payload = try rawPayload(&t, d, o.config.max_value_bytes);
        contractWrite(&w, o, rule);
        baselineWrite(&w, rule.baseline);
        w.int(u32, @intFromBool(rule.suppressed));
        w.int(u32, 0);
        w.int(u64, d.low);
        w.int(u64, d.high);
        w.int(u64, d.generation);
        w.int(i64, d.observed);
        w.int(u64, d.duration);
        w.int(u32, d.kind);
        w.int(u32, d.presence);
        w.int(u32, d.status);
        w.int(u32, d.reason);
        w.int(u64, d.capability_mask);
        w.int(u32, d.quality);
        w.int(u32, 0);
        w.int(i64, t.load(i64, 12, rule.row));
        w.int(u32, @intCast(payload.len));
        w.int(u32, 0);
        w.put(payload);
    }
    for (o.metrics[0..o.metric_count], 0..) |m, mi| {
        const location = st.resultLocation(o, @intCast(mi)) catch return error.Shared;
        const result = location.table;
        const row = location.row;
        w.int(u64, m.id);
        w.int(u64, m.identity);
        var slot: usize = 1;
        while (slot < 9) : (slot += 1) w.int(u64, if (slot == 4 or slot == 5) result.load(u32, slot, row) else if (slot == 6) @bitCast(result.load(i64, slot, row)) else result.load(u64, slot, row));
        w.int(u32, result.load(u32, 11, row));
        w.int(u32, @intFromBool(m.attempted));
    }
    std.debug.assert(w.at + 8 == length);
    w.int(u64, checksum(w.bytes[0..w.at]));
    // Validate our own image too: inconsistent references never leave as a
    // seemingly valid recovery point. Caller ignores bytes on failure.
    _ = try validate(o, w.bytes);
    return length;
}
// Private codec shape; does not depend on kernel catalog replacement scratch.
// Eight original result fields plus authoritative public slot11.
const ResultImage = [10]u64;
fn resultRead(r: *Reader, o: *const st.Owner, mi: u32) Error!ResultImage {
    if (try r.int(u64) != o.metrics[mi].id or try r.int(u64) != o.metrics[mi].identity) return error.Stale;
    var result: ResultImage = undefined;
    for (result[0..8]) |*v| v.* = try r.int(u64);
    result[8] = try r.int(u32);
    result[9] = try r.int(u32);
    if (result[9] > 1) return error.Invalid;
    // Slot4 is RAW diagnostic MetricStatus; slot11 is public status1..7.
    if ((result[3] != 1 and result[3] != 3 and result[3] != 4 and result[3] != 5) or result[8] < 1 or result[8] > 7 or result[4] > std.math.maxInt(u32) or @as(i64, @bitCast(result[5])) < 0 or result[6] == 0 or result[6] == std.math.maxInt(u64)) return error.Invalid;
    // No good reference still carries selected definition identity/generation.
    // The selected rule image must prove these guards below.
    if (result[0] == 0 or result[2] == 0 or result[7] == 0) return error.Invalid;
    if (result[8] == 2 and (result[1] == 0 or result[3] == 1)) return error.Invalid;
    if (result[1] != 0 and result[2] == 0) return error.Invalid;
    if (result[9] == 0 and (result[1] != 0 or result[5] != 0)) return error.Invalid;
    if (result[8] == 1 and (result[3] != 1 or result[0] == 0 or result[0] != result[1])) return error.Invalid;
    if (result[8] > 2) {
        if (result[1] != 0) return error.Invalid;
        const expected: u32 = switch (result[3]) {
            3 => 3,
            4 => 5,
            5 => 4,
            else => return error.Invalid,
        };
        if (result[8] < 6 and result[8] != expected) return error.Invalid;
        if (result[8] >= 6 and result[3] != 3 and result[3] != 5) return error.Invalid;
    }
    return result;
}
fn validate(o: *const st.Owner, image: []const u8) Error!Header {
    if (image.len < 72) return error.Truncated;
    const expected = std.mem.readInt(u64, image[image.len - 8 ..][0..8], .little);
    if (checksum(image[0 .. image.len - 8]) != expected) return error.Invalid;
    var r = Reader{ .bytes = image };
    const h = try header(&r);
    if (h.sources > o.config.source_capacity or h.rules > o.config.rule_capacity or h.metrics > o.config.metric_capacity) return error.Capacity;
    if (h.sources != o.source_count or h.rules != o.rule_count or h.metrics != o.metric_count or o.catalog_generation == 0) return error.Stale;
    if (h.round < o.round_generation) return error.Stale;
    // The catalog's unique IDs and references are already admitted. Matching
    // every ordered contract proves uniqueness and identity closure here.
    for (o.sources[0..o.source_count]) |src| {
        if (try r.int(u64) != src.id or try r.int(u64) != src.incarnation or try r.int(u64) != src.capability or try r.int(u32) != src.availability) return error.Stale;
        if (try r.int(u32) > 1) return error.Invalid;
        if (try r.int(u32) != src.access_region) return error.Stale;
        if (try r.int(u32) != 0) return error.Invalid;
    }
    const rules_at = r.at;
    // Per-source actual cold byte limits are checked before opening writers.
    for (o.sources[0..o.source_count], 0..) |_, si| {
        var scan = Reader{ .bytes = image, .at = rules_at };
        var payload: u64 = 0;
        const suppressed = std.mem.readInt(u32, image[64 + si * 40 + 28 ..][0..4], .little) == 1;
        const t = st.table(o, @intCast(si)) catch return error.Shared;
        try tableReady(&t, o.sources[si].rows);
        if (!provenance.sourceMatches(&t, o.sources[si]) or provenance.word(&t, 3) != o.sources[si].capability) return error.Stale;
        for (o.rules[0..o.rule_count]) |rule| {
            const candidate = try ruleRead(&scan, o, rule);
            if (rule.source == si) {
                payload += candidate.payload.len;
                if (suppressed and !candidate.value.suppressed) return error.Invalid;
            }
        }
        if (payload > t.specs[9].size) return error.Capacity;
    }
    for (o.rules[0..o.rule_count]) |rule| {
        const candidate = try ruleRead(&r, o, rule);
        const t = st.table(o, rule.source) catch return error.Shared;
        if (!provenance.ruleMatches(&t, rule.row, rule, o.metrics[rule.metric], o.sources[rule.source])) return error.Stale;
        var counters: s.Counters = .{};
        const current = st.readGood(&t, rule.row, &counters);
        if (candidate.value.generation < current.generation) return error.Stale;
        // Reusing an (identity,generation) pair for different good bytes would
        // defeat every public reference guard. Health/baseline may advance
        // without advancing the good-value generation.
        if (candidate.value.generation == current.generation and (!sameGood(candidate.value, current) or !std.mem.eql(u8, candidate.payload, try rawPayload(&t, current, o.config.max_value_bytes)))) return error.Stale;
        if (h.round == o.round_generation and (candidate.value.status != current.status or candidate.value.reason != current.reason or candidate.attempt != t.load(i64, 12, rule.row) or !std.meta.eql(candidate.value.baseline, rule.baseline) or candidate.value.suppressed != rule.suppressed)) return error.Stale;
    }
    var group: u32 = 0;
    while (group < o.result_count) : (group += 1) {
        const table = st.resultGroupTable(o, group) catch return error.Shared;
        try tableReady(&table, o.result_rows[group]);
        if (provenance.word(&table, 1) != o.owner_epoch) return error.Stale;
    }
    for (o.metrics[0..o.metric_count], 0..) |_, mi| {
        const location = st.resultLocation(o, @intCast(mi)) catch return error.Shared;
        const current_results = location.table;
        const row = location.row;
        if (!provenance.metricMatches(&current_results, row, o.metrics[mi])) return error.Stale;
        const result = try resultRead(&r, o, @intCast(mi));
        if (o.metrics[mi].attempted and result[9] == 0) return error.Stale;
        const current_generation = current_results.load(u64, 7, row);
        if (result[6] < current_generation) return error.Stale;
        if (h.round == o.round_generation or result[6] == current_generation) {
            for (result[0..8], 1..) |value, slot| {
                const current: u64 = if (slot == 4 or slot == 5) current_results.load(u32, slot, row) else if (slot == 6) @bitCast(current_results.load(i64, slot, row)) else current_results.load(u64, slot, row);
                if (value != current) return error.Stale;
            }
            if (result[8] != current_results.load(u32, 11, row)) return error.Stale;
            if ((result[9] == 1) != o.metrics[mi].attempted) return error.Stale;
        }
        var selected_found = result[0] == 0;
        var reference_found = false;
        var winner: ?st.Rule = null;
        var health: ?st.Rule = null;
        var diagnostic: u32 = 3;
        var scan = Reader{ .bytes = image, .at = rules_at };
        for (o.rules[0..o.rule_count]) |rule| {
            const candidate = try ruleRead(&scan, o, rule);
            if (rule.metric != mi) continue;
            if (health == null or rule.priority < health.?.priority or (rule.priority == health.?.priority and rule.id < health.?.id)) {
                health = rule;
                diagnostic = st.metricStatus(candidate.value.status);
            }
            if (candidate.value.status == 1 and candidate.value.presence != 0 and (winner == null or rule.priority < winner.?.priority or (rule.priority == winner.?.priority and rule.id < winner.?.id))) winner = rule;
            if (rule.id == result[0]) selected_found = true;
            const reference_id = if (result[1] != 0) result[1] else result[0];
            if (rule.id == reference_id) {
                if (rule.identity != result[7] or candidate.value.generation != result[2]) return error.Invalid;
                if (result[1] != 0 and candidate.value.presence == 0) return error.Invalid;
                if (result[8] == 1 and candidate.value.status != 1) return error.Invalid;
                reference_found = true;
            }
        }
        if (!selected_found or !reference_found) return error.Invalid;
        if (winner) |good| {
            if (result[8] != 1 or result[0] != good.id) return error.Invalid;
        } else if (result[8] == 1 or result[3] != diagnostic or result[0] != (if (health) |selected| selected.id else @as(u64, 0))) return error.Invalid;
    }
    if (r.at != image.len - 8) return error.Invalid;
    return h;
}
pub fn restoreWorkSize(o: *const st.Owner, image_length: u64) u64 {
    const aligned = std.math.add(u64, image_length, 7) catch return std.math.maxInt(u64);
    return std.math.add(u64, aligned & ~@as(u64, 7), (@as(u64, o.source_count) + o.result_count) * (@sizeOf(s.Open) + @sizeOf(ProvenanceOpen)) + @as(u64, o.source_count) * @sizeOf(u32) + 24) catch std.math.maxInt(u64);
}
pub fn prepare(o: *st.Owner, image: []const u8, work: []align(8) u8, out: *Prepared) Error!void {
    o.detach();
    defer o.detach();
    out.* = .{};
    if (!disjoint(o, image) or !disjoint(o, work) or st.overlaps(@intFromPtr(image.ptr), image.len, @intFromPtr(work.ptr), work.len)) return error.Invalid;
    _ = try validate(o, image);
    if (work.len < restoreWorkSize(o, image.len)) return error.Workspace;
    @memcpy(work[0..image.len], image);
    out.* = .{ .work = work.ptr, .work_length = work.len, .image_length = image.len, .catalog_generation = o.catalog_generation, .round_generation = o.round_generation, .token = o.token, .checksum = checksum(image) };
}
/// Must run under the parent's publication permission. Prepared contains only
/// caller-owned memory, never a retained native borrow or a callback.
pub fn publish(o: *st.Owner, prepared: *Prepared, utc: i64) Error!void {
    o.detach();
    defer o.detach();
    const q = prepared.*;
    prepared.* = .{};
    const pointer = q.work orelse return error.Invalid;
    if (utc < 0 or @intFromPtr(pointer) % 8 != 0 or q.work_length > std.math.maxInt(usize) or q.image_length > q.work_length or q.work_length < restoreWorkSize(o, q.image_length)) return error.Invalid;
    const work: []align(8) u8 = @alignCast(pointer[0..@intCast(q.work_length)]);
    if (!disjoint(o, work)) return error.Invalid;
    if (q.catalog_generation != o.catalog_generation or q.round_generation != o.round_generation or q.token != o.token) return error.Stale;
    const image = work[0..@intCast(q.image_length)];
    if (checksum(image) != q.checksum) return error.Invalid;
    const h = try validate(o, image);
    // Preserve non-reused plan tokens and monotonic owner revision on recovery.
    const operation = @max(o.token, h.token);
    if (o.round_generation == std.math.maxInt(u64) or operation == std.math.maxInt(u64) or @as(u32, @truncate(operation)) == std.math.maxInt(u32)) return error.GenerationCapacity;
    const next_token = operation + 1;
    var fba = std.heap.FixedBufferAllocator.init(work[std.mem.alignForward(usize, image.len, 8)..]);
    const opens = fba.allocator().alloc(s.Open, @as(usize, o.source_count) + o.result_count) catch return error.Workspace;
    const proofs = fba.allocator().alloc(ProvenanceOpen, @as(usize, o.source_count) + o.result_count) catch return error.Workspace;
    const offsets = fba.allocator().alloc(u32, o.source_count) catch return error.Workspace;
    @memset(offsets, 0);
    var opened: usize = 0;
    defer for (opens[0..opened]) |*open| open.closeUnchanged();
    for (o.sources[0..o.source_count], 0..) |_, si| {
        const t = st.table(o, @intCast(si)) catch return error.Shared;
        opens[si] = s.Open.begin(t, false, true, true) catch return error.Shared;
        opened += 1;
    }
    var gi: u32 = 0;
    while (gi < o.result_count) : (gi += 1) {
        const rt = st.resultGroupTable(o, gi) catch return error.Shared;
        opens[o.source_count + gi] = s.Open.begin(rt, false, true, false) catch return error.Shared;
        opened += 1;
    }
    var proof_count: usize = 0;
    defer for (proofs[0..proof_count]) |*proof| proof.unchanged();
    for (opens[0..opened], 0..) |*open, i| {
        proofs[i] = try ProvenanceOpen.begin(&open.table);
        proof_count += 1;
    }
    var sources = Reader{ .bytes = image, .at = 64 };
    for (o.sources[0..o.source_count]) |*src| {
        _ = sources.take(28) catch unreachable;
        src.suppressed = (sources.int(u32) catch unreachable) == 1;
        _ = sources.take(8) catch unreachable;
    }
    var r = Reader{ .bytes = image, .at = 64 + @as(usize, o.source_count) * 40 };
    for (o.rules[0..o.rule_count]) |*rule| {
        // Validation above exhausted every possible parse/type/capacity error.
        const candidate = ruleRead(&r, o, rule.*) catch unreachable;
        var d = candidate.value;
        const open = &opens[rule.source];
        const off = offsets[rule.source];
        if (candidate.payload.len != 0) {
            open.values.?.store(u8, open.table.binding.positions[9], off, candidate.payload) catch unreachable;
            o.counters.written +|= candidate.payload.len;
            o.counters.touched +|= candidate.payload.len;
            o.counters.payload +|= candidate.payload.len;
        }
        d.payload_ref = @as(u64, off) | (@as(u64, candidate.payload.len) << 32);
        offsets[rule.source] += @intCast(candidate.payload.len);
        st.writeGood(&open.table, &open.values.?, rule.row, d, &o.counters);
        s.store(&open.table, &open.health.?, &o.counters, u32, 10, rule.row, d.status);
        s.store(&open.table, &open.health.?, &o.counters, u32, 11, rule.row, d.reason);
        s.store(&open.table, &open.health.?, &o.counters, i64, 12, rule.row, candidate.attempt);
        // Legacy RuleStateOutput restores the good value and health, but has
        // no generic counter-pair baseline. The next sample must rebuild that
        // interval even when the checkpoint contains a valid encoded baseline.
        // CPU continuity is a separate source contract and remains restorable.
        rule.baseline = if (old.isCounterMetric(rule.kind)) .{} else d.baseline;
        rule.suppressed = d.suppressed;
    }
    for (opens[0..o.source_count], 0..) |*open, si| {
        s.length(&open.table, &open.values.?, &o.counters, 9, offsets[si]);
        provenance.header(&open.table, &proofs[si].writer.?, &o.counters, o.sources[si], o.sources[si].rows);
        open.close(&o.counters, utc) catch unreachable;
        proofs[si].close(utc, &o.counters);
    }
    payload_index.rebuild(o);
    for (o.metrics[0..o.metric_count], 0..) |_, mi| {
        const metric = o.metrics[mi];
        const result = &opens[o.source_count + metric.result_group];
        const proof = &proofs[o.source_count + metric.result_group];
        const result_row = metric.result_row;
        const values = resultRead(&r, o, @intCast(mi)) catch unreachable;
        for (values[0..8], 1..) |value, slot| {
            if (slot == 4 or slot == 5) s.store(&result.table, &result.values.?, &o.counters, u32, slot, result_row, @intCast(value)) else if (slot == 6) s.store(&result.table, &result.values.?, &o.counters, i64, slot, result_row, @bitCast(value)) else s.store(&result.table, &result.values.?, &o.counters, u64, slot, result_row, value);
        }
        // New data-area source/local-row references are a deterministic index
        // of the validated retained ID/identity in this exact target catalog.
        var source: u32 = p.none;
        var row: u32 = p.none;
        const reference_id = if (values[1] != 0) values[1] else values[0];
        for (o.rules[0..o.rule_count]) |rule| if (rule.metric == mi and rule.id == reference_id and rule.identity == values[7]) {
            source = o.sources[rule.source].binding_ordinal;
            row = rule.row;
            break;
        };
        s.store(&result.table, &result.values.?, &o.counters, u32, 9, result_row, source);
        s.store(&result.table, &result.values.?, &o.counters, u32, 10, result_row, row);
        s.store(&result.table, &result.values.?, &o.counters, u32, 11, result_row, @intCast(values[8]));
        o.metrics[mi].attempted = values[9] == 1;
        provenance.metric(&result.table, &proof.writer.?, &o.counters, result_row, o.metrics[mi]);
    }
    gi = 0;
    while (gi < o.result_count) : (gi += 1) {
        const result = &opens[o.source_count + gi];
        const proof = &proofs[o.source_count + gi];
        provenance.header(&result.table, &proof.writer.?, &o.counters, null, o.result_rows[gi]);
        provenance.ownerEpoch(&result.table, &proof.writer.?, &o.counters, next_token >> 32);
        result.close(&o.counters, utc) catch unreachable;
        proof.close(utc, &o.counters);
    }
    // Round generation names the recovered committed state. Advance the local
    // operation token instead: replaying a copied Prepared must fail, while a
    // subsequent newer checkpoint must not be rejected as an equal round just
    // because this owner previously restored one (legacy operation/state split).
    o.round_generation = h.round;
    o.token = next_token;
    o.owner_epoch = next_token >> 32;
    o.last_utc_ms = utc;
}
comptime {
    std.debug.assert(@sizeOf(Prepared) == 56);
}
