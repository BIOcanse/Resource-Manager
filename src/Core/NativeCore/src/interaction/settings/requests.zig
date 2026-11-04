//! Settings capability binding and request/result mapping. The original store
//! and coordinator remain the only state owners (S-store / S-apply).
//! A continuation is a small value, never a retained managed pointer. The host
//! awaits the selected original call before returning its borrowed facts here.
const std = @import("std");
const rules = @import("report_rules_legacy.zig");
const json = @import("../wire/output.zig");

pub export fn rm_settings_report_rules_encode(context: ?*anyopaque, callbacks: ?*const rules.Callbacks, output: ?*const json.OutputCallbacks) callconv(.c) i32 {
    return rules.encode(context, callbacks, output);
}

pub const Operation = enum(u32) { read, write, patch, reapply, defaults };
pub const Action = enum(u32) { begin, load, project_runtime, apply, apply_patch, rebuild, load_reapplied, project_publication, diagnostics, defaults, done };
pub const Disposition = enum(u32) { unchanged, applied, constrained, delivery_failures };
pub const Result = extern struct {
    action: Action = .begin,
    status: u32 = 0,
    disposition: Disposition = .unchanged,
    inspect_crash_dump_keys: u32 = 0,
};
pub const Facts = extern struct {
    invalid_patch: u32,
    delivery_failures: u32,
    constrained: u32,
    disposition: ?[*]const u16,
    disposition_length: usize,
};

pub fn next(operation: u32, completed: u32, facts: *const Facts, output: *Result) i32 {
    output.* = .{};
    if (operation > @backingInt(Operation.defaults) or completed > @backingInt(Action.done) or
        facts.invalid_patch > 1 or facts.delivery_failures > 1 or facts.constrained > 1 or
        (facts.disposition == null and facts.disposition_length != 0)) return 1;
    const op: Operation = @fromBackingInt(operation);
    const previous: Action = @fromBackingInt(completed);
    if (facts.invalid_patch != 0) {
        if (op != .patch or previous != .apply_patch) return 1;
        output.* = .{ .action = .done, .status = 400 };
        return 0;
    }
    const action: Action = switch (previous) {
        .begin => switch (op) {
            .read => .load,
            .write => .apply,
            .patch => .apply_patch,
            .reapply => .rebuild,
            .defaults => .defaults,
        },
        .load => if (op == .read) .project_runtime else return 1,
        .project_runtime => if (op == .read) .diagnostics else return 1,
        .apply => if (op == .write) .diagnostics else return 1,
        .apply_patch => if (op == .patch) .diagnostics else return 1,
        .rebuild => if (op == .reapply) .load_reapplied else return 1,
        .load_reapplied => if (op == .reapply) .project_publication else return 1,
        .project_publication => if (op == .reapply) .diagnostics else return 1,
        .diagnostics => if (op != .defaults) .done else return 1,
        .defaults => if (op == .defaults) .done else return 1,
        .done => return 1,
    };
    output.action = action;
    // Reads/reapply report observed registry state; writes retain the original
    // commit diagnostics (including failed writes and rollback details).
    if (action == .diagnostics and (op == .read or op == .reapply)) output.inspect_crash_dump_keys = 1;
    if (action == .project_publication) output.disposition = if (facts.delivery_failures != 0)
        .delivery_failures
    else if (facts.constrained != 0) .constrained else .applied;
    if (action == .done) output.status = switch (op) {
        .read, .defaults => 200,
        .reapply => if (facts.delivery_failures != 0) 202 else 200,
        .write, .patch => status(facts.disposition, facts.disposition_length),
    };
    return 0;
}

fn equals(text: ?[*]const u16, length: usize, expected: []const u8) bool {
    if (length != expected.len) return false;
    const value = text orelse return false;
    for (expected, 0..) |byte, index| if (value[index] != byte) return false;
    return true;
}

fn status(text: ?[*]const u16, length: usize) u32 {
    if (equals(text, length, "committedAndApplied") or equals(text, length, "committedWithCapabilityConstraints")) return 200;
    if (equals(text, length, "committedWithDeliveryFailures") or equals(text, length, "savedNotApplied")) return 202;
    return 409;
}

// Static protocol identifiers and reason strings are not product defaults.
pub fn getText(kind: u32, value: u32) ?[:0]const u16 {
    return switch (kind) {
        0 => switch (value) {
            @backingInt(Operation.read) => std.unicode.utf8ToUtf16LeStringLiteral("settings.read"),
            @backingInt(Operation.write) => std.unicode.utf8ToUtf16LeStringLiteral("settings.write"),
            @backingInt(Operation.patch) => std.unicode.utf8ToUtf16LeStringLiteral("settings.patch"),
            @backingInt(Operation.reapply) => std.unicode.utf8ToUtf16LeStringLiteral("settings.reapply"),
            @backingInt(Operation.defaults) => std.unicode.utf8ToUtf16LeStringLiteral("settings.defaults"),
            else => null,
        },
        1 => switch (value) {
            @backingInt(Operation.write) => std.unicode.utf8ToUtf16LeStringLiteral("app-settings-saved"),
            @backingInt(Operation.patch) => std.unicode.utf8ToUtf16LeStringLiteral("app-settings-patched"),
            @backingInt(Operation.reapply) => std.unicode.utf8ToUtf16LeStringLiteral("app-settings-reapply"),
            else => null,
        },
        2 => switch (value) {
            @backingInt(Disposition.applied) => std.unicode.utf8ToUtf16LeStringLiteral("committedAndApplied"),
            @backingInt(Disposition.constrained) => std.unicode.utf8ToUtf16LeStringLiteral("committedWithCapabilityConstraints"),
            @backingInt(Disposition.delivery_failures) => std.unicode.utf8ToUtf16LeStringLiteral("committedWithDeliveryFailures"),
            else => null,
        },
        3 => std.unicode.utf8ToUtf16LeStringLiteral("Invalid application settings patch"),
        else => null,
    };
}

pub export fn rm_settings_request_next(operation: u32, completed: u32, facts: ?*const Facts, output: ?*Result) callconv(.c) i32 {
    return next(operation, completed, facts orelse return 1, output orelse return 1);
}

pub export fn rm_settings_request_text(kind: u32, value: u32) callconv(.c) ?[*:0]const u16 {
    return if (getText(kind, value)) |value_text| value_text.ptr else null;
}
