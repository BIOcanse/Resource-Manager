const std = @import("std");
const protocol = @import("protocol.zig");
const ResultCode = @import("../../common/result_codes.zig").ResultCode;

const no_index = std.math.maxInt(u32);

pub const EntrySlot = struct {
    active: bool = false,
    is_launcher: bool = false,
    entry_handle: u64 = 0,
    attribution_id_handle: u64 = 0,
    display_name_handle: u64 = 0,
    source: u32 = 0,
    software_kind: u32 = 0,
    primary_name_offset: u32 = 0,
    primary_name_length: u32 = 0,
    alias_count: u32 = 0,
    product_alias_count: u32 = 0,
    root_count: u32 = 0,
};

pub const AliasSlot = struct {
    active: bool = false,
    kind: u32 = 0,
    flags: u32 = 0,
    entry_index: u32 = no_index,
    key_offset: u32 = 0,
    key_length: u32 = 0,
    numeric_value: u64 = 0,
};

pub const RootSlot = struct {
    active: bool = false,
    root_handle: u64 = 0,
    entry_index: u32 = no_index,
    key_offset: u32 = 0,
    key_length: u32 = 0,
};

pub const Fingerprint = struct {
    low: u64 = 0,
    high: u64 = 0,
};

pub const Bank = struct {
    entries: []EntrySlot,
    aliases: []AliasSlot,
    roots: []RootSlot,
    key_bytes: []u8,
    entry_index: []u32,
    identity_index: []u32,
    alias_index: []u32,
    root_index: []u32,
    entry_count: u32 = 0,
    alias_count: u32 = 0,
    root_count: u32 = 0,
    key_byte_count: u32 = 0,
    prohibited_alias_count: u32 = 0,
    launcher_token_count: u32 = 0,
    managed_child_rule_count: u32 = 0,
    fingerprint: Fingerprint = .{},

    pub fn init(allocator: std.mem.Allocator, config: *const protocol.Config) !Bank {
        const entries = try allocator.alloc(EntrySlot, config.maximum_entry_count);
        errdefer allocator.free(entries);
        const aliases = try allocator.alloc(AliasSlot, config.maximum_alias_count);
        errdefer allocator.free(aliases);
        const roots = try allocator.alloc(RootSlot, config.maximum_root_count);
        errdefer allocator.free(roots);
        const key_bytes = try allocator.alloc(u8, config.maximum_catalog_key_byte_count);
        errdefer allocator.free(key_bytes);
        const entry_index = try allocator.alloc(u32, config.entry_index_capacity);
        errdefer allocator.free(entry_index);
        const identity_index = try allocator.alloc(u32, config.identity_index_capacity);
        errdefer allocator.free(identity_index);
        const alias_index = try allocator.alloc(u32, config.alias_index_capacity);
        errdefer allocator.free(alias_index);
        const root_index = try allocator.alloc(u32, config.root_index_capacity);
        errdefer allocator.free(root_index);

        var bank = Bank{
            .entries = entries,
            .aliases = aliases,
            .roots = roots,
            .key_bytes = key_bytes,
            .entry_index = entry_index,
            .identity_index = identity_index,
            .alias_index = alias_index,
            .root_index = root_index,
        };
        bank.clear();
        return bank;
    }

    pub fn deinit(self: *Bank, allocator: std.mem.Allocator) void {
        allocator.free(self.root_index);
        allocator.free(self.alias_index);
        allocator.free(self.identity_index);
        allocator.free(self.entry_index);
        allocator.free(self.key_bytes);
        allocator.free(self.roots);
        allocator.free(self.aliases);
        allocator.free(self.entries);
        self.* = undefined;
    }

    fn clear(self: *Bank) void {
        @memset(self.entries, .{});
        @memset(self.aliases, .{});
        @memset(self.roots, .{});
        @memset(self.key_bytes, 0);
        @memset(self.entry_index, no_index);
        @memset(self.identity_index, no_index);
        @memset(self.alias_index, no_index);
        @memset(self.root_index, no_index);
        self.entry_count = 0;
        self.alias_count = 0;
        self.root_count = 0;
        self.key_byte_count = 0;
        self.prohibited_alias_count = 0;
        self.launcher_token_count = 0;
        self.managed_child_rule_count = 0;
        self.fingerprint = .{};
    }

    pub fn load(
        self: *Bank,
        entries: []const protocol.EntryInput,
        aliases: []const protocol.AliasInput,
        roots: []const protocol.RootInput,
        key_bytes: []const u8,
        expected_prohibited_alias_count: u32,
        expected_launcher_token_count: u32,
        expected_managed_child_rule_count: u32,
    ) ResultCode {
        self.clear();
        if (entries.len > self.entries.len or aliases.len > self.aliases.len or
            roots.len > self.roots.len or key_bytes.len > self.key_bytes.len)
        {
            return .out_of_memory;
        }
        @memcpy(self.key_bytes[0..key_bytes.len], key_bytes);

        var expected_key_offset: u32 = 0;
        var previous_entry_handle: u64 = 0;
        for (entries, 0..) |entry, entry_slot_index| {
            if (!protocol.validEntry(&entry, key_bytes) or entry.entry_handle <= previous_entry_handle or
                entry.primary_name_offset != expected_key_offset)
            {
                return .invalid_argument;
            }
            expected_key_offset = std.math.add(u32, expected_key_offset, entry.primary_name_length) catch
                return .invalid_argument;
            previous_entry_handle = entry.entry_handle;
            self.entries[entry_slot_index] = .{
                .active = true,
                .entry_handle = entry.entry_handle,
                .attribution_id_handle = entry.attribution_id_handle,
                .display_name_handle = entry.display_name_handle,
                .source = entry.source,
                .software_kind = entry.software_kind,
                .primary_name_offset = entry.primary_name_offset,
                .primary_name_length = entry.primary_name_length,
            };
            if (!insertEntryIndex(self, entry.entry_handle, @intCast(entry_slot_index)) or
                !insertIdentityIndex(self, entry.attribution_id_handle, @intCast(entry_slot_index)))
            {
                return .invalid_argument;
            }
        }

        var previous_root_handle: u64 = 0;
        for (roots, 0..) |root, root_slot_index| {
            if (!protocol.validRoot(&root, key_bytes) or root.root_handle <= previous_root_handle or
                root.key_offset != expected_key_offset)
            {
                return .invalid_argument;
            }
            expected_key_offset = std.math.add(u32, expected_key_offset, root.key_length) catch
                return .invalid_argument;
            previous_root_handle = root.root_handle;
            const entry_slot_index = self.findEntry(root.entry_handle) orelse return .invalid_argument;
            self.roots[root_slot_index] = .{
                .active = true,
                .root_handle = root.root_handle,
                .entry_index = entry_slot_index,
                .key_offset = root.key_offset,
                .key_length = root.key_length,
            };
            self.entries[entry_slot_index].root_count += 1;
            if (!insertRootIndex(self, root.root_handle, @intCast(root_slot_index))) return .invalid_argument;
        }

        var prohibited_alias_count: u32 = 0;
        var launcher_token_count: u32 = 0;
        var managed_child_rule_count: u32 = 0;
        var previous_alias: ?protocol.AliasInput = null;
        for (aliases, 0..) |alias, alias_slot_index| {
            if (!protocol.validAlias(&alias, key_bytes)) return .invalid_argument;
            const kind = protocol.aliasKind(alias.kind) orelse return .invalid_argument;
            if (kind != .steam_app_id) {
                if (alias.key_offset != expected_key_offset) return .invalid_argument;
                expected_key_offset = std.math.add(u32, expected_key_offset, alias.key_length) catch
                    return .invalid_argument;
            }
            if (previous_alias) |previous| {
                if (!aliasLessThan(&previous, &alias, key_bytes)) return .invalid_argument;
            }
            previous_alias = alias;

            const prohibited = (alias.flags & protocol.AliasFlags.prohibited) != 0;
            const rule = kind == .launcher_token or kind == .managed_child_segment;
            const entry_slot_index = if (prohibited or rule)
                no_index
            else
                self.findEntry(alias.entry_handle) orelse return .invalid_argument;
            self.aliases[alias_slot_index] = .{
                .active = true,
                .kind = alias.kind,
                .flags = alias.flags,
                .entry_index = entry_slot_index,
                .key_offset = alias.key_offset,
                .key_length = alias.key_length,
                .numeric_value = alias.numeric_value,
            };
            if (!rule and !insertAliasIndex(self, @intCast(alias_slot_index))) return .invalid_argument;
            if (prohibited) {
                prohibited_alias_count += 1;
            } else if (kind == .launcher_token) {
                launcher_token_count += 1;
            } else if (kind == .managed_child_segment) {
                managed_child_rule_count += 1;
            } else {
                self.entries[entry_slot_index].alias_count += 1;
                if (kind == .product_name) self.entries[entry_slot_index].product_alias_count += 1;
            }
        }
        if (expected_key_offset != key_bytes.len or
            prohibited_alias_count != expected_prohibited_alias_count or
            launcher_token_count != expected_launcher_token_count or
            managed_child_rule_count != expected_managed_child_rule_count)
        {
            return .invalid_argument;
        }
        self.alias_count = @intCast(aliases.len);
        for (self.entries[0..entries.len]) |*entry| {
            if (entry.alias_count == 0) return .invalid_argument;
            const primary_name = protocol.keySlice(
                self.key_bytes,
                entry.primary_name_offset,
                entry.primary_name_length,
            ) orelse return .invalid_argument;
            entry.is_launcher = self.matchesRule(.launcher_token, primary_name);
        }

        self.entry_count = @intCast(entries.len);
        self.root_count = @intCast(roots.len);
        self.key_byte_count = @intCast(key_bytes.len);
        self.prohibited_alias_count = prohibited_alias_count;
        self.launcher_token_count = launcher_token_count;
        self.managed_child_rule_count = managed_child_rule_count;
        self.fingerprint = catalogFingerprint(entries, aliases, roots, key_bytes);
        return .ok;
    }

    pub fn findEntry(self: *const Bank, handle: u64) ?u32 {
        const mask = self.entry_index.len - 1;
        var probe = hashU64(handle) & mask;
        var remaining = self.entry_index.len;
        while (remaining != 0) : (remaining -= 1) {
            const slot_index = self.entry_index[probe];
            if (slot_index == no_index) return null;
            if (self.entries[slot_index].entry_handle == handle) return slot_index;
            probe = (probe + 1) & mask;
        }
        return null;
    }

    pub fn findAlias(self: *const Bank, kind: protocol.AliasKind, numeric: u64, key: []const u8) ?u32 {
        const mask = self.alias_index.len - 1;
        var probe = aliasHash(kind, numeric, key) & mask;
        var remaining = self.alias_index.len;
        while (remaining != 0) : (remaining -= 1) {
            const slot_index = self.alias_index[probe];
            if (slot_index == no_index) return null;
            const alias = &self.aliases[slot_index];
            if (aliasMatches(self, alias, kind, numeric, key)) {
                return if ((alias.flags & protocol.AliasFlags.prohibited) != 0)
                    null
                else
                    alias.entry_index;
            }
            probe = (probe + 1) & mask;
        }
        return null;
    }

    pub fn matchesRule(self: *const Bank, kind: protocol.AliasKind, candidate: []const u8) bool {
        for (self.aliases[0..self.alias_count]) |alias| {
            if (alias.kind != @intFromEnum(kind)) continue;
            const rule = protocol.keySlice(self.key_bytes, alias.key_offset, alias.key_length) orelse continue;
            if (std.mem.indexOf(u8, candidate, rule) != null) return true;
        }
        return false;
    }

    pub fn matchesExactRule(self: *const Bank, kind: protocol.AliasKind, candidate: []const u8) bool {
        for (self.aliases[0..self.alias_count]) |alias| {
            if (alias.kind != @intFromEnum(kind)) continue;
            const rule = protocol.keySlice(self.key_bytes, alias.key_offset, alias.key_length) orelse continue;
            if (std.mem.eql(u8, candidate, rule)) return true;
        }
        return false;
    }
};

