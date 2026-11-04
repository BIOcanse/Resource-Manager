//! Kernel gate registration overlay only. Maintenance wires accepted exports.
const common = @import("http_common_product_test_root.zig");
pub const Caller = common.Caller;
pub const Input = common.Input;
pub const Requirement = common.Requirement;
pub const ReadKind = common.ReadKind;
pub const evaluateRead = common.evaluateRead;
pub const RegionReference = common.RegionReference;
pub const SelectionIdentity = common.SelectionIdentity;
pub const region_count = common.region_count;
pub const selectionRegion = common.selectionRegion;
comptime {
    _ = common;
    _ = @import("interaction/capabilities/kernel_history.zig").rm_kernel_history_invoke;
    _ = @import("interaction/capabilities/kernel_history.zig").rm_kernel_history_clear;
    _ = @import("interaction/http/kernel_history.zig").rm_http_kernel_history_borrow;
}
