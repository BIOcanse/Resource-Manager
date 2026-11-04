//! Transient SDK request grouping only. No runtime import, owner mutation or values.
const std = @import("std");
const p = @import("protocol.zig");
const st = @import("state.zig");
const c = @import("catalog_compiler.zig");
pub const Ref = c.Ref;
pub const Text = c.Text;
pub const Columns = extern struct {
    count: u32 = 0,
    reserved: u32 = 0,
    field_codes: ?[*]const u32 = null,
    scope_indices: ?[*]const u32 = null,
    display_indices: ?[*]const u32 = null,
    device_keys: ?[*]const Ref = null,
    text: Text = .{},
};
pub const Dispatch = extern struct {
    row_count: u32 = 0,
    sdk_row_count: u32 = 0,
    group_count: u32 = 0,
    source_count: u32 = 0,
    token: u64 = 0,
    catalog_generation: u64 = 0,
    source_ids: ?[*]const u64 = null,
    source_group_offsets: ?[*]const u32 = null,
    group_scope_indices: ?[*]const u32 = null,
    group_first_rows: ?[*]const u32 = null,
    group_row_offsets: ?[*]const u32 = null,
    row_indices: ?[*]const u32 = null,
    raw_row_groups: ?[*]const u32 = null,
    group_field_offsets: ?[*]const u32 = null,
    field_codes: ?[*]const u32 = null,
    key_order: ?[*]const u32 = null,
    display_order: ?[*]const u32 = null,
    result_ordinals: ?[*]i32 = null,
};
const Error = error{ Invalid, Stale, Capacity, Work };
fn fail(f: *p.Failure, code: p.Code, family: p.Family, required: u64, limit: u64) Error {
    f.* = .{ .code = code, .family = family, .required = required, .limit = limit };
    return switch (code) {
        .stale_plan => error.Stale,
        .capacity_exceeded => error.Capacity,
        .workspace_unavailable => error.Work,
        else => error.Invalid,
    };
}
fn add(comptime T: type, count: u64, cursor: *u64) Error!void {
    const aligned = (std.math.add(u64, cursor.*, @alignOf(T) - 1) catch return error.Capacity) & ~@as(u64, @alignOf(T) - 1);
    cursor.* = std.math.add(u64, aligned, std.math.mul(u64, count, @sizeOf(T)) catch return error.Capacity) catch return error.Capacity;
}
pub fn requiredSize(rows: u32, sources: u32) Error!u64 {
    var n: u64 = 0;
    try add(u64, sources, &n);
    try add(u32, @as(u64, sources) + 1, &n);
    // Output columns, each with actual-row maximum capacity, never per-value bytes.
    inline for (0..10) |i| try add(u32, @as(u64, rows) + @as(u64, if (i == 2 or i == 5) 1 else 0), &n);
    try add(u32, rows, &n); // row -> actual source-event ordinal (sort context)
    try add(bool, rows, &n); // excluded row marks
    try add(u32, sources, &n); // source-ID sorted lookup, preserving event order
    return (std.math.add(u64, n, 7) catch return error.Capacity) & ~@as(u64, 7);
}
fn range(address: usize, length: usize) bool {
    return length <= std.math.maxInt(usize) - address;
}
fn overlap(address: usize, length: usize, bytes: []const u8) bool {
    return st.overlaps(address, length, @intFromPtr(bytes.ptr), bytes.len);
}
fn pointer(comptime T: type, ptr: ?[*]const T, count: usize) Error!void {
    if (count == 0) return;
    const v = ptr orelse return error.Invalid;
    const address = @intFromPtr(v);
    const length = std.math.mul(usize, count, @sizeOf(T)) catch return error.Invalid;
    if (address % @alignOf(T) != 0 or !range(address, length)) return error.Invalid;
}
fn columnsOutside(comptime T: type, value: T, count: usize, bytes: []const u8) Error!void {
    inline for (@typeInfo(T).@"struct".field_names, @typeInfo(T).@"struct".field_types) |field_name, field_type| {
        if (@typeInfo(field_type) == .optional) {
            const Ptr = @typeInfo(field_type).optional.child;
            if (@typeInfo(Ptr) == .pointer) if (@field(value, field_name)) |ptr| {
                const Child = @typeInfo(Ptr).pointer.child;
                const len = std.math.mul(usize, count, @sizeOf(Child)) catch return error.Invalid;
                if (@intFromPtr(ptr) % @alignOf(Child) != 0 or !range(@intFromPtr(ptr), len) or overlap(@intFromPtr(ptr), len, bytes)) return error.Invalid;
            };
        }
    }
}
fn publicOutside(o: *const st.Owner, bytes: []const u8) Error!void {
    if (overlap(@intFromPtr(o), @intCast(@max(o.active_length, @sizeOf(st.Owner))), bytes)) return error.Invalid;
    for (o.bindings) |b| if (b.table.region) |v| {
        if (b.table.byte_length > std.math.maxInt(usize) or overlap(@intFromPtr(v), @intCast(b.table.byte_length), bytes)) return error.Invalid;
    };
    for (o.results[0..o.result_count]) |b| if (b.region) |v| {
        if (b.byte_length > std.math.maxInt(usize) or overlap(@intFromPtr(v), @intCast(b.byte_length), bytes)) return error.Invalid;
    };
    if (o.gpu_vector) |b| if (b.region) |v| {
        if (b.byte_length > std.math.maxInt(usize) or overlap(@intFromPtr(v), @intCast(b.byte_length), bytes)) return error.Invalid;
    };
    if (o.work.region) |v| {
        if (overlap(@intFromPtr(v), o.work.length, bytes)) return error.Invalid;
    }
    if (o.work.npu_plan) |plan| if (plan.base) |v| {
        if (plan.length > std.math.maxInt(usize) or overlap(@intFromPtr(v), @intCast(plan.length), bytes)) return error.Invalid;
    };
}
fn inputsOutside(raw: p.Round, cols: Columns, ids: []const u64, excluded: []const u32, bytes: []const u8) Error!void {
    try columnsOutside(p.Observations, raw.observations, raw.observations.capacity, bytes);
    try columnsOutside(p.Queries, raw.queries, raw.queries.capacity, bytes);
    try columnsOutside(Columns, cols, cols.count, bytes);
    if (cols.text.length != 0 and overlap(@intFromPtr(cols.text.bytes orelse return error.Invalid), cols.text.length, bytes)) return error.Invalid;
    if (raw.payload_capacity != 0 and (raw.payload_capacity > std.math.maxInt(usize) or overlap(@intFromPtr(raw.payload orelse return error.Invalid), @intCast(raw.payload_capacity), bytes))) return error.Invalid;
    if (ids.len != 0 and overlap(@intFromPtr(ids.ptr), ids.len * 8, bytes)) return error.Invalid;
    if (excluded.len != 0 and overlap(@intFromPtr(excluded.ptr), excluded.len * 4, bytes)) return error.Invalid;
}
fn key(cols: Columns, row: u32) []const u8 {
    const r = cols.device_keys.?[row];
    return cols.text.bytes.?[r.offset..][0..r.length];
}
const RowSort = struct {
    cols: Columns,
    source: []const u32,
    fn less(ctx: @This(), a: u32, b: u32) bool {
        if (ctx.source[a] != ctx.source[b]) return ctx.source[a] < ctx.source[b];
        const sa = ctx.cols.scope_indices.?[a];
        const sb = ctx.cols.scope_indices.?[b];
        if (sa != sb) return sa < sb;
        const order = std.mem.order(u8, key(ctx.cols, a), key(ctx.cols, b));
        if (order != .eq) return order == .lt;
        const fa = ctx.cols.field_codes.?[a];
        const fb = ctx.cols.field_codes.?[b];
        return if (fa != fb) fa < fb else a < b;
    }
};
const GroupSort = struct {
    cols: Columns,
    first: []const u32,
    display: bool,
    fn less(ctx: @This(), a: u32, b: u32) bool {
        const ar = ctx.first[a];
        const br = ctx.first[b];
        if (ctx.display) {
            const av: i32 = @bitCast(ctx.cols.display_indices.?[ar]);
            const bv: i32 = @bitCast(ctx.cols.display_indices.?[br]);
            if (av != bv) return av < bv;
        } else {
            const order = std.mem.order(u8, key(ctx.cols, ar), key(ctx.cols, br));
            if (order != .eq) return order == .lt;
        }
        return a < b;
    }
};
const SourceSort = struct {
    ids: []const u64,
    fn less(ctx: @This(), a: u32, b: u32) bool {
        return ctx.ids[a] < ctx.ids[b];
    }
};
fn sourceOrdinal(ids: []const u64, order: []const u32, id: u64) ?u32 {
    var low: usize = 0;
    var high = order.len;
    while (low < high) {
        const mid = low + (high - low) / 2;
        if (ids[order[mid]] < id) low = mid + 1 else high = mid;
    }
    return if (low < order.len and ids[order[low]] == id) order[low] else null;
}
pub fn build(o: *const st.Owner, raw: p.Round, cols: Columns, ids: []const u64, excluded: []const u32, bytes: []align(8) u8, f: *p.Failure) Error!Dispatch {
    if (o.magic != st.Owner.live_magic or raw.reserved != 0 or raw.observations.reserved != 0 or raw.queries.reserved != 0 or cols.reserved != 0 or cols.text.reserved != 0 or cols.count != raw.count or raw.count > raw.observations.capacity or raw.count > raw.queries.capacity or excluded.len > raw.count or o.result_count > 2) return error.Invalid;
    if (raw.token == 0 or raw.token != o.work.raw.token or raw.catalog_generation != o.work.raw.catalog_generation or raw.catalog_generation != o.catalog_generation or raw.count != o.work.raw.count) return error.Stale;
    if (raw.count > o.config.rule_capacity or raw.count > o.rule_count) return fail(f, .capacity_exceeded, .rules, raw.count, @min(o.config.rule_capacity, o.rule_count));
    if (ids.len > o.config.source_capacity or ids.len > o.source_count) return fail(f, .capacity_exceeded, .sources, ids.len, @min(o.config.source_capacity, o.source_count));
    if (bytes.len > o.config.work_limit) return fail(f, .capacity_exceeded, .work, bytes.len, o.config.work_limit);
    const combined = std.math.add(u64, o.work.length, bytes.len) catch return fail(f, .capacity_exceeded, .work, std.math.maxInt(u64), o.config.work_limit);
    if (combined > o.config.work_limit) return fail(f, .capacity_exceeded, .work, combined, o.config.work_limit);
    const required = try requiredSize(raw.count, @intCast(ids.len));
    if (required > o.config.work_limit) return fail(f, .capacity_exceeded, .work, required, o.config.work_limit);
    if (bytes.len < required) return fail(f, .workspace_unavailable, .work, required, bytes.len);
    try publicOutside(o, bytes);
    try inputsOutside(raw, cols, ids, excluded, bytes);
    try pointer(u64, raw.queries.source_ids, raw.count);
    try pointer(u32, raw.observations.rule_indices, raw.count);
    try pointer(u32, cols.field_codes, raw.count);
    try pointer(u32, cols.scope_indices, raw.count);
    try pointer(u32, cols.display_indices, raw.count);
    try pointer(Ref, cols.device_keys, raw.count);
    try pointer(u8, cols.text.bytes, cols.text.length);
    var a = std.heap.FixedBufferAllocator.init(bytes);
    const alloc = a.allocator();
    const source_ids = alloc.alloc(u64, ids.len) catch return error.Work;
    const source_offsets = alloc.alloc(u32, ids.len + 1) catch return error.Work;
    const scopes = alloc.alloc(u32, raw.count) catch return error.Work;
    const first = alloc.alloc(u32, raw.count) catch return error.Work;
    const row_offsets = alloc.alloc(u32, @as(usize, raw.count) + 1) catch return error.Work;
    const rows = alloc.alloc(u32, raw.count) catch return error.Work;
    const reverse = alloc.alloc(u32, raw.count) catch return error.Work;
    const field_offsets = alloc.alloc(u32, @as(usize, raw.count) + 1) catch return error.Work;
    const fields = alloc.alloc(u32, raw.count) catch return error.Work;
    const keys = alloc.alloc(u32, raw.count) catch return error.Work;
    const displays = alloc.alloc(u32, raw.count) catch return error.Work;
    const results = alloc.alloc(i32, raw.count) catch return error.Work;
    const source_for_row = alloc.alloc(u32, raw.count) catch return error.Work;
    const excluded_marks = alloc.alloc(bool, raw.count) catch return error.Work;
    const source_order = alloc.alloc(u32, ids.len) catch return error.Work;
    @memcpy(source_ids, ids);
    @memset(reverse, p.none);
    @memset(excluded_marks, false);
    @memset(results, -1);
    for (ids, 0..) |id, i| {
        if (id == 0) return error.Invalid;
        source_order[i] = @intCast(i);
    }
    std.mem.sortUnstable(u32, source_order, SourceSort{ .ids = ids }, SourceSort.less);
    var i: usize = 1;
    while (i < ids.len) : (i += 1) if (ids[source_order[i - 1]] == ids[source_order[i]]) return error.Invalid;
    for (excluded) |r| {
        if (r >= raw.count or excluded_marks[r]) return error.Invalid;
        excluded_marks[r] = true;
    }
    var used: u32 = 0;
    i = 0;
    while (i < raw.count) : (i += 1) {
        if (raw.observations.rule_indices.?[i] >= o.rule_count) return error.Invalid;
        const r = cols.device_keys.?[i];
        if (@as(u64, r.offset) + r.length > cols.text.length or r.length == 0 or !std.unicode.utf8ValidateSlice(key(cols, @intCast(i))) or cols.field_codes.?[i] == 0 or cols.field_codes.?[i] > std.math.maxInt(i32)) return error.Invalid;
        if ((cols.scope_indices.?[i] == p.none) != (cols.display_indices.?[i] == p.none) or (cols.display_indices.?[i] != p.none and cols.display_indices.?[i] > std.math.maxInt(i32))) return error.Invalid;
        // A synthetic dependency has no actual SDK acquisition/source event.
        // Its metadata is checked above, but it must not require a fabricated
        // source ordinal merely to be excluded from dispatch.
        if (excluded_marks[i]) {
            source_for_row[i] = p.none;
            continue;
        }
        source_for_row[i] = sourceOrdinal(ids, source_order, raw.queries.source_ids.?[i]) orelse return error.Invalid;
        rows[used] = @intCast(i);
        used += 1;
    }
    std.mem.sortUnstable(u32, rows[0..used], RowSort{ .cols = cols, .source = source_for_row }, RowSort.less);
    var groups: u32 = 0;
    var field_count: u32 = 0;
    var source: usize = 0;
    var cursor: u32 = 0;
    while (source < ids.len) : (source += 1) {
        source_offsets[source] = groups;
        while (cursor < used and source_for_row[rows[cursor]] == source) {
            const start = cursor;
            const representative = rows[cursor];
            first[groups] = representative;
            scopes[groups] = cols.scope_indices.?[representative];
            row_offsets[groups] = cursor;
            field_offsets[groups] = field_count;
            var last_field: u32 = 0;
            while (cursor < used) {
                const row = rows[cursor];
                if (source_for_row[row] != source or cols.scope_indices.?[row] != scopes[groups] or !std.mem.eql(u8, key(cols, row), key(cols, representative))) break;
                if (cols.display_indices.?[row] != cols.display_indices.?[representative]) return error.Invalid;
                reverse[row] = groups;
                const field = cols.field_codes.?[row];
                if (cursor == start or last_field != field) {
                    fields[field_count] = field;
                    field_count += 1;
                    last_field = field;
                }
                cursor += 1;
            }
            keys[groups] = groups;
            displays[groups] = groups;
            groups += 1;
        }
        const begin = source_offsets[source];
        std.mem.sortUnstable(u32, keys[begin..groups], GroupSort{ .cols = cols, .first = first, .display = false }, GroupSort.less);
        std.mem.sortUnstable(u32, displays[begin..groups], GroupSort{ .cols = cols, .first = first, .display = true }, GroupSort.less);
    }
    source_offsets[ids.len] = groups;
    row_offsets[groups] = used;
    field_offsets[groups] = field_count;
    return .{ .row_count = raw.count, .sdk_row_count = used, .group_count = groups, .source_count = @intCast(ids.len), .token = raw.token, .catalog_generation = raw.catalog_generation, .source_ids = source_ids.ptr, .source_group_offsets = source_offsets.ptr, .group_scope_indices = scopes.ptr, .group_first_rows = first.ptr, .group_row_offsets = row_offsets.ptr, .row_indices = rows.ptr, .raw_row_groups = reverse.ptr, .group_field_offsets = field_offsets.ptr, .field_codes = fields.ptr, .key_order = keys.ptr, .display_order = displays.ptr, .result_ordinals = results.ptr };
}
fn result(err: ?Error, failure: p.Failure, f: ?*p.Failure) u32 {
    var value = failure;
    if (err) |e| {
        if (value.code == .ok) value.code = switch (e) {
            error.Stale => .stale_plan,
            error.Capacity => .capacity_exceeded,
            error.Work => .workspace_unavailable,
            else => .invalid_contract,
        };
    }
    if (f) |target| target.* = value;
    return @backingInt(value.code);
}
pub export fn rm_metric_sdk_dispatch_required_size(rows: u32, sources: u32, out: ?*u64, f: ?*p.Failure) u32 {
    const target = out orelse return @backingInt(p.Code.invalid_contract);
    if (@intFromPtr(target) % 8 != 0 or (if (f) |v| @intFromPtr(v) % 8 != 0 or st.overlaps(@intFromPtr(v), @sizeOf(p.Failure), @intFromPtr(target), 8) else false)) return @backingInt(p.Code.invalid_contract);
    target.* = requiredSize(rows, sources) catch |err| return result(err, .{}, f);
    return result(null, .{}, f);
}
pub export fn rm_metric_sdk_dispatch_build(owner: ?*const st.Owner, raw_ptr: ?*const p.Round, cols_ptr: ?*const Columns, source_ids: ?[*]const u64, source_count: u32, excluded_ptr: ?[*]const u32, excluded_count: u32, work: ?[*]u8, length: u64, out: ?*Dispatch, f: ?*p.Failure) u32 {
    const o = owner orelse return @backingInt(p.Code.invalid_contract);
    const rp = raw_ptr orelse return @backingInt(p.Code.invalid_contract);
    const cp = cols_ptr orelse return @backingInt(p.Code.invalid_contract);
    const target = out orelse return @backingInt(p.Code.invalid_contract);
    if (@intFromPtr(o) % @alignOf(st.Owner) != 0 or @intFromPtr(rp) % 8 != 0 or @intFromPtr(cp) % 8 != 0 or @intFromPtr(target) % 8 != 0 or (if (f) |v| @intFromPtr(v) % 8 != 0 else false)) return @backingInt(p.Code.invalid_contract);
    if (o.magic != st.Owner.live_magic or o.result_count > 2) return @backingInt(p.Code.invalid_contract);
    pointer(u64, source_ids, source_count) catch return @backingInt(p.Code.invalid_contract);
    pointer(u32, excluded_ptr, excluded_count) catch return @backingInt(p.Code.invalid_contract);
    const ids: []const u64 = if (source_count == 0) &.{} else source_ids.?[0..source_count];
    const excluded: []const u32 = if (excluded_count == 0) &.{} else excluded_ptr.?[0..excluded_count];
    // Validate output/failure headers BEFORE clearing/writing either. Rejected
    // aliases must not mutate raw work, inputs, active or public mappings.
    const output_bytes = std.mem.asBytes(target);
    publicOutside(o, output_bytes) catch return @backingInt(p.Code.invalid_contract);
    inputsOutside(rp.*, cp.*, ids, excluded, output_bytes) catch return @backingInt(p.Code.invalid_contract);
    if (st.overlaps(@intFromPtr(rp), @sizeOf(p.Round), @intFromPtr(target), @sizeOf(Dispatch)) or st.overlaps(@intFromPtr(cp), @sizeOf(Columns), @intFromPtr(target), @sizeOf(Dispatch))) return @backingInt(p.Code.invalid_contract);
    if (f) |v| {
        const fb = std.mem.asBytes(v);
        publicOutside(o, fb) catch return @backingInt(p.Code.invalid_contract);
        inputsOutside(rp.*, cp.*, ids, excluded, fb) catch return @backingInt(p.Code.invalid_contract);
        if (overlap(@intFromPtr(rp), @sizeOf(p.Round), fb) or overlap(@intFromPtr(cp), @sizeOf(Columns), fb) or overlap(@intFromPtr(target), @sizeOf(Dispatch), fb)) return @backingInt(p.Code.invalid_contract);
    }
    var failure: p.Failure = .{};
    const ptr = work orelse return result(error.Invalid, failure, f);
    if (@intFromPtr(ptr) % 8 != 0 or length > std.math.maxInt(usize) or !range(@intFromPtr(ptr), @intCast(length))) return result(error.Invalid, failure, f);
    const bytes: []align(8) u8 = @alignCast(ptr[0..@intCast(length)]);
    if (overlap(@intFromPtr(rp), @sizeOf(p.Round), bytes) or overlap(@intFromPtr(cp), @sizeOf(Columns), bytes) or overlap(@intFromPtr(target), @sizeOf(Dispatch), bytes) or (if (f) |v| overlap(@intFromPtr(v), @sizeOf(p.Failure), bytes) else false)) return @backingInt(p.Code.invalid_contract);
    const dispatch = build(o, rp.*, cp.*, ids, excluded, bytes, &failure) catch |err| return result(err, failure, f);
    target.* = dispatch;
    return result(null, .{}, f);
}
pub fn exportAnchor() void {
    _ = &rm_metric_sdk_dispatch_required_size;
    _ = &rm_metric_sdk_dispatch_build;
}
comptime {
    std.debug.assert(@sizeOf(Columns) == 56 and @sizeOf(Dispatch) == 128 and @offsetOf(Dispatch, "source_ids") == 32 and @offsetOf(Dispatch, "result_ordinals") == 120);
}
