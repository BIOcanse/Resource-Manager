const std = @import("std");
const protocol = @import("protocol.zig");

pub const IndexState = enum(u8) {
    empty = 0,
    occupied = 1,
    tombstone = 2,
};

pub const IndexSlot = struct {
    state: IndexState = .empty,
    key: protocol.Handle128 = .{ .high = 0, .low = 0 },
    record_index: u32 = protocol.no_slot,
};

pub const Record = struct {
    occupied: bool = false,
    state: protocol.OperationState = .queued,
    operation_id: protocol.Handle128 = .{ .high = 0, .low = 0 },
    domain_id: protocol.Handle128 = .{ .high = 0, .low = 0 },
    kind_handle: protocol.Handle128 = .{ .high = 0, .low = 0 },
    title_handle: protocol.Handle128 = .{ .high = 0, .low = 0 },
    request_handle: protocol.Handle128 = .{ .high = 0, .low = 0 },
    result_handle: protocol.Handle128 = .{ .high = 0, .low = 0 },
    error_handle: protocol.Handle128 = .{ .high = 0, .low = 0 },
    attempt_token: protocol.Handle128 = .{ .high = 0, .low = 0 },
    priority: i64 = 0,
    submit_sequence: u64 = 0,
    state_revision: u64 = 0,
    created_utc_milliseconds: i64 = 0,
    created_monotonic_milliseconds: u64 = 0,
    updated_utc_milliseconds: i64 = 0,
    updated_monotonic_milliseconds: u64 = 0,
    started_monotonic_milliseconds: u64 = 0,
    retry_at_monotonic_milliseconds: u64 = 0,
    terminal_at_monotonic_milliseconds: u64 = 0,
    terminal_retire_at_monotonic_milliseconds: u64 = 0,
    maximum_attempts: u32 = 0,
    attempt_number: u32 = 0,
    retry_delay_milliseconds: u32 = 0,
    recovery_origin_state: u32 = 0,
    execution_timeout_milliseconds: u64 = 0,
    cancel_grace_milliseconds: u64 = 0,
    terminal_retention_milliseconds: u64 = 0,
    progress_sequence: u64 = 0,
    progress_valid_mask: u64 = 0,
    percent_milli: u32 = 0,
    bytes_done: u64 = 0,
    bytes_total: u64 = 0,
    speed_bytes_per_second: u64 = 0,
    stage_handle: protocol.Handle128 = .{ .high = 0, .low = 0 },
    message_handle: protocol.Handle128 = .{ .high = 0, .low = 0 },
    checkpoint_handle: protocol.Handle128 = .{ .high = 0, .low = 0 },
    flags: u64 = 0,
    attempt_configuration_generation: u64 = 0,
    queue_order: u64 = 0,
    session_local: bool = false,
    pending_action_id: u64 = 0,
    pending_action_kind: u32 = 0,
    pending_plan_epoch: u64 = 0,
    pending_configuration_generation: u64 = 0,
    pending_action_token: protocol.Handle128 = .{ .high = 0, .low = 0 },
    pending_deadline_monotonic_milliseconds: u64 = 0,
    last_cancel_action_id: u64 = 0,

    pub fn toOutput(self: *const Record) protocol.OperationOutput {
        return .{
            .struct_size = @sizeOf(protocol.OperationOutput),
            .state = @intFromEnum(self.state),
            .operation_id = self.operation_id,
            .domain_id = self.domain_id,
            .kind_handle = self.kind_handle,
            .title_handle = self.title_handle,
            .request_handle = self.request_handle,
            .result_handle = self.result_handle,
            .error_handle = self.error_handle,
            .attempt_token = self.attempt_token,
            .priority = self.priority,
            .submit_sequence = self.submit_sequence,
            .state_revision = self.state_revision,
            .created_utc_milliseconds = self.created_utc_milliseconds,
            .created_monotonic_milliseconds = self.created_monotonic_milliseconds,
            .updated_utc_milliseconds = self.updated_utc_milliseconds,
            .updated_monotonic_milliseconds = self.updated_monotonic_milliseconds,
            .started_monotonic_milliseconds = self.started_monotonic_milliseconds,
            .retry_at_monotonic_milliseconds = self.retry_at_monotonic_milliseconds,
            .terminal_at_monotonic_milliseconds = self.terminal_at_monotonic_milliseconds,
            .terminal_retire_at_monotonic_milliseconds = self.terminal_retire_at_monotonic_milliseconds,
            .maximum_attempts = self.maximum_attempts,
            .attempt_number = self.attempt_number,
            .retry_delay_milliseconds = self.retry_delay_milliseconds,
            .recovery_origin_state = self.recovery_origin_state,
            .execution_timeout_milliseconds = self.execution_timeout_milliseconds,
            .cancel_grace_milliseconds = self.cancel_grace_milliseconds,
            .terminal_retention_milliseconds = self.terminal_retention_milliseconds,
            .progress_sequence = self.progress_sequence,
            .progress_valid_mask = self.progress_valid_mask,
            .percent_milli = self.percent_milli,
            .reserved_u32 = 0,
            .bytes_done = self.bytes_done,
            .bytes_total = self.bytes_total,
            .speed_bytes_per_second = self.speed_bytes_per_second,
            .stage_handle = self.stage_handle,
            .message_handle = self.message_handle,
            .checkpoint_handle = self.checkpoint_handle,
            .flags = self.flags,
            .attempt_configuration_generation = self.attempt_configuration_generation,
            .queue_order = self.queue_order,
            .reserved = [_]u64{0} ** 2,
        };
    }
};

