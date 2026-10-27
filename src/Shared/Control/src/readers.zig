//! Runtime readers identified by an item key and a holder name. The controller
//! owns synchronization and callback dispatch; this table owns only registration
//! identity and copied names in fixed-capacity storage.
const std = @import("std");
const types = @import("types.zig");
pub const ItemKey = types.ItemKey;

pub const Entry = struct {
    used: bool = false,
    id: u64 = 0,
    key: ItemKey = .{ .item = 0 },
    holder: []const u8 = &.{},
    offset: usize = 0,
    length: usize = 0,
};

/// Two allocations at creation reserve the entries and holder-text bytes.
/// Removing an entry compacts the bytes, so all released capacity is reusable.
/// Entry views last only until the next registration or removal.
pub const Readers = struct {
    allocator: std.mem.Allocator,
    entries: []Entry,
    bytes: []u8,
    used_bytes: usize = 0,
    count: usize = 0,
    last_id: u64 = 0,
    peak: usize = 0,

    pub fn init(allocator: std.mem.Allocator, entry_capacity: usize, byte_capacity: usize) !Readers {
        const entries = try allocator.alloc(Entry, entry_capacity);
        errdefer allocator.free(entries);
        const bytes = try allocator.alloc(u8, byte_capacity);
        @memset(entries, .{});
        return .{ .allocator = allocator, .entries = entries, .bytes = bytes };
    }
    pub fn deinit(self: *Readers) void {
        self.allocator.free(self.entries);
        self.allocator.free(self.bytes);
        self.* = undefined;
    }
    pub fn current(self: *const Readers) usize {
        return self.used_bytes + self.count * @sizeOf(Entry);
    }
    pub fn capacity(self: *const Readers) usize {
        return self.bytes.len + self.entries.len * @sizeOf(Entry);
    }
    pub fn register(self: *Readers, key: ItemKey, holder: []const u8) !u64 {
        if (holder.len == 0) return error.InvalidHolder;
        var free: ?usize = null;
        for (self.entries, 0..) |entry, index| {
            if (!entry.used) {
                if (free == null) free = index;
                continue;
            }
            if (entry.key.item == key.item and entry.key.block == key.block and std.mem.eql(u8, entry.holder, holder))
                return error.DuplicateHolder;
        }
        const slot = free orelse return error.ReaderFull;
        if (holder.len > self.bytes.len - self.used_bytes or self.last_id == std.math.maxInt(u64))
            return error.ReaderFull;
        const offset = self.used_bytes;
        @memcpy(self.bytes[offset .. offset + holder.len], holder);
        const id = self.last_id + 1;
        self.entries[slot] = .{
            .used = true,
            .id = id,
            .key = key,
            .holder = self.bytes[offset .. offset + holder.len],
            .offset = offset,
            .length = holder.len,
        };
        self.used_bytes += holder.len;
        self.count += 1;
        self.last_id = id;
        self.peak = @max(self.peak, self.current());
        return id;
    }
    pub fn unregister(self: *Readers, id: u64) void {
        var found: ?usize = null;
        for (self.entries, 0..) |entry, index| {
            if (entry.used and entry.id == id) {
                found = index;
                break;
            }
        }
        const slot = found orelse return;
        const removed = self.entries[slot];
        const after = removed.offset + removed.length;
        std.mem.copyForwards(u8, self.bytes[removed.offset .. self.used_bytes - removed.length], self.bytes[after..self.used_bytes]);
        self.entries[slot] = .{};
        self.used_bytes -= removed.length;
        self.count -= 1;
        for (self.entries) |*entry| {
            if (!entry.used) continue;
            if (entry.offset > removed.offset) entry.offset -= removed.length;
            entry.holder = self.bytes[entry.offset .. entry.offset + entry.length];
        }
    }
};
