//! MCP-only assembly. Production callers supply a real host; the test host is
//! explicit and can never become the default/release product binding.
const std = @import("std");

pub fn clientModule(b: *std.Build, target: std.Build.ResolvedTarget, optimize: std.builtin.OptimizeMode) *std.Build.Module {
    const contract = b.createModule(.{ .root_source_file = b.path("src/interaction/contracts/invocation.zig"), .target = target, .optimize = optimize });
    const wire = b.createModule(.{ .root_source_file = b.path("src/interaction/pipes/wire.zig"), .target = target, .optimize = optimize });
    wire.addImport("rm_invocation_contract", contract);
    const client = b.createModule(.{ .root_source_file = b.path("src/interaction/pipes/client.zig"), .target = target, .optimize = optimize });
    client.addImport("rm_invocation_contract", contract);
    client.addImport("rm_invocation_wire", wire);
    return client;
}

pub fn coreModule(b: *std.Build, target: std.Build.ResolvedTarget, optimize: std.builtin.OptimizeMode, client: *std.Build.Module) *std.Build.Module {
    const core = b.createModule(.{ .root_source_file = b.path("src/interaction/mcp/main.zig"), .target = target, .optimize = optimize });
    core.addImport("rm_invocation_client", client);
    core.linkSystemLibrary("kernel32", .{});
    return core;
}

/// Mainline can call this with the canonical P0 client and its production
/// mcp_host. No service registry/settings/owner or other frontend is imported.
pub fn product(b: *std.Build, core: *std.Build.Module, host: *std.Build.Module, use_llvm: bool) *std.Build.Step.Compile {
    core.addImport("mcp_host", host);
    return b.addExecutable(.{ .name = "ResourceManager.Mcp", .root_module = core, .use_llvm = use_llvm });
}

pub fn build(b: *std.Build) void {
    const target = b.standardTargetOptions(.{});
    const optimize = b.standardOptimizeOption(.{});
    const use_llvm = b.option(bool, "test-llvm", "Use LLVM for MCP artifacts") orelse false;
    const client = clientModule(b, target, optimize);
    const core = coreModule(b, target, optimize, client);
    const test_root = b.createModule(.{ .root_source_file = b.path("../../../tests/Native/interaction/mcp_test_root.zig"), .target = target, .optimize = optimize });
    test_root.addImport("mcp", core);
    test_root.addImport("rm_invocation_client", client);
    const unit = b.addTest(.{ .name = "mcp-tests", .root_module = test_root, .use_llvm = use_llvm });
    const check = b.step("check-mcp", "Compile MCP units and the explicit process-vector fixture");
    check.dependOn(&b.addInstallArtifact(unit, .{ .dest_dir = .{ .override = .bin } }).step);
    b.step("test-mcp", "Run MCP native tests").dependOn(&b.addRunArtifact(unit).step);

    const fixture_host = b.createModule(.{ .root_source_file = b.path("../../../tests/Native/interaction/mcp_host_fixture.zig"), .target = target, .optimize = optimize });
    fixture_host.addImport("mcp", core);
    fixture_host.addImport("rm_invocation_client", client);
    core.addImport("mcp_host", fixture_host);
    const fixture_root = b.createModule(.{ .root_source_file = b.path("../../../tests/Native/interaction/mcp_process_fixture.zig"), .target = target, .optimize = optimize });
    fixture_root.addImport("mcp", core);
    fixture_root.linkSystemLibrary("kernel32", .{});
    const fixture = b.addExecutable(.{ .name = "ResourceManager.Mcp", .root_module = fixture_root, .use_llvm = use_llvm });
    check.dependOn(&b.addInstallArtifact(fixture, .{ .dest_dir = .{ .override = .{ .custom = "fixture" } } }).step);
    b.default_step = check;

    const production_step = b.step("mcp", "Build MCP with an explicitly supplied production mcp_host");
    if (b.option([]const u8, "mcp-host", "Production host source relative to this build file")) |source| {
        const real_host = b.createModule(.{ .root_source_file = b.path(source), .target = target, .optimize = optimize });
        const real_core = coreModule(b, target, optimize, client);
        real_host.addImport("mcp", real_core);
        real_host.addImport("rm_invocation_client", client);
        const executable = product(b, real_core, real_host, use_llvm);
        production_step.dependOn(&b.addInstallArtifact(executable, .{}).step);
    } else production_step.dependOn(&b.addFail("Production MCP requires -Dmcp-host; the vector fixture is not a release host").step);
}
