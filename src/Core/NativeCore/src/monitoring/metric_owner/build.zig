const std = @import("std");
pub fn build(b: *std.Build) void {
    const target = b.standardTargetOptions(.{});
    const optimize = b.standardOptimizeOption(.{});
    const module = b.createModule(.{ .root_source_file = b.path("../../metric_owner_test_root.zig"), .target = target, .optimize = optimize });
    const shared = b.createModule(.{ .root_source_file = b.path("../../../../../Shared/SharedSection/src/root.zig"), .target = target, .optimize = optimize });
    module.addImport("rm_shared_section", shared);
    const tests = b.addTest(.{ .root_module = module, .use_llvm = false });
    const run = b.addRunArtifact(tests);
    b.step("test", "Run inactive metric owner semantics").dependOn(&run.step);
}
