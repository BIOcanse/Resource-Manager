const std = @import("std");
const index = @import("index.zig");
const protocol = @import("protocol.zig");

pub const Error = error{
    InvalidArgument,
    CapacityExceeded,
    ResidentBudgetExceeded,
};

const CapabilitySlot = struct {
    row: protocol.CapabilityInput = std.mem.zeroes(protocol.CapabilityInput),
};

const RouteSlot = struct {
    row: protocol.RouteInput = std.mem.zeroes(protocol.RouteInput),
};

const ModelSlot = struct {
    row: protocol.ModelInput = std.mem.zeroes(protocol.ModelInput),
};

const AliasSlot = struct {
    row: protocol.ModelAliasInput = std.mem.zeroes(protocol.ModelAliasInput),
};

pub const capability_slot_size: u64 = @sizeOf(CapabilitySlot);
pub const route_slot_size: u64 = @sizeOf(RouteSlot);
pub const model_slot_size: u64 = @sizeOf(ModelSlot);
pub const alias_slot_size: u64 = @sizeOf(AliasSlot);

pub const CatalogBank = struct {
    capabilities: []CapabilitySlot,
    routes: []RouteSlot,
    capability_index: []index.HandleEntry,
    text: []u8,
    capability_count: usize = 0,
    route_count: usize = 0,
    text_count: usize = 0,
    generation: u64 = 0,
    service_enabled: bool = false,

    pub fn init(allocator: std.mem.Allocator, config: protocol.Config) !CatalogBank {
        const capabilities = try allocator.alloc(CapabilitySlot, config.maximum_capability_count);
        errdefer allocator.free(capabilities);
        const routes = try allocator.alloc(RouteSlot, config.maximum_route_count);
        errdefer allocator.free(routes);
        const capability_index = try allocator.alloc(index.HandleEntry, config.capability_index_capacity);
        errdefer allocator.free(capability_index);
        const text = try allocator.alloc(u8, config.maximum_catalog_text_bytes);
        errdefer allocator.free(text);

        @memset(capabilities, .{});
        @memset(routes, .{});
        index.clear(index.HandleEntry, capability_index);
        @memset(text, 0);
        return .{
            .capabilities = capabilities,
            .routes = routes,
            .capability_index = capability_index,
            .text = text,
        };
    }

    pub fn deinit(self: *CatalogBank, allocator: std.mem.Allocator) void {
        allocator.free(self.capabilities);
        allocator.free(self.routes);
        allocator.free(self.capability_index);
        allocator.free(self.text);
        self.* = undefined;
    }

    pub fn reset(self: *CatalogBank) void {
        @memset(self.capabilities, .{});
        @memset(self.routes, .{});
        index.clear(index.HandleEntry, self.capability_index);
        @memset(self.text, 0);
        self.capability_count = 0;
        self.route_count = 0;
        self.text_count = 0;
        self.generation = 0;
        self.service_enabled = false;
    }

    pub fn stage(
        self: *CatalogBank,
        header: protocol.CatalogReplaceInput,
        capabilities: []const protocol.CapabilityInput,
        routes: []const protocol.RouteInput,
        text: []const u8,
    ) Error!void {
        self.reset();
        if (header.struct_size != @sizeOf(protocol.CatalogReplaceInput) or
            header.reserved_u32 != 0 or
            !protocol.allZero([3]u64, header.reserved) or
            (header.service_enabled != 0 and header.service_enabled != 1) or
            header.catalog_generation == 0 or
            header.capability_count != capabilities.len or
            header.route_count != routes.len or
            header.text_byte_count != text.len)
        {
            return error.InvalidArgument;
        }
        if (capabilities.len > self.capabilities.len or
            routes.len > self.routes.len or
            text.len > self.text.len)
        {
            return error.CapacityExceeded;
        }
        if (!std.unicode.utf8ValidateSlice(text)) return error.InvalidArgument;

        @memcpy(self.text[0..text.len], text);
        self.text_count = text.len;

        for (capabilities, 0..) |row, slot| {
            if (row.struct_size != @sizeOf(protocol.CapabilityInput) or
                row.capability_handle == 0 or
                row.payload_handle == 0 or
                row.flags & ~protocol.CapabilityFlags.known != 0 or
                row.reserved != 0 or
                !index.insertHandle(self.capability_index, row.capability_handle, slot))
            {
                return error.InvalidArgument;
            }
            self.capabilities[slot].row = row;
        }

        for (routes, 0..) |row, slot| {
            if (row.struct_size != @sizeOf(protocol.RouteInput) or
                row.route_handle == 0 or
                row.payload_handle == 0 or
                row.flags & ~protocol.RouteFlags.known != 0 or
                row.method_mask == 0 or
                row.method_mask & ~protocol.MethodMask.known != 0 or
                row.reserved_u32 != 0 or
                row.reserved != 0 or
                !validSpan(row.path, self.text_count) or
                !validRoutePath(self.span(row.path)))
            {
                return error.InvalidArgument;
            }
            const catalog_route = row.flags & protocol.RouteFlags.catalog_route != 0;
            if ((catalog_route and row.capability_handle != 0) or
                (!catalog_route and index.findHandle(self.capability_index, row.capability_handle) == null))
            {
                return error.InvalidArgument;
            }
            for (routes[0..slot]) |existing| {
                if (existing.route_handle == row.route_handle) return error.InvalidArgument;
                if (existing.method_mask & row.method_mask != 0 and
                    std.mem.eql(u8, self.span(existing.path), self.span(row.path)))
                {
                    return error.InvalidArgument;
                }
            }
            self.routes[slot].row = row;
        }

        self.capability_count = capabilities.len;
        self.route_count = routes.len;
        self.generation = header.catalog_generation;
        self.service_enabled = header.service_enabled != 0;
    }

    pub fn capability(self: *const CatalogBank, handle: u64) ?protocol.CapabilityInput {
        const slot = index.findHandle(self.capability_index, handle) orelse return null;
        if (slot >= self.capability_count) return null;
        return self.capabilities[slot].row;
    }

    pub fn route(
        self: *const CatalogBank,
        path: []const u8,
    ) ?protocol.RouteInput {
        var best: ?protocol.RouteInput = null;
        var best_length: usize = 0;
        for (self.routes[0..self.route_count]) |slot| {
            const row = slot.row;
            const prefix = self.span(row.path);
            const matches = if (row.flags & protocol.RouteFlags.exact_path != 0)
                std.mem.eql(u8, path, prefix)
            else
                pathStartsWithSegment(path, prefix);
            if (!matches) continue;
            if (prefix.len > best_length or
                (prefix.len == best_length and (best == null or row.route_handle < best.?.route_handle)))
            {
                best = row;
                best_length = prefix.len;
            }
        }
        return best;
    }

    pub fn span(self: *const CatalogBank, value: protocol.TextSpan) []const u8 {
        const start = @as(usize, value.offset);
        return self.text[start .. start + @as(usize, value.length)];
    }
};

