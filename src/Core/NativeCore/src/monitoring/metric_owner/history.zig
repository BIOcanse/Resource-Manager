//! Requested history. All resident bytes are in creator-supplied positions.
const std = @import("std");
const p = @import("protocol.zig");
const st = @import("state.zig");
const storage = @import("storage.zig");
const ss = @import("rm_shared_section");
const round = @import("round.zig");
const vector = @import("vector_inputs.zig");
pub const Error = error{ Invalid, Capacity, Workspace, Shared, Stale };
pub const Requirement = extern struct { metric_id: u64, rounds: u32, reserved: u32 = 0 };
pub const Binding = extern struct {
    region: ?[*]u8,
    byte_length: u64,
    rows_source: u32,
    data_source: u32,
    positions: [9]u32,
    reserved: u32 = 0,
};
pub const Sample = extern struct { time: i64 = 0, bits: u64 = 0, present: u32 = 0, reserved: u32 = 0 };
// Slots: identity u64, metric ID u64, depth/start/count/head u32,
// flattened sample time i64, f64 bits u64, nullable presence u32.
pub const Table = struct {
    binding: Binding,
    layout: ss.layout.Layout,
    specs: [9]ss.PositionSpec,
    rows: u32,
    capacity: u32,
    samples: u32,
    fn bytes(t: *const Table) []align(8) u8 {
        return @alignCast(t.binding.region.?[0..@intCast(t.binding.byte_length)]);
    }
    pub fn load(t: *const Table, comptime T: type, slot: usize, row: u32) T {
        const ptr: *const T = @ptrCast(@alignCast(t.layout.dataArea(t.bytes()).ptr + t.specs[slot].offset + @as(usize, row) * @sizeOf(T)));
        return @atomicLoad(T, ptr, .acquire);
    }
    fn seq(t: *const Table) u64 {
        return @atomicLoad(u64, &t.layout.source(t.bytes(), t.binding.rows_source).sequence, .acquire);
    }
    pub fn init(b: Binding) Error!Table {
        return initMode(b, false);
    }
    fn initMode(b: Binding, mixed: bool) Error!Table {
        const base = b.region orelse return error.Invalid;
        if (b.reserved != 0 or b.byte_length > std.math.maxInt(usize) or @intFromPtr(base) % 8 != 0 or b.rows_source == b.data_source) return error.Invalid;
        const region_bytes: []align(8) u8 = @alignCast(base[0..@intCast(b.byte_length)]);
        const layout = ss.layout.Layout.load(region_bytes) catch return error.Shared;
        for ([_]u32{ b.rows_source, b.data_source }) |src| {
            if (src >= layout.header.source_count) return error.Invalid;
            const sequence = @atomicLoad(u64, &layout.source(region_bytes, src).sequence, .acquire);
            if (sequence & (ss.ended_bit | 1) != 0 or sequence > ss.ended_bit - 4) return error.Shared;
        }
        var t = Table{ .binding = b, .layout = layout, .specs = undefined, .rows = 0, .capacity = std.math.maxInt(u32), .samples = std.math.maxInt(u32) };
        for (b.positions, 0..) |pos, slot| {
            if (pos >= layout.header.position_count) return error.Invalid;
            for (b.positions[0..slot]) |prior| if (prior == pos) return error.Invalid;
            const spec = layout.checkedSpec(region_bytes, pos) catch return error.Shared;
            for (t.specs[0..slot]) |prior| if (spec.size != 0 and prior.size != 0 and @as(u64, spec.offset) < @as(u64, prior.offset) + prior.size and @as(u64, prior.offset) < @as(u64, spec.offset) + spec.size) return error.Invalid;
            const width: u32 = if (slot == 0 or slot == 1 or slot == 6 or slot == 7) 8 else 4;
            if (spec.kind != .column or spec.element_width != width or spec.source != (if (slot == 0) b.rows_source else b.data_source) or spec.identity != (if (slot >= 1 and slot <= 5) b.positions[0] else ss.none)) return error.Invalid;
            t.specs[slot] = spec;
            if (slot < 6) t.capacity = @min(t.capacity, spec.size / width) else t.samples = @min(t.samples, spec.size / width);
        }
        t.rows = @atomicLoad(u32, &layout.position(region_bytes, b.positions[0]).length, .acquire);
        if (t.rows > t.capacity) return error.Invalid;
        var end: u64 = 0;
        var i: u32 = 0;
        while (i < t.rows) : (i += 1) {
            const depth = t.load(u32, 2, i);
            const start = t.load(u32, 3, i);
            const count = t.load(u32, 4, i);
            const head = t.load(u32, 5, i);
            if (depth == 0 or start != end or count > depth or head >= depth or t.load(u64, 0, i) == 0 or t.load(u64, 1, i) == 0) return error.Invalid;
            var j: u32 = 0;
            while (j < i) : (j += 1) if (!mixed and t.load(u64, 1, i) == t.load(u64, 1, j)) return error.Invalid;
            end += depth;
            if (end > t.samples) return error.Invalid;
        }
        for (b.positions[1..6]) |pos| {
            const entry = layout.position(region_bytes, pos);
            if (@atomicLoad(u32, &entry.length, .acquire) != t.rows or (t.rows != 0 and @atomicLoad(u64, &entry.identity_sequence, .acquire) != t.seq())) return error.Stale;
        }
        for (b.positions[6..9]) |pos| if (@atomicLoad(u32, &layout.position(region_bytes, pos).length, .acquire) != end) return error.Invalid;
        return t;
    }
    pub fn sample(t: *const Table, row: u32, index: u32) Sample {
        const at = t.load(u32, 3, row) + @as(u32, @intCast((@as(u64, t.load(u32, 5, row)) + index) % t.load(u32, 2, row)));
        return .{ .time = t.load(i64, 6, at), .bits = t.load(u64, 7, at), .present = t.load(u32, 8, at) };
    }
};
pub const Open = struct {
    table: Table,
    writer: ?ss.Writer,
    prior_state: ss.SourceState,
    prior_reason: u32,
    prior_time: i64,
    pub fn close(open: *Open, utc: i64) void {
        if (open.writer) |*w| {
            w.end(.has_value, utc) catch unreachable;
            open.writer = null;
        }
    }
    pub fn closeUnchanged(open: *Open) void {
        if (open.writer) |*w| {
            w.end(if (open.prior_state == .has_value) .has_value else .{ .unavailable = open.prior_reason }, open.prior_time) catch unreachable;
            open.writer = null;
        }
    }
};
pub fn begin(b: Binding) Error!Open {
    const t = try Table.init(b);
    return beginTable(t);
}
fn beginTable(t: Table) Error!Open {
    const b = t.binding;
    const entry = t.layout.source(t.bytes(), b.data_source);
    const state = @atomicLoad(ss.SourceState, &entry.state, .acquire);
    const reason = @atomicLoad(u32, &entry.reason, .acquire);
    const time = @atomicLoad(i64, &entry.write_time_unix_ms, .acquire);
    return .{ .table = t, .prior_state = state, .prior_reason = reason, .prior_time = time, .writer = ss.Writer.begin(t.bytes(), b.data_source) catch return error.Shared };
}
fn put(w: *const ss.Writer, b: Binding, comptime T: type, slot: usize, row: u32, value: T) void {
    w.store(T, b.positions[slot], row, (&value)[0..1]) catch unreachable;
}
pub fn validateBinding(o: *const st.Owner, b: Binding) Error!void {
    const table = try Table.init(b);
    return validateTable(o, table);
}
fn validateTable(o: *const st.Owner, table: Table) Error!void {
    const b = table.binding;
    const ptr = @intFromPtr(b.region.?);
    const len: usize = @intCast(b.byte_length);
    if (st.overlaps(ptr, len, @intFromPtr(o), @intCast(o.active_length))) return error.Invalid;
    // Shared mappings are allowed; shared writer sources and overlapping mappings aren't.
    for (o.bindings) |origin| {
        try separate(b, origin.table);
        if (b.region == origin.table.region) try separatePositions(table, origin.table, false);
    }
    for (o.results[0..o.result_count]) |result| {
        try separate(b, result);
        if (b.region == result.region) try separatePositions(table, result, true);
    }
    if (o.gpu_vector != null) {
        const reader = vector.HistoryReader.init(o) catch return error.Shared;
        reader.separate(b.region, b.byte_length, &.{ b.rows_source, b.data_source }, &table.specs) catch return error.Invalid;
    }
    if (o.result_count > 1) _ = try bindingGroup(o, b);
}
fn bindingGroup(o: *const st.Owner, b: Binding) Error!u32 {
    for (o.results[0..o.result_count], 0..) |result, group| if (b.region == result.region and b.byte_length == result.byte_length) return @intCast(group);
    return error.Invalid;
}
fn metricBinding(o: *const st.Owner, b: Binding, metric: u32) Error!void {
    if (o.result_count > 1 and o.metrics[metric].result_group != try bindingGroup(o, b)) return error.Stale;
}
fn separatePositions(t: Table, raw: p.TableBinding, result: bool) Error!void {
    const other = storage.Table.init(raw, 0, result) catch return error.Shared;
    const used: usize = if (result) 12 else 16;
    for (t.specs) |spec| for (other.specs[0..used]) |prior| {
        if (spec.size != 0 and prior.size != 0 and @as(u64, spec.offset) < @as(u64, prior.offset) + prior.size and @as(u64, prior.offset) < @as(u64, spec.offset) + spec.size) return error.Invalid;
    };
    const provenance = other.layout.checkedSpec(other.bytes(), raw.provenance_position) catch return error.Shared;
    for (t.specs) |spec| if (spec.size != 0 and provenance.size != 0 and @as(u64, spec.offset) < @as(u64, provenance.offset) + provenance.size and @as(u64, provenance.offset) < @as(u64, spec.offset) + spec.size) return error.Invalid;
}
fn separate(b: Binding, raw: p.TableBinding) Error!void {
    if (b.region == raw.region) {
        for ([_]u32{ raw.rows_source, raw.value_source, raw.health_source, raw.provenance_source }) |src| if (src == b.rows_source or src == b.data_source) return error.Invalid;
    } else if (st.overlaps(@intFromPtr(b.region.?), @intCast(b.byte_length), @intFromPtr(raw.region.?), @intCast(raw.byte_length))) return error.Invalid;
}
pub fn preflight(o: *const st.Owner, b: Binding, completed: []const u32) Error!void {
    try validateBinding(o, b);
    // Native commit closes raw sources before resolving result references, and
    // append resolves the result once more. Reserve that post-write read's
    // Table validation headroom before any source can be changed.
    for (o.sources[0..o.source_count]) |src| try postReadHeadroom(src.binding);
    for (o.results[0..o.result_count]) |result| try postReadHeadroom(result);
    const table = try Table.init(b);
    var row: u32 = 0;
    while (row < table.rows) : (row += 1) {
        if (metricIndex(o, table.load(u64, 1, row))) |metric| try metricBinding(o, b, metric);
    }
    for (completed, 0..) |mi, i| {
        if (mi >= o.metric_count) return error.Invalid;
        for (completed[0..i]) |prior| if (prior == mi) return error.Invalid;
    }
}
fn postReadHeadroom(b: p.TableBinding) Error!void {
    const pointer = b.region orelse return error.Invalid;
    if (@intFromPtr(pointer) % 8 != 0 or b.byte_length > std.math.maxInt(usize)) return error.Invalid;
    const bytes: []align(8) u8 = @alignCast(pointer[0..@intCast(b.byte_length)]);
    const layout = ss.layout.Layout.load(bytes) catch return error.Shared;
    for ([_]u32{ b.value_source, b.health_source, b.provenance_source }) |source| {
        if (source >= layout.header.source_count) return error.Invalid;
        if (@atomicLoad(u64, &layout.source(bytes, source).sequence, .acquire) > ss.ended_bit - 6) return error.Capacity;
    }
}
fn metricIndex(o: *const st.Owner, id: u64) ?u32 {
    for (o.metrics[0..o.metric_count], 0..) |m, i| if (m.id == id) return @intCast(i);
    return null;
}
/// Status is the public SamplingObservationStatus (Current=1). Retained good
/// references deliberately do not turn an unsuccessful round into a number.
pub fn currentSample(o: *const st.Owner, mi: u32, utc: i64) Error!Sample {
    const location = st.resultLocation(o, mi) catch return error.Shared;
    const result = location.table;
    const result_row = location.row;
    var sample = Sample{ .time = utc };
    if (result.load(u32, 11, result_row) != 1) return sample;
    const id = result.load(u64, 2, result_row);
    const generation = result.load(u64, 3, result_row);
    const identity = result.load(u64, 8, result_row);
    const ordinal = result.load(u32, 9, result_row);
    const source = st.sourceIndexForBinding(o, ordinal) orelse return error.Invalid;
    const row = result.load(u32, 10, result_row);
    if (row >= o.sources[source].rows) return error.Invalid;
    const raw = st.sourceTableByBinding(o, ordinal) catch return error.Shared;
    if (raw.load(u64, 15, row) != id or raw.load(u64, 0, row) != identity or raw.load(u64, 5, row) != generation or raw.load(u32, 4, row) != 1) return sample;
    const low = raw.load(u64, 1, row);
    const high = raw.load(u64, 2, row);
    const number: f64 = switch (raw.load(u32, 3, row)) {
        1 => @bitCast(low),
        2 => @floatFromInt(@as(i64, @bitCast(low))),
        3 => @floatFromInt(low),
        4 => @floatFromInt((@as(u128, high) << 64) | low),
        else => return sample,
    };
    if (!std.math.isFinite(number)) return error.Invalid;
    sample.present = 1;
    sample.bits = @bitCast(number);
    return sample;
}
fn appendSample(open: *Open, row: u32, sample: Sample) void {
    const t = &open.table;
    const depth = t.load(u32, 2, row);
    const count = t.load(u32, 4, row);
    const head = t.load(u32, 5, row);
    const at = t.load(u32, 3, row) + @as(u32, @intCast((@as(u64, head) + count) % depth));
    const w = &open.writer.?;
    put(w, t.binding, i64, 6, at, sample.time);
    put(w, t.binding, u64, 7, at, sample.bits);
    put(w, t.binding, u32, 8, at, sample.present);
    put(w, t.binding, u32, 4, row, @intCast(@min(count + @as(u64, 1), depth)));
    if (count == depth) put(w, t.binding, u32, 5, row, @intCast((@as(u64, head) + 1) % depth));
}
/// Parent acquires this Open before its first public store. All checks are
/// completed by preflight; no allocation or fallible work remains here.
pub fn appendOpened(o: *const st.Owner, open: *Open, completed: []const u32, utc: i64) void {
    for (completed) |mi| {
        var row: u32 = 0;
        while (row < open.table.rows) : (row += 1) {
            if (open.table.load(u64, 1, row) != o.metrics[mi].id or open.table.load(u64, 0, row) != o.metrics[mi].identity) continue;
            appendSample(open, row, currentSample(o, mi, utc) catch unreachable);
        }
    }
}
/// Complete native admission, callable by the parent's existing permit callback.
/// No C# history/retention decision and no kernel Owner layout extension needed.
pub fn commit(o: *st.Owner, b: Binding, completed: []const u32, token: u64, utc: i64) p.Code {
    return commitMany(o, (&b)[0..1], completed, token, utc);
}
pub fn commitMany(o: *st.Owner, bindings: []const Binding, completed: []const u32, token: u64, utc: i64) p.Code {
    return commitMixed(o, bindings, &.{}, completed, token, utc);
}
pub fn commitMixed(o: *st.Owner, bindings: []const Binding, mixed: []const MixedBinding, completed: []const u32, token: u64, utc: i64) p.Code {
    defer o.detach();
    if (utc < 0 or o.work.region == null or !o.work.prepared or token != o.token) return o.fail(.stale_plan, .none, token, o.token);
    if (bindings.len + mixed.len > o.result_count) return o.fail(.invalid_contract, .binding, bindings.len + mixed.len, o.result_count);
    var tables: [2]Table = undefined;
    var mixed_tables: [2]MixedTable = undefined;
    var has_vector_history = false;
    for (bindings, 0..) |b, i| {
        preflight(o, b, completed) catch |err| return o.fail(if (err == error.Shared) .shared_section else if (err == error.Capacity) .capacity_exceeded else if (err == error.Stale) .stale_identity else .invalid_contract, if (err == error.Capacity) .generation else .binding, 0, 0);
        for (bindings[0..i]) |prior| if (b.region == prior.region) return o.fail(.invalid_contract, .binding, i, 0);
        tables[i] = Table.init(b) catch unreachable;
    }
    for (mixed, 0..) |b, i| {
        mixed_tables[i] = preflightMixed(o, b, completed) catch |err| return o.fail(if (err == error.Shared) .shared_section else if (err == error.Capacity) .capacity_exceeded else if (err == error.Stale) .stale_identity else .invalid_contract, if (err == error.Capacity) .generation else .binding, 0, 0);
        for (bindings) |prior| if (b.base.region == prior.region) return o.fail(.invalid_contract, .binding, i, 0);
        for (mixed[0..i]) |prior| if (b.base.region == prior.base.region) return o.fail(.invalid_contract, .binding, i, 0);
        tables[bindings.len + i] = mixed_tables[i].base;
        var row: u32 = 0;
        while (row < mixed_tables[i].base.rows) : (row += 1) if (mixed_tables[i].load(u32, 0, row) == 2) {
            has_vector_history = true;
        };
    }
    // Capture only native metadata before commit detaches Work. No value copy.
    const selected = if (has_vector_history) vector.historySelection(o) catch return o.fail(.stale_identity, .binding, 0, 0) else null;
    if (has_vector_history and o.gpu_vector != null) {
        const reader = vector.HistoryReader.init(o) catch return o.fail(.shared_section, .binding, 0, 0);
        reader.preflight() catch |err| return o.fail(if (err == error.Capacity) .capacity_exceeded else .shared_section, .generation, 0, 0);
    }
    if (completed.len != o.work.request_count or completed.len > o.work.requested.len) return o.fail(.invalid_contract, .metrics, completed.len, o.work.request_count);
    const stable = o.work.requested[0..o.work.request_count];
    for (completed, 0..) |metric, i| {
        for (completed[0..i]) |prior| if (prior == metric) return o.fail(.invalid_contract, .metrics, metric, 0);
        var found = false;
        for (stable) |expected| if (metric == expected) {
            found = true;
            break;
        };
        if (!found) return o.fail(.invalid_contract, .metrics, metric, 0);
    }
    // Validate provenance before opening any writer. `completed` must be exactly
    // the caller's admitted completion list, whose lifetime spans this call.
    const address = @intFromPtr(completed.ptr);
    const size = completed.len * @sizeOf(u32);
    if (completed.len != 0 and st.overlaps(address, size, @intFromPtr(o), @intCast(o.active_length))) return o.fail(.invalid_contract, .work, 0, 0);
    for (tables[0 .. bindings.len + mixed.len]) |t| if (completed.len != 0 and st.overlaps(address, size, @intFromPtr(t.binding.region.?), @intCast(t.binding.byte_length))) return o.fail(.invalid_contract, .work, 0, 0);
    // The kernel already captured these requests in the held work prefix.
    // Use that immutable list after commit; no extra copy or allocation and no
    // foreign input that can be overwritten by public stores/Open scratch.
    var opens: [2]Open = undefined;
    var open_indices: [2]usize = undefined;
    var opened: usize = 0;
    defer for (opens[0..opened]) |*open| open.closeUnchanged();
    for (tables[0 .. bindings.len + mixed.len], 0..) |table, i| {
        if (table.rows == 0 or (completed.len == 0 and selected == null)) continue;
        opens[opened] = beginTable(table) catch return o.fail(.shared_section, .binding, 0, 0);
        open_indices[opened] = i;
        opened += 1;
    }
    const code = round.commit(o, token, utc);
    if (code != .ok) return code;
    for (opens[0..opened], 0..) |*open, i| {
        const index = open_indices[i];
        if (index < bindings.len) appendOpened(o, open, stable, utc) else appendMixed(o, open, &mixed_tables[index - bindings.len], stable, selected, utc);
        open.close(utc);
    }
    return .ok;
}
const Row = struct { id: u64, identity: u64, depth: u32, start: u32, count: u32 = 0 };
/// Scratch upper bound; actual configure allocates only normalized requests and
/// their retained samples, never a capacity-sized payload bank.
pub fn configureWorkSize(request_count: u32, sample_count: u32) u64 {
    return @as(u64, request_count) * @sizeOf(Row) + @as(u64, sample_count) * @sizeOf(Sample) + 16;
}
pub fn configure(o: *st.Owner, b: Binding, requirements: []const Requirement, work: []align(8) u8, utc: i64) Error!void {
    o.detach();
    defer o.detach();
    if (utc < 0) return error.Invalid;
    try validateBinding(o, b);
    const t = try Table.init(b);
    if (st.overlaps(@intFromPtr(work.ptr), work.len, @intFromPtr(o), @intCast(o.active_length)) or st.overlaps(@intFromPtr(work.ptr), work.len, @intFromPtr(b.region.?), @intCast(b.byte_length))) return error.Invalid;
    for (o.bindings) |origin| if (st.overlaps(@intFromPtr(work.ptr), work.len, @intFromPtr(origin.table.region.?), @intCast(origin.table.byte_length))) return error.Invalid;
    for (o.results[0..o.result_count]) |result| if (st.overlaps(@intFromPtr(work.ptr), work.len, @intFromPtr(result.region.?), @intCast(result.byte_length))) return error.Invalid;
    if (requirements.len != 0 and st.overlaps(@intFromPtr(work.ptr), work.len, @intFromPtr(requirements.ptr), requirements.len * @sizeOf(Requirement))) return error.Invalid;
    // Normalize duplicates without allocating or touching the binding.
    var row_count: u32 = 0;
    var sample_count: u64 = 0;
    for (requirements, 0..) |req, i| {
        if (req.reserved != 0 or req.rounds == 0 or metricIndex(o, req.metric_id) == null) return error.Invalid;
        try metricBinding(o, b, metricIndex(o, req.metric_id).?);
        var duplicate = false;
        for (requirements[0..i]) |prior| if (prior.metric_id == req.metric_id) {
            duplicate = true;
            break;
        };
        if (duplicate) continue;
        var depth = req.rounds;
        for (requirements[i + 1 ..]) |other| if (other.metric_id == req.metric_id) {
            depth = @max(depth, other.rounds);
        };
        row_count += 1;
        sample_count += depth;
    }
    if (row_count > t.capacity or sample_count > t.samples) return error.Capacity;
    if (row_count == 0 and t.rows == 0) return;
    if (work.len < configureWorkSize(row_count, @intCast(sample_count))) return error.Workspace;
    var fba = std.heap.FixedBufferAllocator.init(work);
    const rows = fba.allocator().alloc(Row, row_count) catch return error.Workspace;
    const samples = fba.allocator().alloc(Sample, @intCast(sample_count)) catch return error.Workspace;
    var used: u32 = 0;
    var next: u32 = 0;
    for (requirements, 0..) |req, i| {
        var duplicate = false;
        for (requirements[0..i]) |prior| if (prior.metric_id == req.metric_id) {
            duplicate = true;
            break;
        };
        if (duplicate) continue;
        var depth = req.rounds;
        for (requirements[i + 1 ..]) |other| if (other.metric_id == req.metric_id) {
            depth = @max(depth, other.rounds);
        };
        const mi = metricIndex(o, req.metric_id).?;
        var row = Row{ .id = req.metric_id, .identity = o.metrics[mi].identity, .depth = depth, .start = next };
        var found = false;
        var prior: u32 = 0;
        while (prior < t.rows) : (prior += 1) {
            if (t.load(u64, 1, prior) != row.id or t.load(u64, 0, prior) != row.identity) continue;
            found = true;
            const count = t.load(u32, 4, prior);
            row.count = @min(count, depth);
            var j: u32 = 0;
            while (j < row.count) : (j += 1) {
                const sample = t.sample(prior, count - row.count + j);
                if (sample.time < 0 or sample.present > 1 or (sample.present == 0 and sample.bits != 0) or (sample.present == 1 and !std.math.isFinite(@as(f64, @bitCast(sample.bits))))) return error.Invalid;
                samples[next + j] = sample;
            }
            break;
        }
        if (!found) {
            const location = st.resultLocation(o, mi) catch return error.Shared;
            const time = location.table.load(i64, 6, location.row);
            // Completed-attempt validity is independent of UTC0 and catalog
            // revisions. Kernel owns this identity-scoped continuity fact.
            if (o.metrics[mi].attempted) {
                samples[next] = try currentSample(o, mi, time);
                row.count = 1;
            }
        }
        rows[used] = row;
        used += 1;
        next += depth;
    }
    var membership = t.rows != row_count;
    for (rows, 0..) |row, i| if (i >= t.rows or t.load(u64, 0, @intCast(i)) != row.identity or t.load(u64, 1, @intCast(i)) != row.id or t.load(u32, 2, @intCast(i)) != row.depth) {
        membership = true;
    };
    // No stores before both writer acquisitions succeed.
    // Acquire data first: a rejected rows writer must not bump the row identity
    // sequence and invalidate untouched metadata from the previous plan.
    var open = try begin(b);
    defer open.closeUnchanged();
    var identity: ?ss.Writer = if (membership) ss.Writer.begin(t.bytes(), b.rows_source) catch return error.Shared else null;
    defer {
        if (identity) |*w| {
            w.end(.has_value, utc) catch {};
        }
    }
    const values = &open.writer.?;
    if (identity) |*w| {
        for (rows, 0..) |row, i| put(w, b, u64, 0, @intCast(i), row.identity);
        w.setLength(b.positions[0], row_count, 0) catch unreachable;
        w.end(.has_value, utc) catch unreachable;
        identity = null;
    }
    for (rows, 0..) |row, i| {
        const ri: u32 = @intCast(i);
        put(values, b, u64, 1, ri, row.id);
        put(values, b, u32, 2, ri, row.depth);
        put(values, b, u32, 3, ri, row.start);
        put(values, b, u32, 4, ri, row.count);
        put(values, b, u32, 5, ri, 0);
        var j: u32 = 0;
        while (j < row.depth) : (j += 1) {
            const sample = if (j < row.count) samples[row.start + j] else Sample{};
            put(values, b, i64, 6, row.start + j, sample.time);
            put(values, b, u64, 7, row.start + j, sample.bits);
            put(values, b, u32, 8, row.start + j, sample.present);
        }
    }
    for (b.positions[1..6]) |pos| values.setLength(pos, row_count, t.seq()) catch unreachable;
    for (b.positions[6..9]) |pos| values.setLength(pos, @intCast(sample_count), 0) catch unreachable;
    open.close(utc);
}
comptime {
    std.debug.assert(@sizeOf(Binding) == 64 and @sizeOf(Requirement) == 16 and @sizeOf(Sample) == 24);
}

