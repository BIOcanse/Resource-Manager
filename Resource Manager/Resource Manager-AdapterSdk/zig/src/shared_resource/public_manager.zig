const std = @import("std");
const state = @import("state.zig");
const resource_ops = @import("resource_ops.zig");
const activity = @import("activity.zig");
const sort = @import("scheduler_sort.zig");
const types = state.types;

pub const UnloadReason = enum(u8) {
    zero_subscribers = 1,
    capacity_shortage = 2,
};

pub const PlanRequest = extern struct {
    abi_version: u32,
    struct_size: u32,
    sample_generation: u64,
    memory_shortage: u8,
    video_memory_shortage: u8,
    reserved0: [6]u8,
    subscriber_weight: u32,
    activity_weight: u32,
    weight_scale: u32,
    subscriber_half_saturation: u32,
    activity_half_saturation: u32,
    reserved1: u32,
};

pub const Candidate = extern struct {
    resource: types.ResourceRef,
    scheduling_revision: u64,
    gate_epoch: u64,
    size_bytes: u64,
    retention_score_q16: u32,
    subscriber_count: u32,
    activity_score: u8,
    reason: u8,
    flags: u8,
    reserved0: u8,
};

pub const PlanSummary = extern struct {
    abi_version: u32,
    struct_size: u32,
    topology_generation: u64,
    sample_generation: u64,
    resource_count: u32,
    protected_resource_count: u32,
    candidate_count: u32,
    zero_subscriber_candidate_count: u32,
    capacity_candidate_count: u32,
    reserved0: u32,
};

pub fn plan(
    context: *state.Context,
    request: *const PlanRequest,
    output: [*]Candidate,
    output_capacity: u32,
    summary: *PlanSummary,
) !void {
    if (!validRequest(request)) return error.InvalidArgument;

    var guard = try context.acquire();
    defer guard.deinit();
    if (guard.abandoned or context.consistencyState() != .stable) {
        return error.InconsistentState;
    }
    if (request.sample_generation < context.header.activity_generation) {
        return error.Conflict;
    }

    const activity_delta = request.sample_generation - context.header.activity_generation;
    var resource_count: u32 = 0;
    var protected_count: u32 = 0;
    var candidate_count: u32 = 0;
    var zero_subscriber_count: u32 = 0;
    var capacity_count: u32 = 0;
    var activity_changes: u32 = 0;

    var slot: u32 = 0;
    while (slot < context.header.resource_capacity) : (slot += 1) {
        if (context.resourceControl(slot) != .active) continue;
        resource_count += 1;

        const subscribers =
            context.column(u32, context.layout.resources.active_subscriber_count)[slot];
        const protection = context.hardProtectionCount(slot);
        const current_activity =
            context.column(u8, context.layout.resources.activity_score)[slot];
        const settled_activity = activity.afterGenerations(current_activity, activity_delta);
        if (settled_activity != current_activity) {
            _ = types.nextGeneration(
                context.column(u64, context.layout.resources.scheduling_revision)[slot],
            ) orelse return error.CapacityExhausted;
            activity_changes += 1;
        }

        if (subscribers == 0 and protection != 0) {
            @atomicStore(
                u8,
                &context.header.consistency_state,
                @intFromEnum(types.ConsistencyState.quarantined),
                .release,
            );
            return error.InconsistentState;
        }
        if (protection != 0 or context.destructiveActive(slot)) {
            protected_count += 1;
            continue;
        }

        const available = context.column(u8, context.layout.resources.availability)[slot] ==
            @intFromEnum(types.Availability.available);
        if (!available) continue;

        if (subscribers == 0) {
            candidate_count += 1;
            zero_subscriber_count += 1;
        } else if (hasApplicableShortage(context, slot, request)) {
            candidate_count += 1;
            capacity_count += 1;
        }
    }

    if (candidate_count > output_capacity) return error.BufferTooSmall;
    if (activity_changes != 0) _ = try context.nextTopologyGeneration();

    if (activity_delta != 0) {
        var mutation = try context.beginMutation();
        defer mutation.finish();
        if (activity_changes != 0) {
            var changed_slot: u32 = 0;
            while (changed_slot < context.header.resource_capacity) : (changed_slot += 1) {
                if (context.resourceControl(changed_slot) != .active) continue;
                const values = context.column(u8, context.layout.resources.activity_score);
                const settled = activity.afterGenerations(values[changed_slot], activity_delta);
                if (settled == values[changed_slot]) continue;
                values[changed_slot] = settled;
                try resource_ops.bumpSchedulingRevision(context, changed_slot);
            }
            try context.incrementTopology();
        }
        context.header.activity_generation = request.sample_generation;
    }

    var written: u32 = 0;
    slot = 0;
    while (slot < context.header.resource_capacity) : (slot += 1) {
        if (context.resourceControl(slot) != .active or
            context.hardProtectionCount(slot) != 0 or
            context.destructiveActive(slot) or
            context.column(u8, context.layout.resources.availability)[slot] !=
                @intFromEnum(types.Availability.available))
        {
            continue;
        }

        const subscribers =
            context.column(u32, context.layout.resources.active_subscriber_count)[slot];
        const reason: UnloadReason = if (subscribers == 0)
            .zero_subscribers
        else if (hasApplicableShortage(context, slot, request))
            .capacity_shortage
        else
            continue;
        const activity_score =
            context.column(u8, context.layout.resources.activity_score)[slot];
        output[written] = .{
            .resource = resource_ops.resourceRef(context, slot),
            .scheduling_revision = context.column(u64, context.layout.resources.scheduling_revision)[slot],
            .gate_epoch = context.column(u64, context.layout.resources.gate_epoch)[slot],
            .size_bytes = context.column(u64, context.layout.resources.size_bytes)[slot],
            .retention_score_q16 = retentionScore(request, subscribers, activity_score),
            .subscriber_count = subscribers,
            .activity_score = activity_score,
            .reason = @intFromEnum(reason),
            .flags = context.column(u8, context.layout.resources.flags)[slot],
            .reserved0 = 0,
        };
        written += 1;
    }
    std.debug.assert(written == candidate_count);
    sort.heapSortBy(Candidate, output[0..written], comesAfter);

    summary.* = .{
        .abi_version = types.abi_version,
        .struct_size = @sizeOf(PlanSummary),
        .topology_generation = context.header.topology_generation,
        .sample_generation = context.header.activity_generation,
        .resource_count = resource_count,
        .protected_resource_count = protected_count,
        .candidate_count = candidate_count,
        .zero_subscriber_candidate_count = zero_subscriber_count,
        .capacity_candidate_count = capacity_count,
        .reserved0 = 0,
    };
}

