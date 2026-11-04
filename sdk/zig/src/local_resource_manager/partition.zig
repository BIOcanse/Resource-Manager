const std = @import("std");
const protocol = @import("protocol.zig");
const layout = @import("layout.zig");
const state = @import("state.zig");
const activity = @import("activity.zig");
const planning = @import("planning.zig");
const operation_sequence = @import("operation_sequence.zig");

pub const AdmissionPlan = struct {
    summary: protocol.PartitionAdmissionSummary,
    ticket: protocol.PartitionAdmissionTicket,
};

const Parent = struct {
    table_slot_index: u32,
    partition_slot_index: u32,
    partition_generation: u64,
    first_cell_index: u32,
    capacity: u32,
    structure_revision: u64,
    first_child_partition: u32,

    fn isTable(self: Parent) bool {
        return self.partition_slot_index == layout.no_slot;
    }
};

const SubtreeStats = struct {
    settled_activity: u64 = 0,
    resource_count: u32 = 0,
    protected: bool = false,
};

const ChildRank = struct {
    settled_activity: u64,
    local_start: u32,
    slot_index: u32,
};

const scratch_present: u32 = 1 << 0;
const scratch_protected: u32 = 1 << 1;

const ChildSortContext = struct {
    view: layout.View,
    scratch: []const protocol.PartitionAdmissionScratch,
};

pub fn resolvePartition(
    view: layout.View,
    handle: protocol.PartitionHandle,
) !*layout.PartitionSlot {
    if (handle.manager_instance_id != view.header.manager_instance_id or
        handle.table_slot_index >= view.tables.len or
        handle.partition_slot_index >= view.partitions.len or
        handle.table_slot_generation == 0 or handle.partition_slot_generation == 0)
    {
        return error.StalePartition;
    }
    const table = &view.tables[handle.table_slot_index];
    const partition_slot = &view.partitions[handle.partition_slot_index];
    if (!state.isOccupied(table) or !state.isOccupied(partition_slot) or
        table.generation != handle.table_slot_generation or
        partition_slot.generation != handle.partition_slot_generation or
        partition_slot.table_slot_index != handle.table_slot_index or
        table.table_incarnation != handle.table_incarnation or
        !protocol.sameId(table.table_id, handle.table_id))
    {
        return error.StalePartition;
    }
    return partition_slot;
}

pub fn partitionHandle(view: layout.View, slot_index: u32) protocol.PartitionHandle {
    const slot = &view.partitions[slot_index];
    const table = &view.tables[slot.table_slot_index];
    return .{
        .manager_instance_id = view.header.manager_instance_id,
        .table_slot_generation = table.generation,
        .partition_slot_generation = slot.generation,
        .table_id = table.table_id,
        .table_incarnation = table.table_incarnation,
        .table_slot_index = slot.table_slot_index,
        .partition_slot_index = slot_index,
    };
}

pub fn planAdmission(
    view: layout.View,
    input: protocol.PartitionCreateInput,
    victim_output: []protocol.PartitionHandle,
    intent_output: []protocol.Intent,
    scratch: []protocol.PartitionAdmissionScratch,
) !AdmissionPlan {
    _ = try operation_sequence.requireCurrentImplicit(view);
    if (view.header.active_operation_phase != .planning and
        view.header.active_operation_phase != .executing)
    {
        return if (view.header.active_operation_phase == .recovery_required)
            error.RecoveryRequired
        else
            error.InvalidOperationPhase;
    }
    if (!protocol.validPartitionCreateInput(input)) return error.InvalidArgument;
    if (scratch.len < view.partitions.len) return error.BufferTooSmall;
    const parent = try resolveInputParent(view, input);
    if (input.capacity > parent.capacity) return error.InvalidArgument;
    if (parent.isTable()) {
        const table = &view.tables[parent.table_slot_index];
        if (table.partition_reservation_capacity == 0) return error.PartitionFull;
        if (input.capacity > table.partition_reservation_capacity) return error.InvalidArgument;
    }

    @memset(scratch[0..view.partitions.len], std.mem.zeroes(protocol.PartitionAdmissionScratch));
    const child_count = try prepareChildSummaries(view, parent, victim_output, scratch);
    try accumulateChildStats(view, parent, scratch);

    var reclaimable_count: u32 = 0;
    for (victim_output[0..child_count]) |handle| {
        if (scratch[handle.partition_slot_index].flags & scratch_protected != 0) continue;
        victim_output[reclaimable_count] = handle;
        reclaimable_count += 1;
    }
    std.sort.heap(
        protocol.PartitionHandle,
        victim_output[0..reclaimable_count],
        ChildSortContext{ .view = view, .scratch = scratch },
        childHandleBefore,
    );
    for (victim_output[0..reclaimable_count], 0..) |handle, index| {
        scratch[handle.partition_slot_index].selection_rank = @intCast(index + 1);
    }

    var selected_count: u32 = 0;
    var local_start = if (view.header.partition_free_head == layout.no_slot)
        null
    else
        try findAdmissionRun(view, parent, input.capacity, scratch, 0);
    if (local_start == null) {
        if (reclaimable_count == 0) return error.PartitionFull;
        if (try findAdmissionRun(
            view,
            parent,
            input.capacity,
            scratch,
            reclaimable_count,
        ) == null) return error.PartitionFull;

        var low: u32 = 1;
        var high = reclaimable_count;
        while (low < high) {
            const middle = low + (high - low) / 2;
            if (try findAdmissionRun(view, parent, input.capacity, scratch, middle) != null) {
                high = middle;
            } else {
                low = middle + 1;
            }
        }
        selected_count = low;
        local_start = try findAdmissionRun(view, parent, input.capacity, scratch, selected_count);
    }

    const intent_count = try countSelectedResources(view, parent, scratch, selected_count);
    if (intent_count > intent_output.len) return error.BufferTooSmall;
    try publishSelectedIntents(
        view,
        parent,
        scratch,
        selected_count,
        intent_output[0..intent_count],
    );

    if (view.header.next_partition_admission_id == std.math.maxInt(u64)) {
        return error.GenerationExhausted;
    }

    const victims = victim_output[0..selected_count];
    const table_handle = state.tableHandle(view, parent.table_slot_index);
    const parent_handle = if (parent.isTable())
        std.mem.zeroes(protocol.PartitionHandle)
    else
        partitionHandle(view, parent.partition_slot_index);
    const operation = operation_sequence.currentToken(view);
    const ticket = protocol.PartitionAdmissionTicket{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.PartitionAdmissionTicket),
        .table = table_handle,
        .parent = parent_handle,
        .manager_instance_id = view.header.manager_instance_id,
        .operation_id = operation.operation_id,
        .operation_generation = operation.operation_generation,
        .admission_id = view.header.next_partition_admission_id,
        .parent_structure_revision = parent.structure_revision,
        .victim_hash = hashVictims(victims),
        .local_start = local_start.?,
        .capacity = input.capacity,
        .victim_count = selected_count,
        .has_parent = if (parent.isTable()) 0 else 1,
        .kind = .create,
        .reserved0 = .{ 0, 0 },
    };
    const table = &view.tables[parent.table_slot_index];
    table.active_partition_admission_id = ticket.admission_id;
    table.active_partition_admission_hash = hashAdmission(ticket, victims);
    table.active_partition_operation_id = operation.operation_id;
    table.active_partition_operation_generation = operation.operation_generation;
    view.header.next_partition_admission_id += 1;
    return .{
        .summary = .{
            .operation_id = operation.operation_id,
            .operation_generation = operation.operation_generation,
            .snapshot_generation = view.header.snapshot_generation,
            .intent_count = intent_count,
            .victim_count = selected_count,
            .local_start = local_start.?,
            .capacity = input.capacity,
        },
        .ticket = ticket,
    };
}

