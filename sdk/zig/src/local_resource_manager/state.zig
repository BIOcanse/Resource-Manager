const std = @import("std");
const protocol = @import("protocol.zig");
const identity = @import("identity.zig");
const layout = @import("layout.zig");
const activity = @import("activity.zig");
const operation_sequence = @import("operation_sequence.zig");

pub fn requiredBytes(config: protocol.Config) !usize {
    return layout.requiredBytes(config);
}

pub fn alignment() usize {
    return layout.alignment();
}

pub fn initialize(buffer: []u8, config: protocol.Config, manager_instance_id: u64) !void {
    if (!protocol.validConfig(config) or manager_instance_id == 0) return error.InvalidArgument;
    if (buffer.len != try requiredBytes(config) or @intFromPtr(buffer.ptr) % alignment() != 0) {
        return error.InvalidState;
    }

    @memset(buffer, 0);
    const view = try layout.openUnchecked(buffer, config);
    view.header.* = .{
        .config = config,
        .total_size = buffer.len,
        .manager_instance_id = manager_instance_id,
        .snapshot_generation = 1,
        .activity_epoch = 1,
        .next_operation_id = 1,
        .next_operation_generation = 1,
        .active_operation_id = 0,
        .active_operation_generation = 0,
        .active_operation_start_snapshot_generation = 0,
        .active_operation_settled_execution_count = 0,
        .active_operation_confirmed_effect_count = 0,
        .active_operation_kind = .maintenance,
        .active_operation_phase = .inactive,
        .active_operation_reserved0 = .{ 0, 0, 0, 0, 0, 0 },
        .next_table_generation = 1,
        .next_resource_generation = 1,
        .next_partition_generation = 1,
        .next_capability_generation = 1,
        .next_pending_generation = 1,
        .next_attempt_id = 1,
        .table_count = 0,
        .resource_count = 0,
        .partition_count = 0,
        .ledger_cell_count = 0,
        .capability_count = 0,
        .pending_count = 0,
        .table_free_head = 0,
        .resource_free_head = 0,
        .partition_free_head = 0,
        .capability_free_head = 0,
        .pending_free_head = 0,
        .uid_tombstone_count = 0,
        .reserved0 = 0,
        .next_partition_admission_id = 1,
        .reserved1 = 0,
    };
    initializeFreeList(layout.TableSlot, view.tables);
    initializeFreeList(layout.ResourceSlot, view.resources);
    initializeFreeList(layout.PartitionSlot, view.partitions);
    initializeFreeList(layout.CapabilitySlot, view.capabilities);
    initializeFreeList(layout.PendingSlot, view.pending);
}

pub fn open(buffer: []u8) !layout.View {
    if (buffer.len < @sizeOf(layout.Header) or @intFromPtr(buffer.ptr) % alignment() != 0) {
        return error.InvalidState;
    }
    const header: *layout.Header = @ptrCast(@alignCast(buffer.ptr));
    if (!protocol.validConfig(header.config) or header.total_size != buffer.len or
        header.manager_instance_id == 0 or header.snapshot_generation == 0 or
        header.activity_epoch == 0 or header.next_operation_id == 0 or
        header.next_operation_generation == 0 or header.next_table_generation == 0 or
        header.next_resource_generation == 0 or header.next_partition_generation == 0 or
        header.next_capability_generation == 0 or
        header.next_pending_generation == 0 or header.next_attempt_id == 0 or
        header.next_partition_admission_id == 0 or
        header.table_count > header.config.table_capacity or
        header.resource_count > header.config.resource_capacity or
        header.partition_count > header.config.partition_capacity or
        header.ledger_cell_count > header.config.resource_capacity or
        header.capability_count > header.config.capability_capacity or
        header.pending_count > header.config.pending_capacity or header.reserved0 != 0 or
        header.reserved1 != 0 or
        !allZero(header.active_operation_reserved0[0..]) or
        !validOperationHeader(header) or
        !validFreeHead(header.table_free_head, header.config.table_capacity) or
        !validFreeHead(header.resource_free_head, header.config.resource_capacity) or
        !validFreeHead(header.partition_free_head, header.config.partition_capacity) or
        !validFreeHead(header.capability_free_head, header.config.capability_capacity) or
        !validFreeHead(header.pending_free_head, header.config.pending_capacity) or
        header.uid_tombstone_count > header.config.uid_bucket_capacity or
        buffer.len != try requiredBytes(header.config))
    {
        return error.InvalidState;
    }
    const view = try layout.openUnchecked(buffer, header.config);
    try validateCounts(view);
    return view;
}

pub fn registerTable(view: layout.View, spec: protocol.TableSpec) !protocol.TableHandle {
    try operation_sequence.enterMutationImplicit(view);
    if (!protocol.validTableSpec(spec) or spec.capacity > view.header.config.resource_capacity) {
        return error.InvalidArgument;
    }
    var reserved_capacity: u64 = spec.capacity;
    for (view.tables) |*table| {
        if (isOccupied(table) and protocol.sameId(table.table_id, spec.table_id)) {
            return error.DuplicateTable;
        }
        if (isOccupied(table)) {
            reserved_capacity = std.math.add(
                u64,
                reserved_capacity,
                table.capacity,
            ) catch return error.ResourceFull;
        }
    }
    if (reserved_capacity > view.header.config.resource_capacity) return error.ResourceFull;
    var free_cell_count: u32 = 0;
    for (view.ledger_cells) |*cell| {
        if (!isOccupied(cell)) free_cell_count += 1;
    }
    if (free_cell_count < spec.capacity) return error.ResourceFull;
    try ensureSnapshotAdvance(view.header);
    const slot_index = view.header.table_free_head;
    if (slot_index == layout.no_slot) return error.TableFull;
    const generation = try takeGeneration(&view.header.next_table_generation);
    const slot = &view.tables[slot_index];
    view.header.table_free_head = slot.next_free;
    slot.* = .{
        .table_id = spec.table_id,
        .adapter_key = spec.adapter_key,
        .mode_capability = std.mem.zeroes(protocol.CapabilityBinding),
        .capacity_capability = std.mem.zeroes(protocol.CapabilityBinding),
        .table_incarnation = spec.table_incarnation,
        .topology_generation = spec.topology_generation,
        .generation = generation,
        .revision = 1,
        .partition_revision = 1,
        .last_paced_epoch = 0,
        .capacity = spec.capacity,
        .occupied_count = 0,
        .direct_occupied_count = 0,
        .partition_reservation_start = spec.partition_reservation_start,
        .partition_reservation_capacity = spec.partition_reservation_capacity,
        .active_pending_count = 0,
        .first_resource_slot = layout.no_slot,
        .first_ledger_cell = layout.no_slot,
        .first_partition_slot = layout.no_slot,
        .next_free = layout.no_slot,
        .flags = layout.occupied_flag,
        .domain = spec.domain,
        .has_paced_epoch = 0,
        .reserved0 = .{ 0, 0 },
        .paced_snapshot_generation = 0,
        .active_partition_admission_id = 0,
        .active_partition_admission_hash = 0,
        .active_partition_operation_id = 0,
        .active_partition_operation_generation = 0,
    };
    allocateTableCells(
        view,
        slot_index,
        slot,
        spec.capacity,
        spec.partition_reservation_start,
        spec.partition_reservation_capacity,
    );
    view.header.table_count += 1;
    advanceSnapshotAssumeAvailable(view.header);
    return tableHandle(view, slot_index);
}

pub fn unregisterTable(view: layout.View, handle: protocol.TableHandle) !void {
    try operation_sequence.enterMutationImplicit(view);
    const table = try resolveTable(view, handle);
    if (table.first_partition_slot != layout.no_slot) return error.PartitionBusy;
    if (table.active_pending_count != 0) return error.ResourceBusy;
    var resource_index = table.first_resource_slot;
    while (resource_index != layout.no_slot) {
        const resource = &view.resources[resource_index];
        if (resource.protected_use != 0 or resource.pending_slot_index != layout.no_slot) {
            return error.ResourceBusy;
        }
        resource_index = resource.next_table_resource;
    }
    try ensureSnapshotAdvance(view.header);

    resource_index = table.first_resource_slot;
    while (resource_index != layout.no_slot) {
        const resource = &view.resources[resource_index];
        const next_resource = resource.next_table_resource;
        removeUid(view, handle.slot_index, table, resource.resource_uid);
        clearResourceCell(view, resource_index, resource);
        unlinkResource(view, resource_index, table);
        freeResourceSlot(view, resource_index);
        resource_index = next_resource;
    }
    var capability_index: usize = 0;
    while (capability_index < view.capabilities.len) : (capability_index += 1) {
        const capability = &view.capabilities[capability_index];
        if (!isOccupied(capability) or capability.table_slot_index != handle.slot_index) continue;
        freeCapabilitySlot(view, @intCast(capability_index));
    }
    freeTableCells(view, table);
    freeTableSlot(view, handle.slot_index);
    maybeRebuildUidIndex(view);
    advanceSnapshotAssumeAvailable(view.header);
}

