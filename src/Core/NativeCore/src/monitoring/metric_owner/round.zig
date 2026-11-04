//! Owner operation wrapper: exclusive entry, borrowed work, SharedSection
//! admission/publication and detach. Interval arithmetic lives in counter_core.
const std = @import("std");
const p = @import("protocol.zig");
const st = @import("state.zig");
const s = @import("storage.zig");
const old = @import("../metric_snapshot/protocol.zig");
const payload_index = @import("payload_index.zig");
const provenance = @import("provenance.zig");
const counter_core = @import("counter_core.zig");

pub fn plan(o: *st.Owner, requests: []const u32, bytes: []align(8) u8, utc: i64, out: *p.Round) p.Code {
    o.detach();
    out.* = .{};
    if (o.catalog_generation == 0 or utc < 0) return o.fail(.invalid_contract, .none, 0, 0);
    if (bytes.len > o.config.work_limit) return o.fail(.capacity_exceeded, .work, bytes.len, o.config.work_limit);
    if (!st.validWork(o, bytes)) return o.fail(.workspace_unavailable, .work, o.sizes.work, bytes.len);
    if (o.token == std.math.maxInt(u64) or @as(u32, @truncate(o.token)) == std.math.maxInt(u32)) return o.fail(.capacity_exceeded, .generation, 0, o.token);
    var w = st.allocateRound(o, bytes) catch return o.fail(.workspace_unavailable, .work, o.sizes.work, bytes.len);
    @memmove(w.requested[0..requests.len], requests);
    w.request_count = @intCast(requests.len);
    std.mem.sort(u32, w.requested[0..requests.len], {}, std.sort.asc(u32));
    for (w.requested[0..requests.len], 0..) |m, i| {
        if (m >= o.metric_count or (i != 0 and w.requested[i - 1] == m)) return o.fail(.invalid_contract, .metrics, m, o.metric_count);
    }
    // Native provider facts may change availability between cold catalogs.
    // Preserve old planner semantics: due-only omissions do not reset an
    // interval, but an explicit unavailable transition resets that source's
    // generic counter baselines. CPU acquisition intervals remain independent.
    for (o.sources[0..o.source_count]) |*src| {
        if (src.availability != 1 and src.planned_availability == 1) {
            var ri = src.first_rule;
            var visited: u32 = 0;
            while (ri != p.none and visited < src.rows) : (visited += 1) {
                if (old.isCounterMetric(o.rules[ri].kind)) o.rules[ri].baseline = .{};
                ri = o.rules[ri].next_source;
            }
        }
        src.planned_availability = src.availability;
    }
    w.utc = utc;
    o.token += 1;
    w.raw.token = o.token;
    w.raw.catalog_generation = o.catalog_generation;
    for (w.requested[0..requests.len]) |metric| {
        var selected: ?u32 = null;
        var cursor = o.metrics[metric].first_rule;
        var visited: u32 = 0;
        while (cursor != p.none and visited < o.rule_count) : (visited += 1) {
            const rule = o.rules[cursor];
            if (!rule.suppressed and !o.sources[rule.source].suppressed and o.sources[rule.source].availability == 1 and (selected == null or better(rule, o.rules[selected.?]))) selected = cursor;
            cursor = rule.next_metric;
        }
        const ri = selected orelse continue;
        const r = o.rules[ri];
        const i = w.raw.count;
        w.raw.count += 1;
        w.synthetic[i] = false;
        w.expected[i] = @intCast(ri);
        const raw = &w.raw.observations;
        raw.rule_indices.?[i] = @intCast(ri);
        raw.status.?[i] = 2;
        raw.kinds.?[i] = r.value_kind;
        raw.presence.?[i] = 0;
        raw.reasons.?[i] = 0;
        raw.flags.?[i] = 0;
        raw.low.?[i] = 0;
        raw.high.?[i] = 0;
        raw.reference.?[i] = 0;
        raw.monotonic_ns.?[i] = 0;
        raw.tick_frequency.?[i] = 0;
        raw.duration_ms.?[i] = 0;
        raw.observed_ms.?[i] = utc;
        raw.payload_offsets.?[i] = 0;
        raw.payload_lengths.?[i] = 0;
        raw.capability_masks.?[i] = r.capability_mask;
        raw.quality.?[i] = 0;
        const q = w.raw.queries;
        const src = o.sources[r.source];
        @constCast(q.rule_ids.?)[i] = r.id;
        @constCast(q.source_ids.?)[i] = src.id;
        @constCast(q.metric_ids.?)[i] = o.metrics[r.metric].id;
        @constCast(q.identities.?)[i] = r.identity;
        @constCast(q.incarnations.?)[i] = src.incarnation;
        @constCast(q.capabilities.?)[i] = src.capability;
        @constCast(q.capability_masks.?)[i] = r.capability_mask;
        @constCast(q.value_kinds.?)[i] = r.value_kind;
        @constCast(q.metric_kinds.?)[i] = r.kind;
    }
    o.work = w;
    o.work.receipt.?.* = .{ .token = o.token, .catalog_generation = o.catalog_generation, .query_count = w.raw.count, .queries = w.raw.queries, .metric_indices = w.affected.ptr, .metric_ids = w.affected_ids.ptr, .invalidated_aliases = w.invalidated_aliases.ptr };
    out.* = w.raw;
    return .ok;
}

