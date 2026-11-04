const std = @import("std");
const windows = std.os.windows;

// Payloads are immutable borrowed language-boundary references, never JSON round trips.
// This owner owns every retained reference and every subscription slot. The callbacks
// are codec/platform leaves; none owns sequence, retention, restore or delivery policy.
pub const Meta = extern struct { sequence: i64, flags: u32 };
pub const Store = extern struct { version: i32, next_sequence: i64, count: usize };
pub const Callbacks = extern struct {
    release: *const fn (usize) callconv(.c) void,
    describe: *const fn (usize) callconv(.c) Meta,
    load: *const fn (?*anyopaque, *Store) callconv(.c) i32,
    loaded_item: *const fn (?*anyopaque, usize) callconv(.c) usize,
    loaded_end: *const fn (?*anyopaque) callconv(.c) void,
    create_item: *const fn (?*anyopaque, i64) callconv(.c) usize,
    save: *const fn (?*anyopaque, i64, [*]const usize, usize, usize) callconv(.c) i32,
    deliver: *const fn (?*anyopaque, [*]const usize, usize, [*]const usize, usize) callconv(.c) i32,
    log_error: *const fn (?*anyopaque, u32) callconv(.c) i32,
};
pub const Config = extern struct { maximum_items: usize, maximum_subscribers: usize };

pub const Owner = struct {
    allocator: std.mem.Allocator,
    callbacks: Callbacks,
    mutex: windows.SRWLOCK = windows.SRWLOCK_INIT,
    items: []usize,
    readers: []usize,
    count: usize = 0,
    reader_count: usize = 0,
    next_sequence: i64 = 1,
    restored: bool = false,

    pub fn init(allocator: std.mem.Allocator, config: Config, callbacks: Callbacks) !*Owner {
        if (config.maximum_items == 0 or config.maximum_subscribers == 0) return error.InvalidCapacity;
        const self = try allocator.create(Owner);
        errdefer allocator.destroy(self);
        const items = try allocator.alloc(usize, config.maximum_items);
        errdefer allocator.free(items);
        const readers = try allocator.alloc(usize, config.maximum_subscribers);
        self.* = .{ .allocator = allocator, .callbacks = callbacks, .items = items, .readers = readers };
        return self;
    }
    pub fn deinit(self: *Owner) void {
        for (self.items[0..self.count]) |item| self.callbacks.release(item);
        // SafeHandle keeps the owner alive for each registered async reader.
        std.debug.assert(self.reader_count == 0);
        const allocator = self.allocator;
        allocator.free(self.readers);
        allocator.free(self.items);
        allocator.destroy(self);
    }
    pub fn lock(self: *Owner) void { windows.ntdll.RtlAcquireSRWLockExclusive(&self.mutex); }
    pub fn unlock(self: *Owner) void { windows.ntdll.RtlReleaseSRWLockExclusive(&self.mutex); }

    fn restore(self: *Owner, context: ?*anyopaque, persist: bool) i32 {
        if (self.restored) return 0;
        self.restored = true;
        if (!persist) return 0;
        var store: Store = undefined;
        const status = self.callbacks.load(context, &store);
        defer self.callbacks.loaded_end(context);
        if (status == 1) return 0; // Missing file.
        if (status == 7) return 7; // Unexpected codec exception propagates without publication.
        if (status != 0) return self.callbacks.log_error(context, 0);
        if (store.version != 1 or store.count > self.items.len or store.next_sequence >= std.math.maxInt(i64)) {
            return self.callbacks.log_error(context, 0);
        }
        var accepted: usize = 0;
        var last: i64 = 0;
        while (accepted < store.count) {
            const item = self.callbacks.loaded_item(context, accepted);
            if (item == 0) break;
            const meta = self.callbacks.describe(item);
            // Flags: nonblank kind, nondefault timestamp, object facts, registered kind.
            if (meta.sequence <= last or meta.flags & 7 != 7) {
                self.callbacks.release(item);
                break;
            }
            self.items[accepted] = item;
            last = meta.sequence;
            accepted += 1;
        }
        if (accepted != store.count or store.next_sequence <= last) {
            for (self.items[0..accepted]) |item| self.callbacks.release(item);
            return self.callbacks.log_error(context, 0);
        }
        self.count = accepted;
        self.next_sequence = store.next_sequence;
        return 0;
    }
    pub fn read(self: *Owner, context: ?*anyopaque, persist: bool) i32 {
        const status = self.restore(context, persist);
        if (status != 0) return status;
        return self.callbacks.deliver(context, self.items.ptr, self.count, self.readers.ptr, 0);
    }
    pub fn subscribe(self: *Owner, context: ?*anyopaque, persist: bool, reader: usize) i32 {
        if (reader == 0) return 2;
        if (self.reader_count == self.readers.len) return 3;
        const status = self.restore(context, persist);
        if (status != 0) return status;
        // Register and emit the first current value under the same owner lock.
        self.readers[self.reader_count] = reader;
        self.reader_count += 1;
        const result = self.callbacks.deliver(context, self.items.ptr, self.count, self.readers.ptr + self.reader_count - 1, 1);
        if (result != 0) self.reader_count -= 1;
        return result;
    }
    pub fn unsubscribe(self: *Owner, reader: usize) void {
        for (self.readers[0..self.reader_count], 0..) |value, index| {
            if (value != reader) continue;
            self.reader_count -= 1;
            self.readers[index] = self.readers[self.reader_count];
            return;
        }
    }
    pub fn post(self: *Owner, context: ?*anyopaque, persist: bool, capacity: usize, flags: u32) i32 {
        if (flags & 8 == 0) return 4;
        if (flags & 4 == 0) return 5;
        if (capacity == 0 or capacity > self.items.len) return 2;
        const status = self.restore(context, persist);
        if (status != 0) return status;
        if (self.next_sequence == std.math.maxInt(i64)) return 6;
        const item = self.callbacks.create_item(context, self.next_sequence);
        if (item == 0) return 7;
        const evicted = self.count + 1 -| capacity;
        self.next_sequence += 1;
        // Borrow the kept suffix plus the new item directly. Neither mutation
        // nor an evicted reference becomes visible before the save attempt.
        if (persist) {
            const saved = self.callbacks.save(context, self.next_sequence, self.items.ptr + evicted, self.count - evicted, item);
            if (saved == 7 or (saved != 0 and self.callbacks.log_error(context, 1) != 0)) {
                self.callbacks.release(item);
                return 7;
            }
        }
        for (self.items[0..evicted]) |old| self.callbacks.release(old);
        std.mem.copyForwards(usize, self.items[0 .. self.count - evicted], self.items[evicted..self.count]);
        self.count -= evicted;
        self.items[self.count] = item;
        self.count += 1;
        // Expected I/O failure still publishes; unexpected codec/logger errors
        // consume the sequence but leave the previous current list, as before.
        if (self.reader_count == 0) return 0;
        return self.callbacks.deliver(context, self.items.ptr, self.count, self.readers.ptr, self.reader_count);
    }
    pub fn ownedBytes(self: *const Owner) usize {
        return @sizeOf(Owner) + (self.items.len + self.readers.len) * @sizeOf(usize);
    }
};

