const std = @import("std");
const types = @import("types.zig");
const capacity_module = @import("capacity.zig");

pub const ErrorCode = enum(u16) {
    none = 0,
    policy_not_accepted = 1,
    action_result_count_mismatch = 2,
    action_result_not_completed = 3,
    identity_mismatch = 4,
    invalid_transition = 5,
    source_capacity_overflow = 6,
    target_capacity_underflow = 7,
    invalid_input = 8,
};

pub const Disposition = enum(u8) {
    retain = 1,
    complete = 2,
};

pub fn apply(
    danger_min_physical_after_vram_move_ratio: f64,
    danger_min_virtual_after_physical_move_ratio: f64,
    expected_action_gate_epoch: u64,
    request: *const types.FeedbackRequest,
    selection: *const types.SelectionOutput,
    capacity_input: *const types.CapacityInput,
    action: *const types.ActionFeedbackInput,
    output: *types.FeedbackOutput,
) types.PlanError!Disposition {
    if (!validRatio(danger_min_physical_after_vram_move_ratio) or
        !validRatio(danger_min_virtual_after_physical_move_ratio))
    {
        return error.InvalidConfig;
    }
    output.* = failure(capacity_input.*, .invalid_input, 0, .retain);
    if (!validRequest(request) or !validSelection(selection) or
        !allZero(action.reserved0[0..]))
    {
        return error.InvalidFeedback;
    }
    if (request.expected_request_id != selection.request_id or
        action.request_id != selection.request_id or
        !types.sameExecutionAuthority(action.authority, selection.authority) or
        action.action_gate_epoch != expected_action_gate_epoch or
        expected_action_gate_epoch != 0 or
        action.previous_tier != selection.tier or
        action.status > 5)
    {
        output.* = failure(
            capacity_input.*,
            .identity_mismatch,
            request.policy_danger_flags,
            .retain,
        );
        return error.InvalidFeedback;
    }
    if (request.policy_accepted == 0) {
        const result_code = if (request.policy_result_code == @intFromEnum(types.ResultCode.unknown))
            @intFromEnum(types.ResultCode.rejected)
        else
            request.policy_result_code;
        output.* = policyFailure(
            capacity_input.*,
            .policy_not_accepted,
            result_code,
            request.policy_danger_flags,
            .complete,
        );
        return .complete;
    }
    if (request.action_result_count != 1) {
        output.* = failure(
            capacity_input.*,
            .action_result_count_mismatch,
            request.policy_danger_flags,
            .retain,
        );
        return error.InvalidFeedback;
    }
    if (action.status != 0) {
        const disposition: Disposition = switch (action.status) {
            1, 2, 3 => .complete,
            4, 5 => .retain,
            else => unreachable,
        };
        output.* = failure(
            capacity_input.*,
            .action_result_not_completed,
            request.policy_danger_flags,
            disposition,
        );
        return disposition;
    }

    const target_tier = transitionTarget(selection.tier, selection.authority.action) orelse {
        output.* = failure(
            capacity_input.*,
            .invalid_transition,
            request.policy_danger_flags,
            .complete,
        );
        return .complete;
    };
    if (!validTransition(selection, action, target_tier)) {
        output.* = failure(
            capacity_input.*,
            .invalid_transition,
            request.policy_danger_flags,
            .complete,
        );
        return .complete;
    }

    var resolved = capacity_module.resolve(capacity_input) catch return error.InvalidCapacity;
    const source_index = types.tierIndex(selection.tier) orelse return error.InvalidFeedback;
    const target_index = types.tierIndex(target_tier) orelse return error.InvalidFeedback;
    if (!increaseFree(&resolved, source_index, action.released_bytes)) {
        output.* = failure(
            capacity_input.*,
            .source_capacity_overflow,
            dangerForTier(selection.tier),
            .complete,
        );
        return .complete;
    }
    if (source_index != target_index and !decreaseFree(&resolved, target_index, action.resident_bytes)) {
        output.* = failure(
            capacity_input.*,
            .target_capacity_underflow,
            dangerForTier(target_tier) | types.danger_paging_stall,
            .complete,
        );
        return .complete;
    }

    const projected = projectCapacity(capacity_input, &resolved);
    var result_code = if (request.policy_result_code == @intFromEnum(types.ResultCode.unknown))
        @intFromEnum(types.ResultCode.applied)
    else
        request.policy_result_code;
    var danger_flags = request.policy_danger_flags;
    const post_danger = postActionDanger(
        danger_min_physical_after_vram_move_ratio,
        danger_min_virtual_after_physical_move_ratio,
        selection,
        &resolved,
    );
    if (post_danger != 0) {
        result_code = @intFromEnum(types.ResultCode.target_pool_danger);
        danger_flags |= post_danger;
    }
    output.* = .{
        .projected_capacity = projected,
        .applied = 1,
        .result_code = result_code,
        .danger_flags = danger_flags,
        .current_tier = target_tier,
        .error_code = @intFromEnum(ErrorCode.none),
        .reservation_disposition = @intFromEnum(Disposition.complete),
        .reserved0 = 0,
        .reserved1 = 0,
    };
    return .complete;
}

fn validRequest(value: *const types.FeedbackRequest) bool {
    return value.abi_version == types.protocol_version and
        value.struct_size == @sizeOf(types.FeedbackRequest) and
        value.expected_request_id > 0 and
        value.policy_result_code <= @intFromEnum(types.ResultCode.ledger_drift_danger) and
        value.policy_danger_flags & ~types.all_danger_bits == 0 and
        value.policy_accepted <= 1 and value.reserved0 == 0 and value.reserved1 == 0;
}

