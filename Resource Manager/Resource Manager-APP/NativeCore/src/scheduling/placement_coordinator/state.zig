const std = @import("std");
const protocol = @import("protocol.zig");
const ResultCode = @import("../../common/result_codes.zig").ResultCode;

pub const no_index: u32 = std.math.maxInt(u32);

pub const Slot = struct {
    active: bool = false,
    desired_seen: bool = false,
    applied_seen: bool = false,
    override_external_state: bool = false,
    state: protocol.SlotState = .empty,
    pending_disposition: ?protocol.ActionDisposition = null,
    target_key: u64 = 0,
    record_key: u64 = 0,
    resource_kind: u32 = 0,
    placement_kind: u32 = 0,
    desired_digest: u64 = 0,
    applied_digest: u64 = 0,
    previous_digest: u64 = 0,
    current_digest: u64 = 0,
    pending_action_id: u64 = 0,
    retry_at_milliseconds: u64 = 0,
    last_cycle_epoch: u64 = 0,
    desired_process_start_key: u64 = 0,
    applied_process_start_key: u64 = 0,
    desired_process_id: u32 = 0,
    applied_process_id: u32 = 0,
    desired_priority: u32 = 0,
    retry_count: u32 = 0,
    pending_reason_mask: u64 = 0,
};

pub const Session = struct {
    allocator: std.mem.Allocator,
    mutex: std.atomic.Mutex = .unlocked,
    config: protocol.Config,
    slots: []Slot,
    state_index: []u32,
    scratch_targets: []u64,
    scratch_records: []u64,
    scratch_index: []u32,
    next_action_id: u64 = 1,
    state_revision: u64 = 1,
    last_cycle_epoch: u64 = 0,
    next_wake_milliseconds: u64 = 0,

    pub fn create(config: *const protocol.Config) !*Session {
        if (!protocol.validConfig(config)) return error.InvalidConfiguration;
        const allocator = std.heap.page_allocator;
        const self = try allocator.create(Session);
        errdefer allocator.destroy(self);

        const state_capacity: usize = @intCast(config.maximum_state_count);
        const input_capacity = try std.math.add(
            usize,
            @intCast(config.maximum_desired_count),
            @intCast(config.maximum_applied_count),
        );
        const scratch_capacity = try std.math.add(usize, state_capacity, input_capacity);
        const state_index_capacity = try indexCapacity(state_capacity);
        const scratch_index_capacity = try indexCapacity(scratch_capacity);

        const slots = try allocator.alloc(Slot, state_capacity);
        errdefer allocator.free(slots);
        @memset(slots, .{});
        const state_index = try allocator.alloc(u32, state_index_capacity);
        errdefer allocator.free(state_index);
        @memset(state_index, no_index);
        const scratch_targets = try allocator.alloc(u64, scratch_capacity);
        errdefer allocator.free(scratch_targets);
        @memset(scratch_targets, 0);
        const scratch_records = try allocator.alloc(u64, scratch_capacity);
        errdefer allocator.free(scratch_records);
        @memset(scratch_records, 0);
        const scratch_index = try allocator.alloc(u32, scratch_index_capacity);
        errdefer allocator.free(scratch_index);
        @memset(scratch_index, no_index);

        self.* = .{
            .allocator = allocator,
            .config = config.*,
            .slots = slots,
            .state_index = state_index,
            .scratch_targets = scratch_targets,
            .scratch_records = scratch_records,
            .scratch_index = scratch_index,
        };
        return self;
    }

    pub fn destroy(self: *Session) void {
        const allocator = self.allocator;
        allocator.free(self.scratch_index);
        allocator.free(self.scratch_records);
        allocator.free(self.scratch_targets);
        allocator.free(self.state_index);
        allocator.free(self.slots);
        allocator.destroy(self);
    }

    pub fn reconfigure(self: *Session, config: *const protocol.Config) ResultCode {
        lockMutex(&self.mutex);
        defer self.mutex.unlock();
        if (!protocol.validConfig(config)) return .abi_mismatch;
        if (config.generation <= self.config.generation) return .stale_frame;
        if (config.maximum_desired_count != self.config.maximum_desired_count or
            config.maximum_applied_count != self.config.maximum_applied_count or
            config.maximum_action_count != self.config.maximum_action_count or
            config.maximum_state_count != self.config.maximum_state_count)
        {
            return .invalid_argument;
        }
        self.config = config.*;
        self.state_revision +%= 1;
        if (self.state_revision == 0) self.state_revision = 1;
        return .ok;
    }

    pub fn reset(self: *Session) ResultCode {
        lockMutex(&self.mutex);
        defer self.mutex.unlock();
        @memset(self.slots, .{});
        @memset(self.state_index, no_index);
        self.next_action_id = 1;
        self.last_cycle_epoch = 0;
        self.next_wake_milliseconds = 0;
        self.state_revision +%= 1;
        if (self.state_revision == 0) self.state_revision = 1;
        return .ok;
    }

    pub fn capacity(self: *const Session) protocol.Capacity {
        return .{
            .struct_size = @sizeOf(protocol.Capacity),
            .state_capacity = self.config.maximum_state_count,
            .action_capacity = self.config.maximum_action_count,
            .snapshot_state_capacity = self.config.maximum_state_count,
            .reserved = .{ 0, 0, 0, 0 },
        };
    }

    pub fn rebuildStateIndex(self: *Session) bool {
        @memset(self.state_index, no_index);
        for (self.slots, 0..) |slot, slot_index| {
            if (!slot.active) continue;
            if (!insertIndex(
                self.state_index,
                self.slots,
                slot.target_key,
                slot.record_key,
                @intCast(slot_index),
            )) return false;
        }
        return true;
    }

    pub fn findSlot(self: *const Session, target_key: u64, record_key: u64) ?u32 {
        if (self.state_index.len == 0) return null;
        const mask = self.state_index.len - 1;
        var probe: usize = hashIdentity(target_key, record_key) & mask;
        var remaining = self.state_index.len;
        while (remaining != 0) : (remaining -= 1) {
            const slot_index = self.state_index[probe];
            if (slot_index == no_index) return null;
            const slot = self.slots[slot_index];
            if (slot.active and protocol.sameIdentity(
                target_key,
                record_key,
                slot.target_key,
                slot.record_key,
            )) return slot_index;
            probe = (probe + 1) & mask;
        }
        return null;
    }

    pub fn findOrCreateSlot(self: *Session, target_key: u64, record_key: u64) ?u32 {
        if (self.findSlot(target_key, record_key)) |slot_index| return slot_index;
        for (self.slots, 0..) |*slot, slot_index| {
            if (slot.active) continue;
            slot.* = .{
                .active = true,
                .target_key = target_key,
                .record_key = record_key,
            };
            if (!insertIndex(
                self.state_index,
                self.slots,
                target_key,
                record_key,
                @intCast(slot_index),
            )) {
                slot.* = .{};
                return null;
            }
            return @intCast(slot_index);
        }
        return null;
    }

    pub fn clearSlot(self: *Session, slot_index: u32) void {
        self.slots[slot_index] = .{};
    }

    pub fn beginScratch(self: *Session) void {
        @memset(self.scratch_index, no_index);
        @memset(self.scratch_targets, 0);
        @memset(self.scratch_records, 0);
    }

    pub fn insertScratch(self: *Session, target_key: u64, record_key: u64, row_index: u32) bool {
        const mask = self.scratch_index.len - 1;
        var probe: usize = hashIdentity(target_key, record_key) & mask;
        var remaining = self.scratch_index.len;
        while (remaining != 0) : (remaining -= 1) {
            const existing = self.scratch_index[probe];
            if (existing == no_index) {
                self.scratch_targets[row_index] = target_key;
                self.scratch_records[row_index] = record_key;
                self.scratch_index[probe] = row_index;
                return true;
            }
            if (self.scratch_targets[existing] == target_key and
                self.scratch_records[existing] == record_key) return false;
            probe = (probe + 1) & mask;
        }
        return false;
    }

    pub fn findScratch(self: *const Session, target_key: u64, record_key: u64) ?u32 {
        const mask = self.scratch_index.len - 1;
        var probe: usize = hashIdentity(target_key, record_key) & mask;
        var remaining = self.scratch_index.len;
        while (remaining != 0) : (remaining -= 1) {
            const existing = self.scratch_index[probe];
            if (existing == no_index) return null;
            if (self.scratch_targets[existing] == target_key and
                self.scratch_records[existing] == record_key) return existing;
            probe = (probe + 1) & mask;
        }
        return null;
    }

    pub fn allocateActionId(self: *Session) u64 {
        const result = self.next_action_id;
        self.next_action_id +%= 1;
        if (self.next_action_id == 0) self.next_action_id = 1;
        return result;
    }

    pub fn advanceRevision(self: *Session) void {
        self.state_revision +%= 1;
        if (self.state_revision == 0) self.state_revision = 1;
    }
};

