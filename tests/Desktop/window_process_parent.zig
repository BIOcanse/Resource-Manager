//! A real resident parent whose abrupt termination must close its child's Job.
const std = @import("std");
const wp = @import("window_process");
const w = wp.win32;
const Host = struct {
    output: w.HANDLE,
    fn event(context: ?*anyopaque, value: wp.Event) void {
        const self: *Host = @ptrCast(@alignCast(context.?));
        switch (value) {
            .window_handle => |pid| {
                var message: [128]u8 = undefined;
                wp.writeAll(self.output, std.fmt.bufPrint(&message, "{{\"type\":\"window-handle\",\"handle\":\"{d}\"}}\n", .{pid}) catch unreachable) catch {};
            },
            else => {},
        }
    }
};
pub fn main(init: std.process.Init.Minimal) !void {
    var storage: [64 * 1024]u8 = undefined;
    var allocator = std.heap.FixedBufferAllocator.init(&storage);
    var args = try std.process.Args.Iterator.initAllocator(init.args, allocator.allocator());
    defer args.deinit();
    var output: ?w.HANDLE = null;
    var input: ?w.HANDLE = null;
    var executable: ?[]const u8 = null;
    _ = args.next();
    while (args.next()) |arg| {
        if (std.mem.eql(u8, arg, "--resident-pipe-read")) input = @ptrFromInt(try std.fmt.parseInt(usize, args.next() orelse return error.MissingArgument, 10));
        if (std.mem.eql(u8, arg, "--resident-pipe-write")) output = @ptrFromInt(try std.fmt.parseInt(usize, args.next() orelse return error.MissingArgument, 10));
        if (std.mem.eql(u8, arg, "--child-path")) executable = args.next() orelse return error.MissingArgument;
    }
    const parent_input = input orelse return error.MissingPipe;
    defer w.close(parent_input);
    var host: Host = .{ .output = output orelse return error.MissingPipe };
    defer w.close(host.output);
    const stop = w.CreateEventW(null, 1, 0, null) orelse return error.Event;
    defer w.close(stop);
    const cancel = w.CreateEventW(null, 1, 0, null) orelse return error.Event;
    defer w.close(cancel);
    var client: wp.Client = .{};
    try client.run(stop, &host, Host.event, .{
        .executable = executable orelse return error.MissingChild,
        .arguments = "--mode ignore-stop",
        .cancel_poll_ms = 10,
        .stop_deadline_ms = 1000,
        .timeout_policy = .wait,
        .stop_wait_cancel = cancel,
    });
}
