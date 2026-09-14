const std = @import("std");
const protocol = @import("protocol.zig");
const state = @import("state.zig");
const rules = @import("rules.zig");
const window = @import("window.zig");
const persistence = @import("persistence.zig");
const ResultCode = @import("../../common/result_codes.zig").ResultCode;

pub fn plan(
    session: *state.Session,
    input: *const protocol.PlanInput,
    reports: []protocol.ReportOutput,
    operations: []protocol.PersistenceOperation,
    output: *protocol.PlanOutput,
) ResultCode {
    output.* = std.mem.zeroes(protocol.PlanOutput);
    if (!session.rules_ready or !session.import_ready) return .unavailable;
    if (!protocol.validPlan(input, &session.config)) return .abi_mismatch;
    if (input.report_limit != reports.len or input.persistence_limit != operations.len) {
        return .invalid_argument;
    }
    if (session.planned_persistence_count != 0) {
        return replayPending(session, input, reports, operations, output);
    }
    if (input.plan_epoch <= session.last_plan_epoch or
        !session.operationAdvances(input.operation_epoch, input.command_monotonic_milliseconds))
    {
        return .stale_frame;
    }

    const saved_mutation = session.next_mutation_version;
    const saved_report = session.next_report_handle;
    const saved_report_slot = session.next_report_slot;
    const saved_metadata_mutation = session.metadata_mutation_version;
    const saved_metadata_logical_utc = session.metadata_logical_utc_ms;
    session.beginFactTransaction();
    const logical_now = @max(session.logical_utc_ms, input.command_utc_milliseconds);
    const maintenance = rules.maintain(session, logical_now);
    if (maintenance != .ok) {
        session.rollbackFactTransaction();
        restoreAggregateAfterRollback(session);
        session.next_mutation_version = saved_mutation;
        session.next_report_handle = saved_report;
        session.next_report_slot = saved_report_slot;
        session.metadata_mutation_version = saved_metadata_mutation;
        session.metadata_logical_utc_ms = saved_metadata_logical_utc;
        return maintenance;
    }

    const report_total = collectReportOrder(session);
    const report_count = @min(report_total, reports.len);
    for (session.report_order[0..report_count], reports[0..report_count]) |slot_index, *target| {
        target.* = reportOutput(session, slot_index);
    }
    for (reports[report_count..]) |*target| target.* = std.mem.zeroes(protocol.ReportOutput);

    const dirty = persistence.collectDirty(session, logical_now) catch {
        for (reports) |*target| target.* = std.mem.zeroes(protocol.ReportOutput);
        for (operations) |*target| target.* = std.mem.zeroes(protocol.PersistenceOperation);
        session.rollbackFactTransaction();
        restoreAggregateAfterRollback(session);
        session.next_mutation_version = saved_mutation;
        session.next_report_handle = saved_report;
        session.next_report_slot = saved_report_slot;
        session.metadata_mutation_version = saved_metadata_mutation;
        session.metadata_logical_utc_ms = saved_metadata_logical_utc;
        return .out_of_memory;
    };
    if (dirty.len > operations.len) {
        for (reports) |*target| target.* = std.mem.zeroes(protocol.ReportOutput);
        for (operations) |*target| target.* = std.mem.zeroes(protocol.PersistenceOperation);
        session.rollbackFactTransaction();
        restoreAggregateAfterRollback(session);
        session.next_mutation_version = saved_mutation;
        session.next_report_handle = saved_report;
        session.next_report_slot = saved_report_slot;
        session.metadata_mutation_version = saved_metadata_mutation;
        session.metadata_logical_utc_ms = saved_metadata_logical_utc;
        return .buffer_too_small;
    }
    const operation_count = dirty.len;
    for (dirty[0..operation_count], operations[0..operation_count]) |reference, *target| {
        target.* = persistence.operationFor(session, reference, input.plan_epoch);
    }
    for (operations[operation_count..]) |*target| {
        target.* = std.mem.zeroes(protocol.PersistenceOperation);
    }
    @memset(session.planned_persistence_index, state.no_index);
    for (operations[0..operation_count], session.planned_persistence[0..operation_count], 0..) |
        operation,
        *reference,
        planned_index,
    | {
        reference.* = .{
            .plan_epoch = operation.plan_epoch,
            .mutation_version = operation.mutation_version,
            .identity_handle = operation.identity_handle,
            .slot_generation = operation.slot_generation,
            .kind = operation.operation_kind,
            .slot_index = operation.slot_index,
        };
        if (!session.insertPlannedPersistenceIndex(@intCast(planned_index))) {
            @memset(session.planned_persistence_index, state.no_index);
            for (reports) |*target| target.* = std.mem.zeroes(protocol.ReportOutput);
            for (operations) |*target| target.* = std.mem.zeroes(protocol.PersistenceOperation);
            session.rollbackFactTransaction();
            restoreAggregateAfterRollback(session);
            session.next_mutation_version = saved_mutation;
            session.next_report_handle = saved_report;
            session.next_report_slot = saved_report_slot;
            session.metadata_mutation_version = saved_metadata_mutation;
            session.metadata_logical_utc_ms = saved_metadata_logical_utc;
            return .invalid_argument;
        }
    }
    session.planned_persistence_count = @intCast(operation_count);

    session.commitFactTransaction();
    session.last_plan_epoch = input.plan_epoch;
    session.commitOperation(
        input.operation_epoch,
        input.command_monotonic_milliseconds,
        input.command_utc_milliseconds,
    );

    var flags: u64 = 0;
    if (report_count != 0) flags |= protocol.PlanFlags.reports_available;
    if (operation_count != 0) flags |= protocol.PlanFlags.persistence_available;
    if (report_total > report_count) flags |= protocol.PlanFlags.has_more_reports;
    if (dirty.len > operation_count) flags |= protocol.PlanFlags.has_more_persistence;
    if (session.clock_rollback_observed) flags |= protocol.PlanFlags.clock_rollback_observed;
    const next_wake = nextWake(session, logical_now);
    if (next_wake != 0) flags |= protocol.PlanFlags.next_wake_valid;

    output.* = .{
        .struct_size = @sizeOf(protocol.PlanOutput),
        .report_count = @intCast(report_count),
        .persistence_operation_count = @intCast(operation_count),
        .active_report_count = @intCast(report_total),
        .trusted_report_count = trustedReportCount(session),
        .source_count = occupiedCount(state.SourceSlot, session.sources),
        .observation_count = occupiedCount(state.ObservationSlot, session.observations),
        .bucket_count = occupiedCount(state.BucketSlot, session.buckets),
        .report_total_count = occupiedCount(state.ReportSlot, session.reports),
        .trust_count = committedTrustCount(session),
        .reserved_u32 = 0,
        .first_mutation_version = if (operation_count == 0)
            0
        else
            operations[0].mutation_version,
        .last_mutation_version = if (operation_count == 0)
            0
        else
            operations[operation_count - 1].mutation_version,
        .session_instance_low = session.config.session_instance_low,
        .session_instance_high = session.config.session_instance_high,
        .last_operation_epoch = session.last_operation_epoch,
        .last_plan_epoch = session.last_plan_epoch,
        .last_feedback_epoch = session.last_feedback_epoch,
        .last_command_monotonic_milliseconds = session.last_command_monotonic_milliseconds,
        .logical_utc_milliseconds = session.logical_utc_ms,
        .next_wake_utc_milliseconds = next_wake,
        .state_revision = session.state_revision,
        .import_generation = session.import_generation,
        .flags = flags,
        .resident_byte_count = session.resident_byte_count,
        .reserved = .{ 0, 0 },
    };
    if (operation_count != 0) session.planned_output = output.*;
    return .ok;
}

