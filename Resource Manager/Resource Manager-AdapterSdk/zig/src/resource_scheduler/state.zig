const std = @import("std");
const types = @import("types.zig");
const sorting = @import("sort.zig");

const occupied_flag: u8 = 1 << 0;
const journal_bound_flag: u8 = 1 << 1;

comptime {
    if (@sizeOf(Slot) != 288) @compileError("resource scheduler state slot ABI drift");
}

pub const Header = extern struct {
    abi_version: u32,
    struct_size: u32,
    total_size: u64,
    capacity: u32,
    active_count: u32,
    state_generation: u64,
    next_slot_generation: u64,
    reserved0: [3]u64,
};

pub const Slot = extern struct {
    pending: types.PendingActionInput,
    request_id: u64,
    configuration_generation: u64,
    danger_min_physical_after_vram_move_ratio: f64,
    danger_min_virtual_after_physical_move_ratio: f64,
    journal_transaction_id_low: u64,
    journal_transaction_id_high: u64,
    slot_generation: u64,
    action_gate_epoch: u64,
    flags: u8,
    reserved0: [7]u8,
};

pub const View = struct {
    header: *Header,
    slots: []Slot,
};

pub fn requiredBytes(capacity: u32) types.PlanError!usize {
    if (capacity == 0) return error.InvalidState;
    const slot_offset = std.mem.alignForward(usize, @sizeOf(Header), @alignOf(Slot));
    const slot_bytes = std.math.mul(usize, capacity, @sizeOf(Slot)) catch return error.NumericOverflow;
    return std.math.add(usize, slot_offset, slot_bytes) catch return error.NumericOverflow;
}

pub fn initialize(buffer: []u8, capacity: u32, generation: u64) types.PlanError!void {
    if (generation == 0 or buffer.len != try requiredBytes(capacity)) return error.InvalidState;
    if (@intFromPtr(buffer.ptr) % @alignOf(Header) != 0) return error.InvalidState;
    @memset(buffer, 0);
    const view = try openUnchecked(buffer, capacity);
    view.header.* = .{
        .abi_version = types.protocol_version,
        .struct_size = @sizeOf(Header),
        .total_size = buffer.len,
        .capacity = capacity,
        .active_count = 0,
        .state_generation = generation,
        .next_slot_generation = 1,
        .reserved0 = .{ 0, 0, 0 },
    };
}

pub fn open(buffer: []u8) types.PlanError!View {
    if (buffer.len < @sizeOf(Header) or @intFromPtr(buffer.ptr) % @alignOf(Header) != 0) {
        return error.InvalidState;
    }
    const header: *Header = @ptrCast(@alignCast(buffer.ptr));
    if (header.abi_version != types.protocol_version or
        header.struct_size != @sizeOf(Header) or
        header.total_size != buffer.len or
        header.capacity == 0 or
        header.active_count > header.capacity or
        header.state_generation == 0 or
        header.next_slot_generation == 0 or
        !allZero(std.mem.asBytes(&header.reserved0)))
    {
        return error.InvalidState;
    }
    const expected = try requiredBytes(header.capacity);
    if (expected != buffer.len) return error.InvalidState;
    const view = try openUnchecked(buffer, header.capacity);
    var counted: u32 = 0;
    for (view.slots) |slot| {
        if (slot.flags & ~(occupied_flag | journal_bound_flag) != 0 or
            !allZero(slot.reserved0[0..]))
        {
            return error.InvalidState;
        }
        if (slot.flags & occupied_flag != 0) {
            if (slot.slot_generation == 0 or
                slot.request_id == 0 or
                slot.configuration_generation == 0 or
                !validRatio(slot.danger_min_physical_after_vram_move_ratio) or
                !validRatio(slot.danger_min_virtual_after_physical_move_ratio) or
                !validGateState(slot) or
                ((slot.flags & journal_bound_flag != 0) !=
                    ((slot.journal_transaction_id_low |
                        slot.journal_transaction_id_high) != 0)) or
                (slot.pending.state == @intFromEnum(types.PendingState.effect_uncertain) and
                    (slot.flags & journal_bound_flag == 0 or
                        slot.pending.journal_transaction_id_low !=
                            slot.journal_transaction_id_low or
                        slot.pending.journal_transaction_id_high !=
                            slot.journal_transaction_id_high)))
            {
                return error.InvalidState;
            }
            counted += 1;
        }
    }
    if (counted != header.active_count) return error.InvalidState;
    return view;
}

