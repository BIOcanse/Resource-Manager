const std = @import("std");
const protocol = @import("protocol.zig");

pub fn compareId(left: protocol.Identifier128, right: protocol.Identifier128) std.math.Order {
    if (left.high < right.high) return .lt;
    if (left.high > right.high) return .gt;
    if (left.low < right.low) return .lt;
    if (left.low > right.low) return .gt;
    return .eq;
}

pub fn sameTable(left: protocol.ResourceHandle, right: protocol.ResourceHandle) bool {
    return compareTable(left, right) == .eq;
}

pub fn sameResource(left: protocol.ResourceHandle, right: protocol.ResourceHandle) bool {
    return compareResource(left, right) == .eq;
}

pub fn compareTable(
    left: protocol.ResourceHandle,
    right: protocol.ResourceHandle,
) std.math.Order {
    var result = std.math.order(left.manager_instance_id, right.manager_instance_id);
    if (result != .eq) return result;
    result = std.math.order(left.table_slot_index, right.table_slot_index);
    if (result != .eq) return result;
    result = std.math.order(left.table_slot_generation, right.table_slot_generation);
    if (result != .eq) return result;
    result = compareId(left.table_id, right.table_id);
    if (result != .eq) return result;
    return std.math.order(left.table_incarnation, right.table_incarnation);
}

pub fn compareResource(
    left: protocol.ResourceHandle,
    right: protocol.ResourceHandle,
) std.math.Order {
    var result = compareTable(left, right);
    if (result != .eq) return result;
    result = std.math.order(left.resource_slot_index, right.resource_slot_index);
    if (result != .eq) return result;
    result = std.math.order(left.resource_slot_generation, right.resource_slot_generation);
    if (result != .eq) return result;
    return compareId(left.resource_uid, right.resource_uid);
}

pub fn hashResource(
    table_slot_generation: u64,
    table_id: protocol.Identifier128,
    table_incarnation: u64,
    resource_uid: protocol.Identifier128,
) u64 {
    var hash: u64 = 14695981039346656037;
    hashU64(&hash, table_slot_generation);
    hashU64(&hash, table_id.low);
    hashU64(&hash, table_id.high);
    hashU64(&hash, table_incarnation);
    hashU64(&hash, resource_uid.low);
    hashU64(&hash, resource_uid.high);
    return if (hash == 0) 14695981039346656037 else hash;
}

fn hashU64(hash: *u64, value: u64) void {
    var remaining = value;
    var index: u8 = 0;
    while (index < 8) : (index += 1) {
        hash.* ^= @truncate(remaining);
        hash.* *%= 1099511628211;
        remaining >>= 8;
    }
}
