const protocol = @import("protocol.zig");

pub fn config(generation: u64) protocol.Config {
    return .{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.Config),
        .generation = generation,
        .maximum_desired_count = 8,
        .maximum_applied_count = 8,
        .maximum_action_count = 8,
        .maximum_state_count = 8,
        .retry_delay_milliseconds = 100,
        .action_timeout_milliseconds = 50,
        .maximum_future_skew_milliseconds = 5,
        .flags = 0,
        .reserved = .{ 0, 0 },
    };
}

pub fn cycle(generation: u64, epoch: u64, now: u64, desired_count: u32, applied_count: u32) protocol.CycleInput {
    return .{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.CycleInput),
        .configuration_generation = generation,
        .cycle_epoch = epoch,
        .observed_at_milliseconds = now,
        .valid_mask = protocol.CycleValid.required,
        .desired_count = desired_count,
        .applied_count = applied_count,
        .action_capacity = 8,
        .action_count = 0,
        .next_wake_milliseconds = 0,
        .state_revision = 0,
        .reserved = .{ 0, 0 },
    };
}

pub fn desired(target_key: u64, record_key: u64, digest: u64) protocol.DesiredInput {
    return .{
        .struct_size = @sizeOf(protocol.DesiredInput),
        .flags = 0,
        .target_key = target_key,
        .record_key = record_key,
        .resource_kind = @intFromEnum(protocol.ResourceKind.cpu),
        .placement_kind = @intFromEnum(protocol.PlacementKind.cpu_sets),
        .desired_digest = digest,
        .process_start_key = 22,
        .process_id = 11,
        .priority = 0,
        .valid_mask = protocol.DesiredValid.required | protocol.DesiredValid.process_identity,
        .reserved = 0,
    };
}

pub fn applied(
    target_key: u64,
    record_key: u64,
    receipt_digest: u64,
    previous_digest: u64,
    current_digest: u64,
) protocol.AppliedInput {
    return .{
        .struct_size = @sizeOf(protocol.AppliedInput),
        .flags = protocol.AppliedFlags.payload_valid,
        .target_key = target_key,
        .record_key = record_key,
        .resource_kind = @intFromEnum(protocol.ResourceKind.cpu),
        .placement_kind = @intFromEnum(protocol.PlacementKind.cpu_sets),
        .receipt_digest = receipt_digest,
        .previous_digest = previous_digest,
        .current_digest = current_digest,
        .process_start_key = 22,
        .process_id = 11,
        .observation_status = @intFromEnum(protocol.ObservationStatus.found),
        .valid_mask = protocol.AppliedValid.required |
            protocol.AppliedValid.current_digest |
            protocol.AppliedValid.process_identity,
        .reserved = 0,
    };
}

pub fn feedback(
    action: protocol.ActionOutput,
    status: protocol.FeedbackStatus,
    completed_at: u64,
) protocol.FeedbackInput {
    return .{
        .struct_size = @sizeOf(protocol.FeedbackInput),
        .status = @intFromEnum(status),
        .action_id = action.action_id,
        .target_key = action.target_key,
        .record_key = action.record_key,
        .completed_at_milliseconds = completed_at,
        .system_error_code = 0,
        .flags = 0,
        .observed_digest = action.desired_digest,
        .valid_mask = protocol.FeedbackValid.observed_digest,
    };
}
