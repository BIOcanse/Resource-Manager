const std = @import("std");

pub fn build(b: *std.Build) void {
    const target = b.standardTargetOptions(.{});
    const optimize = b.standardOptimizeOption(.{});
    const state_names = b.option(bool, "state-names", "Include development registry names") orelse (optimize == .Debug);
    if (state_names and optimize != .Debug) @panic("Registry names are development-only; release builds require -Dstate-names=false");
    const control = b.createModule(.{
        .root_source_file = b.path("../../Shared/Control/src/root.zig"),
        .target = target,
        .optimize = .ReleaseSafe,
    });
    const module = b.createModule(.{ .root_source_file = b.path("src/main.zig"), .target = target, .optimize = optimize });
    const states = stateItems(b, control, target, optimize, state_names);
    module.addImport("desktop_state_items", states);
    configure(module, control);
    if (b.option([]const u8, "points-root", "Directory containing selected FreedomPoint values")) |root| {
        const values = b.createModule(.{ .root_source_file = .{ .cwd_relative = b.pathJoin(&.{ root, "points.values.zig" }) }, .target = target, .optimize = optimize });
        const structure = b.createModule(.{ .root_source_file = b.path("src/generated/points.structure.zig"), .target = target, .optimize = optimize });
        values.addImport("desktop_points_structure", structure);
        module.addImport("desktop_points_values", values);
    }
    const executable = b.addExecutable(.{ .name = "ResourceManager.Desktop", .root_module = module });
    executable.subsystem = .Windows;
    b.installArtifact(executable);
    const tests = b.createModule(.{ .root_source_file = b.path("src/tests.zig"), .target = target, .optimize = optimize });
    tests.addImport("desktop_state_items", states);
    configure(tests, control);
    const run = b.addRunArtifact(b.addTest(.{ .root_module = tests }));
    const test_step = b.step("test", "Run resident tree and block tests");
    test_step.dependOn(&run.step);
    const pipe = b.createModule(.{ .root_source_file = b.path("src/window_process/root.zig"), .target = target, .optimize = optimize });
    const fixture_module = b.createModule(.{ .root_source_file = b.path("../../../tests/Desktop/window_process_child.zig"), .target = target, .optimize = optimize });
    fixture_module.addImport("window_process", pipe);
    const fixture = b.addExecutable(.{ .name = "window-process-fixture", .root_module = fixture_module });
    const parent_module = b.createModule(.{ .root_source_file = b.path("../../../tests/Desktop/window_process_parent.zig"), .target = target, .optimize = optimize });
    parent_module.addImport("window_process", pipe);
    const parent = b.addExecutable(.{ .name = "window-process-parent-fixture", .root_module = parent_module });
    const scenario_module = b.createModule(.{ .root_source_file = b.path("../../../tests/Desktop/window_process_tests.zig"), .target = target, .optimize = optimize });
    scenario_module.addImport("window_process", pipe);
    const options = b.addOptions();
    options.addOptionPath("path", fixture.getEmittedBin());
    options.addOptionPath("parentPath", parent.getEmittedBin());
    options.addOption([]const u8, "realNativeUi", b.option([]const u8, "native-ui", "Debug NativeUi executable for explicit real-child acceptance") orelse "");
    scenario_module.addOptions("fixture_options", options);
    const pipe_filter = b.option([]const u8, "pipe-filter", "Run only matching pipe tests after a failure");
    const scenario_run = b.addRunArtifact(b.addTest(.{ .root_module = scenario_module, .filters = if (pipe_filter) |filter| &.{filter} else &.{} }));
    test_step.dependOn(&scenario_run.step);
    const desktop_tree = b.createModule(.{ .root_source_file = b.path("src/tree.zig"), .target = target, .optimize = optimize });
    desktop_tree.addImport("rm_control", control);
    desktop_tree.addImport("desktop_state_items", states);
    // Export always uses a separate development table, even if the product
    // table omits names. The exporter is never installed with the resident.
    const export_tree = b.createModule(.{ .root_source_file = b.path("src/tree.zig"), .target = target, .optimize = .ReleaseSafe });
    export_tree.addImport("rm_control", control);
    export_tree.addImport("desktop_state_items", stateItems(b, control, target, .ReleaseSafe, true));
    const export_source = b.createModule(.{
        .root_source_file = b.path("src/export_tree.zig"),
        .target = target,
        .optimize = .ReleaseSafe,
        .imports = &.{ .{ .name = "rm_control", .module = control }, .{ .name = "desktop_tree", .module = export_tree } },
    });
    const exporter = b.addExecutable(.{
        .name = "export-control-tree",
        .use_llvm = false,
        .root_module = b.createModule(.{
            .root_source_file = b.path("../../Shared/Control/src/export_tree_main.zig"),
            .target = target,
            .optimize = .ReleaseSafe,
            .imports = &.{ .{ .name = "rm_control", .module = control }, .{ .name = "process_tree", .module = export_source } },
        }),
    });
    const run_exporter = b.addRunArtifact(exporter);
    b.step("export-tree", "Export the resident root and its window process launch").dependOn(&run_exporter.step);
    const export_for_test = b.addRunArtifact(exporter);
    const export_check = b.addSystemCommand(&.{ "pwsh", "-NoProfile", "-File" });
    export_check.addFileArg(b.path("../../../tests/Desktop/ResidentControlTreeExport.Tests.ps1"));
    export_check.addArg("-TreeJson");
    export_check.addFileArg(export_for_test.captureStdOut(.{}));
    b.step("test-export", "Check the actual resident export and window launch").dependOn(&export_check.step);
    test_step.dependOn(&export_check.step);
    const integration = b.createModule(.{ .root_source_file = b.path("../../../tests/Desktop/resident_tree_scenarios.zig"), .target = target, .optimize = optimize });
    integration.addImport("rm_control", control);
    integration.addImport("desktop_tree", desktop_tree);
    integration.addImport("window_process", pipe);
    integration.addOptions("fixture_options", options);
    const integration_filter = b.option([]const u8, "integration-filter", "Run only matching resident tree scenarios");
    const integration_run = b.addRunArtifact(b.addTest(.{ .root_module = integration, .filters = if (integration_filter) |filter| &.{filter} else &.{} }));
    test_step.dependOn(&integration_run.step);
    b.step("test-integration", "Run the resident controller with real child processes").dependOn(&integration_run.step);
    b.step("test-blocks", "Run resident declarations and block unit tests").dependOn(&run.step);
    const key_fixture = b.createModule(.{ .root_source_file = b.path("src/backend/key_fixture_test.zig"), .target = target, .optimize = optimize });
    configure(key_fixture, control);
    const key_fixture_tests = b.addTest(.{ .root_module = key_fixture, .filters = &.{"prepared trusted"} });
    const key_fixture_run = b.addRunArtifact(key_fixture_tests);
    b.step("check-key-fixture", "Compile protected key acceptance without running it").dependOn(&key_fixture_tests.step);
    b.step("test-key-fixture", "Read prepared protected key storage as an ordinary user (explicit acceptance)").dependOn(&key_fixture_run.step);
    b.step("test-pipe", "Run deterministic real parent/child pipe scenarios").dependOn(&scenario_run.step);
    const native_ui = b.createModule(.{ .root_source_file = b.path("../../../tests/Desktop/native_ui_process_test.zig"), .target = target, .optimize = optimize });
    native_ui.addImport("window_process", pipe);
    native_ui.addOptions("fixture_options", options);
    native_ui.linkSystemLibrary("user32", .{});
    native_ui.link_libc = true;
    const native_ui_run = b.addRunArtifact(b.addTest(.{ .root_module = native_ui, .filters = &.{"secondary display"} }));
    b.step("test-native-ui", "Run an explicitly selected real Debug NativeUi child on a secondary display").dependOn(&native_ui_run.step);
    const native_ui_headless = b.addRunArtifact(b.addTest(.{ .root_module = native_ui, .filters = &.{"headless"} }));
    b.step("test-native-ui-headless", "Run a real Debug NativeUi with parent demand always false and no visible window").dependOn(&native_ui_headless.step);
    b.step("fixture", "Install the pipe test child").dependOn(&b.addInstallArtifact(fixture, .{}).step);
}

