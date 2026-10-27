const std = @import("std");
pub const max_line = 16 * 1024; // Includes LF; limit is bytes, not characters.
pub const BackendSession = struct { ready: bool = false, generation: i64 = 0, reason: ?[]const u8 = null };
pub const ShowCommand = enum { show, @"show-passive", @"show-passive-secondary" };
pub const Scope = enum { frontend, backend };
pub const StopOutcome = enum { normal, deadline_terminated, wait_cancelled };
pub const StopResult = struct { outcome: StopOutcome, elapsed_ms: u64, exit_code: u32 };
pub const Event = union(enum) {
    running: u32,
    window_handle: usize,
    user_close,
    apply_exit,
    preferences_changed: Scope,
    language: []const u8,
    crashed: u32,
    stopped: StopResult,
};

/// Fixed-capacity JSON string encoder. No escaping is delegated to shell syntax.
pub fn quote(output: []u8, input: []const u8) ![]const u8 {
    if (!std.unicode.utf8ValidateSlice(input)) return error.InvalidUtf8;
    var n: usize = 0;
    try append(output, &n, "\"");
    for (input) |c| {
        switch (c) {
            '"' => try append(output, &n, "\\\""),
            '\\' => try append(output, &n, "\\\\"),
            0...8, 11, 12, 14...31 => {
                const s = try std.fmt.bufPrint(output[n..], "\\u00{x:0>2}", .{c});
                n += s.len;
            },
            '\n' => try append(output, &n, "\\n"),
            '\r' => try append(output, &n, "\\r"),
            '\t' => try append(output, &n, "\\t"),
            else => {
                if (n == output.len) return error.NoSpaceLeft;
                output[n] = c;
                n += 1;
            },
        }
    }
    try append(output, &n, "\"");
    return output[0..n];
}
fn append(output: []u8, n: *usize, value: []const u8) !void {
    if (output.len - n.* < value.len) return error.NoSpaceLeft;
    @memcpy(output[n.*..][0..value.len], value);
    n.* += value.len;
}

pub fn parse(line: []const u8, allocator: std.mem.Allocator) !?Event {
    if (line.len + 1 > max_line or !std.unicode.utf8ValidateSlice(line)) return error.InvalidMessage;
    const parsed = try std.json.parseFromSlice(std.json.Value, allocator, line, .{ .max_value_len = max_line, .allocate = .alloc_always });
    const obj = switch (parsed.value) {
        .object => |o| o,
        else => return error.InvalidMessage,
    };
    const kind = try string(obj, "type");
    if (std.mem.eql(u8, kind, "user-close")) return .user_close;
    if (std.mem.eql(u8, kind, "applyExit")) return .apply_exit;
    if (std.mem.eql(u8, kind, "window-handle")) return .{ .window_handle = try std.fmt.parseInt(usize, try string(obj, "handle"), 10) };
    if (std.mem.eql(u8, kind, "language")) return .{ .language = try string(obj, "language") };
    if (std.mem.eql(u8, kind, "preferences-changed")) return .{ .preferences_changed = std.meta.stringToEnum(Scope, try string(obj, "scope")) orelse return error.InvalidMessage };
    return null; // Other negotiated facts may be added independently.
}
fn string(obj: std.json.ObjectMap, key: []const u8) ![]const u8 {
    const value = obj.get(key) orelse return error.InvalidMessage;
    return switch (value) {
        .string => |s| s,
        else => error.InvalidMessage,
    };
}
