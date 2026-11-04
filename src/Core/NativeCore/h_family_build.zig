//! Integration gate: build the real product ABI once, plus isolated H fixtures.
const std = @import("std");
const product_build = @import("build.zig");

pub fn build(b: *std.Build) void {
    product_build.build(b);
    var product: *std.Build.Module = undefined;
    var previous: *std.Build.Step = undefined;
    for (b.getInstallStep().dependencies.items) |step| {
        if (step.tag != .install_artifact) continue;
        const install: *std.Build.Step.InstallArtifact = @fieldParentPtr("step", step);
        if (std.mem.eql(u8, install.artifact.name, "ResourceManager.NativeCore")) {
            product = install.artifact.root_module;
            previous = &install.artifact.step;
        }
    }
    const target = product.resolved_target.?;
    const fixtures = .{
        .{ "ResourceManager.HttpCommon.Fixture", "src/notifications_fixture_test_root.zig" },
        .{ "ResourceManager.KernelHistory.Fixture", "src/kernel_history_fixture_test_root.zig" },
        .{ "ResourceManager.RuntimeHttp.Fixture", "src/runtime_http_fixture_test_root.zig" },
    };
    const main_fixtures = b.step("build-main-http-fixtures", "Build the dedicated listener DLLs owned by the main backend tests");
    inline for (fixtures) |spec| {
        const module = b.createModule(.{ .root_source_file = b.path(spec[1]), .target = target, .optimize = product.optimize, .error_tracing = product.error_tracing });
        product_build.addHttpParser(b, module, target, product.optimize.?);
        module.linkSystemLibrary("kernel32", .{});
        const fixture = b.addLibrary(.{ .name = spec[0], .linkage = .dynamic, .root_module = module });
        fixture.step.dependOn(previous);
        previous = &fixture.step;
        b.installArtifact(fixture);
        if (!std.mem.eql(u8, spec[0], "ResourceManager.RuntimeHttp.Fixture"))
            main_fixtures.dependOn(&b.addInstallArtifact(fixture, .{}).step);
    }
    const common = b.createModule(.{ .root_source_file = b.path("src/http_common_test_root.zig"), .target = target, .optimize = .debug });
    product_build.addHttpParser(b, common, target, .debug);
    common.linkSystemLibrary("kernel32", .{});
    common.linkSystemLibrary("iphlpapi", .{});
    const specs = .{
        .{ "http-common-test.exe", "tests/http_common_test.zig", "http_common", common },
        .{ "http-host-test.exe", "tests/http_host_test.zig", "http_common", common },
        .{ "notifications-http-test.exe", "../../../tests/NativeCore/notifications_http.zig", "notifications_http", b.createModule(.{ .root_source_file = b.path("src/notifications_test_api.zig"), .target = target, .optimize = .debug }) },
        .{ "kernel-history-test.exe", "tests/kernel_history_test.zig", "kernel", b.createModule(.{ .root_source_file = b.path("src/kernel_history_test_root.zig"), .target = target, .optimize = .debug }) },
        .{ "runtime-http-test.exe", "tests/runtime_http_test.zig", "runtime", b.createModule(.{ .root_source_file = b.path("src/runtime_http_test_root.zig"), .target = target, .optimize = .debug }) },
    };
    inline for (specs) |spec| {
        const module = b.createModule(.{ .root_source_file = b.path(spec[1]), .target = target, .optimize = .debug, .imports = &.{.{ .name = spec[2], .module = spec[3] }} });
        // llhttp COFF export directives and Windows import libraries require
        // the slices' LLVM/LLD route. Explicit ordering bounds its memory.
        const tests = b.addTest(.{ .root_module = module });
        tests.step.dependOn(previous);
        previous = &tests.step;
        b.getInstallStep().dependOn(&b.addInstallArtifact(tests, .{ .dest_dir = .{ .override = .bin }, .dest_sub_path = spec[0] }).step);
    }
}
