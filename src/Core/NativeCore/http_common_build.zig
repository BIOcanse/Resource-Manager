//! Focused verification only: keep the existing product build graph and replace
//! its root with a test registration overlay, without editing build.zig/abi.zig.
const std = @import("std");
pub fn build(b: *std.Build) void {
    @import("build.zig").build(b);
    var product: *std.Build.Module = undefined;
    for (b.getInstallStep().dependencies.items) |step| {
        if (step.tag == .install_artifact) {
            const install: *std.Build.Step.InstallArtifact = @fieldParentPtr("step", step);
            if (std.mem.eql(u8, install.artifact.name, "ResourceManager.NativeCore")) {
                install.artifact.root_module.root_source_file = b.path("src/http_common_product_test_root.zig");
                product = install.artifact.root_module;
            }
        }
    }
    const target = product.resolved_target.?;
    // Dev's grant leaf already consumes owner-deps names, while its shared
    // build/ABI registration is maintenance-owned. The TEST overlay exposes
    // aliases of the existing canonical types, not another access/layout owner.
    product.addImport("rm_access", product);
    product.addImport("rm_data_area_plan", product);
    const translated = b.addTranslateC(.{
        .root_source_file = b.path("../../../build/dependencies/llhttp/include/llhttp.h"),
        .target = target,
        .optimize = .debug,
    });
    const module = b.createModule(.{
        .root_source_file = b.path("src/http_common_test_root.zig"),
        .target = target,
        .optimize = .debug,
    });
    module.addImport("llhttp", translated.createModule());
    module.addIncludePath(b.path("../../../build/dependencies/llhttp/include"));
    module.addCSourceFiles(.{ .root = b.path("../../../build/dependencies/llhttp"), .files = &.{ "src/api.c", "src/http.c", "src/llhttp.c" }, .flags = &.{"-std=c99"} });
    module.link_libc = true;
    module.linkSystemLibrary("ws2_32", .{});
    module.linkSystemLibrary("kernel32", .{});
    module.linkSystemLibrary("iphlpapi", .{});
    const fixture_module = b.createModule(.{
        .root_source_file = b.path("src/http_common_fixture_test_root.zig"),
        .target = target,
        // Family descriptors contain internal Zig error-union callbacks. Their
        // hidden error-trace calling ABI must match the consuming product.
        .optimize = product.optimize,
        .error_tracing = product.error_tracing,
    });
    fixture_module.addImport("llhttp", translated.createModule());
    fixture_module.addIncludePath(b.path("../../../build/dependencies/llhttp/include"));
    fixture_module.addCSourceFiles(.{ .root = b.path("../../../build/dependencies/llhttp"), .files = &.{ "src/api.c", "src/http.c", "src/llhttp.c" }, .flags = &.{"-std=c99"} });
    fixture_module.link_libc = true;
    fixture_module.linkSystemLibrary("ws2_32", .{});
    fixture_module.linkSystemLibrary("kernel32", .{});
    const fixture = b.addLibrary(.{ .name = "ResourceManager.HttpCommon.Fixture", .linkage = .dynamic, .root_module = fixture_module });
    b.installArtifact(fixture);
    const tests = b.addTest(.{ .root_module = b.createModule(.{
        .root_source_file = b.path("tests/http_common_test.zig"),
        .target = target,
        .optimize = .debug,
        .imports = &.{.{ .name = "http_common", .module = module }},
    }) });
    const install_tests = b.addInstallArtifact(tests, .{ .dest_dir = .{ .override = .bin } });
    b.getInstallStep().dependOn(&install_tests.step);
    const host_tests = b.addTest(.{ .root_module = b.createModule(.{
        .root_source_file = b.path("tests/http_host_test.zig"),
        .target = target,
        .optimize = .debug,
        .imports = &.{.{ .name = "http_common", .module = module }},
    }) });
    const install_host_tests = b.addInstallArtifact(host_tests, .{
        .dest_dir = .{ .override = .bin },
        .dest_sub_path = "http-host-test.exe",
    });
    b.getInstallStep().dependOn(&install_host_tests.step);
}
