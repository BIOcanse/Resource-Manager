const std = @import("std");
const protocol = @import("protocol.zig");
const state = @import("state.zig");
const ResultCode = @import("../../common/result_codes.zig").ResultCode;

const MetricSelectionSummary = struct {
    selected_source_count: u32,
    unavailable_metric_count: u32,
};

const InventorySelectionSummary = struct {
    source_index: ?u32,
    selected_source_count: u32,
    flags: u32,
};

pub fn plan(
    session: *state.Session,
    input: *const protocol.PlanInput,
    requested_metrics: []const protocol.PlanMetricInput,
    source_modes: []const protocol.SourceModeInput,
    output: *protocol.PlanOutput,
    source_plans: []protocol.SourcePlanOutput,
    metric_plans: []protocol.MetricPlanOutput,
) ResultCode {
    state.lock(&session.mutex);
    defer state.unlock(&session.mutex);

    if (!validInput(session, input, requested_metrics, source_modes)) return .abi_mismatch;
    if (source_plans.len != input.source_plan_capacity or
        metric_plans.len != input.metric_plan_capacity or
        source_plans.len < session.active.source_count or
        metric_plans.len < requested_metrics.len)
    {
        return .buffer_too_small;
    }
    if (session.phase == .empty or session.phase == .completion_open) {
        return .unavailable;
    }
    if (!session.operationAdvances(input.operation_epoch, input.command_at_milliseconds) or
        input.plan_epoch <= session.last_plan_epoch)
    {
        return .stale_frame;
    }

    const source_validation = validateSourceModes(session, source_modes);
    if (source_validation != .ok) return source_validation;
    const metric_validation = validateRequestedMetrics(session, requested_metrics);
    if (metric_validation != .ok) return metric_validation;
    const next_revision = session.nextRevision() orelse return .unavailable;

    stageSourceModes(session, input.plan_epoch, source_modes);
    const metric_summary = selectMetricRules(session, input.plan_epoch, requested_metrics);
    const inventory_summary = selectInventorySource(session, input);

    @memset(source_plans, std.mem.zeroes(protocol.SourcePlanOutput));
    @memset(metric_plans, std.mem.zeroes(protocol.MetricPlanOutput));
    const source_output_count = writeSourcePlans(
        session,
        input.plan_epoch,
        inventory_summary.source_index,
        source_plans,
    );
    writeMetricPlans(session, input.plan_epoch, requested_metrics, metric_plans);

    session.last_operation_epoch = input.operation_epoch;
    session.last_plan_epoch = input.plan_epoch;
    session.last_command_at_milliseconds = input.command_at_milliseconds;
    session.state_revision = next_revision;
    output.* = .{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.PlanOutput),
        .configuration_generation = session.config.generation,
        .catalog_generation = session.catalog_generation,
        .operation_epoch = input.operation_epoch,
        .plan_epoch = input.plan_epoch,
        .state_revision = next_revision,
        .source_plan_count = metric_summary.selected_source_count +
            inventory_summary.selected_source_count,
        .metric_plan_count = @intCast(requested_metrics.len),
        .unavailable_metric_count = metric_summary.unavailable_metric_count,
        .flags = inventory_summary.flags,
        .semantic_fingerprint = planFingerprint(
            source_plans[0..source_output_count],
            metric_plans[0..requested_metrics.len],
        ),
        .reserved = .{ 0, 0 },
    };
    return .ok;
}

fn validateSourceModes(
    session: *const state.Session,
    source_modes: []const protocol.SourceModeInput,
) ResultCode {
    var previous_source_handle: u64 = 0;
    for (source_modes) |mode| {
        if (!validSourceMode(&mode) or mode.source_handle <= previous_source_handle) {
            return .invalid_argument;
        }
        previous_source_handle = mode.source_handle;
        const source_index = session.active.findSource(mode.source_handle) orelse
            return .invalid_argument;
        const source = session.active.sources[source_index];
        const previous_mode = session.source_modes[source_index];
        const highest_accepted_capability_generation = @max(
            source.capability_generation,
            if (previous_mode.present) previous_mode.input.capability_generation else 0,
        );
        if (highest_accepted_capability_generation != 0 and
            mode.capability_generation < highest_accepted_capability_generation)
        {
            return .stale_frame;
        }
        if (source.status == .unsupported and
            mode.capability_generation == source.capability_generation and
            mode.availability == @intFromEnum(protocol.SourceAvailability.available))
        {
            return .stale_frame;
        }
    }
    return .ok;
}

fn validateRequestedMetrics(
    session: *const state.Session,
    requested_metrics: []const protocol.PlanMetricInput,
) ResultCode {
    var previous_metric_handle: u64 = 0;
    for (requested_metrics) |requested| {
        if (requested.struct_size != @sizeOf(protocol.PlanMetricInput) or
            requested.flags != 0 or requested.metric_handle <= previous_metric_handle or
            !allZero(requested.reserved))
        {
            return .invalid_argument;
        }
        previous_metric_handle = requested.metric_handle;
        _ = session.active.findMetric(requested.metric_handle) orelse return .invalid_argument;
    }
    return .ok;
}

