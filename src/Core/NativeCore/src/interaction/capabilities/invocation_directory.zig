//! Native service registration. This is metadata + a single call adapter, never
//! a second policy table or current/working-state owner.
pub const contract = @import("rm_invocation_contract").capability;
pub const Unary = @import("unary.zig").Unary;
pub const Caller = @import("unary.zig").Caller;
pub const Result = @import("unary.zig").Result;
pub const Sink = @import("unary.zig").Sink;

pub const gpu_resolve: contract.Descriptor = .{
    .operation = "gpu.resolve",
    .input_schema_json = "{\"type\":\"string\"}",
    .output_schema_json = "{\"type\":\"string\"}",
    .kind = .command,
    .admission = .normal_action,
};
pub const gpu_report: contract.Descriptor = .{
    .operation = "gpu.report",
    .input_schema_json = "{\"type\":\"string\"}",
    .output_schema_json = "{\"type\":\"string\"}",
    .kind = .command,
    .admission = .normal_action,
};

// C0 supplies the complete output schema from its shared ViewResult. This
// constructor cannot invent a reduced result or advertise anyopaque as data.
// The original capability invokes Read/Package/Acknowledge/Delete.
pub const CrashOperation = enum { read, package, acknowledge, delete };
pub fn crash(operation: CrashOperation, output_schema_json: []const u8, admission: contract.Admission) contract.Descriptor {
    return .{
        .operation = switch (operation) {
            .read => "crash-reports.read",
            .package => "crash-reports.package",
            .acknowledge => "crash-reports.acknowledge",
            .delete => "crash-reports.delete",
        },
        .input_schema_json = if (operation == .read) "{\"type\":\"null\"}" else "{\"type\":\"object\",\"required\":[\"id\"],\"properties\":{\"id\":{\"type\":\"string\"}},\"additionalProperties\":false}",
        .output_schema_json = output_schema_json,
        .kind = if (operation == .read) .scan else .command,
        .admission = admission,
    };
}