/// Additive schema. Legacy Binding/Requirement remain scalar-only and unchanged.
pub const MixedBinding = extern struct {
    base: Binding,
    kind_position: u32,
    address_position: u32,
    text_position: u32,
    live_position: u32,
    version: u32 = 1,
    reserved: u32 = 0,
};
pub const MixedRequirement = extern struct {
    kind: u32, // 1 kernel metric, 2 GPU engine
    rounds: u32,
    id: u64, // metric ID or vector source ID; never an untyped engine hash
    address: vector.Ref = .{},
    identity: u64 = 0, // scalar: derive; GPU: exact published row guard
};
pub const MixedTable = struct {
    base: Table,
    binding: MixedBinding,
    specs: [4]ss.PositionSpec,
    text_length: u32,
    fn positions(b: MixedBinding) [4]u32 {
        return .{ b.kind_position, b.address_position, b.text_position, b.live_position };
    }
    fn load(t: *const MixedTable, comptime T: type, slot: usize, row: u32) T {
        const ptr: *const T = @ptrCast(@alignCast(t.base.layout.dataArea(t.base.bytes()).ptr + t.specs[slot].offset + @as(usize, row) * @sizeOf(T)));
        return @atomicLoad(T, ptr, .acquire);
    }
    fn address(t: *const MixedTable, row: u32) Error![]const u8 {
        const packed_ref = t.load(u64, 1, row);
        const offset: u32 = @truncate(packed_ref);
        const length: u32 = @intCast(packed_ref >> 32);
        if (@as(u64, offset) + length > t.text_length) return error.Invalid;
        return t.base.layout.dataArea(t.base.bytes())[t.specs[2].offset + offset ..][0..length];
    }
    pub fn init(b: MixedBinding) Error!MixedTable {
        if (b.version != 1 or b.reserved != 0) return error.Invalid;
        var t = MixedTable{ .base = try Table.initMode(b.base, true), .binding = b, .specs = undefined, .text_length = 0 };
        const layout = t.base.layout;
        const region = t.base.bytes();
        const ps = positions(b);
        for (ps, 0..) |pos, i| {
            for (b.base.positions) |prior| if (prior == pos) return error.Invalid;
            for (ps[0..i]) |prior| if (prior == pos) return error.Invalid;
            const spec = layout.checkedSpec(region, pos) catch return error.Shared;
            const width: u32 = if (i == 2) 1 else if (i == 1) 8 else 4;
            if (spec.source != b.base.data_source or spec.kind != (if (i == 2) ss.PositionKind.string_table else .column) or spec.element_width != width or spec.identity != (if (i == 2) ss.none else b.base.positions[0])) return error.Invalid;
            for (t.base.specs) |prior| if (overlapSpec(spec, prior)) return error.Invalid;
            for (t.specs[0..i]) |prior| if (overlapSpec(spec, prior)) return error.Invalid;
            const entry = layout.position(region, pos);
            if (i == 2) {
                t.text_length = @atomicLoad(u32, &entry.length, .acquire);
                if (t.text_length > spec.size) return error.Invalid;
            } else {
                t.base.capacity = @min(t.base.capacity, spec.size / width);
                if (entry.length != t.base.rows or (t.base.rows != 0 and entry.identity_sequence != t.base.seq())) return error.Stale;
            }
            t.specs[i] = spec;
        }
        if (t.base.rows > t.base.capacity) return error.Invalid;
        var row: u32 = 0;
        while (row < t.base.rows) : (row += 1) {
            const kind = t.load(u32, 0, row);
            const name = try t.address(row);
            if ((kind != 1 and kind != 2) or t.load(u32, 3, row) > 1 or (kind == 1 and name.len != 0) or (kind == 2 and name.len == 0)) return error.Invalid;
            var prior: u32 = 0;
            while (prior < row) : (prior += 1) if (kind == t.load(u32, 0, prior) and t.base.load(u64, 1, row) == t.base.load(u64, 1, prior) and std.mem.eql(u8, name, try t.address(prior))) return error.Invalid;
        }
        return t;
    }
};
fn overlapSpec(a: ss.PositionSpec, b: ss.PositionSpec) bool {
    return a.size != 0 and b.size != 0 and @as(u64, a.offset) < @as(u64, b.offset) + b.size and @as(u64, b.offset) < @as(u64, a.offset) + a.size;
}
fn validateMixed(o: *const st.Owner, t: MixedTable) Error!void {
    try validateTable(o, t.base);
    // Reuse all kernel/provenance/vector separation checks for the extra bytes.
    for (t.specs) |spec| {
        var extra = t.base;
        extra.specs = @splat(spec);
        try validateTable(o, extra);
    }
}
fn preflightMixed(o: *const st.Owner, b: MixedBinding, completed: []const u32) Error!MixedTable {
    const t = try MixedTable.init(b);
    try validateMixed(o, t);
    for (o.sources[0..o.source_count]) |src| try postReadHeadroom(src.binding);
    for (o.results[0..o.result_count]) |result| try postReadHeadroom(result);
    var row: u32 = 0;
    while (row < t.base.rows) : (row += 1) {
        if (t.load(u32, 0, row) == 1) {
            if (metricIndex(o, t.base.load(u64, 1, row))) |mi| try metricBinding(o, b.base, mi);
        } else if (b.base.region != o.results[0].region or b.base.byte_length != o.results[0].byte_length or o.gpu_vector == null or t.base.load(u64, 1, row) != o.gpu_vector.?.source_id) return error.Stale;
    }
    for (completed, 0..) |mi, i| {
        if (mi >= o.metric_count) return error.Invalid;
        for (completed[0..i]) |prior| if (prior == mi) return error.Invalid;
    }
    return t;
}
fn putMixed(w: *const ss.Writer, t: *const MixedTable, comptime T: type, slot: usize, row: u32, value: T) void {
    const ps = MixedTable.positions(t.binding);
    w.store(T, ps[slot], row, (&value)[0..1]) catch unreachable;
}
fn appendMixed(o: *const st.Owner, open: *Open, t: *const MixedTable, completed: []const u32, selection: ?vector.HistorySelection, utc: i64) void {
    var has_vector = false;
    var probe: u32 = 0;
    while (probe < t.base.rows) : (probe += 1) if (t.load(u32, 0, probe) == 2) {
        has_vector = true;
    };
    const reader: ?vector.HistoryReader = if (has_vector) vector.HistoryReader.init(o) catch unreachable else null;
    var row: u32 = 0;
    while (row < t.base.rows) : (row += 1) {
        if (t.load(u32, 0, row) == 1) {
            for (completed) |mi| if (o.metrics[mi].id == t.base.load(u64, 1, row) and o.metrics[mi].identity == t.base.load(u64, 0, row)) {
                appendSample(open, row, currentSample(o, mi, utc) catch unreachable);
                break;
            };
            continue;
        }
        if (t.load(u32, 3, row) == 0) continue;
        const r = &(reader orelse unreachable);
        const at = r.find(t.address(row) catch unreachable, 0) catch unreachable;
        // Missing/replaced identity retires this history key, including catalog
        // probes. A later identical address cannot inherit this subscription.
        if (at == null or r.identity(at.?) != t.base.load(u64, 0, row)) {
            putMixed(&open.writer.?, t, u32, 3, row, 0);
            continue;
        }
        if (selection) |selected| if (selected.isSelected(at.?)) {
            const value = r.value(at.?); // DATA AREA after accepted publication
            appendSample(open, row, .{ .time = utc, .bits = value.bits, .present = value.present });
        };
    }
}
const MixedRow = struct { base: Row, kind: u32, address: vector.Ref };
pub fn mixedWorkSize(requests: u32, samples: u32, text_bytes: u32) u64 {
    return @as(u64, requests) * @sizeOf(MixedRow) + @as(u64, samples) * @sizeOf(Sample) + text_bytes + 16;
}
fn inputAddress(text: vector.Text, ref: vector.Ref) Error![]const u8 {
    if (text.reserved != 0 or @as(u64, ref.offset) + ref.length > text.length or (text.length != 0 and text.bytes == null)) return error.Invalid;
    return if (ref.length == 0) &.{} else text.bytes.?[ref.offset..][0..ref.length];
}
fn sameRequirement(a: MixedRequirement, b: MixedRequirement, text: vector.Text) Error!bool {
    return a.kind == b.kind and a.id == b.id and std.mem.eql(u8, try inputAddress(text, a.address), try inputAddress(text, b.address));
}
pub fn configureMixed(o: *st.Owner, b: MixedBinding, requirements: []const MixedRequirement, text: vector.Text, work: []align(8) u8, utc: i64) Error!void {
    var accepted = false;
    defer if (!accepted) o.detach();
    if (utc < 0) return error.Invalid;
    // An explicit plan change may follow GPU prepare so a brand-new field can
    // receive its first sample in that SAME admission. Borrow a disjoint small
    // configure partition; do not detach or overwrite prepared round metadata.
    if (o.work.region) |kernel| {
        if (!o.work.prepared or st.overlaps(@intFromPtr(work.ptr), work.len, @intFromPtr(kernel), o.work.length)) return error.Invalid;
        var occupied: u64 = o.work.length;
        if (o.work.gpu_pending) |gpu| {
            if (st.overlaps(@intFromPtr(work.ptr), work.len, @intFromPtr(gpu.base), gpu.length)) return error.Invalid;
            occupied = std.math.add(u64, occupied, gpu.length) catch return error.Capacity;
        }
        if (o.work.npu_plan) |npu| {
            if (st.overlaps(@intFromPtr(work.ptr), work.len, @intFromPtr(npu.base.?), @intCast(npu.length))) return error.Invalid;
            occupied = std.math.add(u64, occupied, npu.length) catch return error.Capacity;
        }
        const total = std.math.add(u64, occupied, work.len) catch return error.Capacity;
        if (total > o.config.work_limit) {
            _ = o.fail(.capacity_exceeded, .work, total, o.config.work_limit);
            return error.Capacity;
        }
    }
    // GPU history belongs to the normal result mapping. Reject a wrong-class
    // request before interpreting stored mixed rows: an administrator table
    // may legitimately contain scalar rows without mixed extension metadata.
    for (requirements) |req| if (req.kind == 2 and (b.base.region != o.results[0].region or b.base.byte_length != o.results[0].byte_length)) return error.Invalid;
    const t = try MixedTable.init(b);
    try validateMixed(o, t);
    const address = @intFromPtr(work.ptr);
    if (st.overlaps(address, work.len, @intFromPtr(o), @intCast(o.active_length)) or st.overlaps(address, work.len, @intFromPtr(b.base.region.?), @intCast(b.base.byte_length))) return error.Invalid;
    for (o.bindings) |origin| if (st.overlaps(address, work.len, @intFromPtr(origin.table.region.?), @intCast(origin.table.byte_length))) return error.Invalid;
    for (o.results[0..o.result_count]) |result| if (st.overlaps(address, work.len, @intFromPtr(result.region.?), @intCast(result.byte_length))) return error.Invalid;
    if (o.gpu_vector) |gpu| if (st.overlaps(address, work.len, @intFromPtr(gpu.region.?), @intCast(gpu.byte_length))) return error.Invalid;
    if (requirements.len != 0 and st.overlaps(address, work.len, @intFromPtr(requirements.ptr), requirements.len * @sizeOf(MixedRequirement))) return error.Invalid;
    if (text.bytes) |ptr| if (st.overlaps(address, work.len, @intFromPtr(ptr), text.length)) return error.Invalid;
    _ = try inputAddress(text, .{});
    const reader: ?vector.HistoryReader = if (o.gpu_vector != null) vector.HistoryReader.init(o) catch return error.Shared else null;
    var count: u32 = 0;
    var sample_count: u64 = 0;
    var text_count: u64 = 0;
    for (requirements, 0..) |req, i| {
        const name = try inputAddress(text, req.address);
        if (req.rounds == 0 or req.id == 0) return error.Invalid;
        switch (req.kind) {
            1 => {
                if (req.identity != 0 or req.address.offset != 0 or name.len != 0) return error.Invalid;
                try metricBinding(o, b.base, metricIndex(o, req.id) orelse return error.Stale);
            },
            2 => {
                const r = &(reader orelse return error.Stale);
                if (req.identity == 0 or name.len == 0 or req.id != r.table.binding.source_id) return error.Invalid;
                const published = r.find(name, req.identity) catch null;
                if (published == null) {
                    const candidate = (vector.historyCandidate(o, o.token, name, req.identity) catch return error.Stale) orelse return error.Stale;
                    if (candidate.source_id != req.id) return error.Stale;
                }
            },
            else => return error.Invalid,
        }
        var duplicate = false;
        for (requirements[0..i]) |prior| if (try sameRequirement(prior, req, text)) {
            duplicate = true;
            break;
        };
        if (duplicate) continue;
        var depth = req.rounds;
        for (requirements[i + 1 ..]) |other| if (try sameRequirement(req, other, text)) {
            depth = @max(depth, other.rounds);
        };
        count += 1;
        sample_count += depth;
        text_count += name.len;
    }
    if (count > t.base.capacity) {
        _ = o.fail(.capacity_exceeded, .metrics, count, t.base.capacity);
        return error.Capacity;
    }
    if (sample_count > t.base.samples) {
        _ = o.fail(.capacity_exceeded, .payload, sample_count, t.base.samples);
        return error.Capacity;
    }
    if (text_count > t.specs[2].size) {
        _ = o.fail(.capacity_exceeded, .payload, text_count, t.specs[2].size);
        return error.Capacity;
    }
    if (count == 0 and t.base.rows == 0) {
        accepted = true;
        return;
    }
    const required = mixedWorkSize(count, @intCast(sample_count), @intCast(text_count));
    if (work.len < required) {
        _ = o.fail(.workspace_unavailable, .work, required, work.len);
        return error.Workspace;
    }
    var fba = std.heap.FixedBufferAllocator.init(work);
    const rows = fba.allocator().alloc(MixedRow, count) catch return error.Workspace;
    const samples = fba.allocator().alloc(Sample, @intCast(sample_count)) catch return error.Workspace;
    const names = fba.allocator().alloc(u8, @intCast(text_count)) catch return error.Workspace;
    var used: u32 = 0;
    var start: u32 = 0;
    var text_used: u32 = 0;
    for (requirements, 0..) |req, i| {
        var duplicate = false;
        for (requirements[0..i]) |prior| if (try sameRequirement(req, prior, text)) {
            duplicate = true;
            break;
        };
        if (duplicate) continue;
        const name = try inputAddress(text, req.address);
        var depth = req.rounds;
        for (requirements[i + 1 ..]) |other| if (try sameRequirement(req, other, text)) {
            depth = @max(depth, other.rounds);
        };
        const mi = if (req.kind == 1) metricIndex(o, req.id) else null;
        var row = MixedRow{ .base = .{ .id = req.id, .identity = if (mi) |index| o.metrics[index].identity else req.identity, .depth = depth, .start = start }, .kind = req.kind, .address = .{ .offset = text_used, .length = @intCast(name.len) } };
        @memcpy(names[text_used..][0..name.len], name);
        text_used += @intCast(name.len);
        var found = false;
        var prior: u32 = 0;
        while (prior < t.base.rows) : (prior += 1) {
            if (t.load(u32, 0, prior) != req.kind or t.base.load(u64, 1, prior) != req.id or t.base.load(u64, 0, prior) != row.base.identity or !std.mem.eql(u8, name, try t.address(prior))) continue;
            found = true;
            const prior_count = t.base.load(u32, 4, prior);
            row.base.count = @min(prior_count, depth);
            var j: u32 = 0;
            while (j < row.base.count) : (j += 1) {
                const sample = t.base.sample(prior, prior_count - row.base.count + j);
                if (sample.time < 0 or sample.present > 1 or (sample.present == 0 and sample.bits != 0) or (sample.present == 1 and !std.math.isFinite(@as(f64, @bitCast(sample.bits))))) return error.Invalid;
                samples[start + j] = sample;
            }
            break;
        }
        if (!found) {
            if (mi) |index| {
                if (o.metrics[index].attempted) {
                    const location = st.resultLocation(o, index) catch return error.Shared;
                    samples[start] = try currentSample(o, index, location.table.load(i64, 6, location.row));
                    row.base.count = 1;
                }
            } else {
                const r = &(reader orelse unreachable);
                const at = r.find(name, req.identity) catch null;
                if (at) |index| {
                    const value = r.value(index);
                    if (value.attempted) {
                        if (value.attempt < 0 or (value.present == 1 and !std.math.isFinite(@as(f64, @bitCast(value.bits))))) return error.Invalid;
                        samples[start] = .{ .time = value.attempt, .bits = value.bits, .present = value.present };
                        row.base.count = 1;
                    }
                }
            }
        }
        rows[used] = row;
        used += 1;
        start += depth;
    }
    var membership = count != t.base.rows;
    for (rows, 0..) |row, i| {
        const index: u32 = @intCast(i);
        if (index >= t.base.rows or t.base.load(u64, 0, index) != row.base.identity or t.base.load(u64, 1, index) != row.base.id or t.base.load(u32, 2, index) != row.base.depth or t.load(u32, 0, index) != row.kind or !std.mem.eql(u8, names[row.address.offset..][0..row.address.length], try t.address(index))) membership = true;
    }
    var open = try beginTable(t.base);
    defer open.closeUnchanged();
    var identity: ?ss.Writer = if (membership) ss.Writer.begin(t.base.bytes(), b.base.rows_source) catch return error.Shared else null;
    defer if (identity) |*w| {
        w.end(.has_value, utc) catch {};
    };
    if (identity) |*w| {
        for (rows, 0..) |row, i| put(w, b.base, u64, 0, @intCast(i), row.base.identity);
        w.setLength(b.base.positions[0], count, 0) catch unreachable;
        w.end(.has_value, utc) catch unreachable;
        identity = null;
    }
    const values = &open.writer.?;
    for (rows, 0..) |row, i| {
        const index: u32 = @intCast(i);
        put(values, b.base, u64, 1, index, row.base.id);
        put(values, b.base, u32, 2, index, row.base.depth);
        put(values, b.base, u32, 3, index, row.base.start);
        put(values, b.base, u32, 4, index, row.base.count);
        put(values, b.base, u32, 5, index, 0);
        putMixed(values, &t, u32, 0, index, row.kind);
        putMixed(values, &t, u64, 1, index, @as(u64, row.address.offset) | (@as(u64, row.address.length) << 32));
        putMixed(values, &t, u32, 3, index, 1);
        var j: u32 = 0;
        while (j < row.base.depth) : (j += 1) {
            const sample = if (j < row.base.count) samples[row.base.start + j] else Sample{};
            put(values, b.base, i64, 6, row.base.start + j, sample.time);
            put(values, b.base, u64, 7, row.base.start + j, sample.bits);
            put(values, b.base, u32, 8, row.base.start + j, sample.present);
        }
    }
    values.store(u8, b.text_position, 0, names) catch unreachable;
    for (b.base.positions[1..6]) |pos| values.setLength(pos, count, t.base.seq()) catch unreachable;
    for ([_]u32{ b.kind_position, b.address_position, b.live_position }) |pos| values.setLength(pos, count, t.base.seq()) catch unreachable;
    for (b.base.positions[6..9]) |pos| values.setLength(pos, @intCast(sample_count), 0) catch unreachable;
    values.setLength(b.text_position, text_used, 0) catch unreachable;
    open.close(utc);
    accepted = true;
}
comptime {
    std.debug.assert(@sizeOf(MixedBinding) == 88 and @sizeOf(MixedRequirement) == 32);
}
