//! The complete clear command: restore, mutate/save/commit, then publish once.
//! The platform/source adapters borrow the existing owner; no second current.
const items = @import("../../monitoring/kernel_leak/items.zig");
pub const Action = *const fn (?*anyopaque) callconv(.c) i32;
pub const Mutate = *const fn (?*anyopaque, *i32) callconv(.c) i32;
pub const Owner = extern struct { context: ?*anyopaque, restore: Action, mutate: Mutate, publish: Action };
pub const Save = *const fn (?*anyopaque) callconv(.c) i32;
pub const Error = error{ Invalid, CallbackFailed };

pub fn invoke(owner: Owner) Error!i32 {
    if (owner.restore(owner.context) != 0) return error.CallbackFailed;
    var count: i32 = 0;
    if (owner.mutate(owner.context, &count) != 0) return error.CallbackFailed;
    if (count < 0) return error.Invalid;
    if (owner.publish(owner.context) != 0) return error.CallbackFailed;
    return count;
}

/// Runs inside the store's existing serialized, locally leased region. The
/// save callback only projects/persists pending rows. A save failure leaves the
/// committed rows untouched; the next command replaces the abandoned plan.
pub fn clear(store: *items.Store, context: ?*anyopaque, save: Save) !i32 {
    const before = (try store.snapshot(false)).len;
    const plan = try store.clearHistory();
    if (plan.changed != 0 and save(context) != 0) return error.CallbackFailed;
    try store.commit();
    return @intCast(before - (try store.snapshot(false)).len);
}

pub export fn rm_kernel_history_invoke(owner: ?*const Owner, count: ?*i32) callconv(.c) i32 {
    const result = count orelse return 1;
    result.* = 0;
    result.* = invoke((owner orelse return 1).*) catch return 2;
    return 0;
}
pub export fn rm_kernel_history_clear(store: ?*items.Store, context: ?*anyopaque, save: ?Save, count: ?*i32) callconv(.c) i32 {
    const result = count orelse return 1;
    result.* = 0;
    result.* = clear(store orelse return 1, context, save orelse return 1) catch return 2;
    return 0;
}
