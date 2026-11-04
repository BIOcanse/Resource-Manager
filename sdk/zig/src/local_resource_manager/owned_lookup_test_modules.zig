//! Explicit module surface for generated/local owned-ledger tests.
//! This file is added as a module entry only; existing SDK sources and
//! producer inputs do not import it.
pub const uid = @import("uid.zig");
pub const types = @import("owned/types.zig");
pub const notifications = @import("owned/notifications.zig");
pub const owner = @import("owned/owner.zig");
pub const index = @import("owned/index.zig");
pub const index_b2 = @import("owned/index_b2.zig");
pub const memory_resource = @import("memory_resource.zig");
