//! A real child process: exchanges only inherited anonymous-pipe handles.
const std = @import("std");
const wp = @import("window_process");
const w = wp.win32;
pub fn main(init: std.process.Init.Minimal) !void {
    var storage: [32 * 1024]u8 = undefined;
    var allocator = std.heap.FixedBufferAllocator.init(&storage);
    var args = try std.process.Args.Iterator.initAllocator(init.args, allocator.allocator());
    defer args.deinit();
    var read: ?wp.HANDLE = null;
    var write: ?wp.HANDLE = null;
    var mode: []const u8 = "close";
    var forbidden: ?wp.HANDLE = null;
    _ = args.next();
    while (args.next()) |arg| {
        if (std.mem.eql(u8, arg, "--resident-pipe-read")) read = @ptrFromInt(try std.fmt.parseInt(usize, args.next() orelse return error.MissingArgument, 10));
        if (std.mem.eql(u8, arg, "--resident-pipe-write")) write = @ptrFromInt(try std.fmt.parseInt(usize, args.next() orelse return error.MissingArgument, 10));
        if (std.mem.eql(u8, arg, "--mode")) mode = args.next() orelse return error.MissingArgument;
        if (std.mem.eql(u8, arg, "--forbidden-handle")) forbidden = @ptrFromInt(try std.fmt.parseInt(usize, args.next() orelse return error.MissingArgument, 10));
    }
    const input = read orelse return error.MissingPipe;
    const output = write orelse return error.MissingPipe;
    defer w.close(input);
    defer w.close(output);
    if (forbidden) |handle| {
        var flags: u32 = 0;
        if (w.GetHandleInformation(handle, &flags) != 0) return error.UnrelatedHandleInherited;
    }
    var line: [wp.protocol.max_line]u8 = undefined;
    var n: usize = 0;
    var got_start = false;
    var got_session = false;
    var got_language = false;
    var got_show = false;
    var json_storage: [96 * 1024]u8 = undefined;
    while (true) {
        var got: u32 = 0;
        var one: [1]u8 = undefined;
        if (w.ReadFile(input, &one, 1, &got, null) == 0 or got == 0) return;
        if (one[0] != '\n') {
            if (n == line.len - 1) return error.LineTooLong;
            line[n] = one[0];
            n += 1;
            continue;
        }
        var arena = std.heap.FixedBufferAllocator.init(&json_storage);
        const parsed = try std.json.parseFromSlice(std.json.Value, arena.allocator(), line[0..n], .{});
        const object = parsed.value.object;
        const kind = object.get("type").?.string;
        if (std.mem.eql(u8, kind, "stop")) {
            // Stop takes priority over initial facts; a run stopped during startup
            // must still drain successfully without waiting for its first show.
            return;
        }
        if (std.mem.eql(u8, kind, "backend-session")) {
            got_session = true;
            const generation = object.get("generation").?.integer;
            if (std.mem.eql(u8, mode, "session") and generation == 22 and !object.get("ready").?.bool)
                try wp.writeAll(output, "{\"type\":\"preferences-changed\",\"scope\":\"frontend\"}\n");
            if (std.mem.eql(u8, mode, "session") and generation == 23 and object.get("ready").?.bool)
                try wp.writeAll(output, "{\"type\":\"preferences-changed\",\"scope\":\"backend\"}\n");
        }
        if (std.mem.eql(u8, kind, "language")) {
            if (!got_session and !got_start) return error.InitialFactsOutOfOrder;
            got_language = true;
        }
        if (std.mem.eql(u8, kind, "show-window")) {
            if (!(got_session and got_language)) return error.InitialFactsOutOfOrder;
            got_show = true;
        }
        if (std.mem.eql(u8, kind, "parent-wants-me-running") and object.get("running").?.bool and !got_start) {
            if (!(got_session and got_language and got_show)) return error.InitialFactsOutOfOrder;
            got_start = true;
            // PID is a distinct, nonzero decimal stand-in for a window handle.
            var response: [128]u8 = undefined;
            try wp.writeAll(output, try std.fmt.bufPrint(&response, "{{\"type\":\"window-handle\",\"handle\":\"{d}\"}}\n", .{w.GetCurrentProcessId()}));
            if (std.mem.eql(u8, mode, "close")) try wp.writeAll(output, "{\"type\":\"user-close\"}\n");
            if (std.mem.eql(u8, mode, "update")) try wp.writeAll(output, "{\"type\":\"applyExit\"}\n");
            if (std.mem.eql(u8, mode, "crash")) w.ExitProcess(42);
            if (std.mem.eql(u8, mode, "malformed")) try wp.writeAll(output, "{\"type\":\"window-handle\",\"handle\":\"bad\"}\n");
            if (std.mem.eql(u8, mode, "oversize")) {
                var oversized: [wp.protocol.max_line + 1]u8 = @splat('x');
                oversized[oversized.len - 1] = '\n';
                try wp.writeAll(output, &oversized);
            }
        }
        n = 0;
    }
}
