const std = @import("std");
const protocol = @import("protocol.zig");

pub const TextSlot = struct {
    kind: protocol.TextKind = .evidence,
    handle: protocol.Handle128 = .{ .high = 0, .low = 0 },
    offset: u32 = 0,
    length: u32 = 0,
};

pub const FactSlot = struct {
    source: protocol.SourceId = .display_config,
    valid_mask: u64 = 0,
    identity_mask: u64 = 0,
    source_record_ordinal: u64 = 0,
    adapter_luid: u64 = 0,
    target_id: u32 = 0,
    connector_instance: u32 = 0,
    output_technology: u32 = 0,
    status_flags: u32 = 0,
    monitor_path_text_index: u32 = protocol.no_text_index,
    source_device_text_index: u32 = protocol.no_text_index,
    friendly_name_text_index: u32 = protocol.no_text_index,
    edid_serial_text_index: u32 = protocol.no_text_index,
    evidence_text_index: u32 = protocol.no_text_index,
    match_text_index: u32 = protocol.no_text_index,
    match_flags: u32 = 0,
    position_x: i32 = 0,
    position_y: i32 = 0,
    width: i32 = 0,
    height: i32 = 0,
    refresh_numerator: u32 = 0,
    refresh_denominator: u32 = 0,
    bits_per_color_channel: u32 = 0,
    minimum_luminance_milli_nits: u32 = 0,
    maximum_luminance_milli_nits: u32 = 0,
    maximum_full_frame_luminance_milli_nits: u32 = 0,
    capability_flags: u64 = 0,
    observed_at_utc_ms: i64 = 0,
    source_object_key: u64 = 0,
    payload_handle: protocol.Handle128 = .{ .high = 0, .low = 0 },
};

