const std = @import("std");
const protocol = @import("protocol.zig");

const occupied_flag: u32 = 1 << 0;
const no_slot: u32 = std.math.maxInt(u32);
var next_ledger_instance_id = std.atomic.Value(u64).init(1);

const Header = extern struct {
    config: protocol.Config,
    total_size: u64,
    ledger_instance_id: u64,
    occupied_count: u32,
    free_head: u32,
    ledger_generation: u64,
    owner_application_key: u64,
    owner_process_instance_key: u64,
    owner_process_id: u32,
    reserved_owner: u32,
    source_sequence: u64,
    captured_at_unix_milliseconds: i64,
    observed_at_monotonic: u64,
    fingerprint: u64,
    last_activity_settlement: u64,
    surface_state: protocol.SurfaceState,
    has_snapshot: u8,
    has_activity_settlement: u8,
    reserved0: [5]u8,
};

const Slot = extern struct {
    generation: u32,
    flags: u32,
    next_free: u32,
    activity_touched: u32,
    value: protocol.ResourceInput,
};

const View = struct {
    header: *Header,
    slots: []Slot,
};

pub fn requiredBytes(capacity: u32) !usize {
    if (capacity == 0) return error.InvalidArgument;
    const slot_offset = std.mem.alignForward(usize, @sizeOf(Header), @alignOf(Slot));
    const slot_bytes = std.math.mul(usize, capacity, @sizeOf(Slot)) catch return error.NumericOverflow;
    return std.math.add(usize, slot_offset, slot_bytes) catch return error.NumericOverflow;
}

pub fn alignment() usize {
    return @max(@alignOf(Header), @alignOf(Slot));
}

pub fn initialize(buffer: []u8, config: protocol.Config) !void {
    if (!protocol.validConfig(config)) return error.InvalidArgument;
    if (buffer.len != try requiredBytes(config.capacity)) return error.InvalidState;
    if (@intFromPtr(buffer.ptr) % alignment() != 0) return error.InvalidState;
    @memset(buffer, 0);
    const view = try openUnchecked(buffer, config.capacity);
    view.header.* = .{
        .config = config,
        .total_size = buffer.len,
        .ledger_instance_id = allocateLedgerInstanceId(),
        .occupied_count = 0,
        .free_head = 0,
        .ledger_generation = 1,
        .owner_application_key = 0,
        .owner_process_instance_key = 0,
        .owner_process_id = 0,
        .reserved_owner = 0,
        .source_sequence = 0,
        .captured_at_unix_milliseconds = 0,
        .observed_at_monotonic = 0,
        .fingerprint = 0,
        .last_activity_settlement = 0,
        .surface_state = .pure_background,
        .has_snapshot = 0,
        .has_activity_settlement = 0,
        .reserved0 = .{ 0, 0, 0, 0, 0 },
    };
    for (view.slots, 0..) |*slot, index| {
        slot.* = std.mem.zeroes(Slot);
        slot.next_free = if (index + 1 < view.slots.len) @intCast(index + 1) else no_slot;
    }
}

pub fn applyConfig(buffer: []u8, config: protocol.Config) !void {
    if (!protocol.validConfig(config)) return error.InvalidArgument;
    const view = try open(buffer);
    if (config.capacity != view.header.config.capacity) return error.InvalidState;
    if (config.configuration_generation <= view.header.config.configuration_generation) {
        return error.ConfigurationRegression;
    }
    view.header.config = config;
}