pub const ModelBank = struct {
    models: []ModelSlot,
    aliases: []AliasSlot,
    model_index: []index.HandleEntry,
    alias_index: []index.HashEntry,
    text: []u8,
    model_count: usize = 0,
    alias_count: usize = 0,
    text_count: usize = 0,
    generation: u64 = 0,

    pub fn init(allocator: std.mem.Allocator, config: protocol.Config) !ModelBank {
        const models = try allocator.alloc(ModelSlot, config.maximum_model_count);
        errdefer allocator.free(models);
        const aliases = try allocator.alloc(AliasSlot, config.maximum_model_alias_count);
        errdefer allocator.free(aliases);
        const model_index = try allocator.alloc(index.HandleEntry, config.model_index_capacity);
        errdefer allocator.free(model_index);
        const alias_index = try allocator.alloc(index.HashEntry, config.alias_index_capacity);
        errdefer allocator.free(alias_index);
        const text = try allocator.alloc(u8, config.maximum_model_text_bytes);
        errdefer allocator.free(text);

        @memset(models, .{});
        @memset(aliases, .{});
        index.clear(index.HandleEntry, model_index);
        index.clear(index.HashEntry, alias_index);
        @memset(text, 0);
        return .{
            .models = models,
            .aliases = aliases,
            .model_index = model_index,
            .alias_index = alias_index,
            .text = text,
        };
    }

    pub fn deinit(self: *ModelBank, allocator: std.mem.Allocator) void {
        allocator.free(self.models);
        allocator.free(self.aliases);
        allocator.free(self.model_index);
        allocator.free(self.alias_index);
        allocator.free(self.text);
        self.* = undefined;
    }

    pub fn reset(self: *ModelBank) void {
        @memset(self.models, .{});
        @memset(self.aliases, .{});
        index.clear(index.HandleEntry, self.model_index);
        index.clear(index.HashEntry, self.alias_index);
        @memset(self.text, 0);
        self.model_count = 0;
        self.alias_count = 0;
        self.text_count = 0;
        self.generation = 0;
    }

    pub fn stage(
        self: *ModelBank,
        header: protocol.ModelReplaceInput,
        models: []const protocol.ModelInput,
        aliases: []const protocol.ModelAliasInput,
        text: []const u8,
    ) Error!void {
        self.reset();
        if (header.struct_size != @sizeOf(protocol.ModelReplaceInput) or
            header.reserved_u32 != 0 or
            header.reserved_u32_2 != 0 or
            header.acquisition_attempt_handle == 0 or
            !protocol.allZero([2]u64, header.reserved) or
            header.model_generation == 0 or
            header.model_count != models.len or
            header.alias_count != aliases.len or
            header.text_byte_count != text.len)
        {
            return error.InvalidArgument;
        }
        if (models.len > self.models.len or
            aliases.len > self.aliases.len or
            text.len > self.text.len)
        {
            return error.CapacityExceeded;
        }
        if (!std.unicode.utf8ValidateSlice(text)) return error.InvalidArgument;

        @memcpy(self.text[0..text.len], text);
        self.text_count = text.len;

        for (models, 0..) |row, slot| {
            if (row.struct_size != @sizeOf(protocol.ModelInput) or
                row.model_handle == 0 or
                row.provider_handle == 0 or
                row.payload_handle == 0 or
                row.flags & ~protocol.ModelFlags.known != 0 or
                row.reserved != 0 or
                !index.insertHandle(self.model_index, row.model_handle, slot))
            {
                return error.InvalidArgument;
            }
            self.models[slot].row = row;
        }

        for (aliases, 0..) |row, slot| {
            if (row.struct_size != @sizeOf(protocol.ModelAliasInput) or
                row.flags != 0 or
                !protocol.allZero([2]u64, row.reserved) or
                index.findHandle(self.model_index, row.model_handle) == null or
                !validSpan(row.alias, self.text_count))
            {
                return error.InvalidArgument;
            }
            const alias = self.span(row.alias);
            if (!validModelAlias(alias)) return error.InvalidArgument;
            self.aliases[slot].row = row;
            const context = AliasContext{
                .bank = self,
                .value = alias,
                .available_alias_count = slot,
            };
            if (!index.insertHash(
                self.alias_index,
                hashAsciiFold(alias),
                slot,
                context,
                aliasEquals,
            )) return error.InvalidArgument;
        }

        for (models) |model| {
            var found = false;
            for (aliases) |alias| {
                if (alias.model_handle == model.model_handle) {
                    found = true;
                    break;
                }
            }
            if (!found) return error.InvalidArgument;
        }

        self.model_count = models.len;
        self.alias_count = aliases.len;
        self.generation = header.model_generation;
    }

    pub fn resolveAlias(self: *const ModelBank, raw: []const u8) ?protocol.ModelInput {
        const alias = trimAsciiBoundary(raw);
        if (alias.len == 0 or !std.unicode.utf8ValidateSlice(alias)) return null;
        const context = AliasContext{
            .bank = self,
            .value = alias,
            .available_alias_count = self.alias_count,
        };
        const alias_slot = index.findHash(
            self.alias_index,
            hashAsciiFold(alias),
            context,
            aliasEquals,
        ) orelse return null;
        if (alias_slot >= self.alias_count) return null;
        const model_handle = self.aliases[alias_slot].row.model_handle;
        const model_slot = index.findHandle(self.model_index, model_handle) orelse return null;
        if (model_slot >= self.model_count) return null;
        return self.models[model_slot].row;
    }

    pub fn containsModel(self: *const ModelBank, model_handle: u64) bool {
        const slot = index.findHandle(self.model_index, model_handle) orelse return false;
        return slot < self.model_count;
    }

    pub fn span(self: *const ModelBank, value: protocol.TextSpan) []const u8 {
        const start = @as(usize, value.offset);
        return self.text[start .. start + @as(usize, value.length)];
    }
};

