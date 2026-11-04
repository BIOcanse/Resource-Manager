//! Family-only verification overlay. No change to the shared build or ABI.
const std = @import("std");
pub fn build(b: *std.Build) void {
    @import("http_common_build.zig").build(b);
    var product: *std.Build.Module = undefined;
    for (b.getInstallStep().dependencies.items) |step| {
        if (step.tag != .install_artifact) continue;
        const install: *std.Build.Step.InstallArtifact = @fieldParentPtr("step", step);
        if (std.mem.eql(u8, install.artifact.name, "ResourceManager.NativeCore")) {
            install.artifact.root_module.root_source_file = b.path("src/notifications_product_test_root.zig");
            product = install.artifact.root_module;
        }
        if (std.mem.eql(u8, install.artifact.name, "ResourceManager.HttpCommon.Fixture"))
            install.artifact.root_module.root_source_file = b.path("src/notifications_fixture_test_root.zig");
    }
    const api = b.createModule(.{ .root_source_file = b.path("src/notifications_test_api.zig"), .target = product.resolved_target.?, .optimize = .debug });
    const tests = b.addTest(.{ .root_module = b.createModule(.{
        .root_source_file = b.path("../../../tests/NativeCore/notifications_http.zig"),
        .target = product.resolved_target.?,
        .optimize = .debug,
        .imports = &.{.{ .name = "notifications_http", .module = api }},
    }) });
    b.getInstallStep().dependOn(&b.addInstallArtifact(tests, .{ .dest_dir = .{ .override = .bin }, .dest_sub_path = "notifications-http-test.exe" }).step);
}