pub fn importSnapshot(
    buffer: []u8,
    input: protocol.SnapshotInput,
    resources: []const protocol.ResourceInput,
) !protocol.SnapshotSummary {
    const view = try open(buffer);
    try validateSnapshot(view, input, resources);
    const input_fingerprint = fingerprintSnapshot(input, resources);
    if (view.header.has_snapshot != 0 and input.source_sequence == view.header.source_sequence) {
        if (input_fingerprint != view.header.fingerprint) return error.SameSequenceConflict;
        return summary(view);
    }
    try ensureOwner(view, input);
    replaceResources(view, input.import_flags, resources);
    view.header.owner_application_key = input.owner_application_key;
    view.header.owner_process_instance_key = input.owner_process_instance_key;
    view.header.owner_process_id = input.owner_process_id;
    view.header.source_sequence = input.source_sequence;
    view.header.captured_at_unix_milliseconds = input.captured_at_unix_milliseconds;
    view.header.observed_at_monotonic = input.observed_at_monotonic;
    view.header.fingerprint = input_fingerprint;
    view.header.surface_state = input.surface_state;
    view.header.has_snapshot = 1;
    advanceLedgerGeneration(view.header);
    return summary(view);
}

pub fn touchResources(buffer: []u8, resource_ids: []const u32) !protocol.SnapshotSummary {
    const view = try openWithSnapshot(buffer);
    try validateUniqueIds(resource_ids);
    for (resource_ids) |id| {
        if (findSlotById(view, id) == null) return error.ResourceNotFound;
    }
    for (resource_ids) |id| findSlotById(view, id).?.activity_touched = 1;
    return summary(view);
}

pub fn settleActivity(buffer: []u8, now_timestamp: u64) !protocol.SnapshotSummary {
    const view = try openWithSnapshot(buffer);
    if (now_timestamp == 0 or
        (view.header.has_activity_settlement != 0 and now_timestamp < view.header.last_activity_settlement))
    {
        return error.TimestampRegression;
    }
    const should_settle = view.header.has_activity_settlement == 0 or
        now_timestamp - view.header.last_activity_settlement >= view.header.config.activity_settlement_interval;
    if (!should_settle) return summary(view);
    for (view.slots) |*slot| {
        if (!occupied(slot)) continue;
        slot.value.activity_score = settleScore(
            slot.value.activity_score,
            slot.activity_touched != 0,
            view.header.config,
        );
        slot.activity_touched = 0;
    }
    view.header.last_activity_settlement = now_timestamp;
    view.header.has_activity_settlement = 1;
    advanceLedgerGeneration(view.header);
    return summary(view);
}

pub fn applyActionFeedback(
    buffer: []u8,
    feedback: protocol.ActionFeedback,
) !protocol.SnapshotSummary {
    const view = try openWithSnapshot(buffer);
    if (!protocol.validActionFeedback(feedback)) return error.InvalidArgument;
    const slot = try resolveRef(view, feedback.resource);
    if (feedback.status != .completed) return summary(view);
    switch (feedback.action) {
        .move_down, .move_up => try applyMove(slot, feedback),
        .trim => {
            if (feedback.resident_bytes != 0) slot.value.size_bytes = feedback.resident_bytes;
        },
        .discard => freeSlot(view, feedback.resource.slot_index),
    }
    advanceLedgerGeneration(view.header);
    return summary(view);
}

pub fn readSnapshot(
    buffer: []u8,
    output: []protocol.ResourceView,
) !protocol.SnapshotSummary {
    const view = try openWithSnapshot(buffer);
    if (output.len < view.header.occupied_count) return error.BufferTooSmall;
    var output_index: usize = 0;
    for (view.slots, 0..) |slot, slot_index| {
        if (!occupied(&slot)) continue;
        output[output_index] = .{
            .resource = .{
                .slot_index = @intCast(slot_index),
                .slot_generation = slot.generation,
                .resource_key = slot.value.resource_key,
            },
            .ledger_generation = view.header.ledger_generation,
            .value = slot.value,
        };
        output_index += 1;
    }
    return summary(view);
}

