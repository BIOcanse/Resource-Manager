//! Focused consumer-level checks for Native C ABI output/input aliasing.
//! This is an SDK-owned source test paired with `owned/native.zig`.
const std = @import("std");
const native = @import("native.zig");
const types = @import("types.zig");
const uid = @import("../uid.zig");

const Uid = uid.Identifier(128);
const attributes: types.Attributes = .{
    .recoverability = .recoverable,
    .recovery_cost_coefficient = 1,
    .access_loss_impact = .unobservable_now,
};
const declarations = [_]types.Table{.{
    .capacity = 4,
    .partition_capacity = 1,
    .reserved_start = 0,
    .reserved_count = 0,
    .attributes = attributes,
    .policy = .{
        .score_epochs = 4,
        .idle_epochs = 4,
        .protection = .concentrated,
        .concentrated_trigger_percent = 50,
        .concentrated_target_percent = 75,
        .smooth_trigger_percent = 80,
        .smooth_emergency_percent = 60,
        .smooth_interval_epochs = 1,
        .smooth_release_count = 1,
        .tick_maximum_actions = 1,
        .release_check = .{ .enabled = false, .tolerance_parts_per_million = 0, .tolerance_bytes = 0 },
    },
}};
const fixed: [0]types.For(Uid).Fixed = .{};
const Binding = native.Binding(
    .{ .uid_width = 128, .activity_width = 8, .index_buckets = 8 },
    &declarations,
    &fixed,
    .{ .structure_hash = @splat(0), .policy_hash = @splat(0) },
);

const BorrowUidOverlap = extern union {
    borrow: native.Borrow,
    uid: [@sizeOf(Uid)]u8,
};

const LeaseBorrowOverlap = extern union {
    lease: native.Lease,
    borrow: native.Borrow,
};

test "Native outputs reject UID and lease aliases before clearing caller memory" {
    var state: Binding.StateType = .{};
    var instance: ?*anyopaque = null;
    try std.testing.expectEqual(
        @intFromEnum(native.Status.ok),
        Binding.rm_ledger_create(@ptrCast(&state), @intCast(@sizeOf(Binding.StateType)), &instance),
    );

    var operation: Binding.OperationWorkspaceType = .{};
    const table_target = native.Target{ .kind = 0, .table = 0, .parent_uid = null, .parent_uid_length = 0 };
    var zero_uid = @as([@sizeOf(Uid)]u8, @splat(0));
    var initial_borrow = std.mem.zeroes(native.Borrow);
    try std.testing.expectEqual(
        @intFromEnum(native.Status.newly_allocated),
        Binding.rm_ledger_push(
            instance,
            &table_target,
            zero_uid[0..].ptr,
            @intCast(zero_uid.len),
            1,
            17,
            @ptrCast(&operation),
            @intCast(@sizeOf(Binding.OperationWorkspaceType)),
            &initial_borrow,
        ),
    );
    try std.testing.expectEqual(
        @intFromEnum(native.Status.completed),
        Binding.rm_ledger_end_use(instance, &initial_borrow.lease, 0),
    );

    var uid_alias = BorrowUidOverlap{ .uid = @as([@sizeOf(Uid)]u8, @splat(0x73)) };
    const uid_before = uid_alias.uid;
    try std.testing.expectEqual(
        @intFromEnum(native.Status.input_error),
        Binding.rm_ledger_use(instance, uid_alias.uid[0..].ptr, @intCast(uid_alias.uid.len), &uid_alias.borrow),
    );
    try std.testing.expectEqualSlices(u8, uid_before[0..], uid_alias.uid[0..]);

    var lease_alias = LeaseBorrowOverlap{ .lease = .{
        .slot = 2,
        .reserved = 0,
        .allocation_number = 41,
        .use_generation = 7,
    } };
    const lease_before = lease_alias.lease;
    const update = native.Update{
        .present_mask = 0,
        .recoverability = 0,
        .recovery_cost_coefficient = 0,
        .access_loss_impact = 0,
        .allocation_bytes = 0,
    };
    try std.testing.expectEqual(
        @intFromEnum(native.Status.input_error),
        Binding.rm_ledger_update_lease(instance, &lease_alias.lease, &update, &lease_alias.borrow),
    );
    try std.testing.expectEqualDeep(lease_before, lease_alias.lease);

    try std.testing.expectEqual(
        @intFromEnum(native.Status.ok),
        Binding.rm_ledger_close(
            instance,
            zero_uid[0..].ptr,
            @intCast(zero_uid.len),
            @ptrCast(&operation),
            @intCast(@sizeOf(Binding.OperationWorkspaceType)),
        ),
    );
    var destruction: Binding.DestroyWorkspaceType = .{};
    try std.testing.expectEqual(
        @intFromEnum(native.Status.ok),
        Binding.rm_ledger_destroy(
            instance,
            0,
            @ptrCast(&destruction),
            @intCast(@sizeOf(Binding.DestroyWorkspaceType)),
        ),
    );
}
