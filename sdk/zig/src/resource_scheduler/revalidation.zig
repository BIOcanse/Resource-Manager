const std = @import("std");
const config_module = @import("config.zig");
const filtering = @import("filtering.zig");
const state = @import("state.zig");
const types = @import("types.zig");

pub fn revalidateAndBegin(
    config: *const config_module.Config,
    view: state.View,
    token: *const types.ReservationToken,
    selection: *const types.SelectionOutput,
    input: *const types.RevalidationInput,
    output: *types.RevalidationOutput,
) types.PlanError!void {
    output.* = blocked(.snapshot_unavailable);
    if (!config_module.validate(config) or !validInput(input)) {
        return error.InvalidRequest;
    }
    _ = try state.validateReservedSelection(view, token, selection);

    if (input.flags & types.revalidation_flag_snapshot_available == 0) return;
    if (input.authority.source_snapshot_generation <
        selection.authority.source_snapshot_generation)
    {
        output.* = blocked(.snapshot_older);
        return;
    }
    if (input.target_key != selection.target_key or !sameIdentity(input, selection)) {
        output.* = blocked(.identity_changed);
        return;
    }
    const host_self_trim = selection.authority.action == types.action_trim and
        selection.authority.action_route ==
            @intFromEnum(types.ActionRoute.manager_direct) and
        selection.authority.flags ==
            types.authority_flags_host_self_executor;
    if ((input.size_bytes != selection.size_bytes and !host_self_trim) or
        input.tier != selection.tier)
    {
        output.* = blocked(.resource_changed);
        return;
    }

    const resource = toPrivateResource(input);
    const target = targetForAuthority(input);
    if (!filtering.validResource(
        &resource,
        &target,
        input.authority.projection_epoch,
    )) {
        output.* = blocked(.resource_changed);
        return;
    }
    const decision = filtering.staticallyAllowedActions(
        config,
        &resource,
        input.policy_grade,
        selection.authority.action,
    );
    if (decision.reason != .allowed or
        decision.allowed_actions & selection.authority.action == 0)
    {
        output.* = blocked(switch (decision.reason) {
            .required_now => .required_now,
            else => .action_blocked,
        });
        return;
    }

    try state.begin(view, token, 0);
    output.* = .{
        .allowed = 1,
        .reason = @intFromEnum(types.RevalidationReason.allowed),
        .reserved0 = .{ 0, 0, 0, 0, 0, 0 },
    };
}

fn validInput(input: *const types.RevalidationInput) bool {
    if (input.abi_version != types.protocol_version or
        input.struct_size != @sizeOf(types.RevalidationInput) or
        input.target_key == 0 or
        !types.validExecutionAuthority(input.authority) or
        config_module.policyIndex(input.policy_grade) == null or
        input.flags & ~types.all_revalidation_flags != 0 or
        input.reserved0 != 0 or
        !allZero(input.reserved1[0..]))
    {
        return false;
    }

    if (input.flags & types.revalidation_flag_snapshot_available == 0) {
        return input.size_bytes == 0 and
            input.tier == 0 and
            input.resource_kind == 0 and input.recovery_kind == 0 and input.granularity == 0 and
            input.inapplicable_actions == 0 and input.demand_mask == 0 and
            input.activity_score == 0 and input.reserved0 == 0 and allZero(input.reserved1[0..]);
    }

    return input.flags == types.revalidation_flag_snapshot_available;
}

fn sameIdentity(
    input: *const types.RevalidationInput,
    selection: *const types.SelectionOutput,
) bool {
    var current = input.authority;
    current.source_snapshot_generation =
        selection.authority.source_snapshot_generation;
    return types.sameExecutionAuthority(current, selection.authority);
}

fn allZero(values: []const u8) bool {
    for (values) |value| if (value != 0) return false;
    return true;
}

fn toPrivateResource(input: *const types.RevalidationInput) types.PrivateResourceInput {
    var resource = std.mem.zeroes(types.PrivateResourceInput);
    resource.abi_version = types.protocol_version;
    resource.struct_size = @sizeOf(types.PrivateResourceInput);
    resource.target_key = input.target_key;
    resource.size_bytes = input.size_bytes;
    resource.tier = input.tier;
    resource.resource_kind = input.resource_kind;
    resource.recovery_kind = input.recovery_kind;
    resource.granularity = input.granularity;
    resource.inapplicable_actions = input.inapplicable_actions |
        (types.destructive_action_bits & ~input.authority.action);
    resource.action_route = input.authority.action_route;
    resource.demand_mask = input.demand_mask;
    resource.activity_score = input.activity_score;
    switch (input.authority.action) {
        types.action_discard => resource.discard_authority = input.authority,
        types.action_trim => resource.trim_authority = input.authority,
        types.action_move_down => resource.move_down_authority = input.authority,
        else => unreachable,
    }
    return resource;
}

