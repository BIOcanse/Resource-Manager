//! Worker-only verification graph. No shared ABI/build registration edits.
const std = @import("std");
pub fn build(b: *std.Build) void {
    @import("http_common_build.zig").build(b);
    var product: *std.Build.Module = undefined;
    for (b.getInstallStep().dependencies.items) |step| {
        if (step.tag == .install_artifact) {
            const install: *std.Build.Step.InstallArtifact = @fieldParentPtr("step", step);
            if (std.mem.eql(u8, install.artifact.name, "ResourceManager.NativeCore")) {
                product = install.artifact.root_module;
                product.root_source_file = b.path("src/runtime_http_product_test_root.zig");
            }
        }
    }
    const target = product.resolved_target.?;
    const translated = b.addTranslateC(.{ .root_source_file = b.path("../../../build/dependencies/llhttp/include/llhttp.h"), .target = target, .optimize = product.optimize.? });
    const fixture = b.createModule(.{ .root_source_file = b.path("src/runtime_http_fixture_test_root.zig"), .target = target, .optimize = product.optimize, .error_tracing = product.error_tracing });
    fixture.addImport("llhttp", translated.createModule());
    fixture.addIncludePath(b.path("../../../build/dependencies/llhttp/include"));
    fixture.addCSourceFiles(.{ .root = b.path("../../../build/dependencies/llhttp"), .files = &.{ "src/api.c", "src/http.c", "src/llhttp.c" }, .flags = &.{"-std=c99"} });
    fixture.link_libc = true;
    fixture.linkSystemLibrary("ws2_32", .{});
    fixture.linkSystemLibrary("kernel32", .{});
    b.installArtifact(b.addLibrary(.{ .name = "ResourceManager.RuntimeHttp.Fixture", .linkage = .dynamic, .root_module = fixture }));
    const runtime = b.createModule(.{ .root_source_file = b.path("src/runtime_http_test_root.zig"), .target = target, .optimize = .debug });
    const tests = b.addTest(.{ .root_module = b.createModule(.{ .root_source_file = b.path("tests/runtime_http_test.zig"), .target = target, .optimize = .debug, .imports = &.{.{ .name = "runtime", .module = runtime }} }) });
    b.getInstallStep().dependOn(&b.addInstallArtifact(tests, .{ .dest_dir = .{ .override = .bin }, .dest_sub_path = "runtime-http-test.exe" }).step);
}
