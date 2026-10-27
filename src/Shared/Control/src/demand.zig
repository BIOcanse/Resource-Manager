const std = @import("std");
const types = @import("types.zig");
pub const Reason = types.Reason;
pub const Change = struct { previous: ?u32, current: ?u32 };
pub const Entry = struct {
    used: bool = false,
    block: u32 = 0,
    key: []const u8 = &.{},
    reason: Reason = .{ .kind = 0, .owner = &.{}, .cause = &.{}, .plan_version = 0 },
    offset: usize = 0,
    key_length: usize = 0,
    owner_length: usize = 0,
    cause_length: usize = 0,
    fn byteLength(self: Entry) usize {
        return self.key_length + self.owner_length + self.cause_length;
    }
};

/// A bounded allocator for demand entries and their copied texts. Entries and
/// bytes are reserved once. Removing a demand packs the live texts in place,
/// making all released capacity available to the next call.
/// All access is synchronized by the controller; slices last until mutation.
pub const Demands = struct {
    allocator: std.mem.Allocator,
    entries: []Entry,
    bytes: []u8,
    used_bytes: usize = 0,
    count: usize = 0,
    peak: usize = 0,

    pub fn init(allocator: std.mem.Allocator, entry_capacity: usize, byte_capacity: usize) !Demands {
        const entries = try allocator.alloc(Entry, entry_capacity);
        errdefer allocator.free(entries);
        const bytes = try allocator.alloc(u8, byte_capacity);
        @memset(entries, .{});
        return .{ .allocator = allocator, .entries = entries, .bytes = bytes };
    }
    pub fn deinit(self: *Demands) void {
        self.allocator.free(self.entries);
        self.allocator.free(self.bytes);
        self.* = undefined;
    }
    pub fn current(self: *const Demands) usize {
        return self.used_bytes + self.count * @sizeOf(Entry);
    }
    pub fn capacity(self: *const Demands) usize {
        return self.bytes.len + self.entries.len * @sizeOf(Entry);
    }
    pub fn has(self: *const Demands, block: u32) bool {
        for (self.entries) |entry| if (entry.used and entry.block == block) return true;
        return false;
    }
    pub fn find(self: *const Demands, key: []const u8) ?usize {
        for (self.entries, 0..) |entry, index| if (entry.used and std.mem.eql(u8, entry.key, key)) return index;
        return null;
    }
    fn refresh(self: *Demands, entry: *Entry) void {
        const owner_start = entry.offset + entry.key_length;
        const cause_start = owner_start + entry.owner_length;
        entry.key = self.bytes[entry.offset..owner_start];
        entry.reason.owner = self.bytes[owner_start..cause_start];
        entry.reason.cause = self.bytes[cause_start .. cause_start + entry.cause_length];
    }
    fn removeAt(self: *Demands, index: usize) void {
        const removed = self.entries[index];
        const length = removed.byteLength();
        const after = removed.offset + length;
        std.mem.copyForwards(u8, self.bytes[removed.offset .. self.used_bytes - length], self.bytes[after..self.used_bytes]);
        self.used_bytes -= length;
        self.entries[index] = .{};
        self.count -= 1;
        for (self.entries) |*entry| {
            if (!entry.used) continue;
            if (entry.offset > removed.offset) entry.offset -= length;
            self.refresh(entry);
        }
    }
    pub fn clear(self: *Demands, key: []const u8) !Change {
        if (key.len == 0) return error.InvalidDemand;
        const index = self.find(key) orelse return .{ .previous = null, .current = null };
        const previous = self.entries[index].block;
        self.removeAt(index);
        return .{ .previous = previous, .current = null };
    }
    pub fn set(self: *Demands, key: []const u8, block: u32, reason: Reason) !Change {
        if (key.len == 0 or block == 0 or reason.owner.len == 0 or reason.cause.len == 0) return error.InvalidDemand;
        const existing = self.find(key);
        var slot = existing;
        if (slot == null) {
            for (self.entries, 0..) |entry, index| {
                if (!entry.used) {
                    slot = index;
                    break;
                }
            }
        }
        const index = slot orelse return error.DemandFull;
        const old_length = if (existing) |old| self.entries[old].byteLength() else 0;
        const needed = std.math.add(usize, key.len, reason.owner.len) catch return error.DemandFull;
        const total = std.math.add(usize, needed, reason.cause.len) catch return error.DemandFull;
        if (total > self.bytes.len - (self.used_bytes - old_length)) return error.DemandFull;
        const previous: ?u32 = if (existing) |old| self.entries[old].block else null;
        // The host's strings belong to this call, never to this allocator. Thus
        // removing a previous entry cannot invalidate the incoming text.
        if (existing) |old| self.removeAt(old);
        const start = self.used_bytes;
        @memcpy(self.bytes[start .. start + key.len], key);
        const owner_start = start + key.len;
        @memcpy(self.bytes[owner_start .. owner_start + reason.owner.len], reason.owner);
        const cause_start = owner_start + reason.owner.len;
        @memcpy(self.bytes[cause_start .. cause_start + reason.cause.len], reason.cause);
        self.entries[index] = .{
            .used = true,
            .block = block,
            .offset = start,
            .key_length = key.len,
            .owner_length = reason.owner.len,
            .cause_length = reason.cause.len,
            .reason = .{ .kind = reason.kind, .owner = &.{}, .cause = &.{}, .plan_version = reason.plan_version },
        };
        self.refresh(&self.entries[index]);
        self.used_bytes += total;
        self.count += 1;
        self.peak = @max(self.peak, self.current());
        return .{ .previous = previous, .current = block };
    }
};