fn insertIndex(
    index: []u32,
    slots: []const Slot,
    target_key: u64,
    record_key: u64,
    slot_index: u32,
) bool {
    const mask = index.len - 1;
    var probe: usize = hashIdentity(target_key, record_key) & mask;
    var remaining = index.len;
    while (remaining != 0) : (remaining -= 1) {
        const existing = index[probe];
        if (existing == no_index) {
            index[probe] = slot_index;
            return true;
        }
        const slot = slots[existing];
        if (slot.active and protocol.sameIdentity(
            target_key,
            record_key,
            slot.target_key,
            slot.record_key,
        )) return false;
        probe = (probe + 1) & mask;
    }
    return false;
}

fn hashIdentity(target_key: u64, record_key: u64) usize {
    var value = target_key ^ std.math.rotl(u64, record_key, 31);
    value *%= 0x9E37_79B9_7F4A_7C15;
    value ^= value >> 33;
    return @truncate(value);
}

fn indexCapacity(item_capacity: usize) !usize {
    const doubled = try std.math.mul(usize, @max(item_capacity, 1), 2);
    return std.math.ceilPowerOfTwo(usize, doubled);
}

pub fn lockMutex(mutex: *std.atomic.Mutex) void {
    while (!mutex.tryLock()) std.atomic.spinLoopHint();
}