fn obsIndex(o: *const st.Owner, rule: u32) ?u32 {
    var i: u32 = 0;
    while (i < o.work.raw.count) : (i += 1) {
        if (o.work.expected[i] == rule) return i;
    }
    return null;
}
fn candidate(o: *st.Owner, rule: u32, observation: ?u32) st.Delta {
    const r = o.rules[rule];
    if (o.work.invalid_sources[r.source]) return .{ .kind = r.value_kind, .invalidate = true, .write_value = true };
    if (o.work.unsupported_sources[r.source]) {
        var d = st.readGood(&o.work.tables[r.source], r.row, &o.counters);
        d.status = 3;
        d.reason = o.work.unsupported_reasons[r.source];
        d.suppressed = true;
        d.baseline = if (r.baseline.schema == 16) r.baseline else .{};
        return d;
    }
    if (observation) |i| return o.work.deltas[i];
    return st.readGood(&o.work.tables[r.source], r.row, &o.counters);
}

pub fn prepare(o: *st.Owner, token: u64) p.Code {
    if (o.work.region == null or token != o.token or o.work.prepared) {
        o.detach();
        return o.fail(.stale_plan, .none, token, o.token);
    }
    var success = false;
    defer if (!success) o.detach();
    const w = &o.work;
    const raw = &w.raw.observations;
    w.affected_count = 0;
    for (o.sources[0..o.source_count], 0..) |src, i| {
        w.tables[i] = st.table(o, @intCast(i)) catch return o.fail(.shared_section, .binding, i, 0);
        w.invalid_sources[i] = false;
        w.unsupported_sources[i] = false;
        w.unsupported_reasons[i] = 0;
        _ = src;
    }
    for (o.results[0..o.result_count], 0..) |_, gi| w.tables[o.source_count + gi] = st.resultGroupTable(o, @intCast(gi)) catch return o.fail(.shared_section, .binding, gi, 0);
    if (o.round_generation == std.math.maxInt(u64)) return o.fail(.capacity_exceeded, .generation, 0, o.round_generation);
    // Suppressed sources still complete a requested attempt, without inventing
    // a query receipt. This also supplies history's nullable failed sample.
    for (w.requested[0..w.request_count]) |metric| markAffected(o, metric);
    var i: u32 = 0;
    while (i < w.raw.count) : (i += 1) {
        const ri = w.expected[i];
        const r = o.rules[ri];
        markAffected(o, r.metric);
        const status = raw.status.?[i];
        const flags = raw.flags.?[i];
        const q = w.raw.queries;
        const src = o.sources[r.source];
        if (q.rule_ids.?[i] != r.id or q.source_ids.?[i] != src.id or q.metric_ids.?[i] != o.metrics[r.metric].id or q.identities.?[i] != r.identity or q.incarnations.?[i] != src.incarnation or q.capabilities.?[i] != src.capability or q.capability_masks.?[i] != r.capability_mask or q.value_kinds.?[i] != r.value_kind or q.metric_kinds.?[i] != r.kind) return o.fail(.stale_identity, .rules, ri, 0);
        if (raw.rule_indices.?[i] != ri or status < 1 or status > 4 or flags & ~@as(u32, 63) != 0) return o.fail(.invalid_contract, .rules, ri, 0);
        if (flags & 32 != 0 and (status != 3 or flags & 8 != 0)) return o.fail(.invalid_contract, .rules, ri, 0);
        if (flags & 32 != 0) {
            w.unsupported_sources[r.source] = true;
            w.unsupported_reasons[r.source] = raw.reasons.?[i];
        }
        if (flags & 16 != 0 and (r.kind != 1 or r.value_kind != 1 or flags & 7 != 0)) return o.fail(.invalid_contract, .rules, ri, 0);
        if (flags & 7 != 0 and !old.isCounterMetric(r.kind)) return o.fail(.invalid_contract, .rules, ri, 0);
        if (flags & 7 != 0) {
            const expected_flags: u32 = if (old.isFloatingCounterMetric(r.kind)) 4 else if (old.isTimedCounterMetric(r.kind)) 2 else 1;
            if (flags & 7 != expected_flags) return o.fail(.invalid_contract, .rules, ri, 0);
        }
        if (raw.observed_ms.?[i] < 0) return o.fail(.invalid_contract, .rules, ri, 0);
        // Preserve wall-clock rollback: attempt and observation need not increase.
        if (raw.observed_ms.?[i] > w.utc and @as(u64, @intCast(raw.observed_ms.?[i] - w.utc)) > o.config.maximum_future_skew_ms) return o.fail(.invalid_contract, .rules, ri, 0);
        if (raw.capability_masks.?[i] & ~r.capability_mask != 0) return o.fail(.invalid_contract, .rules, ri, 0);
        if (flags & 8 != 0) w.invalid_sources[r.source] = true;
        var d = st.readGood(&w.tables[r.source], r.row, &o.counters);
        d.baseline = if (status == 1 or r.baseline.schema == 16) r.baseline else .{};
        d.suppressed = status == 3;
        d.status = status;
        d.reason = raw.reasons.?[i];
        d.write_value = false;
        if (status == 1) {
            const kind = raw.kinds.?[i];
            const presence = raw.presence.?[i];
            const len = raw.payload_lengths.?[i];
            const off = raw.payload_offsets.?[i];
            if (len > o.config.max_value_bytes) return o.fail(.capacity_exceeded, .payload, len, o.config.max_value_bytes);
            if (kind != r.value_kind or presence < 1 or presence > 2 or off > w.payload.len or len > w.payload.len - off) return o.fail(.invalid_contract, .payload, len, o.config.max_value_bytes);
            if ((kind != 5 or presence != 1) and len != 0) return o.fail(.invalid_contract, .payload, len, 0);
            if (kind == 5 and presence == 1 and (len < r.minimum or len > r.maximum)) return o.fail(.invalid_contract, .payload, len, r.maximum);
            const low = raw.low.?[i];
            const high = raw.high.?[i];
            if (kind == 5 and (low != 0 or high != 0)) return o.fail(.invalid_contract, .rules, ri, 0);
            if ((kind != 4 and kind != 5 and high != 0 and flags & 23 == 0) or (kind == 6 and low > 1)) return o.fail(.invalid_contract, .rules, ri, 0);
            if (kind == 1 and presence == 1 and flags & 23 == 0 and !std.math.isFinite(@as(f64, @bitCast(low)))) return o.fail(.invalid_contract, .rules, ri, 0);
            d.low = if (presence == 2) 0 else low;
            d.high = if (kind == 4 and presence == 1) high else 0;
            d.kind = kind;
            d.presence = presence;
            d.observed = raw.observed_ms.?[i];
            d.duration = raw.duration_ms.?[i];
            d.payload_ref = 0;
            d.capability_mask = raw.capability_masks.?[i];
            d.quality = raw.quality.?[i];
            d.write_value = true;
            if (flags & 16 != 0) {
                if (r.kind != 1 or kind != 1 or presence != 1 or flags & 7 != 0 or raw.tick_frequency.?[i] == 0) return o.fail(.invalid_contract, .rules, ri, 0);
                if (high < low or raw.monotonic_ns.?[i] == 0) return o.fail(.invalid_contract, .rules, ri, 0);
                if (!cpuCounter(r, raw.*, i, &d) or !valid(r, d)) {
                    d = st.readGood(&w.tables[r.source], r.row, &o.counters);
                    d.baseline = baseline(raw.*, i);
                    d.status = 4;
                    d.reason = raw.reasons.?[i];
                    d.write_value = false;
                }
            } else if (old.isCounterMetric(r.kind)) {
                const expected_flags: u32 = if (old.isFloatingCounterMetric(r.kind)) 4 else if (old.isTimedCounterMetric(r.kind)) 2 else 1;
                if (presence != 1 or flags & 7 != expected_flags) return o.fail(.invalid_contract, .rules, ri, 0);
                if (old.isFloatingCounterMetric(r.kind)) {
                    const primary: f64 = @bitCast(low);
                    const reference: f64 = @bitCast(high);
                    if (!std.math.isFinite(primary) or !std.math.isFinite(reference) or primary < 0 or reference < 0) return o.fail(.invalid_contract, .rules, ri, 0);
                }
                if (old.isTimedCounterMetric(r.kind) and raw.tick_frequency.?[i] == 0) return o.fail(.invalid_contract, .rules, ri, 0);
                if (r.kind == 19 and (raw.reference.?[i] == 0 or raw.reference.?[i] > std.math.maxInt(u32))) return o.fail(.invalid_contract, .rules, ri, 0);
                if (!counter(r, raw.*, i, &d) or !valid(r, d)) {
                    d = st.readGood(&w.tables[r.source], r.row, &o.counters);
                    d.baseline = baseline(raw.*, i);
                    d.status = 2;
                    d.reason = raw.reasons.?[i];
                    d.write_value = false;
                }
            } else if (flags & 7 != 0) return o.fail(.invalid_contract, .rules, ri, 0) else d.baseline = .{};
            if (d.write_value and d.presence == 1 and d.kind != 5 and !valid(r, d)) return o.fail(.invalid_contract, .rules, ri, 0);
            if (d.write_value) {
                const prior = w.tables[r.source].load(u64, 5, r.row);
                if (prior == std.math.maxInt(u64)) return o.fail(.capacity_exceeded, .generation, 0, prior);
                d.generation = prior + 1;
                if (kind == 5 and presence == 1 and len > 0) {
                    const destination = reserve(o, r.source, len, i) orelse return o.fail(.payload_capacity, .payload, len, w.tables[r.source].specs[9].size);
                    d.payload_ref = @as(u64, destination) | (@as(u64, len) << 32);
                }
            }
        }
        w.deltas[i] = d;
    }
    const derived_code = @import("vector_inputs.zig").derive(o);
    if (derived_code != .ok) return derived_code;
    // Invalidation is source-wide, including rules/aliases outside the request.
    for (o.sources[0..o.source_count], 0..) |src, si| if (w.invalid_sources[si] or w.unsupported_sources[si]) {
        var ri = src.first_rule;
        var visited: u32 = 0;
        while (ri != p.none and visited < src.rows) : (visited += 1) {
            const r = o.rules[ri];
            markAffected(o, r.metric);
            if (w.invalid_sources[si] and w.tables[si].load(u64, 5, r.row) == std.math.maxInt(u64)) return o.fail(.capacity_exceeded, .generation, 0, std.math.maxInt(u64));
            ri = r.next_source;
        }
    };
    for (w.affected[0..w.affected_count]) |mi| {
        const m = o.metrics[mi];
        if (w.tables[o.source_count + m.result_group].load(u64, 7, m.result_row) == std.math.maxInt(u64)) return o.fail(.capacity_exceeded, .generation, 0, std.math.maxInt(u64));
    }
    w.prepared = true;
    w.receipt.?.affected_count = w.affected_count;
    for (w.affected[0..w.affected_count], 0..) |mi, ai| {
        w.affected_ids[ai] = o.metrics[mi].id;
        if (!wasRequested(w, mi) and invalidatedMetric(o, mi)) {
            w.invalidated_aliases[w.invalidated_alias_count] = mi;
            w.invalidated_alias_count += 1;
        }
    }
    w.receipt.?.invalidated_alias_count = w.invalidated_alias_count;
    // Adapter query views expire at prepare. Compact only one-shot query
    // metadata after every immutable identity has been validated; deltas and
    // original observation indices remain unchanged for publication.
    var queried: u32 = 0;
    var oi: u32 = 0;
    while (oi < w.raw.count) : (oi += 1) if (raw.status.?[oi] != 4 and !w.synthetic[oi]) {
        if (queried != oi) {
            inline for (.{ "rule_ids", "source_ids", "metric_ids", "identities", "incarnations", "capabilities", "capability_masks", "value_kinds", "metric_kinds" }) |field| {
                @constCast(@field(w.raw.queries, field).?)[queried] = @field(w.raw.queries, field).?[oi];
            }
        }
        queried += 1;
    };
    w.receipt.?.query_count = queried;
    success = true;
    return .ok;
}

