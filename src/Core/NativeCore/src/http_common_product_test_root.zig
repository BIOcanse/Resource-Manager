//! Test-only registration overlay. Maintenance registers accepted exports in
//! abi.zig during its gate train; workers never mutate that shared root.
const canonical_access = @import("interaction/capabilities/access.zig");
pub const Caller = canonical_access.Caller;
pub const Input = canonical_access.Input;
pub const Requirement = canonical_access.Requirement;
pub const ReadKind = canonical_access.ReadKind;
pub const evaluateRead = canonical_access.evaluateRead;
const canonical_plan = @import("data_area/plan.zig");
pub const RegionReference = canonical_plan.RegionReference;
pub const SelectionIdentity = canonical_plan.SelectionIdentity;
pub const region_count = canonical_plan.region_count;
pub const selectionRegion = canonical_plan.selectionRegion;
comptime {
    _ = @import("abi.zig");
    const common = @import("interaction/http/host_abi.zig");
    _ = common.rm_http_common_owns;
    _ = common.rm_http_common_output_bound;
    _ = common.rm_http_common_dispatch;
    _ = common.rm_http_common_family_owns;
    _ = common.rm_http_common_family_capacity;
    _ = common.rm_http_common_family_init;
    _ = common.rm_http_common_family_admit;
    _ = common.rm_http_common_family_start;
    _ = common.rm_http_common_family_step;
    _ = common.rm_http_common_family_release;
    // Mirror the accepted P-access product binding while maintenance owns the
    // shared registration root. No listener fixture may enter this DLL.
    const access = @import("interaction/capabilities/access_abi.zig");
    _ = access.rm_interaction_access_evaluate;
    _ = access.rm_interaction_access_read;
    _ = access.rm_interaction_access_process_read;
    _ = access.rm_interaction_access_gpu_operation;
    // Grant remains a native-boundary test here. No current product consumer
    // exists; product registration awaits that actual owner composition.
    _ = @import("data_area/grant_abi.zig").rm_data_area_grant_execute;
}