fn replayPending(
    session: *state.Session,
    input: *const protocol.PlanInput,
    reports: []protocol.ReportOutput,
    operations: []protocol.PersistenceOperation,
    output: *protocol.PlanOutput,
) ResultCode {
    const planned = session.planned_output;
    if (input.plan_epoch != session.last_plan_epoch or
        input.operation_epoch != session.last_operation_epoch or
        input.command_monotonic_milliseconds != session.last_command_monotonic_milliseconds or
        input.command_utc_milliseconds != session.last_command_utc_ms)
    {
        return .stale_frame;
    }
    if (reports.len < planned.report_count or
        operations.len < session.planned_persistence_count)
    {
        return .buffer_too_small;
    }

    const report_total = collectReportOrder(session);
    if (report_total < planned.report_count) return .invalid_argument;
    for (
        session.report_order[0..planned.report_count],
        reports[0..planned.report_count],
    ) |slot_index, *target| {
        target.* = reportOutput(session, slot_index);
    }
    for (reports[planned.report_count..]) |*target| {
        target.* = std.mem.zeroes(protocol.ReportOutput);
    }
    for (
        session.planned_persistence[0..session.planned_persistence_count],
        operations[0..session.planned_persistence_count],
    ) |reference, *target| {
        target.* = persistence.operationFor(session, .{
            .mutation_version = reference.mutation_version,
            .index = reference.slot_index,
            .kind = @enumFromInt(reference.kind),
        }, reference.plan_epoch);
    }
    for (operations[session.planned_persistence_count..]) |*target| {
        target.* = std.mem.zeroes(protocol.PersistenceOperation);
    }
    output.* = planned;
    return .ok;
}

