const state = @import("state.zig");
pub export fn rm_dependency_cache_initialize(region: ?[*]align(8) u8, length: usize, incarnation: u64, statuses: u64, providers: u64) callconv(.c) u32 {
    const bytes = region orelse return 2;
    return if (state.initialize(bytes[0..length], incarnation, statuses, providers)) 0 else 2;
}
pub export fn rm_dependency_cache_execute(region: ?[*]align(8) u8, length: usize, operation: u32, kind: u32, unicode_profile: u32, key: ?[*]const u16, key_length: u32, source: u64, incarnation: u64, generation: u64, projection: u64, result: ?*state.Result) callconv(.c) u32 {
    const output = result orelse return 2;
    output.* = .{};
    if (unicode_profile > 2) return 2;
    const bytes = region orelse return 2;
    const text: []const u16 = if (key) |pointer| pointer[0..key_length] else if (key_length == 0) &.{} else return 2;
    return state.execute(bytes[0..length], operation, kind, unicode_profile, text, source, incarnation, generation, projection, output);
}