pub fn commitAdmission(
    view: layout.View,
    ticket: protocol.PartitionAdmissionTicket,
    victims: []const protocol.PartitionHandle,
) !protocol.PartitionHandle {
    try validateTicketShape(view, ticket, victims, .create);
    const operation = operation_sequence.currentToken(view);
    try operation_sequence.requireSettlementEligible(view, operation);
    const table = try state.resolveTable(view, ticket.table);
    if (table.active_partition_admission_id != ticket.admission_id or
        table.active_partition_admission_hash != hashAdmission(ticket, victims) or
        table.active_partition_operation_id != ticket.operation_id or
        table.active_partition_operation_generation != ticket.operation_generation)
    {
        return error.AdmissionStale;
    }
    const parent = try resolveTicketParent(view, ticket, table);
    if (parent.structure_revision != ticket.parent_structure_revision) {
        return error.AdmissionStale;
    }
    if (ticket.local_start > parent.capacity or
        ticket.capacity > parent.capacity - ticket.local_start)
    {
        return error.AdmissionStale;
    }

    for (victims, 0..) |victim_handle, victim_index| {
        for (victims[victim_index + 1 ..]) |other| {
            if (victim_handle.partition_slot_index == other.partition_slot_index and
                victim_handle.partition_slot_generation == other.partition_slot_generation)
            {
                return error.AdmissionStale;
            }
        }
        const victim = try resolvePartition(view, victim_handle);
        if (victim.table_slot_index != parent.table_slot_index or
            victim.parent_partition_slot_index != parent.partition_slot_index or
            victim.parent_partition_generation != parent.partition_generation)
        {
            return error.AdmissionStale;
        }
        if (!subtreeIsEmpty(view, victim)) return error.PartitionBusy;
    }

    var ordinal: u32 = 0;
    var cell_index = try cellAtParentOrdinal(view, parent, ticket.local_start);
    while (ordinal < ticket.capacity) : (ordinal += 1) {
        const cell = &view.ledger_cells[cell_index];
        if (parent.isTable() and !state.isPartitionReservedCell(cell)) {
            return error.AdmissionStale;
        }
        if (cell.resource_slot_index != layout.no_slot) return error.PartitionBusy;
        if (!cellOwnedDirectlyByParent(cell, parent)) {
            const direct_child = try directChildForCellOwner(view, parent, cell) orelse
                return error.AdmissionStale;
            if (!containsVictim(victims, view, direct_child)) return error.AdmissionStale;
        }
        cell_index = cell.next_table_cell;
    }

    if (view.header.next_partition_generation == std.math.maxInt(u64)) {
        return error.GenerationExhausted;
    }
    if (parent.structure_revision == std.math.maxInt(u64) or
        view.header.snapshot_generation == std.math.maxInt(u64))
    {
        return error.GenerationExhausted;
    }
    if (view.header.partition_free_head == layout.no_slot and victims.len == 0) {
        return error.PartitionFull;
    }

    try operation_sequence.enterSettlement(view, operation);

    for (victims) |victim_handle| {
        destroyEmptySubtreeAssumeValid(view, victim_handle.partition_slot_index);
    }

    const slot_index = view.header.partition_free_head;
    if (slot_index == layout.no_slot) return error.InvalidState;
    const generation = view.header.next_partition_generation;
    view.header.next_partition_generation += 1;
    const slot = &view.partitions[slot_index];
    if (state.isOccupied(slot) or slot.parent_partition_slot_index != layout.no_slot or
        slot.first_cell_index != layout.no_slot or slot.first_child_partition != layout.no_slot or
        slot.previous_sibling_partition != layout.no_slot or
        slot.next_sibling_partition != layout.no_slot)
    {
        unreachable;
    }
    view.header.partition_free_head = slot.next_free;
    const first_cell = try cellAtParentOrdinal(view, parent, ticket.local_start);
    slot.* = .{
        .generation = generation,
        .structure_revision = 1,
        .parent_partition_generation = parent.partition_generation,
        .table_slot_index = parent.table_slot_index,
        .parent_partition_slot_index = parent.partition_slot_index,
        .first_cell_index = first_cell,
        .local_start = ticket.local_start,
        .capacity = ticket.capacity,
        .first_child_partition = layout.no_slot,
        .previous_sibling_partition = layout.no_slot,
        .next_sibling_partition = layout.no_slot,
        .next_free = layout.no_slot,
        .flags = layout.occupied_flag,
        .first_table_ordinal = view.ledger_cells[first_cell].table_local_ordinal,
    };
    claimCells(view, slot_index, slot);
    linkPartition(view, parent, slot_index);
    incrementParentRevision(view, parent);
    clearActiveAdmission(table);
    view.header.partition_count += 1;
    view.header.snapshot_generation += 1;
    return partitionHandle(view, slot_index);
}

