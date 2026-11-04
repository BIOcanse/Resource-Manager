const std = @import("std");
pub const ss = @import("rm_shared_section");
const p = @import("protocol.zig");
pub const Error = error{ Invalid, Shared, Capacity, Payload, Stale, Closed };

pub const Table = struct {
    binding: p.TableBinding,
    layout: ss.layout.Layout,
    specs: [16]ss.PositionSpec = undefined,
    result: bool,
    pub fn bytes(t: *const Table) []align(8) u8 {
        return @alignCast(t.binding.region.?[0..@intCast(t.binding.byte_length)]);
    }
    pub fn init(b: p.TableBinding, rows: u32, result: bool) Error!Table {
        const base = b.region orelse return error.Invalid;
        if (@intFromPtr(base) % 8 != 0) return error.Invalid;
        if (b.reserved != 0 or b.byte_length > std.math.maxInt(usize)) return error.Invalid;
        const region: []align(8) u8 = @alignCast(base[0..@intCast(b.byte_length)]);
        const layout = ss.layout.Layout.load(region) catch return error.Shared;
        if (b.rows_source == b.value_source or b.rows_source == b.health_source or b.value_source == b.health_source) return error.Invalid;
        var t = Table{ .binding = b, .layout = layout, .result = result };
        for ([_]u32{ b.rows_source, b.value_source, b.health_source, b.provenance_source }, 0..) |src, si| {
            const sources = [_]u32{ b.rows_source, b.value_source, b.health_source, b.provenance_source };
            for (sources[0..si]) |prior| if (src == prior) return error.Invalid;
            if (src >= layout.header.source_count) return error.Invalid;
            const seq = @atomicLoad(u64, &layout.source(region, src).sequence, .acquire);
            if (seq & (ss.ended_bit | 1) != 0 or seq > ss.ended_bit - 4) return error.Shared;
        }
        const used: usize = if (result) 12 else 16;
        var i: usize = 0;
        while (i < used) : (i += 1) {
            if (b.positions[i] >= layout.header.position_count) return error.Invalid;
            const spec = layout.checkedSpec(region, b.positions[i]) catch return error.Shared;
            const width: u32 = if (result) (if (i == 4 or i == 5 or i == 9 or i == 10 or i == 11) 4 else 8) else (if (i == 9) 1 else if (i == 3 or i == 4 or i == 10 or i == 11 or i == 13) 4 else 8);
            const source = if (i == 0 or (!result and i == 15)) b.rows_source else if (!result and i >= 10 and i <= 12) b.health_source else b.value_source;
            const identity = if (i == 0 or (!result and i == 9)) p.none else b.positions[0];
            if (spec.source != source or spec.element_width != width or spec.identity != identity) return error.Invalid;
            if (spec.kind != (if (!result and i == 9) ss.PositionKind.string_table else ss.PositionKind.column)) return error.Invalid;
            if ((result or i != 9) and spec.size / width < rows) return error.Capacity;
            for (t.specs[0..i]) |prior| {
                if (spec.size != 0 and prior.size != 0 and @as(u64, spec.offset) < @as(u64, prior.offset) + prior.size and @as(u64, prior.offset) < @as(u64, spec.offset) + spec.size) return error.Invalid;
            }
            t.specs[i] = spec;
        }
        while (i < 16) : (i += 1) {
            if (b.positions[i] != p.none) return error.Invalid;
        }
        if (b.provenance_position >= layout.header.position_count) return error.Invalid;
        const prov = layout.checkedSpec(region, b.provenance_position) catch return error.Shared;
        if (prov.source != b.provenance_source or prov.kind != .column or prov.element_width != 8 or prov.identity != p.none or prov.size / 8 < 8 + @as(u64, rows) * (if (result) @as(u64, 4) else 12)) return error.Invalid;
        for (t.specs[0..used]) |prior| if (prov.size != 0 and prior.size != 0 and @as(u64, prov.offset) < @as(u64, prior.offset) + prior.size and @as(u64, prior.offset) < @as(u64, prov.offset) + prov.size) return error.Invalid;
        return t;
    }
    pub fn identitySequence(t: *const Table) u64 {
        return @atomicLoad(u64, &t.layout.source(t.bytes(), t.binding.rows_source).sequence, .acquire);
    }
    pub fn rowCapacity(t: *const Table) u32 {
        var limit: u32 = std.math.maxInt(u32);
        const used: usize = if (t.result) 12 else 16;
        for (t.specs[0..used], 0..) |spec, slot| {
            if (!t.result and slot == 9) continue;
            limit = @min(limit, spec.size / spec.element_width);
        }
        const proof = t.layout.checkedSpec(t.bytes(), t.binding.provenance_position) catch unreachable;
        return @min(limit, (proof.size / 8 - 8) / (if (t.result) @as(u32, 4) else 12));
    }
    pub fn load(t: *const Table, comptime T: type, slot: usize, row: u32) T {
        const ptr: *const T = @ptrCast(@alignCast(t.layout.dataArea(t.bytes()).ptr + t.specs[slot].offset + @as(usize, row) * @sizeOf(T)));
        return @atomicLoad(T, ptr, .acquire);
    }
    pub fn writer(t: *const Table, source: u32) Error!ss.Writer {
        return ss.Writer.begin(t.bytes(), source) catch return error.Shared;
    }
};

