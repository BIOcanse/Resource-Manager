const std = @import("std");
const protocol = @import("protocol.zig");
const alignment = @import("alignment.zig");
const ResultCode = @import("../../common/result_codes.zig").ResultCode;

pub const no_index: u32 = std.math.maxInt(u32);
const tombstone_index: u32 = no_index - 1;

pub const SourceSlot = struct {
    active: bool = false,
    persistent: bool = false,
    explicit_interval: bool = false,
    source_handle: u64 = 0,
    last_seen_milliseconds: u64 = 0,
    observed_interval_milliseconds: u64 = 0,
    explicit_interval_milliseconds: u64 = 0,
    item_count: u32 = 0,
};

pub const ItemSlot = struct {
    active: bool = false,
    item_handle: u64 = 0,
    last_sampled_milliseconds: u64 = 0,
    retry_at_milliseconds: u64 = 0,
    last_planned_epoch: u64 = 0,
    scheduled_due_milliseconds: u64 = 0,
    effective_interval_milliseconds: u64 = 0,
};

pub const MembershipSlot = struct {
    active: bool = false,
    source_index: u32 = 0,
    item_index: u32 = 0,
};

pub const Session = struct {
    allocator: std.mem.Allocator,
    mutex: std.atomic.Mutex = .unlocked,
    config: protocol.Config,
    sources: []SourceSlot,
    items: []ItemSlot,
    memberships: []MembershipSlot,
    source_index: []u32,
    item_index: []u32,
    scratch_item_handles: []u64,
    scratch_effective_intervals: []u64,
    scratch_source_counts: []u32,
    last_operation_epoch: u64 = 0,
    last_command_at_milliseconds: u64 = 0,
    last_plan_epoch: u64 = 0,
    next_wake_milliseconds: u64 = 0,
    state_revision: u64 = 1,

    pub fn create(config: *const protocol.Config) !*Session {
        if (!protocol.validConfig(config)) return error.InvalidConfiguration;
        const allocator = std.heap.page_allocator;
        const self = try allocator.create(Session);
        errdefer allocator.destroy(self);

        const source_capacity: usize = @intCast(config.maximum_source_count);
        const item_capacity: usize = @intCast(config.maximum_item_count);
        const membership_capacity: usize = @intCast(config.maximum_membership_count);

        const sources = try allocator.alloc(SourceSlot, source_capacity);
        errdefer allocator.free(sources);
        @memset(sources, .{});
        const items = try allocator.alloc(ItemSlot, item_capacity);
        errdefer allocator.free(items);
        @memset(items, .{});
        const memberships = try allocator.alloc(MembershipSlot, membership_capacity);
        errdefer allocator.free(memberships);
        @memset(memberships, .{});

        const source_index = try allocator.alloc(u32, try indexCapacity(source_capacity));
        errdefer allocator.free(source_index);
        @memset(source_index, no_index);
        const item_index = try allocator.alloc(u32, try indexCapacity(item_capacity));
        errdefer allocator.free(item_index);
        @memset(item_index, no_index);

        const scratch_item_handles = try allocator.alloc(u64, item_capacity);
        errdefer allocator.free(scratch_item_handles);
        @memset(scratch_item_handles, 0);
        const scratch_effective_intervals = try allocator.alloc(u64, item_capacity);
        errdefer allocator.free(scratch_effective_intervals);
        @memset(scratch_effective_intervals, std.math.maxInt(u64));
        const scratch_source_counts = try allocator.alloc(u32, item_capacity);
        errdefer allocator.free(scratch_source_counts);
        @memset(scratch_source_counts, 0);

        self.* = .{
            .allocator = allocator,
            .config = config.*,
            .sources = sources,
            .items = items,
            .memberships = memberships,
            .source_index = source_index,
            .item_index = item_index,
            .scratch_item_handles = scratch_item_handles,
            .scratch_effective_intervals = scratch_effective_intervals,
            .scratch_source_counts = scratch_source_counts,
        };
        return self;
    }

    pub fn destroy(self: *Session) void {
        const allocator = self.allocator;
        allocator.free(self.scratch_source_counts);
        allocator.free(self.scratch_effective_intervals);
        allocator.free(self.scratch_item_handles);
        allocator.free(self.item_index);
        allocator.free(self.source_index);
        allocator.free(self.memberships);
        allocator.free(self.items);
        allocator.free(self.sources);
        allocator.destroy(self);
    }

    pub fn reconfigure(self: *Session, config: *const protocol.Config) ResultCode {
        lockMutex(&self.mutex);
        defer self.mutex.unlock();
        if (!protocol.validConfig(config)) return .abi_mismatch;
        if (config.generation <= self.config.generation) return .stale_frame;
        if (!sameCapacities(config, &self.config)) return .invalid_argument;
        self.config = config.*;
        _ = self.rebuildItemAggregates();
        self.alignKnownItems(self.last_command_at_milliseconds);
        self.next_wake_milliseconds =
            self.nextWakeFromAggregates(self.last_command_at_milliseconds);
        self.advanceRevision();
        return .ok;
    }

    pub fn reset(self: *Session, input: *const protocol.ControlInput) ResultCode {
        lockMutex(&self.mutex);
        defer self.mutex.unlock();
        if (!protocol.validControl(input, &self.config)) return .abi_mismatch;
        if (!self.operationAdvances(input.operation_epoch, input.command_at_milliseconds)) {
            return .stale_frame;
        }
        @memset(self.sources, .{});
        @memset(self.items, .{});
        @memset(self.memberships, .{});
        @memset(self.source_index, no_index);
        @memset(self.item_index, no_index);
        self.last_plan_epoch = 0;
        self.next_wake_milliseconds = 0;
        self.commitOperation(input.operation_epoch, input.command_at_milliseconds);
        return .ok;
    }

    pub fn capacity(self: *const Session) protocol.Capacity {
        return .{
            .struct_size = @sizeOf(protocol.Capacity),
            .source_capacity = self.config.maximum_source_count,
            .item_capacity = self.config.maximum_item_count,
            .membership_capacity = self.config.maximum_membership_count,
            .due_item_capacity = self.config.maximum_due_item_count,
            .source_view_capacity = self.config.maximum_source_view_count,
            .expired_source_capacity = self.config.maximum_expired_source_count,
            .snapshot_source_capacity = self.config.maximum_source_count,
            .snapshot_item_capacity = self.config.maximum_item_count,
            .reserved_u32 = 0,
            .reserved = .{ 0, 0, 0 },
        };
    }

    pub fn operationAdvances(self: *const Session, epoch: u64, command_at: u64) bool {
        return epoch > self.last_operation_epoch and command_at >= self.last_command_at_milliseconds;
    }

    pub fn observedTimeValid(self: *const Session, observed_at: u64, command_at: u64) bool {
        return observed_at <= saturatingAdd(command_at, self.config.maximum_future_skew_milliseconds);
    }

    pub fn commitOperation(self: *Session, epoch: u64, command_at: u64) void {
        self.last_operation_epoch = epoch;
        self.last_command_at_milliseconds = command_at;
        self.advanceRevision();
    }

    pub fn advanceRevision(self: *Session) void {
        self.state_revision +%= 1;
        if (self.state_revision == 0) self.state_revision = 1;
    }

    pub fn effectiveSourceInterval(self: *const Session, source: *const SourceSlot) u64 {
        const requested = if (source.explicit_interval)
            source.explicit_interval_milliseconds
        else if (source.observed_interval_milliseconds != 0)
            source.observed_interval_milliseconds
        else
            self.config.default_interval_milliseconds;
        return @max(requested, self.config.minimum_interval_milliseconds);
    }

    pub fn findSource(self: *const Session, source_handle: u64) ?u32 {
        return findIndex(self.source_index, self.sources, source_handle, sourceHandleAt);
    }

    pub fn findItem(self: *const Session, item_handle: u64) ?u32 {
        return findIndex(self.item_index, self.items, item_handle, itemHandleAt);
    }

    pub fn removeSourceAt(self: *Session, source_slot_index: u32) void {
        self.deactivateSourceAt(source_slot_index);
        for (self.memberships) |*membership| {
            if (membership.active and membership.source_index == source_slot_index) {
                membership.* = .{};
            }
        }
    }

    pub fn sourceItemCount(self: *const Session, source_slot_index: u32) u32 {
        return self.sources[source_slot_index].item_count;
    }

    pub fn deactivateSourceAt(self: *Session, source_slot_index: u32) void {
        const handle = self.sources[source_slot_index].source_handle;
        removeIndex(self.source_index, self.sources, handle, sourceHandleAt);
        self.sources[source_slot_index] = .{};
    }

    pub fn clearInactiveSourceMemberships(self: *Session) void {
        for (self.memberships) |*membership| {
            if (membership.active and !self.sources[membership.source_index].active) {
                membership.* = .{};
            }
        }
    }

    pub fn activeSourceCount(self: *const Session) u32 {
        var count: u32 = 0;
        for (self.sources) |source| if (source.active) {
            count += 1;
        };
        return count;
    }

    pub fn knownItemCount(self: *const Session) u32 {
        var count: u32 = 0;
        for (self.items) |item| if (item.active) {
            count += 1;
        };
        return count;
    }

    pub fn membershipCount(self: *const Session) u32 {
        var count: u32 = 0;
        for (self.memberships) |membership| if (membership.active) {
            count += 1;
        };
        return count;
    }

    pub fn rebuildItemAggregates(self: *Session) u32 {
        @memset(self.scratch_effective_intervals, std.math.maxInt(u64));
        @memset(self.scratch_source_counts, 0);
        for (self.memberships) |membership| {
            if (!membership.active) continue;
            const source = &self.sources[membership.source_index];
            const item = &self.items[membership.item_index];
            if (!source.active or !item.active) continue;
            const interval = self.effectiveSourceInterval(source);
            const item_index: usize = @intCast(membership.item_index);
            if (interval < self.scratch_effective_intervals[item_index]) {
                self.scratch_effective_intervals[item_index] = interval;
            }
            self.scratch_source_counts[item_index] += 1;
        }

        var active_items: u32 = 0;
        for (self.scratch_source_counts) |source_count| {
            if (source_count != 0) active_items += 1;
        }
        return active_items;
    }

    pub fn nextWakeFromAggregates(self: *const Session, now: u64) u64 {
        var next_wake: u64 = 0;
        for (self.items, 0..) |item, item_index| {
            if (!item.active or self.scratch_source_counts[item_index] == 0) continue;
            const interval = self.scratch_effective_intervals[item_index];
            const due_at = alignment.dueAt(
                item.last_sampled_milliseconds,
                item.scheduled_due_milliseconds,
                item.retry_at_milliseconds,
                interval,
                now,
            );
            if (next_wake == 0 or due_at < next_wake) next_wake = due_at;
        }
        return next_wake;
    }

    pub fn alignKnownItems(self: *Session, command_at_milliseconds: u64) void {
        for (self.items, 0..) |*item, item_index| {
            if (!item.active) continue;
            if (self.scratch_source_counts[item_index] == 0) {
                item.retry_at_milliseconds = 0;
                item.scheduled_due_milliseconds = 0;
                item.effective_interval_milliseconds = 0;
                continue;
            }

            const interval = self.scratch_effective_intervals[item_index];
            if (item.effective_interval_milliseconds != interval) {
                item.retry_at_milliseconds = if (item.effective_interval_milliseconds != 0 and
                    item.retry_at_milliseconds != 0)
                    alignment.nextDue(command_at_milliseconds, interval)
                else
                    0;
                item.effective_interval_milliseconds = interval;
            }
            if (item.last_sampled_milliseconds == 0) {
                item.scheduled_due_milliseconds = 0;
                continue;
            }
            const aligned = alignment.nextDue(
                item.last_sampled_milliseconds,
                interval,
            );
            if (item.scheduled_due_milliseconds == 0 or
                item.scheduled_due_milliseconds > command_at_milliseconds)
            {
                item.scheduled_due_milliseconds = aligned;
            }
        }
    }

    pub fn track(
        self: *Session,
        input: *const protocol.TrackInput,
        references: []const protocol.ItemReference,
    ) ResultCode {
        if (!protocol.validTrackHeader(input, &self.config)) return .abi_mismatch;
        if (references.len != input.item_count) return .invalid_argument;
        if (!self.operationAdvances(input.operation_epoch, input.command_at_milliseconds)) {
            return .stale_frame;
        }
        if (!self.observedTimeValid(input.observed_at_milliseconds, input.command_at_milliseconds)) {
            return .stale_frame;
        }

        for (references, 0..) |reference, index| {
            if (!protocol.validItemReference(&reference)) return .abi_mismatch;
            self.scratch_item_handles[index] = reference.item_handle;
        }
        const handles = self.scratch_item_handles[0..references.len];
        std.sort.pdq(u64, handles, {}, lessThanU64);
        for (handles, 0..) |handle, index| {
            if (index != 0 and handles[index - 1] == handle) return .invalid_argument;
        }

        const existing_source_index = self.findSource(input.source_handle);
        if (existing_source_index) |source_index| {
            if (input.observed_at_milliseconds < self.sources[source_index].last_seen_milliseconds) {
                return .stale_frame;
            }
        }

        var new_item_count: usize = 0;
        for (handles) |handle| if (self.findItem(handle) == null) {
            new_item_count += 1;
        };
        if (new_item_count > self.freeItemCount()) return .out_of_memory;
        if (existing_source_index == null and self.freeSourceCount() == 0) return .out_of_memory;
        const reusable_memberships = if (existing_source_index) |source_index|
            self.sourceItemCount(source_index)
        else
            0;
        if (references.len > self.freeMembershipCount() + reusable_memberships) return .out_of_memory;

        const source_index = existing_source_index orelse self.allocateSource(input.source_handle) orelse
            unreachable;
        for (self.memberships) |*membership| {
            if (membership.active and membership.source_index == source_index) membership.* = .{};
        }

        for (handles) |handle| {
            const item_index = self.findItem(handle) orelse
                self.allocateItem(handle) orelse unreachable;
            const membership = self.allocateMembership() orelse unreachable;
            membership.* = .{
                .active = true,
                .source_index = source_index,
                .item_index = item_index,
            };
        }

        const source = &self.sources[source_index];
        const elapsed = input.observed_at_milliseconds - source.last_seen_milliseconds;
        if (source.last_seen_milliseconds != 0 and elapsed != 0 and elapsed < self.config.active_ttl_milliseconds) {
            source.observed_interval_milliseconds = if (source.observed_interval_milliseconds == 0)
                elapsed
            else
                smoothedInterval(source.observed_interval_milliseconds, elapsed);
        }
        source.active = true;
        source.persistent = input.flags & protocol.SourceFlags.persistent != 0;
        source.explicit_interval = input.flags & protocol.SourceFlags.explicit_interval != 0;
        source.source_handle = input.source_handle;
        source.last_seen_milliseconds = input.observed_at_milliseconds;
        source.explicit_interval_milliseconds = input.explicit_interval_milliseconds;
        source.item_count = @intCast(handles.len);

        _ = self.rebuildItemAggregates();
        self.alignKnownItems(input.command_at_milliseconds);
        self.next_wake_milliseconds = self.nextWakeFromAggregates(input.command_at_milliseconds);
        self.commitOperation(input.operation_epoch, input.command_at_milliseconds);
        return .ok;
    }

    pub fn remove(self: *Session, input: *const protocol.RemoveInput) ResultCode {
        if (!protocol.validRemove(input, &self.config)) return .abi_mismatch;
        if (!self.operationAdvances(input.operation_epoch, input.command_at_milliseconds)) {
            return .stale_frame;
        }
        const source_index = self.findSource(input.source_handle) orelse return .no_data;
        self.removeSourceAt(source_index);
        _ = self.rebuildItemAggregates();
        self.alignKnownItems(input.command_at_milliseconds);
        self.next_wake_milliseconds = self.nextWakeFromAggregates(input.command_at_milliseconds);
        self.commitOperation(input.operation_epoch, input.command_at_milliseconds);
        return .ok;
    }

    fn freeSourceCount(self: *const Session) usize {
        var count: usize = 0;
        for (self.sources) |source| if (!source.active) {
            count += 1;
        };
        return count;
    }

    fn freeItemCount(self: *const Session) usize {
        var count: usize = 0;
        for (self.items) |item| if (!item.active) {
            count += 1;
        };
        return count;
    }

    fn freeMembershipCount(self: *const Session) usize {
        var count: usize = 0;
        for (self.memberships) |membership| if (!membership.active) {
            count += 1;
        };
        return count;
    }

    fn allocateSource(self: *Session, handle: u64) ?u32 {
        for (self.sources, 0..) |*source, index| {
            if (source.active) continue;
            source.* = .{ .active = true, .source_handle = handle };
            if (!insertIndex(self.source_index, self.sources, handle, @intCast(index), sourceHandleAt)) {
                source.* = .{};
                return null;
            }
            return @intCast(index);
        }
        return null;
    }

    fn allocateItem(self: *Session, handle: u64) ?u32 {
        for (self.items, 0..) |*item, index| {
            if (item.active) continue;
            item.* = .{ .active = true, .item_handle = handle };
            if (!insertIndex(self.item_index, self.items, handle, @intCast(index), itemHandleAt)) {
                item.* = .{};
                return null;
            }
            return @intCast(index);
        }
        return null;
    }

    fn allocateMembership(self: *Session) ?*MembershipSlot {
        for (self.memberships) |*membership| {
            if (!membership.active) return membership;
        }
        return null;
    }
};