const AliasContext = struct {
    bank: *const ModelBank,
    value: []const u8,
    available_alias_count: usize,
};

fn aliasEquals(context: AliasContext, slot: usize) bool {
    if (slot >= context.available_alias_count) return false;
    const candidate = context.bank.span(context.bank.aliases[slot].row.alias);
    return asciiFoldEqual(candidate, context.value);
}

fn validSpan(span: protocol.TextSpan, byte_count: usize) bool {
    if (span.length == 0) return false;
    const start = @as(usize, span.offset);
    const length = @as(usize, span.length);
    return start <= byte_count and length <= byte_count - start;
}

fn validRoutePath(path: []const u8) bool {
    if (path.len == 0 or path[0] != '/' or path.len > 1 and path[path.len - 1] == '/') return false;
    if (!std.unicode.utf8ValidateSlice(path)) return false;
    var previous_slash = false;
    for (path) |byte| {
        if (byte == 0 or byte == '?' or byte == '#') return false;
        if (byte == '/') {
            if (previous_slash) return false;
            previous_slash = true;
        } else {
            previous_slash = false;
        }
    }
    return true;
}

fn pathStartsWithSegment(path: []const u8, prefix: []const u8) bool {
    if (!std.mem.startsWith(u8, path, prefix)) return false;
    return path.len == prefix.len or
        (path.len > prefix.len and path[prefix.len] == '/');
}

