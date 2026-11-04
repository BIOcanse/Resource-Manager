//! C18 transport-neutral inputs and capability metadata. No framing, pipe, HTTP,
//! OS handles, defaults or domain state. Identity is an in-process proof borrow.
const std = @import("std");
pub const maximum_operation_bytes = 64; // Existing Invoke envelope contract, unchanged.

pub fn operationValid(operation: []const u8) bool {
    if (operation.len == 0 or operation.len > maximum_operation_bytes) return false;
    const view = std.unicode.Utf8View.init(operation) catch return false;
    var iterator = view.iterator();
    while (iterator.nextCodepoint()) |point| {
        // Unicode White_Space, matching the existing IsNullOrWhiteSpace check.
        switch (point) {
            0x09...0x0d, 0x20, 0x85, 0xa0, 0x1680, 0x2000...0x200a, 0x2028, 0x2029, 0x202f, 0x205f, 0x3000 => {},
            else => return true,
        }
    }
    return false;
}

pub const Request = struct { operation: []const u8, payload: std.json.Value };
pub const Status = enum(u32) {
    ok,
    not_found,
    unauthorized,
    forbidden,
    unavailable,
    invalid_request,
    cancelled,
    effect_unknown,
    pub fn text(self: Status) []const u8 {
        return switch (self) {
            .not_found => "not-found",
            .invalid_request => "invalid-request",
            .effect_unknown => "effect-unknown",
            else => @tagName(self),
        };
    }
    pub fn parse(value: []const u8) ?Status {
        inline for (std.meta.tags(Status)) |status| {
            if (std.mem.eql(u8, value, status.text())) return status;
        }
        return null;
    }
};
pub const Response = struct { status: Status, payload: std.json.Value };
pub const Kind = enum(u32) { current_read, scan, command, subscription };
// Metadata only, not a second policy. The binding invokes the original policy.
pub const Admission = enum(u32) { anonymous, normal_read, normal_action, administrator, per_selector };
pub const Descriptor = struct {
    operation: []const u8,
    input_schema_json: []const u8,
    output_schema_json: []const u8,
    kind: Kind,
    admission: Admission,
};

pub const Cancel = struct {
    context: ?*anyopaque = null,
    is_cancelled: *const fn (?*anyopaque) bool,
    pub fn check(self: Cancel) error{Cancelled}!void {
        if (self.is_cancelled(self.context)) return error.Cancelled;
    }
    pub const none: Cancel = .{ .is_cancelled = never };
    fn never(_: ?*anyopaque) bool {
        return false;
    }
};

// Explicit absolute monotonic deadline, supplied by the caller; no timeout default.
pub const Control = struct { cancel: Cancel, deadline_ms: ?u64 };
