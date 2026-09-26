const std = @import("std");
const types = @import("types.zig");
const config_module = @import("config.zig");
const capacity_module = @import("capacity.zig");
const scoring = @import("scoring.zig");
const filtering = @import("filtering.zig");
const sorting = @import("sort.zig");
const fact_batch_module = @import("fact_batch.zig");

const tier_plan_order = [_]u8{
    @intFromEnum(types.Tier.virtual_memory),
    @intFromEnum(types.Tier.physical_memory),
    @intFromEnum(types.Tier.vram),
};
const destructive_actions = [_]u8{
    types.action_discard,
    types.action_trim,
    types.action_move_down,
};

pub fn requiredCandidateCapacity(private_resource_count: usize) types.PlanError!usize {
    if (private_resource_count > std.math.maxInt(usize) / destructive_actions.len or
        private_resource_count > std.math.maxInt(u32) / destructive_actions.len)
    {
        return error.NumericOverflow;
    }
    return private_resource_count * destructive_actions.len;
}

pub fn plan(
    config: *const config_module.Config,
    request: *const types.PlanRequest,
    targets: []const types.TargetInput,
    resources: []const types.PrivateResourceInput,
    state_pending: []const types.PendingActionInput,
    journal_pending: []const types.PendingActionInput,
    fact_batch: *const fact_batch_module.FactBatchInput,
    buffers: *types.PlannerBuffers,
    global: *types.GlobalSelectionOutput,
) types.PlanError!types.PlanSummary {
    if (!config_module.validate(config)) return error.InvalidConfig;
    if (!filtering.validateRequest(request, config)) return error.InvalidRequest;
    const pending_count = std.math.add(
        usize,
        state_pending.len,
        journal_pending.len,
    ) catch return error.NumericOverflow;
    try fact_batch_module.validate(
        fact_batch,
        config.generation,
        targets.len,
        resources.len,
        journal_pending.len,
    );
    try validateBuffers(targets.len, resources.len, pending_count, buffers);

    zeroBuffers(targets.len, resources.len, pending_count, buffers);
    global.* = zeroGlobalSelection();

    var summary = std.mem.zeroes(types.PlanSummary);
    summary.request_id = request.request_id;
    summary.scoring_config_generation = config.generation;
    summary.target_count = @intCast(targets.len);
    summary.resource_count = @intCast(resources.len);
    summary.candidate_capacity_required = @intCast(try requiredCandidateCapacity(resources.len));

    try assignTargets(config, targets, resources, buffers);
    markResourceValidity(targets, resources, fact_batch.projection_epoch, buffers, &summary);
    const active_pending_count = try preparePending(
        request,
        state_pending,
        journal_pending,
        buffers.pending_scratch,
        &summary,
    );

    var global_reserved_physical: u64 = 0;
    var global_reserved_virtual: u64 = 0;
    accumulatePendingReservations(
        buffers.pending_scratch[0..active_pending_count],
        &global_reserved_physical,
        &global_reserved_virtual,
    );
    for (buffers.target_scratch[0..targets.len]) |*work| {
        work.reserved_physical_bytes = global_reserved_physical;
        work.reserved_virtual_bytes = global_reserved_virtual;
    }

    var candidate_count: usize = 0;
    var target_index: usize = 0;
    while (target_index < targets.len) : (target_index += 1) {
        const target = &targets[target_index];
        const resolved = try capacity_module.resolve(&target.capacity);
        const target_candidate_start = candidate_count;
        const policy_index = config_module.policyIndex(target.policy_grade) orelse return error.InvalidTarget;

        var plan_output = std.mem.zeroes(types.TargetPlanOutput);
        plan_output.target_key = target.target_key;
        plan_output.owner_application_key = target.owner_application_key;
        plan_output.first_selection_index = types.none_index;
        plan_output.active_phase_order = 9;
        plan_output.result_code = @intFromEnum(types.ResultCode.no_changes);
        plan_output.target_flags = target.target_flags;

        var tier_offset: usize = 0;
        while (tier_offset < tier_plan_order.len) : (tier_offset += 1) {
            const tier = tier_plan_order[tier_offset];
            const tier_index: usize = tier;
            const pressure = capacity_module.pressureLevel(
                config,
                resolved.free_ratio[tier_index],
                config.target_free_ratio[tier_index],
            );
            plan_output.pressure_level[tier_index] = pressure;
            const goal = try capacity_module.releaseGoalBytes(
                config,
                tier,
                &resolved,
                target.base_score,
                buffers.target_scratch[target_index].ledger_bytes[tier_index],
            );
            plan_output.release_goal_bytes[tier_index] = goal;
            if (goal == 0 or !tierEnabled(request, target, tier)) continue;

            const segment_start = candidate_count;
            const phase_actions = filtering.phaseActionMask(tier) & request.requested_action_mask;
            const pressure_for_filter = @max(pressure, config.policy_grade_pressure[policy_index]);
            var resource_offset: usize = 0;
            while (resource_offset < target.private_resource_count) : (resource_offset += 1) {
                const resource_index = @as(usize, target.private_resource_start) + resource_offset;
                const scratch = &buffers.resource_scratch[resource_index];
                if (scratch.flags & types.resource_scratch_valid == 0) continue;
                const resource = &resources[resource_index];
                if (resource.tier != tier) continue;

                const decision = filtering.allowedActions(
                    config,
                    target,
                    resource,
                    &resolved,
                    pressure_for_filter,
                    phase_actions,
                );
                if (decision.allowed_actions == 0) {
                    countFilterReason(&summary, decision.reason);
                    continue;
                }

                for (destructive_actions) |action| {
                    if (decision.allowed_actions & action == 0) continue;
                    const importance = scoring.candidateImportance(config, resource, target, action) catch {
                        summary.invalid_numeric_count += 1;
                        continue;
                    };
                    const release_bytes = scoring.estimateReleaseBytes(
                        config,
                        resource.size_bytes,
                        action,
                    ) catch {
                        summary.invalid_numeric_count += 1;
                        continue;
                    };
                    if (!std.math.isFinite(importance) or importance <= 0 or release_bytes == 0) {
                        summary.invalid_numeric_count += 1;
                        continue;
                    }

                    var candidate = std.mem.zeroes(types.CandidateOutput);
                    candidate.target_key = target.target_key;
                    candidate.authority = types.authorityForAction(resource, action) orelse
                        return error.InvalidTarget;
                    candidate.size_bytes = resource.size_bytes;
                    candidate.estimated_release_bytes = release_bytes;
                    candidate.owner_importance = importance;
                    candidate.final_importance = importance;
                    candidate.resource_input_index = @intCast(resource_index);
                    candidate.target_input_index = @intCast(target_index);
                    candidate.resource_id = candidate.authority.resource_id;
                    candidate.tier = resource.tier;
                    candidate.resource_kind = resource.resource_kind;
                    candidate.activity_score = resource.activity_score;
                    candidate.surface_state = target.surface_state;
                    candidate.phase_order = capacity_module.phaseOrder(resource.tier);
                    candidate.candidate_flags = types.candidate_flag_quota_selected;
                    buffers.candidates[candidate_count] = candidate;
                    candidate_count += 1;
                    summary.raw_candidate_count += 1;
                    buffers.target_scratch[target_index].raw_candidate_count += 1;
                }
            }

            const raw_segment_end = candidate_count;
            selectReleaseQuotaFromHeap(
                buffers.candidates[segment_start..raw_segment_end],
                goal,
                buffers.resource_scratch,
            );
            candidate_count = compactQuotaCandidates(
                buffers.candidates,
                segment_start,
                raw_segment_end,
                buffers.resource_scratch,
            );
        }

        plan_output.candidate_count = @intCast(candidate_count - target_candidate_start);
        buffers.target_scratch[target_index].candidate_count = plan_output.candidate_count;
        plan_output.action_limit = if (capacity_module.isConservativeTargetPressure(config, &resolved))
            1
        else
            config.max_actions_per_target;
        buffers.target_scratch[target_index].action_limit = plan_output.action_limit;
        buffers.target_plans[target_index] = plan_output;
    }

    for (buffers.candidates[0..candidate_count]) |candidate| {
        const work = &buffers.target_scratch[candidate.target_input_index];
        if (candidate.phase_order < work.active_phase_order) {
            work.active_phase_order = candidate.phase_order;
        }
    }

    var selection_count: usize = 0;
    if (request.flags & types.request_flag_emit_candidates != 0) {
        sorting.canonicalCandidates(buffers.candidates[0..candidate_count]);
        for (buffers.candidates[0..candidate_count], 0..) |*candidate, rank| {
            candidate.rank = @intCast(rank);
        }
        selection_count = try selectQueueActions(
            config,
            request.request_id,
            targets,
            buffers.pending_scratch[0..active_pending_count],
            buffers.candidates[0..candidate_count],
            buffers.selections,
            buffers.resource_scratch,
            buffers.target_scratch,
            fact_batch.action_budget,
            &summary,
        );
    } else {
        selection_count = try selectQueueActionsFromCanonicalHeap(
            config,
            request.request_id,
            targets,
            buffers.pending_scratch[0..active_pending_count],
            buffers.candidates[0..candidate_count],
            buffers.selections,
            buffers.resource_scratch,
            buffers.target_scratch,
            fact_batch.action_budget,
            &summary,
        );
    }
    selection_count = compactSelections(
        buffers.candidates[0..candidate_count],
        buffers.selections[0..selection_count],
        buffers.target_scratch[0..targets.len],
        buffers.target_plans[0..targets.len],
    );

    target_index = 0;
    while (target_index < targets.len) : (target_index += 1) {
        const work = buffers.target_scratch[target_index];
        buffers.target_plans[target_index].active_phase_order = work.active_phase_order;
        buffers.target_plans[target_index].result_code = work.result_code;
        buffers.target_plans[target_index].danger_flags = work.danger_flags;
    }

    chooseGlobalSelection(
        config,
        targets,
        buffers.target_plans[0..targets.len],
        buffers.selections[0..selection_count],
        global,
    );

    summary.active_pending_count = @intCast(active_pending_count);
    summary.candidate_count = @intCast(candidate_count);
    summary.selection_count = @intCast(selection_count);
    return summary;
}