pub fn registerCapability(
    view: layout.View,
    table_handle: protocol.TableHandle,
    spec: protocol.CapabilitySpec,
) !protocol.CapabilityHandle {
    try operation_sequence.enterMutationImplicit(view);
    if (!protocol.validCapabilitySpec(spec)) return error.InvalidArgument;
    const table = try resolveTable(view, table_handle);
    for (view.capabilities) |*capability| {
        if (isOccupied(capability) and capability.table_slot_index == table_handle.slot_index and
            capability.capability_id == spec.capability_id)
        {
            return error.DuplicateCapability;
        }
    }
    const roles = capabilityRoles(spec.expected_effects);
    if ((roles.mode and !protocol.isEmptyBinding(table.mode_capability)) or
        (roles.capacity and !protocol.isEmptyBinding(table.capacity_capability)))
    {
        return error.IntentConflict;
    }
    const next_table_revision = try increment(table.revision);
    try ensureSnapshotAdvance(view.header);
    const slot_index = view.header.capability_free_head;
    if (slot_index == layout.no_slot) return error.CapabilityFull;
    const generation = try takeGeneration(&view.header.next_capability_generation);
    const slot = &view.capabilities[slot_index];
    view.header.capability_free_head = slot.next_free;
    slot.* = .{
        .capability_id = spec.capability_id,
        .generation = generation,
        .table_slot_index = table_handle.slot_index,
        .action_code = spec.action_code,
        .next_free = layout.no_slot,
        .flags = layout.occupied_flag,
        .expected_effects = spec.expected_effects,
        .destructive = spec.destructive,
        .reserved0 = .{ 0, 0, 0, 0, 0, 0 },
        .reserved1 = 0,
    };
    const binding = bindingFromHandle(capabilityHandle(view, slot_index));
    if (roles.mode) table.mode_capability = binding;
    if (roles.capacity) table.capacity_capability = binding;
    table.revision = next_table_revision;
    view.header.capability_count += 1;
    advanceSnapshotAssumeAvailable(view.header);
    return capabilityHandle(view, slot_index);
}

pub fn replaceCapability(
    view: layout.View,
    handle: protocol.CapabilityHandle,
    spec: protocol.CapabilitySpec,
) !protocol.CapabilityHandle {
    try operation_sequence.enterMutationImplicit(view);
    if (!protocol.validCapabilitySpec(spec) or spec.capability_id != handle.capability_id) {
        return error.InvalidArgument;
    }
    const slot = try resolveCapability(view, handle);
    if (capabilityHasPendingIntent(view, handle.slot_index, handle.slot_generation)) {
        return error.ResourceBusy;
    }
    const table = &view.tables[slot.table_slot_index];
    const old_mode = sameBinding(table.mode_capability, handle);
    const old_capacity = sameBinding(table.capacity_capability, handle);
    const roles = capabilityRoles(spec.expected_effects);
    if ((!old_mode and roles.mode and !protocol.isEmptyBinding(table.mode_capability)) or
        (!old_capacity and roles.capacity and !protocol.isEmptyBinding(table.capacity_capability)))
    {
        return error.IntentConflict;
    }
    if (table.revision == std.math.maxInt(u64)) return error.GenerationExhausted;
    try ensureSnapshotAdvance(view.header);
    const generation = try takeGeneration(&view.header.next_capability_generation);
    slot.generation = generation;
    slot.action_code = spec.action_code;
    slot.expected_effects = spec.expected_effects;
    slot.destructive = spec.destructive;
    const replacement = bindingFromHandle(capabilityHandle(view, handle.slot_index));
    if (old_mode) table.mode_capability = std.mem.zeroes(protocol.CapabilityBinding);
    if (old_capacity) table.capacity_capability = std.mem.zeroes(protocol.CapabilityBinding);
    if (roles.mode) table.mode_capability = replacement;
    if (roles.capacity) table.capacity_capability = replacement;
    table.revision += 1;
    advanceSnapshotAssumeAvailable(view.header);
    return capabilityHandle(view, handle.slot_index);
}

pub fn unregisterCapability(view: layout.View, handle: protocol.CapabilityHandle) !void {
    try operation_sequence.enterMutationImplicit(view);
    const capability = try resolveCapability(view, handle);
    if (capabilityHasPendingIntent(view, handle.slot_index, handle.slot_generation)) {
        return error.ResourceBusy;
    }
    const table = &view.tables[capability.table_slot_index];
    const mode_match = sameBinding(table.mode_capability, handle);
    const capacity_match = sameBinding(table.capacity_capability, handle);
    if ((mode_match or capacity_match) and table.occupied_count != 0) return error.ResourceBusy;
    if (table.revision == std.math.maxInt(u64)) return error.GenerationExhausted;
    try ensureSnapshotAdvance(view.header);
    if (mode_match) table.mode_capability = std.mem.zeroes(protocol.CapabilityBinding);
    if (capacity_match) table.capacity_capability = std.mem.zeroes(protocol.CapabilityBinding);
    table.revision += 1;
    freeCapabilitySlot(view, handle.slot_index);
    advanceSnapshotAssumeAvailable(view.header);
}

pub fn registerResource(
    view: layout.View,
    table_handle: protocol.TableHandle,
    spec: protocol.ResourceSpec,
) !protocol.ResourceHandle {
    if (!protocol.validResourceSpec(spec)) return error.InvalidArgument;
    const table = try resolveTable(view, table_handle);
    if (table.occupied_count >= table.capacity) return error.TableFull;
    const cell_index = findFirstDirectEmptyCell(view, table) orelse return error.TableFull;
    return registerResourceAtLedgerCell(view, table_handle, cell_index, spec, null);
}

pub fn validatePartitionResourceAdmissionSpec(
    view: layout.View,
    table_handle: protocol.TableHandle,
    spec: protocol.ResourceSpec,
    replacement_resource_slot_index: ?u32,
) !void {
    if (!protocol.validResourceSpec(spec)) return error.InvalidArgument;
    const table = try resolveTable(view, table_handle);
    try validateTableBindings(view, table_handle.slot_index, table);
    if (findResourceIndex(view, table_handle.slot_index, table, spec.resource_uid)) |existing| {
        if (replacement_resource_slot_index == null or
            existing != replacement_resource_slot_index.?)
        {
            return error.DuplicateResource;
        }
    }
    if (replacement_resource_slot_index == null) {
        if (table.occupied_count >= table.capacity) return error.TableFull;
        if (view.header.resource_free_head == layout.no_slot) return error.ResourceFull;
        return;
    }
    const replacement_index = replacement_resource_slot_index.?;
    if (replacement_index >= view.resources.len) return error.InvalidState;
    const replacement = &view.resources[replacement_index];
    if (!isOccupied(replacement) or replacement.table_slot_index != table_handle.slot_index) {
        return error.InvalidState;
    }
}

pub fn registerPartitionResourceAtLedgerCell(
    view: layout.View,
    partition_handle: protocol.PartitionHandle,
    cell_index: u32,
    spec: protocol.ResourceSpec,
) !protocol.ResourceHandle {
    const table_handle = protocol.TableHandle{
        .manager_instance_id = partition_handle.manager_instance_id,
        .slot_generation = partition_handle.table_slot_generation,
        .table_id = partition_handle.table_id,
        .table_incarnation = partition_handle.table_incarnation,
        .slot_index = partition_handle.table_slot_index,
        .reserved0 = 0,
    };
    return registerResourceAtLedgerCell(
        view,
        table_handle,
        cell_index,
        spec,
        partition_handle,
    );
}