pub fn reserve(
    view: View,
    request: *const types.ReservationRequest,
    selection: *const types.SelectionOutput,
    token: *types.ReservationToken,
) types.PlanError!void {
    if (!validReservationRequest(request) or !validSelection(selection) or
        request.configuration_generation != selection.configuration_generation)
    {
        return error.InvalidState;
    }

    var free_index: ?usize = null;
    var target_count: u32 = 0;
    for (view.slots, 0..) |slot, index| {
        if (slot.flags & occupied_flag == 0) {
            if (free_index == null) free_index = index;
            continue;
        }
        if (slot.pending.target_key == selection.target_key) target_count += 1;
        if (types.sameResourceLocation(slot.pending.authority, selection.authority)) {
            return error.InvalidState;
        }
    }
    if (view.header.active_count >= request.maximum_in_flight or
        target_count >= request.maximum_in_flight_per_target)
    {
        return error.StateFull;
    }
    if (view.header.next_slot_generation == std.math.maxInt(u64)) {
        return error.NumericOverflow;
    }
    const index = free_index orelse return error.StateFull;
    const slot = &view.slots[index];
    const slot_generation = view.header.next_slot_generation;
    view.header.next_slot_generation += 1;
    slot.* = std.mem.zeroes(Slot);
    slot.pending = .{
        .abi_version = types.protocol_version,
        .struct_size = @sizeOf(types.PendingActionInput),
        .authority = selection.authority,
        .journal_transaction_id_low = 0,
        .journal_transaction_id_high = 0,
        .target_key = selection.target_key,
        .size_bytes = selection.size_bytes,
        .deadline_timestamp = request.deadline_timestamp,
        .pending_generation = request.pending_generation,
        .state = @intFromEnum(types.PendingState.reserved),
        .tier = selection.tier,
        .flags = 0,
        .reserved0 = .{ 0, 0, 0, 0, 0 },
    };
    slot.request_id = selection.request_id;
    slot.configuration_generation = request.configuration_generation;
    slot.danger_min_physical_after_vram_move_ratio = request.danger_min_physical_after_vram_move_ratio;
    slot.danger_min_virtual_after_physical_move_ratio = request.danger_min_virtual_after_physical_move_ratio;
    slot.slot_generation = slot_generation;
    slot.flags = occupied_flag;
    view.header.active_count += 1;
    token.* = .{
        .state_generation = view.header.state_generation,
        .slot_generation = slot_generation,
        .pending_generation = request.pending_generation,
        .slot_index = @intCast(index),
        .reserved0 = 0,
    };
}

pub fn begin(
    view: View,
    token: *const types.ReservationToken,
    action_gate_epoch: u64,
) types.PlanError!void {
    const slot = try resolveSlot(view, token);
    if (slot.pending.state != @intFromEnum(types.PendingState.reserved) or
        slot.flags & journal_bound_flag == 0)
    {
        return error.StaleReservation;
    }
    if (action_gate_epoch != 0) {
        return error.InvalidState;
    }
    slot.action_gate_epoch = action_gate_epoch;
    slot.pending.state = @intFromEnum(types.PendingState.active);
}

