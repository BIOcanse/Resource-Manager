const std = @import("std");

// Windows registry protocol slots. Custom chords live under crashdump, not Parameters.
pub const slot_count = 7;
pub const maximum_value_bytes = 256;
pub const Value = extern struct {
    present: u32 = 0,
    value_type: u32 = 0,
    length: u32 = 0,
    data: [maximum_value_bytes]u8 = @splat(0),
};
pub const Snapshot = extern struct { values: [slot_count]Value = @splat(.{}) };
pub const Request = extern struct {
    enabled: u32 = 0,
    custom: u32 = 0,
    dump1_keys: u32 = 0,
    dump2_key: u32 = 0,
};
pub const Reason = enum(u32) {
    ok = 0,
    invalid_settings = 1,
    read_failed = 2,
    write_failed = 3,
    verify_failed = 4,
    restore_pending = 5,
    invalid_snapshot = 6,
    access_denied = 7,
    value_too_large = 8,
    configuration_mismatch = 9,
};
pub const Operation = enum(u32) { none = 0, enable = 1, restore = 2 };
pub const Plan = extern struct {
    operation: Operation = .none,
    capture_previous: u32 = 0,
    reason: Reason = .ok,
};
pub const Result = extern struct {
    reason: Reason = .ok,
    detail: u32 = 0,
    failed_slot: u32 = 0,
    rollback_detail: u32 = 0,
    requires_restart: u32 = 0,
    kernel_dump_configured: u32 = 0,
};

// Adapter methods are synchronous. No retained registry handle, allocator, thread, or lease.
pub const Adapter = struct {
    context: ?*anyopaque,
    read: *const fn (?*anyopaque, u32, *Value) u32,
    write: *const fn (?*anyopaque, u32, *const Value) u32,
};

// Microsoft's driver scan-code table. Dump2Key is an index, not a virtual key or scan code.
// https://learn.microsoft.com/windows-hardware/drivers/debugger/forcing-a-system-crash-from-the-keyboard
const key_to_scan = [_]u8{
    0x00, 0x29, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09,
    0x0A, 0x0B, 0x0C, 0x0D, 0x7D, 0x0E, 0x0F, 0x10, 0x11, 0x12,
    0x13, 0x14, 0x15, 0x16, 0x17, 0x18, 0x19, 0x1A, 0x1B, 0x00,
    0x3A, 0x1E, 0x1F, 0x20, 0x21, 0x22, 0x23, 0x24, 0x25, 0x26,
    0x27, 0x28, 0x2B, 0x1C, 0x2A, 0x00, 0x2C, 0x2D, 0x2E, 0x2F,
    0x30, 0x31, 0x32, 0x33, 0x34, 0x35, 0x73, 0x36, 0x1D, 0x00,
    0x38, 0x39, 0xB8, 0x00, 0x9D, 0x00, 0x00, 0x00, 0x00, 0x00,
    0x00, 0x00, 0x00, 0x00, 0x00, 0xD2, 0xD3, 0x00, 0x00, 0xCB,
    0xC7, 0xCF, 0x00, 0xC8, 0xD0, 0xC9, 0xD1, 0x00, 0x00, 0xCD,
    0x45, 0x47, 0x4B, 0x4F, 0x00, 0xB5, 0x48, 0x4C, 0x50, 0x52,
    0x37, 0x49, 0x4D, 0x51, 0x53, 0x4A, 0x4E, 0x00, 0x9C, 0x00,
    0x01, 0x00, 0x3B, 0x3C, 0x3D, 0x3E, 0x3F, 0x40, 0x41, 0x42,
    0x43, 0x44, 0x57, 0x58, 0x00, 0x46, 0x00, 0x00, 0x00, 0x00,
    0x00, 0x7B, 0x79, 0x70,
};

pub fn validate(request: Request) Reason {
    if (request.enabled > 1 or request.custom > 1) return .invalid_settings;
    if (request.custom == 0) return if (request.dump1_keys == 0 and request.dump2_key == 0) .ok else .invalid_settings;
    if (request.dump1_keys == 0 or request.dump1_keys & ~@as(u32, 0x77) != 0) return .invalid_settings;
    if (request.dump2_key >= key_to_scan.len) return .invalid_settings;
    return if (key_to_scan[request.dump2_key] != 0 or request.dump2_key == 124) .ok else .invalid_settings;
}

pub fn plan(previous: Request, desired: Request, has_snapshot: bool) Plan {
    const reason = validate(desired);
    if (reason != .ok) return .{ .reason = reason };
    if (desired.enabled == 0) return .{ .operation = if (has_snapshot) .restore else .none };
    if (previous.enabled == 0 and has_snapshot) return .{ .reason = .restore_pending };
    const changed = previous.enabled != desired.enabled or previous.custom != desired.custom or
        previous.dump1_keys != desired.dump1_keys or previous.dump2_key != desired.dump2_key;
    return .{ .operation = if (changed or !has_snapshot) .enable else .none, .capture_previous = if (!has_snapshot) 1 else 0 };
}

pub fn validSnapshot(snapshot: *const Snapshot) bool {
    var index: usize = 0;
    while (index < slot_count) : (index += 1) {
        const value = &snapshot.values[index];
        if (value.present > 1 or value.length > maximum_value_bytes) return false;
        if (value.present == 0 and (value.length != 0 or value.value_type != 0)) return false;
    }
    return true;
}

fn failure(code: u32, slot: usize, fallback: Reason) Result {
    return .{ .reason = if (code == 5) .access_denied else if (code == 234) .value_too_large else fallback, .detail = code, .failed_slot = @intCast(slot) };
}