fn baseline(raw: p.Observations, i: u32) st.Baseline {
    return .{ .valid = true, .schema = raw.flags.?[i] & 23, .low = raw.low.?[i], .high = raw.high.?[i], .frequency = raw.tick_frequency.?[i], .reference = raw.reference.?[i], .time = raw.observed_ms.?[i], .monotonic = raw.monotonic_ns.?[i] };
}
fn cpuCounter(r: st.Rule, raw: p.Observations, i: u32, d: *st.Delta) bool {
    const next = baseline(raw, i);
    d.baseline = next;
    const value = counter_core.cpuCounter(r.baseline, next) orelse return false;
    d.kind = 1;
    d.low = value.low;
    d.high = 0;
    d.duration = value.duration;
    return true;
}
fn counter(r: st.Rule, raw: p.Observations, i: u32, d: *st.Delta) bool {
    const next = baseline(raw, i);
    d.baseline = next;
    const value = counter_core.counter(r.kind, r.baseline, next) orelse return false;
    d.kind = 1;
    d.low = value.low;
    d.high = 0;
    d.duration = value.duration;
    return true;
}
fn valid(r: st.Rule, d: st.Delta) bool {
    if (d.kind == 4 or d.kind == 5) return true;
    return old.validValue(d.kind, r.flags, r.minimum, r.maximum, d.low);
}

