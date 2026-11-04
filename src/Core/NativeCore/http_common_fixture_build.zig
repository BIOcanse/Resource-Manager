//! TEST-ONLY listener DLL; no product ABI or service build graph.
const std = @import("std");
pub fn build(b: *std.Build) void {
    const target = b.standardTargetOptions(.{});
    // Match the product's target/toolchain/optimization when a validation DLL
    // supplies opaque native Family descriptors to that product.
    const optimize = b.standardOptimizeOption(.{});
    const translated = b.addTranslateC(.{ .root_source_file = b.path("../../../build/dependencies/llhttp/include/llhttp.h"), .target = target, .optimize = optimize });
    const module = b.createModule(.{ .root_source_file = b.path("src/http_common_fixture_test_root.zig"), .target = target, .optimize = optimize });
    module.addImport("llhttp", translated.createModule());
    module.addIncludePath(b.path("../../../build/dependencies/llhttp/include"));
    module.addCSourceFiles(.{ .root = b.path("../../../build/dependencies/llhttp"), .files = &.{ "src/api.c", "src/http.c", "src/llhttp.c" }, .flags = &.{"-std=c99"} });
    module.link_libc = true;
    module.linkSystemLibrary("ws2_32", .{});
    module.linkSystemLibrary("kernel32", .{});
    b.installArtifact(b.addLibrary(.{ .name = "ResourceManager.HttpCommon.Fixture", .linkage = .dynamic, .root_module = module }));
}
