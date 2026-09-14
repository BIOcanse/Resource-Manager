const std = @import("std");
const protocol = @import("protocol.zig");
const identity = @import("identity.zig");

pub fn sortCapacity(values: []protocol.Intent) void {
    std.sort.heap(protocol.Intent, values, {}, capacityBefore);
}

pub fn sortMode(values: []protocol.Intent) void {
    std.sort.heap(protocol.Intent, values, {}, modeBefore);
}

pub fn sortCanonical(values: []protocol.Intent) void {
    std.sort.heap(protocol.Intent, values, {}, canonicalBefore);
}

pub fn sortCapacityCanonical(values: []protocol.Intent) void {
    std.sort.heap(protocol.Intent, values, {}, capacityCanonicalBefore);
}

pub fn capacityLess(left: protocol.Intent, right: protocol.Intent) bool {
    const impact_order = compareImpact(left.access_loss_impact, right.access_loss_impact);
    if (impact_order != .eq) return impact_order == .lt;
    if (left.settled_activity != right.settled_activity) {
        return left.settled_activity < right.settled_activity;
    }
    const recovery_order = compareRecoveryBurden(left, right);
    if (recovery_order != .eq) return recovery_order == .lt;
    return identity.compareId(left.resource.resource_uid, right.resource.resource_uid) == .lt;
}

pub fn modeLess(left: protocol.Intent, right: protocol.Intent) bool {
    const impact_order = compareImpact(left.access_loss_impact, right.access_loss_impact);
    if (impact_order != .eq) return impact_order == .lt;
    if (left.settled_activity != right.settled_activity) {
        return left.settled_activity < right.settled_activity;
    }
    const recovery_order = compareRecoveryBurden(left, right);
    if (recovery_order != .eq) return recovery_order == .lt;
    if (left.size_bytes != right.size_bytes) return left.size_bytes > right.size_bytes;
    return identity.compareId(left.resource.resource_uid, right.resource.resource_uid) == .lt;
}

pub fn canonicalLess(left: protocol.Intent, right: protocol.Intent) bool {
    const left_capacity = left.reason_flags & protocol.intent_reason_capacity != 0;
    const right_capacity = right.reason_flags & protocol.intent_reason_capacity != 0;
    if (left_capacity != right_capacity) return left_capacity;
    return identity.compareResource(left.resource, right.resource) == .lt;
}

fn capacityCanonicalLess(left: protocol.Intent, right: protocol.Intent) bool {
    const table_order = identity.compareTable(left.resource, right.resource);
    if (table_order != .eq) return table_order == .lt;
    return capacityLess(left, right);
}

fn compareImpact(
    left: protocol.AccessLossImpact,
    right: protocol.AccessLossImpact,
) std.math.Order {
    const left_rank = impactRank(left);
    const right_rank = impactRank(right);
    if (left_rank < right_rank) return .lt;
    if (left_rank > right_rank) return .gt;
    return .eq;
}

fn compareRecoveryBurden(left: protocol.Intent, right: protocol.Intent) std.math.Order {
    const left_burden = @as(u128, left.recovery_cost_coefficient) * left.size_bytes;
    const right_burden = @as(u128, right.recovery_cost_coefficient) * right.size_bytes;
    return std.math.order(left_burden, right_burden);
}

fn impactRank(value: protocol.AccessLossImpact) u8 {
    return switch (value) {
        .unobservable_now => 0,
        .observable_now => 1,
        .fatal => 2,
    };
}

fn capacityBefore(_: void, left: protocol.Intent, right: protocol.Intent) bool {
    return capacityLess(left, right);
}

fn modeBefore(_: void, left: protocol.Intent, right: protocol.Intent) bool {
    return modeLess(left, right);
}

fn canonicalBefore(_: void, left: protocol.Intent, right: protocol.Intent) bool {
    return canonicalLess(left, right);
}

fn capacityCanonicalBefore(_: void, left: protocol.Intent, right: protocol.Intent) bool {
    return capacityCanonicalLess(left, right);
}
