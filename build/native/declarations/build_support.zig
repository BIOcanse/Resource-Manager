//! Shared native generation graph used by the bootstrap and product build patches.
const std = @import("std");

pub fn tool(b: *std.Build, source: std.Build.LazyPath) *std.Build.Step.Compile {
    return b.addExecutable(.{
        .name = "rm-declarations",
        .root_module = b.createModule(.{ .root_source_file = source, .target = b.graph.host, .optimize = .safe }),
    });
}

pub fn stateItems(b: *std.Build, generator: *std.Build.Step.Compile, registry: std.Build.LazyPath, process: []const u8, names: bool, maximum_bytes: usize) std.Build.LazyPath {
    const generation = b.addRunArtifact(generator);
    generation.addArg("state-items");
    generation.addFileArg(registry);
    const output = generation.addOutputFileArg(b.fmt("{s}_state_items.zig", .{process}));
    generation.addArgs(&.{ b.fmt("{d}", .{maximum_bytes}), process, if (names) "names" else "no-names" });
    return output;
}

pub fn frontendDefaults(b: *std.Build, generator: *std.Build.Step.Compile, declaration: std.Build.LazyPath, maximum_bytes: usize) std.Build.LazyPath {
    const generation = b.addRunArtifact(generator);
    generation.addArg("frontend-defaults");
    generation.addFileArg(declaration);
    const output = generation.addOutputFileArg("frontend_defaults.zig");
    generation.addArg(b.fmt("{d}", .{maximum_bytes}));
    return output;
}
