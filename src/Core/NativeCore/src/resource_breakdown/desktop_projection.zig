const std = @import("std");

// D-c04/I preparation only: no Store, sampling, publication, locks or allocator.
// The caller borrows verified inputs for this synchronous call. Returned facts
// contain only values; neither a source view nor a lease escapes the call.
// Wire/ABI registration waits for S's frozen scoped ReadCurrent contract.
pub const RuntimeState = enum {
    foreground_focused,
    foreground_unfocused,
    background_window,
    background_process,
};

pub const WindowPresence = struct {
    has_visible_window: bool,
    has_background_window: bool,
};

pub const Facts = struct {
    runtime_state: RuntimeState,
    foreground_focused: bool,
    has_visible_window: bool,
    has_background_window: bool,
};

// Equivalent to WindowsProcessRuntimeStateSnapshot.Project for one instance.
// A present row with both flags false is still observed window ownership.
// Unknown foreground / reused instance rejection remains at the source read
// boundary, as in Store.Read; this function does not broaden that contract.
pub fn project(
    verified_instance: bool,
    foreground: bool,
    window: ?WindowPresence,
    ownership_incomplete: bool,
) ?Facts {
    if (!verified_instance) return null;
    if (ownership_incomplete and !foreground and window == null) return null;

    const visible = foreground or (if (window) |row| row.has_visible_window else false);
    const background = if (window) |row| row.has_background_window else false;
    return .{
        .runtime_state = if (foreground)
            .foreground_focused
        else if (visible)
            .foreground_unfocused
        else if (background)
            .background_window
        else
            .background_process,
        .foreground_focused = foreground,
        .has_visible_window = visible,
        .has_background_window = background,
    };
}

// Compare the ORIGINAL source owner's complete value tuple, not a second
// epoch or a subset such as sequence/observedAt/PID. Identity is deliberately
// generic: S owns its eventual type/format. Pass null for loss, unavailable,
// invalid selection or failed stable read. Identity must be a stable value,
// not a pointer/slice into mutable source memory.
// The current value must be read inside HostedPublication's original commit
// gate; a comparison made earlier (e.g. under sampleGate) is insufficient.
pub fn samePublication(comptime Identity: type, captured: ?Identity, current: ?Identity) bool {
    const previous = captured orelse return false;
    const selected = current orelse return false;
    return std.meta.eql(previous, selected);
}

pub const Reconciliation = union(enum) {
    unaffected,
    recompute,
    replace: ?Facts,
};

// Only decide the DESKTOP fields of a process already proved to belong to
// this session by the existing process identity source. The caller retains
// all process rows, other sessions and non-desktop fields. A stale capture
// with a lost source clears this session's desktop fields. A changed, valid
// source requires recomputation in the original gate: neither the stale facts
// nor an empty replacement may overwrite the newer desktop publication.
// No scheduling/publication status is invented here.
pub fn reconcile(
    comptime Identity: type,
    process_session: u32,
    source_session: u32,
    captured: ?Identity,
    current: ?Identity,
    captured_facts: ?Facts,
) Reconciliation {
    if (process_session != source_session) return .unaffected;
    if (current == null) return .{ .replace = null };
    if (!samePublication(Identity, captured, current)) return .recompute;
    return .{ .replace = captured_facts };
}
