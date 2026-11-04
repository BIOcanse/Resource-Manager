//! Discovery-owned lifecycle over the authoritative vector algorithms.
//! All arrays are actual-run cold metadata or one operation's caller work.
const std = @import("std");
pub const v = @import("vector_inputs.zig");
const c = @import("catalog_compiler.zig");
const p = c.p;
const st = @import("state.zig");
pub const Extra = extern struct {
    topology_bytes: u64 = 0,
    vector_work_bytes: u64 = 0,
    scope_count: u32 = 0,
    topology_text_bytes: u32 = 0,
    gpu_bound_rows: u32 = 0,
    reserved: u32 = 0,
    grouping_work_bytes: u64 = 0,
};
pub const Admission = extern struct { binding: v.BoundVector = .{}, bound_row_capacity: u32 = 0, reserved: u32 = 0 };
pub const Active = struct {
    extra: Extra = .{},
    memory: []align(8) u8 = &.{},
    topology: v.ScopeTopology = .{},
    admission: ?Admission = null,
    enabled: bool = false,
};
pub const GpuHeader = extern struct { input: v.GpuInput = .{}, query_token: u64 = 0 };
pub const SensorHeader = extern struct {
    input: v.SensorInput = .{},
    incarnation: u64 = 0,
    capability: u64 = 0,
    observation: u64 = 0,
    query_token: u64 = 0,
};
pub const Inputs = extern struct {
    gpu: ?*GpuHeader = null,
    sensor: ?*SensorHeader = null,
    gpu_capacity: u32 = 0,
    sensor_capacity: u32 = 0,
    scope_count: u32 = 0,
    reserved: u32 = 0,
    npu_generations: ?[*]u64 = null,
    npu_row_scopes: ?[*]const u32 = null,
    excluded_count: u32 = 0,
    raw_count: u32 = 0,
    excluded_rows: ?[*]const u32 = null,
};
pub const Round = struct {
    request: v.Request = .{},
    npu: v.NpuPlan = .{},
    inputs: Inputs = .{},
    gpu_work: []align(8) u8 = &.{},
    publication: ?*const v.GpuPublication = null,
    npu_source: u64 = 0,
    gpu_source: u64 = 0,
    sensor_source: u64 = 0,
    gpu_columns: v.GpuInput = .{},
    sensor_columns: v.SensorInput = .{},
};
pub fn checked(code: u32, failure: *p.Failure) c.Error!void {
    if (code != 0) {
        if (failure.code == .ok) failure.code = @fromBackingInt(@intCast(code));
        return error.Invalid;
    }
}
fn add(comptime T: type, count: u64, cursor: *u64) c.Error!void {
    cursor.* = (std.math.add(u64, cursor.*, 7) catch return error.Exhausted) & ~@as(u64, 7);
    cursor.* = std.math.add(u64, cursor.*, std.math.mul(u64, @sizeOf(T), count) catch return error.Exhausted) catch return error.Exhausted;
}
fn get(text: v.Text, ref: v.Ref) c.Error![]const u8 {
    if (ref.offset == p.none) {
        if (ref.length != 0) return error.Invalid;
        return &.{};
    }
    return c.get(.{ .bytes = text.bytes, .length = text.length, .reserved = text.reserved }, .{ .offset = ref.offset, .length = ref.length });
}
pub fn validate(topology: v.ScopeTopology, scopes: c.Scopes, text: c.Text) c.Error!void {
    if (!c.pointers(c.Scopes, scopes)) return error.Invalid;
    if (topology.count != scopes.count or topology.reserved != 0 or topology.text.reserved != 0) return error.Invalid;
    if (topology.count != 0) inline for (@typeInfo(v.ScopeTopology).@"struct".field_names, @typeInfo(v.ScopeTopology).@"struct".field_types) |field_name, field_type| {
        if (@typeInfo(field_type) == .optional and @field(topology, field_name) == null) return error.Invalid;
    };
    var i: u32 = 0;
    while (i < topology.count) : (i += 1) {
        if (!c.eql(try get(topology.text, topology.keys.?[i]), try c.get(text, scopes.keys.?[i])) or topology.expansions.?[i] != scopes.expansions.?[i] or topology.flags.?[i] != scopes.flags.?[i] or topology.generations.?[i] != scopes.generations.?[i] or topology.luids.?[i] != scopes.luids.?[i] or topology.driver_present.?[i] > 1) return error.Identity;
        inline for (.{ "parents", "pnp", "pnp_drivers" }) |field| _ = try get(topology.text, @field(topology, field).?[i]);
    }
}
pub fn validateRoles(roles: anytype, admission: ?Admission) c.Error!void {
    var seen: u32 = 0;
    for (roles) |role| {
        const flags = role.flags & 14;
        if (seen & flags != 0) return error.Identity;
        seen |= flags;
        if (role.flags & 2 != 0) {
            const bound = admission orelse return error.Invalid;
            if (bound.reserved != 0 or bound.binding.source_id != role.source_id) return error.Identity;
        }
    }
    if ((seen & 2 != 0) != (admission != null)) return error.Invalid;
}
pub fn workBound(compiled: c.Compiled, roles: anytype) c.Error!u64 {
    var bytes: u64 = 256 + @as(u64, compiled.bindings.count) * 16 + @as(u64, compiled.catalog.metrics.count) * 4;
    for (roles) |role| {
        if (role.flags & 4 != 0) bytes = std.math.add(u64, bytes, try npuWorkBound(compiled, role.source_id)) catch return error.Exhausted;
        if (role.flags & 2 != 0) bytes = std.math.add(u64, bytes, @sizeOf(GpuHeader) + @as(u64, role.row_capacity) * 28 + 64) catch return error.Exhausted;
        if (role.flags & 8 != 0) bytes = std.math.add(u64, bytes, @sizeOf(SensorHeader) + @as(u64, role.row_capacity) * 24 + 64) catch return error.Exhausted;
    }
    return (std.math.add(u64, bytes, 7) catch return error.Exhausted) & ~@as(u64, 7);
}
pub fn topologySize(topology: v.ScopeTopology) c.Error!u64 {
    var bytes: u64 = 0;
    inline for (@typeInfo(v.ScopeTopology).@"struct".field_types) |field_type| if (@typeInfo(field_type) == .optional) {
        const Element = @typeInfo(@typeInfo(field_type).optional.child).pointer.child;
        try add(Element, topology.count, &bytes);
    };
    try add(u8, try topologyTextBytes(topology), &bytes);
    return (std.math.add(u64, bytes, 7) catch return error.Exhausted) & ~@as(u64, 7);
}
pub fn topologyTextBytes(topology: v.ScopeTopology) c.Error!u32 {
    var strings: u64 = 0;
    var i: u32 = 0;
    while (i < topology.count) : (i += 1) inline for (.{ "keys", "parents", "pnp", "pnp_drivers" }) |field| {
        strings += (try get(topology.text, @field(topology, field).?[i])).len;
    };
    if (strings > std.math.maxInt(u32)) return error.Payload;
    return @intCast(strings);
}
pub fn inputSize(count: u32, text_capacity: u32) c.Error!u64 {
    var bytes: u64 = 0;
    inline for (@typeInfo(v.ScopeTopology).@"struct".field_types) |field_type| if (@typeInfo(field_type) == .optional) {
        const Element = @typeInfo(@typeInfo(field_type).optional.child).pointer.child;
        try add(Element, count, &bytes);
    };
    try add(u8, text_capacity, &bytes);
    return (std.math.add(u64, bytes, 7) catch return error.Exhausted) & ~@as(u64, 7);
}
pub fn inputLayout(count: u32, text_capacity: u32, memory: []align(8) u8) c.Error!v.ScopeTopology {
    if (memory.len < try inputSize(count, text_capacity)) return error.Work;
    var fba = std.heap.FixedBufferAllocator.init(memory);
    const a = fba.allocator();
    var out = v.ScopeTopology{ .count = count };
    inline for (@typeInfo(v.ScopeTopology).@"struct".field_names, @typeInfo(v.ScopeTopology).@"struct".field_types) |field_name, field_type| if (@typeInfo(field_type) == .optional) {
        const Element = @typeInfo(@typeInfo(field_type).optional.child).pointer.child;
        const rows = a.alignedAlloc(Element, .@"8", count) catch return error.Work;
        @memset(rows, std.mem.zeroes(Element));
        @field(out, field_name) = rows.ptr;
    };
    const bytes = a.alignedAlloc(u8, .@"8", text_capacity) catch return error.Work;
    out.text = .{ .bytes = bytes.ptr, .length = text_capacity };
    return out;
}
pub fn npuWorkBound(compiled: c.Compiled, source_id: u64) c.Error!u64 {
    _ = source_id;
    return v.discoveryNpuWorkSize(compiled.bindings.count, compiled.catalog.metrics.count, compiled.scopes.count) catch return error.Exhausted;
}
pub fn compact(topology: v.ScopeTopology, memory: []align(8) u8) v.ScopeTopology {
    var fba = std.heap.FixedBufferAllocator.init(memory);
    const a = fba.allocator();
    var out = topology;
    inline for (@typeInfo(v.ScopeTopology).@"struct".field_names, @typeInfo(v.ScopeTopology).@"struct".field_types) |field_name, field_type| if (@typeInfo(field_type) == .optional) {
        const Element = @typeInfo(@typeInfo(field_type).optional.child).pointer.child;
        const rows = a.alignedAlloc(Element, .@"8", topology.count) catch unreachable;
        if (topology.count != 0) @memcpy(rows, @field(topology, field_name).?[0..topology.count]);
        @field(out, field_name) = rows.ptr;
    };
    _ = a.alloc(u8, (8 - fba.end_index % 8) % 8) catch unreachable;
    const text = memory[fba.end_index..];
    var used: u32 = 0;
    var i: u32 = 0;
    while (i < topology.count) : (i += 1) inline for (.{ "keys", "parents", "pnp", "pnp_drivers" }) |field| {
        const ref = @field(topology, field).?[i];
        if (ref.offset == p.none) {
            @constCast(@field(out, field).?)[i] = ref;
        } else {
            const bytes = get(topology.text, ref) catch unreachable;
            @memcpy(text[used..][0..bytes.len], bytes);
            @constCast(@field(out, field).?)[i] = .{ .offset = used, .length = @intCast(bytes.len) };
            used += @intCast(bytes.len);
        }
    };
    out.text = .{ .bytes = text.ptr, .length = used };
    return out;
}
pub fn bindings(compiled: c.Compiled, generation: u64) v.Bindings {
    const b = compiled.bindings;
    return .{ .count = b.count, .catalog_generation = generation, .field_codes = b.field_codes, .scope_indices = b.scope_indices, .metric_indices = b.metric_indices, .scope_ids = b.scope_ids, .addresses = @ptrCast(b.addresses), .device_keys = @ptrCast(b.device_keys), .text = .{ .bytes = compiled.text.bytes, .length = compiled.text.length } };
}
pub fn copyRequest(request: @import("selection.zig").Request, allocator: std.mem.Allocator, limit: u64, failure: *p.Failure) c.Error!v.Request {
    const bytes = @as(u64, request.count) * 8 + request.text.length;
    if (bytes > limit) return c.fail(failure, .capacity_exceeded, .work, bytes, limit);
    const keys = allocator.alloc(v.Ref, request.count) catch return error.Work;
    const text = allocator.alloc(u8, request.text.length) catch return error.Work;
    if (request.text.length != 0) @memcpy(text, request.text.bytes.?[0..request.text.length]);
    var i: u32 = 0;
    while (i < request.count) : (i += 1) keys[i] = .{ .offset = request.keys.?[i].offset, .length = request.keys.?[i].length };
    return .{ .flags = (if (request.flags & 3 != 0) @as(u32, 1) else 0) | (if (request.flags & 4 != 0) @as(u32, 2) else 0), .count = request.count, .text = .{ .bytes = text.ptr, .length = @intCast(text.len) }, .addresses = keys.ptr };
}
pub fn expand(o: *st.Owner, active: *const Active, compiled: c.Compiled, roles: anytype, initial: []const u32, request: @import("selection.zig").Request, allocator: std.mem.Allocator, input_limit: u64, failure: *p.Failure) c.Error!struct { round: Round, metrics: []const u32 } {
    var round: Round = .{};
    var indices = initial;
    if (!active.enabled) return .{ .round = round, .metrics = indices };
    const b = bindings(compiled, o.catalog_generation);
    round.request = try copyRequest(request, allocator, input_limit, failure);
    for (roles) |role| {
        if (role.flags & 2 != 0) {
            if (round.gpu_source != 0) return error.Invalid;
            round.gpu_source = role.source_id;
        }
        if (role.flags & 4 != 0) {
            if (round.npu_source != 0) return error.Invalid;
            round.npu_source = role.source_id;
        }
        if (role.flags & 8 != 0) {
            if (round.sensor_source != 0) return error.Invalid;
            round.sensor_source = role.source_id;
        }
    }
    if (round.gpu_source != 0) {
        var size: u64 = 0;
        try checked(v.rm_metric_vector_gpu_expand_size(o, &size, failure), failure);
        const bytes = allocator.alignedAlloc(u8, .@"8", @intCast(@max(size, 8))) catch return error.Work;
        var selected: v.Selection = .{};
        try checked(v.rm_metric_vector_gpu_expand(o, &b, &active.topology, &round.request, if (indices.len == 0) null else indices.ptr, @intCast(indices.len), bytes.ptr, bytes.len, &selected, failure), failure);
        indices = if (selected.count == 0) &.{} else selected.metric_indices.?[0..selected.count];
    }
    if (round.npu_source != 0) {
        var size: u64 = 0;
        try checked(v.rm_metric_vector_npu_size(o, &b, &active.topology, if (indices.len == 0) null else indices.ptr, @intCast(indices.len), round.npu_source, &size, failure), failure);
        const bytes = allocator.alignedAlloc(u8, .@"8", @intCast(@max(size, 8))) catch return error.Work;
        try checked(v.rm_metric_vector_npu_expand(o, &b, &active.topology, if (indices.len == 0) null else indices.ptr, @intCast(indices.len), round.npu_source, bytes.ptr, bytes.len, &round.npu, failure), failure);
        indices = if (round.npu.metric_count == 0) &.{} else round.npu.metrics.?[0..round.npu.metric_count];
    }
    return .{ .round = round, .metrics = indices };
}
pub fn attachNpu(o: *st.Owner, round: *Round, expected: u64, failure: *p.Failure) c.Error!void {
    if (round.npu_source != 0) try checked(v.rm_metric_vector_npu_attach(o, o.token, expected, &round.npu, failure), failure);
}
pub fn excluded(round: *const Round, row: u32) bool {
    var i: u32 = 0;
    while (i < round.npu.target_count) : (i += 1) if (round.npu.target_observations.?[i] == row) return true;
    return false;
}
pub fn syntheticRule(round: *const Round, rule: u32) bool {
    var i: u32 = 0;
    while (i < round.npu.target_count) : (i += 1) if (round.npu.targets.?[i] == rule) return true;
    return false;
}
pub fn inputs(o: *st.Owner, round: *Round, roles: anytype, source_ids: []const u64, observations: []const u64, compiled: c.Compiled, allocator: std.mem.Allocator, active: *const Active, failure: *p.Failure) c.Error!void {
    const row_scopes = allocator.alloc(u32, o.work.raw.count) catch return error.Work;
    const omitted = allocator.alloc(u32, round.npu.target_count) catch return error.Work;
    @memset(row_scopes, p.none);
    var omitted_count: u32 = 0;
    var row: u32 = 0;
    while (row < o.work.raw.count) : (row += 1) {
        if (excluded(round, row)) {
            omitted[omitted_count] = row;
            omitted_count += 1;
            continue;
        }
        const ri = o.work.expected[row];
        if (round.npu_source != 0 and o.sources[o.rules[ri].source].id == round.npu_source and compiled.bindings.field_codes.?[ri] == @import("field_codes.zig").npu_engine_reported_running_time_ratio_percent) row_scopes[row] = compiled.bindings.scope_indices.?[ri];
    }
    round.inputs = .{ .npu_generations = if (round.npu_source == 0) null else round.npu.sample_generations, .npu_row_scopes = row_scopes.ptr, .scope_count = active.topology.count, .raw_count = o.work.raw.count, .excluded_count = omitted_count, .excluded_rows = omitted.ptr };
    for (roles) |role| for (source_ids, observations) |id, observation| if (role.source_id == id) {
        const si = blk: {
            for (o.sources[0..o.source_count], 0..) |source, i| if (source.id == id) break :blk i;
            return error.Identity;
        };
        const source = o.sources[si];
        if (role.flags & 2 != 0) {
            const header = allocator.create(GpuHeader) catch return error.Work;
            const reader = v.HistoryReader.init(o) catch return error.Invalid;
            if (reader.rows > active.extra.gpu_bound_rows) return c.fail(failure, .capacity_exceeded, .binding, reader.rows, active.extra.gpu_bound_rows);
            header.* = .{ .input = .{ .source_id = id, .incarnation = source.incarnation, .capability = source.capability, .observation = observation, .status = 0 }, .query_token = o.token };
            header.input.luids = (allocator.alloc(u64, role.row_capacity) catch return error.Work).ptr;
            header.input.physical = (allocator.alloc(u32, role.row_capacity) catch return error.Work).ptr;
            header.input.engines = (allocator.alloc(u32, role.row_capacity) catch return error.Work).ptr;
            header.input.classes = (allocator.alloc(u32, role.row_capacity) catch return error.Work).ptr;
            header.input.usage = (allocator.alloc(f64, role.row_capacity) catch return error.Work).ptr;
            round.gpu_columns = header.input;
            round.inputs.gpu = header;
            round.inputs.gpu_capacity = role.row_capacity;
            var size: u64 = 0;
            try checked(v.rm_metric_vector_gpu_work_size(o, role.row_capacity, &size, failure), failure);
            if (size > active.extra.vector_work_bytes) return c.fail(failure, .capacity_exceeded, .work, size, active.extra.vector_work_bytes);
            round.gpu_work = allocator.alignedAlloc(u8, .@"8", @intCast(@max(size, 8))) catch return error.Work;
        }
        if (role.flags & 8 != 0) {
            const header = allocator.create(SensorHeader) catch return error.Work;
            header.* = .{ .input = .{ .source_id = id, .status = 0 }, .incarnation = source.incarnation, .capability = source.capability, .observation = observation, .query_token = o.token };
            header.input.indices = (allocator.alloc(u32, role.row_capacity) catch return error.Work).ptr;
            header.input.rpm = (allocator.alloc(f64, role.row_capacity) catch return error.Work).ptr;
            header.input.percent = (allocator.alloc(f64, role.row_capacity) catch return error.Work).ptr;
            header.input.valid = (allocator.alloc(u32, role.row_capacity) catch return error.Work).ptr;
            round.sensor_columns = header.input;
            round.inputs.sensor = header;
            round.inputs.sensor_capacity = role.row_capacity;
        }
    };
}
pub fn prePrepare(o: *st.Owner, round: *Round, active: *const Active, compiled: c.Compiled, source_ids: []const u64, statuses: []const u32, observations: []const u64, failure: *p.Failure) c.Error!void {
    const b = bindings(compiled, o.catalog_generation);
    if (round.inputs.sensor) |header| {
        const columns = round.sensor_columns;
        if (header.query_token != o.token or header.input.source_id != round.sensor_source or header.input.indices != columns.indices or header.input.rpm != columns.rpm or header.input.percent != columns.percent or header.input.valid != columns.valid) return error.Identity;
        if (header.input.count > round.inputs.sensor_capacity) return c.fail(failure, .capacity_exceeded, .binding, header.input.count, round.inputs.sensor_capacity);
        try checkSource(o, header.input.source_id, header.incarnation, header.capability, header.observation, header.input.status, source_ids, statuses, observations);
        try checked(v.rm_metric_vector_notebook_apply(o, o.token, &b, &active.topology, &header.input, failure), failure);
    }
    if (round.inputs.gpu) |header| {
        const columns = round.gpu_columns;
        if (header.query_token != o.token or header.input.source_id != round.gpu_source or header.input.luids != columns.luids or header.input.physical != columns.physical or header.input.engines != columns.engines or header.input.classes != columns.classes or header.input.usage != columns.usage) return error.Identity;
        if (header.input.count > round.inputs.gpu_capacity) return c.fail(failure, .capacity_exceeded, .binding, header.input.count, round.inputs.gpu_capacity);
        try checkSource(o, header.input.source_id, header.input.incarnation, header.input.capability, header.input.observation, header.input.status, source_ids, statuses, observations);
        if (header.input.status < 1 or header.input.status > 5 or header.input.observed_ms < 0) return error.Invalid;
        // This is a real PDH query with vector operands, but no scalar busy
        // value. Preserve its query receipt without fabricating a maximum.
        // Skipped acquisition remains skipped; NPU synthetic slots are separate.
        for (o.work.expected[0..o.work.raw.count], 0..) |ri, row| if (o.sources[o.rules[ri].source].id == round.gpu_source and compiled.bindings.field_codes.?[ri] == @import("field_codes.zig").gpu_engine_busy) {
            const raw = o.work.raw.observations;
            raw.status.?[row] = if (header.input.status == 5) 4 else if (header.input.status == 4) 3 else 2;
            raw.presence.?[row] = 0;
            raw.observed_ms.?[row] = header.input.observed_ms;
            raw.reasons.?[row] = header.input.reason;
        };
    }
}
fn checkSource(o: *st.Owner, id: u64, incarnation: u64, capability: u64, observation: u64, status: u32, source_ids: []const u64, statuses: []const u32, observations: []const u64) c.Error!void {
    var found = false;
    for (source_ids, statuses, observations) |source, completed, expected| if (source == id) {
        if (status != completed or observation != expected) return error.Identity;
        found = true;
    };
    if (!found) return error.Identity;
    for (o.sources[0..o.source_count]) |source| if (source.id == id) {
        if (source.incarnation != incarnation or source.capability != capability) return error.Identity;
        return;
    };
    return error.Identity;
}
pub fn postPrepare(o: *st.Owner, round: *Round, active: *const Active, compiled: c.Compiled, failure: *p.Failure) c.Error!void {
    if (round.inputs.gpu) |header| {
        const b = bindings(compiled, o.catalog_generation);
        var publication: ?*const v.GpuPublication = null;
        try checked(v.rm_metric_vector_gpu_prepare(o, o.token, &b, &active.topology, &round.request, &header.input, round.gpu_work.ptr, round.gpu_work.len, &publication, failure), failure);
        if (publication.?.rows > active.extra.gpu_bound_rows) return c.fail(failure, .capacity_exceeded, .binding, publication.?.rows, active.extra.gpu_bound_rows);
        round.publication = publication;
    }
}
pub fn clear(round: *Round) void {
    round.* = .{};
}
comptime {
    std.debug.assert(@sizeOf(Extra) == 40 and @sizeOf(Admission) == 136);
    std.debug.assert(@sizeOf(GpuHeader) == 112 and @sizeOf(SensorHeader) == 112 and @sizeOf(Inputs) == 64);
}
