const std = @import("std");
const declarations = @import("declarations.zig");
const types = @import("types.zig");

pub const ItemKey = types.ItemKey;

/// Reads never sample, block, or allocate. The three empty cases preserve why
/// the owner cannot currently supply a value.
pub const Read = types.Read;
pub const Presence = types.Presence;

pub fn eql(a: Read, b: Read) bool {
    return std.meta.eql(a, b);
}

fn encode(value: Read) u8 {
    return switch (value) {
        .not_published => 0,
        .owner_stopped => 1,
        .read_failed => 2,
        .value => |present| if (present) 4 else 3,
    };
}
fn decode(value: u8) Read {
    return switch (value) {
        0 => .not_published,
        1 => .owner_stopped,
        2 => .read_failed,
        3 => .{ .value = false },
        4 => .{ .value = true },
        else => unreachable,
    };
}

/// An opaque capability issued exactly once for an item's declared storage.
/// The controller keeps it opaque at the host boundary.
pub const Claim = *opaque {};
pub const Entry = struct {
    key: ItemKey,
    declaration: declarations.Item,
    claimed: bool = false,
    bound: bool = true,
    current: std.atomic.Value(u8) = .init(0),
    cell_value: Read = .not_published,
    read_function: ?types.ReadFunction = null,

    pub fn readCurrent(self: *const Entry) Read {
        return decode(self.current.load(.acquire));
    }
};

/// A round retains this binding, not a second copy of the owner's state.
pub const Capture = union(enum) {
    publication: Read,
    function: types.ReadFunction,
    pub fn read(self: Capture) Read {
        return switch (self) {
            .publication => |value| value,
            .function => |function| blk: {
                var value: types.ReadView = .{ .kind = 2, .value = 0 };
                function.read(function.context, &value);
                break :blk value.toRead();
            },
        };
    }
};

/// Current values and sole-owner claims. Publications load one atomic byte.
/// The controller synchronizes captures and mutations; it resolves captured
/// read functions outside its lock and owns the event points after mutations.
pub const State = struct {
    allocator: std.mem.Allocator,
    entries: []Entry,

    pub fn init(allocator: std.mem.Allocator, registry: declarations.Registry) !State {
        var count: usize = 0;
        for (registry.items) |item| count += if (item.per_block) registry.blocks.len else 1;
        const entries = try allocator.alloc(Entry, count);
        var next: usize = 0;
        for (registry.items) |item| {
            if (item.per_block) {
                for (registry.blocks) |block| {
                    entries[next] = .{ .key = .{ .item = item.uid, .block = block.uid }, .declaration = item };
                    next += 1;
                }
            } else {
                entries[next] = .{ .key = .{ .item = item.uid }, .declaration = item };
                next += 1;
            }
        }
        return .{ .allocator = allocator, .entries = entries };
    }

    pub fn deinit(self: *State) void {
        self.allocator.free(self.entries);
        self.* = undefined;
    }
    pub fn indexOf(self: *const State, key: ItemKey) !usize {
        for (self.entries, 0..) |*entry, index| {
            if (entry.key.item == key.item and entry.key.block == key.block) return index;
        }
        return error.UnregisteredItem;
    }
    pub fn claim(self: *State, key: ItemKey, storage: declarations.Storage) !Claim {
        const index = try self.indexOf(key);
        const entry = &self.entries[index];
        if (entry.declaration.storage != storage) return error.StorageMismatch;
        if (entry.claimed) return error.AlreadyClaimed;
        entry.claimed = true;
        return @ptrCast(entry);
    }
    pub fn claimWrite(self: *State, key: ItemKey) !Claim {
        return self.claim(key, .own);
    }
    pub fn claimCell(self: *State, key: ItemKey) !Claim {
        return self.claim(key, .reference);
    }
    pub fn claimReadFunction(self: *State, key: ItemKey, function: types.ReadFunction) !Claim {
        const entry = &self.entries[try self.indexOf(key)];
        if (entry.declaration.value_type != .bool) return error.TypeMismatch;
        const capability = try self.claim(key, .reference);
        entry.read_function = function;
        return capability;
    }
    pub fn capture(self: *const State, key: ItemKey) !Capture {
        const entry = &self.entries[try self.indexOf(key)];
        if (entry.declaration.value_type != .bool) return error.TypeMismatch;
        if (entry.read_function) |function| {
            if (!entry.bound) return .{ .publication = .owner_stopped };
            return .{ .function = function };
        }
        return .{ .publication = entry.readCurrent() };
    }
    pub fn read(self: *const State, key: ItemKey) !Read {
        return (try self.capture(key)).read();
    }
    pub fn readPresence(self: *const State, key: ItemKey) !Presence {
        const entry = &self.entries[try self.indexOf(key)];
        if (entry.declaration.value_type != .host) return error.TypeMismatch;
        return switch (entry.readCurrent()) {
            .value => .present,
            .not_published => .not_published,
            .owner_stopped => .owner_stopped,
            .read_failed => .read_failed,
        };
    }
    pub fn claimIndex(self: *const State, capability: Claim) !usize {
        const address = @intFromPtr(capability);
        const start = @intFromPtr(self.entries.ptr);
        const byte_length = self.entries.len * @sizeOf(Entry);
        if (address < start or address - start >= byte_length) return error.InvalidClaim;
        const offset = address - start;
        if (offset % @sizeOf(Entry) != 0) return error.InvalidClaim;
        const index = offset / @sizeOf(Entry);
        if (!self.entries[index].claimed) return error.InvalidClaim;
        return index;
    }
    fn claimedEntry(self: *State, capability: Claim) !*Entry {
        return &self.entries[try self.claimIndex(capability)];
    }
    pub fn publish(self: *State, capability: Claim, value: Read) !bool {
        const entry = try self.claimedEntry(capability);
        if (entry.declaration.value_type != .bool) return error.TypeMismatch;
        if (entry.read_function != null) return error.StorageMismatch;
        return publishCurrent(entry, value);
    }
    pub fn publishPresence(self: *State, capability: Claim, value: Presence) !bool {
        const entry = try self.claimedEntry(capability);
        if (entry.declaration.value_type != .host) return error.TypeMismatch;
        const publication: Read = switch (value) {
            .present => .{ .value = true },
            .not_published => .not_published,
            .owner_stopped => .owner_stopped,
            .read_failed => .read_failed,
        };
        return publishCurrent(entry, publication);
    }
    fn publishCurrent(entry: *Entry, value: Read) bool {
        if (entry.declaration.storage == .reference) {
            entry.cell_value = value;
            if (!entry.bound) return false;
        }
        const changed = !eql(entry.readCurrent(), value);
        entry.current.store(encode(value), .release);
        return changed;
    }
    pub fn bind(self: *State, capability: Claim) !bool {
        const entry = try self.claimedEntry(capability);
        if (entry.declaration.storage != .reference) return error.StorageMismatch;
        entry.bound = true;
        const changed = !eql(entry.readCurrent(), entry.cell_value);
        entry.current.store(encode(entry.cell_value), .release);
        return changed;
    }
    pub fn unbind(self: *State, capability: Claim) !bool {
        const entry = try self.claimedEntry(capability);
        if (entry.declaration.storage != .reference) return error.StorageMismatch;
        entry.bound = false;
        const changed = !eql(entry.readCurrent(), .owner_stopped);
        entry.current.store(encode(.owner_stopped), .release);
        return changed;
    }
};