fn validateSnapshot(
    view: View,
    input: protocol.SnapshotInput,
    resources: []const protocol.ResourceInput,
) !void {
    if (!protocol.validSnapshotInput(input)) return error.InvalidArgument;
    if (resources.len > view.slots.len) return error.BufferTooSmall;
    const maximum_age: i64 = view.header.config.maximum_snapshot_age_milliseconds;
    const maximum_future: i64 = view.header.config.maximum_future_skew_milliseconds;
    if (input.captured_at_unix_milliseconds > input.now_unix_milliseconds + maximum_future) {
        return error.SnapshotFromFuture;
    }
    if (input.now_unix_milliseconds - input.captured_at_unix_milliseconds > maximum_age) {
        return error.SnapshotTooOld;
    }
    if (view.header.has_snapshot != 0) {
        if (input.source_sequence < view.header.source_sequence) return error.SequenceRegression;
        if (input.source_sequence > view.header.source_sequence and
            input.captured_at_unix_milliseconds < view.header.captured_at_unix_milliseconds)
        {
            return error.TimestampRegression;
        }
    }
    try validateResources(resources);
}

fn validateResources(resources: []const protocol.ResourceInput) !void {
    for (resources, 0..) |resource, index| {
        if (!protocol.validResourceInput(resource)) return error.InvalidArgument;
        for (resources[0..index]) |previous| {
            if (previous.resource_key == resource.resource_key) return error.DuplicateResourceKey;
            if (previous.resource_id == resource.resource_id) return error.DuplicateResourceId;
        }
    }
}

fn fingerprintSnapshot(
    input: protocol.SnapshotInput,
    resources: []const protocol.ResourceInput,
) u64 {
    var hash: u64 = 14695981039346656037;
    hashValue(&hash, input.owner_application_key);
    hashValue(&hash, input.owner_process_instance_key);
    hashValue(&hash, input.owner_process_id);
    hashValue(&hash, input.source_sequence);
    hashValue(&hash, input.captured_at_unix_milliseconds);
    hashValue(&hash, input.schema_version);
    hashValue(&hash, input.surface_state);
    hashValue(&hash, input.import_flags);
    for (resources) |resource| hashBytes(&hash, std.mem.asBytes(&resource));
    return if (hash == 0) 14695981039346656037 else hash;
}

fn hashValue(hash: *u64, value: anytype) void {
    hashBytes(hash, std.mem.asBytes(&value));
}

fn hashBytes(hash: *u64, bytes: []const u8) void {
    for (bytes) |byte| {
        hash.* ^= byte;
        hash.* *%= 1099511628211;
    }
}

fn ensureOwner(view: View, input: protocol.SnapshotInput) !void {
    if (view.header.has_snapshot == 0) return;
    if (view.header.owner_application_key != input.owner_application_key or
        view.header.owner_process_instance_key != input.owner_process_instance_key)
    {
        return error.InvalidState;
    }
}

fn replaceResources(view: View, import_flags: u8, resources: []const protocol.ResourceInput) void {
    for (view.slots, 0..) |*slot, slot_index| {
        if (occupied(slot) and !containsKey(resources, slot.value.resource_key)) {
            freeSlot(view, @intCast(slot_index));
        }
    }
    for (resources) |resource| {
        if (findSlotByKey(view, resource.resource_key)) |slot| {
            const previous = slot.value;
            slot.value = resource;
            preserveManagedFields(&slot.value, previous, import_flags);
        } else {
            const slot = allocateSlot(view) catch unreachable;
            slot.value = resource;
        }
    }
}

fn preserveManagedFields(
    target: *protocol.ResourceInput,
    previous: protocol.ResourceInput,
    import_flags: u8,
) void {
    if (import_flags & protocol.import_activity_flag == 0) target.activity_score = previous.activity_score;
    if (import_flags & protocol.import_demand_flag == 0) target.demand_mask = previous.demand_mask;
}