fn registerResourceAtLedgerCell(
    view: layout.View,
    table_handle: protocol.TableHandle,
    cell_index: u32,
    spec: protocol.ResourceSpec,
    partition_authority: ?protocol.PartitionHandle,
) !protocol.ResourceHandle {
    if (!protocol.validResourceSpec(spec)) return error.InvalidArgument;
    const table = try resolveTable(view, table_handle);
    if (table.occupied_count >= table.capacity) return error.TableFull;
    if (cell_index >= view.ledger_cells.len) return error.InvalidArgument;
    const cell = &view.ledger_cells[cell_index];
    if (!isOccupied(cell) or cell.table_slot_index != table_handle.slot_index) {
        return error.InvalidArgument;
    }
    if (cell.resource_slot_index != layout.no_slot or cell.resource_slot_generation != 0) {
        return error.SlotOccupied;
    }
    if (partition_authority) |authority| {
        if (!isPartitionReservedCell(cell) or
            authority.manager_instance_id != view.header.manager_instance_id or
            authority.table_slot_generation != table.generation or
            !protocol.sameId(authority.table_id, table.table_id) or
            authority.table_incarnation != table.table_incarnation or
            authority.table_slot_index != table_handle.slot_index or
            authority.partition_slot_generation == 0 or
            authority.partition_slot_index >= view.partitions.len)
        {
            return error.StalePartition;
        }
        const owner = &view.partitions[authority.partition_slot_index];
        if (!isOccupied(owner) or owner.generation != authority.partition_slot_generation or
            owner.table_slot_index != table_handle.slot_index)
        {
            return error.StalePartition;
        }
        if (cell.owner_partition_slot_index != authority.partition_slot_index or
            cell.owner_partition_generation != authority.partition_slot_generation)
        {
            return error.SlotOccupied;
        }
    } else if (isPartitionReservedCell(cell) or
        cell.owner_partition_slot_index != layout.no_slot or
        cell.owner_partition_generation != 0)
    {
        return error.SlotOccupied;
    }
    try validateTableBindings(view, table_handle.slot_index, table);
    if (findResourceIndex(view, table_handle.slot_index, table, spec.resource_uid) != null) {
        return error.DuplicateResource;
    }
    try operation_sequence.enterMutationImplicit(view);
    try ensureSnapshotAdvance(view.header);
    const next_table_revision = try increment(table.revision);
    const slot_index = view.header.resource_free_head;
    if (slot_index == layout.no_slot) return error.ResourceFull;
    const generation = try takeGeneration(&view.header.next_resource_generation);
    const slot = &view.resources[slot_index];
    view.header.resource_free_head = slot.next_free;
    slot.* = .{
        .resource_uid = spec.resource_uid,
        .size_bytes = spec.size_bytes,
        .activity_value = 0,
        .last_activity_epoch = view.header.activity_epoch,
        .row_revision = 1,
        .activity_revision = 1,
        .generation = generation,
        .use_generation = 1,
        .active_lease_generation = 0,
        .pending_generation = 0,
        .recovery_cost_coefficient = spec.recovery_cost_coefficient,
        .table_slot_index = table_handle.slot_index,
        .ledger_cell_index = cell_index,
        .pending_slot_index = layout.no_slot,
        .previous_table_resource = layout.no_slot,
        .next_table_resource = layout.no_slot,
        .next_free = layout.no_slot,
        .flags = layout.occupied_flag,
        .recoverability = spec.recoverability,
        .access_loss_impact = spec.access_loss_impact,
        .protected_use = 0,
        .reserved0 = 0,
        .reserved1 = 0,
    };
    cell.resource_slot_index = slot_index;
    cell.resource_slot_generation = generation;
    insertUid(view, table, slot_index) catch |err| {
        cell.resource_slot_index = layout.no_slot;
        cell.resource_slot_generation = 0;
        slot.* = std.mem.zeroes(layout.ResourceSlot);
        slot.ledger_cell_index = layout.no_slot;
        slot.pending_slot_index = layout.no_slot;
        slot.previous_table_resource = layout.no_slot;
        slot.next_table_resource = layout.no_slot;
        slot.next_free = view.header.resource_free_head;
        view.header.resource_free_head = slot_index;
        return err;
    };
    linkResource(view, slot_index, table);
    table.occupied_count += 1;
    if (!isPartitionReservedCell(cell)) table.direct_occupied_count += 1;
    table.revision = next_table_revision;
    view.header.resource_count += 1;
    advanceSnapshotAssumeAvailable(view.header);
    return resourceHandle(view, slot_index);
}

pub fn updateResource(
    view: layout.View,
    handle: protocol.ResourceHandle,
    spec: protocol.ResourceSpec,
) !void {
    try operation_sequence.enterMutationImplicit(view);
    if (!protocol.validResourceSpec(spec) or !protocol.sameId(spec.resource_uid, handle.resource_uid)) {
        return error.InvalidArgument;
    }
    const resource = try resolveResource(view, handle);
    if (resource.protected_use != 0 or resource.pending_slot_index != layout.no_slot) {
        return error.ResourceBusy;
    }
    try validateTableBindings(view, resource.table_slot_index, &view.tables[resource.table_slot_index]);
    try ensureSnapshotAdvance(view.header);
    resource.size_bytes = spec.size_bytes;
    resource.recovery_cost_coefficient = spec.recovery_cost_coefficient;
    resource.recoverability = spec.recoverability;
    resource.access_loss_impact = spec.access_loss_impact;
    resource.row_revision = try increment(resource.row_revision);
    advanceSnapshotAssumeAvailable(view.header);
}

pub fn unregisterResource(view: layout.View, handle: protocol.ResourceHandle) !void {
    try operation_sequence.enterMutationImplicit(view);
    const resource = try resolveResource(view, handle);
    if (resource.protected_use != 0 or resource.pending_slot_index != layout.no_slot) {
        return error.ResourceBusy;
    }
    const table = &view.tables[resource.table_slot_index];
    const direct = !isPartitionReservedCell(&view.ledger_cells[resource.ledger_cell_index]);
    try ensureSnapshotAdvance(view.header);
    const next_table_revision = try increment(table.revision);
    removeUid(view, resource.table_slot_index, table, resource.resource_uid);
    clearResourceCell(view, handle.resource_slot_index, resource);
    unlinkResource(view, handle.resource_slot_index, table);
    freeResourceSlot(view, handle.resource_slot_index);
    table.occupied_count -= 1;
    if (direct) table.direct_occupied_count -= 1;
    table.revision = next_table_revision;
    maybeRebuildUidIndex(view);
    advanceSnapshotAssumeAvailable(view.header);
}

pub fn touchResource(view: layout.View, handle: protocol.ResourceHandle, weight: u32) !void {
    try operation_sequence.enterMutationImplicit(view);
    const resource = try resolveResource(view, handle);
    if (resource.pending_slot_index != layout.no_slot) {
        return error.ResourceBusy;
    }
    try ensureSnapshotAdvance(view.header);
    try activity.touch(resource, view.header, weight);
    advanceSnapshotAssumeAvailable(view.header);
}

pub fn advanceActivityEpoch(view: layout.View, epoch_count: u64) !void {
    try operation_sequence.enterMutationImplicit(view);
    if (epoch_count == 0) return error.InvalidArgument;
    try ensureSnapshotAdvance(view.header);
    view.header.activity_epoch = std.math.add(u64, view.header.activity_epoch, epoch_count) catch
        return error.GenerationExhausted;
    advanceSnapshotAssumeAvailable(view.header);
}

pub fn beginUse(view: layout.View, handle: protocol.ResourceHandle) !protocol.UseLease {
    try operation_sequence.enterMutationImplicit(view);
    const resource = try resolveResource(view, handle);
    if (resource.protected_use != 0 or resource.pending_slot_index != layout.no_slot) {
        return error.ResourceBusy;
    }
    try ensureSnapshotAdvance(view.header);
    resource.use_generation = try increment(resource.use_generation);
    resource.active_lease_generation = resource.use_generation;
    resource.protected_use = 1;
    try activity.touch(resource, view.header, 1);
    advanceSnapshotAssumeAvailable(view.header);
    return .{ .resource = handle, .lease_generation = resource.active_lease_generation };
}

pub fn endUse(view: layout.View, lease: protocol.UseLease) !void {
    try operation_sequence.enterMutationImplicit(view);
    const resource = try resolveResource(view, lease.resource);
    if (resource.protected_use == 0 or resource.active_lease_generation != lease.lease_generation) {
        return error.StaleResource;
    }
    try ensureSnapshotAdvance(view.header);
    resource.use_generation = try increment(resource.use_generation);
    resource.active_lease_generation = 0;
    resource.protected_use = 0;
    advanceSnapshotAssumeAvailable(view.header);
}

pub fn readTableCapacity(view: layout.View, handle: protocol.TableHandle) !protocol.TableCapacityView {
    const table = try resolveTable(view, handle);
    const direct_capacity = table.capacity - table.partition_reservation_capacity;
    const partition_occupied_count = table.occupied_count - table.direct_occupied_count;
    return .{
        .table = handle,
        .capacity = table.capacity,
        .occupied_count = table.occupied_count,
        .free_count = table.capacity - table.occupied_count,
        .active_pending_count = table.active_pending_count,
        .direct_capacity = direct_capacity,
        .direct_occupied_count = table.direct_occupied_count,
        .direct_free_count = direct_capacity - table.direct_occupied_count,
        .partition_reservation_start = table.partition_reservation_start,
        .partition_reservation_capacity = table.partition_reservation_capacity,
        .partition_occupied_count = partition_occupied_count,
        .partition_free_count = table.partition_reservation_capacity - partition_occupied_count,
        .reserved0 = 0,
        .revision = table.revision,
    };
}

pub fn readResource(view: layout.View, handle: protocol.ResourceHandle) !protocol.ResourceView {
    const resource = try resolveResource(view, handle);
    return .{
        .resource = handle,
        .size_bytes = resource.size_bytes,
        .settled_activity = activity.settledValue(resource, view.header),
        .row_revision = resource.row_revision,
        .activity_revision = resource.activity_revision,
        .recovery_cost_coefficient = resource.recovery_cost_coefficient,
        .recoverability = resource.recoverability,
        .access_loss_impact = resource.access_loss_impact,
        .protected_use = resource.protected_use,
        .pending = if (resource.pending_slot_index == layout.no_slot) 0 else 1,
        .reserved0 = .{ 0, 0, 0, 0 },
    };
}

