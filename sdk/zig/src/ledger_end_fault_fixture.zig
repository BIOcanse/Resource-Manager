//! Test-only Native consumer root. Production entries import ledger.zig instead.
pub const uid = @import("local_resource_manager/uid.zig");
pub const types = @import("local_resource_manager/owned/types.zig");
pub const native = @import("local_resource_manager/owned/native.zig");
pub const fault = @import("local_resource_manager/owned/end_failure_pages.zig");

pub export fn rm_ledger_test_fail_release(count: u32) callconv(.c) void {
    fault.failures = count;
}
pub export fn rm_ledger_test_release_attempts() callconv(.c) u64 {
    return fault.attempts;
}
pub fn exportFaultControls() void {
    _ = &rm_ledger_test_fail_release;
    _ = &rm_ledger_test_release_attempts;
}
