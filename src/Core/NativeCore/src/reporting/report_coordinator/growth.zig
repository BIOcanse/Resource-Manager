const std = @import("std");
const protocol = @import("protocol.zig");
const state = @import("state.zig");
const ResultCode = @import("../../common/result_codes.zig").ResultCode;

pub fn occupied(slots: anytype) u32 {
    var count: u32 = 0;
    for (slots) |slot| if (slot.occupied) {
        count += 1;
    };
    return count;
}

pub fn replaceRules(session: *state.Session, count: usize) ResultCode {
    var demand = session.allocationShape();
    demand.maximum_rule_count = @intCast(count);
    return session.ensureShape(demand, .report_rules);
}

pub fn importRows(session: *state.Session, rows: []const protocol.PersistenceOperation) ResultCode {
    var demand = session.allocationShape();
    var counts = [_]u32{0} ** 6;
    for (rows) |row| {
        if (row.operation_kind >= 1 and row.operation_kind <= 6) counts[row.operation_kind - 1] += 1;
    }
    demand.maximum_source_count = counts[@intFromEnum(protocol.PersistenceKind.source) - 1];
    demand.maximum_observation_count = counts[@intFromEnum(protocol.PersistenceKind.observation) - 1];
    demand.maximum_bucket_count = counts[@intFromEnum(protocol.PersistenceKind.bucket) - 1];
    demand.maximum_report_count = counts[@intFromEnum(protocol.PersistenceKind.report) - 1];
    demand.maximum_trust_count = counts[@intFromEnum(protocol.PersistenceKind.trust) - 1];
    return session.ensureShape(demand, .report_import);
}

pub fn observe(session: *state.Session, input: *const protocol.SourceSnapshotInput, facts: []const protocol.FactInput) ResultCode {
    return session.ensureShape(observationDemand(session, input, facts), .report_facts);
}

pub fn observationDemand(session: *state.Session, input: *const protocol.SourceSnapshotInput, facts: []const protocol.FactInput) protocol.Config {
    var demand = session.allocationShape();
    const source = session.findSource(input.source_handle, input.coverage_scope_handle);
    demand.maximum_source_count = occupied(session.sources) + @as(u32, if (source == null) 1 else 0);
    var observations = occupied(session.observations);
    var buckets = occupied(session.buckets);
    var observed_at = @max(input.observed_at_utc_milliseconds, session.logical_utc_ms);
    if (source) |index| observed_at = @max(observed_at, session.sources[index].last_observed_at_utc_ms);
    const bucket_width: i64 = @intCast(session.config.bucket_width_milliseconds);
    const bucket_start = @divFloor(observed_at, bucket_width) * bucket_width;
    for (facts) |fact| {
        if (fact.flags == 0) continue;
        const rule_index = session.findRule(fact.rule_handle) orelse continue;
        const rule = session.rules[rule_index].value;
        if (session.findObservation(input.source_handle, input.coverage_scope_handle, fact.rule_handle, fact.target_handle) == null) observations += 1;
        if ((rule.flags & protocol.RuleFlags.rolling) != 0 and
            session.findBucket(input.source_handle, fact.rule_handle, fact.target_handle, bucket_start) == null) buckets += 1;
    }
    // The fact-key validation column shares the observation extent.
    demand.maximum_observation_count = @max(observations, @as(u32, @intCast(facts.len)));
    demand.maximum_bucket_count = buckets;
    return demand;
}

pub fn trust(session: *state.Session, input: *const protocol.TrustCommandInput) ResultCode {
    var demand = session.allocationShape();
    if (input.command_kind == @intFromEnum(protocol.TrustCommandKind.add) and
        session.findTrust(input.target_handle, input.family_handle) == null)
        demand.maximum_trust_count = occupied(session.trusts) + 1;
    return session.ensureShape(demand, .report_trust);
}

pub fn reportRows(session: *state.Session, count: u32) ResultCode {
    var demand = session.allocationShape();
    demand.maximum_report_count = count;
    return session.ensureShape(demand, .report_reports);
}

pub fn persistencePlan(session: *state.Session, count: u32) ResultCode {
    var demand = session.allocationShape();
    demand.maximum_persistence_operation_count = count;
    return session.ensureShape(demand, .report_persistence);
}