fn insertEntryIndex(bank: *Bank, handle: u64, slot_index: u32) bool {
    const mask = bank.entry_index.len - 1;
    var probe = hashU64(handle) & mask;
    var remaining = bank.entry_index.len;
    while (remaining != 0) : (remaining -= 1) {
        if (bank.entry_index[probe] == no_index) {
            bank.entry_index[probe] = slot_index;
            return true;
        }
        if (bank.entries[bank.entry_index[probe]].entry_handle == handle) return false;
        probe = (probe + 1) & mask;
    }
    return false;
}

fn insertIdentityIndex(bank: *Bank, handle: u64, slot_index: u32) bool {
    const mask = bank.identity_index.len - 1;
    var probe = hashU64(handle) & mask;
    var remaining = bank.identity_index.len;
    while (remaining != 0) : (remaining -= 1) {
        if (bank.identity_index[probe] == no_index) {
            bank.identity_index[probe] = slot_index;
            return true;
        }
        if (bank.entries[bank.identity_index[probe]].attribution_id_handle == handle) return false;
        probe = (probe + 1) & mask;
    }
    return false;
}

fn insertRootIndex(bank: *Bank, handle: u64, slot_index: u32) bool {
    const mask = bank.root_index.len - 1;
    var probe = hashU64(handle) & mask;
    var remaining = bank.root_index.len;
    while (remaining != 0) : (remaining -= 1) {
        if (bank.root_index[probe] == no_index) {
            bank.root_index[probe] = slot_index;
            return true;
        }
        if (bank.roots[bank.root_index[probe]].root_handle == handle) return false;
        probe = (probe + 1) & mask;
    }
    return false;
}

