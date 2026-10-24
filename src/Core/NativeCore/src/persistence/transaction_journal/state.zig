const growth = @import("../../common/workspace_growth.zig");
const std = @import("std");
const protocol = @import("protocol.zig");

pub const no_index: u32 = std.math.maxInt(u32);
pub const initial_record_capacity: u32 = 16;

pub const Session = struct {
    growth: growth.Log = .{},
    allocator: std.mem.Allocator,
    mutex: std.atomic.Mutex = .unlocked,
    config: protocol.CreateConfig,
    records: []protocol.Record,
    staging_records: []protocol.Record,
    index: []u32,
    sort_indices: []u32,
    count: u32 = 0,
    journal_revision: u64 = 1,

    pub fn create(config: *const protocol.CreateConfig) !*Session {
        return createWithAllocator(config, std.heap.page_allocator);
    }

    pub fn createWithAllocator(
        config: *const protocol.CreateConfig,
        allocator: std.mem.Allocator,
    ) !*Session {
        if (!protocol.validCreateConfig(config)) return error.InvalidConfiguration;
        const resident_bytes = try requiredResidentBytes(config.record_capacity);
        if (resident_bytes > config.maximum_resident_bytes) return error.ResidentBudgetExceeded;
        const self = try allocator.create(Session);
        errdefer allocator.destroy(self);

        const record_capacity: usize = @min(initial_record_capacity, config.record_capacity);
        const index_capacity = try indexCapacity(record_capacity);
        const records = try allocator.alloc(protocol.Record, record_capacity);
        errdefer allocator.free(records);
        @memset(records, std.mem.zeroes(protocol.Record));
        const staging_records = try allocator.alloc(protocol.Record, record_capacity);
        errdefer allocator.free(staging_records);
        @memset(staging_records, std.mem.zeroes(protocol.Record));
        const index = try allocator.alloc(u32, index_capacity);
        errdefer allocator.free(index);
        @memset(index, no_index);
        const sort_indices = try allocator.alloc(u32, record_capacity);
        errdefer allocator.free(sort_indices);
        @memset(sort_indices, 0);

        self.* = .{
            .allocator = allocator,
            .config = config.*,
            .records = records,
            .staging_records = staging_records,
            .index = index,
            .sort_indices = sort_indices,
        };
        return self;
    }

    pub fn destroy(self: *Session) void {
        const allocator = self.allocator;
        allocator.free(self.sort_indices);
        allocator.free(self.index);
        allocator.free(self.staging_records);
        allocator.free(self.records);
        allocator.destroy(self);
    }

    pub fn capacity(self: *const Session) protocol.Capacity {
        return .{
            .struct_size = @sizeOf(protocol.Capacity),
            .record_capacity = self.config.record_capacity,
            .maximum_image_length = @sizeOf(protocol.ImageHeader) +
                @as(u64, self.config.record_capacity) * @sizeOf(protocol.Record),
            .resident_bytes = requiredResidentBytes(self.config.record_capacity) catch unreachable,
            .reserved = .{0},
        };
    }

    pub fn workspaceCapacity(self: *Session) protocol.WorkspaceCapacity {
        lockMutex(&self.mutex);
        defer self.mutex.unlock();
        return .{
            .struct_size = @sizeOf(protocol.WorkspaceCapacity),
            .growth = self.growth,
            .record_capacity = @intCast(self.records.len),
            .staging_record_capacity = @intCast(self.staging_records.len),
            .index_capacity = @intCast(self.index.len),
            .sort_capacity = @intCast(self.sort_indices.len),
            .resident_bytes = requiredResidentBytes(@intCast(self.records.len)) catch unreachable,
        };
    }

    // The caller owns the Session mutex. Allocate a complete replacement before
    // publishing any pointer, so allocation failure leaves the old bank intact.
    pub fn ensureRecordCapacityUnlocked(self: *Session, required: u32, reason: enum { prepare, prepare_batch, decode }) protocol.Status {
        if (required <= self.records.len) return .ok;
        if (required > self.config.record_capacity) return .capacity_full;
        var new_capacity: u32 = @intCast(self.records.len);
        while (new_capacity < required) {
            new_capacity = @intCast(@min(
                std.math.mul(u64, new_capacity, 2) catch return .capacity_full,
                @as(u64, self.config.record_capacity),
            ));
        }
        const resident_bytes = requiredResidentBytes(new_capacity) catch return .capacity_full;
        if (resident_bytes > self.config.maximum_resident_bytes) return .capacity_full;
        const old_capacity = self.records.len;
        self.growUnlocked(new_capacity) catch return .capacity_full;
        self.growth.record(switch (reason) {
            .prepare => .journal_prepare,
            .prepare_batch => .journal_batch,
            .decode => .journal_decode,
        }, .rows, old_capacity, new_capacity);
        return .ok;
    }

    fn growUnlocked(self: *Session, new_capacity: u32) !void {
        const records = try self.allocator.alloc(protocol.Record, new_capacity);
        errdefer self.allocator.free(records);
        @memset(records, std.mem.zeroes(protocol.Record));
        const staging_records = try self.allocator.alloc(protocol.Record, new_capacity);
        errdefer self.allocator.free(staging_records);
        @memset(staging_records, std.mem.zeroes(protocol.Record));
        const index = try self.allocator.alloc(u32, try indexCapacity(new_capacity));
        errdefer self.allocator.free(index);
        const sort_indices = try self.allocator.alloc(u32, new_capacity);
        errdefer self.allocator.free(sort_indices);
        @memset(sort_indices, 0);
        std.mem.copyForwards(protocol.Record, records[0..self.count], self.records[0..self.count]);
        std.mem.copyForwards(protocol.Record, staging_records[0..self.staging_records.len], self.staging_records);
        std.mem.copyForwards(u32, sort_indices[0..self.sort_indices.len], self.sort_indices);
        var replacement: Session = .{
            .allocator = self.allocator,
            .config = self.config,
            .records = records,
            .staging_records = staging_records,
            .index = index,
            .sort_indices = sort_indices,
            .count = self.count,
            .journal_revision = self.journal_revision,
        };
        if (!replacement.rebuildIndex()) return error.InvalidIndex;
        const old_records = self.records;
        const old_staging_records = self.staging_records;
        const old_index = self.index;
        const old_sort_indices = self.sort_indices;
        self.records = records;
        self.staging_records = staging_records;
        self.index = index;
        self.sort_indices = sort_indices;
        self.allocator.free(old_sort_indices);
        self.allocator.free(old_index);
        self.allocator.free(old_staging_records);
        self.allocator.free(old_records);
    }

    pub fn find(self: *const Session, identity: *const protocol.Identity) ?u32 {
        const mask = self.index.len - 1;
        var probe = identityHash(identity) & mask;
        var remaining = self.index.len;
        while (remaining != 0) : (remaining -= 1) {
            const record_index = self.index[probe];
            if (record_index == no_index) return null;
            if (record_index < self.count and
                protocol.sameIdentity(&self.records[record_index].identity, identity))
            {
                return record_index;
            }
            probe = (probe + 1) & mask;
        }
        return null;
    }

    pub fn insertIndex(self: *Session, record_index: u32) bool {
        const identity = &self.records[record_index].identity;
        const mask = self.index.len - 1;
        var probe = identityHash(identity) & mask;
        var remaining = self.index.len;
        while (remaining != 0) : (remaining -= 1) {
            if (self.index[probe] == no_index) {
                self.index[probe] = record_index;
                return true;
            }
            probe = (probe + 1) & mask;
        }
        return false;
    }

    pub fn rebuildIndex(self: *Session) bool {
        @memset(self.index, no_index);
        var record_index: u32 = 0;
        while (record_index < self.count) : (record_index += 1) {
            if (!self.insertIndex(record_index)) return false;
        }
        return true;
    }

    pub fn removeAt(self: *Session, record_index: u32) bool {
        const last_index = self.count - 1;
        if (record_index != last_index) self.records[record_index] = self.records[last_index];
        self.records[last_index] = std.mem.zeroes(protocol.Record);
        self.count = last_index;
        return self.rebuildIndex();
    }

    pub fn prepareSortIndices(self: *Session) []u32 {
        var index: u32 = 0;
        while (index < self.count) : (index += 1) self.sort_indices[index] = index;
        const active = self.sort_indices[0..self.count];
        heapSortIndices(self.records, active);
        return active;
    }
};

