const std = @import("std");
const protocol = @import("protocol.zig");
const ResultCode = @import("../../common/result_codes.zig").ResultCode;

pub const no_index = std.math.maxInt(u32);
const tombstone_index = no_index - 1;

pub const RegistrationSlot = struct {
    occupied: bool = false,
    software_handle: u64 = 0,
    catalog_entry_handle: u64 = 0,
    display_name_handle: u64 = 0,
    software_kind_handle: u64 = 0,
    path_count: u32 = 0,
};

pub const PathSlot = struct {
    occupied: bool = false,
    delete_pending: bool = false,
    dirty: bool = false,
    identity_confirmed: bool = false,
    root_confirmed: bool = false,
    registration_index: u32 = no_index,
    path_handle: u64 = 0,
    root_handle: u64 = 0,
    first_observed_utc_ms: i64 = 0,
    executable_path_length: u32 = 0,
    root_path_length: u32 = 0,
    mutation_version: u64 = 0,
};

pub const Bank = struct {
    registrations: []RegistrationSlot,
    paths: []PathSlot,
    executable_path_bytes: []u8,
    root_path_bytes: []u8,
    registration_index: []u32,
    path_index: []u32,
    maximum_executable_path_byte_count: u32,
    maximum_root_path_byte_count: u32,
    registration_count: u32 = 0,
    path_count: u32 = 0,

    pub fn init(allocator: std.mem.Allocator, config: *const protocol.Config) !Bank {
        const registrations = try allocator.alloc(RegistrationSlot, config.maximum_registration_count);
        errdefer allocator.free(registrations);
        const paths = try allocator.alloc(PathSlot, config.maximum_path_count);
        errdefer allocator.free(paths);
        const executable_bytes = try allocator.alloc(
            u8,
            try std.math.mul(usize, config.maximum_path_count, config.maximum_executable_path_byte_count),
        );
        errdefer allocator.free(executable_bytes);
        const root_bytes = try allocator.alloc(
            u8,
            try std.math.mul(usize, config.maximum_path_count, config.maximum_root_path_byte_count),
        );
        errdefer allocator.free(root_bytes);
        const registration_index = try allocator.alloc(u32, config.registration_index_capacity);
        errdefer allocator.free(registration_index);
        const path_index = try allocator.alloc(u32, config.path_index_capacity);
        errdefer allocator.free(path_index);

        var bank = Bank{
            .registrations = registrations,
            .paths = paths,
            .executable_path_bytes = executable_bytes,
            .root_path_bytes = root_bytes,
            .registration_index = registration_index,
            .path_index = path_index,
            .maximum_executable_path_byte_count = config.maximum_executable_path_byte_count,
            .maximum_root_path_byte_count = config.maximum_root_path_byte_count,
        };
        bank.clear();
        return bank;
    }

    pub fn deinit(self: *Bank, allocator: std.mem.Allocator) void {
        allocator.free(self.path_index);
        allocator.free(self.registration_index);
        allocator.free(self.root_path_bytes);
        allocator.free(self.executable_path_bytes);
        allocator.free(self.paths);
        allocator.free(self.registrations);
        self.* = undefined;
    }

    pub fn clear(self: *Bank) void {
        @memset(self.registrations, .{});
        @memset(self.paths, .{});
        @memset(self.executable_path_bytes, 0);
        @memset(self.root_path_bytes, 0);
        @memset(self.registration_index, no_index);
        @memset(self.path_index, no_index);
        self.registration_count = 0;
        self.path_count = 0;
    }

    pub fn loadPersisted(
        self: *Bank,
        config: *const protocol.Config,
        input: *const protocol.ImportInput,
        rows: []const protocol.PersistedPathInput,
        key_bytes: []const u8,
    ) ResultCode {
        self.clear();
        if (rows.len != input.row_count or key_bytes.len != input.key_byte_count) return .invalid_argument;
        var expected_key_offset: u32 = 0;
        var previous_software: u64 = 0;
        var previous_path: u64 = 0;
        for (rows) |*row| {
            if (!protocol.validPersistedPath(row, config, key_bytes)) return .invalid_argument;
            if (!protocol.withinFutureSkew(
                input.command_utc_ms,
                row.first_observed_utc_ms,
                config.maximum_future_skew_ms,
            )) {
                return .invalid_argument;
            }
            if (row.executable_path_offset != expected_key_offset) return .invalid_argument;
            expected_key_offset = std.math.add(u32, expected_key_offset, row.executable_path_length) catch
                return .invalid_argument;
            if (row.root_path_offset != expected_key_offset) return .invalid_argument;
            expected_key_offset = std.math.add(u32, expected_key_offset, row.root_path_length) catch
                return .invalid_argument;
            if (row.software_handle < previous_software or
                (row.software_handle == previous_software and row.path_handle <= previous_path))
            {
                return .invalid_argument;
            }
            previous_path = if (row.software_handle == previous_software) row.path_handle else row.path_handle;
            previous_software = row.software_handle;
            if ((row.flags & protocol.PathFlags.root_confirmed) != 0 and
                (row.flags & protocol.PathFlags.identity_confirmed) == 0)
            {
                return .invalid_argument;
            }
            const executable = protocol.keySlice(key_bytes, row.executable_path_offset, row.executable_path_length).?;
            const root = protocol.keySlice(key_bytes, row.root_path_offset, row.root_path_length).?;
            if (!protocol.isSameOrUnder(executable, root) or !self.rootHandleCompatible(row.root_handle, root)) {
                return .invalid_argument;
            }

            const registration_slot = self.findRegistration(row.software_handle) orelse blk: {
                const slot = self.allocateRegistration() orelse return .out_of_memory;
                self.registrations[slot] = .{
                    .occupied = true,
                    .software_handle = row.software_handle,
                    .catalog_entry_handle = row.catalog_entry_handle,
                    .display_name_handle = row.display_name_handle,
                    .software_kind_handle = row.software_kind_handle,
                };
                if (!insertIndex(self.registration_index, self.registrations, row.software_handle, slot)) {
                    return .invalid_argument;
                }
                self.registration_count += 1;
                break :blk slot;
            };
            const registration = &self.registrations[registration_slot];
            if (registration.catalog_entry_handle != row.catalog_entry_handle or
                registration.display_name_handle != row.display_name_handle or
                registration.software_kind_handle != row.software_kind_handle)
            {
                return .invalid_argument;
            }
            if (self.findPath(row.path_handle) != null) return .invalid_argument;
            const path_slot = self.allocatePath() orelse return .out_of_memory;
            self.paths[path_slot] = .{
                .occupied = true,
                .identity_confirmed = (row.flags & protocol.PathFlags.identity_confirmed) != 0,
                .root_confirmed = (row.flags & protocol.PathFlags.root_confirmed) != 0,
                .registration_index = registration_slot,
                .path_handle = row.path_handle,
                .root_handle = row.root_handle,
                .first_observed_utc_ms = row.first_observed_utc_ms,
                .executable_path_length = row.executable_path_length,
                .root_path_length = row.root_path_length,
            };
            self.writeExecutablePath(path_slot, executable);
            self.writeRootPath(path_slot, root);
            if (!insertPathIndex(self.path_index, self.paths, row.path_handle, path_slot)) return .invalid_argument;
            registration.path_count += 1;
            self.path_count += 1;
        }
        if (expected_key_offset != key_bytes.len) return .invalid_argument;
        return .ok;
    }

    pub fn findRegistration(self: *const Bank, software_handle: u64) ?u32 {
        if (software_handle == 0) return null;
        const mask = self.registration_index.len - 1;
        var probe = hashU64(software_handle) & mask;
        var remaining = self.registration_index.len;
        while (remaining != 0) : (remaining -= 1) {
            const slot = self.registration_index[probe];
            if (slot == no_index) return null;
            if (slot != tombstone_index and self.registrations[slot].occupied and
                self.registrations[slot].software_handle == software_handle)
            {
                return slot;
            }
            probe = (probe + 1) & mask;
        }
        return null;
    }

    pub fn findPath(self: *const Bank, path_handle: u64) ?u32 {
        if (path_handle == 0) return null;
        const mask = self.path_index.len - 1;
        var probe = hashU64(path_handle) & mask;
        var remaining = self.path_index.len;
        while (remaining != 0) : (remaining -= 1) {
            const slot = self.path_index[probe];
            if (slot == no_index) return null;
            if (slot != tombstone_index and self.paths[slot].occupied and self.paths[slot].path_handle == path_handle) {
                return slot;
            }
            probe = (probe + 1) & mask;
        }
        return null;
    }

    pub fn createRegistration(
        self: *Bank,
        software_handle: u64,
        catalog_entry_handle: u64,
        display_name_handle: u64,
        software_kind_handle: u64,
    ) ?u32 {
        const slot = self.allocateRegistration() orelse return null;
        self.registrations[slot] = .{
            .occupied = true,
            .software_handle = software_handle,
            .catalog_entry_handle = catalog_entry_handle,
            .display_name_handle = display_name_handle,
            .software_kind_handle = software_kind_handle,
        };
        if (!insertIndex(self.registration_index, self.registrations, software_handle, slot)) {
            self.registrations[slot] = .{};
            return null;
        }
        self.registration_count += 1;
        return slot;
    }

    pub fn createPath(
        self: *Bank,
        registration_index: u32,
        path_handle: u64,
        root_handle: u64,
        executable_path: []const u8,
        root_path: []const u8,
        first_observed_utc_ms: i64,
        identity_confirmed: bool,
        root_confirmed: bool,
    ) ?u32 {
        const slot = self.allocatePath() orelse return null;
        self.paths[slot] = .{
            .occupied = true,
            .identity_confirmed = identity_confirmed,
            .root_confirmed = root_confirmed,
            .registration_index = registration_index,
            .path_handle = path_handle,
            .root_handle = root_handle,
            .first_observed_utc_ms = first_observed_utc_ms,
            .executable_path_length = @intCast(executable_path.len),
            .root_path_length = @intCast(root_path.len),
        };
        self.writeExecutablePath(slot, executable_path);
        self.writeRootPath(slot, root_path);
        if (!insertPathIndex(self.path_index, self.paths, path_handle, slot)) {
            self.paths[slot] = .{};
            return null;
        }
        self.registrations[registration_index].path_count += 1;
        self.path_count += 1;
        return slot;
    }

    pub fn removePath(self: *Bank, path_slot: u32) void {
        const path = self.paths[path_slot];
        removePathIndex(self.path_index, self.paths, path.path_handle);
        self.paths[path_slot] = .{};
        @memset(self.executableStorage(path_slot), 0);
        @memset(self.rootStorage(path_slot), 0);
        const registration = &self.registrations[path.registration_index];
        registration.path_count -= 1;
        self.path_count -= 1;
        if (registration.path_count == 0) self.removeRegistration(path.registration_index);
    }

    pub fn executablePath(self: *const Bank, path_slot: u32) []const u8 {
        const path = &self.paths[path_slot];
        const start = @as(usize, path_slot) * self.maximum_executable_path_byte_count;
        return self.executable_path_bytes[start .. start + path.executable_path_length];
    }

    pub fn rootPath(self: *const Bank, path_slot: u32) []const u8 {
        const path = &self.paths[path_slot];
        const start = @as(usize, path_slot) * self.maximum_root_path_byte_count;
        return self.root_path_bytes[start .. start + path.root_path_length];
    }

    pub fn writeRootPath(self: *Bank, path_slot: u32, root_path: []const u8) void {
        const storage = self.rootStorage(path_slot);
        @memset(storage, 0);
        @memcpy(storage[0..root_path.len], root_path);
        self.paths[path_slot].root_path_length = @intCast(root_path.len);
    }

    pub fn rootHandleCompatible(self: *const Bank, root_handle: u64, root_path: []const u8) bool {
        for (self.paths, 0..) |path, index| {
            if (!path.occupied or path.root_handle != root_handle) continue;
            const slot: u32 = @intCast(index);
            return std.mem.eql(u8, self.rootPath(slot), root_path);
        }
        return true;
    }

    fn writeExecutablePath(self: *Bank, path_slot: u32, executable_path: []const u8) void {
        const storage = self.executableStorage(path_slot);
        @memset(storage, 0);
        @memcpy(storage[0..executable_path.len], executable_path);
        self.paths[path_slot].executable_path_length = @intCast(executable_path.len);
    }

    fn executableStorage(self: *Bank, path_slot: u32) []u8 {
        const start = @as(usize, path_slot) * self.maximum_executable_path_byte_count;
        return self.executable_path_bytes[start .. start + self.maximum_executable_path_byte_count];
    }

    fn rootStorage(self: *Bank, path_slot: u32) []u8 {
        const start = @as(usize, path_slot) * self.maximum_root_path_byte_count;
        return self.root_path_bytes[start .. start + self.maximum_root_path_byte_count];
    }

    fn allocateRegistration(self: *const Bank) ?u32 {
        for (self.registrations, 0..) |registration, index| if (!registration.occupied) return @intCast(index);
        return null;
    }

    fn allocatePath(self: *const Bank) ?u32 {
        for (self.paths, 0..) |path, index| if (!path.occupied) return @intCast(index);
        return null;
    }

    fn removeRegistration(self: *Bank, registration_slot: u32) void {
        const software_handle = self.registrations[registration_slot].software_handle;
        removeIndex(self.registration_index, self.registrations, software_handle);
        self.registrations[registration_slot] = .{};
        self.registration_count -= 1;
    }
};

