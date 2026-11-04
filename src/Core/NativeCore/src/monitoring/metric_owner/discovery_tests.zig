const std = @import("std");
const testing = std.testing;
const c = @import("catalog_compiler.zig");
const sel = @import("selection.zig");
const inv = @import("inventory.zig");
const exports = @import("discovery_exports.zig");
const ss = @import("rm_shared_section");
const p = c.p;

// All large fixture/work/region buffers use the leak-checking test allocator.
const Fixture = struct {
    arena: [4096]u8 = undefined,
    used: u32 = 0,
    source_ids: [2]u64 = .{ 10, 20 },
    source_names: [2]c.Ref = undefined,
    incarnations: [2]u64 = .{ 1, 2 },
    capabilities: [2]u64 = .{ 1, 1 },
    availability: [2]u32 = .{ 1, 1 },
    source_priorities: [2]u32 = .{ 10, 20 },
    tnames: [5]c.Ref = undefined,
    addresses: [5]c.Ref = undefined,
    tsources: [5]u64 = .{ 10, 10, 20, 10, 10 },
    fingerprints: [5]u64 = .{ 1, 2, 3, 4, 5 },
    expansions: [5]u32 = .{ 4, 4, 4, 3, 3 },
    kinds: [5]u32 = .{ 10, 11, 10, 4, 5 },
    scope_kinds: [5]u32 = .{ 4, 4, 4, 3, 3 },
    values: [5]u32 = .{ 3, 3, 3, 3, 3 },
    flags: [5]u32 = @splat(0),
    priorities: [5]u32 = .{ 10, 10, 20, 10, 10 },
    vendors: [5]u32 = .{ 15, 15, 15, 0, 0 },
    applicability: [5]u32 = @splat(0),
    maxima: [5]u32 = .{ 2, 2, 2, 1, 1 },
    minimum: [5]u64 = @splat(0),
    maximum: [5]u64 = @splat(std.math.maxInt(u64)),
    masks: [5]u64 = @splat(1),
    ftemplates: [4]c.Ref = undefined,
    fnames: [4]c.Ref = undefined,
    codes: [4]u32 = .{ 53, 54, 21, 23 },
    resources: [4]u32 = .{ 4, 4, 2, 2 },
    fvendors: [4]u32 = @splat(0),
    legacy: [4]u32 = @splat(1),
    keys: [2]c.Ref = undefined,
    names: [2]c.Ref = undefined,
    sexpansions: [2]u32 = .{ 4, 4 },
    indices: [2]u32 = .{ 1, 0 },
    generations: [2]u64 = .{ 5, 5 },
    luids: [2]u64 = .{ 200, 100 },
    svendors: [2]u32 = .{ 4, 4 },
    sflags: [2]u32 = .{ 1, 1 },
    binding: [2]c.Ref = @as([2]c.Ref, @splat(.{})),
    fn put(f: *Fixture, bytes: []const u8) c.Ref {
        const ref = c.Ref{ .offset = f.used, .length = @intCast(bytes.len) };
        @memcpy(f.arena[f.used..][0..bytes.len], bytes);
        f.used += @intCast(bytes.len);
        return ref;
    }
    fn init(f: *Fixture) void {
        f.* = .{};
        f.source_names = .{ f.put("nvidia.nvml"), f.put("pdh.gpu-engine") };
        f.tnames = .{ f.put("nvml.used"), f.put("nvml.total"), f.put("pdh.used"), f.put("memory.used"), f.put("memory.total") };
        f.addresses = .{ f.put("gpu.{gpu}.vram"), f.put("gpu.{gpu}.vramTotal"), f.put("gpu.{gpu}.vram"), f.put("memory.usage"), f.put("memory.total") };
        f.ftemplates = .{ f.addresses[0], f.addresses[1], f.addresses[3], f.addresses[4] };
        f.fnames = .{ f.put("gpu_all_memory_used_reported_bytes"), f.put("gpu_all_memory_total_reported_bytes"), f.put("memory_all_physical_used_bytes"), f.put("memory_all_physical_total_bytes") };
        f.keys = .{ f.put("adapter-B"), f.put("adapter-A") };
        f.names = .{ f.put("GPU B"), f.put("GPU A") };
    }
    fn facts(f: *Fixture) c.Facts {
        return .{ .text = .{ .bytes = &f.arena, .length = f.used }, .sources = .{ .count = 2, .ids = &f.source_ids, .names = &f.source_names, .incarnations = &f.incarnations, .capabilities = &f.capabilities, .availability = &f.availability, .priorities = &f.source_priorities }, .templates = .{ .count = 5, .names = &f.tnames, .addresses = &f.addresses, .source_ids = &f.tsources, .fingerprints = &f.fingerprints, .expansions = &f.expansions, .kinds = &f.kinds, .scope_kinds = &f.scope_kinds, .value_kinds = &f.values, .flags = &f.flags, .priorities = &f.priorities, .vendor_masks = &f.vendors, .applicability = &f.applicability, .maximum_instances = &f.maxima, .minimum = &f.minimum, .maximum = &f.maximum, .masks = &f.masks }, .fields = .{ .count = 4, .templates = &f.ftemplates, .names = &f.fnames, .codes = &f.codes, .resources = &f.resources, .vendors = &f.fvendors, .legacy = &f.legacy }, .scopes = .{ .count = 2, .keys = &f.keys, .names = &f.names, .expansions = &f.sexpansions, .display_indices = &f.indices, .generations = &f.generations, .luids = &f.luids, .vendor_masks = &f.svendors, .flags = &f.sflags, .binding_keys = &f.binding } };
    }
};
const limits = c.Limits{ .sources = 2, .rules = 8, .metrics = 8, .scopes = 2, .handles = 18, .text_bytes = 8192, .work_bytes = 65536 };
fn makeFixture() !*Fixture {
    const f = try testing.allocator.create(Fixture);
    f.init();
    return f;
}
fn workspace(size: u64) ![]align(8) u8 {
    return testing.allocator.alignedAlloc(u8, .@"8", @intCast(size));
}
fn find(cat: c.Compiled, field: u32, scope: u32, source: u64) !u32 {
    var i: u32 = 0;
    while (i < cat.bindings.count) : (i += 1) if (cat.bindings.field_codes.?[i] == field and cat.bindings.display_indices.?[i] == scope and cat.catalog.rules.source_ids.?[i] == source) return i;
    return error.TestUnexpectedResult;
}
test "old projection fixture: canonical GPU variants, exact scope IDs, legacy keys, flat indices and borrowed output" {
    const f = try makeFixture();
    defer testing.allocator.destroy(f);
    var failure: p.Failure = .{};
    const size = try c.required(f.facts(), limits, &failure);
    const work = try workspace(size);
    defer testing.allocator.free(work);
    const cat = try c.compile(f.facts(), limits, work, &failure);
    try testing.expectEqual(@as(u32, 8), cat.catalog.rules.count);
    const row = try find(cat, 53, 0, 10);
    try testing.expectEqualStrings("2000000000000001/gpu_nvidia_fb_used_v1_including_reserved_bytes", try c.get(cat.text, cat.bindings.addresses.?[row]));
    try testing.expectEqualStrings("gpu.0.vram", try c.get(cat.text, cat.bindings.legacy.?[row]));
    try testing.expectEqualStrings("adapter-A", try c.get(cat.text, cat.bindings.device_keys.?[row]));
    try testing.expectEqual(@as(u64, 1), cat.catalog.rules.ids.?[row]);
    try testing.expectEqual(@as(u64, 1), cat.catalog.rules.metric_ids.?[row]);
    try testing.expectEqual(c.expandedFingerprint(1, 0x2000000000000001, ""), cat.catalog.rules.identities.?[row]);
    try testing.expectEqual(@as(u32, 0), cat.bindings.metric_indices.?[row]);
    try testing.expectEqual(@as(u32, 6), cat.legacy.count);
    var legacy_row: u32 = 0;
    var found_alias = false;
    while (legacy_row < cat.legacy.count) : (legacy_row += 1) if (c.eql(try c.get(cat.text, cat.legacy.keys.?[legacy_row]), "gpu.0.vram")) {
        found_alias = true;
        try testing.expectEqual(row, cat.legacy.rule_indices.?[legacy_row]);
        try testing.expectEqual(@as(u32, 0), cat.legacy.metric_indices.?[legacy_row]);
    };
    try testing.expect(found_alias);
    try testing.expect(c.overlap(@intFromPtr(work.ptr), work.len, @intFromPtr(cat.text.bytes), cat.text.length));
    try testing.expect(c.overlap(@intFromPtr(work.ptr), work.len, @intFromPtr(cat.catalog.rules.ids), cat.catalog.rules.count * 8));
    try testing.expectEqualSlices(u64, &f.source_ids, cat.catalog.sources.ids.?[0..2]);
    // Reclaiming this work invalidates only this borrowed output, never retained owner state.
    @memset(work, 0xA5);
    try testing.expectEqual(@as(u64, 0xA5A5A5A5A5A5A5A5), cat.catalog.rules.ids.?[row]);
}
test "GPU discovery reorder preserves IDs and exact canonical bytes; duplicate identity and caps reject" {
    const f = try makeFixture();
    defer testing.allocator.destroy(f);
    var failure: p.Failure = .{};
    const size = try c.required(f.facts(), limits, &failure);
    const left = try workspace(size);
    defer testing.allocator.free(left);
    const right = try workspace(size);
    defer testing.allocator.free(right);
    const before = try c.compile(f.facts(), limits, left, &failure);
    inline for (.{ "keys", "names", "indices", "luids" }) |name| std.mem.swap(@TypeOf(@field(f, name)[0]), &@field(f, name)[0], &@field(f, name)[1]);
    const after = try c.compile(f.facts(), limits, right, &failure);
    try testing.expectEqualSlices(u64, before.catalog.rules.ids.?[0..8], after.catalog.rules.ids.?[0..8]);
    try testing.expectEqualSlices(u64, before.catalog.rules.metric_ids.?[0..8], after.catalog.rules.metric_ids.?[0..8]);
    var i: u32 = 0;
    while (i < 8) : (i += 1) try testing.expectEqualStrings(try c.get(before.text, before.bindings.addresses.?[i]), try c.get(after.text, after.bindings.addresses.?[i]));
    f.luids[1] = f.luids[0];
    try testing.expectError(error.Invalid, c.compile(f.facts(), limits, right, &failure));
    f.luids[1] = 200;
    var low = limits;
    low.rules = 7;
    try testing.expectError(error.Invalid, c.required(f.facts(), low, &failure));
    try testing.expectEqual(p.Code.capacity_exceeded, failure.code);
    try testing.expectEqual(@as(u64, 8), failure.required);
    try testing.expectError(error.Invalid, c.compile(f.facts(), limits, right[0 .. size - 1], &failure));
    try testing.expectEqual(p.Code.workspace_unavailable, failure.code);
}
test "stateless request projection resolves legacy dependencies and explicit dataset bits; owner roles add implicit inventory work" {
    const f = try makeFixture();
    defer testing.allocator.destroy(f);
    var failure: p.Failure = .{};
    const work = try workspace(try c.required(f.facts(), limits, &failure));
    defer testing.allocator.free(work);
    const cat = try c.compile(f.facts(), limits, work, &failure);
    const sw = try workspace(try sel.required(cat));
    defer testing.allocator.free(sw);
    const epochs = @as([8]u64, @splat(1));
    const support = @as([8]u32, @splat(1));
    const quantities = @as([8]c.Ref, @splat(.{}));
    const facts = sel.ReadingFacts{ .count = 8, .epochs = &epochs, .availability = &support, .quantities = &quantities };
    const request_text = "GPU.0.VRAM";
    var keys = [_]c.Ref{ .{ .length = request_text.len }, .{ .length = request_text.len } };
    var request = sel.Request{ .text = .{ .bytes = request_text, .length = request_text.len }, .count = 2, .keys = &keys };
    var result = try sel.select(cat, facts, request, sw, &failure);
    const used = try find(cat, 53, 0, 10);
    const total = try find(cat, 54, 0, 10);
    try testing.expectEqual(@as(u32, 2), result.metric_count);
    try testing.expectEqualSlices(u64, &.{ cat.catalog.rules.metric_ids.?[used], cat.catalog.rules.metric_ids.?[total] }, result.metric_ids.?[0..2]);
    try testing.expectEqual(@as(u32, 0), result.dataset_mask & 2);
    try testing.expectEqualStrings("gpu.fb_used_including_reserved", try c.get(result.text, result.quantities.?[used]));
    try testing.expectEqualStrings("reading-7ADD119BEA773EA5C41E2172F14D64919A1EBBEE7A299EC156383F65BD055DF5", try c.get(result.text, result.reading_ids.?[used]));
    try testing.expectEqual(used, result.preferred_rules.?[used]);
    try testing.expectEqual(@as(u32, 2), result.source_offsets.?[1]);
    try testing.expectEqual(@as(u32, 2), result.source_offsets.?[2]);
    const memory_text = "memory.usage";
    request.count = 1;
    request.text = .{ .bytes = memory_text, .length = memory_text.len };
    keys[0].length = memory_text.len;
    result = try sel.select(cat, facts, request, sw, &failure);
    try testing.expectEqual(@as(u32, 2), result.metric_count);
    const empty = try sel.select(cat, facts, .{}, sw, &failure);
    try testing.expectEqual(@as(u32, 0), empty.metric_count);
    const probe = try sel.select(cat, facts, .{ .flags = 4 }, sw, &failure);
    try testing.expectEqual(@as(u32, 0), probe.metric_count);
    try testing.expectEqual(@as(u32, 1), probe.dataset_mask);
    request.flags = 2;
    request.count = 0;
    result = try sel.select(cat, facts, request, sw, &failure);
    try testing.expectEqual(@as(u32, 6), result.metric_count);
}
test "provider completion checks exact observation, retains unsupported and respects cooldown boundary" {
    var state = sel.ProviderState{ .incarnation = 10, .capability = 2, .observation = 0 };
    try testing.expectEqual(@as(u64, 1), try sel.providerBegin(&state, 100));
    var event = sel.ProviderEvent{ .incarnation = 10, .capability = 2, .observation = 0, .completed_ms = 100, .cooldown_ms = 50, .status = 3, .apply_cooldown = 1 };
    try testing.expectError(error.Identity, sel.providerComplete(&state, event));
    try testing.expectEqual(@as(i64, 0), state.unavailable_until_ms);
    event.observation = 1;
    try sel.providerComplete(&state, event);
    try testing.expectEqual(@as(u32, 2), sel.providerAvailability(state, 149));
    try testing.expectError(error.Invalid, sel.providerBegin(&state, 149));
    try testing.expectEqual(@as(u64, 2), try sel.providerBegin(&state, 150));
    event.observation = 2;
    event.status = 4;
    try sel.providerComplete(&state, event);
    try testing.expectEqual(@as(u32, 3), sel.providerAvailability(state, 1000));
    try testing.expectError(error.Invalid, sel.providerBegin(&state, 1000));
}
test "persisted handle map survives template reorder and exactly admitted map capacity" {
    const f = try makeFixture();
    defer testing.allocator.destroy(f);
    var failure: p.Failure = .{};
    const first_work = try workspace(65536);
    defer testing.allocator.free(first_work);
    const second_work = try workspace(65536);
    defer testing.allocator.free(second_work);
    const first = try c.compile(f.facts(), limits, first_work, &failure);
    const persisted_keys = try testing.allocator.alloc(c.Ref, first.handles.count);
    defer testing.allocator.free(persisted_keys);
    var i: u32 = 0;
    while (i < first.handles.count) : (i += 1) persisted_keys[i] = f.put(try c.get(first.text, first.handles.keys.?[i]));
    inline for (.{ "tnames", "addresses", "tsources", "fingerprints", "expansions", "kinds", "scope_kinds", "values", "flags", "priorities", "vendors", "applicability", "maxima", "minimum", "maximum", "masks" }) |name| std.mem.swap(@TypeOf(@field(f, name)[0]), &@field(f, name)[0], &@field(f, name)[2]);
    var facts = f.facts();
    facts.handles = first.handles;
    facts.handles.keys = persisted_keys.ptr;
    var exact_limits = limits;
    exact_limits.handles = first.handles.count;
    const second = try c.compile(facts, exact_limits, second_work, &failure);
    try testing.expectEqual(first.handles.count, second.handles.count);
    try testing.expectEqualSlices(u64, first.catalog.rules.ids.?[0..8], second.catalog.rules.ids.?[0..8]);
    try testing.expectEqualSlices(u64, first.catalog.rules.metric_ids.?[0..8], second.catalog.rules.metric_ids.?[0..8]);
    exact_limits.handles -= 1;
    try testing.expectError(error.Invalid, c.compile(facts, exact_limits, second_work, &failure));
    try testing.expectEqual(p.Family.binding, failure.family);
}
test "native support learning ignores late identity, retains support on unavailable, and resets mismatched capability" {
    const f = try makeFixture();
    defer testing.allocator.destroy(f);
    var failure: p.Failure = .{};
    const catalog_work = try workspace(65536);
    defer testing.allocator.free(catalog_work);
    const cat = try c.compile(f.facts(), limits, catalog_work, &failure);
    const learn_work = try workspace(try sel.observeRequired(cat));
    defer testing.allocator.free(learn_work);
    const next_work = try workspace(try sel.observeRequired(cat));
    defer testing.allocator.free(next_work);
    const epochs = @as([8]u64, @splat(1));
    const support = @as([8]u32, @splat(0));
    const quantities = @as([8]c.Ref, @splat(.{}));
    const prior = sel.ReadingFacts{ .count = 8, .epochs = &epochs, .availability = &support, .quantities = &quantities };
    const row = try find(cat, 53, 0, 10);
    const rows = [_]u32{row};
    var inc = [_]u64{99};
    const caps = [_]u64{1};
    const observations = [_]u64{5};
    const current = [_]u64{ 5, 0 };
    var statuses = [_]u32{1};
    var supported = [_]u32{0};
    const unsupported = [_]u32{0};
    const q = [_]c.Ref{.{}};
    const events = sel.ReadingEvents{ .count = 1, .rule_indices = &rows, .incarnations = &inc, .capabilities = &caps, .observations = &observations, .statuses = &statuses, .supported = &supported, .source_unsupported = &unsupported, .quantities = &q, .current_observations = &current };
    var learned = try sel.observe(cat, prior, events, learn_work, &failure);
    try testing.expectEqual(@as(u32, 0), learned.availability.?[row]);
    inc[0] = 1;
    learned = try sel.observe(cat, prior, events, learn_work, &failure);
    try testing.expectEqual(@as(u32, 1), learned.availability.?[row]);
    statuses[0] = 2;
    const retained = try sel.observe(cat, learned, events, next_work, &failure);
    try testing.expectEqual(@as(u32, 1), retained.availability.?[row]);
    var changed_epochs = epochs;
    changed_epochs[row] = 99;
    var changed = retained;
    changed.epochs = &changed_epochs;
    const reset = try sel.observe(cat, changed, events, learn_work, &failure);
    try testing.expectEqual(@as(u32, 0), reset.availability.?[row]);
    statuses[0] = 3;
    supported[0] = 1;
    const actual_unsupported = try sel.observe(cat, reset, events, next_work, &failure);
    try testing.expectEqual(@as(u32, 2), actual_unsupported.availability.?[row]);
}
fn regionBinding(region: []align(8) u8, capacity: u32, payload: u32) !inv.Binding {
    const sources = [_]ss.SourceIdentity{try ss.SourceIdentity.init("fixture GPU inventory", 1, 0, 0)};
    var specs: [14]ss.PositionSpec = undefined; // fixed fixture schema, 392 bytes
    var offset: u32 = 0;
    var i: u32 = 0;
    while (i < 14) : (i += 1) {
        const width: u32 = if (i == 13) 1 else if (i == 10) 4 else 8;
        const size = if (i == 13) payload else capacity * width;
        specs[i] = .{ .source = 0, .kind = if (i == 13) .string_table else if (i == 12) .string_column else .column, .offset = offset, .size = size, .element_width = width, .identity = if (i == 0 or i == 13) p.none else 0, .strings = if (i == 12) 13 else p.none };
        offset = std.mem.alignForward(u32, offset + size, 8);
    }
    try ss.initialize(region, 1, 1, 14, offset, &sources, &specs);
    return .{ .region = region.ptr, .length = region.len, .source = 0, .positions = .{ 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13 } };
}
test "inventory publishes sorted exact identity and rejects changed LUID before writes; not requested and empty differ" {
    const region = try workspace(4096);
    defer testing.allocator.free(region);
    const binding = try regionBinding(region, 2, 32);
    const work = try workspace(512);
    defer testing.allocator.free(work);
    const ids = [_]u64{ 2, 1 };
    var low = [_]u64{ 20, 10 };
    const high = [_]u64{ 0, 0 };
    const stable = [_]u64{ 200, 100 };
    const masks = [_]u64{ 1, 1 };
    const topology = [_]u64{ 5, 5 };
    const keys = [_]c.Ref{ .{ .length = 1 }, .{ .offset = 1, .length = 1 } };
    var facts = inv.Facts{ .columns = .{ .count = 2, .adapter_ids = &ids, .luid_low = &low, .luid_high = &high, .stable_key_ids = &stable, .capability_masks = &masks, .topology = &topology, .keys = &keys }, .text = .{ .bytes = "BA", .length = 2 }, .source_id = 10, .generation = 1, .observation = 1, .observed_ms = 100, .source_capability_mask = 1, .requested = 1, .maximum_count = 2 };
    var failure: p.Failure = .{};
    const out = try inv.publish(facts, binding, work, &failure);
    try testing.expectEqualSlices(u64, &.{ 1, 2 }, out.columns.adapter_ids.?[0..2]);
    try testing.expectEqualStrings("A", try c.get(out.text, out.columns.keys.?[0]));
    const layout = try ss.layout.Layout.load(region);
    const sequence = @atomicLoad(u64, &layout.source(region, 0).sequence, .acquire);
    try testing.expectEqual(@as(u64, 2), sequence);
    low[1] = 99;
    try testing.expectError(error.Identity, inv.publish(facts, binding, work, &failure));
    try testing.expectEqual(sequence, @atomicLoad(u64, &layout.source(region, 0).sequence, .acquire));
    low[1] = 10;
    facts.columns.count = 0;
    facts.requested = 0;
    try testing.expectEqual(@as(u32, 0), (try inv.publish(facts, binding, work, &failure)).published);
    try testing.expectEqual(sequence, @atomicLoad(u64, &layout.source(region, 0).sequence, .acquire));
    facts.requested = 1;
    _ = try inv.publish(facts, binding, work, &failure);
    try testing.expectEqual(@as(u32, 0), @atomicLoad(u32, &layout.position(region, 0).length, .acquire));
    try testing.expectEqual(ss.SourceState.has_value, @atomicLoad(ss.SourceState, &layout.source(region, 0).state, .acquire));
    facts.status = 3;
    facts.reason = 77;
    _ = try inv.publish(facts, binding, work, &failure);
    try testing.expectEqual(ss.SourceState.unavailable, @atomicLoad(ss.SourceState, &layout.source(region, 0).state, .acquire));
    try testing.expectEqual(@as(u32, 77), @atomicLoad(u32, &layout.source(region, 0).reason, .acquire));
}
test "inventory UTC epoch zero is a completed time and unchanged content stays unpublished" {
    const region = try workspace(4096);
    defer testing.allocator.free(region);
    const binding = try regionBinding(region, 1, 16);
    const work = try workspace(512);
    defer testing.allocator.free(work);
    const ids = [_]u64{1};
    const topology = [_]u64{0};
    const high = [_]u64{0};
    const refs = [_]c.Ref{.{ .length = 1 }};
    var facts = inv.Facts{ .columns = .{ .count = 1, .adapter_ids = &ids, .luid_low = &ids, .luid_high = &high, .stable_key_ids = &ids, .capability_masks = &ids, .topology = &topology, .keys = &refs }, .text = .{ .bytes = "A", .length = 1 }, .source_id = 10, .generation = 1, .observation = 1, .observed_ms = 0, .source_capability_mask = 1, .requested = 1, .maximum_count = 1 };
    var failure: p.Failure = .{};
    try testing.expectEqual(@as(u32, 1), (try inv.publish(facts, binding, work, &failure)).published);
    const layout = try ss.layout.Layout.load(region);
    try testing.expectEqual(@as(i64, 0), layout.source(region, 0).write_time_unix_ms);
    const sequence = layout.source(region, 0).sequence;
    facts.generation = 2;
    facts.observation = 2;
    try testing.expectEqual(@as(u32, 0), (try inv.publish(facts, binding, work, &failure)).published);
    facts.observed_ms = -1;
    try testing.expectError(error.Invalid, inv.publish(facts, binding, work, &failure));
    try testing.expectEqual(sequence, layout.source(region, 0).sequence);
}