fn stageSourceModes(
    session: *state.Session,
    plan_epoch: u64,
    source_modes: []const protocol.SourceModeInput,
) void {
    @memset(session.source_modes, .{});
    for (source_modes) |mode| {
        const source_index = session.active.findSource(mode.source_handle).?;
        session.source_modes[source_index] = .{
            .present = true,
            .input = mode,
            .plan_epoch = plan_epoch,
        };
    }
}

fn selectMetricRules(
    session: *state.Session,
    plan_epoch: u64,
    requested_metrics: []const protocol.PlanMetricInput,
) MetricSelectionSummary {
    session.planned_rule_count = @intCast(requested_metrics.len);
    @memset(session.planned_rule_handles, 0);
    @memset(session.planned_rule_flags, false);
    var summary = MetricSelectionSummary{
        .selected_source_count = 0,
        .unavailable_metric_count = 0,
    };
    for (requested_metrics, 0..) |requested, ordinal| {
        const metric_index = session.active.findMetric(requested.metric_handle).?;
        const metric = session.active.metrics[metric_index];
        const selected_rule_handle = selectMetricRule(session, plan_epoch, &metric);
        session.planned_rule_handles[ordinal] = selected_rule_handle;
        if (selected_rule_handle == 0) {
            summary.unavailable_metric_count += 1;
        } else {
            const rule_index = session.active.findRule(selected_rule_handle).?;
            const source_index =
                session.active.findSource(session.active.rules[rule_index].definition.source_handle).?;
            if (session.source_modes[source_index].planned_rule_count == 1) {
                summary.selected_source_count += 1;
            }
        }
    }
    return summary;
}

fn selectMetricRule(
    session: *state.Session,
    plan_epoch: u64,
    metric: *const @import("catalog.zig").MetricRecord,
) u64 {
    var offset: u32 = 0;
    while (offset < metric.rule_count) : (offset += 1) {
        const rule_index = metric.first_rule_index + offset;
        const rule = session.active.rules[rule_index];
        const source_index = session.active.findSource(rule.definition.source_handle).?;
        const mode = session.source_modes[source_index];
        if (!mode.present or
            mode.input.zone_mode == @intFromEnum(protocol.ZoneMode.freeze) or
            mode.input.availability != @intFromEnum(protocol.SourceAvailability.available) or
            (rule.unsupported_until_capability_generation != 0 and
                mode.input.capability_generation <=
                    rule.unsupported_until_capability_generation))
        {
            continue;
        }
        session.planned_rule_flags[rule_index] = true;
        session.source_modes[source_index].planned_rule_count += 1;
        session.source_modes[source_index].selected = true;
        session.source_modes[source_index].plan_token_fingerprint = extendPlanToken(
            session.source_modes[source_index].plan_token_fingerprint,
            plan_epoch,
            &session.source_modes[source_index].input,
            rule.definition.rule_handle,
            false,
        );
        return rule.definition.rule_handle;
    }
    return 0;
}

fn selectInventorySource(
    session: *state.Session,
    input: *const protocol.PlanInput,
) InventorySelectionSummary {
    var summary = InventorySelectionSummary{
        .source_index = null,
        .selected_source_count = 0,
        .flags = 0,
    };
    if ((input.flags & protocol.PlanFlags.include_gpu_inventory) == 0) return summary;

    for (session.active.sources[0..session.active.source_count], 0..) |source, ordinal| {
        if (source.definition.source_role !=
            @intFromEnum(protocol.SourceRole.gpu_inventory))
        {
            continue;
        }
        const mode = session.source_modes[ordinal];
        if (mode.present and
            mode.input.zone_mode != @intFromEnum(protocol.ZoneMode.freeze) and
            mode.input.availability == @intFromEnum(protocol.SourceAvailability.available))
        {
            summary.source_index = @intCast(ordinal);
            summary.selected_source_count = 1;
            summary.flags = protocol.PlanOutputFlags.gpu_inventory_selected;
            session.source_modes[ordinal].selected = true;
            session.source_modes[ordinal].inventory_selected = true;
            session.source_modes[ordinal].plan_token_fingerprint = extendPlanToken(
                0,
                input.plan_epoch,
                &session.source_modes[ordinal].input,
                source.definition.source_handle,
                true,
            );
        } else {
            summary.flags = protocol.PlanOutputFlags.gpu_inventory_unavailable;
        }
        return summary;
    }
    summary.flags = protocol.PlanOutputFlags.gpu_inventory_unavailable;
    return summary;
}

