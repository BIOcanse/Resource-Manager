//! Pending registration in the EXISTING NativeCore ABI by maintenance. No DLL,
//! allocator, global registry or fallback is introduced by this leaf.
//!
//! Joining: register these four exports in abi.zig, then adapt the original
//! selected-source slot under its existing commit gate. Its context carries the
//! full service/connection/T/W identity; Publisher below is only business data,
//! never that selection proof. R lends an already validated stable Frame and
//! Windows span for this synchronous call and rechecks selection around reads.
//! commit(context) returns 1 only AFTER that same selection's trusted T/value
//! transition. retire(context) reports a committed invalidation even if mapping
//! drainage is still pending. notify(context) wakes the existing scoped signal
//! after either transition; subscribe before the initial read. Neither this
//! module nor the CLR bridge owns registration, mappings or subscriber lifetime.
const o = @import("owner.zig");
const r = @import("read.zig");
const m = o.model;
pub const owner = o;
pub const read = r;
pub const model = m;

pub export fn rm_desktop_observation_validate(state: *const o.State, selected: *const m.Publisher, publisher_verified: u32, frame: *const m.Frame, windows: *const m.Windows, now_utc_ticks: i64, limits: *const m.Limits) callconv(.c) u32 {
    return @intFromBool(o.validate(state.*, selected.*, publisher_verified == 1, frame, windows.*, now_utc_ticks, limits.*));
}

pub export fn rm_desktop_observation_publish(state: *o.State, selected: *const m.Publisher, publisher_verified: u32, frame: *const m.Frame, windows: *const m.Windows, now_utc_ticks: i64, limits: *const m.Limits, context: ?*anyopaque, commit: o.Commit, notify: o.Notify) callconv(.c) u32 {
    return @intFromBool(o.publish(state, selected.*, publisher_verified == 1, frame, windows.*, now_utc_ticks, limits.*, context, commit, notify));
}

pub export fn rm_desktop_observation_end(context: ?*anyopaque, retire: o.Commit, notify: o.Notify) callconv(.c) u32 {
    return @intFromBool(o.end(context, retire, notify));
}

pub export fn rm_desktop_observation_read(process: *const r.Process, frame: ?*const m.Frame, windows: *const m.Windows, now_utc_ticks: i64, output: *r.Fact) callconv(.c) void {
    output.* = r.current(process.*, frame, windows.*, now_utc_ticks);
}