test "C ABI null and malformed UTF8 reject without replacing caller result" {
    var failure: p.Failure = .{};
    var size: u64 = 123;
    try testing.expectEqual(@backingInt(p.Code.invalid_contract), exports.rm_metric_discovery_catalog_size(null, &limits, &size, &failure));
    try testing.expectEqual(@as(u64, 123), size);
    try testing.expectError(error.Invalid, c.get(.{ .bytes = &.{0xff}, .length = 1 }, .{ .length = 1 }));
    try testing.expectError(error.Invalid, c.get(.{ .bytes = "A", .length = 1 }, .{ .offset = std.math.maxInt(u32), .length = 2 }));
}
test "inventory stage is cold until admission, unchanged has no clock refresh, and rollback preserves coherent rows" {
    const region = try workspace(4096);
    defer testing.allocator.free(region);
    const binding = try regionBinding(region, 1, 32);
    const work = try workspace(512);
    defer testing.allocator.free(work);
    const ids = [_]u64{1};
    const low = [_]u64{10};
    const high = [_]u64{0};
    const masks = [_]u64{1};
    var topology = [_]u64{5};
    const keys = [_]c.Ref{.{ .length = 1 }};
    var facts = inv.Facts{ .columns = .{ .count = 1, .adapter_ids = &ids, .luid_low = &low, .luid_high = &high, .stable_key_ids = &ids, .capability_masks = &masks, .topology = &topology, .keys = &keys }, .text = .{ .bytes = "A", .length = 1 }, .source_id = 10, .generation = 1, .observation = 1, .observed_ms = 100, .source_capability_mask = 1, .requested = 1, .maximum_count = 1 };
    var failure: p.Failure = .{};
    _ = try inv.publish(facts, binding, work, &failure);
    const layout = try ss.layout.Layout.load(region);
    const entry = layout.source(region, 0);
    facts.observed_ms = 200;
    facts.generation = 2;
    var unchanged = try inv.stage(facts, binding, work, &failure);
    try testing.expect(!unchanged.changed);
    try unchanged.admit();
    try testing.expectEqual(@as(u32, 0), unchanged.publishAccepted().published);
    try testing.expectEqual(@as(u64, 2), @atomicLoad(u64, &entry.sequence, .acquire));
    try testing.expectEqual(@as(i64, 100), @atomicLoad(i64, &entry.write_time_unix_ms, .acquire));
    topology[0] = 6;
    var staged = try inv.stage(facts, binding, work, &failure);
    try testing.expect(staged.changed);
    try testing.expectEqual(@as(u64, 2), @atomicLoad(u64, &entry.sequence, .acquire));
    try staged.admit();
    try testing.expectEqual(@as(u64, 3), @atomicLoad(u64, &entry.sequence, .acquire));
    staged.abort();
    try testing.expectEqual(@as(u64, 4), @atomicLoad(u64, &entry.sequence, .acquire));
    try testing.expectEqual(@as(i64, 100), @atomicLoad(i64, &entry.write_time_unix_ms, .acquire));
    try testing.expectEqual(@as(u64, 4), @atomicLoad(u64, &layout.position(region, 12).identity_sequence, .acquire));
    staged = try inv.stage(facts, binding, work, &failure);
    try staged.admit();
    try testing.expectEqual(@as(u32, 1), staged.publishAccepted().published);
    try testing.expectEqual(@as(u64, 6), @atomicLoad(u64, &entry.sequence, .acquire));
}
test "inventory payload and row caps reject before public sequence changes" {
    const region = try workspace(4096);
    defer testing.allocator.free(region);
    const binding = try regionBinding(region, 1, 1);
    const work = try workspace(512);
    defer testing.allocator.free(work);
    const ids = [_]u64{1};
    const low = [_]u64{10};
    const high = [_]u64{0};
    const stable = [_]u64{100};
    const masks = [_]u64{1};
    const topology = [_]u64{1};
    var keys = [_]c.Ref{.{ .length = 2 }};
    var facts = inv.Facts{ .columns = .{ .count = 1, .adapter_ids = &ids, .luid_low = &low, .luid_high = &high, .stable_key_ids = &stable, .capability_masks = &masks, .topology = &topology, .keys = &keys }, .text = .{ .bytes = "AB", .length = 2 }, .source_id = 10, .generation = 1, .observation = 1, .observed_ms = 1, .source_capability_mask = 1, .requested = 1, .maximum_count = 1 };
    var failure: p.Failure = .{};
    try testing.expectError(error.Payload, inv.publish(facts, binding, work, &failure));
    try testing.expectEqual(p.Code.payload_capacity, failure.code);
    const layout = try ss.layout.Layout.load(region);
    try testing.expectEqual(@as(u64, 0), @atomicLoad(u64, &layout.source(region, 0).sequence, .acquire));
    var misaligned = binding;
    misaligned.region = region.ptr + 1;
    misaligned.length -= 1;
    try testing.expectError(error.Invalid, inv.publish(facts, misaligned, work, &failure));
    try testing.expectEqual(@as(u64, 0), @atomicLoad(u64, &layout.source(region, 0).sequence, .acquire));
    keys[0].offset = std.math.maxInt(u32);
    try testing.expectError(error.Invalid, inv.publish(facts, binding, work, &failure));
    try testing.expectEqual(@as(u64, 0), @atomicLoad(u64, &layout.source(region, 0).sequence, .acquire));
    facts.maximum_count = 0;
    try testing.expectError(error.Capacity, inv.publish(facts, binding, work, &failure));
    try testing.expectEqual(p.Family.binding, failure.family);
    try testing.expectEqual(@as(u64, 1), failure.required);
}
test "Intel field applicability uses raw DTS, thermal, RAPL and frequency evidence natively" {
    var expansions = [_]u32{11};
    var flags = [_]u32{768};
    const thermal = [_]u32{7};
    const rapl = [_]u32{9};
    const power = [_]u32{9};
    var mhz = [_]u32{0};
    const scopes = c.Scopes{ .count = 1, .expansions = &expansions, .flags = &flags, .thermal_caps = &thermal, .rapl_domains = &rapl, .power_domains = &power, .base_mhz = &mhz };
    try testing.expect(c.intelApplies(scopes, 0, 147));
    try testing.expect(!c.intelApplies(scopes, 0, 146));
    try testing.expect(c.intelApplies(scopes, 0, 152));
    try testing.expect(!c.intelApplies(scopes, 0, 156));
    try testing.expect(c.intelApplies(scopes, 0, 175));
    try testing.expect(!c.intelApplies(scopes, 0, 176));
    flags[0] |= 2048;
    try testing.expect(c.intelApplies(scopes, 0, 176));
    expansions[0] = 12;
    flags[0] = 1024;
    try testing.expect(c.intelApplies(scopes, 0, 168));
    try testing.expect(!c.intelApplies(scopes, 0, 310));
    mhz[0] = 3600;
    try testing.expect(c.intelApplies(scopes, 0, 310));
}
test "empty discovery has no implicit fields or datasets and exact work bound is sufficient" {
    var failure: p.Failure = .{};
    const cap = c.Limits{ .sources = 0, .rules = 0, .metrics = 0, .scopes = 0, .handles = 0, .text_bytes = 0, .work_bytes = 1024 };
    const memory = try workspace(try c.required(.{}, cap, &failure));
    defer testing.allocator.free(memory);
    const cat = try c.compile(.{}, cap, memory, &failure);
    try testing.expectEqual(@as(u32, 0), cat.catalog.rules.count);
    try testing.expectEqual(@as(u32, 0), cat.handles.count);
    const selected_work = try workspace(try sel.required(cat));
    defer testing.allocator.free(selected_work);
    const selected = try sel.select(cat, .{}, .{}, selected_work, &failure);
    try testing.expectEqual(@as(u32, 0), selected.metric_count);
    try testing.expectEqual(@as(u32, 0), selected.dataset_mask);
}
test "conflicting exact native metric identity rejects without replacing ABI descriptor" {
    const f = try makeFixture();
    defer testing.allocator.destroy(f);
    f.source_names[1] = f.put("windows.memory");
    f.addresses[2] = f.addresses[3];
    f.expansions[2] = 3;
    f.scope_kinds[2] = 3;
    f.kinds[2] = 5;
    const work = try workspace(65536);
    defer testing.allocator.free(work);
    var out: c.Compiled = .{};
    out.catalog.metrics.count = 123;
    var failure: p.Failure = .{};
    const facts = f.facts();
    try testing.expectEqual(@backingInt(p.Code.stale_identity), exports.rm_metric_discovery_compile(&facts, &limits, work.ptr, work.len, &out, &failure));
    try testing.expectEqual(@as(u32, 123), out.catalog.metrics.count);
    var tiny = limits;
    tiny.text_bytes = 1;
    try testing.expectError(error.Payload, c.compile(f.facts(), tiny, work, &failure));
    try testing.expectEqual(p.Family.payload, failure.family);
    try testing.expectEqual(@as(u64, 1), failure.limit);
    try testing.expect(!c.exact("\xc2\xa0identity"));
}
test "raw GPU inventory joins canonical LUID and inventory generation natively without managed IDs" {
    const f = try makeFixture();
    defer testing.allocator.destroy(f);
    var failure: p.Failure = .{};
    const catalog_work = try workspace(65536);
    defer testing.allocator.free(catalog_work);
    const cat = try c.compile(f.facts(), limits, catalog_work, &failure);
    const region = try workspace(4096);
    defer testing.allocator.free(region);
    const binding = try regionBinding(region, 2, 32);
    var luids = [_]u64{ 200, 100 };
    var facts = inv.CatalogFacts{ .raw = .{ .columns = .{ .count = 2, .luid_low = &luids }, .source_id = 10, .generation = 1, .observation = 1, .observed_ms = 100, .source_capability_mask = 7, .requested = 1, .maximum_count = 2 }, .inventory_generation = 5, .topology_fingerprint = 99, .observed_count = 2 };
    const memory = try workspace(try inv.catalogRequired(facts));
    defer testing.allocator.free(memory);
    const output = try inv.publishCatalog(cat, facts, binding, memory, &failure);
    try testing.expectEqualSlices(u64, &.{ 0x2000000000000001, 0x2000000000000002 }, output.columns.adapter_ids.?[0..2]);
    try testing.expectEqualSlices(u64, output.columns.adapter_ids.?[0..2], output.columns.stable_key_ids.?[0..2]);
    try testing.expectEqualStrings("adapter-A", try c.get(output.text, output.columns.keys.?[0]));
    try testing.expectEqual(@as(u64, 7), output.columns.capability_masks.?[0]);
    try testing.expectEqual(@as(u64, 99), output.columns.topology.?[0]);
    try testing.expectEqual(@as(u64, 1), output.generation);
    const layout = try ss.layout.Layout.load(region);
    const sequence = @atomicLoad(u64, &layout.source(region, 0).sequence, .acquire);
    try testing.expectEqual(@as(u32, 0), (try inv.publishCatalog(cat, facts, binding, memory, &failure)).published);
    try testing.expectEqual(sequence, @atomicLoad(u64, &layout.source(region, 0).sequence, .acquire));
    facts.inventory_generation = 6;
    try testing.expectError(error.Identity, inv.publishCatalog(cat, facts, binding, memory, &failure));
    try testing.expectEqual(sequence, @atomicLoad(u64, &layout.source(region, 0).sequence, .acquire));
    facts.inventory_generation = 5;
    luids[0] = 999;
    try testing.expectError(error.Identity, inv.publishCatalog(cat, facts, binding, memory, &failure));
    try testing.expectEqual(sequence, @atomicLoad(u64, &layout.source(region, 0).sequence, .acquire));
    luids[0] = 200;
    facts.skipped_count = 1;
    try testing.expectError(error.Invalid, inv.publishCatalog(cat, facts, binding, memory, &failure));
    try testing.expectEqual(sequence, @atomicLoad(u64, &layout.source(region, 0).sequence, .acquire));
}
