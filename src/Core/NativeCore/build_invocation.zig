//! P0-only modules and focused checks. Does not assemble other interaction slices.
const std = @import("std");

pub fn add(b: *std.Build, target: std.Build.ResolvedTarget, optimize: std.builtin.OptimizeMode, test_llvm: bool, product: *std.Build.Module, product_tests: *std.Build.Module, all_tests: *std.Build.Step) void {
    const contract = b.createModule(.{ .root_source_file = b.path("src/interaction/contracts/invocation.zig"), .target = target, .optimize = optimize });
    const wire = b.createModule(.{ .root_source_file = b.path("src/interaction/pipes/wire.zig"), .target = target, .optimize = optimize });
    wire.addImport("rm_invocation_contract", contract);
    const client = b.createModule(.{ .root_source_file = b.path("src/interaction/pipes/client.zig"), .target = target, .optimize = optimize });
    client.addImport("rm_invocation_contract", contract);
    client.addImport("rm_invocation_wire", wire);
    const owner = b.createModule(.{ .root_source_file = b.path("src/interaction/pipes/invocation.zig"), .target = target, .optimize = optimize });
    owner.addImport("rm_invocation_contract", contract);
    owner.addImport("rm_invocation_wire", wire);
    owner.linkSystemLibrary("kernel32", .{});
    const abi = b.createModule(.{ .root_source_file = b.path("src/interaction/pipes/invocation_abi.zig"), .target = target, .optimize = optimize });
    abi.addImport("rm_invocation", owner);
    // C18 is the product capability owner used by the invocation ABI. Share
    // that same instance with tests; no test root is imported by the product.
    const directory = b.createModule(.{ .root_source_file = b.path("src/interaction/capabilities/invocation_directory.zig"), .target = target, .optimize = optimize });
    directory.addImport("rm_invocation_contract", contract);
    abi.addImport("rm_invocation_directory", directory);
    product.addImport("rm_invocation_abi", abi);
    product_tests.addImport("rm_invocation_abi", abi);

    const check = b.step("check-interaction-pipe", "Compile P0 owner/client/ABI and install focused test artifacts");
    const run = b.step("test-interaction-pipe", "Run P0 invocation tests");
    const tests = [_][]const u8{ "wire", "client", "owner", "abi" };
    for (tests) |name| {
        const root = b.createModule(.{
            .root_source_file = b.path(b.fmt("../../../tests/Native/interaction/invocation_{s}_tests.zig", .{name})),
            .target = target,
            .optimize = optimize,
        });
        root.addImport("rm_invocation_contract", contract);
        root.addImport("rm_invocation_wire", wire);
        root.addImport("rm_invocation_client", client);
        root.addImport("rm_invocation", owner);
        root.addImport("rm_invocation_abi", abi);
        root.addImport("rm_invocation_directory", directory);
        const unit = b.addTest(.{ .name = b.fmt("invocation-{s}-tests", .{name}), .root_module = root, .use_llvm = test_llvm });
        const install = b.addInstallArtifact(unit, .{ .dest_dir = .{ .override = .bin } });
        check.dependOn(&install.step);
        const execute = b.addRunArtifact(unit);
        run.dependOn(&execute.step);
    }
    all_tests.dependOn(run);

    const fixture_root = b.createModule(.{
        .root_source_file = b.path("../../../tests/Native/interaction/invocation_fixture.zig"),
        .target = target,
        .optimize = optimize,
    });
    fixture_root.addImport("rm_invocation_abi", abi);
    fixture_root.addImport("rm_invocation_contract", contract);
    const fixture = b.addLibrary(.{ .name = "ResourceManager.InvocationFixture", .linkage = .dynamic, .root_module = fixture_root, .use_llvm = test_llvm });
    const install_fixture = b.addInstallArtifact(fixture, .{});
    check.dependOn(&install_fixture.step);
}
