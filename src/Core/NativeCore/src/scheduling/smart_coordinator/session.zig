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

pub fn workspaceCapacity(session: *Session, output: *protocol.WorkspaceCapacity) protocol.Status {
    lock(&session.mutex);
    defer session.mutex.unlock();
    session.fillWorkspaceCapacity(output);
    return .ok;
}

pub fn queryScratchLayout(session: *Session, requirements: *scratch.Requirements) protocol.Status {
    lock(&session.mutex);
    defer session.mutex.unlock();
    requirements.* = scratch.Layout.init(&session.config).requirements;
    return .ok;
}

// The measure table belongs to the caller's fact region, so its slot count is
// reported before the facts are projected.
pub fn queryMeasureTable(session: *Session, rows: u32, table_count: *u64) protocol.Status {
    lock(&session.mutex);
    defer session.mutex.unlock();
    if (rows > session.config.max_input_rows) return .capacity_exceeded;
    table_count.* = state.hashIndexCapacity(@max(1, rows)) catch return .out_of_memory;
    return .ok;
}

// Native reports each plan column's row count before the caller opens it: one
// staging column per entity kind and the candidate rows plan() writes into the
// action buffer. Cycle validation stays in plan(), so its statuses are unchanged.
pub fn measurePlan(
    session: *Session,
    input: *const protocol.CycleInput,
    input_rows: ?[*]const protocol.InputRow,
    input_row_capacity: u32,
    table_pointer: ?[*]u32,
    table_count: u64,
    requirements: *scratch.Requirements,
    action_rows: *u32,
) protocol.Status {
    lock(&session.mutex);
    defer session.mutex.unlock();

    if (input.input_count > session.config.max_input_rows) return .capacity_exceeded;
    if (input_row_capacity < input.input_count) return .buffer_too_small;
    const required_table = state.hashIndexCapacity(@max(1, input.input_count)) catch return .out_of_memory;
    if (table_count < required_table) return .buffer_too_small;
    if (!std.math.isPowerOfTwo(table_count) or table_count > std.math.maxInt(usize) / @sizeOf(u32)) return .invalid_argument;
    const table_start = table_pointer orelse return .invalid_argument;
    if (!disjoint(@ptrCast(table_start), table_count * @sizeOf(u32), input_rows, input_row_capacity, protocol.InputRow)) return .invalid_argument;
    const rows: []const protocol.InputRow = if (input.input_count == 0)
        &[_]protocol.InputRow{}
    else
        (input_rows orelse return .invalid_argument)[0..@intCast(input.input_count)];
    const measured = ingest.measure(session, rows, table_start[0..@intCast(table_count)]);
    requirements.* = scratch.Layout.initCounts(
        @min(measured.processes, session.config.max_processes),
        @min(measured.softwares, session.config.max_software_groups),
        @min(measured.gpus, session.config.max_gpu_states),
    ).requirements;
    action_rows.* = measured.actions;
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
    requirements: *const scratch.Requirements,
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
    const layout = scratch.Layout.initMeasured(&session.config, requirements) orelse return .invalid_argument;
    if (!layout.bind(session, pointer, scratch_length)) return .invalid_argument;
    defer scratch.unbind(session);

    if (input.action_capacity != action_capacity) {
        return .buffer_too_small;
    }
    const actions = (actions_pointer orelse return .invalid_argument)[0..@intCast(action_capacity)];
    const stage_status = ingest.stage(session, input, input_rows, input_row_capacity);
    if (stage_status != .ok) return stage_status;
    if (action_capacity < planner.requiredActionCapacity(session)) return .buffer_too_small;
    var demand: state.BankDemand = undefined;
    const demand_status = planner.bankDemand(session, input, &demand);
    if (demand_status != .ok) return demand_status;
    if (planner.hasCapacityForPlan(session, input, demand)) {
        return advanceStaged(session, input, actions, snapshot);
    }
    // Run the existing planner once against private authority. It discovers
    // exact reservations/groups on insertion; only a successful plan is swapped
    // into the live session while this mutex remains held.
    var candidate = session.cloneBanks() catch return .out_of_memory;
    var committed = false;
    defer if (!committed) candidate.freeBanks();
    const growth_status = candidate.ensureBankCapacity(demand);
    if (growth_status != .ok) return growth_status;
    const status = advanceStaged(&candidate, input, actions, snapshot);
    if (status != .ok) {
        // Timeout observation keeps the existing resynchronization boundary.
        // Allocation failures never publish any part of the candidate.
        if (status == .unavailable and candidate.state_revision != session.state_revision) {
            session.commitBanks(&candidate);
            committed = true;
        }
        return status;
    }
    session.commitBanks(&candidate);
    committed = true;
    return .ok;
}

fn advanceStaged(session: *Session, input: *const protocol.CycleInput, actions: []protocol.Action, snapshot: *protocol.Snapshot) protocol.Status {
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
