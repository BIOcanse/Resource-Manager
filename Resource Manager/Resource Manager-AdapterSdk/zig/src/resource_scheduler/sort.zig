const std = @import("std");
const types = @import("types.zig");

pub fn canonicalCandidates(items: []types.CandidateOutput) void {
    heapSort(types.CandidateOutput, items, canonicalComesAfter);
}

pub fn canonicalMinHeap(items: []types.CandidateOutput) void {
    if (items.len < 2) return;
    var start = items.len / 2;
    while (start > 0) {
        start -= 1;
        siftCanonicalMin(items, start, items.len);
    }
}

pub fn popCanonicalFirst(items: []types.CandidateOutput, heap_count: *usize) ?usize {
    if (heap_count.* == 0) return null;
    heap_count.* -= 1;
    std.mem.swap(types.CandidateOutput, &items[0], &items[heap_count.*]);
    if (heap_count.* > 1) siftCanonicalMin(items, 0, heap_count.*);
    return heap_count.*;
}

pub fn rankedMinHeap(items: []types.CandidateOutput) void {
    if (items.len < 2) return;
    var start = items.len / 2;
    while (start > 0) {
        start -= 1;
        siftRankedMin(items, start, items.len);
    }
}

pub fn popRankedFirst(items: []types.CandidateOutput, heap_count: *usize) ?usize {
    if (heap_count.* == 0) return null;
    heap_count.* -= 1;
    std.mem.swap(types.CandidateOutput, &items[0], &items[heap_count.*]);
    if (heap_count.* > 1) siftRankedMin(items, 0, heap_count.*);
    return heap_count.*;
}

pub fn resourceOrder(
    order: []u32,
    resources: []const types.PrivateResourceInput,
    scratch: []const types.ResourceScratch,
) void {
    if (order.len < 2) return;
    var start = order.len / 2;
    while (start > 0) {
        start -= 1;
        siftResourceOrder(order, resources, scratch, start, order.len);
    }
    var end = order.len;
    while (end > 1) {
        end -= 1;
        std.mem.swap(u32, &order[0], &order[end]);
        siftResourceOrder(order, resources, scratch, 0, end);
    }
}

pub fn pending(items: []types.PendingActionInput) void {
    heapSort(types.PendingActionInput, items, pendingComesAfter);
}

pub fn targetPlanKeys(items: []types.TargetPlanOutput) void {
    heapSort(types.TargetPlanOutput, items, targetPlanKeyComesAfter);
}

pub fn canonicalComesBefore(left: types.CandidateOutput, right: types.CandidateOutput) bool {
    return compareCanonical(left, right) < 0;
}

pub fn pendingKeyCompare(left: types.PendingActionInput, right: types.PendingActionInput) i8 {
    const authority = compareResourceAuthority(left.authority, right.authority);
    if (authority != 0) return authority;
    if (left.authority.action != right.authority.action) {
        return if (left.authority.action < right.authority.action) -1 else 1;
    }
    if (left.target_key != right.target_key) return if (left.target_key < right.target_key) -1 else 1;
    if (left.pending_generation != right.pending_generation) {
        return if (left.pending_generation > right.pending_generation) -1 else 1;
    }
    if (left.deadline_timestamp != right.deadline_timestamp) {
        return if (left.deadline_timestamp > right.deadline_timestamp) -1 else 1;
    }
    return 0;
}

fn compareCanonical(left: types.CandidateOutput, right: types.CandidateOutput) i8 {
    if (left.phase_order != right.phase_order) return if (left.phase_order < right.phase_order) -1 else 1;
    if (left.final_importance != right.final_importance) {
        return if (left.final_importance < right.final_importance) -1 else 1;
    }
    if (left.estimated_release_bytes != right.estimated_release_bytes) {
        return if (left.estimated_release_bytes > right.estimated_release_bytes) -1 else 1;
    }
    if (left.activity_score != right.activity_score) {
        return if (left.activity_score < right.activity_score) -1 else 1;
    }
    if (left.target_key != right.target_key) return if (left.target_key < right.target_key) -1 else 1;
    if (left.authority.owner_application_key != right.authority.owner_application_key) {
        return if (left.authority.owner_application_key < right.authority.owner_application_key) -1 else 1;
    }
    const authority = compareExecutionAuthority(left.authority, right.authority);
    if (authority != 0) return authority;
    if (left.resource_input_index != right.resource_input_index) {
        return if (left.resource_input_index < right.resource_input_index) -1 else 1;
    }
    return 0;
}

fn compareRanked(left: types.CandidateOutput, right: types.CandidateOutput) i8 {
    if (left.final_importance != right.final_importance) {
        return if (left.final_importance < right.final_importance) -1 else 1;
    }
    if (left.estimated_release_bytes != right.estimated_release_bytes) {
        return if (left.estimated_release_bytes > right.estimated_release_bytes) -1 else 1;
    }
    if (left.activity_score != right.activity_score) {
        return if (left.activity_score < right.activity_score) -1 else 1;
    }
    const authority = compareExecutionAuthority(left.authority, right.authority);
    if (authority != 0) return authority;
    return 0;
}

fn canonicalComesAfter(left: types.CandidateOutput, right: types.CandidateOutput) bool {
    return compareCanonical(left, right) > 0;
}

fn pendingComesAfter(left: types.PendingActionInput, right: types.PendingActionInput) bool {
    return pendingKeyCompare(left, right) > 0;
}

