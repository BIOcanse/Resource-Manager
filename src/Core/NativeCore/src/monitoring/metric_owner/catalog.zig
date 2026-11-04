const std = @import("std");
const p = @import("protocol.zig");
const st = @import("state.zig");
const s = @import("storage.zig");
const round = @import("round.zig");
const old = @import("../metric_snapshot/protocol.zig");
const provenance = @import("provenance.zig");
const payload_index = @import("payload_index.zig");

fn validColumns(c: p.Catalog) bool {
    return c.sources.reserved == 0 and c.rules.reserved == 0 and c.metrics.reserved == 0 and
        (c.sources.count == 0 or (c.sources.ids != null and c.sources.incarnations != null and c.sources.capabilities != null and c.sources.availability != null)) and
        (c.rules.count == 0 or (c.rules.ids != null and c.rules.source_ids != null and c.rules.metric_ids != null and c.rules.identities != null and c.rules.kinds != null and c.rules.flags != null and c.rules.priorities != null and c.rules.value_kinds != null and c.rules.minimum_bits != null and c.rules.maximum_bits != null and c.rules.capability_masks != null)) and
        (c.metrics.count == 0 or (c.metrics.ids != null and c.metrics.identities != null));
}
pub fn replace(o: *st.Owner, c: p.Catalog, bytes: []align(8) u8, utc: i64) p.Code {
    return replaceBound(o, c, o.bindings, bytes, utc);
}
pub fn replaceBound(o: *st.Owner, c: p.Catalog, declarations: []const p.SourceBinding, bytes: []align(8) u8, utc: i64) p.Code {
    o.detach();
    defer o.detach();
    if (!validColumns(c) or utc < 0) return o.fail(.invalid_contract, .none, 0, 0);
    if (c.sources.count != o.sources.len) return o.fail(.invalid_contract, .sources, c.sources.count, o.sources.len);
    if (c.rules.count > o.rules.len) return o.fail(.capacity_exceeded, .rules, c.rules.count, o.rules.len);
    if (c.metrics.count > o.metrics.len) return o.fail(.capacity_exceeded, .metrics, c.metrics.count, o.metrics.len);
    if (bytes.len > o.config.work_limit) return o.fail(.capacity_exceeded, .work, bytes.len, o.config.work_limit);
    if (!st.validWork(o, bytes)) return o.fail(.workspace_unavailable, .work, o.sizes.work, bytes.len);
    if (st.overlaps(@intFromPtr(declarations.ptr), declarations.len * @sizeOf(p.SourceBinding), @intFromPtr(bytes.ptr), bytes.len)) return o.fail(.invalid_contract, .binding, 0, 0);
    if (o.catalog_generation == std.math.maxInt(u64)) return o.fail(.capacity_exceeded, .generation, 0, o.catalog_generation);
    var fba = std.heap.FixedBufferAllocator.init(bytes);
    // Inputs may already occupy this prefix. Allocator.alloc would mark them
    // undefined even though we only need to reserve their declared extent.
    fba.end_index = @intCast(st.catalogInputBytes(o.config));
    if (!safeInputs(c, bytes, fba.end_index)) return o.fail(.invalid_contract, .work, 0, 0);
    const a = fba.allocator();
    const sources = a.alloc(st.Source, o.config.source_capacity) catch unreachable;
    const bindings = a.alloc(p.SourceBinding, o.config.source_capacity) catch unreachable;
    const rules = a.alloc(st.Rule, o.config.rule_capacity) catch unreachable;
    const metrics = a.alloc(st.Metric, o.config.metric_capacity) catch unreachable;
    const metric_states = a.alloc(st.ResultCandidate, o.config.metric_capacity) catch unreachable;
    const good = a.alloc(st.Delta, o.config.rule_capacity) catch unreachable;
    const opens = a.alloc(s.Open, @as(usize, o.config.source_capacity) * 2 + 2) catch unreachable;
    const tables = a.alloc(s.Table, @as(usize, o.config.source_capacity) * 2 + 2) catch unreachable;
    const changed = a.alloc(bool, @as(usize, o.config.source_capacity) * 2 + 2) catch unreachable;
    const payload = a.alloc(u8, @intCast(o.config.payload_work_bytes)) catch unreachable;
    var payload_used: usize = 0;
    if (declarations.len != o.bindings.len) return o.fail(.invalid_contract, .binding, declarations.len, o.bindings.len);
    @memcpy(bindings, declarations);
    if (o.legacy_bindings and o.catalog_generation == 0) for (bindings, 0..) |*binding, bi| {
        if (binding.source_id == 0) binding.source_id = c.sources.ids.?[bi];
    };
    for (bindings, 0..) |binding, bi| {
        if (binding.source_id == 0 or binding.reserved != 0 or binding.access_region >= o.result_count) return o.fail(.invalid_contract, .binding, bi, 0);
        const b = binding.table;
        _ = s.Table.init(b, 0, false) catch return o.fail(.invalid_contract, .binding, bi, 0);
        if (b.region != o.results[binding.access_region].region or st.overlaps(@intFromPtr(b.region.?), @intCast(b.byte_length), @intFromPtr(o), @intCast(o.active_length)) or st.overlaps(@intFromPtr(b.region.?), @intCast(b.byte_length), @intFromPtr(bytes.ptr), bytes.len)) return o.fail(.invalid_contract, .binding, bi, 0);
        for (o.results[0..o.result_count]) |result| if (st.bindingAlias(b, result)) return o.fail(.invalid_contract, .binding, bi, 0);
        for (bindings[0..bi]) |prior| if (prior.source_id == binding.source_id or st.bindingAlias(prior.table, b)) return o.fail(.invalid_contract, .binding, bi, 0);
    }
    var i: u32 = 0;
    while (i < c.sources.count) : (i += 1) {
        const id = c.sources.ids.?[i];
        const incarnation = c.sources.incarnations.?[i];
        const capability = c.sources.capabilities.?[i];
        const availability = c.sources.availability.?[i];
        if (id == 0 or incarnation == 0 or capability == 0 or availability < 1 or availability > 3) return o.fail(.invalid_contract, .sources, i, 0);
        var j: u32 = 0;
        while (j < i) : (j += 1) if (sources[j].id == id) return o.fail(.invalid_contract, .sources, i, 0);
        var ordinal: ?u32 = null;
        for (bindings, 0..) |binding, bi| if (binding.source_id == id) {
            ordinal = @intCast(bi);
            break;
        };
        const bi = ordinal orelse return o.fail(.invalid_contract, .binding, id, 0);
        sources[i] = .{ .id = id, .incarnation = incarnation, .capability = capability, .availability = availability, .planned_availability = availability, .binding = bindings[bi].table, .binding_ordinal = bi, .access_region = bindings[bi].access_region };
        tables[i] = s.Table.init(sources[i].binding, 0, false) catch return o.fail(.invalid_contract, .binding, i, 0);
        if (o.catalog_generation == 0 and provenance.sourceMatches(&tables[i], sources[i]) and provenance.word(&tables[i], 3) == capability) sources[i].suppressed = provenance.word(&tables[i], 5) != 0;
        for (o.sources[0..o.source_count]) |prior| if (prior.id == id and prior.incarnation == incarnation and prior.capability == capability) {
            sources[i].suppressed = prior.suppressed;
            break;
        };
        if (availability == 3) sources[i].suppressed = true;
        changed[i] = false;
    }
    i = 0;
    while (i < c.metrics.count) : (i += 1) {
        metrics[i] = .{ .id = c.metrics.ids.?[i], .identity = c.metrics.identities.?[i] };
        if (metrics[i].id == 0 or metrics[i].identity == 0) return o.fail(.invalid_contract, .metrics, i, 0);
        var j: u32 = 0;
        while (j < i) : (j += 1) if (metrics[j].id == metrics[i].id or metrics[j].identity == metrics[i].identity) return o.fail(.invalid_contract, .metrics, i, 0);
        metric_states[i] = @splat(0);
        if (o.catalog_generation != 0) {
            for (o.metrics[0..o.metric_count], 0..) |prior, mi| if (prior.id == metrics[i].id and prior.identity == metrics[i].identity) {
                const location = st.resultLocation(o, @intCast(mi)) catch return o.fail(.shared_section, .binding, mi, 0);
                const prior_table = location.table;
                metrics[i].attempted = prior.attempted;
                var slot: usize = 1;
                while (slot < 10) : (slot += 1) {
                    const physical = st.resultSlot(slot - 1);
                    metric_states[i][slot - 1] = if (physical == 4 or physical == 5 or physical == 11) prior_table.load(u32, physical, location.row) else if (physical == 6) @bitCast(prior_table.load(i64, physical, location.row)) else prior_table.load(u64, physical, location.row);
                }
                if (metric_states[i][6] == std.math.maxInt(u64)) return o.fail(.capacity_exceeded, .generation, 0, std.math.maxInt(u64));
                break;
            };
        } else {
            for (o.results[0..o.result_count]) |binding| {
                const prior_table = s.Table.init(binding, 0, true) catch return o.fail(.invalid_contract, .binding, 0, 0);
                const prior_count = provenance.count(&prior_table) orelse 0;
                var row: u32 = 0;
                while (row < prior_count) : (row += 1) if (provenance.metricMatches(&prior_table, row, metrics[i])) {
                    metrics[i].attempted = provenance.attempted(&prior_table, row);
                    for (&metric_states[i], 0..) |*value, ci| {
                        const slot = st.resultSlot(ci);
                        value.* = if (slot == 4 or slot == 5 or slot == 11) prior_table.load(u32, slot, row) else if (slot == 6) @bitCast(prior_table.load(i64, slot, row)) else prior_table.load(u64, slot, row);
                    }
                    break;
                };
            }
        }
    }
    i = 0;
    while (i < c.rules.count) : (i += 1) {
        const id = c.rules.ids.?[i];
        const identity = c.rules.identities.?[i];
        const kind = c.rules.kinds.?[i];
        const flags = c.rules.flags.?[i];
        if (id == 0 or identity == 0 or kind < 1 or kind > 19 or flags & ~@as(u32, 15) != 0) return o.fail(.invalid_contract, .rules, i, 0);
        var j: u32 = 0;
        while (j < i) : (j += 1) if (rules[j].id == id or rules[j].identity == identity) return o.fail(.invalid_contract, .rules, i, 0);
        var source: ?u32 = null;
        for (sources[0..c.sources.count], 0..) |src, si| if (src.id == c.rules.source_ids.?[i]) {
            source = @intCast(si);
            break;
        };
        var metric: ?u32 = null;
        for (metrics[0..c.metrics.count], 0..) |m, mi| if (m.id == c.rules.metric_ids.?[i]) {
            metric = @intCast(mi);
            break;
        };
        if (source == null or metric == null) return o.fail(.invalid_contract, .rules, i, 0);
        var definition = std.mem.zeroes(old.MetricDefinitionInput);
        definition.struct_size = @sizeOf(old.MetricDefinitionInput);
        definition.rule_handle = id;
        definition.metric_handle = c.rules.metric_ids.?[i];
        definition.source_handle = c.rules.source_ids.?[i];
        definition.scope_handle = 1;
        definition.scope_kind = 1;
        definition.metric_kind = kind;
        definition.value_kind = c.rules.value_kinds.?[i];
        definition.minimum_value_bits = c.rules.minimum_bits.?[i];
        definition.maximum_value_bits = c.rules.maximum_bits.?[i];
        definition.source_priority = c.rules.priorities.?[i];
        definition.semantic_fingerprint = 1;
        definition.flags = flags;
        // The new presence contract admits empty bytes separately from absent
        // and null; the old numeric/range helpers otherwise remain authoritative.
        if (definition.value_kind == 5 and definition.minimum_value_bits == 0) definition.minimum_value_bits = 1;
        if (definition.value_kind == 5 and c.rules.minimum_bits.?[i] == 0 and definition.maximum_value_bits == 0) definition.maximum_value_bits = 1;
        if (c.rules.value_kinds.?[i] == 5 and c.rules.maximum_bits.?[i] > o.config.max_value_bytes) return o.fail(.capacity_exceeded, .payload, c.rules.maximum_bits.?[i], o.config.max_value_bytes);
        if (!old.validMetricDefinition(&definition)) return o.fail(.invalid_contract, .rules, i, 0);
        const si = source.?;
        const row = sources[si].rows;
        sources[si].rows += 1;
        rules[i] = .{ .id = id, .identity = identity, .source = si, .metric = metric.?, .row = row, .kind = kind, .flags = flags, .priority = c.rules.priorities.?[i], .value_kind = c.rules.value_kinds.?[i], .minimum = c.rules.minimum_bits.?[i], .maximum = c.rules.maximum_bits.?[i], .capability_mask = c.rules.capability_masks.?[i] };
        const m = &metrics[metric.?];
        if (m.value_kind != 0 and m.value_kind != rules[i].value_kind) return o.fail(.invalid_contract, .metrics, m.id, 0);
        m.value_kind = rules[i].value_kind;
        m.result_group = @max(m.result_group, sources[si].access_region);
        if (m.last_rule != p.none) rules[m.last_rule].next_metric = i else m.first_rule = i;
        m.last_rule = i;
        const src = &sources[si];
        if (src.last_rule != p.none) rules[src.last_rule].next_source = i else src.first_rule = i;
        src.last_rule = i;
        good[i] = .{ .kind = rules[i].value_kind };
        tables[si] = s.Table.init(src.binding, 0, false) catch return o.fail(.invalid_contract, .binding, si, 0);
        if (row + 1 > tables[si].rowCapacity()) return o.fail(.capacity_exceeded, .rules, row + 1, tables[si].rowCapacity());
        const previous_generation = tables[si].load(u64, 5, row);
        if (previous_generation == std.math.maxInt(u64)) return o.fail(.capacity_exceeded, .generation, 0, previous_generation);
        good[i].generation = previous_generation + 1;
        // An exact identity includes origin incarnation and calculation facts.
        for (o.rules[0..o.rule_count]) |prior| {
            const prior_source = o.sources[prior.source];
            if (prior.id != id or prior.identity != identity or prior_source.id != sources[si].id or prior_source.incarnation != sources[si].incarnation or prior.kind != kind or prior.flags != flags or
                prior.value_kind != rules[i].value_kind or prior.minimum != rules[i].minimum or prior.maximum != rules[i].maximum or prior.capability_mask != rules[i].capability_mask or o.metrics[prior.metric].id != m.id or o.metrics[prior.metric].identity != m.identity) continue;
            const t = st.table(o, prior.source) catch return o.fail(.shared_section, .binding, prior.source, 0);
            good[i] = st.readGood(&t, prior.row, &o.counters);
            if (!provenance.validGood(&t, rules[i], good[i])) return o.fail(.invalid_contract, .rules, i, 0);
            good[i].old_generation = good[i].generation;
            if (!std.meta.eql(prior_source.binding, sources[si].binding) or prior.row != row) {
                const next = @max(previous_generation, good[i].generation);
                if (next == std.math.maxInt(u64)) return o.fail(.capacity_exceeded, .generation, 0, next);
                good[i].generation = next + 1;
            }
            if (prior_source.capability == sources[si].capability) {
                rules[i].baseline = prior.baseline;
                rules[i].suppressed = prior.suppressed;
            } else {
                good[i].status = 2;
                rules[i].baseline = if (prior.baseline.schema == 16) prior.baseline else .{};
            }
            const len: usize = @intCast(good[i].payload_ref >> 32);
            const off: usize = @as(u32, @truncate(good[i].payload_ref));
            if (len > o.config.max_value_bytes or off > t.specs[9].size or len > t.specs[9].size - off) return o.fail(.invalid_contract, .payload, len, o.config.max_value_bytes);
            if (len > 0) {
                if (len > payload.len - payload_used) return o.fail(.capacity_exceeded, .payload, payload_used + len, payload.len);
                const area = t.layout.dataArea(t.bytes());
                @memcpy(payload[payload_used..][0..len], area[@as(usize, t.specs[9].offset) + off ..][0..len]);
                good[i].payload_ref = @as(u64, @intCast(payload_used)) | (@as(u64, len) << 32);
                payload_used += len;
            }
            break;
        }
        if (o.catalog_generation == 0 and provenance.sourceMatches(&tables[si], src.*)) {
            const prior_count = provenance.count(&tables[si]).?;
            var prior_row: u32 = 0;
            while (prior_row < prior_count) : (prior_row += 1) if (provenance.ruleMatches(&tables[si], prior_row, rules[i], m.*, src.*)) {
                good[i] = st.readGood(&tables[si], prior_row, &o.counters);
                if (!provenance.validGood(&tables[si], rules[i], good[i])) return o.fail(.invalid_contract, .rules, i, 0);
                good[i].old_generation = good[i].generation;
                if (prior_row != row) {
                    const next = @max(previous_generation, good[i].generation);
                    if (next == std.math.maxInt(u64)) return o.fail(.capacity_exceeded, .generation, 0, next);
                    good[i].generation = next + 1;
                }
                rules[i].suppressed = good[i].status == 3 and provenance.word(&tables[si], 3) == src.capability;
                if (good[i].status == 1) good[i].status = 2;
                const len: usize = @intCast(good[i].payload_ref >> 32);
                const off: usize = @as(u32, @truncate(good[i].payload_ref));
                if (len > o.config.max_value_bytes or off > tables[si].specs[9].size or len > tables[si].specs[9].size - off) return o.fail(.invalid_contract, .payload, len, o.config.max_value_bytes);
                if (len > payload.len - payload_used) return o.fail(.capacity_exceeded, .payload, payload_used + len, payload.len);
                if (len > 0) {
                    @memcpy(payload[payload_used..][0..len], tables[si].layout.dataArea(tables[si].bytes())[@as(usize, tables[si].specs[9].offset) + off ..][0..len]);
                    good[i].payload_ref = @as(u64, @intCast(payload_used)) | (@as(u64, len) << 32);
                    payload_used += len;
                }
                break;
            };
        }
        if (sources[si].availability != 1) {
            good[i].status = if (sources[si].availability == 3) 3 else 2;
            if (rules[i].baseline.schema != 16) rules[i].baseline = .{};
        }
        // Changed membership/order alone controls the identity writer.
        const prior_count = provenance.count(&tables[si]) orelse 0;
        const same_row = row < prior_count and provenance.sourceMatches(&tables[si], src.*) and tables[si].load(u64, 0, row) == identity and tables[si].load(u64, 15, row) == id;
        if (!same_row) changed[si] = true;
    }
    for (sources[0..c.sources.count], 0..) |src, si| {
        if (src.rows != (provenance.count(&tables[si]) orelse 0) or tables[si].identitySequence() == 0) changed[si] = true;
        tables[si] = s.Table.init(src.binding, 0, false) catch return o.fail(.invalid_contract, .binding, si, 0);
        for (tables[si].specs[0..16], 0..) |spec, slot| if (slot != 9 and src.rows > spec.size / spec.element_width) return o.fail(.capacity_exceeded, .rules, src.rows, spec.size / spec.element_width);
        var needed: u64 = 0;
        for (rules[0..c.rules.count], 0..) |r, ri| if (r.source == si) {
            needed += good[ri].payload_ref >> 32;
        };
        if (needed > tables[si].specs[9].size) return o.fail(.payload_capacity, .payload, needed, tables[si].specs[9].size);
    }
    var group_rows = [_]u32{ 0, 0 };
    for (metrics[0..c.metrics.count]) |*m| {
        if (m.value_kind == 0) return o.fail(.invalid_contract, .metrics, m.id, 0);
        m.result_row = group_rows[m.result_group];
        group_rows[m.result_group] += 1;
    }
    var epoch = o.owner_epoch;
    for (o.results[0..o.result_count], 0..) |binding, gi| {
        const at = c.sources.count + gi;
        tables[at] = s.Table.init(binding, 0, true) catch return o.fail(.invalid_contract, .binding, gi, 0);
        if (group_rows[gi] > tables[at].rowCapacity()) return o.fail(.capacity_exceeded, .metrics, group_rows[gi], tables[at].rowCapacity());
        const rt = &tables[at];
        if (o.catalog_generation == 0) {
            const sequence = @atomicLoad(u64, &rt.layout.source(rt.bytes(), binding.provenance_source).sequence, .acquire);
            const previous = @max(sequence / 2, if (provenance.count(rt) != null) provenance.word(rt, 1) else 0);
            if (previous >= std.math.maxInt(u32)) return o.fail(.capacity_exceeded, .generation, previous + 1, std.math.maxInt(u32));
            epoch = @max(epoch, previous + 1);
        }
        changed[at] = group_rows[gi] != (provenance.count(rt) orelse 0) or rt.identitySequence() == 0;
        for (metrics[0..c.metrics.count], 0..) |m, mi| if (m.result_group == gi) {
            if (!provenance.metricMatches(rt, m.result_row, m)) changed[at] = true;
            metric_states[mi][6] = @max(metric_states[mi][6], rt.load(u64, 7, m.result_row));
            if (metric_states[mi][6] == std.math.maxInt(u64)) return o.fail(.capacity_exceeded, .generation, 0, metric_states[mi][6]);
        };
    }
    // Admit abandoned physical origins too, before any mutation. Their durable
    // row/provenance contracts remain intact; only fast sources become stopped.
    const publication_count = @as(usize, c.sources.count) + o.result_count;
    var table_count = publication_count;
    for (o.bindings) |prior| {
        var still_used = false;
        for (bindings) |binding| if (st.bindingAlias(prior.table, binding.table)) {
            still_used = true;
            break;
        };
        if (!still_used) {
            tables[table_count] = s.Table.init(prior.table, 0, false) catch return o.fail(.shared_section, .binding, table_count, 0);
            changed[table_count] = false;
            table_count += 1;
        }
    }
    var opened: usize = 0;
    defer {
        for (opens[0..opened]) |*open| open.closeUnchanged();
    }
    for (tables[0..table_count], 0..) |t, si| {
        opens[si] = if (si < publication_count)
            s.Open.beginCatalog(t, changed[si], true, si < c.sources.count) catch return o.fail(.shared_section, .binding, si, 0)
        else
            s.Open.begin(t, false, true, true) catch return o.fail(.shared_section, .binding, si, 0);
        opened += 1;
    }
    // Close membership writers before stamping dependent columns.
    for (opens[0..opened], 0..) |*open, si| if (open.rows) |*writer| {
        if (si < c.sources.count) {
            for (rules[0..c.rules.count]) |r| if (r.source == si) {
                s.store(&open.table, writer, &o.counters, u64, 0, r.row, r.identity);
                s.store(&open.table, writer, &o.counters, u64, 15, r.row, r.id);
            };
            s.length(&open.table, writer, &o.counters, 0, sources[si].rows);
            s.length(&open.table, writer, &o.counters, 15, sources[si].rows);
        } else {
            const group = si - c.sources.count;
            for (metrics[0..c.metrics.count]) |m| if (m.result_group == group) s.store(&open.table, writer, &o.counters, u64, 0, m.result_row, m.identity);
            s.length(&open.table, writer, &o.counters, 0, group_rows[group]);
        }
        s.finish(writer, &o.counters, utc) catch return o.fail(.shared_section, .binding, si, 0);
        open.rows = null;
    };
    for (sources[0..c.sources.count], 0..) |src, si| {
        const open = &opens[si];
        var used: u32 = 0;
        for (rules[0..c.rules.count], 0..) |r, ri| if (r.source == si) {
            var d = good[ri];
            const len: u32 = @intCast(d.payload_ref >> 32);
            const off: u32 = @truncate(d.payload_ref);
            if (len > 0) {
                open.values.?.store(u8, open.table.binding.positions[9], used, payload[off..][0..len]) catch unreachable;
                o.counters.written +|= len;
                o.counters.touched +|= len;
                o.counters.payload +|= len;
                d.payload_ref = @as(u64, used) | (@as(u64, len) << 32);
                used += len;
            }
            st.writeGood(&open.table, &open.values.?, r.row, d, &o.counters);
            s.store(&open.table, &open.health.?, &o.counters, u32, 10, r.row, d.status);
            s.store(&open.table, &open.health.?, &o.counters, u32, 11, r.row, d.reason);
            s.store(&open.table, &open.health.?, &o.counters, i64, 12, r.row, d.attempt);
        };
        var slot: usize = 1;
        while (slot < 15) : (slot += 1) s.length(&open.table, if (slot >= 10 and slot <= 12) &open.health.? else &open.values.?, &o.counters, slot, if (slot == 9) used else src.rows);
        provenance.header(&open.table, &open.provenance.?, &o.counters, src, src.rows);
        var ri = src.first_rule;
        while (ri != p.none) {
            provenance.rule(&open.table, &open.provenance.?, &o.counters, rules[ri], metrics[rules[ri].metric]);
            ri = rules[ri].next_source;
        }
        open.close(&o.counters, utc) catch return o.fail(.shared_section, .binding, si, 0);
    }
    @memcpy(o.bindings, bindings);
    @memcpy(o.sources[0..c.sources.count], sources[0..c.sources.count]);
    @memcpy(o.rules[0..c.rules.count], rules[0..c.rules.count]);
    @memcpy(o.metrics[0..c.metrics.count], metrics[0..c.metrics.count]);
    o.source_count = c.sources.count;
    o.rule_count = c.rules.count;
    o.metric_count = c.metrics.count;
    o.result_rows = group_rows;
    if (o.catalog_generation == 0) {
        o.owner_epoch = epoch;
        o.token = epoch << 32;
    }
    payload_index.rebuild(o);
    // Restore exact metric identities through reordering, then validate retained
    // references against the newly published source-local rule rows.
    for (metrics[0..c.metrics.count], 0..) |m, mi| {
        const result = &opens[c.sources.count + m.result_group];
        var snap = metric_states[mi];
        var matched = false;
        var ri = o.metrics[mi].first_rule;
        while (ri != p.none) {
            const r = o.rules[ri];
            if (r.id == snap[1] and r.identity == snap[7] and good[ri].old_generation == snap[2] and good[ri].presence != 0) {
                snap[2] = good[ri].generation;
                matched = true;
                break;
            }
            ri = r.next_metric;
        }
        if (!matched) {
            snap[1] = 0;
            snap[2] = 0;
            snap[7] = 0;
        }
        s.store(&result.table, &result.values.?, &o.counters, u64, 2, m.result_row, snap[1]);
        s.store(&result.table, &result.values.?, &o.counters, u64, 3, m.result_row, snap[2]);
        s.store(&result.table, &result.values.?, &o.counters, u64, 7, m.result_row, snap[6]);
        s.store(&result.table, &result.values.?, &o.counters, u64, 8, m.result_row, snap[7]);
        round.publishMetric(o, &result.table, &result.values.?, @intCast(mi), @bitCast(snap[5]));
    }
    for (o.results[0..o.result_count], 0..) |_, gi| {
        const result = &opens[c.sources.count + gi];
        var slot: usize = 1;
        while (slot < 12) : (slot += 1) s.length(&result.table, &result.values.?, &o.counters, slot, group_rows[gi]);
        provenance.header(&result.table, &result.provenance.?, &o.counters, null, group_rows[gi]);
        provenance.ownerEpoch(&result.table, &result.provenance.?, &o.counters, o.owner_epoch);
        for (o.metrics[0..o.metric_count]) |m| if (m.result_group == gi) provenance.metric(&result.table, &result.provenance.?, &o.counters, m.result_row, m);
        result.close(&o.counters, utc) catch return o.fail(.shared_section, .binding, gi, 0);
    }
    for (opens[publication_count..table_count]) |*open| {
        open.values.?.end(.{ .unavailable = p.no_subscription_reason }, utc) catch unreachable;
        open.values = null;
        open.health.?.end(.{ .unavailable = p.no_subscription_reason }, utc) catch unreachable;
        open.health = null;
        o.counters.written +|= 64;
        o.counters.touched +|= 64;
    }
    o.catalog_generation += 1;
    o.last_utc_ms = utc;
    return .ok;
}

