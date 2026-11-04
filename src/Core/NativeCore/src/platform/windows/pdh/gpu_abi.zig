const gpu = @import("gpu_owner.zig");
const query = @import("shared_query.zig");

pub export fn rm_pdh_gpu_shared_query(host: ?*const query.Host, request: ?*const query.Request) callconv(.c) u32 {
    return query.read(host orelse return 1, request orelse return 1);
}
pub export fn rm_pdh_gpu_shared_query_windows(host: ?*const query.Host, request: ?*const query.Request) callconv(.c) u32 {
    var windows_host = (host orelse return 1).*;
    query.windows(&windows_host);
    return query.read(&windows_host, request orelse return 1);
}

pub export fn rm_pdh_gpu_workspace(input: ?*const gpu.Input) callconv(.c) usize {
    const value = input orelse return 0;
    if (value.size != @sizeOf(gpu.Input) or value.version != 1) return 0;
    return gpu.required(value) orelse 0;
}
pub export fn rm_pdh_gpu_project(input: ?*const gpu.Input, workspace: ?[*]u8, length: usize,
    maximum: usize, output: ?*gpu.Output) callconv(.c) u32 {
    const result = output orelse return 1;
    result.* = .{};
    if (length > maximum or maximum == 0) return 1;
    return gpu.project(input orelse return 1, (workspace orelse return 1)[0..length], result);
}
pub export fn rm_pdh_gpu_parse(name: gpu.Name, process: u32, pid: ?*u32, luid: ?*u64) callconv(.c) u32 {
    if (process > 1) return 0;
    return @intFromBool(gpu.parse(name, process == 1, pid orelse return 0, luid orelse return 0));
}
pub export fn rm_pdh_gpu_shared(values: ?[*]const gpu.Formatted, count: usize, requested: ?[*]const u64,
    requested_count: usize, process: u32, rows: ?[*]gpu.SharedRow, capacity: usize, result: ?*gpu.SharedResult) callconv(.c) u32 {
    const output = result orelse return 1;
    output.* = .{};
    if (process > 1 or (count > 0 and values == null) or (requested_count > 0 and requested == null) or (capacity > 0 and rows == null)) return 1;
    return gpu.shared(if (values) |p| p[0..count] else &.{}, if (requested) |p| p[0..requested_count] else &.{},
        process == 1, if (rows) |p| p[0..capacity] else &.{}, output);
}
pub export fn rm_pdh_gpu_decode(buffer: ?[*]const u8, allocated: u32, returned: u32, count: u32,
    limits: gpu.DecoderLimits, rows: ?[*]gpu.Formatted, capacity: usize) callconv(.c) u32 {
    if (capacity > 0 and rows == null) return 0;
    return @intFromBool(gpu.decode(buffer, allocated, returned, count, limits, if (rows) |p| p[0..capacity] else &.{}));
}