pub fn bindJournal(
    view: View,
    token: *const types.ReservationToken,
    transaction_id_low: u64,
    transaction_id_high: u64,
) types.PlanError!void {
    const slot = try resolveSlot(view, token);
    if (slot.pending.state != @intFromEnum(types.PendingState.reserved) or
        slot.flags & journal_bound_flag != 0 or
        (transaction_id_low | transaction_id_high) == 0)
    {
        return error.StaleReservation;
    }
    slot.journal_transaction_id_low = transaction_id_low;
    slot.journal_transaction_id_high = transaction_id_high;
    slot.flags |= journal_bound_flag;
}

pub fn complete(view: View, token: *const types.ReservationToken) types.PlanError!void {
    const slot = try resolveSlot(view, token);
    if (slot.pending.state != @intFromEnum(types.PendingState.active) and
        slot.pending.state != @intFromEnum(types.PendingState.effect_uncertain))
    {
        return error.StaleReservation;
    }
    releaseResolved(view, slot);
}

pub fn cancel(view: View, token: *const types.ReservationToken) types.PlanError!void {
    const slot = try resolveSlot(view, token);
    if (slot.pending.state != @intFromEnum(types.PendingState.reserved)) return error.StaleReservation;
    releaseResolved(view, slot);
}

pub fn abandon(view: View, token: *const types.ReservationToken) types.PlanError!void {
    const slot = try resolveSlot(view, token);
    if (slot.pending.state != @intFromEnum(types.PendingState.active) or
        slot.flags & journal_bound_flag == 0)
    {
        return error.StaleReservation;
    }
    slot.pending.journal_transaction_id_low = slot.journal_transaction_id_low;
    slot.pending.journal_transaction_id_high = slot.journal_transaction_id_high;
    slot.pending.state = @intFromEnum(types.PendingState.effect_uncertain);
}

pub fn validateActiveSelection(
    view: View,
    token: *const types.ReservationToken,
    selection: *const types.SelectionOutput,
) types.PlanError!*Slot {
    return validateSelection(view, token, selection, .active);
}

pub fn validateTerminalSelection(
    view: View,
    token: *const types.ReservationToken,
    selection: *const types.SelectionOutput,
) types.PlanError!*Slot {
    const slot = try resolveSlot(view, token);
    if (slot.pending.state != @intFromEnum(types.PendingState.active) and
        slot.pending.state != @intFromEnum(types.PendingState.effect_uncertain))
    {
        return error.StaleReservation;
    }
    try validateSelectionAuthority(slot, selection);
    return slot;
}

pub fn validateReservedSelection(
    view: View,
    token: *const types.ReservationToken,
    selection: *const types.SelectionOutput,
) types.PlanError!*Slot {
    return validateSelection(view, token, selection, .reserved);
}

fn validateSelection(
    view: View,
    token: *const types.ReservationToken,
    selection: *const types.SelectionOutput,
    expected_state: types.PendingState,
) types.PlanError!*Slot {
    const slot = try resolveSlot(view, token);
    if (slot.pending.state != @intFromEnum(expected_state)) return error.StaleReservation;
    try validateSelectionAuthority(slot, selection);
    return slot;
}

fn validateSelectionAuthority(
    slot: *const Slot,
    selection: *const types.SelectionOutput,
) types.PlanError!void {
    if (slot.pending.target_key != selection.target_key or
        slot.request_id != selection.request_id or
        slot.pending.size_bytes != selection.size_bytes or
        slot.configuration_generation != selection.configuration_generation or
        !types.sameExecutionAuthority(slot.pending.authority, selection.authority) or
        slot.pending.tier != selection.tier)
    {
        return error.InvalidFeedback;
    }
}

fn releaseResolved(view: View, slot: *Slot) void {
    slot.* = std.mem.zeroes(Slot);
    view.header.active_count -= 1;
}

pub fn snapshot(view: View, now: u64, output: []types.PendingActionInput) types.PlanError!usize {
    if (output.len < view.header.capacity) return error.BufferTooSmall;
    var count: usize = 0;
    for (view.slots) |*slot| {
        if (slot.flags & occupied_flag == 0) continue;
        _ = now;
        output[count] = slot.pending;
        count += 1;
    }
    sorting.pending(output[0..count]);
    var index: usize = 1;
    while (index < count) : (index += 1) {
        if (types.sameResourceLocation(
            output[index - 1].authority,
            output[index].authority,
        )) {
            return error.InvalidPending;
        }
    }
    return count;
}

