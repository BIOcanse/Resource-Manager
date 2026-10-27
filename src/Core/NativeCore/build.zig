const std = @import("std");

pub fn build(b: *std.Build) void {
    const target = b.standardTargetOptions(.{});
    const optimize = b.standardOptimizeOption(.{});

    const control_module = b.createModule(.{
        .root_source_file = b.path("../../Shared/Control/src/root.zig"),
        .target = target,
        .optimize = .ReleaseSafe,
    });

    const powershell = b.option([]const u8, "powershell", "PowerShell executable used to generate state-item declarations") orelse "pwsh";
    const state_names = b.option(bool, "state-names", "Include development-only names in generated state-item declarations") orelse true;
    const generate_backend_declarations = b.addSystemCommand(&.{ powershell, "-NoProfile", "-File" });
    generate_backend_declarations.addFileArg(b.path("../../../scripts/build/Generate-StateItems.ps1"));
    generate_backend_declarations.addFileInput(b.path("../../../scripts/build/StateItemRegistry.psm1"));
    generate_backend_declarations.addArg("-Registry");
    generate_backend_declarations.addFileArg(b.path("../../../src/Shared/StateItems/state-items.json"));
    generate_backend_declarations.addArgs(&.{ "-Process", "backend", "-Language", "Zig" });
    generate_backend_declarations.addArg(if (state_names) "-Names:$true" else "-Names:$false");
    generate_backend_declarations.addArg("-Output");
    const backend_declarations_file = generate_backend_declarations.addOutputFileArg("backend_declarations.zig");

    const backend_declarations_module = b.createModule(.{
        .root_source_file = backend_declarations_file,
        .target = target,
        .optimize = .ReleaseSafe,
    });
    backend_declarations_module.addImport("rm_control", control_module);

    const backend_tree_module = b.createModule(.{
        .root_source_file = b.path("src/control/backend_tree.zig"),
        .target = target,
        .optimize = optimize,
    });
    backend_tree_module.addImport("rm_control", control_module);
    backend_tree_module.addImport("backend_declarations", backend_declarations_module);

    // The exporter is a separate development executable. Its named declarations
    // never flow into the product library, including -Dstate-names=false builds.
    const generate_export_declarations = b.addSystemCommand(&.{ powershell, "-NoProfile", "-File" });
    generate_export_declarations.addFileArg(b.path("../../../scripts/build/Generate-StateItems.ps1"));
    generate_export_declarations.addFileInput(b.path("../../../scripts/build/StateItemRegistry.psm1"));
    generate_export_declarations.addArg("-Registry");
    generate_export_declarations.addFileArg(b.path("../../../src/Shared/StateItems/state-items.json"));
    generate_export_declarations.addArgs(&.{ "-Process", "backend", "-Language", "Zig", "-Names:$true", "-Output" });
    const export_declarations_file = generate_export_declarations.addOutputFileArg("backend_export_declarations.zig");
    const export_declarations = b.createModule(.{
        .root_source_file = export_declarations_file,
        .target = target,
        .optimize = .ReleaseSafe,
        .imports = &.{.{ .name = "rm_control", .module = control_module }},
    });
    const export_backend_tree = b.createModule(.{
        .root_source_file = b.path("src/control/backend_tree.zig"),
        .target = target,
        .optimize = .ReleaseSafe,
        .imports = &.{
            .{ .name = "rm_control", .module = control_module },
            .{ .name = "backend_declarations", .module = export_declarations },
        },
    });
    const process_tree = b.createModule(.{
        .root_source_file = b.path("src/control/export_tree.zig"),
        .target = target,
        .optimize = .ReleaseSafe,
        .imports = &.{
            .{ .name = "rm_control", .module = control_module },
            .{ .name = "backend_tree", .module = export_backend_tree },
        },
    });
    const exporter = b.addExecutable(.{
        .name = "export-control-tree",
        .use_llvm = false,
        .root_module = b.createModule(.{
            .root_source_file = b.path("../../Shared/Control/src/export_tree_main.zig"),
            .target = target,
            .optimize = .ReleaseSafe,
            .imports = &.{
                .{ .name = "rm_control", .module = control_module },
                .{ .name = "process_tree", .module = process_tree },
            },
        }),
    });
    const run_exporter = b.addRunArtifact(exporter);
    b.step("export-tree", "Export development control declarations").dependOn(&run_exporter.step);

    const native_core_module = b.createModule(.{
        .root_source_file = b.path("src/abi.zig"),
        .target = target,
        .optimize = optimize,
    });
    native_core_module.addImport("rm_control", control_module);
    native_core_module.addImport("backend_tree", backend_tree_module);
    native_core_module.linkSystemLibrary("pdh", .{});
    native_core_module.linkSystemLibrary("iphlpapi", .{});
    native_core_module.linkSystemLibrary("psapi", .{});
    native_core_module.linkSystemLibrary("bcrypt", .{});
    native_core_module.linkSystemLibrary("advapi32", .{});

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
    test_module.addImport("rm_control", control_module);
    test_module.addImport("backend_tree", backend_tree_module);
    test_module.linkSystemLibrary("pdh", .{});
    test_module.linkSystemLibrary("iphlpapi", .{});
    test_module.linkSystemLibrary("psapi", .{});
    test_module.linkSystemLibrary("bcrypt", .{});
    test_module.linkSystemLibrary("advapi32", .{});

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

    const control_abi_test_module = b.createModule(.{
        .root_source_file = b.path("src/control_abi_test_root.zig"),
        .target = target,
        .optimize = .ReleaseFast,
    });
    control_abi_test_module.addImport("rm_control", control_module);
    control_abi_test_module.addImport("backend_tree", backend_tree_module);
    control_abi_test_module.addImport("backend_declarations", backend_declarations_module);
    const control_abi_tests = b.addTest(.{ .root_module = control_abi_test_module });
    const run_control_abi_tests = b.addRunArtifact(control_abi_tests);
    test_step.dependOn(&run_control_abi_tests.step);
    test_step.dependOn(&run_smart_coordinator_tests.step);
    test_step.dependOn(&run_transaction_journal_tests.step);

    const kernel_etw_module = b.createModule(.{
        .root_source_file = b.path("src/kernel_etw_test_root.zig"),
        .target = target,
        .optimize = optimize,
    });
    kernel_etw_module.linkSystemLibrary("advapi32", .{});
    const kernel_etw_tests = b.addTest(.{ .root_module = kernel_etw_module });
    const run_kernel_etw_tests = b.addRunArtifact(kernel_etw_tests);
    const kernel_etw_step = b.step("test-kernel-etw", "Run kernel ETW migration equivalence and allocation tests");
    kernel_etw_step.dependOn(&run_kernel_etw_tests.step);
    test_step.dependOn(&run_kernel_etw_tests.step);

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