fn validateBuffers(
    target_count: usize,
    resource_count: usize,
    pending_count: usize,
    buffers: *const types.PlannerBuffers,
) types.PlanError!void {
    if (target_count > std.math.maxInt(u32) or resource_count > std.math.maxInt(u32) or
        pending_count > std.math.maxInt(u32))
    {
        return error.NumericOverflow;
    }
    const candidate_capacity = try requiredCandidateCapacity(resource_count);
    if (buffers.candidates.len < candidate_capacity or
        buffers.selections.len < resource_count or
        buffers.target_plans.len < target_count or
        buffers.resource_scratch.len < resource_count or
        buffers.target_scratch.len < target_count or
        buffers.resource_order.len < resource_count or
        buffers.pending_scratch.len < pending_count)
    {
        return error.BufferTooSmall;
    }
}

fn zeroBuffers(
    target_count: usize,
    resource_count: usize,
    pending_count: usize,
    buffers: *types.PlannerBuffers,
) void {
    for (buffers.target_plans[0..target_count]) |*item| item.* = std.mem.zeroes(types.TargetPlanOutput);
    for (buffers.target_scratch[0..target_count]) |*item| {
        item.* = std.mem.zeroes(types.TargetScratch);
        item.active_phase_order = 9;
        item.result_code = @intFromEnum(types.ResultCode.no_changes);
    }
    for (buffers.resource_scratch[0..resource_count], 0..) |*item, index| {
        item.* = std.mem.zeroes(types.ResourceScratch);
        item.target_input_index = types.none_index;
        item.group_index = types.none_index;
        buffers.resource_order[index] = @intCast(index);
    }
    for (buffers.pending_scratch[0..pending_count]) |*item| item.* = std.mem.zeroes(types.PendingActionInput);
}

