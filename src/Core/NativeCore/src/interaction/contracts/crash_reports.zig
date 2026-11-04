//! Protocol-neutral synchronous crash capabilities. Values and target text are
//! borrowed from the original owner for this call; there is no report mirror.
pub const view = @import("../../crash_reports/view.zig");
pub const Operation = enum(u32) { read = 0, package = 1, acknowledge = 2, delete = 3 };
pub const Outcome = enum(u32) { success = 0, not_found = 1 };
// Original selection ABI. value is only a call-local token, never field data.
pub const Result = extern struct {
    operation: Operation,
    outcome: Outcome,
    value: ?*anyopaque,
};
// Complete borrowed result consumed by HTTP, P0 and other native consumers.
// Its accessors/context are valid only inside the source bridge's sync scope.
pub const ViewResult = extern struct {
    operation: Operation,
    outcome: Outcome,
    source: view.Source,
};
pub const Read = *const fn (?*anyopaque, *?*anyopaque) callconv(.c) i32;
pub const WithId = *const fn (?*anyopaque, ?[*]const u16, usize, *?*anyopaque) callconv(.c) i32;
pub const Delete = *const fn (?*anyopaque, ?[*]const u16, usize, *u32) callconv(.c) i32;
pub const Owner = extern struct {
    read: Read,
    package: WithId,
    acknowledge: WithId,
    delete: Delete,
};
// ABI failures are separate from business outcomes. Callback failures must be
// rethrown by the managed bridge, never turned into not-found or success.
pub const Code = struct {
    pub const success: i32 = 0;
    pub const invalid: i32 = 1;
    pub const callback: i32 = 2;
};

pub fn id(operation: Operation) []const u8 {
    return switch (operation) {
        .read => "crash-reports.read",
        .package => "crash-reports.package",
        .acknowledge => "crash-reports.acknowledge",
        .delete => "crash-reports.delete",
    };
}