/// First-fit among current live payload extents plus earlier reservations. No
/// retained free-list or compaction; old extents become reusable after commit.
fn reserve(o: *st.Owner, source: u32, length: u32, before: u32) ?u32 {
    const cap = o.work.tables[source].specs[9].size;
    const own = o.rules[o.work.expected[before]];
    const old_ref = o.work.tables[source].load(u64, 8, own.row);
    if (old_ref >> 32 >= length and old_ref >> 32 != 0 and @as(u64, @as(u32, @truncate(old_ref))) + (old_ref >> 32) <= cap) return @truncate(old_ref);
    var tail: u64 = payload_index.end(o, &o.work.tables[source], source);
    var earlier: u32 = 0;
    while (earlier < before) : (earlier += 1) if (o.rules[o.work.expected[earlier]].source == source) {
        const ref = o.work.deltas[earlier].payload_ref;
        tail = @max(tail, @as(u64, @as(u32, @truncate(ref))) + (ref >> 32));
    };
    if (tail + length <= cap) return @intCast(tail);
    var start: u64 = 0;
    var iterations: usize = 0;
    while (iterations <= @as(usize, o.rule_count) + before + 1) : (iterations += 1) {
        if (start + length > cap) return null;
        var end = start;
        var ri = o.sources[source].first_rule;
        var visited: u32 = 0;
        while (ri != p.none and visited < o.sources[source].rows) : (visited += 1) {
            const r = o.rules[ri];
            ri = r.next_source;
            const ref = o.work.tables[source].load(u64, 8, r.row);
            o.counters.read +|= 8;
            const off: u64 = @as(u32, @truncate(ref));
            const len = ref >> 32;
            if (len != 0 and off < start + length and start < off + len) end = @max(end, off + len);
        }
        var j: u32 = 0;
        while (j < before) : (j += 1) {
            if (o.rules[o.work.expected[j]].source != source) continue;
            const ref = o.work.deltas[j].payload_ref;
            const off: u64 = @as(u32, @truncate(ref));
            const len = ref >> 32;
            if (len != 0 and off < start + length and start < off + len) end = @max(end, off + len);
        }
        if (end == start) return @intCast(start);
        start = end;
    }
    return null;
}
fn markAffected(o: *st.Owner, metric: u32) void {
    const w = &o.work;
    if (o.metrics[metric].affected_token == o.token) return;
    o.metrics[metric].affected_token = o.token;
    w.affected[w.affected_count] = metric;
    w.affected_count += 1;
}
fn invalidatedMetric(o: *const st.Owner, metric: u32) bool {
    var ri = o.metrics[metric].first_rule;
    var visited: u32 = 0;
    while (ri != p.none and visited < o.rule_count) : (visited += 1) {
        if (o.work.invalid_sources[o.rules[ri].source]) return true;
        ri = o.rules[ri].next_metric;
    }
    return false;
}
fn wasRequested(w: *const st.Work, metric: u32) bool {
    var lo: usize = 0;
    var hi: usize = w.request_count;
    while (lo < hi) {
        const mid = lo + (hi - lo) / 2;
        const item = w.requested[mid];
        if (item == metric) return true;
        if (item < metric) lo = mid + 1 else hi = mid;
    }
    return false;
}