fn applyMove(slot: *Slot, feedback: protocol.ActionFeedback) !void {
    const source = slot.value.tier;
    const expected = switch (feedback.action) {
        .move_down => switch (source) {
            .vram => protocol.ResourceTier.physical_memory,
            .physical_memory => protocol.ResourceTier.virtual_memory,
            .virtual_memory => return error.InvalidArgument,
        },
        .move_up => switch (source) {
            .virtual_memory => protocol.ResourceTier.physical_memory,
            .physical_memory => protocol.ResourceTier.vram,
            .vram => return error.InvalidArgument,
        },
        else => return error.InvalidArgument,
    };
    if (feedback.current_tier != expected) return error.InvalidArgument;
    slot.value.resource_kind = movedKind(
        source,
        feedback.current_tier,
        slot.value.resource_kind,
        feedback.preferred_gpu_kind,
    );
    slot.value.tier = feedback.current_tier;
    if (feedback.resident_bytes != 0) slot.value.size_bytes = feedback.resident_bytes;
}

fn movedKind(
    source: protocol.ResourceTier,
    target: protocol.ResourceTier,
    current: protocol.ResourceKind,
    preferred: protocol.ResourceKind,
) protocol.ResourceKind {
    if (source == target) return current;
    if (source == .vram and target != .vram and isGpuKind(current)) return .staging_buffer;
    if (source != .vram and target == .vram) {
        if (isGpuKind(preferred)) return preferred;
        return switch (current) {
            .staging_buffer, .model_weights => .compute_buffer,
            .media_resource => .texture,
            else => current,
        };
    }
    return current;
}

fn isGpuKind(kind: protocol.ResourceKind) bool {
    return switch (kind) {
        .render_surface, .texture, .render_buffer, .compute_buffer => true,
        else => false,
    };
}

fn settleScore(current: u8, active: bool, config: protocol.Config) u8 {
    const with_activity: u16 = if (active)
        @min(@as(u16, 255), @as(u16, current) + @as(u16, config.activity_increment))
    else
        current;
    return @intCast((@as(u32, with_activity) * config.activity_decay_numerator) /
        config.activity_decay_denominator);
}

fn resolveRef(view: View, reference: protocol.ResourceRef) !*Slot {
    if (reference.slot_index >= view.slots.len) return error.StaleResourceReference;
    const slot = &view.slots[reference.slot_index];
    if (!occupied(slot) or slot.generation != reference.slot_generation or
        slot.value.resource_key != reference.resource_key)
    {
        return error.StaleResourceReference;
    }
    return slot;
}

fn allocateSlot(view: View) !*Slot {
    if (view.header.free_head == no_slot) return error.BufferTooSmall;
    const slot_index = view.header.free_head;
    const slot = &view.slots[slot_index];
    view.header.free_head = slot.next_free;
    const generation = nextGeneration(slot.generation);
    slot.* = std.mem.zeroes(Slot);
    slot.generation = generation;
    slot.flags = occupied_flag;
    slot.next_free = no_slot;
    view.header.occupied_count += 1;
    return slot;
}

fn freeSlot(view: View, slot_index: u32) void {
    const slot = &view.slots[slot_index];
    if (!occupied(slot)) return;
    const generation = slot.generation;
    slot.* = std.mem.zeroes(Slot);
    slot.generation = generation;
    slot.next_free = view.header.free_head;
    view.header.free_head = slot_index;
    view.header.occupied_count -= 1;
}

fn nextGeneration(current: u32) u32 {
    return if (current == 0 or current == std.math.maxInt(u32)) 1 else current + 1;
}

fn advanceLedgerGeneration(header: *Header) void {
    header.ledger_generation = if (header.ledger_generation == std.math.maxInt(u64))
        1
    else
        header.ledger_generation + 1;
}

fn allocateLedgerInstanceId() u64 {
    while (true) {
        const candidate = next_ledger_instance_id.fetchAdd(1, .monotonic);
        if (candidate != 0) return candidate;
    }
}