fn assignTargets(
    config: *const config_module.Config,
    targets: []const types.TargetInput,
    resources: []const types.PrivateResourceInput,
    buffers: *types.PlannerBuffers,
) types.PlanError!void {
    for (targets, 0..) |target, target_index| {
        if (!filtering.validateTarget(&target, config)) return error.InvalidTarget;
        _ = capacity_module.resolve(&target.capacity) catch return error.InvalidCapacity;
        buffers.target_plans[target_index].target_key = target.target_key;
    }
    sorting.targetPlanKeys(buffers.target_plans[0..targets.len]);
    var key_index: usize = 1;
    while (key_index < targets.len) : (key_index += 1) {
        if (buffers.target_plans[key_index - 1].target_key ==
            buffers.target_plans[key_index].target_key)
        {
            return error.InvalidTarget;
        }
    }

    var target_index: usize = 0;
    while (target_index < targets.len) : (target_index += 1) {
        const target = &targets[target_index];
        const start: usize = target.private_resource_start;
        const count: usize = target.private_resource_count;
        if (start > resources.len or count > resources.len - start) return error.InvalidTarget;
        var offset: usize = 0;
        while (offset < count) : (offset += 1) {
            const resource_index = start + offset;
            const scratch = &buffers.resource_scratch[resource_index];
            if (scratch.flags & types.resource_scratch_assigned != 0) return error.InvalidTarget;
            scratch.target_input_index = @intCast(target_index);
            scratch.group_index = @intCast(resource_index);
            scratch.flags = types.resource_scratch_assigned;
        }
    }

    for (buffers.resource_scratch[0..resources.len]) |scratch| {
        if (scratch.flags & types.resource_scratch_assigned == 0) return error.InvalidTarget;
    }
}

