//! Indexes contain only rule indices. Payload bounds are always read from the
//! unique public columns; no private byte/ref/current-value replica exists.
const st = @import("state.zig");
const s = @import("storage.zig");
const p = @import("protocol.zig");
fn key(o: *st.Owner, t: *const s.Table, ri: u32) u32 {
    const r = o.rules[ri];
    const ref = t.load(u64, 8, r.row);
    o.counters.read +|= 12;
    if (t.load(u32, 4, r.row) != 1 or ref >> 32 == 0) return 0;
    return @as(u32, @truncate(ref)) + @as(u32, @intCast(ref >> 32));
}
fn swap(o: *st.Owner, source: u32, a: u32, b: u32) void {
    const start = o.sources[source].heap_start;
    const tmp = o.payload_heap[start + a];
    o.payload_heap[start + a] = o.payload_heap[start + b];
    o.payload_heap[start + b] = tmp;
    o.rules[o.payload_heap[start + a]].heap_position = a;
    o.rules[tmp].heap_position = b;
}
pub fn update(o: *st.Owner, t: *const s.Table, ri: u32) void {
    const source = o.rules[ri].source;
    const src = &o.sources[source];
    const start = src.heap_start;
    var pos = o.rules[ri].heap_position;
    const extent_end = key(o, t, ri);
    if (extent_end == 0) {
        if (pos == p.none) return;
        src.heap_count -= 1;
        o.rules[ri].heap_position = p.none;
        if (pos == src.heap_count) return;
        o.payload_heap[start + pos] = o.payload_heap[start + src.heap_count];
        o.rules[o.payload_heap[start + pos]].heap_position = pos;
    } else if (pos == p.none) {
        pos = src.heap_count;
        src.heap_count += 1;
        o.payload_heap[start + pos] = ri;
        o.rules[ri].heap_position = pos;
    }
    while (pos > 0) {
        const parent = (pos - 1) / 2;
        if (key(o, t, o.payload_heap[start + parent]) >= key(o, t, o.payload_heap[start + pos])) break;
        swap(o, source, parent, pos);
        pos = parent;
    }
    while (@as(u64, pos) * 2 + 1 < src.heap_count) {
        var child = pos * 2 + 1;
        if (child + 1 < src.heap_count and key(o, t, o.payload_heap[start + child + 1]) > key(o, t, o.payload_heap[start + child])) child += 1;
        if (key(o, t, o.payload_heap[start + pos]) >= key(o, t, o.payload_heap[start + child])) break;
        swap(o, source, pos, child);
        pos = child;
    }
}
pub fn end(o: *st.Owner, t: *const s.Table, source: u32) u32 {
    const src = o.sources[source];
    return if (src.heap_count == 0) 0 else key(o, t, o.payload_heap[src.heap_start]);
}
pub fn rebuild(o: *st.Owner) void {
    var start: u32 = 0;
    for (o.sources[0..o.source_count], 0..) |*src, si| {
        src.heap_start = start;
        start += src.rows;
        src.heap_count = 0;
        const t = st.table(o, @intCast(si)) catch unreachable;
        var ri = src.first_rule;
        var visited: u32 = 0;
        while (ri != p.none and visited < src.rows) : (visited += 1) {
            o.rules[ri].heap_position = p.none;
            if (o.rules[ri].value_kind == 5) update(o, &t, ri);
            ri = o.rules[ri].next_source;
        }
    }
}