fn openUnchecked(buffer: []u8, capacity: u32) types.PlanError!View {
    const slot_offset = std.mem.alignForward(usize, @sizeOf(Header), @alignOf(Slot));
    if (@intFromPtr(buffer.ptr + slot_offset) % @alignOf(Slot) != 0) return error.InvalidState;
    const header: *Header = @ptrCast(@alignCast(buffer.ptr));
    const slots_pointer: [*]Slot = @ptrCast(@alignCast(buffer.ptr + slot_offset));
    return .{ .header = header, .slots = slots_pointer[0..capacity] };
}

fn resolveSlot(view: View, token: *const types.ReservationToken) types.PlanError!*Slot {
    if (token.reserved0 != 0 or token.state_generation != view.header.state_generation or
        token.slot_index >= view.slots.len)
    {
        return error.StaleReservation;
    }
    const slot = &view.slots[token.slot_index];
    if (slot.flags & occupied_flag == 0 or
        slot.slot_generation != token.slot_generation or
        slot.pending.pending_generation != token.pending_generation)
    {
        return error.StaleReservation;
    }
    return slot;
}

fn validReservationRequest(value: *const types.ReservationRequest) bool {
    return value.abi_version == types.protocol_version and
        value.struct_size == @sizeOf(types.ReservationRequest) and
        value.now_monotonic_timestamp > 0 and
        value.deadline_timestamp > value.now_monotonic_timestamp and
        value.pending_generation > 0 and
        value.configuration_generation > 0 and
        std.math.isFinite(value.danger_min_physical_after_vram_move_ratio) and
        value.danger_min_physical_after_vram_move_ratio >= 0 and
        value.danger_min_physical_after_vram_move_ratio <= 1 and
        std.math.isFinite(value.danger_min_virtual_after_physical_move_ratio) and
        value.danger_min_virtual_after_physical_move_ratio >= 0 and
        value.danger_min_virtual_after_physical_move_ratio <= 1 and
        value.maximum_in_flight > 0 and
        value.maximum_in_flight_per_target > 0 and
        value.maximum_in_flight_per_target <= value.maximum_in_flight;
}

fn validSelection(value: *const types.SelectionOutput) bool {
    return value.target_key != 0 and value.request_id != 0 and
        types.validExecutionAuthority(value.authority) and
        value.size_bytes != 0 and
        value.configuration_generation != 0 and
        types.tierIndex(value.tier) != null and allZero(value.reserved0[0..]);
}

fn validGateState(slot: Slot) bool {
    if (slot.pending.state == @intFromEnum(types.PendingState.reserved)) {
        return slot.action_gate_epoch == 0;
    }
    if (slot.pending.state != @intFromEnum(types.PendingState.active) and
        slot.pending.state != @intFromEnum(types.PendingState.effect_uncertain))
    {
        return false;
    }
    return slot.action_gate_epoch == 0;
}

fn allZero(values: []const u8) bool {
    for (values) |value| if (value != 0) return false;
    return true;
}

fn validRatio(value: f64) bool {
    return std.math.isFinite(value) and value >= 0 and value <= 1;
}