fn markResourceValidity(
    targets: []const types.TargetInput,
    resources: []const types.PrivateResourceInput,
    projection_epoch: u64,
    buffers: *types.PlannerBuffers,
    summary: *types.PlanSummary,
) void {
    for (resources, 0..) |*resource, resource_index| {
        const target_index = buffers.resource_scratch[resource_index].target_input_index;
        if (filtering.validResource(resource, &targets[target_index], projection_epoch)) {
            buffers.resource_scratch[resource_index].flags |= types.resource_scratch_valid;
        }
    }

    sorting.resourceOrder(
        buffers.resource_order[0..resources.len],
        resources,
        buffers.resource_scratch,
    );
    var position: usize = 1;
    while (position < resources.len) : (position += 1) {
        const left_index = buffers.resource_order[position - 1];
        const right_index = buffers.resource_order[position];
        const left_scratch = &buffers.resource_scratch[left_index];
        const right_scratch = &buffers.resource_scratch[right_index];
        const left_authority = types.primaryAuthority(&resources[left_index]);
        const right_authority = types.primaryAuthority(&resources[right_index]);
        if (left_scratch.target_input_index == right_scratch.target_input_index and
            left_authority != null and
            right_authority != null and
            types.sameResourceLocation(left_authority.?, right_authority.?))
        {
            left_scratch.flags |= types.resource_scratch_duplicate;
            right_scratch.flags |= types.resource_scratch_duplicate;
        }
    }

    for (resources, 0..) |resource, resource_index| {
        const scratch = &buffers.resource_scratch[resource_index];
        if (scratch.flags & types.resource_scratch_duplicate != 0) {
            scratch.flags &= ~types.resource_scratch_valid;
        }
        if (scratch.flags & types.resource_scratch_valid == 0) {
            summary.invalid_resource_count += 1;
            continue;
        }
        const tier_index: usize = resource.tier;
        const target_work = &buffers.target_scratch[scratch.target_input_index];
        target_work.ledger_bytes[tier_index] = capacity_module.saturatingAdd(
            target_work.ledger_bytes[tier_index],
            resource.size_bytes,
        );
    }
}