pub fn requiredResidentBytes(record_capacity_raw: u32) !u64 {
    const record_capacity: u64 = record_capacity_raw;
    const doubled_capacity = try std.math.mul(u64, record_capacity, 2);
    var index_capacity: u64 = 1;
    while (index_capacity < doubled_capacity) {
        index_capacity = try std.math.mul(u64, index_capacity, 2);
    }

    var total: u64 = @sizeOf(Session);
    total = try std.math.add(u64, total, try std.math.mul(
        u64,
        doubled_capacity,
        @sizeOf(protocol.Record),
    ));
    total = try std.math.add(u64, total, try std.math.mul(u64, index_capacity, @sizeOf(u32)));
    total = try std.math.add(u64, total, try std.math.mul(u64, record_capacity, @sizeOf(u32)));
    return total;
}

pub fn lockMutex(mutex: *std.atomic.Mutex) void {
    while (!mutex.tryLock()) std.atomic.spinLoopHint();
}

fn indexCapacity(record_capacity: usize) !usize {
    const doubled = try std.math.mul(usize, record_capacity, 2);
    var capacity: usize = 1;
    while (capacity < doubled) capacity = try std.math.mul(usize, capacity, 2);
    return capacity;
}

fn identityHash(identity: *const protocol.Identity) usize {
    var value = identity.configuration_generation ^
        std.math.rotl(u64, identity.plan_epoch, 13) ^
        std.math.rotl(u64, identity.action_id, 29) ^
        std.math.rotl(u64, identity.host_session_incarnation, 47) ^
        std.math.rotl(u64, identity.target_id, 7) ^
        std.math.rotl(u64, identity.software_id, 19) ^
        std.math.rotl(u64, identity.process_start_key, 37) ^
        @as(u64, identity.process_id);
    value ^= value >> 33;
    value *%= 0xff51_afd7_ed55_8ccd;
    value ^= value >> 33;
    return @truncate(value);
}

fn heapSortIndices(records: []const protocol.Record, indices: []u32) void {
    if (indices.len < 2) return;
    var start = indices.len / 2;
    while (start != 0) {
        start -= 1;
        siftDown(records, indices, start, indices.len);
    }
    var end = indices.len;
    while (end > 1) {
        end -= 1;
        std.mem.swap(u32, &indices[0], &indices[end]);
        siftDown(records, indices, 0, end);
    }
}

fn siftDown(records: []const protocol.Record, indices: []u32, root_start: usize, end: usize) void {
    var root = root_start;
    while (true) {
        const left = root * 2 + 1;
        if (left >= end) return;
        var child = left;
        const right = left + 1;
        if (right < end and recordIndexLess(records, indices[left], indices[right])) child = right;
        if (!recordIndexLess(records, indices[root], indices[child])) return;
        std.mem.swap(u32, &indices[root], &indices[child]);
        root = child;
    }
}

fn recordIndexLess(records: []const protocol.Record, left: u32, right: u32) bool {
    return protocol.identityLessThan(&records[left].identity, &records[right].identity);
}

comptime {
    if (@sizeOf(usize) != 8) @compileError("transaction journal requires a 64-bit host");
}