fn summary(view: View) protocol.SnapshotSummary {
    return .{
        .ledger_generation = view.header.ledger_generation,
        .configuration_generation = view.header.config.configuration_generation,
        .ledger_instance_id = view.header.ledger_instance_id,
        .owner_application_key = view.header.owner_application_key,
        .owner_process_instance_key = view.header.owner_process_instance_key,
        .source_sequence = view.header.source_sequence,
        .captured_at_unix_milliseconds = view.header.captured_at_unix_milliseconds,
        .observed_at_monotonic = view.header.observed_at_monotonic,
        .fingerprint = view.header.fingerprint,
        .owner_process_id = view.header.owner_process_id,
        .resource_count = view.header.occupied_count,
        .surface_state = view.header.surface_state,
        .has_snapshot = view.header.has_snapshot,
        .reserved0 = .{ 0, 0, 0, 0, 0, 0 },
    };
}

fn openWithSnapshot(buffer: []u8) !View {
    const view = try open(buffer);
    if (view.header.has_snapshot == 0) return error.InvalidState;
    return view;
}

fn open(buffer: []u8) !View {
    if (buffer.len < @sizeOf(Header) or @intFromPtr(buffer.ptr) % alignment() != 0) return error.InvalidState;
    const header: *Header = @ptrCast(@alignCast(buffer.ptr));
    if (!protocol.validConfig(header.config) or header.total_size != buffer.len or
        header.ledger_instance_id == 0 or
        header.occupied_count > header.config.capacity or header.reserved_owner != 0 or header.has_snapshot > 1 or
        header.has_activity_settlement > 1 or !allZero(header.reserved0[0..]) or
        buffer.len != try requiredBytes(header.config.capacity))
    {
        return error.InvalidState;
    }
    const view = try openUnchecked(buffer, header.config.capacity);
    try validateSlots(view);
    return view;
}

fn openUnchecked(buffer: []u8, capacity: u32) !View {
    const slot_offset = std.mem.alignForward(usize, @sizeOf(Header), @alignOf(Slot));
    const slots_pointer: [*]Slot = @ptrCast(@alignCast(buffer.ptr + slot_offset));
    return .{ .header = @ptrCast(@alignCast(buffer.ptr)), .slots = slots_pointer[0..capacity] };
}

fn validateSlots(view: View) !void {
    var occupied_count: u32 = 0;
    for (view.slots) |slot| {
        if (slot.flags & ~occupied_flag != 0 or slot.activity_touched > 1) return error.InvalidState;
        if (!occupied(&slot)) continue;
        if (slot.generation == 0 or !protocol.validResourceInput(slot.value)) return error.InvalidState;
        occupied_count += 1;
    }
    if (occupied_count != view.header.occupied_count) return error.InvalidState;
}

fn occupied(slot: *const Slot) bool {
    return slot.flags & occupied_flag != 0;
}

fn findSlotByKey(view: View, resource_key: u64) ?*Slot {
    for (view.slots) |*slot| if (occupied(slot) and slot.value.resource_key == resource_key) return slot;
    return null;
}

fn findSlotById(view: View, resource_id: u32) ?*Slot {
    for (view.slots) |*slot| if (occupied(slot) and slot.value.resource_id == resource_id) return slot;
    return null;
}

fn containsKey(resources: []const protocol.ResourceInput, resource_key: u64) bool {
    for (resources) |resource| if (resource.resource_key == resource_key) return true;
    return false;
}

fn validateUniqueIds(ids: []const u32) !void {
    for (ids, 0..) |id, index| {
        if (id == 0) return error.InvalidArgument;
        for (ids[0..index]) |previous| if (previous == id) return error.DuplicateResourceId;
    }
}

fn allZero(values: []const u8) bool {
    for (values) |value| if (value != 0) return false;
    return true;
}

comptime {
    if (@sizeOf(Header) != 152) @compileError("private resource Header ABI drift");
    if (@sizeOf(Slot) != 48) @compileError("private resource Slot ABI drift");
}
