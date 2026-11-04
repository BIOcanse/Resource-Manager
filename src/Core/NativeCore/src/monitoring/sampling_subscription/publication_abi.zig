//! Temporary foreign-language binding for the existing C7 consumers.
//! Ordinary Zig consumers import publication.zig directly. Storage is lent by
//! the caller; byte counts/alignment are queried, never duplicated as layouts.
pub const publication = @import("publication.zig");
const p = publication;

pub export fn rm_current_publication_abi_version() callconv(.c) u32 { return 1; }
pub export fn rm_current_publication_signal_size() callconv(.c) usize { return @sizeOf(p.Signal); }
pub export fn rm_current_publication_signal_alignment() callconv(.c) usize { return @alignOf(p.Signal); }
pub export fn rm_current_publication_subscription_size() callconv(.c) usize { return @sizeOf(p.Subscription); }
pub export fn rm_current_publication_subscription_alignment() callconv(.c) usize { return @alignOf(p.Subscription); }

pub export fn rm_current_publication_signal_init(storage: ?*anyopaque, size: usize) callconv(.c) u32 {
    const signal = checked(p.Signal, storage, size) orelse return 1;
    signal.* = .{};
    return 0;
}

pub export fn rm_current_publication_subscribe(owner: ?*anyopaque, storage: ?*anyopaque, size: usize,
    context: ?*anyopaque, notify: ?p.Notify) callconv(.c) u32
{
    const signal = checked(p.Signal, owner, @sizeOf(p.Signal)) orelse return 1;
    const node = checked(p.Subscription, storage, size) orelse return 1;
    const callback = notify orelse return 1;
    // Caller supplies zero-initialized storage on its first use. An active
    // node is rejected without overwriting its links or callback context.
    signal.subscribe(node, context, callback) catch return 1;
    return 0;
}

pub export fn rm_current_publication_unsubscribe(owner: ?*anyopaque, storage: ?*anyopaque) callconv(.c) void {
    const signal = checked(p.Signal, owner, @sizeOf(p.Signal)) orelse return;
    const node = checked(p.Subscription, storage, @sizeOf(p.Subscription)) orelse return;
    signal.unsubscribe(node);
}

pub export fn rm_current_publication_poll(owner: ?*anyopaque, storage: ?*anyopaque) callconv(.c) u32 {
    const signal = checked(p.Signal, owner, @sizeOf(p.Signal)) orelse return @intFromEnum(p.Poll.ended);
    const node = checked(p.Subscription, storage, @sizeOf(p.Subscription)) orelse return @intFromEnum(p.Poll.ended);
    return @intFromEnum(signal.poll(node));
}

pub export fn rm_current_publication_publish(owner: ?*anyopaque) callconv(.c) void {
    const signal = checked(p.Signal, owner, @sizeOf(p.Signal)) orelse return;
    signal.publish();
}

pub export fn rm_current_publication_end(owner: ?*anyopaque) callconv(.c) void {
    const signal = checked(p.Signal, owner, @sizeOf(p.Signal)) orelse return;
    signal.end();
}

pub export fn rm_current_publication_signal_deinit(owner: ?*anyopaque) callconv(.c) void {
    const signal = checked(p.Signal, owner, @sizeOf(p.Signal)) orelse return;
    signal.deinit();
}

fn checked(comptime T: type, storage: ?*anyopaque, size: usize) ?*T {
    const pointer = storage orelse return null;
    if (size != @sizeOf(T) or @intFromPtr(pointer) % @alignOf(T) != 0) return null;
    return @ptrCast(@alignCast(pointer));
}