pub const Bank = struct {
    allocator: std.mem.Allocator,
    records: []Record,
    operation_index: []IndexSlot,
    domain_index: []IndexSlot,
    free_stack: []u32,
    order_scratch: []u32,
    actions: []protocol.ActionOutput,
    operation_count: u32 = 0,
    domain_count: u32 = 0,
    free_count: u32 = 0,
    action_count: u32 = 0,

    pub fn create(allocator: std.mem.Allocator, config: *const protocol.Config) !Bank {
        const records = try allocator.alloc(Record, config.maximum_operation_count);
        errdefer allocator.free(records);
        const operation_index = try allocator.alloc(IndexSlot, config.operation_index_capacity);
        errdefer allocator.free(operation_index);
        const domain_index = try allocator.alloc(IndexSlot, config.domain_index_capacity);
        errdefer allocator.free(domain_index);
        const free_stack = try allocator.alloc(u32, config.maximum_operation_count);
        errdefer allocator.free(free_stack);
        const order_scratch = try allocator.alloc(u32, config.maximum_operation_count);
        errdefer allocator.free(order_scratch);
        const actions = try allocator.alloc(protocol.ActionOutput, config.maximum_action_count);
        errdefer allocator.free(actions);

        @memset(records, .{});
        @memset(operation_index, .{});
        @memset(domain_index, .{});
        @memset(actions, std.mem.zeroes(protocol.ActionOutput));
        for (free_stack, 0..) |*slot, index| {
            slot.* = @intCast(free_stack.len - index - 1);
        }
        @memset(order_scratch, protocol.no_slot);

        return .{
            .allocator = allocator,
            .records = records,
            .operation_index = operation_index,
            .domain_index = domain_index,
            .free_stack = free_stack,
            .order_scratch = order_scratch,
            .actions = actions,
            .free_count = @intCast(free_stack.len),
        };
    }

    pub fn destroy(self: *Bank) void {
        self.allocator.free(self.actions);
        self.allocator.free(self.order_scratch);
        self.allocator.free(self.free_stack);
        self.allocator.free(self.domain_index);
        self.allocator.free(self.operation_index);
        self.allocator.free(self.records);
        self.* = undefined;
    }

    pub fn reset(self: *Bank) void {
        @memset(self.records, .{});
        @memset(self.operation_index, .{});
        @memset(self.domain_index, .{});
        @memset(self.actions, std.mem.zeroes(protocol.ActionOutput));
        for (self.free_stack, 0..) |*slot, index| {
            slot.* = @intCast(self.free_stack.len - index - 1);
        }
        @memset(self.order_scratch, protocol.no_slot);
        self.operation_count = 0;
        self.domain_count = 0;
        self.free_count = @intCast(self.free_stack.len);
        self.action_count = 0;
    }

    pub fn allocateRecord(self: *Bank) ?u32 {
        if (self.free_count == 0) return null;
        self.free_count -= 1;
        const index = self.free_stack[self.free_count];
        self.records[index] = .{ .occupied = true };
        self.operation_count += 1;
        return index;
    }

    pub fn releaseRecord(self: *Bank, index: u32) void {
        const record = &self.records[index];
        if (!record.occupied) return;
        _ = removeIndex(self.operation_index, record.operation_id);
        if ((record.flags & protocol.OperationFlags.domain_valid) != 0) {
            self.removeDomain(record.domain_id);
        }
        record.* = .{};
        self.free_stack[self.free_count] = index;
        self.free_count += 1;
        self.operation_count -= 1;
    }

    pub fn findOperation(self: *const Bank, key: protocol.Handle128) ?u32 {
        return findIndex(self.operation_index, key);
    }

    pub fn findDomain(self: *const Bank, key: protocol.Handle128) ?u32 {
        return findIndex(self.domain_index, key);
    }

    pub fn insertOperation(self: *Bank, key: protocol.Handle128, record_index: u32) bool {
        return insertIndex(self.operation_index, key, record_index);
    }

    pub fn insertDomain(self: *Bank, key: protocol.Handle128, record_index: u32) bool {
        if (!insertIndex(self.domain_index, key, record_index)) return false;
        self.domain_count += 1;
        return true;
    }

    pub fn removeDomain(self: *Bank, key: protocol.Handle128) void {
        if (removeIndex(self.domain_index, key)) self.domain_count -= 1;
    }

    pub fn clearActions(self: *Bank) void {
        if (self.action_count != 0) {
            @memset(
                self.actions[0..self.action_count],
                std.mem.zeroes(protocol.ActionOutput),
            );
        }
        self.action_count = 0;
    }

    pub fn appendAction(self: *Bank, action: protocol.ActionOutput) bool {
        if (self.action_count >= self.actions.len) return false;
        self.actions[self.action_count] = action;
        self.action_count += 1;
        return true;
    }
};