pub fn commit(o: *st.Owner, token: u64, utc: i64) p.Code {
    return commitWithReceipt(o, token, utc, null);
}
pub fn commitWithReceipt(o: *st.Owner, token: u64, utc: i64, out: ?*p.Receipt) p.Code {
    if (out) |target| target.* = .{};
    defer o.detach();
    if (o.work.region == null or !o.work.prepared or token != o.token) return o.fail(.stale_plan, .none, token, o.token);
    if (utc < 0) return o.fail(.invalid_contract, .none, 0, 0);
    const w = &o.work;
    // Revalidate mappings immediately before admission. The region creator must
    // serialize mapping teardown and schema changes with the borrowed scope.
    for (o.sources[0..o.source_count], 0..) |src, si| {
        w.tables[si] = st.table(o, @intCast(si)) catch return o.fail(.shared_section, .binding, si, 0);
        _ = src;
    }
    for (o.results[0..o.result_count], 0..) |_, gi| w.tables[o.source_count + gi] = st.resultGroupTable(o, @intCast(gi)) catch return o.fail(.shared_section, .binding, gi, 0);
    var vector_writers = @import("vector_inputs.zig").begin(o) catch return o.fail(.shared_section, .binding, 0, 0);
    defer vector_writers.rollback();
    var opened: usize = 0;
    defer {
        for (w.opens[0..opened]) |*open| open.closeUnchanged();
    }
    for (o.sources[0..o.source_count], 0..) |_, si| {
        var health = false;
        var values = w.invalid_sources[si];
        var i: u32 = 0;
        while (i < w.raw.count) : (i += 1) if (o.rules[w.expected[i]].source == si) {
            health = true;
            values = values or w.deltas[i].write_value;
        };
        if (w.unsupported_sources[si] and !w.invalid_sources[si]) values = false;
        health = health or w.invalid_sources[si] or w.unsupported_sources[si];
        w.opens[si] = if (w.unsupported_sources[si])
            s.Open.beginCatalog(w.tables[si], false, values, health) catch return o.fail(.shared_section, .binding, si, 0)
        else
            s.Open.begin(w.tables[si], false, values, health) catch return o.fail(.shared_section, .binding, si, 0);
        opened += 1;
    }
    for (o.results[0..o.result_count], 0..) |_, gi| {
        var changed = false;
        var first_attempt = false;
        for (w.affected[0..w.affected_count]) |mi| if (o.metrics[mi].result_group == gi) {
            changed = true;
        };
        // Every affected result is published with this round's attempt UTC,
        // including acquired dependencies and source-wide aliases outside the
        // requested set. Admit their first-attempt provenance before stores.
        for (w.affected[0..w.affected_count]) |mi| if (o.metrics[mi].result_group == gi and !o.metrics[mi].attempted) {
            first_attempt = true;
        };
        w.opens[o.source_count + gi] = if (first_attempt)
            s.Open.beginCatalog(w.tables[o.source_count + gi], false, changed, false) catch return o.fail(.shared_section, .binding, gi, 0)
        else
            s.Open.begin(w.tables[o.source_count + gi], false, changed, false) catch return o.fail(.shared_section, .binding, gi, 0);
        opened += 1;
    }
    @import("vector_inputs.zig").admitRows(o, &vector_writers) catch return o.fail(.shared_section, .binding, 0, 0);
    for (w.expected[0..w.raw.count], 0..) |ri, oi| applyRule(o, ri, @intCast(oi), utc);
    for (o.sources[0..o.source_count], 0..) |src, si| if (w.invalid_sources[si] or w.unsupported_sources[si]) {
        var ri = src.first_rule;
        var visited: u32 = 0;
        while (ri != p.none and visited < src.rows) : (visited += 1) {
            const next = o.rules[ri].next_source;
            if (obsIndex(o, ri) == null) applyRule(o, ri, null, utc);
            ri = next;
        }
    };
    // Rule value/health sequences close first; result references publish last.
    for (o.sources[0..o.source_count], 0..) |*src, si| {
        if (w.invalid_sources[si]) src.suppressed = false else if (w.unsupported_sources[si]) {
            src.suppressed = true;
            provenance.suppressSource(&w.opens[si].table, &w.opens[si].provenance.?, &o.counters);
        }
    }
    for (w.opens[0..o.source_count], 0..) |*open, si| {
        if (open.values) |*v| {
            const entry = open.table.layout.position(open.table.bytes(), open.table.binding.positions[9]);
            const previous = @atomicLoad(u32, &entry.length, .acquire);
            const used = payload_index.end(o, &open.table, @intCast(si));
            if (used != previous) s.length(&open.table, v, &o.counters, 9, used);
        }
        open.close(&o.counters, utc) catch return o.fail(.shared_section, .binding, 0, 0);
    }
    for (o.results[0..o.result_count], 0..) |_, gi| {
        const result = &w.opens[o.source_count + gi];
        if (result.values != null) {
            for (w.affected[0..w.affected_count]) |mi| if (o.metrics[mi].result_group == gi and !o.metrics[mi].attempted) {
                o.metrics[mi].attempted = true;
                provenance.markAttempted(&result.table, &result.provenance.?, &o.counters, o.metrics[mi].result_row);
            };
            for (w.affected[0..w.affected_count]) |mi| if (o.metrics[mi].result_group == gi) publishMetric(o, &result.table, &result.values.?, mi, utc);
            result.close(&o.counters, utc) catch return o.fail(.shared_section, .binding, gi, 0);
        }
    }
    @import("vector_inputs.zig").publish(o, &vector_writers, utc);
    o.round_generation += 1;
    o.last_utc_ms = utc;
    w.receipt.?.round_generation = o.round_generation;
    w.receipt.?.utc_ms = utc;
    w.receipt.?.committed = 1;
    if (out) |target| target.* = w.receipt.?.*;
    return .ok;
}