pub fn planClose(
    view: layout.View,
    target: protocol.PartitionHandle,
    intent_output: []protocol.Intent,
) !AdmissionPlan {
    _ = try operation_sequence.requireCurrentImplicit(view);
    if (view.header.active_operation_phase != .planning and
        view.header.active_operation_phase != .executing)
    {
        return if (view.header.active_operation_phase == .recovery_required)
            error.RecoveryRequired
        else
            error.InvalidOperationPhase;
    }

    const target_slot = try resolvePartition(view, target);
    const parent = try parentForSlot(view, target_slot);
    const stats = try subtreeStats(view, target.partition_slot_index);
    if (stats.protected) return error.PartitionBusy;
    if (stats.resource_count > intent_output.len) return error.BufferTooSmall;
    try publishCloseIntents(view, target, intent_output[0..stats.resource_count]);

    if (view.header.next_partition_admission_id == std.math.maxInt(u64)) {
        return error.GenerationExhausted;
    }

    const table_handle = state.tableHandle(view, parent.table_slot_index);
    const parent_handle = if (parent.isTable())
        std.mem.zeroes(protocol.PartitionHandle)
    else
        partitionHandle(view, parent.partition_slot_index);
    const operation = operation_sequence.currentToken(view);
    const victims = [_]protocol.PartitionHandle{target};
    const ticket = protocol.PartitionAdmissionTicket{
        .abi_version = protocol.abi_version,
        .struct_size = @sizeOf(protocol.PartitionAdmissionTicket),
        .table = table_handle,
        .parent = parent_handle,
        .manager_instance_id = view.header.manager_instance_id,
        .operation_id = operation.operation_id,
        .operation_generation = operation.operation_generation,
        .admission_id = view.header.next_partition_admission_id,
        .parent_structure_revision = parent.structure_revision,
        .victim_hash = hashVictims(victims[0..]),
        .local_start = target_slot.local_start,
        .capacity = target_slot.capacity,
        .victim_count = 1,
        .has_parent = if (parent.isTable()) 0 else 1,
        .kind = .close,
        .reserved0 = .{ 0, 0 },
    };
    const table = &view.tables[parent.table_slot_index];
    table.active_partition_admission_id = ticket.admission_id;
    table.active_partition_admission_hash = hashAdmission(ticket, victims[0..]);
    table.active_partition_operation_id = operation.operation_id;
    table.active_partition_operation_generation = operation.operation_generation;
    view.header.next_partition_admission_id += 1;
    return .{
        .summary = .{
            .operation_id = operation.operation_id,
            .operation_generation = operation.operation_generation,
            .snapshot_generation = view.header.snapshot_generation,
            .intent_count = stats.resource_count,
            .victim_count = 1,
            .local_start = target_slot.local_start,
            .capacity = target_slot.capacity,
        },
        .ticket = ticket,
    };
}

pub fn commitClose(
    view: layout.View,
    ticket: protocol.PartitionAdmissionTicket,
    target: protocol.PartitionHandle,
) !void {
    const victims = [_]protocol.PartitionHandle{target};
    try validateTicketShape(view, ticket, victims[0..], .close);
    const operation = operation_sequence.currentToken(view);
    try operation_sequence.requireSettlementEligible(view, operation);
    const table = try state.resolveTable(view, ticket.table);
    if (table.active_partition_admission_id != ticket.admission_id or
        table.active_partition_admission_hash != hashAdmission(ticket, victims[0..]) or
        table.active_partition_operation_id != ticket.operation_id or
        table.active_partition_operation_generation != ticket.operation_generation)
    {
        return error.AdmissionStale;
    }

    const parent = try resolveTicketParent(view, ticket, table);
    if (parent.structure_revision != ticket.parent_structure_revision) {
        return error.AdmissionStale;
    }
    const target_slot = try resolvePartition(view, target);
    if (target_slot.table_slot_index != parent.table_slot_index or
        target_slot.parent_partition_slot_index != parent.partition_slot_index or
        target_slot.parent_partition_generation != parent.partition_generation or
        target_slot.local_start != ticket.local_start or
        target_slot.capacity != ticket.capacity)
    {
        return error.AdmissionStale;
    }
    if (!subtreeIsEmpty(view, target_slot)) return error.PartitionBusy;
    if (parent.structure_revision == std.math.maxInt(u64) or
        view.header.snapshot_generation == std.math.maxInt(u64))
    {
        return error.GenerationExhausted;
    }

    try operation_sequence.enterSettlement(view, operation);
    destroyEmptySubtreeAssumeValid(view, target.partition_slot_index);
    incrementParentRevision(view, parent);
    clearActiveAdmission(table);
    view.header.snapshot_generation += 1;
}

pub fn registerResourceAt(
    view: layout.View,
    partition_handle: protocol.PartitionHandle,
    local_ordinal: u32,
    spec: protocol.ResourceSpec,
) !protocol.ResourceHandle {
    const partition_slot = try resolvePartition(view, partition_handle);
    if (local_ordinal >= partition_slot.capacity) return error.InvalidArgument;
    const cell_index = try partitionCellAtOrdinal(view, partition_slot, local_ordinal);
    const cell = &view.ledger_cells[cell_index];
    if (cell.owner_partition_slot_index != partition_handle.partition_slot_index or
        cell.owner_partition_generation != partition_handle.partition_slot_generation)
    {
        return error.SlotOccupied;
    }
    return state.registerPartitionResourceAtLedgerCell(
        view,
        partition_handle,
        cell_index,
        spec,
    );
}