fn sameCapacities(left: *const protocol.Config, right: *const protocol.Config) bool {
    return left.maximum_source_count == right.maximum_source_count and
        left.maximum_item_count == right.maximum_item_count and
        left.maximum_membership_count == right.maximum_membership_count and
        left.maximum_due_item_count == right.maximum_due_item_count and
        left.maximum_source_view_count == right.maximum_source_view_count and
        left.maximum_expired_source_count == right.maximum_expired_source_count;
}

fn sourceHandleAt(slots: []const SourceSlot, index: u32) u64 {
    return slots[index].source_handle;
}

fn itemHandleAt(slots: []const ItemSlot, index: u32) u64 {
    return slots[index].item_handle;
}

fn findIndex(
    index: []const u32,
    slots: anytype,
    handle: u64,
    comptime handleAt: anytype,
) ?u32 {
    const mask = index.len - 1;
    var probe = hashHandle(handle) & mask;
    var remaining = index.len;
    while (remaining != 0) : (remaining -= 1) {
        const slot_index = index[probe];
        if (slot_index == no_index) return null;
        if (slot_index != tombstone_index and handleAt(slots, slot_index) == handle) return slot_index;
        probe = (probe + 1) & mask;
    }
    return null;
}

fn insertIndex(
    index: []u32,
    slots: anytype,
    handle: u64,
    slot_index: u32,
    comptime handleAt: anytype,
) bool {
    const mask = index.len - 1;
    var probe = hashHandle(handle) & mask;
    var first_tombstone: ?usize = null;
    var remaining = index.len;
    while (remaining != 0) : (remaining -= 1) {
        const existing = index[probe];
        if (existing == tombstone_index) {
            if (first_tombstone == null) first_tombstone = probe;
        } else if (existing == no_index) {
            index[first_tombstone orelse probe] = slot_index;
            return true;
        } else if (handleAt(slots, existing) == handle) {
            return false;
        }
        probe = (probe + 1) & mask;
    }
    if (first_tombstone) |tombstone| {
        index[tombstone] = slot_index;
        return true;
    }
    return false;
}

