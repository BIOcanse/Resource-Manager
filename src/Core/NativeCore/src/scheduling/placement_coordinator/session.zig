const protocol = @import("protocol.zig");
const state = @import("state.zig");
const planner = @import("planner.zig");
const feedback = @import("feedback.zig");
const ResultCode = @import("../../common/result_codes.zig").ResultCode;

const empty_states = [_]protocol.StateOutput{};

pub const Session = state.Session;

pub fn plan(
    session: *Session,
    cycle: *protocol.CycleInput,
    desired_pointer: ?[*]const protocol.DesiredInput,
    desired_capacity: u32,
    applied_pointer: ?[*]const protocol.AppliedInput,
    applied_capacity: u32,
    action_pointer: ?[*]protocol.ActionOutput,
    action_capacity: u32,
) ResultCode {
    return planner.plan(
        session,
        cycle,
        desired_pointer,
        desired_capacity,
        applied_pointer,
        applied_capacity,
        action_pointer,
        action_capacity,
    );
}

pub fn applyFeedback(
    session: *Session,
    feedback_pointer: ?[*]const protocol.FeedbackInput,
    feedback_count: u32,
) ResultCode {
    return feedback.apply(session, feedback_pointer, feedback_count);
}

pub fn snapshot(
    session: *Session,
    header: *protocol.SnapshotHeader,
    state_pointer: ?[*]protocol.StateOutput,
    state_capacity: u32,
) ResultCode {
    state.lockMutex(&session.mutex);
    defer session.mutex.unlock();
    if (header.abi_version != protocol.abi_version or
        header.struct_size != @sizeOf(protocol.SnapshotHeader) or
        header.configuration_generation != 0 or
        header.state_revision != 0 or
        header.last_cycle_epoch != 0 or
        header.next_action_id != 0 or
        header.active_state_count != 0 or
        header.pending_apply_count != 0 or
        header.pending_restore_count != 0 or
        header.blocked_count != 0 or
        header.retry_wait_count != 0 or
        header.state_output_count != 0 or
        header.flags != 0 or
        header.next_wake_milliseconds != 0 or
        header.reserved[0] != 0 or header.reserved[1] != 0)
    {
        return .abi_mismatch;
    }
    if (state_capacity < session.config.maximum_state_count) return .buffer_too_small;
    const outputs = if (state_capacity == 0)
        @constCast(empty_states[0..])
    else
        (state_pointer orelse return .invalid_argument)[0..state_capacity];
    @memset(outputs, @import("std").mem.zeroes(protocol.StateOutput));

    var output_count: usize = 0;
    var pending_apply_count: u32 = 0;
    var pending_restore_count: u32 = 0;
    var blocked_count: u32 = 0;
    var retry_wait_count: u32 = 0;
    for (session.slots) |slot| {
        if (!slot.active) continue;
        outputs[output_count] = .{
            .struct_size = @sizeOf(protocol.StateOutput),
            .state = @intFromEnum(slot.state),
            .target_key = slot.target_key,
            .record_key = slot.record_key,
            .resource_kind = slot.resource_kind,
            .placement_kind = slot.placement_kind,
            .desired_digest = slot.desired_digest,
            .applied_digest = slot.applied_digest,
            .previous_digest = slot.previous_digest,
            .pending_action_id = slot.pending_action_id,
            .retry_at_milliseconds = slot.retry_at_milliseconds,
            .last_cycle_epoch = slot.last_cycle_epoch,
            .process_start_key = if (slot.desired_process_start_key != 0)
                slot.desired_process_start_key
            else
                slot.applied_process_start_key,
            .process_id = if (slot.desired_process_id != 0)
                slot.desired_process_id
            else
                slot.applied_process_id,
            .retry_count = slot.retry_count,
            .reserved = .{ 0, 0 },
        };
        output_count += 1;
        switch (slot.state) {
            .pending_apply => pending_apply_count += 1,
            .pending_restore => pending_restore_count += 1,
            .blocked => blocked_count += 1,
            .retry_wait => retry_wait_count += 1,
            else => {},
        }
    }

    header.* = .{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.SnapshotHeader),
        .configuration_generation = session.config.generation,
        .state_revision = session.state_revision,
        .last_cycle_epoch = session.last_cycle_epoch,
        .next_action_id = session.next_action_id,
        .active_state_count = @intCast(output_count),
        .pending_apply_count = pending_apply_count,
        .pending_restore_count = pending_restore_count,
        .blocked_count = blocked_count,
        .retry_wait_count = retry_wait_count,
        .state_output_count = @intCast(output_count),
        .flags = if (session.next_wake_milliseconds != 0)
            protocol.SnapshotFlags.next_wake_valid
        else
            0,
        .next_wake_milliseconds = session.next_wake_milliseconds,
        .reserved = .{ 0, 0 },
    };
    return .ok;
}
