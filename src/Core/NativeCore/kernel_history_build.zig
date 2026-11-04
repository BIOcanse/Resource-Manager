//! Kernel-only gate additions. Product/shared build and ABI remain maintenance-owned.
const std = @import("std");
pub fn build(b: *std.Build) void {
    @import("http_common_build.zig").build(b);
    var target: std.Build.ResolvedTarget = undefined;
    for (b.getInstallStep().dependencies.items) |step| {
        if (step.tag != .install_artifact) continue;
        const install: *std.Build.Step.InstallArtifact = @fieldParentPtr("step", step);
        if (std.mem.eql(u8, install.artifact.name, "ResourceManager.NativeCore")) {
            install.artifact.root_module.root_source_file = b.path("src/kernel_history_product_test_root.zig");
            target = install.artifact.root_module.resolved_target.?;
        }
    }
    const core = b.createModule(.{ .root_source_file = b.path("src/kernel_history_test_root.zig"), .target = target, .optimize = .debug });
    const test_exe = b.addTest(.{ .root_module = b.createModule(.{ .root_source_file = b.path("tests/kernel_history_test.zig"), .target = target, .optimize = .debug, .imports = &.{.{ .name = "kernel", .module = core }} }) });
    b.getInstallStep().dependOn(&b.addInstallArtifact(test_exe, .{ .dest_dir = .{ .override = .bin }, .dest_sub_path = "kernel-history-test.exe" }).step);
    const translated = b.addTranslateC(.{ .root_source_file = b.path("../../../build/dependencies/llhttp/include/llhttp.h"), .target = target, .optimize = .fast });
    const fixture = b.createModule(.{ .root_source_file = b.path("src/kernel_history_fixture_test_root.zig"), .target = target, .optimize = .fast });
    fixture.addImport("llhttp", translated.createModule());
    fixture.addIncludePath(b.path("../../../build/dependencies/llhttp/include"));
    fixture.addCSourceFiles(.{ .root = b.path("../../../build/dependencies/llhttp"), .files = &.{ "src/api.c", "src/http.c", "src/llhttp.c" }, .flags = &.{"-std=c99"} });
    fixture.link_libc = true;
    fixture.linkSystemLibrary("ws2_32", .{});
    fixture.linkSystemLibrary("kernel32", .{});
    b.installArtifact(b.addLibrary(.{ .name = "ResourceManager.KernelHistory.Fixture", .linkage = .dynamic, .root_module = fixture }));
}