fn targetForAuthority(input: *const types.RevalidationInput) types.TargetInput {
    return .{
        .abi_version = types.protocol_version,
        .struct_size = @sizeOf(types.TargetInput),
        .target_key = input.target_key,
        .owner_application_key = input.authority.owner_application_key,
        .owner_instance_id_low = input.authority.owner_instance_id_low,
        .owner_instance_id_high = input.authority.owner_instance_id_high,
        .owner_context_generation = input.authority.owner_context_generation,
        .lease_generation = input.authority.lease_generation,
        .capability_generation = input.authority.capability_generation,
        .base_score = 0,
        .capacity = std.mem.zeroes(types.CapacityInput),
        .private_resource_start = 0,
        .private_resource_count = 1,
        .policy_grade = input.policy_grade,
        .cpu_grade = types.no_scheduling_grade,
        .gpu_grade = types.no_scheduling_grade,
        .surface_state = 0,
        .target_flags = 0,
        .reserved0 = .{ 0, 0, 0 },
        .reserved1 = 0,
    };
}

fn blocked(reason: types.RevalidationReason) types.RevalidationOutput {
    return .{
        .allowed = 0,
        .reason = @intFromEnum(reason),
        .reserved0 = .{ 0, 0, 0, 0, 0, 0 },
    };
}

fn testAuthority(
    source: types.Source,
    action: u8,
    target_key: u64,
    resource_key: u64,
    source_generation: u64,
) types.ResourceExecutionAuthority {
    return .{
        .source = @intFromEnum(source),
        .action = action,
        .action_route = if (source == .adapted_private)
            @intFromEnum(types.ActionRoute.adapter_handler)
        else
            @intFromEnum(types.ActionRoute.manager_direct),
        .flags = types.authority_flag_typed_executor_proof,
        .resource_slot = @intCast(resource_key),
        .resource_id = @intCast(resource_key),
        .reserved0 = 0,
        .ledger_instance_id = target_key + 1,
        .source_snapshot_generation = source_generation,
        .resource_generation = 1,
        .resource_key = resource_key,
        .owner_application_key = target_key + 2,
        .owner_instance_id_low = target_key + 3,
        .owner_instance_id_high = target_key + 4,
        .owner_context_generation = 1,
        .lease_generation = 1,
        .binding_generation = 1,
        .capability_generation = 1,
        .scheduling_revision = 1,
        .executor_id_low = 1,
        .executor_id_high = 2,
        .action_attempt_id_low = resource_key + action,
        .action_attempt_id_high = 1,
        .projection_epoch = 1,
    };
}

