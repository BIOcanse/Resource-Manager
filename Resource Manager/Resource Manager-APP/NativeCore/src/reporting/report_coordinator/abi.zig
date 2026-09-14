const protocol = @import("protocol.zig");
const session_module = @import("session.zig");
const ResultCode = @import("../../common/result_codes.zig").ResultCode;

pub export fn rm_report_coordinator_abi_version() callconv(.c) u32 {
    return protocol.abi_version;
}

pub export fn rm_report_coordinator_create(
    config: ?*const protocol.Config,
    handle: ?*?*anyopaque,
) callconv(.c) i32 {
    const output = handle orelse return code(.invalid_argument);
    output.* = null;
    const session = session_module.Session.create(config orelse return code(.invalid_argument)) catch |err| switch (err) {
        error.InvalidConfiguration => return code(.abi_mismatch),
        else => return code(.out_of_memory),
    };
    output.* = @ptrCast(session);
    return code(.ok);
}

pub export fn rm_report_coordinator_destroy(handle: ?*anyopaque) callconv(.c) void {
    const session: *session_module.Session = @ptrCast(@alignCast(handle orelse return));
    session.destroy();
}

pub export fn rm_report_coordinator_reconfigure(
    handle: ?*anyopaque,
    config: ?*const protocol.Config,
) callconv(.c) i32 {
    const session: *session_module.Session = @ptrCast(@alignCast(
        handle orelse return code(.invalid_argument),
    ));
    return code(session.reconfigure(config orelse return code(.invalid_argument)));
}

pub export fn rm_report_coordinator_query_capacity(
    handle: ?*anyopaque,
    output: ?*protocol.Capacity,
    output_size: u32,
) callconv(.c) i32 {
    if (output_size != @sizeOf(protocol.Capacity)) return code(.abi_mismatch);
    const session: *session_module.Session = @ptrCast(@alignCast(
        handle orelse return code(.invalid_argument),
    ));
    (output orelse return code(.invalid_argument)).* = session.capacity();
    return code(.ok);
}

pub export fn rm_report_coordinator_replace_rules(
    handle: ?*anyopaque,
    input: ?*const protocol.RuleReplaceInput,
    rules: ?[*]const protocol.RuleInput,
    rule_count: u32,
) callconv(.c) i32 {
    const session: *session_module.Session = @ptrCast(@alignCast(
        handle orelse return code(.invalid_argument),
    ));
    return code(session_module.replaceRules(
        session,
        input orelse return code(.invalid_argument),
        rules,
        rule_count,
    ));
}

pub export fn rm_report_coordinator_import(
    handle: ?*anyopaque,
    input: ?*const protocol.ImportInput,
    rows: ?[*]const protocol.PersistenceOperation,
    row_count: u32,
) callconv(.c) i32 {
    const session: *session_module.Session = @ptrCast(@alignCast(
        handle orelse return code(.invalid_argument),
    ));
    return code(session_module.importPersisted(
        session,
        input orelse return code(.invalid_argument),
        rows,
        row_count,
    ));
}

pub export fn rm_report_coordinator_observe(
    handle: ?*anyopaque,
    input: ?*const protocol.SourceSnapshotInput,
    facts: ?[*]const protocol.FactInput,
    fact_count: u32,
) callconv(.c) i32 {
    const session: *session_module.Session = @ptrCast(@alignCast(
        handle orelse return code(.invalid_argument),
    ));
    return code(session_module.observe(
        session,
        input orelse return code(.invalid_argument),
        facts,
        fact_count,
    ));
}

pub export fn rm_report_coordinator_command_trust(
    handle: ?*anyopaque,
    input: ?*const protocol.TrustCommandInput,
) callconv(.c) i32 {
    const session: *session_module.Session = @ptrCast(@alignCast(
        handle orelse return code(.invalid_argument),
    ));
    return code(session_module.commandTrust(
        session,
        input orelse return code(.invalid_argument),
    ));
}

pub export fn rm_report_coordinator_plan(
    handle: ?*anyopaque,
    input: ?*const protocol.PlanInput,
    reports: ?[*]protocol.ReportOutput,
    report_capacity: u32,
    operations: ?[*]protocol.PersistenceOperation,
    operation_capacity: u32,
    output: ?*protocol.PlanOutput,
) callconv(.c) i32 {
    const session: *session_module.Session = @ptrCast(@alignCast(
        handle orelse return code(.invalid_argument),
    ));
    return code(session_module.plan(
        session,
        input orelse return code(.invalid_argument),
        reports,
        report_capacity,
        operations,
        operation_capacity,
        output orelse return code(.invalid_argument),
    ));
}

pub export fn rm_report_coordinator_apply_feedback(
    handle: ?*anyopaque,
    input: ?*const protocol.PersistenceFeedbackInput,
    feedbacks: ?[*]const protocol.PersistenceFeedback,
    feedback_count: u32,
) callconv(.c) i32 {
    const session: *session_module.Session = @ptrCast(@alignCast(
        handle orelse return code(.invalid_argument),
    ));
    return code(session_module.applyFeedback(
        session,
        input orelse return code(.invalid_argument),
        feedbacks,
        feedback_count,
    ));
}

fn code(result: ResultCode) i32 {
    return @intFromEnum(result);
}

comptime {
    _ = protocol.Config;
    _ = protocol.Capacity;
    _ = protocol.RuleInput;
    _ = protocol.RuleReplaceInput;
    _ = protocol.FactInput;
    _ = protocol.SourceSnapshotInput;
    _ = protocol.TrustCommandInput;
    _ = protocol.ImportInput;
    _ = protocol.PlanInput;
    _ = protocol.ReportOutput;
    _ = protocol.PersistenceOperation;
    _ = protocol.PersistenceFeedbackInput;
    _ = protocol.PersistenceFeedback;
    _ = protocol.PlanOutput;
}
