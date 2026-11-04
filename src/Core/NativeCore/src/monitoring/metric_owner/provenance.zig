//! Durable non-value facts in a creator-bound, independently versioned column.
//! Exact words, not hashes. No current scalar/payload replica is stored here.
const st = @import("state.zig");
const s = @import("storage.zig");
const old = @import("../metric_snapshot/protocol.zig");
pub const raw_magic: u64 = 0x3157524f564f5250;
pub const result_magic: u64 = 0x31534d4f564f5250;
pub fn stride(result: bool) u32 {
    return if (result) 4 else 12;
}
fn spec(t: *const s.Table) s.ss.PositionSpec {
    return t.layout.checkedSpec(t.bytes(), t.binding.provenance_position) catch unreachable;
}
pub fn word(t: *const s.Table, index: u32) u64 {
    const at = spec(t);
    const ptr: *const u64 = @ptrCast(@alignCast(t.layout.dataArea(t.bytes()).ptr + at.offset + @as(usize, index) * 8));
    return @atomicLoad(u64, ptr, .acquire);
}
pub fn count(t: *const s.Table) ?u32 {
    const at = spec(t);
    const entry = t.layout.position(t.bytes(), t.binding.provenance_position);
    const len = @atomicLoad(u32, &entry.length, .acquire);
    if (len < 8 or word(t, 0) != (if (t.result) result_magic else raw_magic) or word(t, 7) != 1 or word(t, 6) != t.identitySequence()) return null;
    const n = word(t, 4);
    if (n > (at.size / 8 - 8) / stride(t.result) or len != 8 + n * stride(t.result)) return null;
    if (@atomicLoad(u32, &t.layout.position(t.bytes(), t.binding.positions[0]).length, .acquire) != n) return null;
    const used: usize = if (t.result) 12 else 16;
    var slot: usize = 1;
    while (slot < used) : (slot += 1) {
        if (!t.result and slot == 9) continue;
        const col = t.layout.position(t.bytes(), t.binding.positions[slot]);
        if (@atomicLoad(u32, &col.length, .acquire) != n or @atomicLoad(u64, &col.identity_sequence, .acquire) != t.identitySequence()) return null;
    }
    return @intCast(n);
}
pub fn sourceMatches(t: *const s.Table, src: st.Source) bool {
    return count(t) != null and word(t, 1) == src.id and word(t, 2) == src.incarnation;
}
fn ruleWords(r: st.Rule, m: st.Metric) [12]u64 {
    return .{ r.id, r.identity, m.id, m.identity, r.kind, r.flags, r.priority, r.value_kind, r.minimum, r.maximum, r.capability_mask, 0 };
}
pub fn ruleMatches(t: *const s.Table, row: u32, r: st.Rule, m: st.Metric, src: st.Source) bool {
    const n = count(t) orelse return false;
    if (row >= n or !sourceMatches(t, src)) return false;
    const expected = ruleWords(r, m);
    for (expected, 0..) |value, i| if (i != 6 and word(t, 8 + row * 12 + @as(u32, @intCast(i))) != value) return false;
    return t.load(u64, 0, row) == r.identity and t.load(u64, 15, row) == r.id;
}
pub fn metricMatches(t: *const s.Table, row: u32, m: st.Metric) bool {
    const n = count(t) orelse return false;
    return row < n and word(t, 8 + row * 4) == m.id and word(t, 9 + row * 4) == m.identity and word(t, 11 + row * 4) <= 1 and t.load(u64, 0, row) == m.identity;
}
fn store(t: *const s.Table, w: *const s.ss.Writer, c: *s.Counters, index: u32, value: u64) void {
    w.store(u64, t.binding.provenance_position, index, (&value)[0..1]) catch unreachable;
    c.written +|= 8;
    c.touched +|= 8;
}
pub fn header(t: *const s.Table, w: *const s.ss.Writer, c: *s.Counters, src: ?st.Source, rows: u32) void {
    const words = [_]u64{ if (t.result) result_magic else raw_magic, if (src) |v| v.id else 0, if (src) |v| v.incarnation else 0, if (src) |v| v.capability else 0, rows, if (src) |v| @intFromBool(v.suppressed) else 0, t.identitySequence(), 1 };
    for (words, 0..) |value, i| store(t, w, c, @intCast(i), value);
    w.setLength(t.binding.provenance_position, 8 + rows * stride(t.result), 0) catch unreachable;
    c.written +|= 12;
    c.touched +|= 12;
}
pub fn rule(t: *const s.Table, w: *const s.ss.Writer, c: *s.Counters, r: st.Rule, m: st.Metric) void {
    for (ruleWords(r, m), 0..) |value, i| store(t, w, c, 8 + r.row * 12 + @as(u32, @intCast(i)), value);
}
pub fn metric(t: *const s.Table, w: *const s.ss.Writer, c: *s.Counters, row: u32, m: st.Metric) void {
    for ([_]u64{ m.id, m.identity, m.value_kind, @intFromBool(m.attempted) }, 0..) |value, i| store(t, w, c, 8 + row * 4 + @as(u32, @intCast(i)), value);
}
pub fn attempted(t: *const s.Table, row: u32) bool {
    return word(t, 11 + row * 4) == 1;
}
pub fn markAttempted(t: *const s.Table, w: *const s.ss.Writer, c: *s.Counters, row: u32) void {
    store(t, w, c, 11 + row * 4, 1);
}
pub fn ownerEpoch(t: *const s.Table, w: *const s.ss.Writer, c: *s.Counters, epoch: u64) void {
    store(t, w, c, 1, epoch);
}
pub fn suppressSource(t: *const s.Table, w: *const s.ss.Writer, c: *s.Counters) void {
    store(t, w, c, 5, 1);
}
pub fn validGood(t: *const s.Table, r: st.Rule, d: st.Delta) bool {
    if (d.kind != r.value_kind or d.presence > 2 or d.status < 1 or d.status > 4 or d.observed < 0 or d.attempt < 0 or d.capability_mask & ~r.capability_mask != 0) return false;
    if (d.presence != 0 and d.generation == 0) return false;
    if (d.status == 1 and d.presence == 0) return false;
    if (d.kind != 4 and d.high != 0) return false;
    if (d.kind != 5 and d.payload_ref != 0) return false;
    if (d.presence == 1) {
        if (d.kind == 5) {
            const off: u64 = @as(u32, @truncate(d.payload_ref));
            const len = d.payload_ref >> 32;
            const extent = @atomicLoad(u32, &t.layout.position(t.bytes(), t.binding.positions[9]).length, .acquire);
            return d.low == 0 and len >= r.minimum and len <= r.maximum and off + len <= extent;
        }
        if (d.kind != 4 and !old.validValue(d.kind, r.flags, r.minimum, r.maximum, d.low)) return false;
    }
    return true;
}
