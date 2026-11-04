//! Demand-driven IO binding for capability admission, not an access authority.
//! The sole access owner checks connection/Host/Origin/actual peer first, then
//! either supplies its final decision or requests the live ordinary-action value
//! for that same admission. No route table, settings snapshot, defaults or cache.
//!
//! prepare/readOrdinaryActions/completeAdmission are synchronous scoped calls. Admission may
//! borrow the access owner's proved caller while admit runs; nothing is retained
//! here. Async preparation belongs to dispatch, not a second request lifecycle.
//! Integration is pending the native dispatch/C15 owner (absent on this base).

/// Decision and Admission belong to the access owner. The latter preserves its
/// exact operation/selector and caller proof between preparation and resumption.
pub fn Preparation(comptime Decision: type, comptime Admission: type) type {
    return union(enum) {
        decided: Decision,
        ordinary_actions: Admission,
    };
}

/// Access owns prepare() and completeAdmission(admission, enabled), including every denial
/// and the final authorization. Policy's readOrdinaryActions() returns just that
/// live committed value through C11/C12. Failures propagate; no fallback allows
/// work, and an independent/denied decision never touches the settings source.
pub fn admit(comptime Access: type, access: *Access, policy: anytype) !Access.Decision {
    return switch (try access.prepare()) {
        .decided => |decision| decision,
        .ordinary_actions => |admission| try access.completeAdmission(admission, try policy.readOrdinaryActions()),
    };
}