pub fn resolveTable(view: layout.View, handle: protocol.TableHandle) !*layout.TableSlot {
    if (handle.manager_instance_id != view.header.manager_instance_id or
        handle.slot_index >= view.tables.len or handle.slot_generation == 0 or handle.reserved0 != 0)
    {
        return error.StaleTable;
    }
    const table = &view.tables[handle.slot_index];
    if (!isOccupied(table) or table.generation != handle.slot_generation or
        table.table_incarnation != handle.table_incarnation or
        !protocol.sameId(table.table_id, handle.table_id))
    {
        return error.StaleTable;
    }
    return table;
}

pub fn resolveResource(view: layout.View, handle: protocol.ResourceHandle) !*layout.ResourceSlot {
    if (handle.manager_instance_id != view.header.manager_instance_id or
        handle.table_slot_index >= view.tables.len or
        handle.resource_slot_index >= view.resources.len or
        handle.table_slot_generation == 0 or handle.resource_slot_generation == 0)
    {
        return error.StaleResource;
    }
    const table = &view.tables[handle.table_slot_index];
    const resource = &view.resources[handle.resource_slot_index];
    if (!isOccupied(table) or !isOccupied(resource) or
        table.generation != handle.table_slot_generation or
        resource.generation != handle.resource_slot_generation or
        resource.table_slot_index != handle.table_slot_index or
        table.table_incarnation != handle.table_incarnation or
        !protocol.sameId(table.table_id, handle.table_id) or
        !protocol.sameId(resource.resource_uid, handle.resource_uid))
    {
        return error.StaleResource;
    }
    return resource;
}

pub fn resolveCapability(
    view: layout.View,
    handle: protocol.CapabilityHandle,
) !*layout.CapabilitySlot {
    if (handle.manager_instance_id != view.header.manager_instance_id or
        handle.table_slot_index >= view.tables.len or handle.slot_index >= view.capabilities.len or
        handle.slot_generation == 0)
    {
        return error.StaleCapability;
    }
    const table = &view.tables[handle.table_slot_index];
    const capability = &view.capabilities[handle.slot_index];
    if (!isOccupied(table) or !isOccupied(capability) or
        capability.generation != handle.slot_generation or
        capability.table_slot_index != handle.table_slot_index or
        capability.capability_id != handle.capability_id)
    {
        return error.StaleCapability;
    }
    return capability;
}

pub fn resolveBinding(
    view: layout.View,
    table_slot_index: u32,
    binding: protocol.CapabilityBinding,
) !*layout.CapabilitySlot {
    if (protocol.isEmptyBinding(binding) or binding.slot_index >= view.capabilities.len) {
        return error.StaleCapability;
    }
    const capability = &view.capabilities[binding.slot_index];
    if (!isOccupied(capability) or capability.table_slot_index != table_slot_index or
        capability.capability_id != binding.capability_id or capability.generation != binding.generation)
    {
        return error.StaleCapability;
    }
    return capability;
}

pub fn tableHandle(view: layout.View, slot_index: u32) protocol.TableHandle {
    const table = &view.tables[slot_index];
    return .{
        .manager_instance_id = view.header.manager_instance_id,
        .slot_generation = table.generation,
        .table_id = table.table_id,
        .table_incarnation = table.table_incarnation,
        .slot_index = slot_index,
        .reserved0 = 0,
    };
}

pub fn capabilityHandle(view: layout.View, slot_index: u32) protocol.CapabilityHandle {
    const capability = &view.capabilities[slot_index];
    return .{
        .manager_instance_id = view.header.manager_instance_id,
        .slot_generation = capability.generation,
        .capability_id = capability.capability_id,
        .table_slot_index = capability.table_slot_index,
        .slot_index = slot_index,
    };
}

pub fn bindingFromHandle(handle: protocol.CapabilityHandle) protocol.CapabilityBinding {
    return .{
        .capability_id = handle.capability_id,
        .generation = handle.slot_generation,
        .slot_index = handle.slot_index,
        .reserved0 = 0,
    };
}

pub fn resourceHandle(view: layout.View, slot_index: u32) protocol.ResourceHandle {
    const resource = &view.resources[slot_index];
    const table = &view.tables[resource.table_slot_index];
    return .{
        .manager_instance_id = view.header.manager_instance_id,
        .table_slot_generation = table.generation,
        .resource_slot_generation = resource.generation,
        .table_id = table.table_id,
        .table_incarnation = table.table_incarnation,
        .resource_uid = resource.resource_uid,
        .table_slot_index = resource.table_slot_index,
        .resource_slot_index = slot_index,
    };
}

pub fn freeResource(view: layout.View, resource_slot_index: u32) !void {
    const resource = &view.resources[resource_slot_index];
    if (!isOccupied(resource)) return error.StaleResource;
    const table = &view.tables[resource.table_slot_index];
    const next_table_revision = try increment(table.revision);
    const direct = !isPartitionReservedCell(&view.ledger_cells[resource.ledger_cell_index]);
    removeUid(view, resource.table_slot_index, table, resource.resource_uid);
    clearResourceCell(view, resource_slot_index, resource);
    unlinkResource(view, resource_slot_index, table);
    freeResourceSlot(view, resource_slot_index);
    table.occupied_count -= 1;
    if (direct) table.direct_occupied_count -= 1;
    table.revision = next_table_revision;
    maybeRebuildUidIndex(view);
}

pub fn tableCellAtOrdinal(
    view: layout.View,
    table: *const layout.TableSlot,
    ordinal: u32,
) !u32 {
    if (ordinal >= table.capacity) return error.InvalidArgument;
    var cell_index = table.first_ledger_cell;
    var current: u32 = 0;
    while (current < ordinal) : (current += 1) {
        if (cell_index == layout.no_slot or cell_index >= view.ledger_cells.len) {
            return error.InvalidState;
        }
        cell_index = view.ledger_cells[cell_index].next_table_cell;
    }
    if (cell_index == layout.no_slot or cell_index >= view.ledger_cells.len) {
        return error.InvalidState;
    }
    return cell_index;
}

fn findFirstDirectEmptyCell(view: layout.View, table: *const layout.TableSlot) ?u32 {
    var cell_index = table.first_ledger_cell;
    while (cell_index != layout.no_slot) {
        const cell = &view.ledger_cells[cell_index];
        if (!isPartitionReservedCell(cell) and
            cell.owner_partition_slot_index == layout.no_slot and
            cell.owner_partition_generation == 0 and
            cell.resource_slot_index == layout.no_slot and cell.resource_slot_generation == 0)
        {
            return cell_index;
        }
        cell_index = cell.next_table_cell;
    }
    return null;
}

pub fn isPartitionReservedCell(cell: *const layout.LedgerCell) bool {
    return cell.flags & layout.partition_reserved_flag != 0;
}

fn clearResourceCell(
    view: layout.View,
    resource_slot_index: u32,
    resource: *const layout.ResourceSlot,
) void {
    const cell = &view.ledger_cells[resource.ledger_cell_index];
    if (cell.resource_slot_index != resource_slot_index or
        cell.resource_slot_generation != resource.generation)
    {
        unreachable;
    }
    cell.resource_slot_index = layout.no_slot;
    cell.resource_slot_generation = 0;
}

fn linkResource(view: layout.View, resource_slot_index: u32, table: *layout.TableSlot) void {
    const resource = &view.resources[resource_slot_index];
    resource.previous_table_resource = layout.no_slot;
    resource.next_table_resource = table.first_resource_slot;
    if (table.first_resource_slot != layout.no_slot) {
        view.resources[table.first_resource_slot].previous_table_resource = resource_slot_index;
    }
    table.first_resource_slot = resource_slot_index;
}

fn unlinkResource(view: layout.View, resource_slot_index: u32, table: *layout.TableSlot) void {
    const resource = &view.resources[resource_slot_index];
    if (resource.previous_table_resource == layout.no_slot) {
        table.first_resource_slot = resource.next_table_resource;
    } else {
        view.resources[resource.previous_table_resource].next_table_resource = resource.next_table_resource;
    }
    if (resource.next_table_resource != layout.no_slot) {
        view.resources[resource.next_table_resource].previous_table_resource =
            resource.previous_table_resource;
    }
}

pub fn advanceSnapshot(view: layout.View) !void {
    try ensureSnapshotAdvance(view.header);
    advanceSnapshotAssumeAvailable(view.header);
}

pub fn isOccupied(slot: anytype) bool {
    return slot.flags & layout.occupied_flag != 0;
}

fn validateTableBindings(
    view: layout.View,
    table_slot_index: u32,
    table: *const layout.TableSlot,
) !void {
    var valid_count: u8 = 0;
    if (!protocol.isEmptyBinding(table.mode_capability)) {
        _ = try resolveBinding(view, table_slot_index, table.mode_capability);
        valid_count += 1;
    }
    if (!protocol.isEmptyBinding(table.capacity_capability)) {
        const capability = try resolveBinding(view, table_slot_index, table.capacity_capability);
        if (capability.expected_effects & protocol.effect_releases_ledger_slot == 0) {
            return error.InvalidArgument;
        }
        valid_count += 1;
    }
    if (valid_count == 0) return error.InvalidArgument;
}

