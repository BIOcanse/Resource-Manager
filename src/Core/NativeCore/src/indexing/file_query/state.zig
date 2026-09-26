const std = @import("std");
const protocol = @import("protocol.zig");
const ResultCode = @import("../../common/result_codes.zig").ResultCode;

pub const no_index = std.math.maxInt(u32);

pub const CandidateSlot = struct {
    occupied: bool = false,
    entry_handle: u64 = 0,
    candidate_ordinal: u32 = 0,
    source_mask: u32 = 0,
    file_name_offset: u32 = 0,
    file_name_length: u32 = 0,
    relative_path_offset: u32 = 0,
    relative_path_length: u32 = 0,
    software_name_offset: u32 = 0,
    software_name_length: u32 = 0,
    file_name_rune_count: u32 = 0,
    matched_priority: u32 = 0,
};

const OrdinalSlot = struct {
    occupied: bool = false,
    ordinal: u32 = 0,
    candidate_index: u32 = no_index,
    source_id: u32 = 0,
};

pub const Session = struct {
    allocator: std.mem.Allocator,
    mutex: std.atomic.Mutex = .unlocked,
    config: protocol.Config,
    resident_byte_count: u64,
    query_bytes: []u8,
    plan_bytes: []u8,
    source_plans: []protocol.SourcePlanOutput,
    candidates: []CandidateSlot,
    candidate_text_bytes: []u8,
    entry_index: []u32,
    ordinals: []OrdinalSlot,
    ordinal_index: []u32,
    rank_order: []u32,
    phase: protocol.QueryPhase = .empty,
    state_revision: u64 = 1,
    last_operation_epoch: u64 = 0,
    last_query_epoch: u64 = 0,
    last_batch_epoch: u64 = 0,
    query_byte_count: u32 = 0,
    query_rune_count: u32 = 0,
    plan_byte_count: u32 = 0,
    source_plan_count: u32 = 0,
    requested_result_count: u32 = 0,
    candidate_limit_per_source: u32 = 0,
    maximum_total_candidate_count: u32 = 0,
    submitted_candidate_count: u32 = 0,
    submitted_per_source: [4]u32 = .{ 0, 0, 0, 0 },
    ordinal_count: u32 = 0,
    unique_candidate_count: u32 = 0,
    candidate_text_byte_count: u32 = 0,
    matched_candidate_count: u32 = 0,
    result_count: u32 = 0,

    pub fn create(config: *const protocol.Config) !*Session {
        if (!protocol.validConfig(config)) return error.InvalidConfiguration;
        const resident = residentByteCount(config) catch return error.InvalidConfiguration;
        if (resident > config.resident_byte_budget) return error.InvalidConfiguration;

        const allocator = std.heap.page_allocator;
        const self = try allocator.create(Session);
        errdefer allocator.destroy(self);
        const query_bytes = try allocator.alloc(u8, config.maximum_query_utf8_byte_count);
        errdefer allocator.free(query_bytes);
        const plan_bytes = try allocator.alloc(u8, config.maximum_plan_utf8_byte_count);
        errdefer allocator.free(plan_bytes);
        const source_plans = try allocator.alloc(
            protocol.SourcePlanOutput,
            config.maximum_source_plan_count,
        );
        errdefer allocator.free(source_plans);
        const candidates = try allocator.alloc(
            CandidateSlot,
            config.maximum_unique_candidate_count,
        );
        errdefer allocator.free(candidates);
        const candidate_text_bytes = try allocator.alloc(
            u8,
            config.candidate_text_arena_byte_count,
        );
        errdefer allocator.free(candidate_text_bytes);
        const entry_index = try allocator.alloc(u32, config.entry_index_capacity);
        errdefer allocator.free(entry_index);
        const ordinals = try allocator.alloc(
            OrdinalSlot,
            config.maximum_submitted_candidate_count,
        );
        errdefer allocator.free(ordinals);
        const ordinal_index = try allocator.alloc(u32, config.ordinal_index_capacity);
        errdefer allocator.free(ordinal_index);
        const rank_order = try allocator.alloc(u32, config.maximum_unique_candidate_count);
        errdefer allocator.free(rank_order);

        @memset(query_bytes, 0);
        @memset(plan_bytes, 0);
        @memset(source_plans, std.mem.zeroes(protocol.SourcePlanOutput));
        @memset(candidates, .{});
        @memset(candidate_text_bytes, 0);
        @memset(entry_index, no_index);
        @memset(ordinals, .{});
        @memset(ordinal_index, no_index);
        @memset(rank_order, no_index);

        self.* = .{
            .allocator = allocator,
            .config = config.*,
            .resident_byte_count = resident,
            .query_bytes = query_bytes,
            .plan_bytes = plan_bytes,
            .source_plans = source_plans,
            .candidates = candidates,
            .candidate_text_bytes = candidate_text_bytes,
            .entry_index = entry_index,
            .ordinals = ordinals,
            .ordinal_index = ordinal_index,
            .rank_order = rank_order,
        };
        return self;
    }

    pub fn destroy(self: *Session) void {
        const allocator = self.allocator;
        allocator.free(self.rank_order);
        allocator.free(self.ordinal_index);
        allocator.free(self.ordinals);
        allocator.free(self.entry_index);
        allocator.free(self.candidate_text_bytes);
        allocator.free(self.candidates);
        allocator.free(self.source_plans);
        allocator.free(self.plan_bytes);
        allocator.free(self.query_bytes);
        allocator.destroy(self);
    }

    pub fn reconfigure(self: *Session, config: *const protocol.Config) ResultCode {
        lockMutex(&self.mutex);
        defer self.mutex.unlock();
        if (!protocol.validConfig(config)) return .abi_mismatch;
        if (config.generation <= self.config.generation) return .stale_frame;
        if (self.phase != .empty or !sameShape(config, &self.config)) {
            return .invalid_argument;
        }
        if (self.resident_byte_count > config.resident_byte_budget) return .invalid_argument;
        self.config = config.*;
        self.advanceRevision();
        return .ok;
    }

    pub fn capacity(self: *Session) protocol.Capacity {
        lockMutex(&self.mutex);
        defer self.mutex.unlock();
        return .{
            .struct_size = @sizeOf(protocol.Capacity),
            .query_utf8_byte_capacity = self.config.maximum_query_utf8_byte_count,
            .query_rune_capacity = self.config.maximum_query_rune_count,
            .plan_utf8_byte_capacity = self.config.maximum_plan_utf8_byte_count,
            .source_plan_capacity = self.config.maximum_source_plan_count,
            .candidate_capacity_per_source = self.config.maximum_candidate_count_per_source,
            .submitted_candidate_capacity = self.config.maximum_submitted_candidate_count,
            .unique_candidate_capacity = self.config.maximum_unique_candidate_count,
            .candidate_submit_batch_capacity = self.config.maximum_candidate_submit_batch_count,
            .candidate_submit_utf8_byte_capacity = self.config.maximum_candidate_submit_utf8_byte_count,
            .candidate_text_arena_byte_capacity = self.config.candidate_text_arena_byte_count,
            .file_name_utf8_byte_capacity = self.config.maximum_file_name_utf8_byte_count,
            .result_capacity = self.config.maximum_result_count,
            .entry_index_capacity = self.config.entry_index_capacity,
            .ordinal_index_capacity = self.config.ordinal_index_capacity,
            .reserved_u32 = 0,
            .resident_byte_count = self.resident_byte_count,
            .reserved = .{ 0, 0, 0, 0 },
        };
    }

    pub fn reset(self: *Session, input: *const protocol.ResetInput) ResultCode {
        lockMutex(&self.mutex);
        defer self.mutex.unlock();
        if (!protocol.validReset(input, &self.config)) return .abi_mismatch;
        if (input.operation_epoch <= self.last_operation_epoch) return .stale_frame;
        self.clearQueryState();
        self.last_operation_epoch = input.operation_epoch;
        self.advanceRevision();
        return .ok;
    }

    pub fn snapshot(self: *Session, output: *protocol.SnapshotOutput) ResultCode {
        lockMutex(&self.mutex);
        defer self.mutex.unlock();
        if (!protocol.emptySnapshot(output)) return .abi_mismatch;
        output.* = .{
            .abi_version = protocol.abi_version,
            .struct_size = @sizeOf(protocol.SnapshotOutput),
            .configuration_generation = self.config.generation,
            .state_revision = self.state_revision,
            .last_operation_epoch = self.last_operation_epoch,
            .last_query_epoch = self.last_query_epoch,
            .last_batch_epoch = self.last_batch_epoch,
            .phase = @intFromEnum(self.phase),
            .source_plan_count = self.source_plan_count,
            .submitted_candidate_count = self.submitted_candidate_count,
            .unique_candidate_count = self.unique_candidate_count,
            .matched_candidate_count = self.matched_candidate_count,
            .result_count = self.result_count,
            .query_byte_count = self.query_byte_count,
            .plan_byte_count = self.plan_byte_count,
            .candidate_text_byte_count = self.candidate_text_byte_count,
            .resident_byte_count = self.resident_byte_count,
            .flags = 0,
            .reserved = .{ 0, 0, 0 },
        };
        return .ok;
    }

    pub fn query(self: *const Session) []const u8 {
        return self.query_bytes[0..self.query_byte_count];
    }

    pub fn candidateFileName(self: *const Session, candidate: *const CandidateSlot) []const u8 {
        return self.candidate_text_bytes[candidate.file_name_offset .. candidate.file_name_offset + candidate.file_name_length];
    }

    pub fn candidateRelativePath(self: *const Session, candidate: *const CandidateSlot) []const u8 {
        return self.candidate_text_bytes[candidate.relative_path_offset .. candidate.relative_path_offset + candidate.relative_path_length];
    }

    pub fn candidateSoftwareName(self: *const Session, candidate: *const CandidateSlot) []const u8 {
        return self.candidate_text_bytes[candidate.software_name_offset .. candidate.software_name_offset + candidate.software_name_length];
    }

    pub fn sourcePriority(self: *const Session, source: protocol.SourceId) u32 {
        return switch (source) {
            .file_name => self.config.file_name_priority,
            .relative_path => self.config.relative_path_priority,
            .software_name => self.config.software_name_priority,
            .short_scan => 0,
        };
    }

    pub fn findEntry(self: *const Session, entry_handle: u64) ?u32 {
        const mask = self.entry_index.len - 1;
        var probe = hashU64(entry_handle) & mask;
        var remaining = self.entry_index.len;
        while (remaining != 0) : (remaining -= 1) {
            const index = self.entry_index[probe];
            if (index == no_index) return null;
            if (self.candidates[index].occupied and
                self.candidates[index].entry_handle == entry_handle)
            {
                return index;
            }
            probe = (probe + 1) & mask;
        }
        return null;
    }

    pub fn findOrdinal(self: *const Session, ordinal: u32) ?u32 {
        const mask = self.ordinal_index.len - 1;
        var probe = hashU64(ordinal) & mask;
        var remaining = self.ordinal_index.len;
        while (remaining != 0) : (remaining -= 1) {
            const index = self.ordinal_index[probe];
            if (index == no_index) return null;
            if (self.ordinals[index].occupied and self.ordinals[index].ordinal == ordinal) {
                return self.ordinals[index].candidate_index;
            }
            probe = (probe + 1) & mask;
        }
        return null;
    }

    pub fn insertEntryIndex(self: *Session, candidate_index: u32) bool {
        const entry_handle = self.candidates[candidate_index].entry_handle;
        const mask = self.entry_index.len - 1;
        var probe = hashU64(entry_handle) & mask;
        var remaining = self.entry_index.len;
        while (remaining != 0) : (remaining -= 1) {
            if (self.entry_index[probe] == no_index) {
                self.entry_index[probe] = candidate_index;
                return true;
            }
            probe = (probe + 1) & mask;
        }
        return false;
    }

    pub fn insertOrdinalIndex(
        self: *Session,
        ordinal: u32,
        candidate_index: u32,
        source_id: protocol.SourceId,
    ) bool {
        if (self.ordinal_count >= self.ordinals.len) return false;
        const ordinal_slot_index = self.ordinal_count;
        const mask = self.ordinal_index.len - 1;
        var probe = hashU64(ordinal) & mask;
        var remaining = self.ordinal_index.len;
        while (remaining != 0) : (remaining -= 1) {
            if (self.ordinal_index[probe] == no_index) {
                self.ordinals[ordinal_slot_index] = .{
                    .occupied = true,
                    .ordinal = ordinal,
                    .candidate_index = candidate_index,
                    .source_id = @intFromEnum(source_id),
                };
                self.ordinal_index[probe] = ordinal_slot_index;
                self.ordinal_count += 1;
                return true;
            }
            probe = (probe + 1) & mask;
        }
        return false;
    }

    pub fn clearQueryState(self: *Session) void {
        @memset(self.query_bytes[0..self.query_byte_count], 0);
        @memset(self.plan_bytes[0..self.plan_byte_count], 0);
        @memset(
            self.source_plans[0..self.source_plan_count],
            std.mem.zeroes(protocol.SourcePlanOutput),
        );
        @memset(self.candidates[0..self.unique_candidate_count], .{});
        @memset(self.candidate_text_bytes[0..self.candidate_text_byte_count], 0);
        @memset(self.entry_index, no_index);
        @memset(self.ordinals[0..self.ordinal_count], .{});
        @memset(self.ordinal_index, no_index);
        @memset(self.rank_order[0..self.unique_candidate_count], no_index);
        self.phase = .empty;
        self.query_byte_count = 0;
        self.query_rune_count = 0;
        self.plan_byte_count = 0;
        self.source_plan_count = 0;
        self.requested_result_count = 0;
        self.candidate_limit_per_source = 0;
        self.maximum_total_candidate_count = 0;
        self.submitted_candidate_count = 0;
        self.submitted_per_source = .{ 0, 0, 0, 0 };
        self.ordinal_count = 0;
        self.unique_candidate_count = 0;
        self.candidate_text_byte_count = 0;
        self.matched_candidate_count = 0;
        self.result_count = 0;
        self.last_batch_epoch = 0;
    }

    pub fn advanceRevision(self: *Session) void {
        self.state_revision = std.math.add(u64, self.state_revision, 1) catch
            std.math.maxInt(u64);
    }
};