pub const Bank = struct {
    allocator: std.mem.Allocator,
    facts: []FactSlot,
    texts: []TextSlot,
    text_bytes: []u8,
    text_index: []u32,
    nodes: []protocol.NodeOutput,
    edges: []protocol.EdgeOutput,
    capabilities: []protocol.DisplayCapabilityOutput,
    unresolved: []protocol.UnresolvedOutput,
    diffs: []protocol.DiffEntry,
    fact_count: u32 = 0,
    text_count: u32 = 0,
    text_byte_count: u32 = 0,
    node_count: u32 = 0,
    edge_count: u32 = 0,
    capability_count: u32 = 0,
    unresolved_count: u32 = 0,
    diff_count: u32 = 0,
    source_generations: [protocol.source_count]u64 =
        [_]u64{0} ** protocol.source_count,

    pub fn create(
        allocator: std.mem.Allocator,
        config: *const protocol.Config,
    ) !Bank {
        const facts = try allocator.alloc(FactSlot, config.maximum_observation_count);
        errdefer allocator.free(facts);
        const texts = try allocator.alloc(TextSlot, config.maximum_text_binding_count);
        errdefer allocator.free(texts);
        const text_bytes = try allocator.alloc(u8, config.maximum_text_byte_count);
        errdefer allocator.free(text_bytes);
        const text_index = try allocator.alloc(u32, config.text_index_capacity);
        errdefer allocator.free(text_index);
        const nodes = try allocator.alloc(protocol.NodeOutput, config.maximum_node_count);
        errdefer allocator.free(nodes);
        const edges = try allocator.alloc(protocol.EdgeOutput, config.maximum_edge_count);
        errdefer allocator.free(edges);
        const capabilities = try allocator.alloc(
            protocol.DisplayCapabilityOutput,
            config.maximum_capability_count,
        );
        errdefer allocator.free(capabilities);
        const unresolved = try allocator.alloc(
            protocol.UnresolvedOutput,
            config.maximum_unresolved_count,
        );
        errdefer allocator.free(unresolved);
        const diffs = try allocator.alloc(protocol.DiffEntry, config.maximum_diff_entry_count);
        errdefer allocator.free(diffs);
        var bank = Bank{
            .allocator = allocator,
            .facts = facts,
            .texts = texts,
            .text_bytes = text_bytes,
            .text_index = text_index,
            .nodes = nodes,
            .edges = edges,
            .capabilities = capabilities,
            .unresolved = unresolved,
            .diffs = diffs,
        };
        bank.reset();
        return bank;
    }

    pub fn destroy(self: *Bank) void {
        self.allocator.free(self.diffs);
        self.allocator.free(self.unresolved);
        self.allocator.free(self.capabilities);
        self.allocator.free(self.edges);
        self.allocator.free(self.nodes);
        self.allocator.free(self.text_index);
        self.allocator.free(self.text_bytes);
        self.allocator.free(self.texts);
        self.allocator.free(self.facts);
        self.* = undefined;
    }

    pub fn reset(self: *Bank) void {
        @memset(self.facts, .{});
        @memset(self.texts, .{});
        @memset(self.text_bytes, 0);
        @memset(self.text_index, protocol.no_text_index);
        @memset(self.nodes, std.mem.zeroes(protocol.NodeOutput));
        @memset(self.edges, std.mem.zeroes(protocol.EdgeOutput));
        @memset(self.capabilities, std.mem.zeroes(protocol.DisplayCapabilityOutput));
        @memset(self.unresolved, std.mem.zeroes(protocol.UnresolvedOutput));
        @memset(self.diffs, std.mem.zeroes(protocol.DiffEntry));
        self.fact_count = 0;
        self.text_count = 0;
        self.text_byte_count = 0;
        self.node_count = 0;
        self.edge_count = 0;
        self.capability_count = 0;
        self.unresolved_count = 0;
        self.diff_count = 0;
        self.source_generations = [_]u64{0} ** protocol.source_count;
    }

    pub fn text(self: *const Bank, index: u32) []const u8 {
        const slot = self.texts[index];
        return self.text_bytes[slot.offset .. slot.offset + slot.length];
    }

    pub fn findText(
        self: *const Bank,
        kind: protocol.TextKind,
        handle: protocol.Handle128,
        bytes: []const u8,
    ) ?u32 {
        const mask = self.text_index.len - 1;
        var probe = hashHandle(handle, @intFromEnum(kind)) & mask;
        var remaining = self.text_index.len;
        while (remaining != 0) : (remaining -= 1) {
            const index = self.text_index[probe];
            if (index == protocol.no_text_index) return null;
            const slot = self.texts[index];
            if (slot.kind == kind and equalHandle(slot.handle, handle) and
                std.mem.eql(u8, self.text_bytes[slot.offset .. slot.offset + slot.length], bytes))
            {
                return index;
            }
            probe = (probe + 1) & mask;
        }
        return null;
    }

    pub fn appendText(
        self: *Bank,
        kind: protocol.TextKind,
        handle: protocol.Handle128,
        bytes: []const u8,
    ) !u32 {
        const mask = self.text_index.len - 1;
        var probe = hashHandle(handle, @intFromEnum(kind)) & mask;
        var remaining = self.text_index.len;
        while (remaining != 0) : (remaining -= 1) {
            const existing = self.text_index[probe];
            if (existing == protocol.no_text_index) break;
            const slot = self.texts[existing];
            if (slot.kind == kind and equalHandle(slot.handle, handle)) {
                if (!std.mem.eql(
                    u8,
                    self.text_bytes[slot.offset .. slot.offset + slot.length],
                    bytes,
                )) return error.HashCollision;
                return existing;
            }
            probe = (probe + 1) & mask;
        }
        if (self.text_count >= self.texts.len) return error.TextCapacity;
        const next_end = std.math.add(u32, self.text_byte_count, @intCast(bytes.len)) catch
            return error.TextCapacity;
        if (next_end > self.text_bytes.len) return error.TextCapacity;
        const index = self.text_count;
        self.texts[index] = .{
            .kind = kind,
            .handle = handle,
            .offset = self.text_byte_count,
            .length = @intCast(bytes.len),
        };
        @memcpy(self.text_bytes[self.text_byte_count..next_end], bytes);
        self.text_count += 1;
        self.text_byte_count = next_end;
        insertTextIndex(self, index);
        return index;
    }

    pub fn removeSourceFacts(self: *Bank, source: protocol.SourceId) void {
        var output: u32 = 0;
        for (self.facts[0..self.fact_count]) |fact| {
            if (fact.source == source) continue;
            self.facts[output] = fact;
            output += 1;
        }
        for (self.facts[output..self.fact_count]) |*fact| fact.* = .{};
        self.fact_count = output;
    }

    pub fn rebuildTextIndex(self: *Bank) void {
        @memset(self.text_index, protocol.no_text_index);
        for (0..self.text_count) |index| insertTextIndex(self, @intCast(index));
    }
};

fn insertTextIndex(self: *Bank, index: u32) void {
    const slot = self.texts[index];
    const mask = self.text_index.len - 1;
    var probe = hashHandle(slot.handle, @intFromEnum(slot.kind)) & mask;
    while (self.text_index[probe] != protocol.no_text_index) {
        probe = (probe + 1) & mask;
    }
    self.text_index[probe] = index;
}

pub fn equalHandle(left: protocol.Handle128, right: protocol.Handle128) bool {
    return left.high == right.high and left.low == right.low;
}

pub fn lessHandle(_: void, left: protocol.Handle128, right: protocol.Handle128) bool {
    return left.high < right.high or (left.high == right.high and left.low < right.low);
}

pub fn hashHandle(handle: protocol.Handle128, discriminator: u32) usize {
    var value = handle.high ^ std.math.rotl(u64, handle.low, 23) ^
        (@as(u64, discriminator) *% 0x9E37_79B9_7F4A_7C15);
    value ^= value >> 30;
    value *%= 0xBF58_476D_1CE4_E5B9;
    value ^= value >> 27;
    value *%= 0x94D0_49BB_1331_11EB;
    value ^= value >> 31;
    return @intCast(value);
}