fn testAuthority(
    source: types.Source,
    action: u8,
    target_key: u64,
    resource_key: u64,
) types.ResourceExecutionAuthority {
    return .{
        .source = @intFromEnum(source),
        .action = action,
        .action_route = if (source == .adapted_private)
            @intFromEnum(types.ActionRoute.adapter_handler)
        else
            @intFromEnum(types.ActionRoute.manager_direct),
        .flags = types.authority_flag_typed_executor_proof,
        .resource_slot = @intCast(resource_key),
        .resource_id = @intCast(resource_key),
        .reserved0 = 0,
        .ledger_instance_id = target_key + 1,
        .source_snapshot_generation = 1,
        .resource_generation = 1,
        .resource_key = resource_key,
        .owner_application_key = target_key + 2,
        .owner_instance_id_low = target_key + 3,
        .owner_instance_id_high = target_key + 4,
        .owner_context_generation = 1,
        .lease_generation = 1,
        .binding_generation = 1,
        .capability_generation = 1,
        .scheduling_revision = 1,
        .executor_id_low = 1,
        .executor_id_high = 2,
        .action_attempt_id_low = resource_key + action,
        .action_attempt_id_high = 1,
        .projection_epoch = 1,
    };
}

test "reservation state preserves active work past its transport deadline" {
    const capacity = 2;
    var storage: [try requiredBytes(capacity)]u8 align(@alignOf(Header)) = undefined;
    try initialize(storage[0..], capacity, 7);
    const view = try open(storage[0..]);
    var selection = std.mem.zeroes(types.SelectionOutput);
    selection.target_key = 11;
    selection.request_id = 101;
    selection.authority = testAuthority(.adapted_private, types.action_discard, 11, 14);
    selection.size_bytes = 4096;
    selection.configuration_generation = 17;
    selection.tier = @intFromEnum(types.Tier.physical_memory);
    var request = std.mem.zeroes(types.ReservationRequest);
    request.abi_version = types.protocol_version;
    request.struct_size = @sizeOf(types.ReservationRequest);
    request.now_monotonic_timestamp = 100;
    request.deadline_timestamp = 110;
    request.pending_generation = 13;
    request.configuration_generation = 17;
    request.danger_min_physical_after_vram_move_ratio = 0.10;
    request.danger_min_virtual_after_physical_move_ratio = 0.12;
    request.maximum_in_flight = 2;
    request.maximum_in_flight_per_target = 1;
    var token = std.mem.zeroes(types.ReservationToken);
    try reserve(view, &request, &selection, &token);
    try bindJournal(view, &token, 91, 92);
    try begin(view, &token, 0);
    var pending: [capacity]types.PendingActionInput = undefined;
    try std.testing.expectEqual(@as(usize, 1), try snapshot(view, 999, pending[0..]));
    try complete(view, &token);
    try std.testing.expectEqual(@as(usize, 0), try snapshot(view, 1000, pending[0..]));
}

test "reservation state preserves private resource identity" {
    const capacity = 1;
    var storage: [try requiredBytes(capacity)]u8 align(@alignOf(Header)) = undefined;
    try initialize(storage[0..], capacity, 21);
    const view = try open(storage[0..]);
    var selection = std.mem.zeroes(types.SelectionOutput);
    selection.target_key = 31;
    selection.request_id = 102;
    selection.authority = testAuthority(.adapted_private, types.action_discard, 31, 61);
    selection.size_bytes = 4096;
    selection.configuration_generation = 71;
    selection.tier = @intFromEnum(types.Tier.physical_memory);
    var request = std.mem.zeroes(types.ReservationRequest);
    request.abi_version = types.protocol_version;
    request.struct_size = @sizeOf(types.ReservationRequest);
    request.now_monotonic_timestamp = 100;
    request.deadline_timestamp = 110;
    request.pending_generation = 81;
    request.configuration_generation = 71;
    request.danger_min_physical_after_vram_move_ratio = 0.10;
    request.danger_min_virtual_after_physical_move_ratio = 0.12;
    request.maximum_in_flight = 1;
    request.maximum_in_flight_per_target = 1;
    var token = std.mem.zeroes(types.ReservationToken);
    try reserve(view, &request, &selection, &token);
    var pending: [capacity]types.PendingActionInput = undefined;
    try std.testing.expectEqual(@as(usize, 1), try snapshot(view, 100, pending[0..]));
    try std.testing.expect(
        types.sameExecutionAuthority(selection.authority, pending[0].authority),
    );
    try cancel(view, &token);
}

