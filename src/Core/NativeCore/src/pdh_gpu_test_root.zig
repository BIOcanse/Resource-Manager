const abi = @import("platform/windows/pdh/gpu_abi.zig");
comptime {
    _ = abi.rm_pdh_gpu_workspace;
    _ = abi.rm_pdh_gpu_project;
    _ = abi.rm_pdh_gpu_parse;
    _ = abi.rm_pdh_gpu_shared;
    _ = abi.rm_pdh_gpu_decode;
    _ = abi.rm_pdh_gpu_shared_query;
    _ = abi.rm_pdh_gpu_shared_query_windows;
}
test { _ = @import("platform/windows/pdh/gpu_owner_tests.zig"); }
test { _ = @import("platform/windows/pdh/shared_query_tests.zig"); }
