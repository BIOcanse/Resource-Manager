const std = @import("std");
const protocol = @import("protocol.zig");
const state = @import("state.zig");
const ResultCode = @import("../../common/result_codes.zig").ResultCode;

pub fn clear(
    session: *state.Session,
    input: *const protocol.ClearHistoryInput,
    removed_count: *u32,
) ResultCode {
    removed_count.* = 0;
    if (!session.rules_ready or !session.import_ready) return .unavailable;
    if (!protocol.validClearHistory(input, &session.config)) return .abi_mismatch;
    if (session.planned_persistence_count != 0) return .unavailable;
    if (!session.operationAdvances(input.operation_epoch, input.command_monotonic_milliseconds)) return .stale_frame;
    var count: u32 = 0;
    for (session.reports) |report| {
        if (report.occupied and !report.active and !report.delete_pending) count += 1;
    }
    const mutations = @as(u64, count) + 1;
    if (count != 0 and (session.next_mutation_version == 0 or
        mutations > std.math.maxInt(u64) - session.next_mutation_version)) return .out_of_memory;
    for (session.reports) |*report| {
        if (!report.occupied or report.active or report.delete_pending) continue;
        report.delete_pending = true;
        report.mutation_version = session.allocateMutation().?;
    }
    if (count != 0) {
        // The checkpoint carries issued report identity highwater. It must be
        // committed with deletion even when the clock has not advanced.
        session.metadata_mutation_version = session.allocateMutation().?;
        session.metadata_logical_utc_ms = @max(session.logical_utc_ms, input.command_utc_milliseconds);
    }
    session.commitOperation(input.operation_epoch, input.command_monotonic_milliseconds, input.command_utc_milliseconds);
    removed_count.* = count;
    return .ok;
}