fn targetPlanKeyComesAfter(left: types.TargetPlanOutput, right: types.TargetPlanOutput) bool {
    return left.target_key > right.target_key;
}

fn heapSort(comptime T: type, items: []T, comptime comes_after: fn (T, T) bool) void {
    if (items.len < 2) return;
    var start = items.len / 2;
    while (start > 0) {
        start -= 1;
        siftDown(T, items, start, items.len, comes_after);
    }
    var end = items.len;
    while (end > 1) {
        end -= 1;
        std.mem.swap(T, &items[0], &items[end]);
        siftDown(T, items, 0, end, comes_after);
    }
}

fn siftDown(
    comptime T: type,
    items: []T,
    root_start: usize,
    end: usize,
    comptime comes_after: fn (T, T) bool,
) void {
    var root = root_start;
    while (true) {
        const left = (root * 2) + 1;
        if (left >= end) return;
        var candidate = left;
        const right = left + 1;
        if (right < end and comes_after(items[right], items[left])) candidate = right;
        if (!comes_after(items[candidate], items[root])) return;
        std.mem.swap(T, &items[root], &items[candidate]);
        root = candidate;
    }
}

fn siftCanonicalMin(
    items: []types.CandidateOutput,
    root_start: usize,
    end: usize,
) void {
    var root = root_start;
    while (true) {
        const left = (root * 2) + 1;
        if (left >= end) return;
        var candidate = left;
        const right = left + 1;
        if (right < end and canonicalComesBefore(items[right], items[left])) {
            candidate = right;
        }
        if (!canonicalComesBefore(items[candidate], items[root])) return;
        std.mem.swap(types.CandidateOutput, &items[root], &items[candidate]);
        root = candidate;
    }
}

fn siftRankedMin(
    items: []types.CandidateOutput,
    root_start: usize,
    end: usize,
) void {
    var root = root_start;
    while (true) {
        const left = (root * 2) + 1;
        if (left >= end) return;
        var candidate = left;
        const right = left + 1;
        if (right < end and compareRanked(items[right], items[left]) < 0) {
            candidate = right;
        }
        if (compareRanked(items[candidate], items[root]) >= 0) return;
        std.mem.swap(types.CandidateOutput, &items[root], &items[candidate]);
        root = candidate;
    }
}

fn siftResourceOrder(
    order: []u32,
    resources: []const types.PrivateResourceInput,
    scratch: []const types.ResourceScratch,
    root_start: usize,
    end: usize,
) void {
    var root = root_start;
    while (true) {
        const left = (root * 2) + 1;
        if (left >= end) return;
        var candidate = left;
        const right = left + 1;
        if (right < end and resourceOrderAfter(order[right], order[left], resources, scratch)) {
            candidate = right;
        }
        if (!resourceOrderAfter(order[candidate], order[root], resources, scratch)) return;
        std.mem.swap(u32, &order[root], &order[candidate]);
        root = candidate;
    }
}

pub fn compareResourceAuthority(
    left: types.ResourceExecutionAuthority,
    right: types.ResourceExecutionAuthority,
) i8 {
    if (left.source != right.source) return if (left.source < right.source) -1 else 1;
    if (left.ledger_instance_id != right.ledger_instance_id) {
        return if (left.ledger_instance_id < right.ledger_instance_id) -1 else 1;
    }
    if (left.resource_slot != right.resource_slot) {
        return if (left.resource_slot < right.resource_slot) -1 else 1;
    }
    if (left.resource_generation != right.resource_generation) {
        return if (left.resource_generation < right.resource_generation) -1 else 1;
    }
    if (left.resource_key != right.resource_key) return if (left.resource_key < right.resource_key) -1 else 1;
    return 0;
}

pub fn compareExecutionAuthority(
    left: types.ResourceExecutionAuthority,
    right: types.ResourceExecutionAuthority,
) i8 {
    const resource = compareResourceAuthority(left, right);
    if (resource != 0) return resource;
    if (left.action != right.action) return if (left.action < right.action) -1 else 1;
    if (left.action_attempt_id_high != right.action_attempt_id_high) {
        return if (left.action_attempt_id_high < right.action_attempt_id_high) -1 else 1;
    }
    if (left.action_attempt_id_low != right.action_attempt_id_low) {
        return if (left.action_attempt_id_low < right.action_attempt_id_low) -1 else 1;
    }
    if (left.projection_epoch != right.projection_epoch) {
        return if (left.projection_epoch < right.projection_epoch) -1 else 1;
    }
    return 0;
}

fn resourceOrderAfter(
    left_index: u32,
    right_index: u32,
    resources: []const types.PrivateResourceInput,
    scratch: []const types.ResourceScratch,
) bool {
    const left_scratch = scratch[left_index];
    const right_scratch = scratch[right_index];
    if (left_scratch.target_input_index != right_scratch.target_input_index) {
        return left_scratch.target_input_index > right_scratch.target_input_index;
    }
    const left = types.primaryAuthority(&resources[left_index]);
    const right = types.primaryAuthority(&resources[right_index]);
    if (left == null or right == null) {
        if (left == null and right != null) return true;
        if (left != null and right == null) return false;
        return left_index > right_index;
    }
    const order = compareResourceAuthority(left.?, right.?);
    if (order != 0) return order > 0;
    return left_index > right_index;
}
