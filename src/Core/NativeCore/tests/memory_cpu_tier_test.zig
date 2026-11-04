const std = @import("std");
const http = @import("memory_cpu_tier").http;
const view = http.tier;
const expect = std.testing.expect;
const equal = std.testing.expectEqual;
const u16s = std.unicode.utf8ToUtf16LeStringLiteral;

const Fixture = struct {
    runtime: bool = true,
    running: ?i64 = null,
    next: ?i64 = 4194304,
    conserve: ?i64 = 5,
    next_conserve: i64 = 9,
    unknown: bool = false,
    fail_scalar: bool = false,
    fail_text: bool = false,
    fail_output: bool = false,
    fail_commit: bool = false,
    segment: usize = 31,
    bytes: [8192]u8 = undefined,
    used: usize = 0,
    commits: usize = 0,
    fn self(context: ?*anyopaque) *Fixture {
        return @ptrCast(@alignCast(context.?));
    }
    fn scalar(context: ?*anyopaque, index: usize, child: usize, field: view.Scalar, out: *i64) callconv(.c) i32 {
        const f = self(context);
        if (f.fail_scalar) return 2;
        out.* = switch (field) {
            .tier => 3,
            .option_count => 2,
            .ignored_count => 1,
            .runtime_present, .writable => @intFromBool(f.runtime),
            .file_count => 1,
            .running_gen0_present => @intFromBool(f.running != null),
            .running_gen0 => f.running orelse 0,
            .next_gen0_present => @intFromBool(f.next != null),
            .next_gen0 => f.next orelse 0,
            .running_conserve_present => @intFromBool(f.conserve != null),
            .running_conserve => f.conserve orelse 0,
            .next_conserve => f.next_conserve,
            .value_present, .cell_present => @intFromBool(index == 1 or child < 4),
            .value_integer => if (index == 0) 4194304 else 5,
            .cell_count => 5,
            .cell_integer => @intCast(child),
            .minimum => 0,
            .maximum => 9223372036854775807,
            .unset => @intFromBool(index == 0),
            .choice_count => 0,
        };
        return 0;
    }
    fn text(context: ?*anyopaque, index: usize, _: usize, field: view.Text, sink: ?*anyopaque, visitor: view.Visitor) callconv(.c) i32 {
        const f = self(context);
        if (f.fail_text) return 2;
        const result: ?[]const u16 = switch (field) {
            .name => if (f.unknown) u16s("unknown") else if (index == 0) u16s("gcGen0MaxBudget") else u16s("gcConserveMemory"),
            .unit => u16s("bytes"),
            .kind => u16s("integer"),
            .category, .cell_category => u16s("proportional"),
            .source => if (index == 0) u16s("tier") else u16s("user"),
            .apply => u16s("restart"),
            .value_choice, .cell_choice, .reason, .last_failure => null,
            .not_installed_reason => u16s("not-installed"),
            .native_ui_running => u16s("unavailable"),
            .file_process => u16s("service"),
            .file_path => u16s("D:\\测试\\\"config\""),
            .ignored_code => u16s("unknown-option"),
            .ignored_path => u16s("$.memoryCpuTier.overrides.unknown"),
            .choice => unreachable,
        };
        return if (result) |v| visitor(sink, v.ptr, v.len) else visitor(sink, null, 0);
    }
    fn reserve(context: ?*anyopaque, completed: usize, pointer: *?[*]u8, capacity: *usize) callconv(.c) i32 {
        const f = self(context);
        if (f.fail_output) return 2;
        if (commit(context, completed) != 0 or f.bytes.len - f.used < f.segment) return 2;
        pointer.* = f.bytes[f.used..].ptr;
        capacity.* = f.segment;
        return 0;
    }
    fn commit(context: ?*anyopaque, completed: usize) callconv(.c) i32 {
        const f = self(context);
        if (f.fail_commit) return 2;
        f.used += completed;
        f.commits += 1;
        return 0;
    }
    const callbacks = view.Callbacks{ .scalar = scalar, .text = text };
    const output = http.OutputCallbacks{ .reserve = reserve, .commit = commit };
    fn source(f: *Fixture) view.Source {
        return .{ .context = f, .callbacks = &callbacks };
    }
};

test "published selection and no-writer running/next semantics are independent of HTTP" {
    try expect(view.usePublished(true));
    try expect(!view.usePublished(false));
    var f = Fixture{};
    const v = view.View{ .source = f.source() };
    try equal(view.RuntimeValue{ .running = null, .next = 4194304 }, try v.runtimeValue(0));
    f.runtime = false;
    try equal(view.RuntimeValue{ .running = null, .next = null }, try v.runtimeValue(0));
    f.conserve = null;
    try equal(view.RuntimeValue{ .running = 0, .next = 0 }, try v.runtimeValue(1));
    f.conserve = 4294967301;
    try equal(view.RuntimeValue{ .running = 5, .next = 5 }, try v.runtimeValue(1));
    f.unknown = true;
    try std.testing.expectError(error.UnsupportedRuntimeOption, v.runtimeValue(0));
}

test "final-output segmentation preserves every byte and null running values" {
    var full = Fixture{ .segment = 4096 };
    try equal(@as(i32, 0), http.encodeStream(&full, &Fixture.callbacks, &Fixture.output));
    const sizes = [_]usize{ 1, 7, 31, 127 };
    var index: usize = 0;
    while (index < sizes.len) : (index += 1) {
        var segmented = Fixture{ .segment = sizes[index] };
        try equal(@as(i32, 0), http.encodeStream(&segmented, &Fixture.callbacks, &Fixture.output));
        try std.testing.expectEqualSlices(u8, full.bytes[0..full.used], segmented.bytes[0..segmented.used]);
    }
    const parsed = try std.json.parseFromSlice(std.json.Value, std.testing.allocator, full.bytes[0..full.used], .{});
    defer parsed.deinit();
    const options = parsed.value.object.get("options").?.array.items;
    try expect(std.meta.activeTag(options[0].object.get("runningValue").?) == .null);
    try expect(options[0].object.get("pendingRestart").?.bool);
    try equal(@as(i64, 5), options[1].object.get("runningValue").?.integer);
}

test "source and output failures remain explicit; malformed ABI is rejected" {
    var f = Fixture{ .fail_scalar = true };
    try equal(@as(i32, 2), http.encodeStream(&f, &Fixture.callbacks, &Fixture.output));
    f = .{ .fail_text = true };
    try equal(@as(i32, 2), http.encodeStream(&f, &Fixture.callbacks, &Fixture.output));
    f = .{ .fail_output = true };
    try equal(@as(i32, 2), http.encodeStream(&f, &Fixture.callbacks, &Fixture.output));
    f = .{ .fail_commit = true };
    try equal(@as(i32, 2), http.encodeStream(&f, &Fixture.callbacks, &Fixture.output));
    f = .{ .unknown = true };
    try equal(@as(i32, 5), http.encodeStream(&f, &Fixture.callbacks, &Fixture.output));
    try equal(@as(i32, 1), http.encodeStream(&f, null, &Fixture.output));
    try equal(@as(i32, 1), http.encodeStream(&f, &Fixture.callbacks, null));
}