fn findResourceIndex(
    view: layout.View,
    table_slot_index: u32,
    table: *const layout.TableSlot,
    uid: protocol.Identifier128,
) ?u32 {
    const hash = identity.hashResource(table.generation, table.table_id, table.table_incarnation, uid);
    const mask: usize = view.uid_buckets.len - 1;
    var probe: usize = 0;
    while (probe < view.uid_buckets.len) : (probe += 1) {
        const bucket = &view.uid_buckets[(@as(usize, @intCast(hash)) + probe) & mask];
        if (bucket.state == 0) return null;
        if (bucket.state != layout.bucket_occupied or bucket.hash != hash or
            bucket.resource_slot_index >= view.resources.len)
        {
            continue;
        }
        const resource = &view.resources[bucket.resource_slot_index];
        if (isOccupied(resource) and resource.generation == bucket.resource_generation and
            resource.table_slot_index == table_slot_index and
            protocol.sameId(resource.resource_uid, uid))
        {
            return bucket.resource_slot_index;
        }
    }
    return null;
}

fn insertUid(view: layout.View, table: *const layout.TableSlot, resource_slot_index: u32) !void {
    const resource = &view.resources[resource_slot_index];
    const hash = identity.hashResource(
        table.generation,
        table.table_id,
        table.table_incarnation,
        resource.resource_uid,
    );
    const mask: usize = view.uid_buckets.len - 1;
    var first_tombstone: ?usize = null;
    var probe: usize = 0;
    while (probe < view.uid_buckets.len) : (probe += 1) {
        const index = (@as(usize, @intCast(hash)) + probe) & mask;
        const bucket = &view.uid_buckets[index];
        if (bucket.state == layout.bucket_tombstone and first_tombstone == null) {
            first_tombstone = index;
            continue;
        }
        if (bucket.state != 0) continue;
        writeUidBucket(view, first_tombstone orelse index, hash, resource_slot_index);
        return;
    }
    if (first_tombstone) |index| {
        writeUidBucket(view, index, hash, resource_slot_index);
        return;
    }
    return error.ResourceFull;
}

fn writeUidBucket(view: layout.View, index: usize, hash: u64, resource_slot_index: u32) void {
    const bucket = &view.uid_buckets[index];
    if (bucket.state == layout.bucket_tombstone) view.header.uid_tombstone_count -= 1;
    bucket.* = .{
        .hash = hash,
        .resource_generation = view.resources[resource_slot_index].generation,
        .resource_slot_index = resource_slot_index,
        .state = layout.bucket_occupied,
        .reserved0 = .{ 0, 0, 0 },
    };
}

fn removeUid(
    view: layout.View,
    table_slot_index: u32,
    table: *const layout.TableSlot,
    uid: protocol.Identifier128,
) void {
    const hash = identity.hashResource(table.generation, table.table_id, table.table_incarnation, uid);
    const mask: usize = view.uid_buckets.len - 1;
    var probe: usize = 0;
    while (probe < view.uid_buckets.len) : (probe += 1) {
        const bucket = &view.uid_buckets[(@as(usize, @intCast(hash)) + probe) & mask];
        if (bucket.state == 0) return;
        if (bucket.state != layout.bucket_occupied or bucket.hash != hash) continue;
        if (bucket.resource_slot_index >= view.resources.len) continue;
        const resource = &view.resources[bucket.resource_slot_index];
        if (isOccupied(resource) and resource.generation == bucket.resource_generation and
            resource.table_slot_index == table_slot_index and
            protocol.sameId(resource.resource_uid, uid))
        {
            bucket.* = std.mem.zeroes(layout.UidBucket);
            bucket.state = layout.bucket_tombstone;
            view.header.uid_tombstone_count += 1;
            return;
        }
    }
}

fn capabilityHasPendingIntent(
    view: layout.View,
    capability_slot_index: u32,
    capability_generation: u64,
) bool {
    for (view.pending) |*pending| {
        if (!isOccupied(pending)) continue;
        if (pending.intent.capability_slot_index == capability_slot_index and
            pending.intent.capability_generation == capability_generation)
        {
            return true;
        }
    }
    return false;
}

fn sameBinding(
    binding: protocol.CapabilityBinding,
    handle: protocol.CapabilityHandle,
) bool {
    return binding.capability_id == handle.capability_id and
        binding.generation == handle.slot_generation and binding.slot_index == handle.slot_index;
}

const CapabilityRoles = struct {
    mode: bool,
    capacity: bool,
};

fn capabilityRoles(effects: u8) CapabilityRoles {
    return .{
        .mode = effects & ~protocol.effect_releases_ledger_slot != 0,
        .capacity = effects & protocol.effect_releases_ledger_slot != 0,
    };
}

fn maybeRebuildUidIndex(view: layout.View) void {
    if (@as(u64, view.header.uid_tombstone_count) * 4 < view.uid_buckets.len) return;
    @memset(view.uid_buckets, std.mem.zeroes(layout.UidBucket));
    view.header.uid_tombstone_count = 0;
    var index: usize = 0;
    while (index < view.resources.len) : (index += 1) {
        const resource = &view.resources[index];
        if (!isOccupied(resource)) continue;
        const table = &view.tables[resource.table_slot_index];
        insertUid(view, table, @intCast(index)) catch unreachable;
    }
}

fn freeTableSlot(view: layout.View, slot_index: u32) void {
    const slot = &view.tables[slot_index];
    const generation = slot.generation;
    slot.* = std.mem.zeroes(layout.TableSlot);
    slot.generation = generation;
    slot.next_free = view.header.table_free_head;
    view.header.table_free_head = slot_index;
    view.header.table_count -= 1;
}

fn allocateTableCells(
    view: layout.View,
    table_slot_index: u32,
    table: *layout.TableSlot,
    count: u32,
    partition_reservation_start: u32,
    partition_reservation_capacity: u32,
) void {
    var first = layout.no_slot;
    var previous = layout.no_slot;
    var allocated: u32 = 0;
    var index: u32 = 0;
    while (index < view.ledger_cells.len and allocated < count) : (index += 1) {
        const cell = &view.ledger_cells[index];
        if (isOccupied(cell)) continue;
        const reserved = allocated >= partition_reservation_start and
            allocated - partition_reservation_start < partition_reservation_capacity;
        cell.* = .{
            .resource_slot_generation = 0,
            .owner_partition_generation = 0,
            .resource_slot_index = layout.no_slot,
            .owner_partition_slot_index = layout.no_slot,
            .table_slot_index = table_slot_index,
            .next_table_cell = layout.no_slot,
            .flags = layout.occupied_flag |
                (if (reserved) layout.partition_reserved_flag else 0),
            .table_local_ordinal = allocated,
        };
        if (first == layout.no_slot) first = index;
        if (previous != layout.no_slot) view.ledger_cells[previous].next_table_cell = index;
        previous = index;
        allocated += 1;
    }
    if (allocated != count) unreachable;
    table.first_ledger_cell = first;
    view.header.ledger_cell_count += count;
}

fn freeTableCells(view: layout.View, table: *layout.TableSlot) void {
    var cell_index = table.first_ledger_cell;
    var freed: u32 = 0;
    while (cell_index != layout.no_slot) {
        const cell = &view.ledger_cells[cell_index];
        const next = cell.next_table_cell;
        if (cell.resource_slot_index != layout.no_slot or
            cell.owner_partition_slot_index != layout.no_slot)
        {
            unreachable;
        }
        cell.* = std.mem.zeroes(layout.LedgerCell);
        cell.resource_slot_index = layout.no_slot;
        cell.owner_partition_slot_index = layout.no_slot;
        cell.next_table_cell = layout.no_slot;
        freed += 1;
        cell_index = next;
    }
    if (freed != table.capacity) unreachable;
    view.header.ledger_cell_count -= freed;
    table.first_ledger_cell = layout.no_slot;
}

fn freeResourceSlot(view: layout.View, slot_index: u32) void {
    const slot = &view.resources[slot_index];
    const generation = slot.generation;
    slot.* = std.mem.zeroes(layout.ResourceSlot);
    slot.generation = generation;
    slot.next_free = view.header.resource_free_head;
    slot.ledger_cell_index = layout.no_slot;
    slot.pending_slot_index = layout.no_slot;
    slot.previous_table_resource = layout.no_slot;
    slot.next_table_resource = layout.no_slot;
    view.header.resource_free_head = slot_index;
    view.header.resource_count -= 1;
}

fn freeCapabilitySlot(view: layout.View, slot_index: u32) void {
    const slot = &view.capabilities[slot_index];
    const generation = slot.generation;
    slot.* = std.mem.zeroes(layout.CapabilitySlot);
    slot.generation = generation;
    slot.next_free = view.header.capability_free_head;
    view.header.capability_free_head = slot_index;
    view.header.capability_count -= 1;
}

