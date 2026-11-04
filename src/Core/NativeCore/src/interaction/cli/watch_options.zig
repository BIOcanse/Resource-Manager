//! Watch's contribution to CLI1's common parser and schema-driven help.
//! Product parameters (including selector/frequency) remain in the typed payload.
const std = @import("std");
const invocation = @import("rm_invocation_contract");

pub const command = "watch";
pub const required_kind: invocation.Kind = .subscription;

// CLI1 owns input files, schema conversion, format selection and their defaults.
// Watch introduces no business defaults or additional capability allow-list.
pub const Description = struct {
    name: []const u8,
    kind: invocation.Kind,
    payload_from_service_schema: bool,
    streaming: bool,
};

pub const description: Description = .{
    .name = command,
    .kind = required_kind,
    .payload_from_service_schema = true,
    .streaming = true,
};

pub const Error = error{ MissingOperation, InvalidOperation, DescriptorMismatch, NotSubscribable };

/// Metadata checks only. The same service/client must still admit the request.
/// Borrowed operation/payload lifetime extends through subscribe's consumption.
pub fn convert(request: invocation.Request, descriptor: invocation.Descriptor) Error!invocation.Request {
    if (request.operation.len == 0) return error.MissingOperation;
    if (!invocation.operationValid(request.operation)) return error.InvalidOperation;
    if (!std.mem.eql(u8, request.operation, descriptor.operation)) return error.DescriptorMismatch;
    if (descriptor.kind != required_kind) return error.NotSubscribable;
    return request;
}
