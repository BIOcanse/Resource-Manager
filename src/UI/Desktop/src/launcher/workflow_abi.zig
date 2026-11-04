pub const workflow = @import("workflow.zig");

// All pointers and callbacks are borrowed only until this function returns.
// The host drains this synchronous call before freeing its callback context.
pub export fn rm_launcher_workflow_start(elevated: u8, restart: u8, callbacks: ?*const workflow.Callbacks) callconv(.c) u32 {
    if (elevated > 1 or restart > 1) return @backingInt(workflow.Result.invalid_arguments);
    const borrowed = callbacks orelse return @backingInt(workflow.Result.invalid_arguments);
    return @backingInt(workflow.start(elevated != 0, restart != 0, borrowed));
}
