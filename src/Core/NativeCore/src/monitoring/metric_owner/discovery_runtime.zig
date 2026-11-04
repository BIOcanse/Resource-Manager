//! Actual-run discovery continuity. All memory is borrowed from that run's tail.
//! No current values, inventory replica, lease, allocator or work pointer lives here.
const std = @import("std");
const c = @import("catalog_compiler.zig");
const sel = @import("selection.zig");
const p = c.p;
const st = @import("state.zig");
const kernel_catalog = @import("catalog.zig");
const kernel_round = @import("round.zig");
const history = @import("history.zig");
const ss = @import("rm_shared_section");
const inv = @import("inventory.zig");
const dv = @import("discovery_vectors.zig");
const sdk = @import("sdk_dispatch.zig");
pub const SourceRole = extern struct {
    source_id: u64 = 0,
    flags: u32 = 0,
    row_capacity: u32 = 0,
    text_capacity: u32 = 0,
    reserved: u32 = 0,
    source_capability_mask: u64 = 0,
    inventory_generation: u64 = 0,
    topology_fingerprint: u64 = 0,
};
pub const Roles = extern struct { count: u32 = 0, reserved: u32 = 0, rows: ?[*]const SourceRole = null };
pub const SourceRoleView = extern struct { count: u32 = 0, reserved: u32 = 0, flags: ?[*]const u32 = null };
pub const InventoryHeader = extern struct {
    generation: u64 = 0,
    topology_fingerprint: u64 = 0,
    observed_ms: i64 = 0,
    count: u32 = 0,
    observed_count: u32 = 0,
    skipped_count: u32 = 0,
    overflow_count: u32 = 0,
    status: u32 = 0,
    reason: u32 = 0,
    text_length: u32 = 0,
    reserved: u32 = 0,
    query_token: u64 = 0,
};
pub const InventoryInput = extern struct {
    source_id: u64 = 0,
    incarnation: u64 = 0,
    capability: u64 = 0,
    observation: u64 = 0,
    catalog_generation: u64 = 0,
    query_token: u64 = 0,
    row_capacity: u32 = 0,
    text_capacity: u32 = 0,
    header: ?*InventoryHeader = null,
    luids: ?[*]u64 = null,
    keys: ?[*]c.Ref = null,
    text: c.Text = .{},
};
pub const Sizes = extern struct {
    extra_active_bytes: u64 = 0,
    extra_operation_work_bytes: u64 = 0,
    canonical_bytes: u64 = 0,
    learned_text_bytes: u64 = 0,
    source_capacity: u32 = 0,
    rule_capacity: u32 = 0,
    compiler_work_bytes: u64 = 0,
    input_text_bytes: u64 = 0,
};
/// Optional anonymous normal-region catalog: row ordinal, field code, metric
/// kind, value kind, field name, source name, UTF8 table. No runtime identities.
pub const CatalogBinding = extern struct {
    region: ?[*]u8 = null,
    length: u64 = 0,
    source: u32 = 0,
    reserved: u32 = 0,
    positions: [7]u32 = @splat(p.none),
};
pub const CatalogDeclaration = extern struct {
    rows: u32 = 0,
    string_bytes: u32 = 0,
    widths: [7]u32 = .{ 8, 4, 4, 4, 8, 8, 1 },
    kinds: [7]u32 = .{ 2, 2, 2, 2, 3, 3, 4 },
};
pub const ProviderInputs = extern struct {
    count: u32 = 0,
    reserved: u32 = 0,
    source_ids: ?[*]const u64 = null,
    states: ?[*]const sel.ProviderState = null,
};
pub const Measurement = extern struct {
    sizes: Sizes = .{},
    catalog: CatalogDeclaration = .{},
    metric_count: u32 = 0,
    reserved: u32 = 0,
};
pub const FullSizes = extern struct { base: Sizes = .{}, extra: dv.Extra = .{} };
pub const FullMeasurement = extern struct { sizes: FullSizes = .{}, catalog: CatalogDeclaration = .{}, metric_count: u32 = 0, reserved: u32 = 0 };
pub const SourceEvents = extern struct {
    count: u32 = 0,
    reserved: u32 = 0,
    source_indices: ?[*]const u32 = null,
    statuses: ?[*]u32 = null,
    apply_cooldown: ?[*]u32 = null,
    completed_ms: ?[*]i64 = null,
    source_ids: ?[*]const u64 = null,
    observations: ?[*]const u64 = null,
};
pub const Dispatch = extern struct {
    count: u32 = 0,
    reserved: u32 = 0,
    field_codes: ?[*]const u32 = null,
    scope_indices: ?[*]const u32 = null,
    display_indices: ?[*]const u32 = null,
    scope_ids: ?[*]const u64 = null,
    field_names: ?[*]const c.Ref = null,
    device_keys: ?[*]const c.Ref = null,
    text: c.Text = .{},
};
pub const Plan = extern struct {
    operation: ?*Operation = null,
    raw: p.Round = .{},
    selection: sel.Selection = .{},
    dispatch: Dispatch = .{},
    sources: SourceEvents = .{},
    readings: sel.ReadingEvents = .{},
};
pub const Context = struct {
    magic: u64 = live_magic,
    owner: *st.Owner,
    sizes: Sizes,
    canonical: []align(8) u8,
    providers: []sel.ProviderState,
    roles: []SourceRole,
    resources: []u32,
    inventory_binding: ?inv.Binding = null,
    cold_binding: ?CatalogBinding = null,
    full: dv.Active = .{},
    learned: sel.ReadingFacts,
    learned_text: []u8,
    compiled: c.Compiled = .{},
    catalog_generation: u64 = 0,
    cooldown_ms: u64 = 0,
    operation_token: u64 = 0,
    pub const live_magic: u64 = 0x444953434f565232;
};
pub const Operation = struct {
    context: *Context,
    token: u64 = 0,
    catalog_generation: u64 = 0,
    source_events: SourceEvents = .{},
    expected_sources: []u32,
    providers: []sel.ProviderState,
    current_observations: []u64,
    source_roles: []u32,
    inventory_source: u32 = p.none,
    inventory_input: InventoryInput = .{},
    inventory_work: []align(8) u8 = &.{},
    inventory_text: []u8 = &.{},
    inventory_prepared: ?inv.Prepared = null,
    vectors: dv.Round = .{},
    sdk_grouping: sdk.Dispatch = .{},
    readings: sel.ReadingEvents = .{},
    learning_work: []align(8) u8,
    learned: sel.ReadingFacts = .{},
    due: []const u32 = &.{},
    prepared: bool = false,
    accepted: bool = false,
};
fn align8(value: u64) c.Error!u64 {
    return (std.math.add(u64, value, 7) catch return error.Exhausted) & ~@as(u64, 7);
}
fn add(cursor: *u64, comptime T: type, count: u64) c.Error!void {
    cursor.* = try align8(cursor.*);
    cursor.* = std.math.add(u64, cursor.*, std.math.mul(u64, @sizeOf(T), count) catch return error.Exhausted) catch return error.Exhausted;
}
fn columnCount(comptime T: type, value: T, comptime name: []const u8) u32 {
    return if (comptime c.eql(name, "support_scopes") or c.eql(name, "support_fields")) value.support_count else value.count;
}
fn columnsSize(comptime T: type, value: T, cursor: *u64) c.Error!void {
    inline for (@typeInfo(T).@"struct".field_names, @typeInfo(T).@"struct".field_types) |field_name, field_type| if (@typeInfo(field_type) == .optional) {
        if (@field(value, field_name) != null) {
            const Ptr = @typeInfo(field_type).optional.child;
            try add(cursor, @typeInfo(Ptr).pointer.child, columnCount(T, value, field_name));
        }
    };
}
fn copyColumns(comptime T: type, value: T, allocator: std.mem.Allocator) !T {
    var out = value;
    inline for (@typeInfo(T).@"struct".field_names, @typeInfo(T).@"struct".field_types) |field_name, field_type| if (@typeInfo(field_type) == .optional) {
        if (@field(value, field_name)) |pointer| {
            const Element = @typeInfo(@TypeOf(pointer)).pointer.child;
            const n = columnCount(T, value, field_name);
            const destination = try allocator.alignedAlloc(Element, .@"8", n);
            @memcpy(destination, pointer[0..n]);
            @field(out, field_name) = destination.ptr;
        }
    };
    return out;
}
fn referencedText(src: c.Compiled, fact: bool) c.Error!u64 {
    var total: u64 = 0;
    var i: u32 = 0;
    if (fact) {
        while (i < src.sources.count) : (i += 1) total += (try c.get(src.fact_text, src.sources.names.?[i])).len;
        i = 0;
        while (i < src.scopes.count) : (i += 1) inline for (.{ "keys", "names", "binding_keys" }) |field| {
            total += (try c.get(src.fact_text, @field(src.scopes, field).?[i])).len;
        };
    } else {
        while (i < src.bindings.count) : (i += 1) inline for (.{ "addresses", "legacy", "field_names", "device_keys" }) |field| {
            total += (try c.get(src.text, @field(src.bindings, field).?[i])).len;
        };
        i = 0;
        while (i < src.handles.count) : (i += 1) total += (try c.get(src.text, src.handles.keys.?[i])).len;
        i = 0;
        while (i < src.legacy.count) : (i += 1) total += (try c.get(src.text, src.legacy.keys.?[i])).len;
    }
    if (total > std.math.maxInt(u32)) return error.Payload;
    return total;
}
pub fn compactSize(src: c.Compiled) c.Error!u64 {
    _ = try sel.required(src);
    var bytes: u64 = 0;
    try columnsSize(c.Sources, src.sources, &bytes);
    try columnsSize(p.RuleColumns, src.catalog.rules, &bytes);
    try columnsSize(p.MetricColumns, src.catalog.metrics, &bytes);
    try columnsSize(c.Bindings, src.bindings, &bytes);
    try columnsSize(c.Handles, src.handles, &bytes);
    try columnsSize(c.Scopes, src.scopes, &bytes);
    try columnsSize(c.Legacy, src.legacy, &bytes);
    try add(&bytes, u8, try referencedText(src, false));
    try add(&bytes, u8, try referencedText(src, true));
    return try align8(bytes);
}
const TextWriter = struct {
    bytes: []u8,
    used: u32 = 0,
    fn put(self: *TextWriter, text: []const u8) c.Ref {
        const ref = c.Ref{ .offset = self.used, .length = @intCast(text.len) };
        @memcpy(self.bytes[self.used..][0..text.len], text);
        self.used += @intCast(text.len);
        return ref;
    }
};
/// Called only after compactSize and destination extent preflight. No fallible
/// operation remains here; all Refs are rebased into the actual-run tail.
fn compact(src: c.Compiled, destination: []align(8) u8) c.Compiled {
    var fba = std.heap.FixedBufferAllocator.init(destination);
    const a = fba.allocator();
    var out = src;
    out.sources = copyColumns(c.Sources, src.sources, a) catch unreachable;
    out.catalog.sources = .{ .count = out.sources.count, .ids = out.sources.ids, .incarnations = out.sources.incarnations, .capabilities = out.sources.capabilities, .availability = out.sources.availability };
    out.catalog.rules = copyColumns(p.RuleColumns, src.catalog.rules, a) catch unreachable;
    out.catalog.metrics = copyColumns(p.MetricColumns, src.catalog.metrics, a) catch unreachable;
    out.bindings = copyColumns(c.Bindings, src.bindings, a) catch unreachable;
    out.handles = copyColumns(c.Handles, src.handles, a) catch unreachable;
    out.scopes = copyColumns(c.Scopes, src.scopes, a) catch unreachable;
    out.legacy = copyColumns(c.Legacy, src.legacy, a) catch unreachable;
    // The size function rounds each column to8, which is >= its natural alignment.
    // Align text explicitly to make its calculation and allocator layout agree.
    _ = a.alloc(u8, (8 - fba.end_index % 8) % 8) catch unreachable;
    var text = TextWriter{ .bytes = a.alloc(u8, @intCast(referencedText(src, false) catch unreachable)) catch unreachable };
    var i: u32 = 0;
    while (i < src.bindings.count) : (i += 1) inline for (.{ "addresses", "legacy", "field_names", "device_keys" }) |field| {
        @constCast(@field(out.bindings, field).?)[i] = text.put(c.get(src.text, @field(src.bindings, field).?[i]) catch unreachable);
    };
    i = 0;
    while (i < src.handles.count) : (i += 1) @constCast(out.handles.keys.?)[i] = text.put(c.get(src.text, src.handles.keys.?[i]) catch unreachable);
    i = 0;
    while (i < src.legacy.count) : (i += 1) @constCast(out.legacy.keys.?)[i] = text.put(c.get(src.text, src.legacy.keys.?[i]) catch unreachable);
    out.text = .{ .bytes = text.bytes.ptr, .length = text.used };
    _ = a.alloc(u8, (8 - fba.end_index % 8) % 8) catch unreachable;
    var facts = TextWriter{ .bytes = a.alloc(u8, @intCast(referencedText(src, true) catch unreachable)) catch unreachable };
    i = 0;
    while (i < src.sources.count) : (i += 1) @constCast(out.sources.names.?)[i] = facts.put(c.get(src.fact_text, src.sources.names.?[i]) catch unreachable);
    i = 0;
    while (i < src.scopes.count) : (i += 1) inline for (.{ "keys", "names", "binding_keys" }) |field| {
        @constCast(@field(out.scopes, field).?)[i] = facts.put(c.get(src.fact_text, @field(src.scopes, field).?[i]) catch unreachable);
    };
    out.fact_text = .{ .bytes = facts.bytes.ptr, .length = facts.used };
    return out;
}
fn learningSize(rules: u32, text: u64) c.Error!u64 {
    return try align8(@as(u64, rules) * 20 + 32 + text);
}
fn roleWork(roles: Roles) c.Error!u64 {
    if (roles.reserved != 0 or (roles.count != 0 and roles.rows == null)) return error.Invalid;
    var result: u64 = 0;
    var i: u32 = 0;
    while (i < roles.count) : (i += 1) {
        const role = roles.rows.?[i];
        if (role.source_id == 0 or role.reserved != 0 or role.flags & ~@as(u32, 15) != 0) return error.Invalid;
        var prior: u32 = 0;
        while (prior < i) : (prior += 1) if (roles.rows.?[prior].source_id == role.source_id) return error.Identity;
        if (role.flags & 1 != 0) {
            if (result != 0 or role.inventory_generation == 0) return error.Invalid;
            result = try align8(@as(u64, role.row_capacity) * 128 + @as(u64, role.text_capacity) * 2 + 512);
        }
    }
    return result;
}
fn validateRoles(cat: c.Compiled, roles: Roles, failure: *p.Failure) c.Error!void {
    _ = try roleWork(roles);
    if (roles.count > cat.sources.count) return error.Invalid;
    var i: u32 = 0;
    while (i < roles.count) : (i += 1) {
        const role = roles.rows.?[i];
        if (role.source_id == 0) continue;
        var known = false;
        var si: u32 = 0;
        while (si < cat.sources.count) : (si += 1) if (cat.sources.ids.?[si] == role.source_id) {
            known = true;
        };
        if (!known) return error.Identity;
        if (role.flags & 1 == 0) continue;
        si = 0;
        while (si < cat.bindings.count) : (si += 1) if (cat.catalog.rules.source_ids.?[si] == role.source_id) return error.Invalid;
        var count: u64 = 0;
        var text: u64 = 0;
        si = 0;
        while (si < cat.scopes.count) : (si += 1) if (cat.scopes.expansions.?[si] == 4) {
            if (cat.scopes.generations.?[si] != role.inventory_generation) return error.Identity;
            count += 1;
            text += (try c.get(cat.fact_text, cat.scopes.keys.?[si])).len;
        };
        if (count > role.row_capacity) return c.fail(failure, .capacity_exceeded, .binding, count, role.row_capacity);
        if (text > role.text_capacity) return c.fail(failure, .payload_capacity, .payload, text, role.text_capacity);
    }
}
fn roleFor(ctx: *const Context, source_id: u64) SourceRole {
    for (ctx.roles) |role| if (role.source_id == source_id) return role;
    return .{};
}
fn currentRoles(ctx: *const Context) Roles {
    var count: u32 = 0;
    while (count < ctx.roles.len and ctx.roles[count].source_id != 0) : (count += 1) {}
    return .{ .count = count, .rows = ctx.roles.ptr };
}
fn separateInventory(binding: inv.Binding, region: ?[*]u8, length: u64, source: u32) c.Error!void {
    if (binding.region == region) {
        if (binding.length != length or binding.source == source) return error.Invalid;
    } else if (c.overlap(@intFromPtr(binding.region), @intCast(binding.length), @intFromPtr(region), @intCast(length))) return error.Invalid;
}
fn inventoryExclusive(ctx: *const Context, binding: inv.Binding, work: []align(8) u8) c.Error!void {
    if (binding.region == null or binding.length > std.math.maxInt(usize) or @intFromPtr(binding.region) % 8 != 0) return error.Invalid;
    if (binding.region != ctx.owner.results[0].region or binding.length != ctx.owner.results[0].byte_length) return error.Invalid;
    if (c.overlap(@intFromPtr(binding.region), @intCast(binding.length), @intFromPtr(ctx.owner), @intCast(ctx.owner.active_length)) or c.overlap(@intFromPtr(binding.region), @intCast(binding.length), @intFromPtr(work.ptr), work.len)) return error.Invalid;
    for (ctx.owner.bindings) |origin| {
        if (roleFor(ctx, origin.source_id).flags & 1 != 0 and origin.access_region != 0) return error.Invalid;
        const b = origin.table;
        inline for (.{ "rows_source", "value_source", "health_source", "provenance_source" }) |field| try separateInventory(binding, b.region, b.byte_length, @field(b, field));
    }
    for (ctx.owner.results[0..ctx.owner.result_count]) |b| {
        inline for (.{ "rows_source", "value_source", "health_source", "provenance_source" }) |field| try separateInventory(binding, b.region, b.byte_length, @field(b, field));
    }
    if (ctx.cold_binding) |b| try separateInventory(binding, b.region, b.length, b.source);
    if (ctx.full.admission) |admission| {
        const vector = admission.binding;
        inline for (.{ "rows_source", "value_source", "health_source" }) |field| try separateInventory(binding, vector.region, vector.byte_length, @field(vector, field));
        if (binding.region == vector.region) {
            const region: []align(8) u8 = @alignCast(binding.region.?[0..@intCast(binding.length)]);
            const layout = ss.layout.Layout.load(region) catch return error.Invalid;
            for (binding.positions) |position| {
                const spec = layout.checkedSpec(region, position) catch return error.Invalid;
                for (vector.positions) |other| {
                    const other_spec = layout.checkedSpec(region, other) catch return error.Invalid;
                    if (position == other or c.overlap(spec.offset, spec.size, other_spec.offset, other_spec.size)) return error.Invalid;
                }
            }
        }
    }
}
fn activeSize(sizes: Sizes) c.Error!u64 {
    var bytes: u64 = 0;
    try add(&bytes, Context, 1);
    try add(&bytes, u8, sizes.canonical_bytes);
    try add(&bytes, sel.ProviderState, sizes.source_capacity);
    try add(&bytes, SourceRole, sizes.source_capacity);
    try add(&bytes, u32, sizes.rule_capacity);
    try add(&bytes, u64, sizes.rule_capacity);
    try add(&bytes, u32, sizes.rule_capacity);
    try add(&bytes, c.Ref, sizes.rule_capacity);
    try add(&bytes, u8, sizes.learned_text_bytes);
    return try align8(bytes);
}
pub fn measure(candidate: c.Compiled, compiler_work: u64, learned_text: u64, canonical_reserve: u64, input_bytes: u64, failure: *p.Failure) c.Error!Sizes {
    failure.* = .{};
    const required = try compactSize(candidate);
    if (canonical_reserve != 0 and canonical_reserve < required) return c.fail(failure, .capacity_exceeded, .active, required, canonical_reserve);
    if (learned_text > std.math.maxInt(u32)) return c.fail(failure, .capacity_exceeded, .payload, learned_text, std.math.maxInt(u32));
    var map_bytes: u64 = 0;
    var i: u32 = 0;
    while (i < candidate.handles.count) : (i += 1) map_bytes += (try c.get(candidate.text, candidate.handles.keys.?[i])).len;
    var out = Sizes{ .canonical_bytes = if (canonical_reserve == 0) required else canonical_reserve, .learned_text_bytes = learned_text, .source_capacity = candidate.sources.count, .rule_capacity = candidate.bindings.count, .compiler_work_bytes = std.math.add(u64, compiler_work, @as(u64, candidate.handles.count) * 32) catch return error.Exhausted, .input_text_bytes = @max(input_bytes, candidate.fact_text.length) };
    out.extra_active_bytes = try activeSize(out);
    var prefix: u64 = @sizeOf(Operation) + 4096;
    // Replacement can simultaneously hold the caller input prefix, its raw
    // text rebased with the prior stable map, and that map's Ref columns.
    inline for (.{ out.compiler_work_bytes, out.input_text_bytes, out.input_text_bytes, map_bytes, @as(u64, candidate.handles.count) * @sizeOf(c.Ref), out.canonical_bytes, try sel.required(candidate), try sel.observeRequired(candidate), try learningSize(out.rule_capacity, learned_text) }) |part| prefix = std.math.add(u64, prefix, part) catch return error.Exhausted;
    prefix = std.math.add(u64, prefix, @as(u64, out.source_capacity) * 160 + @as(u64, out.rule_capacity) * 160) catch return error.Exhausted;
    out.extra_operation_work_bytes = try align8(prefix);
    out.extra_operation_work_bytes = std.math.add(u64, out.extra_operation_work_bytes, sdk.requiredSize(candidate.bindings.count, candidate.sources.count) catch return error.Exhausted) catch return error.Exhausted;
    return out;
}
pub fn bind(owner: *st.Owner, tail: []align(8) u8, sizes: Sizes, cooldown_ms: u64, failure: *p.Failure) c.Error!*Context {
    return bindLayout(owner, tail, sizes, cooldown_ms, 0, failure);
}
fn bindLayout(owner: *st.Owner, tail: []align(8) u8, sizes: Sizes, cooldown_ms: u64, topology_bytes: u64, failure: *p.Failure) c.Error!*Context {
    if (owner.magic != st.Owner.live_magic or cooldown_ms > std.math.maxInt(i64)) return error.Invalid;
    if (sizes.source_capacity > owner.sources.len or sizes.rule_capacity > owner.rules.len) return error.Invalid;
    const required = std.math.add(u64, try activeSize(sizes), topology_bytes) catch return error.Exhausted;
    if (sizes.extra_active_bytes != required or tail.len < required) return c.fail(failure, .capacity_exceeded, .active, required, tail.len);
    const expected = try align8(owner.sizes.active);
    const active_total = std.math.add(u64, expected, required) catch return error.Exhausted;
    const work_total = std.math.add(u64, try align8(owner.sizes.work), sizes.extra_operation_work_bytes) catch return error.Exhausted;
    if (active_total > owner.config.active_limit) return c.fail(failure, .capacity_exceeded, .active, active_total, owner.config.active_limit);
    if (work_total > owner.config.work_limit) return c.fail(failure, .capacity_exceeded, .work, work_total, owner.config.work_limit);
    if (@intFromPtr(tail.ptr) != @intFromPtr(owner) + expected or @as(u128, expected) + tail.len > owner.active_length) return error.Invalid;
    var fba = std.heap.FixedBufferAllocator.init(tail);
    const a = fba.allocator();
    const ctx = a.create(Context) catch return error.Work;
    const canonical = a.alignedAlloc(u8, .@"8", @intCast(sizes.canonical_bytes)) catch return error.Work;
    const providers = a.alloc(sel.ProviderState, sizes.source_capacity) catch return error.Work;
    const roles = a.alloc(SourceRole, sizes.source_capacity) catch return error.Work;
    @memset(roles, .{});
    const resources = a.alignedAlloc(u32, .@"8", sizes.rule_capacity) catch return error.Work;
    @memset(resources, 0);
    const epochs = a.alloc(u64, sizes.rule_capacity) catch return error.Work;
    const availability = a.alignedAlloc(u32, .@"8", sizes.rule_capacity) catch return error.Work;
    const quantities = a.alignedAlloc(c.Ref, .@"8", sizes.rule_capacity) catch return error.Work;
    const text = a.alignedAlloc(u8, .@"8", @intCast(sizes.learned_text_bytes)) catch return error.Work;
    @memset(providers, .{ .incarnation = 1, .capability = 1, .observation = 0 });
    @memset(epochs, 0);
    @memset(availability, 0);
    @memset(quantities, .{});
    ctx.* = .{ .owner = owner, .sizes = sizes, .canonical = canonical, .providers = providers, .roles = roles, .resources = resources, .cooldown_ms = cooldown_ms, .learned_text = text, .learned = .{ .count = 0, .epochs = epochs.ptr, .availability = availability.ptr, .quantities = quantities.ptr, .text = .{ .bytes = text.ptr, .length = 0 } } };
    if (topology_bytes != 0) ctx.full.memory = a.alignedAlloc(u8, .@"8", @intCast(topology_bytes)) catch return error.Work;
    return ctx;
}
fn valid(ctx: *const Context) bool {
    return ctx.magic == Context.live_magic and ctx.owner.magic == st.Owner.live_magic and (ctx.catalog_generation == 0 or ctx.catalog_generation == ctx.owner.catalog_generation);
}
fn workspace(ctx: *Context, bytes: []align(8) u8, failure: *p.Failure) c.Error!struct { prefix: []align(8) u8, kernel: []align(8) u8 } {
    if (!valid(ctx)) return error.Identity;
    if (bytes.len > ctx.owner.config.work_limit) return c.fail(failure, .capacity_exceeded, .work, bytes.len, ctx.owner.config.work_limit);
    const prefix = ctx.sizes.extra_operation_work_bytes;
    if (prefix % 8 != 0) return error.Invalid;
    const required = std.math.add(u64, prefix, ctx.owner.sizes.work) catch return error.Exhausted;
    if (bytes.len < required) return c.fail(failure, .workspace_unavailable, .work, required, bytes.len);
    if (!st.validWork(ctx.owner, bytes)) return error.Invalid;
    return .{ .prefix = bytes[0..@intCast(prefix)], .kernel = @alignCast(bytes[@intCast(prefix)..]) };
}
fn inputRange(pointer: usize, length: usize, bytes: []align(8) u8, reserved: u64) bool {
    if (!c.overlap(pointer, length, @intFromPtr(bytes.ptr), bytes.len)) return true;
    return pointer >= @intFromPtr(bytes.ptr) and @as(u128, pointer) + length <= @as(u128, @intFromPtr(bytes.ptr)) + reserved;
}
fn inputColumns(comptime T: type, value: T, bytes: []align(8) u8, reserved: u64) bool {
    inline for (@typeInfo(T).@"struct".field_names, @typeInfo(T).@"struct".field_types) |field_name, field_type| if (@typeInfo(field_type) == .optional) {
        if (@field(value, field_name)) |pointer| {
            const Element = @typeInfo(@TypeOf(pointer)).pointer.child;
            if (!inputRange(@intFromPtr(pointer), @as(usize, columnCount(T, value, field_name)) * @sizeOf(Element), bytes, reserved)) return false;
        }
    };
    return true;
}
fn copyLearning(ctx: *Context, facts: sel.ReadingFacts) void {
    const n = facts.count;
    @memcpy(@constCast(ctx.learned.epochs.?)[0..n], facts.epochs.?[0..n]);
    @memcpy(@constCast(ctx.learned.availability.?)[0..n], facts.availability.?[0..n]);
    @memcpy(@constCast(ctx.learned.quantities.?)[0..n], facts.quantities.?[0..n]);
    @memcpy(ctx.learned_text[0..facts.text.length], facts.text.bytes.?[0..facts.text.length]);
    ctx.learned.count = n;
    ctx.learned.text.length = facts.text.length;
    ctx.learned.integrated_gpu = facts.integrated_gpu;
}
pub fn catalogDeclaration(candidate: c.Compiled) c.Error!CatalogDeclaration {
    var bytes: u64 = 0;
    var i: u32 = 0;
    while (i < candidate.bindings.count) : (i += 1) {
        bytes += (try c.get(candidate.text, candidate.bindings.field_names.?[i])).len;
        bytes += (try c.get(candidate.fact_text, candidate.sources.names.?[candidate.bindings.source_indices.?[i]])).len;
    }
    if (bytes > std.math.maxInt(u32)) return error.Payload;
    return .{ .rows = candidate.bindings.count, .string_bytes = @intCast(bytes) };
}
const ColdTable = struct {
    binding: CatalogBinding,
    region: []align(8) u8,
    layout: ss.layout.Layout,
    fn load(table: ColdTable, comptime T: type, slot: u32, row: u32) T {
        const spec = table.layout.checkedSpec(table.region, table.binding.positions[slot]) catch unreachable;
        const pointer: *const T = @ptrCast(@alignCast(table.layout.dataArea(table.region).ptr + spec.offset + @as(usize, row) * @sizeOf(T)));
        return @atomicLoad(T, pointer, .acquire);
    }
    fn unchanged(table: ColdTable, candidate: c.Compiled) c.Error!bool {
        const source = table.layout.source(table.region, table.binding.source);
        if (@atomicLoad(ss.SourceState, &source.state, .acquire) != .has_value) return false;
        const sequence = @atomicLoad(u64, &source.sequence, .acquire);
        var slot: u32 = 0;
        while (slot < 6) : (slot += 1) {
            const position = table.layout.position(table.region, table.binding.positions[slot]);
            if (@atomicLoad(u32, &position.length, .acquire) != candidate.bindings.count) return false;
            if (slot != 0 and @atomicLoad(u64, &position.identity_sequence, .acquire) != sequence) return error.Invalid;
        }
        const spec = table.layout.checkedSpec(table.region, table.binding.positions[6]) catch return error.Invalid;
        const used = @atomicLoad(u32, &table.layout.position(table.region, table.binding.positions[6]).length, .acquire);
        if (used > spec.size) return error.Invalid;
        const text = c.Text{ .bytes = table.layout.dataArea(table.region).ptr + spec.offset, .length = used };
        var i: u32 = 0;
        while (i < candidate.bindings.count) : (i += 1) {
            if (table.load(u64, 0, i) != @as(u64, i) + 1 or table.load(u32, 1, i) != candidate.bindings.field_codes.?[i] or table.load(u32, 2, i) != candidate.catalog.rules.kinds.?[i] or table.load(u32, 3, i) != candidate.catalog.rules.value_kinds.?[i]) return false;
            const field = table.load(u64, 4, i);
            const origin = table.load(u64, 5, i);
            const field_text = try c.get(text, .{ .offset = @truncate(field), .length = @truncate(field >> 32) });
            const origin_text = try c.get(text, .{ .offset = @truncate(origin), .length = @truncate(origin >> 32) });
            if (!c.eql(field_text, try c.get(candidate.text, candidate.bindings.field_names.?[i])) or !c.eql(origin_text, try c.get(candidate.fact_text, candidate.sources.names.?[candidate.bindings.source_indices.?[i]]))) return false;
        }
        return true;
    }
    fn init(binding: CatalogBinding, declaration: CatalogDeclaration, failure: *p.Failure) c.Error!ColdTable {
        const pointer = binding.region orelse return error.Invalid;
        if (binding.reserved != 0 or @intFromPtr(pointer) % 8 != 0 or binding.length > std.math.maxInt(usize)) return error.Invalid;
        const region: []align(8) u8 = @alignCast(pointer[0..@intCast(binding.length)]);
        const layout = ss.layout.Layout.load(region) catch return error.Invalid;
        if (binding.source >= layout.header.source_count) return error.Invalid;
        const sequence = @atomicLoad(u64, &layout.source(region, binding.source).sequence, .acquire);
        if (sequence & (ss.ended_bit | 1) != 0 or sequence > ss.ended_bit - 4) return error.Invalid;
        var slot: u32 = 0;
        while (slot < 7) : (slot += 1) {
            const spec = layout.checkedSpec(region, binding.positions[slot]) catch return error.Invalid;
            if (@backingInt(spec.kind) != declaration.kinds[slot] or spec.element_width != declaration.widths[slot] or spec.source != binding.source or spec.identity != (if (slot == 0 or slot == 6) p.none else binding.positions[0]) or ((slot == 4 or slot == 5) and spec.strings != binding.positions[6])) return error.Invalid;
            const needed = if (slot == 6) declaration.string_bytes else declaration.rows;
            if (spec.size / spec.element_width < needed) {
                failure.* = .{ .code = if (slot == 6) .payload_capacity else .capacity_exceeded, .family = if (slot == 6) .payload else .binding, .required = needed, .limit = spec.size / spec.element_width };
                return if (slot == 6) error.Payload else error.Capacity;
            }
            var prior: u32 = 0;
            while (prior < slot) : (prior += 1) {
                const previous = layout.checkedSpec(region, binding.positions[prior]) catch return error.Invalid;
                if (binding.positions[slot] == binding.positions[prior] or c.overlap(spec.offset, spec.size, previous.offset, previous.size)) return error.Invalid;
            }
        }
        return .{ .binding = binding, .region = region, .layout = layout };
    }
    fn publish(table: ColdTable, writer: *ss.Writer, candidate: c.Compiled, utc: i64) void {
        const positions = table.binding.positions;
        writer.setLength(positions[6], 0, 0) catch unreachable;
        var i: u32 = 0;
        while (i < candidate.bindings.count) : (i += 1) {
            const ordinal: u64 = @as(u64, i) + 1;
            writer.store(u64, positions[0], i, (&ordinal)[0..1]) catch unreachable;
            const code = candidate.bindings.field_codes.?[i];
            const kind = candidate.catalog.rules.kinds.?[i];
            const value = candidate.catalog.rules.value_kinds.?[i];
            writer.store(u32, positions[1], i, (&code)[0..1]) catch unreachable;
            writer.store(u32, positions[2], i, (&kind)[0..1]) catch unreachable;
            writer.store(u32, positions[3], i, (&value)[0..1]) catch unreachable;
            writer.appendString(positions[4], i, c.get(candidate.text, candidate.bindings.field_names.?[i]) catch unreachable) catch unreachable;
            writer.appendString(positions[5], i, c.get(candidate.fact_text, candidate.sources.names.?[candidate.bindings.source_indices.?[i]]) catch unreachable) catch unreachable;
        }
        var slot: u32 = 0;
        while (slot < 6) : (slot += 1) writer.setLength(positions[slot], candidate.bindings.count, if (slot == 0) 0 else writer.roundSequence()) catch unreachable;
        writer.end(.has_value, utc) catch unreachable;
    }
};
fn coldSeparate(table: ColdTable, region: ?[*]u8, length: u64, sources: []const u32, positions: []const u32) c.Error!void {
    if (table.binding.region != region) {
        if (c.overlap(@intFromPtr(table.binding.region), @intCast(table.binding.length), @intFromPtr(region), @intCast(length))) return error.Invalid;
        return;
    }
    if (table.binding.length != length) return error.Invalid;
    for (sources) |source| if (table.binding.source == source) return error.Invalid;
    for (table.binding.positions) |position| {
        const spec = table.layout.checkedSpec(table.region, position) catch return error.Invalid;
        for (positions) |other| {
            if (other == p.none) continue;
            const other_spec = table.layout.checkedSpec(table.region, other) catch return error.Invalid;
            if (position == other or c.overlap(spec.offset, spec.size, other_spec.offset, other_spec.size)) return error.Invalid;
        }
    }
}
fn preservation(ctx: *Context, candidate: c.Compiled, initial: ?*const ProviderInputs, a: std.mem.Allocator, failure: *p.Failure) c.Error!struct { providers: []sel.ProviderState, learned: sel.ReadingFacts } {
    if (initial) |inputs| {
        if (inputs.reserved != 0 or inputs.count > candidate.sources.count or (inputs.count != 0 and (inputs.source_ids == null or inputs.states == null))) return error.Invalid;
        var at: u32 = 0;
        while (at < inputs.count) : (at += 1) {
            var previous: u32 = 0;
            while (previous < at) : (previous += 1) if (inputs.source_ids.?[previous] == inputs.source_ids.?[at]) return error.Identity;
        }
    }
    const providers = a.alloc(sel.ProviderState, candidate.sources.count) catch return error.Work;
    const epochs = a.alloc(u64, candidate.bindings.count) catch return error.Work;
    const availability = a.alloc(u32, candidate.bindings.count) catch return error.Work;
    const quantities = a.alloc(c.Ref, candidate.bindings.count) catch return error.Work;
    const text = a.alloc(u8, @intCast(ctx.sizes.learned_text_bytes)) catch return error.Work;
    var writer = TextWriter{ .bytes = text };
    var i: u32 = 0;
    while (i < providers.len) : (i += 1) {
        const sources = candidate.sources;
        var state = sel.ProviderState{ .incarnation = sources.incarnations.?[i], .capability = sources.capabilities.?[i], .observation = 0, .unsupported = @intFromBool(sources.availability.?[i] == 3) };
        var found = false;
        var old: u32 = 0;
        while (old < ctx.compiled.sources.count) : (old += 1) if (ctx.compiled.sources.ids.?[old] == sources.ids.?[i] and ctx.providers[old].incarnation == state.incarnation and ctx.providers[old].capability == state.capability) {
            state = ctx.providers[old];
            found = true;
            break;
        };
        if (!found) {
            var seeded = false;
            if (initial) |inputs| {
                var seed: u32 = 0;
                while (seed < inputs.count) : (seed += 1) if (inputs.source_ids.?[seed] == sources.ids.?[i]) {
                    state = inputs.states.?[seed];
                    seeded = true;
                    if (state.reserved != 0 or state.incarnation != sources.incarnations.?[i] or state.capability != sources.capabilities.?[i] or state.unsupported > 1 or state.unavailable_until_ms < 0) return error.Identity;
                    if (sources.availability.?[i] == 3 and state.unsupported == 0) return error.Identity;
                    if (sources.availability.?[i] == 2 and state.unavailable_until_ms == 0) return error.Identity;
                };
            }
            if (!seeded and sources.availability.?[i] == 2) return error.Invalid;
        }
        providers[i] = state;
    }
    i = 0;
    while (i < candidate.bindings.count) : (i += 1) {
        const source = candidate.bindings.source_indices.?[i];
        epochs[i] = candidate.sources.capabilities.?[source];
        availability[i] = 0;
        quantities[i] = .{};
        var old: u32 = 0;
        while (old < ctx.compiled.bindings.count) : (old += 1) {
            const os = ctx.compiled.bindings.source_indices.?[old];
            if (candidate.catalog.rules.ids.?[i] != ctx.compiled.catalog.rules.ids.?[old] or candidate.catalog.rules.identities.?[i] != ctx.compiled.catalog.rules.identities.?[old] or candidate.sources.ids.?[source] != ctx.compiled.sources.ids.?[os] or candidate.sources.incarnations.?[source] != ctx.providers[os].incarnation or epochs[i] != ctx.providers[os].capability) continue;
            const mi = candidate.bindings.metric_indices.?[i];
            const omi = ctx.compiled.bindings.metric_indices.?[old];
            if (candidate.catalog.metrics.ids.?[mi] != ctx.compiled.catalog.metrics.ids.?[omi] or candidate.catalog.metrics.identities.?[mi] != ctx.compiled.catalog.metrics.identities.?[omi] or candidate.bindings.field_codes.?[i] != ctx.compiled.bindings.field_codes.?[old]) continue;
            var same_rule = true;
            inline for (.{ "kinds", "flags", "priorities", "value_kinds", "minimum_bits", "maximum_bits", "capability_masks" }) |field| {
                if (@field(candidate.catalog.rules, field).?[i] != @field(ctx.compiled.catalog.rules, field).?[old]) same_rule = false;
            }
            if (!same_rule) continue;
            availability[i] = ctx.learned.availability.?[old];
            const value = try c.get(ctx.learned.text, ctx.learned.quantities.?[old]);
            if (value.len > text.len - writer.used) return c.fail(failure, .payload_capacity, .payload, @as(u64, writer.used) + value.len, text.len);
            quantities[i] = writer.put(value);
            break;
        }
    }
    var integrated: c.Ref = .{};
    if (ctx.catalog_generation != 0) {
        const value = try c.get(ctx.learned.text, ctx.learned.integrated_gpu);
        var still_present = false;
        i = 0;
        while (i < candidate.scopes.count) : (i += 1) if (candidate.scopes.expansions.?[i] == 4 and c.eql(value, try c.get(candidate.fact_text, candidate.scopes.keys.?[i]))) {
            still_present = true;
        };
        if (still_present) {
            if (value.len > text.len - writer.used) return error.Payload;
            integrated = writer.put(value);
        }
    }
    return .{ .providers = providers, .learned = .{ .count = candidate.bindings.count, .epochs = epochs.ptr, .availability = availability.ptr, .quantities = quantities.ptr, .text = .{ .bytes = text.ptr, .length = writer.used }, .integrated_gpu = integrated } };
}
pub fn replace(ctx: *Context, raw: c.Facts, limits: c.Limits, initial: ?*const ProviderInputs, binding: ?*const CatalogBinding, bytes: []align(8) u8, input_prefix: u64, utc: i64, topology: ?*const dv.v.ScopeTopology, failure: *p.Failure) c.Error!void {
    failure.* = .{};
    ctx.owner.detach();
    ctx.operation_token = 0;
    _ = try c.get(raw.text, .{});
    const split = try workspace(ctx, bytes, failure);
    if (ctx.full.enabled != (topology != null)) return error.Invalid;
    if (topology) |t| {
        if (t.count > ctx.full.extra.scope_count) return c.fail(failure, .capacity_exceeded, .binding, t.count, ctx.full.extra.scope_count);
        try dv.validate(t.*, raw.scopes, raw.text);
        const text_bytes = try dv.topologyTextBytes(t.*);
        if (text_bytes > ctx.full.extra.topology_text_bytes) return c.fail(failure, .payload_capacity, .payload, text_bytes, ctx.full.extra.topology_text_bytes);
        const needed = try dv.topologySize(t.*);
        if (needed > ctx.full.memory.len) return c.fail(failure, .capacity_exceeded, .active, needed, ctx.full.memory.len);
        if (!inputRange(@intFromPtr(t.text.bytes), t.text.length, bytes, input_prefix) or !inputColumns(dv.v.ScopeTopology, t.*, bytes, input_prefix)) return error.Invalid;
        if (!c.disjointColumns(dv.v.ScopeTopology, t.*, t.count, @alignCast(@as([*]u8, @ptrCast(ctx.owner))[0..@intCast(ctx.owner.active_length)])) or c.overlap(@intFromPtr(t.text.bytes), t.text.length, @intFromPtr(ctx.owner), @intCast(ctx.owner.active_length))) return error.Invalid;
    }
    const initial_value: ?ProviderInputs = if (initial) |value| value.* else null;
    const binding_value: ?CatalogBinding = if (binding) |value| value.* else null;
    if (utc < 0 or raw.text.length > ctx.sizes.input_text_bytes) return c.fail(failure, .capacity_exceeded, .work, raw.text.length, ctx.sizes.input_text_bytes);
    if (!inputRange(@intFromPtr(raw.text.bytes), raw.text.length, bytes, input_prefix) or !inputColumns(c.Sources, raw.sources, bytes, input_prefix) or !inputColumns(c.Templates, raw.templates, bytes, input_prefix) or !inputColumns(c.Fields, raw.fields, bytes, input_prefix) or !inputColumns(c.Scopes, raw.scopes, bytes, input_prefix) or !inputColumns(c.Handles, raw.handles, bytes, input_prefix)) return error.Invalid;
    if (initial_value) |value| if (!inputColumns(ProviderInputs, value, bytes, input_prefix)) return error.Invalid;
    var fba = std.heap.FixedBufferAllocator.init(split.prefix);
    const a = fba.allocator();
    if (input_prefix > ctx.sizes.input_text_bytes or input_prefix > split.prefix.len) return error.Invalid;
    // The caller already filled this immutable prefix. alloc() poisons bytes;
    // reserve them by moving the bounded allocator cursor without touching input.
    fba.end_index = @intCast(input_prefix);
    var facts = raw;
    if (ctx.catalog_generation != 0) {
        // The runtime map is authoritative during this run. An immutable seed
        // may be repeated, but conflicting/unknown seed rows cannot be hidden
        // by substituting the active map before validation.
        _ = try c.required(raw, limits, failure);
        var seed: u32 = 0;
        while (seed < raw.handles.count) : (seed += 1) {
            var matched = false;
            var prior: u32 = 0;
            while (prior < ctx.compiled.handles.count) : (prior += 1) {
                if (raw.handles.kinds.?[seed] == ctx.compiled.handles.kinds.?[prior] and raw.handles.ids.?[seed] == ctx.compiled.handles.ids.?[prior] and c.eql(try c.get(raw.text, raw.handles.keys.?[seed]), try c.get(ctx.compiled.text, ctx.compiled.handles.keys.?[prior]))) matched = true;
            }
            if (!matched) return error.Identity;
        }
        var key_bytes: usize = 0;
        var i: u32 = 0;
        while (i < ctx.compiled.handles.count) : (i += 1) key_bytes += (try c.get(ctx.compiled.text, ctx.compiled.handles.keys.?[i])).len;
        const arena = a.alloc(u8, @as(usize, raw.text.length) + key_bytes) catch return error.Work;
        if (raw.text.length != 0) @memcpy(arena[0..raw.text.length], raw.text.bytes.?[0..raw.text.length]);
        var text = TextWriter{ .bytes = arena, .used = raw.text.length };
        const keys = a.alloc(c.Ref, ctx.compiled.handles.count) catch return error.Work;
        i = 0;
        while (i < keys.len) : (i += 1) keys[i] = text.put(try c.get(ctx.compiled.text, ctx.compiled.handles.keys.?[i]));
        facts.text = .{ .bytes = arena.ptr, .length = text.used };
        facts.handles = ctx.compiled.handles;
        facts.handles.keys = keys.ptr;
    }
    const needed = try c.required(facts, limits, failure);
    if (needed > ctx.sizes.compiler_work_bytes) return c.fail(failure, .capacity_exceeded, .work, needed, ctx.sizes.compiler_work_bytes);
    const compile_bytes = a.alignedAlloc(u8, .@"8", @intCast(needed)) catch return error.Work;
    const compiled = try c.compile(facts, limits, compile_bytes, failure);
    var source_at: u32 = 0;
    while (source_at < compiled.sources.count) : (source_at += 1) {
        var old_source: u32 = 0;
        while (old_source < ctx.compiled.sources.count) : (old_source += 1) {
            if (compiled.sources.ids.?[source_at] == ctx.compiled.sources.ids.?[old_source] and !c.eql(try c.get(compiled.fact_text, compiled.sources.names.?[source_at]), try c.get(ctx.compiled.fact_text, ctx.compiled.sources.names.?[old_source]))) return error.Identity;
        }
    }
    const compact_bytes = try compactSize(compiled);
    if (compact_bytes > ctx.canonical.len) return c.fail(failure, .capacity_exceeded, .active, compact_bytes, ctx.canonical.len);
    const snapshot = a.alignedAlloc(u8, .@"8", @intCast(compact_bytes)) catch return error.Work;
    const candidate = compact(compiled, snapshot);
    try validateRoles(candidate, currentRoles(ctx), failure);
    if (ctx.full.enabled) {
        const roles = currentRoles(ctx);
        const role_rows: []const SourceRole = if (roles.count == 0) &.{} else roles.rows.?[0..roles.count];
        try dv.validateRoles(role_rows, ctx.full.admission);
        var vector_required = try dv.workBound(candidate, role_rows);
        if (ctx.full.admission) |bound| {
            var raw_rows: u32 = 0;
            for (role_rows) |role| if (role.flags & 2 != 0) {
                raw_rows = role.row_capacity;
            };
            const gpu_required = dv.v.discoveryGpuWorkSizeChecked(bound.binding, raw_rows, bound.bound_row_capacity, candidate.catalog.metrics.count, ctx.owner.config.work_limit, failure) catch return error.Invalid;
            vector_required = std.math.add(u64, vector_required, gpu_required) catch return error.Exhausted;
        }
        vector_required = try align8(vector_required);
        if (vector_required > ctx.full.extra.vector_work_bytes) return c.fail(failure, .capacity_exceeded, .work, vector_required, ctx.full.extra.vector_work_bytes);
        const grouping_required = sdk.requiredSize(candidate.bindings.count, candidate.sources.count) catch return error.Exhausted;
        if (grouping_required > ctx.full.extra.grouping_work_bytes) return c.fail(failure, .capacity_exceeded, .work, grouping_required, ctx.full.extra.grouping_work_bytes);
        if (ctx.full.admission) |bound| if (ctx.owner.gpu_vector == null) {
            dv.v.discoveryValidateGpuBinding(ctx.owner, bound.binding, bound.binding.source_id) catch |err| {
                failure.* = .{ .code = if (err == error.Capacity) .capacity_exceeded else if (err == error.Shared) .shared_section else .invalid_contract, .family = .binding };
                return error.Invalid;
            };
        };
    }
    const resources = a.alloc(u32, candidate.bindings.count) catch return error.Work;
    for (resources, 0..) |*resource, ri| {
        resource.* = 0;
        var fi: u32 = 0;
        while (fi < raw.fields.count) : (fi += 1) if (raw.fields.codes.?[fi] == candidate.bindings.field_codes.?[ri]) {
            if (resource.* != 0 and resource.* != raw.fields.resources.?[fi]) return error.Identity;
            resource.* = raw.fields.resources.?[fi];
        };
    }
    if (candidate.sources.count > ctx.providers.len or candidate.bindings.count > ctx.sizes.rule_capacity) return c.fail(failure, .capacity_exceeded, .rules, candidate.bindings.count, ctx.sizes.rule_capacity);
    const continuity = try preservation(ctx, candidate, if (initial_value) |*value| value else null, a, failure);
    const cold = if (binding_value) |value| try ColdTable.init(value, try catalogDeclaration(candidate), failure) else null;
    if (ctx.inventory_binding) |inventory| {
        try inventoryExclusive(ctx, inventory, bytes);
        if (binding_value) |value| try separateInventory(inventory, value.region, value.length, value.source);
    }
    const cold_changed = if (cold) |table| !try table.unchanged(candidate) else false;
    if (cold) |table| {
        if (c.overlap(@intFromPtr(table.region.ptr), table.region.len, @intFromPtr(bytes.ptr), bytes.len) or c.overlap(@intFromPtr(table.region.ptr), table.region.len, @intFromPtr(ctx.owner), @intCast(ctx.owner.active_length))) return error.Invalid;
        if (ctx.inventory_binding) |inventory| try coldSeparate(table, inventory.region, inventory.length, &.{inventory.source}, &inventory.positions);
        if (ctx.full.admission) |admission| {
            const vector = admission.binding;
            try coldSeparate(table, vector.region, vector.byte_length, &.{ vector.rows_source, vector.value_source, vector.health_source }, &vector.positions);
        }
        var source_index: usize = 0;
        while (source_index < ctx.owner.sources.len) : (source_index += 1) {
            const bound = ctx.owner.sources[source_index].binding;
            if (bound.region == table.binding.region and (table.binding.source == bound.rows_source or table.binding.source == bound.value_source or table.binding.source == bound.health_source or table.binding.source == bound.provenance_source)) return error.Invalid;
        }
        var group: u32 = 0;
        while (group < ctx.owner.result_count) : (group += 1) {
            const bound = ctx.owner.results[group];
            if (bound.region == table.binding.region and (table.binding.source == bound.rows_source or table.binding.source == bound.value_source or table.binding.source == bound.health_source or table.binding.source == bound.provenance_source)) return error.Invalid;
        }
    }
    const code = kernel_catalog.replace(ctx.owner, candidate.catalog, split.kernel, utc);
    if (code != .ok) {
        failure.* = ctx.owner.failure;
        return error.Invalid;
    }
    ctx.compiled = compact(candidate, ctx.canonical);
    if (topology) |t| ctx.full.topology = dv.compact(t.*, ctx.full.memory);
    @memcpy(ctx.resources[0..resources.len], resources);
    @memcpy(ctx.providers[0..continuity.providers.len], continuity.providers);
    copyLearning(ctx, continuity.learned);
    ctx.catalog_generation = ctx.owner.catalog_generation;
    if (ctx.full.admission) |bound| if (ctx.owner.gpu_vector == null) {
        // The pure vector preflight above proved this exact descriptor/source;
        // catalog admission is serialized with creator lifetime and writers.
        var bind_failure: p.Failure = .{};
        const bind_code = dv.v.rm_metric_vector_gpu_bind(ctx.owner, &bound.binding, &bind_failure);
        if (bind_code != 0) unreachable;
    };
    ctx.cold_binding = binding_value;
    ctx.operation_token = 0;
    // ColdTable.init preflighted every Writer.begin condition. This source is
    // exclusive and creator end/unmap is serialized across the whole call.
    // Begin only after kernel acceptance: rejected replacement leaves even the
    // cold source's sequence and identity stamps untouched.
    if (cold_changed) {
        const table = cold.?;
        var writer = ss.Writer.begin(table.region, table.binding.source) catch unreachable;
        table.publish(&writer, ctx.compiled, utc);
    }
}
pub fn plan(ctx: *Context, request: sel.Request, bytes: []align(8) u8, input_prefix: u64, utc: i64, failure: *p.Failure) c.Error!Plan {
    failure.* = .{};
    ctx.owner.detach();
    ctx.operation_token = 0;
    const split = try workspace(ctx, bytes, failure);
    if (ctx.catalog_generation == 0 or utc < 0 or input_prefix > ctx.sizes.input_text_bytes) return error.Invalid;
    if (!inputRange(@intFromPtr(request.text.bytes), request.text.length, bytes, input_prefix) or !inputRange(@intFromPtr(request.keys), @as(usize, request.count) * @sizeOf(c.Ref), bytes, input_prefix)) return error.Invalid;
    var fba = std.heap.FixedBufferAllocator.init(split.prefix);
    const a = fba.allocator();
    if (input_prefix > split.prefix.len) return c.fail(failure, .workspace_unavailable, .work, input_prefix, split.prefix.len);
    fba.end_index = @intCast(input_prefix);
    const op = a.create(Operation) catch return error.Work;
    const providers = a.alloc(sel.ProviderState, ctx.compiled.sources.count) catch return error.Work;
    const current = a.alloc(u64, providers.len) catch return error.Work;
    const expected_sources = a.alloc(u32, providers.len) catch return error.Work;
    const source_indices = a.alloc(u32, providers.len) catch return error.Work;
    const source_status = a.alloc(u32, providers.len) catch return error.Work;
    const cooldown_flags = a.alloc(u32, providers.len) catch return error.Work;
    const completed = a.alloc(i64, providers.len) catch return error.Work;
    const source_ids = a.alloc(u64, providers.len) catch return error.Work;
    const source_observations = a.alloc(u64, providers.len) catch return error.Work;
    const source_roles = a.alloc(u32, providers.len) catch return error.Work;
    var i: u32 = 0;
    while (i < providers.len) : (i += 1) {
        providers[i] = ctx.providers[i];
        current[i] = providers[i].observation;
        const availability = sel.providerAvailability(providers[i], utc);
        ctx.owner.sources[i].availability = availability;
        @constCast(ctx.compiled.sources.availability.?)[i] = availability;
    }
    const select_bytes = a.alignedAlloc(u8, .@"8", @intCast(try sel.required(ctx.compiled))) catch return error.Work;
    var selection = try sel.select(ctx.compiled, ctx.learned, request, select_bytes, failure);
    const initial_due: []const u32 = if (selection.metric_count == 0) &.{} else selection.metric_indices.?[0..selection.metric_count];
    for (ctx.roles) |role| if (role.flags & 14 != 0 and !ctx.full.enabled) return error.Invalid;
    var expansion = try dv.expand(ctx.owner, &ctx.full, ctx.compiled, ctx.roles, initial_due, request, a, ctx.sizes.input_text_bytes, failure);
    var include_inventory = selection.dataset_mask & 2 != 0 or request.flags & 2 != 0;
    i = 0;
    while (i < ctx.compiled.bindings.count) : (i += 1) if (ctx.resources[i] == 4) {
        var selected: u32 = 0;
        while (selected < expansion.metrics.len) : (selected += 1) if (expansion.metrics[selected] == ctx.compiled.bindings.metric_indices.?[i]) {
            include_inventory = true;
        };
    };
    var inventory_source: u32 = p.none;
    var inventory_role: SourceRole = .{};
    if (include_inventory) {
        selection.dataset_mask |= 2;
        var declared = false;
        i = 0;
        while (i < ctx.compiled.sources.count) : (i += 1) {
            const role = roleFor(ctx, ctx.compiled.sources.ids.?[i]);
            if (role.flags & 1 == 0) continue;
            declared = true;
            if (ctx.compiled.sources.availability.?[i] == 1 and !ctx.owner.sources[i].suppressed) {
                inventory_source = i;
                inventory_role = role;
            }
        }
        if (!declared or ctx.inventory_binding == null) return error.Invalid;
        try inventoryExclusive(ctx, ctx.inventory_binding.?, bytes);
    }
    var raw: p.Round = .{};
    const code = kernel_round.plan(ctx.owner, expansion.metrics, split.kernel, utc, &raw);
    if (code != .ok) {
        failure.* = ctx.owner.failure;
        return error.Invalid;
    }
    var success = false;
    defer if (!success) ctx.owner.detach();
    var source_count: u32 = 0;
    i = 0;
    while (i < raw.count) : (i += 1) {
        const ri = raw.observations.rule_indices.?[i];
        if (dv.syntheticRule(&expansion.round, ri)) continue;
        const si = ctx.compiled.bindings.source_indices.?[ri];
        var found = false;
        var j: u32 = 0;
        while (j < source_count) : (j += 1) if (expected_sources[j] == si) {
            found = true;
        };
        if (!found) {
            expected_sources[source_count] = si;
            source_indices[source_count] = si;
            source_status[source_count] = 0;
            cooldown_flags[source_count] = 0;
            completed[source_count] = utc;
            source_count += 1;
        }
    }
    if (inventory_source != p.none) {
        var found = false;
        for (expected_sources[0..source_count]) |si| if (si == inventory_source) {
            found = true;
        };
        if (!found) {
            expected_sources[source_count] = inventory_source;
            source_indices[source_count] = inventory_source;
            source_status[source_count] = 0;
            cooldown_flags[source_count] = 0;
            completed[source_count] = utc;
            source_count += 1;
        }
    }
    i = 0;
    while (i < source_count) : (i += 1) if (ctx.providers[expected_sources[i]].observation == std.math.maxInt(u64)) return c.fail(failure, .capacity_exceeded, .generation, std.math.maxInt(u64), std.math.maxInt(u64) - 1);
    // Observation sequence is admission continuity, matching old BeginObservation:
    // it advances even if this round is later rejected; support/cooldown does not.
    i = 0;
    while (i < source_count) : (i += 1) {
        const si = expected_sources[i];
        current[si] = try sel.providerBegin(&ctx.providers[si], utc);
        providers[si] = ctx.providers[si];
        source_ids[i] = ctx.compiled.sources.ids.?[si];
        source_observations[i] = current[si];
        source_roles[i] = roleFor(ctx, source_ids[i]).flags;
    }
    var npu_observation: u64 = 0;
    for (source_ids[0..source_count], source_observations[0..source_count]) |id, observation| if (id == expansion.round.npu_source) {
        npu_observation = observation;
    };
    try dv.attachNpu(ctx.owner, &expansion.round, npu_observation, failure);
    @memcpy(ctx.owner.work.requested[0..initial_due.len], initial_due);
    ctx.owner.work.request_count = @intCast(initial_due.len);
    var dispatch = Dispatch{ .count = raw.count };
    dispatch.field_codes = (a.alloc(u32, raw.count) catch return error.Work).ptr;
    dispatch.scope_indices = (a.alloc(u32, raw.count) catch return error.Work).ptr;
    dispatch.display_indices = (a.alloc(u32, raw.count) catch return error.Work).ptr;
    dispatch.scope_ids = (a.alloc(u64, raw.count) catch return error.Work).ptr;
    dispatch.field_names = (a.alloc(c.Ref, raw.count) catch return error.Work).ptr;
    dispatch.device_keys = (a.alloc(c.Ref, raw.count) catch return error.Work).ptr;
    const dispatch_text = a.alloc(u8, ctx.compiled.text.length) catch return error.Work;
    var text = TextWriter{ .bytes = dispatch_text };
    var readings = sel.ReadingEvents{ .count = raw.count, .current_observations = current.ptr };
    readings.rule_indices = (a.alloc(u32, raw.count) catch return error.Work).ptr;
    readings.incarnations = (a.alloc(u64, raw.count) catch return error.Work).ptr;
    readings.capabilities = (a.alloc(u64, raw.count) catch return error.Work).ptr;
    readings.observations = (a.alloc(u64, raw.count) catch return error.Work).ptr;
    readings.statuses = (a.alloc(u32, raw.count) catch return error.Work).ptr;
    readings.supported = (a.alloc(u32, raw.count) catch return error.Work).ptr;
    readings.source_unsupported = (a.alloc(u32, raw.count) catch return error.Work).ptr;
    readings.quantities = (a.alloc(c.Ref, raw.count) catch return error.Work).ptr;
    readings.text = .{ .bytes = (a.alloc(u8, @intCast(ctx.sizes.learned_text_bytes)) catch return error.Work).ptr, .length = @intCast(ctx.sizes.learned_text_bytes) };
    i = 0;
    while (i < raw.count) : (i += 1) {
        const ri = raw.observations.rule_indices.?[i];
        const b = ctx.compiled.bindings;
        const si = b.source_indices.?[ri];
        @constCast(dispatch.field_codes.?)[i] = b.field_codes.?[ri];
        @constCast(dispatch.scope_indices.?)[i] = b.scope_indices.?[ri];
        @constCast(dispatch.display_indices.?)[i] = b.display_indices.?[ri];
        @constCast(dispatch.scope_ids.?)[i] = b.scope_ids.?[ri];
        @constCast(dispatch.field_names.?)[i] = text.put(c.get(ctx.compiled.text, b.field_names.?[ri]) catch unreachable);
        @constCast(dispatch.device_keys.?)[i] = text.put(c.get(ctx.compiled.text, b.device_keys.?[ri]) catch unreachable);
        @constCast(readings.rule_indices.?)[i] = ri;
        @constCast(readings.incarnations.?)[i] = raw.queries.incarnations.?[i];
        @constCast(readings.capabilities.?)[i] = raw.queries.capabilities.?[i];
        @constCast(readings.observations.?)[i] = current[si];
        @constCast(readings.statuses.?)[i] = 0;
        @constCast(readings.supported.?)[i] = 0;
        @constCast(readings.source_unsupported.?)[i] = 0;
        @constCast(readings.quantities.?)[i] = .{};
    }
    dispatch.text = .{ .bytes = text.bytes.ptr, .length = text.used };
    const learning = a.alignedAlloc(u8, .@"8", @intCast(try sel.observeRequired(ctx.compiled))) catch return error.Work;
    const events = SourceEvents{ .count = source_count, .source_indices = source_indices.ptr, .statuses = source_status.ptr, .apply_cooldown = cooldown_flags.ptr, .completed_ms = completed.ptr, .source_ids = source_ids.ptr, .observations = source_observations.ptr };
    op.* = .{ .context = ctx, .token = raw.token, .catalog_generation = raw.catalog_generation, .source_events = events, .expected_sources = expected_sources[0..source_count], .providers = providers, .source_roles = source_roles[0..source_count], .current_observations = current, .readings = readings, .learning_work = learning, .due = if (selection.metric_count == 0) &.{} else selection.metric_indices.?[0..selection.metric_count], .inventory_source = inventory_source };
    op.vectors = expansion.round;
    try dv.inputs(ctx.owner, &op.vectors, ctx.roles, source_ids[0..source_count], source_observations[0..source_count], ctx.compiled, a, &ctx.full, failure);
    const grouping_size = sdk.requiredSize(raw.count, source_count) catch return error.Exhausted;
    const grouping_work = a.alignedAlloc(u8, .@"8", @intCast(@max(grouping_size, 8))) catch return error.Work;
    const cols = sdk.Columns{ .count = dispatch.count, .field_codes = dispatch.field_codes, .scope_indices = dispatch.scope_indices, .display_indices = dispatch.display_indices, .device_keys = dispatch.device_keys, .text = dispatch.text };
    const omitted: []const u32 = if (op.vectors.inputs.excluded_count == 0) &.{} else op.vectors.inputs.excluded_rows.?[0..op.vectors.inputs.excluded_count];
    op.sdk_grouping = sdk.build(ctx.owner, raw, cols, source_ids[0..source_count], omitted, grouping_work, failure) catch return error.Invalid;
    if (inventory_source != p.none) {
        const header = a.create(InventoryHeader) catch return error.Work;
        header.* = .{ .query_token = raw.token };
        const luids = a.alloc(u64, inventory_role.row_capacity) catch return error.Work;
        const keys = a.alloc(c.Ref, inventory_role.row_capacity) catch return error.Work;
        @memset(luids, 0);
        @memset(keys, .{});
        const input_text = a.alloc(u8, inventory_role.text_capacity) catch return error.Work;
        op.inventory_text = a.alloc(u8, inventory_role.text_capacity) catch return error.Work;
        op.inventory_work = a.alignedAlloc(u8, .@"8", @as(usize, inventory_role.row_capacity) * 112 + 256) catch return error.Work;
        const state = providers[inventory_source];
        op.inventory_input = .{ .source_id = inventory_role.source_id, .incarnation = state.incarnation, .capability = state.capability, .observation = state.observation, .catalog_generation = raw.catalog_generation, .query_token = raw.token, .row_capacity = inventory_role.row_capacity, .text_capacity = inventory_role.text_capacity, .header = header, .luids = luids.ptr, .keys = keys.ptr, .text = .{ .bytes = input_text.ptr, .length = inventory_role.text_capacity } };
    }
    ctx.operation_token = raw.token;
    success = true;
    return .{ .operation = op, .raw = raw, .selection = selection, .dispatch = dispatch, .sources = events, .readings = readings };
}
fn prepareInventory(ctx: *Context, op: *Operation, failure: *p.Failure) c.Error!void {
    if (op.inventory_source == p.none) return;
    const input = op.inventory_input;
    const header = input.header.?.*;
    const si = op.inventory_source;
    const role = roleFor(ctx, ctx.compiled.sources.ids.?[si]);
    const provider = op.providers[si];
    if (ctx.owner.sources[si].access_region != 0) return error.Invalid;
    if (input.source_id != role.source_id or input.incarnation != provider.incarnation or input.capability != provider.capability or input.observation != provider.observation or input.catalog_generation != ctx.catalog_generation or input.query_token != op.token or header.query_token != op.token or header.reserved != 0 or header.status < 1 or header.status > 5) return error.Identity;
    if (header.count > input.row_capacity) return c.fail(failure, .capacity_exceeded, .binding, header.count, input.row_capacity);
    if (header.text_length > input.text_capacity) return c.fail(failure, .payload_capacity, .payload, header.text_length, input.text_capacity);
    var event_matched = false;
    for (op.expected_sources, 0..) |source, row| if (source == si) {
        if (header.status != op.source_events.statuses.?[row]) return error.Identity;
        event_matched = true;
    };
    if (!event_matched) return error.Identity;
    if (header.status == 1) {
        if (header.generation != role.inventory_generation or header.topology_fingerprint != role.topology_fingerprint) return error.Identity;
        var expected_count: u32 = 0;
        var scope: u32 = 0;
        while (scope < ctx.compiled.scopes.count) : (scope += 1) if (ctx.compiled.scopes.expansions.?[scope] == 4) {
            expected_count += 1;
        };
        if (header.count != expected_count) return error.Identity;
    } else if (header.count != 0 or header.text_length != 0) return error.Invalid;
    const facts = inv.CatalogFacts{
        .raw = .{ .columns = .{ .count = header.count, .luid_low = input.luids, .keys = input.keys }, .text = .{ .bytes = input.text.bytes, .length = header.text_length }, .source_id = input.source_id, .generation = op.token, .observation = input.observation, .observed_ms = header.observed_ms, .source_capability_mask = role.source_capability_mask, .requested = 1, .status = header.status, .reason = header.reason, .maximum_count = role.row_capacity },
        .inventory_generation = header.generation,
        .topology_fingerprint = header.topology_fingerprint,
        .observed_count = header.observed_count,
        .skipped_count = header.skipped_count,
        .overflow_count = header.overflow_count,
    };
    var prepared = try inv.stageCatalog(ctx.compiled, facts, ctx.inventory_binding.?, op.inventory_work, failure);
    var text = TextWriter{ .bytes = op.inventory_text };
    var row: u32 = 0;
    while (row < prepared.output.columns.count) : (row += 1) {
        const key = try c.get(prepared.output.text, prepared.output.columns.keys.?[row]);
        if (key.len > text.bytes.len - text.used) return c.fail(failure, .payload_capacity, .payload, @as(u64, text.used) + key.len, text.bytes.len);
        @constCast(prepared.output.columns.keys.?)[row] = text.put(key);
    }
    prepared.output.text = .{ .bytes = text.bytes.ptr, .length = text.used };
    op.inventory_prepared = prepared;
}
pub fn prepare(ctx: *Context, op: *Operation, extras: sel.ReadingEvents, sources: SourceEvents, failure: *p.Failure) c.Error!void {
    failure.* = .{};
    var success = false;
    defer if (!success) {
        ctx.owner.detach();
        ctx.operation_token = 0;
    };
    if (!valid(ctx) or op.context != ctx or op.prepared or op.accepted or op.token != ctx.operation_token or op.catalog_generation != ctx.catalog_generation or ctx.owner.work.region == null or ctx.owner.token != op.token) return error.Identity;
    if (sources.count != op.source_events.count or sources.reserved != 0 or sources.source_indices != op.source_events.source_indices or sources.statuses != op.source_events.statuses or sources.apply_cooldown != op.source_events.apply_cooldown or sources.completed_ms != op.source_events.completed_ms or sources.source_ids != op.source_events.source_ids or sources.observations != op.source_events.observations) return error.Invalid;
    if (extras.count != op.readings.count or extras.reserved != 0 or extras.tail_reserved != 0 or extras.text.reserved != 0 or extras.supported != op.readings.supported or extras.quantities != op.readings.quantities or extras.text.bytes != op.readings.text.bytes or extras.text.length > op.readings.text.length) return error.Invalid;
    var i: u32 = 0;
    while (i < sources.count) : (i += 1) {
        if (sources.source_indices.?[i] != op.expected_sources[i]) return error.Identity;
        const si = op.expected_sources[i];
        const state = &op.providers[si];
        if (sources.source_ids.?[i] != ctx.compiled.sources.ids.?[si] or sources.observations.?[i] != state.observation) return error.Identity;
        try sel.providerComplete(state, .{ .incarnation = state.incarnation, .capability = state.capability, .observation = state.observation, .completed_ms = sources.completed_ms.?[i], .cooldown_ms = ctx.cooldown_ms, .status = sources.statuses.?[i], .apply_cooldown = sources.apply_cooldown.?[i] });
    }
    // A categorical source-unsupported completion becomes the kernel's raw
    // source fact. The kernel invalidates every same-source alias, including
    // aliases outside the requested set, only on accepted commit.
    i = 0;
    while (i < ctx.owner.work.raw.count) : (i += 1) {
        const ri = ctx.owner.work.expected[i];
        const si = ctx.compiled.bindings.source_indices.?[ri];
        if (op.readings.rule_indices.?[i] != ri or op.readings.incarnations.?[i] != ctx.compiled.sources.incarnations.?[si] or op.readings.capabilities.?[i] != ctx.compiled.sources.capabilities.?[si] or op.readings.observations.?[i] != op.providers[si].observation or op.current_observations[si] != op.providers[si].observation) return error.Identity;
        if (ctx.owner.work.raw.observations.flags.?[i] & 32 != 0 and op.providers[si].unsupported == 0) return error.Invalid;
        if (op.providers[si].unsupported != 0) {
            ctx.owner.work.raw.observations.status.?[i] = 3;
            ctx.owner.work.raw.observations.presence.?[i] = 0;
            ctx.owner.work.raw.observations.flags.?[i] |= 32;
        }
    }
    try prepareInventory(ctx, op, failure);
    try dv.prePrepare(ctx.owner, &op.vectors, &ctx.full, ctx.compiled, if (sources.count == 0) &.{} else sources.source_ids.?[0..sources.count], if (sources.count == 0) &.{} else sources.statuses.?[0..sources.count], if (sources.count == 0) &.{} else sources.observations.?[0..sources.count], failure);
    const code = kernel_round.prepare(ctx.owner, op.token);
    if (code != .ok) {
        failure.* = ctx.owner.failure;
        return error.Invalid;
    }
    try dv.postPrepare(ctx.owner, &op.vectors, &ctx.full, ctx.compiled, failure);
    op.readings.text.length = extras.text.length;
    op.readings.integrated_gpu = extras.integrated_gpu;
    op.readings.integrated_rule = extras.integrated_rule;
    i = 0;
    while (i < op.readings.count) : (i += 1) {
        const ri = ctx.owner.work.expected[i];
        const si = ctx.compiled.bindings.source_indices.?[ri];
        @constCast(op.readings.statuses.?)[i] = if (op.providers[si].unsupported != 0) 3 else ctx.owner.work.deltas[i].status;
        @constCast(op.readings.source_unsupported.?)[i] = @intFromBool(op.providers[si].unsupported != 0);
    }
    op.learned = try sel.observe(ctx.compiled, ctx.learned, op.readings, op.learning_work, failure);
    if (op.learned.text.length > ctx.learned_text.len) return c.fail(failure, .payload_capacity, .payload, op.learned.text.length, ctx.learned_text.len);
    op.prepared = true;
    success = true;
}
pub fn commit(ctx: *Context, op: *Operation, history_bindings: []const history.Binding, mixed: []const history.MixedBinding, utc: i64, failure: *p.Failure) c.Error!void {
    failure.* = .{};
    var success = false;
    defer if (!success) {
        ctx.owner.detach();
        ctx.operation_token = 0;
    };
    if (!valid(ctx) or op.context != ctx or !op.prepared or op.accepted or op.token != ctx.operation_token or ctx.owner.work.region == null or !ctx.owner.work.prepared) return error.Identity;
    defer clearInventory(op);
    if (op.due.len != ctx.owner.work.request_count) return error.Identity;
    var due_index: usize = 0;
    while (due_index < op.due.len) : (due_index += 1) if (op.due[due_index] != ctx.owner.work.requested[due_index]) return error.Identity;
    if (ctx.inventory_binding) |binding| for (history_bindings) |b| {
        try separateInventory(binding, b.region, b.byte_length, b.rows_source);
        try separateInventory(binding, b.region, b.byte_length, b.data_source);
    };
    if (ctx.inventory_binding) |binding| for (mixed) |b| {
        try separateInventory(binding, b.base.region, b.base.byte_length, b.base.rows_source);
        try separateInventory(binding, b.base.region, b.base.byte_length, b.base.data_source);
    };
    if (op.inventory_prepared) |*prepared| {
        prepared.admit() catch |err| {
            failure.* = .{ .code = if (err == error.Identity) .stale_identity else .shared_section, .family = .binding };
            return err;
        };
    }
    defer if (op.inventory_prepared) |*prepared| prepared.abort();
    const code = if (mixed.len != 0) history.commitMixed(ctx.owner, history_bindings, mixed, op.due, op.token, utc) else if (history_bindings.len != 0) history.commitMany(ctx.owner, history_bindings, op.due, op.token, utc) else kernel_round.commit(ctx.owner, op.token, utc);
    if (code != .ok) {
        failure.* = ctx.owner.failure;
        return error.Invalid;
    }
    if (op.inventory_prepared) |*prepared| _ = prepared.publishAccepted();
    // Every extent/token/string was preflighted by prepare. No fallible call,
    // new allocator access or decision follows any successful publication.
    copyLearning(ctx, op.learned);
    @memcpy(ctx.providers[0..op.providers.len], op.providers);
    var i: u32 = 0;
    while (i < op.providers.len) : (i += 1) {
        ctx.owner.sources[i].availability = sel.providerAvailability(op.providers[i], utc);
        if (op.providers[i].unsupported != 0) ctx.owner.sources[i].suppressed = true;
        @constCast(ctx.compiled.sources.availability.?)[i] = ctx.owner.sources[i].availability;
    }
    op.accepted = true;
    ctx.operation_token = 0;
    success = true;
}
fn clearInventory(op: *Operation) void {
    if (op.inventory_prepared) |*prepared| prepared.abort();
    op.inventory_input = .{};
    op.inventory_prepared = null;
    op.inventory_work = &.{};
    op.inventory_text = &.{};
    op.inventory_source = p.none;
    // Publication pointers remain borrowed through the synchronous accepted
    // callback. DetachOperation clears them at the end of that same work use.
}
pub fn detach(ctx: *Context) void {
    if (ctx.magic == Context.live_magic and ctx.owner.magic == st.Owner.live_magic) ctx.owner.detach();
    ctx.operation_token = 0;
}
pub fn close(ctx: *Context) void {
    detach(ctx);
    ctx.compiled = .{};
    ctx.providers = &.{};
    ctx.roles = &.{};
    ctx.resources = &.{};
    ctx.inventory_binding = null;
    ctx.cold_binding = null;
    ctx.full = .{};
    ctx.learned = .{};
    ctx.learned_text = &.{};
    ctx.canonical = &.{};
    ctx.catalog_generation = 0;
    ctx.magic = 0;
}
fn output(f: ?*p.Failure, failure: p.Failure, err: ?c.Error) u32 {
    var value = failure;
    if (err) |error_value| if (value.code == .ok) {
        value.code = switch (error_value) {
            error.Identity => .stale_identity,
            error.Capacity, error.Exhausted => .capacity_exceeded,
            error.Work => .workspace_unavailable,
            error.Payload => .payload_capacity,
            else => .invalid_contract,
        };
        value.family = switch (error_value) {
            error.Work => .work,
            error.Payload => .payload,
            error.Exhausted => .generation,
            else => .none,
        };
    };
    if (f) |pointer| pointer.* = value;
    return @backingInt(value.code);
}
fn memory(pointer: ?[*]u8, length: u64) ?[]align(8) u8 {
    const value = pointer orelse return null;
    if (@intFromPtr(value) % 8 != 0 or length > std.math.maxInt(usize)) return null;
    return @as([*]align(8) u8, @alignCast(value))[0..@intCast(length)];
}
fn context(pointer: ?*Context) ?*Context {
    const value = pointer orelse return null;
    return if (value.magic == Context.live_magic) value else null;
}
fn measureRaw(facts: ?*const c.Facts, limits: ?*const c.Limits, roles: Roles, work: ?[*]u8, length: u64, input_prefix: u64, learned_text: u64, canonical_reserve: u64, input_bytes: u64, out: ?*Measurement, f: ?*p.Failure) u32 {
    const raw = facts orelse return output(f, .{}, error.Invalid);
    const cap = limits orelse return output(f, .{}, error.Invalid);
    const target = out orelse return output(f, .{}, error.Invalid);
    const inventory_work = roleWork(roles) catch |err| return output(f, .{}, err);
    const bytes = memory(work, length) orelse return output(f, .{}, error.Invalid);
    var failure: p.Failure = .{};
    const required_compile = c.required(raw.*, cap.*, &failure) catch |err| return output(f, failure, err);
    const offset = align8(input_prefix) catch |err| return output(f, .{}, err);
    const required = std.math.add(u64, offset, required_compile) catch return output(f, .{}, error.Exhausted);
    if (required > bytes.len) return output(f, .{ .code = .workspace_unavailable, .family = .work, .required = required, .limit = bytes.len }, null);
    if (!inputRange(@intFromPtr(roles.rows), @as(usize, roles.count) * @sizeOf(SourceRole), bytes, input_prefix)) return output(f, .{}, error.Invalid);
    if (!inputRange(@intFromPtr(raw.text.bytes), raw.text.length, bytes, input_prefix) or !inputColumns(c.Sources, raw.sources, bytes, input_prefix) or !inputColumns(c.Templates, raw.templates, bytes, input_prefix) or !inputColumns(c.Fields, raw.fields, bytes, input_prefix) or !inputColumns(c.Scopes, raw.scopes, bytes, input_prefix) or !inputColumns(c.Handles, raw.handles, bytes, input_prefix)) return output(f, .{}, error.Invalid);
    const candidate = c.compile(raw.*, cap.*, @alignCast(bytes[@intCast(offset)..]), &failure) catch |err| return output(f, failure, err);
    validateRoles(candidate, roles, &failure) catch |err| return output(f, failure, err);
    var sizes = measure(candidate, required_compile, learned_text, canonical_reserve, @max(input_bytes, input_prefix), &failure) catch |err| return output(f, failure, err);
    sizes.extra_operation_work_bytes = std.math.add(u64, sizes.extra_operation_work_bytes, inventory_work) catch return output(f, .{}, error.Exhausted);
    const declaration = catalogDeclaration(candidate) catch |err| return output(f, .{}, err);
    target.* = .{ .sizes = sizes, .catalog = declaration, .metric_count = candidate.catalog.metrics.count };
    return output(f, .{}, null);
}
pub export fn rm_metric_discovery_runtime_measure(facts: ?*const c.Facts, limits: ?*const c.Limits, work: ?[*]u8, length: u64, input_prefix: u64, learned_text: u64, canonical_reserve: u64, input_bytes: u64, out: ?*Measurement, f: ?*p.Failure) u32 {
    return measureRaw(facts, limits, .{}, work, length, input_prefix, learned_text, canonical_reserve, input_bytes, out, f);
}
pub export fn rm_metric_discovery_runtime_measure_roles(facts: ?*const c.Facts, limits: ?*const c.Limits, roles: ?*const Roles, work: ?[*]u8, length: u64, input_prefix: u64, learned_text: u64, canonical_reserve: u64, input_bytes: u64, out: ?*Measurement, f: ?*p.Failure) u32 {
    const declared = roles orelse return output(f, .{}, error.Invalid);
    return measureRaw(facts, limits, declared.*, work, length, input_prefix, learned_text, canonical_reserve, input_bytes, out, f);
}
pub export fn rm_metric_discovery_runtime_topology_input_size(count: u32, text_capacity: u32, out: ?*u64, f: ?*p.Failure) u32 {
    const target = out orelse return output(f, .{}, error.Invalid);
    target.* = dv.inputSize(count, text_capacity) catch |err| return output(f, .{}, err);
    return output(f, .{}, null);
}
pub export fn rm_metric_discovery_runtime_topology_input(count: u32, text_capacity: u32, work: ?[*]u8, length: u64, out: ?*dv.v.ScopeTopology, prefix: ?*u64, f: ?*p.Failure) u32 {
    const target = out orelse return output(f, .{}, error.Invalid);
    const required_out = prefix orelse return output(f, .{}, error.Invalid);
    const bytes = memory(work, length) orelse return output(f, .{}, error.Invalid);
    const required = dv.inputSize(count, text_capacity) catch |err| return output(f, .{}, err);
    if (required > bytes.len) return output(f, .{ .code = .workspace_unavailable, .family = .work, .required = required, .limit = bytes.len }, null);
    const result = dv.inputLayout(count, text_capacity, bytes) catch |err| return output(f, .{}, err);
    target.* = result;
    required_out.* = required;
    return output(f, .{}, null);
}
pub export fn rm_metric_discovery_runtime_measure_full(facts: ?*const c.Facts, limits: ?*const c.Limits, declared: ?*const Roles, topology: ?*const dv.v.ScopeTopology, admission: ?*const dv.Admission, work: ?[*]u8, length: u64, input_prefix: u64, learned_text: u64, canonical_reserve: u64, input_bytes: u64, out: ?*FullMeasurement, f: ?*p.Failure) u32 {
    const raw = facts orelse return output(f, .{}, error.Invalid);
    const cap = limits orelse return output(f, .{}, error.Invalid);
    const roles = (declared orelse return output(f, .{}, error.Invalid)).*;
    const t = (topology orelse return output(f, .{}, error.Invalid)).*;
    const bound: ?dv.Admission = if (admission) |a| a.* else null;
    const target = out orelse return output(f, .{}, error.Invalid);
    const bytes = memory(work, length) orelse return output(f, .{}, error.Invalid);
    var failure: p.Failure = .{};
    const required_compile = c.required(raw.*, cap.*, &failure) catch |err| return output(f, failure, err);
    const offset = align8(input_prefix) catch |err| return output(f, .{}, err);
    const required = std.math.add(u64, offset, required_compile) catch return output(f, .{}, error.Exhausted);
    if (required > bytes.len) return output(f, .{ .code = .workspace_unavailable, .family = .work, .required = required, .limit = bytes.len }, null);
    if (!inputRange(@intFromPtr(roles.rows), @as(usize, roles.count) * @sizeOf(SourceRole), bytes, input_prefix) or !inputRange(@intFromPtr(t.text.bytes), t.text.length, bytes, input_prefix) or !inputColumns(dv.v.ScopeTopology, t, bytes, input_prefix)) return output(f, .{}, error.Invalid);
    dv.validate(t, raw.scopes, raw.text) catch |err| return output(f, failure, err);
    const role_rows: []const SourceRole = if (roles.count == 0) &.{} else (roles.rows orelse return output(f, .{}, error.Invalid))[0..roles.count];
    dv.validateRoles(role_rows, bound) catch |err| return output(f, failure, err);
    const candidate = c.compile(raw.*, cap.*, @alignCast(bytes[@intCast(offset)..]), &failure) catch |err| return output(f, failure, err);
    validateRoles(candidate, roles, &failure) catch |err| return output(f, failure, err);
    var sizes = measure(candidate, required_compile, learned_text, canonical_reserve, @max(input_bytes, input_prefix), &failure) catch |err| return output(f, failure, err);
    const topology_bytes = dv.topologySize(t) catch |err| return output(f, failure, err);
    var vector_work = dv.workBound(candidate, role_rows) catch |err| return output(f, failure, err);
    if (bound) |b| {
        var raw_rows: u32 = 0;
        for (role_rows) |role| if (role.flags & 2 != 0) {
            raw_rows = role.row_capacity;
        };
        const gpu_work = dv.v.discoveryGpuWorkSizeChecked(b.binding, raw_rows, b.bound_row_capacity, candidate.catalog.metrics.count, cap.work_bytes, &failure) catch |err| return output(f, failure, if (err == error.Capacity) error.Capacity else error.Invalid);
        vector_work = std.math.add(u64, vector_work, gpu_work) catch return output(f, failure, error.Exhausted);
    }
    // Vector helpers return their exact byte extent, which may end on a u32.
    // The following kernel partition must still start at an eight-byte boundary.
    vector_work = align8(vector_work) catch |err| return output(f, failure, err);
    sizes.extra_active_bytes = std.math.add(u64, sizes.extra_active_bytes, topology_bytes) catch return output(f, .{}, error.Exhausted);
    sizes.extra_operation_work_bytes = std.math.add(u64, sizes.extra_operation_work_bytes, (roleWork(roles) catch |err| return output(f, failure, err))) catch return output(f, .{}, error.Exhausted);
    sizes.extra_operation_work_bytes = std.math.add(u64, sizes.extra_operation_work_bytes, vector_work) catch return output(f, .{}, error.Exhausted);
    if (sizes.extra_operation_work_bytes > cap.work_bytes) return output(f, .{ .code = .capacity_exceeded, .family = .work, .required = sizes.extra_operation_work_bytes, .limit = cap.work_bytes }, null);
    target.* = .{ .sizes = .{ .base = sizes, .extra = .{ .topology_bytes = topology_bytes, .vector_work_bytes = vector_work, .scope_count = t.count, .topology_text_bytes = dv.topologyTextBytes(t) catch |err| return output(f, failure, err), .gpu_bound_rows = if (bound) |b| b.bound_row_capacity else 0, .grouping_work_bytes = sdk.requiredSize(candidate.bindings.count, candidate.sources.count) catch return output(f, failure, error.Exhausted) } }, .catalog = catalogDeclaration(candidate) catch |err| return output(f, failure, err), .metric_count = candidate.catalog.metrics.count };
    return output(f, .{}, null);
}
pub export fn rm_metric_discovery_runtime_sizes(candidate: ?*const c.Compiled, compiler_work: u64, learned_text: u64, canonical_reserve: u64, input_bytes: u64, out: ?*Sizes, f: ?*p.Failure) u32 {
    const src = candidate orelse return output(f, .{}, error.Invalid);
    const target = out orelse return output(f, .{}, error.Invalid);
    var failure: p.Failure = .{};
    const sizes = measure(src.*, compiler_work, learned_text, canonical_reserve, input_bytes, &failure) catch |err| return output(f, failure, err);
    target.* = sizes;
    return output(f, .{}, null);
}
pub export fn rm_metric_discovery_runtime_catalog_declaration(candidate: ?*const c.Compiled, out: ?*CatalogDeclaration, f: ?*p.Failure) u32 {
    const src = candidate orelse return output(f, .{}, error.Invalid);
    const target = out orelse return output(f, .{}, error.Invalid);
    target.* = catalogDeclaration(src.*) catch |err| return output(f, .{}, err);
    return output(f, .{}, null);
}
pub export fn rm_metric_discovery_runtime_bind(owner: ?*st.Owner, tail: ?[*]u8, length: u64, sizes: ?*const Sizes, cooldown_ms: u64, out: ?*?*Context, f: ?*p.Failure) u32 {
    const o = owner orelse return output(f, .{}, error.Invalid);
    const extents = sizes orelse return output(f, .{}, error.Invalid);
    const target = out orelse return output(f, .{}, error.Invalid);
    const region = memory(tail, length) orelse return output(f, .{}, error.Invalid);
    var failure: p.Failure = .{};
    const value = bind(o, region, extents.*, cooldown_ms, &failure) catch |err| return output(f, failure, err);
    target.* = value;
    return output(f, .{}, null);
}
pub export fn rm_metric_discovery_runtime_attach(owner: ?*st.Owner, sizes: ?*const Sizes, cooldown_ms: u64, out: ?*?*Context, f: ?*p.Failure) u32 {
    const o = owner orelse return output(f, .{}, error.Invalid);
    const extents = sizes orelse return output(f, .{}, error.Invalid);
    const target = out orelse return output(f, .{}, error.Invalid);
    if (o.magic != st.Owner.live_magic) return output(f, .{ .code = .closed }, null);
    const offset = align8(o.sizes.active) catch |err| return output(f, .{}, err);
    if (offset > o.active_length) return output(f, .{}, error.Invalid);
    const base: [*]align(8) u8 = @ptrFromInt(@intFromPtr(o) + @as(usize, @intCast(offset)));
    var failure: p.Failure = .{};
    const value = bind(o, base[0..@intCast(o.active_length - offset)], extents.*, cooldown_ms, &failure) catch |err| return output(f, failure, err);
    target.* = value;
    return output(f, .{}, null);
}
pub export fn rm_metric_discovery_runtime_attach_roles(owner: ?*st.Owner, sizes: ?*const Sizes, declared: ?*const Roles, inventory_binding: ?*const inv.Binding, cooldown_ms: u64, out: ?*?*Context, f: ?*p.Failure) u32 {
    const o = owner orelse return output(f, .{}, error.Invalid);
    const extents = sizes orelse return output(f, .{}, error.Invalid);
    const roles = (declared orelse return output(f, .{}, error.Invalid)).*;
    const target = out orelse return output(f, .{}, error.Invalid);
    const binding: ?inv.Binding = if (inventory_binding) |b| b.* else null;
    const extra = roleWork(roles) catch |err| return output(f, .{}, err);
    if (roles.count > extents.source_capacity or (extra != 0) != (binding != null) or extra > extents.extra_operation_work_bytes) return output(f, .{}, error.Invalid);
    if (o.magic != st.Owner.live_magic) return output(f, .{ .code = .closed }, null);
    if (c.overlap(@intFromPtr(roles.rows), @as(usize, roles.count) * @sizeOf(SourceRole), @intFromPtr(o), @intCast(o.active_length))) return output(f, .{}, error.Invalid);
    const offset = align8(o.sizes.active) catch |err| return output(f, .{}, err);
    if (offset > o.active_length) return output(f, .{}, error.Invalid);
    const base: [*]align(8) u8 = @ptrFromInt(@intFromPtr(o) + @as(usize, @intCast(offset)));
    var failure: p.Failure = .{};
    const ctx = bind(o, base[0..@intCast(o.active_length - offset)], extents.*, cooldown_ms, &failure) catch |err| return output(f, failure, err);
    if (roles.count != 0) @memcpy(ctx.roles[0..roles.count], roles.rows.?[0..roles.count]);
    ctx.inventory_binding = binding;
    target.* = ctx;
    return output(f, .{}, null);
}
pub export fn rm_metric_discovery_runtime_attach_full(owner: ?*st.Owner, sizes: ?*const FullSizes, declared: ?*const Roles, inventory_binding: ?*const inv.Binding, admission: ?*const dv.Admission, cooldown_ms: u64, out: ?*?*Context, f: ?*p.Failure) u32 {
    const o = owner orelse return output(f, .{}, error.Invalid);
    const extents = (sizes orelse return output(f, .{}, error.Invalid)).*;
    const roles = (declared orelse return output(f, .{}, error.Invalid)).*;
    const target = out orelse return output(f, .{}, error.Invalid);
    const bound: ?dv.Admission = if (admission) |b| b.* else null;
    const binding: ?inv.Binding = if (inventory_binding) |b| b.* else null;
    const extra = roleWork(roles) catch |err| return output(f, .{}, err);
    if (roles.count > extents.base.source_capacity or extents.extra.reserved != 0 or (extra != 0) != (binding != null) or extra > extents.base.extra_operation_work_bytes) return output(f, .{}, error.Invalid);
    const rows: []const SourceRole = if (roles.count == 0) &.{} else roles.rows.?[0..roles.count];
    dv.validateRoles(rows, bound) catch |err| return output(f, .{}, err);
    if (o.magic != st.Owner.live_magic) return output(f, .{ .code = .closed }, null);
    if (c.overlap(@intFromPtr(roles.rows), rows.len * @sizeOf(SourceRole), @intFromPtr(o), @intCast(o.active_length))) return output(f, .{}, error.Invalid);
    const offset = align8(o.sizes.active) catch |err| return output(f, .{}, err);
    if (offset > o.active_length) return output(f, .{}, error.Invalid);
    const base: [*]align(8) u8 = @ptrFromInt(@intFromPtr(o) + @as(usize, @intCast(offset)));
    var failure: p.Failure = .{};
    const ctx = bindLayout(o, base[0..@intCast(o.active_length - offset)], extents.base, cooldown_ms, extents.extra.topology_bytes, &failure) catch |err| return output(f, failure, err);
    if (rows.len != 0) @memcpy(ctx.roles[0..rows.len], rows);
    ctx.inventory_binding = binding;
    ctx.full.extra = extents.extra;
    ctx.full.admission = bound;
    ctx.full.enabled = true;
    target.* = ctx;
    return output(f, .{}, null);
}
fn operationValid(ctx: *Context, op: *Operation) bool {
    return valid(ctx) and op.context == ctx and op.token == ctx.operation_token and op.catalog_generation == ctx.catalog_generation and ctx.owner.work.region != null and !op.prepared and !op.accepted;
}
pub export fn rm_metric_discovery_runtime_inventory_view(pointer: ?*Context, operation: ?*Operation, out: ?*InventoryInput, f: ?*p.Failure) u32 {
    const ctx = context(pointer) orelse return output(f, .{ .code = .closed }, null);
    const op = operation orelse return output(f, .{}, error.Invalid);
    const target = out orelse return output(f, .{}, error.Invalid);
    if (!operationValid(ctx, op)) return output(f, .{}, error.Identity);
    target.* = op.inventory_input;
    return output(f, .{}, null);
}
pub export fn rm_metric_discovery_runtime_source_roles_view(pointer: ?*Context, operation: ?*Operation, out: ?*SourceRoleView, f: ?*p.Failure) u32 {
    const ctx = context(pointer) orelse return output(f, .{ .code = .closed }, null);
    const op = operation orelse return output(f, .{}, error.Invalid);
    const target = out orelse return output(f, .{}, error.Invalid);
    if (!operationValid(ctx, op)) return output(f, .{}, error.Identity);
    target.* = .{ .count = @intCast(op.source_roles.len), .flags = op.source_roles.ptr };
    return output(f, .{}, null);
}
pub export fn rm_metric_discovery_runtime_sdk_dispatch(pointer: ?*Context, operation: ?*Operation, out: ?*sdk.Dispatch, f: ?*p.Failure) u32 {
    const ctx = context(pointer) orelse return output(f, .{ .code = .closed }, null);
    const op = operation orelse return output(f, .{}, error.Invalid);
    const target = out orelse return output(f, .{}, error.Invalid);
    if (!operationValid(ctx, op)) return output(f, .{}, error.Identity);
    target.* = op.sdk_grouping;
    return output(f, .{}, null);
}
pub export fn rm_metric_discovery_runtime_vector_inputs(pointer: ?*Context, operation: ?*Operation, out: ?*dv.Inputs, f: ?*p.Failure) u32 {
    const ctx = context(pointer) orelse return output(f, .{ .code = .closed }, null);
    const op = operation orelse return output(f, .{}, error.Invalid);
    const target = out orelse return output(f, .{}, error.Invalid);
    if (!operationValid(ctx, op)) return output(f, .{}, error.Identity);
    target.* = op.vectors.inputs;
    return output(f, .{}, null);
}
pub export fn rm_metric_discovery_runtime_vector_publication(pointer: ?*Context, operation: ?*Operation, out: ?*?*const dv.v.GpuPublication, f: ?*p.Failure) u32 {
    const ctx = context(pointer) orelse return output(f, .{ .code = .closed }, null);
    const op = operation orelse return output(f, .{}, error.Invalid);
    const target = out orelse return output(f, .{}, error.Invalid);
    if (!valid(ctx) or op.context != ctx or !op.prepared or op.token != ctx.owner.token) return output(f, .{}, error.Identity);
    target.* = op.vectors.publication;
    return output(f, .{}, null);
}
pub export fn rm_metric_discovery_runtime_project_request_size(pointer: ?*Context, out: ?*u64, f: ?*p.Failure) u32 {
    const ctx = context(pointer) orelse return output(f, .{ .code = .closed }, null);
    const target = out orelse return output(f, .{}, error.Invalid);
    if (!valid(ctx) or !ctx.full.enabled) return output(f, .{}, error.Identity);
    target.* = dv.v.requestProjectionSize(ctx.owner) catch |err| return output(f, .{}, if (err == error.Capacity) error.Capacity else error.Invalid);
    return output(f, .{}, null);
}
pub export fn rm_metric_discovery_runtime_project_request(pointer: ?*Context, request: ?*const sel.Request, work: ?[*]u8, length: u64, input_prefix: u64, out: ?*dv.v.RequestProjection, f: ?*p.Failure) u32 {
    const ctx = context(pointer) orelse return output(f, .{ .code = .closed }, null);
    const raw = (request orelse return output(f, .{}, error.Invalid)).*;
    const target = out orelse return output(f, .{}, error.Invalid);
    const bytes = memory(work, length) orelse return output(f, .{}, error.Invalid);
    if (!valid(ctx) or !ctx.full.enabled or ctx.catalog_generation == 0 or input_prefix > ctx.sizes.input_text_bytes) return output(f, .{}, error.Identity);
    if (bytes.len > ctx.owner.config.work_limit) return output(f, .{ .code = .capacity_exceeded, .family = .work, .required = bytes.len, .limit = ctx.owner.config.work_limit }, null);
    const offset = align8(input_prefix) catch |err| return output(f, .{}, err);
    if (offset > bytes.len or !inputRange(@intFromPtr(raw.keys), @as(usize, raw.count) * 8, bytes, input_prefix) or !inputRange(@intFromPtr(raw.text.bytes), raw.text.length, bytes, input_prefix)) return output(f, .{}, error.Invalid);
    const b = dv.bindings(ctx.compiled, ctx.catalog_generation);
    const r = dv.v.Request{ .flags = (if (raw.flags & 3 != 0) @as(u32, 1) else 0) | (if (raw.flags & 4 != 0) @as(u32, 2) else 0), .count = raw.count, .text = .{ .bytes = raw.text.bytes, .length = raw.text.length, .reserved = raw.text.reserved }, .addresses = @ptrCast(raw.keys) };
    if (raw.flags & ~@as(u32, 7) != 0) return output(f, .{}, error.Invalid);
    const projected = dv.v.projectRequest(ctx.owner, b, ctx.full.topology, r, @alignCast(bytes[@intCast(offset)..])) catch |err| return output(f, .{}, if (err == error.Capacity) error.Capacity else if (err == error.Stale) error.Identity else if (err == error.Work) error.Work else error.Invalid);
    target.* = projected;
    return output(f, .{}, null);
}
pub export fn rm_metric_discovery_runtime_replace(pointer: ?*Context, facts: ?*const c.Facts, limits: ?*const c.Limits, initial: ?*const ProviderInputs, catalog_binding: ?*const CatalogBinding, work: ?[*]u8, length: u64, input_prefix: u64, utc: i64, f: ?*p.Failure) u32 {
    const ctx = context(pointer) orelse return output(f, .{ .code = .closed }, null);
    detach(ctx);
    const raw = facts orelse return output(f, .{}, error.Invalid);
    const cap = limits orelse return output(f, .{}, error.Invalid);
    const bytes = memory(work, length) orelse return output(f, .{}, error.Invalid);
    var failure: p.Failure = .{};
    replace(ctx, raw.*, cap.*, initial, catalog_binding, bytes, input_prefix, utc, null, &failure) catch |err| return output(f, failure, err);
    return output(f, .{}, null);
}
pub export fn rm_metric_discovery_runtime_replace_full(pointer: ?*Context, facts: ?*const c.Facts, limits: ?*const c.Limits, topology: ?*const dv.v.ScopeTopology, initial: ?*const ProviderInputs, catalog_binding: ?*const CatalogBinding, work: ?[*]u8, length: u64, input_prefix: u64, utc: i64, f: ?*p.Failure) u32 {
    const ctx = context(pointer) orelse return output(f, .{ .code = .closed }, null);
    detach(ctx);
    const raw = facts orelse return output(f, .{}, error.Invalid);
    const cap = limits orelse return output(f, .{}, error.Invalid);
    const t = topology orelse return output(f, .{}, error.Invalid);
    const bytes = memory(work, length) orelse return output(f, .{}, error.Invalid);
    var failure: p.Failure = .{};
    replace(ctx, raw.*, cap.*, initial, catalog_binding, bytes, input_prefix, utc, t, &failure) catch |err| return output(f, failure, err);
    return output(f, .{}, null);
}
pub export fn rm_metric_discovery_runtime_plan(pointer: ?*Context, request: ?*const sel.Request, work: ?[*]u8, length: u64, input_prefix: u64, utc: i64, out: ?*Plan, f: ?*p.Failure) u32 {
    const ctx = context(pointer) orelse return output(f, .{ .code = .closed }, null);
    detach(ctx);
    const input = request orelse return output(f, .{}, error.Invalid);
    const target = out orelse return output(f, .{}, error.Invalid);
    const bytes = memory(work, length) orelse return output(f, .{}, error.Invalid);
    var failure: p.Failure = .{};
    const value = plan(ctx, input.*, bytes, input_prefix, utc, &failure) catch |err| return output(f, failure, err);
    target.* = value;
    return output(f, .{}, null);
}
pub export fn rm_metric_discovery_runtime_prepare(pointer: ?*Context, operation: ?*Operation, readings: ?*const sel.ReadingEvents, sources: ?*const SourceEvents, f: ?*p.Failure) u32 {
    const ctx = context(pointer) orelse return output(f, .{ .code = .closed }, null);
    if (operation == null or readings == null or sources == null) {
        detach(ctx);
        return output(f, .{}, error.Invalid);
    }
    const op = operation orelse return output(f, .{}, error.Invalid);
    const raw = readings orelse return output(f, .{}, error.Invalid);
    const source = sources orelse return output(f, .{}, error.Invalid);
    var failure: p.Failure = .{};
    prepare(ctx, op, raw.*, source.*, &failure) catch |err| return output(f, failure, err);
    return output(f, .{}, null);
}
pub export fn rm_metric_discovery_runtime_commit(pointer: ?*Context, operation: ?*Operation, history_binding: ?*const history.Binding, utc: i64, f: ?*p.Failure) u32 {
    const ctx = context(pointer) orelse return output(f, .{ .code = .closed }, null);
    const op = operation orelse {
        detach(ctx);
        return output(f, .{}, error.Invalid);
    };
    var failure: p.Failure = .{};
    const bindings: []const history.Binding = if (history_binding) |binding| @as([*]const history.Binding, @ptrCast(binding))[0..1] else &.{};
    commit(ctx, op, bindings, &.{}, utc, &failure) catch |err| return output(f, failure, err);
    return output(f, .{}, null);
}
pub export fn rm_metric_discovery_runtime_commit_grouped(pointer: ?*Context, operation: ?*Operation, history_bindings: ?[*]const history.Binding, count: u32, utc: i64, f: ?*p.Failure) u32 {
    const ctx = context(pointer) orelse return output(f, .{ .code = .closed }, null);
    if (operation == null or count == 0 or count > ctx.owner.result_count or history_bindings == null) {
        detach(ctx);
        return output(f, .{}, error.Invalid);
    }
    var failure: p.Failure = .{};
    commit(ctx, operation.?, history_bindings.?[0..count], &.{}, utc, &failure) catch |err| return output(f, failure, err);
    return output(f, .{}, null);
}
pub export fn rm_metric_discovery_runtime_detach(pointer: ?*Context) void {
    if (context(pointer)) |ctx| detach(ctx);
}
pub export fn rm_metric_discovery_runtime_commit_mixed(pointer: ?*Context, operation: ?*Operation, scalar: ?[*]const history.Binding, scalar_count: u32, mixed: ?[*]const history.MixedBinding, mixed_count: u32, utc: i64, f: ?*p.Failure) u32 {
    const ctx = context(pointer) orelse return output(f, .{ .code = .closed }, null);
    if (operation == null or scalar_count + @as(u64, mixed_count) > ctx.owner.result_count or (scalar_count != 0 and scalar == null) or (mixed_count != 0 and mixed == null)) {
        detach(ctx);
        return output(f, .{}, error.Invalid);
    }
    var failure: p.Failure = .{};
    commit(ctx, operation.?, if (scalar_count == 0) &.{} else scalar.?[0..scalar_count], if (mixed_count == 0) &.{} else mixed.?[0..mixed_count], utc, &failure) catch |err| return output(f, failure, err);
    return output(f, .{}, null);
}
pub export fn rm_metric_discovery_runtime_detach_operation(pointer: ?*Context, operation: ?*Operation) void {
    const ctx = context(pointer) orelse return;
    if (operation) |op| if (op.context == ctx) {
        clearInventory(op);
        dv.clear(&op.vectors);
        op.sdk_grouping = .{};
    };
    detach(ctx);
}
pub export fn rm_metric_discovery_runtime_close(pointer: ?*Context) void {
    if (context(pointer)) |ctx| close(ctx);
}
pub fn exportAnchor() void {
    _ = &rm_metric_discovery_runtime_measure;
    _ = &rm_metric_discovery_runtime_measure_roles;
    _ = &rm_metric_discovery_runtime_measure_full;
    _ = &rm_metric_discovery_runtime_topology_input_size;
    _ = &rm_metric_discovery_runtime_topology_input;
    _ = &rm_metric_discovery_runtime_attach_roles;
    _ = &rm_metric_discovery_runtime_attach_full;
    _ = &rm_metric_discovery_runtime_replace_full;
    _ = &rm_metric_discovery_runtime_inventory_view;
    _ = &rm_metric_discovery_runtime_source_roles_view;
    _ = &rm_metric_discovery_runtime_sdk_dispatch;
    _ = &rm_metric_discovery_runtime_vector_inputs;
    _ = &rm_metric_discovery_runtime_vector_publication;
    _ = &rm_metric_discovery_runtime_project_request_size;
    _ = &rm_metric_discovery_runtime_project_request;
    _ = &rm_metric_discovery_runtime_sizes;
    _ = &rm_metric_discovery_runtime_catalog_declaration;
    _ = &rm_metric_discovery_runtime_bind;
    _ = &rm_metric_discovery_runtime_attach;
    _ = &rm_metric_discovery_runtime_replace;
    _ = &rm_metric_discovery_runtime_plan;
    _ = &rm_metric_discovery_runtime_prepare;
    _ = &rm_metric_discovery_runtime_commit;
    _ = &rm_metric_discovery_runtime_commit_grouped;
    _ = &rm_metric_discovery_runtime_commit_mixed;
    _ = &rm_metric_discovery_runtime_detach;
    _ = &rm_metric_discovery_runtime_detach_operation;
    _ = &rm_metric_discovery_runtime_close;
}
comptime {
    std.debug.assert(@sizeOf(SourceRole) == 48 and @sizeOf(Roles) == 16 and @sizeOf(SourceRoleView) == 16);
    std.debug.assert(@sizeOf(InventoryHeader) == 64 and @sizeOf(InventoryInput) == 96);
    std.debug.assert(@offsetOf(SourceRole, "source_capability_mask") == 24 and @offsetOf(InventoryHeader, "query_token") == 56);
    std.debug.assert(@offsetOf(InventoryInput, "header") == 56 and @offsetOf(InventoryInput, "keys") == 72 and @offsetOf(InventoryInput, "text") == 80);
    std.debug.assert(@sizeOf(Sizes) == 56 and @sizeOf(Measurement) == 128);
    std.debug.assert(@sizeOf(FullSizes) == 96 and @sizeOf(FullMeasurement) == 168);
    std.debug.assert(@sizeOf(ProviderInputs) == 24 and @sizeOf(CatalogBinding) == 56 and @sizeOf(CatalogDeclaration) == 64);
    std.debug.assert(@sizeOf(SourceEvents) == 56 and @sizeOf(Dispatch) == 72 and @sizeOf(Plan) == 640);
    std.debug.assert(@offsetOf(Plan, "raw") == 8 and @offsetOf(Plan, "selection") == 272 and @offsetOf(Plan, "dispatch") == 400);
    std.debug.assert(@offsetOf(Plan, "sources") == 472 and @offsetOf(Plan, "readings") == 528);
}