fn insertIndex(index: []u32, slots: []const RegistrationSlot, handle: u64, slot: u32) bool {
    const mask = index.len - 1;
    var probe = hashU64(handle) & mask;
    var first_tombstone: ?usize = null;
    var remaining = index.len;
    while (remaining != 0) : (remaining -= 1) {
        const existing = index[probe];
        if (existing == tombstone_index) {
            if (first_tombstone == null) first_tombstone = probe;
        } else if (existing == no_index) {
            index[first_tombstone orelse probe] = slot;
            return true;
        } else if (slots[existing].software_handle == handle) return false;
        probe = (probe + 1) & mask;
    }
    if (first_tombstone) |target| {
        index[target] = slot;
        return true;
    }
    return false;
}

fn insertPathIndex(index: []u32, slots: []const PathSlot, handle: u64, slot: u32) bool {
    const mask = index.len - 1;
    var probe = hashU64(handle) & mask;
    var first_tombstone: ?usize = null;
    var remaining = index.len;
    while (remaining != 0) : (remaining -= 1) {
        const existing = index[probe];
        if (existing == tombstone_index) {
            if (first_tombstone == null) first_tombstone = probe;
        } else if (existing == no_index) {
            index[first_tombstone orelse probe] = slot;
            return true;
        } else if (slots[existing].path_handle == handle) return false;
        probe = (probe + 1) & mask;
    }
    if (first_tombstone) |target| {
        index[target] = slot;
        return true;
    }
    return false;
}

