const std = @import("std");
const protocol = @import("protocol.zig");
const state = @import("state.zig");
const ingest = @import("ingest.zig");
const planner = @import("planner.zig");
const feedback = @import("feedback.zig");
const scratch = @import("scratch.zig");
const disjoint = @import("../compute_scoring/scratch.zig").disjoint;

pub const Session = state.Session;

pub fn create(config: *const protocol.Config) !*Session {
    return Session.create(config);
}

pub fn destroy(session: *Session) void {
    session.destroy();
}

pub fn capacityForConfig(config: *const protocol.Config, capacity: *protocol.Capacity) protocol.Status {
    const validation = protocol.validateConfig(config);
    if (validation != .ok) return validation;
    protocol.fillCapacity(config, capacity);
    return .ok;
}

pub fn queryCapacity(session: *Session, capacity: *protocol.Capacity) protocol.Status {
    lock(&session.mutex);
    defer session.mutex.unlock();
    protocol.fillCapacity(&session.config, capacity);
    return .ok;
}

pub fn queryScratchLayout(session: *Session, requirements: *scratch.Requirements) protocol.Status {
    lock(&session.mutex);
    defer session.mutex.unlock();
    requirements.* = scratch.Layout.init(&session.config).requirements;
    return .ok;
}

pub fn queryScratchLayoutForRows(session: *Session, rows: u32, requirements: *scratch.Requirements) protocol.Status {
    lock(&session.mutex);
    defer session.mutex.unlock();
    if (rows > session.config.max_input_rows) return .capacity_exceeded;
    requirements.* = scratch.Layout.initForRows(&session.config, rows).requirements;
    return .ok;
}

pub fn reconfigure(session: *Session, config: *const protocol.Config) protocol.Status {
    lock(&session.mutex);
    defer session.mutex.unlock();
    const result = session.reconfigure(config);
    if (result == .ok) planner.resolveNextWake(session, session.last_observed_at_ms);
    return result;
}

pub fn reset(session: *Session) protocol.Status {
    lock(&session.mutex);
    defer session.mutex.unlock();
    return session.reset();
}

pub fn plan(
    session: *Session,
    input: *const protocol.CycleInput,
    input_rows: ?[*]const protocol.InputRow,
    input_row_capacity: u32,
    actions_pointer: ?[*]protocol.Action,
    action_capacity: u32,
    scratch_pointer: ?*anyopaque,
    scratch_length: u64,
    snapshot: *protocol.Snapshot,
) protocol.Status {
    lock(&session.mutex);
    defer session.mutex.unlock();

    const pointer = scratch_pointer orelse return .invalid_argument;
    if (!disjoint(pointer, scratch_length, input_rows, input_row_capacity, protocol.InputRow) or
        !disjoint(pointer, scratch_length, actions_pointer, action_capacity, protocol.Action) or
        !disjoint(pointer, scratch_length, @as(?[*]const protocol.CycleInput, @ptrCast(input)), 1, protocol.CycleInput) or
        !disjoint(pointer, scratch_length, @as(?[*]protocol.Snapshot, @ptrCast(snapshot)), 1, protocol.Snapshot)) return .invalid_argument;
    if (input.input_count > session.config.max_input_rows) return .capacity_exceeded;
    if (!scratch.Layout.initForRows(&session.config, input.input_count).bind(session, pointer, scratch_length)) return .invalid_argument;
    defer scratch.unbind(session);

    if (input.action_capacity != action_capacity) {
        return .buffer_too_small;
    }
    const actions = (actions_pointer orelse return .invalid_argument)[0..@intCast(action_capacity)];
    const stage_status = ingest.stage(session, input, input_rows, input_row_capacity);
    if (stage_status != .ok) return stage_status;
    if (action_capacity < planner.requiredActionCapacity(session)) return .buffer_too_small;
    const expiry_status = feedback.expireReservations(session, input.observed_at_ms);
    if (expiry_status != .ok) return expiry_status;
    if (session.requires_authoritative_resync and
        (input.flags & protocol.CycleFlags.authoritative_applied_facts) == 0)
    {
        return .unavailable;
    }
    return planner.advance(session, input, actions, snapshot);
}

pub fn applyFeedback(
    session: *Session,
    feedback_pointer: ?[*]const protocol.Feedback,
    feedback_count: u32,
    feedback_capacity: u32,
    snapshot: *protocol.Snapshot,
) protocol.Status {
    lock(&session.mutex);
    defer session.mutex.unlock();
    return feedback.applyBatch(session, feedback_pointer, feedback_count, feedback_capacity, snapshot);
}

pub fn getSnapshot(
    session: *Session,
    output: *protocol.Snapshot,
    row_pointer: ?[*]protocol.SnapshotRow,
    row_capacity: u32,
) protocol.Status {
    lock(&session.mutex);
    defer session.mutex.unlock();
    planner.fillSnapshot(session, null, 0, output);
    if (output.snapshot_row_count == 0) return .ok;
    if (row_capacity < output.snapshot_row_count) return .buffer_too_small;
    const rows = (row_pointer orelse return .invalid_argument)[0..@intCast(row_capacity)];
    return planner.copySnapshotRows(session, rows);
}

fn lock(mutex: *std.atomic.Mutex) void {
    while (!mutex.tryLock()) std.atomic.spinLoopHint();
}
