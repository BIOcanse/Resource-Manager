const std = @import("std");
pub const Key = @import("series.zig").Key;

// Payload is a relative offset into this owner's native metadata arena, never
// a managed object handle or a cached pointer into a borrowed input.
pub const Row = extern struct {
    pid: i32,
    state: u32,
    birth: u64,
    key: [20]u8,
    key_length: u32,
    pad: u32 = 0,
    current: u64,
    growth: u64,
    seconds: f64,
    rate: f64,
    sample_count: u32,
    reserved: u32 = 0,
    first: i64,
    last: i64,
    payload: usize,
    first_offset_minutes: i32 = 0,
    last_offset_minutes: i32 = 0,

    pub fn identity(self: Row) Key {
        return .{ .pid = self.pid, .reserved = 0, .birth = self.birth, .key = self.key, .key_length = self.key_length };
    }
};
pub const PlanResult = extern struct {
    count: u32 = 0,
    unrecorded: u32 = 0,
    recorded_suspects: u32 = 0,
    changed: u32 = 0,
    required_bytes: usize = 0,
};
pub const Error = error{ InvalidInput, CapacityExceeded, BufferTooSmall };

pub const Text = extern struct { pointer: ?[*]const u16, length: usize };
pub const Metadata = extern struct {
    image_status: u32,
    services_status: u32,
    name: Text,
    path: Text,
    product: Text,
    names: ?[*]const Text,
    names_count: usize,
};

pub fn validMetadata(metadata: Metadata) bool {
    if (metadata.image_status > 2 or metadata.services_status > 2) return false;
    if (metadata.image_status == 0) {
        if (!nonblank(metadata.name) or !nonblank(metadata.path)) return false;
    } else if (metadata.name.pointer != null or metadata.path.pointer != null or metadata.product.pointer != null) return false;
    if (metadata.services_status != 0 and metadata.names_count != 0) return false;
    if (metadata.names_count > 0) {
        const names = metadata.names orelse return false;
        for (names[0..metadata.names_count]) |name| if (!nonblank(name)) return false;
    }
    return true;
}

fn nonblank(text: Text) bool {
    const pointer = text.pointer orelse return false;
    for (pointer[0..text.length]) |character| {
        // System.Char.IsWhiteSpace's Unicode White_Space set, including the
        // control and non-breaking characters relevant to persisted metadata.
        switch (character) {
            0x09...0x0d, 0x20, 0x85, 0xa0, 0x1680, 0x2000...0x200a, 0x2028, 0x2029, 0x202f, 0x205f, 0x3000 => {},
            else => return true,
        }
    }
    return false;
}

pub fn requiredBytes(maximum: u32) usize {
    if (maximum == 0) return 0;
    const rows = std.math.mul(usize, maximum, 2 * @sizeOf(Row)) catch return 0;
    const indices = std.math.mul(usize, 2 * indexCapacity(maximum) + maximum, @sizeOf(u32)) catch return 0;
    const bytes = std.math.add(usize, @sizeOf(Store), std.math.add(usize, rows, indices) catch return 0) catch return 0;
    return std.mem.alignForward(usize, bytes, 8);
}

fn indexCapacity(maximum: u32) usize {
    return std.math.ceilPowerOfTwoAssert(usize, @as(usize, maximum) * 2);
}

