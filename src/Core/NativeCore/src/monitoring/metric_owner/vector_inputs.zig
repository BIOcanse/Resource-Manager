//! Borrowed vector operands and native projection/derived rules. No creator,
//! allocator-owned memory, callbacks, current-value mirror or sampler loop.
const std = @import("std");
const field_codes = @import("field_codes.zig");
const p = @import("protocol.zig");
const st = @import("state.zig");
const ss = @import("rm_shared_section");
const old = @import("../metric_snapshot/protocol.zig");
pub const Ref = extern struct { offset: u32 = 0, length: u32 = 0 };
pub const Text = extern struct { bytes: ?[*]const u8 = null, length: u32 = 0, reserved: u32 = 0 };
pub const Bindings = extern struct {
    count: u32 = 0,
    reserved: u32 = 0,
    catalog_generation: u64 = 0,
    field_codes: ?[*]const u32 = null,
    scope_indices: ?[*]const u32 = null,
    metric_indices: ?[*]const u32 = null,
    scope_ids: ?[*]const u64 = null,
    addresses: ?[*]const Ref = null,
    device_keys: ?[*]const Ref = null,
    text: Text = .{},
};
pub const ScopeTopology = extern struct {
    count: u32 = 0,
    reserved: u32 = 0,
    text: Text = .{},
    keys: ?[*]const Ref = null,
    parents: ?[*]const Ref = null,
    pnp: ?[*]const Ref = null,
    pnp_drivers: ?[*]const Ref = null,
    directory_low: ?[*]const u64 = null,
    directory_high: ?[*]const u64 = null,
    luids: ?[*]const u64 = null,
    drivers: ?[*]const u64 = null,
    generations: ?[*]const u64 = null,
    vendors: ?[*]const u32 = null,
    devices: ?[*]const u32 = null,
    engine_indices: ?[*]const u32 = null,
    engine_counts: ?[*]const u32 = null,
    driver_present: ?[*]const u32 = null,
    flags: ?[*]const u32 = null,
    expansions: ?[*]const u32 = null,
};
pub const Request = extern struct { flags: u32 = 0, count: u32 = 0, text: Text = .{}, addresses: ?[*]const Ref = null };
pub const NpuPlan = extern struct {
    base: ?[*]u8 = null,
    length: u64 = 0,
    catalog_generation: u64 = 0,
    token: u64 = 0,
    source_id: u64 = 0,
    target_count: u32 = 0,
    engine_count: u32 = 0,
    metric_count: u32 = 0,
    original_count: u32 = 0,
    scope_count: u32 = 0,
    reserved: u32 = 0,
    targets: ?[*]u32 = null,
    offsets: ?[*]u32 = null,
    engines: ?[*]u32 = null,
    metrics: ?[*]u32 = null,
    originals: ?[*]u32 = null,
    complete: ?[*]u32 = null,
    sample_generations: ?[*]u64 = null,
    target_observations: ?[*]u32 = null,
    target_ids: ?[*]u64 = null,
    target_identities: ?[*]u64 = null,
    engine_ids: ?[*]u64 = null,
    engine_identities: ?[*]u64 = null,
    engine_scopes: ?[*]u32 = null,
    expected_generation: u64 = 0,
};
pub const BoundVector = extern struct {
    region: ?[*]u8 = null,
    byte_length: u64 = 0,
    source_id: u64 = 0,
    rows_source: u32 = p.none,
    value_source: u32 = p.none,
    health_source: u32 = p.none,
    reserved: u32 = 0,
    positions: [21]u32 = @splat(p.none),
};
pub const GpuInput = extern struct {
    count: u32 = 0,
    reserved: u32 = 0,
    source_id: u64 = 0,
    incarnation: u64 = 0,
    capability: u64 = 0,
    observation: u64 = 0,
    observed_ms: i64 = 0,
    status: u32 = 1,
    reason: u32 = 0,
    valid_mask: u64 = 0,
    luids: ?[*]const u64 = null,
    physical: ?[*]const u32 = null,
    engines: ?[*]const u32 = null,
    classes: ?[*]const u32 = null,
    usage: ?[*]const f64 = null,
};
pub const SensorInput = extern struct {
    source_id: u64 = 0,
    observed_ms: i64 = 0,
    status: u32 = 1,
    reason: u32 = 0,
    cpu_rpm: f64 = 0,
    cpu_percent: f64 = 0,
    cpu_valid: u32 = 0,
    count: u32 = 0,
    indices: ?[*]const u32 = null,
    rpm: ?[*]const f64 = null,
    percent: ?[*]const f64 = null,
    valid: ?[*]const u32 = null,
};
pub const Selection = extern struct { count: u32 = 0, reserved: u32 = 0, metric_indices: ?[*]u32 = null };
pub const Error = error{ Invalid, Stale, Capacity, Work, Shared };
fn code(err: Error) p.Code {
    return switch (err) {
        error.Stale => .stale_identity,
        error.Capacity => .capacity_exceeded,
        error.Work => .workspace_unavailable,
        error.Shared => .shared_section,
        else => .invalid_contract,
    };
}
fn finish(o: ?*st.Owner, err: ?Error, f: ?*p.Failure) u32 {
    if (err) |e| {
        if (o) |owner| owner.detach();
        if (f) |out| out.* = if (o) |owner| if (owner.failure.code == code(e)) owner.failure else .{ .code = code(e) } else .{ .code = code(e) };
        return @backingInt(code(e));
    }
    if (f) |out| out.* = .{};
    return 0;
}
fn ownerOf(pointer: ?*st.Owner) ?*st.Owner {
    const o = pointer orelse return null;
    if (o.magic != st.Owner.live_magic) return null;
    o.failure = .{};
    return o;
}
fn span(pointer: ?[*]u8, length: u64) Error![]align(8) u8 {
    const ptr = pointer orelse return error.Invalid;
    if (@intFromPtr(ptr) % 8 != 0 or length > std.math.maxInt(usize) - @intFromPtr(ptr)) return error.Invalid;
    return @alignCast(ptr[0..@intCast(length)]);
}
fn text(t: Text, r: Ref) Error![]const u8 {
    if (t.reserved != 0 or (t.length != 0 and t.bytes == null)) return error.Invalid;
    if (r.offset == p.none) {
        if (r.length != 0) return error.Invalid;
        return &.{};
    }
    if (r.offset > t.length or r.length > t.length - r.offset) return error.Invalid;
    if (r.length == 0) return &.{};
    return t.bytes.?[r.offset..][0..r.length];
}
fn same(t: Text, a: Ref, b: Ref) Error!bool {
    return a.offset == p.none and b.offset == p.none or (a.offset != p.none and b.offset != p.none and std.mem.eql(u8, try text(t, a), try text(t, b)));
}
fn pointers(comptime T: type, v: T, count: u32) bool {
    if (count == 0) return true;
    inline for (@typeInfo(T).@"struct".field_names, @typeInfo(T).@"struct".field_types) |field_name, field_type| {
        if (@typeInfo(field_type) == .optional and @field(v, field_name) == null) return false;
    }
    return true;
}
fn facts(o: *st.Owner, b: Bindings, t: ScopeTopology) Error!void {
    if (@as(u64, t.count) * 8 > o.config.work_limit) {
        _ = o.fail(.capacity_exceeded, .work, @as(u64, t.count) * 8, o.config.work_limit);
        return error.Capacity;
    }
    return factsReadOnly(o, b, t);
}
fn factsReadOnly(o: *const st.Owner, b: Bindings, t: ScopeTopology) Error!void {
    if (@as(u64, t.count) * 8 > o.config.work_limit) return error.Capacity;
    if (b.reserved != 0 or t.reserved != 0 or b.catalog_generation != o.catalog_generation or b.count != o.rule_count or !pointers(Bindings, b, b.count) or !pointers(ScopeTopology, t, t.count)) return error.Invalid;
    _ = try text(b.text, .{});
    _ = try text(t.text, .{});
    for (o.rules[0..o.rule_count], 0..) |r, i| {
        if (b.metric_indices.?[i] != r.metric) return error.Stale;
        _ = try text(b.text, b.addresses.?[i]);
        _ = try text(b.text, b.device_keys.?[i]);
        const si = b.scope_indices.?[i];
        if (si != p.none and si >= t.count) return error.Invalid;
    }
    var i: u32 = 0;
    while (i < t.count) : (i += 1) {
        _ = try text(t.text, t.keys.?[i]);
        _ = try text(t.text, t.parents.?[i]);
        _ = try text(t.text, t.pnp.?[i]);
        _ = try text(t.text, t.pnp_drivers.?[i]);
        if (t.driver_present.?[i] > 1) return error.Invalid;
    }
}
fn work(o: *st.Owner, bytes: []align(8) u8) Error!void {
    if (bytes.len > o.config.work_limit) {
        _ = o.fail(.capacity_exceeded, .work, bytes.len, o.config.work_limit);
        return error.Capacity;
    }
    return workReadOnly(o, bytes);
}
fn workReadOnly(o: *const st.Owner, bytes: []align(8) u8) Error!void {
    if (bytes.len > o.config.work_limit) return error.Capacity;
    if (st.overlaps(@intFromPtr(bytes.ptr), bytes.len, @intFromPtr(o), @intCast(o.active_length))) return error.Invalid;
    for (o.bindings) |b| if (st.overlaps(@intFromPtr(bytes.ptr), bytes.len, @intFromPtr(b.table.region.?), @intCast(b.table.byte_length))) return error.Invalid;
    for (o.results[0..o.result_count]) |b| if (st.overlaps(@intFromPtr(bytes.ptr), bytes.len, @intFromPtr(b.region.?), @intCast(b.byte_length))) return error.Invalid;
    if (o.gpu_vector) |b| if (st.overlaps(@intFromPtr(bytes.ptr), bytes.len, @intFromPtr(b.region.?), @intCast(b.byte_length))) return error.Invalid;
}
fn disjointColumns(comptime T: type, value: T, count: u32, bytes: []align(8) u8) Error!void {
    inline for (@typeInfo(T).@"struct".field_names, @typeInfo(T).@"struct".field_types) |field_name, field_type| {
        if (@typeInfo(field_type) == .optional) {
            const Ptr = @typeInfo(field_type).optional.child;
            if (@typeInfo(Ptr) == .pointer) if (@field(value, field_name)) |ptr| {
                const length = std.math.mul(usize, count, @sizeOf(@typeInfo(Ptr).pointer.child)) catch return error.Capacity;
                if (st.overlaps(@intFromPtr(ptr), length, @intFromPtr(bytes.ptr), bytes.len)) return error.Invalid;
            };
        }
    }
}
fn outsideText(t: Text, bytes: []align(8) u8) Error!void {
    if (t.length != 0 and st.overlaps(@intFromPtr(t.bytes orelse return error.Invalid), t.length, @intFromPtr(bytes.ptr), bytes.len)) return error.Invalid;
}
fn immutableOutside(b: Bindings, t: ScopeTopology, bytes: []align(8) u8) Error!void {
    try disjointColumns(Bindings, b, b.count, bytes);
    try disjointColumns(ScopeTopology, t, t.count, bytes);
    try outsideText(b.text, bytes);
    try outsideText(t.text, bytes);
}
fn addSize(comptime T: type, n: u64, at: *u64) Error!void {
    // Allocator.alloc(T, 0) returns a sentinel without padding/advancing FBA.
    if (n == 0) return;
    const a = std.math.add(u64, at.*, @alignOf(T) - 1) catch return error.Capacity;
    at.* = std.math.add(u64, a & ~@as(u64, @alignOf(T) - 1), std.math.mul(u64, n, @sizeOf(T)) catch return error.Capacity) catch return error.Capacity;
}
fn better(a: st.Rule, b: st.Rule) bool {
    return a.priority < b.priority or (a.priority == b.priority and a.id < b.id);
}
fn targetRule(o: *const st.Owner, b: Bindings, metric: u32) ?u32 {
    const r = selectedRule(o, metric) orelse return null;
    return if (b.field_codes.?[r] == field_codes.npu_device_engine_busy_percent) r else null;
}
fn selectedRule(o: *const st.Owner, metric: u32) ?u32 {
    var chosen: ?u32 = null;
    var fallback: ?u32 = null;
    var ri = o.metrics[metric].first_rule;
    var n: u32 = 0;
    while (ri != p.none and n < o.rule_count) : (n += 1) {
        const r = o.rules[ri];
        if (fallback == null or better(r, o.rules[fallback.?])) fallback = ri;
        if (!r.suppressed and !o.sources[r.source].suppressed and o.sources[r.source].availability == 1 and (chosen == null or better(r, o.rules[chosen.?]))) chosen = ri;
        ri = o.rules[ri].next_metric;
    }
    return chosen orelse fallback;
}
fn engineFor(o: *const st.Owner, b: Bindings, t: ScopeTopology, target: u32, ri: u32, source: u64) Error!bool {
    if (b.field_codes.?[ri] != field_codes.npu_engine_reported_running_time_ratio_percent or o.sources[o.rules[ri].source].id != source) return false;
    if (selectedRule(o, o.rules[ri].metric) != ri) return false;
    const si = b.scope_indices.?[ri];
    const ts = b.scope_indices.?[target];
    if (si == p.none or ts == p.none) return error.Invalid;
    return std.mem.eql(u8, try text(t.text, t.parents.?[si]), try text(t.text, t.keys.?[ts]));
}
fn identitySame(t: ScopeTopology, a: u32, b: u32) Error!bool {
    return try same(t.text, t.parents.?[a], t.parents.?[b]) and try same(t.text, t.pnp.?[a], t.pnp.?[b]) and try same(t.text, t.pnp_drivers.?[a], t.pnp_drivers.?[b]) and
        t.directory_low.?[a] == t.directory_low.?[b] and t.directory_high.?[a] == t.directory_high.?[b] and t.luids.?[a] == t.luids.?[b] and t.vendors.?[a] == t.vendors.?[b] and t.devices.?[a] == t.devices.?[b] and t.engine_counts.?[a] == t.engine_counts.?[b] and t.driver_present.?[a] == t.driver_present.?[b] and (t.driver_present.?[a] == 0 or t.drivers.?[a] == t.drivers.?[b]);
}
const NpuCounts = struct { targets: u32 = 0, edges: u32 = 0 };
fn npuCounts(o: *st.Owner, b: Bindings, t: ScopeTopology, requests: []const u32, source: u64) Error!NpuCounts {
    try facts(o, b, t);
    if (requests.len > o.metric_count or source == 0) return error.Invalid;
    var out: NpuCounts = .{};
    for (requests, 0..) |mi, i| {
        if (mi >= o.metric_count) return error.Invalid;
        for (requests[0..i]) |prior| if (prior == mi) return error.Invalid;
        const tr = targetRule(o, b, mi) orelse continue;
        const target = o.rules[tr];
        const ts = b.scope_indices.?[tr];
        if (ts == p.none or t.expansions.?[ts] != 16 or target.value_kind != 1 or old.isCounterMetric(target.kind) or o.sources[target.source].id != source) return error.Invalid;
        if (b.scope_ids.?[tr] == 0 or !std.mem.eql(u8, try text(b.text, b.device_keys.?[tr]), try text(t.text, t.keys.?[ts]))) return error.Stale;
        out.targets += 1;
        var ri: u32 = 0;
        while (ri < o.rule_count) : (ri += 1) if (try engineFor(o, b, t, tr, ri, source)) {
            const r = o.rules[ri];
            const si = b.scope_indices.?[ri];
            if (r.value_kind != 1 or !old.isTimedCounterMetric(r.kind) or t.expansions.?[si] != 17 or !std.mem.eql(u8, try text(b.text, b.device_keys.?[ri]), try text(t.text, t.keys.?[si])) or o.sources[r.source].access_region > o.metrics[target.metric].result_group) return error.Invalid;
            out.edges = std.math.add(u32, out.edges, 1) catch return error.Capacity;
        };
    }
    return out;
}
fn npuSize(o: *st.Owner, b: Bindings, t: ScopeTopology, requests: []const u32, source: u64) Error!u64 {
    const n = try npuCounts(o, b, t, requests, source);
    const size = try npuLayoutSize(n.targets, n.edges, o.metric_count, requests.len, t.count);
    if (size > o.config.work_limit) {
        _ = o.fail(.capacity_exceeded, .work, size, o.config.work_limit);
        return error.Capacity;
    }
    return size;
}
pub fn npuExpand(o: *st.Owner, b: Bindings, t: ScopeTopology, requests: []const u32, source: u64, bytes: []align(8) u8) Error!NpuPlan {
    try work(o, bytes);
    try immutableOutside(b, t, bytes);
    if (st.overlaps(@intFromPtr(requests.ptr), requests.len * 4, @intFromPtr(bytes.ptr), bytes.len)) return error.Invalid;
    const size = try npuSize(o, b, t, requests, source);
    if (bytes.len < size) {
        _ = o.fail(.workspace_unavailable, .work, size, bytes.len);
        return error.Work;
    }
    const n = try npuCounts(o, b, t, requests, source);
    var fba = std.heap.FixedBufferAllocator.init(bytes);
    const a = fba.allocator();
    var out = NpuPlan{ .base = bytes.ptr, .length = bytes.len, .catalog_generation = o.catalog_generation, .source_id = source, .target_count = n.targets, .engine_count = n.edges, .original_count = @intCast(requests.len), .scope_count = t.count };
    out.targets = (a.alloc(u32, n.targets) catch unreachable).ptr;
    out.offsets = (a.alloc(u32, @as(usize, n.targets) + 1) catch unreachable).ptr;
    out.engines = (a.alloc(u32, n.edges) catch unreachable).ptr;
    out.metrics = (a.alloc(u32, o.metric_count) catch unreachable).ptr;
    out.originals = (a.alloc(u32, requests.len) catch unreachable).ptr;
    out.complete = (a.alloc(u32, n.targets) catch unreachable).ptr;
    out.sample_generations = (a.alloc(u64, t.count) catch unreachable).ptr;
    out.target_observations = (a.alloc(u32, n.targets) catch unreachable).ptr;
    out.target_ids = (a.alloc(u64, n.targets) catch unreachable).ptr;
    out.target_identities = (a.alloc(u64, n.targets) catch unreachable).ptr;
    out.engine_ids = (a.alloc(u64, n.edges) catch unreachable).ptr;
    out.engine_identities = (a.alloc(u64, n.edges) catch unreachable).ptr;
    out.engine_scopes = (a.alloc(u32, n.edges) catch unreachable).ptr;
    @memcpy(out.originals.?[0..requests.len], requests);
    @memcpy(out.metrics.?[0..requests.len], requests);
    out.metric_count = @intCast(requests.len);
    @memset(out.sample_generations.?[0..t.count], 0);
    @memset(out.target_observations.?[0..n.targets], p.none);
    var ti: u32 = 0;
    var ei: u32 = 0;
    for (requests) |mi| {
        const tr = targetRule(o, b, mi) orelse continue;
        out.targets.?[ti] = tr;
        out.target_ids.?[ti] = o.rules[tr].id;
        out.target_identities.?[ti] = o.rules[tr].identity;
        out.offsets.?[ti] = ei;
        var ri: u32 = 0;
        while (ri < o.rule_count) : (ri += 1) if (try engineFor(o, b, t, tr, ri, source)) {
            out.engines.?[ei] = ri;
            ei += 1;
            const dep = o.rules[ri].metric;
            var found = false;
            for (out.metrics.?[0..out.metric_count]) |v| {
                if (v == dep) {
                    found = true;
                    break;
                }
            }
            if (!found) {
                out.metrics.?[out.metric_count] = dep;
                out.metric_count += 1;
            }
        };
        const start = out.offsets.?[ti];
        const Sort = struct {
            top: ScopeTopology,
            bindings: Bindings,
            fn less(ctx: @This(), x: u32, y: u32) bool {
                return ctx.top.engine_indices.?[ctx.bindings.scope_indices.?[x]] < ctx.top.engine_indices.?[ctx.bindings.scope_indices.?[y]];
            }
        };
        std.mem.sort(u32, out.engines.?[start..ei], Sort{ .top = t, .bindings = b }, Sort.less);
        var complete = ei > start;
        if (complete) {
            const first = b.scope_indices.?[out.engines.?[start]];
            complete = t.engine_counts.?[first] == ei - start and t.luids.?[first] != 0 and (t.directory_low.?[first] != 0 or t.directory_high.?[first] != 0) and (try text(t.text, t.pnp.?[first])).len != 0;
            for (out.engines.?[start..ei], 0..) |er, j| {
                const si = b.scope_indices.?[er];
                if (t.engine_indices.?[si] != j or !try identitySame(t, first, si)) complete = false;
            }
        }
        out.complete.?[ti] = @intFromBool(complete);
        ti += 1;
    }
    out.offsets.?[ti] = ei;
    for (out.engines.?[0..ei], 0..) |ri, i| {
        out.engine_ids.?[i] = o.rules[ri].id;
        out.engine_identities.?[i] = o.rules[ri].identity;
        out.engine_scopes.?[i] = b.scope_indices.?[ri];
    }
    std.mem.sort(u32, out.metrics.?[0..out.metric_count], {}, std.sort.asc(u32));
    std.mem.sort(u32, out.originals.?[0..out.original_count], {}, std.sort.asc(u32));
    return out;
}
fn inside(comptime T: type, ptr: ?[*]T, n: usize, base: usize, len: usize) bool {
    const x = ptr orelse return n == 0;
    // Allocator.alloc(0) may return an aligned sentinel outside its arena.
    // Empty columns are never dereferenced; nonempty columns must be borrowed.
    if (n == 0) return true;
    const at = @intFromPtr(x);
    const bytes = std.math.mul(usize, n, @sizeOf(T)) catch return false;
    return at % @alignOf(T) == 0 and at >= base and bytes <= len and at - base <= len - bytes;
}
fn planReady(o: *st.Owner, plan: *const NpuPlan) Error!void {
    if (plan.catalog_generation != o.catalog_generation or plan.reserved != 0 or plan.length > o.config.work_limit or plan.length > std.math.maxInt(usize)) return error.Stale;
    if (!pointers(NpuPlan, plan.*, 1)) return error.Invalid;
    const base = @intFromPtr(plan.base orelse return error.Invalid);
    const len: @TypeOf(@as(usize, 0)) = @intCast(plan.length);
    if (plan.target_count > o.rule_count or plan.metric_count > o.metric_count or plan.original_count > o.metric_count) return error.Invalid;
    inline for (.{ "targets", "complete", "target_observations" }) |field| if (!inside(u32, @field(plan.*, field), plan.target_count, base, len)) return error.Invalid;
    if (!inside(u32, plan.offsets, @as(usize, plan.target_count) + 1, base, len) or !inside(u32, plan.engines, plan.engine_count, base, len) or !inside(u32, plan.metrics, plan.metric_count, base, len) or !inside(u32, plan.originals, plan.original_count, base, len) or !inside(u64, plan.sample_generations, plan.scope_count, base, len)) return error.Invalid;
    inline for (.{ "target_ids", "target_identities" }) |field| if (!inside(u64, @field(plan.*, field), plan.target_count, base, len)) return error.Invalid;
    if (!inside(u32, plan.engine_scopes, plan.engine_count, base, len)) return error.Invalid;
    inline for (.{ "engine_ids", "engine_identities" }) |field| if (!inside(u64, @field(plan.*, field), plan.engine_count, base, len)) return error.Invalid;
    for (plan.targets.?[0..plan.target_count], 0..) |ri, i| if (ri >= o.rule_count or plan.target_ids.?[i] != o.rules[ri].id or plan.target_identities.?[i] != o.rules[ri].identity or plan.complete.?[i] > 1 or plan.offsets.?[i] > plan.offsets.?[i + 1] or plan.offsets.?[i + 1] > plan.engine_count) return error.Stale;
    for (plan.engines.?[0..plan.engine_count], 0..) |ri, i| if (ri >= o.rule_count or plan.engine_ids.?[i] != o.rules[ri].id or plan.engine_identities.?[i] != o.rules[ri].identity) return error.Stale;
    if (plan.offsets.?[0] != 0 or plan.offsets.?[plan.target_count] != plan.engine_count) return error.Invalid;
    for (plan.metrics.?[0..plan.metric_count], 0..) |mi, i| {
        if (mi >= o.metric_count or (i != 0 and mi <= plan.metrics.?[i - 1])) return error.Invalid;
    }
    for (plan.originals.?[0..plan.original_count], 0..) |mi, i| {
        if (mi >= o.metric_count or (i != 0 and mi <= plan.originals.?[i - 1])) return error.Invalid;
        if (std.mem.indexOfScalar(u32, plan.metrics.?[0..plan.metric_count], mi) == null) return error.Invalid;
    }
    for (plan.targets.?[0..plan.target_count], 0..) |ri, i| {
        if (o.sources[o.rules[ri].source].id != plan.source_id or o.rules[ri].value_kind != 1 or std.mem.indexOfScalar(u32, plan.metrics.?[0..plan.metric_count], o.rules[ri].metric) == null) return error.Invalid;
        if (std.mem.indexOfScalar(u32, plan.targets.?[0..i], ri) != null) return error.Invalid;
    }
    for (plan.engines.?[0..plan.engine_count], 0..) |ri, i| {
        if (plan.engine_scopes.?[i] >= plan.scope_count or o.sources[o.rules[ri].source].id != plan.source_id or o.rules[ri].value_kind != 1 or std.mem.indexOfScalar(u32, plan.metrics.?[0..plan.metric_count], o.rules[ri].metric) == null) return error.Invalid;
    }
}
fn observation(o: *const st.Owner, ri: u32) ?u32 {
    for (o.work.expected[0..o.work.raw.count], 0..) |v, i| if (v == ri) return @intCast(i);
    return null;
}
pub fn npuAttach(o: *st.Owner, token: u64, generation: u64, plan: *NpuPlan) Error!void {
    if (o.work.region == null or o.work.prepared or token != o.token or plan.token != 0 or o.work.npu_plan != null) return error.Stale;
    try planReady(o, plan);
    if (generation == 0) for (plan.engines.?[0..plan.engine_count]) |ri| {
        if (observation(o, ri) != null) return error.Invalid;
    };
    if (plan.length > o.config.work_limit - o.work.length) {
        _ = o.fail(.capacity_exceeded, .work, plan.length + o.work.length, o.config.work_limit);
        return error.Capacity;
    }
    const bytes = try span(plan.base, plan.length);
    try work(o, bytes);
    if (st.overlaps(@intFromPtr(bytes.ptr), bytes.len, @intFromPtr(o.work.region.?), o.work.length)) return error.Invalid;
    if (plan.metric_count != o.work.request_count or !std.mem.eql(u32, plan.metrics.?[0..plan.metric_count], o.work.requested[0..o.work.request_count])) return error.Stale;
    for (plan.targets.?[0..plan.target_count], 0..) |ri, i| {
        if (observation(o, ri)) |oi| {
            o.work.synthetic[oi] = true;
            o.work.raw.observations.status.?[oi] = 4;
            plan.target_observations.?[i] = oi;
        }
    }
    @memcpy(o.work.requested[0..plan.original_count], plan.originals.?[0..plan.original_count]);
    o.work.request_count = plan.original_count;
    plan.token = token;
    plan.expected_generation = generation;
    o.work.npu_plan = plan.*;
}
/// Called by round.prepare AFTER all counter evaluation, BEFORE publication.
pub fn derive(o: *st.Owner) p.Code {
    const plan = o.work.npu_plan orelse return .ok;
    planReady(o, &plan) catch return o.fail(.stale_identity, .rules, 0, 0);
    if (plan.token != o.token) return o.fail(.stale_plan, .none, plan.token, o.token);
    for (plan.targets.?[0..plan.target_count], 0..) |ri, ti| {
        const oi = observation(o, ri) orelse continue;
        var complete = plan.complete.?[ti] == 1;
        var max: ?st.Delta = null;
        var generation: u64 = 0;
        for (plan.engines.?[plan.offsets.?[ti]..plan.offsets.?[ti + 1]], plan.offsets.?[ti]..) |er, edge| {
            const ei = observation(o, er) orelse {
                complete = false;
                continue;
            };
            const d = o.work.deltas[ei];
            const scope = plan.engine_scopes.?[edge];
            if (scope >= plan.scope_count or !d.write_value or d.status != 1 or d.presence != 1 or d.kind != 1 or d.observed < 0 or o.work.invalid_sources[o.rules[er].source] or o.work.unsupported_sources[o.rules[er].source]) {
                complete = false;
                continue;
            }
            const value: @TypeOf(@as(f64, 0)) = @bitCast(d.low);
            const g = plan.sample_generations.?[scope];
            if (!std.math.isFinite(value) or value < 0 or g != plan.expected_generation or (generation != 0 and generation != g)) {
                complete = false;
                continue;
            }
            generation = g;
            if (max == null or value > @as(f64, @bitCast(max.?.low))) max = d;
        }
        var d = o.work.deltas[oi];
        d.status = 2;
        d.reason = 0x4e505501;
        d.write_value = false;
        d.baseline = .{};
        if (complete and max != null) {
            const m = max.?;
            const r = o.rules[ri];
            const value: @TypeOf(@as(f64, 0)) = @bitCast(m.low);
            if (old.validValue(1, r.flags, r.minimum, r.maximum, m.low) and std.math.isFinite(value)) {
                const previous = o.work.tables[r.source].load(u64, 5, r.row);
                if (previous == std.math.maxInt(u64)) return o.fail(.capacity_exceeded, .generation, 0, previous);
                d = .{ .low = m.low, .kind = 1, .presence = 1, .status = 1, .generation = previous + 1, .observed = m.observed, .duration = m.duration, .capability_mask = r.capability_mask, .write_value = true };
            }
        }
        o.work.deltas[oi] = d;
    }
    return .ok;
}