fn applyRule(o: *st.Owner, ri: u32, oi: ?u32, utc: i64) void {
    const w = &o.work;
    const r = &o.rules[ri];
    var d = candidate(o, ri, oi);
    const open = &w.opens[r.source];
    const t = &open.table;
    if (d.invalidate) {
        d.generation = t.load(u64, 5, r.row) + 1;
        d.status = 2;
        d.reason = 8;
        r.baseline = .{};
        r.suppressed = false;
    } else {
        r.baseline = d.baseline;
        r.suppressed = d.suppressed;
    }
    if (d.write_value) {
        if (!d.invalidate and d.kind == 5 and d.presence == 1 and d.payload_ref >> 32 != 0) {
            const i = oi.?;
            const len: u32 = @intCast(d.payload_ref >> 32);
            const off: u32 = @truncate(d.payload_ref);
            open.values.?.store(u8, t.binding.positions[9], off, w.payload[w.raw.observations.payload_offsets.?[i]..][0..len]) catch unreachable;
            o.counters.written +|= len;
            o.counters.touched +|= len;
            o.counters.payload +|= len;
        }
        st.writeChangedGood(t, &open.values.?, r.row, d, &o.counters);
        if (r.value_kind == 5) payload_index.update(o, t, ri);
    }
    s.store(t, &open.health.?, &o.counters, u32, 10, r.row, d.status);
    s.store(t, &open.health.?, &o.counters, u32, 11, r.row, d.reason);
    s.store(t, &open.health.?, &o.counters, i64, 12, r.row, utc);
}

