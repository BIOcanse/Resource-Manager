//! GPU inventory has exactly one published authority: the caller's bound table.
const std = @import("std");
const ss = @import("rm_shared_section");
const c = @import("catalog_compiler.zig");
const p = c.p;
pub const Columns = extern struct {
    count: u32 = 0,
    reserved: u32 = 0,
    adapter_ids: ?[*]const u64 = null,
    luid_low: ?[*]const u64 = null,
    luid_high: ?[*]const u64 = null,
    stable_key_ids: ?[*]const u64 = null,
    capability_masks: ?[*]const u64 = null,
    topology: ?[*]const u64 = null,
    keys: ?[*]const c.Ref = null,
};
pub const Facts = extern struct {
    columns: Columns = .{},
    text: c.Text = .{},
    source_id: u64 = 0,
    generation: u64 = 0,
    observation: u64 = 0,
    observed_ms: i64 = 0,
    source_capability_mask: u64 = 0,
    requested: u32 = 0,
    status: u32 = 1, // SourceStatus:complete1/partial2/unavailable3/unsupported4
    reason: u32 = 0,
    maximum_count: u32 = 0,
};
/// All positions belong to one caller-declared inventory source, never a new
/// mapping. Slots: adapter/source/generation/observed/luidLow/luidHigh/keyID/
/// capability/topology/valid/status/fingerprint/exactKey/stringTable.
pub const Binding = extern struct {
    region: ?[*]u8,
    length: u64,
    source: u32,
    reserved: u32 = 0,
    positions: [14]u32,
};
pub const Output = extern struct {
    columns: Columns = .{},
    text: c.Text = .{},
    fingerprints: ?[*]const u64 = null,
    source_id: u64 = 0,
    generation: u64 = 0,
    observed_ms: i64 = 0,
    status: u32 = 0,
    published: u32 = 0,
};
/// Raw live-directory facts. IDs/keys/masks/high in raw.columns are not required:
/// native resolves them from the canonical catalog and the packed Windows LUID.
pub const CatalogFacts = extern struct {
    raw: Facts = .{},
    inventory_generation: u64 = 0,
    topology_fingerprint: u64 = 0,
    observed_count: u32 = 0,
    skipped_count: u32 = 0,
    overflow_count: u32 = 0,
    reserved: u32 = 0,
};
pub fn catalogRequired(facts: CatalogFacts) c.Error!u64 {
    if (facts.reserved != 0 or facts.raw.columns.reserved != 0 or facts.raw.requested > 1 or facts.raw.status < 1 or facts.raw.status > 5) return error.Invalid;
    if (facts.raw.columns.count > facts.raw.maximum_count) return error.Capacity;
    return @as(u64, facts.raw.columns.count) * 112 + 256;
}
pub fn publishCatalog(cat: c.Compiled, facts: CatalogFacts, binding: Binding, work: []align(8) u8, failure: *p.Failure) c.Error!Output {
    var prepared = try stageCatalog(cat, facts, binding, work, failure);
    try prepared.admit();
    return prepared.publishAccepted();
}
pub fn stageCatalog(cat: c.Compiled, facts: CatalogFacts, binding: Binding, work: []align(8) u8, failure: *p.Failure) c.Error!Prepared {
    failure.* = .{};
    const size = catalogRequired(facts) catch |err| {
        if (err == error.Capacity) failure.* = .{ .code = .capacity_exceeded, .family = .binding, .required = facts.raw.columns.count, .limit = facts.raw.maximum_count };
        return err;
    };
    if (work.len < size) return c.fail(failure, .workspace_unavailable, .work, size, work.len);
    const input = facts.raw;
    if (input.requested == 0 or input.status != 1) return stage(input, binding, work, failure);
    if (facts.observed_count != input.columns.count or facts.skipped_count != 0 or facts.overflow_count != 0 or !c.pointers(c.Scopes, cat.scopes) or !c.pointers(c.Handles, cat.handles)) return error.Invalid;
    if (!c.pointers(c.Sources, cat.sources)) return error.Invalid;
    var known_source = false;
    var source_index: u32 = 0;
    while (source_index < cat.sources.count) : (source_index += 1) if (cat.sources.ids.?[source_index] == input.source_id) {
        known_source = true;
    };
    if (!known_source) return error.Identity;
    if (facts.inventory_generation == 0 or (input.columns.count != 0 and input.columns.luid_low == null)) return error.Invalid;
    if (!c.disjointColumns(Columns, input.columns, input.columns.count, work) or !c.disjointColumns(c.Scopes, cat.scopes, cat.scopes.count, work) or !c.disjointColumns(c.Handles, cat.handles, cat.handles.count, work) or c.overlap(@intFromPtr(work.ptr), work.len, @intFromPtr(cat.text.bytes), cat.text.length) or c.overlap(@intFromPtr(work.ptr), work.len, @intFromPtr(cat.fact_text.bytes), cat.fact_text.length) or c.overlap(@intFromPtr(work.ptr), work.len, @intFromPtr(input.text.bytes), input.text.length)) return error.Invalid;
    var fba = std.heap.FixedBufferAllocator.init(work);
    const a = fba.allocator();
    const ids = a.alloc(u64, input.columns.count) catch return error.Work;
    const high = a.alloc(u64, input.columns.count) catch return error.Work;
    const masks = a.alloc(u64, input.columns.count) catch return error.Work;
    const keys = a.alloc(c.Ref, input.columns.count) catch return error.Work;
    const topology = a.alloc(u64, input.columns.count) catch return error.Work;
    var row: u32 = 0;
    while (row < input.columns.count) : (row += 1) {
        var scope: ?u32 = null;
        var si: u32 = 0;
        while (si < cat.scopes.count) : (si += 1) if (cat.scopes.expansions.?[si] == 4 and cat.scopes.luids.?[si] == input.columns.luid_low.?[row]) {
            if (scope != null) return error.Identity;
            scope = si;
        };
        const matched = scope orelse return error.Identity;
        if (cat.scopes.generations.?[matched] != facts.inventory_generation or (input.columns.luid_high != null and input.columns.luid_high.?[row] != 0)) return error.Identity;
        const key = try c.get(cat.fact_text, cat.scopes.keys.?[matched]);
        var handle: ?u64 = null;
        var hi: u32 = 0;
        while (hi < cat.handles.count) : (hi += 1) if (cat.handles.kinds.?[hi] == 1) {
            const exact_key = try c.get(cat.text, cat.handles.keys.?[hi]);
            if (std.mem.startsWith(u8, exact_key, "gpu|") and c.eql(exact_key[4..], key)) {
                if (handle != null) return error.Identity;
                handle = cat.handles.ids.?[hi];
            }
        };
        ids[row] = handle orelse return error.Identity;
        high[row] = 0;
        masks[row] = input.source_capability_mask;
        keys[row] = cat.scopes.keys.?[matched];
        topology[row] = facts.topology_fingerprint;
        if ((input.columns.adapter_ids != null and input.columns.adapter_ids.?[row] != ids[row]) or (input.columns.stable_key_ids != null and input.columns.stable_key_ids.?[row] != ids[row])) return error.Identity;
        if (input.columns.keys != null and !c.eql(try c.get(input.text, input.columns.keys.?[row]), key)) return error.Identity;
    }
    var resolved = input;
    resolved.columns.adapter_ids = ids.ptr;
    resolved.columns.stable_key_ids = ids.ptr;
    resolved.columns.luid_high = high.ptr;
    resolved.columns.capability_masks = masks.ptr;
    resolved.columns.keys = keys.ptr;
    resolved.columns.topology = topology.ptr;
    resolved.text = cat.fact_text;
    const offset = std.mem.alignForward(usize, fba.end_index, 8);
    return stage(resolved, binding, @alignCast(work[offset..]), failure);
}
pub fn required(facts: Facts) c.Error!u64 {
    if (facts.columns.reserved != 0 or facts.text.reserved != 0 or facts.requested > 1 or facts.status < 1 or facts.status > 5) return error.Invalid;
    if (facts.columns.count > facts.maximum_count) return error.Capacity;
    if (facts.columns.count != 0) inline for (@typeInfo(Columns).@"struct".field_names, @typeInfo(Columns).@"struct".field_types) |field_name, field_type| {
        if (@typeInfo(field_type) == .optional and @field(facts.columns, field_name) == null) return error.Invalid;
    };
    return @as(u64, facts.columns.count) * 72 + 128;
}
fn fingerprint(facts: Facts, i: u32) u64 {
    const input = facts.columns;
    // Exact old inventory semantic hash. Timestamps/source generation excluded.
    const values = [_]u64{ 0, input.adapter_ids.?[i], facts.source_id, input.luid_low.?[i], input.luid_high.?[i], input.stable_key_ids.?[i], input.capability_masks.?[i], input.topology.?[i], 15, 1 };
    const hash = std.hash.Wyhash.hash(0x726d6770756f7574, std.mem.asBytes(&values));
    return if (hash == 0) 1 else hash;
}
const Table = struct {
    region: []align(8) u8,
    binding: Binding,
    layout: ss.layout.Layout,
    specs: [14]ss.PositionSpec = undefined, // fixed 14-position descriptor, 392 bytes
    fn init(binding: Binding, rows: u32, payload: u64, failure: *p.Failure) c.Error!Table {
        const base = binding.region orelse return error.Invalid;
        if (binding.reserved != 0 or @intFromPtr(base) % 8 != 0 or binding.length > std.math.maxInt(usize)) return error.Invalid;
        const region: []align(8) u8 = @alignCast(base[0..@intCast(binding.length)]);
        const layout = ss.layout.Layout.load(region) catch return error.Invalid;
        if (binding.source >= layout.header.source_count) return error.Invalid;
        const sequence = @atomicLoad(u64, &layout.source(region, binding.source).sequence, .acquire);
        if (sequence & (ss.ended_bit | 1) != 0 or sequence > ss.ended_bit - 4) return error.Invalid;
        var table = Table{ .region = region, .binding = binding, .layout = layout };
        var i: u32 = 0;
        while (i < 14) : (i += 1) {
            const spec = layout.checkedSpec(region, binding.positions[i]) catch return error.Invalid;
            const width: u32 = if (i == 13) 1 else if (i == 10) 4 else 8;
            const kind: ss.PositionKind = if (i == 13) .string_table else if (i == 12) .string_column else .column;
            if (spec.source != binding.source or spec.element_width != width or spec.kind != kind or spec.identity != (if (i == 0 or i == 13) p.none else binding.positions[0]) or (i == 12 and spec.strings != binding.positions[13])) return error.Invalid;
            if (i == 13) {
                if (spec.size < payload) {
                    failure.* = .{ .code = .payload_capacity, .family = .payload, .required = payload, .limit = spec.size };
                    return error.Payload;
                }
            } else if (spec.size / width < rows) {
                failure.* = .{ .code = .capacity_exceeded, .family = .binding, .required = rows, .limit = spec.size / width };
                return error.Capacity;
            }
            var j: u32 = 0;
            while (j < i) : (j += 1) if (binding.positions[j] == binding.positions[i] or c.overlap(spec.offset, spec.size, table.specs[j].offset, table.specs[j].size)) return error.Invalid;
            table.specs[i] = spec;
        }
        return table;
    }
    fn load(table: *const Table, comptime T: type, slot: u32, row: u32) T {
        const at = table.layout.dataArea(table.region).ptr + table.specs[slot].offset + @as(usize, row) * @sizeOf(T);
        const ptr: *const T = @ptrCast(@alignCast(at));
        return @atomicLoad(T, ptr, .acquire);
    }
    fn count(table: *const Table) c.Error!u32 {
        const position = table.layout.position(table.region, table.binding.positions[0]);
        const rows = @atomicLoad(u32, &position.length, .acquire);
        const sequence = @atomicLoad(u64, &table.layout.source(table.region, table.binding.source).sequence, .acquire);
        var slot: u32 = 0;
        while (slot < 13) : (slot += 1) {
            const pos = table.layout.position(table.region, table.binding.positions[slot]);
            if (@atomicLoad(u32, &pos.length, .acquire) != rows or rows > table.specs[slot].size / table.specs[slot].element_width) return error.Invalid;
            if (rows != 0 and slot != 0 and @atomicLoad(u64, &pos.identity_sequence, .acquire) != sequence) return error.Identity;
        }
        return rows;
    }
    fn key(table: *const Table, row: u32) c.Error![]const u8 {
        const reference = table.load(u64, 12, row);
        const length = @atomicLoad(u32, &table.layout.position(table.region, table.binding.positions[13]).length, .acquire);
        if (length > table.specs[13].size) return error.Invalid;
        const text = c.Text{ .bytes = table.layout.dataArea(table.region).ptr + table.specs[13].offset, .length = length };
        return c.get(text, .{ .offset = @truncate(reference), .length = @truncate(reference >> 32) });
    }
    fn validPrior(table: *const Table, row: u32) bool {
        if (table.load(u64, 0, row) == 0 or table.load(u64, 1, row) == 0 or table.load(u64, 2, row) == 0 or @as(i64, @bitCast(table.load(u64, 3, row))) < 0 or (table.load(u64, 4, row) == 0 and table.load(u64, 5, row) == 0) or table.load(u64, 6, row) == 0 or table.load(u64, 9, row) != 15 or table.load(u32, 10, row) != 1) return false;
        const values = [_]u64{ 0, table.load(u64, 0, row), table.load(u64, 1, row), table.load(u64, 4, row), table.load(u64, 5, row), table.load(u64, 6, row), table.load(u64, 7, row), table.load(u64, 8, row), 15, 1 }; // fixed ten semantic fields,80 bytes
        const hash = std.hash.Wyhash.hash(0x726d6770756f7574, std.mem.asBytes(&values));
        return table.load(u64, 11, row) == (if (hash == 0) @as(u64, 1) else hash);
    }
};
pub fn publish(facts: Facts, binding: Binding, work: []align(8) u8, failure: *p.Failure) c.Error!Output {
    var prepared = try stage(facts, binding, work, failure);
    try prepared.admit();
    return prepared.publishAccepted();
}
pub fn stage(facts: Facts, binding: Binding, work: []align(8) u8, failure: *p.Failure) c.Error!Prepared {
    failure.* = .{};
    const size = required(facts) catch |err| {
        if (err == error.Capacity) failure.* = .{ .code = .capacity_exceeded, .family = .binding, .required = facts.columns.count, .limit = facts.maximum_count };
        return err;
    };
    if (work.len < size) return c.fail(failure, .workspace_unavailable, .work, size, work.len);
    if (facts.requested == 0 or facts.status == 5) {
        if (facts.columns.count != 0) return error.Invalid;
        return .{}; // not requested is not a successful empty enumeration
    }
    if (facts.source_id == 0 or facts.generation == 0 or facts.observation == 0 or facts.observed_ms < 0 or (facts.status != 1 and facts.columns.count != 0)) return error.Invalid;
    if (!c.disjointColumns(Columns, facts.columns, facts.columns.count, work) or c.overlap(@intFromPtr(work.ptr), work.len, @intFromPtr(facts.text.bytes), facts.text.length)) return error.Invalid;
    const input = facts.columns;
    var payload: u64 = 0;
    var i: u32 = 0;
    while (i < input.count) : (i += 1) {
        const key = try c.get(facts.text, input.keys.?[i]);
        if (!c.exact(key) or input.adapter_ids.?[i] == 0 or input.stable_key_ids.?[i] == 0 or (input.luid_low.?[i] == 0 and input.luid_high.?[i] == 0) or input.capability_masks.?[i] & ~facts.source_capability_mask != 0) return error.Invalid;
        payload += key.len;
        var j: u32 = 0;
        while (j < i) : (j += 1) if (input.adapter_ids.?[i] == input.adapter_ids.?[j] or input.stable_key_ids.?[i] == input.stable_key_ids.?[j] or (input.luid_low.?[i] == input.luid_low.?[j] and input.luid_high.?[i] == input.luid_high.?[j]) or c.eql(key, try c.get(facts.text, input.keys.?[j]))) return error.Identity;
    }
    const table = try Table.init(binding, input.count, payload, failure);
    if (c.overlap(@intFromPtr(work.ptr), work.len, @intFromPtr(table.region.ptr), table.region.len) or c.overlap(@intFromPtr(facts.text.bytes), facts.text.length, @intFromPtr(table.region.ptr), table.region.len)) return error.Invalid;
    const old_count = try table.count();
    i = 0;
    while (i < old_count) : (i += 1) if (!table.validPrior(i) or !c.exact(try table.key(i))) return error.Invalid;
    // Any reused adapter ID, LUID or stable key must retain the entire identity.
    i = 0;
    while (i < input.count) : (i += 1) {
        var j: u32 = 0;
        while (j < old_count) : (j += 1) {
            const same_id = table.load(u64, 0, j) == input.adapter_ids.?[i];
            const same_luid = table.load(u64, 4, j) == input.luid_low.?[i] and table.load(u64, 5, j) == input.luid_high.?[i];
            const same_key = table.load(u64, 6, j) == input.stable_key_ids.?[i];
            if ((same_id or same_luid or same_key) and !(same_id and same_luid and same_key and table.load(u64, 1, j) == facts.source_id and c.eql(try table.key(j), try c.get(facts.text, input.keys.?[i])))) return error.Identity;
            if (same_id and facts.generation < table.load(u64, 2, j)) return error.Identity;
        }
    }
    var fba = std.heap.FixedBufferAllocator.init(work);
    const a = fba.allocator();
    const order = a.alloc(u32, input.count) catch return error.Work;
    var out = Output{ .text = facts.text, .source_id = facts.source_id, .generation = facts.generation, .observed_ms = facts.observed_ms, .status = if (facts.status == 1) 1 else if (facts.status == 4) 4 else 3 };
    out.columns = .{ .count = input.count, .adapter_ids = (a.alloc(u64, input.count) catch return error.Work).ptr, .luid_low = (a.alloc(u64, input.count) catch return error.Work).ptr, .luid_high = (a.alloc(u64, input.count) catch return error.Work).ptr, .stable_key_ids = (a.alloc(u64, input.count) catch return error.Work).ptr, .capability_masks = (a.alloc(u64, input.count) catch return error.Work).ptr, .topology = (a.alloc(u64, input.count) catch return error.Work).ptr, .keys = (a.alloc(c.Ref, input.count) catch return error.Work).ptr };
    out.fingerprints = (a.alloc(u64, input.count) catch return error.Work).ptr;
    i = 0;
    while (i < input.count) : (i += 1) order[i] = i;
    i = 1;
    while (i < input.count) : (i += 1) {
        var j = i;
        while (j > 0 and input.adapter_ids.?[order[j]] < input.adapter_ids.?[order[j - 1]]) : (j -= 1) std.mem.swap(u32, &order[j], &order[j - 1]);
    }
    for (order, 0..) |from, to| {
        inline for (@typeInfo(Columns).@"struct".field_names, @typeInfo(Columns).@"struct".field_types) |field_name, field_type| if (@typeInfo(field_type) == .optional) {
            @constCast(@field(out.columns, field_name).?)[to] = @field(input, field_name).?[from];
        };
        @constCast(out.fingerprints.?)[to] = fingerprint(facts, from);
    }
    const source_entry = table.layout.source(table.region, binding.source);
    var unchanged = old_count == out.columns.count and @atomicLoad(ss.SourceState, &source_entry.state, .acquire) == (if (facts.status == 1) ss.SourceState.has_value else ss.SourceState.unavailable) and @atomicLoad(u32, &source_entry.reason, .acquire) == (if (facts.status == 1) @as(u32, 0) else facts.reason);
    i = 0;
    while (unchanged and i < out.columns.count) : (i += 1) {
        const col = out.columns;
        unchanged = table.load(u64, 0, i) == col.adapter_ids.?[i] and table.load(u64, 1, i) == facts.source_id and table.load(u64, 4, i) == col.luid_low.?[i] and table.load(u64, 5, i) == col.luid_high.?[i] and table.load(u64, 6, i) == col.stable_key_ids.?[i] and table.load(u64, 7, i) == col.capability_masks.?[i] and table.load(u64, 8, i) == col.topology.?[i] and table.load(u64, 11, i) == out.fingerprints.?[i] and c.eql(try table.key(i), try c.get(facts.text, col.keys.?[i]));
    }
    return .{ .table = table, .output = out, .changed = !unchanged, .reason = facts.reason, .prior_count = old_count, .sequence = @atomicLoad(u64, &source_entry.sequence, .acquire) };
}
/// Operation-local metadata and sorted columns only. Never stored in active.
pub const Prepared = struct {
    table: ?Table = null,
    output: Output = .{},
    changed: bool = false,
    reason: u32 = 0,
    prior_count: u32 = 0,
    sequence: u64 = 0,
    writer: ?ss.Writer = null,
    prior_state: ss.SourceState = .unavailable,
    prior_reason: u32 = 0,
    prior_time: i64 = 0,
    pub fn admit(self: *Prepared) c.Error!void {
        const table = self.table orelse return;
        const source = table.layout.source(table.region, table.binding.source);
        if (@atomicLoad(u64, &source.sequence, .acquire) != self.sequence) return error.Identity;
        if (!self.changed) return;
        self.prior_state = @atomicLoad(ss.SourceState, &source.state, .acquire);
        self.prior_reason = @atomicLoad(u32, &source.reason, .acquire);
        self.prior_time = @atomicLoad(i64, &source.write_time_unix_ms, .acquire);
        self.writer = ss.Writer.begin(table.region, table.binding.source) catch return error.Invalid;
    }
    pub fn abort(self: *Prepared) void {
        if (self.writer) |*writer| {
            const table = self.table.?;
            // Admission changed this source's identity sequence, but no row
            // bytes were written. Refresh stamps without copying public data.
            var slot: u32 = 1;
            while (slot < 13) : (slot += 1) writer.setLength(table.binding.positions[slot], self.prior_count, writer.roundSequence()) catch unreachable;
            writer.end(if (self.prior_state == .has_value) .has_value else .{ .unavailable = self.prior_reason }, self.prior_time) catch unreachable;
            self.writer = null;
        }
    }
    pub fn publishAccepted(self: *Prepared) Output {
        if (!self.changed) return self.output;
        const binding = self.table.?.binding;
        const out = &self.output;
        const writer = &self.writer.?;
        writer.setLength(binding.positions[13], 0, 0) catch unreachable;
        var i: u32 = 0;
        while (i < out.columns.count) : (i += 1) {
            const col = out.columns;
            const values = [_]u64{ col.adapter_ids.?[i], out.source_id, out.generation, @bitCast(out.observed_ms), col.luid_low.?[i], col.luid_high.?[i], col.stable_key_ids.?[i], col.capability_masks.?[i], col.topology.?[i], 15 };
            for (values, 0..) |value, slot| writer.store(u64, binding.positions[slot], i, (&value)[0..1]) catch unreachable;
            const status: u32 = 1;
            writer.store(u32, binding.positions[10], i, (&status)[0..1]) catch unreachable;
            writer.store(u64, binding.positions[11], i, out.fingerprints.?[i..][0..1]) catch unreachable;
            writer.appendString(binding.positions[12], i, c.get(out.text, col.keys.?[i]) catch unreachable) catch unreachable;
        }
        i = 0;
        while (i < 13) : (i += 1) writer.setLength(binding.positions[i], out.columns.count, if (i == 0) 0 else writer.roundSequence()) catch unreachable;
        writer.end(if (out.status == 1) .has_value else .{ .unavailable = self.reason }, out.observed_ms) catch unreachable;
        out.published = 1;
        self.writer = null;
        return out.*;
    }
};
