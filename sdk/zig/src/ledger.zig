//! Independent owned-memory ledger entry for generated consumer builds.
//! Policy values enter through the build's `ledger_values` module.
pub const uid = @import("local_resource_manager/uid.zig");
pub const types = @import("local_resource_manager/owned/types.zig");
pub const native = @import("local_resource_manager/owned/native.zig");
