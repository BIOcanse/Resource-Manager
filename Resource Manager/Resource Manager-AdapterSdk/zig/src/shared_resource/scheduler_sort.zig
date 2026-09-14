const std = @import("std");

pub fn heapSort(comptime Candidate: type, items: []Candidate) void {
    if (items.len < 2) return;
    var start = items.len / 2;
    while (start > 0) {
        start -= 1;
        siftDown(Candidate, items, start, items.len);
    }
    var end = items.len;
    while (end > 1) {
        end -= 1;
        std.mem.swap(Candidate, &items[0], &items[end]);
        siftDown(Candidate, items, 0, end);
    }
}

pub fn heapSortBy(
    comptime Candidate: type,
    items: []Candidate,
    comptime comes_after: fn (Candidate, Candidate) bool,
) void {
    if (items.len < 2) return;
    var start = items.len / 2;
    while (start > 0) {
        start -= 1;
        siftDownBy(Candidate, items, start, items.len, comes_after);
    }
    var end = items.len;
    while (end > 1) {
        end -= 1;
        std.mem.swap(Candidate, &items[0], &items[end]);
        siftDownBy(Candidate, items, 0, end, comes_after);
    }
}

fn siftDown(
    comptime Candidate: type,
    items: []Candidate,
    root_start: usize,
    end: usize,
) void {
    var root = root_start;
    while (true) {
        const left = (root * 2) + 1;
        if (left >= end) return;
        var candidate = left;
        const right = left + 1;
        if (right < end and comesAfter(items[right], items[left])) candidate = right;
        if (!comesAfter(items[candidate], items[root])) return;
        std.mem.swap(Candidate, &items[root], &items[candidate]);
        root = candidate;
    }
}

fn siftDownBy(
    comptime Candidate: type,
    items: []Candidate,
    root_start: usize,
    end: usize,
    comptime comes_after: fn (Candidate, Candidate) bool,
) void {
    var root = root_start;
    while (true) {
        const left = (root * 2) + 1;
        if (left >= end) return;
        var candidate = left;
        const right = left + 1;
        if (right < end and comes_after(items[right], items[left])) candidate = right;
        if (!comes_after(items[candidate], items[root])) return;
        std.mem.swap(Candidate, &items[root], &items[candidate]);
        root = candidate;
    }
}

fn comesAfter(left: anytype, right: @TypeOf(left)) bool {
    if (left.public_candidate_importance != right.public_candidate_importance) {
        return left.public_candidate_importance > right.public_candidate_importance;
    }
    if (left.resource.public_resource_id != right.resource.public_resource_id) {
        return left.resource.public_resource_id > right.resource.public_resource_id;
    }
    if (left.resource.ledger_instance_id != right.resource.ledger_instance_id) {
        return left.resource.ledger_instance_id > right.resource.ledger_instance_id;
    }
    if (left.resource.resource_slot != right.resource.resource_slot) {
        return left.resource.resource_slot > right.resource.resource_slot;
    }
    if (left.resource.resource_generation != right.resource.resource_generation) {
        return left.resource.resource_generation > right.resource.resource_generation;
    }
    if (left.owner_application_key != right.owner_application_key) {
        return left.owner_application_key > right.owner_application_key;
    }
    return left.action_mask > right.action_mask;
}
