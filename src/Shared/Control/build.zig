const std = @import("std");

pub fn build(b: *std.Build) void {
    const target = b.standardTargetOptions(.{});
    // This boundary always retains assertions, including ReleaseFast consumers.
    const control = b.addModule("rm_control", .{
        .root_source_file = b.path("src/root.zig"),
        .target = target,
        .optimize = .safe,
    });
    const tests = b.addTest(.{ .root_module = b.createModule(.{
        .root_source_file = b.path("tests/root.zig"),
        .target = target,
        .optimize = .safe,
        .imports = &.{.{ .name = "rm_control", .module = control }},
    }) });
    const run_tests = b.addRunArtifact(tests);
    const test_step = b.step("test", "Run controller scenarios and compile-time rejection tests");
    test_step.dependOn(&run_tests.step);
    const json_tests = b.addTest(.{ .root_module = b.createModule(.{
        .root_source_file = b.path("src/json.zig"),
        .target = target,
        .optimize = .safe,
    }) });
    const run_json_tests = b.addRunArtifact(json_tests);
    test_step.dependOn(&run_json_tests.step);

    const fixtures = [_]struct { file: []const u8, message: []const u8 }{
        .{ .file = "unknown_parent", .message = "control tree parent is not a registered block" },
        .{ .file = "unknown_input", .message = "control tree input is not a registered block" },
        .{ .file = "missing_block", .message = "control tree must contain every registered block exactly once" },
        .{ .file = "duplicate_block", .message = "control tree must contain every registered block exactly once" },
        .{ .file = "multiple_roots", .message = "control tree root must be the program block" },
        .{ .file = "parent_cycle", .message = "control tree parents contain a cycle" },
        .{ .file = "missing_top", .message = "every direct child must have exactly one top condition" },
        .{ .file = "duplicate_top", .message = "every direct child must have exactly one top condition" },
        .{ .file = "non_direct_top", .message = "top condition must decide a direct child of its node" },
        .{ .file = "non_block_top", .message = "top condition must decide a registered block" },
        .{ .file = "over_limit", .message = "decision node has more than 64 condition instances" },
        .{ .file = "per_block_over_limit", .message = "decision node has more than 64 condition instances" },
        .{ .file = "condition_cycle", .message = "decision node conditions contain a cycle" },
        .{ .file = "host_item_read", .message = "conditions cannot read host-valued items" },
        .{ .file = "unknown_item", .message = "condition reads an unregistered item" },
        .{ .file = "unknown_condition", .message = "condition reads an unregistered condition" },
        .{ .file = "missing_compute", .message = "every condition must have exactly one compute function" },
        .{ .file = "duplicate_compute", .message = "every condition must have exactly one compute function" },
    };
    for (fixtures) |fixture| {
        const compile = b.addObject(.{ .name = fixture.file, .root_module = b.createModule(.{
            .root_source_file = b.path(b.fmt("tests/compile_fail/{s}.zig", .{fixture.file})),
            .target = target,
            .optimize = .safe,
            .imports = &.{.{ .name = "rm_control", .module = control }},
        }) });
        compile.expect_errors = .{ .contains = fixture.message };
        test_step.dependOn(&compile.step);
    }
    const valid_data_cycle = b.addObject(.{ .name = "valid_data_cycle", .root_module = b.createModule(.{
        .root_source_file = b.path("tests/compile_fail/valid_data_cycle.zig"),
        .target = target,
        .optimize = .fast,
        .imports = &.{.{ .name = "rm_control", .module = control }},
    }) });
    test_step.dependOn(&valid_data_cycle.step);
}