pub const GpuPublication = extern struct {
    handle: ?*anyopaque = null,
    token: u64 = 0,
    rows: u32 = 0,
    selected_count: u32 = 0,
    selected_rows: ?[*]u32 = null,
    addresses: ?[*]Ref = null,
    text: Text = .{},
    published: u32 = 0,
    reserved: u32 = 0,
    suppressed_metrics: ?[*]u32 = null,
    suppressed_count: u32 = 0,
    tail_reserved: u32 = 0,
};
const EngineRow = struct {
    identity: u64 = 0,
    luid: u64 = 0,
    physical: u32 = 0,
    engine: u32 = 0,
    class: u32 = 0,
    scope: u32 = 0,
    scope_id: u64 = 0,
    scope_generation: u64 = 0,
    trigger: u64 = 0,
    trigger_rule: u32 = 0,
    selected: bool = false,
    received: bool = false,
    refresh: bool = false,
    write_value: bool = false,
    usage: f64 = 0,
    address: Ref = .{},
    key: Ref = .{},
    old_row: u32 = p.none,
    good_low: u64 = 0,
    good_generation: u64 = 0,
    good_observed: i64 = 0,
    good_presence: u32 = 0,
    good_observation: u64 = 0,
    health: u32 = 2,
    health_reason: u32 = 0,
    attempt: i64 = 0,
    public_status: u32 = 3,
};
pub const GpuPrepared = struct {
    view: GpuPublication = .{},
    base: [*]u8,
    length: usize,
    catalog_generation: u64,
    binding: BoundVector,
    input: GpuInput,
    rows: []EngineRow,
    row_count: u32 = 0,
    text: []u8,
    used: u32 = 0,
    changed_rows: bool = false,
    current: bool = false,
    resume_values: bool = false,
};
const VectorTable = struct {
    binding: BoundVector,
    layout: ss.layout.Layout,
    specs: [21]ss.PositionSpec,
    capacity: u32,
    fn bytes(t: *const @This()) []align(8) u8 {
        return @alignCast(t.binding.region.?[0..@intCast(t.binding.byte_length)]);
    }
    fn init(b: BoundVector) Error!@This() {
        const region_bytes = try span(b.region, b.byte_length);
        const l = ss.layout.Layout.load(region_bytes) catch return error.Shared;
        if (b.reserved != 0 or b.source_id == 0) return error.Invalid;
        const sources = [_]u32{ b.rows_source, b.value_source, b.health_source };
        for (sources, 0..) |src, i| {
            if (src >= l.header.source_count) return error.Invalid;
            for (sources[0..i]) |prior| if (prior == src) return error.Invalid;
            const seq = l.source(region_bytes, src).sequence;
            if (seq & (ss.ended_bit | 1) != 0 or seq > ss.ended_bit - 6) return error.Shared;
        }
        var specs: [21]ss.PositionSpec = undefined;
        var capacity: u32 = std.math.maxInt(u32);
        for (b.positions, 0..) |position, i| {
            const spec = l.checkedSpec(region_bytes, position) catch return error.Invalid;
            const width: u32 = switch (i) {
                2, 3, 4, 6, 7, 10, 11, 17, 20 => 4,
                14 => 1,
                else => 8,
            };
            const rows = i <= 4 or (i >= 13 and i <= 18);
            const health = (i >= 10 and i <= 12) or i == 20;
            if (spec.element_width != width or spec.source != (if (rows) b.rows_source else if (health) b.health_source else b.value_source) or spec.identity != (if (i == 0 or i == 14) p.none else b.positions[0]) or spec.kind != (if (i == 14) ss.PositionKind.string_table else ss.PositionKind.column)) return error.Invalid;
            for (specs[0..i]) |prior| if (spec.size != 0 and prior.size != 0 and @as(u64, spec.offset) < @as(u64, prior.offset) + prior.size and @as(u64, prior.offset) < @as(u64, spec.offset) + spec.size) return error.Invalid;
            specs[i] = spec;
            if (i != 14) capacity = @min(capacity, spec.size / width);
        }
        return .{ .binding = b, .layout = l, .specs = specs, .capacity = capacity };
    }
    fn load(t: *const @This(), comptime T: type, slot: usize, row: u32) T {
        const ptr: *const T = @ptrCast(@alignCast(t.layout.dataArea(t.bytes()).ptr + t.specs[slot].offset + @as(usize, row) * @sizeOf(T)));
        return @atomicLoad(T, ptr, .acquire);
    }
    fn count(t: *const @This()) Error!u32 {
        const n = t.layout.position(t.bytes(), t.binding.positions[0]).length;
        if (n > t.capacity) return error.Invalid;
        const seq = t.layout.source(t.bytes(), t.binding.rows_source).sequence;
        for (t.binding.positions, 0..) |pos, i| {
            const entry = t.layout.position(t.bytes(), pos);
            if (i == 14) {
                if (entry.length > t.specs[14].size) return error.Invalid;
            } else if (entry.length != n or (i != 0 and entry.identity_sequence != seq)) return error.Stale;
        }
        return n;
    }
};
fn sourceIndex(o: *const st.Owner, id: u64) ?u32 {
    for (o.sources[0..o.source_count], 0..) |src, i| if (src.id == id) return @intCast(i);
    return null;
}
fn vectorSeparate(o: *st.Owner, b: BoundVector) Error!void {
    try discoveryValidateGpuBinding(o, b, b.source_id);
    const si = sourceIndex(o, b.source_id) orelse return error.Stale;
    if (o.sources[si].access_region != 0) return error.Invalid;
}
fn different(t: VectorTable, b: p.TableBinding) Error!void {
    if (t.binding.region != b.region) return;
    for ([_]u32{ t.binding.rows_source, t.binding.value_source, t.binding.health_source }) |src| for ([_]u32{ b.rows_source, b.value_source, b.health_source, b.provenance_source }) |other| if (src == other) return error.Invalid;
    for (t.specs) |spec| {
        for (b.positions) |pos| {
            if (pos == p.none) continue;
            const other = t.layout.checkedSpec(t.bytes(), pos) catch return error.Invalid;
            if (spec.size != 0 and other.size != 0 and @as(u64, spec.offset) < @as(u64, other.offset) + other.size and @as(u64, other.offset) < @as(u64, spec.offset) + spec.size) return error.Invalid;
        }
        const other = t.layout.checkedSpec(t.bytes(), b.provenance_position) catch return error.Invalid;
        if (spec.size != 0 and other.size != 0 and @as(u64, spec.offset) < @as(u64, other.offset) + other.size and @as(u64, other.offset) < @as(u64, spec.offset) + spec.size) return error.Invalid;
    }
}
fn gpuSize(o: *st.Owner, raw_count: u32) Error!u64 {
    const b = o.gpu_vector orelse return error.Invalid;
    const t = try VectorTable.init(b);
    const count = std.math.add(u64, raw_count, try t.count()) catch return error.Capacity;
    const size = try gpuLayoutSize(count, o.metric_count);
    if (size > o.config.work_limit) {
        _ = o.fail(.capacity_exceeded, .work, size, o.config.work_limit);
        return error.Capacity;
    }
    return size;
}
fn rowLess(_: void, a: EngineRow, b: EngineRow) bool {
    if (a.luid != b.luid) return a.luid < b.luid;
    if (a.physical != b.physical) return a.physical < b.physical;
    if (a.engine != b.engine) return a.engine < b.engine;
    return a.class < b.class;
}
fn rowEqual(a: EngineRow, b: EngineRow) bool {
    return a.luid == b.luid and a.physical == b.physical and a.engine == b.engine and a.class == b.class;
}
fn append(plan: *GpuPrepared, value: []const u8) Error!Ref {
    if (value.len > plan.text.len - plan.used) return error.Capacity;
    const r = Ref{ .offset = plan.used, .length = @intCast(value.len) };
    @memcpy(plan.text[plan.used..][0..value.len], value);
    plan.used += @intCast(value.len);
    return r;
}
fn decimal(plan: *GpuPrepared, value: u32) Error!void {
    var buffer: [10]u8 = undefined;
    const written = std.fmt.bufPrint(&buffer, "{d}", .{value}) catch unreachable;
    _ = try append(plan, written);
}
fn luidHex(plan: *GpuPrepared, value: u64) Error!void {
    var buffer: [16]u8 = undefined;
    _ = try append(plan, std.fmt.bufPrint(&buffer, "{X:0>16}", .{value}) catch unreachable);
}
fn names(plan: *GpuPrepared, row: *EngineRow) Error!void {
    var start = plan.used;
    _ = try append(plan, "gpu-engine/");
    try luidHex(plan, row.luid);
    _ = try append(plan, "/");
    try decimal(plan, row.physical);
    _ = try append(plan, "/");
    try decimal(plan, row.engine);
    _ = try append(plan, "/");
    try decimal(plan, row.class);
    _ = try append(plan, "/busyPercent");
    row.address = .{ .offset = start, .length = plan.used - start };
    start = plan.used;
    _ = try append(plan, "gpu-engine|");
    try luidHex(plan, row.luid);
    _ = try append(plan, "|");
    try decimal(plan, row.physical);
    _ = try append(plan, "|");
    try decimal(plan, row.engine);
    _ = try append(plan, "|");
    try decimal(plan, row.class);
    row.key = .{ .offset = start, .length = plan.used - start };
}
fn requestValid(r: Request) Error!void {
    if (r.flags & ~@as(u32, 3) != 0 or (r.count != 0 and r.addresses == null)) return error.Invalid;
    var i: u32 = 0;
    while (i < r.count) : (i += 1) {
        _ = try text(r.text, r.addresses.?[i]);
    }
}
fn requested(r: Request, address: []const u8, trigger: []const u8) Error!bool {
    if (r.flags & 3 != 0) return true;
    var i: u32 = 0;
    while (i < r.count) : (i += 1) {
        const s = try text(r.text, r.addresses.?[i]);
        if (std.ascii.eqlIgnoreCase(s, address) or std.ascii.eqlIgnoreCase(s, trigger)) return true;
    }
    return false;
}
fn gpuTrigger(o: *const st.Owner, b: Bindings, t: ScopeTopology, luid: u64, source: u64) Error!?u32 {
    if (luid == 0) return null;
    var found: ?u32 = null;
    var i: u32 = 0;
    while (i < b.count) : (i += 1) {
        const si = b.scope_indices.?[i];
        if (b.field_codes.?[i] != field_codes.gpu_engine_busy or si == p.none or t.expansions.?[si] != 4 or t.luids.?[si] != luid or o.sources[o.rules[i].source].id != source) continue;
        if (selectedRule(o, o.rules[i].metric) != i) continue;
        if (!std.mem.eql(u8, try text(b.text, b.device_keys.?[i]), try text(t.text, t.keys.?[si])) or b.scope_ids.?[i] == 0) return error.Stale;
        if (found) |prior| {
            if (b.scope_indices.?[prior] != si or b.scope_ids.?[prior] != b.scope_ids.?[i]) return error.Stale;
            if (better(o.rules[i], o.rules[prior])) found = i;
        } else found = i;
    }
    return found;
}
fn requestLuid(r: Request, luid: u64, trigger: []const u8) Error!bool {
    if (r.flags & 3 != 0) return true;
    var buffer: [27]u8 = undefined;
    const prefix = std.fmt.bufPrint(&buffer, "gpu-engine/{X:0>16}", .{luid}) catch unreachable;
    var i: u32 = 0;
    while (i < r.count) : (i += 1) {
        const s = try text(r.text, r.addresses.?[i]);
        if (std.ascii.eqlIgnoreCase(s, trigger) or (s.len > prefix.len and s[prefix.len] == '/' and std.ascii.eqlIgnoreCase(s[0..prefix.len], prefix))) return true;
    }
    return false;
}
pub fn gpuExpand(o: *st.Owner, b: Bindings, t: ScopeTopology, r: Request, initial: []const u32, bytes: []align(8) u8) Error!Selection {
    try facts(o, b, t);
    try requestValid(r);
    try work(o, bytes);
    try immutableOutside(b, t, bytes);
    try outsideText(r.text, bytes);
    try disjointColumns(Request, r, r.count, bytes);
    if (st.overlaps(@intFromPtr(initial.ptr), initial.len * 4, @intFromPtr(bytes.ptr), bytes.len)) return error.Invalid;
    const required = @as(u64, o.metric_count) * 4;
    if (bytes.len < required) {
        _ = o.fail(.workspace_unavailable, .work, required, bytes.len);
        return error.Work;
    }
    const indices: [*]u32 = @ptrCast(bytes.ptr);
    var count: u32 = 0;
    for (initial) |mi| {
        if (mi >= o.metric_count) return error.Invalid;
        var found = false;
        for (indices[0..count]) |v| {
            if (v == mi) found = true;
        }
        if (!found) {
            indices[count] = mi;
            count += 1;
        }
    }
    var ri: u32 = 0;
    while (ri < o.rule_count) : (ri += 1) {
        const si = b.scope_indices.?[ri];
        if (b.field_codes.?[ri] != field_codes.gpu_engine_busy or si == p.none or t.expansions.?[si] != 4) continue;
        if (try requestLuid(r, t.luids.?[si], try text(b.text, b.addresses.?[ri]))) {
            const mi = o.rules[ri].metric;
            var found = false;
            for (indices[0..count]) |v| if (v == mi) {
                found = true;
                break;
            };
            if (!found) {
                indices[count] = mi;
                count += 1;
            }
        }
    }
    std.mem.sort(u32, indices[0..count], {}, std.sort.asc(u32));
    return .{ .count = count, .metric_indices = indices };
}
pub fn gpuPrepare(o: *st.Owner, token: u64, b: Bindings, t: ScopeTopology, r: Request, input: GpuInput, bytes: []align(8) u8) Error!*GpuPublication {
    if (o.work.region == null or !o.work.prepared or token != o.token or o.work.gpu_pending != null) return error.Stale;
    try facts(o, b, t);
    try requestValid(r);
    try work(o, bytes);
    try immutableOutside(b, t, bytes);
    try outsideText(r.text, bytes);
    try disjointColumns(Request, r, r.count, bytes);
    try disjointColumns(GpuInput, input, input.count, bytes);
    if (bytes.len > o.config.work_limit - o.work.length) {
        _ = o.fail(.capacity_exceeded, .work, bytes.len + o.work.length, o.config.work_limit);
        return error.Capacity;
    }
    if (st.overlaps(@intFromPtr(bytes.ptr), bytes.len, @intFromPtr(o.work.region.?), o.work.length)) return error.Invalid;
    if (o.work.npu_plan) |npu| {
        if (st.overlaps(@intFromPtr(bytes.ptr), bytes.len, @intFromPtr(npu.base.?), @intCast(npu.length))) return error.Invalid;
        const required = std.math.add(u64, bytes.len + o.work.length, npu.length) catch return error.Capacity;
        if (required > o.config.work_limit) {
            _ = o.fail(.capacity_exceeded, .work, required, o.config.work_limit);
            return error.Capacity;
        }
    }
    const binding = o.gpu_vector orelse return error.Invalid;
    const table = try VectorTable.init(binding);
    const old_count = try table.count();
    const si = sourceIndex(o, input.source_id) orelse return error.Stale;
    const src = o.sources[si];
    if (input.source_id != binding.source_id or input.incarnation != src.incarnation or input.capability != src.capability) return error.Stale;
    if (src.suppressed or src.availability != 1) return error.Stale;
    var planned_source = false;
    for (o.work.expected[0..o.work.raw.count]) |ri| if (o.rules[ri].source == si and b.field_codes.?[ri] == field_codes.gpu_engine_busy) {
        planned_source = true;
        break;
    };
    if (!planned_source) return error.Stale;
    if (input.reserved != 0 or input.observation == 0 or input.observed_ms < 0 or input.status < 1 or input.status > 5 or !pointers(GpuInput, input, input.count)) return error.Invalid;
    const need = try gpuSize(o, input.count);
    if (bytes.len < need) {
        _ = o.fail(.workspace_unavailable, .work, need, bytes.len);
        return error.Work;
    }
    var fba = std.heap.FixedBufferAllocator.init(bytes);
    const a = fba.allocator();
    const plan = a.create(GpuPrepared) catch unreachable;
    const max: usize = @as(usize, input.count) + old_count;
    const rows = a.alloc(EngineRow, max) catch unreachable;
    const selected = a.alloc(u32, max) catch unreachable;
    const addresses = a.alloc(Ref, max) catch unreachable;
    const suppressed = a.alloc(u32, o.metric_count) catch unreachable;
    const arena = a.alloc(u8, @as(usize, max) * 144) catch unreachable;
    plan.* = .{ .base = bytes.ptr, .length = bytes.len, .catalog_generation = o.catalog_generation, .binding = binding, .input = input, .rows = rows, .text = arena, .current = input.status == 1 and input.observed_ms > 0 and !o.work.invalid_sources[si] and !o.work.unsupported_sources[si] };
    if (o.work.unsupported_sources[si]) {
        plan.input.status = 4;
        plan.input.reason = o.work.unsupported_reasons[si];
    }
    plan.view = .{ .handle = plan, .token = token, .selected_rows = selected.ptr, .addresses = addresses.ptr };
    plan.view.suppressed_metrics = suppressed.ptr;
    for (o.rules[0..o.rule_count], 0..) |rule, ri| if (b.field_codes.?[ri] == field_codes.gpu_engine_busy and o.sources[rule.source].id == input.source_id) {
        var found = false;
        for (suppressed[0..plan.view.suppressed_count]) |mi| if (mi == rule.metric) {
            found = true;
            break;
        };
        if (!found) {
            suppressed[plan.view.suppressed_count] = rule.metric;
            plan.view.suppressed_count += 1;
        }
    };
    const catalog_only = r.flags & 2 != 0 and r.flags & 1 == 0;
    if (plan.current) {
        var i: u32 = 0;
        while (i < input.count) : (i += 1) {
            const value = input.usage.?[i];
            if (!std.math.isFinite(value)) continue;
            if (input.classes.?[i] > 2) return error.Invalid;
            const tr = (try gpuTrigger(o, b, t, input.luids.?[i], input.source_id)) orelse continue;
            if (!try requestLuid(r, input.luids.?[i], try text(b.text, b.addresses.?[tr]))) continue;
            const scope = b.scope_indices.?[tr];
            rows[plan.row_count] = .{ .luid = input.luids.?[i], .physical = input.physical.?[i], .engine = input.engines.?[i], .class = input.classes.?[i], .scope = scope, .scope_id = b.scope_ids.?[tr], .scope_generation = t.generations.?[scope], .trigger = o.metrics[o.rules[tr].metric].id, .trigger_rule = tr, .received = true, .usage = std.math.clamp(value, 0, 100) };
            plan.row_count += 1;
        }
        std.mem.sort(EngineRow, rows[0..plan.row_count], {}, rowLess);
        var used: u32 = 0;
        for (rows[0..plan.row_count]) |row| {
            if (used > 0 and rowEqual(rows[used - 1], row)) {
                rows[used - 1].usage = @min(100, rows[used - 1].usage + row.usage);
            } else {
                rows[used] = row;
                used += 1;
            }
        }
        plan.row_count = used;
    }
    // A missing numeric reading is not catalog retirement. Only a successful
    // catalog probe removes absent engines; ordinary failures retain sets.
    var prior: u32 = 0;
    while (prior < old_count) : (prior += 1) {
        if (o.work.invalid_sources[si]) break;
        const luid = table.load(u64, 1, prior);
        const tr = (try gpuTrigger(o, b, t, luid, input.source_id)) orelse continue;
        const scope = b.scope_indices.?[tr];
        const row = EngineRow{ .luid = luid, .physical = table.load(u32, 2, prior), .engine = table.load(u32, 3, prior), .class = table.load(u32, 4, prior), .scope = scope, .scope_id = b.scope_ids.?[tr], .scope_generation = t.generations.?[scope], .trigger = o.metrics[o.rules[tr].metric].id, .trigger_rule = tr };
        var received = false;
        for (rows[0..plan.row_count]) |candidate_row| if (rowEqual(candidate_row, row)) {
            received = true;
            break;
        };
        if (received or (catalog_only and plan.current and try requestLuid(r, luid, try text(b.text, b.addresses.?[tr])))) continue;
        const identity = gpuRowIdentity(input.source_id, input.incarnation, input.capability, row.luid, row.physical, row.engine, row.class, row.scope_id, row.scope_generation);
        if (identity != table.load(u64, 0, prior)) continue;
        rows[plan.row_count] = row;
        plan.row_count += 1;
    }
    if (plan.row_count > table.capacity) {
        _ = o.fail(.capacity_exceeded, .rules, plan.row_count, table.capacity);
        return error.Capacity;
    }
    std.mem.sort(EngineRow, rows[0..plan.row_count], {}, rowLess);
    for (rows[0..plan.row_count], 0..) |*row, i| {
        row.identity = gpuRowIdentity(input.source_id, input.incarnation, input.capability, row.luid, row.physical, row.engine, row.class, row.scope_id, row.scope_generation);
        for (rows[0..i]) |previous| if (previous.identity == row.identity) return error.Invalid;
        try names(plan, row);
        row.selected = try requested(r, plan.text[row.address.offset..][0..row.address.length], try text(b.text, b.addresses.?[row.trigger_rule]));
        row.write_value = plan.current and row.received and row.selected and !catalog_only;
        row.refresh = (row.selected and !catalog_only) or o.work.unsupported_sources[si];
        var old_row: u32 = 0;
        while (old_row < old_count) : (old_row += 1) if (table.load(u64, 0, old_row) == row.identity) {
            row.old_row = old_row;
            break;
        };
        if (row.old_row != p.none) {
            row.good_generation = table.load(u64, 8, row.old_row);
            if (row.selected and table.load(u32, 7, row.old_row) != 0 and table.layout.source(table.bytes(), binding.value_source).state == .unavailable and table.layout.source(table.bytes(), binding.value_source).reason == p.no_subscription_reason) plan.resume_values = true;
        } else if (catalog_only) {
            row.health = 4;
            row.public_status = 4;
        }
    }
    if (plan.used > table.specs[14].size) {
        _ = o.fail(.capacity_exceeded, .payload, plan.used, table.specs[14].size);
        return error.Capacity;
    }
    plan.changed_rows = plan.row_count != old_count;
    for (rows[0..plan.row_count], 0..) |row, i| {
        if (i >= old_count or row.identity != table.load(u64, 0, @intCast(i))) plan.changed_rows = true;
    }
    for (rows[0..plan.row_count], 0..) |*row, i| {
        const at: u32 = @intCast(i);
        const physical_gen = table.load(u64, 8, at);
        if (plan.changed_rows and row.old_row != p.none) {
            row.good_low = table.load(u64, 5, row.old_row);
            row.good_observed = table.load(i64, 9, row.old_row);
            row.good_presence = table.load(u32, 7, row.old_row);
            row.good_observation = table.load(u64, 19, row.old_row);
            row.health = table.load(u32, 10, row.old_row);
            row.health_reason = table.load(u32, 11, row.old_row);
            row.attempt = table.load(i64, 12, row.old_row);
            row.public_status = table.load(u32, 20, row.old_row);
            if (table.load(u32, 6, row.old_row) != 1 or row.good_presence > 1 or (row.good_presence == 1 and !std.math.isFinite(@as(f64, @bitCast(row.good_low))))) return error.Invalid;
        }
        if (row.write_value or (plan.changed_rows and row.old_row != at)) {
            const previous = @max(physical_gen, row.good_generation);
            if (previous == std.math.maxInt(u64)) {
                _ = o.fail(.capacity_exceeded, .generation, 0, previous);
                return error.Capacity;
            }
            row.good_generation = previous + 1;
        }
        if (row.selected) {
            selected[plan.view.selected_count] = at;
            plan.view.selected_count += 1;
        }
        addresses[i] = row.address;
    }
    plan.input.luids = null;
    plan.input.physical = null;
    plan.input.engines = null;
    plan.input.classes = null;
    plan.input.usage = null;
    plan.view.rows = plan.row_count;
    plan.view.text = .{ .bytes = plan.text.ptr, .length = plan.used };
    o.work.gpu_pending = plan;
    return &plan.view;
}
const Prior = struct { state: ss.SourceState, reason: u32, time: i64 };
pub const VectorWriters = struct {
    table: ?VectorTable = null,
    rows: ?ss.Writer = null,
    values: ?ss.Writer = null,
    health: ?ss.Writer = null,
    prior: [3]Prior = undefined,
    pub fn rollback(v: *@This()) void {
        const writers = [_]*?ss.Writer{ &v.rows, &v.values, &v.health };
        for (writers, 0..) |ptr, i| if (ptr.*) |*w| {
            const old_state = v.prior[i];
            w.end(if (old_state.state == .has_value) .has_value else .{ .unavailable = old_state.reason }, old_state.time) catch {};
            ptr.* = null;
        };
    }
};
pub fn begin(o: *st.Owner) Error!VectorWriters {
    const plan = o.work.gpu_pending orelse return .{};
    if (plan.catalog_generation != o.catalog_generation or plan.view.token != o.token or plan.view.published != 0) return error.Stale;
    const t = try VectorTable.init(plan.binding);
    var v = VectorWriters{ .table = t };
    errdefer v.rollback();
    const b = plan.binding;
    for ([_]u32{ b.rows_source, b.value_source, b.health_source }, 0..) |src, i| {
        const e = t.layout.source(t.bytes(), src);
        v.prior[i] = .{ .state = e.state, .reason = e.reason, .time = e.write_time_unix_ms };
    }
    var changed = plan.changed_rows;
    var values = plan.changed_rows or plan.resume_values;
    for (plan.rows[0..plan.row_count]) |row| {
        changed = changed or row.refresh;
        values = values or row.write_value;
    }
    if (changed) v.health = ss.Writer.begin(t.bytes(), b.health_source) catch return error.Shared;
    if (values) v.values = ss.Writer.begin(t.bytes(), b.value_source) catch return error.Shared;
    return v;
}
pub fn admitRows(o: *st.Owner, v: *VectorWriters) Error!void {
    const plan = o.work.gpu_pending orelse return;
    if (plan.changed_rows) v.rows = ss.Writer.begin(v.table.?.bytes(), plan.binding.rows_source) catch return error.Shared;
}
fn put(o: *st.Owner, t: VectorTable, w: *const ss.Writer, comptime T: type, slot: usize, row: u32, value: T) void {
    w.store(T, t.binding.positions[slot], row, (&value)[0..1]) catch unreachable;
    o.counters.written +|= @sizeOf(T);
    o.counters.touched +|= @sizeOf(T);
}
fn setLength(o: *st.Owner, t: VectorTable, w: *const ss.Writer, slot: usize, count: u32, seq: u64) void {
    w.setLength(t.binding.positions[slot], count, seq) catch unreachable;
    o.counters.written +|= 12;
    o.counters.touched +|= 12;
}
fn close(o: *st.Owner, w: *ss.Writer, utc: i64) void {
    w.end(.has_value, utc) catch unreachable;
    o.counters.written +|= 32;
    o.counters.touched +|= 32;
}
pub fn publish(o: *st.Owner, v: *VectorWriters, utc: i64) void {
    const plan = o.work.gpu_pending orelse return;
    const t = v.table.?;
    const n = plan.row_count;
    var row_seq = t.layout.source(t.bytes(), t.binding.rows_source).sequence;
    if (v.rows) |*w| {
        for (plan.rows[0..n], 0..) |r, i| {
            const at: u32 = @intCast(i);
            put(o, t, w, u64, 0, at, r.identity);
            put(o, t, w, u64, 1, at, r.luid);
            put(o, t, w, u32, 2, at, r.physical);
            put(o, t, w, u32, 3, at, r.engine);
            put(o, t, w, u32, 4, at, r.class);
            put(o, t, w, u64, 13, at, @as(u64, r.address.offset) | (@as(u64, r.address.length) << 32));
            put(o, t, w, u64, 15, at, @as(u64, r.key.offset) | (@as(u64, r.key.length) << 32));
            put(o, t, w, u64, 16, at, r.trigger);
            put(o, t, w, u32, 17, at, r.scope);
            put(o, t, w, u64, 18, at, r.scope_id);
        }
        w.store(u8, t.binding.positions[14], 0, plan.text[0..plan.used]) catch unreachable;
        o.counters.written +|= plan.used;
        o.counters.touched +|= plan.used;
        row_seq = w.roundSequence();
        for ([_]usize{ 0, 1, 2, 3, 4, 13, 15, 16, 17, 18 }) |slot| setLength(o, t, w, slot, n, if (slot == 0) 0 else row_seq);
        setLength(o, t, w, 14, plan.used, 0);
        close(o, w, utc);
        v.rows = null;
    }
    for (plan.rows[0..n], 0..) |r, i| {
        const at: u32 = @intCast(i);
        if (r.write_value or plan.changed_rows) {
            const w = &v.values.?;
            put(o, t, w, u64, 5, at, if (r.write_value) @bitCast(r.usage) else r.good_low);
            put(o, t, w, u32, 6, at, 1);
            put(o, t, w, u32, 7, at, if (r.write_value) 1 else r.good_presence);
            put(o, t, w, u64, 8, at, r.good_generation);
            put(o, t, w, i64, 9, at, if (r.write_value) plan.input.observed_ms else r.good_observed);
            put(o, t, w, u64, 19, at, if (r.write_value) plan.input.observation else r.good_observation);
        }
        if (r.refresh or plan.changed_rows) {
            const status: u32 = if (r.write_value) 1 else if (r.refresh) switch (plan.input.status) {
                4 => 3,
                5 => 4,
                else => 2,
            } else r.health;
            const presence = if (r.write_value) @as(u32, 1) else if (plan.changed_rows) r.good_presence else t.load(u32, 7, at);
            const public: u32 = if (r.write_value) 1 else if (!r.refresh) r.public_status else if (presence != 0) 2 else switch (status) {
                3 => 5,
                4 => 4,
                else => 3,
            };
            put(o, t, &v.health.?, u32, 10, at, status);
            put(o, t, &v.health.?, u32, 11, at, if (r.refresh) plan.input.reason else r.health_reason);
            put(o, t, &v.health.?, i64, 12, at, if (r.refresh) utc else r.attempt);
            put(o, t, &v.health.?, u32, 20, at, public);
        }
    }
    if (v.values) |*w| {
        if (plan.changed_rows) for ([_]usize{ 5, 6, 7, 8, 9, 19 }) |slot| setLength(o, t, w, slot, n, row_seq);
        close(o, w, utc);
        v.values = null;
    }
    if (v.health) |*w| {
        if (plan.changed_rows) for ([_]usize{ 10, 11, 12, 20 }) |slot| setLength(o, t, w, slot, n, row_seq);
        close(o, w, utc);
        v.health = null;
    }
    plan.view.published = 1;
}
pub fn stop(o: *st.Owner, utc: i64) Error!void {
    const binding = o.gpu_vector orelse return;
    const t = try VectorTable.init(binding);
    for ([_]u32{ binding.value_source, binding.health_source }) |src| {
        var w = ss.Writer.begin(t.bytes(), src) catch return error.Shared;
        w.end(.{ .unavailable = p.no_subscription_reason }, utc) catch return error.Shared;
    }
    o.gpu_vector = null;
}

