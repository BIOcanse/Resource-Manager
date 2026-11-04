//! Original Store publication rules, without sessions/current/publisher maps.
//! State belongs to the original selected-source lifetime. The caller holds
//! its original commit gate across publish/end; no new lock or selection owner.
//! Only last_observed_at is retained, to reject backwards commits. Reinitialize
//! it only when data_area has committed a NEW selection, never on bad input.
const m = @import("model.zig");
pub const model = m;

pub const State = extern struct { last_observed_at_utc_ticks: i64 = 0 };
pub const Commit = *const fn (?*anyopaque) callconv(.c) u32;
/// Original synchronous no-throw publication signal; no managed exception may cross ABI.
pub const Notify = *const fn (?*anyopaque) callconv(.c) void;

/// `selected` is from the original owner, never Frame/body. Platform identity
/// verification remains at the existing caller/OS boundary; it is not policy.
pub fn validate(state: State, selected: m.Publisher, publisher_verified: bool, frame: *const m.Frame, windows: m.Windows, now_utc_ticks: i64, limits: m.Limits) bool {
    if (!publisher_verified or selected.session_id <= 0 or selected.process_id <= 4 or
        selected.creation_file_time_utc <= 0 or !m.samePublisher(selected, frame.publisher) or
        frame.observed_at_utc_ticks <= 0 or frame.observed_at_utc_ticks > now_utc_ticks or
        frame.observed_at_utc_ticks < state.last_observed_at_utc_ticks or
        frame.complete > 1 or frame.foreground_known > 1 or frame.foreground_present > 1 or
        frame.windows_present != 1 or frame.window_count > limits.maximum_windows or !windows.valid(frame.window_count) or
        (frame.foreground_known == 1 and frame.foreground_present == 0)) return false;
    if (frame.foreground_present == 1 and !m.validWindow(frame.foreground)) return false;
    var i: u32 = 0;
    while (i < frame.window_count) : (i += 1) {
        const row = windows.at(i) orelse return false;
        if (!m.validWindow(row)) return false;
        var j: u32 = 0;
        while (j < i) : (j += 1) {
            const previous = windows.at(j) orelse return false;
            if (previous.process_id == row.process_id) return false;
        }
    }
    return true;
}

/// commit must recheck the SAME data_area selection tuple and commit trusted T
/// before returning 1. The stable input/borrow lasts through commit. A refusal
/// leaves State and the notification unchanged; the CONNECTION decides whether
/// invalid input ends its selection. This function never retires on rejection.
pub fn publish(state: *State, selected: m.Publisher, publisher_verified: bool, frame: *const m.Frame, windows: m.Windows, now_utc_ticks: i64, limits: m.Limits, context: ?*anyopaque, commit: Commit, notify: Notify) bool {
    if (!validate(state.*, selected, publisher_verified, frame, windows, now_utc_ticks, limits)) return false;
    if (commit(context) != 1) return false;
    state.last_observed_at_utc_ticks = frame.observed_at_utc_ticks;
    notify(context);
    return true;
}

/// Called after the original connection has stopped admitting work. retire
/// invalidates T and removes ONLY this selection under the original gate; a
/// stale/repeated end returns 0. A committed invalidation still returns 1 when
/// borrows remain: draining is separate from the transition and notification.
/// Drain callbacks/borrows before releasing maps.
pub fn end(context: ?*anyopaque, retire: Commit, notify: Notify) bool {
    if (retire(context) != 1) return false;
    notify(context);
    return true;
}
