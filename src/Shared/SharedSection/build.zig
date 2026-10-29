const std = @import("std");

pub fn build(b: *std.Build) void {
    const target = b.standardTargetOptions(.{});
    // Region contents are untrusted input: this boundary keeps its safety checks,
    // including inside ReleaseFast consumers.
    const shared_section = b.addModule("rm_shared_section", .{
        .root_source_file = b.path("src/root.zig"),
        .target = target,
        .optimize = .ReleaseSafe,
    });
    const tests = b.addTest(.{ .root_module = b.createModule(.{
        .root_source_file = b.path("tests/root.zig"),
        .target = target,
        .optimize = .ReleaseSafe,
        .imports = &.{.{ .name = "rm_shared_section", .module = shared_section }},
    }) });
    const run_tests = b.addRunArtifact(tests);
    const test_step = b.step("test", "Run layout, sequence, malformed-region, end and C ABI tests");
    test_step.dependOn(&run_tests.step);
}
