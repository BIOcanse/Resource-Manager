const growth = @import("../../common/workspace_growth.zig");
const std = @import("std");
const bank_module = @import("bank.zig");
const protocol = @import("protocol.zig");

pub const Session = struct {
    growth: growth.Log = .{},
    allocator: std.mem.Allocator,
    mutex: std.atomic.Mutex = .unlocked,
    config: protocol.CreateConfig,
    active: bank_module.Bank,
    staging: bank_module.Bank,
    sort_indices: []u32,
    ledger_revision: u64 = 1,

    pub fn create(config: *const protocol.CreateConfig) !*Session {
        return createWithAllocator(config, std.heap.page_allocator);
    }

    pub fn createWithAllocator(
        config: *const protocol.CreateConfig,
        allocator: std.mem.Allocator,
    ) !*Session {
        return createWithContentCapacity(config, allocator, @min(config.record_capacity, 16));
    }

    pub fn createWithContentCapacity(
        config: *const protocol.CreateConfig,
        allocator: std.mem.Allocator,
        record_capacity: u32,
    ) !*Session {
        if (!protocol.validCreateConfig(config)) return error.InvalidConfiguration;
        if (record_capacity == 0 or record_capacity > config.record_capacity)
            return error.InvalidConfiguration;
        const allocation_config = try allocationConfig(config, record_capacity);
        const resident_bytes = try requiredResidentBytes(&allocation_config);
        if (resident_bytes > config.maximum_resident_bytes) return error.ResidentBudgetExceeded;

        const self = try allocator.create(Session);
        errdefer allocator.destroy(self);
        var active = try bank_module.Bank.init(allocator, &allocation_config);
        errdefer active.deinit(allocator);
        var staging = try bank_module.Bank.init(allocator, &allocation_config);
        errdefer staging.deinit(allocator);
        const sort_indices = try allocator.alloc(u32, record_capacity);
        errdefer allocator.free(sort_indices);
        @memset(sort_indices, 0);

        self.* = .{
            .allocator = allocator,
            .config = config.*,
            .active = active,
            .staging = staging,
            .sort_indices = sort_indices,
        };
        return self;
    }

    pub fn destroy(self: *Session) void {
        const allocator = self.allocator;
        allocator.free(self.sort_indices);
        self.staging.deinit(allocator);
        self.active.deinit(allocator);
        allocator.destroy(self);
    }

    pub fn capacity(self: *const Session) protocol.Capacity {
        return .{
            .struct_size = @sizeOf(protocol.Capacity),
            .record_capacity = self.config.record_capacity,
            .primary_index_capacity = self.config.primary_index_capacity,
            .payload_index_capacity = self.config.payload_index_capacity,
            .record_size = @sizeOf(protocol.Record),
            .flags = 0,
            .maximum_image_bytes = self.config.maximum_image_bytes,
            .resident_bytes = self.workspaceCapacity().resident_bytes,
            .reserved = .{ 0, 0, 0 },
        };
    }

    /// The caller owns the session mutex, including every growth operation.
    pub fn workspaceCapacity(self: *const Session) protocol.WorkspaceCapacity {
        var allocated = self.config;
        allocated.record_capacity = @intCast(self.active.records.len);
        allocated.primary_index_capacity = @intCast(self.active.primary_index.len);
        allocated.payload_index_capacity = @intCast(self.active.payload_index.len);
        return .{
            .struct_size = @sizeOf(protocol.WorkspaceCapacity),
            .growth = self.growth,
            .record_capacity = allocated.record_capacity,
            .primary_index_capacity = allocated.primary_index_capacity,
            .payload_index_capacity = allocated.payload_index_capacity,
            .resident_bytes = requiredResidentBytes(&allocated) catch unreachable,
        };
    }

    /// Allocate and copy both banks before publishing any replacement storage.
    /// Failure leaves records, indexes, sort storage and revisions untouched.
    pub fn ensureRecordCapacity(self: *Session, needed: u32, reason: growth.Reason) !void {
        if (needed <= self.active.records.len) return;
        if (needed > self.config.record_capacity) return error.CapacityFull;
        var next: u32 = @intCast(self.active.records.len);
        while (next < needed) {
            next = @intCast(@min(@as(u64, next) * 2, self.config.record_capacity));
        }
        const allocation_config = try allocationConfig(&self.config, next);
        const new_bytes = try requiredResidentBytes(&allocation_config);
        if (new_bytes > self.config.maximum_resident_bytes) return error.ResidentBudgetExceeded;
        var active = try bank_module.Bank.init(self.allocator, &allocation_config);
        errdefer active.deinit(self.allocator);
        var staging = try bank_module.Bank.init(self.allocator, &allocation_config);
        errdefer staging.deinit(self.allocator);
        const sort_indices = try self.allocator.alloc(u32, next);
        errdefer self.allocator.free(sort_indices);
        @memset(sort_indices, 0);
        @memcpy(sort_indices[0..self.sort_indices.len], self.sort_indices);
        try copyBank(&active, &self.active);
        try copyBank(&staging, &self.staging);
        const old_capacity = self.workspaceCapacity();
        std.mem.swap(bank_module.Bank, &self.active, &active);
        std.mem.swap(bank_module.Bank, &self.staging, &staging);
        const old_sort_indices = self.sort_indices;
        self.sort_indices = sort_indices;
        self.allocator.free(old_sort_indices);
        staging.deinit(self.allocator);
        active.deinit(self.allocator);
        self.growth.record(reason, .rows, old_capacity.record_capacity, next);
    }

    pub fn prepareSortIndices(self: *Session) []u32 {
        var index: u32 = 0;
        while (index < self.active.count) : (index += 1) self.sort_indices[index] = index;
        const active_indices = self.sort_indices[0..self.active.count];
        heapSortIndices(self.active.records, active_indices);
        return active_indices;
    }

    pub fn publishStaging(self: *Session, ledger_revision: u64) void {
        std.mem.swap(bank_module.Bank, &self.active, &self.staging);
        self.staging.clear();
        self.ledger_revision = ledger_revision;
    }
};