pub fn scope(kind: []const u16) u32 {
    const names = .{ "memory-protection.action", "update-available", "access-request", "crash-report", "cpu-monopoly-protection.action", "device-restart.action" };
    const scopes = .{ 1, 2, 4, 2, 1, 1 };
    inline for (names, scopes) |name, result| {
        if (kind.len == name.len) {
            var equal = true;
            for (kind, name) |a, b| { if (a != b) { equal = false; break; } }
            if (equal) return result;
        }
    }
    return 0;
}

pub const Projection = extern struct {
    scope_at: *const fn (?*anyopaque, usize) callconv(.c) u32,
    select: *const fn (?*anyopaque, usize) callconv(.c) void,
};
pub fn project(context: ?*anyopaque, count: usize, access: u32, callbacks: Projection) usize {
    var selected: usize = 0;
    for (0..count) |index| {
        const required = callbacks.scope_at(context, index);
        if (required == 0 or access & required == 0) continue;
        callbacks.select(context, index);
        selected += 1;
    }
    return selected;
}

pub fn sameSequences(context: ?*anyopaque, left: usize, right: usize,
    sequence: *const fn (?*anyopaque, u32, usize) callconv(.c) i64) bool {
    if (left != right) return false;
    for (0..left) |index| if (sequence(context, 0, index) != sequence(context, 1, index)) return false;
    return true;
}