fn insertAliasIndex(bank: *Bank, slot_index: u32) bool {
    const alias = &bank.aliases[slot_index];
    const kind = protocol.aliasKind(alias.kind) orelse return false;
    const key = if (kind == .steam_app_id)
        &.{}
    else
        protocol.keySlice(bank.key_bytes, alias.key_offset, alias.key_length) orelse return false;
    const mask = bank.alias_index.len - 1;
    var probe = aliasHash(kind, alias.numeric_value, key) & mask;
    var remaining = bank.alias_index.len;
    while (remaining != 0) : (remaining -= 1) {
        const existing_index = bank.alias_index[probe];
        if (existing_index == no_index) {
            bank.alias_index[probe] = slot_index;
            return true;
        }
        if (aliasMatches(bank, &bank.aliases[existing_index], kind, alias.numeric_value, key)) return false;
        probe = (probe + 1) & mask;
    }
    return false;
}

fn aliasMatches(
    bank: *const Bank,
    alias: *const AliasSlot,
    kind: protocol.AliasKind,
    numeric: u64,
    key: []const u8,
) bool {
    if (alias.kind != @intFromEnum(kind)) return false;
    if (kind == .steam_app_id) return alias.numeric_value == numeric;
    const stored = protocol.keySlice(bank.key_bytes, alias.key_offset, alias.key_length) orelse return false;
    return std.mem.eql(u8, stored, key);
}

