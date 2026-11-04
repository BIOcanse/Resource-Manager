const std = @import("std");
const native = @import("owner.zig");
const host = @import("../../telemetry/kernel_etw/host_allocator.zig");
const errors = @import("errors.zig");
fn owner(handle: ?*anyopaque) ?*native.Owner {
    return @ptrCast(@alignCast(handle));
}
fn status(err: anyerror) i32 {
    return errors.describe(err).status;
}
pub export fn rm_freeze_create(path: ?[*]const u8, length: usize, callbacks: ?*const host.Callbacks, scratch: ?*const native.ScratchCallbacks, changed: ?native.Changed, context: ?*anyopaque, out: ?*?*anyopaque) callconv(.c) i32 {
    const result = out orelse return 400;
    result.* = null;
    result.* = native.Owner.createScoped((path orelse return 400)[0..length], if (callbacks) |c| c.* else @import("private_allocator.zig").callbacks, if (scratch) |s| s.* else null, changed orelse return 400, context) catch |err| return status(err);
    return 0;
}
pub export fn rm_freeze_destroy(handle: ?*anyopaque) callconv(.c) void {
    if (owner(handle)) |self| self.destroy();
}
pub export fn rm_freeze_start_events(handle: ?*anyopaque) callconv(.c) i32 {
    (owner(handle) orelse return 400).startEvents() catch |err| return status(err);
    return 0;
}
pub export fn rm_freeze_stop_events(handle: ?*anyopaque) callconv(.c) i32 {
    (owner(handle) orelse return 400).stopEvents();
    return 0;
}
pub export fn rm_freeze_start_recorder(handle: ?*anyopaque) callconv(.c) i32 {
    (owner(handle) orelse return 400).startRecorder() catch |err| return status(err);
    return 0;
}
pub export fn rm_freeze_stop_recorder(handle: ?*anyopaque) callconv(.c) i32 {
    (owner(handle) orelse return 400).stopRecorder();
    return 0;
}
pub export fn rm_freeze_submit(handle: ?*anyopaque, input: ?[*]const u8, length: usize) callconv(.c) i32 {
    (owner(handle) orelse return 400).submit((input orelse return 400)[0..length]) catch |err| return status(err);
    return 0;
}
pub export fn rm_freeze_setting(handle: ?*anyopaque, key: u32) callconv(.c) f64 {
    return (owner(handle) orelse return -1).setting(key);
}
pub export fn rm_freeze_commit_threshold(handle: ?*anyopaque, threshold: f64) callconv(.c) i32 {
    (owner(handle) orelse return 400).setThreshold(threshold) catch |err| return status(err);
    return 0;
}
/// Lifecycle failures use the same synchronous, native-owned JSON as requests.
/// A status code alone cannot preserve the reason for a failed run.
pub export fn rm_freeze_lifecycle_with_error(handle: ?*anyopaque, operation: u32, threshold: f64, error_write: ?errors.Write, context: ?*anyopaque) callconv(.c) i32 {
    const self = owner(handle) orelse return requestFailure(error.InvalidOperation, error_write, context);
    switch (operation) {
        0 => self.startEvents() catch |err| return requestFailure(err, error_write, context),
        1 => self.startRecorder() catch |err| return requestFailure(err, error_write, context),
        2 => self.setThreshold(threshold) catch |err| return requestFailure(err, error_write, context),
        3 => self.stopEvents(),
        4 => self.stopRecorder(),
        else => return requestFailure(error.InvalidOperation, error_write, context),
    }
    return 0;
}
pub export fn rm_freeze_process_frame(handle: ?*anyopaque, at: i64, available: u32, count: usize, reader: ?native.ProcessReader, context: ?*anyopaque) callconv(.c) i32 {
    (owner(handle) orelse return 400).processFrame(at, available != 0, count, reader orelse return 400, context) catch |err| return status(err);
    return 0;
}
pub export fn rm_freeze_request(handle: ?*anyopaque, op: u32, input: ?[*]const u8, length: usize, write: ?*const fn (?*anyopaque, [*]const u8, usize) callconv(.c) i32, context: ?*anyopaque) callconv(.c) i32 {
    return rm_freeze_request_with_error(handle, op, input, length, write, null, context);
}
/// Separate success/error streams preserve the original failure even after a
/// listing emitted a prefix. The host discards all success bytes on failure.
pub export fn rm_freeze_request_with_error(handle: ?*anyopaque, op: u32, input: ?[*]const u8, length: usize, write: ?errors.Write, error_write: ?errors.Write, context: ?*anyopaque) callconv(.c) i32 {
    const self = owner(handle) orelse return requestFailure(error.InvalidOperation, error_write, context);
    const output = write orelse return requestFailure(error.InvalidOperation, error_write, context);
    if (input == null and length != 0) return requestFailure(error.InvalidOperation, error_write, context);
    self.request(op, if (input) |ptr| ptr[0..length] else "", .{ .context = context, .write = output }) catch |err| return requestFailure(err, error_write, context);
    return 0;
}
fn requestFailure(err: anyerror, write: ?errors.Write, context: ?*anyopaque) i32 {
    const failure = errors.describe(err);
    if (write) |callback| _ = errors.emit(failure, callback, context);
    return failure.status;
}
/// Integer-only ABI callers can ask native for the same bounded error shape.
pub export fn rm_freeze_error_response(code: i32, write: ?errors.Write, context: ?*anyopaque) callconv(.c) i32 {
    const failure = errors.fromStatus(code);
    if (write) |callback| _ = errors.emit(failure, callback, context);
    return failure.status;
}

test {
    _ = @import("abi_error_tests.zig");
}