fn configure(module: *std.Build.Module, control: *std.Build.Module) void {
    module.addImport("rm_control", control);
    module.link_libc = true;
    for ([_][]const u8{ "user32", "shell32", "advapi32", "winhttp", "iphlpapi", "ws2_32", "ole32" }) |library|
        module.linkSystemLibrary(library, .{});
}

fn stateItems(b: *std.Build, control: *std.Build.Module, target: std.Build.ResolvedTarget, optimize: std.builtin.OptimizeMode, state_names: bool) *std.Build.Module {
    const generation = b.addSystemCommand(&.{ "pwsh", "-NoProfile", "-File" });
    generation.addFileArg(b.path("../../../scripts/build/Generate-DesktopStateItems.ps1"));
    generation.addFileInput(b.path("../../Shared/StateItems/state-items.json"));
    generation.addFileInput(b.path("../../../scripts/build/Generate-StateItems.ps1"));
    generation.addFileInput(b.path("../../../scripts/build/StateItemRegistry.psm1"));
    generation.addArg("-Output");
    const state_file = generation.addOutputFileArg("state_items.zig");
    if (state_names) generation.addArg("-Names");
    const states = b.createModule(.{ .root_source_file = state_file, .target = target, .optimize = optimize });
    states.addImport("rm_control", control);
    return states;
}