pub const Counters = struct { written: u64 = 0, touched: u64 = 0, read: u64 = 0, payload: u64 = 0 };
pub fn store(t: *const Table, w: *const ss.Writer, c: *Counters, comptime T: type, slot: usize, row: u32, value: T) void {
    w.store(T, t.binding.positions[slot], row, (&value)[0..1]) catch unreachable;
    c.written +|= @sizeOf(T);
    c.touched +|= @sizeOf(T);
}
pub fn length(t: *const Table, w: *const ss.Writer, c: *Counters, slot: usize, rows: u32) void {
    w.setLength(t.binding.positions[slot], rows, if (slot == 0) 0 else if (w.source == t.binding.rows_source) w.roundSequence() else t.identitySequence()) catch unreachable;
    c.written +|= 12;
    c.touched +|= 12;
}
pub fn finish(w: *ss.Writer, c: *Counters, time: i64) Error!void {
    w.end(.has_value, time) catch return error.Shared;
    c.written +|= 32;
    c.touched +|= 32;
}

/// Pre-open every participating source before the first store. No allocation,
/// parsing, capacity checks, or vendor calls remain after this point.
pub const Open = struct {
    table: Table,
    rows: ?ss.Writer = null,
    values: ?ss.Writer = null,
    health: ?ss.Writer = null,
    provenance: ?ss.Writer = null,
    previous: [4]Previous = @splat(Previous{}),
    const Previous = struct { state: ss.SourceState = .unavailable, reason: u32 = 0, time: i64 = 0 };
    fn previousOf(t: *const Table, source: u32) Previous {
        const entry = t.layout.source(t.bytes(), source);
        return .{ .state = @atomicLoad(ss.SourceState, &entry.state, .acquire), .reason = @atomicLoad(u32, &entry.reason, .acquire), .time = @atomicLoad(i64, &entry.write_time_unix_ms, .acquire) };
    }
    pub fn begin(t: Table, rows: bool, values: bool, health: bool) Error!Open {
        var o = Open{ .table = t };
        o.previous = .{ previousOf(&t, t.binding.rows_source), previousOf(&t, t.binding.value_source), previousOf(&t, t.binding.health_source), previousOf(&t, t.binding.provenance_source) };
        errdefer o.closeUnchanged();
        if (rows) o.rows = try t.writer(t.binding.rows_source);
        if (values) o.values = try t.writer(t.binding.value_source);
        if (health) o.health = try t.writer(t.binding.health_source);
        return o;
    }
    pub fn closeUnchanged(o: *Open) void {
        if (o.provenance) |*w| {
            restore(w, o.previous[3]);
            o.provenance = null;
        }
        if (o.rows) |*w| {
            restore(w, o.previous[0]);
            o.rows = null;
        }
        if (o.values) |*w| {
            restore(w, o.previous[1]);
            o.values = null;
        }
        if (o.health) |*w| {
            restore(w, o.previous[2]);
            o.health = null;
        }
    }
    fn restore(w: *ss.Writer, prior: Previous) void {
        const outcome: ss.seqlock.Outcome = if (prior.state == .has_value) .has_value else .{ .unavailable = prior.reason };
        w.end(outcome, prior.time) catch {};
    }
    pub fn close(o: *Open, c: *Counters, time: i64) Error!void {
        if (o.rows) |*w| {
            try finish(w, c, time);
            o.rows = null;
        }
        if (o.values) |*w| {
            try finish(w, c, time);
            o.values = null;
        }
        if (o.health) |*w| {
            try finish(w, c, time);
            o.health = null;
        }
        if (o.provenance) |*w| {
            try finish(w, c, time);
            o.provenance = null;
        }
    }
    pub fn beginCatalog(t: Table, rows: bool, values: bool, health: bool) Error!Open {
        var o = try begin(t, rows, values, health);
        errdefer o.closeUnchanged();
        o.provenance = try t.writer(t.binding.provenance_source);
        return o;
    }
};