test "reservation state locks a resource across different actions" {
    const capacity = 2;
    var storage: [try requiredBytes(capacity)]u8 align(@alignOf(Header)) = undefined;
    try initialize(storage[0..], capacity, 91);
    const view = try open(storage[0..]);
    var first = std.mem.zeroes(types.SelectionOutput);
    first.target_key = 101;
    first.request_id = 103;
    first.authority = testAuthority(.adapted_private, types.action_discard, 101, 401);
    first.size_bytes = 4096;
    first.configuration_generation = 501;
    first.tier = @intFromEnum(types.Tier.physical_memory);
    var second = first;
    second.target_key = 102;
    second.authority.action = types.action_trim;
    second.authority.action_attempt_id_low += 1;

    var request = std.mem.zeroes(types.ReservationRequest);
    request.abi_version = types.protocol_version;
    request.struct_size = @sizeOf(types.ReservationRequest);
    request.now_monotonic_timestamp = 100;
    request.deadline_timestamp = 110;
    request.pending_generation = 601;
    request.configuration_generation = 501;
    request.danger_min_physical_after_vram_move_ratio = 0.10;
    request.danger_min_virtual_after_physical_move_ratio = 0.12;
    request.maximum_in_flight = 2;
    request.maximum_in_flight_per_target = 1;
    var first_token = std.mem.zeroes(types.ReservationToken);
    try reserve(view, &request, &first, &first_token);
    request.pending_generation = 602;
    var second_token = std.mem.zeroes(types.ReservationToken);

    try std.testing.expectError(error.InvalidState, reserve(view, &request, &second, &second_token));
    try cancel(view, &first_token);
}

test "reservation slot generation exhausts without mutating state or reviving a stale token" {
    const capacity = 1;
    var storage: [try requiredBytes(capacity)]u8 align(@alignOf(Header)) = undefined;
    try initialize(storage[0..], capacity, 101);
    const view = try open(storage[0..]);
    view.header.next_slot_generation = std.math.maxInt(u64) - 1;

    var selection = std.mem.zeroes(types.SelectionOutput);
    selection.target_key = 201;
    selection.request_id = 301;
    selection.authority = testAuthority(.adapted_private, types.action_discard, 201, 401);
    selection.size_bytes = 4096;
    selection.configuration_generation = 501;
    selection.tier = @intFromEnum(types.Tier.physical_memory);

    var request = std.mem.zeroes(types.ReservationRequest);
    request.abi_version = types.protocol_version;
    request.struct_size = @sizeOf(types.ReservationRequest);
    request.now_monotonic_timestamp = 100;
    request.deadline_timestamp = 110;
    request.pending_generation = 601;
    request.configuration_generation = 501;
    request.danger_min_physical_after_vram_move_ratio = 0.10;
    request.danger_min_virtual_after_physical_move_ratio = 0.12;
    request.maximum_in_flight = 1;
    request.maximum_in_flight_per_target = 1;

    var last_token = std.mem.zeroes(types.ReservationToken);
    try reserve(view, &request, &selection, &last_token);
    try std.testing.expectEqual(std.math.maxInt(u64) - 1, last_token.slot_generation);
    try std.testing.expectEqual(std.math.maxInt(u64), view.header.next_slot_generation);
    try cancel(view, &last_token);

    const header_before = view.header.*;
    const slot_before = view.slots[0];
    const token_before = last_token;
    request.pending_generation = 602;
    selection.request_id = 302;
    selection.authority.action_attempt_id_low += 1;

    try std.testing.expectError(
        error.NumericOverflow,
        reserve(view, &request, &selection, &last_token),
    );
    try std.testing.expectEqualDeep(header_before, view.header.*);
    try std.testing.expectEqualDeep(slot_before, view.slots[0]);
    try std.testing.expectEqualDeep(token_before, last_token);
    try std.testing.expectError(error.StaleReservation, cancel(view, &token_before));
}
