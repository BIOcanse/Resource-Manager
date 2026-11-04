//! Zig 0.17 public C ABI lifecycle fixture (including static Linux musl).
//! Tiny deterministic test payload; choose eligible stable, substantial data in production.
const std = @import("std");
const ledger_entry = @import("ledger_entry");
const ledger_sdk = @import("ledger_sdk");
const ledger_values = @import("ledger_values");

const native = ledger_sdk.native;
const UidWidthBits: u16 = @intCast(ledger_values.Bound.uid_width_bits);
const Uid = ledger_sdk.uid.Identifier(UidWidthBits);
const Binding = ledger_entry.LedgerBinding;
const allocation_length: usize = 64;
const allocation_bytes: u64 = @intCast(allocation_length);
const pattern: u8 = 0x5a;

fn expectStatus(actual: i32, expected: native.Status) !void {
    if (actual != @backingInt(expected)) return error.UnexpectedNativeStatus;
}

pub fn main() !void {
    const allocator = std.heap.page_allocator;

    var descriptor = std.mem.zeroes(native.Descriptor);
    try expectStatus(Binding.rm_ledger_query_descriptor(&descriptor), .ok);
    if (descriptor.uid_width_bits != UidWidthBits or
        descriptor.instance_size != @sizeOf(Binding.StateType) or
        descriptor.operation_workspace_size != @sizeOf(Binding.OperationWorkspaceType) or
        descriptor.destroy_workspace_size != @sizeOf(Binding.DestroyWorkspaceType))
        return error.GeneratedBindingMismatch;

    // The public binding exposes descriptor-sized storage and workspace types.
    // Allocate and zero them before calling the generated Native owner.
    const state = try allocator.create(Binding.StateType);
    defer allocator.destroy(state);
    @memset(std.mem.asBytes(state), 0);

    const operation = try allocator.create(Binding.OperationWorkspaceType);
    defer allocator.destroy(operation);
    @memset(std.mem.asBytes(operation), 0);

    const destruction = try allocator.create(Binding.DestroyWorkspaceType);
    defer allocator.destroy(destruction);
    @memset(std.mem.asBytes(destruction), 0);

    var instance: ?*anyopaque = null;
    try expectStatus(Binding.rm_ledger_create(
        @ptrCast(state),
        @intCast(@sizeOf(Binding.StateType)),
        &instance,
    ), .ok);
    const live = instance orelse return error.NativeCreateReturnedNull;
    var owner_created = true;
    defer {
        // This example has no concurrent or asynchronous access still running.
        // Force destruction is only the error cleanup for this owned instance.
        if (owner_created) {
            const status = Binding.rm_ledger_destroy(
                live,
                1,
                @ptrCast(destruction),
                @intCast(@sizeOf(Binding.DestroyWorkspaceType)),
            );
            std.debug.assert(status == @backingInt(native.Status.ok));
        }
    }

    var uid: Uid = .{ .bytes = @splat(0) };
    uid.bytes[0] = 0xd1;
    const target = native.Target{
        .kind = 0, // table target
        .table = 0,
        .parent_uid = null,
        .parent_uid_length = 0,
    };

    var loading = std.mem.zeroes(native.Borrow);
    try expectStatus(Binding.rm_ledger_push(
        live,
        &target,
        uid.bytes[0..].ptr,
        @intCast(uid.bytes.len),
        1,
        allocation_bytes,
        @ptrCast(operation),
        @intCast(@sizeOf(Binding.OperationWorkspaceType)),
        &loading,
    ), .newly_allocated);
    if (loading.length != allocation_bytes or loading.data == null)
        return error.InvalidLoadingBorrow;
    @memset(loading.data.?[0..allocation_length], pattern);
    try expectStatus(Binding.rm_ledger_end_use(live, &loading.lease, 0), .completed);

    var resident = std.mem.zeroes(native.Borrow);
    try expectStatus(Binding.rm_ledger_use(
        live,
        uid.bytes[0..].ptr,
        @intCast(uid.bytes.len),
        &resident,
    ), .in_memory);
    if (resident.length != allocation_bytes or resident.data == null)
        return error.InvalidResidentBorrow;
    for (resident.data.?[0..allocation_length]) |byte| {
        if (byte != pattern) return error.ResourceContentMismatch;
    }
    try expectStatus(Binding.rm_ledger_end_use(live, &resident.lease, 0), .completed);

    // The example's table 0 declares recoverable, non-Fatal data.
    // Real callers must preserve their resource's actual recovery properties.
    try expectStatus(Binding.rm_ledger_close(
        live,
        uid.bytes[0..].ptr,
        @intCast(uid.bytes.len),
        @ptrCast(operation),
        @intCast(@sizeOf(Binding.OperationWorkspaceType)),
    ), .ok);
    var ticks = std.mem.zeroes(native.TickResult);
    const tick = try allocator.create(Binding.TickWorkspaceType);
    defer allocator.destroy(tick);
    @memset(std.mem.asBytes(tick), 0);
    try expectStatus(Binding.rm_ledger_tick(live, @ptrCast(tick), @sizeOf(Binding.TickWorkspaceType), &ticks), .ok);
    var failed_load = std.mem.zeroes(native.Borrow);
    try expectStatus(Binding.rm_ledger_push(live, &target, uid.bytes[0..].ptr, uid.bytes.len, 1, allocation_bytes, @ptrCast(operation), @sizeOf(Binding.OperationWorkspaceType), &failed_load), .newly_allocated);
    try expectStatus(Binding.rm_ledger_end_use(live, &failed_load.lease, 2), .abandoned);
    var absent = std.mem.zeroes(native.Borrow);
    try expectStatus(Binding.rm_ledger_use(live, uid.bytes[0..].ptr, uid.bytes.len, &absent), .not_in_memory);
    try expectStatus(Binding.rm_ledger_tick(live, @ptrCast(tick), @sizeOf(Binding.TickWorkspaceType), &ticks), .ok);
    try expectStatus(Binding.rm_ledger_destroy(
        live,
        0,
        @ptrCast(destruction),
        @intCast(@sizeOf(Binding.DestroyWorkspaceType)),
    ), .ok);
    owner_created = false;

    std.debug.print("ledger UID bytes: {any}\n", .{uid.bytes});
    std.debug.print("ledger Native lifecycle: pass (UID{d})\n", .{UidWidthBits});
}
