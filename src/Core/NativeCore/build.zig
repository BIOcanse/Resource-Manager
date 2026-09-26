const std = @import("std");

pub fn build(b: *std.Build) void {
    const target = b.standardTargetOptions(.{});
    const optimize = b.standardOptimizeOption(.{});

    const native_core_module = b.createModule(.{
        .root_source_file = b.path("src/abi.zig"),
        .target = target,
        .optimize = optimize,
    });
    native_core_module.linkSystemLibrary("pdh", .{});
    native_core_module.linkSystemLibrary("psapi", .{});
    native_core_module.linkSystemLibrary("bcrypt", .{});

    const native_core = b.addLibrary(.{
        .name = "ResourceManager.NativeCore",
        .root_module = native_core_module,
        .linkage = .dynamic,
    });
    b.installArtifact(native_core);

    const test_module = b.createModule(.{
        .root_source_file = b.path("src/abi.zig"),
        .target = target,
        .optimize = optimize,
    });
    test_module.linkSystemLibrary("pdh", .{});
    test_module.linkSystemLibrary("psapi", .{});
    test_module.linkSystemLibrary("bcrypt", .{});

    const native_core_tests = b.addTest(.{
        .root_module = test_module,
    });
    const smart_coordinator_tests = b.addTest(.{
        .root_module = b.createModule(.{
            .root_source_file = b.path("src/smart_coordinator_test_root.zig"),
            .target = target,
            .optimize = optimize,
        }),
    });
    const transaction_journal_tests = b.addTest(.{
        .root_module = b.createModule(.{
            .root_source_file = b.path("src/transaction_journal_test_root.zig"),
            .target = target,
            .optimize = optimize,
        }),
    });
    const run_tests = b.addRunArtifact(native_core_tests);
    const run_smart_coordinator_tests = b.addRunArtifact(smart_coordinator_tests);
    const run_transaction_journal_tests = b.addRunArtifact(transaction_journal_tests);

    const test_step = b.step("test", "Run NativeCore tests");
    test_step.dependOn(&run_tests.step);
    test_step.dependOn(&run_smart_coordinator_tests.step);
    test_step.dependOn(&run_transaction_journal_tests.step);

    const calibration_tool_module = b.createModule(.{
        .root_source_file = b.path("src/scheduling/memory_mode_controller/calibration_cli.zig"),
        .target = target,
        .optimize = optimize,
    });
    const calibration_tool = b.addExecutable(.{
        .name = "ResourceManager.MemoryModeCalibrate",
        .root_module = calibration_tool_module,
    });
    const run_calibration_tool = b.addRunArtifact(calibration_tool);
    if (b.args) |args| run_calibration_tool.addArgs(args);

    const calibration_step = b.step(
        "calibrate",
        "Calibrate and seal RAM or VRAM memory-mode thresholds from a canonical TSV corpus",
    );
    calibration_step.dependOn(&run_calibration_tool.step);

    const assembly_tool_module = b.createModule(.{
        .root_source_file = b.path("src/scheduling/memory_mode_controller/calibration_assembly_cli.zig"),
        .target = target,
        .optimize = optimize,
    });
    const assembly_tool = b.addExecutable(.{
        .name = "ResourceManager.MemoryModeAssembleCorpus",
        .root_module = assembly_tool_module,
    });
    const run_assembly_tool = b.addRunArtifact(assembly_tool);
    if (b.args) |args| run_assembly_tool.addArgs(args);

    const assembly_step = b.step(
        "assemble-calibration-corpus",
        "Assemble a canonical calibration corpus from ordinal workload observations",
    );
    assembly_step.dependOn(&run_assembly_tool.step);
}