fn safeRange(comptime T: type, ptr: ?[*]const T, count: u32, bytes: []align(8) u8, prefix: usize) bool {
    if (count == 0) return true;
    const start = @intFromPtr(ptr.?);
    const length = @as(usize, count) * @sizeOf(T);
    if (start % @alignOf(T) != 0) return false;
    if (!st.overlaps(start, length, @intFromPtr(bytes.ptr), bytes.len)) return true;
    const base = @intFromPtr(bytes.ptr);
    return start >= base and length <= prefix and start - base <= prefix - length;
}
fn safeInputs(c: p.Catalog, bytes: []align(8) u8, prefix: usize) bool {
    if (!safeRange(u32, c.sources.availability, c.sources.count, bytes, prefix)) return false;
    inline for (.{ "ids", "incarnations", "capabilities" }) |field| if (!safeRange(u64, @field(c.sources, field), c.sources.count, bytes, prefix)) return false;
    inline for (.{ "ids", "source_ids", "metric_ids", "identities", "minimum_bits", "maximum_bits", "capability_masks" }) |field| if (!safeRange(u64, @field(c.rules, field), c.rules.count, bytes, prefix)) return false;
    inline for (.{ "kinds", "flags", "priorities", "value_kinds" }) |field| if (!safeRange(u32, @field(c.rules, field), c.rules.count, bytes, prefix)) return false;
    inline for (.{ "ids", "identities" }) |field| if (!safeRange(u64, @field(c.metrics, field), c.metrics.count, bytes, prefix)) return false;
    return true;
}