fn initializeFreeList(comptime T: type, slots: []T) void {
    for (slots, 0..) |*slot, index| {
        slot.* = std.mem.zeroes(T);
        slot.next_free = if (index + 1 < slots.len) @intCast(index + 1) else layout.no_slot;
        if (T == layout.ResourceSlot) {
            slot.ledger_cell_index = layout.no_slot;
            slot.pending_slot_index = layout.no_slot;
            slot.previous_table_resource = layout.no_slot;
            slot.next_table_resource = layout.no_slot;
        } else if (T == layout.PartitionSlot) {
            slot.parent_partition_slot_index = layout.no_slot;
            slot.first_cell_index = layout.no_slot;
            slot.first_child_partition = layout.no_slot;
            slot.previous_sibling_partition = layout.no_slot;
            slot.next_sibling_partition = layout.no_slot;
        }
    }
}

fn validateCounts(view: layout.View) !void {
    var table_count: u32 = 0;
    var reserved_resource_capacity: u64 = 0;
    var resource_count: u32 = 0;
    var ledger_cell_count: u32 = 0;
    var partition_count: u32 = 0;
    var capability_count: u32 = 0;
    var pending_count: u32 = 0;
    for (view.tables, 0..) |*slot, table_index| {
        if (slot.flags & ~layout.occupied_flag != 0 or
            !allZero(slot.reserved0[0..]) or slot.has_paced_epoch > 1)
        {
            return error.InvalidState;
        }
        if (!isOccupied(slot)) continue;
        if (slot.has_paced_epoch == 0 and
            (slot.last_paced_epoch != 0 or slot.paced_snapshot_generation != 0))
        {
            return error.InvalidState;
        }
        if (slot.generation == 0 or slot.revision == 0 or slot.partition_revision == 0 or
            slot.table_incarnation == 0 or
            protocol.isZeroId(slot.table_id) or slot.capacity == 0 or
            slot.capacity > view.header.config.resource_capacity or
            slot.occupied_count > slot.capacity or
            slot.partition_reservation_capacity > slot.capacity or
            slot.partition_reservation_start > slot.capacity - slot.partition_reservation_capacity or
            (slot.partition_reservation_capacity == 0 and
                slot.partition_reservation_start != 0) or
            slot.direct_occupied_count > slot.capacity - slot.partition_reservation_capacity or
            slot.direct_occupied_count > slot.occupied_count or
            slot.active_pending_count > view.header.config.pending_capacity or
            @intFromEnum(slot.domain) > @intFromEnum(protocol.DomainKind.gpu))
        {
            return error.InvalidState;
        }
        if ((slot.active_partition_admission_id == 0) !=
            (slot.active_partition_admission_hash == 0) or
            (slot.active_partition_admission_id == 0) !=
                (slot.active_partition_operation_id == 0) or
            (slot.active_partition_admission_id == 0) !=
                (slot.active_partition_operation_generation == 0) or
            slot.active_partition_admission_id >= view.header.next_partition_admission_id)
        {
            return error.InvalidState;
        }
        if (slot.active_partition_admission_id != 0 and
            (view.header.active_operation_phase == .inactive or
                slot.active_partition_operation_id != view.header.active_operation_id or
                slot.active_partition_operation_generation !=
                    view.header.active_operation_generation))
        {
            return error.InvalidState;
        }
        var table_cell_count: u32 = 0;
        var table_reserved_cell_count: u32 = 0;
        var table_direct_resource_count: u32 = 0;
        var table_cell_index = slot.first_ledger_cell;
        while (table_cell_index != layout.no_slot) {
            if (table_cell_index >= view.ledger_cells.len or table_cell_count >= slot.capacity) {
                return error.InvalidState;
            }
            const cell = &view.ledger_cells[table_cell_index];
            if (!isOccupied(cell) or cell.table_slot_index != table_index or
                cell.flags & ~layout.ledger_cell_known_flags != 0)
            {
                return error.InvalidState;
            }
            if (cell.table_local_ordinal != table_cell_count) return error.InvalidState;
            const expected_reserved = table_cell_count >= slot.partition_reservation_start and
                table_cell_count - slot.partition_reservation_start <
                    slot.partition_reservation_capacity;
            if (isPartitionReservedCell(cell) != expected_reserved) return error.InvalidState;
            if (expected_reserved) {
                table_reserved_cell_count += 1;
            } else if (cell.resource_slot_index != layout.no_slot) {
                table_direct_resource_count += 1;
            }
            table_cell_count += 1;
            table_cell_index = cell.next_table_cell;
        }
        if (table_cell_count != slot.capacity or
            table_reserved_cell_count != slot.partition_reservation_capacity or
            table_direct_resource_count != slot.direct_occupied_count)
        {
            return error.InvalidState;
        }
        ledger_cell_count += table_cell_count;
        if ((!protocol.isEmptyBinding(slot.mode_capability) and
            (!bindingIsCurrent(view, @intCast(table_index), slot.mode_capability) or
                view.capabilities[slot.mode_capability.slot_index].expected_effects &
                    ~protocol.effect_releases_ledger_slot == 0)) or
            (!protocol.isEmptyBinding(slot.capacity_capability) and
                (!bindingIsCurrent(view, @intCast(table_index), slot.capacity_capability) or
                    view.capabilities[slot.capacity_capability.slot_index].expected_effects &
                        protocol.effect_releases_ledger_slot == 0)))
        {
            return error.InvalidState;
        }
        if ((slot.domain == .memory and
            (!protocol.isZeroId(slot.adapter_key) or slot.topology_generation != 0)) or
            (slot.domain == .gpu and
                (protocol.isZeroId(slot.adapter_key) or slot.topology_generation == 0)))
        {
            return error.InvalidState;
        }
        var listed_count: u32 = 0;
        var listed_index = slot.first_resource_slot;
        var previous_index = layout.no_slot;
        while (listed_index != layout.no_slot) {
            if (listed_index >= view.resources.len or listed_count >= slot.capacity) {
                return error.InvalidState;
            }
            const resource = &view.resources[listed_index];
            if (!isOccupied(resource) or resource.table_slot_index != table_index or
                resource.previous_table_resource != previous_index)
            {
                return error.InvalidState;
            }
            previous_index = listed_index;
            listed_index = resource.next_table_resource;
            listed_count += 1;
        }
        if (listed_count != slot.occupied_count) return error.InvalidState;
        reserved_resource_capacity = std.math.add(
            u64,
            reserved_resource_capacity,
            slot.capacity,
        ) catch return error.InvalidState;
        table_count += 1;
    }
    if (reserved_resource_capacity > view.header.config.resource_capacity) {
        return error.InvalidState;
    }
    for (view.resources, 0..) |*slot, resource_index| {
        if (slot.flags & ~layout.occupied_flag != 0 or slot.reserved0 != 0 or slot.reserved1 != 0 or
            slot.protected_use > 1)
        {
            return error.InvalidState;
        }
        if (!isOccupied(slot)) continue;
        if (slot.generation == 0 or slot.row_revision == 0 or slot.activity_revision == 0 or
            slot.use_generation == 0 or protocol.isZeroId(slot.resource_uid) or
            slot.table_slot_index >= view.tables.len or
            slot.ledger_cell_index >= view.ledger_cells.len or
            !isOccupied(&view.tables[slot.table_slot_index]) or
            @intFromEnum(slot.recoverability) > @intFromEnum(protocol.Recoverability.recoverable) or
            @intFromEnum(slot.access_loss_impact) >
                @intFromEnum(protocol.AccessLossImpact.unobservable_now) or
            !validFreeHead(slot.pending_slot_index, view.header.config.pending_capacity) or
            (slot.protected_use == 0) != (slot.active_lease_generation == 0) or
            (slot.pending_slot_index == layout.no_slot) != (slot.pending_generation == 0) or
            (protocol.isEmptyBinding(view.tables[slot.table_slot_index].mode_capability) and
                protocol.isEmptyBinding(view.tables[slot.table_slot_index].capacity_capability)))
        {
            return error.InvalidState;
        }
        const table = &view.tables[slot.table_slot_index];
        const cell = &view.ledger_cells[slot.ledger_cell_index];
        if (!isOccupied(cell) or cell.table_slot_index != slot.table_slot_index or
            cell.resource_slot_index != resource_index or
            cell.resource_slot_generation != slot.generation)
        {
            return error.InvalidState;
        }
        if ((slot.previous_table_resource == layout.no_slot and
            table.first_resource_slot != resource_index) or
            (slot.previous_table_resource != layout.no_slot and
                (slot.previous_table_resource >= view.resources.len or
                    view.resources[slot.previous_table_resource].next_table_resource != resource_index)) or
            (slot.next_table_resource != layout.no_slot and
                (slot.next_table_resource >= view.resources.len or
                    view.resources[slot.next_table_resource].previous_table_resource != resource_index)))
        {
            return error.InvalidState;
        }
        if (slot.pending_slot_index != layout.no_slot) {
            const pending = &view.pending[slot.pending_slot_index];
            if (!isOccupied(pending) or pending.generation != slot.pending_generation or
                pending.intent.resource.resource_slot_index !=
                    @as(u32, @intCast((@intFromPtr(slot) - @intFromPtr(view.resources.ptr)) /
                        @sizeOf(layout.ResourceSlot))))
            {
                return error.InvalidState;
            }
        }
        resource_count += 1;
    }
    for (view.ledger_cells, 0..) |*cell, cell_index| {
        if (cell.flags & ~layout.ledger_cell_known_flags != 0) {
            return error.InvalidState;
        }
        if (!isOccupied(cell)) continue;
        if (cell.table_slot_index >= view.tables.len or
            !isOccupied(&view.tables[cell.table_slot_index]) or
            cell.table_local_ordinal >= view.tables[cell.table_slot_index].capacity or
            (cell.resource_slot_index == layout.no_slot) != (cell.resource_slot_generation == 0) or
            (cell.owner_partition_slot_index == layout.no_slot) !=
                (cell.owner_partition_generation == 0))
        {
            return error.InvalidState;
        }
        if (cell.resource_slot_index != layout.no_slot) {
            if (cell.resource_slot_index >= view.resources.len) return error.InvalidState;
            const resource = &view.resources[cell.resource_slot_index];
            if (!isOccupied(resource) or resource.generation != cell.resource_slot_generation or
                resource.ledger_cell_index != cell_index or
                resource.table_slot_index != cell.table_slot_index)
            {
                return error.InvalidState;
            }
        }
        if (cell.owner_partition_slot_index != layout.no_slot) {
            if (cell.owner_partition_slot_index >= view.partitions.len) return error.InvalidState;
            const owner = &view.partitions[cell.owner_partition_slot_index];
            if (!isOccupied(owner) or owner.generation != cell.owner_partition_generation or
                owner.table_slot_index != cell.table_slot_index)
            {
                return error.InvalidState;
            }
        }
    }
    for (view.partitions) |*slot| {
        if (slot.flags & ~layout.occupied_flag != 0) {
            return error.InvalidState;
        }
        if (!isOccupied(slot)) continue;
        if (slot.generation == 0 or slot.structure_revision == 0 or slot.capacity == 0 or
            slot.table_slot_index >= view.tables.len or
            !isOccupied(&view.tables[slot.table_slot_index]) or
            slot.first_cell_index >= view.ledger_cells.len or
            slot.previous_sibling_partition != layout.no_slot and
                slot.previous_sibling_partition >= view.partitions.len or
            slot.next_sibling_partition != layout.no_slot and
                slot.next_sibling_partition >= view.partitions.len or
            slot.first_child_partition != layout.no_slot and
                slot.first_child_partition >= view.partitions.len)
        {
            return error.InvalidState;
        }
        const parent_capacity = if (slot.parent_partition_slot_index == layout.no_slot) blk: {
            if (slot.parent_partition_generation != 0) return error.InvalidState;
            const table = &view.tables[slot.table_slot_index];
            const reservation_end = std.math.add(
                u32,
                table.partition_reservation_start,
                table.partition_reservation_capacity,
            ) catch return error.InvalidState;
            if (table.partition_reservation_capacity == 0 or
                slot.local_start < table.partition_reservation_start or
                slot.local_start > reservation_end or
                slot.capacity > reservation_end - slot.local_start)
            {
                return error.InvalidState;
            }
            break :blk view.tables[slot.table_slot_index].capacity;
        } else blk: {
            if (slot.parent_partition_slot_index >= view.partitions.len) return error.InvalidState;
            const parent = &view.partitions[slot.parent_partition_slot_index];
            if (!isOccupied(parent) or parent.generation != slot.parent_partition_generation or
                parent.table_slot_index != slot.table_slot_index)
            {
                return error.InvalidState;
            }
            break :blk parent.capacity;
        };
        const parent_first_ordinal: u32 = if (slot.parent_partition_slot_index == layout.no_slot)
            0
        else
            view.partitions[slot.parent_partition_slot_index].first_table_ordinal;
        const expected_first_ordinal = std.math.add(
            u32,
            parent_first_ordinal,
            slot.local_start,
        ) catch return error.InvalidState;
        if (slot.local_start > parent_capacity or slot.capacity > parent_capacity - slot.local_start or
            slot.first_table_ordinal != expected_first_ordinal)
        {
            return error.InvalidState;
        }
        const first_cell = &view.ledger_cells[slot.first_cell_index];
        if (!isOccupied(first_cell) or first_cell.table_slot_index != slot.table_slot_index or
            first_cell.table_local_ordinal != expected_first_ordinal)
        {
            return error.InvalidState;
        }
        partition_count += 1;
    }
    var listed_partition_count: u32 = 0;
    for (view.tables, 0..) |*table, table_index| {
        if (!isOccupied(table)) continue;
        listed_partition_count += try validatePartitionForestForTable(
            view,
            @intCast(table_index),
            table,
        );
    }
    if (listed_partition_count != partition_count) return error.InvalidState;
    try validatePartitionFreeList(view, partition_count);
    for (view.capabilities, 0..) |*slot, capability_index| {
        if (slot.flags & ~layout.occupied_flag != 0 or !allZero(slot.reserved0[0..]) or
            slot.reserved1 != 0 or slot.destructive > 1)
        {
            return error.InvalidState;
        }
        if (!isOccupied(slot)) continue;
        if (slot.generation == 0 or slot.capability_id == 0 or slot.action_code == 0 or
            slot.table_slot_index >= view.tables.len or
            !isOccupied(&view.tables[slot.table_slot_index]) or slot.expected_effects == 0 or
            slot.expected_effects & ~protocol.supported_effect_mask != 0 or
            (slot.expected_effects & protocol.effect_releases_ledger_slot != 0 and
                slot.destructive == 0))
        {
            return error.InvalidState;
        }
        const table = &view.tables[slot.table_slot_index];
        const bound_as_mode = bindingMatchesSlot(table.mode_capability, slot, @intCast(capability_index));
        const bound_as_capacity = bindingMatchesSlot(
            table.capacity_capability,
            slot,
            @intCast(capability_index),
        );
        const roles = capabilityRoles(slot.expected_effects);
        if (bound_as_mode != roles.mode or bound_as_capacity != roles.capacity) {
            return error.InvalidState;
        }
        capability_count += 1;
    }
    var started_pending_count: u32 = 0;
    var uncertain_pending_count: u32 = 0;
    for (view.pending) |*slot| {
        if (slot.flags & ~layout.occupied_flag != 0 or !allZero(slot.reserved0[0..])) {
            return error.InvalidState;
        }
        if (!isOccupied(slot)) continue;
        if (slot.generation == 0 or slot.attempt_id == 0 or
            (slot.state != .reserved and slot.state != .effect_started and
                slot.state != .effect_uncertain) or
            slot.intent.resource.resource_slot_index >= view.resources.len)
        {
            return error.InvalidState;
        }
        const resource = &view.resources[slot.intent.resource.resource_slot_index];
        if (!isOccupied(resource) or
            resource.pending_slot_index !=
                @as(u32, @intCast((@intFromPtr(slot) - @intFromPtr(view.pending.ptr)) /
                    @sizeOf(layout.PendingSlot))) or
            resource.pending_generation != slot.generation)
        {
            return error.InvalidState;
        }
        if (!operation_sequence.intentBelongsToCurrent(view, slot.intent)) {
            return error.InvalidState;
        }
        if (slot.state == .effect_started) started_pending_count += 1;
        if (slot.state == .effect_uncertain) uncertain_pending_count += 1;
        pending_count += 1;
    }
    if (table_count != view.header.table_count or resource_count != view.header.resource_count or
        ledger_cell_count != view.header.ledger_cell_count or
        partition_count != view.header.partition_count or
        capability_count != view.header.capability_count or pending_count != view.header.pending_count)
    {
        return error.InvalidState;
    }
    if (uncertain_pending_count != 0 and
        view.header.active_operation_phase != .recovery_required)
    {
        return error.InvalidState;
    }
    if (view.header.active_operation_phase == .recovery_required and
        started_pending_count + uncertain_pending_count == 0)
    {
        return error.InvalidState;
    }
    for (view.tables, 0..) |*table, table_index| {
        if (!isOccupied(table)) continue;
        var resources_for_table: u32 = 0;
        var pending_for_table: u32 = 0;
        for (view.resources) |*resource| {
            if (isOccupied(resource) and resource.table_slot_index == table_index) {
                resources_for_table += 1;
            }
        }
        for (view.pending) |*pending| {
            if (isOccupied(pending) and pending.intent.resource.table_slot_index == table_index) {
                pending_for_table += 1;
            }
        }
        if (resources_for_table != table.occupied_count or
            pending_for_table != table.active_pending_count)
        {
            return error.InvalidState;
        }
    }
    var tombstone_count: u32 = 0;
    for (view.uid_buckets) |*bucket| {
        if (bucket.state > layout.bucket_tombstone or !allZero(bucket.reserved0[0..])) {
            return error.InvalidState;
        }
        if (bucket.state == layout.bucket_tombstone) {
            tombstone_count += 1;
            continue;
        }
        if (bucket.state != layout.bucket_occupied) continue;
        if (bucket.hash == 0 or bucket.resource_generation == 0 or
            bucket.resource_slot_index >= view.resources.len)
        {
            return error.InvalidState;
        }
        const resource = &view.resources[bucket.resource_slot_index];
        if (!isOccupied(resource) or resource.generation != bucket.resource_generation) {
            return error.InvalidState;
        }
    }
    if (tombstone_count != view.header.uid_tombstone_count) return error.InvalidState;
}

