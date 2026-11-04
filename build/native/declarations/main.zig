const std = @import("std");
const state = @import("state_items.zig");
const frontend = @import("frontend_defaults.zig");

pub fn main(init: std.process.Init) void {
    var diagnostic: state.Diagnostic = .{};
    defer diagnostic.deinit(init.arena.allocator());
    run(init, &diagnostic) catch |err| {
        var buffer: [1024]u8 = undefined;
        var stderr = std.Io.File.stderr().writer(init.io, &buffer);
        stderr.interface.print("declaration generation: {s}; entry={any} field={s}: {s}\n", .{ @errorName(err), diagnostic.entry, diagnostic.field, diagnostic.reason }) catch {};
        stderr.interface.flush() catch {};
        diagnostic.deinit(init.arena.allocator());
        std.process.exit(1);
    };
}

fn run(init: std.process.Init, diagnostic: *state.Diagnostic) !void {
    const allocator = init.arena.allocator();
    const args = try init.minimal.args.toSlice(allocator);
    // Explicit resource limit belongs to the invoking build, not a second product policy.
    if (args.len < 2) return error.ExpectedCommand;
    const states = std.mem.eql(u8, args[1], "state-items");
    const defaults = std.mem.eql(u8, args[1], "frontend-defaults");
    if ((!states and !defaults) or args.len != (if (states) @as(usize, 7) else 5)) return error.ExpectedCommandInputOutputLimitAndStateProcessNames;
    const maximum = try std.fmt.parseInt(usize, args[4], 10);
    if (maximum == 0 or maximum == std.math.maxInt(usize)) return error.InvalidInputLimit;
    if (std.mem.eql(u8, args[2], args[3])) return error.InputEqualsOutput;
    const cwd = std.Io.Dir.cwd();
    // readFileAlloc rejects a stream that reaches its limit before probing EOF.
    // Reserve one byte for that probe while keeping the CLI limit inclusive.
    const bytes = try cwd.readFileAlloc(init.io, args[2], allocator, .limited(maximum + 1));
    if (bytes.len > maximum) return error.StreamTooLong;
    var output: std.Io.Writer.Allocating = .init(allocator);
    defer output.deinit();
    if (states) {
        const names = if (std.mem.eql(u8, args[6], "names")) true else if (std.mem.eql(u8, args[6], "no-names")) false else return error.ExpectedNamesOrNoNames;
        var registry = try state.read(allocator, bytes, diagnostic);
        defer registry.deinit();
        try state.emit(registry, args[5], names, &output.writer);
    } else try frontend.emit(allocator, bytes, &output.writer);
    // Validate and produce fully before touching the old output; failed input
    // leaves the prior file intact. Identical output preserves mtime.
    const generated = output.written();
    const existing = cwd.readFileAlloc(init.io, args[3], allocator, .limited(generated.len + 1)) catch |err| switch (err) {
        error.FileNotFound, error.StreamTooLong => null,
        else => return err,
    };
    if (existing) |old| if (std.mem.eql(u8, old, generated)) return;
    if (std.fs.path.dirname(args[3])) |directory| try cwd.createDirPath(init.io, directory);
    var file = try cwd.createFileAtomic(init.io, args[3], .{ .replace = true });
    defer file.deinit(init.io);
    try file.file.writeStreamingAll(init.io, generated);
    try file.file.sync(init.io);
    try file.replace(init.io);
}
