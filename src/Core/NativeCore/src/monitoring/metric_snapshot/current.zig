const std = @import("std");
const protocol = @import("protocol.zig");
const state = @import("state.zig");
const scratch = @import("scratch.zig");
const ResultCode = @import("../../common/result_codes.zig").ResultCode;

// Final public values, independently of the source collection workspace. The
// caller supplies the two resident banks through its existing memory owner.
pub fn commit(session: *state.Session, input: *const protocol.CurrentCommitInput,
    requested: []const u64, retired: []const u64, values: []const protocol.CurrentValue,
    bytes: []const u8, resident: []u8) ResultCode
{
    state.lock(&session.mutex);
    defer state.unlock(&session.mutex);
    if (input.abi_version != protocol.abi_version or input.struct_size != @sizeOf(protocol.CurrentCommitInput)) return .abi_mismatch;
    if (input.flags != 0 or input.reserved != 0 or !sortedHandles(requested) or !sortedHandles(retired)) return .invalid_argument;
    const maximum = @min(std.math.maxInt(u32), session.config.resident_byte_budget / (2 * @sizeOf(protocol.CurrentValue)));
    if (input.cell_capacity > maximum or requested.len > maximum or retired.len > maximum or values.len > input.cell_capacity or
        input.byte_capacity > @as(u64, input.cell_capacity) * protocol.maximum_value_bytes or bytes.len > input.byte_capacity) return .invalid_argument;
    const bank_bytes = @as(u64, input.cell_capacity) * @sizeOf(protocol.CurrentValue) + input.byte_capacity;
    if (input.byte_capacity % 8 != 0 or bank_bytes > std.math.maxInt(usize) / 2 or resident.len != bank_bytes * 2 or
        (resident.len != 0 and @intFromPtr(resident.ptr) % @alignOf(protocol.CurrentValue) != 0)) return .invalid_argument;
    if (session.residentByteCount() - session.current_region.len + resident.len + session.scratch_layout.byte_count > session.config.resident_byte_budget) return .invalid_argument;
    // Observation storage begins at offset zero and remains borrowed while a
    // completion is open. Final current values must use independent storage.
    if (session.phase == .completion_open and overlaps(resident.ptr, resident.len,
        @ptrCast(session.observations.ptr), session.scratch_layout.byte_count)) return .invalid_argument;
    for (retired) |handle| if (contains(requested, handle)) return .invalid_argument;
    var prior_dataset: u64 = 0;
    var prior_cell: u64 = 0;
    var supplied_bytes: u64 = 0;
    for (values) |value| {
        if (value.dataset_handle == 0 or value.cell_handle == 0 or !contains(requested, value.dataset_handle) or
            value.dataset_handle < prior_dataset or (value.dataset_handle == prior_dataset and value.cell_handle <= prior_cell) or
            !validValue(value, bytes, &supplied_bytes)) return .invalid_argument;
        prior_dataset = value.dataset_handle;
        prior_cell = value.cell_handle;
    }
    if (supplied_bytes != bytes.len) return .invalid_argument;
    var count: u64 = values.len;
    var byte_count: u64 = bytes.len;
    for (session.current_values) |value| {
        if (contains(requested, value.dataset_handle) or contains(retired, value.dataset_handle)) continue;
        count += 1;
        if (value.value_kind == @intFromEnum(protocol.ValueKind.bytes)) byte_count += value.value_high_bits;
    }
    if (count > input.cell_capacity or byte_count > input.byte_capacity) return .buffer_too_small;
    const same_region = resident.ptr == session.current_region.ptr and resident.len == session.current_region.len;
    if (!same_region and resident.len != 0 and session.current_region.len != 0 and
        overlaps(resident.ptr, resident.len, session.current_region.ptr, session.current_region.len)) return .invalid_argument;
    if (bank_bytes == 0) {
        session.current_region = resident;
        session.current_bank = 0;
        session.current_values = &.{};
        session.current_bytes = &.{};
        return .ok;
    }
    const bank: u32 = if (same_region) 1 - session.current_bank else 0;
    const start = @as(usize, @intCast(bank_bytes)) * bank;
    const target = resident[start .. start + @as(usize, @intCast(bank_bytes))];
    if (overlapsInput(target, input, requested, retired, values, bytes)) return .invalid_argument;
    const cell_length: usize = @as(usize, input.cell_capacity) * @sizeOf(protocol.CurrentValue);
    const cells = std.mem.bytesAsSlice(protocol.CurrentValue, @as([]align(@alignOf(protocol.CurrentValue)) u8, @alignCast(target[0..cell_length])));
    const payload = target[cell_length..];
    var old_index: usize = 0;
    var new_index: usize = 0;
    var output_index: usize = 0;
    var output_bytes: usize = 0;
    while (old_index < session.current_values.len or new_index < values.len) {
        while (old_index < session.current_values.len and (contains(requested, session.current_values[old_index].dataset_handle) or
            contains(retired, session.current_values[old_index].dataset_handle))) : (old_index += 1) {}
        const take_new = new_index < values.len and (old_index == session.current_values.len or less(values[new_index], session.current_values[old_index]));
        if (!take_new and old_index == session.current_values.len) break;
        var value = if (take_new) values[new_index] else session.current_values[old_index];
        const source = if (take_new) bytes else session.current_bytes;
        if (value.value_kind == @intFromEnum(protocol.ValueKind.bytes) and value.flags == 1) {
            const offset: usize = @intCast(value.value_bits);
            const length: usize = @intCast(value.value_high_bits);
            @memcpy(payload[output_bytes .. output_bytes + length], source[offset .. offset + length]);
            value.value_bits = output_bytes;
            output_bytes += length;
        }
        cells[output_index] = value;
        output_index += 1;
        if (take_new) new_index += 1 else old_index += 1;
    }
    // Nothing visible changed until every requested replacement was prepared.
    session.current_region = resident;
    session.current_bank = bank;
    session.current_values = cells[0..output_index];
    session.current_bytes = payload[0..output_bytes];
    return .ok;
}