fn validOperationHeader(header: *const layout.Header) bool {
    const phase_value = @intFromEnum(header.active_operation_phase);
    if (phase_value > @intFromEnum(protocol.OperationPhase.recovery_required)) return false;
    if (header.active_operation_phase == .inactive) {
        return header.active_operation_id == 0 and
            header.active_operation_generation == 0 and
            header.active_operation_start_snapshot_generation == 0 and
            header.active_operation_settled_execution_count == 0 and
            header.active_operation_confirmed_effect_count == 0 and
            header.pending_count == 0;
    }
    if (header.active_operation_id == 0 or header.active_operation_generation == 0 or
        header.active_operation_start_snapshot_generation == 0 or
        header.active_operation_id >= header.next_operation_id or
        header.active_operation_generation >= header.next_operation_generation or
        @intFromEnum(header.active_operation_kind) <
            @intFromEnum(protocol.OperationKind.maintenance) or
        @intFromEnum(header.active_operation_kind) >
            @intFromEnum(protocol.OperationKind.partition_resource_admission))
    {
        return false;
    }
    return header.active_operation_phase != .recovery_required or header.pending_count != 0;
}

fn validatePartitionForestForTable(
    view: layout.View,
    table_slot_index: u32,
    table: *const layout.TableSlot,
) !u32 {
    var cursor_ordinal: u32 = 0;
    var cell_index = table.first_ledger_cell;
    var parent_index = layout.no_slot;
    var candidate_index = table.first_partition_slot;
    var expected_previous = layout.no_slot;
    var entered: u32 = 0;
    var exited: u32 = 0;

    while (true) {
        if (candidate_index != layout.no_slot) {
            if (candidate_index >= view.partitions.len or entered >= view.partitions.len) {
                return error.InvalidState;
            }
            const candidate = &view.partitions[candidate_index];
            if (!isOccupied(candidate) or candidate.table_slot_index != table_slot_index or
                candidate.parent_partition_slot_index != parent_index or
                candidate.previous_sibling_partition != expected_previous)
            {
                return error.InvalidState;
            }
            const parent_generation: u64 = if (parent_index == layout.no_slot)
                0
            else
                view.partitions[parent_index].generation;
            const parent_first_ordinal: u32 = if (parent_index == layout.no_slot)
                0
            else
                view.partitions[parent_index].first_table_ordinal;
            const parent_capacity: u32 = if (parent_index == layout.no_slot)
                table.capacity
            else
                view.partitions[parent_index].capacity;
            if (candidate.parent_partition_generation != parent_generation or
                candidate.local_start > parent_capacity or
                candidate.capacity > parent_capacity - candidate.local_start)
            {
                return error.InvalidState;
            }
            const candidate_first = std.math.add(
                u32,
                parent_first_ordinal,
                candidate.local_start,
            ) catch return error.InvalidState;
            if (candidate.first_table_ordinal != candidate_first or
                candidate_first < cursor_ordinal)
            {
                return error.InvalidState;
            }
            try validateDirectOwnerRange(
                view,
                table_slot_index,
                parent_index,
                parent_generation,
                &cursor_ordinal,
                &cell_index,
                candidate_first,
            );
            if (cell_index != candidate.first_cell_index) return error.InvalidState;

            entered += 1;
            parent_index = candidate_index;
            candidate_index = candidate.first_child_partition;
            expected_previous = layout.no_slot;
            continue;
        }

        if (parent_index == layout.no_slot) {
            try validateDirectOwnerRange(
                view,
                table_slot_index,
                layout.no_slot,
                0,
                &cursor_ordinal,
                &cell_index,
                table.capacity,
            );
            if (cell_index != layout.no_slot or entered != exited) return error.InvalidState;
            return entered;
        }

        if (exited >= entered) return error.InvalidState;
        const completed_index = parent_index;
        const completed = &view.partitions[completed_index];
        const completed_end = std.math.add(
            u32,
            completed.first_table_ordinal,
            completed.capacity,
        ) catch return error.InvalidState;
        try validateDirectOwnerRange(
            view,
            table_slot_index,
            completed_index,
            completed.generation,
            &cursor_ordinal,
            &cell_index,
            completed_end,
        );
        exited += 1;
        parent_index = completed.parent_partition_slot_index;
        candidate_index = completed.next_sibling_partition;
        expected_previous = completed_index;
    }
}

