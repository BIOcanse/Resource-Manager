const std = @import("std");
const protocol = @import("protocol.zig");

pub const no_slot: u32 = std.math.maxInt(u32);
pub const occupied_flag: u32 = 1 << 0;
pub const partition_reserved_flag: u32 = 1 << 1;
pub const ledger_cell_known_flags: u32 = occupied_flag | partition_reserved_flag;
pub const bucket_occupied: u8 = 1;
pub const bucket_tombstone: u8 = 2;

pub const Header = extern struct {
    config: protocol.Config,
    total_size: u64,
    manager_instance_id: u64,
    snapshot_generation: u64,
    activity_epoch: u64,
    next_operation_id: u64,
    next_operation_generation: u64,
    active_operation_id: u64,
    active_operation_generation: u64,
    active_operation_start_snapshot_generation: u64,
    active_operation_settled_execution_count: u32,
    active_operation_confirmed_effect_count: u32,
    active_operation_kind: protocol.OperationKind,
    active_operation_phase: protocol.OperationPhase,
    active_operation_reserved0: [6]u8,
    next_table_generation: u64,
    next_resource_generation: u64,
    next_partition_generation: u64,
    next_capability_generation: u64,
    next_pending_generation: u64,
    next_attempt_id: u64,
    table_count: u32,
    resource_count: u32,
    partition_count: u32,
    ledger_cell_count: u32,
    capability_count: u32,
    pending_count: u32,
    table_free_head: u32,
    resource_free_head: u32,
    partition_free_head: u32,
    capability_free_head: u32,
    pending_free_head: u32,
    uid_tombstone_count: u32,
    reserved0: u32,
    next_partition_admission_id: u64,
    reserved1: u64,
};

pub const TableSlot = extern struct {
    table_id: protocol.Identifier128,
    adapter_key: protocol.Identifier128,
    mode_capability: protocol.CapabilityBinding,
    capacity_capability: protocol.CapabilityBinding,
    table_incarnation: u64,
    topology_generation: u64,
    generation: u64,
    revision: u64,
    partition_revision: u64,
    last_paced_epoch: u64,
    capacity: u32,
    occupied_count: u32,
    direct_occupied_count: u32,
    partition_reservation_start: u32,
    partition_reservation_capacity: u32,
    active_pending_count: u32,
    first_resource_slot: u32,
    first_ledger_cell: u32,
    first_partition_slot: u32,
    next_free: u32,
    flags: u32,
    domain: protocol.DomainKind,
    has_paced_epoch: u8,
    reserved0: [2]u8,
    paced_snapshot_generation: u64,
    active_partition_admission_id: u64,
    active_partition_admission_hash: u64,
    active_partition_operation_id: u64,
    active_partition_operation_generation: u64,
};

pub const ResourceSlot = extern struct {
    resource_uid: protocol.Identifier128,
    size_bytes: u64,
    activity_value: u64,
    last_activity_epoch: u64,
    row_revision: u64,
    activity_revision: u64,
    generation: u64,
    use_generation: u64,
    active_lease_generation: u64,
    pending_generation: u64,
    recovery_cost_coefficient: u32,
    table_slot_index: u32,
    ledger_cell_index: u32,
    pending_slot_index: u32,
    previous_table_resource: u32,
    next_table_resource: u32,
    next_free: u32,
    flags: u32,
    recoverability: protocol.Recoverability,
    access_loss_impact: protocol.AccessLossImpact,
    protected_use: u8,
    reserved0: u8,
    reserved1: u64,
};

pub const LedgerCell = extern struct {
    resource_slot_generation: u64,
    owner_partition_generation: u64,
    resource_slot_index: u32,
    owner_partition_slot_index: u32,
    table_slot_index: u32,
    next_table_cell: u32,
    flags: u32,
    table_local_ordinal: u32,
};

pub const PartitionSlot = extern struct {
    generation: u64,
    structure_revision: u64,
    parent_partition_generation: u64,
    table_slot_index: u32,
    parent_partition_slot_index: u32,
    first_cell_index: u32,
    local_start: u32,
    capacity: u32,
    first_child_partition: u32,
    previous_sibling_partition: u32,
    next_sibling_partition: u32,
    next_free: u32,
    flags: u32,
    first_table_ordinal: u32,
};

pub const CapabilitySlot = extern struct {
    capability_id: u64,
    generation: u64,
    table_slot_index: u32,
    action_code: u32,
    next_free: u32,
    flags: u32,
    expected_effects: u8,
    destructive: u8,
    reserved0: [6]u8,
    reserved1: u64,
};