pub fn planResourceAdmission(
    view: layout.View,
    partition_handle: protocol.PartitionHandle,
    spec: protocol.ResourceSpec,
) !protocol.PartitionResourceAdmission {
    _ = try operation_sequence.requireCurrentImplicit(view);
    if (view.header.active_operation_phase != .planning and
        view.header.active_operation_phase != .executing)
    {
        return if (view.header.active_operation_phase == .recovery_required)
            error.RecoveryRequired
        else
            error.InvalidOperationPhase;
    }
    const partition_slot = try resolvePartition(view, partition_handle);
    var first_empty: ?u32 = null;
    var victim_ordinal: ?u32 = null;
    var victim_activity: u64 = 0;
    var cell_index = partition_slot.first_cell_index;
    var ordinal: u32 = 0;
    while (ordinal < partition_slot.capacity) : (ordinal += 1) {
        const cell = &view.ledger_cells[cell_index];
        if (cell.owner_partition_slot_index == partition_handle.partition_slot_index and
            cell.owner_partition_generation == partition_handle.partition_slot_generation)
        {
            if (cell.resource_slot_index == layout.no_slot) {
                if (first_empty == null) first_empty = ordinal;
            } else if (planning.partitionCandidate(
                view,
                cell.resource_slot_index,
                partition_handle,
            )) |candidate| {
                if (victim_ordinal == null or candidate.settled_activity < victim_activity or
                    (candidate.settled_activity == victim_activity and ordinal < victim_ordinal.?))
                {
                    victim_ordinal = ordinal;
                    victim_activity = candidate.settled_activity;
                }
            }
        }
        cell_index = cell.next_table_cell;
    }

    const selected_ordinal = first_empty orelse victim_ordinal orelse return error.PartitionFull;
    var intent = std.mem.zeroes(protocol.Intent);
    var requires_release: u8 = 0;
    var replacement_resource_slot_index: ?u32 = null;
    if (first_empty == null) {
        const victim_cell = try partitionCellAtOrdinal(view, partition_slot, selected_ordinal);
        replacement_resource_slot_index = view.ledger_cells[victim_cell].resource_slot_index;
        intent = planning.partitionCandidate(
            view,
            replacement_resource_slot_index.?,
            partition_handle,
        ) orelse return error.PartitionBusy;
        requires_release = 1;
    }
    try state.validatePartitionResourceAdmissionSpec(
        view,
        state.tableHandle(view, partition_slot.table_slot_index),
        spec,
        replacement_resource_slot_index,
    );
    const operation = operation_sequence.currentToken(view);
    return .{
        .partition = partition_handle,
        .intent = intent,
        .resource = spec,
        .operation_id = operation.operation_id,
        .operation_generation = operation.operation_generation,
        .snapshot_generation = view.header.snapshot_generation,
        .structure_revision = partition_slot.structure_revision,
        .local_ordinal = selected_ordinal,
        .requires_release = requires_release,
        .reserved0 = .{ 0, 0, 0 },
    };
}

pub fn commitResourceAdmission(
    view: layout.View,
    admission: protocol.PartitionResourceAdmission,
) !protocol.ResourceHandle {
    const operation = operation_sequence.currentToken(view);
    if (admission.operation_id != operation.operation_id or
        admission.operation_generation != operation.operation_generation)
    {
        return error.AdmissionStale;
    }
    try operation_sequence.requireSettlementEligible(view, operation);
    if (admission.snapshot_generation == 0 or admission.structure_revision == 0 or
        admission.requires_release > 1 or !allZero(admission.reserved0[0..]) or
        !protocol.validResourceSpec(admission.resource))
    {
        return error.InvalidArgument;
    }
    if (admission.requires_release == 0) {
        if (!allZero(std.mem.asBytes(&admission.intent))) return error.InvalidArgument;
    } else if (!planning.validIntentShape(admission.intent) or
        admission.intent.reason_flags != protocol.intent_reason_partition or
        admission.intent.reclaim_partition_slot_index !=
            admission.partition.partition_slot_index or
        admission.intent.reclaim_partition_slot_generation !=
            admission.partition.partition_slot_generation)
    {
        return error.InvalidArgument;
    }

    const slot = try resolvePartition(view, admission.partition);
    if (slot.structure_revision != admission.structure_revision or
        admission.local_ordinal >= slot.capacity)
    {
        return error.AdmissionStale;
    }
    const cell_index = try partitionCellAtOrdinal(view, slot, admission.local_ordinal);
    const cell = &view.ledger_cells[cell_index];
    if (cell.owner_partition_slot_index != admission.partition.partition_slot_index or
        cell.owner_partition_generation != admission.partition.partition_slot_generation)
    {
        return error.AdmissionStale;
    }
    if (cell.resource_slot_index != layout.no_slot or cell.resource_slot_generation != 0) {
        return error.SlotOccupied;
    }
    try state.validatePartitionResourceAdmissionSpec(
        view,
        state.tableHandle(view, slot.table_slot_index),
        admission.resource,
        null,
    );
    try operation_sequence.enterSettlement(view, operation);
    return registerResourceAt(
        view,
        admission.partition,
        admission.local_ordinal,
        admission.resource,
    );
}

pub fn readPartition(
    view: layout.View,
    handle: protocol.PartitionHandle,
) !protocol.PartitionView {
    const slot = try resolvePartition(view, handle);
    const stats = try subtreeStats(view, handle.partition_slot_index);
    var child_count: u32 = 0;
    var child_index = slot.first_child_partition;
    while (child_index != layout.no_slot) {
        if (child_index >= view.partitions.len or child_count >= view.partitions.len) {
            return error.InvalidState;
        }
        child_count += 1;
        child_index = view.partitions[child_index].next_sibling_partition;
    }
    const parent_handle = if (slot.parent_partition_slot_index == layout.no_slot)
        std.mem.zeroes(protocol.PartitionHandle)
    else
        partitionHandle(view, slot.parent_partition_slot_index);
    return .{
        .partition = handle,
        .parent = parent_handle,
        .settled_activity = stats.settled_activity,
        .structure_revision = slot.structure_revision,
        .local_start = slot.local_start,
        .capacity = slot.capacity,
        .direct_child_count = child_count,
        .descendant_resource_count = stats.resource_count,
        .has_parent = if (slot.parent_partition_slot_index == layout.no_slot) 0 else 1,
        .reclaim_protected = if (stats.protected) 1 else 0,
        .reserved0 = .{ 0, 0, 0, 0, 0, 0 },
    };
}