pub fn lockMutex(mutex: *std.atomic.Mutex) void {
    while (!mutex.tryLock()) std.atomic.spinLoopHint();
}

pub fn runeCount(value: []const u8) ?u32 {
    if (!std.unicode.utf8ValidateSlice(value)) return null;
    var count: u32 = 0;
    var index: usize = 0;
    while (index < value.len) {
        index += utf8SequenceLength(value[index]) orelse return null;
        count = std.math.add(u32, count, 1) catch return null;
    }
    return count;
}

pub fn utf8SequenceLength(first: u8) ?u3 {
    if (first < 0x80) return 1;
    if ((first & 0xe0) == 0xc0) return 2;
    if ((first & 0xf0) == 0xe0) return 3;
    if ((first & 0xf8) == 0xf0) return 4;
    return null;
}

fn hashU64(value: u64) usize {
    var mixed = value;
    mixed ^= mixed >> 30;
    mixed *%= 0xbf58_476d_1ce4_e5b9;
    mixed ^= mixed >> 27;
    mixed *%= 0x94d0_49bb_1331_11eb;
    mixed ^= mixed >> 31;
    return @intCast(mixed);
}

fn sameShape(left: *const protocol.Config, right: *const protocol.Config) bool {
    return left.maximum_query_utf8_byte_count == right.maximum_query_utf8_byte_count and
        left.maximum_query_rune_count == right.maximum_query_rune_count and
        left.maximum_plan_utf8_byte_count == right.maximum_plan_utf8_byte_count and
        left.maximum_source_plan_count == right.maximum_source_plan_count and
        left.maximum_candidate_count_per_source == right.maximum_candidate_count_per_source and
        left.maximum_submitted_candidate_count == right.maximum_submitted_candidate_count and
        left.maximum_unique_candidate_count == right.maximum_unique_candidate_count and
        left.maximum_candidate_submit_batch_count == right.maximum_candidate_submit_batch_count and
        left.maximum_candidate_submit_utf8_byte_count ==
            right.maximum_candidate_submit_utf8_byte_count and
        left.candidate_text_arena_byte_count == right.candidate_text_arena_byte_count and
        left.maximum_file_name_utf8_byte_count == right.maximum_file_name_utf8_byte_count and
        left.maximum_result_count == right.maximum_result_count and
        left.entry_index_capacity == right.entry_index_capacity and
        left.ordinal_index_capacity == right.ordinal_index_capacity;
}

fn residentByteCount(config: *const protocol.Config) !u64 {
    var total: u64 = @sizeOf(Session);
    total = try addSliceBytes(total, u8, config.maximum_query_utf8_byte_count);
    total = try addSliceBytes(total, u8, config.maximum_plan_utf8_byte_count);
    total = try addSliceBytes(total, protocol.SourcePlanOutput, config.maximum_source_plan_count);
    total = try addSliceBytes(total, CandidateSlot, config.maximum_unique_candidate_count);
    total = try addSliceBytes(total, u8, config.candidate_text_arena_byte_count);
    total = try addSliceBytes(total, u32, config.entry_index_capacity);
    total = try addSliceBytes(total, OrdinalSlot, config.maximum_submitted_candidate_count);
    total = try addSliceBytes(total, u32, config.ordinal_index_capacity);
    total = try addSliceBytes(total, u32, config.maximum_unique_candidate_count);
    return total;
}

fn addSliceBytes(total: u64, comptime T: type, count: u32) !u64 {
    const bytes = try std.math.mul(u64, @sizeOf(T), count);
    return try std.math.add(u64, total, bytes);
}
