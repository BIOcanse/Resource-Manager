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
    const runtime = @import("interaction/http/runtime_abi.zig");
    _ = runtime.rm_http_runtime_borrow;
    _ = runtime.rm_http_runtime_path;
    _ = runtime.rm_http_runtime_minimum_window;
}