pub fn notebookApply(o: *st.Owner, token: u64, b: Bindings, t: ScopeTopology, input: SensorInput) Error!void {
    if (o.work.region == null or o.work.prepared or token != o.token) return error.Stale;
    try facts(o, b, t);
    _ = sourceIndex(o, input.source_id) orelse return error.Stale;
    if (input.status < 1 or input.status > 5 or input.observed_ms < 0 or input.cpu_valid & ~@as(u32, 3) != 0 or !pointers(SensorInput, input, input.count)) return error.Invalid;
    var dedicated: u32 = 0;
    var gpu_scope: u32 = p.none;
    var si: u32 = 0;
    while (si < t.count) : (si += 1) if (t.expansions.?[si] == 4 and t.flags.?[si] & 1 != 0) {
        if (t.luids.?[si] == 0 or (try text(t.text, t.keys.?[si])).len == 0) return error.Invalid;
        dedicated += 1;
        gpu_scope = si;
    };
    var i: u32 = 0;
    while (i < input.count) : (i += 1) {
        if (input.valid.?[i] & ~@as(u32, 3) != 0) return error.Invalid;
        var j: u32 = 0;
        while (j < i) : (j += 1) if (input.indices.?[j] == input.indices.?[i]) return error.Invalid;
    }
    // Preflight every selected target before changing private raw observation
    // slots, so malformed raw schema cannot partially install an observation.
    for (o.work.expected[0..o.work.raw.count]) |ri| if (o.sources[o.rules[ri].source].id == input.source_id) {
        const field = b.field_codes.?[ri];
        if ((field == 19 or field == 20 or field == 63 or field == 64) and o.rules[ri].value_kind != 1) return error.Invalid;
    };
    for (o.work.expected[0..o.work.raw.count], 0..) |ri, oi| {
        if (o.sources[o.rules[ri].source].id != input.source_id) continue;
        const field = b.field_codes.?[ri];
        if (field != 19 and field != 20 and field != 63 and field != 64) continue;
        var status: u32 = switch (input.status) {
            1 => 1,
            4 => 3,
            5 => 4,
            else => 2,
        };
        var value: f64 = 0;
        var present = false;
        if (field == 19 or field == 20) {
            const mask: u32 = if (field == 19) 1 else 2;
            present = input.cpu_valid & mask != 0;
            value = if (field == 19) input.cpu_rpm else input.cpu_percent;
        } else if (dedicated == 1 and input.count == 1 and b.scope_indices.?[ri] == gpu_scope) {
            const mask: u32 = if (field == 64) 1 else 2;
            present = input.valid.?[0] & mask != 0;
            value = if (field == 64) input.rpm.?[0] else input.percent.?[0];
        } else if (status == 1) status = 3;
        if (status == 1 and (!present or !std.math.isFinite(value))) status = 2;
        const raw = o.work.raw.observations;
        raw.status.?[oi] = status;
        raw.reasons.?[oi] = input.reason;
        raw.flags.?[oi] = 0;
        raw.presence.?[oi] = if (status == 1) 1 else 0;
        raw.kinds.?[oi] = 1;
        raw.low.?[oi] = if (status == 1) @bitCast(value) else 0;
        raw.high.?[oi] = 0;
        raw.observed_ms.?[oi] = input.observed_ms;
        raw.payload_lengths.?[oi] = 0;
    }
}
fn checkedIndices(ptr: ?[*]const u32, count: u32) Error![]const u32 {
    if (count != 0 and ptr == null) return error.Invalid;
    return if (ptr) |at| at[0..count] else &.{};
}
pub export fn rm_metric_vector_npu_size(owner: ?*st.Owner, b: ?*const Bindings, t: ?*const ScopeTopology, requested_indices: ?[*]const u32, count: u32, source: u64, out: ?*u64, f: ?*p.Failure) u32 {
    if (out) |v| v.* = 0;
    const o = ownerOf(owner) orelse return finish(null, error.Invalid, f);
    const target = out orelse return finish(o, error.Invalid, f);
    target.* = npuSize(o, (b orelse return finish(o, error.Invalid, f)).*, (t orelse return finish(o, error.Invalid, f)).*, checkedIndices(requested_indices, count) catch return finish(o, error.Invalid, f), source) catch |err| return finish(o, err, f);
    return finish(o, null, f);
}
pub export fn rm_metric_vector_npu_expand(owner: ?*st.Owner, b: ?*const Bindings, t: ?*const ScopeTopology, requested_indices: ?[*]const u32, count: u32, source: u64, bytes: ?[*]u8, length: u64, out: ?*NpuPlan, f: ?*p.Failure) u32 {
    if (out) |v| v.* = .{};
    const o = ownerOf(owner) orelse return finish(null, error.Invalid, f);
    const target = out orelse return finish(o, error.Invalid, f);
    target.* = npuExpand(o, (b orelse return finish(o, error.Invalid, f)).*, (t orelse return finish(o, error.Invalid, f)).*, checkedIndices(requested_indices, count) catch return finish(o, error.Invalid, f), source, span(bytes, length) catch return finish(o, error.Invalid, f)) catch |err| return finish(o, err, f);
    return finish(o, null, f);
}
pub export fn rm_metric_vector_npu_attach(owner: ?*st.Owner, token: u64, generation: u64, plan: ?*NpuPlan, f: ?*p.Failure) u32 {
    const o = ownerOf(owner) orelse return finish(null, error.Invalid, f);
    npuAttach(o, token, generation, plan orelse return finish(o, error.Invalid, f)) catch |err| return finish(o, err, f);
    return finish(o, null, f);
}
pub export fn rm_metric_vector_gpu_expand_size(owner: ?*st.Owner, out: ?*u64, f: ?*p.Failure) u32 {
    if (out) |v| v.* = 0;
    const o = ownerOf(owner) orelse return finish(null, error.Invalid, f);
    const target = out orelse return finish(o, error.Invalid, f);
    target.* = @as(u64, o.metric_count) * 4;
    return finish(o, null, f);
}
pub export fn rm_metric_vector_gpu_expand(owner: ?*st.Owner, b: ?*const Bindings, t: ?*const ScopeTopology, request: ?*const Request, initial: ?[*]const u32, count: u32, bytes: ?[*]u8, length: u64, out: ?*Selection, f: ?*p.Failure) u32 {
    if (out) |v| v.* = .{};
    const o = ownerOf(owner) orelse return finish(null, error.Invalid, f);
    const target = out orelse return finish(o, error.Invalid, f);
    target.* = gpuExpand(o, (b orelse return finish(o, error.Invalid, f)).*, (t orelse return finish(o, error.Invalid, f)).*, (request orelse return finish(o, error.Invalid, f)).*, checkedIndices(initial, count) catch return finish(o, error.Invalid, f), span(bytes, length) catch return finish(o, error.Invalid, f)) catch |err| return finish(o, err, f);
    return finish(o, null, f);
}
pub export fn rm_metric_vector_gpu_bind(owner: ?*st.Owner, binding: ?*const BoundVector, f: ?*p.Failure) u32 {
    const o = ownerOf(owner) orelse return finish(null, error.Invalid, f);
    o.detach();
    const b = (binding orelse return finish(o, error.Invalid, f)).*;
    if (o.gpu_vector != null) return finish(o, error.Invalid, f);
    vectorSeparate(o, b) catch |err| return finish(o, err, f);
    o.gpu_vector = b;
    return finish(o, null, f);
}
pub export fn rm_metric_vector_gpu_work_size(owner: ?*st.Owner, count: u32, out: ?*u64, f: ?*p.Failure) u32 {
    if (out) |v| v.* = 0;
    const o = ownerOf(owner) orelse return finish(null, error.Invalid, f);
    const target = out orelse return finish(o, error.Invalid, f);
    target.* = gpuSize(o, count) catch |err| return finish(o, err, f);
    return finish(o, null, f);
}
pub export fn rm_metric_vector_gpu_prepare(owner: ?*st.Owner, token: u64, b: ?*const Bindings, t: ?*const ScopeTopology, request: ?*const Request, input: ?*const GpuInput, bytes: ?[*]u8, length: u64, out: ?*?*const GpuPublication, f: ?*p.Failure) u32 {
    if (out) |v| v.* = null;
    const o = ownerOf(owner) orelse return finish(null, error.Invalid, f);
    const target = out orelse return finish(o, error.Invalid, f);
    target.* = gpuPrepare(o, token, (b orelse return finish(o, error.Invalid, f)).*, (t orelse return finish(o, error.Invalid, f)).*, (request orelse return finish(o, error.Invalid, f)).*, (input orelse return finish(o, error.Invalid, f)).*, span(bytes, length) catch return finish(o, error.Invalid, f)) catch |err| return finish(o, err, f);
    return finish(o, null, f);
}
pub export fn rm_metric_vector_notebook_apply(owner: ?*st.Owner, token: u64, b: ?*const Bindings, t: ?*const ScopeTopology, input: ?*const SensorInput, f: ?*p.Failure) u32 {
    const o = ownerOf(owner) orelse return finish(null, error.Invalid, f);
    notebookApply(o, token, (b orelse return finish(o, error.Invalid, f)).*, (t orelse return finish(o, error.Invalid, f)).*, (input orelse return finish(o, error.Invalid, f)).*) catch |err| return finish(o, err, f);
    return finish(o, null, f);
}
pub fn exportAnchor() void {
    _ = &field_codes.rm_metric_vector_field_code;
    _ = &rm_metric_vector_npu_size;
    _ = &rm_metric_vector_npu_expand;
    _ = &rm_metric_vector_npu_attach;
    _ = &rm_metric_vector_gpu_expand_size;
    _ = &rm_metric_vector_gpu_expand;
    _ = &rm_metric_vector_gpu_bind;
    _ = &rm_metric_vector_gpu_work_size;
    _ = &rm_metric_vector_gpu_prepare;
    _ = &rm_metric_vector_notebook_apply;
}
comptime {
    std.debug.assert(@sizeOf(Ref) == 8 and @sizeOf(Text) == 16 and @sizeOf(Bindings) == 80 and @sizeOf(ScopeTopology) == 152 and @sizeOf(Request) == 32);
    std.debug.assert(@sizeOf(NpuPlan) == 176 and @sizeOf(BoundVector) == 128 and @sizeOf(GpuInput) == 104 and @sizeOf(SensorInput) == 80 and @sizeOf(GpuPublication) == 80 and @sizeOf(Selection) == 16);
}