fn collectReportOrder(session: *state.Session) usize {
    var count: usize = 0;
    for (session.reports, 0..) |report, index| {
        if (!report.occupied or report.delete_pending or !report.active) continue;
        session.report_order[count] = @intCast(index);
        count += 1;
    }
    std.sort.heap(u32, session.report_order[0..count], session, reportBefore);
    return count;
}

fn reportBefore(session: *state.Session, left_index: u32, right_index: u32) bool {
    const left = session.reports[left_index];
    const right = session.reports[right_index];
    if (left.severity != right.severity) return left.severity > right.severity;
    if (left.priority != right.priority) return left.priority > right.priority;
    return left.report_handle < right.report_handle;
}

fn reportOutput(
    session: *const state.Session,
    slot_index: u32,
) protocol.ReportOutput {
    const report = session.reports[slot_index];
    const observation = if (report.observation_index != state.no_index and
        report.observation_index < session.observations.len)
        session.observations[report.observation_index]
    else
        state.ObservationSlot{};
    const windows = if (report.observation_index == state.no_index)
        window.WindowValues{}
    else
        window.values(session, report.observation_index);
    var flags: u32 = protocol.ReportFlags.active;
    if (report.trusted_suppressed) flags |= protocol.ReportFlags.trusted_suppressed;
    return .{
        .struct_size = @sizeOf(protocol.ReportOutput),
        .flags = flags,
        .report_handle = report.report_handle,
        .slot_generation = report.slot_generation,
        .target_handle = report.target_handle,
        .family_handle = report.family_handle,
        .rule_handle = report.rule_handle,
        .report_type_handle = report.report_type_handle,
        .resource_kind_handle = report.resource_kind_handle,
        .payload_handle = report.payload_handle,
        .evidence_payload_handle = report.evidence_payload_handle,
        .created_at_utc_milliseconds = report.created_at_utc_ms,
        .updated_at_utc_milliseconds = report.updated_at_utc_ms,
        .last_observed_at_utc_milliseconds = report.last_observed_at_utc_ms,
        .current_value = if (observation.current_valid) observation.current_value else 0,
        .average_value = if (observation.sample_count == 0)
            0
        else
            observation.value_sum / @as(f64, @floatFromInt(observation.sample_count)),
        .peak_value = observation.peak_value,
        .window_24h_value = windows.sum_24h,
        .window_7d_value = windows.sum_7d,
        .sample_count = observation.sample_count,
        .active_sample_count = observation.active_sample_count,
        .priority = report.priority,
        .severity = report.severity,
        .slot_index = slot_index,
        .reserved_u32 = 0,
        .reserved = .{ 0, 0 },
    };
}

fn nextWake(session: *const state.Session, now_utc_ms: i64) i64 {
    var next = window.nextBoundary(session, now_utc_ms);
    for (session.observations) |observation| {
        if (!observation.occupied or observation.delete_pending) continue;
        const rule_index = session.findRule(observation.rule_handle) orelse continue;
        const rule = session.rules[rule_index].value;
        const duration = if (observation.active)
            if (rule.stale_after_milliseconds == 0)
                session.config.default_stale_after_milliseconds
            else
                rule.stale_after_milliseconds
        else if (rule.retention_milliseconds == 0)
            session.config.default_retention_milliseconds
        else
            rule.retention_milliseconds;
        const deadline = addSaturatingI64(observation.last_observed_at_utc_ms, duration);
        if (deadline > now_utc_ms and (next == 0 or deadline < next)) next = deadline;
    }
    return next;
}

fn occupiedCount(comptime T: type, slots: []const T) u32 {
    var count: u32 = 0;
    for (slots) |slot| {
        if (slot.occupied) {
            count += 1;
        }
    }
    return count;
}

fn trustedReportCount(session: *const state.Session) u32 {
    var count: u32 = 0;
    for (session.reports) |report| {
        if (report.occupied and report.active and report.trusted_suppressed and
            !report.delete_pending) count += 1;
    }
    return count;
}

fn committedTrustCount(session: *const state.Session) u32 {
    var count: u32 = 0;
    for (session.trusts) |trust| {
        if (trust.occupied and trust.committed) count += 1;
    }
    return count;
}

fn addSaturatingI64(value: i64, increment: u64) i64 {
    if (increment > std.math.maxInt(i64)) return std.math.maxInt(i64);
    return std.math.add(i64, value, @intCast(increment)) catch std.math.maxInt(i64);
}

fn restoreAggregateAfterRollback(session: *state.Session) void {
    if (window.rebuildAggregate(session, session.logical_utc_ms) != .ok) unreachable;
}