fn validRequest(request: *const PlanRequest) bool {
    return request.abi_version == types.abi_version and
        request.struct_size == @sizeOf(PlanRequest) and
        request.sample_generation != 0 and
        request.memory_shortage <= 1 and
        request.video_memory_shortage <= 1 and
        std.mem.allEqual(u8, request.reserved0[0..], 0) and
        request.subscriber_weight != 0 and
        request.activity_weight != 0 and
        request.weight_scale != 0 and
        @as(u64, request.subscriber_weight) + request.activity_weight == request.weight_scale and
        request.subscriber_half_saturation != 0 and
        request.activity_half_saturation != 0 and
        request.reserved1 == 0;
}

fn hasApplicableShortage(
    context: *const state.Context,
    slot: u32,
    request: *const PlanRequest,
) bool {
    const gpu_backed = context.column(u8, context.layout.resources.flags)[slot] &
        types.gpu_backed_flag != 0;
    return if (gpu_backed)
        request.video_memory_shortage != 0
    else
        request.memory_shortage != 0;
}

fn retentionScore(request: *const PlanRequest, subscribers: u32, score: u8) u32 {
    const subscriber_q16 = saturationQ16(subscribers, request.subscriber_half_saturation);
    const activity_q16 = saturationQ16(score, request.activity_half_saturation);
    const weighted = @as(u128, subscriber_q16) * request.subscriber_weight +
        @as(u128, activity_q16) * request.activity_weight;
    return @intCast(weighted / request.weight_scale);
}

fn saturationQ16(value: anytype, half_saturation: u32) u32 {
    const wide_value: u128 = @intCast(value);
    const denominator = wide_value + half_saturation;
    return @intCast((wide_value * std.math.maxInt(u16)) / denominator);
}

fn comesAfter(left: Candidate, right: Candidate) bool {
    if (left.retention_score_q16 != right.retention_score_q16) {
        return left.retention_score_q16 > right.retention_score_q16;
    }
    if (left.resource.public_resource_id != right.resource.public_resource_id) {
        return left.resource.public_resource_id > right.resource.public_resource_id;
    }
    if (left.resource.resource_generation != right.resource.resource_generation) {
        return left.resource.resource_generation > right.resource.resource_generation;
    }
    return left.resource.resource_slot > right.resource.resource_slot;
}

test "two-field retention score is bounded and monotonic" {
    const request = PlanRequest{
        .abi_version = types.abi_version,
        .struct_size = @sizeOf(PlanRequest),
        .sample_generation = 1,
        .memory_shortage = 0,
        .video_memory_shortage = 0,
        .reserved0 = [_]u8{0} ** 6,
        .subscriber_weight = 400,
        .activity_weight = 600,
        .weight_scale = 1000,
        .subscriber_half_saturation = 4,
        .activity_half_saturation = 8,
        .reserved1 = 0,
    };
    const empty = retentionScore(&request, 0, 0);
    const subscribed = retentionScore(&request, 1, 0);
    const active = retentionScore(&request, 1, 32);
    try std.testing.expectEqual(@as(u32, 0), empty);
    try std.testing.expect(subscribed > empty);
    try std.testing.expect(active > subscribed);
    try std.testing.expect(active <= std.math.maxInt(u16));
}
