const std = @import("std");
pub fn build(b: *std.Build) void {
    const target = b.standardTargetOptions(.{});
    const control = b.createModule(.{
        .root_source_file = b.path("../../../src/Shared/Control/src/root.zig"),
        .target = target,
        .optimize = .ReleaseSafe,
    });
    const source = b.createModule(.{
        .root_source_file = b.path("process.zig"),
        .target = target,
        .optimize = .ReleaseSafe,
        .imports = &.{.{ .name = "rm_control", .module = control }},
    });
    const executable = b.addExecutable(.{ .name = "fixture-export", .use_llvm = false, .root_module = b.createModule(.{
        .root_source_file = b.path("../../../src/Shared/Control/src/export_tree_main.zig"),
        .target = target,
        .optimize = .ReleaseSafe,
        .imports = &.{ .{ .name = "rm_control", .module = control }, .{ .name = "process_tree", .module = source } },
    }) });
    const run = b.addRunArtifact(executable);
    b.step("export-tree", "Export fixture declarations").dependOn(&run.step);
    const tests = b.addTest(.{ .use_llvm = false, .root_module = b.createModule(.{
        .root_source_file = b.path("../../../src/Shared/Control/src/export_tree.zig"),
        .target = target,
        .optimize = .ReleaseSafe,
    }) });
    const run_tests = b.addRunArtifact(tests);
    b.step("test", "Test declaration-only export").dependOn(&run_tests.step);
}