pub fn capture(adapter: Adapter, snapshot: *Snapshot) Result {
    snapshot.* = .{};
    var index: usize = 0;
    while (index < slot_count) : (index += 1) {
        const status = adapter.read(adapter.context, @intCast(index), &snapshot.values[index]);
        if (status != 0) return failure(status, index, .read_failed);
    }
    return if (validSnapshot(snapshot)) .{ .kernel_dump_configured = @intFromBool(kernelDump(snapshot.values[6])) } else .{ .reason = .invalid_snapshot };
}

pub fn dword(number: u32) Value {
    var value: Value = .{ .present = 1, .value_type = 4, .length = 4 };
    std.mem.writeInt(u32, value.data[0..4], number, .little);
    return value;
}

pub fn kernelDump(value: Value) bool {
    if (value.present != 1 or value.value_type != 4 or value.length != 4) return false;
    // Complete, kernel, automatic and active memory dumps contain kernel memory; small dump does not.
    // https://learn.microsoft.com/troubleshoot/windows-server/performance/memory-dump-file-options
    return switch (std.mem.readInt(u32, value.data[0..4], .little)) {
        1, 2, 7 => true,
        else => false,
    };
}

fn same(left: *const Value, right: *const Value) bool {
    if (left.length > maximum_value_bytes or right.length > maximum_value_bytes) return false;
    return left.present == right.present and left.value_type == right.value_type and left.length == right.length and
        std.mem.eql(u8, left.data[0..left.length], right.data[0..right.length]);
}

// Read-only status: a saved preference is never represented as an observed registry fact.
pub fn inspect(adapter: Adapter, desired: Request, baseline: ?*const Snapshot) Result {
    if (validate(desired) != .ok) return .{ .reason = .invalid_settings };
    if (baseline) |saved| {
        if (!validSnapshot(saved)) return .{ .reason = .invalid_snapshot };
    }
    var actual: Snapshot = .{};
    var result = capture(adapter, &actual);
    if (result.reason != .ok) return result;
    result.requires_restart = 1;
    var expected = actual;
    if (desired.enabled != 0) {
        var device: usize = 0;
        while (device < 2) : (device += 1) {
            const base = device * 3;
            expected.values[base] = if (desired.custom == 0) dword(1) else .{};
            expected.values[base + 1] = if (desired.custom != 0) dword(desired.dump1_keys) else .{};
            expected.values[base + 2] = if (desired.custom != 0) dword(desired.dump2_key) else .{};
        }
        if (!kernelDump(actual.values[6])) expected.values[6] = dword(2);
    } else if (baseline) |saved| {
        if (!validSnapshot(saved)) return .{ .reason = .invalid_snapshot };
        expected = saved.*;
    }
    var index: usize = 0;
    while (index < slot_count) : (index += 1) {
        if (!same(&expected.values[index], &actual.values[index])) {
            result.reason = .configuration_mismatch;
            result.failed_slot = @intCast(index);
            break;
        }
    }
    return result;
}

// The caller MUST durably save baseline with the app setting before invoking this function.
// Every failed write/verification restores this call's pre-image, keeping the saved baseline for recovery.
pub fn apply(adapter: Adapter, desired: Request, baseline: *const Snapshot) Result {
    const reason = validate(desired);
    if (reason != .ok) return .{ .reason = reason };
    if (!validSnapshot(baseline)) return .{ .reason = .invalid_snapshot };
    var before: Snapshot = .{};
    const captured = capture(adapter, &before);
    if (captured.reason != .ok) return captured;
    var target = baseline.*;
    if (desired.enabled != 0) {
        target = before;
        var device: usize = 0;
        while (device < 2) : (device += 1) {
            const base = device * 3;
            target.values[base] = if (desired.custom == 0) dword(1) else .{};
            target.values[base + 1] = if (desired.custom != 0) dword(desired.dump1_keys) else .{};
            target.values[base + 2] = if (desired.custom != 0) dword(desired.dump2_key) else .{};
        }
        if (!kernelDump(before.values[6])) target.values[6] = dword(2);
    }
    var result: Result = .{ .requires_restart = 1, .kernel_dump_configured = @intFromBool(kernelDump(target.values[6])) };
    var index: usize = 0;
    while (index < slot_count) : (index += 1) {
        const status = adapter.write(adapter.context, @intCast(index), &target.values[index]);
        if (status != 0) {
            result = failure(status, index, .write_failed);
            break;
        }
        var readback: Value = .{};
        const read_status = adapter.read(adapter.context, @intCast(index), &readback);
        if (read_status != 0) {
            result = failure(read_status, index, .verify_failed);
            break;
        }
        if (!same(&target.values[index], &readback)) {
            result = .{ .reason = .verify_failed, .failed_slot = @intCast(index) };
            break;
        }
    }
    if (result.reason == .ok) return result;
    // Restore all slots, including the possibly-partially-written failing one; never stop after one rollback failure.
    index = 0;
    while (index < slot_count) : (index += 1) {
        const status = adapter.write(adapter.context, @intCast(index), &before.values[index]);
        if (status != 0 and result.rollback_detail == 0) result.rollback_detail = status;
        var readback: Value = .{};
        const verify = adapter.read(adapter.context, @intCast(index), &readback);
        if (result.rollback_detail == 0 and (verify != 0 or !same(&before.values[index], &readback)))
            result.rollback_detail = if (verify != 0) verify else 13;
    }
    return result;
}
