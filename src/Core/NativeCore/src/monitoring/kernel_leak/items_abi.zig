const items = @import("items.zig");

fn status(err: items.Error) c_int {
    return switch (err) {
        error.InvalidInput => 1,
        error.CapacityExceeded => 2,
        error.BufferTooSmall => 3,
    };
}
fn view(comptime T: type, pointer: ?[*]const T, count: usize) ?[]const T {
    if (count == 0) return &.{};
    return if (pointer) |p| p[0..count] else null;
}
pub export fn rm_kernel_leak_items_size(maximum: u32) callconv(.c) usize {
    return items.requiredBytes(maximum);
}
pub export fn rm_kernel_leak_item_metadata_valid(metadata: ?*const items.Metadata) callconv(.c) u32 {
    return if (metadata) |value| @intFromBool(items.validMetadata(value.*)) else 0;
}
pub export fn rm_kernel_leak_items_init(region: ?[*]align(8) u8, length: usize, maximum: u32) callconv(.c) c_int {
    const memory = region orelse return 1;
    _ = items.init(memory[0..length], maximum) catch |err| return status(err);
    return 0;
}
pub export fn rm_kernel_leak_items_reserve(region: ?*items.Store, length: usize) callconv(.c) c_int {
    const store = region orelse return 1;
    store.reserve(length) catch |err| return status(err);
    return 0;
}
pub export fn rm_kernel_leak_items_restore(region: ?*items.Store, rows: ?[*]const items.Row, descriptions: ?[*]const items.Metadata, count: usize, required_bytes: ?*usize) callconv(.c) c_int {
    const required = required_bytes orelse return 1;
    required.* = 0;
    const store = region orelse return 1;
    store.restore(view(items.Row, rows, count) orelse return 1, view(items.Metadata, descriptions, count) orelse return 1) catch |err| {
        required.* = store.required_arena;
        return status(err);
    };
    return 0;
}
pub export fn rm_kernel_leak_items_plan(region: ?*items.Store, ticks: i64, live: ?[*]const items.Key, live_count: usize, unavailable: ?[*]const items.Key, unavailable_count: usize, judged: ?[*]const items.Key, judged_count: usize, suspects: ?[*]const items.Row, descriptions: ?[*]const items.Metadata, suspect_count: usize, capacity: u32, output: ?*items.PlanResult) callconv(.c) c_int {
    const result = output orelse return 1;
    result.* = .{};
    const store = region orelse return 1;
    result.* = store.plan(ticks, view(items.Key, live, live_count) orelse return 1, view(items.Key, unavailable, unavailable_count) orelse return 1, view(items.Key, judged, judged_count) orelse return 1, view(items.Row, suspects, suspect_count) orelse return 1, view(items.Metadata, descriptions, suspect_count) orelse return 1, capacity) catch |err| {
        result.required_bytes = store.required_arena;
        return status(err);
    };
    return 0;
}
pub export fn rm_kernel_leak_items_clear(region: ?*items.Store, output: ?*items.PlanResult) callconv(.c) c_int {
    const result = output orelse return 1;
    result.* = .{};
    const store = region orelse return 1;
    result.* = store.clearHistory() catch |err| return status(err);
    return 0;
}
pub export fn rm_kernel_leak_items_snapshot(region: ?*items.Store, pending: u32, rows: ?*?[*]const items.Row, count: ?*usize) callconv(.c) c_int {
    const output = rows orelse return 1;
    const length = count orelse return 1;
    output.* = null;
    length.* = 0;
    const store = region orelse return 1;
    const snapshot = store.snapshot(pending != 0) catch |err| return status(err);
    output.* = snapshot.ptr;
    length.* = snapshot.len;
    return 0;
}
pub export fn rm_kernel_leak_items_commit(region: ?*items.Store) callconv(.c) c_int {
    const store = region orelse return 1;
    store.commit() catch |err| return status(err);
    return 0;
}
pub export fn rm_kernel_leak_items_metadata(region: ?*items.Store, pending: u32, index: usize, output: ?*items.Metadata) callconv(.c) c_int {
    const result = output orelse return 1;
    const store = region orelse return 1;
    result.* = store.metadata(pending != 0, index) catch |err| return status(err);
    return 0;
}
pub export fn rm_kernel_leak_items_service(region: ?*items.Store, pending: u32, index: usize, name_index: usize, output: ?*items.Text) callconv(.c) c_int {
    const result = output orelse return 1;
    const store = region orelse return 1;
    result.* = store.service(pending != 0, index, name_index) catch |err| return status(err);
    return 0;
}
