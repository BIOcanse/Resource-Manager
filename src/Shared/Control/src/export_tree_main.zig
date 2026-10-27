//! Build-only executable. process_tree is a process-owned declaration module.
const std = @import("std");
const control = @import("rm_control");
const source = @import("process_tree");

pub fn main(init: std.process.Init) !void {
    var diagnostic: ?[]const u8 = null;
    var measure = control.json.Writer.init(&.{});
    control.export_tree.write(source.registry, source.process_name, source.profiles, &measure, &diagnostic) catch |err| {
        const stderr = std.Io.File.stderr();
        try stderr.writeStreamingAll(init.io, @errorName(err));
        try stderr.writeStreamingAll(init.io, ": ");
        try stderr.writeStreamingAll(init.io, diagnostic orelse "invalid declaration");
        try stderr.writeStreamingAll(init.io, "\n");
        return err;
    };
    const bytes = try init.gpa.alloc(u8, measure.len);
    defer init.gpa.free(bytes);
    var out = control.json.Writer.init(bytes);
    try control.export_tree.write(source.registry, source.process_name, source.profiles, &out, &diagnostic);
    try std.Io.File.stdout().writeStreamingAll(init.io, bytes);
}