pub fn read(session: *state.Session, output: *protocol.CurrentReadOutput,
    values: []protocol.CurrentValue, bytes: []u8) ResultCode
{
    state.lock(&session.mutex);
    defer state.unlock(&session.mutex);
    if (readDestinationsOverlap(session, output, values, bytes)) return .invalid_argument;
    output.* = .{ .struct_size = @sizeOf(protocol.CurrentReadOutput), .cell_count = @intCast(session.current_values.len),
        .byte_count = @intCast(session.current_bytes.len), .reserved = 0 };
    if (values.len < session.current_values.len or bytes.len < session.current_bytes.len) return .buffer_too_small;
    @memcpy(values[0..session.current_values.len], session.current_values);
    @memcpy(bytes[0..session.current_bytes.len], session.current_bytes);
    return .ok;
}

// Selection visits only the requested keys. The committed sorted table remains
// the sole current owner; byte offsets are rewritten for the compact result.
pub fn readSelected(session: *state.Session, selection: []const protocol.CurrentKey,
    output: *protocol.CurrentReadOutput, values: []protocol.CurrentValue, bytes: []u8) ResultCode
{
    state.lock(&session.mutex);
    defer state.unlock(&session.mutex);
    if (readDestinationsOverlap(session, output, values, bytes) or
        overlaps(@ptrCast(selection.ptr), selection.len * @sizeOf(protocol.CurrentKey), @ptrCast(output), @sizeOf(protocol.CurrentReadOutput)) or
        overlaps(@ptrCast(selection.ptr), selection.len * @sizeOf(protocol.CurrentKey), @ptrCast(values.ptr), values.len * @sizeOf(protocol.CurrentValue)) or
        overlaps(@ptrCast(selection.ptr), selection.len * @sizeOf(protocol.CurrentKey), bytes.ptr, bytes.len)) return .invalid_argument;
    var cell_count: u32 = 0;
    var byte_count: u32 = 0;
    for (selection) |key| {
        if (key.dataset_handle == 0 or key.cell_handle == 0) return .invalid_argument;
        const value = find(session.current_values, key) orelse continue;
        cell_count = std.math.add(u32, cell_count, 1) catch return .invalid_argument;
        if (value.value_kind == @intFromEnum(protocol.ValueKind.bytes))
            byte_count = std.math.add(u32, byte_count, @intCast(value.value_high_bits)) catch return .invalid_argument;
    }
    output.* = .{ .struct_size = @sizeOf(protocol.CurrentReadOutput), .cell_count = cell_count,
        .byte_count = byte_count, .reserved = 0 };
    if (values.len < cell_count or bytes.len < byte_count) return .buffer_too_small;
    var output_index: usize = 0;
    var output_bytes: usize = 0;
    for (selection) |key| {
        var value = find(session.current_values, key) orelse continue;
        if (value.value_kind == @intFromEnum(protocol.ValueKind.bytes) and value.flags == 1) {
            const offset: usize = @intCast(value.value_bits);
            const length: usize = @intCast(value.value_high_bits);
            @memcpy(bytes[output_bytes .. output_bytes + length], session.current_bytes[offset .. offset + length]);
            value.value_bits = output_bytes;
            output_bytes += length;
        }
        values[output_index] = value;
        output_index += 1;
    }
    return .ok;
}