// Both row banks live in the caller's registered Ledger region. A planned
// mutation is invisible until the caller successfully commits the file.
pub const Store = extern struct {
    maximum: u32,
    count: u32 = 0,
    pending_count: u32 = 0,
    pending: u32 = 0,
    arena_capacity: usize = 0,
    committed_bytes: usize = 0,
    pending_bytes: usize = 0,
    required_arena: usize = 0,

    fn arena(self: *Store, candidate: bool) []align(8) u8 {
        const pointer: [*]align(8) u8 = @ptrCast(self);
        const offset = requiredBytes(self.maximum) + (if (candidate) self.arena_capacity else 0);
        const start: [*]align(8) u8 = @alignCast(pointer + offset);
        return start[0..self.arena_capacity];
    }
    pub fn reserve(self: *Store, length: usize) Error!void {
        const base = requiredBytes(self.maximum);
        if (length < base) return error.InvalidInput;
        const capacity = ((length - base) / 2) & ~@as(usize, 7);
        if (capacity < self.arena_capacity) return error.InvalidInput;
        self.pending = 0;
        self.arena_capacity = capacity;
    }
    pub fn metadata(self: *Store, candidate: bool, index: usize) Error!Metadata {
        const rows = try self.snapshot(candidate);
        if (index >= rows.len) return error.InvalidInput;
        return (Source{ .owned = .{ .arena = self.arena(candidate), .offset = rows[index].payload } }).description();
    }
    pub fn service(self: *Store, candidate: bool, index: usize, name_index: usize) Error!Text {
        const rows = try self.snapshot(candidate);
        if (index >= rows.len) return error.InvalidInput;
        const source = Source{ .owned = .{ .arena = self.arena(candidate), .offset = rows[index].payload } };
        if (name_index >= source.description().names_count) return error.InvalidInput;
        return source.name(name_index);
    }

    fn bank(self: *Store, candidate: bool) []Row {
        const pointer: [*]align(8) u8 = @ptrCast(self);
        const offset = @sizeOf(Store) + (if (candidate) @as(usize, self.maximum) * @sizeOf(Row) else 0);
        const rows: [*]Row = @ptrCast(@alignCast(pointer + offset));
        return rows[0..self.maximum];
    }
    fn indexMemory(self: *Store) []u32 {
        const pointer: [*]align(8) u8 = @ptrCast(self);
        const offset = @sizeOf(Store) + @as(usize, self.maximum) * 2 * @sizeOf(Row);
        const indices: [*]u32 = @ptrCast(@alignCast(pointer + offset));
        return indices[0 .. indexCapacity(self.maximum) * 2 + self.maximum];
    }
    fn resetIndex(self: *Store) void {
        @memset(self.indexMemory(), 0);
    }
    fn find(self: *Store, rows: []const Row, key: Key) ?usize {
        if (key.key_length == 0 or key.key_length > key.key.len) return null;
        const slots = self.indexMemory()[0..indexCapacity(self.maximum)];
        var bucket = hash(key) & (slots.len - 1);
        for (0..slots.len) |_| {
            const encoded = slots[bucket];
            if (encoded == 0) return null;
            const index = encoded - 1;
            if (same(rows[index].identity(), key)) return index;
            bucket = (bucket + 1) & (slots.len - 1);
        }
        return null;
    }
    fn addIndex(self: *Store, rows: []const Row, index: usize) void {
        const capacity = indexCapacity(self.maximum);
        const memory = self.indexMemory();
        const slots = memory[0..capacity];
        var bucket = hash(rows[index].identity()) & (capacity - 1);
        for (0..capacity) |_| {
            if (slots[bucket] == 0) {
                slots[bucket] = @intCast(index + 1);
                break;
            }
            bucket = (bucket + 1) & (capacity - 1);
        }
        const pid_bucket = pidHash(rows[index].pid) & (capacity - 1);
        memory[2 * capacity + index] = memory[capacity + pid_bucket];
        memory[capacity + pid_bucket] = @intCast(index + 1);
    }
    fn mark(self: *Store, rows: []Row, key: Key, flag: u32) void {
        if (self.find(rows, key)) |index| rows[index].reserved |= flag;
    }
    fn markPid(self: *Store, rows: []Row, pid: i32, flag: u32) void {
        const capacity = indexCapacity(self.maximum);
        const memory = self.indexMemory();
        var encoded = memory[capacity + (pidHash(pid) & (capacity - 1))];
        for (0..rows.len) |_| {
            if (encoded == 0) break;
            const index = encoded - 1;
            if (rows[index].pid == pid) rows[index].reserved |= flag;
            encoded = memory[2 * capacity + index];
        }
    }
    pub fn snapshot(self: *Store, candidate: bool) Error![]const Row {
        if (candidate and self.pending == 0) return error.InvalidInput;
        return self.bank(candidate)[0..if (candidate) self.pending_count else self.count];
    }
    pub fn restore(self: *Store, rows: []const Row, descriptions: []const Metadata) Error!void {
        self.pending = 0;
        if (rows.len > self.maximum) return error.CapacityExceeded;
        if (rows.len != descriptions.len) return error.InvalidInput;
        for (rows, descriptions) |row, description| {
            if (!valid(row) or !validMetadata(description)) return error.InvalidInput;
        }
        const target = self.bank(true)[0..rows.len];
        @memcpy(target, rows);
        self.resetIndex();
        for (target, 0..) |*row, index| {
            row.reserved = 1;
            row.payload = index;
            if (self.find(target[0..index], row.identity()) != null) return error.InvalidInput;
            self.addIndex(target, index);
        }
        order(target);
        try self.prepareMetadata(target, descriptions);
        self.pending_count = @intCast(rows.len);
        self.pending = 1;
        try self.commit();
    }
    pub fn plan(self: *Store, ticks: i64, live: []const Key, unavailable: []const Key, judged: []const Key, suspects: []const Row, descriptions: []const Metadata, capacity: u32) Error!PlanResult {
        self.pending = 0;
        if (capacity == 0 or capacity > self.maximum or descriptions.len != suspects.len) return error.InvalidInput;
        for (suspects, descriptions, 0..) |suspect, description, index| {
            if (!validIdentity(suspect.identity()) or !validMetadata(description)) return error.InvalidInput;
            for (suspects[0..index]) |previous| if (same(previous.identity(), suspect.identity())) return error.InvalidInput;
        }
        const original = self.bank(false)[0..self.count];
        const candidate = self.bank(true);
        @memcpy(candidate[0..self.count], original);
        var count: usize = self.count;
        self.resetIndex();
        for (candidate[0..count], 0..) |*row, index| {
            row.reserved = 0;
            self.addIndex(candidate, index);
        }
        for (live) |key| self.mark(candidate[0..count], key, 1);
        for (unavailable) |key| {
            if (key.key_length == 0) self.markPid(candidate[0..count], key.pid, 1) else self.mark(candidate[0..count], key, 1);
        }
        for (judged) |key| self.mark(candidate[0..count], key, 2);
        for (suspects) |row| self.mark(candidate[0..count], row.identity(), 4);
        for (candidate[0..count]) |*row| {
            if (row.state == 0 and (row.reserved & 1 == 0 or (row.reserved & 2 != 0 and row.reserved & 4 == 0))) row.state = 1;
            row.reserved = 0;
        }
        var result = PlanResult{};
        for (suspects, 0..) |suspect, source_index| {
            const found = self.find(candidate[0..count], suspect.identity());
            if (found == null and count >= capacity) {
                result.unrecorded += 1;
                continue;
            }
            if (found == null and count == self.maximum) return error.CapacityExceeded;
            const index = found orelse count;
            const first = if (found != null) candidate[index].first else ticks;
            const first_offset = if (found != null) candidate[index].first_offset_minutes else suspect.first_offset_minutes;
            candidate[index] = suspect;
            candidate[index].state = 0;
            candidate[index].first = first;
            candidate[index].first_offset_minutes = first_offset;
            candidate[index].last = ticks;
            candidate[index].reserved = 1;
            candidate[index].payload = source_index;
            if (found == null) {
                self.addIndex(candidate, index);
                count += 1;
            }
            result.recorded_suspects += 1;
        }
        order(candidate[0..count]);
        try self.prepareMetadata(candidate[0..count], descriptions);
        result.count = @intCast(count);
        result.changed = @intFromBool(count != original.len or !equalRows(original, candidate[0..count]) or
            self.committed_bytes != self.pending_bytes or !std.mem.eql(u8, self.arena(false)[0..self.committed_bytes], self.arena(true)[0..self.pending_bytes]));
        self.pending_count = @intCast(count);
        self.pending = 1;
        return result;
    }
    pub fn clearHistory(self: *Store) Error!PlanResult {
        self.pending = 0;
        var count: usize = 0;
        const candidate = self.bank(true);
        for (self.bank(false)[0..self.count]) |row| {
            if (row.state != 0) continue;
            candidate[count] = row;
            count += 1;
        }
        try self.prepareMetadata(candidate[0..count], &.{});
        self.pending_count = @intCast(count);
        self.pending = 1;
        return .{ .count = @intCast(count), .changed = @intFromBool(count != self.count) };
    }
    fn prepareMetadata(self: *Store, rows: []Row, descriptions: []const Metadata) Error!void {
        var required: usize = 0;
        for (rows) |row| {
            const source = if (row.reserved == 1) Source{ .borrowed = descriptions[row.payload] } else Source{ .owned = .{ .arena = self.arena(false), .offset = row.payload } };
            required = std.math.add(usize, required, try source.requiredBytes()) catch return error.CapacityExceeded;
        }
        self.required_arena = required;
        if (required > self.arena_capacity) return error.BufferTooSmall;
        const target = self.arena(true)[0..required];
        @memset(target, 0);
        var offset: usize = 0;
        for (rows) |*row| {
            const source = if (row.reserved == 1) Source{ .borrowed = descriptions[row.payload] } else Source{ .owned = .{ .arena = self.arena(false), .offset = row.payload } };
            const next = try source.write(target, offset);
            row.payload = offset;
            row.reserved = 0;
            offset = next;
        }
        self.pending_bytes = required;
    }
    pub fn commit(self: *Store) Error!void {
        if (self.pending == 0) return error.InvalidInput;
        @memcpy(self.bank(false)[0..self.pending_count], self.bank(true)[0..self.pending_count]);
        @memcpy(self.arena(false)[0..self.pending_bytes], self.arena(true)[0..self.pending_bytes]);
        self.committed_bytes = self.pending_bytes;
        self.count = self.pending_count;
        self.pending = 0;
        self.pending_count = 0;
    }
};

