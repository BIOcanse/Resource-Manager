const std = @import("std");
pub const policy = @import("access.zig");

// Existing managed HTTP/pipe consumers still use the old ABI until the approved
// privilege integration removes them. Keep the historical listener callable
// without changing the new two-level leaf or the shared ABI registration file.
comptime {
    const legacy = @import("legacy_access.zig");
    _ = legacy.rm_access_evaluate;
    _ = legacy.rm_access_text;
    _ = legacy.rm_access_subset;
    _ = legacy.rm_access_missing;
    _ = legacy.rm_access_contains;
    _ = legacy.rm_access_gpu_operation;
}

// New entry points and layout: never reuse a retired export with a different
// signature. Public NativeCore registration is integrated by maintenance only.
// The pending revision now includes a borrowed process-permission view.
pub const abi_version: u32 = 0x0001_0002;
pub const Status = enum(u32) { ok = 0, invalid_argument = 1 };
pub const Request = extern struct {
    abi_version: u32,
    struct_size: u32,
    requirement: u32,
    local_connection: u32,
    origin_allowed: u32,
    normal_actions_allowed: u32,
    caller_verified: u32,
    caller_is_administrator: u32,
    process_id: i32,
    windows_session_id: i32,
    process_created_utc_ticks: i64,
    windows_sid: ?[*]const u16,
    windows_sid_length: usize,
};

fn decode(input: *const Request) ?policy.Input {
    if (input.abi_version != abi_version or input.struct_size != @sizeOf(Request) or
        input.local_connection > 1 or input.origin_allowed > 1 or input.normal_actions_allowed > 1 or
        input.caller_verified > 1 or input.caller_is_administrator > 1 or
        (input.windows_sid == null and input.windows_sid_length != 0)) return null;
    const requirement = std.enums.fromInt(policy.Requirement, input.requirement) orelse return null;
    return .{
        .requirement = requirement,
        .local_connection = input.local_connection != 0,
        .origin_allowed = input.origin_allowed != 0,
        .normal_actions_allowed = input.normal_actions_allowed != 0,
        .caller = if (input.caller_verified == 0) null else .{
            .process_id = input.process_id,
            .process_created_utc_ticks = input.process_created_utc_ticks,
            .windows_session_id = input.windows_session_id,
            .windows_sid = if (input.windows_sid) |sid| sid[0..input.windows_sid_length] else &.{},
            .is_administrator = input.caller_is_administrator != 0,
        },
    };
}

pub export fn rm_interaction_access_evaluate(request: ?*const Request, output: ?*policy.Decision) callconv(.c) Status {
    const decision = output orelse return .invalid_argument;
    decision.* = policy.reject(.access_policy_missing, .unidentified);
    const input = request orelse return .invalid_argument;
    decision.* = policy.evaluate(decode(input) orelse return .invalid_argument);
    return .ok;
}

pub const ReadRequest = extern struct {
    struct_size: u32,
    read_kind: u32,
    source_session_verified: u32,
    source_windows_session_id: i32,
    access: Request,
};

pub export fn rm_interaction_access_read(request: ?*const ReadRequest, output: ?*policy.Decision) callconv(.c) Status {
    const decision = output orelse return .invalid_argument;
    decision.* = policy.reject(.access_policy_missing, .unidentified);
    const input = request orelse return .invalid_argument;
    if (input.struct_size != @sizeOf(ReadRequest) or input.source_session_verified > 1) return .invalid_argument;
    const kind = std.enums.fromInt(policy.ReadKind, input.read_kind) orelse return .invalid_argument;
    const caller_input = decode(&input.access) orelse return .invalid_argument;
    decision.* = policy.evaluateRead(caller_input, .{
        .kind = kind,
        .windows_session_id = if (input.source_session_verified != 0) input.source_windows_session_id else null,
    });
    return .ok;
}

pub const ProcessEvidence = extern struct {
    status: u32,
    extent: u32,
    error_kind: u32,
    native_error: u32,
    caller_process_id: i32,
    caller_process_created_utc_ticks: i64,
    caller_scope: ?*const anyopaque,
    capability: ?*const anyopaque,
    fact: policy.ProcessFact,
};
pub const ProcessReadRequest = extern struct {
    struct_size: u32,
    read_use: u32,
    caller_scope: ?*const anyopaque,
    source: policy.ProcessFact,
    evidence: ?*const ProcessEvidence,
    access: Request,
};

pub export fn rm_interaction_access_process_read(request: ?*const ProcessReadRequest, output: ?*policy.ProcessReadDecision) callconv(.c) Status {
    const decision = output orelse return .invalid_argument;
    decision.* = .{ .access = policy.reject(.access_policy_missing, .unidentified), .status = .not_proven, .error_kind = .none, .native_error = 0 };
    const input = request orelse return .invalid_argument;
    if (input.struct_size != @sizeOf(ProcessReadRequest)) return .invalid_argument;
    const use = std.enums.fromInt(policy.ProcessReadUse, input.read_use) orelse return .invalid_argument;
    const access = decode(&input.access) orelse return .invalid_argument;
    var evidence: ?policy.ProcessReadEvidence = null;
    if (input.evidence) |proof| {
        evidence = .{
            .status = std.enums.fromInt(policy.ProcessPermissionStatus, proof.status) orelse return .invalid_argument,
            .extent = std.enums.fromInt(policy.EvidenceExtent, proof.extent) orelse return .invalid_argument,
            .error_kind = std.enums.fromInt(policy.NativeErrorKind, proof.error_kind) orelse return .invalid_argument,
            .native_error = proof.native_error,
            .caller_process_id = proof.caller_process_id,
            .caller_process_created_utc_ticks = proof.caller_process_created_utc_ticks,
            .caller_scope = proof.caller_scope,
            .capability = proof.capability,
            .fact = proof.fact,
        };
    }
    decision.* = policy.evaluateProcessRead(access, input.caller_scope, input.source, use, evidence);
    return .ok;
}

pub export fn rm_interaction_access_gpu_operation(pointer: ?[*]const u16, length: usize, payload_length: usize, maximum_payload: usize) callconv(.c) policy.GpuOperation {
    if (pointer == null and length != 0) return .invalid;
    return policy.gpuOperation(if (pointer) |p| p[0..length] else &.{}, payload_length, maximum_payload);
}