fn removeIndex(index: []u32, slots: []const RegistrationSlot, handle: u64) void {
    const mask = index.len - 1;
    var probe = hashU64(handle) & mask;
    var remaining = index.len;
    while (remaining != 0) : (remaining -= 1) {
        const existing = index[probe];
        if (existing == no_index) return;
        if (existing != tombstone_index and slots[existing].software_handle == handle) {
            index[probe] = tombstone_index;
            return;
        }
        probe = (probe + 1) & mask;
    }
}

fn removePathIndex(index: []u32, slots: []const PathSlot, handle: u64) void {
    const mask = index.len - 1;
    var probe = hashU64(handle) & mask;
    var remaining = index.len;
    while (remaining != 0) : (remaining -= 1) {
        const existing = index[probe];
        if (existing == no_index) return;
        if (existing != tombstone_index and slots[existing].path_handle == handle) {
            index[probe] = tombstone_index;
            return;
        }
        probe = (probe + 1) & mask;
    }
}

fn hashU64(value: u64) usize {
    var result = value;
    result ^= result >> 33;
    result *%= 0xff51_afd7_ed55_8ccd;
    result ^= result >> 33;
    result *%= 0xc4ce_b9fe_1a85_ec53;
    result ^= result >> 33;
    return @truncate(result);
}
