const protocol = @import("protocol.zig");
const state = @import("state.zig");
const planner = @import("planner.zig");
const completion = @import("completion.zig");
const snapshot_module = @import("snapshot.zig");
const ResultCode = @import("../../common/result_codes.zig").ResultCode;

pub const Session = state.Session;

pub fn track(
    session: *Session,
    input: *const protocol.TrackInput,
    item_pointer: ?[*]const protocol.ItemReference,
    item_count: u32,
) ResultCode {
    if (item_count == 0) return .invalid_argument;
    const items = (item_pointer orelse return .invalid_argument)[0..item_count];
    state.lockMutex(&session.mutex);
    defer session.mutex.unlock();
    const begun = session.beginScratch(session.scratchRowsFor(items.len, true));
    if (begun != .ok) return begun;
    defer session.endScratch();
    return session.track(input, items);
}

pub fn remove(session: *Session, input: *const protocol.RemoveInput) ResultCode {
    state.lockMutex(&session.mutex);
    defer session.mutex.unlock();
    const begun = session.beginScratch(session.scratchRowsFor(0, false));
    if (begun != .ok) return begun;
    defer session.endScratch();
    return session.remove(input);
}

pub fn plan(
    session: *Session,
    header: *protocol.PlanHeader,
    due_pointer: ?[*]protocol.DueItemOutput,
    due_capacity: u32,
    source_pointer: ?[*]protocol.SourceViewOutput,
    source_capacity: u32,
    expired_pointer: ?[*]protocol.ExpiredSourceOutput,
    expired_capacity: u32,
    trigger_pointer: ?[*]protocol.TriggerOutput,
    trigger_capacity: u32,
) ResultCode {
    return planner.plan(
        session,
        header,
        due_pointer,
        due_capacity,
        source_pointer,
        source_capacity,
        expired_pointer,
        expired_capacity,
        trigger_pointer,
        trigger_capacity,
    );
}

pub fn complete(
    session: *Session,
    input: *const protocol.CompletionInput,
    item_pointer: ?[*]const protocol.ItemReference,
    item_count: u32,
    trigger_pointer: ?[*]const protocol.TriggerOutput,
    trigger_count: u32,
    output: *protocol.CompletionOutput,
) ResultCode {
    if (item_count == 0 and trigger_count == 0) return .invalid_argument;
    const items: []const protocol.ItemReference = if (item_count == 0)
        &.{}
        else
            (item_pointer orelse return .invalid_argument)[0..item_count];
    const triggers: []const protocol.TriggerOutput = if (trigger_count == 0)
        &.{}
        else
        (trigger_pointer orelse return .invalid_argument)[0..trigger_count];
    return completion.complete(session, input, items, triggers, output);
}

pub fn snapshot(
    session: *Session,
    header: *protocol.SnapshotHeader,
    source_pointer: ?[*]protocol.SourceStateOutput,
    source_capacity: u32,
    item_pointer: ?[*]protocol.ItemStateOutput,
    item_capacity: u32,
) ResultCode {
    return snapshot_module.snapshot(
        session,
        header,
        source_pointer,
        source_capacity,
        item_pointer,
        item_capacity,
    );
}