pub fn init(memory: []align(8) u8, maximum: u32) Error!*Store {
    const required = requiredBytes(maximum);
    if (required == 0 or memory.len < required) return error.InvalidInput;
    const store: *Store = @ptrCast(memory.ptr);
    store.* = .{ .maximum = maximum };
    try store.reserve(memory.len);
    return store;
}

const StoredText = extern struct { offset: usize = 0, length: usize = 0 };
const StoredMetadata = extern struct {
    image_status: u32,
    services_status: u32,
    name: StoredText,
    path: StoredText,
    product: StoredText,
    names_offset: usize,
    names_count: usize,
};

const Source = union(enum) {
    borrowed: Metadata,
    owned: struct { arena: []const u8, offset: usize },

    fn stored(self: Source) *align(1) const StoredMetadata {
        return @ptrCast(self.owned.arena[self.owned.offset..].ptr);
    }
    fn text(arena_bytes: []const u8, value: StoredText) Text {
        return .{ .pointer = if (value.offset == 0) null else @ptrCast(@alignCast(arena_bytes[value.offset..].ptr)), .length = value.length };
    }
    fn description(self: Source) Metadata {
        return switch (self) {
            .borrowed => |value| value,
            .owned => |value| blk: {
                const header = self.stored();
                break :blk .{ .image_status = header.image_status, .services_status = header.services_status, .name = text(value.arena, header.name), .path = text(value.arena, header.path), .product = text(value.arena, header.product), .names = null, .names_count = header.names_count };
            },
        };
    }
    fn name(self: Source, index: usize) Text {
        return switch (self) {
            .borrowed => |value| value.names.?[index],
            .owned => |value| blk: {
                const names: [*]align(1) const StoredText = @ptrCast(value.arena[self.stored().names_offset..].ptr);
                break :blk text(value.arena, names[index]);
            },
        };
    }
    fn requiredBytes(self: Source) Error!usize {
        const value = self.description();
        var total = std.math.add(usize, @sizeOf(StoredMetadata), std.math.mul(usize, value.names_count, @sizeOf(StoredText)) catch return error.CapacityExceeded) catch return error.CapacityExceeded;
        for ([_]Text{ value.name, value.path, value.product }) |part| {
            if (part.pointer == null) continue;
            total = std.math.add(usize, total, std.math.mul(usize, part.length, 2) catch return error.CapacityExceeded) catch return error.CapacityExceeded;
        }
        for (0..value.names_count) |index| {
            total = std.math.add(usize, total, std.math.mul(usize, self.name(index).length, 2) catch return error.CapacityExceeded) catch return error.CapacityExceeded;
        }
        if (total > std.math.maxInt(usize) - 7) return error.CapacityExceeded;
        return std.mem.alignForward(usize, total, 8);
    }
    fn writeText(target: []u8, offset: *usize, value: Text) StoredText {
        const pointer = value.pointer orelse return .{};
        const result = StoredText{ .offset = offset.*, .length = value.length };
        const bytes = std.mem.sliceAsBytes(pointer[0..value.length]);
        @memcpy(target[offset.*..][0..bytes.len], bytes);
        offset.* += bytes.len;
        return result;
    }
    fn write(self: Source, target: []u8, offset: usize) Error!usize {
        const value = self.description();
        const names_offset = offset + @sizeOf(StoredMetadata);
        var cursor = names_offset + value.names_count * @sizeOf(StoredText);
        const header: *align(1) StoredMetadata = @ptrCast(target[offset..].ptr);
        header.* = .{ .image_status = value.image_status, .services_status = value.services_status, .name = writeText(target, &cursor, value.name), .path = writeText(target, &cursor, value.path), .product = writeText(target, &cursor, value.product), .names_offset = names_offset, .names_count = value.names_count };
        const names: [*]align(1) StoredText = @ptrCast(target[names_offset..].ptr);
        for (0..value.names_count) |index| names[index] = writeText(target, &cursor, self.name(index));
        return offset + try self.requiredBytes();
    }
};