fn validModelAlias(alias: []const u8) bool {
    if (alias.len == 0 or !std.unicode.utf8ValidateSlice(alias)) return false;
    if (trimAsciiBoundary(alias).len != alias.len) return false;
    return std.mem.indexOfScalar(u8, alias, 0) == null;
}

fn trimAsciiBoundary(value: []const u8) []const u8 {
    var start: usize = 0;
    var end = value.len;
    while (start < end and isAsciiBoundarySpace(value[start])) : (start += 1) {}
    while (end > start and isAsciiBoundarySpace(value[end - 1])) : (end -= 1) {}
    return value[start..end];
}

fn isAsciiBoundarySpace(byte: u8) bool {
    return byte == ' ' or byte == '\t' or byte == '\r' or byte == '\n';
}

fn hashAsciiFold(value: []const u8) u64 {
    var hash: u64 = 0xcbf29ce484222325;
    for (value) |byte| {
        const folded = if (byte >= 'A' and byte <= 'Z') byte + 32 else byte;
        hash = (hash ^ folded) *% 0x100000001b3;
    }
    return hash;
}

fn asciiFoldEqual(left: []const u8, right: []const u8) bool {
    if (left.len != right.len) return false;
    for (left, right) |left_byte, right_byte| {
        const folded_left = if (left_byte >= 'A' and left_byte <= 'Z') left_byte + 32 else left_byte;
        const folded_right = if (right_byte >= 'A' and right_byte <= 'Z') right_byte + 32 else right_byte;
        if (folded_left != folded_right) return false;
    }
    return true;
}