fn preparePending(
    request: *const types.PlanRequest,
    state_pending: []const types.PendingActionInput,
    journal_pending: []const types.PendingActionInput,
    work: []types.PendingActionInput,
    summary: *types.PlanSummary,
) types.PlanError!usize {
    var active_count: usize = 0;
    for ([_][]const types.PendingActionInput{ state_pending, journal_pending }) |pending| {
        for (pending) |item| {
            if (!filtering.validatePending(&item)) return error.InvalidPending;
            const retains_past_deadline =
                item.state == @intFromEnum(types.PendingState.active) or
                item.state == @intFromEnum(types.PendingState.journal_pending) or
                item.state == @intFromEnum(types.PendingState.effect_uncertain);
            if (!retains_past_deadline and
                item.deadline_timestamp <= request.now_monotonic_timestamp)
            {
                continue;
            }
            work[active_count] = item;
            active_count += 1;
        }
    }
    sorting.pending(work[0..active_count]);

    var write: usize = 0;
    var read: usize = 0;
    while (read < active_count) {
        var selected = work[read];
        read += 1;
        while (read < active_count and
            types.sameResourceLocation(selected.authority, work[read].authority))
        {
            selected = mergePending(selected, work[read]) catch {
                summary.pending_duplicate_count += 1;
                return error.InvalidPending;
            };
            read += 1;
        }
        work[write] = selected;
        write += 1;
    }
    return write;
}

fn mergePending(
    left: types.PendingActionInput,
    right: types.PendingActionInput,
) types.PlanError!types.PendingActionInput {
    if (std.meta.eql(left, right)) return left;
    if (!types.sameExecutionAuthority(left.authority, right.authority) or
        left.target_key != right.target_key or
        left.size_bytes != right.size_bytes or
        left.tier != right.tier)
    {
        return error.InvalidPending;
    }
    const left_journal = isJournalPending(left);
    const right_journal = isJournalPending(right);
    if (left_journal == right_journal) return error.InvalidPending;
    const in_memory = if (left_journal) right else left;
    const durable = if (left_journal) left else right;
    if (in_memory.state != @intFromEnum(types.PendingState.reserved) and
        in_memory.state != @intFromEnum(types.PendingState.active))
    {
        return error.InvalidPending;
    }
    return durable;
}

fn isJournalPending(item: types.PendingActionInput) bool {
    return item.state == @intFromEnum(types.PendingState.journal_pending) or
        item.state == @intFromEnum(types.PendingState.effect_uncertain);
}

fn accumulatePendingReservations(
    pending: []const types.PendingActionInput,
    reserved_physical: *u64,
    reserved_virtual: *u64,
) void {
    for (pending) |item| {
        if (item.authority.action != types.action_move_down) continue;
        if (item.tier == @intFromEnum(types.Tier.vram)) {
            reserved_physical.* = capacity_module.saturatingAdd(reserved_physical.*, item.size_bytes);
        } else if (item.tier == @intFromEnum(types.Tier.physical_memory)) {
            reserved_virtual.* = capacity_module.saturatingAdd(reserved_virtual.*, item.size_bytes);
        }
    }
}

fn selectReleaseQuotaFromHeap(
    candidates: []types.CandidateOutput,
    goal: u64,
    scratch: []types.ResourceScratch,
) void {
    sorting.rankedMinHeap(candidates);
    var heap_count = candidates.len;
    var selected_bytes: u64 = 0;
    while (sorting.popRankedFirst(candidates, &heap_count)) |candidate_index| {
        const candidate = candidates[candidate_index];
        const resource = &scratch[candidate.resource_input_index];
        if (resource.flags & types.resource_scratch_quota_selected != 0) continue;
        resource.flags |= types.resource_scratch_quota_selected;
        selected_bytes = capacity_module.saturatingAdd(
            selected_bytes,
            candidate.estimated_release_bytes,
        );
        if (selected_bytes >= goal) break;
    }
}