fn validateDirectOwnerRange(
    view: layout.View,
    table_slot_index: u32,
    owner_partition_slot_index: u32,
    owner_partition_generation: u64,
    cursor_ordinal: *u32,
    cell_index: *u32,
    end_ordinal: u32,
) !void {
    if (end_ordinal < cursor_ordinal.*) return error.InvalidState;
    while (cursor_ordinal.* < end_ordinal) {
        if (cell_index.* == layout.no_slot or cell_index.* >= view.ledger_cells.len) {
            return error.InvalidState;
        }
        const cell = &view.ledger_cells[cell_index.*];
        if (!isOccupied(cell) or cell.table_slot_index != table_slot_index or
            cell.table_local_ordinal != cursor_ordinal.* or
            cell.owner_partition_slot_index != owner_partition_slot_index or
            cell.owner_partition_generation != owner_partition_generation)
        {
            return error.InvalidState;
        }
        cell_index.* = cell.next_table_cell;
        cursor_ordinal.* += 1;
    }
}

fn validatePartitionFreeList(view: layout.View, occupied_count: u32) !void {
    const expected_free_count = std.math.sub(
        u32,
        @intCast(view.partitions.len),
        occupied_count,
    ) catch return error.InvalidState;
    var free_count: u32 = 0;
    var free_index = view.header.partition_free_head;
    while (free_index != layout.no_slot) {
        if (free_index >= view.partitions.len or free_count >= expected_free_count) {
            return error.InvalidState;
        }
        const slot = &view.partitions[free_index];
        if (isOccupied(slot) or slot.structure_revision != 0 or
            slot.parent_partition_generation != 0 or slot.table_slot_index != 0 or
            slot.parent_partition_slot_index != layout.no_slot or
            slot.first_cell_index != layout.no_slot or slot.local_start != 0 or
            slot.capacity != 0 or slot.first_child_partition != layout.no_slot or
            slot.previous_sibling_partition != layout.no_slot or
            slot.next_sibling_partition != layout.no_slot or slot.first_table_ordinal != 0)
        {
            return error.InvalidState;
        }
        free_count += 1;
        free_index = slot.next_free;
    }
    if (free_count != expected_free_count) return error.InvalidState;
}

fn bindingIsCurrent(
    view: layout.View,
    table_slot_index: u32,
    binding: protocol.CapabilityBinding,
) bool {
    if (protocol.isEmptyBinding(binding)) return false;
    if (binding.slot_index >= view.capabilities.len) return false;
    const capability = &view.capabilities[binding.slot_index];
    return isOccupied(capability) and capability.table_slot_index == table_slot_index and
        capability.capability_id == binding.capability_id and
        capability.generation == binding.generation;
}

fn bindingMatchesSlot(
    binding: protocol.CapabilityBinding,
    capability: *const layout.CapabilitySlot,
    capability_slot_index: u32,
) bool {
    return binding.capability_id == capability.capability_id and
        binding.generation == capability.generation and binding.slot_index == capability_slot_index;
}

fn validFreeHead(value: u32, capacity: u32) bool {
    return value == layout.no_slot or value < capacity;
}

fn ensureSnapshotAdvance(header: *const layout.Header) !void {
    if (header.snapshot_generation == std.math.maxInt(u64)) return error.GenerationExhausted;
}

fn advanceSnapshotAssumeAvailable(header: *layout.Header) void {
    header.snapshot_generation += 1;
}

fn takeGeneration(next: *u64) !u64 {
    if (next.* == std.math.maxInt(u64)) return error.GenerationExhausted;
    const value = next.*;
    next.* += 1;
    return value;
}

fn increment(value: u64) !u64 {
    return std.math.add(u64, value, 1) catch error.GenerationExhausted;
}

fn allZero(values: []const u8) bool {
    for (values) |value| if (value != 0) return false;
    return true;
}
