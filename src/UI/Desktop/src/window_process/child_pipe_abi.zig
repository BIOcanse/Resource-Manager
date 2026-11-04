//! Leaf exports for the single shared Desktop compatibility DLL. W4 removes
//! the managed binding; this file does not introduce another product build.
const std = @import("std");
const child = @import("child_pipe.zig");
var live_owners: std.atomic.Value(usize) = .init(0);

pub export fn rm_window_child_pipe_workspace_bytes(config: *const child.Config, result: *usize) callconv(.c) c_int {
    result.* = child.workspaceBytes(config.*) catch return -1;
    return 0;
}
pub export fn rm_window_child_pipe_create(config: *const child.Config, io: *const child.Io, context: ?*anyopaque, result: *?*child.Owner) callconv(.c) c_int {
    result.* = null;
    const initial = child.Owner.init(config.*, io.*, context) catch return -1;
    // Small owner only. All variable-capacity storage is the run's Ledger lease.
    const owner = std.heap.c_allocator.create(child.Owner) catch return -1;
    owner.* = initial;
    _ = live_owners.fetchAdd(1, .monotonic);
    result.* = owner;
    return 0;
}
pub export fn rm_window_child_pipe_run(owner: *child.Owner, region: ?[*]u8, length: usize) callconv(.c) c_int {
    const storage = if (region) |p| p[0..length] else if (length == 0) @as([]u8, &.{}) else return -1;
    owner.run(storage) catch {
        owner.io.ready(owner.context, -1);
        return -1;
    };
    return 0;
}
pub export fn rm_window_child_pipe_start(owner: *child.Owner, receiver: *const child.Receiver) callconv(.c) c_int {
    return if (owner.startReader(receiver.*) catch return -1) 0 else 1;
}
pub export fn rm_window_child_pipe_send(owner: *child.Owner, kind: child.Text, field: child.Text, value: child.Text, completion: ?*anyopaque) callconv(.c) c_int {
    return @backingInt(owner.send(kind, field, value, completion));
}
pub export fn rm_window_child_pipe_stop(owner: *child.Owner) callconv(.c) void {
    owner.stop();
}
pub export fn rm_window_child_pipe_destroy(owner: *child.Owner) callconv(.c) c_int {
    owner.mutex.lock();
    if (owner.active) {
        owner.mutex.unlock();
        return -1;
    }
    const close_unstarted = !owner.finished;
    owner.finished = true;
    owner.mutex.unlock();
    if (close_unstarted) owner.io.close(owner.context);
    std.heap.c_allocator.destroy(owner);
    _ = live_owners.fetchSub(1, .monotonic);
    return 0;
}
/// Allocation diagnostic only; does not drive lifecycle or record activity.
pub export fn rm_window_child_pipe_live_owners() callconv(.c) usize {
    return live_owners.load(.monotonic);
}
// The receiver consumes the borrowed decoded value synchronously, just as the
// running owner does. No returned pointer outlives the caller's scratch region.
pub export fn rm_window_child_pipe_decode(input: [*]const u8, length: usize, scratch: [*]u8, scratch_length: usize, max_depth: u32, receiver: *const child.Receiver) callconv(.c) c_int {
    const message = child.decode(input[0..length], scratch[0..scratch_length], max_depth) catch return -1;
    return receiver.receive(receiver.context, &message, 0);
}
pub export fn rm_window_child_pipe_decode_bytes(length: usize, max_depth: u32, result: *usize) callconv(.c) c_int {
    result.* = child.parseBytes(length, max_depth) catch return -1;
    return 0;
}
