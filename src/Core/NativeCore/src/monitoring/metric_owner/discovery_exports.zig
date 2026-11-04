//! Separate inactive export anchor. Synchronous and non-owning.
const std = @import("std");
pub const catalog = @import("catalog_compiler.zig");
pub const selection = @import("selection.zig");
pub const inventory = @import("inventory.zig");
const p = catalog.p;
comptime {
    std.debug.assert(@sizeOf(catalog.Ref) == 8 and @sizeOf(catalog.Text) == 16);
    std.debug.assert(@sizeOf(catalog.Sources) == 56 and @sizeOf(catalog.Templates) == 136 and @sizeOf(catalog.Fields) == 56);
    std.debug.assert(@sizeOf(catalog.Scopes) == 136 and @sizeOf(catalog.Handles) == 32 and @sizeOf(catalog.Facts) == 440);
    std.debug.assert(@sizeOf(catalog.Limits) == 32 and @sizeOf(catalog.Bindings) == 96 and @sizeOf(catalog.Legacy) == 32 and @sizeOf(catalog.Compiled) == 544);
    std.debug.assert(@sizeOf(selection.Request) == 32 and @sizeOf(selection.ReadingFacts) == 56 and @sizeOf(selection.ReadingEvents) == 112);
    std.debug.assert(@sizeOf(selection.Selection) == 128 and @sizeOf(selection.ProviderState) == 40 and @sizeOf(selection.ProviderEvent) == 48);
    std.debug.assert(@sizeOf(inventory.Columns) == 64 and @sizeOf(inventory.Facts) == 136 and @sizeOf(inventory.Binding) == 80 and @sizeOf(inventory.Output) == 120);
    std.debug.assert(@sizeOf(inventory.CatalogFacts) == 168);
}
fn invalid(f: ?*p.Failure) u32 {
    if (f) |out| out.* = .{ .code = .invalid_contract };
    return @intFromEnum(p.Code.invalid_contract);
}
fn result(f: ?*p.Failure, failure: p.Failure, err: catalog.Error) u32 {
    var out = failure;
    if (out.code == .ok) out.code = switch (err) {
        error.Identity => .stale_identity,
        error.Capacity, error.Exhausted => .capacity_exceeded,
        error.Work => .workspace_unavailable,
        error.Payload => .payload_capacity,
        else => .invalid_contract,
    };
    if (out.family == .none) out.family = switch (err) {
        error.Payload => .payload,
        error.Work => .work,
        error.Exhausted => .generation,
        else => .none,
    };
    if (f) |target| target.* = out;
    return @intFromEnum(out.code);
}
fn success(f: ?*p.Failure) u32 {
    if (f) |out| out.* = .{};
    return 0;
}
fn bytes(pointer: ?[*]u8, length: u64) ?[]align(8) u8 {
    const ptr = pointer orelse return null;
    if (@intFromPtr(ptr) % 8 != 0 or length > std.math.maxInt(usize)) return null;
    return @as([*]align(8) u8, @alignCast(ptr))[0..@intCast(length)];
}
pub export fn rm_metric_discovery_catalog_size(facts: ?*const catalog.Facts, limits: ?*const catalog.Limits, out: ?*u64, f: ?*p.Failure) u32 {
    const input = facts orelse return invalid(f);
    const cap = limits orelse return invalid(f);
    const target = out orelse return invalid(f);
    var failure: p.Failure = .{};
    const size = catalog.required(input.*, cap.*, &failure) catch |err| return result(f, failure, err);
    target.* = size;
    return success(f);
}
pub export fn rm_metric_discovery_compile(facts: ?*const catalog.Facts, limits: ?*const catalog.Limits, work: ?[*]u8, length: u64, out: ?*catalog.Compiled, f: ?*p.Failure) u32 {
    const input = facts orelse return invalid(f);
    const cap = limits orelse return invalid(f);
    const target = out orelse return invalid(f);
    const memory = bytes(work, length) orelse return invalid(f);
    var failure: p.Failure = .{};
    const compiled = catalog.compile(input.*, cap.*, memory, &failure) catch |err| return result(f, failure, err);
    target.* = compiled;
    return success(f);
}
pub export fn rm_metric_discovery_selection_size(cat: ?*const catalog.Compiled, out: ?*u64, f: ?*p.Failure) u32 {
    const input = cat orelse return invalid(f);
    const target = out orelse return invalid(f);
    target.* = selection.required(input.*) catch |err| return result(f, .{}, err);
    return success(f);
}
pub export fn rm_metric_discovery_select(cat: ?*const catalog.Compiled, facts: ?*const selection.ReadingFacts, request: ?*const selection.Request, work: ?[*]u8, length: u64, out: ?*selection.Selection, f: ?*p.Failure) u32 {
    const input = cat orelse return invalid(f);
    const raw = facts orelse return invalid(f);
    const req = request orelse return invalid(f);
    const target = out orelse return invalid(f);
    const memory = bytes(work, length) orelse return invalid(f);
    var failure: p.Failure = .{};
    const selected = selection.select(input.*, raw.*, req.*, memory, &failure) catch |err| return result(f, failure, err);
    target.* = selected;
    return success(f);
}
pub export fn rm_metric_discovery_observe_size(cat: ?*const catalog.Compiled, out: ?*u64, f: ?*p.Failure) u32 {
    const input = cat orelse return invalid(f);
    const target = out orelse return invalid(f);
    target.* = selection.observeRequired(input.*) catch |err| return result(f, .{}, err);
    return success(f);
}
pub export fn rm_metric_discovery_observe(cat: ?*const catalog.Compiled, prior: ?*const selection.ReadingFacts, events: ?*const selection.ReadingEvents, work: ?[*]u8, length: u64, out: ?*selection.ReadingFacts, f: ?*p.Failure) u32 {
    const input = cat orelse return invalid(f);
    const old = prior orelse return invalid(f);
    const raw = events orelse return invalid(f);
    const target = out orelse return invalid(f);
    const memory = bytes(work, length) orelse return invalid(f);
    var failure: p.Failure = .{};
    const learned = selection.observe(input.*, old.*, raw.*, memory, &failure) catch |err| return result(f, failure, err);
    target.* = learned;
    return success(f);
}
pub export fn rm_metric_discovery_inventory_size(facts: ?*const inventory.Facts, out: ?*u64, f: ?*p.Failure) u32 {
    const input = facts orelse return invalid(f);
    const target = out orelse return invalid(f);
    target.* = inventory.required(input.*) catch |err| return result(f, if (err == error.Capacity) .{ .code = .capacity_exceeded, .family = .binding, .required = input.columns.count, .limit = input.maximum_count } else .{}, err);
    return success(f);
}
pub export fn rm_metric_discovery_inventory(facts: ?*const inventory.Facts, binding: ?*const inventory.Binding, work: ?[*]u8, length: u64, out: ?*inventory.Output, f: ?*p.Failure) u32 {
    const input = facts orelse return invalid(f);
    const table = binding orelse return invalid(f);
    const target = out orelse return invalid(f);
    const memory = bytes(work, length) orelse return invalid(f);
    var failure: p.Failure = .{};
    const published = inventory.publish(input.*, table.*, memory, &failure) catch |err| return result(f, failure, err);
    target.* = published;
    return success(f);
}
pub export fn rm_metric_discovery_inventory_catalog_size(facts: ?*const inventory.CatalogFacts, out: ?*u64, f: ?*p.Failure) u32 {
    const input = facts orelse return invalid(f);
    const target = out orelse return invalid(f);
    target.* = inventory.catalogRequired(input.*) catch |err| return result(f, if (err == error.Capacity) .{ .code = .capacity_exceeded, .family = .binding, .required = input.raw.columns.count, .limit = input.raw.maximum_count } else .{}, err);
    return success(f);
}
pub export fn rm_metric_discovery_inventory_catalog(cat: ?*const catalog.Compiled, facts: ?*const inventory.CatalogFacts, binding: ?*const inventory.Binding, work: ?[*]u8, length: u64, out: ?*inventory.Output, f: ?*p.Failure) u32 {
    const input = cat orelse return invalid(f);
    const raw = facts orelse return invalid(f);
    const table = binding orelse return invalid(f);
    const target = out orelse return invalid(f);
    const memory = bytes(work, length) orelse return invalid(f);
    var failure: p.Failure = .{};
    const published = inventory.publishCatalog(input.*, raw.*, table.*, memory, &failure) catch |err| return result(f, failure, err);
    target.* = published;
    return success(f);
}
pub export fn rm_metric_discovery_provider_begin(state: ?*selection.ProviderState, now_ms: i64, observation: ?*u64, f: ?*p.Failure) u32 {
    const input = state orelse return invalid(f);
    const target = observation orelse return invalid(f);
    target.* = selection.providerBegin(input, now_ms) catch |err| return result(f, .{}, err);
    return success(f);
}
pub export fn rm_metric_discovery_provider_complete(state: ?*selection.ProviderState, event: ?*const selection.ProviderEvent, availability: ?*u32, f: ?*p.Failure) u32 {
    const input = state orelse return invalid(f);
    const completion = event orelse return invalid(f);
    const target = availability orelse return invalid(f);
    selection.providerComplete(input, completion.*) catch |err| return result(f, .{}, err);
    target.* = selection.providerAvailability(input.*, completion.completed_ms);
    return success(f);
}
pub export fn rm_metric_discovery_provider_capture(state: ?*const selection.ProviderState, now_ms: i64, availability: ?*u32, f: ?*p.Failure) u32 {
    const input = state orelse return invalid(f);
    const target = availability orelse return invalid(f);
    if (input.reserved != 0 or input.incarnation == 0 or input.capability == 0 or input.unsupported > 1 or now_ms < 0) return invalid(f);
    target.* = selection.providerAvailability(input.*, now_ms);
    return success(f);
}
pub fn exportAnchor() void {
    @import("discovery_runtime.zig").exportAnchor();
    _ = &rm_metric_discovery_catalog_size;
    _ = &rm_metric_discovery_compile;
    _ = &rm_metric_discovery_selection_size;
    _ = &rm_metric_discovery_select;
    _ = &rm_metric_discovery_inventory_size;
    _ = &rm_metric_discovery_inventory;
    _ = &rm_metric_discovery_provider_begin;
    _ = &rm_metric_discovery_provider_complete;
    _ = &rm_metric_discovery_observe_size;
    _ = &rm_metric_discovery_observe;
    _ = &rm_metric_discovery_inventory_catalog_size;
    _ = &rm_metric_discovery_inventory_catalog;
    _ = &rm_metric_discovery_provider_capture;
}
