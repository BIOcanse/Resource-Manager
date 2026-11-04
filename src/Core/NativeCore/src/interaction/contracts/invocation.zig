//! Shared Invoke.v1 contract. Frontends import this and pipes/client only.
//! JSON values keep number lexemes (including exact 64-bit IDs) until output.
const std = @import("std");

pub const version = "ResourceManager.TrustedAccess.Invoke.v1/no-key/1";
pub const pipe_name = "ResourceManager.TrustedAccess.Invoke.v1";
pub const directory_operation = "capabilities.list";
pub const header_bytes = @sizeOf(u32);
pub const maximum_operation_bytes = capability.maximum_operation_bytes;

pub const capability = @import("capability.zig");
pub const operationValid = capability.operationValid;

// Assembly resolves these once from the authoritative declarations. No second
// copy of defaults here; maintenance assigns the new choices FreedomPoints.
pub const Limits = struct {
    maximum_frame_bytes: u32,
    maximum_connections: usize,
    worker_stack_bytes: usize,
    request_timeout_ms: u32,
    response_timeout_ms: u32,
    pub fn valid(self: Limits) bool {
        return self.maximum_frame_bytes != 0 and self.maximum_connections != 0 and
            self.worker_stack_bytes != 0 and self.request_timeout_ms != 0 and self.response_timeout_ms != 0;
    }
};

pub const Request = capability.Request;
pub const Status = capability.Status;
pub const Response = capability.Response;
pub const Kind = capability.Kind;
pub const Admission = capability.Admission;
pub const Descriptor = capability.Descriptor;

pub const directory_descriptor: Descriptor = .{
    .operation = directory_operation,
    .input_schema_json = "{\"type\":\"null\"}",
    .output_schema_json = "{\"type\":\"object\",\"required\":[\"contract\",\"complete\",\"operations\"],\"properties\":{\"contract\":{\"type\":\"string\"},\"complete\":{\"type\":\"boolean\"},\"operations\":{\"type\":\"array\"}},\"additionalProperties\":false}",
    .kind = .current_read,
    .admission = .normal_read,
};

pub const Cancel = capability.Cancel;
// Absolute monotonic millisecond deadline. null is for an admitted subscription,
// never used for the initial request/attestation. Transport must honor Cancel
// while blocked; ending one context cannot cancel other connections.
pub const Control = capability.Control;
pub const Clock = struct {
    context: ?*anyopaque,
    now_ms: *const fn (?*anyopaque) u64,
    pub fn deadline(self: Clock, timeout_ms: u32) u64 {
        return std.math.add(u64, self.now_ms(self.context), timeout_ms) catch std.math.maxInt(u64);
    }
};

// One owned OS connection. read/write may short transfer; zero is EOF/failure.
// verify_server checks pipe PID + pinned process creation/image + SCM identity,
// and rechecks after response. A pipe name alone is never service evidence.
pub const Connection = struct {
    context: *anyopaque,
    read: *const fn (*anyopaque, []u8, Control) anyerror!usize,
    write: *const fn (*anyopaque, []const u8, Control) anyerror!usize,
    verify_server: *const fn (*anyopaque) anyerror!void,
    // True after OS disconnect/termination; must not consume the next frame.
    is_ended: *const fn (*anyopaque) bool,
    interrupt: *const fn (*anyopaque) void,
    close: *const fn (*anyopaque) void,
};
pub const Connector = struct {
    context: *anyopaque,
    open: *const fn (*anyopaque, Control) anyerror!Connection,
};
pub const Effect = enum { not_started, unknown };
pub const FailureKind = enum { transport, cancelled, identity, invalid_response, invalid_request };
pub const Failure = struct { kind: FailureKind, effect: Effect };
