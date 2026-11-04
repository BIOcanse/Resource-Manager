//! Inactive replacement. Linking exports does not create an owner or sample.
const std = @import("std");
pub const protocol = @import("protocol.zig");
pub const state = @import("state.zig");
pub const catalog = @import("catalog.zig");
pub const round = @import("round.zig");
const storage = @import("storage.zig");
const p = protocol;
fn finish(code: p.Code, o: ?*state.Owner, f: ?*p.Failure) u32 {
    if (f) |out| out.* = if (code == .ok) .{} else if (o) |owner| owner.failure else .{ .code = code };
    return @intFromEnum(code);
}
fn ownerOf(o: ?*state.Owner) ?*state.Owner {
    const owner = o orelse return null;
    return if (owner.magic == state.Owner.live_magic) owner else null;
}
fn configValid(c: p.Config) bool {
    return c.abi_version == p.version and c.source_capacity > 0 and c.max_value_bytes > 0;
}
fn failSize(f: ?*p.Failure, code: p.Code, family: p.Family, required: u64, limit: u64) u32 {
    if (f) |out| out.* = .{ .code = code, .family = family, .required = required, .limit = limit };
    return @intFromEnum(code);
}
pub export fn rm_metric_owner_required_sizes(config: ?*const p.Config, out: ?*p.Sizes, f: ?*p.Failure) u32 {
    const c = config orelse return finish(.invalid_contract, null, f);
    const target = out orelse return finish(.invalid_contract, null, f);
    target.* = .{ .active = 0, .work = 0 };
    if (c.abi_version != p.version) return finish(.invalid_contract, null, f);
    if (!configValid(c.*)) return finish(.invalid_contract, null, f);
    if (c.payload_work_bytes > std.math.maxInt(u32)) return failSize(f, .capacity_exceeded, .payload, c.payload_work_bytes, std.math.maxInt(u32));
    const sizes = state.sizes(c.*);
    target.* = sizes;
    if (sizes.active == std.math.maxInt(u64) or sizes.work == std.math.maxInt(u64)) return failSize(f, .capacity_exceeded, .work, std.math.maxInt(u64), c.work_limit);
    if (sizes.active > c.active_limit) return failSize(f, .capacity_exceeded, .active, sizes.active, c.active_limit);
    if (sizes.work > c.work_limit) return failSize(f, .capacity_exceeded, .work, sizes.work, c.work_limit);
    if (sizes.active > std.math.maxInt(usize) or sizes.work > std.math.maxInt(usize)) return finish(.invalid_contract, null, f);
    return finish(.ok, null, f);
}
pub export fn rm_metric_owner_bind(config: ?*const p.Config, active: ?[*]u8, length: u64, bindings: ?[*]const p.TableBinding, count: u32, result: ?*const p.TableBinding, out: ?*?*state.Owner, f: ?*p.Failure) u32 {
    return bindInternal(config, active, length, bindings, null, count, if (result) |r| @as([*]const p.TableBinding, @ptrCast(r)) else null, 1, out, f);
}
pub export fn rm_metric_owner_bind_grouped(config: ?*const p.Config, active: ?[*]u8, length: u64, bindings: ?[*]const p.SourceBinding, count: u32, results: ?[*]const p.TableBinding, group_count: u32, out: ?*?*state.Owner, f: ?*p.Failure) u32 {
    if (group_count != 2) {
        if (out) |target| target.* = null;
        return finish(.invalid_contract, null, f);
    }
    return bindInternal(config, active, length, null, bindings, count, results, group_count, out, f);
}
fn bindInternal(config: ?*const p.Config, active: ?[*]u8, length: u64, legacy: ?[*]const p.TableBinding, declarations: ?[*]const p.SourceBinding, count: u32, results: ?[*]const p.TableBinding, group_count: u32, out: ?*?*state.Owner, f: ?*p.Failure) u32 {
    const target = out orelse return finish(.invalid_contract, null, f);
    target.* = null;
    const c = config orelse return finish(.invalid_contract, null, f);
    var sizes: p.Sizes = undefined;
    const code = rm_metric_owner_required_sizes(c, &sizes, f);
    if (code != 0) return code;
    const raw_base = active orelse return finish(.invalid_contract, null, f);
    const groups = results orelse return finish(.invalid_contract, null, f);
    if (@intFromPtr(raw_base) % 8 != 0 or (legacy == null and declarations == null)) return finish(.invalid_contract, null, f);
    if (length > c.active_limit) return failSize(f, .capacity_exceeded, .active, length, c.active_limit);
    if (length < sizes.active or length > std.math.maxInt(usize)) return failSize(f, .capacity_exceeded, .active, sizes.active, length);
    if (count != c.source_capacity) return failSize(f, .invalid_contract, .sources, count, c.source_capacity);
    for (groups[0..group_count], 0..) |rb, gi| {
        _ = storage.Table.init(rb, 0, true) catch return finish(.invalid_contract, null, f);
        if (state.overlaps(@intFromPtr(raw_base), @intCast(length), @intFromPtr(rb.region.?), @intCast(rb.byte_length))) return finish(.invalid_contract, null, f);
        for (groups[0..gi]) |prior| if (prior.region == rb.region or state.bindingAlias(prior, rb)) return finish(.invalid_contract, null, f);
    }
    var i: u32 = 0;
    while (i < count) : (i += 1) {
        const declaration = if (declarations) |ds| ds[i] else p.SourceBinding{ .source_id = 0, .access_region = 0, .table = legacy.?[i] };
        if (declaration.reserved != 0 or declaration.access_region >= group_count or (declarations != null and declaration.source_id == 0)) return finish(.invalid_contract, null, f);
        const b = declaration.table;
        _ = storage.Table.init(b, 0, false) catch return finish(.invalid_contract, null, f);
        if (b.region != groups[declaration.access_region].region or state.overlaps(@intFromPtr(raw_base), @intCast(length), @intFromPtr(b.region.?), @intCast(b.byte_length))) return finish(.invalid_contract, null, f);
        for (groups[0..group_count]) |rb| if (state.bindingAlias(b, rb)) return finish(.invalid_contract, null, f);
        var j: u32 = 0;
        while (j < i) : (j += 1) {
            const prior = if (declarations) |ds| ds[j] else p.SourceBinding{ .source_id = 0, .access_region = 0, .table = legacy.?[j] };
            if (state.bindingAlias(b, prior.table) or (declarations != null and declaration.source_id == prior.source_id)) return finish(.invalid_contract, null, f);
        }
    }
    const base: [*]align(8) u8 = @alignCast(raw_base);
    var fba = std.heap.FixedBufferAllocator.init(base[0..@intCast(length)]);
    const a = fba.allocator();
    const o = a.create(state.Owner) catch unreachable;
    const sources = a.alloc(state.Source, c.source_capacity) catch unreachable;
    const bound = a.alloc(p.SourceBinding, c.source_capacity) catch unreachable;
    const rules = a.alloc(state.Rule, c.rule_capacity) catch unreachable;
    const metrics = a.alloc(state.Metric, c.metric_capacity) catch unreachable;
    const heap = a.alloc(u32, c.rule_capacity) catch unreachable;
    for (sources, 0..) |*src, bi| {
        bound[bi] = if (declarations) |ds| ds[bi] else .{ .source_id = 0, .access_region = 0, .table = legacy.?[bi] };
        src.* = .{ .binding = bound[bi].table, .binding_ordinal = @intCast(bi), .access_region = bound[bi].access_region };
    }
    const empty = p.TableBinding{ .region = null, .byte_length = 0, .rows_source = 0, .value_source = 0, .health_source = 0 };
    o.* = .{ .config = c.*, .sizes = sizes, .active_length = length, .sources = sources, .bindings = bound, .rules = rules, .metrics = metrics, .payload_heap = heap, .result = groups[0], .results = .{ groups[0], if (group_count == 2) groups[1] else empty }, .result_count = group_count, .legacy_bindings = declarations == null };
    target.* = o;
    return finish(.ok, o, f);
}
fn bytesOf(pointer: ?[*]u8, length: u64) ?[]align(8) u8 {
    const base = pointer orelse return null;
    if (@intFromPtr(base) % 8 != 0 or length > std.math.maxInt(usize)) return null;
    return @alignCast(base[0..@intCast(length)]);
}
pub export fn rm_metric_owner_catalog_input(owner: ?*state.Owner, work: ?[*]u8, length: u64, out: ?*p.Catalog, f: ?*p.Failure) u32 {
    const o = ownerOf(owner) orelse return finish(.closed, null, f);
    o.detach();
    const target = out orelse return finish(.invalid_contract, null, f);
    if (length > o.config.work_limit) {
        o.detach();
        return finish(o.fail(.capacity_exceeded, .work, length, o.config.work_limit), o, f);
    }
    const bytes = bytesOf(work, length) orelse return finish(.invalid_contract, null, f);
    if (!state.validWork(o, bytes)) return finish(o.fail(.workspace_unavailable, .work, o.sizes.work, bytes.len), o, f);
    var fba = std.heap.FixedBufferAllocator.init(bytes);
    target.* = state.allocateCatalogInput(fba.allocator(), o.config) catch unreachable;
    return finish(.ok, o, f);
}
pub export fn rm_metric_owner_replace_catalog(owner: ?*state.Owner, input: ?*const p.Catalog, work: ?[*]u8, length: u64, utc: i64, f: ?*p.Failure) u32 {
    const o = ownerOf(owner) orelse return finish(.closed, null, f);
    const c = input orelse {
        o.detach();
        return finish(.invalid_contract, null, f);
    };
    if (length > o.config.work_limit) {
        o.detach();
        return finish(o.fail(.capacity_exceeded, .work, length, o.config.work_limit), o, f);
    }
    const bytes = bytesOf(work, length) orelse {
        o.detach();
        return finish(.invalid_contract, null, f);
    };
    return finish(catalog.replace(o, c.*, bytes, utc), o, f);
}
pub export fn rm_metric_owner_replace_catalog_bound(owner: ?*state.Owner, input: ?*const p.Catalog, origins: ?[*]const p.SourceBinding, count: u32, work: ?[*]u8, length: u64, utc: i64, f: ?*p.Failure) u32 {
    const o = ownerOf(owner) orelse return finish(.closed, null, f);
    o.detach();
    const c = input orelse return finish(.invalid_contract, null, f);
    const declarations = origins orelse return finish(.invalid_contract, null, f);
    if (count != o.bindings.len) return finish(o.fail(.invalid_contract, .binding, count, o.bindings.len), o, f);
    if (length > o.config.work_limit) return finish(o.fail(.capacity_exceeded, .work, length, o.config.work_limit), o, f);
    const bytes = bytesOf(work, length) orelse return finish(.invalid_contract, null, f);
    return finish(catalog.replaceBound(o, c.*, declarations[0..count], bytes, utc), o, f);
}
pub export fn rm_metric_owner_result_route(owner: ?*state.Owner, metric: u32, out: ?*p.ResultRoute, f: ?*p.Failure) u32 {
    if (out) |target| target.* = .{};
    const o = ownerOf(owner) orelse return finish(.closed, null, f);
    const target = out orelse return finish(.invalid_contract, null, f);
    if (metric >= o.metric_count) return finish(o.fail(.invalid_contract, .metrics, metric, o.metric_count), o, f);
    const m = o.metrics[metric];
    target.* = .{ .group = m.result_group, .row = m.result_row, .metric_id = m.id, .identity = m.identity, .catalog_generation = o.catalog_generation };
    return finish(.ok, o, f);
}
pub export fn rm_metric_owner_plan(owner: ?*state.Owner, indices: ?[*]const u32, count: u32, work: ?[*]u8, length: u64, utc: i64, out: ?*p.Round, f: ?*p.Failure) u32 {
    const o = ownerOf(owner) orelse return finish(.closed, null, f);
    const target = out orelse {
        o.detach();
        return finish(.invalid_contract, null, f);
    };
    target.* = .{};
    if (count > o.metric_count or (count != 0 and indices == null)) {
        o.detach();
        return finish(.invalid_contract, null, f);
    }
    const requests: []const u32 = if (indices) |ptr| ptr[0..count] else &.{};
    if (length > o.config.work_limit) {
        o.detach();
        return finish(o.fail(.capacity_exceeded, .work, length, o.config.work_limit), o, f);
    }
    const bytes = bytesOf(work, length) orelse {
        o.detach();
        return finish(.invalid_contract, null, f);
    };
    return finish(round.plan(o, requests, bytes, utc, target), o, f);
}
pub export fn rm_metric_owner_prepare(owner: ?*state.Owner, token: u64, f: ?*p.Failure) u32 {
    const o = ownerOf(owner) orelse return finish(.closed, null, f);
    return finish(round.prepare(o, token), o, f);
}
pub export fn rm_metric_owner_commit(owner: ?*state.Owner, token: u64, utc: i64, f: ?*p.Failure) u32 {
    const o = ownerOf(owner) orelse return finish(.closed, null, f);
    return finish(round.commit(o, token, utc), o, f);
}
pub export fn rm_metric_owner_commit_receipt(owner: ?*state.Owner, token: u64, utc: i64, out: ?*p.Receipt, f: ?*p.Failure) u32 {
    if (out) |target| target.* = .{};
    const o = ownerOf(owner) orelse return finish(.closed, null, f);
    const target = out orelse {
        o.detach();
        return finish(.invalid_contract, null, f);
    };
    return finish(round.commitWithReceipt(o, token, utc, target), o, f);
}
pub export fn rm_metric_owner_receipt_view(owner: ?*state.Owner, token: u64, out: ?*?*const p.Receipt, f: ?*p.Failure) u32 {
    if (out) |target| target.* = null;
    const o = ownerOf(owner) orelse return finish(.closed, null, f);
    const target = out orelse {
        o.detach();
        return finish(.invalid_contract, null, f);
    };
    target.* = null;
    if (o.work.region == null or !o.work.prepared or token != o.token) {
        o.detach();
        return finish(o.fail(.stale_plan, .none, token, o.token), o, f);
    }
    target.* = o.work.receipt;
    return finish(.ok, o, f);
}
pub export fn rm_metric_owner_prepared_view(owner: ?*state.Owner, token: u64, out: ?*p.Prepared, f: ?*p.Failure) u32 {
    const o = ownerOf(owner) orelse return finish(.closed, null, f);
    const target = out orelse {
        o.detach();
        return finish(.invalid_contract, null, f);
    };
    target.* = .{ .token = 0, .catalog_generation = 0, .metric_indices = null, .count = 0 };
    if (o.work.region == null or !o.work.prepared or token != o.token) {
        o.detach();
        return finish(o.fail(.stale_plan, .none, token, o.token), o, f);
    }
    target.* = .{ .token = token, .catalog_generation = o.catalog_generation, .metric_indices = o.work.affected.ptr, .count = o.work.affected_count };
    return finish(.ok, o, f);
}
pub export fn rm_metric_owner_abort_detach(owner: ?*state.Owner, f: ?*p.Failure) u32 {
    const o = ownerOf(owner) orelse return finish(.closed, null, f);
    o.detach();
    return finish(.ok, o, f);
}
pub export fn rm_metric_owner_unbind(owner: ?*state.Owner, f: ?*p.Failure) u32 {
    const o = ownerOf(owner) orelse return finish(.closed, null, f);
    return rm_metric_owner_unbind_at(o, o.last_utc_ms, f);
}
pub export fn rm_metric_owner_unbind_at(owner: ?*state.Owner, utc: i64, f: ?*p.Failure) u32 {
    const o = ownerOf(owner) orelse return finish(.closed, null, f);
    o.detach();
    var code: p.Code = .ok;
    @import("vector_inputs.zig").stop(o, utc) catch {
        code = .shared_section;
    };
    for (o.bindings) |binding| markUnavailable(binding.table, utc) catch {
        code = .shared_section;
    };
    for (o.results[0..o.result_count]) |binding| markUnavailable(binding, utc) catch {
        code = .shared_section;
    };
    o.sources = &.{};
    o.bindings = &.{};
    o.rules = &.{};
    o.metrics = &.{};
    o.payload_heap = &.{};
    o.source_count = 0;
    o.rule_count = 0;
    o.metric_count = 0;
    o.result = .{ .region = null, .byte_length = 0, .rows_source = 0, .value_source = 0, .health_source = 0 };
    o.results = .{ o.result, o.result };
    o.result_count = 0;
    o.gpu_vector = null;
    o.magic = 0;
    return finish(code, null, f);
}
fn markUnavailable(binding: p.TableBinding, utc: i64) storage.Error!void {
    const bytes: []align(8) u8 = @alignCast(binding.region.?[0..@intCast(binding.byte_length)]);
    for ([_]u32{ binding.value_source, binding.health_source }) |source| {
        var writer = storage.ss.Writer.begin(bytes, source) catch return error.Shared;
        writer.end(.{ .unavailable = p.no_subscription_reason }, utc) catch return error.Shared;
    }
}
pub export fn rm_metric_owner_diagnostics(owner: ?*state.Owner, out: ?*p.Diagnostics) u32 {
    const o = ownerOf(owner) orelse return @intFromEnum(p.Code.closed);
    const target = out orelse return @intFromEnum(p.Code.invalid_contract);
    var borrowed: u64 = if (o.work.region != null) o.work.length else 0;
    if (o.work.npu_plan) |plan| borrowed += plan.length;
    if (o.work.gpu_pending) |plan| borrowed += plan.length;
    target.* = .{ .catalog_generation = o.catalog_generation, .round_generation = o.round_generation, .active_bytes = o.sizes.active, .work_bytes = borrowed, .bytes_written = o.counters.written, .bytes_touched = o.counters.touched, .bytes_read = o.counters.read, .payload_written = o.counters.payload, .attached = @intFromBool(o.work.region != null), .phase = if (o.work.region == null) 0 else if (o.work.prepared) 2 else 1 };
    return 0;
}
pub fn exportAnchor() void {
    @import("vector_inputs.zig").exportAnchor();
    if (!@import("builtin").is_test) {
        @import("support_exports.zig").exportAnchor();
        @import("discovery_exports.zig").exportAnchor();
    }
    _ = &rm_metric_owner_required_sizes;
    _ = &rm_metric_owner_bind;
    _ = &rm_metric_owner_bind_grouped;
    _ = &rm_metric_owner_replace_catalog_bound;
    _ = &rm_metric_owner_result_route;
    _ = &rm_metric_owner_catalog_input;
    _ = &rm_metric_owner_replace_catalog;
    _ = &rm_metric_owner_plan;
    _ = &rm_metric_owner_prepare;
    _ = &rm_metric_owner_prepared_view;
    _ = &rm_metric_owner_commit;
    _ = &rm_metric_owner_commit_receipt;
    _ = &rm_metric_owner_receipt_view;
    _ = &rm_metric_owner_abort_detach;
    _ = &rm_metric_owner_unbind;
    _ = &rm_metric_owner_unbind_at;
    _ = &rm_metric_owner_diagnostics;
}
test {
    _ = @import("tests.zig");
}
