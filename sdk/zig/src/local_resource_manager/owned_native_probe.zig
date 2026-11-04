//! Comparison-only C exports for the current typed owner.
//! This module is intentionally separate from the production Native ABI.
const comparison = @import("owned/native_comparison.zig");

pub export fn rm_owned_native_probe_storage_size() callconv(.c) usize {
    return comparison.storageSize();
}

pub export fn rm_owned_native_probe_storage_alignment() callconv(.c) usize {
    return comparison.storageAlignment();
}

pub export fn rm_owned_native_probe_uid_width_bits() callconv(.c) u32 {
    return comparison.uidWidthBits();
}

pub export fn rm_owned_native_probe_candidate() callconv(.c) u32 {
    return comparison.candidateId();
}

pub export fn rm_owned_native_probe_create(
    caller_storage: ?*anyopaque,
    storage_bytes: usize,
    out_instance: ?*?*anyopaque,
) callconv(.c) i32 {
    return comparison.create(caller_storage, storage_bytes, out_instance);
}

pub export fn rm_owned_native_probe_destroy(instance: ?*anyopaque, forced: u8) callconv(.c) i32 {
    return comparison.destroy(instance, forced);
}

pub export fn rm_owned_native_probe_push(
    instance: ?*anyopaque,
    target_kind: u8,
    target_table: u32,
    target_parent_uid: ?[*]const u8,
    target_parent_uid_length: usize,
    uid: ?[*]const u8,
    uid_length: usize,
    has_allocation_bytes: u8,
    allocation_bytes: u64,
    out_slot: ?*u32,
    out_allocation_number: ?*u64,
    out_use_generation: ?*u64,
    out_region: ?*?[*]u8,
    out_region_length: ?*usize,
) callconv(.c) i32 {
    return comparison.push(
        instance,
        target_kind,
        target_table,
        target_parent_uid,
        target_parent_uid_length,
        uid,
        uid_length,
        has_allocation_bytes,
        allocation_bytes,
        out_slot,
        out_allocation_number,
        out_use_generation,
        out_region,
        out_region_length,
    );
}

pub export fn rm_owned_native_probe_use(
    instance: ?*anyopaque,
    uid: ?[*]const u8,
    uid_length: usize,
    out_slot: ?*u32,
    out_allocation_number: ?*u64,
    out_use_generation: ?*u64,
    out_region: ?*?[*]u8,
    out_region_length: ?*usize,
) callconv(.c) i32 {
    return comparison.use(
        instance,
        uid,
        uid_length,
        out_slot,
        out_allocation_number,
        out_use_generation,
        out_region,
        out_region_length,
    );
}

pub export fn rm_owned_native_probe_use_fixed(
    instance: ?*anyopaque,
    declaration: u32,
    out_slot: ?*u32,
    out_allocation_number: ?*u64,
    out_use_generation: ?*u64,
    out_region: ?*?[*]u8,
    out_region_length: ?*usize,
) callconv(.c) i32 {
    return comparison.useFixed(
        instance,
        declaration,
        out_slot,
        out_allocation_number,
        out_use_generation,
        out_region,
        out_region_length,
    );
}

pub export fn rm_owned_native_probe_push_partition(
    instance: ?*anyopaque,
    target_kind: u8,
    target_table: u32,
    target_parent_uid: ?[*]const u8,
    target_parent_uid_length: usize,
    uid: ?[*]const u8,
    uid_length: usize,
    capacity: u32,
    recoverability: u8,
    recovery_cost_coefficient: u32,
    access_loss_impact: u8,
    out_created: ?*u8,
    out_allocation_number: ?*u64,
) callconv(.c) i32 {
    return comparison.pushPartition(
        instance,
        target_kind,
        target_table,
        target_parent_uid,
        target_parent_uid_length,
        uid,
        uid_length,
        capacity,
        recoverability,
        recovery_cost_coefficient,
        access_loss_impact,
        out_created,
        out_allocation_number,
    );
}

pub export fn rm_owned_native_probe_end_use(
    instance: ?*anyopaque,
    slot: u32,
    allocation_number: u64,
    use_generation: u64,
    disposition: u8,
) callconv(.c) i32 {
    return comparison.endUse(instance, slot, allocation_number, use_generation, disposition);
}

pub export fn rm_owned_native_probe_update_uid(
    instance: ?*anyopaque,
    uid: ?[*]const u8,
    uid_length: usize,
    has_allocation_bytes: u8,
    allocation_bytes: u64,
    attribute_mask: u8,
    recoverability: u8,
    recovery_cost_coefficient: u32,
    access_loss_impact: u8,
) callconv(.c) i32 {
    return comparison.updateUid(
        instance,
        uid,
        uid_length,
        has_allocation_bytes,
        allocation_bytes,
        attribute_mask,
        recoverability,
        recovery_cost_coefficient,
        access_loss_impact,
    );
}

pub export fn rm_owned_native_probe_update_lease(
    instance: ?*anyopaque,
    slot: u32,
    allocation_number: u64,
    use_generation: u64,
    has_allocation_bytes: u8,
    allocation_bytes: u64,
    attribute_mask: u8,
    recoverability: u8,
    recovery_cost_coefficient: u32,
    access_loss_impact: u8,
    out_region: ?*?[*]u8,
    out_region_length: ?*usize,
) callconv(.c) i32 {
    return comparison.updateLease(
        instance,
        slot,
        allocation_number,
        use_generation,
        has_allocation_bytes,
        allocation_bytes,
        attribute_mask,
        recoverability,
        recovery_cost_coefficient,
        access_loss_impact,
        out_region,
        out_region_length,
    );
}

pub export fn rm_owned_native_probe_close(
    instance: ?*anyopaque,
    uid: ?[*]const u8,
    uid_length: usize,
) callconv(.c) i32 {
    return comparison.close(instance, uid, uid_length);
}

pub export fn rm_owned_native_probe_force_release(
    instance: ?*anyopaque,
    uid: ?[*]const u8,
    uid_length: usize,
) callconv(.c) i32 {
    return comparison.forceRelease(instance, uid, uid_length);
}

pub export fn rm_owned_native_probe_set_mode(
    instance: ?*anyopaque,
    table: u32,
    mode: u8,
) callconv(.c) i32 {
    return comparison.setMode(instance, table, mode);
}

pub export fn rm_owned_native_probe_tick(
    instance: ?*anyopaque,
    out_released: ?*usize,
    out_scanned: ?*usize,
) callconv(.c) i32 {
    return comparison.tick(instance, out_released, out_scanned);
}

pub export fn rm_owned_native_probe_view(
    instance: ?*anyopaque,
    table: u32,
    out_registered_bytes: ?*u64,
    out_system_page_bytes: ?*u64,
) callconv(.c) i32 {
    return comparison.view(instance, table, out_registered_bytes, out_system_page_bytes);
}

pub export fn rm_owned_native_probe_subscribe(
    instance: ?*anyopaque,
    table: u32,
    callback: ?comparison.Callback,
    context: ?*anyopaque,
    out_table: ?*u32,
    out_slot: ?*u32,
    out_generation: ?*u64,
) callconv(.c) i32 {
    return comparison.subscribe(instance, table, callback, context, out_table, out_slot, out_generation);
}

pub export fn rm_owned_native_probe_unsubscribe(
    instance: ?*anyopaque,
    table: u32,
    slot: u32,
    generation: u64,
) callconv(.c) i32 {
    return comparison.unsubscribe(instance, table, slot, generation);
}