fn validIdentity(key: Key) bool {
    return key.pid > 0 and key.birth > 0 and key.key_length > 0 and key.key_length <= key.key.len;
}
fn valid(row: Row) bool {
    return validIdentity(row.identity()) and row.pad == 0 and row.state <= 1 and row.first > 0 and row.last >= row.first and
        std.math.isFinite(row.rate) and row.rate > 0 and std.math.isFinite(row.seconds) and row.seconds > 0 and
        row.sample_count >= 2 and row.sample_count <= std.math.maxInt(i32) and row.growth > 0 and row.growth <= row.current and
        row.first_offset_minutes >= -840 and row.first_offset_minutes <= 840 and row.last_offset_minutes >= -840 and row.last_offset_minutes <= 840;
}
fn same(left: Key, right: Key) bool {
    return left.pid == right.pid and left.key_length == right.key_length and left.key_length <= left.key.len and
        std.mem.eql(u8, left.key[0..left.key_length], right.key[0..right.key_length]);
}
fn hash(key: Key) usize {
    var hasher = std.hash.Wyhash.init(0);
    hasher.update(std.mem.asBytes(&key.pid));
    hasher.update(key.key[0..key.key_length]);
    return @truncate(hasher.final());
}
fn pidHash(pid: i32) usize {
    return @truncate(std.hash.Wyhash.hash(0, std.mem.asBytes(&pid)));
}
fn equalRows(left: []const Row, right: []const Row) bool {
    if (left.len != right.len) return false;
    for (left, right) |a, b| {
        if (!same(a.identity(), b.identity()) or a.state != b.state or a.current != b.current or a.growth != b.growth or
            a.seconds != b.seconds or a.rate != b.rate or a.sample_count != b.sample_count or a.first != b.first or
            a.last != b.last or a.payload != b.payload or a.first_offset_minutes != b.first_offset_minutes or a.last_offset_minutes != b.last_offset_minutes) return false;
    }
    return true;
}
fn order(rows: []Row) void {
    // Stable insertion preserves the original dictionary insertion order for
    // equal first-observation timestamps, as Enumerable.OrderBy did.
    if (rows.len < 2) return;
    for (1..rows.len) |index| {
        const row = rows[index];
        var current = index;
        while (current > 0 and rows[current - 1].first > row.first) : (current -= 1) rows[current] = rows[current - 1];
        rows[current] = row;
    }
}