fn removeIndex(
    index: []u32,
    slots: anytype,
    handle: u64,
    comptime handleAt: anytype,
) void {
    const mask = index.len - 1;
    var probe = hashHandle(handle) & mask;
    var remaining = index.len;
    while (remaining != 0) : (remaining -= 1) {
        const slot_index = index[probe];
        if (slot_index == no_index) return;
        if (slot_index != tombstone_index and handleAt(slots, slot_index) == handle) {
            backwardShiftDelete(index, slots, probe, handleAt);
            return;
        }
        probe = (probe + 1) & mask;
    }
}

fn backwardShiftDelete(
    index: []u32,
    slots: anytype,
    deleted_index: usize,
    comptime handleAt: anytype,
) void {
    const mask = index.len - 1;
    var hole = deleted_index;
    index[hole] = no_index;
    var scan = (hole + 1) & mask;
    while (index[scan] != no_index) : (scan = (scan + 1) & mask) {
        if (index[scan] == tombstone_index) continue;
        const home = hashHandle(handleAt(slots, index[scan])) & mask;
        if (probeDistance(home, hole, mask) >= probeDistance(home, scan, mask)) continue;
        index[hole] = index[scan];
        index[scan] = no_index;
        hole = scan;
    }
}

fn probeDistance(home: usize, position: usize, mask: usize) usize {
    return (position -% home) & mask;
}