// Support's public, non-owning typed reader. All vector-column knowledge stays
// here. The reader holds descriptors only, never a duplicate current-value cache.
pub const HistoryReader = struct {
    table: VectorTable,
    rows: u32,
    pub fn init(o: *const st.Owner) Error!HistoryReader {
        const b = o.gpu_vector orelse return error.Invalid;
        const t = try VectorTable.init(b);
        return .{ .table = t, .rows = try t.count() };
    }
    pub fn identity(r: *const HistoryReader, row: u32) u64 {
        return r.table.load(u64, 0, row);
    }
    pub fn identitySequence(r: *const HistoryReader) u64 {
        return @atomicLoad(u64, &r.table.layout.position(r.table.bytes(), r.table.binding.positions[13]).identity_sequence, .acquire);
    }
    pub fn address(r: *const HistoryReader, row: u32) Error![]const u8 {
        if (row >= r.rows) return error.Invalid;
        const packed_ref = r.table.load(u64, 13, row);
        const offset: u32 = @truncate(packed_ref);
        const length: u32 = @intCast(packed_ref >> 32);
        const used = r.table.layout.position(r.table.bytes(), r.table.binding.positions[14]).length;
        if (@as(u64, offset) + length > used) return error.Invalid;
        return r.table.layout.dataArea(r.table.bytes())[r.table.specs[14].offset + offset ..][0..length];
    }
    pub fn find(r: *const HistoryReader, address_bytes: []const u8, expected: u64) Error!?u32 {
        var found: ?u32 = null;
        var i: u32 = 0;
        while (i < r.rows) : (i += 1) if (std.mem.eql(u8, try r.address(i), address_bytes)) {
            if (found != null) return error.Invalid;
            if (expected != 0 and r.identity(i) != expected) return error.Stale;
            found = i;
        };
        return found;
    }
    pub fn value(r: *const HistoryReader, row: u32) HistoryValue {
        const public_status = r.table.load(u32, 20, row);
        const present = public_status == 1 and r.table.load(u32, 7, row) == 1;
        return .{
            .attempt = r.table.load(i64, 12, row),
            .bits = if (present) r.table.load(u64, 5, row) else 0,
            .present = if (present) 1 else 0,
            .attempted = public_status != 4 or r.table.load(u32, 7, row) != 0,
            .public_status = public_status,
        };
    }
    pub fn preflight(r: *const HistoryReader) Error!void {
        for ([_]u32{ r.table.binding.rows_source, r.table.binding.value_source, r.table.binding.health_source }) |src| {
            const seq = @atomicLoad(u64, &r.table.layout.source(r.table.bytes(), src).sequence, .acquire);
            // Post-publication VectorTable.init still requires six tickets.
            if (seq > ss.ended_bit - 8) return error.Capacity;
        }
        var i: u32 = 0;
        while (i < r.rows) : (i += 1) {
            _ = try r.address(i);
            const val = r.value(i);
            const presence = r.table.load(u32, 7, i);
            if (r.identity(i) == 0 or val.attempt < 0 or val.public_status < 1 or val.public_status > 7 or r.table.load(u32, 6, i) != 1 or presence > 1 or ((val.public_status == 1 or val.public_status == 2) and presence != 1) or (presence == 1 and (r.table.load(u64, 8, i) == 0 or !std.math.isFinite(@as(f64, @bitCast(r.table.load(u64, 5, i))))))) return error.Invalid;
        }
    }
    pub fn separate(r: *const HistoryReader, region: ?[*]u8, length: u64, sources: []const u32, specs: []const ss.PositionSpec) Error!void {
        const b = r.table.binding;
        if (region != b.region) {
            if (st.overlaps(@intFromPtr(region.?), @intCast(length), @intFromPtr(b.region.?), @intCast(b.byte_length))) return error.Invalid;
            return;
        }
        for (sources) |src| for ([_]u32{ b.rows_source, b.value_source, b.health_source }) |other| if (src == other) return error.Invalid;
        for (specs) |spec| for (r.table.specs) |other| if (spec.size != 0 and other.size != 0 and @as(u64, spec.offset) < @as(u64, other.offset) + other.size and @as(u64, other.offset) < @as(u64, spec.offset) + spec.size) return error.Invalid;
    }
};
pub const HistoryValue = struct { attempt: i64, bits: u64, present: u32, attempted: bool, public_status: u32 };
pub const HistorySelection = struct {
    plan: *const GpuPrepared,
    pub fn isSelected(s: HistorySelection, row: u32) bool {
        if (!s.plan.rows[row].refresh) return false;
        for (s.plan.view.selected_rows.?[0..s.plan.view.selected_count]) |selected| if (selected == row) return true;
        return false;
    }
};
/// Capture before commit detaches Work. Borrow only through the SAME outer work
/// lease. The plan supplies selection/identity metadata, never numeric samples.
pub fn historySelection(o: *const st.Owner) Error!?HistorySelection {
    const plan = o.work.gpu_pending orelse return null;
    if (plan.catalog_generation != o.catalog_generation or plan.view.token != o.token or plan.view.published != 0) return error.Stale;
    if (plan.row_count > plan.rows.len or plan.view.rows != plan.row_count or plan.view.selected_count > plan.row_count or plan.view.selected_rows == null) return error.Invalid;
    for (plan.view.selected_rows.?[0..plan.view.selected_count], 0..) |row, i| {
        if (row >= plan.row_count) return error.Invalid;
        for (plan.view.selected_rows.?[0..i]) |prior| if (prior == row) return error.Invalid;
    }
    return .{ .plan = plan };
}
pub const HistoryDefinition = struct { source_id: u64, identity: u64, row: u32 };
/// Prepared definitions allow history subscription before a newly discovered
/// engine's FIRST numeric publication. No candidate value escapes this helper.
pub fn historyCandidate(o: *const st.Owner, token: u64, address_bytes: []const u8, expected: u64) Error!?HistoryDefinition {
    const selection = (try historySelection(o)) orelse return null;
    if (token != o.token) return error.Stale;
    const plan = selection.plan;
    var found: ?HistoryDefinition = null;
    for (plan.rows[0..plan.row_count], 0..) |row, i| {
        if (@as(u64, row.address.offset) + row.address.length > plan.used) return error.Invalid;
        if (!std.mem.eql(u8, plan.text[row.address.offset..][0..row.address.length], address_bytes)) continue;
        if (found != null or (expected != 0 and row.identity != expected)) return error.Stale;
        found = .{ .source_id = plan.binding.source_id, .identity = row.identity, .row = @intCast(i) };
    }
    return found;
}