pub fn readCell(
    view: layout.View,
    handle: protocol.PartitionHandle,
    local_ordinal: u32,
) !protocol.PartitionCellView {
    const slot = try resolvePartition(view, handle);
    if (local_ordinal >= slot.capacity) return error.InvalidArgument;
    const cell_index = try partitionCellAtOrdinal(view, slot, local_ordinal);
    const cell = &view.ledger_cells[cell_index];
    var output = protocol.PartitionCellView{
        .partition = handle,
        .resource = std.mem.zeroes(protocol.ResourceHandle),
        .child_partition = std.mem.zeroes(protocol.PartitionHandle),
        .local_ordinal = local_ordinal,
        .kind = .empty,
        .reserved0 = .{ 0, 0, 0 },
    };
    if (cell.owner_partition_slot_index == handle.partition_slot_index and
        cell.owner_partition_generation == handle.partition_slot_generation)
    {
        if (cell.resource_slot_index != layout.no_slot) {
            output.kind = .resource;
            output.resource = state.resourceHandle(view, cell.resource_slot_index);
        }
        return output;
    }
    const parent = parentFromPartitionAt(view, handle.partition_slot_index);
    const child_index = try directChildForCellOwner(view, parent, cell) orelse
        return error.InvalidState;
    output.kind = .child_partition;
    output.child_partition = partitionHandle(view, child_index);
    return output;
}

pub fn resourceBelongsToPartition(
    view: layout.View,
    resource: *const layout.ResourceSlot,
    handle: protocol.PartitionHandle,
) bool {
    const target = resolvePartition(view, handle) catch return false;
    if (target.table_slot_index != resource.table_slot_index or
        resource.ledger_cell_index >= view.ledger_cells.len)
    {
        return false;
    }
    const cell = &view.ledger_cells[resource.ledger_cell_index];
    var owner_index = cell.owner_partition_slot_index;
    var owner_generation = cell.owner_partition_generation;
    var hops: u32 = 0;
    while (owner_index != layout.no_slot and hops < view.partitions.len) : (hops += 1) {
        if (owner_index >= view.partitions.len) return false;
        const owner = &view.partitions[owner_index];
        if (!state.isOccupied(owner) or owner.generation != owner_generation) return false;
        if (owner_index == handle.partition_slot_index and
            owner_generation == handle.partition_slot_generation)
        {
            return true;
        }
        owner_index = owner.parent_partition_slot_index;
        owner_generation = owner.parent_partition_generation;
    }
    return false;
}

fn resolveInputParent(view: layout.View, input: protocol.PartitionCreateInput) !Parent {
    const table = try state.resolveTable(view, input.table);
    if (input.has_parent == 0) return parentFromTable(input.table.slot_index, table);
    _ = try resolvePartition(view, input.parent);
    if (input.parent.table_slot_index != input.table.slot_index or
        input.parent.table_slot_generation != input.table.slot_generation or
        input.parent.table_incarnation != input.table.table_incarnation or
        !protocol.sameId(input.parent.table_id, input.table.table_id))
    {
        return error.StalePartition;
    }
    return parentFromPartitionAt(view, input.parent.partition_slot_index);
}

fn resolveTicketParent(
    view: layout.View,
    ticket: protocol.PartitionAdmissionTicket,
    table: *layout.TableSlot,
) !Parent {
    if (ticket.has_parent == 0) return parentFromTable(ticket.table.slot_index, table);
    const parent_slot = try resolvePartition(view, ticket.parent);
    if (parent_slot.table_slot_index != ticket.table.slot_index) return error.AdmissionStale;
    return parentFromPartitionAt(view, ticket.parent.partition_slot_index);
}

fn parentForSlot(view: layout.View, slot: *const layout.PartitionSlot) !Parent {
    if (slot.parent_partition_slot_index == layout.no_slot) {
        return parentFromTable(slot.table_slot_index, &view.tables[slot.table_slot_index]);
    }
    if (slot.parent_partition_slot_index >= view.partitions.len) return error.InvalidState;
    const parent_slot = &view.partitions[slot.parent_partition_slot_index];
    if (!state.isOccupied(parent_slot) or
        parent_slot.generation != slot.parent_partition_generation)
    {
        return error.InvalidState;
    }
    return parentFromPartitionAt(view, slot.parent_partition_slot_index);
}

fn parentFromTable(table_slot_index: u32, table: *const layout.TableSlot) Parent {
    return .{
        .table_slot_index = table_slot_index,
        .partition_slot_index = layout.no_slot,
        .partition_generation = 0,
        .first_cell_index = table.first_ledger_cell,
        .capacity = table.capacity,
        .structure_revision = table.partition_revision,
        .first_child_partition = table.first_partition_slot,
    };
}

fn parentFromPartitionAt(view: layout.View, slot_index: u32) Parent {
    const slot = &view.partitions[slot_index];
    return .{
        .table_slot_index = slot.table_slot_index,
        .partition_slot_index = slot_index,
        .partition_generation = slot.generation,
        .first_cell_index = slot.first_cell_index,
        .capacity = slot.capacity,
        .structure_revision = slot.structure_revision,
        .first_child_partition = slot.first_child_partition,
    };
}