fn hashHandle(handle: u64) usize {
    var value = handle;
    value ^= value >> 33;
    value *%= 0xff51_afd7_ed55_8ccd;
    value ^= value >> 33;
    value *%= 0xc4ce_b9fe_1a85_ec53;
    value ^= value >> 33;
    return @truncate(value);
}

fn indexCapacity(item_capacity: usize) !usize {
    const doubled = try std.math.mul(usize, @max(item_capacity, 1), 2);
    return std.math.ceilPowerOfTwo(usize, doubled);
}

fn smoothedInterval(previous: u64, elapsed: u64) u64 {
    const numerator: u128 = @as(u128, previous) * 3 + elapsed;
    return @intCast(numerator / 4);
}

pub fn saturatingAdd(left: u64, right: u64) u64 {
    return std.math.add(u64, left, right) catch std.math.maxInt(u64);
}

fn lessThanU64(_: void, left: u64, right: u64) bool {
    return left < right;
}

pub fn lockMutex(mutex: *std.atomic.Mutex) void {
    while (!mutex.tryLock()) std.atomic.spinLoopHint();
}

test "sampling source index deletion compacts probes without historical tombstones" {
    var index = [_]u32{no_index} ** 64;
    var sources = [_]SourceSlot{.{}} ** 1;
    var ordinal: u64 = 1;
    while (ordinal <= 4_096) : (ordinal += 1) {
        sources[0] = .{ .active = true, .source_handle = ordinal };
        try std.testing.expect(insertIndex(&index, &sources, ordinal, 0, sourceHandleAt));
        try std.testing.expectEqual(@as(?u32, 0), findIndex(&index, &sources, ordinal, sourceHandleAt));
        removeIndex(&index, &sources, ordinal, sourceHandleAt);
        try std.testing.expectEqual(@as(?u32, null), findIndex(&index, &sources, ordinal, sourceHandleAt));
        sources[0] = .{};
    }

    for (index) |slot_index| try std.testing.expectEqual(no_index, slot_index);
}