// Pure discovery admission/read helpers. All schema/selection knowledge remains
// in this authoritative module; callers supply real mappings and local work.
fn gpuLayoutSize(candidates: u64, metrics: u64) Error!u64 {
    var size: u64 = 0;
    try addSize(GpuPrepared, 1, &size);
    try addSize(EngineRow, candidates, &size);
    try addSize(u32, candidates, &size);
    try addSize(Ref, candidates, &size);
    try addSize(u32, metrics, &size);
    try addSize(u8, std.math.mul(u64, candidates, 144) catch return error.Capacity, &size);
    return size;
}
fn npuLayoutSize(targets: u64, edges: u64, metrics: u64, originals: u64, scopes: u64) Error!u64 {
    var size: u64 = 0;
    try addSize(u32, targets, &size);
    try addSize(u32, std.math.add(u64, targets, 1) catch return error.Capacity, &size);
    try addSize(u32, edges, &size);
    try addSize(u32, metrics, &size);
    try addSize(u32, originals, &size);
    try addSize(u32, targets, &size);
    try addSize(u64, scopes, &size);
    try addSize(u32, targets, &size);
    try addSize(u64, targets, &size);
    try addSize(u64, targets, &size);
    try addSize(u64, edges, &size);
    try addSize(u64, edges, &size);
    try addSize(u32, edges, &size);
    return size;
}
pub fn discoveryGpuWorkSize(binding: BoundVector, raw_rows: u32, bound_row_capacity: u32, metric_count: u32, work_limit: u64) Error!u64 {
    var failure: p.Failure = .{};
    return discoveryGpuWorkSizeChecked(binding, raw_rows, bound_row_capacity, metric_count, work_limit, &failure);
}
pub fn discoveryGpuWorkSizeChecked(binding: BoundVector, raw_rows: u32, bound_row_capacity: u32, metric_count: u32, work_limit: u64, failure: *p.Failure) Error!u64 {
    failure.* = .{};
    const t = VectorTable.init(binding) catch |err| {
        failure.* = .{ .code = code(err), .family = .binding };
        return err;
    };
    const current = t.count() catch |err| {
        failure.* = .{ .code = code(err), .family = .binding };
        return err;
    };
    if (bound_row_capacity > t.capacity or current > bound_row_capacity) {
        failure.* = .{ .code = .capacity_exceeded, .family = .rules, .required = if (bound_row_capacity > t.capacity) bound_row_capacity else current, .limit = if (bound_row_capacity > t.capacity) t.capacity else bound_row_capacity };
        return error.Capacity;
    }
    const candidates = @as(u64, raw_rows) + bound_row_capacity;
    const size = gpuLayoutSize(candidates, metric_count) catch |err| {
        failure.* = .{ .code = .capacity_exceeded, .family = .work, .required = std.math.maxInt(u64), .limit = work_limit };
        return err;
    };
    if (size > work_limit) {
        failure.* = .{ .code = .capacity_exceeded, .family = .work, .required = size, .limit = work_limit };
        return error.Capacity;
    }
    return size;
}
pub fn discoveryNpuWorkSize(rule_count: u32, metric_count: u32, scope_count: u32) Error!u64 {
    const targets: u64 = @min(rule_count, metric_count);
    const edges = targets * rule_count;
    if (edges > std.math.maxInt(u32)) return error.Capacity;
    return npuLayoutSize(targets, edges, metric_count, metric_count, scope_count);
}
fn outsideAttached(o: *const st.Owner, pointer: usize, length: usize) Error!void {
    if (o.work.region) |base| if (st.overlaps(pointer, length, @intFromPtr(base), o.work.length)) return error.Invalid;
    if (o.work.gpu_pending) |gpu| if (st.overlaps(pointer, length, @intFromPtr(gpu.base), gpu.length)) return error.Invalid;
    if (o.work.npu_plan) |npu| if (st.overlaps(pointer, length, @intFromPtr(npu.base.?), @intCast(npu.length))) return error.Invalid;
}
pub fn discoveryValidateGpuBinding(o: *const st.Owner, binding: BoundVector, expectedSourceID: u64) Error!void {
    // Match gpu_bind's non-idempotent install contract, so a passing cold
    // preflight cannot promise an install that the export will then reject.
    if (o.gpu_vector != null) return error.Invalid;
    return validateGpuLocation(o, binding, expectedSourceID);
}
fn validateGpuLocation(o: *const st.Owner, binding: BoundVector, expectedSourceID: u64) Error!void {
    if (o.magic != st.Owner.live_magic or expectedSourceID == 0 or binding.source_id != expectedSourceID) return error.Invalid;
    if (o.result_count == 0 or binding.region != o.results[0].region or binding.byte_length != o.results[0].byte_length) return error.Invalid;
    const t = try VectorTable.init(binding);
    _ = try t.count();
    const pointer = @intFromPtr(binding.region.?);
    const length: usize = @intCast(binding.byte_length);
    if (st.overlaps(pointer, length, @intFromPtr(o), @intCast(o.active_length))) return error.Invalid;
    try outsideAttached(o, pointer, length);
    var declared = false;
    for (o.bindings) |raw| {
        if (raw.source_id == expectedSourceID) {
            if (declared or raw.access_region != 0) return error.Invalid;
            declared = true;
        } else if (o.legacy_bindings and o.source_count == 0 and raw.source_id == 0 and raw.access_region == 0) declared = true;
        try different(t, raw.table);
        if (binding.region != raw.table.region and st.overlaps(pointer, length, @intFromPtr(raw.table.region.?), @intCast(raw.table.byte_length))) return error.Invalid;
    }
    if (!declared) return error.Stale;
    if (o.source_count != 0) {
        const si = sourceIndex(o, expectedSourceID) orelse return error.Stale;
        if (o.sources[si].access_region != 0) return error.Invalid;
    }
    for (o.results[0..o.result_count]) |result| {
        try different(t, result);
        if (binding.region != result.region and st.overlaps(pointer, length, @intFromPtr(result.region.?), @intCast(result.byte_length))) return error.Invalid;
    }
}
fn gpuRowIdentity(source: u64, incarnation: u64, capability: u64, luid: u64, physical: u32, engine: u32, class: u32, scope_id: u64, scope_generation: u64) u64 {
    const words = [_]u64{ source, incarnation, capability, luid, physical, engine, class, scope_id, scope_generation };
    const hash = std.hash.Wyhash.hash(0x726d656e67696e65, std.mem.asBytes(&words));
    return if (hash == 0) 1 else hash;
}
pub const RequestProjection = extern struct {
    binding: BoundVector = .{},
    source_id: u64 = 0,
    catalog_generation: u64 = 0,
    membership_revision: u64 = 0,
    selection_revision: u64 = 0,
    selected_count: u32 = 0,
    base_count: u32 = 0,
    selected_rows: ?[*]const u32 = null,
    expected_identities: ?[*]const u64 = null,
    base_metric_indices: ?[*]const u32 = null,
    base_metric_ids: ?[*]const u64 = null,
    suppressed_count: u32 = 0,
    reserved: u32 = 0,
    suppressed_metric_indices: ?[*]const u32 = null,
    suppressed_metric_ids: ?[*]const u64 = null,
};
fn projectionSize(rows: u32, metrics: u32) Error!u64 {
    var size: u64 = 0;
    try addSize(u32, rows, &size);
    try addSize(u64, rows, &size);
    try addSize(u32, metrics, &size);
    try addSize(u64, metrics, &size);
    return size;
}
pub fn requestProjectionSize(o: *const st.Owner) Error!u64 {
    if (o.magic != st.Owner.live_magic) return error.Invalid;
    const binding = o.gpu_vector orelse return 0;
    const table = try VectorTable.init(binding);
    const size = try projectionSize(try table.count(), o.metric_count);
    if (size > o.config.work_limit) return error.Capacity;
    return size;
}
fn projectedRow(o: *const st.Owner, b: Bindings, t: ScopeTopology, r: Request, reader: HistoryReader, row: u32) Error!bool {
    const table = reader.table;
    const tr = (try gpuTrigger(o, b, t, table.load(u64, 1, row), table.binding.source_id)) orelse return false;
    const scope = b.scope_indices.?[tr];
    const mi = o.rules[tr].metric;
    if (table.load(u32, 17, row) != scope or table.load(u64, 18, row) != b.scope_ids.?[tr] or table.load(u64, 16, row) != o.metrics[mi].id) return false;
    const si = sourceIndex(o, table.binding.source_id) orelse return error.Stale;
    const src = o.sources[si];
    if (reader.identity(row) != gpuRowIdentity(src.id, src.incarnation, src.capability, table.load(u64, 1, row), table.load(u32, 2, row), table.load(u32, 3, row), table.load(u32, 4, row), b.scope_ids.?[tr], t.generations.?[scope])) return false;
    const trigger = try text(b.text, b.addresses.?[tr]);
    return try requestLuid(r, table.load(u64, 1, row), trigger) and try requested(r, try reader.address(row), trigger);
}
pub fn projectRequest(o: *const st.Owner, b: Bindings, t: ScopeTopology, r: Request, bytes: []align(8) u8) Error!RequestProjection {
    if (o.magic != st.Owner.live_magic or @intFromPtr(bytes.ptr) % 8 != 0) return error.Invalid;
    try factsReadOnly(o, b, t);
    try requestValid(r);
    try workReadOnly(o, bytes);
    try outsideAttached(o, @intFromPtr(bytes.ptr), bytes.len);
    var occupied: u64 = bytes.len;
    if (o.work.region != null) occupied = std.math.add(u64, occupied, o.work.length) catch return error.Capacity;
    if (o.work.gpu_pending) |gpu| occupied = std.math.add(u64, occupied, gpu.length) catch return error.Capacity;
    if (o.work.npu_plan) |npu| occupied = std.math.add(u64, occupied, npu.length) catch return error.Capacity;
    if (occupied > o.config.work_limit) return error.Capacity;
    try immutableOutside(b, t, bytes);
    try outsideText(r.text, bytes);
    try disjointColumns(Request, r, r.count, bytes);
    const size = try requestProjectionSize(o);
    if (bytes.len < size) return error.Work;
    var out = RequestProjection{ .catalog_generation = o.catalog_generation };
    const binding = o.gpu_vector orelse {
        out.selection_revision = 1;
        return out;
    };
    try validateGpuLocation(o, binding, binding.source_id);
    const reader = try HistoryReader.init(o);
    // Validate every selector/identity/ref BEFORE touching even scratch output.
    var row: u32 = 0;
    while (row < reader.rows) : (row += 1) {
        _ = try reader.address(row);
        if (reader.identity(row) == 0) return error.Invalid;
        var prior: u32 = 0;
        while (prior < row) : (prior += 1) if (reader.identity(prior) == reader.identity(row)) return error.Invalid;
        _ = try projectedRow(o, b, t, r, reader, row);
    }
    var fba = std.heap.FixedBufferAllocator.init(bytes);
    const a = fba.allocator();
    const selected = a.alloc(u32, reader.rows) catch return error.Work;
    const identities = a.alloc(u64, reader.rows) catch return error.Work;
    const base = a.alloc(u32, o.metric_count) catch return error.Work;
    const ids = a.alloc(u64, o.metric_count) catch return error.Work;
    out.binding = binding;
    out.source_id = binding.source_id;
    out.membership_revision = @atomicLoad(u64, &reader.table.layout.source(reader.table.bytes(), binding.rows_source).sequence, .acquire);
    out.selected_rows = selected.ptr;
    out.expected_identities = identities.ptr;
    out.base_metric_indices = base.ptr;
    out.base_metric_ids = ids.ptr;
    row = 0;
    while (row < reader.rows) : (row += 1) if (projectedRow(o, b, t, r, reader, row) catch unreachable) {
        selected[out.selected_count] = row;
        identities[out.selected_count] = reader.identity(row);
        out.selected_count += 1;
    };
    for (o.metrics[0..o.metric_count], 0..) |metric, mi| {
        for (o.rules[0..o.rule_count], 0..) |rule, ri| if (rule.metric == mi and b.field_codes.?[ri] == field_codes.gpu_engine_busy and o.sources[rule.source].id == binding.source_id) {
            base[out.base_count] = @intCast(mi);
            ids[out.base_count] = metric.id;
            out.base_count += 1;
            break;
        };
    }
    out.suppressed_count = out.base_count;
    out.suppressed_metric_indices = out.base_metric_indices;
    out.suppressed_metric_ids = out.base_metric_ids;
    var hash = std.hash.Wyhash.init(0x726d70726f6a6563);
    hash.update(std.mem.asBytes(&out.source_id));
    hash.update(std.mem.asBytes(&out.catalog_generation));
    hash.update(std.mem.asBytes(&out.membership_revision));
    hash.update(std.mem.asBytes(&out.selected_count));
    hash.update(std.mem.asBytes(&out.base_count));
    hash.update(std.mem.sliceAsBytes(selected[0..out.selected_count]));
    hash.update(std.mem.sliceAsBytes(identities[0..out.selected_count]));
    hash.update(std.mem.sliceAsBytes(base[0..out.base_count]));
    hash.update(std.mem.sliceAsBytes(ids[0..out.base_count]));
    const revision = hash.final();
    out.selection_revision = if (revision == 0) 1 else revision;
    return out;
}
comptime {
    std.debug.assert(@sizeOf(RequestProjection) == 224);
    std.debug.assert(@offsetOf(RequestProjection, "source_id") == 128 and @offsetOf(RequestProjection, "selected_rows") == 168 and @offsetOf(RequestProjection, "suppressed_metric_ids") == 216);
}