pub const PendingSlot = extern struct {
    intent: protocol.Intent,
    generation: u64,
    attempt_id: u64,
    next_free: u32,
    flags: u32,
    state: protocol.PendingState,
    reserved0: [7]u8,
};

pub const UidBucket = extern struct {
    hash: u64,
    resource_generation: u64,
    resource_slot_index: u32,
    state: u8,
    reserved0: [3]u8,
};

pub const View = struct {
    header: *Header,
    tables: []TableSlot,
    resources: []ResourceSlot,
    ledger_cells: []LedgerCell,
    partitions: []PartitionSlot,
    capabilities: []CapabilitySlot,
    pending: []PendingSlot,
    uid_buckets: []UidBucket,
};

const Offsets = struct {
    tables: usize,
    resources: usize,
    ledger_cells: usize,
    partitions: usize,
    capabilities: usize,
    pending: usize,
    uid_buckets: usize,
    total: usize,
};

pub fn requiredBytes(config: protocol.Config) !usize {
    if (!protocol.validConfig(config)) return error.InvalidArgument;
    return (try offsets(config)).total;
}

pub fn alignment() usize {
    return @max(
        @alignOf(Header),
        @max(
            @alignOf(TableSlot),
            @max(
                @alignOf(ResourceSlot),
                @max(
                    @alignOf(LedgerCell),
                    @max(
                        @alignOf(PartitionSlot),
                        @max(@alignOf(CapabilitySlot), @max(@alignOf(PendingSlot), @alignOf(UidBucket))),
                    ),
                ),
            ),
        ),
    );
}

pub fn openUnchecked(buffer: []u8, config: protocol.Config) !View {
    const positions = try offsets(config);
    return .{
        .header = @ptrCast(@alignCast(buffer.ptr)),
        .tables = typedSlice(TableSlot, buffer, positions.tables, config.table_capacity),
        .resources = typedSlice(ResourceSlot, buffer, positions.resources, config.resource_capacity),
        .ledger_cells = typedSlice(LedgerCell, buffer, positions.ledger_cells, config.resource_capacity),
        .partitions = typedSlice(PartitionSlot, buffer, positions.partitions, config.partition_capacity),
        .capabilities = typedSlice(CapabilitySlot, buffer, positions.capabilities, config.capability_capacity),
        .pending = typedSlice(PendingSlot, buffer, positions.pending, config.pending_capacity),
        .uid_buckets = typedSlice(UidBucket, buffer, positions.uid_buckets, config.uid_bucket_capacity),
    };
}

fn offsets(config: protocol.Config) !Offsets {
    var cursor = std.mem.alignForward(usize, @sizeOf(Header), @alignOf(TableSlot));
    const tables = cursor;
    cursor = try addRegion(cursor, config.table_capacity, TableSlot);
    cursor = std.mem.alignForward(usize, cursor, @alignOf(ResourceSlot));
    const resources = cursor;
    cursor = try addRegion(cursor, config.resource_capacity, ResourceSlot);
    cursor = std.mem.alignForward(usize, cursor, @alignOf(LedgerCell));
    const ledger_cells = cursor;
    cursor = try addRegion(cursor, config.resource_capacity, LedgerCell);
    cursor = std.mem.alignForward(usize, cursor, @alignOf(PartitionSlot));
    const partitions = cursor;
    cursor = try addRegion(cursor, config.partition_capacity, PartitionSlot);
    cursor = std.mem.alignForward(usize, cursor, @alignOf(CapabilitySlot));
    const capabilities = cursor;
    cursor = try addRegion(cursor, config.capability_capacity, CapabilitySlot);
    cursor = std.mem.alignForward(usize, cursor, @alignOf(PendingSlot));
    const pending = cursor;
    cursor = try addRegion(cursor, config.pending_capacity, PendingSlot);
    cursor = std.mem.alignForward(usize, cursor, @alignOf(UidBucket));
    const uid_buckets = cursor;
    cursor = try addRegion(cursor, config.uid_bucket_capacity, UidBucket);
    return .{
        .tables = tables,
        .resources = resources,
        .ledger_cells = ledger_cells,
        .partitions = partitions,
        .capabilities = capabilities,
        .pending = pending,
        .uid_buckets = uid_buckets,
        .total = cursor,
    };
}

fn addRegion(cursor: usize, count: u32, comptime T: type) !usize {
    const bytes = std.math.mul(usize, count, @sizeOf(T)) catch return error.NumericOverflow;
    return std.math.add(usize, cursor, bytes) catch return error.NumericOverflow;
}

fn typedSlice(comptime T: type, buffer: []u8, offset: usize, count: u32) []T {
    const pointer: [*]T = @ptrCast(@alignCast(buffer.ptr + offset));
    return pointer[0..count];
}
