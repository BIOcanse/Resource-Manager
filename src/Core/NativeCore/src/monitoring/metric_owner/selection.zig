//! Request and physical-reading decisions over borrowed native catalog columns.
const std = @import("std");
const c = @import("catalog_compiler.zig");
const p = c.p;
pub const Request = extern struct {
    text: c.Text = .{},
    keys: ?[*]const c.Ref = null,
    count: u32 = 0,
    flags: u32 = 0, // all1, all GPU core2, catalog4
};
/// Passive support facts, one per compiled rule. Epoch mismatch is unverified.
/// availability:0 unverified,1 available,2 unsupported. Quantities are raw
/// provider-declared strings, used only for hardware-monitor CPU temperature.
pub const ReadingFacts = extern struct {
    count: u32 = 0,
    reserved: u32 = 0,
    epochs: ?[*]const u64 = null,
    availability: ?[*]const u32 = null,
    quantities: ?[*]const c.Ref = null,
    text: c.Text = .{},
    integrated_gpu: c.Ref = .{},
};
/// Raw adapter results. Identity columns are checked against current source
/// incarnation/capability and the admitted observation sequence before learning.
pub const ReadingEvents = extern struct {
    count: u32 = 0,
    reserved: u32 = 0,
    rule_indices: ?[*]const u32 = null,
    incarnations: ?[*]const u64 = null,
    capabilities: ?[*]const u64 = null,
    observations: ?[*]const u64 = null,
    statuses: ?[*]const u32 = null,
    supported: ?[*]const u32 = null,
    source_unsupported: ?[*]const u32 = null,
    quantities: ?[*]const c.Ref = null,
    current_observations: ?[*]const u64 = null,
    text: c.Text = .{},
    integrated_gpu: c.Ref = .{},
    integrated_rule: u32 = p.none,
    tail_reserved: u32 = 0,
};
pub fn observeRequired(cat: c.Compiled) c.Error!u64 {
    if (!validCatalog(cat)) return error.Invalid;
    return @as(u64, cat.bindings.count) * 4120 + 8192;
}
pub fn observe(cat: c.Compiled, prior: ReadingFacts, events: ReadingEvents, work: []align(8) u8, failure: *p.Failure) c.Error!ReadingFacts {
    failure.* = .{};
    const required_bytes = try observeRequired(cat);
    if (work.len < required_bytes) return c.fail(failure, .workspace_unavailable, .work, required_bytes, work.len);
    const n = cat.bindings.count;
    if (events.count > n) return c.fail(failure, .capacity_exceeded, .rules, events.count, n);
    if (!catalogDisjoint(cat, work) or c.overlap(@intFromPtr(work.ptr), work.len, @intFromPtr(events.current_observations), @as(usize, cat.sources.count) * 8)) return error.Invalid;
    if (prior.count != n or prior.reserved != 0 or events.reserved != 0 or events.tail_reserved != 0 or (n != 0 and (prior.epochs == null or prior.availability == null or prior.quantities == null)) or (cat.sources.count != 0 and events.current_observations == null)) return error.Invalid;
    inline for (@typeInfo(ReadingEvents).@"struct".field_names, @typeInfo(ReadingEvents).@"struct".field_types) |field_name, field_type| {
        if (@typeInfo(field_type) == .optional and events.count != 0 and @field(events, field_name) == null) return error.Invalid;
    }
    if (!c.disjointColumns(ReadingFacts, prior, n, work) or !c.disjointColumns(ReadingEvents, events, events.count, work) or c.overlap(@intFromPtr(work.ptr), work.len, @intFromPtr(prior.text.bytes), prior.text.length) or c.overlap(@intFromPtr(work.ptr), work.len, @intFromPtr(events.text.bytes), events.text.length)) return error.Invalid;
    var i: u32 = 0;
    while (i < n) : (i += 1) {
        if (prior.availability.?[i] > 2 or (try c.get(prior.text, prior.quantities.?[i])).len > 4096) return error.Invalid;
    }
    const integrated = try c.get(events.text, events.integrated_gpu);
    if (integrated.len > 4096 or (integrated.len != 0 and (events.integrated_rule >= n or !c.exact(integrated)))) return error.Invalid;
    i = 0;
    while (i < events.count) : (i += 1) {
        if (events.rule_indices.?[i] >= n or events.statuses.?[i] > 4 or events.supported.?[i] > 1 or events.source_unsupported.?[i] > 1 or (events.source_unsupported.?[i] == 1 and events.statuses.?[i] != 3) or (try c.get(events.text, events.quantities.?[i])).len > 4096) return error.Invalid;
        var j: u32 = 0;
        while (j < i) : (j += 1) if (events.rule_indices.?[i] == events.rule_indices.?[j]) return error.Invalid;
    }
    var fba = std.heap.FixedBufferAllocator.init(work);
    const a = fba.allocator();
    const epochs = a.alloc(u64, n) catch return error.Work;
    const availability = a.alloc(u32, n) catch return error.Work;
    const quantities = a.alloc(c.Ref, n) catch return error.Work;
    const quantity_from_event = a.alloc(bool, n) catch return error.Work;
    var strings = Strings{ .bytes = work[fba.end_index..], .failure = failure };
    i = 0;
    while (i < n) : (i += 1) {
        const src = cat.bindings.source_indices.?[i];
        epochs[i] = cat.sources.capabilities.?[src];
        const same_epoch = prior.epochs.?[i] == epochs[i];
        availability[i] = if (cat.sources.availability.?[src] == 3) 2 else if (same_epoch) prior.availability.?[i] else 0;
        quantities[i] = if (same_epoch) prior.quantities.?[i] else .{};
        quantity_from_event[i] = false;
    }
    var integrated_result = prior.integrated_gpu;
    var integrated_from_event = false;
    i = 0;
    while (i < events.count) : (i += 1) {
        const row = events.rule_indices.?[i];
        const src = cat.bindings.source_indices.?[row];
        if (events.incarnations.?[i] != cat.sources.incarnations.?[src] or events.capabilities.?[i] != cat.sources.capabilities.?[src] or events.observations.?[i] != events.current_observations.?[src]) continue;
        if (events.source_unsupported.?[i] != 0) {
            var j: u32 = 0;
            while (j < n) : (j += 1) if (cat.bindings.source_indices.?[j] == src) {
                availability[j] = 2;
            };
        }
        const quantity = try c.get(events.text, events.quantities.?[i]);
        if (quantity.len != 0) {
            quantities[row] = events.quantities.?[i];
            quantity_from_event[row] = true;
        }
        // Old tracker applies declared support first, then the actual accepted
        // unsupported observation/frame status. The latter takes precedence.
        if (events.statuses.?[i] == 3) availability[row] = 2 else if (events.supported.?[i] != 0 or events.statuses.?[i] == 1) availability[row] = 1;
        if (integrated.len != 0 and row == events.integrated_rule) {
            integrated_result = events.integrated_gpu;
            integrated_from_event = true;
        }
        // Multiple native rules for one canonical field share support facts.
        var j: u32 = 0;
        while (j < n) : (j += 1) if (cat.bindings.metric_indices.?[j] == cat.bindings.metric_indices.?[row] and cat.bindings.source_indices.?[j] == src) {
            availability[j] = availability[row];
            quantities[j] = quantities[row];
            quantity_from_event[j] = quantity_from_event[row];
        };
    }
    i = 0;
    while (i < n) : (i += 1) quantities[i] = try strings.put(try c.get(if (quantity_from_event[i]) events.text else prior.text, quantities[i]));
    integrated_result = try strings.put(try c.get(if (integrated_from_event) events.text else prior.text, integrated_result));
    return .{ .count = n, .epochs = epochs.ptr, .availability = availability.ptr, .quantities = quantities.ptr, .text = .{ .bytes = strings.bytes.ptr, .length = strings.used }, .integrated_gpu = integrated_result };
}
pub const Selection = extern struct {
    metric_count: u32 = 0,
    rule_count: u32 = 0,
    metric_indices: ?[*]const u32 = null,
    metric_ids: ?[*]const u64 = null,
    rule_indices: ?[*]const u32 = null, // grouped by source, then catalog rule order
    source_offsets: ?[*]const u32 = null, // sources.count+1, field batches
    preferred_rules: ?[*]const u32 = null, // each rule -> preferred or none
    availability: ?[*]const u32 = null,
    source_ranks: ?[*]const u32 = null,
    alias_ranks: ?[*]const u32 = null,
    quantities: ?[*]const c.Ref = null,
    reading_ids: ?[*]const c.Ref = null,
    legacy_keys: ?[*]const c.Ref = null,
    legacy_rules: ?[*]const u32 = null,
    legacy_count: u32 = 0,
    dataset_mask: u32 = 0,
    text: c.Text = .{},
};
/// Caller-owned actual-run continuity only. No fields or global state in Zig.
pub const ProviderState = extern struct {
    incarnation: u64,
    capability: u64,
    observation: u64,
    unavailable_until_ms: i64 = 0,
    unsupported: u32 = 0,
    reserved: u32 = 0,
};
pub const ProviderEvent = extern struct {
    incarnation: u64,
    capability: u64,
    observation: u64,
    completed_ms: i64,
    cooldown_ms: u64,
    status: u32,
    apply_cooldown: u32,
};
/// Status retains old SourceStatus complete1/partial2/unavailable3/unsupported4.
/// Completion identity must match the admitted observation; no hidden retry.
pub fn providerComplete(state: *ProviderState, event: ProviderEvent) c.Error!void {
    if (state.reserved != 0 or state.incarnation == 0 or state.capability == 0 or state.unsupported > 1 or event.status < 1 or event.status > 5 or event.apply_cooldown > 1 or event.completed_ms < 0 or event.cooldown_ms > std.math.maxInt(i64)) return error.Invalid;
    if (event.incarnation != state.incarnation or event.capability != state.capability or event.observation != state.observation) return error.Identity;
    const until = if (event.status == 3 and event.apply_cooldown == 1) std.math.add(i64, event.completed_ms, @intCast(event.cooldown_ms)) catch return error.Exhausted else 0;
    if (event.status == 4) state.unsupported = 1;
    state.unavailable_until_ms = if (event.status == 3 and event.apply_cooldown == 1) until else 0;
}
pub fn providerBegin(state: *ProviderState, now: i64) c.Error!u64 {
    if (state.reserved != 0 or state.incarnation == 0 or state.capability == 0 or state.unsupported > 1 or now < 0) return error.Invalid;
    if (state.unsupported != 0 or state.unavailable_until_ms > now) return error.Invalid;
    if (state.observation == std.math.maxInt(u64)) return error.Exhausted;
    state.observation += 1;
    return state.observation;
}
pub fn providerAvailability(state: ProviderState, now: i64) u32 {
    return if (state.unsupported != 0) 3 else if (state.unavailable_until_ms > now) 2 else 1;
}
pub fn required(catalog: c.Compiled) c.Error!u64 {
    if (!validCatalog(catalog)) return error.Invalid;
    var text: u64 = 0;
    var i: u32 = 0;
    while (i < catalog.bindings.count) : (i += 1) {
        text += (try c.get(catalog.text, catalog.bindings.field_names.?[i])).len + (try c.get(catalog.text, catalog.bindings.legacy.?[i])).len + 4608;
    }
    return @as(u64, catalog.bindings.count) * 80 + @as(u64, catalog.catalog.metrics.count) * 24 + @as(u64, catalog.sources.count) * 8 + text + 512;
}
fn validCatalog(cat: c.Compiled) bool {
    const n = cat.catalog.rules.count;
    if (cat.bindings.count != n or cat.bindings.reserved != 0 or cat.text.reserved != 0) return false;
    inline for (@typeInfo(c.Bindings).@"struct".field_names, @typeInfo(c.Bindings).@"struct".field_types) |field_name, field_type| {
        if (@typeInfo(field_type) == .optional and n != 0 and @field(cat.bindings, field_name) == null) return false;
    }
    inline for (@typeInfo(p.RuleColumns).@"struct".field_names, @typeInfo(p.RuleColumns).@"struct".field_types) |field_name, field_type| {
        if (@typeInfo(field_type) == .optional and n != 0 and @field(cat.catalog.rules, field_name) == null) return false;
    }
    if (cat.sources.count != cat.catalog.sources.count or !c.pointers(c.Sources, cat.sources) or !c.pointers(c.Scopes, cat.scopes)) return false;
    if (cat.catalog.metrics.count != 0 and (cat.catalog.metrics.ids == null or cat.catalog.metrics.identities == null)) return false;
    var i: u32 = 0;
    while (i < cat.sources.count) : (i += 1) {
        _ = c.get(cat.fact_text, cat.sources.names.?[i]) catch return false;
    }
    i = 0;
    while (i < n) : (i += 1) {
        if (cat.bindings.source_indices.?[i] >= cat.sources.count or cat.bindings.metric_indices.?[i] >= cat.catalog.metrics.count) return false;
        inline for (.{ "addresses", "legacy", "field_names", "device_keys" }) |name| {
            _ = c.get(cat.text, @field(cat.bindings, name).?[i]) catch return false;
        }
    }
    return true;
}
fn catalogDisjoint(cat: c.Compiled, work: []align(8) u8) bool {
    return c.disjointColumns(p.RuleColumns, cat.catalog.rules, cat.catalog.rules.count, work) and c.disjointColumns(p.MetricColumns, cat.catalog.metrics, cat.catalog.metrics.count, work) and c.disjointColumns(c.Bindings, cat.bindings, cat.bindings.count, work) and c.disjointColumns(c.Sources, cat.sources, cat.sources.count, work) and c.disjointColumns(c.Scopes, cat.scopes, cat.scopes.count, work) and !c.overlap(@intFromPtr(work.ptr), work.len, @intFromPtr(cat.text.bytes), cat.text.length) and !c.overlap(@intFromPtr(work.ptr), work.len, @intFromPtr(cat.fact_text.bytes), cat.fact_text.length);
}
fn source(cat: c.Compiled, row: u32) []const u8 {
    return c.get(cat.fact_text, cat.sources.names.?[cat.bindings.source_indices.?[row]]) catch unreachable;
}
const Reading = struct { quantity: []const u8, source_rank: u32 = 10, alias_rank: u32 = 0 };
fn reading(cat: c.Compiled, facts: ReadingFacts, row: u32, availability: u32) c.Error!Reading {
    const code = cat.bindings.field_codes.?[row];
    const src = source(cat, row);
    const fallback = try c.get(cat.text, cat.bindings.field_names.?[row]);
    if (c.eql(src, "amd.smu")) return switch (code) {
        7 => .{ .quantity = "cpu.control_temperature" },
        17 => .{ .quantity = if (facts.integrated_gpu.length == 0) "cpu.apu_temperature_unmapped" else "gpu.edge_temperature", .alias_rank = 1 },
        60 => .{ .quantity = "gpu.edge_temperature" },
        9 => .{ .quantity = "cpu.package_power_actual" },
        5 => .{ .quantity = "cpu.core_voltage_reported" },
        6 => .{ .quantity = "cpu.package_current_reported" },
        else => generic(code, fallback),
    };
    if (code == 7 and c.eql(src, "hardware-monitor.core")) {
        const declared = if (availability != 0) try c.get(facts.text, facts.quantities.?[row]) else "";
        return .{ .quantity = if (declared.len == 0) "cpu.temperature_reported" else declared, .source_rank = 20 };
    }
    if (code == 312 and c.eql(src, "amd.smn")) return .{ .quantity = "cpu.ccd_die_temperature" };
    if (code == 147 and c.eql(src, "intel.msr")) return .{ .quantity = "cpu.package_temperature" };
    if (c.eql(src, "intel.igcl")) switch (code) {
        229 => return .{ .quantity = "gpu.highest_sensor_temperature" },
        251 => return .{ .quantity = "gpu.chip_interval_power" },
        233 => return .{ .quantity = "gpu.supply_voltage" },
        else => {},
    };
    if (code == 107 and c.eql(src, "windows.nvme-health")) return .{ .quantity = "storage.composite_temperature" };
    if (code == 47 and c.eql(src, "windows.storage-sensors")) {
        var i: u32 = 0;
        while (i < cat.bindings.count) : (i += 1) if (cat.bindings.scope_ids.?[i] == cat.bindings.scope_ids.?[row] and cat.bindings.field_codes.?[i] == 107) return .{ .quantity = "storage.composite_temperature", .source_rank = 20 };
    }
    if (c.eql(src, "amd.adlx")) switch (code) {
        60 => return .{ .quantity = "gpu.edge_temperature", .source_rank = 20 },
        53 => return .{ .quantity = "gpu.dedicated_memory_used_reported" },
        228 => return .{ .quantity = "gpu.dedicated_memory_used_reported", .alias_rank = 1 },
        55 => return .{ .quantity = "gpu.dedicated_memory_used_reported_percent" },
        57 => return .{ .quantity = "gpu.chip_power_reported" },
        else => {},
    };
    if (c.eql(src, "nvidia.nvml")) switch (code) {
        60 => return .{ .quantity = "gpu.core_temperature" },
        53 => return .{ .quantity = "gpu.fb_used_including_reserved" },
        55 => return .{ .quantity = "gpu.fb_used_including_reserved_percent" },
        57 => return .{ .quantity = "gpu.power_nvml_reported" },
        else => {},
    };
    if (c.eql(src, "pdh.gpu-engine")) switch (code) {
        50 => return .{ .quantity = "gpu.busiest_engine_busy_percent" },
        313 => return .{ .quantity = "gpu.adapter_shared_memory_used" },
        else => {},
    };
    return generic(code, fallback);
}
fn generic(code: u32, fallback: []const u8) Reading {
    return .{ .quantity = switch (code) {
        1 => "cpu.busy_percent",
        2 => "cpu.frequency_reported",
        3 => "cpu.frequency_reference_percent",
        19 => "cpu.cooling_fan_rpm",
        20 => "cpu.cooling_fan_speed_percent",
        21 => "memory.physical_used",
        22 => "memory.physical_used_percent",
        25 => "memory.commit_used",
        26 => "memory.commit_used_percent",
        32 => "memory.temperature_reported",
        51 => "gpu.graphics_clock_reported",
        52 => "gpu.graphics_clock_reference_percent",
        56 => "gpu.memory_clock_reported",
        64 => "gpu.cooling_fan_rpm",
        63 => "gpu.cooling_fan_speed_percent",
        66 => "gpu.current_reported",
        65 => "gpu.core_voltage_reported",
        else => fallback,
    }, .source_rank = switch (code) {
        1, 2, 3, 19, 20, 21, 22, 25, 26, 32, 51, 52, 56, 64, 63, 66, 65 => 10,
        else => 100,
    } };
}
fn device(cat: c.Compiled, facts: ReadingFacts, row: u32) c.Error![]const u8 {
    if (cat.bindings.field_codes.?[row] == 17 and c.eql(source(cat, row), "amd.smu") and facts.integrated_gpu.length != 0) return c.get(facts.text, facts.integrated_gpu);
    return c.get(cat.text, cat.bindings.device_keys.?[row]);
}
fn preferred(cat: c.Compiled, ranks: []const u32, aliases: []const u32, a: u32, b: u32) bool {
    if (ranks[a] != ranks[b]) return ranks[a] < ranks[b];
    if (aliases[a] != aliases[b]) return aliases[a] < aliases[b];
    const name = std.mem.order(u8, c.get(cat.text, cat.bindings.field_names.?[a]) catch unreachable, c.get(cat.text, cat.bindings.field_names.?[b]) catch unreachable);
    if (name != .eq) return name == .lt;
    return std.mem.order(u8, c.get(cat.text, cat.bindings.device_keys.?[a]) catch unreachable, c.get(cat.text, cat.bindings.device_keys.?[b]) catch unreachable) == .lt;
}
fn legacyPreferred(cat: c.Compiled, ranks: []const u32, a: u32, b: u32) bool {
    if (ranks[a] != ranks[b]) return ranks[a] < ranks[b];
    const ap = cat.sources.priorities.?[cat.bindings.source_indices.?[a]];
    const bp = cat.sources.priorities.?[cat.bindings.source_indices.?[b]];
    return if (ap != bp) ap < bp else cat.catalog.rules.metric_ids.?[a] < cat.catalog.rules.metric_ids.?[b];
}
const Strings = struct {
    bytes: []u8,
    used: u32 = 0,
    failure: *p.Failure,
    fn put(s: *Strings, value: []const u8) c.Error!c.Ref {
        if (value.len > s.bytes.len - s.used) {
            s.failure.* = .{ .code = .payload_capacity, .family = .payload, .required = @as(u64, s.used) + value.len, .limit = s.bytes.len };
            return error.Payload;
        }
        const r = c.Ref{ .offset = s.used, .length = @intCast(value.len) };
        @memcpy(s.bytes[s.used..][0..value.len], value);
        s.used += @intCast(value.len);
        return r;
    }
    fn get(s: Strings, r: c.Ref) []const u8 {
        return s.bytes[r.offset..][0..r.length];
    }
};
fn legacy(cat: c.Compiled, row: u32, buffer: *[64]u8) c.Error![]const u8 {
    const declared = try c.get(cat.text, cat.bindings.legacy.?[row]);
    if (declared.len != 0) return declared;
    const src = source(cat, row);
    const code = cat.bindings.field_codes.?[row];
    const index = cat.bindings.display_indices.?[row];
    if (index != p.none and ((c.eql(src, "amd.smu") and code == 60) or (c.eql(src, "intel.igcl") and (code == 229 or code == 251 or code == 233)))) {
        return std.fmt.bufPrint(buffer, "gpu.{d}.{s}", .{ index, if (code == 60 or code == 229) "temperature" else if (code == 251) "power" else "coreVoltage" }) catch return error.Invalid;
    }
    if (code == 147 and c.eql(src, "intel.msr")) {
        const si = cat.bindings.scope_indices.?[row];
        if (si != p.none and cat.scopes.expansions.?[si] == 11) {
            var first = true;
            var i: u32 = 0;
            while (i < cat.scopes.count) : (i += 1) if (cat.scopes.expansions.?[i] == 11 and cat.scopes.display_indices.?[i] < index) {
                first = false;
            };
            if (first) return "cpu.temperature";
        }
    }
    return "";
}
pub fn select(cat: c.Compiled, facts: ReadingFacts, request: Request, work: []align(8) u8, failure: *p.Failure) c.Error!Selection {
    failure.* = .{};
    _ = try c.get(request.text, .{});
    const size = try required(cat);
    if (work.len < size) return c.fail(failure, .workspace_unavailable, .work, size, work.len);
    if (!c.disjointColumns(p.RuleColumns, cat.catalog.rules, cat.catalog.rules.count, work) or !c.disjointColumns(p.MetricColumns, cat.catalog.metrics, cat.catalog.metrics.count, work) or !c.disjointColumns(c.Bindings, cat.bindings, cat.bindings.count, work) or !c.disjointColumns(c.Sources, cat.sources, cat.sources.count, work) or !c.disjointColumns(c.Scopes, cat.scopes, cat.scopes.count, work) or !c.disjointColumns(ReadingFacts, facts, facts.count, work) or c.overlap(@intFromPtr(work.ptr), work.len, @intFromPtr(cat.text.bytes), cat.text.length) or c.overlap(@intFromPtr(work.ptr), work.len, @intFromPtr(cat.fact_text.bytes), cat.fact_text.length) or c.overlap(@intFromPtr(work.ptr), work.len, @intFromPtr(facts.text.bytes), facts.text.length) or c.overlap(@intFromPtr(work.ptr), work.len, @intFromPtr(request.text.bytes), request.text.length) or c.overlap(@intFromPtr(work.ptr), work.len, @intFromPtr(request.keys), @as(usize, request.count) * @sizeOf(c.Ref))) return error.Invalid;
    const n = cat.bindings.count;
    if (facts.reserved != 0 or facts.count != n or request.flags & ~@as(u32, 7) != 0 or (n != 0 and (facts.epochs == null or facts.availability == null or facts.quantities == null)) or (request.count != 0 and request.keys == null)) return error.Invalid;
    const integrated = try c.get(facts.text, facts.integrated_gpu);
    if (integrated.len != 0 and !c.exact(integrated)) return error.Invalid;
    var i: u32 = 0;
    while (i < request.count) : (i += 1) if (!c.exact(try c.get(request.text, request.keys.?[i]))) return error.Invalid;
    i = 0;
    while (i < n) : (i += 1) {
        if (facts.availability.?[i] > 2) return error.Invalid;
        const q = try c.get(facts.text, facts.quantities.?[i]);
        if (q.len > 4096) return error.Invalid;
    }
    var fba = std.heap.FixedBufferAllocator.init(work);
    const a = fba.allocator();
    const selected = a.alloc(bool, cat.catalog.metrics.count) catch return error.Work;
    @memset(selected, request.flags & 1 != 0);
    const metric_indices = a.alloc(u32, cat.catalog.metrics.count) catch return error.Work;
    const metric_ids = a.alloc(u64, cat.catalog.metrics.count) catch return error.Work;
    const rule_indices = a.alloc(u32, n) catch return error.Work;
    const offsets = a.alloc(u32, cat.sources.count + 1) catch return error.Work;
    const choices = a.alloc(u32, n) catch return error.Work;
    const availability = a.alloc(u32, n) catch return error.Work;
    const quantities = a.alloc(c.Ref, n) catch return error.Work;
    const ids = a.alloc(c.Ref, n) catch return error.Work;
    const keys = a.alloc(c.Ref, n) catch return error.Work;
    const legacy_rules = a.alloc(u32, n) catch return error.Work;
    const ranks = a.alloc(u32, n) catch return error.Work;
    const aliases = a.alloc(u32, n) catch return error.Work;
    var strings = Strings{ .bytes = work[fba.end_index..], .failure = failure };
    i = 0;
    while (i < n) : (i += 1) {
        const src = cat.bindings.source_indices.?[i];
        availability[i] = if (cat.sources.availability.?[src] == 3) 2 else if (facts.epochs.?[i] == cat.sources.capabilities.?[src]) facts.availability.?[i] else 0;
    }
    // Exact presentation aliases share support only, never numeric values.
    i = 0;
    while (i < n) : (i += 1) if (availability[i] == 1) {
        var j: u32 = 0;
        while (j < n) : (j += 1) if (supportAliases(cat, facts, i, j)) {
            availability[j] = 1;
        };
    };
    i = 0;
    while (i < n) : (i += 1) {
        const rule = try reading(cat, facts, i, availability[i]);
        quantities[i] = try strings.put(rule.quantity);
        ranks[i] = rule.source_rank;
        aliases[i] = rule.alias_rank;
        var sha = std.crypto.hash.sha2.Sha256.init(.{});
        sha.update(try device(cat, facts, i));
        sha.update(&.{0});
        sha.update(rule.quantity);
        var digest: [32]u8 = undefined; // SHA256 output, fixed 32 bytes
        sha.final(&digest);
        var id: [72]u8 = undefined; // "reading-" + uppercase SHA256 hex
        @memcpy(id[0..8], "reading-");
        const hex = "0123456789ABCDEF";
        for (digest, 0..) |byte, at| {
            id[8 + at * 2] = hex[byte >> 4];
            id[9 + at * 2] = hex[byte & 15];
        }
        ids[i] = try strings.put(&id);
        var address_buffer: [64]u8 = undefined; // largest generated legacy alias <64 bytes
        keys[i] = try strings.put(try legacy(cat, i, &address_buffer));
    }
    i = 0;
    while (i < n) : (i += 1) {
        var best: u32 = p.none;
        var j: u32 = 0;
        while (j < n) : (j += 1) if (availability[j] == 1 and c.eql(strings.get(ids[i]), strings.get(ids[j])) and (best == p.none or preferred(cat, ranks, aliases, j, best))) {
            best = j;
        };
        choices[i] = best;
    }
    var legacy_count: u32 = 0;
    i = 0;
    while (i < n) : (i += 1) {
        const key = strings.get(keys[i]);
        if (key.len == 0) continue;
        var found: ?u32 = null;
        var j: u32 = 0;
        while (j < legacy_count) : (j += 1) if (c.ci(strings.get(keys[j]), key)) {
            found = j;
            break;
        };
        if (found) |at| {
            const best = choices[i];
            if (best != p.none and (legacy_rules[at] == p.none or legacyPreferred(cat, ranks, best, legacy_rules[at]))) legacy_rules[at] = best;
        } else {
            keys[legacy_count] = keys[i];
            legacy_rules[legacy_count] = choices[i];
            legacy_count += 1;
        }
    }
    // canonical requested fields and legacy preferred source + unverified probes
    i = 0;
    while (i < n) : (i += 1) {
        const code = cat.bindings.field_codes.?[i];
        const mi = cat.bindings.metric_indices.?[i];
        if (request.flags & 2 != 0 and ((cat.bindings.legacy.?[i].length != 0 and (code == 50 or code == 53 or code == 55)) or code == 295)) selected[mi] = true;
        var k: u32 = 0;
        while (k < request.count) : (k += 1) {
            const key = try c.get(request.text, request.keys.?[k]);
            if (c.ci(key, try c.get(cat.text, cat.bindings.addresses.?[i]))) selected[mi] = true;
            var buffer: [64]u8 = undefined;
            if (c.ci(key, try legacy(cat, i, &buffer))) {
                if (availability[i] == 0) selected[mi] = true;
                var j: u32 = 0;
                while (j < n) : (j += 1) if (availability[j] == 0 and c.eql(strings.get(ids[i]), strings.get(ids[j]))) {
                    selected[cat.bindings.metric_indices.?[j]] = true;
                };
                j = 0;
                while (j < legacy_count) : (j += 1) if (c.ci(key, strings.get(keys[j])) and legacy_rules[j] != p.none) {
                    selected[cat.bindings.metric_indices.?[legacy_rules[j]]] = true;
                };
            }
        }
    }
    // Dependency expansion is bounded by the finite catalog, no recursive walk.
    i = 0;
    while (i < n) : (i += 1) if (selected[cat.bindings.metric_indices.?[i]]) {
        const support: u32 = switch (cat.bindings.field_codes.?[i]) {
            21 => 23,
            25 => 27,
            53 => 54,
            else => 0,
        };
        if (support != 0) {
            var j: u32 = 0;
            while (j < n) : (j += 1) if (cat.bindings.scope_ids.?[j] == cat.bindings.scope_ids.?[i] and cat.bindings.field_codes.?[j] == support) {
                selected[cat.bindings.metric_indices.?[j]] = true;
            };
        }
    };
    var out = Selection{ .metric_indices = metric_indices.ptr, .metric_ids = metric_ids.ptr, .rule_indices = rule_indices.ptr, .source_offsets = offsets.ptr, .preferred_rules = choices.ptr, .availability = availability.ptr, .source_ranks = ranks.ptr, .alias_ranks = aliases.ptr, .quantities = quantities.ptr, .reading_ids = ids.ptr, .legacy_keys = keys.ptr, .legacy_rules = legacy_rules.ptr, .legacy_count = legacy_count };
    i = 0;
    while (i < selected.len) : (i += 1) if (selected[i]) {
        metric_indices[out.metric_count] = i;
        metric_ids[out.metric_count] = cat.catalog.metrics.ids.?[i];
        out.metric_count += 1;
    };
    var src: u32 = 0;
    while (src < cat.sources.count) : (src += 1) {
        offsets[src] = out.rule_count;
        i = 0;
        while (i < n) : (i += 1) if (cat.bindings.source_indices.?[i] == src and selected[cat.bindings.metric_indices.?[i]]) {
            rule_indices[out.rule_count] = i;
            out.rule_count += 1;
        };
    }
    offsets[cat.sources.count] = out.rule_count;
    out.dataset_mask = if (request.flags & 1 != 0) 0x1ff else if (request.flags & 4 != 0) 1 else 0;
    i = 0;
    while (i < request.count) : (i += 1) out.dataset_mask |= dataset(try c.get(request.text, request.keys.?[i]));
    out.text = .{ .bytes = strings.bytes.ptr, .length = strings.used };
    return out;
}
fn supportAliases(cat: c.Compiled, facts: ReadingFacts, a: u32, b: u32) bool {
    if (cat.bindings.source_indices.?[a] != cat.bindings.source_indices.?[b]) return false;
    const ac = cat.bindings.field_codes.?[a];
    const bc = cat.bindings.field_codes.?[b];
    const src = source(cat, a);
    if (c.eql(src, "amd.adlx") and (ac == 53 or ac == 228) and (bc == 53 or bc == 228) and cat.bindings.scope_ids.?[a] == cat.bindings.scope_ids.?[b]) return true;
    if (facts.integrated_gpu.length != 0 and c.eql(src, "amd.smu") and (ac == 17 or ac == 60) and (bc == 17 or bc == 60)) {
        const target = c.get(facts.text, facts.integrated_gpu) catch return false;
        return (ac == 17 or c.eql(c.get(cat.text, cat.bindings.device_keys.?[a]) catch return false, target)) and (bc == 17 or c.eql(c.get(cat.text, cat.bindings.device_keys.?[b]) catch return false, target));
    }
    return false;
}
fn dataset(key: []const u8) u32 {
    const names = [_][]const u8{ "system.metric.catalog", "system.gpu.inventory", "storage.inventory", "fan.inventory", "battery.inventory", "numa.inventory", "cpu.usage", "memory.usage", "virtualMemory.usage" };
    for (names, 0..) |name, i| if (c.ci(key, name)) return @as(u32, 1) << @intCast(i);
    return 0;
}
