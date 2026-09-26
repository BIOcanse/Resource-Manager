const std = @import("std");
const protocol = @import("protocol.zig");

pub const no_index: u32 = std.math.maxInt(u32);

pub const Bank = struct {
    records: []protocol.Record,
    primary_index: []u32,
    payload_index: []u32,
    count: u32 = 0,

    pub fn init(allocator: std.mem.Allocator, config: *const protocol.CreateConfig) !Bank {
        const records = try allocator.alloc(protocol.Record, config.record_capacity);
        errdefer allocator.free(records);
        const primary_index = try allocator.alloc(u32, config.primary_index_capacity);
        errdefer allocator.free(primary_index);
        const payload_index = try allocator.alloc(u32, config.payload_index_capacity);
        errdefer allocator.free(payload_index);
        var result = Bank{
            .records = records,
            .primary_index = primary_index,
            .payload_index = payload_index,
        };
        result.clear();
        return result;
    }

    pub fn deinit(self: *Bank, allocator: std.mem.Allocator) void {
        allocator.free(self.payload_index);
        allocator.free(self.primary_index);
        allocator.free(self.records);
        self.* = undefined;
    }

    pub fn clear(self: *Bank) void {
        @memset(self.records, std.mem.zeroes(protocol.Record));
        @memset(self.primary_index, no_index);
        @memset(self.payload_index, no_index);
        self.count = 0;
    }

    pub fn findPrimary(self: *const Bank, primary: *const protocol.PrimaryIdentity) ?u32 {
        const mask = self.primary_index.len - 1;
        var probe = primaryHash(primary) & mask;
        var remaining = self.primary_index.len;
        while (remaining != 0) : (remaining -= 1) {
            const record_index = self.primary_index[probe];
            if (record_index == no_index) return null;
            if (record_index < self.count and
                protocol.samePrimary(&self.records[record_index].primary, primary))
            {
                return record_index;
            }
            probe = (probe + 1) & mask;
        }
        return null;
    }

    pub fn findPayload(
        self: *const Bank,
        binding: *const protocol.OriginalBinding,
        payload: *const protocol.DurablePayloadReference,
    ) ?u32 {
        const mask = self.payload_index.len - 1;
        var probe = payloadHash(binding, payload) & mask;
        var remaining = self.payload_index.len;
        while (remaining != 0) : (remaining -= 1) {
            const record_index = self.payload_index[probe];
            if (record_index == no_index) return null;
            if (record_index < self.count) {
                const record = &self.records[record_index];
                if (protocol.samePayloadKey(&record.original_binding, &record.payload, binding, payload))
                    return record_index;
            }
            probe = (probe + 1) & mask;
        }
        return null;
    }

    pub fn append(self: *Bank, record: protocol.Record) bool {
        if (self.count == self.records.len) return false;
        const index = self.count;
        self.records[index] = record;
        self.count += 1;
        if (!self.insertPrimary(index) or !self.insertPayload(index)) {
            self.count -= 1;
            self.records[index] = std.mem.zeroes(protocol.Record);
            _ = self.rebuildIndexes();
            return false;
        }
        return true;
    }

    pub fn removeAt(self: *Bank, record_index: u32) bool {
        if (record_index >= self.count) return false;
        const last = self.count - 1;
        if (record_index != last) self.records[record_index] = self.records[last];
        self.records[last] = std.mem.zeroes(protocol.Record);
        self.count = last;
        return self.rebuildIndexes();
    }

    pub fn rebuildIndexes(self: *Bank) bool {
        @memset(self.primary_index, no_index);
        @memset(self.payload_index, no_index);
        var index: u32 = 0;
        while (index < self.count) : (index += 1) {
            if (!self.insertPrimary(index) or !self.insertPayload(index)) return false;
        }
        return true;
    }

    fn insertPrimary(self: *Bank, record_index: u32) bool {
        const primary = &self.records[record_index].primary;
        const mask = self.primary_index.len - 1;
        var probe = primaryHash(primary) & mask;
        var remaining = self.primary_index.len;
        while (remaining != 0) : (remaining -= 1) {
            if (self.primary_index[probe] == no_index) {
                self.primary_index[probe] = record_index;
                return true;
            }
            probe = (probe + 1) & mask;
        }
        return false;
    }

    fn insertPayload(self: *Bank, record_index: u32) bool {
        const record = &self.records[record_index];
        const mask = self.payload_index.len - 1;
        var probe = payloadHash(&record.original_binding, &record.payload) & mask;
        var remaining = self.payload_index.len;
        while (remaining != 0) : (remaining -= 1) {
            if (self.payload_index[probe] == no_index) {
                self.payload_index[probe] = record_index;
                return true;
            }
            probe = (probe + 1) & mask;
        }
        return false;
    }
};

fn primaryHash(primary: *const protocol.PrimaryIdentity) usize {
    const value = @as(u64, primary.scope) ^ std.math.rotl(u64, primary.target_id, 7) ^
        std.math.rotl(u64, primary.software_id, 19) ^
        std.math.rotl(u64, primary.process_start_key, 37) ^ @as(u64, primary.process_id);
    return @truncate(mix64(value));
}

fn payloadHash(
    binding: *const protocol.OriginalBinding,
    payload: *const protocol.DurablePayloadReference,
) usize {
    const value = binding.journal_instance_low ^
        std.math.rotl(u64, binding.journal_instance_high, 17) ^
        std.math.rotl(u64, @as(u64, payload.slot), 31) ^
        std.math.rotl(u64, @as(u64, payload.generation), 47);
    return @truncate(mix64(value));
}

fn mix64(initial: u64) u64 {
    var value = initial;
    value ^= value >> 33;
    value *%= 0xff51_afd7_ed55_8ccd;
    value ^= value >> 33;
    value *%= 0xc4ce_b9fe_1a85_ec53;
    value ^= value >> 33;
    return value;
}