fn compactQuotaCandidates(
    candidates: []types.CandidateOutput,
    segment_start: usize,
    segment_end: usize,
    scratch: []const types.ResourceScratch,
) usize {
    var write = segment_start;
    var read = segment_start;
    while (read < segment_end) : (read += 1) {
        const candidate = candidates[read];
        if (scratch[candidate.resource_input_index].flags &
            types.resource_scratch_quota_selected == 0)
        {
            continue;
        }
        candidates[write] = candidate;
        write += 1;
    }
    return write;
}

fn selectQueueActions(
    config: *const config_module.Config,
    request_id: u64,
    targets: []const types.TargetInput,
    pending: []const types.PendingActionInput,
    candidates: []types.CandidateOutput,
    selections: []types.SelectionOutput,
    resource_scratch: []types.ResourceScratch,
    target_scratch: []types.TargetScratch,
    action_budget: u32,
    summary: *types.PlanSummary,
) types.PlanError!usize {
    var selection_count: usize = 0;
    for (candidates, 0..) |*candidate, candidate_index| {
        if (selection_count >= action_budget) break;
        if (try considerQueueCandidate(
            config,
            request_id,
            targets,
            pending,
            candidate,
            candidate_index,
            &selections[selection_count],
            resource_scratch,
            target_scratch,
            summary,
        )) selection_count += 1;
    }
    return selection_count;
}

fn selectQueueActionsFromCanonicalHeap(
    config: *const config_module.Config,
    request_id: u64,
    targets: []const types.TargetInput,
    pending: []const types.PendingActionInput,
    candidates: []types.CandidateOutput,
    selections: []types.SelectionOutput,
    resource_scratch: []types.ResourceScratch,
    target_scratch: []types.TargetScratch,
    action_budget: u32,
    summary: *types.PlanSummary,
) types.PlanError!usize {
    if (action_budget == 0 or candidates.len == 0) return 0;
    sorting.canonicalMinHeap(candidates);
    var heap_count = candidates.len;
    var canonical_rank: u32 = 0;
    var selection_count: usize = 0;
    while (selection_count < action_budget) {
        const candidate_index = sorting.popCanonicalFirst(candidates, &heap_count) orelse break;
        const candidate = &candidates[candidate_index];
        candidate.rank = canonical_rank;
        canonical_rank += 1;
        if (try considerQueueCandidate(
            config,
            request_id,
            targets,
            pending,
            candidate,
            candidate_index,
            &selections[selection_count],
            resource_scratch,
            target_scratch,
            summary,
        )) selection_count += 1;
    }
    return selection_count;
}

fn considerQueueCandidate(
    config: *const config_module.Config,
    request_id: u64,
    targets: []const types.TargetInput,
    pending: []const types.PendingActionInput,
    candidate: *types.CandidateOutput,
    candidate_index: usize,
    selection: *types.SelectionOutput,
    resource_scratch: []types.ResourceScratch,
    target_scratch: []types.TargetScratch,
    summary: *types.PlanSummary,
) types.PlanError!bool {
    const target_index: usize = candidate.target_input_index;
    const target_work = &target_scratch[target_index];
    if (target_work.danger_flags != 0 or
        candidate.phase_order != target_work.active_phase_order or
        target_work.selected_action_count >= target_work.action_limit)
    {
        return false;
    }
    if (hasPending(pending, candidate.*)) {
        summary.pending_blocked_count += 1;
        return false;
    }
    const resource_work = &resource_scratch[candidate.resource_input_index];
    if (resource_work.flags & types.resource_scratch_action_selected != 0) return false;

    const resolved = try capacity_module.resolve(&targets[target_index].capacity);
    const danger = targetDangerFlags(config, candidate.*, &resolved, target_work.*);
    if (danger != 0) {
        target_work.danger_flags = danger;
        target_work.result_code = @intFromEnum(types.ResultCode.target_pool_danger);
        summary.danger_target_count += 1;
        return false;
    }

    selection.* = std.mem.zeroes(types.SelectionOutput);
    selection.target_key = candidate.target_key;
    selection.request_id = request_id;
    selection.authority = candidate.authority;
    selection.size_bytes = candidate.size_bytes;
    selection.estimated_release_bytes = candidate.estimated_release_bytes;
    selection.configuration_generation = config.generation;
    selection.final_importance = candidate.final_importance;
    selection.candidate_index = @intCast(candidate_index);
    selection.target_input_index = candidate.target_input_index;
    selection.tier = candidate.tier;
    selection.activity_score = candidate.activity_score;
    candidate.candidate_flags |= types.candidate_flag_queue_selected;
    resource_work.flags |= types.resource_scratch_action_selected;
    target_work.selected_action_count += 1;
    addReservation(candidate.*, target_work);
    return true;
}