fn cellAtParentOrdinal(view: layout.View, parent: Parent, ordinal: u32) !u32 {
    if (ordinal >= parent.capacity) return error.InvalidArgument;
    var cell_index = parent.first_cell_index;
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

fn partitionCellAtOrdinal(
    view: layout.View,
    slot: *const layout.PartitionSlot,
    ordinal: u32,
) !u32 {
    return cellAtParentOrdinal(view, .{
        .table_slot_index = slot.table_slot_index,
        .partition_slot_index = layout.no_slot,
        .partition_generation = 0,
        .first_cell_index = slot.first_cell_index,
        .capacity = slot.capacity,
        .structure_revision = slot.structure_revision,
        .first_child_partition = slot.first_child_partition,
    }, ordinal);
}

fn findAdmissionRun(
    view: layout.View,
    parent: Parent,
    requested_capacity: u32,
    scratch: []const protocol.PartitionAdmissionScratch,
    selected_count: u32,
) !?u32 {
    var run_start: u32 = 0;
    var run_length: u32 = 0;
    var cell_index = parent.first_cell_index;
    var child_index = parent.first_child_partition;
    var ordinal: u32 = 0;
    while (ordinal < parent.capacity) : (ordinal += 1) {
        const cell = &view.ledger_cells[cell_index];
        const direct_child = try childAtOrdinal(view, ordinal, &child_index);
        const available = if (direct_child) |index|
            scratch[index].selection_rank != 0 and
                scratch[index].selection_rank <= selected_count
        else
            cellOwnedDirectlyByParent(cell, parent) and
                cell.resource_slot_index == layout.no_slot and
                (!parent.isTable() or state.isPartitionReservedCell(cell));
        if (available) {
            if (run_length == 0) run_start = ordinal;
            run_length += 1;
            if (run_length >= requested_capacity) return run_start;
        } else {
            run_length = 0;
        }
        cell_index = cell.next_table_cell;
    }
    return null;
}

fn prepareChildSummaries(
    view: layout.View,
    parent: Parent,
    victim_output: []protocol.PartitionHandle,
    scratch: []protocol.PartitionAdmissionScratch,
) !u32 {
    var child_count: u32 = 0;
    var child_index = parent.first_child_partition;
    var visited: u32 = 0;
    while (child_index != layout.no_slot) : (visited += 1) {
        if (visited >= view.partitions.len or child_index >= view.partitions.len) {
            return error.InvalidState;
        }
        const child = &view.partitions[child_index];
        if (child_count >= victim_output.len) return error.BufferTooSmall;
        scratch[child_index] = .{
            .settled_activity = 0,
            .resource_count = 0,
            .local_start = child.local_start,
            .selection_rank = 0,
            .flags = scratch_present,
        };
        victim_output[child_count] = partitionHandle(view, child_index);
        child_count += 1;
        child_index = child.next_sibling_partition;
    }
    return child_count;
}

fn accumulateChildStats(
    view: layout.View,
    parent: Parent,
    scratch: []protocol.PartitionAdmissionScratch,
) !void {
    var cell_index = parent.first_cell_index;
    var child_index = parent.first_child_partition;
    var ordinal: u32 = 0;
    while (ordinal < parent.capacity) : (ordinal += 1) {
        const cell = &view.ledger_cells[cell_index];
        if (try childAtOrdinal(view, ordinal, &child_index)) |direct_child| {
            if (cell.resource_slot_index != layout.no_slot) {
                const resource = &view.resources[cell.resource_slot_index];
                const summary = &scratch[direct_child];
                summary.resource_count += 1;
                const settled = activity.settledValue(resource, view.header);
                if (settled > summary.settled_activity) summary.settled_activity = settled;
                if (planning.partitionCandidate(
                    view,
                    cell.resource_slot_index,
                    partitionHandle(view, direct_child),
                ) == null) {
                    summary.flags |= scratch_protected;
                }
            }
        }
        cell_index = cell.next_table_cell;
    }
}

fn childHandleBefore(
    context: ChildSortContext,
    left: protocol.PartitionHandle,
    right: protocol.PartitionHandle,
) bool {
    const left_summary = context.scratch[left.partition_slot_index];
    const right_summary = context.scratch[right.partition_slot_index];
    return rankLess(.{
        .settled_activity = left_summary.settled_activity,
        .local_start = left_summary.local_start,
        .slot_index = left.partition_slot_index,
    }, .{
        .settled_activity = right_summary.settled_activity,
        .local_start = right_summary.local_start,
        .slot_index = right.partition_slot_index,
    });
}

fn countSelectedResources(
    view: layout.View,
    parent: Parent,
    scratch: []const protocol.PartitionAdmissionScratch,
    selected_count: u32,
) !u32 {
    var count: u32 = 0;
    var cell_index = parent.first_cell_index;
    var child_index = parent.first_child_partition;
    var ordinal: u32 = 0;
    while (ordinal < parent.capacity) : (ordinal += 1) {
        const cell = &view.ledger_cells[cell_index];
        if (try childAtOrdinal(view, ordinal, &child_index)) |direct_child| {
            const rank = scratch[direct_child].selection_rank;
            if (rank != 0 and rank <= selected_count and
                cell.resource_slot_index != layout.no_slot)
            {
                count += 1;
            }
        }
        cell_index = cell.next_table_cell;
    }
    return count;
}

fn publishSelectedIntents(
    view: layout.View,
    parent: Parent,
    scratch: []const protocol.PartitionAdmissionScratch,
    selected_count: u32,
    output: []protocol.Intent,
) !void {
    var published: u32 = 0;
    var cell_index = parent.first_cell_index;
    var child_index = parent.first_child_partition;
    var ordinal: u32 = 0;
    while (ordinal < parent.capacity) : (ordinal += 1) {
        const cell = &view.ledger_cells[cell_index];
        if (try childAtOrdinal(view, ordinal, &child_index)) |direct_child| {
            const rank = scratch[direct_child].selection_rank;
            if (rank != 0 and rank <= selected_count and
                cell.resource_slot_index != layout.no_slot)
            {
                output[published] = planning.partitionCandidate(
                    view,
                    cell.resource_slot_index,
                    partitionHandle(view, direct_child),
                ) orelse return error.PartitionBusy;
                published += 1;
            }
        }
        cell_index = cell.next_table_cell;
    }
    if (published != output.len) return error.InvalidState;
}

fn publishCloseIntents(
    view: layout.View,
    target: protocol.PartitionHandle,
    output: []protocol.Intent,
) !void {
    var published: usize = 0;
    for (view.resources, 0..) |*resource, resource_index| {
        if (!state.isOccupied(resource) or
            !resourceBelongsToPartition(view, resource, target))
        {
            continue;
        }
        if (published >= output.len) return error.InvalidState;
        output[published] = planning.partitionCandidate(
            view,
            @intCast(resource_index),
            target,
        ) orelse return error.PartitionBusy;
        published += 1;
    }
    if (published != output.len) return error.InvalidState;
}

fn childAtOrdinal(
    view: layout.View,
    ordinal: u32,
    child_index: *u32,
) !?u32 {
    var visited: u32 = 0;
    while (child_index.* != layout.no_slot) : (visited += 1) {
        if (visited >= view.partitions.len or child_index.* >= view.partitions.len) {
            return error.InvalidState;
        }
        const child = &view.partitions[child_index.*];
        const child_end = std.math.add(u32, child.local_start, child.capacity) catch
            return error.InvalidState;
        if (ordinal < child.local_start) return null;
        if (ordinal < child_end) return child_index.*;
        child_index.* = child.next_sibling_partition;
    }
    return null;
}

fn rankLess(left: ChildRank, right: ChildRank) bool {
    if (left.settled_activity != right.settled_activity) {
        return left.settled_activity < right.settled_activity;
    }
    if (left.local_start != right.local_start) return left.local_start < right.local_start;
    return left.slot_index < right.slot_index;
}

fn subtreeStats(view: layout.View, partition_slot_index: u32) !SubtreeStats {
    if (partition_slot_index >= view.partitions.len or
        !state.isOccupied(&view.partitions[partition_slot_index]))
    {
        return error.StalePartition;
    }
    var stats = SubtreeStats{};
    for (view.resources, 0..) |*resource, resource_index| {
        if (!state.isOccupied(resource)) continue;
        if (!resourceBelongsToPartition(
            view,
            resource,
            partitionHandle(view, partition_slot_index),
        )) continue;
        stats.resource_count += 1;
        const settled = activity.settledValue(resource, view.header);
        if (settled > stats.settled_activity) stats.settled_activity = settled;
        if (resource.protected_use != 0 or resource.pending_slot_index != layout.no_slot or
            planning.partitionCandidate(
                view,
                @intCast(resource_index),
                partitionHandle(view, partition_slot_index),
            ) == null)
        {
            stats.protected = true;
        }
    }
    return stats;
}

fn directChildForCellOwner(
    view: layout.View,
    parent: Parent,
    cell: *const layout.LedgerCell,
) !?u32 {
    if (cellOwnedDirectlyByParent(cell, parent)) return null;
    var owner_index = cell.owner_partition_slot_index;
    var owner_generation = cell.owner_partition_generation;
    var hops: u32 = 0;
    while (owner_index != layout.no_slot) : (hops += 1) {
        if (hops >= view.partitions.len or owner_index >= view.partitions.len) {
            return error.InvalidState;
        }
        const owner = &view.partitions[owner_index];
        if (!state.isOccupied(owner) or owner.generation != owner_generation or
            owner.table_slot_index != parent.table_slot_index)
        {
            return error.InvalidState;
        }
        if (owner.parent_partition_slot_index == parent.partition_slot_index and
            owner.parent_partition_generation == parent.partition_generation)
        {
            return owner_index;
        }
        owner_index = owner.parent_partition_slot_index;
        owner_generation = owner.parent_partition_generation;
    }
    return error.InvalidState;
}

fn cellOwnedDirectlyByParent(cell: *const layout.LedgerCell, parent: Parent) bool {
    return cell.owner_partition_slot_index == parent.partition_slot_index and
        cell.owner_partition_generation == parent.partition_generation;
}

fn claimCells(view: layout.View, slot_index: u32, slot: *const layout.PartitionSlot) void {
    var cell_index = slot.first_cell_index;
    var ordinal: u32 = 0;
    while (ordinal < slot.capacity) : (ordinal += 1) {
        const cell = &view.ledger_cells[cell_index];
        cell.owner_partition_slot_index = slot_index;
        cell.owner_partition_generation = slot.generation;
        cell_index = cell.next_table_cell;
    }
}

fn linkPartition(view: layout.View, parent: Parent, slot_index: u32) void {
    const slot = &view.partitions[slot_index];
    var current = if (parent.isTable())
        view.tables[parent.table_slot_index].first_partition_slot
    else
        view.partitions[parent.partition_slot_index].first_child_partition;
    var previous = layout.no_slot;
    while (current != layout.no_slot and
        view.partitions[current].local_start < slot.local_start)
    {
        previous = current;
        current = view.partitions[current].next_sibling_partition;
    }

    slot.previous_sibling_partition = previous;
    slot.next_sibling_partition = current;
    if (previous != layout.no_slot) {
        view.partitions[previous].next_sibling_partition = slot_index;
    } else if (parent.isTable()) {
        view.tables[parent.table_slot_index].first_partition_slot = slot_index;
    } else {
        view.partitions[parent.partition_slot_index].first_child_partition = slot_index;
    }
    if (current != layout.no_slot) {
        view.partitions[current].previous_sibling_partition = slot_index;
    }
}

fn unlinkPartitionAssumeValid(view: layout.View, slot_index: u32) void {
    const slot = &view.partitions[slot_index];
    if (slot.previous_sibling_partition == layout.no_slot) {
        if (slot.parent_partition_slot_index == layout.no_slot) {
            view.tables[slot.table_slot_index].first_partition_slot = slot.next_sibling_partition;
        } else {
            const parent = &view.partitions[slot.parent_partition_slot_index];
            if (!state.isOccupied(parent) or parent.generation != slot.parent_partition_generation) {
                unreachable;
            }
            parent.first_child_partition = slot.next_sibling_partition;
        }
    } else {
        view.partitions[slot.previous_sibling_partition].next_sibling_partition =
            slot.next_sibling_partition;
    }
    if (slot.next_sibling_partition != layout.no_slot) {
        view.partitions[slot.next_sibling_partition].previous_sibling_partition =
            slot.previous_sibling_partition;
    }
}

fn subtreeIsEmpty(view: layout.View, root: *const layout.PartitionSlot) bool {
    var cell_index = root.first_cell_index;
    var ordinal: u32 = 0;
    while (ordinal < root.capacity) : (ordinal += 1) {
        if (cell_index == layout.no_slot or cell_index >= view.ledger_cells.len) return false;
        const cell = &view.ledger_cells[cell_index];
        if (cell.resource_slot_index != layout.no_slot) return false;
        cell_index = cell.next_table_cell;
    }
    return true;
}

fn destroyEmptySubtreeAssumeValid(view: layout.View, root_index: u32) void {
    const root = &view.partitions[root_index];
    const restore_owner_index = root.parent_partition_slot_index;
    const restore_owner_generation = root.parent_partition_generation;
    var cell_index = root.first_cell_index;
    var ordinal: u32 = 0;
    while (ordinal < root.capacity) : (ordinal += 1) {
        const cell = &view.ledger_cells[cell_index];
        if (cell.resource_slot_index != layout.no_slot) unreachable;
        cell.owner_partition_slot_index = restore_owner_index;
        cell.owner_partition_generation = restore_owner_generation;
        cell_index = cell.next_table_cell;
    }

    var current_index = root_index;
    while (true) {
        const current = &view.partitions[current_index];
        if (current.first_child_partition != layout.no_slot) {
            current_index = current.first_child_partition;
            continue;
        }
        const next_sibling = current.next_sibling_partition;
        const parent_index = current.parent_partition_slot_index;
        unlinkPartitionAssumeValid(view, current_index);
        freePartitionSlot(view, current_index);
        if (current_index == root_index) break;
        current_index = if (next_sibling != layout.no_slot) next_sibling else parent_index;
    }
}

fn freePartitionSlot(view: layout.View, slot_index: u32) void {
    const slot = &view.partitions[slot_index];
    const generation = slot.generation;
    slot.* = std.mem.zeroes(layout.PartitionSlot);
    slot.generation = generation;
    slot.parent_partition_slot_index = layout.no_slot;
    slot.first_cell_index = layout.no_slot;
    slot.first_child_partition = layout.no_slot;
    slot.previous_sibling_partition = layout.no_slot;
    slot.next_sibling_partition = layout.no_slot;
    slot.next_free = view.header.partition_free_head;
    slot.first_table_ordinal = 0;
    view.header.partition_free_head = slot_index;
    view.header.partition_count -= 1;
}

fn incrementParentRevision(view: layout.View, parent: Parent) void {
    if (parent.isTable()) {
        view.tables[parent.table_slot_index].partition_revision += 1;
    } else {
        view.partitions[parent.partition_slot_index].structure_revision += 1;
    }
}

fn clearActiveAdmission(table: *layout.TableSlot) void {
    table.active_partition_admission_id = 0;
    table.active_partition_admission_hash = 0;
    table.active_partition_operation_id = 0;
    table.active_partition_operation_generation = 0;
}

fn containsVictim(
    victims: []const protocol.PartitionHandle,
    view: layout.View,
    slot_index: u32,
) bool {
    for (victims) |victim| {
        if (victim.partition_slot_index == slot_index and
            victim.partition_slot_generation == view.partitions[slot_index].generation)
        {
            return true;
        }
    }
    return false;
}

fn validateTicketShape(
    view: layout.View,
    ticket: protocol.PartitionAdmissionTicket,
    victims: []const protocol.PartitionHandle,
    expected_kind: protocol.PartitionAdmissionKind,
) !void {
    if (ticket.abi_version != protocol.abi_version or
        ticket.struct_size != @sizeOf(protocol.PartitionAdmissionTicket) or
        ticket.manager_instance_id != view.header.manager_instance_id or
        ticket.admission_id == 0 or ticket.parent_structure_revision == 0 or
        ticket.capacity == 0 or
        ticket.has_parent > 1 or ticket.kind != expected_kind or
        ticket.victim_count != victims.len or
        !allZero(ticket.reserved0[0..]) or ticket.victim_hash != hashVictims(victims))
    {
        return error.InvalidArgument;
    }
    if (ticket.operation_id != view.header.active_operation_id or
        ticket.operation_generation != view.header.active_operation_generation)
    {
        return error.StaleOperation;
    }
    if ((ticket.has_parent == 0) != protocol.isZeroPartitionHandle(ticket.parent)) {
        return error.InvalidArgument;
    }
}

fn hashAdmission(
    ticket: protocol.PartitionAdmissionTicket,
    victims: []const protocol.PartitionHandle,
) u64 {
    var hash: u64 = 14695981039346656037;
    const values = [_]u64{
        ticket.abi_version,
        ticket.struct_size,
        ticket.table.manager_instance_id,
        ticket.table.slot_generation,
        ticket.table.table_id.low,
        ticket.table.table_id.high,
        ticket.table.table_incarnation,
        ticket.table.slot_index,
        ticket.parent.manager_instance_id,
        ticket.parent.table_slot_generation,
        ticket.parent.partition_slot_generation,
        ticket.parent.table_id.low,
        ticket.parent.table_id.high,
        ticket.parent.table_incarnation,
        ticket.parent.table_slot_index,
        ticket.parent.partition_slot_index,
        ticket.manager_instance_id,
        ticket.operation_id,
        ticket.operation_generation,
        ticket.admission_id,
        ticket.parent_structure_revision,
        ticket.victim_hash,
        ticket.local_start,
        ticket.capacity,
        ticket.victim_count,
        ticket.has_parent,
        @intFromEnum(ticket.kind),
    };
    for (values) |value| hash = (hash ^ value) *% 1099511628211;
    for (victims) |victim| {
        const victim_values = [_]u64{
            victim.manager_instance_id,
            victim.table_slot_generation,
            victim.partition_slot_generation,
            victim.table_id.low,
            victim.table_id.high,
            victim.table_incarnation,
            victim.table_slot_index,
            victim.partition_slot_index,
        };
        for (victim_values) |value| hash = (hash ^ value) *% 1099511628211;
    }
    return hash;
}

fn hashVictims(victims: []const protocol.PartitionHandle) u64 {
    var hash: u64 = 14695981039346656037;
    for (victims) |victim| {
        const values = [_]u64{
            victim.manager_instance_id,
            victim.table_slot_generation,
            victim.partition_slot_generation,
            victim.table_id.low,
            victim.table_id.high,
            victim.table_incarnation,
            victim.table_slot_index,
            victim.partition_slot_index,
        };
        for (values) |value| hash = (hash ^ value) *% 1099511628211;
    }
    return hash;
}

fn allZero(values: []const u8) bool {
    for (values) |value| if (value != 0) return false;
    return true;
}