test "private revalidation accepts newer snapshots and Host self trim size drift" {
    const testing = std.testing;
    var config = std.mem.zeroes(config_module.Config);
    config.abi_version = types.protocol_version;
    config.struct_size = @sizeOf(config_module.Config);
    config.generation = 7;
    config.bytes_per_megabyte = 1024 * 1024;
    config.size_importance_min = 1;
    config.size_importance_max = 2;
    config.size_log_divisor = 2;
    @memset(config.resource_kind_multiplier[0..], 1);
    @memset(config.surface_multiplier[0..], 1);
    @memset(config.policy_grade_multiplier[0..], 1);
    @memset(config.policy_grade_pressure[0..], 1);
    @memset(config.scheduling_grade_multiplier[0..], 1);
    config.absent_scheduling_grade_multiplier = 1;
    for (&config.action_kind_multiplier) |*row| @memset(row[0..], 1);
    config.activity_base_multiplier = 1;
    config.activity_normalizer = 255;
    @memset(config.demand_multiplier[0..], 1);
    config.large_resource_bytes = 1;
    config.high_activity_score = 200;
    config.physical_to_virtual_desperate_pressure_level = 3;
    config.physical_to_virtual_min_free_ratio = 0.1;
    config.physical_to_virtual_desperate_min_free_ratio = 0.05;
    config.vram_to_physical_min_free_ratio = 0.1;
    config.pressure_free_ratio_threshold = .{ 0.1, 0.2, 0.4 };
    config.target_free_ratio = .{ 0.5, 0.5, 0.5 };
    config.desired_free_ratio_level_2_to_4 = .{ 0.5, 0.5, 0.5 };
    config.minimum_release_bytes = .{ 1, 1, 1, 1 };
    config.max_release_share = .{ 1, 1, 1, 1 };
    config.base_score_threshold = .{ 80, 50, 20 };
    config.base_release_multiplier = .{ 1, 1, 1, 1 };
    config.base_score_min = 0;
    config.base_score_max = 100;
    config.trim_release_numerator = 1;
    config.trim_release_denominator = 2;
    config.max_actions_per_target = 1;
    config.strong_pressure_free_ratio = 0.1;
    config.danger_min_physical_after_vram_move_ratio = 0.05;
    config.danger_min_virtual_after_physical_move_ratio = 0.05;
    @memset(config.danger_severity_weight[0..], 1);
    try testing.expect(config_module.validate(&config));

    const capacity: u32 = 1;
    const byte_count = try state.requiredBytes(capacity);
    const alignment = @alignOf(state.Header);
    const storage = try testing.allocator.alignedAlloc(u8, .fromByteUnits(alignment), byte_count);
    defer testing.allocator.free(storage);
    try state.initialize(storage, capacity, 9);
    const view = try state.open(storage);

    var selection = std.mem.zeroes(types.SelectionOutput);
    selection.target_key = 10;
    selection.request_id = 101;
    selection.authority = testAuthority(
        .adapted_private,
        types.action_discard,
        selection.target_key,
        14,
        2,
    );
    selection.size_bytes = 4096;
    selection.configuration_generation = config.generation;
    selection.tier = @intFromEnum(types.Tier.physical_memory);
    var request = std.mem.zeroes(types.ReservationRequest);
    request.abi_version = types.protocol_version;
    request.struct_size = @sizeOf(types.ReservationRequest);
    request.now_monotonic_timestamp = 1;
    request.deadline_timestamp = 100;
    request.pending_generation = 3;
    request.configuration_generation = config.generation;
    request.danger_min_physical_after_vram_move_ratio = 0.05;
    request.danger_min_virtual_after_physical_move_ratio = 0.05;
    request.maximum_in_flight = 1;
    request.maximum_in_flight_per_target = 1;
    var token = std.mem.zeroes(types.ReservationToken);
    try state.reserve(view, &request, &selection, &token);
    try state.bindJournal(view, &token, 501, 502);

    var input = std.mem.zeroes(types.RevalidationInput);
    input.abi_version = types.protocol_version;
    input.struct_size = @sizeOf(types.RevalidationInput);
    input.authority = selection.authority;
    input.target_key = selection.target_key;
    input.size_bytes = selection.size_bytes;
    input.policy_grade = -4;
    input.tier = selection.tier;
    input.flags = types.revalidation_flag_snapshot_available;
    input.resource_kind = @intFromEnum(types.ResourceKind.cache);
    input.recovery_kind = @intFromEnum(types.RecoveryKind.built_data);
    input.granularity = @intFromEnum(types.Granularity.fully_loaded);
    input.demand_mask = types.demand_required_now;
    var output = std.mem.zeroes(types.RevalidationOutput);
    try revalidateAndBegin(&config, view, &token, &selection, &input, &output);
    try testing.expectEqual(@as(u8, 0), output.allowed);
    try testing.expectEqual(@intFromEnum(types.RevalidationReason.required_now), output.reason);

    input.demand_mask = 0;
    try revalidateAndBegin(&config, view, &token, &selection, &input, &output);
    try testing.expectEqual(@as(u8, 1), output.allowed);
    _ = try state.validateActiveSelection(view, &token, &selection);
    try state.complete(view, &token);

    var self_selection = std.mem.zeroes(types.SelectionOutput);
    self_selection.target_key = 30;
    self_selection.request_id = 103;
    self_selection.authority = testAuthority(
        .adapted_private,
        types.action_trim,
        self_selection.target_key,
        34,
        20,
    );
    self_selection.authority.action_route =
        @intFromEnum(types.ActionRoute.manager_direct);
    self_selection.authority.flags =
        types.authority_flags_host_self_executor;
    self_selection.size_bytes = 4096;
    self_selection.configuration_generation = config.generation;
    self_selection.tier = @intFromEnum(types.Tier.physical_memory);
    request.pending_generation = 5;
    var self_token = std.mem.zeroes(types.ReservationToken);
    try state.reserve(view, &request, &self_selection, &self_token);
    try state.bindJournal(view, &self_token, 505, 506);

    var self_input = std.mem.zeroes(types.RevalidationInput);
    self_input.abi_version = types.protocol_version;
    self_input.struct_size = @sizeOf(types.RevalidationInput);
    self_input.authority = self_selection.authority;
    self_input.authority.source_snapshot_generation += 1;
    self_input.target_key = self_selection.target_key;
    self_input.size_bytes = self_selection.size_bytes + 1024;
    self_input.policy_grade = -3;
    self_input.tier = self_selection.tier;
    self_input.flags = types.revalidation_flag_snapshot_available;
    self_input.resource_kind =
        @intFromEnum(types.ResourceKind.temporary_compute_memory);
    self_input.recovery_kind =
        @intFromEnum(types.RecoveryKind.live_state);
    self_input.granularity =
        @intFromEnum(types.Granularity.partial_usable);
    self_input.inapplicable_actions =
        types.destructive_action_bits & ~types.action_trim;
    try revalidateAndBegin(
        &config,
        view,
        &self_token,
        &self_selection,
        &self_input,
        &output,
    );
    try testing.expectEqual(@as(u8, 1), output.allowed);
    _ = try state.validateActiveSelection(
        view,
        &self_token,
        &self_selection,
    );
    try state.complete(view, &self_token);
}
