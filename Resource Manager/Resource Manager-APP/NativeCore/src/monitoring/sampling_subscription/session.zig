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
    return session.track(input, items);
}

pub fn remove(session: *Session, input: *const protocol.RemoveInput) ResultCode {
    state.lockMutex(&session.mutex);
    defer session.mutex.unlock();
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
    );
}

pub fn complete(
    session: *Session,
    input: *const protocol.CompletionInput,
    item_pointer: ?[*]const protocol.ItemReference,
    item_count: u32,
    output: *protocol.CompletionOutput,
) ResultCode {
    if (item_count == 0) return .invalid_argument;
    const items = (item_pointer orelse return .invalid_argument)[0..item_count];
    return completion.complete(session, input, items, output);
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
