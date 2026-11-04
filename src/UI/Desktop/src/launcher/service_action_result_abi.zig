const std = @import("std");
const owner = @import("service_action_result.zig");
pub const Options = owner.Options;
pub const Result = owner.Result;
pub const Bridge = owner.Bridge;

pub export fn rm_launcher_result_run(options: *const Options, bridge: *const Bridge, restart: u32, cancel: ?owner.Handle, result: *Result) callconv(.c) void {
    result.* = owner.run(std.heap.c_allocator, options.*, bridge.*, restart != 0, cancel);
}
pub export fn rm_launcher_result_read_begin(options: *const Options, pipe: owner.Handle, pid: u32, cancel: ?owner.Handle, timeout_ms: u32, result: *Result) callconv(.c) ?*owner.ReadSession {
    return owner.beginRead(std.heap.c_allocator, options.*, pipe, pid, cancel, timeout_ms, result);
}
pub export fn rm_launcher_result_read_finish(session: *owner.ReadSession, result: *Result) callconv(.c) void {
    result.* = owner.finishRead(session);
}
pub export fn rm_launcher_result_read_abort(session: *owner.ReadSession) callconv(.c) void {
    owner.abortRead(session);
}
pub export fn rm_launcher_result_write(options: *const Options, bridge: *const Bridge, name: [*]const u16, name_len: usize, text: [*]const u16, text_len: usize, result: *Result) callconv(.c) void {
    result.* = owner.write(std.heap.c_allocator, options.*, bridge.*, name[0..name_len], text[0..text_len]);
}
pub export fn rm_launcher_result_release(result: *Result) callconv(.c) void {
    result.deinit(std.heap.c_allocator);
}