pub fn committedByteCount(config: *const protocol.Config, page_size: u64) !u64 {
    if (page_size == 0) return error.Overflow;
    var total: u64 = 0;
    total = try addCommittedProduct(
        total,
        config.maximum_operation_count,
        @sizeOf(Record),
        page_size,
    );
    total = try addCommittedProduct(
        total,
        config.operation_index_capacity,
        @sizeOf(IndexSlot),
        page_size,
    );
    total = try addCommittedProduct(
        total,
        config.domain_index_capacity,
        @sizeOf(IndexSlot),
        page_size,
    );
    total = try addCommittedProduct(
        total,
        config.maximum_operation_count,
        @sizeOf(u32),
        page_size,
    );
    total = try addCommittedProduct(
        total,
        config.maximum_operation_count,
        @sizeOf(u32),
        page_size,
    );
    total = try addCommittedProduct(
        total,
        config.maximum_action_count,
        @sizeOf(protocol.ActionOutput),
        page_size,
    );
    return total;
}

fn findIndex(index: []const IndexSlot, key: protocol.Handle128) ?u32 {
    const mask = index.len - 1;
    var probe = hashHandle(key) & mask;
    var remaining = index.len;
    while (remaining != 0) : (remaining -= 1) {
        const slot = index[probe];
        switch (slot.state) {
            .empty => return null,
            .occupied => if (protocol.equalHandle(slot.key, key)) return slot.record_index,
            .tombstone => {},
        }
        probe = (probe + 1) & mask;
    }
    return null;
}

