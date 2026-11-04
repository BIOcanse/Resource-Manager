const std = @import("std");
const core = @import("owner.zig");
pub export fn rm_notifications_create(config: *const core.Config, callbacks: *const core.Callbacks) callconv(.c) ?*core.Owner {
    // Only bounded reference/slot metadata lives here. Payloads stay at the typed
    // bridge until their producers migrate; no native JSON/data workspace exists.
    return core.Owner.init(std.heap.smp_allocator, config.*, callbacks.*) catch null;
}
pub export fn rm_notifications_destroy(owner: *core.Owner) callconv(.c) void { owner.deinit(); }
pub export fn rm_notifications_read(owner: *core.Owner, context: ?*anyopaque, persist: u32) callconv(.c) i32 {
    owner.lock(); defer owner.unlock();
    return owner.read(context, persist != 0);
}
pub export fn rm_notifications_post(owner: *core.Owner, context: ?*anyopaque, persist: u32, capacity: usize, flags: u32) callconv(.c) i32 {
    owner.lock(); defer owner.unlock();
    return owner.post(context, persist != 0, capacity, flags);
}
pub export fn rm_notifications_subscribe(owner: *core.Owner, context: ?*anyopaque, persist: u32, reader: usize) callconv(.c) i32 {
    owner.lock(); defer owner.unlock();
    return owner.subscribe(context, persist != 0, reader);
}
pub export fn rm_notifications_unsubscribe(owner: *core.Owner, reader: usize) callconv(.c) void {
    owner.lock(); defer owner.unlock();
    owner.unsubscribe(reader);
}
pub export fn rm_notifications_owned_bytes(owner: *core.Owner) callconv(.c) usize { return owner.ownedBytes(); }
pub export fn rm_notifications_scope(kind: ?[*]const u16, length: usize) callconv(.c) u32 {
    return core.scope((kind orelse return 0)[0..length]);
}
pub export fn rm_notifications_project(context: ?*anyopaque, count: usize, access: u32, callbacks: *const core.Projection) callconv(.c) usize {
    return core.project(context, count, access, callbacks.*);
}
pub export fn rm_notifications_sequences_equal(context: ?*anyopaque, left: usize, right: usize,
    sequence: *const fn (?*anyopaque, u32, usize) callconv(.c) i64) callconv(.c) u32 {
    return @intFromBool(core.sameSequences(context, left, right, sequence));
}
