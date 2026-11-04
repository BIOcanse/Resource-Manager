//! B-generate bootstrap only. Product/package roots remain with their integrator.
const std = @import("std");
const generation = @import("declarations/build_support.zig");

pub fn build(b: *std.Build) void {
    const target = b.standardTargetOptions(.{});
    const optimize = b.standardOptimizeOption(.{});
    const generator = generation.tool(b, b.path("declarations/main.zig"));
    b.installArtifact(generator);
    // Build-tool input limit, explicitly overridable; not runtime product behavior.
    const maximum = b.option(usize, "declaration-max-bytes", "Maximum declaration input size") orelse 16 * 1024 * 1024;
    const states = b.createModule(.{ .root_source_file = b.path("declarations/state_items.zig"), .target = target, .optimize = optimize });
    const frontend = b.createModule(.{ .root_source_file = b.path("declarations/frontend_defaults.zig"), .target = target, .optimize = optimize });
    const production = b.createModule(.{ .root_source_file = b.path("../../src/Shared/Control/src/root.zig"), .target = target, .optimize = .safe });
    const cases = b.createModule(.{ .root_source_file = b.path("../../tests/Native/Generation/declarations_tests.zig"), .target = target, .optimize = optimize });
    cases.addImport("state_generator", states);
    cases.addImport("frontend_generator", frontend);
    // Keep fixture contents as tracked build inputs. Reading during configure
    // would both require the removed LazyPath.getPath API and miss cache changes.
    const fixture_files = b.addWriteFiles();
    _ = fixture_files.addCopyFile(b.path("../../src/Shared/StateItems/state-items.json"), "state-items.json");
    _ = fixture_files.addCopyFile(b.path("../../src/UI/Frontend/preferences.points.json"), "preferences.points.json");
    _ = fixture_files.addCopyFile(b.path("../../src/UI/Desktop/src/generated/frontend_defaults.zig"), "frontend_defaults.zig");
    const fixture = fixture_files.add("fixture.zig",
        \\pub const registry_bytes = @embedFile("state-items.json");
        \\pub const preferences_bytes = @embedFile("preferences.points.json");
        \\pub const expected_defaults = @embedFile("frontend_defaults.zig");
    );
    cases.addImport("fixture", b.createModule(.{ .root_source_file = fixture, .target = target, .optimize = optimize }));
    const generate = b.step("generate", "Generate all process state tables and frontend defaults from authoritative inputs");
    inline for (.{ "backend", "ui", "desktop", "window" }) |process| {
        inline for (.{ true, false }) |names| {
            const file = generation.stateItems(b, generator, b.path("../../src/Shared/StateItems/state-items.json"), process, names, maximum);
            const module = b.createModule(.{ .root_source_file = file, .target = target, .optimize = optimize });
            module.addImport("rm_control", production);
            cases.addImport(process ++ (if (names) "_named" else "_unnamed"), module);
            const install = b.addInstallFile(file, "generated/" ++ process ++ (if (names) "_named.zig" else "_unnamed.zig"));
            generate.dependOn(&install.step);
        }
    }
    const defaults = generation.frontendDefaults(b, generator, b.path("../../src/UI/Frontend/preferences.points.json"), maximum);
    cases.addImport("generated_defaults", b.createModule(.{ .root_source_file = defaults, .target = target, .optimize = optimize }));
    generate.dependOn(&b.addInstallFile(defaults, "generated/frontend_defaults.zig").step);
    const tests = b.addTest(.{ .root_module = cases });
    const install_tests = b.addInstallArtifact(tests, .{ .dest_dir = .{ .override = .{ .custom = "tests" } }, .dest_sub_path = "declarations-tests.exe" });
    const check = b.step("check", "Compile generator and focused cases without running tests");
    check.dependOn(&install_tests.step);
    b.step("test", "Run native generation, invalid-input and real-consumer tests").dependOn(&b.addRunArtifact(tests).step);
}