pub fn publishMetric(o: *st.Owner, t: *const s.Table, writer: *const s.ss.Writer, metric: u32, utc: i64) void {
    const result_row = o.metrics[metric].result_row;
    var winner: ?u32 = null;
    var health_rule: ?u32 = null;
    var cursor = o.metrics[metric].first_rule;
    var visited: u32 = 0;
    while (cursor != p.none and visited < o.rule_count) : (visited += 1) {
        const ri = cursor;
        const r = o.rules[ri];
        cursor = r.next_metric;
        const rt = st.table(o, r.source) catch unreachable;
        const status = rt.load(u32, 10, r.row);
        o.counters.read +|= 4;
        if (health_rule == null or better(r, o.rules[health_rule.?])) health_rule = @intCast(ri);
        if (status == 1 and rt.load(u32, 4, r.row) != 0 and (winner == null or better(r, o.rules[winner.?]))) winner = @intCast(ri);
    }
    var retained = t.load(u64, 2, result_row);
    var gen = t.load(u64, 3, result_row);
    var identity = t.load(u64, 8, result_row);
    var selected: u64 = 0;
    var status: u32 = 3;
    var reason: u32 = 0;
    var raw_health: u32 = 2;
    var source_index: u32 = p.none;
    var local_row: u32 = p.none;
    if (winner) |ri| {
        const r = o.rules[ri];
        const rt = st.table(o, r.source) catch unreachable;
        retained = r.id;
        selected = r.id;
        identity = r.identity;
        gen = rt.load(u64, 5, r.row);
        status = 1;
        source_index = o.sources[r.source].binding_ordinal;
        local_row = r.row;
    } else {
        var retained_valid = false;
        cursor = o.metrics[metric].first_rule;
        visited = 0;
        while (cursor != p.none and visited < o.rule_count) : (visited += 1) {
            const r = o.rules[cursor];
            cursor = r.next_metric;
            if (r.id == retained and r.identity == identity) {
                const rt = st.table(o, r.source) catch unreachable;
                retained_valid = rt.load(u32, 4, r.row) != 0 and rt.load(u64, 5, r.row) == gen;
                if (retained_valid) {
                    source_index = o.sources[r.source].binding_ordinal;
                    local_row = r.row;
                }
                break;
            }
        }
        if (!retained_valid) {
            retained = 0;
            gen = 0;
            identity = 0;
        }
        if (health_rule) |ri| {
            const r = o.rules[ri];
            const rt = st.table(o, r.source) catch unreachable;
            selected = r.id;
            status = st.metricStatus(rt.load(u32, 10, r.row));
            raw_health = rt.load(u32, 10, r.row);
            reason = rt.load(u32, 11, r.row);
            if (retained == 0) {
                identity = r.identity;
                source_index = o.sources[r.source].binding_ordinal;
                local_row = r.row;
                gen = rt.load(u64, 5, r.row);
            }
        }
    }
    const c = &o.counters;
    s.store(t, writer, c, u64, 1, result_row, selected);
    s.store(t, writer, c, u64, 2, result_row, retained);
    s.store(t, writer, c, u64, 3, result_row, gen);
    s.store(t, writer, c, u32, 4, result_row, status);
    s.store(t, writer, c, u32, 5, result_row, reason);
    s.store(t, writer, c, i64, 6, result_row, utc);
    s.store(t, writer, c, u64, 7, result_row, t.load(u64, 7, result_row) + 1);
    s.store(t, writer, c, u64, 8, result_row, identity);
    s.store(t, writer, c, u32, 9, result_row, source_index);
    s.store(t, writer, c, u32, 10, result_row, local_row);
    const public_status: u32 = if (winner != null) 1 else if (retained != 0) 2 else switch (raw_health) {
        3 => 5,
        4 => 4,
        else => 3,
    };
    s.store(t, writer, c, u32, 11, result_row, public_status);
}
fn better(a: st.Rule, b: st.Rule) bool {
    return a.priority < b.priority or (a.priority == b.priority and a.id < b.id);
}
