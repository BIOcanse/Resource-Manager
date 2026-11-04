const std = @import("std");
const core = @import("status.zig");
const scalar = @import("scalar_paths.zig");
pub export fn rm_dependency_probe_execute(region: ?[*]u8, length: usize, input_length: u32, operation: u32, result: ?*core.Result) callconv(.c) void {
    const output = result orelse return;
    output.* = .{ .status = 1 };
    const address = region orelse return;
    if (@intFromPtr(address) % @alignOf(core.Request) != 0 or input_length < @sizeOf(core.Request) or input_length > length or length > std.math.maxInt(u32)) return;
    const start = std.mem.alignForward(usize, input_length, 8);
    if (start > length) {
        output.* = .{ .status = 2, .required_bytes = start };
        return;
    }
    var arena = @import("registered_arena.zig").Arena.init(address[start..length]);
    const context: core.Context = .{ .region = address[0..length], .input_length = input_length, .allocator = arena.allocator() };
    const request: *const core.Request = @ptrCast(@alignCast(address));
    if (request.unicode_profile > 2) return;
    @import("ordinal.zig").profile = request.unicode_profile;
    output.* = core.execute(context, request.*, operation) catch |err| .{ .required_bytes = if (err == error.OutOfMemory) std.math.add(u64, start, arena.required) catch std.math.maxInt(u64) else 0, .status = switch (err) {
        error.OutOfMemory => 2,
        error.AccessDenied => 4,
        error.InvalidInput => 1,
        error.ReparsePoint => 5,
        error.HardLink => 6,
        error.EntryBound => 7,
        else => 3,
    } };
}
pub export fn rm_dependency_path_fact(path: ?[*:0]const u16, count: u32, root_count: u32, regular: u32, os_error: ?*u32) callconv(.c) u32 {
    const err = os_error orelse return 1;
    return scalar.check(path orelse return 1, count, root_count, regular != 0, err);
}
comptime {
    if (@sizeOf(core.Request) != 188 or @sizeOf(core.Result) != 104 or @sizeOf(core.Slice) != 8) @compileError("Dependency probe ABI mismatch");
}