fn insertIndex(index: []IndexSlot, key: protocol.Handle128, record_index: u32) bool {
    const mask = index.len - 1;
    var probe = hashHandle(key) & mask;
    var first_tombstone: ?usize = null;
    var remaining = index.len;
    while (remaining != 0) : (remaining -= 1) {
        const slot = &index[probe];
        switch (slot.state) {
            .empty => {
                const target = if (first_tombstone) |value| &index[value] else slot;
                target.* = .{
                    .state = .occupied,
                    .key = key,
                    .record_index = record_index,
                };
                return true;
            },
            .occupied => if (protocol.equalHandle(slot.key, key)) return false,
            .tombstone => if (first_tombstone == null) {
                first_tombstone = probe;
            },
        }
        probe = (probe + 1) & mask;
    }
    if (first_tombstone) |value| {
        index[value] = .{
            .state = .occupied,
            .key = key,
            .record_index = record_index,
        };
        return true;
    }
    return false;
}

fn removeIndex(index: []IndexSlot, key: protocol.Handle128) bool {
    const mask = index.len - 1;
    var probe = hashHandle(key) & mask;
    var remaining = index.len;
    while (remaining != 0) : (remaining -= 1) {
        switch (index[probe].state) {
            .empty => return false,
            .occupied => if (protocol.equalHandle(index[probe].key, key)) {
                backwardShiftDelete(index, probe);
                return true;
            },
            .tombstone => {},
        }
        probe = (probe + 1) & mask;
    }
    return false;
}

fn backwardShiftDelete(index: []IndexSlot, deleted_index: usize) void {
    const mask = index.len - 1;
    var hole = deleted_index;
    index[hole] = .{};
    var scan = (hole + 1) & mask;
    while (index[scan].state != .empty) : (scan = (scan + 1) & mask) {
        if (index[scan].state == .tombstone) continue;
        const home = hashHandle(index[scan].key) & mask;
        if (probeDistance(home, hole, mask) >= probeDistance(home, scan, mask)) continue;
        index[hole] = index[scan];
        index[scan] = .{};
        hole = scan;
    }
}

fn probeDistance(home: usize, position: usize, mask: usize) usize {
    return (position -% home) & mask;
}

fn hashHandle(handle: protocol.Handle128) usize {
    var value = handle.high ^ std.math.rotl(u64, handle.low, 29);
    value ^= value >> 33;
    value *%= 0xff51afd7ed558ccd;
    value ^= value >> 33;
    value *%= 0xc4ceb9fe1a85ec53;
    value ^= value >> 33;
    return @truncate(value);
}

fn addCommittedProduct(
    current: u64,
    count: u32,
    item_size: usize,
    page_size: u64,
) !u64 {
    const bytes = try std.math.mul(u64, count, item_size);
    const padded = try std.math.add(u64, bytes, page_size - 1);
    const committed = (padded / page_size) * page_size;
    return try std.math.add(u64, current, committed);
}

test "operation index deletion compacts probes without historical tombstones" {
    var index = [_]IndexSlot{.{}} ** 64;
    var ordinal: u64 = 1;
    while (ordinal <= 4_096) : (ordinal += 1) {
        const key = protocol.Handle128{
            .high = ordinal,
            .low = ordinal *% 0x9e37_79b9_7f4a_7c15,
        };
        try std.testing.expect(insertIndex(&index, key, 7));
        try std.testing.expectEqual(@as(?u32, 7), findIndex(&index, key));
        try std.testing.expect(removeIndex(&index, key));
        try std.testing.expectEqual(@as(?u32, null), findIndex(&index, key));
    }

    for (index) |slot| try std.testing.expectEqual(IndexState.empty, slot.state);
}