fn writeSourcePlans(
    session: *const state.Session,
    plan_epoch: u64,
    inventory_source_index: ?u32,
    source_plans: []protocol.SourcePlanOutput,
) usize {
    var output_count: usize = 0;
    for (session.active.sources[0..session.active.source_count], 0..) |source, source_index| {
        const mode = session.source_modes[source_index];
        if (mode.planned_rule_count == 0 and
            inventory_source_index != @as(?u32, @intCast(source_index)))
        {
            continue;
        }
        source_plans[output_count] = .{
            .struct_size = @sizeOf(protocol.SourcePlanOutput),
            .flags = protocol.SourcePlanFlags.selected |
                if (mode.input.zone_mode == @intFromEnum(protocol.ZoneMode.low_power))
                    protocol.SourcePlanFlags.low_power
                else
                    0,
            .source_handle = source.definition.source_handle,
            .capability_generation = mode.input.capability_generation,
            .plan_epoch = plan_epoch,
            .plan_token_fingerprint = mode.plan_token_fingerprint,
            .requested_rule_count = mode.planned_rule_count,
            .zone_mode = mode.input.zone_mode,
            .availability = mode.input.availability,
            .source_role = source.definition.source_role,
            .reserved = .{ 0, 0 },
        };
        output_count += 1;
    }
    return output_count;
}

fn writeMetricPlans(
    session: *const state.Session,
    plan_epoch: u64,
    requested_metrics: []const protocol.PlanMetricInput,
    metric_plans: []protocol.MetricPlanOutput,
) void {
    for (requested_metrics, 0..) |requested, ordinal| {
        const metric_index = session.active.findMetric(requested.metric_handle).?;
        const metric = session.active.metrics[metric_index];
        const definition = session.active.rules[metric.first_rule_index].definition;
        const selected_rule_handle = session.planned_rule_handles[ordinal];
        var selected_source_handle: u64 = 0;
        var selected_rule = definition;
        if (selected_rule_handle != 0) {
            const rule_index = session.active.findRule(selected_rule_handle).?;
            selected_rule = session.active.rules[rule_index].definition;
            selected_source_handle = selected_rule.source_handle;
        }
        metric_plans[ordinal] = .{
            .struct_size = @sizeOf(protocol.MetricPlanOutput),
            .flags = if (selected_rule_handle != 0)
                protocol.MetricPlanFlags.selected
            else
                protocol.MetricPlanFlags.unavailable,
            .rule_handle = selected_rule_handle,
            .metric_handle = requested.metric_handle,
            .source_handle = selected_source_handle,
            .scope_handle = selected_rule.scope_handle,
            .plan_epoch = plan_epoch,
            .metric_kind = selected_rule.metric_kind,
            .value_kind = selected_rule.value_kind,
            .reserved = .{ 0, 0 },
        };
    }
}

fn validInput(
    session: *const state.Session,
    input: *const protocol.PlanInput,
    requested_metrics: []const protocol.PlanMetricInput,
    source_modes: []const protocol.SourceModeInput,
) bool {
    if (input.abi_version != protocol.abi_version or
        input.struct_size != @sizeOf(protocol.PlanInput) or
        input.configuration_generation != session.config.generation or
        input.catalog_generation != session.catalog_generation or
        input.operation_epoch == 0 or input.plan_epoch == 0 or
        input.requested_metric_count != requested_metrics.len or
        input.source_mode_count != source_modes.len or
        input.source_plan_capacity > session.config.maximum_source_plan_count or
        input.metric_plan_capacity > session.config.maximum_metric_plan_count or
        (input.flags & ~protocol.PlanFlags.known) != 0 or !allZero(input.reserved))
    {
        return false;
    }
    if (requested_metrics.len == 0 or
        requested_metrics.len > session.config.maximum_plan_metric_count or
        source_modes.len != session.active.source_count or
        source_modes.len > session.config.maximum_source_mode_count)
    {
        return false;
    }
    return true;
}

fn validSourceMode(input: *const protocol.SourceModeInput) bool {
    return input.struct_size == @sizeOf(protocol.SourceModeInput) and
        input.flags == 0 and input.source_handle != 0 and
        input.capability_generation != 0 and
        protocol.knownEnum(protocol.ZoneMode, input.zone_mode) and
        protocol.knownEnum(protocol.SourceAvailability, input.availability) and
        allZero(input.reserved);
}

fn planFingerprint(
    source_plans: []const protocol.SourcePlanOutput,
    metric_plans: []const protocol.MetricPlanOutput,
) u64 {
    var hash = std.hash.Wyhash.init(0x726d_6d65_7472_706c);
    hash.update(std.mem.sliceAsBytes(source_plans));
    hash.update(std.mem.sliceAsBytes(metric_plans));
    const result = hash.final();
    return if (result == 0) 1 else result;
}

fn extendPlanToken(
    previous: u64,
    plan_epoch: u64,
    mode: *const protocol.SourceModeInput,
    row_identity: u64,
    inventory_selected: bool,
) u64 {
    const fields = [_]u64{
        previous,
        plan_epoch,
        mode.source_handle,
        mode.capability_generation,
        mode.zone_mode,
        mode.availability,
        row_identity,
        @intFromBool(inventory_selected),
    };
    const value = std.hash.Wyhash.hash(
        0x726d_6d65_7472_7074,
        std.mem.asBytes(&fields),
    );
    return if (value == 0) 1 else value;
}

fn allZero(values: anytype) bool {
    for (values) |value| if (value != 0) return false;
    return true;
}
