const bars = @import("current_bars.zig");

// All callbacks and their source handle are borrowed only until this returns.
// No allocation, retained pointer, thread, callback after return, or async work.
pub export fn rm_resource_bars_project(context: ?*anyopaque, callbacks: ?*const bars.Callbacks, request: ?*const bars.Request) callconv(.c) i32 {
    bars.project(context, callbacks orelse return 1, (request orelse return 1).*) catch |err| return switch (err) {
        error.InvalidInput => 1,
        error.CallbackFailed => 2,
        error.DuplicateBar => 3,
        error.DuplicateDataset => 4,
        error.NullKey => 5,
    };
    return 0;
}