fn find(values: []const protocol.CurrentValue, key: protocol.CurrentKey) ?protocol.CurrentValue {
    var low: usize = 0;
    var high = values.len;
    while (low < high) {
        const mid = low + (high - low) / 2;
        const value = values[mid];
        if (value.dataset_handle < key.dataset_handle or (value.dataset_handle == key.dataset_handle and value.cell_handle < key.cell_handle))
            low = mid + 1
        else
            high = mid;
    }
    if (low == values.len or values[low].dataset_handle != key.dataset_handle or values[low].cell_handle != key.cell_handle) return null;
    return values[low];
}

fn readDestinationsOverlap(session: *const state.Session, output: *protocol.CurrentReadOutput,
    values: []protocol.CurrentValue, bytes: []u8) bool
{
    return overlaps(@ptrCast(output), @sizeOf(protocol.CurrentReadOutput), session.current_region.ptr, session.current_region.len) or
        overlaps(@ptrCast(values.ptr), values.len * @sizeOf(protocol.CurrentValue), session.current_region.ptr, session.current_region.len) or
        overlaps(bytes.ptr, bytes.len, session.current_region.ptr, session.current_region.len) or
        overlaps(@ptrCast(output), @sizeOf(protocol.CurrentReadOutput), @ptrCast(values.ptr), values.len * @sizeOf(protocol.CurrentValue)) or
        overlaps(@ptrCast(output), @sizeOf(protocol.CurrentReadOutput), bytes.ptr, bytes.len) or
        overlaps(@ptrCast(values.ptr), values.len * @sizeOf(protocol.CurrentValue), bytes.ptr, bytes.len);
}

fn sortedHandles(values: []const u64) bool {
    var previous: u64 = 0;
    for (values) |value| { if (value <= previous) return false; previous = value; }
    return true;
}
fn contains(values: []const u64, key: u64) bool {
    var low: usize = 0;
    var high = values.len;
    while (low < high) { const mid = low + (high - low) / 2; if (values[mid] < key) low = mid + 1 else high = mid; }
    return low < values.len and values[low] == key;
}
fn less(a: protocol.CurrentValue, b: protocol.CurrentValue) bool {
    return a.dataset_handle < b.dataset_handle or (a.dataset_handle == b.dataset_handle and a.cell_handle < b.cell_handle);
}
fn validValue(value: protocol.CurrentValue, bytes: []const u8, used: *u64) bool {
    if (value.flags > 1 or !protocol.knownEnum(protocol.ValueKind, value.value_kind)) return false;
    if (value.flags == 0) return value.value_bits == 0 and value.value_high_bits == 0;
    return switch (@as(protocol.ValueKind, @enumFromInt(value.value_kind))) {
        .float64 => value.value_high_bits == 0 and std.math.isFinite(@as(f64, @bitCast(value.value_bits))),
        .signed64, .unsigned64 => value.value_high_bits == 0,
        .boolean => value.value_high_bits == 0 and value.value_bits <= 1,
        .unsigned128 => true,
        .bytes => blk: {
            if (value.value_bits != used.* or value.value_high_bits > protocol.maximum_value_bytes or
                value.value_bits > bytes.len or value.value_high_bits > bytes.len - value.value_bits) break :blk false;
            used.* += value.value_high_bits;
            break :blk true;
        },
    };
}
fn overlaps(a: [*]const u8, an: usize, b: [*]const u8, bn: usize) bool {
    if (an == 0 or bn == 0) return false;
    const ar = scratch.Range.init(a, an) orelse return true;
    const br = scratch.Range.init(b, bn) orelse return true;
    return scratch.Range.overlaps(ar, br);
}
fn overlapsInput(target: []u8, input: *const protocol.CurrentCommitInput, requested: []const u64,
    retired: []const u64, values: []const protocol.CurrentValue, bytes: []const u8) bool {
    return overlaps(target.ptr, target.len, @ptrCast(input), @sizeOf(protocol.CurrentCommitInput)) or
        overlaps(target.ptr, target.len, @ptrCast(requested.ptr), requested.len * @sizeOf(u64)) or
        overlaps(target.ptr, target.len, @ptrCast(retired.ptr), retired.len * @sizeOf(u64)) or
        overlaps(target.ptr, target.len, @ptrCast(values.ptr), values.len * @sizeOf(protocol.CurrentValue)) or
        overlaps(target.ptr, target.len, bytes.ptr, bytes.len);
}