fn compactSelections(
    candidates: []types.CandidateOutput,
    selections: []types.SelectionOutput,
    target_scratch: []types.TargetScratch,
    target_plans: []types.TargetPlanOutput,
) usize {
    for (target_plans) |*plan_output| {
        plan_output.selected_action_count = 0;
        plan_output.first_selection_index = types.none_index;
    }

    var write: usize = 0;
    for (selections) |selection| {
        const target_index: usize = selection.target_input_index;
        if (target_scratch[target_index].danger_flags != 0) {
            candidates[selection.candidate_index].candidate_flags &= ~types.candidate_flag_queue_selected;
            continue;
        }
        selections[write] = selection;
        const plan_output = &target_plans[target_index];
        if (plan_output.first_selection_index == types.none_index) {
            plan_output.first_selection_index = @intCast(write);
        }
        plan_output.selected_action_count += 1;
        write += 1;
    }
    return write;
}

fn targetDangerFlags(
    config: *const config_module.Config,
    candidate: types.CandidateOutput,
    resolved: *const capacity_module.Resolved,
    work: types.TargetScratch,
) u8 {
    if (candidate.authority.action != types.action_move_down) return 0;
    if (candidate.tier == @intFromEnum(types.Tier.vram)) {
        if (!hasRoom(
            resolved.total_bytes[@intFromEnum(types.Tier.physical_memory)],
            resolved.free_bytes[@intFromEnum(types.Tier.physical_memory)],
            work.reserved_physical_bytes,
            candidate.size_bytes,
            config.danger_min_physical_after_vram_move_ratio,
        )) return types.danger_memory | types.danger_paging_stall;
    } else if (candidate.tier == @intFromEnum(types.Tier.physical_memory)) {
        if (!hasRoom(
            resolved.total_bytes[@intFromEnum(types.Tier.virtual_memory)],
            resolved.free_bytes[@intFromEnum(types.Tier.virtual_memory)],
            work.reserved_virtual_bytes,
            candidate.size_bytes,
            config.danger_min_virtual_after_physical_move_ratio,
        )) return types.danger_virtual_memory | types.danger_paging_stall |
            types.danger_system_interrupt;
    }
    return 0;
}

fn hasRoom(
    total_bytes: u64,
    free_bytes: u64,
    reserved_bytes: u64,
    next_bytes: u64,
    minimum_free_ratio: f64,
) bool {
    if (total_bytes == 0) return true;
    const required = capacity_module.saturatingAdd(reserved_bytes, next_bytes);
    const remaining = if (free_bytes > required) free_bytes - required else 0;
    const ratio = @as(f64, @floatFromInt(remaining)) / @as(f64, @floatFromInt(total_bytes));
    return ratio >= minimum_free_ratio;
}

fn addReservation(candidate: types.CandidateOutput, work: *types.TargetScratch) void {
    if (candidate.authority.action != types.action_move_down) return;
    if (candidate.tier == @intFromEnum(types.Tier.vram)) {
        work.reserved_physical_bytes = capacity_module.saturatingAdd(
            work.reserved_physical_bytes,
            candidate.size_bytes,
        );
    } else if (candidate.tier == @intFromEnum(types.Tier.physical_memory)) {
        work.reserved_virtual_bytes = capacity_module.saturatingAdd(
            work.reserved_virtual_bytes,
            candidate.size_bytes,
        );
    }
}

