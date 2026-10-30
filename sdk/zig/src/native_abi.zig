const resource_scheduler = @import("resource_scheduler/abi.zig");
const private_resource = @import("private_resource/abi.zig");
const local_resource_manager = @import("local_resource_manager/abi.zig");

comptime {
    _ = resource_scheduler;
    _ = private_resource;
    _ = local_resource_manager;
}

test {
    _ = resource_scheduler;
    _ = private_resource;
    _ = local_resource_manager;
}