fn validSelection(value: *const types.SelectionOutput) bool {
    return value.target_key != 0 and value.request_id != 0 and
        types.validExecutionAuthority(value.authority) and
        value.size_bytes != 0 and
        value.configuration_generation != 0 and
        types.tierIndex(value.tier) != null and allZero(value.reserved0[0..]);
}

fn transitionTarget(tier: u8, action: u8) ?u8 {
    if (action == types.action_discard or action == types.action_trim) return tier;
    if (action == types.action_move_down) {
        return switch (tier) {
            @intFromEnum(types.Tier.vram) => @intFromEnum(types.Tier.physical_memory),
            @intFromEnum(types.Tier.physical_memory) => @intFromEnum(types.Tier.virtual_memory),
            else => null,
        };
    }
    if (action == types.action_move_up) {
        return switch (tier) {
            @intFromEnum(types.Tier.virtual_memory) => @intFromEnum(types.Tier.physical_memory),
            @intFromEnum(types.Tier.physical_memory) => @intFromEnum(types.Tier.vram),
            else => null,
        };
    }
    return null;
}

fn validTransition(selection: *const types.SelectionOutput, action: *const types.ActionFeedbackInput, target_tier: u8) bool {
    if (action.current_tier != target_tier) return false;
    return switch (selection.authority.action) {
        types.action_discard => action.released_bytes == selection.size_bytes and action.resident_bytes == 0,
        types.action_trim => action.released_bytes <= selection.size_bytes and
            action.resident_bytes <= selection.size_bytes and
            std.math.maxInt(u64) - action.released_bytes >= action.resident_bytes and
            action.released_bytes + action.resident_bytes == selection.size_bytes,
        types.action_move_down, types.action_move_up => action.released_bytes == selection.size_bytes and action.resident_bytes > 0,
        else => false,
    };
}

fn increaseFree(value: *capacity_module.Resolved, index: usize, bytes: u64) bool {
    const total = value.total_bytes[index];
    const free = value.free_bytes[index];
    if (total == 0 or std.math.maxInt(u64) - free < bytes or free + bytes > total) return false;
    value.free_bytes[index] = free + bytes;
    value.free_ratio[index] = @as(f64, @floatFromInt(value.free_bytes[index])) / @as(f64, @floatFromInt(total));
    return true;
}

fn decreaseFree(value: *capacity_module.Resolved, index: usize, bytes: u64) bool {
    const total = value.total_bytes[index];
    const free = value.free_bytes[index];
    if (total == 0 or free < bytes) return false;
    value.free_bytes[index] = free - bytes;
    value.free_ratio[index] = @as(f64, @floatFromInt(value.free_bytes[index])) / @as(f64, @floatFromInt(total));
    return true;
}

fn projectCapacity(source: *const types.CapacityInput, resolved: *const capacity_module.Resolved) types.CapacityInput {
    var result = source.*;
    result.free_vram_bytes = resolved.free_bytes[0];
    result.free_physical_bytes = resolved.free_bytes[1];
    result.free_virtual_bytes = resolved.free_bytes[2];
    var index: usize = 0;
    while (index < 3) : (index += 1) {
        if (resolved.total_bytes[index] != 0) result.free_bytes_valid_mask |= @as(u8, 1) << @intCast(index);
    }
    return result;
}

fn postActionDanger(
    danger_min_physical_after_vram_move_ratio: f64,
    danger_min_virtual_after_physical_move_ratio: f64,
    selection: *const types.SelectionOutput,
    resolved: *const capacity_module.Resolved,
) u8 {
    if (selection.authority.action != types.action_move_down) return 0;
    if (selection.tier == @intFromEnum(types.Tier.vram) and
        resolved.free_ratio[@intFromEnum(types.Tier.physical_memory)] < danger_min_physical_after_vram_move_ratio)
    {
        return types.danger_memory | types.danger_paging_stall;
    }
    if (selection.tier == @intFromEnum(types.Tier.physical_memory) and
        resolved.free_ratio[@intFromEnum(types.Tier.virtual_memory)] < danger_min_virtual_after_physical_move_ratio)
    {
        return types.danger_virtual_memory | types.danger_paging_stall | types.danger_system_interrupt;
    }
    return 0;
}

fn validRatio(value: f64) bool {
    return std.math.isFinite(value) and value >= 0 and value <= 1;
}

fn dangerForTier(tier: u8) u8 {
    return switch (tier) {
        @intFromEnum(types.Tier.vram) => types.danger_vram,
        @intFromEnum(types.Tier.physical_memory) => types.danger_memory,
        @intFromEnum(types.Tier.virtual_memory) => types.danger_virtual_memory,
        else => 0,
    };
}

fn failure(
    capacity: types.CapacityInput,
    code: ErrorCode,
    danger_flags: u8,
    disposition: Disposition,
) types.FeedbackOutput {
    return policyFailure(
        capacity,
        code,
        @intFromEnum(types.ResultCode.ledger_drift_danger),
        danger_flags,
        disposition,
    );
}

fn policyFailure(
    capacity: types.CapacityInput,
    code: ErrorCode,
    result_code: u8,
    danger_flags: u8,
    disposition: Disposition,
) types.FeedbackOutput {
    return .{
        .projected_capacity = capacity,
        .applied = 0,
        .result_code = result_code,
        .danger_flags = danger_flags,
        .current_tier = 0,
        .error_code = @intFromEnum(code),
        .reservation_disposition = @intFromEnum(disposition),
        .reserved0 = 0,
        .reserved1 = 0,
    };
}

fn allZero(values: []const u8) bool {
    for (values) |value| if (value != 0) return false;
    return true;
}
