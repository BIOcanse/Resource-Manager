//! The original report-rules schema as a resumable traversal. The source owns
//! its accepted immutable rule array, not a runtime-plan publication lease.
const scalar = @import("../wire/value.zig");
pub const Value = scalar.Value;
pub const Key = struct { index: usize, child: usize = 0, field: u32 };
pub const Token = union(enum) { raw: []const u8, value: Key, end };
const names = .{ "name", "reportType", "displayUnit", "scale", "step", "thresholdMinimum", "thresholdMaximum", "countMaximum", "zeroActivityMaximum" };
const defaults = .{ "activationThreshold", "clearThreshold", "requiredConsecutiveHits", "requiredConsecutiveMisses", "zeroActivityThreshold" };
const Phase = enum { start, rule, name, value, comparison, edit_start, edit, edit_value, defaults_start, default_name, default_value, executables_start, executable, executable_value, rule_end, end };

pub const Traversal = struct {
    phase: Phase = .start,
    index: usize = 0,
    child: usize = 0,
    field: u32 = 0,
    rules: usize = 0,
    children: usize = 0,

    pub fn next(self: *Traversal, source: anytype) !Token {
        switch (self.phase) {
            .start => {
                self.rules = try nonnegative(try source.count(0, 0));
                self.phase = .rule;
                return .{ .raw = "{\"rules\":[" };
            },
            .rule => {
                if (self.index == self.rules) {
                    self.phase = .end;
                    return .{ .raw = "]}" };
                }
                self.field = 0;
                self.phase = .name;
                return .{ .raw = if (self.index == 0) "{" else ",{" };
            },
            .name => {
                self.phase = .value;
                inline for (names, 0..) |name, field| if (self.field == field) return .{ .raw = (if (field == 0) "" else ",") ++ "\"" ++ name ++ "\":" };
                unreachable;
            },
            .value => {
                const key: Key = .{ .index = self.index, .field = self.field };
                self.field += 1;
                self.phase = if (self.field == 2) .comparison else if (self.field == names.len) .edit_start else .name;
                return .{ .value = key };
            },
            .comparison => {
                self.phase = .name;
                return .{ .raw = if (try source.count(self.index, 3) == 1) ",\"comparison\":\"atOrAbove\"" else ",\"comparison\":\"atOrBelow\"" };
            },
            .edit_start => {
                self.children = try nonnegative(try source.count(self.index, 1));
                self.child = 0;
                self.phase = .edit;
                return .{ .raw = ",\"editableFields\":[" };
            },
            .edit => {
                if (self.child == self.children) {
                    self.phase = .defaults_start;
                    return .{ .raw = "]" };
                }
                self.phase = .edit_value;
                return .{ .raw = if (self.child == 0) "" else "," };
            },
            .edit_value => {
                const key: Key = .{ .index = self.index, .child = self.child, .field = 9 };
                self.child += 1;
                self.phase = .edit;
                return .{ .value = key };
            },
            .defaults_start => {
                self.field = 0;
                self.phase = .default_name;
                return .{ .raw = ",\"defaults\":{" };
            },
            .default_name => {
                self.phase = .default_value;
                inline for (defaults, 0..) |name, field| if (self.field == field) return .{ .raw = (if (field == 0) "" else ",") ++ "\"" ++ name ++ "\":" };
                unreachable;
            },
            .default_value => {
                const key: Key = .{ .index = self.index, .field = 10 + self.field };
                self.field += 1;
                self.phase = if (self.field == defaults.len) .executables_start else .default_name;
                return .{ .value = key };
            },
            .executables_start => {
                const count = try source.count(self.index, 2);
                if (count < -1) return error.Invalid;
                self.child = 0;
                self.phase = if (count == -1) .rule_end else .executable;
                self.children = if (count == -1) 0 else @intCast(count);
                return .{ .raw = if (count == -1) ",\"executableNames\":null" else ",\"executableNames\":[" };
            },
            .executable => {
                if (self.child == self.children) {
                    self.phase = .rule_end;
                    return .{ .raw = "]" };
                }
                self.phase = .executable_value;
                return .{ .raw = if (self.child == 0) "" else "," };
            },
            .executable_value => {
                const key: Key = .{ .index = self.index, .child = self.child, .field = 15 };
                self.child += 1;
                self.phase = .executable;
                return .{ .value = key };
            },
            .rule_end => {
                self.index += 1;
                self.phase = .rule;
                return .{ .raw = "}}" };
            },
            .end => return .end,
        }
    }
};

fn nonnegative(count: i64) !usize {
    if (count < 0) return error.Invalid;
    return @intCast(count);
}

pub const Cursor = struct {
    traversal: Traversal = .{},
    token: Token = .{ .raw = "" },
    raw_offset: usize = 0,
    value: scalar.Cursor = .{},
    terminal: bool = false,

    /// View is valid only for THIS synchronous call. Borrow the SAME accepted
    /// result throughout this run; never read current again to resume. No view,
    /// source pointer, managed text, or lease is stored in this cursor.
    pub fn fill(self: *Cursor, view: anytype, bytes: []u8) !usize {
        if (bytes.len == 0) return error.EmptyWindow;
        if (self.terminal) return 0;
        return self.fillInner(view, bytes) catch |err| {
            self.terminal = true;
            return err;
        };
    }

    fn fillInner(self: *Cursor, view: anytype, bytes: []u8) !usize {
        var written: usize = 0;
        while (written < bytes.len) {
            switch (self.token) {
                .raw => |raw| {
                    const count = @min(bytes.len - written, raw.len - self.raw_offset);
                    @memcpy(bytes[written..][0..count], raw[self.raw_offset..][0..count]);
                    self.raw_offset += count;
                    written += count;
                    if (self.raw_offset != raw.len) break;
                },
                .value => |key| {
                    written += try self.value.fill(try view.value(key), bytes[written..]);
                    if (!self.value.done()) break;
                },
                .end => {
                    self.terminal = true;
                    break;
                },
            }
            self.token = try self.traversal.next(view);
            self.raw_offset = 0;
            self.value = .{};
        }
        return written;
    }

    pub fn cancel(self: *Cursor) void {
        self.terminal = true;
    }
};
