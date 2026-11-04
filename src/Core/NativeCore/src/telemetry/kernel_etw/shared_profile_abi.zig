const std = @import("std");
const shared = @import("shared_owner.zig");
const profile = @import("publication_profile.zig");
const Result = @import("../../common/result_codes.zig").ResultCode;
fn code(value: Result) i32 {
    return @intFromEnum(value);
}
fn owner(handle: ?*anyopaque) ?*shared.Owner {
    return @ptrCast(@alignCast(handle));
}
/// Diagnostic opt-in before processing. No publication policy is changed.
pub export fn rm_kernel_etw_shared_profile_enable(handle: ?*anyopaque, enabled: u32) callconv(.c) i32 {
    const self = owner(handle) orelse return code(.invalid_argument);
    if (enabled > 1 or self.published_version != 0) return code(.invalid_argument);
    if (self.trace) |t| if (t.consumer.processing.load(.acquire) or t.consumer.counters.records != 0) return code(.unavailable);
    self.profile.configure(enabled == 1) catch return code(.unavailable);
    if (self.has(1)) self.capture.residency.profile = if (enabled == 1) &self.profile else null;
    if (self.host_callbacks == null) {
        self.region.profile_enabled = enabled == 1;
        self.region.profile_stats = .{};
    }
    return code(.ok);
}
/// Caller joins the flow and retains its normal owner read lease before query.
/// The processing check also rejects accidental live reads; it is not a lease.
/// Region operation statistics apply only to legacy contiguous-region owners.
/// Host-allocated owners report zero in that field; their Ledger accounting is
/// exposed by the regular owner/region usage interfaces.
pub export fn rm_kernel_etw_shared_profile_query(handle: ?*anyopaque, output: ?*profile.View, size: u32) callconv(.c) i32 {
    const self = owner(handle) orelse return code(.invalid_argument);
    const out = output orelse return code(.invalid_argument);
    if (size != @sizeOf(profile.View)) return code(.abi_mismatch);
    if (self.trace) |t| if (t.consumer.processing.load(.acquire)) return code(.unavailable);
    out.* = self.profile.view;
    out.region = if (self.host_callbacks == null) self.region.profile_stats else .{};
    return code(.ok);
}

/// Joined-flow CPU detail; the existing 344-byte phase view stays unchanged.
pub export fn rm_kernel_etw_shared_profile_query_cpu_detail(handle: ?*anyopaque, output: ?*profile.CpuDetail, size: u32) callconv(.c) i32 {
    const self = owner(handle) orelse return code(.invalid_argument);
    const out = output orelse return code(.invalid_argument);
    if (size != @sizeOf(profile.CpuDetail)) return code(.abi_mismatch);
    if (self.trace) |t| if (t.consumer.processing.load(.acquire)) return code(.unavailable);
    out.* = self.profile.cpu_detail;
    return code(.ok);
}
