const std = @import("std");
const control_sdk = @import("control_tree_sdk");

pub fn build(b: *std.Build) void {
    const control_source = control_sdk.source(b, b.path("../../.."));
    const target = b.standardTargetOptions(.{});
    const optimize = b.standardOptimizeOption(.{});
    const test_llvm = b.option(bool, "test-llvm", "Use LLVM for test artifacts; false selects the bounded self-hosted route") orelse true;
    const notifications = b.createModule(.{ .root_source_file = b.path("src/notifications/owner.zig"), .target = target, .optimize = optimize });
    const notification_tests = b.addTest(.{ .use_llvm = test_llvm, .root_module = b.createModule(.{
        .root_source_file = b.path("../../../tests/NativeCore/notifications.zig"),
        .target = target,
        .optimize = optimize,
        .imports = &.{.{ .name = "notifications", .module = notifications }},
    }) });
    const install_notification_tests = b.addInstallArtifact(notification_tests, .{
        .dest_dir = .{ .override = .{ .custom = "tests" } },
        .dest_sub_path = "notifications-tests.exe",
    });
    b.step("build-notifications-tests", "Compile only the focused notification tests").dependOn(&install_notification_tests.step);
    const run_notification_tests = b.addRunArtifact(notification_tests);
    b.step("test-notifications", "Run only the focused notification tests").dependOn(&run_notification_tests.step);

    const control_module = b.createModule(.{
        .root_source_file = control_source.path(b, "src/root.zig"),
        .target = target,
        .optimize = .safe,
    });
    const shared_section_module = b.createModule(.{
        .root_source_file = b.path("../../Shared/SharedSection/src/root.zig"),
        .target = target,
        .optimize = .safe,
    });
    const windows_clock = b.createModule(.{
        .root_source_file = b.path("../../Shared/Runtime/native/windows/clock.zig"),
        .target = target,
        .optimize = optimize,
    });
    const powershell = b.option([]const u8, "powershell", "PowerShell executable used to generate state-item declarations") orelse "pwsh";
    const state_names = b.option(bool, "state-names", "Include development-only names in generated state-item declarations") orelse true;
    const generate_backend_declarations = b.addSystemCommand(&.{ powershell, "-NoProfile", "-File" });
    generate_backend_declarations.addFileArg(b.path("../../../build/scripts/build/Generate-StateItems.ps1"));
    generate_backend_declarations.addFileInput(b.path("../../../build/scripts/build/StateItemRegistry.psm1"));
    generate_backend_declarations.addArg("-Registry");
    generate_backend_declarations.addFileArg(b.path("../../../src/Shared/StateItems/state-items.json"));
    generate_backend_declarations.addArgs(&.{ "-Process", "backend", "-Language", "Zig" });
    generate_backend_declarations.addArg(if (state_names) "-Names:$true" else "-Names:$false");
    generate_backend_declarations.addArg("-Output");
    const backend_declarations_file = generate_backend_declarations.addOutputFileArg("backend_declarations.zig");

    const backend_declarations_module = b.createModule(.{
        .root_source_file = backend_declarations_file,
        .target = target,
        .optimize = .safe,
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
    generate_export_declarations.addFileArg(b.path("../../../build/scripts/build/Generate-StateItems.ps1"));
    generate_export_declarations.addFileInput(b.path("../../../build/scripts/build/StateItemRegistry.psm1"));
    generate_export_declarations.addArg("-Registry");
    generate_export_declarations.addFileArg(b.path("../../../src/Shared/StateItems/state-items.json"));
    generate_export_declarations.addArgs(&.{ "-Process", "backend", "-Language", "Zig", "-Names:$true", "-Output" });
    const export_declarations_file = generate_export_declarations.addOutputFileArg("backend_export_declarations.zig");
    const export_declarations = b.createModule(.{
        .root_source_file = export_declarations_file,
        .target = target,
        .optimize = .safe,
        .imports = &.{.{ .name = "rm_control", .module = control_module }},
    });
    const export_backend_tree = b.createModule(.{
        .root_source_file = b.path("src/control/backend_tree.zig"),
        .target = target,
        .optimize = .safe,
        .imports = &.{
            .{ .name = "rm_control", .module = control_module },
            .{ .name = "backend_declarations", .module = export_declarations },
        },
    });
    const process_tree = b.createModule(.{
        .root_source_file = b.path("src/control/export_tree.zig"),
        .target = target,
        .optimize = .safe,
        .imports = &.{
            .{ .name = "rm_control", .module = control_module },
            .{ .name = "backend_tree", .module = export_backend_tree },
        },
    });
    const exporter = b.addExecutable(.{
        .name = "export-control-tree",
        .use_llvm = false,
        .root_module = b.createModule(.{
            .root_source_file = control_source.path(b, "src/export_tree_main.zig"),
            .target = target,
            .optimize = .safe,
            .imports = &.{
                .{ .name = "rm_control", .module = control_module },
                .{ .name = "process_tree", .module = process_tree },
                .{ .name = "control_tree", .module = control_module },
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
    native_core_module.addImport("rm_shared_section", shared_section_module);
    native_core_module.addImport("rm_windows_clock", windows_clock);
    native_core_module.addImport("backend_tree", backend_tree_module);
    addHttpParser(b, native_core_module, target, optimize);
    native_core_module.linkSystemLibrary("kernel32", .{});
    native_core_module.linkSystemLibrary("pdh", .{});
    native_core_module.linkSystemLibrary("iphlpapi", .{});
    native_core_module.linkSystemLibrary("psapi", .{});
    native_core_module.linkSystemLibrary("bcrypt", .{});
    native_core_module.linkSystemLibrary("advapi32", .{});
    native_core_module.linkSystemLibrary("setupapi", .{});
    native_core_module.linkSystemLibrary("cfgmgr32", .{});
    native_core_module.linkSystemLibrary("ntdll", .{});
    native_core_module.linkSystemLibrary("wevtapi", .{});
    native_core_module.linkSystemLibrary("shell32", .{});

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
    test_module.addImport("rm_shared_section", shared_section_module);
    test_module.addImport("rm_windows_clock", windows_clock);
    test_module.addImport("backend_tree", backend_tree_module);
    addHttpParser(b, test_module, target, optimize);
    test_module.linkSystemLibrary("kernel32", .{});
    test_module.linkSystemLibrary("setupapi", .{});
    test_module.linkSystemLibrary("cfgmgr32", .{});
    test_module.linkSystemLibrary("pdh", .{});
    test_module.linkSystemLibrary("iphlpapi", .{});
    test_module.linkSystemLibrary("psapi", .{});
    test_module.linkSystemLibrary("bcrypt", .{});
    test_module.linkSystemLibrary("advapi32", .{});
    test_module.linkSystemLibrary("ntdll", .{});
    test_module.linkSystemLibrary("wevtapi", .{});
    test_module.linkSystemLibrary("shell32", .{});

    const freeze_tests_module = b.createModule(.{ .root_source_file = b.path("src/freeze_reports_test_root.zig"), .target = target, .optimize = optimize });
    freeze_tests_module.linkSystemLibrary("kernel32", .{});
    freeze_tests_module.linkSystemLibrary("ntdll", .{});
    freeze_tests_module.linkSystemLibrary("wevtapi", .{});
    const freeze_tests = b.addTest(.{ .use_llvm = test_llvm, .root_module = freeze_tests_module });
    b.step("check-freeze-reports", "Compile F3/F4 focused tests").dependOn(&freeze_tests.step);
    const freeze_run = b.addRunArtifact(freeze_tests);
    b.step("test-freeze-reports", "Run F3/F4 focused tests").dependOn(&freeze_run.step);

    const native_core_tests = b.addTest(.{
        .use_llvm = test_llvm,
        .root_module = test_module,
    });
    b.step("check-abi", "Compile NativeCore ABI tests without running").dependOn(&native_core_tests.step);
    const smart_coordinator_tests = b.addTest(.{
        .use_llvm = test_llvm,
        .root_module = b.createModule(.{
            .root_source_file = b.path("src/smart_coordinator_test_root.zig"),
            .target = target,
            .optimize = optimize,
        }),
    });
    const transaction_journal_tests = b.addTest(.{
        .use_llvm = test_llvm,
        .root_module = b.createModule(.{
            .root_source_file = b.path("src/transaction_journal_test_root.zig"),
            .target = target,
            .optimize = optimize,
        }),
    });
    const run_tests = b.addRunArtifact(native_core_tests);
    const run_smart_coordinator_tests = b.addRunArtifact(smart_coordinator_tests);
    const run_transaction_journal_tests = b.addRunArtifact(transaction_journal_tests);
    const metric_owner_test_module = b.createModule(.{
        .root_source_file = b.path("src/metric_owner_test_root.zig"),
        .target = target,
        .optimize = optimize,
    });
    metric_owner_test_module.addImport("rm_shared_section", shared_section_module);
    const metric_owner_tests = b.addTest(.{ .name = "metric-owner-tests", .use_llvm = test_llvm, .root_module = metric_owner_test_module });
    const run_metric_owner_tests = b.addRunArtifact(metric_owner_tests);
    b.step("test-metric-owner", "Run hardware metric owner migration gates").dependOn(&run_metric_owner_tests.step);

    const metric_owner_support_module = b.createModule(.{
        .root_source_file = b.path("src/metric_owner_support_test_root.zig"),
        .target = target,
        .optimize = optimize,
    });
    metric_owner_support_module.addImport("rm_shared_section", shared_section_module);
    const metric_owner_support_tests = b.addTest(.{ .name = "metric-owner-support-tests", .use_llvm = test_llvm, .root_module = metric_owner_support_module });
    const run_metric_owner_support_tests = b.addRunArtifact(metric_owner_support_tests);
    b.step("test-metric-owner-support", "Run metric history and checkpoint gates").dependOn(&run_metric_owner_support_tests.step);

    const metric_owner_discovery_module = b.createModule(.{
        .root_source_file = b.path("src/metric_owner_discovery_test_root.zig"),
        .target = target,
        .optimize = optimize,
    });
    metric_owner_discovery_module.addImport("rm_shared_section", shared_section_module);
    const metric_owner_discovery_tests = b.addTest(.{ .name = "metric-owner-discovery-tests", .use_llvm = test_llvm, .root_module = metric_owner_discovery_module });
    const run_metric_owner_discovery_tests = b.addRunArtifact(metric_owner_discovery_tests);
    b.step("test-metric-owner-discovery", "Run metric catalog selection and inventory gates").dependOn(&run_metric_owner_discovery_tests.step);

    const metric_owner_vector_module = b.createModule(.{
        .root_source_file = b.path("src/metric_owner_vector_test_root.zig"),
        .target = target,
        .optimize = optimize,
    });
    metric_owner_vector_module.addImport("rm_shared_section", shared_section_module);
    const metric_owner_vector_tests = b.addTest(.{ .name = "metric-owner-vectors-tests", .use_llvm = test_llvm, .root_module = metric_owner_vector_module });
    const run_metric_owner_vector_tests = b.addRunArtifact(metric_owner_vector_tests);
    b.step("test-metric-owner-vectors", "Run metric engine vectors and derived input gates").dependOn(&run_metric_owner_vector_tests.step);

    const metric_owner_sdk_module = b.createModule(.{
        .root_source_file = b.path("src/metric_owner_sdk_dispatch_test_root.zig"),
        .target = target,
        .optimize = optimize,
    });
    metric_owner_sdk_module.addImport("rm_shared_section", shared_section_module);
    const metric_owner_sdk_tests = b.addTest(.{ .name = "metric-owner-sdk-dispatch-tests", .use_llvm = test_llvm, .root_module = metric_owner_sdk_module });
    const run_metric_owner_sdk_tests = b.addRunArtifact(metric_owner_sdk_tests);
    b.step("test-metric-owner-sdk-dispatch", "Run metric SDK workspace grouping gates").dependOn(&run_metric_owner_sdk_tests.step);

    const metric_owner_equivalence_module = b.createModule(.{
        .root_source_file = b.path("src/metric_owner_application_equivalence_test_root.zig"),
        .target = target,
        .optimize = optimize,
    });
    metric_owner_equivalence_module.addImport("rm_shared_section", shared_section_module);
    const metric_owner_equivalence_tests = b.addTest(.{
        .name = "metric-owner-equivalence-tests",
        .use_llvm = test_llvm,
        .root_module = metric_owner_equivalence_module,
        .filters = &.{"application equivalence"},
    });
    const run_metric_owner_equivalence_tests = b.addRunArtifact(metric_owner_equivalence_tests);
    b.step("test-metric-owner-equivalence", "Run legacy and native metric application equivalence gates").dependOn(&run_metric_owner_equivalence_tests.step);

    // Queue jobs consume these precompiled artifacts, without compiling or
    // running any test while this step prepares them.
    const build_metric_owner_tests = b.step("build-metric-owner-tests", "Compile and install all six metric owner test suites without running");
    const metric_owner_suites = .{
        .{ metric_owner_tests, "metric-owner-tests.exe" },
        .{ metric_owner_support_tests, "metric-owner-support-tests.exe" },
        .{ metric_owner_discovery_tests, "metric-owner-discovery-tests.exe" },
        .{ metric_owner_vector_tests, "metric-owner-vectors-tests.exe" },
        .{ metric_owner_sdk_tests, "metric-owner-sdk-dispatch-tests.exe" },
        .{ metric_owner_equivalence_tests, "metric-owner-equivalence-tests.exe" },
    };
    inline for (metric_owner_suites) |suite| {
        const install_suite = b.addInstallArtifact(suite[0], .{
            .dest_dir = .{ .override = .{ .custom = "tests" } },
            .dest_sub_path = suite[1],
        });
        build_metric_owner_tests.dependOn(&install_suite.step);
    }

    const test_step = b.step("test", "Run NativeCore tests");
    test_step.dependOn(&run_tests.step);
    @import("build_invocation.zig").add(b, target, optimize, test_llvm, native_core_module, test_module, test_step);

    const cpu_monopoly_module = b.createModule(.{
        .root_source_file = b.path("src/control/cpu_monopoly_protection/core.zig"),
        .target = target,
        .optimize = optimize,
    });
    const cpu_monopoly_tests = b.addTest(.{
        .use_llvm = test_llvm,
        .root_module = b.createModule(.{
            .root_source_file = b.path("../../../tests/Native/control/cpu_monopoly_protection_test.zig"),
            .target = target,
            .optimize = optimize,
            .imports = &.{.{ .name = "cpu_monopoly_protection", .module = cpu_monopoly_module }},
        }),
    });
    b.step("check-cpu-monopoly-protection", "Compile CPU monopoly protection tests").dependOn(&cpu_monopoly_tests.step);
    const run_cpu_monopoly_tests = b.addRunArtifact(cpu_monopoly_tests);
    b.step("test-cpu-monopoly-protection", "Run CPU monopoly protection tests").dependOn(&run_cpu_monopoly_tests.step);
    test_step.dependOn(&run_cpu_monopoly_tests.step);
    const device_restart_module = b.createModule(.{
        .root_source_file = b.path("src/platform/windows/device_restart/core.zig"),
        .target = target,
        .optimize = optimize,
    });
    const device_action_module = b.createModule(.{
        .root_source_file = b.path("src/reporting/device_actions/core.zig"),
        .target = target,
        .optimize = optimize,
    });
    const crash_dump_keys_module = b.createModule(.{
        .root_source_file = b.path("src/crash_dump_keys_test_root.zig"),
        .target = target,
        .optimize = optimize,
    });
    const freeze_check = b.step("check-freeze-device-actions", "Compile F5/F6 tests without running device or registry operations");
    const freeze_test = b.step("test-freeze-device-actions", "Run F5/F6 fakes and HKCU test-key registry round trips");
    const specs = .{
        .{ "device_restart", "../../../tests/Native/platform/device_restart_test.zig", device_restart_module },
        .{ "device_actions", "../../../tests/Native/reporting/device_actions_test.zig", device_action_module },
        .{ "crash_dump_keys", "../../../tests/Native/platform/crash_dump_keys_test.zig", crash_dump_keys_module },
    };
    inline for (specs) |spec| {
        const m = b.createModule(.{ .root_source_file = b.path(spec[1]), .target = target, .optimize = optimize, .imports = &.{.{ .name = spec[0], .module = spec[2] }} });
        m.linkSystemLibrary("advapi32", .{});
        const artifact = b.addTest(.{ .root_module = m, .use_llvm = test_llvm });
        freeze_check.dependOn(&artifact.step);
        const run = b.addRunArtifact(artifact);
        freeze_test.dependOn(&run.step);
        test_step.dependOn(&run.step);
    }
    const interaction_access_module = b.createModule(.{
        .root_source_file = b.path("src/interaction/capabilities/access_abi.zig"),
        .target = target,
        .optimize = optimize,
    });
    const interaction_access_tests = b.addTest(.{
        .use_llvm = test_llvm,
        .root_module = b.createModule(.{
            .root_source_file = b.path("../../../tests/Native/interaction/access_privilege_test.zig"),
            .target = target,
            .optimize = optimize,
            .imports = &.{.{ .name = "interaction_access", .module = interaction_access_module }},
        }),
    });
    b.step("check-access", "Compile the access policy tests without running").dependOn(&interaction_access_tests.step);
    const run_interaction_access_tests = b.addRunArtifact(interaction_access_tests);
    b.step("test-access", "Run privilege, desktop and process permission policy tests").dependOn(&run_interaction_access_tests.step);
    test_step.dependOn(&run_interaction_access_tests.step);

    const loopback_tcp_tests = b.addTest(.{
        .use_llvm = test_llvm,
        .root_module = b.createModule(.{
            .root_source_file = b.path("src/loopback_tcp_test_root.zig"),
            .target = target,
            .optimize = optimize,
        }),
    });
    const run_loopback_tcp_tests = b.addRunArtifact(loopback_tcp_tests);
    b.step("test-loopback-tcp", "Run connected TCP owner tests").dependOn(&run_loopback_tcp_tests.step);
    test_step.dependOn(&run_loopback_tcp_tests.step);
    const pdh_gpu_module = b.createModule(.{
        .root_source_file = b.path("src/pdh_gpu_test_root.zig"),
        .target = target,
        .optimize = optimize,
    });
    pdh_gpu_module.linkSystemLibrary("pdh", .{});
    pdh_gpu_module.linkSystemLibrary("iphlpapi", .{});
    const pdh_gpu_tests = b.addTest(.{ .root_module = pdh_gpu_module, .use_llvm = test_llvm });
    b.step("check-pdh-gpu", "Compile PDH GPU owner tests").dependOn(&pdh_gpu_tests.step);
    const run_pdh_gpu_tests = b.addRunArtifact(pdh_gpu_tests);
    b.step("test-pdh-gpu", "Run PDH GPU owner tests").dependOn(&run_pdh_gpu_tests.step);
    test_step.dependOn(&run_pdh_gpu_tests.step);
    const crash_reports_module = b.createModule(.{
        .root_source_file = b.path("src/interaction/http/crash_reports.zig"),
        .target = target,
        .optimize = optimize,
    });
    const crash_reports_tests = b.addTest(.{
        .use_llvm = test_llvm,
        .root_module = b.createModule(.{
            .root_source_file = b.path("tests/crash_reports_test.zig"),
            .target = target,
            .optimize = optimize,
            .imports = &.{.{ .name = "crash_reports", .module = crash_reports_module }},
        }),
    });
    const run_crash_reports_tests = b.addRunArtifact(crash_reports_tests);
    b.step("test-crash-reports", "Run the crash-report HTTP codec tests").dependOn(&run_crash_reports_tests.step);
    test_step.dependOn(&run_crash_reports_tests.step);

    const targeted_recording_csv_module = b.createModule(.{
        .root_source_file = b.path("src/interaction/exports/targeted_recording_csv.zig"),
        .target = target,
        .optimize = optimize,
    });
    const targeted_recording_csv_tests = b.addTest(.{
        .use_llvm = test_llvm,
        .root_module = b.createModule(.{
            .root_source_file = b.path("../../../tests/Native/interaction/targeted_recording_csv_test.zig"),
            .target = target,
            .optimize = optimize,
            .imports = &.{.{ .name = "targeted_recording_csv", .module = targeted_recording_csv_module }},
        }),
    });
    const run_targeted_recording_csv_tests = b.addRunArtifact(targeted_recording_csv_tests);
    b.step("check-targeted-recording-csv", "Compile ordinary targeted-recording CSV tests")
        .dependOn(&targeted_recording_csv_tests.step);
    b.step("test-targeted-recording-csv", "Run ordinary targeted-recording CSV tests")
        .dependOn(&run_targeted_recording_csv_tests.step);
    test_step.dependOn(&run_targeted_recording_csv_tests.step);

    const hook_wait_module = b.createModule(.{
        .root_source_file = b.path("src/interaction/http/hook_wait_abi.zig"),
        .target = target,
        .optimize = optimize,
    });
    const hook_wait_tests = b.addTest(.{
        .use_llvm = test_llvm,
        .root_module = b.createModule(.{
            .root_source_file = b.path("../../../tests/Native/interaction/hook_wait_test.zig"),
            .target = target,
            .optimize = optimize,
            .imports = &.{.{ .name = "hook_wait", .module = hook_wait_module }},
        }),
    });
    b.step("check-hook-wait", "Compile hook-wait HTTP response tests").dependOn(&hook_wait_tests.step);
    const run_hook_wait_tests = b.addRunArtifact(hook_wait_tests);
    b.step("test-hook-wait", "Run hook-wait HTTP response tests").dependOn(&run_hook_wait_tests.step);
    test_step.dependOn(&run_hook_wait_tests.step);

    const cpu_os_abi_module = b.createModule(.{
        .root_source_file = b.path("src/platform/windows/cpu_features_abi.zig"),
        .target = target,
        .optimize = optimize,
    });
    const cpu_os_test_module = b.createModule(.{
        .root_source_file = b.path("../../../tests/Native/platform/cpu_features_test.zig"),
        .target = target,
        .optimize = optimize,
        .imports = &.{.{ .name = "cpu_features_abi", .module = cpu_os_abi_module }},
    });
    cpu_os_test_module.linkSystemLibrary("kernel32", .{});
    const cpu_os_tests = b.addTest(.{ .use_llvm = test_llvm, .root_module = cpu_os_test_module });
    b.step("check-cpu-os-features", "Compile Windows CPU OS feature tests").dependOn(&cpu_os_tests.step);
    const run_cpu_os_tests = b.addRunArtifact(cpu_os_tests);
    b.step("test-cpu-os-features", "Run Windows CPU OS feature tests").dependOn(&run_cpu_os_tests.step);
    test_step.dependOn(&run_cpu_os_tests.step);

    const crash_parameters_module = b.createModule(.{
        .root_source_file = b.path("src/interaction/settings/crash_reports_abi.zig"),
        .target = target,
        .optimize = optimize,
    });
    const crash_parameters_tests = b.addTest(.{
        .use_llvm = test_llvm,
        .root_module = b.createModule(.{
            .root_source_file = b.path("../../../tests/Native/interaction/crash_report_parameters_test.zig"),
            .target = target,
            .optimize = optimize,
            .imports = &.{.{ .name = "crash_report_parameters", .module = crash_parameters_module }},
        }),
    });
    b.step("check-crash-report-parameters", "Compile crash parameter boundary tests").dependOn(&crash_parameters_tests.step);
    const run_crash_parameters_tests = b.addRunArtifact(crash_parameters_tests);
    b.step("test-crash-report-parameters", "Run crash parameter boundary tests").dependOn(&run_crash_parameters_tests.step);
    test_step.dependOn(&run_crash_parameters_tests.step);

    const resource_bars_module = b.createModule(.{
        .root_source_file = b.path("src/resource_breakdown/current_bars.zig"),
        .target = target,
        .optimize = optimize,
    });
    const resource_bars_tests = b.addTest(.{
        .use_llvm = test_llvm,
        .root_module = b.createModule(.{
            .root_source_file = b.path("../../../tests/Native/interaction/resource_bars_test.zig"),
            .target = target,
            .optimize = optimize,
            .imports = &.{.{ .name = "resource_bars", .module = resource_bars_module }},
        }),
    });
    b.step("check-resource-bars", "Compile current resource-bar projection tests").dependOn(&resource_bars_tests.step);
    const run_resource_bars_tests = b.addRunArtifact(resource_bars_tests);
    b.step("test-resource-bars", "Test current resource-bar projection").dependOn(&run_resource_bars_tests.step);
    test_step.dependOn(&run_resource_bars_tests.step);

    const tier_view_module = b.createModule(.{
        .root_source_file = b.path("src/memory_cpu_tier_test_root.zig"),
        .target = target,
        .optimize = optimize,
    });
    const tier_view_tests = b.addTest(.{
        .use_llvm = test_llvm,
        .root_module = b.createModule(.{
            .root_source_file = b.path("tests/memory_cpu_tier_test.zig"),
            .target = target,
            .optimize = optimize,
            .imports = &.{.{ .name = "memory_cpu_tier", .module = tier_view_module }},
        }),
    });
    const run_tier_view_tests = b.addRunArtifact(tier_view_tests);
    b.step("test-memory-cpu-tier", "Run Z2 query view and final-output borrowing tests").dependOn(&run_tier_view_tests.step);
    test_step.dependOn(&run_tier_view_tests.step);
    const crash_capabilities_module = b.createModule(.{
        .root_source_file = b.path("src/crash_capabilities_test_root.zig"),
        .target = target,
        .optimize = optimize,
    });
    const crash_capabilities_tests = b.addTest(.{
        .use_llvm = test_llvm,
        .root_module = b.createModule(.{
            .root_source_file = b.path("tests/crash_capabilities_test.zig"),
            .target = target,
            .optimize = optimize,
            .imports = &.{.{ .name = "crash_capabilities", .module = crash_capabilities_module }},
        }),
    });
    const run_crash_capabilities_tests = b.addRunArtifact(crash_capabilities_tests);
    b.step("test-crash-capabilities", "Run the shared crash capability binding tests").dependOn(&run_crash_capabilities_tests.step);
    test_step.dependOn(&run_crash_capabilities_tests.step);

    const data_area_test_module = b.createModule(.{
        .root_source_file = b.path("src/data_area_test_root.zig"),
        .target = target,
        .optimize = optimize,
    });
    data_area_test_module.addImport("rm_shared_section", shared_section_module);
    const data_area_tests = b.addTest(.{ .use_llvm = test_llvm, .root_module = data_area_test_module });
    const run_data_area_tests = b.addRunArtifact(data_area_tests);
    b.step("test-data-area", "Run the data area region owner tests").dependOn(&run_data_area_tests.step);
    test_step.dependOn(&run_data_area_tests.step);

    const control_abi_test_module = b.createModule(.{
        .root_source_file = b.path("src/control_abi_test_root.zig"),
        .target = target,
        .optimize = .fast,
    });
    control_abi_test_module.addImport("rm_control", control_module);
    control_abi_test_module.addImport("backend_tree", backend_tree_module);
    control_abi_test_module.addImport("backend_declarations", backend_declarations_module);
    const control_abi_tests = b.addTest(.{ .root_module = control_abi_test_module, .use_llvm = test_llvm });
    const run_control_abi_tests = b.addRunArtifact(control_abi_tests);
    test_step.dependOn(&run_control_abi_tests.step);
    test_step.dependOn(&run_smart_coordinator_tests.step);
    test_step.dependOn(&run_transaction_journal_tests.step);

    // Named imported modules do not contribute their own test declarations to
    // the ABI test roots. Run the production decision oracle and tray cases explicitly.
    const backend_rule_tests = b.addTest(.{
        .name = "backend-decision-rules",
        .use_llvm = false,
        .root_module = backend_tree_module,
    });
    const run_backend_rule_tests = b.addRunArtifact(backend_rule_tests);
    test_step.dependOn(&run_backend_rule_tests.step);
    const check_control = b.step("check-control", "Compile control tests without running");
    check_control.dependOn(&control_abi_tests.step);
    check_control.dependOn(&backend_rule_tests.step);
    const install_control = b.step("install-control-tests", "Install focused control test executables");
    install_control.dependOn(&b.addInstallArtifact(control_abi_tests, .{}).step);
    install_control.dependOn(&b.addInstallArtifact(backend_rule_tests, .{}).step);
    const control_test_step = b.step("test-control", "Run control ABI and backend decision oracle tests");
    control_test_step.dependOn(&run_control_abi_tests.step);
    control_test_step.dependOn(&run_backend_rule_tests.step);
    const kernel_etw_module = b.createModule(.{
        .root_source_file = b.path("src/kernel_etw_test_root.zig"),
        .target = target,
        .optimize = optimize,
    });
    kernel_etw_module.linkSystemLibrary("advapi32", .{});
    kernel_etw_module.linkSystemLibrary("ntdll", .{});
    const kernel_etw_tests = b.addTest(.{ .root_module = kernel_etw_module, .use_llvm = test_llvm });
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
    run_calibration_tool.addPassthruArgs();

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
    run_assembly_tool.addPassthruArgs();

    const assembly_step = b.step(
        "assemble-calibration-corpus",
        "Assemble a canonical calibration corpus from ordinal workload observations",
    );
    assembly_step.dependOn(&run_assembly_tool.step);
}

// The HTTP ABI is part of the product library, so its parser dependency must
// also be present when building abi.zig without a verification overlay.
pub fn addHttpParser(b: *std.Build, module: *std.Build.Module, target: std.Build.ResolvedTarget, optimize: std.builtin.OptimizeMode) void {
    const translated = b.addTranslateC(.{ .root_source_file = b.path("../../../build/dependencies/llhttp/include/llhttp.h"), .target = target, .optimize = optimize });
    module.addImport("llhttp", translated.createModule());
    module.addIncludePath(b.path("../../../build/dependencies/llhttp/include"));
    module.addCSourceFiles(.{ .root = b.path("../../../build/dependencies/llhttp"), .files = &.{ "src/api.c", "src/http.c", "src/llhttp.c" }, .flags = &.{"-std=c99"} });
    module.link_libc = true;
    module.linkSystemLibrary("ws2_32", .{});
}