fn allocationConfig(config: *const protocol.CreateConfig, records: u32) !protocol.CreateConfig {
    var result = config.*;
    result.record_capacity = records;
    const minimum_index = try std.math.mul(u32, records, 2);
    const index_capacity = try std.math.ceilPowerOfTwo(u32, minimum_index);
    result.primary_index_capacity = @min(index_capacity, config.primary_index_capacity);
    result.payload_index_capacity = @min(index_capacity, config.payload_index_capacity);
    return result;
}

fn copyBank(destination: *bank_module.Bank, source: *const bank_module.Bank) !void {
    @memcpy(destination.records[0..source.count], source.records[0..source.count]);
    destination.count = source.count;
    if (!destination.rebuildIndexes()) return error.CapacityFull;
}

pub fn requiredResidentBytes(config: *const protocol.CreateConfig) !u64 {
    var bank_bytes: u64 = 0;
    bank_bytes = try addAllocation(bank_bytes, config.record_capacity, @sizeOf(protocol.Record));
    bank_bytes = try addAllocation(bank_bytes, config.primary_index_capacity, @sizeOf(u32));
    bank_bytes = try addAllocation(bank_bytes, config.payload_index_capacity, @sizeOf(u32));
    var total: u64 = @sizeOf(Session);
    total = try std.math.add(u64, total, try std.math.mul(u64, bank_bytes, 2));
    total = try addAllocation(total, config.record_capacity, @sizeOf(u32));
    return total;
}

pub fn lockMutex(mutex: *std.atomic.Mutex) void {
    while (!mutex.tryLock()) std.atomic.spinLoopHint();
}

fn addAllocation(total: u64, count: u32, item_size: usize) !u64 {
    return std.math.add(u64, total, try std.math.mul(u64, count, item_size));
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
        if (right < end and less(records, indices[left], indices[right])) child = right;
        if (!less(records, indices[root], indices[child])) return;
        std.mem.swap(u32, &indices[root], &indices[child]);
        root = child;
    }
}

fn less(records: []const protocol.Record, left: u32, right: u32) bool {
    return protocol.primaryLessThan(&records[left].primary, &records[right].primary);
}

comptime {
    if (@sizeOf(usize) != 8) @compileError("applied ownership requires a 64-bit host");
}
