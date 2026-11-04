const protocol = @import("protocol.zig");
const state = @import("state.zig");
const rules = @import("rules.zig");
const trust = @import("trust.zig");
const history = @import("history.zig");
const planner = @import("planner.zig");
const persistence = @import("persistence.zig");
const ResultCode = @import("../../common/result_codes.zig").ResultCode;

pub const Session = state.Session;

pub fn queryObservationCount(
    session: *Session,
    input: *const protocol.SourceSnapshotInput,
    fact_pointer: ?[*]const protocol.FactInput,
    fact_count: u32,
    required: *u32,
) ResultCode {
    required.* = 0;
    const rows = if (fact_count == 0) &.{} else (fact_pointer orelse return .invalid_argument)[0..fact_count];
    state.lockMutex(&session.mutex);
    defer session.mutex.unlock();
    var validation_config = session.config;
    validation_config.maximum_observation_count = @max(fact_count, validation_config.maximum_observation_count);
    if (!protocol.validSourceSnapshot(input, &validation_config)) return .abi_mismatch;
    if (input.fact_count != fact_count) return .invalid_argument;
    required.* = @import("growth.zig").observationDemand(session, input, rows).maximum_observation_count;
    return .ok;
}

pub fn replaceRules(
    session: *Session,
    input: *const protocol.RuleReplaceInput,
    rule_pointer: ?[*]const protocol.RuleInput,
    rule_count: u32,
) ResultCode {
    const rows = if (rule_count == 0)
        &.{}
    else
        (rule_pointer orelse return .invalid_argument)[0..rule_count];
    state.lockMutex(&session.mutex);
    defer session.mutex.unlock();
    return rules.replace(session, input, rows);
}

pub fn importPersisted(
    session: *Session,
    input: *const protocol.ImportInput,
    row_pointer: ?[*]const protocol.PersistenceOperation,
    row_count: u32,
) ResultCode {
    const rows = if (row_count == 0)
        &.{}
    else
        (row_pointer orelse return .invalid_argument)[0..row_count];
    state.lockMutex(&session.mutex);
    defer session.mutex.unlock();
    return persistence.importPersisted(session, input, rows);
}

pub fn observe(
    session: *Session,
    input: *const protocol.SourceSnapshotInput,
    fact_pointer: ?[*]const protocol.FactInput,
    fact_count: u32,
) ResultCode {
    const rows = if (fact_count == 0)
        &.{}
    else
        (fact_pointer orelse return .invalid_argument)[0..fact_count];
    state.lockMutex(&session.mutex);
    defer session.mutex.unlock();
    return rules.observe(session, input, rows);
}

pub fn commandTrust(
    session: *Session,
    input: *const protocol.TrustCommandInput,
) ResultCode {
    state.lockMutex(&session.mutex);
    defer session.mutex.unlock();
    return trust.command(session, input);
}

pub fn queryPlanSize(
    session: *Session,
    input: *const protocol.PlanInput,
    report_count: *u32,
    operation_count: *u32,
) ResultCode {
    state.lockMutex(&session.mutex);
    defer session.mutex.unlock();
    return planner.queryPlanSize(session, input, report_count, operation_count);
}

pub fn clearHistory(session: *Session, input: *const protocol.ClearHistoryInput, removed_count: *u32) ResultCode {
    state.lockMutex(&session.mutex);
    defer session.mutex.unlock();
    return history.clear(session, input, removed_count);
}

pub fn plan(
    session: *Session,
    input: *const protocol.PlanInput,
    report_pointer: ?[*]protocol.ReportOutput,
    report_capacity: u32,
    operation_pointer: ?[*]protocol.PersistenceOperation,
    operation_capacity: u32,
    output: *protocol.PlanOutput,
) ResultCode {
    if (report_capacity == 0 or operation_capacity == 0) return .invalid_argument;
    const reports = (report_pointer orelse return .invalid_argument)[0..report_capacity];
    const operations = (operation_pointer orelse return .invalid_argument)[0..operation_capacity];
    state.lockMutex(&session.mutex);
    defer session.mutex.unlock();
    return planner.plan(session, input, reports, operations, output);
}

pub fn applyFeedback(
    session: *Session,
    input: *const protocol.PersistenceFeedbackInput,
    feedback_pointer: ?[*]const protocol.PersistenceFeedback,
    feedback_count: u32,
) ResultCode {
    if (feedback_count == 0) return .invalid_argument;
    const feedbacks = (feedback_pointer orelse return .invalid_argument)[0..feedback_count];
    state.lockMutex(&session.mutex);
    defer session.mutex.unlock();
    return persistence.applyFeedback(session, input, feedbacks);
}
