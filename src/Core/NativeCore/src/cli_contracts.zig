//! CLI contract imports share the NativeCore source root so domain contracts
//! can retain their own relative imports. No capability implementation is linked.
pub const crash_reports = @import("interaction/contracts/crash_reports.zig");