fn aliasLessThan(
    left: *const protocol.AliasInput,
    right: *const protocol.AliasInput,
    key_bytes: []const u8,
) bool {
    if (left.kind != right.kind) return left.kind < right.kind;
    const kind = protocol.aliasKind(left.kind) orelse return false;
    if (kind == .steam_app_id) {
        if (left.numeric_value != right.numeric_value) return left.numeric_value < right.numeric_value;
    } else {
        const left_key = protocol.keySlice(key_bytes, left.key_offset, left.key_length) orelse return false;
        const right_key = protocol.keySlice(key_bytes, right.key_offset, right.key_length) orelse return false;
        switch (std.mem.order(u8, left_key, right_key)) {
            .lt => return true,
            .gt => return false,
            .eq => {},
        }
    }
    return left.entry_handle < right.entry_handle;
}

fn catalogFingerprint(
    entries: []const protocol.EntryInput,
    aliases: []const protocol.AliasInput,
    roots: []const protocol.RootInput,
    key_bytes: []const u8,
) Fingerprint {
    var low: u64 = 0xcbf2_9ce4_8422_2325;
    var high: u64 = 0x6c62_272e_07bb_0142;
    mix(&low, &high, entries.len);
    mix(&low, &high, aliases.len);
    mix(&low, &high, roots.len);
    mix(&low, &high, key_bytes.len);
    for (entries) |entry| {
        mix(&low, &high, entry.entry_handle);
        mix(&low, &high, entry.attribution_id_handle);
        mix(&low, &high, entry.display_name_handle);
        mix(&low, &high, entry.source);
        mix(&low, &high, entry.software_kind);
        const key = protocol.keySlice(key_bytes, entry.primary_name_offset, entry.primary_name_length) orelse &.{};
        for (key) |value| mix(&low, &high, value);
    }
    for (roots) |root| {
        mix(&low, &high, root.root_handle);
        mix(&low, &high, root.entry_handle);
        const key = protocol.keySlice(key_bytes, root.key_offset, root.key_length) orelse &.{};
        for (key) |value| mix(&low, &high, value);
    }
    for (aliases) |alias| {
        mix(&low, &high, alias.kind);
        mix(&low, &high, alias.entry_handle);
        mix(&low, &high, alias.flags);
        mix(&low, &high, alias.numeric_value);
        if (alias.key_length != 0) {
            const key = protocol.keySlice(key_bytes, alias.key_offset, alias.key_length) orelse &.{};
            for (key) |value| mix(&low, &high, value);
        }
    }
    return .{ .low = low, .high = high };
}

fn mix(low: *u64, high: *u64, value: anytype) void {
    const converted: u64 = @intCast(value);
    low.* = (low.* ^ converted) *% 0x0000_0100_0000_01b3;
    high.* = (high.* ^ rotateLeft(converted +% 0x9e37_79b9_7f4a_7c15, 23)) *%
        0xc2b2_ae3d_27d4_eb4f;
}

fn aliasHash(kind: protocol.AliasKind, numeric: u64, key: []const u8) usize {
    var hash: u64 = 0xcbf2_9ce4_8422_2325;
    hash = (hash ^ @intFromEnum(kind)) *% 0x0000_0100_0000_01b3;
    if (kind == .steam_app_id) {
        hash = (hash ^ numeric) *% 0x0000_0100_0000_01b3;
    } else {
        for (key) |value| hash = (hash ^ value) *% 0x0000_0100_0000_01b3;
    }
    return @truncate(hash);
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

fn rotateLeft(value: u64, offset: u6) u64 {
    return std.math.rotl(u64, value, offset);
}
