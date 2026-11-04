const std = @import("std");

pub fn build(b: *std.Build) void {
    const target = b.standardTargetOptions(.{});
    const optimize = b.standardOptimizeOption(.{});
    const activity_decay_module = b.createModule(.{
        .root_source_file = b.path("src/activity_decay.zig"),
        .target = target,
        .optimize = optimize,
    });

    const adapter_module = b.addModule("resource_manager_adapter", .{
        .root_source_file = b.path("src/adapter_resource_ledger.zig"),
        .target = target,
        .optimize = optimize,
    });

    const native_module = b.createModule(.{
        .root_source_file = b.path("src/native_abi.zig"),
        .target = target,
        .optimize = optimize,
    });
    native_module.addImport("activity_decay", activity_decay_module);
    const native = b.addLibrary(.{
        .name = "ResourceManager.Adapter.Native",
        .root_module = native_module,
        .linkage = .dynamic,
    });
    b.installArtifact(native);

    const adapter_tests = b.addTest(.{ .root_module = adapter_module });
    const native_tests_module = b.createModule(.{
        .root_source_file = b.path("src/native_abi.zig"),
        .target = target,
        .optimize = optimize,
    });
    native_tests_module.addImport("activity_decay", activity_decay_module);
    const native_tests = b.addTest(.{ .root_module = native_tests_module });
    const resource_scheduler_tests = b.addTest(.{
        .root_module = b.createModule(.{
            .root_source_file = b.path("src/resource_scheduler/root.zig"),
            .target = target,
            .optimize = optimize,
        }),
    });
    const private_resource_tests = b.addTest(.{
        .root_module = b.createModule(.{
            .root_source_file = b.path("src/private_resource/root.zig"),
            .target = target,
            .optimize = optimize,
        }),
    });
    const local_resource_manager_tests_module = b.createModule(.{
        .root_source_file = b.path("src/local_resource_manager/root.zig"),
        .target = target,
        .optimize = optimize,
    });
    local_resource_manager_tests_module.addImport("activity_decay", activity_decay_module);
    const local_resource_manager_tests = b.addTest(.{
        .root_module = local_resource_manager_tests_module,
    });
    const run_adapter_tests = b.addRunArtifact(adapter_tests);
    const run_native_tests = b.addRunArtifact(native_tests);
    const run_resource_scheduler_tests = b.addRunArtifact(resource_scheduler_tests);
    const run_private_resource_tests = b.addRunArtifact(private_resource_tests);
    const run_local_resource_manager_tests = b.addRunArtifact(local_resource_manager_tests);
    const local_resource_manager_test_step = b.step(
        "test-local-resource-manager",
        "Run LocalResourceManager Zig tests",
    );
    local_resource_manager_test_step.dependOn(&run_local_resource_manager_tests.step);
    const test_step = b.step("test", "Run Adapter SDK Zig tests");
    test_step.dependOn(&run_adapter_tests.step);
    test_step.dependOn(&run_native_tests.step);
    test_step.dependOn(&run_resource_scheduler_tests.step);
    test_step.dependOn(&run_private_resource_tests.step);
    test_step.dependOn(&run_local_resource_manager_tests.step);
}