fn chooseGlobalSelection(
    config: *const config_module.Config,
    targets: []const types.TargetInput,
    target_plans: []const types.TargetPlanOutput,
    selections: []const types.SelectionOutput,
    output: *types.GlobalSelectionOutput,
) void {
    var danger_target: ?usize = null;
    var danger_score: u32 = 0;
    for (target_plans, 0..) |target_plan, target_index| {
        if (target_plan.danger_flags == 0) continue;
        const severity = dangerSeverity(config, target_plan.danger_flags);
        if (danger_target == null or severity > danger_score or
            (severity == danger_score and
                target_plan.target_key < target_plans[danger_target.?].target_key))
        {
            danger_target = target_index;
            danger_score = severity;
        }
    }
    if (danger_target) |target_index| {
        output.* = zeroGlobalSelection();
        output.target_key = targets[target_index].target_key;
        output.owner_application_key = targets[target_index].owner_application_key;
        output.target_input_index = @intCast(target_index);
        output.danger_severity = danger_score;
        output.kind = @intFromEnum(types.SelectionKind.danger);
        output.result_code = @intFromEnum(types.ResultCode.target_pool_danger);
        output.danger_flags = target_plans[target_index].danger_flags;
        return;
    }

    if (selections.len == 0) return;
    const selected = selections[0];
    output.* = .{
        .target_key = selected.target_key,
        .owner_application_key = selected.authority.owner_application_key,
        .candidate_index = selected.candidate_index,
        .selection_index = 0,
        .target_input_index = selected.target_input_index,
        .danger_severity = 0,
        .kind = @intFromEnum(types.SelectionKind.action),
        .result_code = @intFromEnum(types.ResultCode.no_changes),
        .danger_flags = 0,
        .reserved0 = 0,
        .reserved1 = 0,
    };
}

fn dangerSeverity(config: *const config_module.Config, flags: u8) u32 {
    var result: u32 = 0;
    var index: usize = 0;
    while (index < config.danger_severity_weight.len) : (index += 1) {
        const bit: u8 = @as(u8, 1) << @intCast(index);
        if (flags & bit != 0) result += config.danger_severity_weight[index];
    }
    return result;
}

fn hasPending(pending: []const types.PendingActionInput, candidate: types.CandidateOutput) bool {
    var low: usize = 0;
    var high: usize = pending.len;
    while (low < high) {
        const middle = low + ((high - low) / 2);
        const comparison = comparePendingToCandidate(pending[middle], candidate);
        if (comparison < 0) {
            low = middle + 1;
        } else if (comparison > 0) {
            high = middle;
        } else {
            return true;
        }
    }
    return false;
}

fn comparePendingToCandidate(item: types.PendingActionInput, candidate: types.CandidateOutput) i8 {
    return sorting.compareResourceAuthority(item.authority, candidate.authority);
}

fn tierEnabled(request: *const types.PlanRequest, target: *const types.TargetInput, tier: u8) bool {
    const bit: u8 = @as(u8, 1) << @intCast(tier);
    if (request.enabled_tier_mask & bit == 0) return false;
    return tier != @intFromEnum(types.Tier.vram) or
        target.target_flags & types.target_flag_include_vram != 0;
}

fn countFilterReason(summary: *types.PlanSummary, reason: filtering.FilterReason) void {
    switch (reason) {
        .required_now => summary.required_now_count += 1,
        .no_legal_action => summary.no_legal_action_count += 1,
        .demand_blocked => summary.demand_blocked_count += 1,
        .intrinsic_blocked => summary.intrinsic_blocked_count += 1,
        .capacity_blocked => summary.capacity_blocked_count += 1,
        .invalid_resource => summary.invalid_resource_count += 1,
        .allowed => {},
    }
}

fn zeroGlobalSelection() types.GlobalSelectionOutput {
    var result = std.mem.zeroes(types.GlobalSelectionOutput);
    result.candidate_index = types.none_index;
    result.selection_index = types.none_index;
    result.target_input_index = types.none_index;
    result.kind = @intFromEnum(types.SelectionKind.none);
    result.result_code = @intFromEnum(types.ResultCode.no_changes);
    return result;
}
